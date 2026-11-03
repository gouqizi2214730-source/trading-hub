using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Almacén columnar en memoria de todas las operaciones de un instrumento (historia + vivo).
/// Las velas de cualquier período se reconstruyen desde acá en milisegundos, así que cambiar
/// de 1m a 5m o de velas de tiempo a velas de rango no requiere volver a descargar nada.
/// Solo se usa desde el hilo de UI.
/// </summary>
public sealed class TradeStore
{
    private const int ChunkBits = 16;
    private const int ChunkSize = 1 << ChunkBits;
    private const int ChunkMask = ChunkSize - 1;

    private readonly List<Chunk> _chunks = new();
    private readonly Instrument _instrument;

    public TradeStore(Instrument instrument)
    {
        _instrument = instrument;
    }

    public Instrument Instrument => _instrument;
    public int Count { get; private set; }
    public long LastId { get; private set; } = -1;
    public DateTime FirstTime => Count > 0 ? GetTime(0) : DateTime.MinValue;
    public DateTime LastTime => Count > 0 ? GetTime(Count - 1) : DateTime.MinValue;

    private sealed class Chunk
    {
        public readonly long[] Ids = new long[ChunkSize];
        public readonly long[] Times = new long[ChunkSize];
        public readonly long[] Prices = new long[ChunkSize];
        public readonly decimal[] Volumes = new decimal[ChunkSize];
        public readonly byte[] Sides = new byte[ChunkSize];
    }

    public void Add(in Trade t)
    {
        int idx = Count;
        int c = idx >> ChunkBits;
        if (c == _chunks.Count) _chunks.Add(new Chunk());
        var ch = _chunks[c];
        int o = idx & ChunkMask;
        ch.Ids[o] = t.Id;
        ch.Times[o] = t.Time.Ticks;
        ch.Prices[o] = _instrument.ToTicks(t.Price);
        ch.Volumes[o] = t.Volume;
        ch.Sides[o] = (byte)t.Side;
        Count++;
        if (t.Id > LastId) LastId = t.Id;
    }

    /// <summary>Cambia cada vez que el almacén se vacía (se reemplaza la historia): lo armado sobre él ya no sirve.</summary>
    public long Generation { get; private set; }

    public void Clear()
    {
        _chunks.Clear();
        Count = 0;
        LastId = -1;
        Generation++;
    }

    public long GetId(int i) => _chunks[i >> ChunkBits].Ids[i & ChunkMask];
    public DateTime GetTime(int i) => new(_chunks[i >> ChunkBits].Times[i & ChunkMask], DateTimeKind.Utc);
    public long GetTimeTicks(int i) => _chunks[i >> ChunkBits].Times[i & ChunkMask];
    public long GetPriceTicks(int i) => _chunks[i >> ChunkBits].Prices[i & ChunkMask];
    public decimal GetPrice(int i) => _instrument.FromTicks(GetPriceTicks(i));
    public decimal GetVolume(int i) => _chunks[i >> ChunkBits].Volumes[i & ChunkMask];
    public TradeSide GetSide(int i) => (TradeSide)_chunks[i >> ChunkBits].Sides[i & ChunkMask];

    public Trade Get(int i)
    {
        var ch = _chunks[i >> ChunkBits];
        int o = i & ChunkMask;
        return new Trade(ch.Ids[o], new DateTime(ch.Times[o], DateTimeKind.Utc), _instrument.FromTicks(ch.Prices[o]), ch.Volumes[o], (TradeSide)ch.Sides[o]);
    }

    /// <summary>Primer índice con tiempo &gt;= time (búsqueda binaria).</summary>
    public int LowerBound(DateTime time)
    {
        long t = time.Ticks;
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (GetTimeTicks(mid) < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Estimación de memoria usada (bytes).</summary>
    public long MemoryBytes => (long)_chunks.Count * ChunkSize * (8 + 8 + 8 + 16 + 1);
}
