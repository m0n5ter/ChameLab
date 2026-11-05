using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ChameLab.Tools;
using Xunit;

namespace ChameLab.Cli.Tests;

public class ReviewCycleTests
{
    private const string Marker = "<!-- chameleon-ultra-draft-inactivity:";
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Api(JsonArray pulls, JsonArray comments) : HttpMessageHandler
    {
        public List<(string Method, string Path, JsonNode? Body)> Mutations { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            JsonNode response;
            if (request.Method != HttpMethod.Get)
            {
                Mutations.Add((request.Method.Method, path, request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))));
                response = new JsonObject();
            }
            else if (path.EndsWith("/labels")) response = JsonNode.Parse("[{\"name\":\"feature freeze\"},{\"name\":\"close soon\"}]")!;
            else if (path.EndsWith("/pulls")) response = pulls;
            else if (path.EndsWith("/comments")) response = comments;
            else if (path.EndsWith("/pulls/1")) response = pulls[0]!;
            else throw new InvalidOperationException("Unexpected test request: " + path);
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
    private static JsonObject Pull(bool draft = true, params string[] labels) => new()
    {
        ["number"] = 1, ["draft"] = draft, ["user"] = new JsonObject { ["login"] = "author" },
        ["labels"] = new JsonArray(labels.Select(l => (JsonNode)new JsonObject { ["name"] = l }).ToArray()),
        ["updated_at"] = Now.AddDays(-60).ToString("O")
    };
    private static JsonObject Comment(string phase, int days) => new()
    {
        ["id"] = 10, ["user"] = new JsonObject { ["login"] = "github-actions[bot]" },
        ["body"] = Marker + phase + " -->", ["created_at"] = Now.AddDays(-days).ToString("O"), ["updated_at"] = Now.AddDays(-days).ToString("O")
    };
    private static PrReviewCycle Cycle(Api api, JsonObject? payload = null, string actor = "author") => new(
        new HttpClient(api) { BaseAddress = new Uri("https://example.test/") }, "owner/repo", payload ?? new(), actor, 30, 14, 30, new FixedClock());

    [Fact]
    public async Task FreezeLabelsOnlyReadyPulls()
    {
        var api = new Api(new JsonArray(Pull(false, "close soon"), Pull(true)), []);
        await Cycle(api).Run("start-feature-freeze");
        Assert.Equal(2, api.Mutations.Count);
        Assert.Equal("feature freeze", api.Mutations[0].Body!["labels"]![0]!.GetValue<string>());
        Assert.Equal("DELETE", api.Mutations[1].Method);
    }

    [Theory]
    [InlineData("none", false, 60, "warning")]
    [InlineData("warning", false, 15, "close-soon")]
    [InlineData("close-soon", true, 31, "closed")]
    [InlineData("close-soon", true, 2, "unchanged")]
    [InlineData("close-soon", false, 31, "cancelled")]
    [InlineData("none", true, 0, "close-soon")]
    public async Task InactivityTransitionsPreserveTimersAndManualLabelChanges(string phase, bool labeled, int days, string expected)
    {
        var api = new Api(new JsonArray(Pull(true, labeled ? ["close soon"] : [])), phase == "none" ? [] : new JsonArray(Comment(phase, days)));
        await Cycle(api).Run("scan-drafts");
        if (expected == "unchanged") Assert.Empty(api.Mutations);
        else if (expected == "cancelled") Assert.Equal(("DELETE", "/repos/owner/repo/issues/comments/10"), (Assert.Single(api.Mutations).Method, api.Mutations[0].Path));
        else
        {
            Assert.Contains(api.Mutations, m => m.Body?["body"]?.GetValue<string>().Contains(Marker + expected) == true);
            Assert.Equal(expected == "closed", api.Mutations.Any(m => m.Body?["state"]?.GetValue<string>() == "closed"));
        }
    }

    [Theory]
    [InlineData("author", "no", true)]
    [InlineData("author", "still working", false)]
    [InlineData("reviewer", "no", false)]
    public async Task OnlyAuthorCanDeclineContinuingInterest(string actor, string reply, bool closeSoon)
    {
        var api = new Api(new JsonArray(Pull()), new JsonArray(Comment("warning", 15)));
        var payload = new JsonObject
        {
            ["issue"] = new JsonObject { ["number"] = 1, ["pull_request"] = new JsonObject() },
            ["comment"] = new JsonObject { ["body"] = reply }
        };
        await Cycle(api, payload, actor).Run("discussion-activity");
        Assert.Equal(closeSoon, api.Mutations.Any(m => m.Method == "POST" && m.Path.EndsWith("/labels")));
        Assert.Equal(!closeSoon, api.Mutations.Any(m => m.Method == "DELETE"));
    }
}
