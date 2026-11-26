using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;

namespace TradingHub.App.Services.Workbench;

public static class KlineBridge
{
    private static readonly Dictionary<string, IKlineHistory> Fallback = new();

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

        if (int.TryParse(interval, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain)) return plain;
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
            }
        }

        return Public(feedId);
    }

    public static async Task<List<Kline>> LoadAsync(IKlineHistory feed, string symbol, int minutes, DateTime from, DateTime to, CancellationToken ct, Instrument? instrument = null)
    {
        int baseMin = minutes % 15 == 0 ? 15 : minutes % 5 == 0 ? 5 : 1;
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

    public static InstrumentData? FindOpenData(AppServices svc, string feedId, string symbol)
    {
        string upper = symbol.ToUpperInvariant();
        return svc.Hub.Items.FirstOrDefault(d =>
            d.Instrument.FeedId.Equals(feedId, StringComparison.OrdinalIgnoreCase) &&
            d.Instrument.Symbol.Equals(upper, StringComparison.OrdinalIgnoreCase));
    }

    public static string? BuildDepthJson(InstrumentData data, int limit, bool bybit)
    {
        var snap = data.Depth;
        if (!snap.HasBid && !snap.HasAsk) return null;
        var inst = data.Instrument;
        int cap = Math.Clamp(limit, 1, 1000);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sb = new StringBuilder(cap * 48 + 128);
        if (bybit)
        {
            sb.Append("{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"s\":\"").Append(inst.Symbol).Append("\",\"b\":[");
            int bi = 0;
            foreach (var b in snap.Bids.Take(cap))
            {
                if (bi++ > 0) sb.Append(',');
                sb.Append("[\"").Append(inst.FormatPrice(inst.FromTicks(b.PriceTicks))).Append("\",\"").Append(N(b.Size)).Append("\"]");
            }
            sb.Append("],\"a\":[");
            int ai = 0;
            foreach (var a in snap.Asks.Take(cap))
            {
                if (ai++ > 0) sb.Append(',');
                sb.Append("[\"").Append(inst.FormatPrice(inst.FromTicks(a.PriceTicks))).Append("\",\"").Append(N(a.Size)).Append("\"]");
            }
            sb.Append("],\"ts\":").Append(now).Append(",\"u\":1},\"retExtInfo\":{},\"time\":").Append(now).Append('}');
        }
        else
        {
            sb.Append("{\"lastUpdateId\":").Append(now).Append(",\"E\":").Append(now).Append(",\"T\":").Append(now).Append(",\"bids\":[");
            int bi = 0;
            foreach (var b in snap.Bids.Take(cap))
            {
                if (bi++ > 0) sb.Append(',');
                sb.Append("[\"").Append(inst.FormatPrice(inst.FromTicks(b.PriceTicks))).Append("\",\"").Append(N(b.Size)).Append("\"]");
            }
            sb.Append("],\"asks\":[");
            int ai = 0;
            foreach (var a in snap.Asks.Take(cap))
            {
                if (ai++ > 0) sb.Append(',');
                sb.Append("[\"").Append(inst.FormatPrice(inst.FromTicks(a.PriceTicks))).Append("\",\"").Append(N(a.Size)).Append("\"]");
            }
            sb.Append("]}");
        }
        return sb.ToString();
    }

    public static async Task<bool> TryServeAsync(AppServices svc, Uri target, HttpContext ctx)
    {
        string host = target.Host.ToLowerInvariant(), path = target.AbsolutePath;
        bool isBinanceFutHost = host == "fapi.binance.com";
        bool isBinanceSpotHost = host is "api.binance.com" or "data-api.binance.vision";
        bool isBybitHost = host is "api.bybit.com" or "api.bytick.com";

        if ((isBinanceFutHost && path == "/fapi/v1/depth") ||
            (isBinanceSpotHost && path == "/api/v3/depth") ||
            (isBybitHost && path == "/v5/market/orderbook"))
        {
            string? sym = Query(target, "symbol");
            if (!string.IsNullOrEmpty(sym))
            {
                string fid = isBybitHost ? BybitFeed.LinearId : isBinanceFutHost ? BinanceFeed.FuturesId : BinanceFeed.SpotId;
                if (FindOpenData(svc, fid, sym) is { } openData)
                {
                    int lim = int.TryParse(Query(target, "limit"), out var dl) ? dl : 100;
                    if (BuildDepthJson(openData, lim, isBybitHost) is { } depthJson)
                    {
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.Headers.CacheControl = "no-store";
                        ctx.Response.Headers["X-KVP-Source"] = "memory-depth-bus";
                        await ctx.Response.WriteAsync(depthJson, ctx.RequestAborted).ConfigureAwait(false);
                        return true;
                    }
                }
            }
        }

        bool binanceFut = isBinanceFutHost && path == "/fapi/v1/klines";
        bool binanceSpot = isBinanceSpotHost && path == "/api/v3/klines";
        bool bybit = isBybitHost && path == "/v5/market/kline";
        if (!binanceFut && !binanceSpot && !bybit) return false;
        string? symbol = Query(target, "symbol"), interval = Query(target, "interval");
        if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(interval)) return false;
        int minutes = Minutes(interval);
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
        var feed = FeedFor(svc, bybit ? BybitFeed.LinearId : binanceFut ? BinanceFeed.FuturesId : BinanceFeed.SpotId, true);
        if (feed == null) return false;
        var bars = await LoadAsync(feed, symbol.ToUpperInvariant(), minutes,
            DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, fromMs)).UtcDateTime, DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(toMs, now + stepMs)).UtcDateTime, ctx.RequestAborted).ConfigureAwait(false);
        bars = bars.Where(b => b.OpenTimeMs >= fromMs && b.OpenTimeMs <= toMs).ToList();
        if (bars.Count > limit) bars = start != null && !bybit ? bars.Take(limit).ToList() : bars.Skip(bars.Count - limit).ToList();
        if (bars.Count == 0) return false;

        var sb = new StringBuilder(bars.Count * 96);
        if (bybit)
        {
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
