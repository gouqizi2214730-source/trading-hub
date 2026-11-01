using System.Diagnostics;
using System.Text.Json;
using TradingHub.Common;

namespace TradingHub.App.Services;

/// <summary>
/// Llevar a otra carpeta (otro disco) la carpeta de la app (ajustes, conexiones, espacio de trabajo, cuentas, backtests…)
/// y las de indicadores y estrategias propios.
///
/// La de la app se muda al volver a abrir, antes de que nada la use: se anota en <see cref="Product.LocationFile"/> (que
/// queda siempre en la ubicación de siempre), se reinicia, se copia todo, se verifica y recién ahí se borra lo viejo. Los
/// datos de mercado no se mueven con ella (se mueven aparte, en «Carpeta de los datos de mercado»): si estaban adentro,
/// quedan donde están. Indicadores y estrategias son pocos archivos: se copian en el momento y se reinicia.
/// </summary>
public static class AppLocation
{
    private const string ScriptsFile = "carpetas.json";
    private static readonly string[] MarketFolders = { "Ticks", "Klines", "TicksCompact" };

    private sealed class Pointer
    {
        public string? Root { get; set; }
        public string? MoveFrom { get; set; }
    }

    public sealed class ScriptFolders
    {
        public string? Indicators { get; set; }
        public string? Strategies { get; set; }
    }

    private static bool Testing => Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 };

    private static StringComparison Cmp => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), Cmp);

    private static bool Inside(string child, string parent) => Norm(child).StartsWith(Norm(parent) + Path.DirectorySeparatorChar, Cmp);

    private static Pointer ReadPointer()
    {
        try
        {
            return File.Exists(Product.LocationFile) ? JsonSerializer.Deserialize<Pointer>(File.ReadAllText(Product.LocationFile)) ?? new Pointer() : new Pointer();
        }
        catch
        {
            return new Pointer();
        }
    }

    private static void WritePointer(Pointer p)
    {
        Directory.CreateDirectory(Product.DefaultAppData);
        File.WriteAllText(Product.LocationFile, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>La carpeta de la app se movió (no está en la ubicación de siempre).</summary>
    public static bool IsCustom => !Same(AppPaths.Root, Product.DefaultAppData);

    // ===================================================================== indicadores y estrategias

    /// <summary>Carpetas elegidas para indicadores y estrategias (null = las de siempre).</summary>
    public static ScriptFolders Scripts => _scripts ??= LoadScripts();

    // se lee la primera vez que se pide (no al cargar la clase): AppPaths la usa y esta usa AppPaths; con inicializadores
    // estáticos el orden de arranque dejaba una de las dos a medio hacer y la app se cerraba antes de abrir la ventana
    private static ScriptFolders? _scripts;

    private static ScriptFolders LoadScripts()
    {
        try
        {
            var path = Path.Combine(AppPaths.Root, ScriptsFile);
            if (File.Exists(path)) return JsonSerializer.Deserialize<ScriptFolders>(File.ReadAllText(path)) ?? new ScriptFolders();
        }
        catch
        {
            // archivo dañado: las de siempre
        }

        return new ScriptFolders();
    }

    private static void SaveScripts(ScriptFolders s)
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(Path.Combine(AppPaths.Root, ScriptsFile), JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Por qué no se puede usar <paramref name="target"/> en lugar de <paramref name="current"/> (null si se puede).</summary>
    public static string? Problem(string current, string target)
    {
        if (Same(target, current)) return "Ya está en esa carpeta.";
        if (Inside(target, current)) return "Elegí una carpeta que no esté adentro de la actual.";
        if (Inside(current, target) && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any(e => !Same(e, current)))
            return "Elegí una carpeta vacía o que no contenga a la actual.";
        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, ".tradinghub-prueba");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return $"No se puede escribir en esa carpeta: {ex.Message}";
        }

        return null;
    }

    /// <summary>Mueve los indicadores o las estrategias propios a <paramref name="target"/> (después hay que reiniciar).</summary>
    public static void MoveScripts(bool indicators, string target)
    {
        string current = indicators ? AppPaths.UserIndicators : AppPaths.Strategies;
        if (Directory.Exists(current))
        {
            CopyTree(current, target, _ => true);
            VerifyAndDelete(current, target, _ => true);
        }

        bool isDefault = Same(target, Path.Combine(Product.Documents, indicators ? "Indicadores" : "Estrategias"));
        var s = new ScriptFolders { Indicators = Scripts.Indicators, Strategies = Scripts.Strategies };
        if (indicators) s.Indicators = isDefault ? null : Norm(target);
        else s.Strategies = isDefault ? null : Norm(target);
        SaveScripts(s);
    }

    // ===================================================================== carpeta de la app

    /// <summary>
    /// Anota la mudanza de la carpeta de la app a <paramref name="target"/> (se hace al reiniciar). Si los datos de
    /// mercado estaban adentro, quedan fijos donde están.
    /// </summary>
    public static void RequestRootMove(string target)
    {
        var from = AppPaths.Root;
        if (DataLocation.Custom == null) DataLocation.Pin(from);
        Directory.CreateDirectory(target);
        bool isDefault = Same(target, Product.DefaultAppData);
        WritePointer(new Pointer { Root = isDefault ? null : Norm(target), MoveFrom = from });
    }

    /// <summary>
    /// Al abrir, antes de que nada use la carpeta: termina una mudanza pendiente. Si algo falla, lo viejo queda como
    /// estaba y la app sigue usando la carpeta vieja (se reintenta la próxima vez).
    /// </summary>
    /// <summary>Si al abrir no se pudo mudar la carpeta de la app (sigue en la vieja, completa).</summary>
    public static string? MoveError { get; private set; }

    public static string? FinishPendingMove() => MoveError = DoFinishPendingMove();

    private static string? DoFinishPendingMove()
    {
        if (Testing) return null;
        var p = ReadPointer();
        if (string.IsNullOrWhiteSpace(p.MoveFrom)) return null;
        string from = p.MoveFrom!, to = string.IsNullOrWhiteSpace(p.Root) ? Product.DefaultAppData : p.Root!;
        try
        {
            WaitForOtherInstances();
            if (!Same(from, to) && Directory.Exists(from))
            {
                bool Include(string rel) => !MarketFolders.Any(m => rel.Equals(m, Cmp) || rel.StartsWith(m + Path.DirectorySeparatorChar, Cmp))
                                            && !rel.Equals(Path.GetFileName(Product.LocationFile), Cmp);
                CopyTree(from, to, Include);
                VerifyAndDelete(from, to, Include);
            }

            WritePointer(new Pointer { Root = p.Root });
            return null;
        }
        catch (OperationCanceledException)
        {
            return null; // queda pendiente
        }
        catch (Exception ex)
        {
            // vuelve a la carpeta vieja (completa) y se reintenta al abrir otra vez
            try
            {
                WritePointer(new Pointer { Root = Same(from, Product.DefaultAppData) ? null : from, MoveFrom = null });
            }
            catch
            {
                // nada que hacer
            }

            return ex.Message;
        }
    }

    /// <summary>Al reiniciar, la instancia anterior todavía puede estar guardando: se espera que termine.</summary>
    internal static void WaitForOtherInstances()
    {
        var me = Environment.ProcessId;
        var name = Process.GetCurrentProcess().ProcessName;
        for (int i = 0; i < 100; i++)
        {
            bool other = Process.GetProcessesByName(name).Any(x => x.Id != me);
            if (!other) return;
            Thread.Sleep(100);
        }

        // sigue abierta otra Trading Hub: no se toca nada (se muda la próxima vez que se abra sola)
        throw new OperationCanceledException("Hay otra Trading Hub abierta");
    }

    internal static void CopyTree(string from, string to, Func<string, bool> include)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, file);
            if (!include(rel)) continue;
            var dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
        }
    }

    /// <summary>Verifica que cada archivo esté del otro lado con el mismo tamaño y recién ahí borra el origen.</summary>
    internal static void VerifyAndDelete(string from, string to, Func<string, bool> include)
    {
        var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Where(f => include(Path.GetRelativePath(from, f))).ToList();
        foreach (var file in files)
        {
            var dest = new FileInfo(Path.Combine(to, Path.GetRelativePath(from, file)));
            if (!dest.Exists || dest.Length != new FileInfo(file).Length)
                throw new IOException($"La copia de {Path.GetRelativePath(from, file)} no quedó completa; lo viejo queda como estaba.");
        }

        foreach (var file in files) File.Delete(file);
        // carpetas que quedaron vacías
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
    }
}
