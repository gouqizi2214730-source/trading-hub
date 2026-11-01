using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingHub.App.Services;

/// <summary>
/// Busca versiones nuevas en GitHub, sin cuentas ni claves (la API pública de releases):
/// <list type="bullet">
/// <item>en <see cref="Repo"/> (esta copia), el instalador nuevo para bajar e instalar;</item>
/// <item>en <see cref="Upstream"/> (el proyecto original), si salió una versión que esta copia todavía no empaquetó.</item>
/// </list>
/// Las versiones de esta copia son las del original con un número más al final (1.2.0 → 1.2.0.1, 1.2.0.2…), así que
/// una versión nueva del original (1.3.0) siempre es más nueva que cualquier empaquetado de la anterior.
/// Nunca instala solo: baja el instalador, revisa su SHA-256 y lo abre; el instalador pide cerrar Trading Hub (sin
/// cerrarla a la fuerza, por si hay operaciones abiertas).
/// </summary>
public static class UpdateChecker
{
    /// <summary>El repositorio de esta copia (sus releases traen los instaladores).</summary>
    public const string Repo = "gouqizi2214730-source/trading-hub";

    /// <summary>El proyecto original.</summary>
    public const string Upstream = "ralphtradesz/trading-hub";

    public static string ReleasesUrl => $"https://github.com/{Repo}/releases";

    /// <summary>Cada cuánto se busca sola al abrir la app.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    /// <summary>La versión de esta app (1.2.0.1 en los empaquetados de esta copia; 1.2.0 si se compiló a mano).</summary>
    public static Version Current { get; } = Normalize(typeof(UpdateChecker).Assembly.GetName().Version) ?? new Version(0, 0, 0);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TradingHub", Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Una versión publicada.</summary>
    public sealed record Release(Version Version, string Tag, string PageUrl, string? InstallerUrl, string? InstallerName, long InstallerSize, string? Sha256, string? Notes);

    /// <summary>Lo que se encontró: lo nuevo de esta copia (para instalar) y lo nuevo del original (para avisar).</summary>
    public sealed record Result(Release? Update, Release? UpstreamNewer, Release? Latest, string? Error)
    {
        public bool HasUpdate => Update != null;
    }

    /// <summary>
    /// «v1.2.0», «1.2.0.3» o «v1.2.0-fork.3» → una versión comparable (null si no lo es). «-fork.N» cuenta como el
    /// cuarto número.
    /// </summary>
    public static Version? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        int fork = -1;
        int dash = t.IndexOf('-');
        if (dash >= 0)
        {
            var suffix = t[(dash + 1)..];
            t = t[..dash];
            var digits = new string(suffix.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
            if (suffix.StartsWith("fork", StringComparison.OrdinalIgnoreCase) && int.TryParse(digits, out var n)) fork = n;
            else return null; // otras prereleases (beta, rc…) no se ofrecen
        }

        if (!Version.TryParse(t, out var v)) return null;
        if (fork >= 0 && v.Revision < 0) v = new Version(v.Major, v.Minor, Math.Max(v.Build, 0), fork);
        return Normalize(v);
    }

    /// <summary>1.2 → 1.2.0 (y 1.2.0.0 queda 1.2.0.0): así se comparan bien entre sí.</summary>
    private static Version? Normalize(Version? v) => v == null ? null
        : v.Build < 0 ? new Version(v.Major, v.Minor, 0)
        : v;

    /// <summary>La versión base (sin el número de esta copia): 1.2.0.3 → 1.2.0.</summary>
    public static Version Base(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Si <paramref name="candidate"/> es más nueva que <paramref name="current"/>.</summary>
    public static bool IsNewer(Version candidate, Version current) => candidate.CompareTo(current) > 0;

    /// <summary>El nombre del instalador para esta computadora.</summary>
    public static string InstallerAssetName => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
        ? "TradingHub-Setup-arm64.exe"
        : "TradingHub-Setup-x64.exe";

    /// <summary>Busca en las dos fuentes. No tira excepciones: los problemas de red quedan en <see cref="Result.Error"/>.</summary>
    public static async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        Release? mine = null, upstream = null;
        string? error = null;
        try
        {
            mine = await LatestAsync(Repo, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error = Describe(ex);
        }

        try
        {
            upstream = await LatestAsync(Upstream, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error ??= Describe(ex);
        }

        var update = mine != null && IsNewer(mine.Version, Current) ? mine : null;
        // el original tiene algo que esta copia todavía no empaquetó (ni está instalado)
        var newest = mine?.Version ?? Current;
        if (IsNewer(Current, newest)) newest = Current;
        var upstreamNewer = upstream != null && IsNewer(upstream.Version, Base(newest)) ? upstream : null;
        return new Result(update, upstreamNewer, mine, update == null && upstreamNewer == null ? error : null);
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "GitHub limitó las consultas por un rato; probá más tarde.",
        HttpRequestException or TaskCanceledException => "No se pudo conectar con GitHub.",
        _ => ex.Message,
    };

    /// <summary>La última versión estable de un repositorio (la más nueva entre las publicadas, no solo la marcada).</summary>
    private static async Task<Release?> LatestAsync(string repo, CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases?per_page=20", ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var list = await JsonSerializer.DeserializeAsync<List<GhRelease>>(stream, cancellationToken: ct).ConfigureAwait(false) ?? new();
        return Pick(list);
    }

    /// <summary>De las releases (como las da GitHub), la más nueva publicada y estable.</summary>
    internal static Release? Pick(IEnumerable<GhRelease> releases)
    {
        Release? best = null;
        foreach (var r in releases)
        {
            if (r.Draft || r.Prerelease) continue;
            var v = Parse(r.TagName);
            if (v == null || (best != null && !IsNewer(v, best.Version))) continue;
            var asset = r.Assets?.FirstOrDefault(a => string.Equals(a.Name, InstallerAssetName, StringComparison.OrdinalIgnoreCase));
            string? sha = asset?.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..].ToLowerInvariant() : null;
            best = new Release(v, r.TagName ?? "", r.HtmlUrl ?? "", asset?.BrowserDownloadUrl, asset?.Name, asset?.Size ?? 0, sha, r.Body);
        }

        return best;
    }

    /// <summary>
    /// Baja el instalador a la carpeta temporal y revisa tamaño y SHA-256 (cuando GitHub lo publica). Devuelve la ruta.
    /// </summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (release.InstallerUrl == null || release.InstallerName == null) throw new InvalidOperationException("Esta versión no trae instalador para esta computadora.");
        var dir = Path.Combine(Path.GetTempPath(), "TradingHub-Update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(release.InstallerName)}-{release.Version}.exe");
        var part = path + ".part";

        using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        {
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TradingHub", Current.ToString()));
            using var resp = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? release.InstallerSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
        }

        var size = new FileInfo(part).Length;
        if (release.InstallerSize > 0 && size != release.InstallerSize)
        {
            File.Delete(part);
            throw new InvalidDataException("La descarga quedó incompleta; probá de nuevo.");
        }

        if (release.Sha256 != null)
        {
            string actual;
            await using (var f = File.OpenRead(part)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false));
            if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidDataException("El instalador bajado no coincide con el publicado (SHA-256); no se abre.");
            }
        }

        File.Move(part, path, overwrite: true);
        return path;
    }

    /// <summary>Abre el instalador (Windows). El instalador pide cerrar Trading Hub si sigue abierta.</summary>
    public static void RunInstaller(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
        System.Diagnostics.Process.Start(psi);
    }

    // ===================================================================== lo que devuelve la API de GitHub

    internal sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GhAsset>? Assets { get; set; }
    }

    internal sealed class GhAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
