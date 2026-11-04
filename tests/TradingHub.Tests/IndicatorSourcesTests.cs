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

    [Theory]
    [InlineData(typeof(VpProfile))]
    [InlineData(typeof(MovingAverage))]
    [InlineData(typeof(Cvd))]
    [InlineData(typeof(VwapSuite))]
    public void Editar_directo_compila_y_reemplaza_al_original(Type t)
    {
        var dir = Path.Combine(Path.GetTempPath(), "th-edit-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var path = IndicatorSources.EditInPlace(t, dir);
            Assert.Equal(t.Name + ".cs", Path.GetFileName(path));
            var res = new ScriptCompiler().Compile(new[] { path }, "EditTest" + t.Name);
            Assert.True(res.Success, string.Join("\n", res.Diagnostics.Where(d => d.Severity == "error").Select(d => d.Line + ": " + d.Message)));
            var edited = res.Assembly!.GetTypes().Single(x => x.Name == t.Name && typeof(TradingHub.Indicators.Indicator).IsAssignableFrom(x));
            Assert.Equal(t.FullName, IndicatorCatalog.ReplacedBy(edited));
            // mismo nombre visible que el original; las demás clases del archivo no aparecen en la biblioteca
            Assert.Equal(TradingHub.Indicators.Indicator.DefaultName(t), TradingHub.Indicators.Indicator.DefaultName(edited));
            var catalog = new IndicatorCatalog();
            catalog.Scan(t.Assembly, "Trading Hub");
            catalog.Scan(res.Assembly!, UserScripts.Source);
            Assert.Equal(edited, catalog.Find(t.FullName!)!.Type);
            Assert.DoesNotContain(catalog.Items, i => i.Type == t);
            Assert.Single(catalog.Items, i => i.Source == UserScripts.Source);
            // la misma ruta si se vuelve a editar
            Assert.Equal(path, IndicatorSources.EditInPlace(t, dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
