using System.Text.Json;
using TradingHub.App.Services;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Las alertas salen por webhook (POST JSON) y por Telegram, y quedan en el registro.</summary>
public class AlertCenterTests
{
    [Fact]
    public async Task Webhook_recibe_el_json_y_telegram_el_texto()
    {
        using var s = new FakeHttpServer();
        s.OnHttp = req => req.Route.StartsWith("/bot", StringComparison.Ordinal) ? (200, "{\"ok\":true}") : (200, "ok");
        var ev = new AlertEvent(new DateTime(2026, 11, 4, 1, 2, 3, DateTimeKind.Utc), AlertKind.Indicator, "BTCUSDT", "VP", "ruptura alcista");
        await AlertCenter.PostWebhookAsync(new Uri(s.Http + "/hook"), "X-Token: abc", ev);
        var req = Assert.Single(s.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("abc", req.Headers.GetValueOrDefault("X-Token"));
        using var doc = JsonDocument.Parse(req.Body);
        Assert.Equal("indicator", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("BTCUSDT", doc.RootElement.GetProperty("instrument").GetString());
        Assert.Equal("[KVP] BTCUSDT · VP: ruptura alcista", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("2026-11-04T01:02:03.0000000Z", doc.RootElement.GetProperty("time").GetString());
    }

    [Fact]
    public void El_texto_de_una_alerta()
    {
        Assert.Equal("[KVP] ETHUSDT · Precio: tocó 3000", AlertCenter.Text(new AlertEvent(DateTime.UtcNow, AlertKind.Price, "ETHUSDT", "Precio", "tocó 3000")));
        Assert.Equal("[KVP] Prueba: hola", AlertCenter.Text(new AlertEvent(DateTime.UtcNow, AlertKind.System, "", "Prueba", "hola")));
    }
}
