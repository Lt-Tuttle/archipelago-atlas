using System.Net.Http;
using System.Text;
using AP_Atlas.Core.Reports;
using AP_Atlas.Core.Testing;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

public class ScrubberTests
{
    private static readonly Scrubber Scrubber = new("kimj", new[] { "archipelago.gg:38281", "wss://play.example.net:1234", "192.168.1.5" }, new[] { "Tester", "Alice B", "x" });

    [Theory]
    [InlineData(@"at MainTrackerWindow.Hide() in C:\Users\kimj\Documents\Atlas\Scripts\MainTrackerWindow.cs:line 202", @"at MainTrackerWindow.Hide() in <path>\MainTrackerWindow.cs:line 202")]
    [InlineData(@"saved to D:/Games/Archipelago/PortableData/settings.json now", @"saved to <path>\settings.json now")]
    [InlineData(@"the folder C:\Users\kimj\Documents\Archipelago\ is full", @"the folder <path> is full")]
    [InlineData(@"\\NAS\share\drew\log.txt", @"<path>\log.txt")]
    [InlineData("see https://archipelago.gg/room/AbC123 or wss://archipelago.gg:38281/", "see <url> or <url>")]
    [InlineData("mail someone@example.com today", "mail <email> today")]
    [InlineData("from 192.168.1.5:38281 and 10.0.0.7", "from <ip> and <ip>")]
    [InlineData("at fe80::1 or 2001:db8:85a3:0:0:8a2e:370:7334 but not 02:58:05", "at <ip> or <ip> but not 02:58:05")]
    [InlineData("server archipelago.gg:38281 and play.example.net:1234 and example.org:51234", "server <host> and <host> and <host>")]
    [InlineData("key 0123456789abcdef0123456789abcdef0123 here", "key <token> here")]
    [InlineData("User kimj (KIMJ) but not ankimj or kimj2", "User <user> (<user>) but not ankimj or kimj2")]
    [InlineData("Tester's slot, ALICE B and alice b; tester", "<name>'s slot, <name> and <name>; <name>")]
    [InlineData("play.example.net alone and archipelago.gg alone", "<host> alone and <host> alone")]
    [InlineData("x marks nothing: a one-letter name is left", "x marks nothing: a one-letter name is left")]
    [InlineData("System.InvalidOperationException: Cannot access a disposed object.", "System.InvalidOperationException: Cannot access a disposed object.")]
    [InlineData("[2026-10-07 02:58:05] [INFO] Atlas 0.1.0-beta.1 started.", "[2026-10-07 02:58:05] [INFO] Atlas 0.1.0-beta.1 started.")]
    public void Each_rule_replaces_what_it_should_and_nothing_else(string text, string expected) => Assert.Equal(expected, Scrubber.Scrub(text));

    [Fact]
    public void Nothing_known_scrubs_only_the_general_kinds()
    {
        var plain = new Scrubber(null);
        Assert.Equal(@"<path>\a.cs and <email> and kimj", plain.Scrub(@"C:\x\a.cs and a@b.cc and kimj"));
        Assert.Equal("", plain.Scrub(null));
    }
}

public class CrashReportTests
{
    private const string Sample = "2026-10-07T02:58:05.4750728-07:00\nA background task failed without being checked\n\n" +
        "System.AggregateException: A Task's exception(s) were not observed. (Unable to connect to the remote server)\n" +
        " ---> System.Net.WebSockets.WebSocketException (0x80004005): Unable to connect to the remote server\n" +
        " ---> System.Net.Sockets.SocketException (10061): No connection could be made because the target machine actively refused it.\n" +
        "   at System.Net.Sockets.Socket.AwaitableSocketAsyncEventArgs.ThrowException(SocketError error, CancellationToken cancellationToken)\n" +
        "   --- End of inner exception stack trace ---\n" +
        "   at System.Net.WebSockets.ClientWebSocket.ConnectAsyncCore(Uri uri)\n" +
        "   at Archipelago.MultiClient.Net.Helpers.ArchipelagoSocketHelper.ConnectToProvidedUri(Uri uri)\n" +
        "   --- End of inner exception stack trace ---\n" +
        "   at AP_Atlas.Core.Connections.SessionManager.LogInAsync(ArchipelagoSession session) in C:\\Users\\kimj\\Atlas\\AP_Atlas.Core\\Connections\\SessionManager.cs:line 377\n" +
        "   at MainTrackerWindow.<HideConnectingOverlay>b__16_0() in C:\\Users\\kimj\\Atlas\\Scripts\\MainTrackerWindow.Connections.cs:line 202\n";

    [Fact]
    public void A_crash_file_is_read_into_its_exceptions_innermost_first_with_file_names_only()
    {
        var report = CrashReport.Parse(Sample);
        Assert.Equal(new DateTime(2026, 10, 7, 9, 58, 5, 475, DateTimeKind.Utc), report.WhenUtc!.Value.AddTicks(-report.WhenUtc.Value.Ticks % TimeSpan.TicksPerMillisecond));
        Assert.Equal("A background task failed without being checked", report.What);
        Assert.Equal(3, report.Exceptions.Count);
        Assert.Equal("System.Net.Sockets.SocketException (10061)", report.Exceptions[0].Type);
        Assert.Equal("No connection could be made because the target machine actively refused it.", report.Exceptions[0].Message);
        Assert.Single(report.Exceptions[0].Frames);
        Assert.False(report.Exceptions[0].Frames[0].InApp);
        Assert.Equal("System.Net.WebSockets.WebSocketException (0x80004005)", report.Exceptions[1].Type);
        Assert.Equal(2, report.Exceptions[1].Frames.Count);
        Assert.Equal("System.AggregateException", report.Exceptions[2].Type);
        Assert.StartsWith("A Task's exception(s) were not observed.", report.Exceptions[2].Message);
        var frame = report.Exceptions[2].Frames[0];
        Assert.Equal("LogInAsync(ArchipelagoSession session)", frame.Function);
        Assert.Equal("AP_Atlas.Core.Connections.SessionManager", frame.Module);
        Assert.Equal("SessionManager.cs", frame.File);
        Assert.Equal(377, frame.Line);
        Assert.True(frame.InApp);
        Assert.Equal("MainTrackerWindow", report.Exceptions[2].Frames[1].Module);
        Assert.True(report.Exceptions[2].Frames[1].InApp);
    }

    [Fact]
    public void A_message_over_several_lines_and_a_file_without_a_time_are_read()
    {
        var report = CrashReport.Parse("Unhandled exception\n\nSystem.ObjectDisposedException: Cannot access a disposed object.\nObject name: 'Godot.Button'.\n   at Godot.GodotObject.GetPtr(GodotObject instance)\n");
        Assert.Null(report.WhenUtc);
        Assert.Equal("Unhandled exception", report.What);
        var only = Assert.Single(report.Exceptions);
        Assert.Equal("System.ObjectDisposedException", only.Type);
        Assert.Equal("Cannot access a disposed object.\nObject name: 'Godot.Button'.", only.Message);
        Assert.Single(only.Frames);
        Assert.Empty(CrashReport.Parse("").Exceptions);
    }
}

public class SentryTests
{
    [Theory]
    [InlineData("https://0123456789abcdef0123456789abcdef@o1234567890123456.ingest.us.sentry.io/1234567890123456", true, "https://o1234567890123456.ingest.us.sentry.io", "0123456789abcdef0123456789abcdef", "1234567890123456")]
    [InlineData("http://abc123@127.0.0.1:8765/42", true, "http://127.0.0.1:8765", "abc123", "42")]
    [InlineData("http://abc123@sentry.example.com/42", false, "", "", "")]
    [InlineData("https://o1.ingest.sentry.io/42", false, "", "", "")]
    [InlineData("https://key:secret@o1.ingest.sentry.io/42", false, "", "", "")]
    [InlineData("https://key@o1.ingest.sentry.io/", false, "", "", "")]
    [InlineData("https://key@o1.ingest.sentry.io/not-a-project", false, "", "", "")]
    [InlineData("", false, "", "", "")]
    public void A_dsn_is_read_only_when_it_is_one(string text, bool ok, string site, string key, string project)
    {
        Assert.Equal(ok, SentryDsn.TryParse(text, out var dsn));
        if (!ok) return;
        Assert.Equal(site, dsn!.Site);
        Assert.Equal(key, dsn.PublicKey);
        Assert.Equal(project, dsn.ProjectId);
        Assert.Equal($"/api/{project}/envelope/", dsn.EnvelopePath);
    }

    private static readonly ReportContext Context = new("0.1.0-beta.1+abc1234", "beta", "Windows", "10.0.26200", "10.0.4", "4.7.2.stable.mono", "gl_compatibility", 8);

    [Fact]
    public void The_event_carries_the_exceptions_and_the_build_scrubbed_and_the_envelope_wraps_it()
    {
        var scrubber = new Scrubber("kimj", names: new[] { "Tester" });
        var report = CrashReport.Parse("Unhandled exception\n\nSystem.IO.IOException: Tester's file C:\\Users\\kimj\\x.txt is held\n   at AP_Atlas.Core.SafeFile.Write(String path) in C:\\Users\\kimj\\Atlas\\SafeFile.cs:line 30\n");
        var when = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        var e = SentryEnvelope.Event("0123456789abcdef0123456789abcdef", report, Context, "I was kimj, connecting Tester", scrubber.Scrub, when);
        string json = e.ToString();
        Assert.DoesNotContain("kimj", json);
        Assert.DoesNotContain("Users", json);
        Assert.DoesNotContain("Tester", json);
        Assert.Equal("csharp", (string?)e["platform"]);
        Assert.Equal("error", (string?)e["level"]);
        Assert.Equal("atlas@0.1.0-beta.1+abc1234", (string?)e["release"]);
        Assert.Equal("beta", (string?)e["environment"]);
        Assert.Equal("2026-10-07T12:00:00.000Z", (string?)e["timestamp"]);
        var value = e["exception"]!["values"]![0]!;
        Assert.Equal("System.IO.IOException", (string?)value["type"]);
        Assert.Equal("<name>'s file <path>\\x.txt is held", (string?)value["value"]);
        var frame = value["stacktrace"]!["frames"]![0]!;
        Assert.Equal("Write(String path)", (string?)frame["function"]);
        Assert.Equal("SafeFile.cs", (string?)frame["filename"]);
        Assert.Equal(30, (int?)frame["lineno"]);
        Assert.Equal("10.0.26200", (string?)e["contexts"]!["os"]!["version"]);
        Assert.Equal("unhandled", (string?)e["tags"]!["kind"]);
        Assert.Equal("I was <user>, connecting <name>", (string?)e["extra"]!["note"]);
        Assert.Null(e["user"]);
        Assert.Null(e["server_name"]);

        Assert.True(SentryDsn.TryParse("http://abc123@127.0.0.1:1/42", out var dsn));
        byte[] envelope = SentryEnvelope.Build(dsn!, "0123456789abcdef0123456789abcdef", e, when);
        string[] lines = Encoding.UTF8.GetString(envelope).Split('\n');
        Assert.Equal(3, lines.Length);
        var header = JObject.Parse(lines[0]);
        Assert.Equal("0123456789abcdef0123456789abcdef", (string?)header["event_id"]);
        Assert.Equal("http://abc123@127.0.0.1:1/42", (string?)header["dsn"]);
        var item = JObject.Parse(lines[1]);
        Assert.Equal("event", (string?)item["type"]);
        Assert.Equal(Encoding.UTF8.GetByteCount(lines[2]), (int?)item["length"]);
        Assert.Equal((string?)e["event_id"], (string?)JObject.Parse(lines[2])["event_id"]);
        Assert.Equal("Sentry sentry_version=7, sentry_client=atlas-reporter/0.1.0-beta.1+abc1234, sentry_key=abc123", SentryEnvelope.AuthHeader(dsn!, Context.Release));
    }

    [Fact]
    public void A_report_without_an_exception_is_a_message()
    {
        var e = SentryEnvelope.Event("0123456789abcdef0123456789abcdef", CrashReport.Parse("Unhandled exception (Atlas is closing)\n"), Context, null, s => s, DateTime.UtcNow);
        Assert.Null(e["exception"]);
        Assert.Equal("Unhandled exception (Atlas is closing)", (string?)e["message"]!["formatted"]);
        Assert.Equal("fatal", (string?)e["level"]);
        Assert.Null(e["extra"]!["note"]);
    }
}

public class CrashReporterTests
{
    private static readonly ReportContext Context = new("0.1.0-beta.1", "beta", "Windows", "10.0", "10.0", "4.7.2", "gl", 4);

    private static string Crash(string logs, string name, string what = "Unhandled exception")
    {
        Directory.CreateDirectory(logs);
        string path = Path.Combine(logs, name);
        File.WriteAllText(path, $"2026-10-07T02:58:05.000-07:00\n{what}\n\nSystem.Exception: kimj broke C:\\Users\\kimj\\it.txt\n   at MainTrackerWindow.Go() in C:\\Users\\kimj\\Atlas\\Scripts\\MainTrackerWindow.cs:line 7\n");
        return path;
    }

    [Fact]
    public async Task A_report_is_posted_as_an_envelope_scrubbed_and_the_file_is_sent_once()
    {
        PoliteHttp.Spacing = TimeSpan.Zero;
        try
        {
            await using var site = new FakeWebSite();
            site.Answer = (path, _) => new FakeWebSite.FullAnswer(path == "/api/42/envelope/" ? 200 : 404, Encoding.UTF8.GetBytes("{\"id\":\"1\"}"));
            using var temp = new TempFolder();
            string logs = temp.File("logs"), reports = temp.File("reports");
            string crash = Crash(logs, "crash_1.txt");
            Assert.True(SentryDsn.TryParse($"http://abc123@127.0.0.1:{new Uri(site.Site).Port}/42", out var dsn));
            var reporter = new CrashReporter(logs, reports, dsn!, new Scrubber("kimj"), Context);
            Assert.Equal(new[] { crash }, reporter.PendingFiles());
            string preview = reporter.Preview(crash, "my note by kimj");
            Assert.DoesNotContain("kimj", preview);
            Assert.Contains("<user> broke <path>\\\\it.txt", preview);

            var response = await reporter.SendAsync(crash, "my note by kimj", TestContext.Current.CancellationToken);
            Assert.True(response.Ok, response.Message);
            var request = Assert.Single(site.Received);
            Assert.Equal("POST", request.Method);
            Assert.Equal("/api/42/envelope/", request.Path);
            Assert.Equal("application/x-sentry-envelope", request.Headers["content-type"]);
            Assert.Contains("sentry_key=abc123", request.Headers["x-sentry-auth"]);
            string body = Encoding.UTF8.GetString(request.Body);
            Assert.DoesNotContain("kimj", body);
            Assert.DoesNotContain("Users", body);
            Assert.Contains("\"note\":\"my note by <user>\"", body);
            Assert.Contains("\"type\":\"event\"", body);
            Assert.Empty(reporter.PendingFiles());
            Assert.Equal("sent", reporter.ReadState().Files["crash_1.txt"].Status);
        }
        finally
        {
            PoliteHttp.Spacing = TimeSpan.FromSeconds(1);
        }
    }

    [Fact]
    public async Task A_failed_send_is_tried_again_later_but_not_for_ever_and_a_refusal_ends_it()
    {
        PoliteHttp.Spacing = TimeSpan.Zero;
        try
        {
            await using var site = new FakeWebSite();
            int status = 500;
            site.Answer = (_, _) => new FakeWebSite.FullAnswer(status, Array.Empty<byte>());
            using var temp = new TempFolder();
            string logs = temp.File("logs"), reports = temp.File("reports");
            string crash = Crash(logs, "crash_1.txt");
            Assert.True(SentryDsn.TryParse($"http://abc123@127.0.0.1:{new Uri(site.Site).Port}/42", out var dsn));
            var reporter = new CrashReporter(logs, reports, dsn!, new Scrubber(null), Context);
            for (int i = 1; i <= CrashReporter.MaxAttempts; i++)
            {
                PoliteHttp.StopWaiting(dsn!.Site); // the polite client would otherwise leave a failing site alone for a while
                Assert.False((await reporter.SendAsync(crash, null, TestContext.Current.CancellationToken)).Ok);
                Assert.Equal(i, reporter.ReadState().Files["crash_1.txt"].Attempts);
                Assert.Equal(i < CrashReporter.MaxAttempts, reporter.PendingFiles().Count == 1);
            }
            string refused = Crash(logs, "crash_2.txt");
            status = 401;
            PoliteHttp.StopWaiting(dsn!.Site);
            Assert.False((await reporter.SendAsync(refused, null, TestContext.Current.CancellationToken)).Ok);
            Assert.Equal("failed", reporter.ReadState().Files["crash_2.txt"].Status);
            Assert.Empty(reporter.PendingFiles());
        }
        finally
        {
            PoliteHttp.Spacing = TimeSpan.FromSeconds(1);
        }
    }

    [Fact]
    public void Dismissed_and_old_files_are_not_offered_and_the_newest_comes_first()
    {
        using var temp = new TempFolder();
        string logs = temp.File("logs"), reports = temp.File("reports");
        Assert.True(SentryDsn.TryParse("http://abc123@127.0.0.1:1/42", out var dsn));
        var reporter = new CrashReporter(logs, reports, dsn!, new Scrubber(null), Context);
        Assert.Empty(reporter.PendingFiles());
        string older = Crash(logs, "crash_1.txt"), newer = Crash(logs, "crash_2.txt"), ancient = Crash(logs, "crash_0.txt");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(ancient, DateTime.UtcNow - CrashReporter.MaxAge - TimeSpan.FromDays(1));
        Assert.Equal(new[] { newer, older }, reporter.PendingFiles());
        reporter.Dismiss(new[] { newer });
        Assert.Equal(new[] { older }, reporter.PendingFiles());
        Assert.Equal("dismissed", reporter.ReadState().Files["crash_2.txt"].Status);
    }
}

public class ProblemBundleTests
{
    [Fact]
    public void The_bundle_holds_the_logs_tail_the_crash_reports_and_the_system_information_scrubbed()
    {
        using var temp = new TempFolder();
        string logs = temp.File("logs"), reports = temp.File("reports");
        Directory.CreateDirectory(logs);
        var log = new StringBuilder();
        for (int i = 0; i < 20000; i++) log.Append("[2026-10-07 02:58:05] [INFO] line ").Append(i).Append(" by kimj at C:\\Users\\kimj\\x.txt\n");
        File.WriteAllText(Path.Combine(logs, "atlas_log.txt"), log.ToString());
        File.WriteAllText(Path.Combine(logs, "crash_20261007_025805_475.txt"), "2026-10-07T02:58:05.000-07:00\nUnhandled exception\n\nSystem.Exception: kimj\n");
        File.WriteAllText(Path.Combine(logs, "other.txt"), "not included");

        string path = ProblemBundle.Write(reports, logs, new Scrubber("kimj", names: new[] { "Tester" }), "The Archipelago Atlas 0.1.0\nWindows 10.0 (kimj's PC)\nSlot: Tester", new DateTime(2026, 10, 7, 12, 30, 0));

        Assert.Equal(Path.Combine(reports, "atlas-report-20261007-123000.zip"), path);
        using var zip = SafeZip.Open(path);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "README.txt", "atlas_log.txt", "crash_20261007_025805_475.txt", "system.txt" }, names);
        string tail = zip.ReadText(zip.GetEntry("atlas_log.txt")!);
        Assert.StartsWith("[…]\n[2026-10-07", tail);
        Assert.EndsWith("line 19999 by <user> at <path>\\x.txt\n", tail);
        Assert.DoesNotContain("kimj", tail);
        Assert.InRange(Encoding.UTF8.GetByteCount(tail), 1, ProblemBundle.LogTail + 16);
        Assert.Equal("The Archipelago Atlas 0.1.0\nWindows 10.0 (<user>'s PC)\nSlot: <name>", zip.ReadText(zip.GetEntry("system.txt")!));
        Assert.Contains("System.Exception: <user>", zip.ReadText(zip.GetEntry("crash_20261007_025805_475.txt")!));
        Assert.Contains("issues", zip.ReadText(zip.GetEntry("README.txt")!));
    }
}
