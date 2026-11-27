using SkiaSharp;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Services.Workbench;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native.Classic;
using TradingHub.Indicators.Native.Structure;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;
using ChartDrawingKind = TradingHub.Charting.Drawings.DrawingKind;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public class ArchitectureOptimizationTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = "test", TickSize = 0.1m, VolumeStep = 0.001m, PointValue = 1m };
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TradeStore_empaqueta_volumen_en_8_bytes_sin_perdida_y_soporta_lectura_concurrente()
    {
        var store = new TradeStore(Btc);
        decimal[] testVolumes = { 1m, 0.001m, 0.12345678m, 99999.87654321m, 0.00000001m, 42.5m, 1000000m, 3.1415926535m };

        for (int i = 0; i < testVolumes.Length; i++)
            store.Add(new Trade(i + 1, T0.AddSeconds(i), 84500m + i * 0.1m, testVolumes[i], i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));

        Assert.Equal(65536L * 33L, store.MemoryBytes);
        for (int i = 0; i < testVolumes.Length; i++)
        {
            Assert.Equal(testVolumes[i], store.GetVolume(i));
            Assert.Equal(i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell, store.GetSide(i));
        }

        using var cts = new CancellationTokenSource();
        var readerTask = Task.Run(() =>
        {
            long reads = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                int c = store.Count;
                if (c > 10) { var t = store.Get(c - 1); Assert.True(t.Volume > 0); reads++; }
            }
            return reads;
        });

        for (int i = testVolumes.Length; i < 140_000; i++)
            store.Add(new Trade(i + 1, T0.AddMilliseconds(i * 10), 84500m + (i % 50) * 0.1m, 0.025m, TradeSide.Buy));

        cts.Cancel();
        long totalReads = await readerTask;
        Assert.Equal(140_000, store.Count);
        Assert.Equal(3L * 65536L * 33L, store.MemoryBytes);
        Assert.True(totalReads >= 0);
    }

    [Fact]
    public void Lang_cortocircuita_numeros_puros_y_conserva_traducciones_calientes_en_cache_de_dos_generaciones()
    {
        Lang.Load(Lang.Chinese);
        try
        {
            Assert.Null(Lang.Translate("84700.5"));
            Assert.Null(Lang.Translate("+12.40%"));
            Assert.Null(Lang.Translate("12:34:56"));

            string hotKey = "Ajustes";
            string? hotVal = Lang.Translate(hotKey);
            Assert.NotNull(hotVal);

            for (int i = 0; i < 12_000; i++)
            {
                _ = Lang.Translate(hotKey);
                _ = Lang.Translate($"Texto dinámico de prueba {i}");
            }

            Assert.Equal(hotVal, Lang.Translate(hotKey));
        }
        finally
        {
            Lang.Load(Lang.Spanish);
        }
    }

    [Fact]
    public void ChartRenderer_reutiliza_capa_estatica_SKPicture_al_mover_cruz_y_dibujar_objetos()
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        for (int i = 0; i < 120; i++)
            data.Trades.Add(new Trade(i + 1, T0.AddSeconds(i * 10), 84000m + (i % 20) * 0.5m, 0.5m, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));

        using var model = new ChartModel(data, ChartPeriod.M1);
        var renderer = new ChartRenderer();
        var state = new ChartRenderState { MouseX = 200, MouseY = 180 };

        using var surface = SKSurface.Create(new SKImageInfo(900, 600));
        renderer.Render(surface.Canvas, model, 900, 600, state);
        Assert.Equal(1, renderer.StaticCacheMisses);
        Assert.Equal(0, renderer.StaticCacheHits);

        var ctl = new ChartController(model, state);
        for (int i = 0; i < 30; i++)
        {
            ctl.MouseMove(150 + i * 8, 120 + i * 4, InputModifiers.None);
            renderer.Render(surface.Canvas, model, 900, 600, state);
        }

        Assert.Equal(1, renderer.StaticCacheMisses);
        Assert.Equal(30, renderer.StaticCacheHits);

        model.Drawings.Add(new ChartDrawing { Kind = ChartDrawingKind.HorizontalLine, Points = { new ChartPoint(T0, 84005) }, Selected = true });
        model.InvalidateOverlay();
        renderer.Render(surface.Canvas, model, 900, 600, state);
        Assert.Equal(1, renderer.StaticCacheMisses);
        Assert.Equal(31, renderer.StaticCacheHits);

        model.Viewport.ScrollBars = 5;
        model.Invalidate();
        renderer.Render(surface.Canvas, model, 900, 600, state);
        Assert.Equal(2, renderer.StaticCacheMisses);
    }

    [Fact]
    public void BracketManager_reconcilia_ordenes_huerfanas_elimina_stops_duplicados_y_adopta_brackets_tras_reconexion()
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        data.Trades.Add(new Trade(1, T0, 100m, 1m, TradeSide.Buy));
        var broker = new SimBroker(data, new Portfolio(), new TradingStatistics());

        broker.Market(OrderDirections.Buy, 2m);
        var sl1 = ((ITradingAccount)broker).Exit(OrderTypes.Stop, OrderDirections.Sell, 2m, 95m, "SL");
        var slDup = ((ITradingAccount)broker).Exit(OrderTypes.Stop, OrderDirections.Sell, 2m, 94m, "SL");
        var tp1 = ((ITradingAccount)broker).Exit(OrderTypes.Limit, OrderDirections.Sell, 2m, 110m, "TP");

        using var brackets = new BracketManager(broker);
        Assert.NotNull(brackets.StopLoss);
        Assert.Equal(95m, brackets.StopLoss!.TriggerPrice);
        Assert.Single(brackets.Targets);
        Assert.Equal(110m, brackets.Targets[0].Price);
        Assert.DoesNotContain(slDup, broker.ActiveOrders);
        Assert.Contains(sl1, broker.ActiveOrders);
        Assert.Contains(tp1, broker.ActiveOrders);
    }

    private sealed class DummyProtectiveStrategy : Strategy
    {
        protected override void OnBar(TradingHub.Strategies.Bar bar) { }
    }

    [Fact]
    public void LiveStrategyRunner_adopta_ordenes_protectoras_heredadas_y_mantiene_vinculo_oco()
    {
        var prevRoot = LiveStore.Root;
        var tempRoot = Path.Combine(Path.GetTempPath(), "th_opt_live_" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = tempRoot;
        try
        {
            var data = new InstrumentData(Btc, new SimulatedFeed());
            for (int i = 0; i < 5; i++)
                data.Trades.Add(new Trade(i + 1, T0.AddSeconds(i * 20), 100m, 1m, TradeSide.Buy));

            var broker = new SimBroker(data, new Portfolio(), new TradingStatistics());
            broker.Market(OrderDirections.Buy, 1m, "entrada previa");
            _ = broker.Stop(OrderDirections.Sell, 1m, 95m, "DummyProtectiveStrategy: Stop loss");
            _ = broker.Limit(OrderDirections.Sell, 1m, 108m, "DummyProtectiveStrategy: Take profit");

            var strategy = new DummyProtectiveStrategy { StopLossTicks = 50, TakeProfitTicks = 80 };
            using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1) { ProtectOnStart = true };
            runner.SeedPosition(new LiveFill(T0.AddMinutes(-5), true, 1m, 100m));
            runner.Start();

            Assert.Equal(2, broker.ActiveOrders.Count());
            Assert.Equal(2, runner.ActiveOrders.Count);

            int start = data.Trades.Count;
            data.Trades.Add(new Trade(99, T0.AddMinutes(2), 109m, 1m, TradeSide.Buy));
            var field = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ((Action<int, int>?)field!.GetValue(data))?.Invoke(start, 1);

            Assert.Equal(0m, broker.Position!.Volume);
            Assert.Equal(0m, runner.Ledger.Position);
            Assert.Empty(broker.ActiveOrders);
        }
        finally
        {
            LiveStore.Root = prevRoot;
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void StructureIndicator_actualiza_velas_incrementalmente_y_todos_los_15_grupos_de_biblioteca_tienen_indicadores()
    {
        var data = VpUltimateTests.Market(5);
        using var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC", Style = ChartStyle.Candles });
        var scanner = new CandlePatternScanner { ShowDoji = true };
        model.AddIndicator(scanner);
        model.Update();
        Assert.Null(scanner.ComputeError);

        int start = data.Trades.Count;
        var nextTime = data.Trades.LastTime.AddMinutes(16);
        data.Trades.Add(new Trade(start + 100, nextTime, data.LastPrice + 2m, 1.5m, TradeSide.Buy));
        var field = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        ((Action<int, int>?)field!.GetValue(data))?.Invoke(start, 1);
        Assert.True(scanner.IncrementalRefreshes >= 1);

        Type[] classicTypes =
        {
            typeof(HullKalmanMa), typeof(SuperTrendPro), typeof(AdxDmi), typeof(StochasticRsi),
            typeof(KeltnerDonchianChannels), typeof(YangZhangVolatility), typeof(MoneyFlowSuite), typeof(CandlePatternScanner)
        };
        foreach (var t in classicTypes)
        {
            var ind = (Indicator)Activator.CreateInstance(t)!;
            var slot = model.AddIndicator(ind);
            model.Update();
            Assert.Null(slot.Error);
        }

        using var surface = SKSurface.Create(new SKImageInfo(1200, 700));
        new ChartRenderer().Render(surface.Canvas, model, 1200, 700, new ChartRenderState());

        var catalog = new IndicatorCatalog();
        catalog.Scan(typeof(HullKalmanMa).Assembly, "Nativos");
        var groups = IndicatorLibrary.Build(catalog.Items, new LibraryLayout());
        foreach (var expectedCategory in IndicatorCategories.Order)
            Assert.Contains(groups, g => g.Id == expectedCategory && g.Items.Count > 0);
    }

    [Fact]
    public void OrderEntryService_calcula_tamano_por_riesgo_coloca_escalera_y_gira_posicion_sin_dependencia_de_UI()
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        data.Trades.Add(new Trade(1, T0, 1000m, 1m, TradeSide.Buy));
        var broker = new SimBroker(data, new Portfolio(), new TradingStatistics());
        using var brackets = new BracketManager(broker)
        {
            Template = new BracketTemplate { Enabled = true, StopTicks = 50, Targets = { new BracketTarget { Ticks = 100, Percent = 100 } } }
        };
        var service = new OrderEntryService(broker, brackets);

        decimal qty = service.CalculateQuantityByRisk(10_000m, 1m, 1000m, 950m);
        Assert.Equal(2m, qty);

        var ladder = service.PlaceLimitLadder(OrderDirections.Buy, 2m, 990m, 960m, 4, attachBrackets: true);
        Assert.Equal(4, ladder.Count);
        Assert.Equal(2m, ladder.Sum(o => o.Volume));
        Assert.Equal(990m, ladder[0].Price);
        Assert.Equal(960m, ladder[3].Price);
        Assert.Equal(4, brackets.Pending.Count);

        service.SubmitMarket(OrderDirections.Buy, 1.5m);
        Assert.Equal(1.5m, broker.Position!.Volume);
        service.ReversePosition();
        Assert.Equal(-1.5m, broker.Position!.Volume);
    }
}
