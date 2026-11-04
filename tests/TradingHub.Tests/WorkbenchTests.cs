using TradingHub.App.Services.Workbench;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Servidor local del Workbench: direcciones permitidas en el proxy y velas armadas desde la caché de KVP.</summary>
public class WorkbenchTests
{
    [Theory]
    [InlineData("https://fapi.binance.com/fapi/v1/klines", "symbol=BTCUSDT", true)]
    [InlineData("https:/api.bybit.com/v5/market/kline", "", true)]
    [InlineData("http://127.0.0.1:8080/x", "", false)]
    [InlineData("http://192.168.1.10/", "", false)]
    [InlineData("http://localhost/", "", false)]
    [InlineData("ftp://example.com/", "", false)]
    [InlineData("https://printer.local/", "", false)]
    public void Solo_direcciones_publicas(string raw, string query, bool ok)
    {
        var u = WorkbenchServer.PublicTarget(raw, query);
        Assert.Equal(ok, u != null);
        if (ok && query.Length > 0) Assert.Contains(query, u!.Query);
    }

    [Theory]
    [InlineData("1m", 1)]
    [InlineData("15m", 15)]
    [InlineData("4h", 240)]
    [InlineData("1d", 1440)]
    [InlineData("60", 60)]
    [InlineData("D", 1440)]
    [InlineData("x", 0)]
    public void Intervalos(string s, int minutes) => Assert.Equal(minutes, KlineBridge.Minutes(s));

    private sealed class FakeKlines : IKlineHistory
    {
        public int Calls;
        public int Minutes;

        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(null);

        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice, Func<IReadOnlyList<Kline>, bool> onBatch,
            IProgress<double>? progress, CancellationToken ct)
        {
            Calls++;
            Minutes = minutes;
            var list = new List<Kline>();
            long step = minutes * 60_000L;
            long t = new DateTimeOffset(fromUtc).ToUnixTimeMilliseconds() / step * step;
            for (; t < new DateTimeOffset(toUtc).ToUnixTimeMilliseconds(); t += step)
                list.Add(new Kline(t, 100, 101, 99, 100.5m, 2, 1.5m, true));
            onBatch(list);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Las_velas_de_una_hora_salen_de_las_de_15_minutos()
    {
        var old = KlineHistory.CacheRoot;
        KlineHistory.CacheRoot = null;
        try
        {
            var feed = new FakeKlines();
            var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            var bars = await KlineBridge.LoadAsync(feed, "BTCUSDT", 60, from, from.AddHours(5), CancellationToken.None);
            Assert.Equal(15, feed.Minutes);
            Assert.True(bars.Count >= 5);
            Assert.All(bars.Take(5), b => Assert.Equal(0, b.OpenTimeMs % 3_600_000));
            Assert.Equal(8m, bars[0].Volume); // 4 velas de 15 min × 2
            Assert.Equal(6m, bars[0].BuyVolume);
        }
        finally
        {
            KlineHistory.CacheRoot = old;
        }
    }
}
