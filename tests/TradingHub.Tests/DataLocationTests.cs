using TradingHub.App.Services;

namespace TradingHub.Tests;

public class DataLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "th-dataloc-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Write(string rel, string text)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void Mudar_copia_todo_completa_lo_nuevo_y_recien_ahi_borra_el_origen()
    {
        string from = Path.Combine(_root, "viejo"), to = Path.Combine(_root, "nuevo");
        Write("viejo/Ticks/bybit-linear/BTCUSDT/2026-10-30.ticks", "dia completo");
        var today = Write("viejo/Ticks/bybit-linear/BTCUSDT/2026-11-01.ticks", "hoy");
        Write("viejo/Klines/bybit-linear/BTCUSDT/2026-09.bin", "velas");
        Write("viejo/settings.json", "no se toca");

        var reports = new List<(long, long)>();
        DataLocation.CopyAll(from, to, new SyncProgress(reports), CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(to, "Ticks/bybit-linear/BTCUSDT/2026-10-30.ticks")));
        Assert.True(File.Exists(Path.Combine(to, "Klines/bybit-linear/BTCUSDT/2026-09.bin")));
        Assert.False(File.Exists(Path.Combine(to, "settings.json")));
        Assert.Equal(reports[^1].Item1, reports[^1].Item2);
        Assert.True(Directory.Exists(Path.Combine(from, "Ticks"))); // el origen sigue hasta terminar

        // mientras tanto se siguió grabando hoy
        File.AppendAllText(today, " y mas operaciones");
        File.SetLastWriteTimeUtc(today, DateTime.UtcNow.AddMinutes(1));
        DataLocation.Complete(from, to);
        Assert.Equal("hoy y mas operaciones", File.ReadAllText(Path.Combine(to, "Ticks/bybit-linear/BTCUSDT/2026-11-01.ticks")));
        Assert.False(Directory.Exists(Path.Combine(from, "Ticks")));
        Assert.False(Directory.Exists(Path.Combine(from, "Klines")));
        Assert.True(File.Exists(Path.Combine(from, "settings.json")));
    }

    private sealed class SyncProgress : IProgress<(long, long)>
    {
        private readonly List<(long, long)> _list;
        public SyncProgress(List<(long, long)> list) => _list = list;
        public void Report((long, long) value) => _list.Add(value);
    }
}
