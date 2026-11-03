using TradingHub.Feeds;
using TradingHub.Market;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Cripto agregado: operaciones fusionadas por tiempo, velas sumadas y suscripción a todos los mercados.</summary>
public class AggregateFeedTests
{
    private sealed class FakeFeed : IMarketDataFeed, IKlineHistory
    {
        private readonly List<Trade> _trades;
        private readonly List<Kline> _klines;
        public readonly List<Instrument> Instruments;
        public int Subscriptions;

        public FakeFeed(string id, string[] symbols, List<Trade> trades, List<Kline> klines, decimal tick = 0.1m)
        {
            Id = id;
            _trades = trades;
            _klines = klines;
            Instruments = symbols.Select(s => new Instrument { Symbol = s, FeedId = id, TickSize = tick, BaseAsset = s[..3], QuoteAsset = "USDT" }).ToList();
        }

        public string Id { get; }
        public string DisplayName => Id;
        public bool SupportsHistory => true;
        public bool IsLive => true;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(Instruments);

        public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            // en dos lotes, para probar la fusión en streaming
            int half = _trades.Count / 2;
            onBatch(_trades.Take(half).ToList());
            await Task.Delay(10, ct);
            onBatch(_trades.Skip(half).ToList());
        }

        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            Subscriptions++;
            sink.OnTrades(instrument, new[] { new Trade(1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m, 1, TradeSide.Buy) });
            return new Sub(this);
        }

        private sealed class Sub : IDisposable
        {
            private readonly FakeFeed _f;
            public Sub(FakeFeed f) => _f = f;
            public void Dispose() => _f.Subscriptions--;
        }

        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice, Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            onBatch(_klines);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private static DateTime T(int sec) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(sec);

    [Fact]
    public async Task MergesTradesByTimeAndSumsKlines()
    {
        var a = new FakeFeed("binance-futures", new[] { "BTCUSDT", "ETHUSDT", "SOLOA" },
            new List<Trade> { new(1, T(0), 100.0m, 1, TradeSide.Buy), new(2, T(2), 100.2m, 2, TradeSide.Sell), new(3, T(4), 100.4m, 1, TradeSide.Buy), new(4, T(6), 100.6m, 1, TradeSide.Buy) },
            new List<Kline> { new(0, 100, 101, 99, 100.5m, 10, 6, true), new(60000, 100.5m, 102, 100, 101, 20, 5, true) });
        var b = new FakeFeed("bybit-linear", new[] { "BTCUSDT", "ETHUSDT", "SOLOB" },
            new List<Trade> { new(1, T(1), 100.05m, 3, TradeSide.Sell), new(2, T(3), 100.25m, 1, TradeSide.Buy), new(3, T(5), 100.45m, 2, TradeSide.Sell), new(4, T(7), 100.65m, 2, TradeSide.Buy) },
            new List<Kline> { new(0, 100.1m, 100.9m, 99.5m, 100.4m, 4, 0, false), new(120000, 101, 101.5m, 100.8m, 101.2m, 8, 0, false) }, tick: 0.05m);
        var agg = new AggregateFeed(new IMarketDataFeed[] { a, b });
        var instruments = await agg.GetInstrumentsAsync(CancellationToken.None);
        Assert.Equal(new[] { "BTCUSDT", "ETHUSDT" }, instruments.Select(i => i.Symbol)); // solo los que están en los dos
        var btc = instruments[0];
        Assert.Equal(AggregateFeed.FeedId, btc.FeedId);
        Assert.Equal(0.05m, btc.TickSize);

        var merged = new List<Trade>();
        await agg.LoadTradesAsync(btc, T(-10), T(100), null, batch =>
        {
            merged.AddRange(batch);
            return true;
        }, null, CancellationToken.None);
        Assert.Equal(8, merged.Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => T(i)), merged.Select(t => t.Time)); // intercaladas por tiempo
        Assert.True(merged.Zip(merged.Skip(1)).All(p => p.Second.Id > p.First.Id)); // ids crecientes
        Assert.Equal(13, merged.Sum(t => t.Volume));

        var klines = new List<Kline>();
        await agg.LoadKlinesAsync(btc, T(0), T(600), 1, 0, batch =>
        {
            klines.AddRange(batch);
            return true;
        }, null, CancellationToken.None);
        Assert.Equal(3, klines.Count);
        var first = klines[0];
        Assert.Equal(14, first.Volume); // 10 + 4
        Assert.Equal(101, first.High);
        Assert.Equal(99, first.Low);
        Assert.Equal(100, first.Open); // el del primer mercado
        Assert.True(first.HasBuyVolume);
        Assert.Equal(6 + Data.KlineHistory.EstimateBuyVolume(new Kline(0, 100.1m, 100.9m, 99.5m, 100.4m, 4, 0, false)), first.BuyVolume);
        Assert.Equal(20, klines[1].Volume);
        Assert.Equal(8, klines[2].Volume);

        var sink = new CountingSink();
        using (agg.Subscribe(btc, sink))
        {
            await Task.Delay(200);
            Assert.Equal(1, a.Subscriptions);
            Assert.Equal(1, b.Subscriptions);
            Assert.Equal(2, sink.Trades);
        }

        await Task.Delay(50);
        Assert.Equal(0, a.Subscriptions);
        Assert.Equal(0, b.Subscriptions);
    }

    private sealed class CountingSink : IMarketDataSink
    {
        public int Trades;
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) => Interlocked.Add(ref Trades, trades.Count);
        public void OnDepth(Instrument instrument, DepthUpdate update) { }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { }
    }
}
