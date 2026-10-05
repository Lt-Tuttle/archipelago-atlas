#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AP_Atlas.Core.EngineSetup;
using Godot;
using Newtonsoft.Json;

namespace AP_Atlas.Core
{
    /// <summary>
    /// Atlas's reliability self-test: the protections that keep data safe and logic honest, checked for real.
    /// Run before every release:
    ///   set ATLAS_SELFTEST=1 and ATLAS_DATA_DIR=&lt;an empty scratch folder&gt;, then start Atlas (headless is fine).
    ///   Optional: ATLAS_SELFTEST_AP=&lt;an Archipelago install&gt; also health-checks that engine end to end.
    ///   Optional: ATLAS_SELFTEST_SETUP=1 also sets up the portable engine from nothing in the scratch folder (about 55 MB
    ///   from python.org, GitHub and PyPI, every file hash-checked) and health-checks it.
    /// It refuses to run against a real data folder. Results print as "SELFTEST PASS/FAIL …" lines and go to
    /// selftest_results.txt in the scratch folder; Atlas exits with code 1 if anything failed.
    /// </summary>
    public static partial class SelfTest
    {
        public static bool Requested => System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST") == "1";

        private static readonly List<string> _results = new List<string>();
        private static int _failures, _passes;

        /// <summary>
        /// Why the data folder isn't safe for a test run, or null when it is. Tests need ATLAS_DATA_DIR set to an empty scratch
        /// folder, so they never touch real data; only Atlas's logs, and files whose names start with ownFilesPrefix, may be there.
        /// </summary>
        public static string ScratchFolderProblem(string ownFilesPrefix)
        {
            string dataDir = DataManager.GetDataDirectory();
            string requested = System.Environment.GetEnvironmentVariable("ATLAS_DATA_DIR");
            if (string.IsNullOrWhiteSpace(requested) || !string.Equals(Path.GetFullPath(requested), dataDir, StringComparison.OrdinalIgnoreCase))
                return "set ATLAS_DATA_DIR to an empty scratch folder; tests never run on real data.";
            Directory.CreateDirectory(dataDir);
            bool OwnOrLogs(string entry)
            {
                string name = Path.GetFileName(entry);
                return name.Equals("logs", StringComparison.OrdinalIgnoreCase)
                    || (ownFilesPrefix != null && name.StartsWith(ownFilesPrefix, StringComparison.OrdinalIgnoreCase));
            }
            if (!Directory.EnumerateFileSystemEntries(dataDir).All(OwnOrLogs))
                return "ATLAS_DATA_DIR must be empty (it looks like it holds real data).";
            return null;
        }

        public static async Task<int> RunAsync()
        {
            string problem = ScratchFolderProblem("selftest");
            if (problem != null)
            {
                GD.PrintErr("SELFTEST REFUSED: " + problem);
                return 2;
            }
            string dataDir = DataManager.GetDataDirectory();
            Logger.UseFolder(Path.Combine(dataDir, "logs"));

            Print($"Atlas self-test in {dataDir}");
            Test("SafeFile keeps the previous version as .bak", SafeFileKeepsBackup);
            Test("SafeFile recovers a damaged file from .bak", SafeFileRecoversFromBackup);
            Test("SafeFile recovers a zero-filled file (power loss)", SafeFileRecoversZeroFilled);
            Test("SafeFile restores a file lost mid-swap", SafeFileRestoresMissing);
            Test("SafeFile falls back without a backup and never throws", SafeFileFallsBack);
            Test("Settings survive damage and null collections", SettingsSurviveDamage);
            Test("Profiles survive damage and null lists", ProfilesSurviveDamage);
            Test("Annotations exclusions round-trip", AnnotationsRoundTrip);
            Test("YAML reader: block lists, quotes, comments, unquoted colons", YamlBlockLists);
            Test("YAML reader: weighted games, flow lists, several players", YamlDocuments);
            Test("Engine: a missing engine reports why instead of starting", EngineReportsMissing);
            Test("Apworld sources: GitHub links in any form are understood", RepoLinksParse);
            Test("Engine: an interrupted update is repaired at startup", EngineRecoversInterruptedUpdate);
            await TestAsync("Engine: process registry finds and stops engine processes", ProcessRegistry);
            await TestAsync("Logic: a failed query is a failure, never an empty answer", LogicFailureIsExplicit);
            await TestAsync("Downloads: a file that doesn't match its hash is rejected", DownloadRejectsBadHash);
            Test("Cheese Tracker: links in any form are understood", CheeseLinksParse);
            Test("Cheese Tracker: slots match by number and game, never by guess", CheeseRowsMatch);
            Test("Cheese Tracker: suggestions follow logic and leave judgment calls alone", CheeseSuggestions);
            Test("Cheese Tracker: automatic updates only touch your claimed slots and stop for outside changes", CheeseAutomaticRules);
            Test("Cheese Tracker: the API key is stored encrypted", CheeseKeyEncrypted);
            Test("Cheese Tracker tab: filters, sorting, activity and hint counts match Cheese Tracker's", CheeseTableRules);
            await TestAsync("Cheese Tracker: a failing site is left alone, then asked again", CheeseBacksOff);
            await TestAsync("Cheese Tracker: changes re-read first, keep others' edits, and never touch others' slots", CheeseChangesAreSafe);
            await TestAsync("Cheese Tracker: a read while the site is left alone never sticks as updating", CheeseReadsNeverStick);
            Test("Sphere Tracker: only spheretracker.de room links are taken (never ?refresh)", SphereLinksParse);
            Test("Sphere Tracker: the host's room tables are read, and which multiworld the room is for", SpherePagesParse);
            Test("Sphere Tracker tab: a slot's rows, searching and sorting", SphereTableRules);
            await TestAsync("Sphere Tracker: only a room the host created, for this multiworld; nothing in race mode; never stuck", SpheresHostRoomOnly);
            await TestAsync("Sphere Tracker: a page too large isn't retried on its own, large pages are read less often, a stalled one is cut off", SpheresLargePagesAndStalls);
            Test("Links: only https web pages and existing folders are opened, never files or network shares", LinksOpenSafely);
            Test("Room passwords are saved encrypted, never as plain text (older files are converted)", PasswordsSavedEncrypted);
            Test("Permissions: Always allow is kept and can be taken back; Allow once lasts the session", PermissionsAreKeptAndRevocable);
            await TestAsync("Downloads: the wrong file or a too-large one is never kept; redirects only to web addresses", DownloadsAreChecked);
            await TestAsync("GitHub: a rate limit is reported as one (never as 'not found') and waited out; unchanged answers come from the cache", GitHubLimitsAreRespected);
            Test("JSON: deeply nested input is refused instead of crashing", DeepJsonIsRefused);
            await TestAsync("Your Archipelago install: nothing is added without your OK, and Remove Atlas's files undoes it", OwnInstallNeedsConsent);
            Test("Map packs: versions compare by number; only real PopTracker packs are installed", PackChecks);
            Test("Engine packages: Archipelago's are exact versions with hashes; a world's requirements can't point pip elsewhere", EnginePackagesArePinned);
            await TestAsync("Unawaited work: a failure is reported with what was being done; cancelling isn't one; routine work is only logged", UnawaitedWorkIsReported);
            await TestAsync("SafeFile: a file another program holds is read again, never taken for damage, and never saved over", HeldFilesAreNeverMistakenOrOverwritten);
            Test("SafeFile: a deleted file stays deleted (its backup goes with it)", DeletedFilesStayDeleted);
            await TestAsync("Settings: a burst of changes is written once; closing or an immediate save writes a pending one", SettingsBurstsAreSavedOnce);
            await TestAsync("Profiles another program held at load aren't saved over, and the user is told", HeldProfilesAreNotSavedOver);
            await TestAsync("Window updates handed to the main thread skip a closed owner, and a failure is logged", DeferredWorkRespectsItsOwner);
            await TestAsync("Pack Doctor: an analysis reads its own snapshot, never the fixes as they change", PackDoctorReadsASnapshot);
            await TestAsync("Settings saved from a background thread are written on the main thread, and the log says so", OffThreadSavesMoveToTheMainThread);

            string ap = System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST_AP");
            if (!string.IsNullOrWhiteSpace(ap))
                await TestAsync($"Engine end to end on {ap}", () => EngineEndToEnd(ap));

            if (System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST_SETUP") == "1")
                await TestAsync("Engine: a portable setup from nothing downloads, verifies and passes its health check", PortableSetupEndToEnd);

            if (System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST_ALLGAMES") == "1")
                await TestAsync("Every installed game still rebuilds (no game worse than the baseline)", () => GameRegression(ap));

            string packFolder = System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST_PACKS");
            if (!string.IsNullOrWhiteSpace(packFolder))
                await TestAsync("Map packs load and link (no pack worse than the baseline)", () => PackRegression(packFolder, ap));

            string seeds = System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST_SEEDS");
            if (!string.IsNullOrWhiteSpace(seeds))
                await TestAsync("Logic matches real seeds (no game worse than the baseline)", () => SeedRegression(seeds, ap));

            Print($"SELFTEST DONE: {_passes} passed, {_failures} failed");
            try { File.WriteAllLines(Path.Combine(dataDir, "selftest_results.txt"), _results); } catch { }
            return _failures == 0 ? 0 : 1;
        }

        // =====================================================================
        // Harness
        // =====================================================================

        private static void Print(string line)
        {
            GD.Print(line);
            _results.Add(line);
        }

        private static void Test(string name, Action body)
        {
            try { body(); _passes++; Print("SELFTEST PASS " + name); }
            catch (Exception ex) { _failures++; Print($"SELFTEST FAIL {name}: {ex.Message}"); }
        }

        private static async Task TestAsync(string name, Func<Task> body)
        {
            try { await body(); _passes++; Print("SELFTEST PASS " + name); }
            catch (Exception ex) { _failures++; Print($"SELFTEST FAIL {name}: {ex.Message}"); }
        }

        private static void Expect(bool condition, string what)
        {
            if (!condition) throw new Exception(what);
        }

        private static string Scratch(string name)
        {
            string dir = Path.Combine(DataManager.GetDataDirectory(), "selftest_scratch");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        private class Sample { public int Version { get; set; } public List<string> Items { get; set; } }

        // =====================================================================
        // Files
        // =====================================================================

        private static void SafeFileKeepsBackup()
        {
            string path = Scratch("keep.json");
            SafeFile.WriteJson(path, new Sample { Version = 1 });
            SafeFile.WriteJson(path, new Sample { Version = 2 });
            Expect(SafeFile.ReadJson<Sample>(path, () => null)?.Version == 2, "the newest version wasn't read back");
            Expect(JsonConvert.DeserializeObject<Sample>(File.ReadAllText(path + ".bak"))?.Version == 1, ".bak doesn't hold the previous version");
            Expect(!File.Exists(path + ".tmp"), "a temp file was left behind");
        }

        private static void SafeFileRecoversFromBackup()
        {
            string path = Scratch("damaged.json");
            SafeFile.WriteJson(path, new Sample { Version = 1 });
            SafeFile.WriteJson(path, new Sample { Version = 2 });
            File.WriteAllText(path, "{ \"Version\": 3, \"Items\": [ \"trunc");
            string recovered = null;
            void OnRecovered(string p, string _) => recovered = p;
            SafeFile.Recovered += OnRecovered;
            try
            {
                var value = SafeFile.ReadJson<Sample>(path, () => null);
                Expect(value?.Version == 1, $"expected the .bak (version 1), got {value?.Version.ToString() ?? "nothing"}");
            }
            finally { SafeFile.Recovered -= OnRecovered; }
            Expect(recovered != null, "the recovery wasn't reported");
            Expect(Directory.GetFiles(Path.GetDirectoryName(path), "damaged.json.corrupt-*").Length > 0, "the damaged file wasn't kept aside");
            Expect(SafeFile.ReadJson<Sample>(path, () => null)?.Version == 1, "the restored file doesn't read back");
        }

        private static void SafeFileRecoversZeroFilled()
        {
            string path = Scratch("zeros.json");
            SafeFile.WriteJson(path, new Sample { Version = 1 });
            SafeFile.WriteJson(path, new Sample { Version = 2 });
            File.WriteAllBytes(path, new byte[64]);
            Expect(SafeFile.ReadJson<Sample>(path, () => null)?.Version == 1, "a zero-filled file wasn't recovered");
        }

        private static void SafeFileRestoresMissing()
        {
            string path = Scratch("missing.json");
            SafeFile.WriteJson(path, new Sample { Version = 1 });
            SafeFile.WriteJson(path, new Sample { Version = 2 });
            File.Delete(path); // as if a crash hit between moving the old file aside and the new one in
            Expect(SafeFile.ReadJson<Sample>(path, () => null)?.Version == 1, "the last good copy wasn't restored");
            Expect(File.Exists(path), "the file wasn't put back");
        }

        private static void SafeFileFallsBack()
        {
            string path = Scratch("nobackup.json");
            File.WriteAllText(path, "not json at all");
            var value = SafeFile.ReadJson(path, () => new Sample { Version = 42 });
            Expect(value.Version == 42, "the fallback wasn't used");
        }

        private static void SettingsSurviveDamage()
        {
            var settings = new AppSettings { GlobalFontSize = 17 };
            DataManager.SaveSettings(settings);
            DataManager.SaveSettings(settings);
            string path = Path.Combine(DataManager.GetDataDirectory(), "settings.json");
            File.WriteAllText(path, "{\"GlobalFontSize\": 17, \"MapCameras\": nu");
            var loaded = DataManager.LoadSettings();
            Expect(loaded.GlobalFontSize == 17, "settings weren't recovered");
            File.WriteAllText(path, "{\"GlobalFontSize\": 15, \"MapCameras\": null, \"SlotYamlPaths\": null, \"CollapsedPropertySections\": null, \"MapNodeScale\": 0}");
            loaded = DataManager.LoadSettings();
            Expect(loaded.MapCameras != null && loaded.SlotYamlPaths != null && loaded.CollapsedPropertySections != null, "null collections weren't repaired");
            Expect(loaded.MapNodeScale > 0, "an invalid node scale wasn't repaired");
        }

        private static void ProfilesSurviveDamage()
        {
            var profiles = new List<MultiworldProfile> { new MultiworldProfile { Name = "Test", Slots = new List<string> { "A", "B" } } };
            DataManager.SaveProfiles(profiles);
            DataManager.SaveProfiles(profiles);
            string path = Path.Combine(DataManager.GetDataDirectory(), "profiles.json");
            File.WriteAllText(path, "[{\"Name\": \"Te");
            var loaded = DataManager.LoadProfiles();
            Expect(loaded.Count == 1 && loaded[0].Slots.Count == 2, "profiles weren't recovered from the backup");
            File.WriteAllText(path, "[{\"Name\": \"Broken\", \"Slots\": null, \"ActiveSlots\": null, \"SavedStats\": null}, null]");
            loaded = DataManager.LoadProfiles();
            Expect(loaded.Count == 1 && loaded[0].Slots != null && loaded[0].ActiveSlots != null && loaded[0].SavedStats != null, "null lists weren't repaired");
        }

        private static void AnnotationsRoundTrip()
        {
            string key = Annotations.SlotKey("selftest", "Slot");
            Annotations.SetExclusionOverride(key, 101, true);
            Annotations.SetExclusionOverrides(key, new long[] { 102, 103 }, false);
            Expect(Annotations.GetExclusionOverride(key, 101) == true, "an exclusion wasn't kept");
            Expect(Annotations.GetExclusionOverride(key, 103) == false, "an inclusion wasn't kept");
            Annotations.ClearExclusionOverrides(key);
            Expect(Annotations.GetExclusionOverride(key, 101) == null, "reset didn't clear");
        }

        // =====================================================================
        // YAML
        // =====================================================================

        private static void YamlBlockLists()
        {
            string path = Scratch("a.yaml");
            File.WriteAllText(path, "# comment\nname: Drew{number}\ngame: Dark Souls III\nDark Souls III:\n  exclude_locations:\n" +
                "    - \"FS: Coiled Sword - received from Siegward\"\n    - 'HWL: Broadsword #2'\n    - FS: Ashen Estus Flask\n    - Painted World\n" +
                "  priority_locations: []\n");
            var r = YamlExclusions.Read(path, "Dark Souls III", "Drew2");
            Expect(r.Error == null, "read failed: " + r.Error);
            Expect(r.Names.SequenceEqual(new[] { "FS: Coiled Sword - received from Siegward", "HWL: Broadsword #2", "FS: Ashen Estus Flask", "Painted World" }),
                "names were " + string.Join(" | ", r.Names));
        }

        private static void YamlDocuments()
        {
            string path = Scratch("b.yaml");
            File.WriteAllText(path, "name: Alice\ngame:\n  A Link to the Past: 0\n  Dark Souls II: 50\nDark Souls II:\n  exclude_locations: [\"Things Betwixt: Chest\", 'Majula: Pot']\n" +
                "---\nname: Bob\ngame: Dark Souls II\nDark Souls II:\n  exclude_locations:\n  - Heide: Chest\n");
            var alice = YamlExclusions.Read(path, "Dark Souls II", "Alice");
            Expect(alice.Names.SequenceEqual(new[] { "Things Betwixt: Chest", "Majula: Pot" }), "weighted game / flow list: " + string.Join(" | ", alice.Names));
            var bob = YamlExclusions.Read(path, "Dark Souls II", "Bob");
            Expect(bob.Names.SequenceEqual(new[] { "Heide: Chest" }), "second document: " + string.Join(" | ", bob.Names));
            Expect(YamlExclusions.Read(path, "Dark Souls II", "Carol").Error != null, "an unknown player should be an error");
            Expect(YamlExclusions.Read(Scratch("does-not-exist.yaml"), "X", "Y").Error != null, "a missing file should be an error, not a crash");
        }

        // =====================================================================
        // Engine
        // =====================================================================

        private static void RepoLinksParse()
        {
            var cases = new Dictionary<string, string>
            {
                ["https://github.com/tathxo/DSAP/releases#release-v0.2.6"] = "tathxo/DSAP",
                ["https://github.com/tathxo/DSAP"] = "tathxo/DSAP",
                ["github.com/tathxo/DSAP.git"] = "tathxo/DSAP",
                ["https://github.com/ArsonAssassin/DSAP/releases/download/0.1.1.0/dsr.apworld"] = "ArsonAssassin/DSAP",
                ["https://github.com/owner/my.project-2/tree/main"] = "owner/my.project-2",
                ["git@github.com:owner/repo.git"] = "owner/repo",
                ["owner/repo"] = "owner/repo",
                ["https://example.com/owner/repo"] = null,
                ["not a link"] = null
            };
            foreach (var kv in cases)
            {
                string got = ApworldSources.ParseRepo(kv.Key);
                Expect(got == kv.Value, $"{kv.Key} read as {got ?? "nothing"}, expected {kv.Value ?? "nothing"}");
            }
        }

        private static void EngineReportsMissing()
        {
            var nowhere = EngineInstall.Existing(Scratch("no-archipelago-here"));
            Expect(!nowhere.CanLaunch, "a folder without Archipelago claims it can launch");
            Expect(AtlasEngine.ProblemWith(nowhere) != null, "no problem reported for a missing install");
            Expect(nowhere.WorldsDir == null, "a worlds folder was offered (or created) in a random folder");
            Expect(!Directory.Exists(Path.Combine(Scratch("no-archipelago-here"), "custom_worlds")), "custom_worlds was created in a folder that isn't Archipelago");
            var steps = AtlasEngine.Steps(EngineInstall.Portable());
            Expect(steps.Count > 0 && steps.All(s => s.Id == EngineStepId.Check || s.State != EngineStepState.Ok || s.Id == EngineStepId.Bridge),
                "an empty portable engine reports steps as done");
        }

        private static void EngineRecoversInterruptedUpdate()
        {
            string python = AtlasEngine.PythonDir;
            Directory.CreateDirectory(python + ".previous");
            File.WriteAllText(Path.Combine(python + ".previous", "marker.txt"), "previous");
            Directory.CreateDirectory(python + ".new"); // a staging folder left by the crash
            if (Directory.Exists(python)) Directory.Delete(python, true);
            AtlasEngine.RecoverInterruptedUpdate();
            Expect(File.Exists(Path.Combine(python, "marker.txt")), "the previous runtime wasn't restored");
            Expect(!Directory.Exists(python + ".new"), "the half-finished staging folder wasn't cleared");
            Directory.Delete(python, true);
        }

        private static async Task ProcessRegistry()
        {
            string root = Scratch("fake-engine");
            Directory.CreateDirectory(root);
            var info = new ProcessStartInfo { FileName = "cmd.exe", UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "/c", "ping", "-n", "30", "127.0.0.1" }) info.ArgumentList.Add(a);
            using var process = Process.Start(info);
            ProcessJob.Track(process, root);
            Expect(ProcessJob.RunningUnder(root) == 1, "a running engine process wasn't counted");
            Expect(ProcessJob.RunningUnder(Scratch("other-engine")) == 0, "a process was counted under the wrong engine");
            Expect(ProcessJob.KillAllUnder(root) == 1, "the process wasn't stopped");
            await Task.Run(() => process.WaitForExit(5000));
            Expect(process.HasExited, "the process is still running");
            Expect(ProcessJob.RunningUnder(root) == 0, "a stopped process is still counted");
        }

        private static async Task LogicFailureIsExplicit()
        {
            var engine = new LogicEngineManager(EngineInstall.Existing(Scratch("no-archipelago-here")), _ => { });
            var answer = await engine.GetReachableLocationsAsync(new List<long> { 1, 2, 3 });
            Expect(answer == null, "a stopped engine answered with a list (it would be read as 'unlocks nothing')");
            Expect(!string.IsNullOrEmpty(engine.LastQueryFailure), "no failure reason was given");
            bool started = await engine.StartEngineAsync("Some Game", "Slot", 1, new Dictionary<string, object>());
            Expect(!started && engine.LastStartError?.Code == "no_engine", $"a missing engine should fail with no_engine, got {engine.LastStartError?.Code ?? "success"}");
        }

        private static async Task DownloadRejectsBadHash()
        {
            // A local HTTP server isn't available here, so use a tiny, stable public file and a wrong hash.
            string target = Scratch("download.bin");
            try
            {
                await EngineDownloader.DownloadAsync("https://www.python.org/robots.txt", target, new string('0', 64), null, default);
                throw new Exception("a file with the wrong hash was accepted");
            }
            catch (InvalidDataException) { }
            catch (IOException ex) when (ex.InnerException is System.Net.Http.HttpRequestException)
            {
                Print("  (offline: skipped the download part)");
                return;
            }
            Expect(!File.Exists(target) && !File.Exists(target + ".part"), "a rejected download was left on disk");
        }

        /// <summary>
        /// Replays every generated seed in a folder through the engine. The first run records a baseline (some games
        /// can't be exact without the player's YAML); later runs fail if any game gets worse, and report improvements.
        /// ATLAS_SELFTEST_BASELINE overrides where the baseline lives; ATLAS_SELFTEST_UPDATE_BASELINE=1 rewrites it.
        /// </summary>
        private static async Task SeedRegression(string folder, string apPath)
        {
            var install = string.IsNullOrWhiteSpace(apPath) ? EngineInstall.Portable() : EngineInstall.Existing(apPath);
            Expect(AtlasEngine.ProblemWith(install) == null, "the engine can't run: " + AtlasEngine.ProblemWith(install));
            var seedFiles = Directory.GetFiles(folder, "*.zip").Concat(Directory.GetFiles(folder, "*.archipelago")).OrderBy(f => f).ToList();
            Expect(seedFiles.Count > 0, "no generated seeds in " + folder);

            // State per "seed|player|game": exact; differs (rebuild matched the seed, so differences are real rule gaps);
            // unverifiable (the rebuild can't match: other apworld version, or options missing from slot data, which are
            // re-rolled randomly, so counts vary run to run and only the state is compared); error.
            var now = new Dictionary<string, SeedBaselineEntry>();
            foreach (var seed in seedFiles)
            {
                var report = await SeedVerifier.VerifyAsync(install, seed, null, default);
                if (report.Error != null) Print($"  {Path.GetFileName(seed)}: couldn't run ({report.Error})");
                foreach (var p in report.Players)
                {
                    Print($"  {Path.GetFileName(seed)} · {p.Game} ({p.Name}): {p.Verdict}");
                    string state = p.Error != null ? "error" : p.Exact ? "exact"
                        : p.ChecksumMatch == false || p.Rebuild?.Match == false ? "unverifiable" : "differs";
                    now[$"{Path.GetFileName(seed)}|{p.Player}|{p.Game}"] = new SeedBaselineEntry { State = state, Differences = p.Late + p.Early };
                }
            }

            string baselinePath = System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST_BASELINE");
            if (string.IsNullOrWhiteSpace(baselinePath)) baselinePath = ProjectSettings.GlobalizePath("res://Tests/seed_baseline.json");
            Dictionary<string, SeedBaselineEntry> baseline = null;
            try { baseline = SafeFile.ReadJson<Dictionary<string, SeedBaselineEntry>>(baselinePath, () => null); } catch { }
            if (baseline == null || baseline.Values.Any(v => v?.State == null) || System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST_UPDATE_BASELINE") == "1")
            {
                SafeFile.WriteJson(baselinePath, now);
                Print($"  Baseline recorded in {baselinePath}: " + string.Join(", ", now.GroupBy(v => v.Value.State).Select(g => $"{g.Count()} {g.Key}")) + ".");
                return;
            }
            var worse = new List<string>();
            foreach (var kv in baseline)
            {
                if (!now.TryGetValue(kv.Key, out var cur)) { worse.Add($"{kv.Key}: no result any more"); continue; }
                var was = kv.Value;
                if (was.State == "exact" && cur.State != "exact") worse.Add($"{kv.Key}: was exact, now {cur.State} ({cur.Differences} differences)");
                else if (was.State == "differs" && (cur.State is "unverifiable" or "error" || cur.State == "differs" && cur.Differences > was.Differences))
                    worse.Add($"{kv.Key}: {cur.State} with {cur.Differences} differences (baseline {was.Differences})");
                else if (was.State == "unverifiable" && cur.State == "error") worse.Add($"{kv.Key}: now fails to rebuild");
                else if (cur.State == "exact" && was.State != "exact") Print($"  Improved: {kv.Key} is now exact. Update the baseline to lock it in.");
            }
            foreach (var key in now.Keys.Except(baseline.Keys)) Print($"  New in this run (not in the baseline): {key} ({now[key].State})");
            Expect(worse.Count == 0, "logic got worse: " + string.Join("; ", worse));
        }

        /// <summary>Rebuilds every installed game; fails if a game that worked in the baseline doesn't any more.</summary>
        private static async Task GameRegression(string apPath)
        {
            var install = string.IsNullOrWhiteSpace(apPath) ? EngineInstall.Portable() : EngineInstall.Existing(apPath);
            Expect(AtlasEngine.ProblemWith(install) == null, "the engine can't run: " + AtlasEngine.ProblemWith(install));
            var record = await GameSweep.RunAsync(install, null, default);
            var now = record.Results.ToDictionary(kv => kv.Key, kv => kv.Value.Ok, StringComparer.OrdinalIgnoreCase);
            Print($"  {now.Values.Count(v => v)} of {now.Count} games rebuild and compute logic with default options.");
            string baselinePath = System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST_GAMES_BASELINE");
            if (string.IsNullOrWhiteSpace(baselinePath)) baselinePath = ProjectSettings.GlobalizePath("res://Tests/games_baseline.json");
            Dictionary<string, bool> baseline = null;
            try { baseline = SafeFile.ReadJson<Dictionary<string, bool>>(baselinePath, () => null); } catch { }
            if (baseline == null || System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST_UPDATE_BASELINE") == "1")
            {
                SafeFile.WriteJson(baselinePath, now);
                Print($"  Baseline recorded in {baselinePath}.");
                return;
            }
            var broken = baseline.Where(kv => kv.Value && (!now.TryGetValue(kv.Key, out var ok) || !ok)).Select(kv => kv.Key).ToList();
            foreach (var g in now.Where(kv => kv.Value && baseline.TryGetValue(kv.Key, out var was) && !was)) Print($"  Improved: {g.Key} now rebuilds.");
            Expect(broken.Count == 0, "games that rebuilt before no longer do: " + string.Join(", ", broken));
        }

        /// <summary>
        /// Loads every map pack in a folder (Tools/fetch_pack_corpus.py builds one) and runs the Pack Doctor on each
        /// against its game's real names from the engine. Fails when a pack that loaded no longer does, or its
        /// linking gets worse than the baseline (ATLAS_SELFTEST_PACKS_BASELINE, default res://Tests/packs_baseline.json).
        /// </summary>
        private static async Task PackRegression(string folder, string apPath)
        {
            var install = string.IsNullOrWhiteSpace(apPath) ? EngineInstall.Portable() : EngineInstall.Existing(apPath);
            var zips = Directory.GetFiles(folder, "*.zip").OrderBy(f => f).ToList();
            Expect(zips.Count > 0, "no pack zips in " + folder);

            // Names for every installed game, fetched once, so each pack is loaded, checked and released in turn
            // (packs hold decoded map images: keeping 40 in memory at once could exhaust it).
            var engine = new LogicEngineManager(install, _ => { });
            var known = await engine.FetchLocalNamesAsync(new string[0]);
            if (known != null)
            {
                PopTracker.GameNames.SetKnownGames(known.Value.KnownGames);
                var all = await engine.FetchLocalNamesAsync(known.Value.KnownGames);
                if (all != null) foreach (var t in all.Value.Tables.Values) PopTracker.GameNames.Store(t);
            }

            var now = new Dictionary<string, PackBaselineEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var zip in zips)
            {
                string key = Path.GetFileName(zip);
                var sw = Stopwatch.StartNew();
                PopTracker.LoadedPack pack = null;
                string loadError = null;
                try
                {
                    pack = await Task.Run(() => PopTracker.PopTrackerPackLoader.InspectZipPack(zip));
                    if (pack == null) loadError = "not a PopTracker pack (no usable manifest)";
                }
                catch (Exception ex) { loadError = "the loader crashed: " + ex.GetType().Name + ": " + ex.Message; }
                if (pack == null)
                {
                    now[key] = new PackBaselineEntry { Loaded = false, Note = loadError };
                    Print($"  {key}: {loadError}");
                    continue;
                }
                string game = PopTracker.GameNames.ResolveGame(pack.Manifest);
                var entry = new PackBaselineEntry
                {
                    Loaded = true,
                    Game = game,
                    Maps = pack.Maps.Count,
                    Pins = pack.Locations.Count,
                    LoadIssues = pack.LoadIssues.Count
                };
                if (game != null)
                {
                    try
                    {
                        var inputs = PopTracker.PackDoctor.Prepare(pack, PopTracker.GameNames.Best(game));
                        var report = await Task.Run(() => PopTracker.PackDoctor.Analyze(inputs));
                        entry.SectionsPct = Pct(report.SectionsLinked, report.SectionsTotal);
                        entry.TilesPct = Pct(report.TilesLinked, report.TilesTotal);
                        entry.PlacedPct = Pct(report.ApLocationsPlaced, report.ApLocationsTotal);
                        entry.Problems = report.Findings.Count(f => f.Severity == PopTracker.FindingSeverity.Problem);
                        entry.ScriptsRan = report.ScriptsRan;
                    }
                    catch (Exception ex) { entry.Note = "the Pack Doctor crashed: " + ex.GetType().Name + ": " + ex.Message; }
                }
                else entry.Note = "its game isn't installed in the engine (loader checked only)";
                now[key] = entry;
                Print($"  {key}: {(game ?? pack.Manifest?.GameName ?? "?")} · {entry.Maps} maps, {entry.Pins} pins, {entry.LoadIssues} load issues" +
                      (entry.SectionsPct >= 0 ? $" · checks linked {entry.SectionsPct}%, tiles {entry.TilesPct}%, locations placed {entry.PlacedPct}%, {entry.Problems} problems" : "") +
                      (entry.Note != null ? " · " + entry.Note : "") + $" ({sw.Elapsed.TotalSeconds:0.0}s)");
                pack = null;
                GC.Collect();
            }

            string baselinePath = System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST_PACKS_BASELINE");
            if (string.IsNullOrWhiteSpace(baselinePath)) baselinePath = ProjectSettings.GlobalizePath("res://Tests/packs_baseline.json");
            Dictionary<string, PackBaselineEntry> baseline = null;
            try { baseline = SafeFile.ReadJson<Dictionary<string, PackBaselineEntry>>(baselinePath, () => null); } catch { }
            int crashed = now.Values.Count(v => v.Note != null && v.Note.Contains("crashed"));
            if (baseline == null || System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST_UPDATE_BASELINE") == "1")
            {
                SafeFile.WriteJson(baselinePath, now);
                Print($"  Baseline recorded in {baselinePath}: {now.Values.Count(v => v.Loaded)} of {now.Count} loaded, {now.Values.Count(v => v.SectionsPct >= 0)} checked against real names.");
                Expect(crashed == 0, $"{crashed} pack(s) crashed the loader or the Pack Doctor");
                return;
            }
            var worse = new List<string>();
            foreach (var kv in baseline)
            {
                if (!now.TryGetValue(kv.Key, out var cur)) continue; // pack removed from the corpus
                var was = kv.Value;
                if (was.Loaded && !cur.Loaded) worse.Add($"{kv.Key} no longer loads ({cur.Note})");
                if (cur.Note != null && cur.Note.Contains("crashed") && (was.Note == null || !was.Note.Contains("crashed"))) worse.Add($"{kv.Key}: {cur.Note}");
                if (was.SectionsPct >= 0 && cur.SectionsPct >= 0 && cur.SectionsPct < was.SectionsPct - 1) worse.Add($"{kv.Key} links fewer checks ({was.SectionsPct}% → {cur.SectionsPct}%)");
                if (was.TilesPct >= 0 && cur.TilesPct >= 0 && cur.TilesPct < was.TilesPct - 1) worse.Add($"{kv.Key} links fewer tiles ({was.TilesPct}% → {cur.TilesPct}%)");
                if (was.PlacedPct >= 0 && cur.PlacedPct >= 0 && cur.PlacedPct < was.PlacedPct - 1) worse.Add($"{kv.Key} places fewer locations ({was.PlacedPct}% → {cur.PlacedPct}%)");
                if (cur.LoadIssues > was.LoadIssues) worse.Add($"{kv.Key} has more load issues ({was.LoadIssues} → {cur.LoadIssues})");
            }
            Expect(worse.Count == 0, "map packs got worse: " + string.Join("; ", worse));
        }

        private static int Pct(int part, int total) => total <= 0 ? -1 : (int)Math.Round(100.0 * part / total);

        public class PackBaselineEntry
        {
            public bool Loaded { get; set; }
            public string Game { get; set; }
            public int Maps { get; set; }
            public int Pins { get; set; }
            public int LoadIssues { get; set; }
            public int SectionsPct { get; set; } = -1;
            public int TilesPct { get; set; } = -1;
            public int PlacedPct { get; set; } = -1;
            public int Problems { get; set; }
            public bool ScriptsRan { get; set; }
            public string Note { get; set; }
        }

        public class SeedBaselineEntry
        {
            public string State { get; set; }
            public int Differences { get; set; }
        }

        /// <summary>The whole portable setup, as "Set up everything" runs it, in the scratch folder; then the health check.</summary>
        private static async Task PortableSetupEndToEnd()
        {
            var install = EngineInstall.Portable();
            Expect(!install.CanLaunch, "the scratch folder already has an engine");
            bool ok = await AtlasEngine.SetUpAsync(install, line => Print("  " + line), _ => { }, default);
            Expect(ok, "setup didn't finish (see the lines above)");
            var check = await AtlasEngine.RunCheckAsync(install, line => Print("  " + line), default);
            Expect(check.Passed, "health check failed: " + check.Problem);
            Expect(check.Smoke != null && check.Smoke.Ok, "the end-to-end logic test didn't run or failed");
        }

        private static async Task EngineEndToEnd(string apPath)
        {
            var install = EngineInstall.Existing(apPath);
            Expect(install.CanLaunch, "that folder isn't an Archipelago install");
            Expect(install.HasTracker, "the Universal Tracker isn't installed there");
            var check = await AtlasEngine.RunCheckAsync(install, line => Print("  " + line), default);
            Expect(check.Passed, "health check failed: " + check.Problem);
            Expect(check.Smoke != null && check.Smoke.Ok, "the end-to-end logic test didn't run or failed");
            Expect(check.Smoke.GoalWithAllItems == true, $"the engine couldn't tell that {check.Smoke.Game}'s goal is reachable with every item (go mode detection)");
        }
    }
}
