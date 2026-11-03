using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace TradingHub.Feeds.Longbridge;

/// <summary>
/// Socket de cotizaciones de 长桥 (protocolo binario v1 + protobuf): una sola conexión por feed, con pedidos
/// (id → respuesta), pushes (operaciones, profundidad, cotización) y reconexión automática con las suscripciones vivas.
/// </summary>
public sealed class LongbridgeSocket : IDisposable
{
    private readonly LongbridgeHttp _http;
    private readonly LongbridgeSettings _s;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly SemaphoreSlim _rate = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<LbPacket.Parsed>> _pending = new();
    private readonly object _lock = new();
    private readonly HashSet<string> _subscribed = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ClientWebSocket? _ws;
    private Task? _loop;
    private int _nextId;
    private DateTime _lastSend = DateTime.MinValue;
    private string? _sessionId;
    private DateTime _sessionExpires;
    private string? _lastError;

    public LongbridgeSocket(LongbridgeHttp http, LongbridgeSettings settings)
    {
        _http = http;
        _s = settings;
    }

    /// <summary>Pushes del servidor (comando, cuerpo protobuf). Se llama desde el hilo de lectura.</summary>
    public event Action<byte, byte[]>? Push;

    /// <summary>Estado de la conexión (para las suscripciones).</summary>
    public event Action<FeedStatus, string?>? Status;

    public bool IsConnected => _ws is { State: WebSocketState.Open } && _ready.Task.IsCompletedSuccessfully;

    /// <summary>Mínimo entre pedidos (la API limita por segundo).</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromMilliseconds(120);

    private void Report(FeedStatus st, string? msg) => Status?.Invoke(st, msg);

    /// <summary>Arranca la conexión (si no estaba) y espera a que esté autenticada.</summary>
    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        Task<bool> ready;
        lock (_lock)
        {
            _loop ??= Task.Run(RunAsync);
            ready = _ready.Task;
        }

        if (ready.IsCompletedSuccessfully) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await ready.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(_lastError ?? "长桥 no respondió a tiempo");
        }
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            bool any = false;
            string reason = "se cortó la conexión";
            ClientWebSocket? ws = null;
            try
            {
                Report(FeedStatus.Connecting, "Conectando a 长桥…");
                bool reconnect = _sessionId != null && DateTime.UtcNow < _sessionExpires.AddMinutes(-1);
                string? otp = reconnect ? null : await _http.SocketOtpAsync(ct).ConfigureAwait(false);
                ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                ws.Options.SetRequestHeader("Accept-Language", "zh-CN");
                using (var c = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    c.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(new Uri(_s.QuoteWs + "?version=1&codec=1&platform=9"), c.Token).ConfigureAwait(false);
                }

                _ws = ws;
                var reader = Task.Run(() => ReadLoopAsync(ws, ct), ct);
                // autenticación (o reconexión de la misma sesión)
                var auth = reconnect
                    ? await RequestRawAsync(LbCmd.Reconnect, LbMessages.ReconnectRequest(_sessionId!), 5000, ct).ConfigureAwait(false)
                    : await RequestRawAsync(LbCmd.Auth, LbMessages.AuthRequest(otp!), 10000, ct).ConfigureAwait(false);
                if (auth.Status != LbPacket.StatusSuccess)
                {
                    var (code, msg) = LbMessages.ReadError(auth.Body);
                    if (reconnect)
                    {
                        _sessionId = null; // la sesión venció: la próxima vuelta entra de nuevo con OTP
                        throw new InvalidOperationException($"la sesión venció ({msg})");
                    }

                    throw new InvalidOperationException($"长桥 no aceptó la autenticación ({code}): {msg}");
                }

                var (sid, expires) = LbMessages.ReadAuthResponse(auth.Body);
                if (sid.Length > 0)
                {
                    _sessionId = sid;
                    _sessionExpires = expires > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(expires).UtcDateTime : DateTime.UtcNow.AddHours(1);
                }

                failures = 0;
                any = true;
                _lastError = null;
                _ready.TrySetResult(true);
                Report(FeedStatus.Connected, null);
                await ResubscribeAsync(ct).ConfigureAwait(false);
                await reader.ConfigureAwait(false);
                reason = "长桥 cerró la conexión";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                _lastError = reason;
                if (!any) failures++;
            }
            finally
            {
                lock (_lock)
                {
                    if (_ready.Task.IsCompleted) _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                FailPending(new IOException(reason));
                try
                {
                    ws?.Dispose();
                }
                catch
                {
                    // ya cerrado
                }

                _ws = null;
            }

            if (ct.IsCancellationRequested) break;
            var wait = LiveSubscription.Backoff(failures);
            Report(FeedStatus.Reconnecting, $"Reconectando en {wait.TotalSeconds:0} s ({reason})");
            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Report(FeedStatus.Disconnected, null);
    }

    private void FailPending(Exception ex)
    {
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(ex);
    }

    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[1 << 16];
        var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);

            if (r.MessageType != WebSocketMessageType.Binary) continue;
            LbPacket.Parsed p;
            try
            {
                p = LbPacket.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length));
            }
            catch (Exception ex)
            {
                LongbridgeLog.Warn("长桥: paquete ilegible: " + ex.Message);
                continue;
            }

            switch (p.Type)
            {
                case LbPacket.TypeResponse:
                    if (_pending.TryRemove(p.RequestId, out var tcs)) tcs.TrySetResult(p);
                    break;
                case LbPacket.TypePush:
                    try
                    {
                        Push?.Invoke(p.Cmd, p.Body);
                    }
                    catch (Exception ex)
                    {
                        LongbridgeLog.Warn("长桥: error procesando un push: " + ex.Message);
                    }

                    break;
                case LbPacket.TypeRequest:
                    if (p.Cmd == LbCmd.Heartbeat)
                    {
                        // el servidor pregunta si seguimos: se le devuelve lo mismo
                        _ = SendAsync(ws, LbPacket.Response(LbCmd.Heartbeat, p.RequestId, LbPacket.StatusSuccess, p.Body), ct);
                    }
                    else if (p.Cmd == LbCmd.Close)
                    {
                        var (code, msg) = LbMessages.ReadError(p.Body);
                        throw new IOException($"长桥 cerró la conexión ({code}): {msg}");
                    }

                    break;
            }
        }
    }

    private async Task SendAsync(ClientWebSocket ws, byte[] data, CancellationToken ct)
    {
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(data, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task<LbPacket.Parsed> RequestRawAsync(byte cmd, byte[] body, int timeoutMs, CancellationToken ct)
    {
        var ws = _ws ?? throw new IOException("sin conexión con 长桥");
        uint id = (uint)Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<LbPacket.Parsed>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(ws, LbPacket.Request(cmd, id, (ushort)Math.Min(timeoutMs, ushort.MaxValue), body), ct).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(timeoutMs + 2000);
            return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"长桥 no respondió al comando {cmd}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Manda un pedido y devuelve el cuerpo de la respuesta (lanza con el mensaje del servidor si falló).</summary>
    public async Task<byte[]> RequestAsync(byte cmd, byte[] body, CancellationToken ct, int timeoutMs = 10000)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await _rate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = MinInterval - (DateTime.UtcNow - _lastSend);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _lastSend = DateTime.UtcNow;
        }
        finally
        {
            _rate.Release();
        }

        for (int attempt = 0; ; attempt++)
        {
            var p = await RequestRawAsync(cmd, body, timeoutMs, ct).ConfigureAwait(false);
            if (p.Status == LbPacket.StatusSuccess) return p.Body;
            var (code, msg) = LbMessages.ReadError(p.Body);
            // 301606: límite de pedidos; se espera y se reintenta
            if (code == 301606 && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 + attempt), ct).ConfigureAwait(false);
                continue;
            }

            throw new LongbridgeException(p.Status, code, msg);
        }
    }

    /// <summary>Suscribe símbolos (operaciones, profundidad y cotización); se repite al reconectar.</summary>
    public async Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        var list = symbols.ToList();
        lock (_lock)
        {
            foreach (var s in list) _subscribed.Add(s);
        }

        await RequestAsync(LbCmd.Subscribe, LbMessages.SubscribeRequest(list, new[] { LbCmd.SubQuote, LbCmd.SubDepth, LbCmd.SubTrade }, firstPush: true), ct).ConfigureAwait(false);
    }

    public async Task UnsubscribeAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        var list = symbols.ToList();
        lock (_lock)
        {
            foreach (var s in list) _subscribed.Remove(s);
        }

        if (!IsConnected) return;
        try
        {
            await RequestAsync(LbCmd.Unsubscribe, LbMessages.UnsubscribeRequest(list, new[] { LbCmd.SubQuote, LbCmd.SubDepth, LbCmd.SubTrade }), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LongbridgeLog.Warn("长桥: no se pudo desuscribir: " + ex.Message);
        }
    }

    private async Task ResubscribeAsync(CancellationToken ct)
    {
        List<string> list;
        lock (_lock)
        {
            list = _subscribed.ToList();
        }

        if (list.Count == 0) return;
        try
        {
            await RequestAsync(LbCmd.Subscribe, LbMessages.SubscribeRequest(list, new[] { LbCmd.SubQuote, LbCmd.SubDepth, LbCmd.SubTrade }, firstPush: true), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LongbridgeLog.Warn("长桥: no se pudieron volver a suscribir los símbolos: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        FailPending(new ObjectDisposedException(nameof(LongbridgeSocket)));
        try
        {
            _ws?.Dispose();
        }
        catch
        {
            // ya cerrado
        }
    }
}

/// <summary>Error de la API de 长桥 (estado del paquete, código y mensaje del servidor).</summary>
public sealed class LongbridgeException : Exception
{
    public LongbridgeException(byte status, long code, string message) : base(Describe(status, code, message))
    {
        Status = status;
        Code = code;
    }

    public byte Status { get; }
    public long Code { get; }

    private static string Describe(byte status, long code, string message) => status switch
    {
        LbPacket.StatusUnauthenticated => "长桥: sesión no autenticada (volvé a conectar en Conexiones). " + message,
        LbPacket.StatusPermissionDenied => "长桥: sin permiso para esos datos (cotizaciones no incluidas en tu cuenta). " + message,
        _ => $"长桥 ({code}): {message}",
    };
}
