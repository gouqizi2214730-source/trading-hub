using TradingHub.Feeds;

namespace TradingHub.Indicators.Native.Flow;

/// <summary>
/// Los datos de derivados que usan varios indicadores (interés abierto de Binance + Bybit, financiación y liquidaciones en
/// vivo) traídos una vez por símbolo y período y consultados por vela. Lo trae en segundo plano y avisa al terminar.
/// </summary>
public sealed class DerivFeed : IDisposable
{
    private readonly Action<Action> _post;
    private readonly Action _loaded;
    private readonly bool _oi, _funding, _liqs;
    private string _key = "";
    private bool _loading;
    private List<DerivPoint>[] _oiLists = Array.Empty<List<DerivPoint>>();
    private List<DerivPoint>[] _fundLists = Array.Empty<List<DerivPoint>>();
    private IDisposable? _watch;
    private string? _watchSymbol;

    /// <param name="post">Cómo volver al hilo de la interfaz (el DoActionInGuiThread del indicador).</param>
    /// <param name="loaded">Qué hacer cuando llegan los datos (normalmente recalcular).</param>
    public DerivFeed(Action<Action> post, Action loaded, bool oi = true, bool funding = false, bool liquidations = false)
    {
        _post = post;
        _loaded = loaded;
        _oi = oi;
        _funding = funding;
        _liqs = liquidations;
    }

    public string? Error { get; private set; }

    public bool HasOi => _oiLists.Any(l => l.Count > 0);

    public bool HasFunding => _fundLists.Any(l => l.Count > 0);

    /// <summary>El símbolo de futuros perpetuos de un instrumento (BTC-USDT, BTCUSDT.P → BTCUSDT).</summary>
    public static string Normalize(string? symbol) =>
        (symbol ?? "").ToUpperInvariant().Replace("-", "").Replace("/", "").Replace(".P", "").Replace("PERP", "");

    /// <summary>Pide los datos si cambió el símbolo, el período o el inicio (no hace nada si ya están o se están trayendo).</summary>
    public void Ensure(string symbol, DateTime from, TimeSpan barLength)
    {
        symbol = Normalize(symbol);
        if (symbol.Length == 0) return;
        if (_liqs && _watchSymbol != symbol)
        {
            _watch?.Dispose();
            _watch = Derivatives.WatchLiquidations(symbol);
            _watchSymbol = symbol;
        }

        if (!_oi && !_funding) return;
        var step = barLength <= TimeSpan.Zero ? TimeSpan.FromHours(1) : barLength < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : barLength > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : barLength;
        string key = string.Join("|", symbol, (int)step.TotalMinutes, from.Date.Ticks);
        if (key == _key || _loading) return;
        _loading = true;
        _ = Task.Run(async () =>
        {
            var oi = new List<List<DerivPoint>>();
            var fund = new List<List<DerivPoint>>();
            string? err = null;
            foreach (var ex in new[] { Derivatives.Binance, Derivatives.Bybit })
            {
                try
                {
                    if (_oi) oi.Add(await Derivatives.OpenInterestAsync(ex, symbol, step, from, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false));
                    if (_funding) fund.Add(await Derivatives.FundingAsync(ex, symbol, from.AddDays(-2), DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false));
                }
                catch (Exception e)
                {
                    err = ex + ": " + e.Message;
                }
            }

            _post(() =>
            {
                _loading = false;
                _oiLists = oi.ToArray();
                _fundLists = fund.ToArray();
                Error = oi.Count == 0 && fund.Count == 0 ? err : null;
                _key = key;
                _loaded();
            });
        });
    }

    private static double? At(List<DerivPoint> list, DateTime t)
    {
        int lo = 0, hi = list.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid].Time <= t)
            {
                ans = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        return ans >= 0 ? list[ans].Value : null;
    }

    /// <summary>El interés abierto sumado de los exchanges (en monedas) en el instante <paramref name="t"/> o antes.</summary>
    public double? OiAt(DateTime t)
    {
        double sum = 0;
        bool any = false;
        foreach (var l in _oiLists)
            if (At(l, t) is { } v)
            {
                sum += v;
                any = true;
            }

        return any ? sum : null;
    }

    /// <summary>La financiación promedio (en %) vigente en el instante <paramref name="t"/>.</summary>
    public double? FundingAt(DateTime t)
    {
        double sum = 0;
        int n = 0;
        foreach (var l in _fundLists)
            if (At(l, t) is { } v)
            {
                sum += v;
                n++;
            }

        return n > 0 ? sum / n : null;
    }

    /// <summary>Lo liquidado (en monedas) entre dos instantes: largos y cortos.</summary>
    public (double Long, double Short) Liquidations(DateTime from, DateTime to)
    {
        if (_watchSymbol == null) return (0, 0);
        double l = 0, s = 0;
        foreach (var q in Derivatives.Liquidations(_watchSymbol, from, to))
        {
            if (q.LongLiquidated) l += q.Quantity;
            else s += q.Quantity;
        }

        return (l, s);
    }

    /// <summary>Cambia con cada liquidación nueva (para volver a dibujar).</summary>
    public long LiquidationGeneration => _watchSymbol == null ? 0 : Derivatives.LiquidationGeneration(_watchSymbol);

    public void Dispose()
    {
        _watch?.Dispose();
        _watch = null;
        _watchSymbol = null;
    }
}
