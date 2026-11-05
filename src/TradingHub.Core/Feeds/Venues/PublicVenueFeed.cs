using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Venues;

/// <summary>Un mercado cripto (para el filtro spot / perpetuo y el nombre del exchange en el flujo por exchange).</summary>
public interface ICryptoVenue
{
    /// <summary>El exchange (OKX, Binance…): un mercado y su spot son el mismo exchange.</summary>
    string Venue { get; }

    bool IsSpot { get; }

    /// <summary>La cotización con más volumen del exchange (Coinbase: USD), la que entra en el agregado.</summary>
    string PreferredQuote => "USDT";
}

/// <summary>
/// Datos públicos de un exchange cripto por WebSocket (operaciones y libro), sin cuenta ni claves, como los lee
/// LiquidityMapperFast. Las cantidades se pasan a monedas (contratos × tamaño del contrato; en los inversos, dólares ÷
/// precio). La historia de operaciones es lo que KVP va grabando: la API pública de estos mercados solo da lo último.
/// </summary>
public abstract class PublicVenueFeed : IMarketDataFeed, ICryptoVenue
{
    /// <summary>Lo propio de cada instrumento del exchange: monedas por unidad de tamaño y si es inverso (tamaño en USD).</summary>
    public sealed record Spec(string Native, decimal Multiplier = 1, bool Inverse = false);

    protected readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentDictionary<string, Spec> _specs = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    protected PublicVenueFeed() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("KVP/1.0");

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Venue { get; }
    public abstract bool IsSpot { get; }
    public virtual string PreferredQuote => "USDT";
    public bool SupportsHistory => true;
    public bool IsLive => true;
    public bool RecentTradesOnly => true;

    /// <summary>Cuánto puede pasar sin mensajes antes de reconectar.</summary>
    protected virtual TimeSpan Stale => TimeSpan.FromSeconds(30);

    /// <summary>Mensaje que mantiene viva la conexión y cada cuánto (null = ninguno).</summary>
    protected virtual (TimeSpan Every, string Frame)? Keepalive => null;

    protected abstract Task<List<(Instrument Instrument, Spec Spec)>> ListAsync(CancellationToken ct);
    protected abstract Uri WsUri(Instrument instrument, Spec spec);
    protected abstract IEnumerable<string> SubscribeFrames(Instrument instrument, Spec spec);

    /// <summary>Un mensaje del WebSocket. Un salto en la secuencia del libro: <see cref="Session.Resync"/>.</summary>
    protected abstract void OnMessage(string text, Session s);

    /// <summary>Pasa un mensaje como si llegara del WebSocket (para las pruebas). Devuelve las operaciones que salieron.</summary>
    public List<Trade> Process(string text, Session s)
    {
        OnMessage(text, s);
        var res = new List<Trade>(s.Trades);
        s.Trades.Clear();
        return res;
    }

    /// <summary>Registra el tamaño de contrato de un instrumento (para las pruebas o si la lista no se pudo leer).</summary>
    public void SetSpec(Instrument i, Spec spec) => _specs[i.Symbol] = spec;

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var list = await ListAsync(ct).ConfigureAwait(false);
        foreach (var (i, s) in list) _specs[i.Symbol] = s;
        string[] popular = { "BTC", "ETH", "SOL", "XRP", "DOGE" };
        return list.Select(x => x.Instrument)
            .OrderBy(i => Array.IndexOf(popular, i.BaseAsset) is var k && k >= 0 ? k : 999)
            .ThenBy(i => i.Symbol, StringComparer.Ordinal)
            .ToList();
    }

    protected Spec SpecOf(Instrument i) => _specs.TryGetValue(i.Symbol, out var s) ? s : new Spec(i.Symbol);

    public virtual Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        progress?.Report(1);
        return Task.CompletedTask;
    }

    // ======================================================================= ayudas para leer JSON

    /// <summary>Número de un JSON (viene como número o como texto); 0 si no se puede leer.</summary>
    public static decimal D(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                return e.TryGetDecimal(out var d) ? d : (decimal)e.GetDouble();
            case JsonValueKind.String:
                return decimal.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            default:
                return 0;
        }
    }

    public static decimal D(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var e) ? D(e) : 0;

    public static string S(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var e) ? e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString() : "";

    public static DateTime Ms(decimal ms) => DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime;

    /// <summary>Quita los ceros de más (0.10000 → 0.1) para que el tick se muestre bien.</summary>
    public static decimal Norm(decimal v) => v / 1.0000000000000000000000000000m;

    protected static decimal Pow10(int exp)
    {
        decimal r = 1;
        if (exp >= 0)
            for (int i = 0; i < exp; i++) r *= 10;
        else
            for (int i = 0; i < -exp; i++) r /= 10;
        return r;
    }

    protected async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct) =>
        JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false));

    // ======================================================================= en vivo

    /// <summary>Lo que el mensaje de un exchange le pasa al feed: operaciones nuevas y cambios del libro.</summary>
    public sealed class Session
    {
        internal readonly List<Trade> Trades = new();
        internal bool BookChanged;
        internal bool Snapshot;

        public Session(Instrument instrument, Spec spec)
        {
            Instrument = instrument;
            Spec = spec;
        }

        public Instrument Instrument { get; }
        public Spec Spec { get; }
        public OrderBookBuilder Book { get; } = new();

        /// <summary>Estado propio del exchange (la última secuencia del libro, por ejemplo).</summary>
        public long Seq { get; set; } = -1;

        /// <summary>Monedas de una cantidad del exchange (contratos o, en los inversos, dólares).</summary>
        public decimal Coins(decimal size, decimal price) =>
            Spec.Inverse ? (price > 0 ? size / price : 0) : size * Spec.Multiplier;

        public void Trade(DateTime time, decimal price, decimal size, bool buy)
        {
            decimal coins = Coins(size, price);
            if (price <= 0 || coins <= 0) return;
            Trades.Add(new Trade(0, time, price, coins, buy ? TradeSide.Buy : TradeSide.Sell));
        }

        /// <summary>Un nivel del libro (cantidad 0 lo borra).</summary>
        public void Level(BookSide side, decimal price, decimal size)
        {
            if (price <= 0) return;
            Book.Set(side, Instrument.ToTicks(price), Math.Max(0, Coins(size, price)));
            BookChanged = true;
        }

        /// <summary>Filas [precio, cantidad, …] de un lado.</summary>
        public void Levels(BookSide side, JsonElement rows)
        {
            if (rows.ValueKind != JsonValueKind.Array) return;
            foreach (var r in rows.EnumerateArray())
                if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() >= 2) Level(side, D(r[0]), D(r[1]));
        }

        /// <summary>Empieza un libro nuevo (foto completa).</summary>
        public void Clear()
        {
            Book.Clear();
            Snapshot = true;
            BookChanged = true;
        }

        /// <summary>El libro perdió la secuencia: se reconecta y se pide de nuevo.</summary>
        public static void Resync(string reason) => throw new VenueResyncException(reason);
    }

    public sealed class VenueResyncException(string message) : Exception(message);

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSubscription(DisplayName, (st, msg) => sink.OnStatus(instrument, st, msg));
        _ = Task.Run(() => RunAsync(instrument, sink, sub));
        return sub;
    }

    private async Task RunAsync(Instrument instrument, IMarketDataSink sink, LiveSubscription liveSub)
    {
        var ct = liveSub.Token;
        int failures = 0, requested = 0;
        bool everConnected = false;
        DateTime lastTradeTime = DateTime.MinValue;
        if (!_specs.ContainsKey(instrument.Symbol))
        {
            try
            {
                await GetInstrumentsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // sin la lista se usa tamaño 1 (y se vuelve a intentar al reconectar)
            }
        }

        while (!ct.IsCancellationRequested)
        {
            var spec = SpecOf(instrument);
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            ws.Options.SetRequestHeader("User-Agent", "KVP/1.0");
            var conn = liveSub.BeginConnection();
            var session = new Session(instrument, spec);
            bool receivedAny = false;
            string reason = "se cortó la conexión";
            try
            {
                if (!everConnected) liveSub.SetChannel("datos", FeedStatus.Connecting, $"Conectando a {DisplayName}…");
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(conn.Token))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(WsUri(instrument, spec), connectCts.Token).ConfigureAwait(false);
                }

                foreach (var f in SubscribeFrames(instrument, spec))
                    await ws.SendAsync(Encoding.UTF8.GetBytes(f), WebSocketMessageType.Text, true, conn.Token).ConfigureAwait(false);
                liveSub.SetChannel("datos", FeedStatus.Connected, null);
                everConnected = true;
                conn.CancelAfter(Stale);
                // algunos exchanges mandan las últimas operaciones al suscribirse: ya pasaron (o se contaron en la conexión anterior)
                var connectedAt = DateTime.UtcNow.AddSeconds(-2);

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(conn.Token);
                var sendLock = new SemaphoreSlim(1, 1);
                if (Keepalive is { } ka)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            while (!pingCts.IsCancellationRequested && ws.State == WebSocketState.Open)
                            {
                                await Task.Delay(ka.Every, pingCts.Token).ConfigureAwait(false);
                                await sendLock.WaitAsync(pingCts.Token).ConfigureAwait(false);
                                try
                                {
                                    await ws.SendAsync(Encoding.UTF8.GetBytes(ka.Frame), WebSocketMessageType.Text, true, pingCts.Token).ConfigureAwait(false);
                                }
                                finally
                                {
                                    sendLock.Release();
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // la conexión se cerró
                        }
                    }, pingCts.Token);
                }

                var buf = new byte[1 << 16];
                var ms = new MemoryStream();
                DateTime lastPublish = DateTime.MinValue;
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buf, conn.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);

                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        reason = "el servidor cerró la conexión";
                        break;
                    }

                    receivedAny = true;
                    failures = 0;
                    conn.CancelAfter(Stale);
                    string text = r.MessageType == WebSocketMessageType.Binary && IsGzip(ms) ? Gunzip(ms) : Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    try
                    {
                        OnMessage(text, session);
                    }
                    catch (JsonException)
                    {
                        continue; // un mensaje que no es JSON (pong, por ejemplo)
                    }
                    catch (InvalidOperationException)
                    {
                        continue; // un mensaje con otra forma (confirmaciones de suscripción)
                    }
                    catch (KeyNotFoundException)
                    {
                        continue;
                    }

                    if (session.Trades.Count > 0)
                    {
                        var list = new List<Trade>(session.Trades.Count);
                        foreach (var t in session.Trades)
                        {
                            if (t.Time < connectedAt) continue;
                            // el tiempo no retrocede (el orden de llegada manda)
                            var time = t.Time < lastTradeTime ? lastTradeTime : t.Time;
                            lastTradeTime = time;
                            list.Add(new Trade(Interlocked.Increment(ref _nextId), time, t.Price, t.Volume, t.Side));
                        }

                        session.Trades.Clear();
                        if (list.Count > 0) sink.OnTrades(instrument, list);
                    }

                    if (session.BookChanged)
                    {
                        var now = DateTime.UtcNow;
                        if (session.Snapshot || (now - lastPublish).TotalMilliseconds >= 100)
                        {
                            sink.OnDepth(instrument, session.Book.BuildUpdate(now, 500, session.Snapshot));
                            lastPublish = now;
                            session.Snapshot = false;
                            session.BookChanged = false;
                        }
                    }
                }

                pingCts.Cancel();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                reason = liveSub.WasRequested(ref requested) ? "reconexión pedida" : receivedAny ? "no llegaban datos" : "no respondió a tiempo";
                if (!receivedAny) failures++;
            }
            catch (VenueResyncException ex)
            {
                reason = "libro fuera de secuencia (" + ex.Message + ")";
            }
            catch (Exception ex)
            {
                failures++;
                reason = ex.Message;
            }
            finally
            {
                liveSub.EndConnection(conn);
            }

            if (ct.IsCancellationRequested) break;
            var wait = LiveSubscription.Backoff(failures);
            liveSub.SetChannel("datos", FeedStatus.Reconnecting, failures == 0
                ? $"Reconectando ({reason})…"
                : $"Reconectando en {wait.TotalSeconds:0} s · intento {failures} ({reason})");
            await Task.Delay(wait, ct).ContinueWith(_ => { }).ConfigureAwait(false);
        }

        liveSub.SetChannel("datos", FeedStatus.Disconnected, null);
    }

    private static bool IsGzip(MemoryStream ms) => ms.Length > 2 && ms.GetBuffer()[0] == 0x1f && ms.GetBuffer()[1] == 0x8b;

    private static string Gunzip(MemoryStream ms)
    {
        ms.Position = 0;
        using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
        using var rd = new StreamReader(gz, Encoding.UTF8);
        return rd.ReadToEnd();
    }

    public void Dispose() => Http.Dispose();
}
