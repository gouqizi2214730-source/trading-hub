using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;
using TradingHub.Indicators.Native.VpUltimate;

namespace TradingHub.Indicators.Native.Structure;

public abstract class StructureIndicator : Indicator
{
    protected readonly List<Bar> Bars = new();
    protected int Offset;
    private int _computedCount = -1;
    private int _prevOffset = -1;
    private (long T, double O, double H, double L, double C, double V) _lastForming;
    private DateTime _lastLive = DateTime.MinValue;
    private readonly Action _tick;
    private int _maxBars = 3000;

    [System.ComponentModel.Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public long IncrementalRefreshes { get; private set; }

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

    protected override void OnDispose() => UnsubscribeToTimer(TimeSpan.FromSeconds(4), _tick);

    protected override void OnRecalculate()
    {
        _computedCount = -1;
        _prevOffset = -1;
        _lastForming = default;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < CurrentBar - 1) return;
        if (CurrentBar != _computedCount) Refresh(force: false);
    }

    protected static long Sec(DateTime t) => (long)(DateTime.SpecifyKind(t, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;

    private Bar ReadBar(int i)
    {
        var c = GetCandle(i);
        return new Bar(Sec(c.Time), (double)c.Open, (double)c.High, (double)c.Low, (double)c.Close, (double)c.Volume);
    }

    private void Refresh(bool force)
    {
        int n = CurrentBar;
        if (n == 0) return;
        if (!force && n == _computedCount) return;

        var lastCandle = ReadBar(n - 1);
        var formingKey = (lastCandle.T, lastCandle.O, lastCandle.H, lastCandle.L, lastCandle.C, lastCandle.V);
        if (force && n == _computedCount && formingKey == _lastForming)
        {
            _lastLive = DateTime.UtcNow;
            return;
        }

        int newOffset = Math.Max(0, n - _maxBars);
        if (_computedCount > 0 && newOffset == _prevOffset && n >= _computedCount && Bars.Count == _computedCount - _prevOffset)
        {
            int startIdx = Math.Max(newOffset, _computedCount - 1);
            for (int i = startIdx; i < n; i++)
            {
                var b = i == n - 1 ? lastCandle : ReadBar(i);
                int local = i - newOffset;
                if (local < Bars.Count) Bars[local] = b;
                else Bars.Add(b);
            }
            IncrementalRefreshes++;
        }
        else
        {
            Bars.Clear();
            for (int i = newOffset; i < n; i++)
                Bars.Add(i == n - 1 ? lastCandle : ReadBar(i));
        }

        Offset = _prevOffset = newOffset;
        _computedCount = n;
        _lastForming = formingKey;
        _lastLive = DateTime.UtcNow;
        try
        {
            Compute();
            ComputeError = null;
        }
        catch (Exception ex)
        {
            ComputeError = ex.ToString();
        }

        RedrawChart();
    }

    [System.ComponentModel.Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ComputeError { get; private set; }

    protected abstract void Compute();

    protected TimeSpan BarLength => ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.Zero;

    protected static readonly RenderFont SmallFont = new("Arial", 9);
    protected static readonly RenderFont BoldFont = new("Arial", 9, FontStyle.Bold);

    protected int X(int localBar, bool start = true) => ChartInfo!.GetXByBar(localBar + Offset, start);
    protected int Y(double price) => ChartInfo!.GetYByPrice((decimal)price, false);

    protected int RightEdge => ChartInfo!.PriceChartContainer.Region.Right;

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

    protected IndicatorLevel Level(string kind, string name, int fromBar, int toBar, double price, string? part = null)
    {
        int from = Math.Clamp(fromBar + Offset, 0, Math.Max(0, CurrentBar - 1));
        long key = CurrentBar > 0 ? GetCandle(from).Time.Ticks : from;
        string when = CurrentBar > 0 ? GetCandle(from).Time.ToString("dd/MM HH:mm") : "";
        return new IndicatorLevel($"{kind}@{key}", ($"{UiText.Tr(name)} {when}".Trim() + (part == null ? "" : " · " + UiText.Tr(part))),
            (decimal)price, from, toBar < 0 ? int.MaxValue : toBar + Offset);
    }

    protected IEnumerable<IndicatorLevel> BoxLevels(string kind, string name, int fromBar, int toBar, double top, double bottom)
    {
        yield return Level(kind + ".top", name, fromBar, toBar, Math.Max(top, bottom), "borde de arriba");
        yield return Level(kind + ".bottom", name, fromBar, toBar, Math.Min(top, bottom), "borde de abajo");
    }

    protected bool Visible(int localBar) => localBar + Offset >= FirstVisibleBarNumber - 1 && localBar + Offset <= LastVisibleBarNumber + 1;
}
