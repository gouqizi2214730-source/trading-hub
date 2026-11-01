using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>Bybit v5 de mentira: revisa la firma de cada pedido y lleva órdenes y posición de un perpetuo.</summary>
internal sealed class FakeBybit : IDisposable
{
    public const string Key = "KEY123", Secret = "SECRET456";
    private readonly FakeHttpServer _http = new();
    private readonly object _gate = new();
    private int _next = 1;

    public sealed class Ord
    {
        public string Id = "", Link = "", Side = "", Type = "", Status = "New";
        public decimal Qty, Price, Trigger, Exec;
        public bool Reduce;
        public int TriggerDirection;
    }

    public List<Ord> Orders { get; } = new();
    public List<JsonObject> Created { get; } = new();
    public List<JsonObject> Amended { get; } = new();
    public decimal Position, AvgPrice;
    public decimal MarketPrice = 50000m;
    public int BadSignatures;

    public FakeBybit()
    {
        _http.OnHttp = Handle;
    }

    public string Url => _http.Http;

    public BybitAccountSettings Settings(BybitEnvironment env = BybitEnvironment.Demo) => new() { Environment = env, ApiKey = Key, ApiSecret = Secret, BaseUrl = Url };

    private static string Ok(object result) => JsonSerializer.Serialize(new { retCode = 0, retMsg = "OK", result, time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

    private static string Err(int code, string msg) => JsonSerializer.Serialize(new { retCode = code, retMsg = msg, result = new { } });

    private static string F(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);

    private (int, string) Handle(FakeRequest r)
    {
        string path = r.Path.Split('?')[0];
        string query = r.Path.Contains('?') ? r.Path[(r.Path.IndexOf('?') + 1)..] : "";
        if (path == "/v5/market/time") return (200, Ok(new { timeSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) }));

        // firma: HMAC(secret, timestamp + key + recvWindow + query|cuerpo)
        string ts = r.Headers.GetValueOrDefault("X-BAPI-TIMESTAMP", ""), recv = r.Headers.GetValueOrDefault("X-BAPI-RECV-WINDOW", "");
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        string expected = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(ts + Key + recv + (r.Method == "GET" ? query : r.Body)))).ToLowerInvariant();
        if (r.Headers.GetValueOrDefault("X-BAPI-API-KEY") != Key || r.Headers.GetValueOrDefault("X-BAPI-SIGN") != expected)
        {
            Interlocked.Increment(ref BadSignatures);
            return (200, Err(10004, "error sign!"));
        }

        lock (_gate)
        {
            switch (path)
            {
                case "/v5/account/wallet-balance":
                    return (200, Ok(new { list = new[] { new { totalEquity = "10500.5", totalAvailableBalance = "9800", totalPerpUPL = "12.5", totalWalletBalance = "10488" } } }));
                case "/v5/position/list":
                    return (200, Ok(new
                    {
                        list = new[]
                        {
                            new { symbol = "BTCUSDT", side = Position > 0 ? "Buy" : Position < 0 ? "Sell" : "", size = F(Math.Abs(Position)), avgPrice = F(AvgPrice), unrealisedPnl = "0", leverage = "10" },
                        },
                    }));
                case "/v5/order/realtime":
                    return (200, Ok(new
                    {
                        list = Orders.Where(o => o.Status is "New" or "PartiallyFilled" or "Untriggered").Select(o => new
                        {
                            orderId = o.Id, orderLinkId = o.Link, side = o.Side, orderType = o.Type, price = F(o.Price), triggerPrice = F(o.Trigger), qty = F(o.Qty),
                            cumExecQty = F(o.Exec), leavesQty = F(o.Qty - o.Exec), orderStatus = o.Status, reduceOnly = o.Reduce,
                        }).ToArray(),
                    }));
                case "/v5/order/history":
                {
                    string id = query.Split('&').First(p => p.StartsWith("orderId=")).Split('=')[1];
                    var o = Orders.First(x => x.Id == id);
                    return (200, Ok(new { list = new[] { new { orderId = o.Id, orderStatus = o.Status, cumExecQty = F(o.Exec), avgPrice = F(o.Price) } } }));
                }

                case "/v5/order/create":
                {
                    var b = JsonNode.Parse(r.Body)!.AsObject();
                    Created.Add(b);
                    if ((int?)b["positionIdx"] != 0) return (200, Err(10001, "position idx not match position mode"));
                    var o = new Ord
                    {
                        Id = "oid-" + _next++, Link = (string?)b["orderLinkId"] ?? "", Side = (string)b["side"]!, Type = (string)b["orderType"]!,
                        Qty = decimal.Parse((string)b["qty"]!, CultureInfo.InvariantCulture),
                        Price = b["price"] is { } p ? decimal.Parse((string)p!, CultureInfo.InvariantCulture) : 0,
                        Trigger = b["triggerPrice"] is { } t ? decimal.Parse((string)t!, CultureInfo.InvariantCulture) : 0,
                        Reduce = b["reduceOnly"] is { } ro && (bool)ro!,
                        TriggerDirection = b["triggerDirection"] is { } td ? (int)td! : 0,
                    };
                    if (o.Trigger > 0) o.Status = "Untriggered";
                    else if (o.Type == "Market") Fill(o, MarketPrice);
                    Orders.Add(o);
                    return (200, Ok(new { orderId = o.Id, orderLinkId = o.Link }));
                }

                case "/v5/order/amend":
                {
                    var b = JsonNode.Parse(r.Body)!.AsObject();
                    Amended.Add(b);
                    var o = Orders.First(x => x.Id == (string)b["orderId"]!);
                    if (b["price"] is { } p) o.Price = decimal.Parse((string)p!, CultureInfo.InvariantCulture);
                    if (b["triggerPrice"] is { } t) o.Trigger = decimal.Parse((string)t!, CultureInfo.InvariantCulture);
                    if (b["qty"] is { } q) o.Qty = decimal.Parse((string)q!, CultureInfo.InvariantCulture);
                    return (200, Ok(new { orderId = o.Id }));
                }

                case "/v5/order/cancel":
                {
                    var b = JsonNode.Parse(r.Body)!.AsObject();
                    var o = Orders.First(x => x.Id == (string)b["orderId"]!);
                    o.Status = o.Status == "Untriggered" ? "Deactivated" : "Cancelled";
                    return (200, Ok(new { orderId = o.Id }));
                }

                case "/v5/order/cancel-all":
                    foreach (var o in Orders.Where(o => o.Status is "New" or "Untriggered")) o.Status = o.Status == "Untriggered" ? "Deactivated" : "Cancelled";
                    return (200, Ok(new { list = Array.Empty<object>() }));
            }
        }

        return (404, "{}");
    }

    /// <summary>Se ejecuta una orden límite que estaba abierta (el precio llegó).</summary>
    public void FillLimit(string id)
    {
        lock (_gate)
        {
            var o = Orders.First(x => x.Id == id);
            Fill(o, o.Price);
        }
    }

    private void Fill(Ord o, decimal px)
    {
        decimal signed = o.Side == "Buy" ? o.Qty : -o.Qty;
        decimal next = Position + signed;
        if (Position == 0 || Math.Sign(Position) == Math.Sign(signed)) AvgPrice = (AvgPrice * Math.Abs(Position) + px * o.Qty) / Math.Abs(next == 0 ? 1 : next);
        else if (next != 0 && Math.Sign(next) != Math.Sign(Position)) AvgPrice = px;
        Position = next;
        if (Position == 0) AvgPrice = 0;
        o.Exec = o.Qty;
        o.Status = "Filled";
    }

    public void Dispose() => _http.Dispose();
}

public class BybitAccountTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = "bybit-linear", TickSize = 0.1m, VolumeStep = 0.001m };

    private static async Task Until(Func<bool> condition, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    [Fact]
    public void Firma_v5_es_hmac_de_hora_clave_ventana_y_contenido()
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes("s"));
        string expected = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes("1700000000000k5000category=linear"))).ToLowerInvariant();
        Assert.Equal(expected, BybitAccountClient.Sign(1700000000000, "k", 5000, "category=linear", "s"));
    }

    [Fact]
    public void Entornos_apuntan_a_su_servidor()
    {
        Assert.Equal("https://api-demo.bybit.com", new BybitAccountSettings { Environment = BybitEnvironment.Demo }.RestBase);
        Assert.Equal("https://api-testnet.bybit.com", new BybitAccountSettings { Environment = BybitEnvironment.Testnet }.RestBase);
        Assert.Equal("https://api.bybit.com", new BybitAccountSettings { Environment = BybitEnvironment.Real }.RestBase);
        Assert.Equal("https://api.bytick.com", new BybitAccountSettings { Environment = BybitEnvironment.Real, BaseUrl = "https://api.bytick.com/" }.RestBase);
        Assert.False(new BybitAccountSettings { Environment = BybitEnvironment.Demo }.IsReal);
        Assert.True(new BybitAccountSettings { Environment = BybitEnvironment.Real }.IsReal);
    }

    [Fact]
    public async Task Prueba_la_conexion_con_el_saldo_y_explica_las_claves_malas()
    {
        using var fake = new FakeBybit();
        using (var ok = new BybitAccountClient(fake.Settings()))
        {
            var (good, msg) = await ok.TestAsync(CancellationToken.None);
            Assert.True(good, msg);
            Assert.Contains("10500.5", msg.Replace(",", "."));
        }

        var bad = fake.Settings();
        bad.ApiSecret = "otro";
        using var wrong = new BybitAccountClient(bad);
        var (fine, error) = await wrong.TestAsync(CancellationToken.None);
        Assert.False(fine);
        Assert.Contains("10004", error);
        Assert.Contains("API key", error);
    }

    [Fact]
    public async Task Opera_mercado_limite_stop_modifica_cancela_y_cierra()
    {
        using var fake = new FakeBybit();
        var data = new InstrumentData(Btc, new SimulatedFeed());
        using var broker = new BybitBroker(data, fake.Settings(), "Bybit (demo)", pollInterval: TimeSpan.FromMilliseconds(150));
        Assert.False(broker.IsReal);

        // mercado: la posición llega con el refresco
        broker.Market(OrderDirections.Buy, 0.01m, "Chart Trader");
        await Until(() => broker.Position.Volume == 0.01m);
        Assert.Equal(0.01m, broker.Position.Volume);
        Assert.Equal(50000m, broker.Position.AveragePrice);
        var first = fake.Created[0];
        Assert.Equal("linear", (string)first["category"]!);
        Assert.Equal("Buy", (string)first["side"]!);
        Assert.Equal("Market", (string)first["orderType"]!);
        Assert.Equal("0.01", (string)first["qty"]!);
        Assert.StartsWith("th-", (string)first["orderLinkId"]!);

        // límite: el mismo objeto queda activo con su id de Bybit
        var limit = broker.Limit(OrderDirections.Buy, 0.02m, 49000.04m, "Estrategia");
        await Until(() => limit.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(limit));
        Assert.Contains(limit, broker.ActiveOrders);
        Assert.Equal("49000", (string)fake.Created[1]["price"]!); // redondeado al tick
        Assert.Equal("GTC", (string)fake.Created[1]["timeInForce"]!);

        // modificar: amend sobre la misma orden (no cancela)
        broker.Modify(limit, 48900);
        await Until(() => fake.Amended.Count == 1);
        Assert.Equal("48900", (string)fake.Amended[0]["price"]!);
        Assert.Null(fake.Amended[0]["qty"]);
        await Task.Delay(400);
        Assert.Equal(48900m, limit.Price);
        Assert.Contains(limit, broker.ActiveOrders);

        // cambiar la cantidad también es amend
        var same = broker.Replace(limit, 48950, 0.03m);
        Assert.Same(limit, same);
        await Until(() => fake.Amended.Count == 2);
        Assert.Equal("0.03", (string)fake.Amended[1]["qty"]!);

        // se ejecuta del lado de Bybit: deja de estar activa y figura como ejecutada
        fake.FillLimit(limit.ExchangeOrderId);
        await Until(() => limit.State == OrderStates.Done);
        Assert.Equal(OrderStatus.Filled, limit.Status);
        Assert.DoesNotContain(limit, broker.ActiveOrders);
        await Until(() => broker.Position.Volume == 0.04m);
        Assert.Equal(0.04m, broker.Position.Volume);

        // stop loss: condicional a mercado, solo reduce, se dispara al bajar
        var stop = ((ITradingAccount)broker).Exit(OrderTypes.Stop, OrderDirections.Sell, 0.04m, 48000, "SL");
        await Until(() => stop.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(stop));
        var sl = fake.Created[2];
        Assert.Equal("Market", (string)sl["orderType"]!);
        Assert.Equal("48000", (string)sl["triggerPrice"]!);
        Assert.Equal(2, (int)sl["triggerDirection"]!);
        Assert.True((bool)sl["reduceOnly"]!);
        Assert.Equal(OrderTypes.Stop, stop.Type);
        Assert.Equal(48000m, stop.TriggerPrice);

        // mover el stop cambia el disparo
        broker.Modify(stop, 48200);
        await Until(() => fake.Amended.Count == 3);
        Assert.Equal("48200", (string)fake.Amended[2]["triggerPrice"]!);

        // cancelar
        broker.Cancel(stop);
        await Until(() => stop.State == OrderStates.Done);
        Assert.Equal(OrderStatus.Canceled, stop.Status);

        // cerrar: cancela lo que quede y manda mercado solo-reducir
        broker.Flatten();
        await Until(() => broker.Position.Volume == 0);
        Assert.Equal(0m, broker.Position.Volume);
        var close = fake.Created[^1];
        Assert.Equal("Sell", (string)close["side"]!);
        Assert.True((bool)close["reduceOnly"]!);
        Assert.Equal(0, fake.BadSignatures);
        Assert.NotNull(broker.Wallet);
    }

    [Fact]
    public async Task Una_orden_rechazada_queda_fallida_con_el_motivo()
    {
        using var fake = new FakeBybit();
        var settings = fake.Settings();
        settings.ApiSecret = "malo";
        var data = new InstrumentData(Btc, new SimulatedFeed());
        using var broker = new BybitBroker(data, settings, "Bybit (demo)", pollInterval: TimeSpan.FromSeconds(5));
        var o = broker.Limit(OrderDirections.Sell, 0.01m, 60000);
        await Until(() => o.State == OrderStates.Failed);
        Assert.Equal(OrderStatus.Rejected, o.Status);
        Assert.Contains("10004", broker.LastError);
        Assert.DoesNotContain(o, broker.ActiveOrders);
    }

    [Fact]
    public void Modo_cobertura_explica_que_hay_que_pasar_a_unidireccional()
    {
        var msg = BybitAccountClient.Explain(10001, "position idx not match position mode");
        Assert.Contains("one-way", msg);
    }
}
