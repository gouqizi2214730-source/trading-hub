using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native.VpUltimate;
using TradingHub.Market;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Los indicadores «VP definitivo» calculan y se dibujan sin errores con varios días de operaciones.</summary>
public class VpUltimateTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = "test", TickSize = 0.1m, VolumeStep = 0.001m };

    /// <summary>~8 días de un paseo aleatorio, una operación cada 20 s, con más volumen en las sesiones de Londres / NY.</summary>
    internal static InstrumentData Market(int days = 8, int seed = 5)
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        var rnd = new Random(seed);
        var start = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc); // lunes
        decimal px = 70000m;
        long id = 1;
        for (var t = start; t < start.AddDays(days); t = t.AddSeconds(20))
        {
            int h = t.Hour;
            double activity = h is >= 7 and < 21 ? 3 : 1;
            px = Math.Max(1000, px + (decimal)Math.Round((rnd.NextDouble() - 0.497) * 40 * activity, 1));
            decimal vol = Math.Round((decimal)(rnd.NextDouble() * activity * (rnd.Next(50) == 0 ? 25 : 1)), 3) + 0.001m;
            data.Trades.Add(new Trade(id++, t, px, vol, rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        return data;
    }

    private static Indicator[] All() => new Indicator[]
    {
        new VpProfile { ShowDeveloping = true },
        new VpVolumeColors(),
        new VpMultiVwap(),
        new VpAnchoredVwap { Fill = true },
        new VpPreviousVwap { Band1 = true },
        new VpVwapCloses(),
        new VpDeltaBubbles(),
        new VpSessionBackground(),
        new VpKeyLevels { DayRange = true, AsiaRange = true, LondonRange = true, NewYorkRange = true },
        new VpSessionProfiles { ShowAsia = true, ShowDeveloping = true },
        new VpRollingVwap { Band2 = true, Fill = true },
        new VpDoubleInside { MotherMultiple = 0 },
    };

    [Theory]
    [InlineData("M15")]
    [InlineData("H1")]
    [InlineData("D1")]
    public void Todos_calculan_y_se_dibujan(string period)
    {
        var old = ResultCache.Root;
        ResultCache.Root = null;
        try
        {
            var data = Market();
            var model = new ChartModel(data, ChartPeriod.Parse(period), new ChartSettings { TimeZoneId = "UTC" });
            foreach (var ind in All()) model.AddIndicator(ind);
            for (int k = 0; k < 3; k++) model.Update();
            model.Viewport.BarWidth = period == "M15" ? 3 : 12;
            using var surface = SKSurface.Create(new SKImageInfo(1600, 900));
            surface.Canvas.Clear(SKColors.Black);
            new ChartRenderer().Render(surface.Canvas, model, 1600, 900, new ChartRenderState());
            var dir = Environment.GetEnvironmentVariable("TH_SNAPSHOT_DIR");
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                using var img = surface.Snapshot();
                using var png = img.Encode(SKEncodedImageFormat.Png, 90);
                File.WriteAllBytes(Path.Combine(dir, $"vp-{period}.png"), png.ToArray());
            }
        }
        finally
        {
            ResultCache.Root = old;
        }
    }

    [Fact]
    public void Cada_uno_por_separado()
    {
        var dir = Environment.GetEnvironmentVariable("TH_SNAPSHOT_DIR");
        foreach (var ind in All())
        {
            var data = Market(6);
            var model = new ChartModel(data, ChartPeriod.Parse("M30"), new ChartSettings { TimeZoneId = "UTC" });
            model.AddIndicator(ind);
            for (int k = 0; k < 3; k++) model.Update();
            model.Viewport.BarWidth = 6;
            using var surface = SKSurface.Create(new SKImageInfo(1400, 800));
            surface.Canvas.Clear(SKColors.Black);
            new ChartRenderer().Render(surface.Canvas, model, 1400, 800, new ChartRenderState());
            if (string.IsNullOrEmpty(dir)) continue;
            using var img = surface.Snapshot();
            using var png = img.Encode(SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(Path.Combine(dir, $"one-{ind.GetType().Name}.png"), png.ToArray());
        }
    }

    [Fact]
    public void Velas_por_volumen_pintan_solo_las_anormales()
    {
        var data = Market(3);
        var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        var ind = new VpVolumeColors();
        model.AddIndicator(ind);
        for (int k = 0; k < 3; k++) model.Update();
        var paint = ind.DataSeries.OfType<PaintbarsDataSeries>().Single();
        int painted = Enumerable.Range(0, model.Series.Count).Count(i => paint[i] != null);
        Assert.InRange(painted, 1, model.Series.Count / 2);
    }

    [Fact]
    public void Vwap_anclado_coincide_con_el_de_la_sesion()
    {
        var data = Market(2);
        var model = new ChartModel(data, ChartPeriod.Parse("H1"), new ChartSettings { TimeZoneId = "UTC" });
        var a = new VpAnchoredVwap();
        var b = new TradingHub.Indicators.Native.SessionVwap();
        model.AddIndicator(a);
        model.AddIndicator(b);
        for (int k = 0; k < 3; k++) model.Update();
        var va = (ValueDataSeries)a.DataSeries[1];
        var vb = (ValueDataSeries)b.DataSeries[0];
        for (int i = 0; i < model.Series.Count - 1; i++) Assert.Equal(Math.Round(vb[i], 4), Math.Round(va[i], 4));
    }

    [Fact]
    public void Posicion_larga_con_riesgo_y_recorrido()
    {
        var data = Market(2);
        var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        model.Update();
        var s = model.Series;
        int e = s.Count - 60;
        double entry = (double)s.TicksToPrice(s[e].CloseTicks);
        var d = new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.LongPosition, AccountSize = 10000, RiskPercent = 1 };
        d.Points.Add(new TradingHub.Charting.Drawings.ChartPoint(s[e].Time, entry));
        d.InitPosition(300, s[^1].Time.AddHours(5));
        Assert.Equal(Math.Floor(100m / 300m / 0.001m) * 0.001m, d.EffectiveQuantity(0.001m)); // 1 % de 10000 ÷ 300 de stop
        var r = d.PositionResult(s);
        Assert.True(r.ExitBar > e);
        model.Drawings.Add(d);
        model.Viewport.BarWidth = 8;
        using var surface = SKSurface.Create(new SKImageInfo(1400, 800));
        new ChartRenderer().Render(surface.Canvas, model, 1400, 800, new ChartRenderState());
        var dir = Environment.GetEnvironmentVariable("TH_SNAPSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        using var img = surface.Snapshot();
        using var png = img.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(dir, "position.png"), png.ToArray());
    }
}
