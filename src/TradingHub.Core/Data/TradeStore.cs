using TradingHub.Market;

namespace TradingHub.Data;

public sealed class TradeStore
{
    private const int ChunkBits = 16;
    private const int ChunkSize = 1 << ChunkBits;
    private const int ChunkMask = ChunkSize - 1;

    private Chunk[] _chunks = Array.Empty<Chunk>();
    private readonly Instrument _instrument;

    public TradeStore(Instrument instrument)
    {
        _instrument = instrument;
    }

    public Instrument Instrument => _instrument;
    private int _count;

    public int Count
    {
        get => Volatile.Read(ref _count);
        private set => Volatile.Write(ref _count, value);
    }

    public long LastId { get; private set; } = -1;
    public DateTime FirstTime => Count > 0 ? GetTime(0) : DateTime.MinValue;
    public DateTime LastTime
    {
        get
        {
            int c = Count;
            return c > 0 ? GetTime(c - 1) : DateTime.MinValue;
        }
    }

    private sealed class Chunk
    {
        public readonly long[] Ids = new long[ChunkSize];
        public readonly long[] Times = new long[ChunkSize];
        public readonly long[] Prices = new long[ChunkSize];
        public readonly ulong[] Volumes = new ulong[ChunkSize];
        public readonly byte[] Sides = new byte[ChunkSize];
    }

    public void Add(in Trade t)
    {
        int idx = _count;
        int c = idx >> ChunkBits;
        var chunks = _chunks;
        if (c >= chunks.Length)
        {
            var next = new Chunk[c + 1];
            Array.Copy(chunks, next, chunks.Length);
            for (int k = chunks.Length; k <= c; k++) next[k] = new Chunk();
            Volatile.Write(ref _chunks, next);
            chunks = next;
        }

        var ch = chunks[c];
        int o = idx & ChunkMask;
        ch.Ids[o] = t.Id;
        ch.Times[o] = t.Time.Ticks;
        ch.Prices[o] = _instrument.ToTicks(t.Price);
        var (mantissa, scale, neg) = PackVolume(t.Volume);
        ch.Volumes[o] = mantissa;
        ch.Sides[o] = (byte)(((byte)t.Side & 0x03) | ((scale & 0x1F) << 2) | (neg ? 0x80 : 0));
        Volatile.Write(ref _count, idx + 1);
        if (t.Id > LastId) LastId = t.Id;
    }

    private static (ulong Mantissa, int Scale, bool Negative) PackVolume(decimal d)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(d, bits);
        if (bits[2] != 0)
        {
            d /= 1.0000000000000000000000000000m;
            decimal.GetBits(d, bits);
            if (bits[2] != 0)
            {
                d = Math.Round(d, 8);
                decimal.GetBits(d, bits);
            }
        }

        int flags = bits[3];
        int scale = (flags >> 16) & 0xFF;
        bool neg = (flags & unchecked((int)0x80000000)) != 0;
        ulong mant = ((ulong)(uint)bits[1] << 32) | (uint)bits[0];
        return (mant, Math.Min(scale, 28), neg);
    }

    private static decimal UnpackVolume(ulong mantissa, byte sideAndScale)
    {
        int scale = (sideAndScale >> 2) & 0x1F;
        bool neg = (sideAndScale & 0x80) != 0;
        return new decimal((int)(mantissa & 0xFFFFFFFFUL), (int)(mantissa >> 32), 0, neg, (byte)scale);
    }

    private long _generation;

    public long Generation => Volatile.Read(ref _generation);

    public void Clear()
    {
        Volatile.Write(ref _generation, _generation + 1);
        Volatile.Write(ref _count, 0);
        LastId = -1;
        Volatile.Write(ref _chunks, Array.Empty<Chunk>());
    }

    private Chunk GetChunk(int i)
    {
        var chunks = Volatile.Read(ref _chunks);
        int c = i >> ChunkBits;
        if ((uint)c >= (uint)chunks.Length)
            throw new InvalidOperationException("El almacén de operaciones cambió durante la lectura concurrente.");
        return chunks[c];
    }

    public long GetId(int i) => GetChunk(i).Ids[i & ChunkMask];
    public DateTime GetTime(int i) => new(GetChunk(i).Times[i & ChunkMask], DateTimeKind.Utc);
    public long GetTimeTicks(int i) => GetChunk(i).Times[i & ChunkMask];
    public long GetPriceTicks(int i) => GetChunk(i).Prices[i & ChunkMask];
    public decimal GetPrice(int i) => _instrument.FromTicks(GetPriceTicks(i));

    public decimal GetVolume(int i)
    {
        var ch = GetChunk(i);
        int o = i & ChunkMask;
        return UnpackVolume(ch.Volumes[o], ch.Sides[o]);
    }

    public TradeSide GetSide(int i) => (TradeSide)(GetChunk(i).Sides[i & ChunkMask] & 0x03);

    public Trade Get(int i)
    {
        var ch = GetChunk(i);
        int o = i & ChunkMask;
        return new Trade(
            ch.Ids[o],
            new DateTime(ch.Times[o], DateTimeKind.Utc),
            _instrument.FromTicks(ch.Prices[o]),
            UnpackVolume(ch.Volumes[o], ch.Sides[o]),
            (TradeSide)(ch.Sides[o] & 0x03));
    }

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

    public long MemoryBytes => (long)Volatile.Read(ref _chunks).Length * ChunkSize * (8 + 8 + 8 + 8 + 1);
}
