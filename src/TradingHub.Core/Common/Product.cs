using System.Text.Json;

namespace TradingHub.Common;

/// <summary>El nombre de la app y sus carpetas.</summary>
public static class Product
{
    public const string Name = "Trading Hub";

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
