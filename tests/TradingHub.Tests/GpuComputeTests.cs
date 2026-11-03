using TradingHub.Compute;
using TradingHub.Data;
using TradingHub.Market;
using Xunit;

namespace TradingHub.Tests;

/// <summary>El agrupado por precio da lo mismo en la CPU, en paralelo y en el acelerador de ILGPU.</summary>
public class GpuComputeTests
{
    [Fact]
    public void BinsMatchAcrossBackends()
    {
        var inst = new Instrument { Symbol = "BTCUSDT", FeedId = "test", TickSize = 0.1m };
        var store = new TradeStore(inst);
        var rnd = new Random(3);
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 600_000; i++)
            store.Add(new Trade(i + 1, t.AddMilliseconds(i * 10), 70000 + rnd.Next(-2000, 2000) * 0.1m, Math.Round((decimal)rnd.NextDouble() * 3, 3) + 0.001m, rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell));

        GpuCompute.Mode = "off";
        var cpu = GpuCompute.BinByPrice(store, 0, store.Count)!;
        Assert.False(cpu.OnGpu);
        Assert.Equal(store.Count, cpu.Count > 0 ? store.Count : 0);
        double totalBid = cpu.Bid.Sum(), totalAsk = cpu.Ask.Sum();
        decimal expectedBid = 0, expectedAsk = 0;
        for (int i = 0; i < store.Count; i++)
            if (store.GetSide(i) == TradeSide.Buy) expectedAsk += store.GetVolume(i);
            else expectedBid += store.GetVolume(i);
        Assert.Equal((double)expectedBid, totalBid, 3);
        Assert.Equal((double)expectedAsk, totalAsk, 3);

        // el acelerador de CPU de ILGPU recorre el mismo camino que la GPU (copias, kernel, atómicos)
        GpuCompute.Dispose();
        GpuCompute.Mode = "cpu-accelerator";
        GpuCompute.MinTradesForGpu = 1000;
        var gpu = GpuCompute.BinByPrice(store, 0, store.Count)!;
        Assert.True(gpu.OnGpu, GpuCompute.Status);
        Assert.Equal(cpu.Low, gpu.Low);
        Assert.Equal(cpu.High, gpu.High);
        for (long i = 0; i < cpu.Count; i++)
        {
            Assert.Equal(cpu.Bid[i], gpu.Bid[i], 6);
            Assert.Equal(cpu.Ask[i], gpu.Ask[i], 6);
        }

        GpuCompute.Dispose();
        GpuCompute.Mode = "auto";
        GpuCompute.MinTradesForGpu = 400_000;
    }
}
