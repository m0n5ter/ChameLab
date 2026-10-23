using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Services;

/// <summary>The newest ChameLab release on GitHub: its tag (<c>v1.0.8</c>) is the app version.</summary>
public sealed record AppRelease(Version Version, string Tag, string Name, string Notes, string PageUrl);

/// <summary>
/// Checks GitHub releases for a newer version of the app itself. Release tags carry
/// the app version; builds between releases are <c>1.0.7-dev.N</c> and count as newer than v1.0.7.
/// </summary>
public static partial class AppUpdateService
{
    private static readonly HttpClient Http = CreateHttp();

    /// <summary><c>v1.0.7</c>, <c>1.0.7-dev.3</c>, <c>1.0</c>: the numeric part, any suffix is ignored.</summary>
    [GeneratedRegex(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:-\S+)?$")]
    private static partial Regex VersionPattern();

    /// <summary>The running app's version, from the assembly's informational version (no build metadata).</summary>
    public static string CurrentVersion { get; } =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "dev";

    /// <summary>
    /// The web app is redeployed with every release, so only installed apps need checking.
    /// A local build (0.0.0-local) or one from an untagged repo (0.0.0-dev.N) has nothing to compare.
    /// </summary>
    public static bool IsSupported =>
        !OperatingSystem.IsBrowser() && TryParse(CurrentVersion, out var current) && current > new Version(0, 0, 0);

    /// <summary>The latest (non pre-release) release.</summary>
    public static async Task<AppRelease> GetLatestAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{FirmwareUpdateService.Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("No version has been released yet.");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParse(tag, out var version))
            throw new InvalidOperationException($"Release {tag} is not tagged with a version.");

        return new AppRelease(
            version, tag,
            root.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name ? name : tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.GetProperty("html_url").GetString() ?? $"https://github.com/{FirmwareUpdateService.Repository}/releases");
    }

    /// <summary>
    /// Whether <paramref name="release"/> is newer than the running app. A dev build of the same
    /// version comes after that release, so only a higher version counts.
    /// </summary>
    public static bool IsNewer(AppRelease release) =>
        TryParse(CurrentVersion, out var current) && release.Version > current;

    private static bool TryParse(string text, out Version version)
    {
        var m = VersionPattern().Match(text.Trim());
        version = m.Success
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0)
            : new Version();
        return m.Success;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChameLab");
        return http;
    }
}
