using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.Charting;

namespace TradingHub.App.Windows;

/// <summary>
/// Configuración de un gráfico: colores del fondo, las velas, los ejes, el precio, el footprint y las órdenes, y qué
/// se muestra. Cada gráfico tiene la suya (los gráficos nuevos arrancan con la del gráfico activo).
/// </summary>
internal sealed class ChartSettingsWindow : Window
{
    private static readonly uint[] Palette =
    {
        0xFF000000, 0xFF01000C, 0xFF06152A, 0xFF0B1220, 0xFF131722, 0xFF1E222D, 0xFF2A2E39, 0xFF434651, 0xFF787B86, 0xFFB2B5BE, 0xFFFFFFFF,
        0xFF49D149, 0xFF26A69A, 0xFF089981, 0xFF22D3EE, 0xFF1E90FF, 0xFF2962FF, 0xFFB478FF, 0xFFEC4899, 0xFFE84548, 0xFFF23645, 0xFFF97316, 0xFFF2B53A,
    };

    private readonly TradingView _host;
    private readonly ChartTab _chart;
    private readonly ChartModel _model;
    private readonly List<Action> _refreshers = new();

    public ChartSettingsWindow(TradingView host, ChartTab chart)
    {
        _host = host;
        _chart = chart;
        _model = chart.Model!;
        Title = $"Configuración del gráfico · {chart.Symbol} {_model.Period.DisplayName}";
        Width = 470;
        Height = 700;
        MinWidth = 420;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;

        // secciones en pestañas (barra de arriba), como la configuración de ATAS
        var tabs = new TabControl { Margin = new Thickness(14, 4, 14, 0) };
        void Tab(string title, params Control[] rows)
        {
            var v = new StackPanel { Spacing = 10, Margin = new Thickness(6, 14, 6, 18) };
            foreach (var r in rows) v.Children.Add(r);
            tabs.Items.Add(new TabItem { Header = title, Content = new ScrollViewer { Content = v } });
        }

        Tab("Fondo",
            ColorRow("Fondo (arriba)", nameof(ChartTheme.BackgroundTop)),
            ColorRow("Fondo (abajo)", nameof(ChartTheme.BackgroundBottom)),
            SmallButton("Fondo liso (abajo igual que arriba)", () => Set(nameof(ChartTheme.BackgroundBottom), Get(nameof(ChartTheme.BackgroundTop)))),
            ColorRow("Cuadrícula", nameof(ChartTheme.Grid)),
            Toggle("Mostrar la cuadrícula", () => _model.Settings.ShowGrid, v => _model.Settings.ShowGrid = v),
            ColorRow("Separador de paneles", nameof(ChartTheme.PanelSeparator)));
        Tab("Velas",
            ColorRow("Alcista", nameof(ChartTheme.UpCandle)),
            ColorRow("Bajista", nameof(ChartTheme.DownCandle)),
            ColorRow("Borde alcista", nameof(ChartTheme.UpCandleBorder)),
            ColorRow("Borde bajista", nameof(ChartTheme.DownCandleBorder)),
            ColorRow("Mecha", nameof(ChartTheme.Wick)));
        Tab("Ejes",
            ColorRow("Fondo de los ejes", nameof(ChartTheme.AxisBackground)),
            ColorRow("Texto de los ejes", nameof(ChartTheme.AxisText)),
            ColorRow("Líneas de los ejes", nameof(ChartTheme.AxisLine)),
            ColorRow("Texto", nameof(ChartTheme.Text)),
            ColorRow("Texto secundario", nameof(ChartTheme.TextDim)),
            Toggle("Datos de la vela arriba (Ap, Máx, Mín, Cie…)", () => _model.Settings.ShowInfoPanel, v => _model.Settings.ShowInfoPanel = v),
            Toggle("La rueda acerca el gráfico entero (precio y tiempo a la vez, como una imagen)", () => _model.Settings.ZoomBothAxes, v => _model.Settings.ZoomBothAxes = v));
        Tab("Precio",
            ColorRow("Último precio (sube)", nameof(ChartTheme.LastPriceUp)),
            ColorRow("Último precio (baja)", nameof(ChartTheme.LastPriceDown)),
            ColorRow("Línea del último precio", nameof(ChartTheme.LastPriceLine)),
            Toggle("Mostrar la línea del último precio", () => _model.Settings.ShowLastPriceLine, v => _model.Settings.ShowLastPriceLine = v),
            ColorRow("Cursor (cruz)", nameof(ChartTheme.Crosshair)),
            ColorRow("Etiqueta del cursor", nameof(ChartTheme.CrosshairLabelBack)),
            ColorRow("Inicio de sesión", nameof(ChartTheme.NewSession)),
            Toggle("Marcar el inicio de cada sesión", () => _model.Settings.ShowSessionLines, v => _model.Settings.ShowSessionLines = v),
            Toggle("Mapa de calor de liquidez (libro de órdenes) detrás de las velas", () => _model.Settings.ShowLiquidityHeatmap, v => _model.Settings.ShowLiquidityHeatmap = v),
            Percent("Opacidad del mapa de calor", () => _model.Settings.HeatmapOpacity, v => _model.Settings.HeatmapOpacity = v));
        Tab("Footprint",
            ColorRow("Bid (vendedores)", nameof(ChartTheme.Bid)),
            ColorRow("Ask (compradores)", nameof(ChartTheme.Ask)),
            ColorRow("Celdas", nameof(ChartTheme.ClusterBase)),
            ColorRow("Borde de las celdas", nameof(ChartTheme.CellBorder)),
            ColorRow("Texto de las celdas", nameof(ChartTheme.ClusterText)),
            ColorRow("Texto del POC en las celdas", nameof(ChartTheme.ClusterTextStrong)),
            ColorRow("Texto de las escaleras", nameof(ChartTheme.LadderText)),
            ColorRow("Escalera: resaltado positivo", nameof(ChartTheme.LadderPositive)),
            ColorRow("Escalera: resaltado negativo", nameof(ChartTheme.LadderNegative)),
            ColorRow("POC", nameof(ChartTheme.Poc)),
            ColorRow("Área de valor", nameof(ChartTheme.ValueArea)),
            ColorRow("Desequilibrio comprador", nameof(ChartTheme.ImbalanceAsk)),
            ColorRow("Desequilibrio vendedor", nameof(ChartTheme.ImbalanceBid)),
            ColorRow("Delta positivo", nameof(ChartTheme.DeltaPositiveText)),
            ColorRow("Delta negativo", nameof(ChartTheme.DeltaNegativeText)),
            ColorRow("Perfil de volumen", nameof(ChartTheme.ProfileBar)),
            Note("Estos son los colores de todo el gráfico. Cada columna del footprint (escalera de delta, volumen, bid o ask, celdas o perfil) también puede tener los suyos, texto incluido, en el Editor de footprint."));

        // grosor y trazo de la línea entrada → salida (las operaciones propias y las de las estrategias)
        var width = new Slider { Minimum = 0.5, Maximum = 6, Value = _model.Settings.TradeLineWidth, TickFrequency = 0.5, IsSnapToTickEnabled = true, Width = 180 };
        var widthText = new TextBlock { Text = $"{_model.Settings.TradeLineWidth:0.#} px", VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
        width.ValueChanged += (_, _) =>
        {
            _model.Settings.TradeLineWidth = width.Value;
            widthText.Text = $"{width.Value:0.#} px";
            _model.Invalidate();
            Workspace.Save(_host);
        };
        var style = new ComboBox { Width = 180 };
        foreach (var (value, label) in new[] { (TradeLineStyle.Dashed, "Rayas"), (TradeLineStyle.Solid, "Continua"), (TradeLineStyle.Dotted, "Puntos") })
            style.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        style.SelectedItem = style.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (TradeLineStyle)i.Tag! == _model.Settings.TradeLineStyle);
        style.SelectionChanged += (_, _) =>
        {
            if (style.SelectedItem is not ComboBoxItem { Tag: TradeLineStyle v }) return;
            _model.Settings.TradeLineStyle = v;
            _model.Invalidate();
            Workspace.Save(_host);
        };
        _refreshers.Add(() =>
        {
            width.Value = _model.Settings.TradeLineWidth;
            style.SelectedItem = style.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (TradeLineStyle)i.Tag! == _model.Settings.TradeLineStyle);
        });
        Control Row(string label, Control control)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } });
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            return g;
        }

        Tab("Operaciones",
            Ui.H3("Líneas de entrada a salida"),
            ColorRow("Operación ganadora", nameof(ChartTheme.TradeWin)),
            ColorRow("Operación perdedora", nameof(ChartTheme.TradeLoss)),
            Row("Grosor", Ui.H(10, width, widthText)),
            Row("Trazo", style),
            Toggle("Marcar mis operaciones (ejecuciones y de la entrada a la salida)", () => _model.Settings.ShowTradeMarks, v => _model.Settings.ShowTradeMarks = v),
            Note("Las líneas de las estrategias puestas en el gráfico usan los mismos colores, grosor y trazo."),
            Ui.H3("Órdenes y posición"),
            ColorRow("Orden de compra", nameof(ChartTheme.BuyOrder)),
            ColorRow("Orden de venta", nameof(ChartTheme.SellOrder)),
            ColorRow("Posición larga / compras", nameof(ChartTheme.PositionLong)),
            ColorRow("Posición corta / ventas", nameof(ChartTheme.PositionShort)));

        var head = Ui.V(4, Ui.H2("Configuración del gráfico"), Note($"{chart.Symbol} · {_model.Period.DisplayName}. Los cambios se ven al instante y quedan guardados en este gráfico."));
        head.Margin = new Thickness(20, 16, 20, 4);
        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);
        body.Children.Add(tabs);

        var reset = Ui.Button("Restablecer todo", MaterialIconKind.Restore, () =>
        {
            _model.Settings.Colors = null;
            Changed();
        });
        ToolTip.SetTip(reset, "Vuelve a los colores de siempre en este gráfico");
        var toAll = Ui.Button("Aplicar a todos los gráficos", MaterialIconKind.ContentCopy, () =>
        {
            int n = _host.ApplyChartLook(_chart);
            Title = $"Configuración del gráfico · aplicada a {n} gráfico{(n == 1 ? "" : "s")}";
        });
        ToolTip.SetTip(toAll, "Copia estos colores y lo que se muestra a todos los gráficos abiertos");
        var close = Ui.Button("Listo", MaterialIconKind.Check, Close, "primary");
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(20, 10, 20, 16) };
        foreach (var b in new Control[] { reset, toAll, close }) actions.Children.Add(b);

        var root = new DockPanel();
        var actionsBorder = new Border { Child = actions, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(actionsBorder, Dock.Bottom);
        root.Children.Add(actionsBorder);
        root.Children.Add(body);
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }

    private static TextBlock Note(string text) => new() { Text = text, Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    private static Control Section(string title, params Control[] rows)
    {
        var v = new StackPanel { Spacing = 9 };
        v.Children.Add(Ui.H3(title));
        foreach (var r in rows) v.Children.Add(r);
        return v;
    }

    private static Button SmallButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2), MinHeight = 24, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => onClick();
        return b;
    }

    private uint Get(string property) =>
        _model.Settings.Colors != null && _model.Settings.Colors.TryGetValue(property, out var c) ? c : ChartTheme.DefaultOf(property);

    private void Set(string property, uint? argb)
    {
        var colors = _model.Settings.Colors ??= new Dictionary<string, uint>();
        if (argb is not { } v || v == ChartTheme.DefaultOf(property)) colors.Remove(property);
        else colors[property] = v;
        if (colors.Count == 0) _model.Settings.Colors = null;
        Changed();
    }

    private void Changed()
    {
        _model.ApplyColors();
        Workspace.Save(_host);
        foreach (var r in _refreshers) r();
    }

    private Control Percent(string label, Func<int> get, Action<int> set)
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Width = 180, Value = get(), TickFrequency = 5 };
        var text = new TextBlock { Text = get() + " %", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Text2, Width = 40 };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
            int v = (int)Math.Round(slider.Value);
            text.Text = v + " %";
            set(v);
            _model.Invalidate();
            Workspace.Save(_host);
        };
        _refreshers.Add(() => slider.Value = get());
        return Ui.H(10, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, slider, text);
    }

    private Control Toggle(string label, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = get() };
        cb.IsCheckedChanged += (_, _) =>
        {
            set(cb.IsChecked == true);
            _model.Invalidate();
            Workspace.Save(_host);
        };
        _refreshers.Add(() => cb.IsChecked = get());
        return cb;
    }

    /// <summary>Fila de un color: muestra el actual y al tocarlo abre la paleta, el código y "restablecer".</summary>
    private Control ColorRow(string label, string property)
    {
        var swatch = new Border { Width = 34, Height = 18, CornerRadius = new CornerRadius(4), BorderBrush = Ui.Line, BorderThickness = new Thickness(1) };
        var code = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Text2, FontFamily = Ui.MonoFont };
        var changed = new TextBlock { Text = "•", Foreground = Ui.Accent, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold };
        ToolTip.SetTip(changed, "Cambiado en este gráfico");
        var hex = new TextBox { Width = 110, Watermark = "#RRGGBB o #RRGGBBAA", FontSize = 12 };
        var opacity = new Slider { Minimum = 0, Maximum = 255, Width = 150, TickFrequency = 5 };
        var opacityText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Text2, Width = 38 };
        var palette = new WrapPanel { ItemSpacing = 4, LineSpacing = 4, MaxWidth = 290 };
        var flyout = new Flyout();
        bool syncing = false;

        void Refresh()
        {
            uint c = Get(property);
            swatch.Background = new SolidColorBrush(Color.FromUInt32(c));
            code.Text = $"#{c & 0xFFFFFF:X6}" + ((c >> 24) != 0xFF ? $" · {(c >> 24) * 100 / 255}%" : "");
            changed.IsVisible = _model.Settings.Colors?.ContainsKey(property) == true;
            syncing = true;
            hex.Text = (c >> 24) == 0xFF ? $"#{c & 0xFFFFFF:X6}" : $"#{c & 0xFFFFFF:X6}{c >> 24:X2}";
            opacity.Value = c >> 24;
            opacityText.Text = $"{(c >> 24) * 100 / 255}%";
            syncing = false;
        }

        foreach (var p in Palette)
        {
            uint color = p;
            var b = new Button { Content = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromUInt32(color)), BorderBrush = Ui.Line, BorderThickness = new Thickness(1) }, Padding = new Thickness(1), MinHeight = 0 };
            b.Click += (_, _) =>
            {
                // conserva la transparencia que tenía (la cuadrícula, por ejemplo, es casi transparente)
                uint alpha = Get(property) & 0xFF000000;
                Set(property, (color & 0x00FFFFFF) | (alpha == 0 ? 0xFF000000 : alpha));
            };
            palette.Children.Add(b);
        }

        void ApplyHex()
        {
            var t = (hex.Text ?? "").Trim().TrimStart('#');
            if (t.Length is 6 or 8 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                Set(property, t.Length == 6 ? 0xFF000000 | v : (v >> 8) | ((v & 0xFF) << 24));
            else Refresh();
        }

        hex.LostFocus += (_, _) => ApplyHex();
        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) ApplyHex();
        };
        opacity.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            uint c = Get(property);
            Set(property, (c & 0x00FFFFFF) | ((uint)Math.Round(opacity.Value) << 24));
        };
        var reset = SmallButton("Restablecer", () => Set(property, null));
        flyout.Content = Ui.V(10, Ui.H3(label), palette, Ui.H(8, new TextBlock { Text = "Código", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center }, hex),
            Ui.H(8, new TextBlock { Text = "Opacidad", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center }, opacity, opacityText), reset).Also(v => v.Margin = new Thickness(12));

        var button = new Button { Content = Ui.H(8, swatch, code), Padding = new Thickness(6, 3), MinHeight = 26, Flyout = flyout, HorizontalAlignment = HorizontalAlignment.Left };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*,Auto") };
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } });
        Grid.SetColumn(button, 1);
        g.Children.Add(button);
        Grid.SetColumn(changed, 2);
        g.Children.Add(changed);
        _refreshers.Add(Refresh);
        Refresh();
        return g;
    }
}
