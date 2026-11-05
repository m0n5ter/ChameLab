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
}
