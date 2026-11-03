using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingHub.Feeds;
using TradingHub.Feeds.Longbridge;
using TradingHub.Market;
using Xunit;

namespace TradingHub.Tests;

/// <summary>长桥 (Longbridge): protobuf a mano, framing del socket, OAuth 2.0 con PKCE y el feed contra un servidor de mentira.</summary>
public class LongbridgeTests
{
    [Fact]
    public void ProtoRoundTrip()
    {
        var body = LbMessages.SubscribeRequest(new[] { "700.HK", "AAPL.US" }, new[] { LbCmd.SubTrade, LbCmd.SubDepth }, firstPush: true);
        var symbols = new List<string>();
        var types = new List<int>();
        bool first = false;
        var r = new ProtoReader(body);
        while (r.Next())
        {
            switch (r.Field)
            {
                case 1: symbols.Add(r.String()); break;
                case 2: types.AddRange(r.PackedInt32()); break;
                case 3: first = r.Bool(); break;
                default: r.Skip(); break;
            }
        }

        Assert.Equal(new[] { "700.HK", "AAPL.US" }, symbols);
        Assert.Equal(new[] { 4, 2 }, types);
        Assert.True(first);

        // varints grandes y negativos
        var w = new ProtoWriter();
        w.Int64(1, long.MaxValue);
        w.Int32(2, -5);
        var rr = new ProtoReader(w.ToArray());
        Assert.True(rr.Next());
        Assert.Equal(long.MaxValue, rr.Int64());
        Assert.True(rr.Next());
        Assert.Equal(-5, rr.Int32());
        Assert.False(rr.Next());
    }

    private static byte[] CandlestickResponse(string symbol, params (long T, decimal O, decimal H, decimal L, decimal C, long V)[] candles)
    {
        var w = new ProtoWriter();
        w.String(1, symbol);
        foreach (var c in candles)
        {
            var k = new ProtoWriter();
            k.String(1, c.C.ToString(System.Globalization.CultureInfo.InvariantCulture));
            k.String(2, c.O.ToString(System.Globalization.CultureInfo.InvariantCulture));
            k.String(3, c.L.ToString(System.Globalization.CultureInfo.InvariantCulture));
            k.String(4, c.H.ToString(System.Globalization.CultureInfo.InvariantCulture));
            k.Int64(5, c.V);
            k.String(6, "1234.5");
            k.Int64(7, c.T);
            w.Message(2, k);
        }

        return w.ToArray();
    }

    [Fact]
    public void ReadsCandlesTradesAndDepth()
    {
        var candles = LbMessages.ReadCandlesticks(CandlestickResponse("700.HK", (1700000060, 10.5m, 10.8m, 10.4m, 10.7m, 500), (1700000000, 10.2m, 10.6m, 10.1m, 10.5m, 300)));
        Assert.Equal(2, candles.Count);
        Assert.Equal(1700000000, candles[0].TimestampSec); // ordenadas
        Assert.Equal(10.7m, candles[1].Close);
        Assert.Equal(10.4m, candles[1].Low);

        var t = new ProtoWriter();
        t.String(1, "AAPL.US");
        var tr = new ProtoWriter();
        tr.String(1, "190.25");
        tr.Int64(2, 100);
        tr.Int64(3, 1700000000);
        tr.String(4, "");
        tr.Int32(5, 2);
        t.Message(3, tr);
        var (sym, trades) = LbMessages.ReadTrades(t.ToArray(), push: true);
        Assert.Equal("AAPL.US", sym);
        Assert.Single(trades);
        Assert.Equal(190.25m, trades[0].Price);
        Assert.Equal(2, trades[0].Direction);

        var d = new ProtoWriter();
        d.String(1, "700.HK");
        var lvl = new ProtoWriter();
        lvl.Int32(1, 1);
        lvl.String(2, "300.2");
        lvl.Int64(3, 4000);
        lvl.Int64(4, 12);
        d.Message(3, lvl);
        var (_, asks, bids) = LbMessages.ReadDepth(d.ToArray(), push: true);
        Assert.Single(asks);
        Assert.Empty(bids);
        Assert.Equal(300.2m, asks[0].Price);
        Assert.Equal(4000, asks[0].Volume);
    }

    [Fact]
    public void PacketFraming()
    {
        var body = LbMessages.AuthRequest("otp-123");
        var req = LbPacket.Request(LbCmd.Auth, 7, 5000, body);
        Assert.Equal(11 + body.Length, req.Length);
        Assert.Equal(LbPacket.TypeRequest, req[0]);
        Assert.Equal(LbCmd.Auth, req[1]);
        var parsed = LbPacket.Parse(req);
        Assert.Equal(7u, parsed.RequestId);
        Assert.Equal(body, parsed.Body);

        // respuesta comprimida con gzip
        var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) gz.Write(body);
        var zipped = ms.ToArray();
        var resp = LbPacket.Response(LbCmd.Auth, 7, LbPacket.StatusSuccess, zipped);
        resp[0] |= 0x20;
        var p2 = LbPacket.Parse(resp);
        Assert.Equal(LbPacket.TypeResponse, p2.Type);
        Assert.Equal(body, p2.Body);

        // push: 5 bytes de cabecera
        var push = new byte[5 + body.Length];
        push[0] = LbPacket.TypePush;
        push[1] = LbCmd.PushTradeData;
        push[4] = (byte)body.Length;
        body.CopyTo(push, 5);
        var p3 = LbPacket.Parse(push);
        Assert.Equal(LbCmd.PushTradeData, p3.Cmd);
        Assert.Equal(body, p3.Body);
    }

    [Fact]
    public void OAuthUrlsAndPkce()
    {
        var (verifier, challenge) = LongbridgeOAuth.Pkce();
        Assert.Equal(LongbridgeOAuth.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), challenge);
        Assert.DoesNotContain('=', challenge);
        var url = LongbridgeOAuth.AuthorizeUrl(LongbridgeOAuth.BaseUrlGlobal, "cid", LongbridgeOAuth.RedirectUri(60355), "st", challenge);
        Assert.StartsWith("https://openapi.longbridge.com/oauth2/authorize?response_type=code&client_id=cid", url);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A60355%2Fcallback", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Equal("us", LongbridgeHttp.DcRegion("us_abc"));
        Assert.Equal("ap", LongbridgeHttp.DcRegion("ap_abc"));
        Assert.Equal("HK", LongbridgeFeed.Market("700.HK"));
        Assert.Equal(0.05m, LongbridgeFeed.TickSizeFor("700.HK", 95m));
        Assert.Equal(0.5m, LongbridgeFeed.TickSizeFor("700.HK", 600m));
        Assert.Equal(0.01m, LongbridgeFeed.TickSizeFor("AAPL.US", 600m));
        var (date, minute) = LongbridgeFeed.MarketTime("AAPL.US", new DateTime(2026, 11, 3, 14, 30, 0, DateTimeKind.Utc));
        Assert.Equal("20261103", date);
        Assert.Equal("0930", minute);
        var (dateHk, minuteHk) = LongbridgeFeed.MarketTime("700.HK", new DateTime(2026, 11, 3, 1, 30, 0, DateTimeKind.Utc));
        Assert.Equal("20261103", dateHk);
        Assert.Equal("0930", minuteHk);
    }

    /// <summary>Servidor de 长桥 de mentira: OAuth, OTP y el socket de cotizaciones.</summary>
    private sealed class FakeLongbridge : IDisposable
    {
        public readonly FakeHttpServer Server = new();
        public string? CodeVerifier;
        public int Auths, Subscribes;

        public FakeLongbridge()
        {
            Server.OnHttp = req =>
            {
                if (req.Path.StartsWith("/oauth2/register")) return (201, JsonSerializer.Serialize(new { client_id = "kvp-client", client_secret = (string?)null }));
                if (req.Path.StartsWith("/oauth2/token"))
                {
                    var form = req.Body.Split('&').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => Uri.UnescapeDataString(kv[1]));
                    if (form["grant_type"] == "authorization_code")
                    {
                        CodeVerifier = form["code_verifier"];
                        if (form["code"] != "code-1" || form["client_id"] != "kvp-client") return (400, "{\"error\":\"invalid_grant\"}");
                        return (200, JsonSerializer.Serialize(new { access_token = "us_access1", refresh_token = "refresh1", expires_in = 7200, token_type = "Bearer" }));
                    }

                    if (form["grant_type"] == "refresh_token" && form["refresh_token"] == "refresh1")
                        return (200, JsonSerializer.Serialize(new { access_token = "us_access2", expires_in = 7200 }));
                    return (400, "{\"error\":\"invalid_grant\"}");
                }

                if (req.Path.StartsWith("/v1/socket/token"))
                {
                    if (req.Headers.GetValueOrDefault("Authorization") != "Bearer us_access1" && req.Headers.GetValueOrDefault("Authorization") != "Bearer us_access2") return (401, "{\"code\":401,\"message\":\"token inválido\"}");
                    return (200, "{\"code\":0,\"data\":{\"otp\":\"otp-xyz\",\"limit\":10,\"online\":1}}");
                }

                return (404, "{}");
            };
            Server.OnSocket = SocketAsync;
        }

        private async Task SocketAsync(string path, WebSocket ws, CancellationToken ct)
        {
            Assert.Contains("version=1", path);
            Assert.Contains("codec=1", path);
            var buf = new byte[1 << 16];
            var ms = new MemoryStream();
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                var p = LbPacket.Parse(ms.ToArray());
                byte[] body;
                byte status = LbPacket.StatusSuccess;
                switch (p.Cmd)
                {
                    case LbCmd.Auth:
                    {
                        var rr = new ProtoReader(p.Body);
                        string token = "";
                        while (rr.Next())
                            if (rr.Field == 1) token = rr.String();
                            else rr.Skip();
                        Auths++;
                        if (token != "otp-xyz")
                        {
                            status = LbPacket.StatusUnauthenticated;
                            var e = new ProtoWriter();
                            e.Int64(1, 401);
                            e.String(2, "otp inválido");
                            body = e.ToArray();
                            break;
                        }

                        var w = new ProtoWriter();
                        w.String(1, "sess-1");
                        w.Int64(2, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());
                        body = w.ToArray();
                        break;
                    }

                    case LbCmd.QuerySecurityStaticInfo:
                    {
                        var w = new ProtoWriter();
                        foreach (var sym in new[] { "700.HK", "AAPL.US" })
                        {
                            var i = new ProtoWriter();
                            i.String(1, sym);
                            i.String(2, sym == "700.HK" ? "腾讯控股" : "苹果");
                            i.String(3, sym == "700.HK" ? "TENCENT" : "Apple");
                            i.String(6, sym == "700.HK" ? "SEHK" : "NASDAQ");
                            i.String(7, sym == "700.HK" ? "HKD" : "USD");
                            i.Int32(8, sym == "700.HK" ? 100 : 1);
                            w.Message(1, i);
                        }

                        body = w.ToArray();
                        break;
                    }

                    case LbCmd.QuerySecurityQuote:
                    {
                        var w = new ProtoWriter();
                        foreach (var sym in new[] { "700.HK", "AAPL.US" })
                        {
                            var q = new ProtoWriter();
                            q.String(1, sym);
                            q.String(2, sym == "700.HK" ? "412.4" : "190.1");
                            q.Int64(7, 1700000000);
                            w.Message(1, q);
                        }

                        body = w.ToArray();
                        break;
                    }

                    case LbCmd.QueryHistoryCandlestick:
                    {
                        // velas de 1 minuto desde el minuto pedido (dos páginas de 3; la tercera vacía)
                        var rr = new ProtoReader(p.Body);
                        string date = "", minute = "";
                        int period = 0;
                        while (rr.Next())
                        {
                            if (rr.Field == 2) period = rr.Int32();
                            else if (rr.Field == 5)
                            {
                                var off = new ProtoReader(rr.Bytes());
                                while (off.Next())
                                {
                                    if (off.Field == 2) date = off.String();
                                    else if (off.Field == 3) minute = off.String();
                                    else off.Skip();
                                }
                            }
                            else rr.Skip();
                        }

                        Assert.Equal(1, period);
                        var local = DateTime.ParseExact(date + minute, "yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
                        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), LongbridgeFeed.MarketTimeZone("700.HK"));
                        long start = new DateTimeOffset(utc).ToUnixTimeSeconds();
                        long limit = 1699999980 + 6 * 60;
                        var list = new List<(long, decimal, decimal, decimal, decimal, long)>();
                        for (long t = start; t < limit && list.Count < 3; t += 60) list.Add((t, 10m, 11m, 9m, 10.5m, 100));
                        body = CandlestickResponse("700.HK", list.ToArray());
                        break;
                    }

                    case LbCmd.Subscribe:
                    {
                        Subscribes++;
                        body = Array.Empty<byte>();
                        // y enseguida un push con dos operaciones y la profundidad
                        var t = new ProtoWriter();
                        t.String(1, "700.HK");
                        foreach (var (price, dir) in new[] { ("412.6", 2), ("412.4", 1) })
                        {
                            var tr = new ProtoWriter();
                            tr.String(1, price);
                            tr.Int64(2, 200);
                            tr.Int64(3, 1700000100);
                            tr.Int32(5, dir);
                            t.Message(3, tr);
                        }

                        var pushBody = t.ToArray();
                        var push = new byte[5 + pushBody.Length];
                        push[0] = LbPacket.TypePush;
                        push[1] = LbCmd.PushTradeData;
                        push[4] = (byte)pushBody.Length;
                        pushBody.CopyTo(push, 5);
                        await ws.SendAsync(LbPacket.Response(p.Cmd, p.RequestId, status, body), WebSocketMessageType.Binary, true, ct);
                        await ws.SendAsync(push, WebSocketMessageType.Binary, true, ct);
                        continue;
                    }

                    case LbCmd.QueryTrade:
                    {
                        var t = new ProtoWriter();
                        t.String(1, "700.HK");
                        var tr = new ProtoWriter();
                        tr.String(1, "412.0");
                        tr.Int64(2, 300);
                        tr.Int64(3, 1700000050);
                        tr.Int32(5, 0);
                        t.Message(2, tr);
                        body = t.ToArray();
                        break;
                    }

                    default:
                        body = Array.Empty<byte>();
                        break;
                }

                await ws.SendAsync(LbPacket.Response(p.Cmd, p.RequestId, status, body), WebSocketMessageType.Binary, true, ct);
            }
        }

        public void Dispose() => Server.Dispose();
    }

    private sealed class Sink : IMarketDataSink
    {
        public readonly List<Trade> Trades = new();
        public readonly TaskCompletionSource<bool> Got = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FeedStatus Status;

        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            lock (Trades) Trades.AddRange(trades);
            Got.TrySetResult(true);
        }

        public void OnDepth(Instrument instrument, DepthUpdate update)
        {
        }

        public void OnStatus(Instrument instrument, FeedStatus status, string? message) => Status = status;
    }

    [Fact]
    public async Task LoginRegistersClientAndExchangesCodeWithPkce()
    {
        using var fake = new FakeLongbridge();
        using var http = new HttpClient();
        var (token, clientId) = await LongbridgeOAuth.LoginAsync(fake.Server.Http, null, "KVP", url =>
        {
            // el «navegador»: valida la URL y vuelve a la redirección con el código y el state
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            Assert.Equal("kvp-client", q["client_id"]);
            Assert.Equal("S256", q["code_challenge_method"]);
            string redirect = q["redirect_uri"]!;
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                await http.GetStringAsync(redirect + "?code=code-1&state=" + Uri.EscapeDataString(q["state"]!));
            });
        }, CancellationToken.None, TimeSpan.FromSeconds(20));
        Assert.Equal("kvp-client", clientId);
        Assert.Equal("us_access1", token.Access);
        Assert.Equal("refresh1", token.Refresh);
        Assert.True(token.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 7000);
        Assert.False(string.IsNullOrEmpty(fake.CodeVerifier));
        var register = fake.Server.Requests.First(r => r.Path.StartsWith("/oauth2/register"));
        Assert.Contains("\"KVP\"", register.Body);
        Assert.Contains("http://localhost:60355/callback", register.Body);
    }

    [Fact]
    public async Task FeedListsHistoryAndLiveTradesThroughTheSocket()
    {
        using var fake = new FakeLongbridge();
        var settings = new LongbridgeSettings
        {
            ClientId = "kvp-client",
            AccessToken = "us_access1",
            RefreshToken = "refresh1",
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 7200,
            BaseUrl = fake.Server.Http,
            QuoteWsUrl = fake.Server.Ws + "/v2",
            Symbols = new List<string> { "700.HK", "AAPL.US" },
        };
        using var feed = new LongbridgeFeed(settings);
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
        var instruments = await feed.GetInstrumentsAsync(ct);
        Assert.Equal(2, instruments.Count);
        var tencent = instruments.First(i => i.Symbol == "700.HK");
        Assert.Equal("腾讯控股", tencent.Description);
        Assert.Equal(0.2m, tencent.TickSize); // 412,4 HKD → tick 0,2
        Assert.Equal("HKD", tencent.QuoteAsset);
        Assert.Equal(0.01m, instruments.First(i => i.Symbol == "AAPL.US").TickSize);
        Assert.Equal(1, fake.Auths);

        // velas de 1 minuto: dos páginas (3 + 3) y la tercera vacía corta
        var klines = new List<Kline>();
        var from = DateTimeOffset.FromUnixTimeSeconds(1699999980).UtcDateTime;
        await feed.LoadKlinesAsync(tencent, from, from.AddMinutes(10), 1, 0, b =>
        {
            klines.AddRange(b);
            return true;
        }, null, ct);
        Assert.Equal(6, klines.Count);
        Assert.Equal(1699999980L * 1000, klines[0].OpenTimeMs);
        Assert.Equal(1700000280L * 1000, klines[5].OpenTimeMs);
        Assert.False(klines[0].HasBuyVolume);

        // el tape del día
        var trades = new List<Trade>();
        await feed.LoadTradesAsync(tencent, from.AddMinutes(-10), from.AddMinutes(10), null, b =>
        {
            trades.AddRange(b);
            return true;
        }, null, ct);
        Assert.Single(trades);
        Assert.Equal(412.0m, trades[0].Price);

        // en vivo: suscripción y push con el lado real
        var sink = new Sink();
        using (feed.Subscribe(tencent, sink))
        {
            await sink.Got.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, sink.Trades.Count);
            Assert.Equal(TradeSide.Buy, sink.Trades[0].Side);
            Assert.Equal(TradeSide.Sell, sink.Trades[1].Side);
            Assert.Equal(412.6m, sink.Trades[0].Price);
            Assert.Equal(1, fake.Subscribes);
        }

        var (ok, msg) = await feed.TestAsync(ct);
        Assert.True(ok, msg);
        Assert.Contains("腾讯控股", msg);
        Assert.Equal(1, fake.Auths); // una sola conexión para todo
    }

    [Fact]
    public async Task HttpRefreshesTheTokenWhenAboutToExpire()
    {
        using var fake = new FakeLongbridge();
        var refreshed = 0;
        var settings = new LongbridgeSettings
        {
            ClientId = "kvp-client",
            AccessToken = "us_access1",
            RefreshToken = "refresh1",
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60, // vence en un minuto: se renueva antes de usarlo
            BaseUrl = fake.Server.Http,
        };
        settings.TokensRefreshed = _ => refreshed++;
        using var http = new LongbridgeHttp(settings);
        string otp = await http.SocketOtpAsync(CancellationToken.None);
        Assert.Equal("otp-xyz", otp);
        Assert.Equal("us_access2", settings.AccessToken);
        Assert.Equal("refresh1", settings.RefreshToken); // el servidor no mandó otro: se conserva
        Assert.Equal(1, refreshed);
        var req = fake.Server.Requests.First(r => r.Path.StartsWith("/v1/socket/token"));
        Assert.Equal("Bearer us_access2", req.Headers["Authorization"]);
        Assert.Equal("us", req.Headers["x-dc-region"]);
    }
}
