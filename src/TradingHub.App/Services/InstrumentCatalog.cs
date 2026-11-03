using System.Text.Json;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.App.Services;

/// <summary>
/// Lista de instrumentos de cada conexión guardada en disco: sin red (o con el exchange caído) igual se abren los gráficos
/// de lo que ya se conocía, con la historia que está bajada.
/// </summary>
public static class InstrumentCatalog
{
    private sealed record Row(string Symbol, string FeedId, string Exchange, decimal TickSize, decimal VolumeStep, decimal PointValue, string BaseAsset, string QuoteAsset, string Description);

    private static string Dir => Path.Combine(AppPaths.Root, "Instrumentos");

    private static string FileFor(string profileId)
    {
        var name = string.Concat(profileId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Dir, name + ".json");
    }

    /// <summary>Pide la lista al feed; si no se puede (sin red), la última que se guardó.</summary>
    public static async Task<IReadOnlyList<Instrument>> LoadAsync(IMarketDataFeed feed, string profileId, CancellationToken ct)
    {
        try
        {
            var list = await feed.GetInstrumentsAsync(ct).ConfigureAwait(false);
            if (list.Count > 0) Save(profileId, list);
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cached = Read(profileId);
            if (cached.Count == 0) throw;
            return cached;
        }
    }

    private static void Save(string profileId, IReadOnlyList<Instrument> list)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var rows = list.Select(i => new Row(i.Symbol, i.FeedId, i.Exchange, i.TickSize, i.VolumeStep, i.PointValue, i.BaseAsset, i.QuoteAsset, i.Description)).ToList();
            var tmp = FileFor(profileId) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(rows));
            File.Move(tmp, FileFor(profileId), overwrite: true);
        }
        catch
        {
            // sin caché: la próxima vez se pide de nuevo
        }
    }

    public static IReadOnlyList<Instrument> Read(string profileId)
    {
        try
        {
            var path = FileFor(profileId);
            if (!File.Exists(path)) return Array.Empty<Instrument>();
            var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(path)) ?? new List<Row>();
            return rows.Select(r => new Instrument
            {
                Symbol = r.Symbol, FeedId = r.FeedId, Exchange = r.Exchange, TickSize = r.TickSize, VolumeStep = r.VolumeStep, PointValue = r.PointValue,
                BaseAsset = r.BaseAsset, QuoteAsset = r.QuoteAsset, Description = r.Description,
            }).ToList();
        }
        catch
        {
            return Array.Empty<Instrument>();
        }
    }
}
