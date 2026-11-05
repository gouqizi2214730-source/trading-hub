using System.Drawing;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;
using Xunit;

namespace TradingHub.Tests;

/// <summary>
/// Un indicador propio cualquiera (como uno recién escrito): dibuja líneas con DrawLine / DrawLines / DrawRectangle y no
/// declara nada para las alertas. El gráfico las captura al dibujarlo y se pueden alertar igual.
/// </summary>
public class PlainLinesIndicator : Indicator
{
    public decimal Level { get; set; }
    public bool Draw { get; set; } = true;

    public PlainLinesIndicator() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("X", "x") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    protected override void OnCalculate(int bar, decimal value) { }

    public decimal Curve(int bar) => Math.Round(Level - 700 + 60 * (decimal)Math.Sin(bar / 3.0), 1);

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar < 40 || !Draw) return;
        var pc = ChartInfo.PriceChartContainer;
        int last = CurrentBar - 1;
        var font = new RenderFont("Arial", 8);
        // una horizontal desde la vela last-30 hasta el borde derecho, con su nombre al lado
        int y = ChartInfo.GetYByPrice(Level, false);
        context.DrawLine(new RenderPen(Color.Orange, 2), ChartInfo.GetXByBar(last - 30), y, pc.Region.Right, y);
        context.DrawString("Mi nivel", font, Color.Orange, ChartInfo.GetXByBar(last - 30), y - 14);
        // una inclinada de dos puntos que llega a la última vela
        context.DrawLine(new RenderPen(Color.Cyan, 1), ChartInfo.GetXByBar(last - 20, false), ChartInfo.GetYByPrice(Level - 500, false),
            ChartInfo.GetXByBar(last, false), ChartInfo.GetYByPrice(Level - 300, false));
        // una caja con borde (zona) que sigue a la derecha
        int yt = ChartInfo.GetYByPrice(Level + 300, false), yb = ChartInfo.GetYByPrice(Level + 200, false);
        context.DrawRectangle(new RenderPen(Color.Magenta, 1), new Rectangle(ChartInfo.GetXByBar(last - 15), yt, pc.Region.Right - ChartInfo.GetXByBar(last - 15), yb - yt));
        // una curva dibujada vela a vela con DrawLine (muchos tramos que se tocan) y otra de una vez con DrawLines
        for (int b = last - 25; b < last; b++)
            context.DrawLine(new RenderPen(Color.Lime, 1), ChartInfo.GetXByBar(b, false), ChartInfo.GetYByPrice(Curve(b), false),
                ChartInfo.GetXByBar(b + 1, false), ChartInfo.GetYByPrice(Curve(b + 1), false));
        var pts = Enumerable.Range(last - 25, 26).Select(b => new Point(ChartInfo.GetXByBar(b, false), ChartInfo.GetYByPrice(Curve(b) - 400, false))).ToArray();
        context.DrawLines(new RenderPen(Color.Yellow, 1), pts);
        // una etiqueta con recuadro (no es una línea de precio) y una marca chica
        context.DrawString("etiqueta", font, Color.White, ChartInfo.GetXByBar(last - 5), y + 30);
        context.DrawRectangle(new RenderPen(Color.White, 1), new Rectangle(ChartInfo.GetXByBar(last - 5) - 3, y + 28, 50, 16));
        context.DrawLine(new RenderPen(Color.Red, 1), ChartInfo.GetXByBar(last - 3) - 3, y - 50, ChartInfo.GetXByBar(last - 3) + 3, y - 50);
    }
}

public class DrawnLineAlertTests
{
    private static (ChartModel Model, PlainLinesIndicator Ind, IndicatorSlot Slot) Setup()
    {
        var data = VpUltimateTests.Market(4);
        var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        var ind = new PlainLinesIndicator();
        model.AddIndicator(ind);
        model.Update();
        var c = model.Series[^1];
        ind.Level = model.Instrument.RoundPrice(model.Instrument.FromTicks(c.CloseTicks) + 150.25m);
        model.Viewport.BarWidth = 12;
        model.Viewport.AutoScale = false;
        model.Viewport.PriceTop = (double)ind.Level + 800;
        model.Viewport.PriceBottom = (double)ind.Level - 1200;
        Render(model);
        return (model, ind, model.Indicators.Single(s => ReferenceEquals(s.Indicator, ind)));
    }

    private static void Render(ChartModel model)
    {
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1200, 700));
        new ChartRenderer().Render(surface.Canvas, model, 1200, 700, new ChartRenderState());
    }

    [Fact]
    public void Las_lineas_de_un_indicador_que_no_declara_nada_se_capturan()
    {
        var (model, ind, slot) = Setup();
        var lines = slot.DrawnLines;
        uint orange = (uint)Color.Orange.ToArgb(), cyan = (uint)Color.Cyan.ToArgb(), magenta = (uint)Color.Magenta.ToArgb();
        // la horizontal, con el precio exacto (no el del píxel) y su nombre
        var h = Assert.Single(lines, l => l.Color == orange);
        Assert.Equal(ind.Level, h.LastPrice);
        Assert.Equal("Mi nivel", h.Text);
        Assert.True(h.Right);
        // la inclinada y los dos bordes de la caja
        Assert.Single(lines, l => l.Color == cyan);
        Assert.Equal(2, lines.Count(l => l.Color == magenta));
        // cada curva es una sola línea (no un pedazo por vela) con un punto por vela
        var lime = Assert.Single(lines, l => l.Color == (uint)Color.Lime.ToArgb());
        Assert.Equal(26, lime.Prices.Length);
        Assert.Single(lines, l => l.Color == (uint)Color.Yellow.ToArgb());
        // ni el recuadro de la etiqueta ni la marca chica
        Assert.DoesNotContain(lines, l => l.Color == (uint)Color.White.ToArgb());
        Assert.DoesNotContain(lines, l => l.Color == (uint)Color.Red.ToArgb());

        // en el diálogo de alertas
        var ops = ChartAlertEngine.Lines(model).Where(o => o.Line != null).ToList();
        Assert.Contains(ops, o => o.Label.Contains("Mi nivel"));
        Assert.Equal(6, ops.Count);
    }

    [Fact]
    public void Clic_derecho_la_captura_y_la_alerta_la_sigue()
    {
        var (model, ind, slot) = Setup();
        var layout = model.LastLayout!;
        int last = model.Series.Count - 1;
        float y = ((IChartContainer)layout.Main).GetYByPrice(ind.Level, false);
        var hit = ChartAlertEngine.LineAt(model, layout.CenterX(last - 10), y + 3, 10);
        Assert.NotNull(hit);
        Assert.NotNull(hit!.Line);
        Assert.Contains("Mi nivel", hit.Label);
        Assert.True(ChartAlertEngine.TryValue(model, hit, last, out var v));
        Assert.Equal(ind.Level, v);
        Assert.False(ChartAlertEngine.TryValue(model, hit, last - 40, out _)); // antes de que empiece

        // resaltada
        model.HighlightedLine = hit;
        Render(model);
        Assert.NotEmpty(ChartAlertEngine.PixelsOf(model, layout, hit));

        // la inclinada: interpola y se prolonga
        var slope = ChartAlertEngine.Lines(model).First(o => o.Line?.Color == (uint)Color.Cyan.ToArgb());
        Assert.True(ChartAlertEngine.TryValue(model, slope, last - 10, out var mid));
        Assert.True(ChartAlertEngine.TryValue(model, slope, last, out var end));
        Assert.True(end > mid);

        // las curvas: el valor de la curva en cada vela
        foreach (var color in new[] { Color.Lime, Color.Yellow })
        {
            var curve = ChartAlertEngine.Lines(model).First(o => o.Line?.Color == (uint)color.ToArgb());
            for (int b = last - 25; b <= last; b += 5)
            {
                Assert.True(ChartAlertEngine.TryValue(model, curve, b, out var cv));
                Assert.Equal(ind.Curve(b) - (color == Color.Yellow ? 400 : 0), cv);
            }
        }

        // una alerta que toca, con su copia guardada en JSON
        var alert = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.Crossing, Right = hit.Copy() };
        var json = System.Text.Json.JsonSerializer.Serialize(alert);
        var back = System.Text.Json.JsonSerializer.Deserialize<ChartAlert>(json)!;
        Assert.NotNull(back.Right.Line);
        Assert.True(ChartAlertEngine.TryValue(model, back.Right, last, out var v2));
        Assert.Equal(ind.Level, v2);
        model.Alerts.Add(back);
        Assert.NotNull(ChartAlertEngine.Check(model, back, last));

        // el indicador la mueve un poco (un nivel en formación): la alerta la sigue
        ind.Level += 40;
        Render(model);
        Assert.True(ChartAlertEngine.TryValue(model, back.Right, last, out var moved));
        Assert.Equal(ind.Level, moved);

        // si deja de dibujarse (otra parte de la historia, panel oculto…) queda su última forma
        ind.Draw = false;
        Render(model);
        Assert.Empty(slot.DrawnLines);
        Assert.True(ChartAlertEngine.TryValue(model, back.Right, last, out var kept));
        Assert.Equal(ind.Level, kept);
    }
}
