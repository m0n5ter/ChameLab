using System;
using System.Text.RegularExpressions;

namespace ChameLab.Protocol;

/// <summary>
/// A firmware release version: <c>v2.3.0</c> for a tagged build, plus the number
/// of commits after the tag for development builds (<c>git describe</c> gives
/// <c>v2.3.0-4-gabc1234</c>), which makes them newer than the tag itself.
/// </summary>
public readonly partial record struct FirmwareVersion(int Major, int Minor, int Patch, int Commits)
    : IComparable<FirmwareVersion>
{
    [GeneratedRegex(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:-(\d+)-g[0-9a-f]+)?(?:-dirty)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    /// <summary>Parses a release tag, a <c>git describe</c> string or a plain <c>major.minor</c>.</summary>
    public static bool TryParse(string? text, out FirmwareVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return false;
        static int Group(Match m, int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        version = new FirmwareVersion(Group(m, 1), Group(m, 2), Group(m, 3), Group(m, 4));
        return true;
    }

    /// <summary>
    /// What a device runs: its <c>git describe</c> when that names a release, else the
    /// <c>major.minor</c> the firmware reports (local builds describe as a bare hash).
    /// </summary>
    public static FirmwareVersion? OfDevice(string gitVersion, string appVersion) =>
        TryParse(gitVersion, out var git) ? git :
        TryParse(appVersion, out var app) ? app : null;

    public int CompareTo(FirmwareVersion other) =>
        (Major, Minor, Patch, Commits).CompareTo((other.Major, other.Minor, other.Patch, other.Commits));

    public static bool operator >(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) < 0;

    public override string ToString() =>
        Commits > 0 ? $"v{Major}.{Minor}.{Patch}+{Commits}" : $"v{Major}.{Minor}.{Patch}";
}
