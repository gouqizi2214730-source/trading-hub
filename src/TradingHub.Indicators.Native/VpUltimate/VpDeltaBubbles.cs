using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators.Native.VpUltimate;

/// <summary>
/// Módulo 6 del «VP definitivo»: burbujas de delta (o volumen) anormal y detección de absorción. El delta de cada vela es
/// el real (comprador − vendedor de su footprint). Solo velas cerradas: lo que llega al umbral y vuelve antes del cierre
/// no aparece.
/// </summary>
[DisplayName("VP definitivo · Burbujas de delta")]
[Category(Vp.Category)]
[Description("Burbuja en las velas con |delta| anormal (Z robusto sobre el logaritmo, o un mínimo fijo), en el precio medio de la vela. Amarilla = absorción: mucho flujo agresivo que no mueve el precio.")]
public class VpDeltaBubbles : VpIndicator
{
    public enum Metric
    {
        [Display(Name = "Delta de volumen")] Delta,
        [Display(Name = "Volumen (signo de la vela)")] Volume,
    }

    public enum Trigger
    {
        [Display(Name = "Adaptativo (Z robusto)")] Adaptive,
        [Display(Name = "Umbral fijo")] Fixed,
    }

    private sealed record Bubble(int Bar, decimal Price, decimal Value, double Z, bool Absorption, double ImpactRank);

    private readonly List<Bubble> _bubbles = new();
    private readonly List<double> _abs = new();       // |métrica| de las últimas velas (para el Z)
    private readonly List<double> _flow = new();      // |métrica| de la ventana de absorción
    private readonly List<double> _impacts = new();   // impacto precio / flujo de las últimas 100 velas
    private readonly List<decimal> _bodies = new();   // cuerpos de las últimas 20 velas
    private readonly List<decimal> _closes = new();
    private Metric _metric = Metric.Delta;
    private Trigger _trigger = Trigger.Adaptive;
    private int _lookback = 60, _absWindow = 10;
    private decimal _z = 2, _minVolume = 200, _impactPct = 30, _bodyRatio = 1;
    private bool _absorption = true;
    private DateTime _lastAlerted = DateTime.MinValue;

    public VpDeltaBubbles()
    {
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    [Display(Name = "Medida", GroupName = "Cálculo", Order = 10)]
    public Metric Source { get => _metric; set { _metric = value; RecalculateValues(); } }

    [Display(Name = "Velas de referencia", GroupName = "Cálculo", Order = 20)]
    [Range(20, 1000)]
    public int Lookback { get => _lookback; set { _lookback = Math.Clamp(value, 20, 1000); RecalculateValues(); } }

    [Display(Name = "Disparo", GroupName = "Cálculo", Order = 30)]
    public Trigger Mode { get => _trigger; set { _trigger = value; RecalculateValues(); } }

    [Display(Name = "Umbral de Z", GroupName = "Cálculo", Order = 40)]
    public decimal ZThreshold { get => _z; set { _z = Math.Max(0.5m, value); RecalculateValues(); } }

    [Display(Name = "Umbral fijo (volumen mínimo)", GroupName = "Cálculo", Order = 50)]
    public decimal MinVolume { get => _minVolume; set { _minVolume = Math.Max(0, value); RecalculateValues(); } }

    [Display(Name = "Detectar absorción", GroupName = "Absorción", Order = 60)]
    public bool Absorption { get => _absorption; set { _absorption = value; RecalculateValues(); } }

    [Display(Name = "Ventana (velas)", GroupName = "Absorción", Order = 70)]
    [Range(3, 50)]
    public int AbsorptionWindow { get => _absWindow; set { _absWindow = Math.Clamp(value, 3, 50); RecalculateValues(); } }

    [Display(Name = "Impacto: percentil máximo", GroupName = "Absorción", Order = 80)]
    [Range(5, 60)]
    public decimal ImpactPercentile { get => _impactPct; set { _impactPct = Math.Clamp(value, 5, 60); RecalculateValues(); } }

    [Display(Name = "Cuerpo máximo (× mediana)", GroupName = "Absorción", Order = 90)]
    [Range(0.2, 2)]
    public decimal BodyRatio { get => _bodyRatio; set { _bodyRatio = value; RecalculateValues(); } }

    [Display(Name = "Mostrar compradoras", GroupName = "Visualización", Order = 100)]
    public bool ShowBullish { get; set; } = true;

    [Display(Name = "Mostrar vendedoras", GroupName = "Visualización", Order = 110)]
    public bool ShowBearish { get; set; } = true;

    [Display(Name = "Tamaño según la fuerza", GroupName = "Visualización", Order = 120)]
    public bool ScaleBySize { get; set; } = true;

    [Display(Name = "Opacidad según la fuerza", GroupName = "Visualización", Order = 130)]
    public bool AdaptiveColor { get; set; } = true;

    [Display(Name = "Opacidad fija (%)", GroupName = "Visualización", Order = 140)]
    [Range(1, 100)]
    public int FixedOpacity { get; set; } = 85;

    [Display(Name = "Color comprador", GroupName = "Colores", Order = 150)]
    public Color BullColor { get; set; } = Color.FromArgb(255, 0, 150, 100);

    [Display(Name = "Color vendedor", GroupName = "Colores", Order = 160)]
    public Color BearColor { get; set; } = Color.FromArgb(255, 182, 0, 61);

    [Display(Name = "Color de absorción", GroupName = "Colores", Order = 170)]
    public Color AbsorptionColor { get; set; } = Color.FromArgb(255, 255, 235, 59);

    [Display(Name = "Color del texto", GroupName = "Colores", Order = 180)]
    public Color TextColor { get; set; } = Color.White;

    [Display(Name = "Alerta: umbral alcanzado", GroupName = "Alertas", Order = 190)]
    public bool AlertTrigger { get; set; }

    [Display(Name = "Alerta: absorción", GroupName = "Alertas", Order = 200)]
    public bool AlertAbsorption { get; set; }

    protected override void Reset()
    {
        _bubbles.Clear();
        _abs.Clear();
        _flow.Clear();
        _impacts.Clear();
        _bodies.Clear();
        _closes.Clear();
    }

    private static void Push<T>(List<T> list, T v, int max)
    {
        list.Add(v);
        if (list.Count > max) list.RemoveAt(0);
    }

    protected override void Commit(int bar)
    {
        var c = GetCandle(bar);
        decimal raw = _metric == Metric.Delta ? c.Delta : c.Close >= c.Open ? c.Volume : -c.Volume;
        double abs = (double)Math.Abs(raw);
        Push(_abs, abs, _lookback);
        double z = Vp.RobustLogZ(_abs, abs);
        bool triggered = _trigger == Trigger.Adaptive ? z >= (double)_z : Math.Abs(raw) >= _minVolume;

        // impacto precio / flujo (Kyle λ / Amihud): cuánto se movió el precio por unidad de flujo agresivo en la ventana
        Push(_flow, abs, _absWindow);
        Push(_closes, c.Close, _absWindow + 1);
        double flowSum = _flow.Sum();
        double? impact = _closes.Count > _absWindow && flowSum > 0 ? (double)Math.Abs(c.Close - _closes[0]) / flowSum : null;
        double rank = double.NaN;
        if (impact is { } im)
        {
            // percentil como ta.percentrank: % de las 100 anteriores que quedan por debajo o igual
            if (_impacts.Count >= 100) rank = _impacts.Count(x => x <= im) * 100.0 / _impacts.Count;
            Push(_impacts, im, 100);
        }

        decimal body = Math.Abs(c.Close - c.Open);
        Push(_bodies, body, 20);
        decimal medBody = Vp.Median(_bodies);
        bool absorption = _absorption && triggered && !double.IsNaN(rank) && rank < (double)_impactPct && body < medBody * _bodyRatio;
        if (!triggered) return;

        _bubbles.Add(new Bubble(bar, Vp.CandleVwap(c), raw, z, absorption, rank));
        if (_bubbles.Count > 5000) _bubbles.RemoveAt(0);
        // alertas solo de la vela que acaba de cerrar (no al recalcular la historia) y una vez por vela
        if (bar == CurrentBar - 2 && c.Time > _lastAlerted && DateTime.UtcNow - Vp.Utc(c.LastTime) < TimeSpan.FromMinutes(2) + BarLength)
        {
            _lastAlerted = c.Time;
            if (absorption && AlertAbsorption)
                AddAlert("Alert1", string.Format(UiText.Tr("Absorción en {0}: {1}"), InstrumentInfo?.Instrument, Vp.Short(raw)));
            else if (AlertTrigger)
                AddAlert("Alert1", string.Format(UiText.Tr("Flujo anormal en {0}: Z {1}"), InstrumentInfo?.Instrument, z.ToString("0.##")));
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        int barW = Math.Max(2, ChartInfo.PriceChartContainer.BarsWidth);
        foreach (var b in _bubbles)
        {
            if (b.Bar < first || b.Bar > last) continue;
            bool bull = b.Value > 0;
            if ((bull && !ShowBullish) || (!bull && !ShowBearish)) continue;
            var baseColor = b.Absorption ? AbsorptionColor : bull ? BullColor : BearColor;
            // transparencia como el original: más fuerte = más opaca
            double transp = AdaptiveColor ? Math.Max(10, 100 - b.Z * 15) : 100 - FixedOpacity;
            var fill = Vp.Alpha(baseColor, (int)Math.Round(255 * (100 - transp) / 100));
            double scale = !ScaleBySize ? 1 : b.Z > (double)_z + 2 ? 1.8 : b.Z > (double)_z + 1 ? 1.35 : 1;
            string text = (bull ? "+" : "-") + Vp.Short(b.Value);
            var size = context.MeasureString(text, SmallFont);
            int d = (int)Math.Max(Math.Max(size.Width + 6, 14) * scale, barW);
            int x = X(b.Bar) + barW / 2, y = Y(b.Price);
            context.FillEllipse(fill, new Rectangle(x - d / 2, y - d / 2, d, d));
            if (d >= size.Width + 2) context.DrawString(text, SmallFont, TextColor, x - size.Width / 2, y - size.Height / 2);
        }
    }
}
