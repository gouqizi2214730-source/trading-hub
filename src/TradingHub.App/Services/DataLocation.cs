using System.Text.Json;

namespace TradingHub.App.Services;

/// <summary>
/// Dónde se guardan los datos de mercado (ticks grabados y bajados, y la caché de velas), que son lo que más ocupa. Por
/// defecto van en la carpeta de la app; se pueden llevar a otro disco desde Ajustes → Datos.
///
/// Mudarlos va en dos pasos para no perder nada de lo que se está grabando: primero se copia todo con la app abierta
/// (con progreso) y se anota la mudanza; al volver a abrir, antes de grabar nada, se copia lo que cambió mientras tanto
/// (lo del día), se verifica que esté todo y recién ahí se borra la copia vieja.
/// </summary>
public static class DataLocation
{
    private const string FileName = "carpeta-datos.json";
    private static readonly string[] Folders = { "Ticks", "Klines", "TicksCompact", "IndicatorCache" };

    private sealed class Config
    {
        public string? Folder { get; set; }

        /// <summary>Carpeta de la que se mudaron los datos y falta terminar (al abrir).</summary>
        public string? MoveFrom { get; set; }
    }

    /// <summary>
    /// Archivos que no se copian: el candado de grabación (lo tiene abierto en exclusiva la app que graba; copiarlo daba
    /// «el archivo está siendo usado por otro proceso» y la mudanza no avanzaba nunca) y temporales.
    /// </summary>
    internal static bool Skip(string file)
    {
        var name = Path.GetFileName(file);
        return name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".reparando", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".tradinghub-prueba", StringComparison.Ordinal);
    }

    private static IEnumerable<string> DataFiles(string dir) => Files(dir).Where(f => !Skip(f));

    private static string ConfigPath => Path.Combine(AppPaths.Root, FileName);

    private static Config? _config;

    private static Config Current
    {
        get
        {
            if (_config != null) return _config;
            try
            {
                _config = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new Config() : new Config();
            }
            catch
            {
                _config = new Config();
            }

            return _config;
        }
    }

    /// <summary>Carpeta elegida, o null si es la predeterminada.</summary>
    public static string? Custom => string.IsNullOrWhiteSpace(Current.Folder) ? null : Current.Folder;

    /// <summary>Carpeta base de los datos de mercado (adentro van Ticks y Klines).</summary>
    public static string Root => Custom ?? AppPaths.Root;

    /// <summary>Fija los datos de mercado en <paramref name="folder"/> (cuando la carpeta de la app se muda sin ellos).</summary>
    public static void Pin(string folder) => Save(new Config { Folder = Norm(folder), MoveFrom = Current.MoveFrom });

    private static void Save(Config c)
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true }));
        _config = c;
    }

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool Inside(string child, string parent) =>
        Norm(child).StartsWith(Norm(parent) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Por qué no se puede usar esa carpeta (null si se puede).</summary>
    public static string? Problem(string target)
    {
        if (Same(target, Root)) return "Los datos ya están en esa carpeta.";
        foreach (var f in Folders)
            if (Inside(target, Path.Combine(Root, f)) || Same(target, Path.Combine(Root, f)))
                return "Elegí una carpeta que no esté adentro de la actual.";
        if (Inside(Root, target) && !Same(target, AppPaths.Root)) return "Elegí una carpeta que no contenga a la actual.";
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

    /// <summary>Lo que ocupan los datos actuales.</summary>
    public static long CurrentSize() => Folders.Select(f => Path.Combine(Root, f)).Where(Directory.Exists)
        .Sum(d => DataFiles(d).Sum(f => SafeLength(f)));

    private static IEnumerable<string> Files(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch
        {
            return 0;
        }
    }

    public static long FreeSpace(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    /// <summary>
    /// Paso 1 (con la app abierta): copia los datos a <paramref name="target"/> y deja anotada la mudanza. Se termina al
    /// volver a abrir Trading Hub (<see cref="FinishPendingMove"/>).
    /// </summary>
    public static async Task MoveAsync(string target, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var from = Root;
        await Task.Run(() => CopyAll(from, target, progress, ct), ct).ConfigureAwait(false);
        bool isDefault = Same(target, AppPaths.Root);
        Save(new Config { Folder = isDefault ? null : Norm(target), MoveFrom = from });
    }

    /// <summary>Copia Ticks y Klines de una carpeta base a otra (aunque haya archivos abiertos grabándose).</summary>
    internal static void CopyAll(string from, string target, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        long total = Folders.Select(f => Path.Combine(from, f)).Where(Directory.Exists).Sum(d => DataFiles(d).Sum(SafeLength)), done = 0;
        foreach (var f in Folders)
        {
            var src = Path.Combine(from, f);
            if (!Directory.Exists(src)) continue;
            foreach (var file in DataFiles(src).ToList())
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(target, f, Path.GetRelativePath(src, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                try
                {
                    CopyShared(file, dest);
                }
                catch (IOException) when (File.Exists(file))
                {
                    // tomado en exclusiva por algo (otro programa, un antivirus): se copia al reiniciar, con la app cerrada
                }

                done += SafeLength(file);
                progress?.Report((done, total));
            }
        }
    }

    /// <summary>Copia aunque el archivo esté abierto para escribir (lo que se está grabando).</summary>
    private static void CopyShared(string file, string dest)
    {
        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }

    /// <summary>
    /// Paso 2 (al abrir, antes de grabar nada): copia lo que cambió desde el paso 1, verifica y borra la copia vieja.
    /// Si algo falla, la copia vieja queda (nada se pierde) y la mudanza se reintenta la próxima vez.
    /// </summary>
    public static string? FinishPendingMove()
    {
        var c = Current;
        if (string.IsNullOrWhiteSpace(c.MoveFrom)) return null;
        try
        {
            // al reiniciar, la instancia anterior puede seguir cerrándose con los archivos abiertos: se espera
            AppLocation.WaitForOtherInstances();
            if (!Same(c.MoveFrom!, Root)) Complete(c.MoveFrom!, Root);
            Save(new Config { Folder = c.Folder });
            return null;
        }
        catch (OperationCanceledException)
        {
            return null; // sigue abierta otra Trading Hub: se termina la próxima vez
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return ex.Message;
        }
    }

    /// <summary>Copia lo que falte o cambió, verifica que esté todo del otro lado y recién ahí borra el origen.</summary>
    internal static void Complete(string from, string to)
    {
        foreach (var f in Folders)
        {
            var src = Path.Combine(from, f);
            if (!Directory.Exists(src)) continue;
            foreach (var file in DataFiles(src).ToList())
            {
                var dest = Path.Combine(to, f, Path.GetRelativePath(src, file));
                var si = new FileInfo(file);
                var di = new FileInfo(dest);
                if (!di.Exists || di.Length != si.Length || di.LastWriteTimeUtc < si.LastWriteTimeUtc)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    CopyShared(file, dest);
                }
            }

            bool complete = DataFiles(src).All(file =>
            {
                var di = new FileInfo(Path.Combine(to, f, Path.GetRelativePath(src, file)));
                return di.Exists && di.Length == SafeLength(file);
            });
            if (!complete) throw new IOException($"La copia de {f} no quedó completa; la carpeta vieja queda como estaba.");
        }

        // todo está del otro lado: lo viejo se borra lo que se pueda (si algo quedó tomado, queda ahí sin molestar: la
        // mudanza ya está hecha y no se reintenta para siempre)
        foreach (var f in Folders)
        {
            var src = Path.Combine(from, f);
            if (!Directory.Exists(src)) continue;
            try
            {
                Directory.Delete(src, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (var file in Files(src).ToList())
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // tomado: queda
                    }
                }

                foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(src))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // queda
                    }
                }
            }
        }
    }
}
