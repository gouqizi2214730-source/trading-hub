using System.Collections;
using System.Reflection;
using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using Xunit;

namespace TradingHub.Tests;

public class VolumeProfileRowsTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = "test", TickSize = 0.1m, VolumeStep = 0.001m };
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static (ChartModel Model, SessionVolumeProfile Profile) Chart(Action<SessionVolumeProfile> setup)
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        long id = 1;
        // 120 velas de 1 minuto, precios entre 100,0 y 139,9 (400 ticks)
        for (int i = 0; i < 120; i++)
            for (int j = 0; j < 10; j++)
                data.Trades.Add(new Trade(id++, T0.AddMinutes(i).AddSeconds(j * 5), 100m + ((i * 37 + j * 13) % 400) * 0.1m, 1, j % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var profile = new SessionVolumeProfile { Period = SessionVolumeProfile.ProfilePeriod.Visible };
        setup(profile);
        model.AddIndicator(profile);
        for (int k = 0; k < 3; k++) model.Update();
        // se dibuja sin errores
        using var surface = SKSurface.Create(new SKImageInfo(900, 500));
        new ChartRenderer().Render(surface.Canvas, model, 900, 500, new ChartRenderState());
        return (model, profile);
    }

    private static (int Rows, decimal RowSize, decimal Total) Last(SessionVolumeProfile p)
    {
        var list = (IList)typeof(SessionVolumeProfile).GetField("_profiles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;
        var last = list[list.Count - 1]!;
        var t = last.GetType();
        var rows = (IDictionary)t.GetField("Rows")!.GetValue(last)!;
        return (rows.Count, (decimal)t.GetField("RowSize")!.GetValue(last)!, (decimal)t.GetField("Total")!.GetValue(last)!);
    }

    [Fact]
    public void Por_defecto_una_fila_por_tick()
    {
        var (_, p) = Chart(_ => { });
        var (rows, size, total) = Last(p);
        Assert.Equal(0.1m, size);
        Assert.True(rows > 100, $"filas: {rows}");
        Assert.Equal(1200m, total);
    }

    [Fact]
    public void Ticks_por_fila_junta_los_niveles()
    {
        var (_, p) = Chart(x => x.TicksPerRow = 50);
        var (rows, size, total) = Last(p);
        Assert.Equal(5m, size);
        Assert.InRange(rows, 7, 9); // 40 puntos de rango en filas de 5
        Assert.Equal(1200m, total); // no se pierde volumen al juntar
    }

    [Fact]
    public void Cantidad_de_filas_ajusta_el_alto_solo()
    {
        var (_, p) = Chart(x =>
        {
            x.RowMode = SessionVolumeProfile.RowSizing.Rows;
            x.RowCount = 20;
        });
        var (rows, size, total) = Last(p);
        Assert.Equal(2m, size); // 400 ticks / 20 filas = 20 ticks por fila
        Assert.InRange(rows, 19, 21);
        Assert.Equal(1200m, total);
    }
}
