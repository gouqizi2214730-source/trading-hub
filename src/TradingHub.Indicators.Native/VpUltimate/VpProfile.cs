using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators.Native.VpUltimate;

/// <summary>
/// Módulo 1 del «VP definitivo»: perfil de volumen por período con POC (normal o comprador / vendedor), área de valor,
/// área de valor exterior y la «razón de colas» (ancho del área exterior ÷ ancho del área de valor, comparada con la de
/// una distribución normal). Solo se dibujan los períodos terminados (el actual, opcional, con las velas cerradas). Los
/// períodos cerrados se guardan en disco: al volver a abrir no se recalculan.
/// </summary>
[DisplayName("VP definitivo · Perfil de volumen")]
[Category(Vp.Category)]
[Description("Perfil de volumen por período (día, semana…) con POC normal o comprador / vendedor, área de valor, área exterior y razón de colas. Solo períodos terminados: no repinta.")]
public class VpProfile : VpIndicator
{
    public enum PocKind
    {
        [Display(Name = "POC normal")] Regular,
        [Display(Name = "POC comprador / vendedor")] BullBear,
    }

    private sealed class Result
    {
        public int StartBar, EndBar;
        public decimal YMin, YMax, Step;
        public double[] Values = Array.Empty<double>();
        public decimal Poc, PocUp, PocDn, Vah, Val, OuterHi, OuterLo;
        public double Tail;

        // POC que se extienden a la derecha: hasta qué vela llegan y si fueron tocados
        public int PocEnd, PocUpEnd, PocDnEnd;
        public bool PocDead, PocUpDead, PocDnDead;
    }

    private readonly List<Result> _done = new();
    private readonly List<Result> _live = new(); // con algún POC todavía sin tocar
    private Result? _developing;
    private int _periodStart;

    private VpPeriod _period = VpPeriod.Day;
    private int _rows = 200;
    private VpVolumeMode _volumeMode = VpVolumeMode.Raw;
    private decimal _capMult = 10;
    private bool _excludeAuction;
    private int _vaPercent = 70;
    private int _outerPercent = 90;
    private bool _showDeveloping;
    private bool _deletePoc;

    public VpProfile()
    {
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    // ------------------------------------------------------------------ cálculo

    [Display(Name = "Período del perfil", GroupName = "Perfil", Order = 10)]
    public VpPeriod Period { get => _period; set { _period = value; RecalculateValues(); } }

    [Display(Name = "Filas del perfil", GroupName = "Perfil", Order = 20)]
    [Range(10, 1000)]
    public int Rows { get => _rows; set { _rows = Math.Clamp(value, 10, 1000); RecalculateValues(); } }

    [Display(Name = "Volumen", GroupName = "Perfil", Order = 30)]
    public VpVolumeMode VolumeMode { get => _volumeMode; set { _volumeMode = value; RecalculateValues(); } }

    [Display(Name = "Recorte (× mediana del período)", GroupName = "Perfil", Order = 40)]
    [Range(2, 100)]
    public decimal CapMultiple { get => _capMult; set { _capMult = Math.Clamp(value, 2, 100); RecalculateValues(); } }

    [Display(Name = "Excluir velas de subasta de apertura / cierre (acciones de EE. UU.)", GroupName = "Perfil", Order = 50)]
    public bool ExcludeAuction { get => _excludeAuction; set { _excludeAuction = value; RecalculateValues(); } }

    [Display(Name = "Mostrar el período en curso", GroupName = "Perfil", Order = 60)]
    public bool ShowDeveloping { get => _showDeveloping; set { _showDeveloping = value; RecalculateValues(); } }

    [Display(Name = "Ancho del perfil (% del período)", GroupName = "Perfil", Order = 70)]
    [Range(1, 100)]
    public int WidthPercent { get; set; } = 100;

    [Display(Name = "Color del perfil", GroupName = "Perfil", Order = 80)]
    public Color ProfileColor { get; set; } = Color.FromArgb(77, 33, 150, 243);

    // ------------------------------------------------------------------ POC

    [Display(Name = "Mostrar POC", GroupName = "POC", Order = 100)]
    public bool ShowPoc { get; set; } = true;

    [Display(Name = "Tipo de POC", GroupName = "POC", Order = 110)]
    public PocKind PocType { get; set; } = PocKind.Regular;

    [Display(Name = "Borrar el POC al ser tocado (solo los vírgenes)", GroupName = "POC", Order = 120)]
    public bool DeletePocOnTouch { get => _deletePoc; set { _deletePoc = value; RecalculateValues(); } }

    [Display(Name = "Cantidad de POC a mostrar", GroupName = "POC", Order = 130)]
    [Range(1, 500)]
    public int PocCount { get; set; } = 10;

    [Display(Name = "Color del POC normal", GroupName = "POC", Order = 140)]
    public Color PocColor { get; set; } = Color.FromArgb(255, 255, 235, 59);

    [Display(Name = "Grosor del POC", GroupName = "POC", Order = 150)]
    [Range(1, 10)]
    public int PocWidth { get; set; } = 2;

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

    // ------------------------------------------------------------------ cálculo

    protected override void Reset()
    {
        _done.Clear();
        _live.Clear();
        _developing = null;
        _periodStart = 0;
    }

    protected override void Commit(int bar)
    {
        if (bar > 0 && NewPeriod(bar, _period))
        {
            if (Build(_periodStart, bar - 1, true) is { } r)
            {
                _done.Add(r);
                _live.Add(r);
            }

            _periodStart = bar;
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

        if (_showDeveloping && bar >= CurrentBar - 2 && bar >= _periodStart) _developing = Build(_periodStart, bar, false);
    }

    private void Extend(int bar, decimal price, ref int end, ref bool dead)
    {
        if (dead) return;
        end = bar;
        if (Touched(bar, price)) dead = true;
    }

    protected override void Live(int bar)
    {
        if (_showDeveloping && bar == _periodStart && Committed < bar) _developing = null;
    }

    private string Bucket => string.Join("|", "vpu-profile1", InstrumentInfo?.Instrument ?? "", InstrumentInfo?.TickSize ?? 0, _rows, (int)_volumeMode, _capMult,
        _excludeAuction, _vaPercent, _outerPercent);

    private string Fingerprint(int s, int e)
    {
        decimal vol = 0, delta = 0;
        for (int b = s; b <= e; b++)
        {
            var c = GetCandle(b);
            vol += c.Volume;
            delta += c.Delta;
        }

        return string.Join("|", GetCandle(s).Time.Ticks, GetCandle(e).Time.Ticks, e - s, vol, delta);
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

        decimal yMin = decimal.MaxValue, yMax = decimal.MinValue;
        for (int b = s; b <= e; b++)
        {
            var c = GetCandle(b);
            if (c.Volume <= 0) continue;
            yMin = Math.Min(yMin, c.Low);
            yMax = Math.Max(yMax, c.High);
        }

        if (yMin > yMax) return null;
        int rows = _rows;
        decimal step = Math.Max((yMax - yMin) / rows, 0.0000000001m);
        var all = new double[rows + 1];
        var up = new double[rows + 1];
        var dn = new double[rows + 1];

        decimal cap = decimal.MaxValue;
        if (_volumeMode == VpVolumeMode.Cap)
        {
            var sample = new List<decimal>();
            for (int b = s; b <= e; b++)
            {
                var c = GetCandle(b);
                if (_excludeAuction && Vp.IsAuction(c.Time)) continue;
                foreach (var l in c.GetAllPriceLevels()) sample.Add(l.Volume);
            }

            if (sample.Count > 0) cap = Vp.Median(sample) * _capMult;
        }

        for (int b = s; b <= e; b++)
        {
            var c = GetCandle(b);
            if (_excludeAuction && Vp.IsAuction(c.Time)) continue;
            foreach (var l in c.GetAllPriceLevels())
            {
                if (l.Volume <= 0) continue;
                double v = (double)Math.Min(l.Volume, cap);
                if (_volumeMode == VpVolumeMode.Log) v = Math.Log(1 + v);
                double buyShare = l.Bid + l.Ask > 0 ? (double)(l.Ask / (l.Bid + l.Ask)) : 0.5;
                int i = (int)Math.Clamp(Math.Floor((l.Price - yMin) / step), 0, rows);
                all[i] += v;
                up[i] += v * buyShare;
                dn[i] += v * (1 - buyShare);
            }
        }

        int pi = ArgMax(all);
        if (all[pi] <= 0) return null;
        decimal Mid(int i) => Math.Min(yMin + (i + 0.5m) * step, yMax);
        double total = all.Sum();
        var (vu, vd, acc) = Expand(all, pi, pi, all[pi], total * _vaPercent / 100.0);
        var (ou, od, _) = Expand(all, vu, vd, acc, total * _outerPercent / 100.0);
        var r = new Result
        {
            StartBar = s,
            EndBar = e,
            YMin = yMin,
            YMax = yMax,
            Step = step,
            Values = all,
            Poc = Mid(pi),
            PocUp = Mid(ArgMax(up)),
            PocDn = Mid(ArgMax(dn)),
            Vah = Math.Min(yMin + (vu + 1) * step, yMax),
            Val = yMin + vd * step,
            OuterHi = Math.Min(yMin + (ou + 1) * step, yMax),
            OuterLo = yMin + od * step,
            Tail = (double)(ou - od + 1) / Math.Max(vu - vd + 1, 1),
            PocEnd = e,
            PocUpEnd = e,
            PocDnEnd = e,
        };
        if (key != null) ResultCache.Put(Bucket, key, Write(r));
        return r;
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
            r.Values = new double[rd.ReadInt32()];
            for (int i = 0; i < r.Values.Length; i++) r.Values[i] = rd.ReadDouble();
            return r;
        }
        catch
        {
            return null;
        }
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
        if (_showDeveloping && _developing != null) Draw(context, _developing, first, last, false, true, false, tailRef);
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
            for (int i = 0; i < r.Values.Length; i++)
            {
                if (r.Values[i] <= 0) continue;
                decimal lo = r.YMin + i * r.Step, hi = Math.Min(lo + r.Step, r.YMax);
                int yTop = Y(hi), yBot = Y(lo);
                int len = (int)Math.Round(r.Values[i] / max * w);
                if (len > 0) ctx.FillRectangle(ProfileColor, new Rectangle(x0, Math.Min(yTop, yBot), len, Math.Max(1, Math.Abs(yBot - yTop))));
            }

            if (ShowValueAreaBox)
                ctx.FillRectangle(ValueAreaBoxColor, new Rectangle(x0, Y(r.Vah), Math.Max(1, x1 - x0), Math.Max(1, Y(r.Val) - Y(r.Vah))));
            var vaStyle = final ? ValueAreaStyle : LineDashStyle.Dot;
            if (ShowVah) ctx.DrawLine(Vp.Pen(VahColor, ValueAreaWidth, vaStyle), x0, Y(r.Vah), x1, Y(r.Vah));
            if (ShowVal) ctx.DrawLine(Vp.Pen(ValColor, ValueAreaWidth, vaStyle), x0, Y(r.Val), x1, Y(r.Val));
        }

        if (ShowPoc && withPoc)
        {
            if (PocType == PocKind.Regular) DrawPoc(ctx, r.Poc, x0, r.PocEnd, r.PocDead, PocColor, final);
            else
            {
                DrawPoc(ctx, r.PocUp, x0, r.PocUpEnd, r.PocUpDead, BullPocColor, final);
                DrawPoc(ctx, r.PocDn, x0, r.PocDnEnd, r.PocDnDead, BearPocColor, final);
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

    private void DrawPoc(RenderContext ctx, decimal price, int x0, int end, bool dead, Color color, bool final)
    {
        if (final && dead && _deletePoc) return;
        // sin tocar: llega hasta la vela actual
        int xEnd = final ? (dead ? X(end, false) : X(CurrentBar - 1, false)) : X(CurrentBar - 1, false);
        ctx.DrawLine(Vp.Pen(color, PocWidth, final ? LineDashStyle.Solid : LineDashStyle.Dot), x0, Y(price), Math.Max(x0 + 1, xEnd), Y(price));
    }
}
