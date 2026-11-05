using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

using TradingHub.Indicators.Native.VpUltimate;

namespace TradingHub.Indicators.Native.Structure;

/// <summary>
/// Períodos mayores en el gráfico (ICT / PO3): las últimas velas de dos períodos mayores dibujadas a la derecha, sus
/// FVG sin llenar más cercanos al precio, la secuencia PO3 (C1 → C2 barrido y vuelta → C3 → C4) con sus T-Spot, el
/// CISD que la confirma con las proyecciones STDV, y los breakers / unicornios. Portado del módulo «结构» de VPFM
/// (HTF SUITE, Bandz Po3 Profiler, HTF FVGs Tap, Unicorn Model).
/// </summary>
[DisplayName("Períodos mayores: velas, FVG, PO3 y breakers (ICT)")]
[Category(IndicatorCategories.Structure)]
[Description("Velas de dos períodos mayores a la derecha, sus FVG sin llenar, la secuencia PO3 con T-Spot, CISD + STDV y breakers / unicornios.")]
public class HtfStructure : StructureIndicator
{
    private List<Bar> _h1 = new(), _h2 = new();
    private long _s1, _s2;
    private readonly List<(Gap Gap, long Tf, int Start)> _htfFvg = new();
    private List<Po3Sequence> _po3 = new();
    private List<Breaker> _breakers = new();
    private List<Gap> _fvgs = new();
    private Cisd? _cisd;
    private List<(double D, double Price)> _stdv = new();
    private int _c1Idx, _c2Idx, _c3Idx;
    private int _tf1Minutes, _tf2Minutes;

    [Display(Name = "Primer período mayor (minutos, 0 = automático)", GroupName = "Cálculo", Order = 10)]
    [Range(0, 50000)]
    public int FirstMinutes { get => _tf1Minutes; set { _tf1Minutes = Math.Max(0, value); RecalculateValues(); } }

    [Display(Name = "Segundo período mayor (minutos, 0 = automático)", GroupName = "Cálculo", Order = 11)]
    [Range(0, 50000)]
    public int SecondMinutes { get => _tf2Minutes; set { _tf2Minutes = Math.Max(0, value); RecalculateValues(); } }

    [Display(Name = "Velas de los períodos mayores (derecha)", GroupName = "Capas", Order = 20)]
    public bool ShowCandles { get; set; } = true;

    [Display(Name = "FVG de períodos mayores", GroupName = "Capas", Order = 21)]
    public bool ShowHtfFvg { get; set; } = true;

    [Display(Name = "PO3 y T-Spot", GroupName = "Capas", Order = 22)]
    public bool ShowPo3 { get; set; } = true;

    [Display(Name = "CISD y proyecciones STDV", GroupName = "Capas", Order = 23)]
    public bool ShowStdv { get; set; } = true;

    [Display(Name = "Breakers / unicornios", GroupName = "Capas", Order = 24)]
    public bool ShowBreakers { get; set; } = true;

    [Display(Name = "Alcista", GroupName = "Colores", Order = 30)]
    public Color BullColor { get; set; } = Color.FromArgb(255, 38, 166, 154);

    [Display(Name = "Bajista", GroupName = "Colores", Order = 31)]
    public Color BearColor { get; set; } = Color.FromArgb(255, 239, 83, 80);

    [Display(Name = "PO3", GroupName = "Colores", Order = 32)]
    public Color Po3Color { get; set; } = Color.FromArgb(255, 242, 179, 61);

    [Display(Name = "Texto", GroupName = "Colores", Order = 33)]
    public Color TextColor { get; set; } = Color.FromArgb(255, 135, 150, 171);

    private int IdxAtOrAfter(long t)
    {
        int lo = 0, hi = Bars.Count - 1, r = Bars.Count;
        while (lo <= hi)
        {
            int m = (lo + hi) >> 1;
            if (Bars[m].T >= t) { r = m; hi = m - 1; }
            else lo = m + 1;
        }

        return r;
    }

    protected override void Compute()
    {
        var len = BarLength > TimeSpan.Zero ? BarLength : TimeSpan.FromMinutes(5);
        var (a, b) = Smc.HigherTimeframes(len);
        _s1 = _tf1Minutes > 0 ? _tf1Minutes * 60L : a;
        _s2 = _tf2Minutes > 0 ? _tf2Minutes * 60L : b;
        if (_s1 <= (long)len.TotalSeconds) _s1 = Math.Max(a, (long)len.TotalSeconds * 4);
        if (_s2 <= _s1) _s2 = Math.Max(b, _s1 * 4);
        _h1 = Smc.Aggregate(Bars, _s1);
        _h2 = Smc.Aggregate(Bars, _s2);
        _po3 = Smc.Po3(_h1);
        _fvgs = Smc.Fvg(Bars);
        double px = Bars[^1].C;
        _htfFvg.Clear();
        foreach (var (tf, hb) in new[] { (_s1, _h1), (_s2, _h2) })
        {
            if (hb.Count < 5) continue;
            double atr = Smc.Atr(hb);
            var list = Smc.Fvg(hb, atr * 0.1).Where(f => f.Filled < 0 && f.I > hb.Count - 120)
                .Select(f => (f, tf, IdxAtOrAfter(hb[f.I - 1].T), Math.Min(Math.Abs(f.Top - px), Math.Abs(f.Bottom - px))))
                .OrderBy(x => x.Item4).Take(4);
            foreach (var (f, t, start, _) in list) _htfFvg.Add((f, t, start));
        }

        _breakers = Smc.Breakers(Bars, _h1, _s1, _fvgs);
        _cisd = null;
        _stdv = new List<(double, double)>();
        var seq = _po3.FirstOrDefault();
        if (seq != null)
        {
            _c1Idx = IdxAtOrAfter(seq.C1.T);
            _c2Idx = IdxAtOrAfter(seq.C2.T);
            _c3Idx = seq.I2 + 1 < _h1.Count ? IdxAtOrAfter(_h1[seq.I2 + 1].T) : Bars.Count;
            var cisds = Smc.Cisds(Bars);
            _cisd = cisds.FirstOrDefault(c => c.Bull == (seq.Side == 1) && c.I >= _c2Idx);
            if (_cisd != null) _stdv = Smc.Stdv(_cisd.Level, seq.Extreme);
        }
    }

    /// <summary>Lo que dibuja, para las alertas: FVG de los períodos mayores, PO3 (C1, barrido, T-Spots), CISD / STDV y breakers.</summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        if (Bars.Count == 0) yield break;
        int last = Bars.Count - 1;
        if (ShowHtfFvg)
            foreach (var (g, tf, start) in _htfFvg)
                foreach (var l in BoxLevels("hfvg" + tf + "." + g.Dir, $"FVG {Smc.TfName(tf)}", Math.Min(start, last), -1, g.Top, g.Bottom)) yield return l;
        var seq = _po3.FirstOrDefault();
        if (ShowPo3 && seq != null)
        {
            yield return Level("c1h", "C1 alto", _c1Idx, -1, seq.C1.H);
            yield return Level("c1l", "C1 bajo", _c1Idx, -1, seq.C1.L);
            yield return Level("c2", seq.Side == 1 ? "C2 barrido bajo" : "C2 barrido alto", _c2Idx, -1, seq.Extreme);
            if (seq.TSpot3 is { } t3)
                foreach (var l in BoxLevels("ts3", "T-Spot C3", Math.Min(_c3Idx, last), -1, Math.Max(t3.From, t3.To), Math.Min(t3.From, t3.To))) yield return l;
            if (seq.TSpot4 is { } t4)
            {
                int c4 = seq.I2 + 2 < _h1.Count ? IdxAtOrAfter(_h1[seq.I2 + 2].T) : last;
                foreach (var l in BoxLevels("ts4", "T-Spot C4", Math.Min(c4, last), -1, Math.Max(t4.From, t4.To), Math.Min(t4.From, t4.To))) yield return l;
            }
        }

        if (ShowStdv && _cisd != null)
        {
            yield return Level("cisd", "CISD", _cisd.Start, -1, _cisd.Level);
            foreach (var (d, price) in _stdv)
                yield return Level("stdv" + d.ToString(System.Globalization.CultureInfo.InvariantCulture), $"STDV {d:0.#}", _cisd.I, -1, price);
        }

        if (ShowBreakers)
            foreach (var b in _breakers)
                foreach (var l in BoxLevels("brk" + b.Side, b.Unicorn ? "Unicornio" : "Breaker", b.I0, b.Invalid >= 0 ? b.Invalid : -1, b.Top, b.Bottom)) yield return l;
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Bars.Count == 0) return;
        int last = Bars.Count - 1;
        if (ShowHtfFvg)
            foreach (var (g, tf, start) in _htfFvg)
            {
                var c = g.Dir == 1 ? BullColor : BearColor;
                Box(ctx, Math.Min(start, last), -1, g.Top, g.Bottom, Vp.Alpha(c, 40), Vp.Alpha(c, 120));
                LabelLeft(ctx, $"FVG {Smc.TfName(tf)}", X(Math.Min(start, last)) + 3, Y((g.Top + g.Bottom) / 2), Vp.Alpha(TextColor, 220));
            }

        var seq = _po3.FirstOrDefault();
        if (ShowPo3 && seq != null)
        {
            // C1: alto y bajo; C2: extremo barrido; T-Spot de C3 y C4
            HLine(ctx, _c1Idx, -1, seq.C1.H, Vp.Alpha(Po3Color, 170), 1, LineDashStyle.Dash);
            HLine(ctx, _c1Idx, -1, seq.C1.L, Vp.Alpha(Po3Color, 170), 1, LineDashStyle.Dash);
            LabelLeft(ctx, "C1 alto", X(_c1Idx) + 3, Y(seq.C1.H), Po3Color);
            LabelLeft(ctx, "C1 bajo", X(_c1Idx) + 3, Y(seq.C1.L), Po3Color);
            HLine(ctx, _c2Idx, -1, seq.Extreme, Po3Color, 2);
            LabelLeft(ctx, (seq.Side == 1 ? "C2 barrido bajo" : "C2 barrido alto") + (seq.TwoSided ? " (dos lados)" : ""), X(_c2Idx) + 3, Y(seq.Extreme), Po3Color, true);
            if (seq.TSpot3 is { } t3)
            {
                Box(ctx, Math.Min(_c3Idx, last), -1, Math.Max(t3.From, t3.To), Math.Min(t3.From, t3.To), Vp.Alpha(Po3Color, 45), Vp.Alpha(Po3Color, 130));
                LabelLeft(ctx, "T-Spot C3", X(Math.Min(_c3Idx, last)) + 3, Y((t3.From + t3.To) / 2), Po3Color);
            }

            if (seq.TSpot4 is { } t4)
            {
                int c4 = seq.I2 + 2 < _h1.Count ? IdxAtOrAfter(_h1[seq.I2 + 2].T) : last;
                Box(ctx, Math.Min(c4, last), -1, Math.Max(t4.From, t4.To), Math.Min(t4.From, t4.To), Vp.Alpha(Po3Color, 45), Vp.Alpha(Po3Color, 130));
                LabelLeft(ctx, "T-Spot C4", X(Math.Min(c4, last)) + 3, Y((t4.From + t4.To) / 2), Po3Color);
            }
        }

        if (ShowStdv && _cisd != null)
        {
            var col = _cisd.Bull ? BullColor : BearColor;
            HLine(ctx, _cisd.Start, -1, _cisd.Level, col, 1, LineDashStyle.DashDot);
            LabelLeft(ctx, "CISD", X(_cisd.I) + 3, Y(_cisd.Level), col, true);
            foreach (var (d, price) in _stdv)
            {
                HLine(ctx, _cisd.I, -1, price, Vp.Alpha(col, 150), 1, LineDashStyle.Dot);
                LabelLeft(ctx, $"STDV {d:0.#}", X(_cisd.I) + 3, Y(price), Vp.Alpha(col, 200));
            }
        }

        if (ShowBreakers)
            foreach (var b in _breakers)
            {
                var c = b.Side == 1 ? BullColor : BearColor;
                int end = b.Invalid >= 0 ? b.Invalid : -1;
                Box(ctx, b.I0, end, b.Top, b.Bottom, Vp.Alpha(c, b.Unicorn ? 60 : 35), Vp.Alpha(c, 140));
                LabelLeft(ctx, b.Unicorn ? "Unicornio" : "Breaker", X(b.I0) + 3, Y((b.Top + b.Bottom) / 2), c, b.Unicorn);
                // el nivel barrido
                HLine(ctx, b.Sweep, b.Confirm, b.Level, Vp.Alpha(c, 120), 1, LineDashStyle.Dot);
            }

        if (ShowCandles) DrawHtfCandles(ctx, seq);
    }

    /// <summary>Las últimas 4 velas del primer período mayor y 3 del segundo, a la derecha del precio.</summary>
    private void DrawHtfCandles(RenderContext ctx, Po3Sequence? seq)
    {
        var region = ChartInfo!.PriceChartContainer.Region;
        int x = RightEdge - 8;
        int cw = 9, gap = 5;
        foreach (var (bars, tf, count) in new[] { (_h2, _s2, 3), (_h1, _s1, 4) })
        {
            if (bars.Count == 0) continue;
            var list = bars.Skip(Math.Max(0, bars.Count - count)).ToList();
            int width = list.Count * (cw + gap);
            x -= width;
            if (x < region.Left) break;
            int topY = int.MaxValue;
            foreach (var b in list) topY = Math.Min(topY, Y(b.H));
            var size = ctx.MeasureString(Smc.TfName(tf), BoldFont);
            ctx.DrawString(Smc.TfName(tf), BoldFont, TextColor, x, Math.Max(region.Top + 2, topY - size.Height - 4));
            for (int k = 0; k < list.Count; k++)
            {
                var b = list[k];
                int cx = x + k * (cw + gap) + cw / 2;
                int yo = Y(b.O), yc = Y(b.C), yh = Y(b.H), yl = Y(b.L);
                var col = b.C >= b.O ? BullColor : BearColor;
                ctx.DrawLine(Vp.Pen(col, 1), cx, yh, cx, yl);
                ctx.FillRectangle(col, new Rectangle(cx - cw / 2, Math.Min(yo, yc), cw, Math.Max(1, Math.Abs(yc - yo))));
                if (seq != null && ReferenceEquals(bars, _h1))
                {
                    int idx = bars.Count - list.Count + k;
                    string lab = idx == seq.I1 ? "C1" : idx == seq.I2 ? "C2" : idx == seq.I2 + 1 ? "C3" : idx == seq.I2 + 2 ? "C4" : "";
                    if (lab.Length > 0) Label(ctx, lab, cx, yl, Po3Color, true, above: false);
                }
            }

            x -= 14;
        }
    }
}
