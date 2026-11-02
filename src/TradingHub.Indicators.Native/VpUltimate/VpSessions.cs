using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators.Native.VpUltimate;

/// <summary>Las tres sesiones (Asia, Londres, Nueva York) en hora de Nueva York, con horario de verano automático.</summary>
public abstract class VpSessionBase : VpIndicator
{
    private string _asia = "1900-0400", _london = "0300-1100", _ny = "0900-1600";

    [Display(Name = "Sesión de Asia (hora de Nueva York)", GroupName = "Sesiones", Order = 1)]
    public string AsiaSession { get => _asia; set { _asia = Vp.ParseSession(value) != null ? value : _asia; RecalculateValues(); } }

    [Display(Name = "Sesión de Londres (hora de Nueva York)", GroupName = "Sesiones", Order = 2)]
    public string LondonSession { get => _london; set { _london = Vp.ParseSession(value) != null ? value : _london; RecalculateValues(); } }

    [Display(Name = "Sesión de Nueva York (hora de Nueva York)", GroupName = "Sesiones", Order = 3)]
    public string NewYorkSession { get => _ny; set { _ny = Vp.ParseSession(value) != null ? value : _ny; RecalculateValues(); } }

    /// <summary>Tramos en orden: 0 = Asia, 1 = Londres, 2 = Nueva York.</summary>
    protected (int Start, int End)[] Sessions => new[]
    {
        Vp.ParseSession(_asia) ?? (1900, 400), Vp.ParseSession(_london) ?? (300, 1100), Vp.ParseSession(_ny) ?? (900, 1600),
    };

    protected static readonly string[] SessionNames = { "Asia", "Londres", "Nueva York" };
}

/// <summary>Módulo 7 del «VP definitivo»: fondo de color en las horas de cada sesión (solo gráficos intradiarios).</summary>
[DisplayName("VP definitivo · Sesiones (fondo)")]
[Category(Vp.Category)]
[Description("Pinta el fondo durante las sesiones de Asia, Londres y Nueva York (hora de Nueva York, con horario de verano automático). Solo en gráficos intradiarios.")]
public class VpSessionBackground : VpSessionBase
{
    public VpSessionBackground()
    {
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Asia", GroupName = "Colores", Order = 10)]
    public bool ShowAsia { get; set; } = true;

    [Display(Name = "Color de Asia", GroupName = "Colores", Order = 11)]
    public Color AsiaColor { get; set; } = Color.FromArgb(26, 41, 98, 255);

    [Display(Name = "Londres", GroupName = "Colores", Order = 20)]
    public bool ShowLondon { get; set; } = true;

    [Display(Name = "Color de Londres", GroupName = "Colores", Order = 21)]
    public Color LondonColor { get; set; } = Color.FromArgb(26, 76, 175, 80);

    [Display(Name = "Nueva York", GroupName = "Colores", Order = 30)]
    public bool ShowNewYork { get; set; } = true;

    [Display(Name = "Color de Nueva York", GroupName = "Colores", Order = 31)]
    public Color NewYorkColor { get; set; } = Color.FromArgb(26, 242, 54, 69);

    protected override void Reset()
    {
    }

    protected override void Commit(int bar)
    {
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || !Intraday) return;
        var region = ChartInfo.PriceChartContainer.Region;
        var sessions = Sessions;
        var on = new[] { ShowAsia, ShowLondon, ShowNewYork };
        var colors = new[] { AsiaColor, LondonColor, NewYorkColor };
        int first = FirstVisibleBarNumber, last = Math.Min(LastVisibleBarNumber, CurrentBar - 1);
        for (int s = 0; s < 3; s++)
        {
            if (!on[s]) continue;
            int runStart = -1;
            for (int b = first; b <= last + 1; b++)
            {
                bool inside = b <= last && Vp.InSession(Vp.Hm(Vp.Ny(GetCandle(b).Time)), sessions[s]);
                if (inside && runStart < 0) runStart = b;
                if (!inside && runStart >= 0)
                {
                    int x0 = X(runStart), x1 = X(b - 1) + Math.Max(1, ChartInfo.PriceChartContainer.BarsWidth);
                    context.FillRectangle(colors[s], new Rectangle(x0, region.Top, Math.Max(1, x1 - x0), region.Height));
                    runStart = -1;
                }
            }
        }
    }
}

/// <summary>
/// Módulo 9 del «VP definitivo»: perfil de volumen de cada sesión (Asia, Londres, Nueva York) con POC, VAH / VAL; el POC
/// que todavía no fue tocado se extiende a la derecha hasta que una vela cerrada lo toca. Las sesiones se dibujan al
/// terminar (la que está en curso, opcional, con las velas cerradas).
/// </summary>
[DisplayName("VP definitivo · Perfil por sesión")]
[Category(Vp.Category)]
[Description("Perfil de volumen de cada sesión de Asia, Londres y Nueva York con POC, VAH y VAL; los POC no tocados se extienden hasta que el precio los toca. Gráficos de 1 hora o menos.")]
public class VpSessionProfiles : VpSessionBase
{
    private sealed class Result
    {
        public int Session, StartBar, EndBar, PocEnd;
        public decimal Lo, Hi, Step, Poc, Vah, Val;
        public double[] Bins = Array.Empty<double>();
        public bool PocDead, Final;
    }

    private readonly List<Result>[] _done = { new(), new(), new() };
    private readonly List<Result> _live = new(); // POC todavía sin tocar
    private readonly Result?[] _dev = new Result?[3];
    private readonly int[] _start = { -1, -1, -1 };
    private int _rows = 60, _vaPercent = 70;
    private VpVolumeMode _mode = VpVolumeMode.Raw;
    private decimal _cap = 10;
    private bool _excludeAuction, _developing;

    public VpSessionProfiles()
    {
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Asia", GroupName = "Sesiones", Order = 10)]
    public bool ShowAsia { get; set; }

    [Display(Name = "Color de Asia", GroupName = "Sesiones", Order = 11)]
    public Color AsiaColor { get; set; } = Color.FromArgb(255, 41, 98, 255);

    [Display(Name = "Londres", GroupName = "Sesiones", Order = 20)]
    public bool ShowLondon { get; set; } = true;

    [Display(Name = "Color de Londres", GroupName = "Sesiones", Order = 21)]
    public Color LondonColor { get; set; } = Color.FromArgb(255, 76, 175, 80);

    [Display(Name = "Nueva York", GroupName = "Sesiones", Order = 30)]
    public bool ShowNewYork { get; set; } = true;

    [Display(Name = "Color de Nueva York", GroupName = "Sesiones", Order = 31)]
    public Color NewYorkColor { get; set; } = Color.FromArgb(255, 242, 54, 69);

    [Display(Name = "Filas del perfil", GroupName = "Perfil", Order = 40)]
    [Range(10, 400)]
    public int Rows { get => _rows; set { _rows = Math.Clamp(value, 10, 400); RecalculateValues(); } }

    [Display(Name = "Área de valor (%)", GroupName = "Perfil", Order = 50)]
    [Range(1, 100)]
    public int ValueAreaPercent { get => _vaPercent; set { _vaPercent = Math.Clamp(value, 1, 100); RecalculateValues(); } }

    [Display(Name = "Volumen", GroupName = "Perfil", Order = 60)]
    public VpVolumeMode VolumeMode { get => _mode; set { _mode = value; RecalculateValues(); } }

    [Display(Name = "Recorte (× mediana de la sesión)", GroupName = "Perfil", Order = 70)]
    [Range(2, 100)]
    public decimal CapMultiple { get => _cap; set { _cap = Math.Clamp(value, 2, 100); RecalculateValues(); } }

    [Display(Name = "Excluir velas de subasta de apertura / cierre (acciones de EE. UU.)", GroupName = "Perfil", Order = 80)]
    public bool ExcludeAuction { get => _excludeAuction; set { _excludeAuction = value; RecalculateValues(); } }

    [Display(Name = "Mostrar la sesión en curso", GroupName = "Perfil", Order = 90)]
    public bool ShowDeveloping { get => _developing; set { _developing = value; RecalculateValues(); } }

    [Display(Name = "Ancho del perfil (% de la sesión)", GroupName = "Visualización", Order = 100)]
    [Range(10, 100)]
    public int WidthPercent { get; set; } = 60;

    [Display(Name = "Transparencia del perfil (%)", GroupName = "Visualización", Order = 110)]
    [Range(0, 100)]
    public int Transparency { get; set; } = 75;

    [Display(Name = "Conservar las últimas N de cada sesión", GroupName = "Visualización", Order = 120)]
    [Range(1, 50)]
    public int Keep { get; set; } = 3;

    [Display(Name = "POC", GroupName = "Visualización", Order = 130)]
    public bool ShowPoc { get; set; } = true;

    [Display(Name = "VAH / VAL", GroupName = "Visualización", Order = 140)]
    public bool ShowValueArea { get; set; } = true;

    [Display(Name = "Rellenar el área de valor", GroupName = "Visualización", Order = 150)]
    public bool ShowBox { get; set; }

    [Display(Name = "Extender el POC hasta que lo toquen", GroupName = "Visualización", Order = 160)]
    public bool ExtendPoc { get; set; } = true;

    [Display(Name = "Nombre de la sesión junto al POC", GroupName = "Visualización", Order = 170)]
    public bool ShowLabel { get; set; } = true;

    private bool Usable => Intraday && BarLength <= TimeSpan.FromHours(1);

    protected override void Reset()
    {
        foreach (var l in _done) l.Clear();
        _live.Clear();
        for (int i = 0; i < 3; i++)
        {
            _dev[i] = null;
            _start[i] = -1;
        }
    }

    private static bool Crosses((int Start, int End) s) => s.Start > s.End;

    protected override void Commit(int bar)
    {
        if (!Usable) return;
        var sessions = Sessions;
        var ny = Vp.Ny(GetCandle(bar).Time);
        bool dateChanged = bar > 0 && Vp.Ny(GetCandle(bar - 1).Time).Date != ny.Date;
        int hm = Vp.Hm(ny);
        for (int s = 0; s < 3; s++)
        {
            bool inside = Vp.InSession(hm, sessions[s]);
            bool split = dateChanged && !Crosses(sessions[s]);
            if (_start[s] >= 0 && (!inside || split))
            {
                // terminó: se arma con sus velas [inicio, bar - 1]
                if (Build(s, _start[s], bar - 1, true) is { } r)
                {
                    _done[s].Add(r);
                    _live.Add(r);
                    if (_done[s].Count > 200) _done[s].RemoveAt(0);
                }

                _start[s] = -1;
                _dev[s] = null;
            }

            if (inside && _start[s] < 0) _start[s] = bar;

            if (_developing && _start[s] >= 0 && bar >= CurrentBar - 2) _dev[s] = Build(s, _start[s], bar, false);
        }

        // los POC sin tocar siguen a la derecha
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var r = _live[i];
            if (bar <= r.EndBar) continue;
            r.PocEnd = bar;
            if (!Touched(bar, r.Poc)) continue;
            r.PocDead = true;
            _live.RemoveAt(i);
        }

        if (_live.Count > 600) _live.RemoveRange(0, _live.Count - 600);
    }

    private Result? Build(int session, int s, int e, bool final)
    {
        decimal lo = decimal.MaxValue, hi = decimal.MinValue;
        var sample = new List<decimal>();
        for (int b = s; b <= e; b++)
        {
            var c = GetCandle(b);
            if (c.Volume <= 0 || (_excludeAuction && Vp.IsAuction(c.Time))) continue;
            lo = Math.Min(lo, c.Low);
            hi = Math.Max(hi, c.High);
            if (_mode == VpVolumeMode.Cap)
                foreach (var l in c.GetAllPriceLevels()) sample.Add(l.Volume);
        }

        if (lo > hi) return null;
        decimal range = hi - lo;
        int n = range > 0 ? _rows : 1;
        decimal step = range > 0 ? range / n : Math.Max(InstrumentInfo?.TickSize ?? 0.01m, 0.0000001m);
        decimal cap = _mode == VpVolumeMode.Cap && sample.Count > 0 ? Vp.Median(sample) * _cap : decimal.MaxValue;
        var bins = new double[n];
        for (int b = s; b <= e; b++)
        {
            var c = GetCandle(b);
            if (_excludeAuction && Vp.IsAuction(c.Time)) continue;
            foreach (var l in c.GetAllPriceLevels())
            {
                if (l.Volume <= 0) continue;
                double v = (double)Math.Min(l.Volume, cap);
                if (_mode == VpVolumeMode.Log) v = Math.Log(1 + v);
                bins[(int)Math.Clamp(Math.Floor((l.Price - lo) / step), 0, n - 1)] += v;
            }
        }

        double total = bins.Sum(), max = bins.Max();
        if (total <= 0 || max <= 0) return null;
        int pi = Array.IndexOf(bins, max);
        int up = pi, dn = pi;
        double acc = max, target = total * _vaPercent / 100.0;
        while (acc < target && (up < n - 1 || dn > 0))
        {
            double vu = up < n - 1 ? bins[up + 1] : -1, vd = dn > 0 ? bins[dn - 1] : -1;
            if (vu > vd) acc += bins[++up];
            else acc += bins[--dn];
        }

        return new Result
        {
            Session = session,
            StartBar = s,
            EndBar = e,
            PocEnd = e,
            Lo = lo,
            Hi = hi,
            Step = step,
            Bins = bins,
            Poc = Math.Min(lo + (pi + 0.5m) * step, hi),
            Vah = Math.Min(lo + (up + 1) * step, hi),
            Val = lo + dn * step,
            Final = final,
        };
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || !Usable) return;
        var on = new[] { ShowAsia, ShowLondon, ShowNewYork };
        var colors = new[] { AsiaColor, LondonColor, NewYorkColor };
        for (int s = 0; s < 3; s++)
        {
            if (!on[s]) continue;
            var list = _done[s];
            for (int i = Math.Max(0, list.Count - Keep); i < list.Count; i++) Draw(context, list[i], colors[s]);
            if (_developing && _dev[s] is { } d) Draw(context, d, colors[s]);
        }
    }

    private void Draw(RenderContext ctx, Result r, Color color)
    {
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        int reach = ExtendPoc && r.Final ? (r.PocDead ? r.PocEnd : CurrentBar - 1) : r.EndBar;
        if (r.StartBar > last || reach + 1 < first) return;
        int x0 = X(r.StartBar), x1 = X(r.EndBar + 1);
        double max = r.Bins.Max();
        double w = Math.Max(1, x1 - x0) * WidthPercent / 100.0;
        var fill = Vp.Alpha(color, (int)Math.Round(255 * (100 - Transparency) / 100.0));
        for (int i = 0; i < r.Bins.Length; i++)
        {
            if (r.Bins[i] <= 0) continue;
            decimal lo = r.Lo + i * r.Step, hi = Math.Min(lo + r.Step, r.Hi);
            int yTop = Y(hi), yBot = Y(lo);
            int len = (int)Math.Round(r.Bins[i] / max * w);
            if (len > 0) ctx.FillRectangle(fill, new Rectangle(x0, Math.Min(yTop, yBot), len, Math.Max(1, Math.Abs(yBot - yTop))));
        }

        var style = r.Final ? LineDashStyle.Dash : LineDashStyle.Dot;
        if (ShowBox) ctx.FillRectangle(Vp.Alpha(color, r.Final ? 26 : 20), new Rectangle(x0, Y(r.Vah), Math.Max(1, x1 - x0), Math.Max(1, Y(r.Val) - Y(r.Vah))));
        if (ShowValueArea)
        {
            ctx.DrawLine(Vp.Pen(color, 1, style), x0, Y(r.Vah), x1, Y(r.Vah));
            ctx.DrawLine(Vp.Pen(color, 1, style), x0, Y(r.Val), x1, Y(r.Val));
        }

        if (ShowPoc)
        {
            int xEnd = ExtendPoc && r.Final ? X(reach, false) : x1;
            ctx.DrawLine(Vp.Pen(color, 2, r.Final ? LineDashStyle.Solid : LineDashStyle.Dot), x0, Y(r.Poc), Math.Max(x1, xEnd), Y(r.Poc));
        }

        if (ShowLabel)
        {
            string text = UiText.Tr(SessionNames[r.Session]) + " POC" + (r.Final ? "" : UiText.Tr(" (en curso)"));
            var size = ctx.MeasureString(text, SmallFont);
            ctx.DrawString(text, SmallFont, color, x1 + 3, Y(r.Poc) - size.Height - 1);
        }
    }
}
