using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TradingHub.Common.Logging;

namespace TradingHub.App.Services.Workbench;

/// <summary>
/// El motor del mapa de calor de FlowMap (su servidor de Python, con su propio runtime embebido en el instalador de KVP:
/// flowmap/pyruntime). Arranca la primera vez que el módulo «热力» lo necesita, escucha solo en 127.0.0.1 en un puerto
/// interno y el servidor local de KVP le pasa /api y /ws (mismo origen para la página). Se cierra con KVP.
/// </summary>
public sealed class FlowMapSidecar : IDisposable
{
    private readonly AppServices _svc;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _proc;
    private readonly HttpClient _probe = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };

    public FlowMapSidecar(AppServices svc) => _svc = svc;

    public static string Dir => Environment.GetEnvironmentVariable("TRADINGHUB_FLOWMAP_DIR") is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "flowmap");

    /// <summary>El intérprete del runtime embebido (null si esta instalación no lo trae).</summary>
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
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string? LastError { get; private set; }
    public bool Running => _proc is { HasExited: false };

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Que esté corriendo y respondiendo (lo arranca si hace falta; tarda unos segundos la primera vez).</summary>
    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
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
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            // el proxy de KVP (Ajustes → General) también para el motor
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
            // ya terminó
        }

        p.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _probe.Dispose();
    }
}
