using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using TradingHub.Data;
using TradingHub.Feeds.Binance;
using TradingHub.Market;

namespace TradingHub.Feeds.External;

// Otros datos de afuera que usan los indicadores: el flujo diario de los ETF al contado (por una API que se conecta en
// Conexiones), las probabilidades de Polymarket (públicas) y las velas de otro símbolo de Binance (para comparar).

/// <summary>El flujo neto de un día de los ETF al contado de una moneda, en dólares (y por fondo si viene).</summary>
public sealed record EtfFlowDay(DateTime Date, double NetUsd, IReadOnlyDictionary<string, double>? Funds = null);

public interface IEtfFlowsProvider
{
    string Name { get; }

    bool Supports(string asset);

    Task<IReadOnlyList<EtfFlowDay>> DailyAsync(string asset, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>El centro de los flujos de ETF: el proveedor conectado y la caché (se renueva cada hora).</summary>
public static class EtfFlows
{
    private static IEtfFlowsProvider? _provider;
    private static readonly ConcurrentDictionary<string, (DateTime At, List<EtfFlowDay> Days)> Cache = new();
    private static readonly ConcurrentDictionary<string, bool> Loading = new();

    public static event Action? Changed;

    public static long Generation { get; private set; }

    public static string? LastError { get; private set; }

    public static IEtfFlowsProvider? Provider
    {
        get => _provider;
        set
        {
            _provider = value;
            Cache.Clear();
            Generation++;
            Changed?.Invoke();
        }
    }

    public static string? Unavailable(string asset)
    {
        if (_provider == null) return "Sin datos de ETF: conectá una API en Conexiones → «Flujos de ETF (API)».";
        if (!_provider.Supports(asset)) return $"{_provider.Name} no tiene ETF de {asset}.";
        return null;
    }

    public static IReadOnlyList<EtfFlowDay> Daily(string asset, DateTime from)
    {
        var p = _provider;
        if (p == null || !p.Supports(asset)) return Array.Empty<EtfFlowDay>();
        Cache.TryGetValue(asset, out var hit);
        bool stale = DateTime.UtcNow - hit.At > TimeSpan.FromHours(1) || (hit.Days != null && hit.Days.Count > 0 && hit.Days[0].Date > from.Date.AddDays(3));
        if (stale && Loading.TryAdd(asset, true))
            _ = Task.Run(async () =>
            {
                try
                {
                    var start = hit.Days is { Count: > 0 } && hit.Days[0].Date < from ? hit.Days[0].Date : from.Date.AddDays(-5);
                    var got = await p.DailyAsync(asset, start, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    Cache[asset] = (DateTime.UtcNow, got.OrderBy(d => d.Date).ToList());
                    LastError = null;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Cache[asset] = (DateTime.UtcNow.AddMinutes(-50), hit.Days ?? new List<EtfFlowDay>());
                }
                finally
                {
                    Loading.TryRemove(asset, out _);
                    Generation++;
                    Changed?.Invoke();
                }
            });
        return hit.Days ?? (IReadOnlyList<EtfFlowDay>)Array.Empty<EtfFlowDay>();
    }

    /// <summary>Para las pruebas: pone datos directamente.</summary>
    public static void Seed(string asset, IEnumerable<EtfFlowDay> days)
    {
        _provider ??= new SeededEtf();
        Cache[asset] = (DateTime.UtcNow.AddYears(1), days.OrderBy(d => d.Date).ToList());
        Generation++;
        Changed?.Invoke();
    }

    private sealed class SeededEtf : IEtfFlowsProvider
    {
        public string Name => "Prueba";

        public bool Supports(string asset) => true;

        public Task<IReadOnlyList<EtfFlowDay>> DailyAsync(string asset, DateTime from, DateTime to, CancellationToken ct) => Task.FromResult<IReadOnlyList<EtfFlowDay>>(Array.Empty<EtfFlowDay>());
    }
}

/// <summary>
/// Una API propia de flujos de ETF: <c>GET {base}/flows?asset=BTC&amp;from=AAAA-MM-DD&amp;to=AAAA-MM-DD</c> →
/// <c>[{"date":"2026-11-03","net":-123400000,"funds":{"IBIT":-80000000,"FBTC":…}}]</c> (dólares; también acepta
/// <c>"netMillions"</c> en millones). La clave va en el encabezado que digas (o como Bearer).
/// </summary>
public sealed class JsonEtfFlowsApi : IEtfFlowsProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _base;
    private readonly string? _header, _key;

    public JsonEtfFlowsApi(string baseUrl, string? header, string? key)
    {
        _base = baseUrl.TrimEnd('/');
        _header = string.IsNullOrWhiteSpace(header) ? null : header.Trim();
        _key = string.IsNullOrWhiteSpace(key) ? null : key;
    }

    public string Name => "API de ETF";

    public bool Supports(string asset) => asset is "BTC" or "ETH" or "SOL" or "XRP" or "LTC" or "DOGE" or "HBAR" or "ADA" or "AVAX" or "DOT" or "LINK" or "SUI";

    private static double D(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0 : 0;

    public async Task<IReadOnlyList<EtfFlowDay>> DailyAsync(string asset, DateTime from, DateTime to, CancellationToken ct)
    {
        string url = $"{_base}/flows?asset={Uri.EscapeDataString(asset)}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (_key != null)
        {
            if (_header == null || _header.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            else req.Headers.TryAddWithoutValidation(_header, _key);
        }

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var data) ? data : doc.RootElement;
        var res = new List<EtfFlowDay>();
        foreach (var o in root.EnumerateArray())
        {
            if (!o.TryGetProperty("date", out var dv) || !DateTime.TryParse(dv.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) continue;
            double net = o.TryGetProperty("net", out _) ? D(o, "net") : D(o, "netMillions") * 1e6;
            Dictionary<string, double>? funds = null;
            if (o.TryGetProperty("funds", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                funds = new Dictionary<string, double>();
                foreach (var p in f.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number) funds[p.Name] = p.Value.GetDouble();
            }

            res.Add(new EtfFlowDay(DateTime.SpecifyKind(date.Date, DateTimeKind.Utc), net, funds));
        }

        return res;
    }
}

/// <summary>
/// Las probabilidades de un mercado de Polymarket (públicas, sin cuenta): se busca por su «slug» (lo que va después de
/// /event/ o /market/ en la dirección) y se baja la historia del precio de un resultado (0 a 1).
/// </summary>
public static class Polymarket
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private static readonly ConcurrentDictionary<string, (DateTime At, PolyMarketInfo? Info, List<(DateTime T, double P)> History, string? Error)> Cache = new();
    private static readonly ConcurrentDictionary<string, bool> Loading = new();

    public sealed record PolyMarketInfo(string Question, string[] Outcomes, string[] TokenIds);

    public static event Action? Changed;

    public static long Generation { get; private set; }

    /// <summary>El slug de una dirección de Polymarket (o el texto tal cual si ya es un slug).</summary>
    public static string SlugOf(string? text)
    {
        var s = (text ?? "").Trim();
        int q = s.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) s = s[..q];
        var parts = s.TrimEnd('/').Split('/');
        return parts.Length > 0 ? parts[^1] : s;
    }

    private static string[] JsonList(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return Array.Empty<string>();
        if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray().Select(x => x.ToString()).ToArray();
        try
        {
            using var d = JsonDocument.Parse(v.GetString() ?? "[]");
            return d.RootElement.EnumerateArray().Select(x => x.ToString()).ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public static async Task<PolyMarketInfo?> MarketAsync(string slug, CancellationToken ct)
    {
        foreach (var url in new[] { $"https://gamma-api.polymarket.com/markets?slug={Uri.EscapeDataString(slug)}", $"https://gamma-api.polymarket.com/events?slug={Uri.EscapeDataString(slug)}" })
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var market = item.TryGetProperty("markets", out var ms) && ms.ValueKind == JsonValueKind.Array && ms.GetArrayLength() > 0 ? ms[0] : item;
                var tokens = JsonList(market, "clobTokenIds");
                if (tokens.Length == 0) continue;
                string question = market.TryGetProperty("question", out var qq) ? qq.GetString() ?? slug : slug;
                return new PolyMarketInfo(question, JsonList(market, "outcomes"), tokens);
            }
        }

        return null;
    }

    public static async Task<List<(DateTime T, double P)>> HistoryAsync(string tokenId, DateTime from, DateTime to, int fidelityMinutes, CancellationToken ct)
    {
        var res = new List<(DateTime, double)>();
        // por tramos (la API recorta los rangos largos con resolución fina)
        var chunk = TimeSpan.FromDays(Math.Clamp(fidelityMinutes * 10, 7, 120));
        for (var a = from; a < to; a += chunk)
        {
            var b = a + chunk < to ? a + chunk : to;
            long s = new DateTimeOffset(a).ToUnixTimeSeconds(), e = new DateTimeOffset(b).ToUnixTimeSeconds();
            var json = await Http.GetStringAsync($"https://clob.polymarket.com/prices-history?market={Uri.EscapeDataString(tokenId)}&startTs={s}&endTs={e}&fidelity={fidelityMinutes}", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("history", out var h)) continue;
            foreach (var p in h.EnumerateArray())
                res.Add((DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("t").GetInt64()).UtcDateTime, p.GetProperty("p").GetDouble()));
        }

        return res.OrderBy(p => p.Item1).GroupBy(p => p.Item1).Select(g => g.Last()).ToList();
    }

    /// <summary>La historia de un resultado (lo que haya; se renueva cada 2 minutos). Error: por qué no hay.</summary>
    public static (PolyMarketInfo? Info, IReadOnlyList<(DateTime T, double P)> History, string? Error) Odds(string slugOrUrl, int outcome, DateTime from, int fidelityMinutes)
    {
        string slug = SlugOf(slugOrUrl);
        if (slug.Length == 0) return (null, Array.Empty<(DateTime, double)>(), "Elegí un mercado de Polymarket (su dirección o «slug»).");
        string key = $"{slug}|{outcome}|{fidelityMinutes}";
        Cache.TryGetValue(key, out var hit);
        bool covers = hit.History is { Count: > 0 } && hit.History[0].T <= from.AddDays(1);
        if ((DateTime.UtcNow - hit.At > TimeSpan.FromMinutes(2) || (!covers && DateTime.UtcNow - hit.At > TimeSpan.FromSeconds(20))) && Loading.TryAdd(key, true))
            _ = Task.Run(async () =>
            {
                try
                {
                    var info = hit.Info ?? await MarketAsync(slug, CancellationToken.None).ConfigureAwait(false);
                    if (info == null)
                    {
                        Cache[key] = (DateTime.UtcNow, null, new List<(DateTime, double)>(), "No encontré ese mercado en Polymarket.");
                        return;
                    }

                    int k = Math.Clamp(outcome, 0, info.TokenIds.Length - 1);
                    var hist = await HistoryAsync(info.TokenIds[k], from, DateTime.UtcNow, fidelityMinutes, CancellationToken.None).ConfigureAwait(false);
                    Cache[key] = (DateTime.UtcNow, info, hist, hist.Count == 0 ? "Ese mercado no tiene historia en este rango." : null);
                }
                catch (Exception ex)
                {
                    Cache[key] = (DateTime.UtcNow, hit.Info, hit.History ?? new List<(DateTime, double)>(), "Polymarket: " + ex.Message);
                }
                finally
                {
                    Loading.TryRemove(key, out _);
                    Generation++;
                    Changed?.Invoke();
                }
            });
        if (hit.History == null) return (null, Array.Empty<(DateTime, double)>(), Loading.ContainsKey(key) || hit.At == default ? "Trayendo las probabilidades de Polymarket…" : null);
        return (hit.Info, hit.History, hit.Error);
    }

    /// <summary>Para las pruebas.</summary>
    public static void Seed(string slugOrUrl, int outcome, int fidelityMinutes, string question, IEnumerable<(DateTime T, double P)> history)
    {
        Cache[$"{SlugOf(slugOrUrl)}|{outcome}|{fidelityMinutes}"] = (DateTime.UtcNow.AddYears(1), new PolyMarketInfo(question, new[] { "Yes", "No" }, new[] { "a", "b" }), history.ToList(), null);
        Generation++;
        Changed?.Invoke();
    }
}

/// <summary>Velas de otro símbolo de Binance (para comparar con el del gráfico), con caché y en segundo plano.</summary>
public static class CompareKlines
{
    private static readonly ConcurrentDictionary<string, (DateTime At, List<Kline> Klines, string? Error)> Cache = new();
    private static readonly ConcurrentDictionary<string, bool> Loading = new();
    private static readonly Lazy<BinanceFeed> Futures = new(() => new BinanceFeed(true));
    private static readonly Lazy<BinanceFeed> Spot = new(() => new BinanceFeed(false));

    public static event Action? Changed;

    public static long Generation { get; private set; }

    public static (IReadOnlyList<Kline> Klines, string? Error) Get(string symbol, bool futures, DateTime from, int minutes)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (symbol.Length == 0) return (Array.Empty<Kline>(), "Elegí el símbolo para comparar.");
        minutes = minutes >= 15 ? 15 : minutes >= 5 ? 5 : 1;
        string key = $"{(futures ? "f" : "s")}|{symbol}|{minutes}";
        Cache.TryGetValue(key, out var hit);
        long fromMs = new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        bool covers = hit.Klines is { Count: > 0 } && hit.Klines[0].OpenTimeMs <= fromMs + minutes * 60_000L * 2;
        if ((!covers || DateTime.UtcNow - hit.At > TimeSpan.FromSeconds(Math.Max(20, minutes * 20))) && DateTime.UtcNow - hit.At > TimeSpan.FromSeconds(10) && Loading.TryAdd(key, true))
            _ = Task.Run(async () =>
            {
                try
                {
                    var feed = futures ? Futures.Value : Spot.Value;
                    var inst = new Instrument { Symbol = symbol, FeedId = futures ? BinanceFeed.FuturesId : BinanceFeed.SpotId, TickSize = 0.0001m, Exchange = feed.Venue };
                    // lo ya traído se conserva: solo se pide desde el principio pedido (si falta) y lo nuevo
                    var start = covers ? DateTimeOffset.FromUnixTimeMilliseconds(hit.Klines![^1].OpenTimeMs).UtcDateTime : from;
                    var got = await KlineHistory.LoadAsync(feed, inst, start, DateTime.UtcNow, minutes, 0, null, CancellationToken.None).ConfigureAwait(false);
                    var list = covers ? hit.Klines!.Where(k => k.OpenTimeMs < (got.Count > 0 ? got[0].OpenTimeMs : long.MaxValue)).Concat(got).ToList() : got;
                    Cache[key] = (DateTime.UtcNow, list, list.Count == 0 ? $"Binance no tiene {symbol}." : null);
                }
                catch (Exception ex)
                {
                    Cache[key] = (DateTime.UtcNow, hit.Klines ?? new List<Kline>(), $"{symbol}: {ex.Message}");
                }
                finally
                {
                    Loading.TryRemove(key, out _);
                    Generation++;
                    Changed?.Invoke();
                }
            });
        return (hit.Klines ?? (IReadOnlyList<Kline>)Array.Empty<Kline>(), hit.Klines == null ? "Trayendo " + symbol + "…" : hit.Error);
    }

    /// <summary>Para las pruebas.</summary>
    public static void Seed(string symbol, bool futures, int minutes, IEnumerable<Kline> klines)
    {
        minutes = minutes >= 15 ? 15 : minutes >= 5 ? 5 : 1;
        Cache[$"{(futures ? "f" : "s")}|{symbol.ToUpperInvariant()}|{minutes}"] = (DateTime.UtcNow.AddYears(1), klines.OrderBy(k => k.OpenTimeMs).ToList(), null);
        Generation++;
        Changed?.Invoke();
    }
}
