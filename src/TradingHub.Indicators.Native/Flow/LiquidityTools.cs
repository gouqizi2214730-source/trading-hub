using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using TradingHub.Data;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Flow;

/// <summary>
/// Liquidity Tracker de LiquidityMapperFast: LT-Bid y LT-Ask son los dólares de cada lado del libro (todos los mercados del
/// agregado) pesados por 2^(−d / vida media), con d la distancia en puntos básicos desde el mejor precio de ese lado (los
/// pesos que bajan exponencialmente de Bookmap; en puntos básicos para que exchanges con distinto tick coincidan). Se
/// calcula con las fotos del libro grabadas, así cambiar un ajuste recalcula toda la historia visible.
/// </summary>
[DisplayName("Liquidity Tracker (LT-Bid / LT-Ask)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("La liquidez de cada lado del libro pesada por la distancia al precio (vida media en puntos básicos), como el Liquidity Tracker de Bookmap y LiquidityMapperFast. Líneas o desequilibrio.")]
public class LiquidityTracker : Indicator
{
    public enum LtView
    {
        [Display(Name = "Líneas LT-Bid y LT-Ask")] Lines,
        [Display(Name = "Desequilibrio (%)")] Imbalance,
    }

    private readonly ValueDataSeries _bid = new("LTB", "LT-Bid") { Color = Color.FromArgb(255, 38, 194, 129), Width = 2, ShowZeroValue = false };
    private readonly ValueDataSeries _ask = new("LTA", "LT-Ask") { Color = Color.FromArgb(255, 240, 82, 90), Width = 2, ShowZeroValue = false };
    private readonly ValueDataSeries _imb = new("LTI", "Desequilibrio") { VisualType = VisualMode.Histogram, ShowZeroValue = false };
    private LtView _view = LtView.Lines;
    private decimal _half = 10, _min, _max;
    private bool _perLevel;

    public LiquidityTracker() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _bid;
        DataSeries.Add(_ask);
        DataSeries.Add(_imb);
    }

    [Display(Name = "Vista", GroupName = "Visualización", Order = 10)]
    public LtView View { get => _view; set { _view = value; RecalculateValues(); } }

    [Display(Name = "Vida media (puntos básicos)", GroupName = "Cálculo", Order = 20, Description = "A esta distancia del mejor precio un nivel pesa la mitad; al doble, un cuarto.")]
    public decimal HalfLife { get => _half; set { _half = Math.Max(0.1m, value); RecalculateValues(); } }

    [Display(Name = "Solo niveles desde (USD, 0 = todos)", GroupName = "Cálculo", Order = 30)]
    public decimal MinLevelUsd { get => _min; set { _min = Math.Max(0, value); RecalculateValues(); } }

    [Display(Name = "Solo niveles hasta (USD, 0 = sin límite)", GroupName = "Cálculo", Order = 31)]
    public decimal MaxLevelUsd { get => _max; set { _max = Math.Max(0, value); RecalculateValues(); } }

    [Display(Name = "Promedio por nivel (en vez de la suma)", GroupName = "Cálculo", Order = 40)]
    public bool PerLevelAverage { get => _perLevel; set { _perLevel = value; RecalculateValues(); } }

    protected override void OnRecalculate()
    {
        bool lines = _view == LtView.Lines;
        _bid.VisualType = lines ? VisualMode.Line : VisualMode.Hide;
        _ask.VisualType = lines ? VisualMode.Line : VisualMode.Hide;
        _imb.VisualType = lines ? VisualMode.Hide : VisualMode.Histogram;
    }

    /// <summary>LT de una foto del libro: (bid, ask) en dólares pesados.</summary>
    public static (double Bid, double Ask) Measure(DepthSample s, decimal tick, double halfLifeBp, double minUsd, double maxUsd, bool perLevel)
    {
        double t = (double)tick, bb = s.BestBid * t, ba = s.BestAsk * t;
        if (bb <= 0 || ba <= 0) return (0, 0);
        double bid = 0, ask = 0, wb = 0, wa = 0;
        for (int i = 0; i < s.BidOffsets.Length; i++)
        {
            double p = s.BidPrice(i) * t, usd = s.BidSizes[i] * p;
            if (usd < minUsd || (maxUsd > 0 && usd > maxUsd)) continue;
            double w = Math.Pow(2, -((bb - p) / bb * 1e4) / halfLifeBp);
            bid += usd * w;
            wb += w;
        }

        for (int i = 0; i < s.AskOffsets.Length; i++)
        {
            double p = s.AskPrice(i) * t, usd = s.AskSizes[i] * p;
            if (usd < minUsd || (maxUsd > 0 && usd > maxUsd)) continue;
            double w = Math.Pow(2, -((p - ba) / ba * 1e4) / halfLifeBp);
            ask += usd * w;
            wa += w;
        }

        return perLevel ? (wb > 0 ? bid / wb : 0, wa > 0 ? ask / wa : 0) : (bid, ask);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var samples = GetDepthSamples(bar);
        if (samples.Count == 0) return;
        decimal tick = BookTools.Tick(this);
        double b = 0, a = 0;
        foreach (var s in samples)
        {
            var (x, y) = Measure(s, tick, (double)_half, (double)_min, (double)_max, _perLevel);
            b += x;
            a += y;
        }

        b /= samples.Count;
        a /= samples.Count;
        _bid[bar] = (decimal)b;
        _ask[bar] = (decimal)a;
        double imb = b + a > 0 ? (b - a) / (b + a) * 100 : 0;
        _imb[bar] = (decimal)imb;
        _imb.Colors[bar] = imb >= 0 ? Color.FromArgb(220, 38, 194, 129) : Color.FromArgb(220, 240, 82, 90);
    }
}

/// <summary>
/// Mirror de LiquidityMapperFast: con el mouse sobre el gráfico, la franja desde el precio hasta el mouse y la franja igual de
/// ancha del otro lado se enmarcan con el color de cada lado y dicen cuánto tiene cada una (en el libro de ahora) y cuál
/// tiene más («el otro lado tiene 1,13× más»). Alejar el mouse muestra cómo cambia el balance con la distancia.
/// </summary>
[DisplayName("Mirror: comparar el libro a igual distancia")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Al pasar el mouse, compara la liquidez del libro entre el precio y el mouse con la franja igual del otro lado.")]
public class BookMirror : Indicator
{
    public BookMirror() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("MIR", "Mirror") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Ancho del marco (px desde la derecha)", GroupName = "Visualización", Order = 10)]
    [Range(60, 800)]
    public int Width { get; set; } = 180;

    [Display(Name = "Color bid", GroupName = "Colores", Order = 20)]
    public Color BidColor { get; set; } = Color.FromArgb(255, 38, 194, 129);

    [Display(Name = "Color ask", GroupName = "Colores", Order = 21)]
    public Color AskColor { get; set; } = Color.FromArgb(255, 240, 82, 90);

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    /// <summary>Suma del libro entre dos precios (incluidos), en monedas y en dólares.</summary>
    public static (double Coins, double Usd) Sum(IEnumerable<(decimal Price, decimal Size)> levels, decimal lo, decimal hi)
    {
        double c = 0, u = 0;
        foreach (var (p, s) in levels)
        {
            if (p < lo || p > hi) continue;
            c += (double)s;
            u += (double)(s * p);
        }

        return (c, u);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        var m = MouseLocationInfo;
        if (m.IsMouseLeave) return;
        var region = ChartInfo.PriceChartContainer.Region;
        if (!region.Contains(m.LastPosition)) return;
        var book = new List<(decimal Price, decimal Size, bool Bid)>();
        foreach (var d in MarketDepthInfo.GetMarketDepthSnapshot())
            if (d.Volume > 0 && d.DataType is MarketDataType.Bid or MarketDataType.Ask) book.Add((d.Price, d.Volume, d.DataType == MarketDataType.Bid));
        if (book.Count == 0)
        {
            // sin el libro en vivo: la última foto grabada
            var samples = GetDepthSamples(CurrentBar - 1);
            if (samples.Count > 0)
            {
                var s = samples[^1];
                decimal tick = BookTools.Tick(this);
                for (int i = 0; i < s.BidOffsets.Length; i++) book.Add((s.BidPrice(i) * tick, (decimal)s.BidSizes[i], true));
                for (int i = 0; i < s.AskOffsets.Length; i++) book.Add((s.AskPrice(i) * tick, (decimal)s.AskSizes[i], false));
            }
        }

        if (book.Count == 0) return;
        decimal bestBid = book.Where(b => b.Bid).Select(b => b.Price).DefaultIfEmpty(0).Max();
        decimal bestAsk = book.Where(b => !b.Bid).Select(b => b.Price).DefaultIfEmpty(0).Min();
        if (bestBid <= 0 || bestAsk <= 0) return;
        decimal mark = (bestBid + bestAsk) / 2;
        decimal mouse = m.PriceBelowMouse;
        decimal dist = Math.Abs(mouse - mark);
        if (dist <= 0) return;
        bool mouseAbove = mouse > mark;
        var asks = book.Where(b => !b.Bid).Select(b => (b.Price, b.Size));
        var bids = book.Where(b => b.Bid).Select(b => (b.Price, b.Size));
        var (askC, askU) = Sum(asks, mark, mark + dist);
        var (bidC, bidU) = Sum(bids, mark - dist, mark);
        int x1 = region.Right - 2, x0 = Math.Max(region.Left, x1 - Math.Clamp(Width, 60, 800));
        int yMark = ChartInfo.GetYByPrice(mark, false), yUp = ChartInfo.GetYByPrice(mark + dist, false), yDown = ChartInfo.GetYByPrice(mark - dist, false);
        context.FillRectangle(Color.FromArgb(30, AskColor.R, AskColor.G, AskColor.B), new Rectangle(x0, Math.Min(yUp, yMark), x1 - x0, Math.Abs(yMark - yUp)));
        context.FillRectangle(Color.FromArgb(30, BidColor.R, BidColor.G, BidColor.B), new Rectangle(x0, Math.Min(yDown, yMark), x1 - x0, Math.Abs(yDown - yMark)));
        context.DrawRectangle(new RenderPen(AskColor, 1), new Rectangle(x0, Math.Min(yUp, yMark), x1 - x0, Math.Abs(yMark - yUp)));
        context.DrawRectangle(new RenderPen(BidColor, 1), new Rectangle(x0, Math.Min(yDown, yMark), x1 - x0, Math.Abs(yDown - yMark)));
        var font = new RenderFont("Arial", 8);
        var bold = new RenderFont("Arial", 8, FontStyle.Bold);
        double bp = (double)(dist / mark) * 1e4;
        string pct = bp >= 100 ? (bp / 100).ToString("0.##", CultureInfo.InvariantCulture) + " %" : bp.ToString("0.#", CultureInfo.InvariantCulture) + " bp";
        context.DrawString($"asks {FlowText.Usd(askU)} · {FlowText.Num(askC)}", bold, AskColor, x0 + 4, Math.Min(yUp, yMark) + 2);
        context.DrawString($"bids {FlowText.Usd(bidU)} · {FlowText.Num(bidC)}", bold, BidColor, x0 + 4, Math.Max(yDown, yMark) - 13);
        double near = mouseAbove ? askU : bidU, far = mouseAbove ? bidU : askU;
        string verdict = near <= 0 && far <= 0 ? ""
            : far >= near
                ? string.Format(UiText.Tr("el otro lado tiene {0}× más"), (near > 0 ? far / near : 99).ToString("0.00", CultureInfo.CurrentCulture))
                : string.Format(UiText.Tr("este lado tiene {0}× más"), (far > 0 ? near / far : 99).ToString("0.00", CultureInfo.CurrentCulture));
        string text = "± " + pct + (verdict.Length > 0 ? " · " + verdict : "");
        var size = context.MeasureString(text, font);
        context.FillRectangle(FlowText.Back, new Rectangle(x0, yMark - 7, size.Width + 8, 14));
        context.DrawString(text, font, Color.FromArgb(235, 235, 240), x0 + 4, yMark - 6);
    }
}

/// <summary>
/// Niveles de liquidación de Hyperliquid (como en LiquidityMapperFast): los precios que liquidarían las posiciones abiertas
/// en BTC, según las fotos de posiciones de HyperTracker (una cada ~30 min), agrupadas cada $10 por el servicio público de
/// LiquidityMapperFast. Se dibujan a la derecha de la última vela: largos debajo del precio, cortos encima; cuanto más hay,
/// más fuerte. Un nivel desaparece cuando el precio pasó por él después de la foto. Fotos de más de 3 horas no se dibujan.
/// </summary>
[DisplayName("Niveles de liquidación (Hyperliquid · HyperTracker)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Dónde se liquidarían las posiciones abiertas de BTC en Hyperliquid (fotos de HyperTracker, cada $10): largos abajo, cortos arriba, a la derecha de la última vela.")]
public class LiquidationLevels : Indicator
{
    public const string ServiceUrl = "https://levels.hiverust.dev/v1/btc/liquidations.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Una franja de $step: dólares de largos y de cortos (aislados + cruzados) y la posición más grande de cada lado.</summary>
    public readonly record struct Bin(double Price, double Long, double Short, double LongTop, double ShortTop);

    public sealed record Snapshot(DateTime AsOf, double Mark, double Step, int Positions, string Attribution, List<Bin> Bins);

    private Snapshot? _snap;
    private DateTime _fetched = DateTime.MinValue;
    private bool _loading;
    private string? _error;
    private readonly Action _tick;

    public LiquidationLevels() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("LIQLV", "Niveles") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _tick = () =>
        {
            if (DateTime.UtcNow - _fetched > TimeSpan.FromSeconds(60)) Fetch();
        };
    }

    [Display(Name = "Ancho de la franja (px)", GroupName = "Visualización", Order = 10)]
    [Range(30, 600)]
    public int Width { get; set; } = 160;

    [Display(Name = "Agrupar de a (USD de precio, 0 = los $10 del servicio)", GroupName = "Visualización", Order = 20)]
    public decimal Group { get; set; }

    [Display(Name = "Mostrar el total acumulado desde el precio", GroupName = "Visualización", Order = 30)]
    public bool Cumulative { get; set; } = true;

    [Display(Name = "Color largos", GroupName = "Colores", Order = 40)]
    public Color LongColor { get; set; } = Color.FromArgb(255, 240, 82, 90);

    [Display(Name = "Color cortos", GroupName = "Colores", Order = 41)]
    public Color ShortColor { get; set; } = Color.FromArgb(255, 38, 194, 129);

    protected override void OnInitialize() => SubscribeToTimer(TimeSpan.FromSeconds(5), _tick);

    protected override void OnDispose() => UnsubscribeFromTimer(TimeSpan.FromSeconds(5), _tick);

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == CurrentBar - 1 && _snap == null && !_loading && DateTime.UtcNow - _fetched > TimeSpan.FromSeconds(30)) Fetch();
    }

    private bool IsBtc => (InstrumentInfo?.Instrument ?? "").ToUpperInvariant().StartsWith("BTC", StringComparison.Ordinal);

    private void Fetch()
    {
        if (_loading || !IsBtc) return;
        _loading = true;
        _fetched = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            Snapshot? s = null;
            string? err = null;
            try
            {
                s = Parse(await Http.GetStringAsync(ServiceUrl).ConfigureAwait(false));
                if (s == null) err = UiText.Tr("respuesta con otro formato");
            }
            catch (Exception e)
            {
                err = e.Message;
            }

            DoActionInGuiThread(() =>
            {
                _loading = false;
                if (s != null) _snap = s;
                _error = s == null ? err : null;
                RedrawChart();
            });
        });
    }

    /// <summary>El archivo del servicio (formato 2): cada franja es [k, largos aislados, largos cruzados, cortos aislados, cortos cruzados, billeteras…, mayor largo, mayor corto].</summary>
    public static Snapshot? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var d = doc.RootElement;
        if (!d.TryGetProperty("v", out var v) || v.GetInt32() != 2 || !d.TryGetProperty("snapshot", out var snap)) return null;
        double step = d.GetProperty("step").GetDouble();
        if (step <= 0) return null;
        var bins = new List<Bin>();
        foreach (var b in d.GetProperty("bins").EnumerateArray())
        {
            if (b.GetArrayLength() < 9) continue;
            double k = b[0].GetDouble();
            bins.Add(new Bin(k * step, b[1].GetDouble() + b[2].GetDouble(), b[3].GetDouble() + b[4].GetDouble(), b[7].GetDouble(), b[8].GetDouble()));
        }

        var asOf = DateTimeOffset.FromUnixTimeMilliseconds(snap.GetProperty("asOf").GetInt64()).UtcDateTime;
        string attr = d.TryGetProperty("attribution", out var a) ? a.GetString() ?? "" : "";
        int positions = d.TryGetProperty("positions", out var p) ? p.GetInt32() : 0;
        return new Snapshot(asOf, d.GetProperty("mark").GetDouble(), step, positions, attr, bins);
    }

    /// <summary>Lo que queda de cada franja después de que el precio pasó (el mínimo desde la foto se lleva los largos; el máximo, los cortos).</summary>
    public static List<Bin> Remaining(Snapshot s, double lowSince, double highSince) =>
        s.Bins.Select(b => new Bin(b.Price, b.Price + s.Step > lowSince ? 0 : b.Long, b.Price < highSince ? 0 : b.Short, b.LongTop, b.ShortTop))
            .Where(b => b.Long > 0 || b.Short > 0).ToList();

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        var region = ChartInfo.PriceChartContainer.Region;
        var font = new RenderFont("Arial", 8);
        if (!IsBtc)
        {
            context.DrawString(UiText.Tr("Niveles de liquidación: solo BTC (el servicio de HyperTracker es de BTC)."), font, FlowText.Dim, region.Left + 8, region.Bottom - 14);
            return;
        }

        if (_snap == null)
        {
            string msg = _loading ? UiText.Tr("Trayendo los niveles de liquidación…") : UiText.Tr("Niveles de liquidación: ") + (_error ?? UiText.Tr("sin datos"));
            context.DrawString(msg, font, FlowText.Dim, region.Left + 8, region.Bottom - 14);
            return;
        }

        if (DateTime.UtcNow - _snap.AsOf > TimeSpan.FromHours(3))
        {
            context.DrawString(UiText.Tr("Niveles de liquidación: la última foto tiene más de 3 horas (no se dibuja)."), font, FlowText.Dim, region.Left + 8, region.Bottom - 14);
            return;
        }

        // lo que el precio ya recorrió desde la foto
        double lo = double.MaxValue, hi = double.MinValue;
        for (int b = CurrentBar - 1; b >= 0; b--)
        {
            var c = GetCandle(b);
            var end = b + 1 < CurrentBar ? GetCandle(b + 1).Time : DateTime.UtcNow;
            if (end <= _snap.AsOf) break;
            lo = Math.Min(lo, (double)c.Low);
            hi = Math.Max(hi, (double)c.High);
        }

        var bins = Remaining(_snap, lo == double.MaxValue ? _snap.Mark : lo, hi == double.MinValue ? _snap.Mark : hi);
        double step = Group > 0 ? Math.Max(_snap.Step, (double)Group) : _snap.Step;
        var grouped = bins.GroupBy(b => Math.Floor(b.Price / step) * step)
            .Select(g => new Bin(g.Key, g.Sum(x => x.Long), g.Sum(x => x.Short), g.Max(x => x.LongTop), g.Max(x => x.ShortTop))).OrderBy(b => b.Price).ToList();
        double top = (double)ChartInfo.PriceChartContainer.High, bottom = (double)ChartInfo.PriceChartContainer.Low;
        var visible = grouped.Where(b => b.Price + step >= bottom && b.Price <= top).ToList();
        double max = visible.Count > 0 ? visible.Max(b => Math.Max(b.Long, b.Short)) : 1;
        int x0 = ChartInfo.GetXByBar(CurrentBar - 1) + Math.Max(4, ChartInfo.PriceChartContainer.BarsWidth) + 6;
        int w = Math.Clamp(Width, 30, 600);
        if (x0 + w > region.Right) x0 = Math.Max(region.Left, region.Right - w);
        double mark = (double)GetCandle(CurrentBar - 1).Close;
        foreach (var b in visible)
        {
            int y0 = ChartInfo.GetYByPrice((decimal)(b.Price + step), false), y1 = ChartInfo.GetYByPrice((decimal)b.Price, false);
            int h = Math.Max(1, Math.Abs(y1 - y0) - (Math.Abs(y1 - y0) > 3 ? 1 : 0));
            int y = Math.Min(y0, y1);
            foreach (var (usd, topPos, color) in new[] { (b.Long, b.LongTop, LongColor), (b.Short, b.ShortTop, ShortColor) })
            {
                if (usd <= 0) continue;
                double t = Math.Sqrt(usd / max);
                int len = Math.Max(1, (int)(w * t));
                context.FillRectangle(Color.FromArgb((int)(60 + 180 * t), color.R, color.G, color.B), new Rectangle(x0, y, len, h));
                // un rombo donde una sola posición tiene la mitad o más
                if (topPos >= usd / 2 && usd >= max * 0.15)
                    context.FillPolygon(Color.FromArgb(240, 250, 220, 120), new[] { new Point(x0 + len + 4, y + h / 2 - 3), new Point(x0 + len + 7, y + h / 2), new Point(x0 + len + 4, y + h / 2 + 3), new Point(x0 + len + 1, y + h / 2) });
                if (h >= 9 && t > 0.3) context.DrawString(FlowText.Usd(usd), font, Color.FromArgb(230, 240, 240, 245), x0 + 2, y + h / 2 - 6);
            }
        }

        if (Cumulative)
        {
            // totales desde el precio: largos que se liquidarían bajando hasta el borde de abajo, cortos subiendo hasta el de arriba
            double longs = visible.Where(b => b.Price < mark).Sum(b => b.Long), shorts = visible.Where(b => b.Price > mark).Sum(b => b.Short);
            string tot = string.Format(UiText.Tr("largos abajo {0} · cortos arriba {1}"), FlowText.Usd(longs), FlowText.Usd(shorts));
            context.DrawString(tot, font, FlowText.Dim, Math.Min(x0, region.Right - context.MeasureString(tot, font).Width - 4), region.Top + 4);
        }

        string age = ((int)(DateTime.UtcNow - _snap.AsOf).TotalMinutes).ToString(CultureInfo.InvariantCulture);
        string note = string.Format(UiText.Tr("posiciones de hace {0} min · {1}"), age, _snap.Attribution);
        context.DrawString(note, font, FlowText.Dim, Math.Min(x0, region.Right - context.MeasureString(note, font).Width - 4), region.Bottom - 14);
    }
}
