using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Indicators.Native.Structure;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Los indicadores de la biblioteca nueva (de openmarket): calculan, dibujan, no fallan y no se redibujan hacia atrás.</summary>
public class LibraryIndicatorsTests
{
    public static IEnumerable<object[]> Types() => new[]
    {
        typeof(SupplyDemandZones), typeof(CalendarKeyLevels), typeof(FloorPivots), typeof(StandardPivots), typeof(LineBreak),
        typeof(LiquiditySweeps), typeof(SessionSweepInverseFvg), typeof(PriorDayFvg), typeof(EqualHighsLowsFvg), typeof(GapFillBreakouts), typeof(BreakoutTestsRetests),
        typeof(TradingHub.Indicators.Native.Flow.CvdFlowDominance), typeof(TradingHub.Indicators.Native.Flow.CvdDivergence), typeof(TradingHub.Indicators.Native.Flow.DecisionBoard),
        typeof(TradingHub.Indicators.Native.Flow.BarAbsorption), typeof(TradingHub.Indicators.Native.Flow.VolumeDeltaPro), typeof(TradingHub.Indicators.Native.Flow.VolumeBubbles),
        typeof(TradingHub.Indicators.Native.Flow.FootprintAlerts), typeof(TradingHub.Indicators.Native.BigTrades), typeof(TradingHub.Indicators.Native.Flow.BarStatsStrip),
        typeof(ValueAreaReversion), typeof(ProfileLevelAlerts), typeof(ProfileTails), typeof(VolumeProfileValueArea), typeof(VolumeHeatHours),
        typeof(SlidingVolumeProfile), typeof(CalendarAnchoredVwap), typeof(ExecutionBenchmarks),
        typeof(MarketSessions), typeof(SessionMap), typeof(AsianSessionSweeps), typeof(SessionSweeps), typeof(SessionGapFill),
        typeof(StructureShiftSweeps), typeof(AnchoredMarketStructure), typeof(StructureRegimeOscillator), typeof(StructureScatterPanel), typeof(StructureTrendPanel), typeof(WaveExtensions),
    }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(Types))]
    public void Calcula_y_dibuja_sin_errores(Type type)
    {
        foreach (var period in new[] { "M5", "M15", "H1" })
        {
            var data = VpUltimateTests.Market(10);
            var model = new ChartModel(data, ChartPeriod.Parse(period), new ChartSettings { TimeZoneId = "UTC", Style = ChartStyle.Candles });
            var ind = (Indicator)Activator.CreateInstance(type)!;
            model.AddIndicator(ind);
            for (int k = 0; k < 2; k++) model.Update();
            model.Viewport.BarWidth = 8;
            using var surface = SKSurface.Create(new SKImageInfo(1400, 800));
            new ChartRenderer().Render(surface.Canvas, model, 1400, 800, new ChartRenderState());
            var slot = model.Indicators.Single();
            Assert.Null(slot.Error);
            if (ind is StructureIndicator si) Assert.True(si.ComputeError == null, si.ComputeError);
            _ = ind.GetAlertLevels().ToList();
            var dir = Environment.GetEnvironmentVariable("TH_SNAPSHOT_DIR");
            if (string.IsNullOrEmpty(dir) || period != "M15") continue;
            Directory.CreateDirectory(dir);
            using var img = surface.Snapshot();
            using var png = img.Encode(SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(Path.Combine(dir, $"lib-{type.Name}.png"), png.ToArray());
        }
    }

    /// <summary>
    /// No se redibuja hacia atrás: las marcas y etiquetas que había con menos historia siguen iguales (misma vela, mismo
    /// precio) cuando llegan más velas.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void No_redibuja_hacia_atras(Type type)
    {
        if (!typeof(SceneIndicator).IsAssignableFrom(type)) return;
        Scene Run(int days, out int lastClosed)
        {
            var model = new ChartModel(VpUltimateTests.Market(days), ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC", Style = ChartStyle.Candles });
            var ind = (SceneIndicator)Activator.CreateInstance(type)!;
            model.AddIndicator(ind);
            for (int k = 0; k < 2; k++) model.Update();
            lastClosed = model.Series.Count - 2;
            Assert.True(ind.ComputeError == null, ind.ComputeError);
            return ind.CurrentScene;
        }

        var shortRun = Run(7, out int cut);
        var full = Run(10, out _);
        // las de los últimos días de la corrida corta (lo más viejo puede salir de la vista por los límites de dibujo)
        int from = cut - 150;
        foreach (var m in shortRun.Marks.Where(m => m.Bar < cut && m.Bar >= from))
            Assert.True(full.Marks.Any(f => f.Bar == m.Bar && f.Kind == m.Kind && Math.Abs(f.Price - m.Price) < 1e-6 && f.Text == m.Text),
                $"{type.Name}: la marca {m.Kind} «{m.Text}» de la vela {m.Bar} desapareció o se movió");
        foreach (var t in shortRun.Tags.Where(t => t.Bar < cut && t.Bar >= from))
            Assert.True(full.Tags.Any(f => f.Bar == t.Bar && Math.Abs(f.Price - t.Price) < 1e-6 && f.Text == t.Text),
                $"{type.Name}: la etiqueta «{t.Text}» de la vela {t.Bar} desapareció o se movió");
    }
}
