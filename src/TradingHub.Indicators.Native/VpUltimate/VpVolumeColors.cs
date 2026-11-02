using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

namespace TradingHub.Indicators.Native.VpUltimate;

/// <summary>
/// Módulo 1 del «VP definitivo» (coloreo): pinta las velas con volumen anormal. Alcista: mira el volumen comprador;
/// bajista: el vendedor; Z robusto sobre el logaritmo (mediana y MAD), así un volumen gigante aislado no corre la base.
/// Solo velas cerradas: el color no cambia mientras la vela se forma.
/// </summary>
[DisplayName("VP definitivo · Velas por volumen")]
[Category(Vp.Category)]
[Description("Pinta las velas con volumen comprador / vendedor anormalmente alto (o volumen total bajo), con Z robusto sobre el logaritmo. Solo velas cerradas.")]
public class VpVolumeColors : VpIndicator
{
    public enum SplitMode
    {
        [Display(Name = "Footprint (comprador / vendedor exacto)")] Footprint,
        [Display(Name = "Estimado por la forma de la vela")] Shape,
    }

    private readonly PaintbarsDataSeries _paint = new("Paint", "Velas") { IsHidden = true };
    private readonly List<double> _buy = new(), _sell = new(), _vol = new();
    private int _len = 20;
    private SplitMode _split = SplitMode.Footprint;
    private decimal _highZ = 2, _lowZ = -1;

    public VpVolumeColors()
    {
        DataSeries.Add(_paint);
    }

    [Display(Name = "Velas de referencia", GroupName = "Cálculo", Order = 10)]
    [Range(5, 500)]
    public int Lookback { get => _len; set { _len = Math.Clamp(value, 5, 500); RecalculateValues(); } }

    [Display(Name = "Comprador / vendedor", GroupName = "Cálculo", Order = 20)]
    public SplitMode Split { get => _split; set { _split = value; RecalculateValues(); } }

    [Display(Name = "Volumen alto: Z mayor que", GroupName = "Cálculo", Order = 30)]
    public decimal HighZ { get => _highZ; set { _highZ = value; RecalculateValues(); } }

    [Display(Name = "Volumen bajo: Z menor que", GroupName = "Cálculo", Order = 40)]
    public decimal LowZ { get => _lowZ; set { _lowZ = value; RecalculateValues(); } }

    [Display(Name = "Alcista con volumen alto", GroupName = "Colores", Order = 50)]
    public bool HighUp { get => _highUp; set { _highUp = value; RecalculateValues(); } }
    private bool _highUp = true;

    [Display(Name = "Color: alcista con volumen alto", GroupName = "Colores", Order = 51)]
    public Color HighUpColor { get => _highUpColor; set { _highUpColor = value; RecalculateValues(); } }
    private Color _highUpColor = Color.FromArgb(255, 0, 255, 0);

    [Display(Name = "Bajista con volumen alto", GroupName = "Colores", Order = 60)]
    public bool HighDown { get => _highDown; set { _highDown = value; RecalculateValues(); } }
    private bool _highDown = true;

    [Display(Name = "Color: bajista con volumen alto", GroupName = "Colores", Order = 61)]
    public Color HighDownColor { get => _highDownColor; set { _highDownColor = value; RecalculateValues(); } }
    private Color _highDownColor = Color.FromArgb(255, 255, 0, 0);

    [Display(Name = "Alcista con volumen bajo", GroupName = "Colores", Order = 70)]
    public bool LowUp { get => _lowUp; set { _lowUp = value; RecalculateValues(); } }
    private bool _lowUp = true;

    [Display(Name = "Color: alcista con volumen bajo", GroupName = "Colores", Order = 71)]
    public Color LowUpColor { get => _lowUpColor; set { _lowUpColor = value; RecalculateValues(); } }
    private Color _lowUpColor = Color.FromArgb(255, 175, 255, 175);

    [Display(Name = "Bajista con volumen bajo", GroupName = "Colores", Order = 80)]
    public bool LowDown { get => _lowDown; set { _lowDown = value; RecalculateValues(); } }
    private bool _lowDown = true;

    [Display(Name = "Color: bajista con volumen bajo", GroupName = "Colores", Order = 81)]
    public Color LowDownColor { get => _lowDownColor; set { _lowDownColor = value; RecalculateValues(); } }
    private Color _lowDownColor = Color.FromArgb(255, 255, 175, 175);

    protected override void Reset()
    {
        _buy.Clear();
        _sell.Clear();
        _vol.Clear();
    }

    private static void Push(List<double> list, double v, int len)
    {
        list.Add(v);
        if (list.Count > len) list.RemoveAt(0);
    }

    protected override void Commit(int bar)
    {
        var c = GetCandle(bar);
        decimal buy, sell;
        if (_split == SplitMode.Footprint) (buy, sell) = Vp.BuySell(c);
        else
        {
            decimal range = c.High - c.Low;
            buy = range > 0 ? c.Volume * (c.Close - c.Low) / range : c.Volume / 2;
            sell = range > 0 ? c.Volume * (c.High - c.Close) / range : c.Volume / 2;
        }

        Push(_buy, (double)buy, _len);
        Push(_sell, (double)sell, _len);
        Push(_vol, (double)c.Volume, _len);
        bool up = c.Close >= c.Open;
        double z = up ? Vp.RobustLogZ(_buy, (double)buy) : Vp.RobustLogZ(_sell, (double)sell);
        bool high = z > (double)_highZ;
        bool low = !high && Vp.RobustLogZ(_vol, (double)c.Volume) < (double)_lowZ;
        Color? color = high && up && HighUp ? HighUpColor
            : high && !up && HighDown ? HighDownColor
            : low && up && LowUp ? LowUpColor
            : low && !up && LowDown ? LowDownColor
            : null;
        _paint[bar] = color;
    }
}
