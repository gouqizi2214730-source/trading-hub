using System.Globalization;
using SkiaSharp;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.Charting.Drawings;

/// <summary>
/// Cálculo de los dibujos que se arman con las operaciones (no con las velas del gráfico): VWAP anclado y perfil de rango
/// fijo. Como salen del almacén de operaciones al tick, dan exactamente lo mismo en cualquier período y con cualquier
/// zoom o paso de precio. Lo calculado se guarda en el dibujo y solo se rehace si cambian los puntos, los ajustes o
/// llegan operaciones nuevas (y entonces, de forma incremental).
/// </summary>
public sealed class TradeDrawingCalc
{
    // ------------------------------------------------------------------ VWAP anclado

    private sealed class VwapState
    {
        public string Key = "";
        public long Generation = -1;
        public int Processed;          // operaciones ya sumadas
        public decimal Pv, V, P2v;     // acumulados desde el ancla
        public int AnchorIndex;
        // por vela del gráfico: VWAP y desvío al cierre de la vela (índice = barra − primera barra)
        public int FirstBar;
        public readonly List<(float Vwap, float Sd)> Bars = new();
        public string SeriesKey = "";
    }

    private readonly VwapState _vwap = new();

    /// <summary>VWAP y desvío al cierre de cada vela desde el ancla (null donde todavía no hay datos).</summary>
    public (int FirstBar, IReadOnlyList<(float Vwap, float Sd)> Bars) AnchoredVwap(TradeStore store, CandleSeries series, DateTime anchorUtc)
    {
        var s = _vwap;
        string key = anchorUtc.Ticks.ToString();
        string seriesKey = series.Period.Code + "|" + series.PriceStep;
        if (s.Key != key || s.Generation != store.Generation || s.SeriesKey != seriesKey || series.Count == 0 || s.Processed > store.Count)
        {
            s.Key = key;
            s.Generation = store.Generation;
            s.SeriesKey = seriesKey;
            s.AnchorIndex = store.LowerBound(anchorUtc);
            s.Processed = s.AnchorIndex;
            s.Pv = s.V = s.P2v = 0;
            s.Bars.Clear();
            s.FirstBar = series.FindBar(anchorUtc);
            if (s.FirstBar < 0) s.FirstBar = 0;
        }

        if (series.Count == 0) return (0, s.Bars);
        // sigue desde la última vela que se calculó (la que estaba en curso se vuelve a hacer)
        int bar = s.FirstBar + Math.Max(0, s.Bars.Count - 1);
        while (bar < series.Count)
        {
            var c = series[bar];
            int end = bar < series.Count - 1 ? Math.Min(c.LastTradeIndex, store.Count - 1) : store.Count - 1;
            if (bar < series.Count - 1 && c.LastTradeIndex < 0) end = s.Processed - 1; // vela sin operaciones
            if (end >= s.Processed)
            {
                for (int i = s.Processed; i <= end; i++)
                {
                    decimal p = store.GetPrice(i);
                    decimal v = store.GetVolume(i);
                    s.Pv += p * v;
                    s.V += v;
                    s.P2v += p * p * v;
                }

                s.Processed = end + 1;
            }

            float vwap = s.V > 0 ? (float)(s.Pv / s.V) : float.NaN;
            float sd = s.V > 0 ? (float)Math.Sqrt((double)Math.Max(0, s.P2v / s.V - (s.Pv / s.V) * (s.Pv / s.V))) : 0;
            // la vela en curso ya tiene su entrada (se actualiza en cada llamada); al cerrarse queda como está
            if (s.Bars.Count > bar - s.FirstBar) s.Bars[bar - s.FirstBar] = (vwap, sd);
            else s.Bars.Add((vwap, sd));
            if (bar == series.Count - 1) break;
            bar++;
        }

        return (s.FirstBar, s.Bars);
    }

    // ------------------------------------------------------------------ perfil de rango fijo

    public sealed class ProfileResult
    {
        public decimal RowSize;
        public decimal Low, High;
        public SortedDictionary<decimal, (decimal Bid, decimal Ask)> Rows = new();
        public decimal Poc, Vah, Val, Total, MaxVolume;
        public int Trades;
        public DateTime From, To;
    }

    private string _profileKey = "";
    private long _profileGeneration = -1;
    private int _profileCount;
    private ProfileResult? _profile;

    /// <summary>Perfil de las operaciones entre dos instantes (la mayor hasta ahora si <paramref name="toUtc"/> es futuro).</summary>
    public ProfileResult? FixedRangeProfile(TradeStore store, Instrument instrument, DateTime fromUtc, DateTime toUtc, int rows, int valueAreaPercent)
    {
        if (toUtc < fromUtc) (fromUtc, toUtc) = (toUtc, fromUtc);
        bool open = toUtc >= store.LastTime; // sigue creciendo con cada operación
        string key = string.Join("|", fromUtc.Ticks, toUtc.Ticks, rows, valueAreaPercent, instrument.TickSize);
        if (_profile != null && _profileKey == key && _profileGeneration == store.Generation && (!open || _profileCount == store.Count)) return _profile;
        _profileKey = key;
        _profileGeneration = store.Generation;
        _profileCount = store.Count;

        int a = store.LowerBound(fromUtc), b = store.LowerBound(toUtc);
        if (b <= a) return _profile = null;
        var levels = new Dictionary<long, (decimal Bid, decimal Ask)>();
        long lo = long.MaxValue, hi = long.MinValue;
        for (int i = a; i < b; i++)
        {
            long p = store.GetPriceTicks(i);
            if (p < lo) lo = p;
            if (p > hi) hi = p;
            levels.TryGetValue(p, out var v);
            decimal vol = store.GetVolume(i);
            levels[p] = store.GetSide(i) == TradeSide.Buy ? (v.Bid, v.Ask + vol) : (v.Bid + vol, v.Ask);
        }

        decimal tick = instrument.TickSize;
        long span = hi - lo + 1;
        long ticksPerRow = rows <= 0 ? 1 : Math.Max(1, (long)Math.Ceiling(span / (double)rows));
        var result = new ProfileResult { RowSize = tick * ticksPerRow, Low = instrument.FromTicks(lo), High = instrument.FromTicks(hi), Trades = b - a, From = fromUtc, To = toUtc };
        foreach (var (p, v) in levels)
        {
            decimal row = instrument.FromTicks(lo + (p - lo) / ticksPerRow * ticksPerRow);
            result.Rows.TryGetValue(row, out var r);
            result.Rows[row] = (r.Bid + v.Bid, r.Ask + v.Ask);
        }

        decimal max = -1, poc = 0, total = 0;
        foreach (var (row, v) in result.Rows)
        {
            decimal vol = v.Bid + v.Ask;
            total += vol;
            if (vol > max)
            {
                max = vol;
                poc = row;
            }
        }

        result.Poc = poc;
        result.Total = total;
        result.MaxVolume = max;
        var prices = result.Rows.Keys.ToList();
        int pi = prices.IndexOf(poc), up = pi, dn = pi;
        decimal acc = max, target = total * valueAreaPercent / 100m;
        decimal Vol(int i) => result.Rows[prices[i]].Bid + result.Rows[prices[i]].Ask;
        while (acc < target && (up < prices.Count - 1 || dn > 0))
        {
            decimal vu = up < prices.Count - 1 ? Vol(up + 1) : -1, vd = dn > 0 ? Vol(dn - 1) : -1;
            if (vu >= vd) acc += Vol(++up);
            else acc += Vol(--dn);
        }

        result.Vah = prices[up] + result.RowSize - tick;
        result.Val = prices[dn];
        return _profile = result;
    }
}
