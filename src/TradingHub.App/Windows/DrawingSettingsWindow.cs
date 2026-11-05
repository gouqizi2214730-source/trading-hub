using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.Charting.Drawings;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Windows;

/// <summary>
/// Panel completo de un dibujo (doble clic sobre él): color, grosor, estilo de línea, relleno, texto,
/// extensiones, niveles de Fibonacci, precios, cantidad de la posición y visibilidad. Los cambios se ven al
/// instante en todos los gráficos del instrumento.
/// </summary>
public sealed class DrawingSettingsWindow : Window
{
    private static readonly uint[] Palette =
    {
        0xFF1E90FF, 0xFF3D8BFD, 0xFF22D3EE, 0xFF26C281, 0xFF49D149, 0xFFF2B53A, 0xFFF97316,
        0xFFF0525A, 0xFFE84548, 0xFFEC4899, 0xFFB478FF, 0xFFFFFFFF, 0xFF9CA3AF, 0xFF1F2937,
    };

    private readonly ChartDrawing _d;
    private readonly Instrument _instrument;
    private readonly ChartPeriod _period;
    private readonly Action _changed;
    private readonly AppServices _svc;
    private TimeZoneInfo _tz => _svc.Settings.TimeZoneId is { Length: > 0 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z) ? z : TimeZoneInfo.Local;

    /// <param name="changed">Se llama en cada cambio (redibujar y guardar).</param>
    /// <param name="delete">Borra el dibujo de su lista.</param>
    /// <param name="duplicate">Agrega una copia.</param>
    public DrawingSettingsWindow(AppServices svc, ChartDrawing drawing, Instrument instrument, ChartPeriod period, Action changed, Action delete, Action duplicate)
    {
        _svc = svc;
        _d = drawing;
        _instrument = instrument;
        _period = period;
        _changed = changed;
        Title = ChartDrawing.KindName(drawing.Kind);
        Width = 430;
        Height = 640;
        MinWidth = 380;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(20, 18, 20, 18) };
        body.Children.Add(Ui.H2(ChartDrawing.KindName(drawing.Kind)));

        if (!drawing.IsPosition && drawing.Kind != DrawingKind.Ruler) body.Children.Add(StyleSection());
        if (drawing.Kind is DrawingKind.Rectangle or DrawingKind.ParallelChannel) body.Children.Add(FillSection());
        if (drawing.Kind is not (DrawingKind.Ruler or DrawingKind.FibRetracement) && !drawing.IsPosition) body.Children.Add(TextSection());
        if (drawing.Kind is DrawingKind.TrendLine or DrawingKind.Rectangle or DrawingKind.HorizontalRay or DrawingKind.FibRetracement or DrawingKind.ParallelChannel) body.Children.Add(ExtendSection());
        if (drawing.Kind == DrawingKind.FibRetracement) body.Children.Add(LevelsSection());
        if (drawing.IsPosition) body.Children.Add(PositionSection());
        else if (drawing.Kind == DrawingKind.AnchoredVwap) body.Children.Add(AnchoredVwapSection());
        else if (drawing.Kind == DrawingKind.FixedRangeProfile) body.Children.Add(ProfileSection());
        else if (drawing.Kind != DrawingKind.VerticalLine) body.Children.Add(PricesSection());
        body.Children.Add(VisibilitySection());

        var makeDefault = Ui.Button("Usar como predeterminado", MaterialIconKind.StarOutline, () =>
        {
            var template = new ChartDrawing { Kind = _d.Kind };
            template.CopyStyleFrom(_d);
            _svc.Settings.DrawingDefaults[_d.Kind.ToString()] = template;
            _svc.Settings.Save();
            Title = ChartDrawing.KindName(_d.Kind) + " · estilo guardado como predeterminado";
        });
        ToolTip.SetTip(makeDefault, "Los próximos dibujos de este tipo salen con este estilo");
        var dup = Ui.Button("Duplicar", MaterialIconKind.ContentCopy, () =>
        {
            duplicate();
            Close();
        });
        var del = Ui.Button("Eliminar", MaterialIconKind.DeleteOutline, () =>
        {
            delete();
            Close();
        }, "danger");
        var close = Ui.Button("Listo", MaterialIconKind.Check, Close, "primary");
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(20, 10, 20, 16) };
        foreach (var b in new Control[] { makeDefault, dup, del, close }) actions.Children.Add(b);

        var root = new DockPanel();
        var actionsBorder = new Border { Child = actions, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(actionsBorder, Dock.Bottom);
        root.Children.Add(actionsBorder);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }

    private void Changed() => _changed();

    // ------------------------------------------------------------------ secciones

    private static TextBlock Note(string text) => new() { Text = text, Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    private Control Section(string title, params Control[] rows)
    {
        var v = new StackPanel { Spacing = 10 };
        v.Children.Add(Ui.H3(title));
        foreach (var r in rows) v.Children.Add(r);
        return v;
    }

    private static Control Labeled(string label, Control control)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
        g.Children.Add(t);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private Control StyleSection()
    {
        var width = new Slider { Minimum = 0.5, Maximum = 10, Value = _d.Width, TickFrequency = 0.5, IsSnapToTickEnabled = true, MinWidth = 160 };
        var widthText = new TextBlock { Text = $"{_d.Width:0.#} px", VerticalAlignment = VerticalAlignment.Center, Width = 44, Classes = { "muted" } };
        width.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _d.Width = (float)width.Value;
            widthText.Text = $"{_d.Width:0.#} px";
            Changed();
        };
        var style = new Ui.Segmented<LineStyleKind>(new[]
        {
            (LineStyleKind.Solid, "Sólida", (string?)null),
            (LineStyleKind.Dashed, "Guiones", (string?)null),
            (LineStyleKind.Dotted, "Puntos", (string?)null),
        });
        style.Select(_d.EffectiveLineStyle);
        style.SelectionChanged += v =>
        {
            _d.LineStyle = v;
            _d.Dashed = false;
            Changed();
        };
        return Section("Línea",
            Labeled("Color", ColorPicker(() => _d.ColorArgb, c => _d.ColorArgb = c, allowAuto: false)),
            Labeled("Grosor", Ui.H(8, width, widthText)),
            Labeled("Estilo", style));
    }

    private Control FillSection()
    {
        uint Current() => _d.FillArgb != 0 ? _d.FillArgb : (_d.ColorArgb & 0x00FFFFFF) | (40u << 24);
        var opacity = new Slider { Minimum = 0, Maximum = 255, Value = Current() >> 24, MinWidth = 160 };
        opacity.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _d.FillArgb = (Current() & 0x00FFFFFF) | ((uint)opacity.Value << 24);
            Changed();
        };
        return Section("Relleno",
            Labeled("Color", ColorPicker(() => _d.FillArgb, c => _d.FillArgb = c == 0 ? 0 : (c & 0x00FFFFFF) | ((uint)opacity.Value << 24), allowAuto: true)),
            Labeled("Opacidad", opacity));
    }

    private Control TextSection()
    {
        var text = new TextBox { Text = _d.Text == "alerta" ? "" : _d.Text, Watermark = _d.Kind == DrawingKind.Text ? "Texto" : "Etiqueta (opcional)", AcceptsReturn = false };
        text.TextChanged += (_, _) =>
        {
            _d.Text = text.Text ?? "";
            if (_d.Text == "alerta") _d.Text = "alerta ";
            Changed();
        };
        var size = new NumericUpDown { Minimum = 8, Maximum = 40, Increment = 1, Value = (decimal)_d.FontSize, FormatString = "0", Width = 110 };
        size.ValueChanged += (_, _) =>
        {
            _d.FontSize = (float)(size.Value ?? 13);
            Changed();
        };
        var bold = new CheckBox { Content = "Negrita", IsChecked = _d.Bold, Margin = new Thickness(12, 0, 0, 0) };
        bold.IsCheckedChanged += (_, _) =>
        {
            _d.Bold = bold.IsChecked == true;
            Changed();
        };
        return Section("Texto",
            text,
            Labeled("Tamaño", Ui.H(4, size, bold)),
            Labeled("Color", ColorPicker(() => _d.TextArgb, c => _d.TextArgb = c, allowAuto: true)));
    }

    private Control ExtendSection()
    {
        var left = new CheckBox { Content = "A la izquierda", IsChecked = _d.ExtendLeft };
        var right = new CheckBox { Content = "A la derecha", IsChecked = _d.ExtendRight, Margin = new Thickness(16, 0, 0, 0) };
        left.IsCheckedChanged += (_, _) =>
        {
            _d.ExtendLeft = left.IsChecked == true;
            Changed();
        };
        right.IsCheckedChanged += (_, _) =>
        {
            _d.ExtendRight = right.IsChecked == true;
            Changed();
        };
        var rows = new List<Control> { Ui.H(0, left, right) };
        if (_d.Kind is DrawingKind.HorizontalRay or DrawingKind.FibRetracement or DrawingKind.HorizontalLine)
        {
            var price = new CheckBox { Content = "Mostrar el precio", IsChecked = _d.ShowPriceLabel };
            price.IsCheckedChanged += (_, _) =>
            {
                _d.ShowPriceLabel = price.IsChecked == true;
                Changed();
            };
            rows.Add(price);
        }

        return Section("Extender", rows.ToArray());
    }

    private Control LevelsSection()
    {
        var list = new StackPanel { Spacing = 6 };
        void Rebuild()
        {
            list.Children.Clear();
            var levels = _d.Levels ??= _d.EffectiveLevels.Select(l => l.Clone()).ToList();
            foreach (var lvl in levels.OrderBy(l => l.Value).ToList())
            {
                var on = new CheckBox { IsChecked = lvl.Visible, VerticalAlignment = VerticalAlignment.Center };
                on.IsCheckedChanged += (_, _) =>
                {
                    lvl.Visible = on.IsChecked == true;
                    Changed();
                };
                var value = new NumericUpDown { Value = (decimal)lvl.Value, Increment = 0.001m, FormatString = "0.###", Width = 150, Minimum = -10, Maximum = 10, NumberFormat = CultureInfo.InvariantCulture.NumberFormat };
                value.ValueChanged += (_, _) =>
                {
                    lvl.Value = (double)(value.Value ?? 0);
                    Changed();
                };
                var color = SwatchButton(() => lvl.ColorArgb != 0 ? lvl.ColorArgb : _d.ColorArgb, c =>
                {
                    lvl.ColorArgb = c;
                    Changed();
                });
                var remove = Ui.IconButton(MaterialIconKind.Close, "Quitar el nivel", () =>
                {
                    levels.Remove(lvl);
                    Changed();
                    Rebuild();
                }, 15);
                list.Children.Add(Ui.H(8, on, value, color, remove));
            }
        }

        Rebuild();
        var add = Ui.Button("Agregar nivel", MaterialIconKind.Plus, () =>
        {
            (_d.Levels ??= _d.EffectiveLevels.Select(l => l.Clone()).ToList()).Add(new FibLevel { Value = 2.618 });
            Changed();
            Rebuild();
        });
        var reset = Ui.Button("Restablecer", MaterialIconKind.Restore, () =>
        {
            _d.Levels = null;
            Changed();
            Rebuild();
        });
        var price = new CheckBox { Content = "Mostrar el precio de cada nivel", IsChecked = _d.ShowPriceLabel };
        price.IsCheckedChanged += (_, _) =>
        {
            _d.ShowPriceLabel = price.IsChecked == true;
            Changed();
        };
        return Section("Niveles", list, Ui.H(8, add, reset), price);
    }

    private NumericUpDown PriceBox(double value, Action<double> set)
    {
        decimal tick = _instrument.TickSize > 0 ? _instrument.TickSize : 0.01m;
        var box = new NumericUpDown
        {
            Value = _instrument.RoundPrice((decimal)value),
            Increment = tick,
            FormatString = "F" + _instrument.PriceDecimals.ToString(CultureInfo.InvariantCulture),
            MinWidth = 160,
            NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
        };
        box.ValueChanged += (_, _) =>
        {
            if (box.Value is not { } v) return;
            set((double)v);
            Changed();
        };
        return box;
    }

    private Control PositionSection()
    {
        var rr = new TextBlock { Classes = { "muted" } };
        void UpdateRr()
        {
            double risk = Math.Abs(_d.Points[0].Price - _d.Points[2].Price), reward = Math.Abs(_d.Points[1].Price - _d.Points[0].Price);
            rr.Text = risk > 0 ? $"Riesgo/beneficio 1 : {reward / risk:0.00}" : "";
        }

        UpdateRr();
        Action? onPrices = null;
        var entry = PriceBox(_d.Points[0].Price, v =>
        {
            _d.Points[0] = new ChartPoint(_d.Points[0].Time, v);
            UpdateRr();
            onPrices?.Invoke();
        });
        var target = PriceBox(_d.Points[1].Price, v =>
        {
            _d.Points[1] = new ChartPoint(_d.Points[1].Time, v);
            UpdateRr();
        });
        var stop = PriceBox(_d.Points[2].Price, v =>
        {
            _d.Points[2] = new ChartPoint(_d.Points[2].Time, v);
            UpdateRr();
            onPrices?.Invoke();
        });
        var qty = new NumericUpDown { Value = _d.Quantity, Minimum = 0, Increment = _instrument.VolumeStep > 0 ? _instrument.VolumeStep : 1, FormatString = "0.########", MinWidth = 160, NumberFormat = CultureInfo.InvariantCulture.NumberFormat };
        qty.ValueChanged += (_, _) =>
        {
            _d.Quantity = qty.Value ?? 0;
            Changed();
        };
        // como en TradingView: tamaño de la cuenta + riesgo en % → la cantidad sale sola (riesgo ÷ distancia al stop)
        var sized = new TextBlock { Classes = { "muted" } };
        void UpdateSized()
        {
            decimal q = _d.EffectiveQuantity(_instrument.VolumeStep);
            sized.Text = _d.AccountSize > 0 && _d.RiskPercent > 0
                ? $"Cantidad por riesgo: {q.ToString("0.########", CultureInfo.InvariantCulture)} (arriesga {(_d.AccountSize * _d.RiskPercent / 100m).ToString("0.##", CultureInfo.InvariantCulture)})"
                : "";
            qty.IsEnabled = !(_d.AccountSize > 0 && _d.RiskPercent > 0);
        }

        var account = new NumericUpDown { Value = _d.AccountSize, Minimum = 0, Increment = 100, FormatString = "0.##", MinWidth = 160, NumberFormat = CultureInfo.InvariantCulture.NumberFormat };
        account.ValueChanged += (_, _) =>
        {
            _d.AccountSize = account.Value ?? 0;
            UpdateSized();
            Changed();
        };
        var riskPct = new NumericUpDown { Value = _d.RiskPercent, Minimum = 0, Maximum = 100, Increment = 0.5m, FormatString = "0.##", MinWidth = 160, NumberFormat = CultureInfo.InvariantCulture.NumberFormat };
        riskPct.ValueChanged += (_, _) =>
        {
            _d.RiskPercent = riskPct.Value ?? 0;
            UpdateSized();
            Changed();
        };
        UpdateSized();
        onPrices = UpdateSized;
        return Section("Precios",
            Labeled("Entrada", entry),
            Labeled("Objetivo", target),
            Labeled("Stop", stop),
            Labeled("Cuenta", account),
            Labeled("Riesgo (%)", riskPct),
            sized,
            Labeled("Cantidad", qty),
            rr,
            Note("Con cuenta y riesgo, la cantidad sale sola (como en TradingView); si no, escribila. Desde la entrada hasta donde terminó se sombrea el recorrido con su resultado: cerrada si tocó el objetivo o el stop, abierta al último precio si no."));
    }

    private NumericUpDown Num(decimal value, decimal min, decimal max, decimal step, string format, Action<decimal> set)
    {
        var n = new NumericUpDown { Value = value, Minimum = min, Maximum = max, Increment = step, FormatString = format, MinWidth = 160, NumberFormat = CultureInfo.InvariantCulture.NumberFormat };
        n.ValueChanged += (_, _) =>
        {
            set(n.Value ?? value);
            Changed();
        };
        return n;
    }

    private Control AnchoredVwapSection()
    {
        var when = new TextBlock { Classes = { "muted" }, Text = $"Ancla: {TimeZoneInfo.ConvertTimeFromUtc(_d.Points[0].Time, _tz):yyyy-MM-dd HH:mm}" };
        return Section("VWAP anclado",
            when,
            Labeled("Banda 1 (× σ)", Num((decimal)_d.Band1, 0, 10, 0.5m, "0.##", v => _d.Band1 = (double)v)),
            Labeled("Banda 2 (× σ)", Num((decimal)_d.Band2, 0, 10, 0.5m, "0.##", v => _d.Band2 = (double)v)),
            Note("Se calcula con cada operación desde el ancla (no con las velas): da lo mismo en cualquier período y con cualquier zoom. 0 = sin banda. Arrastrá el ancla para moverla."));
    }

    private Control ProfileSection()
    {
        var snap = new CheckBox { Content = "Sesiones enteras (del inicio de la primera al fin de la última)", IsChecked = _d.SnapToSessions };
        snap.IsCheckedChanged += (_, _) =>
        {
            _d.SnapToSessions = snap.IsChecked == true;
            Changed();
        };
        var va = new CheckBox { Content = "Mostrar VAH / VAL", IsChecked = _d.ShowValueArea };
        va.IsCheckedChanged += (_, _) =>
        {
            _d.ShowValueArea = va.IsChecked == true;
            Changed();
        };
        var delta = new CheckBox { Content = "Perfil de delta (comprador − vendedor) en vez de volumen", IsChecked = _d.ShowDeltaProfile };
        delta.IsCheckedChanged += (_, _) =>
        {
            _d.ShowDeltaProfile = delta.IsChecked == true;
            Changed();
        };
        return Section("Perfil de rango fijo",
            new TextBlock { Classes = { "muted" }, Text = $"Desde {TimeZoneInfo.ConvertTimeFromUtc(_d.Points[0].Time, _tz):yyyy-MM-dd HH:mm} hasta {TimeZoneInfo.ConvertTimeFromUtc(_d.Points[^1].Time, _tz):yyyy-MM-dd HH:mm}" },
            snap,
            Labeled("Filas (0 = un tick)", Num(_d.ProfileRows, 0, 2000, 10, "0", v => _d.ProfileRows = (int)v)),
            Labeled("Área de valor (%)", Num(_d.ValueAreaPercent, 1, 100, 1, "0", v => _d.ValueAreaPercent = (int)v)),
            Labeled("Ancho (% del rango)", Num(_d.ProfileWidthPercent, 5, 100, 5, "0", v => _d.ProfileWidthPercent = (int)v)),
            va,
            delta,
            Note("Como en Exocharts: elegí un inicio y un fin y se juntan en un solo perfil (varias sesiones, semanas…). Se arma con cada operación, así que es igual en todos los períodos. Arrastrá los bordes para cambiar el rango."));
    }

    private Control PricesSection()
    {
        var rows = new List<Control>();
        int n = _d.Kind is DrawingKind.HorizontalLine or DrawingKind.HorizontalRay or DrawingKind.Text ? 1 : _d.Points.Count;
        for (int i = 0; i < n && i < _d.Points.Count; i++)
        {
            int k = i;
            rows.Add(Labeled(n == 1 ? "Precio" : $"Precio {k + 1}", PriceBox(_d.Points[k].Price, v => _d.Points[k] = new ChartPoint(_d.Points[k].Time, v))));
        }

        return Section("Coordenadas", rows.ToArray());
    }

    private Control VisibilitySection()
    {
        var hidden = new CheckBox { Content = "Ocultar", IsChecked = _d.Hidden };
        hidden.IsCheckedChanged += (_, _) =>
        {
            _d.Hidden = hidden.IsChecked == true;
            Changed();
        };
        var only = new CheckBox { Content = $"Mostrar solo en {_period.DisplayName}", IsChecked = _d.OnlyPeriod != null, };
        if (_d.OnlyPeriod != null && _d.OnlyPeriod != _period.Code) only.Content = $"Mostrar solo en {_d.OnlyPeriod} (destildá para verlo en todos)";
        ToolTip.SetTip(only, "Sin tildar, el dibujo se ve en todos los gráficos de este instrumento (todos los períodos)");
        only.IsCheckedChanged += (_, _) =>
        {
            _d.OnlyPeriod = only.IsChecked == true ? _period.Code : null;
            Changed();
        };
        var locked = new CheckBox { Content = "Bloquear (no se puede mover)", IsChecked = _d.Locked };
        locked.IsCheckedChanged += (_, _) =>
        {
            _d.Locked = locked.IsChecked == true;
            Changed();
        };
        var rows = new List<Control> { hidden, only, locked };
        if (_d.Kind is DrawingKind.HorizontalLine or DrawingKind.HorizontalRay)
        {
            var alert = new CheckBox { Content = "Alerta cuando el precio la toque", IsChecked = _d.IsAlert };
            alert.IsCheckedChanged += (_, _) =>
            {
                _d.Alert = alert.IsChecked == true;
                if (!_d.Alert && _d.Text == "alerta") _d.Text = "";
                Changed();
            };
            rows.Add(alert);
        }

        rows.Add(Note("Los dibujos se ven en todos los gráficos del mismo instrumento y se sincronizan al moverlos o editarlos."));
        return Section("Visibilidad", rows.ToArray());
    }

    // ------------------------------------------------------------------ colores

    private Control ColorPicker(Func<uint> get, Action<uint> set, bool allowAuto)
    {
        var wrap = new WrapPanel { ItemSpacing = 5, LineSpacing = 5 };
        var hex = new TextBox { Width = 96, Watermark = "#RRGGBB", FontSize = 12 };
        var swatches = new List<(uint Color, Border Ring)>();

        void Refresh()
        {
            uint cur = get();
            foreach (var (c, ring) in swatches) ring.BorderBrush = (c | 0xFF000000) == (cur | 0xFF000000) && cur != 0 ? Ui.Accent : Brushes.Transparent;
            hex.Text = cur == 0 ? "" : $"#{cur & 0xFFFFFF:X6}";
        }

        if (allowAuto)
        {
            var auto = new Button { Content = "Auto", Padding = new Thickness(6, 1), MinHeight = 22, FontSize = 11 };
            ToolTip.SetTip(auto, "Igual que la línea");
            auto.Click += (_, _) =>
            {
                set(0);
                Refresh();
                Changed();
            };
            wrap.Children.Add(auto);
        }

        foreach (var c in Palette)
        {
            var color = c;
            var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromUInt32(color)) };
            var ring = new Border { Child = swatch, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), Padding = new Thickness(1) };
            var b = new Button { Content = ring, Padding = new Thickness(0), MinHeight = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            b.Click += (_, _) =>
            {
                set(color);
                Refresh();
                Changed();
            };
            swatches.Add((color, ring));
            wrap.Children.Add(b);
        }

        hex.LostFocus += (_, _) => ApplyHex();
        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) ApplyHex();
        };

        void ApplyHex()
        {
            var t = (hex.Text ?? "").Trim().TrimStart('#');
            if (t.Length == 6 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                set(0xFF000000 | rgb);
                Changed();
            }

            Refresh();
        }

        wrap.Children.Add(hex);
        Refresh();
        return wrap;
    }

    /// <summary>Botoncito de color que abre la paleta (para los niveles de Fibonacci).</summary>
    private Control SwatchButton(Func<uint> get, Action<uint> set)
    {
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromUInt32(get())) };
        var panel = new WrapPanel { ItemSpacing = 4, LineSpacing = 4, MaxWidth = 170, Margin = new Thickness(8) };
        var flyout = new Flyout { Content = panel };
        foreach (var c in Palette)
        {
            var color = c;
            var b = new Button { Content = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromUInt32(color)) }, Padding = new Thickness(1), MinHeight = 0 };
            b.Click += (_, _) =>
            {
                set(color);
                swatch.Background = new SolidColorBrush(Color.FromUInt32(color));
                flyout.Hide();
            };
            panel.Children.Add(b);
        }

        return new Button { Content = swatch, Padding = new Thickness(2), MinHeight = 0, Flyout = flyout, VerticalAlignment = VerticalAlignment.Center };
    }
}
