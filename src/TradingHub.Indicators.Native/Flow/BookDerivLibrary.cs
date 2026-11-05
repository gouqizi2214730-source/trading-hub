using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Data;
using TradingHub.Indicators.Native.Structure;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Flow;

// Libro de órdenes (sección 09) y derivados (sección 10): oscilador de desequilibrio de liquidez, presión del libro,
// bandas de liquidación por apalancamiento, mapa estimado de liquidaciones y estallidos de liquidaciones.

/// <summary>La profundidad del libro entre dos distancias del precio (en % a cada lado): compras y ventas.</summary>
internal static class BookDepth
{
    public static readonly double[] Steps = { 0, 1, 2.5, 5, 10 };

    /// <summary>Lo que hay en el libro (la última foto de la vela) entre <paramref name="fromPct"/> y <paramref name="toPct"/> del precio.</summary>
    public static (double Bid, double Ask)? Between(IReadOnlyList<DepthSample> samples, double price, decimal tick, double fromPct, double toPct)
    {
        if (samples.Count == 0 || price <= 0 || tick <= 0) return null;
        var d = samples[^1];
        int near = fromPct <= 0 ? -1 : (int)(price * fromPct / 100 / (double)tick), far = Math.Max(1, (int)(price * toPct / 100 / (double)tick));
        var (fb, fa) = d.Within(far);
        var (nb, na) = near < 0 ? (0d, 0d) : d.Within(near);
        return (fb - nb, fa - na);
    }
}

/// <summary>Oscilador del desequilibrio del libro (compras − ventas entre dos distancias), suavizado, con extremos.</summary>
[DisplayName("Oscilador de desequilibrio de liquidez")]
[Category(IndicatorCategories.OrderBook)]
[Description("La diferencia entre la liquidez de compra y la de venta del libro (entre dos distancias del precio, en %), de −100 a 100, suavizada. Resalta los extremos que pasan los umbrales. Usa las fotos del libro grabadas mientras el gráfico está abierto.")]
public class LiquidityImbalanceOscillator : Indicator
{
    public enum PlotStyle
    {
        [Display(Name = "Línea")] Line,
        [Display(Name = "Barras")] Bars,
        [Display(Name = "Barras por fuerza")] StrengthBars,
    }

    private readonly ValueDataSeries _osc = new("LIO", "Desequilibrio") { Width = 2, ShowZeroValue = true };
    private readonly List<double> _raw = new();
    private int _d1, _d2 = 2, _smooth = 3;
    private PlotStyle _style = PlotStyle.Bars;

    public LiquidityImbalanceOscillator() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _osc;
        LineSeries.Add(new LineSeries("Mid", "0") { Value = 0, Color = Color.FromArgb(110, 140, 140, 150), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("Up", "+") { Value = 40, Color = Color.FromArgb(70, 100, 255, 218), LineDashStyle = LineDashStyle.Dot });
        LineSeries.Add(new LineSeries("Dn", "−") { Value = -40, Color = Color.FromArgb(70, 244, 67, 54), LineDashStyle = LineDashStyle.Dot });
        Apply();
    }

    private static readonly string[] DepthNames = { "0 %", "1 %", "2,5 %", "5 %", "10 %" };

    public enum DepthFrom
    {
        [Display(Name = "0 %")] P0,
        [Display(Name = "1 %")] P1,
        [Display(Name = "2,5 %")] P25,
        [Display(Name = "5 %")] P5,
    }

    public enum DepthTo
    {
        [Display(Name = "1 %")] P1 = 1,
        [Display(Name = "2,5 %")] P25,
        [Display(Name = "5 %")] P5,
        [Display(Name = "10 %")] P10,
    }

    [Display(Name = "Desde", GroupName = "Cálculo", Order = 10)] public DepthFrom From { get => (DepthFrom)_d1; set { _d1 = (int)value; RecalculateValues(); } }
    [Display(Name = "Hasta", GroupName = "Cálculo", Order = 11)] public DepthTo To { get => (DepthTo)_d2; set { _d2 = (int)value; RecalculateValues(); } }
    [Display(Name = "Suavizado (velas)", GroupName = "Cálculo", Order = 12)] [Range(1, 100)] public int Smooth { get => _smooth; set { _smooth = value; RecalculateValues(); } }
    [Display(Name = "Estilo", GroupName = "Dibujo", Order = 20)] public PlotStyle Style { get => _style; set { _style = value; Apply(); } }
    [Display(Name = "Umbral de compras", GroupName = "Dibujo", Order = 21)] [Range(1, 100)] public decimal BidThreshold { get => LineSeries[1].Value; set => LineSeries[1].Value = value; }
    [Display(Name = "Umbral de ventas", GroupName = "Dibujo", Order = 22)] [Range(1, 100)] public decimal AskThreshold { get => -LineSeries[2].Value; set => LineSeries[2].Value = -value; }
    [Display(Name = "Resaltar los extremos", GroupName = "Dibujo", Order = 23)] public bool Highlight { get; set; } = true;
    [Display(Name = "Compras", GroupName = "Colores", Order = 30)] public Color BidColor { get; set; } = Color.FromArgb(255, 100, 255, 218);
    [Display(Name = "Ventas", GroupName = "Colores", Order = 31)] public Color AskColor { get; set; } = Color.FromArgb(255, 244, 67, 54);
    [Display(Name = "Extremos", GroupName = "Colores", Order = 32)] public Color ExtremeColor { get; set; } = Color.FromArgb(255, 255, 235, 59);

    private void Apply() => _osc.VisualType = _style == PlotStyle.Line ? VisualMode.Line : VisualMode.Histogram;

    protected override void OnRecalculate() => _raw.Clear();

    protected override void OnCalculate(int bar, decimal value)
    {
        while (_raw.Count <= bar) _raw.Add(double.NaN);
        var c = GetCandle(bar);
        decimal tick = InstrumentInfo?.TickSize > 0 ? InstrumentInfo.TickSize : 0.01m;
        double from = BookDepth.Steps[Math.Clamp(_d1, 0, 3)], to = BookDepth.Steps[Math.Clamp(Math.Max(_d2, _d1 + 1), 1, 4)];
        if (BookDepth.Between(GetDepthSamples(bar), (double)c.Close, tick, from, to) is { } d && d.Bid + d.Ask > 0) _raw[bar] = (d.Bid - d.Ask) / (d.Bid + d.Ask) * 100;
        double sum = 0;
        int n = 0;
        for (int k = Math.Max(0, bar - _smooth + 1); k <= bar; k++)
            if (!double.IsNaN(_raw[k]))
            {
                sum += _raw[k];
                n++;
            }

        if (n == 0) return;
        double v = sum / n;
        _osc[bar] = (decimal)v;
        bool extreme = Highlight && (v >= (double)BidThreshold || v <= -(double)AskThreshold);
        var basec = v >= 0 ? BidColor : AskColor;
        _osc.Colors[bar] = extreme ? ExtremeColor : _style == PlotStyle.StrengthBars ? Pal.A(basec, (int)(60 + 195 * Math.Min(1, Math.Abs(v) / 100))) : basec;
    }
}

/// <summary>La presión del libro alrededor del precio: franjas a 1, 2,5, 5 y 10 % pintadas según qué lado pesa más.</summary>
[DisplayName("Presión del libro de órdenes")]
[Category(IndicatorCategories.OrderBook)]
[Description("Alrededor del precio de cada vela, franjas hasta el 1 %, 2,5 %, 5 % y 10 %: abajo pintadas por cuánto pesan las compras del libro y arriba por cuánto pesan las ventas, solo cuando un lado pasa su proporción mínima. Los extremos se resaltan.")]
public class OrderBookPressure : Indicator
{
    [Display(Name = "Compras: proporción mínima (%)", GroupName = "Cálculo", Order = 10)] [Range(0, 100)] public decimal MinBidRatio { get; set; } = 20;
    [Display(Name = "Ventas: proporción mínima (%)", GroupName = "Cálculo", Order = 11)] [Range(0, 100)] public decimal MinAskRatio { get; set; } = 20;
    [Display(Name = "Intensidad", GroupName = "Dibujo", Order = 20)] [Range(0.1, 10)] public decimal Scale { get; set; } = 1;
    [Display(Name = "Resaltar los extremos", GroupName = "Dibujo", Order = 21)] public bool Highlight { get; set; } = true;
    [Display(Name = "Líneas de referencia", GroupName = "Dibujo", Order = 22)] public bool ShowReference { get; set; } = true;
    [Display(Name = "Compras", GroupName = "Colores", Order = 30)] public Color BidColor { get; set; } = Color.FromArgb(255, 165, 214, 167);
    [Display(Name = "Ventas", GroupName = "Colores", Order = 31)] public Color AskColor { get; set; } = Color.FromArgb(255, 241, 98, 126);
    [Display(Name = "Extremos", GroupName = "Colores", Order = 32)] public Color ExtremeColor { get; set; } = Color.FromArgb(255, 255, 255, 0);

    public OrderBookPressure() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Historical);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar == 0) return;
        decimal tick = InstrumentInfo?.TickSize > 0 ? InstrumentInfo.TickSize : 0.01m;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        int bw = Math.Max(1, ChartInfo.PriceChartContainer.BarsWidth);
        for (int bar = first; bar <= last; bar++)
        {
            var samples = GetDepthSamples(bar);
            if (samples.Count == 0) continue;
            double px = (double)GetCandle(bar).Close;
            int x = ChartInfo.GetXByBar(bar);
            for (int k = 1; k < BookDepth.Steps.Length; k++)
            {
                if (BookDepth.Between(samples, px, tick, BookDepth.Steps[k - 1], BookDepth.Steps[k]) is not { } d || d.Bid + d.Ask <= 0) continue;
                double bidShare = d.Bid / (d.Bid + d.Ask) * 100, askShare = 100 - bidShare;
                double imb = (d.Bid - d.Ask) / (d.Bid + d.Ask);
                int a = (int)Math.Clamp(Math.Abs(imb) * 200 * (double)Scale, 0, 160);
                bool extreme = Highlight && Math.Abs(imb) >= 0.6;
                double lo = px * (1 - BookDepth.Steps[k] / 100), hi = px * (1 - BookDepth.Steps[k - 1] / 100);
                if (imb > 0 && bidShare >= (double)MinBidRatio)
                {
                    int y0 = ChartInfo.GetYByPrice((decimal)hi, false), y1 = ChartInfo.GetYByPrice((decimal)lo, false);
                    ctx.FillRectangle(Pal.A(extreme ? ExtremeColor : BidColor, a), new Rectangle(x, Math.Min(y0, y1), bw + 1, Math.Max(1, Math.Abs(y1 - y0))));
                }

                lo = px * (1 + BookDepth.Steps[k - 1] / 100);
                hi = px * (1 + BookDepth.Steps[k] / 100);
                if (imb < 0 && askShare >= (double)MinAskRatio)
                {
                    int y0 = ChartInfo.GetYByPrice((decimal)hi, false), y1 = ChartInfo.GetYByPrice((decimal)lo, false);
                    ctx.FillRectangle(Pal.A(extreme ? ExtremeColor : AskColor, a), new Rectangle(x, Math.Min(y0, y1), bw + 1, Math.Max(1, Math.Abs(y1 - y0))));
                }
            }
        }

        if (!ShowReference) return;
        double p = (double)GetCandle(CurrentBar - 1).Close;
        var pen = new RenderPen(Color.FromArgb(70, 150, 150, 160), 1, System.Drawing.Drawing2D.DashStyle.Dot);
        var r = ChartInfo.PriceChartContainer.Region;
        foreach (double s in BookDepth.Steps.Skip(1))
            foreach (int sign in new[] { -1, 1 })
            {
                int y = ChartInfo.GetYByPrice((decimal)(p * (1 + sign * s / 100)), false);
                ctx.DrawLine(pen, r.Left, y, r.Right, y);
            }
    }
}

/// <summary>Los precios de liquidación de un largo y un corto a 5, 10, 20, 50 y 100× desde el precio de cada vela.</summary>
[DisplayName("Bandas de liquidación por apalancamiento")]
[Category(IndicatorCategories.Derivatives)]
[Description("Con el precio de cada vela, dónde se liquidaría un largo (abajo) y un corto (arriba) abierto ahí a 5, 10, 20, 50 y 100×. Las bandas entre apalancamientos se rellenan.")]
public class LiquidationBands : Indicator
{
    private readonly int[] _lev = { 5, 10, 20, 50, 100 };
    private readonly ValueDataSeries[] _long = new ValueDataSeries[5];
    private readonly ValueDataSeries[] _short = new ValueDataSeries[5];
    private bool _close = true, _longs = true, _shorts = true, _lines;

    public LiquidationBands() : base(true)
    {
        for (int k = 0; k < 5; k++)
        {
            _long[k] = new ValueDataSeries("L" + k, "Largos " + _lev[k] + "×") { Color = Pal.A(Pal.Bull, 140), ShowZeroValue = false };
            _short[k] = new ValueDataSeries("S" + k, "Cortos " + _lev[k] + "×") { Color = Pal.A(Pal.Bear, 140), ShowZeroValue = false };
            if (k == 0) DataSeries[0] = _long[k];
            else DataSeries.Add(_long[k]);
            DataSeries.Add(_short[k]);
        }

        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Historical);
        Apply();
    }

    private void Apply()
    {
        for (int k = 0; k < 5; k++)
        {
            _long[k].VisualType = _longs && _lines ? VisualMode.Line : VisualMode.Hide;
            _short[k].VisualType = _shorts && _lines ? VisualMode.Line : VisualMode.Hide;
        }
    }

    [Display(Name = "Con el cierre (si no, HLC3)", GroupName = "Cálculo", Order = 10)] public bool UseClose { get => _close; set { _close = value; RecalculateValues(); } }
    [Display(Name = "Apalancamiento 1", GroupName = "Cálculo", Order = 11)] [Range(1, 500)] public int Lev1 { get => _lev[0]; set { _lev[0] = value; RecalculateValues(); } }
    [Display(Name = "Apalancamiento 2", GroupName = "Cálculo", Order = 12)] [Range(1, 500)] public int Lev2 { get => _lev[1]; set { _lev[1] = value; RecalculateValues(); } }
    [Display(Name = "Apalancamiento 3", GroupName = "Cálculo", Order = 13)] [Range(1, 500)] public int Lev3 { get => _lev[2]; set { _lev[2] = value; RecalculateValues(); } }
    [Display(Name = "Apalancamiento 4", GroupName = "Cálculo", Order = 14)] [Range(1, 500)] public int Lev4 { get => _lev[3]; set { _lev[3] = value; RecalculateValues(); } }
    [Display(Name = "Apalancamiento 5", GroupName = "Cálculo", Order = 15)] [Range(1, 500)] public int Lev5 { get => _lev[4]; set { _lev[4] = value; RecalculateValues(); } }
    [Display(Name = "Bandas de largos (abajo)", GroupName = "Dibujo", Order = 20)] public bool ShowLongs { get => _longs; set { _longs = value; Apply(); } }
    [Display(Name = "Bandas de cortos (arriba)", GroupName = "Dibujo", Order = 21)] public bool ShowShorts { get => _shorts; set { _shorts = value; Apply(); } }
    [Display(Name = "Líneas de las bandas", GroupName = "Dibujo", Order = 22)] public bool ShowLines { get => _lines; set { _lines = value; Apply(); } }
    [Display(Name = "Rellenar", GroupName = "Dibujo", Order = 23)] public bool Fill { get; set; } = true;
    [Display(Name = "Opacidad del relleno (%)", GroupName = "Dibujo", Order = 24)] [Range(0, 100)] public int FillOpacity { get; set; } = 10;

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        decimal px = _close ? c.Close : (c.High + c.Low + c.Close) / 3;
        for (int k = 0; k < 5; k++)
        {
            decimal m = 1m / Math.Max(1, _lev[k]);
            _long[k][bar] = px * (1 - m);
            _short[k][bar] = px * (1 + m);
        }
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (!Fill || ChartInfo == null) return;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        if (last <= first) return;
        // de la menos apalancada (más lejos) a la más (más cerca): bandas entre una y la siguiente
        var order = Enumerable.Range(0, 5).OrderBy(k => _lev[k]).ToArray();
        for (int j = 1; j < 5; j++)
        {
            foreach (var (series, on, color) in new[] { (_long, _longs, Pal.Bull), (_short, _shorts, Pal.Bear) })
            {
                if (!on) continue;
                var a = series[order[j - 1]];
                var b = series[order[j]];
                var top = new List<Point>();
                var bot = new List<Point>();
                for (int i = first; i <= last; i++)
                {
                    int x = ChartInfo.GetXByBar(i, false);
                    top.Add(new Point(x, ChartInfo.GetYByPrice(a[i], false)));
                    bot.Add(new Point(x, ChartInfo.GetYByPrice(b[i], false)));
                }

                bot.Reverse();
                ctx.FillPolygon(Pal.A(color, (int)(255 * FillOpacity / 100.0 * (0.5 + j / 8.0))), top.Concat(bot).ToArray());
            }
        }
    }
}

/// <summary>Mapa estimado de liquidaciones: dónde se liquidarían las posiciones abiertas en la ventana (del OI y el volumen).</summary>
[DisplayName("Mapa de liquidaciones (estimado con el OI)")]
[Category(IndicatorCategories.Derivatives)]
[Description("Una estimación de dónde están los precios de liquidación de las posiciones abiertas en las últimas N velas: el OI que entró en cada vela se reparte entre largos y cortos según quién compró y quién vendió, y en varios apalancamientos. Se dibuja como barras pegadas al eje (cortos arriba, largos abajo) y se nombran los tres grupos más grandes. Lo que el precio ya atravesó desaparece. Es un modelo, no el dato del exchange.")]
public class LiquidationMap : SceneIndicator
{
    private readonly DerivFeed _deriv;
    private double[] _rows = Array.Empty<double>();
    private double _lo, _step, _price;

    [Display(Name = "Velas de posiciones abiertas", GroupName = "Cálculo", Order = 10)] [Range(20, 5000)] public int Lookback { get; set; } = 672;
    [Display(Name = "Margen de mantenimiento (%)", GroupName = "Cálculo", Order = 11)] [Range(0, 5)] public decimal MaintMargin { get; set; } = 0.5m;
    [Display(Name = "Rango del perfil (% a cada lado)", GroupName = "Cálculo", Order = 12)] [Range(1, 50)] public decimal RangePct { get; set; } = 12;
    [Display(Name = "Filas", GroupName = "Cálculo", Order = 13)] [Range(20, 500)] public int Rows { get; set; } = 160;
    [Display(Name = "Líneas de los grupos más grandes (por lado)", GroupName = "Dibujo", Order = 20)] [Range(0, 10)] public int ClusterLines { get; set; }
    [Display(Name = "Ancho máximo (px)", GroupName = "Dibujo", Order = 21)] [Range(30, 600)] public int Width { get; set; } = 220;

    private static readonly (double Lev, double Weight)[] Tiers = { (5, 0.1), (10, 0.25), (25, 0.3), (50, 0.2), (100, 0.15) };

    public LiquidationMap()
    {
        _deriv = new DerivFeed(a => DoActionInGuiThread(a), RecalculateValues, oi: true);
    }

    protected override void OnDispose()
    {
        base.OnDispose();
        _deriv.Dispose();
    }

    protected override void Build(Scene s)
    {
        int n = Bars.Count;
        if (n < 10) return;
        int from = Math.Max(1, n - Lookback);
        _deriv.Ensure(InstrumentInfo?.Instrument ?? "", TimeOf(from), BarLength);
        _price = Bars[^1].C;
        double range = _price * (double)RangePct / 100;
        _lo = _price - range;
        _step = 2 * range / Rows;
        _rows = new double[Rows];
        if (!_deriv.HasOi)
        {
            s.DashTitle = "Mapa de liquidaciones";
            s.Row("Interés abierto", _deriv.Error ?? "trayendo…");
            return;
        }

        double mm = (double)MaintMargin / 100;
        // de la vela de entrada hasta ahora: el mínimo y el máximo que tocó (lo atravesado ya se liquidó)
        var minAfter = new double[n];
        var maxAfter = new double[n];
        double mn = double.MaxValue, mx = double.MinValue;
        for (int i = n - 1; i >= 0; i--)
        {
            minAfter[i] = mn;
            maxAfter[i] = mx;
            mn = Math.Min(mn, Bars[i].L);
            mx = Math.Max(mx, Bars[i].H);
        }

        for (int i = from; i < n; i++)
        {
            var t0 = TimeOf(i);
            var t1 = i + 1 < n ? TimeOf(i + 1) : DateTime.UtcNow;
            if (_deriv.OiAt(t1) is not { } a || _deriv.OiAt(t0) is not { } b) continue;
            double opened = a - b;
            if (opened <= 0) continue;
            var c = GetCandle(i + Offset);
            double buyShare = c.Volume > 0 ? (double)((c.Volume + c.Delta) / 2 / c.Volume) : 0.5;
            double entry = (Bars[i].H + Bars[i].L + Bars[i].C) / 3;
            foreach (var (lev, w) in Tiers)
            {
                double longLiq = entry * (1 - 1 / lev + mm), shortLiq = entry * (1 + 1 / lev - mm);
                double usdL = opened * buyShare * w * entry, usdS = opened * (1 - buyShare) * w * entry;
                if (minAfter[i] > longLiq) Add(longLiq, usdL);
                if (maxAfter[i] < shortLiq) Add(shortLiq, usdS);
            }
        }

        void Add(double price, double usd)
        {
            int k = (int)((price - _lo) / _step);
            if (k >= 0 && k < Rows) _rows[k] += usd;
        }

        double max = _rows.Max();
        if (max <= 0) return;
        for (int k = 0; k < Rows; k++)
            if (_rows[k] < max * 0.1) _rows[k] = 0;

        // grupos: filas seguidas de un mismo lado
        var clusters = new List<(int A, int B, double Usd, bool Above)>();
        for (int k = 0; k < Rows; k++)
        {
            if (_rows[k] <= 0) continue;
            bool above = _lo + (k + 0.5) * _step > _price;
            if (clusters.Count > 0 && clusters[^1].B == k - 1 && clusters[^1].Above == above) clusters[^1] = (clusters[^1].A, k, clusters[^1].Usd + _rows[k], above);
            else clusters.Add((k, k, _rows[k], above));
        }

        foreach (var cl in clusters.OrderByDescending(c => Enumerable.Range(c.A, c.B - c.A + 1).Max(k => _rows[k])).Take(3))
        {
            double p = _lo + ((cl.A + cl.B) / 2.0 + 0.5) * _step;
            s.Tag(LastBar, p, "$" + VpUltimate.Vp.Short((decimal)cl.Usd) + " liq", Color.Transparent, cl.Above ? Pal.Bull : Pal.Bear);
        }

        foreach (bool above in new[] { true, false })
        {
            var rows = Enumerable.Range(0, Rows).Where(k => _rows[k] > 0 && (_lo + (k + 0.5) * _step > _price) == above).OrderByDescending(k => _rows[k]).ToList();
            var used = new List<double>();
            foreach (var k in rows)
            {
                if (used.Count >= ClusterLines) break;
                double p = _lo + (k + 0.5) * _step;
                if (used.Any(u => Math.Abs(u - p) / _price < 0.005)) continue;
                used.Add(p);
                s.HLine(Math.Max(0, LastBar - 200), -1, p, Pal.A(above ? Pal.Bull : Pal.Bear, 150), 1, LineDashStyle.Dot, $"{Price(p)}  ${VpUltimate.Vp.Short((decimal)_rows[k])}", "Grupo de liquidaciones");
            }
        }

        double near5 = _price * 0.05;
        double up5 = 0, dn5 = 0;
        for (int k = 0; k < Rows; k++)
        {
            double p = _lo + (k + 0.5) * _step;
            if (p > _price && p - _price <= near5) up5 += _rows[k];
            if (p < _price && _price - p <= near5) dn5 += _rows[k];
        }

        s.DashTitle = "Mapa de liquidaciones (estimado)";
        s.Row("Cortos a menos del 5 %", "$" + VpUltimate.Vp.Short((decimal)up5), Pal.Bull);
        s.Row("Largos a menos del 5 %", "$" + VpUltimate.Vp.Short((decimal)dn5), Pal.Bear);
    }

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (_rows.Length == 0) return;
        double max = _rows.Max();
        if (max <= 0) return;
        int right = region.Right - 2;
        for (int k = 0; k < _rows.Length; k++)
        {
            if (_rows[k] <= 0) continue;
            double p = _lo + k * _step;
            int y0 = Y(p + _step), y1 = Y(p);
            int w = (int)(Width * _rows[k] / max);
            bool above = p + _step / 2 > _price;
            ctx.FillRectangle(Pal.A(above ? Pal.Bull : Pal.Bear, 170), new Rectangle(right - w, Math.Min(y0, y1), Math.Max(1, w), Math.Max(1, Math.Abs(y1 - y0))));
        }
    }
}

/// <summary>Liquidaciones de largos y cortos por vela (USD) y los estallidos que pasan la norma.</summary>
[DisplayName("Estallidos de liquidaciones")]
[Category(IndicatorCategories.Derivatives)]
[Description("Las liquidaciones de largos (ventas forzadas) y de cortos (compras forzadas) de cada vela en dólares, una columna apilada por vela. Cuando una vela salta sobre la norma (desvíos sobre la media de la ventana) se marca el estallido con su tamaño. Las liquidaciones llegan en vivo y se graban (hay desde que el gráfico estuvo abierto).")]
public class LiquidationBursts : Indicator
{
    private readonly ValueDataSeries _long = new("LL", "Largos liquidados") { VisualType = VisualMode.Histogram, Color = Pal.A(Pal.Bear, 220), ShowZeroValue = false };
    private readonly ValueDataSeries _short = new("SL", "Cortos liquidados") { VisualType = VisualMode.Histogram, Color = Pal.A(Pal.Bull, 220), ShowZeroValue = false };
    private readonly DerivFeed _deriv;
    private readonly Action _tick;
    private long _gen = -1;

    public LiquidationBursts() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _short;
        DataSeries.Add(_long);
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _deriv = new DerivFeed(a => DoActionInGuiThread(a), RecalculateValues, oi: false, liquidations: true);
        _tick = () =>
        {
            if (_deriv.LiquidationGeneration != _gen)
            {
                _gen = _deriv.LiquidationGeneration;
                RecalculateValues();
            }
        };
    }

    [Display(Name = "Estallido desde (desvíos)", GroupName = "Cálculo", Order = 10)] [Range(0.5, 20)] public decimal BurstZ { get; set; } = 3;
    [Display(Name = "Ventana (velas)", GroupName = "Cálculo", Order = 11)] [Range(5, 2000)] public int Window { get; set; } = 96;
    [Display(Name = "Unidad de la burbuja (USD)", GroupName = "Dibujo", Order = 20)] [Range(1000, 1e10)] public decimal BubbleUnit { get; set; } = 1_000_000;

    protected override void OnInitialize() => SubscribeToTimer(TimeSpan.FromSeconds(3), _tick);

    protected override void OnDispose()
    {
        UnsubscribeFromTimer(TimeSpan.FromSeconds(3), _tick);
        _deriv.Dispose();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0 && CurrentBar > 0) _deriv.Ensure(InstrumentInfo?.Instrument ?? "", GetCandle(0).Time, ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.FromMinutes(15));
        var c = GetCandle(bar);
        var end = bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : DateTime.UtcNow;
        var (l, s) = _deriv.Liquidations(c.Time, end);
        // apiladas: los largos abajo y los cortos encima (la serie de cortos es el total)
        _long[bar] = (decimal)(l * (double)c.Close);
        _short[bar] = (decimal)((l + s) * (double)c.Close);
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container is not { } box) return;
        int first = Math.Max(Window, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        var font = new RenderFont("Arial", 7, FontStyle.Bold);
        if (_short[Math.Max(0, CurrentBar - 1)] == 0 && _short.ToArrayLast(Window).All(v => v == 0))
        {
            ctx.DrawString(UiText.Tr("Las liquidaciones se graban desde que el gráfico está abierto."), new RenderFont("Arial", 8), FlowText.Dim, box.Region.Left + 8, box.Region.Top + 16);
            return;
        }

        for (int bar = first; bar <= last; bar++)
        {
            double sum = 0, sq = 0;
            for (int k = bar - Window; k < bar; k++)
            {
                double v = (double)_short[k];
                sum += v;
                sq += v * v;
            }

            double mean = sum / Window, sd = Math.Sqrt(Math.Max(0, sq / Window - mean * mean));
            double total = (double)_short[bar];
            if (sd <= 0 || (total - mean) / sd < (double)BurstZ) continue;
            double longs = (double)_long[bar];
            bool flush = longs >= total - longs;
            int x = ChartInfo.GetXByBar(bar, false), y = box.GetYByValue(_short[bar]) - 8;
            int r = (int)Math.Clamp(5 + 3 * Math.Sqrt(total / (double)BubbleUnit), 5, 22);
            ctx.FillEllipse(Pal.A(flush ? Pal.Bear : Pal.Bull, 170), new Rectangle(x - r, y - r, 2 * r, 2 * r));
            ctx.DrawString("$" + VpUltimate.Vp.Short((decimal)total), font, Color.White, x - r, y - 5);
        }
    }
}

internal static class SeriesExt
{
    public static IEnumerable<decimal> ToArrayLast(this ValueDataSeries s, int n)
    {
        int count = s.Count;
        for (int i = Math.Max(0, count - n); i < count; i++) yield return s[i];
    }
}
