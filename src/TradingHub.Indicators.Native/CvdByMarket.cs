using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

namespace TradingHub.Indicators.Native;

/// <summary>
/// CVD de cada mercado por separado y el agregado (como el «CVD BTC» de openmarket con una línea por exchange y el
/// «聚合累积成交量差值» coloreado por signo). En un instrumento agregado (conexión «Agregado»: Binance + Bybit…) cada
/// operación sabe de qué mercado vino; en uno solo, la única línea es la de ese mercado. Las velas que vienen de historia
/// en velas (sin operaciones) suman su delta solo al agregado.
/// </summary>
[DisplayName("CVD por mercado (cada exchange + agregado)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Una línea de CVD por exchange (Binance futuros, Bybit, Binance spot…) y el CVD agregado coloreado por signo, para ver qué mercado empuja. Funciona con la conexión «Agregado».")]
public class CvdByMarket : Indicator
{
    public enum CvdReset
    {
        [Display(Name = "Nunca")] Never,
        [Display(Name = "Cada sesión")] Session,
        [Display(Name = "Cada semana")] Week,
        [Display(Name = "Cada mes")] Month,
    }

    public enum CvdGrouping
    {
        [Display(Name = "Cada mercado")] Market,
        [Display(Name = "Cada exchange (spot + perpetuo juntos)")] Exchange,
        [Display(Name = "Spot contra perpetuos")] SpotPerp,
    }

    /// <summary>Hasta cuántos mercados (los del agregado).</summary>
    public const int N = 16;

    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 242, 181, 58), Color.FromArgb(255, 86, 180, 255), Color.FromArgb(255, 180, 120, 255), Color.FromArgb(255, 38, 194, 129),
        Color.FromArgb(255, 255, 120, 160), Color.FromArgb(255, 120, 220, 220), Color.FromArgb(255, 200, 200, 90), Color.FromArgb(255, 255, 150, 90),
        Color.FromArgb(255, 150, 160, 255), Color.FromArgb(255, 110, 200, 120), Color.FromArgb(255, 230, 110, 230), Color.FromArgb(255, 170, 140, 110),
        Color.FromArgb(255, 90, 140, 200), Color.FromArgb(255, 200, 90, 90), Color.FromArgb(255, 160, 220, 90), Color.FromArgb(255, 210, 170, 220),
    };

    private readonly ValueDataSeries _agg = new("AGG", "CVD agregado") { Color = Color.FromArgb(255, 220, 220, 220), Width = 2, VisualType = VisualMode.Histogram };
    private readonly ValueDataSeries[] _lines = new ValueDataSeries[N];
    private readonly decimal[] _acc = new decimal[N];
    private int[] _lane = Array.Empty<int>();
    private CvdGrouping _grouping = CvdGrouping.Market;
    private decimal _aggAcc;
    private readonly Dictionary<long, (int Trades, decimal[] Delta, decimal Unknown)> _cache = new();
    private CvdReset _reset = CvdReset.Never;
    private bool _histogram = true;

    public CvdByMarket() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _agg;
        for (int i = 0; i < N; i++)
        {
            _lines[i] = new ValueDataSeries("M" + i, "Mercado " + (i + 1)) { Color = Palette[i], Width = 1, ShowZeroValue = false };
            DataSeries.Add(_lines[i]);
        }

        LineSeries.Add(new LineSeries("Zero", "Cero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Solid });
    }

    [Display(Name = "Reiniciar", GroupName = "Cálculo", Order = 10)]
    public CvdReset Reset { get => _reset; set { _reset = value; RecalculateValues(); } }

    [Display(Name = "Una línea por", GroupName = "Cálculo", Order = 15)]
    public CvdGrouping Grouping { get => _grouping; set { _grouping = value; RecalculateValues(); } }

    [Display(Name = "Agregado como histograma por signo (si no, línea)", GroupName = "Visualización", Order = 20)]
    public bool AggregateHistogram { get => _histogram; set { _histogram = value; RecalculateValues(); } }

    [Display(Name = "Mostrar el agregado", GroupName = "Visualización", Order = 25)]
    public bool ShowAggregate { get; set; } = true;

    [Display(Name = "Color positivo", GroupName = "Visualización", Order = 30)]
    public Color PositiveColor { get; set; } = Color.FromArgb(200, 38, 194, 129);

    [Display(Name = "Color negativo", GroupName = "Visualización", Order = 31)]
    public Color NegativeColor { get; set; } = Color.FromArgb(200, 240, 82, 90);

    protected override void OnRecalculate()
    {
        Array.Clear(_acc);
        _aggAcc = 0;
        _cache.Clear();
        var sources = InstrumentInfo?.Sources ?? Array.Empty<string>();
        var (lane, names) = Lanes(sources, InstrumentInfo?.SourceVenues ?? Array.Empty<string>(), InstrumentInfo?.SourceIsSpot ?? Array.Empty<bool>(), _grouping);
        _lane = lane;
        for (int i = 0; i < N; i++)
        {
            bool used = i < names.Count && sources.Length > 1;
            _lines[i].Name = i < names.Count ? "CVD " + names[i] : "Mercado " + (i + 1);
            _lines[i].VisualType = used ? VisualMode.Line : VisualMode.Hide;
        }

        _agg.VisualType = !ShowAggregate ? VisualMode.Hide : _histogram ? VisualMode.Histogram : VisualMode.Line;
    }

    /// <summary>A qué línea va cada mercado y el nombre de cada línea, según la forma de agrupar.</summary>
    public static (int[] Lane, List<string> Names) Lanes(string[] sources, string[] venues, bool[] spot, CvdGrouping grouping)
    {
        var lane = new int[sources.Length];
        var names = new List<string>();
        for (int i = 0; i < sources.Length; i++)
        {
            string name = grouping switch
            {
                CvdGrouping.Exchange => i < venues.Length ? venues[i] : sources[i],
                CvdGrouping.SpotPerp => i < spot.Length && spot[i] ? "spot" : "perpetuos",
                _ => sources[i],
            };
            int k = grouping == CvdGrouping.Market ? -1 : names.IndexOf(name);
            if (k < 0)
            {
                k = names.Count;
                names.Add(name);
            }

            lane[i] = Math.Min(k, N - 1);
        }

        return (lane, names);
    }

    private bool NewPeriod(int bar) => _reset switch
    {
        CvdReset.Session => IsNewSession(bar),
        CvdReset.Week => IsNewWeek(bar),
        CvdReset.Month => IsNewMonth(bar),
        _ => false,
    };

    /// <summary>Delta de cada mercado en una vela (y lo que no se sabe de dónde vino), recordado para las velas cerradas.</summary>
    private (decimal[] Delta, decimal Unknown) Split(int bar)
    {
        var c = GetCandle(bar);
        long key = c.Time.Ticks;
        int trades = (int)c.Ticks;
        if (bar < CurrentBar - 1 && _cache.TryGetValue(key, out var hit) && hit.Trades == trades) return (hit.Delta, hit.Unknown);
        var d = new decimal[N];
        decimal known = 0;
        foreach (var t in GetTrades(bar))
        {
            if (t.Source < 0 || t.Source >= N) continue;
            decimal v = t.IsBuy ? t.Volume : -t.Volume;
            d[t.Source < _lane.Length ? _lane[t.Source] : t.Source] += v;
            known += v;
        }

        decimal unknown = c.Delta - known;
        if (bar < CurrentBar - 1) _cache[key] = (trades, d, unknown);
        return (d, unknown);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar > 0 && NewPeriod(bar))
        {
            Array.Clear(_acc);
            _aggAcc = 0;
        }

        var c = GetCandle(bar);
        bool multi = (InstrumentInfo?.Sources.Length ?? 0) > 1;
        decimal[] acc = (decimal[])_acc.Clone();
        decimal agg = _aggAcc + c.Delta;
        if (multi)
        {
            var (d, _) = Split(bar);
            for (int i = 0; i < N; i++) acc[i] += d[i];
            for (int i = 0; i < N; i++) _lines[i][bar] = acc[i];
        }

        _agg[bar] = agg;
        _agg.Colors[bar] = agg >= 0 ? PositiveColor : NegativeColor;
        // la vela en formación se vuelve a calcular con cada operación: lo acumulado solo avanza con las cerradas
        if (bar < CurrentBar - 1)
        {
            Array.Copy(acc, _acc, N);
            _aggAcc = agg;
        }
    }
}
