using System;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public class AppUpdateServiceTests
{
    [Theory]
    [InlineData("v1.3.32", 1, 3, 32, 0)]
    [InlineData("1.3.32-dev.5", 1, 3, 32, 5)]
    [InlineData("v1.3", 1, 3, 0, 0)]
    [InlineData("0.0.0-local", 0, 0, 0, 0)]
    public void ParsesVersionAndDevBuild(string text, int major, int minor, int patch, int build)
    {
        Assert.True(AppUpdateService.TryParse(text, out var version, out var devBuild));
        Assert.Equal(new Version(major, minor, patch), version);
        Assert.Equal(build, devBuild);
    }

    [Theory]
    // Stable releases: only a higher version counts, a dev build already follows its release.
    [InlineData("1.3.33", 0, "1.3.32", true)]
    [InlineData("1.3.32", 0, "1.3.32", false)]
    [InlineData("1.3.32", 0, "1.3.32-dev.4", false)]
    [InlineData("1.3.33", 0, "1.3.32-dev.40", true)]
    // Dev builds: a later build of the same version, or of a higher one.
    [InlineData("1.3.32", 5, "1.3.32-dev.4", true)]
    [InlineData("1.3.32", 4, "1.3.32-dev.4", false)]
    [InlineData("1.3.32", 1, "1.3.32", true)]
    [InlineData("1.3.32", 9, "1.3.33", false)]
    public void ComparesDevBuildsWithinAVersion(string release, int devBuild, string current, bool newer) =>
        Assert.Equal(newer, AppUpdateService.IsNewer(Version.Parse(release), devBuild, current));
}
