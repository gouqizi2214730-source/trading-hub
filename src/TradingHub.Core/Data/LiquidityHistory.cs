using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Una foto del libro de órdenes: los mejores niveles de cada lado, guardados como desplazamiento (en ticks) desde el
/// mejor precio y tamaño. Compacta: miles por hora caben en memoria y en disco.
/// </summary>
public sealed class DepthSample
{
    public DepthSample(DateTime time, long bestBid, long bestAsk, int[] bidOffsets, float[] bidSizes, int[] askOffsets, float[] askSizes, int bucket = 1)
    {
        Time = time;
        BestBid = bestBid;
        BestAsk = bestAsk;
        BidOffsets = bidOffsets;
        BidSizes = bidSizes;
        AskOffsets = askOffsets;
        AskSizes = askSizes;
        Bucket = Math.Max(1, bucket);
    }

    public DateTime Time { get; }
    public long BestBid { get; }
    public long BestAsk { get; }

    /// <summary>
    /// Ticks que abarca cada nivel guardado: 1 = al tick; más cuando el libro era muy profundo y se resumió (cada nivel
    /// suma los órdenes de <see cref="Bucket"/> ticks: el bid hacia abajo desde su precio, el ask hacia arriba).
    /// </summary>
    public int Bucket { get; }

    /// <summary>Niveles del bid: precio = BestBid − desplazamiento.</summary>
    public int[] BidOffsets { get; }
    public float[] BidSizes { get; }

    /// <summary>Niveles del ask: precio = BestAsk + desplazamiento.</summary>
    public int[] AskOffsets { get; }
    public float[] AskSizes { get; }

    public long BidPrice(int i) => BestBid - BidOffsets[i];
    public long AskPrice(int i) => BestAsk + AskOffsets[i];

    /// <summary>Liquidez total de cada lado hasta <paramref name="withinTicks"/> ticks del mejor precio.</summary>
    public (double Bid, double Ask) Within(int withinTicks)
    {
        double b = 0, a = 0;
        for (int i = 0; i < BidOffsets.Length; i++)
            if (BidOffsets[i] <= withinTicks) b += BidSizes[i];
        for (int i = 0; i < AskOffsets.Length; i++)
            if (AskOffsets[i] <= withinTicks) a += AskSizes[i];
        return (b, a);
    }
}

/// <summary>
/// Historia del libro de órdenes de un instrumento: una foto cada pocos segundos mientras el gráfico está abierto, en
/// memoria (las últimas horas) y en disco (un archivo por día), para el mapa de calor de liquidez y los indicadores
/// de delta del libro. Las fotos están en orden de tiempo.
/// </summary>
public sealed class LiquidityHistory
{
    /// <summary>Carpeta raíz ({raíz}/Liquidez/{feed}/{símbolo}/{día}.liq); null = no se guarda en disco.</summary>
    public static string? Root { get; set; }

    public static bool Enabled { get; set; } = true;

    /// <summary>Niveles guardados por lado; un libro más profundo se resume en tramos de varios ticks.</summary>
    public static int LevelsPerSide { get; set; } = 100;
    public static int SampleSeconds { get; set; } = 3;

    /// <summary>Horas que se mantienen en memoria.</summary>
    public static int KeepHours { get; set; } = 12;

    private readonly List<DepthSample> _samples = new();
    private readonly object _lock = new();
    private readonly Instrument _instrument;
    private DateTime _lastSample = DateTime.MinValue;
    private DateTime _fileDay = DateTime.MinValue;
    private FileStream? _file;
    private BinaryWriter? _writer;
    private long _generation;

    public LiquidityHistory(Instrument instrument) => _instrument = instrument;

    /// <summary>Cambia cada vez que entra o se carga algo (para que el gráfico vuelva a dibujar).</summary>
    public long Generation => Volatile.Read(ref _generation);

    public int Count
    {
        get
        {
            lock (_lock) return _samples.Count;
        }
    }

    /// <summary>Copia de las fotos entre dos instantes (ordenadas).</summary>
    public List<DepthSample> Between(DateTime fromUtc, DateTime toUtc)
    {
        lock (_lock)
        {
            int i = IndexAtOrAfter(fromUtc);
            var res = new List<DepthSample>();
            for (; i < _samples.Count && _samples[i].Time < toUtc; i++) res.Add(_samples[i]);
            return res;
        }
    }

    /// <summary>La última foto (o null).</summary>
    public DepthSample? Latest
    {
        get
        {
            lock (_lock) return _samples.Count > 0 ? _samples[^1] : null;
        }
    }

    /// <summary>La última foto anterior a un instante (o null).</summary>
    public DepthSample? Before(DateTime utc)
    {
        lock (_lock)
        {
            int i = IndexAtOrAfter(utc) - 1;
            return i >= 0 ? _samples[i] : null;
        }
    }

    private int IndexAtOrAfter(DateTime t)
    {
        int lo = 0, hi = _samples.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_samples[mid].Time < t) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    public static string DayPath(string root, string feedId, string symbol, DateOnly day)
    {
        static string Clean(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(root, "Liquidez", Clean(feedId), Clean(symbol), day.ToString("yyyy-MM-dd") + ".liq");
    }

    /// <summary>Toma una foto si pasó el intervalo (se llama con cada actualización del libro).</summary>
    public void Sample(DepthSnapshot snap, bool persist)
    {
        if (!Enabled || snap.Bids.Length == 0 || snap.Asks.Length == 0) return;
        var now = snap.Time == DateTime.MinValue ? DateTime.UtcNow : snap.Time;
        if (now - _lastSample < TimeSpan.FromSeconds(Math.Max(1, SampleSeconds))) return;
        _lastSample = now;
        var s = Make(snap, now, LevelsPerSide);
        lock (_lock)
        {
            if (_samples.Count > 0 && s.Time <= _samples[^1].Time) return;
            _samples.Add(s);
            Trim();
        }

        Interlocked.Increment(ref _generation);
        if (persist && Root != null) Write(s);
    }

    private void Trim()
    {
        if (_samples.Count == 0) return;
        // relativo a la foto más nueva (en un replay el reloj no es el de ahora)
        var limit = _samples[^1].Time.AddHours(-Math.Max(1, KeepHours));
        int n = 0;
        while (n < _samples.Count && _samples[n].Time < limit) n++;
        if (n > 0) _samples.RemoveRange(0, n);
    }

    public static DepthSample Make(DepthSnapshot snap, DateTime time, int levels)
    {
        long bestBid = snap.Bids[0].PriceTicks, bestAsk = snap.Asks[0].PriceTicks;
        // cuántos ticks abarca el libro: si no entra en «levels» niveles al tick, cada nivel resume un tramo
        long spanBid = bestBid - snap.Bids[^1].PriceTicks, spanAsk = snap.Asks[^1].PriceTicks - bestAsk;
        long span = Math.Max(spanBid, spanAsk) + 1;
        int bucket = (int)Math.Max(1, (span + levels - 1) / levels);
        if (bucket == 1 && snap.Bids.Length <= levels && snap.Asks.Length <= levels)
        {
            var bo1 = new int[snap.Bids.Length];
            var bs1 = new float[snap.Bids.Length];
            for (int i = 0; i < bo1.Length; i++)
            {
                bo1[i] = (int)Math.Clamp(bestBid - snap.Bids[i].PriceTicks, 0, int.MaxValue);
                bs1[i] = (float)snap.Bids[i].Size;
            }

            var ao1 = new int[snap.Asks.Length];
            var as1 = new float[snap.Asks.Length];
            for (int i = 0; i < ao1.Length; i++)
            {
                ao1[i] = (int)Math.Clamp(snap.Asks[i].PriceTicks - bestAsk, 0, int.MaxValue);
                as1[i] = (float)snap.Asks[i].Size;
            }

            return new DepthSample(time, bestBid, bestAsk, bo1, bs1, ao1, as1, 1);
        }

        // resumen por tramos de «bucket» ticks (se suman los tamaños), hasta «levels» tramos por lado
        var bids = new SortedDictionary<int, float>();
        foreach (var l in snap.Bids)
        {
            int k = (int)Math.Clamp((bestBid - l.PriceTicks) / bucket, 0, int.MaxValue);
            if (k >= levels) break;
            bids.TryGetValue(k, out var v);
            bids[k] = v + (float)l.Size;
        }

        var asks = new SortedDictionary<int, float>();
        foreach (var l in snap.Asks)
        {
            int k = (int)Math.Clamp((l.PriceTicks - bestAsk) / bucket, 0, int.MaxValue);
            if (k >= levels) break;
            asks.TryGetValue(k, out var v);
            asks[k] = v + (float)l.Size;
        }

        var bo = new int[bids.Count];
        var bs = new float[bids.Count];
        int j = 0;
        foreach (var (k, v) in bids)
        {
            bo[j] = k * bucket;
            bs[j++] = v;
        }

        var ao = new int[asks.Count];
        var asz = new float[asks.Count];
        j = 0;
        foreach (var (k, v) in asks)
        {
            ao[j] = k * bucket;
            asz[j++] = v;
        }

        return new DepthSample(time, bestBid, bestAsk, bo, bs, ao, asz, bucket);
    }

    // ===================================================================== disco

    private void Write(DepthSample s)
    {
        try
        {
            var day = s.Time.Date;
            if (_writer == null || day != _fileDay)
            {
                _writer?.Dispose();
                _file?.Dispose();
                var path = DayPath(Root!, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(day));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new BinaryWriter(_file);
                _fileDay = day;
            }

            WriteOne(_writer!, s);
            _writer!.Flush();
        }
        catch (Exception)
        {
            // sin disco: sigue en memoria
            _writer = null;
            _file = null;
        }
    }

    public static void WriteOne(BinaryWriter w, DepthSample s)
    {
        w.Write(s.Time.Ticks);
        w.Write(s.BestBid);
        w.Write(s.BestAsk);
        w.Write((ushort)s.BidOffsets.Length);
        w.Write((ushort)s.AskOffsets.Length);
        w.Write(s.Bucket);
        for (int i = 0; i < s.BidOffsets.Length; i++)
        {
            w.Write(s.BidOffsets[i]);
            w.Write(s.BidSizes[i]);
        }

        for (int i = 0; i < s.AskOffsets.Length; i++)
        {
            w.Write(s.AskOffsets[i]);
            w.Write(s.AskSizes[i]);
        }
    }

    /// <summary>Lee un archivo de día (lo que se pueda: una cola rota se ignora).</summary>
    public static List<DepthSample> ReadFile(string path)
    {
        var list = new List<DepthSample>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new BinaryReader(fs);
            while (fs.Position + 32 <= fs.Length)
            {
                long ticks = r.ReadInt64();
                long bb = r.ReadInt64(), ba = r.ReadInt64();
                int nb = r.ReadUInt16(), na = r.ReadUInt16();
                int bucket = r.ReadInt32();
                if (bucket < 1 || bucket > 1_000_000 || fs.Position + (nb + na) * 8L > fs.Length) break;
                var bo = new int[nb];
                var bs = new float[nb];
                for (int i = 0; i < nb; i++)
                {
                    bo[i] = r.ReadInt32();
                    bs[i] = r.ReadSingle();
                }

                var ao = new int[na];
                var asz = new float[na];
                for (int i = 0; i < na; i++)
                {
                    ao[i] = r.ReadInt32();
                    asz[i] = r.ReadSingle();
                }

                list.Add(new DepthSample(new DateTime(ticks, DateTimeKind.Utc), bb, ba, bo, bs, ao, asz, bucket));
            }
        }
        catch (Exception)
        {
            // archivo dañado: lo leído sirve igual
        }

        return list;
    }

    /// <summary>Carga de disco lo guardado de las últimas <see cref="KeepHours"/> horas (al abrir el instrumento).</summary>
    public void Load()
    {
        if (Root == null) return;
        var since = DateTime.UtcNow.AddHours(-Math.Max(1, KeepHours));
        var loaded = new List<DepthSample>();
        for (var day = DateOnly.FromDateTime(since); day <= DateOnly.FromDateTime(DateTime.UtcNow); day = day.AddDays(1))
        {
            var path = DayPath(Root, _instrument.FeedId, _instrument.Symbol, day);
            if (!File.Exists(path)) continue;
            foreach (var s in ReadFile(path))
                if (s.Time >= since) loaded.Add(s);
        }

        if (loaded.Count == 0) return;
        lock (_lock)
        {
            // lo de disco va antes de lo que ya entró en vivo
            var live = _samples.Where(s => loaded.Count == 0 || s.Time > loaded[^1].Time).ToList();
            _samples.Clear();
            _samples.AddRange(loaded);
            _samples.AddRange(live);
            Trim();
        }

        Interlocked.Increment(ref _generation);
    }

    /// <summary>Días con fotos guardadas (para Datos).</summary>
    public static List<DateOnly> AvailableDays(string root, string feedId, string symbol)
    {
        var dir = Path.GetDirectoryName(DayPath(root, feedId, symbol, DateOnly.MinValue))!;
        if (!Directory.Exists(dir)) return new List<DateOnly>();
        return Directory.EnumerateFiles(dir, "*.liq").Select(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", out var d) ? d : (DateOnly?)null)
            .Where(d => d != null).Select(d => d!.Value).OrderBy(d => d).ToList();
    }

    public void Close()
    {
        _writer?.Dispose();
        _file?.Dispose();
        _writer = null;
        _file = null;
    }
}
