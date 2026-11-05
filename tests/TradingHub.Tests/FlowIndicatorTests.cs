using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native.Flow;
using TradingHub.Market;
using Xunit;

namespace TradingHub.Tests;

/// <summary>La columna de flujo por exchange, la tira de estadísticas y NL/NS (de LiquidityMapperFast).</summary>
public class FlowIndicatorTests
{
    private static (ChartModel Model, InstrumentData Data) Market()
    {
        var inst = new Instrument { Symbol = "BTCUSDT", FeedId = AggregateFeed.FeedId, TickSize = 0.1m, VolumeStep = 0.001m };
        var feed = new AggregateFeed(new IMarketDataFeed[] { new SimulatedFeed(), new SimulatedFeed(), new SimulatedFeed() });
        var data = new InstrumentData(inst, feed);
        var t0 = DateTime.UtcNow.AddHours(-10);
        long slot = 0;
        var rnd = new Random(5);
        decimal px = 80000;
        for (int i = 0; i < 20000; i++)
        {
            var t = t0.AddSeconds(i * 1.8);
            int src = i % 3;
            bool buy = src switch { 0 => rnd.Next(10) < 7, 1 => rnd.Next(10) < 3, _ => rnd.Next(2) == 0 };
            px += buy ? 0.5m : -0.5m;
            decimal v = rnd.Next(20) == 0 ? 10 : 0.05m * (1 + rnd.Next(10)); // algunas ballenas (~$800k)
            data.Trades.Add(new Trade(AggregateFeed.TaggedId(t, src, ref slot), t, px, v, buy ? TradeSide.Buy : TradeSide.Sell));
        }

        var model = new ChartModel(data, ChartPeriod.Parse("M5"), new ChartSettings { TimeZoneId = "UTC", Style = ChartStyle.Candles });
        return (model, data);
    }

    [Fact]
    public void Los_tamanios_y_la_columna_de_flujo_se_arman_y_se_dibujan()
    {
        Assert.Equal(0, FlowBar.Bucket(1000));
        Assert.Equal(2, FlowBar.Bucket(80_000));
        Assert.Equal(7, FlowBar.Bucket(9_000_000));

        var (model, _) = Market();
        var flow = new FlowByExchange { Ranking = FlowByExchange.RankWindow.Visible };
        var stats = new BarStatsStrip { DeltaPct = true, Trades = true, AvgTrade = true, DeltaRetail = true, DeltaWhales = true, CvdWhales = true, Imbalances = true, Stacked = true, Poc = true, Range = true };
        model.AddIndicator(flow);
        model.AddIndicator(stats);
        model.AddIndicator(new PositioningNlNs());
        model.Update();
        model.Viewport.BarWidth = 30;
        using var surface = SKSurface.Create(new SKImageInfo(1400, 1000));
        new ChartRenderer().Render(surface.Canvas, model, 1400, 1000, new ChartRenderState());
        using var img = surface.Snapshot();
        var dir = Environment.GetEnvironmentVariable("KVP_SNAPSHOT_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            using var png = img.Encode(SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(Path.Combine(dir, "flow.png"), png.ToArray());
        }

        // la columna dibujó las líneas de spot / perpetuo (ámbar) en su panel
        using var bmp = SKBitmap.FromImage(img);
        int amber = 0;
        for (int y = 0; y < 1000; y += 2)
            for (int x = 200; x < 1300; x += 2)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > 200 && c.Green > 140 && c.Green < 200 && c.Blue < 90) amber++;
            }

        Assert.True(amber > 50, $"ámbar: {amber}");
    }
}
