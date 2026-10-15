using System.Text;
using AP_Atlas.Core.Reports;
using AP_Atlas.Core.Testing;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>What Report a problem sends: the scrubber's newer rules, the names seen in rooms, and the report itself.</summary>
public class ProblemReportTests
{
    private static readonly Scrubber Scrubber = new("kimj", new[] { "archipelago.gg:38281" }, new[] { "Tester" }, secrets: new[] { "hunter2" }, userFolder: "kim.j");

    [Theory]
    [InlineData(@"at X.Go() in C:\Users\Kim J\Documents\My Atlas\Scripts\X.cs:line 7", @"at X.Go() in <path>\X.cs:line 7")]
    [InlineData(@"opened ""C:\Users\Kim J\Saves\My Save.txt"" and 'D:\Game Files\a b.json' fine", @"opened ""<path>\My Save.txt"" and '<path>\a b.json' fine")]
    [InlineData("atlas_bridge.py:532 and Godot.cs:88 and data.json:12 but archipelago.gg:38281 and play.example.org:1234", "atlas_bridge.py:532 and Godot.cs:88 and data.json:12 but <host> and <host>")]
    [InlineData("the room password hunter2 (HUNTER2) but not hunter22", "the room password <secret> (<secret>) but not hunter22")]
    [InlineData(@"kim.j's folder C:\Users\kim.j\x.txt", @"<user>'s folder <path>\x.txt")]
    [InlineData("line one by kimj\nline two at archipelago.gg:38281\n\nTester", "line one by <user>\nline two at <host>\n\n<name>")]
    [InlineData(@"Could not find file 'C:\Users\Kim O'Brien\AppData\x.json'.", @"Could not find file '<path>\x.json'.")]
    [InlineData(@"C:\Users\O'Brien\AppData\x.log", @"<path>\x.log")]
    [InlineData(@"Loaded C:\packs\foo.zip (3/5)", @"Loaded <path>\foo.zip (3/5)")]
    [InlineData(@"C:\Atlas\bin;C:\Users\Kim J\AppData\x.log", @"<path>;<path>\x.log")]
    [InlineData("ap.example.com.py:38281 and tcp://play.example.org:1234", "<host> and tcp://<host>")]
    public void The_newer_rules_scrub_what_they_should_and_keep_code_lines(string text, string expected) => Assert.Equal(expected, Scrubber.Scrub(text));

    [Theory]
    [InlineData(@"Profile: C:\Users\Kim Jones", "Profile: <path> <user>")]
    [InlineData(@"saved to C:\Users\Kim Jones.", "saved to <path> <user>.")]
    public void A_user_folder_with_a_space_is_scrubbed_where_the_path_rule_stops(string text, string expected) =>
        Assert.Equal(expected, new Scrubber("Kim", userFolder: "Kim Jones").Scrub(text));

    [Fact]
    public void Names_seen_in_rooms_are_kept_once_without_common_words_and_written_only_when_new()
    {
        using var temp = new TempFolder();
        string reports = temp.File("reports");
        Assert.True(SeenNames.Remember(reports, new[] { "Alice", "Bo", "Server", "alice", " Carol ", null }, new[] { "play.example.org:1234", "" }));
        var seen = SeenNames.Read(reports);
        Assert.Equal(new[] { "Alice", "Carol" }, seen.Names);
        Assert.Equal(new[] { "play.example.org:1234" }, seen.Servers);
        // Nothing new: nothing written.
        var written = File.GetLastWriteTimeUtc(Path.Combine(reports, SeenNames.FileName));
        Assert.False(SeenNames.Remember(reports, new[] { "ALICE", "Carol" }, new[] { "play.example.org:1234" }));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(reports, SeenNames.FileName)));
        // Capped: the oldest go first.
        Assert.True(SeenNames.Remember(reports, Enumerable.Range(0, SeenNames.MaxEach + 5).Select(i => $"Player{i:0000}"), Array.Empty<string>()));
        seen = SeenNames.Read(reports);
        Assert.Equal(SeenNames.MaxEach, seen.Names.Count);
        Assert.DoesNotContain("Alice", seen.Names);
        Assert.Equal($"Player{SeenNames.MaxEach + 4:0000}", seen.Names[^1]);
        // A damaged file reads as nothing seen.
        File.WriteAllText(Path.Combine(reports, SeenNames.FileName), "{\"Names\": null, \"Servers\": null}");
        Assert.Empty(SeenNames.Read(reports).Names);
    }

    private static readonly ReportContext Context = new("0.1.0-beta.1+abc", "beta", "Windows", "10.0.26200", ".NET 10.0", "4.7.2", "opengl3", 8);

    [Fact]
    public void A_report_is_one_event_tagged_with_its_id_and_each_part_an_attachment()
    {
        var parts = new[] { new ProblemPart("system.txt", "System information", "Atlas 0.1.0"), new ProblemPart("atlas_log.txt", "The end of Atlas's log", "line één\n") };
        const string eventId = "0123456789abcdef0123456789abcdef";
        Assert.True(SentryDsn.TryParse("https://abc123@o1.ingest.sentry.io/42", out var dsn));
        var e = ProblemReport.Event(eventId, parts, "  It froze  ", Context, new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal("01234567", ProblemReport.IdOf(eventId));
        Assert.Equal("problem-report", (string?)e["tags"]!["kind"]);
        Assert.Equal("01234567", (string?)e["tags"]!["report_id"]);
        Assert.Equal("It froze", (string?)e["extra"]!["note"]);
        Assert.Equal(new[] { "problem-report", eventId }, e["fingerprint"]!.Select(t => (string)t!));
        Assert.Equal(parts.Sum(p => p.Bytes), ProblemReport.SizeOf(parts));

        string envelope = Encoding.UTF8.GetString(ProblemReport.Envelope(dsn!, eventId, e, parts, new DateTime(2026, 10, 15, 12, 0, 1, DateTimeKind.Utc)));
        var lines = envelope.Split('\n');
        Assert.Equal(eventId, (string?)JObject.Parse(lines[0])["event_id"]);
        Assert.Equal("event", (string?)JObject.Parse(lines[1])["type"]);
        var system = JObject.Parse(lines[3]);
        Assert.Equal("attachment", (string?)system["type"]);
        Assert.Equal("system.txt", (string?)system["filename"]);
        Assert.Equal("Atlas 0.1.0", lines[4]);
        var log = JObject.Parse(lines[5]);
        Assert.Equal("atlas_log.txt", (string?)log["filename"]);
        Assert.Equal(Encoding.UTF8.GetByteCount("line één\n"), (int)log["length"]!);
    }

    [Fact]
    public void A_sent_reports_copy_names_its_id_and_keeps_the_note()
    {
        using var temp = new TempFolder();
        string reports = temp.File("reports");
        var parts = new[] { new ProblemPart("system.txt", "System information", "Atlas 0.1.0") };
        string path = ProblemBundle.Write(reports, parts, "It froze when <name> joined", "01234567", new DateTime(2026, 10, 15, 12, 30, 0));
        using var zip = SafeZip.Open(path);
        Assert.Equal(new[] { "README.txt", "note.txt", "system.txt" }, zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Contains("report 01234567", zip.ReadText(zip.GetEntry("README.txt")!));
        Assert.Equal("It froze when <name> joined\n", zip.ReadText(zip.GetEntry("note.txt")!));
    }
}
