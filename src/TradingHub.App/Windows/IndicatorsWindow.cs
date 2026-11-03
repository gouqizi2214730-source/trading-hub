using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Indicators;

namespace TradingHub.App.Windows;

/// <summary>Agregar, quitar y configurar indicadores del gráfico activo.</summary>
public sealed class IndicatorsWindow : Window
{
    private readonly AppServices _svc;
    private readonly ChartModel _model;
    private readonly TextBox _search = new() { Watermark = "Buscar por nombre o categoría…" };
    private readonly ListBox _available = new();
    private readonly ListBox _onChart = new();
    private readonly PropertyGrid _grid = new();
    private readonly Button _templates = new();
    private readonly TextBlock _title = new() { Classes = { "h2" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _errorText = new() { Foreground = Ui.Down, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _count = new() { Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Control _emptyState;

    public IndicatorsWindow(AppServices svc, ChartModel model)
    {
        _svc = svc;
        _model = model;
        Title = $"Indicadores — {model.Title}";
        Width = 1120;
        Height = 720;
        MinWidth = 900;
        MinHeight = 520;
        Background = Ui.Bg0;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _emptyState = Ui.V(8,
            Ui.Icon(MaterialIconKind.TuneVariant, 34, Ui.Text2),
            new TextBlock { Text = "Elegí un indicador del gráfico para configurarlo", Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center });
        _emptyState.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyState.VerticalAlignment = VerticalAlignment.Center;
        ((StackPanel)_emptyState).Children[0].HorizontalAlignment = HorizontalAlignment.Center;

        _grid.Changed += () => _model.Invalidate();
        Content = Build();
        FillAvailable();
        FillOnChart();
        _search.TextChanged += (_, _) => FillAvailable();
        _svc.Catalog.Changed += FillAvailable;
        Closed += (_, _) => _svc.Catalog.Changed -= FillAvailable;
    }

    private Control Build()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,12,300,12,*"), Margin = new Thickness(14) };

        // ---- biblioteca
        var left = new DockPanel();
        var libHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var code = Ui.IconButton(MaterialIconKind.CodeBraces, "Escribir un indicador propio en C# (editor)", () => new CodeEditorWindow(_svc, CodeKind.Indicator).Show(this), 17);
        DockPanel.SetDock(code, Dock.Right);
        libHeader.Children.Add(code);
        libHeader.Children.Add(Ui.H(8, Ui.H3("Biblioteca"), _count));
        DockPanel.SetDock(libHeader, Dock.Top);
        left.Children.Add(libHeader);
        _search.InnerLeftContent = Ui.Icon(MaterialIconKind.Magnify, 16, Ui.Text2);
        ((Control)_search.InnerLeftContent).Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_search, Dock.Top);
        left.Children.Add(_search);
        var add = Ui.Button("Agregar al gráfico", MaterialIconKind.Plus, AddSelected, "primary");
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        add.HorizontalContentAlignment = HorizontalAlignment.Center;
        add.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(add, Dock.Bottom);
        left.Children.Add(add);
        _available.DoubleTapped += (_, _) => AddSelected();
        _available.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(Ui.Card(_available, 4));
        grid.Children.Add(left);

        // ---- en el gráfico
        var mid = new DockPanel();
        var midHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var tools = Ui.H(2,
            Ui.IconButton(MaterialIconKind.EyeOutline, "Mostrar / ocultar", () => WithSelected(s =>
            {
                s.Visible = !s.Visible;
                _model.Invalidate();
            }), 17),
            Ui.IconButton(MaterialIconKind.DockBottom, "Pasar entre el gráfico de precio y un panel propio", () => WithSelected(s =>
            {
                if (s.Indicator.DenyToChangePanel) return;
                _model.MoveToPanel(s, s.Panel == null ? new SubPanel() : null);
                _model.Invalidate();
            }), 17),
            Ui.IconButton(MaterialIconKind.DeleteOutline, "Quitar del gráfico", () => WithSelected(s =>
            {
                _model.RemoveIndicator(s);
                _grid.Target = null;
            }), 17));
        DockPanel.SetDock(tools, Dock.Right);
        midHeader.Children.Add(tools);
        midHeader.Children.Add(Ui.H3("En el gráfico"));
        DockPanel.SetDock(midHeader, Dock.Top);
        mid.Children.Add(midHeader);
        _onChart.SelectionChanged += (_, _) => SelectSlot();
        mid.Children.Add(Ui.Card(_onChart, 4));
        Grid.SetColumn(mid, 2);
        grid.Children.Add(mid);

        // ---- configuración (con sus plantillas)
        _templates.Content = Ui.H(6, Ui.Icon(MaterialIconKind.BookmarkMultipleOutline, 15), new TextBlock { Text = "Plantillas", VerticalAlignment = VerticalAlignment.Center });
        _templates.Padding = new Thickness(9, 3);
        _templates.MinHeight = 26;
        _templates.IsVisible = false;
        ToolTip.SetTip(_templates, "Guardar estos ajustes como plantilla, aplicar una guardada o elegir con cuál arrancan los nuevos");
        _templates.Click += (_, _) => ShowTemplates();
        var titleRow = new DockPanel();
        DockPanel.SetDock(_templates, Dock.Right);
        titleRow.Children.Add(_templates);
        titleRow.Children.Add(_title);
        var head = Ui.V(4, titleRow, _subtitle, _errorText);
        head.Margin = new Thickness(0, 0, 0, 12);
        var right = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        right.Children.Add(head);
        right.Children.Add(new Panel { Children = { _grid, _emptyState } });
        var rightCard = Ui.Card(right, 16);
        Grid.SetColumn(rightCard, 4);
        grid.Children.Add(rightCard);
        return grid;
    }

    private void WithSelected(Action<IndicatorSlot> action)
    {
        if (_onChart.SelectedItem is not ListBoxItem { Tag: IndicatorSlot s }) return;
        action(s);
        FillOnChart();
        _onChart.SelectedItem = _onChart.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == s);
    }

    private void FillAvailable()
    {
        var q = (_search.Text ?? "").Trim();
        _available.Items.Clear();
        var items = _svc.Catalog.Items.Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Category.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _count.Text = $"{items.Count}";
        foreach (var g in items.GroupBy(i => i.Source == UserScripts.Source ? "★ " + UserScripts.Source : i.Category).OrderBy(g => g.Key.StartsWith('★') ? 0 : 1).ThenBy(g => g.Key))
        {
            _available.Items.Add(new ListBoxItem { Content = g.Key.ToUpperInvariant(), IsEnabled = false }.Classes("header"));
            foreach (var i in g)
            {
                var row = new DockPanel();
                // sin etiqueta de origen para los de la biblioteca (Trading Hub y los técnicos); solo los tuyos y los extra
                if (i.Source != "Trading Hub" && i.Source != "Técnicos")
                {
                    string tag = i.Source == UserScripts.Source ? "propio" : "extra";
                    var badge = new Border { Child = new TextBlock { Text = tag, FontSize = 10 }, Classes = { "pill" }, Padding = new Thickness(6, 1) };
                    DockPanel.SetDock(badge, Dock.Right);
                    row.Children.Add(badge);
                }

                row.Children.Add(new TextBlock { Text = i.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var item = new ListBoxItem { Content = row, Tag = i };
                if (!string.IsNullOrWhiteSpace(i.Description)) ToolTip.SetTip(item, i.Description);
                item.ContextMenu = LibraryMenu(i);
                _available.Items.Add(item);
            }
        }
    }

    /// <summary>Clic derecho en la biblioteca: ver el código, copiarlo como propio, editarlo o borrarlo.</summary>
    private ContextMenu LibraryMenu(IndicatorInfo i)
    {
        var menu = new ContextMenu();
        var items = new List<Control>();
        var add = new MenuItem { Header = "Agregar al gráfico", Icon = Ui.Icon(MaterialIconKind.Plus, 16) };
        add.Click += (_, _) => AddIndicator(i);
        items.Add(add);
        items.Add(new Separator());
        bool own = i.Source == UserScripts.Source;
        if (own)
        {
            var edit = new MenuItem { Header = "Editar el código…", Icon = Ui.Icon(MaterialIconKind.CodeBraces, 16) };
            edit.Click += (_, _) => new CodeEditorWindow(_svc, CodeKind.Indicator, IndicatorSources.UserFile(i.Type)).Show(this);
            items.Add(edit);
            var del = new MenuItem { Header = "Eliminar este indicador…", Icon = Ui.Icon(MaterialIconKind.DeleteOutline, 16) };
            del.Click += async (_, _) =>
            {
                var file = IndicatorSources.UserFile(i.Type);
                if (file == null) return;
                bool ok = await ConfirmDialog.ShowAsync(this, "Eliminar indicador", $"Se borra el archivo {Path.GetFileName(file)} de tus indicadores. Los gráficos que lo usan lo pierden.", "Eliminar", danger: true);
                if (!ok) return;
                try
                {
                    File.Delete(file);
                    _svc.Scripts?.CompileAll();
                }
                catch (Exception ex)
                {
                    await ConfirmDialog.ShowAsync(this, "No se pudo borrar", ex.Message, "Cerrar");
                }
            };
            items.Add(del);
        }
        else
        {
            var view = new MenuItem { Header = "Ver el código fuente…", Icon = Ui.Icon(MaterialIconKind.FileCodeOutline, 16) };
            view.Click += (_, _) =>
            {
                var src = IndicatorSources.EmbeddedSource(i.Type);
                if (src == null)
                {
                    _ = ConfirmDialog.ShowAsync(this, "Sin código fuente", "Este indicador viene compilado sin su código.", "Cerrar");
                    return;
                }

                new CodeEditorWindow(_svc, CodeKind.Indicator, null, (i.Name, src)).Show(this);
            };
            items.Add(view);
            var fork = new MenuItem { Header = "Copiar como indicador propio (para modificarlo)…", Icon = Ui.Icon(MaterialIconKind.ContentCopy, 16) };
            fork.Click += async (_, _) =>
            {
                var name = await PromptDialog.ShowAsync(this, "Copiar como propio", "Nombre de la clase de la copia", i.Type.Name + "Propio", "Copiar");
                if (name == null) return;
                try
                {
                    var path = IndicatorSources.Fork(i.Type, name);
                    _svc.Scripts?.CompileAll();
                    new CodeEditorWindow(_svc, CodeKind.Indicator, path).Show(this);
                }
                catch (Exception ex)
                {
                    await ConfirmDialog.ShowAsync(this, "No se pudo copiar", ex.Message, "Cerrar");
                }
            };
            items.Add(fork);
        }

        menu.ItemsSource = items;
        return menu;
    }

    private void FillOnChart()
    {
        _onChart.Items.Clear();
        foreach (var s in _model.Indicators)
        {
            var icon = Ui.Icon(s.Visible ? MaterialIconKind.Eye : MaterialIconKind.EyeOffOutline, 15, s.Visible ? Ui.Text1 : Ui.Text2);
            var name = new TextBlock { Text = s.Indicator.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = s.Visible ? 1 : 0.55 };
            var where = new TextBlock { Text = s.Panel == null ? "precio" : "panel", Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel();
            var right = Ui.H(6, where);
            if (s.Error != null) right.Children.Insert(0, Ui.Icon(MaterialIconKind.AlertCircleOutline, 15, Ui.Down));
            DockPanel.SetDock(icon, Dock.Left);
            icon.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(icon);
            row.Children.Add(right);
            row.Children.Add(name);
            _onChart.Items.Add(new ListBoxItem { Content = row, Tag = s });
        }

        if (_model.Indicators.Count == 0)
            _onChart.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "Sin indicadores. Elegí uno de la biblioteca y tocá Agregar.", Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap }, IsEnabled = false });
    }

    private void AddSelected()
    {
        if (_available.SelectedItem is not ListBoxItem { Tag: IndicatorInfo info }) return;
        AddIndicator(info);
    }

    private void AddIndicator(IndicatorInfo info)
    {
        try
        {
            var ind = (Indicator)Activator.CreateInstance(info.Type)!;
            // arranca con su plantilla predeterminada, si tiene (con sus colores)
            bool templated = Workspace.ApplyDefaultIndicatorTemplate(_svc, ind);
            var slot = _model.AddIndicator(ind, applyDefaultColors: !templated);
            FillOnChart();
            _onChart.SelectedItem = _onChart.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == slot);
        }
        catch (Exception ex)
        {
            _errorText.Text = $"No se pudo crear {info.Name}: {ex.InnerException?.Message ?? ex.Message}";
            _errorText.IsVisible = true;
        }
    }

    /// <summary>Menú de plantillas del indicador elegido.</summary>
    private void ShowTemplates()
    {
        if (_onChart.SelectedItem is not ListBoxItem { Tag: IndicatorSlot s }) return;
        var ind = s.Indicator;
        var type = ind.GetType();
        string key = type.FullName!;
        var names = Workspace.IndicatorTemplateNames(type);
        _svc.Settings.DefaultIndicatorTemplates.TryGetValue(key, out var def);
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        var save = new MenuItem { Header = "Guardar estos ajustes como plantilla…", Icon = Ui.Icon(MaterialIconKind.ContentSaveOutline, 16) };
        save.Click += async (_, _) =>
        {
            var name = await PromptDialog.ShowAsync(this, $"Plantilla de {ind.Name}", "Nombre de la plantilla (guarda todos sus ajustes y colores)", def ?? ind.Name, "Guardar");
            if (string.IsNullOrWhiteSpace(name)) return;
            Workspace.SaveIndicatorTemplate(ind, name);
            _subtitle.Text = $"Plantilla «{name.Trim()}» guardada";
        };
        flyout.Items.Add(save);
        if (names.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "Todavía no hay plantillas de este indicador", IsEnabled = false });
        }
        else
        {
            flyout.Items.Add(new Separator());
            foreach (var n in names)
            {
                var apply = new MenuItem { Header = n == def ? $"{n}  · predeterminada" : n, Icon = Ui.Icon(MaterialIconKind.BookmarkOutline, 16) };
                apply.Click += (_, _) =>
                {
                    if (!Workspace.ApplyIndicatorTemplate(ind, n)) return;
                    _grid.Target = null;
                    _grid.Target = ind;
                    _model.Invalidate();
                    _subtitle.Text = $"Plantilla «{n}» aplicada";
                };
                flyout.Items.Add(apply);
            }

            flyout.Items.Add(new Separator());
            var forNew = new MenuItem { Header = "Con cuál arrancan los nuevos", Icon = Ui.Icon(MaterialIconKind.StarOutline, 16) };
            MenuItem Choice(string header, string? value)
            {
                var mi = new MenuItem { Header = header, Icon = value == def ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null };
                mi.Click += (_, _) =>
                {
                    if (value == null) _svc.Settings.DefaultIndicatorTemplates.Remove(key);
                    else _svc.Settings.DefaultIndicatorTemplates[key] = value;
                    _svc.Settings.Save();
                    _subtitle.Text = value == null ? "Los nuevos arrancan con los ajustes de fábrica" : $"Los nuevos {ind.Name} arrancan con «{value}»";
                };
                return mi;
            }

            forNew.Items.Add(Choice("Ajustes de fábrica", null));
            foreach (var n in names) forNew.Items.Add(Choice(n, n));
            flyout.Items.Add(forNew);

            var delete = new MenuItem { Header = "Borrar una plantilla", Icon = Ui.Icon(MaterialIconKind.DeleteOutline, 16) };
            foreach (var n in names)
            {
                var mi = new MenuItem { Header = n };
                mi.Click += (_, _) =>
                {
                    Workspace.DeleteIndicatorTemplate(type, n);
                    if (def == n)
                    {
                        _svc.Settings.DefaultIndicatorTemplates.Remove(key);
                        _svc.Settings.Save();
                    }

                    _subtitle.Text = $"Plantilla «{n}» borrada";
                };
                delete.Items.Add(mi);
            }

            flyout.Items.Add(delete);
        }

        flyout.ShowAt(_templates);
    }

    /// <summary>Abre la ventana con un indicador del gráfico ya elegido (para editar sus ajustes).</summary>
    public void Select(IndicatorSlot slot)
    {
        _onChart.SelectedItem = _onChart.Items.OfType<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, slot));
    }

    private void SelectSlot()
    {
        _templates.IsVisible = _onChart.SelectedItem is ListBoxItem { Tag: IndicatorSlot };
        if (_onChart.SelectedItem is not ListBoxItem { Tag: IndicatorSlot s })
        {
            _grid.Target = null;
            _title.Text = "";
            _subtitle.Text = "";
            _errorText.IsVisible = false;
            _emptyState.IsVisible = true;
            return;
        }

        _emptyState.IsVisible = false;
        _title.Text = s.Indicator.Name;
        var info = _svc.Catalog.Find(s.Indicator.GetType().FullName!);
        _subtitle.Text = string.Join(" · ", new[] { info?.Category, info?.Description }.Where(x => !string.IsNullOrWhiteSpace(x)));
        _errorText.Text = s.Error != null ? "Error: " + s.Error : "";
        _errorText.IsVisible = s.Error != null;
        _grid.Target = s.Indicator;
    }
}
