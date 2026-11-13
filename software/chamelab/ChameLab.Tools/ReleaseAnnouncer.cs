using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChameLab.Tools;

internal static class ReleaseAnnouncer
{
    internal static (string? Firmware, List<string> Bullets)? Section(string path, string tag) => Section(File.ReadAllLines(path), tag);

    // A release's entries: those under "### Summary" ("### Кратко") when it has that part, else all of them
    internal static (string? Firmware, List<string> Bullets)? Section(string[] lines, string tag)
    {
        var start = Array.FindIndex(lines, l => Regex.IsMatch(l, "^## \\[" + Regex.Escape(tag) + "\\]"));
        if (start < 0) return null;
        var match = Regex.Match(lines[start], @"(?:firmware|прошивка) (v\S+)");
        var summary = new List<string>(); var all = new List<string>(); var inSummary = false;
        foreach (var line in lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("## ")))
        {
            var text = line.Trim();
            if (text.StartsWith("### ")) { inSummary = Regex.IsMatch(text[4..].Trim(), "^(summary|кратко)$", RegexOptions.IgnoreCase); continue; }
            var list = inSummary ? summary : all;
            if (text.StartsWith("- ")) list.Add(text[2..].Trim());
            else if (text.Length > 0 && list.Count > 0) list[^1] += " " + text;
        }
        return (match.Success ? match.Groups[1].Value : null, summary.Count > 0 ? summary : all);
    }
    internal static string Html(string text) => string.Join('\n', text.Split('\n').Select(line =>
    {
        line = line.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        line = Regex.Replace(line, "`([^`]+)`", "<code>$1</code>"); line = Regex.Replace(line, @"\*\*(.+?)\*\*", "<b>$1</b>");
        return line.StartsWith("- ") ? "• " + line[2..] : line;
    }));
    internal static string Post(string title, List<string> bullets, string more, string links, int limit, bool html)
    {
        string Render(string text) => html ? Html(text) : text;
        for (var count = bullets.Count; count >= 0; count--)
        {
            var items = bullets.Take(count).Select(b => "- " + b).ToList(); if (count < bullets.Count) items.Add(more);
            var text = Render(string.Join("\n\n", new[] { "**" + title + "**", string.Join('\n', items), links }.Where(s => s.Length > 0)));
            if (text.Length <= limit) return text;
        }
        return Render($"**{title}**\n\n{links}");
    }

    // Google Play takes at most 500 characters of plain text per language
    internal static string PlayNotes(List<string> bullets)
    {
        const int limit = 500;
        var items = bullets.Select(b => Regex.Replace(Regex.Replace(b, "^ChameLab: ", ""), @"`|\*\*", ""))
            .Select(b => "• " + char.ToUpperInvariant(b[0]) + b[1..]).ToList();
        for (var count = items.Count; count > 0; count--)
        {
            var text = string.Join('\n', items.Take(count));
            if (text.Length <= limit) return text;
        }
        if (items.Count == 0) return "";
        // Even the first entry is too long: cut it at a word
        var cut = items[0][..(limit - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > 0 ? cut[..space] : cut).TrimEnd(',', ';', ':', ' ', '—') + "…";
    }

    // whatsnew-<locale> files for the Google Play upload, from the release's summary
    public static void WritePlayNotes(string tag, string directory)
    {
        var english = Section("CHANGELOG.md", tag) ?? throw new ArgumentException("CHANGELOG.md has no section for " + tag);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "whatsnew-en-US"), PlayNotes(english.Bullets));
        if (Section("CHANGELOG.ru.md", tag) is { Bullets.Count: > 0 } russian)
            File.WriteAllText(Path.Combine(directory, "whatsnew-ru-RU"), PlayNotes(russian.Bullets));
        foreach (var file in Directory.GetFiles(directory))
            Console.WriteLine($"{Path.GetFileName(file)}:\n{File.ReadAllText(file)}\n");
    }

    public static async Task Run(string tag, bool dryRun)
    {
        var english = Section("CHANGELOG.md", tag) ?? throw new ArgumentException("CHANGELOG.md has no section for " + tag);
        var russian = Section("CHANGELOG.ru.md", tag);
        var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "m0n5ter/ChameLab";
        // The posts carry the summary and link to the full changelog
        const string site = "https://m0n5ter.github.io/ChameLab/download/";
        var enChangelog = $"https://github.com/{repo}/blob/main/CHANGELOG.md";
        var ruChangelog = $"https://github.com/{repo}/blob/main/CHANGELOG.ru.md";
        var enTitle = $"ChameLab {tag} is out" + (english.Firmware is { } fw ? " · firmware " + fw : "");
        var ruTitle = $"Вышел ChameLab {tag}" + (english.Firmware is { } rf ? " · прошивка " + rf : "");
        var ruLinks = $"Скачать: {site}\nПолный список изменений: {ruChangelog}";
        var posts = new Dictionary<string, string>
        {
            ["discord"] = Post(enTitle, english.Bullets, "- …and more in the full changelog", $"Download: {site}\nFull changelog: {enChangelog}", 1900, false),
            ["telegram"] = Post(ruTitle, russian?.Bullets ?? [], "- …и другие изменения в полном списке", ruLinks, 4000, true),
            ["max"] = Post(ruTitle, russian?.Bullets ?? [], "- …и другие изменения в полном списке", ruLinks, 3900, true)
        };
        if (dryRun) { foreach (var (channel, text) in posts) Console.WriteLine($"{channel} ({text.Length} chars):\n{text}\n"); return; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) }; http.DefaultRequestHeaders.UserAgent.ParseAdd("ChameLab-release-announcer"); var failed = false;
        async Task Send(string channel, string? url, JsonObject body, string? authorization = null)
        {
            if (string.IsNullOrEmpty(url)) { Console.WriteLine(channel + ": not configured, skipped"); return; }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
                using var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode) { failed = true; Console.Error.WriteLine($"{channel}: HTTP {(int)response.StatusCode}"); }
                else Console.WriteLine(channel + ": posted");
            }
            catch (Exception ex) { failed = true; Console.Error.WriteLine(channel + ": " + ex.GetType().Name); }
        }
        string? Env(string name) => Environment.GetEnvironmentVariable(name);
        var telegram = Env("TELEGRAM_BOT_TOKEN"); var max = Env("MAX_BOT_TOKEN"); var chat = Env("MAX_CHAT_ID");
        await Send("telegram", string.IsNullOrEmpty(telegram) ? null : $"https://api.telegram.org/bot{telegram}/sendMessage", new JsonObject { ["chat_id"] = Env("TELEGRAM_CHAT_ID") ?? "@chamelab", ["text"] = posts["telegram"], ["parse_mode"] = "HTML", ["link_preview_options"] = new JsonObject { ["is_disabled"] = true } });
        await Send("max", string.IsNullOrEmpty(max) || string.IsNullOrEmpty(chat) ? null : $"https://platform-api2.max.ru/messages?chat_id={Uri.EscapeDataString(chat)}&disable_link_preview=true", new JsonObject { ["text"] = posts["max"], ["format"] = "html" }, max);
        await Send("discord", Env("DISCORD_WEBHOOK_URL"), new JsonObject { ["content"] = posts["discord"], ["flags"] = 4, ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() } });
        if (failed) throw new IOException("One or more configured announcement channels failed.");
    }
}
