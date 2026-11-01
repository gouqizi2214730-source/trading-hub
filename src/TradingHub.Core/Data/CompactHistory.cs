using System.IO.Compression;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Historia resumida para ver meses o años de ticks guardados sin llenar la memoria.
///
/// Los días con ticks en disco que quedan fuera de la ventana de ticks (Ajustes → Datos → Footprint en la historia) se
/// cargan resumidos en tramos de N minutos (1, 5, 15 o 60; Ajustes → Datos): en cada tramo, la primera y la última
/// operación tal cual (apertura y cierre exactos), la más alta y la más baja (máximo y mínimo exactos) y el resto juntado
/// por lado y por fila de precio de ~1 punto básico (BTC a 80.000: filas de 0,8). Así las velas de N minutos o más
/// conservan su OHLC, su volumen y su delta exactos y un footprint y perfil fieles (con filas de ese tamaño). Un día real
/// de BTCUSDT en Bybit: 2,38 millones de operaciones → ~180 mil por minuto o ~43 mil por hora.
///
/// Las operaciones resumidas tienen ids negativos por encima de <see cref="CompressedIdBase"/>: cuentan como reales para
/// las velas (hay footprint) pero no como operaciones sueltas (Big Trades las ignora). Cada día se resume una sola vez y
/// se guarda en caché; se vuelve a armar si el archivo del día cambia.
/// </summary>
public static class CompactHistory
{
    /// <summary>Los ids de las operaciones resumidas van de acá hasta 0 (las armadas con velas, por debajo).</summary>
    public const long CompressedIdBase = long.MinValue / 4;

    private const int Version = 2;

    /// <summary>Tramos posibles (minutos).</summary>
    public static readonly int[] Granularities = { 1, 5, 15, 60 };
    private const int Magic = 0x48434854; // "THCH"

    public static bool IsCompressed(long id) => id >= CompressedIdBase && id < 0;

    public static string CachePath(string root, string feedId, string symbol, DateOnly day, int minutes = 60) =>
        Path.ChangeExtension(TickFile.DayPath(root, feedId, symbol, day), $".m{minutes}.thc");

    /// <summary>Alto de las filas de precio en que se junta (múltiplo del tick, ~1 punto básico del precio).</summary>
    public static decimal BucketFor(decimal price, decimal tick)
    {
        if (tick <= 0) return 0;
        long k = (long)Math.Floor(price * 0.00001m / tick);
        return tick * Math.Max(1, k);
    }

    /// <summary>Resume un día (ordenado por tiempo). Devuelve operaciones con id 0 (los ids se asignan al cargar).</summary>
    public static List<Trade> Compress(IReadOnlyList<Trade> trades, decimal tick, int minutes = 60)
    {
        long span = TimeSpan.TicksPerMinute * Math.Max(1, minutes);
        var res = new List<Trade>(Math.Min(trades.Count, 1 << 16));
        if (trades.Count == 0) return res;
        decimal bucket = BucketFor(trades[0].Price, tick);
        var agg = new Dictionary<(decimal Row, TradeSide Side), (decimal Vol, decimal PxVol)>();
        int i = 0;
        while (i < trades.Count)
        {
            long slot = trades[i].Time.Ticks / span;
            int start = i;
            while (i < trades.Count && trades[i].Time.Ticks / span == slot) i++;
            int end = i - 1;
            int hi = start, lo = start;
            for (int k = start; k <= end; k++)
            {
                if (trades[k].Price > trades[hi].Price) hi = k;
                if (trades[k].Price < trades[lo].Price) lo = k;
            }

            var first = trades[start];
            res.Add(new Trade(0, first.Time, first.Price, first.Volume, first.Side));
            if (end == start) continue;
            // máximo y mínimo tal cual (si no son la apertura o el cierre)
            if (hi != start && hi != end) res.Add(new Trade(0, first.Time, trades[hi].Price, trades[hi].Volume, trades[hi].Side));
            if (lo != start && lo != end && lo != hi) res.Add(new Trade(0, first.Time, trades[lo].Price, trades[lo].Volume, trades[lo].Side));

            agg.Clear();
            for (int k = start + 1; k < end; k++)
            {
                if (k == hi || k == lo) continue;
                var t = trades[k];
                decimal row = bucket > 0 ? Math.Floor(t.Price / bucket) * bucket : t.Price;
                agg.TryGetValue((row, t.Side), out var a);
                agg[(row, t.Side)] = (a.Vol + t.Volume, a.PxVol + t.Price * t.Volume);
            }

            foreach (var ((_, side), (vol, pxVol)) in agg.OrderBy(kv => kv.Key.Row).ThenBy(kv => kv.Key.Side))
            {
                if (vol <= 0) continue;
                // el precio promedio de la fila, en un tick (queda dentro de la fila y del rango de la hora)
                decimal px = tick > 0 ? Math.Round(pxVol / vol / tick) * tick : pxVol / vol;
                res.Add(new Trade(0, first.Time, px, vol, side));
            }

            var last = trades[end];
            res.Add(new Trade(0, last.Time, last.Price, last.Volume, last.Side));
        }

        return res;
    }

    /// <summary>
    /// El día resumido, desde la caché si está al día con el archivo de ticks (si no, se arma y se guarda). Los ids se
    /// asignan crecientes con el tiempo a partir de <paramref name="lastId"/>.
    /// </summary>
    public static List<Trade> LoadDay(string ticksRoot, string cacheRoot, Instrument inst, DateOnly day, ref long lastId, int minutes = 60)
    {
        var source = TickFile.DayPath(ticksRoot, inst.FeedId, inst.Symbol, day);
        var cache = CachePath(cacheRoot, inst.FeedId, inst.Symbol, day, minutes);
        var list = TryReadCache(cache, source) ?? Build(source, cache, inst.TickSize, minutes);
        var res = new List<Trade>(list.Count);
        foreach (var t in list) res.Add(new Trade(NextId(t.Time, ref lastId), t.Time, t.Price, t.Volume, t.Side));
        return res;
    }

    /// <summary>Arma la caché de un día si falta o quedó vieja (se puede llamar en paralelo para días distintos).</summary>
    public static void EnsureCached(string ticksRoot, string cacheRoot, Instrument inst, DateOnly day, int minutes = 60)
    {
        var source = TickFile.DayPath(ticksRoot, inst.FeedId, inst.Symbol, day);
        var cache = CachePath(cacheRoot, inst.FeedId, inst.Symbol, day, minutes);
        if (!IsFresh(cache, source)) Build(source, cache, inst.TickSize, minutes);
    }

    public static bool IsFresh(string cache, string source)
    {
        try
        {
            if (!File.Exists(cache) || !File.Exists(source)) return false;
            using var fs = File.OpenRead(cache);
            using var r = new BinaryReader(fs);
            var src = new FileInfo(source);
            return r.ReadInt32() == Magic && r.ReadInt32() == Version && r.ReadInt64() == src.Length && r.ReadInt64() == src.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return false;
        }
    }

    private static long NextId(DateTime t, ref long lastId)
    {
        long id = CompressedIdBase + (t.Ticks - DateTime.UnixEpoch.Ticks) / 10 * 8;
        if (id <= lastId) id = lastId + 1;
        lastId = id;
        return id;
    }

    private static List<Trade> Build(string source, string cache, decimal tick, int minutes)
    {
        var src = new FileInfo(source);
        long len = src.Length, mtime = src.LastWriteTimeUtc.Ticks;
        var raw = TickFile.SortAndDedupe(TickFile.Read(source));
        var list = Compress(raw, tick, minutes);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            var tmp = cache + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(len);
                w.Write(mtime);
                w.Write(list.Count);
                using var gz = new GZipStream(fs, CompressionLevel.Fastest, leaveOpen: true);
                using var bw = new BinaryWriter(gz);
                foreach (var t in list)
                {
                    bw.Write(t.Time.Ticks);
                    bw.Write(t.Price);
                    bw.Write(t.Volume);
                    bw.Write((byte)t.Side);
                }
            }

            File.Move(tmp, cache, overwrite: true);
        }
        catch
        {
            // sin caché igual se usa lo resumido (la próxima vez se vuelve a armar)
        }

        return list;
    }

    private static List<Trade>? TryReadCache(string cache, string source)
    {
        try
        {
            if (!File.Exists(cache) || !File.Exists(source)) return null;
            var src = new FileInfo(source);
            using var fs = File.OpenRead(cache);
            using var r = new BinaryReader(fs);
            if (r.ReadInt32() != Magic || r.ReadInt32() != Version || r.ReadInt64() != src.Length || r.ReadInt64() != src.LastWriteTimeUtc.Ticks) return null;
            int n = r.ReadInt32();
            var list = new List<Trade>(n);
            using var gz = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true);
            using var br = new BinaryReader(gz);
            for (int i = 0; i < n; i++)
            {
                long ticks = br.ReadInt64();
                decimal price = br.ReadDecimal();
                decimal vol = br.ReadDecimal();
                var side = (TradeSide)br.ReadByte();
                list.Add(new Trade(0, new DateTime(ticks, DateTimeKind.Utc), price, vol, side));
            }

            return list;
        }
        catch
        {
            return null;
        }
    }
}
