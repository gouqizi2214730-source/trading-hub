using System.Collections;
using System.Reflection;
using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Indicators.Native.VpUltimate;
using Xunit;

namespace TradingHub.Tests;

/// <summary>El perfil de volumen único: perfiles unidos a mano (inicio / fin) y solos por superposición; el VWAP único.</summary>
public class ProfileMergeTests
{
    private static (ChartModel Model, VpProfile Profile) Chart(Action<VpProfile> setup, string period = "H1")
    {
        var data = VpUltimateTests.Market(8);
        var model = new ChartModel(data, ChartPeriod.Parse(period), new ChartSettings { TimeZoneId = "UTC" });
        var vp = new VpProfile();
        setup(vp);
        model.AddIndicator(vp);
        model.Update();
        return (model, vp);
    }

    private static IList Done(VpProfile p) => (IList)typeof(VpProfile).GetField("_done", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;

    private static int Parts(object r) => (int)r.GetType().GetField("Parts")!.GetValue(r)!;

    [Fact]
    public void Unir_desde_un_inicio_hasta_un_fin_deja_un_solo_perfil()
    {
        var old = ResultCache.Root;
        ResultCache.Root = null;
        try
        {
            var (model, vp) = Chart(_ => { });
            int before = Done(vp).Count;
            Assert.True(before >= 6);
            // días 2 a 4 (19/10 es lunes): inicio = martes 00:00, fin = jueves 00:00
            var from = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc).Ticks;
            var to = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc).Ticks;
            vp.MergedRanges = $"{from}-{to}";
            model.Update();
            var done = Done(vp);
            Assert.Equal(before - 2, done.Count);
            Assert.Contains(done.Cast<object>(), r => Parts(r) == 3);
            // se guarda con el gráfico
            Assert.Equal($"{from}-{to}", vp.MergedRanges);
        }
        finally
        {
            ResultCache.Root = old;
        }
    }

    [Fact]
    public void Los_vecinos_que_se_superponen_se_unen_solos()
    {
        var old = ResultCache.Root;
        ResultCache.Root = null;
        try
        {
            var (_, plain) = Chart(_ => { });
            var (_, auto) = Chart(x =>
            {
                x.AutoMerge = true;
                x.AutoMergePercent = 1;
                x.MergeBasis = VpProfile.OverlapBasis.Range;
            });
            Assert.True(Done(auto).Count < Done(plain).Count);
            Assert.Contains(Done(auto).Cast<object>(), r => Parts(r) > 1);
        }
        finally
        {
            ResultCache.Root = old;
        }
    }

    [Fact]
    public void Filas_por_ticks_alineadas_al_tick()
    {
        var old = ResultCache.Root;
        ResultCache.Root = null;
        try
        {
            var (_, vp) = Chart(x =>
            {
                x.RowMode = VpProfile.RowSizing.Ticks;
                x.TicksPerRow = 50;
            });
            var r = Done(vp)[0]!;
            decimal step = (decimal)r.GetType().GetField("Step")!.GetValue(r)!;
            decimal yMin = (decimal)r.GetType().GetField("YMin")!.GetValue(r)!;
            Assert.Equal(5m, step);
            Assert.Equal(0m, yMin % 5m);
        }
        finally
        {
            ResultCache.Root = old;
        }
    }

    [Fact]
    public void El_vwap_unico_calcula_sus_partes_y_guarda_sus_ajustes()
    {
        var data = VpUltimateTests.Market(8);
        var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        var v = new VwapSuite { UseClaire = true, UseMultiPeriod = true, UseRolling = true, UseSessions = true, UseCloses = true, UsePrevious = true };
        v.Anchored.Band3 = true;
        model.AddIndicator(v);
        model.Update();
        int last = data.Trades.Count > 0 ? v.CurrentBar - 1 : 0;
        var main = (ValueDataSeries)v.DataSeries.FindById("A.VWAP");
        Assert.True(main[last] > 0);
        Assert.True(((ValueDataSeries)v.DataSeries.FindById("A.U3"))[last] > main[last]);
        Assert.True(((ValueDataSeries)v.DataSeries.FindById("C.R30"))[last] >= 0);

        v.Anchored.Mult1 = 1.5m;
        var props = Workspace.SaveIndicator(v, out var series);
        var copy = new VwapSuite();
        Workspace.LoadIndicator(copy, props, series);
        Assert.Equal(1.5m, copy.Anchored.Mult1);
        Assert.True(copy.Anchored.Band3);
        Assert.True(copy.UseClaire);
        Assert.NotNull(copy.DataSeries.FindById("S.V0"));
    }
}

/// <summary>Alertas como en TradingView sobre el precio y las líneas de los indicadores.</summary>
public class ChartAlertTests
{
    [Fact]
    public void El_precio_cruzando_el_vwap_dispara_y_una_sola_vez_se_apaga()
    {
        var data = VpUltimateTests.Market(4);
        var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        var v = new VwapSuite();
        model.AddIndicator(v);
        model.Update();
        var line = ChartAlertEngine.Lines(model).First(l => l.Series == "A.VWAP");
        Assert.Equal("TradingHub.Indicators.Native.VpUltimate.VwapSuite#0", line.Indicator);
        var a = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.Crossing, Right = line, Frequency = AlertFrequency.Once };
        int crosses = 0;
        for (int b = 1; b < model.Series.Count; b++)
            if (ChartAlertEngine.Check(model, a, b) == true) crosses++;
        Assert.True(crosses > 3);

        // mayor que un valor fijo bajo: se cumple ya; «una sola vez» → se apaga
        var g = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.GreaterThan, Right = AlertOperand.Fixed(1), Frequency = AlertFrequency.Once };
        model.Alerts.Add(g);
        Assert.Single(ChartAlertEngine.Evaluate(model, DateTime.UtcNow));
        Assert.False(g.Active);
        Assert.Empty(ChartAlertEngine.Evaluate(model, DateTime.UtcNow));

        // «una vez por vela»: no se repite en la misma vela
        var p = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.GreaterThan, Right = AlertOperand.Fixed(1), Frequency = AlertFrequency.OncePerBar };
        model.Alerts.Add(p);
        Assert.Single(ChartAlertEngine.Evaluate(model, DateTime.UtcNow));
        Assert.Empty(ChartAlertEngine.Evaluate(model, DateTime.UtcNow));
        Assert.True(p.Active);

        // canal: dentro de [0, 10^9]
        var ch = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.InsideChannel, Right = AlertOperand.Fixed(1_000_000_000), Lower = AlertOperand.Fixed(0), Frequency = AlertFrequency.EveryTime };
        Assert.True(ChartAlertEngine.Check(model, ch, model.Series.Count - 1));
    }
}
