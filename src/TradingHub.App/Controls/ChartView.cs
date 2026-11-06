using Avalonia;
using Avalonia.Input;
using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Rendering;

namespace TradingHub.App.Controls;

/// <summary>Control de gráfico: dibuja un <see cref="ChartModel"/> y traduce el mouse/teclado al controlador.</summary>
public sealed class ChartView : SkiaView
{
    private readonly ChartRenderer _renderer = new();
    private readonly ChartRenderState _state = new();
    private ChartModel? _model;
    private ChartController? _controller;

    public ChartModel? Model
    {
        get => _model;
        set
        {
            if (ReferenceEquals(_model, value)) return;
            _model = value;
            _controller = value != null ? new ChartController(value, _state) : null;
            if (_controller != null) _controller.ToolFinished += () => ToolFinished?.Invoke();
            if (_controller != null) _controller.DrawingsChanged += () => DrawingsChanged?.Invoke();
            if (_controller != null) _controller.OrderDragged += (o, p) => OrderDragged?.Invoke(o, p);
            if (_controller != null) _controller.BracketDropped += (d, p) => BracketDropped?.Invoke(d, p);
            if (_controller != null) _controller.DrawingDoubleClicked += d => DrawingDoubleClicked?.Invoke(d);
            if (_controller != null) _controller.DrawingDefaults = k => DrawingDefaults?.Invoke(k);
            if (_controller != null)
            {
                _controller.Magnet = _magnet;
                _controller.StickyTool = _sticky;
            }

            if (_controller != null) _controller.IndicatorsChanged += () => IndicatorsChanged?.Invoke();
            if (_controller != null) _controller.IndicatorSettingsRequested += slot => IndicatorSettingsRequested?.Invoke(slot);
            if (_controller != null) _controller.StrategySettingsRequested += st => StrategySettingsRequested?.Invoke(st);
            if (_controller != null) _controller.StrategyRemoveRequested += st => StrategyRemoveRequested?.Invoke(st);
            if (_controller != null) _controller.TimeZoneRequested += () => TimeZoneRequested?.Invoke();
            InvalidateVisual();
        }
    }

    public ChartController? Controller => _controller;

    private TradingHub.Charting.MagnetMode _magnet;
    private bool _sticky;

    /// <summary>Imán de los dibujos (también para los gráficos que se arman después).</summary>
    public TradingHub.Charting.MagnetMode Magnet
    {
        get => _magnet;
        set
        {
            _magnet = value;
            if (_controller != null) _controller.Magnet = value;
        }
    }

    /// <summary>Seguir dibujando: la herramienta queda activa después de cada dibujo.</summary>
    public bool StickyTool
    {
        get => _sticky;
        set
        {
            _sticky = value;
            if (_controller != null) _controller.StickyTool = value;
        }
    }

    public event Action? ToolFinished;
    public event Action? DrawingsChanged;

    /// <summary>Orden propia arrastrada a un nuevo precio.</summary>
    public event Action<TradingHub.Trading.Order, decimal>? OrderDragged;

    /// <summary>Stop/objetivo soltado (desde la posición o una orden pendiente).</summary>
    public event Action<BracketDrag, decimal>? BracketDropped;

    /// <summary>Pedido de menú contextual (clic derecho) con la posición.</summary>
    public event Action<ChartView, Point>? MenuRequested;

    /// <summary>La cruz del cursor se movió (instante y precio bajo el mouse; null al salir del gráfico).</summary>
    public event Action<DateTime?, double?>? CrosshairMoved;

    /// <summary>Doble clic sobre un dibujo (abre su panel de configuración).</summary>
    public event Action<TradingHub.Charting.Drawings.ChartDrawing>? DrawingDoubleClicked;

    /// <summary>Se ocultó, mostró o quitó un indicador desde la leyenda.</summary>
    public event Action? IndicatorsChanged;

    /// <summary>Clic en la hora de la esquina del eje de tiempo.</summary>
    public event Action? TimeZoneRequested;

    /// <summary>Un carácter tecleado sobre el gráfico sin modificadores (entrada rápida de símbolo / período).</summary>
    public event Action<string>? Typed;

    /// <summary>Ajustes de un indicador pedidos desde la leyenda.</summary>
    public event Action<IndicatorSlot>? IndicatorSettingsRequested;

    /// <summary>Configurar o quitar una estrategia del gráfico (desde la leyenda).</summary>
    public event Action<StrategyOverlay>? StrategySettingsRequested;

    public event Action<StrategyOverlay>? StrategyRemoveRequested;

    /// <summary>Estilo por defecto de cada tipo de dibujo.</summary>
    public Func<TradingHub.Charting.Drawings.DrawingKind, TradingHub.Charting.Drawings.ChartDrawing?>? DrawingDefaults { get; set; }

    /// <summary>Muestra la cruz de otro gráfico vinculado (instante y, si es el mismo instrumento, precio).</summary>
    public void SetLinkedCrosshair(DateTime? time, double? price)
    {
        if (_state.LinkedTime == time && _state.LinkedPrice == price) return;
        _state.LinkedTime = time;
        _state.LinkedPrice = price;
        InvalidateVisual();
    }

    private void RaiseCrosshair(Point p)
    {
        if (CrosshairMoved == null || _model?.LastLayout is not { } l) return;
        if (!l.Plot.Contains((float)p.X, (float)p.Y) || _model.Series.Count == 0)
        {
            CrosshairMoved(null, null);
            return;
        }

        var time = TradingHub.Charting.Drawings.ChartDrawing.TimeAt(l, _model.Series, (float)p.X);
        double? price = l.PanelAt((float)p.Y)?.IsPrice == true ? l.Main.ValueAt((float)p.Y) : null;
        CrosshairMoved(time, price);
    }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        if (_model == null)
        {
            canvas.Clear(new SKColor(6, 10, 20));
            return;
        }

        _renderer.Render(canvas, _model, width, height, _state);
    }

    private static InputModifiers Mods(KeyModifiers k)
    {
        var m = InputModifiers.None;
        if (k.HasFlag(KeyModifiers.Shift)) m |= InputModifiers.Shift;
        if (k.HasFlag(KeyModifiers.Control)) m |= InputModifiers.Control;
        if (k.HasFlag(KeyModifiers.Alt)) m |= InputModifiers.Alt;
        if (k.HasFlag(KeyModifiers.Meta)) m |= InputModifiers.Command;
        return m;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_controller == null) return;
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var mods = Mods(e.KeyModifiers);
        if (props.IsRightButtonPressed)
        {
            MenuRequested?.Invoke(this, p);
            e.Handled = true;
            return;
        }

        if (_controller.TryCompletePending((float)p.X, (float)p.Y, mods))
        {
            InvalidateVisual();
            return;
        }

        var button = props.IsMiddleButtonPressed ? PointerButton.Middle : PointerButton.Left;
        _controller.MouseDown((float)p.X, (float)p.Y, button, e.ClickCount, mods);
        e.Pointer.Capture(this);
        UpdateCursor();
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_controller == null) return;
        var p = e.GetPosition(this);
        var mods = Mods(e.KeyModifiers);
        if (_controller.HasPending) _controller.HoverPending((float)p.X, (float)p.Y, mods);
        _controller.MouseMove((float)p.X, (float)p.Y, mods);
        UpdateCursor();
        InvalidateVisual();
        RaiseCrosshair(p);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_controller == null) return;
        var p = e.GetPosition(this);
        _controller.MouseUp((float)p.X, (float)p.Y, e.InitialPressMouseButton == MouseButton.Right ? PointerButton.Right : PointerButton.Left, Mods(e.KeyModifiers));
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_controller == null) return;
        var p = e.GetPosition(this);
        _controller.Wheel((float)p.X, (float)p.Y, (float)e.Delta.X, (float)e.Delta.Y, Mods(e.KeyModifiers));
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _controller?.Leave();
        InvalidateVisual();
        CrosshairMoved?.Invoke(null, null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_controller == null) return;
        // Alt+R: restablecer la vista, como en TradingView
        if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.Alt && _model != null)
        {
            _model.ResetView();
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (_controller.ForwardKey(e) || _controller.KeyDown(e.Key.ToString(), Mods(e.KeyModifiers)))
        {
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || _model == null || string.IsNullOrEmpty(e.Text)) return;
        string t = e.Text;
        if (t.Length == 1 && (char.IsLetterOrDigit(t[0]) || t[0] is '.' or '?' or '/'))
        {
            Typed?.Invoke(t);
            e.Handled = true;
        }
    }

    private void UpdateCursor()
    {
        if (_controller == null) return;
        Cursor = _controller.Cursor switch
        {
            StdCursor.SizeNS => new Cursor(StandardCursorType.SizeNorthSouth),
            StdCursor.SizeWE => new Cursor(StandardCursorType.SizeWestEast),
            StdCursor.SizeAll => new Cursor(StandardCursorType.SizeAll),
            StdCursor.Hand => new Cursor(StandardCursorType.Hand),
            StdCursor.Arrow => new Cursor(StandardCursorType.Arrow),
            StdCursor.IBeam => new Cursor(StandardCursorType.Ibeam),
            _ => new Cursor(StandardCursorType.Cross),
        };
    }
}
