using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;

namespace TradingHub.App.Views;

/// <summary>
/// Barra de dibujos de cada gráfico, a la izquierda (como en TradingView y OpenMarket): herramientas en grupos con la
/// flechita a la derecha de cada botón para elegir otra del mismo grupo (el botón queda con la última usada; doble clic la
/// deja fija), las favoritas juntas y, abajo, imán, seguir dibujando, dibujar con un gesto, la barrita después de dibujar,
/// bloquear, ocultar, borrar y elegir las favoritas.
/// </summary>
internal sealed class DrawingToolStrip : UserControl
{
    private sealed class Group
    {
        public required ToggleButton Button;
        public required DrawingKind?[] Kinds;
        public DrawingKind? Current;
    }

    internal static readonly Dictionary<DrawingKind, (MaterialIconKind Icon, string Name)> Info = new()
    {
        [DrawingKind.TrendLine] = (MaterialIconKind.ChartLineVariant, "Línea de tendencia"),
        [DrawingKind.Ray] = (MaterialIconKind.ArrowTopRightThin, "Rayo"),
        [DrawingKind.ExtendedLine] = (MaterialIconKind.ArrowExpandHorizontal, "Línea extendida"),
        [DrawingKind.HorizontalLine] = (MaterialIconKind.Minus, "Línea horizontal"),
        [DrawingKind.HorizontalRay] = (MaterialIconKind.ArrowRightThin, "Rayo horizontal"),
        [DrawingKind.VerticalLine] = (MaterialIconKind.DragVertical, "Línea vertical"),
        [DrawingKind.CrossLine] = (MaterialIconKind.Plus, "Línea cruzada"),
        [DrawingKind.Rectangle] = (MaterialIconKind.RectangleOutline, "Rectángulo / zona"),
        [DrawingKind.Ellipse] = (MaterialIconKind.EllipseOutline, "Elipse"),
        [DrawingKind.ParallelChannel] = (MaterialIconKind.VectorLine, "Canal paralelo (dos clics para la línea base; el tercer manejador, el ancho)"),
        [DrawingKind.FibRetracement] = (MaterialIconKind.FormatLineSpacing, "Retroceso de Fibonacci"),
        [DrawingKind.Brush] = (MaterialIconKind.Brush, "Pincel (a mano alzada)"),
        [DrawingKind.Highlighter] = (MaterialIconKind.FormatColorHighlight, "Resaltador"),
        [DrawingKind.Path] = (MaterialIconKind.VectorPolyline, "Trayectoria (un clic por punto; doble clic o Enter termina)"),
        [DrawingKind.Arrow] = (MaterialIconKind.ArrowTopRight, "Flecha"),
        [DrawingKind.Ruler] = (MaterialIconKind.RulerSquare, "Regla (medir; también con Shift + arrastre)"),
        [DrawingKind.LongPosition] = (MaterialIconKind.TrendingUp, "Posición larga (entrada, stop y objetivo)"),
        [DrawingKind.ShortPosition] = (MaterialIconKind.TrendingDown, "Posición corta (entrada, stop y objetivo)"),
        [DrawingKind.Text] = (MaterialIconKind.FormatText, "Texto"),
        [DrawingKind.AnchoredVwap] = (MaterialIconKind.Anchor, "VWAP anclado (desde el punto que elijas, calculado con cada operación)"),
        [DrawingKind.FixedRangeProfile] = (MaterialIconKind.ChartBar, "Perfil de rango fijo (volumen entre dos puntos; puede abarcar varias sesiones)"),
    };

    /// <summary>El atajo de teclado de cada herramienta («Alt+T»; lo pone la vista con los atajos de los ajustes).</summary>
    internal static Func<DrawingKind, string?>? ShortcutOf { get; set; }

    private static KeyGesture? GestureOf(DrawingKind k)
    {
        try
        {
            return ShortcutOf?.Invoke(k) is { Length: > 0 } g ? KeyGesture.Parse(g) : null;
        }
        catch
        {
            return null;
        }
    }

    private readonly List<Group> _groups = new();
    private readonly List<(DrawingKind Kind, ToggleButton Button)> _favButtons = new();
    private readonly StackPanel _favPanel = new() { Orientation = Orientation.Vertical, Spacing = 2 };
    private readonly Control _favDivider = Divider();
    private readonly ToggleButton _magnet, _sticky, _ink, _afterBar, _lock, _hide;
    private readonly Button _star;
    private MagnetMode _magnetMode, _lastMagnet = MagnetMode.Weak;
    private bool _syncing, _indicatorsHidden;
    private object? _lastClicked;
    private long _lastClickAt;
    private DrawingKind? _kept, _checked;
    private List<DrawingKind> _favorites = new();

    public DrawingToolStrip()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Thickness(4, 6, 2, 6) };
        DrawingKind?[][][] sections =
        {
            new[] { new DrawingKind?[] { null } },
            new[]
            {
                new DrawingKind?[] { DrawingKind.TrendLine, DrawingKind.Ray, DrawingKind.ExtendedLine, DrawingKind.HorizontalLine, DrawingKind.HorizontalRay, DrawingKind.VerticalLine, DrawingKind.CrossLine },
                new DrawingKind?[] { DrawingKind.Rectangle, DrawingKind.Ellipse, DrawingKind.ParallelChannel },
                new DrawingKind?[] { DrawingKind.FibRetracement },
            },
            new[] { new DrawingKind?[] { DrawingKind.Brush, DrawingKind.Highlighter, DrawingKind.Path }, new DrawingKind?[] { DrawingKind.Arrow } },
            new[] { new DrawingKind?[] { DrawingKind.AnchoredVwap, DrawingKind.FixedRangeProfile } },
            // a la vista, como en TradingView (antes compartían un botón y la corta quedaba escondida en el menú)
            new[] { new DrawingKind?[] { DrawingKind.LongPosition }, new DrawingKind?[] { DrawingKind.ShortPosition }, new DrawingKind?[] { DrawingKind.Ruler } },
            new[] { new DrawingKind?[] { DrawingKind.Text } },
        };
        foreach (var section in sections)
        {
            if (panel.Children.Count > 0) panel.Children.Add(Divider());
            foreach (var kinds in section)
            {
                var g = new Group { Button = new ToggleButton().Classes("dtool"), Kinds = kinds, Current = kinds[0] };
                UpdateButton(g);
                g.Button.Click += (_, _) => Clicked(g, g.Current);
                _groups.Add(g);
                panel.Children.Add(Row(g.Button, kinds.Length > 1 ? more => ShowGroup(g, more) : null, "Más herramientas de este grupo"));
            }
        }

        // las favoritas, juntas (se eligen con la estrella de abajo)
        panel.Children.Add(_favDivider);
        panel.Children.Add(_favPanel);
        _favDivider.IsVisible = _favPanel.IsVisible = false;

        // ---- abajo: cómo se dibuja y qué se hace con los dibujos
        panel.Children.Add(Divider());
        _magnet = new ToggleButton().Classes("dtool");
        _magnet.Click += (_, _) =>
        {
            if (_syncing) return;
            SetMagnet(_magnetMode == MagnetMode.Off ? _lastMagnet : MagnetMode.Off, raise: true);
        };
        panel.Children.Add(Row(_magnet, ShowMagnetMenu, "Imán débil o fuerte"));
        _sticky = Toggle(MaterialIconKind.PencilLockOutline, "Seguir dibujando: la herramienta queda activa después de cada dibujo (Esc la suelta; doble clic en una herramienta la deja fija solo a ella)", v => StickyChanged?.Invoke(v));
        panel.Children.Add(Row(_sticky, null, null));
        _ink = Toggle(MaterialIconKind.GestureTapHold, "Dibujar con un gesto: mantené apretado sobre el gráfico hasta que se llene el anillo y trazá la forma · mantené apretado este botón para ver los gestos", v => InkChanged?.Invoke(v));
        AddHold(_ink, () => ShowGuide(_ink));
        panel.Children.Add(Row(_ink, ShowInkMenu, "Gestos"));
        _afterBar = Toggle(MaterialIconKind.TuneVariant, "Mostrar la barrita del dibujo (color, grosor, alerta…) apenas se termina de dibujar", v => AfterBarChanged?.Invoke(v));
        panel.Children.Add(Row(_afterBar, null, null));
        _lock = Toggle(MaterialIconKind.LockOpenVariantOutline, "Bloquear todos los dibujos (se pueden elegir, pero no mover)", v =>
        {
            UpdateLockIcon();
            LockChanged?.Invoke(v);
        });
        panel.Children.Add(Row(_lock, null, null));
        _hide = Toggle(MaterialIconKind.EyeOutline, "Ocultar todos los dibujos (siguen guardados y sus alertas siguen andando); la flechita: ocultar los indicadores", v =>
        {
            UpdateHideIcon();
            HideChanged?.Invoke(v);
        });
        panel.Children.Add(Row(_hide, ShowHideMenu, "Ocultar dibujos o indicadores"));
        panel.Children.Add(Divider());
        var delete = new Button { Content = Ui.Icon(MaterialIconKind.DeleteOutline, 19) }.Classes("dtool");
        ToolTip.SetTip(delete, "Borrar el dibujo elegido (Supr); la flechita: borrar todos");
        delete.Click += (_, _) => DeleteSelectedRequested?.Invoke();
        panel.Children.Add(Row(delete, more =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop };
            var one = new MenuItem { Header = "Borrar el dibujo elegido", Icon = Ui.Icon(MaterialIconKind.DeleteOutline, 16) };
            one.Click += (_, _) => DeleteSelectedRequested?.Invoke();
            var all = new MenuItem { Header = "Borrar todos los dibujos de este instrumento", Icon = Ui.Icon(MaterialIconKind.DeleteSweepOutline, 16) };
            all.Click += (_, _) => ClearRequested?.Invoke();
            menu.Items.Add(one);
            menu.Items.Add(all);
            menu.ShowAt(more);
        }, "Borrar todos"));
        _star = new Button { Content = Ui.Icon(MaterialIconKind.StarOutline, 19) }.Classes("dtool");
        ToolTip.SetTip(_star, "Herramientas de dibujo favoritas");
        _star.Click += (_, _) => ShowFavoritesMenu(_star);
        panel.Children.Add(Row(_star, null, null));

        _groups[0].Button.IsChecked = true;
        SetMagnet(MagnetMode.Off, raise: false);
        UpdateLockIcon();
        UpdateHideIcon();
        Content = new Border
        {
            Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden },
            Background = Ui.Bg1,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
    }

    public event Action<DrawingKind?>? ToolSelected;

    /// <summary>Clic en una herramienta: la elige; el segundo clic seguido (doble clic) la deja fija.</summary>
    private void Clicked(object source, DrawingKind? kind)
    {
        long now = Environment.TickCount64;
        bool second = ReferenceEquals(_lastClicked, source) && now - _lastClickAt < 450;
        _lastClicked = second ? null : source;
        _lastClickAt = now;
        Check(kind);
        if (second && kind is { } k) ToolKept?.Invoke(k);
        else ToolSelected?.Invoke(kind);
    }

    /// <summary>Doble clic en una herramienta: queda fija hasta Esc o elegir otra.</summary>
    public event Action<DrawingKind>? ToolKept;

    public event Action? ClearRequested;
    public event Action? DeleteSelectedRequested;
    public event Action<MagnetMode>? MagnetChanged;
    public event Action<bool>? StickyChanged;
    public event Action<bool>? InkChanged;
    public event Action<bool>? AfterBarChanged;
    public event Action<bool>? LockChanged;
    public event Action<bool>? HideChanged;
    public event Action<bool>? HideIndicatorsChanged;
    public event Action<List<DrawingKind>>? FavoritesChanged;

    private static Control Divider() => new Border { Height = 1, Margin = new Thickness(6, 4, 8, 4) }.Classes("divider");

    /// <summary>Un botón con la flechita del grupo a su derecha (como en TradingView: se ve al pasar el mouse).</summary>
    private static Control Row(Control button, Action<Control>? more, string? moreTip)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,10") };
        grid.Children.Add(button);
        if (more != null)
        {
            var arrow = new Button { Content = Ui.Icon(MaterialIconKind.MenuRight, 12, Ui.Text2) }.Classes("dmore");
            if (moreTip != null) ToolTip.SetTip(arrow, moreTip);
            arrow.Click += (_, _) => more(arrow);
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(arrow);
            // clic derecho sobre el botón: también abre el grupo
            button.AddHandler(PointerReleasedEvent, (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Right) return;
                e.Handled = true;
                more(arrow);
            }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        }

        return grid;
    }

    /// <summary>Mantener apretado el botón (medio segundo) hace otra cosa; el clic de ese apretón no cambia el botón.</summary>
    private void AddHold(ToggleButton button, Action onHold)
    {
        DispatcherTimer? timer = null;
        bool held = false;
        button.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            held = false;
            timer?.Stop();
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Input, (_, _) =>
            {
                timer?.Stop();
                held = true;
                onHold();
            });
            timer.Start();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(PointerReleasedEvent, (_, _) => timer?.Stop(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(PointerCaptureLostEvent, (_, _) => timer?.Stop(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        button.IsCheckedChanged += (_, _) =>
        {
            if (!held || _syncing) return;
            // fue un apretón largo: el botón vuelve a como estaba
            held = false;
            _syncing = true;
            button.IsChecked = !button.IsChecked;
            _syncing = false;
        };
    }

    private ToggleButton Toggle(MaterialIconKind icon, string tip, Action<bool> changed)
    {
        var t = new ToggleButton { Content = Ui.Icon(icon, 19) }.Classes("dtool");
        ToolTip.SetTip(t, tip);
        t.IsCheckedChanged += (_, _) =>
        {
            if (!_syncing) changed(t.IsChecked == true);
        };
        return t;
    }

    private void UpdateLockIcon() => _lock.Content = Ui.Icon(_lock.IsChecked == true ? MaterialIconKind.LockOutline : MaterialIconKind.LockOpenVariantOutline, 19);

    private void UpdateHideIcon() => _hide.Content = Ui.Icon(_hide.IsChecked == true || _indicatorsHidden ? MaterialIconKind.EyeOffOutline : MaterialIconKind.EyeOutline, 19);

    private void SetMagnet(MagnetMode mode, bool raise)
    {
        _magnetMode = mode;
        if (mode != MagnetMode.Off) _lastMagnet = mode;
        _syncing = true;
        _magnet.IsChecked = mode != MagnetMode.Off;
        _syncing = false;
        _magnet.Content = Ui.Icon(mode == MagnetMode.Strong ? MaterialIconKind.MagnetOn : MaterialIconKind.Magnet, 19);
        ToolTip.SetTip(_magnet, mode switch
        {
            MagnetMode.Weak => "Imán débil: los puntos se pegan al máximo, mínimo, apertura o cierre de la vela cuando el puntero está cerca",
            MagnetMode.Strong => "Imán fuerte: los puntos siempre se pegan al máximo, mínimo, apertura o cierre de la vela",
            _ => "Imán apagado (Shift lo prende mientras lo tenés apretado)",
        });
        if (raise) MagnetChanged?.Invoke(mode);
    }

    private void ShowMagnetMenu(Control anchor)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop };
        foreach (var (mode, text, icon) in new[] { (MagnetMode.Weak, "Imán débil (cuando el puntero está cerca)", MaterialIconKind.Magnet), (MagnetMode.Strong, "Imán fuerte (siempre)", MaterialIconKind.MagnetOn), (MagnetMode.Off, "Sin imán", MaterialIconKind.Close) })
        {
            var m = mode;
            var item = new MenuItem { Header = text, Icon = Ui.Icon(_magnetMode == m ? MaterialIconKind.Check : icon, 16) };
            item.Click += (_, _) => SetMagnet(m, raise: true);
            menu.Items.Add(item);
        }

        menu.ShowAt(anchor);
    }

    private void ShowInkMenu(Control anchor)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop };
        bool on = _ink.IsChecked == true;
        var toggle = new MenuItem { Header = "Dibujar con un gesto", Icon = Ui.Icon(on ? MaterialIconKind.Check : MaterialIconKind.GestureTapHold, 16) };
        toggle.Click += (_, _) => _ink.IsChecked = !on;
        var guide = new MenuItem { Header = "Ver los gestos…", Icon = Ui.Icon(MaterialIconKind.HelpCircleOutline, 16) };
        guide.Click += (_, _) => ShowGuide(_ink);
        menu.Items.Add(toggle);
        menu.Items.Add(guide);
        menu.ShowAt(anchor);
    }

    /// <summary>La ayuda de los gestos: qué trazo da cada dibujo.</summary>
    internal static void ShowGuide(Control anchor)
    {
        var rows = new StackPanel { Spacing = 7, Width = 360 };
        rows.Children.Add(new TextBlock { Text = "Dibujar con un gesto", FontWeight = FontWeight.SemiBold, FontSize = 14 });
        rows.Children.Add(new TextBlock { Text = "Mantené apretado el botón del mouse sobre el gráfico, sin moverlo, hasta que se llene el anillo. Sin soltar, trazá la forma:", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        foreach (var (glyph, text) in new[]
                 {
                     ("M2,12 L30,12", "Raya horizontal → línea horizontal"),
                     ("M16,2 L16,22", "Raya vertical → línea vertical"),
                     ("M2,20 L30,4", "En diagonal → línea de tendencia (hasta el borde derecho: rayo)"),
                     ("M4,12 A12,8 0 1 1 28,12 A12,8 0 1 1 4,12", "Un círculo → elipse"),
                     ("M4,4 L28,4 L28,20 L4,20 Z", "Una caja → rectángulo"),
                     ("M2,20 L28,4 L19,5", "Una raya con un ganchito al final → flecha"),
                     ("M2,5 L28,5 L4,19 L30,19", "Una Z → retroceso de Fibonacci (entre la primera y la última raya)"),
                     ("M2,12 L7,4 L11,20 L15,4 L19,20 L23,4 L28,16", "Un garabato encima → borra los dibujos que toca"),
                     ("M2,18 L10,6 L18,14 L30,4", "Cualquier otra forma → trayectoria"),
                 })
        {
            var shape = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(glyph),
                Stroke = Ui.Accent,
                StrokeThickness = 1.8,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Width = 32,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(shape);
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 310 });
            rows.Children.Add(row);
        }

        rows.Children.Add(new TextBlock { Text = "Al soltar aparece una barrita para cambiarlo por otro tipo (de línea a rayo, de rectángulo a elipse…) o borrarlo. Con el imán, los puntos se pegan a las velas. Esc cancela el trazo.", TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" } });
        new Flyout { Placement = PlacementMode.RightEdgeAlignedTop, Content = new Border { Padding = new Thickness(8, 6), Child = rows } }.ShowAt(anchor);
    }

    private void ShowHideMenu(Control anchor)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop };
        bool drawings = _hide.IsChecked == true;
        var d = new MenuItem { Header = "Ocultar los dibujos", Icon = Ui.Icon(drawings ? MaterialIconKind.Check : MaterialIconKind.PencilOutline, 16) };
        d.Click += (_, _) => _hide.IsChecked = !drawings;
        var i = new MenuItem { Header = "Ocultar los indicadores", Icon = Ui.Icon(_indicatorsHidden ? MaterialIconKind.Check : MaterialIconKind.FunctionVariant, 16) };
        i.Click += (_, _) => SetIndicatorsHidden(!_indicatorsHidden, raise: true);
        bool all = drawings && _indicatorsHidden;
        var both = new MenuItem { Header = all ? "Mostrar todo" : "Ocultar todo", Icon = Ui.Icon(all ? MaterialIconKind.EyeOutline : MaterialIconKind.EyeOffOutline, 16) };
        both.Click += (_, _) =>
        {
            if (_hide.IsChecked == all) _hide.IsChecked = !all;
            if (_indicatorsHidden == all) SetIndicatorsHidden(!all, raise: true);
        };
        menu.Items.Add(d);
        menu.Items.Add(i);
        menu.Items.Add(new Separator());
        menu.Items.Add(both);
        menu.ShowAt(anchor);
    }

    private void SetIndicatorsHidden(bool hidden, bool raise)
    {
        _indicatorsHidden = hidden;
        UpdateHideIcon();
        if (raise) HideIndicatorsChanged?.Invoke(hidden);
    }

    private void ShowFavoritesMenu(Control anchor)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedBottom };
        menu.Items.Add(new MenuItem { Header = "Herramientas favoritas", IsEnabled = false, FontWeight = FontWeight.SemiBold });
        if (_favorites.Count == 0) menu.Items.Add(new MenuItem { Header = "Todavía no hay: elegilas abajo y quedan juntas en la barra", IsEnabled = false });
        foreach (var k in _favorites)
        {
            var kind = k;
            var item = new MenuItem { Header = Info[kind].Name, Icon = Ui.Icon(Info[kind].Icon, 16) };
            item.Click += (_, _) =>
            {
                Check(kind);
                ToolSelected?.Invoke(kind);
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "Elegir las favoritas", Icon = Ui.Icon(MaterialIconKind.StarOutline, 16) };
        foreach (var k in Info.Keys)
        {
            var kind = k;
            bool fav = _favorites.Contains(kind);
            var item = new MenuItem { Header = Info[kind].Name, Icon = Ui.Icon(fav ? MaterialIconKind.Star : Info[kind].Icon, 16, fav ? Ui.Accent : null) };
            item.Click += (_, _) =>
            {
                var list = _favorites.ToList();
                if (!list.Remove(kind)) list.Add(kind);
                SetFavorites(list);
                FavoritesChanged?.Invoke(list);
            };
            edit.Items.Add(item);
        }

        menu.Items.Add(edit);
        menu.ShowAt(anchor);
    }

    /// <summary>Las herramientas favoritas: se ven juntas en la barra.</summary>
    public void SetFavorites(IReadOnlyList<DrawingKind> favorites)
    {
        _favorites = favorites.Where(Info.ContainsKey).Distinct().ToList();
        _favPanel.Children.Clear();
        _favButtons.Clear();
        foreach (var k in _favorites)
        {
            var kind = k;
            var b = new ToggleButton { Content = Ui.Icon(Info[kind].Icon, 19) }.Classes("dtool");
            ToolTip.SetTip(b, Tip(kind));
            b.Click += (_, _) => Clicked(b, kind);
            _favButtons.Add((kind, b));
            _favPanel.Children.Add(Row(b, null, null));
        }

        _favDivider.IsVisible = _favPanel.IsVisible = _favorites.Count > 0;
        _star.Content = Ui.Icon(_favorites.Count > 0 ? MaterialIconKind.Star : MaterialIconKind.StarOutline, 19, _favorites.Count > 0 ? Ui.Accent : null);
        Check(_checked);
    }

    /// <summary>Muestra el estado del gráfico (sin avisar a nadie).</summary>
    public void SetStates(MagnetMode magnet, bool sticky, bool locked, bool hidden, bool ink, bool afterBar, bool indicatorsHidden)
    {
        SetMagnet(magnet, raise: false);
        _syncing = true;
        _sticky.IsChecked = sticky;
        _ink.IsChecked = ink;
        _afterBar.IsChecked = afterBar;
        _lock.IsChecked = locked;
        _hide.IsChecked = hidden;
        _syncing = false;
        _indicatorsHidden = indicatorsHidden;
        UpdateLockIcon();
        UpdateHideIcon();
    }

    private static string Tip(DrawingKind k) => ShortcutOf?.Invoke(k) is { Length: > 0 } s ? Info[k].Name + " · " + Services.Hotkeys.Pretty(s) : Info[k].Name;

    private void UpdateButton(Group g)
    {
        if (g.Current is { } k)
        {
            var icon = Ui.Icon(Info[k].Icon, 19);
            if (_kept == k)
            {
                // fija con doble clic: un puntito abajo a la derecha
                var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 5, Height = 5, Fill = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -6, -6) };
                g.Button.Content = new Panel { Children = { icon, dot } };
            }
            else g.Button.Content = icon;

            ToolTip.SetTip(g.Button, Tip(k) + (_kept == k ? "\nFija: sigue activa después de cada dibujo (Esc la suelta)" : "\nDoble clic: dejarla fija"));
        }
        else
        {
            g.Button.Content = Ui.Icon(MaterialIconKind.CursorDefaultOutline, 19);
            ToolTip.SetTip(g.Button, "Cursor (Esc)");
        }
    }

    private void ShowGroup(Group g, Control anchor)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop };
        foreach (var k in g.Kinds.OfType<DrawingKind>())
        {
            var kind = k;
            var item = new MenuItem { Header = Info[kind].Name, Icon = Ui.Icon(Info[kind].Icon, 16) };
            item.InputGesture = GestureOf(kind);
            if (Equals(g.Current, kind)) item.FontWeight = FontWeight.SemiBold;
            item.Click += (_, _) =>
            {
                g.Current = kind;
                UpdateButton(g);
                Check(kind);
                ToolSelected?.Invoke(kind);
            };
            menu.Items.Add(item);
        }

        menu.ShowAt(anchor);
    }

    /// <summary>La herramienta fija con doble clic (null = ninguna): su botón lleva un puntito.</summary>
    public void MarkKept(DrawingKind? kind)
    {
        if (_kept == kind) return;
        _kept = kind;
        foreach (var g in _groups) UpdateButton(g);
    }

    /// <summary>Marca la herramienta activa (null = cursor); el grupo pasa a mostrarla.</summary>
    public void Check(DrawingKind? kind)
    {
        _checked = kind;
        foreach (var g in _groups)
        {
            bool has = g.Kinds.Contains(kind);
            if (has && kind != null && !Equals(g.Current, kind))
            {
                g.Current = kind;
                UpdateButton(g);
            }

            g.Button.IsChecked = has;
        }

        foreach (var (k, b) in _favButtons) b.IsChecked = kind == k;
        if (kind == null || kind != _kept) MarkKept(null);
    }
}
