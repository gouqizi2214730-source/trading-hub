using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Delta acumulado (CVD) con varias formas de verlo: línea, área, velas (con máximo / mínimo del delta de cada vela),
/// histograma o escalones; reinicio por sesión / semana / mes o nunca; media móvil; y divergencias con el precio (el
/// precio hace un máximo más alto y el CVD no, o al revés) marcadas en el panel.
/// </summary>
[DisplayName("CVD (delta acumulado)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Delta acumulado como línea, área, velas, histograma o escalones; reinicio por sesión, semana o mes; media móvil y divergencias con el precio.")]
public class Cvd : Indicator
{
    public enum CvdView
    {
        [Display(Name = "Línea")] Line,
        [Display(Name = "Área")] Area,
        [Display(Name = "Velas")] Candles,
        [Display(Name = "Histograma")] Histogram,
        [Display(Name = "Escalones")] Steps,
    }

    public enum CvdReset
    {
        [Display(Name = "Nunca")] Never,
        [Display(Name = "Cada sesión")] Session,
        [Display(Name = "Cada semana")] Week,
        [Display(Name = "Cada mes")] Month,
    }

    private readonly ValueDataSeries _line = new("CVD", "CVD") { Color = Color.FromArgb(255, 86, 180, 255), Width = 2 };
    private readonly RangeDataSeries _area = new("Area", "Área") { RangeColor = Color.FromArgb(70, 86, 180, 255) };
    private readonly CandleDataSeries _candles = new("Candles", "Velas de CVD") { DrawCandleBorder = false };
    private readonly ValueDataSeries _ma = new("MA", "Media") { Color = Color.FromArgb(255, 255, 170, 60), Width = 1, ShowZeroValue = false };
    private readonly List<(int Bar, bool Bullish, decimal Value)> _divergences = new();
    private readonly Queue<decimal> _window = new();
    private decimal _windowSum;
    private CvdView _view = CvdView.Line;
    private CvdReset _reset = CvdReset.Never;
    private int _maPeriod;
    private int _divLookback = 5;
    private bool _divergences_on = true;
    private decimal _last;

    public Cvd() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _line;
        DataSeries.Add(_area);
        DataSeries.Add(_candles);
        DataSeries.Add(_ma);
        LineSeries.Add(new LineSeries("Zero", "Cero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Solid });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Vista", GroupName = "Visualización", Order = 10)]
    public CvdView View { get => _view; set { _view = value; RecalculateValues(); } }

    [Display(Name = "Reiniciar", GroupName = "Cálculo", Order = 20)]
    public CvdReset Reset { get => _reset; set { _reset = value; RecalculateValues(); } }

    [Display(Name = "Media móvil (velas, 0 = sin)", GroupName = "Cálculo", Order = 30)]
    [Range(0, 500)]
    public int MaPeriod { get => _maPeriod; set { _maPeriod = Math.Clamp(value, 0, 500); RecalculateValues(); } }

    [Display(Name = "Marcar divergencias con el precio", GroupName = "Divergencias", Order = 40)]
    public bool Divergences { get => _divergences_on; set { _divergences_on = value; RecalculateValues(); } }

    [Display(Name = "Velas a cada lado del pivote", GroupName = "Divergencias", Order = 50)]
    [Range(2, 30)]
    public int DivergenceLookback { get => _divLookback; set { _divLookback = Math.Clamp(value, 2, 30); RecalculateValues(); } }

    [Display(Name = "Color positivo", GroupName = "Visualización", Order = 60)]
    public Color PositiveColor { get; set; } = Color.FromArgb(255, 73, 209, 73);

    [Display(Name = "Color negativo", GroupName = "Visualización", Order = 70)]
    public Color NegativeColor { get; set; } = Color.FromArgb(255, 232, 69, 72);

    [Display(Name = "Color de la línea", GroupName = "Visualización", Order = 80)]
    public Color LineColor { get => _line.Color; set { _line.Color = value; _area.RangeColor = Color.FromArgb(70, value.R, value.G, value.B); } }

    protected override void OnRecalculate()
    {
        _last = 0;
        _window.Clear();
        _windowSum = 0;
        _divergences.Clear();
    }

    private bool ResetAt(int bar) => bar > 0 && _reset switch
    {
        CvdReset.Session => IsNewSession(bar),
        CvdReset.Week => IsNewWeek(bar),
        CvdReset.Month => IsNewMonth(bar),
        _ => false,
    };

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        decimal open = bar == 0 ? 0 : _candles[bar - 1].Close;
        if (ResetAt(bar)) open = 0;
        decimal close = open + c.Delta;
        _candles[bar] = new Candle(open, open + c.MaxDelta, open + c.MinDelta, close);
        _line[bar] = close;
        _area[bar] = _view == CvdView.Area ? new RangeValue(Math.Max(close, 0), Math.Min(close, 0)) : new RangeValue();

        // la serie principal cambia de forma según la vista
        _line.VisualType = _view switch
        {
            CvdView.Histogram => VisualMode.Histogram,
            CvdView.Steps => VisualMode.Steps,
            CvdView.Candles => VisualMode.OnlyValueOnAxis,
            _ => VisualMode.Line,
        };
        _candles.Visible = _view == CvdView.Candles;
        _candles.UpCandleColor = PositiveColor;
        _candles.DownCandleColor = NegativeColor;
        if (_view == CvdView.Histogram) _line.Colors[bar] = close >= 0 ? PositiveColor : NegativeColor;
        else _line.Colors[bar] = _line.Color;

        // media móvil simple del CVD (solo con velas cerradas en la ventana)
        if (_maPeriod > 0)
        {
            if (bar < CurrentBar - 1)
            {
                _window.Enqueue(close);
                _windowSum += close;
                while (_window.Count > _maPeriod) _windowSum -= _window.Dequeue();
                _ma[bar] = _window.Count >= _maPeriod ? _windowSum / _window.Count : 0;
            }
            else
            {
                // la vela en curso: las últimas (período − 1) cerradas + esta
                decimal sum = _windowSum + close - (_window.Count >= _maPeriod ? _window.Peek() : 0);
                int count = Math.Min(_maPeriod, _window.Count + 1);
                _ma[bar] = count >= _maPeriod ? sum / count : 0;
            }
        }
        else _ma[bar] = 0;

        _last = close;
        if (_divergences_on && bar >= 2 * _divLookback && bar < CurrentBar - 1) CheckDivergence(bar);
    }

    /// <summary>Pivote confirmado en bar − lookback: precio con máximo más alto que el pivote anterior y CVD más bajo (bajista), o al revés.</summary>
    private void CheckDivergence(int bar)
    {
        int n = _divLookback;
        int p = bar - n;
        var pc = GetCandle(p);
        bool high = true, low = true;
        for (int i = p - n; i <= p + n; i++)
        {
            if (i == p) continue;
            var o = GetCandle(i);
            if (o.High >= pc.High) high = false;
            if (o.Low <= pc.Low) low = false;
        }

        if (!high && !low) return;
        // pivote anterior del mismo tipo, hasta 60 velas atrás
        for (int q = p - n - 1; q >= Math.Max(2 * n, p - 60); q--)
        {
            var qc = GetCandle(q);
            bool pivot = true;
            for (int i = q - n; i <= q + n && pivot; i++)
            {
                if (i == q) continue;
                var o = GetCandle(i);
                if (high && o.High >= qc.High) pivot = false;
                if (low && o.Low <= qc.Low) pivot = false;
            }

            if (!pivot) continue;
            decimal cvdP = _candles[p].Close, cvdQ = _candles[q].Close;
            if (high && pc.High > qc.High && cvdP < cvdQ) _divergences.Add((p, false, Math.Max(_candles[p].High, _candles[p].Close)));
            else if (low && pc.Low < qc.Low && cvdP > cvdQ) _divergences.Add((p, true, Math.Min(_candles[p].Low, _candles[p].Close)));
            return;
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (!_divergences_on || Container == null || ChartInfo == null) return;
        var container = Container;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        var font = new RenderFont("Arial", 9);
        foreach (var (bar, bullish, value) in _divergences)
        {
            if (bar < first || bar > last) continue;
            int x = ChartInfo.GetXByBar(bar, false);
            int y = container.GetYByValue(value);
            var color = bullish ? PositiveColor : NegativeColor;
            // triángulo apuntando al pivote y una etiqueta corta
            var pts = bullish
                ? new[] { new Point(x, y + 4), new Point(x - 5, y + 12), new Point(x + 5, y + 12) }
                : new[] { new Point(x, y - 4), new Point(x - 5, y - 12), new Point(x + 5, y - 12) };
            context.FillPolygon(color, pts);
            string text = UiText.Tr(bullish ? "Div. alcista" : "Div. bajista");
            var size = context.MeasureString(text, font);
            context.DrawString(text, font, color, x - size.Width / 2, bullish ? y + 13 : y - 13 - size.Height);
        }
    }
}
