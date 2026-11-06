using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.Charting.Drawings;

namespace TradingHub.App.Views;

/// <summary>
/// La barrita que aparece al lado de donde se soltó un gesto (como en OpenMarket): qué dibujo salió, por cuáles otros
/// tipos se puede cambiar con el mismo trazo y borrarlo; después de un garabato, cuántos dibujos borró y deshacer. Se va
/// sola a los pocos segundos o con el próximo clic en el gráfico.
/// </summary>
internal sealed class InkChip : Border
{
    private readonly TradingView _host;
    private readonly ChartTab _chart;
    private readonly DispatcherTimer _timer;

    public InkChip(TradingView host, ChartTab chart)
    {
        _host = host;
        _chart = chart;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Padding = new Thickness(4, 3);
        CornerRadius = new CornerRadius(8);
        Background = Ui.Bg2;
        BorderBrush = Ui.Line;
        BorderThickness = new Thickness(1);
        BoxShadow = BoxShadows.Parse("0 4 14 0 #66000000");
        IsVisible = false;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background, (_, _) => Hide());
        // con el mouse encima no se va
        PointerEntered += (_, _) => _timer.Stop();
        PointerExited += (_, _) =>
        {
            if (IsVisible) _timer.Start();
        };
        chart.View.AddHandler(PointerPressedEvent, (_, _) => Hide(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public void Hide()
    {
        _timer.Stop();
        IsVisible = false;
    }

    public void Show(InkResult r)
    {
        Child = r.Erase ? BuildErase(r) : r.Drawing != null ? BuildCreated(r) : null;
        if (Child == null)
        {
            Hide();
            return;
        }

        // al lado de donde se soltó, sin salirse del gráfico (ni taparle el eje de precios)
        var size = _chart.View.Bounds.Size;
        Measure(Size.Infinity);
        double w = DesiredSize.Width, h = DesiredSize.Height;
        double right = size.Width - TradingHub.Charting.ChartLayout.PriceAxisWidth - 4;
        double x = r.X + 14 + w <= right ? r.X + 14 : Math.Max(4, r.X - 14 - w);
        if (x + w > right) x = Math.Max(4, right - w);
        double y = r.Y + 14 + h <= size.Height - 30 ? r.Y + 14 : Math.Max(4, r.Y - 14 - h);
        Margin = new Thickness(x, y, 0, 0);
        IsVisible = true;
        _timer.Stop();
        _timer.Start();
    }

    private static Button Small(Control content, string tip)
    {
        var b = new Button { Content = content, Height = 28, MinHeight = 0, MinWidth = 28, Padding = new Thickness(6, 0) }.Classes("icon");
        b.Width = double.NaN;
        ToolTip.SetTip(b, tip);
        return b;
    }

    private static Control Sep() => new Border { Width = 1, Height = 18, Margin = new Thickness(3, 0), Background = Ui.Line, VerticalAlignment = VerticalAlignment.Center };

    private static Control Label(MaterialIconKind icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        row.Children.Add(Ui.Icon(icon, 15, Ui.Accent));
        row.Children.Add(new TextBlock { Text = text, Classes = { "small" }, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private Control BuildCreated(InkResult r)
    {
        var d = r.Drawing!;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var info = DrawingToolStrip.Info.TryGetValue(d.Kind, out var i) ? i : (Icon: MaterialIconKind.PencilOutline, Name: ChartDrawing.KindName(d.Kind));
        row.Children.Add(Label(info.Icon, info.Name.Split(" (")[0]));
        if (r.Alternatives.Count > 0 && r.Geometry != null)
        {
            row.Children.Add(Sep());
            row.Children.Add(new TextBlock { Text = "Cambiar por:", Classes = { "small", "muted" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) });
            foreach (var k in r.Alternatives)
            {
                var kind = k;
                var alt = DrawingToolStrip.Info.TryGetValue(kind, out var ai) ? ai : (Icon: MaterialIconKind.PencilOutline, Name: ChartDrawing.KindName(kind));
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                content.Children.Add(Ui.Icon(alt.Icon, 15));
                content.Children.Add(new TextBlock { Text = alt.Name.Split(" (")[0], FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                var b = Small(content, "Mismo trazo, otro tipo de dibujo");
                b.Click += (_, _) =>
                {
                    if (_chart.View.Controller is not { } c || !_chart.Model!.Drawings.Contains(d)) return;
                    c.RetypeInk(d, r.Geometry, kind);
                    _host.DrawingsTouched(_chart);
                    Show(new InkResult { Drawing = d, Geometry = r.Geometry, Alternatives = InkDrawings.Alternatives(kind), X = r.X, Y = r.Y });
                };
                row.Children.Add(b);
            }
        }

        row.Children.Add(Sep());
        var delete = Small(Ui.Icon(MaterialIconKind.DeleteOutline, 17), "Borrarlo");
        delete.Click += (_, _) =>
        {
            if (_chart.Model == null) return;
            _chart.Model.Drawings.Remove(d);
            _chart.Model.Alerts.RemoveAll(a => a.Uses(d.Id.ToString("N")));
            _chart.Model.Invalidate();
            _host.DrawingsTouched(_chart);
            Hide();
        };
        row.Children.Add(delete);
        var help = Small(Ui.Icon(MaterialIconKind.HelpCircleOutline, 16), "Los gestos");
        help.Click += (_, _) => DrawingToolStrip.ShowGuide(help);
        row.Children.Add(help);
        return row;
    }

    private Control BuildErase(InkResult r)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        string text = r.Removed.Count switch { 0 => "El garabato no tocó ningún dibujo", 1 => "Se borró un dibujo", _ => $"Se borraron {r.Removed.Count} dibujos" };
        row.Children.Add(Label(MaterialIconKind.Eraser, text));
        if (r.Removed.Count > 0)
        {
            row.Children.Add(Sep());
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(Ui.Icon(MaterialIconKind.UndoVariant, 15));
            content.Children.Add(new TextBlock { Text = "Deshacer", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var undo = Small(content, "Volver a poner lo que borró el garabato");
            undo.Click += (_, _) =>
            {
                _chart.View.Controller?.UndoErase(r.Removed);
                _host.DrawingsTouched(_chart);
                Hide();
            };
            row.Children.Add(undo);
        }

        return row;
    }
}
