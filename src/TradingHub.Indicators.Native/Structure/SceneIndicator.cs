using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Indicators.Native.VpUltimate;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Structure;

/// <summary>Dónde va el tablero de un indicador.</summary>
public enum DashCorner
{
    [Display(Name = "Arriba a la izquierda")] TopLeft,
    [Display(Name = "Arriba al centro")] TopCenter,
    [Display(Name = "Arriba a la derecha")] TopRight,
    [Display(Name = "Al medio a la izquierda")] MiddleLeft,
    [Display(Name = "Al medio")] Middle,
    [Display(Name = "Al medio a la derecha")] MiddleRight,
    [Display(Name = "Abajo a la izquierda")] BottomLeft,
    [Display(Name = "Abajo al centro")] BottomCenter,
    [Display(Name = "Abajo a la derecha")] BottomRight,
}

public enum CalendarPeriod
{
    [Display(Name = "Día")] Day,
    [Display(Name = "Semana")] Week,
    [Display(Name = "Mes")] Month,
    [Display(Name = "Trimestre")] Quarter,
    [Display(Name = "Año")] Year,
}

/// <summary>Los colores de siempre de estos indicadores.</summary>
public static class Pal
{
    public static readonly Color Bull = Color.FromArgb(255, 38, 166, 154);
    public static readonly Color Bear = Color.FromArgb(255, 239, 83, 80);
    public static readonly Color Neutral = Color.FromArgb(255, 41, 98, 255);
    public static readonly Color Muted = Color.FromArgb(255, 120, 123, 134);
    public static readonly Color Rose = Color.FromArgb(255, 251, 113, 133);
    public static readonly Color Amber = Color.FromArgb(255, 251, 140, 0);
    public static readonly Color Text = Color.FromArgb(255, 230, 232, 238);

    public static Color A(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);
}

public enum MarkKind
{
    TriangleUp,
    TriangleDown,
    Circle,
    Diamond,
    Cross,
    Square,
}

public enum TextSize
{
    [Display(Name = "Chico")] Small,
    [Display(Name = "Normal")] Normal,
    [Display(Name = "Grande")] Large,
}

/// <summary>
/// Lo que dibuja un indicador de estructura, en velas locales (las de <see cref="StructureIndicator"/>, −1 = hasta el borde
/// derecho) y precios: cajas, líneas, marcas, etiquetas, fondos por vela, caminos y un tablero. Se arma entero en cada
/// cálculo (solo con velas cerradas para los eventos: no se redibuja hacia atrás) y se dibuja lo que se ve.
/// </summary>
public sealed class Scene
{
    public readonly record struct BoxShape(int From, int To, double Top, double Bottom, Color Fill, Color Border, string? Text = null, Color TextColor = default, LineDashStyle Style = LineDashStyle.Solid, string? AlertName = null);

    public readonly record struct LineShape(int From, int To, double P0, double P1, Color Color, int Width = 1, LineDashStyle Style = LineDashStyle.Solid, string? Label = null, bool LabelRight = true, string? AlertName = null);

    public readonly record struct MarkShape(int Bar, double Price, MarkKind Kind, Color Color, string? Text = null, bool Above = true, int Size = 0);

    public readonly record struct TagShape(int Bar, double Price, string Text, Color Back, Color Fore, bool Above = true);

    public readonly record struct PathShape(List<(int Bar, double Price)> Points, Color Color, int Width = 1, LineDashStyle Style = LineDashStyle.Solid);

    public List<BoxShape> Boxes { get; } = new();
    public List<LineShape> Lines { get; } = new();
    public List<MarkShape> Marks { get; } = new();
    public List<TagShape> Tags { get; } = new();
    public List<PathShape> Paths { get; } = new();

    /// <summary>Fondo de toda la columna de una vela.</summary>
    public Dictionary<int, Color> Tints { get; } = new();

    /// <summary>El tablero (título y filas: nombre, valor, color del valor).</summary>
    public string? DashTitle { get; set; }

    public List<(string Name, string Value, Color Color)> Dash { get; } = new();

    public void Box(int from, int to, double top, double bottom, Color fill, Color border, string? text = null, Color textColor = default, LineDashStyle style = LineDashStyle.Solid, string? alert = null) =>
        Boxes.Add(new BoxShape(from, to, Math.Max(top, bottom), Math.Min(top, bottom), fill, border, text, textColor, style, alert));

    public void HLine(int from, int to, double price, Color color, int width = 1, LineDashStyle style = LineDashStyle.Solid, string? label = null, string? alert = null) =>
        Lines.Add(new LineShape(from, to, price, price, color, width, style, label, true, alert));

    public void Segment(int from, int to, double p0, double p1, Color color, int width = 1, LineDashStyle style = LineDashStyle.Solid) =>
        Lines.Add(new LineShape(from, to, p0, p1, color, width, style));

    public void Mark(int bar, double price, MarkKind kind, Color color, string? text = null, bool above = true) => Marks.Add(new MarkShape(bar, price, kind, color, text, above));

    public void Tag(int bar, double price, string text, Color back, Color fore, bool above = true) => Tags.Add(new TagShape(bar, price, text, back, fore, above));

    public void Row(string name, string value, Color color = default) => Dash.Add((name, value, color));
}

/// <summary>Base de los indicadores que se dibujan con una <see cref="Scene"/> (estructura, sesiones, niveles…).</summary>
public abstract class SceneIndicator : StructureIndicator
{
    private Scene _scene = new();
    private bool _dash = true;
    private DashCorner _corner = DashCorner.TopRight;
    private TextSize _text = TextSize.Normal;

    /// <summary>Día, semana y mes de cada vela local (cambian donde el gráfico empieza una sesión, una semana o un mes).</summary>
    protected int[] DayKey = Array.Empty<int>(), WeekKey = Array.Empty<int>(), MonthKey = Array.Empty<int>(), QuarterKey = Array.Empty<int>(), YearKey = Array.Empty<int>();

    /// <summary>La clave de período de cada vela local (día, semana, mes, trimestre, año).</summary>
    protected int[] PeriodKey(CalendarPeriod p) => p switch
    {
        CalendarPeriod.Week => WeekKey,
        CalendarPeriod.Month => MonthKey,
        CalendarPeriod.Quarter => QuarterKey,
        CalendarPeriod.Year => YearKey,
        _ => DayKey,
    };

    /// <summary>El período «automático» de los pivotes según el largo de la vela (hasta 15 min el día, después semana, mes, año).</summary>
    protected CalendarPeriod AutoPeriod()
    {
        var len = BarLength;
        if (len <= TimeSpan.Zero || len <= TimeSpan.FromMinutes(15)) return CalendarPeriod.Day;
        if (len < TimeSpan.FromDays(1)) return CalendarPeriod.Week;
        if (len < TimeSpan.FromDays(7)) return CalendarPeriod.Month;
        return CalendarPeriod.Year;
    }

    [Display(Name = "Mostrar el tablero", GroupName = "Tablero", Order = 900)]
    public bool ShowDashboard { get => _dash; set { _dash = value; RedrawChart(); } }

    [Display(Name = "Posición del tablero", GroupName = "Tablero", Order = 901)]
    public DashCorner DashboardPosition { get => _corner; set { _corner = value; RedrawChart(); } }

    [Display(Name = "Tamaño del texto", GroupName = "Tablero", Order = 902)]
    public TextSize TextSize { get => _text; set { _text = value; RedrawChart(); } }

    /// <summary>Lo que se dibuja ahora (para las pruebas).</summary>
    [System.ComponentModel.Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public Scene CurrentScene => _scene;

    /// <summary>Última vela local cerrada (la que se está formando no dispara eventos: no se redibuja hacia atrás).</summary>
    protected int LastClosed => Bars.Count - 2;

    /// <summary>Última vela local (la que se está formando).</summary>
    protected int LastBar => Bars.Count - 1;

    protected sealed override void Compute()
    {
        int n = Bars.Count;
        DayKey = new int[n];
        WeekKey = new int[n];
        MonthKey = new int[n];
        QuarterKey = new int[n];
        YearKey = new int[n];
        int d = 0, w = 0, m = 0, q = 0, y = 0;
        // el mes de la sesión (una sesión de CME empieza la noche anterior: se mira medio día después de la apertura)
        static DateTime Trade(DateTime t) => t.AddHours(12);
        var t0 = Trade(n > 0 ? TimeOf(0) : DateTime.UnixEpoch);
        int lastQ = (t0.Month - 1) / 3, lastY = t0.Year;
        for (int i = 0; i < n; i++)
        {
            int bar = i + Offset;
            if (i > 0)
            {
                if (IsNewSession(bar)) d++;
                if (IsNewWeek(bar)) w++;
                if (IsNewMonth(bar))
                {
                    m++;
                    var t = Trade(TimeOf(i));
                    if ((t.Month - 1) / 3 != lastQ || t.Year != lastY) q++;
                    if (t.Year != lastY) y++;
                    lastQ = (t.Month - 1) / 3;
                    lastY = t.Year;
                }
            }

            DayKey[i] = d;
            WeekKey[i] = w;
            MonthKey[i] = m;
            QuarterKey[i] = q;
            YearKey[i] = y;
        }

        var s = new Scene();
        Build(s);
        _scene = s;
    }

    /// <summary>Arma lo que se dibuja con <see cref="StructureIndicator.Bars"/>.</summary>
    protected abstract void Build(Scene s);

    /// <summary>El instante de apertura de una vela local.</summary>
    protected DateTime TimeOf(int i) => DateTime.UnixEpoch.AddSeconds(Bars[Math.Clamp(i, 0, Bars.Count - 1)].T);

    protected string Price(double p) => ChartInfo?.PriceChartContainer is { } c && c.Step > 0 ? ((decimal)p).ToString("0." + new string('#', Math.Min(8, Decimals(c.Step)))) : p.ToString("0.##");

    private static int Decimals(decimal step)
    {
        int k = 0;
        while (step != Math.Floor(step) && k < 10)
        {
            step *= 10;
            k++;
        }

        return Math.Max(k, 2);
    }

    // ===================================================================== alertas

    /// <summary>Las líneas y bordes que siguen vivos (a la derecha o hasta la última vela), con su nombre.</summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        var s = _scene;
        int last = Bars.Count - 1;
        if (last < 0) yield break;
        foreach (var l in s.Lines)
        {
            if (l.AlertName == null || l.P0 != l.P1 || (l.To >= 0 && l.To < last)) continue;
            yield return Level("ln" + l.AlertName.GetHashCode(), l.AlertName, l.From, l.To, l.P0);
        }

        foreach (var b in s.Boxes)
        {
            if (b.AlertName == null || (b.To >= 0 && b.To < last)) continue;
            foreach (var lv in BoxLevels("bx" + b.AlertName.GetHashCode(), b.AlertName, b.From, b.To, b.Top, b.Bottom)) yield return lv;
        }
    }

    // ===================================================================== dibujo

    private RenderFont Font(bool bold = false) => new("Arial", _text switch { TextSize.Small => 8, TextSize.Large => 11, _ => 9 }, bold ? FontStyle.Bold : FontStyle.Regular);

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Bars.Count == 0) return;
        var s = _scene;
        DashRect = Rectangle.Empty;
        var pc = ChartInfo.PriceChartContainer;
        int first = FirstVisibleBarNumber - Offset, last = LastVisibleBarNumber - Offset;
        var region = pc.Region;
        int bw = Math.Max(1, pc.BarsWidth);
        // lo que va a la derecha de la última vela (un gráfico de line break, etiquetas) se ve si se ve la última
        int lastVis = last >= LastBar ? int.MaxValue : last;
        bool Visible(int from, int to) => from <= lastVis && (to < 0 || to >= first);

        foreach (var (bar, color) in s.Tints)
        {
            if (bar < first || bar > last) continue;
            int x0 = X(bar), x1 = X(bar + 1);
            ctx.FillRectangle(color, new Rectangle(x0, region.Top, Math.Max(1, x1 - x0), region.Height));
        }

        var font = Font();
        foreach (var b in s.Boxes)
        {
            if (!Visible(b.From, b.To)) continue;
            int x0 = X(b.From), x1 = b.To < 0 ? RightEdge : X(b.To, false) + bw / 2;
            if (x1 <= x0) x1 = x0 + 2;
            int y0 = Y(b.Top), y1 = Y(b.Bottom);
            var rect = new Rectangle(x0, Math.Min(y0, y1), x1 - x0, Math.Max(1, Math.Abs(y1 - y0)));
            if (b.Fill.A > 0) ctx.FillRectangle(b.Fill, rect);
            if (b.Border.A > 0) ctx.DrawRectangle(Vp.Pen(b.Border, 1, b.Style), rect);
            if (!string.IsNullOrEmpty(b.Text))
            {
                var t = UiText.Tr(b.Text);
                var size = ctx.MeasureString(t, font);
                if (size.Height <= rect.Height + 4)
                    ctx.DrawString(t, font, b.TextColor.A > 0 ? b.TextColor : b.Border, Math.Max(rect.Left + 3, Math.Min(rect.Right - size.Width - 3, region.Right - size.Width - 4)), rect.Top + (rect.Height - size.Height) / 2);
            }
        }

        foreach (var l in s.Lines)
        {
            if (!Visible(l.From, l.To)) continue;
            int x0 = X(l.From), x1 = l.To < 0 ? RightEdge : X(l.To, false);
            int y0 = Y(l.P0), y1 = Y(l.P1);
            if (l.To < 0 && l.P0 != l.P1) x1 = X(LastBar, false); // una inclinada «hasta el borde»: P1 es su precio en la última vela

            ctx.DrawLine(Vp.Pen(l.Color, l.Width, l.Style), x0, y0, Math.Max(x0 + 1, x1), y1);
            if (!string.IsNullOrEmpty(l.Label))
            {
                var t = UiText.Tr(l.Label);
                var size = ctx.MeasureString(t, font);
                int lx = l.LabelRight ? Math.Min(x1 + 3, region.Right - size.Width - 2) : x0 + 2;
                ctx.DrawString(t, font, l.Color, lx, (l.LabelRight ? y1 : y0) - size.Height - 1);
            }
        }

        foreach (var p in s.Paths)
        {
            if (p.Points.Count < 2) continue;
            if (p.Points[^1].Bar < first || p.Points[0].Bar > lastVis) continue;
            var pts = p.Points.Select(q => new Point(X(q.Bar, false), Y(q.Price))).ToArray();
            ctx.DrawLines(Vp.Pen(p.Color, p.Width, p.Style), pts);
        }

        int ms = Math.Clamp(bw / 2 + 2, 4, 9);
        foreach (var m in s.Marks)
        {
            if (m.Bar < first || m.Bar > lastVis) continue;
            int x = X(m.Bar, false), y = Y(m.Price) + (m.Above ? -ms - 3 : ms + 3);
            DrawMark(ctx, m.Kind, x, y, m.Size > 0 ? m.Size : ms, m.Color);
            if (!string.IsNullOrEmpty(m.Text))
            {
                var t = UiText.Tr(m.Text);
                var size = ctx.MeasureString(t, font);
                ctx.DrawString(t, font, m.Color, x - size.Width / 2, m.Above ? y - ms - size.Height - 1 : y + ms + 1);
            }
        }

        foreach (var t in s.Tags)
        {
            if (t.Bar < first || t.Bar > lastVis) continue;
            var text = UiText.Tr(t.Text);
            var size = ctx.MeasureString(text, font);
            int x = X(t.Bar, false) - size.Width / 2 - 4, y = t.Above ? Y(t.Price) - size.Height - 10 : Y(t.Price) + 6;
            x = Math.Clamp(x, region.Left + 2, Math.Max(region.Left + 2, region.Right - size.Width - 10));
            ctx.FillRectangle(t.Back, new Rectangle(x, y, size.Width + 8, size.Height + 4), 3);
            ctx.DrawString(text, font, t.Fore, x + 4, y + 2);
        }

        if (_dash && s.Dash.Count > 0) DrawDash(ctx, s, region);
        RenderExtra(ctx, region);
    }

    /// <summary>Lo que un indicador dibuja aparte de la escena (un gráfico de dispersión, un mapa…).</summary>
    protected virtual void RenderExtra(RenderContext ctx, Rectangle region)
    {
    }

    /// <summary>El rectángulo del tablero (para poner algo debajo).</summary>
    protected Rectangle DashRect { get; private set; }

    private static void DrawMark(RenderContext ctx, MarkKind kind, int x, int y, int r, Color c)
    {
        switch (kind)
        {
            case MarkKind.TriangleUp:
                ctx.FillPolygon(c, new[] { new Point(x, y - r), new Point(x + r, y + r), new Point(x - r, y + r) });
                break;
            case MarkKind.TriangleDown:
                ctx.FillPolygon(c, new[] { new Point(x, y + r), new Point(x + r, y - r), new Point(x - r, y - r) });
                break;
            case MarkKind.Diamond:
                ctx.FillPolygon(c, new[] { new Point(x, y - r), new Point(x + r, y), new Point(x, y + r), new Point(x - r, y) });
                break;
            case MarkKind.Square:
                ctx.FillRectangle(c, new Rectangle(x - r + 1, y - r + 1, 2 * r - 2, 2 * r - 2));
                break;
            case MarkKind.Cross:
                ctx.DrawLine(new RenderPen(c, 2), x - r, y - r, x + r, y + r);
                ctx.DrawLine(new RenderPen(c, 2), x - r, y + r, x + r, y - r);
                break;
            default:
                ctx.FillEllipse(c, new Rectangle(x - r + 1, y - r + 1, 2 * r - 2, 2 * r - 2));
                break;
        }
    }

    private void DrawDash(RenderContext ctx, Scene s, Rectangle region)
    {
        var font = Font();
        var bold = Font(true);
        var rows = s.Dash.Select(r => (Name: UiText.Tr(r.Name), Value: UiText.Tr(r.Value), r.Color)).ToList();
        string? title = s.DashTitle == null ? null : UiText.Tr(s.DashTitle);
        int nameW = rows.Count == 0 ? 0 : rows.Max(r => ctx.MeasureString(r.Name, font).Width);
        int valW = rows.Count == 0 ? 0 : rows.Max(r => ctx.MeasureString(r.Value, bold).Width);
        int rowH = ctx.MeasureString("Ag", font).Height + 3;
        int titleW = title == null ? 0 : ctx.MeasureString(title, bold).Width;
        int w = Math.Max(titleW, nameW + valW + 18) + 16;
        int h = rows.Count * rowH + (title != null ? rowH + 2 : 0) + 10;
        int col = (int)_corner % 3, row = (int)_corner / 3;
        int x = col switch { 0 => region.Left + 8, 1 => region.Left + (region.Width - w) / 2, _ => region.Right - w - 8 };
        int y = row switch { 0 => region.Top + 26, 1 => region.Top + (region.Height - h) / 2, _ => region.Bottom - h - 8 };
        DashRect = new Rectangle(x, y, w, h);
        ctx.FillRectangle(Color.FromArgb(215, 18, 20, 26), new Rectangle(x, y, w, h), 5);
        ctx.DrawRectangle(new RenderPen(Color.FromArgb(90, 120, 130, 150), 1), new Rectangle(x, y, w, h), 5);
        int cy = y + 5;
        if (title != null)
        {
            ctx.DrawString(title, bold, Color.FromArgb(235, 235, 240), x + 8, cy);
            cy += rowH + 2;
        }

        foreach (var r in rows)
        {
            ctx.DrawString(r.Name, font, Color.FromArgb(160, 165, 175), x + 8, cy);
            var vs = ctx.MeasureString(r.Value, bold);
            ctx.DrawString(r.Value, bold, r.Color.A > 0 ? r.Color : Color.FromArgb(235, 235, 240), x + w - vs.Width - 8, cy);
            cy += rowH;
        }
    }
}

/// <summary>Cálculos de siempre (ATR de Wilder, medias, pivotes confirmados, sesiones del mundo).</summary>
public static class Ta
{
    /// <summary>ATR de Wilder (promedio simple de los primeros <paramref name="len"/> rangos, después suavizado).</summary>
    public static double[] Atr(IReadOnlyList<Bar> b, int len)
    {
        int n = b.Count;
        var res = new double[n];
        double sum = 0, atr = double.NaN;
        len = Math.Max(1, len);
        for (int i = 0; i < n; i++)
        {
            double tr = i == 0 ? b[i].H - b[i].L : Math.Max(b[i].H - b[i].L, Math.Max(Math.Abs(b[i].H - b[i - 1].C), Math.Abs(b[i].L - b[i - 1].C)));
            if (i < len)
            {
                sum += tr;
                res[i] = sum / (i + 1); // mientras se llena: el promedio de lo que hay
                if (i == len - 1) atr = sum / len;
                continue;
            }

            atr = (atr * (len - 1) + tr) / len;
            res[i] = atr;
        }

        return res;
    }

    public static double[] Ema(IReadOnlyList<double> x, int len)
    {
        var res = new double[x.Count];
        double k = 2.0 / (Math.Max(1, len) + 1), e = double.NaN, sum = 0;
        for (int i = 0; i < x.Count; i++)
        {
            if (i < len)
            {
                sum += x[i];
                e = sum / (i + 1);
            }
            else e = x[i] * k + e * (1 - k);
            res[i] = e;
        }

        return res;
    }

    public static double[] Sma(IReadOnlyList<double> x, int len)
    {
        var res = new double[x.Count];
        double sum = 0;
        for (int i = 0; i < x.Count; i++)
        {
            sum += x[i];
            if (i >= len) sum -= x[i - len];
            res[i] = i + 1 >= len ? sum / len : double.NaN;
        }

        return res;
    }

    public static double[] Rma(IReadOnlyList<double> x, int len)
    {
        var res = new double[x.Count];
        double sum = 0, r = double.NaN;
        for (int i = 0; i < x.Count; i++)
        {
            if (i < len)
            {
                sum += x[i];
                r = i == len - 1 ? sum / len : double.NaN;
            }
            else r = (r * (len - 1) + x[i]) / len;
            res[i] = r;
        }

        return res;
    }

    public static double[] StdDev(IReadOnlyList<double> x, int len)
    {
        var res = new double[x.Count];
        for (int i = 0; i < x.Count; i++)
        {
            if (i + 1 < len)
            {
                res[i] = double.NaN;
                continue;
            }

            double m = 0;
            for (int k = i - len + 1; k <= i; k++) m += x[k];
            m /= len;
            double v = 0;
            for (int k = i - len + 1; k <= i; k++) v += (x[k] - m) * (x[k] - m);
            res[i] = Math.Sqrt(v / len);
        }

        return res;
    }

    /// <summary>Un pivote confirmado: la vela del alto / bajo y la vela en que se confirma (<c>right</c> velas después).</summary>
    public readonly record struct Pivot(int Bar, int Confirm, double Price, bool High);

    /// <summary>
    /// Pivotes altos y bajos con <paramref name="left"/> velas a la izquierda (pueden empatar) y <paramref name="right"/> a la
    /// derecha (estrictas), hasta la vela <paramref name="upTo"/>: cada uno recién existe en su vela de confirmación.
    /// </summary>
    public static List<Pivot> Pivots(IReadOnlyList<Bar> b, int left, int right, int upTo)
    {
        var res = new List<Pivot>();
        left = Math.Max(1, left);
        right = Math.Max(1, right);
        for (int c = left + right; c <= upTo && c < b.Count; c++)
        {
            int p = c - right;
            bool hi = true, lo = true;
            for (int k = p - left; k < p && (hi || lo); k++)
            {
                if (b[k].H > b[p].H) hi = false;
                if (b[k].L < b[p].L) lo = false;
            }

            for (int k = p + 1; k <= c && (hi || lo); k++)
            {
                if (b[k].H >= b[p].H) hi = false;
                if (b[k].L <= b[p].L) lo = false;
            }

            if (hi) res.Add(new Pivot(p, c, b[p].H, true));
            if (lo) res.Add(new Pivot(p, c, b[p].L, false));
        }

        return res;
    }

    public static double TrueRange(IReadOnlyList<Bar> b, int i) =>
        i == 0 ? b[i].H - b[i].L : Math.Max(b[i].H - b[i].L, Math.Max(Math.Abs(b[i].H - b[i - 1].C), Math.Abs(b[i].L - b[i - 1].C)));

    // ------------------------------------------------------------------ sesiones del mundo

    public enum WorldSession
    {
        [Display(Name = "Sídney")] Sydney,
        [Display(Name = "Tokio")] Tokyo,
        [Display(Name = "Londres")] London,
        [Display(Name = "Nueva York")] NewYork,
    }

    private static TimeZoneInfo Zone(params string[] ids)
    {
        foreach (var id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception)
            {
                // probar el siguiente nombre (Windows / IANA)
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static readonly TimeZoneInfo SydneyTz = Zone("Australia/Sydney", "AUS Eastern Standard Time");
    private static readonly TimeZoneInfo TokyoTz = Zone("Asia/Tokyo", "Tokyo Standard Time");
    private static readonly TimeZoneInfo LondonTz = Zone("Europe/London", "GMT Standard Time");
    private static readonly TimeZoneInfo NewYorkTz = Zone("America/New_York", "Eastern Standard Time");

    /// <summary>Hora local de apertura y cierre de cada sesión (con su horario de verano).</summary>
    public static (TimeZoneInfo Tz, int Start, int End) Hours(WorldSession s) => s switch
    {
        WorldSession.Sydney => (SydneyTz, 7 * 60, 16 * 60),
        WorldSession.Tokyo => (TokyoTz, 9 * 60, 18 * 60),
        WorldSession.London => (LondonTz, 8 * 60, 17 * 60),
        _ => (NewYorkTz, 8 * 60, 17 * 60),
    };

    /// <summary>Si el instante UTC <paramref name="utc"/> cae en la sesión (y la fecha local de esa sesión).</summary>
    public static bool InSession(WorldSession s, DateTime utc, out DateTime localDay)
    {
        var (tz, start, end) = Hours(s);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        localDay = local.Date;
        int m = local.Hour * 60 + local.Minute;
        bool weekday = local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        return weekday && m >= start && m < end;
    }

    /// <summary>Sesión de horas sueltas en UTC (inicio y fin en minutos; si el fin es menor, cruza la medianoche).</summary>
    public static bool InUtcWindow(DateTime utc, int startMin, int endMin)
    {
        int m = utc.Hour * 60 + utc.Minute;
        return startMin <= endMin ? m >= startMin && m < endMin : m >= startMin || m < endMin;
    }
}
