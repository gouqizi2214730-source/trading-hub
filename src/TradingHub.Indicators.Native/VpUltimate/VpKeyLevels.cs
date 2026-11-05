using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators.Native.VpUltimate;

/// <summary>
/// Módulo 8 del «VP definitivo»: niveles clave. Aperturas del período actual (4 h, día, semana, mes, trimestre, año),
/// máximo / mínimo / punto medio del período anterior, rango del lunes y máximos / mínimos / aperturas de las sesiones de
/// Asia, Londres y Nueva York. Solo se usan períodos terminados (no repinta). Los máximos y mínimos se borran cuando una
/// vela cerrada los toca (o los salta con un hueco): quedan solo los vírgenes.
/// </summary>
[DisplayName("VP definitivo · Niveles clave")]
[Category(Vp.Category)]
[Description("Aperturas del período, máximo / mínimo / punto medio del anterior (4 h a año), rango del lunes y niveles de las sesiones. Los máximos y mínimos tocados se borran: quedan los vírgenes.")]
public class VpKeyLevels : VpSessionBase
{
    public enum Placement
    {
        [Display(Name = "Desde donde nace el nivel")] Standard,
        [Display(Name = "Anclado a la derecha")] Right,
    }

    /// <summary>Grupo de color / grosor de un nivel.</summary>
    private enum Group { H4, Day, Monday, Week, Month, Quarter, Year, Asia, London, NewYork }

    private sealed class Level
    {
        public string Id = "";
        public string Name = "";
        public decimal Price;
        public int StartBar;
        public bool HighLow;
        public bool Consumed;
        public Group Group;
    }

    private sealed class PeriodTrack
    {
        public int StartBar = -1;
        public decimal Open, High, Low;
        public int PrevStart = -1;
        public decimal PrevHigh, PrevLow;
        public bool HasPrev;
    }

    private sealed class SessionTrack
    {
        public bool Active;
        public int CurStart;
        public decimal CurOpen, CurHigh, CurLow;
        public bool HasDone;
        public int DoneStart;
        public decimal DoneOpen, DoneHigh, DoneLow;
    }

    private static readonly (VpPeriod Period, Group Group, string Code, string Open, string High, string Low, string Mid)[] PeriodDefs =
    {
        (VpPeriod.Hour4, Group.H4, "4H", "Apertura 4 h", "Máximo 4 h anterior", "Mínimo 4 h anterior", "Punto medio 4 h anterior"),
        (VpPeriod.Day, Group.Day, "D", "Apertura del día", "Máximo de ayer", "Mínimo de ayer", "Punto medio de ayer"),
        (VpPeriod.Week, Group.Week, "W", "Apertura de la semana", "Máximo de la semana anterior", "Mínimo de la semana anterior", "Punto medio de la semana anterior"),
        (VpPeriod.Month, Group.Month, "M", "Apertura del mes", "Máximo del mes anterior", "Mínimo del mes anterior", "Punto medio del mes anterior"),
        (VpPeriod.Quarter, Group.Quarter, "Q", "Apertura del trimestre", "Máximo del trimestre anterior", "Mínimo del trimestre anterior", "Punto medio del trimestre anterior"),
        (VpPeriod.Year, Group.Year, "Y", "Apertura del año", "Máximo del año anterior", "Mínimo del año anterior", "Punto medio del año anterior"),
    };

    private static readonly string[,] SessionLevelNames =
    {
        { "Máximo de Asia", "Mínimo de Asia", "Apertura de Asia" },
        { "Máximo de Londres", "Mínimo de Londres", "Apertura de Londres" },
        { "Máximo de Nueva York", "Mínimo de Nueva York", "Apertura de Nueva York" },
    };

    private readonly PeriodTrack[] _periods = PeriodDefs.Select(_ => new PeriodTrack()).ToArray();
    private readonly SessionTrack[] _sessions = { new(), new(), new() };
    private readonly List<Level> _current = new();
    private readonly List<Level> _history = new();
    private readonly Dictionary<string, decimal> _dead = new();
    private int _mondayDayStart = -1;
    private decimal _mondayHigh, _mondayLow;
    private int _mondayStart = -1;
    private bool _mondayReady;
    private decimal _dayHigh, _dayLow;
    private int _dayStart = -1;
    private bool _dayIsMonday;

    // qué niveles (cambiarlos recalcula)
    private readonly bool[] _open = { false, true, true, true, true, true };
    private readonly bool[] _range = { false, false, true, true, false, false };
    private readonly bool[] _mid = { false, false, true, true, true, true };
    private bool _monday = true, _mondayMid = true, _asia, _london, _newYork, _touchDelete = true;
    private int _keepHistory = 3;

    public VpKeyLevels()
    {
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    // ------------------------------------------------------------------ visualización

    [Display(Name = "Posición", GroupName = "Visualización", Order = 10)]
    public Placement Style { get; set; } = Placement.Standard;

    [Display(Name = "Extender (velas a la derecha)", GroupName = "Visualización", Order = 20)]
    [Range(5, 500)]
    public int ExtendBars { get; set; } = 30;

    [Display(Name = "Anclaje a la derecha (velas)", GroupName = "Visualización", Order = 30)]
    [Range(5, 500)]
    public int AnchorBars { get; set; } = 250;

    [Display(Name = "Estilo de línea", GroupName = "Visualización", Order = 40)]
    public LineDashStyle LineStyle { get; set; } = LineDashStyle.Solid;

    [Display(Name = "Mostrar el nombre del nivel", GroupName = "Visualización", Order = 50)]
    public bool ShowNames { get; set; } = true;

    [Display(Name = "Un solo color para todo", GroupName = "Visualización", Order = 60)]
    public bool GlobalColoring { get; set; }

    [Display(Name = "Color único", GroupName = "Visualización", Order = 70)]
    public Color GlobalColor { get; set; } = Color.White;

    [Display(Name = "Borrar máximos / mínimos al ser tocados", GroupName = "Visualización", Order = 80)]
    public bool TouchDelete { get => _touchDelete; set { _touchDelete = value; RecalculateValues(); } }

    [Display(Name = "Conservar los vírgenes anteriores (por tipo)", GroupName = "Visualización", Order = 90)]
    [Range(0, 10)]
    public int KeepHistory { get => _keepHistory; set { _keepHistory = Math.Clamp(value, 0, 10); RecalculateValues(); } }

    // ------------------------------------------------------------------ períodos

    [Display(Name = "4 h: apertura", GroupName = "4 horas", Order = 100)] public bool H4Open { get => _open[0]; set { _open[0] = value; RecalculateValues(); } }
    [Display(Name = "4 h: máximo / mínimo anterior", GroupName = "4 horas", Order = 101)] public bool H4Range { get => _range[0]; set { _range[0] = value; RecalculateValues(); } }
    [Display(Name = "4 h: punto medio anterior", GroupName = "4 horas", Order = 102)] public bool H4Mid { get => _mid[0]; set { _mid[0] = value; RecalculateValues(); } }
    [Display(Name = "4 h: color", GroupName = "4 horas", Order = 103)] public Color H4Color { get; set; } = Color.FromArgb(255, 255, 152, 0);
    [Display(Name = "4 h: grosor", GroupName = "4 horas", Order = 104)][Range(1, 5)] public int H4Width { get; set; } = 1;

    [Display(Name = "Día: apertura", GroupName = "Diario", Order = 110)] public bool DayOpen { get => _open[1]; set { _open[1] = value; RecalculateValues(); } }
    [Display(Name = "Día: máximo / mínimo de ayer", GroupName = "Diario", Order = 111)] public bool DayRange { get => _range[1]; set { _range[1] = value; RecalculateValues(); } }
    [Display(Name = "Día: punto medio de ayer", GroupName = "Diario", Order = 112)] public bool DayMid { get => _mid[1]; set { _mid[1] = value; RecalculateValues(); } }
    [Display(Name = "Día: color", GroupName = "Diario", Order = 113)] public Color DayColor { get; set; } = Color.FromArgb(255, 8, 188, 212);
    [Display(Name = "Día: grosor", GroupName = "Diario", Order = 114)][Range(1, 5)] public int DayWidth { get; set; } = 1;

    [Display(Name = "Lunes: máximo / mínimo", GroupName = "Rango del lunes", Order = 120)] public bool Monday { get => _monday; set { _monday = value; RecalculateValues(); } }
    [Display(Name = "Lunes: punto medio", GroupName = "Rango del lunes", Order = 121)] public bool MondayMid { get => _mondayMid; set { _mondayMid = value; RecalculateValues(); } }
    [Display(Name = "Lunes: color", GroupName = "Rango del lunes", Order = 122)] public Color MondayColor { get; set; } = Color.White;
    [Display(Name = "Lunes: grosor", GroupName = "Rango del lunes", Order = 123)][Range(1, 5)] public int MondayWidth { get; set; } = 1;

    [Display(Name = "Semana: apertura", GroupName = "Semanal", Order = 130)] public bool WeekOpen { get => _open[2]; set { _open[2] = value; RecalculateValues(); } }
    [Display(Name = "Semana: máximo / mínimo anterior", GroupName = "Semanal", Order = 131)] public bool WeekRange { get => _range[2]; set { _range[2] = value; RecalculateValues(); } }
    [Display(Name = "Semana: punto medio anterior", GroupName = "Semanal", Order = 132)] public bool WeekMid { get => _mid[2]; set { _mid[2] = value; RecalculateValues(); } }
    [Display(Name = "Semana: color", GroupName = "Semanal", Order = 133)] public Color WeekColor { get; set; } = Color.FromArgb(255, 255, 252, 188);
    [Display(Name = "Semana: grosor", GroupName = "Semanal", Order = 134)][Range(1, 5)] public int WeekWidth { get; set; } = 1;

    [Display(Name = "Mes: apertura", GroupName = "Mensual", Order = 140)] public bool MonthOpen { get => _open[3]; set { _open[3] = value; RecalculateValues(); } }
    [Display(Name = "Mes: máximo / mínimo anterior", GroupName = "Mensual", Order = 141)] public bool MonthRange { get => _range[3]; set { _range[3] = value; RecalculateValues(); } }
    [Display(Name = "Mes: punto medio anterior", GroupName = "Mensual", Order = 142)] public bool MonthMid { get => _mid[3]; set { _mid[3] = value; RecalculateValues(); } }
    [Display(Name = "Mes: color", GroupName = "Mensual", Order = 143)] public Color MonthColor { get; set; } = Color.FromArgb(255, 8, 212, 140);
    [Display(Name = "Mes: grosor", GroupName = "Mensual", Order = 144)][Range(1, 5)] public int MonthWidth { get; set; } = 1;

    [Display(Name = "Trimestre: apertura", GroupName = "Trimestral", Order = 150)] public bool QuarterOpen { get => _open[4]; set { _open[4] = value; RecalculateValues(); } }
    [Display(Name = "Trimestre: máximo / mínimo anterior", GroupName = "Trimestral", Order = 151)] public bool QuarterRange { get => _range[4]; set { _range[4] = value; RecalculateValues(); } }
    [Display(Name = "Trimestre: punto medio anterior", GroupName = "Trimestral", Order = 152)] public bool QuarterMid { get => _mid[4]; set { _mid[4] = value; RecalculateValues(); } }
    [Display(Name = "Trimestre: color", GroupName = "Trimestral", Order = 153)] public Color QuarterColor { get; set; } = Color.FromArgb(255, 242, 54, 69);
    [Display(Name = "Trimestre: grosor", GroupName = "Trimestral", Order = 154)][Range(1, 5)] public int QuarterWidth { get; set; } = 1;

    [Display(Name = "Año: apertura", GroupName = "Anual", Order = 160)] public bool YearOpen { get => _open[5]; set { _open[5] = value; RecalculateValues(); } }
    [Display(Name = "Año: máximo / mínimo anterior", GroupName = "Anual", Order = 161)] public bool YearRange { get => _range[5]; set { _range[5] = value; RecalculateValues(); } }
    [Display(Name = "Año: punto medio anterior", GroupName = "Anual", Order = 162)] public bool YearMid { get => _mid[5]; set { _mid[5] = value; RecalculateValues(); } }
    [Display(Name = "Año: color", GroupName = "Anual", Order = 163)] public Color YearColor { get; set; } = Color.FromArgb(255, 242, 54, 69);
    [Display(Name = "Año: grosor", GroupName = "Anual", Order = 164)][Range(1, 5)] public int YearWidth { get; set; } = 1;

    [Display(Name = "Asia: máximo / mínimo / apertura", GroupName = "Sesiones", Order = 170)] public bool AsiaRange { get => _asia; set { _asia = value; RecalculateValues(); } }
    [Display(Name = "Asia: color", GroupName = "Sesiones", Order = 171)] public Color AsiaColor { get; set; } = Color.White;
    [Display(Name = "Londres: máximo / mínimo / apertura", GroupName = "Sesiones", Order = 172)] public bool LondonRange { get => _london; set { _london = value; RecalculateValues(); } }
    [Display(Name = "Londres: color", GroupName = "Sesiones", Order = 173)] public Color LondonColor { get; set; } = Color.White;
    [Display(Name = "Nueva York: máximo / mínimo / apertura", GroupName = "Sesiones", Order = 174)] public bool NewYorkRange { get => _newYork; set { _newYork = value; RecalculateValues(); } }
    [Display(Name = "Nueva York: color", GroupName = "Sesiones", Order = 175)] public Color NewYorkColor { get; set; } = Color.White;
    [Display(Name = "Sesiones: grosor", GroupName = "Sesiones", Order = 176)][Range(1, 5)] public int SessionWidth { get; set; } = 1;

    // ------------------------------------------------------------------ cálculo

    protected override void Reset()
    {
        for (int i = 0; i < _periods.Length; i++) _periods[i] = new PeriodTrack();
        for (int i = 0; i < 3; i++) _sessions[i] = new SessionTrack();
        _current.Clear();
        _history.Clear();
        _dead.Clear();
        _mondayStart = _mondayDayStart = _dayStart = -1;
        _mondayReady = _dayIsMonday = false;
    }

    /// <summary>El gráfico tiene velas de este período o menores (si no, el nivel no tiene sentido).</summary>
    private bool Fits(VpPeriod p) => BarLength > TimeSpan.Zero && BarLength <= Length(p);

    private void Put(string id, string name, int startBar, decimal price, Group group, bool highLow)
    {
        if (_touchDelete && highLow && _dead.TryGetValue(id, out var deadPrice) && deadPrice == price) return;
        _dead.Remove(id);
        int idx = _current.FindIndex(l => l.Id == id);
        if (idx < 0)
        {
            _current.Add(new Level { Id = id, Name = name, Price = price, StartBar = startBar, HighLow = highLow, Group = group });
            return;
        }

        var k = _current[idx];
        if (k.Price != price && highLow && _touchDelete && _keepHistory > 0 && !k.Consumed)
        {
            // cambió el período: el anterior todavía virgen pasa a la historia
            _history.Add(k);
            _current.RemoveAt(idx);
            TrimHistory(id);
            _current.Add(new Level { Id = id, Name = name, Price = price, StartBar = startBar, HighLow = highLow, Group = group });
            return;
        }

        if (k.Price != price) k.Consumed = false;
        k.Price = price;
        k.StartBar = startBar;
    }

    private void Remove(string id)
    {
        int idx = _current.FindIndex(l => l.Id == id);
        if (idx < 0) return;
        var k = _current[idx];
        _current.RemoveAt(idx);
        if (k.HighLow && _touchDelete && _keepHistory > 0 && !k.Consumed)
        {
            _history.Add(k);
            TrimHistory(id);
        }
    }

    private void TrimHistory(string id)
    {
        while (_history.Count(l => l.Id == id) > _keepHistory) _history.RemoveAt(_history.FindIndex(l => l.Id == id));
    }

    protected override void Commit(int bar)
    {
        var c = GetCandle(bar);

        // períodos: al empezar uno nuevo, el anterior queda terminado
        for (int i = 0; i < PeriodDefs.Length; i++)
        {
            var t = _periods[i];
            if (t.StartBar < 0 || NewPeriod(bar, PeriodDefs[i].Period))
            {
                if (t.StartBar >= 0)
                {
                    t.PrevStart = t.StartBar;
                    t.PrevHigh = t.High;
                    t.PrevLow = t.Low;
                    t.HasPrev = true;
                }

                t.StartBar = bar;
                t.Open = c.Open;
                t.High = c.High;
                t.Low = c.Low;
            }
            else
            {
                t.High = Math.Max(t.High, c.High);
                t.Low = Math.Min(t.Low, c.Low);
            }
        }

        // rango del lunes: cuando termina el lunes (el día empieza un lunes, UTC); se borra al empezar la semana
        bool newDay = _dayStart < 0 || NewPeriod(bar, VpPeriod.Day);
        if (NewPeriod(bar, VpPeriod.Week) && bar > 0) _mondayReady = false;
        if (newDay)
        {
            if (_dayStart >= 0 && _dayIsMonday)
            {
                _mondayHigh = _dayHigh;
                _mondayLow = _dayLow;
                _mondayStart = _dayStart;
                _mondayReady = !NewPeriod(bar, VpPeriod.Week);
            }

            _dayStart = bar;
            _dayHigh = c.High;
            _dayLow = c.Low;
            _dayIsMonday = Vp.Utc(c.Time).DayOfWeek == DayOfWeek.Monday;
        }
        else
        {
            _dayHigh = Math.Max(_dayHigh, c.High);
            _dayLow = Math.Min(_dayLow, c.Low);
        }

        // sesiones (hora de Nueva York): al terminar una, sus niveles
        var sessions = Sessions;
        var ny = Vp.Ny(c.Time);
        bool dateChanged = bar > 0 && Vp.Ny(GetCandle(bar - 1).Time).Date != ny.Date;
        for (int s = 0; s < 3; s++)
        {
            var t = _sessions[s];
            bool inside = Vp.InSession(Vp.Hm(ny), sessions[s]);
            bool split = dateChanged && sessions[s].Start < sessions[s].End;
            if (t.Active && (!inside || split))
            {
                t.Active = false;
                t.HasDone = true;
                t.DoneStart = t.CurStart;
                t.DoneOpen = t.CurOpen;
                t.DoneHigh = t.CurHigh;
                t.DoneLow = t.CurLow;
            }

            if (!inside) continue;
            if (!t.Active)
            {
                t.Active = true;
                t.CurStart = bar;
                t.CurOpen = c.Open;
                t.CurHigh = c.High;
                t.CurLow = c.Low;
            }
            else
            {
                t.CurHigh = Math.Max(t.CurHigh, c.High);
                t.CurLow = Math.Min(t.CurLow, c.Low);
            }
        }

        // niveles vigentes
        for (int i = 0; i < PeriodDefs.Length; i++)
        {
            var d = PeriodDefs[i];
            var t = _periods[i];
            if (!Fits(d.Period)) continue;
            if (_open[i]) Put(d.Code + "_O", d.Open, t.StartBar, t.Open, d.Group, false);
            if (!t.HasPrev) continue;
            if (_range[i])
            {
                Put(d.Code + "_H", d.High, t.PrevStart, t.PrevHigh, d.Group, true);
                Put(d.Code + "_L", d.Low, t.PrevStart, t.PrevLow, d.Group, true);
            }

            if (_mid[i]) Put(d.Code + "_M", d.Mid, t.PrevStart, (t.PrevHigh + t.PrevLow) / 2, d.Group, false);
        }

        bool mondayOk = _mondayReady && Fits(VpPeriod.Day);
        if (_monday && mondayOk)
        {
            Put("MON_H", "Máximo del lunes", _mondayStart, _mondayHigh, Group.Monday, true);
            Put("MON_L", "Mínimo del lunes", _mondayStart, _mondayLow, Group.Monday, true);
        }
        else
        {
            Remove("MON_H");
            Remove("MON_L");
        }

        if (_mondayMid && mondayOk) Put("MON_M", "Punto medio del lunes", _mondayStart, (_mondayHigh + _mondayLow) / 2, Group.Monday, false);
        else Remove("MON_M");

        var sessionOn = new[] { _asia, _london, _newYork };
        var sessionGroup = new[] { Group.Asia, Group.London, Group.NewYork };
        for (int s = 0; s < 3; s++)
        {
            var t = _sessions[s];
            if (!sessionOn[s] || !Intraday || !t.HasDone) continue;
            string code = "S" + s;
            Put(code + "_H", SessionLevelNames[s, 0], t.DoneStart, t.DoneHigh, sessionGroup[s], true);
            Put(code + "_L", SessionLevelNames[s, 1], t.DoneStart, t.DoneLow, sessionGroup[s], true);
            Put(code + "_O", SessionLevelNames[s, 2], t.DoneStart, t.DoneOpen, sessionGroup[s], false);
        }

        // tocados al cierre de esta vela: se borran
        if (!_touchDelete) return;
        for (int i = _current.Count - 1; i >= 0; i--)
        {
            var k = _current[i];
            if (!k.HighLow || !Touched(bar, k.Price)) continue;
            _dead[k.Id] = k.Price;
            _current.RemoveAt(i);
        }

        for (int i = _history.Count - 1; i >= 0; i--)
            if (Touched(bar, _history[i].Price))
                _history.RemoveAt(i);
    }

    // ------------------------------------------------------------------ dibujo

    private (Color Color, int Width) Look(Group g)
    {
        var (color, width) = g switch
        {
            Group.H4 => (H4Color, H4Width),
            Group.Day => (DayColor, DayWidth),
            Group.Monday => (MondayColor, MondayWidth),
            Group.Week => (WeekColor, WeekWidth),
            Group.Month => (MonthColor, MonthWidth),
            Group.Quarter => (QuarterColor, QuarterWidth),
            Group.Year => (YearColor, YearWidth),
            Group.Asia => (AsiaColor, SessionWidth),
            Group.London => (LondonColor, SessionWidth),
            _ => (NewYorkColor, SessionWidth),
        };
        return (GlobalColoring ? GlobalColor : color, width);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        int lastBar = Math.Max(0, CurrentBar - 1);
        int xRight = X(lastBar + ExtendBars, false);
        foreach (var k in _history.Concat(_current))
        {
            var (color, width) = Look(k.Group);
            int x1 = Style == Placement.Right ? X(lastBar + AnchorBars, false) : X(k.StartBar);
            int y = Y(k.Price);
            context.DrawLine(Vp.Pen(color, width, LineStyle), Math.Min(x1, xRight), y, Math.Max(x1, xRight), y);
            if (!ShowNames) continue;
            string text = UiText.Tr(k.Name);
            // a la derecha de la línea; si no entra, pegado al borde del gráfico
            int tw = context.MeasureString(text, SmallFont).Width;
            int tx = Math.Min(Math.Max(x1, xRight) + 3, ChartInfo.PriceChartContainer.Region.Right - tw - 4);
            Text(context, text, color, tx, y - 1);
        }
    }

    /// <summary>Los niveles clave dibujados, para las alertas.</summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        foreach (var k in _history.Concat(_current))
            yield return new IndicatorLevel(k.Id + "@" + (k.StartBar >= 0 && k.StartBar < CurrentBar ? GetCandle(k.StartBar).Time.Ticks : 0), UiText.Tr(k.Name), k.Price, Style == Placement.Right ? 0 : k.StartBar);
    }
}
