using System.Globalization;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Historia larga en velas del exchange (1, 5 o 15 minutos): se baja en segundos aunque sean años, se
/// guarda en caché por mes y se convierte en operaciones sintéticas que respetan exactamente el OHLC,
/// el volumen y el delta de cada vela. Las operaciones sintéticas tienen id negativo: las velas que
/// arman se marcan (<see cref="Candle.IsSynthetic"/>) para no inventar un footprint.
/// </summary>
public static class KlineHistory
{
    /// <summary>Carpeta de la caché (la fija la app). Sin carpeta no se guarda nada.</summary>
    public static string? CacheRoot { get; set; }

    /// <summary>Base de los ids sintéticos (siempre negativos y crecientes con el tiempo).</summary>
    public const long SyntheticIdBase = long.MinValue / 2;

    /// <summary>Armada con velas (por debajo de las resumidas por hora, que sí cuentan como reales para las velas).</summary>
    public static bool IsSynthetic(long tradeId) => tradeId < CompactHistory.CompressedIdBase;

    /// <summary>Resolución según el largo del rango: menos memoria cuando son años.</summary>
    public static int MinutesFor(TimeSpan range) => range.TotalDays <= 400 ? 1 : range.TotalDays <= 1500 ? 5 : 15;

    /// <summary>Operaciones sintéticas aproximadas que genera un rango (para avisar antes de cargar).</summary>
    public static long EstimateTrades(TimeSpan range) => (long)(range.TotalMinutes / MinutesFor(range) * 4);

    private static long ToMs(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    /// <summary>Velas entre dos instantes, usando la caché para los meses ya cerrados.</summary>
    public static async Task<List<Kline>> LoadAsync(IKlineHistory source, Instrument inst, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        IProgress<double>? progress, CancellationToken ct, Action<Exception>? onError = null)
    {
        var result = new List<Kline>();
        if (toUtc <= fromUtc) return result;

        // el simulador genera la historia hacia atrás desde el precio actual: de una sola vez y sin caché
        if (inst.FeedId == SimulatedFeed.FeedId || CacheRoot == null)
        {
            await source.LoadKlinesAsync(inst, fromUtc, toUtc, minutes, endPrice, batch =>
            {
                result.AddRange(batch);
                return !ct.IsCancellationRequested;
            }, progress, ct).ConfigureAwait(false);
            return result;
        }

        var months = new List<DateTime>();
        for (var m = new DateTime(fromUtc.Year, fromUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc); m < toUtc; m = m.AddMonths(1)) months.Add(m);
        long fromMs = ToMs(fromUtc), toMs = ToMs(toUtc);
        for (int i = 0; i < months.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var monthStart = months[i];
            var monthEnd = monthStart.AddMonths(1);
            // un mes se da por cerrado (y se guarda) dos días después de terminar
            bool closed = monthEnd <= DateTime.UtcNow.AddDays(-2);
            int index = i;
            var sub = new Progress<double>(p => progress?.Report((index + Math.Clamp(p, 0, 1)) / months.Count));
            List<Kline> klines;
            if (closed && ReadCache(inst, monthStart, minutes, source.CacheTag) is { } cached)
            {
                klines = cached;
            }
            else
            {
                var from = closed ? monthStart : (fromUtc > monthStart ? fromUtc : monthStart);
                var to = closed ? monthEnd : (toUtc < monthEnd ? toUtc : monthEnd);
                klines = new List<Kline>();
                var target = klines;
                try
                {
                    await source.LoadKlinesAsync(inst, from, to, minutes, endPrice, batch =>
                    {
                        target.AddRange(batch);
                        return !ct.IsCancellationRequested;
                    }, sub, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (onError != null && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
                {
                    // un mes que no llegó no tira los demás (ni los guardados en caché)
                    onError(ex);
                    target.Clear();
                    continue;
                }

                if (closed && klines.Count > 0) WriteCache(inst, monthStart, minutes, klines, source.CacheTag);
            }

            foreach (var k in klines)
                if (k.OpenTimeMs >= fromMs && k.OpenTimeMs < toMs) result.Add(k);
            progress?.Report((i + 1) / (double)months.Count);
        }

        return result;
    }

    private static string CachePath(Instrument inst, DateTime month, int minutes, string tag)
    {
        static string Clean(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(CacheRoot!, Clean(inst.FeedId), Clean(inst.Symbol), $"{month:yyyy-MM}.{minutes}m{(tag.Length > 0 ? "." + tag : "")}.bin");
    }

    private static List<Kline>? ReadCache(Instrument inst, DateTime month, int minutes, string tag)
    {
        try
        {
            var path = CachePath(inst, month, minutes, tag);
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path));
            int n = r.ReadInt32();
            var list = new List<Kline>(n);
            for (int i = 0; i < n; i++)
            {
                long t = r.ReadInt64();
                decimal o = r.ReadDecimal(), h = r.ReadDecimal(), l = r.ReadDecimal(), c = r.ReadDecimal(), v = r.ReadDecimal(), b = r.ReadDecimal();
                bool hb = r.ReadBoolean();
                list.Add(new Kline(t, o, h, l, c, v, b, hb));
            }

            return list;
        }
        catch
        {
            return null; // caché dañada: se vuelve a bajar
        }
    }

    private static void WriteCache(Instrument inst, DateTime month, int minutes, List<Kline> klines, string tag)
    {
        try
        {
            var path = CachePath(inst, month, minutes, tag);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(klines.Count);
                foreach (var k in klines)
                {
                    w.Write(k.OpenTimeMs);
                    w.Write(k.Open);
                    w.Write(k.High);
                    w.Write(k.Low);
                    w.Write(k.Close);
                    w.Write(k.Volume);
                    w.Write(k.BuyVolume);
                    w.Write(k.HasBuyVolume);
                }
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // sin caché: la próxima vez se baja de nuevo
        }
    }

    /// <summary>
    /// Convierte velas en operaciones sintéticas: cuatro por vela (apertura, mínimo/máximo, máximo/mínimo y
    /// cierre), con el volumen comprador y vendedor exacto. Solo usa las velas que caen enteras dentro de
    /// alguna de las ventanas (los huecos sin ticks reales).
    /// </summary>
    /// <summary>
    /// Sin compras / ventas del exchange (Bybit, acciones…): repartir el volumen por la forma de la vela
    /// (comprador = volumen × (cierre − mínimo) / (máximo − mínimo)); apagado, mitad y mitad (delta cero).
    /// </summary>
    public static bool EstimateDeltaFromShape { get; set; } = true;

    public static decimal EstimateBuyVolume(Kline k)
    {
        if (!EstimateDeltaFromShape) return k.Volume / 2;
        decimal range = k.High - k.Low;
        if (range <= 0) return k.Volume / 2;
        return Math.Clamp(k.Volume * (k.Close - k.Low) / range, 0, k.Volume);
    }

    public static void Synthesize(IReadOnlyList<Kline> klines, Instrument inst, int minutes, IReadOnlyList<(DateTime From, DateTime To)> windows, List<Trade> output)
    {
        long step = minutes * 60_000L;
        long quarter = step / 4;
        int w = 0;
        var prints = new (decimal Vol, TradeSide Side)[4];
        var prices = new decimal[4];
        foreach (var k in klines)
        {
            if (k.Volume <= 0) continue;
            var open = FromMs(k.OpenTimeMs);
            var close = open.AddMilliseconds(step);
            while (w < windows.Count && windows[w].To <= open) w++;
            if (w >= windows.Count) break;
            if (open < windows[w].From || close > windows[w].To) continue;

            decimal buy = k.HasBuyVolume ? Math.Clamp(k.BuyVolume, 0, k.Volume) : EstimateBuyVolume(k);
            decimal sell = k.Volume - buy;
            bool up = k.Close >= k.Open;
            decimal o = inst.RoundPrice(k.Open), h = inst.RoundPrice(k.High), l = inst.RoundPrice(k.Low), c = inst.RoundPrice(k.Close);
            prices[0] = o;
            prices[1] = up ? l : h;
            prices[2] = up ? h : l;
            prices[3] = c;

            // compras en la apertura y el extremo a favor, ventas en el otro extremo y el cierre;
            // si un lado no tiene volumen, el otro se reparte en los cuatro puntos (así el OHLC queda exacto)
            if (buy <= 0)
                for (int j = 0; j < 4; j++) prints[j] = (sell / 4, TradeSide.Sell);
            else if (sell <= 0)
                for (int j = 0; j < 4; j++) prints[j] = (buy / 4, TradeSide.Buy);
            else
            {
                prints[0] = (buy / 2, TradeSide.Buy);
                prints[1] = (sell / 2, TradeSide.Sell);
                prints[2] = (buy / 2, TradeSide.Buy);
                prints[3] = (sell / 2, TradeSide.Sell);
            }

            long baseId = SyntheticIdBase + k.OpenTimeMs * 4;
            for (int j = 0; j < 4; j++)
                output.Add(new Trade(baseId + j, open.AddMilliseconds(quarter * j), prices[j], prints[j].Vol, prints[j].Side));
        }
    }

    /// <summary>
    /// Intervalos donde hay ticks reales (los cortes de más de 3 minutos los separan), redondeados a minutos
    /// completos para que ninguna vela se superponga con operaciones reales.
    /// </summary>
    public static List<(DateTime From, DateTime To)> Coverage(IReadOnlyList<Trade> real)
    {
        var res = new List<(DateTime, DateTime)>();
        if (real.Count == 0) return res;
        static DateTime Floor(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var start = real[0].Time;
        var last = real[0].Time;
        for (int i = 1; i < real.Count; i++)
        {
            var t = real[i].Time;
            if (t - last > TimeSpan.FromMinutes(3))
            {
                res.Add((Floor(start), Floor(last).AddMinutes(1)));
                start = t;
            }

            last = t;
        }

        res.Add((Floor(start), Floor(last).AddMinutes(1)));
        return res;
    }

    /// <summary>Resta a las ventanas los tramos cubiertos.</summary>
    public static List<(DateTime From, DateTime To)> Subtract(IReadOnlyList<(DateTime From, DateTime To)> windows, IReadOnlyList<(DateTime From, DateTime To)> covered)
    {
        var res = new List<(DateTime, DateTime)>();
        foreach (var (wf, wt) in windows)
        {
            var pieces = new List<(DateTime From, DateTime To)> { (wf, wt) };
            foreach (var (cf, ct) in covered)
            {
                var next = new List<(DateTime, DateTime)>();
                foreach (var (pf, pt) in pieces)
                {
                    if (ct <= pf || cf >= pt)
                    {
                        next.Add((pf, pt));
                        continue;
                    }

                    if (cf > pf) next.Add((pf, cf));
                    if (ct < pt) next.Add((ct, pt));
                }

                pieces = next;
            }

            res.AddRange(pieces.Where(p => p.To > p.From));
        }

        return res.OrderBy(p => p.Item1).ToList();
    }

    /// <summary>Une dos listas ordenadas por tiempo.</summary>
    public static List<Trade> MergeByTime(List<Trade> a, List<Trade> b)
    {
        if (a.Count == 0) return b;
        if (b.Count == 0) return a;
        var res = new List<Trade>(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count) res.Add(a[i].Time <= b[j].Time ? a[i++] : b[j++]);
        while (i < a.Count) res.Add(a[i++]);
        while (j < b.Count) res.Add(b[j++]);
        return res;
    }

    /// <summary>Lee un CSV de velas de los archivos públicos de Binance (con o sin encabezado).</summary>
    public static List<Kline> ParseBinanceCsv(TextReader reader)
    {
        var list = new List<Kline>(45_000);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || !char.IsDigit(line[0])) continue;
            var f = line.Split(',');
            if (f.Length < 10) continue;
            long t = long.Parse(f[0], CultureInfo.InvariantCulture);
            if (t > 100_000_000_000_000) t /= 1000; // desde 2025 spot viene en microsegundos
            decimal D(int k) => decimal.Parse(f[k], NumberStyles.Float, CultureInfo.InvariantCulture);
            list.Add(new Kline(t, D(1), D(2), D(3), D(4), D(5), D(9), true));
        }

        return list;
    }
}
