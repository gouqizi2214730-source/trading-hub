using System.Drawing;
using System.Text.Json.Serialization;
using TradingHub.Charting.Drawings;
using TradingHub.Data;

namespace TradingHub.Charting;

/// <summary>
/// Una línea que un indicador dibujó en el gráfico de precio (con DrawLine, DrawLines o el borde de una caja), en instantes
/// y precios. El gráfico las anota al dibujar cada indicador, así cualquier línea de cualquier indicador (también uno propio
/// recién escrito) se puede elegir con clic derecho y alertar, sin que el indicador la declare.
/// </summary>
public sealed class DrawnLine
{
    /// <summary>Los puntos (de izquierda a derecha): instantes…</summary>
    public DateTime[] Times { get; set; } = Array.Empty<DateTime>();

    /// <summary>…y precios.</summary>
    public decimal[] Prices { get; set; } = Array.Empty<decimal>();

    /// <summary>Color del trazo (ARGB): distingue las líneas de un mismo indicador al seguirlas.</summary>
    public uint Color { get; set; }

    /// <summary>Llega a la última vela (o al borde derecho): sigue hacia la derecha mientras el indicador la dibuje.</summary>
    public bool Right { get; set; }

    /// <summary>Empieza en el borde izquierdo del gráfico: lo de antes no se vio.</summary>
    public bool CutLeft { get; set; }

    /// <summary>El texto que el indicador escribió junto a la línea (su nombre), si hay.</summary>
    public string Text { get; set; } = "";

    /// <summary>De la última captura: posiciones en velas (con fracción) de esta serie.</summary>
    [JsonIgnore] internal double[]? Bars;

    /// <summary>De la última captura: los puntos en píxeles (para el clic derecho).</summary>
    [JsonIgnore] internal PointF[]? Pixels;

    [JsonIgnore] public decimal LastPrice => Prices.Length > 0 ? Prices[^1] : 0;

    internal double[] BarsIn(CandleSeries series) => Bars ?? Times.Select(t => ChartDrawing.BarPos(series, t)).ToArray();

    /// <summary>
    /// El precio de la línea en la vela <paramref name="bar"/> (null fuera de su tramo). Si sigue a la derecha, una recta de
    /// dos puntos se prolonga con su pendiente y una curva queda en su último precio.
    /// </summary>
    public decimal? ValueAt(CandleSeries series, double bar)
    {
        if (Prices.Length == 0) return null;
        var bars = BarsIn(series);
        if (bars.Length != Prices.Length) return null;
        if (bar < bars[0] - 0.01) return null;
        if (bar > bars[^1] + 0.01)
        {
            if (!Right) return null;
            if (bars.Length == 2 && bars[1] - bars[0] > 0.5 && Prices[0] != Prices[1])
                return Prices[1] + (Prices[1] - Prices[0]) * (decimal)((bar - bars[1]) / (bars[1] - bars[0]));
            return Prices[^1];
        }

        for (int i = 1; i < bars.Length; i++)
        {
            if (bar > bars[i] + 0.01) continue;
            double span = bars[i] - bars[i - 1];
            if (span <= 1e-9) return Prices[i];
            double f = Math.Clamp((bar - bars[i - 1]) / span, 0, 1);
            return Prices[i - 1] + (Prices[i] - Prices[i - 1]) * (decimal)f;
        }

        return Prices[^1];
    }

    /// <summary>Puntos que se guardan con una alerta (de una curva larga, los últimos: lo que importa para alertar).</summary>
    private const int MaxSnapshotPoints = 400;

    /// <summary>Una copia que se guarda con la alerta (en instantes: sobrevive a más historia y a reiniciar).</summary>
    public DrawnLine Snapshot(CandleSeries series)
    {
        int n = Bars?.Length ?? Times.Length;
        int skip = Math.Max(0, n - MaxSnapshotPoints);
        return new DrawnLine
        {
            Times = Bars != null ? Bars.Skip(skip).Select(b => TimeAt(series, b)).ToArray() : Times.Skip(skip).ToArray(),
            Prices = Prices.Skip(skip).ToArray(),
            Color = Color,
            Right = Right,
            CutLeft = CutLeft || skip > 0,
            Text = Text,
        };
    }

    public DrawnLine Clone() => new()
    {
        Times = (DateTime[])Times.Clone(), Prices = (decimal[])Prices.Clone(), Color = Color, Right = Right, CutLeft = CutLeft, Text = Text,
        Bars = (double[]?)Bars?.Clone(), Pixels = (PointF[]?)Pixels?.Clone(),
    };

    /// <summary>Clave para reconocerla en las listas (color, inicio y precio).</summary>
    internal string Key(CandleSeries series)
    {
        var bars = BarsIn(series);
        string start = CutLeft || bars.Length == 0 ? "edge" : TimeAt(series, bars[0]).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"drawn:{Color:X8}:{start}:{LastPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>Instante de una posición en velas (con fracción; más allá de la última sigue al ritmo del período).</summary>
    internal static DateTime TimeAt(CandleSeries series, double b)
    {
        if (series.Count == 0) return DateTime.UtcNow;
        int bar = (int)Math.Floor(b);
        double frac = b - bar;
        if (bar >= series.Count - 1)
        {
            var dur = series.Period.ApproxDuration;
            var last = series[^1].Time;
            return dur > TimeSpan.Zero ? last + TimeSpan.FromTicks((long)(dur.Ticks * (b - (series.Count - 1)))) : last;
        }

        if (bar < 0) return series[0].Time;
        var t0 = series[bar].Time;
        var t1 = series[bar + 1].Time;
        return t0 + TimeSpan.FromTicks((long)((t1 - t0).Ticks * frac));
    }

    /// <summary>
    /// La línea de <paramref name="lines"/> (las de la última captura del mismo indicador) que es <paramref name="old"/>
    /// ahora: el mismo color, que empiece en la misma vela y el precio más cercano (un VAH en formación se mueve). Null si
    /// no está (por ejemplo, el gráfico mira otra parte de la historia).
    /// </summary>
    internal static DrawnLine? Match(CandleSeries series, DrawnLine old, IReadOnlyList<DrawnLine> lines)
    {
        var ob = old.BarsIn(series);
        if (ob.Length == 0) return null;
        decimal oldRef = old.LastPrice;
        DrawnLine? best = null;
        decimal bestD = decimal.MaxValue;
        foreach (var l in lines)
        {
            if (l.Color != old.Color || l.Prices.Length == 0) continue;
            var lb = l.BarsIn(series);
            decimal d;
            if (!old.CutLeft && !l.CutLeft)
            {
                if (Math.Abs(lb[0] - ob[0]) > 1.01) continue;
                // la misma línea que se movió un poco (un nivel en formación): hasta un 3 %
                d = Math.Abs(l.LastPrice - oldRef);
                if (oldRef != 0 && d / Math.Abs(oldRef) > 0.03m) continue;
            }
            else
            {
                // empieza fuera de la vista: tiene que pasar por el mismo precio donde se superponen (una vela antes del
                // final si se puede: la punta de una curva se mueve con el precio en vivo)
                double at = Math.Max(lb[0], ob[0]), until = Math.Min(lb[^1], ob[^1]);
                if (until < at - 0.01) continue;
                double probe = Math.Max(at, until - 1);
                if (l.ValueAt(series, probe) is not { } a || old.ValueAt(series, probe) is not { } b) continue;
                d = Math.Abs(a - b);
                if (oldRef != 0 && d / Math.Abs(oldRef) > 0.0005m) continue;
            }

            if (old.Text.Length > 0 && l.Text != old.Text) d += Math.Abs(oldRef) * 0.0001m + 0.0000001m; // a igualdad, el del mismo nombre
            if (d >= bestD) continue;
            bestD = d;
            best = l;
        }

        return best;
    }
}

/// <summary>Junta lo que dibuja un indicador mientras se dibuja (líneas, textos y los precios que pidió convertir).</summary>
internal sealed class DrawnLineCapture
{
    private const int MaxLines = 3000;
    private const float MinWidth = 12;

    private readonly ChartModel _model;
    private readonly ChartLayout _layout;
    private readonly PanelLayout _panel;
    private readonly List<(PointF[] Pts, Color Color, decimal?[] Exact)> _raw = new();

    // los tramos sueltos de DrawLine: una curva dibujada vela a vela son muchos tramos que se tocan, y se juntan en una
    private readonly List<(PointF A, PointF B, uint Color, decimal? EA, decimal? EB)> _segments = new();
    private const int MaxSegments = 40_000;
    private readonly List<(string Text, RectangleF Box)> _texts = new();

    /// <summary>El último precio que el indicador convirtió a cada Y (GetYByPrice): el precio exacto de una línea a esa altura.</summary>
    private readonly Dictionary<int, decimal> _yPrices = new();

    /// <summary>Las conversiones en orden: los puntos de una línea son las que pidió justo antes de dibujarla.</summary>
    private readonly List<(int Y, decimal Price)> _log = new();

    public void Convert(int y, decimal price)
    {
        _yPrices[y] = price;
        if (_log.Count < 200_000) _log.Add((y, price));
    }

    public DrawnLineCapture(ChartModel model, ChartLayout layout, PanelLayout panel)
    {
        _model = model;
        _layout = layout;
        _panel = panel;
    }

    public void Line(PointF[] pts, Color color)
    {
        if (color.A < 40 || pts.Length < 2) return;
        float minX = float.MaxValue, maxX = float.MinValue;
        foreach (var p in pts)
        {
            if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.X) || float.IsInfinity(p.Y)) return;
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
        }

        if (maxX - minX < 0.5f) return; // verticales
        if (pts.Length == 2)
        {
            // un tramo: puede ser una línea entera o un pedazo de una curva (se juntan al terminar)
            if (_segments.Count >= MaxSegments) return;
            var ex = ExactFor(pts);
            if (pts[0].X <= pts[1].X) _segments.Add((pts[0], pts[1], (uint)color.ToArgb(), ex[0], ex[1]));
            else _segments.Add((pts[1], pts[0], (uint)color.ToArgb(), ex[1], ex[0]));
            return;
        }

        if (_raw.Count >= MaxLines || maxX - minX < MinWidth) return;
        _raw.Add((pts, color, ExactFor(pts)));
    }

    /// <summary>
    /// El precio exacto de cada punto: el que el indicador convirtió a esa Y justo antes de dibujar (de atrás para adelante,
    /// así dos puntos a la misma altura con precios distintos no se confunden); si no, el último convertido a esa Y.
    /// </summary>
    private decimal?[] ExactFor(PointF[] pts)
    {
        var res = new decimal?[pts.Length];
        int cursor = _log.Count - 1;
        for (int i = pts.Length - 1; i >= 0; i--)
        {
            float y = pts[i].Y;
            int iy = (int)MathF.Round(y);
            if (MathF.Abs(y - iy) >= 0.01f) continue;
            int found = -1;
            for (int k = cursor, n = 0; k >= 0 && n < 3; k--, n++)
            {
                if (_log[k].Y != iy) continue;
                found = k;
                break;
            }

            if (found >= 0)
            {
                res[i] = _log[found].Price;
                cursor = found - 1;
            }
            else if (_yPrices.TryGetValue(iy, out var price)) res[i] = price;
        }

        return res;
    }

    /// <summary>Junta los tramos que se tocan punta con punta (del mismo color) en polilíneas.</summary>
    private void ChainSegments()
    {
        static (int, int) Key(PointF p) => ((int)MathF.Round(p.X * 2), (int)MathF.Round(p.Y * 2));
        foreach (var group in _segments.GroupBy(sg => sg.Color))
        {
            var chains = new List<(List<PointF> Pts, List<decimal?> Exact)>();
            var byEnd = new Dictionary<(int, int), int>();
            var seen = new HashSet<((int, int), (int, int))>();
            foreach (var sg in group.OrderBy(sg => sg.A.X).ThenBy(sg => sg.B.X))
            {
                if (!seen.Add((Key(sg.A), Key(sg.B)))) continue; // el mismo tramo dos veces
                if (byEnd.Remove(Key(sg.A), out int c))
                {
                    chains[c].Pts.Add(sg.B);
                    chains[c].Exact.Add(sg.EB);
                }
                else
                {
                    c = chains.Count;
                    chains.Add((new List<PointF> { sg.A, sg.B }, new List<decimal?> { sg.EA, sg.EB }));
                }

                byEnd[Key(sg.B)] = c;
            }

            var color = System.Drawing.Color.FromArgb((int)group.Key);
            foreach (var (pts, exact) in chains)
            {
                if (_raw.Count >= MaxLines) return;
                if (pts[^1].X - pts[0].X < MinWidth) continue; // marcas y cruces chicas
                _raw.Add((pts.ToArray(), color, exact.ToArray()));
            }
        }
    }

    public void Text(string text, RectangleF box)
    {
        if (_texts.Count < 2000 && !string.IsNullOrWhiteSpace(text)) _texts.Add((text.Trim(), box));
    }

    public List<DrawnLine> Finish()
    {
        ChainSegments();
        var res = new List<DrawnLine>();
        var seen = new HashSet<(uint, int, int, int, int)>();
        var series = _model.Series;
        int last = series.Count - 1;
        decimal tick = _model.Instrument.TickSize > 0 ? _model.Instrument.TickSize : 0;
        var rect = _panel.Rect;
        foreach (var (raw, color, rawExact) in _raw)
        {
            // de izquierda a derecha (una polilínea que vuelve sobre sí misma no es una línea de precio)
            var pts = raw;
            var exact = rawExact;
            if (pts[^1].X < pts[0].X)
            {
                pts = pts.Reverse().ToArray();
                exact = exact.Reverse().ToArray();
            }

            bool monotonic = true;
            for (int i = 1; i < pts.Length && monotonic; i++) monotonic = pts[i].X >= pts[i - 1].X - 0.01f;
            if (!monotonic) continue;
            if (pts.All(p => p.Y < rect.Top - 2) || pts.All(p => p.Y > rect.Bottom + 2)) continue;
            uint argb = (uint)color.ToArgb();
            if (!seen.Add((argb, (int)pts[0].X, (int)pts[0].Y, (int)pts[^1].X, (int)pts[^1].Y))) continue;
            if (IsLabelFrame(pts)) continue;

            var bars = new double[pts.Length];
            var prices = new decimal[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                bars[i] = _layout.BarAtExact(pts[i].X);
                if (exact[i] is { } e) prices[i] = e;
                else
                {
                    decimal v = (decimal)Math.Clamp(_panel.ValueAt(pts[i].Y), -7.9e27, 7.9e27);
                    prices[i] = tick > 0 ? Math.Round(v / tick) * tick : v;
                }
            }

            var line = new DrawnLine
            {
                Bars = bars,
                Prices = prices,
                Pixels = pts,
                Color = argb,
                Right = pts[^1].X >= rect.Right - 2 || bars[^1] >= last - 0.6,
                CutLeft = pts[0].X <= rect.Left + 2,
                Text = LabelFor(pts),
            };
            res.Add(line);
        }

        return res;
    }

    /// <summary>El borde de arriba o de abajo del recuadro de una etiqueta (un rectángulo apenas más ancho que su texto).</summary>
    private bool IsLabelFrame(PointF[] pts)
    {
        if (pts.Length != 2 || MathF.Abs(pts[0].Y - pts[1].Y) > 0.5f) return false;
        float minX = pts[0].X, maxX = pts[1].X, y = pts[0].Y;
        foreach (var (_, box) in _texts)
        {
            // el ancho del texto es aproximado: el recuadro lo cubre casi entero y es apenas más ancho
            if (maxX - minX > box.Width * 1.4f + 30) continue;
            if (minX > box.Left + box.Width * 0.3f || maxX < box.Right - box.Width * 0.3f) continue;
            if (y >= box.Top - 6 && y <= box.Bottom + 6) return true;
        }

        return false;
    }

    /// <summary>El texto escrito pegado a la línea (arriba o abajo, cerca de sus extremos).</summary>
    private string LabelFor(PointF[] pts)
    {
        if (_texts.Count == 0) return "";
        float minX = pts[0].X, maxX = pts[^1].X;
        string best = "";
        float bestD = float.MaxValue;
        foreach (var (text, box) in _texts)
        {
            if (box.Right < minX - 8 || box.Left > maxX + 140) continue;
            float x = Math.Clamp(box.Left, minX, maxX);
            float y = YAt(pts, x);
            // la línea pasa por la caja del texto o la toca por arriba o por abajo
            float d = y < box.Top ? box.Top - y : y > box.Bottom ? y - box.Bottom : 0;
            if (d > 6) continue;
            float dx = box.Left > maxX ? box.Left - maxX : 0;
            float score = d * 4 + dx * 0.05f;
            if (score >= bestD) continue;
            bestD = score;
            best = text;
        }

        return best.Length > 40 ? best[..40] + "…" : best;
    }

    internal static float YAt(PointF[] pts, float x)
    {
        if (x <= pts[0].X) return pts[0].Y;
        for (int i = 1; i < pts.Length; i++)
        {
            if (x > pts[i].X) continue;
            float span = pts[i].X - pts[i - 1].X;
            return span <= 0.001f ? pts[i].Y : pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * (x - pts[i - 1].X) / span;
        }

        return pts[^1].Y;
    }

    /// <summary>Distancia en píxeles de (x, y) a la polilínea (infinito si x cae fuera de su tramo).</summary>
    internal static float Distance(PointF[] pts, float x, float y, float slack = 3)
    {
        if (pts.Length < 2 || x < pts[0].X - slack || x > pts[^1].X + slack) return float.MaxValue;
        float best = float.MaxValue;
        for (int i = 1; i < pts.Length; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len2 = dx * dx + dy * dy;
            float t = len2 <= 1e-6f ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1);
            float px = a.X + t * dx - x, py = a.Y + t * dy - y;
            best = Math.Min(best, MathF.Sqrt(px * px + py * py));
        }

        return best;
    }
}
