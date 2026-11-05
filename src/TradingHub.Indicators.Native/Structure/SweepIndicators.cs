using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

namespace TradingHub.Indicators.Native.Structure;

// Barridos y rupturas de la sección 12: barridos de liquidez, barrido de sesión con FVG invertido, FVG del máximo / mínimo de
// ayer, máximos y mínimos iguales con FVG, rupturas al rellenar huecos y zonas con pruebas y re-pruebas. Todo se decide con
// velas cerradas: una marca puesta no se mueve ni desaparece.

/// <summary>Barridos de liquidez en los máximos y mínimos confirmados: por mecha y por re-prueba después de romper.</summary>
[DisplayName("Barridos de liquidez (mecha y re-prueba)")]
[Category(IndicatorCategories.Structure)]
[Description("Cada máximo y mínimo confirmado es un nivel de liquidez. Barrido por mecha: la vela lo pasa y cierra del otro lado. Barrido por re-prueba: después de romperlo vuelve a probarlo y lo rechaza. Dibuja el área barrida hasta que un cierre la atraviesa.")]
public class LiquiditySweeps : SceneIndicator
{
    public enum SweepMode
    {
        [Display(Name = "Mechas y re-pruebas")] All,
        [Display(Name = "Solo mechas")] OnlyWicks,
        [Display(Name = "Solo rupturas y re-pruebas")] OnlyRetests,
    }

    [Display(Name = "Velas a cada lado del swing", GroupName = "Cálculo", Order = 10)] [Range(1, 100)] public int Length { get; set; } = 5;
    [Display(Name = "Qué barridos", GroupName = "Cálculo", Order = 11)] public SweepMode Mode { get; set; } = SweepMode.All;
    [Display(Name = "Alargar el área hasta que se rompa", GroupName = "Cálculo", Order = 12)] public bool Extend { get; set; } = true;
    [Display(Name = "Velas que vive un área", GroupName = "Cálculo", Order = 13)] [Range(1, 5000)] public int MaxBars { get; set; } = 300;
    [Display(Name = "Áreas", GroupName = "Dibujo", Order = 20)] public bool ShowZones { get; set; } = true;
    [Display(Name = "Etiquetas (mecha / re-prueba)", GroupName = "Dibujo", Order = 21)] public bool ShowTriggers { get; set; } = true;
    [Display(Name = "Fondo en la vela del barrido", GroupName = "Dibujo", Order = 22)] public bool ShowBackground { get; set; } = true;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 30)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 31)] public Color BearColor { get; set; } = Pal.Bear;

    private sealed class Lvl
    {
        public double P;
        public int Bar;
        public bool High, Broken, Wicked, Dead;
    }

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < Length * 2 + 1) return;
        var pivots = Ta.Pivots(Bars, Length, Length, last).ToLookup(p => p.Confirm);
        var levels = new List<Lvl>();
        var zones = new List<(int From, int To, double Top, double Bottom, bool Bull)>();
        int bullW = 0, bearW = 0, bullR = 0, bearR = 0;
        string lastSweep = "—";
        bool onlyWicks = Mode == SweepMode.OnlyWicks, onlyRetest = Mode == SweepMode.OnlyRetests;
        for (int i = 0; i <= last; i++)
        {
            foreach (var p in pivots[i].OrderByDescending(p => p.High)) levels.Add(new Lvl { P = p.Price, Bar = p.Bar, High = p.High });
            var b = Bars[i];
            bool swept = false;
            void Sweep(bool bull, double top, double bottom, bool wick)
            {
                zones.Add((i, -1, top, bottom, bull));
                swept = true;
                if (ShowTriggers) s.Tag(i, bull ? b.L : b.H, wick ? "Mecha" : "Re-prueba", Pal.A(bull ? BullColor : BearColor, 200), Color.White, !bull);
                if (wick && bull) bullW++;
                else if (wick) bearW++;
                else if (bull) bullR++;
                else bearR++;
                lastSweep = (bull ? "alcista" : "bajista") + " · " + (wick ? "mecha" : "re-prueba");
            }

            foreach (var lv in levels)
            {
                if (lv.Dead || i <= lv.Bar + Length) continue;
                if (i - lv.Bar > 2000)
                {
                    lv.Dead = true;
                    continue;
                }

                if (lv.High)
                {
                    if (!lv.Broken)
                    {
                        if (b.C > lv.P)
                        {
                            if (onlyWicks) lv.Dead = true;
                            else lv.Broken = true;
                        }
                        else if (!onlyRetest && !lv.Wicked && b.H > lv.P && b.C < lv.P)
                        {
                            Sweep(false, b.H, lv.P, true);
                            lv.Wicked = true;
                        }
                    }
                    else if (b.C < lv.P) lv.Dead = true;
                    else if (!onlyWicks && b.L < lv.P && b.C > lv.P)
                    {
                        Sweep(true, lv.P, b.L, false);
                        lv.Dead = true;
                    }
                }
                else
                {
                    if (!lv.Broken)
                    {
                        if (b.C < lv.P)
                        {
                            if (onlyWicks) lv.Dead = true;
                            else lv.Broken = true;
                        }
                        else if (!onlyRetest && !lv.Wicked && b.L < lv.P && b.C > lv.P)
                        {
                            Sweep(true, lv.P, b.L, true);
                            lv.Wicked = true;
                        }
                    }
                    else if (b.C > lv.P) lv.Dead = true;
                    else if (!onlyWicks && b.H > lv.P && b.C < lv.P)
                    {
                        Sweep(false, b.H, lv.P, false);
                        lv.Dead = true;
                    }
                }
            }

            levels.RemoveAll(l => l.Dead);
            if (swept && ShowBackground) s.Tints[i] = Pal.A(BearColor, 18);

            // las áreas vivas terminan con un cierre del otro lado o por edad
            for (int z = 0; z < zones.Count; z++)
            {
                var zn = zones[z];
                if (zn.To >= 0 || zn.From == i) continue;
                bool broken = zn.Bull ? b.C < zn.Bottom : b.C > zn.Top;
                if (broken || !Extend || i - zn.From >= MaxBars) zones[z] = zn with { To = broken || !Extend ? i : zn.From + MaxBars };
            }
        }

        if (ShowZones)
            foreach (var zn in zones.TakeLast(80))
            {
                var c = zn.Bull ? BullColor : BearColor;
                bool live = zn.To < 0;
                s.Box(zn.From, live ? -1 : zn.To, zn.Top, zn.Bottom, Pal.A(c, live ? 40 : 20), Pal.A(c, live ? 140 : 60), alert: live ? "Área barrida" : null);
            }

        s.DashTitle = "Barridos de liquidez";
        s.Row("Mechas alcistas / bajistas", $"{bullW} / {bearW}");
        s.Row("Re-pruebas alcistas / bajistas", $"{bullR} / {bearR}");
        s.Row("Último barrido", lastSweep);
        s.Row("Niveles vivos", levels.Count.ToString());
    }
}

/// <summary>Barrido del rango de una sesión, vuelta adentro, ruptura de la línea de tendencia y entrada en el FVG invertido.</summary>
[DisplayName("Barrido de sesión + FVG invertido")]
[Category(IndicatorCategories.Structure)]
[Description("El rango de una sesión (Sídney, Tokio, Londres o Nueva York, en UTC). Fuera de ella, una vela barre el máximo o el mínimo; si vuelve a cerrar adentro a tiempo es un barrido válido. La ruptura de la línea de tendencia confirma la entrada, con stop en el FVG invertido (o a un ATR) y objetivo en múltiplos de ATR.")]
public class SessionSweepInverseFvg : SceneIndicator
{
    public enum StopKind
    {
        [Display(Name = "Borde del FVG invertido (o ATR)")] InverseFvg,
        [Display(Name = "Distancia en ATR")] Atr,
    }

    [Display(Name = "Sesión", GroupName = "Cálculo", Order = 10)] public Ta.WorldSession Session { get; set; } = Ta.WorldSession.London;
    [Display(Name = "Velas para volver al rango", GroupName = "Cálculo", Order = 11)] [Range(1, 200)] public int SweepBars { get; set; } = 10;
    [Display(Name = "ATR", GroupName = "Cálculo", Order = 12)] [Range(1, 200)] public int AtrLength { get; set; } = 14;
    [Display(Name = "Barrido máximo (ATR)", GroupName = "Cálculo", Order = 13)] [Range(0.1, 50)] public decimal MaxSweepAtr { get; set; } = 5;
    [Display(Name = "Stop", GroupName = "Cálculo", Order = 14)] public StopKind Stop { get; set; } = StopKind.InverseFvg;
    [Display(Name = "Estricto (un barrido por sesión)", GroupName = "Cálculo", Order = 15)] public bool Strict { get; set; }
    [Display(Name = "Objetivo (ATR)", GroupName = "Cálculo", Order = 16)] [Range(0.1, 50)] public decimal TargetAtr { get; set; } = 2;
    [Display(Name = "Caja de la sesión", GroupName = "Dibujo", Order = 20)] public bool ShowSessionBox { get; set; } = true;
    [Display(Name = "Barridos", GroupName = "Dibujo", Order = 21)] public bool ShowSweeps { get; set; } = true;
    [Display(Name = "Entradas (FVG, stop, objetivo)", GroupName = "Dibujo", Order = 22)] public bool ShowTrades { get; set; } = true;
    [Display(Name = "Sesión", GroupName = "Colores", Order = 30)] public Color SessionColor { get; set; } = Color.FromArgb(255, 41, 98, 255);
    [Display(Name = "Alcista", GroupName = "Colores", Order = 31)] public Color BullColor { get; set; } = Color.FromArgb(255, 0, 176, 79);
    [Display(Name = "Bajista", GroupName = "Colores", Order = 32)] public Color BearColor { get; set; } = Color.FromArgb(255, 156, 27, 65);
    [Display(Name = "Barrido fallido", GroupName = "Colores", Order = 33)] public Color FailColor { get; set; } = Pal.Neutral;

    private static (int Start, int End) Window(Ta.WorldSession s) => s switch
    {
        Ta.WorldSession.Sydney => (22 * 60, 7 * 60),
        Ta.WorldSession.Tokyo => (0, 9 * 60),
        Ta.WorldSession.London => (8 * 60, 16 * 60),
        _ => (13 * 60, 22 * 60),
    };

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < 5) return;
        var atr = Ta.Atr(Bars, AtrLength);
        var (ws, we) = Window(Session);
        bool prevIn = false, prevClosedInside = false;
        int sessStart = -1, sessEnd = -1, valid = 0;
        double sh = 0, sl = 0;
        int sweepDir = 0, sweepBar = -1, armedDir = 0; // sweepDir: 1 barrió el mínimo (alcista), −1 el máximo
        bool upDone = false, dnDone = false;
        int tlA = -1, tlB = -1;
        int valids = 0, fails = 0, setups = 0;
        string lastSetup = "—";
        for (int i = 0; i <= last; i++)
        {
            var b = Bars[i];
            bool inS = Ta.InUtcWindow(TimeOf(i), ws, we);
            if (inS && !prevIn && i > 0)
            {
                // empieza una sesión nueva: se olvida lo anterior
                if (sessStart >= 0 && ShowSessionBox) s.Box(sessStart, sessEnd, sh, sl, Pal.A(SessionColor, 26), Pal.A(SessionColor, 90));
                sessStart = sessEnd = i;
                sh = b.H;
                sl = b.L;
                sweepDir = armedDir = 0;
                upDone = dnDone = false;
                valid = 0;
            }
            else if (inS && sessStart >= 0)
            {
                sh = Math.Max(sh, b.H);
                sl = Math.Min(sl, b.L);
                sessEnd = i;
            }

            bool closedInside = sessStart >= 0 && b.C <= sh && b.C >= sl;
            if (!inS && sessStart >= 0 && i > sessEnd)
            {
                bool limit = Strict && valid >= 1;
                // un barrido: pasa el extremo de la sesión (no más de N ATR) después de una vela que cerró adentro
                if (sweepDir == 0 && !limit && prevClosedInside)
                {
                    double cap = atr[i] * (double)MaxSweepAtr;
                    if (!upDone && b.H > sh && b.H - sh <= cap)
                    {
                        sweepDir = -1;
                        sweepBar = i;
                        upDone = true;
                    }
                    else if (!dnDone && b.L < sl && sl - b.L <= cap)
                    {
                        sweepDir = 1;
                        sweepBar = i;
                        dnDone = true;
                    }

                    if (sweepDir != 0 && ShowSweeps) s.Mark(i, sweepDir < 0 ? b.H : b.L, MarkKind.Circle, Pal.A(sweepDir > 0 ? BullColor : BearColor, 160), null, sweepDir < 0);
                }

                if (sweepDir != 0 && armedDir == 0)
                {
                    if (i - sweepBar <= SweepBars && closedInside)
                    {
                        // vuelve adentro a tiempo: barrido válido, se arma la línea de tendencia desde el extremo del tramo
                        valid++;
                        valids++;
                        armedDir = sweepDir;
                        if (ShowSweeps) s.Tag(i, sweepDir < 0 ? b.H : b.L, "Válido", Pal.A(sweepDir > 0 ? BullColor : BearColor, 210), Color.White, sweepDir < 0);
                        tlA = -1;
                        tlB = -1;
                        if (sweepDir < 0)
                        {
                            // barrió el máximo: línea de soporte por los mínimos (de la sesión a la ruptura)
                            double m1 = double.MaxValue;
                            for (int k = sessStart; k <= sweepBar; k++)
                                if (Bars[k].L < m1)
                                {
                                    m1 = Bars[k].L;
                                    tlA = k;
                                }

                            // la tangente: la menor pendiente deja todos los mínimos por encima de la línea
                            double best = double.MaxValue;
                            for (int k = tlA + 1; k <= i; k++)
                            {
                                double slope = (Bars[k].L - m1) / (k - tlA);
                                if (Bars[k].L > m1 && slope < best)
                                {
                                    best = slope;
                                    tlB = k;
                                }
                            }

                            if (best <= 0 || best == double.MaxValue) tlB = -1;
                        }
                        else
                        {
                            double m1 = double.MinValue;
                            for (int k = sessStart; k <= sweepBar; k++)
                                if (Bars[k].H > m1)
                                {
                                    m1 = Bars[k].H;
                                    tlA = k;
                                }

                            // la tangente: la mayor pendiente deja todos los máximos por debajo de la línea
                            double best = double.MinValue;
                            for (int k = tlA + 1; k <= i; k++)
                            {
                                double slope = (Bars[k].H - m1) / (k - tlA);
                                if (Bars[k].H < m1 && slope > best)
                                {
                                    best = slope;
                                    tlB = k;
                                }
                            }

                            if (best >= 0 || best == double.MinValue) tlB = -1;
                        }
                    }
                    else if (i - sweepBar > SweepBars)
                    {
                        fails++;
                        if (ShowSweeps) s.Tag(i, sweepDir < 0 ? b.H : b.L, "No válido", Pal.A(FailColor, 200), Color.White, sweepDir < 0);
                        sweepDir = 0;
                    }
                }
                else if (armedDir != 0)
                {
                    if (Strict && !closedInside)
                    {
                        armedDir = sweepDir = 0; // estricto: un cierre afuera del rango mata la idea
                    }
                    else if (tlA >= 0 && tlB > tlA)
                    {
                        double pa = armedDir < 0 ? Bars[tlA].L : Bars[tlA].H, pb = armedDir < 0 ? Bars[tlB].L : Bars[tlB].H;
                        double tl = pa + (pb - pa) * (i - tlA) / (tlB - tlA);
                        bool brk = armedDir < 0 ? b.C < tl : b.C > tl;
                        if (brk && closedInside)
                        {
                            setups++;
                            bool lng = armedDir > 0;
                            lastSetup = (lng ? "compra " : "venta ") + Price(b.C);
                            // el FVG invertido más ancho desde la ruptura
                            double fTop = double.NaN, fBot = double.NaN, width = 0;
                            int fBar = -1;
                            for (int k = Math.Max(sweepBar, 2); k <= i; k++)
                            {
                                var a = Bars[k - 2];
                                var c = Bars[k];
                                if (lng && c.H < a.L && a.L - c.H > width) { width = a.L - c.H; fTop = a.L; fBot = c.H; fBar = k - 1; }
                                if (!lng && c.L > a.H && c.L - a.H > width) { width = c.L - a.H; fTop = c.L; fBot = a.H; fBar = k - 1; }
                            }

                            double stop = Stop == StopKind.InverseFvg && fBar >= 0 ? (lng ? fBot : fTop) : (lng ? b.C - atr[i] : b.C + atr[i]);
                            double target = lng ? b.C + atr[i] * (double)TargetAtr : b.C - atr[i] * (double)TargetAtr;
                            var col = lng ? BullColor : BearColor;
                            if (ShowTrades)
                            {
                                s.Segment(tlA, i, pa, tl, col, 1, LineDashStyle.Dash);
                                s.Mark(i, lng ? b.L : b.H, lng ? MarkKind.TriangleUp : MarkKind.TriangleDown, col, "Ruptura", !lng);
                                if (fBar >= 0) s.Box(fBar, i, fTop, fBot, Pal.A(Pal.Muted, 40), Pal.A(Pal.Muted, 120), "iFVG");
                                s.Box(i, Math.Min(i + 15, LastBar), target, b.C, Pal.A(BullColor, 30), Pal.A(BullColor, 90), "Objetivo " + Price(target));
                                s.Box(i, Math.Min(i + 15, LastBar), b.C, stop, Pal.A(BearColor, 30), Pal.A(BearColor, 90), "Stop " + Price(stop));
                            }

                            armedDir = sweepDir = 0;
                        }
                    }
                }
            }

            prevIn = inS;
            prevClosedInside = closedInside;
        }

        if (sessStart >= 0 && ShowSessionBox)
        {
            bool live = sessEnd >= last - 1;
            s.Box(sessStart, live ? -1 : sessEnd, sh, sl, Pal.A(SessionColor, 26), Pal.A(SessionColor, 90));
            s.HLine(sessStart, -1, sh, SessionColor, 1, LineDashStyle.Dot, "Máximo de la sesión", "Máximo de la sesión");
            s.HLine(sessStart, -1, sl, SessionColor, 1, LineDashStyle.Dot, "Mínimo de la sesión", "Mínimo de la sesión");
        }

        s.DashTitle = "Barrido de sesión + iFVG";
        s.Row("Sesión", Session.ToString());
        s.Row("En la sesión", Ta.InUtcWindow(TimeOf(LastBar), ws, we) ? "sí" : "no");
        s.Row("Barridos válidos / no válidos", $"{valids} / {fails}");
        s.Row("Entradas", setups.ToString());
        s.Row("Última entrada", lastSetup);
    }
}

/// <summary>Barrido del máximo o mínimo de días anteriores y entrada con el FVG que se forma después.</summary>
[DisplayName("FVG del máximo / mínimo de ayer")]
[Category(IndicatorCategories.Structure)]
[Description("Los máximos y mínimos de los días anteriores (los no tocados). Cuando el precio barre uno, el primer FVG a favor dentro de la ventana da la entrada (al formarse o al volver al 50 %), con stop y objetivo en ATR y filtro de tendencia opcional.")]
public class PriorDayFvg : SceneIndicator
{
    public enum EntryTiming
    {
        [Display(Name = "Al formarse el FVG")] OnFormation,
        [Display(Name = "Al volver al 50 %")] AtMidpoint,
    }

    [Display(Name = "Días que se guardan", GroupName = "Niveles", Order = 10)] [Range(1, 60)] public int DaysToKeep { get; set; } = 10;
    [Display(Name = "Mostrar los FVG", GroupName = "FVG", Order = 20)] public bool ShowFvgs { get; set; } = true;
    [Display(Name = "FVG que se dibujan", GroupName = "FVG", Order = 21)] [Range(1, 50)] public int MaxFvgs { get; set; } = 10;
    [Display(Name = "Filtro de tamaño (ATR)", GroupName = "FVG", Order = 22)] public bool SizeFilter { get; set; } = true;
    [Display(Name = "ATR del FVG", GroupName = "FVG", Order = 23)] [Range(1, 200)] public int FvgAtrLength { get; set; } = 14;
    [Display(Name = "Tamaño mínimo (ATR)", GroupName = "FVG", Order = 24)] [Range(0, 10)] public decimal FvgAtrMult { get; set; } = 0.25m;
    [Display(Name = "Dejar los rellenados", GroupName = "FVG", Order = 25)] public bool KeepFilled { get; set; }
    [Display(Name = "Línea del 50 %", GroupName = "FVG", Order = 26)] public bool ShowMid { get; set; } = true;
    [Display(Name = "Compras", GroupName = "Entradas", Order = 30)] public bool Longs { get; set; } = true;
    [Display(Name = "Ventas", GroupName = "Entradas", Order = 31)] public bool Shorts { get; set; } = true;
    [Display(Name = "Invertir las señales", GroupName = "Entradas", Order = 32)] public bool Invert { get; set; }
    [Display(Name = "Momento de entrada", GroupName = "Entradas", Order = 33)] public EntryTiming Entry { get; set; } = EntryTiming.AtMidpoint;
    [Display(Name = "Velas después del barrido", GroupName = "Entradas", Order = 34)] [Range(1, 500)] public int Window { get; set; } = 30;
    [Display(Name = "ATR del stop", GroupName = "Entradas", Order = 35)] [Range(1, 200)] public int StopAtrLength { get; set; } = 14;
    [Display(Name = "Stop (ATR)", GroupName = "Entradas", Order = 36)] [Range(0.1, 20)] public decimal StopAtr { get; set; } = 1.5m;
    [Display(Name = "Objetivo (ATR)", GroupName = "Entradas", Order = 37)] [Range(0.1, 50)] public decimal TargetAtr { get; set; } = 3;
    [Display(Name = "Filtro de tendencia (EMA)", GroupName = "Entradas", Order = 38)] public bool TrendFilter { get; set; }
    [Display(Name = "EMA de la tendencia", GroupName = "Entradas", Order = 39)] [Range(2, 1000)] public int TrendEma { get; set; } = 200;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 40)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 41)] public Color BearColor { get; set; } = Pal.Bear;
    [Display(Name = "Niveles de ayer", GroupName = "Colores", Order = 42)] public Color LevelColor { get; set; } = Pal.Amber;

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < 5) return;
        var fAtr = Ta.Atr(Bars, FvgAtrLength);
        var tAtr = Ta.Atr(Bars, StopAtrLength);
        var ema = Ta.Ema(Bars.Select(b => b.C).ToList(), TrendEma);
        var levels = new List<(double P, bool High, int From, int Touched)>();
        double dh = Bars[0].H, dl = Bars[0].L;
        int dayStart = 0;
        int setupDir = 0, setupBar = -1;
        var queue = new List<(double Mid, bool Long)>();
        int pos = 0, entryBar = -1;
        double stop = 0, target = 0, entry = 0;
        var fvgs = new List<(int From, int To, double Top, double Bottom, bool Bull)>();
        int wins = 0, losses = 0;
        for (int i = 1; i <= last; i++)
        {
            var b = Bars[i];
            if (DayKey[i] != DayKey[i - 1])
            {
                // el día que terminó deja su máximo y su mínimo
                levels.Add((dh, true, dayStart, -1));
                levels.Add((dl, false, dayStart, -1));
                while (levels.Count > DaysToKeep * 2) levels.RemoveAt(0);
                dh = b.H;
                dl = b.L;
                dayStart = i;
                if (pos == 0)
                {
                    setupDir = 0;
                    queue.Clear();
                }
            }
            else
            {
                dh = Math.Max(dh, b.H);
                dl = Math.Min(dl, b.L);
            }

            // barridos: el primer toque de un nivel que no se había tocado
            bool hiSweep = false, loSweep = false;
            for (int k = 0; k < levels.Count; k++)
            {
                var lv = levels[k];
                if (lv.Touched >= 0 || i <= lv.From) continue;
                if (lv.High && b.H >= lv.P)
                {
                    levels[k] = lv with { Touched = i };
                    hiSweep = true;
                }
                else if (!lv.High && b.L <= lv.P)
                {
                    levels[k] = lv with { Touched = i };
                    loSweep = true;
                }
            }

            if (hiSweep || loSweep)
            {
                setupDir = hiSweep && loSweep ? 2 : loSweep ? 1 : -1;
                setupBar = i;
                if (pos == 0) queue.Clear();
                s.Mark(i, loSweep ? b.L : b.H, MarkKind.Circle, Pal.A(LevelColor, 200), null, !loSweep);
            }

            // un trade abierto: stop u objetivo (probados en la misma vela de entrada también)
            if (pos != 0)
            {
                bool hitStop = pos > 0 ? b.L <= stop : b.H >= stop, hitTarget = pos > 0 ? b.H >= target : b.L <= target;
                if (hitStop || hitTarget)
                {
                    if (hitStop) losses++;
                    else wins++;
                    s.Mark(i, pos > 0 ? b.H : b.L, MarkKind.Cross, hitStop ? BearColor : BullColor, hitStop ? "Stop" : "Objetivo", pos > 0);
                    s.Segment(entryBar, i, stop, stop, BearColor, 1, LineDashStyle.Dash);
                    s.Segment(entryBar, i, target, target, BullColor, 1, LineDashStyle.Dash);
                    pos = 0;
                }
            }

            // FVG de tres velas
            if (i >= 2)
            {
                var a = Bars[i - 2];
                double min = SizeFilter ? fAtr[i] * (double)FvgAtrMult : 0;
                bool bull = b.L > a.H && b.L - a.H >= min, bear = b.H < a.L && a.L - b.H >= min;
                if (bull || bear)
                {
                    double top = bull ? b.L : a.L, bot = bull ? a.H : b.H;
                    fvgs.Add((i - 1, -1, top, bot, bull));
                    bool inWindow = setupDir != 0 && i - setupBar <= Window;
                    bool longSig = bull && inWindow && (setupDir == 1 || setupDir == 2);
                    bool shortSig = bear && inWindow && (setupDir == -1 || setupDir == 2);
                    if (TrendFilter)
                    {
                        longSig &= b.C > ema[i];
                        shortSig &= b.C < ema[i];
                    }

                    if (Invert) (longSig, shortSig) = (shortSig, longSig);
                    longSig &= Longs;
                    shortSig &= Shorts;
                    if (pos == 0 && (longSig || shortSig))
                    {
                        if (Entry == EntryTiming.OnFormation) Open(i, b.C, longSig);
                        else queue.Add(((top + bot) / 2, longSig));
                    }
                }
            }

            if (pos == 0 && queue.Count > 0)
            {
                int q = queue.FindIndex(x => b.L <= x.Mid && b.H >= x.Mid);
                if (q >= 0)
                {
                    Open(i, queue[q].Mid, queue[q].Long);
                    queue.Clear();
                }
            }

            // los FVG se rellenan
            for (int k = 0; k < fvgs.Count; k++)
            {
                var f = fvgs[k];
                if (f.To >= 0 || i <= f.From + 1) continue;
                if (f.Bull ? b.L <= f.Bottom : b.H >= f.Top) fvgs[k] = f with { To = i };
            }
        }

        void Open(int i, double price, bool lng)
        {
            pos = lng ? 1 : -1;
            entry = price;
            entryBar = i;
            stop = lng ? price - tAtr[i] * (double)StopAtr : price + tAtr[i] * (double)StopAtr;
            target = lng ? price + tAtr[i] * (double)TargetAtr : price - tAtr[i] * (double)TargetAtr;
            s.Mark(i, lng ? Bars[i].L : Bars[i].H, lng ? MarkKind.TriangleUp : MarkKind.TriangleDown, lng ? BullColor : BearColor, lng ? "Compra" : "Venta", !lng);
            setupDir = 0;
        }

        foreach (var lv in levels)
            s.HLine(lv.From, lv.Touched >= 0 ? lv.Touched : -1, lv.P, Pal.A(LevelColor, lv.Touched >= 0 ? 90 : 220), 1, lv.Touched >= 0 ? LineDashStyle.Dot : LineDashStyle.Solid,
                alert: lv.Touched < 0 ? (lv.High ? "Máximo de un día anterior" : "Mínimo de un día anterior") : null);
        if (ShowFvgs)
            foreach (var f in fvgs.Where(f => KeepFilled || f.To < 0).TakeLast(MaxFvgs))
            {
                var c = f.Bull ? BullColor : BearColor;
                s.Box(f.From, f.To, f.Top, f.Bottom, Pal.A(c, 35), Pal.A(c, 110), alert: f.To < 0 ? "FVG" : null);
                if (ShowMid) s.HLine(f.From, f.To, (f.Top + f.Bottom) / 2, Pal.A(c, 150), 1, LineDashStyle.Dot);
            }

        if (pos != 0)
        {
            s.HLine(entryBar, -1, stop, BearColor, 1, LineDashStyle.Dash, "Stop " + Price(stop), "Stop");
            s.HLine(entryBar, -1, target, BullColor, 1, LineDashStyle.Dash, "Objetivo " + Price(target), "Objetivo");
        }

        if (TrendFilter) s.Paths.Add(new Scene.PathShape(Enumerable.Range(Math.Max(0, Bars.Count - 600), Math.Min(600, Bars.Count)).Select(i => (i, ema[i])).ToList(), Pal.Muted));
        s.DashTitle = "FVG de ayer";
        s.Row("Posición", pos > 0 ? "comprado" : pos < 0 ? "vendido" : "afuera", pos > 0 ? BullColor : pos < 0 ? BearColor : default);
        s.Row("Objetivos / stops", $"{wins} / {losses}");
        s.Row("Niveles sin tocar", levels.Count(l => l.Touched < 0).ToString());
    }
}

/// <summary>Zonas de máximos / mínimos iguales y su ruptura con una vela grande que deja un FVG.</summary>
[DisplayName("Máximos y mínimos iguales con ruptura y FVG")]
[Category(IndicatorCategories.Structure)]
[Description("Zonas de liquidez donde dos máximos (o mínimos) confirmados quedan casi iguales. Cuando una vela de cuerpo grande cierra a través de la zona y la secuencia deja un FVG, se marca la ruptura y se proyecta el FVG.")]
public class EqualHighsLowsFvg : SceneIndicator
{
    [Display(Name = "Velas a la izquierda", GroupName = "Cálculo", Order = 10)] [Range(1, 100)] public int Left { get; set; } = 10;
    [Display(Name = "Velas a la derecha", GroupName = "Cálculo", Order = 11)] [Range(1, 50)] public int Right { get; set; } = 2;
    [Display(Name = "Diferencia máxima (%)", GroupName = "Cálculo", Order = 12)] [Range(0.001, 5)] public decimal ThresholdPct { get; set; } = 0.03m;
    [Display(Name = "Zonas como máximo", GroupName = "Cálculo", Order = 13)] [Range(1, 200)] public int MaxZones { get; set; } = 60;
    [Display(Name = "Velas como máximo entre los dos", GroupName = "Cálculo", Order = 14)] [Range(2, 500)] public int MaxApart { get; set; } = 30;
    [Display(Name = "Edad máxima de la zona (velas)", GroupName = "Cálculo", Order = 15)] [Range(1, 1000)] public int MaxAge { get; set; } = 30;
    [Display(Name = "Volumen en la zona", GroupName = "Dibujo", Order = 20)] public bool ShowVolume { get; set; } = true;
    [Display(Name = "Señales de ruptura", GroupName = "Ruptura", Order = 30)] public bool ShowBreakouts { get; set; } = true;
    [Display(Name = "ATR", GroupName = "Ruptura", Order = 31)] [Range(1, 200)] public int AtrLength { get; set; } = 14;
    [Display(Name = "Cuerpo mínimo (ATR)", GroupName = "Ruptura", Order = 32)] [Range(0, 10)] public decimal BodyAtr { get; set; } = 0.5m;
    [Display(Name = "FVG mínimo (ATR)", GroupName = "Ruptura", Order = 33)] [Range(0, 10)] public decimal FvgAtr { get; set; } = 0.1m;
    [Display(Name = "FVG de la ruptura", GroupName = "Ruptura", Order = 34)] public bool ShowFvgs { get; set; } = true;
    [Display(Name = "Velas que se proyecta el FVG", GroupName = "Ruptura", Order = 35)] [Range(1, 200)] public int FvgProjection { get; set; } = 5;
    [Display(Name = "Máximos iguales", GroupName = "Colores", Order = 40)] public Color EqhColor { get; set; } = Pal.Bear;
    [Display(Name = "Mínimos iguales", GroupName = "Colores", Order = 41)] public Color EqlColor { get; set; } = Pal.Bull;

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < Left + Right + 3) return;
        var atr = Ta.Atr(Bars, AtrLength);
        var pivots = Ta.Pivots(Bars, Left, Right, last);
        var zones = new List<(int A, int B, int Confirm, double Top, double Bottom, bool High, double Vol, int End, bool Broke)>();
        var prevHi = new List<Ta.Pivot>();
        var prevLo = new List<Ta.Pivot>();
        int bull = 0, bear = 0;
        var byConfirm = pivots.ToLookup(p => p.Confirm);
        int pendingBar = -1, pendingDir = 0;
        for (int i = 0; i <= last; i++)
        {
            var b = Bars[i];
            // una ruptura pendiente se confirma en la vela siguiente si las tres velas dejaron un FVG
            if (pendingBar == i - 1 && i >= 2 && pendingDir != 0)
            {
                var a = Bars[i - 2];
                double min = atr[i] * (double)FvgAtr;
                bool ok = pendingDir > 0 ? b.L > a.H && b.L - a.H >= min : b.H < a.L && a.L - b.H >= min;
                if (ok && ShowBreakouts)
                {
                    var c = pendingDir > 0 ? EqlColor : EqhColor;
                    s.Mark(i, pendingDir > 0 ? b.L : b.H, pendingDir > 0 ? MarkKind.TriangleUp : MarkKind.TriangleDown, c, "Ruptura", pendingDir < 0);
                    if (ShowFvgs) s.Box(i - 1, Math.Min(i + FvgProjection, LastBar), pendingDir > 0 ? b.L : a.L, pendingDir > 0 ? a.H : b.H, Pal.A(c, 45), Pal.A(c, 140), "FVG");
                    if (pendingDir > 0) bull++;
                    else bear++;
                }

                pendingDir = 0;
            }

            foreach (var p in byConfirm[i])
            {
                var list = p.High ? prevHi : prevLo;
                foreach (var q in list.Where(q => p.Bar - q.Bar <= MaxApart).Reverse())
                {
                    double diff = Math.Abs(p.Price - q.Price) / Math.Max(1e-12, q.Price) * 100;
                    if (diff > (double)ThresholdPct) continue;
                    double vol = 0;
                    for (int k = q.Bar; k <= p.Bar; k++) vol += Bars[k].V;
                    zones.Add((q.Bar, p.Bar, i, Math.Max(p.Price, q.Price), Math.Min(p.Price, q.Price), p.High, vol, -1, false));
                    break;
                }

                list.Add(p);
                if (list.Count > 50) list.RemoveAt(0);
            }

            // zonas vivas: barridas, rotas por una vela grande, o viejas
            for (int z = 0; z < zones.Count; z++)
            {
                var zn = zones[z];
                if (zn.End >= 0 || i <= zn.Confirm) continue;
                double body = Math.Abs(b.C - b.O);
                bool wide = body >= atr[i] * (double)BodyAtr;
                if (zn.High ? b.C > zn.Top : b.C < zn.Bottom)
                {
                    zones[z] = zn with { End = i, Broke = wide };
                    if (wide)
                    {
                        pendingBar = i;
                        pendingDir = zn.High ? 1 : -1;
                    }
                }
                else if (i - zn.Confirm > MaxAge) zones[z] = zn with { End = i };
            }

            while (zones.Count > MaxZones) zones.RemoveAt(0);
        }

        foreach (var zn in zones)
        {
            var c = zn.High ? EqhColor : EqlColor;
            bool live = zn.End < 0;
            string text = (zn.High ? "EQH" : "EQL") + (ShowVolume ? " · " + VpUltimate.Vp.Short((decimal)zn.Vol) : "");
            s.Box(zn.A, live ? -1 : zn.End, zn.Top + (zn.Top == zn.Bottom ? atr[zn.B] * 0.05 : 0), zn.Bottom, Pal.A(c, live ? 40 : 18), Pal.A(c, live ? 140 : 60), text, alert: live ? (zn.High ? "Máximos iguales" : "Mínimos iguales") : null);
        }

        s.DashTitle = "Máximos / mínimos iguales";
        s.Row("Zonas vivas", zones.Count(z => z.End < 0).ToString());
        s.Row("Rupturas alcistas / bajistas", $"{bull} / {bear}");
    }
}

/// <summary>FVG con filtro de ATR que se van rellenando, el nivel estructural que dejan y su ruptura.</summary>
[DisplayName("Rupturas al rellenar huecos (FVG)")]
[Category(IndicatorCategories.Structure)]
[Description("Huecos de valor justo (FVG) filtrados por ATR que se van rellenando a medida que el precio entra. Cada hueco vivo toma un nivel estructural (un pivote del cuerpo del otro lado); el cierre que lo corta es la ruptura: un FVG alcista deja un soporte arriba, y romperlo es bajista.")]
public class GapFillBreakouts : SceneIndicator
{
    [Display(Name = "Tamaño mínimo (ATR)", GroupName = "Cálculo", Order = 10)] [Range(0, 10)] public decimal AtrMult { get; set; } = 1;
    [Display(Name = "Largo del pivote", GroupName = "Cálculo", Order = 11)] [Range(1, 50)] public int PivotLength { get; set; } = 5;
    [Display(Name = "Edad máxima del hueco (velas)", GroupName = "Cálculo", Order = 12)] [Range(10, 2000)] public int MaxBarsBack { get; set; } = 200;
    [Display(Name = "Huecos como máximo", GroupName = "Cálculo", Order = 13)] [Range(1, 50)] public int MaxGaps { get; set; } = 5;
    [Display(Name = "Huecos", GroupName = "Dibujo", Order = 20)] public bool ShowGaps { get; set; } = true;
    [Display(Name = "Niveles", GroupName = "Dibujo", Order = 21)] public bool ShowLevels { get; set; } = true;
    [Display(Name = "Etiquetas de ruptura", GroupName = "Dibujo", Order = 22)] public bool ShowLabels { get; set; } = true;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 30)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 31)] public Color BearColor { get; set; } = Pal.Bear;

    private sealed class G
    {
        public int Bar, LevelBar = -1, LevelEnd = -1, End = -1;
        public double Top, Bottom, Remain, Level = double.NaN;
        public bool Bull, Used;
    }

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < 5) return;
        var atr = Ta.Atr(Bars, 14);
        var book = new List<G>();
        var all = new List<G>();
        int L = PivotLength;
        int bullBrk = 0, bearBrk = 0;
        for (int i = 2; i <= last; i++)
        {
            var b = Bars[i];
            var a = Bars[i - 2];
            double min = atr[i] * (double)AtrMult;
            if (b.L > a.H && b.L - a.H >= min) Push(new G { Bar = i - 1, Top = b.L, Bottom = a.H, Remain = b.L, Bull = true });
            else if (b.H < a.L && a.L - b.H >= min) Push(new G { Bar = i - 1, Top = a.L, Bottom = b.H, Remain = b.H, Bull = false });

            // pivote del cuerpo (máx/mín de apertura y cierre) confirmado L velas tarde
            double pHi = double.NaN, pLo = double.NaN;
            int pc = i - L;
            if (pc - L >= 0)
            {
                double ch = Math.Max(Bars[pc].O, Bars[pc].C), cl = Math.Min(Bars[pc].O, Bars[pc].C);
                bool isHi = true, isLo = true;
                for (int k = pc - L; k <= i; k++)
                {
                    if (k == pc) continue;
                    double hk = Math.Max(Bars[k].O, Bars[k].C), lk = Math.Min(Bars[k].O, Bars[k].C);
                    if (k < pc ? hk > ch : hk >= ch) isHi = false;
                    if (k < pc ? lk < cl : lk <= cl) isLo = false;
                }

                if (isHi) pHi = ch;
                if (isLo) pLo = cl;
            }

            // del más nuevo al más viejo: se rellenan, toman su nivel y se rompen
            for (int k = book.Count - 1; k >= 0; k--)
            {
                var g = book[k];
                if (g.Bull)
                {
                    if (b.L < g.Remain) g.Remain = Math.Max(g.Bottom, b.L);
                    if (g.Remain <= g.Bottom) { g.End = i; book.RemoveAt(k); continue; }
                    if (double.IsNaN(g.Level) && !g.Used && !double.IsNaN(pLo) && pLo > g.Remain && pc > g.Bar) { g.Level = pLo; g.LevelBar = pc; }
                }
                else
                {
                    if (b.H > g.Remain) g.Remain = Math.Min(g.Top, b.H);
                    if (g.Remain >= g.Top) { g.End = i; book.RemoveAt(k); continue; }
                    if (double.IsNaN(g.Level) && !g.Used && !double.IsNaN(pHi) && pHi < g.Remain && pc > g.Bar) { g.Level = pHi; g.LevelBar = pc; }
                }

                if (!double.IsNaN(g.Level) && g.LevelEnd < 0 && i > g.LevelBar + L)
                {
                    bool cut = g.Bull ? b.C < g.Level : b.C > g.Level;
                    if (cut)
                    {
                        g.LevelEnd = i;
                        g.Used = true;
                        if (i - g.Bar <= MaxBarsBack && ShowLabels)
                        {
                            // un hueco alcista deja un soporte arriba: romperlo es bajista (y al revés)
                            bool bearish = g.Bull;
                            s.Tag(i, bearish ? b.H : b.L, bearish ? "Ruptura bajista" : "Ruptura alcista", Pal.A(bearish ? BearColor : BullColor, 210), Color.White, bearish);
                            if (bearish) bearBrk++;
                            else bullBrk++;
                        }
                    }
                }

                if (i - g.Bar > MaxBarsBack && g.LevelEnd < 0 && !double.IsNaN(g.Level)) g.LevelEnd = i;
            }
        }

        void Push(G g)
        {
            book.Add(g);
            all.Add(g);
            if (book.Count > MaxGaps)
            {
                book[0].End = book[0].End < 0 ? Bars.Count - 1 : book[0].End;
                book.RemoveAt(0);
            }
        }

        foreach (var g in all.TakeLast(60))
        {
            var c = g.Bull ? BullColor : BearColor;
            bool live = book.Contains(g);
            if (ShowGaps)
            {
                double top = g.Bull ? g.Remain : g.Top, bot = g.Bull ? g.Bottom : g.Remain;
                s.Box(g.Bar, live ? -1 : g.End, top, bot, Pal.A(c, live ? 40 : 15), Pal.A(c, live ? 130 : 50), "FVG", alert: live ? "FVG" : null);
            }

            if (ShowLevels && !double.IsNaN(g.Level))
                s.HLine(g.LevelBar, g.LevelEnd, g.Level, Pal.A(g.Bull ? BullColor : BearColor, 200), 1, LineDashStyle.Dash, alert: g.LevelEnd < 0 ? "Nivel del hueco" : null);
        }

        s.DashTitle = "Rupturas de huecos";
        s.Row("Huecos vivos", book.Count.ToString());
        s.Row("Rupturas alcistas / bajistas", $"{bullBrk} / {bearBrk}");
    }
}

/// <summary>Zonas de soporte / resistencia en los swings con sus pruebas, rupturas, re-pruebas y vencimientos.</summary>
[DisplayName("Pruebas y re-pruebas de rupturas")]
[Category(IndicatorCategories.Structure)]
[Description("Cada mínimo confirmado arma una zona alcista (soporte) y cada máximo una bajista (resistencia), de un ancho en ATR. Marca las pruebas (abre adentro y cierra a favor), la ruptura, las re-pruebas de la zona rota y las que vencen sin prueba. Solo aperturas y cierres disparan.")]
public class BreakoutTestsRetests : SceneIndicator
{
    public enum Side
    {
        [Display(Name = "Alcistas y bajistas")] Both,
        [Display(Name = "Solo alcistas")] Bullish,
        [Display(Name = "Solo bajistas")] Bearish,
    }

    [Display(Name = "Zonas", GroupName = "Cálculo", Order = 10)] public Side Display { get; set; } = Side.Both;
    [Display(Name = "Ancho (× ATR 200)", GroupName = "Cálculo", Order = 11)] [Range(0.05, 10)] public decimal Width { get; set; } = 1;
    [Display(Name = "Velas que vive una zona", GroupName = "Cálculo", Order = 12)] [Range(10, 5000)] public int MaxBars { get; set; } = 500;
    [Display(Name = "Solo la última prueba", GroupName = "Cálculo", Order = 13)] public bool OnlyLast { get; set; }
    [Display(Name = "Velas mínimas entre marcas", GroupName = "Cálculo", Order = 14)] [Range(0, 100)] public int MinBars { get; set; }
    [Display(Name = "Velas a la izquierda", GroupName = "Cálculo", Order = 15)] [Range(1, 100)] public int Left { get; set; } = 10;
    [Display(Name = "Velas a la derecha", GroupName = "Cálculo", Order = 16)] [Range(1, 50)] public int Right { get; set; } = 1;
    [Display(Name = "Zonas", GroupName = "Dibujo", Order = 20)] public bool ShowZones { get; set; } = true;
    [Display(Name = "Marcas de prueba / re-prueba", GroupName = "Dibujo", Order = 21)] public bool ShowGlyphs { get; set; } = true;
    [Display(Name = "Fondo en la ruptura", GroupName = "Dibujo", Order = 22)] public bool ShowBreakoutBg { get; set; } = true;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 30)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 31)] public Color BearColor { get; set; } = Pal.Bear;

    private sealed class M
    {
        public int State, From, Since, Tests, Retests, LastMark = -1000, BrokeAt = -1;
        public double Top, Bottom;
    }

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < Left + Right + 2) return;
        var atr = Ta.Atr(Bars, 200);
        var pivots = Ta.Pivots(Bars, Left, Right, last).ToLookup(p => p.Confirm);
        var bull = new M();
        var bear = new M();
        var boxes = new List<(int From, int To, double Top, double Bottom, bool Bull, bool Broken)>();
        int brkUp = 0, brkDn = 0;
        bool useBull = Display != Side.Bearish, useBear = Display != Side.Bullish;
        for (int i = 1; i <= last; i++)
        {
            var b = Bars[i];
            double w = atr[i] * (double)Width;
            foreach (var p in pivots[i])
            {
                if (!p.High && useBull)
                {
                    Close(bull, true, i);
                    bull = new M { State = 1, From = p.Bar, Since = i, Bottom = p.Price, Top = p.Price + w };
                    s.Mark(p.Bar, p.Price, MarkKind.Circle, Pal.A(BullColor, 160), null, false);
                }
                else if (p.High && useBear)
                {
                    Close(bear, false, i);
                    bear = new M { State = 1, From = p.Bar, Since = i, Top = p.Price, Bottom = p.Price - w };
                    s.Mark(p.Bar, p.Price, MarkKind.Circle, Pal.A(BearColor, 160));
                }
            }

            if (useBull) Step(bull, true, i, b);
            if (useBear) Step(bear, false, i, b);
        }

        void Glyph(M m, int i, string text, double price, Color c, bool above, bool retest)
        {
            if (!ShowGlyphs) return;
            if (!OnlyLast && i - m.LastMark < MinBars) return;
            if (OnlyLast) s.Marks.RemoveAll(x => x.Text == text && x.Bar >= m.From && x.Color == c);
            s.Mark(i, price, retest ? MarkKind.Diamond : MarkKind.Square, c, text, above);
            m.LastMark = i;
        }

        void Step(M m, bool isBull, int i, Bar b)
        {
            if (m.State == 0) return;
            double top = m.Top, bot = m.Bottom;
            bool openIn = b.O >= bot && b.O <= top;
            var col = isBull ? BullColor : BearColor;
            var broken = isBull ? BearColor : BullColor;
            if (m.State == 1)
            {
                if (isBull ? b.C < bot : b.C > top)
                {
                    m.State = 2;
                    m.BrokeAt = i;
                    m.Since = i;
                    if (isBull) brkDn++;
                    else brkUp++;
                    if (ShowBreakoutBg) s.Tints[i] = Pal.A(broken, 30);
                    s.Mark(i, isBull ? b.L : b.H, isBull ? MarkKind.TriangleDown : MarkKind.TriangleUp, broken, null, !isBull);
                }
                else if (openIn && (isBull ? b.C > top : b.C < bot))
                {
                    m.Tests++;
                    Glyph(m, i, "Prueba", isBull ? b.L : b.H, col, !isBull, false);
                }
                else if (i - m.Since >= MaxBars)
                {
                    if (m.Tests == 0) s.Mark(i, isBull ? b.L : b.H, MarkKind.Cross, Pal.A(col, 150), null, !isBull);
                    Close(m, isBull, i);
                }
            }
            else if (m.State == 2)
            {
                if (openIn && (isBull ? b.C < bot : b.C > top))
                {
                    m.Retests++;
                    Glyph(m, i, "Re-prueba", isBull ? b.H : b.L, broken, isBull, true);
                }
                else if (i - m.Since >= MaxBars)
                {
                    if (m.Retests == 0) s.Mark(i, isBull ? b.H : b.L, MarkKind.Cross, Pal.A(broken, 150), null, isBull);
                    Close(m, isBull, i);
                }
            }
        }

        void Close(M m, bool isBull, int i)
        {
            if (m.State == 0) return;
            boxes.Add((m.From, i, m.Top, m.Bottom, isBull, m.State == 2));
            m.State = 0;
        }

        if (ShowZones)
        {
            foreach (var bx in boxes.TakeLast(40))
            {
                var c = bx.Broken ? (bx.Bull ? BearColor : BullColor) : (bx.Bull ? BullColor : BearColor);
                s.Box(bx.From, bx.To, bx.Top, bx.Bottom, Pal.A(c, 18), Pal.A(c, 60));
            }

            foreach (var (m, isBull) in new[] { (bull, true), (bear, false) })
            {
                if (m.State == 0) continue;
                var c = m.State == 2 ? (isBull ? BearColor : BullColor) : (isBull ? BullColor : BearColor);
                s.Box(m.From, -1, m.Top, m.Bottom, Pal.A(c, 40), Pal.A(c, 150), m.State == 2 ? "Rota" : null, alert: isBull ? "Zona alcista" : "Zona bajista");
            }
        }

        string St(M m) => m.State switch { 1 => "viva", 2 => "rota", _ => "—" };
        s.DashTitle = "Pruebas y re-pruebas";
        if (useBull) s.Row("Zona alcista", $"{St(bull)} · {bull.Tests} pruebas · {bull.Retests} re-pruebas", BullColor);
        if (useBear) s.Row("Zona bajista", $"{St(bear)} · {bear.Tests} pruebas · {bear.Retests} re-pruebas", BearColor);
        s.Row("Rupturas arriba / abajo", $"{brkUp} / {brkDn}");
    }
}
