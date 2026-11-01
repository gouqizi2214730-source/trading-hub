using System.Collections.Concurrent;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Todos los datos de un instrumento: operaciones (historia + vivo), libro y estado de la conexión.
/// Los feeds escriben desde sus hilos en colas; la UI llama a <see cref="Pump"/> en su hilo para
/// aplicar los cambios y disparar los eventos. Varios gráficos pueden compartir la misma instancia.
/// </summary>
public sealed class InstrumentData : IMarketDataSink, IDisposable
{
    private readonly ConcurrentQueue<Trade> _incoming = new();
    private readonly ConcurrentQueue<BookChange> _depthChanges = new();
    private volatile DepthSnapshot? _latestDepth;
    private volatile bool _depthFull;
    private volatile bool _historyMerged;
    private List<Trade>? _historyToMerge;
    private readonly object _historyLock = new();
    private IDisposable? _subscription;
    private CancellationTokenSource? _cts;
    private TickRecorder? _recorder;
    private FeedStatus _status = FeedStatus.Disconnected;
    private string? _statusMessage;
    private volatile bool _statusDirty;

    public InstrumentData(Instrument instrument, IMarketDataFeed feed)
    {
        Instrument = instrument;
        Feed = feed;
        Trades = new TradeStore(instrument);
    }

    public Instrument Instrument { get; }
    public IMarketDataFeed Feed { get; }
    public TradeStore Trades { get; }
    public DepthSnapshot Depth { get; private set; } = DepthSnapshot.Empty;

    public FeedStatus Status => _status;
    public string? StatusMessage => _statusMessage;
    public bool IsLoadingHistory { get; private set; }
    public double HistoryProgress { get; private set; }
    public string? HistoryError { get; private set; }

    /// <summary>Desde cuándo hay historia pedida/cargada (UTC).</summary>
    public DateTime HistoryFrom { get; private set; } = DateTime.MaxValue;

    /// <summary>Si está trayendo historia más vieja en segundo plano.</summary>
    public bool IsExtending { get; private set; }

    private List<Trade>? _olderToMerge;
    private DateTime _pendingFrom;

    /// <summary>Última operación del mercado cuando está cerrado (la historia se arma hasta ahí).</summary>
    private DateTime? _anchor;

    public decimal LastPrice => Trades.Count > 0 ? Trades.GetPrice(Trades.Count - 1) : 0m;
    public decimal BestBid => Depth.HasBid ? Instrument.FromTicks(Depth.BestBidTicks) : 0m;
    public decimal BestAsk => Depth.HasAsk ? Instrument.FromTicks(Depth.BestAskTicks) : 0m;

    /// <summary>Operaciones nuevas agregadas al almacén (índice inicial, cantidad). Hilo de UI.</summary>
    public event Action<int, int>? TradesAppended;

    /// <summary>La historia terminó de cargarse (los gráficos deben reconstruirse). Hilo de UI.</summary>
    public event Action? HistoryLoaded;

    /// <summary>Cambió el libro. Hilo de UI.</summary>
    public event Action<DepthUpdate>? DepthUpdated;

    public event Action? StatusChanged;

    /// <summary>Carpeta donde se graban los ticks (null = no grabar).</summary>
    public string? RecordingRoot { get; set; }

    /// <summary>Si graba lo recibido en vivo (la carpeta se sigue leyendo aunque no grabe).</summary>
    public bool RecordLive { get; set; } = true;

    // ----------------------------------------------------------------- arranque

    /// <summary>Se suscribe al vivo y carga historia desde <paramref name="historyFrom"/>.</summary>
    public void Start(DateTime historyFrom)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _historyMerged = false;
        IsLoadingHistory = true;
        HistoryFrom = historyFrom;
        HistoryProgress = 0;
        HistoryError = null;
        SetStatus(FeedStatus.Connecting, "Conectando…");

        if (Feed.IsLive && RecordingRoot != null && RecordLive)
            _recorder = new TickRecorder(RecordingRoot, Instrument);

        // 1) vivo primero (se acumula mientras carga la historia)
        try
        {
            _subscription = Feed.Subscribe(Instrument, this);
        }
        catch (Exception ex)
        {
            SetStatus(FeedStatus.Error, ex.Message);
        }

        // 2) historia: archivos locales + los huecos que falten desde el feed
        _ = Task.Run(async () =>
        {
            List<Trade> all;
            try
            {
                var from = historyFrom;
                if (Feed is ISessionAwareFeed session)
                {
                    // mercado cerrado (noche, fin de semana): el mismo largo de historia, terminando en la última operación
                    try
                    {
                        var last = await session.LastTradeTimeAsync(Instrument, ct).ConfigureAwait(false);
                        if (last is { } lt && Feed.Now - lt > TimeSpan.FromMinutes(10))
                        {
                            _anchor = lt;
                            from = lt - (Feed.Now - historyFrom);
                            HistoryFrom = from;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // sin la hora de la última operación se carga como siempre
                    }
                }

                all = await LoadRangeAsync(from, Feed.Now, long.MaxValue, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                HistoryError = ex.Message;
                all = new List<Trade>();
            }

            lock (_historyLock) _historyToMerge = all;
        }, ct);
    }

    /// <summary>
    /// Hasta qué antigüedad se piden ticks al exchange cuando faltan. Lo anterior (y sin ticks en disco)
    /// se completa con velas del exchange: así se cargan meses o años en segundos, como en cualquier plataforma.
    /// </summary>
    public TimeSpan TickWindow { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Hasta cuánto atrás se piden ticks al exchange al abrir un gráfico (lo más viejo, de lo bajado en Datos).</summary>
    public static TimeSpan MaxFetchWindow { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Máximo de operaciones reales que se leen de disco por carga (lo que no entra se completa con velas).</summary>
    public static long MaxDiskTrades { get; set; } = MemoryBudget(0.12, 30_000_000, 200_000_000);

    /// <summary>Desde cuándo hay ticks reales (antes, la historia viene de velas del exchange).</summary>
    public DateTime RealTicksFrom { get; private set; } = DateTime.MaxValue;

    /// <summary>
    /// Lee lo grabado/descargado entre <paramref name="from"/> y <paramref name="to"/> y completa los huecos:
    /// lo reciente con ticks del exchange (se graban para no volver a pedirlos) y lo viejo con velas.
    /// </summary>
    private async Task<List<Trade>> LoadRangeAsync(DateTime from, DateTime to, long maxIdExclusive, CancellationToken ct)
    {
        var real = new List<Trade>();
        var compactCovered = new List<(DateTime From, DateTime To)>();
        List<Trade>? compact = null;
        if (RecordingRoot != null && Feed.IsLive)
        {
            // de lo más nuevo a lo más viejo: dentro de la ventana de ticks y hasta el tope de memoria, cada operación; lo
            // de antes (o lo que no entra), resumido por hora (ver CompactHistory)
            DateTime rawCut = (_anchor ?? Feed.Now) - TickWindow;
            var chunks = new List<List<Trade>>();
            var compactDays = new List<DateOnly>();
            long count = 0;
            bool rawFull = false;
            // los días de la ventana se leen en paralelo (cada uno tarda segundos con millones de operaciones)
            var windowDays = EachDay(from, to).Where(d => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1) > rawCut
                                                          && File.Exists(TickFile.DayPath(RecordingRoot, Instrument.FeedId, Instrument.Symbol, d))).ToList();
            var prefetched = new System.Collections.Concurrent.ConcurrentDictionary<DateOnly, List<Trade>>();
            if (windowDays.Count > 1)
                Parallel.ForEach(windowDays, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 6), CancellationToken = ct }, d =>
                {
                    try
                    {
                        prefetched[d] = TickFile.Read(TickFile.DayPath(RecordingRoot, Instrument.FeedId, Instrument.Symbol, d));
                    }
                    catch (IOException)
                    {
                        // se vuelve a intentar abajo
                    }
                });

            foreach (var day in EachDay(from, to).Reverse())
            {
                var path = TickFile.DayPath(RecordingRoot, Instrument.FeedId, Instrument.Symbol, day);
                if (!File.Exists(path)) continue;
                var dayStart = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                bool raw = !rawFull && (dayStart.AddDays(1) > rawCut || CompactRoot == null || !UseCompactHistory);
                if (raw)
                {
                    var read = prefetched.TryRemove(day, out var pre) ? pre : TickFile.Read(path);
                    var list = read.Where(t => t.Time >= from && t.Time < to && t.Id < maxIdExclusive).ToList();
                    if (count > 0 && count + list.Count > MaxDiskTrades) rawFull = true;
                    else
                    {
                        chunks.Add(list);
                        count += list.Count;
                        continue;
                    }
                }

                if (CompactRoot == null || !UseCompactHistory) break;
                compactDays.Add(day);
            }

            chunks.Reverse();
            foreach (var c in chunks) real.AddRange(c);
            real = TickFile.SortAndDedupe(real);

            if (compactDays.Count > 0)
            {
                compactDays.Reverse();
                compact = LoadCompact(compactDays, from, to, compactCovered, ct);
            }
        }

        // los huecos se buscan en las operaciones sueltas; lo resumido por hora cuenta como cubierto
        List<Trade> Merged()
        {
            if (compact == null || compact.Count == 0) return real;
            var all = new List<Trade>(compact.Count + real.Count);
            all.AddRange(compact);
            all.AddRange(real);
            return TickFile.SortAndDedupe(all);
        }

        if (!Feed.SupportsHistory) return Merged();
        var gaps = SubtractCovered(FindGaps(real, from, to, tailById: maxIdExclusive == long.MaxValue), compactCovered);
        if (gaps.Count == 0) return Merged();

        // lo reciente en ticks; lo viejo en velas (si el feed las tiene)
        var klineSource = Feed as IKlineHistory;
        // los ticks cubren las últimas horas de mercado (con el mercado cerrado, antes de la última operación)
        // (con ventanas largas, del exchange se piden ticks como mucho de MaxFetchWindow: lo de antes sale de lo bajado en
        // Datos o va en velas; si no, meses de ticks bajados de una vez llenarían la memoria)
        DateTime tickCut = klineSource != null ? (_anchor ?? Feed.Now) - (TickWindow < MaxFetchWindow ? TickWindow : MaxFetchWindow) : DateTime.MinValue;
        var tickGaps = new List<(DateTime From, DateTime To, long? FromId)>();
        var klineGaps = new List<(DateTime From, DateTime To)>();
        foreach (var (gFrom, gTo, fromId) in gaps)
        {
            if (gTo <= tickCut) klineGaps.Add((gFrom, gTo));
            else if (gFrom >= tickCut) tickGaps.Add((gFrom, gTo, fromId));
            else
            {
                klineGaps.Add((gFrom, tickCut));
                tickGaps.Add((tickCut, gTo, null));
            }
        }

        int parts = tickGaps.Count + (klineGaps.Count > 0 ? 1 : 0);
        int part = 0;
        var fetched = new List<Trade>();
        foreach (var (gFrom, gTo, fromId) in tickGaps)
        {
            int index = part++;
            try
            {
            // los días ya cerrados salen del archivo público del exchange (un día entero en segundos, en vez de miles de
            // pedidos a la API); lo de hoy y lo que el archivo todavía no tenga, de la API como siempre
            var rest = await LoadFromArchivesAsync(gFrom, gTo, fromId, fetched, from, to, maxIdExclusive,
                p => HistoryProgress = (index + Math.Clamp(p, 0, 1) * 0.5) / parts, ct).ConfigureAwait(false);
            for (int r = 0; r < rest.Count; r++)
            {
                var (rFrom, rTo, rId) = rest[r];
                double share = 1.0 / rest.Count, done = (double)r / rest.Count;
                var progress = new Progress<double>(p => HistoryProgress = (index + 0.5 + 0.5 * (done + share * Math.Clamp(p, 0, 1))) / parts);
                await Feed.LoadTradesAsync(Instrument, rFrom, rTo, rId, batch =>
                {
                    foreach (var t in batch)
                        if (t.Id < maxIdExclusive && t.Time >= from && t.Time <= to) fetched.Add(t);
                    return !ct.IsCancellationRequested;
                }, progress, ct).ConfigureAwait(false);
            }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // sin red hacia el exchange: lo de disco se muestra igual (antes se perdía todo)
                NetworkProblem(ex);
            }
        }

        if (fetched.Count > 0)
        {
            _recorder?.WriteHistory(fetched);
            real.AddRange(fetched);
            real = TickFile.SortAndDedupe(real);
        }

        if (real.Count > 0) RealTicksFrom = real[0].Time < RealTicksFrom ? real[0].Time : RealTicksFrom;

        // feeds que solo dan lo último por la API (Bybit): lo que los ticks no cubrieron (hoy antes de abrir, o un día sin
        // archivo publicado todavía) va en velas en vez de quedar vacío
        if (klineSource != null && Feed.RecentTradesOnly && tickGaps.Count > 0)
        {
            var missing = KlineHistory.Subtract(tickGaps.Select(g => (g.From, g.To)).ToList(), KlineHistory.Coverage(real))
                .Where(m => m.To - m.From >= TimeSpan.FromMinutes(2)).ToList();
            if (missing.Count > 0)
            {
                klineGaps.AddRange(missing);
                klineGaps.Sort((a, b) => a.From.CompareTo(b.From));
            }
        }

        if (klineGaps.Count == 0 || klineSource == null) return Merged();

        // velas solo donde no hay ticks reales
        var windows = KlineHistory.Subtract(klineGaps, KlineHistory.Coverage(real));
        if (windows.Count == 0) return Merged();
        var kFrom = windows[0].From;
        var kTo = windows[^1].To;
        int minutes = KlineHistory.MinutesFor(kTo - kFrom);
        decimal endPrice = real.Count > 0 ? real[0].Price : Trades.Count > 0 ? Trades.GetPrice(0) : 0;
        int kIndex = part;
        var kProgress = new Progress<double>(p => HistoryProgress = (kIndex + Math.Clamp(p, 0, 1)) / parts);
        List<Kline> klines;
        try
        {
            klines = await KlineHistory.LoadAsync(klineSource, Instrument, kFrom, kTo, minutes, endPrice, kProgress, ct, NetworkProblem).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            NetworkProblem(ex);
            return Merged();
        }

        var synthetic = new List<Trade>(klines.Count * 4);
        KlineHistory.Synthesize(klines, Instrument, minutes, windows, synthetic);
        return KlineHistory.MergeByTime(synthetic, Merged());
    }

    /// <summary>
    /// Un pedido al exchange falló mientras se cargaba la historia: se anota (se muestra en la barra de estado) y se sigue
    /// con lo que haya (lo de disco y lo que sí llegó).
    /// </summary>
    private void NetworkProblem(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        HistoryError ??= inner != ex ? $"{ex.Message} ({inner.Message})" : ex.Message;
    }

    /// <summary>Raíz de la caché de días resumidos por hora (null = no se resume: lo que no entra va en velas).</summary>
    public string? CompactRoot { get; set; }

    /// <summary>Resumir por hora los días con ticks que quedan fuera de la ventana (se apaga en algunas pruebas).</summary>
    public static bool UseCompactHistory { get; set; } = true;

    /// <summary>Tramo del resumen en minutos (1, 5, 15 o 60): las velas de ese largo o más quedan exactas.</summary>
    public int CompactMinutes { get; set; } = 1;

    /// <summary>
    /// Tope de operaciones resumidas en memoria (de lo más nuevo a lo más viejo): ~25 % de la memoria a ~100 bytes por
    /// operación mientras se carga (64 GB → ~170 millones), entre 20 y 400 millones.
    /// </summary>
    public static long MaxCompactTrades { get; set; } = MemoryBudget(0.25, 20_000_000, 400_000_000);

    /// <summary>Operaciones que entran en una parte de la memoria de la computadora.</summary>
    public static long MemoryBudget(double share, long min, long max)
    {
        long total;
        try
        {
            total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch
        {
            total = 0;
        }

        return total <= 0 ? min : Math.Clamp((long)(total * share / 100), min, max);
    }

    /// <summary>Días resumidos por hora (arma en paralelo los que faltan en la caché), del más viejo al más nuevo.</summary>
    private List<Trade> LoadCompact(List<DateOnly> days, DateTime from, DateTime to, List<(DateTime From, DateTime To)> covered, CancellationToken ct)
    {
        var root = RecordingRoot!;
        var cacheRoot = CompactRoot!;
        var missing = days.Where(d => !CompactHistory.IsFresh(CompactHistory.CachePath(cacheRoot, Instrument.FeedId, Instrument.Symbol, d, CompactMinutes),
            TickFile.DayPath(root, Instrument.FeedId, Instrument.Symbol, d))).ToList();
        int done = 0;
        if (missing.Count > 0)
        {
            Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), CancellationToken = ct }, d =>
            {
                try
                {
                    CompactHistory.EnsureCached(root, cacheRoot, Instrument, d, CompactMinutes);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // ese día queda para las velas
                }

                HistoryProgress = 0.9 * Interlocked.Increment(ref done) / missing.Count;
            });
        }

        // de lo más nuevo a lo más viejo hasta el tope; después en orden
        var perDay = new List<(DateOnly Day, List<Trade> Trades)>();
        long count = 0;
        long lastId = CompactHistory.CompressedIdBase;
        for (int i = days.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            List<Trade> list;
            try
            {
                long tmp = CompactHistory.CompressedIdBase;
                list = CompactHistory.LoadDay(root, cacheRoot, Instrument, days[i], ref tmp, CompactMinutes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            if (count > 0 && count + list.Count > MaxCompactTrades) break;
            perDay.Add((days[i], list));
            count += list.Count;
        }

        perDay.Reverse();
        var res = new List<Trade>((int)Math.Min(count, int.MaxValue));
        foreach (var (day, list) in perDay)
        {
            var dayStart = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            DateTime cFrom = dayStart > from ? dayStart : from, cTo = dayStart.AddDays(1) < to ? dayStart.AddDays(1) : to;
            if (cTo <= cFrom) continue;
            // cubiertas: las horas que tienen operaciones (un día grabado a medias deja huecos para las velas)
            long? runStart = null, runEnd = null;
            foreach (var t in list)
            {
                long h = t.Time.Ticks / TimeSpan.TicksPerHour;
                if (runEnd == h) continue;
                if (runEnd != null && h == runEnd + 1)
                {
                    runEnd = h;
                    continue;
                }

                if (runStart != null) AddCovered(runStart.Value, runEnd!.Value);
                runStart = runEnd = h;
            }

            if (runStart != null) AddCovered(runStart.Value, runEnd!.Value);

            void AddCovered(long h0, long h1)
            {
                DateTime a = new(h0 * TimeSpan.TicksPerHour, DateTimeKind.Utc), b = new((h1 + 1) * TimeSpan.TicksPerHour, DateTimeKind.Utc);
                if (a < cFrom) a = cFrom;
                if (b > cTo) b = cTo;
                if (b > a) covered.Add((a, b));
            }

            // ids crecientes en todo el tramo (dentro de su rango: cuentan como reales para las velas)
            foreach (var t in list)
                if (t.Time >= from && t.Time < to)
                {
                    long id = CompactHistory.CompressedIdBase + (t.Time.Ticks - DateTime.UnixEpoch.Ticks) / 10 * 8;
                    if (id <= lastId) id = lastId + 1;
                    lastId = id;
                    res.Add(new Trade(id, t.Time, t.Price, t.Volume, t.Side));
                }
        }

        return res;
    }

    /// <summary>Saca de los huecos los tramos ya cubiertos (conserva el id de continuación del último hueco).</summary>
    private static List<(DateTime From, DateTime To, long? FromId)> SubtractCovered(List<(DateTime From, DateTime To, long? FromId)> gaps, List<(DateTime From, DateTime To)> covered)
    {
        if (covered.Count == 0) return gaps;
        var res = new List<(DateTime From, DateTime To, long? FromId)>();
        foreach (var (gFrom, gTo, id) in gaps)
            foreach (var (pf, pt) in KlineHistory.Subtract(new List<(DateTime From, DateTime To)> { (gFrom, gTo) }, covered))
                if (pt - pf >= TimeSpan.FromMinutes(1))
                    res.Add((pf, pt, pt == gTo ? id : null));
        return res;
    }

    /// <summary>
    /// Usar los archivos públicos diarios (Binance y Bybit) para los días cerrados que faltan. Se apaga en las pruebas.
    /// </summary>
    public static bool UsePublicArchives { get; set; } = true;

    /// <summary>
    /// Baja de los archivos públicos los días ya cerrados (UTC) que tocan [<paramref name="gFrom"/>, <paramref name="gTo"/>)
    /// y devuelve lo que queda para pedir a la API (hoy, y los días cuyo archivo todavía no se publicó).
    /// </summary>
    private async Task<List<(DateTime From, DateTime To, long? FromId)>> LoadFromArchivesAsync(DateTime gFrom, DateTime gTo, long? fromId,
        List<Trade> output, DateTime from, DateTime to, long maxIdExclusive, Action<double> progress, CancellationToken ct)
    {
        var rest = new List<(DateTime From, DateTime To, long? FromId)>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (!UsePublicArchives || !PublicArchives.Supports(Instrument.FeedId) || DateOnly.FromDateTime(gFrom) >= today)
        {
            rest.Add((gFrom, gTo, fromId));
            return rest;
        }

        var days = EachDay(gFrom, gTo).Where(d => d < today).ToList();
        DateTime cursor = gFrom;
        long? id = fromId;
        for (int i = 0; i < days.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var day = days[i];
            var dayStart = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var dayEnd = dayStart.AddDays(1);
            DateTime sFrom = gFrom > dayStart ? gFrom : dayStart, sTo = gTo < dayEnd ? gTo : dayEnd;
            if (sTo <= sFrom) continue;
            List<Trade>? list = null;
            // menos de una hora: no vale bajar el día entero
            if (sTo - sFrom >= TimeSpan.FromHours(1))
            {
                try
                {
                    int index = i;
                    list = await PublicArchives.DownloadDayAsync(Instrument, day, new Progress<double>(p => progress((index + Math.Clamp(p, 0, 1)) / days.Count)), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    list = null; // sin archivo (o sin red para él): queda para la API
                }
            }

            if (list == null)
            {
                if (cursor < sTo) rest.Add((cursor, sTo, id));
                cursor = sTo;
                id = null;
                continue;
            }

            if (cursor < sFrom) rest.Add((cursor, sFrom, id));
            foreach (var t in list)
                if (t.Id < maxIdExclusive && t.Time >= sFrom && t.Time < sTo && t.Time >= from && t.Time <= to) output.Add(t);
            cursor = sTo;
            id = null;
        }

        if (cursor < gTo) rest.Add((cursor, gTo, id));
        progress(1);
        return rest;
    }

    /// <summary>Rangos que faltan en <paramref name="local"/> (ordenado) para cubrir [from, to).</summary>
    public static List<(DateTime From, DateTime To, long? FromId)> FindGaps(IReadOnlyList<Trade> local, DateTime from, DateTime to, bool tailById)
    {
        var gaps = new List<(DateTime, DateTime, long?)>();
        if (local.Count == 0)
        {
            gaps.Add((from, to, null));
            return gaps;
        }

        if (local[0].Time - from > TimeSpan.FromMinutes(1)) gaps.Add((from, local[0].Time, null));
        for (int i = 1; i < local.Count; i++)
        {
            if (local[i].Time - local[i - 1].Time > TimeSpan.FromMinutes(3))
                gaps.Add((local[i - 1].Time, local[i].Time, null));
        }

        // demasiados cortes (instrumento poco líquido): conviene pedir todo de una
        if (gaps.Count > 20)
        {
            gaps.Clear();
            gaps.Add((from, to, null));
            return gaps;
        }

        if (to - local[^1].Time > TimeSpan.FromSeconds(1))
            gaps.Add((local[^1].Time, to, tailById ? local[^1].Id + 1 : null));
        return gaps;
    }

    /// <summary>
    /// Trae historia más vieja hasta <paramref name="from"/> (si hace falta): primero de los archivos
    /// grabados/descargados y lo que falte desde el feed. Al terminar se reconstruyen los gráficos.
    /// </summary>
    public bool ExtendHistory(DateTime from)
    {
        if (from >= HistoryFrom || IsLoadingHistory || IsExtending) return false;
        var ct = _cts?.Token ?? CancellationToken.None;
        DateTime until = Trades.Count > 0 ? Trades.FirstTime : Feed.Now;
        // si lo primero que hay es historia en velas (ids negativos), los ids no sirven para cortar
        long firstId = Trades.Count > 0 && Trades.GetId(0) > 0 ? Trades.GetId(0) : long.MaxValue;
        IsExtending = true;
        HistoryProgress = 0;
        _pendingFrom = from;
        _ = Task.Run(async () =>
        {
            var older = new List<Trade>();
            try
            {
                older = await LoadRangeAsync(from, until, firstId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HistoryError = ex.Message;
            }

            lock (_historyLock) _olderToMerge = older;
        }, ct);
        return true;
    }

    private (DateTime From, DateTime To, List<Trade> Trades)? _upgradeToMerge;

    /// <summary>
    /// Después de agrandar <see cref="TickWindow"/>: vuelve a pedir con operaciones reales la parte de la historia que se
    /// había cargado en velas y ahora entra en la ventana (así el footprint aparece sin volver a abrir la app).
    /// Devuelve false si no hay nada que cambiar o ya se está cargando algo.
    /// </summary>
    public bool UpgradeToTicks()
    {
        if (!_historyMerged || IsLoadingHistory || IsExtending || Trades.Count == 0 || !Feed.SupportsHistory || Feed is not IKlineHistory) return false;
        if (Trades.GetId(0) > 0) return false; // ya empieza con operaciones reales
        int firstReal = -1;
        for (int i = 0; i < Trades.Count; i++)
        {
            if (Trades.GetId(i) <= 0) continue;
            firstReal = i;
            break;
        }

        DateTime cut = (_anchor ?? Feed.Now) - TickWindow;
        DateTime from = cut > Trades.FirstTime ? cut : Trades.FirstTime;
        DateTime to = firstReal >= 0 ? Trades.GetTime(firstReal) : Trades.LastTime;
        if (to - from < TimeSpan.FromMinutes(5)) return false;
        var ct = _cts?.Token ?? CancellationToken.None;
        IsExtending = true;
        HistoryProgress = 0;
        _ = Task.Run(async () =>
        {
            List<Trade> list;
            try
            {
                list = await LoadRangeAsync(from, to, long.MaxValue, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HistoryError = ex.Message;
                list = new List<Trade>();
            }

            lock (_historyLock) _upgradeToMerge = (from, to, list);
        }, ct);
        return true;
    }

    private long _lastDataAt;
    private int _tradedSinceAnchor;

    /// <summary>Cuándo llegó el último dato en vivo (operación o libro), en hora real UTC.</summary>
    public DateTime LastDataAt => new(Interlocked.Read(ref _lastDataAt), DateTimeKind.Utc);

    /// <summary>Al abrir, el mercado estaba cerrado (la última operación era vieja) y todavía no volvió a operar.</summary>
    public bool MarketClosed => _anchor != null && Volatile.Read(ref _tradedSinceAnchor) == 0;

    /// <summary>Última operación conocida cuando el mercado estaba cerrado al abrir.</summary>
    public DateTime? ClosedAt => _anchor;

    /// <summary>
    /// Fuerza la reconexión al instante (por ejemplo al despertar la Mac). Los feeds que lo soportan cortan
    /// la conexión y rellenan lo perdido; los demás se vuelven a suscribir.
    /// </summary>
    public void Reconnect()
    {
        if (_subscription is IReconnectable r)
        {
            r.Reconnect();
            return;
        }

        if (_subscription == null || !Feed.IsLive) return;
        _subscription.Dispose();
        try
        {
            _subscription = Feed.Subscribe(Instrument, this);
        }
        catch (Exception ex)
        {
            SetStatus(FeedStatus.Error, ex.Message);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _subscription?.Dispose();
        _subscription = null;
        _recorder?.Dispose();
        _recorder = null;
    }

    private static IEnumerable<DateOnly> EachDay(DateTime fromUtc, DateTime toUtc)
    {
        for (var d = DateOnly.FromDateTime(fromUtc); d <= DateOnly.FromDateTime(toUtc); d = d.AddDays(1))
            yield return d;
    }

    // ----------------------------------------------------------------- IMarketDataSink (hilos del feed)

    void IMarketDataSink.OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
    {
        foreach (var t in trades) _incoming.Enqueue(t);
        _recorder?.WriteLive(trades);
        _lastDataAt = DateTime.UtcNow.Ticks;
        if (trades.Count > 0 && _anchor != null) Volatile.Write(ref _tradedSinceAnchor, 1);
    }

    void IMarketDataSink.OnDepth(Instrument instrument, DepthUpdate update)
    {
        _lastDataAt = DateTime.UtcNow.Ticks;
        _latestDepth = update.Snapshot;
        if (update.IsFullRefresh) _depthFull = true;
        foreach (var c in update.Changes) _depthChanges.Enqueue(c);
        // evita que la cola crezca sin límite si la UI está trabada
        while (_depthChanges.Count > 50_000) _depthChanges.TryDequeue(out _);
    }

    void IMarketDataSink.OnStatus(Instrument instrument, FeedStatus status, string? message) => SetStatus(status, message);

    private void SetStatus(FeedStatus status, string? message)
    {
        _status = status;
        _statusMessage = message;
        _statusDirty = true;
    }

    // ----------------------------------------------------------------- hilo de UI

    /// <summary>Aplica lo recibido. Llamar periódicamente desde el hilo de UI.</summary>
    public void Pump()
    {
        if (_statusDirty)
        {
            _statusDirty = false;
            StatusChanged?.Invoke();
        }

        if (!_historyMerged)
        {
            List<Trade>? hist;
            lock (_historyLock)
            {
                hist = _historyToMerge;
                _historyToMerge = null;
            }

            if (hist != null)
            {
                Trades.Clear();
                foreach (var t in hist) Trades.Add(t);
                _historyMerged = true;
                IsLoadingHistory = false;
                HistoryProgress = 1;
                DrainIncoming(skipKnown: true, raise: false);
                HistoryLoaded?.Invoke();
                StatusChanged?.Invoke();
            }
        }
        else
        {
            List<Trade>? older;
            lock (_historyLock)
            {
                older = _olderToMerge;
                _olderToMerge = null;
            }

            (DateTime From, DateTime To, List<Trade> Trades)? upgrade;
            lock (_historyLock)
            {
                upgrade = _upgradeToMerge;
                _upgradeToMerge = null;
            }

            if (upgrade is { } u)
            {
                // cambia el tramo [From, To) por lo que llegó (operaciones reales y, donde no hubo, velas)
                if (u.Trades.Count > 0)
                {
                    var current = new List<Trade>(Trades.Count);
                    for (int i = 0; i < Trades.Count; i++) current.Add(Trades.Get(i));
                    Trades.Clear();
                    foreach (var t in current)
                        if (t.Time < u.From) Trades.Add(t);
                    foreach (var t in u.Trades)
                        if (t.Time >= u.From && t.Time < u.To) Trades.Add(t);
                    foreach (var t in current)
                        if (t.Time >= u.To) Trades.Add(t);
                }

                IsExtending = false;
                HistoryProgress = 1;
                if (u.Trades.Count > 0) HistoryLoaded?.Invoke();
                StatusChanged?.Invoke();
            }

            if (older != null)
            {
                // reconstruye el almacén: historia vieja + lo que ya había
                if (older.Count > 0)
                {
                    var current = new List<Trade>(Trades.Count);
                    for (int i = 0; i < Trades.Count; i++) current.Add(Trades.Get(i));
                    Trades.Clear();
                    foreach (var t in older) Trades.Add(t);
                    foreach (var t in current) Trades.Add(t);
                }

                HistoryFrom = _pendingFrom;
                IsExtending = false;
                HistoryProgress = 1;
                if (older.Count > 0) HistoryLoaded?.Invoke();
                StatusChanged?.Invoke();
            }

            DrainIncoming(skipKnown: true, raise: true);
        }

        var depth = _latestDepth;
        if (depth != null && !ReferenceEquals(depth, Depth))
        {
            Depth = depth;
            var changes = new List<BookChange>();
            while (_depthChanges.TryDequeue(out var c)) changes.Add(c);
            bool full = _depthFull;
            _depthFull = false;
            DepthUpdated?.Invoke(new DepthUpdate { Snapshot = depth, Changes = changes, IsFullRefresh = full });
        }
    }

    private void DrainIncoming(bool skipKnown, bool raise)
    {
        if (_incoming.IsEmpty) return;
        int start = Trades.Count;
        long lastId = Trades.LastId;
        long lastTicks = Trades.Count > 0 ? Trades.GetTimeTicks(Trades.Count - 1) : long.MinValue;
        while (_incoming.TryDequeue(out var t))
        {
            // ya estaba: id conocido y no más nuevo que lo último guardado. Si un id raro quedó adelante
            // (archivo dañado), lo nuevo de verdad se reconoce por el tiempo y no se pierde el vivo.
            if (skipKnown && t.Id <= lastId && t.Time.Ticks <= lastTicks) continue;
            Trades.Add(t);
            lastId = Math.Max(lastId, t.Id);
            if (t.Time.Ticks > lastTicks) lastTicks = t.Time.Ticks;
        }

        int added = Trades.Count - start;
        if (added > 0 && raise) TradesAppended?.Invoke(start, added);
    }

    public void Dispose() => Stop();
}

/// <summary>Graba ticks en disco en un hilo aparte (no traba la UI).</summary>
public sealed class TickRecorder : IDisposable
{
    private readonly string _root;
    private readonly Instrument _instrument;
    private readonly BlockingCollection<Trade> _queue = new(new ConcurrentQueue<Trade>());
    private readonly Thread? _thread;
    private readonly FileStream? _lock;
    private TickFile.Writer? _writer;
    private DateOnly _day;

    public TickRecorder(string root, Instrument instrument)
    {
        _root = root;
        _instrument = instrument;
        // un solo proceso graba cada instrumento: si otro Trading Hub ya lo graba, este solo lee
        _lock = TickFile.TryLock(root, instrument.FeedId, instrument.Symbol);
        if (_lock == null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "recorder-" + instrument.Symbol };
        _thread.Start();
    }

    /// <summary>False si otro proceso ya graba este instrumento (no se escribe nada).</summary>
    public bool IsActive => _lock != null;

    public void WriteLive(IReadOnlyList<Trade> trades)
    {
        if (_lock == null) return;
        foreach (var t in trades) _queue.TryAdd(t);
    }

    public void WriteHistory(IReadOnlyList<Trade> trades)
    {
        if (_lock == null) return;
        foreach (var t in trades) _queue.TryAdd(t);
    }

    private void Run()
    {
        try
        {
            foreach (var t in _queue.GetConsumingEnumerable())
            {
                var day = DateOnly.FromDateTime(t.Time);
                if (_writer == null || day != _day)
                {
                    _writer?.Dispose();
                    _day = day;
                    _writer = new TickFile.Writer(TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day), _instrument);
                }

                _writer.Write(t);
                if (_queue.Count == 0) _writer.Flush();
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _writer?.Dispose();
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread?.Join(2000);
        _lock?.Dispose();
    }
}

/// <summary>Administra los <see cref="InstrumentData"/> abiertos (uno por feed+símbolo).</summary>
public sealed class MarketDataHub : IDisposable
{
    private readonly Dictionary<string, InstrumentData> _items = new();

    public string? RecordingRoot { get; set; }

    /// <summary>Caché de los días resumidos por hora (ver <see cref="CompactHistory"/>).</summary>
    public string? CompactRoot { get; set; }

    /// <summary>Tramo del resumen de la historia vieja, en minutos (Ajustes → Datos).</summary>
    public int CompactMinutes { get; set; } = 1;

    /// <summary>
    /// Hasta qué antigüedad la historia trae operaciones reales (con footprint); lo anterior se completa con velas.
    /// Lo elige Ajustes → Datos.
    /// </summary>
    public TimeSpan MaxTickWindow { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Cambia la ventana de ticks también en los instrumentos abiertos y les trae lo que ahora entra en ella.</summary>
    public int ApplyTickWindow(TimeSpan window)
    {
        MaxTickWindow = window < TimeSpan.FromHours(2) ? TimeSpan.FromHours(2) : window;
        int upgrading = 0;
        foreach (var d in _items.Values)
        {
            bool grew = MaxTickWindow > d.TickWindow;
            d.TickWindow = MaxTickWindow;
            if (grew && d.UpgradeToTicks()) upgrading++;
        }

        return upgrading;
    }

    /// <summary>Si los instrumentos nuevos graban el vivo.</summary>
    public bool RecordLive { get; set; } = true;

    public IReadOnlyCollection<InstrumentData> Items => _items.Values;

    public InstrumentData GetOrStart(IMarketDataFeed feed, Instrument instrument, TimeSpan history)
    {
        if (_items.TryGetValue(instrument.Key, out var d)) return d;
        // ticks del exchange para lo reciente; lo más viejo se completa con velas (rápido aunque sean años). La ventana no
        // depende de cuánto se cargó al abrir: si después se pide más historia (más días, o arrastrando hacia atrás), lo
        // que cae dentro de la ventana también llega con sus operaciones y se ve con footprint
        var window = MaxTickWindow < TimeSpan.FromHours(2) ? TimeSpan.FromHours(2) : MaxTickWindow;
        d = new InstrumentData(instrument, feed) { RecordingRoot = RecordingRoot, CompactRoot = CompactRoot, CompactMinutes = CompactMinutes, RecordLive = RecordLive, TickWindow = window };
        _items[instrument.Key] = d;
        d.Start(feed.Now - history);
        return d;
    }

    public void Release(InstrumentData data)
    {
        // los datos se mantienen mientras la app esté abierta (cambiar de gráfico es instantáneo)
    }

    public void PumpAll()
    {
        foreach (var d in _items.Values) d.Pump();
    }

    /// <summary>Reconecta todos los instrumentos en vivo (despertó la Mac, volvió internet...).</summary>
    public void ReconnectAll()
    {
        foreach (var d in _items.Values) d.Reconnect();
    }

    public void Dispose()
    {
        foreach (var d in _items.Values) d.Dispose();
        _items.Clear();
    }
}
