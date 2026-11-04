using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Fotos del libro de órdenes: muestreo, disco, mapa de calor y el indicador de delta del libro.</summary>
public class LiquidityTests
{
    private static DepthSnapshot Book(DateTime t, long bestBid, decimal bidSize, decimal askSize, int levels = 10)
    {
        var bids = new BookLevel[levels];
        var asks = new BookLevel[levels];
        for (int i = 0; i < levels; i++)
        {
            bids[i] = new BookLevel(bestBid - i, bidSize * (1 + i % 3));
            asks[i] = new BookLevel(bestBid + 1 + i, askSize * (1 + i % 2));
        }

        return new DepthSnapshot(bids, asks, t);
    }

    [Fact]
    public void SamplesEveryIntervalAndSurvivesDisk()
    {
        var inst = new Instrument { Symbol = "BTCUSDT", FeedId = "test", TickSize = 0.1m };
        var oldRoot = LiquidityHistory.Root;
        var dir = Path.Combine(Path.GetTempPath(), "kvp-liq-" + Guid.NewGuid().ToString("N"));
        LiquidityHistory.Root = dir;
        try
        {
            LiquidityHistory.SampleSeconds = 2;
            var liq = new LiquidityHistory(inst);
            var t0 = DateTime.UtcNow.AddMinutes(-10);
            for (int i = 0; i < 20; i++) liq.Sample(Book(t0.AddSeconds(i), 700000 + i, 2m, 3m), persist: true);
            // una por cada 2 segundos
            Assert.Equal(10, liq.Count);
            var last = liq.Latest!;
            Assert.Equal(700018, last.BestBid);
            Assert.Equal(700019, last.BestAsk);
            Assert.Equal(0, last.BidOffsets[0]);
            Assert.Equal(2, last.BidOffsets[2]);
            Assert.Equal(2f, last.BidSizes[0]);
            var (b, a) = last.Within(1);
            Assert.Equal(2 + 4, b); // dos niveles: 2 y 2×2
            Assert.Equal(3 + 6, a);
            liq.Close();

            var path = LiquidityHistory.DayPath(dir, "test", "BTCUSDT", DateOnly.FromDateTime(t0));
            Assert.True(File.Exists(path));
            var read = LiquidityHistory.ReadFile(path);
            Assert.Equal(10, read.Count);
            Assert.Equal(last.Time, read[^1].Time);
            Assert.Equal(last.AskSizes, read[^1].AskSizes);

            // al abrir de nuevo, se carga lo guardado
            var again = new LiquidityHistory(inst);
            again.Load();
            Assert.Equal(10, again.Count);
            Assert.Single(LiquidityHistory.AvailableDays(dir, "test", "BTCUSDT"));
        }
        finally
        {
            LiquidityHistory.Root = oldRoot;
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void HeatmapAndBookDeltaRender()
    {
        var oldRoot = LiquidityHistory.Root;
        LiquidityHistory.Root = null;
        try
        {
            var data = VpUltimateTests.Market(2);
            // fotos del libro durante las últimas 6 horas: más bid que ask, y un muro grande en el ask a 15 ticks
            var trades = data.Trades;
            var end = trades.GetTime(trades.Count - 1);
            for (var t = end.AddHours(-6); t < end; t = t.AddSeconds(5))
            {
                // precio aproximado en ese momento: la última operación anterior
                int idx = 0;
                for (int lo = 0, hi = trades.Count - 1; lo <= hi;)
                {
                    int mid = (lo + hi) >> 1;
                    if (trades.GetTime(mid) <= t)
                    {
                        idx = mid;
                        lo = mid + 1;
                    }
                    else hi = mid - 1;
                }

                long px = trades.GetPriceTicks(idx);
                // un libro ralo (un nivel cada 10 ticks, 100 niveles) con un muro en el ask a 150 ticks
                var bids = Enumerable.Range(0, 100).Select(i => new BookLevel(px - i * 10, 3m + i % 4)).ToArray();
                var asks = Enumerable.Range(0, 100).Select(i => new BookLevel(px + 1 + i * 10, i == 15 ? 80m : 1.5m + i % 3)).ToArray();
                data.Liquidity.Sample(new DepthSnapshot(bids, asks, t), persist: false);
            }

            Assert.True(data.Liquidity.Count > 1000);
            var model = new ChartModel(data, ChartPeriod.Parse("M5"), new ChartSettings { TimeZoneId = "UTC", ShowLiquidityHeatmap = true, Style = ChartStyle.Candles });
            var ind = new OrderbookDelta { WithinTicks = 200 };
            model.AddIndicator(ind);
            // los del libro estilo Kiyotaka: cambios (agregado / retirado), órdenes grandes y profundidad 0–1 %
            var pct = new OrderbookDelta { Range = OrderbookDelta.DepthRange.Pct1 };
            model.AddIndicator(new OrderBookChanges { Window = OrderBookChanges.ChangeWindow.Hour4 });
            model.AddIndicator(new LargeBookOrders());
            model.AddIndicator(pct);
            for (int k = 0; k < 3; k++) model.Update();
            Assert.NotEqual(0, ((ValueDataSeries)pct.DataSeries[0])[model.Series.Count - 2]);
            var s = (ValueDataSeries)ind.DataSeries[0];
            // en las velas con fotos el bid supera al ask (salvo el muro, que está a 15 ticks: dentro de 20)
            int withData = 0;
            for (int i = 0; i < model.Series.Count; i++)
                if (model.GetDepthSamples(i).Count > 0) withData++;
            Assert.True(withData > 50);
            int lastBar = model.Series.Count - 2;
            Assert.NotEqual(0, s[lastBar]);
            var (bid, ask, n) = OrderbookDelta.Measure(model.GetDepthSamples(lastBar), 10, OrderbookDelta.BookAggregation.Average);
            Assert.True(n > 0);
            Assert.True(bid > ask, $"bid {bid} ask {ask}"); // sin el muro (a 15 ticks) el bid pesa más
            var (bidW, askW, _) = OrderbookDelta.Measure(model.GetDepthSamples(lastBar), 200, OrderbookDelta.BookAggregation.Average);
            Assert.True(askW > bidW, $"bid {bidW} ask {askW}"); // con el muro adentro, el ask pesa más

            model.Viewport.BarWidth = 14;
            using var surface = SKSurface.Create(new SKImageInfo(1400, 800));
            new ChartRenderer().Render(surface.Canvas, model, 1400, 800, new ChartRenderState());
            // el mapa pintó algo con la paleta (celeste / amarillo) en la zona del gráfico
            using var img = surface.Snapshot();
            using var bmp = SKBitmap.FromImage(img);
            int colored = 0;
            for (int y = 100; y < 500; y += 4)
                for (int x = 300; x < 1200; x += 4)
                {
                    var c = bmp.GetPixel(x, y);
                    bool blue = c.Blue > 60 && c.Blue > c.Red + 30 && c.Blue > c.Green + 10;
                    bool yellow = c.Red > 150 && c.Green > 140 && c.Blue < c.Green - 40;
                    if (blue || yellow) colored++;
                }

            var dir = Environment.GetEnvironmentVariable("TH_SNAPSHOT_DIR");
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                using var png = img.Encode(SKEncodedImageFormat.Png, 90);
                File.WriteAllBytes(Path.Combine(dir, "liquidity-heatmap.png"), png.ToArray());
            }

            Assert.True(colored > 50, "el mapa de calor no se ve");
            Assert.All(model.Indicators, sl => Assert.Null(sl.Error));
        }
        finally
        {
            LiquidityHistory.Root = oldRoot;
        }
    }
}
