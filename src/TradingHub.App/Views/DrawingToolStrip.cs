using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Material.Icons;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;

namespace TradingHub.App.Views;

/// <summary>
/// Barra de dibujos de cada gráfico, a la izquierda (como en TradingView y OpenMarket): herramientas en grupos con la
/// flechita a la derecha de cada botón para elegir otra del mismo grupo (el botón queda con la última usada) y, abajo,
/// imán, seguir dibujando, bloquear, ocultar y borrar los dibujos.
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
        [DrawingKind.HorizontalLine] = (MaterialIconKind.Minus, "Línea horizontal"),
        [DrawingKind.HorizontalRay] = (MaterialIconKind.ArrowRightThin, "Rayo horizontal"),
        [DrawingKind.VerticalLine] = (MaterialIconKind.DragVertical, "Línea vertical"),
        [DrawingKind.TrendLine] = (MaterialIconKind.ChartLineVariant, "Línea de tendencia"),
        [DrawingKind.Ray] = (MaterialIconKind.ArrowTopRightThin, "Rayo"),
        [DrawingKind.ExtendedLine] = (MaterialIconKind.ArrowExpandHorizontal, "Línea extendida"),
        [DrawingKind.Rectangle] = (MaterialIconKind.RectangleOutline, "Rectángulo / zona"),
        [DrawingKind.ParallelChannel] = (MaterialIconKind.VectorLine, "Canal paralelo (dos clics para la línea base; el tercer manejador, el ancho)"),
        [DrawingKind.FibRetracement] = (MaterialIconKind.FormatLineSpacing, "Retroceso de Fibonacci"),
        [DrawingKind.Ruler] = (MaterialIconKind.RulerSquare, "Regla (medir)"),
        [DrawingKind.LongPosition] = (MaterialIconKind.TrendingUp, "Posición larga (entrada, stop y objetivo)"),
        [DrawingKind.ShortPosition] = (MaterialIconKind.TrendingDown, "Posición corta (entrada, stop y objetivo)"),
        [DrawingKind.Text] = (MaterialIconKind.FormatText, "Texto"),
        [DrawingKind.AnchoredVwap] = (MaterialIconKind.Anchor, "VWAP anclado (desde el punto que elijas, calculado con cada operación)"),
        [DrawingKind.FixedRangeProfile] = (MaterialIconKind.ChartBar, "Perfil de rango fijo (volumen entre dos puntos; puede abarcar varias sesiones)"),
    };

    private readonly List<Group> _groups = new();
    private readonly ToggleButton _magnet, _sticky, _lock, _hide;
    private MagnetMode _magnetMode, _lastMagnet = MagnetMode.Weak;
    private bool _syncing;

    public DrawingToolStrip()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Thickness(4, 6, 2, 6) };
        DrawingKind?[][][] sections =
        {
            new[] { new DrawingKind?[] { null } },
            new[]
            {
                new DrawingKind?[] { DrawingKind.TrendLine, DrawingKind.Ray, DrawingKind.ExtendedLine, DrawingKind.HorizontalLine, DrawingKind.HorizontalRay, DrawingKind.VerticalLine },
                new DrawingKind?[] { DrawingKind.Rectangle, DrawingKind.ParallelChannel },
                new DrawingKind?[] { DrawingKind.FibRetracement },
            },
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
                g.Button.Click += (_, _) =>
                {
                    Check(g.Current);
                    ToolSelected?.Invoke(g.Current);
                };
                _groups.Add(g);
                panel.Children.Add(Row(g.Button, kinds.Length > 1 ? more => ShowGroup(g, more) : null, "Más herramientas de este grupo"));
            }
        }

        // ---- abajo: cómo se dibuja y qué se hace con los dibujos
        panel.Children.Add(Divider());
        _magnet = new ToggleButton().Classes("dtool");
        _magnet.Click += (_, _) =>
        {
            if (_syncing) return;
            SetMagnet(_magnetMode == MagnetMode.Off ? _lastMagnet : MagnetMode.Off, raise: true);
        };
        panel.Children.Add(Row(_magnet, ShowMagnetMenu, "Imán débil o fuerte"));
        _sticky = Toggle(MaterialIconKind.PencilLockOutline, "Seguir dibujando: la herramienta queda activa después de cada dibujo (Esc la suelta)", v => StickyChanged?.Invoke(v));
        panel.Children.Add(Row(_sticky, null, null));
        _lock = Toggle(MaterialIconKind.LockOpenVariantOutline, "Bloquear todos los dibujos (se pueden elegir, pero no mover)", v =>
        {
            UpdateLockIcon();
            LockChanged?.Invoke(v);
        });
        panel.Children.Add(Row(_lock, null, null));
        _hide = Toggle(MaterialIconKind.EyeOutline, "Ocultar todos los dibujos (siguen guardados y sus alertas siguen andando)", v =>
        {
            UpdateHideIcon();
            HideChanged?.Invoke(v);
        });
        panel.Children.Add(Row(_hide, null, null));
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
    public event Action? ClearRequested;
    public event Action? DeleteSelectedRequested;
    public event Action<MagnetMode>? MagnetChanged;
    public event Action<bool>? StickyChanged;
    public event Action<bool>? LockChanged;
    public event Action<bool>? HideChanged;

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

    private void UpdateHideIcon() => _hide.Content = Ui.Icon(_hide.IsChecked == true ? MaterialIconKind.EyeOffOutline : MaterialIconKind.EyeOutline, 19);

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

    /// <summary>Muestra el estado del gráfico (sin avisar a nadie).</summary>
    public void SetStates(MagnetMode magnet, bool sticky, bool locked, bool hidden)
    {
        SetMagnet(magnet, raise: false);
        _syncing = true;
        _sticky.IsChecked = sticky;
        _lock.IsChecked = locked;
        _hide.IsChecked = hidden;
        _syncing = false;
        UpdateLockIcon();
        UpdateHideIcon();
    }

    private static void UpdateButton(Group g)
    {
        if (g.Current is { } k)
        {
            g.Button.Content = Ui.Icon(Info[k].Icon, 19);
            ToolTip.SetTip(g.Button, Info[k].Name);
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
            if (Equals(g.Current, kind)) item.FontWeight = Avalonia.Media.FontWeight.SemiBold;
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

    /// <summary>Marca la herramienta activa (null = cursor); el grupo pasa a mostrarla.</summary>
    public void Check(DrawingKind? kind)
    {
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
    }
}
