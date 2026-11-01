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

    [Fact]
    public void Mudar_con_la_app_grabando_no_choca_con_el_candado_ni_los_archivos_abiertos()
    {
        var from = Path.Combine(_root, "viejo");
        var to = Path.Combine(_root, "nuevo");
        Write("viejo/Ticks/bybit-linear/BTCUSDT/2026-10-01.ticks", "dia viejo");
        var today = Write("viejo/Ticks/bybit-linear/BTCUSDT/2026-11-02.ticks", "hoy");
        // lo que tiene abierto la app mientras graba: el candado en exclusiva y el archivo del día para escribir
        using (var lockFile = TradingHub.Data.TickFile.TryLock(Path.Combine(from, "Ticks"), "bybit-linear", "BTCUSDT"))
        using (var writing = new FileStream(today, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            Assert.NotNull(lockFile);
            DataLocation.CopyAll(from, to, null, CancellationToken.None); // antes: «está siendo usado por otro proceso»
            writing.Write("+mas"u8);
        }

        Assert.False(File.Exists(Path.Combine(to, "Ticks", "bybit-linear", "BTCUSDT", ".grabando.lock")));
        // al reiniciar (ya sin la app vieja): completa lo grabado mientras tanto y borra lo viejo
        DataLocation.Complete(from, to);
        Assert.Equal("hoy+mas", File.ReadAllText(Path.Combine(to, "Ticks", "bybit-linear", "BTCUSDT", "2026-11-02.ticks")));
        Assert.Equal("dia viejo", File.ReadAllText(Path.Combine(to, "Ticks", "bybit-linear", "BTCUSDT", "2026-10-01.ticks")));
        Assert.False(Directory.Exists(Path.Combine(from, "Ticks")));
    }

    [Fact]
    public void Mudar_la_carpeta_de_la_app_deja_los_datos_de_mercado_y_borra_solo_lo_copiado()
    {
        string from = Path.Combine(_root, "C"), to = Path.Combine(_root, "D", "Trading Hub");
        Write("C/settings.json", "{}");
        Write("C/connections.json", "[]");
        Write("C/Live/runners.json", "x");
        Write("C/Ticks/bybit-linear/BTCUSDT/2026-10-30.vtk", "ticks");
        Write("C/ubicacion.json", "puntero");
        bool Include(string rel) => !rel.StartsWith("Ticks") && rel != "ubicacion.json";
        AppLocation.CopyTree(from, to, Include);
        AppLocation.VerifyAndDelete(from, to, Include);
        Assert.True(File.Exists(Path.Combine(to, "settings.json")));
        Assert.True(File.Exists(Path.Combine(to, "Live", "runners.json")));
        Assert.False(File.Exists(Path.Combine(to, "Ticks", "bybit-linear", "BTCUSDT", "2026-10-30.vtk")));
        Assert.False(File.Exists(Path.Combine(from, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(from, "Live")));
        Assert.True(File.Exists(Path.Combine(from, "Ticks", "bybit-linear", "BTCUSDT", "2026-10-30.vtk")));
        Assert.True(File.Exists(Path.Combine(from, "ubicacion.json")));

        Assert.NotNull(AppLocation.Problem(to, Path.Combine(to, "sub")));
        Assert.NotNull(AppLocation.Problem(to, to));
        Assert.Null(AppLocation.Problem(to, Path.Combine(_root, "E")));
    }

    private sealed class SyncProgress : IProgress<(long, long)>
    {
        private readonly List<(long, long)> _list;
        public SyncProgress(List<(long, long)> list) => _list = list;
        public void Report((long, long) value) => _list.Add(value);
    }
}
