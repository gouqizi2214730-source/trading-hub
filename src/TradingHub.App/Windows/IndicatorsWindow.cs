using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
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
        _available.DoubleTapped += (_, e) =>
        {
            if (_available.SelectedItem is ListBoxItem { Tag: IndicatorInfo }) AddSelected();
        };
        _available.KeyDown += (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down)) return;
            if (_available.SelectedItem is not ListBoxItem { Tag: IndicatorInfo info } || (_search.Text ?? "").Trim().Length > 0) return;
            ShiftSelected(info, e.Key == Key.Up ? -1 : +1);
            e.Handled = true;
        };
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

    private LibraryLayout Layout => _svc.Settings.Library;

    /// <summary>Guarda cómo quedó la biblioteca y la vuelve a mostrar (en esta ventana y en las demás).</summary>
    private void LibraryChanged()
    {
        _svc.Settings.Save();
        _svc.Catalog.NotifyChanged();
        _model.Invalidate();
    }

    private static bool Matches(IndicatorInfo i, string q) =>
        q.Length == 0 || Indicator.DefaultName(i.Type).Contains(q, StringComparison.CurrentCultureIgnoreCase)
                      || i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Category.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                      || TradingHub.Rendering.UiText.Tr(i.Name).Contains(q, StringComparison.CurrentCultureIgnoreCase);

    private void FillAvailable()
    {
        var q = (_search.Text ?? "").Trim();
        _available.Items.Clear();
        var all = _svc.Catalog.Items;
        var groups = IndicatorLibrary.Build(all, Layout);
        int count = 0;
        foreach (var g in groups)
        {
            var items = g.Items.Where(i => Matches(i, q)).ToList();
            if (q.Length > 0 && items.Count == 0) continue; // buscando: solo los grupos con algo
            count += items.Count;
            bool collapsed = g.Collapsed && q.Length == 0;
            _available.Items.Add(GroupHeader(g, collapsed, items.Count));
            if (collapsed) continue;
            foreach (var i in items)
            {
                var row = new DockPanel();
                // sin etiqueta de origen para los de la biblioteca (Trading Hub y los técnicos); solo los tuyos y los extra
                if (i.Source != "Trading Hub" && i.Source != "Técnicos")
                {
                    string tag = i.Source == UserScripts.Source ? "propio" : "extra";
                    // una copia editada de la biblioteca cuyo original se actualizó después
                    if (IndicatorCatalog.ReplacedBy(i.Type) is { } orig && IndicatorSources.Stale.Contains(orig)) tag = "original actualizado";
                    var badge = new Border { Child = new TextBlock { Text = tag, FontSize = 10 }, Classes = { "pill" }, Padding = new Thickness(6, 1) };
                    DockPanel.SetDock(badge, Dock.Right);
                    row.Children.Add(badge);
                }

                row.Children.Add(new TextBlock { Text = Indicator.DefaultName(i.Type), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var item = new ListBoxItem { Content = row, Tag = i, Padding = new Thickness(18, 4, 8, 4) };
                string? tip = Layout.Names.ContainsKey(i.Type.FullName!) ? $"{TradingHub.Rendering.UiText.Tr(i.Name)}\n{i.Description}".Trim() : i.Description;
                if (!string.IsNullOrWhiteSpace(tip)) ToolTip.SetTip(item, tip);
                item.ContextMenu = LibraryMenu(i, g);
                if (q.Length == 0) Draggable(item, i);
                _available.Items.Add(item);
            }
        }

        _count.Text = $"{count}";
    }

    // ------------------------------------------------------------------ grupos

    private ListBoxItem GroupHeader(LibraryGroup g, bool collapsed, int count)
    {
        var chevron = Ui.Icon(collapsed ? MaterialIconKind.ChevronRight : MaterialIconKind.ChevronDown, 15, Ui.Text2);
        var name = new TextBlock { Text = g.Name.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var n = new TextBlock { Text = count.ToString(), Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Left);
        chevron.Margin = new Thickness(0, 0, 4, 0);
        DockPanel.SetDock(n, Dock.Right);
        row.Children.Add(chevron);
        row.Children.Add(n);
        row.Children.Add(name);
        var header = new ListBoxItem { Content = row, Tag = g, Cursor = new Cursor(StandardCursorType.Hand) };
        header.Classes.Add("header");
        ToolTip.SetTip(header, "Clic: abrir o cerrar el grupo · clic derecho: renombrar, ordenar, grupos nuevos · arrastrá indicadores acá para moverlos");
        header.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || _dragging) return;
            IndicatorLibrary.SetCollapsed(Layout, g.Id, !collapsed);
            LibraryChanged();
            e.Handled = true;
        };
        header.ContextMenu = GroupMenu(g);
        return header;
    }

    private ContextMenu GroupMenu(LibraryGroup g)
    {
        var all = _svc.Catalog.Items;
        var items = new List<Control>();
        MenuItem Item(string text, MaterialIconKind icon, Action run, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, Icon = Ui.Icon(icon, 16), IsEnabled = enabled };
            mi.Click += (_, _) => run();
            return mi;
        }

        items.Add(Item("Renombrar el grupo…", MaterialIconKind.RenameOutline, async () =>
        {
            var name = await PromptDialog.ShowAsync(this, "Renombrar el grupo", "Nombre del grupo (vacío = el de fábrica)", g.Name, "Renombrar");
            if (name == null) return;
            IndicatorLibrary.RenameGroup(Layout, g.Id, name);
            LibraryChanged();
        }));
        items.Add(Item("Grupo nuevo…", MaterialIconKind.FolderPlusOutline, async () =>
        {
            var name = await PromptDialog.ShowAsync(this, "Grupo nuevo", "Nombre del grupo (va debajo de este)", "", "Crear");
            if (string.IsNullOrWhiteSpace(name)) return;
            IndicatorLibrary.CreateGroup(Layout, all, name, g.Id);
            LibraryChanged();
        }));
        items.Add(new Separator());
        items.Add(Item("Subir el grupo", MaterialIconKind.ArrowUp, () =>
        {
            IndicatorLibrary.ShiftGroup(Layout, all, g.Id, -1);
            LibraryChanged();
        }));
        items.Add(Item("Bajar el grupo", MaterialIconKind.ArrowDown, () =>
        {
            IndicatorLibrary.ShiftGroup(Layout, all, g.Id, +1);
            LibraryChanged();
        }));
        items.Add(Item("Ordenar por nombre (A → Z)", MaterialIconKind.SortAlphabeticalAscending, () =>
        {
            IndicatorLibrary.SortGroup(Layout, all, g.Id);
            LibraryChanged();
        }, g.Items.Count > 1));
        if (g.IsUser)
        {
            items.Add(new Separator());
            items.Add(Item("Borrar el grupo (sus indicadores vuelven a su grupo de fábrica)", MaterialIconKind.FolderRemoveOutline, () =>
            {
                IndicatorLibrary.DeleteGroup(Layout, g.Id);
                LibraryChanged();
            }));
        }

        items.Add(new Separator());
        items.Add(Item("Volver la biblioteca a como viene de fábrica…", MaterialIconKind.Restore, async () =>
        {
            if (!await ConfirmDialog.ShowAsync(this, "Biblioteca de fábrica", "Se borran los grupos nuevos, el orden y los nombres que pusiste. Los indicadores de los gráficos no cambian.", "Volver a fábrica", danger: true)) return;
            IndicatorLibrary.Reset(Layout);
            LibraryChanged();
        }));
        return new ContextMenu { ItemsSource = items };
    }

    /// <summary>Lo de la biblioteca de un indicador: renombrarlo, moverlo a otro grupo, subirlo o bajarlo.</summary>
    private List<Control> OrganizeItems(IndicatorInfo i, LibraryGroup current)
    {
        var all = _svc.Catalog.Items;
        string type = i.Type.FullName!;
        var res = new List<Control>();
        var rename = new MenuItem { Header = "Renombrar…", Icon = Ui.Icon(MaterialIconKind.RenameOutline, 16) };
        rename.Click += async (_, _) =>
        {
            var name = await PromptDialog.ShowAsync(this, "Renombrar indicador", $"Nombre en la biblioteca y en los gráficos (vacío = «{TradingHub.Rendering.UiText.Tr(i.Name)}»)", Indicator.DefaultName(i.Type), "Renombrar");
            if (name == null) return;
            IndicatorLibrary.Rename(Layout, type, name);
            LibraryChanged();
            FillOnChart();
        };
        res.Add(rename);
        if (Layout.Names.ContainsKey(type))
        {
            var back = new MenuItem { Header = $"Volver al nombre de fábrica («{TradingHub.Rendering.UiText.Tr(i.Name)}»)", Icon = Ui.Icon(MaterialIconKind.Restore, 16) };
            back.Click += (_, _) =>
            {
                IndicatorLibrary.Rename(Layout, type, null);
                LibraryChanged();
                FillOnChart();
            };
            res.Add(back);
        }

        var move = new MenuItem { Header = "Mover al grupo", Icon = Ui.Icon(MaterialIconKind.FolderMoveOutline, 16) };
        var groups = new List<Control>();
        foreach (var g in IndicatorLibrary.Build(all, Layout))
        {
            if (g.Id == current.Id) continue;
            var mi = new MenuItem { Header = g.Name };
            var id = g.Id;
            mi.Click += (_, _) =>
            {
                IndicatorLibrary.Move(Layout, all, type, id, null);
                LibraryChanged();
            };
            groups.Add(mi);
        }

        groups.Add(new Separator());
        var fresh = new MenuItem { Header = "Grupo nuevo…", Icon = Ui.Icon(MaterialIconKind.FolderPlusOutline, 16) };
        fresh.Click += async (_, _) =>
        {
            var name = await PromptDialog.ShowAsync(this, "Grupo nuevo", "Nombre del grupo nuevo", "", "Crear y mover");
            if (string.IsNullOrWhiteSpace(name)) return;
            var id = IndicatorLibrary.CreateGroup(Layout, all, name, current.Id);
            IndicatorLibrary.Move(Layout, all, type, id, null);
            LibraryChanged();
        };
        groups.Add(fresh);
        move.ItemsSource = groups;
        res.Add(move);
        var up = new MenuItem { Header = "Subir", Icon = Ui.Icon(MaterialIconKind.ArrowUp, 16), InputGesture = new KeyGesture(Key.Up, KeyModifiers.Alt) };
        up.Click += (_, _) => ShiftSelected(i, -1);
        var down = new MenuItem { Header = "Bajar", Icon = Ui.Icon(MaterialIconKind.ArrowDown, 16), InputGesture = new KeyGesture(Key.Down, KeyModifiers.Alt) };
        down.Click += (_, _) => ShiftSelected(i, +1);
        res.Add(up);
        res.Add(down);
        return res;
    }

    private void ShiftSelected(IndicatorInfo i, int delta)
    {
        IndicatorLibrary.Shift(Layout, _svc.Catalog.Items, i.Type.FullName!, delta);
        LibraryChanged();
        _available.SelectedItem = _available.Items.OfType<ListBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, i));
    }

    // ------------------------------------------------------------------ arrastrar para ordenar

    private IndicatorInfo? _dragFrom;
    private Point _dragStart;
    private bool _dragging;
    private ListBoxItem? _dropOn;

    private void Draggable(ListBoxItem item, IndicatorInfo info)
    {
        item.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;
            _dragFrom = info;
            _dragStart = e.GetPosition(_available);
            _dragging = false;
        };
        item.PointerMoved += (_, e) =>
        {
            if (_dragFrom == null || !e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;
            var p = e.GetPosition(_available);
            if (!_dragging && Math.Abs(p.Y - _dragStart.Y) + Math.Abs(p.X - _dragStart.X) < 7) return;
            _dragging = true;
            ShowDrop(TargetAt(p));
        };
        item.PointerReleased += (_, e) =>
        {
            var from = _dragFrom;
            bool dragging = _dragging;
            _dragFrom = null;
            _dragging = false;
            ShowDrop(null);
            if (from == null || !dragging) return;
            var target = TargetAt(e.GetPosition(_available));
            e.Handled = true;
            Drop(from, target);
        };
    }

    private ListBoxItem? TargetAt(Point p)
    {
        var hit = _available.InputHitTest(p) as Visual;
        return hit as ListBoxItem ?? hit?.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
    }

    private void ShowDrop(ListBoxItem? target)
    {
        if (ReferenceEquals(_dropOn, target)) return;
        if (_dropOn != null)
        {
            _dropOn.BorderThickness = new Thickness(0);
            _dropOn.BorderBrush = null;
        }

        _dropOn = target;
        if (target == null) return;
        target.BorderBrush = Ui.Accent;
        target.BorderThickness = new Thickness(0, 2, 0, 0); // se suelta antes de este
    }

    private void Drop(IndicatorInfo from, ListBoxItem? target)
    {
        var all = _svc.Catalog.Items;
        string type = from.Type.FullName!;
        switch (target?.Tag)
        {
            case IndicatorInfo before when !ReferenceEquals(before, from):
            {
                var g = IndicatorLibrary.Build(all, Layout).FirstOrDefault(x => x.Items.Contains(before));
                if (g == null) return;
                IndicatorLibrary.Move(Layout, all, type, g.Id, before.Type.FullName);
                break;
            }
            case LibraryGroup g:
                IndicatorLibrary.Move(Layout, all, type, g.Id, null);
                break;
            default:
                return;
        }

        LibraryChanged();
        _available.SelectedItem = _available.Items.OfType<ListBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, from));
    }

    /// <summary>Clic derecho en la biblioteca: ver el código, copiarlo como propio, editarlo o borrarlo.</summary>
    private ContextMenu LibraryMenu(IndicatorInfo i, LibraryGroup group)
    {
        var menu = new ContextMenu();
        var items = new List<Control>();
        var add = new MenuItem { Header = "Agregar al gráfico", Icon = Ui.Icon(MaterialIconKind.Plus, 16) };
        add.Click += (_, _) => AddIndicator(i);
        items.Add(add);
        items.Add(new Separator());
        items.AddRange(OrganizeItems(i, group));
        items.Add(new Separator());
        bool own = i.Source == UserScripts.Source;
        string? replaces = IndicatorCatalog.ReplacedBy(i.Type);
        if (own)
        {
            var edit = new MenuItem { Header = "Editar el código…", Icon = Ui.Icon(MaterialIconKind.CodeBraces, 16) };
            edit.Click += (_, _) => new CodeEditorWindow(_svc, CodeKind.Indicator, IndicatorSources.UserFile(i.Type)).Show(this);
            items.Add(edit);
            if (replaces != null)
            {
                // un indicador de la biblioteca editado: borrar el archivo vuelve al original
                bool stale = IndicatorSources.Stale.Contains(replaces);
                var restore = new MenuItem
                {
                    Header = stale ? "El original se actualizó: volver al original nuevo (descarta tus cambios)…" : "Volver al original de la biblioteca (descarta tus cambios)…",
                    Icon = Ui.Icon(MaterialIconKind.Restore, 16),
                };
                restore.Click += async (_, _) =>
                {
                    var file = IndicatorSources.UserFile(i.Type);
                    if (file == null) return;
                    if (!await ConfirmDialog.ShowAsync(this, "Volver al original", $"Se borra tu versión editada ({Path.GetFileName(file)}) y vuelve la de la biblioteca. Los gráficos que lo usan siguen andando con el original.", "Volver al original", danger: true)) return;
                    try
                    {
                        File.Delete(file);
                        _svc.Scripts?.CompileAll();
                        FillAvailable();
                    }
                    catch (Exception ex)
                    {
                        await ConfirmDialog.ShowAsync(this, "No se pudo borrar", ex.Message, "Cerrar");
                    }
                };
                items.Add(restore);
                items.Add(HideItem(i, replaces));
            }
            else
            {
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
                        FillAvailable();
                    }
                    catch (Exception ex)
                    {
                        await ConfirmDialog.ShowAsync(this, "No se pudo borrar", ex.Message, "Cerrar");
                    }
                };
                items.Add(del);
            }
        }
        else
        {
            // editar directo (sin copiar ni renombrar): su código pasa a «Editados» y reemplaza al original en todos lados
            var edit = new MenuItem { Header = "Editar el código…", Icon = Ui.Icon(MaterialIconKind.CodeBraces, 16) };
            edit.Click += async (_, _) =>
            {
                try
                {
                    var path = IndicatorSources.EditInPlace(i.Type);
                    _svc.Scripts?.CompileAll();
                    new CodeEditorWindow(_svc, CodeKind.Indicator, path).Show(this);
                    FillAvailable();
                }
                catch (Exception ex)
                {
                    await ConfirmDialog.ShowAsync(this, "No se pudo editar", ex.Message, "Cerrar");
                }
            };
            items.Add(edit);
            items.Add(HideItem(i, i.Type.FullName!));
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
            var fork = new MenuItem { Header = "Copiar como indicador nuevo (con otro nombre)…", Icon = Ui.Icon(MaterialIconKind.ContentCopy, 16) };
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

        if (_svc.Catalog.Hidden.Count > 0)
        {
            items.Add(new Separator());
            var back = new MenuItem { Header = $"Volver a mostrar los quitados de la biblioteca ({_svc.Catalog.Hidden.Count})", Icon = Ui.Icon(MaterialIconKind.EyeOutline, 16) };
            back.Click += (_, _) =>
            {
                _svc.Catalog.Hidden.Clear();
                _svc.Settings.HiddenIndicators.Clear();
                _svc.Settings.Save();
                _svc.Catalog.NotifyChanged();
                FillAvailable();
            };
            items.Add(back);
        }

        menu.ItemsSource = items;
        return menu;
    }

    /// <summary>Quitar un indicador de la biblioteca (no se borra nada: los gráficos guardados lo siguen abriendo).</summary>
    private MenuItem HideItem(IndicatorInfo i, string fullName)
    {
        var hide = new MenuItem { Header = "Eliminar de la biblioteca…", Icon = Ui.Icon(MaterialIconKind.DeleteOutline, 16) };
        hide.Click += async (_, _) =>
        {
            if (!await ConfirmDialog.ShowAsync(this, "Eliminar de la biblioteca", $"«{i.Name}» deja de aparecer en la lista. Los gráficos que ya lo usan lo siguen mostrando, y podés volver a mostrarlo desde este mismo menú.", "Eliminar", danger: true)) return;
            _svc.Catalog.Hidden.Add(fullName);
            if (!_svc.Settings.HiddenIndicators.Contains(fullName)) _svc.Settings.HiddenIndicators.Add(fullName);
            if (i.Type.FullName != fullName && !_svc.Settings.HiddenIndicators.Contains(i.Type.FullName!))
            {
                _svc.Catalog.Hidden.Add(i.Type.FullName!);
                _svc.Settings.HiddenIndicators.Add(i.Type.FullName!);
            }

            _svc.Settings.Save();
            _svc.Catalog.NotifyChanged();
            FillAvailable();
        };
        return hide;
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
