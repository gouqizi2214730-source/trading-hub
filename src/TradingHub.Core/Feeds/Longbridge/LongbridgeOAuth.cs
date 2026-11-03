using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingHub.Feeds.Longbridge;

/// <summary>
/// Conexión de 长桥 (Longbridge): se entra con OAuth 2.0 desde el navegador (sin App Key ni Secret). La app registra su
/// cliente una sola vez (registro dinámico), abre la autorización con PKCE y guarda los tokens en el Llavero.
/// </summary>
public sealed class LongbridgeSettings
{
    /// <summary>Id de cliente OAuth (lo da el registro dinámico; no es secreto).</summary>
    public string ClientId { get; set; } = "";

    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";

    /// <summary>Vencimiento del token de acceso (segundos Unix).</summary>
    public long ExpiresAt { get; set; }

    /// <summary>"global" (openapi.longbridge.com) o "cn" (openapi.longbridge.cn, China continental).</summary>
    public string Region { get; set; } = "global";

    /// <summary>Símbolos que muestra el buscador (700.HK, AAPL.US, 600519.SH…).</summary>
    public List<string> Symbols { get; set; } = new(LongbridgeOAuth.DefaultSymbols);

    /// <summary>Servidores alternativos (pruebas).</summary>
    public string? BaseUrl { get; set; }
    public string? QuoteWsUrl { get; set; }

    /// <summary>Se avisa cuando se renovaron los tokens (para guardarlos).</summary>
    public Action<LongbridgeSettings>? TokensRefreshed { get; set; }

    public string Http => (BaseUrl ?? (Region == "cn" ? LongbridgeOAuth.BaseUrlCn : LongbridgeOAuth.BaseUrlGlobal)).TrimEnd('/');
    public string QuoteWs => (QuoteWsUrl ?? (Region == "cn" ? LongbridgeOAuth.QuoteWsCn : LongbridgeOAuth.QuoteWsGlobal)).TrimEnd('/');

    public bool HasToken => AccessToken.Length > 0;
}

/// <summary>OAuth 2.0 de 长桥: registro del cliente, URL de autorización (PKCE S256), canje del código y renovación.</summary>
public static class LongbridgeOAuth
{
    public const string BaseUrlGlobal = "https://openapi.longbridge.com";
    public const string BaseUrlCn = "https://openapi.longbridge.cn";
    public const string QuoteWsGlobal = "wss://openapi-quote.longbridge.com/v2";
    public const string QuoteWsCn = "wss://openapi-quote.longbridge.cn/v2";

    /// <summary>Puertos locales para la redirección (el primero libre; todos quedan registrados en el cliente).</summary>
    public static readonly int[] CallbackPorts = { 60355, 60356, 60357, 60358 };

    public static readonly string[] DefaultSymbols =
    {
        "700.HK", "9988.HK", "3690.HK", "1810.HK", "9618.HK", "2800.HK",
        "AAPL.US", "NVDA.US", "TSLA.US", "MSFT.US", "AMZN.US", "META.US", "GOOGL.US", "SPY.US", "QQQ.US", "TQQQ.US",
        "600519.SH", "300750.SZ",
    };

    public static string RedirectUri(int port) => $"http://localhost:{port}/callback";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Registra un cliente público (sin secreto) y devuelve su client_id.</summary>
    public static async Task<string> RegisterClientAsync(string baseUrl, string clientName, IEnumerable<string> redirectUris, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/oauth2/register", new
        {
            client_name = clientName,
            redirect_uris = redirectUris.ToArray(),
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        }, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"长桥 no registró el cliente ({(int)resp.StatusCode}): {Short(body)}");
        using var doc = JsonDocument.Parse(body);
        string id = doc.RootElement.TryGetProperty("client_id", out var c) ? c.GetString() ?? "" : "";
        if (id.Length == 0) throw new InvalidOperationException("长桥 no devolvió el client_id: " + Short(body));
        return id;
    }

    /// <summary>Verificador y desafío PKCE (S256).</summary>
    public static (string Verifier, string Challenge) Pkce()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        string verifier = Base64Url(bytes);
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string AuthorizeUrl(string baseUrl, string clientId, string redirectUri, string state, string codeChallenge) =>
        $"{baseUrl.TrimEnd('/')}/oauth2/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&state={Uri.EscapeDataString(state)}&code_challenge={Uri.EscapeDataString(codeChallenge)}&code_challenge_method=S256&access_type=offline";

    public sealed record Token(string Access, string Refresh, long ExpiresAt);

    /// <summary>Canjea el código de la redirección por los tokens.</summary>
    public static Task<Token> ExchangeAsync(string baseUrl, string clientId, string code, string redirectUri, string verifier, CancellationToken ct) =>
        TokenRequestAsync(baseUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }, null, ct);

    /// <summary>Renueva el token de acceso (si el servidor no manda otro de renovación, se conserva el viejo).</summary>
    public static Task<Token> RefreshAsync(string baseUrl, string clientId, string refreshToken, CancellationToken ct) =>
        TokenRequestAsync(baseUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        }, refreshToken, ct);

    private static async Task<Token> TokenRequestAsync(string baseUrl, Dictionary<string, string> form, string? keepRefresh, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/oauth2/token") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"长桥 respondió {(int)resp.StatusCode} al pedir el token: {Short(body)}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            string access = Str(root, "access_token");
            if (!resp.IsSuccessStatusCode || access.Length == 0)
            {
                string err = Str(root, "error_description") is { Length: > 0 } d ? d : Str(root, "error") is { Length: > 0 } e ? e : Short(body);
                throw new InvalidOperationException("长桥 no dio el token: " + err);
            }

            string refresh = Str(root, "refresh_token");
            if (refresh.Length == 0 && keepRefresh != null) refresh = keepRefresh;
            long expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt64(out var n) ? n : 3600;
            return new Token(access, refresh, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn);
        }
    }

    private static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;

    /// <summary>
    /// Entra con la cuenta: registra el cliente si hace falta, abre el navegador (<paramref name="openUrl"/>), espera la
    /// redirección en localhost y canjea el código. Devuelve los tokens y el client_id usado.
    /// </summary>
    public static async Task<(Token Token, string ClientId)> LoginAsync(string baseUrl, string? clientId, string appName, Action<string> openUrl, CancellationToken ct, TimeSpan? timeout = null)
    {
        System.Net.HttpListener? listener = null;
        int port = 0;
        foreach (var candidate in CallbackPorts)
        {
            var l = new System.Net.HttpListener();
            l.Prefixes.Add($"http://localhost:{candidate}/");
            try
            {
                l.Start();
                listener = l;
                port = candidate;
                break;
            }
            catch (Exception)
            {
                l.Close();
            }
        }

        if (listener == null) throw new InvalidOperationException($"No se pudo abrir ningún puerto local para la redirección ({string.Join(", ", CallbackPorts)}): cerrá la CLI de 长桥 u otra app que lo use.");
        using (listener)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                clientId = await RegisterClientAsync(baseUrl, appName, CallbackPorts.Select(RedirectUri), ct).ConfigureAwait(false);
            string redirect = RedirectUri(port);
            var (verifier, challenge) = Pkce();
            string state = Base64Url(RandomNumberGenerator.GetBytes(24));
            openUrl(AuthorizeUrl(baseUrl, clientId, redirect, state, challenge));
            var wait = listener.GetContextAsync();
            var limit = Task.Delay(timeout ?? TimeSpan.FromMinutes(5), ct);
            if (await Task.WhenAny(wait, limit).ConfigureAwait(false) != wait)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("no llegó la autorización de 长桥 (5 minutos)");
            }

            var ctx = await wait.ConfigureAwait(false);
            string? code = ctx.Request.QueryString["code"];
            string? gotState = ctx.Request.QueryString["state"];
            string? error = ctx.Request.QueryString["error_description"] ?? ctx.Request.QueryString["error"];
            bool ok = code != null && gotState == state;
            var html = Encoding.UTF8.GetBytes(CallbackHtml(ok, appName));
            ctx.Response.ContentType = "text/html; charset=utf-8";
            try
            {
                await ctx.Response.OutputStream.WriteAsync(html, ct).ConfigureAwait(false);
                ctx.Response.Close();
            }
            catch (Exception)
            {
                // el navegador cerró antes: no importa
            }

            if (code == null) throw new InvalidOperationException(error ?? "长桥 no mandó el código de autorización");
            if (gotState != state) throw new InvalidOperationException("la respuesta de 长桥 no coincide con el pedido (state): intentá de nuevo");
            var token = await ExchangeAsync(baseUrl, clientId, code, redirect, verifier, ct).ConfigureAwait(false);
            return (token, clientId);
        }
    }

    /// <summary>Página que ve el usuario en el navegador al volver de 长桥.</summary>
    public static string CallbackHtml(bool ok, string appName) => ok
        ? $"<html><body style='font-family:-apple-system,Segoe UI,sans-serif;padding:40px;background:#202020;color:#e0e0e0;text-align:center'><h2>✓ Listo</h2><p>{appName} quedó autorizado en 长桥. Ya podés cerrar esta pestaña y volver a {appName}.</p></body></html>"
        : $"<html><body style='font-family:-apple-system,Segoe UI,sans-serif;padding:40px;background:#202020;color:#e0e0e0;text-align:center'><h2>No se autorizó</h2><p>Volvé a {appName} e intentá de nuevo.</p></body></html>";
}

/// <summary>Cliente HTTP de la API de 长桥 con token Bearer (renovado solo cuando está por vencer).</summary>
public sealed class LongbridgeHttp : IDisposable
{
    private readonly LongbridgeSettings _s;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _refresh = new(1, 1);

    public LongbridgeHttp(LongbridgeSettings settings) => _s = settings;

    /// <summary>Token de acceso vigente (renueva si vence en menos de 5 minutos).</summary>
    public async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        if (!_s.HasToken) throw new InvalidOperationException("Falta autorizar 长桥: en Conexiones tocá «Conectar con 长桥» para entrar con tu cuenta.");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_s.ExpiresAt == 0 || _s.ExpiresAt - now > 300) return _s.AccessToken;
        if (_s.RefreshToken.Length == 0) return _s.AccessToken;
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_s.ExpiresAt - now > 300) return _s.AccessToken;
            var t = await LongbridgeOAuth.RefreshAsync(_s.Http, _s.ClientId, _s.RefreshToken, ct).ConfigureAwait(false);
            _s.AccessToken = t.Access;
            _s.RefreshToken = t.Refresh;
            _s.ExpiresAt = t.ExpiresAt;
            try
            {
                _s.TokensRefreshed?.Invoke(_s);
            }
            catch (Exception ex)
            {
                LongbridgeLog.Warn("No se pudieron guardar los tokens renovados de 长桥: " + ex.Message);
            }

            return _s.AccessToken;
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>Región del centro de datos según el token (us_… → us; el resto → ap), como hacen los SDK oficiales.</summary>
    public static string DcRegion(string token) => token.StartsWith("us_", StringComparison.Ordinal) ? "us" : "ap";

    public async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        string token = await AccessTokenAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Get, _s.Http + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("X-Api-Key", _s.ClientId);
        req.Headers.TryAddWithoutValidation("X-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        req.Headers.TryAddWithoutValidation("x-dc-region", DcRegion(token));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string msg = body;
            try
            {
                using var err = JsonDocument.Parse(body);
                if (err.RootElement.TryGetProperty("message", out var m)) msg = m.GetString() ?? body;
            }
            catch (JsonException)
            {
            }

            throw new HttpRequestException(resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? "长桥 rechazó la sesión (volvé a conectar en Conexiones): " + msg
                : $"长桥 respondió {(int)resp.StatusCode}: {msg}");
        }

        return JsonDocument.Parse(body);
    }

    /// <summary>Contraseña de un solo uso para entrar al socket de cotizaciones.</summary>
    public async Task<string> SocketOtpAsync(CancellationToken ct)
    {
        using var doc = await GetAsync("/v1/socket/token", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var d) ? d : root;
        if (data.TryGetProperty("limit", out var limit) && data.TryGetProperty("online", out var online) && online.GetInt32() >= limit.GetInt32())
            throw new InvalidOperationException($"长桥 no deja más conexiones de datos ({online.GetInt32()} de {limit.GetInt32()} en uso): cerrá otra app o la CLI.");
        return data.TryGetProperty("otp", out var otp) ? otp.GetString() ?? "" : throw new InvalidOperationException("长桥 no dio la contraseña del socket: " + root.GetRawText());
    }

    public void Dispose() => _http.Dispose();
}
