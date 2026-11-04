using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using TradingHub.App.Services;
using TradingHub.App.Windows;

namespace TradingHub.App.Controls;

/// <summary>
/// Una página web dentro de KVP: en Windows, Microsoft Edge WebView2 (viene con Windows 10 / 11) en una ventana hija; en
/// los demás sistemas no hay control embebido y <see cref="Supported"/> es false (la página se abre en el navegador).
/// Los enlaces que abren ventanas nuevas van al navegador del sistema.
/// </summary>
public sealed class WebViewHost : NativeControlHost
{
    private IntPtr _hwnd;
    private Microsoft.Web.WebView2.Core.CoreWebView2Controller? _controller;
    private string? _url;

    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>Error al crear el control (por ejemplo, sin el runtime de WebView2).</summary>
    public event Action<string>? Failed;

    public string? Url
    {
        get => _url;
        set
        {
            _url = value;
            if (_controller != null && value != null) _controller.CoreWebView2.Navigate(value);
        }
    }

    public void Reload() => _controller?.CoreWebView2.Reload();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    private const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows()) return base.CreateNativeControlCore(parent);
        _hwnd = CreateWindowEx(0, "Static", "", WsChild | WsVisible | WsClipChildren, 0, 0, 100, 100, parent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        _ = InitAsync(_hwnd);
        return new PlatformHandle(_hwnd, "HWND");
    }

    private async Task InitAsync(IntPtr hwnd)
    {
        try
        {
            var data = Path.Combine(DataLocation.Root, "Workbench", "WebView2");
            Directory.CreateDirectory(data);
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, data);
            var c = await env.CreateCoreWebView2ControllerAsync(hwnd);
            if (_hwnd != hwnd)
            {
                c.Close();
                return;
            }

            _controller = c;
            c.CoreWebView2.Settings.IsStatusBarEnabled = false;
            c.CoreWebView2.Settings.AreDevToolsEnabled = true;
            // ventanas nuevas y enlaces externos: al navegador
            c.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                Shell.OpenUrl(e.Uri);
            };
            UpdateBounds();
            if (_url != null) c.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
        }
    }

    private void UpdateBounds()
    {
        if (_controller == null) return;
        double scale = (VisualRoot as TopLevel)?.RenderScaling ?? 1;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, Math.Max(1, (int)(Bounds.Width * scale)), Math.Max(1, (int)(Bounds.Height * scale)));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateBounds();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && _controller != null) _controller.IsVisible = IsVisible;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (!OperatingSystem.IsWindows())
        {
            base.DestroyNativeControlCore(control);
            return;
        }

        try
        {
            _controller?.Close();
        }
        catch
        {
            // ya cerrado
        }

        _controller = null;
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }
}
