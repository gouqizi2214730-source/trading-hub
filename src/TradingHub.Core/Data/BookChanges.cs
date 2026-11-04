namespace TradingHub.Data;

/// <summary>Lo que cambió en un precio del libro: órdenes agregadas, retiradas sin operarse y consumidas por operaciones.</summary>
public sealed class BookLevelChange
{
    public double BidAdded, BidPulled, BidFilled;
    public double AskAdded, AskPulled, AskFilled;

    public void Add(BookLevelChange o)
    {
        BidAdded += o.BidAdded;
        BidPulled += o.BidPulled;
        BidFilled += o.BidFilled;
        AskAdded += o.AskAdded;
        AskPulled += o.AskPulled;
        AskFilled += o.AskFilled;
    }

    public double Added => BidAdded + AskAdded;
    public double Pulled => BidPulled + AskPulled;
}

/// <summary>Una orden grande del libro mientras estuvo (y cómo terminó: operada, retirada o movida con el precio).</summary>
public sealed class LargeOrder
{
    public bool IsBid;
    public long Price;
    public double Size;
    public DateTime From, To;
    public bool Open = true;

    /// <summary>Se retiró sin operarse (posible spoof).</summary>
    public bool Pulled;

    /// <summary>Desapareció y reapareció al lado siguiendo al precio (chasing): el precio al que se fue.</summary>
    public long? MovedTo;

    /// <summary>Parte operada antes de desaparecer.</summary>
    public double Filled;
}

/// <summary>
/// «Order Book Changes» / «Order Book Delta» (Kiyotaka, openmarket): comparando cada foto del libro con la anterior se ve,
/// precio por precio, cuánto se agregó y cuánto se sacó; lo sacado se separa en lo que se operó (operaciones agresivas
/// a ese precio) y lo retirado sin operarse. Y el seguimiento de las órdenes grandes: cuándo aparecen, cuánto duran y si se
/// retiran (spoof) o se corren con el precio (chasing).
/// </summary>
public static class BookChanges
{
    /// <summary>Precios (en ticks) que cubren las dos fotos del lado pedido (fuera de eso un nivel «desaparece» por no verse).</summary>
    private static (long Lo, long Hi) Covered(DepthSample a, DepthSample b, bool bid)
    {
        if (bid)
        {
            long loA = a.BidOffsets.Length > 0 ? a.BestBid - a.BidOffsets[^1] : a.BestBid;
            long loB = b.BidOffsets.Length > 0 ? b.BestBid - b.BidOffsets[^1] : b.BestBid;
            return (Math.Max(loA, loB), Math.Max(a.BestBid, b.BestBid));
        }

        long hiA = a.AskOffsets.Length > 0 ? a.BestAsk + a.AskOffsets[^1] : a.BestAsk;
        long hiB = b.AskOffsets.Length > 0 ? b.BestAsk + b.AskOffsets[^1] : b.BestAsk;
        return (Math.Min(a.BestAsk, b.BestAsk), Math.Min(hiA, hiB));
    }

    private static Dictionary<long, double> Side(DepthSample s, bool bid)
    {
        var d = new Dictionary<long, double>(bid ? s.BidOffsets.Length : s.AskOffsets.Length);
        if (bid)
            for (int i = 0; i < s.BidOffsets.Length; i++) d[s.BidPrice(i)] = s.BidSizes[i];
        else
            for (int i = 0; i < s.AskOffsets.Length; i++) d[s.AskPrice(i)] = s.AskSizes[i];
        return d;
    }

    /// <summary>
    /// Cambios del libro durante una vela: <paramref name="samples"/> son sus fotos (en orden) y <paramref name="previous"/>
    /// la última de antes (para no perder el primer tramo). <paramref name="traded"/> da lo operado a cada precio en la vela
    /// (vendedores agresivos, que consumen el bid; compradores, el ask). Clave = precio en ticks agrupado de a
    /// <paramref name="groupTicks"/>.
    /// </summary>
    public static Dictionary<long, BookLevelChange> ForBar(IReadOnlyList<DepthSample> samples, DepthSample? previous, Func<long, (double Sell, double Buy)> traded, long groupTicks = 1)
    {
        var removedBid = new Dictionary<long, double>();
        var removedAsk = new Dictionary<long, double>();
        var res = new Dictionary<long, BookLevelChange>();
        groupTicks = Math.Max(1, groupTicks);
        long G(long p) => (long)Math.Floor((double)p / groupTicks) * groupTicks;
        BookLevelChange At(long p)
        {
            long g = G(p);
            if (!res.TryGetValue(g, out var c)) res[g] = c = new BookLevelChange();
            return c;
        }

        var prev = previous;
        Dictionary<long, double>? pb = prev == null ? null : Side(prev, true), pa = prev == null ? null : Side(prev, false);
        foreach (var cur in samples)
        {
            var cb = Side(cur, true);
            var ca = Side(cur, false);
            if (prev != null && prev.Bucket == cur.Bucket)
            {
                Compare(prev, cur, pb!, cb, true, removedBid, At);
                Compare(prev, cur, pa!, ca, false, removedAsk, At);
            }

            prev = cur;
            pb = cb;
            pa = ca;
        }

        // lo sacado: lo operado a ese precio primero, el resto se retiró sin operarse
        foreach (var (p, removed) in removedBid)
        {
            double filled = Math.Min(removed, traded(p).Sell);
            var c = At(p);
            c.BidFilled += filled;
            c.BidPulled += removed - filled;
        }

        foreach (var (p, removed) in removedAsk)
        {
            double filled = Math.Min(removed, traded(p).Buy);
            var c = At(p);
            c.AskFilled += filled;
            c.AskPulled += removed - filled;
        }

        return res;
    }

    private static void Compare(DepthSample a, DepthSample b, Dictionary<long, double> sa, Dictionary<long, double> sb, bool bid,
        Dictionary<long, double> removed, Func<long, BookLevelChange> at)
    {
        var (lo, hi) = Covered(a, b, bid);
        if (hi < lo) return;
        // del lado del bid, un precio por encima del mejor bid nuevo ya no tiene bid (se consumió o se retiró); igual el ask
        foreach (var (p, size) in sa)
        {
            if (p < lo || p > hi) continue;
            double now = sb.TryGetValue(p, out var v) ? v : 0;
            if (now < size) removed[p] = (removed.TryGetValue(p, out var r) ? r : 0) + (size - now);
        }

        foreach (var (p, size) in sb)
        {
            if (p < lo || p > hi) continue;
            double before = sa.TryGetValue(p, out var v) ? v : 0;
            if (size > before)
            {
                var c = at(p);
                if (bid) c.BidAdded += size - before;
                else c.AskAdded += size - before;
            }
        }
    }

    /// <summary>
    /// Sigue las órdenes grandes (tamaño ≥ <paramref name="minSize"/>) foto a foto. Una que desaparece sin operarse es
    /// «retirada» (spoof si duró menos de <paramref name="spoofSeconds"/>); si en la misma foto aparece otra parecida (±30 %)
    /// del mismo lado, a pocos ticks y en la dirección del precio, es «chasing» (se corrió con el precio).
    /// </summary>
    public static List<LargeOrder> TrackLarge(IReadOnlyList<DepthSample> samples, double minSize, Func<long, DateTime, DateTime, double> tradedAt,
        int maxMoveTicks = 50, List<LargeOrder>? open = null)
    {
        var all = new List<LargeOrder>();
        var live = new Dictionary<(bool, long), LargeOrder>();
        if (open != null)
            foreach (var o in open.Where(o => o.Open)) live[(o.IsBid, o.Price)] = o;
        DepthSample? prev = null;
        foreach (var s in samples)
        {
            var seen = new HashSet<(bool, long)>();
            var born = new List<LargeOrder>();
            for (int side = 0; side < 2; side++)
            {
                bool bid = side == 0;
                int n = bid ? s.BidOffsets.Length : s.AskOffsets.Length;
                for (int i = 0; i < n; i++)
                {
                    double size = bid ? s.BidSizes[i] : s.AskSizes[i];
                    if (size < minSize) continue;
                    long p = bid ? s.BidPrice(i) : s.AskPrice(i);
                    var key = (bid, p);
                    seen.Add(key);
                    if (live.TryGetValue(key, out var o))
                    {
                        o.To = s.Time;
                        o.Size = Math.Max(o.Size, size);
                    }
                    else
                    {
                        o = new LargeOrder { IsBid = bid, Price = p, Size = size, From = s.Time, To = s.Time };
                        live[key] = o;
                        all.Add(o);
                        born.Add(o);
                    }
                }
            }

            // las que ya no están: operadas, retiradas o corridas
            foreach (var (key, o) in live.ToList())
            {
                if (seen.Contains(key)) continue;
                // fuera de lo que muestra la foto (el precio se alejó): no se sabe, se cierra sin juzgar
                bool visible = key.Item1
                    ? s.BidOffsets.Length > 0 && key.Item2 >= s.BestBid - s.BidOffsets[^1] && key.Item2 <= Math.Max(s.BestBid, prev?.BestBid ?? s.BestBid)
                    : s.AskOffsets.Length > 0 && key.Item2 <= s.BestAsk + s.AskOffsets[^1] && key.Item2 >= Math.Min(s.BestAsk, prev?.BestAsk ?? s.BestAsk);
                live.Remove(key);
                o.Open = false;
                o.To = s.Time;
                if (!visible) continue;
                o.Filled = tradedAt(o.Price, o.From, s.Time);
                if (o.Filled >= o.Size * 0.5) continue; // se operó
                o.Pulled = true;
                var twin = born.FirstOrDefault(b => b.IsBid == o.IsBid && Math.Abs(b.Price - o.Price) <= maxMoveTicks && b.Price != o.Price
                                                    && b.Size >= o.Size * 0.7 && b.Size <= o.Size * 1.3
                                                    && (prev == null || (o.IsBid ? Math.Sign(b.Price - o.Price) == Math.Sign(s.BestBid - prev.BestBid) : Math.Sign(b.Price - o.Price) == Math.Sign(s.BestAsk - prev.BestAsk))));
                if (twin != null) o.MovedTo = twin.Price;
            }

            prev = s;
        }

        return all;
    }
}
