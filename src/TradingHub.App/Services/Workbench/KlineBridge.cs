using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;

namespace TradingHub.App.Services.Workbench;

/// <summary>
/// Las velas que piden los módulos web (las mismas URL de Binance y Bybit que usaban en VPFM) salen de la caché de velas
/// de KVP: una sola descarga para los gráficos y para el Workbench, y lo ya bajado no se vuelve a pedir. La respuesta
/// tiene exactamente el formato del exchange, así los módulos no cambian (y se pueden actualizar desde sus originales).
/// </summary>
public static class KlineBridge
{
    private static readonly Dictionary<string, IKlineHistory> Fallback = new();

    /// <summary>Minutos de un intervalo («1m», «4h», «1d», «60», «D», «W»…); 0 si no se reconoce.</summary>
    public static int Minutes(string interval)
    {
        interval = interval.Trim();
        if (interval.Length == 0) return 0;
        switch (interval)
        {
            case "D": return 1440;
            case "W": return 10080;
            case "M": return 43200;
        }

        if (int.TryParse(interval, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain)) return plain; // Bybit: minutos
        char unit = interval[^1];
        if (!int.TryParse(interval[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) return 0;
        return unit switch
        {
            'm' => n,
            'h' => n * 60,
            'd' => n * 1440,
            'w' => n * 10080,
            'M' => n * 43200,
            _ => 0,
        };
    }

    /// <summary>El feed de velas de una conexión (o, sin conexión, el público de Binance futuros).</summary>
    public static IKlineHistory? FeedFor(AppServices svc, string conn)
    {
        if (conn.Length > 0)
        {
            var p = svc.Connections.Find(conn);
            if (p == null) return Public(conn);
            try
            {
                return svc.Connections.GetFeed(p.Id) as IKlineHistory;
            }
            catch
            {
                return null;
            }
        }

        var def = svc.Connections.Enabled.FirstOrDefault(x => x.Kind == ConnectionKind.BinanceFutures);
        if (def != null && svc.Connections.GetFeed(def.Id) is IKlineHistory k) return k;
        return Public(BinanceFeed.FuturesId);
    }

    /// <summary>Feeds públicos (sin perfil) de Binance futuros / spot y Bybit.</summary>
    private static IKlineHistory? Public(string id)
    {
        lock (Fallback)
        {
            if (Fallback.TryGetValue(id, out var f)) return f;
            IKlineHistory? created = id switch
            {
                BinanceFeed.FuturesId => new BinanceFeed(futures: true),
                BinanceFeed.SpotId => new BinanceFeed(futures: false),
                BybitFeed.LinearId => new BybitFeed(),
                _ => null,
            };
            if (created != null) Fallback[id] = created;
            return created;
        }
    }

    private static IKlineHistory? FeedFor(AppServices svc, string feedId, bool exact)
    {
        var p = svc.Connections.Enabled.FirstOrDefault(x => x.MarketDataId == feedId && x.Kind is ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot or ConnectionKind.Bybit);
        if (p != null)
        {
            try
            {
                if (svc.Connections.GetFeed(p.Id) is IKlineHistory k) return k;
            }
            catch
            {
                // el público
            }
        }

        return Public(feedId);
    }

    /// <summary>
    /// Velas de <paramref name="minutes"/> minutos entre dos instantes: se arman con las de 1, 5 o 15 minutos de la caché
    /// (la que divide al intervalo), alineadas en UTC (las diarias empiezan a las 00:00 UTC, como en los exchanges).
    /// </summary>
    public static async Task<List<Kline>> LoadAsync(IKlineHistory feed, string symbol, int minutes, DateTime from, DateTime to, CancellationToken ct, Instrument? instrument = null)
    {
        int baseMin = minutes % 15 == 0 ? 15 : minutes % 5 == 0 ? 5 : 1;
        // muchos años de velas de 15 min son demasiadas para pedir de una: en diarias o más se usa la propia del exchange
        long step = minutes * 60_000L;
        long fromMs = new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / step * step;
        long toMs = new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        string feedId = feed is IMarketDataFeed md ? md.Id : "";
        var inst = instrument ?? new Instrument { Symbol = symbol, FeedId = feedId, TickSize = 0.00000001m };
        var raw = await KlineHistory.LoadAsync(feed, inst, DateTimeOffset.FromUnixTimeMilliseconds(fromMs).UtcDateTime, DateTimeOffset.FromUnixTimeMilliseconds(toMs).UtcDateTime + TimeSpan.FromMinutes(baseMin),
            baseMin, 0, null, ct).ConfigureAwait(false);
        if (minutes == baseMin) return raw;
        var res = new List<Kline>(raw.Count / Math.Max(1, minutes / baseMin) + 1);
        Kline? cur = null;
        long curStart = 0;
        foreach (var k in raw)
        {
            long start = k.OpenTimeMs / step * step;
            if (cur is { } c && start == curStart)
            {
                cur = c with
                {
                    High = Math.Max(c.High, k.High), Low = Math.Min(c.Low, k.Low), Close = k.Close, Volume = c.Volume + k.Volume,
                    BuyVolume = c.BuyVolume + k.BuyVolume, HasBuyVolume = c.HasBuyVolume && k.HasBuyVolume,
                };
                continue;
            }

            if (cur is { } done) res.Add(done);
            cur = k with { OpenTimeMs = start };
            curStart = start;
        }

        if (cur is { } last) res.Add(last);
        return res;
    }

    private static string? Query(Uri u, string name)
    {
        foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string k = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            if (k.Equals(name, StringComparison.OrdinalIgnoreCase)) return eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
        }

        return null;
    }

    private static string N(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Si la URL es la de velas de Binance (futuros /fapi/v1/klines o spot /api/v3/klines) o de Bybit (/v5/market/kline,
    /// lineales o spot), responde desde KVP y devuelve true. Si no, false (se pide al exchange como siempre).
    /// </summary>
    public static async Task<bool> TryServeAsync(AppServices svc, Uri target, HttpContext ctx)
    {
        string host = target.Host.ToLowerInvariant(), path = target.AbsolutePath;
        bool binanceFut = host == "fapi.binance.com" && path == "/fapi/v1/klines";
        bool binanceSpot = host is "api.binance.com" or "data-api.binance.vision" && path == "/api/v3/klines";
        bool bybit = (host is "api.bybit.com" or "api.bytick.com") && path == "/v5/market/kline";
        if (!binanceFut && !binanceSpot && !bybit) return false;
        string? symbol = Query(target, "symbol"), interval = Query(target, "interval");
        if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(interval)) return false;
        int minutes = Minutes(interval);
        // meses y semanas no se alinean con la grilla UTC fija: esos los da el exchange
        if (minutes <= 0 || minutes >= 10080) return false;
        if (bybit && (Query(target, "category") ?? "linear") != "linear") return false;

        int limit = int.TryParse(Query(target, "limit"), out var l) ? l : bybit ? 200 : 500;
        limit = Math.Clamp(limit, 1, bybit ? 1000 : 1500);
        long? start = long.TryParse(Query(target, bybit ? "start" : "startTime"), out var s) ? s : null;
        long? end = long.TryParse(Query(target, bybit ? "end" : "endTime"), out var e) ? e : null;
        long stepMs = minutes * 60_000L;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long toMs = end ?? (start != null ? start.Value + stepMs * limit : now);
        long fromMs = start ?? toMs - stepMs * limit;
        // muy largo para la caché en velas chicas (años en diarias: miles de velas de 15 min por mes): igual sirve, la
        // caché es por mes
        var feed = FeedFor(svc, bybit ? BybitFeed.LinearId : binanceFut ? BinanceFeed.FuturesId : BinanceFeed.SpotId, true);
        if (feed == null) return false;
        var bars = await LoadAsync(feed, symbol.ToUpperInvariant(), minutes,
            DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, fromMs)).UtcDateTime, DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(toMs, now + stepMs)).UtcDateTime, ctx.RequestAborted).ConfigureAwait(false);
        bars = bars.Where(b => b.OpenTimeMs >= fromMs && b.OpenTimeMs <= toMs).ToList();
        // Binance: desde startTime las primeras «limit»; sin startTime, las últimas
        if (bars.Count > limit) bars = start != null && !bybit ? bars.Take(limit).ToList() : bars.Skip(bars.Count - limit).ToList();
        if (bars.Count == 0) return false; // nada en KVP: que responda el exchange

        var sb = new StringBuilder(bars.Count * 96);
        if (bybit)
        {
            // Bybit: de la más nueva a la más vieja, todo como texto
            sb.Append("{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"category\":\"linear\",\"symbol\":\"").Append(symbol.ToUpperInvariant()).Append("\",\"list\":[");
            for (int i = bars.Count - 1; i >= 0; i--)
            {
                var k = bars[i];
                if (i < bars.Count - 1) sb.Append(',');
                decimal turnover = k.Volume * (k.High + k.Low + k.Close) / 3;
                sb.Append("[\"").Append(k.OpenTimeMs).Append("\",\"").Append(N(k.Open)).Append("\",\"").Append(N(k.High)).Append("\",\"").Append(N(k.Low)).Append("\",\"")
                    .Append(N(k.Close)).Append("\",\"").Append(N(k.Volume)).Append("\",\"").Append(N(Math.Round(turnover, 4))).Append("\"]");
            }

            sb.Append("]},\"retExtInfo\":{},\"time\":").Append(now).Append('}');
        }
        else
        {
            // Binance: [apertura, o, h, l, c, volumen, cierre, volumen en USDT, operaciones, comprador base, comprador USDT, "0"]
            sb.Append('[');
            for (int i = 0; i < bars.Count; i++)
            {
                var k = bars[i];
                if (i > 0) sb.Append(',');
                decimal typical = (k.High + k.Low + k.Close) / 3;
                decimal buy = k.HasBuyVolume ? k.BuyVolume : k.Volume / 2;
                sb.Append('[').Append(k.OpenTimeMs).Append(",\"").Append(N(k.Open)).Append("\",\"").Append(N(k.High)).Append("\",\"").Append(N(k.Low)).Append("\",\"")
                    .Append(N(k.Close)).Append("\",\"").Append(N(k.Volume)).Append("\",").Append(k.OpenTimeMs + stepMs - 1).Append(",\"").Append(N(Math.Round(k.Volume * typical, 4)))
                    .Append("\",0,\"").Append(N(buy)).Append("\",\"").Append(N(Math.Round(buy * typical, 4))).Append("\",\"0\"]");
            }

            sb.Append(']');
        }

        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-KVP-Source"] = "kline-cache";
        await ctx.Response.WriteAsync(sb.ToString(), ctx.RequestAborted).ConfigureAwait(false);
        return true;
    }
}
