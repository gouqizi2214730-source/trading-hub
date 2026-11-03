using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native.VpUltimate;

/// <summary>
/// POC vírgenes («naked POC»): el POC de cada día, semana, mes y año que el precio todavía no volvió a tocar, en cualquier
/// período de gráfico (se arman con el footprint de las velas del gráfico: en un gráfico de 1 minuto salen los diarios,
/// semanales, mensuales y anuales; en uno diario, los semanales en adelante). Los días ya cerrados se guardan en disco.
/// </summary>
internal sealed class NakedPocEngine
{
    public sealed class Level
    {
        public VpPeriod Period;
        public int StartBar, EndBar;
        public DateTime Start;
        public decimal Price;
        public decimal Volume;
        public int TouchBar = -1;
        public bool Touched => TouchBar >= 0;
    }

    private static readonly VpPeriod[] Periods = { VpPeriod.Day, VpPeriod.Week, VpPeriod.Month, VpPeriod.Year };

    private readonly VpIndicator _owner;
    private readonly Dictionary<decimal, decimal>[] _acc = { new(), new(), new(), new() };
    private readonly int[] _start = { -1, -1, -1, -1 };
    private readonly DateTime[] _startTime = new DateTime[4];
    private Dictionary<decimal, decimal> _day = new();
    private int _dayStart = -1;
    private decimal _dayVol, _dayDelta;

    public readonly List<Level> Levels = new();
    public readonly List<Level> Naked = new();

    public NakedPocEngine(VpIndicator owner) => _owner = owner;

    public bool[] Enabled { get; } = { true, true, true, true };

    public void Reset()
    {
        for (int i = 0; i < 4; i++)
        {
            _acc[i].Clear();
            _start[i] = -1;
        }

        _day = new Dictionary<decimal, decimal>();
        _dayStart = -1;
        Levels.Clear();
        Naked.Clear();
    }

    private string Bucket => string.Join("|", "npoc-day1", _owner.InstrumentInfo?.Instrument ?? "", _owner.InstrumentInfo?.TickSize ?? 0);

    /// <summary>Una vela cerrada, en orden.</summary>
    public void Commit(int bar, Func<int, VpPeriod, bool> newPeriod, Func<int, IndicatorCandle> candle, TimeSpan barLength)
    {
        var c = candle(bar);
        bool newDay = _dayStart < 0 || newPeriod(bar, VpPeriod.Day);
        if (newDay && _dayStart >= 0) CloseDay(bar, newPeriod);
        if (newDay)
        {
            _dayStart = bar;
            _dayVol = _dayDelta = 0;
            _day = new Dictionary<decimal, decimal>();
        }

        // el día se arma con los niveles de cada vela (en gráficos diarios o mayores, cada vela ya es un día o más)
        _dayVol += c.Volume;
        _dayDelta += c.Delta;
        if (barLength <= TimeSpan.FromDays(1))
            foreach (var l in _owner.GetTickLevels(bar))
            {
                if (l.Volume <= 0) continue;
                _day.TryGetValue(l.Price, out var v);
                _day[l.Price] = v + l.Volume;
            }

        // tocados al cierre de esta vela
        for (int i = Naked.Count - 1; i >= 0; i--)
        {
            var lv = Naked[i];
            if (bar <= lv.EndBar) continue;
            if (c.Low <= lv.Price && c.High >= lv.Price)
            {
                lv.TouchBar = bar;
                Naked.RemoveAt(i);
            }
        }
    }

    /// <summary>El día que termina en <paramref name="bar"/> − 1: su POC y su aporte a la semana, el mes y el año.</summary>
    private void CloseDay(int bar, Func<int, VpPeriod, bool> newPeriod)
    {
        var dayStartTime = Vp.Utc(_owner.GetCandle(_dayStart).Time);
        // en disco: los niveles del día (clave: cuándo empezó y lo que suma)
        string key = string.Join("|", dayStartTime.Ticks, bar - _dayStart, _dayVol, _dayDelta);
        if (_day.Count == 0 && ResultCache.TryGet(Bucket, key, out var bytes)) _day = ReadDay(bytes);
        else if (_day.Count > 0) ResultCache.Put(Bucket, key, WriteDay(_day));

        for (int i = 0; i < 4; i++)
        {
            bool fresh = _start[i] < 0;
            if (fresh)
            {
                _start[i] = _dayStart;
                _startTime[i] = dayStartTime;
            }

            foreach (var (p, v) in _day)
            {
                _acc[i].TryGetValue(p, out var a);
                _acc[i][p] = a + v;
            }
        }

        // cierra los períodos que terminan con este día (el día siempre; semana / mes / año si la vela siguiente los empieza)
        for (int i = 0; i < 4; i++)
        {
            bool ends = i == 0 || newPeriod(bar, Periods[i]);
            if (!ends) continue;
            if (Enabled[i] && _acc[i].Count > 0)
            {
                decimal poc = 0, max = -1;
                foreach (var (p, v) in _acc[i])
                    if (v > max || (v == max && p < poc))
                    {
                        max = v;
                        poc = p;
                    }

                var lv = new Level { Period = Periods[i], StartBar = _start[i], EndBar = bar - 1, Start = _startTime[i], Price = poc, Volume = max };
                Levels.Add(lv);
                Naked.Add(lv);
            }

            _acc[i].Clear();
            _start[i] = -1;
        }

        if (Levels.Count > 20_000) Levels.RemoveRange(0, Levels.Count - 20_000);
    }

    private static byte[] WriteDay(Dictionary<decimal, decimal> d)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(d.Count);
            foreach (var (p, v) in d)
            {
                w.Write(p);
                w.Write(v);
            }
        }

        return ms.ToArray();
    }

    private static Dictionary<decimal, decimal> ReadDay(byte[] bytes)
    {
        var d = new Dictionary<decimal, decimal>();
        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes));
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) d[r.ReadDecimal()] = r.ReadDecimal();
        }
        catch
        {
            d.Clear();
        }

        return d;
    }

    public static string Code(VpPeriod p) => p switch { VpPeriod.Day => "D", VpPeriod.Week => "W", VpPeriod.Month => "M", _ => "Y" };
}

/// <summary>Indicador: POC vírgenes de día, semana, mes y año en cualquier gráfico.</summary>
[DisplayName("VP definitivo · POC vírgenes (naked POC)")]
[Category(Vp.Category)]
[Description("El POC de cada día, semana, mes y año que el precio todavía no volvió a tocar, en cualquier período de gráfico; se extiende hasta que una vela cerrada lo toca.")]
public class VpNakedPoc : VpIndicator
{
    private readonly NakedPocEngine _engine;

    public VpNakedPoc()
    {
        _engine = new NakedPocEngine(this);
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    [Display(Name = "Diarios", GroupName = "Períodos", Order = 10)] public bool Daily { get => _engine.Enabled[0]; set { _engine.Enabled[0] = value; RecalculateValues(); } }
    [Display(Name = "Color diario", GroupName = "Períodos", Order = 11)] public Color DailyColor { get; set; } = Color.FromArgb(255, 255, 235, 59);
    [Display(Name = "Semanales", GroupName = "Períodos", Order = 20)] public bool Weekly { get => _engine.Enabled[1]; set { _engine.Enabled[1] = value; RecalculateValues(); } }
    [Display(Name = "Color semanal", GroupName = "Períodos", Order = 21)] public Color WeeklyColor { get; set; } = Color.FromArgb(255, 255, 152, 0);
    [Display(Name = "Mensuales", GroupName = "Períodos", Order = 30)] public bool Monthly { get => _engine.Enabled[2]; set { _engine.Enabled[2] = value; RecalculateValues(); } }
    [Display(Name = "Color mensual", GroupName = "Períodos", Order = 31)] public Color MonthlyColor { get; set; } = Color.FromArgb(255, 0, 230, 118);
    [Display(Name = "Anuales", GroupName = "Períodos", Order = 40)] public bool Yearly { get => _engine.Enabled[3]; set { _engine.Enabled[3] = value; RecalculateValues(); } }
    [Display(Name = "Color anual", GroupName = "Períodos", Order = 41)] public Color YearlyColor { get; set; } = Color.FromArgb(255, 244, 67, 54);

    [Display(Name = "Mostrar también los ya tocados (hasta donde los tocaron)", GroupName = "Visualización", Order = 50)]
    public bool ShowTouched { get; set; }

    [Display(Name = "Grosor", GroupName = "Visualización", Order = 60)]
    [Range(1, 6)]
    public int Width { get; set; } = 1;

    [Display(Name = "Estilo", GroupName = "Visualización", Order = 70)]
    public LineDashStyle Style { get; set; } = LineDashStyle.Solid;

    [Display(Name = "Etiqueta (período y fecha)", GroupName = "Visualización", Order = 80)]
    public bool ShowLabel { get; set; } = true;

    [Display(Name = "Máximo de líneas por período", GroupName = "Visualización", Order = 90)]
    [Range(1, 500)]
    public int MaxPerPeriod { get; set; } = 50;

    protected override void Reset() => _engine.Reset();

    protected override void Commit(int bar) => _engine.Commit(bar, NewPeriod, GetCandle, BarLength);

    internal static void Render(VpIndicator owner, RenderContext ctx, NakedPocEngine engine, Color[] colors, bool showTouched, int width, LineDashStyle style, bool label, int maxPerPeriod)
    {
        if (owner.ChartInfo == null) return;
        var info = owner.ChartInfo;
        int last = Math.Max(0, owner.CurrentBar - 1);
        int xRight = info.PriceChartContainer.Region.Right;
        var shown = new int[4];
        var font = new RenderFont("Arial", 8);
        for (int i = engine.Levels.Count - 1; i >= 0; i--)
        {
            var lv = engine.Levels[i];
            int k = Array.IndexOf(new[] { VpPeriod.Day, VpPeriod.Week, VpPeriod.Month, VpPeriod.Year }, lv.Period);
            if (!engine.Enabled[k] || (lv.Touched && !showTouched)) continue;
            if (shown[k] >= maxPerPeriod) continue;
            shown[k]++;
            int x0 = info.GetXByBar(Math.Min(lv.EndBar + 1, last), true);
            int x1 = lv.Touched ? info.GetXByBar(lv.TouchBar, false) : xRight;
            if (x1 <= x0) continue;
            int y = info.GetYByPrice(lv.Price, false);
            var color = lv.Touched ? Vp.Alpha(colors[k], 110) : colors[k];
            ctx.DrawLine(Vp.Pen(color, width, style), x0, y, x1, y);
            if (!label) continue;
            string text = NakedPocEngine.Code(lv.Period) + " NPOC " + lv.Start.ToString(lv.Period == VpPeriod.Day ? "dd/MM" : lv.Period == VpPeriod.Week ? "dd/MM" : lv.Period == VpPeriod.Month ? "MM/yyyy" : "yyyy");
            var size = ctx.MeasureString(text, font);
            ctx.DrawString(text, font, color, Math.Min(x0 + 4, xRight - size.Width - 4), y - size.Height - 1);
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout) =>
        Render(this, context, _engine, new[] { DailyColor, WeeklyColor, MonthlyColor, YearlyColor }, ShowTouched, Width, Style, ShowLabel, MaxPerPeriod);
}
