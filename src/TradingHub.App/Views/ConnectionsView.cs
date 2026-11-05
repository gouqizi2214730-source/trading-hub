using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;
using TradingHub.Feeds.Rithmic;

namespace TradingHub.App.Views;

/// <summary>
/// Conexiones: perfiles de datos y cuentas (Rithmic, Binance, Bybit, Interactive Brokers y simulador).
/// Las contraseñas y API secrets se guardan en el Llavero de macOS (en Windows, en el Administrador de credenciales), nunca
/// en archivos.
/// </summary>
internal sealed class ConnectionsView : UserControl, IPage
{
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly ListBox _list = new();
    private readonly ContentControl _detail = new();
    private readonly Dictionary<string, (bool Ok, string Message, DateTime When)> _results = new();
    private ConnectionProfile? _selected;
    private CancellationTokenSource? _testCts;

    public ConnectionsView(AppServices svc, Action<string> status)
    {
        _svc = svc;
        _status = status;
        Content = Build();
        FillList();
        _svc.Connections.Changed += FillList;
    }

    public void OnShown() => FillList();

    public void OnTimer()
    {
    }

    private static MaterialIconKind IconOf(ConnectionKind k) => k switch
    {
        ConnectionKind.Simulator => MaterialIconKind.FlaskOutline,
        ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot => MaterialIconKind.Bitcoin,
        ConnectionKind.Bybit => MaterialIconKind.CurrencyUsd,
        ConnectionKind.Rithmic => MaterialIconKind.ChartTimelineVariant,
        ConnectionKind.AmpFutures => MaterialIconKind.ChartBellCurveCumulative,
        ConnectionKind.Tradovate => MaterialIconKind.ChartLineVariant,
        ConnectionKind.CTrader => MaterialIconKind.ChartAreaspline,
        ConnectionKind.Oanda => MaterialIconKind.Earth,
        ConnectionKind.Alpaca => MaterialIconKind.Finance,
        ConnectionKind.Databento => MaterialIconKind.DatabaseArrowDownOutline,
        ConnectionKind.Longbridge => MaterialIconKind.Bridge,
        ConnectionKind.Aggregate => MaterialIconKind.Layers,
        ConnectionKind.OptionsData => MaterialIconKind.ChartBellCurve,
        ConnectionKind.EtfFlows => MaterialIconKind.SwapVerticalBold,
        _ => MaterialIconKind.Bank,
    };

    private Control Build()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*") };

        // ---- lista
        var add = new Button { Content = Ui.H(6, Ui.Icon(MaterialIconKind.Plus, 16), new TextBlock { Text = "Nueva conexión" }), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var menu = new MenuFlyout();
        foreach (var kind in Enum.GetValues<ConnectionKind>())
        {
            // qué da cada una, para elegir sin tener que saberlo
            string? hint = kind switch
            {
                ConnectionKind.Tradovate => "futuros: cuentas para operar y datos",
                ConnectionKind.CTrader => "CFDs y forex de IC Markets, Pepperstone, FxPro…",
                ConnectionKind.Oanda => "CFDs de índices, oro y forex (práctica gratis)",
                ConnectionKind.Alpaca => "acciones y ETFs de EE.UU. (SPY, QQQ) gratis",
                ConnectionKind.Databento => "historia de futuros de CME desde 2010",
                ConnectionKind.Longbridge => "acciones de Hong Kong, EE.UU. y China (entrás con tu cuenta)",
                ConnectionKind.Aggregate => "Binance + Bybit sumados (volumen y delta agregados)",
                ConnectionKind.OptionsData => "cadena, operaciones e historia de opciones para sus indicadores",
                ConnectionKind.EtfFlows => "flujo diario de los ETF al contado (BTC, ETH…)",
                _ => null,
            };
            object itemHeader = hint == null ? Ui.EnumName(kind) : Ui.V(1, new TextBlock { Text = Ui.EnumName(kind) }, new TextBlock { Text = hint, Classes = { "faint", "small" } });
            var mi = new MenuItem { Header = itemHeader, Icon = Ui.Icon(IconOf(kind), 16) };
            mi.Click += (_, _) => AddProfile(kind);
            menu.Items.Add(mi);
        }

        add.Flyout = menu;
        var header = Ui.V(4, Ui.H1("Conexiones"), Ui.Muted("Datos de mercado y cuentas para operar"));
        header.Margin = new Thickness(0, 0, 0, 16);
        var left = new DockPanel { Margin = new Thickness(20, 20, 12, 20) };
        DockPanel.SetDock(header, Dock.Top);
        left.Children.Add(header);
        DockPanel.SetDock(add, Dock.Bottom);
        add.Margin = new Thickness(0, 12, 0, 0);
        left.Children.Add(add);
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: ConnectionProfile p }) Select(p);
        };
        _list.Background = Brushes.Transparent;
        left.Children.Add(_list);
        grid.Children.Add(new Border { Child = left, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) });

        // ---- detalle
        _detail.Margin = new Thickness(28, 24, 28, 32);
        var scroll = new ScrollViewer { Content = _detail };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        return grid;
    }

    private void FillList()
    {
        var keep = _selected?.Id;
        _list.Items.Clear();
        foreach (var p in _svc.Connections.Profiles)
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            dot.Fill = !p.Enabled ? Ui.Text2 : _results.TryGetValue(p.Id, out var r) ? (r.Ok ? Ui.Up : Ui.Down) : Ui.Line;
            ToolTip.SetTip(dot, !p.Enabled ? "Desactivada" : _results.TryGetValue(p.Id, out var r2) ? r2.Message : "Sin probar");
            var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = Ui.Bg3, Child = Ui.Icon(IconOf(p.Kind), 18, p.Enabled ? Ui.Accent : Ui.Text2) };
            string sub = Describe(p);
            var text = Ui.V(1, new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold, Opacity = p.Enabled ? 1 : 0.6 }, new TextBlock { Text = sub, Classes = { "muted", "small" } });
            var row = new DockPanel { Margin = new Thickness(2, 4) };
            DockPanel.SetDock(icon, Dock.Left);
            icon.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(dot, Dock.Right);
            row.Children.Add(icon);
            row.Children.Add(dot);
            row.Children.Add(text);
            _list.Items.Add(new ListBoxItem { Content = row, Tag = p });
        }

        var sel = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((ConnectionProfile)i.Tag!).Id == keep) ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault();
        _list.SelectedItem = sel;
    }

    /// <summary>Resumen de una conexión para la lista.</summary>
    private string Describe(ConnectionProfile p)
    {
        string prefix = p.Name == Ui.EnumName(p.Kind) ? "" : Ui.EnumName(p.Kind) + " · ";
        if (!p.Enabled) return prefix + "desactivada";
        string detail = p.Kind switch
        {
            ConnectionKind.Simulator => "mercado simulado",
            ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot => string.IsNullOrWhiteSpace(p.ApiKey)
                ? "datos públicos · sin cuenta"
                : p.Testnet ? "datos + cuenta testnet" : p.AllowRealOrders ? "datos + cuenta REAL" : "datos + cuenta real (solo lectura)",
            ConnectionKind.Bybit => string.IsNullOrWhiteSpace(p.ApiKey)
                ? "datos públicos · sin cuenta"
                : p.BybitEnv == TradingHub.Feeds.Bybit.BybitEnvironment.Real
                    ? p.AllowRealOrders ? "datos + cuenta REAL" : "datos + cuenta real (solo lectura)"
                    : $"datos + cuenta {AppServices.BybitEnvLabel(p.BybitEnv)}",
            ConnectionKind.Rithmic or ConnectionKind.AmpFutures => string.IsNullOrWhiteSpace(p.RithmicUser) ? "sin usuario configurado" : $"{p.RithmicUser} · {p.RithmicSystem}",
            ConnectionKind.Tradovate => string.IsNullOrWhiteSpace(p.TradovateUser) ? "sin usuario configurado" : $"{p.TradovateUser} · {(p.Testnet ? "demo" : p.AllowRealOrders ? "REAL" : "real (solo lectura)")}",
            ConnectionKind.CTrader => string.IsNullOrWhiteSpace(p.CTraderClientId) ? "sin aplicación configurada" : $"{(p.Testnet ? "demo" : "real")}{(p.CTraderLogin > 0 ? " · cuenta " + p.CTraderLogin : "")}",
            ConnectionKind.Oanda => (p.Testnet ? "práctica" : "real") + (string.IsNullOrWhiteSpace(p.OandaAccount) ? "" : " · " + p.OandaAccount),
            ConnectionKind.Alpaca => string.IsNullOrWhiteSpace(p.ApiKey) ? "sin claves" : $"historia SIP · en vivo {p.AlpacaLiveFeed.ToUpperInvariant()}",
            ConnectionKind.Databento => "historia de futuros de CME (pago por uso)",
            ConnectionKind.Longbridge => string.IsNullOrWhiteSpace(_svc.Connections.GetSecret(p, "token")) ? "sin autorizar (entrá con tu cuenta)" : $"{(p.LongbridgeRegion == "cn" ? "China continental" : "global")} · {p.LongbridgeSymbols.Count} símbolos",
            ConnectionKind.Aggregate => string.Join(" + ", p.AggregateSources.Select(AggregateSourceName)),
            ConnectionKind.OptionsData => p.OptionsSource == OptionsSource.Deribit ? "Deribit · BTC y ETH" : string.IsNullOrWhiteSpace(p.ExternalUrl) ? "sin dirección configurada" : "API propia · " + HostOf(p.ExternalUrl),
            ConnectionKind.EtfFlows => string.IsNullOrWhiteSpace(p.ExternalUrl) ? "sin dirección configurada" : "API · " + HostOf(p.ExternalUrl),
            _ => $"{p.IbHost}:{p.IbPort}",
        };
        return prefix + detail;
    }

    private static string HostOf(string url) => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? u.Host : url.Trim();

    private void AddProfile(ConnectionKind kind)
    {
        int n = _svc.Connections.Profiles.Count(p => p.Kind == kind) + 1;
        var p = new ConnectionProfile { Kind = kind, Name = Ui.EnumName(kind) + (n > 1 ? $" {n}" : ""), Enabled = kind is not (ConnectionKind.Rithmic or ConnectionKind.AmpFutures) };
        if (kind is ConnectionKind.CTrader) p.Name = n > 1 ? $"cTrader {n}" : "cTrader";
        if (kind is ConnectionKind.Alpaca) p.Name = n > 1 ? $"Alpaca {n}" : "Alpaca";
        if (kind is ConnectionKind.Databento) p.Name = "Databento";
        if (kind is ConnectionKind.Longbridge) p.Name = n > 1 ? $"长桥 {n}" : "长桥";
        if (kind is ConnectionKind.Aggregate) p.Name = n > 1 ? $"Cripto agregado {n}" : "Cripto agregado";
        // el primero de cada tipo usa el id de sus datos (así los archivos de ticks quedan con un nombre claro)
        string defaultId = ConnectionProfile.DefaultId(kind);
        if (kind is ConnectionKind.Tradovate or ConnectionKind.CTrader or ConnectionKind.Oanda or ConnectionKind.Alpaca or ConnectionKind.Databento or ConnectionKind.Longbridge or ConnectionKind.Aggregate
                or ConnectionKind.OptionsData or ConnectionKind.EtfFlows
            && _svc.Connections.Profiles.All(x => x.Id != defaultId))
            p.Id = defaultId;
        if (kind == ConnectionKind.Simulator) p.HistoryHours = 6;
        if (kind == ConnectionKind.AmpFutures)
        {
            // AMP da cuentas de Rithmic: servidor de producción (Chicago) y el sistema de la demo por defecto
            p.RithmicUri = RithmicGateways.ChicagoUri;
            p.RithmicSystem = "Rithmic Paper Trading";
        }
        _svc.Connections.Profiles.Add(p);
        _svc.Connections.Save();
        _selected = p;
        FillList();
    }

    // ===================================================================== detalle

    private void Select(ConnectionProfile p)
    {
        _selected = p;
        _detail.Content = BuildDetail(p);
    }

    private Control BuildDetail(ConnectionProfile p)
    {
        var name = new TextBox { Text = p.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, MinWidth = 170, MaxWidth = 300 };
        var enabled = new ToggleSwitch { IsChecked = p.Enabled, OnContent = "Activada", OffContent = "Desactivada" };
        var history = new ComboBox { MinWidth = 160 };
        foreach (var (h, label) in new[] { (0.5, "30 minutos"), (1.0, "1 hora"), (1.5, "1,5 horas"), (3.0, "3 horas"), (6.0, "6 horas"), (12.0, "12 horas"), (24.0, "1 día"), (72.0, "3 días"), (168.0, "7 días"), (ChartToolbar.AllHistory, "Todo el historial") })
            history.Items.Add(new ComboBoxItem { Content = label, Tag = h });
        history.SelectedItem = history.Items.OfType<ComboBoxItem>().OrderBy(i => Math.Abs((double)i.Tag! - p.HistoryHours)).First();

        var resultIcon = new ContentControl();
        var resultText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var resultBox = new Border { Child = Ui.H(10, resultIcon, resultText), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8), Background = Ui.Bg2, IsVisible = false };
        void ShowResult(bool ok, string msg, bool running = false)
        {
            resultBox.IsVisible = true;
            resultIcon.Content = running ? Ui.Icon(MaterialIconKind.ProgressClock, 18, Ui.Text1) : Ui.Icon(ok ? MaterialIconKind.CheckCircle : MaterialIconKind.CloseCircle, 18, ok ? Ui.Up : Ui.Down);
            resultText.Text = msg;
        }

        if (_results.TryGetValue(p.Id, out var last)) ShowResult(last.Ok, $"{last.Message}  ({Ui.Ago(last.When)})");

        // campos según el tipo (se escriben en el perfil al guardar)
        var apply = new List<Action>();
        var sections = new StackPanel { Spacing = 16 };

        var dataSection = Ui.V(14,
            Ui.H2("Datos de mercado"),
            Ui.Field("Historia al abrir un gráfico", history, "Lo grabado o descargado en Datos carga al instante; el resto se pide a la conexión."));
        var dataCard = Ui.Card(dataSection);
        sections.Children.Add(dataCard);
        apply.Add(() => p.HistoryHours = (double)((ComboBoxItem)history.SelectedItem!).Tag!);

        switch (p.Kind)
        {
            case ConnectionKind.Simulator:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "Mercado simulado con libro y operaciones realistas, ideal para probar sin conexión. No necesita configuración."));
                break;
            case ConnectionKind.BinanceFutures:
            case ConnectionKind.BinanceSpot:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "Los datos de mercado de Binance son públicos: no hace falta cuenta para ver gráficos, DOM y cinta."));
                sections.Children.Add(Ui.Card(BinanceAccount(p, apply)));
                break;
            case ConnectionKind.Bybit:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "Los datos de los perpetuos lineales (USDT) de Bybit son públicos: no hace falta cuenta para ver gráficos, DOM y cinta."));
                sections.Children.Add(Ui.Card(BybitAccount(p, apply)));
                break;
            case ConnectionKind.Rithmic:
            case ConnectionKind.AmpFutures:
                sections.Children.Add(Ui.Card(RithmicSection(p, apply)));
                break;
            case ConnectionKind.InteractiveBrokers:
                sections.Children.Add(Ui.Card(IbSection(p, apply)));
                break;
            case ConnectionKind.Tradovate:
                sections.Children.Add(Ui.Card(TradovateSection(p, apply)));
                break;
            case ConnectionKind.CTrader:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "Los CFDs no tienen operaciones reales: cada cotización (bid) cuenta como una operación de volumen 1. Sirve para precio, velas, replay y backtests; el footprint y el delta son aproximados."));
                sections.Children.Add(Ui.Card(CTraderSection(p, apply)));
                break;
            case ConnectionKind.Oanda:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "Los CFDs no tienen operaciones reales: cada cotización (precio medio) cuenta como una operación de volumen 1. Sirve para precio, velas, replay y backtests; el footprint y el delta son aproximados."));
                sections.Children.Add(Ui.Card(OandaSection(p, apply)));
                break;
            case ConnectionKind.Alpaca:
                sections.Children.Add(Ui.Card(AlpacaSection(p, apply)));
                break;
            case ConnectionKind.Databento:
                dataCard.IsVisible = false;
                sections.Children.Add(Ui.Card(DatabentoSection(p, apply)));
                break;
            case ConnectionKind.Longbridge:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "长桥 da el tape del día (con el lado de cada operación) y velas de años. Lo que no tiene ticks se arma con las velas, repartiendo el volumen por la forma de cada vela (delta estimado)."));
                sections.Children.Add(Ui.Card(LongbridgeSection(p, apply)));
                break;
            case ConnectionKind.Aggregate:
                sections.Children.Add(Ui.Card(AggregateSection(p, apply)));
                break;
            case ConnectionKind.OptionsData:
                dataCard.IsVisible = false;
                sections.Children.Add(Ui.Card(OptionsDataSection(p, apply)));
                break;
            case ConnectionKind.EtfFlows:
                dataCard.IsVisible = false;
                sections.Children.Add(Ui.Card(EtfFlowsSection(p, apply)));
                break;
        }

        // ---- encabezado y acciones
        var test = Ui.Button("Probar conexión", MaterialIconKind.LanConnect, () => { });
        var save = Ui.Button("Guardar", MaterialIconKind.ContentSaveOutline, () => { }, "primary");
        var delete = Ui.Button("Eliminar", MaterialIconKind.DeleteOutline, () => { }, "ghost");
        delete.IsVisible = _svc.Connections.Profiles.Count(x => x.Kind == p.Kind) > 1 || p.Kind is ConnectionKind.InteractiveBrokers or ConnectionKind.AmpFutures
            or ConnectionKind.Tradovate or ConnectionKind.CTrader or ConnectionKind.Oanda or ConnectionKind.Alpaca or ConnectionKind.Databento or ConnectionKind.Longbridge or ConnectionKind.Aggregate
            or ConnectionKind.OptionsData or ConnectionKind.EtfFlows;

        void Save()
        {
            p.Name = string.IsNullOrWhiteSpace(name.Text) ? Ui.EnumName(p.Kind) : name.Text.Trim();
            p.Enabled = enabled.IsChecked == true;
            foreach (var a in apply) a();
            _svc.Connections.Save();
            _svc.Connections.Invalidate(p);
            InstrumentPicker.ClearCache(p.Id);
            _status($"Conexión \"{p.Name}\" guardada");
        }

        save.Click += (_, _) => Save();
        test.Click += async (_, _) =>
        {
            Save();
            _testCts?.Cancel();
            _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            test.IsEnabled = false;
            ShowResult(true, "Probando…", running: true);
            try
            {
                var (ok, msg) = await _svc.Connections.TestAsync(p, _testCts.Token);
                _results[p.Id] = (ok, msg, DateTime.UtcNow);
                ShowResult(ok, msg);
                FillList();
            }
            finally
            {
                test.IsEnabled = true;
            }
        };
        delete.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            if (!await ConfirmDialog.ShowAsync(w, "Eliminar conexión", $"Se borra \"{p.Name}\" y sus secretos {(Os.IsWindows ? "del Administrador de credenciales" : "del Llavero")}. Los datos grabados no se tocan.", "Eliminar", danger: true)) return;
            _svc.Connections.ClearSecrets(p);
            _svc.Connections.Invalidate(p);
            _svc.Connections.Profiles.Remove(p);
            _svc.Connections.Save();
            _selected = null;
            FillList();
        };

        var kindBadge = new Border { Child = Ui.H(6, Ui.Icon(IconOf(p.Kind), 14, Ui.Accent), new TextBlock { Text = Ui.EnumName(p.Kind), FontSize = 12 }), Classes = { "pill" } };
        // con el mismo nombre que el tipo, la etiqueta no agrega nada (y en las largas tapaba el interruptor)
        kindBadge.IsVisible = p.Name != Ui.EnumName(p.Kind);
        var titleRow = new DockPanel();
        // el interruptor va con las acciones: en ventanas angostas no se superpone con «Eliminar»
        enabled.VerticalAlignment = VerticalAlignment.Center;
        enabled.Margin = new Thickness(0, 0, 8, 0);
        var actions = Ui.H(8, enabled, delete, test, save);
        DockPanel.SetDock(actions, Dock.Right);
        titleRow.Children.Add(actions);
        var left = Ui.H(12, name, kindBadge);
        left.ClipToBounds = true;
        titleRow.Children.Add(left);

        return new StackPanel { Spacing = 16, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left, Children = { titleRow, resultBox, sections } };
    }

    private static Control Note(MaterialIconKind icon, string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center };
        var d = new DockPanel();
        var i = Ui.Icon(icon, 16, Ui.Text2);
        i.Margin = new Thickness(0, 0, 8, 0);
        i.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(i, Dock.Left);
        d.Children.Add(i);
        d.Children.Add(t);
        return d;
    }

    private static Grid TwoColumns(params Control[] fields)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        int row = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            if (i % 2 == 0)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                if (i > 0) g.RowDefinitions.Add(new RowDefinition(new GridLength(14)));
                row = g.RowDefinitions.Count - 1;
            }

            Grid.SetRow(fields[i], row);
            Grid.SetColumn(fields[i], i % 2 == 0 ? 0 : 2);
            g.Children.Add(fields[i]);
        }

        return g;
    }

    private Control BinanceAccount(ConnectionProfile p, List<Action> apply)
    {
        var testnet = new ToggleSwitch { IsChecked = p.Testnet, OnContent = "Testnet (dinero de prueba)", OffContent = "Cuenta real" };
        var key = new TextBox { Text = p.ApiKey, Watermark = "API key" };
        var secret = new TextBox { PasswordChar = '•', Watermark = string.IsNullOrEmpty(_svc.Connections.GetSecret(p)) ? "API secret" : $"•••••••• (guardado en {Os.SecretStoreShort})" };
        var allowReal = new CheckBox { Content = "Permitir órdenes REALES desde Trading Hub", IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = "Pedir confirmación antes de cada orden real", IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Increment = 0.001m, FormatString = "0.#####", Watermark = "sin límite" };
        var warn = Note(MaterialIconKind.AlertOutline, "Con una cuenta real las órdenes mueven dinero de verdad. Por defecto Trading Hub solo opera en testnet; activá las órdenes reales únicamente si sabés lo que hacés.");
        void UpdateEnabled()
        {
            bool real = testnet.IsChecked != true;
            allowReal.IsEnabled = real;
            confirm.IsEnabled = real && allowReal.IsChecked == true;
            maxQty.IsEnabled = real && allowReal.IsChecked == true;
            warn.IsVisible = real;
        }

        testnet.IsCheckedChanged += (_, _) => UpdateEnabled();
        allowReal.IsCheckedChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();

        apply.Add(() =>
        {
            p.Testnet = testnet.IsChecked == true;
            p.ApiKey = (key.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            p.AllowRealOrders = !p.Testnet && allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked != false;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });

        var help = new Button { Content = "¿Cómo creo una API key?", Classes = { "ghost" }, Padding = new Thickness(0), FontSize = 12, Foreground = Ui.Focus };
        help.Click += (_, _) => Shell.Open(testnet.IsChecked == true
            ? (p.Kind == ConnectionKind.BinanceFutures ? "https://testnet.binancefuture.com" : "https://testnet.binance.vision")
            : "https://www.binance.com/es/my/settings/api-management");

        return Ui.V(14,
            Ui.H2("Cuenta para operar"),
            Note(MaterialIconKind.ShieldKeyOutline, "Creá una API key solo con permiso de trading (sin retiros). El secret se guarda en " + (Os.IsWindows ? "el Administrador de credenciales de Windows." : "el Llavero de macOS.")),
            testnet,
            TwoColumns(Ui.Field("API key", key), Ui.Field("API secret", secret)),
            help,
            Ui.Divider(),
            allowReal,
            confirm,
            Ui.Field("Cantidad máxima por orden (0 = sin límite)", maxQty),
            warn);
    }

    private Control BybitAccount(ConnectionProfile p, List<Action> apply)
    {
        var env = new ComboBox { MinWidth = 320 };
        foreach (var (value, label) in new[]
                 {
                     (TradingHub.Feeds.Bybit.BybitEnvironment.Demo, "Demo Trading (mercado real, dinero de prueba)"),
                     (TradingHub.Feeds.Bybit.BybitEnvironment.Testnet, "Testnet (testnet.bybit.com)"),
                     (TradingHub.Feeds.Bybit.BybitEnvironment.Real, "Cuenta real"),
                 })
            env.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        env.SelectedItem = env.Items.OfType<ComboBoxItem>().First(i => (TradingHub.Feeds.Bybit.BybitEnvironment)i.Tag! == p.BybitEnv);
        var key = new TextBox { Text = p.ApiKey, Watermark = "API key" };
        var secret = new TextBox { PasswordChar = '•', Watermark = string.IsNullOrEmpty(_svc.Connections.GetSecret(p)) ? "API secret" : $"•••••••• (guardado en {Os.SecretStoreShort})" };
        var server = new TextBox { Text = p.BybitServer, Watermark = "vacío = el servidor de Bybit para ese entorno" };
        var allowReal = new CheckBox { Content = "Permitir órdenes REALES desde Trading Hub", IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = "Pedir confirmación antes de cada orden real", IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Increment = 0.001m, FormatString = "0.#####", Watermark = "sin límite" };
        var warn = Note(MaterialIconKind.AlertOutline, "Con la cuenta real las órdenes mueven dinero de verdad. Probá primero con Demo Trading; activá las órdenes reales únicamente si sabés lo que hacés.");
        TradingHub.Feeds.Bybit.BybitEnvironment Selected() => env.SelectedItem is ComboBoxItem { Tag: TradingHub.Feeds.Bybit.BybitEnvironment e } ? e : TradingHub.Feeds.Bybit.BybitEnvironment.Demo;
        void UpdateEnabled()
        {
            bool real = Selected() == TradingHub.Feeds.Bybit.BybitEnvironment.Real;
            allowReal.IsEnabled = real;
            confirm.IsEnabled = real && allowReal.IsChecked == true;
            maxQty.IsEnabled = real && allowReal.IsChecked == true;
            warn.IsVisible = real;
        }

        env.SelectionChanged += (_, _) => UpdateEnabled();
        allowReal.IsCheckedChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();

        apply.Add(() =>
        {
            p.BybitEnv = Selected();
            p.Testnet = p.BybitEnv != TradingHub.Feeds.Bybit.BybitEnvironment.Real;
            p.ApiKey = (key.Text ?? "").Trim();
            p.BybitServer = (server.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            p.AllowRealOrders = !p.Testnet && allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked != false;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });

        var help = new Button { Content = "¿Cómo creo una API key?", Classes = { "ghost" }, Padding = new Thickness(0), FontSize = 12, Foreground = Ui.Focus };
        help.Click += (_, _) => Shell.Open(Selected() == TradingHub.Feeds.Bybit.BybitEnvironment.Testnet
            ? "https://testnet.bybit.com/app/user/api-management"
            : "https://www.bybit.com/app/user/api-management");

        return Ui.V(14,
            Ui.H2("Cuenta para operar"),
            Note(MaterialIconKind.ShieldKeyOutline, "Creá una API key de sistema (HMAC) solo con permiso de Contratos → Órdenes y Posiciones (sin retiros). Para Demo Trading, creala con Bybit en modo Demo. Trading Hub opera los perpetuos USDT de la cuenta unificada (UTA) en modo unidireccional (one-way). El secret se guarda en " + (Os.IsWindows ? "el Administrador de credenciales de Windows." : "el Llavero de macOS.")),
            Ui.Field("Entorno", env),
            TwoColumns(Ui.Field("API key", key), Ui.Field("API secret", secret)),
            help,
            Ui.Field("Servidor (opcional; por ejemplo https://api.bytick.com si api.bybit.com no responde desde tu red)", server),
            Ui.Divider(),
            allowReal,
            confirm,
            Ui.Field("Cantidad máxima por orden (0 = sin límite)", maxQty),
            warn);
    }

    private static string RithmicCacheFile => Path.Combine(AppPaths.Root, "rithmic-servidores.json");

    private static List<RithmicServer> LoadRithmicCache()
    {
        try
        {
            if (File.Exists(RithmicCacheFile))
                return System.Text.Json.JsonSerializer.Deserialize<List<RithmicServer>>(File.ReadAllText(RithmicCacheFile)) ?? new();
        }
        catch
        {
            // caché dañada: se vuelve a pedir
        }

        return new List<RithmicServer>();
    }

    /// <summary>Rithmic como en ATAS: usuario, contraseña, un solo «Servidor» (sistema. región), cotizaciones agregadas y tiempo de espera.</summary>
    private Control RithmicSection(ConnectionProfile p, List<Action> apply)
    {
        var servers = LoadRithmicCache();
        var search = new TextBox { Watermark = "Buscar servidor (paper, 01, Apex, Frankfurt, Sao Paulo…)" };
        var server = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, MaxDropDownHeight = 420, PlaceholderText = "Elegí el servidor" };
        var info = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        var refresh = Ui.IconButton(MaterialIconKind.Refresh, "Volver a pedir la lista a todos los servidores", () => { }, 16);
        refresh.Margin = new Thickness(6, 0, 0, 0);
        string selSystem = p.RithmicSystem, selUri = p.RithmicUri;

        // servidor a mano (por si Rithmic agrega uno nuevo)
        var customUri = new TextBox { Text = p.RithmicUri, Watermark = "wss://servidor:443" };
        var customSystem = new TextBox { Text = p.RithmicSystem, Watermark = "Sistema (ej. Rithmic 01)" };
        var customToggle = new CheckBox { Content = "Usar un servidor que no está en la lista" };
        var customPanel = TwoColumns(Ui.Field("Dirección", customUri), Ui.Field("Sistema", customSystem));
        customPanel.IsVisible = false;
        customToggle.IsCheckedChanged += (_, _) => customPanel.IsVisible = customToggle.IsChecked == true;

        void Fill()
        {
            var words = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var list = servers.Where(sv => words.All(w => sv.Display.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            int fastest = servers.Count > 0 ? servers.Min(x => x.LatencyMs) : 0;
            server.Items.Clear();
            ComboBoxItem? selected = null;
            foreach (var sv in list)
            {
                var item = new ComboBoxItem
                {
                    Tag = sv,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new DockPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = $"{sv.LatencyMs} ms", Classes = { "faint", "small" }, Foreground = sv.LatencyMs <= fastest * 1.3 ? Ui.Up : null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }.Also(t => DockPanel.SetDock(t, Dock.Right)),
                            new TextBlock { Text = sv.Display, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                };
                server.Items.Add(item);
                if (sv.System == selSystem && sv.Uri == selUri) selected = item;
            }

            if (selected == null && words.Length == 0)
            {
                // lo guardado no está en la lista (o todavía no llegó): se muestra igual
                var cur = new RithmicServer(selSystem, RithmicGateways.NameOf(selUri), selUri, 0);
                selected = new ComboBoxItem { Tag = cur, Content = new TextBlock { Text = cur.Display } };
                server.Items.Insert(0, selected);
            }

            server.SelectedItem = selected;
        }

        var aggNote = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        void UpdateAggregated()
        {
            var known = servers.FirstOrDefault(x => x.System == selSystem && x.Uri == selUri);
            aggNote.Text = known == null ? ""
                : known.Aggregated ? "Este sistema ofrece cotizaciones agregadas."
                : "Este sistema no ofrece cotizaciones agregadas: se conecta sin ellas (pedirlas igual hace que Rithmic rechace el login).";
        }

        server.SelectionChanged += (_, _) =>
        {
            if (server.SelectedItem is ComboBoxItem { Tag: RithmicServer sv })
            {
                selSystem = sv.System;
                selUri = sv.Uri;
                UpdateAggregated();
            }
        };
        search.TextChanged += (_, _) => Fill();

        async Task DiscoverAsync()
        {
            info.Text = "Consultando los servidores de Rithmic…";
            refresh.IsEnabled = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var found = await RithmicDirectory.DiscoverAsync(cts.Token);
                if (found.Count > 0)
                {
                    servers = found;
                    try
                    {
                        Directory.CreateDirectory(AppPaths.Root);
                        File.WriteAllText(RithmicCacheFile, System.Text.Json.JsonSerializer.Serialize(found));
                    }
                    catch
                    {
                        // sin caché no pasa nada
                    }
                }

                Fill();
                UpdateAggregated();
                var best = servers.GroupBy(x => x.Gateway).Select(g => g.First()).OrderBy(x => x.LatencyMs).FirstOrDefault();
                info.Text = servers.Count == 0
                    ? "No respondió ningún servidor (¿sin internet?)."
                    : $"{servers.Count} combinaciones en {servers.Select(x => x.Uri).Distinct().Count()} servidores. Lo más rápido desde acá: {best?.Gateway} ({best?.LatencyMs} ms). Colo 75 solo funciona desde el datacenter de la CME.";
            }
            catch (Exception ex)
            {
                info.Text = "No se pudo consultar: " + ex.Message;
            }
            finally
            {
                refresh.IsEnabled = true;
            }
        }

        refresh.Click += async (_, _) => await DiscoverAsync();
        var serverRow = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        serverRow.Children.Add(refresh);
        serverRow.Children.Add(server);

        var user = new TextBox { Text = p.RithmicUser, Watermark = "usuario (Login)" };
        var pass = new TextBox { PasswordChar = '•', RevealPassword = false, Watermark = string.IsNullOrEmpty(_svc.Connections.GetSecret(p)) ? "contraseña" : $"•••••••• (guardada en {Os.SecretStoreShort})" };
        var reveal = new ToggleButton { Content = Ui.Icon(MaterialIconKind.EyeOutline, 15), Padding = new Thickness(6, 2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch };
        ToolTip.SetTip(reveal, "Mostrar lo que escribís");
        reveal.IsCheckedChanged += (_, _) => pass.RevealPassword = reveal.IsChecked == true;
        var passRow = new DockPanel();
        DockPanel.SetDock(reveal, Dock.Right);
        reveal.Margin = new Thickness(6, 0, 0, 0);
        passRow.Children.Add(reveal);
        passRow.Children.Add(pass);

        var aggregated = new CheckBox { Content = "Cotizaciones agregadas", IsChecked = p.RithmicAggregatedQuotes };
        ToolTip.SetTip(aggregated, "Rithmic junta las actualizaciones del libro que llegan en el mismo instante (menos mensajes, mismo precio). Igual que la opción de ATAS.");
        var timeout = new NumericUpDown { Value = p.RithmicTimeoutMs, Minimum = 2000, Maximum = 120000, Increment = 1000, FormatString = "0", Width = 140 };
        var app = new TextBox { Text = p.RithmicAppName, Watermark = "TradingHub" };
        var products = new TextBox { Text = string.Join(", ", p.RithmicProducts), Watermark = "ES, NQ, MES, MNQ, CL…" };

        // órdenes: las cuentas de práctica operan siempre; las reales (Rithmic 01, prop firms) solo si se habilita acá
        var allowReal = new CheckBox { Content = "Permitir órdenes en cuentas reales (Rithmic 01, prop firms…)", IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = "Confirmar cada orden real", IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Maximum = 10000, Increment = 1, FormatString = "0", Width = 140 };

        apply.Add(() =>
        {
            if (customToggle.IsChecked == true && !string.IsNullOrWhiteSpace(customUri.Text))
            {
                p.RithmicUri = customUri.Text.Trim();
                if (!string.IsNullOrWhiteSpace(customSystem.Text)) p.RithmicSystem = customSystem.Text.Trim();
            }
            else
            {
                p.RithmicUri = selUri;
                p.RithmicSystem = selSystem;
            }

            p.RithmicUser = (user.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(pass.Text)) _svc.Connections.SetSecret(p, pass.Text);
            pass.Text = "";
            p.RithmicAggregatedQuotes = aggregated.IsChecked == true;
            p.RithmicTimeoutMs = (int)(timeout.Value ?? 10000);
            p.RithmicAppName = string.IsNullOrWhiteSpace(app.Text) ? "TradingHub" : app.Text.Trim();
            p.RithmicProducts = (products.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).Distinct().ToList();
            p.AllowRealOrders = allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked == true;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });

        Fill();
        Dispatcher.UIThread.Post(() => _ = DiscoverAsync(), DispatcherPriority.Background);
        return Ui.V(14,
            Ui.H2(p.Kind == ConnectionKind.AmpFutures ? "Cuenta AMP Futures" : "Cuenta Rithmic"),
            Note(MaterialIconKind.InformationOutline, p.Kind == ConnectionKind.AmpFutures
                ? "AMP Futures opera sobre Rithmic: AMP te manda usuario y contraseña de Rithmic. Para la demo elegí «Rithmic Paper Trading» y para la cuenta real «Rithmic 01», en el servidor más cercano. Trading Hub se conecta directo por R|Protocol (no hace falta tener R|Trader Pro abierto)."
                : "Conexión directa por R|Protocol, la API oficial de Rithmic (no hace falta R|Trader Pro). Elegí el servidor como en ATAS: sistema (Paper Trading, Rithmic 01, tu prop firm) y región."),
            Ui.H3("Conexión"),
            TwoColumns(Ui.Field("Usuario", user), Ui.Field("Contraseña", passRow)),
            Ui.Field("Servidor", Ui.V(8, search, serverRow, info)),
            customToggle,
            customPanel,
            Ui.H3("Datos de mercado"),
            TwoColumns(Ui.V(4, aggregated, aggNote), Ui.Field("Tiempo de espera de conexión (ms)", timeout)),
            Ui.H3("Operar"),
            Note(MaterialIconKind.ShieldCheckOutline, "Tus cuentas de Rithmic aparecen en el Chart Trader («Cuenta») cuando la conexión está activa. Las de práctica (Paper Trading) operan directo; las reales necesitan esta autorización y piden confirmación."),
            allowReal,
            TwoColumns(confirm, Ui.Field("Cantidad máxima por orden (0 = sin límite)", maxQty)),
            Ui.H3("Avanzado"),
            TwoColumns(Ui.Field("Nombre de la aplicación", app, "Algunos sistemas exigen un nombre de app aprobado."), Ui.Field("Productos para el buscador", products, "Se busca el contrato del mes de cada producto (ej. ES → ESZ6).")));
    }

    /// <summary>Campo de secreto: no muestra lo guardado, solo avisa que está en el Llavero (o en Windows).</summary>
    private TextBox SecretBox(ConnectionProfile p, string? name, string watermark) =>
        new() { PasswordChar = '•', Watermark = string.IsNullOrEmpty(name == null ? _svc.Connections.GetSecret(p) : _svc.Connections.GetSecret(p, name)) ? watermark : $"•••••••• (guardado en {Os.SecretStoreShort})" };

    private static Button Link(string text, string url)
    {
        var b = new Button { Content = text, Classes = { "ghost" }, Padding = new Thickness(0), FontSize = 12, Foreground = Ui.Focus };
        b.Click += (_, _) => Shell.Open(url);
        ToolTip.SetTip(b, url);
        return b;
    }

    /// <summary>Tradovate: usuario, contraseña y API key (CID + secret); demo o real; productos.</summary>
    private Control TradovateSection(ConnectionProfile p, List<Action> apply)
    {
        var demo = new ToggleSwitch { IsChecked = p.Testnet, OnContent = "Demo (simulada de Tradovate)", OffContent = "Cuenta real" };
        var user = new TextBox { Text = p.TradovateUser, Watermark = "usuario de Tradovate" };
        var pass = SecretBox(p, null, "contraseña");
        var cid = new TextBox { Text = p.TradovateCid, Watermark = "CID (número)" };
        var sec = SecretBox(p, "apisecret", "secret de la API key");
        var products = new TextBox { Text = string.Join(", ", p.TradovateProducts), Watermark = "ES, NQ, MES, MNQ, CL…" };
        var allowReal = new CheckBox { Content = "Permitir órdenes en cuentas reales de Tradovate", IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = "Pedir confirmación antes de cada orden real", IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Maximum = 10000, Increment = 1, FormatString = "0", Width = 140 };
        void Update()
        {
            allowReal.IsEnabled = demo.IsChecked != true;
            confirm.IsEnabled = demo.IsChecked != true && allowReal.IsChecked == true;
        }

        allowReal.IsCheckedChanged += (_, _) => Update();
        demo.IsCheckedChanged += (_, _) => Update();
        Update();
        apply.Add(() =>
        {
            p.Testnet = demo.IsChecked == true;
            p.TradovateUser = (user.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(pass.Text)) _svc.Connections.SetSecret(p, pass.Text);
            pass.Text = "";
            p.TradovateCid = (cid.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(sec.Text)) _svc.Connections.SetSecret(p, "apisecret", sec.Text.Trim());
            sec.Text = "";
            p.TradovateProducts = (products.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).Distinct().ToList();
            p.AllowRealOrders = !p.Testnet && allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked != false;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });
        return Ui.V(14,
            Ui.H2("Cuenta Tradovate"),
            Note(MaterialIconKind.InformationOutline, "Conexión directa a la API oficial de Tradovate. Tus cuentas aparecen en el Chart Trader y en Cuenta, y se pueden operar desde cualquier gráfico de futuros de CME (también desde uno de Rithmic). Las demo operan directo; las reales necesitan la autorización de abajo."),
            Note(MaterialIconKind.AlertOutline, "Datos de mercado por API: Tradovate solo los da si tu cuenta tiene habilitados los datos de CME para API (una licencia aparte de la CME). Sin eso igual podés operar, con los gráficos de Rithmic u otra conexión."),
            demo,
            TwoColumns(Ui.Field("Usuario", user), Ui.Field("Contraseña", pass)),
            TwoColumns(Ui.Field("CID de la API key", cid), Ui.Field("Secret de la API key", sec)),
            Ui.H(16, Link("¿Dónde saco la API key? (Settings → API Access)", "https://trader.tradovate.com/"), Link("Documentación de la API", "https://partner.tradovate.com")),
            Ui.Field("Productos para el buscador", products, "Se usa el contrato del mes de cada uno (ES → ESZ6)."),
            Ui.Divider(),
            allowReal,
            confirm,
            Ui.Field("Cantidad máxima por orden (0 = sin límite)", maxQty),
            Note(MaterialIconKind.ShieldCheckOutline, "Tradovate deja una sola sesión de datos por usuario: si abrís Tradovate Trader con el mismo usuario, puede cortar la de Trading Hub (y al revés)."));
    }

    /// <summary>cTrader: aplicación de la Open API (Client ID + Secret) y token de la cuenta (OAuth o Playground).</summary>
    private Control CTraderSection(ConnectionProfile p, List<Action> apply)
    {
        const string redirect = "http://127.0.0.1:53682/ctrader";
        var demo = new ToggleSwitch { IsChecked = p.Testnet, OnContent = "Cuenta demo", OffContent = "Cuenta real" };
        var clientId = new TextBox { Text = p.CTraderClientId, Watermark = "Client ID de tu aplicación" };
        var secret = SecretBox(p, null, "Secret de tu aplicación");
        var token = SecretBox(p, "token", "token de acceso (o tocá «Conectar con cTrader»)");
        var login = new NumericUpDown { Value = p.CTraderLogin, Minimum = 0, Maximum = 999999999999, FormatString = "0", Watermark = "la primera", HorizontalAlignment = HorizontalAlignment.Stretch };
        var oauthStatus = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        var connect = Ui.Button("Conectar con cTrader", MaterialIconKind.Login, () => { }, "primary");
        connect.Click += async (_, _) =>
        {
            string id = (clientId.Text ?? "").Trim();
            string sec = !string.IsNullOrWhiteSpace(secret.Text) ? secret.Text.Trim() : _svc.Connections.GetSecret(p);
            if (id.Length == 0 || sec.Length == 0)
            {
                oauthStatus.Text = "Primero completá el Client ID y el Secret de tu aplicación.";
                return;
            }

            connect.IsEnabled = false;
            oauthStatus.Text = "Se abrió el navegador: entrá con tu cTrader ID y autorizá a Trading Hub. Esperando…";
            try
            {
                var (access, refresh) = await CTraderOAuthAsync(id, sec, redirect);
                p.CTraderClientId = id;
                _svc.Connections.SetSecret(p, sec);
                _svc.Connections.SetSecret(p, "token", access);
                if (refresh.Length > 0) _svc.Connections.SetSecret(p, "refresh", refresh);
                _svc.Connections.Save();
                _svc.Connections.Invalidate(p);
                token.Watermark = $"•••••••• (guardado en {Os.SecretStoreShort})";
                oauthStatus.Text = "Listo: Trading Hub quedó autorizado. Tocá «Probar conexión».";
            }
            catch (Exception ex)
            {
                oauthStatus.Text = "No se pudo autorizar: " + ex.Message;
            }
            finally
            {
                connect.IsEnabled = true;
            }
        };
        var copy = Ui.Button("Copiar", MaterialIconKind.ContentCopy, () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) _ = cb.SetTextAsync(redirect);
        }, "ghost");
        apply.Add(() =>
        {
            p.Testnet = demo.IsChecked == true;
            p.CTraderClientId = (clientId.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            if (!string.IsNullOrWhiteSpace(token.Text)) _svc.Connections.SetSecret(p, "token", token.Text.Trim());
            token.Text = "";
            p.CTraderLogin = (long)(login.Value ?? 0);
        });
        return Ui.V(14,
            Ui.H2("cTrader Open API"),
            Note(MaterialIconKind.InformationOutline, "Gratis con cualquier broker que tenga cTrader (IC Markets, Pepperstone, FxPro, Fusion Markets…) y una cuenta demo: CFDs de índices (US100/NAS100, US500, GER40…), forex, oro y petróleo, con historia tick a tick de años."),
            Ui.H3("1 · Tu aplicación (una sola vez)"),
            Note(MaterialIconKind.ShieldKeyOutline, "Creá una aplicación en openapi.ctrader.com (entrás con tu cTrader ID) y agregá esta dirección de redirección. Después copiá acá el Client ID y el Secret."),
            Ui.H(8, new TextBlock { Text = redirect, FontFamily = Ui.MonoFont, VerticalAlignment = VerticalAlignment.Center }, copy, Link("Abrir openapi.ctrader.com", "https://openapi.ctrader.com/apps")),
            TwoColumns(Ui.Field("Client ID", clientId), Ui.Field("Secret", secret)),
            Ui.H3("2 · Autorizar tu cuenta"),
            Ui.H(10, connect, demo),
            oauthStatus,
            Ui.Field("O pegá un token de acceso (el del «Playground» de tu aplicación)", token),
            Ui.Field("Número de cuenta (login de cTrader)", login, "Si el token tiene varias cuentas, la que querés usar. 0 = la primera demo/real."));
    }

    /// <summary>OAuth de cTrader: abre el navegador y espera el código en la dirección local de redirección.</summary>
    private static async Task<(string Access, string Refresh)> CTraderOAuthAsync(string clientId, string secret, string redirect)
    {
        var uri = new Uri(redirect);
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{uri.Port}/");
        listener.Start();
        Shell.Open(TradingHub.Feeds.CTrader.CTraderAuth.AuthorizeUrl(clientId, redirect));
        var wait = listener.GetContextAsync();
        if (await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMinutes(5))) != wait) throw new TimeoutException("no llegó la autorización (5 minutos)");
        var ctx = await wait;
        string? code = ctx.Request.QueryString["code"];
        string? error = ctx.Request.QueryString["error"] ?? ctx.Request.QueryString["error_description"];
        var html = System.Text.Encoding.UTF8.GetBytes(code != null
            ? "<html><body style='font-family:-apple-system;padding:40px'><h2>Listo</h2><p>Trading Hub quedó autorizado. Ya podés cerrar esta pestaña y volver a Trading Hub.</p></body></html>"
            : "<html><body style='font-family:-apple-system;padding:40px'><h2>No se autorizó</h2><p>Volvé a Trading Hub e intentá de nuevo.</p></body></html>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(html);
        ctx.Response.Close();
        if (code == null) throw new InvalidOperationException(error ?? "cTrader no mandó el código");
        var (access, refresh, _) = await TradingHub.Feeds.CTrader.CTraderAuth.ExchangeAsync(clientId, secret, code, redirect, CancellationToken.None);
        return (access, refresh);
    }


    private static string AggregateSourceName(string id) => TradingHub.Feeds.Venues.CryptoVenues.Find(id)?.Name ?? id;

    /// <summary>Cripto agregado: qué mercados se suman.</summary>
    private Control AggregateSection(ConnectionProfile p, List<Action> apply)
    {
        var all = TradingHub.Feeds.Venues.CryptoVenues.All;
        var checks = all.Select(v => new CheckBox { Content = v.Name, IsChecked = p.AggregateSources.Contains(v.Id), Tag = v.Id }).ToList();
        apply.Add(() =>
        {
            var chosen = checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
            if (chosen.Count == 0) chosen.Add(BinanceFeed.FuturesId);
            p.AggregateSources = chosen;
        });
        var markets = new ComboBox { MinWidth = 220 };
        foreach (var m in Enum.GetValues<AggregateMarkets>()) markets.Items.Add(new ComboBoxItem { Content = Ui.EnumName(m), Tag = m });
        markets.SelectedItem = markets.Items.OfType<ComboBoxItem>().First(i => (AggregateMarkets)i.Tag! == p.AggregateMarkets);
        apply.Add(() => p.AggregateMarkets = (markets.SelectedItem as ComboBoxItem)?.Tag is AggregateMarkets am ? am : AggregateMarkets.Both);
        var recommended = Ui.Button("Los recomendados", MaterialIconKind.StarOutline, () =>
        {
            foreach (var c in checks) c.IsChecked = TradingHub.Feeds.Venues.CryptoVenues.Recommended.Contains((string)c.Tag!);
        }, "ghost");
        var everything = Ui.Button("Todos", MaterialIconKind.CheckAll, () =>
        {
            foreach (var c in checks) c.IsChecked = true;
        }, "ghost");
        var perps = new WrapPanel();
        var spots = new WrapPanel();
        for (int i = 0; i < all.Count; i++)
        {
            checks[i].Margin = new Thickness(0, 0, 18, 6);
            (all[i].Spot ? spots : perps).Children.Add(checks[i]);
        }

        var list = new StackPanel { Spacing = 6, Children =
        {
            new TextBlock { Text = "Perpetuos", Classes = { "faint", "small" } }, perps,
            new TextBlock { Text = "Spot", Classes = { "faint", "small" } }, spots,
            Ui.H(6, recommended, everything),
        } };
        var book = new CheckBox { Content = "Sumar también los libros de órdenes (DOM y mapa de calor agregados)", IsChecked = p.AggregateBook };
        apply.Add(() => p.AggregateBook = book.IsChecked == true);
        return Ui.V(14,
            Ui.H2("Mercados que se suman"),
            Note(MaterialIconKind.InformationOutline, "Las operaciones y las velas de todos los mercados elegidos se suman en un solo instrumento (BTCUSDT = Binance + Bybit), como en aggr.trade: volumen, delta y footprint agregados. El precio es el del primero; el libro de órdenes, la suma de todos (o el del primero, si se apaga abajo). Solo entran los símbolos que existen en al menos dos mercados; los nombres se emparejan por moneda (BTC-USDT-SWAP de OKX, BTC-USD de Coinbase y BTC de Hyperliquid son BTCUSDT), y un mercado cuyo precio se aleja más de 3 % del primero queda afuera."),
            list,
            Ui.Field("Spot / perpetuos", markets, "Deja afuera los spot o los perpetuos sin destildarlos (el filtro de LiquidityMapperFast)."),
            book,
            Note(MaterialIconKind.ShieldCheckOutline, "Datos públicos: no hace falta cuenta ni claves. Para operar, usá la conexión de cada mercado."));
    }

    /// <summary>长桥 (Longbridge): entrar con OAuth 2.0 desde el navegador; región y símbolos del buscador.</summary>
    private Control LongbridgeSection(ConnectionProfile p, List<Action> apply)
    {
        var region = new ComboBox { MinWidth = 260 };
        region.Items.Add(new ComboBoxItem { Content = "Global (openapi.longbridge.com)", Tag = "global" });
        region.Items.Add(new ComboBoxItem { Content = "China continental (openapi.longbridge.cn)", Tag = "cn" });
        region.SelectedItem = region.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == p.LongbridgeRegion) ?? region.Items[0];
        var symbols = new TextBox
        {
            Text = string.Join(", ", p.LongbridgeSymbols),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            Watermark = "700.HK, AAPL.US, 600519.SH…",
        };
        bool authorized = !string.IsNullOrWhiteSpace(_svc.Connections.GetSecret(p, "token"));
        var oauthStatus = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap, Text = authorized ? $"Autorizado (tokens guardados en {Os.SecretStoreShort}). Se renuevan solos." : "Todavía no entraste con tu cuenta." };
        var connect = Ui.Button(authorized ? "Volver a entrar con 长桥" : "Conectar con 长桥", MaterialIconKind.Login, () => { }, "primary");
        var logout = Ui.Button("Salir", MaterialIconKind.Logout, () =>
        {
            _svc.Connections.SetSecret(p, "token", null);
            _svc.Connections.SetSecret(p, "refresh", null);
            p.LongbridgeExpiresAt = 0;
            _svc.Connections.Save();
            _svc.Connections.Invalidate(p);
            oauthStatus.Text = "Sesión cerrada. Para volver a usar 长桥, tocá «Conectar con 长桥».";
        }, "ghost");
        logout.IsVisible = authorized;
        connect.Click += async (_, _) =>
        {
            string reg = (string)((ComboBoxItem)region.SelectedItem!).Tag!;
            string baseUrl = reg == "cn" ? TradingHub.Feeds.Longbridge.LongbridgeOAuth.BaseUrlCn : TradingHub.Feeds.Longbridge.LongbridgeOAuth.BaseUrlGlobal;
            // el cliente OAuth es de un servidor: si cambió la región, se registra otro
            string? clientId = p.LongbridgeRegion == reg ? p.LongbridgeClientId : null;
            connect.IsEnabled = false;
            oauthStatus.Text = "Se abrió el navegador: entrá con tu cuenta de 长桥 y autorizá a " + TradingHub.Common.Product.Name + ". Esperando…";
            try
            {
                var (token, id) = await TradingHub.Feeds.Longbridge.LongbridgeOAuth.LoginAsync(baseUrl, clientId, TradingHub.Common.Product.Name, Shell.Open, CancellationToken.None);
                p.LongbridgeRegion = reg;
                p.LongbridgeClientId = id;
                p.LongbridgeExpiresAt = token.ExpiresAt;
                _svc.Connections.SetSecret(p, "token", token.Access);
                _svc.Connections.SetSecret(p, "refresh", token.Refresh);
                _svc.Connections.Save();
                _svc.Connections.Invalidate(p);
                InstrumentPicker.ClearCache(p.Id);
                logout.IsVisible = true;
                oauthStatus.Text = $"Listo: {TradingHub.Common.Product.Name} quedó autorizado en 长桥 (tokens en {Os.SecretStoreShort}). Tocá «Probar conexión».";
            }
            catch (Exception ex)
            {
                oauthStatus.Text = "No se pudo autorizar: " + NetworkProxy.Explain(ex);
            }
            finally
            {
                connect.IsEnabled = true;
            }
        };
        apply.Add(() =>
        {
            p.LongbridgeRegion = (string)((ComboBoxItem)region.SelectedItem!).Tag!;
            p.LongbridgeSymbols = (symbols.Text ?? "").Split(new[] { ',', ';', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant()).Where(s => s.Contains('.')).Distinct().ToList();
            if (p.LongbridgeSymbols.Count == 0) p.LongbridgeSymbols = new List<string>(TradingHub.Feeds.Longbridge.LongbridgeOAuth.DefaultSymbols);
        });
        return Ui.V(14,
            Ui.H2("长桥 OpenAPI (Longbridge)"),
            Note(MaterialIconKind.InformationOutline, "Acciones y ETFs de Hong Kong (700.HK), EE.UU. (AAPL.US), Shanghái/Shenzhen (600519.SH, 300750.SZ) y Singapur con tu cuenta de 长桥: cotizaciones en vivo con el lado de cada operación, profundidad y velas de años. HK y EE.UU. nivel 1 vienen incluidos; otros niveles según tu suscripción en 长桥."),
            Ui.H3("1 · Entrar con tu cuenta (OAuth 2.0)"),
            Note(MaterialIconKind.ShieldKeyOutline, "Sin App Key ni Secret: se abre el navegador, entrás en 长桥 y autorizás la app. Los tokens se guardan en " + Os.SecretStore + " y se renuevan solos. Solo datos de mercado (no se piden permisos de cuenta ni de operar)."),
            Ui.Field("Servidor", region, "China continental usa openapi.longbridge.cn; cuentas de EE.UU. tienen que usar el global."),
            Ui.H(10, connect, logout),
            oauthStatus,
            Ui.H3("2 · Símbolos del buscador"),
            Ui.Field("Símbolos (código.mercado, separados por coma)", symbols, "Podés agregar los que quieras; el buscador muestra estos con su nombre y último precio."),
            Ui.H(16, Link("Abrir open.longbridge.com", "https://open.longbridge.com"), Link("Documentación de la API", "https://open.longbridge.com/docs")));
    }

    /// <summary>OANDA: token personal y cuenta de práctica (gratis) o real.</summary>
    private Control OandaSection(ConnectionProfile p, List<Action> apply)
    {
        var practice = new ToggleSwitch { IsChecked = p.Testnet, OnContent = "Cuenta de práctica (gratis)", OffContent = "Cuenta real" };
        var token = SecretBox(p, null, "token de la API (Manage API Access)");
        var account = new TextBox { Text = p.OandaAccount, Watermark = "101-001-… (vacío = la primera)" };
        apply.Add(() =>
        {
            p.Testnet = practice.IsChecked == true;
            if (!string.IsNullOrWhiteSpace(token.Text)) _svc.Connections.SetSecret(p, token.Text.Trim());
            token.Text = "";
            p.OandaAccount = (account.Text ?? "").Trim();
        });
        return Ui.V(14,
            Ui.H2("OANDA (API v20)"),
            Note(MaterialIconKind.InformationOutline, "Gratis con una cuenta de práctica: CFDs de índices (NAS100_USD = Nasdaq 100, SPX500_USD = S&P 500, US30_USD, DE30_EUR…), oro, petróleo y forex, con años de historia en velas de 5 segundos."),
            practice,
            TwoColumns(Ui.Field("Token", token), Ui.Field("Cuenta", account)),
            Ui.H(16, Link("Crear cuenta de práctica", "https://www.oanda.com/demo-account/"), Link("Generar el token (Manage API Access)", "https://www.oanda.com/demo-account/tpa/personal_token")),
            Note(MaterialIconKind.ShieldKeyOutline, $"El token se guarda en {Os.SecretStore}. Trading Hub lo usa solo para datos (no manda órdenes a OANDA)."));
    }

    /// <summary>Alpaca: claves de la cuenta de práctica (historia SIP desde 2016 gratis).</summary>
    private Control AlpacaSection(ConnectionProfile p, List<Action> apply)
    {
        var key = new TextBox { Text = p.ApiKey, Watermark = "API key ID" };
        var secret = SecretBox(p, null, "secret key");
        var live = new ComboBox { MinWidth = 260 };
        live.Items.Add(new ComboBoxItem { Content = "IEX (gratis, una bolsa)", Tag = "iex" });
        live.Items.Add(new ComboBoxItem { Content = "SIP (todas las bolsas, plan pago)", Tag = "sip" });
        live.SelectedItem = live.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == p.AlpacaLiveFeed) ?? live.Items[0];
        apply.Add(() =>
        {
            p.ApiKey = (key.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            p.AlpacaLiveFeed = (live.SelectedItem as ComboBoxItem)?.Tag as string ?? "iex";
        });
        return Ui.V(14,
            Ui.H2("Alpaca (acciones y ETFs de EE.UU.)"),
            Note(MaterialIconKind.InformationOutline, "Con la cuenta de práctica (gratis, desde cualquier país) la historia tick a tick de TODAS las bolsas de EE.UU. desde 2016 es gratis (salvo los últimos 15 minutos). SPY = S&P 500, QQQ = Nasdaq 100, IWM = Russell 2000, DIA = Dow: operaciones reales para estudiar los índices."),
            TwoColumns(Ui.Field("API key ID", key), Ui.Field("Secret key", secret)),
            Ui.Field("Datos en vivo", live),
            Ui.H(16, Link("Crear cuenta de práctica", "https://app.alpaca.markets/signup"), Link("Dónde están las claves", "https://app.alpaca.markets/paper/dashboard/overview")),
            Note(MaterialIconKind.ShieldKeyOutline, "Trading Hub usa las claves solo para datos (no manda órdenes a Alpaca)."));
    }

    /// <summary>Databento: API key (historia de futuros de CME desde 2010, pago por uso con créditos de regalo).</summary>
    private Control DatabentoSection(ConnectionProfile p, List<Action> apply)
    {
        var key = SecretBox(p, null, "API key (db-…)");
        apply.Add(() =>
        {
            if (!string.IsNullOrWhiteSpace(key.Text)) _svc.Connections.SetSecret(p, key.Text.Trim());
            key.Text = "";
        });
        return Ui.V(14,
            Ui.H2("Databento"),
            Note(MaterialIconKind.InformationOutline, "Historia tick a tick de los futuros de CME (NQ, ES, MNQ, MES, CL, GC…) desde 2010, con el lado agresor real de cada operación: lo que Rithmic no guarda. Es pago por uso; las cuentas nuevas traen US$ 125 de créditos gratis."),
            Ui.Field("API key", key),
            Ui.H(16, Link("Crear cuenta (con créditos de regalo)", "https://databento.com/signup"), Link("Precios", "https://databento.com/pricing")),
            Note(MaterialIconKind.ShieldCheckOutline, "Se usa desde Datos → «Futuros de CME con Databento»: Trading Hub calcula el costo antes y solo baja si confirmás. Se guarda aparte («Databento» en Datos) y en los backtests elegís solo Databento, solo Rithmic o las dos combinadas (Rithmic primero y Databento completa lo que falta)."));
    }

    /// <summary>Datos de opciones para los indicadores de opciones: Deribit (público) o una API propia con el JSON documentado.</summary>
    private Control OptionsDataSection(ConnectionProfile p, List<Action> apply)
    {
        var source = new ComboBox { MinWidth = 260 };
        foreach (var v in Enum.GetValues<OptionsSource>()) source.Items.Add(new ComboBoxItem { Content = Ui.EnumName(v), Tag = v });
        source.SelectedItem = source.Items.OfType<ComboBoxItem>().First(i => (OptionsSource)i.Tag! == p.OptionsSource);
        var url = new TextBox { Text = p.ExternalUrl, Watermark = "https://mi-proveedor.com/api/options" };
        var header = new TextBox { Text = p.ExternalHeader, Watermark = "Authorization" };
        var key = SecretBox(p, null, "API key (opcional)");
        var unders = new TextBox { Text = p.ExternalUnderlyings, Watermark = "BTC, ETH, SPY (vacío = todos)" };
        var custom = Ui.V(14,
            Ui.Field("Dirección base de la API", url, "Se piden /chain, /trades y /daily debajo de esta dirección."),
            TwoColumns(Ui.Field("Encabezado de la clave", header, "«Authorization» la manda como Bearer."), Ui.Field("API key", key)),
            Ui.Field("Subyacentes", unders),
            Note(MaterialIconKind.CodeJson, "Formato: GET /chain?underlying=BTC → {\"time\",\"spot\",\"contracts\":[{\"symbol\",\"expiry\",\"strike\",\"right\":\"C|P\",\"bid\",\"ask\",\"mark\",\"iv\",\"delta\",\"gamma\",\"oi\",\"volume\",\"contractSize\"}]}; GET /trades?underlying=BTC&from=…&to=… → [{\"time\",\"symbol\",\"expiry\",\"strike\",\"right\",\"price\",\"size\",\"side\":\"buy|sell\",\"iv\",\"spot\",\"venue\"}]; GET /daily → [{\"date\",\"atmIv\",\"rr25\",\"pcVolume\",\"pcOi\",\"gex\",\"callWall\",\"putWall\",\"gammaFlip\",\"expectedMove\"}]. IV en fracción (0,55 = 55 %), precios en dólares por contrato; lo que falte se calcula."));
        void Sync() => custom.IsVisible = (OptionsSource)((ComboBoxItem)source.SelectedItem!).Tag! == OptionsSource.CustomJson;
        source.SelectionChanged += (_, _) => Sync();
        Sync();
        apply.Add(() =>
        {
            p.OptionsSource = (OptionsSource)((ComboBoxItem)source.SelectedItem!).Tag!;
            p.ExternalUrl = url.Text?.Trim() ?? "";
            p.ExternalHeader = string.IsNullOrWhiteSpace(header.Text) ? "Authorization" : header.Text.Trim();
            p.ExternalUnderlyings = unders.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(key.Text)) _svc.Connections.SetSecret(p, key.Text.Trim());
            key.Text = "";
        });
        return Ui.V(14,
            Ui.H2("Datos de opciones"),
            Note(MaterialIconKind.InformationOutline, "Los indicadores de opciones (gamma de los creadores de mercado, muros, IV Rank, sesgo, flujo de primas, tablero de opciones…) toman los datos de acá. Sin esta conexión dicen «sin datos de opciones»; cuando la agregás, empiezan a andar solos en los gráficos abiertos. El símbolo del gráfico elige el subyacente (BTCUSDT → BTC)."),
            Ui.Field("Fuente", source),
            custom,
            Note(MaterialIconKind.ShieldCheckOutline, "Deribit es público: no necesita cuenta. La API key de una API propia se guarda " + (Os.IsWindows ? "en el Administrador de credenciales" : "en el Llavero") + ", nunca en los archivos. La historia diaria se completa con una foto por día que Trading Hub guarda mientras el indicador está abierto."));
    }

    /// <summary>Flujos diarios de los ETF al contado por una API (el formato está documentado).</summary>
    private Control EtfFlowsSection(ConnectionProfile p, List<Action> apply)
    {
        var url = new TextBox { Text = p.ExternalUrl, Watermark = "https://mi-proveedor.com/api/etf" };
        var header = new TextBox { Text = p.ExternalHeader, Watermark = "Authorization" };
        var key = SecretBox(p, null, "API key (opcional)");
        apply.Add(() =>
        {
            p.ExternalUrl = url.Text?.Trim() ?? "";
            p.ExternalHeader = string.IsNullOrWhiteSpace(header.Text) ? "Authorization" : header.Text.Trim();
            if (!string.IsNullOrWhiteSpace(key.Text)) _svc.Connections.SetSecret(p, key.Text.Trim());
            key.Text = "";
        });
        return Ui.V(14,
            Ui.H2("Flujos de ETF"),
            Note(MaterialIconKind.InformationOutline, "El indicador «Flujo de los ETF al contado» toma de acá el flujo neto diario de los ETF de la moneda del gráfico. Sin esta conexión dice que no hay datos; cuando la agregás, anda solo."),
            Ui.Field("Dirección base de la API", url, "Se pide /flows?asset=BTC&from=AAAA-MM-DD&to=AAAA-MM-DD debajo de esta dirección."),
            TwoColumns(Ui.Field("Encabezado de la clave", header, "«Authorization» la manda como Bearer."), Ui.Field("API key", key)),
            Note(MaterialIconKind.CodeJson, "Formato: [{\"date\":\"2026-11-03\",\"net\":-123400000,\"funds\":{\"IBIT\":-80000000}}] en dólares (o \"netMillions\" en millones). También vale {\"data\":[…]}."),
            Note(MaterialIconKind.ShieldCheckOutline, "La API key se guarda " + (Os.IsWindows ? "en el Administrador de credenciales" : "en el Llavero") + ", nunca en los archivos."));
    }

    private Control IbSection(ConnectionProfile p, List<Action> apply)
    {
        var host = new TextBox { Text = p.IbHost };
        var port = new NumericUpDown { Value = p.IbPort, Minimum = 1, Maximum = 65535, FormatString = "0" };
        var client = new NumericUpDown { Value = p.IbClientId, Minimum = 0, Maximum = 999, FormatString = "0" };
        apply.Add(() =>
        {
            p.IbHost = string.IsNullOrWhiteSpace(host.Text) ? "127.0.0.1" : host.Text.Trim();
            p.IbPort = (int)(port.Value ?? 7497);
            p.IbClientId = (int)(client.Value ?? 17);
        });
        return Ui.V(14,
            Ui.H2("Interactive Brokers (TWS / IB Gateway)"),
            Note(MaterialIconKind.InformationOutline, "Abrí TWS o IB Gateway y habilitá la API (Configure → API → Settings). Puertos habituales: 7497 TWS paper, 7496 TWS real, 4002 Gateway paper. Por ahora Trading Hub solo verifica la conexión."),
            TwoColumns(Ui.Field("Host", host), Ui.Field("Puerto", port)),
            Ui.Field("Client ID", client));
    }
}
