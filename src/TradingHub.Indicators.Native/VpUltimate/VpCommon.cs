using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.VpUltimate;

/// <summary>Período de referencia de los indicadores «VP definitivo».</summary>
public enum VpPeriod
{
    [Display(Name = "1 hora")] Hour1,
    [Display(Name = "4 horas")] Hour4,
    [Display(Name = "Diario")] Day,
    [Display(Name = "Semanal")] Week,
    [Display(Name = "Mensual")] Month,
    [Display(Name = "Trimestral")] Quarter,
    [Display(Name = "Anual")] Year,
}

/// <summary>Cómo se toma el volumen de cada nivel de precio al armar un perfil.</summary>
public enum VpVolumeMode
{
    [Display(Name = "Sin procesar")] Raw,
    [Display(Name = "Recortar extremos")] Cap,
    [Display(Name = "Compresión logarítmica")] Log,
}

/// <summary>
/// Utilidades compartidas por los indicadores «VP definitivo» (versión para Trading Hub del indicador de TradingView del
/// mismo nombre). A diferencia del original, que reconstruye cada vela con velas de 1 minuto pedidas aparte, acá cada vela
/// trae su footprint (volumen comprador / vendedor por precio): los perfiles, el delta y el VWAP de cada vela son exactos.
/// </summary>
public static class Vp
{
    public const string Category = "VP definitivo";

    public static readonly TimeZoneInfo NewYork = FindNewYork();

    private static TimeZoneInfo FindNewYork()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // el otro nombre
            }
        }

        return TimeZoneInfo.Utc;
    }

    public static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);

    public static DateTime Ny(DateTime t) => TimeZoneInfo.ConvertTimeFromUtc(Utc(t), NewYork);

    /// <summary>Hora y minuto como número (930 = 9:30).</summary>
    public static int Hm(DateTime t) => t.Hour * 100 + t.Minute;

    /// <summary>«0300-1100» → (300, 1100); null si no se entiende.</summary>
    public static (int Start, int End)? ParseSession(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Replace(" ", "").Replace(":", "").Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b)) return null;
        if (a < 0 || a > 2359 || b < 0 || b > 2400 || a % 100 > 59 || b % 100 > 59) return null;
        return (a, b);
    }

    /// <summary>La hora (HHMM) cae en el tramo; inicio &gt; fin = cruza la medianoche.</summary>
    public static bool InSession(int hm, (int Start, int End) s) =>
        s.Start < s.End ? hm >= s.Start && hm < s.End : hm >= s.Start || hm < s.End;

    /// <summary>Velas de subasta de apertura / cierre de las acciones de EE. UU. (hora de Nueva York).</summary>
    public static bool IsAuction(DateTime utc)
    {
        int hm = Hm(Ny(utc));
        return hm == 930 || hm == 1559 || hm == 1600;
    }

    /// <summary>Comprador y vendedor de la vela (agresores); sin footprint, estimado por la forma de la vela.</summary>
    public static (decimal Buy, decimal Sell) BuySell(IndicatorCandle c)
    {
        if (c.Ask + c.Bid > 0) return (c.Ask, c.Bid);
        decimal range = c.High - c.Low;
        if (range <= 0) return (c.Volume / 2, c.Volume / 2);
        return (c.Volume * (c.Close - c.Low) / range, c.Volume * (c.High - c.Close) / range);
    }

    /// <summary>Precio medio ponderado por volumen de la vela (de su footprint; sin él, HLC/3).</summary>
    public static decimal CandleVwap(IndicatorCandle c)
    {
        decimal pv = 0, v = 0;
        foreach (var l in c.GetAllPriceLevels())
        {
            pv += l.Price * l.Volume;
            v += l.Volume;
        }

        return v > 0 ? pv / v : (c.High + c.Low + c.Close) / 3;
    }

    /// <summary>
    /// Z robusto sobre el logaritmo: log(1 + x), centrado en la mediana y escalado con 1,4826 × MAD (un volumen gigante
    /// aislado no corre la base). <paramref name="window"/> incluye el valor actual.
    /// </summary>
    public static double RobustLogZ(IReadOnlyList<double> window, double current)
    {
        if (window.Count < 2) return 0;
        var logs = new double[window.Count];
        for (int i = 0; i < window.Count; i++) logs[i] = Math.Log(1 + Math.Max(window[i], 0));
        double med = Median(logs);
        var devs = new double[logs.Length];
        for (int i = 0; i < logs.Length; i++) devs[i] = Math.Abs(logs[i] - med);
        double mad = Median(devs);
        return mad > 0 ? (Math.Log(1 + Math.Max(current, 0)) - med) / (1.4826 * mad) : 0;
    }

    public static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        var a = (double[])values.Clone();
        Array.Sort(a);
        int n = a.Length;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2;
    }

    public static decimal Median(List<decimal> values)
    {
        if (values.Count == 0) return 0;
        var a = values.ToArray();
        Array.Sort(a);
        int n = a.Length;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2;
    }

    /// <summary>1234567 → «1.23M».</summary>
    public static string Short(decimal v)
    {
        decimal a = Math.Abs(v);
        return a >= 1_000_000_000m ? (a / 1_000_000_000m).ToString("0.##") + "B"
            : a >= 1_000_000m ? (a / 1_000_000m).ToString("0.##") + "M"
            : a >= 1_000m ? (a / 1_000m).ToString("0.##") + "K"
            : a.ToString(a >= 10 ? "0" : "0.##");
    }

    /// <summary>Normal estándar: z que deja en el centro la proporción p (Abramowitz–Stegun 26.2.23).</summary>
    public static double CentralZ(double p)
    {
        double q = (1 - p) / 2;
        double t = Math.Sqrt(-2 * Math.Log(q));
        return t - (2.515517 + 0.802853 * t + 0.010328 * t * t) / (1 + 1.432788 * t + 0.189269 * t * t + 0.001308 * t * t * t);
    }

    public static RenderPen Pen(Color c, int width, LineDashStyle style = LineDashStyle.Solid) =>
        new(c, Math.Clamp(width, 1, 10), Rendering.Settings.PenSettings.ToDash(style));

    public static Color Alpha(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);
}

/// <summary>
/// Base de los indicadores «VP definitivo»: las velas cerradas se procesan una sola vez y en orden (<see cref="Commit"/>),
/// como las señales «al cierre de la vela» del original (no repintan); la vela que se está formando solo se mira para
/// dibujar.
/// </summary>
public abstract class VpIndicator : Indicator
{
    private int _committed = -1;

    protected VpIndicator() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
    }

    /// <summary>Última vela cerrada ya procesada (-1 = ninguna).</summary>
    protected int Committed => _committed;

    protected sealed override void OnRecalculate()
    {
        _committed = -1;
        Reset();
    }

    protected sealed override void OnCalculate(int bar, decimal value)
    {
        while (_committed < bar - 1)
        {
            _committed++;
            Commit(_committed);
        }

        Live(bar);
    }

    /// <summary>Vuelve al estado inicial (antes de recalcular todo).</summary>
    protected abstract void Reset();

    /// <summary>Una vela cerrada, en orden y una sola vez.</summary>
    protected abstract void Commit(int bar);

    /// <summary>La vela actual (puede llamarse muchas veces mientras se forma).</summary>
    protected virtual void Live(int bar)
    {
    }

    /// <summary>Empieza un período nuevo en <paramref name="bar"/>.</summary>
    protected bool NewPeriod(int bar, VpPeriod period)
    {
        if (bar <= 0) return bar == 0;
        var prev = Vp.Utc(GetCandle(bar - 1).Time);
        var cur = Vp.Utc(GetCandle(bar).Time);
        return period switch
        {
            VpPeriod.Hour1 => prev.Ticks / TimeSpan.TicksPerHour != cur.Ticks / TimeSpan.TicksPerHour,
            VpPeriod.Hour4 => prev.Ticks / (TimeSpan.TicksPerHour * 4) != cur.Ticks / (TimeSpan.TicksPerHour * 4),
            VpPeriod.Day => IsNewSession(bar),
            VpPeriod.Week => IsNewWeek(bar),
            VpPeriod.Month => IsNewMonth(bar),
            VpPeriod.Quarter => IsNewMonth(bar) && ((prev.Month - 1) / 3 != (cur.Month - 1) / 3 || prev.Year != cur.Year),
            _ => IsNewMonth(bar) && prev.Year != cur.Year,
        };
    }

    /// <summary>Largo del período (aproximado) para comparar con el de la vela del gráfico.</summary>
    protected static TimeSpan Length(VpPeriod p) => p switch
    {
        VpPeriod.Hour1 => TimeSpan.FromHours(1),
        VpPeriod.Hour4 => TimeSpan.FromHours(4),
        VpPeriod.Day => TimeSpan.FromDays(1),
        VpPeriod.Week => TimeSpan.FromDays(7),
        VpPeriod.Month => TimeSpan.FromDays(28),
        VpPeriod.Quarter => TimeSpan.FromDays(90),
        _ => TimeSpan.FromDays(365),
    };

    /// <summary>Duración de una vela del gráfico (cero si no es por tiempo: ticks, rango…).</summary>
    protected TimeSpan BarLength => ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.Zero;

    /// <summary>El gráfico es intradiario (velas por tiempo de menos de un día).</summary>
    protected bool Intraday => BarLength > TimeSpan.Zero && BarLength < TimeSpan.FromDays(1);

    /// <summary>X de una vela, también a la derecha de la última (se extrapola con el ancho de una vela).</summary>
    protected int X(int bar, bool start = true)
    {
        int last = Math.Max(0, CurrentBar - 1);
        if (bar <= last) return ChartInfo!.GetXByBar(bar, start);
        int x0 = ChartInfo!.GetXByBar(last, start);
        int step = last > 0 ? x0 - ChartInfo.GetXByBar(last - 1, start) : Math.Max(1, ChartInfo.PriceChartContainer.BarsWidth);
        return x0 + (bar - last) * Math.Max(1, step);
    }

    protected int Y(decimal price) => ChartInfo!.GetYByPrice(price, false);

    /// <summary>Tocado al cierre de la vela: la vela cubre el precio o lo saltó con un hueco.</summary>
    protected bool Touched(int bar, decimal price)
    {
        var c = GetCandle(bar);
        if (c.High >= price && c.Low <= price) return true;
        if (bar == 0) return false;
        decimal pc = GetCandle(bar - 1).Close;
        return (pc < price && c.Low > price) || (pc > price && c.High < price);
    }

    protected static readonly RenderFont SmallFont = new("Arial", 8);

    protected void Text(RenderContext ctx, string text, Color color, int x, int y, bool above = true)
    {
        var size = ctx.MeasureString(text, SmallFont);
        ctx.DrawString(text, SmallFont, color, x, above ? y - size.Height : y);
    }

    protected static void TextBox(RenderContext ctx, string text, Color fill, Color fore, int x, int y)
    {
        var size = ctx.MeasureString(text, SmallFont);
        var r = new Rectangle(x, y - size.Height / 2 - 2, size.Width + 8, size.Height + 4);
        ctx.FillRectangle(fill, r, 3);
        ctx.DrawString(text, SmallFont, fore, x + 4, y - size.Height / 2);
    }
}
