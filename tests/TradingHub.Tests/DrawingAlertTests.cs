using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Data;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Alertas sobre lo dibujado (líneas, canales, rectángulos) con cruza, toca, entra y sale, como en TradingView.</summary>
public class DrawingAlertTests
{
    private static ChartModel Model()
    {
        var data = VpUltimateTests.Market(4);
        var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        model.Update();
        return model;
    }

    [Fact]
    public void Un_canal_paralelo_da_sus_tres_lineas_en_cada_vela()
    {
        var m = Model();
        var s = m.Series;
        var d = new ChartDrawing { Kind = DrawingKind.ParallelChannel, ExtendRight = true };
        d.Points.Add(new ChartPoint(s[10].Time, 100));
        d.Points.Add(new ChartPoint(s[20].Time, 110));
        d.InitChannel(5);
        Assert.Equal(105, d.ValueAt(s, 10, "upper"));
        Assert.Equal(100, d.ValueAt(s, 10, "lower"));
        Assert.Equal(107.5, d.ValueAt(s, 15, "middle"));
        Assert.Equal(120, d.ValueAt(s, 30, "lower")); // extendido a la derecha
        Assert.Null(d.ValueAt(s, 5, "lower")); // no extendido a la izquierda

        // mover el primer punto conserva el ancho; el tercer manejador lo cambia
        d.MoveHandle(0, new ChartPoint(s[10].Time, 90));
        Assert.Equal(5, d.ChannelOffset, 6);
        d.MoveHandle(2, new ChartPoint(s[15].Time, 120)); // la base a mitad de camino está en 100
        Assert.Equal(20, d.ChannelOffset, 6);
    }

    [Fact]
    public void El_precio_entra_y_sale_de_un_canal_dibujado_y_toca_una_linea()
    {
        var m = Model();
        var s = m.Series;
        int n = s.Count;
        decimal Px(int b) => m.Instrument.FromTicks(s[b].CloseTicks);
        decimal lo = Enumerable.Range(0, n).Min(Px), hi = Enumerable.Range(0, n).Max(Px);
        decimal mid = (lo + hi) / 2, band = (hi - lo) / 6;
        var rect = new ChartDrawing { Kind = DrawingKind.Rectangle, ExtendLeft = true, ExtendRight = true };
        rect.Points.Add(new ChartPoint(s[0].Time, (double)(mid + band)));
        rect.Points.Add(new ChartPoint(s[n - 1].Time, (double)(mid - band)));
        m.Drawings.Add(rect);
        var lines = ChartAlertEngine.DrawingLines(m, rect);
        Assert.Equal(new[] { "top", "bottom" }, lines.Select(l => l.Series));
        var enter = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.EnteringChannel, Right = lines[0], Lower = lines[1] };
        var exit = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.ExitingChannel, Right = lines[0], Lower = lines[1] };
        int enters = 0, exits = 0;
        for (int b = 1; b < n; b++)
        {
            if (ChartAlertEngine.Check(m, enter, b) == true) enters++;
            if (ChartAlertEngine.Check(m, exit, b) == true) exits++;
        }

        Assert.True(enters > 0 && exits > 0);

        // una horizontal en el máximo de una vela: «toca» con el rango de la vela
        int k = n / 2;
        double high = (double)m.Instrument.FromTicks(s[k].HighTicks);
        var h = new ChartDrawing { Kind = DrawingKind.HorizontalLine };
        h.Points.Add(new ChartPoint(s[k].Time, high));
        m.Drawings.Add(h);
        var touch = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.Touching, Right = ChartAlertEngine.DrawingLines(m, h)[0] };
        Assert.True(ChartAlertEngine.Check(m, touch, k));
        Assert.True(touch.Uses(h.Id.ToString("N")));

        // sube más de 0 % en 1 vela: se cumple en alguna
        var up = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.MovingUpPercent, Right = AlertOperand.Fixed(0), Bars = 1 };
        Assert.Contains(Enumerable.Range(1, n - 1), b => ChartAlertEngine.Check(m, up, b) == true);
    }
}

/// <summary>Alertas sobre lo que los indicadores dibujan a mano: POC vírgenes, cierres de VWAP, niveles del perfil.</summary>
public class IndicatorLevelAlertTests
{
    [Fact]
    public void Un_poc_virgen_se_puede_elegir_con_el_mouse_y_alertar()
    {
        var data = VpUltimateTests.Market(8);
        var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        var npoc = new TradingHub.Indicators.Native.VpUltimate.VpNakedPoc { ShowTouched = true };
        model.AddIndicator(npoc);
        model.AddIndicator(new TradingHub.Indicators.Native.VpUltimate.VpProfile());
        model.Update();
        var levels = npoc.GetAlertLevels().ToList();
        Assert.NotEmpty(levels);
        var ops = ChartAlertEngine.Lines(model).Where(o => o.Series!.StartsWith(ChartAlertEngine.LevelPrefix)).ToList();
        Assert.Contains(ops, o => o.Label.Contains("NPOC"));
        Assert.Contains(ops, o => o.Label.Contains("POC") && o.Indicator!.Contains("VpProfile"));

        var lv = levels[0];
        var op = ops.First(o => o.Series == ChartAlertEngine.LevelPrefix + lv.Id && o.Indicator!.Contains("VpNakedPoc"));
        int bar = Math.Max(lv.FromBar, 0);
        Assert.True(ChartAlertEngine.TryValue(model, op, bar, out var v));
        Assert.Equal(lv.Price, v);

        // el clic derecho sobre la línea la encuentra
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1200, 700));
        model.Viewport.AutoScale = false;
        model.Viewport.PriceTop = (double)lv.Price + 50;
        model.Viewport.PriceBottom = (double)lv.Price - 50;
        new TradingHub.Charting.Rendering.ChartRenderer().Render(surface.Canvas, model, 1200, 700, new TradingHub.Charting.Rendering.ChartRenderState());
        var layout = model.LastLayout!;
        int at = lv.ToBar == int.MaxValue ? model.Series.Count - 1 : Math.Min(lv.ToBar, model.Series.Count - 1);
        float x = layout.CenterX(Math.Max(lv.FromBar, at)), y = layout.Main.Y(lv.Price);
        var hit = ChartAlertEngine.LineAt(model, x, y);
        Assert.NotNull(hit);
        Assert.StartsWith(ChartAlertEngine.LevelPrefix, hit!.Series);

        // donde la dibuja el indicador (un precio de nivel va al centro de su fila): ahí también la captura
        float drawnY = ((TradingHub.Indicators.IChartContainer)layout.Main).GetYByPrice(lv.Price, false);
        var hit2 = ChartAlertEngine.LineAt(model, x, drawnY, 10);
        Assert.NotNull(hit2);
        Assert.Equal(lv.Price, ChartAlertEngine.LevelOf(model, hit2!)!.Value.Price);

        // resaltada como un dibujo elegido
        model.HighlightedLine = hit2;
        new TradingHub.Charting.Rendering.ChartRenderer().Render(surface.Canvas, model, 1200, 700, new TradingHub.Charting.Rendering.ChartRenderState());

        // la etiqueta con las partes elegidas
        npoc.LabelPeriod = false;
        npoc.LabelDate = false;
        npoc.LabelPrice = true;
        Assert.True(npoc.ShowLabel);
        npoc.ShowLabel = false;
        Assert.False(npoc.LabelPrice);

        // y la alerta «toca» se evalúa
        var a = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.Touching, Right = op };
        Assert.NotNull(ChartAlertEngine.Check(model, a, Math.Max(bar, 1)));
    }
}

public class AllIndicatorLevelsTests
{
    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "smc", true },
        new object[] { "htf", false },
        new object[] { "sessions", true },
        new object[] { "svp", true },
        new object[] { "dib", false },
        new object[] { "trap", false },
    };

    private static TradingHub.Indicators.Indicator Make(string kind) => kind switch
    {
        "smc" => new TradingHub.Indicators.Native.Structure.SmcStructure { ShowCisd = true, ShowFilledFvg = true },
        "htf" => new TradingHub.Indicators.Native.Structure.HtfStructure(),
        "sessions" => new TradingHub.Indicators.Native.VpUltimate.VpSessionProfiles { ShowAsia = true, ShowDeveloping = true },
        "svp" => new TradingHub.Indicators.Native.SessionVolumeProfile(),
        "dib" => new TradingHub.Indicators.Native.VpUltimate.VpDoubleInside(),
        _ => new TradingHub.Indicators.Native.Flow.TrappedTraders(),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Las_lineas_que_dibuja_cada_indicador_se_pueden_alertar(string kind, bool mustHave)
    {
        var data = VpUltimateTests.Market(8);
        var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        var ind = Make(kind);
        model.AddIndicator(ind);
        for (int k = 0; k < 2; k++) model.Update();
        var levels = ind.GetAlertLevels().ToList();
        if (mustHave) Assert.NotEmpty(levels);
        Assert.Equal(levels.Count, levels.Select(l => l.Id).Distinct().Count());
        Assert.All(levels, l =>
        {
            Assert.True(l.Price > 0, l.Name);
            Assert.True(l.FromBar >= 0 && l.FromBar < model.Series.Count, l.Name);
            Assert.True(l.ToBar >= l.FromBar, l.Name);
            Assert.False(string.IsNullOrWhiteSpace(l.Name));
        });

        // las ofrece el diálogo de alertas, dan su precio y siguen ahí (mismo Id) después de recalcular
        var ops = ChartAlertEngine.Lines(model).Where(o => o.Series!.StartsWith(ChartAlertEngine.LevelPrefix)).ToList();
        Assert.Equal(levels.Count, ops.Count);
        foreach (var lv in levels.Take(20))
        {
            var op = ops.First(o => o.Series == ChartAlertEngine.LevelPrefix + lv.Id);
            Assert.True(ChartAlertEngine.TryValue(model, op, lv.FromBar, out var v));
            Assert.Equal(lv.Price, v);
        }

        ind.RecalculateValues();
        model.Update();
        var again = ind.GetAlertLevels().Select(l => l.Id).ToHashSet();
        Assert.All(levels.Take(20), l => Assert.Contains(l.Id, again));

        // el clic derecho sobre una de ellas la captura
        if (levels.Count == 0) return;
        var pick = levels[^1];
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1200, 700));
        model.Viewport.AutoScale = false;
        model.Viewport.PriceTop = (double)pick.Price + 50;
        model.Viewport.PriceBottom = (double)pick.Price - 50;
        new TradingHub.Charting.Rendering.ChartRenderer().Render(surface.Canvas, model, 1200, 700, new TradingHub.Charting.Rendering.ChartRenderState());
        var layout = model.LastLayout!;
        int at = Math.Min(pick.ToBar == int.MaxValue ? model.Series.Count - 1 : pick.ToBar, model.Series.Count - 1);
        float x = layout.CenterX(Math.Max(pick.FromBar, at));
        float y = ((TradingHub.Indicators.IChartContainer)layout.Main).GetYByPrice(pick.Price, false);
        var hit = ChartAlertEngine.LineAt(model, x, y, 10);
        Assert.NotNull(hit);
        Assert.StartsWith(ChartAlertEngine.LevelPrefix, hit!.Series);
    }
}
