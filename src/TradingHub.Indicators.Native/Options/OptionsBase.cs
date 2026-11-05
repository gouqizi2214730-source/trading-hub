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

// Base de los indicadores de opciones: toman los datos del centro de opciones (la conexión «Datos de opciones (API)» de
// Conexiones). Sin conexión dicen que no hay datos y esperan: cuando se conecta una, se recalculan solos. El subyacente
// sale del símbolo del gráfico (BTCUSDT → BTC).

/// <summary>Qué vencimiento usar.</summary>
public enum ExpiryPick
{
    [Display(Name = "Todos")] All,
    [Display(Name = "El más cercano")] Nearest,
    [Display(Name = "El siguiente")] Next,
    [Display(Name = "Semanal (≈ 7 días)")] Weekly,
    [Display(Name = "Mensual (≈ 30 días)")] Monthly,
    [Display(Name = "Trimestral (≈ 90 días)")] Quarterly,
}

internal static class Opt
{
    public static readonly Color Call = Color.FromArgb(255, 38, 194, 129);
    public static readonly Color Put = Color.FromArgb(255, 240, 82, 90);
    public static readonly Color Flip = Color.FromArgb(255, 251, 191, 36);
    public static readonly Color Text = Color.FromArgb(235, 230, 232, 238);
    public static readonly Color Dim = Color.FromArgb(200, 160, 166, 178);

    public static string Usd(double v) => double.IsFinite(v) ? FlowText.Usd(v) : "—";

    public static string SignedUsd(double v) => double.IsFinite(v) ? FlowText.Signed(v) : "—";

    public static string Pct(double v, string fmt = "0.0") => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) + " %" : "—";

    public static string N(double v, string fmt = "0.##") => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "—";

    public static Color BiasColor(double v) => v > 0 ? Call : v < 0 ? Put : Pal.Muted;

    /// <summary>El vencimiento elegido (null = todos).</summary>
    public static DateTime? Pick(OptionsChain c, ExpiryPick pick)
    {
        var exps = c.Contracts.Select(o => o.Expiry).Distinct().Where(e => e > c.Time).OrderBy(e => e).ToList();
        if (exps.Count == 0 || pick == ExpiryPick.All) return null;
        return pick switch
        {
            ExpiryPick.Nearest => exps[0],
            ExpiryPick.Next => exps[Math.Min(1, exps.Count - 1)],
            ExpiryPick.Weekly => OptionsMath.ExpiryNear(c, 7),
            ExpiryPick.Monthly => OptionsMath.ExpiryNear(c, 30),
            _ => OptionsMath.ExpiryNear(c, 90),
        };
    }

    public static IEnumerable<OptionContract> Of(OptionsChain c, DateTime? expiry) => expiry == null ? c.Contracts : c.Contracts.Where(o => o.Expiry == expiry);

    public static string ExpiryText(DateTime? e) => e == null ? "todos" : e.Value.ToString("dd/MM/yy", CultureInfo.InvariantCulture);

    /// <summary>El lugar de <paramref name="v"/> entre los valores (0 a 100).</summary>
    public static double Percentile(IEnumerable<double> values, double v)
    {
        var list = values.Where(double.IsFinite).ToList();
        if (list.Count < 2 || !double.IsFinite(v)) return double.NaN;
        return 100.0 * list.Count(x => x < v) / list.Count + 50.0 * list.Count(x => x == v) / list.Count;
    }

    /// <summary>IV Rank: dónde está <paramref name="v"/> entre el mínimo y el máximo (0 a 100).</summary>
    public static double Rank(IEnumerable<double> values, double v)
    {
        var list = values.Where(double.IsFinite).ToList();
        if (list.Count < 2 || !double.IsFinite(v)) return double.NaN;
        double lo = list.Min(), hi = list.Max();
        return hi > lo ? Math.Clamp((v - lo) / (hi - lo) * 100, 0, 100) : 50;
    }

    /// <summary>La foto diaria de un día (o NaN si no hay).</summary>
    public static OptionDaily? DayOf(IReadOnlyList<OptionDaily> days, DateTime day)
    {
        int lo = 0, hi = days.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var d = days[mid].Date.Date;
            if (d == day.Date) return days[mid];
            if (d < day.Date) lo = mid + 1;
            else hi = mid - 1;
        }

        return null;
    }

    /// <summary>Un texto corto de un contrato: «C 100k 27/12».</summary>
    public static string Contract(OptionRight r, double strike, DateTime expiry) =>
        $"{(r == OptionRight.Call ? "C" : "P")} {Strike(strike)} {expiry.ToString("dd/MM", CultureInfo.InvariantCulture)}";

    public static string Strike(double k) => k >= 10000 ? (k / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : k.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Barras horizontales por ejercicio pegadas al borde derecho del precio (calls a la derecha, puts a la izquierda, o con signo).</summary>
    public static void DockBars(RenderContext ctx, Rectangle region, Func<double, int> y, IReadOnlyList<(double Strike, double Left, double Right)> rows, int width, double step, Color left, Color right, bool signed)
    {
        if (rows.Count == 0) return;
        double max = rows.Max(r => Math.Max(Math.Abs(r.Left), Math.Abs(r.Right)));
        if (max <= 0) return;
        int edge = region.Right - 4, mid = edge - width / 2;
        int h = Math.Max(2, Math.Abs(y(rows[0].Strike) - y(rows[0].Strike + step)) - 1);
        h = Math.Min(h, 14);
        ctx.FillRectangle(Color.FromArgb(70, 14, 17, 24), new Rectangle(edge - width - 2, region.Top, width + 4, region.Height));
        foreach (var r in rows)
        {
            int cy = y(r.Strike);
            if (cy < region.Top || cy > region.Bottom) continue;
            if (signed)
            {
                double v = r.Right;
                int w = (int)(width / 2.0 * Math.Abs(v) / max);
                ctx.FillRectangle(v >= 0 ? right : left, new Rectangle(v >= 0 ? mid : mid - w, cy - h / 2, Math.Max(1, w), h));
            }
            else
            {
                int wl = (int)(width / 2.0 * Math.Abs(r.Left) / max), wr = (int)(width / 2.0 * Math.Abs(r.Right) / max);
                if (wl > 0) ctx.FillRectangle(left, new Rectangle(mid - wl, cy - h / 2, wl, h));
                if (wr > 0) ctx.FillRectangle(right, new Rectangle(mid, cy - h / 2, wr, h));
            }
        }

        ctx.DrawLine(new RenderPen(Color.FromArgb(90, 160, 165, 175), 1), mid, region.Top, mid, region.Bottom);
    }
}

/// <summary>Indicador de opciones sobre el precio (con la escena de siempre: líneas, cajas, marcas y cuadro).</summary>
public abstract class OptionsScene : SceneIndicator
{
    private readonly Action _changed;
    private long _gen = -1;

    protected OptionsScene()
    {
        _changed = () => DoActionInGuiThread(() =>
        {
            if (OptionsData.Generation != _gen) RecalculateValues();
        });
    }

    /// <summary>El subyacente del gráfico (BTC, ETH, SPY…).</summary>
    protected string Underlying { get; private set; } = "";

    /// <summary>Por qué no hay nada que dibujar todavía (null = hay datos).</summary>
    protected string? Status { get; set; }

    /// <summary>Cada cuánto se pide la cadena.</summary>
    protected virtual TimeSpan ChainAge => TimeSpan.FromSeconds(60);

    protected override void OnInitialize()
    {
        base.OnInitialize();
        OptionsData.Changed += _changed;
    }

    protected override void OnDispose()
    {
        base.OnDispose();
        OptionsData.Changed -= _changed;
    }

    protected sealed override void Build(Scene s)
    {
        _gen = OptionsData.Generation;
        Underlying = OptionsData.UnderlyingOf(InstrumentInfo?.Instrument);
        Status = OptionsData.Unavailable(Underlying);
        if (Status != null || Bars.Count < 2) return;
        BuildOptions(s);
    }

    protected abstract void BuildOptions(Scene s);

    /// <summary>La cadena (null y <see cref="Status"/> «trayendo…» si todavía no llegó).</summary>
    protected OptionsChain? GetChain()
    {
        var c = OptionsData.Chain(Underlying, ChainAge);
        if (c == null) Status = OptionsData.LastError is { } e ? "Opciones: " + e : $"Trayendo la cadena de opciones de {Underlying}…";
        return c;
    }

    /// <summary>Las operaciones de una vela local.</summary>
    protected IReadOnlyList<OptionTrade> TradesOf(int i) => OptionsData.TradesBetween(Underlying, TimeOf(i), EndOf(i));

    protected IReadOnlyList<OptionTrade> TradesBetween(DateTime from, DateTime to) => OptionsData.TradesBetween(Underlying, from, to);

    /// <summary>El fin de una vela local (la apertura de la siguiente).</summary>
    protected DateTime EndOf(int i) => i + 1 < Bars.Count ? TimeOf(i + 1) : TimeOf(i) + (BarLength > TimeSpan.Zero ? BarLength : TimeSpan.FromMinutes(1));

    /// <summary>La vela local que contiene un instante (−1 antes de la primera).</summary>
    protected int BarOf(DateTime t)
    {
        long s = Sec(t);
        int lo = 0, hi = Bars.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (Bars[mid].T <= s)
            {
                ans = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        return ans;
    }

    /// <summary>La primera vela local del día de trading de la vela <paramref name="i"/>.</summary>
    protected int DayStart(int i)
    {
        int k = Math.Clamp(i, 0, Bars.Count - 1);
        while (k > 0 && DayKey[k - 1] == DayKey[i]) k--;
        return k;
    }

    /// <summary>Si las operaciones desde un instante ya llegaron (si no, se avisa «trayendo»).</summary>
    protected bool TradesReady(DateTime from) => OptionsData.TradesReady(Underlying, from);

    /// <summary>Las operaciones del día de trading actual.</summary>
    protected IReadOnlyList<OptionTrade> TodayTrades() => TradesBetween(TimeOf(DayStart(LastBar)), EndOf(LastBar));

    /// <summary>Volatilidad realizada anualizada (cierres diarios del gráfico, los últimos <paramref name="days"/>).</summary>
    protected double RealizedVol(int days)
    {
        var closes = new List<double>();
        for (int i = 0; i < Bars.Count; i++)
            if (i == Bars.Count - 1 || DayKey[i + 1] != DayKey[i]) closes.Add(Bars[i].C);
        if (closes.Count < 3) return double.NaN;
        var r = new List<double>();
        for (int i = Math.Max(1, closes.Count - days); i < closes.Count; i++) r.Add(Math.Log(closes[i] / closes[i - 1]));
        if (r.Count < 2) return double.NaN;
        double m = r.Average(), v = r.Sum(x => (x - m) * (x - m)) / (r.Count - 1);
        bool crypto = Underlying is "BTC" or "ETH" or "SOL" or "XRP";
        return Math.Sqrt(v * (crypto ? 365 : 252));
    }

    protected sealed override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (Status != null)
        {
            PaneCard.Message(ctx, region, Status);
            return;
        }

        RenderOptions(ctx, region);
    }

    protected virtual void RenderOptions(RenderContext ctx, Rectangle region)
    {
    }

    /// <summary>Un cuadro propio (cuando la escena no alcanza), en la esquina elegida.</summary>
    protected void Card(RenderContext ctx, Rectangle region, string? title, IReadOnlyList<(string, string, Color)> rows) =>
        PaneCard.Draw(ctx, region, title, rows, DashboardPosition, TextSize switch { TextSize.Small => 8, TextSize.Large => 11, _ => 9 }, 26);
}

/// <summary>Indicador de opciones en su propio panel (series por vela).</summary>
public abstract class OptionsPane : Indicator
{
    private readonly Action _changed;
    private long _gen = -1;

    protected OptionsPane() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _changed = () => DoActionInGuiThread(() =>
        {
            if (OptionsData.Generation != _gen) RecalculateValues();
        });
    }

    protected string Underlying { get; private set; } = "";

    protected string? Status { get; set; }

    private OptionDaily? _today;

    [Display(Name = "Mostrar el cuadro", GroupName = "Tablero", Order = 900)] public bool ShowDash { get; set; } = true;
    [Display(Name = "Posición del cuadro", GroupName = "Tablero", Order = 901)] public DashCorner DashPosition { get; set; } = DashCorner.TopRight;

    protected override void OnInitialize() => OptionsData.Changed += _changed;

    protected override void OnDispose() => OptionsData.Changed -= _changed;

    protected sealed override void OnRecalculate()
    {
        _gen = OptionsData.Generation;
        Reset();
    }

    protected virtual void Reset()
    {
    }

    protected sealed override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0)
        {
            Underlying = OptionsData.UnderlyingOf(InstrumentInfo?.Instrument);
            Status = OptionsData.Unavailable(Underlying);
        }

        if (Status != null) return;
        Calc(bar);
    }

    protected abstract void Calc(int bar);

    protected DateTime BarEnd(int bar) => bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : GetCandle(bar).Time + (ChartInfo?.GetTimeFrameTypeChartPeriod() is { } p && p > TimeSpan.Zero ? p : TimeSpan.FromMinutes(1));

    protected IReadOnlyList<OptionTrade> TradesOf(int bar) => OptionsData.TradesBetween(Underlying, GetCandle(bar).Time, BarEnd(bar));

    protected bool NewDay(int bar) => bar == 0 || IsNewSession(bar);

    /// <summary>La foto diaria del día de la vela (para hoy, la de la cadena en vivo si el proveedor todavía no la tiene).</summary>
    protected OptionDaily? DailyAt(int bar, IReadOnlyList<OptionDaily> days)
    {
        var day = GetCandle(bar).Time.Date;
        var d = Opt.DayOf(days, day);
        // hoy (todas sus velas): la foto de la cadena en vivo si el proveedor todavía no tiene el día
        if (d == null && CurrentBar > 0 && day == GetCandle(CurrentBar - 1).Time.Date && OptionsData.Chain(Underlying) is { } c) d = _today ??= OptionsMath.DailyOf(c);
        if (bar == CurrentBar - 1) _today = null;
        return d;
    }

    protected sealed override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box) return;
        if (Status != null)
        {
            PaneCard.Message(ctx, box.Region, Status);
            return;
        }

        Render(ctx, box);
    }

    protected virtual void Render(RenderContext ctx, IChartContainer box)
    {
    }

    protected void Card(RenderContext ctx, IChartContainer box, string? title, IReadOnlyList<(string, string, Color)> rows)
    {
        if (ShowDash) PaneCard.Draw(ctx, box.Region, title, rows, DashPosition);
    }

    /// <summary>Un aviso chico arriba a la izquierda (por ejemplo «trayendo las operaciones…»).</summary>
    protected static void Note(RenderContext ctx, IChartContainer box, string text) =>
        ctx.DrawString(UiText.Tr(text), new RenderFont("Arial", 8), Opt.Dim, box.Region.Left + 6, box.Region.Top + 18);
}
