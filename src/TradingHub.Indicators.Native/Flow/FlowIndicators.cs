using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Feeds;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Flow;

/// <summary>Lo que se operó en una vela, por mercado de origen y por tamaño de operación (en dólares).</summary>
internal sealed class FlowBar
{
    public const int Sources = 16;
    public const int Buckets = 8;

    /// <summary>Límites de los tamaños (USD): menos de 25k, 25-50k, 50-100k, 100-250k, 250-500k, 500k-1M, 1-5M, más de 5M.</summary>
    public static readonly double[] Limits = { 25_000, 50_000, 100_000, 250_000, 500_000, 1_000_000, 5_000_000 };

    public readonly double[] BuyUsd = new double[Sources], SellUsd = new double[Sources];
    public readonly double[] SizeBuy = new double[Buckets], SizeSell = new double[Buckets];
    public int Trades, Buys, Sells;
    public double BuyCoins, SellCoins, MaxTradeUsd;

    /// <summary>Lo operado en la vela que no se sabe de qué mercado vino (historia resumida o armada con velas).</summary>
    public double UnknownBuy, UnknownSell;

    public static int Bucket(double usd)
    {
        for (int i = 0; i < Limits.Length; i++)
            if (usd < Limits[i]) return i;
        return Buckets - 1;
    }

    public double Net(int src) => BuyUsd[src] - SellUsd[src];
    public double Gross(int src) => BuyUsd[src] + SellUsd[src];
}

/// <summary>Las operaciones de cada vela resumidas una vez (las cerradas quedan guardadas; la en formación se rehace).</summary>
internal sealed class FlowCache
{
    private readonly Dictionary<long, (int Count, FlowBar Bar)> _bars = new();
    private (long Key, int Count, DateTime At, FlowBar Bar) _live;

    public void Clear()
    {
        _bars.Clear();
        _live = default;
    }

    public FlowBar Get(Indicator ind, int bar)
    {
        var c = ind.GetCandle(bar);
        long key = c.Time.Ticks;
        int count = (int)c.Ticks;
        bool closed = bar < ind.CurrentBar - 1;
        if (closed && _bars.TryGetValue(key, out var hit) && hit.Count == count) return hit.Bar;
        if (!closed && _live.Bar != null && _live.Key == key && (_live.Count == count || DateTime.UtcNow - _live.At < TimeSpan.FromSeconds(1))) return _live.Bar;
        var f = Build(ind, bar, c);
        if (closed) _bars[key] = (count, f);
        else _live = (key, count, DateTime.UtcNow, f);
        return f;
    }

    private static FlowBar Build(Indicator ind, int bar, IndicatorCandle c)
    {
        var f = new FlowBar();
        double knownBuy = 0, knownSell = 0;
        foreach (var t in ind.GetTrades(bar))
        {
            double v = (double)t.Volume, usd = v * (double)t.Price;
            f.Trades++;
            int b = FlowBar.Bucket(usd);
            if (usd > f.MaxTradeUsd) f.MaxTradeUsd = usd;
            if (t.IsBuy)
            {
                f.Buys++;
                f.BuyCoins += v;
                f.SizeBuy[b] += usd;
                if (t.Source is >= 0 and < FlowBar.Sources) f.BuyUsd[t.Source] += usd;
                else f.UnknownBuy += usd;
                knownBuy += v;
            }
            else
            {
                f.Sells++;
                f.SellCoins += v;
                f.SizeSell[b] += usd;
                if (t.Source is >= 0 and < FlowBar.Sources) f.SellUsd[t.Source] += usd;
                else f.UnknownSell += usd;
                knownSell += v;
            }
        }

        // velas armadas con velas del exchange (sin operaciones): el volumen y el delta de la vela, sin mercado
        if (f.Trades == 0 && c.Volume > 0)
        {
            double px = (double)c.Close, vol = (double)c.Volume, delta = (double)c.Delta;
            f.BuyCoins = (vol + delta) / 2;
            f.SellCoins = (vol - delta) / 2;
            f.UnknownBuy = f.BuyCoins * px;
            f.UnknownSell = f.SellCoins * px;
        }

        return f;
    }
}

internal static class FlowText
{
    public static string Usd(double v)
    {
        double a = Math.Abs(v);
        string s = a >= 1e9 ? (a / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B"
            : a >= 1e6 ? (a / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M"
            : a >= 1e3 ? (a / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "k"
            : a.ToString("0", CultureInfo.InvariantCulture);
        return (v < 0 ? "−" : "") + "$" + s;
    }

    public static string Signed(double v) => (v > 0 ? "+" : "") + Usd(v);

    public static string Num(double v)
    {
        double a = Math.Abs(v);
        string s = a >= 1e6 ? (a / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M"
            : a >= 1e3 ? (a / 1e3).ToString("0.##", CultureInfo.InvariantCulture) + "k"
            : a >= 10 ? a.ToString("0.#", CultureInfo.InvariantCulture)
            : a.ToString("0.###", CultureInfo.InvariantCulture);
        return (v < 0 ? "−" : "") + s;
    }

    public static readonly Color Spot = Color.FromArgb(255, 86, 160, 255);
    public static readonly Color Perp = Color.FromArgb(255, 242, 170, 58);
    public static readonly Color Buy = Color.FromArgb(255, 38, 194, 129);
    public static readonly Color Sell = Color.FromArgb(255, 240, 82, 90);
    public static readonly Color Dim = Color.FromArgb(200, 170, 176, 188);
    public static readonly Color Back = Color.FromArgb(200, 14, 17, 24);
}

/// <summary>
/// La columna de flujo de LiquidityMapperFast en un panel: arriba la fila de TODOS (spot y perpetuos por separado), debajo
/// una fila por exchange ordenada por volumen en la ventana elegida, cada una con su propia escala y el flujo neto agresor
/// (compras − ventas a mercado, en dólares) acumulado desde el borde izquierdo. Las filas de lámparas dicen qué lado va
/// ganando en los últimos 1, 5, 15 y 60 minutos y en cada tamaño de operación. Spot es azul y perpetuo ámbar: el color dice
/// qué mercado, nunca compra o venta. Funciona con la conexión «Agregado» (un exchange por fila).
/// </summary>
[DisplayName("Flujo por exchange (ranking, spot / perpetuos)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("La columna de flujo de LiquidityMapperFast: flujo agresor acumulado de cada exchange (ordenados por volumen), spot y perpetuos por separado, la fila de todos y lámparas de 1/5/15/60 min y por tamaño de operación.")]
public class FlowByExchange : Indicator
{
    public enum RankWindow
    {
        [Display(Name = "Lo visible")] Visible,
        [Display(Name = "15 minutos")] Min15,
        [Display(Name = "1 hora")] Hour1,
        [Display(Name = "24 horas")] Hour24,
    }

    public enum RowHeights
    {
        [Display(Name = "Proporción áurea (cada una 1/φ de la de arriba)")] Golden,
        [Display(Name = "Según la parte del volumen")] Share,
        [Display(Name = "Iguales")] Equal,
    }

    public enum MarketFilter
    {
        [Display(Name = "Spot y perpetuos")] Both,
        [Display(Name = "Solo spot")] Spot,
        [Display(Name = "Solo perpetuos")] Perpetual,
    }

    private readonly FlowCache _cache = new();
    private readonly ValueDataSeries _anchor = new("FLOW", "Flujo") { VisualType = VisualMode.Hide, ShowZeroValue = false };

    public FlowByExchange() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _anchor;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Ordenar por el volumen de", GroupName = "Cálculo", Order = 10)]
    public RankWindow Ranking { get; set; } = RankWindow.Hour1;

    [Display(Name = "Mercados", GroupName = "Cálculo", Order = 15)]
    public MarketFilter Markets { get; set; } = MarketFilter.Both;

    [Display(Name = "Filas de exchanges (como mucho)", GroupName = "Visualización", Order = 20)]
    [Range(1, 16)]
    public int MaxRows { get; set; } = 8;

    [Display(Name = "Alto de las filas", GroupName = "Visualización", Order = 25)]
    public RowHeights Heights { get; set; } = RowHeights.Golden;

    [Display(Name = "Lámparas de flujo y de tamaños arriba", GroupName = "Visualización", Order = 30)]
    public bool ShowDots { get; set; } = true;

    [Display(Name = "Acumulado desde la primera vela (si no, desde el borde izquierdo)", GroupName = "Visualización", Order = 35)]
    public bool SinceStart { get; set; }

    [Display(Name = "Aviso de spot y perpetuos en contra desde (USD)", GroupName = "Cálculo", Order = 40)]
    public decimal DivergenceUsd { get; set; } = 2_000_000;

    [Display(Name = "Ancho de las etiquetas (px)", GroupName = "Visualización", Order = 45)]
    [Range(80, 400)]
    public int Gutter { get; set; } = 170;

    protected override void OnRecalculate() => _cache.Clear();

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    private bool InFilter(bool spot) => Markets switch { MarketFilter.Spot => spot, MarketFilter.Perpetual => !spot, _ => true };

    /// <summary>Primera vela de la ventana de ranking (la ventana termina en la última vela).</summary>
    private int RankStart(int first, int last)
    {
        if (Ranking == RankWindow.Visible) return first;
        var span = Ranking switch { RankWindow.Min15 => TimeSpan.FromMinutes(15), RankWindow.Hour1 => TimeSpan.FromHours(1), _ => TimeSpan.FromHours(24) };
        var end = GetCandle(CurrentBar - 1).Time + (ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.Zero);
        int b = CurrentBar - 1;
        while (b > 0 && GetCandle(b - 1).Time >= end - span) b--;
        return Math.Min(b, last);
    }

    private sealed class Row
    {
        public string Name = "";
        public readonly List<int> Spot = new(), Perp = new();
        public double Gross, Net, NetSpot, NetPerp;
        public bool HasSpot, HasPerp;
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container == null || CurrentBar <= 1) return;
        var r = Container.Region;
        var pc = ChartInfo.PriceChartContainer;
        int first = Math.Max(0, pc.FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, pc.LastVisibleBarNumber);
        if (last < first) return;
        var sources = InstrumentInfo?.Sources ?? Array.Empty<string>();
        var venues = InstrumentInfo?.SourceVenues ?? Array.Empty<string>();
        var spotFlags = InstrumentInfo?.SourceIsSpot ?? Array.Empty<bool>();
        int nsrc = Math.Max(1, Math.Min(FlowBar.Sources, sources.Length));
        var font = new RenderFont("Arial", 8);
        var bold = new RenderFont("Arial", 8, FontStyle.Bold);

        // exchanges: un exchange y su spot son una fila
        var rows = new Dictionary<string, Row>();
        for (int i = 0; i < nsrc; i++)
        {
            bool spot = i < spotFlags.Length && spotFlags[i];
            string v = i < venues.Length ? venues[i] : i < sources.Length ? sources[i] : "?";
            if (!rows.TryGetValue(v, out var row)) rows[v] = row = new Row { Name = v };
            (spot ? row.Spot : row.Perp).Add(i);
        }

        int rankFrom = RankStart(first, last);
        double total = 0;
        for (int b = rankFrom; b <= CurrentBar - 1; b++)
        {
            var f = _cache.Get(this, b);
            foreach (var row in rows.Values)
            {
                foreach (var i in row.Spot)
                {
                    if (!InFilter(true)) continue;
                    row.Gross += f.Gross(i);
                    row.NetSpot += f.Net(i);
                    if (f.Gross(i) > 0) row.HasSpot = true;
                }

                foreach (var i in row.Perp)
                {
                    if (!InFilter(false)) continue;
                    row.Gross += f.Gross(i);
                    row.NetPerp += f.Net(i);
                    if (f.Gross(i) > 0) row.HasPerp = true;
                }
            }
        }

        foreach (var row in rows.Values)
        {
            row.Net = row.NetSpot + row.NetPerp;
            total += row.Gross;
        }

        var ranked = rows.Values.Where(x => x.Gross > 0).OrderByDescending(x => x.Gross).ToList();
        var shown = ranked.Take(Math.Clamp(MaxRows, 1, 16)).ToList();
        int hidden = ranked.Count - shown.Count;

        // debajo del título del panel
        int y = r.Top + 15;
        context.FillRectangle(Color.FromArgb(60, 0, 0, 0), new Rectangle(r.Left, r.Top, r.Width, r.Height));
        if (ShowDots)
        {
            y = DrawDots(context, r, y, font, bold, nsrc, spotFlags);
            y += 2;
        }

        // filas: TODOS arriba, después los exchanges
        int avail = r.Bottom - y - (hidden > 0 ? 12 : 0);
        if (avail < 30) return;
        int aggH = Math.Max(28, (int)(avail * (shown.Count > 0 ? 0.26 : 1)));
        var heights = RowHeightsFor(shown, avail - aggH, total);
        string rankText = Ranking switch { RankWindow.Visible => UiText.Tr("lo visible"), RankWindow.Min15 => "15 min", RankWindow.Hour1 => "1 h", _ => "24 h" };

        // fila de todos
        {
            var spotIdx = Enumerable.Range(0, nsrc).Where(i => i < spotFlags.Length && spotFlags[i] && InFilter(true)).ToList();
            var perpIdx = Enumerable.Range(0, nsrc).Where(i => !(i < spotFlags.Length && spotFlags[i]) && InFilter(false)).ToList();
            double ns = ranked.Sum(x => x.NetSpot), np = ranked.Sum(x => x.NetPerp);
            string note = "";
            if ((double)DivergenceUsd > 0 && Math.Abs(ns) >= (double)DivergenceUsd && Math.Abs(np) >= (double)DivergenceUsd && Math.Sign(ns) != Math.Sign(np))
                note = string.Format(UiText.Tr("spot {0} y perpetuos {1}: van en contra"), ns > 0 ? UiText.Tr("compra") : UiText.Tr("vende"), np > 0 ? UiText.Tr("compran") : UiText.Tr("venden"));
            DrawRow(context, r, y, aggH, first, last, spotIdx, perpIdx, UiText.Tr("TODOS"), ns + np, 1, rankText, note, font, bold, true);
            y += aggH;
        }

        for (int k = 0; k < shown.Count; k++)
        {
            var row = shown[k];
            int h = heights[k];
            if (h < 10) break;
            DrawRow(context, r, y, h, first, last, InFilter(true) ? row.Spot : new List<int>(), InFilter(false) ? row.Perp : new List<int>(),
                $"#{k + 1} {row.Name.ToUpperInvariant()}", row.Net, total > 0 ? row.Gross / total : 0, rankText, "", font, bold, false);
            y += h;
        }

        if (hidden > 0)
            context.DrawString(string.Format(UiText.Tr("+{0} exchanges más (subí «Filas de exchanges»)"), hidden), font, FlowText.Dim, r.Left + 6, r.Bottom - 12);
        if (nsrc <= 1)
            context.DrawString(UiText.Tr("Un solo mercado: usá la conexión «Agregado» para ver una fila por exchange."), font, FlowText.Dim, r.Left + Gutter + 8, r.Top + 30);
    }

    private List<int> RowHeightsFor(List<Row> rows, int avail, double total)
    {
        var res = new List<int>();
        if (rows.Count == 0 || avail <= 0) return res;
        var w = new double[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            w[i] = Heights switch
            {
                RowHeights.Equal => 1,
                RowHeights.Share => total > 0 ? Math.Max(0.04, rows[i].Gross / total) : 1,
                _ => Math.Pow(0.618, i),
            };
        double sum = w.Sum();
        const int min = 16;
        foreach (var x in w) res.Add(Math.Max(min, (int)(avail * x / sum)));
        // si no entra, las de abajo se achican primero
        int over = res.Sum() - avail;
        for (int i = res.Count - 1; i >= 0 && over > 0; i--)
        {
            int cut = Math.Min(over, Math.Max(0, res[i] - min));
            res[i] -= cut;
            over -= cut;
        }

        return res;
    }

    private void DrawRow(RenderContext ctx, Rectangle r, int top, int h, int first, int last, List<int> spot, List<int> perp, string name, double net, double share,
        string rankText, string note, RenderFont font, RenderFont bold, bool aggregate)
    {
        var ci = ChartInfo!;
        ctx.DrawLine(new RenderPen(Color.FromArgb(70, 140, 150, 170)), r.Left, top, r.Right, top);
        // las dos líneas (spot y perpetuo), cada fila con su escala; arrancan en 0
        int n = last - first + 1;
        var lanes = new List<(Color Color, double[] Values)>();
        foreach (var (idx, color) in new[] { (spot, FlowText.Spot), (perp, FlowText.Perp) })
        {
            if (idx.Count == 0) continue;
            var vals = new double[n];
            double acc = 0;
            if (SinceStart)
                for (int b = 0; b < first; b++)
                {
                    var f0 = _cache.Get(this, b);
                    foreach (var i in idx) acc += f0.Net(i);
                }

            bool any = false;
            for (int b = first; b <= last; b++)
            {
                var f = _cache.Get(this, b);
                foreach (var i in idx)
                {
                    acc += f.Net(i);
                    if (f.Gross(i) > 0) any = true;
                }

                vals[b - first] = acc;
            }

            if (any) lanes.Add((color, vals));
        }

        double lo = 0, hi = 0;
        foreach (var (_, v) in lanes)
            foreach (var x in v)
            {
                lo = Math.Min(lo, x);
                hi = Math.Max(hi, x);
            }

        if (hi - lo < 1e-9) hi = lo + 1;
        int pad = 3, y0 = top + pad, y1 = top + h - pad;
        int Y(double v) => (int)Math.Round(y1 - (v - lo) / (hi - lo) * (y1 - y0));
        int zy = Y(0);
        ctx.DrawLine(new RenderPen(Color.FromArgb(60, 160, 160, 170), 1, Rendering.Settings.PenSettings.ToDash(LineDashStyle.Dash)), r.Left, zy, r.Right, zy);
        int half = Math.Max(0, ci.PriceChartContainer.BarsWidth / 2);
        foreach (var (color, v) in lanes)
        {
            var pts = new Point[n];
            for (int k = 0; k < n; k++) pts[k] = new Point(ci.GetXByBar(first + k) + half, Y(v[k]));
            if (pts.Length >= 2) ctx.DrawLines(new RenderPen(color, aggregate ? 1.6f : 1.2f), pts);
        }

        // etiqueta a la izquierda: rango y nombre, flujo neto de la ventana, parte del volumen
        int gw = Math.Clamp(Gutter, 80, 400);
        int lh = Math.Min(h - 2, aggregate ? 40 : 28);
        ctx.FillRectangle(FlowText.Back, new Rectangle(r.Left, top + 1, gw, lh));
        ctx.DrawString(name, bold, Color.FromArgb(235, 235, 240), r.Left + 5, top + 2);
        if (h >= 22)
        {
            string line = $"{FlowText.Signed(net)} · {rankText}" + (aggregate ? "" : $" · {share * 100:0}%");
            ctx.DrawString(line, font, net >= 0 ? FlowText.Buy : FlowText.Sell, r.Left + 5, top + 13);
        }

        if (h >= 32)
        {
            int lx = r.Left + 5;
            if (spot.Count > 0)
            {
                ctx.FillEllipse(FlowText.Spot, new Rectangle(lx, top + 27, 6, 6));
                ctx.DrawString("spot", font, FlowText.Dim, lx + 8, top + 24);
                lx += 40;
            }

            if (perp.Count > 0)
            {
                ctx.FillEllipse(FlowText.Perp, new Rectangle(lx, top + 27, 6, 6));
                ctx.DrawString(UiText.Tr("perp"), font, FlowText.Dim, lx + 8, top + 24);
            }
        }

        if (note.Length > 0) ctx.DrawString(note, bold, Color.FromArgb(255, 240, 200, 90), r.Left + gw + 8, top + 3);
    }

    /// <summary>Lámparas: flujo agresor en 1/5/15/60 min y lo operado por tamaño (compras desde la izquierda, ventas desde la derecha).</summary>
    private int DrawDots(RenderContext ctx, Rectangle r, int y, RenderFont font, RenderFont bold, int nsrc, bool[] spotFlags)
    {
        var lastTime = GetCandle(CurrentBar - 1).Time + (ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.Zero);
        var now = lastTime < DateTime.UtcNow ? lastTime : DateTime.UtcNow;
        int[] mins = { 1, 5, 15, 60 };
        var buy = new double[4];
        var sell = new double[4];
        var sizeBuy = new double[4];
        var sizeSell = new double[4];
        var covered = new double[4];
        // operaciones de la última hora (o de la ventana de ranking para los tamaños)
        int b = CurrentBar - 1;
        var since = now - TimeSpan.FromMinutes(60);
        while (b >= 0)
        {
            var c = GetCandle(b);
            var end = b + 1 < CurrentBar ? GetCandle(b + 1).Time : now;
            if (end <= since) break;
            foreach (var t in GetTrades(b))
            {
                if (t.Time < since) continue;
                if (t.Source >= 0 && t.Source < spotFlags.Length && !InFilter(spotFlags[t.Source])) continue;
                double usd = (double)(t.Volume * t.Price);
                double age = (now - t.Time).TotalMinutes;
                for (int k = 0; k < 4; k++)
                    if (age <= mins[k])
                    {
                        if (t.IsBuy) buy[k] += usd;
                        else sell[k] += usd;
                    }

                int band = usd < 25_000 ? 0 : usd < 100_000 ? 1 : usd < 500_000 ? 2 : 3;
                if (t.IsBuy) sizeBuy[band] += usd;
                else sizeSell[band] += usd;
            }

            b--;
        }

        _ = covered;
        int x = r.Left + 6;
        int rowH = 13;
        ctx.DrawString(UiText.Tr("FLUJO"), bold, FlowText.Dim, x, y);
        int gx = x + 44;
        for (int k = 0; k < 4; k++)
        {
            gx = DotGroup(ctx, gx, y, mins[k] + "m", buy[k], sell[k], font) + 10;
        }

        y += rowH;
        ctx.DrawString(UiText.Tr("TAMAÑO"), bold, FlowText.Dim, x, y);
        gx = x + 44;
        string[] bands = { "<25k", "25-100k", "100-500k", ">500k" };
        double all = sizeBuy.Sum() + sizeSell.Sum();
        for (int k = 0; k < 4; k++)
        {
            double part = all > 0 ? (sizeBuy[k] + sizeSell[k]) / all * 100 : 0;
            gx = DotGroup(ctx, gx, y, $"{bands[k]} {part:0}%", sizeBuy[k], sizeSell[k], font) + 10;
        }

        return y + rowH;
    }

    private static int DotGroup(RenderContext ctx, int x, int y, string label, double buy, double sell, RenderFont font)
    {
        var size = ctx.MeasureString(label, font);
        ctx.DrawString(label, font, FlowText.Dim, x, y);
        x += size.Width + 4;
        double tot = buy + sell;
        int lit = tot > 0 ? (int)Math.Round(buy / tot * 10) : 5;
        bool buyAhead = buy >= sell;
        for (int i = 0; i < 10; i++)
        {
            bool isBuy = i < lit;
            var col = tot <= 0 ? Color.FromArgb(90, 120, 120, 130) : isBuy ? FlowText.Buy : FlowText.Sell;
            if (tot > 0 && isBuy != buyAhead) col = Color.FromArgb(90, col.R, col.G, col.B);
            ctx.FillEllipse(col, new Rectangle(x + i * 7, y + 3, 5, 5));
        }

        x += 70;
        string pct = tot > 0 ? $"{buy / tot * 100:0}%" : "–";
        ctx.DrawString(pct, font, buyAhead ? FlowText.Buy : FlowText.Sell, x + 2, y);
        return x + ctx.MeasureString(pct, font).Width + 2;
    }
}

/// <summary>
/// Tira de estadísticas por vela, como la de LiquidityMapperFast: volumen, delta, CVD, delta %, compras y ventas, lo máximo
/// comprado y vendido en un precio, el POC, los desequilibrios diagonales y sus escaleras, cantidad de operaciones, compras,
/// ventas, operación promedio, delta y CVD de minoristas y de ballenas (por tamaño de operación), rango y cambio del interés
/// abierto. Cada celda se sombrea por magnitud (escala log entre el 2 y el 99 % de lo visible).
/// </summary>
[DisplayName("Estadísticas por vela (tira de LMF)")]
[Category(IndicatorCategories.OrderFlow)]
[Description("Tabla bajo el gráfico con una columna por vela: volumen, delta, CVD, compras/ventas, desequilibrios, operaciones, minoristas y ballenas, rango.")]
public class BarStatsStrip : Indicator
{
    public enum CellStyle
    {
        [Display(Name = "Celdas sombreadas por magnitud")] Filled,
        [Display(Name = "Solo texto coloreado")] Text,
    }

    private readonly FlowCache _cache = new();
    private readonly ValueDataSeries _anchor = new("STATS", "Estadísticas") { VisualType = VisualMode.Hide, ShowZeroValue = false };

    public BarStatsStrip() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _anchor;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _deriv = new DerivFeed(a => DoActionInGuiThread(a), RedrawChart, oi: true, funding: true, liquidations: true);
    }

    private readonly DerivFeed _deriv;

    protected override void OnDispose() => _deriv.Dispose();

    public enum StatUnit
    {
        [Display(Name = "Monedas")] Coin,
        [Display(Name = "Dólares")] Usd,
    }

    public enum CvdAnchor
    {
        [Display(Name = "Todo lo que se ve")] Visible,
        [Display(Name = "Cada día de trading")] Day,
    }

    // filas de la «Bar Statistics» de openmarket (fusionadas acá)
    [Display(Name = "Compras %", GroupName = "Filas · volumen", Order = 7)] public bool BuyPct { get; set; }
    [Display(Name = "Cambio del volumen %", GroupName = "Filas · volumen", Order = 8)] public bool VolChange { get; set; }
    [Display(Name = "Volumen relativo", GroupName = "Filas · volumen", Order = 9)] public bool Rvol { get; set; }
    [Display(Name = "Rango %", GroupName = "Filas · mercado", Order = 31)] public bool RangePct { get; set; }
    [Display(Name = "Distancia al VWAP %", GroupName = "Filas · mercado", Order = 32)] public bool VwapDist { get; set; }
    [Display(Name = "Liquidaciones", GroupName = "Filas · derivados", Order = 33)] public bool Liq { get; set; }
    [Display(Name = "Liquidaciones largos / cortos", GroupName = "Filas · derivados", Order = 34)] public bool LiqSplit { get; set; }
    [Display(Name = "Cambio del interés abierto", GroupName = "Filas · derivados", Order = 35)] public bool OiChange { get; set; }
    [Display(Name = "Financiación", GroupName = "Filas · derivados", Order = 36)] public bool Funding { get; set; }
    [Display(Name = "Libro neto (0,1 % – 2,5 %)", GroupName = "Filas · mercado", Order = 37)] public bool BookDepth { get; set; }
    [Display(Name = "Unidad (volumen, delta, CVD)", GroupName = "Opciones", Order = 39)] public StatUnit Units { get; set; } = StatUnit.Coin;
    [Display(Name = "CVD desde", GroupName = "Opciones", Order = 39)] public CvdAnchor CvdFrom { get; set; } = CvdAnchor.Visible;
    [Display(Name = "Volumen relativo: velas", GroupName = "Opciones", Order = 39)] [Range(2, 500)] public int RvolLength { get; set; } = 20;

    [Display(Name = "Volumen", GroupName = "Filas · volumen", Order = 1)] public bool Vol { get; set; } = true;
    [Display(Name = "Delta", GroupName = "Filas · volumen", Order = 2)] public bool Delta { get; set; } = true;
    [Display(Name = "CVD", GroupName = "Filas · volumen", Order = 3)] public bool Cvd { get; set; } = true;
    [Display(Name = "Delta %", GroupName = "Filas · volumen", Order = 4)] public bool DeltaPct { get; set; }
    [Display(Name = "Volumen comprador", GroupName = "Filas · volumen", Order = 5)] public bool BuyVol { get; set; }
    [Display(Name = "Volumen vendedor", GroupName = "Filas · volumen", Order = 6)] public bool SellVol { get; set; }
    [Display(Name = "Máximo comprado en un precio", GroupName = "Filas · footprint", Order = 10)] public bool MaxBuy { get; set; }
    [Display(Name = "Máximo vendido en un precio", GroupName = "Filas · footprint", Order = 11)] public bool MaxSell { get; set; }
    [Display(Name = "POC", GroupName = "Filas · footprint", Order = 12)] public bool Poc { get; set; }
    [Display(Name = "Desequilibrios (cantidad)", GroupName = "Filas · footprint", Order = 13)] public bool Imbalances { get; set; }
    [Display(Name = "Escaleras de desequilibrios", GroupName = "Filas · footprint", Order = 14)] public bool Stacked { get; set; }
    [Display(Name = "Operaciones", GroupName = "Filas · operaciones", Order = 20)] public bool Trades { get; set; }
    [Display(Name = "Compras", GroupName = "Filas · operaciones", Order = 21)] public bool Buys { get; set; }
    [Display(Name = "Ventas", GroupName = "Filas · operaciones", Order = 22)] public bool Sells { get; set; }
    [Display(Name = "Operación promedio", GroupName = "Filas · operaciones", Order = 23)] public bool AvgTrade { get; set; }
    [Display(Name = "Delta minoristas", GroupName = "Filas · operaciones", Order = 24)] public bool DeltaRetail { get; set; }
    [Display(Name = "Delta ballenas", GroupName = "Filas · operaciones", Order = 25)] public bool DeltaWhales { get; set; }
    [Display(Name = "CVD minoristas", GroupName = "Filas · operaciones", Order = 26)] public bool CvdRetail { get; set; }
    [Display(Name = "CVD ballenas", GroupName = "Filas · operaciones", Order = 27)] public bool CvdWhales { get; set; }
    [Display(Name = "Rango (máximo − mínimo)", GroupName = "Filas · mercado", Order = 30)] public bool Range { get; set; }

    [Display(Name = "Estilo de celda", GroupName = "Opciones", Order = 40)] public CellStyle Style { get; set; } = CellStyle.Filled;

    [Display(Name = "Desequilibrio: veces el lado contrario", GroupName = "Opciones", Order = 41)]
    public decimal ImbalanceRatio { get; set; } = 3;

    [Display(Name = "Desequilibrio: volumen mínimo", GroupName = "Opciones", Order = 42)]
    public decimal ImbalanceMin { get; set; }

    [Display(Name = "Escalera: filas seguidas", GroupName = "Opciones", Order = 43)]
    [Range(2, 10)]
    public int StackRows { get; set; } = 3;

    [Display(Name = "Minoristas: operaciones de hasta (USD)", GroupName = "Opciones", Order = 44)]
    public decimal RetailMax { get; set; } = 100_000;

    [Display(Name = "Ballenas: operaciones desde (USD)", GroupName = "Opciones", Order = 45)]
    public decimal WhaleMin { get; set; } = 500_000;

    protected override void OnRecalculate() => _cache.Clear();

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    private (int Imb, int Stack) Imbalance(IndicatorCandle c)
    {
        var lv = c.GetAllPriceLevels().OrderBy(l => l.Price).ToList();
        int imb = 0, stack = 0, runBuy = 0, runSell = 0;
        for (int i = 0; i < lv.Count; i++)
        {
            // compras de este precio contra las ventas un precio abajo; ventas contra las compras un precio arriba
            bool buy = i > 0 && lv[i].Ask >= ImbalanceMin && lv[i].Ask > 0 && lv[i].Ask >= ImbalanceRatio * Math.Max(lv[i - 1].Bid, 1e-12m);
            bool sell = i + 1 < lv.Count && lv[i].Bid >= ImbalanceMin && lv[i].Bid > 0 && lv[i].Bid >= ImbalanceRatio * Math.Max(lv[i + 1].Ask, 1e-12m);
            if (buy) imb++;
            if (sell) imb++;
            runBuy = buy ? runBuy + 1 : 0;
            runSell = sell ? runSell + 1 : 0;
            if (runBuy == StackRows) stack++;
            if (runSell == StackRows) stack++;
        }

        return (imb, stack);
    }

    private (double Retail, double Whales) SizeDelta(FlowBar f)
    {
        double retail = 0, whales = 0;
        for (int k = 0; k < FlowBar.Buckets; k++)
        {
            double lower = k == 0 ? 0 : FlowBar.Limits[k - 1];
            double upper = k < FlowBar.Limits.Length ? FlowBar.Limits[k] : double.MaxValue;
            double d = f.SizeBuy[k] - f.SizeSell[k];
            if (upper <= (double)RetailMax) retail += d;
            if (lower >= (double)WhaleMin) whales += d;
        }

        return (retail, whales);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container == null || CurrentBar <= 0) return;
        var r = Container.Region;
        var pc = ChartInfo.PriceChartContainer;
        int first = Math.Max(0, pc.FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, pc.LastVisibleBarNumber);
        if (last < first) return;
        var rows = new List<(string Name, Func<int, double?> Value, Func<double, string> Fmt, bool Signed)>();
        var cvd = new Dictionary<int, (double All, double Retail, double Whales)>();
        double a = 0, ar = 0, aw = 0;
        double U(int b) => Units == StatUnit.Usd ? (double)GetCandle(b).Close : 1;
        if (Liq || LiqSplit || OiChange || Funding) _deriv.Ensure(InstrumentInfo?.Instrument ?? "", GetCandle(first).Time.AddDays(-1), ChartInfo.GetTimeFrameTypeChartPeriod());
        for (int b = first; b <= last; b++)
        {
            var f = _cache.Get(this, b);
            var (rd, wd) = SizeDelta(f);
            if (CvdFrom == CvdAnchor.Day && b > first && IsNewSession(b)) a = 0;
            a += (double)GetCandle(b).Delta * U(b);
            ar += rd;
            aw += wd;
            cvd[b] = (a, ar, aw);
        }

        if (Vol) rows.Add(("vol", b => (double)GetCandle(b).Volume * U(b), FlowText.Num, false));
        if (Delta) rows.Add(("delta", b => (double)GetCandle(b).Delta * U(b), FlowText.Num, true));
        if (Cvd) rows.Add(("cvd", b => cvd[b].All, FlowText.Num, true));
        if (DeltaPct) rows.Add(("delta %", b => GetCandle(b).Volume > 0 ? (double)(GetCandle(b).Delta / GetCandle(b).Volume * 100) : null, v => v.ToString("0", CultureInfo.InvariantCulture) + "%", true));
        if (BuyVol) rows.Add(("buy vol", b => (double)(GetCandle(b).Volume + GetCandle(b).Delta) / 2, FlowText.Num, false));
        if (SellVol) rows.Add(("sell vol", b => (double)(GetCandle(b).Volume - GetCandle(b).Delta) / 2, FlowText.Num, false));
        if (MaxBuy) rows.Add(("max buy", b => (double)(GetCandle(b).MaxAskPriceInfo?.Ask ?? 0), FlowText.Num, false));
        if (MaxSell) rows.Add(("max sell", b => (double)(GetCandle(b).MaxBidPriceInfo?.Bid ?? 0), FlowText.Num, false));
        if (Poc) rows.Add(("poc", b => (double)(GetCandle(b).MaxVolumePriceInfo?.Price ?? 0), v => v.ToString("0.##", CultureInfo.InvariantCulture), false));
        if (Imbalances) rows.Add(("imb #", b => Imbalance(GetCandle(b)).Imb, v => v.ToString("0", CultureInfo.InvariantCulture), false));
        if (Stacked) rows.Add(("stacked", b => Imbalance(GetCandle(b)).Stack, v => v.ToString("0", CultureInfo.InvariantCulture), false));
        if (Trades) rows.Add(("trades", b => _cache.Get(this, b).Trades is var t && t > 0 ? t : null, FlowText.Num, false));
        if (Buys) rows.Add(("buys", b => _cache.Get(this, b).Trades > 0 ? _cache.Get(this, b).Buys : null, FlowText.Num, false));
        if (Sells) rows.Add(("sells", b => _cache.Get(this, b).Trades > 0 ? _cache.Get(this, b).Sells : null, FlowText.Num, false));
        if (AvgTrade) rows.Add(("avg trade", b => _cache.Get(this, b) is { Trades: > 0 } f ? (f.BuyCoins + f.SellCoins) / f.Trades : null, FlowText.Num, false));
        if (DeltaRetail) rows.Add(("Δ retail", b => _cache.Get(this, b).Trades > 0 ? SizeDelta(_cache.Get(this, b)).Retail : null, FlowText.Usd, true));
        if (DeltaWhales) rows.Add(("Δ whales", b => _cache.Get(this, b).Trades > 0 ? SizeDelta(_cache.Get(this, b)).Whales : null, FlowText.Usd, true));
        if (CvdRetail) rows.Add(("cvd retail", b => cvd[b].Retail, FlowText.Usd, true));
        if (CvdWhales) rows.Add(("cvd whales", b => cvd[b].Whales, FlowText.Usd, true));
        if (Range) rows.Add(("range", b => (double)(GetCandle(b).High - GetCandle(b).Low), v => v.ToString("0.##", CultureInfo.InvariantCulture), false));
        static string Pct(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        if (BuyPct) rows.Add(("buy %", b => GetCandle(b).Volume > 0 ? (double)((GetCandle(b).Volume + GetCandle(b).Delta) / 2 / GetCandle(b).Volume * 100) : null, Pct, false));
        if (VolChange) rows.Add(("vol chg", b => b > 0 && GetCandle(b - 1).Volume > 0 ? (double)((GetCandle(b).Volume - GetCandle(b - 1).Volume) / GetCandle(b - 1).Volume * 100) : null, Pct, true));
        if (Rvol)
            rows.Add(("rvol", b =>
            {
                decimal sum = 0;
                int n = 0;
                for (int k = Math.Max(0, b - RvolLength); k < b; k++, n++) sum += GetCandle(k).Volume;
                return n > 0 && sum > 0 ? (double)(GetCandle(b).Volume / (sum / n)) : null;
            }, v => v.ToString("0.0", CultureInfo.InvariantCulture) + "x", false));
        if (RangePct) rows.Add(("range %", b => GetCandle(b).Low > 0 ? (double)((GetCandle(b).High - GetCandle(b).Low) / GetCandle(b).Low * 100) : null, v => v.ToString("0.00", CultureInfo.InvariantCulture) + "%", false));
        if (VwapDist)
        {
            // VWAP del día de trading de cada vela
            var vwap = new Dictionary<int, double>();
            double pv = 0, vv = 0;
            int startDay = first;
            while (startDay > 0 && !IsNewSession(startDay)) startDay--;
            for (int b = startDay; b <= last; b++)
            {
                if (b > startDay && IsNewSession(b)) pv = vv = 0;
                var c = GetCandle(b);
                pv += (double)((c.High + c.Low + c.Close) / 3 * c.Volume);
                vv += (double)c.Volume;
                vwap[b] = vv > 0 ? pv / vv : (double)c.Close;
            }

            rows.Add(("vwap dist", b => vwap.TryGetValue(b, out var w) && w > 0 ? ((double)GetCandle(b).Close - w) / w * 100 : null, v => v.ToString("0.00", CultureInfo.InvariantCulture) + "%", true));
        }

        DateTime T0(int b) => GetCandle(b).Time;
        DateTime T1(int b) => b + 1 < CurrentBar ? GetCandle(b + 1).Time : DateTime.UtcNow;
        if (Liq) rows.Add(("liq", b => _deriv.Liquidations(T0(b), T1(b)) is var q && q.Long + q.Short > 0 ? (q.Long + q.Short) * (Units == StatUnit.Usd ? (double)GetCandle(b).Close : 1) : null, FlowText.Num, false));
        if (LiqSplit) rows.Add(("liq L−S", b => _deriv.Liquidations(T0(b), T1(b)) is var q && q.Long + q.Short > 0 ? (q.Short - q.Long) * (Units == StatUnit.Usd ? (double)GetCandle(b).Close : 1) : null, FlowText.Num, true));
        if (OiChange) rows.Add(("oi chg", b => _deriv.OiAt(T1(b)) is { } x && _deriv.OiAt(T0(b)) is { } y ? x - y : null, FlowText.Num, true));
        if (Funding) rows.Add(("funding", b => _deriv.FundingAt(T0(b)), v => v.ToString("0.0000", CultureInfo.InvariantCulture) + "%", true));
        if (BookDepth)
        {
            decimal tick = InstrumentInfo?.TickSize > 0 ? InstrumentInfo.TickSize : 0.01m;
            rows.Add(("book net", b =>
            {
                var samples = GetDepthSamples(b);
                if (samples.Count == 0) return null;
                var d = samples[^1];
                double px = (double)GetCandle(b).Close;
                int near = Math.Max(1, (int)(px * 0.001 / (double)tick)), far = Math.Max(near + 1, (int)(px * 0.025 / (double)tick));
                var (nb, na) = d.Within(near);
                var (fb, fa) = d.Within(far);
                return (fb - nb) - (fa - na);
            }, FlowText.Num, true));
        }
        if (rows.Count == 0) return;

        var font = new RenderFont("Arial", 7);
        var label = new RenderFont("Arial", 7.5f, FontStyle.Bold);
        int rh = Math.Max(10, Math.Min(18, (r.Height - 17) / rows.Count));
        int bw = Math.Max(1, pc.BarsWidth);
        bool text = bw >= 26;
        for (int k = 0; k < rows.Count; k++)
        {
            var (name, val, fmt, signed) = rows[k];
            int y = r.Top + 15 + k * rh;
            if (y + rh > r.Bottom) break;
            // escala por fila: percentiles 2 y 99 de lo visible (log)
            var vs = new List<double>();
            for (int b = first; b <= last; b++)
                if (val(b) is { } v && !double.IsNaN(v)) vs.Add(Math.Abs(v));
            vs.Sort();
            double p2 = vs.Count > 0 ? vs[(int)(vs.Count * 0.02)] : 0, p99 = vs.Count > 0 ? vs[Math.Min(vs.Count - 1, (int)(vs.Count * 0.99))] : 1;
            double lp = Math.Log10(Math.Max(p2, 1e-12) + 1), hp = Math.Log10(Math.Max(p99, 1e-12) + 1);
            for (int b = first; b <= last; b++)
            {
                if (val(b) is not { } v || double.IsNaN(v)) continue;
                int x = ChartInfo.GetXByBar(b);
                double t = hp - lp > 1e-9 ? Math.Clamp((Math.Log10(Math.Abs(v) + 1) - lp) / (hp - lp), 0, 1) : 0.5;
                var basec = signed ? v >= 0 ? FlowText.Buy : FlowText.Sell : Color.FromArgb(255, 110, 150, 220);
                if (Style == CellStyle.Filled)
                {
                    context.FillRectangle(Color.FromArgb((int)(25 + 160 * t), basec.R, basec.G, basec.B), new Rectangle(x, y, Math.Max(1, bw - 1), rh - 1));
                    if (text) context.DrawString(fmt(v), font, Color.FromArgb(235, 240, 240, 245), x + 2, y + 1);
                }
                else if (text) context.DrawString(fmt(v), font, Color.FromArgb((int)(120 + 135 * t), basec.R, basec.G, basec.B), x + 2, y + 1);
            }

            var size = context.MeasureString(name, label);
            context.FillRectangle(FlowText.Back, new Rectangle(r.Left, y, size.Width + 8, rh - 1));
            context.DrawString(name, label, FlowText.Dim, r.Left + 4, y + 1);
        }
    }
}

/// <summary>
/// NL/NS de LiquidityMapperFast: quién abrió y quién cerró posiciones. El cambio del interés abierto de cada vela va al lado
/// que operó a mercado: sube con compradores = largos nuevos; sube con vendedores = cortos nuevos; baja con vendedores =
/// largos que cierran; baja con compradores = cortos que cierran. Barras llenas donde se abrieron, tenues donde se
/// cerraron; la línea es la suma (el posicionamiento neto de los que operan a mercado). Es una estimación, no una señal.
/// </summary>
[DisplayName("NL/NS: largos y cortos nuevos (interés abierto + delta)")]
[Category(IndicatorCategories.Derivatives)]
[Description("Estima largos nuevos, cortos nuevos y cierres con el cambio del interés abierto (Binance + Bybit) y el delta de cada vela.")]
public class PositioningNlNs : Indicator
{
    private readonly ValueDataSeries _bars = new("NLNS", "Largos / cortos") { VisualType = VisualMode.Histogram, ShowZeroValue = false };
    private readonly ValueDataSeries _line = new("NET", "Posicionamiento neto") { Color = Color.FromArgb(255, 230, 230, 235), Width = 1, ShowZeroValue = true };
    private readonly Dictionary<string, List<DerivPoint>> _oi = new();
    private string _key = "";
    private bool _loading;
    private string? _error;

    public PositioningNlNs() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _bars;
        DataSeries.Add(_line);
        LineSeries.Add(new LineSeries("Zero", "Cero") { Value = 0, Color = Color.FromArgb(120, 140, 140, 150), LineDashStyle = LineDashStyle.Solid });
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Mostrar la línea del posicionamiento neto", GroupName = "Visualización", Order = 10)]
    public bool ShowLine { get; set; } = true;

    public enum Classify
    {
        [Display(Name = "Por el delta (quién operó a mercado, LMF)")] ByDelta,
        [Display(Name = "Por el precio (estados de openmarket)")] ByPrice,
    }

    private Classify _by = Classify.ByDelta;
    private int _smooth = 1;

    [Display(Name = "Clasificar", GroupName = "Cálculo", Order = 15)]
    public Classify ClassifyBy { get => _by; set { _by = value; RecalculateValues(); } }

    [Display(Name = "Suavizado (velas; con el precio)", GroupName = "Cálculo", Order = 16)]
    [Range(1, 50)]
    public int Smoothing { get => _smooth; set { _smooth = Math.Clamp(value, 1, 50); RecalculateValues(); } }

    [Display(Name = "Velas del cuadro de estados", GroupName = "Visualización", Order = 11)]
    [Range(2, 500)]
    public int KeyWindow { get; set; } = 24;

    // estado de cada vela con el precio: 1 largos nuevos, 2 cortos nuevos, 3 cortos cubren, 4 largos cierran
    private readonly Dictionary<int, int> _regime = new();

    [Display(Name = "Reiniciar la suma cada día", GroupName = "Cálculo", Order = 20)]
    public bool ResetDaily { get; set; }

    [Display(Name = "Símbolo de futuros (vacío = el del gráfico)", GroupName = "Cálculo", Order = 30)]
    public string SymbolOverride { get; set; } = "";

    private string Symbol => (string.IsNullOrWhiteSpace(SymbolOverride) ? InstrumentInfo?.Instrument ?? "" : SymbolOverride.Trim())
        .ToUpperInvariant().Replace("-", "").Replace("/", "").Replace(".P", "");

    private TimeSpan Step
    {
        get
        {
            var b = ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.FromHours(1);
            return b <= TimeSpan.Zero ? TimeSpan.FromHours(1) : b < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : b > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : b;
        }
    }

    private double _acc;

    protected override void OnRecalculate()
    {
        _acc = 0;
        _regime.Clear();
        _line.VisualType = ShowLine ? VisualMode.Line : VisualMode.Hide;
        if (CurrentBar <= 0 || Symbol.Length == 0) return;
        string sym = Symbol;
        var from = GetCandle(0).Time;
        var step = Step;
        string key = string.Join("|", sym, (int)step.TotalMinutes, from.Date.Ticks);
        if (key == _key || _loading) return;
        _loading = true;
        _ = Task.Run(async () =>
        {
            var got = new Dictionary<string, List<DerivPoint>>();
            string? err = null;
            foreach (var ex in new[] { Derivatives.Binance, Derivatives.Bybit })
            {
                try
                {
                    got[ex] = await Derivatives.OpenInterestAsync(ex, sym, step, from, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    err = ex + ": " + e.Message;
                }
            }

            DoActionInGuiThread(() =>
            {
                _loading = false;
                _error = got.Count == 0 ? err : null;
                _oi.Clear();
                foreach (var kv in got) _oi[kv.Key] = kv.Value;
                _key = key;
                RecalculateValues();
            });
        });
    }

    private double? OiAt(DateTime before)
    {
        double sum = 0;
        bool any = false;
        foreach (var list in _oi.Values)
        {
            int lo = 0, hi = list.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (list[mid].Time < before)
                {
                    ans = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }

            if (ans >= 0)
            {
                sum += list[ans].Value;
                any = true;
            }
        }

        return any ? sum : null;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (_oi.Count == 0) return;
        var c = GetCandle(bar);
        var end = bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : c.Time + Step;
        if (ResetDaily && bar > 0 && GetCandle(bar - 1).Time.Date != c.Time.Date) _acc = 0;
        if (OiAt(end) is not { } now || OiAt(c.Time) is not { } before) return;
        double d = now - before;
        if (_by == Classify.ByPrice)
        {
            // estados de openmarket: el precio sube y el OI sube = largos nuevos, etc. (promediado en N velas)
            int from = Math.Max(0, bar - _smooth + 1);
            var first = GetCandle(from);
            double dOi = (OiAt(end) ?? now) - (OiAt(first.Time) ?? before), dPx = (double)(c.Close - (from > 0 ? GetCandle(from - 1).Close : first.Open));
            dOi /= bar - from + 1;
            int reg = dOi >= 0 ? (dPx >= 0 ? 1 : 2) : (dPx >= 0 ? 3 : 4);
            _regime[bar] = reg;
            _bars[bar] = (decimal)d;
            _bars.Colors[bar] = reg switch
            {
                1 => Color.FromArgb(230, 38, 194, 129),
                2 => Color.FromArgb(230, 240, 82, 90),
                3 => Color.FromArgb(110, 38, 194, 129),
                _ => Color.FromArgb(110, 240, 82, 90),
            };
            double accP = _acc + d;
            _line[bar] = (decimal)accP;
            if (bar < CurrentBar - 1) _acc = accP;
            return;
        }

        double delta = (double)c.Delta;
        if (d == 0 || delta == 0)
        {
            _line[bar] = (decimal)_acc;
            return;
        }

        double signed = Math.Sign(delta) * Math.Abs(d);
        bool opened = d > 0;
        // largos nuevos (verde lleno), cortos nuevos (rojo lleno), largos que cierran (rojo tenue), cortos que cierran (verde tenue)
        _bars[bar] = (decimal)signed;
        _bars.Colors[bar] = (opened, delta > 0) switch
        {
            (true, true) => Color.FromArgb(230, 38, 194, 129),
            (true, false) => Color.FromArgb(230, 240, 82, 90),
            (false, false) => Color.FromArgb(90, 240, 82, 90),
            _ => Color.FromArgb(90, 38, 194, 129),
        };
        double acc = _acc + signed;
        _line[bar] = (decimal)acc;
        if (bar < CurrentBar - 1) _acc = acc;
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (Container == null) return;
        var r = Container.Region;
        string? msg = _loading ? UiText.Tr("Trayendo el interés abierto…") : _error != null ? UiText.Tr("Interés abierto: ") + _error : null;
        var font = new RenderFont("Arial", 8);
        context.DrawString(UiText.Tr("lleno: abren · tenue: cierran (estimación)"), font, FlowText.Dim, r.Left + 8, r.Top + 16);
        if (msg != null) context.DrawString(msg, font, FlowText.Dim, r.Left + 8, r.Top + 29);
        if (_by != Classify.ByPrice || CurrentBar < 2) return;
        // el cuadro de estados (qué parte de las últimas N velas fue cada uno)
        var counts = new int[5];
        int n = 0;
        for (int b = Math.Max(0, CurrentBar - KeyWindow); b < CurrentBar; b++)
            if (_regime.TryGetValue(b, out var g))
            {
                counts[g]++;
                n++;
            }

        if (n == 0) return;
        string[] names = { "", "Largos nuevos", "Cortos nuevos", "Cortos cubren", "Largos cierran" };
        Color[] cols = { Color.Empty, Color.FromArgb(230, 38, 194, 129), Color.FromArgb(230, 240, 82, 90), Color.FromArgb(150, 38, 194, 129), Color.FromArgb(150, 240, 82, 90) };
        int w = 150, x = r.Right - w - 8, y = r.Top + 16;
        context.FillRectangle(Color.FromArgb(215, 18, 20, 26), new Rectangle(x, y, w, 4 * 13 + 8), 4);
        for (int k = 1; k <= 4; k++)
        {
            context.FillRectangle(cols[k], new Rectangle(x + 6, y + 6 + (k - 1) * 13, 8, 8));
            context.DrawString($"{UiText.Tr(names[k])} {100.0 * counts[k] / n:0} %", font, FlowText.Dim, x + 18, y + 3 + (k - 1) * 13);
        }

        // donde cambió el estado que manda en la ventana y se sostuvo tres velas
        int Leader(int end)
        {
            var c2 = new int[5];
            for (int b = Math.Max(0, end - KeyWindow + 1); b <= end; b++)
                if (_regime.TryGetValue(b, out var g)) c2[g]++;
            int best = 1;
            for (int k = 2; k <= 4; k++)
                if (c2[k] > c2[best]) best = k;
            return best;
        }

        int first = Math.Max(KeyWindow + 3, FirstVisibleBarNumber), last = Math.Min(CurrentBar - 1, LastVisibleBarNumber);
        for (int b = first; b <= last; b++)
        {
            int l0 = Leader(b - 3), l1 = Leader(b - 2);
            if (l1 == l0 || Leader(b - 1) != l1 || Leader(b) != l1) continue;
            bool buying = l1 is 1 or 3;
            int xx = ChartInfo!.GetXByBar(b - 2, false);
            context.FillEllipse(buying ? cols[1] : cols[2], new Rectangle(xx - 4, r.Bottom - 12, 8, 8));
        }
    }
}
