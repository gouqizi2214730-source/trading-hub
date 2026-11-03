using TradingHub.App.Controls;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.App.Services;

/// <summary>
/// Velas automáticas: mantiene bajada y al día la historia en velas (1, 5 y 15 minutos) de los instrumentos elegidos,
/// en segundo plano. Al arrancar y cada media hora baja solo lo nuevo (la caché de velas se completa desde la última
/// guardada), así cambiar de período o abrir años de historia no espera a la red.
/// </summary>
public sealed class KlineKeeper : IDisposable
{
    public const int MaxInstruments = 60;
    private readonly AppServices _svc;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _run = new(1, 1);
    private Task? _loop;

    public KlineKeeper(AppServices svc) => _svc = svc;

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "";
    public DateTime? LastRun { get; private set; }

    /// <summary>Último estado por instrumento (clave perfil|símbolo → texto).</summary>
    public Dictionary<string, string> ItemStatus { get; } = new();

    public event Action? Changed;

    public static string Key(string profileId, string symbol) => profileId + "|" + symbol;

    /// <summary>Arranca el ciclo (a los 30 segundos de abrir y después cada media hora).</summary>
    public void Start()
    {
        _loop ??= Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                while (!_cts.IsCancellationRequested)
                {
                    if (_svc.Settings.KlineKeepEnabled) await RunAsync(_cts.Token);
                    await Task.Delay(TimeSpan.FromMinutes(30), _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>Agrega un instrumento a la lista (si está activado el agregado automático) y lo baja enseguida.</summary>
    public void Track(string profileId, Instrument instrument)
    {
        var s = _svc.Settings;
        if (!s.KlineKeepEnabled || !s.KlineKeepAuto) return;
        string key = Key(profileId, instrument.Symbol);
        if (s.KlineKeepInstruments.Contains(key)) return;
        if (s.KlineKeepInstruments.Count >= MaxInstruments) return;
        try
        {
            if (_svc.Connections.GetFeed(profileId) is not IKlineHistory) return;
        }
        catch
        {
            return;
        }

        s.KlineKeepInstruments.Add(key);
        s.Save();
        Changed?.Invoke();
        _ = RunAsync(_cts.Token);
    }

    /// <summary>Baja lo que falte de todos los instrumentos de la lista (una sola corrida a la vez).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (!await _run.WaitAsync(0, ct)) return;
        try
        {
            IsRunning = true;
            var keys = _svc.Settings.KlineKeepInstruments.ToList();
            int years = Math.Clamp(_svc.Settings.KlineKeepYears, 1, 10);
            for (int i = 0; i < keys.Count && !ct.IsCancellationRequested; i++)
            {
                string key = keys[i];
                Status = $"Actualizando velas {i + 1}/{keys.Count}: {key.Split('|').Last()}";
                Changed?.Invoke();
                try
                {
                    ItemStatus[key] = await UpdateOneAsync(key, years, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ItemStatus[key] = "error: " + NetworkProxy.Explain(ex);
                    this.LogWarn($"Velas automáticas de {key}: {ex.Message}");
                }

                Changed?.Invoke();
            }

            LastRun = DateTime.UtcNow;
            Status = keys.Count == 0 ? "No hay instrumentos en la lista." : $"Al día ({DateTime.Now:HH:mm}).";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelado.";
        }
        finally
        {
            IsRunning = false;
            _run.Release();
            Changed?.Invoke();
        }
    }

    private async Task<string> UpdateOneAsync(string key, int years, CancellationToken ct)
    {
        var parts = key.Split('|', 2);
        if (parts.Length != 2) return "clave inválida";
        var profile = _svc.Connections.Find(parts[0]);
        if (profile == null || !profile.Enabled) return "la conexión no está activa";
        var feed = _svc.Connections.GetFeed(profile.Id);
        if (feed is not IKlineHistory klines) return "esta conexión no da velas";
        IReadOnlyList<Instrument> instruments;
        try
        {
            instruments = await InstrumentPicker.InstrumentsAsync(_svc, profile.Id);
        }
        catch (Exception)
        {
            instruments = InstrumentCatalog.Read(profile.Id);
        }

        var inst = instruments.FirstOrDefault(x => x.Symbol.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
        if (inst == null) return "no está en la conexión";
        var now = feed.Now;
        var first = await klines.FirstAvailableAsync(inst, ct) ?? now.AddYears(-years);
        var from = now.AddYears(-years);
        if (first > from) from = first;
        // las mismas resoluciones que usa el gráfico según el largo pedido (ver KlineHistory.MinutesFor)
        var plan = new List<(DateTime From, int Minutes)> { (Max(from, now.AddDays(-400)), 1) };
        if ((now - from).TotalDays > 400) plan.Add((Max(from, now.AddDays(-1500)), 5));
        if ((now - from).TotalDays > 1500) plan.Add((from, 15));
        Exception? last = null;
        int total = 0;
        foreach (var (f, minutes) in plan)
        {
            var got = await KlineHistory.LoadAsync(klines, inst, f, now, minutes, 0, null, ct, ex => last = ex);
            total += got.Count;
        }

        var until = KlineHistory.CachedUntil(klines, inst, 1);
        string when = until is { } u ? TimeZoneInfo.ConvertTimeFromUtc(u, _svc.Settings.TimeZoneId is { Length: > 0 } tz && TryTz(tz) is { } z ? z : TimeZoneInfo.Local).ToString("dd/MM HH:mm") : "—";
        return last != null ? $"parcial ({NetworkProxy.Explain(last)})" : $"al día hasta {when} · {total:N0} velas";
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static TimeZoneInfo? TryTz(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => _cts.Cancel();
}
