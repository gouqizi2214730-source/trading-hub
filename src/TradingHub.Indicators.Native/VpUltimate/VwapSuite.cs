using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using TradingHub.Indicators.Native.Structure;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators.Native.VpUltimate;

/// <summary>
/// El VWAP único de KVP: junta en un solo indicador el «VWAP con desvíos» (anclado a sesión / semana / mes con hasta tres
/// bandas), el «VWAP Claire» (rodantes de 30 / 90 / 365 días y anclados día / semana / mes) y todos los VWAP del «VP
/// definitivo» (varios períodos, período anterior, cierres, móvil y por sesión de Asia / Londres / Nueva York). Cada parte
/// se prende o apaga por separado y se configura en su propio grupo; las que están apagadas no se calculan.
/// </summary>
[DisplayName("VWAP (bandas σ · Claire · VP definitivo)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Un solo VWAP con todo: anclado al período con hasta tres bandas de desvío, rodantes de 30/90/365 días (Claire), varios períodos a la vez, el del período anterior, sus cierres, VWAP móvil y por sesión. Cada parte se activa por separado.")]
public class VwapSuite : Indicator
{
    private readonly (string Prefix, Indicator Child)[] _parts;
    private bool _anchored = true, _claire, _multi, _previous, _rolling, _closes, _sessions;

    public VwapSuite() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _parts = new (string, Indicator)[]
        {
            ("A", Anchored), ("C", Claire), ("M", MultiPeriod), ("P", Previous), ("R", Rolling), ("X", Closes), ("S", Sessions),
        };
        foreach (var (prefix, child) in _parts)
        {
            Add(child);
            // las líneas de cada parte son líneas de este indicador (se ven, se configuran y se pueden alertar desde acá)
            foreach (var s in child.DataSeries.ToList())
            {
                if (s.Id == "Main") continue; // la serie oculta de cada parte
                s.GetType().GetProperty("Id")?.SetValue(s, prefix + "." + s.Id);
                if (s is ValueDataSeries v) v.ShowZeroValue = false;
                DataSeries.Add(s);
            }
        }
    }

    // ------------------------------------------------------------------ partes (encendido)

    [Display(Name = "VWAP anclado con bandas σ (sesión, semana, mes…)", GroupName = "Partes", Order = 1)]
    public bool UseAnchored { get => _anchored; set { _anchored = value; RecalculateValues(); } }

    [Display(Name = "VWAP Claire (rodantes 30 / 90 / 365 días)", GroupName = "Partes", Order = 2)]
    public bool UseClaire { get => _claire; set { _claire = value; RecalculateValues(); } }

    [Display(Name = "VWAP de varios períodos (D / S / M / T / A)", GroupName = "Partes", Order = 3)]
    public bool UseMultiPeriod { get => _multi; set { _multi = value; RecalculateValues(); } }

    [Display(Name = "VWAP del período anterior", GroupName = "Partes", Order = 4)]
    public bool UsePrevious { get => _previous; set { _previous = value; RecalculateValues(); } }

    [Display(Name = "VWAP móvil (ventana de tiempo)", GroupName = "Partes", Order = 5)]
    public bool UseRolling { get => _rolling; set { _rolling = value; RecalculateValues(); } }

    [Display(Name = "Cierres del VWAP (líneas hacia la derecha)", GroupName = "Partes", Order = 6)]
    public bool UseCloses { get => _closes; set { _closes = value; RecalculateValues(); } }

    [Display(Name = "VWAP por sesión (Asia / Londres / Nueva York)", GroupName = "Partes", Order = 7)]
    public bool UseSessions { get => _sessions; set { _sessions = value; RecalculateValues(); } }

    // ------------------------------------------------------------------ partes (ajustes, cada una en su grupo)

    [Display(Name = "Anclado con bandas", GroupName = "Ajustes de cada parte", Order = 10)]
    public VpAnchoredVwap Anchored { get; } = new();

    [Display(Name = "Claire", GroupName = "Ajustes de cada parte", Order = 20)]
    public VwapClaire Claire { get; } = new();

    [Display(Name = "Varios períodos", GroupName = "Ajustes de cada parte", Order = 30)]
    public VpMultiVwap MultiPeriod { get; } = new();

    [Display(Name = "Período anterior", GroupName = "Ajustes de cada parte", Order = 40)]
    public VpPreviousVwap Previous { get; } = new() { Band1 = true };

    [Display(Name = "Móvil", GroupName = "Ajustes de cada parte", Order = 50)]
    public VpRollingVwap Rolling { get; } = new();

    [Display(Name = "Cierres", GroupName = "Ajustes de cada parte", Order = 60)]
    public VpVwapCloses Closes { get; } = new();

    [Display(Name = "Por sesión", GroupName = "Ajustes de cada parte", Order = 70)]
    public VpSessionVwap Sessions { get; } = new();

    private bool On(Indicator child) =>
        ReferenceEquals(child, Anchored) ? _anchored
        : ReferenceEquals(child, Claire) ? _claire
        : ReferenceEquals(child, MultiPeriod) ? _multi
        : ReferenceEquals(child, Previous) ? _previous
        : ReferenceEquals(child, Rolling) ? _rolling
        : ReferenceEquals(child, Closes) ? _closes
        : _sessions;

    protected override void OnCalculate(int bar, decimal value)
    {
        foreach (var (_, child) in _parts)
            if (On(child)) child.Calculate(bar, value);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        foreach (var (_, child) in _parts)
            if (On(child)) RenderChild(child, context, layout);
    }
}
