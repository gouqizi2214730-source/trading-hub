using SkiaSharp;
using TradingHub.Charting.Dom;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using Xunit;

namespace TradingHub.Tests;

/// <summary>El DOM nuevo (solo lectura): tick automático, unidad en USD, cambios del libro y columnas por defecto.</summary>
public class DomTests
{
    [Fact]
    public void Tick_automatico_unidad_usd_y_columnas_nuevas()
    {
        // ±0,5 % de 80.000 en 40 filas: 800 / 40 = 20 $ por fila → 200 ticks de 0,1 → paso «lindo» 200
        Assert.Equal(200, DomModel.AutoStepFor(80000, 0.1m, 40, 0.5));
        Assert.Equal(1, DomModel.AutoStepFor(0, 0.1m, 40, 0.5));
        // el libro llega solo $30 de cada lado: las filas lo abarcan a él (60 / 40 = 1,5 $ → 15 ticks → 20)
        Assert.Equal(20, DomModel.AutoStepFor(80000, 0.1m, 40, 0.5, 30));
        Assert.Equal("$80K", DomRenderer.Amount(1, 80000, DomUnit.Usd, 3));
        Assert.Equal("$1.6M", DomRenderer.Amount(20, 80000, DomUnit.Usd, 3));

        // una configuración vieja (con columnas de órdenes) pasa a las columnas nuevas, sin órdenes
        var old = new DomSettings { Layout = 0, Columns = new() { new() { Column = DomColumn.BuyOrders }, new() { Column = DomColumn.Bid }, new() { Column = DomColumn.Price } } };
        old.Normalize();
        Assert.DoesNotContain(old.Columns, c => c.Column is DomColumn.BuyOrders or DomColumn.SellOrders);
        Assert.Contains(old.Columns, c => c.Column == DomColumn.Size && c.Visible);
        Assert.Contains(old.Columns, c => c.Column == DomColumn.Changes && c.Visible);
        Assert.Equal(new[] { 0.5, 1, 2.5, 5 }, old.Marks());
    }

    [Fact]
    public void Se_dibuja_con_libro_y_operaciones()
    {
        var inst = new Instrument { Symbol = "BTCUSDT", FeedId = "sim", TickSize = 0.1m, VolumeStep = 0.001m };
        var data = new InstrumentData(inst, new SimulatedFeed());
        var t0 = DateTime.UtcNow.AddMinutes(-3);
        var rnd = new Random(2);
        for (int i = 0; i < 2000; i++)
            data.Trades.Add(new Trade(i + 1, t0.AddMilliseconds(i * 90), 80000m + rnd.Next(-30, 30) * 0.1m, 0.01m * rnd.Next(1, 20), rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell));
        long px = inst.ToTicks(80000m);
        var bids = Enumerable.Range(0, 300).Select(i => new BookLevel(px - 1 - i, 0.5m + i % 7)).ToArray();
        var asks = Enumerable.Range(0, 300).Select(i => new BookLevel(px + i, i == 40 ? 60m : 0.4m + i % 5)).ToArray();
        ((IMarketDataSink)data).OnDepth(inst, new DepthUpdate { Snapshot = new DepthSnapshot(bids, asks, DateTime.UtcNow), IsFullRefresh = true });
        data.Pump();
        Assert.True(data.Depth.HasBid);

        foreach (var unit in new[] { DomUnit.Quantity, DomUnit.Usd })
        {
            var settings = new DomSettings { Unit = unit };
            settings.Normalize();
            var dom = new DomModel(data, settings);
            var r = new DomRenderer();
            using var surface = SKSurface.Create(new SKImageInfo(420, 700));
            r.Render(surface.Canvas, dom, 420, 700, 300);
            Assert.True(dom.Step > 1); // el tick automático agrupó
            Assert.True(r.HeaderHeight > 20); // con la barra de estadísticas
            var dir = Environment.GetEnvironmentVariable("KVP_SNAPSHOT_DIR");
            if (!string.IsNullOrEmpty(dir))
            {
                using var img = surface.Snapshot();
                using var png = img.Encode(SKEncodedImageFormat.Png, 90);
                File.WriteAllBytes(Path.Combine(dir, $"dom-{unit}.png"), png.ToArray());
            }
        }
    }
}
