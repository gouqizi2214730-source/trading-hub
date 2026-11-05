using System.Globalization;
using SkiaSharp;
using TradingHub.Data;
using TradingHub.Rendering.Context;
using TradingHub.Rendering;

namespace TradingHub.Charting.Drawings;

public enum DrawingKind
{
    HorizontalLine,
    HorizontalRay,
    VerticalLine,
    TrendLine,
    Ray,
    ExtendedLine,
    Rectangle,
    FibRetracement,
    Text,
    Ruler,
    LongPosition,
    ShortPosition,
    AnchoredVwap,
    FixedRangeProfile,
    ParallelChannel,
}

public enum LineStyleKind
{
    Solid,
    Dashed,
    Dotted,
}

/// <summary>Punto anclado en el tiempo y el precio (sobrevive a cambios de período).</summary>
public struct ChartPoint
{
    public DateTime Time { get; set; }
    public double Price { get; set; }

    public ChartPoint(DateTime time, double price)
    {
        Time = time;
        Price = price;
    }
}

/// <summary>Nivel de un retroceso de Fibonacci (valor, si se ve y su color; 0 = el del dibujo).</summary>
public sealed class FibLevel
{
    public double Value { get; set; }
    public bool Visible { get; set; } = true;
    public uint ColorArgb { get; set; }

    public FibLevel Clone() => new() { Value = Value, Visible = Visible, ColorArgb = ColorArgb };
}

/// <summary>Objeto dibujado por el usuario sobre el gráfico.</summary>
public sealed class ChartDrawing
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DrawingKind Kind { get; set; }
    public List<ChartPoint> Points { get; set; } = new();
    public uint ColorArgb { get; set; } = 0xFF1E90FF;
    public float Width { get; set; } = 1.5f;

    /// <summary>Formato viejo (antes de <see cref="LineStyle"/>): se sigue leyendo.</summary>
    public bool Dashed { get; set; }

    public LineStyleKind LineStyle { get; set; }

    /// <summary>Texto del dibujo (en líneas y zonas se muestra como etiqueta).</summary>
    public string Text { get; set; } = "";

    public bool Locked { get; set; }

    /// <summary>Relleno de zonas (0 = el color de la línea, transparente).</summary>
    public uint FillArgb { get; set; }

    /// <summary>Extender hacia la izquierda / derecha (líneas de tendencia y zonas).</summary>
    public bool ExtendLeft { get; set; }
    public bool ExtendRight { get; set; }

    /// <summary>Mostrar el precio en las líneas y en los niveles.</summary>
    public bool ShowPriceLabel { get; set; } = true;

    public float FontSize { get; set; } = 13;
    public bool Bold { get; set; }

    /// <summary>Color del texto (0 = el de la línea).</summary>
    public uint TextArgb { get; set; }

    /// <summary>Oculto (sigue guardado; se vuelve a mostrar desde su panel o la lista de dibujos).</summary>
    public bool Hidden { get; set; }

    /// <summary>Si se ve solo en un período (código, p. ej. "M5"); null = en todos.</summary>
    public string? OnlyPeriod { get; set; }

    /// <summary>Alerta de precio en líneas horizontales (avisa cuando el precio la cruza).</summary>
    public bool Alert { get; set; }

    /// <summary>Niveles de Fibonacci propios (null = los de siempre).</summary>
    public List<FibLevel>? Levels { get; set; }

    /// <summary>Posición larga/corta: cantidad para calcular ganancia y riesgo (0 = sin montos).</summary>
    public decimal Quantity { get; set; }

    /// <summary>
    /// Posición larga/corta, como en TradingView: tamaño de la cuenta y riesgo en % de ella. Con los dos, la cantidad sale
    /// sola (riesgo ÷ distancia al stop) y tiene prioridad sobre <see cref="Quantity"/>.
    /// </summary>
    public decimal AccountSize { get; set; }

    public decimal RiskPercent { get; set; } = 1;

    /// <summary>VWAP anclado: bandas de desvío (× σ, 0 = sin banda).</summary>
    public double Band1 { get; set; } = 1;
    public double Band2 { get; set; }

    /// <summary>Perfil de rango fijo: filas (0 = una por tick), área de valor, ancho (% del rango) y ajuste a sesiones enteras.</summary>
    public int ProfileRows { get; set; } = 120;
    public int ValueAreaPercent { get; set; } = 70;
    public int ProfileWidthPercent { get; set; } = 60;
    public bool SnapToSessions { get; set; }
    public bool ShowValueArea { get; set; } = true;
    public bool ShowDeltaProfile { get; set; }

    /// <summary>Cálculo sobre las operaciones (VWAP anclado y perfil): se rehace solo cuando hace falta.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TradeDrawingCalc Calc { get; } = new();

    /// <summary>Cantidad que usa la posición: la que sale del riesgo (si hay cuenta) o la escrita.</summary>
    public decimal EffectiveQuantity(decimal volumeStep = 0)
    {
        if (!IsPosition || Points.Count < 3 || AccountSize <= 0 || RiskPercent <= 0) return Quantity;
        decimal dist = (decimal)Math.Abs(Points[0].Price - Points[2].Price);
        if (dist <= 0) return Quantity;
        decimal q = AccountSize * RiskPercent / 100m / dist;
        return volumeStep > 0 ? Math.Floor(q / volumeStep) * volumeStep : Math.Round(q, 6);
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Selected { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public SKColor Color
    {
        get => new(ColorArgb);
        set => ColorArgb = (uint)value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public LineStyleKind EffectiveLineStyle => LineStyle != LineStyleKind.Solid ? LineStyle : Dashed ? LineStyleKind.Dashed : LineStyleKind.Solid;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPosition => Kind is DrawingKind.LongPosition or DrawingKind.ShortPosition;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAlert => Alert || Text == "alerta";

    public int RequiredPoints => Kind switch
    {
        DrawingKind.HorizontalLine or DrawingKind.HorizontalRay or DrawingKind.VerticalLine or DrawingKind.Text => 1,
        DrawingKind.LongPosition or DrawingKind.ShortPosition or DrawingKind.AnchoredVwap => 1,
        DrawingKind.ParallelChannel => 3,
        _ => 2,
    };

    public static readonly double[] FibLevels = { 0, 0.236, 0.382, 0.5, 0.618, 0.786, 1, 1.272, 1.618 };

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<FibLevel> EffectiveLevels => Levels ?? FibLevels.Select(v => new FibLevel { Value = v }).ToList();

    public static string KindName(DrawingKind k) => k switch
    {
        DrawingKind.HorizontalLine => "Línea horizontal",
        DrawingKind.HorizontalRay => "Rayo horizontal",
        DrawingKind.VerticalLine => "Línea vertical",
        DrawingKind.TrendLine => "Línea de tendencia",
        DrawingKind.Ray => "Rayo",
        DrawingKind.ExtendedLine => "Línea extendida",
        DrawingKind.Rectangle => "Rectángulo / zona",
        DrawingKind.FibRetracement => "Retroceso de Fibonacci",
        DrawingKind.Text => "Texto",
        DrawingKind.Ruler => "Regla",
        DrawingKind.LongPosition => "Posición larga",
        DrawingKind.ShortPosition => "Posición corta",
        DrawingKind.AnchoredVwap => "VWAP anclado",
        DrawingKind.FixedRangeProfile => "Perfil de rango fijo",
        DrawingKind.ParallelChannel => "Canal paralelo",
        _ => k.ToString(),
    };

    /// <summary>Si se dibuja en un gráfico de ese período.</summary>
    public bool IsVisibleOn(ChartPeriod period) => !Hidden && (OnlyPeriod == null || OnlyPeriod == period.Code);

    /// <summary>Copia independiente (otro Id).</summary>
    public ChartDrawing Clone()
    {
        var c = (ChartDrawing)MemberwiseClone();
        c.Id = Guid.NewGuid();
        c.Points = Points.ToList();
        c.Levels = Levels?.Select(l => l.Clone()).ToList();
        c.Selected = false;
        return c;
    }

    /// <summary>Toma el estilo (no la posición) de otro dibujo: lo usa "predeterminado" y "copiar estilo".</summary>
    public void CopyStyleFrom(ChartDrawing src)
    {
        ColorArgb = src.ColorArgb;
        Width = src.Width;
        LineStyle = src.EffectiveLineStyle;
        Dashed = false;
        FillArgb = src.FillArgb;
        ExtendLeft = src.ExtendLeft;
        ExtendRight = src.ExtendRight;
        ShowPriceLabel = src.ShowPriceLabel;
        FontSize = src.FontSize;
        Bold = src.Bold;
        TextArgb = src.TextArgb;
        Levels = src.Levels?.Select(l => l.Clone()).ToList();
        Quantity = src.Quantity;
        AccountSize = src.AccountSize;
        RiskPercent = src.RiskPercent;
        Band1 = src.Band1;
        Band2 = src.Band2;
        ProfileRows = src.ProfileRows;
        ValueAreaPercent = src.ValueAreaPercent;
        ProfileWidthPercent = src.ProfileWidthPercent;
        SnapToSessions = src.SnapToSessions;
        ShowValueArea = src.ShowValueArea;
        ShowDeltaProfile = src.ShowDeltaProfile;
    }

    // ------------------------------------------------------------------ posición larga/corta

    /// <summary>Arma una posición con stop y objetivo por defecto (1:2) desde la entrada (Points[0]).</summary>
    public void InitPosition(double risk, DateTime end)
    {
        var e = Points[0];
        bool isLong = Kind == DrawingKind.LongPosition;
        double target = isLong ? e.Price + risk * 2 : e.Price - risk * 2;
        double stop = isLong ? e.Price - risk : e.Price + risk;
        Points = new List<ChartPoint> { e, new(end, target), new(end, stop) };
    }

    /// <summary>
    /// Arrastre de un manejador. En la posición: 0 = entrada, 1 = objetivo, 2 = stop (solo precio) y 3 = largo
    /// (hasta cuándo). En el resto, el punto correspondiente.
    /// </summary>
    public void MoveHandle(int handle, ChartPoint p)
    {
        if (IsPosition && Points.Count >= 3)
        {
            switch (handle)
            {
                case 0: Points[0] = p; break;
                case 1: Points[1] = new ChartPoint(Points[1].Time, p.Price); break;
                case 2: Points[2] = new ChartPoint(Points[2].Time, p.Price); break;
                case 3:
                    Points[1] = new ChartPoint(p.Time, Points[1].Price);
                    Points[2] = new ChartPoint(p.Time, Points[2].Price);
                    break;
            }

            return;
        }

        if (Kind == DrawingKind.FixedRangeProfile && handle >= 0 && handle < Points.Count)
        {
            // solo se mueve en el tiempo (el precio del punto no cuenta)
            Points[handle] = new ChartPoint(p.Time, Points[handle].Price);
            return;
        }

        if (Kind == DrawingKind.ParallelChannel && Points.Count >= 3 && handle is >= 0 and <= 2)
        {
            // el tercer punto es el ancho del canal (en precio, igual en toda la línea); va pegado al primero
            double off = ChannelOffset;
            switch (handle)
            {
                case 0:
                    Points[0] = p;
                    Points[2] = new ChartPoint(p.Time, p.Price + off);
                    break;
                case 1:
                    Points[1] = p;
                    break;
                default:
                {
                    var a = Points[0];
                    var b = Points[1];
                    double span = (b.Time - a.Time).Ticks;
                    double basis = Math.Abs(span) < 1 ? a.Price : a.Price + (p.Time - a.Time).Ticks / span * (b.Price - a.Price);
                    Points[2] = new ChartPoint(a.Time, a.Price + (p.Price - basis));
                    break;
                }
            }

            return;
        }

        if (handle >= 0 && handle < Points.Count) Points[handle] = p;
    }

    /// <summary>Canal paralelo: distancia en precio del borde paralelo a la línea base (el tercer punto, pegado al primero).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double ChannelOffset => Points.Count >= 3 ? Points[2].Price - Points[0].Price : 0;

    /// <summary>Canal paralelo recién trazado: el borde paralelo arranca a <paramref name="offset"/> de la línea base.</summary>
    public void InitChannel(double offset)
    {
        if (Points.Count < 2) return;
        while (Points.Count > 2) Points.RemoveAt(Points.Count - 1);
        Points.Add(new ChartPoint(Points[0].Time, Points[0].Price + offset));
    }

    /// <summary>Resultado de la posición sobre las velas: si tocó el objetivo o el stop primero.</summary>
    public (string Status, int Outcome) PositionOutcome(CandleSeries series)
    {
        var r = PositionResult(series);
        return (r.Status, r.Outcome);
    }

    /// <summary>
    /// Recorrido de la posición, como en TradingView: dónde terminó (vela y precio; si no tocó nada, la última vela o la del
    /// final de la caja y su cierre) y si fue objetivo (1), stop (-1) o sigue abierta (0).
    /// </summary>
    public (string Status, int Outcome, int ExitBar, double ExitPrice) PositionResult(CandleSeries series)
    {
        if (!IsPosition || Points.Count < 3 || series.Count == 0) return ("", 0, -1, 0);
        bool isLong = Kind == DrawingKind.LongPosition;
        double target = Points[1].Price, stop = Points[2].Price;
        var end = Points[1].Time > Points[0].Time ? Points[1].Time : DateTime.MaxValue;
        // desde la vela siguiente a la entrada (la vela de la entrada ya tenía su rango antes de entrar)
        int entryBar = series.FindBar(Points[0].Time);
        if (entryBar < 0 || series[entryBar].Time > Points[0].Time) return ("", 0, -1, 0); // la entrada está en el futuro
        int first = Math.Max(1, entryBar + 1);
        int lastBar = entryBar;
        for (int i = first; i < series.Count && series[i].Time <= end; i++)
        {
            var c = series[i];
            lastBar = i;
            double hi = (double)series.TicksToPrice(c.HighTicks), lo = (double)series.TicksToPrice(c.LowTicks);
            bool hitStop = isLong ? lo <= stop : hi >= stop;
            bool hitTarget = isLong ? hi >= target : lo <= target;
            if (hitStop) return ("Stop tocado", -1, i, stop); // si toca los dos en la misma vela, se toma el peor caso
            if (hitTarget) return ("Objetivo tocado", 1, i, target);
        }

        return ("", 0, lastBar, (double)series.TicksToPrice(series[lastBar].CloseTicks));
    }

    // ------------------------------------------------------------------ coordenadas

    /// <summary>X de un instante (interpola entre velas y extrapola al futuro).</summary>
    public static float XOf(ChartLayout layout, CandleSeries series, DateTime time) =>
        series.Count == 0 ? layout.Plot.Right : layout.CenterX(BarPos(series, time));

    /// <summary>Posición (en velas, con fracción) de un instante: más allá de la última vela sigue al ritmo del período.</summary>
    public static double BarPos(CandleSeries series, DateTime time)
    {
        if (series.Count == 0) return 0;
        var lastTime = series[^1].Time;
        if (time > lastTime)
        {
            var dur = series.Period.ApproxDuration;
            double bars = dur > TimeSpan.Zero ? (time - lastTime).Ticks / (double)dur.Ticks : 1;
            return series.Count - 1 + bars;
        }

        int bar = series.FindBar(time);
        if (bar < 0) return 0;
        double frac = 0;
        if (bar + 1 < series.Count)
        {
            var t0 = series[bar].Time;
            var t1 = series[bar + 1].Time;
            if (t1 > t0) frac = Math.Clamp((time - t0).Ticks / (double)(t1 - t0).Ticks, 0, 0.999);
        }

        return bar + frac;
    }

    // ------------------------------------------------------------------ líneas para las alertas

    /// <summary>Las líneas de este dibujo a las que se les puede poner una alerta: (clave, nombre).</summary>
    public IReadOnlyList<(string Part, string Name)> AlertParts() => Kind switch
    {
        DrawingKind.HorizontalLine or DrawingKind.HorizontalRay or DrawingKind.TrendLine or DrawingKind.Ray or DrawingKind.ExtendedLine => new[] { ("line", "línea") },
        DrawingKind.Rectangle => new[] { ("top", "borde de arriba"), ("bottom", "borde de abajo") },
        DrawingKind.ParallelChannel => new[] { ("upper", "borde de arriba"), ("middle", "línea del medio"), ("lower", "borde de abajo") },
        DrawingKind.AnchoredVwap => new[] { ("vwap", "VWAP") }
            .Concat(Band1 > 0 ? new[] { ("u1", "+" + Band1.ToString("0.##", CultureInfo.InvariantCulture) + "σ"), ("d1", "−" + Band1.ToString("0.##", CultureInfo.InvariantCulture) + "σ") } : Array.Empty<(string, string)>())
            .Concat(Band2 > 0 ? new[] { ("u2", "+" + Band2.ToString("0.##", CultureInfo.InvariantCulture) + "σ"), ("d2", "−" + Band2.ToString("0.##", CultureInfo.InvariantCulture) + "σ") } : Array.Empty<(string, string)>())
            .ToArray(),
        DrawingKind.FibRetracement => EffectiveLevels.Where(l => l.Visible).Select(l => ("fib:" + l.Value.ToString(CultureInfo.InvariantCulture), "nivel " + l.Value.ToString("0.###", CultureInfo.InvariantCulture))).ToArray(),
        _ => Array.Empty<(string, string)>(),
    };

    /// <summary>Si el dibujo forma un canal (dos bordes): rectángulo y canal paralelo.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsChannel => Kind is DrawingKind.Rectangle or DrawingKind.ParallelChannel;

    /// <summary>
    /// Precio de una línea del dibujo en la vela <paramref name="bar"/> (en velas, con fracción); null si en esa vela la
    /// línea no existe (antes del comienzo de un rayo, fuera de un segmento sin extender).
    /// </summary>
    public double? ValueAt(CandleSeries series, double bar, string part)
    {
        if (Points.Count == 0) return null;
        double b0 = BarPos(series, Points[0].Time), p0 = Points[0].Price;
        double b1 = Points.Count > 1 ? BarPos(series, Points[1].Time) : b0, p1 = Points.Count > 1 ? Points[1].Price : p0;
        const double eps = 1e-9;
        double Line(double ba, double pa, double bb, double pb, double at) => Math.Abs(bb - ba) < eps ? pa : pa + (at - ba) * (pb - pa) / (bb - ba);
        bool Within(bool left, bool right)
        {
            double lo = Math.Min(b0, b1), hi = Math.Max(b0, b1);
            return (left || bar >= lo - eps) && (right || bar <= hi + eps);
        }

        switch (Kind)
        {
            case DrawingKind.HorizontalLine:
                return p0;
            case DrawingKind.HorizontalRay:
                return ExtendLeft || bar >= b0 - eps ? p0 : null;
            case DrawingKind.TrendLine:
                return Within(ExtendLeft, ExtendRight) ? Line(b0, p0, b1, p1, bar) : null;
            case DrawingKind.Ray:
            {
                // desde el primer punto hacia el segundo y más allá
                bool forward = b1 >= b0;
                return (forward ? bar >= b0 - eps : bar <= b0 + eps) ? Line(b0, p0, b1, p1, bar) : null;
            }
            case DrawingKind.ExtendedLine:
                return Line(b0, p0, b1, p1, bar);
            case DrawingKind.Rectangle:
                if (!Within(ExtendLeft, ExtendRight)) return null;
                return part == "bottom" ? Math.Min(p0, p1) : Math.Max(p0, p1);
            case DrawingKind.ParallelChannel:
            {
                if (Points.Count < 3 || !Within(ExtendLeft, ExtendRight)) return null;
                double basis = Line(b0, p0, b1, p1, bar);
                double a = basis, b = basis + ChannelOffset;
                return part switch
                {
                    "middle" => (a + b) / 2,
                    "lower" => Math.Min(a, b),
                    _ => Math.Max(a, b),
                };
            }
            case DrawingKind.FibRetracement:
            {
                if (!part.StartsWith("fib:", StringComparison.Ordinal) || !double.TryParse(part[4..], NumberStyles.Float, CultureInfo.InvariantCulture, out var lvl)) return null;
                if (!ExtendLeft && bar < Math.Min(b0, b1) - eps) return null;
                return p1 + (p0 - p1) * (1 - lvl);
            }
            default:
                return null;
        }
    }

    /// <summary>Instante en una coordenada X.</summary>
    public static DateTime TimeAt(ChartLayout layout, CandleSeries series, float x)
    {
        if (series.Count == 0) return DateTime.UtcNow;
        double b = layout.BarAtExact(x);
        int bar = (int)Math.Floor(b);
        double frac = b - bar;
        if (bar >= series.Count - 1)
        {
            var dur = series.Period.ApproxDuration;
            var last = series[^1].Time;
            double extra = b - (series.Count - 1);
            return dur > TimeSpan.Zero ? last + TimeSpan.FromTicks((long)(dur.Ticks * extra)) : last;
        }

        if (bar < 0) return series[0].Time;
        var t0 = series[bar].Time;
        var t1 = series[bar + 1].Time;
        return t0 + TimeSpan.FromTicks((long)((t1 - t0).Ticks * frac));
    }

    // ------------------------------------------------------------------ dibujo

    private SKPathEffect? Effect(float scale = 1) => EffectiveLineStyle switch
    {
        LineStyleKind.Dashed => SKPathEffect.CreateDash(new[] { 6f * scale, 4f * scale }, 0),
        LineStyleKind.Dotted => SKPathEffect.CreateDash(new[] { 1.5f, 3f }, 0),
        _ => null,
    };

    public void Render(SKCanvas canvas, ChartLayout layout, CandleSeries series, ChartTheme theme, int decimals) =>
        Render(canvas, layout, series, theme, decimals, null);

    public void Render(SKCanvas canvas, ChartLayout layout, CandleSeries series, ChartTheme theme, int decimals, TradeStore? trades)
    {
        if (Points.Count == 0 || !IsVisibleOn(series.Period)) return;
        var main = layout.Main;
        var plot = main.Rect;
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Selected ? Width + 1 : Width,
            Color = Color,
            PathEffect = Effect(),
        };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = FillArgb != 0 ? new SKColor(FillArgb) : Color.WithAlpha(40) };
        using var text = new SKPaint { IsAntialias = true, Color = TextArgb != 0 ? new SKColor(TextArgb) : Color };
        var font = RenderContext.GetFont(theme.FontFamily, 11, false, false);
        var labelFont = RenderContext.GetFont(theme.FontFamily, Math.Clamp(FontSize, 8, 40), Bold, false);

        var p0 = Points[0];
        float x0 = XOf(layout, series, p0.Time), y0 = main.Y(p0.Price);
        float x1 = x0, y1 = y0;
        if (Points.Count > 1)
        {
            x1 = XOf(layout, series, Points[1].Time);
            y1 = main.Y(Points[1].Price);
        }

        string label = Text == "alerta" ? "" : Text;
        switch (Kind)
        {
            case DrawingKind.HorizontalLine:
                canvas.DrawLine(plot.Left, y0, plot.Right, y0, stroke);
                if (ShowPriceLabel)
                {
                    string pl = Format(p0.Price, decimals);
                    canvas.DrawUiText(pl, plot.Right - 4 - RenderContext.MeasureWidth(font, pl), y0 - 3, font, text);
                }

                if (label.Length > 0) canvas.DrawUiText(label, plot.Left + 6, y0 - 4, labelFont, text);
                if (IsAlert) DrawBell(canvas, plot.Right - (ShowPriceLabel ? 70 : 14), y0 - 8, Color);
                break;
            case DrawingKind.HorizontalRay:
                canvas.DrawLine(ExtendLeft ? plot.Left : x0, y0, plot.Right, y0, stroke);
                if (ShowPriceLabel)
                {
                    string pl = Format(p0.Price, decimals);
                    canvas.DrawUiText(pl, plot.Right - 4 - RenderContext.MeasureWidth(font, pl), y0 - 3, font, text);
                }

                if (label.Length > 0) canvas.DrawUiText(label, x0 + 6, y0 - 4, labelFont, text);
                if (IsAlert) DrawBell(canvas, x0 + 8, y0 - 8, Color);
                break;
            case DrawingKind.VerticalLine:
                canvas.DrawLine(x0, plot.Top, x0, layout.Panels[^1].Rect.Bottom, stroke);
                if (label.Length > 0) canvas.DrawUiText(label, x0 + 5, plot.Top + labelFont.Size + 4, labelFont, text);
                break;
            case DrawingKind.TrendLine:
            case DrawingKind.Ray:
            case DrawingKind.ExtendedLine:
            {
                bool left = Kind == DrawingKind.ExtendedLine || (Kind == DrawingKind.TrendLine && ExtendLeft);
                bool right = Kind is DrawingKind.Ray or DrawingKind.ExtendedLine || (Kind == DrawingKind.TrendLine && ExtendRight);
                var (ax, ay, bx, by) = Extend(x0, y0, x1, y1, plot, left, right);
                canvas.DrawLine(ax, ay, bx, by, stroke);
                if (label.Length > 0)
                {
                    float w = RenderContext.MeasureWidth(labelFont, label);
                    canvas.DrawUiText(label, (x0 + x1) / 2 - w / 2, (y0 + y1) / 2 - 6, labelFont, text);
                }

                break;
            }
            case DrawingKind.ParallelChannel:
            {
                double off = ChannelOffset;
                float ux0 = x0, uy0 = main.Y(p0.Price + off), ux1 = x1, uy1 = main.Y((Points.Count > 1 ? Points[1].Price : p0.Price) + off);
                var (ax, ay, bx, by) = Extend(x0, y0, x1, y1, plot, ExtendLeft, ExtendRight);
                var (cx, cy, dx, dy) = Extend(ux0, uy0, ux1, uy1, plot, ExtendLeft, ExtendRight);
                using (var path = new SKPath())
                {
                    path.MoveTo(ax, ay);
                    path.LineTo(bx, by);
                    path.LineTo(dx, dy);
                    path.LineTo(cx, cy);
                    path.Close();
                    canvas.DrawPath(path, fill);
                }

                canvas.DrawLine(ax, ay, bx, by, stroke);
                canvas.DrawLine(cx, cy, dx, dy, stroke);
                using (var mid = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Color.WithAlpha(150), PathEffect = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0) })
                    canvas.DrawLine((ax + cx) / 2, (ay + cy) / 2, (bx + dx) / 2, (by + dy) / 2, mid);
                if (label.Length > 0) canvas.DrawUiText(label, Math.Min(x0, x1) + 5, Math.Min(Math.Min(y0, y1), Math.Min(uy0, uy1)) - 5, labelFont, text);
                if (IsAlert) DrawBell(canvas, bx - 14, Math.Min(by, dy) - 10, Color);
                break;
            }
            case DrawingKind.Rectangle:
            {
                var r = new SKRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
                if (ExtendRight) r.Right = plot.Right;
                if (ExtendLeft) r.Left = plot.Left;
                canvas.DrawRect(r, fill);
                canvas.DrawRect(r, stroke);
                if (label.Length > 0) canvas.DrawUiText(label, r.Left + 5, r.Top + labelFont.Size + 3, labelFont, text);
                break;
            }
            case DrawingKind.FibRetracement:
            {
                double pHigh = Points[0].Price, pLow = Points.Count > 1 ? Points[1].Price : pHigh;
                float left = ExtendLeft ? plot.Left : Math.Min(x0, x1);
                float right = ExtendRight || Points.Count < 2 ? plot.Right : Math.Max(Math.Max(x0, x1), plot.Right);
                foreach (var lvl in EffectiveLevels)
                {
                    if (!lvl.Visible) continue;
                    double price = pLow + (pHigh - pLow) * (1 - lvl.Value);
                    float y = main.Y(price);
                    stroke.Color = lvl.ColorArgb != 0 ? new SKColor(lvl.ColorArgb) : Color;
                    text.Color = stroke.Color;
                    stroke.PathEffect = lvl.Value is 0 or 1 ? Effect() : SKPathEffect.CreateDash(new[] { 4f, 3f }, 0);
                    canvas.DrawLine(left, y, right, y, stroke);
                    string l = lvl.Value.ToString("0.###", CultureInfo.InvariantCulture) + (ShowPriceLabel ? $"  ({Format(price, decimals)})" : "");
                    canvas.DrawUiText(l, left + 3, y - 3, font, text);
                }

                break;
            }
            case DrawingKind.Text:
                canvas.DrawUiText(string.IsNullOrEmpty(Text) ? "Texto" : Text, x0, y0, labelFont, text);
                break;
            case DrawingKind.Ruler:
            {
                var r = new SKRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
                bool up = Points.Count > 1 && Points[1].Price >= Points[0].Price;
                var col = up ? theme.UpCandle : theme.DownCandle;
                fill.Color = col.WithAlpha(45);
                stroke.Color = col;
                canvas.DrawRect(r, fill);
                canvas.DrawLine(x0, y0, x1, y1, stroke);
                if (Points.Count > 1)
                {
                    double diff = Points[1].Price - Points[0].Price;
                    double pct = Points[0].Price != 0 ? diff / Points[0].Price * 100 : 0;
                    long ticks = series.Instrument.TickSize > 0 ? (long)Math.Round(diff / (double)series.Instrument.TickSize) : 0;
                    int bars = Math.Abs(layout.BarAt(x1) - layout.BarAt(x0));
                    var span = Points[1].Time - Points[0].Time;
                    string l1 = $"{(diff >= 0 ? "+" : "")}{Format(diff, decimals)} ({pct:+0.00;-0.00}%)  {ticks} ticks";
                    string l2 = $"{bars} barras · {FormatSpan(span)}";
                    DrawBox(canvas, font, (x0 + x1) / 2, Math.Max(y0, y1) + 6, SKColors.White, l1, l2);
                }

                break;
            }
            case DrawingKind.LongPosition:
            case DrawingKind.ShortPosition:
                RenderPosition(canvas, layout, series, theme, decimals, font);
                break;
            case DrawingKind.AnchoredVwap:
                if (trades != null) RenderAnchoredVwap(canvas, layout, series, trades, theme, decimals, font, stroke);
                break;
            case DrawingKind.FixedRangeProfile:
                if (trades != null) RenderFixedRangeProfile(canvas, layout, series, trades, theme, decimals, font, stroke);
                break;
        }

        if (Selected)
        {
            using var h = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
            using var hb = new SKPaint { IsAntialias = true, Color = theme.Selection, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            foreach (var (hx, hy) in HandlePositions(layout, series))
            {
                canvas.DrawCircle(hx, hy, 4.5f, h);
                canvas.DrawCircle(hx, hy, 4.5f, hb);
            }
        }

        if (Locked && Selected)
        {
            var (lx, ly) = HandlePositions(layout, series).FirstOrDefault();
            DrawLock(canvas, lx + 9, ly - 14, theme.Selection);
        }
    }

    private void RenderPosition(SKCanvas canvas, ChartLayout layout, CandleSeries series, ChartTheme theme, int decimals, SKFont font)
    {
        if (Points.Count < 3) return;
        var main = layout.Main;
        bool isLong = Kind == DrawingKind.LongPosition;
        double entry = Points[0].Price, target = Points[1].Price, stop = Points[2].Price;
        float x0 = XOf(layout, series, Points[0].Time), x1 = XOf(layout, series, Points[1].Time);
        if (x1 < x0 + 8) x1 = x0 + 8;
        float yE = main.Y(entry), yT = main.Y(target), yS = main.Y(stop);
        var profit = new SKRect(x0, Math.Min(yE, yT), x1, Math.Max(yE, yT));
        var risk = new SKRect(x0, Math.Min(yE, yS), x1, Math.Max(yE, yS));
        var (status, outcome, exitBar, exitPrice) = PositionResult(series);
        decimal qty = EffectiveQuantity(series.Instrument.VolumeStep);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        fill.Color = theme.UpCandle.WithAlpha(outcome == 1 ? (byte)70 : (byte)38);
        canvas.DrawRect(profit, fill);
        fill.Color = theme.DownCandle.WithAlpha(outcome == -1 ? (byte)70 : (byte)38);
        canvas.DrawRect(risk, fill);
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Selected ? 1.8f : 1.2f };
        line.Color = theme.UpCandle;
        canvas.DrawLine(x0, yT, x1, yT, line);
        line.Color = theme.DownCandle;
        canvas.DrawLine(x0, yS, x1, yS, line);
        line.Color = new SKColor(200, 205, 215);
        canvas.DrawLine(x0, yE, x1, yE, line);

        // recorrido (como en TradingView): de la entrada hasta donde terminó, o hasta la última vela si sigue abierta
        double pnlPoints = 0;
        if (exitBar >= 0)
        {
            float xe = Math.Clamp(layout.CenterX(exitBar), x0, Math.Max(x0, x1));
            float yX = main.Y(exitPrice);
            pnlPoints = isLong ? exitPrice - entry : entry - exitPrice;
            var trail = new SKRect(x0, Math.Min(yE, yX), xe, Math.Max(yE, yX));
            fill.Color = (pnlPoints >= 0 ? theme.UpCandle : theme.DownCandle).WithAlpha(60);
            canvas.DrawRect(trail, fill);
            using var dash = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = new SKColor(200, 205, 215, 200),
                PathEffect = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0) };
            canvas.DrawLine(x0, yE, xe, yX, dash);
        }

        var inst = series.Instrument;
        double tick = inst.TickSize > 0 ? (double)inst.TickSize : 0;
        double reward = Math.Abs(target - entry), riskAmt = Math.Abs(entry - stop);
        double rr = riskAmt > 0 ? reward / riskAmt : 0;
        var ic = CultureInfo.InvariantCulture;
        string Ticks(double d) => tick > 0 ? $" · {Math.Round(d / tick).ToString("0", ic)} ticks" : "";
        string Pct(double d) => entry != 0 ? $" · {(d / entry * 100).ToString("0.00", ic)}%" : "";
        string Money(double d) => qty > 0 ? $" · {((double)qty * d).ToString("0.00", ic)}" : "";
        // las etiquetas se centran en la parte visible de la caja
        var plotRect = main.Rect;
        float vx0 = Math.Max(x0, plotRect.Left), vx1 = Math.Min(x1, plotRect.Right);
        float cx = vx1 > vx0 ? (vx0 + vx1) / 2 : (x0 + x1) / 2;
        string tl = $"Objetivo {Format(target, decimals)}  +{Format(reward, decimals)}{Pct(reward)}{Ticks(reward)}{Money(reward)}";
        string sl = $"Stop {Format(stop, decimals)}  −{Format(riskAmt, decimals)}{Pct(riskAmt)}{Ticks(riskAmt)}{Money(riskAmt)}";
        float boxH = font.Spacing + 8;
        // la etiqueta del objetivo va del lado de afuera (arriba en la larga, abajo en la corta)
        DrawBox(canvas, font, cx, isLong ? yT - boxH - 4 : yT + 4, theme.UpCandle, tl);
        DrawBox(canvas, font, cx, isLong ? yS + 4 : yS - boxH - 4, theme.DownCandle, sl);
        string mid = $"{(isLong ? "Larga" : "Corta")} {Format(entry, decimals)} · R:R {rr.ToString("0.00", ic)}" + (qty > 0 ? $" · Cant {qty.ToString("0.########", ic)}" : "") + (status.Length > 0 ? $" · {status}" : "");
        var midCol = outcome == 1 ? theme.UpCandle : outcome == -1 ? theme.DownCandle : SKColors.White;
        if (exitBar >= 0)
        {
            // resultado: cerrada (tocó algo) o abierta (al último precio)
            string pl = (pnlPoints >= 0 ? "+" : "−") + Format(Math.Abs(pnlPoints), decimals) + Pct(Math.Abs(pnlPoints)).Replace(" · ", " (") + (entry != 0 ? ")" : "")
                        + (qty > 0 ? $" = {(pnlPoints >= 0 ? "+" : "−")}{((double)qty * Math.Abs(pnlPoints)).ToString("0.00", ic)}" : "");
            DrawBox(canvas, font, cx, yE - boxH / 2, midCol, mid, outcome == 0 ? $"P/G abierta {pl}" : $"P/G cerrada {pl}");
        }
        else DrawBox(canvas, font, cx, yE - boxH / 2, midCol, mid);
    }

    // ------------------------------------------------------------------ VWAP anclado y perfil de rango fijo

    private readonly List<SKPoint> _vwapPath = new();
    private SKRect? _profileRect;

    /// <summary>El VWAP desde el ancla (al tick, desde las operaciones) con sus bandas, como línea vela a vela.</summary>
    private void RenderAnchoredVwap(SKCanvas canvas, ChartLayout layout, CandleSeries series, TradeStore trades, ChartTheme theme, int decimals, SKFont font, SKPaint stroke)
    {
        _vwapPath.Clear();
        var main = layout.Main;
        var (firstBar, bars) = Calc.AnchoredVwap(trades, series, Points[0].Time);
        if (bars.Count == 0) return;
        int from = Math.Max(firstBar, layout.FirstVisible - 1), to = Math.Min(firstBar + bars.Count - 1, Math.Min(series.Count - 1, layout.LastVisible + 1));
        var up1 = new List<SKPoint>();
        var dn1 = new List<SKPoint>();
        var up2 = new List<SKPoint>();
        var dn2 = new List<SKPoint>();
        for (int b = Math.Max(firstBar, 0); b <= firstBar + bars.Count - 1 && b < series.Count; b++)
        {
            var (v, sd) = bars[b - firstBar];
            if (float.IsNaN(v)) continue;
            float x = layout.CenterX(b);
            _vwapPath.Add(new SKPoint(x, main.Y(v)));
            if (b < from || b > to) continue;
            if (Band1 > 0)
            {
                up1.Add(new SKPoint(x, main.Y(v + sd * Band1)));
                dn1.Add(new SKPoint(x, main.Y(v - sd * Band1)));
            }

            if (Band2 > 0)
            {
                up2.Add(new SKPoint(x, main.Y(v + sd * Band2)));
                dn2.Add(new SKPoint(x, main.Y(v - sd * Band2)));
            }
        }

        using var band = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, Width - 0.5f), Color = Color.WithAlpha(120), PathEffect = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0) };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = FillArgb != 0 ? new SKColor(FillArgb) : Color.WithAlpha(18) };
        void Band(List<SKPoint> up, List<SKPoint> dn)
        {
            if (up.Count < 2) return;
            using var path = new SKPath();
            path.MoveTo(up[0]);
            for (int i = 1; i < up.Count; i++) path.LineTo(up[i]);
            for (int i = dn.Count - 1; i >= 0; i--) path.LineTo(dn[i]);
            path.Close();
            canvas.DrawPath(path, fill);
            canvas.DrawPoints(SKPointMode.Polygon, up.ToArray(), band);
            canvas.DrawPoints(SKPointMode.Polygon, dn.ToArray(), band);
        }

        Band(up2, dn2);
        Band(up1, dn1);
        // la línea solo en lo visible
        var visible = _vwapPath.Where(p => p.X >= main.Rect.Left - 50 && p.X <= main.Rect.Right + 50).ToArray();
        if (visible.Length >= 2) canvas.DrawPoints(SKPointMode.Polygon, visible, stroke);
        else if (visible.Length == 1) canvas.DrawCircle(visible[0], 2, stroke);

        // ancla y etiqueta con el valor actual
        float xa = XOf(layout, series, Points[0].Time);
        float ya = _vwapPath.Count > 0 ? _vwapPath[0].Y : main.Y(Points[0].Price);
        canvas.DrawLine(xa, main.Rect.Top, xa, main.Rect.Bottom, band);
        var (last, lastSd) = bars[^1];
        if (!float.IsNaN(last))
        {
            string label = $"{UiText.Tr("VWAP anclado")} {Format(last, decimals)}" + (string.IsNullOrEmpty(Text) ? "" : " · " + Text);
            DrawBox(canvas, font, Math.Clamp(_vwapPath[^1].X, main.Rect.Left + 60, main.Rect.Right - 60), _vwapPath[^1].Y - font.Spacing - 12, Color, label);
        }
    }

    /// <summary>Perfil de volumen de las operaciones entre los dos puntos (ajustable a sesiones enteras): POC, VAH y VAL.</summary>
    private void RenderFixedRangeProfile(SKCanvas canvas, ChartLayout layout, CandleSeries series, TradeStore trades, ChartTheme theme, int decimals, SKFont font, SKPaint stroke)
    {
        _profileRect = null;
        if (Points.Count < 2) return;
        var main = layout.Main;
        var (from, to) = (Points[0].Time, Points[1].Time);
        if (to < from) (from, to) = (to, from);
        if (SnapToSessions)
        {
            // el rango abarca sesiones enteras: desde el inicio de la sesión del primer punto hasta el fin de la del segundo
            from = series.Session.SessionStartUtc(series.Session.SessionDate(from));
            to = series.Session.SessionStartUtc(series.Session.SessionDate(to).AddDays(1));
        }

        var r = Calc.FixedRangeProfile(trades, series.Instrument, from, to, ProfileRows, ValueAreaPercent);
        float x0 = XOf(layout, series, from), x1 = XOf(layout, series, to);
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Color.WithAlpha(140), PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0) };
        canvas.DrawLine(x0, main.Rect.Top, x0, main.Rect.Bottom, edge);
        canvas.DrawLine(x1, main.Rect.Top, x1, main.Rect.Bottom, edge);
        if (r == null || r.MaxVolume <= 0) return;

        float yTop = main.Y((double)(r.High + series.Instrument.TickSize)), yBot = main.Y((double)r.Low);
        _profileRect = new SKRect(Math.Min(x0, x1), yTop, Math.Max(x0, x1), yBot);
        float maxW = Math.Max(4, (x1 - x0) * Math.Clamp(ProfileWidthPercent, 5, 100) / 100f);
        using var fill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        var baseColor = FillArgb != 0 ? new SKColor(FillArgb) : Color.WithAlpha(90);
        decimal tick = series.Instrument.TickSize;
        foreach (var (row, v) in r.Rows)
        {
            decimal vol = v.Bid + v.Ask;
            float yr0 = main.Y((double)(row + r.RowSize)), yr1 = main.Y((double)row);
            float h = Math.Max(1, yr1 - yr0 - (yr1 - yr0 > 3 ? 1 : 0));
            bool inVa = row >= r.Val && row <= r.Vah;
            if (ShowDeltaProfile)
            {
                decimal d = v.Ask - v.Bid;
                float w = (float)(maxW * (double)Math.Abs(d) / (double)r.MaxVolume);
                fill.Color = (d >= 0 ? theme.UpCandle : theme.DownCandle).WithAlpha(inVa ? (byte)150 : (byte)90);
                canvas.DrawRect(new SKRect(x0, yr0, x0 + Math.Max(1, w), yr0 + h), fill);
            }
            else
            {
                float w = (float)(maxW * (double)vol / (double)r.MaxVolume);
                fill.Color = row == r.Poc ? new SKColor(255, 196, 0, 220) : inVa ? baseColor.WithAlpha((byte)Math.Min(255, baseColor.Alpha + 60)) : baseColor;
                canvas.DrawRect(new SKRect(x0, yr0, x0 + Math.Max(1, w), yr0 + h), fill);
            }
        }

        // POC, VAH, VAL hasta el final del rango
        float yPoc = main.Y((double)(r.Poc + r.RowSize / 2));
        using var poc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Selected ? Width + 1 : Width, Color = new SKColor(255, 196, 0) };
        canvas.DrawLine(x0, yPoc, x1, yPoc, poc);
        if (ShowValueArea)
        {
            float yVah = main.Y((double)(r.Vah + tick)), yVal = main.Y((double)r.Val);
            canvas.DrawLine(x0, yVah, x1, yVah, stroke);
            canvas.DrawLine(x0, yVal, x1, yVal, stroke);
        }

        using var text = new SKPaint { IsAntialias = true, Color = TextArgb != 0 ? new SKColor(TextArgb) : SKColors.White };
        string title = (string.IsNullOrEmpty(Text) ? UiText.Tr("Perfil") : Text) + $" · POC {Format((double)r.Poc, decimals)} · VA {Format((double)r.Val, decimals)}–{Format((double)r.Vah, decimals)} · {UiText.Tr("Vol")} {FormatVolume(r.Total)}";
        canvas.DrawUiText(title, Math.Max(x0, main.Rect.Left) + 4, Math.Max(yTop, main.Rect.Top) - 4, font, text);
    }

    private static string FormatVolume(decimal v)
    {
        decimal a = Math.Abs(v);
        return a >= 1_000_000_000m ? (a / 1_000_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "B"
            : a >= 1_000_000m ? (a / 1_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "M"
            : a >= 1_000m ? (a / 1_000m).ToString("0.##", CultureInfo.InvariantCulture) + "K"
            : a.ToString(a >= 10 ? "0" : "0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Cajita oscura con una o más líneas de texto, centrada en <paramref name="cx"/>.</summary>
    private static void DrawBox(SKCanvas canvas, SKFont font, float cx, float top, SKColor color, params string[] lines)
    {
        float w = lines.Max(l => RenderContext.MeasureWidth(font, l)) + 12;
        float h = font.Spacing * lines.Length + 8;
        float bx = cx - w / 2;
        using var box = new SKPaint { Color = new SKColor(10, 14, 24, 225), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(bx, top, bx + w, top + h), 4, 4, box);
        using var text = new SKPaint { IsAntialias = true, Color = color };
        for (int i = 0; i < lines.Length; i++)
            canvas.DrawUiText(lines[i], bx + 6, top + 4 + font.Spacing * i - font.Metrics.Ascent, font, text);
    }

    private static void DrawBell(SKCanvas canvas, float x, float y, SKColor color)
    {
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, Color = color };
        using var path = new SKPath();
        path.MoveTo(x - 4, y + 3);
        path.LineTo(x - 4, y - 1);
        path.QuadTo(x - 4, y - 5, x, y - 5);
        path.QuadTo(x + 4, y - 5, x + 4, y - 1);
        path.LineTo(x + 4, y + 3);
        path.Close();
        canvas.DrawPath(path, p);
        canvas.DrawLine(x - 5.5f, y + 3, x + 5.5f, y + 3, p);
        canvas.DrawCircle(x, y + 5, 1, p);
    }

    private static void DrawLock(SKCanvas canvas, float x, float y, SKColor color)
    {
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, Color = color };
        canvas.DrawRoundRect(new SKRect(x - 4, y - 1, x + 4, y + 5), 1.5f, 1.5f, p);
        canvas.DrawArc(new SKRect(x - 2.6f, y - 5, x + 2.6f, y + 1), 180, 180, false, p);
    }

    /// <summary>Extiende un segmento hasta los bordes pedidos del rectángulo.</summary>
    private static (float, float, float, float) Extend(float x0, float y0, float x1, float y1, SKRect r, bool left, bool right)
    {
        if (Math.Abs(x1 - x0) < 0.01f)
        {
            if (!left && !right) return (x0, y0, x1, y1);
            return (x0, left || y1 < y0 ? r.Top : y0, x1, right || y1 > y0 ? r.Bottom : y1);
        }

        float slope = (y1 - y0) / (x1 - x0);
        float ax = x0, ay = y0, bx = x1, by = y1;
        bool forward = x1 >= x0;
        if (right)
        {
            bx = forward ? r.Right : r.Left;
            by = y0 + slope * (bx - x0);
        }

        if (left)
        {
            ax = forward ? r.Left : r.Right;
            ay = y0 + slope * (ax - x0);
        }

        return (ax, ay, bx, by);
    }

    public IEnumerable<(float X, float Y)> HandlePositions(ChartLayout layout, CandleSeries series)
    {
        var main = layout.Main;
        if (IsPosition && Points.Count >= 3)
        {
            float x0 = XOf(layout, series, Points[0].Time), x1 = XOf(layout, series, Points[1].Time);
            if (x1 < x0 + 8) x1 = x0 + 8;
            yield return (x0, main.Y(Points[0].Price));
            yield return (x0, main.Y(Points[1].Price));
            yield return (x0, main.Y(Points[2].Price));
            yield return (x1, main.Y(Points[0].Price));
            yield break;
        }

        if (Kind == DrawingKind.FixedRangeProfile && Points.Count >= 2)
        {
            // los bordes del rango, a media altura de lo que ocupa el perfil
            float ym = _profileRect is { } pr ? pr.MidY : (main.Y(Points[0].Price) + main.Y(Points[1].Price)) / 2;
            yield return (XOf(layout, series, Points[0].Time), ym);
            yield return (XOf(layout, series, Points[1].Time), ym);
            yield break;
        }

        if (Kind == DrawingKind.AnchoredVwap)
        {
            float ya = _vwapPath.Count > 0 ? _vwapPath[0].Y : main.Y(Points[0].Price);
            yield return (XOf(layout, series, Points[0].Time), ya);
            yield break;
        }

        for (int i = 0; i < Points.Count; i++)
        {
            float x = Kind == DrawingKind.HorizontalLine ? (main.Rect.Left + main.Rect.Right) / 2 : XOf(layout, series, Points[i].Time);
            yield return (x, main.Y(Points[i].Price));
        }
    }

    /// <summary>Prueba si un punto toca el objeto. Devuelve el índice del manejador (-1 = el cuerpo).</summary>
    public bool HitTest(ChartLayout layout, CandleSeries series, float x, float y, out int handle)
    {
        handle = -1;
        if (Points.Count == 0 || !IsVisibleOn(series.Period)) return false;
        const float tol = 6;
        int k = 0;
        foreach (var (hx, hy) in HandlePositions(layout, series))
        {
            if (Math.Abs(hx - x) <= tol && Math.Abs(hy - y) <= tol)
            {
                handle = k;
                return true;
            }

            k++;
        }

        var main = layout.Main;
        var plot = main.Rect;
        float x0 = XOf(layout, series, Points[0].Time), y0 = main.Y(Points[0].Price);
        float x1 = x0, y1 = y0;
        if (Points.Count > 1)
        {
            x1 = XOf(layout, series, Points[1].Time);
            y1 = main.Y(Points[1].Price);
        }

        switch (Kind)
        {
            case DrawingKind.HorizontalLine:
                return Math.Abs(y - y0) <= tol;
            case DrawingKind.HorizontalRay:
                return Math.Abs(y - y0) <= tol && (ExtendLeft || x >= x0 - tol);
            case DrawingKind.VerticalLine:
                return Math.Abs(x - x0) <= tol;
            case DrawingKind.ParallelChannel:
            {
                double off = ChannelOffset;
                float uy0 = main.Y(Points[0].Price + off), uy1 = main.Y((Points.Count > 1 ? Points[1].Price : Points[0].Price) + off);
                var (ax, ay, bx, by) = Extend(x0, y0, x1, y1, plot, ExtendLeft, ExtendRight);
                var (cx, cy, dx, dy) = Extend(x0, uy0, x1, uy1, plot, ExtendLeft, ExtendRight);
                if (DistanceToSegment(x, y, ax, ay, bx, by, false) <= tol || DistanceToSegment(x, y, cx, cy, dx, dy, false) <= tol) return true;
                // dentro del canal
                if (x < Math.Min(ax, bx) || x > Math.Max(ax, bx)) return false;
                float t = Math.Abs(bx - ax) < 0.5f ? 0 : (x - ax) / (bx - ax);
                float ya = ay + t * (by - ay), yc = cy + t * (dy - cy);
                return y >= Math.Min(ya, yc) && y <= Math.Max(ya, yc);
            }
            case DrawingKind.Rectangle:
            case DrawingKind.Ruler:
            case DrawingKind.FibRetracement:
            {
                var r = new SKRect(Math.Min(x0, x1) - tol, Math.Min(y0, y1) - tol, Math.Max(x0, x1) + tol, Math.Max(y0, y1) + tol);
                if (Kind == DrawingKind.FibRetracement || ExtendRight) r.Right = plot.Right;
                if (ExtendLeft) r.Left = plot.Left;
                return r.Contains(x, y);
            }
            case DrawingKind.LongPosition:
            case DrawingKind.ShortPosition:
            {
                if (Points.Count < 3) return false;
                float xe = Math.Max(x1, x0 + 8);
                float top = Math.Min(main.Y(Points[1].Price), main.Y(Points[2].Price));
                float bottom = Math.Max(main.Y(Points[1].Price), main.Y(Points[2].Price));
                return new SKRect(x0 - tol, top - tol, xe + tol, bottom + tol).Contains(x, y);
            }
            case DrawingKind.Text:
                return Math.Abs(x - x0 - 20) <= 30 && Math.Abs(y - y0 + 6) <= 10 + FontSize / 2;
            case DrawingKind.AnchoredVwap:
            {
                // cerca de la línea del VWAP (los valores calculados en el último dibujo)
                if (_vwapPath.Count < 2 || x < x0 - tol) return false;
                for (int i = 1; i < _vwapPath.Count; i++)
                    if (DistanceToSegment(x, y, _vwapPath[i - 1].X, _vwapPath[i - 1].Y, _vwapPath[i].X, _vwapPath[i].Y, false) <= tol) return true;
                return false;
            }
            case DrawingKind.FixedRangeProfile:
            {
                if (_profileRect is not { } pr) return new SKRect(Math.Min(x0, x1) - tol, plot.Top, Math.Max(x0, x1) + tol, plot.Bottom).Contains(x, y);
                return new SKRect(pr.Left - tol, pr.Top - tol, pr.Right + tol, pr.Bottom + tol).Contains(x, y);
            }
            default:
            {
                bool left = Kind == DrawingKind.ExtendedLine || (Kind == DrawingKind.TrendLine && ExtendLeft);
                bool right = Kind is DrawingKind.Ray or DrawingKind.ExtendedLine || (Kind == DrawingKind.TrendLine && ExtendRight);
                var (ax, ay, bx, by) = Extend(x0, y0, x1, y1, plot, left, right);
                return DistanceToSegment(x, y, ax, ay, bx, by, false) <= tol;
            }
        }
    }

    private static float DistanceToSegment(float px, float py, float x0, float y0, float x1, float y1, bool infinite)
    {
        float dx = x1 - x0, dy = y1 - y0;
        float len2 = dx * dx + dy * dy;
        if (len2 < 0.0001f) return MathF.Sqrt((px - x0) * (px - x0) + (py - y0) * (py - y0));
        float t = ((px - x0) * dx + (py - y0) * dy) / len2;
        if (!infinite) t = Math.Clamp(t, 0, 1);
        float cx = x0 + t * dx, cy = y0 + t * dy;
        return MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    private static string Format(double v, int decimals) => v.ToString("F" + decimals, CultureInfo.InvariantCulture);

    private static string FormatSpan(TimeSpan s)
    {
        s = s.Duration();
        if (s.TotalDays >= 1) return $"{(int)s.TotalDays}d {s.Hours}h";
        if (s.TotalHours >= 1) return $"{(int)s.TotalHours}h {s.Minutes}m";
        if (s.TotalMinutes >= 1) return $"{(int)s.TotalMinutes}m {s.Seconds}s";
        return $"{s.Seconds}s";
    }
}
