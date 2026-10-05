using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core
{
    /// <summary>Self-tests for the safety nets: unawaited work, file handling and saving.</summary>
    public static partial class SelfTest
    {
        /// <summary>
        /// Async.Fire: a failure is reported once with what was being done (before or after the first await, or already
        /// failed); cancelling isn't a failure; routine work is only logged; success and "nothing" report nothing.
        /// </summary>
        private static async Task UnawaitedWorkIsReported()
        {
            var reported = new List<(string Doing, Exception Error)>();
            void OnFailed(string doing, Exception error) { lock (reported) reported.Add((doing, error)); }
            Async.Failed += OnFailed;
            try
            {
                Async.Fire(Task.Run(async () => { await Task.Delay(30); throw new InvalidOperationException("late failure"); }), "testing a late failure");
                Async.Fire(() => throw new ArgumentException("early failure"), "testing an early failure");
                Async.Fire(Task.FromException(new IOException("already failed")), "testing a failed task");
                Async.Fire(Task.FromCanceled(new CancellationToken(true)), "testing a cancellation");
                Async.Fire(Task.Run(() => throw new OperationCanceledException()), "testing a cancellation from inside");
                Async.Fire(Task.Run(() => throw new InvalidOperationException("routine")), "testing routine work", tellUser: false);
                Async.Fire(Task.CompletedTask, "testing success");
                Async.Fire((Task?)null, "testing nothing");

                for (int i = 0; i < 100; i++)
                {
                    lock (reported) if (reported.Count >= 3) break;
                    await Task.Delay(20);
                }
                // Give anything that shouldn't be reported time to arrive as well.
                await Task.Delay(200);
                List<(string Doing, Exception Error)> seen;
                lock (reported) seen = reported.ToList();
                Expect(seen.Count == 3, $"3 failures should be reported, got {seen.Count}: {string.Join(", ", seen.Select(r => r.Doing))}");
                Expect(seen.Any(r => r.Doing == "testing a late failure" && r.Error is InvalidOperationException && r.Error.Message == "late failure"),
                    "a failure after an await is reported with its own exception (not wrapped)");
                Expect(seen.Any(r => r.Doing == "testing an early failure" && r.Error is ArgumentException), "a throw before the first await is reported");
                Expect(seen.Any(r => r.Doing == "testing a failed task" && r.Error is IOException), "an already-failed task is reported");
            }
            finally
            {
                Async.Failed -= OnFailed;
            }
        }

        /// <summary>
        /// A file another program holds (a virus scan, a sync tool) is read again until it's free, and is never taken for
        /// damage: no .corrupt copy, no backup restored over it. If it stays held, Atlas uses the fallback and refuses to
        /// save over it until it reads again.
        /// </summary>
        private static async Task HeldFilesAreNeverMistakenOrOverwritten()
        {
            string path = Scratch("held.json");
            SafeFile.WriteJson(path, new Sample { Version = 1 });
            SafeFile.WriteJson(path, new Sample { Version = 2 });
            Expect(File.Exists(path + ".bak"), "setup: the older version is the backup");
            string folder = Path.GetDirectoryName(path) ?? "";
            bool SetAside() => Directory.GetFiles(folder, "held.json.corrupt-*").Length > 0;
            var recovered = new List<string>();
            void OnRecovered(string file, string what) { lock (recovered) recovered.Add(what); }
            SafeFile.Recovered += OnRecovered;
            try
            {
                // Held briefly: read again until it's free, and get the real contents.
                var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                var release = Task.Run(async () => { await Task.Delay(250); holder.Dispose(); });
                var read = await Task.Run(() => SafeFile.ReadJson(path, () => new Sample { Version = -1 }));
                await release;
                Expect(read.Version == 2, $"a briefly held file reads as it is (version 2), not {read.Version}");
                Expect(recovered.Count == 0, "a briefly held file isn't damage: " + string.Join("; ", recovered));
                Expect(!SetAside(), "a held file is never set aside as damaged");

                // Held throughout: the fallback, a message, and no saving over it.
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var fallback = await Task.Run(() => SafeFile.ReadJson(path, () => new Sample { Version = -1 }));
                    Expect(fallback.Version == -1, "a file that stays held gives the fallback");
                }
                Expect(recovered.Count == 1 && recovered[0].Contains("another program"), "the user is told why it couldn't be read: " + string.Join("; ", recovered));
                bool refused = false;
                try { SafeFile.WriteJson(path, new Sample { Version = 99 }); }
                catch (IOException) { refused = true; }
                Expect(refused, "a file that couldn't be read must not be saved over");
                Expect(Newtonsoft.Json.JsonConvert.DeserializeObject<Sample>(File.ReadAllText(path))?.Version == 2, "the file on disk is still the real one");
                Expect(!SetAside(), "still nothing set aside as damaged");

                // Once it reads again, saving works again.
                Expect(SafeFile.ReadJson(path, () => new Sample { Version = -1 }).Version == 2, "it reads once it's free");
                SafeFile.WriteJson(path, new Sample { Version = 3 });
                Expect(SafeFile.ReadJson(path, () => new Sample { Version = -1 }).Version == 3, "and saving works again");
            }
            finally
            {
                SafeFile.Recovered -= OnRecovered;
            }
        }

        /// <summary>
        /// SaveSettingsSoon writes a burst of changes once, half a second after the last; an immediate save or closing
        /// (FlushPendingSaves) writes a pending one at once, and nothing is written twice. Every write moves the previous
        /// version to .bak, so .bak shows how many writes there were.
        /// </summary>
        private static async Task SettingsBurstsAreSavedOnce()
        {
            string path = Path.Combine(DataManager.GetDataDirectory(), "settings.json");
            int OnDisk(string file) => Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(file))?.MainSplitOffset ?? -1;
            var settings = new AppSettings { MainSplitOffset = 100 };
            DataManager.SaveSettings(settings);

            // A drag: three changes in a row are one write, half a second after the last.
            foreach (int offset in new[] { 101, 102, 103 })
            {
                settings.MainSplitOffset = offset;
                DataManager.SaveSettingsSoon(settings);
            }
            await Task.Delay(250);
            Expect(OnDisk(path) == 100, "nothing is written while changes are still coming");
            await Task.Delay(600);
            Expect(OnDisk(path) == 103, $"the last value is written after the burst, not {OnDisk(path)}");
            Expect(OnDisk(path + ".bak") == 100, "the burst is written once (the backup is the value from before it)");

            // Closing writes a pending save at once, and the timer doesn't write it again.
            settings.MainSplitOffset = 104;
            DataManager.SaveSettingsSoon(settings);
            DataManager.FlushPendingSaves();
            Expect(OnDisk(path) == 104, "closing writes a pending save at once");
            await Task.Delay(700);
            Expect(OnDisk(path + ".bak") == 103, "a flushed save isn't written a second time");

            // An immediate save includes a pending one.
            settings.MainSplitOffset = 105;
            DataManager.SaveSettingsSoon(settings);
            settings.MainSplitOffset = 106;
            DataManager.SaveSettings(settings);
            await Task.Delay(700);
            Expect(OnDisk(path) == 106 && OnDisk(path + ".bak") == 104, "an immediate save takes the pending one's place");
        }

        /// <summary>
        /// Profiles another program held while Atlas loaded them aren't saved over (Atlas only has the empty fallback);
        /// the failed save is reported to the user, and the real file survives.
        /// </summary>
        private static async Task HeldProfilesAreNotSavedOver()
        {
            string path = Path.Combine(DataManager.GetDataDirectory(), "profiles.json");
            var real = new List<MultiworldProfile> { new MultiworldProfile { Name = "Real multiworld" } };
            DataManager.SaveProfiles(real);
            var failures = new List<string>();
            void OnSaveFailed(string file, string reason) { lock (failures) failures.Add(file); }
            DataManager.SaveFailed += OnSaveFailed;
            try
            {
                List<MultiworldProfile> loaded;
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    loaded = await Task.Run(DataManager.LoadProfiles);
                Expect(loaded.Count == 0, "while held, the profiles can't be read (the empty fallback)");
                DataManager.SaveProfiles(loaded);
                Expect(failures.Contains("profiles.json"), "the refused save is reported to the user");
                var onDisk = DataManager.LoadProfiles();
                Expect(onDisk.Count == 1 && onDisk[0].Name == "Real multiworld", "the real profiles survive");
                DataManager.SaveProfiles(onDisk);
                Expect(failures.Count == 1, "once read again, saving works again");
            }
            finally
            {
                DataManager.SaveFailed -= OnSaveFailed;
            }
        }

        /// <summary>
        /// Ui.Defer runs work on the main thread later, but not for an owner freed meanwhile (a closed slot or window); work
        /// with no owner always runs; a failure is logged instead of being lost.
        /// </summary>
        private static async Task DeferredWorkRespectsItsOwner()
        {
            var errors = new List<string>();
            void OnLog(string line, string level) { if (level == "ERROR") lock (errors) errors.Add(line); }
            Logger.OnLogMessage += OnLog;
            var alive = new Godot.Node();
            var freed = new Godot.Node();
            try
            {
                int ranForFreed = 0, ranForAlive = 0, ranWithoutOwner = 0;
                AP_Atlas.UI.Ui.Defer(freed, () => ranForFreed++);
                freed.Free();
                AP_Atlas.UI.Ui.Defer(alive, () => ranForAlive++);
                AP_Atlas.UI.Ui.Defer(null, () => ranWithoutOwner++);
                AP_Atlas.UI.Ui.Defer(alive, () => throw new InvalidOperationException("deferred failure"), "testing a deferred failure");
                await Task.Delay(200);
                Expect(ranForFreed == 0, "work for a freed owner must not run");
                Expect(ranForAlive == 1 && ranWithoutOwner == 1, $"work for a live owner, and without one, runs once each (ran {ranForAlive} and {ranWithoutOwner})");
                lock (errors) Expect(errors.Any(e => e.Contains("testing a deferred failure") && e.Contains("deferred failure")), "a failure is logged with what was being done");
            }
            finally
            {
                Logger.OnLogMessage -= OnLog;
                alive.Free();
            }
        }

        /// <summary>
        /// A measured step's time leaves out garbage collection pauses during it: a collection another thread set off isn't
        /// blamed on the step that happened to be running, and isn't counted twice (the hitch report lists pauses on their
        /// own line).
        /// </summary>
        private static void StepTimesLeaveOutCollections()
        {
            // The self-test runs before the window's hitch monitor records the main thread.
            int mainThread = PerfMonitor.MainThreadId;
            PerfMonitor.MainThreadId = Environment.CurrentManagedThreadId;
            try
            {
                PerfMonitor.DrainFrameScopes();
                // Live objects for the collector to move, so its pauses are long enough to tell apart.
                var kept = Enumerable.Range(0, 200_000).Select(_ => new byte[64]).ToList();
                var pausedBefore = GC.GetTotalPauseDuration();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                using (PerfMonitor.Measure("Self-test: collecting"))
                {
                    for (int i = 0; i < 3; i++) GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
                double elapsedMs = watch.Elapsed.TotalMilliseconds;
                double pausedMs = (GC.GetTotalPauseDuration() - pausedBefore).TotalMilliseconds;
                GC.KeepAlive(kept);
                var step = PerfMonitor.DrainFrameScopes().Single(s => s.Label == "Self-test: collecting");
                Expect(pausedMs >= 1, $"the collections paused the program for only {pausedMs:0.0} ms, too little to tell apart");
                Expect(step.Ms <= elapsedMs - pausedMs + 1, $"the step took {step.Ms:0.0} ms of {elapsedMs:0.0}, {pausedMs:0.0} of them collecting: the pauses were counted in");
            }
            finally
            {
                PerfMonitor.MainThreadId = mainThread;
            }
        }

        /// <summary>
        /// The Pack Doctor analyses a snapshot taken on the main thread: a fix edited while an analysis runs doesn't change
        /// it under the analysis, and the next snapshot sees the edit. The pack's own mapping is linked as usual.
        /// </summary>
        private static async Task PackDoctorReadsASnapshot()
        {
            var pack = new PopTracker.LoadedPack
            {
                Manifest = new PopTracker.PopTrackerManifest { Name = "Self-test pack", GameName = "Self-test Game", Version = "1.0" },
                SourcePath = Scratch("selftest_pack.zip")
            };
            var pin = new PopTracker.PopTrackerLocation { Name = "Village", FullPath = "Village" };
            pin.Sections.Add(new PopTracker.PopTrackerSection { Name = "Chest" });
            pack.Locations.Add(pin);
            pack.LocationMappingById[1001] = new List<string> { "@Village/Chest" };
            var names = new PopTracker.GameNameTable { Game = "Self-test Game", Source = "server" };
            names.Locations["Village Chest"] = 1001;
            string key = PopTracker.PackFixes.KeyFor(pack);

            var snapshot = PopTracker.PackDoctor.Prepare(pack, names);
            var first = await Task.Run(() => PopTracker.PackDoctor.Analyze(snapshot));
            Expect(first.Findings.Count > 0, "a pack without scripts should get at least one finding");
            Expect(first.Index.ByLocation.ContainsKey(1001), "the pack's mapping links location 1001 to its pin");
            string findingKey = first.Findings[0].Key;

            PopTracker.PackFixes.Edit(key, "self-test: ignore a finding", f => f.Ignored.Add(findingKey));
            var again = await Task.Run(() => PopTracker.PackDoctor.Analyze(snapshot));
            Expect(!again.Findings.First(f => f.Key == findingKey).Ignored, "an analysis reads its snapshot, not the fixes as they change");
            var fresh = await Task.Run(() => PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(pack, names)));
            Expect(fresh.Findings.First(f => f.Key == findingKey).Ignored, "a new snapshot sees the edit");
        }

        /// <summary>Settings saved from a background thread are written on the main thread instead, and the log says so.</summary>
        private static async Task OffThreadSavesMoveToTheMainThread()
        {
            string path = Path.Combine(DataManager.GetDataDirectory(), "settings.json");
            var warnings = new List<string>();
            void OnLog(string line, string level) { if (level == "WARN") lock (warnings) warnings.Add(line); }
            Logger.OnLogMessage += OnLog;
            try
            {
                var settings = new AppSettings { MainSplitOffset = 777 };
                await Task.Run(() => DataManager.SaveSettings(settings));
                await Task.Delay(200);
                Expect(Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path))?.MainSplitOffset == 777, "the save still happens");
                lock (warnings) Expect(warnings.Any(w => w.Contains("background thread")), "the log says a save came from a background thread");
            }
            finally
            {
                Logger.OnLogMessage -= OnLog;
            }
        }

        /// <summary>SafeFile.Delete removes the backup and any temp file too, so a deleted file doesn't come back on the next read.</summary>
        private static void DeletedFilesStayDeleted()
        {
            string path = Scratch("gone.json");
            SafeFile.WriteJson(path, new Sample { Version = 1 });
            SafeFile.WriteJson(path, new Sample { Version = 2 });
            File.WriteAllText(path + ".tmp", "left by an interrupted save");
            Expect(File.Exists(path + ".bak"), "setup: a backup exists");
            SafeFile.Delete(path);
            Expect(!File.Exists(path) && !File.Exists(path + ".bak") && !File.Exists(path + ".tmp"), "the file, its backup and its temp file are all gone");
            Expect(SafeFile.ReadJson(path, () => new Sample { Version = -1 }).Version == -1, "a deleted file doesn't come back from its backup");
            SafeFile.Delete(path); // deleting what's already gone is fine
        }
    }
}
