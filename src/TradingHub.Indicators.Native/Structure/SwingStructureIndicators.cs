using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Structure;

// Estructura por swings de la sección 12: cambios de estructura con barridos, estructura anclada de tres niveles, oscilador
// de régimen, panel de dispersión, panel de tendencia y extensiones de ondas. Los swings se confirman con velas a la
// derecha y cada evento queda en la vela en que se supo: nada se mueve hacia atrás.

/// <summary>Estructura de tres niveles (corto, intermedio y largo plazo) sobre máximos y mínimos crudos.</summary>
public static class TierStructure
{
    public sealed class Swing
    {
        public int Bar, Confirm;
        public double Price;
        public bool High;
    }

    public sealed class Break
    {
        public int Tier, From, Bar;
        public double Level;
        public int Dir;
        public bool Choch;
        public int Resolved = -1; // validación: la vela donde salió de la banda de ATR
        public bool Valid;
        public double ZoneTop, ZoneBottom;
    }

    public sealed class Result
    {
        public readonly List<Swing>[] Swings = { new(), new(), new() };
        public readonly List<Break> Breaks = new();

        /// <summary>Régimen de cada nivel en cada vela: +1 alcista, −1 bajista, 0 sin ruptura todavía.</summary>
        public int[][] Regime = Array.Empty<int[]>();
    }

    /// <summary>
    /// Swing corto: patrón de tres velas confirmado una vela después. Intermedio: un swing corto más alto (o más bajo) que
    /// sus dos vecinos cortos; largo: lo mismo sobre los intermedios. Un cierre a través del último swing de un nivel lo
    /// rompe una vez: BoS si la ruptura anterior fue para el mismo lado, CHoCH si fue al revés. La ruptura se valida con
    /// la banda de ATR(17): el primer cierre afuera de ella a favor la valida, en contra la anula.
    /// </summary>
    public static Result Run(IReadOnlyList<Bar> b, int upTo)
    {
        var r = new Result();
        int n = b.Count;
        r.Regime = new[] { new int[n], new int[n], new int[n] };
        var atr = Ta.Atr(b, 17);
        var lastHigh = new Swing?[3];
        var lastLow = new Swing?[3];
        var crossedH = new bool[3];
        var crossedL = new bool[3];
        var lastDir = new int[3];
        var pending = new Break?[3];
        var highs = new List<Swing>[] { new(), new(), new() };
        var lows = new List<Swing>[] { new(), new(), new() };
        void Add(int tier, Swing sw)
        {
            r.Swings[tier].Add(sw);
            var list = sw.High ? highs[tier] : lows[tier];
            list.Add(sw);
            if (sw.High)
            {
                lastHigh[tier] = sw;
                crossedH[tier] = false;
            }
            else
            {
                lastLow[tier] = sw;
                crossedL[tier] = false;
            }

            // el siguiente nivel: el del medio de tres swings seguidos de este lado si es el extremo
            if (tier < 2 && list.Count >= 3)
            {
                var a = list[^3];
                var m = list[^2];
                var c = list[^1];
                bool up = sw.High ? a.Price < m.Price && m.Price >= c.Price : a.Price > m.Price && m.Price <= c.Price;
                if (up) Add(tier + 1, new Swing { Bar = m.Bar, Confirm = sw.Confirm, Price = m.Price, High = sw.High });
            }
        }

        for (int i = 0; i <= upTo && i < n; i++)
        {
            if (i >= 2)
            {
                if (b[i - 2].H < b[i - 1].H && b[i - 1].H >= b[i].H) Add(0, new Swing { Bar = i - 1, Confirm = i, Price = b[i - 1].H, High = true });
                if (b[i - 2].L > b[i - 1].L && b[i - 1].L <= b[i].L) Add(0, new Swing { Bar = i - 1, Confirm = i, Price = b[i - 1].L, High = false });
            }

            for (int t = 0; t < 3; t++)
            {
                // validación de una ruptura pendiente
                if (pending[t] is { } pb)
                {
                    double up = pb.Level + atr[i], dn = pb.Level - atr[i];
                    if (b[i].C > up || b[i].C < dn)
                    {
                        pb.Resolved = i;
                        pb.Valid = pb.Dir > 0 ? b[i].C > up : b[i].C < dn;
                        pending[t] = null;
                    }
                    else
                    {
                        pb.ZoneTop = Math.Max(pb.ZoneTop, b[i].H);
                        pb.ZoneBottom = Math.Min(pb.ZoneBottom, b[i].L);
                    }
                }

                if (lastHigh[t] is { } h && !crossedH[t] && i > h.Confirm && b[i].C > h.Price)
                {
                    crossedH[t] = true;
                    var br = new Break { Tier = t, From = h.Bar, Bar = i, Level = h.Price, Dir = 1, Choch = lastDir[t] == -1, ZoneTop = b[i].H, ZoneBottom = h.Price };
                    r.Breaks.Add(br);
                    lastDir[t] = 1;
                    pending[t] = br;
                }

                if (lastLow[t] is { } l && !crossedL[t] && i > l.Confirm && b[i].C < l.Price)
                {
                    crossedL[t] = true;
                    var br = new Break { Tier = t, From = l.Bar, Bar = i, Level = l.Price, Dir = -1, Choch = lastDir[t] == 1, ZoneTop = l.Price, ZoneBottom = b[i].L };
                    r.Breaks.Add(br);
                    lastDir[t] = -1;
                    pending[t] = br;
                }

                r.Regime[t][i] = lastDir[t];
            }
        }

        for (int i = upTo + 1; i < n; i++)
            for (int t = 0; t < 3; t++)
                r.Regime[t][i] = r.Regime[t][Math.Max(0, upTo)];
        return r;
    }

    public static readonly string[] TierNames = { "CP", "IP", "LP" };
}

/// <summary>Cambios de estructura interna (BOS / CHoCH) con barridos y el nivel protegido.</summary>
[DisplayName("Cambios de estructura con barridos")]
[Category(IndicatorCategories.Structure)]
[Description("Estructura interna con su largo de swing: BOS cuando el cierre rompe a favor de la tendencia y CHoCH cuando la da vuelta. Marca los barridos (la mecha pasa el nivel y el cierre no) y dibuja el nivel protegido: el swing que tiene que aguantar para que la tendencia siga.")]
public class StructureShiftSweeps : SceneIndicator
{
    [Display(Name = "Largo de la estructura interna", GroupName = "Cálculo", Order = 10)] [Range(2, 100)] public int Length { get; set; } = 5;
    [Display(Name = "Filtro de confluencia (mechas)", GroupName = "Cálculo", Order = 11)] public bool Confluence { get; set; }
    [Display(Name = "BOS", GroupName = "Dibujo", Order = 20)] public bool ShowBos { get; set; } = true;
    [Display(Name = "Barridos", GroupName = "Dibujo", Order = 21)] public bool ShowSweeps { get; set; } = true;
    [Display(Name = "CHoCH", GroupName = "Dibujo", Order = 22)] public bool ShowChoch { get; set; } = true;
    [Display(Name = "Nivel protegido", GroupName = "Dibujo", Order = 23)] public bool ShowProtected { get; set; } = true;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 30)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 31)] public Color BearColor { get; set; } = Pal.Bear;

    protected override void Build(Scene s)
    {
        int last = LastClosed, L = Length;
        if (last < L * 2 + 2) return;
        int leg = -1, trend = 0;
        double hiLvl = double.NaN, loLvl = double.NaN;
        int hiBar = -1, loBar = -1;
        bool hiCrossed = true, loCrossed = true, hiSwept = false, loSwept = false, chochSeen = false;
        int bos = 0, choch = 0, sweeps = 0;
        double prot = double.NaN;
        int protBar = -1;
        for (int i = L; i <= last; i++)
        {
            // pierna: el máximo de hace L velas supera las L siguientes (o el mínimo)
            double winHi = double.MinValue, winLo = double.MaxValue;
            for (int k = i - L + 1; k <= i; k++)
            {
                winHi = Math.Max(winHi, Bars[k].H);
                winLo = Math.Min(winLo, Bars[k].L);
            }

            int prevLeg = leg;
            if (Bars[i - L].H > winHi) leg = 0;
            else if (Bars[i - L].L < winLo) leg = 1;
            if (prevLeg >= 0 && leg - prevLeg == 1)
            {
                loLvl = Bars[i - L].L;
                loBar = i - L;
                loCrossed = loSwept = false;
            }
            else if (prevLeg >= 0 && leg - prevLeg == -1)
            {
                hiLvl = Bars[i - L].H;
                hiBar = i - L;
                hiCrossed = hiSwept = false;
            }

            var b = Bars[i];
            var pb = Bars[i - 1];
            double range = Math.Max(1e-12, b.H - b.L);
            bool bullOk = !Confluence || (b.H - Math.Max(b.O, b.C)) / range < 0.5; // sin una mecha de arriba que rechace
            bool bearOk = !Confluence || (Math.Min(b.O, b.C) - b.L) / range < 0.5;
            if (!hiCrossed && !double.IsNaN(hiLvl) && i > hiBar + L)
            {
                if (b.C > hiLvl && pb.C <= hiLvl && bullOk)
                {
                    bool isChoch = trend == -1;
                    hiCrossed = true;
                    if (isChoch) chochSeen = true;
                    if (isChoch ? ShowChoch : ShowBos && (trend == 1 || chochSeen || trend == 0))
                    {
                        s.Segment(hiBar, i, hiLvl, hiLvl, BullColor, 1, isChoch ? LineDashStyle.Dash : LineDashStyle.Solid);
                        s.Tag((hiBar + i) / 2, hiLvl, isChoch ? "CHoCH" : "BOS", Color.Transparent, BullColor);
                        s.Mark(i, b.L, MarkKind.TriangleUp, BullColor, null, false);
                    }

                    if (isChoch) choch++;
                    else bos++;
                    trend = 1;
                    // el nivel protegido: el mínimo de donde salió la ruptura
                    if (!double.IsNaN(loLvl))
                    {
                        prot = loLvl;
                        protBar = loBar;
                    }
                }
                else if (!hiSwept && b.H > hiLvl && b.C < hiLvl)
                {
                    hiSwept = true;
                    sweeps++;
                    if (ShowSweeps) s.Mark(i, b.H, MarkKind.Circle, BearColor, "Barrido");
                }
            }

            if (!loCrossed && !double.IsNaN(loLvl) && i > loBar + L)
            {
                if (b.C < loLvl && pb.C >= loLvl && bearOk)
                {
                    bool isChoch = trend == 1;
                    loCrossed = true;
                    if (isChoch) chochSeen = true;
                    if (isChoch ? ShowChoch : ShowBos)
                    {
                        s.Segment(loBar, i, loLvl, loLvl, BearColor, 1, isChoch ? LineDashStyle.Dash : LineDashStyle.Solid);
                        s.Tag((loBar + i) / 2, loLvl, isChoch ? "CHoCH" : "BOS", Color.Transparent, BearColor, false);
                        s.Mark(i, b.H, MarkKind.TriangleDown, BearColor);
                    }

                    if (isChoch) choch++;
                    else bos++;
                    trend = -1;
                    if (!double.IsNaN(hiLvl))
                    {
                        prot = hiLvl;
                        protBar = hiBar;
                    }
                }
                else if (!loSwept && b.L < loLvl && b.C > loLvl)
                {
                    loSwept = true;
                    sweeps++;
                    if (ShowSweeps) s.Mark(i, b.L, MarkKind.Circle, BullColor, "Barrido", false);
                }
            }

            // el nivel protegido se pierde con un cierre del otro lado
            if (!double.IsNaN(prot) && (trend == 1 ? b.C < prot : trend == -1 && b.C > prot)) prot = double.NaN;
        }

        if (ShowProtected && !double.IsNaN(prot))
            s.HLine(protBar, -1, prot, trend > 0 ? BullColor : BearColor, 1, LineDashStyle.Dash, "Protegido " + Price(prot), "Nivel protegido");
        if (!double.IsNaN(hiLvl) && !hiCrossed) s.HLine(hiBar, -1, hiLvl, Pal.A(BearColor, 150), 1, LineDashStyle.Dot, alert: "Último máximo interno");
        if (!double.IsNaN(loLvl) && !loCrossed) s.HLine(loBar, -1, loLvl, Pal.A(BullColor, 150), 1, LineDashStyle.Dot, alert: "Último mínimo interno");
        s.DashTitle = "Estructura interna";
        s.Row("Tendencia", trend > 0 ? "alcista" : trend < 0 ? "bajista" : "—", trend > 0 ? BullColor : trend < 0 ? BearColor : default);
        s.Row("BOS / CHoCH / barridos", $"{bos} / {choch} / {sweeps}");
        if (!double.IsNaN(prot)) s.Row("Nivel protegido", Price(prot));
    }
}

/// <summary>Estructura anclada de tres niveles: swings y rupturas de corto, intermedio y largo plazo con su validación.</summary>
[DisplayName("Estructura de mercado anclada (3 niveles)")]
[Category(IndicatorCategories.Structure)]
[Description("Swings de corto, intermedio y largo plazo, sus rupturas (BoS / CHoCH) y la validación de cada ruptura con una banda de ATR: el primer cierre afuera de la banda la confirma o la anula. Con cajas de estructura, fondo en las rupturas y un tablero con la tendencia de cada nivel.")]
public class AnchoredMarketStructure : SceneIndicator
{
    [Display(Name = "Swings de corto plazo", GroupName = "Corto plazo", Order = 10)] public bool StSwings { get; set; } = true;
    [Display(Name = "Estructura de corto plazo", GroupName = "Corto plazo", Order = 11)] public bool StStruct { get; set; } = true;
    [Display(Name = "Swings intermedios", GroupName = "Intermedio", Order = 20)] public bool ItSwings { get; set; } = true;
    [Display(Name = "Estructura intermedia", GroupName = "Intermedio", Order = 21)] public bool ItStruct { get; set; } = true;
    [Display(Name = "Swings de largo plazo", GroupName = "Largo plazo", Order = 30)] public bool LtSwings { get; set; } = true;
    [Display(Name = "Estructura de largo plazo", GroupName = "Largo plazo", Order = 31)] public bool LtStruct { get; set; } = true;
    [Display(Name = "Validar las rupturas", GroupName = "Dibujo", Order = 40)] public bool Validate { get; set; } = true;
    [Display(Name = "Etiquetas", GroupName = "Dibujo", Order = 41)] public bool Labels { get; set; } = true;
    [Display(Name = "Cajas de estructura", GroupName = "Dibujo", Order = 42)] public bool ShowBoxes { get; set; } = true;
    [Display(Name = "Fondo en la ruptura", GroupName = "Dibujo", Order = 43)] public bool ShowBackground { get; set; } = true;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 50)] public Color BullColor { get; set; } = Color.FromArgb(255, 0, 176, 79);
    [Display(Name = "Bajista", GroupName = "Colores", Order = 51)] public Color BearColor { get; set; } = Color.FromArgb(255, 156, 27, 65);

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < 10) return;
        var r = TierStructure.Run(Bars, last);
        bool[] swingsOn = { StSwings, ItSwings, LtSwings }, structOn = { StStruct, ItStruct, LtStruct };
        int start = Math.Max(0, Bars.Count - 1500);
        for (int t = 0; t < 3; t++)
        {
            if (!swingsOn[t]) continue;
            foreach (var sw in r.Swings[t].Where(x => x.Bar >= start))
            {
                var kind = t switch { 0 => MarkKind.Circle, 1 => sw.High ? MarkKind.TriangleDown : MarkKind.TriangleUp, _ => MarkKind.Diamond };
                var c = Pal.A(sw.High ? BearColor : BullColor, t == 0 ? 110 : 220);
                s.Marks.Add(new Scene.MarkShape(sw.Bar, sw.Price, kind, c, t == 2 ? (sw.High ? "LPH" : "LPL") : t == 1 ? (sw.High ? "IPH" : "IPL") : null, sw.High, t == 0 ? 3 : 0));
            }
        }

        foreach (var br in r.Breaks.Where(x => x.Bar >= start && structOn[x.Tier]))
        {
            var c = br.Dir > 0 ? BullColor : BearColor;
            var style = br.Tier switch { 0 => LineDashStyle.Dot, 1 => LineDashStyle.Dash, _ => LineDashStyle.Solid };
            s.Segment(br.From, br.Bar, br.Level, br.Level, c, br.Tier == 2 ? 2 : 1, style);
            if (Labels) s.Tag((br.From + br.Bar) / 2, br.Level, TierStructure.TierNames[br.Tier] + " " + (br.Choch ? "CHoCH" : "BoS"), Color.Transparent, c, br.Dir > 0);
            if (ShowBackground && br.Tier >= 1) s.Tints[br.Bar] = Pal.A(c, 20);
            if (Validate && br.Resolved >= 0)
            {
                if (ShowBoxes) s.Box(br.Bar, br.Resolved, br.ZoneTop, br.ZoneBottom, Pal.A(c, 22), Pal.A(c, 80));
                s.Mark(br.Resolved, br.Dir > 0 ? Bars[br.Resolved].L : Bars[br.Resolved].H, br.Valid ? MarkKind.Square : MarkKind.Cross, br.Valid ? c : Pal.Muted, br.Valid ? "✓" : "✕", br.Dir < 0);
            }
        }

        s.DashTitle = "Estructura anclada";
        string[] names = { "Corto plazo", "Intermedio", "Largo plazo" };
        for (int t = 0; t < 3; t++)
        {
            int reg = r.Regime[t][LastBar];
            var lastBr = r.Breaks.LastOrDefault(x => x.Tier == t);
            string detail = lastBr == null ? "" : " · " + (lastBr.Choch ? "CHoCH" : "BoS") + (lastBr.Resolved < 0 ? " (validando)" : lastBr.Valid ? " ✓" : " ✕");
            s.Row(names[t], (reg > 0 ? "alcista" : reg < 0 ? "bajista" : "—") + detail, reg > 0 ? BullColor : reg < 0 ? BearColor : default);
        }

        foreach (var t in new[] { 0, 1, 2 })
        {
            var hi = r.Swings[t].LastOrDefault(x => x.High);
            var lo = r.Swings[t].LastOrDefault(x => !x.High);
            if (t == 2 && hi != null) s.HLine(hi.Bar, -1, hi.Price, Pal.A(BearColor, 120), 1, LineDashStyle.Dot, alert: "Máximo de largo plazo");
            if (t == 2 && lo != null) s.HLine(lo.Bar, -1, lo.Price, Pal.A(BullColor, 120), 1, LineDashStyle.Dot, alert: "Mínimo de largo plazo");
        }
    }
}

/// <summary>Oscilador 0–100 de la estructura de tres niveles con su ciclo (en un panel).</summary>
[DisplayName("Oscilador de régimen de estructura")]
[Category(IndicatorCategories.Structure)]
[Description("Un oscilador de 0 a 100 armado con la estructura de corto, intermedio y largo plazo: cada nivel dice su régimen (alcista arriba de 50, bajista abajo) y dónde está el cierre en el rango desde su ruptura. El ciclo (histograma desde 50) es el oscilador menos su media, y se marcan los cruces.")]
public class StructureRegimeOscillator : Indicator
{
    private readonly ValueDataSeries _comp = new("MS", "Estructura") { Width = 2, Color = Color.FromArgb(255, 230, 230, 230) };
    private readonly ValueDataSeries _st = new("ST", "Corto plazo") { Color = Color.FromArgb(160, 41, 182, 246), VisualType = VisualMode.Hide };
    private readonly ValueDataSeries _it = new("IT", "Intermedio") { Color = Color.FromArgb(160, 171, 71, 188), VisualType = VisualMode.Hide };
    private readonly ValueDataSeries _lt = new("LT", "Largo plazo") { Color = Color.FromArgb(160, 255, 167, 38), VisualType = VisualMode.Hide };
    private readonly ValueDataSeries _cycle = new("CY", "Ciclo") { VisualType = VisualMode.Hide, ShowZeroValue = true };
    private double[] _cyc = Array.Empty<double>();
    private int _computed = -1;
    private int _w1 = 1, _w2 = 3, _w3 = 2, _smooth = 4, _cycLen = 7;

    public StructureRegimeOscillator() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _comp;
        DataSeries.Add(_st);
        DataSeries.Add(_it);
        DataSeries.Add(_lt);
        DataSeries.Add(_cycle);
        LineSeries.Add(new LineSeries("Mid", "50") { Value = 50, Color = Color.FromArgb(110, 140, 140, 150), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("Up", "80") { Value = 80, Color = Color.FromArgb(60, 140, 140, 150), LineDashStyle = LineDashStyle.Dot });
        LineSeries.Add(new LineSeries("Dn", "20") { Value = 20, Color = Color.FromArgb(60, 140, 140, 150), LineDashStyle = LineDashStyle.Dot });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Peso del corto plazo", GroupName = "Cálculo", Order = 10)] [Range(0, 10)] public int ShortWeight { get => _w1; set { _w1 = value; RecalculateValues(); } }
    [Display(Name = "Peso del intermedio", GroupName = "Cálculo", Order = 11)] [Range(0, 10)] public int MidWeight { get => _w2; set { _w2 = value; RecalculateValues(); } }
    [Display(Name = "Peso del largo plazo", GroupName = "Cálculo", Order = 12)] [Range(0, 10)] public int LongWeight { get => _w3; set { _w3 = value; RecalculateValues(); } }
    [Display(Name = "Suavizado (velas)", GroupName = "Cálculo", Order = 13)] [Range(1, 50)] public int Smooth { get => _smooth; set { _smooth = value; RecalculateValues(); } }
    [Display(Name = "Media del ciclo", GroupName = "Cálculo", Order = 14)] [Range(2, 100)] public int CycleLength { get => _cycLen; set { _cycLen = value; RecalculateValues(); } }
    [Display(Name = "Histograma del ciclo", GroupName = "Dibujo", Order = 20)] public bool ShowCycle { get; set; } = true;
    [Display(Name = "Línea del corto plazo", GroupName = "Dibujo", Order = 23)] public bool ShowShort { get => _st.VisualType != VisualMode.Hide; set => _st.VisualType = value ? VisualMode.Line : VisualMode.Hide; }
    [Display(Name = "Línea del intermedio", GroupName = "Dibujo", Order = 24)] public bool ShowMid { get => _it.VisualType != VisualMode.Hide; set => _it.VisualType = value ? VisualMode.Line : VisualMode.Hide; }
    [Display(Name = "Línea del largo plazo", GroupName = "Dibujo", Order = 25)] public bool ShowLong { get => _lt.VisualType != VisualMode.Hide; set => _lt.VisualType = value ? VisualMode.Line : VisualMode.Hide; }
    [Display(Name = "Cruces de la estructura (50)", GroupName = "Dibujo", Order = 21)] public bool StructureCrosses { get; set; } = true;
    [Display(Name = "Cruces del ciclo", GroupName = "Dibujo", Order = 22)] public bool CycleCrosses { get; set; } = true;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 30)] public Color BullColor { get; set; } = Color.FromArgb(255, 0, 176, 79);
    [Display(Name = "Bajista", GroupName = "Colores", Order = 31)] public Color BearColor { get; set; } = Color.FromArgb(255, 156, 27, 65);

    protected override void OnRecalculate() => _computed = -1;

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < CurrentBar - 1 || _computed == CurrentBar) return;
        _computed = CurrentBar;
        int n = CurrentBar;
        var bars = new List<Bar>(n);
        for (int i = 0; i < n; i++)
        {
            var c = GetCandle(i);
            bars.Add(new Bar(0, (double)c.Open, (double)c.High, (double)c.Low, (double)c.Close, (double)c.Volume));
        }

        var r = TierStructure.Run(bars, n - 1);
        var tiers = new double[3][];
        int[] w = { _w1, _w2, _w3 };
        for (int t = 0; t < 3; t++)
        {
            var raw = new double[n];
            int reg = 0;
            double hi = 0, lo = 0;
            for (int i = 0; i < n; i++)
            {
                int now = r.Regime[t][i];
                if (now != reg)
                {
                    reg = now;
                    hi = lo = bars[i].C;
                }

                hi = Math.Max(hi, bars[i].C);
                lo = Math.Min(lo, bars[i].C);
                double pos = hi > lo ? (bars[i].C - lo) / (hi - lo) : 0.5;
                raw[i] = reg == 0 ? double.NaN : reg > 0 ? 50 + 50 * pos : 50 * pos;
            }

            var sm = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                int k = 0;
                for (int j = Math.Max(0, i - _smooth + 1); j <= i; j++)
                    if (!double.IsNaN(raw[j]))
                    {
                        sum += raw[j];
                        k++;
                    }

                sm[i] = k > 0 ? sum / k : double.NaN;
            }

            tiers[t] = sm;
        }

        var comp = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0, ws = 0;
            for (int t = 0; t < 3; t++)
                if (!double.IsNaN(tiers[t][i]) && w[t] > 0)
                {
                    sum += tiers[t][i] * w[t];
                    ws += w[t];
                }

            comp[i] = ws > 0 ? sum / ws : 50;
        }

        var ema = Ta.Ema(comp, _cycLen);
        _cyc = new double[n];
        for (int i = 0; i < n; i++)
        {
            _cyc[i] = Math.Clamp(50 + (comp[i] - ema[i]) * 2, 0, 100);
            _comp[i] = (decimal)comp[i];
            _comp.Colors[i] = comp[i] >= 50 ? BullColor : BearColor;
            _st[i] = double.IsNaN(tiers[0][i]) ? 0 : (decimal)tiers[0][i];
            _it[i] = double.IsNaN(tiers[1][i]) ? 0 : (decimal)tiers[1][i];
            _lt[i] = double.IsNaN(tiers[2][i]) ? 0 : (decimal)tiers[2][i];
            _cycle[i] = (decimal)_cyc[i];
        }
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box || _cyc.Length == 0) return;
        int first = Math.Max(1, FirstVisibleBarNumber), last = Math.Min(LastVisibleBarNumber, _cyc.Length - 1);
        int bw = Math.Max(1, ChartInfo.PriceChartContainer.BarsWidth);
        int y50 = box.GetYByValue(50);
        for (int i = first; i <= last; i++)
        {
            int x = ChartInfo.GetXByBar(i, false);
            if (ShowCycle)
            {
                int y = box.GetYByValue((decimal)_cyc[i]);
                var c = Pal.A(_cyc[i] >= 50 ? BullColor : BearColor, 150);
                ctx.FillRectangle(c, new Rectangle(x - Math.Max(1, bw / 3), Math.Min(y, y50), Math.Max(1, bw * 2 / 3), Math.Max(1, Math.Abs(y - y50))));
            }

            double c0 = (double)_comp[i - 1], c1 = (double)_comp[i];
            if (StructureCrosses && (c0 - 50) * (c1 - 50) < 0)
            {
                int y = box.GetYByValue((decimal)c1);
                ctx.FillEllipse(c1 > 50 ? BullColor : BearColor, new Rectangle(x - 4, y - 4, 8, 8));
            }

            if (CycleCrosses && (_cyc[i - 1] - 50) * (_cyc[i] - 50) < 0)
            {
                int y = _cyc[i] > 50 ? box.GetYByValue(8) : box.GetYByValue(92);
                var c = _cyc[i] > 50 ? BullColor : BearColor;
                ctx.FillPolygon(c, _cyc[i] > 50
                    ? new[] { new Point(x, y - 5), new Point(x + 4, y + 3), new Point(x - 4, y + 3) }
                    : new[] { new Point(x, y + 5), new Point(x + 4, y - 3), new Point(x - 4, y - 3) });
            }
        }
    }
}

/// <summary>Panel de dispersión de la estructura: el tamaño de cada impulso contra su retroceso, y velas por percentil.</summary>
[DisplayName("Panel de dispersión de la estructura")]
[Category(IndicatorCategories.Structure)]
[Description("Con los swings confirmados mide cada impulso y su retroceso, y los dibuja como puntos en un gráfico de dispersión (el tramo en curso resaltado). Pinta las velas según lo grande que es el tramo actual comparado con los anteriores, marca las rupturas de estructura y muestra una tabla con las estadísticas.")]
public class StructureScatterPanel : SceneIndicator
{
    public enum HeatMode
    {
        [Display(Name = "Combinado")] Combined,
        [Display(Name = "Impulso")] Impulse,
        [Display(Name = "Retroceso")] Pullback,
    }

    private readonly PaintbarsDataSeries _paint = new("SSPaint", "Velas") { IsHidden = true };
    private List<(double X, double Y, bool Bull)> _points = new();
    private (double X, double Y, bool Bull)? _live;

    [Display(Name = "Velas a cada lado del pivote", GroupName = "Cálculo", Order = 10)] [Range(2, 100)] public int PivotLength { get; set; } = 10;
    [Display(Name = "Pintar las velas (mapa de calor)", GroupName = "Dibujo", Order = 20)] public bool Heatmap { get; set; } = true;
    [Display(Name = "Mapa de calor de", GroupName = "Dibujo", Order = 21)] public HeatMode Mode { get; set; } = HeatMode.Pullback;
    [Display(Name = "Gráfico de dispersión", GroupName = "Dibujo", Order = 22)] public bool ShowScatter { get; set; } = true;
    [Display(Name = "Rupturas de estructura", GroupName = "Dibujo", Order = 23)] public bool ShowStructure { get; set; } = true;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 30)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 31)] public Color BearColor { get; set; } = Pal.Bear;

    public StructureScatterPanel() => DataSeries.Add(_paint);

    protected override void Build(Scene s)
    {
        int last = LastClosed, L = PivotLength;
        if (last < L * 2 + 3) return;
        // swings alternados (un máximo nuevo del mismo lado reemplaza al anterior si es más extremo)
        var alt = new List<Ta.Pivot>();
        var byConfirm = Ta.Pivots(Bars, L, L, last).ToLookup(p => p.Confirm);
        var points = new List<(double X, double Y, bool Bull)>();
        int trend = 0;
        double topY = double.NaN, botY = double.NaN;
        int topX = -1, botX = -1;
        bool topCross = true, botCross = true;
        var heat = new double[Bars.Count];
        var trendAt = new int[Bars.Count];
        double maxImp = 1e-12, maxPb = 1e-12;
        int bosUp = 0, bosDn = 0;
        for (int i = 0; i <= last; i++)
        {
            foreach (var p in byConfirm[i])
            {
                if (alt.Count > 0 && alt[^1].High == p.High)
                {
                    if (p.High ? p.Price > alt[^1].Price : p.Price < alt[^1].Price) alt[^1] = p;
                }
                else
                {
                    alt.Add(p);
                    if (alt.Count >= 3)
                    {
                        // impulso A→B y retroceso B→C (en % del precio y como fracción del impulso)
                        var a = alt[^3];
                        var bb = alt[^2];
                        var c = alt[^1];
                        double imp = Math.Abs(bb.Price - a.Price) / Math.Max(1e-12, a.Price) * 100;
                        double pbk = Math.Abs(c.Price - bb.Price) / Math.Max(1e-12, Math.Abs(bb.Price - a.Price));
                        points.Add((imp, pbk, bb.Price > a.Price));
                        maxImp = Math.Max(maxImp, imp);
                        maxPb = Math.Max(maxPb, pbk);
                    }

                    if (alt.Count > 100) alt.RemoveAt(0);
                }

                if (p.High)
                {
                    topY = p.Price;
                    topX = p.Bar;
                    topCross = false;
                }
                else
                {
                    botY = p.Price;
                    botX = p.Bar;
                    botCross = false;
                }
            }

            var b = Bars[i];
            if (!topCross && !double.IsNaN(topY) && b.C > topY)
            {
                topCross = true;
                bool choch = trend < 0;
                trend = 1;
                bosUp++;
                if (ShowStructure)
                {
                    s.Segment(topX, i, topY, topY, BullColor, 1, choch ? LineDashStyle.Dash : LineDashStyle.Solid);
                    s.Tag((topX + i) / 2, topY, choch ? "CHoCH" : "BOS", Color.Transparent, BullColor);
                }
            }

            if (!botCross && !double.IsNaN(botY) && b.C < botY)
            {
                botCross = true;
                bool choch = trend > 0;
                trend = -1;
                bosDn++;
                if (ShowStructure)
                {
                    s.Segment(botX, i, botY, botY, BearColor, 1, choch ? LineDashStyle.Dash : LineDashStyle.Solid);
                    s.Tag((botX + i) / 2, botY, choch ? "CHoCH" : "BOS", Color.Transparent, BearColor, false);
                }
            }

            trendAt[i] = trend;
            // el tramo en curso contra los anteriores (0–1)
            if (alt.Count >= 2)
            {
                var a = alt[^2];
                var c = alt[^1];
                double imp = Math.Abs(c.Price - a.Price) / Math.Max(1e-12, a.Price) * 100;
                double pbk = Math.Abs(b.C - c.Price) / Math.Max(1e-12, Math.Abs(c.Price - a.Price));
                double hi = imp / maxImp, hp = pbk / maxPb;
                heat[i] = Mode switch { HeatMode.Impulse => hi, HeatMode.Pullback => hp, _ => (hi + hp) / 2 };
            }
        }

        _points = points.TakeLast(150).ToList();
        _live = null;
        if (alt.Count >= 2)
        {
            var a = alt[^2];
            var c = alt[^1];
            double imp = Math.Abs(c.Price - a.Price) / Math.Max(1e-12, a.Price) * 100;
            _live = (imp, Math.Abs(Bars[LastBar].C - c.Price) / Math.Max(1e-12, Math.Abs(c.Price - a.Price)), c.Price > a.Price);
            s.Paths.Add(new Scene.PathShape(alt.TakeLast(12).Select(p => (p.Bar, p.Price)).ToList(), Pal.A(Pal.Muted, 160)));
        }

        for (int i = 0; i < Bars.Count; i++)
        {
            Color? col = null;
            if (Heatmap && heat[i] > 0)
            {
                var basis = trendAt[Math.Min(i, last)] >= 0 ? BullColor : BearColor;
                col = Pal.A(basis, (int)(80 + 175 * Math.Clamp(heat[i], 0, 1)));
            }

            _paint[i + Offset] = col;
        }

        s.DashTitle = "Dispersión de la estructura";
        s.Row("Tramos medidos", points.Count.ToString());
        if (points.Count > 0)
        {
            s.Row("Impulso medio", points.Average(p => p.X).ToString("0.00") + " %");
            s.Row("Retroceso medio", (points.Average(p => p.Y) * 100).ToString("0") + " %");
        }

        if (_live is { } lv) s.Row("Tramo actual", $"{lv.X:0.00} % · {lv.Y * 100:0} %", lv.Bull ? BullColor : BearColor);
        s.Row("Rupturas arriba / abajo", $"{bosUp} / {bosDn}");
    }

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (!ShowScatter || _points.Count == 0) return;
        int w = 190, h = 140;
        var d = DashRect;
        int x0 = d.IsEmpty ? region.Right - w - 8 : Math.Clamp(d.Right - w, region.Left + 4, region.Right - w - 4);
        int y0 = d.IsEmpty ? region.Top + 26 : d.Bottom + 6;
        if (y0 + h > region.Bottom) y0 = Math.Max(region.Top + 4, region.Bottom - h - 4);
        ctx.FillRectangle(Color.FromArgb(215, 18, 20, 26), new Rectangle(x0, y0, w, h), 5);
        ctx.DrawRectangle(new RenderPen(Color.FromArgb(90, 120, 130, 150), 1), new Rectangle(x0, y0, w, h), 5);
        var font = new RenderFont("Arial", 8);
        ctx.DrawString(UiText.Tr("Impulso (%) →"), font, Pal.Muted, x0 + 6, y0 + h - 14);
        ctx.DrawString(UiText.Tr("↑ Retroceso"), font, Pal.Muted, x0 + 6, y0 + 4);
        double mx = Math.Max(1e-9, _points.Max(p => p.X)) * 1.1, my = Math.Max(1e-9, Math.Min(3, _points.Max(p => p.Y))) * 1.1;
        if (_live is { } lv0)
        {
            mx = Math.Max(mx, lv0.X * 1.1);
            my = Math.Max(my, Math.Min(3, lv0.Y) * 1.1);
        }

        int px0 = x0 + 10, py0 = y0 + 18, pw = w - 20, ph = h - 36;
        ctx.DrawLine(new RenderPen(Color.FromArgb(70, 140, 140, 150), 1), px0, py0 + ph, px0 + pw, py0 + ph);
        ctx.DrawLine(new RenderPen(Color.FromArgb(70, 140, 140, 150), 1), px0, py0, px0, py0 + ph);
        // el 61.8 % de retroceso como referencia
        int yFib = py0 + ph - (int)(ph * Math.Min(1, 0.618 / my));
        ctx.DrawLine(new RenderPen(Color.FromArgb(60, 251, 140, 0), 1), px0, yFib, px0 + pw, yFib);
        foreach (var p in _points)
        {
            int x = px0 + (int)(pw * Math.Min(1, p.X / mx)), y = py0 + ph - (int)(ph * Math.Min(1, p.Y / my));
            ctx.FillEllipse(Pal.A(p.Bull ? BullColor : BearColor, 170), new Rectangle(x - 2, y - 2, 5, 5));
        }

        if (_live is { } lv)
        {
            int x = px0 + (int)(pw * Math.Min(1, lv.X / mx)), y = py0 + ph - (int)(ph * Math.Min(1, lv.Y / my));
            ctx.DrawEllipse(new RenderPen(Color.White, 2), new Rectangle(x - 5, y - 5, 10, 10));
        }
    }
}

/// <summary>Panel de tendencia de la estructura: BOS / CHoCH, barridos y FVG en el período elegido, con la alineación de seis períodos.</summary>
[DisplayName("Panel de tendencia de la estructura")]
[Category(IndicatorCategories.Structure)]
[Description("Estructura de mercado (BOS / CHoCH) en el período del gráfico o uno mayor, barridos de liquidez, FVG activos y velas pintadas según la tendencia. El tablero muestra la tendencia por estructura de seis períodos (del gráfico a la semana) con velas cerradas.")]
public class StructureTrendPanel : SceneIndicator
{
    public enum Frame
    {
        [Display(Name = "El del gráfico")] Chart,
        [Display(Name = "5 min")] M5,
        [Display(Name = "15 min")] M15,
        [Display(Name = "30 min")] M30,
        [Display(Name = "1 hora")] H1,
        [Display(Name = "2 horas")] H2,
        [Display(Name = "4 horas")] H4,
        [Display(Name = "6 horas")] H6,
        [Display(Name = "12 horas")] H12,
        [Display(Name = "1 día")] D1,
        [Display(Name = "1 semana")] W1,
    }

    private readonly PaintbarsDataSeries _paint = new("STPaint", "Velas") { IsHidden = true };

    [Display(Name = "Estructura", GroupName = "Estructura", Order = 10)] public bool ShowStructure { get; set; } = true;
    [Display(Name = "Período de la estructura", GroupName = "Estructura", Order = 11)] public Frame StructureFrame { get; set; } = Frame.Chart;
    [Display(Name = "Largo del swing", GroupName = "Estructura", Order = 12)] [Range(1, 50)] public int SwingLength { get; set; } = 5;
    [Display(Name = "Pintar las velas por la tendencia", GroupName = "Estructura", Order = 13)] public bool ColorCandles { get; set; } = true;
    [Display(Name = "Barridos de liquidez", GroupName = "Liquidez", Order = 20)] public bool ShowLiquidity { get; set; } = true;
    [Display(Name = "Período de la liquidez", GroupName = "Liquidez", Order = 21)] public Frame LiquidityFrame { get; set; } = Frame.Chart;
    [Display(Name = "FVG", GroupName = "FVG", Order = 30)] public bool ShowFvg { get; set; } = true;
    [Display(Name = "FVG activos como máximo", GroupName = "FVG", Order = 31)] [Range(1, 30)] public int FvgLimit { get; set; } = 5;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 40)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 41)] public Color BearColor { get; set; } = Pal.Bear;

    public StructureTrendPanel() => DataSeries.Add(_paint);

    private static long Seconds(Frame f) => f switch
    {
        Frame.M5 => 300, Frame.M15 => 900, Frame.M30 => 1800, Frame.H1 => 3600, Frame.H2 => 7200, Frame.H4 => 14400, Frame.H6 => 21600,
        Frame.H12 => 43200, Frame.D1 => 86400, Frame.W1 => 604800, _ => 0,
    };

    /// <summary>Las velas de un período (completas), cada una con la vela del gráfico donde ya se sabe.</summary>
    private (List<Bar> Bars, int[] Known) Frames(long sec)
    {
        int chartSec = (int)BarLength.TotalSeconds;
        if (sec <= 0 || sec <= chartSec) return (Bars.Take(Bars.Count).ToList(), Enumerable.Range(0, Bars.Count).ToArray());
        var res = new List<Bar>();
        var known = new List<int>();
        long Key(long t) => sec == 604800 ? (t + 3 * 86400) / sec : t / sec; // semanas desde el lunes
        for (int i = 0; i < Bars.Count; i++)
        {
            var b = Bars[i];
            if (res.Count > 0 && Key(res[^1].T) == Key(b.T))
            {
                var l = res[^1];
                l.H = Math.Max(l.H, b.H);
                l.L = Math.Min(l.L, b.L);
                l.C = b.C;
                l.V += b.V;
                res[^1] = l;
            }
            else
            {
                if (res.Count > 0) known[^1] = i; // la anterior se completa en esta vela
                res.Add(b);
                known.Add(-1);
            }
        }

        if (known.Count > 0) known[^1] = -1; // la última sigue abierta
        return (res, known.ToArray());
    }

    /// <summary>Estructura con swings de largo <paramref name="len"/>: devuelve la tendencia por vela (del período) y las rupturas.</summary>
    private static (int[] Trend, List<(int From, int Bar, double Level, int Dir, bool Choch)> Breaks, List<(int Bar, double Price, bool High)> Swings) Structure(IReadOnlyList<Bar> b, int len, int upTo)
    {
        var trend = new int[b.Count];
        var breaks = new List<(int, int, double, int, bool)>();
        var swings = new List<(int, double, bool)>();
        var piv = Ta.Pivots(b, len, len, upTo).ToLookup(p => p.Confirm);
        double hi = double.NaN, lo = double.NaN;
        int hiBar = -1, loBar = -1, t = 0;
        bool hiX = true, loX = true;
        for (int i = 0; i <= upTo && i < b.Count; i++)
        {
            foreach (var p in piv[i])
            {
                swings.Add((p.Bar, p.Price, p.High));
                if (p.High) { hi = p.Price; hiBar = p.Bar; hiX = false; }
                else { lo = p.Price; loBar = p.Bar; loX = false; }
            }

            if (!hiX && b[i].C > hi)
            {
                hiX = true;
                breaks.Add((hiBar, i, hi, 1, t < 0));
                t = 1;
            }

            if (!loX && b[i].C < lo)
            {
                loX = true;
                breaks.Add((loBar, i, lo, -1, t > 0));
                t = -1;
            }

            trend[i] = t;
        }

        for (int i = upTo + 1; i < b.Count; i++) trend[i] = t;
        return (trend, breaks, swings);
    }

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < SwingLength * 2 + 3) return;
        // estructura en su período: las velas del período se llevan a la vela del gráfico donde se completan
        var (fb, known) = Frames(Seconds(StructureFrame));
        bool chart = known.Length == Bars.Count && Seconds(StructureFrame) <= (long)BarLength.TotalSeconds;
        int fUp = chart ? last : Array.FindLastIndex(known, k => k >= 0 && k <= last);
        int[] Map(int f) => chart ? new[] { f } : new[] { FirstBarOf(f) };
        int FirstBarOf(int f)
        {
            if (chart) return f;
            long t = fb[f].T;
            int i = Bars.FindIndex(x => x.T >= t);
            return Math.Max(0, i);
        }

        var st = Structure(fb, SwingLength, Math.Max(0, fUp));
        var trendChart = new int[Bars.Count];
        int fi = 0, cur = 0;
        for (int i = 0; i < Bars.Count; i++)
        {
            if (chart) cur = st.Trend[Math.Min(i, st.Trend.Length - 1)];
            else
            {
                while (fi < known.Length && known[fi] >= 0 && known[fi] <= i) cur = st.Trend[fi++];
            }

            trendChart[i] = cur;
        }

        if (ShowStructure)
            foreach (var br in st.Breaks.TakeLast(60))
            {
                int from = Map(br.From)[0], to = chart ? br.Bar : (known[br.Bar] >= 0 ? known[br.Bar] : LastBar);
                var c = br.Dir > 0 ? BullColor : BearColor;
                s.Segment(from, to, br.Level, br.Level, c, 1, br.Choch ? LineDashStyle.Dash : LineDashStyle.Solid);
                s.Tag((from + to) / 2, br.Level, br.Choch ? "CHoCH" : "BOS", Color.Transparent, c, br.Dir > 0);
            }

        for (int i = 0; i < Bars.Count; i++) _paint[i + Offset] = ColorCandles && trendChart[i] != 0 ? Pal.A(trendChart[i] > 0 ? BullColor : BearColor, 230) : null;

        // barridos: la mecha pasa un swing (del período de la liquidez) y el cierre vuelve
        if (ShowLiquidity)
        {
            var (lb, lk) = Frames(Seconds(LiquidityFrame));
            bool lchart = lk.Length == Bars.Count && Seconds(LiquidityFrame) <= (long)BarLength.TotalSeconds;
            int lUp = lchart ? last : Array.FindLastIndex(lk, k => k >= 0 && k <= last);
            var sw = Structure(lb, SwingLength, Math.Max(0, lUp)).Swings;
            foreach (var (bar, price, high) in sw)
            {
                int from = lchart ? bar : FirstBarOf(bar);
                for (int i = from + SwingLength + 1; i <= last; i++)
                {
                    var b = Bars[i];
                    if (high && b.H > price && b.C < price)
                    {
                        s.Segment(from, i, price, price, Pal.A(BearColor, 150), 1, LineDashStyle.Dot);
                        s.Mark(i, b.H, MarkKind.Circle, BearColor, "x");
                        break;
                    }

                    if (!high && b.L < price && b.C > price)
                    {
                        s.Segment(from, i, price, price, Pal.A(BullColor, 150), 1, LineDashStyle.Dot);
                        s.Mark(i, b.L, MarkKind.Circle, BullColor, "x", false);
                        break;
                    }

                    if (high ? b.C > price : b.C < price) break;
                }
            }
        }

        if (ShowFvg)
        {
            var fvgs = new List<(int From, double Top, double Bottom, bool Bull, int End)>();
            for (int i = 2; i <= last; i++)
            {
                var a = Bars[i - 2];
                var b = Bars[i];
                if (b.L > a.H) fvgs.Add((i - 1, b.L, a.H, true, -1));
                else if (b.H < a.L) fvgs.Add((i - 1, a.L, b.H, false, -1));
                for (int k = 0; k < fvgs.Count; k++)
                {
                    var f = fvgs[k];
                    if (f.End >= 0 || f.From >= i - 1) continue;
                    if (f.Bull ? b.L <= f.Bottom : b.H >= f.Top) fvgs[k] = f with { End = i };
                }
            }

            foreach (var f in fvgs.Where(f => f.End < 0).TakeLast(FvgLimit))
            {
                var c = f.Bull ? BullColor : BearColor;
                s.Box(f.From, -1, f.Top, f.Bottom, Pal.A(c, 35), Pal.A(c, 110), "FVG", alert: "FVG");
            }
        }

        // alineación de la tendencia en seis períodos (con velas cerradas)
        s.DashTitle = "Alineación de la tendencia";
        int chartSec = (int)BarLength.TotalSeconds;
        var frames = new[] { (0L, "Gráfico"), (900L, "15 min"), (3600L, "1 hora"), (14400L, "4 horas"), (86400L, "1 día"), (604800L, "1 semana") };
        int up = 0, down = 0;
        foreach (var (sec, name) in frames)
        {
            if (sec != 0 && sec <= chartSec) continue;
            var (b2, k2) = Frames(sec);
            int u2 = sec == 0 ? last : Array.FindLastIndex(k2, k => k >= 0 && k <= last);
            if (u2 < SwingLength * 2 + 2)
            {
                s.Row(name, "—");
                continue;
            }

            int t = Structure(b2, SwingLength, u2).Trend[u2];
            if (t > 0) up++;
            else if (t < 0) down++;
            s.Row(name, t > 0 ? "alcista" : t < 0 ? "bajista" : "—", t > 0 ? BullColor : t < 0 ? BearColor : default);
        }

        s.Row("Alineación", up > down ? $"alcista {up}/{up + down}" : down > up ? $"bajista {down}/{up + down}" : "mixta", up > down ? BullColor : down > up ? BearColor : default);
    }
}

/// <summary>Ondas de impulso (cinco ondas) y cuál de ellas es la extendida, con objetivos y la zona de retroceso.</summary>
[DisplayName("Extensiones de ondas (Elliott)")]
[Category(IndicatorCategories.Structure)]
[Description("Con los swings confirmados arma un zigzag y busca impulsos de cinco ondas que respetan las reglas (la 3 no es la más corta, la 2 no pasa el inicio, la 4 no pisa la 1). Marca la onda extendida (1, 3 o 5), proyecta los objetivos de la onda 5 en formación (igualdad y Fibonacci) y la zona de retroceso después de una extensión.")]
public class WaveExtensions : SceneIndicator
{
    [Display(Name = "Largo del swing", GroupName = "Cálculo", Order = 10)] [Range(2, 100)] public int SwingLength { get; set; } = 10;
    [Display(Name = "Largo de las sub-ondas", GroupName = "Cálculo", Order = 11)] [Range(1, 50)] public int SubLength { get; set; } = 4;
    [Display(Name = "Razón de extensión", GroupName = "Cálculo", Order = 12)] [Range(1.05, 5)] public decimal ExtRatio { get; set; } = 1.618m;
    [Display(Name = "Pedir sub-ondas en la extendida", GroupName = "Cálculo", Order = 13)] public bool RequireSub { get; set; } = true;
    [Display(Name = "Tolerancia de igualdad (%)", GroupName = "Cálculo", Order = 14)] [Range(1, 50)] public decimal EqTolerance { get; set; } = 10;
    [Display(Name = "Objetivos de la onda 5", GroupName = "Dibujo", Order = 20)] public bool ShowTargets { get; set; } = true;
    [Display(Name = "Aviso de onda 5 extendida", GroupName = "Dibujo", Order = 21)] public bool ShowWatch { get; set; } = true;
    [Display(Name = "Zona de retroceso", GroupName = "Dibujo", Order = 22)] public bool ShowZone { get; set; } = true;
    [Display(Name = "Nombres de las ondas", GroupName = "Dibujo", Order = 23)] public bool ShowLabels { get; set; } = true;
    [Display(Name = "Camino de las ondas", GroupName = "Dibujo", Order = 24)] public bool ShowPath { get; set; } = true;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 30)] public Color BullColor { get; set; } = Color.FromArgb(255, 0, 176, 79);
    [Display(Name = "Bajista", GroupName = "Colores", Order = 31)] public Color BearColor { get; set; } = Color.FromArgb(255, 156, 27, 65);
    [Display(Name = "Neutro", GroupName = "Colores", Order = 32)] public Color NeutralColor { get; set; } = Pal.Neutral;

    protected override void Build(Scene s)
    {
        int last = LastClosed, L = SwingLength;
        if (last < L * 2 + 5) return;
        var zz = new List<Ta.Pivot>();
        var byConfirm = Ta.Pivots(Bars, L, L, last).ToLookup(p => p.Confirm);
        int bull = 0, bear = 0, e1 = 0, e3 = 0, e5 = 0;
        string lastImpulse = "—";
        var impulses = new List<(Ta.Pivot[] P, int Ext, double Ratio, int Confirm)>();
        (double Eq, double Fib, int From, bool Up)? targets = null;
        bool watch = false;
        for (int i = 0; i <= last; i++)
        {
            foreach (var p in byConfirm[i].OrderBy(p => p.High ? 0 : 1))
            {
                if (zz.Count > 0 && zz[^1].High == p.High)
                {
                    if (p.High ? p.Price >= zz[^1].Price : p.Price <= zz[^1].Price) zz[^1] = p;
                    continue;
                }

                zz.Add(p);
                if (zz.Count > 8) zz.RemoveAt(0);
                int m = zz.Count - 1;
                targets = null;
                watch = false;
                // onda 4 en formación (5 pivotes): objetivos de la 5
                if (m >= 4)
                {
                    var w = zz.Skip(zz.Count - 5).ToArray();
                    bool up = w[1].Price > w[0].Price;
                    double l1 = Math.Abs(w[1].Price - w[0].Price), l3 = Math.Abs(w[3].Price - w[2].Price);
                    bool rules = up ? w[2].Price > w[0].Price && w[3].Price > w[1].Price && w[4].Price > w[1].Price : w[2].Price < w[0].Price && w[3].Price < w[1].Price && w[4].Price < w[1].Price;
                    if (rules)
                    {
                        double dir = up ? 1 : -1;
                        targets = (w[4].Price + dir * l1, w[4].Price + dir * 0.618 * Math.Abs(w[3].Price - w[0].Price), w[4].Bar, up);
                        // la 1 y la 3 casi iguales: suele extenderse la 5
                        watch = Math.Abs(l1 - l3) / Math.Max(l1, l3) * 100 <= (double)EqTolerance;
                    }
                }

                // impulso completo (6 pivotes)
                if (m >= 5)
                {
                    var w = zz.Skip(zz.Count - 6).ToArray();
                    bool up = w[1].Price > w[0].Price;
                    double l1 = Math.Abs(w[1].Price - w[0].Price), l3 = Math.Abs(w[3].Price - w[2].Price), l5 = Math.Abs(w[5].Price - w[4].Price);
                    bool rules = up
                        ? w[2].Price > w[0].Price && w[3].Price > w[1].Price && w[4].Price > w[1].Price && w[5].Price > w[3].Price
                        : w[2].Price < w[0].Price && w[3].Price < w[1].Price && w[4].Price < w[1].Price && w[5].Price < w[3].Price;
                    rules &= !(l3 < l1 && l3 < l5);
                    if (!rules) continue;
                    var lens = new[] { l1, l3, l5 };
                    int ext = Array.IndexOf(lens, lens.Max());
                    double next = lens.Where((_, k) => k != ext).Max();
                    double ratio = next > 0 ? lens[ext] / next : 0;
                    int extWave = ext * 2 + 1;
                    if (ratio < (double)ExtRatio) extWave = 0;
                    if (extWave > 0 && RequireSub)
                    {
                        // la extendida tiene que tener sus propias sub-ondas (pivotes cortos adentro de su tramo)
                        int a = w[ext * 2].Bar, b2 = w[ext * 2 + 1].Bar;
                        int subs = Ta.Pivots(Bars.Skip(a).Take(b2 - a + 1).ToList(), SubLength, SubLength, b2 - a).Count;
                        if (subs < 3) extWave = 0;
                    }

                    impulses.Add((w, extWave, ratio, i));
                    if (up) bull++;
                    else bear++;
                    if (extWave == 1) e1++;
                    else if (extWave == 3) e3++;
                    else if (extWave == 5) e5++;
                    lastImpulse = (up ? "alcista" : "bajista") + (extWave > 0 ? $" · onda {extWave} extendida ({ratio:0.00})" : " · sin extensión");
                }
            }
        }

        foreach (var (p, ext, ratio, confirm) in impulses.TakeLast(6))
        {
            bool up = p[1].Price > p[0].Price;
            var c = up ? BullColor : BearColor;
            if (ShowPath) s.Paths.Add(new Scene.PathShape(p.Select(x => (x.Bar, x.Price)).ToList(), Pal.A(c, 200), 2));
            if (ShowLabels)
                for (int k = 1; k <= 5; k++)
                    s.Tag(p[k].Bar, p[k].Price, k == ext ? $"{k}↑" : k.ToString(), k == ext ? Pal.A(c, 220) : Color.Transparent, k == ext ? Color.White : c, p[k].High);
            if (ext > 0) s.Segment(p[ext - 1].Bar, p[ext].Bar, p[ext - 1].Price, p[ext].Price, NeutralColor, 3);
            if (ShowZone && ext > 0)
            {
                // después de una extensión el precio suele volver al 38.2–61.8 % de la onda extendida
                double a = p[ext - 1].Price, b = p[ext].Price;
                double z1 = b - (b - a) * 0.382, z2 = b - (b - a) * 0.618;
                int reached = -1;
                for (int i = confirm + 1; i <= last; i++)
                    if (Bars[i].L <= Math.Max(z1, z2) && Bars[i].H >= Math.Min(z1, z2))
                    {
                        reached = i;
                        break;
                    }

                s.Box(p[5].Bar, reached >= 0 ? reached : -1, z1, z2, Pal.A(NeutralColor, 28), Pal.A(NeutralColor, 110), reached >= 0 ? "Zona alcanzada" : "Zona de retroceso", alert: reached < 0 ? "Zona de retroceso de la extensión" : null);
            }
        }

        if (ShowTargets && targets is { } tg)
        {
            var c = tg.Up ? BullColor : BearColor;
            s.HLine(tg.From, -1, tg.Eq, c, 1, LineDashStyle.Dash, "Onda 5 = onda 1 " + Price(tg.Eq), "Objetivo de la onda 5 (igualdad)");
            s.HLine(tg.From, -1, tg.Fib, c, 1, LineDashStyle.Dot, "Onda 5 0.618 " + Price(tg.Fib), "Objetivo de la onda 5 (0.618)");
            if (ShowWatch && watch) s.Tag(tg.From, Bars[tg.From].C, "Atención: posible onda 5 extendida", Pal.A(NeutralColor, 210), Color.White, !tg.Up);
        }

        if (ShowPath && zz.Count >= 2) s.Paths.Add(new Scene.PathShape(zz.Select(x => (x.Bar, x.Price)).ToList(), Pal.A(Pal.Muted, 120)));
        s.DashTitle = "Extensiones de ondas";
        s.Row("Impulsos alcistas / bajistas", $"{bull} / {bear}");
        s.Row("Extendida la 1 / 3 / 5", $"{e1} / {e3} / {e5}");
        s.Row("Último impulso", lastImpulse);
        if (targets != null) s.Row("Onda 4 en formación", watch ? "posible 5 extendida" : "objetivos proyectados");
    }
}
