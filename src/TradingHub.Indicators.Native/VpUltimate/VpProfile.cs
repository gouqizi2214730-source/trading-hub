using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators.Native.VpUltimate;

/// <summary>Período de cada perfil (los mismos nombres que <see cref="VpPeriod"/>, más «toda la historia»).</summary>
public enum VpProfilePeriod
{
    [Display(Name = "1 hora")] Hour1,
    [Display(Name = "4 horas")] Hour4,
    [Display(Name = "Sesión (día)")] Day,
    [Display(Name = "Semanal")] Week,
    [Display(Name = "Mensual")] Month,
    [Display(Name = "Trimestral")] Quarter,
    [Display(Name = "Anual")] Year,
    [Display(Name = "Toda la historia (un solo perfil)")] All,
}

/// <summary>
/// Perfil de volumen único de KVP: junta el «perfil de volumen por sesión» y el módulo 1 del «VP definitivo». Un perfil por
/// período (hora, sesión, semana…) con POC normal o comprador / vendedor, área de valor, área exterior y razón de colas,
/// filas por cantidad o por ticks, vista de volumen, delta o bid / ask, POC vírgenes de otros períodos y — como en
/// Exocharts — perfiles unidos: clic derecho en el primero («inicio»), clic derecho en el último («unir hasta acá») y
/// quedan en uno solo; opcionalmente se unen solos los vecinos que se superponen más de un porcentaje.
/// Los períodos cerrados se guardan en disco: al volver a abrir no se recalculan.
/// </summary>
[DisplayName("Perfil de volumen (sesión / período · VP definitivo)")]
[Category(IndicatorCategories.VolumeProfile)]
[Description("Perfil de volumen por sesión, semana, mes… con POC, VAH / VAL, área exterior, colas, POC vírgenes y perfiles unidos (clic derecho en el primero y en el último, o automático si se superponen). Solo períodos terminados: no repinta.")]
public class VpProfile : VpIndicator
{
    public enum PocKind
    {
        [Display(Name = "POC normal")] Regular,
        [Display(Name = "POC comprador / vendedor")] BullBear,
    }

    public enum RowSizing
    {
        [Display(Name = "Cantidad de filas")] Rows,
        [Display(Name = "Ticks por fila")] Ticks,
    }

    public enum ProfileStyle
    {
        [Display(Name = "Volumen")] Volume,
        [Display(Name = "Delta")] Delta,
        [Display(Name = "Bid / Ask")] BidAsk,
    }

    public enum OverlapBasis
    {
        [Display(Name = "Área de valor (VAL – VAH)")] ValueArea,
        [Display(Name = "Rango completo (mínimo – máximo)")] Range,
    }

    private const int MaxRows = 4000;

    private sealed class Result
    {
        public int StartBar, EndBar;
        public decimal YMin, YMax, Step;
        public double[] Values = Array.Empty<double>();
        public double[] Buy = Array.Empty<double>();
        public decimal Poc, PocUp, PocDn, Vah, Val, OuterHi, OuterLo;
        public double Tail;

        // POC que se extienden a la derecha: hasta qué vela llegan y si fueron tocados
        public int PocEnd, PocUpEnd, PocDnEnd;
        public bool PocDead, PocUpDead, PocDnDead;

        // unido a mano (inicio / fin) o solo (superposición), y cuántos períodos tiene
        public bool Manual, Auto;
        public int Parts = 1;
    }

    /// <summary>Lo acumulado del período en curso (se suma vela a vela: no se recorre todo en cada vela nueva).</summary>
    private sealed class Acc
    {
        public int Start = -1, End = -1;
        public readonly Dictionary<decimal, (double All, double Buy)> Levels = new();
        public decimal Lo = decimal.MaxValue, Hi = decimal.MinValue;

        public void Clear(int start)
        {
            Start = start;
            End = start - 1;
            Levels.Clear();
            Lo = decimal.MaxValue;
            Hi = decimal.MinValue;
        }
    }

    private readonly NakedPocEngine _naked;
    private readonly List<Result> _done = new();
    private readonly List<Result> _live = new(); // con algún POC todavía sin tocar
    private readonly Acc _acc = new();
    private Result? _developing;
    private int _periodStart;
    private int _periodParts = 1;
    private List<(long From, long To)> _ranges = new();
    private (long From, long To)? _head;

    private VpProfilePeriod _period = VpProfilePeriod.Day;
    private RowSizing _rowSizing = RowSizing.Rows;
    private int _rows = 200;
    private int _ticksPerRow = 1;
    private VpVolumeMode _volumeMode = VpVolumeMode.Raw;
    private decimal _capMult = 10;
    private bool _excludeAuction;
    private int _vaPercent = 70;
    private int _outerPercent = 90;
    private bool _showDeveloping;
    private bool _deletePoc;
    private bool _autoMerge;
    private int _autoMergePercent = 50;
    private OverlapBasis _overlapBasis = OverlapBasis.ValueArea;

    public VpProfile()
    {
        _naked = new NakedPocEngine(this);
        for (int i = 0; i < 4; i++) _naked.Enabled[i] = false;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    // ------------------------------------------------------------------ cálculo

    [Display(Name = "Período del perfil", GroupName = "Perfil", Order = 10)]
    public VpProfilePeriod Period { get => _period; set { _period = value; RecalculateValues(); } }

    [Display(Name = "Tamaño de las filas", GroupName = "Perfil", Order = 15)]
    public RowSizing RowMode { get => _rowSizing; set { _rowSizing = value; RecalculateValues(); } }

    [Display(Name = "Filas del perfil", GroupName = "Perfil", Order = 20)]
    [Range(10, 1000)]
    public int Rows { get => _rows; set { _rows = Math.Clamp(value, 10, 1000); RecalculateValues(); } }

    [Display(Name = "Ticks por fila", GroupName = "Perfil", Order = 25)]
    [Range(1, 10000)]
    public int TicksPerRow { get => _ticksPerRow; set { _ticksPerRow = Math.Clamp(value, 1, 10000); RecalculateValues(); } }

    [Display(Name = "Volumen", GroupName = "Perfil", Order = 30)]
    public VpVolumeMode VolumeMode { get => _volumeMode; set { _volumeMode = value; RecalculateValues(); } }

    [Display(Name = "Recorte (× mediana del período)", GroupName = "Perfil", Order = 40)]
    [Range(2, 100)]
    public decimal CapMultiple { get => _capMult; set { _capMult = Math.Clamp(value, 2, 100); RecalculateValues(); } }

    [Display(Name = "Excluir velas de subasta de apertura / cierre (acciones de EE. UU.)", GroupName = "Perfil", Order = 50)]
    public bool ExcludeAuction { get => _excludeAuction; set { _excludeAuction = value; RecalculateValues(); } }

    [Display(Name = "Mostrar el período en curso", GroupName = "Perfil", Order = 60)]
    public bool ShowDeveloping { get => _showDeveloping; set { _showDeveloping = value; RecalculateValues(); } }

    [Display(Name = "Vista", GroupName = "Perfil", Order = 65)]
    public ProfileStyle Style { get; set; } = ProfileStyle.Volume;

    [Display(Name = "Ancho del perfil (% del período)", GroupName = "Perfil", Order = 70)]
    [Range(1, 100)]
    public int WidthPercent { get; set; } = 100;

    [Display(Name = "Color del perfil", GroupName = "Perfil", Order = 80)]
    public Color ProfileColor { get; set; } = Color.FromArgb(77, 33, 150, 243);

    [Display(Name = "Pintar las filas del área de valor", GroupName = "Perfil", Order = 82)]
    public bool ColorValueAreaRows { get; set; }

    [Display(Name = "Color de las filas del área de valor", GroupName = "Perfil", Order = 84)]
    public Color ValueAreaRowColor { get; set; } = Color.FromArgb(110, 120, 160, 220);

    [Display(Name = "Pintar la fila del POC", GroupName = "Perfil", Order = 86)]
    public bool ColorPocRow { get; set; }

    [Display(Name = "Color comprador (delta / bid-ask)", GroupName = "Perfil", Order = 88)]
    public Color BuyColor { get; set; } = Color.FromArgb(120, 73, 209, 73);

    [Display(Name = "Color vendedor (delta / bid-ask)", GroupName = "Perfil", Order = 89)]
    public Color SellColor { get; set; } = Color.FromArgb(120, 232, 69, 72);

    // ------------------------------------------------------------------ unir perfiles

    [Display(Name = "Unir solos los perfiles vecinos que se superponen", GroupName = "Unir perfiles", Order = 90)]
    public bool AutoMerge { get => _autoMerge; set { _autoMerge = value; RecalculateValues(); } }

    [Display(Name = "Superposición mínima para unir (%)", GroupName = "Unir perfiles", Order = 92)]
    [Range(1, 100)]
    public int AutoMergePercent { get => _autoMergePercent; set { _autoMergePercent = Math.Clamp(value, 1, 100); RecalculateValues(); } }

    [Display(Name = "Superposición medida sobre", GroupName = "Unir perfiles", Order = 94)]
    public OverlapBasis MergeBasis { get => _overlapBasis; set { _overlapBasis = value; RecalculateValues(); } }

    [Display(Name = "Marcar los perfiles unidos", GroupName = "Unir perfiles", Order = 96)]
    public bool LabelMerged { get; set; } = true;

    /// <summary>Uniones hechas con el clic derecho: «desde-hasta;…» en ticks UTC del inicio de cada período.</summary>
    [Browsable(false)]
    [KeepSetting]
    public string MergedRanges
    {
        get => string.Join(";", _ranges.Select(r => r.From.ToString(CultureInfo.InvariantCulture) + "-" + r.To.ToString(CultureInfo.InvariantCulture)));
        set
        {
            var list = new List<(long, long)>();
            foreach (var part in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var ab = part.Split('-');
                if (ab.Length == 2 && long.TryParse(ab[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
                    && long.TryParse(ab[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b >= a)
                    list.Add((a, b));
            }

            _ranges = Normalize(list);
            RecalculateValues();
        }
    }

    // ------------------------------------------------------------------ POC

    [Display(Name = "Mostrar POC", GroupName = "POC", Order = 100)]
    public bool ShowPoc { get; set; } = true;

    [Display(Name = "Tipo de POC", GroupName = "POC", Order = 110)]
    public PocKind PocType { get; set; } = PocKind.Regular;

    [Display(Name = "Borrar el POC al ser tocado (solo los vírgenes)", GroupName = "POC", Order = 120)]
    public bool DeletePocOnTouch { get => _deletePoc; set { _deletePoc = value; RecalculateValues(); } }

    [Display(Name = "Extender el POC hasta que lo toquen", GroupName = "POC", Order = 125)]
    public bool ExtendPoc { get; set; } = true;

    [Display(Name = "Cantidad de POC a mostrar", GroupName = "POC", Order = 130)]
    [Range(1, 500)]
    public int PocCount { get; set; } = 10;

    [Display(Name = "Color del POC normal", GroupName = "POC", Order = 140)]
    public Color PocColor { get; set; } = Color.FromArgb(255, 255, 235, 59);

    [Display(Name = "Grosor del POC", GroupName = "POC", Order = 150)]
    [Range(1, 10)]
    public int PocWidth { get; set; } = 2;

    [Display(Name = "Estilo del POC", GroupName = "POC", Order = 155)]
    public LineDashStyle PocStyle { get; set; } = LineDashStyle.Solid;

    [Display(Name = "Color del POC comprador", GroupName = "POC", Order = 160)]
    public Color BullPocColor { get; set; } = Color.FromArgb(255, 20, 217, 144);

    [Display(Name = "Color del POC vendedor", GroupName = "POC", Order = 170)]
    public Color BearPocColor { get; set; } = Color.FromArgb(255, 242, 73, 104);

    // ------------------------------------------------------------------ área de valor

    [Display(Name = "Área de valor (%)", GroupName = "Área de valor", Order = 200)]
    [Range(1, 100)]
    public int ValueAreaPercent { get => _vaPercent; set { _vaPercent = Math.Clamp(value, 1, 100); RecalculateValues(); } }

    [Display(Name = "Rellenar el área de valor", GroupName = "Área de valor", Order = 210)]
    public bool ShowValueAreaBox { get; set; } = true;

    [Display(Name = "Color del relleno", GroupName = "Área de valor", Order = 220)]
    public Color ValueAreaBoxColor { get; set; } = Color.FromArgb(38, 255, 255, 255);

    [Display(Name = "Mostrar VAH", GroupName = "Área de valor", Order = 230)]
    public bool ShowVah { get; set; } = true;

    [Display(Name = "Color de VAH", GroupName = "Área de valor", Order = 240)]
    public Color VahColor { get; set; } = Color.FromArgb(255, 242, 54, 69);

    [Display(Name = "Mostrar VAL", GroupName = "Área de valor", Order = 250)]
    public bool ShowVal { get; set; } = true;

    [Display(Name = "Color de VAL", GroupName = "Área de valor", Order = 260)]
    public Color ValColor { get; set; } = Color.FromArgb(255, 76, 175, 80);

    [Display(Name = "Grosor de VAH / VAL", GroupName = "Área de valor", Order = 270)]
    [Range(1, 10)]
    public int ValueAreaWidth { get; set; } = 1;

    [Display(Name = "Estilo de VAH / VAL", GroupName = "Área de valor", Order = 280)]
    public LineDashStyle ValueAreaStyle { get; set; } = LineDashStyle.Solid;

    // ------------------------------------------------------------------ colas

    [Display(Name = "Mostrar área exterior y razón de colas", GroupName = "Área exterior y colas", Order = 300)]
    public bool ShowOuter { get; set; } = true;

    [Display(Name = "Área exterior (%)", GroupName = "Área exterior y colas", Order = 310)]
    [Range(51, 99)]
    public int OuterPercent { get => _outerPercent; set { _outerPercent = Math.Clamp(value, 51, 99); RecalculateValues(); } }

    [Display(Name = "Color de las líneas exteriores", GroupName = "Área exterior y colas", Order = 320)]
    public Color OuterColor { get; set; } = Color.FromArgb(255, 128, 128, 128);

    [Display(Name = "Colas gruesas: razón ≥ normal ×", GroupName = "Área exterior y colas", Order = 330)]
    [Range(1, 3)]
    public decimal FatTail { get; set; } = 1.15m;

    [Display(Name = "Concentrado: razón ≤ normal ×", GroupName = "Área exterior y colas", Order = 340)]
    [Range(0.3, 1)]
    public decimal ThinTail { get; set; } = 0.85m;

    [Display(Name = "Mostrar las colas de los últimos N perfiles", GroupName = "Área exterior y colas", Order = 350)]
    [Range(1, 20)]
    public int TailKeep { get; set; } = 1;

    [Display(Name = "Color: colas gruesas", GroupName = "Área exterior y colas", Order = 360)]
    public Color FatColor { get; set; } = Color.FromArgb(255, 255, 145, 0);

    [Display(Name = "Color: casi normal", GroupName = "Área exterior y colas", Order = 370)]
    public Color NormalColor { get; set; } = Color.FromArgb(255, 128, 128, 128);

    [Display(Name = "Color: concentrado", GroupName = "Área exterior y colas", Order = 380)]
    public Color ThinColor { get; set; } = Color.FromArgb(255, 0, 184, 212);

    // ------------------------------------------------------------------ POC vírgenes de otros períodos

    [Display(Name = "POC vírgenes diarios", GroupName = "POC vírgenes (naked POC)", Order = 400)]
    public bool NakedDaily { get => _naked.Enabled[0]; set { _naked.Enabled[0] = value; RecalculateValues(); } }

    [Display(Name = "POC vírgenes semanales", GroupName = "POC vírgenes (naked POC)", Order = 410)]
    public bool NakedWeekly { get => _naked.Enabled[1]; set { _naked.Enabled[1] = value; RecalculateValues(); } }

    [Display(Name = "POC vírgenes mensuales", GroupName = "POC vírgenes (naked POC)", Order = 420)]
    public bool NakedMonthly { get => _naked.Enabled[2]; set { _naked.Enabled[2] = value; RecalculateValues(); } }

    [Display(Name = "POC vírgenes anuales", GroupName = "POC vírgenes (naked POC)", Order = 430)]
    public bool NakedYearly { get => _naked.Enabled[3]; set { _naked.Enabled[3] = value; RecalculateValues(); } }

    [Display(Name = "Color diario", GroupName = "POC vírgenes (naked POC)", Order = 440)] public Color NakedDailyColor { get; set; } = Color.FromArgb(255, 255, 235, 59);
    [Display(Name = "Color semanal", GroupName = "POC vírgenes (naked POC)", Order = 441)] public Color NakedWeeklyColor { get; set; } = Color.FromArgb(255, 255, 152, 0);
    [Display(Name = "Color mensual", GroupName = "POC vírgenes (naked POC)", Order = 442)] public Color NakedMonthlyColor { get; set; } = Color.FromArgb(255, 0, 230, 118);
    [Display(Name = "Color anual", GroupName = "POC vírgenes (naked POC)", Order = 443)] public Color NakedYearlyColor { get; set; } = Color.FromArgb(255, 244, 67, 54);

    [Display(Name = "Mostrar también los ya tocados", GroupName = "POC vírgenes (naked POC)", Order = 450)]
    public bool NakedShowTouched { get; set; }

    [Display(Name = "Grosor de los POC vírgenes", GroupName = "POC vírgenes (naked POC)", Order = 460)]
    [Range(1, 6)]
    public int NakedWidth { get; set; } = 1;

    [Display(Name = "Etiqueta: período (D / W / M / Y)", GroupName = "POC vírgenes (naked POC)", Order = 470)]
    public bool NakedLabelPeriod { get; set; } = true;

    [Display(Name = "Etiqueta: fecha", GroupName = "POC vírgenes (naked POC)", Order = 471)]
    public bool NakedLabelDate { get; set; } = true;

    [Display(Name = "Etiqueta: precio", GroupName = "POC vírgenes (naked POC)", Order = 472)]
    public bool NakedLabelPrice { get; set; }

    // ------------------------------------------------------------------ períodos y uniones

    private bool Developing => _showDeveloping || _period == VpProfilePeriod.All;

    private bool Boundary(int bar) => _period != VpProfilePeriod.All && NewPeriod(bar, (VpPeriod)(int)_period);

    private long TimeOf(int bar) => Vp.Utc(GetCandle(bar).Time).Ticks;

    /// <summary>Ordena y junta las uniones que se tocan.</summary>
    private static List<(long From, long To)> Normalize(List<(long From, long To)> list)
    {
        list.Sort((a, b) => a.From.CompareTo(b.From));
        var res = new List<(long From, long To)>();
        foreach (var r in list)
        {
            if (res.Count > 0 && r.From <= res[^1].To) res[^1] = (res[^1].From, Math.Max(res[^1].To, r.To));
            else res.Add(r);
        }

        return res;
    }

    /// <summary>El período que empieza en <paramref name="next"/> sigue dentro de una unión que ya incluye a <paramref name="start"/>.</summary>
    private bool KeepMerging(int start, int next)
    {
        if (_ranges.Count == 0) return false;
        long t0 = TimeOf(start), t1 = TimeOf(next);
        foreach (var (from, to) in _ranges)
            if (t0 >= from && t0 <= to && t1 <= to) return true;
        return false;
    }

    private bool InRange(int start)
    {
        long t = TimeOf(start);
        return _ranges.Any(r => t >= r.From && t <= r.To);
    }

    protected override void Reset()
    {
        _naked.Reset();
        _done.Clear();
        _live.Clear();
        _developing = null;
        _periodStart = 0;
        _periodParts = 1;
        _acc.Clear(-1);
    }

    protected override void Commit(int bar)
    {
        if (_naked.Enabled.Any(e => e)) _naked.Commit(bar, NewPeriod, GetCandle, BarLength);
        if (bar > 0 && Boundary(bar))
        {
            if (KeepMerging(_periodStart, bar)) _periodParts++;
            else
            {
                Finish(_periodStart, bar - 1, _periodParts);
                _periodStart = bar;
                _periodParts = 1;
            }
        }

        // POC que siguen a la derecha hasta que una vela cerrada los toca
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var r = _live[i];
            if (bar <= r.EndBar) continue;
            Extend(bar, r.Poc, ref r.PocEnd, ref r.PocDead);
            Extend(bar, r.PocUp, ref r.PocUpEnd, ref r.PocUpDead);
            Extend(bar, r.PocDn, ref r.PocDnEnd, ref r.PocDnDead);
            if (r.PocDead && r.PocUpDead && r.PocDnDead) _live.RemoveAt(i);
        }

        if (Developing && bar >= CurrentBar - 2 && bar >= _periodStart)
        {
            _developing = BuildDeveloping(_periodStart, bar);
            if (_developing != null) _developing.Parts = _periodParts;
        }
    }

    /// <summary>Un período (o varios unidos a mano) terminó: se arma y, si corresponde, se une con el anterior.</summary>
    private void Finish(int s, int e, int parts)
    {
        bool manual = parts > 1 || InRange(s);
        var r = Build(s, e, true);
        if (r == null) return;
        r.Manual = manual;
        r.Parts = parts;
        if (_autoMerge && !manual && _done.Count > 0)
        {
            var prev = _done[^1];
            if (!prev.Manual && prev.EndBar == s - 1 && Overlap(prev, r) * 100 >= _autoMergePercent && Build(prev.StartBar, e, true) is { } joined)
            {
                _done.RemoveAt(_done.Count - 1);
                _live.Remove(prev);
                joined.Auto = true;
                joined.Parts = prev.Parts + 1;
                r = joined;
            }
        }

        _done.Add(r);
        _live.Add(r);
    }

    /// <summary>Parte del rango más chico que comparten dos perfiles (0 a 1).</summary>
    private double Overlap(Result a, Result b)
    {
        bool va = _overlapBasis == OverlapBasis.ValueArea;
        decimal aLo = va ? a.Val : a.YMin, aHi = va ? a.Vah : a.YMax, bLo = va ? b.Val : b.YMin, bHi = va ? b.Vah : b.YMax;
        decimal inter = Math.Min(aHi, bHi) - Math.Max(aLo, bLo);
        decimal smaller = Math.Min(aHi - aLo, bHi - bLo);
        if (inter <= 0) return 0;
        return smaller <= 0 ? 1 : (double)(inter / smaller);
    }

    private void Extend(int bar, decimal price, ref int end, ref bool dead)
    {
        if (dead) return;
        end = bar;
        if (Touched(bar, price)) dead = true;
    }

    protected override void Live(int bar)
    {
        if (Developing && bar == _periodStart && Committed < bar) _developing = null;
    }

    private decimal Tick => InstrumentInfo is { } i ? i.InstrumentTickSize > 0 ? i.InstrumentTickSize : i.TickSize : 0;

    private string Bucket => string.Join("|", "vpu-profile3", InstrumentInfo?.Instrument ?? "", Tick, (int)_rowSizing,
        _rowSizing == RowSizing.Rows ? _rows : _ticksPerRow, (int)_volumeMode, _capMult, _excludeAuction, _vaPercent, _outerPercent);

    private string Fingerprint(int s, int e)
    {
        decimal vol = 0, delta = 0;
        for (int b = s; b <= e; b++)
        {
            var c = GetCandle(b);
            vol += c.Volume;
            delta += c.Delta;
        }

        // la misma clave en cualquier período del gráfico (inicio, inicio del siguiente, volumen y delta): al pasar de 1 h a
        // 4 h o a 15 min los perfiles cerrados salen de lo ya calculado. El recorte y la exclusión de subastas miran cada vela:
        // ahí sí cuenta el largo de la vela.
        string end = e + 1 < CurrentBar ? TimeOf(e + 1).ToString(CultureInfo.InvariantCulture) : GetCandle(e).Time.Ticks + "~" + (e - s);
        string tf = _excludeAuction || _volumeMode == VpVolumeMode.Cap ? BarLength.Ticks.ToString(CultureInfo.InvariantCulture) : "";
        return string.Join("|", TimeOf(s), end, vol, delta, tf);
    }

    /// <summary>Tope de volumen por nivel en el modo «recortar extremos» (mediana del período × el múltiplo).</summary>
    private decimal CapFor(int s, int e)
    {
        if (_volumeMode != VpVolumeMode.Cap) return decimal.MaxValue;
        var sample = new List<decimal>();
        for (int b = s; b <= e; b++)
        {
            if (_excludeAuction && Vp.IsAuction(GetCandle(b).Time)) continue;
            foreach (var l in GetTickLevels(b)) sample.Add(l.Volume);
        }

        return sample.Count > 0 ? Vp.Median(sample) * _capMult : decimal.MaxValue;
    }

    private void AddBar(Acc acc, int bar, decimal cap)
    {
        var c = GetCandle(bar);
        if (c.Volume > 0)
        {
            if (c.Low < acc.Lo) acc.Lo = c.Low;
            if (c.High > acc.Hi) acc.Hi = c.High;
        }

        if (_excludeAuction && Vp.IsAuction(c.Time)) return;
        foreach (var l in GetTickLevels(bar))
        {
            if (l.Volume <= 0) continue;
            double v = (double)Math.Min(l.Volume, cap);
            if (_volumeMode == VpVolumeMode.Log) v = Math.Log(1 + v);
            double buyShare = l.Bid + l.Ask > 0 ? (double)(l.Ask / (l.Bid + l.Ask)) : 0.5;
            acc.Levels.TryGetValue(l.Price, out var cur);
            acc.Levels[l.Price] = (cur.All + v, cur.Buy + v * buyShare);
        }
    }

    /// <summary>El período en curso: se suma solo lo nuevo desde la última vez (en «recortar», el tope cambia: se rearma).</summary>
    private Result? BuildDeveloping(int s, int e)
    {
        if (_volumeMode == VpVolumeMode.Cap) return Build(s, e, false);
        if (_acc.Start != s || _acc.End > e) _acc.Clear(s);
        while (_acc.End < e) AddBar(_acc, ++_acc.End, decimal.MaxValue);
        return FromLevels(s, e, _acc);
    }

    private Result? Build(int s, int e, bool final)
    {
        if (e < s) return null;
        string? key = final ? Fingerprint(s, e) : null;
        if (key != null && ResultCache.TryGet(Bucket, key, out var bytes) && Read(bytes) is { } cached)
        {
            cached.StartBar = s;
            cached.EndBar = e;
            cached.PocEnd = cached.PocUpEnd = cached.PocDnEnd = e;
            return cached;
        }

        Result? r;
        if (final && _volumeMode != VpVolumeMode.Cap && _acc.Start == s && _acc.End == e) r = FromLevels(s, e, _acc); // ya sumado
        else
        {
            var acc = new Acc();
            acc.Clear(s);
            decimal cap = CapFor(s, e);
            for (int b = s; b <= e; b++) AddBar(acc, b, cap);
            r = FromLevels(s, e, acc);
        }

        if (r != null && key != null) ResultCache.Put(Bucket, key, Write(r));
        return r;
    }

    private Result? FromLevels(int s, int e, Acc acc)
    {
        decimal lo = acc.Lo, hi = acc.Hi;
        if (lo > hi || acc.Levels.Count == 0) return null;
        decimal yMin = lo, yMax = hi, step;
        int rows;
        decimal tick = Tick;
        if (_rowSizing == RowSizing.Ticks && tick > 0)
        {
            step = tick * _ticksPerRow;
            if ((hi - lo) / step > MaxRows) step = tick * Math.Ceiling((hi - lo) / tick / MaxRows);
            yMin = Math.Floor(lo / step) * step;
            rows = (int)Math.Floor((hi - yMin) / step);
            yMax = yMin + (rows + 1) * step;
        }
        else
        {
            rows = _rows;
            step = Math.Max((hi - lo) / rows, 0.0000000001m);
        }

        var all = new double[rows + 1];
        var buy = new double[rows + 1];
        foreach (var (price, (a, b)) in acc.Levels)
        {
            int i = (int)Math.Clamp(Math.Floor((price - yMin) / step), 0, rows);
            all[i] += a;
            buy[i] += b;
        }

        int pi = ArgMax(all);
        if (all[pi] <= 0) return null;
        var sell = new double[rows + 1];
        for (int i = 0; i <= rows; i++) sell[i] = all[i] - buy[i];
        decimal Mid(int i) => Math.Min(yMin + (i + 0.5m) * step, yMax);
        double total = all.Sum();
        var (vu, vd, accV) = Expand(all, pi, pi, all[pi], total * _vaPercent / 100.0);
        var (ou, od, _) = Expand(all, vu, vd, accV, total * _outerPercent / 100.0);
        return new Result
        {
            StartBar = s,
            EndBar = e,
            YMin = yMin,
            YMax = yMax,
            Step = step,
            Values = all,
            Buy = buy,
            Poc = Mid(pi),
            PocUp = Mid(ArgMax(buy)),
            PocDn = Mid(ArgMax(sell)),
            Vah = Math.Min(yMin + (vu + 1) * step, yMax),
            Val = yMin + vd * step,
            OuterHi = Math.Min(yMin + (ou + 1) * step, yMax),
            OuterLo = yMin + od * step,
            Tail = (double)(ou - od + 1) / Math.Max(vu - vd + 1, 1),
            PocEnd = e,
            PocUpEnd = e,
            PocDnEnd = e,
        };
    }

    private static int ArgMax(double[] a)
    {
        int best = 0;
        for (int i = 1; i < a.Length; i++)
            if (a[i] > a[best]) best = i;
        return best;
    }

    /// <summary>Desde [dn, up] agrega hacia el lado vecino con más volumen hasta cubrir <paramref name="target"/>.</summary>
    private static (int Up, int Dn, double Acc) Expand(double[] v, int up, int dn, double acc, double target)
    {
        int last = v.Length - 1;
        while (acc < target && (up < last || dn > 0))
        {
            double a = up < last ? v[up + 1] : -1, b = dn > 0 ? v[dn - 1] : -1;
            if (a > b) acc += v[++up];
            else acc += v[--dn];
        }

        return (up, dn, acc);
    }

    private static byte[] Write(Result r)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            foreach (var d in new[] { r.YMin, r.YMax, r.Step, r.Poc, r.PocUp, r.PocDn, r.Vah, r.Val, r.OuterHi, r.OuterLo }) w.Write(d);
            w.Write(r.Tail);
            w.Write(r.Values.Length);
            foreach (var v in r.Values) w.Write(v);
            foreach (var v in r.Buy) w.Write(v);
        }

        return ms.ToArray();
    }

    private static Result? Read(byte[] bytes)
    {
        try
        {
            using var rd = new BinaryReader(new MemoryStream(bytes));
            var r = new Result
            {
                YMin = rd.ReadDecimal(), YMax = rd.ReadDecimal(), Step = rd.ReadDecimal(), Poc = rd.ReadDecimal(), PocUp = rd.ReadDecimal(),
                PocDn = rd.ReadDecimal(), Vah = rd.ReadDecimal(), Val = rd.ReadDecimal(), OuterHi = rd.ReadDecimal(), OuterLo = rd.ReadDecimal(),
                Tail = rd.ReadDouble(),
            };
            int n = rd.ReadInt32();
            r.Values = new double[n];
            r.Buy = new double[n];
            for (int i = 0; i < n; i++) r.Values[i] = rd.ReadDouble();
            for (int i = 0; i < n; i++) r.Buy[i] = rd.ReadDouble();
            return r;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ menú del clic derecho: unir perfiles

    private Result? ResultAt(int bar)
    {
        foreach (var r in _done)
            if (bar >= r.StartBar && bar <= r.EndBar) return r;
        return _developing != null && bar >= _developing.StartBar ? _developing : null;
    }

    /// <summary>Primera y última vela del período <paramref name="p"/> que contiene a <paramref name="bar"/>.</summary>
    private (int From, int To) Enclosing(int bar, VpPeriod p)
    {
        int a = bar;
        while (a > 0 && !NewPeriod(a, p)) a--;
        int b = bar + 1;
        while (b < CurrentBar && !NewPeriod(b, p)) b++;
        return (a, b - 1);
    }

    private void AddRange(long from, long to)
    {
        var list = new List<(long, long)>(_ranges) { (Math.Min(from, to), Math.Max(from, to)) };
        _ranges = Normalize(list);
        RecalculateValues();
    }

    public override IReadOnlyList<IndicatorMenuItem> GetMenuItems(int x, int y)
    {
        var items = new List<IndicatorMenuItem>();
        if (ChartInfo == null || CurrentBar <= 0 || _period == VpProfilePeriod.All) return items;
        int bar = Math.Clamp(ChartInfo.PriceChartContainer.GetBarByX(x), 0, CurrentBar - 1);
        var r = ResultAt(bar);
        if (r == null)
        {
            if (_head != null) items.Add(new IndicatorMenuItem(UiText.Tr("Perfiles: cancelar el inicio marcado"), () => _head = null));
            return items;
        }

        long from = TimeOf(r.StartBar), to = TimeOf(Math.Min(r.EndBar, CurrentBar - 1));
        if (_head is not { } head)
            items.Add(new IndicatorMenuItem(UiText.Tr("Perfiles: marcar este como INICIO de la unión"), () => _head = (from, to)));
        else
        {
            items.Add(new IndicatorMenuItem(UiText.Tr("Perfiles: unir desde el inicio marcado hasta este (FIN)"), () =>
            {
                _head = null;
                AddRange(Math.Min(head.From, from), Math.Max(head.To, to));
            }));
            items.Add(new IndicatorMenuItem(UiText.Tr("Perfiles: cancelar el inicio marcado"), () => _head = null));
        }

        // atajos exactos: la semana o el mes entero de este perfil
        if (_period < VpProfilePeriod.Week)
        {
            var (a, b) = Enclosing(r.StartBar, VpPeriod.Week);
            items.Add(new IndicatorMenuItem(UiText.Tr("Perfiles: unir toda la semana de este perfil"), () => AddRange(TimeOf(a), TimeOf(b))));
        }

        if (_period < VpProfilePeriod.Month)
        {
            var (a, b) = Enclosing(r.StartBar, VpPeriod.Month);
            items.Add(new IndicatorMenuItem(UiText.Tr("Perfiles: unir todo el mes de este perfil"), () => AddRange(TimeOf(a), TimeOf(b))));
        }

        if (r.Manual)
            items.Add(new IndicatorMenuItem(UiText.Tr("Perfiles: separar este perfil unido"), () =>
            {
                _ranges = _ranges.Where(g => !(g.To >= from && g.From <= to)).ToList();
                RecalculateValues();
            }));
        if (_autoMerge)
            items.Add(new IndicatorMenuItem(UiText.Tr("Perfiles: dejar de unir solos los que se superponen"), () => AutoMerge = false, true));
        else
            items.Add(new IndicatorMenuItem(string.Format(UiText.Tr("Perfiles: unir solos los vecinos que se superponen más de {0}%"), _autoMergePercent), () => AutoMerge = true));
        if (_ranges.Count > 0)
            items.Add(new IndicatorMenuItem(UiText.Tr("Perfiles: quitar todas las uniones"), () =>
            {
                _ranges = new List<(long, long)>();
                RecalculateValues();
            }));
        return items;
    }

    // ------------------------------------------------------------------ dibujo

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        double tailRef = Vp.CentralZ(_outerPercent / 100.0) / Vp.CentralZ(_vaPercent / 100.0);
        int pocFrom = Math.Max(0, _done.Count - PocCount);
        int tailFrom = Math.Max(0, _done.Count - TailKeep);
        for (int i = 0; i < _done.Count; i++) Draw(context, _done[i], first, last, true, i >= pocFrom, i >= tailFrom, tailRef);
        if (Developing && _developing != null) Draw(context, _developing, first, last, false, true, false, tailRef);
        if (_naked.Enabled.Any(e => e))
            VpNakedPoc.Render(this, context, _naked, new[] { NakedDailyColor, NakedWeeklyColor, NakedMonthlyColor, NakedYearlyColor }, NakedShowTouched, NakedWidth, LineDashStyle.Solid, NakedLabelPeriod, NakedLabelDate, NakedLabelPrice, 50);

        // el inicio marcado para unir: recuadro punteado
        if (_head is { } head && CurrentBar > 0)
        {
            var r = _done.FirstOrDefault(d => TimeOf(d.StartBar) == head.From);
            if (r != null && r.EndBar >= first && r.StartBar <= last)
            {
                var pen = Vp.Pen(Color.FromArgb(230, 255, 196, 0), 2, LineDashStyle.Dash);
                int x0 = X(r.StartBar), x1 = X(r.EndBar + 1), yTop = Y(r.YMax), yBot = Y(r.YMin);
                context.DrawRectangle(pen, new Rectangle(x0, Math.Min(yTop, yBot), Math.Max(2, x1 - x0), Math.Max(2, Math.Abs(yBot - yTop))));
                Text(context, UiText.Tr("Inicio de la unión: clic derecho en el último perfil → «unir hasta este»"), Color.FromArgb(255, 255, 196, 0), x0, Math.Min(yTop, yBot) - 2);
            }
        }
    }

    private void Draw(RenderContext ctx, Result r, int first, int last, bool final, bool withPoc, bool withTail, double tailRef)
    {
        int pocReach = Math.Max(r.PocEnd, Math.Max(r.PocUpEnd, r.PocDnEnd));
        if (r.StartBar > last || Math.Max(r.EndBar, withPoc ? pocReach : r.EndBar) + 1 < first) return;
        int x0 = X(r.StartBar), x1 = X(r.EndBar + 1);
        double max = r.Values.Max();

        // perfil: cada fila, desde el inicio del período hacia la derecha
        if (max > 0 && r.EndBar >= first)
        {
            double w = Math.Max(1, x1 - x0) * Math.Clamp(WidthPercent, 1, 100) / 100.0;
            int pocRow = ArgMax(r.Values);
            bool hasBuy = r.Buy.Length == r.Values.Length;
            for (int i = 0; i < r.Values.Length; i++)
            {
                if (r.Values[i] <= 0) continue;
                decimal lo = r.YMin + i * r.Step, hi = Math.Min(lo + r.Step, r.YMax);
                int yTop = Y(hi), yBot = Y(lo);
                var rect = (int len, int dx) => new Rectangle(x0 + dx, Math.Min(yTop, yBot), len, Math.Max(1, Math.Abs(yBot - yTop)));
                double buy = hasBuy ? r.Buy[i] : r.Values[i] / 2, sell = r.Values[i] - buy;
                switch (Style)
                {
                    case ProfileStyle.Delta:
                    {
                        double d = buy - sell;
                        int len = (int)Math.Round(Math.Abs(d) / max * w);
                        if (len > 0) ctx.FillRectangle(d >= 0 ? BuyColor : SellColor, rect(len, 0));
                        break;
                    }
                    case ProfileStyle.BidAsk:
                    {
                        int ls = (int)Math.Round(sell / max * w), lb = (int)Math.Round(buy / max * w);
                        if (ls > 0) ctx.FillRectangle(SellColor, rect(ls, 0));
                        if (lb > 0) ctx.FillRectangle(BuyColor, rect(lb, ls));
                        break;
                    }
                    default:
                    {
                        int len = (int)Math.Round(r.Values[i] / max * w);
                        bool inVa = lo >= r.Val && hi <= r.Vah;
                        var color = ColorPocRow && i == pocRow ? PocColor : ColorValueAreaRows && inVa ? ValueAreaRowColor : ProfileColor;
                        if (len > 0) ctx.FillRectangle(color, rect(len, 0));
                        break;
                    }
                }
            }

            if (ShowValueAreaBox)
                ctx.FillRectangle(ValueAreaBoxColor, new Rectangle(x0, Y(r.Vah), Math.Max(1, x1 - x0), Math.Max(1, Y(r.Val) - Y(r.Vah))));
            var vaStyle = final ? ValueAreaStyle : LineDashStyle.Dot;
            if (ShowVah) ctx.DrawLine(Vp.Pen(VahColor, ValueAreaWidth, vaStyle), x0, Y(r.Vah), x1, Y(r.Vah));
            if (ShowVal) ctx.DrawLine(Vp.Pen(ValColor, ValueAreaWidth, vaStyle), x0, Y(r.Val), x1, Y(r.Val));
            if (LabelMerged && r.Parts > 1)
            {
                string label = string.Format(UiText.Tr(r.Auto ? "Unido solo · {0} períodos" : "Unido · {0} períodos"), r.Parts);
                Text(ctx, label, Color.FromArgb(200, 200, 200, 200), x0 + 2, Y(r.YMax) - 2);
            }
        }

        if (ShowPoc && withPoc)
        {
            if (PocType == PocKind.Regular) DrawPoc(ctx, r.Poc, x0, x1, r.PocEnd, r.PocDead, PocColor, final);
            else
            {
                DrawPoc(ctx, r.PocUp, x0, x1, r.PocUpEnd, r.PocUpDead, BullPocColor, final);
                DrawPoc(ctx, r.PocDn, x0, x1, r.PocDnEnd, r.PocDnDead, BearPocColor, final);
            }
        }

        if (final && withTail && ShowOuter && _outerPercent > _vaPercent)
        {
            var pen = Vp.Pen(OuterColor, 1, LineDashStyle.Dot);
            ctx.DrawLine(pen, x0, Y(r.OuterHi), x1, Y(r.OuterHi));
            ctx.DrawLine(pen, x0, Y(r.OuterLo), x1, Y(r.OuterLo));
            double rel = tailRef > 0 ? r.Tail / tailRef : 1;
            var (cls, col) = rel >= (double)FatTail ? ("Colas gruesas: si sale del área de valor tiende a irse lejos", FatColor)
                : rel <= (double)ThinTail ? ("Concentrado: en los bordes del área de valor tiende a volver", ThinColor)
                : ("Casi normal", NormalColor);
            string text = string.Format(UiText.Tr("Razón de colas {0} (normal ≈ {1})"), r.Tail.ToString("0.##"), tailRef.ToString("0.##")) + "\n" + UiText.Tr(cls);
            var size = ctx.MeasureString(text, SmallFont);
            ctx.FillRectangle(Vp.Alpha(col, 60), new Rectangle(x1 + 2, Y(r.OuterHi) - size.Height - 4, size.Width + 8, size.Height + 4), 3);
            ctx.DrawString(text, SmallFont, col, x1 + 6, Y(r.OuterHi) - size.Height - 2);
        }
    }

    /// <summary>
    /// Las líneas del perfil para las alertas: POC (o POC alcista / bajista), VAH y VAL de los últimos perfiles y del que se
    /// está formando, y los POC vírgenes. El Id va con el inicio del perfil, así sigue siendo el mismo al recalcular.
    /// </summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        int last = Math.Max(0, CurrentBar - 1);
        int pocFrom = Math.Max(0, _done.Count - PocCount);
        string DateOf(int bar) => bar >= 0 && bar < CurrentBar ? GetCandle(bar).Time.ToString("dd/MM HH:mm") : "";
        IEnumerable<IndicatorLevel> Of(Result r, bool final, int index)
        {
            string id = (final ? "p" : "dev") + (r.StartBar >= 0 && r.StartBar < CurrentBar ? GetCandle(r.StartBar).Time.Ticks : 0);
            string when = final ? DateOf(r.StartBar) : UiText.Tr("en formación");
            int to = ExtendPoc ? int.MaxValue : r.EndBar;
            if (ShowPoc && (index >= pocFrom || !final))
            {
                if (PocType == PocKind.Regular)
                {
                    if (!(final && r.PocDead && _deletePoc)) yield return new IndicatorLevel(id + ".poc", "POC " + when, r.Poc, r.StartBar, final && r.PocDead ? r.PocEnd : to);
                }
                else
                {
                    if (!(final && r.PocUpDead && _deletePoc)) yield return new IndicatorLevel(id + ".pocu", UiText.Tr("POC alcista ") + when, r.PocUp, r.StartBar, final && r.PocUpDead ? r.PocUpEnd : to);
                    if (!(final && r.PocDnDead && _deletePoc)) yield return new IndicatorLevel(id + ".pocd", UiText.Tr("POC bajista ") + when, r.PocDn, r.StartBar, final && r.PocDnDead ? r.PocDnEnd : to);
                }
            }

            if (ShowVah) yield return new IndicatorLevel(id + ".vah", "VAH " + when, r.Vah, r.StartBar, final ? r.EndBar : last);
            if (ShowVal) yield return new IndicatorLevel(id + ".val", "VAL " + when, r.Val, r.StartBar, final ? r.EndBar : last);
        }

        // los perfiles cercanos al final (los que se ven y se alertan; no toda la historia)
        for (int i = Math.Max(0, _done.Count - Math.Max(PocCount, 5)); i < _done.Count; i++)
            foreach (var l in Of(_done[i], true, i)) yield return l;
        if (Developing && _developing != null)
            foreach (var l in Of(_developing, false, _done.Count)) yield return l;
        if (_naked.Enabled.Any(e => e))
            foreach (var l in VpNakedPoc.Levels(this, _naked, NakedShowTouched, 50)) yield return l with { Id = "n." + l.Id };
    }

    private void DrawPoc(RenderContext ctx, decimal price, int x0, int x1, int end, bool dead, Color color, bool final)
    {
        if (final && dead && _deletePoc) return;
        // sin tocar: llega hasta la vela actual (o solo el ancho del perfil si no se extiende)
        int xEnd = !ExtendPoc ? x1 : final ? (dead ? X(end, false) : X(CurrentBar - 1, false)) : X(CurrentBar - 1, false);
        ctx.DrawLine(Vp.Pen(color, PocWidth, final ? PocStyle : LineDashStyle.Dot), x0, Y(price), Math.Max(x0 + 1, xEnd), Y(price));
    }
}
