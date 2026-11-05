using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChameLab.Tools;

/// <summary>The existing upstream review lifecycle, executed only by the trusted automation workflow.</summary>
internal sealed class PrReviewCycle(HttpClient http, string repository, JsonObject payload, string actor,
    double warningDays, double responseDays, double closeDays, TimeProvider? clock = null)
{
    private const string Freeze = "feature freeze", CloseSoon = "close soon", Marker = "<!-- chameleon-ultra-draft-inactivity:";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private HashSet<string>? _labels;
    private string Path(string suffix) => $"repos/{repository}/{suffix}";
    private static string Text(JsonNode n, string key) => n[key]?.GetValue<string>() ?? "";
    private static int Number(JsonNode n) => n["number"]!.GetValue<int>();
    private static bool Draft(JsonNode n) => n["draft"]?.GetValue<bool>() == true;
    private static bool HasLabel(JsonNode pull, string name) => pull["labels"]!.AsArray().Any(l => Text(l!, "name").Equals(name, StringComparison.OrdinalIgnoreCase));
    private double DaysSince(string date) => (_clock.GetUtcNow() - DateTimeOffset.Parse(date, CultureInfo.InvariantCulture)).TotalDays;

    public static async Task FromEnvironment(string mode)
    {
        string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new ArgumentException("Missing environment: " + name);
        var repository = Env("GITHUB_REPOSITORY");
        // Preserve the workflow's upstream-only scope even if invoked from the command line.
        if (repository != "RfidResearchGroup/ChameleonUltra") throw new InvalidOperationException("PR lifecycle automation is restricted to its upstream repository.");
        var address = new Uri((Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com").TrimEnd('/') + "/");
        if (address.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("GitHub API must use HTTPS.");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Env("GITHUB_TOKEN"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChameLab-review-cycle/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var payload = JsonNode.Parse(await File.ReadAllTextAsync(Env("GITHUB_EVENT_PATH")))!.AsObject();
        var cycle = new PrReviewCycle(http, repository, payload, Env("GITHUB_ACTOR"),
            double.Parse(Env("DRAFT_WARNING_DAYS"), CultureInfo.InvariantCulture),
            double.Parse(Env("DRAFT_RESPONSE_DAYS"), CultureInfo.InvariantCulture),
            double.Parse(Env("DRAFT_CLOSE_DAYS"), CultureInfo.InvariantCulture));
        await cycle.Run(mode);
    }

    private async Task<JsonNode?> Send(HttpMethod method, string path, JsonNode? data = null, HttpStatusCode? ignore = null)
    {
        using var request = new HttpRequestMessage(method, Path(path));
        if (data is not null) request.Content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        if (response.StatusCode == ignore || response.StatusCode == HttpStatusCode.NoContent) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub {method} {path}: HTTP {(int)response.StatusCode}");
        var text = await response.Content.ReadAsStringAsync(); return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private async Task<List<JsonObject>> All(string path)
    {
        var result = new List<JsonObject>();
        for (var page = 1; page <= 1000; page++)
        {
            var items = (await Send(HttpMethod.Get, path + (path.Contains('?') ? "&" : "?") + $"per_page=100&page={page}"))!.AsArray();
            result.AddRange(items.Select(i => i!.AsObject()));
            if (items.Count < 100) return result;
        }
        throw new InvalidDataException("GitHub pagination limit exceeded.");
    }

    private async Task EnsureLabel(string name)
    {
        _labels ??= (await All("labels")).Select(l => Text(l, "name")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_labels.Contains(name)) return;
        await Send(HttpMethod.Post, "labels", new JsonObject
        {
            ["name"] = name, ["color"] = name == Freeze ? "1d76db" : "d73a4a",
            ["description"] = name == Freeze ? "Included in the current review and release batch" : "Will be closed unless development resumes"
        }, HttpStatusCode.UnprocessableEntity);
        _labels.Add(name);
    }
    private async Task AddLabel(int number, string name)
    { await EnsureLabel(name); await Send(HttpMethod.Post, $"issues/{number}/labels", new JsonObject { ["labels"] = new JsonArray(name) }); }
    private async Task RemoveLabel(JsonObject pull, string name)
    {
        if (HasLabel(pull, name)) await Send(HttpMethod.Delete, $"issues/{Number(pull)}/labels/{Uri.EscapeDataString(name)}", ignore: HttpStatusCode.NotFound);
    }
    private async Task<List<JsonObject>> Comments(int number) => (await All($"issues/{number}/comments"))
        .Where(c => Text(c["user"]!, "login") == "github-actions[bot]" && Text(c, "body").Contains(Marker, StringComparison.Ordinal)).ToList();
    private async Task Clear(JsonObject pull)
    {
        await RemoveLabel(pull, CloseSoon);
        foreach (var comment in await Comments(Number(pull))) await Send(HttpMethod.Delete, $"issues/comments/{comment["id"]}");
    }
    private async Task Mark(JsonObject pull, JsonObject? comment)
    {
        await AddLabel(Number(pull), CloseSoon);
        var body = $"{Marker}close-soon -->\nThis draft pull request is now marked `{CloseSoon}` because no continuing interest was confirmed.\n\nIt will be closed after {closeDays} more days without activity. A new commit, PR edit, or conversation comment will cancel the countdown.";
        await Send(comment is null ? HttpMethod.Post : HttpMethod.Patch, comment is null ? $"issues/{Number(pull)}/comments" : $"issues/comments/{comment["id"]}", new JsonObject { ["body"] = body });
        Console.WriteLine($"Marked draft #{Number(pull)} as {CloseSoon}");
    }

    public async Task Run(string mode)
    {
        if (new[] { warningDays, responseDays, closeDays }.Any(n => !double.IsFinite(n) || n < 0)) throw new ArgumentException("Draft inactivity periods must be nonnegative numbers.");
        switch (mode)
        {
            case "start-feature-freeze":
                await EnsureLabel(Freeze); await EnsureLabel(CloseSoon);
                var ready = (await All("pulls?state=open")).Where(p => !Draft(p)).ToArray();
                foreach (var pull in ready) { await AddLabel(Number(pull), Freeze); await RemoveLabel(pull, CloseSoon); }
                var summary = ready.Length == 0 ? "There were no open, non-draft pull requests to label." : $"Labeled {ready.Length} open, non-draft pull request(s): {string.Join(", ", ready.Select(p => "#" + Number(p)))}.";
                Console.WriteLine(summary);
                if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { } file) await File.AppendAllTextAsync(file, "### Feature-freeze batch started\n\n" + summary + "\n");
                break;
            case "pull-activity":
                var changed = payload["pull_request"]!.AsObject(); var action = Text(payload, "action");
                if (action is "converted_to_draft" or "reopened") await RemoveLabel(changed, Freeze);
                if (Draft(changed) || action is "ready_for_review" or "reopened") await Clear(changed);
                break;
            case "discussion-activity":
                if (payload["issue"] is { } issue && issue["pull_request"] is null || actor.EndsWith("[bot]", StringComparison.Ordinal)) return;
                var number = payload["issue"]?["number"] ?? payload["pull_request"]?["number"];
                if (number is null) return;
                var discussed = (await Send(HttpMethod.Get, $"pulls/{number}"))!.AsObject();
                if (!Draft(discussed)) return;
                var active = (await Comments(Number(discussed))).LastOrDefault();
                if (active is null) return;
                var reply = payload["comment"]?["body"]?.GetValue<string>().Trim() ?? "";
                if (actor == Text(discussed["user"]!, "login") && Regex.IsMatch(reply, @"\A(?:no|no thanks|not anymore|no longer interested|please close(?: this)?|close (?:it|this|this pr)|/close-soon)[\s.!]*\z", RegexOptions.IgnoreCase))
                    await Mark(discussed, active);
                else await Clear(discussed);
                break;
            case "scan-drafts":
                await EnsureLabel(CloseSoon);
                foreach (var pull in (await All("pulls?state=open")).Where(Draft))
                {
                    var comment = (await Comments(Number(pull))).LastOrDefault();
                    var body = comment is null ? "" : Text(comment, "body");
                    var phase = body.Contains(Marker + "close-soon") ? "close-soon" : body.Contains(Marker + "warning") ? "warning" : "";
                    if (HasLabel(pull, CloseSoon) && phase != "close-soon") { await Mark(pull, comment); continue; }
                    if (phase == "close-soon" && !HasLabel(pull, CloseSoon)) { await Send(HttpMethod.Delete, $"issues/comments/{comment!["id"]}"); continue; }
                    if (comment is null && DaysSince(Text(pull, "updated_at")) >= warningDays)
                    {
                        await Send(HttpMethod.Post, $"issues/{Number(pull)}/comments", new JsonObject { ["body"] =
                            $"{Marker}warning -->\nThis draft pull request has not had activity for a while. Are you still interested in finishing it?\n\nAny new commit, PR edit, or conversation comment will reset this timer. If there is no activity for another {responseDays} days, the `{CloseSoon}` label will be added.\n\nIf you are no longer interested, reply with `no`, `no longer interested`, `please close`, or `/close-soon`.\n\n<sub>This reminder was posted after {warningDays} days of inactivity.</sub>" });
                        continue;
                    }
                    if (phase == "warning" && DaysSince(Text(comment!, "created_at")) >= responseDays) { await Mark(pull, comment); continue; }
                    if (phase == "close-soon" && DaysSince(Text(comment!, "updated_at")) >= closeDays)
                    {
                        await Send(HttpMethod.Patch, $"issues/comments/{comment!["id"]}", new JsonObject { ["body"] = $"{Marker}closed -->\nClosing this draft pull request after the `{CloseSoon}` waiting period expired. It can be reopened if development resumes." });
                        await Send(HttpMethod.Patch, $"pulls/{Number(pull)}", new JsonObject { ["state"] = "closed" });
                        Console.WriteLine($"Closed inactive draft #{Number(pull)}");
                    }
                }
                break;
            default: throw new ArgumentException("Unknown review mode: " + mode);
        }
    }
}
