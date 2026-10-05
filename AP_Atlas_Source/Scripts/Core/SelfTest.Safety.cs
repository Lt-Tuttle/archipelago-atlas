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
                var (repo, problem) = await ApworldSources.AddUserRepoAsync(new AppSettings(), "Some Game", "https://github.com/owner/limited", CancellationToken.None);
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
