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
        return failures;
    }
}
