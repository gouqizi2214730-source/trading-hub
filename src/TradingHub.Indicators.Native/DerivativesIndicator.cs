using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Feeds;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Derivados de cripto en un panel: tasa de financiación (funding), interés abierto (y su cambio) y liquidaciones, de
/// Binance y Bybit por separado y sumados / promediados — el «Agg Funding Rate · Agg OI · Agg Liquidations» de Kiyotaka y
/// openmarket, para leer el flujo de órdenes junto con el CVD y el libro. Datos públicos, sin cuenta; las liquidaciones
/// llegan en vivo y se graban (hay desde que el gráfico estuvo abierto).
/// </summary>
[DisplayName("Derivados: financiación, interés abierto y liquidaciones")]
[Category(IndicatorCategories.Derivatives)]
[Description("Funding, open interest (nivel o cambio) y liquidaciones de Binance y Bybit, por exchange y agregados. Para cripto (BTCUSDT, ETHUSDT…).")]
public class DerivativesIndicator : Indicator
{
    public enum DerivView
    {
        [Display(Name = "Tasa de financiación (%)")] Funding,
        [Display(Name = "Interés abierto")] OpenInterest,
        [Display(Name = "Cambio del interés abierto (vela a vela)")] OpenInterestChange,
        [Display(Name = "Liquidaciones (largos − / cortos +)")] Liquidations,
    }

    private readonly ValueDataSeries _agg = new("AGG", "Agregado") { Color = Color.FromArgb(255, 230, 230, 230), Width = 2, ShowZeroValue = false };
    private readonly ValueDataSeries _bin = new("BIN", "Binance") { Color = Color.FromArgb(255, 242, 181, 58), Width = 1, ShowZeroValue = false };
    private readonly ValueDataSeries _byb = new("BYB", "Bybit") { Color = Color.FromArgb(255, 86, 180, 255), Width = 1, ShowZeroValue = false };
    private readonly ValueDataSeries _liqLong = new("LIQL", "Largos liquidados") { Color = Color.FromArgb(220, 240, 82, 90), VisualType = VisualMode.Histogram, ShowZeroValue = false };
    private readonly ValueDataSeries _liqShort = new("LIQS", "Cortos liquidados") { Color = Color.FromArgb(220, 38, 194, 129), VisualType = VisualMode.Histogram, ShowZeroValue = false };
    private readonly Dictionary<string, List<DerivPoint>> _data = new();
    private readonly Action _tick;
    private DerivView _view = DerivView.Funding;
    private bool _binance = true, _bybit = true;
    private string _loadedKey = "";
    private bool _loading;
    private string? _error;
    private IDisposable? _liqWatch;
    private string? _liqSymbol;
    private long _liqGen;
    private List<Liquidation> _liqs = new();

    public DerivativesIndicator() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _agg;
        DataSeries.Add(_bin);
        DataSeries.Add(_byb);
        DataSeries.Add(_liqLong);
        DataSeries.Add(_liqShort);
        LineSeries.Add(new LineSeries("Zero", "Cero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Solid });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _tick = OnTimer;
    }

    [Display(Name = "Dato", GroupName = "Cálculo", Order = 10)]
    public DerivView View { get => _view; set { _view = value; RecalculateValues(); } }

    [Display(Name = "Binance (futuros USDT)", GroupName = "Exchanges", Order = 20)]
    public bool UseBinance { get => _binance; set { _binance = value; RecalculateValues(); } }

    [Display(Name = "Bybit (lineales USDT)", GroupName = "Exchanges", Order = 21)]
    public bool UseBybit { get => _bybit; set { _bybit = value; RecalculateValues(); } }

    [Display(Name = "Mostrar cada exchange (además del agregado)", GroupName = "Visualización", Order = 30)]
    public bool ShowEach { get; set; } = true;

    [Display(Name = "Símbolo de futuros (vacío = el del gráfico)", GroupName = "Cálculo", Order = 40)]
    public string SymbolOverride { get; set; } = "";

    private string Symbol
    {
        get
        {
            var s = string.IsNullOrWhiteSpace(SymbolOverride) ? InstrumentInfo?.Instrument ?? "" : SymbolOverride.Trim();
            return s.ToUpperInvariant().Replace("-", "").Replace("/", "").Replace(".P", "");
        }
    }

    protected override void OnInitialize() => SubscribeToTimer(TimeSpan.FromSeconds(2), _tick);

    protected override void OnDispose()
    {
        UnsubscribeFromTimer(TimeSpan.FromSeconds(2), _tick);
        _liqWatch?.Dispose();
        _liqWatch = null;
    }

    private void OnTimer()
    {
        if (_view != DerivView.Liquidations || _liqSymbol == null) return;
        long g = Derivatives.LiquidationGeneration(_liqSymbol);
        if (g != _liqGen) RecalculateValues();
    }

    private TimeSpan Step
    {
        get
        {
            var b = ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.FromHours(1);
            return b <= TimeSpan.Zero ? TimeSpan.FromHours(1) : b < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : b > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : b;
        }
    }

    protected override void OnRecalculate()
    {
        bool liq = _view == DerivView.Liquidations;
        _liqLong.VisualType = liq ? VisualMode.Histogram : VisualMode.Hide;
        _liqShort.VisualType = liq ? VisualMode.Histogram : VisualMode.Hide;
        _agg.VisualType = liq ? VisualMode.Hide : _view == DerivView.OpenInterestChange ? VisualMode.Histogram : VisualMode.Line;
        _bin.VisualType = !liq && ShowEach && _binance ? VisualMode.Line : VisualMode.Hide;
        _byb.VisualType = !liq && ShowEach && _bybit ? VisualMode.Line : VisualMode.Hide;
        if (CurrentBar <= 0 || Symbol.Length == 0) return;
        string sym = Symbol;

        if (liq)
        {
            if (_liqSymbol != sym)
            {
                _liqWatch?.Dispose();
                _liqWatch = Derivatives.WatchLiquidations(sym);
                _liqSymbol = sym;
            }

            _liqGen = Derivatives.LiquidationGeneration(sym);
            _liqs = Derivatives.Liquidations(sym, GetCandle(0).Time, DateTime.UtcNow.AddMinutes(1));
            return;
        }

        // los datos se piden en segundo plano; al llegar se vuelve a calcular
        var from = GetCandle(0).Time;
        var to = DateTime.UtcNow;
        string key = string.Join("|", sym, _view == DerivView.Funding ? "f" : "oi", (int)Step.TotalMinutes, _binance, _bybit, from.Date.Ticks);
        if (key == _loadedKey || _loading) return;
        _loading = true;
        _error = null;
        var step = Step;
        bool funding = _view == DerivView.Funding, bin = _binance, byb = _bybit;
        _ = Task.Run(async () =>
        {
            var got = new Dictionary<string, List<DerivPoint>>();
            string? err = null;
            foreach (var (ex, on) in new[] { (Derivatives.Binance, bin), (Derivatives.Bybit, byb) })
            {
                if (!on) continue;
                try
                {
                    got[ex] = funding
                        ? await Derivatives.FundingAsync(ex, sym, from, to, CancellationToken.None).ConfigureAwait(false)
                        : await Derivatives.OpenInterestAsync(ex, sym, step, from, to, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    err = ex + ": " + e.Message;
                }
            }

            DoActionInGuiThread(() =>
            {
                _loading = false;
                _error = got.Count == 0 ? err ?? UiText.Tr("sin datos") : null;
                _data.Clear();
                foreach (var kv in got) _data[kv.Key] = kv.Value;
                _loadedKey = key;
                RecalculateValues();
            });
        });
    }

    /// <summary>Último valor con tiempo &lt; <paramref name="before"/> (búsqueda binaria).</summary>
    private static double? At(List<DerivPoint> list, DateTime before)
    {
        int lo = 0, hi = list.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid].Time < before)
            {
                ans = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        return ans >= 0 ? list[ans].Value : null;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        var end = bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : c.Time + Step;
        if (_view == DerivView.Liquidations)
        {
            double longs = 0, shorts = 0;
            foreach (var l in _liqs)
            {
                if (l.Time < c.Time || l.Time >= end) continue;
                if (l.LongLiquidated) longs += l.Price * l.Quantity;
                else shorts += l.Price * l.Quantity;
            }

            _liqLong[bar] = (decimal)-longs;
            _liqShort[bar] = (decimal)shorts;
            return;
        }

        double? b = _data.TryGetValue(Derivatives.Binance, out var bl) ? At(bl, end) : null;
        double? y = _data.TryGetValue(Derivatives.Bybit, out var yl) ? At(yl, end) : null;
        double? agg = _view == DerivView.Funding
            ? (b, y) switch { ({ } x1, { } x2) => (x1 + x2) / 2, ({ } x1, null) => x1, (null, { } x2) => x2, _ => null }
            : b != null || y != null ? (b ?? 0) + (y ?? 0) : null;
        if (_view == DerivView.OpenInterestChange)
        {
            var prevEnd = c.Time;
            double? pb = bl != null ? At(bl, prevEnd) : null, py = yl != null ? At(yl, prevEnd) : null;
            double? prev = pb != null || py != null ? (pb ?? 0) + (py ?? 0) : null;
            agg = agg != null && prev != null ? agg - prev : null;
            b = b != null && pb != null ? b - pb : null;
            y = y != null && py != null ? y - py : null;
        }

        _agg[bar] = agg is { } a ? (decimal)a : 0;
        _bin[bar] = b is { } vb ? (decimal)vb : 0;
        _byb[bar] = y is { } vy ? (decimal)vy : 0;
        if (agg is { } v) _agg.Colors[bar] = v >= 0 ? Color.FromArgb(220, 38, 194, 129) : Color.FromArgb(220, 240, 82, 90);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var region = Container?.Region;
        if (region == null) return;
        string? msg = _loading ? UiText.Tr("Trayendo datos de derivados…") : _error != null ? UiText.Tr("Derivados: ") + _error : null;
        if (_view == DerivView.Liquidations && _liqs.Count == 0)
            msg = UiText.Tr("Liquidaciones: escuchando en vivo (Binance + Bybit); se van grabando para verlas después.");
        if (msg != null) context.DrawString(msg, new RenderFont("Arial", 8), Color.FromArgb(200, 180, 186, 196), region.Value.Left + 8, region.Value.Top + 18);
    }
}
