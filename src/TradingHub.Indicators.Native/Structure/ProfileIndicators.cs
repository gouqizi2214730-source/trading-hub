using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Structure;

// Perfil de volumen (sección 07) y VWAP (sección 08): reversión al área de valor, alertas de niveles del perfil, colas del
// perfil, perfil con área de valor y mapa de horas, horas calientes, perfil deslizante, VWAP anclado al calendario y la
// evaluación de ejecución (TWAP / VWAP / POV).

/// <summary>Perfil de volumen simple (filas iguales) con POC y área de valor.</summary>
public static class Profile
{
    public sealed class Result
    {
        public double Low, Step;
        public double[] Rows = Array.Empty<double>();
        public int PocRow;
        public double Poc, Vah, Val;
    }

    /// <summary>Reparte el volumen de cada vela entre las filas que toca y arma POC y área de valor (crece hacia la fila vecina más cargada).</summary>
    public static Result? Build(IReadOnlyList<Bar> b, int from, int to, int rows, double vaPct)
    {
        if (to < from || rows < 2) return null;
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = from; i <= to; i++)
        {
            lo = Math.Min(lo, b[i].L);
            hi = Math.Max(hi, b[i].H);
        }

        if (!(hi > lo)) return null;
        var r = new Result { Low = lo, Step = (hi - lo) / rows, Rows = new double[rows] };
        for (int i = from; i <= to; i++)
        {
            int a = Math.Clamp((int)((b[i].L - lo) / r.Step), 0, rows - 1), z = Math.Clamp((int)((b[i].H - lo) / r.Step), 0, rows - 1);
            double share = b[i].V / (z - a + 1);
            for (int k = a; k <= z; k++) r.Rows[k] += share;
        }

        double total = r.Rows.Sum();
        int poc = 0;
        for (int k = 1; k < rows; k++)
            if (r.Rows[k] > r.Rows[poc]) poc = k;
        int up = poc, dn = poc;
        double acc = r.Rows[poc];
        while (acc < total * vaPct / 100 && (up < rows - 1 || dn > 0))
        {
            double above = up < rows - 1 ? r.Rows[up + 1] : -1, below = dn > 0 ? r.Rows[dn - 1] : -1;
            if (above >= below)
            {
                up++;
                acc += above;
            }
            else
            {
                dn--;
                acc += below;
            }
        }

        r.PocRow = poc;
        r.Poc = lo + (poc + 0.5) * r.Step;
        r.Vah = lo + (up + 1) * r.Step;
        r.Val = lo + dn * r.Step;
        return r;
    }
}

/// <summary>Reversión al área de valor: sale del área y vuelve a cerrar adentro (sesión anterior y en formación).</summary>
[DisplayName("Reversión al área de valor")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("Un perfil de volumen por sesión (de 18:00 a 18:00 de Nueva York). Cuando el precio sale del área de valor y en pocas velas vuelve a cerrar adentro, marca la reversión: contra el área de la sesión anterior (triángulos) y contra la de la sesión en formación (círculos).")]
public class ValueAreaReversion : SceneIndicator
{
    private readonly PaintbarsDataSeries _paint = new("VARPaint", "Velas") { IsHidden = true };

    [Display(Name = "Señales con la sesión anterior", GroupName = "Señales", Order = 10)] public bool PrevSession { get; set; } = true;
    [Display(Name = "Señales con la sesión en formación", GroupName = "Señales", Order = 11)] public bool CurrentSession { get; set; }
    [Display(Name = "Velas como máximo después de salir", GroupName = "Señales", Order = 12)] [Range(1, 100)] public int MaxBars { get; set; } = 10;
    [Display(Name = "Filas del perfil", GroupName = "Perfil", Order = 20)] [Range(10, 400)] public int Rows { get; set; } = 60;
    [Display(Name = "Área de valor (%)", GroupName = "Perfil", Order = 21)] [Range(10, 99)] public int ValueArea { get; set; } = 70;
    [Display(Name = "Pintar las velas de la reversión", GroupName = "Dibujo", Order = 30)] public bool ColorBars { get; set; } = true;
    [Display(Name = "Alcista", GroupName = "Colores", Order = 40)] public Color BullColor { get; set; } = Pal.Bull;
    [Display(Name = "Bajista", GroupName = "Colores", Order = 41)] public Color BearColor { get; set; } = Pal.Bear;

    public ValueAreaReversion() => DataSeries.Add(_paint);

    private static int SessionKey(DateTime utc)
    {
        var ny = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), SessionClock.Zone(SessionZone.NewYork));
        return (int)(ny.AddHours(6).Date - DateTime.UnixEpoch.Date).TotalDays; // la sesión empieza a las 18:00
    }

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < 20) return;
        var keys = new int[Bars.Count];
        for (int i = 0; i < Bars.Count; i++) keys[i] = SessionKey(TimeOf(i));
        int sessStart = 0, prevStart = -1;
        Profile.Result? prev = null;
        // máquinas: 0 adentro, 1 salió arriba, −1 salió abajo (con la vela de la salida)
        int[] state = { 0, 0 };
        int[] outBar = { -1, -1 };
        var latch = new int[Bars.Count];
        int bulls = 0, bears = 0;
        for (int i = 1; i <= last; i++)
        {
            if (keys[i] != keys[i - 1])
            {
                prev = Profile.Build(Bars, sessStart, i - 1, Rows, ValueArea);
                prevStart = sessStart;
                sessStart = i;
                state[0] = state[1] = 0;
                if (prev != null)
                {
                    int end = Math.Min(LastBar, i + Math.Max(1, i - prevStart) - 1);
                    s.HLine(i, end, prev.Vah, Pal.A(BearColor, 150), 1, LineDashStyle.Dash);
                    s.HLine(i, end, prev.Val, Pal.A(BullColor, 150), 1, LineDashStyle.Dash);
                    s.HLine(i, end, prev.Poc, Pal.A(Pal.Amber, 170), 1, LineDashStyle.Dot);
                }
            }

            var b = Bars[i];
            for (int m = 0; m < 2; m++)
            {
                if (m == 0 ? !PrevSession || prev == null : !CurrentSession || i - sessStart < 5) continue;
                var va = m == 0 ? prev! : Profile.Build(Bars, sessStart, i - 1, Rows, ValueArea);
                if (va == null) continue;
                if (state[m] == 0)
                {
                    if (b.C > va.Vah) { state[m] = 1; outBar[m] = i; }
                    else if (b.C < va.Val) { state[m] = -1; outBar[m] = i; }
                }
                else if (i - outBar[m] > MaxBars) state[m] = b.C > va.Vah ? 1 : b.C < va.Val ? -1 : 0;
                else if (state[m] == 1 && b.C < va.Vah && b.C > va.Val)
                {
                    s.Mark(i, b.H, m == 0 ? MarkKind.TriangleDown : MarkKind.Circle, BearColor, m == 0 ? "VAH" : null);
                    latch[i] = -1;
                    bears++;
                    state[m] = 0;
                }
                else if (state[m] == -1 && b.C > va.Val && b.C < va.Vah)
                {
                    s.Mark(i, b.L, m == 0 ? MarkKind.TriangleUp : MarkKind.Circle, BullColor, m == 0 ? "VAL" : null, false);
                    latch[i] = 1;
                    bulls++;
                    state[m] = 0;
                }
            }
        }

        if (prev != null)
        {
            s.HLine(sessStart, -1, prev.Vah, BearColor, 1, LineDashStyle.Dash, "VAH anterior " + Price(prev.Vah), "VAH de la sesión anterior");
            s.HLine(sessStart, -1, prev.Val, BullColor, 1, LineDashStyle.Dash, "VAL anterior " + Price(prev.Val), "VAL de la sesión anterior");
            s.HLine(sessStart, -1, prev.Poc, Pal.Amber, 1, LineDashStyle.Dot, "POC anterior " + Price(prev.Poc), "POC de la sesión anterior");
        }

        for (int i = 0; i < Bars.Count; i++) _paint[i + Offset] = ColorBars && latch[i] != 0 ? (latch[i] > 0 ? BullColor : BearColor) : null;
        s.DashTitle = "Reversión al área de valor";
        s.Row("Reversiones alcistas / bajistas", $"{bulls} / {bears}");
        if (prev != null) s.Row("Área de valor anterior", $"{Price(prev.Val)} – {Price(prev.Vah)}");
    }
}

/// <summary>POC, VAH y VAL de cada día o semana como series (para alertas) y las marcas donde el cierre las cruza.</summary>
[DisplayName("Alertas de niveles del perfil (POC, VAH, VAL)")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("El POC, el VAH y el VAL del perfil de volumen de cada día o semana (UTC), del período en formación o del anterior, como líneas a las que se les pone una alerta, y una marca cuando el cierre cruza uno.")]
public class ProfileLevelAlerts : Indicator
{
    public enum ProfilePeriod
    {
        [Display(Name = "Día")] Day,
        [Display(Name = "Semana")] Week,
    }

    public enum LevelsFrom
    {
        [Display(Name = "Este período (en formación)")] ThisPeriod,
        [Display(Name = "El período anterior")] Previous,
    }

    private readonly ValueDataSeries _poc = new("POC", "POC") { Color = Pal.Amber, Width = 2, ShowZeroValue = false, VisualType = VisualMode.Steps };
    private readonly ValueDataSeries _vah = new("VAH", "VAH") { Color = Pal.Bear, ShowZeroValue = false, VisualType = VisualMode.Steps };
    private readonly ValueDataSeries _val = new("VAL", "VAL") { Color = Pal.Bull, ShowZeroValue = false, VisualType = VisualMode.Steps };
    private readonly List<(int Bar, bool Up, decimal Price)> _crosses = new();
    private ProfilePeriod _period = ProfilePeriod.Day;
    private LevelsFrom _from = LevelsFrom.ThisPeriod;
    private int _va = 70, _ticks = 5;
    private int _done = -1;

    public ProfileLevelAlerts() : base(true)
    {
        DataSeries[0] = _poc;
        DataSeries.Add(_vah);
        DataSeries.Add(_val);
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Período", GroupName = "Cálculo", Order = 10)] public ProfilePeriod Period { get => _period; set { _period = value; RecalculateValues(); } }
    [Display(Name = "Niveles de", GroupName = "Cálculo", Order = 11)] public LevelsFrom From { get => _from; set { _from = value; RecalculateValues(); } }
    [Display(Name = "Área de valor (%)", GroupName = "Cálculo", Order = 12)] [Range(10, 99)] public int ValueArea { get => _va; set { _va = value; RecalculateValues(); } }
    [Display(Name = "Ticks por fila", GroupName = "Cálculo", Order = 13)] [Range(1, 1000)] public int TicksPerRow { get => _ticks; set { _ticks = value; RecalculateValues(); } }
    [Display(Name = "Marcar los cruces", GroupName = "Dibujo", Order = 20)] public bool ShowCrosses { get; set; } = true;

    private readonly SortedDictionary<decimal, decimal> _rows = new();
    private (decimal Poc, decimal Vah, decimal Val)? _prevLv;
    private int _periodStart;
    private decimal _hi, _lo;

    protected override void OnRecalculate()
    {
        _rows.Clear();
        _crosses.Clear();
        _prevLv = null;
        _periodStart = 0;
        _done = -1;
    }

    private bool NewPeriod(int bar) => bar > 0 && (_period == ProfilePeriod.Week ? IsNewWeek(bar) : IsNewSession(bar));

    private (decimal Poc, decimal Vah, decimal Val)? Levels()
    {
        if (_rows.Count == 0) return null;
        decimal step = (InstrumentInfo?.TickSize > 0 ? InstrumentInfo.TickSize : 0.01m) * _ticks;
        var keys = _rows.Keys.ToList();
        var vols = keys.Select(k => _rows[k]).ToList();
        int poc = 0;
        for (int k = 1; k < vols.Count; k++)
            if (vols[k] > vols[poc]) poc = k;
        decimal total = vols.Sum(), acc = vols[poc];
        int up = poc, dn = poc;
        while (acc < total * _va / 100 && (up < vols.Count - 1 || dn > 0))
        {
            decimal above = up < vols.Count - 1 ? vols[up + 1] : -1, below = dn > 0 ? vols[dn - 1] : -1;
            if (above >= below) acc += vols[++up];
            else acc += vols[--dn];
        }

        decimal Mid(int k) => Math.Clamp(keys[k] + step / 2, _lo, _hi);
        return (Mid(poc), Mid(up), Mid(dn));
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar <= _done) return; // lo cerrado no se recalcula (la vela en formación sí, abajo)
        bool closed = bar < CurrentBar - 1;
        // la vela en formación trabaja sobre una copia: lo guardado queda como estaba
        var saved = closed ? null : new SortedDictionary<decimal, decimal>(_rows);
        var savedPrev = _prevLv;
        int savedStart = _periodStart;
        decimal hi0 = _hi, lo0 = _lo;
        if (NewPeriod(bar))
        {
            _prevLv = Levels();
            _rows.Clear();
            _periodStart = bar;
            _hi = 0;
            _lo = decimal.MaxValue;
        }

        var c = GetCandle(bar);
        decimal step = (InstrumentInfo?.TickSize > 0 ? InstrumentInfo.TickSize : 0.01m) * _ticks;
        _hi = Math.Max(_hi, c.High);
        _lo = Math.Min(_lo == 0 ? decimal.MaxValue : _lo, c.Low);
        var levels = c.GetAllPriceLevels().ToList();
        if (levels.Count == 0) levels.Add(new PriceVolumeInfo { Price = c.Close, Volume = c.Volume });
        foreach (var l in levels)
        {
            decimal k = Math.Floor(l.Price / step) * step;
            _rows[k] = (_rows.TryGetValue(k, out var v) ? v : 0) + l.Volume;
        }

        var lv = _from == LevelsFrom.Previous ? _prevLv : Levels();
        if (lv is { } x)
        {
            _poc[bar] = x.Poc;
            _vah[bar] = x.Vah;
            _val[bar] = x.Val;
            if (bar > 0 && bar != _periodStart && closed)
            {
                decimal pc = GetCandle(bar - 1).Close;
                foreach (var p in new[] { x.Poc, x.Vah, x.Val })
                {
                    // los dos cierres contra el mismo nivel del período
                    if (pc <= p && c.Close > p) _crosses.Add((bar, true, p));
                    else if (pc >= p && c.Close < p) _crosses.Add((bar, false, p));
                }
            }
        }

        if (closed) _done = bar;
        else
        {
            // la vela en formación no deja nada guardado
            _rows.Clear();
            foreach (var kv in saved!) _rows[kv.Key] = kv.Value;
            _prevLv = savedPrev;
            _periodStart = savedStart;
            _hi = hi0;
            _lo = lo0;
        }
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (!ShowCrosses || ChartInfo == null) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        foreach (var (bar, up, p) in _crosses)
        {
            if (bar < first || bar > last) continue;
            int x = ChartInfo.GetXByBar(bar, false), y = ChartInfo.GetYByPrice(p, false);
            var c = up ? Pal.Bull : Pal.Bear;
            ctx.FillPolygon(c, up ? new[] { new Point(x, y - 4), new Point(x + 5, y + 4), new Point(x - 5, y + 4) } : new[] { new Point(x, y + 4), new Point(x + 5, y - 4), new Point(x - 5, y - 4) });
        }
    }
}

/// <summary>Las colas del perfil: las zonas de poco volumen arriba (oferta) y abajo (demanda) del perfil reciente.</summary>
[DisplayName("Colas del perfil (zonas de poco volumen)")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("Con el perfil de las últimas N velas marca las colas: las filas de arriba y de abajo con volumen menor que un porcentaje del POC. La de arriba es una zona de oferta y la de abajo una de demanda, vela a vela.")]
public class ProfileTails : Indicator
{
    private readonly ValueDataSeries _sLo = new("SLO", "Oferta (abajo)") { Color = Pal.Bear, ShowZeroValue = false };
    private readonly ValueDataSeries _sHi = new("SHI", "Oferta (arriba)") { Color = Pal.Bear, ShowZeroValue = false };
    private readonly ValueDataSeries _dLo = new("DLO", "Demanda (abajo)") { Color = Pal.Bull, ShowZeroValue = false };
    private readonly ValueDataSeries _dHi = new("DHI", "Demanda (arriba)") { Color = Pal.Bull, ShowZeroValue = false };
    private int _lookback = 200, _rows = 50;
    private decimal _threshold = 10;

    public ProfileTails() : base(true)
    {
        DataSeries[0] = _sLo;
        DataSeries.Add(_sHi);
        DataSeries.Add(_dLo);
        DataSeries.Add(_dHi);
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Historical);
    }

    [Display(Name = "Velas del perfil", GroupName = "Cálculo", Order = 10)] [Range(10, 5000)] public int Lookback { get => _lookback; set { _lookback = value; RecalculateValues(); } }
    [Display(Name = "Filas", GroupName = "Cálculo", Order = 11)] [Range(5, 400)] public int Rows { get => _rows; set { _rows = value; RecalculateValues(); } }
    [Display(Name = "Umbral (% del POC)", GroupName = "Cálculo", Order = 12)] [Range(1, 90)] public decimal Threshold { get => _threshold; set { _threshold = value; RecalculateValues(); } }
    [Display(Name = "Rellenar las zonas", GroupName = "Dibujo", Order = 20)] public bool Fill { get; set; } = true;

    protected override void OnCalculate(int bar, decimal value)
    {
        int from = Math.Max(0, bar - _lookback + 1);
        if (bar - from < 5) return;
        var bars = new List<Bar>(bar - from + 1);
        for (int i = from; i <= bar; i++)
        {
            var c = GetCandle(i);
            bars.Add(new Bar(0, 0, (double)c.High, (double)c.Low, 0, (double)c.Volume));
        }

        var p = Profile.Build(bars, 0, bars.Count - 1, _rows, 70);
        if (p == null) return;
        double max = p.Rows.Max(), thr = max * (double)_threshold / 100;
        int top = p.Rows.Length - 1;
        while (top > p.PocRow && p.Rows[top] < thr) top--;
        int bot = 0;
        while (bot < p.PocRow && p.Rows[bot] < thr) bot++;
        double hi = p.Low + p.Rows.Length * p.Step;
        if (top < p.Rows.Length - 1)
        {
            _sLo[bar] = (decimal)(p.Low + (top + 1) * p.Step);
            _sHi[bar] = (decimal)hi;
        }
        else _sLo[bar] = _sHi[bar] = 0;

        if (bot > 0)
        {
            _dLo[bar] = (decimal)p.Low;
            _dHi[bar] = (decimal)(p.Low + bot * p.Step);
        }
        else _dLo[bar] = _dHi[bar] = 0;
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (!Fill || ChartInfo == null) return;
        int first = Math.Max(0, FirstVisibleBarNumber), last = Math.Min(LastVisibleBarNumber, CurrentBar - 1);
        int bw = Math.Max(1, ChartInfo.PriceChartContainer.BarsWidth);
        for (int i = first; i <= last; i++)
        {
            int x = ChartInfo.GetXByBar(i);
            foreach (var (lo, hi, c) in new[] { (_sLo[i], _sHi[i], Pal.Bear), (_dLo[i], _dHi[i], Pal.Bull) })
            {
                if (lo == 0 || hi == 0) continue;
                int y0 = ChartInfo.GetYByPrice(hi, false), y1 = ChartInfo.GetYByPrice(lo, false);
                ctx.FillRectangle(Pal.A(c, 28), new Rectangle(x, Math.Min(y0, y1), bw + 1, Math.Max(1, Math.Abs(y1 - y0))));
            }
        }
    }
}

/// <summary>El perfil de las últimas N velas pegado al eje de precios, con POC, VAH, VAL y un mapa de volumen por hora y día.</summary>
[DisplayName("Perfil de volumen y área de valor (con mapa por hora)")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("El perfil de volumen de las últimas N velas pegado al eje de precios, con el POC y el área de valor resaltados y sus líneas punteadas, y abajo un mapa de calor del volumen por hora (columnas) y día (filas) de los últimos días.")]
public class VolumeProfileValueArea : SceneIndicator
{
    private Profile.Result? _p;
    private double[,] _heat = new double[0, 0];
    private List<DateTime> _days = new();

    [Display(Name = "Velas del perfil", GroupName = "Cálculo", Order = 10)] [Range(10, 5000)] public int Window { get; set; } = 96;
    [Display(Name = "Área de valor (%)", GroupName = "Cálculo", Order = 11)] [Range(10, 99)] public int ValueArea { get; set; } = 70;
    [Display(Name = "Filas del perfil", GroupName = "Cálculo", Order = 12)] [Range(10, 400)] public int Rows { get; set; } = 64;
    [Display(Name = "Días del mapa (0 = no)", GroupName = "Cálculo", Order = 13)] [Range(0, 30)] public int HeatDays { get; set; } = 7;
    [Display(Name = "Ancho del perfil (px)", GroupName = "Dibujo", Order = 20)] [Range(20, 600)] public int Width { get; set; } = 140;

    public VolumeProfileValueArea() => ShowDashboard = false;

    protected override void Build(Scene s)
    {
        int n = Bars.Count;
        if (n < 5) return;
        int from = Math.Max(0, n - Window);
        _p = Profile.Build(Bars, from, n - 1, Rows, ValueArea);
        if (_p != null)
        {
            s.HLine(from, -1, _p.Poc, Pal.Text, 1, LineDashStyle.Dash, "POC " + Price(_p.Poc), "POC del perfil");
            s.HLine(from, -1, _p.Vah, Pal.Muted, 1, LineDashStyle.Dash, "VAH " + Price(_p.Vah), "VAH del perfil");
            s.HLine(from, -1, _p.Val, Pal.Muted, 1, LineDashStyle.Dash, "VAL " + Price(_p.Val), "VAL del perfil");
        }

        // volumen por hora de cada día de trading (el día empieza donde lo empieza el gráfico)
        _days = new List<DateTime>();
        var map = new Dictionary<(DateTime, int), double>();
        for (int i = 0; i < n; i++)
        {
            var t = TimeOf(i);
            int k = i;
            while (k > 0 && DayKey[k - 1] == DayKey[i]) k--;
            var day = TimeOf(k).AddHours(12).Date;
            if (_days.Count == 0 || _days[^1] != day) _days.Add(day);
            map[(day, t.Hour)] = (map.TryGetValue((day, t.Hour), out var v) ? v : 0) + Bars[i].V;
        }

        _days = _days.TakeLast(HeatDays).ToList();
        _heat = new double[_days.Count, 24];
        for (int d = 0; d < _days.Count; d++)
            for (int h = 0; h < 24; h++)
                _heat[d, h] = map.TryGetValue((_days[d], h), out var v) ? v : 0;
    }

    protected override void RenderExtra(RenderContext ctx, Rectangle region)
    {
        if (_p != null)
        {
            double max = _p.Rows.Max();
            int right = region.Right - 2;
            int up = (int)Math.Round((_p.Vah - _p.Low) / _p.Step) - 1, dn = (int)Math.Round((_p.Val - _p.Low) / _p.Step);
            for (int k = 0; k < _p.Rows.Length; k++)
            {
                if (_p.Rows[k] <= 0) continue;
                int y0 = Y(_p.Low + (k + 1) * _p.Step), y1 = Y(_p.Low + k * _p.Step);
                int w = (int)(Width * _p.Rows[k] / max);
                var c = k == _p.PocRow ? Pal.A(Pal.Text, 200) : k >= dn && k <= up ? Pal.A(Pal.Muted, 150) : Pal.A(Pal.Muted, 60);
                ctx.FillRectangle(c, new Rectangle(right - w, Math.Min(y0, y1), Math.Max(1, w), Math.Max(1, Math.Abs(y1 - y0) - 1)));
            }
        }

        if (_days.Count == 0) return;
        int cell = 9, cw = 11, x0 = region.Left + 8, y = region.Bottom - _days.Count * cell - 8;
        double hmax = 0;
        foreach (var v in _heat) hmax = Math.Max(hmax, v);
        if (hmax <= 0) return;
        var font = new RenderFont("Arial", 7);
        for (int d = 0; d < _days.Count; d++)
        {
            ctx.DrawString(_days[d].ToString("dd/MM"), font, Pal.Muted, x0, y + d * cell - 1);
            for (int h = 0; h < 24; h++)
            {
                double t = _heat[d, h] / hmax;
                ctx.FillRectangle(Color.FromArgb((int)(20 + 220 * Math.Sqrt(t)), 255, 167, 38), new Rectangle(x0 + 32 + h * cw, y + d * cell, cw - 1, cell - 1));
            }
        }
    }
}

/// <summary>Cada hora en una caja sobre su rango, teñida por su volumen contra el promedio; las calientes con borde y etiqueta.</summary>
[DisplayName("Horas calientes de volumen")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("Cada hora del reloj en una caja sobre su rango, teñida por su volumen comparado con el promedio de las horas anteriores: casi invisible si es tranquila, brillante si es caliente. Las horas que pasan el múltiplo llevan borde y una etiqueta «2.3x».")]
public class VolumeHeatHours : SceneIndicator
{
    [Display(Name = "Horas del promedio", GroupName = "Cálculo", Order = 10)] [Range(2, 500)] public int BaselineHours { get; set; } = 24;
    [Display(Name = "Caliente desde (× promedio)", GroupName = "Cálculo", Order = 11)] [Range(1.1, 20)] public decimal HotMultiple { get; set; } = 2;
    [Display(Name = "Horas que se dibujan", GroupName = "Cálculo", Order = 12)] [Range(5, 1000)] public int HoursKept { get; set; } = 60;
    [Display(Name = "Color", GroupName = "Colores", Order = 20)] public Color HeatColor { get; set; } = Color.FromArgb(255, 255, 167, 38);

    public VolumeHeatHours() => ShowDashboard = false;

    protected override void Build(Scene s)
    {
        if (BarLength <= TimeSpan.Zero || BarLength >= TimeSpan.FromHours(1)) return;
        var hours = new List<(int From, int To, double H, double L, double V)>();
        for (int i = 0; i < Bars.Count; i++)
        {
            long hk = Bars[i].T / 3600;
            if (hours.Count == 0 || Bars[hours[^1].From].T / 3600 != hk) hours.Add((i, i, Bars[i].H, Bars[i].L, Bars[i].V));
            else
            {
                var h = hours[^1];
                hours[^1] = (h.From, i, Math.Max(h.H, Bars[i].H), Math.Min(h.L, Bars[i].L), h.V + Bars[i].V);
            }
        }

        for (int k = Math.Max(1, hours.Count - HoursKept); k < hours.Count; k++)
        {
            var h = hours[k];
            double avg = 0;
            int n = 0;
            for (int j = Math.Max(0, k - BaselineHours); j < k; j++, n++) avg += hours[j].V;
            if (n == 0 || avg <= 0) continue;
            avg /= n;
            double ratio = h.V / avg;
            bool hot = ratio >= (double)HotMultiple;
            int alpha = (int)Math.Clamp(10 + 60 * Math.Min(ratio, 3) / 3, 8, 80);
            s.Box(h.From, h.To, h.H, h.L, Pal.A(HeatColor, alpha), hot ? Pal.A(HeatColor, 200) : Color.Transparent);
            if (hot) s.Tag(h.From, h.H, $"{ratio:0.0}x", Color.Transparent, Pal.Muted);
        }
    }
}

/// <summary>El POC, el VAH y el VAL de un perfil que se desliza con las últimas N velas.</summary>
[DisplayName("Perfil de volumen deslizante")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("El perfil de volumen de las últimas N velas, recalculado en cada vela: su POC (el precio con más volumen) y los bordes del área de valor como líneas, y el perfil actual dibujado a la derecha.")]
public class SlidingVolumeProfile : Indicator
{
    private readonly ValueDataSeries _poc = new("POC", "POC") { Color = Pal.Amber, Width = 2, ShowZeroValue = false };
    private readonly ValueDataSeries _vah = new("VAH", "VAH") { Color = Pal.Bear, Width = 1, ShowZeroValue = false };
    private readonly ValueDataSeries _val = new("VAL", "VAL") { Color = Pal.Bull, Width = 1, ShowZeroValue = false };
    private Profile.Result? _last;
    private int _bars = 100, _rows = 24, _va = 70;

    public SlidingVolumeProfile() : base(true)
    {
        DataSeries[0] = _poc;
        DataSeries.Add(_vah);
        DataSeries.Add(_val);
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "Velas", GroupName = "Cálculo", Order = 10)] [Range(10, 5000)] public int Length { get => _bars; set { _bars = value; RecalculateValues(); } }
    [Display(Name = "Filas", GroupName = "Cálculo", Order = 11)] [Range(5, 400)] public int Rows { get => _rows; set { _rows = value; RecalculateValues(); } }
    [Display(Name = "Área de valor (%)", GroupName = "Cálculo", Order = 12)] [Range(10, 99)] public int ValueArea { get => _va; set { _va = value; RecalculateValues(); } }
    [Display(Name = "Dibujar el perfil actual", GroupName = "Dibujo", Order = 20)] public bool ShowProfile { get; set; } = true;

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < _bars - 1) return;
        var bars = new List<Bar>(_bars);
        for (int i = bar - _bars + 1; i <= bar; i++)
        {
            var c = GetCandle(i);
            bars.Add(new Bar(0, 0, (double)c.High, (double)c.Low, 0, (double)c.Volume));
        }

        var p = Profile.Build(bars, 0, bars.Count - 1, _rows, _va);
        if (p == null) return;
        _poc[bar] = (decimal)p.Poc;
        _vah[bar] = (decimal)p.Vah;
        _val[bar] = (decimal)p.Val;
        if (bar == CurrentBar - 1) _last = p;
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (!ShowProfile || _last is not { } p || ChartInfo == null) return;
        int x0 = ChartInfo.GetXByBar(CurrentBar - 1, false) + 12;
        double max = p.Rows.Max();
        for (int k = 0; k < p.Rows.Length; k++)
        {
            int y0 = ChartInfo.GetYByPrice((decimal)(p.Low + (k + 1) * p.Step), false), y1 = ChartInfo.GetYByPrice((decimal)(p.Low + k * p.Step), false);
            int w = (int)(90 * p.Rows[k] / Math.Max(1e-12, max));
            ctx.FillRectangle(k == p.PocRow ? Pal.A(Pal.Amber, 190) : Pal.A(Pal.Muted, 90), new Rectangle(x0, Math.Min(y0, y1), Math.Max(1, w), Math.Max(1, Math.Abs(y1 - y0) - 1)));
        }
    }
}

/// <summary>VWAP anclados al calendario (día, semana, mes, trimestre, año) con bandas, y VWAP de las sesiones.</summary>
[DisplayName("VWAP anclado al calendario (día, semana, mes, trimestre, año)")]
[Category(IndicatorCategories.Vwap)]
[Description("Cinco VWAP que arrancan con el día, la semana, el mes, el trimestre y el año, cada uno con bandas opcionales de ±1σ y ±2σ (desvío ponderado por volumen) y su precio al final de la línea; y VWAP de las sesiones de Sídney, Tokio, Londres y Nueva York (UTC).")]
public class CalendarAnchoredVwap : Indicator
{
    public enum PriceSource
    {
        [Display(Name = "HLC3")] Hlc3,
        [Display(Name = "Cierre")] Close,
        [Display(Name = "HL2")] Hl2,
        [Display(Name = "OHLC4")] Ohlc4,
    }

    private static readonly string[] Names = { "Día", "Semana", "Mes", "Trimestre", "Año", "Sídney", "Tokio", "Londres", "Nueva York" };
    private static readonly Color[] Defaults =
    {
        Color.FromArgb(255, 125, 211, 252), Color.FromArgb(255, 134, 239, 172), Color.FromArgb(255, 253, 186, 116), Color.FromArgb(255, 253, 230, 138), Color.FromArgb(255, 196, 181, 253),
        Color.FromArgb(255, 255, 167, 38), Color.FromArgb(255, 239, 83, 80), Color.FromArgb(255, 41, 98, 255), Color.FromArgb(255, 38, 166, 154),
    };

    private static readonly (int Start, int End)[] SessionWindows = { (22 * 60, 6 * 60), (0, 9 * 60), (7 * 60, 16 * 60), (13 * 60, 22 * 60) };
    private readonly ValueDataSeries[] _mid = new ValueDataSeries[9];
    private readonly ValueDataSeries[][] _bands = new ValueDataSeries[5][];
    private readonly bool[] _show = { true, true, true, true, true, false, false, false, false };
    private readonly bool[] _bandsOn = { false, true, false, false, false };
    private readonly (double Pv, double V, double Pv2)[] _acc = new (double, double, double)[9];
    private readonly (double Pv, double V, double Pv2)[] _done = new (double, double, double)[9];
    private int _committed = -1;
    private PriceSource _src = PriceSource.Hlc3;
    private bool _sigma2;
    private decimal _m1 = 1, _m2 = 2;

    public CalendarAnchoredVwap() : base(true)
    {
        for (int k = 0; k < 9; k++)
        {
            _mid[k] = new ValueDataSeries("V" + k, "VWAP " + Names[k]) { Color = Defaults[k], Width = k == 4 ? 3 : 2, ShowZeroValue = false };
            if (k == 0) DataSeries[0] = _mid[k];
            else DataSeries.Add(_mid[k]);
        }

        for (int k = 0; k < 5; k++)
        {
            _bands[k] = new ValueDataSeries[4];
            for (int j = 0; j < 4; j++)
            {
                _bands[k][j] = new ValueDataSeries($"B{k}{j}", $"{Names[k]} {(j % 2 == 0 ? "+" : "−")}{(j < 2 ? 1 : 2)}σ") { Color = Pal.A(Defaults[k], 120), ShowZeroValue = false, LineDashStyle = LineDashStyle.Dash };
                DataSeries.Add(_bands[k][j]);
            }
        }

        Apply();
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    private void Apply()
    {
        for (int k = 0; k < 9; k++) _mid[k].VisualType = _show[k] ? VisualMode.Line : VisualMode.Hide;
        for (int k = 0; k < 5; k++)
            for (int j = 0; j < 4; j++)
                _bands[k][j].VisualType = _show[k] && _bandsOn[k] && (j < 2 || _sigma2) ? VisualMode.Line : VisualMode.Hide;
    }

    private bool Get(bool[] a, int k) => a[k];

    private void Set(bool[] a, int k, bool v)
    {
        a[k] = v;
        Apply();
        RecalculateValues();
    }

    [Display(Name = "Precio", GroupName = "Cálculo", Order = 1)] public PriceSource Source { get => _src; set { _src = value; RecalculateValues(); } }
    [Display(Name = "Día", GroupName = "Anclas", Order = 10)] public bool Daily { get => Get(_show, 0); set => Set(_show, 0, value); }
    [Display(Name = "Bandas del día", GroupName = "Anclas", Order = 11)] public bool DailyBands { get => Get(_bandsOn, 0); set => Set(_bandsOn, 0, value); }
    [Display(Name = "Semana", GroupName = "Anclas", Order = 12)] public bool Weekly { get => Get(_show, 1); set => Set(_show, 1, value); }
    [Display(Name = "Bandas de la semana", GroupName = "Anclas", Order = 13)] public bool WeeklyBands { get => Get(_bandsOn, 1); set => Set(_bandsOn, 1, value); }
    [Display(Name = "Mes", GroupName = "Anclas", Order = 14)] public bool Monthly { get => Get(_show, 2); set => Set(_show, 2, value); }
    [Display(Name = "Bandas del mes", GroupName = "Anclas", Order = 15)] public bool MonthlyBands { get => Get(_bandsOn, 2); set => Set(_bandsOn, 2, value); }
    [Display(Name = "Trimestre", GroupName = "Anclas", Order = 16)] public bool Quarterly { get => Get(_show, 3); set => Set(_show, 3, value); }
    [Display(Name = "Bandas del trimestre", GroupName = "Anclas", Order = 17)] public bool QuarterlyBands { get => Get(_bandsOn, 3); set => Set(_bandsOn, 3, value); }
    [Display(Name = "Año", GroupName = "Anclas", Order = 18)] public bool Yearly { get => Get(_show, 4); set => Set(_show, 4, value); }
    [Display(Name = "Bandas del año", GroupName = "Anclas", Order = 19)] public bool YearlyBands { get => Get(_bandsOn, 4); set => Set(_bandsOn, 4, value); }
    [Display(Name = "Segunda banda (2σ)", GroupName = "Bandas", Order = 30)] public bool Sigma2 { get => _sigma2; set { _sigma2 = value; Apply(); } }
    [Display(Name = "Primera banda (σ)", GroupName = "Bandas", Order = 31)] [Range(0.1, 5)] public decimal Mult1 { get => _m1; set { _m1 = value; RecalculateValues(); } }
    [Display(Name = "Segunda banda (σ)", GroupName = "Bandas", Order = 32)] [Range(0.1, 10)] public decimal Mult2 { get => _m2; set { _m2 = value; RecalculateValues(); } }
    [Display(Name = "Sídney (22–06 UTC)", GroupName = "Sesiones", Order = 40)] public bool Sydney { get => Get(_show, 5); set => Set(_show, 5, value); }
    [Display(Name = "Tokio (00–09 UTC)", GroupName = "Sesiones", Order = 41)] public bool Tokyo { get => Get(_show, 6); set => Set(_show, 6, value); }
    [Display(Name = "Londres (07–16 UTC)", GroupName = "Sesiones", Order = 42)] public bool London { get => Get(_show, 7); set => Set(_show, 7, value); }
    [Display(Name = "Nueva York (13–22 UTC)", GroupName = "Sesiones", Order = 43)] public bool NewYork { get => Get(_show, 8); set => Set(_show, 8, value); }
    [Display(Name = "Precio al final de la línea", GroupName = "Dibujo", Order = 50)] public bool ShowLabels { get; set; } = true;

    protected override void OnRecalculate()
    {
        Array.Clear(_acc);
        Array.Clear(_done);
        _committed = -1;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    private decimal Price(IndicatorCandle c) => _src switch
    {
        PriceSource.Close => c.Close,
        PriceSource.Hl2 => (c.High + c.Low) / 2,
        PriceSource.Ohlc4 => (c.Open + c.High + c.Low + c.Close) / 4,
        _ => (c.High + c.Low + c.Close) / 3,
    };

    private bool Reset(int k, int bar)
    {
        if (bar == 0) return true;
        var t = GetCandle(bar).Time;
        var pt = GetCandle(bar - 1).Time;
        switch (k)
        {
            case 0: return IsNewSession(bar);
            case 1: return IsNewWeek(bar);
            case 2: return IsNewMonth(bar);
            case 3: return IsNewMonth(bar) && ((t.AddHours(12).Month - 1) / 3 != (pt.AddHours(12).Month - 1) / 3 || t.AddHours(12).Year != pt.AddHours(12).Year);
            case 4: return IsNewMonth(bar) && t.AddHours(12).Year != pt.AddHours(12).Year;
            default:
            {
                var w = SessionWindows[k - 5];
                return Ta.InUtcWindow(t, w.Start, w.End) && !Ta.InUtcWindow(pt, w.Start, w.End);
            }
        }
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        double p = (double)Price(c), v = (double)c.Volume;
        bool closed = bar < CurrentBar - 1;
        if (bar <= _committed) return;
        for (int k = 0; k < 9; k++)
        {
            var a = _done[k];
            if (Reset(k, bar)) a = default;
            bool inSession = k < 5 || Ta.InUtcWindow(c.Time, SessionWindows[k - 5].Start, SessionWindows[k - 5].End);
            if (!inSession)
            {
                if (closed) _done[k] = a;
                _mid[k][bar] = 0;
                continue;
            }

            a = (a.Pv + p * v, a.V + v, a.Pv2 + p * p * v);
            if (closed) _done[k] = a;
            if (a.V <= 0) continue;
            double vw = a.Pv / a.V, sd = Math.Sqrt(Math.Max(0, a.Pv2 / a.V - vw * vw));
            _mid[k][bar] = (decimal)vw;
            if (k < 5)
            {
                _bands[k][0][bar] = (decimal)(vw + sd * (double)_m1);
                _bands[k][1][bar] = (decimal)(vw - sd * (double)_m1);
                _bands[k][2][bar] = (decimal)(vw + sd * (double)_m2);
                _bands[k][3][bar] = (decimal)(vw - sd * (double)_m2);
            }

            if (bar > 0 && Reset(k, bar))
            {
                _mid[k].SetPointOfEndLine(bar - 1);
                if (k < 5)
                    foreach (var b in _bands[k])
                        b.SetPointOfEndLine(bar - 1);
            }
        }

        if (closed) _committed = bar;
    }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (!ShowLabels || ChartInfo == null || CurrentBar == 0) return;
        int bar = CurrentBar - 1;
        int x = ChartInfo.GetXByBar(bar, false) + 8;
        var font = new RenderFont("Arial", 8);
        for (int k = 0; k < 9; k++)
        {
            if (!_show[k] || _mid[k][bar] == 0) continue;
            int y = ChartInfo.GetYByPrice(_mid[k][bar], false);
            ctx.DrawString($"{UiText.Tr(Names[k])} {_mid[k][bar]:0.##}", font, _mid[k].Color, x, y - 6);
        }
    }
}

/// <summary>Evaluación de ejecución: una orden padre trabajada como TWAP, VWAP y POV, contra los precios de referencia.</summary>
[DisplayName("Evaluación de ejecución (TWAP, VWAP, POV)")]
[Category(IndicatorCategories.Vwap)]
[Description("Simula en cada ventana (hora, 4 horas, día, semana, mes) una orden padre trabajada tres formas: TWAP (partes iguales por vela), VWAP (según el volumen típico de cada vela de la ventana) y POV (un porcentaje del volumen de cada vela). Compara el precio promedio de cada una con la llegada, el VWAP y el TWAP de la ventana, en puntos básicos.")]
public class ExecutionBenchmarks : SceneIndicator
{
    public enum OrderSide
    {
        [Display(Name = "Compra")] Buy,
        [Display(Name = "Venta")] Sell,
    }

    public enum Sizing
    {
        [Display(Name = "% del volumen de la ventana")] Percent,
        [Display(Name = "Unidades")] Units,
    }

    public enum ExecWindow
    {
        [Display(Name = "1 hora")] H1,
        [Display(Name = "4 horas")] H4,
        [Display(Name = "Día")] Day,
        [Display(Name = "Semana")] Week,
        [Display(Name = "Mes")] Month,
    }

    [Display(Name = "Lado", GroupName = "Orden", Order = 10)] public OrderSide Side { get; set; } = OrderSide.Buy;
    [Display(Name = "Tamaño", GroupName = "Orden", Order = 11)] public Sizing SizeMode { get; set; } = Sizing.Percent;
    [Display(Name = "Tamaño (% del volumen)", GroupName = "Orden", Order = 12)] [Range(0.1, 100)] public decimal PctSize { get; set; } = 5;
    [Display(Name = "Tamaño (unidades)", GroupName = "Orden", Order = 13)] [Range(0.0001, 1e12)] public decimal UnitSize { get; set; } = 10000;
    [Display(Name = "Ventana", GroupName = "Orden", Order = 14)] public ExecWindow Window { get; set; } = ExecWindow.Day;
    [Display(Name = "Velas del programa (0 = toda la ventana)", GroupName = "Orden", Order = 15)] [Range(0, 10000)] public int ScheduleBars { get; set; }
    [Display(Name = "Participación del POV (%)", GroupName = "Orden", Order = 16)] [Range(1, 100)] public decimal PovRate { get; set; } = 10;
    [Display(Name = "Ventanas del perfil de volumen", GroupName = "Orden", Order = 17)] [Range(1, 100)] public int LookbackWindows { get; set; } = 10;
    [Display(Name = "Líneas de referencia", GroupName = "Dibujo", Order = 20)] public bool ShowBenchmarks { get; set; } = true;
    [Display(Name = "Precios promedio", GroupName = "Dibujo", Order = 21)] public bool ShowFills { get; set; } = true;
    [Display(Name = "Sombrear las velas del programa", GroupName = "Dibujo", Order = 22)] public bool ShadeScheduled { get; set; } = true;

    private int[] WindowKeys()
    {
        var k = new int[Bars.Count];
        for (int i = 0; i < Bars.Count; i++)
            k[i] = Window switch
            {
                ExecWindow.H1 => (int)(Bars[i].T / 3600),
                ExecWindow.H4 => (int)(Bars[i].T / 14400),
                ExecWindow.Week => WeekKey[i],
                ExecWindow.Month => MonthKey[i],
                _ => DayKey[i],
            };
        return k;
    }

    protected override void Build(Scene s)
    {
        int n = Bars.Count;
        if (n < 10) return;
        var keys = WindowKeys();
        var wins = new List<(int From, int To)>();
        for (int i = 0; i < n; i++)
            if (i == 0 || keys[i] != keys[i - 1]) wins.Add((i, i));
            else wins[^1] = (wins[^1].From, i);
        double sign = Side == OrderSide.Buy ? 1 : -1;
        string summary = "—";
        double[] lastBps = new double[3];
        int scored = 0;
        double[] sumBps = new double[3];
        for (int w = 1; w < wins.Count; w++)
        {
            var (from, to) = wins[w];
            bool live = w == wins.Count - 1;
            // el volumen típico de cada vela de la ventana (las anteriores)
            var prevWins = wins.Skip(Math.Max(0, w - LookbackWindows)).Take(Math.Min(LookbackWindows, w)).ToList();
            int len = ScheduleBars > 0 ? ScheduleBars : prevWins.Max(x => x.To - x.From + 1);
            var shape = new double[len];
            double avgVol = 0;
            foreach (var pw in prevWins)
            {
                double tot = 0;
                for (int k = 0; k < len && pw.From + k <= pw.To; k++)
                {
                    shape[k] += Bars[pw.From + k].V;
                    tot += Bars[pw.From + k].V;
                }

                avgVol += tot;
            }

            avgVol /= Math.Max(1, prevWins.Count);
            double shapeSum = shape.Sum();
            double parent = SizeMode == Sizing.Percent ? avgVol * (double)PctSize / 100 : (double)UnitSize;
            if (parent <= 0 || shapeSum <= 0) continue;
            double arrival = Bars[from].O;
            double wpv = 0, wv = 0, tw = 0;
            int tn = 0;
            double[] filled = new double[3], cost = new double[3];
            int end = Math.Min(to, from + len - 1);
            for (int i = from; i <= to; i++)
            {
                var b = Bars[i];
                double px = (b.H + b.L + b.C) / 3;
                wpv += px * b.V;
                wv += b.V;
                tw += px;
                tn++;
                if (i > end) continue;
                int k = i - from;
                double[] want = { parent / len, parent * shape[k] / shapeSum, b.V * (double)PovRate / 100 };
                for (int a = 0; a < 3; a++)
                {
                    double q = Math.Min(want[a], parent - filled[a]);
                    if (q <= 0) continue;
                    filled[a] += q;
                    cost[a] += q * px;
                }

                if (ShadeScheduled && live) s.Tints[i] = Pal.A(Pal.Neutral, 12);
            }

            double vwap = wv > 0 ? wpv / wv : arrival, twap = tn > 0 ? tw / tn : arrival;
            var avg = Enumerable.Range(0, 3).Select(a => filled[a] > 0 ? cost[a] / filled[a] : double.NaN).ToArray();
            if (ShowBenchmarks && w >= wins.Count - 6)
            {
                s.HLine(from, live ? -1 : to, arrival, Pal.Muted, 1, LineDashStyle.Dot, live ? "Llegada " + Price(arrival) : null);
                s.HLine(from, live ? -1 : to, vwap, Pal.Amber, 1, LineDashStyle.Dash, live ? "VWAP de la ventana " + Price(vwap) : null);
            }

            if (ShowFills && w >= wins.Count - 6)
            {
                string[] names = { "TWAP", "VWAP", "POV" };
                Color[] cols = { Color.FromArgb(255, 41, 182, 246), Color.FromArgb(255, 171, 71, 188), Color.FromArgb(255, 102, 187, 106) };
                for (int a = 0; a < 3; a++)
                    if (!double.IsNaN(avg[a])) s.HLine(from, live ? -1 : to, avg[a], cols[a], 1, LineDashStyle.Solid, live ? names[a] + " " + Price(avg[a]) : null);
            }

            for (int a = 0; a < 3; a++)
                lastBps[a] = double.IsNaN(avg[a]) ? double.NaN : (avg[a] - vwap) / vwap * 1e4 * sign;
            if (!live)
            {
                scored++;
                for (int a = 0; a < 3; a++)
                    if (!double.IsNaN(lastBps[a])) sumBps[a] += lastBps[a];
            }

            if (live) summary = $"{filled.Max() / parent * 100:0} % hecho";
        }

        s.DashTitle = "Evaluación de ejecución (contra el VWAP)";
        string[] algo = { "TWAP", "VWAP", "POV" };
        for (int a = 0; a < 3; a++)
            s.Row(algo[a], double.IsNaN(lastBps[a]) ? "—" : $"{lastBps[a]:+0.0;-0.0} pb · prom. {(scored > 0 ? sumBps[a] / scored : 0):+0.0;-0.0}", lastBps[a] > 0 ? Pal.Bear : Pal.Bull);
        s.Row("Ventana actual", summary);
        s.Row("Ventanas evaluadas", scored.ToString());
    }
}
