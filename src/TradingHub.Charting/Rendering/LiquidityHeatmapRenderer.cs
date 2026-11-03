using SkiaSharp;
using TradingHub.Data;

namespace TradingHub.Charting.Rendering;

/// <summary>
/// Mapa de calor de liquidez detrás de las velas (estilo Bookmap / chartbeyond): cada foto del libro pinta sus niveles
/// en la fila de precio que les toca, con más brillo cuanto más tamaño en reposo hay. Las fotos se agrupan por
/// columna de píxeles, así dibujar horas de libro cuesta lo mismo que una pantalla.
/// </summary>
public sealed class LiquidityHeatmapRenderer
{
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };

    // paleta: azul oscuro → celeste → amarillo → blanco
    private static readonly (float T, byte R, byte G, byte B)[] Palette =
    {
        (0f, 20, 40, 110),
        (0.35f, 30, 120, 220),
        (0.65f, 90, 230, 230),
        (0.85f, 250, 220, 60),
        (1f, 255, 255, 255),
    };

    public static SKColor ColorFor(float t, byte alpha)
    {
        t = Math.Clamp(t, 0, 1);
        for (int i = 1; i < Palette.Length; i++)
        {
            if (t > Palette[i].T) continue;
            var (t0, r0, g0, b0) = Palette[i - 1];
            var (t1, r1, g1, b1) = Palette[i];
            float f = t1 > t0 ? (t - t0) / (t1 - t0) : 0;
            return new SKColor((byte)(r0 + (r1 - r0) * f), (byte)(g0 + (g1 - g0) * f), (byte)(b0 + (b1 - b0) * f), alpha);
        }

        return new SKColor(255, 255, 255, alpha);
    }

    public void Render(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout main)
    {
        var series = model.Series;
        if (series.Count == 0 || layout.LastVisible < layout.FirstVisible) return;
        var liq = model.Data.Liquidity;
        if (liq.Count == 0) return;
        int first = Math.Max(0, layout.FirstVisible), last = Math.Min(series.Count - 1, layout.LastVisible);
        var from = series[first].Time;
        var to = last + 1 < series.Count ? series[last + 1].Time : series[last].Time + BarLength(model, last);
        var samples = liq.Between(from, to);
        if (samples.Count == 0) return;

        // columnas: una por foto si entran; si no, varias fotos por columna (se queda el máximo)
        double barSec = BarLength(model, last).TotalSeconds;
        float colW = MathF.Max(1.5f, (float)(layout.BarWidth * Math.Max(1, LiquidityHistory.SampleSeconds) / Math.Max(1, barSec)));
        int step = series.PriceStep;
        float rowH = MathF.Max(1, MathF.Abs(main.Y(series.TicksToPrice(step)) - main.Y(series.TicksToPrice(0))));
        // celdas: columna × precio (del nivel) → tamaño y ticks que abarca (hacia abajo si es bid, arriba si es ask)
        var cells = new Dictionary<(int Col, long Price, bool Bid), (float Size, int Span)>();
        int bar = first;
        foreach (var s in samples)
        {
            while (bar + 1 <= last && series[bar + 1].Time <= s.Time) bar++;
            var next = bar + 1 < series.Count ? series[bar + 1].Time : series[bar].Time + BarLength(model, bar);
            double len = Math.Max(1, (next - series[bar].Time).TotalSeconds);
            double frac = Math.Clamp((s.Time - series[bar].Time).TotalSeconds / len, 0, 1);
            float x = layout.LeftX(bar) + (float)(frac * layout.BarWidth);
            if (x < main.Rect.Left - colW || x > main.Rect.Right) continue;
            int col = (int)MathF.Floor(x / colW);
            for (int i = 0; i < s.BidOffsets.Length; i++) Put(cells, col, s.BidPrice(i), true, s.BidSizes[i], s.Bucket);
            for (int i = 0; i < s.AskOffsets.Length; i++) Put(cells, col, s.AskPrice(i), false, s.AskSizes[i], s.Bucket);
        }

        if (cells.Count == 0) return;
        // escala: logarítmica hasta el percentil 97 (un iceberg no apaga todo lo demás)
        var values = cells.Values.Select(v => v.Size).ToArray();
        Array.Sort(values);
        float max = values[Math.Min(values.Length - 1, (int)(values.Length * 0.97))];
        if (max <= 0) return;
        float logMax = MathF.Log(1 + max);
        byte alpha = (byte)Math.Clamp(model.Settings.HeatmapOpacity * 2.55, 10, 255);
        canvas.Save();
        canvas.ClipRect(main.Rect);
        foreach (var ((col, price, bid), (size, span)) in cells)
        {
            float t = MathF.Log(1 + size) / logMax;
            if (t < 0.04f) continue;
            // el nivel ocupa su fila del gráfico (o el tramo que resume, si es más alto que una fila)
            long top = bid ? price : price + span - 1;
            long bottom = bid ? price - span + 1 : price;
            float yTop = main.Y(series.TicksToPrice((series.RowOf(top) + 1) * step));
            float yBottom = main.Y(series.TicksToPrice(series.RowOf(bottom) * step));
            float h = MathF.Max(1, yBottom - yTop);
            _fill.Color = ColorFor(t, (byte)(alpha * MathF.Min(1, 0.25f + t)));
            canvas.DrawRect(col * colW, yTop, colW, h, _fill);
        }

        canvas.Restore();
    }

    private static void Put(Dictionary<(int, long, bool), (float Size, int Span)> cells, int col, long price, bool bid, float size, int span)
    {
        var key = (col, price, bid);
        if (!cells.TryGetValue(key, out var cur) || size > cur.Size) cells[key] = (size, span);
    }

    private static TimeSpan BarLength(ChartModel model, int bar)
    {
        if (model.Period.IsTimeBased) return model.Period.ApproxDuration;
        var s = model.Series;
        return bar > 0 ? s[bar].Time - s[bar - 1].Time : TimeSpan.FromMinutes(1);
    }
}
