using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TradingHub.Indicators;

/// <summary>
/// Resultados ya calculados de los indicadores (perfiles de sesiones cerradas, por ejemplo), guardados en disco para que
/// al volver a abrir Trading Hub, o después de actualizarla, no se vuelvan a calcular. Cada indicador los separa en
/// «cajones» (instrumento + ajustes) y los identifica con una clave que cambia si cambian las velas de origen.
///
/// Formato de cada cajón: registros [int clave][clave UTF-8][int largo][datos] uno tras otro; un final cortado (la app se
/// cerró a mitad de una escritura) se ignora.
/// </summary>
public static class ResultCache
{
    /// <summary>Carpeta de la caché (la fija la app al arrancar; null = solo en memoria).</summary>
    public static string? Root { get; set; }

    private const int MaxFileBytes = 512 << 20;
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte[]>> Loaded = new();
    private static readonly object WriteLock = new();

    private static string FileFor(string bucket)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bucket)))[..24].ToLowerInvariant();
        return Path.Combine(Root!, hash + ".cache");
    }

    private static ConcurrentDictionary<string, byte[]> Bucket(string bucket) => Loaded.GetOrAdd(bucket, b =>
    {
        var map = new ConcurrentDictionary<string, byte[]>();
        if (Root == null) return map;
        try
        {
            var path = FileFor(b);
            if (!File.Exists(path)) return map;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new BinaryReader(fs);
            while (fs.Position < fs.Length)
            {
                int kl = r.ReadInt32();
                if (kl <= 0 || kl > 1 << 16 || fs.Position + kl > fs.Length) break;
                var key = Encoding.UTF8.GetString(r.ReadBytes(kl));
                int dl = r.ReadInt32();
                if (dl < 0 || fs.Position + dl > fs.Length) break;
                map[key] = r.ReadBytes(dl);
            }
        }
        catch
        {
            // caché dañada: se recalcula
        }

        return map;
    });

    /// <summary>Olvida lo cargado en memoria (se vuelve a leer del disco): para pruebas.</summary>
    public static void ClearMemory() => Loaded.Clear();

    public static bool TryGet(string bucket, string key, out byte[] data)
    {
        if (Bucket(bucket).TryGetValue(key, out var d))
        {
            data = d;
            return true;
        }

        data = Array.Empty<byte>();
        return false;
    }

    public static void Put(string bucket, string key, byte[] data)
    {
        var map = Bucket(bucket);
        if (map.TryGetValue(key, out var old) && old.AsSpan().SequenceEqual(data)) return;
        map[key] = data;
        if (Root == null) return;
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(Root);
                var path = FileFor(bucket);
                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes) Rewrite(path, map);
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var w = new BinaryWriter(fs);
                Write(w, key, data);
            }
        }
        catch
        {
            // sin disco: queda en memoria
        }
    }

    private static void Write(BinaryWriter w, string key, byte[] data)
    {
        var kb = Encoding.UTF8.GetBytes(key);
        w.Write(kb.Length);
        w.Write(kb);
        w.Write(data.Length);
        w.Write(data);
    }

    /// <summary>Un cajón que creció demasiado se reescribe con la mitad más nueva (la vieja se recalcula si hace falta).</summary>
    private static void Rewrite(string path, ConcurrentDictionary<string, byte[]> map)
    {
        var keep = map.ToArray();
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            long bytes = 0;
            for (int i = keep.Length - 1; i >= 0 && bytes < MaxFileBytes / 2; i--)
            {
                Write(w, keep[i].Key, keep[i].Value);
                bytes += keep[i].Value.Length + keep[i].Key.Length + 8;
            }
        }

        File.Move(tmp, path, overwrite: true);
    }
}
