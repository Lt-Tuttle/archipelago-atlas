#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.EngineSetup;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core
{
    /// <summary>Self-tests for the safety and privacy protections: links, secrets, permissions, downloads, GitHub, JSON, the user's install.</summary>
    public static partial class SelfTest
    {
        private static void LinksOpenSafely()
        {
            Expect(ExternalLinks.CheckWeb("https://archipelago.gg/games", out var safe) == null && safe.StartsWith("https://archipelago.gg/"), "a normal https page was refused");
            Expect(ExternalLinks.CheckWeb("http://127.0.0.1:8080/x", out _) == null, "a page on this PC was refused");
            foreach (var bad in new[]
            {
                "file:///C:/Windows/System32/calc.exe", @"C:\Windows\System32\calc.exe", @"\\server\share\run.exe", "javascript:alert(1)",
                "http://example.com/", "https://user:pass@example.com/", "ms-settings:privacy", "data:text/html,<b>x</b>", "", "https://exa mple.com/",
                "https://example.com/\u0007"
            })
                Expect(ExternalLinks.CheckWeb(bad, out _) != null, $"this link should be refused: {bad}");

            string dir = Scratch("links_folder");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "run.exe");
            File.WriteAllText(file, "not a program");
            Expect(ExternalLinks.CheckFolder(dir, out _) == null, "an existing folder was refused");
            Expect(ExternalLinks.CheckFolder(file, out _) != null, "a file was accepted as a folder (opening it could run it)");
            Expect(ExternalLinks.CheckFolder(@"\\server\share", out _) != null, "a network share was accepted");
            Expect(ExternalLinks.CheckFolder("relative\\folder", out _) != null, "a relative path was accepted");
            Expect(ExternalLinks.CheckFolder(Path.Combine(dir, "missing"), out _) != null, "a missing folder was accepted");
        }

        /// <summary>
        /// Text from outside Atlas is shown as written, never read as markup that opens a file. Godot opens the files named
        /// in [img] and [font] tags (first the path's .remap, which this test plants: a .remap Godot reads names a resource,
        /// and Godot logs failing to load it), and for a network path that means connecting to another computer with the
        /// user's Windows sign-in. Through SafeRichText and through the log, no file is opened; rich text reading BBCode
        /// without SafeRichText opens it, which shows the probe works.
        /// </summary>
        private static void OutsideTextIsNeverMarkup()
        {
            string dir = Scratch("markup_probe");
            Directory.CreateDirectory(dir);
            string id = Guid.NewGuid().ToString("N")[..8];
            string png = Path.Combine(dir, "probe.png").Replace('\\', '/');
            string ttf = Path.Combine(dir, "probe.ttf").Replace('\\', '/');
            File.WriteAllText(png + ".remap", "[remap]\npath=\"res://atlas_probe_img_" + id + ".tres\"\n");
            File.WriteAllText(ttf + ".remap", "[remap]\npath=\"res://atlas_probe_font_" + id + ".tres\"\n");
            string hostile = "[img]" + png + "[/img] [font=" + ttf + "]x[/font]";
            string log = Path.Combine(DataManager.GetDataDirectory(), "logs", "atlas_log.txt");
            bool Opened()
            {
                // No log yet means Godot reported nothing.
                string text = File.Exists(log) ? File.ReadAllText(log) : "";
                return text.Contains("atlas_probe_img_" + id) || text.Contains("atlas_probe_font_" + id);
            }

            // SafeRichText, set and appended to: nothing is opened, the tags show as text, and Atlas's own tags still work.
            var safe = new AP_Atlas.UI.SafeRichText { Markup = "[color=lime]own[/color] " + hostile };
            safe.Append("\n" + hostile);
            string shown = safe.GetParsedText();
            safe.Free();
            Expect(!Opened(), "SafeRichText let Godot open a file named in the text");
            Expect(shown.StartsWith("own [img]" + png + "[/img] [font=") && shown.Contains("\n[img]"), $"the text doesn't show as written: '{shown}'");

            // The log: the line for the window shows the message as written.
            string line = null;
            void OnLog(string text, string level) => line ??= text;
            Logger.OnLogMessage += OnLog;
            try { Logger.LogInfo("A pack named " + hostile, "orange"); }
            finally { Logger.OnLogMessage -= OnLog; }
            Expect(line != null && Bbcode.Safe(line) == line && line.Contains("A pack named [lb]img]"), $"a log line keeps the text's tags: {line}");

            // The probe works: rich text that reads BBCode without SafeRichText opens the file.
            var raw = new Godot.RichTextLabel { BbcodeEnabled = true };
            raw.Text = hostile;
            raw.Free();
            Expect(Opened(), "the probe didn't see Godot open the file (has Godot changed how it loads [img] and [font]?)");
        }

        /// <summary>
        /// No pattern runs away on text from outside Atlas. Every regular expression has a time limit (set as Atlas's
        /// assemblies load), and the ones reading large outside text run in linear time: an apworld's source
        /// (GameOfApworld) and a pack's scripts (the option scan). Crafted inputs that would take a backtracking pattern
        /// hours finish in well under the limit, which a fall back to the limit couldn't.
        /// </summary>
        private static void PatternsCantRunAway()
        {
            Expect(new System.Text.RegularExpressions.Regex("a").MatchTimeout == RegexDefaults.MatchTimeout,
                "regular expressions in Atlas have no time limit (was one made before the limit was set?)");

            // An apworld whose world class never closes its bases: 100,000 of them.
            string apworld = Scratch("crafted.apworld");
            if (File.Exists(apworld)) File.Delete(apworld);
            using (var zip = System.IO.Compression.ZipFile.Open(apworld, System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("crafted/__init__.py").Open()))
                for (int i = 0; i < 100_000; i++) writer.Write("class A(World ");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string game = AtlasEngine.GameOfApworld(apworld);
            Expect(game == null && clock.Elapsed < TimeSpan.FromSeconds(1), $"the crafted apworld took {clock.Elapsed.TotalSeconds:0.00} s (and gave '{game}')");

            // A pack script of 200,000 block comments that never close, looked through for the options it reads.
            string pack = Scratch("selftest_crafted.zip");
            var crafted = new System.Text.StringBuilder();
            for (int i = 0; i < 200_000; i++) crafted.Append("--[[ x ");
            AP_Atlas.Core.Testing.FakeMapPack.Write(pack, "Self-test crafted pack", "Self Test Game X", initLua: "local x = 1",
                files: new Dictionary<string, string> { ["scripts/crafted.lua"] = crafted.ToString() });
            var host = PopTracker.PackScriptHost.Load(PopTracker.PopTrackerPackLoader.InspectZipPack(pack)!);
            clock.Restart();
            var options = host!.OptionPathsByCode();
            Expect(options != null && clock.Elapsed < TimeSpan.FromSeconds(1), $"looking through the crafted script took {clock.Elapsed.TotalSeconds:0.00} s");
        }

        private static void PasswordsSavedEncrypted()
        {
            string path = Path.Combine(DataManager.GetDataDirectory(), "profiles.json");
            const string secret = "hunter2-selftest";
            DataManager.SaveProfiles(new List<MultiworldProfile> { new MultiworldProfile { Name = "Secret", Password = secret } });
            string text = File.ReadAllText(path);
            Expect(!text.Contains(secret), "the room password is in profiles.json as plain text");
            Expect(text.Contains("PasswordProtected"), "the encrypted password wasn't saved");
            Expect(DataManager.LoadProfiles().Single().Password == secret, "the password didn't read back");

            // An older file with a plain-text password: read, then saved encrypted, and the .bak copy too.
            File.WriteAllText(path, "[{\"Name\": \"Old\", \"Password\": \"" + secret + "\", \"Slots\": []}]");
            var old = DataManager.LoadProfiles().Single();
            Expect(old.Password == secret, "a plain-text password from an older file wasn't read");
            Expect(!File.ReadAllText(path).Contains(secret), "the older file still holds the password in plain text");
            Expect(!File.Exists(path + ".bak") || !File.ReadAllText(path + ".bak").Contains(secret), "the .bak copy still holds the password in plain text");

            // A password encrypted elsewhere (another PC or account) is kept, not lost, and flagged.
            File.WriteAllText(path, "[{\"Name\": \"Elsewhere\", \"PasswordProtected\": \"bm90IGZvciB0aGlzIFBD\", \"Slots\": []}]");
            var moved = DataManager.LoadProfiles().Single();
            Expect(moved.PasswordUnreadable && moved.Password == "", "an unreadable password wasn't flagged");
            DataManager.SaveProfiles(new List<MultiworldProfile> { moved });
            Expect(File.ReadAllText(path).Contains("bm90IGZvciB0aGlzIFBD"), "an unreadable password was dropped on save");
            DataManager.SaveProfiles(new List<MultiworldProfile>());
            DataManager.SaveProfiles(new List<MultiworldProfile>());
        }

        private static void PermissionsAreKeptAndRevocable()
        {
            Permissions.ResetSessionForTests();
            var settings = new AppSettings();
            try
            {
                Expect(!Permissions.IsAllowed(settings, Permissions.FindArchipelago), "a permission was allowed before being asked");
                Permissions.AllowForSession(Permissions.FindArchipelago);
                Expect(Permissions.IsAllowed(settings, Permissions.FindArchipelago), "Allow once didn't allow");
                Expect(!Permissions.IsAlwaysAllowed(settings, Permissions.FindArchipelago) && settings.PermissionsAllowed.Count == 0, "Allow once was saved");
                Permissions.DenyForSession(Permissions.FindArchipelago);
                Expect(!Permissions.IsAllowed(settings, Permissions.FindArchipelago) && Permissions.DeniedThisSession(Permissions.FindArchipelago), "Don't allow didn't stop it");

                Permissions.SetAlways(settings, Permissions.WriteArchipelago, @"C:\Games\Archipelago\", true);
                Expect(Permissions.IsAllowed(settings, Permissions.WriteArchipelago, @"C:\Games\Archipelago"), "Always allow wasn't kept for that folder");
                Expect(!Permissions.IsAllowed(settings, Permissions.WriteArchipelago, @"D:\Other"), "Always allow leaked to another folder");
                var granted = Permissions.Granted(settings);
                Expect(granted.Count == 1 && granted[0].Kind == Permissions.WriteArchipelago && granted[0].Scope == @"C:\Games\Archipelago", "the kept permission isn't listed");
                Permissions.SetAlways(settings, Permissions.WriteArchipelago, @"C:\Games\Archipelago", false);
                Expect(!Permissions.IsAllowed(settings, Permissions.WriteArchipelago, @"C:\Games\Archipelago") && Permissions.Granted(settings).Count == 0, "taking it back didn't work");
            }
            finally
            {
                Permissions.ResetSessionForTests();
                DataManager.SaveSettings(new AppSettings());
            }
        }

        private static async Task DownloadsAreChecked()
        {
            PoliteHttp.ResetForTests();
            PoliteHttp.Spacing = TimeSpan.Zero;
            using var server = new FakeWebServer();
            const string body = "the right file";
            string goodHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            server.Page("/real", 200, "application/octet-stream", body);
            server.Page("/moved", 302, "text/plain", "", "Location: /real\r\n");
            server.Page("/away", 302, "text/plain", "", "Location: ftp://elsewhere.example/file\r\n");
            server.Page("/big", 200, "application/octet-stream", new string('x', 5000));
            string dest = Scratch("download.bin");
            try
            {
                var r = await PoliteHttp.DownloadAsync(server.Site + "/moved", "test", dest, goodHash, 1 << 20, TimeSpan.FromSeconds(10));
                Expect(r.Ok && File.ReadAllText(dest) == body && r.Text == goodHash, "a redirected download with the right hash wasn't kept: " + r.Message);
                File.Delete(dest);

                r = await PoliteHttp.DownloadAsync(server.Site + "/real", "test", dest, new string('0', 64), 1 << 20, TimeSpan.FromSeconds(10));
                Expect(!r.Ok && !File.Exists(dest) && !File.Exists(dest + ".part"), "a file with the wrong hash was kept");

                r = await PoliteHttp.DownloadAsync(server.Site + "/away", "test", dest, null, 1 << 20, TimeSpan.FromSeconds(10));
                Expect(!r.Ok && !File.Exists(dest), "a redirect to a non-web address was followed");

                r = await PoliteHttp.DownloadAsync(server.Site + "/big", "test", dest, null, 1000, TimeSpan.FromSeconds(10));
                Expect(r.Outcome == WebOutcome.TooLarge && !File.Exists(dest) && !File.Exists(dest + ".part"), "a too-large download wasn't stopped");
            }
            finally
            {
                PoliteHttp.Spacing = TimeSpan.FromSeconds(1);
                PoliteHttp.ResetForTests();
            }
        }

        /// <summary>
        /// Setting the PC's clock (a time sync putting right a wrong boot time, say) changes no wait: waits measure with a
        /// monotonic clock (Deadline). Put back an hour, the next request to a site still goes after the usual second, not
        /// an hour later. Put forward two hours, a site that asked Atlas to slow down is still left alone: ending its wait
        /// early would ask it again too soon. A time a site names (try again at, GitHub's reset) is measured by the site's
        /// clock, so a PC clock that's wrong can't shorten it either.
        /// </summary>
        private static async Task WaitsIgnoreClockChanges()
        {
            PoliteHttp.ResetForTests();
            PoliteHttp.Spacing = TimeSpan.FromSeconds(1);
            var wallClock = Deadline.WallClock;
            using var server = new FakeWebServer();
            server.Page("/page", 200, "text/plain", "hello");
            server.Page("/busy", 429, "text/plain", "slow down", "Retry-After: 60\r\n");
            try
            {
                var first = await PoliteHttp.GetAsync(server.Site + "/page", "test", timeout: TimeSpan.FromSeconds(10));
                Deadline.WallClock = () => DateTime.UtcNow.AddHours(-1);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var second = await PoliteHttp.GetAsync(server.Site + "/page", "test", timeout: TimeSpan.FromSeconds(10));
                Expect(first.Ok && second.Ok && clock.Elapsed < TimeSpan.FromSeconds(5), $"after the clock went back an hour, the next request took {clock.Elapsed.TotalSeconds:0.0} s ({second.Message})");

                var busy = await PoliteHttp.GetAsync(server.Site + "/busy", "test", timeout: TimeSpan.FromSeconds(10));
                Expect(busy.Outcome == WebOutcome.RateLimited && PoliteHttp.WaitingFor(server.Site) != null, "a site that asked Atlas to slow down wasn't left alone");
                Deadline.WallClock = () => DateTime.UtcNow.AddHours(2);
                var during = await PoliteHttp.GetAsync(server.Site + "/page", "test", timeout: TimeSpan.FromSeconds(10));
                Expect(PoliteHttp.WaitingFor(server.Site) != null && during.Outcome == WebOutcome.Waiting, "setting the clock forward ended a site's wait early");

                // A time a site names is measured by the site's own clock (its Date header), so a PC clock that's wrong can't
                // shorten it: with this PC's clock two hours ahead of the site's, "try again at" twenty minutes after the
                // answer still means twenty minutes, and so does GitHub's reset.
                PoliteHttp.StopWaiting(server.Site);
                var sent = DateTime.UtcNow.AddHours(-2);
                server.Page("/busy-until", 429, "text/plain", "slow down", $"Date: {sent:r}\r\nRetry-After: {sent.AddMinutes(20):r}\r\n");
                await PoliteHttp.GetAsync(server.Site + "/busy-until", "test", timeout: TimeSpan.FromSeconds(10));
                var left = PoliteHttp.WaitOf(server.Site).Left;
                Expect(left > TimeSpan.FromMinutes(15), $"with the PC's clock ahead of the site's, its 'try again at' left a wait of {left.TotalMinutes:0} minutes");

                PoliteHttp.ResetForTests();
                GitHubApi.ResetForTests();
                GitHubApi.TestSite = server.Site;
                long reset = new DateTimeOffset(sent.AddMinutes(20)).ToUnixTimeSeconds();
                server.Page("/repos/owner/limited/releases", 403, "application/json", "{\"message\":\"API rate limit exceeded\"}",
                    $"Date: {sent:r}\r\nx-ratelimit-remaining: 0\r\nx-ratelimit-reset: {reset}\r\nx-ratelimit-resource: core\r\n");
                var limited = await GitHubApi.GetAsync("/repos/owner/limited/releases?per_page=50");
                int asked = server.RequestCount;
                var again = await GitHubApi.GetAsync("/repos/owner/other/releases?per_page=50");
                Expect(limited.Outcome == WebOutcome.RateLimited && again.Outcome == WebOutcome.RateLimited && server.RequestCount == asked,
                    $"with the PC's clock ahead of GitHub's, its reset was taken as past ({again.Outcome}: {again.Message})");
            }
            finally
            {
                Deadline.WallClock = wallClock;
                GitHubApi.TestSite = null;
                GitHubApi.ResetForTests();
                PoliteHttp.Spacing = TimeSpan.FromSeconds(1);
                PoliteHttp.ResetForTests();
            }
        }

        private static async Task GitHubLimitsAreRespected()
        {
            PoliteHttp.ResetForTests();
            GitHubApi.ResetForTests();
            PoliteHttp.Spacing = TimeSpan.Zero;
            using var server = new FakeWebServer();
            GitHubApi.TestSite = server.Site;
            long reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
            try
            {
                // A rate limit is reported as a rate limit, and nothing is sent again until GitHub's reset time.
                server.Page("/repos/owner/limited/releases", 403, "application/json", "{\"message\":\"API rate limit exceeded\"}",
                    $"x-ratelimit-remaining: 0\r\nx-ratelimit-reset: {reset}\r\nx-ratelimit-resource: core\r\n");
                var r = await GitHubApi.GetAsync("/repos/owner/limited/releases?per_page=50");
                Expect(r.Outcome == WebOutcome.RateLimited && r.Unknown && !r.NotFound, $"a rate limit came back as {r.Outcome}");
                int before = server.RequestCount;
                r = await GitHubApi.GetAsync("/repos/owner/other/releases?per_page=50");
                Expect(r.Outcome == WebOutcome.RateLimited && server.RequestCount == before, "Atlas asked GitHub again before the reset");

                // Apworld sources: a repository GitHub couldn't be asked about isn't "a repository with no apworlds".
                var (repo, problem) = await ApworldSources.CheckUserRepoAsync("https://github.com/owner/limited", CancellationToken.None);
                Expect(repo == null && problem != null && problem.Contains("couldn't be checked") && !problem.Contains("no .apworld"), "a rate-limited check was reported as: " + problem);

                // An unchanged answer is served from the cache (a 304 doesn't count against the limit).
                GitHubApi.ResetForTests();
                server.Page("/repos/owner/fresh", 200, "application/json", "{\"name\":\"fresh\"}", "ETag: \"v1\"\r\n");
                r = await GitHubApi.GetAsync("/repos/owner/fresh");
                Expect(r.Ok && r.Json?["name"]?.ToString() == "fresh", "a normal answer wasn't read");
                server.Page("/repos/owner/fresh", 304, "application/json", "");
                r = await GitHubApi.GetAsync("/repos/owner/fresh");
                Expect(r.Ok && r.Json?["name"]?.ToString() == "fresh", "an unchanged (304) answer didn't come from the cache");

                server.Page("/repos/owner/gone", 404, "application/json", "{\"message\":\"Not Found\"}");
                r = await GitHubApi.GetAsync("/repos/owner/gone");
                Expect(r.NotFound && !r.Unknown, "a 404 wasn't reported as not found");
            }
            finally
            {
                GitHubApi.TestSite = null;
                GitHubApi.ResetForTests();
                PoliteHttp.Spacing = TimeSpan.FromSeconds(1);
                PoliteHttp.ResetForTests();
            }
        }

        private static void DeepJsonIsRefused()
        {
            string deep = new string('[', 100_000) + new string(']', 100_000);
            bool refused = false;
            try { JsonConvert.DeserializeObject<JToken>(deep); }
            catch (JsonReaderException) { refused = true; }
            Expect(refused, "deeply nested JSON wasn't refused (it could overflow the stack)");
            Expect(JsonConvert.DeserializeObject<JToken>("{\"a\":[[[1]]]}")?["a"] != null, "normal JSON wasn't read");
        }

        private static async Task OwnInstallNeedsConsent()
        {
            Permissions.ResetSessionForTests();
            string root = Scratch("my_archipelago");
            Directory.CreateDirectory(Path.Combine(root, "custom_worlds"));
            File.WriteAllText(Path.Combine(root, "ArchipelagoLauncher.exe"), "");
            var install = EngineInstall.Existing(root);
            string bridge = Path.Combine(root, "custom_worlds", "UltimateBridge.apworld");
            try
            {
                AtlasEngine.InstallBridge(install);
                Expect(!File.Exists(bridge), "Atlas added its bridge to the user's install without their OK");

                Permissions.AllowForSession(Permissions.WriteArchipelago, root);
                AtlasEngine.InstallBridge(install);
                Expect(File.Exists(bridge), "the bridge wasn't added after the user allowed it");
                Expect(AtlasEngine.ChangesIn(root).Any(c => c.Kind == "added" && string.Equals(c.Path, bridge, StringComparison.OrdinalIgnoreCase)), "the change wasn't recorded");

                await AtlasEngine.RemoveAtlasFilesAsync(install, _ => { }, CancellationToken.None);
                Expect(!File.Exists(bridge) && AtlasEngine.ChangesIn(root).Count == 0, "Remove Atlas's files left something behind");
                Expect(File.Exists(Path.Combine(root, "ArchipelagoLauncher.exe")), "Remove Atlas's files touched the user's own files");
            }
            finally
            {
                Permissions.ResetSessionForTests();
            }
        }

        private static void EnginePackagesArePinned()
        {
            string lockText;
            using (var stream = typeof(AtlasEngine).Assembly.GetManifestResourceStream("AtlasEngine.engine_packages.lock"))
            {
                Expect(stream != null, "Atlas's package lock isn't built in");
                using var reader = new StreamReader(stream);
                lockText = reader.ReadToEnd().Replace("\r\n", "\n"); // checkouts may use either line ending
            }
            Expect(System.Text.RegularExpressions.Regex.IsMatch(lockText, @"requirements-signature:\s*[0-9A-F]{16}"), "the lock has no requirements signature");
            var packages = System.Text.RegularExpressions.Regex.Matches(lockText, @"^(\S+)==(\S+) \\$", System.Text.RegularExpressions.RegexOptions.Multiline);
            Expect(packages.Count >= 10, $"the lock lists only {packages.Count} packages");
            foreach (System.Text.RegularExpressions.Match p in packages)
            {
                int after = lockText.IndexOf('\n', p.Index) + 1;
                Expect(lockText.Substring(after).TrimStart().StartsWith("--hash=sha256:"), $"{p.Groups[1].Value} has no hash");
            }
            Expect(!lockText.Split('\n').Any(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("    --hash=sha256:") && !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\S+==\S+ \\$")),
                "the lock has a line that isn't an exact pin or a hash");

            var log = new List<string>();
            string safe = AtlasEngine.SafeWorldRequirements(string.Join("\n", new[]
            {
                "--extra-index-url https://packages.example.com/simple", "-r other.txt", "-e .", "--index-url=https://example.com",
                "pkg==1.0 --hash=sha256:abc", "local @ file:///C:/x.whl", "./local/path", "name @ git+https://github.com/owner/repo@v1",
                "requests>=2", "foo[bar]==1.0; python_version > \"3.8\"", "# a comment"
            }), log.Add);
            var kept = safe.Split('\n').Where(l => l.Length > 0).ToList();
            Expect(kept.SequenceEqual(new[] { "pkg==1.0", "name @ https://github.com/owner/repo/archive/v1.zip", "requests>=2", "foo[bar]==1.0; python_version > \"3.8\"" }),
                "a world's requirements weren't cleaned as expected: " + string.Join(" | ", kept));
            Expect(log.Count >= 6, "dropped requirement lines weren't reported");
        }

        private static void PackChecks()
        {
            Expect(MapPackManagerControl.CompareVersions("1.10.0", "1.9.3") > 0, "1.10.0 should be newer than 1.9.3");
            Expect(MapPackManagerControl.CompareVersions("v2.0", "2.0.0") == 0, "v2.0 and 2.0.0 should be the same version");
            Expect(MapPackManagerControl.CompareVersions("1.0.1", "1.0.2") < 0, "1.0.1 should be older than 1.0.2");

            string pack = Scratch("pack_ok.zip"), notPack = Scratch("pack_bad.zip"), junk = Scratch("pack_junk.zip");
            foreach (var f in new[] { pack, notPack }) if (File.Exists(f)) File.Delete(f);
            using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("mypack/manifest.json").Open())) w.Write("{\"name\":\"x\"}");
            using (var zip = ZipFile.Open(notPack, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("readme.txt").Open())) w.Write("hello");
            File.WriteAllText(junk, "<html>an error page saved as .zip</html>");
            Expect(MapPackManagerControl.CheckPackZip(pack) == null, "a real pack was refused");
            Expect(MapPackManagerControl.CheckPackZip(notPack) != null, "a zip without a manifest was accepted as a pack");
            Expect(MapPackManagerControl.CheckPackZip(junk) != null, "a file that isn't a zip was accepted as a pack");
        }
    }
}
