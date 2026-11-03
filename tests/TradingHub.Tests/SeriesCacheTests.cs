using System.Diagnostics;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Velas armadas una sola vez por período (cambiar de 1h a 4h y volver es instantáneo) y en paralelo cuando hay millones.</summary>
public class SeriesCacheTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = "test", TickSize = 0.1m, VolumeStep = 0.001m };

    private static InstrumentData Store(int trades, int seed = 11)
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        var rnd = new Random(seed);
        var t = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        decimal px = 60000m;
        for (int i = 0; i < trades; i++)
        {
            t = t.AddMilliseconds(rnd.Next(1, 400));
            px = Math.Max(1000, px + rnd.Next(-3, 4) * 0.1m);
            data.Trades.Add(new Trade(i + 1, t, px, rnd.Next(1, 200) / 1000m, rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        return data;
    }

    [Fact]
    public void En_paralelo_da_lo_mismo_que_en_orden()
    {
        var data = Store(2_000_000);
        foreach (var code in new[] { "M1", "M15", "H1", "H4", "D1" })
        {
            var period = ChartPeriod.Parse(code);
            var par = new CandleSeries(Btc, period, 1);
            par.Rebuild(data.Trades); // > 1,5 millones: por tramos en paralelo
            var seq = new CandleSeries(Btc, period, 1);
            for (int i = 0; i < data.Trades.Count; i++) seq.AppendOne(data.Trades, i);
            Assert.Equal(seq.Count, par.Count);
            for (int i = 0; i < seq.Count; i++)
            {
                var a = seq[i];
                var b = par[i];
                Assert.Equal(a.Time, b.Time);
                Assert.Equal((a.OpenTicks, a.HighTicks, a.LowTicks, a.CloseTicks), (b.OpenTicks, b.HighTicks, b.LowTicks, b.CloseTicks));
                Assert.Equal(a.Volume, b.Volume);
                Assert.Equal(a.Delta, b.Delta);
                Assert.Equal(a.Trades, b.Trades);
                Assert.Equal((a.FirstTradeIndex, a.LastTradeIndex), (b.FirstTradeIndex, b.LastTradeIndex));
                Assert.Equal(a.IsClosed, b.IsClosed);
                Assert.Equal(a.PocRow, b.PocRow);
            }
        }
    }

    [Fact]
    public void Cambiar_de_periodo_y_volver_no_rearma_las_velas()
    {
        var data = Store(300_000);
        var h1 = data.GetSeries(ChartPeriod.Parse("H1"), 1);
        var h4 = data.GetSeries(ChartPeriod.Parse("H4"), 1);
        var sw = Stopwatch.StartNew();
        var again = data.GetSeries(ChartPeriod.Parse("H1"), 1);
        sw.Stop();
        Assert.Same(h1, again);
        Assert.NotSame(h1, h4);
        Assert.True(sw.ElapsedMilliseconds < 50, $"{sw.ElapsedMilliseconds} ms");

        // llegan operaciones nuevas: la serie guardada se pone al día, no se rearma
        var last = data.Trades.LastTime;
        data.Trades.Add(new Trade(data.Trades.Count + 1, last.AddHours(5), 60010m, 1, TradeSide.Buy));
        var updated = data.GetSeries(ChartPeriod.Parse("H1"), 1);
        Assert.Same(h1, updated);
        Assert.Equal(last.AddHours(5).Date.AddHours(last.AddHours(5).Hour), updated[updated.Count - 1].Time);

        // se reemplaza la historia (vaciar + volver a cargar): lo armado se tira
        data.Trades.Clear();
        data.Trades.Add(new Trade(1, last, 60000m, 1, TradeSide.Buy));
        var fresh = data.GetSeries(ChartPeriod.Parse("H1"), 1);
        Assert.NotSame(h1, fresh);
        Assert.Equal(1, fresh.Count);
    }
}
