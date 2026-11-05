using SkiaSharp;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Rendering.Context;
using TradingHub.Trading;
using TradingHub.Rendering;

namespace TradingHub.Charting.Dom;

/// <summary>
/// Libro de órdenes vertical (DOM / escalera de precios), solo para leer el mercado: no opera. Como el Order Book de trdr
/// y la escalera de LiquidityMapperFast: cantidad y total acumulado por precio (en monedas o en USD), marcas de distancia
/// al precio (0,5 / 1 / 2,5 / 5 %), cambios del libro en la ventana (+ agregado / − retirado), lo operado reciente y en la
/// sesión, una barra con el spread y la liquidez de cada lado a 0,5 / 2 / 10 %, y al pasar el mouse la comparación con el
/// otro lado a igual distancia (Mirror). El paso de las filas puede ser automático.
/// </summary>
public sealed class DomModel
{
    // operado por tick del instrumento (las filas se arman al dibujar: cambiar el paso no rehace nada)
    private readonly Dictionary<long, (decimal Bid, decimal Ask)> _sessionVolume = new();
    private int _processed;
    private DateOnly _sessionDate;

    // fotos del libro (por tick) para los cambios de la ventana
    private readonly Queue<(DateTime Time, Dictionary<long, decimal> Bids, Dictionary<long, decimal> Asks)> _book = new();
    private DateTime _lastBookTime;

    public DomModel(InstrumentData data, DomSettings? settings = null)
    {
        Data = data;
        Settings = settings ?? new DomSettings();
        AutoCenter = Settings.AutoCenter;
        data.TradesAppended += (_, _) => UpdateVolume();
        data.HistoryLoaded += () =>
        {
            _sessionVolume.Clear();
            _processed = 0;
            UpdateVolume();
        };
        UpdateVolume();
    }

    public InstrumentData Data { get; }
    public Instrument Instrument => Data.Instrument;

    /// <summary>Cuenta del gráfico: solo para marcar el precio promedio de la posición (el DOM no opera).</summary>
    public ITradingAccount? Broker { get; set; }

    /// <summary>Configuración visual y de comportamiento (compartida entre DOMs).</summary>
    public DomSettings Settings { get; set; }

    /// <summary>Ticks del instrumento por fila del DOM (con el tick automático, lo elige el dibujo).</summary>
    public int Step { get; set; } = 1;

    /// <summary>Precio (en ticks) de la fila central cuando no está centrado automáticamente.</summary>
    public long CenterTicks { get; set; }

    /// <summary>Si sigue al precio (se apaga al desplazar con la rueda).</summary>
    public bool AutoCenter { get; set; }

    public float RowHeight => Math.Clamp(Settings.RowHeight, 10, 60);

    public SessionSettings Session { get; set; } = SessionSettings.CryptoUtc;

    /// <summary>Volumen operado en la sesión por tick (bid = vendedores agresivos, ask = compradores agresivos).</summary>
    public IReadOnlyDictionary<long, (decimal Bid, decimal Ask)> SessionVolume => _sessionVolume;

    public long RowOf(long ticks) => CandleSeries.FloorDiv(ticks, Step);

    private void UpdateVolume()
    {
        var store = Data.Trades;
        if (store.Count == 0) return;
        var today = Session.SessionDate(store.LastTime);
        if (today != _sessionDate)
        {
            _sessionDate = today;
            _sessionVolume.Clear();
            _processed = store.LowerBound(Session.SessionStartUtc(today));
        }

        for (int i = _processed; i < store.Count; i++)
        {
            if (store.GetId(i) < 0) continue; // historia en velas: no son operaciones reales
            long t = store.GetPriceTicks(i);
            _sessionVolume.TryGetValue(t, out var v);
            var vol = store.GetVolume(i);
            _sessionVolume[t] = store.GetSide(i) == TradeSide.Sell ? (v.Bid + vol, v.Ask) : (v.Bid, v.Ask + vol);
        }

        _processed = store.Count;
    }

    public void ResetVolume()
    {
        _sessionVolume.Clear();
        _processed = 0;
        _sessionDate = default;
        UpdateVolume();
        _book.Clear();
    }

    /// <summary>Guarda una foto del libro (como mucho una por segundo) y olvida las más viejas que la ventana.</summary>
    internal void RememberBook(DateTime now)
    {
        var d = Data.Depth;
        if (now - _lastBookTime < TimeSpan.FromSeconds(1) || (!d.HasBid && !d.HasAsk)) return;
        _lastBookTime = now;
        var b = new Dictionary<long, decimal>(d.Bids.Length);
        var a = new Dictionary<long, decimal>(d.Asks.Length);
        foreach (var l in d.Bids) b[l.PriceTicks] = b.GetValueOrDefault(l.PriceTicks) + l.Size;
        foreach (var l in d.Asks) a[l.PriceTicks] = a.GetValueOrDefault(l.PriceTicks) + l.Size;
        _book.Enqueue((now, b, a));
        var keep = TimeSpan.FromSeconds(Math.Max(5, Settings.ChangeWindowSeconds) + 2);
        while (_book.Count > 1 && now - _book.Peek().Time > keep) _book.Dequeue();
    }

    /// <summary>La foto del libro de hace la ventana (o la más vieja que haya), y desde cuándo.</summary>
    internal (DateTime Time, Dictionary<long, decimal> Bids, Dictionary<long, decimal> Asks)? BookAgo(DateTime now)
    {
        if (_book.Count == 0) return null;
        var target = now - TimeSpan.FromSeconds(Math.Max(5, Settings.ChangeWindowSeconds));
        (DateTime, Dictionary<long, decimal>, Dictionary<long, decimal>)? best = null;
        foreach (var s in _book)
        {
            best ??= s;
            if (s.Time <= target) best = s;
            else break;
        }

        return best;
    }

    /// <summary>
    /// El paso automático: las filas visibles abarcan ± el rango elegido alrededor del precio, pero nunca más que lo que
    /// llega del libro (<paramref name="bookHalfSpan"/>: del precio al nivel más lejano de cada lado; 0 = no se sabe).
    /// </summary>
    public static int AutoStepFor(decimal price, decimal tick, int rows, double rangePercent, decimal bookHalfSpan = 0)
    {
        if (price <= 0 || tick <= 0 || rows <= 0) return 1;
        double half = (double)price * Math.Clamp(rangePercent, 0.01, 50) / 100;
        if (bookHalfSpan > 0) half = Math.Min(half, (double)bookHalfSpan);
        double span = half * 2;
        long raw = (long)Math.Ceiling(span / rows / (double)tick);
        return Math.Max(1, CandleSeries.NiceStep(Math.Max(1, raw)));
    }
}

/// <summary>Dibuja el DOM.</summary>
public sealed class DomRenderer
{
    private readonly SKPaint _fill = new() { IsAntialias = false };
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private readonly List<(DomColumn Col, float X, float W)> _cols = new();

    private float _width, _height, _headerH, _statsH, _rowH = 18;
    private long _topRow;
    private int _rows;

    private ChartTheme? _theme;

    /// <summary>Colores (por defecto, la base del modo oscuro/claro de la app).</summary>
    public ChartTheme Theme
    {
        get => _theme ?? ChartTheme.Base;
        set => _theme = value;
    }

    /// <summary>Alto del encabezado (estadísticas + rótulos) en el último dibujo.</summary>
    public float HeaderHeight => _headerH;

    /// <summary>Fila de arriba en el último dibujo (si cambia, el DOM se movió).</summary>
    public long TopRow => _topRow;

    /// <summary>Rótulos de cada columna, del más largo al más corto.</summary>
    public static string[] HeaderLabels(DomColumn c) => c switch
    {
        DomColumn.Bid => new[] { "Bid", "B" },
        DomColumn.Price => new[] { "Precio", "Prec.", "$" },
        DomColumn.Ask => new[] { "Ask", "A" },
        DomColumn.Volume => new[] { "Volumen", "Vol.", "V" },
        DomColumn.Delta => new[] { "Delta", "Δ" },
        DomColumn.TradedBid => new[] { "Vol. bid", "V.bid", "Vb" },
        DomColumn.TradedAsk => new[] { "Vol. ask", "V.ask", "Va" },
        DomColumn.Size => new[] { "Cantidad", "Cant.", "#" },
        DomColumn.Total => new[] { "Total", "Tot.", "Σ" },
        DomColumn.Changes => new[] { "Cambios", "± libro", "±" },
        DomColumn.Recent => new[] { "Operado", "Oper.", "Op" },
        _ => new[] { c.ToString() },
    };

    /// <summary>El rótulo más largo que entra en el ancho, o null si no entra ninguno.</summary>
    public static string? FitLabel(DomColumn c, float width, Func<string, float> measure)
    {
        foreach (var s in HeaderLabels(c))
        {
            string t = UiText.Tr(s);
            if (measure(t) <= width - 6) return t;
        }

        return null;
    }

    /// <summary>Reparte el ancho; la columna de precio nunca queda más angosta que el precio (si no, se corta).</summary>
    private void ComputeColumns(DomSettings s, float width, float priceMin = 0)
    {
        _cols.Clear();
        var visible = s.Columns.Where(c => c.Visible && c.Column is not (DomColumn.BuyOrders or DomColumn.SellOrders)).ToList();
        if (visible.Count == 0) visible.Add(new DomColumnSetting { Column = DomColumn.Price });
        double total = visible.Sum(c => Math.Max(0.1, c.Width));
        var price = visible.FirstOrDefault(c => c.Column == DomColumn.Price);
        float priceW = price != null ? (float)(width * Math.Max(0.1, price.Width) / total) : 0;
        bool reserve = price != null && visible.Count > 1 && priceW < priceMin && width > priceMin * 1.3f;
        double others = total - (price != null ? Math.Max(0.1, price.Width) : 0);
        float x = 0;
        foreach (var c in visible)
        {
            float w = !reserve
                ? (float)(width * Math.Max(0.1, c.Width) / total)
                : c.Column == DomColumn.Price ? priceMin : (float)((width - priceMin) * Math.Max(0.1, c.Width) / Math.Max(0.1, others));
            _cols.Add((c.Column, x, w));
            x += w;
        }
    }

    private bool TryColumn(DomColumn c, out float x, out float w)
    {
        foreach (var (col, cx, cw) in _cols)
        {
            if (col != c) continue;
            x = cx;
            w = cw;
            return true;
        }

        x = w = 0;
        return false;
    }

    private static SKColor C(System.Drawing.Color c) => new(c.R, c.G, c.B, c.A);

    /// <summary>Texto corto de un tamaño en la unidad elegida.</summary>
    public static string Amount(decimal coins, decimal price, DomUnit unit, int decimals)
    {
        if (unit == DomUnit.Usd)
        {
            decimal usd = coins * price;
            decimal a = Math.Abs(usd);
            string s = a >= 1_000_000_000 ? (a / 1_000_000_000).ToString("0.##") + "B"
                : a >= 1_000_000 ? (a / 1_000_000).ToString("0.##") + "M"
                : a >= 1_000 ? (a / 1_000).ToString("0.#") + "K"
                : a.ToString("0");
            return (usd < 0 ? "-" : "") + "$" + s;
        }

        return VolumeFormat.Compact(coins, decimals);
    }

    public void Render(SKCanvas canvas, DomModel dom, float width, float height, float? mouseY = null)
    {
        var s = dom.Settings;
        _width = width;
        _height = height;
        _rowH = dom.RowHeight;
        var t = Theme;
        var inst = dom.Instrument;
        var book = dom.Data.Depth;
        var now = DateTime.UtcNow;
        dom.RememberBook(now);
        var unit = s.Unit;

        // la letra nunca más alta que la fila
        float size = Math.Min(Math.Clamp(s.FontSize, 7, 24), _rowH - 3);
        var font = RenderContext.GetFont(t.FontFamily, size, false, false);
        var bold = RenderContext.GetFont(t.FontFamily, size, true, false);
        decimal last = dom.Data.LastPrice > 0 ? dom.Data.LastPrice : book.HasBid ? inst.FromTicks(book.BestBidTicks) : 0;
        decimal sample = last > 0 ? last : inst.TickSize * 100_000;
        ComputeColumns(s, width, RenderContext.MeasureWidth(bold, inst.FormatPrice(sample)) + 10);
        var small = RenderContext.GetFont(t.FontFamily, Math.Max(7, size - 1.5f), false, false);
        var header = RenderContext.GetFont(t.FontFamily, Math.Clamp(s.FontSize - 1.5f, 8, 14), false, false);
        _statsH = s.ShowStats ? MathF.Ceiling(header.Spacing * 2 + 10) : 0;
        _headerH = _statsH + (s.ShowHeader ? MathF.Ceiling(header.Spacing + 9) : 0);

        _fill.Color = t.BackgroundTop;
        canvas.DrawRect(0, 0, width, height, _fill);

        _rows = Math.Max(1, (int)((height - _headerH) / _rowH));
        int drawRows = _rows + 1; // fila parcial abajo

        // tick automático: el paso sale del precio y de cuántas filas entran
        if (s.AutoStep && last > 0)
        {
            // lo que abarca el libro: del precio al nivel más lejano (el lado más corto manda, para que se vean los dos)
            decimal reach = 0;
            if (book.HasBid && book.HasAsk)
                reach = Math.Min(last - inst.FromTicks(book.Bids[^1].PriceTicks), inst.FromTicks(book.Asks[^1].PriceTicks) - last);
            int auto = DomModel.AutoStepFor(last, inst.TickSize, _rows, s.AutoRangePercent, Math.Max(reach, inst.TickSize * 10));
            if (auto != dom.Step)
            {
                // el centro sigue en el mismo precio
                dom.Step = auto;
            }
        }
        else if (!s.AutoStep && dom.Step != Math.Max(1, s.ManualStep)) dom.Step = Math.Max(1, s.ManualStep);

        long lastTicks = dom.Data.Trades.Count > 0 ? dom.Data.Trades.GetPriceTicks(dom.Data.Trades.Count - 1) : (book.HasBid ? book.BestBidTicks : 0);
        long lastRow = dom.RowOf(lastTicks);
        long centerRow = dom.RowOf(dom.CenterTicks);
        // recentra si el precio se va del tercio central
        if (dom.CenterTicks == 0 || (dom.AutoCenter && Math.Abs(lastRow - centerRow) > _rows / 3)) dom.CenterTicks = lastRow * dom.Step;

        long center = dom.RowOf(dom.CenterTicks);
        _topRow = center + _rows / 2;
        long bottomRow = _topRow - drawRows + 1;

        // libro por fila
        var bids = new Dictionary<long, decimal>();
        var asks = new Dictionary<long, decimal>();
        foreach (var l in book.Bids) bids[dom.RowOf(l.PriceTicks)] = bids.GetValueOrDefault(dom.RowOf(l.PriceTicks)) + l.Size;
        foreach (var l in book.Asks) asks[dom.RowOf(l.PriceTicks)] = asks.GetValueOrDefault(dom.RowOf(l.PriceTicks)) + l.Size;
        long bestBidRow = book.HasBid ? dom.RowOf(book.BestBidTicks) : long.MinValue;
        long bestAskRow = book.HasAsk ? dom.RowOf(book.BestAskTicks) : long.MaxValue;
        decimal mid = book.HasBid && book.HasAsk ? (inst.FromTicks(book.BestBidTicks) + inst.FromTicks(book.BestAskTicks)) / 2 : last;

        // total acumulado desde el mejor precio de cada lado
        var cumBid = new Dictionary<long, decimal>();
        var cumAsk = new Dictionary<long, decimal>();
        {
            decimal acc = 0;
            foreach (var r in bids.Keys.Where(r => r <= bestBidRow).OrderByDescending(r => r))
            {
                acc += bids[r];
                cumBid[r] = acc;
            }

            acc = 0;
            foreach (var r in asks.Keys.Where(r => r >= bestAskRow).OrderBy(r => r))
            {
                acc += asks[r];
                cumAsk[r] = acc;
            }
        }

        // cambios del libro en la ventana (por fila) y operado reciente
        var change = new Dictionary<long, decimal>();
        var ago = dom.BookAgo(now);
        if (ago is { } old && now - old.Time >= TimeSpan.FromSeconds(2))
        {
            var oldB = new Dictionary<long, decimal>();
            var oldA = new Dictionary<long, decimal>();
            foreach (var (k, v) in old.Bids) oldB[dom.RowOf(k)] = oldB.GetValueOrDefault(dom.RowOf(k)) + v;
            foreach (var (k, v) in old.Asks) oldA[dom.RowOf(k)] = oldA.GetValueOrDefault(dom.RowOf(k)) + v;
            for (long r = bottomRow; r <= _topRow; r++)
            {
                decimal nowSz = r >= bestAskRow ? asks.GetValueOrDefault(r) : r <= bestBidRow ? bids.GetValueOrDefault(r) : 0;
                decimal oldSz = r >= bestAskRow ? oldA.GetValueOrDefault(r) : r <= bestBidRow ? oldB.GetValueOrDefault(r) : 0;
                if (nowSz != oldSz) change[r] = nowSz - oldSz;
            }
        }

        var recent = new Dictionary<long, (decimal Bid, decimal Ask)>();
        {
            var store = dom.Data.Trades;
            var since = now - TimeSpan.FromSeconds(Math.Max(5, s.ChangeWindowSeconds));
            for (int i = store.Count - 1; i >= 0 && store.GetTime(i) >= since; i--)
            {
                if (store.GetId(i) < 0) continue;
                long r = dom.RowOf(store.GetPriceTicks(i));
                recent.TryGetValue(r, out var v);
                var vol = store.GetVolume(i);
                recent[r] = store.GetSide(i) == TradeSide.Sell ? (v.Bid + vol, v.Ask) : (v.Bid, v.Ask + vol);
            }
        }

        // operado en la sesión por fila (se guarda por tick)
        var session = new Dictionary<long, (decimal Bid, decimal Ask)>();
        foreach (var (tk, v) in dom.SessionVolume)
        {
            long r = dom.RowOf(tk);
            if (r < bottomRow || r > _topRow) continue;
            var cur = session.GetValueOrDefault(r);
            session[r] = (cur.Bid + v.Bid, cur.Ask + v.Ask);
        }

        decimal PriceOf(long r) => r * dom.Step * inst.TickSize;
        decimal U(decimal coins, long r) => unit == DomUnit.Usd ? coins * PriceOf(r) : coins;

        decimal maxBook = 0, maxVol = 0, maxDelta = 0, maxSide = 0, maxCum = 0, maxChange = 0, maxRecent = 0;
        for (long r = bottomRow; r <= _topRow; r++)
        {
            maxBook = Math.Max(maxBook, U(Math.Max(bids.GetValueOrDefault(r), asks.GetValueOrDefault(r)), r));
            maxCum = Math.Max(maxCum, U(Math.Max(cumBid.GetValueOrDefault(r), cumAsk.GetValueOrDefault(r)), r));
            if (change.TryGetValue(r, out var ch)) maxChange = Math.Max(maxChange, Math.Abs(U(ch, r)));
            if (recent.TryGetValue(r, out var rc)) maxRecent = Math.Max(maxRecent, U(rc.Bid + rc.Ask, r));
            if (!session.TryGetValue(r, out var sv)) continue;
            maxVol = Math.Max(maxVol, U(sv.Bid + sv.Ask, r));
            maxDelta = Math.Max(maxDelta, Math.Abs(U(sv.Ask - sv.Bid, r)));
            maxSide = Math.Max(maxSide, U(Math.Max(sv.Bid, sv.Ask), r));
        }

        // «grande»: el umbral elegido, o automático (el 85 % del más grande visible y al menos 3 veces la mediana)
        decimal largeAt = s.LargeSize;
        if (largeAt <= 0)
        {
            var sizes = new List<decimal>();
            for (long r = bottomRow; r <= _topRow; r++)
            {
                decimal v = U(r >= bestAskRow ? asks.GetValueOrDefault(r) : r <= bestBidRow ? bids.GetValueOrDefault(r) : 0, r);
                if (v > 0) sizes.Add(v);
            }

            sizes.Sort();
            largeAt = sizes.Count >= 5 ? Math.Max(sizes[^1] * 0.85m, sizes[sizes.Count / 2] * 3) : decimal.MaxValue;
        }

        int bookDec = FootprintRendererShim.Decimals(maxBook, inst.VolumeDecimals);
        int volDec = FootprintRendererShim.Decimals(maxVol, inst.VolumeDecimals);
        int hoverRow = mouseY is float my && my > _headerH ? (int)((my - _headerH) / _rowH) : -1;

        // marcas de distancia: la fila más cercana a cada % del precio, de cada lado
        var marks = new Dictionary<long, string>();
        if (mid > 0)
            foreach (var pct in s.Marks())
            {
                string label = pct.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";
                long up = dom.RowOf(inst.ToTicks(mid * (1 + (decimal)pct / 100)));
                long dn = dom.RowOf(inst.ToTicks(mid * (1 - (decimal)pct / 100)));
                marks.TryAdd(up, label);
                marks.TryAdd(dn, label);
            }

        // fondos de fila
        var bidBg = C(s.BidBackground);
        var askBg = C(s.AskBackground);
        var spreadBg = C(s.SpreadBackground);
        for (int i = 0; i < drawRows; i++)
        {
            long r = _topRow - i;
            float y = _headerH + i * _rowH;
            _fill.Color = r >= bestAskRow ? askBg : r <= bestBidRow ? bidBg : spreadBg;
            canvas.DrawRect(0, y, width, _rowH, _fill);
            if (i != hoverRow) continue;
            _fill.Color = new SKColor(255, 255, 255, 18);
            canvas.DrawRect(0, y, width, _rowH, _fill);
        }

        var bidColor = C(s.BidColor);
        var askColor = C(s.AskColor);
        var buyVol = C(s.BuyVolumeColor);
        var sellVol = C(s.SellVolumeColor);
        var lastColor = C(s.LastPriceColor);
        var priceText = C(s.PriceText);
        var pos = dom.Broker?.Position;
        long posRow = pos != null && pos.Volume != 0 ? dom.RowOf(inst.ToTicks(pos.AveragePrice)) : long.MinValue;

        foreach (var (col, x, w) in _cols)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(x, _headerH, x + w, height));
            for (int i = 0; i < drawRows; i++)
            {
                long r = _topRow - i;
                float y = _headerH + i * _rowH;
                decimal px = PriceOf(r);
                switch (col)
                {
                    case DomColumn.Bid when r <= bestBidRow && bids.TryGetValue(r, out var bsz) && bsz > 0:
                        BookCell(canvas, x, y, w, bsz, px, maxBook, bidColor, r == bestBidRow, rightAligned: true, s, bookDec, font, bold, largeAt, null);
                        break;
                    case DomColumn.Ask when r >= bestAskRow && asks.TryGetValue(r, out var asz) && asz > 0:
                        BookCell(canvas, x, y, w, asz, px, maxBook, askColor, r == bestAskRow, rightAligned: false, s, bookDec, font, bold, largeAt, null);
                        break;
                    case DomColumn.Size:
                    {
                        bool isAsk = r >= bestAskRow, isBid = r <= bestBidRow;
                        decimal sz = isAsk ? asks.GetValueOrDefault(r) : isBid ? bids.GetValueOrDefault(r) : 0;
                        if (sz > 0)
                            BookCell(canvas, x, y, w, sz, px, maxBook, isAsk ? askColor : bidColor, r == bestAskRow || r == bestBidRow, rightAligned: true, s, bookDec, font, bold, largeAt,
                                marks.GetValueOrDefault(r));
                        else if (marks.TryGetValue(r, out var mk)) Text(canvas, mk, x, y, w, small, t.TextDim, -1);
                        break;
                    }
                    case DomColumn.Total:
                    {
                        decimal cum = r >= bestAskRow ? cumAsk.GetValueOrDefault(r) : r <= bestBidRow ? cumBid.GetValueOrDefault(r) : 0;
                        if (cum <= 0 || maxCum <= 0) break;
                        decimal u = U(cum, r);
                        float bw = (float)(u / maxCum) * (w - 4);
                        _fill.Color = (r >= bestAskRow ? askColor : bidColor).WithAlpha(60);
                        canvas.DrawRect(x + w - 2 - bw, y + 2, bw, _rowH - 4, _fill);
                        Text(canvas, DomRenderer.Amount(cum, px, unit, bookDec), x, y, w, small, t.Text, 1);
                        break;
                    }
                    case DomColumn.Changes when change.TryGetValue(r, out var ch) && maxChange > 0:
                    {
                        decimal u = U(ch, r);
                        float bw = (float)(Math.Abs(u) / maxChange) * (w / 2 - 2);
                        float cx = x + w / 2;
                        _fill.Color = (ch > 0 ? buyVol : sellVol).WithAlpha(170);
                        if (ch > 0) canvas.DrawRect(cx, y + 2, bw, _rowH - 4, _fill);
                        else canvas.DrawRect(cx - bw, y + 2, bw, _rowH - 4, _fill);
                        Text(canvas, (ch > 0 ? "+" : "−") + DomRenderer.Amount(Math.Abs(ch), px, unit, bookDec), x, y, w, small, t.Text, ch > 0 ? -1 : 1);
                        break;
                    }
                    case DomColumn.Recent when recent.TryGetValue(r, out var rc) && maxRecent > 0:
                    {
                        decimal tot = rc.Bid + rc.Ask;
                        float vw = (float)(U(tot, r) / maxRecent) * (w - 4);
                        float bidW = tot > 0 ? vw * (float)(rc.Bid / tot) : 0;
                        _fill.Color = sellVol;
                        canvas.DrawRect(x + 2, y + 2, bidW, _rowH - 4, _fill);
                        _fill.Color = buyVol;
                        canvas.DrawRect(x + 2 + bidW, y + 2, vw - bidW, _rowH - 4, _fill);
                        Text(canvas, DomRenderer.Amount(tot, px, unit, volDec), x, y, w, small, t.Text, -1);
                        break;
                    }
                    case DomColumn.Price:
                    {
                        bool isLast = r == lastRow;
                        if (isLast)
                        {
                            _fill.Color = lastColor;
                            canvas.DrawRect(x + 1, y + 1, w - 2, _rowH - 2, _fill);
                        }

                        bool round = px % (inst.TickSize * dom.Step * 10) == 0;
                        Text(canvas, inst.FormatPrice(px), x, y, w, isLast ? bold : font, isLast ? SKColors.Black : round ? t.Text : priceText, 0);
                        if (!isLast && marks.ContainsKey(r))
                        {
                            _fill.Color = t.TextDim.WithAlpha(160);
                            canvas.DrawRect(x + 1, y + _rowH - 2, w - 2, 1, _fill);
                        }

                        break;
                    }
                    case DomColumn.Volume when session.TryGetValue(r, out var sv) && maxVol > 0:
                    {
                        decimal tot = sv.Bid + sv.Ask;
                        float vw = (float)(U(tot, r) / maxVol) * (w - 4);
                        float bidW = tot > 0 ? vw * (float)(sv.Bid / tot) : 0;
                        _fill.Color = sellVol;
                        canvas.DrawRect(x + 2, y + 2, bidW, _rowH - 4, _fill);
                        _fill.Color = buyVol;
                        canvas.DrawRect(x + 2 + bidW, y + 2, vw - bidW, _rowH - 4, _fill);
                        Text(canvas, DomRenderer.Amount(tot, px, unit, volDec), x, y, w, small, t.Text, -1);
                        break;
                    }
                    case DomColumn.Delta when session.TryGetValue(r, out var sv) && maxDelta > 0:
                    {
                        decimal d = sv.Ask - sv.Bid;
                        if (d == 0) break;
                        float dw = (float)(Math.Abs(U(d, r)) / maxDelta) * (w - 4);
                        _fill.Color = d > 0 ? buyVol : sellVol;
                        canvas.DrawRect(x + 2, y + 2, dw, _rowH - 4, _fill);
                        Text(canvas, (d > 0 ? "+" : "") + DomRenderer.Amount(d, px, unit, volDec), x, y, w, small, t.Text, -1);
                        break;
                    }
                    case DomColumn.TradedBid when session.TryGetValue(r, out var sv) && sv.Bid > 0 && maxSide > 0:
                    {
                        float bw = (float)(U(sv.Bid, r) / maxSide) * (w - 4);
                        _fill.Color = sellVol;
                        canvas.DrawRect(x + w - 2 - bw, y + 2, bw, _rowH - 4, _fill);
                        Text(canvas, DomRenderer.Amount(sv.Bid, px, unit, volDec), x, y, w, small, t.Text, 1);
                        break;
                    }
                    case DomColumn.TradedAsk when session.TryGetValue(r, out var sv) && sv.Ask > 0 && maxSide > 0:
                    {
                        float aw = (float)(U(sv.Ask, r) / maxSide) * (w - 4);
                        _fill.Color = buyVol;
                        canvas.DrawRect(x + 2, y + 2, aw, _rowH - 4, _fill);
                        Text(canvas, DomRenderer.Amount(sv.Ask, px, unit, volDec), x, y, w, small, t.Text, -1);
                        break;
                    }
                }
            }

            canvas.Restore();
        }

        // posición: recuadro en el precio promedio (solo informa)
        if (posRow != long.MinValue)
        {
            int i = (int)(_topRow - posRow);
            if (i >= 0 && i < drawRows)
            {
                float y = _headerH + i * _rowH;
                float px = 0, pw = width;
                if (TryColumn(DomColumn.Price, out var cx, out var cw))
                {
                    px = cx;
                    pw = cw;
                }

                _stroke.Color = pos!.Volume > 0 ? t.PositionLong : t.PositionShort;
                _stroke.StrokeWidth = 2;
                canvas.DrawRect(px + 1.5f, y + 1.5f, pw - 3, _rowH - 3, _stroke);
                _stroke.StrokeWidth = 1;
            }
        }

        // líneas de columnas
        _stroke.Color = new SKColor(40, 46, 60);
        foreach (var (_, x, _) in _cols.Skip(1))
        {
            float xx = MathF.Floor(x) + 0.5f;
            canvas.DrawLine(xx, _statsH, xx, height, _stroke);
        }

        // Mirror: del precio a la fila del mouse contra la misma distancia del otro lado
        if (s.HoverMirror && hoverRow >= 0 && hoverRow < drawRows && mid > 0)
            DrawMirror(canvas, dom, _topRow - hoverRow, mid, bids, asks, bestBidRow, bestAskRow, unit, small, bookDec);

        if (s.ShowStats) DrawStats(canvas, dom, book, mid, unit, header);
        if (s.ShowHeader) DrawHeader(canvas, header);
    }

    private void BookCell(SKCanvas canvas, float x, float y, float w, decimal size, decimal price, decimal max, SKColor color, bool best, bool rightAligned,
        DomSettings s, int decimals, SKFont font, SKFont bold, decimal largeAt, string? mark)
    {
        decimal u = s.Unit == DomUnit.Usd ? size * price : size;
        if (s.BookBars)
        {
            _fill.Color = color.WithAlpha(best ? (byte)230 : (byte)150);
            float bw = max > 0 ? (float)(u / max) * (w - 2) : 0;
            canvas.DrawRect(rightAligned ? x + w - 1 - bw : x + 1, y + 1, bw, _rowH - 2, _fill);
        }
        else if (best)
        {
            _fill.Color = color.WithAlpha(120);
            canvas.DrawRect(x + 1, y + 1, w - 2, _rowH - 2, _fill);
        }

        bool isLarge = u >= largeAt;
        if (isLarge)
        {
            _stroke.Color = C(s.LastPriceColor);
            _stroke.StrokeWidth = 1.5f;
            canvas.DrawRect(x + 1.5f, y + 1.5f, w - 3, _rowH - 3, _stroke);
            _stroke.StrokeWidth = 1;
        }

        if (mark != null) Text(canvas, mark, x, y, w, RenderContext.GetFont(Theme.FontFamily, Math.Max(7, font.Size - 2), false, false), Theme.TextDim, rightAligned ? -1 : 1);
        Text(canvas, Amount(size, price, s.Unit, decimals), x, y, w, best || isLarge ? bold : font, SKColors.White, rightAligned ? 1 : -1);
    }

    /// <summary>
    /// Arriba del DOM: spread y, a 0,5 / 2 / 10 % del precio, cuánto hay de cada lado (como las estadísticas de
    /// crypto-orderbook), con una barra de bids contra asks de lo que se ve (como LiquidityMapperFast).
    /// </summary>
    private void DrawStats(SKCanvas canvas, DomModel dom, DepthSnapshot book, decimal mid, DomUnit unit, SKFont font)
    {
        var t = Theme;
        var inst = dom.Instrument;
        _fill.Color = t.AxisBackground;
        canvas.DrawRect(0, 0, _width, _statsH, _fill);
        if (mid <= 0 || (!book.HasBid && !book.HasAsk))
        {
            _text.Color = t.TextDim;
            canvas.DrawUiText(UiText.Tr("Sin libro todavía"), 6, font.Spacing + 2, font, _text);
            return;
        }

        decimal Within(BookLevel[] side, decimal pct, bool bidSide)
        {
            decimal lim = bidSide ? mid * (1 - pct / 100) : mid * (1 + pct / 100);
            decimal sum = 0;
            foreach (var l in side)
            {
                decimal p = inst.FromTicks(l.PriceTicks);
                if (bidSide ? p < lim : p > lim) break;
                sum += unit == DomUnit.Usd ? l.Size * p : l.Size;
            }

            return sum;
        }

        decimal spread = book.HasBid && book.HasAsk ? inst.FromTicks(book.BestAskTicks) - inst.FromTicks(book.BestBidTicks) : 0;
        int dec = inst.VolumeDecimals;
        string F(decimal v) => unit == DomUnit.Usd ? Amount(v, 1, DomUnit.Usd, 0) : VolumeFormat.Compact(v, FootprintRendererShim.Decimals(v, dec));
        float y1 = font.Spacing + 2, y2 = font.Spacing * 2 + 6;
        string head = $"{UiText.Tr("Spread")} {inst.FormatPrice(spread)} ({(double)(spread / mid) * 1e4:0.0} bp) · ×{dom.Step} tick";
        _text.Color = t.TextDim;
        canvas.DrawUiText(head, 6, y1, font, _text);
        float x = 6;
        var bidC = C(dom.Settings.BidColor).WithAlpha(255);
        var askC = C(dom.Settings.AskColor).WithAlpha(255);
        foreach (var pct in new[] { 0.5m, 2m, 10m })
        {
            decimal b = Within(book.Bids, pct, true), a = Within(book.Asks, pct, false);
            string label = pct.ToString("0.#") + "%";
            _text.Color = t.TextDim;
            canvas.DrawUiText(label, x, y2, font, _text);
            x += RenderContext.MeasureWidth(font, label) + 3;
            // barrita bid / ask
            float bw = 28;
            decimal tot = a + b;
            float split = tot > 0 ? (float)(b / tot) * bw : bw / 2;
            _fill.Color = bidC.WithAlpha(200);
            canvas.DrawRect(x, y2 - font.Size * 0.7f, split, font.Size * 0.7f, _fill);
            _fill.Color = askC.WithAlpha(200);
            canvas.DrawRect(x + split, y2 - font.Size * 0.7f, bw - split, font.Size * 0.7f, _fill);
            x += bw + 3;
            string v = $"{F(b)}/{F(a)}";
            _text.Color = t.Text;
            canvas.DrawUiText(v, x, y2, font, _text);
            x += RenderContext.MeasureWidth(font, v) + 10;
            if (x > _width - 40) break;
        }

        _stroke.Color = new SKColor(40, 46, 60);
        canvas.DrawLine(0, _statsH - 0.5f, _width, _statsH - 0.5f, _stroke);
    }

    /// <summary>El mouse en una fila: lo que hay del precio a esa fila y en la misma distancia del otro lado.</summary>
    private void DrawMirror(SKCanvas canvas, DomModel dom, long hover, decimal mid, Dictionary<long, decimal> bids, Dictionary<long, decimal> asks,
        long bestBidRow, long bestAskRow, DomUnit unit, SKFont font, int dec)
    {
        var inst = dom.Instrument;
        long midRow = dom.RowOf(inst.ToTicks(mid));
        long dist = Math.Abs(hover - midRow);
        if (dist == 0) return;
        decimal near = 0, far = 0;
        bool above = hover > midRow;
        for (long k = 0; k <= dist; k++)
        {
            long ra = midRow + k, rb = midRow - k;
            decimal va = ra >= bestAskRow ? asks.GetValueOrDefault(ra) : 0, vb = rb <= bestBidRow ? bids.GetValueOrDefault(rb) : 0;
            decimal pa = ra * dom.Step * inst.TickSize, pb = rb * dom.Step * inst.TickSize;
            decimal ua = unit == DomUnit.Usd ? va * pa : va, ub = unit == DomUnit.Usd ? vb * pb : vb;
            if (above)
            {
                near += ua;
                far += ub;
            }
            else
            {
                near += ub;
                far += ua;
            }
        }

        // marco del otro lado (la fila espejo)
        long mirror = midRow - (hover - midRow);
        int iMirror = (int)(_topRow - mirror);
        if (iMirror >= 0 && iMirror <= _rows)
        {
            _stroke.Color = new SKColor(242, 181, 58, 200);
            canvas.DrawRect(0.5f, _headerH + iMirror * _rowH + 0.5f, _width - 1, _rowH - 1, _stroke);
        }

        decimal pctAway = Math.Abs(hover * dom.Step * inst.TickSize - mid) / mid * 100;
        string sideNear = above ? UiText.Tr("asks") : UiText.Tr("bids"), sideFar = above ? UiText.Tr("bids") : UiText.Tr("asks");
        string ratio = near > 0 && far > 0 ? (far >= near ? $"{sideFar} ×{far / near:0.00}" : $"{sideNear} ×{near / far:0.00}") : "";
        string text = $"±{pctAway:0.##}% · {sideNear} {Amount(near, 1, unit == DomUnit.Usd ? DomUnit.Usd : DomUnit.Quantity, dec)} · {sideFar} {Amount(far, 1, unit == DomUnit.Usd ? DomUnit.Usd : DomUnit.Quantity, dec)}" + (ratio.Length > 0 ? " · " + ratio : "");
        float tw = RenderContext.MeasureWidth(font, text) + 10;
        int iHover = (int)(_topRow - hover);
        float y = _headerH + iHover * _rowH + (above ? _rowH + 2 : -font.Spacing - 6);
        y = Math.Clamp(y, _headerH, _height - font.Spacing - 6);
        float x = Math.Max(2, _width - tw - 4);
        _fill.Color = new SKColor(20, 24, 32, 235);
        canvas.DrawRoundRect(new SKRect(x, y, x + tw, y + font.Spacing + 4), 4, 4, _fill);
        _text.Color = new SKColor(235, 235, 240);
        canvas.DrawUiText(text, x + 5, y + font.Spacing, font, _text);
    }

    private void Text(SKCanvas canvas, string s, float x, float y, float w, SKFont f, SKColor color, int align)
    {
        float tw = RenderContext.MeasureWidth(f, s);
        float tx = align < 0 ? x + 4 : align > 0 ? x + w - 4 - tw : x + (w - tw) / 2;
        float ty = y + _rowH / 2 - (f.Metrics.Ascent + f.Metrics.Descent) / 2;
        _text.Color = color;
        canvas.DrawUiText(s, tx, ty, f, _text);
    }

    private void DrawHeader(SKCanvas canvas, SKFont font)
    {
        var t = Theme;
        float top = _statsH, h = _headerH - _statsH;
        _fill.Color = t.AxisBackground;
        canvas.DrawRect(0, top, _width, h, _fill);
        float baseline = top + h / 2 - (font.Metrics.Ascent + font.Metrics.Descent) / 2;
        foreach (var (col, x, w) in _cols)
        {
            // el rótulo se abrevia según el ancho y nunca se sale de su columna
            var label = FitLabel(col, w, s => RenderContext.MeasureWidth(font, s));
            if (label == null) continue;
            canvas.Save();
            canvas.ClipRect(new SKRect(x, top, x + w, _headerH));
            _text.Color = t.TextDim;
            canvas.DrawUiText(label, x + w / 2 - RenderContext.MeasureWidth(font, label) / 2, baseline, font, _text);
            canvas.Restore();
        }

        _stroke.Color = new SKColor(40, 46, 60);
        canvas.DrawLine(0, _headerH - 0.5f, _width, _headerH - 0.5f, _stroke);
    }

    /// <summary>Qué hay bajo el mouse: columna y precio.</summary>
    public (DomColumn Column, decimal Price)? HitTest(DomModel dom, float x, float y)
    {
        if (y < _headerH || _width <= 0) return null;
        int i = (int)((y - _headerH) / _rowH);
        long r = _topRow - i;
        decimal price = r * dom.Step * dom.Instrument.TickSize;
        foreach (var (c, cx, cw) in _cols)
            if (x >= cx && x < cx + cw) return (c, price);
        return null;
    }

    public void Scroll(DomModel dom, int rows)
    {
        dom.AutoCenter = false;
        long center = dom.RowOf(dom.CenterTicks) + rows;
        dom.CenterTicks = center * dom.Step;
    }
}

internal static class FootprintRendererShim
{
    public static int Decimals(decimal max, int instrumentDecimals) => Rendering.FootprintRenderer.ChooseDecimals(max, instrumentDecimals);
}
