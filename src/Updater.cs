using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fletta;

/// <summary>Neueste Veröffentlichung auf GitHub: Version, Setup und dessen SHA-256 laut GitHub.</summary>
public sealed record Release(Version Version, string SetupName, string SetupUrl, string Sha256);

/// <summary>
/// Update über die GitHub-Releases. Gefragt wird höchstens einmal am Tag; die Antwort liegt so lange
/// unter %APPDATA%\Fletta, damit jedes neue Fenster den Hinweis ohne weitere Anfrage zeigt — jede PDF
/// ist ein eigener Prozess, und ohne Anmeldung erlaubt GitHub 60 Anfragen je Stunde.
/// </summary>
static class Updater
{
    const string LatestUrl = "https://api.github.com/repos/martinhoess/fletta/releases/latest";
    static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    static readonly HttpClient http = CreateClient();

    static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fletta", "latest-release.json");

    /// <summary>Die laufende Version mit drei Stellen, sonst wäre 0.10.1 kleiner als 0.10.1.0.</summary>
    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version!;
            return new Version(v.Major, v.Minor, v.Build);
        }
    }

    /// <summary>Eine neuere Veröffentlichung oder null — auch offline und bei jedem Fehler, ein Update ist nie dringend.</summary>
    public static async Task<Release?> FindNewerAsync()
    {
        try
        {
            string json;
            if (File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < CheckInterval)
                json = await File.ReadAllTextAsync(CachePath);
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                json = await http.GetStringAsync(LatestUrl, timeout.Token);
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                await File.WriteAllTextAsync(CachePath, json);
            }
            return Parse(json) is { } release && release.Version > Current ? release : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Tag „vX.Y.Z“ und das Setup unter den Anhängen; null, wenn etwas fehlt — auch die Prüfsumme.</summary>
    internal static Release? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tag) || !Version.TryParse(tag.GetString()?.TrimStart('v'), out var version)
                || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                if (name?.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) == true && url is not null
                    && digest?.StartsWith("sha256:", StringComparison.Ordinal) == true)
                    return new Release(version, name, url, digest["sha256:".Length..]);
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Lädt das Setup nach %TEMP% und prüft es gegen die Prüfsumme; wirft bei Netz- oder Prüffehler.</summary>
    public static async Task<string> DownloadAsync(Release release)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetFileName(release.SetupName));
        // HttpClient.Timeout endet mit den Kopfzeilen; reißt die Verbindung danach still ab, hinge das Laden
        // sonst ewig. Die Frist beginnt mit jedem Block neu — eine feste fürs Ganze bräche langsame Leitungen ab.
        var idle = TimeSpan.FromSeconds(60);
        using var stalled = new CancellationTokenSource(idle);
        await using (var target = File.Create(path))
        await using (var source = await http.GetStreamAsync(release.SetupUrl, stalled.Token))
        {
            var buffer = new byte[81920];
            for (int read; (read = await source.ReadAsync(buffer, stalled.Token)) > 0; stalled.CancelAfter(idle))
                await target.WriteAsync(buffer.AsMemory(0, read));
        }
        string hash;
        await using (var written = File.OpenRead(path))
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(written));
        if (hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) return path;
        File.Delete(path); // ein Setup, das nicht passt, bleibt nicht zum versehentlichen Starten liegen
        throw new InvalidDataException("Prüfsumme des Setups stimmt nicht");
    }

    /// <summary>
    /// Startet das Setup sichtbar, aber ohne Rückfragen, und wartet auf sein Ende. Es schließt die laufenden
    /// Fletta-Fenster und öffnet danach wieder, was sich per App.RegisterRestart angemeldet hat.
    /// </summary>
    public static async Task<int> InstallAsync(string setupPath)
    {
        using var setup = Process.Start(new ProcessStartInfo(setupPath)
        {
            ArgumentList = { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RESTARTAPPLICATIONS" },
        })!;
        await setup.WaitForExitAsync();
        return setup.ExitCode;
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Fletta/{Current}"); // ohne User-Agent lehnt die GitHub-API ab
        return client;
    }
}
