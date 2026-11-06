using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Material.Icons;
using SkiaSharp;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Indicators;

namespace TradingHub.App.Views;

// Lo que hacen los botones nuevos de la barra del gráfico (favoritos, alerta, replay, captura y pantalla completa).
public sealed partial class TradingView
{
    /// <summary>Los indicadores favoritos que existen en el catálogo (en el orden en que se marcaron).</summary>
    internal IReadOnlyList<IndicatorInfo> FavoriteIndicators() =>
        _svc.Settings.FavoriteIndicators.Select(t => _svc.Catalog.Items.FirstOrDefault(i => i.Type.FullName == t)).OfType<IndicatorInfo>().ToList();

    /// <summary>Agrega un indicador del catálogo al gráfico (con su plantilla predeterminada, si tiene).</summary>
    internal void AddIndicator(ChartTab? chart, IndicatorInfo info)
    {
        if (chart?.Model == null) return;
        try
        {
            var ind = (Indicator)Activator.CreateInstance(info.Type)!;
            bool templated = Workspace.ApplyDefaultIndicatorTemplate(_svc, ind);
            chart.Model.AddIndicator(ind, applyDefaultColors: !templated);
            chart.Model.Invalidate();
            Workspace.Save(this);
            _toolbar.Sync();
            _status($"«{Indicator.DefaultName(info.Type)}» agregado");
        }
        catch (Exception ex)
        {
            _status($"No se pudo crear {Indicator.DefaultName(info.Type)}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    /// <summary>El menú de la estrella: los favoritos para agregar de un clic y las plantillas del gráfico.</summary>
    internal void ShowFavoritesMenu(ChartTab? chart, Control anchor)
    {
        if (chart?.Model == null) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var favorites = FavoriteIndicators();
        menu.Items.Add(new MenuItem { Header = "Indicadores favoritos", IsEnabled = false, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        if (favorites.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Todavía no hay: en Indicadores, clic derecho → «Agregar a favoritos»", IsEnabled = false });
        foreach (var f in favorites)
        {
            var info = f;
            var item = new MenuItem { Header = Indicator.DefaultName(info.Type), Icon = Ui.Icon(MaterialIconKind.Star, 15, Ui.Accent) };
            item.Click += (_, _) => AddIndicator(chart, info);
            menu.Items.Add(item);
        }

        var open = new MenuItem { Header = "Biblioteca de indicadores…", Icon = Ui.Icon(MaterialIconKind.FunctionVariant, 16) };
        open.Click += (_, _) => OpenIndicators(chart);
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Plantillas de gráfico", IsEnabled = false, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        foreach (var i in ChartTemplateItems(chart)) menu.Items.Add(i);
        menu.ShowAt(anchor);
    }

    /// <summary>Alerta nueva con el último precio (se ajusta en la ventana de alertas).</summary>
    internal void CreatePriceAlert(ChartTab? chart)
    {
        if (chart?.Model == null) return;
        decimal last = chart.Model.Data.LastPrice;
        _ = OpenAlertsAsync(chart, AlertOperand.Price("close", "Precio (cierre)"), AlertOperand.Fixed(chart.Model.Instrument.RoundPrice(last)), null, AlertCondition.Crossing);
    }

    /// <summary>El menú del replay (práctica al azar o a ciegas).</summary>
    internal void ShowReplayMenu(ChartTab? chart, Control anchor)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var random = new MenuItem { Header = "Replay al azar (viendo el instrumento y la fecha)", Icon = Ui.Icon(MaterialIconKind.PlayCircleOutline, 16) };
        random.Click += (_, _) => _ = OpenRandomReplayAsync(blind: false);
        var blind = new MenuItem { Header = "Replay a ciegas (sin instrumento ni fecha)", Icon = Ui.Icon(MaterialIconKind.EyeOffOutline, 16) };
        blind.Click += (_, _) => _ = OpenRandomReplayAsync(blind: true);
        menu.Items.Add(random);
        menu.Items.Add(blind);
        if (chart is { IsReplay: true, Model.Settings.BlindReplay: true })
        {
            var reveal = new MenuItem { Header = "Revelar este replay", Icon = Ui.Icon(MaterialIconKind.EyeOutline, 16) };
            reveal.Click += (_, _) => RevealReplay(chart);
            menu.Items.Add(reveal);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Para un día elegido: Datos → elegí el día → Reproducir", IsEnabled = false });
        menu.ShowAt(anchor);
    }

    /// <summary>Guarda una imagen del gráfico (PNG, al doble de resolución) en Imágenes/KVP y dice dónde quedó.</summary>
    internal void SaveChartImage(ChartTab? chart)
    {
        if (chart?.Model == null) return;
        try
        {
            var size = chart.View.Bounds.Size;
            int w = Math.Max(200, (int)size.Width), h = Math.Max(150, (int)size.Height);
            const float scale = 2;
            using var surface = SKSurface.Create(new SKImageInfo((int)(w * scale), (int)(h * scale)));
            surface.Canvas.Scale(scale);
            new ChartRenderer().Render(surface.Canvas, chart.Model, w, h, new ChartRenderState());
            using var img = surface.Snapshot();
            using var png = img.Encode(SKEncodedImageFormat.Png, 95);
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pictures)) pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dir = Path.Combine(pictures, TradingHub.Common.Product.Name);
            Directory.CreateDirectory(dir);
            string safe = string.Concat(chart.Model.Instrument.Symbol.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string path = Path.Combine(dir, $"{safe}_{chart.Model.Period.DisplayName}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            File.WriteAllBytes(path, png.ToArray());
            _status($"Captura guardada: {path}");
            chart.Model.Invalidate();
        }
        catch (Exception ex)
        {
            _status("No se pudo guardar la captura: " + ex.Message);
        }
    }

    /// <summary>Pantalla completa de la ventana del gráfico (sale con el mismo botón o Esc del sistema).</summary>
    internal void ToggleFullScreen(ChartTab? chart)
    {
        var window = chart?.Window ?? TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;
        window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }
}
