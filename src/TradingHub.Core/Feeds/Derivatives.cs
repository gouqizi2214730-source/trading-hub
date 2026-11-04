using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TradingHub.Feeds;

/// <summary>Un dato de derivados en un instante (financiación en %, interés abierto en contratos…).</summary>
public readonly record struct DerivPoint(DateTime Time, double Value);

/// <summary>Una liquidación: precio, cantidad y si se liquidó un largo (venta forzada) o un corto (compra forzada).</summary>
public readonly record struct Liquidation(DateTime Time, string Exchange, double Price, double Quantity, bool LongLiquidated);

/// <summary>
/// Datos de derivados de los exchanges de cripto, públicos y sin cuenta: tasa de financiación (funding) e interés abierto
/// (open interest) de Binance y Bybit por REST, y liquidaciones en vivo por WebSocket (se graban en disco para verlas
/// después). Como el «Agg Funding Rate / Agg OI / Agg Liquidations» de Kiyotaka y openmarket.
/// </summary>
public static class Derivatives
{
    public const string Binance = "binance";
    public const string Bybit = "bybit";

    /// <summary>Carpeta de los datos guardados ({raíz}/Derivados); null = solo en memoria.</summary>
    public static string? Root { get; set; }

    /// <summary>Servidor de Bybit (se puede cambiar, por ejemplo a api.bytick.com).</summary>
    public static string BybitRest { get; set; } = "https://api.bybit.com";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly ConcurrentDictionary<string, (DateTime From, DateTime To, List<DerivPoint> Points)> Memory = new();

    private static long Ms(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    /// <summary>Pide JSON; un error del exchange (por ejemplo, «región restringida») sale con su mensaje.</summary>
    private static async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false));
        var r = doc.RootElement;
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("msg", out var msg) && !r.TryGetProperty("result", out _))
        {
            doc.Dispose();
            throw new InvalidOperationException(msg.GetString());
        }

        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("retCode", out var rc) && rc.GetInt32() != 0)
        {
            string m = r.TryGetProperty("retMsg", out var rm) ? rm.GetString() ?? "" : "";
            doc.Dispose();
            throw new InvalidOperationException(m);
        }

        return doc;
    }

    private static double D(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.Parse(e.GetString() ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Tasa de financiación (en %) de cada período de cobro entre dos instantes.</summary>
    public static Task<List<DerivPoint>> FundingAsync(string exchange, string symbol, DateTime from, DateTime to, CancellationToken ct) =>
        CachedAsync($"funding|{exchange}|{symbol}", from, to, (f, t) => exchange == Bybit ? BybitFundingAsync(symbol, f, t, ct) : BinanceFundingAsync(symbol, f, t, ct));

    /// <summary>Interés abierto (en monedas del contrato) cada <paramref name="step"/> entre dos instantes.</summary>
    public static Task<List<DerivPoint>> OpenInterestAsync(string exchange, string symbol, TimeSpan step, DateTime from, DateTime to, CancellationToken ct)
    {
        // Binance solo guarda los últimos 30 días del interés abierto histórico
        if (exchange == Binance && from < DateTime.UtcNow.AddDays(-29)) from = DateTime.UtcNow.AddDays(-29);
        return CachedAsync($"oi|{exchange}|{symbol}|{(int)step.TotalMinutes}", from, to, (f, t) => exchange == Bybit ? BybitOiAsync(symbol, step, f, t, ct) : BinanceOiAsync(symbol, step, f, t, ct));
    }

    /// <summary>Lo pedido sale de lo ya traído (memoria y disco) y solo se piden los bordes que falten.</summary>
    private static async Task<List<DerivPoint>> CachedAsync(string key, DateTime from, DateTime to, Func<DateTime, DateTime, Task<List<DerivPoint>>> fetch)
    {
        if (!Memory.TryGetValue(key, out var have)) have = Load(key);
        var points = new SortedDictionary<DateTime, double>();
        foreach (var p in have.Points) points[p.Time] = p.Value;
        DateTime hFrom = have.Points.Count > 0 ? have.From : DateTime.MaxValue, hTo = have.Points.Count > 0 ? have.To : DateTime.MinValue;
        var ranges = new List<(DateTime, DateTime)>();
        if (have.Points.Count == 0) ranges.Add((from, to));
        else
        {
            if (from < hFrom) ranges.Add((from, hFrom));
            // lo último se vuelve a pedir siempre (el período en curso cambia)
            if (to > hTo - TimeSpan.FromHours(1)) ranges.Add((hTo - TimeSpan.FromHours(1) > from ? hTo - TimeSpan.FromHours(1) : from, to));
        }

        foreach (var (f, t) in ranges)
            foreach (var p in await fetch(f, t).ConfigureAwait(false)) points[p.Time] = p.Value;
        var list = points.Select(kv => new DerivPoint(kv.Key, kv.Value)).ToList();
        var entry = (from < hFrom ? from : hFrom, to > hTo ? to : hTo, list);
        Memory[key] = entry;
        Save(key, entry);
        return list.Where(p => p.Time >= from && p.Time <= to).ToList();
    }

    private static string? FileOf(string key) =>
        Root == null ? null : Path.Combine(Root, "Derivados", string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '|' ? '_' : c)) + ".bin");

    private static (DateTime From, DateTime To, List<DerivPoint> Points) Load(string key)
    {
        var f = FileOf(key);
        var empty = (DateTime.MaxValue, DateTime.MinValue, new List<DerivPoint>());
        if (f == null || !File.Exists(f)) return empty;
        try
        {
            using var r = new BinaryReader(File.OpenRead(f));
            var from = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            var to = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            int n = r.ReadInt32();
            var list = new List<DerivPoint>(n);
            for (int i = 0; i < n; i++) list.Add(new DerivPoint(new DateTime(r.ReadInt64(), DateTimeKind.Utc), r.ReadDouble()));
            return (from, to, list);
        }
        catch
        {
            return empty;
        }
    }

    private static void Save(string key, (DateTime From, DateTime To, List<DerivPoint> Points) e)
    {
        var f = FileOf(key);
        if (f == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            using var w = new BinaryWriter(File.Create(f));
            w.Write(e.From.Ticks);
            w.Write(e.To.Ticks);
            w.Write(e.Points.Count);
            foreach (var p in e.Points)
            {
                w.Write(p.Time.Ticks);
                w.Write(p.Value);
            }
        }
        catch
        {
            // la próxima vez se vuelve a pedir
        }
    }

    // ------------------------------------------------------------------ Binance

    private static async Task<List<DerivPoint>> BinanceFundingAsync(string symbol, DateTime from, DateTime to, CancellationToken ct)
    {
        var res = new List<DerivPoint>();
        long start = Ms(from), end = Ms(to);
        for (int guard = 0; guard < 50 && start < end; guard++)
        {
            var url = $"https://fapi.binance.com/fapi/v1/fundingRate?symbol={symbol}&startTime={start}&endTime={end}&limit=1000";
            using var doc = await GetAsync(url, ct).ConfigureAwait(false);
            int n = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                long t = e.GetProperty("fundingTime").GetInt64();
                res.Add(new DerivPoint(FromMs(t), D(e.GetProperty("fundingRate")) * 100));
                start = t + 1;
                n++;
            }

            if (n < 1000) break;
        }

        return res;
    }

    private static string BinancePeriod(TimeSpan step) => step.TotalMinutes switch
    {
        <= 5 => "5m",
        <= 15 => "15m",
        <= 30 => "30m",
        <= 60 => "1h",
        <= 120 => "2h",
        <= 240 => "4h",
        <= 360 => "6h",
        <= 720 => "12h",
        _ => "1d",
    };

    private static async Task<List<DerivPoint>> BinanceOiAsync(string symbol, TimeSpan step, DateTime from, DateTime to, CancellationToken ct)
    {
        var res = new List<DerivPoint>();
        string period = BinancePeriod(step);
        long start = Ms(from), end = Ms(to);
        for (int guard = 0; guard < 200 && start < end; guard++)
        {
            var url = $"https://fapi.binance.com/futures/data/openInterestHist?symbol={symbol}&period={period}&limit=500&startTime={start}&endTime={end}";
            using var doc = await GetAsync(url, ct).ConfigureAwait(false);
            int n = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                long t = e.GetProperty("timestamp").GetInt64();
                res.Add(new DerivPoint(FromMs(t), D(e.GetProperty("sumOpenInterest"))));
                start = t + 1;
                n++;
            }

            if (n < 500) break;
        }

        return res;
    }

    // ------------------------------------------------------------------ Bybit

    private static async Task<List<DerivPoint>> BybitFundingAsync(string symbol, DateTime from, DateTime to, CancellationToken ct)
    {
        // Bybit devuelve de lo más nuevo a lo más viejo, de a 200: se va corriendo el final hacia atrás
        var res = new List<DerivPoint>();
        long start = Ms(from), end = Ms(to);
        for (int guard = 0; guard < 100 && end > start; guard++)
        {
            var url = $"{BybitRest}/v5/market/funding/history?category=linear&symbol={symbol}&startTime={start}&endTime={end}&limit=200";
            using var doc = await GetAsync(url, ct).ConfigureAwait(false);
            var list = doc.RootElement.GetProperty("result").GetProperty("list");
            long oldest = long.MaxValue;
            int n = 0;
            foreach (var e in list.EnumerateArray())
            {
                long t = long.Parse(e.GetProperty("fundingRateTimestamp").GetString()!, CultureInfo.InvariantCulture);
                res.Add(new DerivPoint(FromMs(t), D(e.GetProperty("fundingRate")) * 100));
                oldest = Math.Min(oldest, t);
                n++;
            }

            if (n < 200 || oldest == long.MaxValue) break;
            end = oldest - 1;
        }

        return res;
    }

    private static string BybitInterval(TimeSpan step) => step.TotalMinutes switch
    {
        <= 5 => "5min",
        <= 15 => "15min",
        <= 30 => "30min",
        <= 60 => "1h",
        <= 240 => "4h",
        _ => "1d",
    };

    private static async Task<List<DerivPoint>> BybitOiAsync(string symbol, TimeSpan step, DateTime from, DateTime to, CancellationToken ct)
    {
        var res = new List<DerivPoint>();
        string interval = BybitInterval(step);
        string? cursor = null;
        for (int guard = 0; guard < 300; guard++)
        {
            var url = $"{BybitRest}/v5/market/open-interest?category=linear&symbol={symbol}&intervalTime={interval}&startTime={Ms(from)}&endTime={Ms(to)}&limit=200"
                      + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            using var doc = await GetAsync(url, ct).ConfigureAwait(false);
            var result = doc.RootElement.GetProperty("result");
            int n = 0;
            foreach (var e in result.GetProperty("list").EnumerateArray())
            {
                long t = long.Parse(e.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture);
                res.Add(new DerivPoint(FromMs(t), D(e.GetProperty("openInterest"))));
                n++;
            }

            cursor = result.TryGetProperty("nextPageCursor", out var c) ? c.GetString() : null;
            if (n == 0 || string.IsNullOrEmpty(cursor)) break;
        }

        return res;
    }

    // ------------------------------------------------------------------ liquidaciones en vivo

    private sealed class LiqStream
    {
        public readonly List<Liquidation> Events = new();
        public CancellationTokenSource Cts = new();
        public int Users;
        public long Generation;
    }

    private static readonly Dictionary<string, LiqStream> Streams = new();

    /// <summary>Cambia cada vez que llega una liquidación (para volver a dibujar).</summary>
    public static long LiquidationGeneration(string symbol)
    {
        lock (Streams) return Streams.TryGetValue(symbol, out var s) ? s.Generation : 0;
    }

    /// <summary>
    /// Empieza a escuchar las liquidaciones de un símbolo en Binance y Bybit (una sola conexión por símbolo, compartida).
    /// Lo que ya estaba grabado de los últimos días se carga primero. Devuelve el objeto que la cierra.
    /// </summary>
    public static IDisposable WatchLiquidations(string symbol)
    {
        LiqStream s;
        lock (Streams)
        {
            if (!Streams.TryGetValue(symbol, out s!))
            {
                s = new LiqStream();
                Streams[symbol] = s;
                s.Events.AddRange(LoadLiquidations(symbol, 7));
                var ct = s.Cts.Token;
                _ = Task.Run(() => RunAsync(symbol, Binance, s, ct));
                _ = Task.Run(() => RunAsync(symbol, Bybit, s, ct));
            }

            s.Users++;
        }

        return new Releaser(() =>
        {
            lock (Streams)
            {
                if (--s.Users > 0) return;
                s.Cts.Cancel();
                Streams.Remove(symbol);
            }
        });
    }

    private sealed class Releaser : IDisposable
    {
        private Action? _a;

        public Releaser(Action a) => _a = a;

        public void Dispose() => Interlocked.Exchange(ref _a, null)?.Invoke();
    }

    /// <summary>Liquidaciones conocidas de un símbolo entre dos instantes (grabadas y en vivo).</summary>
    public static List<Liquidation> Liquidations(string symbol, DateTime from, DateTime to)
    {
        lock (Streams)
        {
            if (!Streams.TryGetValue(symbol, out var s)) return LoadLiquidations(symbol, Math.Max(1, (int)Math.Ceiling((DateTime.UtcNow - from).TotalDays))).Where(l => l.Time >= from && l.Time < to).ToList();
            lock (s.Events) return s.Events.Where(l => l.Time >= from && l.Time < to).ToList();
        }
    }

    private static void Add(string symbol, LiqStream s, Liquidation l)
    {
        lock (s.Events) s.Events.Add(l);
        Interlocked.Increment(ref s.Generation);
        if (Root == null) return;
        try
        {
            var dir = Path.Combine(Root, "Derivados", "Liquidaciones", symbol);
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, l.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv"),
                string.Join(",", l.Time.Ticks, l.Exchange, l.Price.ToString(CultureInfo.InvariantCulture), l.Quantity.ToString(CultureInfo.InvariantCulture), l.LongLiquidated ? "L" : "S") + "\n");
        }
        catch
        {
            // seguir en memoria
        }
    }

    private static List<Liquidation> LoadLiquidations(string symbol, int days)
    {
        var res = new List<Liquidation>();
        if (Root == null) return res;
        var dir = Path.Combine(Root, "Derivados", "Liquidaciones", symbol);
        for (int d = days; d >= 0; d--)
        {
            var f = Path.Combine(dir, DateTime.UtcNow.AddDays(-d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
            if (!File.Exists(f)) continue;
            try
            {
                foreach (var line in File.ReadLines(f))
                {
                    var p = line.Split(',');
                    if (p.Length < 5) continue;
                    res.Add(new Liquidation(new DateTime(long.Parse(p[0], CultureInfo.InvariantCulture), DateTimeKind.Utc), p[1],
                        double.Parse(p[2], CultureInfo.InvariantCulture), double.Parse(p[3], CultureInfo.InvariantCulture), p[4] == "L"));
                }
            }
            catch
            {
                // archivo a medio escribir
            }
        }

        return res;
    }

    private static async Task RunAsync(string symbol, string exchange, LiqStream s, CancellationToken ct)
    {
        for (int attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                if (exchange == Binance)
                    await ws.ConnectAsync(new Uri($"wss://fstream.binance.com/ws/{symbol.ToLowerInvariant()}@forceOrder"), ct).ConfigureAwait(false);
                else
                {
                    await ws.ConnectAsync(new Uri("wss://stream.bybit.com/v5/public/linear"), ct).ConfigureAwait(false);
                    var sub = Encoding.UTF8.GetBytes($"{{\"op\":\"subscribe\",\"args\":[\"allLiquidation.{symbol}\"]}}");
                    await ws.SendAsync(sub, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }

                attempt = 0;
                var buf = new byte[1 << 16];
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (exchange == Bybit)
                    _ = Task.Run(async () =>
                    {
                        // Bybit corta la conexión si no recibe un ping cada 20 s
                        try
                        {
                            while (!pingCts.IsCancellationRequested && ws.State == WebSocketState.Open)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(18), pingCts.Token).ConfigureAwait(false);
                                await ws.SendAsync(Encoding.UTF8.GetBytes("{\"op\":\"ping\"}"), WebSocketMessageType.Text, true, pingCts.Token).ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            // se cerró
                        }
                    });

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buf, 0, r.Count);
                    }
                    while (!r.EndOfMessage);

                    if (r.MessageType == WebSocketMessageType.Close) break;
                    foreach (var l in Parse(exchange, ms.ToArray())) Add(symbol, s, l);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // sin red o cortado: se reintenta
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 3 << Math.Min(attempt, 4))), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Lee un mensaje de liquidaciones de Binance (forceOrder) o Bybit (allLiquidation).</summary>
    public static IEnumerable<Liquidation> Parse(string exchange, byte[] json)
    {
        var res = new List<Liquidation>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (exchange == Binance && root.TryGetProperty("o", out var o))
            {
                // una orden de venta forzada = se liquidó un largo
                res.Add(new Liquidation(FromMs(o.GetProperty("T").GetInt64()), Binance, D(o.GetProperty("ap")) > 0 ? D(o.GetProperty("ap")) : D(o.GetProperty("p")),
                    D(o.GetProperty("q")), o.GetProperty("S").GetString() == "SELL"));
            }
            else if (exchange == Bybit && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                // en Bybit «Buy» es la posición: un largo liquidado
                foreach (var e in data.EnumerateArray())
                    res.Add(new Liquidation(FromMs(e.GetProperty("T").GetInt64()), Bybit, D(e.GetProperty("p")), D(e.GetProperty("v")), e.GetProperty("S").GetString() == "Buy"));
            }
        }
        catch
        {
            // mensaje de control (pong, suscripción) o con otro formato
        }

        return res;
    }
}
