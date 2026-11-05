using System.Threading.Channels;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Feeds;

/// <summary>
/// Mercado agregado: suma las operaciones y las velas de varios mercados del mismo instrumento (BTCUSDT de Binance
/// Futures + Bybit, por ejemplo), como hacen aggr.trade o VPFM. El volumen y el delta son la suma; el precio es el del
/// primer mercado cuando difieren. La profundidad (DOM) es la del primer mercado.
/// </summary>
public sealed class AggregateFeed : IMarketDataFeed, IKlineHistory, ITickerStatsProvider
{
    public const string FeedId = "agregado";
    private readonly IReadOnlyList<IMarketDataFeed> _sources;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<(IMarketDataFeed Feed, Instrument Instrument)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _listing = new(1, 1);
    private long _liveSlot;

    /// <summary>
    /// Ids de las operaciones agregadas: el instante en microsegundos × 16 más el número del mercado de origen (0 a 15), así
    /// cada operación sabe de qué mercado vino (para el CVD por mercado) y los ids siguen creciendo con el tiempo.
    /// </summary>
    public static long TaggedId(DateTime utc, int source, ref long lastSlot)
    {
        long slot = (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        if (slot <= lastSlot) slot = lastSlot + 1;
        lastSlot = slot;
        return slot * SourceSlots + (source & (SourceSlots - 1));
    }

    /// <summary>Mercado de origen de una operación agregada (índice en <see cref="Sources"/>; -1 si no se sabe).</summary>
    public static int SourceOf(long id) => id > 0 ? (int)(id & (SourceSlots - 1)) : -1;

    /// <summary>Cuántos mercados distintos pueden sumarse (los ids guardan el número de cada uno).</summary>
    public const int SourceSlots = 16;

    /// <summary>
    /// Nombre común de un instrumento en todos los mercados: la moneda más «USDT» (BTC-USDT-SWAP de OKX, BTC-USD de
    /// Coinbase, BTC de Hyperliquid y BTCUSDT de Binance son el mismo «BTCUSDT»). Null si no cotiza en dólares.
    /// </summary>
    public static string? CanonicalSymbol(Instrument i)
    {
        string b = i.BaseAsset.Trim().ToUpperInvariant(), q = i.QuoteAsset.Trim().ToUpperInvariant();
        if (b.Length == 0) return i.Symbol.ToUpperInvariant();
        if (q is not ("USDT" or "USD" or "USDC")) return null;
        return b + "USDT";
    }

    private static int QuoteRank(IMarketDataFeed feed, Instrument i)
    {
        string q = i.QuoteAsset.ToUpperInvariant();
        if (feed is Venues.ICryptoVenue v && q == v.PreferredQuote) return 0;
        return q switch { "USDT" => 1, "USD" => 2, _ => 3 };
    }

    /// <summary>Si el mercado de origen es spot (el resto, perpetuos).</summary>
    public bool IsSpot(int source) => source >= 0 && source < _sources.Count && _sources[source] is Venues.ICryptoVenue { IsSpot: true };

    /// <summary>El exchange de un mercado de origen (Binance Futures y Binance Spot son «Binance»).</summary>
    public string VenueOf(int source) => source >= 0 && source < _sources.Count
        ? _sources[source] is Venues.ICryptoVenue v ? v.Venue : _sources[source].DisplayName
        : "?";

    private long NextLiveId(DateTime utc, int source)
    {
        lock (this) return TaggedId(utc, source, ref _liveSlot);
    }

    private int SourceIndex(IMarketDataFeed feed)
    {
        for (int i = 0; i < _sources.Count; i++)
            if (ReferenceEquals(_sources[i], feed)) return i;
        return SourceSlots - 1;
    }

    public AggregateFeed(IReadOnlyList<IMarketDataFeed> sources)
    {
        if (sources.Count == 0) throw new ArgumentException("hace falta al menos un mercado", nameof(sources));
        _sources = sources;
    }

    public IReadOnlyList<IMarketDataFeed> Sources => _sources;

    /// <summary>
    /// El libro de órdenes suma los de todos los mercados (precio por precio), como el «aggregated order book heatmap» de
    /// openmarket. Apagado: el libro es el del primer mercado.
    /// </summary>
    public bool AggregateBook { get; set; } = true;

    /// <summary>Junta los libros de cada mercado en uno (precios ya en ticks del instrumento agregado).</summary>
    public static DepthSnapshot MergeBooks(IEnumerable<DepthSnapshot> books, DateTime time)
    {
        var bids = new Dictionary<long, decimal>();
        var asks = new Dictionary<long, decimal>();
        foreach (var b in books)
        {
            foreach (var l in b.Bids) bids[l.PriceTicks] = (bids.TryGetValue(l.PriceTicks, out var v) ? v : 0) + l.Size;
            foreach (var l in b.Asks) asks[l.PriceTicks] = (asks.TryGetValue(l.PriceTicks, out var v) ? v : 0) + l.Size;
        }

        var bl = bids.Where(kv => kv.Value > 0).Select(kv => new BookLevel(kv.Key, kv.Value)).OrderByDescending(l => l.PriceTicks).ToArray();
        var al = asks.Where(kv => kv.Value > 0).Select(kv => new BookLevel(kv.Key, kv.Value)).OrderBy(l => l.PriceTicks).ToArray();
        return new DepthSnapshot(bl, al, time);
    }
    public string Id => FeedId;
    public string DisplayName => "Agregado (" + string.Join(" + ", _sources.Select(s => s.DisplayName)) + ")";
    public bool SupportsHistory => true;
    public bool IsLive => _sources.Any(s => s.IsLive);

    /// <summary>Si alguno de los mercados solo da las últimas operaciones, lo que falta se completa con las velas sumadas.</summary>
    public bool RecentTradesOnly => _sources.All(s => s.RecentTradesOnly);

    // ===================================================================== instrumentos

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var lists = new List<IReadOnlyList<Instrument>>();
        foreach (var s in _sources)
        {
            try
            {
                lists.Add(await s.GetInstrumentsAsync(ct).ConfigureAwait(false));
            }
            catch (Exception) when (lists.Count > 0 || _sources.Count > 1)
            {
                lists.Add(Array.Empty<Instrument>()); // un mercado caído no tira el agregado
            }
        }

        var bySymbol = new Dictionary<string, List<(IMarketDataFeed, Instrument)>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lists.Count; i++)
            foreach (var inst in lists[i])
            {
                if (CanonicalSymbol(inst) is not { } key) continue;
                if (!bySymbol.TryGetValue(key, out var l)) bySymbol[key] = l = new List<(IMarketDataFeed, Instrument)>();
                // un mercado entra una sola vez por moneda (BTCUSDT antes que BTCUSDC)
                int same = l.FindIndex(x => ReferenceEquals(x.Item1, _sources[i]));
                if (same < 0) l.Add((_sources[i], inst));
                else if (QuoteRank(_sources[i], inst) < QuoteRank(_sources[i], l[same].Item2)) l[same] = (_sources[i], inst);
            }

        var result = new List<Instrument>();
        lock (_lock)
        {
            _map.Clear();
            foreach (var (symbol, parts) in bySymbol)
            {
                // en común entre al menos dos mercados (con un solo mercado, todos)
                if (parts.Count < Math.Min(2, _sources.Count)) continue;
                _map[symbol] = parts;
                result.Add(Make(symbol, parts));
            }
        }

        return result.OrderBy(i => i.Symbol).ToList();
    }

    private Instrument Make(string symbol, List<(IMarketDataFeed Feed, Instrument Instrument)> parts)
    {
        var first = parts[0].Instrument;
        return new Instrument
        {
            Symbol = symbol,
            FeedId = FeedId,
            Exchange = string.Join("+", parts.Select(p => p.Feed.DisplayName)),
            TickSize = parts.Min(p => p.Instrument.TickSize),
            VolumeStep = parts.Min(p => p.Instrument.VolumeStep),
            PointValue = first.PointValue,
            BaseAsset = first.BaseAsset,
            QuoteAsset = first.QuoteAsset,
            Description = $"{(first.BaseAsset.Length > 0 ? first.BaseAsset + "/" + first.QuoteAsset : symbol)} agregado ({string.Join(" + ", parts.Select(p => p.Feed.DisplayName))})",
        };
    }

    /// <summary>Los mercados de un símbolo (lista los instrumentos si todavía no se hizo).</summary>
    private async Task<List<(IMarketDataFeed Feed, Instrument Instrument)>> PartsAsync(Instrument instrument, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(instrument.Symbol, out var p)) return p;
        }

        await _listing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_map.TryGetValue(instrument.Symbol, out var p)) return p;
            }

            await GetInstrumentsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _listing.Release();
        }

        lock (_lock)
        {
            return _map.TryGetValue(instrument.Symbol, out var p) ? p : throw new InvalidOperationException($"{instrument.Symbol} no está en los mercados agregados");
        }
    }

    // ===================================================================== operaciones

    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        var parts = await PartsAsync(instrument, ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channels = new List<Channel<IReadOnlyList<Trade>>>();
        var producers = new List<Task>();
        var progresses = new double[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var ch = Channel.CreateBounded<IReadOnlyList<Trade>>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true });
            channels.Add(ch);
            var (feed, inst) = parts[i];
            int index = i;
            var sub = new Progress<double>(p =>
            {
                progresses[index] = p;
                progress?.Report(progresses.Min());
            });
            producers.Add(Task.Run(async () =>
            {
                try
                {
                    await feed.LoadTradesAsync(inst, fromUtc, toUtc, null, batch =>
                    {
                        if (cts.IsCancellationRequested) return false;
                        var copy = batch as List<Trade> ?? batch.ToList();
                        ch.Writer.WriteAsync(copy, cts.Token).AsTask().GetAwaiter().GetResult();
                        return !cts.IsCancellationRequested;
                    }, sub, cts.Token).ConfigureAwait(false);
                    ch.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    ch.Writer.TryComplete(parts.Count > 1 && ex is not OperationCanceledException ? null : ex);
                }
            }, cts.Token));
        }

        // fusión por tiempo: en cada paso sale la operación más vieja de todas las fuentes
        var heads = new IReadOnlyList<Trade>?[parts.Count];
        var idx = new int[parts.Count];
        var done = new bool[parts.Count];
        long lastSlot = 0;
        var srcIndex = parts.Select(p => SourceIndex(p.Feed)).ToArray();
        var merged = new List<Trade>(8192);
        async Task<bool> FillAsync(int i)
        {
            while (heads[i] == null || idx[i] >= heads[i]!.Count)
            {
                if (done[i]) return false;
                try
                {
                    if (!await channels[i].Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false) || !channels[i].Reader.TryRead(out var b))
                    {
                        done[i] = true;
                        return false;
                    }

                    heads[i] = b;
                    idx[i] = 0;
                }
                catch (ChannelClosedException)
                {
                    done[i] = true;
                    return false;
                }
            }

            return true;
        }

        try
        {
            while (!cts.IsCancellationRequested)
            {
                int best = -1;
                long bestTicks = long.MaxValue;
                for (int i = 0; i < parts.Count; i++)
                {
                    if (!await FillAsync(i).ConfigureAwait(false)) continue;
                    long t = heads[i]![idx[i]].Time.Ticks;
                    if (t < bestTicks)
                    {
                        bestTicks = t;
                        best = i;
                    }
                }

                if (best < 0) break;
                var tr = heads[best]![idx[best]++];
                merged.Add(new Trade(TaggedId(tr.Time, srcIndex[best], ref lastSlot), tr.Time, instrument.RoundPrice(tr.Price), tr.Volume, tr.Side));
                if (merged.Count >= 8192)
                {
                    if (!onBatch(merged))
                    {
                        cts.Cancel();
                        break;
                    }

                    merged = new List<Trade>(8192);
                }
            }

            if (merged.Count > 0 && !cts.IsCancellationRequested) onBatch(merged);
        }
        finally
        {
            cts.Cancel();
        }

        try
        {
            await Task.WhenAll(producers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        progress?.Report(1);
    }

    // ===================================================================== velas

    public async Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct)
    {
        var parts = await PartsAsync(instrument, ct).ConfigureAwait(false);
        DateTime? first = null;
        foreach (var (feed, inst) in parts)
            if (feed is IKlineHistory k)
            {
                var f = await k.FirstAvailableAsync(inst, ct).ConfigureAwait(false);
                if (f != null && (first == null || f < first)) first = f;
            }

        return first;
    }

    public string CacheTag => "agg-" + string.Join("-", _sources.Select(s => s.Id));

    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        var parts = await PartsAsync(instrument, ct).ConfigureAwait(false);
        var sources = parts.Where(p => p.Feed is IKlineHistory).ToList();
        if (sources.Count == 0) return;
        var all = new SortedDictionary<long, Kline>();
        for (int i = 0; i < sources.Count; i++)
        {
            var (feed, inst) = sources[i];
            int index = i;
            var sub = new Progress<double>(p => progress?.Report((index + Math.Clamp(p, 0, 1)) / sources.Count));
            try
            {
                await ((IKlineHistory)feed).LoadKlinesAsync(inst, fromUtc, toUtc, minutes, endPrice, batch =>
                {
                    foreach (var k in batch) Merge(all, k, i == 0);
                    return !ct.IsCancellationRequested;
                }, sub, ct).ConfigureAwait(false);
            }
            catch (Exception) when (sources.Count > 1 && i > 0 && !ct.IsCancellationRequested)
            {
                // un mercado que no respondió no deja sin velas al agregado
            }
        }

        var list = all.Values.ToList();
        for (int i = 0; i < list.Count; i += 5000)
            if (!onBatch(list.GetRange(i, Math.Min(5000, list.Count - i)))) return;
        progress?.Report(1);
    }

    /// <summary>Suma una vela a la del mismo minuto: volumen y volumen comprador se suman; el OHLC combina los extremos.</summary>
    public static void Merge(SortedDictionary<long, Kline> all, Kline k, bool primary)
    {
        decimal buy = k.HasBuyVolume ? k.BuyVolume : KlineHistory.EstimateBuyVolume(k);
        if (!all.TryGetValue(k.OpenTimeMs, out var cur))
        {
            all[k.OpenTimeMs] = new Kline(k.OpenTimeMs, k.Open, k.High, k.Low, k.Close, k.Volume, buy, true);
            return;
        }

        decimal curBuy = cur.HasBuyVolume ? cur.BuyVolume : KlineHistory.EstimateBuyVolume(cur);
        all[k.OpenTimeMs] = new Kline(k.OpenTimeMs, primary ? k.Open : cur.Open, Math.Max(cur.High, k.High), Math.Min(cur.Low, k.Low), primary ? k.Close : cur.Close,
            cur.Volume + k.Volume, curBuy + buy, true);
    }

    // ===================================================================== estadísticas

    public async Task<IReadOnlyDictionary<string, TickerStats>> GetTickerStatsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, TickerStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _sources)
        {
            if (s is not ITickerStatsProvider p) continue;
            IReadOnlyDictionary<string, TickerStats> stats;
            try
            {
                stats = await p.GetTickerStatsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (result.Count > 0)
            {
                continue;
            }

            foreach (var (sym, st) in stats)
                result[sym] = result.TryGetValue(sym, out var cur)
                    ? cur with { QuoteVolume = cur.QuoteVolume + st.QuoteVolume, High = Math.Max(cur.High, st.High), Low = Math.Min(cur.Low, st.Low) }
                    : st;
        }

        return result;
    }

    // ===================================================================== en vivo

    private sealed class Live : IDisposable
    {
        public readonly List<IDisposable> Inner = new();
        public long LastTicks;

        /// <summary>Último precio del primer mercado (para dejar afuera un mercado cuyo precio no coincide: otra moneda con el mismo nombre).</summary>
        public double RefPrice;
        public bool Disposed;
        public DepthSnapshot?[] Books = Array.Empty<DepthSnapshot?>();
        public long LastBookEmit;

        public void Dispose()
        {
            List<IDisposable> inner;
            lock (this)
            {
                Disposed = true;
                inner = new List<IDisposable>(Inner);
                Inner.Clear();
            }

            foreach (var d in inner)
            {
                try
                {
                    d.Dispose();
                }
                catch
                {
                    // ya cerrada
                }
            }
        }
    }

    private sealed class PartSink : IMarketDataSink
    {
        private readonly AggregateFeed _owner;
        private readonly Live _live;
        private readonly Instrument _instrument;
        private readonly IMarketDataSink _sink;
        private readonly string _name;
        private readonly bool _depth;
        private readonly Instrument? _source;
        private readonly int _index;
        private readonly int _sourceIndex;

        public PartSink(AggregateFeed owner, Live live, Instrument instrument, IMarketDataSink sink, string name, bool depth, Instrument? source = null, int index = 0, int sourceIndex = 0)
        {
            _sourceIndex = sourceIndex;
            _owner = owner;
            _live = live;
            _instrument = instrument;
            _sink = sink;
            _name = name;
            _depth = depth;
            _source = source;
            _index = index;
        }

        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            var list = new List<Trade>(trades.Count);
            if (trades.Count > 0)
            {
                double px = (double)trades[^1].Price;
                if (_index == 0) Volatile.Write(ref _live.RefPrice, px);
                else if (Mismatch(px)) return;
            }

            foreach (var t in trades)
            {
                // tiempo que nunca retrocede (los relojes de los mercados difieren unos milisegundos)
                long ticks = Math.Max(t.Time.Ticks, Volatile.Read(ref _live.LastTicks));
                Volatile.Write(ref _live.LastTicks, ticks);
                var time = new DateTime(ticks, DateTimeKind.Utc);
                list.Add(new Trade(_owner.NextLiveId(time, _sourceIndex), time, _instrument.RoundPrice(t.Price), t.Volume, t.Side));
            }

            if (list.Count > 0) _sink.OnTrades(_instrument, list);
        }

        public void OnDepth(Instrument instrument, DepthUpdate update)
        {
            if (!_owner.AggregateBook || _source == null)
            {
                if (_depth) _sink.OnDepth(_instrument, update);
                return;
            }

            // libro sumado: el de este mercado (pasado a los ticks del agregado) se guarda y cada ~200 ms sale la suma
            var snap = update.Snapshot;
            if (_index > 0 && snap.Bids.Length > 0 && Mismatch((double)_source.FromTicks(snap.Bids[0].PriceTicks))) return;
            var conv = new DepthSnapshot(Convert(snap.Bids), Convert(snap.Asks), snap.Time);
            DepthSnapshot? merged = null;
            lock (_live)
            {
                if (_index < _live.Books.Length) _live.Books[_index] = conv;
                long now = DateTime.UtcNow.Ticks;
                if (now - _live.LastBookEmit >= TimeSpan.TicksPerMillisecond * 200)
                {
                    _live.LastBookEmit = now;
                    merged = MergeBooks(_live.Books.Where(b => b != null)!, snap.Time);
                }
            }

            if (merged != null) _sink.OnDepth(_instrument, new DepthUpdate { Snapshot = merged, IsFullRefresh = true });
        }

        /// <summary>El precio de este mercado se aleja más de 3 % del primero (no es la misma moneda).</summary>
        private bool Mismatch(double px)
        {
            double r = Volatile.Read(ref _live.RefPrice);
            return r > 0 && px > 0 && Math.Abs(px / r - 1) > 0.03;
        }

        private BookLevel[] Convert(BookLevel[] levels)
        {
            if (_source == null || _source.TickSize == _instrument.TickSize) return levels;
            var res = new BookLevel[levels.Length];
            for (int i = 0; i < levels.Length; i++)
                res[i] = new BookLevel(_instrument.ToTicks(_instrument.RoundPrice(_source.FromTicks(levels[i].PriceTicks))), levels[i].Size);
            return res;
        }

        public void OnStatus(Instrument instrument, FeedStatus status, string? message) =>
            _sink.OnStatus(_instrument, status, message == null ? null : _name + ": " + message);
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var live = new Live();
        _ = Task.Run(async () =>
        {
            List<(IMarketDataFeed Feed, Instrument Instrument)> parts;
            try
            {
                parts = await PartsAsync(instrument, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sink.OnStatus(instrument, FeedStatus.Error, ex.Message);
                return;
            }

            lock (live) live.Books = new DepthSnapshot?[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                var (feed, inst) = parts[i];
                var inner = feed.Subscribe(inst, new PartSink(this, live, instrument, sink, feed.DisplayName, depth: i == 0, inst, i, SourceIndex(feed)));
                bool late;
                lock (live)
                {
                    late = live.Disposed;
                    if (!late) live.Inner.Add(inner);
                }

                if (late) inner.Dispose();
            }
        });
        return live;
    }

    public void Dispose()
    {
        // los mercados de origen los administra quien los creó
    }
}
