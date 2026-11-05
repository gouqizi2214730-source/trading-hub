using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.External;
using TradingHub.Indicators;
using TradingHub.Indicators.Native.Macro;
using TradingHub.Indicators.Native.Options;
using TradingHub.Indicators.Native.Structure;
using Xunit;

namespace TradingHub.Tests;

/// <summary>
/// Los indicadores de opciones y de macro con datos de prueba (una cadena, operaciones, historia diaria y fotos por
/// ejercicio inventadas alrededor del mercado simulado): calculan, dibujan, no fallan y no se redibujan hacia atrás.
/// </summary>
public class OptionsIndicatorsTests
{
    private static readonly object Gate = new();
    private static bool _seeded;

    internal static void Seed()
    {
        lock (Gate)
        {
            if (_seeded) return;
            _seeded = true;
            OptionsData.MaxTradeDays = 100_000;
            var data = VpUltimateTests.Market(10);
            var t0 = data.Trades.FirstTime;
            var end = data.Trades.LastTime;
            double spot = (double)data.Trades.GetPrice(data.Trades.Count - 1);
            var rnd = new Random(7);
            var expiries = new[] { 1, 3, 7, 14, 30, 60, 90 }.Select(d => end.Date.AddDays(d).AddHours(8)).ToList();
            var contracts = new List<OptionContract>();
            foreach (var e in expiries)
                for (double k = Math.Round(spot * 0.7 / 1000) * 1000; k <= spot * 1.3; k += 1000)
                    foreach (var r in new[] { OptionRight.Call, OptionRight.Put })
                    {
                        double t = (e - end).TotalDays / 365, m = Math.Log(k / spot);
                        double iv = 0.5 + 0.6 * m * m - (r == OptionRight.Put ? 0.05 * m : 0) + 0.02 * rnd.NextDouble();
                        var (delta, gamma) = OptionsMath.Greeks(spot, k, t, iv, r);
                        double oi = 200 * Math.Exp(-m * m * 40) * (1 + rnd.NextDouble()) * (r == OptionRight.Put && k < spot ? 1.6 : 1);
                        double mark = Math.Max(5, spot * iv * Math.Sqrt(t) * 0.4 * Math.Exp(-Math.Abs(m) * 6));
                        contracts.Add(new OptionContract($"BTC-{e:ddMMMyy}-{k}-{(r == OptionRight.Call ? "C" : "P")}", e, k, r, mark * 0.98, mark * 1.02, mark, iv, delta, gamma, 0, 0, oi, oi * 0.3, 1));
                    }

            var chain = new OptionsChain("BTC", end, spot, contracts);
            // operaciones: unas cada 2 minutos en el precio de ese momento
            var trades = new List<OptionTrade>();
            int idx = 0;
            for (var t = t0; t < end; t = t.AddMinutes(2))
            {
                while (idx + 1 < data.Trades.Count && data.Trades.GetTime(idx + 1) <= t) idx++;
                double px = (double)data.Trades.GetPrice(idx);
                var e = expiries[rnd.Next(expiries.Count)];
                double k = Math.Round(px * (0.9 + 0.2 * rnd.NextDouble()) / 1000) * 1000;
                var r = rnd.Next(2) == 0 ? OptionRight.Call : OptionRight.Put;
                double size = Math.Round(0.1 + rnd.NextDouble() * (rnd.Next(40) == 0 ? 300 : 8), 1);
                double prem = Math.Max(5, px * 0.6 * Math.Sqrt(Math.Max(1, (e - t).TotalDays) / 365) * 0.4 * Math.Exp(-Math.Abs(Math.Log(k / px)) * 6));
                for (int f = 0; f < (rnd.Next(15) == 0 ? 4 : 1); f++)
                    trades.Add(new OptionTrade(t.AddMilliseconds(f * 150), $"BTC-{e:ddMMMyy}-{k}-{(r == OptionRight.Call ? "C" : "P")}", e, k, r, prem, size, rnd.Next(2) == 0 ? 1 : -1, 0.5 + 0.1 * rnd.NextDouble(), px, 1, rnd.Next(3) == 0 ? "OKX" : "Deribit"));
            }

            var daily = new List<OptionDaily>();
            for (var d = end.Date.AddDays(-400); d < end.Date; d = d.AddDays(1))
            {
                double iv = 0.45 + 0.15 * Math.Sin(d.DayOfYear / 30.0) + 0.05 * rnd.NextDouble();
                double cv = 5000 + 3000 * rnd.NextDouble(), pv = 4000 + 3000 * rnd.NextDouble();
                daily.Add(new OptionDaily(d, spot, iv, -3 + 6 * rnd.NextDouble(), pv / cv, 0.6 + 0.4 * rnd.NextDouble(), 100000, 80000, cv, pv, (rnd.NextDouble() - 0.4) * 5e8, spot * 1.05, spot * 0.95, spot * 0.99, spot * iv / 19));
            }

            var snaps = new List<StrikeSnapshot>();
            for (var t = t0; t <= end; t = t.AddMinutes(30))
            {
                var c = chain with { Time = t };
                snaps.Add(OptionsData.SnapshotOf(c) with { Time = t });
            }

            OptionsData.Seed("BTC", chain, trades, daily, snaps);

            EtfFlows.Seed("BTC", Enumerable.Range(0, 30).Select(i => new EtfFlowDay(end.Date.AddDays(-i), (rnd.NextDouble() - 0.45) * 4e8)));
            var kl = new List<Kline>();
            double eth = 2500;
            for (var t = t0.Date; t < end.AddHours(1); t = t.AddMinutes(1))
            {
                double o = eth;
                eth = Math.Max(100, eth * (1 + (rnd.NextDouble() - 0.5) * 0.002));
                kl.Add(new Kline(new DateTimeOffset(t).ToUnixTimeMilliseconds(), (decimal)o, (decimal)Math.Max(o, eth) + 1, (decimal)Math.Min(o, eth) - 1, (decimal)eth, 10, 5, true));
            }

            foreach (var m in new[] { 1, 5, 15 })
                CompareKlines.Seed("ETHUSDT", true, m, m == 1 ? kl : kl.GroupBy(k => k.OpenTimeMs / (m * 60_000L)).Select(g => new Kline(g.Key * m * 60_000L, g.First().Open, g.Max(x => x.High), g.Min(x => x.Low), g.Last().Close, g.Sum(x => x.Volume), g.Sum(x => x.BuyVolume), true)));
            foreach (var fid in new[] { 5, 15, 60 })
                Polymarket.Seed("test-market", 0, fid, "¿BTC sobre 100k?", Enumerable.Range(0, 10 * 24 * 60 / fid).Select(i => (t0.AddMinutes(i * fid), 0.4 + 0.2 * Math.Sin(i / 20.0) + 0.02 * rnd.NextDouble())));
        }
    }

    public static IEnumerable<object[]> Types() => new[]
    {
        typeof(DealerPositioning), typeof(GreekExposure), typeof(StrikeMatrix), typeof(StrikeLanes), typeof(StrikeBreakdown), typeof(TopContracts),
        typeof(VolatilitySurface), typeof(OptionsOddsCloud), typeof(ExpectedMove), typeof(ContractOdds), typeof(OptionStance), typeof(OptionsDashboard),
        typeof(VolatilityContext), typeof(SpotExposures), typeof(OiChanges), typeof(DayType),
        typeof(StrikeFootprint), typeof(PremiumByStrike), typeof(OptionsAtPrice), typeof(FlowVwap), typeof(FlowMarks), typeof(OptionPrints), typeof(FlowTape),
        typeof(IntervalFlow), typeof(OptionsStats), typeof(OptionsMarketStats), typeof(MarketShare), typeof(PremiumByExpiry),
        typeof(Nope), typeof(ContractSides), typeof(NetFlow), typeof(OptionsVolume), typeof(GreekFlow), typeof(OptionsSentiment), typeof(PremiumTide),
        typeof(IvRank), typeof(VolatilityRegime), typeof(SkewHistory), typeof(RiskReversalSkew), typeof(ExposureHistory), typeof(VarianceRiskPremium),
        typeof(OptionsHistory), typeof(OptionsSentimentGauge),
        typeof(CusumChangePoint), typeof(MarketHud), typeof(SpotEtfFlow), typeof(CorrelationCoefficient), typeof(OddsVsPrice),
    }.Select(t => new object[] { t });

    private static Indicator Create(Type type)
    {
        var ind = (Indicator)Activator.CreateInstance(type)!;
        if (ind is OddsVsPrice o) o.Market = "https://polymarket.com/event/test-market";
        return ind;
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Calcula_y_dibuja_con_datos(Type type)
    {
        Seed();
        foreach (var period in new[] { "M5", "M15", "H1" })
        {
            var model = new ChartModel(VpUltimateTests.Market(10), ChartPeriod.Parse(period), new ChartSettings { TimeZoneId = "UTC", Style = ChartStyle.Candles });
            var ind = Create(type);
            model.AddIndicator(ind);
            for (int k = 0; k < 2; k++) model.Update();
            model.Viewport.BarWidth = 8;
            using var surface = SKSurface.Create(new SKImageInfo(1400, 800));
            new ChartRenderer().Render(surface.Canvas, model, 1400, 800, new ChartRenderState());
            var slot = model.Indicators.Single();
            Assert.Null(slot.Error);
            if (ind is StructureIndicator si) Assert.True(si.ComputeError == null, si.ComputeError);
            _ = ind.GetAlertLevels().ToList();
            var dir = Environment.GetEnvironmentVariable("TH_SNAPSHOT_DIR");
            if (string.IsNullOrEmpty(dir) || period != "M15") continue;
            Directory.CreateDirectory(dir);
            using var img = surface.Snapshot();
            using var png = img.Encode(SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(Path.Combine(dir, $"opt-{type.Name}.png"), png.ToArray());
        }
    }

    /// <summary>Los de la escena muestran algo con datos (no se quedan en «sin datos» ni en «trayendo…»).</summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void Con_datos_dibuja_algo(Type type)
    {
        Seed();
        if (!typeof(SceneIndicator).IsAssignableFrom(type)) return;
        var model = new ChartModel(VpUltimateTests.Market(10), ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC", Style = ChartStyle.Candles });
        var ind = (SceneIndicator)Create(type);
        model.AddIndicator(ind);
        for (int k = 0; k < 2; k++) model.Update();
        var s = ind.CurrentScene;
        bool drew = s.Lines.Count + s.Boxes.Count + s.Marks.Count + s.Paths.Count + s.Dash.Count + s.Tags.Count > 0;
        var status = type.GetProperty("Status", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(ind) as string;
        // los que dibujan aparte (mapas, superficie, perfiles) no llenan la escena: basta con que no haya un aviso
        Assert.True(drew || status == null, $"{type.Name}: {status}");
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void No_redibuja_hacia_atras(Type type)
    {
        Seed();
        if (!typeof(SceneIndicator).IsAssignableFrom(type)) return;
        Scene Run(int days, out int lastClosed)
        {
            var model = new ChartModel(VpUltimateTests.Market(days), ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC", Style = ChartStyle.Candles });
            var ind = (SceneIndicator)Create(type);
            model.AddIndicator(ind);
            for (int k = 0; k < 2; k++) model.Update();
            lastClosed = model.Series.Count - 2;
            Assert.True(ind.ComputeError == null, ind.ComputeError);
            return ind.CurrentScene;
        }

        var shortRun = Run(7, out int cut);
        var full = Run(10, out _);
        int from = cut - 150;
        foreach (var m in shortRun.Marks.Where(m => m.Bar < cut && m.Bar >= from))
            Assert.True(full.Marks.Any(f => f.Bar == m.Bar && f.Kind == m.Kind && Math.Abs(f.Price - m.Price) < 1e-6 && f.Text == m.Text),
                $"{type.Name}: la marca {m.Kind} «{m.Text}» de la vela {m.Bar} desapareció o se movió");
    }

    [Fact]
    public void Las_cuentas_de_opciones_dan_lo_esperado()
    {
        // una call en el dinero: delta ≈ 0,5 y la put ≈ −0,5; la probabilidad de terminar arriba ≈ 50 %
        var (dc, g) = OptionsMath.Greeks(100, 100, 30 / 365.0, 0.5, OptionRight.Call);
        var (dp, _) = OptionsMath.Greeks(100, 100, 30 / 365.0, 0.5, OptionRight.Put);
        Assert.InRange(dc, 0.5, 0.56);
        Assert.InRange(dp, -0.5, -0.44);
        Assert.True(g > 0);
        Assert.InRange(OptionsMath.ProbAbove(100, 100, 30 / 365.0, 0.5), 0.45, 0.5);
        Assert.InRange(OptionsMath.Cdf(1.96), 0.974, 0.976);
        // el nombre de un contrato de Deribit
        var info = DeribitOptions.ParseName("BTC-27DEC26-100000-C");
        Assert.NotNull(info);
        Assert.Equal(new DateTime(2026, 12, 27, 8, 0, 0, DateTimeKind.Utc), info!.Value.Expiry);
        Assert.Equal(100000, info.Value.Strike);
        Assert.Equal(OptionRight.Call, info.Value.Right);
        Assert.Equal("BTC", OptionsData.UnderlyingOf("BTCUSDT"));
        Assert.Equal("ETH", OptionsData.UnderlyingOf("ETH-PERP"));
        Assert.Equal("SPY", OptionsData.UnderlyingOf("SPY"));
        Assert.Equal("will-btc-hit-100k", Polymarket.SlugOf("https://polymarket.com/event/will-btc-hit-100k?tid=1"));
    }

    [Fact]
    public void La_gamma_cambia_de_signo_y_hay_muros()
    {
        Seed();
        var c = OptionsData.Chain("BTC")!;
        Assert.NotNull(c);
        var (cw, pw) = OptionsMath.Walls(c);
        Assert.True(cw >= c.Spot && pw <= c.Spot);
        Assert.True(double.IsFinite(OptionsMath.MaxPain(c, null)));
        var day = OptionsMath.DailyOf(c);
        Assert.True(day.AtmIv > 0.3 && day.AtmIv < 1);
    }
}

/// <summary>Las API propias de opciones y de ETF contra un servidor de mentira: leen el JSON documentado y mandan la clave.</summary>
public class ExternalApiTests
{
    [Fact]
    public async Task La_api_propia_de_opciones_lee_el_formato_documentado()
    {
        using var s = new FakeHttpServer();
        s.OnHttp = req =>
        {
            if (req.Headers.GetValueOrDefault("X-Api-Key") != "secreta") return (401, "{}");
            return req.Route switch
            {
                "/api/chain" => (200, """{"time":"2026-11-05T10:00:00Z","spot":70000,"contracts":[{"symbol":"BTC-C","expiry":"2026-11-27T08:00:00Z","strike":72000,"right":"C","bid":900,"ask":950,"mark":925,"iv":0.52,"delta":0.4,"gamma":0.00003,"oi":120,"volume":30},{"symbol":"BTC-P","expiry":"2026-11-27T08:00:00Z","strike":68000,"right":"P","mark":800,"iv":0.58,"oi":200,"volume":50}]}"""),
                "/api/trades" => (200, """[{"time":"2026-11-05T09:59:00Z","symbol":"BTC-C","expiry":"2026-11-27T08:00:00Z","strike":72000,"right":"C","price":930,"size":5,"side":"buy","iv":0.52,"spot":70000,"venue":"CBOE"}]"""),
                "/api/daily" => (200, """[{"date":"2026-11-04","atmIv":0.5,"rr25":-2.1,"pcVolume":1.2,"gex":-1.5e8,"callWall":75000,"putWall":65000,"gammaFlip":69000}]"""),
                _ => (404, "{}"),
            };
        };
        var api = new JsonOptionsApi(s.Http + "/api/", "X-Api-Key", "secreta", new[] { "btc" });
        Assert.True(api.Supports("BTC"));
        Assert.False(api.Supports("ETH"));
        var chain = await api.ChainAsync("BTC", CancellationToken.None);
        Assert.NotNull(chain);
        Assert.Equal(70000, chain!.Spot);
        Assert.Equal(2, chain.Contracts.Count);
        Assert.Equal(OptionRight.Put, chain.Contracts[1].Right);
        Assert.Equal(1, chain.Contracts[1].ContractSize);
        var trades = await api.TradesAsync("BTC", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, CancellationToken.None);
        Assert.Single(trades);
        Assert.Equal(1, trades[0].Aggressor);
        Assert.Equal(1, trades[0].Bias);
        Assert.Equal("CBOE", trades[0].Venue);
        Assert.Equal(930 * 5, trades[0].Premium);
        var daily = await api.DailyAsync("BTC", DateTime.UtcNow.AddDays(-10), DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(-2.1, Assert.Single(daily).Rr25);
        // la gamma de la put sale de su IV (no vino del proveedor)
        Assert.True(OptionsMath.GammaOf(chain.Contracts[1], chain.Spot, chain.Time) > 0);
    }

    [Fact]
    public async Task La_api_de_etf_lee_dolares_o_millones()
    {
        using var s = new FakeHttpServer();
        s.OnHttp = req => req.Headers.GetValueOrDefault("Authorization") != "Bearer k"
            ? (401, "{}")
            : (200, """{"data":[{"date":"2026-11-03","net":-123400000,"funds":{"IBIT":-80000000}},{"date":"2026-11-04","netMillions":55.5}]}""");
        var api = new JsonEtfFlowsApi(s.Http, "Authorization", "k");
        var days = await api.DailyAsync("BTC", new DateTime(2026, 11, 1), new DateTime(2026, 11, 5), CancellationToken.None);
        Assert.Equal(2, days.Count);
        Assert.Equal(-123400000, days[0].NetUsd);
        Assert.Equal(-80000000, days[0].Funds!["IBIT"]);
        Assert.Equal(55.5e6, days[1].NetUsd, 3);
        Assert.Contains(s.Requests, r => r.Query("asset") == "BTC" && r.Query("from") == "2026-11-01");
    }
}
