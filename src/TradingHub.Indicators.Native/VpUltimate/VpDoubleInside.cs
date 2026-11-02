using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators.Native.VpUltimate;

/// <summary>
/// Módulo 11 del «VP definitivo»: doble vela interior diaria. Una vela madre (K3) contiene a la siguiente (K2) y esta a la
/// siguiente (K1): dos días seguidos de compresión. Los bordes de la madre son el rango de acumulación: un cierre por
/// encima es ruptura alcista, por debajo bajista, y el rango termina. Solo se usan días cerrados: el patrón aparece al
/// abrir el día siguiente a K1; la ruptura, al cierre de una vela.
/// </summary>
[DisplayName("VP definitivo · Doble vela interior diaria")]
[Category(Vp.Category)]
[Description("Vela madre diaria + dos velas interiores seguidas: rango de acumulación con marca y alerta cuando una vela cierra fuera (ruptura) o vence.")]
public class VpDoubleInside : VpIndicator
{
    public enum Containment
    {
        [Display(Name = "Cuerpo dentro del cuerpo")] Body,
        [Display(Name = "Máximo / mínimo dentro del máximo / mínimo")] Range,
    }

    public enum Nesting
    {
        [Display(Name = "Escalonado (K1 dentro de K2)")] Nested,
        [Display(Name = "Las dos dentro de la madre")] BothInMother,
    }

    public enum Edge
    {
        [Display(Name = "Cuerpo de la madre")] Body,
        [Display(Name = "Máximo / mínimo de la madre")] Range,
    }

    public enum Confirm
    {
        [Display(Name = "Cierre de una vela del gráfico")] ChartClose,
        [Display(Name = "Cierre del día")] DailyClose,
    }

    private sealed record Day(int StartBar, decimal Open, decimal High, decimal Low, decimal Close);

    private sealed class Zone
    {
        public int StartBar, EndBar = -1, LabelBar;
        public decimal Top, Bottom, LabelPrice;
        public int State; // 0 vigente, 1 ruptura alcista, -1 bajista, 2 vencida
        public string Info = "";
    }

    private sealed record Mark(int Bar, bool Up, decimal Price);

    private readonly List<Day> _days = new();
    private readonly List<Zone> _zones = new();
    private readonly List<Mark> _marks = new();
    private Zone? _live;
    private int _liveDays;
    private int _dayStart = -1;
    private decimal _o, _h, _l, _c;
    private Containment _wrap = Containment.Body;
    private Nesting _nest = Nesting.Nested;
    private decimal _mult = 2;
    private Edge _edge = Edge.Body;
    private Confirm _confirm = Confirm.ChartClose;
    private int _expiry = 10;
    private DateTime _lastAlerted = DateTime.MinValue;

    public VpDoubleInside()
    {
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    [Display(Name = "Contención", GroupName = "Patrón", Order = 10)]
    public Containment Wrap { get => _wrap; set { _wrap = value; RecalculateValues(); } }

    [Display(Name = "Anidado", GroupName = "Patrón", Order = 20)]
    public Nesting Nest { get => _nest; set { _nest = value; RecalculateValues(); } }

    [Display(Name = "Cuerpo de la madre ≥ × el de cada interior (0 = no exigir)", GroupName = "Patrón", Order = 30)]
    [Range(0, 20)]
    public decimal MotherMultiple { get => _mult; set { _mult = Math.Max(0, value); RecalculateValues(); } }

    [Display(Name = "Bordes del rango", GroupName = "Patrón", Order = 40)]
    public Edge Edges { get => _edge; set { _edge = value; RecalculateValues(); } }

    [Display(Name = "Confirmar la ruptura con", GroupName = "Patrón", Order = 50)]
    public Confirm Breakout { get => _confirm; set { _confirm = value; RecalculateValues(); } }

    [Display(Name = "Días de vigencia (0 = sin vencimiento)", GroupName = "Patrón", Order = 60)]
    [Range(0, 100)]
    public int ExpiryDays { get => _expiry; set { _expiry = Math.Clamp(value, 0, 100); RecalculateValues(); } }

    [Display(Name = "Conservar los últimos N rangos", GroupName = "Visualización", Order = 70)]
    [Range(1, 30)]
    public int Keep { get; set; } = 5;

    [Display(Name = "Rectángulo del rango", GroupName = "Visualización", Order = 80)]
    public bool ShowBox { get; set; } = true;

    [Display(Name = "Etiqueta del patrón", GroupName = "Visualización", Order = 90)]
    public bool ShowLabel { get; set; } = true;

    [Display(Name = "Marca de ruptura", GroupName = "Visualización", Order = 100)]
    public bool ShowBreak { get; set; } = true;

    [Display(Name = "Color del rango", GroupName = "Visualización", Order = 110)]
    public Color ZoneColor { get; set; } = Color.FromArgb(255, 255, 152, 0);

    [Display(Name = "Color de ruptura alcista", GroupName = "Visualización", Order = 120)]
    public Color UpColor { get; set; } = Color.FromArgb(255, 0, 230, 118);

    [Display(Name = "Color de ruptura bajista", GroupName = "Visualización", Order = 130)]
    public Color DownColor { get; set; } = Color.FromArgb(255, 242, 54, 69);

    [Display(Name = "Alerta: se formó el patrón", GroupName = "Alertas", Order = 140)]
    public bool AlertPattern { get; set; }

    [Display(Name = "Alerta: ruptura", GroupName = "Alertas", Order = 150)]
    public bool AlertBreak { get; set; }

    protected override void Reset()
    {
        _days.Clear();
        _zones.Clear();
        _marks.Clear();
        _live = null;
        _liveDays = 0;
        _dayStart = -1;
    }

    private decimal Top(Day d) => _wrap == Containment.Body ? Math.Max(d.Open, d.Close) : d.High;
    private decimal Bottom(Day d) => _wrap == Containment.Body ? Math.Min(d.Open, d.Close) : d.Low;

    private bool Fresh(int bar) => DateTime.UtcNow - Vp.Utc(GetCandle(bar).LastTime) < TimeSpan.FromMinutes(2) + BarLength && GetCandle(bar).Time > _lastAlerted;

    private void Alert(int bar, string message)
    {
        if (!Fresh(bar)) return;
        _lastAlerted = GetCandle(bar).Time;
        AddAlert("Alert1", message);
    }

    private void Break(int bar, int dir, decimal price)
    {
        var z = _live!;
        z.State = dir;
        z.EndBar = bar;
        _live = null;
        _marks.Add(new Mark(bar, dir == 1, price));
        while (_marks.Count > 90) _marks.RemoveAt(0);
        if (AlertBreak)
            Alert(bar, string.Format(UiText.Tr(dir == 1 ? "Doble vela interior: ruptura alcista en {0} ({1})" : "Doble vela interior: ruptura bajista en {0} ({1})"),
                InstrumentInfo?.Instrument, price));
    }

    protected override void Commit(int bar)
    {
        if (BarLength <= TimeSpan.Zero || BarLength > TimeSpan.FromDays(1)) return;
        var c = GetCandle(bar);
        bool newDay = _dayStart < 0 || NewPeriod(bar, VpPeriod.Day);
        if (newDay)
        {
            if (_dayStart >= 0)
            {
                _days.Add(new Day(_dayStart, _o, _h, _l, _c));
                if (_days.Count > 10) _days.RemoveAt(0);
                OnNewDay(bar);
            }

            _dayStart = bar;
            _o = c.Open;
            _h = c.High;
            _l = c.Low;
        }
        else
        {
            _h = Math.Max(_h, c.High);
            _l = Math.Min(_l, c.Low);
        }

        _c = c.Close;

        // ③ ruptura al cierre de una vela del gráfico
        if (_live != null && _confirm == Confirm.ChartClose)
        {
            int d = c.Close > _live.Top ? 1 : c.Close < _live.Bottom ? -1 : 0;
            if (d != 0) Break(bar, d, c.Close);
        }

        if (_live != null) _live.EndBar = bar;
    }

    /// <summary>Empieza un día: el anterior (K1) ya cerró.</summary>
    private void OnNewDay(int bar)
    {
        if (_days.Count < 1) return;
        var k1 = _days[^1];

        // ① ruptura con el cierre del día, o un día más del rango (y vencimiento)
        if (_live != null)
        {
            int d = _confirm == Confirm.DailyClose ? (k1.Close > _live.Top ? 1 : k1.Close < _live.Bottom ? -1 : 0) : 0;
            if (d != 0) Break(bar, d, k1.Close);
            else
            {
                _liveDays++;
                if (_expiry > 0 && _liveDays >= _expiry)
                {
                    _live.State = 2;
                    _live.EndBar = bar;
                    _live = null;
                }
            }
        }

        // ② patrón nuevo con los tres últimos días cerrados
        if (_days.Count < 3) return;
        var k2 = _days[^2];
        var k3 = _days[^3];
        decimal t3 = Top(k3), b3 = Bottom(k3), t2 = Top(k2), b2 = Bottom(k2), t1 = Top(k1), b1 = Bottom(k1);
        decimal body1 = Math.Abs(k1.Close - k1.Open), body2 = Math.Abs(k2.Close - k2.Open), body3 = Math.Abs(k3.Close - k3.Open);
        bool in2 = t2 <= t3 && b2 >= b3;
        bool in1 = _nest == Nesting.Nested ? t1 <= t2 && b1 >= b2 : t1 <= t3 && b1 >= b3;
        bool strong = _mult <= 0 || (body3 >= _mult * body2 && body3 >= _mult * body1);
        if (!(t3 > b3 && in2 && in1 && strong)) return;

        if (_live != null) _live.EndBar = bar;
        var z = new Zone
        {
            StartBar = k3.StartBar,
            EndBar = bar,
            Top = _edge == Edge.Body ? Math.Max(k3.Open, k3.Close) : k3.High,
            Bottom = _edge == Edge.Body ? Math.Min(k3.Open, k3.Close) : k3.Low,
            LabelBar = k1.StartBar,
            LabelPrice = Math.Max(k1.High, Math.Max(k2.High, k3.High)),
        };
        z.Info = string.Format(UiText.Tr("Madre / interiores: {0} / {1} veces"),
            body2 > 0 ? (body3 / body2).ToString("0.#") : "∞", body1 > 0 ? (body3 / body1).ToString("0.#") : "∞");
        if (_live != null && _live.State == 0) _live.State = 2;
        _live = z;
        _liveDays = 0;
        _zones.Add(z);
        while (_zones.Count > 30) _zones.RemoveAt(0);
        if (AlertPattern)
            Alert(bar, string.Format(UiText.Tr("Doble vela interior en {0}: rango {1} ~ {2}"), InstrumentInfo?.Instrument, z.Bottom, z.Top));
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        int last = Math.Max(0, CurrentBar - 1);
        for (int i = Math.Max(0, _zones.Count - Keep); i < _zones.Count; i++)
        {
            var z = _zones[i];
            var color = z.State switch { 1 => UpColor, -1 => DownColor, 2 => Color.Gray, _ => ZoneColor };
            int x0 = X(z.StartBar), x1 = X(z.State == 0 ? last : z.EndBar, false);
            int yTop = Y(z.Top), yBot = Y(z.Bottom);
            if (ShowBox)
            {
                var rect = new Rectangle(x0, yTop, Math.Max(1, x1 - x0), Math.Max(1, yBot - yTop));
                context.FillRectangle(Vp.Alpha(color, z.State == 2 ? 15 : 30), rect);
                context.DrawRectangle(Vp.Pen(color, 1), rect);
            }

            if (ShowLabel)
            {
                string text = UiText.Tr("Doble vela interior");
                var size = context.MeasureString(text, SmallFont);
                int lx = X(z.LabelBar), ly = Y(z.LabelPrice) - size.Height - 8;
                context.FillRectangle(Vp.Alpha(ZoneColor, 204), new Rectangle(lx, ly, size.Width + 8, size.Height + 4), 3);
                context.DrawString(text, SmallFont, Color.White, lx + 4, ly + 2);
            }
        }

        if (!ShowBreak) return;
        int keepMarks = Keep * 3;
        for (int i = Math.Max(0, _marks.Count - keepMarks); i < _marks.Count; i++)
        {
            var m = _marks[i];
            var c = GetCandle(Math.Min(m.Bar, last));
            string text = UiText.Tr(m.Up ? "Doble interior ↑" : "Doble interior ↓");
            var size = context.MeasureString(text, SmallFont);
            int x = X(m.Bar) - size.Width / 2;
            int y = m.Up ? Y(c.Low) + 6 : Y(c.High) - size.Height - 10;
            var color = m.Up ? UpColor : DownColor;
            context.FillRectangle(Vp.Alpha(color, 230), new Rectangle(x - 4, y, size.Width + 8, size.Height + 4), 3);
            context.DrawString(text, SmallFont, Color.Black, x, y + 2);
        }
    }
}
