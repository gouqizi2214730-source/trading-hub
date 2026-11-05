using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.Charting;

namespace TradingHub.App.Windows;

/// <summary>
/// Crear una alerta como en TradingView: «precio / línea de un indicador» + condición (cruza, cruza hacia arriba / abajo,
/// mayor / menor, entra / sale del canal…) + «valor fijo / otra línea», con frecuencia, vencimiento y mensaje. Abajo, las
/// alertas del gráfico (se pausan o se borran).
/// </summary>
internal sealed class AlertDialog : TradingHubDialog<bool>
{
    private readonly ChartModel _model;
    private readonly List<AlertOperand> _sources;
    private readonly ComboBox _left = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _cond = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _right = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _rightValue = new() { FormatString = "0.########", Increment = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _lower = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _lowerValue = new() { FormatString = "0.########", Increment = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _freq = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _expiry = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _message = new() { Watermark = "Mensaje (vacío = la condición)" };
    private readonly NumericUpDown _bars = new() { Minimum = 1, Maximum = 1000, Value = 1, Increment = 1, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Control _barsRow;
    private readonly Control _rightRow;
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly Control _lowerRow;
    private bool _changed;

    private AlertDialog(ChartModel model, AlertOperand? left, AlertOperand? right, AlertOperand? lower, AlertCondition? condition) : base("Alertas", 540)
    {
        _model = model;
        // precio (cierre / máximo / mínimo) y cada línea de los indicadores del gráfico
        _sources = new List<AlertOperand>
        {
            AlertOperand.Price("close", "Precio (cierre)"),
            AlertOperand.Price("high", "Máximo de la vela"),
            AlertOperand.Price("low", "Mínimo de la vela"),
        };
        _sources.AddRange(ChartAlertEngine.Lines(model));
        // las líneas dibujadas: líneas, rayos, rectángulos, canales y niveles de Fibonacci
        _sources.AddRange(ChartAlertEngine.DrawingLines(model));
        Fill(_left, false);
        Fill(_right, true);
        Fill(_lower, true);
        foreach (AlertCondition c in Enum.GetValues<AlertCondition>()) _cond.Items.Add(new ComboBoxItem { Content = PropertyGrid.EnumDisplay(c), Tag = c });
        foreach (AlertFrequency f in Enum.GetValues<AlertFrequency>()) _freq.Items.Add(new ComboBoxItem { Content = PropertyGrid.EnumDisplay(f), Tag = f });
        foreach (var (text, days) in new[] { ("Nunca vence", 0), ("1 día", 1), ("1 semana", 7), ("1 mes", 30), ("3 meses", 90) })
            _expiry.Items.Add(new ComboBoxItem { Content = text, Tag = days });
        _cond.SelectedIndex = condition is { } cnd ? Array.IndexOf(Enum.GetValues<AlertCondition>(), cnd) : 0;
        _freq.SelectedIndex = 1;
        _expiry.SelectedIndex = 0;

        decimal last = model.Data.LastPrice;
        Select(_left, left ?? _sources[0]);
        if (right != null) Select(_right, right);
        else
        {
            _right.SelectedIndex = 0;
            _rightValue.Value = right?.Value ?? last;
        }

        if (lower != null) Select(_lower, lower);
        else _lower.SelectedIndex = 0;
        _lowerValue.Value = last;
        decimal step = model.Instrument.TickSize > 0 ? model.Instrument.TickSize : 1;
        _rightValue.Increment = step;
        _lowerValue.Increment = step;

        _right.SelectionChanged += (_, _) =>
        {
            PairChannel();
            UpdateVisibility();
        };
        _lower.SelectionChanged += (_, _) => UpdateVisibility();
        _cond.SelectionChanged += (_, _) =>
        {
            PairChannel();
            UpdateVisibility();
        };

        _lowerRow = Ui.Field("Borde de abajo del canal", Ui.Row(8, _lower, _lowerValue));
        _barsRow = Ui.Field("En cuántas velas", _bars, "1 = desde la vela anterior. El valor de «Con» es el porcentaje.");
        _rightRow = Ui.Field("Con", Ui.Row(8, _right, _rightValue));
        var create = new Button { Content = "Crear alerta", IsDefault = true }.Classes("primary");
        create.Click += (_, _) => Create();
        var close = new Button { Content = "Listo", IsCancel = true };
        close.Click += (_, _) => Close(_changed);

        var form = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Crear alerta", Classes = { "h2" } },
                Ui.Field("Condición", _left),
                _cond,
                _rightRow,
                _lowerRow,
                _barsRow,
                Ui.Row(8, Ui.Field("Frecuencia", _freq), Ui.Field("Vence", _expiry)),
                Ui.Field("Mensaje", _message, "Se muestra en la app y sale por webhook / Telegram si están activados (Ajustes → Alertas)."),
                new Separator { Margin = new Thickness(0, 6) },
                new TextBlock { Text = "Alertas de este gráfico", Classes = { "h3" } },
                new ScrollViewer { Content = _list, MaxHeight = 220 },
            },
        };
        Content = Shell(form, close, create);
        FillList();
        UpdateVisibility();
    }

    private void Fill(ComboBox box, bool withValue)
    {
        if (withValue) box.Items.Add(new ComboBoxItem { Content = "Valor fijo", Tag = null });
        // en «Con» y en el borde de abajo, las líneas dibujadas primero (es lo que más se alerta), después precio e indicadores
        if (withValue)
        {
            foreach (var s in _sources.Where(o => o.Drawing != null)) box.Items.Add(new ComboBoxItem { Content = "✎ " + s.Label, Tag = s });
            foreach (var s in _sources.Where(o => o.Drawing == null)) box.Items.Add(new ComboBoxItem { Content = s.Label, Tag = s });
            return;
        }

        foreach (var s in _sources) box.Items.Add(new ComboBoxItem { Content = s.Label, Tag = s });
    }

    private static void Select(ComboBox box, AlertOperand op)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is AlertOperand o && o.Indicator == op.Indicator && o.Series == op.Series && o.Drawing == op.Drawing)
            {
                box.SelectedItem = item;
                return;
            }
        }

        // una línea capturada con el clic derecho que no está en la lista (ya terminó a la izquierda, o se repite con un
        // nivel): se agrega para poder elegirla igual
        if (op.Line != null)
        {
            var extra = new ComboBoxItem { Content = op.Label, Tag = op.Copy() };
            box.Items.Add(extra);
            box.SelectedItem = extra;
            return;
        }

        box.SelectedIndex = 0;
    }

    private void UpdateVisibility()
    {
        _rightValue.IsVisible = (_right.SelectedItem as ComboBoxItem)?.Tag == null;
        _lowerValue.IsVisible = (_lower.SelectedItem as ComboBoxItem)?.Tag == null;
        var cond = (_cond.SelectedItem as ComboBoxItem)?.Tag is AlertCondition c ? c : AlertCondition.Crossing;
        _lowerRow.IsVisible = ChartAlert.IsChannel(cond);
        _barsRow.IsVisible = ChartAlert.IsPercent(cond);
        if (ChartAlert.IsPercent(cond))
        {
            // el porcentaje es un valor fijo
            if ((_right.SelectedItem as ComboBoxItem)?.Tag != null) _right.SelectedIndex = 0;
            _rightValue.IsVisible = true;
        }
    }

    /// <summary>
    /// En una condición de canal con el borde de arriba de un dibujo (canal o rectángulo), el de abajo es el otro borde del
    /// mismo dibujo: se elige solo.
    /// </summary>
    private void PairChannel()
    {
        if ((_cond.SelectedItem as ComboBoxItem)?.Tag is not AlertCondition c || !ChartAlert.IsChannel(c)) return;
        if ((_right.SelectedItem as ComboBoxItem)?.Tag is not AlertOperand { Drawing: { } id } r) return;
        string other = r.Series switch { "upper" => "lower", "top" => "bottom", "lower" => "upper", "bottom" => "top", _ => "" };
        if (other.Length == 0) return;
        var op = _sources.FirstOrDefault(o => o.Drawing == id && o.Series == other);
        if (op != null) Select(_lower, op);
    }

    private static AlertOperand Operand(ComboBox box, NumericUpDown value) =>
        (box.SelectedItem as ComboBoxItem)?.Tag is AlertOperand o
            ? o.Copy()
            : AlertOperand.Fixed(value.Value ?? 0);

    private void Create()
    {
        var a = new ChartAlert
        {
            Left = (_left.SelectedItem as ComboBoxItem)?.Tag is AlertOperand l ? l.Copy() : AlertOperand.Price(),
            Condition = (_cond.SelectedItem as ComboBoxItem)?.Tag is AlertCondition c ? c : AlertCondition.Crossing,
            Right = Operand(_right, _rightValue),
            Frequency = (_freq.SelectedItem as ComboBoxItem)?.Tag is AlertFrequency f ? f : AlertFrequency.OncePerBar,
            Message = (_message.Text ?? "").Trim(),
            Bars = (int)(_bars.Value ?? 1),
        };
        if (a.Right.IsFixed) a.Right.Label = ChartAlert.IsPercent(a.Condition) ? (a.Right.Value ?? 0).ToString("0.##", CultureInfo.CurrentCulture) : _model.Instrument.FormatPrice(a.Right.Value ?? 0);
        if (ChartAlert.IsChannel(a.Condition))
        {
            a.Lower = Operand(_lower, _lowerValue);
            if (a.Lower.IsFixed) a.Lower.Label = _model.Instrument.FormatPrice(a.Lower.Value ?? 0);
        }

        if ((_expiry.SelectedItem as ComboBoxItem)?.Tag is int days && days > 0) a.ExpiresUtc = DateTime.UtcNow.AddDays(days);
        _model.Alerts.Add(a);
        _changed = true;
        _message.Text = "";
        FillList();
    }

    private void FillList()
    {
        _list.Children.Clear();
        if (_model.Alerts.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "Todavía no hay alertas en este gráfico.", Classes = { "faint", "small" } });
            return;
        }

        foreach (var a in _model.Alerts.ToList())
        {
            var text = new TextBlock
            {
                Text = (string.IsNullOrWhiteSpace(a.Message) ? a.Describe() : a.Message + " · " + a.Describe()) + " · " + PropertyGrid.EnumDisplay(a.Frequency).ToLower(CultureInfo.CurrentCulture)
                    + (a.ExpiresUtc is { } e ? $" · vence {e.ToLocalTime():dd/MM HH:mm}" : ""),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = a.Active ? 1 : 0.5,
            };
            var toggle = new ToggleSwitch { IsChecked = a.Active, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
            toggle.IsCheckedChanged += (_, _) =>
            {
                a.Active = toggle.IsChecked == true;
                text.Opacity = a.Active ? 1 : 0.5;
                _changed = true;
            };
            var del = Ui.Button("", MaterialIconKind.DeleteOutline, () =>
            {
                _model.Alerts.Remove(a);
                _changed = true;
                FillList();
            }, "ghost");
            var row = new DockPanel();
            DockPanel.SetDock(del, Dock.Right);
            DockPanel.SetDock(toggle, Dock.Left);
            row.Children.Add(del);
            row.Children.Add(toggle);
            row.Children.Add(text);
            _list.Children.Add(new Border { Child = row, Padding = new Thickness(6, 3), CornerRadius = new CornerRadius(6), Background = Ui.Bg3 });
        }
    }

    /// <summary>Abre el diálogo. Devuelve true si cambió alguna alerta (para guardar el espacio de trabajo).</summary>
    public static Task<bool> ShowAsync(Window owner, ChartModel model, AlertOperand? left = null, AlertOperand? right = null,
        AlertOperand? lower = null, AlertCondition? condition = null) =>
        new AlertDialog(model, left, right, lower, condition).ShowDialog<bool>(owner);
}
