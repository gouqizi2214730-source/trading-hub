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
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly Control _lowerRow;
    private bool _changed;

    private AlertDialog(ChartModel model, AlertOperand? left, AlertOperand? right) : base("Alertas", 520)
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
        Fill(_left, false);
        Fill(_right, true);
        Fill(_lower, true);
        foreach (AlertCondition c in Enum.GetValues<AlertCondition>()) _cond.Items.Add(new ComboBoxItem { Content = PropertyGrid.EnumDisplay(c), Tag = c });
        foreach (AlertFrequency f in Enum.GetValues<AlertFrequency>()) _freq.Items.Add(new ComboBoxItem { Content = PropertyGrid.EnumDisplay(f), Tag = f });
        foreach (var (text, days) in new[] { ("Nunca vence", 0), ("1 día", 1), ("1 semana", 7), ("1 mes", 30), ("3 meses", 90) })
            _expiry.Items.Add(new ComboBoxItem { Content = text, Tag = days });
        _cond.SelectedIndex = 0;
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

        _lower.SelectedIndex = 0;
        _lowerValue.Value = last;
        decimal step = model.Instrument.TickSize > 0 ? model.Instrument.TickSize : 1;
        _rightValue.Increment = step;
        _lowerValue.Increment = step;

        _right.SelectionChanged += (_, _) => UpdateVisibility();
        _lower.SelectionChanged += (_, _) => UpdateVisibility();
        _cond.SelectionChanged += (_, _) => UpdateVisibility();

        _lowerRow = Ui.Field("Borde de abajo del canal", Ui.Row(8, _lower, _lowerValue));
        var create = new Button { Content = "Crear alerta", IsDefault = true }.Classes("primary");
        create.Click += (_, _) => Create();
        var close = new Button { Content = "Cerrar", IsCancel = true };
        close.Click += (_, _) => Close(_changed);

        var form = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Crear alerta", Classes = { "h2" } },
                Ui.Field("Condición", _left),
                _cond,
                Ui.Field("Con", Ui.Row(8, _right, _rightValue)),
                _lowerRow,
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
        foreach (var s in _sources) box.Items.Add(new ComboBoxItem { Content = s.Label, Tag = s });
    }

    private static void Select(ComboBox box, AlertOperand op)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is AlertOperand o && o.Indicator == op.Indicator && o.Series == op.Series)
            {
                box.SelectedItem = item;
                return;
            }
        }

        box.SelectedIndex = 0;
    }

    private void UpdateVisibility()
    {
        _rightValue.IsVisible = (_right.SelectedItem as ComboBoxItem)?.Tag == null;
        _lowerValue.IsVisible = (_lower.SelectedItem as ComboBoxItem)?.Tag == null;
        bool channel = (_cond.SelectedItem as ComboBoxItem)?.Tag is AlertCondition c && ChartAlert.IsChannel(c);
        _lowerRow.IsVisible = channel;
    }

    private static AlertOperand Operand(ComboBox box, NumericUpDown value) =>
        (box.SelectedItem as ComboBoxItem)?.Tag is AlertOperand o
            ? new AlertOperand { Indicator = o.Indicator, Series = o.Series, Label = o.Label }
            : AlertOperand.Fixed(value.Value ?? 0);

    private void Create()
    {
        var a = new ChartAlert
        {
            Left = (_left.SelectedItem as ComboBoxItem)?.Tag is AlertOperand l ? new AlertOperand { Indicator = l.Indicator, Series = l.Series, Label = l.Label } : AlertOperand.Price(),
            Condition = (_cond.SelectedItem as ComboBoxItem)?.Tag is AlertCondition c ? c : AlertCondition.Crossing,
            Right = Operand(_right, _rightValue),
            Frequency = (_freq.SelectedItem as ComboBoxItem)?.Tag is AlertFrequency f ? f : AlertFrequency.OncePerBar,
            Message = (_message.Text ?? "").Trim(),
        };
        if (a.Right.IsFixed) a.Right.Label = _model.Instrument.FormatPrice(a.Right.Value ?? 0);
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
    public static Task<bool> ShowAsync(Window owner, ChartModel model, AlertOperand? left = null, AlertOperand? right = null) =>
        new AlertDialog(model, left, right).ShowDialog<bool>(owner);
}
