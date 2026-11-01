using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Feeds.Bybit;

/// <summary>Dónde opera una cuenta de Bybit.</summary>
public enum BybitEnvironment
{
    /// <summary>Demo Trading de Bybit: el mercado real con dinero de prueba (api-demo.bybit.com).</summary>
    Demo,

    /// <summary>Testnet (testnet.bybit.com): otro mercado, con precios propios y dinero de prueba.</summary>
    Testnet,

    /// <summary>La cuenta real.</summary>
    Real,
}

/// <summary>Configuración de una cuenta de Bybit (el secret se guarda en el llavero, no en archivos).</summary>
public sealed class BybitAccountSettings
{
    public BybitEnvironment Environment { get; set; } = BybitEnvironment.Demo;
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";

    /// <summary>Servidor REST propio (vacío = el estándar del entorno; por ejemplo https://api.bytick.com para la real).</summary>
    public string? BaseUrl { get; set; }

    public bool IsReal => Environment == BybitEnvironment.Real;

    public string RestBase => !string.IsNullOrWhiteSpace(BaseUrl)
        ? BaseUrl!.Trim().TrimEnd('/')
        : Environment switch
        {
            BybitEnvironment.Demo => "https://api-demo.bybit.com",
            BybitEnvironment.Testnet => "https://api-testnet.bybit.com",
            _ => "https://api.bybit.com",
        };

    public bool HasKeys => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    public string EnvironmentName => Environment switch
    {
        BybitEnvironment.Demo => "demo",
        BybitEnvironment.Testnet => "testnet",
        _ => "REAL",
    };
}

public sealed record BybitWallet(decimal Equity, decimal Available, decimal UnrealizedPnl, decimal WalletBalance);

public sealed record BybitPosition(string Symbol, decimal Size, decimal AvgPrice, decimal UnrealizedPnl, decimal Leverage);

public sealed record BybitOpenOrder(string OrderId, string LinkId, string Symbol, bool Buy, bool Conditional, bool IsLimit, decimal Price, decimal TriggerPrice,
    decimal Quantity, decimal Executed, decimal Leaves, string Status, bool ReduceOnly);

/// <summary>Una respuesta de Bybit con retCode distinto de 0.</summary>
public sealed class BybitApiException : Exception
{
    public BybitApiException(int code, string message) : base(message) => Code = code;

    public int Code { get; }
}

/// <summary>
/// API privada v5 de Bybit para los perpetuos USDT (category=linear) de una cuenta unificada (UTA): saldo, posición y
/// órdenes. Las llamadas se firman con HMAC-SHA256 de «timestamp + api key + recvWindow + (query o cuerpo JSON)».
/// </summary>
public sealed class BybitAccountClient : IDisposable
{
    public const string Category = "linear";
    private const int RecvWindow = 10000;
    private readonly BybitAccountSettings _s;
    private readonly HttpClient _http;
    private long _timeOffsetMs;
    private bool _timeSynced;

    public BybitAccountClient(BybitAccountSettings settings, HttpMessageHandler? handler = null)
    {
        _s = settings;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TradingHub/1.2");
    }

    public BybitAccountSettings Settings => _s;

    /// <summary>Firma de Bybit v5 (HMAC-SHA256 en hexadecimal).</summary>
    public static string Sign(long timestamp, string apiKey, int recvWindow, string payload, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var data = Encoding.UTF8.GetBytes($"{timestamp}{apiKey}{recvWindow}{payload}");
        return Convert.ToHexString(h.ComputeHash(data)).ToLowerInvariant();
    }

    /// <summary>Ajusta la hora con la del servidor (Bybit rechaza pedidos con la hora corrida).</summary>
    public async Task SyncTimeAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(_s.RestBase + "/v5/market/time", ct).ConfigureAwait(false));
        long server = doc.RootElement.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt64()
            : long.Parse(doc.RootElement.GetProperty("result").GetProperty("timeSecond").GetString()!, CultureInfo.InvariantCulture) * 1000;
        _timeOffsetMs = server - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _timeSynced = true;
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string query, JsonObject? body, CancellationToken ct, bool retried = false)
    {
        if (!_s.HasKeys) throw new InvalidOperationException("Faltan la API key y el secret de Bybit (Conexiones).");
        if (!_timeSynced)
        {
            try
            {
                await SyncTimeAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // sin la hora del servidor se usa la local
            }
        }

        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _timeOffsetMs;
        string json = body?.ToJsonString() ?? "";
        string payload = method == HttpMethod.Get ? query : json;
        string url = _s.RestBase + path + (method == HttpMethod.Get && query.Length > 0 ? "?" + query : "");
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-BAPI-API-KEY", _s.ApiKey);
        req.Headers.Add("X-BAPI-TIMESTAMP", ts.ToString(CultureInfo.InvariantCulture));
        req.Headers.Add("X-BAPI-RECV-WINDOW", RecvWindow.ToString(CultureInfo.InvariantCulture));
        req.Headers.Add("X-BAPI-SIGN", Sign(ts, _s.ApiKey, RecvWindow, payload, _s.ApiSecret));
        if (method != HttpMethod.Get) req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Bybit respondió {(int)resp.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            int code = root.TryGetProperty("retCode", out var c) ? c.GetInt32() : (resp.IsSuccessStatusCode ? 0 : (int)resp.StatusCode);
            if (code != 0)
            {
                string msg = root.TryGetProperty("retMsg", out var m) ? m.GetString() ?? "" : text;
                // la hora corrida: se vuelve a sincronizar y se reintenta una vez
                if (code == 10002 && !retried)
                {
                    _timeSynced = false;
                    return await SendAsync(method, path, query, body, ct, retried: true).ConfigureAwait(false);
                }

                throw new BybitApiException(code, Explain(code, msg));
            }

            return root.TryGetProperty("result", out var r) ? r.Clone() : default;
        }
    }

    /// <summary>Los errores más comunes, con qué hacer.</summary>
    public static string Explain(int code, string msg)
    {
        msg = msg.Trim().TrimEnd('.');
        // cada caso es una frase entera (así se traduce entera en la interfaz)
        return code switch
        {
            10003 or 10004 or 33004 => $"Bybit rechazó la operación ({code}): {msg}. Revisá la API key y el secret (y que sean del mismo entorno: demo, testnet o real).",
            10005 => $"Bybit rechazó la operación ({code}): {msg}. La API key no tiene permiso de trading de contratos.",
            10010 => $"Bybit rechazó la operación ({code}): {msg}. Tu IP no está en la lista permitida de la API key.",
            110007 => $"Bybit rechazó la operación ({code}): {msg}. Saldo insuficiente.",
            _ when msg.Contains("position idx", StringComparison.OrdinalIgnoreCase) =>
                $"Bybit rechazó la operación ({code}): {msg}. Trading Hub opera en modo unidireccional (one-way): cambialo en Bybit para este contrato.",
            _ => $"Bybit rechazó la operación ({code}): {msg}.",
        };
    }

    private Task<JsonElement> GetAsync(string path, string query, CancellationToken ct) => SendAsync(HttpMethod.Get, path, query, null, ct);

    private Task<JsonElement> PostAsync(string path, JsonObject body, CancellationToken ct) => SendAsync(HttpMethod.Post, path, "", body, ct);

    internal static decimal D(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0m;
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
        return string.IsNullOrEmpty(s) ? 0m : decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static string Fmt(decimal v) => v.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>Saldo de la cuenta unificada (en USD).</summary>
    public async Task<BybitWallet?> GetWalletAsync(CancellationToken ct)
    {
        var r = await GetAsync("/v5/account/wallet-balance", "accountType=UNIFIED", ct).ConfigureAwait(false);
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("list", out var list) || list.GetArrayLength() == 0) return null;
        var a = list[0];
        return new BybitWallet(D(a, "totalEquity"), D(a, "totalAvailableBalance"), D(a, "totalPerpUPL"), D(a, "totalWalletBalance"));
    }

    /// <summary>Posición neta del contrato (positiva comprado, negativa vendido).</summary>
    public async Task<BybitPosition?> GetPositionAsync(string symbol, CancellationToken ct)
    {
        var r = await GetAsync("/v5/position/list", $"category={Category}&symbol={symbol}", ct).ConfigureAwait(false);
        if (r.ValueKind != JsonValueKind.Object) return null;
        decimal size = 0, avg = 0, upl = 0, lev = 0;
        foreach (var p in r.GetProperty("list").EnumerateArray())
        {
            decimal q = D(p, "size");
            if (q == 0) continue;
            q = S(p, "side") == "Sell" ? -q : q;
            avg = size + q == 0 ? 0 : (avg * Math.Abs(size) + D(p, "avgPrice") * Math.Abs(q)) / Math.Abs(size + q);
            size += q;
            upl += D(p, "unrealisedPnl");
            lev = D(p, "leverage");
        }

        return new BybitPosition(symbol, size, avg, upl, lev);
    }

    /// <summary>Órdenes abiertas del contrato (límite y condicionales).</summary>
    public async Task<IReadOnlyList<BybitOpenOrder>> GetOpenOrdersAsync(string symbol, CancellationToken ct)
    {
        var r = await GetAsync("/v5/order/realtime", $"category={Category}&symbol={symbol}&limit=50", ct).ConfigureAwait(false);
        var list = new List<BybitOpenOrder>();
        if (r.ValueKind != JsonValueKind.Object) return list;
        foreach (var o in r.GetProperty("list").EnumerateArray())
        {
            decimal trigger = D(o, "triggerPrice");
            string status = S(o, "orderStatus");
            list.Add(new BybitOpenOrder(S(o, "orderId"), S(o, "orderLinkId"), symbol, S(o, "side") == "Buy",
                trigger > 0 || status == "Untriggered", S(o, "orderType") == "Limit", D(o, "price"), trigger,
                D(o, "qty"), D(o, "cumExecQty"), D(o, "leavesQty"), status,
                o.TryGetProperty("reduceOnly", out var ro) && ro.ValueKind == JsonValueKind.True));
        }

        return list;
    }

    /// <summary>Estado final de una orden que ya no está abierta (Filled, Cancelled, Rejected…), o null si no aparece.</summary>
    public async Task<(string Status, decimal Executed, decimal AvgPrice)?> GetOrderResultAsync(string symbol, string orderId, CancellationToken ct)
    {
        var r = await GetAsync("/v5/order/history", $"category={Category}&symbol={symbol}&orderId={orderId}", ct).ConfigureAwait(false);
        if (r.ValueKind != JsonValueKind.Object) return null;
        foreach (var o in r.GetProperty("list").EnumerateArray())
            return (S(o, "orderStatus"), D(o, "cumExecQty"), D(o, "avgPrice"));
        return null;
    }

    /// <summary>
    /// Envía una orden (modo unidireccional). Los stops son órdenes condicionales a mercado: se disparan cuando el último
    /// precio cruza <paramref name="price"/> hacia el lado del stop.
    /// </summary>
    public async Task<string> PlaceOrderAsync(string symbol, OrderDirections dir, OrderTypes type, decimal qty, decimal price, bool reduceOnly, string linkId,
        decimal lastPrice, CancellationToken ct)
    {
        bool buy = dir == OrderDirections.Buy;
        var body = new JsonObject
        {
            ["category"] = Category,
            ["symbol"] = symbol,
            ["side"] = buy ? "Buy" : "Sell",
            ["qty"] = Fmt(qty),
            ["positionIdx"] = 0,
        };
        switch (type)
        {
            case OrderTypes.Market:
                body["orderType"] = "Market";
                break;
            case OrderTypes.Limit:
                body["orderType"] = "Limit";
                body["price"] = Fmt(price);
                body["timeInForce"] = "GTC";
                break;
            case OrderTypes.Stop:
                body["orderType"] = "Market";
                body["triggerPrice"] = Fmt(price);
                body["triggerBy"] = "LastPrice";
                // 1 = se dispara al subir hasta el precio, 2 = al bajar
                body["triggerDirection"] = lastPrice > 0 ? (price > lastPrice ? 1 : 2) : (buy ? 1 : 2);
                break;
            default:
                throw new NotSupportedException("Tipo de orden no soportado");
        }

        if (reduceOnly) body["reduceOnly"] = true;
        if (!string.IsNullOrEmpty(linkId)) body["orderLinkId"] = linkId;
        var r = await PostAsync("/v5/order/create", body, ct).ConfigureAwait(false);
        return S(r, "orderId");
    }

    /// <summary>Cambia precio (o disparo) y/o cantidad de una orden abierta, sin cancelarla.</summary>
    public async Task AmendOrderAsync(string symbol, string orderId, decimal? qty, decimal? price, decimal? triggerPrice, CancellationToken ct)
    {
        var body = new JsonObject { ["category"] = Category, ["symbol"] = symbol, ["orderId"] = orderId };
        if (qty is { } q) body["qty"] = Fmt(q);
        if (price is { } p) body["price"] = Fmt(p);
        if (triggerPrice is { } t) body["triggerPrice"] = Fmt(t);
        await PostAsync("/v5/order/amend", body, ct).ConfigureAwait(false);
    }

    public async Task CancelOrderAsync(string symbol, string orderId, CancellationToken ct) =>
        await PostAsync("/v5/order/cancel", new JsonObject { ["category"] = Category, ["symbol"] = symbol, ["orderId"] = orderId }, ct).ConfigureAwait(false);

    public async Task CancelAllAsync(string symbol, CancellationToken ct) =>
        await PostAsync("/v5/order/cancel-all", new JsonObject { ["category"] = Category, ["symbol"] = symbol }, ct).ConfigureAwait(false);

    /// <summary>Prueba la conexión: la hora del servidor y, si hay claves, el saldo.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        try
        {
            await SyncTimeAsync(ct).ConfigureAwait(false);
            if (!_s.HasKeys) return (true, $"Servidor OK ({_s.RestBase}). Sin claves: solo datos.");
            var w = await GetWalletAsync(ct).ConfigureAwait(false);
            return (true, w == null
                ? $"Cuenta conectada ({_s.EnvironmentName}), sin saldo en la cuenta unificada"
                : $"Cuenta conectada ({_s.EnvironmentName}): {w.Equity:0.##} USD (disponible {w.Available:0.##})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex is HttpRequestException ? $"No se pudo conectar con {_s.RestBase}: {ex.Message}" : ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Cuenta de Bybit como <see cref="ITradingAccount"/>: envía las órdenes por REST (las modifica sin cancelarlas) y
/// refresca posición y órdenes cada 2 segundos, y enseguida después de cada orden. Las órdenes de Trading Hub
/// conservan su objeto (se reconocen por su orderLinkId «th-…»); las abiertas desde otro lado también aparecen.
/// </summary>
public sealed class BybitBroker : ITradingAccount, IDisposable
{
    private readonly BybitAccountClient _client;
    private readonly InstrumentData _data;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _kick = new(0, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Order> _byLink = new();
    private readonly Dictionary<string, Order> _byId = new();
    private readonly HashSet<Order> _pendingSend = new(ReferenceEqualityComparer.Instance);
    private List<Order> _active = new();

    public BybitBroker(InstrumentData data, BybitAccountSettings settings, string name, HttpMessageHandler? handler = null, TimeSpan? pollInterval = null)
    {
        _data = data;
        _client = new BybitAccountClient(settings, handler);
        Name = name;
        PollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        Position = new Position { Security = new Security { Code = data.Instrument.Symbol, SecurityId = data.Instrument.Key } };
        _ = Task.Run(PollLoop);
    }

    public string Name { get; }
    public bool IsReal => _client.Settings.IsReal;
    public decimal LastPrice => _data.LastPrice;
    public Instrument Instrument => _data.Instrument;
    public Position Position { get; }
    public IEnumerable<Order> ActiveOrders => _active;
    public string? LastError { get; private set; }
    public event Action? Changed;

    /// <summary>Saldo de la cuenta (lo trae cada refresco).</summary>
    public BybitWallet? Wallet { get; private set; }

    public TimeSpan PollInterval { get; }

    private void Kick()
    {
        try
        {
            _kick.Release();
        }
        catch (SemaphoreFullException)
        {
            // ya había un refresco pedido
        }
    }

    private async Task PollLoop()
    {
        int round = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(wallet: round++ % 5 == 0).ConfigureAwait(false);
                LastError = null;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            Changed?.Invoke();
            try
            {
                await _kick.WaitAsync(PollInterval, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshAsync(bool wallet)
    {
        var ct = _cts.Token;
        string sym = Instrument.Symbol;
        var open = await _client.GetOpenOrdersAsync(sym, ct).ConfigureAwait(false);
        var pos = await _client.GetPositionAsync(sym, ct).ConfigureAwait(false);
        if (wallet) Wallet = await _client.GetWalletAsync(ct).ConfigureAwait(false);

        var gone = new List<Order>();
        lock (_gate)
        {
            var seen = new HashSet<Order>(ReferenceEqualityComparer.Instance);
            foreach (var x in open)
            {
                Order? o = null;
                if (x.LinkId.Length > 0) _byLink.TryGetValue(x.LinkId, out o);
                if (o == null) _byId.TryGetValue(x.OrderId, out o);
                if (o == null)
                {
                    o = new Order { Comment = "Bybit", Time = DateTime.UtcNow };
                    if (x.LinkId.Length > 0) _byLink[x.LinkId] = o;
                }

                _byId[x.OrderId] = o;
                o.ExchangeOrderId = x.OrderId;
                o.Direction = x.Buy ? OrderDirections.Buy : OrderDirections.Sell;
                o.Type = x.Conditional ? OrderTypes.Stop : x.IsLimit ? OrderTypes.Limit : OrderTypes.Market;
                o.Price = x.Price;
                o.TriggerPrice = x.Conditional ? x.TriggerPrice : x.Price;
                o.QuantityToFill = x.Quantity;
                o.Unfilled = x.Leaves > 0 ? x.Leaves : x.Quantity - x.Executed;
                o.ReduceOnly = x.ReduceOnly;
                o.State = OrderStates.Active;
                o.Status = x.Executed > 0 ? OrderStatus.PartlyFilled : OrderStatus.Placed;
                _pendingSend.Remove(o);
                seen.Add(o);
            }

            // las que estaban abiertas y ya no: se ejecutaron o se cancelaron (se pregunta cuál)
            foreach (var o in _active)
                if (!seen.Contains(o) && !_pendingSend.Contains(o) && o.ExchangeOrderId.Length > 0)
                    gone.Add(o);

            _active = seen.Concat(_pendingSend.Where(o => o.State == OrderStates.Active)).ToList();
        }

        foreach (var o in gone)
        {
            string status = "";
            try
            {
                var r = await _client.GetOrderResultAsync(sym, o.ExchangeOrderId, ct).ConfigureAwait(false);
                if (r is { } res)
                {
                    status = res.Status;
                    o.Unfilled = Math.Max(0, o.QuantityToFill - res.Executed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // sin el detalle: se da por terminada igual
            }

            Finish(o, status);
        }

        Position.Volume = pos?.Size ?? 0;
        Position.AveragePrice = pos?.AvgPrice ?? 0;
        Position.UnrealizedPnL = pos?.UnrealizedPnl ?? 0;
    }

    private static void Finish(Order o, string bybitStatus)
    {
        o.State = bybitStatus is "Rejected" ? OrderStates.Failed : OrderStates.Done;
        o.Status = bybitStatus switch
        {
            "Filled" => OrderStatus.Filled,
            "Rejected" => OrderStatus.Rejected,
            "Cancelled" or "Deactivated" or "PartiallyFilledCanceled" => OrderStatus.Canceled,
            _ => o.Unfilled == 0 ? OrderStatus.Filled : OrderStatus.Canceled,
        };
        if (o.Status == OrderStatus.Filled) o.Unfilled = 0;
    }

    private Order Send(OrderDirections dir, OrderTypes type, decimal qty, decimal price, string comment, bool reduceOnly = false)
    {
        decimal px = type == OrderTypes.Market ? 0 : Instrument.RoundPrice(price);
        var order = new Order
        {
            Direction = dir, Type = type, QuantityToFill = qty, Unfilled = qty, Price = px, TriggerPrice = px, Comment = comment,
            State = OrderStates.Active, Status = OrderStatus.Placed, ReduceOnly = reduceOnly,
        };
        string link = $"th-{order.Id}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 100_000_000}";
        lock (_gate)
        {
            _byLink[link] = order;
            _pendingSend.Add(order);
            if (type != OrderTypes.Market) _active = _active.Append(order).ToList();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string id = await _client.PlaceOrderAsync(Instrument.Symbol, dir, type, qty, px, reduceOnly, link, LastPrice, _cts.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    order.ExchangeOrderId = id;
                    if (id.Length > 0) _byId[id] = order;
                }

                LastError = null;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _pendingSend.Remove(order);
                    _active = _active.Where(o => !ReferenceEquals(o, order)).ToList();
                }

                order.State = OrderStates.Failed;
                order.Status = OrderStatus.Rejected;
                LastError = ex.Message;
            }

            lock (_gate) _pendingSend.Remove(order);
            Changed?.Invoke();
            Kick();
        });
        return order;
    }

    public Order Market(OrderDirections dir, decimal qty, string comment = "") => Send(dir, OrderTypes.Market, qty, 0, comment);
    public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") => Send(dir, OrderTypes.Limit, qty, price, comment);
    public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") => Send(dir, OrderTypes.Stop, qty, trigger, comment);

    /// <summary>Stop loss / objetivo: solo reduce la posición.</summary>
    public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") =>
        Send(dir, type == OrderTypes.Stop ? OrderTypes.Stop : OrderTypes.Limit, qty, price, comment, reduceOnly: true);

    /// <summary>Bybit modifica precio y cantidad sobre la misma orden.</summary>
    public Order Replace(Order order, decimal newPrice, decimal newQuantity)
    {
        decimal px = Instrument.RoundPrice(newPrice);
        bool stop = order.Type == OrderTypes.Stop;
        decimal oldPrice = order.Price, oldTrigger = order.TriggerPrice, oldQty = order.QuantityToFill, oldUnfilled = order.Unfilled;
        decimal total = order.Filled + newQuantity;
        if (stop) order.TriggerPrice = px;
        else order.Price = order.TriggerPrice = px;
        order.QuantityToFill = total;
        order.Unfilled = newQuantity;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 30 && order.ExchangeOrderId.Length == 0 && order.State == OrderStates.Active; i++) await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                if (order.ExchangeOrderId.Length == 0) throw new InvalidOperationException("La orden todavía no figura en Bybit");
                await _client.AmendOrderAsync(Instrument.Symbol, order.ExchangeOrderId, total != oldQty ? total : null, stop ? null : px, stop ? px : null, _cts.Token).ConfigureAwait(false);
                LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastError = ex.Message;
                order.Price = oldPrice;
                order.TriggerPrice = oldTrigger;
                order.QuantityToFill = oldQty;
                order.Unfilled = oldUnfilled;
            }

            Changed?.Invoke();
            Kick();
        });
        return order;
    }

    public void Modify(Order order, decimal newPrice) => Replace(order, newPrice, order.Unfilled);

    public void Cancel(Order order)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 30 && order.ExchangeOrderId.Length == 0 && order.State == OrderStates.Active; i++) await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                if (order.ExchangeOrderId.Length == 0) return;
                await _client.CancelOrderAsync(Instrument.Symbol, order.ExchangeOrderId, _cts.Token).ConfigureAwait(false);
                LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastError = ex.Message;
            }

            Changed?.Invoke();
            Kick();
        });
    }

    public void CancelAt(decimal price)
    {
        foreach (var o in _active.Where(o => (o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price) == price).ToList()) Cancel(o);
    }

    public void CancelAll() => _ = Task.Run(async () =>
    {
        try
        {
            await _client.CancelAllAsync(Instrument.Symbol, _cts.Token).ConfigureAwait(false);
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
        }

        Changed?.Invoke();
        Kick();
    });

    public void Flatten()
    {
        CancelAll();
        var q = Position.Volume;
        if (q > 0) Send(OrderDirections.Sell, OrderTypes.Market, q, 0, "Cerrar", reduceOnly: true);
        else if (q < 0) Send(OrderDirections.Buy, OrderTypes.Market, -q, 0, "Cerrar", reduceOnly: true);
    }

    public void Reverse()
    {
        var q = Position.Volume;
        if (q > 0) Market(OrderDirections.Sell, q * 2, "Revertir");
        else if (q < 0) Market(OrderDirections.Buy, -q * 2, "Revertir");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client.Dispose();
    }
}
