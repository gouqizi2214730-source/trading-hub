using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Historia larga: velas del exchange convertidas en operaciones y mezcladas con ticks reales.</summary>
public class LongHistoryTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 0.5m, VolumeStep = 0.001m };

    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    [Fact]
    public void Las_velas_armadas_respetan_ohlc_volumen_y_delta()
    {
        var t0 = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var klines = new List<Kline>
        {
            new(Ms(t0), 100, 104, 99, 103, 10, 7, true),               // alcista: compra 7, vende 3
            new(Ms(t0.AddMinutes(1)), 103, 103.5m, 97, 98, 4, 0, true), // solo ventas
            new(Ms(t0.AddMinutes(2)), 98, 99, 98, 99, 2, 2, true),      // solo compras
        };
        var trades = new List<Trade>();
        KlineHistory.Synthesize(klines, Inst, 1, new[] { (t0, t0.AddMinutes(3)) }, trades);
        Assert.Equal(12, trades.Count);
        Assert.All(trades, t => Assert.True(KlineHistory.IsSynthetic(t.Id)));
        Assert.True(trades.Zip(trades.Skip(1)).All(p => p.First.Time < p.Second.Time && p.First.Id < p.Second.Id));

        var store = new TradeStore(Inst);
        foreach (var t in trades) store.Add(t);
        var m1 = new CandleSeries(Inst, ChartPeriod.M1);
        m1.Rebuild(store);
        Assert.Equal(3, m1.Count);
        for (int i = 0; i < 3; i++)
        {
            var k = klines[i];
            var c = m1[i];
            Assert.True(c.IsSynthetic);
            Assert.Equal(k.Open, m1.TicksToPrice(c.OpenTicks));
            Assert.Equal(k.High, m1.TicksToPrice(c.HighTicks));
            Assert.Equal(k.Low, m1.TicksToPrice(c.LowTicks));
            Assert.Equal(k.Close, m1.TicksToPrice(c.CloseTicks));
            Assert.Equal(k.Volume, c.Volume);
            Assert.Equal(k.BuyVolume - (k.Volume - k.BuyVolume), c.Delta);
            Assert.Equal(0m, c.MaxTradeVolume); // no cuentan como "big trades"
        }

        // agregadas en una vela de una hora: totales exactos
        var h1 = new CandleSeries(Inst, ChartPeriod.H1);
        h1.Rebuild(store);
        Assert.Single(h1.Candles);
        Assert.Equal(16m, h1[0].Volume);
        Assert.Equal(104m, h1.TicksToPrice(h1[0].HighTicks));
        Assert.Equal(97m, h1.TicksToPrice(h1[0].LowTicks));
        Assert.Equal((7 + 0 + 2) - (3 + 4 + 0), h1[0].Delta);
    }

    [Fact]
    public void Las_velas_solo_llenan_los_huecos_sin_ticks()
    {
        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var real = new List<Trade>
        {
            new(1, t0.AddMinutes(30), 100, 1, TradeSide.Buy),
            new(2, t0.AddMinutes(31), 100, 1, TradeSide.Buy),
            new(3, t0.AddMinutes(32), 100, 1, TradeSide.Buy),
        };
        var coverage = KlineHistory.Coverage(real);
        Assert.Single(coverage);
        var windows = KlineHistory.Subtract(new[] { (t0, t0.AddHours(1)) }, coverage);
        Assert.Equal(2, windows.Count);
        Assert.Equal(t0.AddMinutes(30), windows[0].To);
        Assert.Equal(t0.AddMinutes(33), windows[1].From);

        var klines = Enumerable.Range(0, 60).Select(i => new Kline(Ms(t0.AddMinutes(i)), 100, 101, 99, 100, 1, 0.5m, true)).ToList();
        var synth = new List<Trade>();
        KlineHistory.Synthesize(klines, Inst, 1, windows, synth);
        // 30 minutos antes y 27 después (los minutos con ticks reales no se inventan)
        Assert.Equal((30 + 27) * 4, synth.Count);
        var merged = KlineHistory.MergeByTime(synth, real);
        Assert.True(merged.Zip(merged.Skip(1)).All(p => p.First.Time <= p.Second.Time));
    }

    /// <summary>Feed falso: ticks reales solo para lo pedido y velas de 1 minuto para todo.</summary>
    private sealed class FakeFeed : IMarketDataFeed, IKlineHistory
    {
        public int TickRequests;
        public DateTime EarliestTickRequested = DateTime.MaxValue;
        public string Id => "test";
        public string DisplayName => "Test";
        public DateTime Now => DateTime.UtcNow;
        public bool SupportsHistory => true;
        public bool Live { get; set; }

        public bool IsLive => Live;

        /// <summary>Como Bybit: la API solo da los últimos minutos.</summary>
        public bool RecentOnly { get; set; }

        public bool RecentTradesOnly => RecentOnly;

        /// <summary>Sin red hacia el exchange (como api.bybit.com bloqueado): todo pedido REST falla.</summary>
        public bool Offline { get; set; }
        public void Dispose() { }
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[] { Inst });
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) => new Nothing();
        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(DateTime.UtcNow.AddYears(-1));

        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            TickRequests++;
            if (Offline) throw new HttpRequestException("The SSL connection could not be established, see inner exception.");
            if (fromUtc < EarliestTickRequested) EarliestTickRequested = fromUtc;
            var list = new List<Trade>();
            long id = 1_000_000 + (long)(fromUtc - DateTime.UnixEpoch).TotalSeconds;
            var start = RecentOnly && toUtc - fromUtc > TimeSpan.FromMinutes(10) ? toUtc.AddMinutes(-10) : fromUtc;
            for (var t = start; t < toUtc; t = t.AddSeconds(20)) list.Add(new Trade(id++, t, 100, 0.01m, TradeSide.Buy));
            onBatch(list);
            return Task.CompletedTask;
        }

        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice, Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("The SSL connection could not be established, see inner exception.");
            var list = new List<Kline>();
            long step = minutes * 60_000L;
            for (long t = Ms(fromUtc) / step * step; t < Ms(toUtc); t += step) list.Add(new Kline(t, 100, 101, 99, 100, 1, 0.6m, true));
            onBatch(list);
            return Task.CompletedTask;
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public void Un_anio_de_historia_carga_en_velas_y_lo_reciente_en_ticks()
    {
        var feed = new FakeFeed();
        var data = new InstrumentData(Inst, feed) { TickWindow = TimeSpan.FromHours(1) };
        data.Start(feed.Now.AddMinutes(-30));
        for (int i = 0; i < 400 && data.IsLoadingHistory; i++)
        {
            data.Pump();
            Thread.Sleep(10);
        }

        Assert.False(data.IsLoadingHistory);
        int initial = data.Trades.Count;
        Assert.True(initial > 0);

        // pedir un año: lo de más de una hora atrás viene en velas
        Assert.True(data.ExtendHistory(feed.Now.AddDays(-365)));
        for (int i = 0; i < 1000 && data.IsExtending; i++)
        {
            data.Pump();
            Thread.Sleep(10);
        }

        Assert.False(data.IsExtending);
        Assert.Null(data.HistoryError);
        Assert.True(feed.EarliestTickRequested >= feed.Now.AddHours(-1.1), "no pidió ticks de un año atrás");
        Assert.True(data.Trades.FirstTime <= feed.Now.AddDays(-364));
        Assert.True(data.Trades.Count < 3_000_000);

        var series = new CandleSeries(Inst, ChartPeriod.H1);
        series.Rebuild(data.Trades);
        Assert.True(series[0].IsSynthetic);
        Assert.False(series[series.Count - 1].IsSynthetic);
    }

    private static void Wait(InstrumentData data, Func<bool> busy)
    {
        for (int i = 0; i < 1500 && busy(); i++)
        {
            data.Pump();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Agrandar_la_ventana_cambia_las_velas_por_ticks_sin_reabrir()
    {
        var feed = new FakeFeed();
        var data = new InstrumentData(Inst, feed) { TickWindow = TimeSpan.FromHours(2) };
        data.Start(feed.Now.AddHours(-1));
        Wait(data, () => data.IsLoadingHistory);
        Assert.True(data.ExtendHistory(feed.Now.AddDays(-3)));
        Wait(data, () => data.IsExtending);
        var before = new CandleSeries(Inst, ChartPeriod.H1);
        before.Rebuild(data.Trades);
        int synthBefore = Enumerable.Range(0, before.Count).Count(i => before[i].IsSynthetic);
        Assert.True(synthBefore > 60);

        // la ventana pasa a 2 días: lo de los últimos 2 días se vuelve a pedir con ticks
        int loaded = 0;
        data.HistoryLoaded += () => loaded++;
        data.TickWindow = TimeSpan.FromDays(2);
        Assert.True(data.UpgradeToTicks());
        Wait(data, () => data.IsExtending);
        Assert.Null(data.HistoryError);
        Assert.Equal(1, loaded);
        Assert.True(feed.EarliestTickRequested <= feed.Now.AddDays(-2).AddMinutes(5));

        var after = new CandleSeries(Inst, ChartPeriod.H1);
        after.Rebuild(data.Trades);
        for (int i = 0; i < after.Count; i++)
        {
            bool old = after[i].Time < feed.Now.AddDays(-2).AddHours(-1);
            if (!old) Assert.False(after[i].IsSynthetic, $"vela {after[i].Time:dd HH:mm} sigue en velas");
        }

        Assert.True(after.Count >= before.Count - 1); // no se perdieron velas
        for (int i = 1; i < data.Trades.Count; i++) Assert.True(data.Trades.GetTime(i) >= data.Trades.GetTime(i - 1));
        Assert.False(data.UpgradeToTicks()); // ya no queda nada para cambiar dentro de la ventana
    }

    [Fact]
    public void Si_la_API_solo_da_lo_ultimo_el_resto_de_la_ventana_va_en_velas_y_no_queda_vacio()
    {
        var feed = new FakeFeed { RecentOnly = true };
        var data = new InstrumentData(Inst, feed) { TickWindow = TimeSpan.FromHours(6) };
        data.Start(feed.Now.AddHours(-5));
        Wait(data, () => data.IsLoadingHistory);
        var series = new CandleSeries(Inst, ChartPeriod.M15);
        series.Rebuild(data.Trades);
        Assert.True(data.Trades.FirstTime <= feed.Now.AddHours(-4.9), $"empieza en {data.Trades.FirstTime:HH:mm}");
        // 5 horas de velas de 15 minutos, sin huecos
        Assert.True(series.Count >= 19, $"velas: {series.Count}");
        Assert.True(series[0].IsSynthetic);
        Assert.False(series[series.Count - 1].IsSynthetic);
    }

    [Fact]
    public void Resumir_por_hora_conserva_OHLC_volumen_delta_maximo_y_minimo_de_cada_hora()
    {
        var rnd = new Random(7);
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var raw = new List<Trade>();
        decimal px = 80000m;
        long id = 1;
        for (int s = 0; s < 3 * 3600; s += 2)
        {
            px += (rnd.Next(-30, 31)) * 0.1m;
            raw.Add(new Trade(id++, t0.AddSeconds(s), px, rnd.Next(1, 50) / 1000m, rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        var c = CompactHistory.Compress(raw, 0.1m, 60);
        Assert.True(c.Count < raw.Count / 3, $"{c.Count} de {raw.Count}");
        // por minuto: cada minuto conserva apertura, cierre, máximo, mínimo y volumen
        var m = CompactHistory.Compress(raw, 0.1m, 1);
        for (int k = 0; k < 180; k += 17)
        {
            var a = raw.Where(t => (int)(t.Time - t0).TotalMinutes == k).ToList();
            var b = m.Where(t => (int)(t.Time - t0).TotalMinutes == k).ToList();
            Assert.Equal(a[0].Price, b[0].Price);
            Assert.Equal(a[^1].Price, b[^1].Price);
            Assert.Equal(a.Max(t => t.Price), b.Max(t => t.Price));
            Assert.Equal(a.Min(t => t.Price), b.Min(t => t.Price));
            Assert.Equal(a.Sum(t => t.Volume), b.Sum(t => t.Volume));
        }
        for (int h = 0; h < 3; h++)
        {
            var a = raw.Where(t => t.Time.Hour == h).ToList();
            var b = c.Where(t => t.Time.Hour == h).ToList();
            Assert.Equal(a[0].Price, b[0].Price); // apertura
            Assert.Equal(a[^1].Price, b[^1].Price); // cierre
            Assert.Equal(a.Max(t => t.Price), b.Max(t => t.Price));
            Assert.Equal(a.Min(t => t.Price), b.Min(t => t.Price));
            Assert.Equal(a.Sum(t => t.Volume), b.Sum(t => t.Volume));
            Assert.Equal(a.Where(t => t.Side == TradeSide.Buy).Sum(t => t.Volume), b.Where(t => t.Side == TradeSide.Buy).Sum(t => t.Volume));
            for (int i = 1; i < b.Count; i++) Assert.True(b[i].Time >= b[i - 1].Time);
        }
    }

    [Fact]
    public void Dias_viejos_con_ticks_se_cargan_resumidos_con_footprint_y_los_recientes_enteros()
    {
        var root = Path.Combine(Path.GetTempPath(), "th-compact-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var inst = new Instrument { Symbol = "TEST", FeedId = "test", TickSize = 0.5m, VolumeStep = 0.001m };
            var now = DateTime.UtcNow;
            var from = new DateTime(now.AddDays(-5).Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour, DateTimeKind.Utc);
            var rnd = new Random(3);
            decimal px = 100m, rawVolume = 0;
            long id = 1;
            for (var day = DateOnly.FromDateTime(now.AddDays(-6)); day <= DateOnly.FromDateTime(now); day = day.AddDays(1))
            {
                var list = new List<Trade>();
                var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                for (var t = start; t < start.AddDays(1) && t < now; t = t.AddSeconds(10))
                {
                    px = Math.Max(50, px + rnd.Next(-2, 3) * 0.5m);
                    var v = rnd.Next(1, 9) / 100m;
                    list.Add(new Trade(id++, t, px, v, rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell));
                    if (t >= from) rawVolume += v;
                }

                HistoricalData.WriteDay(root, inst, day, list);
            }

            var feed = new FakeFeed { Live = true };
            var data = new InstrumentData(inst, feed)
            {
                RecordingRoot = root, CompactRoot = Path.Combine(root, "compact"), CompactMinutes = 15, RecordLive = false, TickWindow = TimeSpan.FromDays(1),
            };
            data.Start(from);
            Wait(data, () => data.IsLoadingHistory);
            Assert.Null(data.HistoryError);
            Assert.Equal(0, feed.TickRequests > 0 && feed.EarliestTickRequested < now.AddDays(-1) ? 1 : 0); // no pidió nada viejo

            int compact = 0, real = 0;
            for (int i = 0; i < data.Trades.Count; i++)
            {
                long tid = data.Trades.GetId(i);
                if (CompactHistory.IsCompressed(tid)) compact++;
                else if (tid > 0) real++;
                Assert.False(KlineHistory.IsSynthetic(tid), "no debería haber velas del exchange");
            }

            Assert.True(compact > 0 && real > 0);
            Assert.True(File.Exists(CompactHistory.CachePath(Path.Combine(root, "compact"), "test", "TEST", DateOnly.FromDateTime(now.AddDays(-3)), 15)));

            // velas de 15 minutos (el tramo del resumen): todas con footprint, mismo volumen total que los ticks crudos
            var series = new CandleSeries(inst, ChartPeriod.Parse("M15"));
            series.Rebuild(data.Trades);
            decimal vol = 0;
            for (int i = 0; i < series.Count; i++)
            {
                Assert.False(series[i].IsSynthetic, $"{series[i].Time:dd HH}");
                vol += series[i].Volume;
            }

            Assert.InRange(vol, rawVolume - 0.5m, rawVolume + 2m); // + lo que el feed falso agrega al final
            Assert.True(data.Trades.Count < 5 * 8640, $"operaciones: {data.Trades.Count}");
            Assert.True(series.Count >= 5 * 96 - 2, $"velas: {series.Count}"); // sin huecos
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Si_el_exchange_no_responde_igual_se_muestra_lo_bajado()
    {
        var root = Path.Combine(Path.GetTempPath(), "th-offline-" + Guid.NewGuid().ToString("N")[..8]);
        var kroot = KlineHistory.CacheRoot;
        try
        {
            KlineHistory.CacheRoot = Path.Combine(root, "klines");
            var inst = new Instrument { Symbol = "TEST", FeedId = "test", TickSize = 0.5m, VolumeStep = 0.001m };
            var now = DateTime.UtcNow;
            long id = 1;
            for (var day = DateOnly.FromDateTime(now.AddDays(-10)); day <= DateOnly.FromDateTime(now.AddDays(-1)); day = day.AddDays(1))
            {
                var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var list = new List<Trade>();
                for (var t = start; t < start.AddDays(1); t = t.AddMinutes(1)) list.Add(new Trade(id++, t, 100, 0.01m, TradeSide.Buy));
                HistoricalData.WriteDay(root, inst, day, list);
            }

            var feed = new FakeFeed { Live = true, RecentOnly = true, Offline = true };
            var data = new InstrumentData(inst, feed) { RecordingRoot = root, CompactRoot = Path.Combine(root, "compact"), RecordLive = false };
            data.Start(now.AddDays(-400)); // «todo el historial»
            Wait(data, () => data.IsLoadingHistory);
            Assert.NotNull(data.HistoryError); // se avisa
            Assert.True(data.Trades.Count > 0, "lo de disco no debería perderse por un error de red");
            Assert.True(data.Trades.FirstTime <= now.AddDays(-9.9));
        }
        finally
        {
            KlineHistory.CacheRoot = kroot;
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
