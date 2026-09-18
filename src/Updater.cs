using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fletta;

/// <summary>Veröffentlichung auf GitHub: Version, Setup und dessen SHA-256 laut GitHub; PreRelease bei Tag vX.Y.Z-pre.</summary>
public sealed record Release(Version Version, string SetupName, string SetupUrl, string Sha256, bool PreRelease = false);

/// <summary>
/// Update über die GitHub-Releases. Gefragt wird höchstens einmal am Tag, mit Vorabversionen einmal in der Stunde (wer
/// sie will, testet gerade); die Antwort liegt so lange unter %APPDATA%\Fletta, damit jedes neue Fenster den Hinweis ohne
/// weitere Anfrage zeigt — jede PDF ist ein eigener Prozess, und ohne Anmeldung erlaubt GitHub 60 Anfragen je Stunde.
/// Gefragt wird nach der Liste der Releases, nicht nach „latest“: das liefert keine Vorabversionen.
/// </summary>
static class Updater
{
    const string ReleasesUrl = "https://api.github.com/repos/martinhoess/fletta/releases?per_page=20";
    static readonly HttpClient http = CreateClient();

    static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fletta", "releases.json");

    /// <summary>Die laufende Version mit drei Stellen, sonst wäre 0.10.1 kleiner als 0.10.1.0.</summary>
    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version!;
            return new Version(v.Major, v.Minor, v.Build);
        }
    }

    /// <summary>
    /// Eine neuere Veröffentlichung oder null — auch offline und bei jedem Fehler, ein Update ist nie dringend. force fragt
    /// GitHub, auch wenn die gemerkte Antwort noch frisch ist („Jetzt prüfen“); dann kommen Fehler als Ausnahme heraus.
    /// </summary>
    public static async Task<Release?> FindNewerAsync(bool preReleases, bool force = false)
    {
        try
        {
            string json;
            var interval = preReleases ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
            if (!force && File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < interval)
                json = await File.ReadAllTextAsync(CachePath);
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                json = await http.GetStringAsync(ReleasesUrl, timeout.Token);
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                await File.WriteAllTextAsync(CachePath, json);
            }
            return Newest(json, preReleases) is { } release && release.Version > Current ? release : null;
        }
        catch (Exception e) when (!force && e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Höchste Version aus der Liste der Releases (Antwort von /releases); Vorabversionen nur mit preReleases, Entwürfe nie.
    /// null, wenn keine taugt. Im Selbsttest geprüft.
    /// </summary>
    internal static Release? Newest(string json, bool preReleases)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            return doc.RootElement.EnumerateArray()
                .Where(entry => !Flag(entry, "draft") && (preReleases || !Flag(entry, "prerelease")))
                .Select(Parse)
                .OfType<Release>()
                // Gleiche Nummer bei Freigabe und Vorabversion (CI): die Freigabe gewinnt, egal in welcher Reihenfolge
                // GitHub die Liste liefert (Review 2026-09-18).
                .MaxBy(release => (release.Version, !release.PreRelease));
        }
        catch (JsonException) { return null; }
    }

    static bool Flag(JsonElement entry, string name) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Ein Release (Antwort von /releases/latest oder ein Eintrag der Liste); null, wenn etwas fehlt — auch die Prüfsumme.</summary>
    internal static Release? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Parse(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Tag „vX.Y.Z“ oder „vX.Y.Z-pre“ und das Setup unter den Anhängen. Eine Vorabversion trägt dieselbe Nummer wie ihre
    /// Freigabe (CI: Tag vX.Y.Z-pre zu &lt;Version&gt;X.Y.Z) — wer sie installiert hat, bekommt die Freigabe also nicht noch
    /// einmal angeboten; es ist dasselbe Setup.
    /// </summary>
    static Release? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || Text(root, "tag_name") is not { } tag) return null;
        var number = tag.TrimStart('v');
        var preRelease = number.EndsWith("-pre", StringComparison.Ordinal);
        if (preRelease) number = number[..^"-pre".Length];
        if (!Version.TryParse(number, out var version) || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var (name, url, digest) = (Text(asset, "name"), Text(asset, "browser_download_url"), Text(asset, "digest"));
            if (name?.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) == true && url is not null
                && digest?.StartsWith("sha256:", StringComparison.Ordinal) == true)
                return new Release(version, name, url, digest["sha256:".Length..], preRelease);
        }
        return null;
    }

    /// <summary>Text eines Felds; null, wenn es fehlt oder kein Text ist (GetString wirft sonst).</summary>
    static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
