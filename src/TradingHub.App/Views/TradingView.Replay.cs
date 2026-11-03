using Avalonia.Controls;
using TradingHub.App.Controls;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.App.Views;

/// <summary>
/// Replay aleatorio para practicar: elige al azar un instrumento con días bajados y un momento de inicio, y abre un replay
/// (a ciegas si querés: sin ver el instrumento ni la fecha). Al terminar, o al revelar, muestra el resultado de lo operado
/// con la cuenta simulada.
/// </summary>
public sealed partial class TradingView
{
    private sealed record ReplayRun(ChartTab Tab, DateTime StartUtc, int TradesBefore, decimal PnlBefore, bool Blind);

    private readonly List<ReplayRun> _replayRuns = new();
    private readonly Random _random = new();

    /// <summary>Instrumentos con al menos <paramref name="minDays"/> días de ticks bajados: (conexión, instrumento, días).</summary>
    private async Task<List<(string ProfileId, Instrument Instrument, List<DateOnly> Days)>> ReplayCandidatesAsync(int minDays)
    {
        var res = new List<(string, Instrument, List<DateOnly>)>();
        foreach (var p in _svc.Connections.Enabled.Where(p => p.HasMarketData))
        {
            IMarketDataFeed feed;
            try
            {
                feed = _svc.Connections.GetFeed(p.Id);
            }
            catch
            {
                continue;
            }

            var dir = Path.Combine(AppPaths.Ticks, feed.Id);
            if (!Directory.Exists(dir)) continue;
            IReadOnlyList<Instrument> instruments;
            try
            {
                instruments = await InstrumentPicker.InstrumentsAsync(_svc, p.Id);
            }
            catch
            {
                instruments = InstrumentCatalog.Read(p.Id);
            }

            foreach (var symbolDir in Directory.EnumerateDirectories(dir))
            {
                string symbol = Path.GetFileName(symbolDir);
                var inst = instruments.FirstOrDefault(i => i.Symbol == symbol);
                if (inst == null) continue;
                var days = TickFile.AvailableDays(AppPaths.Ticks, feed.Id, symbol).ToList();
                if (days.Count >= minDays) res.Add((p.Id, inst, days));
            }
        }

        return res;
    }

    /// <summary>Abre un replay en un instrumento y momento al azar (de lo bajado en Datos).</summary>
    internal async Task OpenRandomReplayAsync(bool blind, int lengthDays = 3)
    {
        var candidates = await ReplayCandidatesAsync(Math.Max(2, lengthDays));
        if (candidates.Count == 0)
        {
            _status("Replay aleatorio: no hay instrumentos con suficientes días bajados (Datos → bajar al menos 3 días).");
            return;
        }

        var (_, inst, days) = candidates[_random.Next(candidates.Count)];
        // un inicio al azar que deje «lengthDays» días por delante (dentro de lo bajado)
        int maxStart = Math.Max(0, days.Count - lengthDays);
        var startDay = days[_random.Next(maxStart + 1)];
        var start = startDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddMinutes(_random.Next(0, 24 * 60));
        var last = days.Where(d => d >= startDay).Skip(lengthDays - 1).FirstOrDefault();
        if (last == default) last = days[^1];
        var end = last.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1);
        var period = _active?.Model?.Period ?? ChartPeriod.M5;
        await OpenReplayAsync(inst, start, end, period);
        if (_active?.Model == null || !_active.IsReplay) return;
        var tab = _active;
        var broker = _svc.SimBrokerFor(tab.Model!.Data);
        if (blind)
        {
            tab.Model.Settings.BlindReplay = true;
            tab.Model.Settings.BlindReplayStart = start;
            tab.Model.Invalidate();
            UpdateTitles(tab);
        }

        _replayRuns.Add(new ReplayRun(tab, start, broker.MyTrades.Count, _svc.Portfolio.RealizedPnL, blind));
        _status(blind
            ? "Replay a ciegas: instrumento y fecha ocultos. Operá con la cuenta simulada; al terminar (o con «Revelar») se muestra qué era y el resultado."
            : $"Replay aleatorio de {inst.Symbol} desde {TimeZoneInfo.ConvertTimeFromUtc(start, tab.Model.Settings.TimeZone):dd/MM HH:mm}.");
    }

    /// <summary>Descubre el instrumento y la fecha de un replay a ciegas y muestra el resultado hasta ahora.</summary>
    internal void RevealReplay(ChartTab tab)
    {
        var run = _replayRuns.FirstOrDefault(r => ReferenceEquals(r.Tab, tab));
        if (tab.Model == null) return;
        tab.Model.Settings.BlindReplay = false;
        tab.Model.Invalidate();
        UpdateTitles(tab);
        if (run != null) ReportReplay(run, finished: false);
    }

    private void ReportReplay(ReplayRun run, bool finished)
    {
        if (run.Tab.Model == null) return;
        var broker = _svc.SimBrokerFor(run.Tab.Model.Data);
        int trades = broker.MyTrades.Count - run.TradesBefore;
        decimal pnl = _svc.Portfolio.RealizedPnL - run.PnlBefore;
        var inst = (run.Tab.Model.Data.Feed as ReplayFeed)?.Source ?? run.Tab.Model.Instrument;
        string when = TimeZoneInfo.ConvertTimeFromUtc(run.StartUtc, run.Tab.Model.Settings.TimeZone).ToString("yyyy-MM-dd HH:mm");
        string text = finished
            ? $"Replay terminado · {inst.Symbol} desde {when} · {trades} ejecuciones · resultado {pnl:+0.00;-0.00}"
            : $"{inst.Symbol} desde {when} · {trades} ejecuciones · resultado hasta ahora {pnl:+0.00;-0.00}";
        _svc.Alerts.Raise(AlertKind.System, inst.Symbol, "Replay", text);
    }

    /// <summary>Al terminar un replay con registro, se informa el resultado (se llama desde el ciclo).</summary>
    private void TickReplayRuns()
    {
        for (int i = _replayRuns.Count - 1; i >= 0; i--)
        {
            var run = _replayRuns[i];
            if (run.Tab.Model?.Data.Feed is not ReplayFeed feed)
            {
                _replayRuns.RemoveAt(i);
                continue;
            }

            if (!feed.IsFinished) continue;
            if (run.Blind && run.Tab.Model.Settings.BlindReplay)
            {
                run.Tab.Model.Settings.BlindReplay = false;
                run.Tab.Model.Invalidate();
                UpdateTitles(run.Tab);
            }

            ReportReplay(run, finished: true);
            _replayRuns.RemoveAt(i);
        }
    }

    /// <summary>Botón «Revelar» de la barra de replay (solo a ciegas).</summary>
    private Button BuildRevealButton()
    {
        var b = Ui.Button("Revelar", MaterialIconKind.EyeOutline, () =>
        {
            if (_active != null) RevealReplay(_active);
        });
        b.IsVisible = false;
        return b;
    }
}
