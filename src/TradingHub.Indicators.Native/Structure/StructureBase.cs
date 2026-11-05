using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

using TradingHub.Indicators.Native.VpUltimate;

namespace TradingHub.Indicators.Native.Structure;

/// <summary>
/// Base de los indicadores de estructura: arma las velas simples (las últimas <see cref="MaxBars"/>), recalcula cuando
/// aparece una vela nueva (y cada pocos segundos para la que se está formando) y dibuja cajas, líneas y etiquetas.
/// </summary>
public abstract class StructureIndicator : Indicator
{
    protected readonly List<Bar> Bars = new();
    protected int Offset; // índice de la primera vela de «Bars» en el gráfico
    private int _computedCount = -1;
    private DateTime _lastLive = DateTime.MinValue;
    private readonly Action _tick;
    private int _maxBars = 3000;

    protected StructureIndicator() : base(true)
    {
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
        DataSeries[0].IsHidden = true;
        _tick = () =>
        {
            if (CurrentBar > 0 && (DateTime.UtcNow - _lastLive).TotalSeconds >= 4) Refresh(force: true);
        };
    }

    [Display(Name = "Velas que se analizan (las últimas)", GroupName = "Cálculo", Order = 1)]
    [Range(200, 20000)]
    public int MaxBars { get => _maxBars; set { _maxBars = Math.Clamp(value, 200, 20000); RecalculateValues(); } }

    protected override void OnInitialize() => SubscribeToTimer(TimeSpan.FromSeconds(4), _tick);

    protected override void OnDispose() => UnsubscribeFromTimer(TimeSpan.FromSeconds(4), _tick);

    protected override void OnRecalculate() => _computedCount = -1;

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < CurrentBar - 1) return;
        if (CurrentBar != _computedCount) Refresh(force: false);
    }

    protected static long Sec(DateTime t) => (long)(DateTime.SpecifyKind(t, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;

    private void Refresh(bool force)
    {
        int n = CurrentBar;
        if (n == 0) return;
        if (!force && n == _computedCount) return;
        Bars.Clear();
        Offset = Math.Max(0, n - _maxBars);
        for (int i = Offset; i < n; i++)
        {
            var c = GetCandle(i);
            Bars.Add(new Bar(Sec(c.Time), (double)c.Open, (double)c.High, (double)c.Low, (double)c.Close, (double)c.Volume));
        }

        _computedCount = n;
        _lastLive = DateTime.UtcNow;
        try
        {
            Compute();
            ComputeError = null;
        }
        catch (Exception ex)
        {
            // un cálculo que falla no tira el gráfico: queda lo anterior
            ComputeError = ex.ToString();
        }

        RedrawChart();
    }

    /// <summary>El error del último cálculo (null si anduvo).</summary>
    [System.ComponentModel.Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ComputeError { get; private set; }

    /// <summary>Recalcula la estructura con <see cref="Bars"/>.</summary>
    protected abstract void Compute();

    /// <summary>Largo de la vela del gráfico (0 si no es por tiempo).</summary>
    protected TimeSpan BarLength => ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.Zero;

    // ===================================================================== dibujo

    protected static readonly RenderFont SmallFont = new("Arial", 9);
    protected static readonly RenderFont BoldFont = new("Arial", 9, FontStyle.Bold);

    protected int X(int localBar, bool start = true) => ChartInfo!.GetXByBar(localBar + Offset, start);
    protected int Y(double price) => ChartInfo!.GetYByPrice((decimal)price, false);

    protected int RightEdge => ChartInfo!.PriceChartContainer.Region.Right;

    /// <summary>Caja entre dos velas (la segunda puede ser -1: hasta el borde derecho).</summary>
    protected void Box(RenderContext ctx, int fromBar, int toBar, double top, double bottom, Color fill, Color border)
    {
        int x0 = X(fromBar);
        int x1 = toBar < 0 ? RightEdge : X(toBar, false);
        if (x1 <= x0) x1 = x0 + 2;
        int y0 = Y(top), y1 = Y(bottom);
        var rect = new Rectangle(x0, Math.Min(y0, y1), x1 - x0, Math.Max(1, Math.Abs(y1 - y0)));
        ctx.FillRectangle(fill, rect);
        ctx.DrawRectangle(new RenderPen(border, 1), rect);
    }

    protected void HLine(RenderContext ctx, int fromBar, int toBar, double price, Color color, int width = 1, LineDashStyle style = LineDashStyle.Solid)
    {
        int x0 = X(fromBar);
        int x1 = toBar < 0 ? RightEdge : X(toBar, false);
        int y = Y(price);
        ctx.DrawLine(Vp.Pen(color, width, style), x0, y, x1, y);
    }

    protected void Label(RenderContext ctx, string text, int x, int y, Color color, bool bold = false, bool above = true)
    {
        var font = bold ? BoldFont : SmallFont;
        var size = ctx.MeasureString(text, font);
        ctx.DrawString(text, font, color, x - size.Width / 2, above ? y - size.Height - 2 : y + 2);
    }

    protected void LabelLeft(RenderContext ctx, string text, int x, int y, Color color, bool bold = false)
    {
        var font = bold ? BoldFont : SmallFont;
        var size = ctx.MeasureString(text, font);
        ctx.DrawString(text, font, color, x, y - size.Height / 2);
    }

    /// <summary>
    /// Un nivel para las alertas (de la vela local <paramref name="fromBar"/> a <paramref name="toBar"/>; −1 = sigue a la
    /// derecha). El Id lleva la hora de la vela de inicio (no el precio, que puede moverse: un EQH es un promedio), así es el
    /// mismo al recalcular; <paramref name="kind"/> distingue lo que empieza en la misma vela.
    /// </summary>
    protected IndicatorLevel Level(string kind, string name, int fromBar, int toBar, double price, string? part = null)
    {
        int from = Math.Clamp(fromBar + Offset, 0, Math.Max(0, CurrentBar - 1));
        long key = CurrentBar > 0 ? GetCandle(from).Time.Ticks : from;
        string when = CurrentBar > 0 ? GetCandle(from).Time.ToString("dd/MM HH:mm") : "";
        return new IndicatorLevel($"{kind}@{key}", ($"{UiText.Tr(name)} {when}".Trim() + (part == null ? "" : " · " + UiText.Tr(part))),
            (decimal)price, from, toBar < 0 ? int.MaxValue : toBar + Offset);
    }

    /// <summary>Los dos bordes de una caja como niveles.</summary>
    protected IEnumerable<IndicatorLevel> BoxLevels(string kind, string name, int fromBar, int toBar, double top, double bottom)
    {
        yield return Level(kind + ".top", name, fromBar, toBar, Math.Max(top, bottom), "borde de arriba");
        yield return Level(kind + ".bottom", name, fromBar, toBar, Math.Min(top, bottom), "borde de abajo");
    }

    protected bool Visible(int localBar) => localBar + Offset >= FirstVisibleBarNumber - 1 && localBar + Offset <= LastVisibleBarNumber + 1;
}
