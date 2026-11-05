using ChameLab.Tools;
using Xunit;

namespace ChameLab.Cli.Tests;

public class ReleaseAnnouncerTests
{
    [Fact]
    public void PostsPreserveEscapingAndWholeBulletTruncation()
    {
        Assert.Equal("<b>Release</b>\n• <code>&lt;tag&gt;</code> &amp; text", ReleaseAnnouncer.Html("**Release**\n- `<tag>` & text"));
        var post = ReleaseAnnouncer.Post("Release", [new string('x', 100), "second"], "- more", "https://example.test", 60, false);
        Assert.True(post.Length <= 60);
        Assert.DoesNotContain("xxxxx", post);
        Assert.Contains("more", post);
        Assert.Contains("https://example.test", post);
    }

    [Fact]
    public void SectionTakesTheSummaryWhenThereIsOne()
    {
        string[] lines =
        [
            "## [unreleased][unreleased]",
            "## [v2.0.0][2026-11-05] · firmware v3.0.0-cl",
            "### Summary",
            " - first",
            "   continued",
            " - second",
            "",
            "### Details",
            " - detail",
            "## [v1.0.0][2026-11-04]",
            " - old one",
            " - old two"
        ];
        var release = ReleaseAnnouncer.Section(lines, "v2.0.0")!.Value;
        Assert.Equal("v3.0.0-cl", release.Firmware);
        Assert.Equal(["first continued", "second"], release.Bullets);
        var old = ReleaseAnnouncer.Section(lines, "v1.0.0")!.Value;
        Assert.Null(old.Firmware);
        Assert.Equal(["old one", "old two"], old.Bullets);
    }
}
