using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Data;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>Ayudas compartidas de los indicadores del libro de órdenes.</summary>
internal static class BookTools
{
    public static decimal Tick(Indicator ind) => ind.InstrumentInfo is { } i ? i.InstrumentTickSize > 0 ? i.InstrumentTickSize : i.TickSize : 1;

    /// <summary>Vela que contiene un instante (búsqueda binaria; -1 si es antes de la primera).</summary>
    public static int BarOf(Indicator ind, DateTime t)
    {
        int lo = 0, hi = ind.CurrentBar - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (ind.GetCandle(mid).Time <= t)
            {
                ans = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        return ans;
    }

    /// <summary>X de un instante (dentro de su vela, proporcional al tiempo).</summary>
    public static int XOf(Indicator ind, DateTime t)
    {
        var ci = ind.ChartInfo!;
        int bar = Math.Max(0, BarOf(ind, t));
        int x0 = ci.GetXByBar(bar, true);
        var len = ci.GetTimeFrameTypeChartPeriod();
        if (len <= TimeSpan.Zero) return x0;
        double f = Math.Clamp((t - ind.GetCandle(bar).Time).TotalSeconds / len.TotalSeconds, 0, 1);
        return x0 + (int)(f * Math.Max(1, ci.PriceChartContainer.BarsWidth));
    }

    /// <summary>Lo operado a cada precio (en ticks del instrumento) en una vela: (vendedores, compradores).</summary>
    public static Dictionary<long, (double Sell, double Buy)> Traded(Indicator ind, int bar, decimal tick)
    {
        var d = new Dictionary<long, (double, double)>();
        foreach (var l in ind.GetTickLevels(bar))
        {
            long p = (long)Math.Round(l.Price / tick);
            d.TryGetValue(p, out var v);
            d[p] = (v.Item1 + (double)l.Bid, v.Item2 + (double)l.Ask);
        }

        return d;
    }
}

/// <summary>
/// «Order Book Changes» como en Kiyotaka / openmarket: en una escalera a la derecha del gráfico, cuánto se AGREGÓ y cuánto
/// se RETIRÓ del libro en cada precio durante la ventana elegida (1 hora, 4 horas, 1 día, la sesión o lo visible), del lado
/// del bid y del ask. Lo que se fue operado (consumido por operaciones agresivas) se separa de lo retirado sin operarse.
/// Sirve para ver dónde se apilan órdenes, dónde se sacan antes de que llegue el precio (spoof) y compararlo con el DOM, el
/// CVD, la financiación y el interés abierto. Usa las fotos del libro del mapa de calor (hay datos desde que el gráfico
/// estuvo abierto en vivo).
/// </summary>
[DisplayName("Cambios del libro (agregado / retirado)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Escalera de Order Book Changes: por precio, lo agregado y lo retirado del libro (bid y ask) en la ventana elegida, separando lo operado. Para detectar apilamientos, retiros (spoof) y absorción.")]
public class OrderBookChanges : Indicator
{
    public enum ChangeWindow
    {
        [Display(Name = "Lo visible en el gráfico")] Visible,
        [Display(Name = "1 hora")] Hour1,
        [Display(Name = "4 horas")] Hour4,
        [Display(Name = "1 día")] Day1,
        [Display(Name = "La sesión (día) en curso")] Session,
        [Display(Name = "Horas a elección")] Custom,
    }

    public enum ChangeView
    {
        [Display(Name = "Agregado y retirado (dos columnas)")] Both,
        [Display(Name = "Neto (agregado − retirado)")] Net,
    }

    private readonly Dictionary<long, (int Samples, Dictionary<long, BookLevelChange> Levels)> _perBar = new();
    private readonly Action _tick;
    private ChangeWindow _window = ChangeWindow.Day1;

    public OrderBookChanges() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
        _tick = () => RedrawChart();
    }

    [Display(Name = "Ventana", GroupName = "Cálculo", Order = 10)]
    public ChangeWindow Window { get => _window; set { _window = value; RedrawChart(); } }

    [Display(Name = "Horas (ventana a elección)", GroupName = "Cálculo", Order = 20)]
    [Range(1, 720)]
    public int CustomHours { get; set; } = 24;

    [Display(Name = "Agrupar de a (ticks, 0 = automático)", GroupName = "Cálculo", Order = 30)]
    [Range(0, 1_000_000)]
    public int GroupTicks { get; set; }

    [Display(Name = "Filas aproximadas (automático)", GroupName = "Cálculo", Order = 35)]
    [Range(10, 200)]
    public int AutoRows { get; set; } = 40;

    [Display(Name = "Vista", GroupName = "Visualización", Order = 40)]
    public ChangeView View { get; set; } = ChangeView.Both;

    [Display(Name = "Mostrar lo operado (consumido)", GroupName = "Visualización", Order = 45)]
    public bool ShowFilled { get; set; } = true;

    [Display(Name = "Ancho de la escalera (px)", GroupName = "Visualización", Order = 50)]
    [Range(60, 600)]
    public int Width { get; set; } = 180;

    [Display(Name = "Números en cada fila", GroupName = "Visualización", Order = 55)]
    public bool ShowValues { get; set; } = true;

    [Display(Name = "Color agregado", GroupName = "Colores", Order = 60)]
    public Color AddedColor { get; set; } = Color.FromArgb(200, 38, 194, 129);

    [Display(Name = "Color retirado", GroupName = "Colores", Order = 61)]
    public Color PulledColor { get; set; } = Color.FromArgb(200, 240, 82, 90);

    [Display(Name = "Color operado", GroupName = "Colores", Order = 62)]
    public Color FilledColor { get; set; } = Color.FromArgb(160, 242, 181, 58);

    [Display(Name = "Fondo del lado bid", GroupName = "Colores", Order = 63)]
    public Color BidBack { get; set; } = Color.FromArgb(28, 61, 139, 253);

    [Display(Name = "Fondo del lado ask", GroupName = "Colores", Order = 64)]
    public Color AskBack { get; set; } = Color.FromArgb(28, 240, 82, 90);

    protected override void OnInitialize() => SubscribeToTimer(TimeSpan.FromSeconds(3), _tick);

    protected override void OnDispose() => UnsubscribeFromTimer(TimeSpan.FromSeconds(3), _tick);

    protected override void OnRecalculate() => _perBar.Clear();

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    /// <summary>Cambios de una vela (al tick), recordados mientras no lleguen fotos nuevas a esa vela.</summary>
    private Dictionary<long, BookLevelChange> BarChanges(int bar, decimal tick)
    {
        var samples = GetDepthSamples(bar);
        long key = GetCandle(bar).Time.Ticks;
        if (_perBar.TryGetValue(key, out var hit) && hit.Samples == samples.Count) return hit.Levels;
        DepthSample? prev = null;
        for (int b = bar - 1; b >= 0 && b >= bar - 50 && prev == null; b--)
        {
            var ps = GetDepthSamples(b);
            if (ps.Count > 0) prev = ps[^1];
        }

        var traded = BookTools.Traded(this, bar, tick);
        var levels = samples.Count == 0 ? new Dictionary<long, BookLevelChange>() : BookChanges.ForBar(samples, prev, p => traded.TryGetValue(p, out var v) ? v : (0, 0));
        _perBar[key] = (samples.Count, levels);
        return levels;
    }

    private (int From, int To) WindowBars()
    {
        int last = CurrentBar - 1;
        if (_window == ChangeWindow.Visible) return (Math.Max(0, FirstVisibleBarNumber), Math.Min(last, LastVisibleBarNumber));
        if (_window == ChangeWindow.Session)
        {
            int s = last;
            while (s > 0 && !IsNewSession(s)) s--;
            return (s, last);
        }

        var span = _window switch
        {
            ChangeWindow.Hour1 => TimeSpan.FromHours(1),
            ChangeWindow.Hour4 => TimeSpan.FromHours(4),
            ChangeWindow.Day1 => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(Math.Max(1, CustomHours)),
        };
        var from = GetCandle(last).Time - span;
        int b0 = Math.Max(0, BookTools.BarOf(this, from));
        return (b0, last);
    }

    private string WindowText() => _window switch
    {
        ChangeWindow.Visible => UiText.Tr("lo visible"),
        ChangeWindow.Hour1 => UiText.Tr("1 hora"),
        ChangeWindow.Hour4 => UiText.Tr("4 horas"),
        ChangeWindow.Day1 => UiText.Tr("1 día"),
        ChangeWindow.Session => UiText.Tr("la sesión"),
        _ => string.Format(UiText.Tr("{0} h"), CustomHours),
    };

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        var container = ChartInfo.PriceChartContainer;
        var region = container.Region;
        decimal tick = BookTools.Tick(this);
        var (b0, b1) = WindowBars();

        // suma de la ventana, al tick
        var total = new Dictionary<long, BookLevelChange>();
        int withData = 0;
        for (int b = b0; b <= b1; b++)
        {
            var lv = BarChanges(b, tick);
            if (lv.Count > 0) withData++;
            foreach (var (p, c) in lv)
            {
                if (!total.TryGetValue(p, out var t)) total[p] = t = new BookLevelChange();
                t.Add(c);
            }
        }

        var font = new RenderFont("Arial", 8);
        var dim = Color.FromArgb(200, 180, 186, 196);
        int x1 = region.Right - 4, x0 = x1 - Math.Clamp(Width, 60, 600);
        string title = string.Format(UiText.Tr("Libro · agregado / retirado · {0}"), WindowText());
        if (total.Count == 0)
        {
            context.DrawString(title + " · " + UiText.Tr("sin fotos del libro en la ventana (abrí el gráfico en vivo)"), font, dim, x0 - 120, region.Top + 4);
            return;
        }

        // filas: agrupadas para que se vean ~N en el rango visible
        decimal hiP = container.High, loP = container.Low;
        long group = GroupTicks > 0 ? GroupTicks : Math.Max(1, CandleSeries.NiceStep((long)Math.Ceiling((hiP - loP) / tick / Math.Max(10, AutoRows))));
        var rows = new SortedDictionary<long, BookLevelChange>();
        foreach (var (p, c) in total)
        {
            long g = (long)Math.Floor((double)p / group) * group;
            if (!rows.TryGetValue(g, out var r)) rows[g] = r = new BookLevelChange();
            r.Add(c);
        }

        double max = 1e-9;
        foreach (var r in rows.Values)
            max = View == ChangeView.Net
                ? Math.Max(max, Math.Abs(r.Added - r.Pulled))
                : Math.Max(max, Math.Max(r.Added, r.Pulled + (ShowFilled ? r.BidFilled + r.AskFilled : 0)));

        decimal mid = CurrentBar > 0 ? GetCandle(CurrentBar - 1).Close : 0;
        int colW = (x1 - x0) / 2;
        foreach (var (g, r) in rows)
        {
            decimal lo = g * tick, hi = (g + group) * tick;
            if (hi < loP || lo > hiP) continue;
            int yTop = ChartInfo.GetYByPrice(hi, false), yBot = ChartInfo.GetYByPrice(lo, false);
            int h = Math.Max(1, Math.Abs(yBot - yTop) - 1);
            int y = Math.Min(yTop, yBot);
            if (y < region.Top + 30) continue; // debajo del título
            bool askSide = lo >= mid;
            context.FillRectangle(askSide ? AskBack : BidBack, new Rectangle(x0, y, x1 - x0, h));
            if (View == ChangeView.Net)
            {
                double net = r.Added - r.Pulled;
                int w = (int)Math.Round(Math.Abs(net) / max * (x1 - x0));
                if (w > 0) context.FillRectangle(net >= 0 ? AddedColor : PulledColor, new Rectangle(x1 - w, y, w, h));
                if (ShowValues && h >= 9) context.DrawString(Fmt(net), font, dim, x0 + 2, y + Math.Max(0, (h - 10) / 2));
                continue;
            }

            // columna izquierda: agregado; derecha: retirado (+ operado)
            int wa = (int)Math.Round(r.Added / max * colW);
            if (wa > 0) context.FillRectangle(AddedColor, new Rectangle(x0 + colW - wa, y, wa, h));
            int wp = (int)Math.Round(r.Pulled / max * colW);
            if (wp > 0) context.FillRectangle(PulledColor, new Rectangle(x0 + colW, y, wp, h));
            if (ShowFilled)
            {
                int wf = (int)Math.Round((r.BidFilled + r.AskFilled) / max * colW);
                if (wf > 0) context.FillRectangle(FilledColor, new Rectangle(x0 + colW + wp, y, wf, h));
            }

            if (ShowValues && h >= 9)
            {
                context.DrawString("+" + Fmt(r.Added), font, dim, x0 + 2, y + Math.Max(0, (h - 10) / 2));
                var t = "−" + Fmt(r.Pulled);
                var size = context.MeasureString(t, font);
                context.DrawString(t, font, dim, x1 - size.Width - 2, y + Math.Max(0, (h - 10) / 2));
            }
        }

        if (View == ChangeView.Both) context.DrawLine(new RenderPen(Color.FromArgb(90, 200, 200, 200), 1), x0 + colW, region.Top + 30, x0 + colW, region.Bottom);
        // título y leyenda, alineados a la derecha (la escalera está pegada al eje de precio)
        string legend = UiText.Tr(View == ChangeView.Net ? "verde: más agregado · rojo: más retirado" : "verde agregado · rojo retirado · ámbar operado");
        var ts = context.MeasureString(title, font);
        var ls = context.MeasureString(legend, font);
        int boxW = Math.Max(ts.Width, ls.Width) + 8;
        context.FillRectangle(Color.FromArgb(170, 12, 14, 18), new Rectangle(x1 - boxW, region.Top + 2, boxW, 27));
        context.DrawString(title, font, dim, x1 - ts.Width - 4, region.Top + 4);
        context.DrawString(legend, font, dim, x1 - ls.Width - 4, region.Top + 16);
    }

    private static string Fmt(double v)
    {
        double a = Math.Abs(v);
        string s = a >= 1_000_000 ? (v / 1_000_000).ToString("0.#") + "M" : a >= 1000 ? (v / 1000).ToString("0.#") + "K" : a >= 10 ? v.ToString("0") : v.ToString("0.##");
        return s;
    }
}

/// <summary>
/// Las órdenes grandes del libro dibujadas donde estuvieron (un trazo por orden, de cuando apareció a cuando se fue), como
/// las marcas de órdenes límite del mapa de calor de Kiyotaka: las que se retiraron sin operarse en poco tiempo se marcan
/// como posible spoof (S) y las que se corrieron siguiendo al precio como chasing (C, con una flecha a donde se fueron).
/// </summary>
[DisplayName("Órdenes grandes del libro (spoof / chasing)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Órdenes límite grandes del libro: cuándo aparecen y cuánto duran; marca las retiradas sin operarse (posible spoof) y las que persiguen al precio (chasing). Usa las fotos del libro del mapa de calor.")]
public class LargeBookOrders : Indicator
{
    private readonly Action _tick;
    private List<LargeOrder> _orders = new();
    private (int First, int Last, int Samples, double Min) _key;
    private double _autoMin;
    private DateTime _lastUpdate;

    public LargeBookOrders() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
        _tick = () => RedrawChart();
    }

    [Display(Name = "Tamaño mínimo (0 = automático)", GroupName = "Cálculo", Order = 10)]
    [Range(0, 1_000_000_000)]
    public decimal MinSize { get; set; }

    [Display(Name = "Automático: veces el tamaño típico de un nivel", GroupName = "Cálculo", Order = 15)]
    [Range(2, 200)]
    public int AutoMultiple { get; set; } = 8;

    [Display(Name = "Spoof: retirada antes de (segundos)", GroupName = "Cálculo", Order = 20)]
    [Range(3, 3600)]
    public int SpoofSeconds { get; set; } = 60;

    [Display(Name = "Chasing: distancia máxima (ticks)", GroupName = "Cálculo", Order = 25)]
    [Range(1, 10000)]
    public int ChaseTicks { get; set; } = 50;

    [Display(Name = "Mostrar también las que se operaron", GroupName = "Visualización", Order = 30)]
    public bool ShowFilled { get; set; } = true;

    [Display(Name = "Grosor", GroupName = "Visualización", Order = 35)]
    [Range(1, 8)]
    public int LineWidth { get; set; } = 2;

    [Display(Name = "Color bid", GroupName = "Colores", Order = 40)]
    public Color BidColor { get; set; } = Color.FromArgb(220, 61, 139, 253);

    [Display(Name = "Color ask", GroupName = "Colores", Order = 41)]
    public Color AskColor { get; set; } = Color.FromArgb(220, 240, 82, 90);

    [Display(Name = "Color spoof", GroupName = "Colores", Order = 42)]
    public Color SpoofColor { get; set; } = Color.FromArgb(255, 242, 181, 58);

    [Display(Name = "Color chasing", GroupName = "Colores", Order = 43)]
    public Color ChaseColor { get; set; } = Color.FromArgb(255, 180, 120, 255);

    protected override void OnInitialize() => SubscribeToTimer(TimeSpan.FromSeconds(3), _tick);

    protected override void OnDispose() => UnsubscribeFromTimer(TimeSpan.FromSeconds(3), _tick);

    protected override void OnRecalculate()
    {
        _orders = new List<LargeOrder>();
        _key = default;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    private void Update()
    {
        // se rearma como mucho una vez por segundo (el dibujo corre muchas veces por segundo)
        if ((DateTime.UtcNow - _lastUpdate).TotalSeconds < 1 && _orders.Count > 0) return;
        _lastUpdate = DateTime.UtcNow;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        var samples = new List<DepthSample>();
        for (int b = first; b <= last; b++) samples.AddRange(GetDepthSamples(b));
        double min = (double)MinSize;
        if (min <= 0)
        {
            // automático: N veces la mediana de los tamaños de la última foto
            var latest = samples.Count > 0 ? samples[^1] : null;
            if (latest != null)
            {
                var sizes = latest.BidSizes.Concat(latest.AskSizes).Where(v => v > 0).OrderBy(v => v).ToList();
                if (sizes.Count > 0) _autoMin = sizes[sizes.Count / 2] * Math.Max(2, AutoMultiple);
            }

            min = _autoMin;
        }

        var key = (first, last, samples.Count, min);
        if (key == _key || min <= 0) return;
        _key = key;
        decimal tick = BookTools.Tick(this);
        var tradedCache = new Dictionary<int, Dictionary<long, (double Sell, double Buy)>>();
        double TradedAt(long p, DateTime from, DateTime to)
        {
            double sum = 0;
            int b0 = Math.Max(0, BookTools.BarOf(this, from)), b1 = Math.Max(b0, BookTools.BarOf(this, to));
            for (int b = b0; b <= b1 && b < CurrentBar; b++)
            {
                if (!tradedCache.TryGetValue(b, out var t)) tradedCache[b] = t = BookTools.Traded(this, b, tick);
                if (t.TryGetValue(p, out var v)) sum += v.Sell + v.Buy;
            }

            return sum;
        }

        _orders = BookChanges.TrackLarge(samples, min, TradedAt, ChaseTicks);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        Update();
        decimal tick = BookTools.Tick(this);
        var font = new RenderFont("Arial", 7);
        var region = ChartInfo.PriceChartContainer.Region;
        foreach (var o in _orders)
        {
            bool spoof = o.Pulled && o.MovedTo == null && (o.To - o.From).TotalSeconds <= SpoofSeconds;
            bool chase = o.MovedTo != null;
            if (!ShowFilled && !o.Open && !o.Pulled) continue;
            int x0 = BookTools.XOf(this, o.From), x1 = Math.Max(x0 + 3, BookTools.XOf(this, o.To));
            if (x1 < region.Left || x0 > region.Right) continue;
            int y = ChartInfo.GetYByPrice(o.Price * tick, false);
            var color = spoof ? SpoofColor : chase ? ChaseColor : o.IsBid ? BidColor : AskColor;
            context.DrawLine(new RenderPen(color, Math.Clamp(LineWidth, 1, 8)), x0, y, x1, y);
            if (spoof) context.DrawString("S", font, color, x1 + 2, y - 5);
            if (chase && o.MovedTo is { } to)
            {
                int y2 = ChartInfo.GetYByPrice(to * tick, false);
                context.DrawLine(new RenderPen(color, 1, Rendering.Settings.PenSettings.ToDash(LineDashStyle.Dot)), x1, y, x1 + 6, y2);
                context.DrawString("C", font, color, x1 + 2, y - 5);
            }
        }
    }
}
