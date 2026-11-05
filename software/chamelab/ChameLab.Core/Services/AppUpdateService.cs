using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;

namespace ChameLab.Services;

/// <summary>Which releases the app offers to update to.</summary>
public enum UpdateChannel
{
    /// <summary>Tagged releases only.</summary>
    Stable,

    /// <summary>The development build from the latest <c>main</c> commit, or a newer tagged release.</summary>
    Dev,
}

/// <summary>
/// A ChameLab release on GitHub. <paramref name="DevBuild"/> is the N of a <c>1.0.7-dev.N</c> build
/// (commits after v1.0.7), 0 for a tagged release; <paramref name="Tag"/> is the version as shown.
/// </summary>
public sealed record AppRelease(Version Version, int DevBuild, string Tag, string Name, string Notes, string PageUrl)
{
    public bool IsDevelopment => DevBuild > 0;
}

/// <summary>
/// Checks GitHub releases for a newer version of the app itself. Release tags carry
/// the app version; builds between releases are <c>1.0.7-dev.N</c>, published as the
/// <c>dev</c> pre-release, and count as newer than v1.0.7 and older than v1.0.8.
/// </summary>
public static partial class AppUpdateService
{
    private const string ChannelSetting = "updateChannel";

    private static readonly HttpClient Http = CreateHttp();

    /// <summary><c>v1.0.7</c>, <c>1.0.7-dev.3</c>, <c>1.0</c>: the numeric part and the dev build number, any other suffix is ignored.</summary>
    [GeneratedRegex(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:-dev\.(\d+))?(?:[-+]\S*)?$")]
    private static partial Regex VersionPattern();

    /// <summary>The version in the dev release's name, <c>ChameLab v1.0.7-dev.3 (development)</c>.</summary>
    [GeneratedRegex(@"\bv?(\d+\.\d+(?:\.\d+)?(?:-dev\.\d+)?)\b")]
    private static partial Regex VersionInText();

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
        !OperatingSystem.IsBrowser() && TryParse(CurrentVersion, out var current, out _) && current > new Version(0, 0, 0);

    /// <summary>The saved channel; a development build follows the dev channel until told otherwise.</summary>
    public static UpdateChannel Channel
    {
        get => Enum.TryParse<UpdateChannel>(AppSettings.GetString(ChannelSetting), out var channel)
            ? channel
            : TryParse(CurrentVersion, out _, out var build) && build > 0 ? UpdateChannel.Dev : UpdateChannel.Stable;
        set => AppSettings.Set(ChannelSetting, value.ToString());
    }

    /// <summary>
    /// The newest release on <paramref name="channel"/>: the latest tagged release, and on the dev
    /// channel the dev build when it is newer (it is missing between a release and the next commit).
    /// </summary>
    public static async Task<AppRelease> GetLatestAsync(UpdateChannel channel, CancellationToken ct = default)
    {
        var stable = await GetReleaseAsync("latest", ct);
        if (channel != UpdateChannel.Dev) return stable ?? throw new InvalidOperationException(Strings.Update_NoRelease);

        var dev = await GetReleaseAsync("tags/dev", ct);
        if (stable is null || dev is null) return dev ?? stable ?? throw new InvalidOperationException(Strings.Update_NoRelease);
        return Compare(dev.Version, dev.DevBuild, stable.Version, stable.DevBuild) > 0 ? dev : stable;
    }

    /// <summary>
    /// Whether <paramref name="release"/> is newer than the running app: a higher version, or a
    /// later dev build of the same one. A dev build comes after the release it counts from.
    /// </summary>
    public static bool IsNewer(AppRelease release) => IsNewer(release.Version, release.DevBuild, CurrentVersion);

    internal static bool IsNewer(Version version, int devBuild, string current) =>
        TryParse(current, out var currentVersion, out var currentBuild)
        && Compare(version, devBuild, currentVersion, currentBuild) > 0;

    /// <summary>Reads a release (null when there is none) and its version from the tag, or for the dev release from its name.</summary>
    private static async Task<AppRelease?> GetReleaseAsync(string which, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{FirmwareUpdateService.Repository}/releases/{which}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var name = root.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } text ? text : tag;
        var shown = TryParse(tag, out _, out _) ? tag : VersionInText().Match(name) is { Success: true } m ? "v" + m.Groups[1].Value : tag;
        if (!TryParse(shown, out var version, out var build))
            throw new InvalidOperationException(string.Format(Strings.Update_ReleaseNoVersion, tag));

        return new AppRelease(
            version, build, shown, name,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.GetProperty("html_url").GetString() ?? $"https://github.com/{FirmwareUpdateService.Repository}/releases");
    }

    private static int Compare(Version a, int aBuild, Version b, int bBuild) =>
        a != b ? a.CompareTo(b) : aBuild.CompareTo(bBuild);

    internal static bool TryParse(string text, out Version version, out int devBuild)
    {
        var m = VersionPattern().Match(text.Trim());
        version = m.Success
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0)
            : new Version();
        devBuild = m.Success && m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
        return m.Success;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChameLab");
        return http;
    }
}
