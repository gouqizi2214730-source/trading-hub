using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Feeds.External;
using TradingHub.Indicators.Native.Flow;
using TradingHub.Indicators.Native.Structure;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Options;

// Opciones con las operaciones (flujo): footprint y mapa de primas por ejercicio, opciones por precio, VWAP del flujo,
// órdenes grandes, cinta, flujo del intervalo, estadísticas, cuota por mercado, primas por vencimiento y los paneles de
// NOPE, lado agresor, flujo neto, volumen, flujo de griegas, sentimiento y marea de primas. Una operación es alcista si
// compra calls o vende puts, y bajista si compra puts o vende calls. Las velas pasadas no cambian: no se redibuja.

// ============================================================================ footprint por ejercicio

[DisplayName("Footprint de opciones por ejercicio")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Vela por vela, las primas de opciones operadas en cada ejercicio, dibujadas en el precio del ejercicio: verde si el flujo fue alcista (compra de calls o venta de puts), rojo si fue bajista, más fuerte cuanto más prima. Muestra los ejercicios a los que los operadores vuelven una y otra vez.")]
public class StrikeFootprint : OptionsScene
{
    private readonly List<(int Bar, double Strike, double Premium, double Bias)> _cells = new();
    private double _max, _step;
    private bool _loading;

    [Display(Name = "Ejercicios por vela (0 = todos)", GroupName = "Cálculo", Order = 10)] [Range(0, 100)] public int TopStrikes { get; set; } = 4;
    [Display(Name = "Velas que se dibujan", GroupName = "Cálculo", Order = 11)] [Range(20, 3000)] public int MaxDrawnBars { get; set; } = 400;
    [Display(Name = "Distancia máxima del precio (%)", GroupName = "Cálculo", Order = 12)] [Range(1, 100)] public decimal MaxDistancePct { get; set; } = 15;
    [Display(Name = "Como mapa de calor", GroupName = "Dibujo", Order = 20)] public bool Heatmap { get; set; }
    [Display(Name = "Intensidad", GroupName = "Dibujo", Order = 21)] [Range(10, 255)] public int Alpha { get; set; } = 200;

    public StrikeFootprint() => ShowDashboard = false;

    protected override void BuildOptions(Scene s)
    {
        _cells.Clear();
        _max = 0;
        int first = Math.Max(0, LastBar - MaxDrawnBars);
        _loading = !TradesReady(TimeOf(first));
        var strikes = new SortedSet<double>();
        for (int i = first; i <= LastBar; i++)
        {
            var tr = TradesOf(i);
            if (tr.Count == 0) continue;
            double px = Bars[i].C;
            var by = tr.Where(t => Math.Abs(t.Strike / px - 1) * 100 <= (double)MaxDistancePct).GroupBy(t => t.Strike)
                .Select(g => (Strike: g.Key, Premium: g.Sum(t => t.Premium), Bias: g.Sum(t => t.Bias * t.Premium)))
                .OrderByDescending(x => x.Premium);
            foreach (var c in TopStrikes > 0 ? by.Take(TopStrikes) : by)
            {
                _cells.Add((i, c.Strike, c.Premium, c.Bias));
                _max = Math.Max(_max, c.Premium);
                strikes.Add(c.Strike);
            }
        }

        // la escala con el percentil 90 (una operación enorme no deja todo lo demás apagado)
        if (_cells.Count > 10) _max = _cells.Select(c => c.Premium).OrderBy(v => v).ElementAt((int)(_cells.Count * 0.9));
        var list = strikes.ToList();
        _step = list.Count > 1 ? list.Zip(list.Skip(1), (a, b) => b - a).Where(d => d > 0).DefaultIfEmpty(Bars[LastBar].C * 0.01).Min() : Bars[LastBar].C * 0.01;
    }

    protected override void RenderOptions(RenderContext ctx, Rectangle region)
    {
        var font = new RenderFont("Arial", 7);
        if (_loading) ctx.DrawString(UiText.Tr("Trayendo las operaciones de opciones…"), font, Opt.Dim, region.Left + 8, region.Top + 26);
        if (_max <= 0 || ChartInfo == null) return;
        int first = FirstVisibleBarNumber - Offset, last = LastVisibleBarNumber - Offset;
        int bw = ChartInfo.PriceChartContainer.BarsWidth;
        foreach (var c in _cells)
        {
            if (c.Bar < first || c.Bar > last) continue;
            double f = Math.Min(1, Math.Sqrt(c.Premium / _max));
            int x0 = X(c.Bar) - (Heatmap ? 0 : 0), x1 = X(c.Bar + 1);
            int y0 = Y(c.Strike + _step / 2), y1 = Y(c.Strike - _step / 2);
            var col = c.Bias > 0 ? Opt.Call : c.Bias < 0 ? Opt.Put : Pal.Muted;
            var rect = Heatmap
                ? new Rectangle(x0, Math.Min(y0, y1), Math.Max(1, x1 - x0), Math.Max(1, Math.Abs(y1 - y0)))
                : new Rectangle(x0, Y(c.Strike) - 5, Math.Max(3, x1 - x0 - 1), 10);
            ctx.FillRectangle(Pal.A(col, (int)(Alpha * Math.Max(0.3, f))), rect);
            if (!Heatmap && bw >= 34) ctx.DrawString(FlowText.Usd(c.Premium).Replace("$", ""), font, Color.White, rect.Left + 1, rect.Top - 1);
        }
    }
}

[DisplayName("Primas por ejercicio")]
[Category(IndicatorCategories.Options)]
[Description("Un mapa de calor de las primas de opciones de cada ejercicio vela por vela (todos los ejercicios cerca del precio): verde donde el flujo fue alcista, rojo donde fue bajista. Muestra los precios que los operadores defienden o apuntan.")]
public class PremiumByStrike : StrikeFootprint
{
    public PremiumByStrike()
    {
        TopStrikes = 0;
        Heatmap = true;
        Alpha = 170;
    }
}

// ============================================================================ opciones por precio

[DisplayName("Opciones por precio")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("Un perfil con las primas de opciones apiladas en el precio que tenía el subyacente cuando se operaron (de 1 a 30 días): muestra dónde apuestan más fuerte los operadores. Verde la parte alcista, rojo la bajista; una línea en el precio con más prima.")]
public class OptionsAtPrice : OptionsScene
{
    private double[] _bull = Array.Empty<double>(), _bear = Array.Empty<double>();
    private double _low, _row;
    private int _days = 3;

    [Display(Name = "Días", GroupName = "Cálculo", Order = 10)]
    [Range(1, 30)]
    public int Days
    {
        get => _days;
        set
        {
            _days = Math.Clamp(value, 1, 30);
            OptionsData.MaxTradeDays = Math.Max(OptionsData.MaxTradeDays, _days);
            RecalculateValues();
        }
    }

    [Display(Name = "Filas", GroupName = "Cálculo", Order = 11)] [Range(10, 400)] public int Rows { get; set; } = 80;
    [Display(Name = "Ancho del perfil (px)", GroupName = "Dibujo", Order = 20)] [Range(40, 600)] public int Width { get; set; } = 150;

    protected override void BuildOptions(Scene s)
    {
        OptionsData.MaxTradeDays = Math.Max(OptionsData.MaxTradeDays, _days);
        var now = EndOf(LastBar);
        var from = now.AddDays(-_days);
        var tr = TradesBetween(from, now).Where(t => t.Spot > 0).ToList();
        if (tr.Count == 0)
        {
            Status = TradesReady(from) ? "No hubo operaciones de opciones en estos días." : "Trayendo las operaciones de opciones…";
            return;
        }

        double lo = tr.Min(t => t.Spot), hi = tr.Max(t => t.Spot);
        _row = Math.Max((hi - lo) / Rows, Bars[LastBar].C * 0.0002);
        _low = lo;
        int n = (int)Math.Ceiling((hi - lo) / _row) + 1;
        _bull = new double[n];
        _bear = new double[n];
        foreach (var t in tr)
        {
            int k = Math.Clamp((int)((t.Spot - lo) / _row), 0, n - 1);
            if (t.Bias >= 0) _bull[k] += t.Premium;
            else _bear[k] += t.Premium;
        }

        int poc = Enumerable.Range(0, n).OrderByDescending(k => _bull[k] + _bear[k]).First();
        double pocPx = _low + (poc + 0.5) * _row;
        s.HLine(Math.Max(0, BarOf(from)), -1, pocPx, Opt.Flip, 1, LineDashStyle.Dash, "Más prima " + Price(pocPx), "Precio con más prima de opciones");
        s.DashTitle = UiText.Tr("Opciones por precio") + $" · {_days} d";
        s.Row("Prima alcista", Opt.Usd(_bull.Sum()), Opt.Call);
        s.Row("Prima bajista", Opt.Usd(_bear.Sum()), Opt.Put);
        s.Row("Más prima en", Price(pocPx), Opt.Flip);
    }

    protected override void RenderOptions(RenderContext ctx, Rectangle region)
    {
        if (_bull.Length == 0) return;
        double max = Enumerable.Range(0, _bull.Length).Max(k => _bull[k] + _bear[k]);
        if (max <= 0) return;
        int right = region.Right - 4;
        for (int k = 0; k < _bull.Length; k++)
        {
            double tot = _bull[k] + _bear[k];
            if (tot <= 0) continue;
            int y0 = Y(_low + (k + 1) * _row), y1 = Y(_low + k * _row);
            int wb = (int)(Width * _bull[k] / max), wr = (int)(Width * _bear[k] / max);
            int top = Math.Min(y0, y1), h = Math.Max(1, Math.Abs(y1 - y0) - 1);
            if (wb > 0) ctx.FillRectangle(Pal.A(Opt.Call, 170), new Rectangle(right - wb, top, wb, h));
            if (wr > 0) ctx.FillRectangle(Pal.A(Opt.Put, 170), new Rectangle(right - wb - wr, top, wr, h));
        }
    }
}

// ============================================================================ VWAP del flujo

[DisplayName("VWAP del flujo de opciones")]
[Category(IndicatorCategories.Vwap)]
[Description("Un precio promedio del día ponderado por las primas de opciones (no por el volumen): el precio del subyacente al que entraron los compradores de opciones. Opcionalmente, uno para el flujo alcista y otro para el bajista. Se reinicia cada día y cada punto usa solo lo operado hasta esa vela.")]
public class FlowVwap : OptionsScene
{
    [Display(Name = "Flujo alcista y bajista por separado", GroupName = "Dibujo", Order = 20)] public bool SplitSides { get; set; } = true;
    [Display(Name = "Días que se dibujan", GroupName = "Dibujo", Order = 21)] [Range(1, 30)] public int Days { get; set; } = 3;
    [Display(Name = "Color", GroupName = "Dibujo", Order = 22)] public Color LineColor { get; set; } = Color.FromArgb(255, 251, 191, 36);

    public FlowVwap() => ShowDashboard = false;

    protected override void BuildOptions(Scene s)
    {
        int first = LastBar, d = 0;
        while (first > 0 && d < Days)
        {
            first = DayStart(first) - 1;
            d++;
        }

        first = Math.Max(0, first + 1);
        List<(int, double)>? all = null, bull = null, bear = null;
        double sp = 0, sw = 0, bp = 0, bw = 0, rp = 0, rw = 0;
        for (int i = first; i <= LastBar; i++)
        {
            if (i == first || DayKey[i] != DayKey[i - 1])
            {
                Flush();
                all = new();
                bull = new();
                bear = new();
                sp = sw = bp = bw = rp = rw = 0;
            }

            foreach (var t in TradesOf(i))
            {
                double spot = t.Spot > 0 ? t.Spot : Bars[i].C;
                sp += spot * t.Premium;
                sw += t.Premium;
                if (t.Bias > 0)
                {
                    bp += spot * t.Premium;
                    bw += t.Premium;
                }
                else if (t.Bias < 0)
                {
                    rp += spot * t.Premium;
                    rw += t.Premium;
                }
            }

            if (sw > 0) all!.Add((i, sp / sw));
            if (bw > 0) bull!.Add((i, bp / bw));
            if (rw > 0) bear!.Add((i, rp / rw));
        }

        Flush();
        if (!TradesReady(TimeOf(first)) && s.Paths.Count == 0) Status = "Trayendo las operaciones de opciones…";

        void Flush()
        {
            if (all is { Count: > 1 }) s.Paths.Add(new Scene.PathShape(all, LineColor, 2));
            if (SplitSides && bull is { Count: > 1 }) s.Paths.Add(new Scene.PathShape(bull, Pal.A(Opt.Call, 200), 1, LineDashStyle.Dash));
            if (SplitSides && bear is { Count: > 1 }) s.Paths.Add(new Scene.PathShape(bear, Pal.A(Opt.Put, 200), 1, LineDashStyle.Dash));
        }
    }
}

// ============================================================================ órdenes grandes

[DisplayName("Marcas de órdenes grandes de opciones")]
[Category(IndicatorCategories.Options)]
[Description("Marca sobre las velas los precios donde entraron operaciones grandes de opciones (desde la prima que digas): el tamaño del círculo sigue a la prima y el color al lado (verde alcista, rojo bajista).")]
public class FlowMarks : OptionsScene
{
    protected readonly List<OptionTrade> Big = new();

    [Display(Name = "Prima mínima (USD)", GroupName = "Cálculo", Order = 10)] [Range(1000, 1e10)] public decimal MinPremium { get; set; } = 100_000;
    [Display(Name = "Por vela como mucho", GroupName = "Cálculo", Order = 11)] [Range(1, 20)] public int PerBar { get; set; } = 3;
    [Display(Name = "Velas que se miran", GroupName = "Cálculo", Order = 12)] [Range(20, 3000)] public int Lookback { get; set; } = 500;
    [Display(Name = "Con texto", GroupName = "Dibujo", Order = 20)] public bool ShowText { get; set; } = true;

    public FlowMarks() => ShowDashboard = false;

    protected override void BuildOptions(Scene s)
    {
        Big.Clear();
        int first = Math.Max(0, LastBar - Lookback);
        double min = (double)MinPremium;
        double top = 0;
        var marks = new List<(int Bar, OptionTrade T)>();
        for (int i = first; i <= LastBar; i++)
            foreach (var t in TradesOf(i).Where(t => t.Premium >= min).OrderByDescending(t => t.Premium).Take(PerBar))
            {
                marks.Add((i, t));
                Big.Add(t);
                top = Math.Max(top, t.Premium);
            }

        foreach (var (bar, t) in marks)
        {
            int size = (int)Math.Clamp(4 + 10 * Math.Sqrt(t.Premium / Math.Max(1, top)), 4, 14);
            var col = Pal.A(t.Bias > 0 ? Opt.Call : t.Bias < 0 ? Opt.Put : Pal.Muted, 190);
            string? text = ShowText ? $"{FlowText.Usd(t.Premium)} {Opt.Contract(t.Right, t.Strike, t.Expiry)}" : null;
            s.Marks.Add(new Scene.MarkShape(bar, t.Spot > 0 ? t.Spot : Bars[bar].C, MarkKind.Circle, col, text, t.Bias >= 0, size));
        }

        if (marks.Count == 0 && !TradesReady(TimeOf(first))) Status = "Trayendo las operaciones de opciones…";
    }
}

[DisplayName("Operaciones grandes de opciones")]
[Category(IndicatorCategories.Options)]
[Description("Las operaciones grandes de opciones del subyacente con marcas en el gráfico (como «Marcas de órdenes grandes») y un cuadro con las últimas: hora, contrato, lado y prima. Muestra dónde apuesta el dinero grande.")]
public class OptionPrints : FlowMarks
{
    [Display(Name = "Filas del cuadro", GroupName = "Tablero", Order = 903)] [Range(1, 40)] public int TableRows { get; set; } = 10;

    public OptionPrints()
    {
        ShowDashboard = true;
        ShowText = false;
    }

    protected override void BuildOptions(Scene s)
    {
        base.BuildOptions(s);
        if (Big.Count == 0) return;
        s.DashTitle = UiText.Tr("Operaciones grandes") + " · " + Underlying;
        foreach (var t in Big.OrderByDescending(t => t.Time).Take(TableRows))
            s.Row(t.Time.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture) + "  " + Opt.Contract(t.Right, t.Strike, t.Expiry) + "  " + UiText.Tr(t.Aggressor > 0 ? "compra" : t.Aggressor < 0 ? "venta" : "—"),
                FlowText.Usd(t.Premium), Opt.BiasColor(t.Bias));
    }
}

// ============================================================================ cinta de opciones

[DisplayName("Cinta de opciones")]
[Category(IndicatorCategories.Options)]
[Description("Las órdenes de opciones más recientes primero, con hora, contrato, lado, cantidad y prima. Marca los barridos (varias ejecuciones del mismo contrato y del mismo lado en un segundo) para ver apuros o apuestas raras.")]
public class FlowTape : OptionsScene
{
    [Display(Name = "Prima mínima (USD)", GroupName = "Cálculo", Order = 10)] [Range(0, 1e10)] public decimal MinPremium { get; set; } = 10_000;
    [Display(Name = "Filas", GroupName = "Cálculo", Order = 11)] [Range(3, 60)] public int RowsShown { get; set; } = 15;
    [Display(Name = "Barrido desde (ejecuciones en 1 s)", GroupName = "Cálculo", Order = 12)] [Range(2, 50)] public int SweepFills { get; set; } = 3;

    protected override void BuildOptions(Scene s)
    {
        var now = EndOf(LastBar);
        var from = now.AddHours(-12);
        var tr = TradesBetween(from, now);
        if (tr.Count == 0)
        {
            Status = TradesReady(from) ? "Sin operaciones de opciones en las últimas 12 horas." : "Trayendo las operaciones de opciones…";
            return;
        }

        // las órdenes: ejecuciones del mismo contrato y lado dentro de un segundo se juntan
        var orders = new List<(DateTime T, OptionTrade First, double Size, double Premium, int Fills)>();
        foreach (var t in tr)
        {
            if (orders.Count > 0 && orders[^1].First.Symbol == t.Symbol && orders[^1].First.Aggressor == t.Aggressor && (t.Time - orders[^1].T).TotalSeconds <= 1)
            {
                var o = orders[^1];
                orders[^1] = (t.Time, o.First, o.Size + t.Size, o.Premium + t.Premium, o.Fills + 1);
            }
            else orders.Add((t.Time, t, t.Size, t.Premium, 1));
        }

        s.DashTitle = UiText.Tr("Cinta de opciones") + " · " + Underlying;
        foreach (var o in orders.Where(o => o.Premium >= (double)MinPremium).OrderByDescending(o => o.T).Take(RowsShown))
        {
            string sweep = o.Fills >= SweepFills ? " ⚡" + UiText.Tr("barrido") : "";
            s.Row(o.T.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + Opt.Contract(o.First.Right, o.First.Strike, o.First.Expiry) + "  " +
                  UiText.Tr(o.First.Aggressor > 0 ? "compra" : o.First.Aggressor < 0 ? "venta" : "—") + " " + Opt.N(o.Size, "0.##") + sweep,
                FlowText.Usd(o.Premium), Opt.BiasColor(o.First.Bias));
        }
    }
}

// ============================================================================ flujo del intervalo

[DisplayName("Flujo del intervalo")]
[Category(IndicatorCategories.Options)]
[Description("Un cuadro con los contratos de opciones que más prima juntaron en la última vela cerrada, ordenados por prima, con el lado que dominó.")]
public class IntervalFlow : OptionsScene
{
    [Display(Name = "Contratos", GroupName = "Cálculo", Order = 10)] [Range(1, 40)] public int Count { get; set; } = 10;

    protected override void BuildOptions(Scene s)
    {
        int bar = Math.Max(0, LastClosed);
        var tr = TradesOf(bar);
        s.DashTitle = UiText.Tr("Flujo de la vela de las") + " " + TimeOf(bar).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        if (tr.Count == 0)
        {
            s.Row(TradesReady(TimeOf(bar)) ? "Sin operaciones de opciones" : "Trayendo las operaciones…", "", default);
            return;
        }

        foreach (var g in tr.GroupBy(t => t.Symbol).Select(g => (First: g.First(), Premium: g.Sum(t => t.Premium), Bias: g.Sum(t => t.Bias * t.Premium))).OrderByDescending(x => x.Premium).Take(Count))
            s.Row(Opt.Contract(g.First.Right, g.First.Strike, g.First.Expiry) + "  " + UiText.Tr(g.Bias > 0 ? "alcista" : g.Bias < 0 ? "bajista" : "—"), FlowText.Usd(g.Premium), Opt.BiasColor(g.Bias));
        s.Row("Total de la vela", FlowText.Usd(tr.Sum(t => t.Premium)), default);
    }
}

// ============================================================================ estadísticas

internal static class FlowStats
{
    public static (double CallPrem, double PutPrem, double Bull, double Bear, double Bought, double Sold, int Count) Of(IEnumerable<OptionTrade> trades)
    {
        double cp = 0, pp = 0, bu = 0, be = 0, bo = 0, so = 0;
        int n = 0;
        foreach (var t in trades)
        {
            n++;
            if (t.Right == OptionRight.Call) cp += t.Premium;
            else pp += t.Premium;
            if (t.Bias > 0) bu += t.Premium;
            else if (t.Bias < 0) be += t.Premium;
            if (t.Aggressor > 0) bo += t.Premium;
            else if (t.Aggressor < 0) so += t.Premium;
        }

        return (cp, pp, bu, be, bo, so, n);
    }
}

[DisplayName("Estadísticas de opciones")]
[Category(IndicatorCategories.Options)]
[Description("Un cuadro para ver de un vistazo si hoy mandan las calls o las puts: primas de cada lado, cuánto fue comprado y vendido, la relación puts/calls por prima y el sesgo del flujo.")]
public class OptionsStats : OptionsScene
{
    protected override void BuildOptions(Scene s)
    {
        var st = FlowStats.Of(TodayTrades());
        s.DashTitle = UiText.Tr("Opciones de hoy") + " · " + Underlying;
        if (st.Count == 0)
        {
            s.Row(TradesReady(TimeOf(DayStart(LastBar))) ? "Sin operaciones de opciones hoy" : "Trayendo las operaciones…", "", default);
            return;
        }

        bool calls = st.CallPrem >= st.PutPrem;
        s.Row("Mandan", calls ? "las calls" : "las puts", calls ? Opt.Call : Opt.Put);
        s.Row("Prima de calls", Opt.Usd(st.CallPrem), Opt.Call);
        s.Row("Prima de puts", Opt.Usd(st.PutPrem), Opt.Put);
        s.Row("Puts / calls (prima)", st.CallPrem > 0 ? Opt.N(st.PutPrem / st.CallPrem, "0.00") : "—", default);
        s.Row("Comprado / vendido", Opt.Pct(st.Bought / Math.Max(1, st.Bought + st.Sold) * 100, "0") + " / " + Opt.Pct(st.Sold / Math.Max(1, st.Bought + st.Sold) * 100, "0"), default);
        s.Row("Flujo neto (alcista − bajista)", Opt.SignedUsd(st.Bull - st.Bear), Opt.BiasColor(st.Bull - st.Bear));
        s.Row("Operaciones", st.Count.ToString(CultureInfo.InvariantCulture), default);
    }
}

[DisplayName("Estadísticas del mercado de opciones")]
[Category(IndicatorCategories.Options)]
[Description("Hoy en todo el mercado de opciones de los subyacentes que elijas (BTC y ETH por defecto): para cada uno, si se compran más calls o más puts, la prima y el sesgo del flujo, y el total.")]
public class OptionsMarketStats : OptionsScene
{
    [Display(Name = "Subyacentes", GroupName = "Cálculo", Order = 10)] public string Underlyings { get; set; } = "BTC, ETH";

    protected override void BuildOptions(Scene s)
    {
        var from = TimeOf(DayStart(LastBar));
        s.DashTitle = UiText.Tr("Mercado de opciones hoy");
        double bull = 0, bear = 0;
        foreach (var u in Underlyings.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToUpperInvariant()).Distinct())
        {
            if (OptionsData.Unavailable(u) is { } why)
            {
                s.Row(u, "sin datos", Pal.Muted);
                continue;
            }

            var st = FlowStats.Of(OptionsData.TradesBetween(u, from, EndOf(LastBar)));
            bull += st.Bull;
            bear += st.Bear;
            s.Row(u + " · " + UiText.Tr(st.CallPrem >= st.PutPrem ? "más calls" : "más puts"), Opt.Usd(st.CallPrem + st.PutPrem) + "  " + Opt.SignedUsd(st.Bull - st.Bear), Opt.BiasColor(st.Bull - st.Bear));
        }

        s.Row("Total: alcista / bajista", Opt.Usd(bull) + " / " + Opt.Usd(bear), Opt.BiasColor(bull - bear));
    }
}

[DisplayName("Cuota de los mercados de opciones")]
[Category(IndicatorCategories.Options)]
[Description("Qué parte del volumen de opciones de hoy pasó por cada mercado (cuando el proveedor dice dónde se ejecutó cada operación; en EE.UU., las distintas bolsas de opciones).")]
public class MarketShare : OptionsScene
{
    [Display(Name = "Por prima (si no, por contratos)", GroupName = "Cálculo", Order = 10)] public bool ByPremium { get; set; } = true;

    protected override void BuildOptions(Scene s)
    {
        var tr = TodayTrades();
        s.DashTitle = UiText.Tr("Cuota por mercado") + " · " + Underlying;
        if (tr.Count == 0)
        {
            s.Row(TradesReady(TimeOf(DayStart(LastBar))) ? "Sin operaciones hoy" : "Trayendo las operaciones…", "", default);
            return;
        }

        double total = tr.Sum(t => ByPremium ? t.Premium : t.Size);
        foreach (var g in tr.GroupBy(t => string.IsNullOrEmpty(t.Venue) ? "?" : t.Venue).Select(g => (g.Key, V: g.Sum(t => ByPremium ? t.Premium : t.Size))).OrderByDescending(x => x.V))
            s.Row(g.Key == "?" ? "sin mercado" : g.Key, Opt.Pct(g.V / Math.Max(1e-9, total) * 100), default);
    }
}

[DisplayName("Primas por vencimiento")]
[Category(IndicatorCategories.Options)]
[Description("Las primas de opciones de hoy en cada vencimiento (calls y puts), para ver cuándo esperan los operadores el movimiento.")]
public class PremiumByExpiry : OptionsScene
{
    private List<(DateTime Expiry, double Call, double Put)> _rows = new();

    [Display(Name = "Vencimientos", GroupName = "Cálculo", Order = 10)] [Range(1, 30)] public int Count { get; set; } = 10;

    protected override void BuildOptions(Scene s)
    {
        var tr = TodayTrades();
        _rows = tr.GroupBy(t => t.Expiry).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Where(t => t.Right == OptionRight.Call).Sum(t => t.Premium), g.Where(t => t.Right == OptionRight.Put).Sum(t => t.Premium)))
            .OrderByDescending(r => r.Item2 + r.Item3).Take(Count).OrderBy(r => r.Key).ToList();
        if (_rows.Count == 0) Status = TradesReady(TimeOf(DayStart(LastBar))) ? "Sin operaciones de opciones hoy." : "Trayendo las operaciones de opciones…";
    }

    protected override void RenderOptions(RenderContext ctx, Rectangle region)
    {
        if (_rows.Count == 0 || !ShowDashboard) return;
        var font = new RenderFont("Arial", 8);
        var bold = new RenderFont("Arial", 9, FontStyle.Bold);
        int rh = 17, bw = 150, lw = 70, vw = 60, w = lw + bw + vw + 20, h = rh * _rows.Count + 30;
        int col = (int)DashboardPosition % 3, row = (int)DashboardPosition / 3;
        int x = col switch { 0 => region.Left + 8, 1 => region.Left + (region.Width - w) / 2, _ => region.Right - w - 8 };
        int y = row switch { 0 => region.Top + 26, 1 => region.Top + (region.Height - h) / 2, _ => region.Bottom - h - 8 };
        ctx.FillRectangle(Color.FromArgb(225, 18, 20, 26), new Rectangle(x, y, w, h), 5);
        ctx.DrawString(UiText.Tr("Primas de hoy por vencimiento") + " · " + Underlying, bold, Opt.Text, x + 8, y + 5);
        double max = _rows.Max(r => r.Call + r.Put);
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            int ry = y + 24 + i * rh;
            ctx.DrawString(r.Expiry.ToString("dd/MM/yy", CultureInfo.InvariantCulture), font, Opt.Dim, x + 8, ry);
            int wc = (int)(bw * r.Call / max), wp = (int)(bw * r.Put / max);
            ctx.FillRectangle(Pal.A(Opt.Call, 190), new Rectangle(x + lw, ry + 2, Math.Max(0, wc), rh - 6));
            ctx.FillRectangle(Pal.A(Opt.Put, 190), new Rectangle(x + lw + wc, ry + 2, Math.Max(0, wp), rh - 6));
            ctx.DrawString(FlowText.Usd(r.Call + r.Put), font, Opt.Text, x + lw + bw + 8, ry);
        }
    }
}

// ============================================================================ paneles del flujo

[DisplayName("NOPE (cobertura neta de opciones)")]
[Category(IndicatorCategories.Options)]
[Description("Net Options Pricing Effect: la delta neta de las opciones operadas en cada vela (calls con su delta positiva, puts con la negativa) como porcentaje del volumen del subyacente en la misma vela. Muestra cuánto puede empujar el precio la cobertura de los creadores de mercado.")]
public class Nope : OptionsPane
{
    private readonly ValueDataSeries _nope = new("NOPE", "NOPE") { Color = Color.FromArgb(255, 165, 120, 255), Width = 2, ShowZeroValue = true };
    private readonly ValueDataSeries _raw = new("RAW", "NOPE sin suavizar") { VisualType = VisualMode.Histogram, Color = Pal.A(Pal.Muted, 110), ShowZeroValue = false };
    private int _smooth = 3;

    public Nope()
    {
        DataSeries[0] = _nope;
        DataSeries.Add(_raw);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(90, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
    }

    [Display(Name = "Suavizado (velas)", GroupName = "Cálculo", Order = 10)] [Range(1, 100)] public int Smooth { get => _smooth; set { _smooth = Math.Clamp(value, 1, 100); RecalculateValues(); } }

    protected override void Calc(int bar)
    {
        var c = GetCandle(bar);
        double delta = 0;
        foreach (var t in TradesOf(bar)) delta += t.Delta * t.Size * t.ContractSize;
        double v = c.Volume > 0 ? delta / (double)c.Volume * 100 : 0;
        _raw[bar] = (decimal)v;
        double k = 2.0 / (_smooth + 1), prev = bar > 0 ? (double)_nope[bar - 1] : v;
        _nope[bar] = (decimal)(prev + k * (v - prev));
    }
}

[DisplayName("Lado agresor de las opciones")]
[Category(IndicatorCategories.Options)]
[Description("Barras por vela con la prima de opciones comprada en el ask (arriba, verde) y vendida en el bid (abajo, rojo): si hoy dominan los compradores o los vendedores de opciones.")]
public class ContractSides : OptionsPane
{
    private readonly ValueDataSeries _buy = new("BUY", "Comprado") { VisualType = VisualMode.Histogram, Color = Pal.A(Opt.Call, 200), ShowZeroValue = false };
    private readonly ValueDataSeries _sell = new("SELL", "Vendido") { VisualType = VisualMode.Histogram, Color = Pal.A(Opt.Put, 200), ShowZeroValue = false };
    private double _dayBuy, _daySell;

    public ContractSides()
    {
        DataSeries[0] = _buy;
        DataSeries.Add(_sell);
    }

    protected override void Calc(int bar)
    {
        double b = 0, s = 0;
        foreach (var t in TradesOf(bar))
            if (t.Aggressor > 0) b += t.Premium;
            else if (t.Aggressor < 0) s += t.Premium;
        _buy[bar] = (decimal)b;
        _sell[bar] = (decimal)(-s);
        if (bar == CurrentBar - 1)
        {
            _dayBuy = _daySell = 0;
            int k = bar;
            while (k > 0 && !NewDay(k)) k--;
            for (int i = k; i <= bar; i++)
            {
                _dayBuy += (double)_buy[i];
                _daySell -= (double)_sell[i];
            }
        }
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        double tot = _dayBuy + _daySell;
        Card(ctx, box, UiText.Tr("Lado de hoy") + " · " + Underlying, new List<(string, string, Color)>
        {
            ("Comprado", Opt.Usd(_dayBuy) + "  " + Opt.Pct(tot > 0 ? _dayBuy / tot * 100 : double.NaN, "0"), Opt.Call),
            ("Vendido", Opt.Usd(_daySell) + "  " + Opt.Pct(tot > 0 ? _daySell / tot * 100 : double.NaN, "0"), Opt.Put),
            ("Dominan", tot <= 0 ? "—" : _dayBuy >= _daySell ? "los compradores" : "los vendedores", _dayBuy >= _daySell ? Opt.Call : Opt.Put),
        });
    }
}

[DisplayName("Flujo neto de opciones")]
[Category(IndicatorCategories.Options)]
[Description("Lo que va del día: la prima comprada menos la vendida de las calls (línea verde) y de las puts (línea roja). Calls que suben y puts que bajan es flujo alcista; al revés, bajista. Se reinicia cada día.")]
public class NetFlow : OptionsPane
{
    private readonly ValueDataSeries _calls = new("CALLS", "Calls (compra − venta)") { Color = Opt.Call, Width = 2, ShowZeroValue = true };
    private readonly ValueDataSeries _puts = new("PUTS", "Puts (compra − venta)") { Color = Opt.Put, Width = 2, ShowZeroValue = true };

    public NetFlow()
    {
        DataSeries[0] = _calls;
        DataSeries.Add(_puts);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(90, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
    }

    protected override void Calc(int bar)
    {
        double c = 0, p = 0;
        foreach (var t in TradesOf(bar))
            if (t.Right == OptionRight.Call) c += t.Aggressor * t.Premium;
            else p += t.Aggressor * t.Premium;
        bool reset = NewDay(bar);
        _calls[bar] = (reset ? 0 : _calls[bar - 1]) + (decimal)c;
        _puts[bar] = (reset ? 0 : _puts[bar - 1]) + (decimal)p;
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        if (CurrentBar == 0) return;
        double c = (double)_calls[CurrentBar - 1], p = (double)_puts[CurrentBar - 1];
        Card(ctx, box, UiText.Tr("Flujo neto de hoy") + " · " + Underlying, new List<(string, string, Color)>
        {
            ("Calls", Opt.SignedUsd(c), Opt.Call),
            ("Puts", Opt.SignedUsd(p), Opt.Put),
            ("Lectura", c - p > 0 ? "flujo alcista" : c - p < 0 ? "flujo bajista" : "—", Opt.BiasColor(c - p)),
        });
    }
}

[DisplayName("Volumen de opciones")]
[Category(IndicatorCategories.Options)]
[Description("El volumen de calls (arriba) y de puts (abajo) de cada vela, con un cuadro con la relación puts/calls de hoy y de la ventana.")]
public class OptionsVolume : OptionsPane
{
    private readonly ValueDataSeries _c = new("CV", "Calls") { VisualType = VisualMode.Histogram, Color = Pal.A(Opt.Call, 200), ShowZeroValue = false };
    private readonly ValueDataSeries _p = new("PV", "Puts") { VisualType = VisualMode.Histogram, Color = Pal.A(Opt.Put, 200), ShowZeroValue = false };

    public OptionsVolume()
    {
        DataSeries[0] = _c;
        DataSeries.Add(_p);
    }

    [Display(Name = "Ventana (velas)", GroupName = "Cálculo", Order = 10)] [Range(2, 2000)] public int Window { get; set; } = 96;

    protected override void Calc(int bar)
    {
        double c = 0, p = 0;
        foreach (var t in TradesOf(bar))
            if (t.Right == OptionRight.Call) c += t.Size;
            else p += t.Size;
        _c[bar] = (decimal)c;
        _p[bar] = (decimal)(-p);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        int last = CurrentBar - 1;
        if (last < 0) return;
        double dc = 0, dp = 0, wc = 0, wp = 0;
        int k = last;
        while (k > 0 && !NewDay(k)) k--;
        for (int i = k; i <= last; i++)
        {
            dc += (double)_c[i];
            dp -= (double)_p[i];
        }

        for (int i = Math.Max(0, last - Window + 1); i <= last; i++)
        {
            wc += (double)_c[i];
            wp -= (double)_p[i];
        }

        Card(ctx, box, UiText.Tr("Volumen de opciones") + " · " + Underlying, new List<(string, string, Color)>
        {
            ("Puts/calls hoy", dc > 0 ? Opt.N(dp / dc, "0.00") : "—", dp > dc ? Opt.Put : Opt.Call),
            ("Puts/calls ventana", wc > 0 ? Opt.N(wp / wc, "0.00") : "—", wp > wc ? Opt.Put : Opt.Call),
            ("Calls / puts hoy", Opt.N(dc, "0") + " / " + Opt.N(dp, "0"), default),
        });
    }
}

[DisplayName("Flujo de griegas")]
[Category(IndicatorCategories.Options)]
[Description("Lo que va de cada día: hacia dónde apuestan los operadores de opciones (la delta comprada menos la vendida, en dólares) y si apuestan a que la volatilidad crezca (la vega comprada menos la vendida, línea naranja con su propia escala).")]
public class GreekFlow : OptionsPane
{
    private readonly ValueDataSeries _delta = new("DELTA", "Delta neta (USD)") { Color = Color.FromArgb(255, 86, 160, 255), Width = 2, ShowZeroValue = true };
    private readonly ValueDataSeries _vega = new("VEGA", "Vega neta (USD por punto)") { VisualType = VisualMode.Hide, Color = Pal.Amber };

    public GreekFlow()
    {
        DataSeries[0] = _delta;
        DataSeries.Add(_vega);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(90, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
    }

    protected override void Calc(int bar)
    {
        double d = 0, v = 0;
        foreach (var t in TradesOf(bar))
        {
            double n = t.Aggressor * t.Size * t.ContractSize;
            d += n * t.Delta * t.Spot;
            v += n * OptionsMath.Vega(t.Spot, t.Strike, Math.Max(1.0 / 365, (t.Expiry - t.Time).TotalDays / 365), t.Iv > 0 ? t.Iv : 0.6);
        }

        bool reset = NewDay(bar);
        _delta[bar] = (reset ? 0 : _delta[bar - 1]) + (decimal)d;
        _vega[bar] = (reset ? 0 : _vega[bar - 1]) + (decimal)v;
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        if (ChartInfo == null || CurrentBar == 0) return;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        if (last > first)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = first; i <= last; i++)
            {
                lo = Math.Min(lo, (double)_vega[i]);
                hi = Math.Max(hi, (double)_vega[i]);
            }

            var r = box.Region;
            var pts = new List<Point>();
            for (int i = first; i <= last; i++)
                pts.Add(new Point(ChartInfo.GetXByBar(i, false), r.Bottom - 6 - (int)(hi > lo ? ((double)_vega[i] - lo) / (hi - lo) * (r.Height - 30) : (r.Height - 30) / 2.0)));
            ctx.DrawLines(new RenderPen(Pal.A(Pal.Amber, 200), 1), pts.ToArray());
        }

        double d = (double)_delta[CurrentBar - 1], v = (double)_vega[CurrentBar - 1];
        Card(ctx, box, UiText.Tr("Flujo de griegas de hoy") + " · " + Underlying, new List<(string, string, Color)>
        {
            ("Delta neta", Opt.SignedUsd(d), Opt.BiasColor(d)),
            ("Vega neta", Opt.SignedUsd(v), Pal.Amber),
            ("Apuestan a", (d >= 0 ? UiText.Tr("suba") : UiText.Tr("baja")) + " · " + UiText.Tr(v >= 0 ? "más volatilidad" : "menos volatilidad"), Opt.BiasColor(d)),
        });
    }
}

[DisplayName("Sentimiento de las opciones")]
[Category(IndicatorCategories.Options)]
[Description("Solo hoy: hacia dónde se inclinan los operadores de opciones, de −100 (todo bajista) a +100 (todo alcista), con la prima alcista y bajista acumulada desde la apertura del día.")]
public class OptionsSentiment : OptionsPane
{
    private readonly ValueDataSeries _s = new("SENT", "Sentimiento") { Color = Color.FromArgb(255, 86, 160, 255), Width = 2, ShowZeroValue = false };
    private double _bull, _bear;
    private int _dayStart = -1;

    public OptionsSentiment()
    {
        DataSeries[0] = _s;
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(90, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("U", "+50") { Value = 50, Color = Color.FromArgb(60, 38, 194, 129), LineDashStyle = LineDashStyle.Dot });
        LineSeries.Add(new LineSeries("D", "−50") { Value = -50, Color = Color.FromArgb(60, 240, 82, 90), LineDashStyle = LineDashStyle.Dot });
    }

    private readonly List<double> _cb = new(), _ce = new();

    protected override void Reset()
    {
        _dayStart = -1;
        _bull = _bear = 0;
        _cb.Clear();
        _ce.Clear();
    }

    protected override void Calc(int bar)
    {
        // acumulado del día de trading de cada vela (solo se muestra el de hoy)
        double bu = 0, be = 0;
        foreach (var t in TradesOf(bar))
            if (t.Bias > 0) bu += t.Premium;
            else if (t.Bias < 0) be += t.Premium;
        bool reset = NewDay(bar);
        double cb = (reset ? 0 : _cb[bar - 1]) + bu, ce = (reset ? 0 : _ce[bar - 1]) + be;
        while (_cb.Count <= bar)
        {
            _cb.Add(0);
            _ce.Add(0);
        }

        _cb[bar] = cb;
        _ce[bar] = ce;
        if (reset) _dayStart = bar;
        _s[bar] = cb + ce > 0 ? (decimal)(100 * (cb - ce) / (cb + ce)) : 0;
        _s.Colors[bar] = _s[bar] >= 0 ? Opt.Call : Opt.Put;
        if (bar == CurrentBar - 1)
        {
            _bull = cb;
            _bear = ce;
            // solo hoy: lo de los días anteriores no se dibuja
            for (int i = 0; i < _dayStart; i++) _s[i] = 0;
        }
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        if (CurrentBar == 0) return;
        double v = (double)_s[CurrentBar - 1];
        Card(ctx, box, UiText.Tr("Sentimiento de hoy") + " · " + Underlying, new List<(string, string, Color)>
        {
            ("Puntaje", MacroText(v), Opt.BiasColor(v)),
            ("Prima alcista", Opt.Usd(_bull), Opt.Call),
            ("Prima bajista", Opt.Usd(_bear), Opt.Put),
        });
    }

    private static string MacroText(double v) => (v > 0 ? "+" : "") + v.ToString("0", CultureInfo.InvariantCulture);
}

[DisplayName("Marea de primas")]
[Category(IndicatorCategories.Options)]
[Description("Todo el mercado de opciones de hoy (los subyacentes que elijas): la prima apostada a la suba (verde) contra la apostada a la baja (roja), acumuladas desde la apertura del día.")]
public class PremiumTide : OptionsPane
{
    private readonly ValueDataSeries _bull = new("BULL", "Prima alcista") { Color = Opt.Call, Width = 2, ShowZeroValue = true };
    private readonly ValueDataSeries _bear = new("BEAR", "Prima bajista") { Color = Opt.Put, Width = 2, ShowZeroValue = true };
    private string[] _unders = { "BTC", "ETH" };
    private string _list = "BTC, ETH";

    public PremiumTide()
    {
        DataSeries[0] = _bull;
        DataSeries.Add(_bear);
    }

    [Display(Name = "Subyacentes", GroupName = "Cálculo", Order = 10)]
    public string Underlyings
    {
        get => _list;
        set
        {
            _list = value ?? "";
            _unders = _list.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToUpperInvariant()).Distinct().ToArray();
            RecalculateValues();
        }
    }

    protected override void Calc(int bar)
    {
        double bu = 0, be = 0;
        var from = GetCandle(bar).Time;
        var to = BarEnd(bar);
        foreach (var u in _unders)
        {
            if (OptionsData.Unavailable(u) != null) continue;
            foreach (var t in OptionsData.TradesBetween(u, from, to))
                if (t.Bias > 0) bu += t.Premium;
                else if (t.Bias < 0) be += t.Premium;
        }

        bool reset = NewDay(bar);
        _bull[bar] = (reset ? 0 : _bull[bar - 1]) + (decimal)bu;
        _bear[bar] = (reset ? 0 : _bear[bar - 1]) + (decimal)be;
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        if (CurrentBar == 0) return;
        double bu = (double)_bull[CurrentBar - 1], be = (double)_bear[CurrentBar - 1];
        Card(ctx, box, UiText.Tr("Marea de primas de hoy") + " · " + string.Join("+", _unders), new List<(string, string, Color)>
        {
            ("A la suba", Opt.Usd(bu), Opt.Call),
            ("A la baja", Opt.Usd(be), Opt.Put),
            ("Diferencia", Opt.SignedUsd(bu - be), Opt.BiasColor(bu - be)),
        });
    }
}
