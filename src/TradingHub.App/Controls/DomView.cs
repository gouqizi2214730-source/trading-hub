using Avalonia;
using Avalonia.Input;
using SkiaSharp;
using TradingHub.Charting.Dom;

namespace TradingHub.App.Controls;

/// <summary>
/// DOM (escalera de precios) para leer el libro: no opera. Rueda: desplazar; doble clic: volver al precio. El mouse sobre
/// una fila compara con el otro lado (Mirror).
/// </summary>
public sealed class DomView : SkiaView
{
    private readonly DomRenderer _renderer = new();
    private float? _mouseY;

    public DomModel? Dom { get; set; }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        if (Dom == null)
        {
            canvas.Clear(TradingHub.Charting.ChartTheme.Base.BackgroundTop);
            return;
        }

        _renderer.Render(canvas, Dom, width, height, _mouseY);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Dom == null) return;
        if (e.ClickCount >= 2) Recenter();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _mouseY = (float)e.GetPosition(this).Y;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _mouseY = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Dom == null) return;
        _renderer.Scroll(Dom, (int)Math.Round(e.Delta.Y * 3));
        e.Handled = true;
        InvalidateVisual();
    }

    public void Recenter()
    {
        if (Dom == null) return;
        Dom.AutoCenter = Dom.Settings.AutoCenter;
        Dom.CenterTicks = 0;
        InvalidateVisual();
    }

    /// <summary>Aplica una configuración nueva (columnas, tamaños, colores).</summary>
    public void ApplySettings(DomSettings settings)
    {
        if (Dom == null) return;
        Dom.Settings = settings;
        Dom.AutoCenter = settings.AutoCenter;
        InvalidateVisual();
    }
}

/// <summary>Cinta de operaciones (Time &amp; Sales).</summary>
public sealed class TapeView : SkiaView
{
    private readonly TapeRenderer _renderer = new();

    public TapeModel? Tape { get; set; }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        if (Tape == null)
        {
            canvas.Clear(TradingHub.Charting.ChartTheme.Base.BackgroundTop);
            return;
        }

        _renderer.Render(canvas, Tape, width, height);
    }

    // rueda o trackpad: hacia abajo muestra operaciones más viejas (y se queda quieta); arriba vuelve a lo último
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Tape == null) return;
        _wheel += e.Delta.Y * 3;
        int rows = (int)Math.Truncate(_wheel);
        if (rows == 0) return;
        _wheel -= rows;
        TapeRenderer.Scroll(Tape, -rows);
        e.Handled = true;
        InvalidateVisual();
    }

    private double _wheel;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Tape?.Anchor == null || e.GetPosition(this).Y > TapeRenderer.HeaderHeight) return;
        Tape.Anchor = null;
        InvalidateVisual();
    }

    /// <summary>Vuelve a lo último.</summary>
    public void Live()
    {
        if (Tape == null) return;
        Tape.Anchor = null;
        InvalidateVisual();
    }
}
