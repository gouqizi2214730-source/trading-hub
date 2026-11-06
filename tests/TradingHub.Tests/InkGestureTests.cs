using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Dibujar con un gesto (mantener apretado y trazar la forma, como en OpenMarket) y los dibujos nuevos.</summary>
public class InkGestureTests
{
    private const float Right = 900;

    private static List<SKPoint> Line(float x0, float y0, float x1, float y1, int n = 40)
    {
        var res = new List<SKPoint>();
        for (int i = 0; i <= n; i++) res.Add(new SKPoint(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n));
        return res;
    }

    private static List<SKPoint> Join(params List<SKPoint>[] parts)
    {
        var res = new List<SKPoint>();
        foreach (var p in parts) res.AddRange(res.Count > 0 ? p.Skip(1) : p);
        return res;
    }

    private static InkShapeKind? Kind(List<SKPoint> pts) => InkRecognizer.Recognize(pts, Right)?.Kind;

    [Fact]
    public void Una_raya_horizontal_es_una_linea_horizontal_en_el_promedio()
    {
        var pts = Line(100, 200, 400, 203);
        var s = InkRecognizer.Recognize(pts, Right)!;
        Assert.Equal(InkShapeKind.Horizontal, s.Kind);
        Assert.InRange(s.Level!.Value, 200, 203);
    }

    [Fact]
    public void Rayas_y_diagonales()
    {
        Assert.Equal(InkShapeKind.Vertical, Kind(Line(300, 100, 302, 400)));
        Assert.Equal(InkShapeKind.TrendLine, Kind(Line(100, 400, 500, 150)));
        Assert.Equal(InkShapeKind.Ray, Kind(Line(100, 400, Right, 150)));
        Assert.Null(InkRecognizer.Recognize(Line(100, 100, 108, 104), Right)); // muy corto: solo se mantuvo apretado
    }

    [Fact]
    public void Un_circulo_es_una_elipse_y_una_caja_un_rectangulo()
    {
        var circle = new List<SKPoint>();
        for (int i = 0; i <= 60; i++)
        {
            double a = i / 60.0 * Math.PI * 2;
            circle.Add(new SKPoint(300 + (float)Math.Cos(a) * 120, 250 + (float)Math.Sin(a) * 70));
        }

        var e = InkRecognizer.Recognize(circle, Right)!;
        Assert.Equal(InkShapeKind.Ellipse, e.Kind);
        Assert.InRange(e.A.X, 175, 185);
        Assert.InRange(e.B.Y, 315, 325);

        var box = Join(Line(100, 100, 400, 100), Line(400, 100, 400, 300), Line(400, 300, 100, 300), Line(100, 300, 100, 104));
        Assert.Equal(InkShapeKind.Rectangle, Kind(box));
    }

    [Fact]
    public void Una_raya_con_ganchito_es_una_flecha_y_la_punta_es_lo_mas_lejano()
    {
        var arrow = Join(Line(100, 400, 500, 150), Line(500, 150, 470, 175, 6));
        var s = InkRecognizer.Recognize(arrow, Right)!;
        Assert.Equal(InkShapeKind.Arrow, s.Kind);
        Assert.InRange(s.B.X, 490, 505);
    }

    [Fact]
    public void Una_z_es_un_fibonacci_entre_la_primera_y_la_ultima_raya()
    {
        var z = Join(Line(100, 100, 400, 100), Line(400, 100, 110, 300), Line(110, 300, 420, 300));
        var s = InkRecognizer.Recognize(z, Right)!;
        Assert.Equal(InkShapeKind.Fib, s.Kind);
        Assert.InRange(s.A.Y, 95, 110);
        Assert.InRange(s.B.Y, 290, 305);

        // de ida y vuelta también
        var back = Join(Line(100, 100, 400, 100), Line(400, 100, 120, 300), Line(120, 300, 380, 300));
        Assert.Equal(InkShapeKind.Fib, Kind(back));
    }

    [Fact]
    public void Un_garabato_borra_y_otra_forma_queda_como_trayectoria()
    {
        var scribble = new List<SKPoint>();
        for (int i = 0; i < 8; i++) scribble.AddRange(Line(i % 2 == 0 ? 200 : 300, 200 + i * 4, i % 2 == 0 ? 300 : 200, 202 + i * 4, 10));
        Assert.Equal(InkShapeKind.Scribble, Kind(scribble));

        var w = Join(Line(100, 100, 200, 300), Line(200, 300, 300, 120), Line(300, 120, 420, 330));
        Assert.Equal(InkShapeKind.Path, Kind(w));
    }

    [Fact]
    public void Cambiar_el_tipo_usa_el_mismo_trazo()
    {
        var t0 = new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc);
        var g = new InkGeometry
        {
            Start = new ChartPoint(t0, 100),
            End = new ChartPoint(t0.AddHours(2), 110),
            Corners = new[] { new ChartPoint(t0.AddHours(1), 120) },
            Raw = new[] { new ChartPoint(t0, 100), new ChartPoint(t0.AddMinutes(30), 105), new ChartPoint(t0.AddHours(2), 110) },
        };
        Assert.Equal(2, InkDrawings.PointsFor(DrawingKind.Ray, g).Count);
        Assert.Single(InkDrawings.PointsFor(DrawingKind.HorizontalLine, g));
        Assert.Equal(100, InkDrawings.PointsFor(DrawingKind.HorizontalLine, g)[0].Price);
        Assert.Equal(3, InkDrawings.PointsFor(DrawingKind.Path, g).Count);
        Assert.Equal(3, InkDrawings.PointsFor(DrawingKind.Brush, g).Count);
        Assert.Contains(DrawingKind.Ray, InkDrawings.Alternatives(DrawingKind.TrendLine));
        Assert.Contains(DrawingKind.Ellipse, InkDrawings.Alternatives(DrawingKind.Rectangle));
    }

    [Fact]
    public void Los_tipos_nuevos_van_al_final_y_no_cambian_los_numeros_guardados()
    {
        Assert.Equal(14, (int)DrawingKind.ParallelChannel);
        Assert.Equal(15, (int)DrawingKind.Ellipse);
        Assert.Equal(20, (int)DrawingKind.CrossLine);
    }

    // ------------------------------------------------------------------ con un gráfico de verdad

    private static (ChartModel Model, ChartController Controller, ChartLayout Layout) Chart()
    {
        var data = VpUltimateTests.Market(4);
        var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        model.Update();
        var state = new ChartRenderState();
        using var surface = SKSurface.Create(new SKImageInfo(1000, 600));
        var layout = new ChartRenderer().Render(surface.Canvas, model, 1000, 600, state);
        return (model, new ChartController(model, state) { InkGestures = true }, layout);
    }

    private static void Hold(ChartController c, float x, float y)
    {
        c.MouseDown(x, y, PointerButton.Left, 1, InputModifiers.None);
        Thread.Sleep(InkRecognizer.HoldMs + 60);
        Assert.True(c.InkTick());
    }

    [Fact]
    public void Mantener_apretado_y_trazar_deja_el_dibujo_y_avisa()
    {
        var (m, c, l) = Chart();
        InkResult? result = null;
        c.InkFinished += r => result = r;
        float y = l.Main.Rect.MidY;
        Hold(c, 200, y);
        foreach (var p in Line(200, y, 500, y + 2).Skip(1)) c.MouseMove(p.X, p.Y, InputModifiers.None);
        c.MouseUp(500, y + 2, PointerButton.Left, InputModifiers.None);

        var d = Assert.Single(m.Drawings);
        Assert.Equal(DrawingKind.HorizontalLine, d.Kind);
        Assert.True(d.Selected);
        Assert.NotNull(result);
        Assert.Same(d, result!.Drawing);
        Assert.Contains(DrawingKind.HorizontalRay, result.Alternatives);

        // la barrita: mismo trazo como línea de tendencia
        c.RetypeInk(d, result.Geometry!, DrawingKind.TrendLine);
        Assert.Equal(DrawingKind.TrendLine, d.Kind);
        Assert.Equal(2, d.Points.Count);
    }

    [Fact]
    public void Moverse_antes_de_tiempo_es_arrastrar_el_grafico()
    {
        var (m, c, l) = Chart();
        float y = l.Main.Rect.MidY;
        c.MouseDown(200, y, PointerButton.Left, 1, InputModifiers.None);
        c.MouseMove(260, y, InputModifiers.None);
        Thread.Sleep(InkRecognizer.HoldMs + 60);
        c.InkTick();
        Assert.False(c.InkPending);
        c.MouseUp(260, y, PointerButton.Left, InputModifiers.None);
        Assert.Empty(m.Drawings);
    }

    [Fact]
    public void Un_garabato_encima_borra_y_se_puede_deshacer()
    {
        var (m, c, l) = Chart();
        float y = l.Main.Rect.MidY;
        var line = new ChartDrawing { Kind = DrawingKind.HorizontalLine };
        line.Points.Add(new ChartPoint(m.Series[^10].Time, l.Main.ValueAt(y)));
        m.Drawings.Add(line);
        InkResult? result = null;
        c.InkFinished += r => result = r;
        Hold(c, 300, y - 20);
        for (int i = 0; i < 8; i++)
            foreach (var p in Line(i % 2 == 0 ? 300 : 420, y - 20 + i * 5, i % 2 == 0 ? 420 : 300, y - 18 + i * 5, 10)) c.MouseMove(p.X, p.Y, InputModifiers.None);
        c.MouseUp(300, y + 20, PointerButton.Left, InputModifiers.None);

        Assert.Empty(m.Drawings);
        Assert.True(result!.Erase);
        Assert.Single(result.Removed);
        c.UndoErase(result.Removed);
        Assert.Same(line, Assert.Single(m.Drawings));
    }

    [Fact]
    public void Pincel_trayectoria_y_medir_con_shift()
    {
        var (m, c, l) = Chart();
        float y = l.Main.Rect.MidY;

        // pincel: los puntos salen al arrastrar
        c.ActiveTool = DrawingKind.Brush;
        c.MouseDown(100, y, PointerButton.Left, 1, InputModifiers.None);
        for (int i = 1; i <= 30; i++) c.MouseMove(100 + i * 6, y + (float)Math.Sin(i / 3.0) * 30, InputModifiers.None);
        c.MouseUp(280, y, PointerButton.Left, InputModifiers.None);
        var brush = Assert.Single(m.Drawings);
        Assert.Equal(DrawingKind.Brush, brush.Kind);
        Assert.True(brush.Points.Count > 5);
        Assert.True(brush.HitTest(l, m.Series, 100, y, out _));

        // trayectoria: clic, clic, clic y doble clic
        c.ActiveTool = DrawingKind.Path;
        c.MouseDown(400, y, PointerButton.Left, 1, InputModifiers.None);
        c.MouseUp(400, y, PointerButton.Left, InputModifiers.None);
        Assert.True(c.TryCompletePending(450, y - 40, InputModifiers.None, 1));
        Assert.True(c.TryCompletePending(500, y + 30, InputModifiers.None, 1));
        Assert.True(c.TryCompletePending(500, y + 30, InputModifiers.None, 2));
        var path = m.Drawings[^1];
        Assert.Equal(DrawingKind.Path, path.Kind);
        Assert.Equal(3, path.Points.Count);
        Assert.False(c.HasPending);

        // Shift + arrastre mide sin guardar nada
        int before = m.Drawings.Count;
        c.MouseDown(600, y, PointerButton.Left, 1, InputModifiers.Shift);
        c.MouseMove(700, y - 50, InputModifiers.Shift);
        c.MouseUp(700, y - 50, PointerButton.Left, InputModifiers.Shift);
        Assert.Equal(before, m.Drawings.Count);
    }

    [Fact]
    public void Elipse_y_linea_cruzada_se_tocan_por_dentro_y_por_las_dos_rayas()
    {
        var (m, _, l) = Chart();
        float y = l.Main.Rect.MidY;
        var e = new ChartDrawing { Kind = DrawingKind.Ellipse };
        e.Points.Add(new ChartPoint(ChartDrawing.TimeAt(l, m.Series, 200), l.Main.ValueAt(y - 50)));
        e.Points.Add(new ChartPoint(ChartDrawing.TimeAt(l, m.Series, 400), l.Main.ValueAt(y + 50)));
        Assert.True(e.HitTest(l, m.Series, 300, y, out _));
        Assert.False(e.HitTest(l, m.Series, 210, y - 40, out _)); // cerca de la esquina del recuadro queda afuera

        var cross = new ChartDrawing { Kind = DrawingKind.CrossLine };
        cross.Points.Add(new ChartPoint(ChartDrawing.TimeAt(l, m.Series, 500), l.Main.ValueAt(y)));
        Assert.True(cross.HitTest(l, m.Series, 100, y, out _));
        Assert.True(cross.HitTest(l, m.Series, ChartDrawing.XOf(l, m.Series, cross.Points[0].Time), y - 100, out _));
        Assert.Equal(cross.Points[0].Price, cross.ValueAt(m.Series, 3, "line"));
    }
}
