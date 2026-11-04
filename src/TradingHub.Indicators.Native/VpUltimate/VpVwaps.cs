using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators.Native.VpUltimate;

/// <summary>Precio con el que se calculan los VWAP.</summary>
public enum VpVwapSource
{
    [Display(Name = "Cada operación (footprint, exacto)")] Trades,
    [Display(Name = "HLC/3 de la vela")] Hlc3,
    [Display(Name = "Cierre de la vela")] Close,
}

/// <summary>Sumas de un VWAP (precio × volumen, volumen, precio² × volumen).</summary>
public struct VwapSum
{
    public decimal Pv, V, P2v;

    public void Add(VwapSum o)
    {
        Pv += o.Pv;
        V += o.V;
        P2v += o.P2v;
    }

    public void Sub(VwapSum o)
    {
        Pv -= o.Pv;
        V -= o.V;
        P2v -= o.P2v;
    }

    public decimal Vwap => V > 0 ? Pv / V : 0;

    public decimal StdDev
    {
        get
        {
            if (V <= 0) return 0;
            decimal m = Pv / V;
            return (decimal)Math.Sqrt((double)Math.Max(0, P2v / V - m * m));
        }
    }

    public static VwapSum Of(IndicatorCandle c, VpVwapSource src)
    {
        var s = new VwapSum();
        if (src == VpVwapSource.Trades)
        {
            foreach (var l in c.GetAllPriceLevels())
            {
                s.Pv += l.Price * l.Volume;
                s.P2v += l.Price * l.Price * l.Volume;
                s.V += l.Volume;
            }

            if (s.V > 0) return s;
        }

        decimal p = src == VpVwapSource.Close ? c.Close : (c.High + c.Low + c.Close) / 3;
        return new VwapSum { Pv = p * c.Volume, P2v = p * p * c.Volume, V = c.Volume };
    }
}

/// <summary>Base de los VWAP por período: acumula las velas cerradas y suma aparte la vela en formación.</summary>
public abstract class VpPeriodVwapBase : VpIndicator
{
    private VpVwapSource _source = VpVwapSource.Trades;

    [Display(Name = "Precio", GroupName = "Cálculo", Order = 5)]
    public VpVwapSource Source { get => _source; set { _source = value; RecalculateValues(); } }

    protected VwapSum Sum(int bar) => VwapSum.Of(GetCandle(bar), _source);
}

/// <summary>Módulo 2 del «VP definitivo»: VWAP del día, la semana, el mes, el trimestre y el año a la vez.</summary>
[Browsable(false)] // forma parte de «VWAP (bandas σ · Claire · VP definitivo)»
[DisplayName("VP definitivo · VWAP de varios períodos")]
[Category(Vp.Category)]
[Description("VWAP diario, semanal, mensual, trimestral y anual en el mismo gráfico, cada uno se reinicia al empezar su período.")]
public class VpMultiVwap : VpPeriodVwapBase
{
    private static readonly VpPeriod[] Periods = { VpPeriod.Day, VpPeriod.Week, VpPeriod.Month, VpPeriod.Quarter, VpPeriod.Year };
    private readonly ValueDataSeries[] _lines;
    private readonly VwapSum[] _acc = new VwapSum[5];

    public VpMultiVwap()
    {
        _lines = new[]
        {
            new ValueDataSeries("D", "VWAP diario") { Color = Color.FromArgb(255, 242, 54, 69), ShowZeroValue = false },
            new ValueDataSeries("W", "VWAP semanal") { Color = Color.White, ShowZeroValue = false },
            new ValueDataSeries("M", "VWAP mensual") { Color = Color.FromArgb(255, 41, 98, 255), ShowZeroValue = false },
            new ValueDataSeries("Q", "VWAP trimestral") { Color = Color.FromArgb(255, 76, 175, 80), ShowZeroValue = false },
            new ValueDataSeries("Y", "VWAP anual") { Color = Color.FromArgb(255, 255, 235, 59), ShowZeroValue = false },
        };
        foreach (var l in _lines) DataSeries.Add(l);
    }

    protected override void Reset()
    {
        for (int i = 0; i < 5; i++) _acc[i] = default;
    }

    protected override void Commit(int bar)
    {
        var s = Sum(bar);
        for (int i = 0; i < 5; i++)
        {
            if (NewPeriod(bar, Periods[i]))
            {
                _acc[i] = default;
                if (bar > 0) _lines[i].SetPointOfEndLine(bar - 1);
            }

            _acc[i].Add(s);
            _lines[i][bar] = _acc[i].Vwap;
        }
    }

    protected override void Live(int bar)
    {
        if (bar <= Committed) return;
        var s = Sum(bar);
        for (int i = 0; i < 5; i++)
        {
            var a = NewPeriod(bar, Periods[i]) ? default : _acc[i];
            if (NewPeriod(bar, Periods[i]) && bar > 0) _lines[i].SetPointOfEndLine(bar - 1);
            a.Add(s);
            _lines[i][bar] = a.Vwap;
        }
    }
}

/// <summary>Módulo 3 del «VP definitivo»: VWAP del período en curso con bandas de desvío estándar (ponderado por volumen).</summary>
[Browsable(false)] // forma parte de «VWAP (bandas σ · Claire · VP definitivo)»
[DisplayName("VP definitivo · VWAP anclado con bandas")]
[Category(Vp.Category)]
[Description("VWAP que se reinicia en cada período elegido, con dos bandas de desvío estándar ponderado por volumen y relleno opcional.")]
public class VpAnchoredVwap : VpPeriodVwapBase
{
    private readonly ValueDataSeries _vwap = new("VWAP", "VWAP") { Color = Color.FromArgb(255, 255, 152, 0), Width = 2, ShowZeroValue = false };
    private readonly ValueDataSeries _u2 = new("U2", "+2σ") { Color = Color.FromArgb(77, 242, 54, 69), Width = 3, ShowZeroValue = false };
    private readonly ValueDataSeries _u1 = new("U1", "+1σ") { Color = Color.FromArgb(102, 128, 128, 128), Width = 3, ShowZeroValue = false };
    private readonly ValueDataSeries _d1 = new("D1", "-1σ") { Color = Color.FromArgb(102, 128, 128, 128), Width = 3, ShowZeroValue = false };
    private readonly ValueDataSeries _d2 = new("D2", "-2σ") { Color = Color.FromArgb(77, 41, 98, 255), Width = 3, ShowZeroValue = false };
    private readonly ValueDataSeries _u3 = new("U3", "+3σ") { Color = Color.FromArgb(200, 240, 82, 90), LineDashStyle = LineDashStyle.Dot, ShowZeroValue = false };
    private readonly ValueDataSeries _d3 = new("D3", "-3σ") { Color = Color.FromArgb(200, 240, 82, 90), LineDashStyle = LineDashStyle.Dot, ShowZeroValue = false };
    private readonly RangeDataSeries _f1 = new("F1", "Relleno +2σ / +1σ") { RangeColor = Color.FromArgb(13, 242, 54, 69) };
    private readonly RangeDataSeries _f2 = new("F2", "Relleno ±1σ") { RangeColor = Color.FromArgb(26, 128, 128, 128) };
    private readonly RangeDataSeries _f3 = new("F3", "Relleno -1σ / -2σ") { RangeColor = Color.FromArgb(13, 41, 98, 255) };
    private VwapSum _acc;
    private VpPeriod _period = VpPeriod.Day;
    private bool _band1 = true, _band2 = true, _band3, _fill, _showVwap = true;
    private decimal _m1 = 1, _m2 = 2, _m3 = 3;

    public VpAnchoredVwap()
    {
        DataSeries.Add(_vwap);
        foreach (var s in new[] { _u2, _u1, _d1, _d2, _u3, _d3 }) DataSeries.Add(s);
        foreach (var r in new[] { _f1, _f2, _f3 }) DataSeries.Add(r);
    }

    [Display(Name = "Período", GroupName = "Cálculo", Order = 10)]
    public VpPeriod Period { get => _period; set { _period = value; RecalculateValues(); } }

    [Display(Name = "Mostrar la línea del VWAP", GroupName = "Bandas", Order = 20)]
    public bool ShowVwap { get => _showVwap; set { _showVwap = value; RecalculateValues(); } }

    [Display(Name = "Banda 1", GroupName = "Bandas", Order = 30)]
    public bool Band1 { get => _band1; set { _band1 = value; RecalculateValues(); } }

    [Display(Name = "Banda 1 (× σ)", GroupName = "Bandas", Order = 40)]
    public decimal Mult1 { get => _m1; set { _m1 = value; RecalculateValues(); } }

    [Display(Name = "Banda 2", GroupName = "Bandas", Order = 50)]
    public bool Band2 { get => _band2; set { _band2 = value; RecalculateValues(); } }

    [Display(Name = "Banda 2 (× σ)", GroupName = "Bandas", Order = 60)]
    public decimal Mult2 { get => _m2; set { _m2 = value; RecalculateValues(); } }

    [Display(Name = "Banda 3", GroupName = "Bandas", Order = 62)]
    public bool Band3 { get => _band3; set { _band3 = value; RecalculateValues(); } }

    [Display(Name = "Banda 3 (× σ)", GroupName = "Bandas", Order = 64)]
    public decimal Mult3 { get => _m3; set { _m3 = value; RecalculateValues(); } }

    [Display(Name = "Rellenar entre bandas", GroupName = "Bandas", Order = 70)]
    public bool Fill { get => _fill; set { _fill = value; RecalculateValues(); } }

    protected override void Reset() => _acc = default;

    private void Set(int bar, VwapSum a)
    {
        decimal v = a.Vwap, sd = a.StdDev;
        _vwap[bar] = _showVwap ? v : 0;
        _u1[bar] = _band1 ? v + _m1 * sd : 0;
        _d1[bar] = _band1 ? v - _m1 * sd : 0;
        _u2[bar] = _band2 ? v + _m2 * sd : 0;
        _d2[bar] = _band2 ? v - _m2 * sd : 0;
        _u3[bar] = _band3 ? v + _m3 * sd : 0;
        _d3[bar] = _band3 ? v - _m3 * sd : 0;
        _f1[bar] = _fill && _band1 && _band2 ? new RangeValue(v + _m2 * sd, v + _m1 * sd) : new RangeValue();
        _f2[bar] = _fill && _band1 ? new RangeValue(v + _m1 * sd, v - _m1 * sd) : new RangeValue();
        _f3[bar] = _fill && _band1 && _band2 ? new RangeValue(v - _m1 * sd, v - _m2 * sd) : new RangeValue();
    }

    private void Break(int bar)
    {
        if (bar <= 0) return;
        foreach (var s in new[] { _vwap, _u2, _u1, _d1, _d2, _u3, _d3 }) s.SetPointOfEndLine(bar - 1);
    }

    protected override void Commit(int bar)
    {
        if (NewPeriod(bar, _period))
        {
            _acc = default;
            Break(bar);
        }

        _acc.Add(Sum(bar));
        Set(bar, _acc);
    }

    protected override void Live(int bar)
    {
        if (bar <= Committed) return;
        bool fresh = NewPeriod(bar, _period);
        var a = fresh ? default : _acc;
        if (fresh) Break(bar);
        a.Add(Sum(bar));
        Set(bar, a);
    }
}

/// <summary>Módulo 4 del «VP definitivo»: VWAP final (y bandas) del período anterior, como escalón durante el período actual.</summary>
[Browsable(false)] // forma parte de «VWAP (bandas σ · Claire · VP definitivo)»
[DisplayName("VP definitivo · VWAP del período anterior")]
[Category(Vp.Category)]
[Description("Dónde terminó el VWAP del período anterior (por ejemplo, el «precio de liquidación» de ayer) y sus bandas, durante todo el período actual.")]
public class VpPreviousVwap : VpPeriodVwapBase
{
    private readonly ValueDataSeries _pv = new("PV", "VWAP anterior") { Color = Color.FromArgb(102, 255, 152, 0), ShowZeroValue = false };
    private readonly ValueDataSeries _u2 = new("U2", "+2σ anterior") { Color = Color.FromArgb(77, 242, 54, 69), ShowZeroValue = false };
    private readonly ValueDataSeries _u1 = new("U1", "+1σ anterior") { Color = Color.FromArgb(102, 128, 128, 128), ShowZeroValue = false };
    private readonly ValueDataSeries _d1 = new("D1", "-1σ anterior") { Color = Color.FromArgb(102, 128, 128, 128), ShowZeroValue = false };
    private readonly ValueDataSeries _d2 = new("D2", "-2σ anterior") { Color = Color.FromArgb(77, 41, 98, 255), ShowZeroValue = false };
    private readonly RangeDataSeries _f1 = new("F1", "Relleno +2σ / +1σ") { RangeColor = Color.FromArgb(13, 242, 54, 69) };
    private readonly RangeDataSeries _f2 = new("F2", "Relleno ±1σ") { RangeColor = Color.FromArgb(26, 128, 128, 128) };
    private readonly RangeDataSeries _f3 = new("F3", "Relleno -1σ / -2σ") { RangeColor = Color.FromArgb(13, 41, 98, 255) };
    private VwapSum _acc;
    private (decimal V, decimal Sd)? _prev;
    private VpPeriod _period = VpPeriod.Day;
    private bool _line = true, _band1, _band2, _fill;
    private decimal _m1 = 1, _m2 = 2;

    public VpPreviousVwap()
    {
        foreach (var s in new[] { _pv, _u2, _u1, _d1, _d2 }) DataSeries.Add(s);
        foreach (var r in new[] { _f1, _f2, _f3 }) DataSeries.Add(r);
    }

    [Display(Name = "Período", GroupName = "Cálculo", Order = 10)]
    public VpPeriod Period { get => _period; set { _period = value; RecalculateValues(); } }

    [Display(Name = "Mostrar el VWAP anterior", GroupName = "Bandas", Order = 20)]
    public bool ShowLine { get => _line; set { _line = value; RecalculateValues(); } }

    [Display(Name = "Banda 1", GroupName = "Bandas", Order = 30)]
    public bool Band1 { get => _band1; set { _band1 = value; RecalculateValues(); } }

    [Display(Name = "Banda 1 (× σ)", GroupName = "Bandas", Order = 40)]
    public decimal Mult1 { get => _m1; set { _m1 = value; RecalculateValues(); } }

    [Display(Name = "Banda 2", GroupName = "Bandas", Order = 50)]
    public bool Band2 { get => _band2; set { _band2 = value; RecalculateValues(); } }

    [Display(Name = "Banda 2 (× σ)", GroupName = "Bandas", Order = 60)]
    public decimal Mult2 { get => _m2; set { _m2 = value; RecalculateValues(); } }

    [Display(Name = "Rellenar entre bandas", GroupName = "Bandas", Order = 70)]
    public bool Fill { get => _fill; set { _fill = value; RecalculateValues(); } }

    protected override void Reset()
    {
        _acc = default;
        _prev = null;
    }

    private void Set(int bar, (decimal V, decimal Sd)? prev)
    {
        if (prev is not { } p)
            return;
        var (v, sd) = p;
        _pv[bar] = _line ? v : 0;
        _u1[bar] = _band1 ? v + _m1 * sd : 0;
        _d1[bar] = _band1 ? v - _m1 * sd : 0;
        _u2[bar] = _band2 ? v + _m2 * sd : 0;
        _d2[bar] = _band2 ? v - _m2 * sd : 0;
        _f1[bar] = _fill && _band1 && _band2 ? new RangeValue(v + _m2 * sd, v + _m1 * sd) : new RangeValue();
        _f2[bar] = _fill && _band1 ? new RangeValue(v + _m1 * sd, v - _m1 * sd) : new RangeValue();
        _f3[bar] = _fill && _band1 && _band2 ? new RangeValue(v - _m1 * sd, v - _m2 * sd) : new RangeValue();
    }

    private void Break(int bar)
    {
        if (bar <= 0) return;
        foreach (var s in new[] { _pv, _u2, _u1, _d1, _d2 }) s.SetPointOfEndLine(bar - 1);
    }

    protected override void Commit(int bar)
    {
        if (NewPeriod(bar, _period))
        {
            if (bar > 0 && _acc.V > 0) _prev = (_acc.Vwap, _acc.StdDev);
            _acc = default;
            Break(bar);
        }

        _acc.Add(Sum(bar));
        Set(bar, _prev);
    }

    protected override void Live(int bar)
    {
        if (bar <= Committed) return;
        var prev = _prev;
        if (NewPeriod(bar, _period))
        {
            if (_acc.V > 0) prev = (_acc.Vwap, _acc.StdDev);
            Break(bar);
        }

        Set(bar, prev);
    }
}

/// <summary>Módulo 5 del «VP definitivo»: el VWAP con que cerró cada período, como línea horizontal hacia la derecha.</summary>
[Browsable(false)] // forma parte de «VWAP (bandas σ · Claire · VP definitivo)»
[DisplayName("VP definitivo · Cierres del VWAP")]
[Category(Vp.Category)]
[Description("Al terminar cada período, el valor final de su VWAP (y, si querés, de sus bandas) queda como línea horizontal hacia la derecha. Se conservan los últimos N.")]
public class VpVwapCloses : VpPeriodVwapBase
{
    private sealed record Level(int Bar, decimal Price, Color Color);

    private static readonly VpPeriod[] Periods = { VpPeriod.Day, VpPeriod.Week, VpPeriod.Month, VpPeriod.Year };
    private readonly List<Level>[] _levels = { new(), new(), new(), new() };
    private readonly VwapSum[] _acc = new VwapSum[4];
    private bool _vwap = true, _std1, _std2;
    private decimal _m1 = 1, _m2 = 2;

    [Display(Name = "Conservar los últimos N períodos", GroupName = "Líneas", Order = 10)]
    [Range(1, 50)]
    public int Keep { get; set; } = 3;

    [Display(Name = "Extender a la derecha (si no, hasta la última vela)", GroupName = "Líneas", Order = 20)]
    public bool ExtendRight { get; set; } = true;

    [Display(Name = "Cierre del VWAP", GroupName = "Líneas", Order = 30)]
    public bool CloseVwap { get => _vwap; set { _vwap = value; RecalculateValues(); } }

    [Display(Name = "Cierre de ±1σ", GroupName = "Líneas", Order = 40)]
    public bool CloseStd1 { get => _std1; set { _std1 = value; RecalculateValues(); } }

    [Display(Name = "Cierre de ±2σ", GroupName = "Líneas", Order = 50)]
    public bool CloseStd2 { get => _std2; set { _std2 = value; RecalculateValues(); } }

    [Display(Name = "Banda 1 (× σ)", GroupName = "Líneas", Order = 60)]
    public decimal Mult1 { get => _m1; set { _m1 = value; RecalculateValues(); } }

    [Display(Name = "Banda 2 (× σ)", GroupName = "Líneas", Order = 70)]
    public decimal Mult2 { get => _m2; set { _m2 = value; RecalculateValues(); } }

    [Display(Name = "Diario", GroupName = "Períodos", Order = 100)]
    public bool Daily { get; set; } = true;

    [Display(Name = "Color diario", GroupName = "Períodos", Order = 101)]
    public Color DailyColor { get; set; } = Color.FromArgb(255, 242, 54, 69);

    [Display(Name = "Semanal", GroupName = "Períodos", Order = 110)]
    public bool Weekly { get; set; }

    [Display(Name = "Color semanal", GroupName = "Períodos", Order = 111)]
    public Color WeeklyColor { get; set; } = Color.White;

    [Display(Name = "Mensual", GroupName = "Períodos", Order = 120)]
    public bool Monthly { get; set; }

    [Display(Name = "Color mensual", GroupName = "Períodos", Order = 121)]
    public Color MonthlyColor { get; set; } = Color.FromArgb(255, 41, 98, 255);

    [Display(Name = "Anual", GroupName = "Períodos", Order = 130)]
    public bool Yearly { get; set; }

    [Display(Name = "Color anual", GroupName = "Períodos", Order = 131)]
    public Color YearlyColor { get; set; } = Color.FromArgb(255, 255, 235, 59);

    public VpVwapCloses()
    {
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    protected override void Reset()
    {
        foreach (var l in _levels) l.Clear();
        for (int i = 0; i < 4; i++) _acc[i] = default;
    }

    protected override void Commit(int bar)
    {
        var s = Sum(bar);
        for (int i = 0; i < 4; i++)
        {
            if (NewPeriod(bar, Periods[i]) && bar > 0 && _acc[i].V > 0)
            {
                decimal v = _acc[i].Vwap, sd = _acc[i].StdDev;
                var list = _levels[i];
                if (_vwap) list.Add(new Level(bar, v, Color.Empty));
                if (_std1)
                {
                    list.Add(new Level(bar, v + _m1 * sd, Color.Empty));
                    list.Add(new Level(bar, v - _m1 * sd, Color.Empty));
                }

                if (_std2)
                {
                    list.Add(new Level(bar, v + _m2 * sd, Color.Empty));
                    list.Add(new Level(bar, v - _m2 * sd, Color.Empty));
                }

                int perPeriod = (_vwap ? 1 : 0) + (_std1 ? 2 : 0) + (_std2 ? 2 : 0);
                // se guardan de sobra (el máximo de «conservar»): cuántos se ven se decide al dibujar
                while (list.Count > 50 * Math.Max(1, perPeriod)) list.RemoveAt(0);
            }

            if (NewPeriod(bar, Periods[i])) _acc[i] = default;
            _acc[i].Add(s);
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        var on = new[] { Daily, Weekly, Monthly, Yearly };
        var colors = new[] { DailyColor, WeeklyColor, MonthlyColor, YearlyColor };
        int perPeriod = (_vwap ? 1 : 0) + (_std1 ? 2 : 0) + (_std2 ? 2 : 0);
        if (perPeriod == 0) return;
        int xEnd = ExtendRight ? ChartInfo.Region.Right : X(CurrentBar - 1, false);
        for (int i = 0; i < 4; i++)
        {
            if (!on[i]) continue;
            var list = _levels[i];
            var pen = Vp.Pen(colors[i], 1, LineDashStyle.Dash);
            for (int k = Math.Max(0, list.Count - Keep * perPeriod); k < list.Count; k++)
            {
                int y = Y(list[k].Price);
                context.DrawLine(pen, X(list[k].Bar), y, Math.Max(X(list[k].Bar) + 1, xEnd), y);
            }
        }
    }
}

/// <summary>Módulo 10 del «VP definitivo»: VWAP de una ventana móvil de tiempo (no se reinicia: no salta al abrir el período).</summary>
[Browsable(false)] // forma parte de «VWAP (bandas σ · Claire · VP definitivo)»
[DisplayName("VP definitivo · VWAP móvil")]
[Category(Vp.Category)]
[Description("VWAP de solo las últimas horas / días (la ventana se desliza con cada vela; no se reinicia en cada período), con bandas de desvío.")]
public class VpRollingVwap : VpPeriodVwapBase
{
    public enum Window
    {
        [Display(Name = "Automática (según el gráfico)")] Auto,
        [Display(Name = "1 hora")] Hour1,
        [Display(Name = "4 horas")] Hour4,
        [Display(Name = "1 día")] Day1,
        [Display(Name = "3 días")] Day3,
        [Display(Name = "1 semana")] Week1,
        [Display(Name = "1 mes")] Month1,
        [Display(Name = "3 meses")] Month3,
        [Display(Name = "1 año")] Year1,
        [Display(Name = "Minutos (a elección)")] Custom,
    }

    private readonly ValueDataSeries _vwap = new("RVWAP", "VWAP móvil") { Color = Color.FromArgb(255, 255, 152, 0), Width = 2, ShowZeroValue = false };
    private readonly ValueDataSeries _u2 = new("U2", "+2σ") { Color = Color.FromArgb(102, 255, 152, 0), ShowZeroValue = false };
    private readonly ValueDataSeries _u1 = new("U1", "+1σ") { Color = Color.FromArgb(153, 255, 152, 0), ShowZeroValue = false };
    private readonly ValueDataSeries _d1 = new("D1", "-1σ") { Color = Color.FromArgb(153, 255, 152, 0), ShowZeroValue = false };
    private readonly ValueDataSeries _d2 = new("D2", "-2σ") { Color = Color.FromArgb(102, 255, 152, 0), ShowZeroValue = false };
    private readonly RangeDataSeries _f1 = new("F1", "Relleno banda 1") { RangeColor = Color.FromArgb(20, 255, 152, 0) };
    private readonly Queue<(DateTime Time, VwapSum Sum)> _window = new();
    private VwapSum _acc;
    private Window _win = Window.Auto;
    private int _customMinutes = 1440, _minBars = 10;
    private bool _band1 = true, _band2, _fill;
    private decimal _m1 = 1, _m2 = 2;

    public VpRollingVwap()
    {
        foreach (var s in new[] { _vwap, _u2, _u1, _d1, _d2 }) DataSeries.Add(s);
        DataSeries.Add(_f1);
    }

    [Display(Name = "Ventana", GroupName = "Cálculo", Order = 10)]
    public Window WindowLength { get => _win; set { _win = value; RecalculateValues(); } }

    [Display(Name = "Ventana a elección (minutos)", GroupName = "Cálculo", Order = 20)]
    [Range(1, 1_000_000)]
    public int CustomMinutes { get => _customMinutes; set { _customMinutes = Math.Max(1, value); RecalculateValues(); } }

    [Display(Name = "Mínimo de velas en la ventana", GroupName = "Cálculo", Order = 30)]
    [Range(1, 1000)]
    public int MinBars { get => _minBars; set { _minBars = Math.Max(1, value); RecalculateValues(); } }

    [Display(Name = "Banda 1", GroupName = "Bandas", Order = 40)]
    public bool Band1 { get => _band1; set { _band1 = value; RecalculateValues(); } }

    [Display(Name = "Banda 1 (× σ)", GroupName = "Bandas", Order = 50)]
    public decimal Mult1 { get => _m1; set { _m1 = value; RecalculateValues(); } }

    [Display(Name = "Banda 2", GroupName = "Bandas", Order = 60)]
    public bool Band2 { get => _band2; set { _band2 = value; RecalculateValues(); } }

    [Display(Name = "Banda 2 (× σ)", GroupName = "Bandas", Order = 70)]
    public decimal Mult2 { get => _m2; set { _m2 = value; RecalculateValues(); } }

    [Display(Name = "Rellenar la banda 1", GroupName = "Bandas", Order = 80)]
    public bool Fill { get => _fill; set { _fill = value; RecalculateValues(); } }

    /// <summary>La ventana: la elegida o, en automático, como la del VWAP móvil de TradingView según la vela del gráfico.</summary>
    private TimeSpan Span()
    {
        var bar = BarLength;
        return _win switch
        {
            Window.Hour1 => TimeSpan.FromHours(1),
            Window.Hour4 => TimeSpan.FromHours(4),
            Window.Day1 => TimeSpan.FromDays(1),
            Window.Day3 => TimeSpan.FromDays(3),
            Window.Week1 => TimeSpan.FromDays(7),
            Window.Month1 => TimeSpan.FromDays(30.4375),
            Window.Month3 => TimeSpan.FromDays(91),
            Window.Year1 => TimeSpan.FromDays(365),
            Window.Custom => TimeSpan.FromMinutes(_customMinutes),
            _ => bar <= TimeSpan.FromMinutes(1) ? TimeSpan.FromHours(1)
                : bar <= TimeSpan.FromMinutes(5) ? TimeSpan.FromHours(4)
                : bar <= TimeSpan.FromHours(1) ? TimeSpan.FromDays(1)
                : bar <= TimeSpan.FromHours(4) ? TimeSpan.FromDays(3)
                : bar <= TimeSpan.FromHours(12) ? TimeSpan.FromDays(7)
                : bar <= TimeSpan.FromDays(1) ? TimeSpan.FromDays(30.4375)
                : bar <= TimeSpan.FromDays(7) ? TimeSpan.FromDays(90)
                : TimeSpan.FromDays(365),
        };
    }

    protected override void Reset()
    {
        _window.Clear();
        _acc = default;
    }

    private void Set(int bar, VwapSum a, int count)
    {
        if (count < _minBars || a.V <= 0)
        {
            foreach (var s in new[] { _vwap, _u2, _u1, _d1, _d2 }) s[bar] = 0;
            _f1[bar] = new RangeValue();
            return;
        }

        decimal v = a.Vwap, sd = a.StdDev;
        _vwap[bar] = v;
        _u1[bar] = _band1 ? v + _m1 * sd : 0;
        _d1[bar] = _band1 ? v - _m1 * sd : 0;
        _u2[bar] = _band2 ? v + _m2 * sd : 0;
        _d2[bar] = _band2 ? v - _m2 * sd : 0;
        _f1[bar] = _fill && _band1 ? new RangeValue(v + _m1 * sd, v - _m1 * sd) : new RangeValue();
    }

    protected override void Commit(int bar)
    {
        var c = GetCandle(bar);
        var s = Sum(bar);
        _window.Enqueue((c.Time, s));
        _acc.Add(s);
        var span = Span();
        while (_window.Count > 0 && c.Time - _window.Peek().Time >= span) _acc.Sub(_window.Dequeue().Sum);
        Set(bar, _acc, _window.Count);
    }

    protected override void Live(int bar)
    {
        if (bar <= Committed) return;
        var c = GetCandle(bar);
        var a = _acc;
        a.Add(Sum(bar));
        int count = _window.Count + 1;
        var span = Span();
        foreach (var (t, s) in _window)
        {
            if (c.Time - t < span) break;
            a.Sub(s);
            count--;
        }

        Set(bar, a, count);
    }
}
