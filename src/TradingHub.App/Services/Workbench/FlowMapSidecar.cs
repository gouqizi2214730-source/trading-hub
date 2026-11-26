using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TradingHub.Common.Logging;

namespace TradingHub.App.Services.Workbench;

public sealed class FlowMapSidecar : IDisposable
{
    private readonly AppServices _svc;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _proc;
    private readonly HttpClient _probe = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };

    public FlowMapSidecar(AppServices svc) => _svc = svc;

    public static string Dir => Environment.GetEnvironmentVariable("TRADINGHUB_FLOWMAP_DIR") is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "flowmap");

    public static string? Python
    {
        get
        {
            foreach (var p in new[] { Path.Combine(Dir, "pyruntime", "python.exe"), Path.Combine(Dir, "pyruntime", "bin", "python3") })
                if (File.Exists(p)) return p;
            return null;
        }
    }

    public bool Available => Python != null;
    public int Port { get; private set; }
    public string BaseUrl => "http" + "://127.0.0.1:" + Port;
    public string? LastError { get; private set; }
    public bool Running => _proc is { HasExited: false };

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    public void Touch() => LastActivityUtc = DateTime.UtcNow;

    public bool ReclaimIfIdle()
    {
        if (!Running || IdleTimeout <= TimeSpan.Zero) return false;
        if (DateTime.UtcNow - LastActivityUtc < IdleTimeout) return false;
        this.LogInfo("Motor del mapa de calor detenido por inactividad para liberar recursos.");
        Stop();
        return true;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
        Touch();
        if (Running && await HealthyAsync().ConfigureAwait(false)) return true;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Running && await HealthyAsync().ConfigureAwait(false)) return true;
            var py = Python;
            if (py == null)
            {
                LastError = "Esta instalación no trae el motor del mapa de calor";
                return false;
            }

            Stop();
            Port = FreePort();
            var data = Path.Combine(DataLocation.Root, "Workbench", "flowmap");
            Directory.CreateDirectory(data);
            var psi = new ProcessStartInfo(py, "-m flowmap_server")
            {
                WorkingDirectory = data,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["FLOWMAP_HOST"] = "127.0.0.1";
            psi.Environment["FLOWMAP_PORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            psi.Environment["FLOWMAP_DATA_DIR"] = data;
            psi.Environment["VPFM_WB_DIR"] = WorkbenchServer.WbDir;
            psi.Environment["FLOWMAP_USE_KVP_BRIDGE"] = "1";
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            var s = _svc.Settings;
            if (s.ProxyMode == NetworkProxy.Manual && NetworkProxy.Normalize(s.ProxyAddress) is { } proxy)
                foreach (var k in new[] { "http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ws_proxy", "wss_proxy" })
                    psi.Environment[k] = proxy;
            psi.Environment["no_proxy"] = "localhost,127.0.0.1,::1";
            var log = Path.Combine(data, "flowmap-server.log");
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var writer = TextWriter.Synchronized(new StreamWriter(log, append: false) { AutoFlush = true });
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) writer.WriteLine(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) writer.WriteLine(e.Data);
            };
            proc.Exited += (_, _) => writer.Dispose();
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _proc = proc;
            Touch();
            this.LogInfo($"Motor del mapa de calor arrancando en {BaseUrl}");
            for (int i = 0; i < 180 && !ct.IsCancellationRequested; i++)
            {
                if (proc.HasExited)
                {
                    LastError = $"El motor del mapa de calor se cerró al arrancar (código {proc.ExitCode}; ver {log})";
                    return false;
                }

                if (await HealthyAsync().ConfigureAwait(false))
                {
                    LastError = null;
                    Touch();
                    return true;
                }

                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            LastError = "El motor del mapa de calor no respondió a tiempo";
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> HealthyAsync()
    {
        if (Port == 0) return false;
        try
        {
            using var r = await _probe.GetAsync(BaseUrl + "/api/health").ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void Stop()
    {
        var p = Interlocked.Exchange(ref _proc, null);
        if (p == null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        finally
        {
            p.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        _probe.Dispose();
        _gate.Dispose();
    }
}
