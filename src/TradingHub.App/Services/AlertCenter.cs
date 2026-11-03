using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using TradingHub.Common.Logging;

namespace TradingHub.App.Services;

public enum AlertKind
{
    Price,
    Indicator,
    Strategy,
    Order,
    System,
}

public sealed record AlertEvent(DateTime TimeUtc, AlertKind Kind, string Instrument, string Source, string Message);

/// <summary>
/// Todas las alertas pasan por acá: las de precio (líneas con campana), las de los indicadores (AddAlert), las de las
/// estrategias (Alert / límites / ejecuciones) y las del sistema. Cada una se muestra en la app (aviso + sonido + barra de
/// estado), queda en el registro (alertas.jsonl) y, si está configurado, se manda afuera por webhook (POST con JSON) y
/// Telegram. Las salidas externas son la «interfaz» para conectar bots, n8n, Discord, etc.
/// </summary>
public sealed class AlertCenter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly AppSettings _settings;
    private readonly string _logPath;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _recent = new();

    public AlertCenter(AppSettings settings)
    {
        _settings = settings;
        _logPath = Path.Combine(AppPaths.Root, "alertas.jsonl");
    }

    /// <summary>Últimas alertas (las más nuevas al final).</summary>
    public List<AlertEvent> Recent { get; } = new();

    /// <summary>Se dispara en el hilo de UI con cada alerta (la vista muestra el aviso).</summary>
    public event Action<AlertEvent>? Raised;

    /// <summary>Levanta una alerta. Las repetidas (mismo texto en menos de 2 s) se descartan.</summary>
    public void Raise(AlertKind kind, string instrument, string source, string message)
    {
        var ev = new AlertEvent(DateTime.UtcNow, kind, instrument ?? "", source ?? "", message ?? "");
        lock (_lock)
        {
            string key = ev.Instrument + "|" + ev.Source + "|" + ev.Message;
            if (_recent.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(2)) return;
            _recent[key] = DateTime.UtcNow;
            if (_recent.Count > 500) _recent.Clear();
            Recent.Add(ev);
            if (Recent.Count > 300) Recent.RemoveRange(0, Recent.Count - 300);
        }

        Append(ev);
        if (Dispatcher.UIThread.CheckAccess()) Raised?.Invoke(ev);
        else Dispatcher.UIThread.Post(() => Raised?.Invoke(ev));
        _ = SendExternalAsync(ev);
    }

    private void Append(AlertEvent ev)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, JsonSerializer.Serialize(ev) + Environment.NewLine);
        }
        catch
        {
            // sin registro en disco: sigue en memoria
        }
    }

    /// <summary>Carga las últimas alertas guardadas (al abrir).</summary>
    public void LoadRecent()
    {
        try
        {
            if (!File.Exists(_logPath)) return;
            var lines = File.ReadLines(_logPath).TakeLast(200);
            foreach (var l in lines)
            {
                try
                {
                    if (JsonSerializer.Deserialize<AlertEvent>(l) is { } ev) Recent.Add(ev);
                }
                catch
                {
                    // línea rota
                }
            }
        }
        catch
        {
            // sin registro
        }
    }

    // ===================================================================== salidas externas

    public static string KindName(AlertKind k) => k switch
    {
        AlertKind.Price => "price",
        AlertKind.Indicator => "indicator",
        AlertKind.Strategy => "strategy",
        AlertKind.Order => "order",
        _ => "system",
    };

    private bool ExternalWanted(AlertKind k) => k switch
    {
        AlertKind.Price => _settings.AlertExternalPrice,
        AlertKind.Indicator => _settings.AlertExternalIndicators,
        AlertKind.Strategy => _settings.AlertExternalStrategies,
        AlertKind.Order => _settings.AlertExternalOrders,
        _ => true,
    };

    private async Task SendExternalAsync(AlertEvent ev)
    {
        var s = _settings;
        if (!ExternalWanted(ev.Kind)) return;
        var tasks = new List<Task>();
        if (s.AlertWebhookEnabled && Uri.TryCreate(s.AlertWebhookUrl, UriKind.Absolute, out var url)) tasks.Add(PostWebhookAsync(url, s.AlertWebhookHeader, ev));
        if (s.AlertTelegramEnabled && !string.IsNullOrWhiteSpace(s.AlertTelegramToken) && !string.IsNullOrWhiteSpace(s.AlertTelegramChatId))
            tasks.Add(SendTelegramAsync(s.AlertTelegramToken!, s.AlertTelegramChatId!, ev));
        foreach (var t in tasks)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogWarn("No se pudo mandar la alerta afuera: " + ex.Message);
            }
        }
    }

    /// <summary>POST JSON: { "app": "KVP", "time": ISO-8601, "kind", "instrument", "source", "message" } (+ cabecera opcional, p. ej. Authorization).</summary>
    public static async Task PostWebhookAsync(Uri url, string? header, AlertEvent ev)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new
            {
                app = TradingHub.Common.Product.Name,
                time = ev.TimeUtc.ToString("o"),
                kind = KindName(ev.Kind),
                instrument = ev.Instrument,
                source = ev.Source,
                message = ev.Message,
                text = Text(ev),
            }),
        };
        if (!string.IsNullOrWhiteSpace(header) && header.Contains(':'))
        {
            var i = header.IndexOf(':');
            req.Headers.TryAddWithoutValidation(header[..i].Trim(), header[(i + 1)..].Trim());
        }

        using var r = await Http.SendAsync(req).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
    }

    public static async Task SendTelegramAsync(string token, string chatId, AlertEvent ev)
    {
        var url = $"https://api.telegram.org/bot{token}/sendMessage";
        using var r = await Http.PostAsJsonAsync(url, new { chat_id = chatId, text = Text(ev) }).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
    }

    /// <summary>El texto de una alerta para afuera: «[KVP] BTCUSDT · VP: ruptura…».</summary>
    public static string Text(AlertEvent ev)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(TradingHub.Common.Product.Name).Append("] ");
        if (ev.Instrument.Length > 0) sb.Append(ev.Instrument).Append(" · ");
        if (ev.Source.Length > 0) sb.Append(ev.Source).Append(": ");
        sb.Append(ev.Message);
        return sb.ToString();
    }

    /// <summary>Manda una alerta de prueba por las salidas configuradas; devuelve el error si falla.</summary>
    public async Task<string?> TestExternalAsync()
    {
        var ev = new AlertEvent(DateTime.UtcNow, AlertKind.System, "", "Prueba", "Alerta de prueba de " + TradingHub.Common.Product.Name);
        var errors = new List<string>();
        var s = _settings;
        if (s.AlertWebhookEnabled)
        {
            try
            {
                if (!Uri.TryCreate(s.AlertWebhookUrl, UriKind.Absolute, out var url)) throw new InvalidOperationException("La URL del webhook no es válida");
                await PostWebhookAsync(url, s.AlertWebhookHeader, ev).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add("Webhook: " + ex.Message);
            }
        }

        if (s.AlertTelegramEnabled)
        {
            try
            {
                await SendTelegramAsync(s.AlertTelegramToken ?? "", s.AlertTelegramChatId ?? "", ev).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add("Telegram: " + ex.Message);
            }
        }

        if (!s.AlertWebhookEnabled && !s.AlertTelegramEnabled) return "No hay ninguna salida externa activada.";
        return errors.Count == 0 ? null : string.Join(" · ", errors);
    }
}
