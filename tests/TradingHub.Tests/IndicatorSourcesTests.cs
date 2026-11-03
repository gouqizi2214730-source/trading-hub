using TradingHub.App.Services;
using TradingHub.Indicators.Native;
using TradingHub.Indicators.Native.VpUltimate;
using TradingHub.Scripting;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Cualquier indicador de la biblioteca trae su código: se ve y se copia como propio, y la copia compila.</summary>
public class IndicatorSourcesTests
{
    [Fact]
    public void Los_de_la_biblioteca_traen_su_codigo()
    {
        Assert.Contains("class VpProfile", IndicatorSources.EmbeddedSource(typeof(VpProfile)) ?? "");
        Assert.Contains("class MovingAverage", IndicatorSources.EmbeddedSource(typeof(MovingAverage)) ?? "");
        Assert.Contains("class Cvd", IndicatorSources.EmbeddedSource(typeof(Cvd)) ?? "");
    }

    [Theory]
    [InlineData(typeof(MovingAverage))]
    [InlineData(typeof(VpProfile))]
    [InlineData(typeof(VpKeyLevels))]
    [InlineData(typeof(Cvd))]
    [InlineData(typeof(SessionVolumeProfile))]
    public void La_copia_propia_compila(Type t)
    {
        var dir = Path.Combine(Path.GetTempPath(), "th-fork-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var path = IndicatorSources.Fork(t, t.Name + "Copia", dir);
            Assert.True(File.Exists(path));
            var text = File.ReadAllText(path);
            Assert.Contains("class " + t.Name + "Copia", text);
            Assert.Contains("(copia)", text);
            var res = new ScriptCompiler().Compile(new[] { path }, "ForkTest" + t.Name);
            Assert.True(res.Success, string.Join("\n", res.Diagnostics.Where(d => d.Severity == "error").Select(d => d.Line + ": " + d.Message)));
            Assert.Contains(res.Assembly!.GetTypes(), x => x.Name == t.Name + "Copia");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
