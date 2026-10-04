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
    /// It refuses to run against a real data folder. Results print as "SELFTEST PASS/FAIL …" lines and go to
    /// selftest_results.txt in the scratch folder; Atlas exits with code 1 if anything failed.
    /// </summary>
    public static class SelfTest
    {
        public static bool Requested => System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST") == "1";

        private static readonly List<string> _results = new List<string>();
        private static int _failures, _passes;

        public static async Task<int> RunAsync()
        {
            string dataDir = DataManager.GetDataDirectory();
            string requested = System.Environment.GetEnvironmentVariable("ATLAS_DATA_DIR");
            if (string.IsNullOrWhiteSpace(requested) || !string.Equals(Path.GetFullPath(requested), dataDir, StringComparison.OrdinalIgnoreCase))
            {
                GD.PrintErr("SELFTEST REFUSED: set ATLAS_DATA_DIR to an empty scratch folder; the self-test never runs on real data.");
                return 2;
            }
            Directory.CreateDirectory(dataDir);
            if (Directory.EnumerateFileSystemEntries(dataDir).Any(e => !Path.GetFileName(e).Equals("logs", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(e).StartsWith("selftest")))
            {
                GD.PrintErr("SELFTEST REFUSED: ATLAS_DATA_DIR must be empty (it looks like it holds real data).");
                return 2;
            }

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
            Test("Engine: an interrupted update is repaired at startup", EngineRecoversInterruptedUpdate);
            await TestAsync("Engine: process registry finds and stops engine processes", ProcessRegistry);
            await TestAsync("Logic: a failed query is a failure, never an empty answer", LogicFailureIsExplicit);
            await TestAsync("Downloads: a file that doesn't match its hash is rejected", DownloadRejectsBadHash);

            string ap = System.Environment.GetEnvironmentVariable("ATLAS_SELFTEST_AP");
            if (!string.IsNullOrWhiteSpace(ap))
                await TestAsync($"Engine end to end on {ap}", () => EngineEndToEnd(ap));

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

        public class SeedBaselineEntry
        {
            public string State { get; set; }
            public int Differences { get; set; }
        }

        private static async Task EngineEndToEnd(string apPath)
        {
            var install = EngineInstall.Existing(apPath);
            Expect(install.CanLaunch, "that folder isn't an Archipelago install");
            Expect(install.HasTracker, "the Universal Tracker isn't installed there");
            var check = await AtlasEngine.RunCheckAsync(install, line => Print("  " + line), default);
            Expect(check.Passed, "health check failed: " + check.Problem);
            Expect(check.Smoke != null && check.Smoke.Ok, "the end-to-end logic test didn't run or failed");
        }
    }
}
