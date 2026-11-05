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

    [Fact]
    public void Liquidity_tracker_pesa_por_distancia_y_los_niveles_de_liquidacion_se_van_con_el_precio()
    {
        // un nivel de $1M en el mejor bid y otro igual 10 bp más abajo: con vida media de 10 bp pesa la mitad
        long bb = 800000, ba = 800001; // ticks de 0.1 → 80000.0 / 80000.1
        var s = new DepthSample(DateTime.UtcNow, bb, ba, new[] { 0, 800 }, new[] { 12.5f, 12.5f }, new[] { 0 }, new[] { 12.5f });
        var (bid, ask) = LiquidityTracker.Measure(s, 0.1m, 10, 0, 0, false);
        Assert.InRange(bid, 1_000_000 * 1.49, 1_000_000 * 1.51);
        Assert.InRange(ask, 999_000, 1_001_000);
        var (onlyBig, _) = LiquidityTracker.Measure(s, 0.1m, 10, 2_000_000, 0, false);
        Assert.Equal(0, onlyBig);

        var json = """{"v":2,"coin":"BTC","attribution":"Powered by HyperTracker","snapshot":{"asOf":1793840368498},"mark":80500.0,"positions":3,"step":10.0,"bins":[[8000,100,50,0,0,2,0,120,0],[8040,0,0,300,0,0,1,0,300],[8100,0,0,700,100,0,2,0,600]]}""";
        var snap = LiquidationLevels.Parse(json)!;
        Assert.Equal(3, snap.Bins.Count);
        Assert.Equal(80000, snap.Bins[0].Price);
        Assert.Equal(150, snap.Bins[0].Long);
        // el precio bajó a 80005 (no llegó a la franja 80000-80010? sí: su techo 80010 está encima) y subió a 80450
        var left = LiquidationLevels.Remaining(snap, 80005, 80450);
        Assert.DoesNotContain(left, b => b.Price == 80000);
        Assert.DoesNotContain(left, b => b.Price == 80400);
        Assert.Contains(left, b => b.Price == 81000 && b.Short == 800);

        var book = new[] { (80000m, 1m), (79990m, 2m), (80010m, 0.5m) };
        Assert.Equal(3, BookMirror.Sum(book, 79980, 80000).Coins);
    }

    [Fact]
    public void Absorcion_atrapados_y_destacados()
    {
        var t0 = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        var trades = new List<TradingHub.Indicators.BarTrade>();
        // 30 ventas de 0,2 BTC en 80000 dentro de 6 ms: $480k absorbidos por el bid; y compras sueltas
        for (int i = 0; i < 30; i++) trades.Add(new(i, t0.AddTicks(i * 2000), 80000m, 0.2m, false, 0));
        for (int i = 0; i < 10; i++) trades.Add(new(100 + i, t0.AddSeconds(1 + i), 80001m, 0.1m, true, 0));
        var marks = AbsorptionMarks.Find(trades.OrderBy(t => t.Time).ToList(), 10, 250_000);
        Assert.Single(marks);
        Assert.False(marks[0].AggressorBuy);
        Assert.InRange(marks[0].Usd, 470_000, 490_000);

        // mecha de arriba con compras netas, cuerpo con ventas
        var levels = new List<TradingHub.Indicators.PriceVolumeInfo>
        {
            new(100) { Bid = 5, Ask = 1 }, new(101) { Bid = 5, Ask = 1 }, new(102) { Bid = 4, Ask = 2 },
            new(103) { Bid = 1, Ask = 9 }, new(104) { Bid = 1, Ask = 8 },
        };
        var (net, avg, other) = TrappedTraders.Wick(levels, 103, 104, true);
        Assert.Equal(15, net);
        Assert.True(net > other);
        Assert.InRange(avg, 103m, 104m);

        // regla k·σ: un valor 10 veces lo normal se destaca
        var vals = Enumerable.Range(0, 80).Select(i => i == 79 ? 100.0 : 10 + i % 3).ToArray();
        Assert.True(vals[79] > StandOutRule.Threshold(i => vals[i], 79, 72, 2));
        Assert.Null(StandOutRule.Threshold(i => vals[i], 5, 72, 2));

        // se dibujan sin fallar
        var (model, _) = Market();
        model.AddIndicator(new AbsorptionMarks { ThresholdUsd = 50_000 });
        model.AddIndicator(new TrappedTraders());
        model.AddIndicator(new HighlightsAndSounds());
        model.AddIndicator(new LiquidityTracker());
        model.AddIndicator(new BookMirror());
        model.Update();
        model.Viewport.BarWidth = 20;
        using var surface = SKSurface.Create(new SKImageInfo(1200, 800));
        new ChartRenderer().Render(surface.Canvas, model, 1200, 800, new ChartRenderState());
    }
}
