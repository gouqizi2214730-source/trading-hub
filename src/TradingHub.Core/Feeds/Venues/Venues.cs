using System.Text;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Venues;

/// <summary>Las cotizaciones que entran en el mercado agregado (todo en dólares).</summary>
internal static class Quotes
{
    public static bool Usd(string q) => q is "USDT" or "USD" or "USDC";
}

// ============================================================================ OKX

/// <summary>OKX: libro (400 niveles, encadenado por prevSeqId) y operaciones; en los swaps el tamaño es en contratos (ctVal).</summary>
public sealed class OkxFeed(bool spot) : PublicVenueFeed
{
    public const string SwapId = "okx-swap";
    public const string SpotId = "okx-spot";
    public override string Id => spot ? SpotId : SwapId;
    public override string DisplayName => spot ? "OKX Spot" : "OKX";
    public override string Venue => "OKX";
    public override bool IsSpot => spot;
    protected override (TimeSpan Every, string Frame)? Keepalive => (TimeSpan.FromSeconds(20), "ping");

    /// <summary>OKX atiende también en el puerto 443 (algunas redes cierran el 8443 de su documentación).</summary>
    private const string OkxWs = "wss://ws.okx.com:443/ws/v5/public";

    protected override async Task<List<(Instrument, Spec)>> ListAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://www.okx.com/api/v5/public/instruments?instType={(spot ? "SPOT" : "SWAP")}", ct).ConfigureAwait(false);
        var res = new List<(Instrument, Spec)>();
        foreach (var s in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (S(s, "state") != "live") continue;
            string id = S(s, "instId");
            string bas, quote;
            decimal mult = 1;
            if (spot)
            {
                bas = S(s, "baseCcy");
                quote = S(s, "quoteCcy");
            }
            else
            {
                if (S(s, "ctType") != "linear") continue;
                bas = S(s, "ctValCcy");
                quote = S(s, "settleCcy");
                mult = D(s, "ctVal");
                if (mult <= 0) continue;
            }

            if (!Quotes.Usd(quote)) continue;
            res.Add((new Instrument
            {
                Symbol = id,
                FeedId = Id,
                Exchange = DisplayName,
                TickSize = Norm(D(s, "tickSz")),
                VolumeStep = Norm(D(s, "lotSz") * mult),
                BaseAsset = bas,
                QuoteAsset = quote,
                Description = $"{bas}/{quote} {(spot ? "spot" : "perpetuo")}",
            }, new Spec(id, mult)));
        }

        return res;
    }

    protected override Uri WsUri(Instrument i, Spec s) => new(OkxWs);

    protected override IEnumerable<string> SubscribeFrames(Instrument i, Spec s)
    {
        yield return $"{{\"op\":\"subscribe\",\"args\":[{{\"channel\":\"books\",\"instId\":\"{s.Native}\"}},{{\"channel\":\"trades\",\"instId\":\"{s.Native}\"}}]}}";
    }

    protected override void OnMessage(string text, Session s)
    {
        if (text == "pong") return;
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        if (!m.TryGetProperty("arg", out var arg) || !m.TryGetProperty("data", out var data)) return;
        string channel = S(arg, "channel");
        if (channel == "trades")
        {
            foreach (var d in data.EnumerateArray()) s.Trade(Ms(D(d, "ts")), D(d, "px"), D(d, "sz"), S(d, "side") == "buy");
            return;
        }

        if (channel != "books") return;
        bool snapshot = S(m, "action") == "snapshot";
        foreach (var d in data.EnumerateArray())
        {
            long seq = (long)D(d, "seqId"), prev = (long)D(d, "prevSeqId");
            if (snapshot) s.Clear();
            else if (s.Seq < 0) continue;
            else if (prev != s.Seq && prev != -1) Session.Resync($"{s.Seq} → {prev}");
            s.Seq = seq;
            s.Levels(BookSide.Bid, d.GetProperty("bids"));
            s.Levels(BookSide.Ask, d.GetProperty("asks"));
        }
    }
}

// ============================================================================ Bitget

/// <summary>Bitget (futuros USDT y spot): libro encadenado por seq/pseq y operaciones; tamaños en monedas.</summary>
public sealed class BitgetFeed(bool spot) : PublicVenueFeed
{
    public const string FuturesId = "bitget-futures";
    public const string SpotId = "bitget-spot";
    public override string Id => spot ? SpotId : FuturesId;
    public override string DisplayName => spot ? "Bitget Spot" : "Bitget";
    public override string Venue => "Bitget";
    public override bool IsSpot => spot;
    private string InstType => spot ? "SPOT" : "USDT-FUTURES";
    protected override (TimeSpan Every, string Frame)? Keepalive => (TimeSpan.FromSeconds(25), "ping");

    protected override async Task<List<(Instrument, Spec)>> ListAsync(CancellationToken ct)
    {
        var url = spot ? "https://api.bitget.com/api/v2/spot/public/symbols" : "https://api.bitget.com/api/v2/mix/market/contracts?productType=USDT-FUTURES";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var res = new List<(Instrument, Spec)>();
        foreach (var s in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            string status = S(s, spot ? "status" : "symbolStatus");
            if (status is not ("online" or "normal")) continue;
            string quote = S(s, "quoteCoin");
            if (!Quotes.Usd(quote)) continue;
            decimal tick = spot
                ? Pow10(-(int)D(s, "pricePrecision"))
                : Math.Max(1, D(s, "priceEndStep")) * Pow10(-(int)D(s, "pricePlace"));
            decimal step = spot ? Pow10(-(int)D(s, "quantityPrecision")) : D(s, "sizeMultiplier") is > 0 and var m ? m : 0.001m;
            string sym = S(s, "symbol");
            res.Add((new Instrument
            {
                Symbol = sym,
                FeedId = Id,
                Exchange = DisplayName,
                TickSize = Norm(tick),
                VolumeStep = Norm(step),
                BaseAsset = S(s, "baseCoin"),
                QuoteAsset = quote,
                Description = $"{S(s, "baseCoin")}/{quote} {(spot ? "spot" : "perpetuo")}",
            }, new Spec(sym)));
        }

        return res;
    }

    protected override Uri WsUri(Instrument i, Spec s) => new("wss://ws.bitget.com/v2/ws/public");

    protected override IEnumerable<string> SubscribeFrames(Instrument i, Spec s)
    {
        yield return $"{{\"op\":\"subscribe\",\"args\":[{{\"instType\":\"{InstType}\",\"channel\":\"books\",\"instId\":\"{s.Native}\"}},{{\"instType\":\"{InstType}\",\"channel\":\"trade\",\"instId\":\"{s.Native}\"}}]}}";
    }

    protected override void OnMessage(string text, Session s)
    {
        if (text == "pong") return;
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        if (!m.TryGetProperty("arg", out var arg) || !m.TryGetProperty("data", out var data)) return;
        string channel = S(arg, "channel");
        bool snapshot = S(m, "action") == "snapshot";
        if (channel == "trade")
        {
            if (snapshot) return; // las últimas 50, de antes de conectar
            foreach (var d in data.EnumerateArray()) s.Trade(Ms(D(d, "ts")), D(d, "price"), D(d, "size"), S(d, "side") == "buy");
            return;
        }

        if (channel != "books") return;
        foreach (var d in data.EnumerateArray())
        {
            long seq = (long)D(d, "seq"), pseq = (long)D(d, "pseq");
            if (snapshot) s.Clear();
            else if (s.Seq <= 0 || seq <= s.Seq) continue;
            else if (pseq != s.Seq) Session.Resync($"{s.Seq} → {pseq}");
            s.Seq = seq;
            s.Levels(BookSide.Bid, d.GetProperty("bids"));
            s.Levels(BookSide.Ask, d.GetProperty("asks"));
        }
    }
}

// ============================================================================ Coinbase

/// <summary>Coinbase Exchange (spot en USD): level2_batch y matches. El lado de un match es el del que esperaba: el agresor es el otro.</summary>
public sealed class CoinbaseFeed : PublicVenueFeed
{
    public const string SpotId = "coinbase";
    public override string Id => SpotId;
    public override string DisplayName => "Coinbase";
    public override string Venue => "Coinbase";
    public override bool IsSpot => true;
    public override string PreferredQuote => "USD";

    protected override async Task<List<(Instrument, Spec)>> ListAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("https://api.exchange.coinbase.com/products", ct).ConfigureAwait(false);
        var res = new List<(Instrument, Spec)>();
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            if (S(s, "status") != "online" || S(s, "trading_disabled") == "True" || S(s, "trading_disabled") == "true") continue;
            string quote = S(s, "quote_currency");
            if (!Quotes.Usd(quote)) continue;
            string id = S(s, "id");
            res.Add((new Instrument
            {
                Symbol = id,
                FeedId = Id,
                Exchange = DisplayName,
                TickSize = Norm(D(s, "quote_increment")),
                VolumeStep = Norm(D(s, "base_increment")),
                BaseAsset = S(s, "base_currency"),
                QuoteAsset = quote,
                Description = $"{S(s, "base_currency")}/{quote} spot",
            }, new Spec(id)));
        }

        return res;
    }

    protected override Uri WsUri(Instrument i, Spec s) => new("wss://ws-feed.exchange.coinbase.com");

    protected override IEnumerable<string> SubscribeFrames(Instrument i, Spec s)
    {
        yield return $"{{\"type\":\"subscribe\",\"product_ids\":[\"{s.Native}\"],\"channels\":[\"level2_batch\",\"matches\",\"heartbeat\"]}}";
    }

    protected override void OnMessage(string text, Session s)
    {
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        switch (S(m, "type"))
        {
            case "snapshot":
                s.Clear();
                s.Levels(BookSide.Bid, m.GetProperty("bids"));
                s.Levels(BookSide.Ask, m.GetProperty("asks"));
                break;
            case "l2update":
                foreach (var c in m.GetProperty("changes").EnumerateArray())
                    s.Level(c[0].GetString() == "buy" ? BookSide.Bid : BookSide.Ask, D(c[1]), D(c[2]));
                break;
            case "match":
                // el lado es el de la orden que esperaba
                s.Trade(DateTime.Parse(S(m, "time"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                    D(m, "price"), D(m, "size"), S(m, "side") == "sell");
                break;
            case "error":
                throw new InvalidDataException(S(m, "message") + " " + S(m, "reason"));
        }
    }
}

// ============================================================================ Deribit

/// <summary>Deribit (perpetuos): libro completo encadenado por change_id y operaciones. BTC-PERPETUAL y ETH-PERPETUAL se miden en USD.</summary>
public sealed class DeribitFeed : PublicVenueFeed
{
    public const string PerpId = "deribit";
    public override string Id => PerpId;
    public override string DisplayName => "Deribit";
    public override string Venue => "Deribit";
    public override bool IsSpot => false;
    protected override (TimeSpan Every, string Frame)? Keepalive => (TimeSpan.FromSeconds(60), "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"public/test\"}");
    protected override TimeSpan Stale => TimeSpan.FromSeconds(90);

    protected override async Task<List<(Instrument, Spec)>> ListAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("https://www.deribit.com/api/v2/public/get_instruments?kind=future&currency=any", ct).ConfigureAwait(false);
        var res = new List<(Instrument, Spec)>();
        foreach (var s in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            if (S(s, "settlement_period") != "perpetual" || S(s, "is_active") is "false" or "False") continue;
            string quote = S(s, "quote_currency"), bas = S(s, "base_currency"), name = S(s, "instrument_name");
            if (!Quotes.Usd(quote)) continue;
            bool inverse = S(s, "instrument_type") == "reversed";
            res.Add((new Instrument
            {
                Symbol = name,
                FeedId = Id,
                Exchange = DisplayName,
                TickSize = Norm(D(s, "tick_size")),
                VolumeStep = inverse ? 0.00001m : Norm(D(s, "min_trade_amount") is > 0 and var v ? v : 0.001m),
                BaseAsset = bas,
                QuoteAsset = quote,
                Description = $"{bas}/{quote} perpetuo{(inverse ? " (inverso)" : "")}",
            }, new Spec(name, 1, inverse)));
        }

        return res;
    }

    protected override Uri WsUri(Instrument i, Spec s) => new("wss://www.deribit.com/ws/api/v2");

    protected override IEnumerable<string> SubscribeFrames(Instrument i, Spec s)
    {
        yield return $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"public/subscribe\",\"params\":{{\"channels\":[\"book.{s.Native}.100ms\",\"trades.{s.Native}.100ms\"]}}}}";
    }

    protected override void OnMessage(string text, Session s)
    {
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        if (!m.TryGetProperty("params", out var p)) return;
        string channel = S(p, "channel");
        var data = p.GetProperty("data");
        if (channel.StartsWith("book.", StringComparison.Ordinal))
        {
            long change = (long)D(data, "change_id");
            string type = S(data, "type");
            if (type == "snapshot") s.Clear();
            else if (s.Seq < 0) return;
            else if ((long)D(data, "prev_change_id") != s.Seq) Session.Resync($"{s.Seq} → {S(data, "prev_change_id")}");
            s.Seq = change;
            Rows(s, BookSide.Bid, data.GetProperty("bids"));
            Rows(s, BookSide.Ask, data.GetProperty("asks"));
            return;
        }

        if (channel.StartsWith("trades.", StringComparison.Ordinal))
            foreach (var d in data.EnumerateArray())
                s.Trade(Ms(D(d, "timestamp")), D(d, "price"), D(d, "amount"), S(d, "direction") == "buy");
    }

    /// <summary>Filas [new | change | delete, precio, cantidad].</summary>
    private static void Rows(Session s, BookSide side, JsonElement rows)
    {
        foreach (var r in rows.EnumerateArray())
        {
            if (r.GetArrayLength() < 3) continue;
            bool delete = r[0].GetString() == "delete";
            s.Level(side, D(r[1]), delete ? 0 : D(r[2]));
        }
    }
}

// ============================================================================ Hyperliquid

/// <summary>
/// Hyperliquid (perpetuos en USD): l2Book agrupado a 4 cifras significativas (20 niveles por lado, ~$10 en BTC) y
/// operaciones. El libro llega entero en cada mensaje. kPEPE (miles) entra como 1000PEPE, como en Binance.
/// </summary>
public sealed class HyperliquidFeed : PublicVenueFeed
{
    public const string PerpId = "hyperliquid";
    public override string Id => PerpId;
    public override string DisplayName => "Hyperliquid";
    public override string Venue => "Hyperliquid";
    public override bool IsSpot => false;
    protected override (TimeSpan Every, string Frame)? Keepalive => (TimeSpan.FromSeconds(30), "{\"method\":\"ping\"}");

    protected override async Task<List<(Instrument, Spec)>> ListAsync(CancellationToken ct)
    {
        using var content = new StringContent("{\"type\":\"metaAndAssetCtxs\"}", Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync("https://api.hyperliquid.xyz/info", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var universe = doc.RootElement[0].GetProperty("universe");
        var ctxs = doc.RootElement[1];
        var res = new List<(Instrument, Spec)>();
        int n = 0;
        foreach (var u in universe.EnumerateArray())
        {
            int idx = n++;
            if (S(u, "isDelisted") is "true" or "True") continue;
            string coin = S(u, "name");
            int szDec = (int)D(u, "szDecimals");
            decimal px = idx < ctxs.GetArrayLength() ? D(ctxs[idx], "markPx") : 0;
            res.Add((new Instrument
            {
                Symbol = coin,
                FeedId = Id,
                Exchange = DisplayName,
                TickSize = Norm(TickFor(px, szDec)),
                VolumeStep = Norm(Pow10(-szDec)),
                BaseAsset = coin.Length > 1 && coin[0] == 'k' && char.IsUpper(coin[1]) ? "1000" + coin[1..] : coin,
                QuoteAsset = "USD",
                Description = $"{coin}/USD perpetuo",
            }, new Spec(coin)));
        }

        return res;
    }

    /// <summary>Precios a 5 cifras significativas y como mucho 6 − szDecimals decimales.</summary>
    public static decimal TickFor(decimal price, int szDecimals)
    {
        decimal min = Pow10(-Math.Max(0, 6 - szDecimals));
        if (price <= 0) return min;
        int mag = (int)Math.Floor(Math.Log10((double)price));
        return Math.Max(min, Pow10(mag - 4));
    }

    protected override Uri WsUri(Instrument i, Spec s) => new("wss://api.hyperliquid.xyz/ws");

    protected override IEnumerable<string> SubscribeFrames(Instrument i, Spec s)
    {
        yield return $"{{\"method\":\"subscribe\",\"subscription\":{{\"type\":\"l2Book\",\"coin\":\"{s.Native}\",\"nSigFigs\":4}}}}";
        yield return $"{{\"method\":\"subscribe\",\"subscription\":{{\"type\":\"trades\",\"coin\":\"{s.Native}\"}}}}";
    }

    protected override void OnMessage(string text, Session s)
    {
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        string channel = S(m, "channel");
        if (channel == "l2Book")
        {
            var data = m.GetProperty("data");
            if (S(data, "coin") != s.Spec.Native) return;
            var levels = data.GetProperty("levels");
            if (levels.GetArrayLength() < 2) return;
            s.Clear();
            foreach (var l in levels[0].EnumerateArray()) s.Level(BookSide.Bid, D(l, "px"), D(l, "sz"));
            foreach (var l in levels[1].EnumerateArray()) s.Level(BookSide.Ask, D(l, "px"), D(l, "sz"));
            return;
        }

        if (channel == "trades")
            foreach (var d in m.GetProperty("data").EnumerateArray())
            {
                if (S(d, "coin") != s.Spec.Native) continue;
                s.Trade(Ms(D(d, "time")), D(d, "px"), D(d, "sz"), S(d, "side") == "B");
            }
    }
}

// ============================================================================ MEXC

/// <summary>MEXC (perpetuos USDT): los 20 mejores niveles enteros en cada mensaje y operaciones; tamaños en contratos.</summary>
public sealed class MexcFeed : PublicVenueFeed
{
    public const string PerpId = "mexc";
    public override string Id => PerpId;
    public override string DisplayName => "MEXC";
    public override string Venue => "MEXC";
    public override bool IsSpot => false;
    protected override (TimeSpan Every, string Frame)? Keepalive => (TimeSpan.FromSeconds(15), "{\"method\":\"ping\"}");

    protected override async Task<List<(Instrument, Spec)>> ListAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("https://contract.mexc.com/api/v1/contract/detail", ct).ConfigureAwait(false);
        var res = new List<(Instrument, Spec)>();
        foreach (var s in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (D(s, "state") != 0) continue;
            // solo los perpetuos en USDT (los BTC_USD son inversos, con contratos en dólares)
            string quote = S(s, "quoteCoin");
            if (quote != "USDT" || S(s, "settleCoin") is { Length: > 0 } settle && settle != "USDT") continue;
            decimal size = D(s, "contractSize");
            if (size <= 0) continue;
            string sym = S(s, "symbol");
            res.Add((new Instrument
            {
                Symbol = sym,
                FeedId = Id,
                Exchange = DisplayName,
                TickSize = Norm(D(s, "priceUnit")),
                VolumeStep = Norm(size * Math.Max(1, D(s, "volUnit"))),
                BaseAsset = S(s, "baseCoin"),
                QuoteAsset = quote,
                Description = $"{S(s, "baseCoin")}/{quote} perpetuo",
            }, new Spec(sym, size)));
        }

        return res;
    }

    protected override Uri WsUri(Instrument i, Spec s) => new("wss://contract.mexc.com/edge");

    protected override IEnumerable<string> SubscribeFrames(Instrument i, Spec s)
    {
        yield return $"{{\"method\":\"sub.depth.full\",\"param\":{{\"symbol\":\"{s.Native}\",\"limit\":20}}}}";
        yield return $"{{\"method\":\"sub.deal\",\"param\":{{\"symbol\":\"{s.Native}\"}}}}";
    }

    protected override void OnMessage(string text, Session s)
    {
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        string channel = S(m, "channel");
        if (channel == "rs.error") throw new InvalidDataException("MEXC: " + S(m, "data"));
        if (m.TryGetProperty("symbol", out var sym) && sym.GetString() != s.Spec.Native) return;
        if (channel == "push.depth.full")
        {
            var d = m.GetProperty("data");
            s.Clear();
            s.Levels(BookSide.Bid, d.GetProperty("bids"));
            s.Levels(BookSide.Ask, d.GetProperty("asks"));
            return;
        }

        if (channel == "push.deal")
        {
            var data = m.GetProperty("data");
            if (data.ValueKind == JsonValueKind.Array)
                foreach (var d in data.EnumerateArray()) Deal(s, d);
            else Deal(s, data);
        }
    }

    // T: lado del agresor (1 compra, 2 venta); v: contratos
    private static void Deal(Session s, JsonElement d) => s.Trade(Ms(D(d, "t")), D(d, "p"), D(d, "v"), D(d, "T") == 1);
}

/// <summary>Todos los mercados cripto públicos que se pueden sumar al agregado.</summary>
public static class CryptoVenues
{
    public sealed record Entry(string Id, string Name, bool Spot, Func<IMarketDataFeed> Create);

    public static readonly IReadOnlyList<Entry> All = new Entry[]
    {
        new(Binance.BinanceFeed.FuturesId, "Binance Futures", false, () => new Binance.BinanceFeed(futures: true)),
        new(Bybit.BybitFeed.LinearId, "Bybit", false, () => new Bybit.BybitFeed()),
        new(OkxFeed.SwapId, "OKX", false, () => new OkxFeed(false)),
        new(BitgetFeed.FuturesId, "Bitget", false, () => new BitgetFeed(false)),
        new(HyperliquidFeed.PerpId, "Hyperliquid", false, () => new HyperliquidFeed()),
        new(DeribitFeed.PerpId, "Deribit", false, () => new DeribitFeed()),
        new(MexcFeed.PerpId, "MEXC", false, () => new MexcFeed()),
        new(Binance.BinanceFeed.SpotId, "Binance Spot", true, () => new Binance.BinanceFeed(futures: false)),
        new(CoinbaseFeed.SpotId, "Coinbase", true, () => new CoinbaseFeed()),
        new(Bybit.BybitFeed.SpotId, "Bybit Spot", true, () => new Bybit.BybitFeed(spot: true)),
        new(OkxFeed.SpotId, "OKX Spot", true, () => new OkxFeed(true)),
        new(BitgetFeed.SpotId, "Bitget Spot", true, () => new BitgetFeed(true)),
    };

    public static Entry? Find(string id) => All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Los mercados que LiquidityMapperFast trae prendidos de entrada (perpetuos grandes y spot de Binance y Coinbase).</summary>
    public static readonly string[] Recommended =
    {
        Binance.BinanceFeed.FuturesId, Bybit.BybitFeed.LinearId, OkxFeed.SwapId, BitgetFeed.FuturesId, HyperliquidFeed.PerpId,
        DeribitFeed.PerpId, Binance.BinanceFeed.SpotId, CoinbaseFeed.SpotId,
    };
}
