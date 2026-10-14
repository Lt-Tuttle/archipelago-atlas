using System.Text;
using AP_Atlas.Core.Reports;
using AP_Atlas.Core.Testing;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>A pack fix suggestion: the fixes the user ticked, scrubbed, as one event with the fixes attached, posted once.</summary>
public sealed class FixSuggestionsTests
{
    private static readonly ReportContext Context = new("0.1.0-beta.1+abc1234", "beta", "Windows", "10.0.26200", "10.0.4", "4.7.2.stable.mono", "gl_compatibility", 8);
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static FixSuggestion Suggestion(int pairs = 2, string? note = null) => new(
        "Hades", "0.6.0", "server", "Hades Tracker", "1.2.0", "someone/hades-tracker", Sha,
        Enumerable.Range(1, pairs).Select(i => new FixPair("location", $"Elysium/Varatha / Room {i}", $"Clear Room {i} Spear Weapon", $"Clear Room {i} Sword Weapon", 0.84, "loc:unmatched")).ToList(),
        note);

    [Fact]
    public void The_document_holds_the_pack_the_game_and_each_fix_scrubbed_but_not_atlas_own_facts()
    {
        var scrubber = new Scrubber("kimj", names: new[] { "Varatha" });
        var doc = FixSuggestions.Document(Suggestion(note: "Fixed these as kimj"), Context.Release, scrubber.Scrub);
        Assert.Equal(FixSuggestions.Format, (string?)doc["format"]);
        Assert.Equal(Sha, (string?)doc["pack"]!["sha256"]); // a hash is Atlas's own fact, not a token to hide
        Assert.Equal("someone/hades-tracker", (string?)doc["pack"]!["project"]);
        var fixes = (JArray)doc["fixes"]!;
        Assert.Equal(2, fixes.Count);
        Assert.Equal("Clear Room 1 Spear Weapon", (string?)fixes[0]["chose"]);
        Assert.Equal(0.84, (double)fixes[0]["atlas_score"]!);
        Assert.DoesNotContain("Varatha", doc.ToString());
        Assert.DoesNotContain("kimj", doc.ToString());
    }

    [Fact]
    public void At_most_the_cap_is_sent_and_the_rest_is_counted()
    {
        var doc = FixSuggestions.Document(Suggestion(FixSuggestions.MaxPairs + 7), Context.Release, s => s);
        Assert.Equal(FixSuggestions.MaxPairs, ((JArray)doc["fixes"]!).Count);
        Assert.Equal(7, (int)doc["fixes_left_out"]!);
    }

    [Fact]
    public async Task A_suggestion_is_posted_as_an_event_with_the_fixes_attached()
    {
        PoliteHttp.Spacing = TimeSpan.Zero;
        try
        {
            await using var site = new FakeWebSite();
            site.Answer = (path, _) => new FakeWebSite.FullAnswer(path == "/api/43/envelope/" ? 200 : 404, Encoding.UTF8.GetBytes("{\"id\":\"1\"}"));
            Assert.True(SentryDsn.TryParse($"http://abc123@127.0.0.1:{new Uri(site.Site).Port}/43", out var dsn));
            var response = await FixSuggestions.SendAsync(dsn!, Suggestion(), Context, new Scrubber("kimj").Scrub, TestContext.Current.CancellationToken);
            Assert.True(response.Ok, response.Message);
            var request = Assert.Single(site.Received);
            Assert.Equal("/api/43/envelope/", request.Path);
            Assert.Equal("application/x-sentry-envelope", request.Headers["content-type"]);
            var lines = Encoding.UTF8.GetString(request.Body).Split('\n');
            Assert.Equal("event", (string?)JObject.Parse(lines[1])["type"]);
            var @event = JObject.Parse(lines[2]);
            Assert.Equal("fix-suggestion", (string?)@event["tags"]!["kind"]);
            Assert.Equal("Pack fix suggestion: Hades (Hades Tracker)", (string?)@event["message"]!["formatted"]);
            var attachment = JObject.Parse(lines[3]);
            Assert.Equal("attachment", (string?)attachment["type"]);
            Assert.Equal(FixSuggestions.AttachmentName, (string?)attachment["filename"]);
            var document = JObject.Parse(string.Join("\n", lines.Skip(4)));
            Assert.Equal(2, ((JArray)document["fixes"]!).Count);
            Assert.Equal((int)attachment["length"]!, Encoding.UTF8.GetByteCount(string.Join("\n", lines.Skip(4))));
        }
        finally
        {
            PoliteHttp.Spacing = TimeSpan.FromSeconds(1);
        }
    }
}
