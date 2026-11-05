using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

namespace TradingHub.Indicators.Native.Structure;

// Niveles de la sección 12 (estructura de mercado y niveles clave): zonas de oferta y demanda, niveles clave del calendario,
// pivotes de piso y estándar, y el «line break». Todo con velas cerradas: lo que ya se dibujó no cambia.

/// <summary>Zonas de oferta y demanda en cada máximo y mínimo confirmado, hasta que un cierre las atraviesa.</summary>
[DisplayName("Zonas de oferta y demanda")]
[Category(IndicatorCategories.Structure)]
[Description("Una zona en cada máximo confirmado (oferta) y en cada mínimo (demanda), alargada a la derecha hasta que un cierre la atraviesa; la rota se apaga y se marca la vela de la ruptura. Arriba y abajo del precio se nombra la zona viva más cercana.")]
public class SupplyDemandZones : SceneIndicator
{
    private int _strength = 8, _perSide = 6;
    private decimal _zoneAtr = 0.25m;

    [Display(Name = "Fuerza del swing (velas a cada lado)", GroupName = "Cálculo", Order = 10)]
    [Range(1, 100)]
    public int SwingStrength { get => _strength; set { _strength = Math.Clamp(value, 1, 100); RecalculateValues(); } }

    [Display(Name = "Alto de la zona (en ATR)", GroupName = "Cálculo", Order = 20)]
    [Range(0.01, 10)]
    public decimal ZoneAtr { get => _zoneAtr; set { _zoneAtr = Math.Clamp(value, 0.01m, 10); RecalculateValues(); } }

    [Display(Name = "Zonas por lado", GroupName = "Cálculo", Order = 30)]
    [Range(1, 50)]
    public int ZonesPerSide { get => _perSide; set { _perSide = Math.Clamp(value, 1, 50); RecalculateValues(); } }

    [Display(Name = "Oferta", GroupName = "Colores", Order = 40)] public Color SupplyColor { get; set; } = Pal.Bear;
    [Display(Name = "Demanda", GroupName = "Colores", Order = 41)] public Color DemandColor { get; set; } = Pal.Bull;
    [Display(Name = "Marca de ruptura", GroupName = "Colores", Order = 42)] public Color BreakColor { get; set; } = Pal.Rose;

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < _strength * 2 + 2) return;
        var atr = Ta.Atr(Bars, 14);
        var zones = new List<(int From, int Confirm, double Top, double Bottom, bool Supply, int Broken)>();
        foreach (var p in Ta.Pivots(Bars, _strength, _strength, last))
        {
            double h = atr[p.Confirm] * (double)_zoneAtr;
            zones.Add(p.High ? (p.Bar, p.Confirm, p.Price, p.Price - h, true, -1) : (p.Bar, p.Confirm, p.Price + h, p.Price, false, -1));
        }

        for (int z = 0; z < zones.Count; z++)
        {
            var zn = zones[z];
            for (int i = zn.Confirm + 1; i <= last; i++)
            {
                if (zn.Supply ? Bars[i].C > zn.Top : Bars[i].C < zn.Bottom)
                {
                    zones[z] = zn with { Broken = i };
                    break;
                }
            }
        }

        // la marca de cada ruptura queda aunque su zona ya no se dibuje
        foreach (var zn in zones.Where(z => z.Broken >= 0))
            s.Mark(zn.Broken, zn.Supply ? Bars[zn.Broken].H : Bars[zn.Broken].L, MarkKind.Diamond, BreakColor, null, zn.Supply);
        double close = Bars[LastBar].C;
        (int Index, double Dist) nearUp = (-1, double.MaxValue), nearDn = (-1, double.MaxValue);
        foreach (bool supply in new[] { true, false })
        {
            foreach (var (zn, k) in zones.Select((z, k) => (z, k)).Where(x => x.z.Supply == supply).TakeLast(_perSide))
            {
                var color = supply ? SupplyColor : DemandColor;
                bool live = zn.Broken < 0;
                var c = live ? color : Pal.Muted;
                s.Box(zn.From, live ? -1 : zn.Broken, zn.Top, zn.Bottom, Pal.A(c, live ? 45 : 22), Pal.A(c, live ? 150 : 70),
                    alert: live ? (supply ? "Oferta" : "Demanda") : null);
                if (!live) continue;
                double mid = (zn.Top + zn.Bottom) / 2;
                if (mid > close && mid - close < nearUp.Dist) nearUp = (k, mid - close);
                if (mid < close && close - mid < nearDn.Dist) nearDn = (k, close - mid);
            }
        }

        foreach (var (k, above) in new[] { (nearUp.Index, true), (nearDn.Index, false) })
        {
            if (k < 0) continue;
            var zn = zones[k];
            s.HLine(LastBar, -1, above ? zn.Bottom : zn.Top, Color.Transparent, 1, label: (zn.Supply ? "Oferta " : "Demanda ") + Price(above ? zn.Bottom : zn.Top));
        }

        s.DashTitle = "Oferta y demanda";
        s.Row("Zonas de oferta vivas", zones.Count(z => z.Supply && z.Broken < 0).ToString(), SupplyColor);
        s.Row("Zonas de demanda vivas", zones.Count(z => !z.Supply && z.Broken < 0).ToString(), DemandColor);
        if (nearUp.Index >= 0) s.Row("Oferta más cercana", Price(zones[nearUp.Index].Bottom), SupplyColor);
        if (nearDn.Index >= 0) s.Row("Demanda más cercana", Price(zones[nearDn.Index].Top), DemandColor);
    }
}

/// <summary>Máximo, mínimo, cierre, apertura y punto medio del día, la semana, el mes, el trimestre y el año.</summary>
[DisplayName("Niveles clave (día, semana, mes, trimestre, año)")]
[Category(IndicatorCategories.Structure)]
[Description("Máximo, mínimo y cierre del período anterior, apertura del actual y punto medio (EQ) del anterior, para el día, la semana, el mes, el trimestre y el año. Las etiquetas que caen en el mismo precio se juntan.")]
public class CalendarKeyLevels : SceneIndicator
{
    [Display(Name = "Día", GroupName = "Períodos", Order = 10)] public bool Daily { get; set; } = true;
    [Display(Name = "Semana", GroupName = "Períodos", Order = 11)] public bool Weekly { get; set; } = true;
    [Display(Name = "Mes", GroupName = "Períodos", Order = 12)] public bool Monthly { get; set; } = true;
    [Display(Name = "Trimestre", GroupName = "Períodos", Order = 13)] public bool Quarterly { get; set; }
    [Display(Name = "Año", GroupName = "Períodos", Order = 14)] public bool Yearly { get; set; }

    [Display(Name = "Máximo y mínimo anteriores", GroupName = "Niveles", Order = 20)] public bool ShowHighLow { get; set; } = true;
    [Display(Name = "Cierre anterior", GroupName = "Niveles", Order = 21)] public bool ShowClose { get; set; }
    [Display(Name = "Apertura", GroupName = "Niveles", Order = 22)] public bool ShowOpen { get; set; } = true;
    [Display(Name = "Punto medio (EQ)", GroupName = "Niveles", Order = 23)] public bool ShowEq { get; set; } = true;

    [Display(Name = "Día", GroupName = "Colores", Order = 30)] public Color DayColor { get; set; } = Color.FromArgb(255, 41, 182, 246);
    [Display(Name = "Semana", GroupName = "Colores", Order = 31)] public Color WeekColor { get; set; } = Color.FromArgb(255, 171, 71, 188);
    [Display(Name = "Mes", GroupName = "Colores", Order = 32)] public Color MonthColor { get; set; } = Color.FromArgb(255, 255, 167, 38);
    [Display(Name = "Trimestre", GroupName = "Colores", Order = 33)] public Color QuarterColor { get; set; } = Color.FromArgb(255, 102, 187, 106);
    [Display(Name = "Año", GroupName = "Colores", Order = 34)] public Color YearColor { get; set; } = Color.FromArgb(255, 239, 83, 80);

    public CalendarKeyLevels() => ShowDashboard = false;

    protected override void Build(Scene s)
    {
        if (Bars.Count < 3) return;
        var labels = new List<(double Price, string Text, Color Color)>();
        foreach (var (p, on, code, color) in new[]
                 {
                     (CalendarPeriod.Day, Daily, "D", DayColor), (CalendarPeriod.Week, Weekly, "W", WeekColor), (CalendarPeriod.Month, Monthly, "M", MonthColor),
                     (CalendarPeriod.Quarter, Quarterly, "Q", QuarterColor), (CalendarPeriod.Year, Yearly, "Y", YearColor),
                 })
        {
            if (!on) continue;
            var key = PeriodKey(p);
            int last = LastBar;
            int curStart = last;
            while (curStart > 0 && key[curStart - 1] == key[last]) curStart--;
            if (curStart == 0) continue; // sin un período anterior completo
            int prevStart = curStart - 1;
            while (prevStart > 0 && key[prevStart - 1] == key[curStart - 1]) prevStart--;
            double h = double.MinValue, l = double.MaxValue;
            for (int i = prevStart; i < curStart; i++)
            {
                h = Math.Max(h, Bars[i].H);
                l = Math.Min(l, Bars[i].L);
            }

            double c = Bars[curStart - 1].C, o = Bars[curStart].O, eq = (h + l) / 2;
            void Lvl(string name, double price)
            {
                s.HLine(curStart, -1, price, color, 1, name.StartsWith('E') || name.EndsWith('O') ? LineDashStyle.Dash : LineDashStyle.Solid, alert: name);
                labels.Add((price, name, color));
            }

            if (ShowHighLow)
            {
                Lvl("P" + code + "H", h);
                Lvl("P" + code + "L", l);
            }

            if (ShowClose) Lvl("P" + code + "C", c);
            if (ShowOpen) Lvl(code + "O", o);
            if (ShowEq) Lvl(code + " EQ", eq);
        }

        // una etiqueta por precio (las que coinciden se juntan)
        double tol = Math.Max(1e-9, Bars[LastBar].C * 0.0002);
        foreach (var g in labels.OrderBy(x => x.Price).Aggregate(new List<List<(double Price, string Text, Color Color)>>(), (acc, x) =>
                 {
                     if (acc.Count > 0 && Math.Abs(acc[^1][0].Price - x.Price) <= tol) acc[^1].Add(x);
                     else acc.Add(new List<(double, string, Color)> { x });
                     return acc;
                 }))
        {
            s.Tag(LastBar, g[0].Price, string.Join(" / ", g.Select(x => x.Text)) + " " + Price(g[0].Price), Pal.A(g[0].Color, 200), Color.White);
        }
    }
}

/// <summary>Los pivotes de piso clásicos (P, R1–R3, S1–S3) del período anterior terminado.</summary>
[DisplayName("Pivotes de piso (floor pivots)")]
[Category(IndicatorCategories.Structure)]
[Description("La escalera clásica de pivotes de piso (P, R1–R3, S1–S3) con el máximo, mínimo y cierre del período anterior terminado (día, semana, mes o año). Marca los cruces del pivote y los cierres sobre R2 o bajo S2.")]
public class FloorPivots : SceneIndicator
{
    public enum R3Kind
    {
        [Display(Name = "Clásico")] Classic,
        [Display(Name = "Múltiplo del rango")] RangeMultiple,
    }

    [Display(Name = "Período", GroupName = "Cálculo", Order = 10)] public CalendarPeriod Anchor { get; set; } = CalendarPeriod.Day;
    [Display(Name = "Cálculo de R3 / S3", GroupName = "Cálculo", Order = 11)] public R3Kind Third { get; set; } = R3Kind.Classic;

    [Display(Name = "Períodos que se dibujan", GroupName = "Cálculo", Order = 12)]
    [Range(1, 60)]
    public int Keep { get; set; } = 10;

    [Display(Name = "Marcar los cruces", GroupName = "Cálculo", Order = 13)] public bool ShowCrosses { get; set; } = true;
    [Display(Name = "Pivote", GroupName = "Colores", Order = 20)] public Color PivotColor { get; set; } = Pal.Amber;
    [Display(Name = "Resistencias", GroupName = "Colores", Order = 21)] public Color ResistanceColor { get; set; } = Pal.Bear;
    [Display(Name = "Soportes", GroupName = "Colores", Order = 22)] public Color SupportColor { get; set; } = Pal.Bull;

    protected override void Build(Scene s)
    {
        var key = PeriodKey(Anchor);
        var starts = new List<int>();
        for (int i = 0; i < Bars.Count; i++)
            if (i == 0 || key[i] != key[i - 1]) starts.Add(i);
        double lastP = double.NaN, lastR2 = double.NaN, lastS2 = double.NaN;
        for (int k = Math.Max(1, starts.Count - Keep); k < starts.Count; k++)
        {
            int ps = starts[k - 1], cs = starts[k], ce = k + 1 < starts.Count ? starts[k + 1] - 1 : -1;
            if (k == 1 && ps == 0 && Offset > 0) continue; // el primero puede estar cortado
            double h = double.MinValue, l = double.MaxValue;
            for (int i = ps; i < cs; i++)
            {
                h = Math.Max(h, Bars[i].H);
                l = Math.Min(l, Bars[i].L);
            }

            double c = Bars[cs - 1].C, pv = (h + l + c) / 3, rng = h - l;
            double r1 = 2 * pv - l, s1 = 2 * pv - h, r2 = pv + rng, s2 = pv - rng;
            double r3 = Third == R3Kind.Classic ? h + 2 * (pv - l) : pv + 2 * rng, s3 = Third == R3Kind.Classic ? l - 2 * (h - pv) : pv - 2 * rng;
            bool current = ce < 0;
            foreach (var (name, v, col) in new[] { ("R3", r3, ResistanceColor), ("R2", r2, ResistanceColor), ("R1", r1, ResistanceColor), ("P", pv, PivotColor), ("S1", s1, SupportColor), ("S2", s2, SupportColor), ("S3", s3, SupportColor) })
                s.HLine(cs, ce, v, col, name == "P" ? 2 : 1, label: current ? $"{name} {Price(v)}" : null, alert: current ? "Pivote " + name : null);
            if (ShowCrosses)
            {
                for (int i = Math.Max(cs, 1); i <= Math.Min(ce < 0 ? LastClosed : ce, LastClosed); i++)
                {
                    var b = Bars[i];
                    var pb = Bars[i - 1];
                    if (pb.C <= pv && b.C > pv) s.Mark(i, b.L, MarkKind.TriangleUp, PivotColor, null, false);
                    else if (pb.C >= pv && b.C < pv) s.Mark(i, b.H, MarkKind.TriangleDown, PivotColor);
                    if (pb.C <= r2 && b.C > r2) s.Mark(i, b.L, MarkKind.Circle, ResistanceColor, "R2", false);
                    if (pb.C >= s2 && b.C < s2) s.Mark(i, b.H, MarkKind.Circle, SupportColor, "S2");
                }
            }

            if (current)
            {
                lastP = pv;
                lastR2 = r2;
                lastS2 = s2;
            }
        }

        if (double.IsNaN(lastP)) return;
        double close = Bars[LastBar].C;
        s.DashTitle = "Pivotes de piso";
        s.Row("Pivote", Price(lastP), PivotColor);
        s.Row("Precio contra el pivote", close >= lastP ? "arriba" : "abajo", close >= lastP ? SupportColor : ResistanceColor);
        s.Row("R2 / S2", $"{Price(lastR2)} / {Price(lastS2)}");
    }
}

/// <summary>Pivotes estándar de TradingView: tradicional, Fibonacci, Woodie, clásico, DeMark y Camarilla.</summary>
[DisplayName("Pivotes estándar (tradicional, Fibonacci, Woodie, Camarilla…)")]
[Category(IndicatorCategories.Structure)]
[Description("Escaleras de pivotes tradicionales, Fibonacci, Woodie, clásicos, DeMark y Camarilla del período anterior terminado (automático según la vela, o día, semana, mes, trimestre, año), con sus nombres y precios.")]
public class StandardPivots : SceneIndicator
{
    public enum PivotType
    {
        [Display(Name = "Tradicional")] Traditional,
        [Display(Name = "Fibonacci")] Fibonacci,
        [Display(Name = "Woodie")] Woodie,
        [Display(Name = "Clásico")] Classic,
        [Display(Name = "DeMark")] DeMark,
        [Display(Name = "Camarilla")] Camarilla,
    }

    public enum PivotPeriod
    {
        [Display(Name = "Automático")] Auto,
        [Display(Name = "Día")] Day,
        [Display(Name = "Semana")] Week,
        [Display(Name = "Mes")] Month,
        [Display(Name = "Trimestre")] Quarter,
        [Display(Name = "Año")] Year,
    }

    [Display(Name = "Tipo", GroupName = "Cálculo", Order = 10)] public PivotType Type { get; set; } = PivotType.Traditional;
    [Display(Name = "Período", GroupName = "Cálculo", Order = 11)] public PivotPeriod Period { get; set; } = PivotPeriod.Auto;

    [Display(Name = "Pivotes hacia atrás", GroupName = "Cálculo", Order = 12)]
    [Range(1, 40)]
    public int Back { get; set; } = 15;

    [Display(Name = "Nombres", GroupName = "Etiquetas", Order = 20)] public bool ShowLabels { get; set; } = true;
    [Display(Name = "Precios", GroupName = "Etiquetas", Order = 21)] public bool ShowPrices { get; set; } = true;
    [Display(Name = "Etiquetas a la derecha", GroupName = "Etiquetas", Order = 22)] public bool LabelsRight { get; set; }
    [Display(Name = "Grosor", GroupName = "Niveles", Order = 30)] [Range(1, 10)] public int Width { get; set; } = 1;
    [Display(Name = "P", GroupName = "Niveles", Order = 31)] public bool ShowP { get; set; } = true;
    [Display(Name = "R1 / S1", GroupName = "Niveles", Order = 32)] public bool Tier1 { get; set; } = true;
    [Display(Name = "R2 / S2", GroupName = "Niveles", Order = 33)] public bool Tier2 { get; set; } = true;
    [Display(Name = "R3 / S3", GroupName = "Niveles", Order = 34)] public bool Tier3 { get; set; } = true;
    [Display(Name = "R4 / S4", GroupName = "Niveles", Order = 35)] public bool Tier4 { get; set; } = true;
    [Display(Name = "R5 / S5", GroupName = "Niveles", Order = 36)] public bool Tier5 { get; set; } = true;
    [Display(Name = "Pivote", GroupName = "Colores", Order = 40)] public Color PivotColor { get; set; } = Pal.Amber;
    [Display(Name = "Resistencias", GroupName = "Colores", Order = 41)] public Color ResistanceColor { get; set; } = Pal.Bear;
    [Display(Name = "Soportes", GroupName = "Colores", Order = 42)] public Color SupportColor { get; set; } = Pal.Bull;

    public StandardPivots() => ShowDashboard = false;

    /// <summary>Los niveles de un tipo con la vela anterior (O, H, L, C) y la apertura del período actual.</summary>
    public static List<(string Name, double Value)> Ladder(PivotType t, double o, double h, double l, double c, double curOpen)
    {
        double r = h - l;
        var res = new List<(string, double)>();
        switch (t)
        {
            case PivotType.Fibonacci:
            {
                double p = (h + l + c) / 3;
                res.AddRange(new[] { ("P", p), ("R1", p + 0.382 * r), ("S1", p - 0.382 * r), ("R2", p + 0.618 * r), ("S2", p - 0.618 * r), ("R3", p + r), ("S3", p - r) });
                break;
            }
            case PivotType.Woodie:
            {
                double p = (h + l + 2 * curOpen) / 4;
                double r3 = h + 2 * (p - l), s3 = l - 2 * (h - p);
                res.AddRange(new[] { ("P", p), ("R1", 2 * p - l), ("S1", 2 * p - h), ("R2", p + r), ("S2", p - r), ("R3", r3), ("S3", s3), ("R4", r3 + r), ("S4", s3 - r) });
                break;
            }
            case PivotType.Classic:
            {
                double p = (h + l + c) / 3;
                res.AddRange(new[] { ("P", p), ("R1", 2 * p - l), ("S1", 2 * p - h), ("R2", p + r), ("S2", p - r), ("R3", p + 2 * r), ("S3", p - 2 * r), ("R4", p + 3 * r), ("S4", p - 3 * r) });
                break;
            }
            case PivotType.DeMark:
            {
                double x = c < o ? h + 2 * l + c : c > o ? 2 * h + l + c : h + l + 2 * c;
                res.AddRange(new[] { ("P", x / 4), ("R1", x / 2 - l), ("S1", x / 2 - h) });
                break;
            }
            case PivotType.Camarilla:
            {
                double p = (h + l + c) / 3, r5 = l != 0 ? h / l * c : double.NaN;
                res.AddRange(new[]
                {
                    ("P", p), ("R1", c + 1.1 * r / 12), ("S1", c - 1.1 * r / 12), ("R2", c + 1.1 * r / 6), ("S2", c - 1.1 * r / 6), ("R3", c + 1.1 * r / 4), ("S3", c - 1.1 * r / 4),
                    ("R4", c + 1.1 * r / 2), ("S4", c - 1.1 * r / 2), ("R5", r5), ("S5", c - (r5 - c)),
                });
                break;
            }
            default:
            {
                double p = (h + l + c) / 3;
                res.AddRange(new[]
                {
                    ("P", p), ("R1", 2 * p - l), ("S1", 2 * p - h), ("R2", p + r), ("S2", p - r), ("R3", 2 * p + (h - 2 * l)), ("S3", 2 * p - (2 * h - l)),
                    ("R4", 3 * p + (h - 3 * l)), ("S4", 3 * p - (3 * h - l)), ("R5", 4 * p + (h - 4 * l)), ("S5", 4 * p - (4 * h - l)),
                });
                break;
            }
        }

        return res.Where(x => !double.IsNaN(x.Item2)).ToList();
    }

    protected override void Build(Scene s)
    {
        var period = Period switch
        {
            PivotPeriod.Day => CalendarPeriod.Day,
            PivotPeriod.Week => CalendarPeriod.Week,
            PivotPeriod.Month => CalendarPeriod.Month,
            PivotPeriod.Quarter => CalendarPeriod.Quarter,
            PivotPeriod.Year => CalendarPeriod.Year,
            _ => AutoPeriod(),
        };
        var key = PeriodKey(period);
        var starts = new List<int>();
        for (int i = 0; i < Bars.Count; i++)
            if (i == 0 || key[i] != key[i - 1]) starts.Add(i);
        for (int k = Math.Max(1, starts.Count - Back); k < starts.Count; k++)
        {
            int ps = starts[k - 1], cs = starts[k], ce = k + 1 < starts.Count ? starts[k + 1] - 1 : -1;
            if (k == 1 && Offset > 0) continue; // el primer período puede estar cortado
            double h = double.MinValue, l = double.MaxValue;
            for (int i = ps; i < cs; i++)
            {
                h = Math.Max(h, Bars[i].H);
                l = Math.Min(l, Bars[i].L);
            }

            bool current = ce < 0;
            foreach (var (name, v) in Ladder(Type, Bars[ps].O, h, l, Bars[cs - 1].C, Bars[cs].O))
            {
                int tier = name == "P" ? 0 : name[1] - '0';
                if (!(tier switch { 0 => ShowP, 1 => Tier1, 2 => Tier2, 3 => Tier3, 4 => Tier4, _ => Tier5 })) continue;
                var col = name == "P" ? PivotColor : name[0] == 'R' ? ResistanceColor : SupportColor;
                string? label = ShowLabels || ShowPrices ? $"{(ShowLabels ? name : "")}{(ShowLabels && ShowPrices ? " " : "")}{(ShowPrices ? "(" + Price(v) + ")" : "")}" : null;
                s.Lines.Add(new Scene.LineShape(cs, ce, v, v, col, Width, LineDashStyle.Solid, label, LabelsRight, current ? "Pivote " + name : null));
            }
        }
    }
}

/// <summary>Line break de tres líneas (shinne ashi) sobre los cierres, con sus gatillos y un gráfico de line break al lado.</summary>
[DisplayName("Line break (tres líneas)")]
[Category(IndicatorCategories.Structure)]
[Description("Line break de N líneas sobre los cierres: una línea nueva sigue la tendencia cuando el cierre pasa la última, y la da vuelta cuando pasa el extremo de las últimas N. Muestra la línea activa, los dos gatillos (seguir y dar vuelta), las vueltas, las rachas largas y un gráfico de line break a la derecha.")]
public class LineBreak : SceneIndicator
{
    public enum CloseFrame
    {
        [Display(Name = "El del gráfico")] Chart,
        [Display(Name = "15 min")] M15,
        [Display(Name = "1 hora")] H1,
        [Display(Name = "4 horas")] H4,
        [Display(Name = "1 día")] D1,
    }

    private readonly PaintbarsDataSeries _paint = new("LBPaint", "Velas") { IsHidden = true };

    [Display(Name = "Líneas para dar vuelta", GroupName = "Cálculo", Order = 10)] [Range(1, 10)] public int LineCount { get; set; } = 3;
    [Display(Name = "Cierres de", GroupName = "Cálculo", Order = 11)] public CloseFrame Frame { get; set; } = CloseFrame.Chart;
    [Display(Name = "Racha larga (líneas)", GroupName = "Cálculo", Order = 12)] [Range(2, 50)] public int ExtendedRun { get; set; } = 8;
    [Display(Name = "Líneas históricas", GroupName = "Dibujo", Order = 20)] public bool ShowLines { get; set; } = true;
    [Display(Name = "Marcas de vuelta", GroupName = "Dibujo", Order = 21)] public bool ShowMarkers { get; set; } = true;
    [Display(Name = "Gatillos en el precio", GroupName = "Dibujo", Order = 22)] public bool ShowTriggers { get; set; } = true;
    [Display(Name = "Pintar las velas", GroupName = "Dibujo", Order = 23)] public bool ColorBars { get; set; }
    [Display(Name = "Gráfico de line break a la derecha", GroupName = "Dibujo", Order = 24)] public bool ShowLineChart { get; set; } = true;
    [Display(Name = "Líneas del gráfico", GroupName = "Dibujo", Order = 25)] [Range(5, 100)] public int ChartLines { get; set; } = 30;
    [Display(Name = "Etiquetas de vuelta", GroupName = "Dibujo", Order = 26)] public bool ShowReversalLabels { get; set; } = true;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 30)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 31)] public Color BearColor { get; set; } = Pal.Bear;

    public LineBreak() => DataSeries.Add(_paint);

    private readonly record struct LbLine(int Bar, double Open, double Close, bool Up);

    protected override void Build(Scene s)
    {
        int n = Bars.Count;
        if (n < 3) return;
        long sec = Frame switch { CloseFrame.M15 => 900, CloseFrame.H1 => 3600, CloseFrame.H4 => 14400, CloseFrame.D1 => 86400, _ => 0 };
        // los cierres que cuentan, en la vela del gráfico donde ya se sabe (la siguiente al cierre del bloque)
        var closes = new List<(int Bar, double Close)>();
        for (int i = 0; i <= LastClosed; i++)
        {
            if (sec == 0) closes.Add((i, Bars[i].C));
            else if (i + 1 < n && (Frame == CloseFrame.D1 ? DayKey[i + 1] != DayKey[i] : Bars[i + 1].T / sec != Bars[i].T / sec)) closes.Add((i + 1, Bars[i].C));
        }

        var lines = new List<LbLine>();
        var trendAt = new int[n];
        int trend = 0, run = 0;
        foreach (var (bar, c) in closes)
        {
            if (lines.Count == 0)
            {
                lines.Add(new LbLine(bar, Bars[0].O, c, c >= Bars[0].O));
                trend = lines[0].Up ? 1 : -1;
                run = 1;
                continue;
            }

            var last = lines[^1];
            var lastN = lines.TakeLast(LineCount).ToList();
            double hi = lastN.Max(x => Math.Max(x.Open, x.Close)), lo = lastN.Min(x => Math.Min(x.Open, x.Close));
            if (last.Up)
            {
                if (c > last.Close)
                {
                    lines.Add(new LbLine(bar, last.Close, c, true));
                    run++;
                    if (run == ExtendedRun && ShowMarkers) s.Mark(bar, Bars[bar].L, MarkKind.TriangleUp, BullColor, run.ToString(), false);
                }
                else if (c < lo)
                {
                    lines.Add(new LbLine(bar, Math.Min(last.Open, last.Close), c, false));
                    run = 1;
                    if (ShowMarkers) s.Mark(bar, Bars[bar].H, MarkKind.Circle, BearColor);
                    if (ShowReversalLabels) s.Tag(bar, Bars[bar].H, "Vuelta bajista " + Price(c), Pal.A(BearColor, 210), Color.White);
                }
            }
            else
            {
                if (c < last.Close)
                {
                    lines.Add(new LbLine(bar, last.Close, c, false));
                    run++;
                    if (run == ExtendedRun && ShowMarkers) s.Mark(bar, Bars[bar].H, MarkKind.TriangleDown, BearColor, run.ToString());
                }
                else if (c > hi)
                {
                    lines.Add(new LbLine(bar, Math.Max(last.Open, last.Close), c, true));
                    run = 1;
                    if (ShowMarkers) s.Mark(bar, Bars[bar].L, MarkKind.Circle, BullColor, null, false);
                    if (ShowReversalLabels) s.Tag(bar, Bars[bar].L, "Vuelta alcista " + Price(c), Pal.A(BullColor, 210), Color.White, false);
                }
            }

            trend = lines[^1].Up ? 1 : -1;
        }

        if (lines.Count == 0) return;
        // la línea activa y los gatillos, en escalera desde donde nace cada línea
        for (int k = 0; k < lines.Count; k++)
        {
            var ln = lines[k];
            int to = k + 1 < lines.Count ? lines[k + 1].Bar - 1 : -1;
            for (int i = ln.Bar; i <= (to < 0 ? n - 1 : to); i++) trendAt[i] = ln.Up ? 1 : -1;
            if (ShowLines && k >= lines.Count - 60)
            {
                var col = ln.Up ? BullColor : BearColor;
                s.Box(ln.Bar, to, Math.Max(ln.Open, ln.Close), Math.Min(ln.Open, ln.Close), Pal.A(col, 25), Pal.A(col, 90));
            }

            if (!ShowTriggers || k < lines.Count - 40) continue;
            var lastN = lines.Take(k + 1).TakeLast(LineCount).ToList();
            double rev = ln.Up ? lastN.Min(x => Math.Min(x.Open, x.Close)) : lastN.Max(x => Math.Max(x.Open, x.Close));
            bool current = to < 0;
            s.HLine(ln.Bar, to, ln.Close, ln.Up ? BullColor : BearColor, 1, LineDashStyle.Dot, current ? "Sigue " + Price(ln.Close) : null, current ? "Line break · sigue" : null);
            s.HLine(ln.Bar, to, rev, ln.Up ? BearColor : BullColor, 1, LineDashStyle.Dash, current ? "Da vuelta " + Price(rev) : null, current ? "Line break · da vuelta" : null);
        }

        for (int i = 0; i < n; i++) _paint[i + Offset] = ColorBars && trendAt[i] != 0 ? (trendAt[i] > 0 ? BullColor : BearColor) : null;

        // el gráfico de line break a la derecha de la última vela: un bloque por línea
        if (ShowLineChart)
        {
            var tail = lines.TakeLast(ChartLines).ToList();
            for (int k = 0; k < tail.Count; k++)
            {
                var ln = tail[k];
                var col = ln.Up ? BullColor : BearColor;
                s.Box(LastBar + 3 + k, LastBar + 3 + k, Math.Max(ln.Open, ln.Close), Math.Min(ln.Open, ln.Close), Pal.A(col, 170), col);
            }
        }

        var cur = lines[^1];
        var nLast = lines.TakeLast(LineCount).ToList();
        double revNow = cur.Up ? nLast.Min(x => Math.Min(x.Open, x.Close)) : nLast.Max(x => Math.Max(x.Open, x.Close));
        s.DashTitle = $"Line break ({LineCount} líneas)";
        s.Row("Tendencia", cur.Up ? "alcista" : "bajista", cur.Up ? BullColor : BearColor);
        int streak = 0;
        for (int k = lines.Count - 1; k >= 0 && lines[k].Up == cur.Up; k--) streak++;
        s.Row("Líneas seguidas", streak.ToString());
        s.Row("Sigue por encima de", Price(cur.Close), cur.Up ? BullColor : BearColor);
        s.Row("Da vuelta en", Price(revNow), cur.Up ? BearColor : BullColor);
    }
}
