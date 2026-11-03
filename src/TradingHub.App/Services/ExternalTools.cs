using System.Diagnostics;

namespace TradingHub.App.Services;

/// <summary>
/// Herramientas externas que complementan a la app: VPFM (los módulos web de heatmap, aggr, Liquidity Mapper,
/// derivados y GEX del mismo autor), que corre en su propia ventana con su propio servidor local.
/// </summary>
public static class ExternalTools
{
    public const string VpfmReleases = "https://github.com/gouqizi2214730-source/flowmap/releases/latest";

    /// <summary>Ruta de VPFM.exe si está instalado (instalación por usuario de Tauri/NSIS o por máquina).</summary>
    public static string? FindVpfm()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var candidate in new[]
                 {
                     Path.Combine(local, "VPFM", "VPFM.exe"),
                     Path.Combine(local, "Programs", "VPFM", "VPFM.exe"),
                     Path.Combine(programs, "VPFM", "VPFM.exe"),
                     Path.Combine(local, "FlowMap", "FlowMap.exe"),
                 })
            if (File.Exists(candidate))
                return candidate;
        return null;
    }

    public static void Launch(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
        }
        catch (Exception)
        {
            // si no abre, el menú ofrece la descarga la próxima vez
        }
    }
}
