using Avalonia.Controls;
using Avalonia.Threading;
using TradingHub.App.Windows;

namespace TradingHub.App.Services;

/// <summary>
/// Lo que ve la persona de las actualizaciones: la búsqueda sola al abrir (con aviso en la barra de estado y una
/// pregunta si hay instalador nuevo) y la instalación (bajar, revisar y abrir el instalador).
/// </summary>
internal static class UpdateUi
{
    /// <summary>El último resultado (lo muestra Ajustes).</summary>
    public static UpdateChecker.Result? Last { get; private set; }

    public static event Action<UpdateChecker.Result>? Checked;

    private static int _busy;

    /// <summary>Busca ahora y guarda la hora. Devuelve null si ya había una búsqueda en curso.</summary>
    public static async Task<UpdateChecker.Result?> CheckAsync(AppSettings settings)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return null;
        try
        {
            var r = await UpdateChecker.CheckAsync().ConfigureAwait(false);
            Last = r;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (r.Error == null)
                {
                    settings.LastUpdateCheck = DateTime.UtcNow;
                    settings.Save();
                }

                Checked?.Invoke(r);
            });
            return r;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Al abrir la app: si toca (activado y pasaron 12 h), busca y avisa.</summary>
    public static async Task CheckAtStartupAsync(MainWindow owner, AppSettings settings, Action<string> status)
    {
        if (!settings.CheckUpdates) return;
        if (settings.LastUpdateCheck is { } last && DateTime.UtcNow - last < UpdateChecker.Interval) return;
        var r = await CheckAsync(settings);
        if (r == null) return;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (r.Update is { } u)
            {
                status($"Hay una versión nueva de Trading Hub: {u.Version} (Ajustes → Actualizaciones).");
                if (settings.SkippedUpdate == u.Tag) return;
                await OfferAsync(owner, u, settings, status);
            }
            else if (r.UpstreamNewer is { } up)
            {
                status($"El proyecto original publicó la versión {up.Version}; tu repositorio la empaqueta solo en unas horas.");
            }
        });
    }

    /// <summary>Pregunta si instalar la versión nueva.</summary>
    public static async Task OfferAsync(Window owner, UpdateChecker.Release u, AppSettings settings, Action<string> status)
    {
        if (!OperatingSystem.IsWindows() || u.InstallerUrl == null)
        {
            bool open = await ConfirmDialog.ShowAsync(owner, "Versión nueva de Trading Hub",
                $"Está la versión {u.Version} (tenés la {UpdateChecker.Current}).\n\nSe baja desde la página de la versión en GitHub.", "Abrir la página");
            if (open) Shell.OpenUrl(u.PageUrl);
            return;
        }

        bool install = await ConfirmDialog.ShowAsync(owner, "Versión nueva de Trading Hub",
            $"Está la versión {u.Version} (tenés la {UpdateChecker.Current}).\n\n" +
            "Se baja el instalador y se abre. Tus conexiones, gráficos, estrategias e indicadores quedan como están. " +
            "Si tenés operaciones o estrategias en vivo, dejalas como quieras antes de cerrar Trading Hub.",
            "Bajar e instalar");
        if (!install)
        {
            settings.SkippedUpdate = u.Tag;
            settings.Save();
            status("Actualización pospuesta: está en Ajustes → Actualizaciones cuando quieras.");
            return;
        }

        await InstallAsync(u, status);
    }

    /// <summary>Baja el instalador, lo revisa y lo abre.</summary>
    public static async Task<bool> InstallAsync(UpdateChecker.Release u, Action<string> status)
    {
        try
        {
            int lastPct = -1;
            var progress = new Progress<double>(p =>
            {
                int pct = (int)(p * 100);
                if (pct == lastPct || pct % 5 != 0) return;
                lastPct = pct;
                status($"Bajando Trading Hub {u.Version}… {pct} %");
            });
            var path = await UpdateChecker.DownloadAsync(u, progress);
            status("Instalador listo: cerrá Trading Hub cuando te lo pida y se actualiza.");
            UpdateChecker.RunInstaller(path);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            status($"No se pudo actualizar: {ex.Message}");
            return false;
        }
    }
}
