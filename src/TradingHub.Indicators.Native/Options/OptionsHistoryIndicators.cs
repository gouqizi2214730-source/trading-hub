using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Feeds.External;
using TradingHub.Indicators.Native.Structure;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Options;

// Opciones con la historia diaria: IV Rank, régimen de volatilidad, historia del sesgo, risk reversal, historia de la
// exposición, prima de riesgo de varianza, historia de opciones y medidor de sentimiento. Cada día usa solo los días
// anteriores para compararse: no se redibuja.

/// <summary>Base de los paneles que leen la historia diaria de opciones.</summary>
public abstract class OptionsDailyPane : OptionsPane
{
    protected IReadOnlyList<OptionDaily> Days { get; private set; } = Array.Empty<OptionDaily>();

    /// <summary>El día de la última vela.</summary>
    protected DateTime LastDay => CurrentBar > 0 ? GetCandle(CurrentBar - 1).Time.Date : DateTime.UtcNow.Date;

    protected sealed override void Calc(int bar)
    {
        if (bar == 0) Days = OptionsData.Daily(Underlying);
        CalcDay(bar, DailyAt(bar, Days));
    }

    protected abstract void CalcDay(int bar, OptionDaily? day);

    /// <summary>Los valores de los días anteriores a <paramref name="day"/> (hasta un año).</summary>
    protected IEnumerable<double> Past(DateTime day, Func<OptionDaily, double> f, int lookback = 365) =>
        Days.Where(d => d.Date.Date < day.Date && d.Date.Date >= day.Date.AddDays(-lookback)).Select(f).Where(double.IsFinite);

    protected void NoHistoryNote(RenderContext ctx, IChartContainer box, Func<OptionDaily, double> f)
    {
        int n = Days.Count(d => double.IsFinite(f(d)));
        if (n < 5)
            Note(ctx, box, $"{UiText.Tr("Historia diaria:")} {n} {UiText.Tr("días (se completa con una foto por día mientras el indicador está abierto)")}");
    }
}

[DisplayName("IV Rank")]
[Category(IndicatorCategories.Options)]
[Description("Dónde está la volatilidad implícita de hoy entre la más barata y la más cara del último año (0 a 100), con el percentil y un cuadro que dice si las opciones están baratas, en su precio o caras.")]
public class IvRank : OptionsDailyPane
{
    private readonly ValueDataSeries _rank = new("RANK", "IV Rank") { Color = Color.FromArgb(255, 165, 120, 255), Width = 2, ShowZeroValue = true };
    private readonly ValueDataSeries _pct = new("PCT", "IV percentil") { Color = Pal.A(Pal.Muted, 200), LineDashStyle = LineDashStyle.Dot, ShowZeroValue = true };
    private double _iv = double.NaN;

    public IvRank()
    {
        DataSeries[0] = _rank;
        DataSeries.Add(_pct);
        LineSeries.Add(new LineSeries("H", "70") { Value = 70, Color = Color.FromArgb(70, 240, 82, 90), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("L", "30") { Value = 30, Color = Color.FromArgb(70, 38, 194, 129), LineDashStyle = LineDashStyle.Dash });
    }

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        double iv = d?.AtmIv ?? double.NaN;
        var day = GetCandle(bar).Time.Date;
        var past = Past(day, x => x.AtmIv).ToList();
        double r = Opt.Rank(past, iv), p = Opt.Percentile(past, iv);
        _rank[bar] = double.IsFinite(r) ? (decimal)r : 0;
        _pct[bar] = double.IsFinite(p) ? (decimal)p : 0;
        if (bar == CurrentBar - 1) _iv = iv;
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.AtmIv);
        if (CurrentBar == 0) return;
        double r = (double)_rank[CurrentBar - 1];
        Card(ctx, box, "IV Rank · " + Underlying, new List<(string, string, Color)>
        {
            ("IV hoy", Opt.Pct(_iv * 100), default),
            ("IV Rank", Opt.N(r, "0"), default),
            ("Percentil", Opt.N((double)_pct[CurrentBar - 1], "0"), default),
            ("Opciones", r >= 70 ? "caras" : r <= 30 ? "baratas" : "en su precio", r >= 70 ? Opt.Put : r <= 30 ? Opt.Call : default),
        });
    }
}

[DisplayName("Régimen de volatilidad")]
[Category(IndicatorCategories.Options)]
[Description("Qué tan lejos está la volatilidad implícita de lo normal del último año (en desvíos): barras rojas en los días tensos, azules en los calmos, y un cuadro con cuántos días tensos hubo en el último mes.")]
public class VolatilityRegime : OptionsDailyPane
{
    private readonly ValueDataSeries _z = new("Z", "Desvíos de la IV") { VisualType = VisualMode.Histogram, Color = Pal.A(Pal.Muted, 160), ShowZeroValue = false };

    public VolatilityRegime()
    {
        DataSeries[0] = _z;
        LineSeries.Add(new LineSeries("S", "tenso") { Value = 1.5m, Color = Color.FromArgb(80, 240, 82, 90), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("C", "calmo") { Value = -1, Color = Color.FromArgb(80, 41, 98, 255), LineDashStyle = LineDashStyle.Dash });
    }

    [Display(Name = "Tenso desde (desvíos)", GroupName = "Cálculo", Order = 10)] [Range(0.5, 5)] public decimal Stressed { get => LineSeries[0].Value; set { LineSeries[0].Value = value; RecalculateValues(); } }

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        double iv = d?.AtmIv ?? double.NaN;
        var past = Past(GetCandle(bar).Time.Date, x => x.AtmIv).ToList();
        double z = double.NaN;
        if (past.Count >= 5 && double.IsFinite(iv))
        {
            double m = past.Average(), sd = Math.Sqrt(past.Sum(x => (x - m) * (x - m)) / past.Count);
            z = sd > 0 ? (iv - m) / sd : 0;
        }

        _z[bar] = double.IsFinite(z) ? (decimal)z : 0;
        _z.Colors[bar] = z >= (double)Stressed ? Pal.A(Opt.Put, 220) : z <= -1 ? Pal.A(Pal.Neutral, 200) : Pal.A(Pal.Muted, 140);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.AtmIv);
        if (CurrentBar == 0) return;
        double z = (double)_z[CurrentBar - 1];
        var month = Days.Where(x => x.Date >= LastDay.AddDays(-30)).Select(x => x.AtmIv).ToList();
        var year = Days.Where(x => x.Date >= LastDay.AddDays(-365)).Select(x => x.AtmIv).Where(double.IsFinite).ToList();
        int stressed = 0;
        if (year.Count > 5)
        {
            double m = year.Average(), sd = Math.Sqrt(year.Sum(x => (x - m) * (x - m)) / year.Count);
            stressed = month.Count(v => double.IsFinite(v) && sd > 0 && (v - m) / sd >= (double)Stressed);
        }

        Card(ctx, box, UiText.Tr("Régimen de volatilidad") + " · " + Underlying, new List<(string, string, Color)>
        {
            ("Desvíos de lo normal", Opt.N(z, "+0.00;-0.00"), z >= (double)Stressed ? Opt.Put : default),
            ("Estado", z >= (double)Stressed ? "tenso" : z <= -1 ? "calmo" : "normal", z >= (double)Stressed ? Opt.Put : z <= -1 ? Pal.Neutral : default),
            ("Días tensos (30 d)", stressed.ToString(CultureInfo.InvariantCulture), default),
        });
    }
}

[DisplayName("Historia del sesgo")]
[Category(IndicatorCategories.Options)]
[Description("Cuánto más caras que las calls están las puts (IV de la put de 25 delta menos la de la call, en puntos), día por día, con el rango del último año y su percentil: el miedo sube cuando la línea sube.")]
public class SkewHistory : OptionsDailyPane
{
    private readonly ValueDataSeries _skew = new("SKEW", "Sesgo de las puts") { Color = Opt.Put, Width = 2, ShowZeroValue = true };
    private readonly ValueDataSeries _hi = new("HI", "Máximo del año") { Color = Pal.A(Pal.Muted, 120), LineDashStyle = LineDashStyle.Dot, ShowZeroValue = false };
    private readonly ValueDataSeries _lo = new("LO", "Mínimo del año") { Color = Pal.A(Pal.Muted, 120), LineDashStyle = LineDashStyle.Dot, ShowZeroValue = false };

    public SkewHistory()
    {
        DataSeries[0] = _skew;
        DataSeries.Add(_hi);
        DataSeries.Add(_lo);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(80, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
    }

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        double v = d == null ? double.NaN : -d.Rr25;
        var past = Past(GetCandle(bar).Time.Date, x => -x.Rr25).ToList();
        _skew[bar] = double.IsFinite(v) ? (decimal)v : 0;
        _hi[bar] = past.Count > 1 ? (decimal)past.Max() : 0;
        _lo[bar] = past.Count > 1 ? (decimal)past.Min() : 0;
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.Rr25);
        if (CurrentBar == 0) return;
        double v = (double)_skew[CurrentBar - 1];
        double p = Opt.Percentile(Past(LastDay, x => -x.Rr25), v);
        Card(ctx, box, UiText.Tr("Sesgo de las puts") + " · " + Underlying, new List<(string, string, Color)>
        {
            ("Puts − calls (25Δ)", Opt.N(v, "+0.0;-0.0") + " pts", v > 0 ? Opt.Put : Opt.Call),
            ("Percentil del año", Opt.N(p, "0"), default),
            ("Miedo", !double.IsFinite(p) ? "—" : p >= 80 ? "alto" : p <= 20 ? "bajo" : "normal", p >= 80 ? Opt.Put : default),
        });
    }
}

[DisplayName("Sesgo de risk reversal")]
[Category(IndicatorCategories.Options)]
[Description("Qué está más caro, las puts o las calls: el risk reversal de 25 delta (IV de la call menos la de la put) por día, con la lectura de hoy de la cadena en vivo en el plazo elegido. Negativo (rojo) es que los operadores temen una caída.")]
public class RiskReversalSkew : OptionsDailyPane
{
    private readonly ValueDataSeries _rr = new("RR", "Risk reversal 25Δ") { VisualType = VisualMode.Histogram, Color = Pal.A(Pal.Muted, 160), ShowZeroValue = false };
    private double _live = double.NaN;

    public RiskReversalSkew() => DataSeries[0] = _rr;

    [Display(Name = "Plazo de hoy (días)", GroupName = "Cálculo", Order = 10)] [Range(1, 365)] public int Tenor { get; set; } = 30;

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        double v = d?.Rr25 ?? double.NaN;
        if (bar == CurrentBar - 1 && OptionsData.Chain(Underlying) is { } c)
        {
            _live = OptionsMath.RiskReversal25(c, OptionsMath.ExpiryNear(c, Tenor));
            if (double.IsFinite(_live)) v = _live;
        }

        _rr[bar] = double.IsFinite(v) ? (decimal)v : 0;
        _rr.Colors[bar] = v >= 0 ? Pal.A(Opt.Call, 200) : Pal.A(Opt.Put, 200);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.Rr25);
        Card(ctx, box, UiText.Tr("Risk reversal") + $" {Tenor} d · " + Underlying, new List<(string, string, Color)>
        {
            ("Calls − puts (25Δ)", Opt.N(_live, "+0.0;-0.0") + " pts", _live >= 0 ? Opt.Call : Opt.Put),
            ("Más caras", !double.IsFinite(_live) ? "—" : _live >= 0 ? "las calls" : "las puts", _live >= 0 ? Opt.Call : Opt.Put),
        });
    }
}

[DisplayName("Historia de la exposición")]
[Category(IndicatorCategories.Options)]
[Description("Una columna por día con la gamma de los creadores de mercado (verde positiva, roja negativa) y un cuadro que ubica la de hoy contra la historia: si es inusual.")]
public class ExposureHistory : OptionsDailyPane
{
    private readonly ValueDataSeries _gex = new("GEX", "Gamma (USD por 1 %)") { VisualType = VisualMode.Histogram, Color = Pal.A(Pal.Muted, 160), ShowZeroValue = false };

    public ExposureHistory() => DataSeries[0] = _gex;

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        double v = d?.Gex ?? double.NaN;
        _gex[bar] = double.IsFinite(v) ? (decimal)v : 0;
        _gex.Colors[bar] = v >= 0 ? Pal.A(Opt.Call, 200) : Pal.A(Opt.Put, 200);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.Gex);
        if (CurrentBar == 0) return;
        double v = (double)_gex[CurrentBar - 1];
        double p = Opt.Percentile(Past(LastDay, x => x.Gex), v);
        Card(ctx, box, UiText.Tr("Exposición de hoy") + " · " + Underlying, new List<(string, string, Color)>
        {
            ("Gamma", Opt.SignedUsd(v), Opt.BiasColor(v)),
            ("Percentil", Opt.N(p, "0"), default),
            ("Hoy", !double.IsFinite(p) ? "—" : p >= 90 ? "inusualmente alta" : p <= 10 ? "inusualmente baja" : "normal", p >= 90 || p <= 10 ? Opt.Flip : default),
        });
    }
}

[DisplayName("Prima de riesgo de varianza (VRP)")]
[Category(IndicatorCategories.Options)]
[Description("Para vendedores de opciones: la volatilidad implícita menos la realizada de los últimos 30 días (en puntos). Si las opciones descuentan más movimiento del que hay, venderlas paga; el cuadro dice vender, neutral o esperar.")]
public class VarianceRiskPremium : OptionsDailyPane
{
    private readonly ValueDataSeries _vrp = new("VRP", "VRP") { VisualType = VisualMode.Histogram, Color = Pal.A(Pal.Muted, 160), ShowZeroValue = false };
    private readonly ValueDataSeries _iv = new("IV", "IV") { Color = Color.FromArgb(255, 165, 120, 255), ShowZeroValue = false, VisualType = VisualMode.Hide };
    private readonly ValueDataSeries _rv = new("RV", "Realizada") { Color = Pal.Amber, ShowZeroValue = false, VisualType = VisualMode.Hide };
    private readonly List<double> _closes = new();
    private int _lastDayBar = -1;

    public VarianceRiskPremium()
    {
        DataSeries[0] = _vrp;
        DataSeries.Add(_iv);
        DataSeries.Add(_rv);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(80, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
    }

    [Display(Name = "Días de la realizada", GroupName = "Cálculo", Order = 10)] [Range(5, 120)] public int RealizedDays { get; set; } = 30;
    [Display(Name = "Vender desde (puntos)", GroupName = "Cálculo", Order = 11)] [Range(0, 50)] public decimal SellAbove { get; set; } = 5;

    protected override void Reset()
    {
        _closes.Clear();
        _lastDayBar = -1;
    }

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        if (NewDay(bar) && bar > 0 && bar != _lastDayBar)
        {
            _closes.Add((double)GetCandle(bar - 1).Close);
            _lastDayBar = bar;
        }

        bool crypto = Underlying is "BTC" or "ETH" or "SOL" or "XRP";
        double rv = double.NaN;
        var closes = _closes.Append((double)GetCandle(bar).Close).ToList();
        if (closes.Count > 3)
        {
            var r = new List<double>();
            for (int i = Math.Max(1, closes.Count - RealizedDays); i < closes.Count; i++) r.Add(Math.Log(closes[i] / closes[i - 1]));
            double m = r.Average();
            rv = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / Math.Max(1, r.Count - 1) * (crypto ? 365 : 252)) * 100;
        }

        double iv = (d?.AtmIv ?? double.NaN) * 100, vrp = iv - rv;
        _iv[bar] = double.IsFinite(iv) ? (decimal)iv : 0;
        _rv[bar] = double.IsFinite(rv) ? (decimal)rv : 0;
        _vrp[bar] = double.IsFinite(vrp) ? (decimal)vrp : 0;
        _vrp.Colors[bar] = vrp >= (double)SellAbove ? Pal.A(Opt.Call, 210) : vrp < 0 ? Pal.A(Opt.Put, 210) : Pal.A(Pal.Muted, 150);
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.AtmIv);
        if (CurrentBar == 0) return;
        int l = CurrentBar - 1;
        double v = (double)_vrp[l];
        Card(ctx, box, "VRP · " + Underlying, new List<(string, string, Color)>
        {
            ("IV", Opt.Pct((double)_iv[l]), default),
            ("Realizada", Opt.Pct((double)_rv[l]), default),
            ("Prima", Opt.N(v, "+0.0;-0.0") + " pts", v >= 0 ? Opt.Call : Opt.Put),
            ("Para vender opciones", v >= (double)SellAbove ? "vender" : v < 0 ? "esperar" : "neutral", v >= (double)SellAbove ? Opt.Call : v < 0 ? Opt.Put : default),
        });
    }
}

[DisplayName("Historia de opciones")]
[Category(IndicatorCategories.Options)]
[Description("El volumen o el interés abierto diario de las opciones (calls arriba, puts abajo), con un cuadro con el último día, el promedio de 20 días y los últimos tres días.")]
public class OptionsHistory : OptionsDailyPane
{
    public enum Metric
    {
        [Display(Name = "Volumen")] Volume,
        [Display(Name = "Interés abierto")] OpenInterest,
    }

    private readonly ValueDataSeries _c = new("C", "Calls") { VisualType = VisualMode.Histogram, Color = Pal.A(Opt.Call, 200), ShowZeroValue = false };
    private readonly ValueDataSeries _p = new("P", "Puts") { VisualType = VisualMode.Histogram, Color = Pal.A(Opt.Put, 200), ShowZeroValue = false };
    private Metric _m = Metric.Volume;

    public OptionsHistory()
    {
        DataSeries[0] = _c;
        DataSeries.Add(_p);
    }

    [Display(Name = "Medida", GroupName = "Cálculo", Order = 10)] public Metric Measure { get => _m; set { _m = value; RecalculateValues(); } }

    private (double C, double P) Of(OptionDaily d) => _m == Metric.Volume ? (d.CallVolume, d.PutVolume) : (d.CallOi, d.PutOi);

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        var (c, p) = d == null ? (double.NaN, double.NaN) : Of(d);
        _c[bar] = double.IsFinite(c) ? (decimal)c : 0;
        _p[bar] = double.IsFinite(p) ? (decimal)(-p) : 0;
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => Of(x).C);
        var valid = Days.Where(x => double.IsFinite(Of(x).C)).ToList();
        if (valid.Count == 0) return;
        var last = valid[^1];
        var avg = valid.TakeLast(20).ToList();
        var rows = new List<(string, string, Color)>
        {
            ($"{UiText.Tr("Último día")} ({last.Date:dd/MM})", Opt.N(Of(last).C, "0") + " / " + Opt.N(Of(last).P, "0"), default),
            ("Promedio 20 días", Opt.N(avg.Average(x => Of(x).C), "0") + " / " + Opt.N(avg.Average(x => Of(x).P), "0"), default),
        };
        foreach (var x in valid.TakeLast(3).Reverse())
        {
            var (c, p) = Of(x);
            rows.Add((x.Date.ToString("dd/MM", CultureInfo.InvariantCulture), Opt.N(c, "0") + " / " + Opt.N(p, "0") + (c > 0 ? "  P/C " + Opt.N(p / c, "0.00") : ""), p > c ? Opt.Put : Opt.Call));
        }

        Card(ctx, box, UiText.Tr(_m == Metric.Volume ? "Volumen de opciones (calls / puts)" : "Interés abierto (calls / puts)") + " · " + Underlying, rows);
    }
}

[DisplayName("Medidor de sentimiento de opciones")]
[Category(IndicatorCategories.Options)]
[Description("Un medidor de miedo y codicia de 0 a 100 que compara contra el último año seis señales diarias de opciones y precio: IV, sesgo, puts/calls por volumen y por interés abierto, gamma de los creadores de mercado e impulso del precio. Línea diaria en el panel y el medidor con la lectura de hoy.")]
public class OptionsSentimentGauge : OptionsDailyPane
{
    private readonly ValueDataSeries _g = new("G", "Miedo y codicia") { Color = Color.FromArgb(255, 251, 191, 36), Width = 2, ShowZeroValue = false };
    private readonly List<(DateTime Day, double Close)> _closes = new();
    private readonly List<double> _mom = new();
    private int _signals, _lastDayBar = -1;

    public OptionsSentimentGauge()
    {
        DataSeries[0] = _g;
        LineSeries.Add(new LineSeries("H", "75") { Value = 75, Color = Color.FromArgb(60, 38, 194, 129), LineDashStyle = LineDashStyle.Dot });
        LineSeries.Add(new LineSeries("M", "50") { Value = 50, Color = Color.FromArgb(70, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("L", "25") { Value = 25, Color = Color.FromArgb(60, 240, 82, 90), LineDashStyle = LineDashStyle.Dot });
    }

    protected override void Reset()
    {
        _closes.Clear();
        _mom.Clear();
        _lastDayBar = -1;
    }

    protected override void CalcDay(int bar, OptionDaily? d)
    {
        var day = GetCandle(bar).Time.Date;
        if (NewDay(bar) && bar > 0 && bar != _lastDayBar)
        {
            _lastDayBar = bar;
            _closes.Add((GetCandle(bar - 1).Time.Date, (double)GetCandle(bar - 1).Close));
            if (_closes.Count > 50) _mom.Add(_closes[^1].Close / _closes.Skip(_closes.Count - 51).Take(50).Average(x => x.Close) - 1);
        }

        var parts = new List<double>();
        if (d != null)
        {
            void Add(double v, Func<OptionDaily, double> f, bool greedHigh)
            {
                double p = Opt.Percentile(Past(day, f), v);
                if (double.IsFinite(p)) parts.Add(greedHigh ? p : 100 - p);
            }

            Add(d.AtmIv, x => x.AtmIv, false);
            Add(d.Rr25, x => x.Rr25, true);
            Add(d.PutCallVolume, x => x.PutCallVolume, false);
            Add(d.PutCallOi, x => x.PutCallOi, false);
            Add(d.Gex, x => x.Gex, true);
        }

        // impulso: el cierre contra su media de 50 días, comparado con el último año
        if (_mom.Count > 10)
        {
            double now = (double)GetCandle(bar).Close / _closes.Skip(_closes.Count - 50).Average(x => x.Close) - 1;
            double p = Opt.Percentile(_mom.Skip(Math.Max(0, _mom.Count - 365)), now);
            if (double.IsFinite(p)) parts.Add(p);
        }

        _g[bar] = parts.Count > 0 ? (decimal)parts.Average() : 0;
        if (bar == CurrentBar - 1) _signals = parts.Count;
    }

    protected override void Render(RenderContext ctx, IChartContainer box)
    {
        NoHistoryNote(ctx, box, x => x.AtmIv);
        if (CurrentBar == 0 || !ShowDash) return;
        double v = (double)_g[CurrentBar - 1];
        var r = box.Region;
        int w = 170, h = 110;
        int x = r.Right - w - 8, y = r.Top + 20;
        ctx.FillRectangle(Color.FromArgb(220, 18, 20, 26), new Rectangle(x, y, w, h), 6);
        var arc = new Rectangle(x + 25, y + 18, w - 50, (w - 50));
        ctx.DrawArc(new RenderPen(Color.FromArgb(80, 140, 145, 160), 8), arc, 180, 180);
        string label = v >= 75 ? "Codicia extrema" : v >= 55 ? "Codicia" : v > 45 ? "Neutral" : v > 25 ? "Miedo" : "Miedo extremo";
        var col = v >= 55 ? Opt.Call : v <= 45 ? Opt.Put : Pal.Muted;
        if (v > 0) ctx.DrawArc(new RenderPen(col, 8), arc, 180, (float)(180 * Math.Clamp(v, 0, 100) / 100));
        var big = new RenderFont("Arial", 16, FontStyle.Bold);
        string t = v > 0 ? v.ToString("0", CultureInfo.InvariantCulture) : "—";
        ctx.DrawString(t, big, Opt.Text, x + w / 2 - ctx.MeasureString(t, big).Width / 2, y + 48);
        var f = new RenderFont("Arial", 8);
        string lt = UiText.Tr(label);
        ctx.DrawString(lt, f, col, x + w / 2 - ctx.MeasureString(lt, f).Width / 2, y + 74);
        string st = $"{_signals}/6 " + UiText.Tr("señales");
        ctx.DrawString(st, f, Opt.Dim, x + w / 2 - ctx.MeasureString(st, f).Width / 2, y + 90);
    }
}
