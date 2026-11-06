using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CamSwitch;

/// Checks GitHub Releases for a newer version and runs its installer.
public static class Updater
{
    public const string Repo = "pikkon38/CamSwitcher";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    public sealed record Release(string Version, string Notes, string InstallerUrl, string PageUrl);

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CamSwitch", Platform.Version));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// Returns the latest release if it's newer than this copy, otherwise null.
    public static async Task<Release?> CheckAsync()
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var latest = tag.TrimStart('v', 'V');
        if (!IsNewer(latest, Platform.Version)) return null;

        string? url = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.StartsWith("CamSwitch-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                url = a.GetProperty("browser_download_url").GetString();
        }
        if (url == null) return null;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        return new Release(latest, notes, url, page);
    }

    public static bool IsNewer(string candidate, string current)
    {
        static Version Parse(string s)
        {
            var core = s.Split('-', '+')[0];
            return System.Version.TryParse(core, out var v) ? v : new Version(0, 0);
        }
        // A dev build (0.0.0) never nags.
        var cur = Parse(current);
        return cur > new Version(0, 0, 0) && Parse(candidate) > cur;
    }

    /// Downloads the installer and starts it silently. The installer closes this app,
    /// turns both cameras back on, updates, and starts the new version.
    public static async Task DownloadAndRunAsync(Release r, IProgress<int>? progress = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"CamSwitch-Setup-{r.Version}.exe");
        using (var resp = await Http.GetAsync(r.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(path);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }
        Log.Write($"Running installer {path}");
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
    }
}
