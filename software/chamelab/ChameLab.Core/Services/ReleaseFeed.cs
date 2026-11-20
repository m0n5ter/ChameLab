using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Services;

/// <summary>
/// The published releases, read from the <c>version.json</c> the website carries: GitHub Pages
/// has no rate limit, unlike the GitHub API (60 requests/hour per address without a token, which
/// a shared mobile or office address exhausts for everyone behind it). The manifest keeps the
/// GitHub release JSON's shape, so the readers parse both the same way. When the manifest cannot
/// be read at all (the website is down), the GitHub API is the fallback.
/// </summary>
internal static class ReleaseFeed
{
    /// <summary>The website's manifest the deploy workflow refreshes after every release.</summary>
    public const string ManifestUrl = "https://m0n5ter.github.io/ChameLab/version.json";

    private const string ApiUrl = "https://api.github.com/repos/" + FirmwareUpdateService.Repository + "/releases";

    /// <summary>A re-check within this time reuses the manifest instead of downloading it again.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static JsonDocument? _manifest;
    private static DateTime _manifestFetched = DateTime.MinValue;

    /// <summary>
    /// The <paramref name="which"/> release ("latest" or "dev") as a GitHub-shaped JSON object
    /// (tag_name, name, body, html_url, assets); null when there is no such release.
    /// </summary>
    public static async Task<JsonDocument?> GetReleaseAsync(string which, CancellationToken ct)
    {
        try
        {
            var manifest = await GetManifestAsync(ct);
            return manifest.RootElement.TryGetProperty(which, out var release)
                && release.ValueKind == JsonValueKind.Object
                ? JsonDocument.Parse(release.GetRawText())
                : null; // the manifest is fine and says there is no such release
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // The website is unreachable or serves a broken manifest: ask GitHub directly.
        }
        return await GetApiReleaseAsync(which, ct);
    }

    /// <summary>The GitHub API fallback: rate-limited, so it only runs when the manifest did not.</summary>
    private static async Task<JsonDocument?> GetApiReleaseAsync(string which, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiUrl}/{(which == "latest" ? "latest" : $"tags/{which}")}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task<JsonDocument> GetManifestAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_manifest is not null && DateTime.UtcNow - _manifestFetched < CacheLifetime)
                return _manifest;
            // no-store so a just-published release is not hidden behind a proxy's cached copy
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await Http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _manifest?.Dispose();
            _manifest = manifest;
            _manifestFetched = DateTime.UtcNow;
            return manifest;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // A browser sends its own; a set one makes Safari ask the server whether it may, and the
        // request fails with "Load failed".
        if (!OperatingSystem.IsBrowser())
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChameLab");
        return http;
    }
}
