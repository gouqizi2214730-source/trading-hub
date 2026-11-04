using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using TradingHub.App.Windows;
using TradingHub.App.Views;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using Yarp.ReverseProxy.Forwarder;

namespace TradingHub.App.Services.Workbench;

/// <summary>
/// El servidor local de KVP (solo en 127.0.0.1): sirve el Workbench — los módulos web que venían en VPFM (mapa de calor
/// FlowMap, aggr, Liquidity Mapper, derivados, opciones GEX, estructura, herramientas) — y la API de datos de KVP
/// (<c>/api/kvp/…</c>) que usan esos módulos y cualquier herramienta externa: un solo lugar para las velas bajadas, las
/// operaciones, el libro y la liquidez. Reemplaza al servidor de Python de VPFM en todo menos el motor del mapa de calor
/// de FlowMap, que corre aparte (<see cref="FlowMapSidecar"/>) y se alcanza a través de este mismo puerto.
/// </summary>
public sealed class WorkbenchServer : IAsyncDisposable
{
    /// <summary>Primer puerto que se intenta (VPFM usaba 47821: así pueden convivir).</summary>
    public const int FirstPort = 47831;

    private readonly AppServices _svc;
    private WebApplication? _app;
    private readonly HttpClient _http;
    private readonly HttpMessageInvoker _toSidecar = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    });

    public WorkbenchServer(AppServices svc)
    {
        _svc = svc;
        Sidecar = new FlowMapSidecar(svc);
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public FlowMapSidecar Sidecar { get; }

    /// <summary>«Buscar actualizaciones» desde la página de ajustes del Workbench (lo conecta la ventana principal).</summary>
    public static Action? UpdateRequested { get; set; }
    public int Port { get; private set; }
    public bool Running => _app != null;
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Carpeta de los módulos web (la arma el instalador: wb/ junto al programa; TRADINGHUB_WB_DIR la cambia).</summary>
    public static string WbDir => Environment.GetEnvironmentVariable("TRADINGHUB_WB_DIR") is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "wb");

    public static bool HasModules => File.Exists(Path.Combine(WbDir, "index.html"));

    private static bool PortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task StartAsync()
    {
        if (_app != null) return;
        int port = 0;
        for (int p = FirstPort; p < FirstPort + 30; p++)
            if (PortFree(p))
            {
                port = p;
                break;
            }

        if (port == 0) throw new InvalidOperationException("No hay un puerto libre para el servidor local");
        Port = port;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, Args = Array.Empty<string>() });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        builder.Services.AddHttpForwarder();
        var app = builder.Build();
        Map(app);
        await app.StartAsync().ConfigureAwait(false);
        _app = app;
        this.LogInfo($"Servidor local en {BaseUrl} (módulos: {(HasModules ? WbDir : "no instalados")})");
    }

    // ===================================================================== rutas

    private void Map(WebApplication app)
    {
        // solo páginas de esta misma PC (contra el «DNS rebinding»: el host tiene que ser 127.0.0.1 / localhost)
        app.Use(async (ctx, next) =>
        {
            var host = ctx.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
            {
                ctx.Response.StatusCode = 403;
                return;
            }

            await next();
        });
        // un error en una ruta sale como JSON con el mensaje (no una página vacía)
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex) when (!ctx.Response.HasStarted && ex is not OperationCanceledException)
            {
                this.LogWarn($"Servidor local {ctx.Request.Path}: {ex.Message}");
                await Error(ctx, 500, "error", ex.Message);
            }
        });
        app.UseWhen(c => c.Request.Path.StartsWithSegments("/api/kvp"), b => b.UseWebSockets());

        // «/» y «/wb» van a «/wb/» (sin una ruta: una ruta para «/wb» también tomaría «/wb/» y taparía los archivos)
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value;
            if (path is "/" or "/wb")
            {
                ctx.Response.Redirect("/wb/");
                return;
            }

            await next();
        });
        if (Directory.Exists(WbDir))
        {
            var files = new PhysicalFileProvider(WbDir);
            var types = new FileExtensionContentTypeProvider();
            foreach (var (ext, type) in new[] { (".js", "text/javascript"), (".mjs", "text/javascript"), (".wasm", "application/wasm"), (".map", "application/json"), (".webmanifest", "application/manifest+json") })
                types.Mappings[ext] = type;
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files, RequestPath = "/wb" });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                RequestPath = "/wb",
                ContentTypeProvider = types,
                ServeUnknownFileTypes = true,
                // después de actualizar KVP no quedan scripts viejos en el navegador
                OnPrepareResponse = r => r.Context.Response.Headers.CacheControl = "no-cache",
            });
        }
        else
        {
            app.MapGet("/wb/{**rest}", ctx => Html(ctx, 404, "<h2>Los módulos del Workbench no están instalados</h2><p>Esta versión de KVP no trae la carpeta wb. Instalá la versión completa desde Ajustes → Actualizaciones.</p>"));
        }

        // ---- lo que usaba el «外壳» de VPFM (/api/wb/…)
        app.MapMethods("/api/wb/proxy/{**target}", new[] { "GET", "POST", "HEAD" }, ProxyAsync);
        app.MapGet("/api/wb/info", Info);
        app.MapGet("/api/wb/settings", GetSettings);
        app.MapPost("/api/wb/settings", PostSettingsAsync);
        app.MapPost("/api/wb/update-check", ctx => SameOrigin(ctx, () => Ui(() => UpdateRequested?.Invoke()), new { ok = true }));
        app.MapPost("/api/wb/open", OpenUrlAsync);
        app.MapGet("/api/wb/apps", ctx => Json(ctx, new { fupanbook = new { path = (string?)null, download = "https://github.com/gouqizi2214730-source/trade-journal/releases" } }));
        app.MapPost("/api/wb/launch", ctx => Json(ctx, new { ok = false, reason = "not-installed", download = "https://github.com/gouqizi2214730-source/trade-journal/releases" }));
        app.MapPost("/api/wb/open-data", ctx => SameOrigin(ctx, () => Ui(() => Shell.OpenFolder(DataLocation.Root)), new { ok = true }));
        app.MapPost("/api/wb/window", WindowOpAsync);
        app.MapGet("/api/wb/windows", ctx => Json(ctx, new { open = WorkbenchWindows.OpenModules() }, noStore: true));

        // ---- la API de datos de KVP
        app.MapGet("/api/kvp/info", ctx => Json(ctx, new { app = TradingHub.Common.Product.Name, version = AppVersion, port = Port, modules = HasModules, heatmap = Sidecar.Available }));
        app.MapGet("/api/kvp/connections", Connections);
        app.MapGet("/api/kvp/open", OpenInstruments);
        app.MapGet("/api/kvp/klines", KlinesAsync);
        app.MapGet("/api/kvp/trades", TradesAsync);
        app.MapGet("/api/kvp/depth", DepthAsync);
        app.MapGet("/api/kvp/liquidity", LiquidityAsync);
        app.Map("/api/kvp/stream", StreamAsync);

        // ---- el resto (/api/… y /ws) es del motor del mapa de calor de FlowMap
        app.Map("/ws", ToSidecarAsync);
        app.Map("/api/{**rest}", ToSidecarAsync);
    }

    private static string AppVersion => typeof(WorkbenchServer).Assembly.GetName().Version?.ToString() ?? "";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static Task Json(HttpContext ctx, object value, int status = 200, bool noStore = false)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        if (noStore) ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(value, JsonOpts));
    }

    private static Task Error(HttpContext ctx, int status, string code, string message) => Json(ctx, new { error = new { code, message } }, status);

    private static Task Html(HttpContext ctx, int status, string body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        return ctx.Response.WriteAsync("<meta charset=utf-8><body style='background:#0b1018;color:#ccd;font:15px sans-serif;padding:40px'>" + body);
    }

    /// <summary>Una página de este mismo servidor (las acciones con efecto no se aceptan de otros sitios).</summary>
    private static bool IsSameOrigin(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0)
            return Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.Scheme == "http" && o.Host is "127.0.0.1" or "localhost" && o.Port == ctx.Connection.LocalPort;
        var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        return site is "" or "same-origin" or "none";
    }

    private static Task SameOrigin(HttpContext ctx, Action action, object result)
    {
        if (!IsSameOrigin(ctx)) return Error(ctx, 403, "forbidden", "Solo desde las páginas de KVP");
        action();
        return Json(ctx, result);
    }

    private static void Ui(Action a) => Dispatcher.UIThread.Post(() =>
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            Log.Write(TradingHub.Common.Logging.LogLevel.Warning, typeof(WorkbenchServer), "Workbench: " + ex.Message);
        }
    });

    private static Task<T> OnUi<T>(Func<T> f) => Dispatcher.UIThread.InvokeAsync(f).GetTask();

    // ===================================================================== /api/wb

    private Task Info(HttpContext ctx)
    {
        object? build = null;
        try
        {
            var vf = Path.Combine(WbDir, "version.json");
            if (File.Exists(vf)) build = JsonDocument.Parse(File.ReadAllText(vf)).RootElement.Clone();
        }
        catch
        {
            // sin versión
        }

        var (proxy, source) = EffectiveProxy();
        return Json(ctx, new
        {
            app = "VPFM", host = TradingHub.Common.Product.Name, hostVersion = AppVersion, build, wbDir = HasModules ? WbDir : null, proxy, proxySource = source,
            dataDir = DataLocation.Root, platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux",
            heatmap = Sidecar.Available,
        }, noStore: true);
    }

    private (string Proxy, string Source) EffectiveProxy()
    {
        var s = _svc.Settings;
        if (s.ProxyMode == NetworkProxy.None) return ("", "off");
        if (s.ProxyMode == NetworkProxy.Manual && NetworkProxy.Normalize(s.ProxyAddress) is { } u) return (u, "custom");
        try
        {
            var p = HttpClient.DefaultProxy.GetProxy(new Uri("https://api.binance.com"));
            if (p != null && p.Host != "api.binance.com") return (p.GetLeftPart(UriPartial.Authority), "system");
        }
        catch
        {
            // sin proxy del sistema
        }

        return ("", "none");
    }

    private Task GetSettings(HttpContext ctx)
    {
        var s = _svc.Settings;
        var (proxy, source) = EffectiveProxy();
        string mode = s.ProxyMode == NetworkProxy.Manual ? "custom" : s.ProxyMode == NetworkProxy.None ? "off" : "system";
        return Json(ctx, new { proxyMode = mode, proxy = s.ProxyAddress, effective = proxy, source }, noStore: true);
    }

    /// <summary>El proxy del Workbench es el de KVP (Ajustes → General): cambiarlo acá lo cambia para toda la app.</summary>
    private async Task PostSettingsAsync(HttpContext ctx)
    {
        if (!IsSameOrigin(ctx))
        {
            await Error(ctx, 403, "forbidden", "Solo desde las páginas de KVP");
            return;
        }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var root = doc.RootElement;
        string mode = root.TryGetProperty("proxyMode", out var m) ? m.GetString() ?? "system" : "system";
        string proxy = root.TryGetProperty("proxy", out var p) ? p.GetString() ?? "" : _svc.Settings.ProxyAddress;
        if (mode is not ("system" or "custom" or "off"))
        {
            await Error(ctx, 400, "bad_request", "proxyMode: system / custom / off");
            return;
        }

        if (mode == "custom" && NetworkProxy.Normalize(proxy) == null)
        {
            await Error(ctx, 400, "bad_request", "El proxy tiene que ser http://host:puerto");
            return;
        }

        await OnUi(() =>
        {
            _svc.Settings.ProxyMode = mode == "custom" ? NetworkProxy.Manual : mode == "off" ? NetworkProxy.None : NetworkProxy.System;
            _svc.Settings.ProxyAddress = proxy;
            _svc.Settings.Save();
            NetworkProxy.Apply(_svc.Settings.ProxyMode, _svc.Settings.ProxyAddress);
            return true;
        });
        var (eff, src) = EffectiveProxy();
        await Json(ctx, new { ok = true, effective = eff, source = src });
    }

    private async Task OpenUrlAsync(HttpContext ctx)
    {
        if (!IsSameOrigin(ctx))
        {
            await Error(ctx, 403, "forbidden", "Solo desde las páginas de KVP");
            return;
        }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        string url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await Error(ctx, 400, "bad_request", "Solo se abren direcciones web");
            return;
        }

        Ui(() => Shell.OpenUrl(url));
        await Json(ctx, new { ok = true });
    }

    private async Task WindowOpAsync(HttpContext ctx)
    {
        if (!IsSameOrigin(ctx))
        {
            await Error(ctx, 403, "forbidden", "Solo desde las páginas de KVP");
            return;
        }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        string op = doc.RootElement.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
        string mod = doc.RootElement.TryGetProperty("mod", out var m) ? m.GetString() ?? "" : "";
        if (!WorkbenchWindows.Operate(this, op, mod))
        {
            await Error(ctx, 400, "bad_request", "Operación de ventana desconocida");
            return;
        }

        await Json(ctx, new { ok = true });
    }

    // ===================================================================== proxy de los exchanges

    /// <summary>
    /// «/api/wb/proxy/https://…»: los módulos web piden acá las API públicas de los exchanges (para no chocar con CORS) y
    /// salen con el proxy de KVP. Las velas de Binance y Bybit no salen: se responden desde las velas que ya bajó KVP
    /// (la misma caché que usan los gráficos), en el mismo formato que el exchange.
    /// </summary>
    private async Task ProxyAsync(HttpContext ctx)
    {
        if (!IsSameOrigin(ctx))
        {
            await Error(ctx, 403, "forbidden", "Solo desde las páginas de KVP");
            return;
        }

        var target = PublicTarget(ctx.Request.RouteValues["target"]?.ToString() ?? "", ctx.Request.QueryString.Value ?? "");
        if (target == null)
        {
            await Error(ctx, 400, "bad_target", "Solo direcciones públicas http(s)");
            return;
        }

        try
        {
            if (ctx.Request.Method == "GET" && await KlineBridge.TryServeAsync(_svc, target, ctx).ConfigureAwait(false)) return;
        }
        catch (Exception ex)
        {
            this.LogWarn($"Velas de KVP para {target.Host}: {ex.Message} (se piden al exchange)");
        }

        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
        if (ctx.Request.Method == "POST")
        {
            var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            req.Content = new ByteArrayContent(ms.ToArray());
            if (ctx.Request.ContentType is { } ct) req.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
        }

        foreach (var h in new[] { "Accept", "Accept-Language" })
            if (ctx.Request.Headers.TryGetValue(h, out var v)) req.Headers.TryAddWithoutValidation(h, v.ToString());
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (KVP)");
        try
        {
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)res.StatusCode;
            if (res.Content.Headers.ContentType is { } type) ctx.Response.ContentType = type.ToString();
            ctx.Response.Headers.CacheControl = "no-store";
            await res.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        catch (TaskCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            await Error(ctx, 504, "timeout", "Se agotó el tiempo: " + target.Host);
        }
        catch (HttpRequestException ex)
        {
            var (proxy, _) = EffectiveProxy();
            await Error(ctx, 502, "upstream", ex.Message + (proxy.Length > 0 ? $" (proxy {proxy})" : " (sin proxy: si el exchange está bloqueado, configurá uno en Ajustes → General)"));
        }
    }

    /// <summary>La dirección pedida, si es pública (nada de la red local).</summary>
    public static Uri? PublicTarget(string raw, string query)
    {
        raw = raw.TrimStart('/');
        if (raw.StartsWith("https:/", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) raw = "https://" + raw[7..];
        else if (raw.StartsWith("http:/", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) raw = "http://" + raw[6..];
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        if (query.Length > 1) raw += (raw.Contains('?') ? "&" : "?") + query.TrimStart('?');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) return null;
        string host = u.Host.ToLowerInvariant().TrimEnd('.');
        if (host.Length == 0 || host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal") || host.EndsWith(".lan")) return null;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return null;
            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork && (b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] >= 224))
                return null;
        }

        return u;
    }

    // ===================================================================== motor del mapa de calor

    private async Task ToSidecarAsync(HttpContext ctx)
    {
        if (!Sidecar.Available)
        {
            await Error(ctx, 503, "no_heatmap", "El motor del mapa de calor (FlowMap) no viene en esta instalación de KVP");
            return;
        }

        if (!await Sidecar.EnsureAsync(ctx.RequestAborted))
        {
            await Error(ctx, 503, "heatmap_starting", Sidecar.LastError ?? "El motor del mapa de calor no arrancó");
            return;
        }

        var forwarder = ctx.RequestServices.GetRequiredService<IHttpForwarder>();
        var err = await forwarder.SendAsync(ctx, Sidecar.BaseUrl, _toSidecar, ForwarderRequestConfig.Empty, HttpTransformer.Default);
        if (err != ForwarderError.None && !ctx.Response.HasStarted) await Error(ctx, 502, "heatmap", "Motor del mapa de calor: " + err);
    }

    // ===================================================================== API de datos de KVP

    private Task Connections(HttpContext ctx)
    {
        var list = _svc.Connections.Profiles.Select(p => new { id = p.Id, name = p.Name, kind = p.Kind.ToString(), feed = p.MarketDataId, enabled = p.Enabled }).ToList();
        return Json(ctx, list, noStore: true);
    }

    private async Task OpenInstruments(HttpContext ctx)
    {
        var list = await OnUi(() => _svc.Hub.Items.Select(d => new
        {
            feed = d.Instrument.FeedId, symbol = d.Instrument.Symbol, tick = d.Instrument.TickSize, last = d.LastPrice, trades = d.Trades.Count,
            bookSamples = d.Liquidity.Count,
        }).ToList());
        await Json(ctx, list, noStore: true);
    }

    private static string Q(HttpContext ctx, string name, string def = "") => ctx.Request.Query.TryGetValue(name, out var v) ? v.ToString() : def;

    private static DateTime? Ms(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;

    /// <summary>
    /// GET /api/kvp/klines?conn=&amp;symbol=&amp;tf=1m|5m|15m|1h|4h|1d&amp;from=ms&amp;to=ms&amp;limit=n →
    /// [[apertura ms, apertura, máximo, mínimo, cierre, volumen, volumen comprador], …], de la caché de velas de KVP.
    /// </summary>
    private async Task KlinesAsync(HttpContext ctx)
    {
        string symbol = Q(ctx, "symbol").ToUpperInvariant();
        int minutes = KlineBridge.Minutes(Q(ctx, "tf", "1m"));
        if (symbol.Length == 0 || minutes <= 0)
        {
            await Error(ctx, 400, "bad_request", "Faltan symbol y tf (1m, 5m, 15m, 1h, 4h, 1d…)");
            return;
        }

        var feed = KlineBridge.FeedFor(_svc, Q(ctx, "conn"));
        if (feed == null)
        {
            await Error(ctx, 404, "no_feed", "Esa conexión no tiene velas");
            return;
        }

        int limit = int.TryParse(Q(ctx, "limit"), out var l) ? Math.Clamp(l, 1, 100_000) : 1000;
        var to = Ms(Q(ctx, "to")) ?? DateTime.UtcNow;
        var from = Ms(Q(ctx, "from")) ?? to - TimeSpan.FromMinutes((double)minutes * limit);
        // el instrumento de verdad (con su tick) si la conexión ya lo conoce
        Instrument? inst = null;
        var profile = _svc.Connections.Find(Q(ctx, "conn"));
        if (profile != null)
        {
            try
            {
                var list = await OnUi(() => TradingHub.App.Controls.InstrumentPicker.InstrumentsAsync(_svc, profile.Id)).Unwrap();
                inst = list.FirstOrDefault(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // sin la lista: se arma con el símbolo
            }
        }

        var bars = await KlineBridge.LoadAsync(feed, inst?.Symbol ?? symbol, minutes, from, to, ctx.RequestAborted, inst);
        if (bars.Count > limit) bars = bars.Skip(bars.Count - limit).ToList();
        await Json(ctx, bars.Select(k => new object[] { k.OpenTimeMs, k.Open, k.High, k.Low, k.Close, k.Volume, k.BuyVolume }).ToList());
    }

    private InstrumentData? Find(string feed, string symbol) =>
        _svc.Hub.Items.FirstOrDefault(d => d.Instrument.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && (feed.Length == 0 || d.Instrument.FeedId == feed));

    /// <summary>GET /api/kvp/trades?feed=&amp;symbol=&amp;limit= → [[ms, precio, volumen, "b"|"s"], …] de un instrumento abierto en KVP.</summary>
    private async Task TradesAsync(HttpContext ctx)
    {
        string feed = Q(ctx, "feed"), symbol = Q(ctx, "symbol");
        int limit = int.TryParse(Q(ctx, "limit"), out var l) ? Math.Clamp(l, 1, 100_000) : 500;
        var rows = await OnUi(() =>
        {
            var d = Find(feed, symbol);
            if (d == null) return null;
            var s = d.Trades;
            int from = Math.Max(0, s.Count - limit);
            var res = new List<object[]>(s.Count - from);
            for (int i = from; i < s.Count; i++)
                res.Add(new object[] { (s.GetTimeTicks(i) - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond, s.GetPrice(i), s.GetVolume(i), s.GetSide(i) == TradeSide.Buy ? "b" : "s" });
            return res;
        });
        if (rows == null) await Error(ctx, 404, "not_open", "Ese instrumento no está abierto en KVP (abrilo en un gráfico o usá /api/kvp/stream)");
        else await Json(ctx, rows, noStore: true);
    }

    /// <summary>GET /api/kvp/depth?feed=&amp;symbol=&amp;levels= → {time, bids:[[precio, tamaño]…], asks:[…]}.</summary>
    private async Task DepthAsync(HttpContext ctx)
    {
        string feed = Q(ctx, "feed"), symbol = Q(ctx, "symbol");
        int levels = int.TryParse(Q(ctx, "levels"), out var l) ? Math.Clamp(l, 1, 5000) : 100;
        var snap = await OnUi(() => Find(feed, symbol) is { } d ? (d.Instrument, d.Depth) : ((Instrument, DepthSnapshot)?)null);
        if (snap is not { } s)
        {
            await Error(ctx, 404, "not_open", "Ese instrumento no está abierto en KVP");
            return;
        }

        await Json(ctx, BookJson(s.Item1, s.Item2, levels), noStore: true);
    }

    private static object BookJson(Instrument inst, DepthSnapshot d, int levels) => new
    {
        time = new DateTimeOffset(d.Time == DateTime.MinValue ? DateTime.UtcNow : DateTime.SpecifyKind(d.Time, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        bids = d.Bids.Take(levels).Select(b => new object[] { inst.FromTicks(b.PriceTicks), b.Size }).ToList(),
        asks = d.Asks.Take(levels).Select(a => new object[] { inst.FromTicks(a.PriceTicks), a.Size }).ToList(),
    };

    /// <summary>
    /// GET /api/kvp/liquidity?feed=&amp;symbol=&amp;minutes= → las fotos del libro del mapa de calor:
    /// [{t, bid, ask, bucket, bids:[[ticks desde el mejor, tamaño]…], asks:[…]}, …] (precios en ticks; tick en «tick»).
    /// </summary>
    private async Task LiquidityAsync(HttpContext ctx)
    {
        string feed = Q(ctx, "feed"), symbol = Q(ctx, "symbol");
        int minutes = int.TryParse(Q(ctx, "minutes"), out var m) ? Math.Clamp(m, 1, 60 * 72) : 60;
        var got = await OnUi(() => Find(feed, symbol) is { } d ? (d.Instrument, d.Liquidity) : ((Instrument, LiquidityHistory)?)null);
        if (got is not { } g)
        {
            await Error(ctx, 404, "not_open", "Ese instrumento no está abierto en KVP");
            return;
        }

        var samples = g.Item2.Between(DateTime.UtcNow.AddMinutes(-minutes), DateTime.UtcNow.AddMinutes(1));
        await Json(ctx, new
        {
            tick = g.Item1.TickSize,
            samples = samples.Select(s => new
            {
                t = new DateTimeOffset(DateTime.SpecifyKind(s.Time, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), bid = s.BestBid, ask = s.BestAsk, bucket = s.Bucket,
                bids = s.BidOffsets.Select((o, i) => new object[] { o, s.BidSizes[i] }).ToList(),
                asks = s.AskOffsets.Select((o, i) => new object[] { o, s.AskSizes[i] }).ToList(),
            }).ToList(),
        }, noStore: true);
    }

    /// <summary>
    /// WebSocket /api/kvp/stream?conn=&amp;symbol= → en vivo, del mismo instrumento que usan los gráficos (se abre si no
    /// estaba): {"type":"trades","data":[[ms, precio, volumen, "b"|"s"]…]} y {"type":"book","bids":…,"asks":…} cada 250 ms.
    /// </summary>
    private async Task StreamAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            await Error(ctx, 400, "bad_request", "Esto es un WebSocket");
            return;
        }

        string conn = Q(ctx, "conn"), symbol = Q(ctx, "symbol").ToUpperInvariant();
        var data = await OnUi(async () =>
        {
            var p = _svc.Connections.Find(conn) ?? _svc.Connections.Enabled.FirstOrDefault(x => x.Kind is ConnectionKind.BinanceFutures or ConnectionKind.Bybit);
            if (p == null) return null;
            var list = await TradingHub.App.Controls.InstrumentPicker.InstrumentsAsync(_svc, p.Id);
            var inst = list.FirstOrDefault(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            return inst == null ? null : _svc.Open(p.Id, inst);
        }).ConfigureAwait(false);
        var resolved = data == null ? null : await data.ConfigureAwait(false);
        if (resolved == null)
        {
            await Error(ctx, 404, "not_found", "No encontré esa conexión o ese símbolo");
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var inst2 = resolved.Instrument;
        var queue = new System.Collections.Concurrent.ConcurrentQueue<object[]>();
        DepthSnapshot? book = null;
        void OnTrades(int start, int count)
        {
            var s = resolved.Trades;
            for (int i = start; i < start + count && i < s.Count; i++)
                queue.Enqueue(new object[] { (s.GetTimeTicks(i) - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond, s.GetPrice(i), s.GetVolume(i), s.GetSide(i) == TradeSide.Buy ? "b" : "s" });
        }

        void OnDepth(DepthUpdate u) => Volatile.Write(ref book, u.Snapshot);
        await OnUi(() =>
        {
            resolved.TradesAppended += OnTrades;
            resolved.DepthUpdated += OnDepth;
            return true;
        });
        try
        {
            var ct = ctx.RequestAborted;
            var recv = Task.Run(async () =>
            {
                var buf = new byte[1024];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }, ct);
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested && !recv.IsCompleted)
            {
                await Task.Delay(250, ct);
                var batch = new List<object[]>();
                while (queue.TryDequeue(out var t) && batch.Count < 5000) batch.Add(t);
                if (batch.Count > 0) await Send(ws, new { type = "trades", symbol = inst2.Symbol, data = batch }, ct);
                if (Interlocked.Exchange(ref book, null) is { } b) await Send(ws, new { type = "book", symbol = inst2.Symbol, book = BookJson(inst2, b, 200) }, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // se cerró
        }
        finally
        {
            Ui(() =>
            {
                resolved.TradesAppended -= OnTrades;
                resolved.DepthUpdated -= OnDepth;
            });
        }
    }

    private static Task Send(WebSocket ws, object msg, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, JsonOpts)), WebSocketMessageType.Text, true, ct);

    public async ValueTask DisposeAsync()
    {
        Sidecar.Dispose();
        if (_app != null)
        {
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _app.StopAsync(stop.Token).ConfigureAwait(false);
                await _app.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // cerrando
            }
        }

        _http.Dispose();
        _toSidecar.Dispose();
    }
}
