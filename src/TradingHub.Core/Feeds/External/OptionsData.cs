using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TradingHub.Feeds.External;

// Datos de opciones para los indicadores de opciones: una interfaz (IOptionsProvider) que cualquier proveedor cumple, un
// centro (OptionsData) que la conexión elegida en Conexiones configura y que los indicadores consultan (con caché), y dos
// proveedores: Deribit (público, BTC y ETH) y una API propia por URL con un JSON documentado. Si no hay ninguna conectada,
// los indicadores lo dicen y se quedan esperando: cuando se conecta una, andan solos.

public enum OptionRight
{
    Call,
    Put,
}

/// <summary>Un contrato de la cadena: precio, volatilidad implícita (fracción: 0.55 = 55 %), griegas e interés abierto.</summary>
public sealed record OptionContract(
    string Symbol, DateTime Expiry, double Strike, OptionRight Right,
    double Bid, double Ask, double Mark, double Iv,
    double Delta, double Gamma, double Vega, double Theta,
    double OpenInterest, double Volume, double ContractSize)
{
    public double DaysToExpiry(DateTime now) => Math.Max(1.0 / 24, (Expiry - now).TotalDays);
}

/// <summary>Una foto de la cadena de opciones de un subyacente.</summary>
public sealed record OptionsChain(string Underlying, DateTime Time, double Spot, IReadOnlyList<OptionContract> Contracts);

/// <summary>Una operación de opciones: prima por contrato (USD), contratos y quién agredió (+1 compró en el ask, −1 vendió en el bid, 0 no se sabe).</summary>
public sealed record OptionTrade(DateTime Time, string Symbol, DateTime Expiry, double Strike, OptionRight Right, double Price, double Size, int Aggressor, double Iv, double Spot, double ContractSize, string Venue = "")
{
    /// <summary>Alcista: compra de calls o venta de puts; bajista: compra de puts o venta de calls (0 si no se sabe el lado).</summary>
    public int Bias => Aggressor * (Right == OptionRight.Call ? 1 : -1);

    /// <summary>La delta (Black–Scholes con la IV de la operación).</summary>
    public double Delta => OptionsMath.Greeks(Spot, Strike, Math.Max(1.0 / 365, (Expiry - Time).TotalDays / 365), Iv > 0 ? Iv : 0.6, Right).Delta;

    /// <summary>Prima total en dólares.</summary>
    public double Premium => Price * Size;
}

/// <summary>Lo de un día de un subyacente (para historias y rangos: IV, sesgo, put/call, gamma de los creadores de mercado).</summary>
public sealed record OptionDaily(
    DateTime Date, double Spot, double AtmIv, double Rr25, double PutCallVolume, double PutCallOi,
    double CallOi, double PutOi, double CallVolume, double PutVolume,
    double Gex, double CallWall, double PutWall, double GammaFlip, double ExpectedMove);

/// <summary>Un ejercicio en una foto: interés abierto de calls y puts, gamma de los creadores de mercado (USD por 1 %) y volumen.</summary>
public sealed record StrikeRow(double Strike, double CallOi, double PutOi, double Gex, double CallVolume, double PutVolume);

/// <summary>Una foto por ejercicio de la cadena en un instante.</summary>
public sealed record StrikeSnapshot(DateTime Time, double Spot, IReadOnlyList<StrikeRow> Rows);

/// <summary>Lo que tiene que dar un proveedor de opciones.</summary>
public interface IOptionsProvider
{
    string Name { get; }

    /// <summary>Si tiene opciones de ese subyacente (BTC, ETH, SPY…).</summary>
    bool Supports(string underlying);

    Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct);

    Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct);

    /// <summary>La historia diaria (puede venir vacía: entonces se arma con las fotos que se van guardando).</summary>
    Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>
/// El centro de datos de opciones: el proveedor conectado (o ninguno) y la caché que comparten los indicadores. Las
/// llamadas no bloquean: devuelven lo que hay y piden lo que falta en segundo plano; <see cref="Changed"/> avisa.
/// </summary>
public static class OptionsData
{
    private static IOptionsProvider? _provider;
    private static readonly ConcurrentDictionary<string, (DateTime At, OptionsChain? Chain, string? Error)> Chains = new();
    private static readonly ConcurrentDictionary<string, (DateTime From, DateTime To, DateTime At, List<OptionTrade> Trades)> Trades = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, List<OptionDaily> Days)> Dailies = new();
    private static readonly ConcurrentDictionary<string, bool> Loading = new();

    /// <summary>Días de operaciones que se piden como mucho (las de opciones son muchas: más atrás no se baja).</summary>
    public static int MaxTradeDays { get; set; } = 5;

    /// <summary>Carpeta donde se guardan las fotos diarias que arma (null = solo memoria).</summary>
    public static string? Root { get; set; }

    /// <summary>Cambia cuando llega algo nuevo o cambia el proveedor (los indicadores vuelven a dibujar).</summary>
    public static event Action? Changed;

    public static long Generation { get; private set; }

    public static IOptionsProvider? Provider
    {
        get => _provider;
        set
        {
            _provider = value;
            Chains.Clear();
            Trades.Clear();
            Dailies.Clear();
            Bump();
        }
    }

    public static string? LastError { get; private set; }

    private static void Bump()
    {
        Generation++;
        Changed?.Invoke();
    }

    /// <summary>El subyacente de un símbolo del gráfico (BTCUSDT, BTC-PERP, BTCUSD.P → BTC; SPY → SPY).</summary>
    public static string UnderlyingOf(string? symbol)
    {
        var s = (symbol ?? "").ToUpperInvariant().Replace("-", "").Replace("/", "").Replace(".P", "").Replace("PERP", "");
        foreach (var q in new[] { "USDT", "USDC", "USD", "BUSD" })
            if (s.Length > q.Length && s.EndsWith(q, StringComparison.Ordinal))
                return s[..^q.Length];
        return s;
    }

    /// <summary>Por qué no hay datos para un subyacente (null = sí hay o se están trayendo).</summary>
    public static string? Unavailable(string underlying)
    {
        if (_provider == null) return "Sin datos de opciones: conectá un proveedor en Conexiones → «Datos de opciones (API)».";
        if (!_provider.Supports(underlying)) return $"{_provider.Name} no tiene opciones de {underlying}.";
        return null;
    }

    private static void Run(string key, Func<Task> work, Action<string> onError)
    {
        if (!Loading.TryAdd(key, true)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
                LastError = null;
            }
            catch (Exception ex)
            {
                // se anota la hora del intento: no se reintenta enseguida (sin red no se gira en falso)
                LastError = ex.Message;
                try { onError(ex.Message); }
                catch (Exception) { /* nada */ }
            }
            finally
            {
                Loading.TryRemove(key, out _);
                Bump();
            }
        });
    }

    /// <summary>La última foto de la cadena (se renueva cada <paramref name="maxAge"/>).</summary>
    public static OptionsChain? Chain(string underlying, TimeSpan? maxAge = null)
    {
        var p = _provider;
        if (p == null || !p.Supports(underlying)) return null;
        var age = maxAge ?? TimeSpan.FromSeconds(60);
        Chains.TryGetValue(underlying, out var hit);
        if (DateTime.UtcNow - hit.At > age)
            Run("chain:" + underlying, async () =>
            {
                var c = await p.ChainAsync(underlying, CancellationToken.None).ConfigureAwait(false);
                Chains[underlying] = (DateTime.UtcNow, c ?? hit.Chain, null);
                if (c != null)
                {
                    Record(underlying, c);
                    RecordSnapshot(underlying, c);
                }
            }, err => Chains[underlying] = (DateTime.UtcNow, hit.Chain, err));
        return hit.Chain;
    }

    /// <summary>
    /// Las operaciones entre dos instantes: devuelve lo que ya hay y pide lo que falta en tramos de un día (primero lo
    /// último, después hacia atrás hasta <see cref="MaxTradeDays"/>), y lo nuevo cada minuto.
    /// </summary>
    public static IReadOnlyList<OptionTrade> TradesBetween(string underlying, DateTime from, DateTime to)
    {
        var p = _provider;
        if (p == null || !p.Supports(underlying)) return Array.Empty<OptionTrade>();
        var now = DateTime.UtcNow;
        var floor = now.AddDays(-MaxTradeDays);
        if (from < floor) from = floor;
        if (to > now) to = now;
        Trades.TryGetValue(underlying, out var hit);
        var age = now - hit.At;
        if (hit.Trades == null)
        {
            if (age > TimeSpan.FromSeconds(15))
                Run("trades:" + underlying, async () =>
                {
                    var start = now.AddHours(-24) > from ? now.AddHours(-24) : from;
                    var got = await p.TradesAsync(underlying, start, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    Trades[underlying] = (start, DateTime.UtcNow, DateTime.UtcNow, got.OrderBy(t => t.Time).ToList());
                }, _ => Trades[underlying] = (now, now, DateTime.UtcNow, null!));
            return Array.Empty<OptionTrade>();
        }

        if (hit.From > from.AddMinutes(1) && age > TimeSpan.FromSeconds(2))
            Run("trades:" + underlying, async () =>
            {
                // un día más hacia atrás
                var start = hit.From.AddHours(-24) > from ? hit.From.AddHours(-24) : from;
                var got = await p.TradesAsync(underlying, start, hit.From, CancellationToken.None).ConfigureAwait(false);
                var list = got.Where(t => t.Time < hit.From).OrderBy(t => t.Time).Concat(hit.Trades).ToList();
                Trades[underlying] = (start, hit.To, DateTime.UtcNow, list);
            }, _ => Trades[underlying] = (hit.From, hit.To, DateTime.UtcNow, hit.Trades));
        else if (age > TimeSpan.FromMinutes(1))
            Run("trades:" + underlying, async () =>
            {
                var start = hit.To.AddMinutes(-5);
                var got = await p.TradesAsync(underlying, start, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
                var seen = new HashSet<(DateTime, string, double, double)>(hit.Trades.Where(t => t.Time >= start).Select(t => (t.Time, t.Symbol, t.Price, t.Size)));
                var list = hit.Trades.Concat(got.Where(t => t.Time >= start && !seen.Contains((t.Time, t.Symbol, t.Price, t.Size))).OrderBy(t => t.Time)).ToList();
                list.RemoveAll(t => t.Time < DateTime.UtcNow.AddDays(-MaxTradeDays - 1));
                Trades[underlying] = (list.Count > 0 && list[0].Time > hit.From ? list[0].Time : hit.From, DateTime.UtcNow, DateTime.UtcNow, list);
            }, _ => Trades[underlying] = (hit.From, hit.To, DateTime.UtcNow, hit.Trades));
        int lo = LowerBound(hit.Trades, from), hi = LowerBound(hit.Trades, to.AddTicks(1));
        return hi > lo ? hit.Trades.GetRange(lo, hi - lo) : Array.Empty<OptionTrade>();
    }

    /// <summary>Si ya se trajeron las operaciones desde <paramref name="from"/> (para avisar «trayendo…»).</summary>
    public static bool TradesReady(string underlying, DateTime from)
    {
        var floor = DateTime.UtcNow.AddDays(-MaxTradeDays);
        if (from < floor) from = floor;
        return Trades.TryGetValue(underlying, out var hit) && hit.Trades != null && hit.From <= from.AddMinutes(1);
    }

    private static int LowerBound(List<OptionTrade> list, DateTime t)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid].Time < t) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>La historia diaria (la del proveedor, completada con las fotos guardadas día a día).</summary>
    public static IReadOnlyList<OptionDaily> Daily(string underlying, int days = 400)
    {
        var p = _provider;
        if (p == null || !p.Supports(underlying)) return Array.Empty<OptionDaily>();
        Dailies.TryGetValue(underlying, out var hit);
        if (DateTime.UtcNow - hit.At > TimeSpan.FromHours(1))
            Run("daily:" + underlying, async () =>
            {
                var to = DateTime.UtcNow;
                var got = await p.DailyAsync(underlying, to.AddDays(-days), to, CancellationToken.None).ConfigureAwait(false);
                Dailies[underlying] = (DateTime.UtcNow, Merge(got, LoadRecorded(underlying)));
            }, _ => Dailies[underlying] = (DateTime.UtcNow.AddMinutes(-45), hit.Days ?? Merge(Array.Empty<OptionDaily>(), LoadRecorded(underlying))));
        return hit.Days ?? (IReadOnlyList<OptionDaily>)Array.Empty<OptionDaily>();
    }

    /// <summary>Junta la historia del proveedor con las fotos propias: por día, cada dato que falte en uno sale del otro.</summary>
    public static List<OptionDaily> Merge(IEnumerable<OptionDaily> provider, IEnumerable<OptionDaily> recorded)
    {
        static double P(double a, double b) => double.IsFinite(a) && a != 0 ? a : b;
        var byDay = new Dictionary<DateTime, OptionDaily>();
        foreach (var d in recorded) byDay[d.Date.Date] = d;
        foreach (var d in provider)
        {
            var k = d.Date.Date;
            byDay[k] = byDay.TryGetValue(k, out var r)
                ? new OptionDaily(k, P(d.Spot, r.Spot), P(d.AtmIv, r.AtmIv), P(d.Rr25, r.Rr25), P(d.PutCallVolume, r.PutCallVolume), P(d.PutCallOi, r.PutCallOi),
                    P(d.CallOi, r.CallOi), P(d.PutOi, r.PutOi), P(d.CallVolume, r.CallVolume), P(d.PutVolume, r.PutVolume), P(d.Gex, r.Gex), P(d.CallWall, r.CallWall),
                    P(d.PutWall, r.PutWall), P(d.GammaFlip, r.GammaFlip), P(d.ExpectedMove, r.ExpectedMove))
                : d with { Date = k };
        }

        return byDay.Values.OrderBy(d => d.Date).ToList();
    }

    private sealed class SeededOptions : IOptionsProvider
    {
        public string Name => "Prueba";

        public bool Supports(string underlying) => true;

        public Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct) => Task.FromResult<OptionsChain?>(null);

        public Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) => Task.FromResult<IReadOnlyList<OptionTrade>>(Array.Empty<OptionTrade>());

        public Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) => Task.FromResult<IReadOnlyList<OptionDaily>>(Array.Empty<OptionDaily>());
    }

    // ------------------------------------------------------------------ fotos por ejercicio (cada 30 min, una semana)

    private static readonly ConcurrentDictionary<string, List<StrikeSnapshot>> Snaps = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastSnap = new();

    /// <summary>
    /// Las fotos por ejercicio guardadas (interés abierto, gamma y volumen de cada ejercicio): se toman mientras haya un
    /// gráfico con un indicador de opciones abierto y quedan una semana. Con ellas se ve el cambio del interés abierto y
    /// el mapa de gamma vela a vela sin inventar el pasado.
    /// </summary>
    public static IReadOnlyList<StrikeSnapshot> Snapshots(string underlying)
    {
        if (!Snaps.TryGetValue(underlying, out var list))
        {
            list = LoadSnaps(underlying);
            Snaps[underlying] = list;
        }

        lock (list) return list.ToList();
    }

    public static StrikeSnapshot SnapshotOf(OptionsChain c)
    {
        var gex = OptionsMath.GexByStrike(c);
        var rows = c.Contracts.GroupBy(o => o.Strike).OrderBy(g => g.Key).Select(g => new StrikeRow(g.Key,
            g.Where(o => o.Right == OptionRight.Call).Sum(o => o.OpenInterest), g.Where(o => o.Right == OptionRight.Put).Sum(o => o.OpenInterest),
            gex.TryGetValue(g.Key, out var x) ? x : 0,
            g.Where(o => o.Right == OptionRight.Call).Sum(o => o.Volume), g.Where(o => o.Right == OptionRight.Put).Sum(o => o.Volume))).ToList();
        return new StrikeSnapshot(c.Time, c.Spot, rows);
    }

    private static void RecordSnapshot(string underlying, OptionsChain c)
    {
        if (LastSnap.TryGetValue(underlying, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(29)) return;
        LastSnap[underlying] = DateTime.UtcNow;
        var snap = SnapshotOf(c);
        if (!Snaps.TryGetValue(underlying, out var list))
        {
            list = LoadSnaps(underlying);
            Snaps[underlying] = list;
        }

        List<StrikeSnapshot> copy;
        lock (list)
        {
            list.Add(snap);
            list.RemoveAll(x => x.Time < DateTime.UtcNow.AddDays(-7));
            copy = list.ToList();
        }

        if (Root == null) return;
        try
        {
            Directory.CreateDirectory(Root);
            System.IO.File.WriteAllText(Path.Combine(Root, underlying + ".strikes.json"), JsonSerializer.Serialize(copy));
        }
        catch (Exception)
        {
            // opcional
        }
    }

    private static List<StrikeSnapshot> LoadSnaps(string underlying)
    {
        try
        {
            var path = Root == null ? null : Path.Combine(Root, underlying + ".strikes.json");
            return path != null && System.IO.File.Exists(path) ? JsonSerializer.Deserialize<List<StrikeSnapshot>>(System.IO.File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception)
        {
            return new List<StrikeSnapshot>();
        }
    }

    // ------------------------------------------------------------------ fotos diarias guardadas

    private static string? File(string underlying) => Root == null ? null : Path.Combine(Root, underlying + ".daily.json");

    private static readonly ConcurrentDictionary<string, DateTime> LastRecord = new();

    private static void Record(string underlying, OptionsChain c)
    {
        if (File(underlying) is not { } path) return;
        if (LastRecord.TryGetValue(underlying, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(30)) return;
        LastRecord[underlying] = DateTime.UtcNow;
        try
        {
            var day = OptionsMath.DailyOf(c);
            var list = LoadRecorded(underlying).Where(d => d.Date.Date != day.Date.Date).Append(day).OrderBy(d => d.Date).TakeLast(800).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(list));
        }
        catch (Exception)
        {
            // guardar es opcional: sin disco seguimos en memoria
        }
    }

    private static List<OptionDaily> LoadRecorded(string underlying)
    {
        try
        {
            return File(underlying) is { } path && System.IO.File.Exists(path)
                ? JsonSerializer.Deserialize<List<OptionDaily>>(System.IO.File.ReadAllText(path)) ?? new List<OptionDaily>()
                : new List<OptionDaily>();
        }
        catch (Exception)
        {
            return new List<OptionDaily>();
        }
    }

    /// <summary>Para las pruebas: pone datos directamente (sin proveedor).</summary>
    public static void Seed(string underlying, OptionsChain? chain, IEnumerable<OptionTrade>? trades, IEnumerable<OptionDaily>? daily, IEnumerable<StrikeSnapshot>? snapshots = null)
    {
        _provider ??= new SeededOptions();
        if (snapshots != null) Snaps[underlying] = snapshots.OrderBy(x => x.Time).ToList();
        if (chain != null) Chains[underlying] = (DateTime.UtcNow.AddYears(1), chain, null);
        if (trades != null)
        {
            var l = trades.OrderBy(t => t.Time).ToList();
            Trades[underlying] = (DateTime.MinValue, DateTime.MaxValue, DateTime.UtcNow.AddYears(1), l);
        }

        if (daily != null) Dailies[underlying] = (DateTime.UtcNow.AddYears(1), daily.OrderBy(d => d.Date).ToList());
        Bump();
    }
}

/// <summary>Cuentas de opciones: Black–Scholes, exposición a gamma (GEX), muros, cambio de gamma, sesgo y movimiento esperado.</summary>
public static class OptionsMath
{
    private static double Pdf(double x) => Math.Exp(-x * x / 2) / Math.Sqrt(2 * Math.PI);

    public static double Cdf(double x)
    {
        // Abramowitz–Stegun 7.1.26
        double t = 1 / (1 + 0.2316419 * Math.Abs(x));
        double d = 0.3989423 * Math.Exp(-x * x / 2);
        double p = d * t * (0.3193815 + t * (-0.3565638 + t * (1.781478 + t * (-1.821256 + t * 1.330274))));
        return x > 0 ? 1 - p : p;
    }

    /// <summary>Delta y gamma de Black–Scholes (sin tasa): <paramref name="t"/> en años, <paramref name="iv"/> en fracción.</summary>
    public static (double Delta, double Gamma) Greeks(double spot, double strike, double t, double iv, OptionRight right)
    {
        if (spot <= 0 || strike <= 0 || t <= 0 || iv <= 0) return (right == OptionRight.Call ? (spot > strike ? 1 : 0) : (spot < strike ? -1 : 0), 0);
        double sq = iv * Math.Sqrt(t), d1 = (Math.Log(spot / strike) + iv * iv / 2 * t) / sq;
        double delta = right == OptionRight.Call ? Cdf(d1) : Cdf(d1) - 1;
        return (delta, Pdf(d1) / (spot * sq));
    }

    /// <summary>Vega de Black–Scholes por un punto de volatilidad (1 %), en dólares por unidad del subyacente.</summary>
    public static double Vega(double spot, double strike, double t, double iv)
    {
        if (spot <= 0 || strike <= 0 || t <= 0 || iv <= 0) return 0;
        double sq = iv * Math.Sqrt(t), d1 = (Math.Log(spot / strike) + iv * iv / 2 * t) / sq;
        return spot * Pdf(d1) * Math.Sqrt(t) / 100;
    }

    /// <summary>Probabilidad (neutral al riesgo) de que el precio termine sobre <paramref name="level"/> al vencimiento.</summary>
    public static double ProbAbove(double spot, double level, double t, double iv)
    {
        if (spot <= 0 || level <= 0 || t <= 0 || iv <= 0) return spot > level ? 1 : 0;
        double d2 = (Math.Log(spot / level) - iv * iv / 2 * t) / (iv * Math.Sqrt(t));
        return Cdf(d2);
    }

    /// <summary>La IV de la sonrisa en un ejercicio (interpolada entre los ejercicios de ese vencimiento; el OTM manda).</summary>
    public static double SmileIv(OptionsChain c, DateTime expiry, double strike)
    {
        var pts = c.Contracts.Where(o => o.Expiry == expiry && o.Iv > 0 && (o.Right == OptionRight.Call ? o.Strike >= c.Spot : o.Strike <= c.Spot))
            .GroupBy(o => o.Strike).Select(g => (K: g.Key, Iv: g.Average(o => o.Iv))).OrderBy(x => x.K).ToList();
        if (pts.Count == 0) return AtmIv(c, expiry);
        if (strike <= pts[0].K) return pts[0].Iv;
        if (strike >= pts[^1].K) return pts[^1].Iv;
        for (int i = 1; i < pts.Count; i++)
            if (strike <= pts[i].K)
            {
                double w = (strike - pts[i - 1].K) / Math.Max(1e-9, pts[i].K - pts[i - 1].K);
                return pts[i - 1].Iv + w * (pts[i].Iv - pts[i - 1].Iv);
            }

        return pts[^1].Iv;
    }

    /// <summary>El precio de «máximo dolor» de un vencimiento: donde los compradores de opciones pierden más al vencer.</summary>
    public static double MaxPain(OptionsChain c, DateTime? expiry)
    {
        var set = c.Contracts.Where(o => expiry == null || o.Expiry == expiry).ToList();
        var strikes = set.Select(o => o.Strike).Distinct().OrderBy(k => k).ToList();
        double best = double.NaN, bestPay = double.MaxValue;
        foreach (var k in strikes)
        {
            double pay = 0;
            foreach (var o in set) pay += o.OpenInterest * o.ContractSize * (o.Right == OptionRight.Call ? Math.Max(0, k - o.Strike) : Math.Max(0, o.Strike - k));
            if (pay < bestPay)
            {
                bestPay = pay;
                best = k;
            }
        }

        return best;
    }

    /// <summary>La gamma de un contrato (la del proveedor, o calculada con su IV).</summary>
    public static double GammaOf(OptionContract c, double spot, DateTime now) =>
        c.Gamma > 0 ? c.Gamma : Greeks(spot, c.Strike, c.DaysToExpiry(now) / 365, c.Iv, c.Right).Gamma;

    /// <summary>
    /// Exposición a gamma de los creadores de mercado por precio de ejercicio, en dólares por 1 % de movimiento: los call
    /// suman y los put restan (la convención de siempre: el público compra puts y vende calls).
    /// </summary>
    public static SortedDictionary<double, double> GexByStrike(OptionsChain c, double? atSpot = null, int maxDays = 400)
    {
        double s = atSpot ?? c.Spot;
        var res = new SortedDictionary<double, double>();
        foreach (var o in c.Contracts)
        {
            if (o.DaysToExpiry(c.Time) > maxDays) continue;
            double g = atSpot == null ? GammaOf(o, s, c.Time) : Greeks(s, o.Strike, o.DaysToExpiry(c.Time) / 365, o.Iv, o.Right).Gamma;
            double v = g * o.OpenInterest * o.ContractSize * s * s * 0.01 * (o.Right == OptionRight.Call ? 1 : -1);
            res[o.Strike] = (res.TryGetValue(o.Strike, out var x) ? x : 0) + v;
        }

        return res;
    }

    public static double TotalGex(OptionsChain c, double? atSpot = null) => GexByStrike(c, atSpot).Values.Sum();

    /// <summary>El precio donde la gamma total cambia de signo (buscando cerca del precio actual), o NaN.</summary>
    public static double GammaFlip(OptionsChain c)
    {
        double prev = double.NaN, prevS = double.NaN;
        for (double k = 0.8; k <= 1.2001; k += 0.005)
        {
            double s = c.Spot * k, g = TotalGex(c, s);
            if (!double.IsNaN(prev) && Math.Sign(g) != Math.Sign(prev))
                return prevS + (s - prevS) * Math.Abs(prev) / Math.Max(1e-12, Math.Abs(prev) + Math.Abs(g));
            prev = g;
            prevS = s;
        }

        return double.NaN;
    }

    /// <summary>Muro de calls (el ejercicio en o sobre el precio con más gamma positiva) y de puts (el de más gamma negativa en o bajo el precio).</summary>
    public static (double Call, double Put) Walls(OptionsChain c)
    {
        var g = GexByStrike(c);
        double call = g.Where(kv => kv.Key >= c.Spot).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).DefaultIfEmpty(double.NaN).First();
        double put = g.Where(kv => kv.Key <= c.Spot).OrderBy(kv => kv.Value).Select(kv => kv.Key).DefaultIfEmpty(double.NaN).First();
        return (call, put);
    }

    /// <summary>El vencimiento más cercano a <paramref name="days"/> días (con al menos un día).</summary>
    public static DateTime ExpiryNear(OptionsChain c, double days) =>
        c.Contracts.Select(o => o.Expiry).Distinct().Where(e => (e - c.Time).TotalDays >= 0.5).OrderBy(e => Math.Abs((e - c.Time).TotalDays - days)).DefaultIfEmpty(c.Time).First();

    /// <summary>La IV en el dinero de un vencimiento (promedio del call y el put más cercanos al precio).</summary>
    public static double AtmIv(OptionsChain c, DateTime expiry)
    {
        var at = c.Contracts.Where(o => o.Expiry == expiry && o.Iv > 0).OrderBy(o => Math.Abs(o.Strike - c.Spot)).Take(2).ToList();
        return at.Count == 0 ? double.NaN : at.Average(o => o.Iv);
    }

    /// <summary>Sesgo 25 delta (risk reversal): IV del call de 25 delta menos la del put de −25 delta, en puntos de volatilidad.</summary>
    public static double RiskReversal25(OptionsChain c, DateTime expiry)
    {
        double t = Math.Max(1.0 / 365, (expiry - c.Time).TotalDays / 365);
        OptionContract? Near(OptionRight r, double target) => c.Contracts.Where(o => o.Expiry == expiry && o.Right == r && o.Iv > 0)
            .OrderBy(o => Math.Abs((o.Delta != 0 ? o.Delta : Greeks(c.Spot, o.Strike, t, o.Iv, r).Delta) - target)).FirstOrDefault();
        var call = Near(OptionRight.Call, 0.25);
        var put = Near(OptionRight.Put, -0.25);
        return call == null || put == null ? double.NaN : (call.Iv - put.Iv) * 100;
    }

    /// <summary>El movimiento esperado (±1σ) hasta un vencimiento, en precio.</summary>
    public static double ExpectedMove(OptionsChain c, DateTime expiry)
    {
        double iv = AtmIv(c, expiry);
        return double.IsNaN(iv) ? double.NaN : c.Spot * iv * Math.Sqrt(Math.Max(1.0 / 365, (expiry - c.Time).TotalDays / 365));
    }

    /// <summary>El resumen de un día armado con una foto de la cadena.</summary>
    public static OptionDaily DailyOf(OptionsChain c)
    {
        var e30 = ExpiryNear(c, 30);
        var e1 = ExpiryNear(c, 1);
        double callOi = c.Contracts.Where(o => o.Right == OptionRight.Call).Sum(o => o.OpenInterest), putOi = c.Contracts.Where(o => o.Right == OptionRight.Put).Sum(o => o.OpenInterest);
        double callVol = c.Contracts.Where(o => o.Right == OptionRight.Call).Sum(o => o.Volume), putVol = c.Contracts.Where(o => o.Right == OptionRight.Put).Sum(o => o.Volume);
        var (cw, pw) = Walls(c);
        return new OptionDaily(c.Time.Date, c.Spot, AtmIv(c, e30), RiskReversal25(c, e30), callVol > 0 ? putVol / callVol : double.NaN, callOi > 0 ? putOi / callOi : double.NaN,
            callOi, putOi, callVol, putVol, TotalGex(c), cw, pw, GammaFlip(c), ExpectedMove(c, e1));
    }
}

/// <summary>Deribit: opciones de BTC y ETH (públicas, sin cuenta). La cadena con IV, griegas e interés abierto; las operaciones con su lado.</summary>
public sealed class DeribitOptions : IOptionsProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private const string Api = "https://www.deribit.com/api/v2/public/";

    public string Name => "Deribit";

    public bool Supports(string underlying) => underlying is "BTC" or "ETH";

    private static async Task<JsonElement> Get(string method, string query, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(Api + method + "?" + query, ct).ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("error", out var err)) throw new InvalidOperationException(err.TryGetProperty("message", out var m) ? m.GetString() : "Deribit");
        return doc.RootElement.GetProperty("result").Clone();
    }

    private static double D(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    /// <summary>BTC-27DEC26-100000-C → (vencimiento 08:00 UTC, ejercicio, tipo).</summary>
    public static (DateTime Expiry, double Strike, OptionRight Right)? ParseName(string name)
    {
        var p = name.Split('-');
        if (p.Length != 4) return null;
        if (!DateTime.TryParseExact(p[1], new[] { "dMMMyy", "ddMMMyy" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exp)) return null;
        if (!double.TryParse(p[2].Replace('d', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var k)) return null;
        return (DateTime.SpecifyKind(exp.Date.AddHours(8), DateTimeKind.Utc), k, p[3] == "C" ? OptionRight.Call : OptionRight.Put);
    }

    public async Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct)
    {
        var r = await Get("get_book_summary_by_currency", $"currency={underlying}&kind=option", ct).ConfigureAwait(false);
        var list = new List<OptionContract>();
        double spot = 0;
        var now = DateTime.UtcNow;
        foreach (var o in r.EnumerateArray())
        {
            string name = o.GetProperty("instrument_name").GetString() ?? "";
            if (ParseName(name) is not { } info) continue;
            double under = D(o, "underlying_price");
            if (under > 0) spot = under;
            // los precios de Deribit vienen en la moneda: se pasan a dólares
            double bid = D(o, "bid_price") * under, ask = D(o, "ask_price") * under, mark = D(o, "mark_price") * under;
            double iv = D(o, "mark_iv") / 100;
            var (delta, gamma) = OptionsMath.Greeks(under, info.Strike, Math.Max(1.0 / 365, (info.Expiry - now).TotalDays / 365), iv, info.Right);
            list.Add(new OptionContract(name, info.Expiry, info.Strike, info.Right, bid, ask, mark, iv, delta, gamma, 0, 0, D(o, "open_interest"), D(o, "volume"), 1));
        }

        return list.Count == 0 ? null : new OptionsChain(underlying, now, spot, list);
    }

    public async Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
    {
        var res = new List<OptionTrade>();
        long start = new DateTimeOffset(from).ToUnixTimeMilliseconds(), end = new DateTimeOffset(to).ToUnixTimeMilliseconds();
        // la historia por tramos de 1000 operaciones (desde la más vieja), con un tope para no colgar
        for (int page = 0; page < 250 && start < end; page++)
        {
            var r = await Get("get_last_trades_by_currency_and_time", $"currency={underlying}&kind=option&start_timestamp={start}&end_timestamp={end}&count=1000&sorting=asc", ct).ConfigureAwait(false);
            var trades = r.GetProperty("trades");
            long last = start;
            foreach (var t in trades.EnumerateArray())
            {
                string name = t.GetProperty("instrument_name").GetString() ?? "";
                if (ParseName(name) is not { } info) continue;
                long ts = t.GetProperty("timestamp").GetInt64();
                last = Math.Max(last, ts);
                double index = D(t, "index_price");
                double price = D(t, "price") * index, size = D(t, "amount");
                string dir = t.TryGetProperty("direction", out var dd) ? dd.GetString() ?? "" : "";
                res.Add(new OptionTrade(DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime, name, info.Expiry, info.Strike, info.Right, price, size, dir == "buy" ? 1 : dir == "sell" ? -1 : 0, D(t, "iv") / 100, index, 1, "Deribit"));
            }

            bool more = r.TryGetProperty("has_more", out var hm) && hm.GetBoolean();
            if (!more || last <= start) break;
            start = last + 1;
        }

        return res;
    }

    public async Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
    {
        // la historia del índice de volatilidad (DVOL, IV a 30 días en el dinero) por día
        long s = new DateTimeOffset(from).ToUnixTimeMilliseconds(), e = new DateTimeOffset(to).ToUnixTimeMilliseconds();
        var r = await Get("get_volatility_index_data", $"currency={underlying}&start_timestamp={s}&end_timestamp={e}&resolution=1D", ct).ConfigureAwait(false);
        var res = new List<OptionDaily>();
        foreach (var row in r.GetProperty("data").EnumerateArray())
        {
            var day = DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64()).UtcDateTime.Date;
            double close = row[4].GetDouble() / 100;
            res.Add(new OptionDaily(day, double.NaN, close, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN));
        }

        return res;
    }
}

/// <summary>
/// Una API propia de opciones por URL (la que contrates). Pide JSON a tres direcciones bajo la URL base, con la clave en el
/// encabezado que digas:
/// <c>GET {base}/chain?underlying=BTC</c> → <c>{"time":"…","spot":…,"contracts":[{"symbol","expiry","strike","right":"C|P","bid","ask","mark","iv","delta","gamma","vega","theta","oi","volume","contractSize"}]}</c>;
/// <c>GET {base}/trades?underlying=BTC&amp;from=ISO&amp;to=ISO</c> → <c>[{"time","symbol","expiry","strike","right","price","size","side":"buy|sell|","iv","spot","contractSize","venue"}]</c>;
/// <c>GET {base}/daily?underlying=BTC&amp;from=ISO&amp;to=ISO</c> → <c>[{"date","spot","atmIv","rr25","pcVolume","pcOi","callOi","putOi","callVolume","putVolume","gex","callWall","putWall","gammaFlip","expectedMove"}]</c>.
/// IV en fracción (0.55), precios y primas en dólares por contrato. Un campo que falta vale 0 (o se calcula).
/// </summary>
public sealed class JsonOptionsApi : IOptionsProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _base;
    private readonly string? _header, _key;
    private readonly HashSet<string>? _only;

    public JsonOptionsApi(string baseUrl, string? header, string? key, IEnumerable<string>? underlyings = null)
    {
        _base = baseUrl.TrimEnd('/');
        _header = string.IsNullOrWhiteSpace(header) ? null : header.Trim();
        _key = string.IsNullOrWhiteSpace(key) ? null : key;
        var only = underlyings?.Select(u => u.Trim().ToUpperInvariant()).Where(u => u.Length > 0).ToHashSet();
        _only = only is { Count: > 0 } ? only : null;
    }

    public string Name => "API propia";

    public bool Supports(string underlying) => _only == null || _only.Contains(underlying);

    private async Task<JsonElement> Get(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _base + path);
        if (_key != null)
        {
            if (_header == null || _header.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            else req.Headers.TryAddWithoutValidation(_header, _key);
        }

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.Clone();
    }

    private static double D(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0 : 0;

    private static DateTime T(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && DateTime.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : DateTime.UtcNow;

    private static OptionRight R(JsonElement o) => o.TryGetProperty("right", out var v) && (v.GetString() ?? "").StartsWith("P", StringComparison.OrdinalIgnoreCase) ? OptionRight.Put : OptionRight.Call;

    private static string Iso(DateTime t) => Uri.EscapeDataString(t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));

    public async Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct)
    {
        var r = await Get($"/chain?underlying={Uri.EscapeDataString(underlying)}", ct).ConfigureAwait(false);
        var list = new List<OptionContract>();
        foreach (var o in r.GetProperty("contracts").EnumerateArray())
        {
            double size = D(o, "contractSize");
            list.Add(new OptionContract(o.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "", T(o, "expiry"), D(o, "strike"), R(o), D(o, "bid"), D(o, "ask"), D(o, "mark"), D(o, "iv"),
                D(o, "delta"), D(o, "gamma"), D(o, "vega"), D(o, "theta"), D(o, "oi"), D(o, "volume"), size > 0 ? size : 1));
        }

        return new OptionsChain(underlying, T(r, "time"), D(r, "spot"), list);
    }

    public async Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
    {
        var r = await Get($"/trades?underlying={Uri.EscapeDataString(underlying)}&from={Iso(from)}&to={Iso(to)}", ct).ConfigureAwait(false);
        var res = new List<OptionTrade>();
        foreach (var o in r.EnumerateArray())
        {
            string side = o.TryGetProperty("side", out var sd) ? sd.GetString() ?? "" : "";
            double size = D(o, "contractSize");
            res.Add(new OptionTrade(T(o, "time"), o.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "", T(o, "expiry"), D(o, "strike"), R(o), D(o, "price"), D(o, "size"),
                side == "buy" ? 1 : side == "sell" ? -1 : 0, D(o, "iv"), D(o, "spot"), size > 0 ? size : 1, o.TryGetProperty("venue", out var vn) ? vn.GetString() ?? "" : ""));
        }

        return res;
    }

    public async Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
    {
        var r = await Get($"/daily?underlying={Uri.EscapeDataString(underlying)}&from={Iso(from)}&to={Iso(to)}", ct).ConfigureAwait(false);
        var res = new List<OptionDaily>();
        foreach (var o in r.EnumerateArray())
            res.Add(new OptionDaily(T(o, "date").Date, D(o, "spot"), D(o, "atmIv"), D(o, "rr25"), D(o, "pcVolume"), D(o, "pcOi"), D(o, "callOi"), D(o, "putOi"), D(o, "callVolume"), D(o, "putVolume"),
                D(o, "gex"), D(o, "callWall"), D(o, "putWall"), D(o, "gammaFlip"), D(o, "expectedMove")));
        return res;
    }
}
