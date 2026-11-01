using System.Net;

namespace TradingHub.App.Services;

/// <summary>
/// Proxy de red de Trading Hub (Ajustes → General → Proxy de red): el de Windows/macOS (por defecto), uno propio solo para
/// Trading Hub (por ejemplo el de Clash o v2rayN, http://127.0.0.1:7890) o ninguno. Se aplica a todo: datos de mercado,
/// descargas, órdenes, actualizaciones (las conexiones HTTP y WebSocket que no fijan uno propio).
/// </summary>
public static class NetworkProxy
{
    public const string System = "sistema", Manual = "manual", None = "ninguno";

    /// <summary>Arma el proxy de una configuración (null = conexión directa).</summary>
    public static IWebProxy? Create(string? mode, string? address)
    {
        switch (mode)
        {
            case Manual when Normalize(address) is { } url:
                return new WebProxy(url) { BypassProxyOnLocal = true };
            case None:
                return null;
            default:
                return HttpClient.DefaultProxy;
        }
    }

    /// <summary>«127.0.0.1:7890» → «http://127.0.0.1:7890»; acepta http://, https://, socks4://, socks5://.</summary>
    public static string? Normalize(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var a = address.Trim();
        if (!a.Contains("://", StringComparison.Ordinal)) a = "http://" + a;
        // hace falta el puerto («hola» no es un proxy; «127.0.0.1:7890» sí)
        var authority = a[(a.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        if (!global::System.Text.RegularExpressions.Regex.IsMatch(authority, @":\d+$")) return null;
        return Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "socks4" or "socks4a" or "socks5" && u.Port > 0
            ? u.GetLeftPart(UriPartial.Authority)
            : null;
    }

    /// <summary>Lo aplica a toda la app (llamar al arrancar, antes de cualquier conexión).</summary>
    public static void Apply(string? mode, string? address)
    {
        try
        {
            if (mode == Manual && Normalize(address) is { } url) HttpClient.DefaultProxy = new WebProxy(url) { BypassProxyOnLocal = true };
            else if (mode == None) HttpClient.DefaultProxy = new WebProxy(); // sin dirección = conexión directa
        }
        catch
        {
            // una dirección rara: queda el del sistema
        }
    }

    /// <summary>Prueba con esa configuración si se llega a Bybit y a Binance (sin tocar la de la app).</summary>
    public static async Task<string> TestAsync(string? mode, string? address, CancellationToken ct)
    {
        if (mode == Manual && Normalize(address) == null) return "La dirección del proxy no es válida (por ejemplo 127.0.0.1:7890 o socks5://127.0.0.1:7891).";
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) };
        var proxy = Create(mode, address);
        if (proxy == null) handler.UseProxy = false;
        else handler.Proxy = proxy;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        var lines = new List<string>();
        foreach (var (name, url) in new[] { ("Bybit", "https://api.bybit.com/v5/market/time"), ("Binance", "https://fapi.binance.com/fapi/v1/time") })
        {
            var sw = global::System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var r = await http.GetAsync(url, ct).ConfigureAwait(false);
                lines.Add(r.IsSuccessStatusCode ? $"{name}: OK ({sw.ElapsedMilliseconds} ms)" : $"{name}: respondió {(int)r.StatusCode}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lines.Add($"{name}: {Explain(ex)}");
            }
        }

        return string.Join(" · ", lines);
    }

    /// <summary>Un error de red en palabras (con el detalle interno, que es el que dice qué pasó).</summary>
    public static string Explain(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        bool ssl = ex is HttpRequestException && (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || inner is IOException || inner is global::System.Security.Authentication.AuthenticationException);
        bool timeout = ex is TaskCanceledException or TimeoutException || inner is TimeoutException;
        bool refused = inner is global::System.Net.Sockets.SocketException;
        if (ssl || timeout || refused)
            return inner != ex
                ? $"No se pudo conectar (la conexión se cortó o no respondió). Si en tu red hace falta un proxy para el exchange, configuralo en Ajustes → General → Proxy de red. Detalle: {inner.Message}"
                : "No se pudo conectar (la conexión se cortó o no respondió). Si en tu red hace falta un proxy para el exchange, configuralo en Ajustes → General → Proxy de red.";
        return inner != ex ? $"{ex.Message} ({inner.Message})" : ex.Message;
    }
}
