namespace TradingHub.Indicators.Native.Structure;

/// <summary>Vela simple (segundos Unix de apertura) para los cálculos de estructura.</summary>
public struct Bar
{
    public long T;
    public double O, H, L, C, V;

    public Bar(long t, double o, double h, double l, double c, double v)
    {
        T = t;
        O = o;
        H = h;
        L = l;
        C = c;
        V = v;
    }

    public bool Up => C > O;
    public bool Down => C < O;
}

public sealed class Swing
{
    public int I;
    public int Type; // 1 alto, -1 bajo
    public double Level;
    public string Label = "";
}

public sealed class StructureBreak
{
    public int I;
    public bool IsChoch;
    public int Dir;
    public double Level;
    public int Broken = -1;
}

public sealed class Gap
{
    public int I;
    public int Dir;
    public double Top, Bottom;
    public int Mitigated = -1, Filled = -1;
}

public sealed class OrderBlock
{
    public int I, Dir, Created;
    public double Top, Bottom, Volume, Strength;
    public bool Breaker;
    public int Mitigated = -1, Removed = -1;
}

public sealed class LiquidityPool
{
    public int Type, I, End, Count, Swept;
    public double Level;
}

public sealed class Cisd
{
    public bool Bull;
    public int Start, I, Invalid = -1;
    public double Level;
}

public sealed class Po3Sequence
{
    public int Side, I1, I2, Phase;
    public bool ClosedIn, TwoSided;
    public Bar C1, C2;
    public double Extreme;
    public (double From, double To)? TSpot3, TSpot4;
}

public sealed class Breaker
{
    public int Side, Sweep, I0, I1, Confirm, Invalid = -1;
    public long LevelT;
    public double Level, Top, Bottom;
    public bool Unicorn;
    public Gap? Fvg;
}

/// <summary>
/// Estructura de mercado (Smart Money Concepts), portado de la pestaña «结构» de VPFM (a su vez de smart-money-concepts,
/// MIT): swings, BOS / CHoCH, FVG, order blocks, liquidez (iguales), CISD, PO3, STDV, breakers y agregación a períodos
/// mayores. Funciones puras sobre velas en orden de tiempo.
/// </summary>
public static class Smc
{
    public static List<Swing> Swings(IReadOnlyList<Bar> bars, int len = 10)
    {
        int n = bars.Count;
        var raw = new List<Swing>();
        for (int i = len; i < n - len; i++)
        {
            double h = bars[i].H, l = bars[i].L;
            bool isH = true, isL = true;
            for (int k = i - len; k <= i + len && (isH || isL); k++)
            {
                if (k == i) continue;
                if (bars[k].H > h) isH = false;
                if (bars[k].L < l) isL = false;
            }

            if (isH) raw.Add(new Swing { I = i, Type = 1, Level = h });
            else if (isL) raw.Add(new Swing { I = i, Type = -1, Level = l });
        }

        // de varios seguidos del mismo lado queda el más extremo
        var res = new List<Swing>();
        foreach (var p in raw)
        {
            var top = res.Count > 0 ? res[^1] : null;
            if (top != null && top.Type == p.Type)
            {
                if ((p.Type == 1 && p.Level > top.Level) || (p.Type == -1 && p.Level < top.Level)) res[^1] = p;
            }
            else res.Add(p);
        }

        // etiquetas HH / HL / LH / LL
        double? lastH = null, lastL = null;
        foreach (var p in res)
        {
            if (p.Type == 1)
            {
                p.Label = lastH == null ? "H" : p.Level > lastH ? "HH" : "LH";
                lastH = p.Level;
            }
            else
            {
                p.Label = lastL == null ? "L" : p.Level < lastL ? "LL" : "HL";
                lastL = p.Level;
            }
        }

        return res;
    }

    public static List<StructureBreak> BosChoch(IReadOnlyList<Bar> bars, IReadOnlyList<Swing> sw, bool closeBreak = true)
    {
        var ev = new List<StructureBreak>();
        for (int k = 3; k < sw.Count; k++)
        {
            Swing a = sw[k - 3], b = sw[k - 2], c = sw[k - 1], d = sw[k];
            double l4 = a.Level, l3 = b.Level, l2 = c.Level, l1 = d.Level;
            bool? choch = null;
            int dir = 0;
            if (a.Type == -1 && b.Type == 1 && c.Type == -1 && d.Type == 1)
            {
                if (l4 < l2 && l2 < l3 && l3 < l1) { choch = false; dir = 1; }
                else if (l1 > l3 && l3 > l4 && l4 > l2) { choch = true; dir = 1; }
            }
            else if (a.Type == 1 && b.Type == -1 && c.Type == 1 && d.Type == -1)
            {
                if (l4 > l2 && l2 > l3 && l3 > l1) { choch = false; dir = -1; }
                else if (l1 < l3 && l3 < l4 && l4 < l2) { choch = true; dir = -1; }
            }

            if (choch != null) ev.Add(new StructureBreak { I = b.I, IsChoch = choch.Value, Dir = dir, Level = l3 });
        }

        foreach (var e in ev)
            for (int j = e.I + 2; j < bars.Count; j++)
            {
                double v = e.Dir == 1 ? (closeBreak ? bars[j].C : bars[j].H) : (closeBreak ? bars[j].C : bars[j].L);
                if ((e.Dir == 1 && v > e.Level) || (e.Dir == -1 && v < e.Level))
                {
                    e.Broken = j;
                    break;
                }
            }

        var keep = ev.Where(e => e.Broken >= 0).ToList();
        return keep.Where(e => !keep.Any(o => o != e && e.I < o.I && e.Broken >= o.Broken)).ToList();
    }

    public static List<Gap> Fvg(IReadOnlyList<Bar> bars, double minSize = 0)
    {
        var res = new List<Gap>();
        for (int i = 1; i < bars.Count - 1; i++)
        {
            Bar p = bars[i - 1], c = bars[i], nx = bars[i + 1];
            int dir = 0;
            double top = 0, bottom = 0;
            if (p.H < nx.L && c.C > c.O) { dir = 1; top = nx.L; bottom = p.H; }
            else if (p.L > nx.H && c.C < c.O) { dir = -1; top = p.L; bottom = nx.H; }
            if (dir == 0 || top - bottom < minSize) continue;
            var g = new Gap { I = i, Dir = dir, Top = top, Bottom = bottom };
            for (int j = i + 2; j < bars.Count; j++)
            {
                if (g.Mitigated < 0 && (dir == 1 ? bars[j].L <= top : bars[j].H >= bottom)) g.Mitigated = j;
                if (dir == 1 ? bars[j].L <= bottom : bars[j].H >= top)
                {
                    g.Filled = j;
                    break;
                }
            }

            res.Add(g);
        }

        return res;
    }

    public static List<OrderBlock> OrderBlocks(IReadOnlyList<Bar> bars, IReadOnlyList<Swing> sw, bool closeMitigation = false)
    {
        int n = bars.Count;
        var highs = sw.Where(s => s.Type == 1).Select(s => s.I).ToArray();
        var lows = sw.Where(s => s.Type == -1).Select(s => s.I).ToArray();
        static int LastBefore(int[] arr, int i)
        {
            int lo = 0, hi = arr.Length - 1, r = -1;
            while (lo <= hi)
            {
                int m = (lo + hi) >> 1;
                if (arr[m] < i) { r = arr[m]; lo = m + 1; }
                else hi = m - 1;
            }

            return r;
        }

        var blocks = new List<OrderBlock>();
        foreach (int dir in new[] { 1, -1 })
        {
            var crossed = new HashSet<int>();
            var active = new List<OrderBlock>();
            for (int i = 0; i < n; i++)
            {
                var b = bars[i];
                foreach (var ob in active.ToArray())
                {
                    if (ob.Breaker)
                    {
                        if (dir == 1 ? b.H > ob.Top : b.L < ob.Bottom)
                        {
                            ob.Removed = i;
                            active.Remove(ob);
                        }
                    }
                    else if (dir == 1 ? (closeMitigation ? Math.Min(b.O, b.C) : b.L) < ob.Bottom : (closeMitigation ? Math.Max(b.O, b.C) : b.H) > ob.Top)
                    {
                        ob.Breaker = true;
                        ob.Mitigated = dir == 1 ? i - 1 : i;
                    }
                }

                int piv = LastBefore(dir == 1 ? highs : lows, i);
                if (piv < 0 || crossed.Contains(piv)) continue;
                bool brk = dir == 1 ? b.C > bars[piv].H : b.C < bars[piv].L;
                if (!brk) continue;
                crossed.Add(piv);
                int idx = i - 1;
                if (i - piv > 1)
                {
                    int best = piv + 1;
                    for (int k = piv + 1; k < i; k++)
                        if (dir == 1 ? bars[k].L <= bars[best].L : bars[k].H >= bars[best].H) best = k;
                    idx = best;
                }

                if (idx < 0) continue;
                double v0 = bars[i].V, v1 = i >= 1 ? bars[i - 1].V : 0, v2 = i >= 2 ? bars[i - 2].V : 0;
                double hiV = dir == 1 ? v0 + v1 : v2, loV = dir == 1 ? v2 : v0 + v1;
                var block = new OrderBlock
                {
                    I = idx, Dir = dir, Top = bars[idx].H, Bottom = bars[idx].L, Created = i, Volume = v0 + v1 + v2,
                    Strength = Math.Max(hiV, loV) > 0 ? Math.Min(hiV, loV) / Math.Max(hiV, loV) : 1,
                };
                blocks.Add(block);
                active.Add(block);
            }
        }

        return blocks.Where(b => b.Removed < 0).OrderBy(b => b.I).ToList();
    }

    public static List<LiquidityPool> Liquidity(IReadOnlyList<Bar> bars, IReadOnlyList<Swing> sw, double rangePct = 0.01)
    {
        var res = new List<LiquidityPool>();
        if (bars.Count == 0) return res;
        double hi = double.NegativeInfinity, lo = double.PositiveInfinity;
        foreach (var b in bars)
        {
            if (b.H > hi) hi = b.H;
            if (b.L < lo) lo = b.L;
        }

        double pip = (hi - lo) * rangePct;
        foreach (int type in new[] { 1, -1 })
        {
            var pts = sw.Where(s => s.Type == type).ToList();
            var used = new bool[pts.Count];
            for (int a = 0; a < pts.Count; a++)
            {
                if (used[a]) continue;
                var p = pts[a];
                double rLo = p.Level - pip, rHi = p.Level + pip;
                int swept = -1;
                for (int j = p.I + 1; j < bars.Count; j++)
                    if (type == 1 ? bars[j].H >= rHi : bars[j].L <= rLo)
                    {
                        swept = j;
                        break;
                    }

                var group = new List<Swing> { p };
                for (int b = a + 1; b < pts.Count; b++)
                {
                    var q = pts[b];
                    if (swept >= 0 && q.I >= swept) break;
                    if (!used[b] && q.Level >= rLo && q.Level <= rHi)
                    {
                        group.Add(q);
                        used[b] = true;
                    }
                }

                if (group.Count > 1)
                    res.Add(new LiquidityPool { Type = type, I = p.I, End = group[^1].I, Level = group.Average(x => x.Level), Count = group.Count, Swept = swept });
            }
        }

        return res.OrderBy(x => x.I).ToList();
    }

    public static List<Cisd> Cisds(IReadOnlyList<Bar> bars, int minRun = 2, bool sensitive = false, int maxAge = 60)
    {
        var res = new List<Cisd>();
        (double First, double Last, int Start, int Len)? bear = null, bull = null;
        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            bool red = b.C < b.O, green = b.C > b.O;
            if (red)
            {
                if (bear != null && i > 0 && bars[i - 1].C < bars[i - 1].O) bear = (bear.Value.First, b.O, bear.Value.Start, bear.Value.Len + 1);
                else bear = (b.O, b.O, i, 1);
            }

            if (green)
            {
                if (bull != null && i > 0 && bars[i - 1].C > bars[i - 1].O) bull = (bull.Value.First, b.O, bull.Value.Start, bull.Value.Len + 1);
                else bull = (b.O, b.O, i, 1);
            }

            if (green && bear != null && bear.Value.Len >= minRun && i - bear.Value.Start <= maxAge)
            {
                double lvl = sensitive ? bear.Value.Last : bear.Value.First;
                if (b.C > lvl)
                {
                    res.Add(new Cisd { Bull = true, Start = bear.Value.Start, I = i, Level = lvl });
                    bear = null;
                }
            }

            if (red && bull != null && bull.Value.Len >= minRun && i - bull.Value.Start <= maxAge)
            {
                double lvl = sensitive ? bull.Value.Last : bull.Value.First;
                if (b.C < lvl)
                {
                    res.Add(new Cisd { Bull = false, Start = bull.Value.Start, I = i, Level = lvl });
                    bull = null;
                }
            }
        }

        foreach (var c in res)
            for (int j = c.I + 1; j < bars.Count; j++)
                if (c.Bull ? bars[j].C < c.Level : bars[j].C > c.Level)
                {
                    c.Invalid = j;
                    break;
                }

        return res;
    }

    /// <summary>Secuencias PO3 vivas sobre velas de un período mayor (la más nueva primero).</summary>
    public static List<Po3Sequence> Po3(IReadOnlyList<Bar> htf)
    {
        int n = htf.Count;
        var res = new List<Po3Sequence>();
        foreach (int side in new[] { 1, -1 })
            for (int back = 0; back <= 2; back++)
            {
                int i2 = n - 1 - back;
                if (i2 < 1) break;
                Bar c1 = htf[i2 - 1], c2 = htf[i2];
                bool live = i2 == n - 1;
                bool openInside = c2.O <= c1.H && c2.O >= c1.L;
                bool hiSw = c2.H > c1.H, loSw = c2.L < c1.L;
                bool closedIn = c2.C < c1.H && c2.C > c1.L;
                bool swept = side == -1 ? hiSw : loSw;
                if (!(openInside && swept && (live || closedIn))) continue;
                bool alive = true;
                for (int j = i2 + 1; j < n; j++)
                {
                    if (side == -1 && htf[j].H > c2.H) alive = false;
                    if (side == 1 && htf[j].L < c2.L) alive = false;
                }

                if (!alive) continue;
                Bar? c3 = i2 + 1 < n ? htf[i2 + 1] : null;
                (double, double)? Zone(Bar c, double pct) => (c.C, c.C - side * (c.H - c.L) * pct);
                res.Add(new Po3Sequence
                {
                    Side = side, I1 = i2 - 1, I2 = i2, Phase = back + 2, ClosedIn = closedIn, TwoSided = side == -1 ? loSw : hiSw,
                    C1 = c1, C2 = c2, Extreme = side == -1 ? c2.H : c2.L,
                    TSpot3 = closedIn ? Zone(c2, 0.5) : null,
                    TSpot4 = c3 != null && back >= 2 ? Zone(c3.Value, 0.25) : null,
                });
                break;
            }

        return res.OrderByDescending(s => s.I2).ToList();
    }

    public static List<(double D, double Price)> Stdv(double cisdLevel, double c2Extreme) =>
        new[] { 2.0, 2.5, 4.0, 4.5 }.Select(d => (d, cisdLevel + d * (cisdLevel - c2Extreme))).ToList();

    public static double Atr(IReadOnlyList<Bar> bars, int len = 14)
    {
        double s = 0;
        int k = 0;
        for (int i = Math.Max(1, bars.Count - len * 4); i < bars.Count; i++)
        {
            Bar b = bars[i], p = bars[i - 1];
            s += Math.Max(b.H - b.L, Math.Max(Math.Abs(b.H - p.C), Math.Abs(b.L - p.C)));
            k++;
        }

        return k > 0 ? s / k : 0;
    }

    /// <summary>Breakers / unicornios (Unicorn Model): barrido de un alto / bajo de período mayor, racha contraria, confirmación por cierre.</summary>
    public static List<Breaker> Breakers(IReadOnlyList<Bar> bars, IReadOnlyList<Bar> htf, long htfSec, IReadOnlyList<Gap> fvgs, int minRun = 2, int window = 24)
    {
        int n = bars.Count;
        var res = new List<Breaker>();
        if (n < 10 || htf.Count < 2) return res;
        int IdxAfter(long t)
        {
            int lo = 0, hi = n - 1, r = n;
            while (lo <= hi)
            {
                int m = (lo + hi) >> 1;
                if (bars[m].T >= t) { r = m; hi = m - 1; }
                else lo = m + 1;
            }

            return r;
        }

        int from0 = Math.Max(0, htf.Count - 25);
        for (int ci = from0; ci < htf.Count - 1; ci++)
        {
            var c = htf[ci];
            int from = IdxAfter(c.T + htfSec);
            if (from >= n) continue;
            foreach (int side in new[] { 1, -1 })
            {
                double lvl = side > 0 ? c.L : c.H;
                int j = -1;
                for (int k = from; k < n; k++)
                    if (side > 0 ? bars[k].L < lvl : bars[k].H > lvl)
                    {
                        j = k;
                        break;
                    }

                if (j < 0) continue;
                int e = j;
                for (int k = j; k < Math.Min(n, j + window); k++)
                    if (side > 0 ? bars[k].L < bars[e].L : bars[k].H > bars[e].H) e = k;
                int a = -1, b = -1;
                for (int k = e; k < Math.Min(n, e + window); k++)
                {
                    bool ok = side > 0 ? bars[k].C > bars[k].O : bars[k].C < bars[k].O;
                    if (ok)
                    {
                        if (a < 0) a = k;
                        b = k;
                    }
                    else if (a >= 0)
                    {
                        if (b - a + 1 >= minRun) break;
                        a = -1;
                    }
                }

                if (a < 0 || b - a + 1 < minRun) continue;
                double top = double.NegativeInfinity, bottom = double.PositiveInfinity;
                for (int k = a; k <= b; k++)
                {
                    top = Math.Max(top, bars[k].H);
                    bottom = Math.Min(bottom, bars[k].L);
                }

                int confirm = -1;
                for (int k = b + 1; k < Math.Min(n, b + 1 + window * 2); k++)
                {
                    if (side > 0 ? bars[k].C > top : bars[k].C < bottom)
                    {
                        confirm = k;
                        break;
                    }

                    if (side > 0 ? bars[k].C < bottom : bars[k].C > top) break;
                }

                if (confirm < 0) continue;
                var fvg = fvgs.FirstOrDefault(f => f.Dir == side && f.I >= e && f.I <= confirm && f.Bottom < top && f.Top > bottom);
                int invalid = -1;
                for (int k = confirm + 1; k < n; k++)
                    if (side > 0 ? bars[k].C < bottom : bars[k].C > top)
                    {
                        invalid = k;
                        break;
                    }

                res.Add(new Breaker { Side = side, Level = lvl, LevelT = c.T, Sweep = e, I0 = a, I1 = b, Top = top, Bottom = bottom, Confirm = confirm, Unicorn = fvg != null, Fvg = fvg, Invalid = invalid });
            }
        }

        var seen = new HashSet<string>();
        return res.OrderByDescending(x => x.Confirm).Where(x => seen.Add(x.Side + ":" + x.I0)).Take(6).ToList();
    }

    /// <summary>Velas agregadas a un período mayor (segundos), alineadas a múltiplos del período.</summary>
    public static List<Bar> Aggregate(IReadOnlyList<Bar> bars, long sec)
    {
        var res = new List<Bar>();
        foreach (var b in bars)
        {
            long t = b.T / sec * sec;
            if (res.Count > 0 && res[^1].T == t)
            {
                var last = res[^1];
                last.H = Math.Max(last.H, b.H);
                last.L = Math.Min(last.L, b.L);
                last.C = b.C;
                last.V += b.V;
                res[^1] = last;
            }
            else res.Add(new Bar(t, b.O, b.H, b.L, b.C, b.V));
        }

        return res;
    }

    /// <summary>Bandas de Bollinger sobre cierres (NaN hasta tener «len» valores).</summary>
    public static (double[] Mid, double[] Up, double[] Dn) Bollinger(IReadOnlyList<double> closes, int len = 20, double mult = 2)
    {
        int n = closes.Count;
        var mid = new double[n];
        var up = new double[n];
        var dn = new double[n];
        Array.Fill(mid, double.NaN);
        Array.Fill(up, double.NaN);
        Array.Fill(dn, double.NaN);
        double s = 0, s2 = 0;
        for (int i = 0; i < n; i++)
        {
            s += closes[i];
            s2 += closes[i] * closes[i];
            if (i >= len)
            {
                s -= closes[i - len];
                s2 -= closes[i - len] * closes[i - len];
            }

            if (i >= len - 1)
            {
                double m = s / len, sd = Math.Sqrt(Math.Max(0, s2 / len - m * m));
                mid[i] = m;
                up[i] = m + mult * sd;
                dn[i] = m - mult * sd;
            }
        }

        return (mid, up, dn);
    }

    /// <summary>Período mayor en segundos a partir del largo de la vela del gráfico (como el mapa de VPFM).</summary>
    public static (long First, long Second) HigherTimeframes(TimeSpan bar)
    {
        double m = bar.TotalMinutes;
        const long H = 3600, D = 86400, W = 7 * 86400, Mo = 30 * 86400;
        if (m <= 1) return (15 * 60, H);
        if (m <= 5) return (H, 4 * H);
        if (m <= 15) return (4 * H, D);
        if (m <= 60) return (D, W);
        if (m <= 240) return (W, Mo);
        return (W, Mo);
    }

    /// <summary>Los tres períodos de las Bollinger triples: el del gráfico y dos mayores.</summary>
    public static (long A, long B, long C) BollingerTimeframes(TimeSpan bar)
    {
        double m = bar.TotalMinutes;
        const long H = 3600, D = 86400, W = 7 * 86400, Mo = 30 * 86400;
        if (m <= 5) return (5 * 60, 15 * 60, H);
        if (m <= 15) return (15 * 60, H, 4 * H);
        if (m <= 60) return (H, 4 * H, D);
        if (m <= 240) return (4 * H, D, W);
        return (D, W, Mo);
    }

    public static string TfName(long sec) => sec switch
    {
        < 3600 => sec / 60 + "m",
        < 86400 => sec / 3600 + "h",
        86400 => "1D",
        7 * 86400 => "1W",
        _ => "1M",
    };
}
