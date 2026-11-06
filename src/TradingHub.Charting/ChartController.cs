using SkiaSharp;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Common.Logging;
using TradingHub.Rendering;
using TradingHub.Rendering.Control;
using TradingHub.Trading;

namespace TradingHub.Charting;

public enum PointerButton
{
    Left,
    Right,
    Middle,
}

[Flags]
public enum InputModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Command = 8,
}

/// <summary>
/// Interacción con el gráfico (independiente de la UI): desplazar, hacer zoom, escalar ejes,
/// dibujar y mover objetos, y reenviar el mouse a los indicadores.
/// </summary>
public sealed class ChartController
{
    private enum DragMode
    {
        None,
        Pan,
        PriceScale,
        TimeScale,
        CreateDrawing,
        MoveDrawing,
        PanelResize,
        MoveOrder,
        Bracket,
        ZoomBox,
        Freehand,
        Ink,
        Measure,
    }

    private readonly ChartModel _model;
    private readonly ChartRenderState _state;
    private DragMode _drag;
    private float _downX, _downY, _lastX, _lastY;
    private double _startScroll, _startTop, _startBottom, _startBarWidth;
    private ChartDrawing? _active;
    private int _activeHandle;
    private List<ChartPoint>? _startPoints;
    private SubPanel? _resizePanel;
    private Order? _dragOrder;

    public ChartController(ChartModel model, ChartRenderState state)
    {
        _model = model;
        _state = state;
    }

    /// <summary>Herramienta de dibujo activa (null = cursor normal).</summary>
    public DrawingKind? ActiveTool { get; set; }

    /// <summary>Si la herramienta queda activa después de dibujar (si no, vuelve al cursor).</summary>
    public bool StickyTool { get; set; }

    /// <summary>Imán de los dibujos: pega los puntos al máximo, mínimo, apertura o cierre de la vela (Shift lo fuerza siempre).</summary>
    public MagnetMode Magnet { get; set; }

    /// <summary>
    /// Dibujar con un gesto (como en OpenMarket): mantener apretado el botón sobre el gráfico sin moverse y, cuando se
    /// llena el anillo, trazar la forma sin soltar; al soltar se reconoce y queda el dibujo.
    /// </summary>
    public bool InkGestures { get; set; }

    /// <summary>Herramienta que quedó fija con doble clic en su botón (no vuelve al cursor después de cada dibujo).</summary>
    public DrawingKind? KeepTool { get; set; }

    /// <summary>Terminó un gesto: el dibujo que dejó o lo que borró el garabato.</summary>
    public event Action<InkResult>? InkFinished;

    /// <summary>Se eligió un dibujo con un clic.</summary>
    public event Action<ChartDrawing>? DrawingSelected;

    public ChartDrawing? Selected => _model.Drawings.FirstOrDefault(d => d.Selected);

    /// <summary>Cursor sugerido para la posición actual.</summary>
    public StdCursor Cursor { get; private set; } = StdCursor.Cross;

    /// <summary>Estilo por defecto de cada tipo de dibujo (lo que el usuario guardó como predeterminado).</summary>
    public Func<DrawingKind, ChartDrawing?>? DrawingDefaults { get; set; }

    public event Action? ToolFinished;
    public event Action<ChartDrawing>? DrawingDoubleClicked;
    public event Action? DrawingsChanged;

    /// <summary>Clic en la hora de la esquina del eje de tiempo: elegir la zona horaria del gráfico.</summary>
    public event Action? TimeZoneRequested;

    /// <summary>Se ocultó, mostró o quitó un indicador desde la leyenda del gráfico.</summary>
    public event Action? IndicatorsChanged;

    /// <summary>Pidieron los ajustes de un indicador desde la leyenda (engranaje o doble clic en el nombre).</summary>
    public event Action<IndicatorSlot>? IndicatorSettingsRequested;

    /// <summary>Configurar o quitar una estrategia del gráfico desde la leyenda.</summary>
    public event Action<StrategyOverlay>? StrategySettingsRequested;

    public event Action<StrategyOverlay>? StrategyRemoveRequested;

    /// <summary>Una orden propia arrastrada a un nuevo precio (la vista decide si confirma).</summary>
    public event Action<Order, decimal>? OrderDragged;

    /// <summary>Se soltó un stop/objetivo arrastrado desde la posición o una orden pendiente.</summary>
    public event Action<BracketDrag, decimal>? BracketDropped;

    /// <summary>Zona de arrastre de stop/objetivo bajo el mouse (botones primero, después las líneas).</summary>
    public BracketHandle? HandleAt(float x, float y, bool buttonsOnly)
    {
        foreach (var h in _state.Handles)
            if (h.IsButton && h.Rect.Contains(x, y)) return h;
        if (buttonsOnly) return null;
        foreach (var h in _state.Handles)
            if (!h.IsButton && h.Rect.Contains(x, y)) return h;
        return null;
    }

    /// <summary>Orden propia cuya línea está bajo el mouse (a menos de 5 px).</summary>
    public Order? OrderAt(float x, float y)
    {
        var l = L;
        var acc = _model.Account;
        if (l == null || acc == null || !l.Main.Rect.Contains(x, y)) return null;
        Order? best = null;
        float bestD = 5;
        foreach (var o in acc.ActiveOrders)
        {
            decimal price = o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price;
            float d = Math.Abs(l.Main.Y(price) - y);
            if (d <= bestD)
            {
                bestD = d;
                best = o;
            }
        }

        return best;
    }

    private ChartLayout? L => _model.LastLayout;

    // ======================================================================= mouse

    public void MouseDown(float x, float y, PointerButton button, int clicks, InputModifiers mods)
    {
        var l = L;
        if (l == null) return;
        _downX = _lastX = x;
        _downY = _lastY = y;
        _state.MouseX = x;
        _state.MouseY = y;
        _inkCandidate = false;
        _state.InkHold = null;
        // la medición con Shift queda hasta el próximo clic
        if (_state.Measure != null)
        {
            _state.Measure = null;
            _model.Invalidate();
        }

        // eje de precios
        if (l.PriceAxis.Contains(x, y))
        {
            if (_state.AutoScaleButton is { } ab && ab.Contains(x, y))
            {
                // como la «A» de TradingView: prende la escala automática (apagarla: arrastrar el eje o el gráfico)
                _model.Viewport.AutoScale = !_model.Viewport.AutoScale;
                if (!_model.Viewport.AutoScale)
                {
                    _model.Viewport.PriceTop = l.Main.Top;
                    _model.Viewport.PriceBottom = l.Main.Bottom;
                }

                _model.Invalidate();
                return;
            }

            if (clicks >= 2)
            {
                _model.Viewport.AutoScale = true;
                _model.Invalidate();
                return;
            }

            _drag = DragMode.PriceScale;
            _startTop = l.Main.Top;
            _startBottom = l.Main.Bottom;
            return;
        }

        // hora y zona horaria (esquina del eje de tiempo): elegir la zona
        if (_state.TimeZoneButton is { } tzb && tzb.Contains(x, y))
        {
            TimeZoneRequested?.Invoke();
            return;
        }

        // eje de tiempo
        if (l.TimeAxis.Contains(x, y))
        {
            if (clicks >= 2)
            {
                _model.Viewport.ScrollBars = 0;
                _model.Invalidate();
                return;
            }

            _drag = DragMode.TimeScale;
            _startBarWidth = _model.Viewport.BarWidth;
            _startScroll = _model.Viewport.ScrollBars;
            return;
        }

        if (!l.Plot.Contains(x, y)) return;

        // botón para volver a lo último y leyenda de indicadores (ojo, ajustes, quitar, plegar)
        if (button == PointerButton.Left)
        {
            if (_state.GoLatestButton is { } gl && gl.Contains(x, y))
            {
                _model.Viewport.ScrollBars = 0;
                _model.Viewport.AutoScale = true;
                _model.Invalidate();
                return;
            }

            // la ✕ de la operación resaltada
            if (_state.FocusTradeClose is { } fc && fc.Contains(x, y) && _model.FocusTrade != null)
            {
                _model.FocusTrade = null;
                _model.Invalidate();
                return;
            }

            foreach (var h in _state.Legend)
            {
                if (!h.Rect.Contains(x, y)) continue;
                if (h.Action == LegendAction.Name && clicks < 2) break; // clic simple en el nombre: se mueve el gráfico como siempre
                LegendClicked(h);
                return;
            }
        }

        // separadores de paneles
        foreach (var p in l.Panels.Skip(1))
        {
            if (Math.Abs(y - p.Rect.Top) <= 3 && p.SubPanel != null)
            {
                _drag = DragMode.PanelResize;
                _resizePanel = p.SubPanel;
                return;
            }
        }

        // indicadores (por si quieren el clic)
        if (button == PointerButton.Left && ForwardToIndicators(x, y, button, clicks, mods, MouseKind.Down)) return;

        // botones SL/TP de la posición o de una orden pendiente
        if (button == PointerButton.Left && ActiveTool == null && HandleAt(x, y, buttonsOnly: true) is { } button1)
        {
            StartBracketDrag(button1, y);
            return;
        }

        // arrastrar una orden propia a otro precio
        if (button == PointerButton.Left && ActiveTool == null && OrderAt(x, y) is { } order)
        {
            _dragOrder = order;
            _state.DragOrder = order;
            _state.DragPrice = order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Price;
            _drag = DragMode.MoveOrder;
            _model.Invalidate();
            return;
        }

        // línea de la posición o de un stop/objetivo adjunto a una orden pendiente
        if (button == PointerButton.Left && ActiveTool == null && HandleAt(x, y, buttonsOnly: false) is { } band)
        {
            StartBracketDrag(band, y);
            return;
        }

        if (button == PointerButton.Left && ActiveTool is { } tool && l.Main.Rect.Contains(x, y))
        {
            if (tool is DrawingKind.Brush or DrawingKind.Highlighter)
            {
                // a mano alzada: los puntos van saliendo mientras se arrastra (sin imán ni redondeo al tick)
                _active = new ChartDrawing { Kind = tool, Points = { RawPoint(l, x, y) }, Color = _model.Theme.DrawingDefault };
                if (DrawingDefaults?.Invoke(tool) is { } brushStyle) _active.CopyStyleFrom(brushStyle);
                foreach (var d in _model.Drawings) d.Selected = false;
                _active.Selected = true;
                _model.Drawings.Add(_active);
                _drag = DragMode.Freehand;
                _model.Invalidate();
                return;
            }

            var pt = PointAt(x, y, mods);
            _active = new ChartDrawing { Kind = tool, Points = { pt }, Color = _model.Theme.DrawingDefault };
            if (DrawingDefaults?.Invoke(tool) is { } template) _active.CopyStyleFrom(template);
            if (_active.IsPosition)
            {
                // posición con stop y objetivo 1:2 de entrada; después se ajusta con los manejadores
                double tick = (double)_model.Instrument.TickSize;
                double risk = Math.Max(Math.Abs(l.Main.Top - l.Main.Bottom) * 0.06, tick * 4);
                if (tick > 0) risk = Math.Round(risk / tick) * tick;
                float span = (float)Math.Min(Math.Max(_model.Viewport.BarWidth * 24, 180), l.Plot.Width * 0.4);
                var end = ChartDrawing.TimeAt(l, _model.Series, x + span);
                _active.InitPosition(risk, end);
            }

            if (_active.RequiredPoints > 1) _active.Points.Add(pt);
            foreach (var d in _model.Drawings) d.Selected = false;
            _active.Selected = true;
            _model.Drawings.Add(_active);
            _activeHandle = _active.Points.Count - 1;
            if (_active.RequiredPoints == 1)
            {
                FinishDrawing();
            }
            else
            {
                _drag = DragMode.CreateDrawing;
            }

            _model.Invalidate();
            return;
        }

        if (button == PointerButton.Left)
        {
            // ¿tocó un objeto dibujado? (ocultos no se tocan)
            for (int i = _model.Settings.HideDrawings ? -1 : _model.Drawings.Count - 1; i >= 0; i--)
            {
                var d = _model.Drawings[i];
                if (!d.HitTest(l, _model.Series, x, y, out int handle)) continue;
                foreach (var o in _model.Drawings) o.Selected = false;
                d.Selected = true;
                DrawingSelected?.Invoke(d);
                if (clicks == 2) // un triple clic no abre dos paneles
                {
                    DrawingDoubleClicked?.Invoke(d);
                    _model.Invalidate();
                    return;
                }

                if (!d.Locked && !_model.Settings.LockDrawings)
                {
                    _active = d;
                    _activeHandle = handle;
                    _startPoints = d.Points.ToList();
                    _drag = DragMode.MoveDrawing;
                }

                _model.Invalidate();
                return;
            }

            bool hadSelection = _model.Drawings.Any(d => d.Selected);
            foreach (var o in _model.Drawings) o.Selected = false;
            if (hadSelection) _model.Invalidate();

            if (clicks >= 2)
            {
                _model.Viewport.AutoScale = true;
                _model.Invalidate();
                return;
            }

            // Shift + arrastre: medir (como la regla, pero no queda guardado)
            if ((mods & InputModifiers.Shift) != 0 && ActiveTool == null && l.Main.Rect.Contains(x, y))
            {
                var m0 = PointAt(x, y, InputModifiers.None);
                _state.Measure = new ChartDrawing { Kind = DrawingKind.Ruler, Points = { m0, m0 } };
                _drag = DragMode.Measure;
                _model.Invalidate();
                return;
            }

            // Ctrl + arrastre: rectángulo de zoom (se amplía a lo que encierra)
            if ((mods & (InputModifiers.Control | InputModifiers.Command)) != 0 && ActiveTool == null)
            {
                _drag = DragMode.ZoomBox;
                _state.ZoomBox = new SKRect(x, y, x, y);
                return;
            }

            _drag = DragMode.Pan;
            _startScroll = _model.Viewport.ScrollBars;
            _startTop = l.Main.Top;
            _startBottom = l.Main.Bottom;
            _startAuto = _model.Viewport.AutoScale;
            // mantener apretado sin moverse: dibujar con un gesto
            _inkCandidate = InkGestures && clicks == 1 && !_model.Settings.LockDrawings && l.Main.Rect.Contains(x, y);
            _downTick = Environment.TickCount64;
        }
    }

    // ======================================================================= dibujar con un gesto

    private bool _inkCandidate, _startAuto;
    private long _downTick;
    private readonly List<SKPoint> _ink = new();

    /// <summary>Si está esperando que se mantenga apretado (la vista lo llama seguido mientras tanto).</summary>
    public bool InkPending => _inkCandidate || _drag == DragMode.Ink;

    /// <summary>
    /// Avanza la espera del gesto: llena el anillo y, a los <see cref="InkRecognizer.HoldMs"/> ms sin moverse, empieza a
    /// dibujar. Devuelve true si hay que redibujar.
    /// </summary>
    public bool InkTick()
    {
        if (!_inkCandidate || _drag != DragMode.Pan)
        {
            _inkCandidate = false;
            if (_state.InkHold == null) return false;
            _state.InkHold = null;
            return true;
        }

        long elapsed = Environment.TickCount64 - _downTick;
        if (elapsed >= InkRecognizer.HoldMs)
        {
            BeginInk();
            return true;
        }

        if (elapsed < InkRecognizer.RingStartMs) return false;
        _state.InkHold = (elapsed - InkRecognizer.RingStartMs) / (float)(InkRecognizer.HoldMs - InkRecognizer.RingStartMs);
        _state.InkHoldAt = new SKPoint(_downX, _downY);
        _model.Invalidate();
        return true;
    }

    private void BeginInk()
    {
        // deshace lo poquito que se haya corrido el gráfico mientras se mantenía apretado
        var vp = _model.Viewport;
        vp.ScrollBars = _startScroll;
        vp.AutoScale = _startAuto;
        if (!vp.AutoScale)
        {
            vp.PriceTop = _startTop;
            vp.PriceBottom = _startBottom;
        }

        _inkCandidate = false;
        _state.InkHold = null;
        _drag = DragMode.Ink;
        _ink.Clear();
        _ink.Add(new SKPoint(_downX, _downY));
        _state.InkStroke = _ink;
        _state.InkVictims = null;
        _model.Invalidate();
    }

    private void CancelInk()
    {
        _drag = DragMode.None;
        _ink.Clear();
        _state.InkStroke = null;
        _state.InkVictims = null;
        _state.InkHold = null;
        _model.Invalidate();
    }

    private void InkMove(float x, float y)
    {
        var last = _ink[^1];
        if ((last.X - x) * (last.X - x) + (last.Y - y) * (last.Y - y) < 2.25f) return;
        _ink.Add(new SKPoint(x, y));
        // si va saliendo un garabato, se apaga lo que va a borrar
        if (L is { } l && _ink.Count % 3 == 0)
        {
            var shape = InkRecognizer.Recognize(_ink, l.Main.Rect.Right);
            _state.InkVictims = shape?.Kind == InkShapeKind.Scribble ? InkDrawings.ScribbleVictims(_model, l, shape.Raw).ToHashSet() : null;
        }
    }

    private void CommitInk(float x, float y)
    {
        var l = L;
        var stroke = _ink.ToList();
        CancelInk();
        if (l == null) return;
        var shape = InkRecognizer.Recognize(stroke, l.Main.Rect.Right);
        if (shape == null) return; // solo se mantuvo apretado, sin trazar nada

        if (shape.Kind == InkShapeKind.Scribble)
        {
            var victims = InkDrawings.ScribbleVictims(_model, l, shape.Raw);
            var removed = victims.Select(v => (_model.Drawings.IndexOf(v), v)).ToList();
            foreach (var v in victims) _model.Drawings.Remove(v);
            if (victims.Count > 0) DrawingsChanged?.Invoke();
            _model.Invalidate();
            InkFinished?.Invoke(new InkResult { Erase = true, Removed = removed, X = x, Y = y });
            return;
        }

        if (InkDrawings.KindOf(shape.Kind) is not { } kind) return;
        bool unhid = _model.Settings.HideDrawings;
        _model.Settings.HideDrawings = false;
        var g = GeometryOf(l, shape);
        var d = new ChartDrawing { Kind = kind, Color = _model.Theme.DrawingDefault };
        if (DrawingDefaults?.Invoke(kind) is { } template) d.CopyStyleFrom(template);
        d.Points = InkDrawings.PointsFor(kind, g);
        foreach (var o in _model.Drawings) o.Selected = false;
        d.Selected = true;
        _model.Drawings.Add(d);
        DrawingsChanged?.Invoke();
        _model.Invalidate();
        InkFinished?.Invoke(new InkResult { Drawing = d, Geometry = g, Alternatives = InkDrawings.Alternatives(kind), X = x, Y = y, Unhid = unhid });
    }

    /// <summary>El trazo reconocido pasado a tiempo y precio (con el imán como esté).</summary>
    private InkGeometry GeometryOf(ChartLayout l, InkShape shape)
    {
        var main = l.Main.Rect;
        ChartPoint At(SKPoint p) => PointAt(Math.Clamp(p.X, main.Left, main.Right), Math.Clamp(p.Y, main.Top, main.Bottom), InputModifiers.None);
        var start = At(shape.A);
        if (shape.Kind == InkShapeKind.Vertical && _model.Series.Count > 0)
        {
            // la vertical va en una vela
            int bar = Math.Clamp(l.BarAt(shape.A.X), 0, _model.Series.Count - 1);
            start = new ChartPoint(_model.Series[bar].Time, start.Price);
        }

        return new InkGeometry
        {
            Start = start,
            End = At(shape.B),
            Level = shape.Kind == InkShapeKind.Horizontal && shape.Level is { } y ? LevelPrice(l, shape, y) : null,
            Corners = shape.Corners.Select(At).ToList(),
            Raw = InkRecognizer.Simplify(shape.Raw, 0.75f).Select(p => RawPoint(l, p.X, p.Y)).ToList(),
        };
    }

    /// <summary>Precio de una línea horizontal hecha con el gesto: con imán, el máximo/mínimo/apertura/cierre más cercano de las velas que cruzó.</summary>
    private double LevelPrice(ChartLayout l, InkShape shape, float y)
    {
        double price = l.Main.ValueAt(y);
        var series = _model.Series;
        if (Magnet != MagnetMode.Off && series.Count > 0)
        {
            int a = Math.Clamp(l.BarAt(shape.Bounds.Left), 0, series.Count - 1), b = Math.Clamp(l.BarAt(shape.Bounds.Right), 0, series.Count - 1);
            double? best = null;
            float bestD = 6;
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
            {
                var c = series[i];
                foreach (var t in new[] { c.HighTicks, c.LowTicks, c.OpenTicks, c.CloseTicks })
                {
                    double p = (double)series.TicksToPrice(t);
                    float d = Math.Abs(l.Main.Y(p) - y);
                    if (d >= bestD) continue;
                    bestD = d;
                    best = p;
                }
            }

            if (best is { } snapped) return snapped;
        }

        double tick = (double)_model.Instrument.TickSize;
        return tick > 0 ? Math.Round(price / tick) * tick : price;
    }

    /// <summary>Cambia el tipo de un dibujo hecho con el gesto (la barrita que aparece al lado), con el mismo trazo.</summary>
    public void RetypeInk(ChartDrawing d, InkGeometry g, DrawingKind kind)
    {
        d.Kind = kind;
        d.Points = InkDrawings.PointsFor(kind, g);
        DrawingsChanged?.Invoke();
        _model.Invalidate();
    }

    /// <summary>Deshace lo que borró un garabato.</summary>
    public void UndoErase(IReadOnlyList<(int Index, ChartDrawing Drawing)> removed)
    {
        foreach (var (i, d) in removed.OrderBy(r => r.Index))
        {
            if (_model.Drawings.Contains(d)) continue;
            _model.Drawings.Insert(Math.Clamp(i, 0, _model.Drawings.Count), d);
        }

        DrawingsChanged?.Invoke();
        _model.Invalidate();
    }

    /// <summary>Un punto sin imán ni redondeo (a mano alzada).</summary>
    private ChartPoint RawPoint(ChartLayout l, float x, float y) => new(ChartDrawing.TimeAt(l, _model.Series, x), l.Main.ValueAt(y));

    public void MouseMove(float x, float y, InputModifiers mods)
    {
        var l = L;
        _state.MouseX = x;
        _state.MouseY = y;
        if (l == null) return;
        var vp = _model.Viewport;

        if (_inkCandidate && (Math.Abs(x - _downX) > InkRecognizer.HoldSlop || Math.Abs(y - _downY) > InkRecognizer.HoldSlop))
        {
            // se movió: es arrastrar el gráfico, no el gesto
            _inkCandidate = false;
            _state.InkHold = null;
        }

        switch (_drag)
        {
            case DragMode.Ink:
                InkMove(x, y);
                break;
            case DragMode.Freehand when _active != null:
            {
                var lastPt = _active.Points[^1];
                float lx = ChartDrawing.XOf(l, _model.Series, lastPt.Time), ly = l.Main.Y(lastPt.Price);
                if ((lx - x) * (lx - x) + (ly - y) * (ly - y) >= 4) _active.Points.Add(RawPoint(l, x, y));
                break;
            }
            case DragMode.Measure when _state.Measure is { } measure:
                measure.Points[1] = PointAt(x, y, InputModifiers.None);
                break;
            case DragMode.Pan:
            {
                vp.ScrollBars = _startScroll + (x - _downX) / vp.BarWidth;
                if (!vp.AutoScale)
                {
                    double perPx = (_startTop - _startBottom) / l.Main.Rect.Height;
                    double dv = (y - _downY) * perPx;
                    vp.PriceTop = _startTop + dv;
                    vp.PriceBottom = _startBottom + dv;
                }
                else if (Math.Abs(y - _downY) > 40 && Math.Abs(y - _downY) > Math.Abs(x - _downX) * 1.5)
                {
                    // arrastre vertical fuerte: pasa a escala manual para poder mover el precio
                    vp.AutoScale = false;
                    vp.PriceTop = _startTop;
                    vp.PriceBottom = _startBottom;
                }

                break;
            }
            case DragMode.PriceScale:
            {
                double factor = Math.Exp((y - _downY) * 0.006);
                double mid = (_startTop + _startBottom) / 2;
                double half = (_startTop - _startBottom) / 2 * factor;
                vp.AutoScale = false;
                vp.PriceTop = mid + half;
                vp.PriceBottom = mid - half;
                break;
            }
            case DragMode.ZoomBox:
            {
                var plot = l.Plot;
                float cx = Math.Clamp(x, plot.Left, plot.Right), cy = Math.Clamp(y, plot.Top, plot.Bottom);
                _state.ZoomBox = new SKRect(Math.Min(_downX, cx), Math.Min(_downY, cy), Math.Max(_downX, cx), Math.Max(_downY, cy));
                break;
            }
            case DragMode.TimeScale:
            {
                double factor = Math.Exp((x - _downX) * 0.008);
                vp.BarWidth = Math.Clamp(_startBarWidth * factor, ChartViewport.MinBarWidth, ChartViewport.MaxBarWidth);
                break;
            }
            case DragMode.PanelResize when _resizePanel != null:
            {
                var plot = l.Plot;
                var p = l.Panels.FirstOrDefault(pp => pp.SubPanel == _resizePanel);
                if (p != null)
                {
                    float bottom = p.Rect.Bottom;
                    double ratio = Math.Clamp((bottom - y) / plot.Height, 0.06, 0.6);
                    _resizePanel.HeightRatio = ratio;
                }

                break;
            }
            case DragMode.CreateDrawing when _active != null:
                _active.Points[_activeHandle] = PointAt(x, y, mods);
                break;
            case DragMode.MoveOrder when _dragOrder != null:
            case DragMode.Bracket:
                _state.DragPrice = _model.Instrument.RoundPrice((decimal)l.Main.ValueAt(y));
                break;
            case DragMode.MoveDrawing when _active != null && _startPoints != null:
            {
                if (_activeHandle >= 0)
                {
                    _active.MoveHandle(_activeHandle, PointAt(x, y, mods));
                }
                else
                {
                    // mueve todo el objeto
                    double dPrice = l.Main.ValueAt(y) - l.Main.ValueAt(_downY);
                    for (int i = 0; i < _active.Points.Count; i++)
                    {
                        var sp = _startPoints[i];
                        float sx = ChartDrawing.XOf(l, _model.Series, sp.Time);
                        var t = ChartDrawing.TimeAt(l, _model.Series, sx + (x - _downX));
                        _active.Points[i] = new ChartPoint(t, sp.Price + dPrice);
                    }
                }

                break;
            }
            default:
                ForwardToIndicators(x, y, PointerButton.Left, 0, mods, MouseKind.Move);
                break;
        }

        UpdateCursor(x, y);
        _lastX = x;
        _lastY = y;
        _model.Invalidate();
    }

    public void MouseUp(float x, float y, PointerButton button, InputModifiers mods)
    {
        if (_drag is DragMode.MoveOrder or DragMode.Bracket && L is { } lu)
            _state.DragPrice = _model.Instrument.RoundPrice((decimal)lu.Main.ValueAt(y));

        _inkCandidate = false;
        _state.InkHold = null;
        if (_drag == DragMode.Ink)
        {
            CommitInk(x, y);
            return;
        }

        if (_drag == DragMode.Freehand && _active != null)
        {
            // menos puntos (los que hacen falta para el mismo trazo)
            if (L is { } lf)
            {
                var pts = _active.ScreenPoints(lf, _model.Series);
                var keep = InkRecognizer.Simplify(pts, 0.75f).ToHashSet();
                _active.Points = _active.Points.Where((_, i) => keep.Contains(pts[i])).ToList();
            }

            if (_active.Points.Count < 2) _model.Drawings.Remove(_active);
            FinishDrawing();
            _active = null;
            _model.Invalidate();
            return;
        }

        if (_drag == DragMode.Measure)
        {
            _drag = DragMode.None;
            // un Shift + clic sin arrastrar no deja nada
            if (Math.Abs(x - _downX) < 3 && Math.Abs(y - _downY) < 3) _state.Measure = null;
            _model.Invalidate();
            return;
        }

        if (_drag == DragMode.CreateDrawing && _active != null)
        {
            // si casi no se movió, se interpreta como "clic, clic": espera el segundo clic
            if (Math.Abs(x - _downX) < 3 && Math.Abs(y - _downY) < 3)
            {
                _drag = DragMode.None;
                _pendingSecondClick = true;
                return;
            }

            // trayectoria: el primer tramo se puede arrastrar y sigue con clics (doble clic termina)
            if (_active.Kind == DrawingKind.Path)
            {
                _active.Points.Add(_active.Points[^1]);
                _drag = DragMode.None;
                _pendingSecondClick = true;
                _model.Invalidate();
                return;
            }

            FinishDrawing();
        }
        else if (_drag == DragMode.MoveDrawing)
        {
            DrawingsChanged?.Invoke();
        }
        else if (_drag == DragMode.MoveOrder && _dragOrder != null)
        {
            var o = _dragOrder;
            decimal from = o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price;
            decimal to = _state.DragPrice;
            _dragOrder = null;
            _state.DragOrder = null;
            if (to > 0 && to != from) OrderDragged?.Invoke(o, to);
        }

        else if (_drag == DragMode.Bracket && _state.BracketDrag is { } bd)
        {
            decimal to = _state.DragPrice;
            _state.BracketDrag = null;
            // un clic sin arrastrar no crea órdenes
            if (to > 0 && Math.Abs(y - _downY) >= 4) BracketDropped?.Invoke(bd, to);
        }

        if (_drag == DragMode.ZoomBox && _state.ZoomBox is { } zb && L is { } lz)
        {
            _state.ZoomBox = null;
            if (zb.Width >= 8 && zb.Height >= 8) ZoomTo(lz, zb);
        }

        if (_drag == DragMode.None) ForwardToIndicators(x, y, button, 1, mods, MouseKind.Up);
        _drag = DragMode.None;
        _active = _pendingSecondClick ? _active : null;
        _model.Invalidate();
    }

    private bool _pendingSecondClick;

    /// <summary>Amplía el gráfico a un rectángulo de la pantalla: las velas que abarca ocupan todo el ancho y sus precios todo el alto.</summary>
    private void ZoomTo(ChartLayout l, SKRect r)
    {
        var vp = _model.Viewport;
        double b0 = l.BarAtExact(r.Left), b1 = l.BarAtExact(r.Right);
        double bars = Math.Max(2, b1 - b0);
        double plot = l.Plot.Width;
        vp.BarWidth = Math.Clamp(plot / bars, ChartViewport.MinBarWidth, ChartViewport.MaxBarWidth);
        // la última vela del rectángulo queda en el borde derecho
        vp.ScrollBars = Math.Max(0, l.Count - 1 - b1 + vp.RightMarginBars + 0.5);
        var main = l.Main;
        if (r.Top >= main.Rect.Top - 1 && r.Bottom <= main.Rect.Bottom + 1)
        {
            vp.AutoScale = false;
            vp.PriceTop = main.ValueAt(r.Top);
            vp.PriceBottom = main.ValueAt(r.Bottom);
        }

        _model.Invalidate();
    }

    private void StartBracketDrag(BracketHandle h, float y)
    {
        var l = L;
        if (l == null) return;
        _state.BracketDrag = h.Drag;
        _state.DragPrice = _model.Instrument.RoundPrice((decimal)l.Main.ValueAt(y));
        _drag = DragMode.Bracket;
        _model.Invalidate();
    }

    /// <summary>Segundo clic de una herramienta de dos puntos (modo clic-clic); en la trayectoria, un punto más (doble clic termina).</summary>
    public bool TryCompletePending(float x, float y, InputModifiers mods, int clicks = 1)
    {
        if (!_pendingSecondClick || _active == null) return false;
        var p = PointAt(x, y, mods);
        _active.Points[^1] = p;
        if (_active.Kind == DrawingKind.Path)
        {
            // un clic sobre el punto anterior (o el segundo clic del doble clic) termina
            bool onPrevious = false;
            if (L is { } l && _active.Points.Count >= 2)
            {
                var prev = _active.Points[^2];
                onPrevious = Math.Abs(ChartDrawing.XOf(l, _model.Series, prev.Time) - x) < 5 && Math.Abs(l.Main.Y(prev.Price) - y) < 5;
            }

            if (clicks < 2 && !onPrevious)
            {
                _active.Points.Add(p);
                _model.Invalidate();
                return true;
            }

            FinishPath();
            return true;
        }

        _pendingSecondClick = false;
        FinishDrawing();
        _active = null;
        _model.Invalidate();
        return true;
    }

    /// <summary>Termina la trayectoria que se está haciendo: saca el punto que seguía al puntero si quedó repetido.</summary>
    private void FinishPath()
    {
        if (_active == null) return;
        var pts = _active.Points;
        if (pts.Count > 2 && L is { } l)
        {
            var a = pts[^1];
            var b = pts[^2];
            if (Math.Abs(ChartDrawing.XOf(l, _model.Series, a.Time) - ChartDrawing.XOf(l, _model.Series, b.Time)) < 5 && Math.Abs(l.Main.Y(a.Price) - l.Main.Y(b.Price)) < 5)
                pts.RemoveAt(pts.Count - 1);
        }

        _pendingSecondClick = false;
        if (pts.Count < 2 || pts.All(q => q.Time == pts[0].Time && Math.Abs(q.Price - pts[0].Price) < 1e-12)) _model.Drawings.Remove(_active);
        FinishDrawing();
        _active = null;
        _model.Invalidate();
    }

    public void HoverPending(float x, float y, InputModifiers mods)
    {
        if (_pendingSecondClick && _active != null) _active.Points[^1] = PointAt(x, y, mods);
    }

    public bool HasPending => _pendingSecondClick;

    private void FinishDrawing()
    {
        // canal paralelo: el borde paralelo arranca a un 6 % del alto visible por encima (se ajusta con su manejador)
        if (_active is { Kind: DrawingKind.ParallelChannel, Points.Count: 2 } ch && L is { } lc)
        {
            double off = Math.Abs(lc.Main.Top - lc.Main.Bottom) * 0.06;
            double tick = (double)_model.Instrument.TickSize;
            if (tick > 0) off = Math.Max(tick, Math.Round(off / tick) * tick);
            ch.InitChannel(off);
        }

        _drag = DragMode.None;
        if (!StickyTool && (KeepTool == null || KeepTool != ActiveTool)) ActiveTool = null;
        ToolFinished?.Invoke();
        DrawingsChanged?.Invoke();
    }

    public void Wheel(float x, float y, float dx, float dy, InputModifiers mods)
    {
        var l = L;
        if (l == null) return;
        var vp = _model.Viewport;

        if (l.PriceAxis.Contains(x, y) || (mods & (InputModifiers.Control | InputModifiers.Command)) != 0)
        {
            // zoom vertical
            double factor = Math.Pow(1.12, -dy);
            double top = l.Main.Top, bottom = l.Main.Bottom;
            double anchor = l.Main.ValueAt(y);
            vp.AutoScale = false;
            vp.PriceTop = anchor + (top - anchor) * factor;
            vp.PriceBottom = anchor - (anchor - bottom) * factor;
            _model.Invalidate();
            return;
        }

        if (Math.Abs(dx) > Math.Abs(dy) || (mods & InputModifiers.Shift) != 0)
        {
            double d = Math.Abs(dx) > Math.Abs(dy) ? dx : dy;
            vp.ScrollBars += d * 25 / vp.BarWidth;
            _model.Invalidate();
            return;
        }

        // zoom alrededor del mouse: el tiempo y (si así está) también el precio en la misma proporción
        ZoomAround(l, x, y, Math.Pow(1.12, dy));
    }

    /// <summary>
    /// Acerca (factor &gt; 1) o aleja el gráfico alrededor de (x, y): las velas se ensanchan y, con «zoom de los dos ejes»,
    /// el rango de precio se achica en la misma proporción (como agrandar una imagen); si no, solo el tiempo.
    /// </summary>
    private void ZoomAround(ChartLayout l, float x, float y, double factor)
    {
        var vp = _model.Viewport;
        double before = l.BarAtExact(x);
        double newWidth = Math.Clamp(vp.BarWidth * factor, ChartViewport.MinBarWidth, ChartViewport.MaxBarWidth);
        if (Math.Abs(newWidth - vp.BarWidth) < 1e-9) return;
        double applied = newWidth / vp.BarWidth;
        bool followingLive = l.ScrollBars <= 0.01;
        vp.BarWidth = newWidth;
        if (!followingLive)
        {
            // mantiene fija la barra bajo el mouse: x = R - (N-1-b+M-S+0.5)*W  =>  S = N-1-b+M+0.5-(R-x)/W
            vp.ScrollBars = (l.Count - 1 - before + vp.RightMarginBars + 0.5) - (l.Plot.Right - x) / newWidth;
        }

        if (_model.Settings.ZoomBothAxes)
        {
            // el precio bajo el mouse queda quieto (fuera del panel de precio, el centro)
            double top = l.Main.Top, bottom = l.Main.Bottom;
            double anchor = y >= l.Main.Rect.Top && y <= l.Main.Rect.Bottom ? l.Main.ValueAt(y) : (top + bottom) / 2;
            vp.AutoScale = false;
            vp.PriceTop = anchor + (top - anchor) / applied;
            vp.PriceBottom = anchor - (anchor - bottom) / applied;
        }

        _model.Invalidate();
    }

    public void Leave()
    {
        _state.MouseX = null;
        _state.MouseY = null;
        var ci = _model.ChartInfo.MouseLocationInfo;
        ci.IsMouseLeave = true;
        _model.Invalidate();
    }

    public bool KeyDown(string key, InputModifiers mods)
    {
        var vp = _model.Viewport;
        switch (key)
        {
            case "Delete":
            case "Back":
            {
                var sel = _model.Drawings.Where(d => d.Selected).ToList();
                if (sel.Count == 0) return false;
                foreach (var d in sel) _model.Drawings.Remove(d);
                DrawingsChanged?.Invoke();
                _model.Invalidate();
                return true;
            }
            case "Return" or "Enter" when _pendingSecondClick && _active?.Kind == DrawingKind.Path:
                if (_active.Points.Count > 2) _active.Points.RemoveAt(_active.Points.Count - 1);
                FinishPath();
                return true;
            case "Escape":
                // corta el gesto que se estaba haciendo
                if (_drag == DragMode.Ink || _inkCandidate)
                {
                    _inkCandidate = false;
                    CancelInk();
                    return true;
                }

                // la trayectoria queda con los puntos que ya tenía
                if (_pendingSecondClick && _active is { Kind: DrawingKind.Path, Points.Count: > 2 })
                {
                    _active.Points.RemoveAt(_active.Points.Count - 1);
                    FinishPath();
                    ActiveTool = null;
                    KeepTool = null;
                    ToolFinished?.Invoke();
                    return true;
                }

                if (_state.Measure != null) _state.Measure = null;
                KeepTool = null;
                // cancela un arrastre de orden o de stop/objetivo
                if (_drag is DragMode.MoveOrder or DragMode.Bracket)
                {
                    _dragOrder = null;
                    _state.DragOrder = null;
                    _state.BracketDrag = null;
                    _drag = DragMode.None;
                    _model.Invalidate();
                    return true;
                }

                ActiveTool = null;
                if (_pendingSecondClick && _active != null) _model.Drawings.Remove(_active);
                _pendingSecondClick = false;
                _active = null;
                foreach (var d in _model.Drawings) d.Selected = false;
                ToolFinished?.Invoke();
                _model.Invalidate();
                return true;
            case "Home":
            case "End":
                vp.ScrollBars = 0;
                vp.AutoScale = true;
                _model.Invalidate();
                return true;
            case "Left":
                vp.ScrollBars += Math.Max(1, 60 / vp.BarWidth);
                _model.Invalidate();
                return true;
            case "Right":
                vp.ScrollBars -= Math.Max(1, 60 / vp.BarWidth);
                _model.Invalidate();
                return true;
            case "Add":
            case "OemPlus":
                if (L is { } lp) ZoomAround(lp, lp.Plot.MidX, lp.Main.Rect.MidY, 1.2);
                return true;
            case "Subtract":
            case "OemMinus":
                if (L is { } lm) ZoomAround(lm, lm.Plot.MidX, lm.Main.Rect.MidY, 1 / 1.2);
                return true;
        }

        return false;
    }

    // ======================================================================= ayudas

    /// <summary>Distancia (en píxeles) a la que el imán débil se pega a la vela.</summary>
    public const float WeakMagnetPixels = 14;

    private ChartPoint PointAt(float x, float y, InputModifiers mods)
    {
        var l = L!;
        var series = _model.Series;
        var time = ChartDrawing.TimeAt(l, series, x);
        double price = l.Main.ValueAt(y);
        // imán: se pega al máximo/mínimo/apertura/cierre más cercano de la vela; con Shift o el imán fuerte siempre, con el
        // débil solo si el puntero está cerca (como en TradingView)
        bool snapped = false;
        if (series.Count > 0 && ((mods & InputModifiers.Shift) != 0 || Magnet != MagnetMode.Off))
        {
            int bar = Math.Clamp(l.BarAt(x), 0, Math.Max(0, series.Count - 1));
            var c = series[bar];
            var candidates = new[] { c.HighTicks, c.LowTicks, c.OpenTicks, c.CloseTicks }.Select(t => (double)series.TicksToPrice(t));
            double best = candidates.OrderBy(p => Math.Abs(l.Main.Y(p) - y)).First();
            if ((mods & InputModifiers.Shift) != 0 || Magnet == MagnetMode.Strong || Math.Abs(l.Main.Y(best) - y) <= WeakMagnetPixels)
            {
                price = best;
                time = series[bar].Time;
                snapped = true;
            }
        }

        if (!snapped)
        {
            double tick = (double)_model.Instrument.TickSize;
            price = Math.Round(price / tick) * tick;
        }

        return new ChartPoint(time, price);
    }

    private void LegendClicked(LegendHit h)
    {
        if (h.Strategy is { } st)
        {
            switch (h.Action)
            {
                case LegendAction.ToggleVisible:
                    st.Visible = !st.Visible;
                    IndicatorsChanged?.Invoke();
                    break;
                case LegendAction.Remove:
                    StrategyRemoveRequested?.Invoke(st);
                    break;
                case LegendAction.Settings or LegendAction.Name:
                    StrategySettingsRequested?.Invoke(st);
                    break;
            }

            _model.Invalidate();
            return;
        }

        switch (h.Action)
        {
            case LegendAction.Collapse:
                _model.Settings.ShowIndicatorNames = !_model.Settings.ShowIndicatorNames;
                IndicatorsChanged?.Invoke();
                break;
            case LegendAction.ToggleVisible when h.Slot != null:
                h.Slot.Visible = !h.Slot.Visible;
                IndicatorsChanged?.Invoke();
                break;
            case LegendAction.Remove when h.Slot != null:
                _model.RemoveIndicator(h.Slot);
                IndicatorsChanged?.Invoke();
                break;
            case LegendAction.Settings or LegendAction.Name when h.Slot != null:
                IndicatorSettingsRequested?.Invoke(h.Slot);
                break;
        }

        _model.Invalidate();
    }

    private void UpdateCursor(float x, float y)
    {
        var l = L;
        if (l == null) return;
        if (_drag == DragMode.None && ((_state.GoLatestButton is { } gl && gl.Contains(x, y)) || (_state.FocusTradeClose is { } fc && fc.Contains(x, y)) ||
                                       _state.Legend.Any(h => h.Action != LegendAction.Name && h.Rect.Contains(x, y))))
        {
            Cursor = StdCursor.Hand;
            return;
        }

        var ci = _model.ChartInfo.MouseLocationInfo;
        ci.LastPosition = new System.Drawing.Point((int)x, (int)y);
        ci.IsMouseLeave = false;
        ci.BarBelowMouse = Math.Clamp(l.BarAt(x), 0, Math.Max(0, l.Count - 1));
        ci.PriceBelowMouse = (decimal)l.Main.ValueAt(y);

        if (l.PriceAxis.Contains(x, y)) Cursor = StdCursor.SizeNS;
        else if (l.TimeAxis.Contains(x, y)) Cursor = StdCursor.SizeWE;
        else if (l.Panels.Skip(1).Any(p => Math.Abs(y - p.Rect.Top) <= 3)) Cursor = StdCursor.SizeNS;
        else if (_drag == DragMode.Pan) Cursor = StdCursor.SizeAll;
        else if (_drag is DragMode.MoveOrder or DragMode.Bracket || (ActiveTool == null && (OrderAt(x, y) != null || HandleAt(x, y, false) != null))) Cursor = StdCursor.SizeNS;
        else if (!_model.Settings.HideDrawings && _model.Drawings.Any(d => d.HitTest(l, _model.Series, x, y, out _))) Cursor = StdCursor.Hand;
        else
        {
            var c = IndicatorCursor(x, y);
            Cursor = c is StdCursor.None or StdCursor.Arrow ? StdCursor.Cross : c;
        }
    }

    private enum MouseKind
    {
        Down,
        Up,
        Move,
    }

    private bool ForwardToIndicators(float x, float y, PointerButton button, int clicks, InputModifiers mods, MouseKind kind)
    {
        var e = new RenderControlMouseEventArgs((int)x, (int)y,
            button switch { PointerButton.Right => RenderControlMouseButtons.Right, PointerButton.Middle => RenderControlMouseButtons.Middle, _ => RenderControlMouseButtons.Left },
            clicks)
        {
            Modifiers = (RenderControlKeyModifiers)(int)mods,
        };
        for (int i = _model.Indicators.Count - 1; i >= 0; i--)
        {
            var ind = _model.Indicators[i].Indicator;
            try
            {
                bool handled = kind switch
                {
                    MouseKind.Down => ind.ProcessMouseDown(e) || (clicks >= 2 ? ind.ProcessMouseDoubleClick(e) : ind.ProcessMouseClick(e)),
                    MouseKind.Up => ind.ProcessMouseUp(e),
                    _ => ind.ProcessMouseMove(e),
                };
                if (handled)
                {
                    _model.Invalidate();
                    return true;
                }
            }
            catch (Exception ex)
            {
                this.LogError($"Error en el mouse de {ind.Name}", ex);
            }
        }

        return false;
    }

    private StdCursor IndicatorCursor(float x, float y)
    {
        var e = new RenderControlMouseEventArgs((int)x, (int)y, RenderControlMouseButtons.None);
        foreach (var s in _model.Indicators)
        {
            try
            {
                var c = s.Indicator.GetCursor(e);
                if (c != StdCursor.None) return c;
            }
            catch
            {
                // ignorar
            }
        }

        return StdCursor.None;
    }

    /// <summary>Reenvía una tecla a los indicadores.</summary>
    public bool ForwardKey(Avalonia.Input.KeyEventArgs e)
    {
        foreach (var s in _model.Indicators)
        {
            try
            {
                if (s.Indicator.ProcessKeyDown(e))
                {
                    _model.Invalidate();
                    return true;
                }
            }
            catch (Exception ex)
            {
                this.LogError($"Error de teclado en {s.Indicator.Name}", ex);
            }
        }

        return false;
    }
}

/// <summary>Imán de los dibujos.</summary>
public enum MagnetMode
{
    Off,
    Weak,
    Strong,
}
