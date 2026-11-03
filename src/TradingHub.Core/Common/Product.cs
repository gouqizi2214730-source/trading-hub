using System.Text.Json;

namespace TradingHub.Common;

/// <summary>El nombre de la app y sus carpetas.</summary>
public static class Product
{
    /// <summary>Nombre de esta edición (la copia KVP del proyecto Trading Hub).</summary>
    public const string Name = "KVP";

    /// <summary>Nombre del proyecto original: los textos del código lo nombran y se cambian por <see cref="Name"/> al mostrarlos.</summary>
    public const string UpstreamName = "Trading Hub";

    /// <summary>El texto con el nombre de esta edición en lugar del original («Abrir Trading Hub» → «Abrir KVP»).</summary>
    public static string Brand(string text) => text.Contains(UpstreamName, StringComparison.Ordinal) ? text.Replace(UpstreamName, Name, StringComparison.Ordinal) : text;

    /// <summary>
    /// Las carpetas de la edición anterior (%APPDATA%\Trading Hub, Documentos\Trading Hub) pasan a llamarse como esta
    /// (una sola vez, al abrir, antes de usarlas): ajustes, conexiones, datos e indicadores siguen donde estaban.
    /// </summary>
    public static void MigrateFolders()
    {
        if (Name == UpstreamName) return;
        foreach (var baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
        {
            try
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                string old = Path.Combine(baseDir, UpstreamName), now = Path.Combine(baseDir, Name);
                if (Directory.Exists(old) && !Directory.Exists(now)) Directory.Move(old, now);
            }
            catch
            {
                // sin permisos o en uso: se sigue con lo que haya (la carpeta nueva se crea vacía)
            }
        }
    }

    /// <summary>La carpeta de la app dentro de <paramref name="baseDir"/>.</summary>
    public static string Folder(string baseDir) => Path.Combine(baseDir, Name);

    /// <summary>Dónde va la carpeta de la app si no se movió (~/Library/Application Support/Trading Hub, %APPDATA%\Trading Hub).</summary>
    public static string DefaultAppData => Folder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>Archivo que anota a qué carpeta se movió la de la app (queda siempre en la ubicación de siempre).</summary>
    public static string LocationFile => Path.Combine(DefaultAppData, "ubicacion.json");

    private static string? _appData;

    /// <summary>Datos de la app: la de siempre o la que se eligió en Ajustes → Datos (anotada en <see cref="LocationFile"/>).</summary>
    public static string AppData => _appData ??= ResolveAppData();

    private static string ResolveAppData()
    {
        try
        {
            if (File.Exists(LocationFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(LocationFile));
                if (doc.RootElement.TryGetProperty("Root", out var r) && r.GetString() is { Length: > 0 } root && Directory.Exists(root)) return root;
            }
        }
        catch
        {
            // archivo dañado: la de siempre
        }

        return DefaultAppData;
    }

    /// <summary>Carpeta visible del usuario (~/Documents/Trading Hub): estrategias e indicadores propios.</summary>
    public static string Documents => Folder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
}
