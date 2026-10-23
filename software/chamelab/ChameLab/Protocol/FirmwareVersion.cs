using System;
using System.Text.RegularExpressions;

namespace ChameLab.Protocol;

/// <summary>
/// A firmware version: <c>v2.3.2-cl</c> for a release (the label only names the
/// firmware line and doesn't take part in comparisons), plus the number of firmware
/// commits after it for development builds (<c>v2.3.2-cl-4-gabc1234</c>), which
/// makes them newer than the release itself.
/// </summary>
public readonly partial record struct FirmwareVersion(int Major, int Minor, int Patch, int Commits, string? Label = null)
    : IComparable<FirmwareVersion>
{
    [GeneratedRegex(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:-(?!dirty$)([a-z][a-z0-9]*))?(?:-(\d+)-g[0-9a-f]+)?(?:-dirty)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    /// <summary>Parses a version, a firmware <c>git describe</c>-like string or a plain <c>major.minor</c>.</summary>
    public static bool TryParse(string? text, out FirmwareVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return false;
        static int Group(Match m, int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        version = new FirmwareVersion(Group(m, 1), Group(m, 2), Group(m, 3), Group(m, 5),
            m.Groups[4].Success ? m.Groups[4].Value.ToLowerInvariant() : null);
        return true;
    }

    /// <summary>
    /// What a device runs: its git version when that names a release, else the
    /// <c>major.minor</c> the firmware reports (local builds describe as a bare hash).
    /// </summary>
    public static FirmwareVersion? OfDevice(string gitVersion, string appVersion) =>
        TryParse(gitVersion, out var git) ? git :
        TryParse(appVersion, out var app) ? app : null;

    public int CompareTo(FirmwareVersion other) =>
        (Major, Minor, Patch, Commits).CompareTo((other.Major, other.Minor, other.Patch, other.Commits));

    public static bool operator >(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) < 0;

    public override string ToString()
    {
        var label = Label is null ? "" : $"-{Label}";
        return Commits > 0 ? $"v{Major}.{Minor}.{Patch}{label}+{Commits}" : $"v{Major}.{Minor}.{Patch}{label}";
    }
}
