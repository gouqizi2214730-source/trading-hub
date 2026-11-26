using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Indicators.Drawing;
using TradingHub.Indicators.Native.Structure;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.Classic;

public enum AdaptiveMaKind
{
    [Display(Name = "Media móvil de Hull (HMA)")] Hull,
    [Display(Name = "Media adaptativa de Kaufman (KAMA)")] Kama,
}

[DisplayName("Media móvil de Hull y Adaptive EMA (HMA / KAMA)")]
[Category(IndicatorCategories.MovingAverages)]
[Description("Media móvil rápida de Hull (HMA) o adaptativa por relación de eficiencia de Kaufman (KAMA), con color por dirección de la pendiente.")]
public sealed class HullKalmanMa : Indicator
{
    private readonly ValueDataSeries _line = new("Line", "Media") { Width = 2, Color = Color.FromArgb(255, 38, 166, 154) };
    private readonly ValueDataSeries _halfWma = new("HalfWma");
    private readonly ValueDataSeries _fullWma = new("FullWma");
    private readonly ValueDataSeries _diff = new("Diff");
    private int _period = 21;
    private AdaptiveMaKind _kind = AdaptiveMaKind.Hull;

    public HullKalmanMa() : base(true)
    {
        DataSeries[0] = _line;
    }

    [Display(Name = "Tipo", GroupName = "Cálculo", Order = 1)]
    public AdaptiveMaKind Kind { get => _kind; set { _kind = value; RecalculateValues(); } }

    [Display(Name = "Período", GroupName = "Cálculo", Order = 2)]
    [Range(2, 500)]
    public int Period { get => _period; set { _period = Math.Clamp(value, 2, 500); RecalculateValues(); } }

    [Display(Name = "Color alcista", GroupName = "Colores", Order = 10)]
    public Color UpColor { get; set; } = Color.FromArgb(255, 38, 166, 154);

    [Display(Name = "Color bajista", GroupName = "Colores", Order = 11)]
    public Color DownColor { get; set; } = Color.FromArgb(255, 239, 83, 80);

    protected override void OnRecalculate()
    {
        _halfWma.Clear();
        _fullWma.Clear();
        _diff.Clear();
    }

    private decimal CalcWmaFromClose(int bar, int len)
    {
        int start = Math.Max(0, bar - len + 1);
        decimal num = 0, den = 0;
        int w = 1;
        for (int i = start; i <= bar; i++, w++)
        {
            num += GetCandle(i).Close * w;
            den += w;
        }
        return den > 0 ? num / den : GetCandle(bar).Close;
    }

    private static decimal CalcWmaSeries(ValueDataSeries s, int bar, int len)
    {
        int start = Math.Max(0, bar - len + 1);
        decimal num = 0, den = 0;
        int w = 1;
        for (int i = start; i <= bar; i++, w++)
        {
            num += s[i] * w;
            den += w;
        }
        return den > 0 ? num / den : s[bar];
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        decimal close = GetCandle(bar).Close;
        if (_kind == AdaptiveMaKind.Hull)
        {
            int half = Math.Max(1, _period / 2);
            int sqrt = Math.Max(1, (int)Math.Round(Math.Sqrt(_period)));
            _halfWma[bar] = CalcWmaFromClose(bar, half);
            _fullWma[bar] = CalcWmaFromClose(bar, _period);
            _diff[bar] = 2m * _halfWma[bar] - _fullWma[bar];
            _line[bar] = CalcWmaSeries(_diff, bar, sqrt);
        }
        else
        {
            if (bar < _period)
            {
                _line[bar] = close;
            }
            else
            {
                decimal change = Math.Abs(close - GetCandle(bar - _period).Close);
                decimal vol = 0;
                for (int i = bar - _period + 1; i <= bar; i++)
                    vol += Math.Abs(GetCandle(i).Close - GetCandle(i - 1).Close);
                decimal er = vol > 0 ? change / vol : 0;
                decimal fastSc = 2m / (2m + 1m);
                decimal slowSc = 2m / (30m + 1m);
                decimal sc = er * (fastSc - slowSc) + slowSc;
                sc *= sc;
                decimal prev = _line[bar - 1] == 0 ? GetCandle(bar - 1).Close : _line[bar - 1];
                _line[bar] = prev + sc * (close - prev);
            }
        }

        bool up = bar == 0 || _line[bar] >= _line[bar - 1];
        _line.Colors[bar] = up ? UpColor : DownColor;
    }
}

[DisplayName("SuperTrend ATR (tendencia y trailing)")]
[Category(IndicatorCategories.TrendStrength)]
[Description("Seguimiento de tendencia y stop dinámico por múltiplo de ATR, sin repintado.")]
public sealed class SuperTrendPro : Indicator
{
    private readonly ValueDataSeries _upTrend = new("UpTrend", "Soporte alcista") { Width = 2, Color = Color.FromArgb(255, 38, 166, 154) };
    private readonly ValueDataSeries _dnTrend = new("DnTrend", "Resistencia bajista") { Width = 2, Color = Color.FromArgb(255, 239, 83, 80) };
    private readonly ValueDataSeries _tr = new("TR");
    private readonly ValueDataSeries _atr = new("ATR");
    private readonly ValueDataSeries _upperBand = new("UB");
    private readonly ValueDataSeries _lowerBand = new("LB");
    private readonly ValueDataSeries _dir = new("Dir");
    private int _period = 10;
    private decimal _mult = 3.0m;

    public SuperTrendPro() : base(true)
    {
        DataSeries[0] = _upTrend;
        DataSeries.Add(_dnTrend);
    }

    [Display(Name = "Período ATR", GroupName = "Cálculo", Order = 1)]
    [Range(2, 200)]
    public int Period { get => _period; set { _period = Math.Clamp(value, 2, 200); RecalculateValues(); } }

    [Display(Name = "Multiplicador ATR", GroupName = "Cálculo", Order = 2)]
    [Range(0.5, 20)]
    public decimal Multiplier { get => _mult; set { _mult = Math.Clamp(value, 0.5m, 20m); RecalculateValues(); } }

    protected override void OnRecalculate()
    {
        _tr.Clear();
        _atr.Clear();
        _upperBand.Clear();
        _lowerBand.Clear();
        _dir.Clear();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        decimal prevClose = bar > 0 ? GetCandle(bar - 1).Close : c.Close;
        decimal tr = Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose)));
        _tr[bar] = tr;
        _atr[bar] = bar == 0 ? tr : (_atr[bar - 1] * (_period - 1) + tr) / _period;

        decimal hl2 = (c.High + c.Low) / 2m;
        decimal basicUpper = hl2 + _mult * _atr[bar];
        decimal basicLower = hl2 - _mult * _atr[bar];

        if (bar == 0)
        {
            _upperBand[bar] = basicUpper;
            _lowerBand[bar] = basicLower;
            _dir[bar] = 1;
            _upTrend[bar] = basicLower;
            _dnTrend[bar] = 0;
            return;
        }

        decimal prevUpper = _upperBand[bar - 1];
        decimal prevLower = _lowerBand[bar - 1];
        _upperBand[bar] = (basicUpper < prevUpper || prevClose > prevUpper) ? basicUpper : prevUpper;
        _lowerBand[bar] = (basicLower > prevLower || prevClose < prevLower) ? basicLower : prevLower;

        decimal prevDir = _dir[bar - 1] == 0 ? 1 : _dir[bar - 1];
        decimal dir = prevDir;
        if (prevDir < 0 && c.Close > _upperBand[bar]) dir = 1;
        else if (prevDir > 0 && c.Close < _lowerBand[bar]) dir = -1;
        _dir[bar] = dir;

        if (dir > 0)
        {
            if (prevDir < 0 && bar > 0) _upTrend.SetPointOfEndLine(bar - 1);
            _upTrend[bar] = _lowerBand[bar];
            _dnTrend[bar] = 0;
            _dnTrend.SetPointOfEndLine(bar);
        }
        else
        {
            if (prevDir > 0 && bar > 0) _dnTrend.SetPointOfEndLine(bar - 1);
            _dnTrend[bar] = _upperBand[bar];
            _upTrend[bar] = 0;
            _upTrend.SetPointOfEndLine(bar);
        }
    }
}

[DisplayName("ADX y DMI (fuerza direccional)")]
[Category(IndicatorCategories.TrendStrength)]
[Description("Índice de movimiento direccional promedio (ADX) con +DI y -DI de Wilder para medir la fuerza y dirección de la tendencia.")]
public sealed class AdxDmi : Indicator
{
    private readonly ValueDataSeries _adx = new("ADX", "ADX") { Width = 2, Color = Color.FromArgb(255, 251, 140, 0) };
    private readonly ValueDataSeries _plusDi = new("PlusDI", "+DI") { Width = 1, Color = Color.FromArgb(255, 38, 166, 154) };
    private readonly ValueDataSeries _minusDi = new("MinusDI", "-DI") { Width = 1, Color = Color.FromArgb(255, 239, 83, 80) };
    private readonly ValueDataSeries _trSm = new("TrSm");
    private readonly ValueDataSeries _pDmSm = new("PDmSm");
    private readonly ValueDataSeries _mDmSm = new("MDmSm");
    private readonly ValueDataSeries _dx = new("DX");
    private int _period = 14;

    public AdxDmi() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _adx;
        DataSeries.Add(_plusDi);
        DataSeries.Add(_minusDi);
        LineSeries.Add(new LineSeries("TrendLevel", "Tendencia fuerte (25)") { Value = 25, Color = Color.FromArgb(140, 140, 145, 160), LineDashStyle = LineDashStyle.Dash });
    }

    [Display(Name = "Período", GroupName = "Cálculo", Order = 1)]
    [Range(2, 200)]
    public int Period { get => _period; set { _period = Math.Clamp(value, 2, 200); RecalculateValues(); } }

    protected override void OnRecalculate()
    {
        _trSm.Clear();
        _pDmSm.Clear();
        _mDmSm.Clear();
        _dx.Clear();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        if (bar == 0)
        {
            _trSm[0] = Math.Max(0.00000001m, c.High - c.Low);
            _pDmSm[0] = 0;
            _mDmSm[0] = 0;
            _plusDi[0] = 0;
            _minusDi[0] = 0;
            _dx[0] = 0;
            _adx[0] = 0;
            return;
        }

        var p = GetCandle(bar - 1);
        decimal upMove = c.High - p.High;
        decimal downMove = p.Low - c.Low;
        decimal pDm = (upMove > downMove && upMove > 0) ? upMove : 0;
        decimal mDm = (downMove > upMove && downMove > 0) ? downMove : 0;
        decimal tr = Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - p.Close), Math.Abs(c.Low - p.Close)));

        _trSm[bar] = (_trSm[bar - 1] * (_period - 1) + tr) / _period;
        _pDmSm[bar] = (_pDmSm[bar - 1] * (_period - 1) + pDm) / _period;
        _mDmSm[bar] = (_mDmSm[bar - 1] * (_period - 1) + mDm) / _period;

        decimal trVal = _trSm[bar];
        decimal pDi = trVal > 0 ? 100m * _pDmSm[bar] / trVal : 0;
        decimal mDi = trVal > 0 ? 100m * _mDmSm[bar] / trVal : 0;
        _plusDi[bar] = pDi;
        _minusDi[bar] = mDi;

        decimal sumDi = pDi + mDi;
        _dx[bar] = sumDi > 0 ? 100m * Math.Abs(pDi - mDi) / sumDi : 0;
        _adx[bar] = (_adx[bar - 1] * (_period - 1) + _dx[bar]) / _period;
    }
}

[DisplayName("RSI Estocástico (StochRSI %K / %D)")]
[Category(IndicatorCategories.Oscillators)]
[Description("Oscilador RSI Estocástico con líneas %K y %D suavizadas y niveles 80 / 20.")]
public sealed class StochasticRsi : Indicator
{
    private readonly ValueDataSeries _k = new("K", "%K") { Width = 2, Color = Color.FromArgb(255, 41, 98, 255) };
    private readonly ValueDataSeries _d = new("D", "%D") { Width = 1, Color = Color.FromArgb(255, 251, 140, 0) };
    private readonly ValueDataSeries _rsi = new("RSI");
    private readonly ValueDataSeries _rawK = new("RawK");
    private readonly ValueDataSeries _gain = new("Gain");
    private readonly ValueDataSeries _loss = new("Loss");
    private int _rsiPeriod = 14, _stochPeriod = 14, _smoothK = 3, _smoothD = 3;

    public StochasticRsi() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _k;
        DataSeries.Add(_d);
        LineSeries.Add(new LineSeries("80", "Sobrecompra") { Value = 80, Color = Color.FromArgb(140, 239, 83, 80), LineDashStyle = LineDashStyle.Dash });
        LineSeries.Add(new LineSeries("20", "Sobreventa") { Value = 20, Color = Color.FromArgb(140, 38, 166, 154), LineDashStyle = LineDashStyle.Dash });
    }

    [Display(Name = "Período RSI", GroupName = "Cálculo", Order = 1)]
    [Range(2, 200)]
    public int RsiPeriod { get => _rsiPeriod; set { _rsiPeriod = Math.Clamp(value, 2, 200); RecalculateValues(); } }

    [Display(Name = "Período Estocástico", GroupName = "Cálculo", Order = 2)]
    [Range(2, 200)]
    public int StochPeriod { get => _stochPeriod; set { _stochPeriod = Math.Clamp(value, 2, 200); RecalculateValues(); } }

    [Display(Name = "Suavizado %K", GroupName = "Cálculo", Order = 3)]
    [Range(1, 50)]
    public int SmoothK { get => _smoothK; set { _smoothK = Math.Clamp(value, 1, 50); RecalculateValues(); } }

    [Display(Name = "Suavizado %D", GroupName = "Cálculo", Order = 4)]
    [Range(1, 50)]
    public int SmoothD { get => _smoothD; set { _smoothD = Math.Clamp(value, 1, 50); RecalculateValues(); } }

    protected override void OnRecalculate()
    {
        _rsi.Clear();
        _rawK.Clear();
        _gain.Clear();
        _loss.Clear();
    }

    private static decimal Sma(ValueDataSeries s, int bar, int len)
    {
        int start = Math.Max(0, bar - len + 1);
        decimal sum = 0;
        for (int i = start; i <= bar; i++) sum += s[i];
        return sum / (bar - start + 1);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        decimal close = GetCandle(bar).Close;
        if (bar == 0)
        {
            _rsi[0] = 50;
            _rawK[0] = 50;
            _k[0] = 50;
            _d[0] = 50;
            return;
        }

        decimal diff = close - GetCandle(bar - 1).Close;
        decimal g = Math.Max(0, diff), l = Math.Max(0, -diff);
        _gain[bar] = (_gain[bar - 1] * (_rsiPeriod - 1) + g) / _rsiPeriod;
        _loss[bar] = (_loss[bar - 1] * (_rsiPeriod - 1) + l) / _rsiPeriod;
        _rsi[bar] = _loss[bar] == 0 ? 100m : 100m - 100m / (1m + _gain[bar] / _loss[bar]);

        int start = Math.Max(0, bar - _stochPeriod + 1);
        decimal lo = _rsi[start], hi = _rsi[start];
        for (int i = start + 1; i <= bar; i++)
        {
            if (_rsi[i] < lo) lo = _rsi[i];
            if (_rsi[i] > hi) hi = _rsi[i];
        }

        _rawK[bar] = hi > lo ? 100m * (_rsi[bar] - lo) / (hi - lo) : 50m;
        _k[bar] = Sma(_rawK, bar, _smoothK);
        _d[bar] = Sma(_k, bar, _smoothD);
    }
}

[DisplayName("Canales de Keltner y Donchian (Squeeze)")]
[Category(IndicatorCategories.Volatility)]
[Description("Canal de Keltner por ATR y canal de ruptura de Donchian superpuestos en el gráfico de precios.")]
public sealed class KeltnerDonchianChannels : Indicator
{
    private readonly ValueDataSeries _ema = new("Mid", "EMA Keltner") { Color = Color.FromArgb(220, 251, 140, 0), Width = 1 };
    private readonly ValueDataSeries _kUp = new("KUp", "Keltner superior") { Color = Color.FromArgb(220, 41, 98, 255), Width = 1 };
    private readonly ValueDataSeries _kDn = new("KDn", "Keltner inferior") { Color = Color.FromArgb(220, 41, 98, 255), Width = 1 };
    private readonly ValueDataSeries _dUp = new("DUp", "Donchian máximo") { Color = Color.FromArgb(180, 38, 166, 154), LineDashStyle = LineDashStyle.Dash };
    private readonly ValueDataSeries _dDn = new("DDn", "Donchian mínimo") { Color = Color.FromArgb(180, 239, 83, 80), LineDashStyle = LineDashStyle.Dash };
    private readonly ValueDataSeries _atr = new("ATR");
    private int _period = 20;
    private decimal _atrMult = 1.5m;

    public KeltnerDonchianChannels() : base(true)
    {
        DataSeries[0] = _ema;
        DataSeries.Add(_kUp);
        DataSeries.Add(_kDn);
        DataSeries.Add(_dUp);
        DataSeries.Add(_dDn);
    }

    [Display(Name = "Período", GroupName = "Cálculo", Order = 1)]
    [Range(2, 500)]
    public int Period { get => _period; set { _period = Math.Clamp(value, 2, 500); RecalculateValues(); } }

    [Display(Name = "Multiplicador ATR", GroupName = "Cálculo", Order = 2)]
    [Range(0.2, 10)]
    public decimal AtrMultiplier { get => _atrMult; set { _atrMult = Math.Clamp(value, 0.2m, 10m); RecalculateValues(); } }

    protected override void OnRecalculate() => _atr.Clear();

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        decimal prevClose = bar > 0 ? GetCandle(bar - 1).Close : c.Close;
        decimal tr = Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose)));
        _atr[bar] = bar == 0 ? tr : (_atr[bar - 1] * (_period - 1) + tr) / _period;

        decimal k = 2m / (_period + 1m);
        _ema[bar] = bar == 0 ? c.Close : c.Close * k + _ema[bar - 1] * (1m - k);
        _kUp[bar] = _ema[bar] + _atrMult * _atr[bar];
        _kDn[bar] = _ema[bar] - _atrMult * _atr[bar];

        int start = Math.Max(0, bar - _period + 1);
        decimal hi = c.High, lo = c.Low;
        for (int i = start; i < bar; i++)
        {
            var b = GetCandle(i);
            if (b.High > hi) hi = b.High;
            if (b.Low < lo) lo = b.Low;
        }
        _dUp[bar] = hi;
        _dDn[bar] = lo;
    }
}

[DisplayName("Volatilidad histórica Yang-Zhang y Parkinson (HV%)")]
[Category(IndicatorCategories.Volatility)]
[Description("Estimador de volatilidad realizada anualizada Yang-Zhang (saltos de apertura + rango OHLC) comparado con el estimador de rango de Parkinson.")]
public sealed class YangZhangVolatility : Indicator
{
    private readonly ValueDataSeries _yz = new("YZ", "Yang-Zhang HV%") { Width = 2, Color = Color.FromArgb(255, 180, 120, 255) };
    private readonly ValueDataSeries _pk = new("Parkinson", "Parkinson HV%") { Width = 1, Color = Color.FromArgb(220, 38, 166, 154), LineDashStyle = LineDashStyle.Dash };
    private int _period = 20;
    private int _annualPeriods = 365;

    public YangZhangVolatility() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _yz;
        DataSeries.Add(_pk);
    }

    [Display(Name = "Ventana (velas)", GroupName = "Cálculo", Order = 1)]
    [Range(5, 500)]
    public int Period { get => _period; set { _period = Math.Clamp(value, 5, 500); RecalculateValues(); } }

    [Display(Name = "Períodos por año (anualización)", GroupName = "Cálculo", Order = 2)]
    [Range(1, 525600)]
    public int AnnualPeriods { get => _annualPeriods; set { _annualPeriods = Math.Clamp(value, 1, 525600); RecalculateValues(); } }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < 2)
        {
            _yz[bar] = 0;
            _pk[bar] = 0;
            return;
        }

        int start = Math.Max(1, bar - _period + 1);
        int n = bar - start + 1;
        if (n < 2)
        {
            _yz[bar] = _yz[bar - 1];
            _pk[bar] = _pk[bar - 1];
            return;
        }

        double sumOc = 0, sumCo = 0, sumRs = 0, sumPk = 0;
        var o2cList = new double[n];
        var c2oList = new double[n];
        for (int i = 0; i < n; i++)
        {
            var cur = GetCandle(start + i);
            var prev = GetCandle(start + i - 1);
            double o = Math.Max(1e-9, (double)cur.Open);
            double h = Math.Max(o, (double)cur.High);
            double l = Math.Min(o, Math.Max(1e-9, (double)cur.Low));
            double c = Math.Max(1e-9, (double)cur.Close);
            double pc = Math.Max(1e-9, (double)prev.Close);

            double co = Math.Log(o / pc);
            double oc = Math.Log(c / o);
            double u = Math.Log(h / o);
            double d = Math.Log(l / o);
            double hl = Math.Log(h / l);

            c2oList[i] = co;
            o2cList[i] = oc;
            sumCo += co;
            sumOc += oc;
            sumRs += u * (u - oc) + d * (d - oc);
            sumPk += hl * hl;
        }

        double meanCo = sumCo / n;
        double meanOc = sumOc / n;
        double varCo = 0, varOc = 0;
        for (int i = 0; i < n; i++)
        {
            double dCo = c2oList[i] - meanCo;
            double dOc = o2cList[i] - meanOc;
            varCo += dCo * dCo;
            varOc += dOc * dOc;
        }
        varCo /= (n - 1);
        varOc /= (n - 1);
        double varRs = sumRs / n;
        double k = 0.34 / (1.34 + (n + 1.0) / (n - 1.0));
        double varYz = Math.Max(0, varCo + k * varOc + (1.0 - k) * varRs);
        double varPk = Math.Max(0, sumPk / (4.0 * n * Math.Log(2.0)));

        double ann = Math.Max(1, _annualPeriods);
        _yz[bar] = (decimal)(Math.Sqrt(varYz * ann) * 100.0);
        _pk[bar] = (decimal)(Math.Sqrt(varPk * ann) * 100.0);
    }
}

public enum MoneyFlowMode
{
    [Display(Name = "Chaikin Money Flow (CMF)")] Cmf,
    [Display(Name = "Money Flow Index (MFI)")] Mfi,
    [Display(Name = "On-Balance Volume (OBV)")] Obv,
}

[DisplayName("Flujo de dinero (OBV · CMF · MFI)")]
[Category(IndicatorCategories.Volume)]
[Description("Suite de volumen clásico: Chaikin Money Flow (CMF), Money Flow Index (MFI) y On-Balance Volume (OBV).")]
public sealed class MoneyFlowSuite : Indicator
{
    private readonly ValueDataSeries _val = new("Flow", "Flujo") { Width = 2, Color = Color.FromArgb(255, 38, 166, 154) };
    private readonly ValueDataSeries _obv = new("OBV");
    private MoneyFlowMode _mode = MoneyFlowMode.Cmf;
    private int _period = 20;

    public MoneyFlowSuite() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _val;
    }

    [Display(Name = "Modo", GroupName = "Cálculo", Order = 1)]
    public MoneyFlowMode Mode { get => _mode; set { _mode = value; RecalculateValues(); } }

    [Display(Name = "Período", GroupName = "Cálculo", Order = 2)]
    [Range(2, 500)]
    public int Period { get => _period; set { _period = Math.Clamp(value, 2, 500); RecalculateValues(); } }

    protected override void OnRecalculate() => _obv.Clear();

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        if (bar == 0)
        {
            _obv[0] = c.Volume;
        }
        else
        {
            decimal prevClose = GetCandle(bar - 1).Close;
            decimal dir = c.Close > prevClose ? 1m : c.Close < prevClose ? -1m : 0m;
            _obv[bar] = _obv[bar - 1] + dir * c.Volume;
        }

        if (_mode == MoneyFlowMode.Obv)
        {
            _val[bar] = _obv[bar];
            return;
        }

        int start = Math.Max(0, bar - _period + 1);
        if (_mode == MoneyFlowMode.Cmf)
        {
            decimal sumAd = 0, sumVol = 0;
            for (int i = start; i <= bar; i++)
            {
                var b = GetCandle(i);
                decimal range = b.High - b.Low;
                decimal mfm = range > 0 ? ((b.Close - b.Low) - (b.High - b.Close)) / range : 0m;
                sumAd += mfm * b.Volume;
                sumVol += b.Volume;
            }
            _val[bar] = sumVol > 0 ? sumAd / sumVol : 0m;
        }
        else
        {
            decimal posMf = 0, negMf = 0;
            for (int i = Math.Max(1, start); i <= bar; i++)
            {
                var cur = GetCandle(i);
                var prev = GetCandle(i - 1);
                decimal tpCur = (cur.High + cur.Low + cur.Close) / 3m;
                decimal tpPrev = (prev.High + prev.Low + prev.Close) / 3m;
                decimal rawMf = tpCur * cur.Volume;
                if (tpCur > tpPrev) posMf += rawMf;
                else if (tpCur < tpPrev) negMf += rawMf;
            }
            _val[bar] = negMf == 0 ? 100m : 100m - 100m / (1m + posMf / negMf);
        }
    }
}

[DisplayName("Escáner de patrones de velas (Engulfing · PinBar · Inside · Doji)")]
[Category(IndicatorCategories.Patterns)]
[Description("Detecta patrones clásicos de acción del precio en velas cerradas (Envolvente, PinBar, Inside Bar y Doji) filtrados por rango relativo ATR.")]
public sealed class CandlePatternScanner : SceneIndicator
{
    private bool _showEngulfing = true;
    private bool _showPinBar = true;
    private bool _showInsideBar = true;
    private bool _showDoji;
    private double _minAtrRatio = 0.7;

    [Display(Name = "Envolvente (Engulfing)", GroupName = "Patrones", Order = 1)]
    public bool ShowEngulfing { get => _showEngulfing; set { _showEngulfing = value; RecalculateValues(); } }

    [Display(Name = "PinBar (Martillo / Estrella fugaz)", GroupName = "Patrones", Order = 2)]
    public bool ShowPinBar { get => _showPinBar; set { _showPinBar = value; RecalculateValues(); } }

    [Display(Name = "Vela interior (Inside Bar)", GroupName = "Patrones", Order = 3)]
    public bool ShowInsideBar { get => _showInsideBar; set { _showInsideBar = value; RecalculateValues(); } }

    [Display(Name = "Doji de indecisión", GroupName = "Patrones", Order = 4)]
    public bool ShowDoji { get => _showDoji; set { _showDoji = value; RecalculateValues(); } }

    [Display(Name = "Rango mínimo (× ATR 14)", GroupName = "Filtro", Order = 10)]
    [Range(0.1, 5.0)]
    public double MinAtrRatio { get => _minAtrRatio; set { _minAtrRatio = Math.Clamp(value, 0.1, 5.0); RecalculateValues(); } }

    protected override void Build(Scene s)
    {
        int last = LastClosed;
        if (last < 15) return;

        var atr = new double[last + 1];
        double sumTr = 0;
        for (int i = 0; i <= last; i++)
        {
            var b = Bars[i];
            double pc = i > 0 ? Bars[i - 1].C : b.O;
            double tr = Math.Max(b.H - b.L, Math.Max(Math.Abs(b.H - pc), Math.Abs(b.L - pc)));
            if (i < 14)
            {
                sumTr += tr;
                atr[i] = sumTr / (i + 1);
            }
            else
            {
                atr[i] = (atr[i - 1] * 13.0 + tr) / 14.0;
            }
        }

        int bullEng = 0, bearEng = 0, bullPin = 0, bearPin = 0, insideCount = 0;
        int start = Math.Max(1, last - 300);

        for (int i = start; i <= last; i++)
        {
            var c = Bars[i];
            var p = Bars[i - 1];
            double rng = c.H - c.L;
            double pRng = p.H - p.L;
            if (rng <= 1e-9 || atr[i] <= 1e-9) continue;

            double body = Math.Abs(c.C - c.O);
            double pBody = Math.Abs(p.C - p.O);
            double upperWick = c.H - Math.Max(c.O, c.C);
            double lowerWick = Math.Min(c.O, c.C) - c.L;

            if (_showInsideBar && c.H < p.H && c.L > p.L && pRng >= atr[i] * _minAtrRatio)
            {
                insideCount++;
                s.Mark(i, c.H, MarkKind.Diamond, Pal.Amber, "IB", above: true);
                continue;
            }

            if (rng < atr[i] * _minAtrRatio) continue;

            if (_showEngulfing && body > pBody * 1.05 && pBody > pRng * 0.2)
            {
                bool bull = c.C > c.O && p.C < p.O && c.C >= p.O && c.O <= p.C;
                bool bear = c.C < c.O && p.C > p.O && c.C <= p.O && c.O >= p.C;
                if (bull)
                {
                    bullEng++;
                    s.Mark(i, c.L, MarkKind.TriangleUp, Pal.Bull, "ENG", above: false);
                    continue;
                }
                if (bear)
                {
                    bearEng++;
                    s.Mark(i, c.H, MarkKind.TriangleDown, Pal.Bear, "ENG", above: true);
                    continue;
                }
            }

            if (_showPinBar && body <= rng * 0.35)
            {
                if (lowerWick >= rng * 0.6 && upperWick <= rng * 0.18)
                {
                    bullPin++;
                    s.Mark(i, c.L, MarkKind.TriangleUp, Pal.Bull, "PIN", above: false);
                    continue;
                }
                if (upperWick >= rng * 0.6 && lowerWick <= rng * 0.18)
                {
                    bearPin++;
                    s.Mark(i, c.H, MarkKind.TriangleDown, Pal.Bear, "PIN", above: true);
                    continue;
                }
            }

            if (_showDoji && body <= rng * 0.08 && upperWick >= rng * 0.35 && lowerWick >= rng * 0.35)
            {
                s.Mark(i, c.H, MarkKind.Cross, Pal.Muted, "DOJI", above: true);
            }
        }

        s.DashTitle = "Patrones de velas";
        s.Row("Envolvente alcista / bajista", $"{bullEng} / {bearEng}", bullEng >= bearEng ? Pal.Bull : Pal.Bear);
        s.Row("PinBar alcista / bajista", $"{bullPin} / {bearPin}", bullPin >= bearPin ? Pal.Bull : Pal.Bear);
        s.Row("Inside Bar", insideCount.ToString(), Pal.Amber);
    }
}
