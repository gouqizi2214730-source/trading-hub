using System.Globalization;
using TradingHub.Market;

namespace TradingHub.Feeds.Longbridge;

/// <summary>
/// Acciones y ETFs de Hong Kong, EE.UU., Shanghái/Shenzhen y Singapur por 长桥 (Longbridge OpenAPI). La historia larga
/// viene en velas (1, 5 y 15 minutos, hasta años) y el tape del día con la dirección de cada operación; lo que no tiene
/// ticks se completa con las velas, repartiendo el volumen por la forma de la vela (delta estimado).
/// </summary>
public sealed class LongbridgeFeed : IMarketDataFeed, IKlineHistory, ISessionAwareFeed
{
    public const string FeedId = "longbridge";
    private readonly LongbridgeSettings _s;
    private readonly LongbridgeHttp _http;
    private readonly LongbridgeSocket _socket;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<Live>> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal> _lastPrice = new(StringComparer.OrdinalIgnoreCase);
    private long _liveId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    public LongbridgeFeed(LongbridgeSettings settings)
    {
        _s = settings;
        _http = new LongbridgeHttp(settings);
        _socket = new LongbridgeSocket(_http, settings);
        _socket.Push += OnPush;
        _socket.Status += OnStatus;
    }

    public string Id => FeedId;
    public string DisplayName => "长桥 (Longbridge)";
    public bool SupportsHistory => true;
    public bool IsLive => true;

    /// <summary>El tape solo llega hasta las últimas 1000 operaciones del día: el resto sale de las velas.</summary>
    public bool RecentTradesOnly => true;

    public LongbridgeSettings Settings => _s;

    // ===================================================================== mercados

    /// <summary>Mercado por el sufijo del símbolo (700.HK → HK).</summary>
    public static string Market(string symbol)
    {
        int i = symbol.LastIndexOf('.');
        return i >= 0 ? symbol[(i + 1)..].ToUpperInvariant() : "";
    }

    /// <summary>Zona horaria del mercado (hora de Hong Kong, Nueva York, Shanghái o Singapur).</summary>
    public static TimeZoneInfo MarketTimeZone(string symbol) => Market(symbol) switch
    {
        "US" => Tz("America/New_York", "Eastern Standard Time"),
        "SG" => Tz("Asia/Singapore", "Singapore Standard Time"),
        _ => Tz("Asia/Hong_Kong", "China Standard Time"),
    };

    private static readonly Dictionary<string, TimeZoneInfo> TzCache = new();

    private static TimeZoneInfo Tz(string iana, string windows)
    {
        lock (TzCache)
        {
            if (TzCache.TryGetValue(iana, out var z)) return z;
            try
            {
                z = TimeZoneInfo.FindSystemTimeZoneById(iana);
            }
            catch (TimeZoneNotFoundException)
            {
                try
                {
                    z = TimeZoneInfo.FindSystemTimeZoneById(windows);
                }
                catch (TimeZoneNotFoundException)
                {
                    z = TimeZoneInfo.CreateCustomTimeZone(iana, iana == "America/New_York" ? TimeSpan.FromHours(-5) : TimeSpan.FromHours(8), iana, iana);
                }
            }

            TzCache[iana] = z;
            return z;
        }
    }

    /// <summary>
    /// Tamaño del tick según el mercado y el precio: Hong Kong usa una tabla por tramos (0,001 hasta 5); EE.UU. y
    /// China continental, un centavo.
    /// </summary>
    public static decimal TickSizeFor(string symbol, decimal price)
    {
        if (Market(symbol) != "HK") return 0.01m;
        return price switch
        {
            < 0.25m => 0.001m,
            < 0.5m => 0.005m,
            < 10m => 0.01m,
            < 20m => 0.02m,
            < 100m => 0.05m,
            < 200m => 0.1m,
            < 500m => 0.2m,
            < 1000m => 0.5m,
            < 2000m => 1m,
            < 5000m => 2m,
            _ => 5m,
        };
    }

    /// <summary>Sesiones a pedir: en EE.UU. también pre y post mercado (100 = todas); en los demás, la normal.</summary>
    private static int TradeSession(string symbol) => Market(symbol) == "US" ? 100 : 0;

    // ===================================================================== instrumentos

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var symbols = _s.Symbols.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0 && s.Contains('.')).Distinct().ToList();
        if (symbols.Count == 0) return Array.Empty<Instrument>();
        var infos = new Dictionary<string, LbMessages.LbStaticInfo>(StringComparer.OrdinalIgnoreCase);
        var quotes = new Dictionary<string, LbMessages.LbQuote>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in symbols.Chunk(200))
        {
            try
            {
                foreach (var i in LbMessages.ReadStaticInfo(await _socket.RequestAsync(LbCmd.QuerySecurityStaticInfo, LbMessages.MultiSecurityRequest(chunk), ct).ConfigureAwait(false)))
                    infos[i.Symbol] = i;
            }
            catch (LongbridgeException ex)
            {
                LongbridgeLog.Warn("长桥: no se pudo leer la información de los símbolos: " + ex.Message);
            }

            try
            {
                foreach (var q in LbMessages.ReadQuotes(await _socket.RequestAsync(LbCmd.QuerySecurityQuote, LbMessages.MultiSecurityRequest(chunk), ct).ConfigureAwait(false)))
                    quotes[q.Symbol] = q;
            }
            catch (LongbridgeException ex)
            {
                LongbridgeLog.Warn("长桥: no se pudieron leer las cotizaciones: " + ex.Message);
            }
        }

        var list = new List<Instrument>();
        foreach (var sym in symbols)
        {
            infos.TryGetValue(sym, out var info);
            quotes.TryGetValue(sym, out var q);
            decimal price = q.LastDone > 0 ? q.LastDone : q.PrevClose;
            if (price > 0)
                lock (_lock)
                {
                    _lastPrice[sym] = price;
                }

            list.Add(Make(sym, info, price));
        }

        return list;
    }

    private static Instrument Make(string symbol, LbMessages.LbStaticInfo? info, decimal price)
    {
        string market = Market(symbol);
        string name = info == null ? symbol : info.NameCn.Length > 0 ? info.NameCn : info.NameEn.Length > 0 ? info.NameEn : info.NameHk;
        return new Instrument
        {
            Symbol = symbol,
            FeedId = FeedId,
            Exchange = info?.Exchange is { Length: > 0 } ex ? ex : market,
            TickSize = TickSizeFor(symbol, price),
            VolumeStep = 1,
            PointValue = 1,
            QuoteAsset = info?.Currency is { Length: > 0 } cur ? cur : market switch { "US" => "USD", "HK" => "HKD", "SG" => "SGD", _ => "CNY" },
            Description = name,
        };
    }

    /// <summary>El instrumento de un símbolo que no está en la lista (se consulta su información).</summary>
    public async Task<Instrument> LookupAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var infos = LbMessages.ReadStaticInfo(await _socket.RequestAsync(LbCmd.QuerySecurityStaticInfo, LbMessages.MultiSecurityRequest(new[] { symbol }), ct).ConfigureAwait(false));
        if (infos.Count == 0) throw new InvalidOperationException($"长桥 no conoce el símbolo {symbol} (formato: 700.HK, AAPL.US, 600519.SH).");
        decimal price = 0;
        try
        {
            var q = LbMessages.ReadQuotes(await _socket.RequestAsync(LbCmd.QuerySecurityQuote, LbMessages.MultiSecurityRequest(new[] { symbol }), ct).ConfigureAwait(false));
            if (q.Count > 0) price = q[0].LastDone > 0 ? q[0].LastDone : q[0].PrevClose;
        }
        catch (LongbridgeException)
        {
        }

        return Make(symbol, infos[0], price);
    }

    // ===================================================================== operaciones del día

    private static DateTime FromSec(long sec) => DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime;

    private static TradeSide Side(LbMessages.LbTrade t, ref decimal last, ref TradeSide lastSide)
    {
        var side = t.Direction switch { 2 => TradeSide.Buy, 1 => TradeSide.Sell, _ => HistoryBuilder.TickRule(t.Price, ref last, ref lastSide) };
        last = t.Price;
        lastSide = side;
        return side;
    }

    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        // solo hay tape del día: las últimas 1000 operaciones
        var body = await _socket.RequestAsync(LbCmd.QueryTrade, LbMessages.TradeRequest(instrument.Symbol, 1000), ct).ConfigureAwait(false);
        var (_, trades) = LbMessages.ReadTrades(body, push: false);
        trades.Sort((a, b) => a.TimestampSec.CompareTo(b.TimestampSec));
        var batch = new List<Trade>(trades.Count);
        long lastId = 0;
        decimal last = 0;
        var lastSide = TradeSide.Buy;
        foreach (var t in trades)
        {
            var time = FromSec(t.TimestampSec);
            if (t.Volume <= 0 || time < fromUtc || time >= toUtc) continue;
            batch.Add(new Trade(HistoryBuilder.TimeId(time, ref lastId), time, instrument.RoundPrice(t.Price), t.Volume, Side(t, ref last, ref lastSide)));
        }

        if (batch.Count > 0) onBatch(batch);
        progress?.Report(1);
    }

    public async Task<DateTime?> LastTradeTimeAsync(Instrument instrument, CancellationToken ct)
    {
        var q = LbMessages.ReadQuotes(await _socket.RequestAsync(LbCmd.QuerySecurityQuote, LbMessages.MultiSecurityRequest(new[] { instrument.Symbol }), ct).ConfigureAwait(false));
        return q.Count > 0 && q[0].TimestampSec > 0 ? FromSec(q[0].TimestampSec) : null;
    }

    // ===================================================================== velas

    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(null);

    public string CacheTag => "lb";

    /// <summary>Fecha y minuto de un instante en la hora del mercado (yyyyMMdd, HHmm).</summary>
    public static (string Date, string Minute) MarketTime(string symbol, DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), MarketTimeZone(symbol));
        return (local.ToString("yyyyMMdd", CultureInfo.InvariantCulture), local.ToString("HHmm", CultureInfo.InvariantCulture));
    }

    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        if (toUtc <= fromUtc) return;
        int m = minutes >= 15 ? 15 : minutes >= 5 ? 5 : 1;
        double total = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        var cursor = fromUtc;
        long lastSeen = -1;
        int session = TradeSession(instrument.Symbol);
        for (int page = 0; page < 5000 && !ct.IsCancellationRequested; page++)
        {
            var (date, minute) = MarketTime(instrument.Symbol, cursor);
            var body = await _socket.RequestAsync(LbCmd.QueryHistoryCandlestick, LbMessages.HistoryCandlestickRequest(instrument.Symbol, m, forward: true, date, minute, 1000, session), ct, 20000).ConfigureAwait(false);
            var candles = LbMessages.ReadCandlesticks(body);
            var batch = new List<Kline>(candles.Count);
            long newest = lastSeen;
            foreach (var c in candles)
            {
                if (c.TimestampSec <= lastSeen) continue;
                newest = c.TimestampSec;
                var open = FromSec(c.TimestampSec);
                if (open < fromUtc) continue;
                if (open >= toUtc) break;
                batch.Add(new Kline(c.TimestampSec * 1000, c.Open, c.High, c.Low, c.Close, Math.Max(1, c.Volume), 0, false));
            }

            if (batch.Count > 0)
            {
                if (!onBatch(batch)) return;
                progress?.Report(Math.Min(1, (FromSec(batch[^1].OpenTimeMs / 1000) - fromUtc).TotalSeconds / total));
            }

            if (newest <= lastSeen || candles.Count < 2) break; // no avanzó: no hay más
            lastSeen = newest;
            var next = FromSec(newest).AddMinutes(m);
            if (next >= toUtc) break;
            cursor = next;
        }

        progress?.Report(1);
    }

    // ===================================================================== en vivo

    private sealed class Live : IDisposable
    {
        public Live(LongbridgeFeed feed, Instrument instrument, IMarketDataSink sink)
        {
            Feed = feed;
            Instrument = instrument;
            Sink = sink;
            Subscription = new LiveSubscription("长桥", (st, msg) => sink.OnStatus(instrument, st, msg));
        }

        public LongbridgeFeed Feed { get; }
        public Instrument Instrument { get; }
        public IMarketDataSink Sink { get; }
        public LiveSubscription Subscription { get; }
        public OrderBookBuilder Book { get; } = new();
        public decimal LastPrice;
        public TradeSide LastSide = TradeSide.Buy;

        public void Dispose()
        {
            Feed.Remove(this);
            Subscription.Dispose();
        }
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var live = new Live(this, instrument, sink);
        bool first;
        lock (_lock)
        {
            if (!_live.TryGetValue(instrument.Symbol, out var list)) _live[instrument.Symbol] = list = new List<Live>();
            first = list.Count == 0;
            list.Add(live);
        }

        live.Subscription.SetChannel("datos", _socket.IsConnected ? FeedStatus.Connected : FeedStatus.Connecting, _socket.IsConnected ? null : "Conectando a 长桥…");
        _ = Task.Run(async () =>
        {
            try
            {
                if (first) await _socket.SubscribeAsync(new[] { instrument.Symbol }, live.Subscription.Token).ConfigureAwait(false);
                else await _socket.EnsureConnectedAsync(live.Subscription.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                live.Subscription.SetChannel("datos", FeedStatus.Error, ex.Message);
            }
        });
        return live;
    }

    private void Remove(Live live)
    {
        bool last = false;
        lock (_lock)
        {
            if (_live.TryGetValue(live.Instrument.Symbol, out var list))
            {
                list.Remove(live);
                if (list.Count == 0)
                {
                    _live.Remove(live.Instrument.Symbol);
                    last = true;
                }
            }
        }

        if (last) _ = _socket.UnsubscribeAsync(new[] { live.Instrument.Symbol }, CancellationToken.None);
    }

    private List<Live> LiveFor(string symbol)
    {
        lock (_lock)
        {
            return _live.TryGetValue(symbol, out var list) ? new List<Live>(list) : new List<Live>();
        }
    }

    private void OnStatus(FeedStatus st, string? msg)
    {
        List<Live> all;
        lock (_lock)
        {
            all = _live.Values.SelectMany(l => l).ToList();
        }

        foreach (var l in all) l.Subscription.SetChannel("datos", st, msg);
    }

    private void OnPush(byte cmd, byte[] body)
    {
        switch (cmd)
        {
            case LbCmd.PushTradeData:
            {
                var (symbol, trades) = LbMessages.ReadTrades(body, push: true);
                var lives = LiveFor(symbol);
                if (lives.Count == 0 || trades.Count == 0) return;
                foreach (var live in lives)
                {
                    var list = new List<Trade>(trades.Count);
                    foreach (var t in trades)
                    {
                        if (t.Volume <= 0) continue;
                        decimal price = live.Instrument.RoundPrice(t.Price);
                        var side = t.Direction switch { 2 => TradeSide.Buy, 1 => TradeSide.Sell, _ => HistoryBuilder.TickRule(price, ref live.LastPrice, ref live.LastSide) };
                        live.LastPrice = price;
                        live.LastSide = side;
                        list.Add(new Trade(Interlocked.Increment(ref _liveId), FromSec(t.TimestampSec), price, t.Volume, side));
                    }

                    if (list.Count > 0) live.Sink.OnTrades(live.Instrument, list);
                }

                break;
            }

            case LbCmd.PushDepthData:
            {
                var (symbol, asks, bids) = LbMessages.ReadDepth(body, push: true);
                foreach (var live in LiveFor(symbol))
                {
                    live.Book.Clear();
                    foreach (var a in asks)
                        if (a.Price > 0 && a.Volume > 0) live.Book.Set(BookSide.Ask, live.Instrument.ToTicks(a.Price), a.Volume);
                    foreach (var b in bids)
                        if (b.Price > 0 && b.Volume > 0) live.Book.Set(BookSide.Bid, live.Instrument.ToTicks(b.Price), b.Volume);
                    live.Sink.OnDepth(live.Instrument, live.Book.BuildUpdate(DateTime.UtcNow, 50, true));
                }

                break;
            }
        }
    }

    /// <summary>Prueba la sesión: pide la información de un par de símbolos conocidos.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        if (!_s.HasToken) return (false, "Todavía no entraste: tocá «Conectar con 长桥» para autorizar la app con tu cuenta.");
        var infos = LbMessages.ReadStaticInfo(await _socket.RequestAsync(LbCmd.QuerySecurityStaticInfo, LbMessages.MultiSecurityRequest(new[] { "700.HK", "AAPL.US" }), ct).ConfigureAwait(false));
        string names = string.Join(", ", infos.Select(i => $"{i.Symbol} ({(i.NameCn.Length > 0 ? i.NameCn : i.NameEn)})"));
        return (true, $"Sesión OK. {names}. {_s.Symbols.Count} símbolos en el buscador (se editan acá abajo).");
    }

    public void Dispose()
    {
        _socket.Dispose();
        _http.Dispose();
    }
}
