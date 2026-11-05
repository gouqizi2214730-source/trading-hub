using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Data;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Flow;

/// <summary>La regla de LiquidityMapperFast para «lo que se destaca»: más que la media más k desvíos de las velas anteriores.</summary>
public static class StandOutRule
{
    /// <summary>
    /// Umbral de la vela <paramref name="bar"/>: media + k·σ de las <paramref name="baseline"/> anteriores (la vela no sube su propio
    /// umbral). Null si todavía no hay una docena de velas.
    /// </summary>
    public static double? Threshold(Func<int, double> value, int bar, int baseline, double k)
    {
        int from = Math.Max(0, bar - baseline);
        int n = bar - from;
        if (n < 12) return null;
        double sum = 0, sq = 0;
        for (int i = from; i < bar; i++)
        {
            double v = value(i);
            sum += v;
            sq += v * v;
        }

        double mean = sum / n, var = Math.Max(0, sq / n - mean * mean);
        return mean + k * Math.Sqrt(var);
    }

    /// <summary>Cuántos desvíos por encima de la media de las anteriores está la vela.</summary>
    public static double? Sigmas(Func<int, double> value, int bar, int baseline)
    {
        int from = Math.Max(0, bar - baseline);
        int n = bar - from;
        if (n < 12) return null;
        double sum = 0, sq = 0;
        for (int i = from; i < bar; i++)
        {
            double v = value(i);
            sum += v;
            sq += v * v;
        }

        double mean = sum / n, sd = Math.Sqrt(Math.Max(0, sq / n - mean * mean));
        return sd > 0 ? (value(bar) - mean) / sd : null;
    }
}

/// <summary>
/// Absorción como en LiquidityMapperFast: mucho volumen a mercado de un lado, en un mismo precio, dentro de una ventana corta
/// (10 ms por defecto). Cada una de esas operaciones encontró una orden esperando en ese precio: el lado pasivo se lo comió
/// todo. Se marca lo que pasó (un círculo en el precio, del color del lado que absorbió); no dice qué hizo el precio después.
/// El umbral es fijo en dólares o automático (media + k desvíos de las sumas de ventana recientes).
/// </summary>
[DisplayName("Absorción (marcas en el gráfico)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Marca dónde el libro absorbió mucho volumen a mercado de un lado en un mismo precio y en una ventana corta (como LiquidityMapperFast).")]
public class AbsorptionMarks : Indicator
{
    /// <summary>Una absorción: precio, momento, dólares y si los agresores compraban (el libro vendedor absorbió).</summary>
    public readonly record struct Mark(DateTime Time, decimal Price, double Usd, bool AggressorBuy, int Fills);

    private readonly Dictionary<long, (int Count, List<Mark> Marks)> _cache = new();
    private readonly List<double> _recentSums = new();

    public AbsorptionMarks() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("ABS", "Absorción") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Ventana (ms)", GroupName = "Cálculo", Order = 10)]
    [Range(1, 5000)]
    public int WindowMs { get; set; } = 10;

    [Display(Name = "Umbral (USD; 0 = automático)", GroupName = "Cálculo", Order = 20)]
    public decimal ThresholdUsd { get; set; } = 250_000;

    [Display(Name = "Automático: k desvíos", GroupName = "Cálculo", Order = 21)]
    public decimal K { get; set; } = 4;

    [Display(Name = "Color si absorbió el bid (vendían a mercado)", GroupName = "Colores", Order = 30)]
    public Color BidAbsorbColor { get; set; } = Color.FromArgb(255, 38, 194, 129);

    [Display(Name = "Color si absorbió el ask (compraban a mercado)", GroupName = "Colores", Order = 31)]
    public Color AskAbsorbColor { get; set; } = Color.FromArgb(255, 240, 82, 90);

    [Display(Name = "Sonido al aparecer una nueva", GroupName = "Alertas", Order = 40)]
    public bool Sound { get; set; }

    private DateTime _lastAlerted = DateTime.MinValue;

    protected override void OnRecalculate()
    {
        _cache.Clear();
        _recentSums.Clear();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (!Sound || bar != CurrentBar - 1) return;
        foreach (var m in MarksOf(bar))
        {
            if (m.Time <= _lastAlerted || DateTime.UtcNow - m.Time > TimeSpan.FromSeconds(30)) continue;
            _lastAlerted = m.Time;
            AddAlert("alert2", string.Format(UiText.Tr("Absorción de {0} en {1} ({2} absorbió)"), FlowText.Usd(m.Usd), m.Price.ToString(CultureInfo.InvariantCulture), m.AggressorBuy ? "el ask" : "el bid"));
        }
    }

    /// <summary>
    /// Las absorciones de una lista de operaciones (en orden de tiempo): por precio y lado, la suma en dólares de las operaciones
    /// dentro de la ventana; un grupo cuenta si su mejor ventana llega al umbral.
    /// </summary>
    public static List<Mark> Find(IReadOnlyList<BarTrade> trades, int windowMs, double threshold, List<double>? sums = null)
    {
        var res = new List<Mark>();
        var groups = new Dictionary<(decimal, bool), (DateTime Start, DateTime Last, Queue<(DateTime T, double U)> Win, double WinSum, double Best, int Fills, DateTime BestAt)>();
        var window = TimeSpan.FromMilliseconds(Math.Max(1, windowMs));
        void Close((decimal Price, bool Buy) key, (DateTime Start, DateTime Last, Queue<(DateTime T, double U)> Win, double WinSum, double Best, int Fills, DateTime BestAt) g)
        {
            sums?.Add(g.Best);
            if (g.Best >= threshold) res.Add(new Mark(g.BestAt, key.Price, g.Best, key.Buy, g.Fills));
        }

        foreach (var t in trades)
        {
            var key = (t.Price, t.IsBuy);
            double usd = (double)(t.Price * t.Volume);
            if (groups.TryGetValue(key, out var g) && t.Time - g.Last > window)
            {
                Close(key, g);
                groups.Remove(key);
            }

            if (!groups.TryGetValue(key, out g)) g = (t.Time, t.Time, new Queue<(DateTime, double)>(), 0, 0, 0, t.Time);
            g.Win.Enqueue((t.Time, usd));
            g.WinSum += usd;
            while (g.Win.Count > 0 && t.Time - g.Win.Peek().T > window) g.WinSum -= g.Win.Dequeue().U;
            g.Fills++;
            g.Last = t.Time;
            if (g.WinSum > g.Best)
            {
                g.Best = g.WinSum;
                g.BestAt = t.Time;
            }

            groups[key] = g;
        }

        foreach (var (k, g) in groups) Close(k, g);
        return res;
    }

    private List<Mark> MarksOf(int bar)
    {
        var c = GetCandle(bar);
        long key = c.Time.Ticks;
        int count = (int)c.Ticks;
        if (_cache.TryGetValue(key, out var hit) && hit.Count == count) return hit.Marks;
        var trades = GetTrades(bar).ToList();
        var sums = new List<double>();
        double thr = (double)ThresholdUsd;
        if (thr <= 0)
        {
            // automático: media + k desvíos de las sumas de ventana de las velas ya vistas
            if (_recentSums.Count >= 50)
            {
                double mean = _recentSums.Average();
                double sd = Math.Sqrt(_recentSums.Sum(x => (x - mean) * (x - mean)) / _recentSums.Count);
                thr = mean + (double)K * sd;
            }
            else thr = 250_000;
        }

        var marks = Find(trades, WindowMs, thr, sums);
        if (bar < CurrentBar - 1)
        {
            _recentSums.AddRange(sums);
            if (_recentSums.Count > 20_000) _recentSums.RemoveRange(0, _recentSums.Count - 20_000);
        }

        _cache[key] = (count, marks);
        return marks;
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        var pc = ChartInfo.PriceChartContainer;
        int first = Math.Max(0, pc.FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, pc.LastVisibleBarNumber);
        var all = new List<Mark>();
        for (int b = first; b <= last; b++) all.AddRange(MarksOf(b));
        if (all.Count == 0) return;
        double max = all.Max(m => m.Usd);
        var font = new RenderFont("Arial", 7);
        foreach (var m in all)
        {
            int x = BookTools.XOf(this, m.Time), y = ChartInfo.GetYByPrice(m.Price, false);
            double t = Math.Sqrt(m.Usd / max);
            int r = (int)(4 + 10 * t);
            var color = m.AggressorBuy ? AskAbsorbColor : BidAbsorbColor;
            context.DrawEllipse(new RenderPen(color, 1.6f), new Rectangle(x - r, y - r, 2 * r, 2 * r));
            context.FillEllipse(Color.FromArgb(50, color.R, color.G, color.B), new Rectangle(x - r, y - r, 2 * r, 2 * r));
            if (r >= 9) context.DrawString(FlowText.Usd(m.Usd), font, Color.FromArgb(230, 240, 240, 245), x + r + 2, y - 5);
        }
    }
}

/// <summary>
/// Compras y ventas agresivas rechazadas («traders atrapados» de LiquidityMapperFast): en una vela cerrada cuya mecha tiene más
/// compra (o venta) agresiva neta que cualquier tramo igual de alto del resto de la vela, y que después cerró al menos un ATR
/// más allá del precio promedio estimado de esos agresores, las filas de la mecha se marcan en ámbar durante doce velas (o
/// hasta que el precio vuelva a cerrar más allá de ese precio). Describe un flujo que el precio rechazó; no dice quién tiene
/// qué ni está probado como señal.
/// </summary>
[DisplayName("Agresión rechazada (traders atrapados)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Marca en ámbar la mecha de una vela con mucha compra o venta agresiva que después el precio rechazó (cerró un ATR más allá).")]
public class TrappedTraders : Indicator
{
    public readonly record struct Trap(int Bar, decimal Low, decimal High, decimal AvgPrice, double Net, bool Buyers);

    private readonly List<Trap> _traps = new();
    private readonly ValueDataSeries _anchor = new("TRAP", "Atrapados") { VisualType = VisualMode.Hide, ShowZeroValue = false };

    public TrappedTraders() : base(true)
    {
        DataSeries[0] = _anchor;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "ATR de (velas)", GroupName = "Cálculo", Order = 10)]
    [Range(2, 200)]
    public int AtrPeriod { get; set; } = 14;

    [Display(Name = "Cuántos ATR más allá tiene que cerrar", GroupName = "Cálculo", Order = 20)]
    public decimal AtrMultiple { get; set; } = 1;

    [Display(Name = "Marcar durante (velas)", GroupName = "Visualización", Order = 30)]
    [Range(1, 200)]
    public int KeepBars { get; set; } = 12;

    [Display(Name = "Color", GroupName = "Visualización", Order = 40)]
    public Color Color { get; set; } = Color.FromArgb(255, 242, 181, 58);

    [Display(Name = "Sonido al marcarse una", GroupName = "Alertas", Order = 50)]
    public bool Sound { get; set; }

    protected override void OnRecalculate() => _traps.Clear();

    private decimal Atr(int bar)
    {
        int n = Math.Min(AtrPeriod, bar);
        if (n <= 0) return 0;
        decimal s = 0;
        for (int i = bar - n; i < bar; i++)
        {
            var c = GetCandle(i);
            var p = i > 0 ? GetCandle(i - 1).Close : c.Open;
            s += Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - p), Math.Abs(c.Low - p)));
        }

        return s / n;
    }

    /// <summary>
    /// La mecha de arriba (compradores) o de abajo (vendedores) de una vela con su footprint: (neto en la mecha, precio
    /// promedio de los agresores en ella, mejor neto de cualquier tramo igual de alto del cuerpo y la otra mecha).
    /// </summary>
    public static (double WickNet, decimal Avg, double OtherBest) Wick(IReadOnlyList<PriceVolumeInfo> levels, decimal from, decimal to, bool buyers)
    {
        var rows = levels.OrderBy(l => l.Price).ToList();
        double wick = 0, vol = 0, px = 0;
        var inWick = new List<double>();
        var outside = new List<double>();
        foreach (var l in rows)
        {
            double net = buyers ? (double)(l.Ask - l.Bid) : (double)(l.Bid - l.Ask);
            if (l.Price >= from && l.Price <= to)
            {
                wick += net;
                double v = (double)(buyers ? l.Ask : l.Bid);
                vol += v;
                px += v * (double)l.Price;
                inWick.Add(net);
            }
            else outside.Add(net);
        }

        int h = Math.Max(1, inWick.Count);
        double best = double.MinValue;
        for (int i = 0; i + h <= outside.Count; i++)
        {
            double s = 0;
            for (int j = i; j < i + h; j++) s += outside[j];
            best = Math.Max(best, s);
        }

        return (wick, vol > 0 ? (decimal)(px / vol) : (from + to) / 2, best == double.MinValue ? 0 : best);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        // se decide cada vela una sola vez, cuando ya cerró (la anterior a la en formación)
        int b = bar - 1;
        if (b < AtrPeriod) return;
        if (_traps.Any(t => t.Bar == b)) return;
        var c = GetCandle(b);
        var levels = c.GetAllPriceLevels().ToList();
        if (levels.Count < 3) return;
        decimal atr = Atr(b) * AtrMultiple;
        if (atr <= 0) return;
        decimal top = Math.Max(c.Open, c.Close), bottom = Math.Min(c.Open, c.Close);
        foreach (bool buyers in new[] { true, false })
        {
            decimal lo = buyers ? top : c.Low, hi = buyers ? c.High : bottom;
            if (hi - lo <= 0) continue;
            var (net, avg, other) = Wick(levels, buyers ? lo + 1e-12m : lo, buyers ? hi : hi - 1e-12m, buyers);
            if (net <= 0 || net <= other) continue;
            bool rejected = buyers ? c.Close <= avg - atr : c.Close >= avg + atr;
            if (!rejected) continue;
            var trap = new Trap(b, lo, hi, avg, net, buyers);
            _traps.Add(trap);
            if (Sound && b == CurrentBar - 2)
                AddAlert("alert2", string.Format(UiText.Tr("{0} atrapados: mecha con {1} neto rechazada"), buyers ? UiText.Tr("Compradores") : UiText.Tr("Vendedores"), FlowText.Num(net)));
        }
    }

    /// <summary>Hasta dónde vive el promedio de unos atrapados: hasta que el precio cierra de vuelta más allá, o <see cref="KeepBars"/>.</summary>
    private int EndOf(Trap t) => Life(t).End;

    private (int End, bool Dead) Life(Trap t)
    {
        int end = Math.Min(CurrentBar - 1, t.Bar + KeepBars);
        for (int b = t.Bar + 1; b <= end; b++)
        {
            var c = GetCandle(b);
            if (t.Buyers ? c.Close > t.AvgPrice : c.Close < t.AvgPrice) return (b, true);
        }

        return (end, false);
    }

    /// <summary>La línea punteada del precio promedio de cada grupo de atrapados, para las alertas.</summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        foreach (var t in _traps.TakeLast(50))
        {
            if (t.Bar < 0 || t.Bar >= CurrentBar) continue;
            var time = GetCandle(t.Bar).Time;
            var (end, dead) = Life(t);
            int to = dead ? end : t.Bar + KeepBars; // si sigue vivo, sigue a la derecha hasta KeepBars
            yield return new IndicatorLevel($"trap{(t.Buyers ? "b" : "s")}@{time.Ticks}",
                $"{UiText.Tr(t.Buyers ? "Compradores atrapados" : "Vendedores atrapados")} {time:dd/MM HH:mm} · {UiText.Tr("promedio")}", t.AvgPrice, t.Bar, to);
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        var pc = ChartInfo.PriceChartContainer;
        int bw = Math.Max(2, pc.BarsWidth);
        var font = new RenderFont("Arial", 7);
        foreach (var t in _traps)
        {
            if (t.Bar > pc.LastVisibleBarNumber || t.Bar + KeepBars < pc.FirstVisibleBarNumber) continue;
            // se apaga si el precio cerró de vuelta más allá del promedio de los atrapados
            int end = EndOf(t);

            int x0 = ChartInfo.GetXByBar(t.Bar), x1 = ChartInfo.GetXByBar(end) + bw;
            int y0 = ChartInfo.GetYByPrice(t.High, false), y1 = ChartInfo.GetYByPrice(t.Low, false);
            var rect = new Rectangle(x0 - 1, Math.Min(y0, y1) - 1, bw + 2, Math.Abs(y1 - y0) + 2);
            context.DrawRectangle(new RenderPen(Color, 1.5f), rect);
            int ya = ChartInfo.GetYByPrice(t.AvgPrice, false);
            context.DrawLine(new RenderPen(Color.FromArgb(160, Color.R, Color.G, Color.B), 1, Rendering.Settings.PenSettings.ToDash(LineDashStyle.Dot)), x0, ya, x1, ya);
            context.DrawString(t.Buyers ? "▲✕" : "▼✕", font, Color, x0, t.Buyers ? Math.Min(y0, y1) - 11 : Math.Max(y0, y1) + 2);
        }
    }
}

/// <summary>
/// «Lo que se destaca» y sus sonidos, como Highlights y Sound de LiquidityMapperFast: una vela se destaca cuando su volumen
/// (o su delta neto) supera la media más k desvíos de las anteriores; la vela se marca con un anillo y una columna tenue. Además
/// avisa (con sonido, opcional) un estallido de flujo agresor (el delta de 10 s muy por encima de lo normal), un muro del libro
/// que aparece o se retira sin que el precio llegue, y el balance del libro que se da vuelta.
/// </summary>
[DisplayName("Destacados y avisos (volumen, delta, muros, balance)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Marca las velas con volumen o delta fuera de lo normal (media + k desvíos) y avisa estallidos de flujo, muros que aparecen o se retiran y el balance del libro que se da vuelta.")]
public class HighlightsAndSounds : Indicator
{
    private readonly ValueDataSeries _vol = new("HVOL", "Volumen destacado") { VisualType = VisualMode.Hide, ShowZeroValue = false };
    private readonly HashSet<long> _flagged = new();
    private DateTime _lastSound = DateTime.MinValue;
    private readonly Queue<DateTime> _recentSounds = new();
    private readonly Dictionary<long, (double Size, DateTime Since)> _walls = new();
    private int _balance;
    private DateTime _bookStart = DateTime.MinValue;
    private readonly List<(DateTime T, double Delta)> _flow = new();
    private readonly List<double> _flowNorm = new();
    private DateTime _flowSlot = DateTime.MinValue;
    private double _slotDelta;

    public HighlightsAndSounds() : base(true)
    {
        DataSeries[0] = _vol;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "k (desvíos sobre la media)", GroupName = "Regla", Order = 10)]
    public decimal K { get; set; } = 2;

    [Display(Name = "Velas de referencia", GroupName = "Regla", Order = 11)]
    [Range(12, 1000)]
    public int Baseline { get; set; } = 72;

    [Display(Name = "Destacar volumen", GroupName = "Destacados", Order = 20)]
    public bool Volume { get; set; } = true;

    [Display(Name = "Destacar delta neto", GroupName = "Destacados", Order = 21)]
    public bool Delta { get; set; } = true;

    [Display(Name = "Sonar al cerrar una vela destacada", GroupName = "Sonidos", Order = 30)]
    public bool SoundBar { get; set; }

    [Display(Name = "Sonar con un estallido de flujo (10 s)", GroupName = "Sonidos", Order = 31)]
    public bool SoundBurst { get; set; }

    [Display(Name = "Estallido: desvíos sobre lo normal (30 min)", GroupName = "Sonidos", Order = 32)]
    public decimal BurstSigmas { get; set; } = 4;

    [Display(Name = "Sonar con muros que aparecen o se retiran", GroupName = "Sonidos", Order = 33)]
    public bool SoundWalls { get; set; }

    [Display(Name = "Muro desde (USD, dentro del 1 %)", GroupName = "Sonidos", Order = 34)]
    public decimal WallUsd { get; set; } = 5_000_000;

    [Display(Name = "Sonar cuando el balance del libro (1 %) se da vuelta", GroupName = "Sonidos", Order = 35)]
    public bool SoundBalance { get; set; }

    [Display(Name = "Color", GroupName = "Visualización", Order = 40)]
    public Color Color { get; set; } = Color.FromArgb(255, 242, 181, 58);

    protected override void OnRecalculate()
    {
        _flagged.Clear();
        _walls.Clear();
        _flow.Clear();
        _flowNorm.Clear();
    }

    private double VolOf(int b) => (double)GetCandle(b).Volume;

    private double DeltaOf(int b) => Math.Abs((double)GetCandle(b).Delta);

    public bool Stands(int bar, out string why)
    {
        why = "";
        double k = (double)K;
        if (Volume && StandOutRule.Threshold(VolOf, bar, Baseline, k) is { } tv && VolOf(bar) > tv)
        {
            why = string.Format(UiText.Tr("volumen {0} σ"), (StandOutRule.Sigmas(VolOf, bar, Baseline) ?? 0).ToString("0.0", CultureInfo.InvariantCulture));
            return true;
        }

        if (Delta && StandOutRule.Threshold(DeltaOf, bar, Baseline, k) is { } td && DeltaOf(bar) > td)
        {
            why = string.Format(UiText.Tr("delta {0} σ"), (StandOutRule.Sigmas(DeltaOf, bar, Baseline) ?? 0).ToString("0.0", CultureInfo.InvariantCulture));
            return true;
        }

        return false;
    }

    /// <summary>Como mucho 4 sonidos cada 10 s, y nada de la historia.</summary>
    private void Sound(string message)
    {
        var now = DateTime.UtcNow;
        while (_recentSounds.Count > 0 && now - _recentSounds.Peek() > TimeSpan.FromSeconds(10)) _recentSounds.Dequeue();
        if (_recentSounds.Count >= 4 || now - _lastSound < TimeSpan.FromSeconds(1)) return;
        _recentSounds.Enqueue(now);
        _lastSound = now;
        AddAlert("alert2", message);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        // vela recién cerrada
        if (bar == CurrentBar - 1 && bar > 0)
        {
            long key = GetCandle(bar - 1).Time.Ticks;
            if (SoundBar && !_flagged.Contains(key) && Stands(bar - 1, out var why))
            {
                _flagged.Add(key);
                if (DateTime.UtcNow - GetCandle(bar).Time < TimeSpan.FromMinutes(2)) Sound(UiText.Tr("Vela destacada: ") + why);
            }
        }
    }

    protected override void OnNewTrade(MarketDataArg trade)
    {
        if (!SoundBurst) return;
        var slot = new DateTime(trade.Time.Ticks / TimeSpan.TicksPerSecond / 10 * TimeSpan.TicksPerSecond * 10, DateTimeKind.Utc);
        double d = (double)(trade.Volume * trade.Price) * (trade.Direction == TradeDirection.Buy ? 1 : trade.Direction == TradeDirection.Sell ? -1 : 0);
        if (slot != _flowSlot)
        {
            if (_flowSlot != DateTime.MinValue)
            {
                _flowNorm.Add(_slotDelta);
                if (_flowNorm.Count > 180) _flowNorm.RemoveAt(0); // 30 minutos de tramos de 10 s
            }

            _flowSlot = slot;
            _slotDelta = 0;
        }

        _slotDelta += d;
        if (_flowNorm.Count < 30) return;
        double mean = _flowNorm.Average(), sd = Math.Sqrt(_flowNorm.Sum(x => (x - mean) * (x - mean)) / _flowNorm.Count);
        if (sd > 0 && Math.Abs(_slotDelta - mean) > (double)BurstSigmas * sd && Math.Abs(_slotDelta) > 250_000)
        {
            Sound(string.Format(UiText.Tr("Estallido de flujo: {0} en 10 s"), FlowText.Signed(_slotDelta)));
            _slotDelta = mean; // un aviso por tramo
        }
    }

    protected override void MarketDepthChanged(MarketDataArg depth)
    {
        if (!SoundWalls && !SoundBalance) return;
        var now = DateTime.UtcNow;
        if (_bookStart == DateTime.MinValue) _bookStart = now;
        if (now - _bookStart < TimeSpan.FromSeconds(10)) return; // los primeros segundos del libro no cuentan
        var snap = MarketDepthInfo.GetMarketDepthSnapshot().ToList();
        decimal bb = snap.Where(x => x.DataType == MarketDataType.Bid).Select(x => x.Price).DefaultIfEmpty(0).Max();
        decimal ba = snap.Where(x => x.DataType == MarketDataType.Ask).Select(x => x.Price).DefaultIfEmpty(0).Min();
        if (bb <= 0 || ba <= 0) return;
        decimal mid = (bb + ba) / 2;
        decimal tick = BookTools.Tick(this);
        if (SoundWalls)
        {
            // por franjas de 2 puntos básicos dentro del 1 %
            decimal bin = Math.Max(tick, mid * 0.0002m);
            var sizes = new Dictionary<long, double>();
            foreach (var x in snap)
            {
                if (Math.Abs(x.Price - mid) > mid * 0.01m) continue;
                long k = (long)Math.Floor(x.Price / bin);
                sizes[k] = (sizes.TryGetValue(k, out var v) ? v : 0) + (double)(x.Volume * x.Price);
            }

            double wall = (double)WallUsd;
            foreach (var (k, usd) in sizes)
                if (usd >= wall && !_walls.ContainsKey(k))
                {
                    _walls[k] = (usd, now);
                    Sound(string.Format(UiText.Tr("Muro de {0} en {1}"), FlowText.Usd(usd), (k * bin).ToString("0.##", CultureInfo.InvariantCulture)));
                }

            foreach (var k in _walls.Keys.ToList())
            {
                if (sizes.TryGetValue(k, out var u) && u >= wall * 0.5) continue;
                decimal p = k * bin;
                bool reached = Math.Abs(p - mid) <= bin * 2;
                _walls.Remove(k);
                if (!reached) Sound(string.Format(UiText.Tr("Muro retirado en {0} (sin que llegue el precio)"), p.ToString("0.##", CultureInfo.InvariantCulture)));
            }
        }

        if (SoundBalance)
        {
            double bids = snap.Where(x => x.DataType == MarketDataType.Bid && x.Price >= mid * 0.99m).Sum(x => (double)(x.Volume * x.Price));
            double asks = snap.Where(x => x.DataType == MarketDataType.Ask && x.Price <= mid * 1.01m).Sum(x => (double)(x.Volume * x.Price));
            double imb = bids + asks > 0 ? (bids - asks) / (bids + asks) : 0;
            int side = imb > 0.15 ? 1 : imb < -0.15 ? -1 : _balance; // con histéresis
            if (side != _balance && _balance != 0)
                Sound(side > 0 ? UiText.Tr("El libro se dio vuelta: más bids (1 %)") : UiText.Tr("El libro se dio vuelta: más asks (1 %)"));
            _balance = side;
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 13) return;
        var pc = ChartInfo.PriceChartContainer;
        var region = pc.Region;
        int first = Math.Max(0, pc.FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, pc.LastVisibleBarNumber);
        int bw = Math.Max(2, pc.BarsWidth);
        var font = new RenderFont("Arial", 7);
        for (int b = first; b <= last; b++)
        {
            if (!Stands(b, out var why)) continue;
            var c = GetCandle(b);
            int x = ChartInfo.GetXByBar(b);
            context.FillRectangle(Color.FromArgb(18, Color.R, Color.G, Color.B), new Rectangle(x, region.Top, bw, region.Height));
            int y0 = ChartInfo.GetYByPrice(c.High, false), y1 = ChartInfo.GetYByPrice(c.Low, false);
            context.DrawRectangle(new RenderPen(Color.FromArgb(220, Color.R, Color.G, Color.B), 1.5f), new Rectangle(x - 2, Math.Min(y0, y1) - 2, bw + 4, Math.Abs(y1 - y0) + 4));
            if (bw >= 18) context.DrawString(why, font, Color, x, Math.Min(y0, y1) - 12);
        }
    }
}
