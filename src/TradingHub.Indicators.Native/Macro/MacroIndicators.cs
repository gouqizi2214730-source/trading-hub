using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Feeds.External;
using TradingHub.Indicators.Native.Flow;
using TradingHub.Indicators.Native.Structure;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Macro;

// Macro y otras herramientas (sección 15): detección de cambios con CUSUM, tablero del mercado, flujo de los ETF al
// contado, correlación con otro símbolo y probabilidades de Polymarket contra el precio.

internal static class MacroMath
{
    /// <summary>Correlación de Pearson de las últimas <paramref name="n"/> parejas que terminan en <paramref name="end"/> (NaN si falta algo).</summary>
    public static double Pearson(IReadOnlyList<double> a, IReadOnlyList<double> b, int end, int n)
    {
        if (end - n + 1 < 0) return double.NaN;
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        for (int i = end - n + 1; i <= end; i++)
        {
            double x = a[i], y = b[i];
            if (!double.IsFinite(x) || !double.IsFinite(y)) return double.NaN;
            sa += x;
            sb += y;
            saa += x * x;
            sbb += y * y;
            sab += x * y;
        }

        double cov = sab / n - sa / n * (sb / n), va = saa / n - sa / n * (sa / n), vb = sbb / n - sb / n * (sb / n);
        return va <= 1e-18 || vb <= 1e-18 ? double.NaN : Math.Clamp(cov / Math.Sqrt(va * vb), -1, 1);
    }

    /// <summary>Puntaje z del último valor contra las <paramref name="n"/> velas que terminan en él.</summary>
    public static double Z(IReadOnlyList<double> x, int end, int n)
    {
        if (end - n + 1 < 0) return double.NaN;
        double s = 0, q = 0;
        for (int i = end - n + 1; i <= end; i++)
        {
            if (!double.IsFinite(x[i])) return double.NaN;
            s += x[i];
            q += x[i] * x[i];
        }

        double m = s / n, sd = Math.Sqrt(Math.Max(0, q / n - m * m));
        return sd <= 1e-12 ? 0 : (x[end] - m) / sd;
    }

    public static void Set(List<double> l, int i, double v)
    {
        while (l.Count <= i) l.Add(double.NaN);
        l[i] = v;
    }

    public static string Signed(double v, string fmt = "0.00") => double.IsFinite(v) ? (v > 0 ? "+" : "") + v.ToString(fmt, CultureInfo.InvariantCulture) : "—";
}

// ============================================================================ CUSUM

[DisplayName("Detección de cambios (CUSUM)")]
[Category(IndicatorCategories.Macro)]
[Description("Suma acumulada (CUSUM) de dos lados de los rendimientos logarítmicos normalizados: S+ junta las subas fuera de lo normal y S− las bajas. Cuando una pasa el umbral, el carácter del mercado cambió (se marca y la suma vuelve a cero). Cada vela usa solo lo anterior: no se redibuja.")]
public class CusumChangePoint : Indicator
{
    private readonly ValueDataSeries _pos = new("SP", "S+") { Color = Pal.Bull, Width = 2, ShowZeroValue = true };
    private readonly ValueDataSeries _neg = new("SN", "S−") { Color = Pal.Bear, Width = 2, ShowZeroValue = true };
    private readonly List<double> _r = new(), _spAfter = new(), _snAfter = new(), _since = new();
    private readonly List<double> _alarm = new();
    private int _period = 50;
    private decimal _drift = 0.5m, _thr = 5;

    public CusumChangePoint() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _pos;
        DataSeries.Add(_neg);
        LineSeries.Add(new LineSeries("U", "+umbral") { Value = 5, Color = Color.FromArgb(110, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("D", "−umbral") { Value = -5, Color = Color.FromArgb(110, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(60, 140, 145, 160) });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Período (velas)", GroupName = "Cálculo", Order = 10)] [Range(5, 1000)] public int Period { get => _period; set { _period = Math.Clamp(value, 5, 1000); RecalculateValues(); } }
    [Display(Name = "Deriva (desvíos que se descuentan por vela)", GroupName = "Cálculo", Order = 11)] [Range(0, 5)] public decimal Drift { get => _drift; set { _drift = value; RecalculateValues(); } }

    [Display(Name = "Umbral", GroupName = "Cálculo", Order = 12)]
    [Range(0.5, 50)]
    public decimal Threshold
    {
        get => _thr;
        set
        {
            _thr = value;
            LineSeries[0].Value = value;
            LineSeries[1].Value = -value;
            RecalculateValues();
        }
    }

    [Display(Name = "Marcar los cambios", GroupName = "Dibujo", Order = 20)] public bool ShowMarks { get; set; } = true;
    [Display(Name = "Mostrar el cuadro", GroupName = "Dibujo", Order = 21)] public bool ShowDash { get; set; } = true;
    [Display(Name = "Posición del cuadro", GroupName = "Dibujo", Order = 22)] public DashCorner DashPosition { get; set; } = DashCorner.TopRight;
    [Display(Name = "Cambio al alza", GroupName = "Colores", Order = 30)] public Color UpColor { get => _pos.Color; set => _pos.Color = value; }
    [Display(Name = "Cambio a la baja", GroupName = "Colores", Order = 31)] public Color DownColor { get => _neg.Color; set => _neg.Color = value; }
    [Display(Name = "Marca del cambio", GroupName = "Colores", Order = 32)] public Color MarkColor { get; set; } = Pal.Amber;

    protected override void OnRecalculate()
    {
        _r.Clear();
        _spAfter.Clear();
        _snAfter.Clear();
        _since.Clear();
        _alarm.Clear();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        double close = (double)c.Close, prev = bar > 0 ? (double)GetCandle(bar - 1).Close : close;
        MacroMath.Set(_r, bar, close > 0 && prev > 0 ? Math.Log(close / prev) : 0);
        double z = 0;
        if (bar >= _period - 1)
        {
            double s = 0, q = 0;
            for (int i = bar - _period + 1; i <= bar; i++)
            {
                s += _r[i];
                q += _r[i] * _r[i];
            }

            double mu = s / _period, sd = Math.Sqrt(Math.Max(0, q / _period - mu * mu));
            if (sd > 0) z = (_r[bar] - mu) / sd;
        }

        double sp0 = bar > 0 ? _spAfter[bar - 1] : 0, sn0 = bar > 0 ? _snAfter[bar - 1] : 0, d = (double)_drift, thr = (double)_thr;
        double sp = Math.Max(0, sp0 + z - d), sn = Math.Max(0, sn0 - z - d);
        bool up = sp > thr, dn = sn > thr;
        _pos[bar] = (decimal)sp;
        _neg[bar] = (decimal)(-sn);
        MacroMath.Set(_alarm, bar, up ? 1 : dn ? -1 : 0);
        double since0 = bar > 0 ? _since[bar - 1] : double.NaN;
        MacroMath.Set(_since, bar, up || dn ? 0 : double.IsNaN(since0) ? double.NaN : since0 + 1);
        // después de dibujar, la suma que disparó vuelve a cero
        MacroMath.Set(_spAfter, bar, up ? 0 : sp);
        MacroMath.Set(_snAfter, bar, dn ? 0 : sn);
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box || CurrentBar == 0) return;
        if (ShowMarks)
            for (int bar = Math.Max(0, FirstVisibleBarNumber); bar <= Math.Min(CurrentBar - 1, LastVisibleBarNumber) && bar < _alarm.Count; bar++)
            {
                if (_alarm[bar] == 0) continue;
                bool up = _alarm[bar] > 0;
                int x = ChartInfo.GetXByBar(bar, false), y = box.GetYByValue(up ? _pos[bar] : _neg[bar]);
                int r = Math.Clamp(ChartInfo.PriceChartContainer.BarsWidth / 2 + 2, 3, 6);
                ctx.FillEllipse(MarkColor, new Rectangle(x - r, y - r, 2 * r, 2 * r));
            }

        if (!ShowDash) return;
        int last = CurrentBar - 1;
        double sp = (double)_pos[last], sn = -(double)_neg[last];
        double since = last < _since.Count ? _since[last] : double.NaN;
        int lastAlarm = -1;
        for (int i = Math.Min(last, _alarm.Count - 1); i >= 0; i--)
            if (_alarm[i] != 0)
            {
                lastAlarm = i;
                break;
            }

        PaneCard.Draw(ctx, box.Region, "CUSUM", new List<(string, string, Color)>
        {
            ("Estadístico", MacroMath.Signed(sp >= sn ? sp : -sn), sp >= sn ? UpColor : DownColor),
            ("Velas desde el último cambio", double.IsFinite(since) ? since.ToString("0", CultureInfo.InvariantCulture) : "—", default),
            ("Último cambio", lastAlarm < 0 ? "—" : _alarm[lastAlarm] > 0 ? "al alza" : "a la baja", lastAlarm < 0 ? default : _alarm[lastAlarm] > 0 ? UpColor : DownColor),
        }, DashPosition);
    }
}

// ============================================================================ tablero del mercado

[DisplayName("Tablero del mercado (HUD)")]
[Category(IndicatorCategories.Macro)]
[Description("Un cuadro en una esquina del precio que lee el mercado de un vistazo: la tendencia en grande (alcista con el cierre sobre la EMA y el RSI sobre su línea media, bajista debajo de las dos, mixta si no), tres anillos (RSI, la parte comprada por agresores en la ventana de flujo y el volumen de esta vela contra su promedio), el ATR en % del precio y el cambio del interés abierto en la ventana como línea. La EMA se dibuja sobre el precio.")]
public class MarketHud : SceneIndicator
{
    private int _ema = 50, _rsi = 14, _atr = 14, _flow = 20;
    private decimal _mid = 50;
    private readonly DerivFeed _deriv;
    private string _trend = "Mixta";
    private Color _trendColor = Pal.Muted;
    private double _rsiNow = double.NaN, _buyShare = double.NaN, _volRatio = double.NaN, _atrPct = double.NaN, _oiChange = double.NaN, _emaDist = double.NaN;
    private List<double> _oiLine = new();

    public MarketHud()
    {
        DashboardPosition = DashCorner.BottomLeft;
        _deriv = new DerivFeed(a => DoActionInGuiThread(a), RecalculateValues);
    }

    [Display(Name = "Largo de la EMA (velas)", GroupName = "Cálculo", Order = 10)] [Range(2, 500)] public int EmaLength { get => _ema; set { _ema = Math.Clamp(value, 2, 500); RecalculateValues(); } }
    [Display(Name = "Largo del RSI (velas)", GroupName = "Cálculo", Order = 11)] [Range(2, 200)] public int RsiLength { get => _rsi; set { _rsi = Math.Clamp(value, 2, 200); RecalculateValues(); } }
    [Display(Name = "Largo del ATR (velas)", GroupName = "Cálculo", Order = 12)] [Range(2, 200)] public int AtrLength { get => _atr; set { _atr = Math.Clamp(value, 2, 200); RecalculateValues(); } }
    [Display(Name = "Ventana de flujo (velas)", GroupName = "Cálculo", Order = 13)] [Range(2, 500)] public int FlowWindow { get => _flow; set { _flow = Math.Clamp(value, 2, 500); RecalculateValues(); } }
    [Display(Name = "Línea media del RSI", GroupName = "Cálculo", Order = 14)] [Range(1, 99)] public decimal RsiMidline { get => _mid; set { _mid = value; RecalculateValues(); } }
    [Display(Name = "Dibujar la EMA", GroupName = "Dibujo", Order = 20)] public bool ShowEma { get; set; } = true;
    [Display(Name = "Color de la EMA", GroupName = "Dibujo", Order = 21)] public Color EmaColor { get; set; } = Color.FromArgb(255, 56, 189, 248);

    protected override void OnDispose()
    {
        base.OnDispose();
        _deriv.Dispose();
    }

    private static double[] Rsi(IReadOnlyList<double> c, int len)
    {
        var res = new double[c.Count];
        double up = 0, dn = 0;
        for (int i = 0; i < c.Count; i++)
        {
            res[i] = double.NaN;
            if (i == 0) continue;
            double d = c[i] - c[i - 1], u = Math.Max(0, d), w = Math.Max(0, -d);
            if (i <= len)
            {
                up += u / len;
                dn += w / len;
                if (i < len) continue;
            }
            else
            {
                up = (up * (len - 1) + u) / len;
                dn = (dn * (len - 1) + w) / len;
            }

            res[i] = dn <= 0 ? 100 : 100 - 100 / (1 + up / dn);
        }

        return res;
    }

    protected override void Build(Scene s)
    {
        int n = Bars.Count;
        if (n < 3) return;
        var close = Bars.Select(b => b.C).ToList();
        var ema = Ta.Ema(close, _ema);
        var rsi = Rsi(close, _rsi);
        var atr = Ta.Atr(Bars, _atr);
        if (ShowEma)
        {
            var pts = new List<(int, double)>();
            for (int i = Math.Min(_ema - 1, n - 1); i < n; i++)
                if (double.IsFinite(ema[i])) pts.Add((i, ema[i]));
            s.Paths.Add(new Scene.PathShape(pts, EmaColor, 2));
        }

        int last = n - 1;
        double c = close[last];
        _rsiNow = rsi[last];
        _emaDist = double.IsFinite(ema[last]) && ema[last] > 0 ? (c / ema[last] - 1) * 100 : double.NaN;
        _atrPct = double.IsFinite(atr[last]) && c > 0 ? atr[last] / c * 100 : double.NaN;
        bool above = c > ema[last], hot = _rsiNow > (double)_mid;
        (_trend, _trendColor) = above && hot ? ("Alcista", Pal.Bull) : !above && !hot ? ("Bajista", Pal.Bear) : ("Mixta", Pal.Muted);

        // parte comprada por agresores en la ventana y volumen de esta vela contra el promedio de la ventana
        double buy = 0, vol = 0, avg = 0;
        int from = Math.Max(0, last - _flow + 1);
        bool sided = false;
        for (int i = from; i <= last; i++)
        {
            var cd = GetCandle(i + Offset);
            if (cd.Delta != 0) sided = true;
            buy += ((double)cd.Volume + (double)cd.Delta) / 2;
            vol += (double)cd.Volume;
        }

        for (int i = Math.Max(0, last - _flow); i < last; i++) avg += Bars[i].V;
        avg /= Math.Max(1, last - Math.Max(0, last - _flow));
        _buyShare = sided && vol > 0 ? buy / vol : double.NaN;
        _volRatio = avg > 0 ? Bars[last].V / avg : double.NaN;

        // interés abierto (perpetuos): el cambio en la ventana y su línea
        _deriv.Ensure(InstrumentInfo?.Instrument ?? "", TimeOf(Math.Max(0, last - _flow * 4)), BarLength);
        _oiLine = new List<double>();
        for (int i = Math.Max(0, last - _flow * 3); i <= last; i++)
            if (_deriv.OiAt(TimeOf(i) + (BarLength > TimeSpan.Zero ? BarLength : TimeSpan.FromMinutes(1))) is { } oi)
                _oiLine.Add(oi);
        var oiNow = _deriv.OiAt(DateTime.UtcNow);
        var oiThen = _deriv.OiAt(TimeOf(Math.Max(0, last - _flow)));
        _oiChange = oiNow is { } a && oiThen is { } b && b > 0 ? (a / b - 1) * 100 : double.NaN;
    }

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (!ShowDashboard || Bars.Count < 3) return;
        var big = new RenderFont("Arial", TextSize == TextSize.Large ? 18 : 15, FontStyle.Bold);
        var font = new RenderFont("Arial", TextSize == TextSize.Small ? 8 : 9);
        var bold = new RenderFont("Arial", TextSize == TextSize.Small ? 8 : 9, FontStyle.Bold);
        int w = 250, h = 140;
        int col = (int)DashboardPosition % 3, row = (int)DashboardPosition / 3;
        int x = col switch { 0 => region.Left + 8, 1 => region.Left + (region.Width - w) / 2, _ => region.Right - w - 70 };
        int y = row switch { 0 => region.Top + 26, 1 => region.Top + (region.Height - h) / 2, _ => region.Bottom - h - 8 };
        ctx.FillRectangle(Color.FromArgb(220, 18, 20, 26), new Rectangle(x, y, w, h), 6);
        ctx.DrawRectangle(new RenderPen(Color.FromArgb(90, 120, 130, 150), 1), new Rectangle(x, y, w, h), 6);
        ctx.DrawString(UiText.Tr(_trend), big, _trendColor, x + 10, y + 6);
        string dist = UiText.Tr("vs EMA") + " " + MacroMath.Signed(_emaDist, "0.00") + " %";
        ctx.DrawString(dist, font, Pal.Text, x + w - ctx.MeasureString(dist, font).Width - 10, y + 12);
        var rings = new (string Name, double Frac, string Text, Color Color)[]
        {
            ("RSI", _rsiNow / 100, PaneCard.Num(_rsiNow, "0"), _rsiNow > (double)_mid ? Pal.Bull : Pal.Bear),
            ("Compras", _buyShare, double.IsFinite(_buyShare) ? (_buyShare * 100).ToString("0", CultureInfo.InvariantCulture) + " %" : "—", _buyShare >= 0.5 ? Pal.Bull : Pal.Bear),
            ("Volumen", _volRatio / 3, double.IsFinite(_volRatio) ? _volRatio.ToString("0.0", CultureInfo.InvariantCulture) + "×" : "—", _volRatio >= 1 ? Pal.Amber : Pal.Muted),
        };
        for (int k = 0; k < rings.Length; k++)
        {
            int cx = x + 30 + k * 78, cy = y + 60;
            PaneCard.Ring(ctx, cx, cy, 15, rings[k].Frac, rings[k].Color);
            var t = rings[k].Text;
            var ts = ctx.MeasureString(t, bold);
            ctx.DrawString(t, bold, Pal.Text, cx + 20, cy - ts.Height / 2 - 6);
            ctx.DrawString(UiText.Tr(rings[k].Name), font, FlowText.Dim, cx + 20, cy + 1);
        }

        int ly = y + 92;
        ctx.DrawString(UiText.Tr("ATR"), font, FlowText.Dim, x + 10, ly);
        ctx.DrawString(PaneCard.Pct(_atrPct, 2), bold, Pal.Text, x + 44, ly);
        ctx.DrawString(UiText.Tr("Interés abierto"), font, FlowText.Dim, x + 110, ly);
        string oi = double.IsFinite(_oiChange) ? MacroMath.Signed(_oiChange, "0.00") + " %" : "—";
        ctx.DrawString(oi, bold, _oiChange >= 0 ? Pal.Bull : Pal.Bear, x + w - ctx.MeasureString(oi, bold).Width - 10, ly);
        if (_oiLine.Count >= 2)
        {
            double lo = _oiLine.Min(), hi = _oiLine.Max();
            var rect = new Rectangle(x + 10, y + 110, w - 20, 22);
            var pts = _oiLine.Select((v, i) => new Point(rect.Left + i * rect.Width / (_oiLine.Count - 1), rect.Bottom - (int)(hi > lo ? (v - lo) / (hi - lo) * rect.Height : rect.Height / 2.0))).ToList();
            var poly = pts.Append(new Point(rect.Right, rect.Bottom)).Append(new Point(rect.Left, rect.Bottom)).ToArray();
            ctx.FillPolygon(Pal.A(Pal.Neutral, 60), poly);
            ctx.DrawLines(new RenderPen(Pal.Neutral, 1), pts.ToArray());
        }
        else
            ctx.DrawString(UiText.Tr("Sin interés abierto (solo perpetuos)"), font, FlowText.Dim, x + 10, y + 114);
    }
}

// ============================================================================ flujo de los ETF

[DisplayName("Flujo de los ETF al contado")]
[Category(IndicatorCategories.Macro)]
[Description("El flujo neto diario de los ETF al contado de la moneda del gráfico (millones de USD): una barra por día (entradas en verde, salidas en rojo; en velas de un día o menos el día ocupa todas sus velas), el acumulado del rango como línea y un cuadro con el último día, la suma, la racha y el día más grande. Los días que superan varias veces el día promedio llevan su tamaño. Necesita una conexión «Flujos de ETF (API)».")]
public class SpotEtfFlow : Indicator
{
    private readonly ValueDataSeries _in = new("IN", "Entradas") { VisualType = VisualMode.Histogram, Color = Pal.A(Pal.Bull, 200), ShowZeroValue = false };
    private readonly ValueDataSeries _out = new("OUT", "Salidas") { VisualType = VisualMode.Histogram, Color = Pal.A(Pal.Bear, 200), ShowZeroValue = false };
    private readonly ValueDataSeries _cum = new("CUM", "Acumulado") { VisualType = VisualMode.Hide, Color = Pal.Amber };
    private readonly Action _changed;
    private long _gen = -1;
    private string _asset = "";
    private readonly List<(int Bar, DateTime Day, double Flow)> _days = new();

    public SpotEtfFlow() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _in;
        DataSeries.Add(_out);
        DataSeries.Add(_cum);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(70, 140, 145, 160) });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _changed = () => DoActionInGuiThread(() =>
        {
            if (EtfFlows.Generation != _gen) RecalculateValues();
        });
    }

    [Display(Name = "Día grande (veces el día promedio)", GroupName = "Cálculo", Order = 10)] [Range(1, 20)] public decimal BigDay { get; set; } = 2;
    [Display(Name = "Dibujar el acumulado", GroupName = "Dibujo", Order = 20)] public bool ShowCumulative { get; set; } = true;
    [Display(Name = "Mostrar el cuadro", GroupName = "Dibujo", Order = 21)] public bool ShowDash { get; set; } = true;

    protected override void OnInitialize() => EtfFlows.Changed += _changed;

    protected override void OnDispose() => EtfFlows.Changed -= _changed;

    protected override void OnRecalculate()
    {
        _gen = EtfFlows.Generation;
        _days.Clear();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        _asset = OptionsData.UnderlyingOf(InstrumentInfo?.Instrument);
        var flows = EtfFlows.Daily(_asset, GetCandle(0).Time);
        var day = GetCandle(bar).Time.Date;
        int idx = LowerDay(flows, day);
        double f = idx >= 0 ? flows[idx].NetUsd / 1e6 : 0;
        _in[bar] = f > 0 ? (decimal)f : 0;
        _out[bar] = f < 0 ? (decimal)f : 0;
        double prevCum = bar > 0 ? (double)_cum[bar - 1] : 0;
        bool newDay = bar == 0 || GetCandle(bar - 1).Time.Date != day;
        if (bar > 0 && !newDay) prevCum -= f; // el día ya sumado en la vela anterior
        _cum[bar] = (decimal)(prevCum + f);
        if (newDay && idx >= 0)
        {
            _days.RemoveAll(d => d.Day == day);
            _days.Add((bar, day, f));
        }
    }

    private static int LowerDay(IReadOnlyList<EtfFlowDay> list, DateTime day)
    {
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var d = list[mid].Date.Date;
            if (d == day) return mid;
            if (d < day) lo = mid + 1;
            else hi = mid - 1;
        }

        return -1;
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box) return;
        if (EtfFlows.Unavailable(_asset.Length > 0 ? _asset : "BTC") is { } why)
        {
            PaneCard.Message(ctx, box.Region, why);
            return;
        }

        if (_days.Count == 0)
        {
            PaneCard.Message(ctx, box.Region, EtfFlows.LastError is { } e ? "ETF: " + e : "Trayendo los flujos de los ETF…");
            return;
        }

        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        double mean = _days.Average(d => Math.Abs(d.Flow));
        foreach (var d in _days)
        {
            if (d.Bar < FirstVisibleBarNumber - 200 || d.Bar > LastVisibleBarNumber || mean <= 0 || Math.Abs(d.Flow) < mean * (double)BigDay) continue;
            int x = ChartInfo.GetXByBar(d.Bar, true), y = box.GetYByValue((decimal)d.Flow);
            string t = (d.Flow > 0 ? "+" : "−") + Math.Abs(d.Flow).ToString("0", CultureInfo.InvariantCulture) + "M";
            ctx.DrawString(t, font, d.Flow > 0 ? Pal.Bull : Pal.Bear, x + 2, d.Flow > 0 ? y - 12 : y + 2);
        }

        if (ShowCumulative)
        {
            int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
            if (last > first)
            {
                double lo = double.MaxValue, hi = double.MinValue;
                for (int i = first; i <= last; i++)
                {
                    double v = (double)_cum[i];
                    lo = Math.Min(lo, v);
                    hi = Math.Max(hi, v);
                }

                var r = box.Region;
                int top = r.Top + 18, bottom = r.Bottom - 6;
                var pts = new List<Point>();
                for (int i = first; i <= last; i++)
                {
                    double v = (double)_cum[i];
                    pts.Add(new Point(ChartInfo.GetXByBar(i, false), bottom - (int)(hi > lo ? (v - lo) / (hi - lo) * (bottom - top) : (bottom - top) / 2.0)));
                }

                ctx.DrawLines(new RenderPen(Pal.A(_cum.Color, 220), 2), pts.ToArray());
                string label = UiText.Tr("Acumulado") + " " + MacroMath.Signed((double)_cum[last], "0") + "M";
                ctx.DrawString(label, font, _cum.Color, Math.Min(pts[^1].X + 4, r.Right - ctx.MeasureString(label, font).Width - 4), pts[^1].Y - 12);
            }
        }

        if (!ShowDash) return;
        var lastDay = _days[^1];
        int streak = 0;
        for (int i = _days.Count - 1; i >= 0 && Math.Sign(_days[i].Flow) == Math.Sign(lastDay.Flow) && lastDay.Flow != 0; i--) streak++;
        var bigDay = _days.OrderByDescending(d => Math.Abs(d.Flow)).First();
        PaneCard.Draw(ctx, box.Region, UiText.Tr("ETF al contado") + " · " + _asset, new List<(string, string, Color)>
        {
            ($"{UiText.Tr("Último día")} ({lastDay.Day:dd/MM})", MacroMath.Signed(lastDay.Flow, "0.0") + "M", lastDay.Flow >= 0 ? Pal.Bull : Pal.Bear),
            ("Suma del rango", MacroMath.Signed(_days.Sum(d => d.Flow), "0") + "M", _days.Sum(d => d.Flow) >= 0 ? Pal.Bull : Pal.Bear),
            ("Racha", streak + " " + UiText.Tr(lastDay.Flow >= 0 ? "días de entradas" : "días de salidas"), default),
            ($"{UiText.Tr("Día más grande")} ({bigDay.Day:dd/MM})", MacroMath.Signed(bigDay.Flow, "0") + "M", bigDay.Flow >= 0 ? Pal.Bull : Pal.Bear),
        });
    }
}

// ============================================================================ correlación

[DisplayName("Coeficiente de correlación")]
[Category(IndicatorCategories.Macro)]
[Description("La correlación de Pearson móvil entre este gráfico y otro símbolo de Binance (ETHUSDT por defecto), con precios o rendimientos logarítmicos, el área sombreada y los niveles +1, 0 y −1. Marca cuando cruza el cero. Cada vela usa solo sus datos y los anteriores: no se redibuja.")]
public class CorrelationCoefficient : Indicator
{
    public enum PriceSource
    {
        [Display(Name = "Apertura")] Open,
        [Display(Name = "Máximo")] High,
        [Display(Name = "Mínimo")] Low,
        [Display(Name = "Cierre")] Close,
        [Display(Name = "(Máx + Mín) / 2")] Hl2,
        [Display(Name = "(Máx + Mín + Cierre) / 3")] Hlc3,
        [Display(Name = "(Apert. + Máx + Mín + Cierre) / 4")] Ohlc4,
    }

    private readonly ValueDataSeries _corr = new("CORR", "Correlación") { Color = Color.FromArgb(255, 165, 120, 255), Width = 2, ShowZeroValue = true };
    private readonly List<double> _a = new(), _b = new(), _rawAs = new(), _rawBs = new();
    private readonly Action _changed;
    private long _gen = -1;
    private string _symbol = "ETHUSDT";
    private bool _futures = true, _log;
    private int _len = 20;
    private PriceSource _src = PriceSource.Close;
    private string? _error;

    public CorrelationCoefficient() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _corr;
        LineSeries.Add(new LineSeries("P1", "+1") { Value = 1, Color = Color.FromArgb(80, 140, 145, 160), LineDashStyle = LineDashStyle.Dot });
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(110, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("M1", "−1") { Value = -1, Color = Color.FromArgb(80, 140, 145, 160), LineDashStyle = LineDashStyle.Dot });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Historical);
        _changed = () => DoActionInGuiThread(() =>
        {
            if (CompareKlines.Generation != _gen) RecalculateValues();
        });
    }

    [Display(Name = "Símbolo para comparar", GroupName = "Cálculo", Order = 10)] public string Symbol { get => _symbol; set { _symbol = (value ?? "").Trim().ToUpperInvariant(); RecalculateValues(); } }
    [Display(Name = "De Binance Futures (si no, Spot)", GroupName = "Cálculo", Order = 11)] public bool Futures { get => _futures; set { _futures = value; RecalculateValues(); } }
    [Display(Name = "Largo (velas)", GroupName = "Cálculo", Order = 12)] [Range(3, 1000)] public int Length { get => _len; set { _len = Math.Clamp(value, 3, 1000); RecalculateValues(); } }
    [Display(Name = "Precio", GroupName = "Cálculo", Order = 13)] public PriceSource Source { get => _src; set { _src = value; RecalculateValues(); } }
    [Display(Name = "Con rendimientos logarítmicos", GroupName = "Cálculo", Order = 14)] public bool LogReturns { get => _log; set { _log = value; RecalculateValues(); } }
    [Display(Name = "Sombrear el área", GroupName = "Dibujo", Order = 20)] public bool ShowFill { get; set; } = true;
    [Display(Name = "Marcar los cruces del cero", GroupName = "Dibujo", Order = 21)] public bool ShowCrosses { get; set; } = true;
    [Display(Name = "Color", GroupName = "Dibujo", Order = 22)] public Color LineColor { get => _corr.Color; set => _corr.Color = value; }

    protected override void OnInitialize() => CompareKlines.Changed += _changed;

    protected override void OnDispose() => CompareKlines.Changed -= _changed;

    protected override void OnRecalculate()
    {
        _gen = CompareKlines.Generation;
        _a.Clear();
        _b.Clear();
        _rawAs.Clear();
        _rawBs.Clear();
    }

    private double Of(decimal o, decimal h, decimal l, decimal c) => (double)(_src switch
    {
        PriceSource.Open => o,
        PriceSource.High => h,
        PriceSource.Low => l,
        PriceSource.Hl2 => (h + l) / 2,
        PriceSource.Hlc3 => (h + l + c) / 3,
        PriceSource.Ohlc4 => (o + h + l + c) / 4,
        _ => c,
    });

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        var len = ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.FromMinutes(15);
        if (len <= TimeSpan.Zero) len = TimeSpan.FromMinutes(15);
        int minutes = len.TotalMinutes >= 15 ? 15 : len.TotalMinutes >= 5 ? 5 : 1;
        var (kl, err) = CompareKlines.Get(_symbol, _futures, GetCandle(0).Time, minutes);
        _error = kl.Count == 0 ? err : null;
        // la vela del otro símbolo en el mismo intervalo (armada con sus velas de 1, 5 o 15 min)
        var end = bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : c.Time + len;
        long s = new DateTimeOffset(DateTime.SpecifyKind(c.Time, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), e = new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        double pb = double.NaN;
        int lo = 0, hi = kl.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (kl[mid].OpenTimeMs < s) lo = mid + 1;
            else hi = mid;
        }

        if (lo < kl.Count && kl[lo].OpenTimeMs < e)
        {
            decimal o = kl[lo].Open, h = kl[lo].High, l = kl[lo].Low, cl = kl[lo].Close;
            for (int k = lo; k < kl.Count && kl[k].OpenTimeMs < e; k++)
            {
                h = Math.Max(h, kl[k].High);
                l = Math.Min(l, kl[k].Low);
                cl = kl[k].Close;
            }

            pb = Of(o, h, l, cl);
        }

        double pa = Of(c.Open, c.High, c.Low, c.Close);
        MacroMath.Set(_rawAs, bar, pa);
        MacroMath.Set(_rawBs, bar, pb);
        if (_log)
        {
            MacroMath.Set(_a, bar, bar > 0 && _rawAs[bar - 1] > 0 && pa > 0 ? Math.Log(pa / _rawAs[bar - 1]) : double.NaN);
            MacroMath.Set(_b, bar, bar > 0 && _rawBs[bar - 1] > 0 && pb > 0 ? Math.Log(pb / _rawBs[bar - 1]) : double.NaN);
        }
        else
        {
            MacroMath.Set(_a, bar, pa);
            MacroMath.Set(_b, bar, pb);
        }

        double r = MacroMath.Pearson(_a, _b, bar, _len);
        _corr[bar] = double.IsFinite(r) ? (decimal)r : 0;
    }


    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box) return;
        if (_error != null)
        {
            PaneCard.Message(ctx, box.Region, _error);
            return;
        }

        int first = Math.Max(_len, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        int y0 = box.GetYByValue(0);
        for (int bar = first; bar <= last; bar++)
        {
            double v = (double)_corr[bar];
            int x0 = ChartInfo.GetXByBar(bar, true), x1 = ChartInfo.GetXByBar(bar + 1, true);
            if (ShowFill && v != 0)
            {
                int y = box.GetYByValue((decimal)v);
                ctx.FillRectangle(Pal.A(v > 0 ? Pal.Bull : Pal.Bear, 45), new Rectangle(x0, Math.Min(y, y0), Math.Max(1, x1 - x0), Math.Abs(y - y0)));
            }

            if (ShowCrosses && bar > _len && Math.Sign((double)_corr[bar - 1]) != Math.Sign(v) && v != 0 && _corr[bar - 1] != 0)
            {
                int x = ChartInfo.GetXByBar(bar, false);
                bool up = v > 0;
                ctx.FillPolygon(up ? Pal.Bull : Pal.Bear, up
                    ? new[] { new Point(x, y0 - 9), new Point(x + 4, y0 - 3), new Point(x - 4, y0 - 3) }
                    : new[] { new Point(x, y0 + 9), new Point(x + 4, y0 + 3), new Point(x - 4, y0 + 3) });
            }
        }

        var font = new RenderFont("Arial", 8);
        ctx.DrawString(_symbol + (_futures ? " · Futures" : " · Spot"), font, FlowText.Dim, box.Region.Left + 6, box.Region.Bottom - 14);
    }
}

// ============================================================================ Polymarket contra el precio

[DisplayName("Probabilidades de Polymarket contra el precio")]
[Category(IndicatorCategories.Macro)]
[Description("Compara la probabilidad de un mercado de Polymarket (el resultado elegido, de 0 a 100 %) con el precio de este gráfico: la correlación móvil de los cambios de la probabilidad con los rendimientos, un histograma de divergencia (el puntaje z de la probabilidad menos el del precio, encendido pasado el umbral) y un cuadro con la lectura. Se ve si la probabilidad se mueve con el activo o se estiró. No se redibuja.")]
public class OddsVsPrice : Indicator
{
    private readonly ValueDataSeries _corr = new("CORR", "Correlación") { Color = Color.FromArgb(255, 86, 160, 255), Width = 2, ShowZeroValue = true };
    private readonly ValueDataSeries _div = new("DIV", "Divergencia") { VisualType = VisualMode.Histogram, Color = Pal.A(Pal.Muted, 150), ShowZeroValue = false };
    private readonly ValueDataSeries _odds = new("ODDS", "Probabilidad") { VisualType = VisualMode.Hide, Color = Pal.Amber };
    private readonly List<double> _p = new(), _o = new(), _dp = new(), _dr = new();
    private readonly Action _changed;
    private long _gen = -1;
    private string _market = "";
    private int _outcome, _window = 48, _zwin = 96;
    private decimal _thr = 2;
    private string? _error, _question;

    public OddsVsPrice() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _corr;
        DataSeries.Add(_div);
        DataSeries.Add(_odds);
        LineSeries.Add(new LineSeries("Z", "0") { Value = 0, Color = Color.FromArgb(90, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _changed = () => DoActionInGuiThread(() =>
        {
            if (Polymarket.Generation != _gen) RecalculateValues();
        });
    }

    public enum Outcome
    {
        [Display(Name = "Sí (YES)")] Yes,
        [Display(Name = "No (NO)")] No,
    }

    [Display(Name = "Mercado de Polymarket (dirección o slug)", GroupName = "Cálculo", Order = 10)] public string Market { get => _market; set { _market = (value ?? "").Trim(); RecalculateValues(); } }
    [Display(Name = "Resultado", GroupName = "Cálculo", Order = 11)] public Outcome Side { get => (Outcome)_outcome; set { _outcome = (int)value; RecalculateValues(); } }
    [Display(Name = "Ventana de la correlación (velas)", GroupName = "Cálculo", Order = 12)] [Range(5, 2000)] public int Window { get => _window; set { _window = Math.Clamp(value, 5, 2000); RecalculateValues(); } }
    [Display(Name = "Ventana de los puntajes z (velas)", GroupName = "Cálculo", Order = 13)] [Range(5, 5000)] public int ZWindow { get => _zwin; set { _zwin = Math.Clamp(value, 5, 5000); RecalculateValues(); } }
    [Display(Name = "Umbral de estiramiento", GroupName = "Cálculo", Order = 14)] [Range(0.5, 10)] public decimal Threshold { get => _thr; set { _thr = value; RecalculateValues(); } }
    [Display(Name = "Mostrar el cuadro", GroupName = "Dibujo", Order = 20)] public bool ShowDash { get; set; } = true;

    protected override void OnInitialize() => Polymarket.Changed += _changed;

    protected override void OnDispose() => Polymarket.Changed -= _changed;

    protected override void OnRecalculate()
    {
        _gen = Polymarket.Generation;
        _p.Clear();
        _o.Clear();
        _dp.Clear();
        _dr.Clear();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var len = ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.FromMinutes(15);
        if (len <= TimeSpan.Zero) len = TimeSpan.FromMinutes(15);
        int fidelity = (int)Math.Clamp(len.TotalMinutes, 1, 1440);
        var (info, hist, err) = Polymarket.Odds(_market, _outcome, GetCandle(0).Time, fidelity);
        _error = hist.Count == 0 ? err : null;
        _question = info?.Question;
        var c = GetCandle(bar);
        var end = bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : DateTime.UtcNow;
        // la última probabilidad publicada antes del cierre de la vela
        double odds = double.NaN;
        int lo = 0, hi = hist.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (hist[mid].T < end)
            {
                ans = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        if (ans >= 0 && hist[ans].T >= c.Time - TimeSpan.FromDays(2)) odds = hist[ans].P;
        double px = (double)c.Close;
        MacroMath.Set(_o, bar, odds);
        MacroMath.Set(_p, bar, px);
        MacroMath.Set(_dp, bar, bar > 0 ? odds - _o[bar - 1] : double.NaN);
        MacroMath.Set(_dr, bar, bar > 0 && _p[bar - 1] > 0 ? Math.Log(px / _p[bar - 1]) : double.NaN);
        _odds[bar] = double.IsFinite(odds) ? (decimal)(odds * 100) : 0;
        double r = MacroMath.Pearson(_dp, _dr, bar, _window);
        _corr[bar] = double.IsFinite(r) ? (decimal)r : 0;
        double d = MacroMath.Z(_o, bar, _zwin) - MacroMath.Z(_p, bar, _zwin);
        _div[bar] = double.IsFinite(d) ? (decimal)d : 0;
        _div.Colors[bar] = double.IsFinite(d) && Math.Abs(d) >= (double)_thr ? (d > 0 ? Pal.A(Pal.Bull, 220) : Pal.A(Pal.Bear, 220)) : Pal.A(Pal.Muted, 120);
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box) return;
        if (_error != null)
        {
            PaneCard.Message(ctx, box.Region, _error);
            return;
        }

        int first = Math.Max(1, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        double thr = (double)_thr;
        for (int bar = first; bar <= last; bar++)
        {
            double d = (double)_div[bar], p = (double)_div[bar - 1];
            if (Math.Abs(d) < thr || Math.Abs(p) >= thr || bar == CurrentBar - 1) continue;
            int x = ChartInfo.GetXByBar(bar, false), y = box.GetYByValue((decimal)d);
            bool up = d > 0;
            ctx.FillPolygon(up ? Pal.Bull : Pal.Bear, up
                ? new[] { new Point(x, y - 12), new Point(x + 4, y - 5), new Point(x - 4, y - 5) }
                : new[] { new Point(x, y + 12), new Point(x + 4, y + 5), new Point(x - 4, y + 5) });
        }

        if (!ShowDash || CurrentBar == 0) return;
        int lb = CurrentBar - 1;
        double odds = (double)_odds[lb], corr = (double)_corr[lb], div = (double)_div[lb];
        string q = _question ?? Polymarket.SlugOf(_market);
        if (q.Length > 48) q = q[..47] + "…";
        PaneCard.Draw(ctx, box.Region, q, new List<(string, string, Color)>
        {
            (Side == Outcome.Yes ? "Probabilidad del SÍ" : "Probabilidad del NO", odds > 0 ? odds.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "—", Pal.Amber),
            ("Correlación", MacroMath.Signed(corr), corr >= 0 ? Pal.Bull : Pal.Bear),
            ("Divergencia (z)", MacroMath.Signed(div), Math.Abs(div) >= thr ? div > 0 ? Pal.Bull : Pal.Bear : default),
            ("Lectura", Math.Abs(div) >= thr ? div > 0 ? "la probabilidad se adelantó al precio" : "la probabilidad quedó atrás del precio" : Math.Abs(corr) >= 0.5 ? "se mueven juntos" : "sin relación clara", default),
        });
    }
}
