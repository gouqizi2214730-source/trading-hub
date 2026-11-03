using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

using TradingHub.Indicators.Native.VpUltimate;

namespace TradingHub.Indicators.Native.Structure;

/// <summary>
/// Bandas de Bollinger de tres períodos a la vez (el del gráfico y dos mayores, como «Triple HTF Bollinger Bands»):
/// las de los períodos mayores usan solo velas ya cerradas (escalones), así no se repintan.
/// </summary>
[DisplayName("Bollinger triple (tres períodos)")]
[Category(IndicatorCategories.Trend)]
[Description("Bandas de Bollinger (20, 2) del período del gráfico y de dos períodos mayores, estas últimas con velas cerradas (sin repintar).")]
public class TripleBollinger : Indicator
{
    private readonly ValueDataSeries[] _series = new ValueDataSeries[9];
    private static readonly Color[] Colors = { Color.FromArgb(255, 41, 98, 255), Color.FromArgb(255, 255, 152, 0), Color.FromArgb(255, 156, 39, 176) };
    private int _len = 20;
    private double _mult = 2;
    private int _computed = -1;
    private DateTime _lastPass = DateTime.MinValue;
    private readonly List<double> _closes = new();
    private readonly List<long> _times = new();

    public TripleBollinger() : base(true)
    {
        for (int k = 0; k < 3; k++)
            foreach (var (name, j) in new[] { ("Media", 0), ("Superior", 1), ("Inferior", 2) })
            {
                var s = new ValueDataSeries($"BB{k}{j}", $"{name} {k + 1}") { Color = j == 0 ? Vp.Alpha(Colors[k], 160) : Colors[k], Width = j == 0 ? 1 : 2, ShowZeroValue = false };
                _series[k * 3 + j] = s;
                if (k == 0 && j == 0) DataSeries[0] = s;
                else DataSeries.Add(s);
            }
    }

    [Display(Name = "Período (velas)", GroupName = "Cálculo", Order = 10)]
    [Range(2, 500)]
    public int Length { get => _len; set { _len = Math.Clamp(value, 2, 500); RecalculateValues(); } }

    [Display(Name = "Desvíos", GroupName = "Cálculo", Order = 11)]
    [Range(0.1, 10)]
    public double Multiplier { get => _mult; set { _mult = Math.Clamp(value, 0.1, 10); RecalculateValues(); } }

    protected override void OnRecalculate()
    {
        _computed = -1;
        _closes.Clear();
        _times.Clear();
    }

    private static long Sec(DateTime t) => (long)(DateTime.SpecifyKind(t, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        if (bar < _closes.Count)
        {
            _closes[bar] = (double)c.Close;
        }
        else
        {
            while (_closes.Count < bar)
            {
                _closes.Add(_closes.Count > 0 ? _closes[^1] : (double)c.Close);
                _times.Add(_times.Count > 0 ? _times[^1] : Sec(c.Time));
            }

            _closes.Add((double)c.Close);
            _times.Add(Sec(c.Time));
        }

        if (bar < CurrentBar - 1) return;
        // una pasada por vela nueva (todas las series de una vez) y, con la que se forma, como mucho cada 2 segundos
        if (_computed == CurrentBar && (DateTime.UtcNow - _lastPass).TotalSeconds < 2) return;
        _computed = CurrentBar;
        _lastPass = DateTime.UtcNow;
        var len = ChartInfo?.GetTimeFrameTypeChartPeriod() ?? TimeSpan.FromMinutes(5);
        if (len <= TimeSpan.Zero) len = TimeSpan.FromMinutes(5);
        var (ta, tb, tc) = Smc.BollingerTimeframes(len);
        var bars = new List<Bar>(_closes.Count);
        for (int i = 0; i < _closes.Count; i++) bars.Add(new Bar(_times[i], _closes[i], _closes[i], _closes[i], _closes[i], 0));
        var tfs = new[] { ta, tb, tc };
        long own = (long)len.TotalSeconds;
        for (int k = 0; k < 3; k++)
        {
            if (tfs[k] <= own)
            {
                var (mid, up, dn) = Smc.Bollinger(_closes, _len, _mult);
                for (int i = 0; i < _closes.Count; i++) Set(k, i, mid[i], up[i], dn[i]);
                continue;
            }

            var h = Smc.Aggregate(bars, tfs[k]);
            var (hm, hu, hd) = Smc.Bollinger(h.Select(x => x.C).ToList(), _len, _mult);
            // valor de la última vela mayor ya cerrada en cada vela del gráfico
            int j = 0;
            for (int i = 0; i < _closes.Count; i++)
            {
                while (j + 1 < h.Count && h[j + 1].T <= _times[i]) j++;
                int closed = h[j].T <= _times[i] && j > 0 ? j - 1 : -1;
                if (closed >= 0) Set(k, i, hm[closed], hu[closed], hd[closed]);
                else Set(k, i, double.NaN, double.NaN, double.NaN);
            }
        }
    }

    private void Set(int k, int i, double mid, double up, double dn)
    {
        _series[k * 3][i] = double.IsNaN(mid) ? 0 : (decimal)mid;
        _series[k * 3 + 1][i] = double.IsNaN(up) ? 0 : (decimal)up;
        _series[k * 3 + 2][i] = double.IsNaN(dn) ? 0 : (decimal)dn;
    }
}
