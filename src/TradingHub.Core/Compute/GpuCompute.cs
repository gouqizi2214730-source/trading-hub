using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.OpenCL;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Compute;

/// <summary>
/// Aceleración por GPU (CUDA u OpenCL, con ILGPU) para los cálculos que recorren millones de operaciones: el
/// agrupado por precio de los perfiles de volumen. Si no hay GPU, o el lote es chico, se usa la CPU en paralelo:
/// el resultado es el mismo.
/// </summary>
public static class GpuCompute
{
    private static readonly object Lock = new();
    private static Context? _context;
    private static Accelerator? _accelerator;
    private static Action<Index1D, ArrayView<long>, ArrayView<double>, ArrayView<byte>, long, ArrayView<double>, ArrayView<double>>? _bin;
    private static bool _tried;
    private static string _status = "sin iniciar";

    /// <summary>"auto" (GPU si hay), "off" (siempre CPU) o "cpu-accelerator" (el acelerador de CPU de ILGPU, para probar).</summary>
    public static string Mode { get; set; } = "auto";

    /// <summary>Desde cuántas operaciones conviene la GPU (copiar menos que eso cuesta más que calcular).</summary>
    public static int MinTradesForGpu { get; set; } = 400_000;

    public static string Status
    {
        get
        {
            lock (Lock) return _status;
        }
    }

    public static bool IsAvailable => Accelerator() != null;

    public static string? DeviceName => Accelerator()?.Name;

    /// <summary>Veces que el agrupado corrió en la GPU (para mostrar en Ajustes).</summary>
    public static long GpuRuns;

    private static Accelerator? Accelerator()
    {
        if (Mode == "off") return null;
        lock (Lock)
        {
            if (_tried) return _accelerator;
            _tried = true;
            try
            {
                _context = Context.CreateDefault();
                Device? device = null;
                if (Mode == "cpu-accelerator") device = _context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
                else
                    device = _context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda)
                             ?? _context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL);

                if (device == null)
                {
                    _status = "sin GPU compatible (CUDA / OpenCL): se usa la CPU";
                    return null;
                }

                _accelerator = device.CreateAccelerator(_context);
                _bin = _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<long>, ArrayView<double>, ArrayView<byte>, long, ArrayView<double>, ArrayView<double>>(BinKernel);
                _status = $"{_accelerator.Name} ({_accelerator.AcceleratorType}, {_accelerator.MemorySize / (1 << 20)} MB)";
                return _accelerator;
            }
            catch (Exception ex)
            {
                _status = "GPU no disponible: " + ex.Message;
                _accelerator = null;
                return null;
            }
        }
    }

    private static void BinKernel(Index1D i, ArrayView<long> prices, ArrayView<double> vols, ArrayView<byte> sides, long lo, ArrayView<double> bid, ArrayView<double> ask)
    {
        long idx = prices[i] - lo;
        if (sides[i] == 1) Atomic.Add(ref ask[idx], vols[i]);
        else Atomic.Add(ref bid[idx], vols[i]);
    }

    /// <summary>Resultado del agrupado: volumen vendedor y comprador por tick, desde <see cref="Low"/>.</summary>
    public sealed class Bins
    {
        public long Low, High;
        public double[] Bid = Array.Empty<double>();
        public double[] Ask = Array.Empty<double>();
        public bool OnGpu;
        public int Count => Bid.Length;
    }

    /// <summary>Agrupa las operaciones [a, b) por precio (en ticks); null si no hay ninguna.</summary>
    public static Bins? BinByPrice(TradeStore store, int a, int b)
    {
        if (b <= a) return null;
        long lo = long.MaxValue, hi = long.MinValue;
        for (int i = a; i < b; i++)
        {
            long p = store.GetPriceTicks(i);
            if (p < lo) lo = p;
            if (p > hi) hi = p;
        }

        long span = hi - lo + 1;
        if (span > 50_000_000) return null; // demasiados precios distintos: no es un perfil razonable
        int n = b - a;
        var bins = new Bins { Low = lo, High = hi, Bid = new double[span], Ask = new double[span] };
        if (n >= MinTradesForGpu && Accelerator() is { } acc && _bin != null)
        {
            try
            {
                var prices = new long[n];
                var vols = new double[n];
                var sides = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    prices[i] = store.GetPriceTicks(a + i);
                    vols[i] = (double)store.GetVolume(a + i);
                    sides[i] = (byte)store.GetSide(a + i);
                }

                lock (Lock)
                {
                    using var dPrices = acc.Allocate1D(prices);
                    using var dVols = acc.Allocate1D(vols);
                    using var dSides = acc.Allocate1D(sides);
                    using var dBid = acc.Allocate1D<double>(span);
                    using var dAsk = acc.Allocate1D<double>(span);
                    dBid.MemSetToZero();
                    dAsk.MemSetToZero();
                    _bin(n, dPrices.View, dVols.View, dSides.View, lo, dBid.View, dAsk.View);
                    acc.Synchronize();
                    dBid.CopyToCPU(bins.Bid);
                    dAsk.CopyToCPU(bins.Ask);
                }

                bins.OnGpu = true;
                Interlocked.Increment(ref GpuRuns);
                return bins;
            }
            catch (Exception ex)
            {
                lock (Lock) _status = "GPU falló (se sigue con CPU): " + ex.Message;
                Array.Clear(bins.Bid);
                Array.Clear(bins.Ask);
            }
        }

        // CPU: en paralelo por tramos, cada hilo con sus propios acumuladores y se suman al final
        if (n < 200_000 || span > 4_000_000)
        {
            for (int i = a; i < b; i++)
            {
                long idx = store.GetPriceTicks(i) - lo;
                if (store.GetSide(i) == TradeSide.Buy) bins.Ask[idx] += (double)store.GetVolume(i);
                else bins.Bid[idx] += (double)store.GetVolume(i);
            }

            return bins;
        }

        int parts = Math.Clamp(Environment.ProcessorCount, 2, 16);
        var partial = new (double[] Bid, double[] Ask)[parts];
        Parallel.For(0, parts, k =>
        {
            var pb = new double[span];
            var pa = new double[span];
            int from = a + (int)((long)n * k / parts), to = a + (int)((long)n * (k + 1) / parts);
            for (int i = from; i < to; i++)
            {
                long idx = store.GetPriceTicks(i) - lo;
                if (store.GetSide(i) == TradeSide.Buy) pa[idx] += (double)store.GetVolume(i);
                else pb[idx] += (double)store.GetVolume(i);
            }

            partial[k] = (pb, pa);
        });
        foreach (var (pb, pa) in partial)
            for (long i = 0; i < span; i++)
            {
                bins.Bid[i] += pb[i];
                bins.Ask[i] += pa[i];
            }

        return bins;
    }

    public static void Dispose()
    {
        lock (Lock)
        {
            _accelerator?.Dispose();
            _context?.Dispose();
            _accelerator = null;
            _context = null;
            _tried = false;
        }
    }
}
