using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Twenti.Services;

public sealed class UpdateInfo
{
    public required string LatestVersion { get; init; }
    public required string CurrentVersion { get; init; }
    public required string ReleaseUrl { get; init; }
    public required string ReleaseNotes { get; init; }
}

public sealed class UpdateChecker
{
    private const string ReleasesApi = "https://api.github.com/repos/Sammeeeeeeee/Twenti/releases/latest";

    private static readonly HttpClient Http;

    static UpdateChecker()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Twenti-UpdateChecker/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var json = await Http.GetStringAsync(ReleasesApi).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string url = root.GetProperty("html_url").GetString() ?? "";
            string body = root.TryGetProperty("body", out var bp) ? (bp.GetString() ?? "") : "";

            string latest = tag.TrimStart('v', 'V').Trim();
            string current = CurrentVersion;

            if (IsNewer(latest, current))
            {
                return new UpdateInfo
                {
                    LatestVersion = latest,
                    CurrentVersion = current,
                    ReleaseUrl = url,
                    ReleaseNotes = body,
                };
            }
        }
        catch
        {
            // Network error / repo missing / rate-limited — silent fail.
        }
        return null;
    }

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(Pad(latest), out var l) && Version.TryParse(Pad(current), out var c))
        {
            return l > c;
        }
        return !string.IsNullOrEmpty(latest) && !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
    }

    // Version.TryParse needs at least Major.Minor — pad single-component strings.
    private static string Pad(string v)
    {
        int dots = 0;
        foreach (var ch in v) if (ch == '.') dots++;
        return dots switch
        {
            0 => v + ".0.0",
            1 => v + ".0",
            _ => v,
        };
    }
}
