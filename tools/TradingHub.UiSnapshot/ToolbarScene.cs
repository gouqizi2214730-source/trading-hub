// Escena "barras": la barra del gráfico, la de dibujos y la barrita flotante del dibujo elegido (como TradingView y
// OpenMarket): imán, seguir dibujando, bloquear y ocultar; un dibujo elegido muestra su barrita; favoritos de indicadores.
using Avalonia.Controls;
using Avalonia.Headless;
using TradingHub.App;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;

internal static class ToolbarScene
{
    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "OK " : "FALLA")}  {what}");
            if (!ok) failures++;
        }

        void Shot(string name)
        {
            pump(1);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"));
        }

        window.Navigate("trading");
        pump(1);
        var tv = window.Trading;
        var tab = tv.ActiveTab!;
        var model = tab.Model!;
        Check(tab.Tools != null && tab.DrawingBar != null, "cada gráfico tiene su barra de dibujos y su barrita flotante");

        // un dibujo elegido: aparece la barrita
        var last = model.Series[^1];
        double px = (double)model.Series.TicksToPrice(last.CloseTicks);
        var line = new ChartDrawing { Kind = DrawingKind.HorizontalLine, Points = { new ChartPoint(last.Time, px) }, Selected = true, ColorArgb = 0xFFF2B53A, Width = 2 };
        model.Drawings.Add(line);
        model.Invalidate();
        pump(0.5);
        Check(tab.DrawingBar!.IsVisible, "con un dibujo elegido se ve la barrita flotante");
        Shot("barras_dibujo_elegido");

        // bloquear y ocultar desde la barra de dibujos (por gráfico) e imán (para todos)
        model.Settings.LockDrawings = true;
        tv.SyncDrawingTools(tab);
        pump(0.3);
        Shot("barras_bloqueado");
        model.Settings.LockDrawings = false;
        model.Settings.HideDrawings = true;
        foreach (var d in model.Drawings) d.Selected = false;
        model.Invalidate();
        pump(0.5);
        Check(!tab.DrawingBar.IsVisible, "con los dibujos ocultos no hay barrita");
        model.Settings.HideDrawings = false;
        App.Services.Settings.DrawingMagnet = MagnetMode.Strong;
        tab.View.Magnet = MagnetMode.Strong;
        tv.SyncDrawingTools(tab);
        Check(tab.View.Controller?.Magnet == MagnetMode.Strong, "el imán llega al gráfico");
        App.Services.Settings.DrawingMagnet = MagnetMode.Off;
        tab.View.Magnet = MagnetMode.Off;
        tv.SyncDrawingTools(tab);

        // favoritos: la estrella agrega el indicador de un clic
        var info = App.Services.Catalog.Items.First(i => i.Type.Name == "VpProfile");
        App.Services.Settings.FavoriteIndicators.Clear();
        App.Services.Settings.FavoriteIndicators.Add(info.Type.FullName!);
        Check(tv.FavoriteIndicators().Count == 1, "los favoritos salen del catálogo");
        int before = model.Indicators.Count;
        tv.AddIndicator(tab, info);
        Check(model.Indicators.Count == before + 1, "agregar un favorito lo pone en el gráfico");
        App.Services.Settings.FavoriteIndicators.Clear();
        model.Drawings.Remove(line);
        model.Invalidate();
        Shot("barras_general");

        // los dibujos nuevos: elipse, flecha, trayectoria, pincel, resaltador y línea cruzada
        var l = model.LastLayout!;
        var main = l.Main.Rect;
        ChartPoint P(float fx, float fy) => new(ChartDrawing.TimeAt(l, model.Series, main.Left + main.Width * fx), l.Main.ValueAt(main.Top + main.Height * fy));
        var shapes = new List<ChartDrawing>
        {
            new() { Kind = DrawingKind.Ellipse, Points = { P(0.10f, 0.15f), P(0.28f, 0.40f) }, ColorArgb = 0xFF38BDF8 },
            new() { Kind = DrawingKind.Arrow, Points = { P(0.33f, 0.45f), P(0.50f, 0.20f) }, ColorArgb = 0xFFF2B53A, Width = 2 },
            new() { Kind = DrawingKind.Path, Points = { P(0.55f, 0.60f), P(0.62f, 0.35f), P(0.70f, 0.55f), P(0.78f, 0.30f) }, ColorArgb = 0xFFB478FF, Width = 2 },
            new() { Kind = DrawingKind.CrossLine, Points = { P(0.85f, 0.70f) }, ColorArgb = 0xFF26C281 },
        };
        var brush = new ChartDrawing { Kind = DrawingKind.Brush, ColorArgb = 0xFFF472B6, Width = 2 };
        var hl = new ChartDrawing { Kind = DrawingKind.Highlighter, ColorArgb = 0xFFF2B53A, Width = 2 };
        for (int i = 0; i <= 40; i++)
        {
            brush.Points.Add(P(0.10f + i * 0.006f, 0.70f + (float)Math.Sin(i / 4.0) * 0.06f));
            hl.Points.Add(P(0.40f + i * 0.007f, 0.82f + (float)Math.Sin(i / 9.0) * 0.02f));
        }

        shapes.Add(brush);
        shapes.Add(hl);
        model.Drawings.AddRange(shapes);
        model.Invalidate();
        Shot("barras_dibujos_nuevos");
        Check(shapes.All(d => d.HitTest(l, model.Series, d.ScreenPoints(l, model.Series)[0].X, d.ScreenPoints(l, model.Series)[0].Y, out _)), "los dibujos nuevos se tocan en su primer punto");
        foreach (var d in shapes) model.Drawings.Remove(d);

        // dibujar con un gesto: mantener apretado, el anillo, el trazo y la barrita para cambiar el tipo
        var c = tab.View.Controller!;
        Check(c.InkGestures, "dibujar con un gesto viene prendido");
        float y0 = main.Top + main.Height * 0.35f, x0 = main.Left + main.Width * 0.25f;
        c.MouseDown(x0, y0, PointerButton.Left, 1, InputModifiers.None);
        Thread.Sleep(InkRecognizer.HoldMs / 2);
        c.InkTick();
        model.Invalidate();
        Shot("barras_gesto_anillo");
        Thread.Sleep(InkRecognizer.HoldMs);
        Check(c.InkTick() && c.InkPending, "a los 320 ms sin moverse empieza el gesto");
        for (int i = 1; i <= 30; i++) c.MouseMove(x0 + i * 12, y0 - i * 5, InputModifiers.None);
        model.Invalidate();
        Shot("barras_gesto_trazando");
        c.MouseUp(x0 + 360, y0 - 150, PointerButton.Left, InputModifiers.None);
        pump(0.4);
        var made = model.Drawings.LastOrDefault();
        Check(made?.Kind == DrawingKind.TrendLine, "una diagonal queda como línea de tendencia");
        Check(tab.InkChip?.IsVisible == true, "al soltar aparece la barrita del gesto");
        Shot("barras_gesto_listo");

        // un garabato encima la borra
        float sx = x0 + 150, sy = y0 - 62;
        c.MouseDown(sx, sy - 30, PointerButton.Left, 1, InputModifiers.None);
        Thread.Sleep(InkRecognizer.HoldMs + 60);
        c.InkTick();
        for (int k = 0; k < 8; k++)
            for (int i = 0; i <= 10; i++)
            {
                float t = i / 10f;
                float xx = k % 2 == 0 ? sx - 50 + 100 * t : sx + 50 - 100 * t;
                c.MouseMove(xx, sy - 30 + k * 8 + t * 2, InputModifiers.None);
            }

        model.Invalidate();
        Shot("barras_gesto_garabato");
        c.MouseUp(sx, sy + 34, PointerButton.Left, InputModifiers.None);
        pump(0.4);
        Check(made != null && !model.Drawings.Contains(made), "el garabato borró la línea");
        Shot("barras_gesto_borrado");

        // herramientas favoritas juntas en la barra
        App.Services.Settings.FavoriteDrawingTools = new List<string> { "Brush", "Ellipse", "FibRetracement" };
        tv.SyncDrawingTools(tab);
        pump(0.3);
        Shot("barras_favoritas");
        App.Services.Settings.FavoriteDrawingTools = new List<string>();
        tv.SyncDrawingTools(tab);

        // la ayuda de los gestos
        TradingHub.App.Views.DrawingToolStrip.ShowGuide(tab.Tools!);
        pump(0.5);
        Shot("barras_gestos_ayuda");
        return failures;
    }
}
