using System.Text;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using Xunit;

namespace TradingHub.Tests;

/// <summary>CVD por mercado (operaciones agregadas con su mercado de origen) y lectura de liquidaciones.</summary>
public class DerivativesTests
{
    [Fact]
    public void El_cvd_por_mercado_separa_cada_exchange_y_suma_el_agregado()
    {
        var inst = new Instrument { Symbol = "BTCUSDT", FeedId = AggregateFeed.FeedId, TickSize = 0.1m, VolumeStep = 0.001m };
        var feed = new AggregateFeed(new IMarketDataFeed[] { new SimulatedFeed(), new SimulatedFeed() });
        var data = new InstrumentData(inst, feed);
        var t0 = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        long slot = 0;
        var rnd = new Random(3);
        decimal b0 = 0, b1 = 0;
        for (int i = 0; i < 5000; i++)
        {
            var t = t0.AddSeconds(i * 10);
            int src = i % 2;
            bool buy = src == 0 ? rnd.Next(10) < 7 : rnd.Next(10) < 3; // el mercado 0 compra, el 1 vende
            decimal v = 1;
            if (src == 0) b0 += buy ? v : -v;
            else b1 += buy ? v : -v;
            data.Trades.Add(new Trade(AggregateFeed.TaggedId(t, src, ref slot), t, 70000m + (i % 50), v, buy ? TradeSide.Buy : TradeSide.Sell));
        }

        Assert.Equal(1, AggregateFeed.SourceOf(AggregateFeed.TaggedId(t0, 1, ref slot)));
        var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        var cvd = new CvdByMarket();
        model.AddIndicator(cvd);
        model.Update();
        int last = model.Series.Count - 1;
        var m0 = (ValueDataSeries)cvd.DataSeries.FindById("M0");
        var m1 = (ValueDataSeries)cvd.DataSeries.FindById("M1");
        var agg = (ValueDataSeries)cvd.DataSeries.FindById("AGG");
        Assert.Equal(b0, m0[last]);
        Assert.Equal(b1, m1[last]);
        Assert.Equal(b0 + b1, agg[last]);
        Assert.True(m0[last] > 0 && m1[last] < 0);
    }

    [Fact]
    public void Lee_las_liquidaciones_de_binance_y_bybit()
    {
        var bin = Derivatives.Parse(Derivatives.Binance, Encoding.UTF8.GetBytes(
            "{\"e\":\"forceOrder\",\"E\":1568014460893,\"o\":{\"s\":\"BTCUSDT\",\"S\":\"SELL\",\"o\":\"LIMIT\",\"f\":\"IOC\",\"q\":\"0.014\",\"p\":\"9910\",\"ap\":\"9910\",\"X\":\"FILLED\",\"l\":\"0.014\",\"z\":\"0.014\",\"T\":1568014460893}}")).Single();
        Assert.True(bin.LongLiquidated);
        Assert.Equal(9910, bin.Price);
        Assert.Equal(0.014, bin.Quantity, 6);
        var byb = Derivatives.Parse(Derivatives.Bybit, Encoding.UTF8.GetBytes(
            "{\"topic\":\"allLiquidation.BTCUSDT\",\"type\":\"snapshot\",\"ts\":1739502303000,\"data\":[{\"T\":1739502302929,\"s\":\"BTCUSDT\",\"S\":\"Sell\",\"v\":\"0.5\",\"p\":\"96000\"}]}")).Single();
        Assert.False(byb.LongLiquidated);
        Assert.Equal(96000, byb.Price);
        Assert.Empty(Derivatives.Parse(Derivatives.Bybit, Encoding.UTF8.GetBytes("{\"op\":\"pong\"}")));
    }
}
