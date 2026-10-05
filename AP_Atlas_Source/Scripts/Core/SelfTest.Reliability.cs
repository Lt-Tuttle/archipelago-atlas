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
        /// Map packs: reading one decodes none of its images (they're most of a pack's memory); using it decodes them once
        /// for every user; they're kept while it's used and for the pack released last, and freed (textures too) when
        /// another pack is released after it. The Pack Doctor learns which images decode, and their sizes, without keeping
        /// them, and finds the same whether or not the images are decoded.
        /// </summary>
        private static void PackImagesOnlyWhileUsed()
        {
            string first = Scratch("selftest_images_a.zip"), second = Scratch("selftest_images_b.zip"), third = Scratch("selftest_images_c.zip");
            AP_Atlas.Core.Testing.FakeMapPack.Write(first, "Self-test pack A", "Self Test Game A");
            AP_Atlas.Core.Testing.FakeMapPack.Write(second, "Self-test pack B", "Self Test Game B");
            AP_Atlas.Core.Testing.FakeMapPack.Write(third, "Self-test pack C", "Self Test Game C");
            long decoded = PopTracker.PackImages.Decoded;

            // Reading: the structure, no images.
            var pack = PopTracker.PopTrackerPackLoader.InspectZipPack(first) ?? throw new InvalidOperationException("the test pack wasn't read");
            Expect(pack.Maps.Count == 2 && pack.ItemsByCode.ContainsKey("sword") && pack.ItemsByCode.ContainsKey("shield"), "the test pack was read wrong");
            Expect(PopTracker.PackImages.Decoded == decoded && !pack.ImagesLoaded && pack.Images.Count == 0, "reading a pack decoded its images");
            Expect(pack.FindImage("images/sword.png") == null && pack.Maps["World"].Background == null, "a pack nobody uses has images to show");

            // The Pack Doctor's check: which images decode, and their sizes, without textures.
            var doctored = PopTracker.PopTrackerPackLoader.InspectZipPack(third) ?? throw new InvalidOperationException("the third test pack wasn't read");
            PopTracker.PackImages.Check(doctored);
            Expect(PopTracker.PackImages.Decoded == decoded && !doctored.ImagesLoaded, "checking a pack's images kept them");
            Expect(doctored.ImagesChecked && doctored.BrokenImages.Contains("images/broken.png"), "the image that can't be decoded wasn't found");
            Expect(doctored.HasDecodableImage("images/sword.png") && doctored.HasDecodableImage("/images/sword") && !doctored.HasDecodableImage("images/broken.png") && !doctored.HasDecodableImage("images/none.png"),
                "which images decode is wrong");
            Expect(doctored.HasMapBackground(doctored.Maps["World"]) && !doctored.HasMapBackground(doctored.Maps["Broken"]), "which maps have a background is wrong");
            Expect(doctored.ImageSize("images/world.png") == new Godot.Vector2I(AP_Atlas.Core.Testing.FakeMapPack.MapWidth, AP_Atlas.Core.Testing.FakeMapPack.MapHeight), "the map image's size wasn't learnt");
            // The Pack Doctor, and the stamps that tell a fix whether the author changed its subject, are the same with or
            // without the images decoded (a stamp that changed with them would set a user's fix aside on its own).
            string[] stamped = { "map:World", "map:Broken" };
            var without = PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(doctored, null)).Findings.Select(f => f.Key).OrderBy(k => k).ToList();
            var stampsWithout = stamped.Select(subject => PopTracker.PackFixes.AuthorStamp(doctored, subject)).ToList();
            List<string> with, stampsWith;
            using (PopTracker.PackImages.Use(doctored))
            {
                with = PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(doctored, null)).Findings.Select(f => f.Key).OrderBy(k => k).ToList();
                stampsWith = stamped.Select(subject => PopTracker.PackFixes.AuthorStamp(doctored, subject)).ToList();
            }
            Expect(without.SequenceEqual(with), $"the Pack Doctor finds [{string.Join(", ", without)}] without the images decoded, [{string.Join(", ", with)}] with them");
            Expect(with.Contains("map:nobg:Broken") && with.Contains("map:outside:World") && with.Contains("tile:noimage:shield") &&
                   !with.Contains("map:nobg:World") && !with.Contains("tile:noimage:sword"), $"the Pack Doctor's findings are wrong: {string.Join(", ", with)}");
            Expect(stampsWithout.SequenceEqual(stampsWith) && stampsWith[0] == "images/world.png|ok" && stampsWith[1] == "images/broken.png|missing",
                $"the maps' stamps are [{string.Join(", ", stampsWithout)}] without the images decoded, [{string.Join(", ", stampsWith)}] with them");

            // Using: decoded once for two users, and kept while used.
            decoded = PopTracker.PackImages.Decoded;
            var a = PopTracker.PackImages.Use(pack);
            var b = PopTracker.PackImages.Use(pack);
            Expect(PopTracker.PackImages.Decoded - decoded == 2 && PopTracker.PackImages.UsersOf(pack) == 2, $"two uses of a pack decoded {PopTracker.PackImages.Decoded - decoded} images, not its 2 once");
            var sword = pack.FindImage("images/sword.png");
            Expect(sword != null && pack.Maps["World"].Background != null && pack.Maps["Broken"].Background == null, "a used pack's images aren't there");
            a.Dispose();
            a.Dispose(); // twice is harmless
            Expect(PopTracker.PackImages.UsersOf(pack) == 1 && pack.ImagesLoaded, "a pack stopped being used while still used");
            b.Dispose();
            Expect(pack.ImagesLoaded && Godot.GodotObject.IsInstanceValid(sword), "the pack released last didn't keep its images");

            // Another pack released after it: the first is freed, its textures at once.
            using (PopTracker.PackImages.Use(PopTracker.PopTrackerPackLoader.InspectZipPack(second) ?? throw new InvalidOperationException("the second test pack wasn't read"))) { }
            Expect(!pack.ImagesLoaded && pack.Images.Count == 0 && pack.Maps["World"].BackgroundTexture == null, "a pack released before another kept its images");
            Expect(!Godot.GodotObject.IsInstanceValid(sword), "a freed pack's textures weren't freed");

            // Used again: decoded again.
            decoded = PopTracker.PackImages.Decoded;
            using (PopTracker.PackImages.Use(pack))
                Expect(PopTracker.PackImages.Decoded - decoded == 2 && pack.FindImage("images/sword.png") != null, "a freed pack wasn't decoded again when used again");
        }

        /// <summary>
        /// The Pack Doctor, checking a pack nobody uses (one picked in the Map Packs tab, or just installed), learns which of
        /// its images don't decode first, so its findings are those of a decoded pack, and keeps none of them.
        /// </summary>
        private static async Task PackDoctorChecksUnusedPacksImages()
        {
            string zip = Scratch("selftest_images_d.zip");
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test pack D", "Self Test Game D");
            var pack = PopTracker.PopTrackerPackLoader.InspectZipPack(zip) ?? throw new InvalidOperationException("the test pack wasn't read");
            long decoded = PopTracker.PackImages.Decoded;
            var report = await PopTracker.PackDoctorService.CheckAsync(pack, prompt: false) ?? throw new InvalidOperationException("the Pack Doctor didn't check the pack");
            var keys = report.Findings.Select(f => f.Key).ToList();
            Expect(keys.Contains("map:nobg:Broken") && keys.Contains("tile:noimage:shield") && !keys.Contains("map:nobg:World") && !keys.Contains("tile:noimage:sword"),
                $"the Pack Doctor's findings for a pack nobody uses are wrong: {string.Join(", ", keys)}");
            Expect(PopTracker.PackImages.Decoded == decoded && !pack.ImagesLoaded, "checking a pack nobody uses kept its images");
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
