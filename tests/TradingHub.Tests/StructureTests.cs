using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Indicators.Native.Structure;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Estructura SMC / ICT: algoritmos portados de VPFM y los indicadores que los dibujan.</summary>
public class StructureTests
{
    private static List<Bar> Pattern()
    {
        // una subida en zigzag con un hueco (FVG) y dos máximos iguales
        var bars = new List<Bar>();
        double p = 100;
        long t = 1_700_000_000;
        var path = new[] { 0, 2, 4, 3, 2, 4, 6, 8, 7, 6, 8, 10, 12, 11, 10, 12, 12, 11, 10, 9, 8, 7, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        for (int i = 0; i < 200; i++)
        {
            double target = 100 + (i < path.Length ? path[i] : 15 + Math.Sin(i / 3.0) * 2);
            double o = p, c = target;
            double h = Math.Max(o, c) + 0.3, l = Math.Min(o, c) - 0.3;
            bars.Add(new Bar(t + i * 300, o, h, l, c, 100 + i % 7));
            p = c;
        }

        // un FVG alcista explícito: vela 150 con hueco entre la 149 y la 151
        var b149 = bars[149];
        var b151 = bars[151];
        bars[149] = new Bar(b149.T, 110, 111, 109.5, 110.5, 100);
        bars[150] = new Bar(bars[150].T, 110.5, 116, 110.4, 115.5, 300);
        bars[151] = new Bar(b151.T, 115.5, 116.5, 114, 116, 100);
        for (int i = 152; i < 170; i++) bars[i] = new Bar(bars[i].T, 116, 117, 115.5, 116.5, 100);
        return bars;
    }

    [Fact]
    public void SwingsBreaksAndGaps()
    {
        var bars = Pattern();
        var swings = Smc.Swings(bars, 3);
        Assert.True(swings.Count >= 4);
        Assert.True(swings.Zip(swings.Skip(1)).All(p => p.First.Type != p.Second.Type)); // alternan alto / bajo
        Assert.Contains(swings, s => s.Label == "HH");
        Assert.Contains(swings, s => s.Label == "HL");

        var breaks = Smc.BosChoch(bars, swings);
        Assert.NotEmpty(breaks);
        Assert.All(breaks, b => Assert.True(b.Broken > b.I));

        var fvgs = Smc.Fvg(bars);
        var gap = fvgs.FirstOrDefault(g => g.I == 150);
        Assert.NotNull(gap);
        Assert.Equal(1, gap!.Dir);
        Assert.Equal(114, gap.Top);
        Assert.Equal(111, gap.Bottom);
        Assert.Equal(-1, gap.Filled); // nadie volvió a 111

        var obs = Smc.OrderBlocks(bars, swings);
        Assert.NotEmpty(obs);
        Assert.All(obs, ob => Assert.True(ob.Top >= ob.Bottom));

        var pools = Smc.Liquidity(bars, swings, 0.02);
        Assert.All(pools, p => Assert.True(p.Count >= 2));

        var cisds = Smc.Cisds(bars);
        Assert.All(cisds, c => Assert.True(c.I > c.Start));

        var htf = Smc.Aggregate(bars, 3600);
        Assert.True(htf.Count >= 16 && htf.Count <= 18);
        Assert.Equal(bars.Where(b => b.T / 3600 == bars[0].T / 3600).Max(b => b.H), htf[0].H);
        var (mid, up, dn) = Smc.Bollinger(bars.Select(b => b.C).ToList());
        Assert.True(double.IsNaN(mid[10]) && !double.IsNaN(mid[19]) && up[50] > mid[50] && mid[50] > dn[50]);
        Assert.Equal((3600L, 4 * 3600L), Smc.HigherTimeframes(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void IndicatorsCalculateAndRender()
    {
        var data = VpUltimateTests.Market(6);
        foreach (var period in new[] { "M15", "H1" })
        {
            var model = new ChartModel(data, ChartPeriod.Parse(period), new ChartSettings { TimeZoneId = "UTC", Style = ChartStyle.Candles });
            var smc = new SmcStructure { ShowCisd = true, ShowFilledFvg = true };
            var htf = new HtfStructure();
            var bb = new TripleBollinger();
            var vw = new VwapClaire { Daily = true, Weekly = true };
            foreach (var ind in new Indicator[] { smc, htf, bb, vw }) model.AddIndicator(ind);
            for (int k = 0; k < 3; k++) model.Update();
            var r30 = (ValueDataSeries)vw.DataSeries[0];
            Assert.True(r30[model.Series.Count - 1] > 0, "VWAP 30 días vacío");
            var bbUp = (ValueDataSeries)bb.DataSeries[1];
            Assert.True(bbUp[model.Series.Count - 1] > 0, "Bollinger vacío");
            model.Viewport.BarWidth = 10;
            using var surface = SKSurface.Create(new SKImageInfo(1600, 900));
            new ChartRenderer().Render(surface.Canvas, model, 1600, 900, new ChartRenderState());
            var dir = Environment.GetEnvironmentVariable("TH_SNAPSHOT_DIR");
            if (string.IsNullOrEmpty(dir)) continue;
            Directory.CreateDirectory(dir);
            using var img = surface.Snapshot();
            using var png = img.Encode(SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(Path.Combine(dir, $"structure-{period}.png"), png.ToArray());
        }
    }
}
