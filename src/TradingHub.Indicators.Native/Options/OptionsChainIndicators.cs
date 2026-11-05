using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Feeds.External;
using TradingHub.Indicators.Native.Structure;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Options;

// Opciones con la cadena (foto actual): gamma de los creadores de mercado, exposición por ejercicio, matriz de gamma,
// carriles, distribución por ejercicio, contratos más operados, superficie de volatilidad, nube de probabilidades,
// movimiento esperado, probabilidad de un contrato, postura, tablero, contexto de volatilidad, mapa de gamma por vela,
// cambios del interés abierto y tipo de día.

// ============================================================================ posición de los creadores de mercado

[DisplayName("Posición estimada de los creadores de mercado")]
[Category(IndicatorCategories.Options)]
[Description("Cada 10 minutos estima la gamma de los creadores de mercado con la cadena de opciones: si es positiva, su cobertura amortigua el precio (venden en las subas y compran en las bajas); si es negativa, lo empuja. El cuadro dice cuánto tendrían que comprar o vender si el precio se mueve 1 %, y las líneas marcan el cambio de gamma y los muros de calls y de puts (con su historia desde que se guardan las fotos).")]
public class DealerPositioning : OptionsScene
{
    protected override TimeSpan ChainAge => TimeSpan.FromMinutes(10);

    [Display(Name = "Historia de los muros", GroupName = "Dibujo", Order = 20)] public bool ShowHistory { get; set; } = true;

    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        double gex = OptionsMath.TotalGex(c), flip = OptionsMath.GammaFlip(c);
        var (cw, pw) = OptionsMath.Walls(c);
        int from = Math.Max(0, LastBar - 60);
        if (double.IsFinite(flip)) s.HLine(from, -1, flip, Opt.Flip, 2, LineDashStyle.Dash, "Cambio de gamma " + Price(flip), "Cambio de gamma");
        if (double.IsFinite(cw)) s.HLine(from, -1, cw, Opt.Call, 2, LineDashStyle.Solid, "Muro de calls " + Price(cw), "Muro de calls");
        if (double.IsFinite(pw)) s.HLine(from, -1, pw, Opt.Put, 2, LineDashStyle.Solid, "Muro de puts " + Price(pw), "Muro de puts");
        if (ShowHistory)
        {
            var snaps = OptionsData.Snapshots(Underlying);
            var callPath = new List<(int, double)>();
            var putPath = new List<(int, double)>();
            foreach (var sn in snaps)
            {
                int b = BarOf(sn.Time);
                if (b < 0) continue;
                var call = sn.Rows.Where(r => r.Strike >= sn.Spot).OrderByDescending(r => r.Gex).FirstOrDefault();
                var put = sn.Rows.Where(r => r.Strike <= sn.Spot).OrderBy(r => r.Gex).FirstOrDefault();
                if (call != null) callPath.Add((b, call.Strike));
                if (put != null) putPath.Add((b, put.Strike));
            }

            if (callPath.Count > 1) s.Paths.Add(new Scene.PathShape(callPath, Pal.A(Opt.Call, 130), 1, LineDashStyle.Dot));
            if (putPath.Count > 1) s.Paths.Add(new Scene.PathShape(putPath, Pal.A(Opt.Put, 130), 1, LineDashStyle.Dot));
        }

        s.DashTitle = UiText.Tr("Creadores de mercado") + " · " + Underlying;
        s.Row("Gamma total (por 1 %)", Opt.SignedUsd(gex), Opt.BiasColor(gex));
        s.Row("Régimen", gex >= 0 ? "gamma positiva: amortiguan" : "gamma negativa: amplifican", gex >= 0 ? Opt.Call : Opt.Put);
        string amount = Opt.Usd(Math.Abs(gex));
        s.Row("Si sube 1 %", gex >= 0 ? $"venden ≈ {amount}" : $"compran ≈ {amount}", default);
        s.Row("Si baja 1 %", gex >= 0 ? $"compran ≈ {amount}" : $"venden ≈ {amount}", default);
        if (double.IsFinite(flip)) s.Row("Cambio de gamma", Price(flip) + "  (" + Opt.Pct((flip / c.Spot - 1) * 100, "+0.0;-0.0") + ")", Opt.Flip);
        s.Row("Foto", c.Time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), default);
    }
}

// ============================================================================ exposición de las griegas

[DisplayName("Exposición de las griegas")]
[Category(IndicatorCategories.Options)]
[Description("La foto de ahora: en qué ejercicios y en qué vencimientos se concentra la cobertura de los creadores de mercado. Barras por ejercicio pegadas a la derecha del precio (gamma o delta con signo; vega o interés abierto con calls a la derecha y puts a la izquierda) y un cuadro con los vencimientos de más exposición.")]
public class GreekExposure : OptionsScene
{
    public enum Greek
    {
        [Display(Name = "Gamma (GEX)")] Gamma,
        [Display(Name = "Delta (DEX)")] Delta,
        [Display(Name = "Vega")] Vega,
        [Display(Name = "Interés abierto")] OpenInterest,
    }

    private List<(double Strike, double Left, double Right)> _rows = new();
    private double _step;

    [Display(Name = "Griega", GroupName = "Cálculo", Order = 10)] public Greek Measure { get; set; } = Greek.Gamma;
    [Display(Name = "Vencimiento", GroupName = "Cálculo", Order = 11)] public ExpiryPick Expiry { get; set; } = ExpiryPick.All;
    [Display(Name = "Ancho de las barras (px)", GroupName = "Dibujo", Order = 20)] [Range(40, 600)] public int Width { get; set; } = 160;

    private double Value(OptionContract o, OptionsChain c, out bool callSide)
    {
        callSide = o.Right == OptionRight.Call;
        double t = o.DaysToExpiry(c.Time) / 365, s = c.Spot, n = o.OpenInterest * o.ContractSize;
        return Measure switch
        {
            Greek.Gamma => OptionsMath.GammaOf(o, s, c.Time) * n * s * s * 0.01 * (callSide ? 1 : -1),
            Greek.Delta => (o.Delta != 0 ? o.Delta : OptionsMath.Greeks(s, o.Strike, t, o.Iv, o.Right).Delta) * n * s,
            Greek.Vega => (o.Vega > 0 ? o.Vega : OptionsMath.Vega(s, o.Strike, t, o.Iv)) * n,
            _ => n * s,
        };
    }

    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        var exp = Opt.Pick(c, Expiry);
        var by = new SortedDictionary<double, (double L, double R)>();
        var byExp = new Dictionary<DateTime, double>();
        foreach (var o in Opt.Of(c, exp))
        {
            double v = Value(o, c, out bool call);
            var cur = by.TryGetValue(o.Strike, out var x) ? x : (0, 0);
            bool signed = Measure is Greek.Gamma or Greek.Delta;
            by[o.Strike] = signed ? (0, cur.R + v) : call ? (cur.L, cur.R + v) : (cur.L + v, cur.R);
            byExp[o.Expiry] = (byExp.TryGetValue(o.Expiry, out var e) ? e : 0) + (signed ? v : Math.Abs(v));
        }

        _rows = by.Where(kv => Math.Abs(kv.Key / c.Spot - 1) < 0.35).Select(kv => (kv.Key, kv.Value.L, kv.Value.R)).ToList();
        _step = _rows.Count > 1 ? _rows.Zip(_rows.Skip(1), (a, b) => b.Strike - a.Strike).Where(d => d > 0).DefaultIfEmpty(c.Spot * 0.01).Min() : c.Spot * 0.01;
        var top = _rows.OrderByDescending(r => Math.Abs(r.Left) + Math.Abs(r.Right)).FirstOrDefault();
        if (top.Strike > 0) s.HLine(Math.Max(0, LastBar - 40), -1, top.Strike, Opt.Flip, 1, LineDashStyle.Dash, UiText.Tr("Mayor exposición") + " " + Price(top.Strike), "Mayor exposición");
        s.DashTitle = UiText.Tr(Measure switch { Greek.Gamma => "Gamma", Greek.Delta => "Delta", Greek.Vega => "Vega", _ => "Interés abierto" }) + " · " + Underlying + " · " + UiText.Tr(Opt.ExpiryText(exp));
        s.Row("Total", Opt.SignedUsd(byExp.Values.Sum()), Opt.BiasColor(byExp.Values.Sum()));
        foreach (var kv in byExp.OrderByDescending(kv => Math.Abs(kv.Value)).Take(6))
            s.Row(kv.Key.ToString("dd/MM/yy", CultureInfo.InvariantCulture) + $" ({(kv.Key - c.Time).TotalDays:0} d)", Opt.SignedUsd(kv.Value), Opt.BiasColor(kv.Value));
    }

    protected override void RenderOptions(RenderContext ctx, Rectangle region)
    {
        bool signed = Measure is Greek.Gamma or Greek.Delta;
        Opt.DockBars(ctx, region, Y, _rows, Width, _step, Pal.A(Opt.Put, 190), Pal.A(Opt.Call, 190), signed);
    }
}

// ============================================================================ matriz de ejercicios

[DisplayName("Matriz de ejercicios")]
[Category(IndicatorCategories.Options)]
[Description("Un panel pegado al eje de precio con la fuerza con la que los creadores de mercado tendrían que cubrirse en cada precio (la gamma total recalculada como si el precio estuviera ahí): verde donde amortiguan, rojo donde empujan. Líneas punteadas en el cambio de gamma, los muros y el ejercicio de mayor gamma.")]
public class StrikeMatrix : OptionsScene
{
    private readonly List<(double Price, double Gex)> _curve = new();

    protected override TimeSpan ChainAge => TimeSpan.FromMinutes(5);

    [Display(Name = "Vencimientos hasta (días)", GroupName = "Cálculo", Order = 10)] [Range(1, 1000)] public int MaxDays { get; set; } = 400;
    [Display(Name = "Ancho del panel (px)", GroupName = "Dibujo", Order = 20)] [Range(40, 600)] public int Width { get; set; } = 130;

    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        _curve.Clear();
        for (int k = -30; k <= 30; k++)
        {
            double px = c.Spot * (1 + k * 0.005);
            _curve.Add((px, OptionsMath.GexByStrike(c, px, MaxDays).Values.Sum()));
        }

        double flip = OptionsMath.GammaFlip(c);
        var (cw, pw) = OptionsMath.Walls(c);
        var maxK = OptionsMath.GexByStrike(c, null, MaxDays).OrderByDescending(kv => Math.Abs(kv.Value)).FirstOrDefault();
        int from = Math.Max(0, LastBar - 80);
        if (double.IsFinite(flip)) s.HLine(from, -1, flip, Opt.Flip, 1, LineDashStyle.Dash, "Cambio de gamma", "Cambio de gamma");
        if (double.IsFinite(cw)) s.HLine(from, -1, cw, Opt.Call, 1, LineDashStyle.Dash, "Muro de calls", "Muro de calls");
        if (double.IsFinite(pw)) s.HLine(from, -1, pw, Opt.Put, 1, LineDashStyle.Dash, "Muro de puts", "Muro de puts");
        if (maxK.Key > 0) s.HLine(from, -1, maxK.Key, Pal.Text, 1, LineDashStyle.Dot, "Mayor gamma", "Mayor gamma");
    }

    protected override void RenderOptions(RenderContext ctx, Rectangle region)
    {
        if (_curve.Count < 2) return;
        double max = _curve.Max(p => Math.Abs(p.Gex));
        if (max <= 0) return;
        int edge = region.Right - 4, mid = edge - Width / 2;
        ctx.FillRectangle(Color.FromArgb(80, 14, 17, 24), new Rectangle(edge - Width - 2, region.Top, Width + 4, region.Height));
        for (int k = 0; k < _curve.Count - 1; k++)
        {
            int y0 = Y(_curve[k].Price), y1 = Y(_curve[k + 1].Price);
            double g = _curve[k].Gex;
            int w = (int)(Width / 2.0 * Math.Abs(g) / max);
            var col = Pal.A(g >= 0 ? Opt.Call : Opt.Put, (int)(60 + 150 * Math.Abs(g) / max));
            ctx.FillRectangle(col, new Rectangle(g >= 0 ? mid : mid - w, Math.Min(y0, y1), Math.Max(1, w), Math.Max(1, Math.Abs(y1 - y0))));
        }

        ctx.DrawLine(new RenderPen(Color.FromArgb(110, 170, 175, 185), 1), mid, region.Top, mid, region.Bottom);
        var font = new RenderFont("Arial", 7);
        ctx.DrawString(UiText.Tr("amortiguan →"), font, Opt.Call, mid + 2, region.Top + 4);
        ctx.DrawString(UiText.Tr("← empujan"), font, Opt.Put, mid - 52, region.Top + 4);
    }
}

// ============================================================================ carriles de ejercicios

[DisplayName("Carriles de ejercicios")]
[Category(IndicatorCategories.Options)]
[Description("Sobre el gráfico, un carril horizontal por ejercicio cerca del precio cuyo grosor y brillo siguen el volumen de opciones de hoy en ese ejercicio (de las operaciones, o de la cadena si no hay), con la gamma de cada uno, los muros de calls y de puts y el cambio de gamma de la cadena en vivo.")]
public class StrikeLanes : OptionsScene
{
    [Display(Name = "Ejercicios a cada lado", GroupName = "Cálculo", Order = 10)] [Range(2, 60)] public int Lanes { get; set; } = 12;

    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        var today = TodayTrades();
        var vol = new Dictionary<double, (double Call, double Put)>();
        if (today.Count > 0)
            foreach (var t in today)
            {
                var v = vol.TryGetValue(t.Strike, out var x) ? x : (0, 0);
                vol[t.Strike] = t.Right == OptionRight.Call ? (v.Call + t.Size, v.Put) : (v.Call, v.Put + t.Size);
            }
        else
            foreach (var o in c.Contracts)
            {
                var v = vol.TryGetValue(o.Strike, out var x) ? x : (0, 0);
                vol[o.Strike] = o.Right == OptionRight.Call ? (v.Call + o.Volume, v.Put) : (v.Call, v.Put + o.Volume);
            }

        var strikes = c.Contracts.Select(o => o.Strike).Distinct().OrderBy(k => k).ToList();
        int at = strikes.FindIndex(k => k >= c.Spot);
        if (at < 0) at = strikes.Count - 1;
        var near = strikes.Skip(Math.Max(0, at - Lanes)).Take(2 * Lanes).ToList();
        double max = near.Select(k => vol.TryGetValue(k, out var v) ? v.Call + v.Put : 0).DefaultIfEmpty(0).Max();
        var gex = OptionsMath.GexByStrike(c);
        int start = DayStart(LastBar);
        foreach (var k in near)
        {
            var v = vol.TryGetValue(k, out var x) ? x : (0, 0);
            double tot = v.Call + v.Put, frac = max > 0 ? tot / max : 0;
            var col = v.Call >= v.Put ? Opt.Call : Opt.Put;
            int width = 1 + (int)Math.Round(frac * 4);
            double g = gex.TryGetValue(k, out var gg) ? gg : 0;
            s.HLine(start, -1, k, Pal.A(col, (int)(40 + 180 * frac)), width, LineDashStyle.Solid, frac > 0.35 ? $"{Opt.Strike(k)} · {Opt.N(tot, "0")} · GEX {Opt.SignedUsd(g)}" : null);
        }

        double flip = OptionsMath.GammaFlip(c);
        var (cw, pw) = OptionsMath.Walls(c);
        if (double.IsFinite(flip)) s.HLine(start, -1, flip, Opt.Flip, 2, LineDashStyle.Dash, "Cambio de gamma", "Cambio de gamma");
        if (double.IsFinite(cw)) s.HLine(start, -1, cw, Opt.Call, 2, LineDashStyle.Dash, "Muro de calls", "Muro de calls");
        if (double.IsFinite(pw)) s.HLine(start, -1, pw, Opt.Put, 2, LineDashStyle.Dash, "Muro de puts", "Muro de puts");
        s.DashTitle = UiText.Tr("Carriles") + " · " + Underlying;
        s.Row("Volumen de hoy", Opt.N(vol.Values.Sum(v => v.Call + v.Put), "0"), default);
        s.Row("Calls / puts", Opt.N(vol.Values.Sum(v => v.Call), "0") + " / " + Opt.N(vol.Values.Sum(v => v.Put), "0"), default);
        s.Row("Gamma total", Opt.SignedUsd(gex.Values.Sum()), Opt.BiasColor(gex.Values.Sum()));
    }
}

// ============================================================================ distribución por ejercicio

[DisplayName("Distribución por ejercicio")]
[Category(IndicatorCategories.Options)]
[Description("Antes del vencimiento: qué ejercicios están llenos. Barras pegadas a la derecha con el interés abierto o el volumen de cada ejercicio (calls a la derecha, puts a la izquierda) y una línea en el precio de máximo dolor (donde los compradores de opciones pierden más al vencer).")]
public class StrikeBreakdown : OptionsScene
{
    private List<(double, double, double)> _rows = new();
    private double _step;

    [Display(Name = "Vencimiento", GroupName = "Cálculo", Order = 10)] public ExpiryPick Expiry { get; set; } = ExpiryPick.Nearest;
    [Display(Name = "Usar el volumen (si no, el interés abierto)", GroupName = "Cálculo", Order = 11)] public bool UseVolume { get; set; }
    [Display(Name = "Ancho de las barras (px)", GroupName = "Dibujo", Order = 20)] [Range(40, 600)] public int Width { get; set; } = 160;

    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        var exp = Opt.Pick(c, Expiry);
        var rows = Opt.Of(c, exp).GroupBy(o => o.Strike).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Where(o => o.Right == OptionRight.Put).Sum(o => UseVolume ? o.Volume : o.OpenInterest), g.Where(o => o.Right == OptionRight.Call).Sum(o => UseVolume ? o.Volume : o.OpenInterest)))
            .Where(r => Math.Abs(r.Key / c.Spot - 1) < 0.35).ToList();
        _rows = rows;
        _step = rows.Count > 1 ? rows.Zip(rows.Skip(1), (a, b) => b.Key - a.Key).Where(d => d > 0).DefaultIfEmpty(c.Spot * 0.01).Min() : c.Spot * 0.01;
        double pain = OptionsMath.MaxPain(c, exp);
        if (double.IsFinite(pain)) s.HLine(Math.Max(0, LastBar - 60), -1, pain, Opt.Flip, 2, LineDashStyle.Dash, "Máximo dolor " + Price(pain), "Máximo dolor");
        double calls = rows.Sum(r => r.Item3), puts = rows.Sum(r => r.Item2);
        s.DashTitle = UiText.Tr(UseVolume ? "Volumen por ejercicio" : "Interés abierto por ejercicio") + " · " + UiText.Tr(Opt.ExpiryText(exp));
        s.Row("Calls", Opt.N(calls, "0"), Opt.Call);
        s.Row("Puts", Opt.N(puts, "0"), Opt.Put);
        s.Row("Puts / calls", calls > 0 ? Opt.N(puts / calls, "0.00") : "—", default);
        var topC = rows.OrderByDescending(r => r.Item3).FirstOrDefault();
        var topP = rows.OrderByDescending(r => r.Item2).FirstOrDefault();
        if (topC.Key > 0) s.Row("Más calls en", Price(topC.Key), Opt.Call);
        if (topP.Key > 0) s.Row("Más puts en", Price(topP.Key), Opt.Put);
        if (double.IsFinite(pain)) s.Row("Máximo dolor", Price(pain), Opt.Flip);
    }

    protected override void RenderOptions(RenderContext ctx, Rectangle region) =>
        Opt.DockBars(ctx, region, Y, _rows, Width, _step, Pal.A(Opt.Put, 190), Pal.A(Opt.Call, 190), false);
}

// ============================================================================ contratos más operados

[DisplayName("Contratos de opciones más operados")]
[Category(IndicatorCategories.Options)]
[Description("Un cuadro con los contratos de hoy con más volumen o más interés abierto (tipo, ejercicio, vencimiento, IV y cantidad) y una línea en el gráfico en el ejercicio de cada uno.")]
public class TopContracts : OptionsScene
{
    [Display(Name = "Por interés abierto (si no, por volumen)", GroupName = "Cálculo", Order = 10)] public bool ByOpenInterest { get; set; }
    [Display(Name = "Contratos", GroupName = "Cálculo", Order = 11)] [Range(1, 30)] public int Count { get; set; } = 8;

    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        var top = c.Contracts.OrderByDescending(o => ByOpenInterest ? o.OpenInterest : o.Volume).Take(Count).ToList();
        s.DashTitle = UiText.Tr(ByOpenInterest ? "Más interés abierto" : "Más volumen") + " · " + Underlying;
        int from = Math.Max(0, LastBar - 30);
        foreach (var o in top)
        {
            var col = o.Right == OptionRight.Call ? Opt.Call : Opt.Put;
            s.Row(Opt.Contract(o.Right, o.Strike, o.Expiry) + "  IV " + Opt.N(o.Iv * 100, "0"), Opt.N(ByOpenInterest ? o.OpenInterest : o.Volume, "0"), col);
            s.HLine(from, -1, o.Strike, Pal.A(col, 150), 1, LineDashStyle.Dot, Opt.Contract(o.Right, o.Strike, o.Expiry));
        }
    }
}

// ============================================================================ superficie de volatilidad

[DisplayName("Superficie de volatilidad")]
[Category(IndicatorCategories.Options)]
[Description("La volatilidad implícita de cada vencimiento (filas) en cada distancia del precio (columnas, de −20 % a +20 %), teñida contra la del dinero de su vencimiento: más cara en naranja, más barata en azul. Sirve para encontrar opciones baratas o caras antes de operar.")]
public class VolatilitySurface : OptionsScene
{
    private static readonly double[] Moneyness = { -20, -10, -5, 0, 5, 10, 20 };
    private readonly List<(DateTime Expiry, double Atm, double[] Iv)> _grid = new();

    [Display(Name = "Vencimientos", GroupName = "Cálculo", Order = 10)] [Range(2, 16)] public int Expiries { get; set; } = 8;

    public VolatilitySurface() => DashboardPosition = DashCorner.TopRight;

    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        _grid.Clear();
        foreach (var e in c.Contracts.Select(o => o.Expiry).Distinct().Where(e => e > c.Time).OrderBy(e => e).Take(Expiries))
        {
            double atm = OptionsMath.AtmIv(c, e);
            _grid.Add((e, atm, Moneyness.Select(m => OptionsMath.SmileIv(c, e, c.Spot * (1 + m / 100))).ToArray()));
        }
    }

    protected override void RenderOptions(RenderContext ctx, Rectangle region)
    {
        if (_grid.Count == 0 || !ShowDashboard) return;
        var font = new RenderFont("Arial", 8);
        var bold = new RenderFont("Arial", 8, FontStyle.Bold);
        int cw = 44, rh = 16, lw = 70;
        int w = lw + cw * Moneyness.Length + 12, h = rh * (_grid.Count + 2) + 10;
        int col = (int)DashboardPosition % 3, row = (int)DashboardPosition / 3;
        int x = col switch { 0 => region.Left + 8, 1 => region.Left + (region.Width - w) / 2, _ => region.Right - w - 8 };
        int y = row switch { 0 => region.Top + 26, 1 => region.Top + (region.Height - h) / 2, _ => region.Bottom - h - 8 };
        ctx.FillRectangle(Color.FromArgb(225, 18, 20, 26), new Rectangle(x, y, w, h), 5);
        ctx.DrawString(UiText.Tr("Volatilidad implícita") + " · " + Underlying, bold, Opt.Text, x + 6, y + 4);
        for (int j = 0; j < Moneyness.Length; j++)
            ctx.DrawString(Moneyness[j] == 0 ? "ATM" : Moneyness[j].ToString("+0;−0", CultureInfo.InvariantCulture) + "%", font, Opt.Dim, x + lw + j * cw + 6, y + 4 + rh);
        for (int i = 0; i < _grid.Count; i++)
        {
            var g = _grid[i];
            int ry = y + 4 + rh * (i + 2);
            ctx.DrawString(g.Expiry.ToString("dd/MM/yy", CultureInfo.InvariantCulture), font, Opt.Dim, x + 6, ry);
            for (int j = 0; j < Moneyness.Length; j++)
            {
                double iv = g.Iv[j], rel = double.IsFinite(g.Atm) && g.Atm > 0 ? iv / g.Atm - 1 : 0;
                var back = rel >= 0 ? Color.FromArgb((int)Math.Clamp(30 + rel * 900, 30, 200), 251, 140, 0) : Color.FromArgb((int)Math.Clamp(30 - rel * 900, 30, 200), 41, 98, 255);
                var cell = new Rectangle(x + lw + j * cw, ry - 2, cw - 2, rh - 2);
                ctx.FillRectangle(back, cell, 2);
                ctx.DrawString(Opt.N(iv * 100, "0.0"), font, Opt.Text, cell.Left + 6, ry);
            }
        }
    }
}

// ============================================================================ nube de probabilidades

[DisplayName("Nube de probabilidades de las opciones")]
[Category(IndicatorCategories.Options)]
[Description("Dónde creen los operadores de opciones que va a estar el precio al vencimiento: la distribución que sale de la sonrisa de volatilidad, dibujada como una nube delante de la última vela, con las zonas del 68 % y del 95 % y la mediana como líneas con alerta.")]
public class OptionsOddsCloud : OptionsScene
{
    private readonly List<(double Price, double Density)> _cloud = new();

    [Display(Name = "Vencimiento", GroupName = "Cálculo", Order = 10)] public ExpiryPick Expiry { get; set; } = ExpiryPick.Weekly;
    [Display(Name = "Ancho de la nube (px)", GroupName = "Dibujo", Order = 20)] [Range(40, 600)] public int Width { get; set; } = 150;

    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        var exp = Opt.Pick(c, Expiry == ExpiryPick.All ? ExpiryPick.Weekly : Expiry) ?? OptionsMath.ExpiryNear(c, 7);
        double t = Math.Max(1.0 / 365, (exp - c.Time).TotalDays / 365), atm = OptionsMath.AtmIv(c, exp);
        if (!double.IsFinite(atm) || atm <= 0) return;
        double sd = atm * Math.Sqrt(t);
        var grid = new List<(double K, double Cdf)>();
        for (int k = -80; k <= 80; k++)
        {
            double px = c.Spot * Math.Exp(k / 20.0 * sd);
            grid.Add((px, 1 - OptionsMath.ProbAbove(c.Spot, px, t, OptionsMath.SmileIv(c, exp, px))));
        }

        // la distribución acumulada tiene que crecer: se corrige lo que la sonrisa deja fuera de orden
        for (int i = 1; i < grid.Count; i++)
            if (grid[i].Cdf < grid[i - 1].Cdf) grid[i] = (grid[i].K, grid[i - 1].Cdf);
        _cloud.Clear();
        for (int i = 1; i < grid.Count; i++) _cloud.Add(((grid[i].K + grid[i - 1].K) / 2, (grid[i].Cdf - grid[i - 1].Cdf) / Math.Max(1e-9, grid[i].K - grid[i - 1].K)));
        double Q(double p)
        {
            for (int i = 1; i < grid.Count; i++)
                if (grid[i].Cdf >= p)
                {
                    double w = (p - grid[i - 1].Cdf) / Math.Max(1e-12, grid[i].Cdf - grid[i - 1].Cdf);
                    return grid[i - 1].K + w * (grid[i].K - grid[i - 1].K);
                }

            return grid[^1].K;
        }

        int from = LastBar;
        double q025 = Q(0.025), q16 = Q(0.16), q50 = Q(0.5), q84 = Q(0.84), q975 = Q(0.975);
        s.HLine(from, -1, q50, Opt.Text, 1, LineDashStyle.Dash, "Mediana " + Price(q50), "Mediana al vencimiento");
        s.HLine(from, -1, q84, Opt.Flip, 1, LineDashStyle.Solid, "68 % " + Price(q84), "68 % arriba");
        s.HLine(from, -1, q16, Opt.Flip, 1, LineDashStyle.Solid, "68 % " + Price(q16), "68 % abajo");
        s.HLine(from, -1, q975, Pal.A(Opt.Flip, 150), 1, LineDashStyle.Dot, "95 % " + Price(q975), "95 % arriba");
        s.HLine(from, -1, q025, Pal.A(Opt.Flip, 150), 1, LineDashStyle.Dot, "95 % " + Price(q025), "95 % abajo");
        s.DashTitle = UiText.Tr("Al vencimiento") + " " + exp.ToString("dd/MM/yy", CultureInfo.InvariantCulture);
        s.Row("IV en el dinero", Opt.Pct(atm * 100), default);
        s.Row("68 %", Price(q16) + " – " + Price(q84), Opt.Flip);
        s.Row("95 %", Price(q025) + " – " + Price(q975), default);
        s.Row("Prob. de terminar arriba", Opt.Pct(OptionsMath.ProbAbove(c.Spot, c.Spot, t, atm) * 100, "0"), default);
    }

    protected override void RenderOptions(RenderContext ctx, Rectangle region)
    {
        if (_cloud.Count < 2) return;
        double max = _cloud.Max(p => p.Density);
        if (max <= 0) return;
        int x0 = Math.Min(X(LastBar, false) + 12, region.Right - Width - 4);
        for (int i = 0; i < _cloud.Count - 1; i++)
        {
            int y0 = Y(_cloud[i].Price), y1 = Y(_cloud[i + 1].Price);
            double f = _cloud[i].Density / max;
            int w = (int)(Width * f);
            ctx.FillRectangle(Color.FromArgb((int)(10 + 90 * f * f), 251, 191, 36), new Rectangle(x0, Math.Min(y0, y1), Math.Max(1, w), Math.Max(1, Math.Abs(y1 - y0))));
        }
    }
}

// ============================================================================ movimiento esperado

[DisplayName("Movimiento esperado de las opciones")]
[Category(IndicatorCategories.Options)]
[Description("El rango que el mercado de opciones espera: cada día, líneas al cierre del día anterior ± el movimiento de un desvío según la IV de ese momento (no cambia después), un cono hacia adelante con ±1σ y ±2σ y un cuadro con el movimiento en precio y en %.")]
public class ExpectedMove : OptionsScene
{
    [Display(Name = "Días hacia atrás", GroupName = "Cálculo", Order = 10)] [Range(1, 60)] public int Days { get; set; } = 10;
    [Display(Name = "Velas del cono", GroupName = "Dibujo", Order = 20)] [Range(0, 500)] public int ConeBars { get; set; } = 40;

    protected override void BuildOptions(Scene s)
    {
        var c = OptionsData.Chain(Underlying, ChainAge);
        var daily = OptionsData.Daily(Underlying);
        bool crypto = Underlying is "BTC" or "ETH" or "SOL" or "XRP";
        double year = crypto ? 365 : 252;
        int i = LastBar, done = 0;
        var starts = new List<int>();
        while (i > 0 && done <= Days)
        {
            int st = DayStart(i);
            starts.Add(st);
            i = st - 1;
            done++;
        }

        foreach (var st in starts)
        {
            if (st == 0) continue;
            double prevClose = Bars[st - 1].C;
            var prevDay = TimeOf(st - 1).Date;
            // la IV conocida al abrir el día: la del día anterior (o la de la cadena en vivo para hoy)
            double iv = Opt.DayOf(daily, prevDay)?.AtmIv ?? double.NaN;
            bool today = st == DayStart(LastBar);
            if (today && c != null && !double.IsFinite(iv)) iv = OptionsMath.AtmIv(c, OptionsMath.ExpiryNear(c, 1));
            if (!double.IsFinite(iv) || iv <= 0) continue;
            double em = prevClose * iv * Math.Sqrt(1 / year);
            int end = today ? -1 : DayStart(LastBar) == st ? -1 : NextDayStart(st) - 1;
            s.HLine(st, end, prevClose + em, Opt.Call, 1, LineDashStyle.Solid, today ? "+1σ " + Price(prevClose + em) : null, today ? "Movimiento esperado arriba" : null);
            s.HLine(st, end, prevClose - em, Opt.Put, 1, LineDashStyle.Solid, today ? "−1σ " + Price(prevClose - em) : null, today ? "Movimiento esperado abajo" : null);
            s.HLine(st, end, prevClose, Pal.A(Pal.Muted, 160), 1, LineDashStyle.Dot);
            if (today)
            {
                s.DashTitle = UiText.Tr("Movimiento esperado") + " · " + Underlying;
                s.Row("Hoy (±1σ)", "±" + Price(em), default);
                s.Row("En %", "±" + Opt.Pct(em / prevClose * 100, "0.00"), default);
                s.Row("Rango", Price(prevClose - em) + " – " + Price(prevClose + em), Opt.Flip);
                s.Row("IV usada", Opt.Pct(iv * 100), default);
                double px = Bars[LastBar].C;
                s.Row("Usado del rango", Opt.Pct(Math.Abs(px - prevClose) / em * 100, "0"), Math.Abs(px - prevClose) > em ? Opt.Flip : default);
            }
        }

        if (ConeBars > 0 && c != null && BarLength > TimeSpan.Zero)
        {
            double iv = OptionsMath.AtmIv(c, OptionsMath.ExpiryNear(c, Math.Max(1, ConeBars * BarLength.TotalDays)));
            if (double.IsFinite(iv) && iv > 0)
            {
                double px = Bars[LastBar].C;
                foreach (var k in new[] { 1.0, 2.0 })
                    foreach (var sign in new[] { 1, -1 })
                    {
                        var pts = new List<(int, double)>();
                        for (int b = 0; b <= ConeBars; b += Math.Max(1, ConeBars / 30))
                            pts.Add((LastBar + b, px * Math.Exp(sign * k * iv * Math.Sqrt(b * BarLength.TotalDays / 365))));
                        s.Paths.Add(new Scene.PathShape(pts, Pal.A(sign > 0 ? Opt.Call : Opt.Put, k == 1 ? 170 : 90), 1, k == 1 ? LineDashStyle.Dash : LineDashStyle.Dot));
                    }
            }
        }

        if (s.Lines.Count == 0) Status = OptionsData.LastError is { } err ? "Opciones: " + err : $"Trayendo la volatilidad implícita de {Underlying}…";
    }

    private int NextDayStart(int st)
    {
        int k = st;
        while (k < Bars.Count - 1 && DayKey[k + 1] == DayKey[st]) k++;
        return k + 1;
    }
}

// ============================================================================ probabilidad de un contrato

[DisplayName("Probabilidad de un contrato de opciones")]
[Category(IndicatorCategories.Options)]
[Description("Antes de operar una opción: el contrato elegido (tipo, ejercicio y vencimiento), su prima, el punto de equilibrio, la probabilidad de ganar al vencimiento y de terminar dentro del dinero, y cuántos contratos tomar para arriesgar el monto que digas. Dibuja el ejercicio y el punto de equilibrio.")]
public class ContractOdds : OptionsScene
{
    public enum Way
    {
        [Display(Name = "Comprar")] Buy,
        [Display(Name = "Vender")] Sell,
    }

    [Display(Name = "Tipo", GroupName = "Contrato", Order = 10)] public OptionRight Right { get; set; } = OptionRight.Call;
    [Display(Name = "Operación", GroupName = "Contrato", Order = 11)] public Way Side { get; set; } = Way.Buy;
    [Display(Name = "Ejercicio (0 = por distancia)", GroupName = "Contrato", Order = 12)] [Range(0, 1e9)] public decimal Strike { get; set; }
    [Display(Name = "Distancia del precio (%)", GroupName = "Contrato", Order = 13)] [Range(-50, 50)] public decimal OffsetPct { get; set; } = 5;
    [Display(Name = "Vencimiento", GroupName = "Contrato", Order = 14)] public ExpiryPick Expiry { get; set; } = ExpiryPick.Weekly;
    [Display(Name = "Riesgo por operación (USD)", GroupName = "Contrato", Order = 15)] [Range(1, 1e9)] public decimal Risk { get; set; } = 500;

    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        var exp = Opt.Pick(c, Expiry == ExpiryPick.All ? ExpiryPick.Weekly : Expiry) ?? OptionsMath.ExpiryNear(c, 7);
        double target = Strike > 0 ? (double)Strike : c.Spot * (1 + (double)OffsetPct / 100 * (Right == OptionRight.Call ? 1 : -1));
        var o = c.Contracts.Where(x => x.Expiry == exp && x.Right == Right).OrderBy(x => Math.Abs(x.Strike - target)).FirstOrDefault();
        if (o == null)
        {
            Status = "No hay contratos para ese vencimiento.";
            return;
        }

        double prem = o.Mark > 0 ? o.Mark : (o.Bid + o.Ask) / 2, t = Math.Max(1.0 / 365, (exp - c.Time).TotalDays / 365);
        double iv = o.Iv > 0 ? o.Iv : OptionsMath.AtmIv(c, exp);
        double be = Right == OptionRight.Call ? o.Strike + prem : o.Strike - prem;
        double pAbove = OptionsMath.ProbAbove(c.Spot, be, t, iv), pItm = OptionsMath.ProbAbove(c.Spot, o.Strike, t, iv);
        double win = Right == OptionRight.Call ? pAbove : 1 - pAbove;
        if (Right == OptionRight.Put) pItm = 1 - pItm;
        if (Side == Way.Sell) win = 1 - win;
        double perContract = prem * o.ContractSize;
        // vendida: la pérdida a 2σ (lo que se puede perder si el precio va en contra un movimiento grande)
        double move2 = c.Spot * Math.Exp((Right == OptionRight.Call ? 2 : -2) * iv * Math.Sqrt(t));
        double loss2 = Side == Way.Buy ? perContract : Math.Max(perContract * 0.1, (Right == OptionRight.Call ? Math.Max(0, move2 - o.Strike) : Math.Max(0, o.Strike - move2)) * o.ContractSize - perContract);
        double size = loss2 > 0 ? Math.Floor((double)Risk / loss2 * 100) / 100 : 0;
        int from = Math.Max(0, LastBar - 30);
        var col = Right == OptionRight.Call ? Opt.Call : Opt.Put;
        s.HLine(from, -1, o.Strike, col, 2, LineDashStyle.Solid, "Ejercicio " + Price(o.Strike), "Ejercicio del contrato");
        s.HLine(from, -1, be, Opt.Flip, 1, LineDashStyle.Dash, "Equilibrio " + Price(be), "Punto de equilibrio");
        s.DashTitle = (Side == Way.Buy ? UiText.Tr("Comprar") : UiText.Tr("Vender")) + " " + Opt.Contract(o.Right, o.Strike, o.Expiry);
        s.Row("Prima", Opt.Usd(perContract) + "  (IV " + Opt.N(iv * 100, "0") + " %)", default);
        s.Row("Punto de equilibrio", Price(be), Opt.Flip);
        s.Row("Prob. de ganar al vencer", Opt.Pct(win * 100, "0"), win >= 0.5 ? Opt.Call : Opt.Put);
        s.Row("Prob. dentro del dinero", Opt.Pct(pItm * 100, "0"), default);
        s.Row(Side == Way.Buy ? "Pérdida máxima por contrato" : "Pérdida a 2σ por contrato", Opt.Usd(loss2), Opt.Put);
        s.Row("Contratos para el riesgo", Opt.N(size, "0.##"), default);
        s.Row("Días al vencimiento", Opt.N((exp - c.Time).TotalDays, "0.0"), default);
    }
}

// ============================================================================ postura con opciones

[DisplayName("Postura con opciones")]
[Category(IndicatorCategories.Options)]
[Description("Un cuadro que califica cinco operaciones de opciones comunes (comprar call, comprar put, vender put, vender call cubierta y comprar straddle) como a favor, neutral o en contra, según la tendencia del precio (EMA), qué tan cara está la volatilidad (IV Rank) y el sesgo entre puts y calls.")]
public class OptionStance : OptionsScene
{
    [Display(Name = "Largo de la EMA de tendencia", GroupName = "Cálculo", Order = 10)] [Range(5, 400)] public int EmaLength { get; set; } = 50;

    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        var ema = Ta.Ema(Bars.Select(b => b.C).ToList(), EmaLength);
        double px = Bars[LastBar].C, e = ema[LastBar], e0 = ema[Math.Max(0, LastBar - 10)];
        int trend = px > e && e >= e0 ? 1 : px < e && e <= e0 ? -1 : 0;
        var e30 = OptionsMath.ExpiryNear(c, 30);
        double iv = OptionsMath.AtmIv(c, e30), rr = OptionsMath.RiskReversal25(c, e30);
        var hist = OptionsData.Daily(Underlying).Where(d => d.Date > c.Time.AddDays(-365)).Select(d => d.AtmIv).ToList();
        double rank = Opt.Rank(hist, iv);
        int vol = !double.IsFinite(rank) ? 0 : rank >= 60 ? 1 : rank <= 30 ? -1 : 0; // 1 = cara
        static (string, Color) Verdict(int v) => v > 0 ? ("a favor", Opt.Call) : v < 0 ? ("en contra", Opt.Put) : ("neutral", Pal.Muted);
        int Clamp(int v) => Math.Clamp(v, -1, 1);
        var rows = new (string Name, int Score)[]
        {
            ("Comprar call", Clamp(trend - (vol > 0 ? 1 : 0) + (vol < 0 && trend > 0 ? 1 : 0))),
            ("Comprar put", Clamp(-trend - (vol > 0 ? 1 : 0) + (vol < 0 && trend < 0 ? 1 : 0) + (double.IsFinite(rr) && rr > 2 ? 1 : 0))),
            ("Vender put", Clamp((trend >= 0 ? 1 : -1) + (vol > 0 ? 1 : vol < 0 ? -1 : 0) - (trend < 0 ? 1 : 0))),
            ("Vender call cubierta", Clamp((trend <= 0 ? 1 : -1) + (vol > 0 ? 1 : vol < 0 ? -1 : 0))),
            ("Comprar straddle", Clamp(vol < 0 ? 1 : vol > 0 ? -1 : 0)),
        };
        s.DashTitle = UiText.Tr("Postura") + " · " + Underlying;
        foreach (var r in rows)
        {
            var (t, col) = Verdict(r.Score);
            s.Row(r.Name, t, col);
        }

        s.Row("Tendencia", trend > 0 ? "alcista" : trend < 0 ? "bajista" : "lateral", trend > 0 ? Opt.Call : trend < 0 ? Opt.Put : default);
        s.Row("IV a 30 días · IV Rank", Opt.Pct(iv * 100) + " · " + Opt.N(rank, "0"), default);
        s.Row("Sesgo 25Δ (call − put)", double.IsFinite(rr) ? Opt.N(rr, "+0.0;-0.0") : "—", rr >= 0 ? Opt.Call : Opt.Put);
    }
}

// ============================================================================ tablero de opciones

[DisplayName("Tablero de opciones")]
[Category(IndicatorCategories.Options)]
[Description("Nueve casillas que resumen las opciones del subyacente: IV, IV Rank, sesgo, puts/calls por volumen y por interés abierto, régimen de gamma, distancia al cambio de gamma, movimiento esperado y flujo neto de primas de hoy, con una lectura: si el próximo movimiento tiende a seguir o a frenarse.")]
public class OptionsDashboard : OptionsScene
{
    private readonly List<(string Name, string Value, Color Color)> _tiles = new();
    private string _verdict = "";
    private Color _verdictColor = Pal.Muted;

    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        var e30 = OptionsMath.ExpiryNear(c, 30);
        double iv = OptionsMath.AtmIv(c, e30), rr = OptionsMath.RiskReversal25(c, e30), gex = OptionsMath.TotalGex(c), flip = OptionsMath.GammaFlip(c);
        double em = OptionsMath.ExpectedMove(c, OptionsMath.ExpiryNear(c, 1));
        var day = OptionsMath.DailyOf(c);
        double rank = Opt.Rank(OptionsData.Daily(Underlying).Where(d => d.Date > c.Time.AddDays(-365)).Select(d => d.AtmIv), iv);
        double net = TodayTrades().Sum(t => t.Bias * t.Premium);
        _tiles.Clear();
        _tiles.Add(("IV 30 días", Opt.Pct(iv * 100), default));
        _tiles.Add(("IV Rank", Opt.N(rank, "0"), rank >= 60 ? Opt.Put : rank <= 30 ? Opt.Call : default));
        _tiles.Add(("Sesgo 25Δ", Opt.N(rr, "+0.0;-0.0"), rr >= 0 ? Opt.Call : Opt.Put));
        _tiles.Add(("Puts/calls vol.", Opt.N(day.PutCallVolume, "0.00"), day.PutCallVolume > 1 ? Opt.Put : Opt.Call));
        _tiles.Add(("Puts/calls int.", Opt.N(day.PutCallOi, "0.00"), day.PutCallOi > 1 ? Opt.Put : Opt.Call));
        _tiles.Add(("Gamma", gex >= 0 ? "positiva" : "negativa", gex >= 0 ? Opt.Call : Opt.Put));
        _tiles.Add(("Al cambio de gamma", double.IsFinite(flip) ? Opt.Pct((flip / c.Spot - 1) * 100, "+0.0;-0.0") : "—", Opt.Flip));
        _tiles.Add(("Mov. esperado 1 día", double.IsFinite(em) ? "±" + Opt.Pct(em / c.Spot * 100, "0.00") : "—", default));
        _tiles.Add(("Flujo neto hoy", Opt.SignedUsd(net), Opt.BiasColor(net)));
        var ema = Ta.Ema(Bars.Select(b => b.C).ToList(), 20);
        int trend = Math.Sign(Bars[LastBar].C - ema[LastBar]);
        if (gex < 0 && Math.Sign(net) == trend && trend != 0) (_verdict, _verdictColor) = ("Tiende a seguir: gamma negativa y el flujo acompaña", trend > 0 ? Opt.Call : Opt.Put);
        else if (gex >= 0) (_verdict, _verdictColor) = ("Tiende a frenarse: los creadores de mercado amortiguan", Pal.Muted);
        else (_verdict, _verdictColor) = ("Sin señal clara", Pal.Muted);
    }

    protected override void RenderOptions(RenderContext ctx, Rectangle region)
    {
        if (_tiles.Count == 0 || !ShowDashboard) return;
        var font = new RenderFont("Arial", 8);
        var bold = new RenderFont("Arial", 11, FontStyle.Bold);
        int tw = 104, th = 40, gap = 4;
        int w = 3 * tw + 4 * gap, h = 3 * th + 4 * gap + 22;
        int col = (int)DashboardPosition % 3, row = (int)DashboardPosition / 3;
        int x = col switch { 0 => region.Left + 8, 1 => region.Left + (region.Width - w) / 2, _ => region.Right - w - 8 };
        int y = row switch { 0 => region.Top + 26, 1 => region.Top + (region.Height - h) / 2, _ => region.Bottom - h - 8 };
        ctx.FillRectangle(Color.FromArgb(225, 18, 20, 26), new Rectangle(x, y, w, h), 6);
        for (int k = 0; k < _tiles.Count; k++)
        {
            int cx = x + gap + k % 3 * (tw + gap), cy = y + gap + k / 3 * (th + gap);
            ctx.FillRectangle(Color.FromArgb(160, 32, 36, 46), new Rectangle(cx, cy, tw, th), 4);
            ctx.DrawString(UiText.Tr(_tiles[k].Name), font, Opt.Dim, cx + 6, cy + 4);
            ctx.DrawString(UiText.Tr(_tiles[k].Value), bold, _tiles[k].Color.A > 0 ? _tiles[k].Color : Opt.Text, cx + 6, cy + 18);
        }

        ctx.DrawString(UiText.Tr(_verdict), new RenderFont("Arial", 8, FontStyle.Bold), _verdictColor, x + 8, y + h - 18);
    }
}

// ============================================================================ contexto de volatilidad

[DisplayName("Contexto de volatilidad (opciones)")]
[Category(IndicatorCategories.Volatility)]
[Description("Un cuadro para la apertura del día: si las opciones están caras o baratas contra lo que el precio se movió (IV contra volatilidad realizada), si los operadores temen una caída (sesgo de los puts) y si el corto plazo está tenso (IV de una semana contra la de un mes).")]
public class VolatilityContext : OptionsScene
{
    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        var e30 = OptionsMath.ExpiryNear(c, 30);
        var e7 = OptionsMath.ExpiryNear(c, 7);
        double iv30 = OptionsMath.AtmIv(c, e30), iv7 = OptionsMath.AtmIv(c, e7), rv = RealizedVol(30), rr = OptionsMath.RiskReversal25(c, e30);
        double ratio = rv > 0 ? iv30 / rv : double.NaN, term = iv30 > 0 ? iv7 / iv30 : double.NaN;
        s.DashTitle = UiText.Tr("Contexto de volatilidad") + " · " + Underlying;
        s.Row("IV a 30 días", Opt.Pct(iv30 * 100), default);
        s.Row("Realizada 30 días", Opt.Pct(rv * 100), default);
        s.Row("Opciones", !double.IsFinite(ratio) ? "—" : ratio > 1.25 ? "caras" : ratio < 0.9 ? "baratas" : "en su precio", ratio > 1.25 ? Opt.Put : ratio < 0.9 ? Opt.Call : default);
        s.Row("Miedo a la baja", !double.IsFinite(rr) ? "—" : rr < -3 ? "alto" : rr < -0.5 ? "moderado" : "bajo", rr < -3 ? Opt.Put : default);
        s.Row("Corto plazo (IV 7 d / 30 d)", Opt.N(term, "0.00") + (term > 1.05 ? "  " + UiText.Tr("tenso") : ""), term > 1.05 ? Opt.Flip : default);
    }
}

// ============================================================================ mapa de gamma por vela

[DisplayName("Mapa de gamma por vela")]
[Category(IndicatorCategories.Options)]
[Description("En el fondo de cada vela, un mapa de calor de dónde se cubren más los creadores de mercado (la gamma de cada ejercicio de la foto de ese momento): verde donde amortiguan, rojo donde empujan. Usa las fotos que se guardan cada 30 minutos mientras hay un indicador de opciones abierto: el pasado no se inventa.")]
public class SpotExposures : OptionsScene
{
    private int[] _snapOf = Array.Empty<int>();
    private List<StrikeSnapshot> _snaps = new();
    private double _max;

    [Display(Name = "Intensidad", GroupName = "Dibujo", Order = 20)] [Range(10, 255)] public int Alpha { get; set; } = 90;

    public SpotExposures() => ShowDashboard = false;

    protected override void BuildOptions(Scene s)
    {
        _ = GetChain();
        Status = null;
        _snaps = OptionsData.Snapshots(Underlying).OrderBy(x => x.Time).ToList();
        _snapOf = new int[Bars.Count];
        _max = 0;
        int k = -1;
        for (int i = 0; i < Bars.Count; i++)
        {
            var end = EndOf(i);
            while (k + 1 < _snaps.Count && _snaps[k + 1].Time < end) k++;
            _snapOf[i] = k >= 0 && end - _snaps[k].Time < TimeSpan.FromHours(6) ? k : -1;
        }

        foreach (var sn in _snaps)
            foreach (var r in sn.Rows)
                if (Math.Abs(r.Strike / sn.Spot - 1) < 0.2) _max = Math.Max(_max, Math.Abs(r.Gex));
        if (_snaps.Count == 0) Status = "Todavía no hay fotos de la cadena: se toma una cada 30 minutos con el indicador abierto.";
    }

    protected override void RenderOptions(RenderContext ctx, Rectangle region)
    {
        if (_max <= 0 || ChartInfo == null) return;
        int first = Math.Max(0, FirstVisibleBarNumber - Offset), last = Math.Min(Bars.Count - 1, LastVisibleBarNumber - Offset);
        for (int i = first; i <= last; i++)
        {
            int k = _snapOf[i];
            if (k < 0) continue;
            var sn = _snaps[k];
            int x0 = X(i), x1 = X(i + 1);
            var rows = sn.Rows.Where(r => Math.Abs(r.Strike / sn.Spot - 1) < 0.2).ToList();
            for (int j = 0; j < rows.Count; j++)
            {
                var r = rows[j];
                double f = Math.Abs(r.Gex) / _max;
                if (f < 0.04) continue;
                double half = (j + 1 < rows.Count ? rows[j + 1].Strike - r.Strike : j > 0 ? r.Strike - rows[j - 1].Strike : sn.Spot * 0.005) * 0.4;
                int y0 = Y(r.Strike + half), y1 = Y(r.Strike - half);
                ctx.FillRectangle(Pal.A(r.Gex >= 0 ? Opt.Call : Opt.Put, (int)(Alpha * Math.Sqrt(f))), new Rectangle(x0, Math.Min(y0, y1), Math.Max(1, x1 - x0), Math.Max(1, Math.Abs(y1 - y0))));
            }
        }
    }
}

// ============================================================================ cambios del interés abierto

[DisplayName("Cambios del interés abierto de opciones")]
[Category(IndicatorCategories.Derivatives)]
[Description("Cuánto interés abierto de opciones se abrió (+) o se cerró (−) en cada ejercicio desde la foto de hace unas horas (24 por defecto): barras pegadas a la derecha (calls a la derecha, puts a la izquierda), para ver dónde se están armando posiciones nuevas.")]
public class OiChanges : OptionsScene
{
    private List<(double Strike, double Call, double Put)> _rows = new();
    private double _step;

    [Display(Name = "Comparar con la foto de hace (horas)", GroupName = "Cálculo", Order = 10)] [Range(1, 168)] public int Hours { get; set; } = 24;
    [Display(Name = "Ancho de las barras (px)", GroupName = "Dibujo", Order = 20)] [Range(40, 600)] public int Width { get; set; } = 160;

    protected override void BuildOptions(Scene s)
    {
        var c = GetChain();
        if (c == null) return;
        var now = OptionsData.SnapshotOf(c);
        var old = OptionsData.Snapshots(Underlying).Where(x => x.Time <= c.Time.AddHours(-Hours)).OrderByDescending(x => x.Time).FirstOrDefault();
        if (old == null)
        {
            Status = $"Hace falta una foto de hace {Hours} h: se guarda una cada 30 minutos con el indicador abierto.";
            return;
        }

        var before = old.Rows.ToDictionary(r => r.Strike);
        _rows = now.Rows.Where(r => Math.Abs(r.Strike / c.Spot - 1) < 0.35)
            .Select(r => (r.Strike, r.CallOi - (before.TryGetValue(r.Strike, out var b) ? b.CallOi : 0), r.PutOi - (before.TryGetValue(r.Strike, out var b2) ? b2.PutOi : 0))).ToList();
        _step = _rows.Count > 1 ? _rows.Zip(_rows.Skip(1), (a, b) => b.Strike - a.Strike).Where(d => d > 0).DefaultIfEmpty(c.Spot * 0.01).Min() : c.Spot * 0.01;
        s.DashTitle = UiText.Tr("Interés abierto desde") + " " + old.Time.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
        s.Row("Calls", MacroSigned(_rows.Sum(r => r.Call)), Opt.Call);
        s.Row("Puts", MacroSigned(_rows.Sum(r => r.Put)), Opt.Put);
        var top = _rows.OrderByDescending(r => r.Call + r.Put).FirstOrDefault();
        if (top.Strike > 0 && top.Call + top.Put > 0) s.Row("Más abierto en", Price(top.Strike), default);
    }

    private static string MacroSigned(double v) => (v > 0 ? "+" : "") + Opt.N(v, "0");

    protected override void RenderOptions(RenderContext ctx, Rectangle region)
    {
        if (_rows.Count == 0) return;
        double max = _rows.Max(r => Math.Max(Math.Abs(r.Call), Math.Abs(r.Put)));
        if (max <= 0) return;
        int edge = region.Right - 4, mid = edge - Width / 2;
        int h = Math.Clamp(Math.Abs(Y(_rows[0].Strike) - Y(_rows[0].Strike + _step)) - 1, 2, 14);
        ctx.FillRectangle(Color.FromArgb(70, 14, 17, 24), new Rectangle(edge - Width - 2, region.Top, Width + 4, region.Height));
        foreach (var r in _rows)
        {
            int cy = Y(r.Strike);
            if (cy < region.Top || cy > region.Bottom) continue;
            int wc = (int)(Width / 2.0 * Math.Abs(r.Call) / max), wp = (int)(Width / 2.0 * Math.Abs(r.Put) / max);
            if (wc > 0) ctx.FillRectangle(Pal.A(Opt.Call, r.Call >= 0 ? 210 : 80), new Rectangle(mid, cy - h / 2, wc, h));
            if (wp > 0) ctx.FillRectangle(Pal.A(Opt.Put, r.Put >= 0 ? 210 : 80), new Rectangle(mid - wp, cy - h / 2, wp, h));
        }

        ctx.DrawLine(new RenderPen(Color.FromArgb(90, 160, 165, 175), 1), mid, region.Top, mid, region.Bottom);
    }
}

// ============================================================================ tipo de día

[DisplayName("Tipo de día")]
[Category(IndicatorCategories.Sessions)]
[Description("Qué clase de sesión es y cuánto suele durar. Cada 10 minutos nombra un estado con la última hora de precios: Clavado (rango estrecho), En tendencia (sigue en una dirección), Volátil (rango amplio) o Indeciso. Los rangos se miden contra el movimiento que esperan las opciones (1σ con la IV de 1 día; sin opciones conectadas, con la volatilidad realizada). El cuadro dice cuántas veces el estado siguió 30 minutos a 2 horas después y cuánto se movió el precio; una caja delante del precio muestra ese rango. Si hay opciones, marca el cambio de gamma y los muros.")]
public class DayType : SceneIndicator
{
    public enum State
    {
        [Display(Name = "Indeciso")] Unsettled,
        [Display(Name = "Clavado")] Pinned,
        [Display(Name = "En tendencia")] Trending,
        [Display(Name = "Volátil")] Volatile,
    }

    private readonly Action _changed;
    private long _gen = -1;
    private readonly List<(int Bar, State State)> _ribbon = new();

    public DayType()
    {
        DashboardPosition = DashCorner.BottomRight;
        _changed = () => DoActionInGuiThread(() =>
        {
            if (OptionsData.Generation != _gen) RecalculateValues();
        });
    }

    [Display(Name = "Ventana (muestras de 10 min)", GroupName = "Cálculo", Order = 10)] [Range(2, 60)] public int Window { get; set; } = 6;
    [Display(Name = "Volátil desde (× 1σ)", GroupName = "Cálculo", Order = 11)] [Range(0.2, 10)] public decimal VolatileRatio { get; set; } = 1.5m;
    [Display(Name = "Clavado por debajo de (× 1σ)", GroupName = "Cálculo", Order = 12)] [Range(0.05, 5)] public decimal PinnedRatio { get; set; } = 0.6m;
    [Display(Name = "Eficiencia de tendencia", GroupName = "Cálculo", Order = 13)] [Range(0.1, 1)] public decimal TrendEfficiency { get; set; } = 0.6m;
    [Display(Name = "Casos mínimos", GroupName = "Cálculo", Order = 14)] [Range(1, 1000)] public int MinOutcomes { get; set; } = 20;
    [Display(Name = "Niveles de opciones", GroupName = "Dibujo", Order = 20)] public bool ShowLevels { get; set; } = true;
    [Display(Name = "Cinta de estados", GroupName = "Dibujo", Order = 21)] public bool ShowRibbon { get; set; } = true;

    protected override void OnInitialize()
    {
        base.OnInitialize();
        OptionsData.Changed += _changed;
    }

    protected override void OnDispose()
    {
        base.OnDispose();
        OptionsData.Changed -= _changed;
    }

    private static string Name(State s) => s switch { State.Pinned => "Clavado", State.Trending => "En tendencia", State.Volatile => "Volátil", _ => "Indeciso" };

    protected override void Build(Scene s)
    {
        _gen = OptionsData.Generation;
        _ribbon.Clear();
        int n = Bars.Count;
        if (n < 10) return;
        string under = OptionsData.UnderlyingOf(InstrumentInfo?.Instrument);
        var chain = OptionsData.Unavailable(under) == null ? OptionsData.Chain(under, TimeSpan.FromMinutes(10)) : null;
        var daily = OptionsData.Unavailable(under) == null ? OptionsData.Daily(under) : Array.Empty<OptionDaily>();
        string sym = (InstrumentInfo?.Instrument ?? "").ToUpperInvariant();
        bool crypto = sym.EndsWith("USDT", StringComparison.Ordinal) || sym.EndsWith("USDC", StringComparison.Ordinal) || sym.Contains("BTC") || sym.Contains("ETH");
        double minutesYear = crypto ? 365 * 1440 : 252 * 390;

        // muestras de 10 minutos con el cierre de cada una (de las velas del gráfico)
        var samples = new List<(int Bar, DateTime T, double Px, int Day)>();
        long bucket = long.MinValue;
        for (int i = 0; i < n; i++)
        {
            long b = Bars[i].T / 600;
            if (b != bucket)
            {
                samples.Add((i, TimeOf(i), Bars[i].C, DayKey[i]));
                bucket = b;
            }
            else samples[^1] = (i, samples[^1].T, Bars[i].C, DayKey[i]);
        }

        // volatilidad realizada por muestra (si no hay IV de opciones)
        var rets = new List<double>();
        for (int k = 1; k < samples.Count; k++) rets.Add(Math.Log(samples[k].Px / samples[k - 1].Px));
        double rvAnnual = rets.Count > 20 ? Math.Sqrt(rets.Sum(r => r * r) / rets.Count * (minutesYear / 10)) : 0.5;
        double liveIv = chain != null ? OptionsMath.AtmIv(chain, OptionsMath.ExpiryNear(chain, 1)) : double.NaN;

        var states = new State[samples.Count];
        int w = Window;
        for (int k = 0; k < samples.Count; k++)
        {
            states[k] = State.Unsettled;
            if (k < w || samples[k - w].Day != samples[k].Day) continue;
            double hi = double.MinValue, lo = double.MaxValue, path = 0;
            for (int j = k - w; j <= k; j++)
            {
                hi = Math.Max(hi, samples[j].Px);
                lo = Math.Min(lo, samples[j].Px);
                if (j > k - w) path += Math.Abs(samples[j].Px - samples[j - 1].Px);
            }

            double iv = Opt.DayOf(daily, samples[k].T.Date.AddDays(-1))?.AtmIv ?? double.NaN;
            if (k == samples.Count - 1 && double.IsFinite(liveIv)) iv = liveIv;
            if (!double.IsFinite(iv) || iv <= 0) iv = rvAnnual;
            double sigma = samples[k].Px * iv * Math.Sqrt(w * 10 / minutesYear);
            double range = hi - lo, eff = path > 0 ? Math.Abs(samples[k].Px - samples[k - w].Px) / path : 0;
            states[k] = range >= (double)VolatileRatio * sigma ? State.Volatile
                : eff >= (double)TrendEfficiency && range >= (double)PinnedRatio * sigma ? State.Trending
                : range < (double)PinnedRatio * sigma ? State.Pinned
                : State.Unsettled;
            _ribbon.Add((samples[k].Bar, states[k]));
        }

        // cuántas veces el mismo estado siguió 30, 60, 90 y 120 minutos después (contado, no modelado)
        int last = samples.Count - 1;
        var cur = states[last];
        var horizons = new[] { 3, 6, 9, 12 };
        var odds = new double[4];
        var moves = new double[4];
        var counts = new int[4];
        for (int h = 0; h < 4; h++)
        {
            int held = 0, cnt = 0;
            double mv = 0;
            for (int k = w; k + horizons[h] < last; k++)
            {
                if (states[k] != cur || samples[k + horizons[h]].Day != samples[k].Day) continue;
                cnt++;
                if (states[k + horizons[h]] == cur) held++;
                mv += Math.Abs(samples[k + horizons[h]].Px / samples[k].Px - 1) * 100;
            }

            counts[h] = cnt;
            odds[h] = cnt >= MinOutcomes ? 100.0 * held / cnt : double.NaN;
            moves[h] = cnt >= MinOutcomes ? mv / cnt : double.NaN;
        }

        int longest = Enumerable.Range(0, 4).Where(h => double.IsFinite(moves[h])).DefaultIfEmpty(-1).Max();
        if (longest >= 0)
        {
            double px = Bars[n - 1].C, mv = moves[longest] / 100 * px;
            int ahead = Math.Max(4, (int)(horizons[longest] * 10 / Math.Max(1, BarLength.TotalMinutes)));
            s.Box(n - 1, n - 1 + ahead, px + mv, px - mv, Pal.A(Pal.Neutral, 35), Pal.A(Pal.Neutral, 120), UiText.Tr("rango típico"), default, LineDashStyle.Dash);
        }

        if (ShowLevels && chain != null)
        {
            double flip = OptionsMath.GammaFlip(chain);
            var (cw, pw) = OptionsMath.Walls(chain);
            int from = DayKey.Length > 0 ? Array.IndexOf(DayKey, DayKey[n - 1]) : 0;
            if (double.IsFinite(flip)) s.HLine(from, -1, flip, Opt.Flip, 1, LineDashStyle.Dash, "Cambio de gamma", "Cambio de gamma");
            if (double.IsFinite(cw)) s.HLine(from, -1, cw, Opt.Call, 1, LineDashStyle.Dash, "Muro de calls", "Muro de calls");
            if (double.IsFinite(pw)) s.HLine(from, -1, pw, Opt.Put, 1, LineDashStyle.Dash, "Muro de puts", "Muro de puts");
        }

        double strong = odds[1];
        s.DashTitle = UiText.Tr("Tipo de día") + ": " + UiText.Tr(Name(cur));
        s.Row("Firmeza (1 h)", !double.IsFinite(strong) ? "pocos casos" : strong >= 75 ? "firme" : strong >= 50 ? "media" : "débil", strong >= 75 ? Opt.Call : default);
        string[] hn = { "30 min", "1 h", "1 h 30", "2 h" };
        for (int h = 0; h < 4; h++)
            s.Row($"Sigue a {hn[h]}", double.IsFinite(odds[h]) ? Opt.Pct(odds[h], "0") + " · ±" + Opt.Pct(moves[h], "0.00") : $"— ({counts[h]})", default);
        s.Row("Volatilidad", chain != null && double.IsFinite(liveIv) ? "IV de opciones" : "realizada", default);
    }

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (!ShowRibbon || _ribbon.Count == 0 || ChartInfo == null) return;
        int first = FirstVisibleBarNumber - Offset, last = LastVisibleBarNumber - Offset;
        for (int k = 0; k < _ribbon.Count; k++)
        {
            var (bar, st) = _ribbon[k];
            if (bar < first || bar > last) continue;
            int next = k + 1 < _ribbon.Count ? _ribbon[k + 1].Bar : bar + 1;
            var col = st switch
            {
                State.Volatile => Color.FromArgb(200, 239, 83, 80),
                State.Trending => Color.FromArgb(200, 41, 98, 255),
                State.Pinned => Color.FromArgb(200, 230, 232, 238),
                _ => Color.FromArgb(110, 120, 123, 134),
            };
            int x0 = X(bar), x1 = X(Math.Min(next, bar + 50));
            ctx.FillRectangle(col, new Rectangle(x0, region.Bottom - 6, Math.Max(1, x1 - x0), 5));
        }
    }
}
