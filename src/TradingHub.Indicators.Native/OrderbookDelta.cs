using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Data;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Delta del libro de órdenes (liquidez en reposo): cuánto hay apoyado en el bid y en el ask cerca del precio en cada
/// vela, su diferencia y su desequilibrio (como en openmarket / chartbeyond). Sale de las fotos del libro que guarda
/// el mapa de calor de liquidez: hay datos desde que el gráfico estuvo abierto (en vivo), no en la historia bajada.
/// </summary>
[DisplayName("Profundidad y delta del libro (0 – X %)")]
[Category(IndicatorCategories.OrderBook)]
[Description("Liquidez en reposo cerca del precio por vela: bid, ask, su diferencia (delta del libro) y el desequilibrio en %. Usa las fotos del libro del mapa de calor (solo en vivo).")]
public class OrderbookDelta : Indicator
{
    public enum BookView
    {
        [Display(Name = "Delta del libro (bid − ask)")] Delta,
        [Display(Name = "Desequilibrio (%)")] Imbalance,
        [Display(Name = "Bid y ask por separado")] BidAsk,
        [Display(Name = "Cambio del libro (vela a vela)")] Change,
    }

    /// <summary>Hasta dónde se suma el libro: ticks fijos o un porcentaje del precio (como «Order Book Depth 0–2.5 %»).</summary>
    public enum DepthRange
    {
        [Display(Name = "Ticks a elección")] Ticks,
        [Display(Name = "0 – 0,5 %")] Pct0_5,
        [Display(Name = "0 – 1 %")] Pct1,
        [Display(Name = "0 – 2,5 %")] Pct2_5,
        [Display(Name = "0 – 5 %")] Pct5,
        [Display(Name = "0 – 10 %")] Pct10,
        [Display(Name = "0 – 25 %")] Pct25,
    }

    public enum BookAggregation
    {
        [Display(Name = "Promedio de la vela")] Average,
        [Display(Name = "Última foto")] Last,
        [Display(Name = "Máximo")] Max,
    }

    private readonly ValueDataSeries _main = new("Delta", "Delta del libro") { Color = Color.FromArgb(255, 86, 180, 255), Width = 2, VisualType = VisualMode.Histogram };
    private readonly ValueDataSeries _bid = new("Bid", "Liquidez bid") { Color = Color.FromArgb(255, 60, 200, 120), Width = 2, ShowZeroValue = false };
    private readonly ValueDataSeries _ask = new("Ask", "Liquidez ask") { Color = Color.FromArgb(255, 240, 80, 80), Width = 2, ShowZeroValue = false };
    private BookView _view = BookView.Delta;
    private BookAggregation _agg = BookAggregation.Average;
    private int _within = 20;
    private DepthRange _range = DepthRange.Ticks;
    private double _prevBid, _prevAsk;
    private readonly Action _tick;

    public OrderbookDelta() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _main;
        DataSeries.Add(_bid);
        DataSeries.Add(_ask);
        LineSeries.Add(new LineSeries("Zero", "Cero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Solid });
        _tick = () => RecalculateValues();
    }

    [Display(Name = "Vista", GroupName = "Visualización", Order = 10)]
    public BookView View { get => _view; set { _view = value; RecalculateValues(); } }

    [Display(Name = "Rango del libro", GroupName = "Cálculo", Order = 15)]
    public DepthRange Range { get => _range; set { _range = value; RecalculateValues(); } }

    [Display(Name = "Ticks a cada lado del mejor precio", GroupName = "Cálculo", Order = 20)]
    [Range(1, 500)]
    public int WithinTicks { get => _within; set { _within = Math.Clamp(value, 1, 500); RecalculateValues(); } }

    [Display(Name = "Por vela", GroupName = "Cálculo", Order = 30)]
    public BookAggregation Aggregation { get => _agg; set { _agg = value; RecalculateValues(); } }

    [Display(Name = "Color positivo", GroupName = "Visualización", Order = 40)]
    public Color PositiveColor { get; set; } = Color.FromArgb(255, 60, 200, 120);

    [Display(Name = "Color negativo", GroupName = "Visualización", Order = 41)]
    public Color NegativeColor { get; set; } = Color.FromArgb(255, 240, 80, 80);

    protected override void OnInitialize()
    {
        // el libro cambia sin que haya operaciones: cada 2 s se vuelve a mirar la última vela
        SubscribeToTimer(TimeSpan.FromSeconds(2), _tick);
    }

    protected override void OnDispose() => UnsubscribeFromTimer(TimeSpan.FromSeconds(2), _tick);

    protected override void OnRecalculate()
    {
        _prevBid = _prevAsk = 0;
        _main.VisualType = _view == BookView.Imbalance ? VisualMode.Line : VisualMode.Histogram;
        _main.Name = _view switch
        {
            BookView.Imbalance => "Desequilibrio %",
            BookView.Change => "Cambio del libro",
            _ => "Delta del libro",
        };
        bool split = _view == BookView.BidAsk;
        if (split) _main.VisualType = VisualMode.Hide;
        _bid.VisualType = split ? VisualMode.Line : VisualMode.Hide;
        _ask.VisualType = split ? VisualMode.Line : VisualMode.Hide;
    }

    /// <summary>Liquidez bid y ask de una vela según la agregación elegida (0, 0 si no hay fotos).</summary>
    public static (double Bid, double Ask, int Samples) Measure(IReadOnlyList<DepthSample> samples, int withinTicks, BookAggregation agg, double percent = 0)
    {
        if (samples.Count == 0) return (0, 0, 0);
        // con porcentaje: los ticks salen del precio de cada foto (los precios del libro están en ticks)
        int Ticks(DepthSample s) => percent > 0 ? (int)Math.Max(1, s.BestBid * percent / 100) : withinTicks;
        if (agg == BookAggregation.Last)
        {
            var (b, a) = samples[^1].Within(Ticks(samples[^1]));
            return (b, a, samples.Count);
        }

        double bid = 0, ask = 0;
        foreach (var s in samples)
        {
            var (b, a) = s.Within(Ticks(s));
            if (agg == BookAggregation.Max)
            {
                bid = Math.Max(bid, b);
                ask = Math.Max(ask, a);
            }
            else
            {
                bid += b;
                ask += a;
            }
        }

        if (agg == BookAggregation.Average)
        {
            bid /= samples.Count;
            ask /= samples.Count;
        }

        return (bid, ask, samples.Count);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        double pct = _range switch
        {
            DepthRange.Pct0_5 => 0.5,
            DepthRange.Pct1 => 1,
            DepthRange.Pct2_5 => 2.5,
            DepthRange.Pct5 => 5,
            DepthRange.Pct10 => 10,
            DepthRange.Pct25 => 25,
            _ => 0,
        };
        var (bid, ask, n) = Measure(GetDepthSamples(bar), _within, _agg, pct);
        if (n == 0)
        {
            // sin fotos: se repite lo anterior para que la línea no se corte (y no cuenta como cambio)
            bid = _prevBid;
            ask = _prevAsk;
        }

        decimal v = _view switch
        {
            BookView.Imbalance => bid + ask > 0 ? (decimal)((bid - ask) / (bid + ask) * 100) : 0,
            BookView.Change => (decimal)((bid - _prevBid) - (ask - _prevAsk)),
            _ => (decimal)(bid - ask),
        };
        _main[bar] = v;
        _main.Colors[bar] = v >= 0 ? PositiveColor : NegativeColor;
        _bid[bar] = (decimal)bid;
        _ask[bar] = (decimal)ask;
        if (n > 0 || bar == 0)
        {
            _prevBid = bid;
            _prevAsk = ask;
        }
    }
}
