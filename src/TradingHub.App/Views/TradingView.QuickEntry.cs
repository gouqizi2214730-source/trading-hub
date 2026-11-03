using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Views;

/// <summary>
/// Entrada rápida con el teclado (idea de Vela / TradingView): con el gráfico activo, escribir un símbolo («btc»,
/// «700.hk») o un período («15», «1h», «4h», «d») abre un cuadro arriba del gráfico y Enter lo aplica. «?» muestra
/// los atajos.
/// </summary>
public sealed partial class TradingView
{
    private Popup? _quick;
    private TextBox? _quickBox;
    private StackPanel? _quickList;
    private TextBlock? _quickHint;
    private List<(string ProfileId, Instrument Instrument)> _quickMatches = new();
    private CancellationTokenSource? _quickSearch;

    /// <summary>Un carácter tecleado sobre el gráfico (letra, dígito, punto o «?»).</summary>
    internal void OnChartTyped(ChartTab tab, string text)
    {
        if (tab.Model == null) return;
        if (text == "?")
        {
            ShowShortcuts();
            return;
        }

        if (_quick is { IsOpen: true })
        {
            _quickBox!.Text += text;
            _quickBox.CaretIndex = _quickBox.Text!.Length;
            return;
        }

        BuildQuickEntry(tab);
        _quickBox!.Text = text;
        _quickBox.CaretIndex = text.Length;
        _quick!.PlacementTarget = tab.View;
        _quick.IsOpen = true;
        _quickBox.Focus();
        UpdateQuickMatches(tab);
    }

    private void BuildQuickEntry(ChartTab tab)
    {
        if (_quick != null && _quickBox != null && _quickList != null && _quickHint != null) return;
        _quickBox = new TextBox { Width = 320, FontSize = 16, FontFamily = Ui.MonoFont, Watermark = "símbolo o período" };
        _quickList = new StackPanel { Spacing = 2 };
        _quickHint = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Text = "Enter: aplicar · Esc: cerrar · «15», «1h», «4h», «d», «w» cambian el período; letras buscan un símbolo" };
        var panel = new StackPanel { Spacing = 8, Children = { _quickBox, _quickList, _quickHint } };
        var border = new Border
        {
            Background = Ui.Bg2,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = panel,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, Color = Color.FromArgb(120, 0, 0, 0), OffsetY = 4 }),
        };
        _quick = new Popup
        {
            Child = border,
            Placement = PlacementMode.Top,
            PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft,
            PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight,
            HorizontalOffset = 60,
            VerticalOffset = 40,
            IsLightDismissEnabled = true,
        };
        _quickBox.TextChanged += (_, _) => UpdateQuickMatches(tab);
        _quickBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseQuick();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                _ = ApplyQuickAsync(_active ?? tab);
                e.Handled = true;
            }
        };
        _quick.Closed += (_, _) =>
        {
            _quickSearch?.Cancel();
            (_active ?? tab).View.Focus();
        };
    }

    private void CloseQuick()
    {
        if (_quick != null) _quick.IsOpen = false;
    }

    private static bool LooksLikePeriod(string text) =>
        text.Length > 0 && (text.All(char.IsDigit) || ChartPeriod.TryParse(text, out _));

    private static ChartPeriod? PeriodOf(string text)
    {
        if (text.All(char.IsDigit) && int.TryParse(text, out int minutes) && minutes > 0) return minutes % 60 == 0 && minutes >= 60 ? new ChartPeriod(PeriodType.Hours, minutes / 60) : new ChartPeriod(PeriodType.Minutes, minutes);
        return ChartPeriod.TryParse(text, out var p) ? p : null;
    }

    private void UpdateQuickMatches(ChartTab tab)
    {
        if (_quickList == null || _quickBox == null) return;
        string text = (_quickBox.Text ?? "").Trim();
        _quickList.Children.Clear();
        _quickMatches = new List<(string, Instrument)>();
        if (text.Length == 0) return;
        if (LooksLikePeriod(text) && PeriodOf(text) is { } p)
        {
            _quickList.Children.Add(new TextBlock { Text = $"Período → {p.Code}", Foreground = Ui.Accent, FontWeight = FontWeight.SemiBold });
            if (text.All(char.IsDigit)) _quickList.Children.Add(new TextBlock { Text = "(o seguí escribiendo un símbolo que empiece con números, como 700.HK)", Classes = { "faint", "small" } });
            if (!text.All(char.IsDigit)) return;
        }

        _quickSearch?.Cancel();
        var cts = _quickSearch = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var found = new List<(string, Instrument)>();
            // primero la conexión del gráfico, después las demás con datos
            var profiles = new List<ConnectionProfile>();
            if (_svc.Connections.Find(tab.FeedId) is { } own) profiles.Add(own);
            profiles.AddRange(_svc.Connections.Enabled.Where(x => x.HasMarketData && !profiles.Contains(x)));
            string q = text.ToUpperInvariant().Replace("/", "");
            foreach (var p in profiles)
            {
                if (cts.IsCancellationRequested) return;
                IReadOnlyList<Instrument> list;
                try
                {
                    list = InstrumentPicker.CachedInstruments(p.Id) ?? InstrumentCatalog.Read(p.Id);
                    if (list.Count == 0 && ReferenceEquals(p, profiles[0])) list = await InstrumentPicker.InstrumentsAsync(_svc, p.Id);
                }
                catch
                {
                    continue;
                }

                foreach (var i in list.Where(i => i.Symbol.ToUpperInvariant().StartsWith(q)).OrderBy(i => i.Symbol.Length).ThenBy(i => i.Symbol).Take(6)) found.Add((p.Id, i));
                if (found.Count >= 8) break;
            }

            if (found.Count < 8)
                foreach (var p in profiles)
                {
                    var list = InstrumentPicker.CachedInstruments(p.Id) ?? InstrumentCatalog.Read(p.Id);
                    foreach (var i in list.Where(i => !i.Symbol.ToUpperInvariant().StartsWith(q) && (i.Symbol.ToUpperInvariant().Contains(q) || i.Description.ToUpperInvariant().Contains(q))).Take(4)) found.Add((p.Id, i));
                    if (found.Count >= 10) break;
                }

            if (cts.IsCancellationRequested) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || _quickList == null) return;
                _quickMatches = found.Take(8).ToList();
                foreach (var (pid, inst) in _quickMatches)
                {
                    var row = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4) }.Classes("ghost");
                    row.Content = Ui.H(8, new TextBlock { Text = inst.Symbol, FontWeight = FontWeight.SemiBold, MinWidth = 90 },
                        new TextBlock { Text = inst.Description, Classes = { "faint", "small" }, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 },
                        new TextBlock { Text = _svc.Connections.Find(pid)?.Name ?? pid, Classes = { "faint", "small" } });
                    row.Click += (_, _) => _ = OpenQuickAsync(_active ?? tab, pid, inst);
                    _quickList.Children.Add(row);
                }

                if (_quickMatches.Count == 0 && !LooksLikePeriod(text)) _quickList.Children.Add(new TextBlock { Text = "Sin coincidencias", Classes = { "faint", "small" } });
            });
        });
    }

    private async Task ApplyQuickAsync(ChartTab tab)
    {
        string text = (_quickBox?.Text ?? "").Trim();
        if (text.Length == 0 || tab.Model == null)
        {
            CloseQuick();
            return;
        }

        // un número o «1h»… es un período, salvo que haya un símbolo que coincida exactamente (700.HK)
        var exact = _quickMatches.FirstOrDefault(m => m.Instrument.Symbol.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (exact.Instrument == null && LooksLikePeriod(text) && PeriodOf(text) is { } period)
        {
            CloseQuick();
            (tab.Window?.Toolbar ?? _toolbar).SetPeriod(period);
            _status($"Período {period.Code}");
            return;
        }

        var pick = exact.Instrument != null ? exact : _quickMatches.FirstOrDefault();
        if (pick.Instrument == null)
        {
            _status($"No encontré «{text}» en las conexiones activas.");
            return;
        }

        await OpenQuickAsync(tab, pick.ProfileId, pick.Instrument);
    }

    private async Task OpenQuickAsync(ChartTab tab, string profileId, Instrument inst)
    {
        CloseQuick();
        if (tab.Model == null) return;
        var period = tab.Model.Period;
        var settings = tab.Model.Settings;
        await OpenInTabAsync(tab, profileId, inst.Symbol, period, settings);
        _status($"{inst.Symbol} · {period.Code}");
    }

    /// <summary>Hoja de atajos (tecla «?»).</summary>
    private void ShowShortcuts()
    {
        var items = new StackPanel { Spacing = 4 };
        void Row(string keys, string what) => items.Children.Add(Ui.H(12, new TextBlock { Text = keys, FontFamily = Ui.MonoFont, MinWidth = 130, Foreground = Ui.Accent }, new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 }));
        items.Children.Add(Ui.H3("Teclado sobre el gráfico"));
        Row("letras / números", "Entrada rápida: símbolo (btc, 700.hk) o período (15, 1h, 4h, d, w); Enter aplica");
        Row("?", "Esta hoja");
        Row("Alt+R", "Restablecer la vista");
        Row("rueda / Shift+rueda", "Zoom en el tiempo / en el precio");
        Row("Esc", "Soltar la herramienta de dibujo");
        items.Children.Add(Ui.H3("Atajos configurados (Ajustes → Atajos)"));
        foreach (var group in Hotkeys.All.GroupBy(h => h.Group))
        {
            items.Children.Add(new TextBlock { Text = group.Key, Classes = { "faint", "small" }, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var h in group)
            {
                var g = Hotkeys.GestureOf(_svc.Settings, h.Id);
                if (g == null) continue;
                Row(Hotkeys.Pretty(g), h.Name);
            }
        }

        var win = new Window
        {
            Title = "Atajos de teclado",
            Width = 560,
            Height = 620,
            Content = new ScrollViewer { Content = items, Padding = new Thickness(20) },
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (TopLevel.GetTopLevel(this) is Window owner) win.Show(owner);
        else win.Show();
    }
}
