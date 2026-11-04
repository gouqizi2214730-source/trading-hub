using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Scripting;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Scripts KVS: se traducen, compilan y calculan; los errores salen con la línea del .kvs.</summary>
public class KvsTests
{
    private static (Indicator Ind, ChartModel Model) Run(string source, string name = "Prueba")
    {
        var dir = Path.Combine(Path.GetTempPath(), "kvs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".kvs");
        File.WriteAllText(path, source);
        var res = new ScriptCompiler().Compile(new[] { path }, "KvsTest");
        Assert.True(res.Success, string.Join("\n", res.Diagnostics.Select(d => d.ToString())));
        var type = res.Assembly!.GetTypes().Single(t => t.Name == KvsTranslator.ClassNameFor(name));
        var ind = (Indicator)Activator.CreateInstance(type)!;
        var model = new ChartModel(VpUltimateTests.Market(3), ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        model.AddIndicator(ind);
        model.Update();
        return (ind, model);
    }

    [Fact]
    public void Bandas_con_la_forma_corta()
    {
        var (ind, model) = Run("""
            name "Bandas"
            param length = 20 { min: 1, max: 500 }
            param mult = 2
            input src = ohlcv.close
            output basis = line { overlay: true, color: "#f2b53a", width: 2 }
            output upper = line { overlay: true }
            output lower = line { overlay: true }
            onBar {
              basis = ema(src, length)
              var dev = stdev(src, length)
              upper = basis + mult * dev
              lower = basis - mult * dev
              if (crossover(src, upper)) {
                alert("arriba")
              }
            }
            """);
        int last = model.Series.Count - 1;
        var b = (ValueDataSeries)ind.DataSeries[0];
        var u = (ValueDataSeries)ind.DataSeries[1];
        Assert.True(b[last] > 0);
        Assert.True(u[last] > b[last]);
        Assert.Equal("Bandas", Indicator.DefaultName(ind.GetType()));
        Assert.NotEqual(IndicatorDataProvider.NewPanel, ind.Panel);
        // los parámetros se ven en el editor y recalculan
        ind.GetType().GetProperty("length")!.SetValue(ind, 5m);
        model.Update();
        Assert.True(b[last] > 0);
    }

    [Fact]
    public void La_forma_de_wrun_y_el_delta_coloreado_por_signo()
    {
        var (ind, model) = Run("""
            const len = param("len", 14, { min: 2, max: 200, description: "largo" })
            input("buy", trades.volume, { side: "BUY", missing: "zero" })
            input("sell", trades.volume, { side: "SELL" })
            output("cvd", line, lower, { color_by: "sign", colors: ["#26c281", "#f0525a"] })
            output("avg", line, lower, { color: "#ffffff", opacity: 0.5 })
            function onBar() {
              out_cvd(cum(buy - sell))
              out_avg(sma(cvd, len))
              var prev = cvd[1]
              if (cvd > prev and not na(prev)) { avg = avg }
            }
            """, "Wrun");
        int last = model.Series.Count - 1;
        var cvd = (ValueDataSeries)ind.DataSeries[0];
        decimal expected = 0;
        for (int i = 0; i <= last; i++) expected += model.Series[i].Ask - model.Series[i].Bid;
        Assert.Equal((double)expected, (double)cvd[last], 3);
        Assert.Equal(IndicatorDataProvider.NewPanel, ind.Panel);
    }

    [Theory]
    [InlineData("output x = line\nonBar {\n  x = foo(close)\n}", 3, "foo")]
    [InlineData("output x = line\nonBar {\n  y = close\n}", 3, "y")]
    [InlineData("param n = abc\noutput x = line", 1, "abc")]
    [InlineData("onBar {\n x = 1\n", 3, "")]
    public void Los_errores_dicen_la_linea(string source, int line, string word)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kvs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Malo.kvs");
        File.WriteAllText(path, source);
        var res = new ScriptCompiler().Compile(new[] { path }, "KvsBad");
        Assert.False(res.Success);
        var d = res.Diagnostics.First(x => x.Severity == "error");
        Assert.Equal(path, d.File);
        Assert.Equal(line, d.Line);
        Assert.Contains(word, d.Message);
    }

    [Fact]
    public void Un_error_de_csharp_vuelve_con_la_linea_del_kvs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kvs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Tipo.kvs");
        File.WriteAllText(path, "output x = line\nonBar {\n  x = 1\n  x = close > open\n}\n");
        var res = new ScriptCompiler().Compile(new[] { path }, "KvsType");
        Assert.False(res.Success);
        var d = res.Diagnostics.First(x => x.Severity == "error");
        Assert.Equal(path, d.File);
        Assert.Equal(4, d.Line);
    }
}
