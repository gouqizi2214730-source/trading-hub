using System.ComponentModel.DataAnnotations;
using TradingHub.Indicators;

namespace TradingHub.Charting;

/// <summary>Condición de una alerta (como en TradingView).</summary>
public enum AlertCondition
{
    [Display(Name = "Cruza")] Crossing,
    [Display(Name = "Cruza hacia arriba")] CrossingUp,
    [Display(Name = "Cruza hacia abajo")] CrossingDown,
    [Display(Name = "Toca")] Touching,
    [Display(Name = "Mayor que")] GreaterThan,
    [Display(Name = "Menor que")] LessThan,
    [Display(Name = "Entra en el canal")] EnteringChannel,
    [Display(Name = "Sale del canal")] ExitingChannel,
    [Display(Name = "Dentro del canal")] InsideChannel,
    [Display(Name = "Fuera del canal")] OutsideChannel,
    [Display(Name = "Sube más de un % (en N velas)")] MovingUpPercent,
    [Display(Name = "Baja más de un % (en N velas)")] MovingDownPercent,
}

/// <summary>Cada cuánto puede dispararse.</summary>
public enum AlertFrequency
{
    [Display(Name = "Una sola vez")] Once,
    [Display(Name = "Una vez por vela")] OncePerBar,
    [Display(Name = "Una vez por vela, al cierre")] OncePerBarClose,
    [Display(Name = "Cada vez que se cumple")] EveryTime,
}

/// <summary>
/// Un lado de la condición: el precio de la vela (cierre, máximo, mínimo, apertura), una línea de un indicador del gráfico
/// (por ejemplo, el VWAP) o un valor fijo.
/// </summary>
public sealed class AlertOperand
{
    /// <summary>Indicador del gráfico: «tipo completo#n» (el n-ésimo de ese tipo). Null = el precio o un valor fijo.</summary>
    public string? Indicator { get; set; }

    /// <summary>Línea del indicador (Id de su serie), o del precio: close, high, low, open. Null con <see cref="Value"/> = valor fijo.</summary>
    public string? Series { get; set; }

    public decimal? Value { get; set; }

    /// <summary>Dibujo del gráfico (su Id): una línea, un rayo, un rectángulo o un canal; <see cref="Series"/> dice cuál de sus líneas.</summary>
    public string? Drawing { get; set; }

    /// <summary>Cómo se muestra (nombre del indicador y de la línea).</summary>
    public string Label { get; set; } = "";

    public static AlertOperand Price(string series = "close", string label = "Precio") => new() { Series = series, Label = label };

    public static AlertOperand Fixed(decimal value) => new() { Value = value, Label = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    public bool IsFixed => Indicator == null && Series == null && Drawing == null;

    /// <summary>El precio de la vela (cierre, máximo…), no una línea ni un valor.</summary>
    public bool IsPrice => Indicator == null && Drawing == null && Series != null;

    public AlertOperand Copy() => new() { Indicator = Indicator, Series = Series, Value = Value, Drawing = Drawing, Label = Label };
}

/// <summary>Alerta de un gráfico sobre el precio o las líneas de sus indicadores.</summary>
public sealed class ChartAlert
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public AlertOperand Left { get; set; } = AlertOperand.Price();
    public AlertCondition Condition { get; set; } = AlertCondition.Crossing;
    public AlertOperand Right { get; set; } = new();

    /// <summary>Borde de abajo del canal (para las condiciones de canal, <see cref="Right"/> es el de arriba).</summary>
    public AlertOperand? Lower { get; set; }

    public AlertFrequency Frequency { get; set; } = AlertFrequency.OncePerBar;

    /// <summary>«Sube / baja más de un %»: en cuántas velas (1 = desde la anterior).</summary>
    public int Bars { get; set; } = 1;
    public string Message { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime? ExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // estado de la evaluación (no se guarda)
    internal long FiredBar = long.MinValue;
    internal bool? LastState;
    internal long ClosedBar = long.MinValue;

    public static bool IsChannel(AlertCondition c) => c is AlertCondition.EnteringChannel or AlertCondition.ExitingChannel or AlertCondition.InsideChannel or AlertCondition.OutsideChannel;

    public static bool IsPercent(AlertCondition c) => c is AlertCondition.MovingUpPercent or AlertCondition.MovingDownPercent;

    /// <summary>Si la alerta usa un dibujo (para borrarla con él).</summary>
    public bool Uses(string drawingId) => Left.Drawing == drawingId || Right.Drawing == drawingId || Lower?.Drawing == drawingId;

    public string Describe()
    {
        string cond = Condition switch
        {
            AlertCondition.Crossing => "cruza",
            AlertCondition.CrossingUp => "cruza hacia arriba",
            AlertCondition.CrossingDown => "cruza hacia abajo",
            AlertCondition.Touching => "toca",
            AlertCondition.MovingUpPercent => "sube más de",
            AlertCondition.MovingDownPercent => "baja más de",
            AlertCondition.GreaterThan => "mayor que",
            AlertCondition.LessThan => "menor que",
            AlertCondition.EnteringChannel => "entra en el canal",
            AlertCondition.ExitingChannel => "sale del canal",
            AlertCondition.InsideChannel => "dentro del canal",
            _ => "fuera del canal",
        };
        if (IsPercent(Condition)) return $"{Left.Label} {cond} {Right.Label} % en {Math.Max(1, Bars)} vela{(Bars > 1 ? "s" : "")}";
        return IsChannel(Condition) ? $"{Left.Label} {cond} {Lower?.Label} – {Right.Label}" : $"{Left.Label} {cond} {Right.Label}";
    }
}

/// <summary>Evaluación de las alertas de un gráfico (en el hilo de la interfaz, con cada actualización).</summary>
public static class ChartAlertEngine
{
    /// <summary>Clave de un indicador del gráfico (tipo y cuál de los de ese tipo es).</summary>
    public static string KeyOf(ChartModel model, IndicatorSlot slot)
    {
        var t = slot.Indicator.GetType().FullName!;
        int n = 0;
        foreach (var s in model.Indicators)
        {
            if (ReferenceEquals(s, slot)) break;
            if (s.Indicator.GetType().FullName == t) n++;
        }

        return t + "#" + n;
    }

    public static IndicatorSlot? SlotOf(ChartModel model, string key)
    {
        int hash = key.LastIndexOf('#');
        string t = hash > 0 ? key[..hash] : key;
        int n = hash > 0 && int.TryParse(key[(hash + 1)..], out var k) ? k : 0;
        foreach (var s in model.Indicators)
        {
            var name = s.Indicator.GetType().FullName;
            // un indicador editado reemplaza al original con otro tipo: cuenta como el mismo
            var replaced = s.Indicator.GetType().GetCustomAttributes(typeof(ReplacesIndicatorAttribute), false).OfType<ReplacesIndicatorAttribute>().FirstOrDefault()?.FullName;
            if (name != t && replaced != t) continue;
            if (n-- == 0) return s;
        }

        return null;
    }

    /// <summary>Las líneas de los indicadores del gráfico que se pueden alertar: (operando, nombre).</summary>
    public static List<AlertOperand> Lines(ChartModel model)
    {
        var res = new List<AlertOperand>();
        foreach (var slot in model.Indicators)
        {
            string key = KeyOf(model, slot);
            foreach (var s in slot.Indicator.DataSeries)
            {
                if (s is not ValueDataSeries v || v.IsHidden || v.VisualType == VisualMode.Hide) continue;
                res.Add(new AlertOperand { Indicator = key, Series = s.Id, Label = $"{slot.Indicator.Name} · {s.Name}" });
            }
        }

        return res;
    }

    /// <summary>Las líneas de los dibujos del gráfico que se pueden alertar (líneas, rayos, rectángulos, canales, Fibonacci).</summary>
    public static List<AlertOperand> DrawingLines(ChartModel model)
    {
        var res = new List<AlertOperand>();
        var count = new Dictionary<Drawings.DrawingKind, int>();
        foreach (var d in model.Drawings)
        {
            var parts = d.AlertParts();
            if (parts.Count == 0) continue;
            int n = count[d.Kind] = (count.TryGetValue(d.Kind, out var c) ? c : 0) + 1;
            string name = (d.Text is { Length: > 0 } t && t != "alerta" ? t : Drawings.ChartDrawing.KindName(d.Kind) + (n > 1 ? " " + n : ""));
            foreach (var (part, partName) in parts)
                res.Add(new AlertOperand { Drawing = d.Id.ToString("N"), Series = part, Label = parts.Count > 1 ? $"{name} · {partName}" : name });
        }

        return res;
    }

    /// <summary>Las líneas de un dibujo como operandos (arriba primero en los canales).</summary>
    public static List<AlertOperand> DrawingLines(ChartModel model, Drawings.ChartDrawing d) =>
        DrawingLines(model).Where(o => o.Drawing == d.Id.ToString("N")).ToList();

    /// <summary>La línea de indicador más cercana al punto (x, y) del gráfico, a menos de <paramref name="tolerance"/> píxeles.</summary>
    public static AlertOperand? LineAt(ChartModel model, float x, float y, float tolerance = 7)
    {
        var layout = model.LastLayout;
        var panel = layout?.PanelAt(y);
        if (layout == null || panel == null) return null;
        int bar = Math.Clamp(layout.BarAt(x), 0, Math.Max(0, model.Series.Count - 1));
        AlertOperand? best = null;
        float bestD = tolerance;
        foreach (var slot in model.Indicators)
        {
            if (!slot.Visible) continue;
            bool here = panel.IsPrice ? slot.Panel == null : ReferenceEquals(slot.Panel, panel.SubPanel);
            if (!here) continue;
            string key = KeyOf(model, slot);
            foreach (var s in slot.Indicator.DataSeries)
            {
                if (s is not ValueDataSeries v || v.IsHidden || v.VisualType == VisualMode.Hide) continue;
                decimal val = v[bar];
                if (val == 0 && !v.ShowZeroValue) continue;
                float d = Math.Abs(panel.Y(val) - y);
                if (d > bestD) continue;
                bestD = d;
                best = new AlertOperand { Indicator = key, Series = s.Id, Label = $"{slot.Indicator.Name} · {s.Name}" };
            }
        }

        return best;
    }

    public static bool TryValue(ChartModel model, AlertOperand op, int bar, out decimal value)
    {
        value = 0;
        if (op.IsFixed)
        {
            value = op.Value ?? 0;
            return op.Value != null;
        }

        if (bar < 0 || bar >= model.Series.Count) return false;
        if (op.Drawing != null)
        {
            var d = model.Drawings.FirstOrDefault(x => x.Id.ToString("N") == op.Drawing);
            if (d?.ValueAt(model.Series, bar, op.Series ?? "line") is not { } v || double.IsNaN(v) || double.IsInfinity(v)) return false;
            value = (decimal)Math.Clamp(v, -7.9e27, 7.9e27);
            return true;
        }

        if (op.Indicator == null)
        {
            var c = model.Series[bar];
            value = model.Instrument.FromTicks(op.Series switch
            {
                "high" => c.HighTicks,
                "low" => c.LowTicks,
                "open" => c.OpenTicks,
                _ => c.CloseTicks,
            });
            return true;
        }

        var slot = SlotOf(model, op.Indicator);
        if (slot?.Indicator.DataSeries.FindById(op.Series ?? "") is not ValueDataSeries s) return false;
        value = s[bar];
        return value != 0 || s.ShowZeroValue;
    }

    /// <summary>La condición en la vela <paramref name="bar"/> (null = falta algún valor).</summary>
    public static bool? Check(ChartModel model, ChartAlert a, int bar)
    {
        if (ChartAlert.IsPercent(a.Condition))
        {
            int back = Math.Max(1, a.Bars);
            if (!TryValue(model, a.Left, bar, out var now) || !TryValue(model, a.Left, bar - back, out var then) || then == 0) return null;
            decimal pct = a.Right.Value ?? 0;
            decimal change = (now - then) / Math.Abs(then) * 100m;
            return a.Condition == AlertCondition.MovingUpPercent ? change >= pct : change <= -pct;
        }

        if (!TryValue(model, a.Left, bar, out var x0) || !TryValue(model, a.Right, bar, out var r0)) return null;
        bool has1 = TryValue(model, a.Left, bar - 1, out var x1) & TryValue(model, a.Right, bar - 1, out var r1);
        decimal l0 = 0, l1 = 0;
        if (ChartAlert.IsChannel(a.Condition))
        {
            if (a.Lower == null || !TryValue(model, a.Lower, bar, out l0)) return null;
            has1 &= TryValue(model, a.Lower, bar - 1, out l1);
            if (l0 > r0) (l0, r0) = (r0, l0);
            if (l1 > r1) (l1, r1) = (r1, l1);
        }

        bool inside0 = x0 >= l0 && x0 <= r0, inside1 = x1 >= l1 && x1 <= r1;
        return a.Condition switch
        {
            AlertCondition.Crossing => has1 && ((x1 <= r1 && x0 > r0) || (x1 >= r1 && x0 < r0)),
            AlertCondition.CrossingUp => has1 && x1 <= r1 && x0 > r0,
            AlertCondition.CrossingDown => has1 && x1 >= r1 && x0 < r0,
            AlertCondition.Touching => Touches(model, a, bar, x0, r0, has1, x1, r1),
            AlertCondition.GreaterThan => x0 > r0,
            AlertCondition.LessThan => x0 < r0,
            AlertCondition.EnteringChannel => has1 && !inside1 && inside0,
            AlertCondition.ExitingChannel => has1 && inside1 && !inside0,
            AlertCondition.InsideChannel => inside0,
            _ => !inside0,
        };
    }

    /// <summary>
    /// «Toca»: con el precio, la vela llega a la línea (el mínimo está debajo y el máximo encima); con una línea de un
    /// indicador, la alcanza o la cruza.
    /// </summary>
    private static bool Touches(ChartModel model, ChartAlert a, int bar, decimal x0, decimal r0, bool has1, decimal x1, decimal r1)
    {
        if (a.Left.IsPrice)
        {
            var c = model.Series[bar];
            decimal hi = model.Instrument.FromTicks(c.HighTicks), lo = model.Instrument.FromTicks(c.LowTicks);
            return lo <= r0 && hi >= r0;
        }

        return x0 == r0 || (has1 && (x1 - r1) * (x0 - r0) <= 0);
    }

    /// <summary>Revisa las alertas activas y devuelve las que se disparan ahora (las «una sola vez» quedan apagadas).</summary>
    public static List<ChartAlert> Evaluate(ChartModel model, DateTime nowUtc)
    {
        var fired = new List<ChartAlert>();
        int n = model.Series.Count;
        if (n < 2) return fired;
        foreach (var a in model.Alerts)
        {
            if (!a.Active) continue;
            if (a.ExpiresUtc is { } exp && nowUtc > exp)
            {
                a.Active = false;
                continue;
            }

            bool onClose = a.Frequency == AlertFrequency.OncePerBarClose;
            int bar = onClose ? n - 2 : n - 1;
            long barKey = model.Series[bar].Time.Ticks;
            if (onClose && a.ClosedBar == barKey) continue; // cada vela cerrada se mira una vez
            var state = Check(model, a, bar);
            if (state == null) continue;
            if (onClose) a.ClosedBar = barKey;
            bool was = a.LastState == true;
            a.LastState = state;
            if (state != true) continue;
            bool fire = a.Frequency switch
            {
                AlertFrequency.Once => true,
                AlertFrequency.OncePerBar => a.FiredBar != barKey,
                AlertFrequency.OncePerBarClose => true,
                _ => !was,
            };
            if (!fire) continue;
            a.FiredBar = barKey;
            if (a.Frequency == AlertFrequency.Once) a.Active = false;
            fired.Add(a);
        }

        return fired;
    }
}
