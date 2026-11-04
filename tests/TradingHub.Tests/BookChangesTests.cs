using TradingHub.Data;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Order Book Changes: agregado / retirado / operado por precio, y órdenes grandes (spoof y chasing).</summary>
public class BookChangesTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static DepthSample S(int sec, long bid, (int Off, float Size)[] bids, long ask, (int Off, float Size)[] asks) =>
        new(T0.AddSeconds(sec), bid, ask, bids.Select(b => b.Off).ToArray(), bids.Select(b => b.Size).ToArray(), asks.Select(a => a.Off).ToArray(), asks.Select(a => a.Size).ToArray());

    [Fact]
    public void Separa_lo_agregado_lo_retirado_y_lo_operado()
    {
        var a = S(0, 100, new[] { (0, 5f), (1, 3f) }, 101, new[] { (0, 4f) });
        var b = S(3, 100, new[] { (0, 2f), (1, 6f) }, 101, new[] { (0, 4f), (1, 10f) });
        var res = BookChanges.ForBar(new[] { b }, a, p => p == 100 ? (1, 0) : (0, 0));
        Assert.Equal(1, res[100].BidFilled, 6);
        Assert.Equal(2, res[100].BidPulled, 6);
        Assert.Equal(3, res[99].BidAdded, 6);
        // 102 no se veía en la foto anterior: no cuenta como agregado
        Assert.False(res.ContainsKey(102) && res[102].AskAdded > 0);
        // agrupado de a 10 ticks
        var g = BookChanges.ForBar(new[] { b }, a, _ => (0, 0), 10);
        Assert.Equal(3, g[90].BidAdded, 6);
        Assert.Equal(3, g[100].BidPulled, 6);
    }

    [Fact]
    public void Ordenes_grandes_spoof_y_chasing()
    {
        var samples = new[]
        {
            S(0, 100, new[] { (0, 50f), (1, 1f) }, 101, new[] { (0, 1f), (4, 60f) }),
            S(3, 100, new[] { (0, 50f), (1, 1f) }, 101, new[] { (0, 1f), (4, 60f) }),
            // el bid grande sube con el precio (chasing) y el ask grande desaparece sin operarse (spoof)
            S(6, 101, new[] { (0, 52f), (1, 1f), (2, 1f) }, 102, new[] { (0, 1f), (3, 1f), (5, 1f) }),
        };
        var orders = BookChanges.TrackLarge(samples, 20, (_, _, _) => 0);
        var chase = orders.Single(o => o.IsBid && o.Price == 100);
        Assert.True(chase.Pulled);
        Assert.Equal(101, chase.MovedTo);
        var spoof = orders.Single(o => !o.IsBid && o.Price == 105);
        Assert.True(spoof.Pulled);
        Assert.Null(spoof.MovedTo);
        Assert.Equal(6, (spoof.To - spoof.From).TotalSeconds);
        // si se operó, no es spoof
        var filled = BookChanges.TrackLarge(samples, 20, (p, _, _) => p == 105 ? 60 : 0);
        Assert.False(filled.Single(o => !o.IsBid && o.Price == 105).Pulled);
    }
}

public class AggregateBookTests
{
    [Fact]
    public void Los_libros_de_los_mercados_se_suman_precio_por_precio()
    {
        var a = new TradingHub.Market.DepthSnapshot(new[] { new TradingHub.Market.BookLevel(100, 1), new TradingHub.Market.BookLevel(99, 2) }, new[] { new TradingHub.Market.BookLevel(101, 3) }, DateTime.UtcNow);
        var b = new TradingHub.Market.DepthSnapshot(new[] { new TradingHub.Market.BookLevel(100, 4) }, new[] { new TradingHub.Market.BookLevel(102, 1), new TradingHub.Market.BookLevel(101, 1) }, DateTime.UtcNow);
        var m = TradingHub.Feeds.AggregateFeed.MergeBooks(new[] { a, b }, DateTime.UtcNow);
        Assert.Equal(new[] { 100L, 99L }, m.Bids.Select(l => l.PriceTicks));
        Assert.Equal(5m, m.Bids[0].Size);
        Assert.Equal(new[] { 101L, 102L }, m.Asks.Select(l => l.PriceTicks));
        Assert.Equal(4m, m.Asks[0].Size);
    }
}
