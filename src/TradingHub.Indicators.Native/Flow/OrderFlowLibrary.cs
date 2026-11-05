using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Indicators.Native.Structure;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Flow;

// Order flow de la sección 06 (delta, CVD, footprint): dominio del flujo spot / perpetuos, divergencias del CVD, tablero
// de decisión, absorción por vela, delta con bandas de desvío, burbujas de volumen y alertas del footprint. Los valores de
// las velas cerradas no cambian cuando llegan velas nuevas.

/// <summary>Quién manda en el flujo: el CVD de spot contra el de perpetuos, cada cambio en desvíos (z) solo con lo anterior.</summary>
[DisplayName("CVD: dominio del flujo (spot contra perpetuos)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("El delta de cada vela de los mercados spot y de los perpetuos (del feed agregado), cada uno en desvíos (z) contra su propia historia hasta esa vela: una barra con la suma pintada según quién lidera, o las dos barras. Para ver si el movimiento lo empuja el spot o los derivados.")]
public class CvdFlowDominance : Indicator
{
    public enum DomUnit
    {
        [Display(Name = "Monedas")] Coins,
        [Display(Name = "Dólares")] Usd,
    }

    private readonly ValueDataSeries _sum = new("DOM", "Spot + perpetuos (z)") { VisualType = VisualMode.Histogram, ShowZeroValue = true };
    private readonly ValueDataSeries _spot = new("SPOT", "Spot (z)") { VisualType = VisualMode.Hide, ShowZeroValue = true, Color = Color.FromArgb(200, 255, 167, 38) };
    private readonly ValueDataSeries _perp = new("PERP", "Perpetuos (z)") { VisualType = VisualMode.Hide, ShowZeroValue = true, Color = Color.FromArgb(200, 41, 182, 246) };
    private readonly Dictionary<long, (int Trades, double Spot, double Perp)> _cache = new();
    private double _n, _mS, _m2S, _mP, _m2P;
    private int _done = -1;
    private DomUnit _unit = DomUnit.Usd;
    private bool _breakdown;

    public CvdFlowDominance() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _sum;
        DataSeries.Add(_spot);
        DataSeries.Add(_perp);
        LineSeries.Add(new LineSeries("Zero", "Cero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Dash });
    }

    [Display(Name = "Unidad", GroupName = "Cálculo", Order = 10)] public DomUnit Unit { get => _unit; set { _unit = value; RecalculateValues(); } }

    [Display(Name = "Ver las dos barras (spot y perpetuos)", GroupName = "Dibujo", Order = 20)]
    public bool Breakdown
    {
        get => _breakdown;
        set
        {
            _breakdown = value;
            _sum.VisualType = value ? VisualMode.Hide : VisualMode.Histogram;
            _spot.VisualType = _perp.VisualType = value ? VisualMode.Histogram : VisualMode.Hide;
        }
    }

    [Display(Name = "Lidera el spot", GroupName = "Colores", Order = 30)] public Color SpotColor { get; set; } = Color.FromArgb(220, 255, 167, 38);
    [Display(Name = "Lideran los perpetuos", GroupName = "Colores", Order = 31)] public Color PerpColor { get; set; } = Color.FromArgb(220, 41, 182, 246);

    protected override void OnRecalculate()
    {
        _n = _mS = _m2S = _mP = _m2P = 0;
        _done = -1;
    }

    private (double Spot, double Perp) Deltas(int bar)
    {
        var c = GetCandle(bar);
        long key = c.Time.Ticks;
        int trades = (int)c.Ticks;
        if (bar < CurrentBar - 1 && _cache.TryGetValue(key, out var hit) && hit.Trades == trades) return (hit.Spot, hit.Perp);
        var spotFlags = InstrumentInfo?.SourceIsSpot ?? Array.Empty<bool>();
        double s = 0, p = 0;
        bool any = false;
        foreach (var t in GetTrades(bar))
        {
            any = true;
            double v = (double)(t.IsBuy ? t.Volume : -t.Volume) * (_unit == DomUnit.Usd ? (double)t.Price : 1);
            if (t.Source >= 0 && t.Source < spotFlags.Length && spotFlags[t.Source]) s += v;
            else p += v;
        }

        if (!any)
        {
            // sin operaciones por fuente: el delta de la vela va al lado del mercado del gráfico
            double d = (double)c.Delta * (_unit == DomUnit.Usd ? (double)c.Close : 1);
            if ((InstrumentInfo?.Instrument ?? "").EndsWith(".P", StringComparison.OrdinalIgnoreCase) || spotFlags.Length == 0) p = d;
            else s = d;
        }

        if (bar < CurrentBar - 1) _cache[key] = (trades, s, p);
        return (s, p);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var (s, p) = Deltas(bar);
        bool closed = bar < CurrentBar - 1;
        // Welford hacia adelante: cada vela con las estadísticas de las anteriores y la suya
        double n = _n + 1, ms = _mS + (s - _mS) / n, mp = _mP + (p - _mP) / n;
        double m2s = _m2S + (s - _mS) * (s - ms), m2p = _m2P + (p - _mP) * (p - mp);
        if (closed && bar > _done)
        {
            _n = n;
            _mS = ms;
            _mP = mp;
            _m2S = m2s;
            _m2P = m2p;
            _done = bar;
        }

        double sdS = n > 1 ? Math.Sqrt(m2s / (n - 1)) : 0, sdP = n > 1 ? Math.Sqrt(m2p / (n - 1)) : 0;
        double zs = sdS > 0 ? (s - ms) / sdS : 0, zp = sdP > 0 ? (p - mp) / sdP : 0;
        _spot[bar] = (decimal)Math.Clamp(zs, -50, 50);
        _perp[bar] = (decimal)Math.Clamp(zp, -50, 50);
        _sum[bar] = (decimal)Math.Clamp(zs + zp, -100, 100);
        _sum.Colors[bar] = Math.Abs(zs) >= Math.Abs(zp) ? SpotColor : PerpColor;
        _spot.Colors[bar] = SpotColor;
        _perp.Colors[bar] = PerpColor;
    }
}

/// <summary>CVD en un panel con las divergencias contra el precio en los swings confirmados.</summary>
[DisplayName("CVD con divergencias")]
[Category(IndicatorCategories.OrderFlow)]
[Description("El delta acumulado (CVD, en monedas) en su panel. Cuando el precio hace un máximo más alto y el CVD uno más bajo (o un mínimo más bajo con el CVD más alto), une los dos swings del CVD y lo marca como divergencia. Cada swing se confirma unas velas después: no se redibuja.")]
public class CvdDivergence : Indicator
{
    private readonly ValueDataSeries _cvd = new("CVD", "CVD") { Color = Color.FromArgb(255, 230, 230, 235), Width = 2, ShowZeroValue = true };
    private readonly List<(int A, int B, bool Bear)> _divs = new();
    private readonly List<(int Bar, bool High)> _swings = new();
    private int _swing = 8, _kept = 12;
    private bool _reset;
    private int _computed = -1;

    public CvdDivergence() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _cvd;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Fuerza del swing (velas)", GroupName = "Cálculo", Order = 10)] [Range(2, 50)] public int Swing { get => _swing; set { _swing = Math.Clamp(value, 2, 50); RecalculateValues(); } }
    [Display(Name = "Reiniciar cada día de trading", GroupName = "Cálculo", Order = 11)] public bool ResetDaily { get => _reset; set { _reset = value; RecalculateValues(); } }
    [Display(Name = "Divergencias que se dibujan", GroupName = "Dibujo", Order = 20)] [Range(1, 100)] public int LinesKept { get => _kept; set { _kept = Math.Clamp(value, 1, 100); RedrawChart(); } }
    [Display(Name = "Alcista", GroupName = "Colores", Order = 30)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 31)] public Color BearColor { get; set; } = Pal.Bear;

    protected override void OnRecalculate() => _computed = -1;

    protected override void OnCalculate(int bar, decimal value)
    {
        decimal prev = bar > 0 ? _cvd[bar - 1] : 0;
        if (_reset && bar > 0 && IsNewSession(bar)) prev = 0;
        _cvd[bar] = prev + GetCandle(bar).Delta;
        if (bar < CurrentBar - 1 || _computed == CurrentBar) return;
        _computed = CurrentBar;
        _divs.Clear();
        _swings.Clear();
        var bars = new List<Structure.Bar>(CurrentBar);
        for (int i = 0; i < CurrentBar; i++)
        {
            var c = GetCandle(i);
            bars.Add(new Structure.Bar(0, (double)c.Open, (double)c.High, (double)c.Low, (double)c.Close, 0));
        }

        Ta.Pivot? lastHi = null, lastLo = null;
        foreach (var p in Ta.Pivots(bars, _swing, _swing, CurrentBar - 2))
        {
            _swings.Add((p.Bar, p.High));
            if (p.High)
            {
                if (lastHi is { } h && p.Price > h.Price && _cvd[p.Bar] < _cvd[h.Bar]) _divs.Add((h.Bar, p.Bar, true));
                lastHi = p;
            }
            else
            {
                if (lastLo is { } l && p.Price < l.Price && _cvd[p.Bar] > _cvd[l.Bar]) _divs.Add((l.Bar, p.Bar, false));
                lastLo = p;
            }
        }
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        foreach (var (bar, high) in _swings)
        {
            if (bar < first || bar > last) continue;
            int x = ChartInfo.GetXByBar(bar, false), y = box.GetYByValue(_cvd[bar]);
            ctx.FillEllipse(Pal.A(high ? BearColor : BullColor, 160), new Rectangle(x - 3, y - 3, 6, 6));
        }

        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        foreach (var (a, b, bear) in _divs.TakeLast(_kept))
        {
            if (b < first || a > last) continue;
            var c = bear ? BearColor : BullColor;
            int x0 = ChartInfo.GetXByBar(a, false), x1 = ChartInfo.GetXByBar(b, false);
            int y0 = box.GetYByValue(_cvd[a]), y1 = box.GetYByValue(_cvd[b]);
            ctx.DrawLine(new RenderPen(c, 2, System.Drawing.Drawing2D.DashStyle.Dash), x0, y0, x1, y1);
            ctx.DrawString("div", font, c, x1 + 3, bear ? y1 - 14 : y1 + 2);
        }
    }
}

/// <summary>Un tablero que califica el mercado factor por factor y dice para qué lado se inclina.</summary>
[DisplayName("Tablero de decisión")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Califica seis factores (cierre contra la EMA, RSI, volumen contra su media, flujo agresor, cambio del interés abierto y la parte de largos en las liquidaciones) y escribe para qué lado se inclina («compradores adelante, 4 a 1»), con el puntaje neto de las últimas 64 velas. Un factor sin datos (OI y liquidaciones en spot) no cuenta.")]
public class DecisionBoard : SceneIndicator
{
    private readonly DerivFeed _deriv;
    private List<int> _net = new();

    [Display(Name = "EMA (velas)", GroupName = "Cálculo", Order = 10)] [Range(2, 500)] public int EmaLength { get; set; } = 50;
    [Display(Name = "RSI (velas)", GroupName = "Cálculo", Order = 11)] [Range(2, 100)] public int RsiLength { get; set; } = 14;
    [Display(Name = "Ventana (velas)", GroupName = "Cálculo", Order = 12)] [Range(2, 500)] public int Window { get; set; } = 20;
    [Display(Name = "RSI alcista arriba de", GroupName = "Reglas", Order = 20)] [Range(50, 90)] public decimal RsiBull { get; set; } = 55;
    [Display(Name = "RSI bajista abajo de", GroupName = "Reglas", Order = 21)] [Range(10, 50)] public decimal RsiBear { get; set; } = 45;
    [Display(Name = "Pico de volumen (× media)", GroupName = "Reglas", Order = 22)] [Range(1, 10)] public decimal VolumeSpike { get; set; } = 1.2m;
    [Display(Name = "Ventaja del flujo (%)", GroupName = "Reglas", Order = 23)] [Range(0, 50)] public decimal FlowEdge { get; set; } = 5;
    [Display(Name = "Suba mínima del OI (%)", GroupName = "Reglas", Order = 24)] [Range(0, 10)] public decimal OiFlat { get; set; } = 0.05m;
    [Display(Name = "Largos liquidados: mucho desde (%)", GroupName = "Reglas", Order = 25)] [Range(50, 100)] public decimal LiqHeavy { get; set; } = 65;
    [Display(Name = "Margen para inclinarse (factores)", GroupName = "Reglas", Order = 26)] [Range(1, 6)] public int LeanMargin { get; set; } = 2;
    [Display(Name = "Color de la EMA", GroupName = "Colores", Order = 30)] public Color EmaColor { get; set; } = Color.FromArgb(255, 255, 167, 38);

    public DecisionBoard()
    {
        _deriv = new DerivFeed(a => DoActionInGuiThread(a), RecalculateValues, oi: true, funding: false, liquidations: true);
    }

    protected override void OnDispose()
    {
        base.OnDispose();
        _deriv.Dispose();
    }

    private static double[] Rsi(IReadOnlyList<double> c, int len)
    {
        var res = new double[c.Count];
        double up = 0, dn = 0;
        for (int i = 1; i < c.Count; i++)
        {
            double d = c[i] - c[i - 1];
            double u = Math.Max(d, 0), w = Math.Max(-d, 0);
            if (i <= len)
            {
                up += u / len;
                dn += w / len;
            }
            else
            {
                up = (up * (len - 1) + u) / len;
                dn = (dn * (len - 1) + w) / len;
            }

            res[i] = i < len ? 50 : dn == 0 ? 100 : 100 - 100 / (1 + up / dn);
        }

        return res;
    }

    protected override void Build(Scene s)
    {
        int n = Bars.Count;
        if (n < Math.Max(EmaLength, Window) + 2) return;
        if (CurrentBar > 0) _deriv.Ensure(InstrumentInfo?.Instrument ?? "", GetCandle(Offset).Time, BarLength);
        var closes = Bars.Select(b => b.C).ToList();
        var ema = Ta.Ema(closes, EmaLength);
        var rsi = Rsi(closes, RsiLength);
        var delta = new double[n];
        for (int i = 0; i < n; i++) delta[i] = (double)GetCandle(i + Offset).Delta;

        // el puntaje de una vela: +1 por factor alcista, −1 por bajista; null = sin datos
        (int Bull, int Bear, string[] Read, int[] Sign) Score(int i)
        {
            var read = new string[6];
            var sign = new int[6];
            int from = Math.Max(0, i - Window + 1);
            sign[0] = Bars[i].C > ema[i] ? 1 : Bars[i].C < ema[i] ? -1 : 0;
            read[0] = $"{(Bars[i].C - ema[i]) / ema[i] * 100:+0.00;-0.00} %";
            sign[1] = rsi[i] > (double)RsiBull ? 1 : rsi[i] < (double)RsiBear ? -1 : 0;
            read[1] = rsi[i].ToString("0");
            double avg = 0;
            for (int k = from; k < i; k++) avg += Bars[k].V;
            avg /= Math.Max(1, i - from);
            double vr = avg > 0 ? Bars[i].V / avg : 0;
            sign[2] = vr >= (double)VolumeSpike ? (Bars[i].C >= Bars[i].O ? 1 : -1) : 0;
            read[2] = $"{vr:0.0}×";
            double dsum = 0, vsum = 0;
            for (int k = from; k <= i; k++)
            {
                dsum += delta[k];
                vsum += Bars[k].V;
            }

            double flow = vsum > 0 ? dsum / vsum * 100 : 0;
            sign[3] = flow > (double)FlowEdge ? 1 : flow < -(double)FlowEdge ? -1 : 0;
            read[3] = $"{flow:+0;-0} %";
            var t0 = TimeOf(from);
            var t1 = TimeOf(i) + (BarLength > TimeSpan.Zero ? BarLength : TimeSpan.Zero);
            if (_deriv.OiAt(t0) is { } oa && _deriv.OiAt(t1) is { } ob && oa > 0)
            {
                double ch = (ob - oa) / oa * 100;
                sign[4] = ch > (double)OiFlat ? (Bars[i].C > Bars[from].C ? 1 : -1) : 0;
                read[4] = $"{ch:+0.00;-0.00} %";
            }
            else read[4] = "—";

            var (ll, ls) = _deriv.Liquidations(t0, t1);
            if (ll + ls > 0)
            {
                double longShare = ll / (ll + ls) * 100;
                sign[5] = longShare > (double)LiqHeavy ? -1 : longShare < 100 - (double)LiqHeavy ? 1 : 0;
                read[5] = $"{longShare:0} % largos";
            }
            else read[5] = "—";

            return (sign.Count(x => x > 0), sign.Count(x => x < 0), read, sign);
        }

        _net = new List<int>();
        for (int i = Math.Max(0, n - 64); i < n; i++)
        {
            var sc = Score(i);
            _net.Add(sc.Bull - sc.Bear);
        }

        s.Paths.Add(new Scene.PathShape(Enumerable.Range(Math.Max(0, n - 800), Math.Min(800, n)).Select(i => (i, ema[i])).ToList(), EmaColor, 2));
        var (bull, bear, readings, signs) = Score(n - 1);
        string head = bull > bear ? $"Compradores adelante, {bull} a {bear}" : bear > bull ? $"Vendedores adelante, {bear} a {bull}" : $"Señales divididas, {bull} a {bear}";
        string lean = bull - bear >= LeanMargin ? "inclina a comprar" : bear - bull >= LeanMargin ? "inclina a vender" : "sin inclinación";
        s.DashTitle = head;
        s.Row("Puntaje neto", $"{bull - bear:+0;-0;0} · {lean}", bull > bear ? Pal.Bull : bear > bull ? Pal.Bear : default);
        string[] names = { "Cierre contra la EMA", "RSI", "Volumen contra su media", "Flujo agresor", "Cambio del interés abierto", "Liquidaciones" };
        for (int k = 0; k < 6; k++) s.Row(names[k], readings[k], signs[k] > 0 ? Pal.Bull : signs[k] < 0 ? Pal.Bear : Pal.Muted);
    }

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (_net.Count < 2 || DashRect.IsEmpty || !ShowDashboard) return;
        var d = DashRect;
        int w = d.Width, h = 34, x0 = d.Left, y0 = d.Bottom + 4;
        if (y0 + h > region.Bottom) return;
        ctx.FillRectangle(Color.FromArgb(215, 18, 20, 26), new Rectangle(x0, y0, w, h), 5);
        int mid = y0 + h / 2;
        ctx.DrawLine(new RenderPen(Color.FromArgb(70, 140, 140, 150), 1), x0 + 4, mid, x0 + w - 4, mid);
        var pts = _net.Select((v, k) => new Point(x0 + 4 + (w - 8) * k / Math.Max(1, _net.Count - 1), mid - v * (h / 2 - 3) / 6)).ToArray();
        ctx.DrawLines(new RenderPen(_net[^1] >= 0 ? Pal.Bull : Pal.Bear, 2), pts);
    }
}

/// <summary>Velas donde mucho volumen de un lado no movió el precio (absorción por vela).</summary>
[DisplayName("Absorción por vela (delta grande, poco rango)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Velas cuyo delta (compras − ventas) se dispara sobre su media mientras el rango queda adentro del ATR: alguien absorbió esa agresión. Encuadra la vela y la marca: arriba si absorbieron compras, abajo si absorbieron ventas.")]
public class BarAbsorption : SceneIndicator
{
    [Display(Name = "Pico de delta (× media)", GroupName = "Cálculo", Order = 10)] [Range(1, 20)] public decimal DeltaSpike { get; set; } = 2;
    [Display(Name = "Ventana del delta (velas)", GroupName = "Cálculo", Order = 11)] [Range(5, 500)] public int DeltaWindow { get; set; } = 48;
    [Display(Name = "Rango máximo (ATR)", GroupName = "Cálculo", Order = 12)] [Range(0.1, 5)] public decimal MaxRange { get; set; } = 0.8m;
    [Display(Name = "ATR (velas)", GroupName = "Cálculo", Order = 13)] [Range(2, 200)] public int AtrLength { get; set; } = 14;
    [Display(Name = "Absorbieron compras", GroupName = "Colores", Order = 20)] public Color BuyAbsorbed { get; set; } = Pal.Bear;
    [Display(Name = "Absorbieron ventas", GroupName = "Colores", Order = 21)] public Color SellAbsorbed { get; set; } = Pal.Bull;

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < DeltaWindow + 2) return;
        var atr = Ta.Atr(Bars, AtrLength);
        var d = new double[Bars.Count];
        for (int i = 0; i < Bars.Count; i++) d[i] = (double)GetCandle(i + Offset).Delta;
        double sum = 0;
        int buys = 0, sells = 0;
        for (int i = 0; i <= last; i++)
        {
            if (i >= DeltaWindow)
            {
                double mean = sum / DeltaWindow;
                var b = Bars[i];
                if (mean > 0 && Math.Abs(d[i]) >= mean * (double)DeltaSpike && b.H - b.L < atr[i] * (double)MaxRange)
                {
                    bool buyAbs = d[i] > 0;
                    var c = buyAbs ? BuyAbsorbed : SellAbsorbed;
                    s.Box(i, i, b.H, b.L, Pal.A(c, 40), c);
                    s.Mark(i, buyAbs ? b.H : b.L, MarkKind.Circle, c, null, buyAbs);
                    if (buyAbs) buys++;
                    else sells++;
                }

                sum -= Math.Abs(d[i - DeltaWindow]);
            }

            sum += Math.Abs(d[i]);
        }

        s.DashTitle = "Absorción por vela";
        s.Row("Compras absorbidas", buys.ToString(), BuyAbsorbed);
        s.Row("Ventas absorbidas", sells.ToString(), SellAbsorbed);
    }
}

/// <summary>Delta por vela (o móvil) con bandas de desvío estándar.</summary>
[DisplayName("Delta de volumen pro (bandas de desvío)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("El delta de cada vela (o la suma móvil de N velas), en monedas o dólares, con bandas de ±1, ±2 y ±3 desvíos estándar de las últimas velas. Color fijo o según cuántos desvíos se aleja.")]
public class VolumeDeltaPro : Indicator
{
    public enum DeltaMode
    {
        [Display(Name = "Delta por vela")] PerBar,
        [Display(Name = "Delta móvil")] Rolling,
    }

    public enum Coloring
    {
        [Display(Name = "Dinámico (por desvíos)")] Dynamic,
        [Display(Name = "Fijo")] Fixed,
    }

    private readonly ValueDataSeries _delta = new("VD", "Delta") { VisualType = VisualMode.Histogram, ShowZeroValue = true };
    private readonly ValueDataSeries[] _bands = Enumerable.Range(0, 6).Select(k => new ValueDataSeries("B" + k, (k < 3 ? "+" : "−") + (k % 3 + 1) + "σ") { ShowZeroValue = false, Width = 1 }).ToArray();
    private readonly List<double> _raw = new();
    private DeltaMode _mode = DeltaMode.PerBar;
    private int _rolling = 50, _smooth, _lookback = 50;
    private bool _usd, _fill = true, _std = true;

    public VolumeDeltaPro() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _delta;
        foreach (var b in _bands) DataSeries.Add(b);
        LineSeries.Add(new LineSeries("Zero", "Cero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Solid });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Historical);
    }

    [Display(Name = "Modo", GroupName = "Cálculo", Order = 10)] public DeltaMode Mode { get => _mode; set { _mode = value; RecalculateValues(); } }
    [Display(Name = "Velas del delta móvil", GroupName = "Cálculo", Order = 11)] [Range(2, 1000)] public int RollingLength { get => _rolling; set { _rolling = value; RecalculateValues(); } }
    [Display(Name = "En dólares", GroupName = "Cálculo", Order = 12)] public bool Usd { get => _usd; set { _usd = value; RecalculateValues(); } }
    [Display(Name = "Suavizar (velas; 0 = no)", GroupName = "Cálculo", Order = 13)] [Range(0, 100)] public int SmoothLength { get => _smooth; set { _smooth = value; RecalculateValues(); } }
    [Display(Name = "Velas para los desvíos", GroupName = "Cálculo", Order = 14)] [Range(5, 1000)] public int VolLookback { get => _lookback; set { _lookback = value; RecalculateValues(); } }
    [Display(Name = "Color", GroupName = "Dibujo", Order = 20)] public Coloring ColorMode { get; set; } = Coloring.Fixed;
    [Display(Name = "Bandas de desvío", GroupName = "Dibujo", Order = 21)] public bool ShowStdDev { get => _std; set { _std = value; foreach (var b in _bands) b.VisualType = value && !_fill ? VisualMode.Line : VisualMode.Hide; } }
    [Display(Name = "Bandas rellenas", GroupName = "Dibujo", Order = 22)] public bool FillBands { get => _fill; set { _fill = value; ShowStdDev = _std; } }
    [Display(Name = "Positivo", GroupName = "Colores", Order = 30)] public Color PositiveColor { get; set; } = Color.FromArgb(255, 100, 255, 218);
    [Display(Name = "Negativo", GroupName = "Colores", Order = 31)] public Color NegativeColor { get; set; } = Color.FromArgb(255, 244, 67, 54);

    protected override void OnRecalculate()
    {
        _raw.Clear();
        ShowStdDev = _std;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        double d = (double)c.Delta * (_usd ? (double)c.Close : 1);
        while (_raw.Count <= bar) _raw.Add(0);
        _raw[bar] = d;
        double v = d;
        if (_mode == DeltaMode.Rolling)
        {
            v = 0;
            for (int k = Math.Max(0, bar - _rolling + 1); k <= bar; k++) v += _raw[k];
        }

        if (_smooth > 1)
        {
            double sum = (double)v;
            int cnt = 1;
            for (int k = bar - 1; k >= Math.Max(0, bar - _smooth + 1); k--)
            {
                sum += (double)_delta[k];
                cnt++;
            }

            v = sum / cnt;
        }

        _delta[bar] = (decimal)v;
        // desvío poblacional de las últimas N
        int from = Math.Max(0, bar - _lookback + 1);
        double m = 0;
        int nn = 0;
        for (int k = from; k <= bar; k++, nn++) m += (double)_delta[k];
        m /= Math.Max(1, nn);
        double var = 0;
        for (int k = from; k <= bar; k++) var += ((double)_delta[k] - m) * ((double)_delta[k] - m);
        double sd = nn >= 2 ? Math.Sqrt(var / nn) : 0;
        for (int k = 0; k < 3; k++)
        {
            _bands[k][bar] = sd > 0 ? (decimal)(sd * (k + 1)) : 0;
            _bands[k + 3][bar] = sd > 0 ? (decimal)(-sd * (k + 1)) : 0;
            _bands[k].Color = Pal.A(PositiveColor, 120 - k * 30);
            _bands[k + 3].Color = Pal.A(NegativeColor, 120 - k * 30);
        }

        double z = sd > 0 ? Math.Abs(v) / sd : 0;
        var basec = v >= 0 ? PositiveColor : NegativeColor;
        _delta.Colors[bar] = ColorMode == Coloring.Fixed ? basec : Pal.A(basec, (int)(70 + 185 * Math.Clamp(z / 3, 0, 1)));
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (!_std || !_fill || ChartInfo == null || Container is not { } box) return;
        int first = Math.Max(1, FirstVisibleBarNumber), last = Math.Min(LastVisibleBarNumber, CurrentBar - 1);
        for (int k = 0; k < 3; k++)
        {
            foreach (int sign in new[] { 1, -1 })
            {
                var top = new List<Point>();
                var bot = new List<Point>();
                for (int i = first; i <= last; i++)
                {
                    int x = ChartInfo.GetXByBar(i, false);
                    decimal outer = sign > 0 ? _bands[k][i] : _bands[k + 3][i];
                    decimal inner = k == 0 ? 0 : sign > 0 ? _bands[k - 1][i] : _bands[k + 2][i];
                    top.Add(new Point(x, box.GetYByValue(outer)));
                    bot.Add(new Point(x, box.GetYByValue(inner)));
                }

                if (top.Count < 2) continue;
                bot.Reverse();
                var c = sign > 0 ? PositiveColor : NegativeColor;
                ctx.FillPolygon(Pal.A(c, 22 - k * 5), top.Concat(bot).ToArray());
            }
        }
    }
}

/// <summary>Burbujas en las velas de volumen alto, en los precios donde se concentró (en dólares).</summary>
[DisplayName("Burbujas de volumen")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Marca las velas de volumen alto (en desvíos sobre su media, tres niveles) y, adentro de cada una, los precios donde más se operó: una píldora o un círculo con el volumen en dólares, del color del lado que dominó. Con una tabla de cuántas velas de cada nivel hubo.")]
public class VolumeBubbles : SceneIndicator
{
    public enum BubbleStyle
    {
        [Display(Name = "Píldora")] Pill,
        [Display(Name = "Círculo")] Circle,
    }

    [Display(Name = "Velas de la media", GroupName = "Cálculo", Order = 10)] [Range(5, 1000)] public int Lookback { get; set; } = 50;
    [Display(Name = "Nivel 1 desde (desvíos)", GroupName = "Cálculo", Order = 11)] [Range(0.5, 10)] public decimal Z1 { get; set; } = 1.5m;
    [Display(Name = "Nivel 2 desde (desvíos)", GroupName = "Cálculo", Order = 12)] [Range(0.5, 10)] public decimal Z2 { get; set; } = 2.5m;
    [Display(Name = "Nivel 3 desde (desvíos)", GroupName = "Cálculo", Order = 13)] [Range(0.5, 20)] public decimal Z3 { get; set; } = 3.5m;
    [Display(Name = "Precio: parte mínima del POC", GroupName = "Cálculo", Order = 14)] [Range(0.05, 1)] public decimal BucketShare { get; set; } = 0.35m;
    [Display(Name = "Precio: mínimo en dólares", GroupName = "Cálculo", Order = 15)] [Range(0, 1e10)] public decimal MinBucketUsd { get; set; } = 50_000;
    [Display(Name = "Burbujas por vela", GroupName = "Cálculo", Order = 16)] [Range(1, 20)] public int MaxPills { get; set; } = 3;
    [Display(Name = "Estilo", GroupName = "Dibujo", Order = 20)] public BubbleStyle Style { get; set; } = BubbleStyle.Circle;
    [Display(Name = "Compras", GroupName = "Colores", Order = 30)] public Color BuyColor { get; set; } = Pal.Bull;
    [Display(Name = "Ventas", GroupName = "Colores", Order = 31)] public Color SellColor { get; set; } = Pal.Bear;

    protected override void Build(Scene s)
    {
        int n = Bars.Count;
        if (n < Lookback + 2) return;
        var vols = Bars.Select(b => b.V).ToList();
        var sma = Ta.Sma(vols, Lookback);
        var sd = Ta.StdDev(vols, Lookback);
        int[] tiers = new int[3];
        for (int i = Lookback; i < n; i++)
        {
            double z = sd[i] > 0 ? (vols[i] - sma[i]) / sd[i] : 0;
            int tier = z >= (double)Z3 ? 3 : z >= (double)Z2 ? 2 : z >= (double)Z1 ? 1 : 0;
            if (tier == 0 || i > LastClosed) continue;
            tiers[tier - 1]++;
            double mult = tier switch { 3 => 1.6, 2 => 1.3, _ => 1.0 };
            var c = GetCandle(i + Offset);
            var levels = c.GetAllPriceLevels().ToList();
            if (levels.Count == 0)
            {
                // sin footprint: una burbuja en el cierre con el volumen de la vela
                bool up = c.Delta >= 0;
                Draw(i, (double)c.Close, (double)(c.Volume * c.Close), 1, up, mult);
                continue;
            }

            decimal poc = levels.Max(l => l.Volume);
            foreach (var lv in levels.Where(l => poc > 0 && l.Volume / poc >= BucketShare && l.Volume * l.Price >= MinBucketUsd).OrderByDescending(l => l.Volume).Take(MaxPills))
                Draw(i, (double)lv.Price, (double)(lv.Volume * lv.Price), (double)(lv.Volume / poc), lv.Ask >= lv.Bid, mult);
        }

        void Draw(int i, double price, double usd, double share, bool buy, double mult)
        {
            var col = buy ? BuyColor : SellColor;
            string text = "$" + VpUltimate.Vp.Short((decimal)usd);
            if (Style == BubbleStyle.Pill) s.Tags.Add(new Scene.TagShape(i, price, text, Pal.A(col, 200), Color.White));
            else s.Marks.Add(new Scene.MarkShape(i, price, MarkKind.Circle, Pal.A(col, 140), share >= 0.99 ? text : null, true, (int)((6 + share * 22) * mult / 2)));
        }

        s.DashTitle = "Burbujas de volumen";
        s.Row("Nivel 1 / 2 / 3", $"{tiers[0]} / {tiers[1]} / {tiers[2]}");
    }
}

/// <summary>Las alertas del footprint: escaleras de desequilibrio compradoras y vendedoras y la celda grande de cada vela.</summary>
[DisplayName("Alertas del footprint (desequilibrios y celda grande)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Lee el footprint de cada vela y marca la escalera de desequilibrios compradores más larga (en su borde de arriba), la vendedora (en su borde de abajo) y la celda más grande si pasa un volumen. Cada marca es una serie con su precio: se le pone una alerta «mayor que 0».")]
public class FootprintAlerts : SceneIndicator
{
    public enum ImbalanceMethod
    {
        [Display(Name = "Diagonal")] Diagonal,
        [Display(Name = "Horizontal")] Horizontal,
    }

    private readonly ValueDataSeries _buy = new("SBI", "Escalera compradora") { VisualType = VisualMode.Hide, ShowZeroValue = false };
    private readonly ValueDataSeries _sell = new("SSI", "Escalera vendedora") { VisualType = VisualMode.Hide, ShowZeroValue = false };
    private readonly ValueDataSeries _big = new("BIG", "Celda grande") { VisualType = VisualMode.Hide, ShowZeroValue = false };

    [Display(Name = "Ticks por fila", GroupName = "Cálculo", Order = 10)] [Range(1, 1000)] public int TicksPerRow { get; set; } = 1;
    [Display(Name = "Método", GroupName = "Cálculo", Order = 11)] public ImbalanceMethod Method { get; set; } = ImbalanceMethod.Diagonal;
    [Display(Name = "Razón del desequilibrio", GroupName = "Cálculo", Order = 12)] [Range(1.1, 50)] public decimal Ratio { get; set; } = 3;
    [Display(Name = "Volumen mínimo (0 = no)", GroupName = "Cálculo", Order = 13)] [Range(0, 1e9)] public decimal MinVolume { get; set; }
    [Display(Name = "Filas seguidas para una escalera", GroupName = "Cálculo", Order = 14)] [Range(2, 20)] public int MinRun { get; set; } = 3;
    [Display(Name = "Saltar celdas vacías", GroupName = "Cálculo", Order = 15)] public bool SkipZero { get; set; } = true;
    [Display(Name = "Celda grande desde (0 = no)", GroupName = "Cálculo", Order = 16)] [Range(0, 1e9)] public decimal BigCell { get; set; }
    [Display(Name = "Compradora", GroupName = "Colores", Order = 20)] public Color BuyColor { get; set; } = Pal.Bull;
    [Display(Name = "Vendedora", GroupName = "Colores", Order = 21)] public Color SellColor { get; set; } = Pal.Bear;
    [Display(Name = "Celda grande", GroupName = "Colores", Order = 22)] public Color BigColor { get; set; } = Pal.Amber;

    public FootprintAlerts()
    {
        DataSeries.Add(_buy);
        DataSeries.Add(_sell);
        DataSeries.Add(_big);
        ShowDashboard = false;
    }

    /// <summary>Las filas de la vela agrupadas de a <see cref="TicksPerRow"/> ticks (de abajo arriba).</summary>
    private List<(decimal Price, decimal Bid, decimal Ask)> Rows(IndicatorCandle c, decimal tick)
    {
        decimal step = tick * TicksPerRow;
        return c.GetAllPriceLevels()
            .GroupBy(l => step > 0 ? Math.Floor(l.Price / step) * step : l.Price)
            .Select(g => (g.Key, g.Sum(x => x.Bid), g.Sum(x => x.Ask)))
            .OrderBy(r => r.Key)
            .ToList();
    }

    protected override void Build(Scene s)
    {
        decimal tick = ChartInfo?.PriceChartContainer.Step ?? 0;
        if (tick <= 0) tick = 0.01m;
        int marks = 0;
        for (int i = Math.Max(0, Bars.Count - 1500); i < Bars.Count; i++)
        {
            int bar = i + Offset;
            _buy[bar] = 0;
            _sell[bar] = 0;
            _big[bar] = 0;
            var rows = Rows(GetCandle(bar), tick);
            if (rows.Count < 2) continue;
            bool Imb(int k, bool buy)
            {
                var r = rows[k];
                decimal mine = buy ? r.Ask : r.Bid;
                decimal other;
                if (Method == ImbalanceMethod.Horizontal) other = buy ? r.Bid : r.Ask;
                else if (buy) other = k > 0 ? rows[k - 1].Bid : 0;
                else other = k + 1 < rows.Count ? rows[k + 1].Ask : 0;
                if (MinVolume > 0 && mine < MinVolume) return false;
                if (other == 0) return !SkipZero && mine > 0;
                return mine >= other * Ratio;
            }

            (int Len, int End) Longest(bool buy)
            {
                int best = 0, end = -1, run = 0;
                for (int k = 0; k < rows.Count; k++)
                {
                    run = Imb(k, buy) ? run + 1 : 0;
                    if (run > best)
                    {
                        best = run;
                        end = k;
                    }
                }

                return (best, end);
            }

            var (bl, be) = Longest(true);
            if (bl >= MinRun)
            {
                double top = (double)(rows[be].Price + tick * TicksPerRow);
                _buy[bar] = (decimal)top;
                s.Mark(i, top, MarkKind.TriangleUp, BuyColor, null, true);
                marks++;
            }

            var (sl, se) = Longest(false);
            if (sl >= MinRun)
            {
                double bottom = (double)rows[se - sl + 1].Price;
                _sell[bar] = (decimal)bottom;
                s.Mark(i, bottom, MarkKind.TriangleDown, SellColor, null, false);
                marks++;
            }

            if (BigCell > 0)
            {
                var big = rows.OrderByDescending(r => r.Bid + r.Ask).ThenBy(r => r.Price).First();
                if (big.Bid + big.Ask >= BigCell)
                {
                    double mid = (double)(big.Price + tick * TicksPerRow / 2);
                    _big[bar] = (decimal)mid;
                    s.Mark(i, mid, MarkKind.Square, BigColor, null, true);
                    marks++;
                }
            }
        }

        s.DashTitle = "Alertas del footprint";
        s.Row("Marcas", marks.ToString());
    }
}
