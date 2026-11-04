using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.App.Services.Workbench;

namespace TradingHub.App.Views;

/// <summary>
/// El Workbench (lo que era VPFM) como una sección más de KVP: mapa de calor de FlowMap, operaciones agregadas (aggr),
/// mapa de liquidez, derivados, opciones GEX, estructura y herramientas, con la barra lateral de VPFM. Todo sale del
/// servidor local de KVP (las velas, de la misma caché que los gráficos).
/// </summary>
public sealed class WorkbenchView : UserControl
{
    private readonly AppServices _svc;
    private readonly WebViewHost? _web;
    private readonly TextBlock _status = new() { Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Panel _body = new();

    public WorkbenchView(AppServices svc, string? module = null)
    {
        _svc = svc;
        var browser = Ui.Button("Abrir en el navegador", MaterialIconKind.OpenInNew, () => _ = OpenInBrowserAsync(), "ghost");
        var reload = Ui.Button("Recargar", MaterialIconKind.Refresh, () => _web?.Reload(), "ghost");
        var api = Ui.Button("API de datos", MaterialIconKind.Api, () => _ = OpenApiAsync(), "ghost");
        ToolTip.SetTip(api, "La API local de KVP (/api/kvp/…): velas, operaciones, libro y liquidez para otros programas");
        var bar = new DockPanel { Margin = new Thickness(12, 6), LastChildFill = true };
        var right = Ui.H(6, reload, browser, api);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(Ui.H(8, Ui.Icon(MaterialIconKind.ViewDashboardVariantOutline, 18, Ui.Accent), new TextBlock { Text = "Workbench", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, _status));

        if (WebViewHost.Supported && WorkbenchServer.HasModules)
        {
            _web = new WebViewHost();
            _web.Failed += msg => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _body.Children.Clear();
                _body.Children.Add(Message("No se pudo abrir el Workbench dentro de KVP",
                    "Hace falta el Microsoft Edge WebView2 Runtime (viene con Windows 10 y 11). Mientras tanto se puede usar en el navegador.\n" + msg));
            });
            _body.Children.Add(_web);
        }
        else if (!WorkbenchServer.HasModules)
            _body.Children.Add(Message("Los módulos del Workbench no vienen en esta instalación",
                "El instalador completo de KVP trae los módulos de VPFM (mapa de calor, aggr, liquidez, derivados, GEX, estructura). Actualizá desde Ajustes → Actualizaciones."));
        else
            _body.Children.Add(Message("El Workbench se abre en el navegador en este sistema", "Tocá «Abrir en el navegador»: usa el mismo servidor local y los mismos datos de KVP."));

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_body);
        Content = root;
        _ = StartAsync(module);
    }

    private static Control Message(string title, string text) => new StackPanel
    {
        Margin = new Thickness(40),
        Spacing = 10,
        MaxWidth = 720,
        HorizontalAlignment = HorizontalAlignment.Left,
        Children =
        {
            new TextBlock { Text = title, Classes = { "h2" } },
            new TextBlock { Text = text, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
        },
    };

    private async Task<WorkbenchServer?> ServerAsync()
    {
        try
        {
            var s = _svc.Workbench;
            await s.StartAsync();
            return s;
        }
        catch (Exception ex)
        {
            _status.Text = "El servidor local no arrancó: " + ex.Message;
            return null;
        }
    }

    private async Task StartAsync(string? module)
    {
        var s = await ServerAsync();
        if (s == null) return;
        _status.Text = $"{s.BaseUrl} · velas desde la caché de KVP" + (s.Sidecar.Available ? "" : " · sin motor del mapa de calor");
        if (_web != null) _web.Url = module == null ? s.BaseUrl + "/wb/" : s.BaseUrl + "/wb/window.html#" + module;
    }

    private async Task OpenInBrowserAsync()
    {
        var s = await ServerAsync();
        if (s != null) Shell.OpenUrl(s.BaseUrl + "/wb/");
    }

    private async Task OpenApiAsync()
    {
        var s = await ServerAsync();
        if (s != null) Shell.OpenUrl(s.BaseUrl + "/api/kvp/info");
    }
}

/// <summary>Los módulos del Workbench en ventanas aparte (lo que en VPFM era «在新窗口打开»).</summary>
internal static class WorkbenchWindows
{
    private static readonly string[] Modules = { "flowmap", "aggr", "lmap", "derivs", "options", "structure", "tools" };
    private static readonly Dictionary<string, Window> Open = new();

    public static string[] OpenModules()
    {
        lock (Open) return Open.Keys.ToArray();
    }

    /// <summary>open / focus / close de un módulo; main / sync no hacen nada (la ventana principal es la de KVP).</summary>
    public static bool Operate(WorkbenchServer server, string op, string mod)
    {
        if (op is "main" or "sync") return true;
        if (op is not ("open" or "focus" or "close") || !Modules.Contains(mod)) return false;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Window? w;
            lock (Open) Open.TryGetValue(mod, out w);
            if (op == "close")
            {
                w?.Close();
                return;
            }

            if (w != null)
            {
                w.Activate();
                return;
            }

            if (App.Services == null) return;
            w = new Window
            {
                Title = TradingHub.Common.Product.Name + " · " + mod,
                Width = 1280,
                Height = 820,
                Background = Ui.Bg1,
                Content = new WorkbenchView(App.Services, mod),
            };
            lock (Open) Open[mod] = w;
            w.Closed += (_, _) =>
            {
                lock (Open) Open.Remove(mod);
            };
            w.Show();
        });
        return true;
    }
}
