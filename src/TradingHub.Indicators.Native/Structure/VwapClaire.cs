using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

namespace TradingHub.Indicators.Native.Structure;

/// <summary>
/// VWAP «Claire»: los VWAP rodantes de 30, 90 y 365 días (se arman con velas diarias, el valor de cada día es el de
/// hasta el día anterior: no cambia durante la sesión) y, si se quiere, los VWAP anclados al día, la semana y el mes.
/// </summary>
[Browsable(false)] // forma parte de «VWAP (bandas σ · Claire · VP definitivo)»
[DisplayName("VWAP Claire (rodantes 30 / 90 / 365 días + día / semana / mes)")]
[Category(IndicatorCategories.Vwap)]
[Description("VWAP rodantes de 30, 90 y 365 días (con velas diarias, sin repintar en el día) y VWAP anclados al día, la semana y el mes.")]
public class VwapClaire : Indicator
{
    private readonly ValueDataSeries _r30 = new("R30", "VWAP 30 días") { Color = Color.FromArgb(255, 238, 223, 123), Width = 2, ShowZeroValue = false };
    private readonly ValueDataSeries _r90 = new("R90", "VWAP 90 días") { Color = Color.FromArgb(255, 187, 212, 188), Width = 2, ShowZeroValue = false };
    private readonly ValueDataSeries _r365 = new("R365", "VWAP 365 días") { Color = Color.FromArgb(255, 173, 213, 86), Width = 2, ShowZeroValue = false };
    private readonly ValueDataSeries _d = new("D", "VWAP del día") { Color = Color.FromArgb(255, 83, 167, 176), Width = 1, ShowZeroValue = false };
    private readonly ValueDataSeries _w = new("W", "VWAP de la semana") { Color = Color.FromArgb(255, 143, 167, 255), Width = 1, ShowZeroValue = false };
    private readonly ValueDataSeries _m = new("M", "VWAP del mes") { Color = Color.FromArgb(255, 211, 140, 255), Width = 1, ShowZeroValue = false };
    private readonly Dictionary<long, (double Pv, double V)> _days = new();
    private readonly SortedSet<long> _dayKeys = new();
    private long _curDay = long.MinValue, _curWeek = long.MinValue, _curMonth = long.MinValue;
    private double _dPv, _dV, _wPv, _wV, _mPv, _mV;
    private long _lastRollingDay = long.MinValue;
    private (double R30, double R90, double R365) _rolling;

    public VwapClaire() : base(true)
    {
        DataSeries[0] = _r30;
        DataSeries.Add(_r90);
        DataSeries.Add(_r365);
        DataSeries.Add(_d);
        DataSeries.Add(_w);
        DataSeries.Add(_m);
        _d.VisualType = VisualMode.Hide;
        _w.VisualType = VisualMode.Hide;
        _m.VisualType = VisualMode.Hide;
    }

    [Display(Name = "VWAP del día", GroupName = "Anclados", Order = 10)]
    public bool Daily { get => _d.VisualType != VisualMode.Hide; set { _d.VisualType = value ? VisualMode.Line : VisualMode.Hide; RecalculateValues(); } }

    [Display(Name = "VWAP de la semana", GroupName = "Anclados", Order = 11)]
    public bool Weekly { get => _w.VisualType != VisualMode.Hide; set { _w.VisualType = value ? VisualMode.Line : VisualMode.Hide; RecalculateValues(); } }

    [Display(Name = "VWAP del mes", GroupName = "Anclados", Order = 12)]
    public bool Monthly { get => _m.VisualType != VisualMode.Hide; set { _m.VisualType = value ? VisualMode.Line : VisualMode.Hide; RecalculateValues(); } }

    protected override void OnRecalculate()
    {
        _days.Clear();
        _dayKeys.Clear();
        _curDay = _curWeek = _curMonth = long.MinValue;
        _dPv = _dV = _wPv = _wV = _mPv = _mV = 0;
        _lastRollingDay = long.MinValue;
    }

    private static long DayKey(DateTime t) => (long)(DateTime.SpecifyKind(t, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalDays;

    /// <summary>VWAP rodante de N días hasta el día anterior a <paramref name="day"/>.</summary>
    private double Rolling(long day, int n)
    {
        double pv = 0, v = 0;
        int count = 0;
        foreach (var k in _dayKeys.Reverse())
        {
            if (k >= day) continue;
            if (count++ >= n) break;
            var d = _days[k];
            pv += d.Pv;
            v += d.V;
        }

        return v > 0 && count > 0 ? pv / v : 0;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        double tp = (double)(c.High + c.Low + c.Close) / 3, vol = (double)c.Volume;
        long day = DayKey(c.Time);
        long week = (day + 3) / 7; // lunes empieza la semana
        long month = c.Time.Year * 12 + c.Time.Month;

        // acumulados del día (para los rodantes) — la vela en formación se reescribe: se guarda lo cerrado por día
        if (day != _curDay)
        {
            _curDay = day;
            _dPv = _dV = 0;
            _dayKeys.Add(day);
        }

        if (week != _curWeek)
        {
            _curWeek = week;
            _wPv = _wV = 0;
        }

        if (month != _curMonth)
        {
            _curMonth = month;
            _mPv = _mV = 0;
        }

        // la última vela puede volver a calcularse varias veces: se reconstruye el día desde las velas de ese día
        if (bar == CurrentBar - 1)
        {
            double pv = 0, v = 0, wpv = 0, wv = 0, mpv = 0, mv = 0;
            for (int i = bar; i >= 0; i--)
            {
                var x = GetCandle(i);
                long dk = DayKey(x.Time);
                if ((dk + 3) / 7 != week && x.Time.Year * 12 + x.Time.Month != month) break;
                double xtp = (double)(x.High + x.Low + x.Close) / 3, xv = (double)x.Volume;
                if (dk == day)
                {
                    pv += xtp * xv;
                    v += xv;
                }

                if ((dk + 3) / 7 == week)
                {
                    wpv += xtp * xv;
                    wv += xv;
                }

                if (x.Time.Year * 12 + x.Time.Month == month)
                {
                    mpv += xtp * xv;
                    mv += xv;
                }
            }

            _days[day] = (pv, v);
            Write(bar, day, pv, v, wpv, wv, mpv, mv);
            return;
        }

        _dPv += tp * vol;
        _dV += vol;
        _wPv += tp * vol;
        _wV += vol;
        _mPv += tp * vol;
        _mV += vol;
        _days[day] = (_dPv, _dV);
        Write(bar, day, _dPv, _dV, _wPv, _wV, _mPv, _mV);
    }

    private void Write(int bar, long day, double dPv, double dV, double wPv, double wV, double mPv, double mV)
    {
        if (day != _lastRollingDay)
        {
            _lastRollingDay = day;
            _rolling = (Rolling(day, 30), Rolling(day, 90), Rolling(day, 365));
        }

        _r30[bar] = (decimal)_rolling.R30;
        _r90[bar] = (decimal)_rolling.R90;
        _r365[bar] = (decimal)_rolling.R365;
        _d[bar] = dV > 0 ? (decimal)(dPv / dV) : 0;
        _w[bar] = wV > 0 ? (decimal)(wPv / wV) : 0;
        _m[bar] = mV > 0 ? (decimal)(mPv / mV) : 0;
    }
}
