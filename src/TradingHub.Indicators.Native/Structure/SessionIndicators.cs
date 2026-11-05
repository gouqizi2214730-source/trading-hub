using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

namespace TradingHub.Indicators.Native.Structure;

// Sesiones (sección 14): las sesiones del mercado y su mapa, el barrido de Asia, los barridos de sesión, los huecos entre
// sesiones y el tipo de día. Cada sesión está en su zona horaria (con su horario de verano).

/// <summary>Zonas horarias para las ventanas de las sesiones.</summary>
public enum SessionZone
{
    [Display(Name = "UTC")] Utc,
    [Display(Name = "Nueva York")] NewYork,
    [Display(Name = "Chicago")] Chicago,
    [Display(Name = "Londres")] London,
    [Display(Name = "Fráncfort")] Frankfurt,
    [Display(Name = "Tokio")] Tokyo,
    [Display(Name = "Hong Kong")] HongKong,
    [Display(Name = "Shanghái")] Shanghai,
    [Display(Name = "Sídney")] Sydney,
}

public static class SessionClock
{
    private static readonly Dictionary<SessionZone, TimeZoneInfo> Cache = new();

    public static TimeZoneInfo Zone(SessionZone z)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(z, out var tz)) return tz;
            string[] ids = z switch
            {
                SessionZone.NewYork => new[] { "America/New_York", "Eastern Standard Time" },
                SessionZone.Chicago => new[] { "America/Chicago", "Central Standard Time" },
                SessionZone.London => new[] { "Europe/London", "GMT Standard Time" },
                SessionZone.Frankfurt => new[] { "Europe/Berlin", "W. Europe Standard Time" },
                SessionZone.Tokyo => new[] { "Asia/Tokyo", "Tokyo Standard Time" },
                SessionZone.HongKong => new[] { "Asia/Hong_Kong", "China Standard Time" },
                SessionZone.Shanghai => new[] { "Asia/Shanghai", "China Standard Time" },
                SessionZone.Sydney => new[] { "Australia/Sydney", "AUS Eastern Standard Time" },
                _ => Array.Empty<string>(),
            };
            tz = TimeZoneInfo.Utc;
            foreach (var id in ids)
            {
                try
                {
                    tz = TimeZoneInfo.FindSystemTimeZoneById(id);
                    break;
                }
                catch (Exception)
                {
                    // probar el siguiente nombre (IANA / Windows)
                }
            }

            return Cache[z] = tz;
        }
    }

    /// <summary>«HH:MM-HH:MM» en minutos del día (si el fin es menor, cruza la medianoche).</summary>
    public static (int Start, int End)? Parse(string? window)
    {
        if (string.IsNullOrWhiteSpace(window)) return null;
        var parts = window.Replace(" ", "").Split('-');
        if (parts.Length != 2) return null;
        static int? Min(string p)
        {
            p = p.Replace(":", "");
            if (p.Length is < 3 or > 4 || !int.TryParse(p, out int v)) return null;
            int h = v / 100, m = v % 100;
            return h is >= 0 and <= 24 && m is >= 0 and < 60 ? h * 60 + m : null;
        }

        return Min(parts[0]) is { } a && Min(parts[1]) is { } b ? (a, b) : null;
    }

    /// <summary>Si el instante cae en la ventana (en su zona) y el día de la sesión (el de su apertura).</summary>
    public static bool In(DateTime utc, (int Start, int End) w, TimeZoneInfo tz, out DateTime day)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        int m = local.Hour * 60 + local.Minute;
        bool wraps = w.Start > w.End;
        bool inside = wraps ? m >= w.Start || m < w.End : m >= w.Start && m < w.End;
        day = wraps && m < w.End ? local.Date.AddDays(-1) : local.Date;
        return inside;
    }

    /// <summary>El instante UTC del fin programado de la sesión que abrió el día local <paramref name="day"/>.</summary>
    public static DateTime EndUtc(DateTime day, (int Start, int End) w, TimeZoneInfo tz)
    {
        var end = day.AddMinutes(w.End);
        if (w.Start > w.End) end = end.AddDays(1);
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(end, DateTimeKind.Unspecified), tz);
        }
        catch (ArgumentException)
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(end.AddHours(1), DateTimeKind.Unspecified), tz);
        }
    }
}

/// <summary>Hasta cuatro sesiones del mercado en cajas (máximo y mínimo), con su nombre, línea media y VWAP propio.</summary>
[DisplayName("Sesiones del mercado (Tokio, Londres, Nueva York, Sídney)")]
[Category(IndicatorCategories.Sessions)]
[Description("Hasta cuatro sesiones en cajas alrededor de su máximo y su mínimo, cada una con su nombre, en su zona horaria (el horario de verano se sigue solo). Opcional: línea media, VWAP de cada sesión con bandas de ±1σ y la sesión en curso alargada hasta su fin programado.")]
public class MarketSessions : SceneIndicator
{
    public sealed class SessionSpec
    {
        public bool Show;
        public string Name = "";
        public string Window = "";
        public SessionZone Zone;
        public bool Vwap;
        public Color Color;
    }

    protected readonly SessionSpec[] Specs =
    {
        new() { Show = true, Name = "Tokio", Window = "09:00-18:00", Zone = SessionZone.Tokyo, Color = Color.FromArgb(255, 239, 83, 80) },
        new() { Show = true, Name = "Londres", Window = "08:00-17:00", Zone = SessionZone.London, Color = Color.FromArgb(255, 41, 98, 255) },
        new() { Show = true, Name = "Nueva York", Window = "08:00-17:00", Zone = SessionZone.NewYork, Color = Color.FromArgb(255, 38, 166, 154) },
        new() { Show = false, Name = "Sídney", Window = "08:00-17:00", Zone = SessionZone.Sydney, Color = Color.FromArgb(255, 255, 167, 38) },
    };

    [Display(Name = "Mostrar", GroupName = "Sesión 1", Order = 10)] public bool S1Show { get => Specs[0].Show; set => Specs[0].Show = value; }
    [Display(Name = "Nombre", GroupName = "Sesión 1", Order = 11)] public string S1Name { get => Specs[0].Name; set => Specs[0].Name = value ?? ""; }
    [Display(Name = "Horario (HH:MM-HH:MM)", GroupName = "Sesión 1", Order = 12)] public string S1Window { get => Specs[0].Window; set => Specs[0].Window = value ?? ""; }
    [Display(Name = "Zona horaria", GroupName = "Sesión 1", Order = 13)] public SessionZone S1Zone { get => Specs[0].Zone; set => Specs[0].Zone = value; }
    [Display(Name = "VWAP", GroupName = "Sesión 1", Order = 14)] public bool S1Vwap { get => Specs[0].Vwap; set => Specs[0].Vwap = value; }
    [Display(Name = "Color", GroupName = "Sesión 1", Order = 15)] public Color S1Color { get => Specs[0].Color; set => Specs[0].Color = value; }
    [Display(Name = "Mostrar", GroupName = "Sesión 2", Order = 20)] public bool S2Show { get => Specs[1].Show; set => Specs[1].Show = value; }
    [Display(Name = "Nombre", GroupName = "Sesión 2", Order = 21)] public string S2Name { get => Specs[1].Name; set => Specs[1].Name = value ?? ""; }
    [Display(Name = "Horario (HH:MM-HH:MM)", GroupName = "Sesión 2", Order = 22)] public string S2Window { get => Specs[1].Window; set => Specs[1].Window = value ?? ""; }
    [Display(Name = "Zona horaria", GroupName = "Sesión 2", Order = 23)] public SessionZone S2Zone { get => Specs[1].Zone; set => Specs[1].Zone = value; }
    [Display(Name = "VWAP", GroupName = "Sesión 2", Order = 24)] public bool S2Vwap { get => Specs[1].Vwap; set => Specs[1].Vwap = value; }
    [Display(Name = "Color", GroupName = "Sesión 2", Order = 25)] public Color S2Color { get => Specs[1].Color; set => Specs[1].Color = value; }
    [Display(Name = "Mostrar", GroupName = "Sesión 3", Order = 30)] public bool S3Show { get => Specs[2].Show; set => Specs[2].Show = value; }
    [Display(Name = "Nombre", GroupName = "Sesión 3", Order = 31)] public string S3Name { get => Specs[2].Name; set => Specs[2].Name = value ?? ""; }
    [Display(Name = "Horario (HH:MM-HH:MM)", GroupName = "Sesión 3", Order = 32)] public string S3Window { get => Specs[2].Window; set => Specs[2].Window = value ?? ""; }
    [Display(Name = "Zona horaria", GroupName = "Sesión 3", Order = 33)] public SessionZone S3Zone { get => Specs[2].Zone; set => Specs[2].Zone = value; }
    [Display(Name = "VWAP", GroupName = "Sesión 3", Order = 34)] public bool S3Vwap { get => Specs[2].Vwap; set => Specs[2].Vwap = value; }
    [Display(Name = "Color", GroupName = "Sesión 3", Order = 35)] public Color S3Color { get => Specs[2].Color; set => Specs[2].Color = value; }
    [Display(Name = "Mostrar", GroupName = "Sesión 4", Order = 40)] public bool S4Show { get => Specs[3].Show; set => Specs[3].Show = value; }
    [Display(Name = "Nombre", GroupName = "Sesión 4", Order = 41)] public string S4Name { get => Specs[3].Name; set => Specs[3].Name = value ?? ""; }
    [Display(Name = "Horario (HH:MM-HH:MM)", GroupName = "Sesión 4", Order = 42)] public string S4Window { get => Specs[3].Window; set => Specs[3].Window = value ?? ""; }
    [Display(Name = "Zona horaria", GroupName = "Sesión 4", Order = 43)] public SessionZone S4Zone { get => Specs[3].Zone; set => Specs[3].Zone = value; }
    [Display(Name = "VWAP", GroupName = "Sesión 4", Order = 44)] public bool S4Vwap { get => Specs[3].Vwap; set => Specs[3].Vwap = value; }
    [Display(Name = "Color", GroupName = "Sesión 4", Order = 45)] public Color S4Color { get => Specs[3].Color; set => Specs[3].Color = value; }

    [Display(Name = "Opacidad del relleno (%)", GroupName = "Dibujo", Order = 50)] [Range(0, 100)] public int FillOpacity { get; set; } = 12;
    [Display(Name = "Opacidad del borde (%)", GroupName = "Dibujo", Order = 51)] [Range(0, 100)] public int BorderOpacity { get; set; } = 100;
    [Display(Name = "Nombres", GroupName = "Dibujo", Order = 52)] public bool ShowNames { get; set; } = true;
    [Display(Name = "Línea media", GroupName = "Dibujo", Order = 53)] public bool Midline { get; set; }
    [Display(Name = "Alargar la sesión en curso hasta su fin", GroupName = "Dibujo", Order = 54)] public bool ExtendLive { get; set; } = true;
    [Display(Name = "Mostrar la sesión en curso", GroupName = "Dibujo", Order = 55)] public bool ShowLive { get; set; } = true;
    [Display(Name = "Días que se guardan", GroupName = "Dibujo", Order = 56)] [Range(1, 60)] public int DaysKept { get; set; } = 10;
    [Display(Name = "Bandas de ±1σ del VWAP", GroupName = "VWAP", Order = 60)] public bool VwapBands { get; set; }
    [Display(Name = "Grosor del VWAP", GroupName = "VWAP", Order = 61)] [Range(1, 6)] public int VwapWidth { get; set; } = 2;

    public MarketSessions() => ShowDashboard = false;

    /// <summary>Las sesiones de cada spec: (día, primera vela, última vela, máximo, mínimo, en curso).</summary>
    protected List<(int Spec, DateTime Day, int From, int To, double High, double Low, bool Live)> Sessions()
    {
        var res = new List<(int, DateTime, int, int, double, double, bool)>();
        for (int k = 0; k < Specs.Length; k++)
        {
            var sp = Specs[k];
            if (!sp.Show || SessionClock.Parse(sp.Window) is not { } w) continue;
            var tz = SessionClock.Zone(sp.Zone);
            DateTime? day = null;
            int from = -1, to = -1;
            double h = 0, l = 0;
            for (int i = 0; i < Bars.Count; i++)
            {
                bool inS = SessionClock.In(TimeOf(i), w, tz, out var d);
                if (inS && (day == null || d != day))
                {
                    if (day != null) res.Add((k, day.Value, from, to, h, l, false));
                    day = d;
                    from = to = i;
                    h = Bars[i].H;
                    l = Bars[i].L;
                }
                else if (inS)
                {
                    to = i;
                    h = Math.Max(h, Bars[i].H);
                    l = Math.Min(l, Bars[i].L);
                }
            }

            if (day != null)
            {
                bool live = to == Bars.Count - 1 && SessionClock.In(TimeOf(to), w, tz, out _);
                res.Add((k, day.Value, from, to, h, l, live));
            }
        }

        return res;
    }

    protected override void Build(Scene s)
    {
        if (Bars.Count < 2) return;
        var all = Sessions();
        var lastDay = all.Count == 0 ? DateTime.MinValue : all.Max(x => x.Day);
        foreach (var ss in all)
        {
            if ((lastDay - ss.Day).TotalDays >= DaysKept) continue;
            if (ss.Live && !ShowLive) continue;
            var sp = Specs[ss.Spec];
            int to = ss.To;
            if (ss.Live && ExtendLive && BarLength > TimeSpan.Zero && SessionClock.Parse(sp.Window) is { } w)
            {
                var end = SessionClock.EndUtc(ss.Day, w, SessionClock.Zone(sp.Zone));
                to = ss.To + Math.Max(0, (int)Math.Round((end - TimeOf(ss.To)).TotalSeconds / BarLength.TotalSeconds) - 1);
            }

            s.Box(ss.From, to, ss.High, ss.Low, Pal.A(sp.Color, FillOpacity * 255 / 100), Pal.A(sp.Color, BorderOpacity * 255 / 100),
                alert: ss.Live ? sp.Name : null);
            if (ShowNames) s.Tag(ss.From, ss.High, sp.Name, Color.Transparent, sp.Color);
            if (Midline) s.HLine(ss.From, to, (ss.High + ss.Low) / 2, Pal.A(sp.Color, 180), 1, LineDashStyle.Dash);
            if (sp.Vwap)
            {
                double pv = 0, vv = 0, pv2 = 0;
                var mid = new List<(int, double)>();
                var up = new List<(int, double)>();
                var dn = new List<(int, double)>();
                for (int i = ss.From; i <= ss.To; i++)
                {
                    var b = Bars[i];
                    double tp = (b.H + b.L + b.C) / 3, v = Math.Max(b.V, 1e-12);
                    pv += tp * v;
                    vv += v;
                    pv2 += tp * tp * v;
                    double vw = pv / vv, sd = Math.Sqrt(Math.Max(0, pv2 / vv - vw * vw));
                    mid.Add((i, vw));
                    up.Add((i, vw + sd));
                    dn.Add((i, vw - sd));
                }

                s.Paths.Add(new Scene.PathShape(mid, sp.Color, VwapWidth));
                if (VwapBands)
                {
                    s.Paths.Add(new Scene.PathShape(up, Pal.A(sp.Color, 140), 1, LineDashStyle.Dash));
                    s.Paths.Add(new Scene.PathShape(dn, Pal.A(sp.Color, 140), 1, LineDashStyle.Dash));
                }
            }
        }
    }
}

/// <summary>Mapa de sesiones: Asia, Londres y Nueva York en cajas con su etiqueta, la sesión en curso creciendo.</summary>
[DisplayName("Mapa de sesiones (Asia, Londres, Nueva York)")]
[Category(IndicatorCategories.Sessions)]
[Description("Asia, Londres y Nueva York en cajas alrededor de su máximo y su mínimo, con una etiqueta en cada una; la sesión en curso crece vela a vela. Se ve qué sesión armó el rango, dónde se superpusieron y cuánto lleva la actual.")]
public class SessionMap : MarketSessions
{
    public SessionMap()
    {
        Specs[0].Name = "Asia";
        Specs[0].Window = "00:00-08:00";
        Specs[0].Zone = SessionZone.Utc;
        Specs[1].Window = "07:00-16:00";
        Specs[1].Zone = SessionZone.Utc;
        Specs[2].Window = "13:30-20:00";
        Specs[2].Zone = SessionZone.Utc;
        Specs[3].Show = false;
        DaysKept = 5;
        ExtendLive = false;
    }
}

/// <summary>Barrido del rango de Asia y entrada con el cambio de estructura interna, con stop y objetivo.</summary>
[DisplayName("Barridos de la sesión de Asia")]
[Category(IndicatorCategories.Sessions)]
[Description("El rango de Asia (por defecto 19:00–04:00 de Nueva York). Fuera de él, el primer barrido de cada lado; después, el cambio de estructura interna (CHoCH) en contra del barrido da la entrada, con filtro de tendencia (EMA), stop en el extremo del barrido y objetivo en múltiplos del riesgo.")]
public class AsianSessionSweeps : SceneIndicator
{
    public enum TzMode
    {
        [Display(Name = "Nueva York fijo (UTC−5)")] FixedNy,
        [Display(Name = "Nueva York (con horario de verano)")] NewYork,
        [Display(Name = "UTC")] Utc,
    }

    [Display(Name = "Horario de Asia (HH:MM-HH:MM)", GroupName = "Sesión", Order = 10)] public string AsiaWindow { get; set; } = "19:00-04:00";
    [Display(Name = "Zona horaria", GroupName = "Sesión", Order = 11)] public TzMode Zone { get; set; } = TzMode.FixedNy;
    [Display(Name = "Sesiones que se dibujan", GroupName = "Sesión", Order = 12)] [Range(1, 100)] public int MaxSessions { get; set; } = 20;
    [Display(Name = "Barridos", GroupName = "Señales", Order = 20)] public bool ShowSweeps { get; set; } = true;
    [Display(Name = "Estructura interna", GroupName = "Señales", Order = 21)] public bool ShowStructure { get; set; } = true;
    [Display(Name = "Largo de la estructura interna", GroupName = "Señales", Order = 22)] [Range(1, 20)] public int InternalLength { get; set; } = 2;
    [Display(Name = "Ruptura por mecha (si no, por cierre)", GroupName = "Señales", Order = 23)] public bool WickBreak { get; set; }
    [Display(Name = "Cerrar al empezar otra sesión", GroupName = "Trade", Order = 30)] public bool CloseAtNewSession { get; set; } = true;
    [Display(Name = "Stop (× riesgo)", GroupName = "Trade", Order = 31)] [Range(0.1, 10)] public decimal StopMult { get; set; } = 1;
    [Display(Name = "Objetivo (× riesgo)", GroupName = "Trade", Order = 32)] [Range(0.1, 20)] public decimal RewardMult { get; set; } = 2;
    [Display(Name = "Velas después del barrido", GroupName = "Trade", Order = 33)] [Range(1, 200)] public int MaxBarsAfter { get; set; } = 10;
    [Display(Name = "Margen del stop (ticks)", GroupName = "Trade", Order = 34)] [Range(0, 1000)] public int StopBufferTicks { get; set; }
    [Display(Name = "Filtro de tendencia (EMA)", GroupName = "Trade", Order = 35)] public bool TrendFilter { get; set; } = true;
    [Display(Name = "EMA de la tendencia", GroupName = "Trade", Order = 36)] [Range(2, 1000)] public int TrendLength { get; set; } = 200;
    [Display(Name = "Caja de la sesión", GroupName = "Dibujo", Order = 40)] public bool ShowSessionBox { get; set; } = true;
    [Display(Name = "Cajas de los trades", GroupName = "Dibujo", Order = 41)] public bool ShowTradeBoxes { get; set; } = true;
    [Display(Name = "Fondo en la sesión", GroupName = "Dibujo", Order = 42)] public bool ShowBackground { get; set; } = true;
    [Display(Name = "Sesión", GroupName = "Colores", Order = 50)] public Color SessionColor { get; set; } = Color.FromArgb(255, 255, 167, 38);
    [Display(Name = "Alcista", GroupName = "Colores", Order = 51)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 52)] public Color BearColor { get; set; } = Pal.Bear;

    private bool InAsia(DateTime utc, (int Start, int End) w)
    {
        DateTime wall = Zone switch
        {
            TzMode.Utc => utc,
            TzMode.NewYork => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), SessionClock.Zone(SessionZone.NewYork)),
            _ => utc.AddHours(-5),
        };
        int m = wall.Hour * 60 + wall.Minute;
        return w.Start > w.End ? m >= w.Start || m < w.End : m >= w.Start && m < w.End;
    }

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < 10 || SessionClock.Parse(AsiaWindow) is not { } w) return;
        var ema = Ta.Ema(Bars.Select(b => b.C).ToList(), TrendLength);
        decimal tick = ChartInfo?.PriceChartContainer.Step ?? 0.01m;
        double buffer = (double)tick * StopBufferTicks;
        var sessions = new List<(int From, int To, double H, double L)>();
        bool prevIn = false;
        int from = -1, to = -1;
        double sh = 0, sl = 0;
        bool hiSwept = false, loSwept = false;
        int sweepDir = 0, sweepBar = -1;
        double sweepExt = 0;
        // estructura interna
        var piv = Ta.Pivots(Bars, InternalLength, InternalLength, last).ToLookup(p => p.Confirm);
        double ih = double.NaN, il = double.NaN;
        int ihBar = -1, ilBar = -1, itrend = 0;
        bool ihX = true, ilX = true;
        int pos = 0, entryBar = -1, setups = 0, wins = 0, losses = 0;
        double entry = 0, stop = 0, target = 0;
        for (int i = 1; i <= last; i++)
        {
            var b = Bars[i];
            bool inS = InAsia(TimeOf(i), w);
            if (inS && !prevIn)
            {
                if (from >= 0) sessions.Add((from, to, sh, sl));
                from = to = i;
                sh = b.H;
                sl = b.L;
                hiSwept = loSwept = false;
                sweepDir = 0;
                if (pos != 0 && CloseAtNewSession)
                {
                    s.Mark(i, b.O, MarkKind.Cross, Pal.Muted, "Cierre", pos > 0);
                    pos = 0;
                }
            }
            else if (inS)
            {
                to = i;
                sh = Math.Max(sh, b.H);
                sl = Math.Min(sl, b.L);
            }

            if (inS && ShowBackground) s.Tints[i] = Pal.A(SessionColor, 10);

            foreach (var p in piv[i])
            {
                if (p.High) { ih = p.Price; ihBar = p.Bar; ihX = false; }
                else { il = p.Price; ilBar = p.Bar; ilX = false; }
            }

            // gestión del trade antes que las entradas: una vela de salida no entra
            if (pos != 0)
            {
                bool hitStop = pos > 0 ? b.L <= stop : b.H >= stop, hitTarget = pos > 0 ? b.H >= target : b.L <= target;
                if (hitStop || hitTarget)
                {
                    if (hitStop) losses++;
                    else wins++;
                    s.Mark(i, pos > 0 ? b.H : b.L, MarkKind.Cross, hitStop ? BearColor : BullColor, hitStop ? "Stop" : "Objetivo", pos > 0);
                    if (ShowTradeBoxes)
                    {
                        s.Box(entryBar, i, target, entry, Pal.A(BullColor, 25), Pal.A(BullColor, 80));
                        s.Box(entryBar, i, entry, stop, Pal.A(BearColor, 25), Pal.A(BearColor, 80));
                    }

                    pos = 0;
                }
            }

            // barridos del rango (fuera de la sesión, una vez por lado, con la mecha anterior adentro)
            if (!inS && from >= 0)
            {
                var pb = Bars[i - 1];
                if (!hiSwept && b.H > sh && pb.H <= sh)
                {
                    hiSwept = true;
                    sweepDir = -1;
                    sweepBar = i;
                    sweepExt = b.H;
                    if (ShowSweeps) s.Tag(i, b.H, "Barrido de Asia", Pal.A(BearColor, 200), Color.White);
                }
                else if (!loSwept && b.L < sl && pb.L >= sl)
                {
                    loSwept = true;
                    sweepDir = 1;
                    sweepBar = i;
                    sweepExt = b.L;
                    if (ShowSweeps) s.Tag(i, b.L, "Barrido de Asia", Pal.A(BullColor, 200), Color.White, false);
                }

                if (sweepDir < 0) sweepExt = Math.Max(sweepExt, b.H);
                if (sweepDir > 0) sweepExt = Math.Min(sweepExt, b.L);
            }

            // CHoCH interno: un cierre (o mecha) a través del último swing interno, contra la tendencia interna
            int brk = 0;
            if (!ilX && !double.IsNaN(il) && i > ilBar + InternalLength && (WickBreak ? b.L < il : b.C < il))
            {
                ilX = true;
                if (itrend >= 0) brk = -1;
                if (ShowStructure && itrend >= 0) { s.Segment(ilBar, i, il, il, BearColor, 1, LineDashStyle.Dash); s.Tag((ilBar + i) / 2, il, "CHoCH", Color.Transparent, BearColor, false); }
                itrend = -1;
            }
            else if (!ihX && !double.IsNaN(ih) && i > ihBar + InternalLength && (WickBreak ? b.H > ih : b.C > ih))
            {
                ihX = true;
                if (itrend <= 0) brk = 1;
                if (ShowStructure && itrend <= 0) { s.Segment(ihBar, i, ih, ih, BullColor, 1, LineDashStyle.Dash); s.Tag((ihBar + i) / 2, ih, "CHoCH", Color.Transparent, BullColor); }
                itrend = 1;
            }

            if (pos == 0 && sweepDir != 0 && brk == sweepDir && i - sweepBar <= MaxBarsAfter)
            {
                bool lng = sweepDir > 0;
                if (!TrendFilter || (lng ? b.C > ema[i] : b.C < ema[i]))
                {
                    pos = lng ? 1 : -1;
                    entry = b.C;
                    entryBar = i;
                    double risk = Math.Abs(entry - sweepExt) + buffer;
                    stop = lng ? entry - risk * (double)StopMult : entry + risk * (double)StopMult;
                    target = lng ? entry + risk * (double)RewardMult : entry - risk * (double)RewardMult;
                    setups++;
                    s.Mark(i, lng ? b.L : b.H, lng ? MarkKind.TriangleUp : MarkKind.TriangleDown, lng ? BullColor : BearColor, lng ? "Compra" : "Venta", !lng);
                }

                sweepDir = 0;
            }

            prevIn = inS;
        }

        if (from >= 0) sessions.Add((from, to, sh, sl));
        if (ShowSessionBox)
            foreach (var (f, t, h, l) in sessions.TakeLast(MaxSessions))
            {
                bool latest = f == sessions[^1].From;
                s.Box(f, t, h, l, Pal.A(SessionColor, 22), Pal.A(SessionColor, 140), "Asia");
                if (latest)
                {
                    s.HLine(f, -1, h, SessionColor, 1, LineDashStyle.Dash, "Máximo de Asia " + Price(h), "Máximo de Asia");
                    s.HLine(f, -1, l, SessionColor, 1, LineDashStyle.Dash, "Mínimo de Asia " + Price(l), "Mínimo de Asia");
                }
            }

        if (pos != 0)
        {
            s.HLine(entryBar, -1, stop, BearColor, 1, LineDashStyle.Dash, "Stop " + Price(stop), "Stop");
            s.HLine(entryBar, -1, target, BullColor, 1, LineDashStyle.Dash, "Objetivo " + Price(target), "Objetivo");
        }

        s.DashTitle = "Barridos de Asia";
        s.Row("Posición", pos > 0 ? "comprado" : pos < 0 ? "vendido" : "afuera", pos > 0 ? BullColor : pos < 0 ? BearColor : default);
        s.Row("Entradas", setups.ToString());
        s.Row("Objetivos / stops", $"{wins} / {losses}");
        if (sessions.Count > 0) s.Row("Rango de Asia", $"{Price(sessions[^1].L)} – {Price(sessions[^1].H)}");
    }
}

/// <summary>Barridos de los extremos de las sesiones (Asia, Londres, NY mañana y tarde) con zonas de margen ATR, CHoCH y FVG.</summary>
[DisplayName("Barridos de sesión (Asia, Londres, Nueva York)")]
[Category(IndicatorCategories.Sessions)]
[Description("Al cerrar cada sesión, su máximo y su mínimo quedan defendidos. Una vela que los pasa abriendo del otro lado arma una zona de barrido (con un margen en ATR); si el precio cierra decidido más allá, la zona muere (era una ruptura), y si se queda trabada vuelve a armarse. Opcional: cambios de estructura (CHoCH) y FVG.")]
public class SessionSweeps : SceneIndicator
{
    [Display(Name = "Zonas de barrido arriba", GroupName = "Barridos", Order = 10)] public bool Buyside { get; set; } = true;
    [Display(Name = "Margen arriba (ATR 21)", GroupName = "Barridos", Order = 11)] [Range(0.1, 10)] public decimal BuysideMargin { get; set; } = 1.3m;
    [Display(Name = "Zonas de barrido abajo", GroupName = "Barridos", Order = 12)] public bool Sellside { get; set; } = true;
    [Display(Name = "Margen abajo (ATR 21)", GroupName = "Barridos", Order = 13)] [Range(0.1, 10)] public decimal SellsideMargin { get; set; } = 1.3m;
    [Display(Name = "Velas trabadas para volver a armar", GroupName = "Barridos", Order = 14)] [Range(1, 100)] public int StallBars { get; set; } = 5;
    [Display(Name = "Un barrido por sesión", GroupName = "Barridos", Order = 15)] public bool DetectOnce { get; set; }
    [Display(Name = "Asia (Tokio 9–18)", GroupName = "Sesiones", Order = 20)] public bool Asia { get; set; } = true;
    [Display(Name = "Londres (8–17)", GroupName = "Sesiones", Order = 21)] public bool London { get; set; } = true;
    [Display(Name = "Nueva York mañana (8–13)", GroupName = "Sesiones", Order = 22)] public bool NyAm { get; set; } = true;
    [Display(Name = "Nueva York tarde (13–19)", GroupName = "Sesiones", Order = 23)] public bool NyPm { get; set; } = true;
    [Display(Name = "Cambios de estructura (CHoCH)", GroupName = "Extras", Order = 30)] public bool ShowMss { get; set; }
    [Display(Name = "Largo de los pivotes", GroupName = "Extras", Order = 31)] [Range(2, 50)] public int MssLength { get; set; } = 8;
    [Display(Name = "FVG", GroupName = "Extras", Order = 32)] public bool ShowFvg { get; set; }
    [Display(Name = "Filtro del FVG (ATR 144)", GroupName = "Extras", Order = 33)] [Range(0, 5)] public decimal FvgFilter { get; set; } = 1;
    [Display(Name = "Cajas de las sesiones", GroupName = "Dibujo", Order = 40)] public bool ShowSessionBoxes { get; set; } = true;
    [Display(Name = "Nombres de las sesiones", GroupName = "Dibujo", Order = 41)] public bool ShowSessionNames { get; set; } = true;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 50)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 51)] public Color BearColor { get; set; } = Pal.Bear;

    private static readonly (string Name, SessionZone Zone, int Start, int End, Color Color)[] Defs =
    {
        ("Asia", SessionZone.Tokyo, 9 * 60, 18 * 60, Color.FromArgb(255, 255, 167, 38)),
        ("Londres", SessionZone.London, 8 * 60, 17 * 60, Color.FromArgb(255, 41, 98, 255)),
        ("NY mañana", SessionZone.NewYork, 8 * 60, 13 * 60, Color.FromArgb(255, 38, 166, 154)),
        ("NY tarde", SessionZone.NewYork, 13 * 60, 19 * 60, Color.FromArgb(255, 171, 71, 188)),
    };

    private sealed class Zone
    {
        public bool Live, Armed;
        public int From, Right;
        public double Level, Margin;
    }

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < 30) return;
        if (BarLength > TimeSpan.FromMinutes(5) || BarLength <= TimeSpan.Zero)
        {
            s.DashTitle = "Barridos de sesión";
            s.Row("Período", "usar velas de hasta 5 min");
            return;
        }

        var atr21 = Ta.Atr(Bars, 21);
        var atr144 = Ta.Atr(Bars, 144);
        bool[] on = { Asia, London, NyAm, NyPm };
        var prev = new bool[4];
        var sFrom = new int[4];
        var sh = new double[4];
        var sl = new double[4];
        double pSH = double.NaN, pSL = double.NaN;
        var buy = new Zone();
        var sell = new Zone();
        var zones = new List<(int From, int To, double Top, double Bottom, bool Up, bool Fake)>();
        int buyCount = 0, sellCount = 0;
        string defended = "—";
        // estructura y FVG
        var piv = Ta.Pivots(Bars, MssLength, MssLength, last).ToLookup(p => p.Confirm);
        double ph = double.NaN, pl = double.NaN;
        bool phX = true, plX = true;
        int shift = 0;
        for (int i = 1; i <= last; i++)
        {
            var b = Bars[i];
            var t = TimeOf(i);
            for (int k = 0; k < 4; k++)
            {
                if (!on[k]) continue;
                var tz = SessionClock.Zone(Defs[k].Zone);
                bool act = SessionClock.In(t, (Defs[k].Start, Defs[k].End), tz, out _);
                if (act && !prev[k])
                {
                    sFrom[k] = i;
                    sh[k] = b.H;
                    sl[k] = b.L;
                    if (ShowSessionNames) s.Tag(i, b.L, Defs[k].Name, Color.Transparent, Defs[k].Color, false);
                }
                else if (act)
                {
                    sh[k] = Math.Max(sh[k], b.H);
                    sl[k] = Math.Min(sl[k], b.L);
                }
                else if (prev[k])
                {
                    // cerró: su máximo y su mínimo quedan defendidos
                    if (ShowSessionBoxes)
                    {
                        s.Box(sFrom[k], i - 1, sh[k], sl[k], Pal.A(Defs[k].Color, 18), Pal.A(Defs[k].Color, 110));
                        s.HLine(sFrom[k], i - 1, (sh[k] + sl[k]) / 2, Pal.A(Defs[k].Color, 120), 1, LineDashStyle.Dot);
                    }

                    pSH = sh[k];
                    pSL = sl[k];
                    defended = Defs[k].Name;
                    buy = new Zone();
                    sell = new Zone();
                }

                prev[k] = act;
            }

            if (Buyside && !double.IsNaN(pSH)) Step(buy, true, pSH, (double)BuysideMargin * atr21[i]);
            if (Sellside && !double.IsNaN(pSL)) Step(sell, false, pSL, (double)SellsideMargin * atr21[i]);

            void Step(Zone z, bool up, double level, double m)
            {
                if (!z.Armed)
                {
                    bool create = up ? b.H > level && b.O < level : b.L < level && b.O > level;
                    if (!create) return;
                    z.Armed = z.Live = true;
                    z.From = i;
                    z.Right = i + 1;
                    z.Level = level;
                    z.Margin = m;
                    if (up) buyCount++;
                    else sellCount++;
                    s.Mark(i, up ? b.H : b.L, MarkKind.Circle, up ? BearColor : BullColor, null, up);
                    return;
                }

                if (!z.Live) return;
                bool inside = up ? b.L > level - m && b.H < level + m : b.H < level + m && b.L > level - m;
                if (inside)
                {
                    if (up ? b.H > level : b.L < level) z.Right = i;
                }
                else if (up ? b.C > level + m * 1.5 : b.C < level - m * 1.5)
                {
                    zones.Add((z.From, i, up ? level + m : level, up ? level : level - m, up, true));
                    z.Live = false;
                }
                else if (i - z.Right >= StallBars)
                {
                    zones.Add((z.From, z.Right, up ? level + m : level, up ? level : level - m, up, false));
                    z.Live = false;
                    if (!DetectOnce) z.Armed = false;
                }
            }

            if (ShowMss)
            {
                foreach (var p in piv[i])
                {
                    if (p.High) { ph = p.Price; phX = false; }
                    else { pl = p.Price; plX = false; }
                }

                if (!phX && b.C > ph)
                {
                    phX = true;
                    if (shift < 0) s.Tag(i, b.L, "CHoCH", Pal.A(BullColor, 200), Color.White, false);
                    shift = 1;
                }

                if (!plX && b.C < pl)
                {
                    plX = true;
                    if (shift > 0) s.Tag(i, b.H, "CHoCH", Pal.A(BearColor, 200), Color.White);
                    shift = -1;
                }
            }

            if (ShowFvg && i >= 2)
            {
                var a = Bars[i - 2];
                double thr = (double)FvgFilter * atr144[i];
                if (b.L - a.H > thr && b.L > a.H) s.Box(i - 1, Math.Min(i + 10, LastBar), b.L, a.H, Pal.A(BullColor, 35), Pal.A(BullColor, 110), "FVG");
                else if (a.L - b.H > thr && b.H < a.L) s.Box(i - 1, Math.Min(i + 10, LastBar), a.L, b.H, Pal.A(BearColor, 35), Pal.A(BearColor, 110), "FVG");
            }
        }

        foreach (var z in zones.TakeLast(60))
        {
            var c = z.Up ? BearColor : BullColor;
            s.Box(z.From, z.To, z.Top, z.Bottom, Pal.A(z.Fake ? Pal.Muted : c, 30), Pal.A(z.Fake ? Pal.Muted : c, 110), z.Fake ? "Ruptura" : "Barrido");
        }

        foreach (var (z, up) in new[] { (buy, true), (sell, false) })
            if (z.Live)
                s.Box(z.From, -1, up ? z.Level + z.Margin : z.Level, up ? z.Level : z.Level - z.Margin, Pal.A(up ? BearColor : BullColor, 40), up ? BearColor : BullColor, "Barrido", alert: up ? "Zona de barrido arriba" : "Zona de barrido abajo");
        if (!double.IsNaN(pSH)) s.HLine(LastBar - 20, -1, pSH, Pal.A(BearColor, 200), 1, LineDashStyle.Dash, "Máximo defendido (" + defended + ")", "Máximo defendido");
        if (!double.IsNaN(pSL)) s.HLine(LastBar - 20, -1, pSL, Pal.A(BullColor, 200), 1, LineDashStyle.Dash, "Mínimo defendido (" + defended + ")", "Mínimo defendido");
        s.DashTitle = "Barridos de sesión";
        s.Row("Defendidos", defended);
        s.Row("Barridos arriba / abajo", $"{buyCount} / {sellCount}");
        s.Row("Zona arriba", buy.Live ? "viva" : "—", buy.Live ? BearColor : default);
        s.Row("Zona abajo", sell.Live ? "viva" : "—", sell.Live ? BullColor : default);
    }
}

/// <summary>Huecos al abrir cada sesión (día de trading): si se rellenan, cuánto se alejan y estadísticas.</summary>
[DisplayName("Huecos de sesión y su relleno")]
[Category(IndicatorCategories.Sessions)]
[Description("El hueco entre la apertura de cada sesión y el cierre anterior: alcista o bajista, con o sin solapamiento con la mecha anterior. Marca cuándo se rellena (vuelve al cierre anterior), la máxima desviación en contra y estadísticas de cuántos se rellenan y cuántos dan vuelta.")]
public class SessionGapFill : SceneIndicator
{
    public enum GapKind
    {
        [Display(Name = "Todos")] All,
        [Display(Name = "Con solapamiento")] Overlapping,
        [Display(Name = "Sin solapamiento (rompe la mecha)")] Clean,
    }

    [Display(Name = "Huecos", GroupName = "Cálculo", Order = 10)] public GapKind Filter { get; set; } = GapKind.All;
    [Display(Name = "Estadísticas", GroupName = "Dibujo", Order = 20)] public bool ShowStats { get; set; } = true;
    [Display(Name = "Rellenados alcistas", GroupName = "Dibujo", Order = 21)] public bool ShowFilledBull { get; set; } = true;
    [Display(Name = "Rellenados bajistas", GroupName = "Dibujo", Order = 22)] public bool ShowFilledBear { get; set; } = true;
    [Display(Name = "Sin rellenar", GroupName = "Dibujo", Order = 23)] public bool ShowUnfilled { get; set; } = true;
    [Display(Name = "Máxima desviación", GroupName = "Dibujo", Order = 24)] public bool ShowDeviation { get; set; } = true;
    [Display(Name = "Nivel (cierre anterior)", GroupName = "Dibujo", Order = 25)] public bool ShowLevel { get; set; } = true;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 30)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 31)] public Color BearColor { get; set; } = Pal.Bear;

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < 3) return;
        int nBull = 0, nBear = 0, fBull = 0, fBear = 0, rBull = 0, rBear = 0;
        int start = -1, fillBar = -1;
        bool bull = false, valid = false;
        double open = 0, prevClose = 0, dev = 0;
        void Harvest(int endBar)
        {
            if (!valid || start < 0) return;
            bool filled = fillBar >= 0;
            if (bull) { nBull++; if (filled) fBull++; if (Bars[endBar].C < open) rBull++; }
            else { nBear++; if (filled) fBear++; if (Bars[endBar].C > open) rBear++; }
            var c = bull ? BullColor : BearColor;
            bool show = filled ? (bull ? ShowFilledBull : ShowFilledBear) : ShowUnfilled;
            if (!show) return;
            int to = filled ? fillBar : endBar;
            s.Box(start, to, Math.Max(open, prevClose), Math.Min(open, prevClose), Pal.A(c, filled ? 25 : 45), Pal.A(c, filled ? 80 : 150));
            if (ShowLevel) s.HLine(start, endBar, prevClose, Pal.A(c, 160), 1, LineDashStyle.Dot);
            if (ShowDeviation) s.HLine(start, endBar, dev, Pal.A(Pal.Muted, 150), 1, LineDashStyle.Dash);
        }

        for (int i = 1; i <= last; i++)
        {
            var b = Bars[i];
            var pb = Bars[i - 1];
            if (DayKey[i] != DayKey[i - 1])
            {
                Harvest(i - 1);
                valid = false;
                start = i;
                fillBar = -1;
                open = b.O;
                prevClose = pb.C;
                if (b.O != pb.C)
                {
                    bull = b.O > pb.C;
                    bool overlap = bull ? b.L < pb.H : b.H > pb.L;
                    bool clean = bull ? b.L > pb.H : b.H < pb.L;
                    valid = (overlap || clean) && Filter switch { GapKind.Overlapping => overlap, GapKind.Clean => clean, _ => true };
                    dev = bull ? b.H : b.L;
                }
            }

            if (!valid || start < 0) continue;
            dev = bull ? Math.Max(dev, b.H) : Math.Min(dev, b.L);
            if (fillBar < 0 && (bull ? b.L <= prevClose : b.H >= prevClose))
            {
                fillBar = i;
                s.Mark(i, prevClose, bull ? MarkKind.TriangleDown : MarkKind.TriangleUp, bull ? BullColor : BearColor, "Rellenado", !bull);
            }
        }

        // el hueco de la sesión en curso
        if (valid && start >= 0)
        {
            var c = bull ? BullColor : BearColor;
            s.Box(start, fillBar >= 0 ? fillBar : -1, Math.Max(open, prevClose), Math.Min(open, prevClose), Pal.A(c, 40), c, "Hueco", alert: fillBar < 0 ? "Hueco de la sesión" : null);
            if (ShowLevel) s.HLine(start, -1, prevClose, c, 1, LineDashStyle.Dot, "Cierre anterior " + Price(prevClose), "Cierre de la sesión anterior");
            if (ShowDeviation) s.HLine(start, -1, dev, Pal.Muted, 1, LineDashStyle.Dash, "Máx. desviación " + Price(dev));
        }

        if (!ShowStats) return;
        static string Pct(int a, int n) => n == 0 ? "—" : $"{100.0 * a / n:0} % ({a}/{n})";
        s.DashTitle = "Huecos de sesión";
        s.Row("Alcistas rellenados", Pct(fBull, nBull), BullColor);
        s.Row("Bajistas rellenados", Pct(fBear, nBear), BearColor);
        s.Row("Alcistas que dan vuelta", Pct(rBull, nBull));
        s.Row("Bajistas que dan vuelta", Pct(rBear, nBear));
        if (valid) s.Row("Hueco de hoy", (bull ? "alcista" : "bajista") + (fillBar >= 0 ? " · rellenado" : " · abierto"), bull ? BullColor : BearColor);
    }
}
