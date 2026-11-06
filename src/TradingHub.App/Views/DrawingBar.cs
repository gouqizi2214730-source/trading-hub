using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;

namespace TradingHub.App.Views;

/// <summary>
/// La barrita flotante del dibujo elegido (como en TradingView): color, grosor, estilo de línea, bloquear, alerta,
/// configuración y borrar, arriba al centro del gráfico mientras haya un dibujo elegido.
/// </summary>
internal sealed class DrawingBar : Border
{
    private static readonly (string Name, uint Argb)[] Colors =
    {
        ("Azul", 0xFF3D8BFD), ("Celeste", 0xFF38BDF8), ("Verde", 0xFF26C281), ("Amarillo", 0xFFF2B53A), ("Naranja", 0xFFFB8C00),
        ("Rojo", 0xFFF0525A), ("Rosa", 0xFFF472B6), ("Violeta", 0xFFB478FF), ("Blanco", 0xFFFFFFFF), ("Gris", 0xFF8B93A1),
    };

    private readonly TradingView _host;
    private readonly ChartTab _chart;
    private string _sig = "";

    public DrawingBar(TradingView host, ChartTab chart)
    {
        _host = host;
        _chart = chart;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        // debajo de las dos primeras líneas de la leyenda (precios e indicadores), como la de TradingView
        Margin = new Thickness(0, 40, 0, 0);
        Padding = new Thickness(4, 3);
        CornerRadius = new CornerRadius(8);
        Background = Ui.Bg2;
        BorderBrush = Ui.Line;
        BorderThickness = new Thickness(1);
        BoxShadow = BoxShadows.Parse("0 4 14 0 #66000000");
        IsVisible = false;
    }

    private ChartDrawing? Selected =>
        _chart.Model is { Settings.HideDrawings: false } m ? m.Drawings.FirstOrDefault(d => d.Selected && d.IsVisibleOn(m.Period)) : null;

    /// <summary>Se muestra, se esconde o se rehace si cambió el dibujo elegido (lo llama el ciclo de los gráficos).</summary>
    public void Refresh()
    {
        var d = Selected;
        string sig = d == null ? "" : $"{d.Id}|{d.ColorArgb}|{d.Width}|{d.LineStyle}|{d.Locked}|{d.IsAlert}|{_chart.Model?.Settings.LockDrawings}";
        if (sig == _sig) return;
        _sig = sig;
        IsVisible = d != null;
        if (d != null) Child = Build(d);
    }

    private void Changed()
    {
        _host.DrawingsTouched(_chart);
        _sig = "";
        Refresh();
    }

    private static Button Small(Control content, string tip)
    {
        var b = new Button { Content = content, Height = 28, MinHeight = 0, MinWidth = 28, Padding = new Thickness(6, 0) }.Classes("icon");
        b.Width = double.NaN; // el estilo «icon» lo deja fijo en 32 y corta el texto del grosor
        ToolTip.SetTip(b, tip);
        return b;
    }

    private static Control Sep() => new Border { Width = 1, Height = 18, Margin = new Thickness(3, 0), Background = Ui.Line, VerticalAlignment = VerticalAlignment.Center };

    private Control Build(ChartDrawing d)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = DrawingToolStrip.Info.TryGetValue(d.Kind, out var info) ? info.Name.Split(" (")[0] : d.Kind.ToString(), Classes = { "small", "muted" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0), MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis };
        row.Children.Add(name);
        row.Children.Add(Sep());

        // color
        var swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromUInt32(d.ColorArgb)), BorderBrush = Ui.Line, BorderThickness = new Thickness(1) };
        var color = Small(swatch, "Color");
        color.Click += (_, _) =>
        {
            var grid = new WrapPanel { Width = 5 * 30, ItemSpacing = 4, LineSpacing = 4, Margin = new Thickness(8) };
            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var (cname, argb) in Colors)
            {
                var c = argb;
                var b = new Button { Width = 26, Height = 26, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromUInt32(c)), BorderBrush = d.ColorArgb == c ? Ui.Accent : Ui.Line, BorderThickness = new Thickness(d.ColorArgb == c ? 2 : 1) };
                ToolTip.SetTip(b, cname);
                b.Click += (_, _) =>
                {
                    d.ColorArgb = c;
                    flyout.Hide();
                    Changed();
                };
                grid.Children.Add(b);
            }

            flyout.Content = grid;
            flyout.ShowAt(color);
        };
        row.Children.Add(color);

        // grosor
        var width = Small(new TextBlock { Text = $"{d.Width:0.#} px", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, "Grosor de la línea");
        width.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var w in new[] { 1f, 1.5f, 2f, 3f, 4f })
            {
                var v = w;
                var item = new MenuItem { Header = $"{v:0.#} px", Icon = Math.Abs(d.Width - v) < 0.01 ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null };
                item.Click += (_, _) =>
                {
                    d.Width = v;
                    Changed();
                };
                menu.Items.Add(item);
            }

            menu.ShowAt(width);
        };
        row.Children.Add(width);

        // estilo
        var styleIcon = d.LineStyle switch { LineStyleKind.Dashed => MaterialIconKind.DotsHorizontal, LineStyleKind.Dotted => MaterialIconKind.DotsHorizontal, _ => MaterialIconKind.Minus };
        var style = Small(Ui.Icon(styleIcon, 18), "Estilo de la línea");
        style.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var (k, text) in new[] { (LineStyleKind.Solid, "Continua"), (LineStyleKind.Dashed, "Rayada"), (LineStyleKind.Dotted, "Punteada") })
            {
                var v = k;
                var item = new MenuItem { Header = text, Icon = d.LineStyle == v ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null };
                item.Click += (_, _) =>
                {
                    d.LineStyle = v;
                    d.Dashed = false;
                    Changed();
                };
                menu.Items.Add(item);
            }

            menu.ShowAt(style);
        };
        row.Children.Add(style);
        row.Children.Add(Sep());

        // bloquear
        var locked = d.Locked || _chart.Model?.Settings.LockDrawings == true;
        var lockButton = Small(Ui.Icon(locked ? MaterialIconKind.LockOutline : MaterialIconKind.LockOpenVariantOutline, 17, locked ? Ui.Accent : Ui.Text1), locked ? "Desbloquear (se puede mover)" : "Bloquear (no se mueve)");
        lockButton.Click += (_, _) =>
        {
            d.Locked = !d.Locked;
            Changed();
        };
        row.Children.Add(lockButton);

        // alerta sobre el dibujo
        if (_chart.Model is { } model)
        {
            var lines = ChartAlertEngine.DrawingLines(model, d);
            bool horizontal = d.Kind is DrawingKind.HorizontalLine or DrawingKind.HorizontalRay;
            if (lines.Count > 0 || horizontal)
            {
                var alert = Small(Ui.Icon(d.IsAlert ? MaterialIconKind.Bell : MaterialIconKind.BellPlusOutline, 17, d.IsAlert ? Ui.Accent : Ui.Text1), "Alerta sobre este dibujo");
                alert.Click += (_, _) =>
                {
                    var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
                    var price = AlertOperand.Price("close", "Precio (cierre)");
                    if (horizontal)
                    {
                        var quick = new MenuItem { Header = d.IsAlert ? "Quitar la alerta rápida" : "Alertar cuando el precio toque", Icon = Ui.Icon(MaterialIconKind.BellOutline, 16) };
                        quick.Click += (_, _) =>
                        {
                            if (d.IsAlert)
                            {
                                d.Alert = false;
                                if (d.Text == "alerta") d.Text = "";
                            }
                            else d.Alert = true;
                            Changed();
                        };
                        menu.Items.Add(quick);
                    }

                    if (d.IsChannel && lines.Count >= 2)
                    {
                        foreach (var (text, cond) in new[] { ("Entra en el canal…", AlertCondition.EnteringChannel), ("Sale del canal…", AlertCondition.ExitingChannel) })
                        {
                            var c = cond;
                            var item = new MenuItem { Header = text };
                            item.Click += (_, _) => _ = _host.OpenAlertsAsync(_chart, price, lines[0], lines[^1], c);
                            menu.Items.Add(item);
                        }
                    }
                    else if (lines.Count > 0)
                    {
                        foreach (var (text, cond) in new[] { ("Cruza la línea…", AlertCondition.Crossing), ("Cruza hacia arriba…", AlertCondition.CrossingUp), ("Cruza hacia abajo…", AlertCondition.CrossingDown), ("Toca la línea…", AlertCondition.Touching) })
                        {
                            var c = cond;
                            var item = new MenuItem { Header = text };
                            item.Click += (_, _) => _ = _host.OpenAlertsAsync(_chart, price, lines[0], null, c);
                            menu.Items.Add(item);
                        }
                    }

                    menu.ShowAt(alert);
                };
                row.Children.Add(alert);
            }
        }

        var settings = Small(Ui.Icon(MaterialIconKind.CogOutline, 17), "Configurar el dibujo (también con doble clic)");
        settings.Click += (_, _) => _host.EditDrawing(_chart, d);
        row.Children.Add(settings);
        var delete = Small(Ui.Icon(MaterialIconKind.DeleteOutline, 17), "Borrar el dibujo (Supr)");
        delete.Click += (_, _) =>
        {
            _chart.Model?.Drawings.Remove(d);
            _chart.Model?.Invalidate();
            Changed();
        };
        row.Children.Add(delete);
        return row;
    }
}
