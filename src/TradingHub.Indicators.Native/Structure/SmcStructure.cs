using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

using TradingHub.Indicators.Native.VpUltimate;

namespace TradingHub.Indicators.Native.Structure;

/// <summary>
/// Smart Money Concepts en el gráfico: swings (HH / HL / LH / LL), rupturas de estructura (BOS) y cambios de carácter
/// (CHoCH), order blocks (y breakers), FVG, liquidez en iguales (EQH / EQL) con su barrido y CISD. Portado del módulo
/// «结构» de VPFM (smart-money-concepts).
/// </summary>
[DisplayName("Estructura SMC (swings, BOS/CHoCH, OB, FVG, liquidez)")]
[Category(IndicatorCategories.Structure)]
[Description("Swings con HH/HL/LH/LL, BOS y CHoCH, order blocks y breakers, FVG (con mitigación), iguales EQH/EQL con barrido y CISD; el algoritmo de smart-money-concepts.")]
public class SmcStructure : StructureIndicator
{
    private List<Swing> _swings = new();
    private List<StructureBreak> _breaks = new();
    private List<Gap> _fvgs = new();
    private List<OrderBlock> _blocks = new();
    private List<LiquidityPool> _pools = new();
    private List<Cisd> _cisds = new();
    private int _swingLen = 10;

    [Display(Name = "Velas a cada lado del swing", GroupName = "Cálculo", Order = 10)]
    [Range(2, 100)]
    public int SwingLength { get => _swingLen; set { _swingLen = Math.Clamp(value, 2, 100); RecalculateValues(); } }

    [Display(Name = "Ruptura por cierre (no por mecha)", GroupName = "Cálculo", Order = 11)]
    public bool CloseBreak { get; set; } = true;

    [Display(Name = "Swings (HH / HL / LH / LL)", GroupName = "Capas", Order = 20)]
    public bool ShowSwings { get; set; } = true;

    [Display(Name = "BOS / CHoCH", GroupName = "Capas", Order = 21)]
    public bool ShowBreaks { get; set; } = true;

    [Display(Name = "Order blocks", GroupName = "Capas", Order = 22)]
    public bool ShowOrderBlocks { get; set; }

    [Display(Name = "Máximo de cajas por capa (las últimas)", GroupName = "Capas", Order = 27)]
    [Range(1, 200)]
    public int MaxBoxes { get; set; } = 12;

    [Display(Name = "FVG", GroupName = "Capas", Order = 23)]
    public bool ShowFvg { get; set; } = true;

    [Display(Name = "FVG ya llenados también", GroupName = "Capas", Order = 24)]
    public bool ShowFilledFvg { get; set; }

    [Display(Name = "Liquidez en iguales (EQH / EQL)", GroupName = "Capas", Order = 25)]
    public bool ShowLiquidity { get; set; } = true;

    [Display(Name = "CISD", GroupName = "Capas", Order = 26)]
    public bool ShowCisd { get; set; }

    [Display(Name = "Alcista", GroupName = "Colores", Order = 30)]
    public Color BullColor { get; set; } = Color.FromArgb(255, 38, 166, 154);

    [Display(Name = "Bajista", GroupName = "Colores", Order = 31)]
    public Color BearColor { get; set; } = Color.FromArgb(255, 239, 83, 80);

    [Display(Name = "Liquidez", GroupName = "Colores", Order = 32)]
    public Color LiquidityColor { get; set; } = Color.FromArgb(255, 255, 202, 40);

    [Display(Name = "Texto", GroupName = "Colores", Order = 33)]
    public Color TextColor { get; set; } = Color.FromArgb(255, 200, 205, 215);

    protected override void Compute()
    {
        _swings = Smc.Swings(Bars, _swingLen);
        _breaks = Smc.BosChoch(Bars, _swings, CloseBreak);
        _fvgs = Smc.Fvg(Bars);
        _blocks = Smc.OrderBlocks(Bars, _swings);
        _pools = Smc.Liquidity(Bars, _swings);
        _cisds = Smc.Cisds(Bars);
    }

    /// <summary>
    /// Lo que dibuja, para las alertas: BOS / CHoCH, EQH / EQL, CISD (los 40 más recientes de cada uno) y los bordes de FVG y
    /// order blocks (los que se ven).
    /// </summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        if (Bars.Count == 0) yield break;
        int last = Bars.Count - 1;
        if (ShowFvg)
            foreach (var g in _fvgs.Where(g => g.Filled < 0 || ShowFilledFvg).TakeLast(MaxBoxes))
                foreach (var l in BoxLevels("fvg" + g.Dir, g.Dir == 1 ? "FVG ▲" : "FVG ▼", g.I + 1, g.Filled >= 0 ? g.Filled : -1, g.Top, g.Bottom)) yield return l;
        if (ShowOrderBlocks)
            foreach (var ob in _blocks.TakeLast(MaxBoxes))
                foreach (var l in BoxLevels("ob" + ob.Dir, (ob.Breaker ? "Breaker " : "OB ") + (ob.Dir == 1 ? "▲" : "▼"), ob.I, -1, ob.Top, ob.Bottom)) yield return l;
        if (ShowBreaks)
            foreach (var b in _breaks.TakeLast(40))
                yield return Level((b.IsChoch ? "choch" : "bos") + b.Dir, (b.IsChoch ? "CHoCH " : "BOS ") + (b.Dir == 1 ? "▲" : "▼"), b.I, b.Broken, b.Level);
        if (ShowLiquidity)
            foreach (var p in _pools.TakeLast(40))
                yield return Level(p.Type == 1 ? "eqh" : "eql", p.Type == 1 ? "EQH" : "EQL", p.I, p.Swept >= 0 ? p.Swept : -1, p.Level);
        if (ShowCisd)
            foreach (var c in _cisds.TakeLast(40))
                yield return Level("cisd" + (c.Bull ? "u" : "d"), "CISD", c.Start, c.Invalid >= 0 ? c.Invalid : Math.Min(last, c.I + 20), c.Level);
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Bars.Count == 0) return;
        int last = Bars.Count - 1;
        if (ShowFvg)
            foreach (var g in _fvgs.Where(g => g.Filled < 0 || ShowFilledFvg).TakeLast(MaxBoxes))
            {
                int end = g.Filled >= 0 ? g.Filled : -1;
                var c = g.Dir == 1 ? BullColor : BearColor;
                int alpha = g.Filled >= 0 ? 25 : g.Mitigated >= 0 ? 45 : 70;
                Box(ctx, g.I + 1, end, g.Top, g.Bottom, Vp.Alpha(c, alpha), Vp.Alpha(c, 110));
            }

        if (ShowOrderBlocks)
            foreach (var ob in _blocks.TakeLast(MaxBoxes))
            {
                var c = ob.Dir == 1 ? BullColor : BearColor;
                Box(ctx, ob.I, -1, ob.Top, ob.Bottom, Vp.Alpha(c, ob.Breaker ? 30 : 55), Vp.Alpha(c, 140));
                string text = (ob.Breaker ? "Breaker " : "OB ") + (ob.Dir == 1 ? "▲" : "▼") + $" {ob.Strength:P0}";
                LabelLeft(ctx, text, X(ob.I) + 3, Y((ob.Top + ob.Bottom) / 2), Vp.Alpha(TextColor, 200));
            }

        if (ShowSwings)
            foreach (var s in _swings)
            {
                if (!Visible(s.I)) continue;
                var c = s.Type == 1 ? (s.Label == "HH" ? BullColor : BearColor) : (s.Label == "HL" ? BullColor : BearColor);
                Label(ctx, s.Label, X(s.I, false), Y(s.Level), c, bold: true, above: s.Type == 1);
            }

        if (ShowBreaks)
            foreach (var b in _breaks)
            {
                var c = b.Dir == 1 ? BullColor : BearColor;
                HLine(ctx, b.I, b.Broken, b.Level, c, 1, b.IsChoch ? LineDashStyle.Dash : LineDashStyle.Solid);
                int xm = (X(b.I) + X(b.Broken, false)) / 2;
                Label(ctx, b.IsChoch ? "CHoCH" : "BOS", xm, Y(b.Level), c, bold: b.IsChoch, above: b.Dir == 1);
            }

        if (ShowLiquidity)
            foreach (var p in _pools)
            {
                int end = p.Swept >= 0 ? p.Swept : -1;
                HLine(ctx, p.I, end, p.Level, LiquidityColor, 1, p.Swept >= 0 ? LineDashStyle.Dot : LineDashStyle.Dash);
                string text = (p.Type == 1 ? "EQH" : "EQL") + (p.Swept >= 0 ? " ✕" : "") + (p.Count > 2 ? $" ×{p.Count}" : "");
                Label(ctx, text, X(p.End, false), Y(p.Level), LiquidityColor, false, p.Type == 1);
            }

        if (ShowCisd)
            foreach (var c in _cisds)
            {
                var col = c.Bull ? BullColor : BearColor;
                HLine(ctx, c.Start, c.Invalid >= 0 ? c.Invalid : Math.Min(last, c.I + 20), c.Level, col, 1, LineDashStyle.DashDot);
                Label(ctx, "CISD", X(c.I, false), Y(c.Level), col, false, !c.Bull);
            }
    }
}
