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
        /// A slot's pack scripts start on a background queue, and the main thread hears they're running only later. Items
        /// that arrive meanwhile are queued behind the start: they must reach the scripts (they used to be dropped, so Key
        /// Items showed less than the slot had). Here five items arrive right after the start, and the main thread doesn't
        /// hear the scripts started until the test lets it.
        /// </summary>
        private static async Task PackScriptsGetItemsThatArriveWhileTheyStart()
        {
            string zip = Scratch("selftest_scripts.zip");
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test scripted pack", "Self Test Game S", initLua: """
                Archipelago:AddItemHandler("count swords", function(index, item_id, item_name, player_number)
                    local sword = Tracker:FindObjectForCode("sword")
                    sword.AcquiredCount = sword.AcquiredCount + 1
                end)
                """);
            var pack = PopTracker.PopTrackerPackLoader.InspectZipPack(zip) ?? throw new InvalidOperationException("the test pack wasn't read");
            var mainThread = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            string? warned = null;
            var runner = new PopTracker.PackScriptRunner(mainThread.Enqueue, message => warned = message);
            PopTracker.PackScriptHost? started = null;
            runner.Start(pack, 1, 0, new Newtonsoft.Json.Linq.JObject(), Array.Empty<(long, string, int)>(), Array.Empty<(long, string)>(), (host, _) => started = host);
            runner.FeedItems(Enumerable.Range(0, 5).Select(i => (i, 1000L, "Sword", 1)).ToList());
            for (int i = 0; i < 250 && !runner.Idle; i++) await Task.Delay(20);
            Expect(runner.Idle, "the pack's scripts never finished their work");
            Expect(started == null, "the main thread heard the scripts started before the test let it");
            while (mainThread.TryDequeue(out var work)) work();
            Expect(warned == null, "a piece of script work failed: " + warned);
            Expect(started != null && started.Errors.Count == 0, "the scripts didn't start cleanly: " + string.Join("; ", started?.Errors ?? new List<string>()));
            int count = started?.StateOf("sword")?.Count ?? -1;
            Expect(count == 5, $"the scripts saw {count} of the 5 items that arrived while they started");
        }

        /// <summary>
        /// A pack's scripts run under limits. Each way a piece of their work can run away is stopped and says why: a loop
        /// (alone, in pcall, in a sort's comparison), recursion through Atlas's own functions (which would end Atlas: .NET
        /// can't catch a stack overflow), through a sort (which drops errors that aren't Lua's) or in Lua alone, memory
        /// (growing, or asked for at once by string.rep or table.concat), and time. The scripts stay stopped: later items do
        /// nothing and their tiles fall back to the pack's item list. A slot's runner says why once, on the main thread, the
        /// Pack Doctor reports it, and the limits as they ship stop a loop in seconds.
        /// </summary>
        private static async Task PackScriptsThatRunAwayAreStopped()
        {
            string zip = Scratch("selftest_runaway.zip");
            void WritePack(string gem) => AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test runaway pack", "Self Test Game R", initLua: $$"""
                Archipelago:AddItemHandler("test", function(index, item_id, item_name, player_number)
                    if item_id == 1002 then error("ran after the stop") end
                    if item_id == 1001 then
                        {{gem}}
                    end
                    local sword = Tracker:FindObjectForCode("sword")
                    sword.AcquiredCount = sword.AcquiredCount + 1
                end)
                """, files: new Dictionary<string, string>
            {
                ["scripts/again.lua"] = "ScriptHost:LoadScript('scripts/again.lua')",
                ["scripts/itself.lua"] = "require('scripts.itself')",
                ["scripts/deep.lua"] = "local function f() return 1 + f() end f()",
            });
            var small = new PopTracker.PackScriptHost.ScriptLimits { Steps = 2_000_000, Memory = 64L << 20, Time = TimeSpan.FromSeconds(30) };
            var cases = new (string Name, string Gem, string Stop, PopTracker.PackScriptHost.ScriptLimits Limits)[]
            {
                ("a loop", "while true do end", "stuck in a loop", small),
                ("a loop in pcall", "pcall(function() while true do end end)", "stuck in a loop", small),
                ("a loop in a sort's comparison", "table.sort({ 3, 1, 2 }, function(a, b) while true do end end)", "stuck in a loop", small),
                ("recursion through Atlas's own functions", "ScriptHost:LoadScript('scripts/again.lua')", "called itself too deeply", small),
                ("recursion through a sort", "local function f() table.sort({ 2, 1 }, function(a, b) f() return a < b end) end f()", "called itself too deeply", small),
                ("recursion in Lua", "local function f() return 1 + f() end f()", "called itself too deeply", small),
                ("recursion in Lua that Atlas's functions run", "ScriptHost:LoadScript('scripts/deep.lua')", "called itself too deeply", small),
                ("recursion in Lua that a library function runs", "string.gsub('a', 'a', function() local function r() return 1 + r() end return r() end)", "called itself too deeply", small),
                ("a module that requires itself", "require('scripts.itself')", "called itself too deeply", small),
                ("growing memory", "local t, n = {}, 0 while true do n = n + 1 t[n] = { n } end", "used over 64 MB of memory",
                    new PopTracker.PackScriptHost.ScriptLimits { Steps = 20_000_000, Memory = 64L << 20, Time = TimeSpan.FromSeconds(30) }),
                ("one huge string", "local s = string.rep('x', 100000000)", "asked for over 64 MB of memory at once", small),
                ("a huge string from a table", "local s = string.rep('x', 1000000) local t = {} for i = 1, 100 do t[i] = s end local all = table.concat(t)",
                    "asked for over 64 MB of memory at once", small),
                ("time", "while true do end", "took over 0.3 seconds",
                    new PopTracker.PackScriptHost.ScriptLimits { Steps = 1_000_000_000, Memory = 64L << 20, Time = TimeSpan.FromMilliseconds(300) }),
            };
            foreach (var c in cases)
            {
                WritePack(c.Gem);
                var host = PopTracker.PackScriptHost.Load(PopTracker.PopTrackerPackLoader.InspectZipPack(zip) ?? throw new InvalidOperationException("the test pack wasn't read"));
                host.Limits = c.Limits;
                host.InitLimits = c.Limits;
                int before = -1;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await Task.Run(() =>
                {
                    host.Initialize();
                    host.Clear(1, 0, new Newtonsoft.Json.Linq.JObject());
                    host.ApplyItem(0, 1000, "Sword", 1);
                    before = host.StateOf("sword")?.Count ?? -1;
                    host.ApplyItem(1, 1001, "Cursed Gem", 1);
                    host.ApplyItem(2, 1000, "Sword", 1);
                    host.ApplyItem(3, 1002, "Broken Item", 1);
                });
                Expect(before == 1, $"{c.Name}: the scripts didn't count the sword before running away ({before})");
                Expect(host.StopReason?.StartsWith("the item handler (for Cursed Gem) ") == true && host.StopReason.Contains(c.Stop), $"{c.Name}: {host.StopReason ?? "not stopped"}");
                Expect(host.StateOf("sword") == null && host.Settings(new[] { "sword" }).Count == 0, $"{c.Name}: a stopped pack's tiles still came from its scripts");
                Expect(!host.Errors.Any(e => e.Contains("ran after the stop")), $"{c.Name}: stopped scripts still ran later items");
                Expect(clock.Elapsed < TimeSpan.FromSeconds(20), $"{c.Name}: stopping took {clock.Elapsed.TotalSeconds:0.0} s");
            }

            // init.lua's own work: Initialize says the scripts didn't start.
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test runaway pack", "Self Test Game R", initLua: "while true do end");
            var init = PopTracker.PackScriptHost.Load(PopTracker.PopTrackerPackLoader.InspectZipPack(zip)!);
            init.Limits = small;
            init.InitLimits = small;
            bool started = await Task.Run(() => init.Initialize());
            Expect(!started && init.StopReason?.StartsWith("init.lua was still running after ") == true, $"init.lua's loop: {init.StopReason ?? "not stopped"}");
            // The Pack Doctor says so (its scripts run with the limits as they ship).
            var report = await Task.Run(() => PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(PopTracker.PopTrackerPackLoader.InspectZipPack(zip)!, null)));
            Expect(!report.ScriptsRan && report.Findings.Any(f => f.Key == "script:stopped" && f.Detail.Contains("Stopped because init.lua was still running after")),
                "the Pack Doctor doesn't report the stopped scripts: " + string.Join("; ", report.Findings.Where(f => f.Category == "Scripts").Select(f => f.Title)));

            // A slot's runner, with the limits as they ship: stopped in seconds, said once (a second runaway item does nothing).
            WritePack("while true do end");
            var pack = PopTracker.PopTrackerPackLoader.InspectZipPack(zip)!;
            var mainThread = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            var stops = new List<string>();
            var runner = new PopTracker.PackScriptRunner(mainThread.Enqueue, _ => { }, stops.Add);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            runner.Start(pack, 1, 0, new Newtonsoft.Json.Linq.JObject(), Array.Empty<(long, string, int)>(), Array.Empty<(long, string)>(), (_, _) => { });
            runner.FeedItems(new List<(int, long, string, int)> { (0, 1001L, "Cursed Gem", 1) });
            runner.FeedItems(new List<(int, long, string, int)> { (1, 1001L, "Cursed Gem", 1) });
            for (int i = 0; i < 1500 && !runner.Idle; i++) await Task.Delay(20);
            Expect(runner.Idle, "the runner's scripts were never stopped");
            while (mainThread.TryDequeue(out var work)) work();
            Expect(stops.Count == 1 && stops[0].Contains("stuck in a loop"), $"the runner said: {string.Join(" | ", stops)}");
            Print($"  The limits as they ship stopped a loop in {timer.Elapsed.TotalSeconds:0.0} s.");
        }

        /// <summary>
        /// The pack's scripts are compiled once, before the limits' watchdog is attached (with it attached, each compile writes
        /// out all the code loaded so far): require and LoadScript use them, and a script that doesn't compile or isn't there
        /// is an error init.lua carries on past (even through require, in pcall). A Lua error isn't a stop, nor is a failure
        /// in one of Atlas's own functions, or a .NET failure of MoonSharp's library on an odd argument: both are Lua errors at
        /// the script's call, as in Lua. Library functions that could hurt Atlas in one step are safe: collectgarbage
        /// collects nothing, and the json and dynamic modules aren't there.
        /// </summary>
        private static void KeyItemsLayoutsAreKeptApart()
        {
            string zip = Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "selftest_layouts.zip");
            try
            {
                // Three roots: default and vertical share one grid; horizontal has its own, under a group header.
                AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test layouts pack", "Self Test Game L", layoutsJson:
                    """{"tracker_default":{"type":"itemgrid","rows":[["sword","shield"]]},"tracker_vertical":{"type":"itemgrid","rows":[["sword","shield"]]}""" +
                    ""","tracker_horizontal":{"type":"group","header":"Gear","content":{"type":"itemgrid","rows":[["sword"],["shield"]]}}}""");
                var pack = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.LoadPackForGame("Self Test Game L");
                Expect(pack != null && pack.LayoutGrids.Keys.OrderBy(k => k).SequenceEqual(new[] { "tracker_default", "tracker_horizontal", "tracker_vertical" }),
                    "the pack's root layouts weren't kept apart: " + string.Join(", ", pack?.LayoutGrids.Keys ?? Enumerable.Empty<string>()));
                if (pack == null) return;
                Expect(pack.ItemGridGroups.Count == 2 && ReferenceEquals(pack.LayoutGrids["tracker_default"][0], pack.LayoutGrids["tracker_vertical"][0]),
                    $"a grid two roots share isn't one grid ({pack.ItemGridGroups.Count} merged grids)");
                Expect(pack.LayoutGrids["tracker_horizontal"].Count == 1 && pack.LayoutGrids["tracker_horizontal"][0].Header == "Gear" && pack.LayoutGrids["tracker_horizontal"][0].Rows.Count == 2,
                    "the horizontal root's own grid wasn't kept with its header and rows");
                // The built layouts' blocks: consecutive grids under one header (else layout key) make a block.
                var blocks = AP_Atlas.Core.PopTracker.KeyItemsLayouts.Blocks(new[]
                {
                    new AP_Atlas.Core.PopTracker.PackItemGrid { Header = "Gear", Rows = { new List<string> { "a" } } },
                    new AP_Atlas.Core.PopTracker.PackItemGrid { Header = "Gear", Rows = { new List<string> { "b" } } },
                    new AP_Atlas.Core.PopTracker.PackItemGrid { LayoutKey = "keys_grid", Rows = { new List<string> { "c" } } },
                    new AP_Atlas.Core.PopTracker.PackItemGrid { Rows = { new List<string> { "d" } } }
                });
                Expect(blocks.Select(b => b.Header).SequenceEqual(new[] { "Gear", "keys_grid", "Items" }) && blocks[0].Rows.Count == 2, "the blocks don't follow the headers: " + string.Join(", ", blocks.Select(b => b.Header)));
                var byCategory = AP_Atlas.Core.PopTracker.KeyItemsLayouts.BlocksByCategory(Enumerable.Range(0, 8).Select(i => ($"i{i}", i < 7 ? "Key" : "Ring")), perRow: 3);
                Expect(byCategory.Count == 2 && byCategory[0].Header == "Key" && byCategory[0].Rows.Count == 3 && byCategory[0].Rows[2].Count == 1 && byCategory[1].Rows.Count == 1,
                    "the blocks by category aren't in rows of three");
                Expect(AP_Atlas.Core.PopTracker.KeyItemsLayouts.NameOf("tracker_default") == "Default (pack)" && AP_Atlas.Core.PopTracker.KeyItemsLayouts.NameOf(AP_Atlas.Core.PopTracker.KeyItemsLayouts.Horizontal) == "Horizontal (by group)",
                    "a layout's name is wrong");
            }
            finally
            {
                SafeFile.Delete(zip);
            }
        }

        private static void PackVariantsAreReadApart()
        {
            string zip = Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "selftest_variants.zip");
            try
            {
                AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test variant pack", "Self Test Game V",
                    files: new Dictionary<string, string> { ["var_b/items/items.json"] = """[{"name":"Lantern","type":"toggle","img":"images/sword.png","codes":"lantern"}]""" },
                    variantsJson: """{"standard":{"display_name":"Standard"},"var_b":{"display_name":"Variant B"}}""");
                var standard = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.LoadPackForGame("Self Test Game V");
                Expect(standard != null && standard.Variant == "standard" && standard.VariantKey == null && standard.ItemsByCode.ContainsKey("sword") && !standard.ItemsByCode.ContainsKey("lantern"),
                    "the default read didn't take the base items");
                var variants = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.VariantsOf(standard?.Manifest);
                Expect(variants.Count == 2 && variants.Select(v => v.Id).SequenceEqual(new[] { "standard", "var_b" }) && variants[1].Name == "Variant B", "the pack's variants weren't listed, default first, with their names");
                var b = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.LoadPackForGame("Self Test Game V", "var_b", null);
                Expect(b != null && b.Variant == "var_b" && b.VariantKey == "var_b" && b.ItemsByCode.ContainsKey("lantern") && !b.ItemsByCode.ContainsKey("sword"),
                    "the variant read didn't take the variant's own items in place of the base ones");
                Expect(!ReferenceEquals(standard, b) && ReferenceEquals(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.LoadPackForGame("Self Test Game V", "var_b", null), b)
                    && ReferenceEquals(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.LoadPackForGame("Self Test Game V"), standard), "a pack and its variant aren't each kept once");
                Expect(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.IsCurrent(b) && AP_Atlas.Core.PopTracker.PopTrackerPackLoader.IsCurrent(standard), "a variant read isn't current");
                var unknown = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.LoadPackForGame("Self Test Game V", "no_such", null);
                Expect(ReferenceEquals(unknown, standard), "a variant the manifest doesn't list isn't read as the default");
            }
            finally
            {
                SafeFile.Delete(zip);
            }
        }

        private static async Task PackScriptsReadTheRoomAndFollowTheMap()
        {
            string zip = Scratch("selftest_follow.zip");
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test following pack", "Self Test Game F", initLua: AP_Atlas.Core.Testing.FakeMapPack.FollowingInitLua,
                layoutsJson: AP_Atlas.Core.Testing.FakeMapPack.TwoTabLayout);
            var pack = PopTracker.PopTrackerPackLoader.InspectZipPack(zip) ?? throw new InvalidOperationException("the test pack wasn't read");
            Expect(pack.TabMaps.TryGetValue("Elsewhere", out var elsewhere) && elsewhere.SequenceEqual(new[] { "Broken" })
                && pack.TabMaps.TryGetValue("Overworld", out var overworld) && overworld.SequenceEqual(new[] { "World" }),
                $"the layout's tabs weren't read: {string.Join("; ", pack.TabMaps.Select(t => t.Key + " = " + string.Join(",", t.Value)))}");
            var host = PopTracker.PackScriptHost.Load(pack);
            Expect(host != null && host.FollowsMaps && !host.ReadsGameMemory, "the pack wasn't seen to follow the game's map");
            await Task.Run(() =>
            {
                host!.Initialize();
                host.Clear(1, 0, new Newtonsoft.Json.Linq.JObject());
            });
            Expect(host!.Errors.Count == 0 && !host.Stopped, "the scripts didn't run cleanly: " + string.Join("; ", host.Errors));
            var outbox = host.TakeOutbox();
            Expect(outbox.Watch.SequenceEqual(new[] { "atlas_test_map" }) && outbox.Get.SequenceEqual(new[] { "atlas_test_map" }) && outbox.Hints.Count == 0,
                $"the scripts' requests were {string.Join(",", outbox.Watch)} / {string.Join(",", outbox.Get)} / {outbox.Hints.Count} hints");
            Expect(host.IgnoredWrites.SetEquals(new[] { "atlas_test_written" }) && host.TakeOutbox().IsEmpty, "a write to the room's data storage wasn't refused, or went out");
            Expect(host.LoadedFiles.Contains(("AddMaps", "maps/maps.json")) && host.LoadedFiles.Count == 4, $"the files init.lua loads weren't noted: {string.Join(", ", host.LoadedFiles)}");
            await Task.Run(() => host.ApplyRetrieved("atlas_test_map", "Elsewhere"));
            Expect(host.TakeOutbox().Hints.SequenceEqual(new[] { ("ActivateTab", "Elsewhere") }), "the value read didn't move the map");
            await Task.Run(() => host.ApplySetReply("atlas_test_map", "Overworld", "Elsewhere"));
            Expect(host.TakeOutbox().Hints.SequenceEqual(new[] { ("ActivateTab", "Overworld") }), "a change to the key didn't move the map");
            // A second SetNotify for the same key isn't sent again.
            await Task.Run(() => host.Clear(1, 0, new Newtonsoft.Json.Linq.JObject()));
            outbox = host.TakeOutbox();
            Expect(outbox.Watch.Count == 0 && outbox.Get.SequenceEqual(new[] { "atlas_test_map" }), "the key was asked about again, or not read again");
            var report = await Task.Run(() => PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(pack, null)));
            Expect(report.Findings.Any(f => f.Key == "script:follows-maps") && report.Findings.Any(f => f.Key == "script:writes"), "the Pack Doctor doesn't report live map following or the ignored write");
        }

        private static async Task PackScriptsReadTheRoomAndTheEscapes()
        {
            string zip = Scratch("selftest_room.zip");
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test room pack", "Self Test Game R", initLua: """
                local function count()
                    local sword = Tracker:FindObjectForCode("sword")
                    sword.AcquiredCount = #Archipelago.CheckedLocations * 100 + #Archipelago.MissingLocations
                end
                Archipelago:AddClearHandler("room", function(slot_data)
                    count()
                    Tracker:FindObjectForCode("shield").Active = Archipelago.Seed == "seed-xyz" and Archipelago.ClientStatus.GOAL == 30 and UMLAUT == "Siegbräu"
                        and Archipelago:GetPlayerAlias(1) == "Tess" and Archipelago.GetPlayerName(1) == "Tester" and Archipelago:GetPlayerGame(2) == "Other Game" and Archipelago:GetPlayerAlias(9) == nil
                end)
                Archipelago:AddLocationHandler("room", function(id, name) count() end)
                UMLAUT = "Siegbr\u{00e4}u"
                """, files: new Dictionary<string, string>
            {
                ["scripts/autotracking/location_mapping.lua"] = "LOCATION_MAPPING = {\n    [100124] = \"@A/Siegbr\\u{00e4}u\",\n    [100125] = \"@B/\\x41\\66\\z   C\",\n}\n"
            });
            var pack = PopTracker.PopTrackerPackLoader.InspectZipPack(zip) ?? throw new InvalidOperationException("the test pack wasn't read");
            Expect(pack.LocationMappingById.TryGetValue(100124, out var umlaut) && umlaut.Contains("@A/Siegbräu") && pack.LocationMappingById.TryGetValue(100125, out var escapes) && escapes.Contains("@B/ABC"),
                $"the mapping's escapes weren't read: {string.Join(" | ", pack.LocationMappingById.Select(kv => kv.Key + "=" + string.Join(",", kv.Value)))}");
            var host = PopTracker.PackScriptHost.Load(pack) ?? throw new InvalidOperationException("the pack has no scripts");
            await Task.Run(() =>
            {
                host.Initialize();
                host.SetRoom(new long[] { 1, 2 }, new long[] { 3, 4, 5 }, "seed-xyz");
                host.SetPlayers(new[] { (1, "Tester", "Tess", "Self Test Game R"), (2, "Other", "", "Other Game") });
                host.Clear(1, 0, new Newtonsoft.Json.Linq.JObject());
            });
            Expect(host.Errors.Count == 0 && !host.Stopped, "the scripts didn't run cleanly: " + string.Join("; ", host.Errors));
            Expect(host.StateOf("sword")?.Count == 203 && host.StateOf("shield")?.Active == true,
                $"the room's tables, seed, players or the escaped text weren't as the scripts expect: sword {host.StateOf("sword")?.Count}, shield {host.StateOf("shield")?.Active}");
            await Task.Run(() => host.ApplyLocation(3, "Three"));
            Expect(host.StateOf("sword")?.Count == 302, $"a check didn't move from the missing ids to the checked: sword {host.StateOf("sword")?.Count}");
            Expect(host.UnsupportedApis.Count == 0, "reading the room's tables counted as an unsupported call: " + string.Join(", ", host.UnsupportedApis));
            var names = new Dictionary<long, string> { [1] = "FS: Soul Arrow - shop", [2] = "FS: Soul Arrow - Yoel/Yuria shop", [3] = "FS: Soul Arrow - Orbeck" };
            var best = PopTracker.PackDoctor.Suggest("FS: Soul Arrow - Yoel or Yuria shop", names, 3);
            Expect(best.Count == 3 && best[0].Id == 2 && best[0].Score >= 0.999, $"\"Yoel or Yuria\" and \"Yoel/Yuria\" aren't the same name, or the detail didn't decide between the copies: {string.Join("; ", best.Select(s => s.Label + " " + s.Score.ToString("0.00")))}");
            var sirris = PopTracker.PackDoctor.Suggest("US: Sunset Helm - Pit of Hollows after killing Hodrick with Sirris", new Dictionary<long, string> { [4] = "US: Sunset Helm - Pit of Hollows after killing Hodrick w/Sirris" }, 1);
            Expect(sirris.Count == 1 && sirris[0].Score >= 0.999, $"\"with Sirris\" and \"w/Sirris\" aren't the same name: {(sirris.Count > 0 ? sirris[0].Score : 0):0.00}");
            // The only location of its kind in its area links by itself, whatever its detail says; one of several doesn't.
            var game = new PopTracker.GameNameTable { Game = "Self Test Game R", Locations = new Dictionary<string, long> { ["FS: Firelink Armor - shop after placing all Cinders"] = 10, ["FS: Soul Arrow - shop"] = 1, ["FS: Soul Arrow - Orbeck"] = 3 } };
            var armor = new PopTracker.Finding { Key = "loc:unmatched:A|Armor", Subject = "link:A|Armor", Actions = PopTracker.FindingActions.LinkLocation, Query = "FS: Firelink Armor - shop after beating KFF boss", Suggestions = PopTracker.PackDoctor.Suggest("FS: Firelink Armor - shop after beating KFF boss", game.LocationsById(), 3) };
            var arrow = new PopTracker.Finding { Key = "loc:unmatched:A|Arrow", Subject = "link:A|Arrow", Actions = PopTracker.FindingActions.LinkLocation, Query = "FS: Soul Arrow - Yoel or Yuria shop", Suggestions = PopTracker.PackDoctor.Suggest("FS: Soul Arrow - Yoel or Yuria shop", game.LocationsById(), 3) };
            var report = new PopTracker.DoctorReport { Names = game, Findings = { armor, arrow } };
            var sure = PopTracker.PackDoctor.ExactMatches(report);
            Expect(sure.Count == 1 && sure[0] == armor && armor.Suggestions[0].Id == 10 && armor.Suggestions[0].Reason == "the only 'Firelink Armor' in FS",
                $"the only Firelink Armor in FS wasn't taken as sure (or Soul Arrow was): {string.Join(", ", sure.Select(f => f.Key))}; reason {armor.Suggestions[0].Reason}");
        }

        /// <summary>
        /// Packs show one check on several maps with a section that refers to another ("ref"), and some build their mapping
        /// as their scripts run (Dark Souls II numbers a list of paths in a loop; Symphony of the Night adds its relics to
        /// ITEM_MAPPING in one). Both read as PopTracker reads them; a reference the pack can't resolve is an ordinary
        /// section, said for the author.
        /// </summary>
        private static async Task PackReferencesAndBuiltMappings()
        {
            string zip = Scratch("selftest_refs.zip");
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test refs pack", "Self Test Game F", initLua: """
                ScriptHost:LoadScript("scripts/autotracking/item_mapping.lua")
                ScriptHost:LoadScript("scripts/autotracking/location_mapping.lua")
                """, files: new Dictionary<string, string>
            {
                ["scripts/autotracking/location_mapping.lua"] = """
                    LOCATIONS = {
                        {"@Region/Cave/Chest"},
                        {"@Region/Cave/Shelf"},
                        {"@Overworld/Hut view/"},
                    }
                    LOCATION_MAPPING = {}
                    for n, item in ipairs(LOCATIONS) do
                        LOCATION_MAPPING[n] = item
                    end
                    """,
                ["scripts/autotracking/item_mapping.lua"] = """
                    BASE = 0x100
                    RELICS = { {"sword", "toggle"}, {"shield", "toggle"} }
                    ITEM_MAPPING = {}
                    for n, item in ipairs(RELICS) do
                        ITEM_MAPPING[BASE + n - 1] = item
                    end
                    """,
            }, locationsJson: """
                [{"name":"Region","children":[
                    {"name":"Cave","sections":[{"name":"Chest"},{"name":"Shelf"}],"map_locations":[{"map":"World","x":10,"y":10}]},
                    {"name":"Hut","sections":[{"name":""}],"map_locations":[{"map":"World","x":20,"y":10}]}]},
                 {"name":"Overworld","children":[
                    {"name":"Cave view","sections":[{"name":"Item","ref":"Region/Cave/Chest"},{"ref":"Region/Cave/Shelf"},{"name":"Gone","ref":"Region/Nowhere/Thing"}],"map_locations":[{"map":"World","x":30,"y":10}]},
                    {"name":"Hut view","sections":[{"ref":"Region/Hut/"}],"map_locations":[{"map":"World","x":40,"y":10}]}]}]
                """);
            var pack = await Task.Run(() => PopTracker.PopTrackerPackLoader.InspectZipPack(zip)) ?? throw new InvalidOperationException("the test pack wasn't read");
            string Mapping(Dictionary<long, List<string>> m) => string.Join(" | ", m.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + string.Join(",", kv.Value)));
            Expect(Mapping(pack.LocationMappingById) == "1=@Region/Cave/Chest | 2=@Region/Cave/Shelf | 3=@Overworld/Hut view/" && pack.UnkeyedLocationPaths.Count == 0,
                $"the location mapping the scripts build wasn't read: {Mapping(pack.LocationMappingById)}; unkeyed {pack.UnkeyedLocationPaths.Count}");
            Expect(Mapping(pack.ItemMapping) == "256=sword | 257=shield", $"the item mapping the scripts build wasn't read: {Mapping(pack.ItemMapping)}");
            Expect(pack.MappingsBuiltByScripts.SequenceEqual(new[] { "locations", "items" }) && !pack.LoadIssues.Any(i => i.Contains("mapping.lua")),
                $"the built mappings weren't noted, or the files' issues stayed: {string.Join(", ", pack.MappingsBuiltByScripts)}; {string.Join(" | ", pack.LoadIssues)}");

            var names = new PopTracker.GameNameTable
            {
                Game = "Self Test Game F",
                Locations = new Dictionary<string, long> { ["Cave Chest"] = 1, ["Cave Shelf"] = 2, ["Hut"] = 3, ["Extra"] = 4 },
                Items = new Dictionary<string, long> { ["Sword"] = 256, ["Shield"] = 257 }
            };
            var index = new PopTracker.PackIndex(pack, names.LocationsById(), names.Items.ToDictionary(kv => kv.Value, kv => kv.Key));
            var cave = pack.Locations.Single(l => l.FullPath == "Region/Cave");
            var hut = pack.Locations.Single(l => l.FullPath == "Region/Hut");
            var caveView = pack.Locations.Single(l => l.FullPath == "Overworld/Cave view");
            var hutView = pack.Locations.Single(l => l.FullPath == "Overworld/Hut view");
            string Shown(long id) => index.ByLocation.TryGetValue(id, out var m) ? string.Join(", ", m.Select(x => x.Pin.Name + "/" + (x.Section?.Name ?? "-") + (x.Via != null ? "*" : ""))) : "";
            Expect(Shown(1) == "Cave/Chest, Cave view/Item*" && Shown(2) == "Cave/Shelf, Cave view/*" && Shown(3) == "Hut/, Hut view/*",
                $"a section referring to another doesn't show its check (a path landing on one stands for its target): 1 [{Shown(1)}] 2 [{Shown(2)}] 3 [{Shown(3)}]");
            Expect(index.ReferenceSections == 3 && index.UnresolvedReferences.Count == 1 && index.UnresolvedReferences[0].Section.Name == "Gone"
                && index.UnmatchedSections().Select(u => u.Pin.Name + "/" + u.Section?.Name).SequenceEqual(new[] { "Cave view/Gone" }),
                $"references weren't told apart: {index.ReferenceSections} resolved, {index.UnresolvedReferences.Count} not; unmatched {string.Join(", ", index.UnmatchedSections().Select(u => u.Pin.Name + "/" + u.Section?.Name))}");
            Expect(index.ItemIdsFor("sword").SequenceEqual(new long[] { 256 }), "the built item mapping didn't link the tile");
            // A fix moves the check, and the referring section with it.
            index.ClearSection(cave, cave.Sections[0]);
            index.Link(4, cave, cave.Sections[0]);
            Expect(Shown(1) == "" && Shown(4) == "Cave/Chest, Cave view/Item*", $"a fix didn't carry to the referring section: 1 [{Shown(1)}] 4 [{Shown(4)}]");
            index.Unlink(4, cave);
            Expect(Shown(4) == "", $"unlinking the section left its copy behind: [{Shown(4)}]");

            var report = await Task.Run(() => PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(pack, names)));
            Expect(report.SectionsTotal == 7 && report.SectionsLinked == 6, $"the Doctor counts {report.SectionsLinked} of {report.SectionsTotal} sections linked (6 of 7 expected: references count with their target)");
            Expect(report.Findings.Any(f => f.Key == "loc:refs" && f.Details.Count == 1) && !report.Findings.Any(f => f.Key == "locmap:dangling")
                && report.Findings.Where(f => f.Key.StartsWith("loc:unmatched:", StringComparison.Ordinal)).Select(f => f.Key).SequenceEqual(new[] { "loc:unmatched:Overworld/Cave view|Gone" }),
                "the Doctor didn't report the unresolved reference alone: " + string.Join(", ", report.Findings.Select(f => f.Key)));
            Expect(PopTracker.PackDoctor.AuthorReport(report).Contains("ref \"Region/Nowhere/Thing\""), "the author's report leaves out the reference the pack can't resolve");
            // That section is meant to show a check another section shows: choosing one isn't a clash, as it is for an ordinary section.
            PopTracker.Finding Choosing(string pinPath, string section) => new PopTracker.Finding
            {
                Key = "loc:unmatched:" + pinPath + "|" + section,
                Subject = "link:" + pinPath + "|" + section,
                Actions = PopTracker.FindingActions.LinkLocation,
                Query = section,
                Suggestions = { new PopTracker.Suggestion { Id = 2, Label = "Cave Shelf", Score = 0.9 } }
            };
            var copy = Choosing("Overworld/Cave view", "Gone");
            var ordinary = Choosing("Region/Hut", "");
            var clashes = PopTracker.PackDoctor.Conflicts(report, new List<PopTracker.Finding> { copy }, f => f.Suggestions[0]);
            var ordinaryClashes = PopTracker.PackDoctor.Conflicts(report, new List<PopTracker.Finding> { ordinary }, f => f.Suggestions[0]);
            Expect(clashes.Count == 0 && ordinaryClashes.ContainsKey(ordinary.Key)
                && report.Findings.First(f => f.Key == "loc:unmatched:Overworld/Cave view|Gone").Detail.Contains("Region/Nowhere/Thing"),
                $"a section referring to a missing one was treated as an ordinary one: {string.Join("; ", clashes.Values)} | ordinary {ordinaryClashes.Count}");

            // A slash between words reads as "or" and as a gap, so either way of writing it matches exactly.
            var pharros = PopTracker.PackDoctor.Suggest("Bastille: Parrying Dagger - Pharros - elevator room, metal chest", new Dictionary<long, string> { [30] = "Bastille: Parrying Dagger - Pharros/elevator room, metal chest" }, 1);
            Expect(pharros.Count == 1 && pharros[0].Score >= 0.999, $"\"Pharros - elevator room\" and \"Pharros/elevator room\" aren't the same name: {(pharros.Count > 0 ? pharros[0].Score : 0):0.000}");
            // Numbered copies: the only one with that number links by itself ("1" being the unnumbered first); a number the game hasn't doesn't.
            var game = new PopTracker.GameNameTable { Game = "Self Test Game F", Locations = new Dictionary<string, long> { ["Chariot: Fading Soul - boss arena"] = 20, ["Chariot: Fading Soul - boss arena #2"] = 21, ["Chariot: Fading Soul - boss arena #3"] = 22 } };
            PopTracker.Finding Row(string key, string query) => new PopTracker.Finding { Key = "loc:unmatched:A|" + key, Subject = "link:A|" + key, Actions = PopTracker.FindingActions.LinkLocation, Query = query, Suggestions = PopTracker.PackDoctor.Suggest(query, game.LocationsById(), 3) };
            var second = Row("2", "Chariot: Fading Soul 2 - boss arena");
            var first = Row("1", "Chariot: Fading Soul 1 - boss arena");
            var fourth = Row("4", "Chariot: Fading Soul 4 - boss arena");
            var sure = PopTracker.PackDoctor.ExactMatches(new PopTracker.DoctorReport { Names = game, Findings = { second, first, fourth } });
            Expect(sure.Contains(second) && second.Suggestions[0].Id == 21 && sure.Contains(first) && first.Suggestions[0].Id == 20 && !sure.Contains(fourth),
                $"numbered copies weren't linked by their number: {string.Join(", ", sure.Select(f => f.Key + "→" + f.Suggestions[0].Id))}");

            // Sections that host an item: PopTracker counts no check in one without an item_count (an entrance, a boss
            // toggle), and a location mapped to an item code shows in the section hosting that item (Super Mario 64's pack).
            string hosted = Scratch("selftest_hosted.zip");
            AP_Atlas.Core.Testing.FakeMapPack.Write(hosted, "Self-test hosted pack", "Self Test Game H", files: new Dictionary<string, string>
            {
                ["scripts/autotracking/location_mapping.lua"] = """
                    LOCATION_MAPPING = {
                        [7] = {"__location_item_7"},
                        [8] = {"@Castle/Throne/Chest"},
                    }
                    """
            }, locationsJson: """
                [{"name":"Castle","children":[
                    {"name":"Star","sections":[{"name":"First Star","hosted_item":"__location_item_7"}],"map_locations":[{"map":"World","x":10,"y":10}]},
                    {"name":"Gate","sections":[{"name":"To the Gate","hosted_item":"to_gate"},{"name":"Counted","hosted_item":"boss_key","item_count":1}],"map_locations":[{"map":"World","x":20,"y":10}]},
                    {"name":"Throne","sections":[{"name":"Chest"}],"map_locations":[{"map":"World","x":30,"y":10}]},
                    {"name":"Boss","sections":[{"name":"","hosted_item":"boss","item_count":0}],"map_locations":[{"map":"World","x":40,"y":10}]}]}]
                """);
            var hostedPack = await Task.Run(() => PopTracker.PopTrackerPackLoader.InspectZipPack(hosted)) ?? throw new InvalidOperationException("the hosted test pack wasn't read");
            var hostedNames = new PopTracker.GameNameTable { Game = "Self Test Game H", Locations = new Dictionary<string, long> { ["Star 1"] = 7, ["Throne Chest"] = 8, ["Gate Key"] = 9, ["Boss"] = 10 } };
            var hostedReport = await Task.Run(() => PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(hostedPack, hostedNames)));
            string HostedShown(long id) => hostedReport.Index.ByLocation.TryGetValue(id, out var m) ? string.Join(", ", m.Select(x => x.Pin.Name + "/" + x.Section?.Name)) : "";
            Expect(HostedShown(7) == "Star/First Star" && HostedShown(8) == "Throne/Chest", $"a location mapped to an item code isn't shown by the section hosting it: 7 [{HostedShown(7)}] 8 [{HostedShown(8)}]");
            // A toggle named like a location nothing else shows places it (placed is better than nowhere).
            Expect(HostedShown(10) == "Boss/", $"the boss toggle didn't place the game's \"Boss\": [{HostedShown(10)}]");
            Expect(hostedReport.SectionsTotal == 4 && hostedReport.SectionsLinked == 3 && hostedReport.Index.NoCheckSections == 1
                && hostedReport.Findings.Where(f => f.Key.StartsWith("loc:unmatched:", StringComparison.Ordinal)).Select(f => f.Key).SequenceEqual(new[] { "loc:unmatched:Castle/Gate|Counted" })
                && hostedReport.Findings.Any(f => f.Key == "loc:no-check"),
                $"a section without a check was counted or reported ({hostedReport.SectionsLinked} of {hostedReport.SectionsTotal}, {hostedReport.Index.NoCheckSections} without a check): " + string.Join(", ", hostedReport.Findings.Select(f => f.Key)));
        }

        /// <summary>
        /// The pack lab's first round of rules, each taught by two or more corpus packs: two pins sharing a path (Satisfactory,
        /// Choo-Choo Charles), "Pin (Section)" names (A Short Hike), the one location ending in the section's name (Hylics 2),
        /// apostrophes (Hollow Knight), a JPEG named .png (Mega Man 3, Undertale), Lua 5.4's attribs (Hades), a loop ending in
        /// its continue label (Anodyne, Cat Quest, Donkey Kong 64), PopTracker's Highlight (Kingdom Hearts II), items the scripts
        /// make (Super Metroid, A Link to the Past), a display grid and numbered families said once (A Link to the Past,
        /// Undertale), a file the scripts load but the pack lacks as its own note (fourteen packs' snes.lua).
        /// </summary>
        private static async Task PackLabRoundOne()
        {
            string zip = Scratch("selftest_lab1.zip");
            var enemies = string.Join(",", Enumerable.Range(1, 9).Select(n => $"{{\"name\":\"Enemy #{n}\"}}"));
            var classes = string.Join(",", Enumerable.Range(0, 7).Select(n => $"{{\"name\":\"Anti-Fairy - Class {n}\",\"type\":\"toggle\",\"img\":\"images/sword.png\",\"codes\":\"class_{n}\"}}"));
            var classRow = string.Join(",", Enumerable.Range(0, 7).Select(n => $"\"class_{n}\""));
            // A JPEG by its bytes (a black 4×4), named .png as packs name them.
            var photo = Godot.Image.CreateEmpty(4, 4, false, Godot.Image.Format.Rgb8);
            byte[] jpeg = photo.SaveJpgToBuffer();
            photo.Dispose();
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test lab pack", "Self Test Game L",
                initLua: """
                    Tracker:AddItems("items/items.json")
                    Tracker:AddMaps("maps/maps.json")
                    Tracker:AddLocations("locations/locations.json")
                    Tracker:AddLayouts("layouts/tracker.json")
                    ScriptHost:LoadScript("scripts/autotracking/location_mapping.lua")
                    local SEED <const> = "x"
                    local trans = ScriptHost:CreateLuaItem()
                    trans.Name = "Transition A"
                    trans.CanProvideCodeFunc = function(self, code) return code == "trans_a" end
                    trans.ProvidesCodeFunc = function(self, code) return 1 end
                    PRIORITY = { [Highlight.Priority] = 1, [Highlight.None] = 0 }
                    COUNT = 0
                    for i = 1, 3 do
                        if i == 2 then goto continue end
                        local x = i
                        COUNT = COUNT + x
                        ::continue::
                    end
                    ScriptHost:LoadScript("scripts/missing.lua")
                    WATCHED = {}
                    ScriptHost:AddWatchForCode("count", "*", function(code) WATCHED[#WATCHED + 1] = code end)
                    Tracker.BulkUpdate = true
                    local shield = Tracker:FindObjectForCode("shield")
                    shield.Active = true
                    shield.Active = false
                    shield.Active = true
                    BULK_SEEN = #WATCHED
                    WAS_BULK = Tracker.BulkUpdate
                    Tracker.BulkUpdate = false
                    Tracker:FindObjectForCode("sword").Active = COUNT == 4 and PRIORITY[4] == 1 and SEED == "x"
                        and BULK_SEEN == 0 and WAS_BULK == true and #WATCHED == 1 and WATCHED[1] == "shield"
                    """,
                itemsJson: "[" + """{"name":"Sword","type":"toggle","img":"images/sword.png","codes":"sword"},{"name":"Shield","type":"toggle","img":"images/photo.png","codes":"shield"},""" +
                    """{"name":"HM01 - Cut","type":"toggle","img":"images/sword.png","codes":"hm01"},{"name":"Sword Silo","type":"toggle","img":"images/sword.png","codes":"silo1"},""" +
                    """{"name":"Shield Silo","type":"toggle","img":"images/sword.png","codes":"silo2"},{"name":"Bow Silo","type":"toggle","img":"images/sword.png","codes":"silo3"},""" + classes + "]",
                layoutsJson: """{"tracker_default":{"type":"array","content":[{"type":"itemgrid","rows":[["sword","shield","trans_a"]]},""" +
                    """{"type":"group","header":"Enemizer","content":{"type":"itemgrid","rows":[[""" + classRow + """]]}},""" +
                    """{"type":"tabbed","tabs":[{"title":"Overworld","content":{"type":"map","maps":["World"]}}]}]}}""",
                locationsJson: "[" +
                    """{"name":"Tier 1","children":[{"name":"Hub 1","sections":[{"name":"Hub 1 Item 1"}]}]},""" +
                    """{"name":"Tier 1","children":[{"name":"Hub 1","sections":[{"name":"Hub 1 - item 1","ref":"Tier 1/Hub 1/Hub 1, item 1"}],"map_locations":[{"map":"World","x":10,"y":10}]}]},""" +
                    """{"name":"Shop","children":[{"name":"Purchase 1","sections":[{"item_count":1}],"map_locations":[{"map":"World","x":60,"y":10}]}]},""" +
                    """{"name":"Overview","sections":[{"name":"Shop - purchase 1","ref":"Shop/purchase 1"}],"map_locations":[{"map":"World","x":70,"y":10}]},""" +
                    """{"name":"Beachstickball","sections":[{"name":"10 Hits"}],"map_locations":[{"map":"World","x":20,"y":10}]},""" +
                    """{"name":"Basement","sections":[{"name":"Basement Pot 1"}],"map_locations":[{"map":"World","x":30,"y":10}]},""" +
                    """{"name":"Ruins","sections":[{"name":"Collectors_Map"},{"name":"Relic"},{"name":"Lone Chest"}],"map_locations":[{"map":"World","x":40,"y":10}]},""" +
                    """{"name":"Field","sections":[""" + enemies + """],"map_locations":[{"map":"World","x":50,"y":10}]},""" +
                    """{"name":"Episode 3","children":[{"name":"E3L5: Movie Set","children":[{"name":"3DRealms Atomic Health","sections":[{"name":"3DRealms Atomic Health"}],"map_locations":[{"map":"World","x":60,"y":20}]}]}]},""" +
                    """{"name":"Sinnoh","children":[{"name":"Amity Square","sections":[{"name":"(Left) Item Near Center Warp"}],"map_locations":[{"map":"World","x":60,"y":30}]}]},""" +
                    """{"name":"Hub 2-5","sections":[{"name":"Hub 2-5 - Hub 2-5, item 9"}],"map_locations":[{"map":"World","x":60,"y":40}]}]""",
                files: new Dictionary<string, string>
                {
                    ["scripts/autotracking/location_mapping.lua"] = "LOCATION_MAPPING = {\n    [1] = {\"@Tier 1/Hub 1/Hub 1, item 1\"},\n    [7] = {\"@Shop/purchase 1\"},\n"
                        + "    [\"Ruins Relic\"] = {\"@Ruins/Relic\"},\n    [9] = {\"Lone chest\"},\n    [\"Not A Place\"] = {\"@Ruins/Relic\"},\n}\n",
                    ["scripts/autotracking/item_mapping.lua"] = "ITEM_MAPPING = {\n    [\"Shield\"] = {\"shield\", \"toggle\"},\n    [\"Not An Item\"] = {\"nothing\"},\n}\n",
                },
                binaryFiles: new Dictionary<string, byte[]> { ["images/photo.png"] = jpeg });
            var pack = await Task.Run(() => PopTracker.PopTrackerPackLoader.InspectZipPack(zip)) ?? throw new InvalidOperationException("the lab pack wasn't read");
            await Task.Run(() => PopTracker.PackImages.Check(pack));
            Expect(pack.HasDecodableImage("images/photo.png") && !pack.BrokenImages.Contains("images/photo.png"), "a JPEG named .png wasn't decoded by its header: " + string.Join("; ", pack.LoadIssues));

            // The scripts: Lua 5.4's <const>, a loop ending in its continue label after a local, PopTracker's Highlight, an item the
            // scripts make, and a file they load that the pack doesn't have (an error noted, the rest runs, as in PopTracker).
            var host = PopTracker.PackScriptHost.Load(pack) ?? throw new InvalidOperationException("the lab pack has no scripts");
            await Task.Run(() => host.Initialize());
            Expect(host.Errors.Count == 1 && host.Errors[0].StartsWith("LoadScript: 'scripts/missing.lua'", StringComparison.Ordinal) && !host.Stopped,
                "the scripts didn't run as PopTracker runs them (a <const> local, a loop ending in its continue label, Highlight): " + string.Join("; ", host.Errors));
            Expect(host.StateOf("sword")?.Active == true, "the loop with a continue label, the <const> local, Highlight, or Tracker.BulkUpdate (three changes under it run a \"*\" watch once, with the item's code) didn't work as in PopTracker");
            Expect(!host.UnsupportedApis.Any(a => a.StartsWith("Tracker.BulkUpdate", StringComparison.Ordinal)), "Tracker.BulkUpdate was counted as an unsupported feature");
            Expect(host.ScriptItemProvides("trans_a") && !host.ScriptItemProvides("trans_b"), "an item the scripts make doesn't answer to its code (or answers to others)");

            var names = new PopTracker.GameNameTable
            {
                Game = "Self Test Game L",
                Locations = new Dictionary<string, long>
                {
                    ["Hub 1, item 1"] = 1,
                    ["Beachstickball (10 Hits)"] = 2,
                    ["Waynehouse: Basement Pot 1"] = 3,
                    ["Collector's_Map"] = 4,
                    ["Arcade 2: Double Banana 1"] = 5,
                    ["Arcade 3: Double Banana 1"] = 6,
                    ["Shop Purchase 1"] = 7,
                    ["Ruins Relic"] = 8,
                    ["Lone Chest"] = 9,
                    ["E3L5 3DRealms Atomic Health"] = 10,
                    ["Amity Square (Left) - Item Near Center Warp"] = 11,
                    ["Hub 2-5, item 09"] = 12,
                },
                Items = new Dictionary<string, long> { ["Sword"] = 50, ["Shield"] = 51, ["HM01 Cut"] = 52, ["Bow"] = 53 }
            };
            var report = await Task.Run(() => PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(pack, names)));
            var index = report.Index;
            // A tile named as the game names the item, punctuation aside; tiles named as items plus a word three of them share.
            Expect(index.ItemIdsFor("hm01").SequenceEqual(new long[] { 52 }) && index.ItemIdsFor("silo3").SequenceEqual(new long[] { 53 }) && index.ItemIdsFor("silo1").SequenceEqual(new long[] { 50 }),
                $"tiles weren't paired as the lab's rules say: hm01 [{string.Join(",", index.ItemIdsFor("hm01"))}] silo3 [{string.Join(",", index.ItemIdsFor("silo3"))}] silo1 [{string.Join(",", index.ItemIdsFor("silo1"))}]");
            string Shown(long id) => index.ByLocation.TryGetValue(id, out var m) ? string.Join(", ", m.OrderBy(x => x.Via == null ? 0 : 1).Select(x => x.Section?.Name + ":" + x.Source + (x.Via != null ? "*" : ""))) : "";
            // Two pins share "Tier 1/Hub 1": the mapping's path and the overworld's ref land on the one that has the section, named
            // as the author wrote it ("Hub 1, item 1" for "Hub 1 Item 1"); "Shop/purchase 1" is the child pin "Purchase 1" with one section.
            Expect(Shown(1) == "Hub 1 Item 1:MappingScript, Hub 1 - item 1:MappingScript*" && Shown(7) == ":MappingScript, Shop - purchase 1:MappingScript*"
                && index.UnresolvedReferences.Count == 0 && index.DanglingPaths.Count == 0,
                $"a path two pins share didn't resolve to the one with the section, or a path wasn't read as its author wrote it: 1 [{Shown(1)}] 7 [{Shown(7)}], {index.UnresolvedReferences.Count} unresolved, {index.DanglingPaths.Count} dangling");
            Expect(Shown(2) == "10 Hits:ComposedName", $"\"Beachstickball\" / \"10 Hits\" wasn't the game's \"Beachstickball (10 Hits)\": [{Shown(2)}]");
            // Composed with an ancestor's code, with a leading parenthetical given to the pin, and a section that repeats its pin's name
            // (matched case and punctuation aside: "item 9" for "item 09").
            Expect(Shown(10) == "3DRealms Atomic Health:ComposedName" && Shown(11) == "(Left) Item Near Center Warp:ComposedName" && Shown(12) == "Hub 2-5 - Hub 2-5, item 9:ComposedName",
                $"names composed from the pin's ancestors weren't paired: 10 [{Shown(10)}] 11 [{Shown(11)}] 12 [{Shown(12)}]");
            // A mapping keyed by the game's name, and a value that is a section's name (case aside) rather than a path or an item code.
            Expect(Shown(8) == "Relic:MappingScript" && Shown(9) == "Lone Chest:MappingScript" && !pack.LoadIssues.Any(i => i.Contains("non-numeric")),
                $"a mapping keyed by a name or naming a section wasn't read as the author wrote it: 8 [{Shown(8)}] 9 [{Shown(9)}]; {string.Join(" | ", pack.LoadIssues)}");
            Expect(index.UnknownMappedLocationNames.SequenceEqual(new[] { "Not A Place" }) && index.UnknownMappedItemNames.SequenceEqual(new[] { "Not An Item" })
                && report.Findings.Any(f => f.Key == "locmap:stale-names") && report.Findings.Any(f => f.Key == "itemmap:stale-names") && index.ItemIdsFor("shield").SequenceEqual(new long[] { 51 }),
                "a mapping's names the game doesn't have weren't said, or the item mapping keyed by name didn't link: " + string.Join(", ", index.UnknownMappedLocationNames.Concat(index.UnknownMappedItemNames)));
            // The one location ending in the section's name after its area; two of a kind stay unmatched.
            Expect(Shown(3) == "Basement Pot 1:TailName" && Shown(5) == "" && Shown(6) == "", $"the only location ending in the section's name wasn't taken, or one of two was: 3 [{Shown(3)}] 5 [{Shown(5)}] 6 [{Shown(6)}]");
            Expect(report.Findings.Any(f => f.Key == "loc:tail" && f.Details.Count == 1), "the tail matches weren't listed for review");
            // An apostrophe is part of its word: an exact match the Doctor links by itself.
            var sure = PopTracker.PackDoctor.ExactMatches(report);
            Expect(sure.Any(f => f.Key == "loc:unmatched:Ruins|Collectors_Map" && f.Suggestions[0].Id == 4 && f.Suggestions[0].Score >= 0.999),
                "\"Collectors_Map\" and \"Collector's_Map\" aren't the same name: " + string.Join(", ", sure.Select(f => f.Key + " " + f.Suggestions[0].Score.ToString("0.00"))));
            // Nine "Enemy #n" sections with nothing to match: one family, not nine rows.
            var family = report.Findings.FirstOrDefault(f => f.Key.StartsWith("loc:family:", StringComparison.Ordinal));
            Expect(family != null && family.Details.Count == 9 && !report.Findings.Any(f => f.Key.StartsWith("loc:unmatched:Field|", StringComparison.Ordinal)),
                "the numbered sections that match nothing weren't said once as a family: " + string.Join(", ", report.Findings.Where(f => f.Key.StartsWith("loc:", StringComparison.Ordinal)).Select(f => f.Key)));
            // Tiles: the scripts' item isn't an undefined cell; the enemizer grid is the pack's own display, said once; the two real tiles link.
            var tileKeys = report.Findings.Where(f => f.Key.StartsWith("tile:", StringComparison.Ordinal) || f.Key.StartsWith("grid:", StringComparison.Ordinal)).Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Expect(tileKeys.Count == 2 && tileKeys[0].StartsWith("grid:display:", StringComparison.Ordinal) && tileKeys[1] == "tile:scriptitem:trans_a"
                && report.TilesTotal == 2 && report.TilesLinked == 2 && report.TileDisplay == 7 && report.TilesScripted == 1,
                $"the tiles weren't read as the lab's rules say ({report.TilesLinked} of {report.TilesTotal}, {report.TileDisplay} display, {report.TilesScripted} by the scripts): " + string.Join(", ", tileKeys));
            // The missing file is the pack's own note, not a script failure.
            Expect(report.Findings.Any(f => f.Key.StartsWith("script:file:", StringComparison.Ordinal)) && !report.Findings.Any(f => f.Key.StartsWith("script:error:", StringComparison.Ordinal)),
                "a file the scripts load but the pack lacks wasn't told from a script error: " + string.Join(", ", report.Findings.Where(f => f.Key.StartsWith("script:", StringComparison.Ordinal)).Select(f => f.Key)));
        }

        private static async Task PacksReadAsPopTrackerReadsThem()
        {
            // Ship of Harkinian's pack, in small: a progressive item whose code is on its stage, a label and one of the
            // pack's own switches in the grid, scripts in Lua 5.3, shops whose "Item 1" the mapping leaves out, an overview's
            // copy whose ref names a room that isn't there, the goal the mapping doesn't track, and a map no tab shows.
            string zip = Scratch("selftest_poptracker.zip");
            var spots = string.Join(",", Enumerable.Range(1, 16).Select(n => $"{{\"name\":\"Spot {n}\"}}"));
            var spotMappings = string.Join("\n", Enumerable.Range(1, 16).Select(n => $"    [{300 + n}] = {{\"@Field/Spots/Spot {n}\"}},"));
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test PopTracker pack", "Self Test Game P",
                initLua: """
                    Tracker:AddItems("items/items.json")
                    Tracker:AddItems("items/settings_main.json")
                    Tracker:AddMaps("maps/maps.json")
                    Tracker:AddLocations("locations/locations.json")
                    Tracker:AddLayouts("layouts/tracker.json")
                    HOOKS = 0
                    Tracker:FindObjectForCode("go_mode").Active = false
                    ScriptHost:AddWatchForCode("hook", "hookshot", function(code) HOOKS = HOOKS + 1 end)
                    local hook = Tracker:FindObjectForCode("hookshot")
                    hook:SetOverlay("x")
                    hook:SetOverlayFontSize(12)
                    RESULT = string.format("%s|%s|%d|%d|%d|%d", nil, true, 7 // 2, 6 & 3, 1 << 4 | 1, ~0 & 0xF)
                    Archipelago:AddClearHandler("t", function(slot_data)
                        Tracker:FindObjectForCode("hookshot").Active = true
                        Tracker:FindObjectForCode("shield").Active = RESULT == "nil|true|3|2|17|15" and HOOKS == 1
                    end)
                    """,
                itemsJson: """
                    [{"name":"Progressive Hookshot","type":"progressive","stages":[{"name":"Hookshot","img":"images/sword.png","codes":"hookshot"},{"name":"Longshot","img":"images/sword.png"}]},
                     {"type":"static","img":"images/sword.png","codes":"label_dt"},
                     {"name":"Shield","type":"toggle","img":"images/sword.png","codes":"shield"},
                     {"name":"Shield","type":"toggle","img":"images/sword.png","codes":"shield_drop"},
                     {"name":"Both","type":"composite_toggle","codes":"both","item_left":"hookshot","item_right":"shield"},
                     {"name":"Boss Marker","type":"toggle","img":"images/sword.png","codes":"ft_boss"},
                     {"name":"GO! Mode","type":"toggle","img":"images/sword.png","codes":"go_mode"}]
                    """,
                layoutsJson: """
                    {"tracker_default":{"type":"array","content":[
                        {"type":"itemgrid","rows":[["label_dt","hookshot","shield","setting_auto_tab"],["shield_drop","both","ft_boss","go_mode"]]},
                        {"type":"itemgrid","item_size":3,"rows":[["mark1","mark2"]]},
                        {"type":"tabbed","tabs":[{"title":"Overworld","content":{"type":"map","maps":["World"]}}]}]}}
                    """,
                locationsJson: "[" +
                    """{"name":"Kokiri Forest","children":[{"name":"Shop","sections":[{"name":"Item 1"},{"name":"Item 2"},{"name":"Item 3"}],"map_locations":[{"map":"World","x":10,"y":10}]}]},""" +
                    """{"name":"Goron City","children":[{"name":"Shop","sections":[{"name":"Item 1"},{"name":"Item 2"},{"name":"Item 3"}],"map_locations":[{"map":"World","x":20,"y":10}]}]},""" +
                    """{"name":"Forest Temple","children":[{"name":"Basement Side Room","sections":[{"name":"Gold Skulltula"}],"map_locations":[{"map":"World","x":30,"y":10}]}]},""" +
                    """{"name":"Overworld","children":[{"name":"Forest Temple","sections":[{"name":"Basement Side Room - Gold Skulltula","ref":"Forest Temple/Basement Chest Room/Gold Skulltula"}],"map_locations":[{"map":"World","x":40,"y":10}]}]},""" +
                    """{"name":"Castle","children":[{"name":"Arena","sections":[{"name":"Ganon"}],"map_locations":[{"map":"World","x":50,"y":10}]}]},""" +
                    """{"name":"Field","children":[{"name":"Spots","sections":[""" + spots + """],"map_locations":[{"map":"World","x":60,"y":10}]}]}]""",
                files: new Dictionary<string, string>
                {
                    ["items/settings_main.json"] = """[{"name":"Auto Tab","type":"toggle","img":"images/sword.png","codes":"setting_auto_tab"}]""",
                    ["scripts/autotracking/location_mapping.lua"] = "LOCATION_MAPPING = {\n" +
                        "    [102] = {\"@Kokiri Forest/Shop/Item 2\"},\n    [103] = {\"@Kokiri Forest/Shop/Item 3\"},\n" +
                        "    [112] = {\"@Goron City/Shop/Item 2\"},\n    [113] = {\"@Goron City/Shop/Item 3\"},\n" +
                        "    [200] = {\"@Forest Temple/Basement Side Room/Gold Skulltula\"},\n    [201] = {\"ft_boss\"},\n" + spotMappings + "\n}\n"
                });
            var pack = await Task.Run(() => PopTracker.PopTrackerPackLoader.InspectZipPack(zip)) ?? throw new InvalidOperationException("the test pack wasn't read");
            Expect(pack.ItemsByCode.TryGetValue("hookshot", out var hookshot) && hookshot.Name == "Progressive Hookshot",
                "a progressive item isn't found by its stage's code (PopTracker finds it so)");
            Expect(pack.UnusedMaps().SetEquals(new[] { "Broken" }), $"the map no tab shows and no pin is on isn't the unused one: {string.Join(", ", pack.UnusedMaps())}");
            Expect(pack.ScriptCodes.SetEquals(new[] { "go_mode", "hookshot", "shield" }) && pack.CodesSetByChecks.SetEquals(new[] { "ft_boss" }) && pack.ItemGridGroups.Any(g => g.Decorative && g.Rows[0][0] == "mark1"),
                $"the codes the scripts set ({string.Join(",", pack.ScriptCodes)}), the codes checks set ({string.Join(",", pack.CodesSetByChecks)}) or the decorative grid weren't read");

            // The scripts: Lua 5.3's operators, %s of nil and true, the overlay methods, and a watch on the stage's code.
            var host = PopTracker.PackScriptHost.Load(pack) ?? throw new InvalidOperationException("the pack has no scripts");
            await Task.Run(() =>
            {
                host.Initialize();
                host.Clear(1, 0, new Newtonsoft.Json.Linq.JObject());
            });
            Expect(host.Errors.Count == 0 && !host.Stopped, "the scripts didn't run cleanly: " + string.Join("; ", host.Errors));
            Expect(host.StateOf("hookshot")?.Active == true && host.StateOf("shield")?.Active == true,
                "Lua 5.3's operators, string.format of nil and true, or the watch on a stage's code didn't work as in PopTracker");

            var names = new PopTracker.GameNameTable
            {
                Game = "Self Test Game P",
                Locations = new Dictionary<string, long>
                {
                    ["KF Shop Item 1"] = 101,
                    ["KF Shop Item 2"] = 102,
                    ["KF Shop Item 3"] = 103,
                    ["GC Shop Item 1"] = 111,
                    ["GC Shop Item 2"] = 112,
                    ["GC Shop Item 3"] = 113,
                    ["Forest Temple GS Basement"] = 200,
                    ["Forest Temple Boss"] = 201,
                },
                Items = new Dictionary<string, long> { ["Hookshot"] = 50, ["Shield"] = 51 }
            };
            foreach (int n in Enumerable.Range(1, 16)) names.Locations[$"Field Spot {n}"] = 300 + n;
            var report = await Task.Run(() => PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(pack, names)));
            var index = report.Index;
            string Shown(long id) => index.ByLocation.TryGetValue(id, out var m) ? string.Join(", ", m.Select(x => x.Pin.FullPath + "/" + x.Section?.Name + ":" + x.Source)) : "";
            // Each shop's "Item 1" takes its neighbours' form, not one shop's name for all.
            Expect(Shown(101) == "Kokiri Forest/Shop/Item 1:NeighbourName" && Shown(111) == "Goron City/Shop/Item 1:NeighbourName",
                $"the shops' \"Item 1\" weren't named like their neighbours: 101 [{Shown(101)}] 111 [{Shown(111)}]");
            // The overview's copy follows its own name to the room the ref meant.
            Expect(index.RepairedReferences.Count == 1 && index.UnresolvedReferences.Count == 0 && Shown(200).Contains("Overworld/Forest Temple/Basement Side Room - Gold Skulltula:MappingScript"),
                $"the reference wasn't repaired from the section's own name: [{Shown(200)}]");
            // The goal the pack's mapping doesn't name either is said as such.
            Expect(report.Findings.Any(f => f.Key == "loc:untracked:Castle/Arena|Ganon" && f.Severity == PopTracker.FindingSeverity.Info)
                && !report.Findings.Any(f => f.Key.StartsWith("loc:unmatched:", StringComparison.Ordinal))
                && report.Findings.Any(f => f.Key == "loc:neighbours") && report.Findings.Any(f => f.Key == "loc:refs" && f.Details.Any(d => d.Contains("Atlas followed"))),
                "the Doctor's location findings aren't the expected ones: " + string.Join(", ", report.Findings.Select(f => f.Key)));
            // Tiles: the stage's code links by the stage's name; a tile named like a linked one links like it; the label, the
            // composite and the switch aren't tiles to link; a tile a check sets and one the scripts set are said so; the
            // decorative grid's marks are no tiles at all.
            var tileFindings = report.Findings.Where(f => f.Key.StartsWith("tile:", StringComparison.Ordinal)).Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Expect(index.ItemIdsFor("hookshot").SequenceEqual(new long[] { 50 }) && index.ItemIdsFor("shield_drop").SequenceEqual(new long[] { 51 })
                && report.TilesTotal == 3 && report.TilesLinked == 3 && report.TileDecorations == 2 && report.TileSwitches == 1 && report.TilesByCheck == 1 && report.TilesScripted == 1
                && tileFindings.SequenceEqual(new[] { "tile:bycheck:ft_boss", "tile:scripted:go_mode" }),
                $"the grid's tiles weren't read as PopTracker reads them ({report.TilesLinked} of {report.TilesTotal}, {report.TileDecorations} decorations, {report.TileSwitches} switches, {report.TilesByCheck} by a check, {report.TilesScripted} by the scripts): "
                + string.Join(", ", tileFindings));
            Expect(report.Findings.Any(f => f.Key == "map:unused:Broken") && !report.Findings.Any(f => f.Key == "map:nobg:Broken"),
                "the unused map was reported as a missing picture: " + string.Join(", ", report.Findings.Where(f => f.Key.StartsWith("map:", StringComparison.Ordinal)).Select(f => f.Key)));

            // Two shops' "Item 1" choosing one location clash (eight shops aren't one check shown eight times), while an
            // overview's copy named as the location is the same check.
            PopTracker.Finding Choosing(string pinPath, string section, long id, string label) => new PopTracker.Finding
            {
                Key = "loc:unmatched:" + pinPath + "|" + section,
                Subject = "link:" + pinPath + "|" + section,
                Actions = PopTracker.FindingActions.LinkLocation,
                Query = section,
                Suggestions = { new PopTracker.Suggestion { Id = id, Label = label, Score = 0.9 } }
            };
            var kf = Choosing("Somewhere/Shop A", "Item 9", 900, "KF Shop Item 9");
            var gc = Choosing("Elsewhere/Shop B", "Item 9", 900, "KF Shop Item 9");
            var view = Choosing("Overview/Lake", "Fishing Prize", 901, "Fishing Prize");
            var area = Choosing("Lake/Pond", "Fishing Prize", 901, "Fishing Prize");
            var clashes = PopTracker.PackDoctor.Conflicts(report, new List<PopTracker.Finding> { kf, gc, view, area }, f => f.Suggestions[0]);
            Expect(clashes.Count == 1 && (clashes.ContainsKey(kf.Key) || clashes.ContainsKey(gc.Key)),
                "one check was told from two by its generic name: " + string.Join("; ", clashes.Select(c => c.Key + " " + c.Value)));

            // Atlas's own links that sent two different checks to one location are taken back, and a tile it linked when the
            // code seemed undefined (the code is on the item's stage); the user's own aren't.
            string key = PopTracker.PackFixes.KeyFor(pack);
            try
            {
                PopTracker.PackFixes.Edit(key, "self-test links", file =>
                {
                    file.Links.Add(new PopTracker.LocationLinkFix { Subject = "link:Kokiri Forest/Shop|Item 1", PinPath = "Kokiri Forest/Shop", SectionName = "Item 1", ApLocationId = 101, ApLocationName = "KF Shop Item 1", Source = "suggested" });
                    file.Links.Add(new PopTracker.LocationLinkFix { Subject = "link:Goron City/Shop|Item 1", PinPath = "Goron City/Shop", SectionName = "Item 1", ApLocationId = 101, ApLocationName = "KF Shop Item 1", Source = "suggested" });
                    file.Links.Add(new PopTracker.LocationLinkFix { Subject = "link:Castle/Arena|Ganon", PinPath = "Castle/Arena", SectionName = "Ganon", ApLocationId = 103, ApLocationName = "KF Shop Item 3" });
                    file.Links.Add(new PopTracker.LocationLinkFix { Subject = "link:Field/Spots|Spot 1", PinPath = "Field/Spots", SectionName = "Spot 1", ApLocationId = 103, ApLocationName = "KF Shop Item 3" });
                    file.Tiles.Add(new PopTracker.TileFix { Subject = "tile:hookshot", Code = "hookshot", ApItemId = 50, ApItemName = "Hookshot", Source = "automatic", AuthorStamp = "(absent)" });
                    file.Tiles.Add(new PopTracker.TileFix { Subject = "tile:shield", Code = "shield", ApItemId = 51, ApItemName = "Shield", Source = "automatic", AuthorStamp = PopTracker.PackFixes.AuthorStamp(pack, "tile:shield") });
                });
                int forgotten = PopTracker.PackDoctorService.ForgetStaleGuesses(key, pack);
                var left = PopTracker.PackFixes.Get(key).Links.Select(l => l.Subject).Concat(PopTracker.PackFixes.Get(key).Tiles.Select(t => t.Subject)).OrderBy(s => s, StringComparer.Ordinal).ToList();
                Expect(forgotten == 3 && left.SequenceEqual(new[] { "link:Castle/Arena|Ganon", "link:Field/Spots|Spot 1", "tile:shield" }),
                    $"Atlas's stale guesses weren't taken back alone ({forgotten} taken back; left: {string.Join(", ", left)})");
            }
            finally
            {
                PopTracker.PackFixes.Reset(key);
                SafeFile.Delete(zip);
            }
        }

        private static async Task PackScriptsUseTheirOwnCompiledFiles()
        {
            string zip = Scratch("selftest_modules.zip");
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test modules pack", "Self Test Game M", initLua: """
                local helper = require("scripts.helper")
                ScriptHost:LoadScript("scripts/other.lua")
                local broken = ScriptHost:LoadScript("scripts/broken.lua")
                local missing = ScriptHost:LoadScript("scripts/missing.lua")
                local required = pcall(require, "scripts.broken")
                local failed = pcall(ScriptHost:CreateLuaItem().Set, nil, "key", 1)
                local odd = pcall(math.random, 1e20) or pcall(string.format, "%c", -1) or pcall(os.date, "*t", 1e20)
                local memory = collectgarbage("count")
                for i = 1, 100 do collectgarbage() end
                local sword = Tracker:FindObjectForCode("sword")
                sword.AcquiredCount = helper.answer
                Tracker:FindObjectForCode("shield").Active = OTHER == true and not broken and not missing and not required and not failed
                    and not odd and type(memory) == "number" and json == nil and dynamic == nil
                Archipelago:AddItemHandler("test", function(index, item_id)
                    if item_id == 1002 then error("a broken item") end
                    if item_id == 1003 then local c = string.format("%c", -1) end
                    sword.AcquiredCount = sword.AcquiredCount + 1
                end)
                """, files: new Dictionary<string, string>
            {
                ["scripts/helper.lua"] = "return { answer = 42 }",
                ["scripts/other.lua"] = "OTHER = true",
                ["scripts/broken.lua"] = "this is not lua",
            });
            var host = PopTracker.PackScriptHost.Load(PopTracker.PopTrackerPackLoader.InspectZipPack(zip) ?? throw new InvalidOperationException("the test pack wasn't read"));
            int collections = GC.CollectionCount(2);
            bool started = await Task.Run(() => host.Initialize());
            collections = GC.CollectionCount(2) - collections;
            Expect(started && !host.Stopped, $"init.lua didn't run: {host.StopReason ?? string.Join("; ", host.Errors)}");
            Expect(host.StateOf("sword")?.Count == 42, $"require didn't give the pack's module (sword {host.StateOf("sword")?.Count})");
            Expect(host.StateOf("shield")?.Active == true, "other.lua didn't run, a broken or missing script counted as loaded (or required), a failing function of Atlas's " +
                "or the library's didn't fail as a Lua error, collectgarbage(\"count\") isn't a number, or json or dynamic is there");
            Expect(host.Errors.Any(e => e.StartsWith("scripts/broken.lua: ")) && host.Errors.Contains("LoadScript: 'scripts/missing.lua' isn't in the pack"),
                "the broken and missing scripts weren't reported: " + string.Join("; ", host.Errors));
            Expect(collections < 50, $"collectgarbage() collected Atlas's memory ({collections} full collections for 100 calls)");
            await Task.Run(() =>
            {
                host.ApplyItem(0, 1002, "Broken Item", 1);
                host.ApplyItem(1, 1003, "Odd Item", 1);
                host.ApplyItem(2, 1000, "Sword", 1);
            });
            Expect(!host.Stopped && host.Errors.Any(e => e.Contains("a broken item")) && host.Errors.Any(e => e.Contains("OverflowException")) && host.StateOf("sword")?.Count == 43,
                $"a Lua error or a library's .NET failure stopped the scripts, or wasn't recorded: {host.StopReason}; sword {host.StateOf("sword")?.Count}; " + string.Join("; ", host.Errors));
            Expect(host.Peak.Steps > 0 && host.PeakWork != null, "the busiest piece of work wasn't measured");
        }

        /// <summary>
        /// A map pack's zip can't fill Atlas's memory, whatever its headers claim (SafeZip, ImageBudget). An image whose header
        /// says 20,000 × 20,000 pixels (1.6 GB decoded, from a 33-byte file) is refused before it's decoded; a file that unpacks
        /// to more than Atlas reads isn't read. Either way the rest of the pack works and the user is told why: a pack's load
        /// issues, and for its scripts a stop before they start, which the Pack Doctor reports.
        /// </summary>
        private static async Task PackFilesCantFillMemory()
        {
            string zip = Scratch("selftest_bombs.zip");
            byte[] huge = AP_Atlas.Core.Testing.FakeMapPack.Png(1, 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16), 20000);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(20), 20000);
            var bomb = new byte[SafeZip.TextLimit + 1]; // zips to about 65 KB
            bomb.AsSpan().Fill((byte)' ');
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test bomb pack", "Self Test Game B",
                binaryFiles: new Dictionary<string, byte[]> { ["images/huge.png"] = huge, ["locations/bomb.json"] = bomb });
            var pack = PopTracker.PopTrackerPackLoader.InspectZipPack(zip) ?? throw new InvalidOperationException("the test pack wasn't read");
            Expect(pack.LoadIssues.Any(i => i.StartsWith("pack/locations/bomb.json: wasn't read") && i.Contains("unpacks to more than 64 MB")),
                "the oversized file isn't among the pack's issues: " + string.Join(" | ", pack.LoadIssues));
            Expect(pack.Locations.Count == 2 && pack.ItemsByCode.ContainsKey("sword") && pack.Maps.ContainsKey("World"), "the rest of the pack wasn't read");

            long decoded = PopTracker.PackImages.Decoded;
            using (PopTracker.PackImages.Use(pack))
            {
                Expect(pack.BrokenImages.Contains("images/huge.png") && pack.LoadIssues.Any(i => i.Contains("'images/huge.png'") && i.Contains("20,000 × 20,000 pixels, larger than Atlas shows")),
                    "the huge image wasn't refused: " + string.Join(" | ", pack.LoadIssues));
                Expect(PopTracker.PackImages.Decoded - decoded == 2 && pack.Images.ContainsKey("images/sword.png") && pack.Maps["World"].BackgroundTexture != null,
                    $"the pack's other images weren't decoded ({PopTracker.PackImages.Decoded - decoded} decoded)");
            }

            // One budget covers all of a pack's images: with room for sword.png (8 × 8) and world.png (64 × 32) only, the
            // image after them is refused (a test's small total stands in for the 2 GB a real pack would need).
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test bomb pack", "Self Test Game B",
                binaryFiles: new Dictionary<string, byte[]> { ["images/huge.png"] = huge, ["images/extra.png"] = AP_Atlas.Core.Testing.FakeMapPack.Png(32, 32) });
            var budgeted = PopTracker.PopTrackerPackLoader.InspectZipPack(zip)!;
            await Task.Run(() => PopTracker.PackImages.Check(budgeted, new ImageBudget((8 * 8 + 64 * 32) * 4)));
            Expect(budgeted.BrokenImages.SetEquals(new[] { "images/broken.png", "images/huge.png", "images/extra.png" })
                && budgeted.LoadIssues.Any(i => i.Contains("'images/extra.png'") && i.Contains("together come to more than Atlas decodes")),
                "the pack's images weren't held to one budget: " + string.Join(" | ", budgeted.LoadIssues));

            // Scripts with a file too big to read stop before they start, and say why.
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test bomb pack", "Self Test Game B", initLua: "error('init.lua ran')",
                binaryFiles: new Dictionary<string, byte[]> { ["scripts/bomb.lua"] = bomb });
            var host = PopTracker.PackScriptHost.Load(PopTracker.PopTrackerPackLoader.InspectZipPack(zip)!);
            Expect(host?.StopReason == "'pack/scripts/bomb.lua' unpacks to more than 64 MB, more than Atlas reads from one file", $"the scripts weren't stopped: {host?.StopReason ?? "no scripts"}");
            bool started = await Task.Run(() => host!.Initialize());
            Expect(!started && host!.Errors.Count == 0, "stopped scripts still ran: " + string.Join(" | ", host!.Errors));
            var report = await Task.Run(() => PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(PopTracker.PopTrackerPackLoader.InspectZipPack(zip)!, null)));
            Expect(report.Findings.Any(f => f.Key == "script:stopped" && f.Detail.Contains("unpacks to more than 64 MB")),
                "the Pack Doctor doesn't report the stopped scripts: " + string.Join("; ", report.Findings.Where(f => f.Category == "Scripts").Select(f => f.Title)));

            // An image the user chooses for a fix (Pack Doctor: "Replace image…") is checked the same way, before it's copied.
            string key = PopTracker.PackFixes.KeyFor(pack);
            string chosen = Scratch("selftest_chosen_huge.png");
            File.WriteAllBytes(chosen, huge);
            string? refusal = null;
            try { PopTracker.PackFixes.ImportImage(key, chosen); }
            catch (InvalidDataException ex) { refusal = ex.Message; }
            Expect(refusal?.Contains("20,000 × 20,000 pixels, larger than Atlas shows") == true, $"a huge image was imported as a fix ({refusal ?? "not refused"})");
            File.WriteAllBytes(chosen, AP_Atlas.Core.Testing.FakeMapPack.Png(16, 16));
            string stored = PopTracker.PackFixes.ImportImage(key, chosen);
            using var texture = PopTracker.PackFixes.LoadImage(key, stored);
            Expect(texture?.GetWidth() == 16, "a good image chosen for a fix doesn't load");
        }

        /// <summary>
        /// MoonSharp's compiler recurses once per level of nesting, and running out of stack would end Atlas (.NET can't
        /// catch it). Code nested deeper than Atlas compiles is refused before the compiler sees it, whether it's one of the
        /// pack's files or code a script loads. Code within the limit compiles on a thread of its own with room to spare:
        /// 900 functions inside each other, here started from a thread with only 1 MB of stack, which they'd overflow. Every
        /// loader goes through the same checks (load from a string or a reader function, loadfile with or without its own
        /// environment, dofile), and binary chunks and too much code at once are refused.
        /// </summary>
        private static async Task PackScriptsCompileSafely()
        {
            string zip = Scratch("selftest_compile.zip");
            string nestedFunctions = "return " + string.Concat(Enumerable.Repeat("function() return ", 900)) + "1" + string.Concat(Enumerable.Repeat(" end", 900));
            AP_Atlas.Core.Testing.FakeMapPack.Write(zip, "Self-test compile pack", "Self Test Game C", initLua: """
                print("deep LoadScript: " .. tostring(ScriptHost:LoadScript("scripts/deep.lua")))
                local f, unwrapped = require("scripts.functions"), 0
                while type(f) == "function" do f = f() unwrapped = unwrapped + 1 end
                print("unwrapped: " .. unwrapped .. " to " .. tostring(f))
                local deep, deepError = load(string.rep("(", 5000) .. "1" .. string.rep(")", 5000))
                print("deep load: " .. tostring(deep) .. " | " .. tostring(deepError))
                print("plain load: " .. load("return 1 + 1")())
                local parts, i = { "return ", "40 ", "+ 2" }, 0
                print("reader load: " .. load(function() i = i + 1 return parts[i] end)())
                local binary, binaryError = load(string.dump(function() return 1 end))
                print("binary load: " .. tostring(binary) .. " | " .. tostring(binaryError))
                local long, longError = load(string.rep("x = 1 ", 200000))
                print("long load: " .. tostring(long) .. " | " .. tostring(longError))
                print("loadfile: " .. loadfile("scripts/helper.lua")())
                print("dofile: " .. dofile("scripts/helper.lua"))
                print("loadfile with its own environment: " .. loadfile("scripts/env.lua", "t", { x = 5 })())
                """, files: new Dictionary<string, string>
            {
                ["scripts/deep.lua"] = "return " + new string('(', 100_000) + "1" + new string(')', 100_000),
                ["scripts/functions.lua"] = nestedFunctions,
                ["scripts/helper.lua"] = "return 7",
                ["scripts/env.lua"] = "return x",
            });
            var host = PopTracker.PackScriptHost.Load(PopTracker.PopTrackerPackLoader.InspectZipPack(zip) ?? throw new InvalidOperationException("the test pack wasn't read"));
            // A thread with 1 MB of stack, like a .NET thread's default: the compiler must not need the caller's stack.
            bool started = false;
            var small = new System.Threading.Thread(() => started = host.Initialize(), 1 << 20);
            small.Start();
            await Task.Run(small.Join);
            string Said(string what) => host.Log.FirstOrDefault(line => line.StartsWith(what + ": ")) is string line ? line.Substring(what.Length + 2) : "(nothing)";
            Expect(started && !host.Stopped, $"init.lua didn't run: {host.StopReason ?? string.Join("; ", host.Errors)}");
            Expect(host.CompileErrors.TryGetValue("scripts/deep.lua", out string? refused) && refused.Contains("levels deep, deeper than Atlas compiles"),
                $"the 100,000-deep file wasn't refused: {refused ?? "it compiled"}");
            Expect(Said("deep LoadScript") == "false" && host.Errors.Any(e => e.Contains("deeper than Atlas compiles")), $"loading the refused file: {Said("deep LoadScript")}");
            Expect(host.DeepestNesting >= 100_000, $"the deepest nesting is {host.DeepestNesting}");
            Expect(Said("unwrapped") == "900 to 1", $"900 functions inside each other: {Said("unwrapped")}");
            Expect(Said("deep load").StartsWith("nil | ") && Said("deep load").Contains("deeper than Atlas compiles"), $"load of deep code: {Said("deep load")}");
            Expect(Said("plain load") == "2" && Said("reader load") == "42", $"load: {Said("plain load")}, from a reader: {Said("reader load")}");
            Expect(Said("binary load").StartsWith("nil | ") && Said("binary load").Contains("binary"), $"load of a binary chunk: {Said("binary load")}");
            Expect(Said("long load").StartsWith("nil | ") && Said("long load").Contains("more than Atlas loads at once"), $"load of too much code: {Said("long load")}");
            Expect(Said("loadfile") == "7" && Said("dofile") == "7" && Said("loadfile with its own environment") == "5",
                $"loadfile: {Said("loadfile")}, dofile: {Said("dofile")}, with its own environment: {Said("loadfile with its own environment")}");
        }

        /// <summary>
        /// A settings file from an older Atlas loads: settings Atlas no longer has (the per-area Scale* overrides, the map's
        /// old split and font size) are ignored, the rest are read, and nothing is taken for damage (no backup put back).
        /// </summary>
        private static Task OlderSettingsStillLoad()
        {
            string path = Path.Combine(DataManager.GetDataDirectory(), "settings.json");
            var recovered = new List<string>();
            void OnRecovered(string file, string what) => recovered.Add($"{Path.GetFileName(file)} {what}");
            SafeFile.Recovered += OnRecovered;
            try
            {
                File.WriteAllText(path, """
                    { "SlotsFontSize": 18, "ScaleSidebar": 1.5, "ScaleContent": 2.0, "ScaleConsole": 1.25, "ScaleStatusBar": 0.8,
                      "MapSplitOffset": 400, "MapFontSize": 20, "ThemeAccentColor": "#123456" }
                    """);
                var settings = DataManager.LoadSettings();
                Expect(settings.SlotsFontSize == 18 && settings.ThemeAccentColor == "#123456", "an older settings file's settings weren't read");
                Expect(recovered.Count == 0, "an older settings file was taken for damage: " + string.Join("; ", recovered));
            }
            finally
            {
                SafeFile.Recovered -= OnRecovered;
                DataManager.SaveSettings(new AppSettings());
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// The Pack Doctor analyses a snapshot taken on the main thread: a fix edited while an analysis runs doesn't change
        /// it under the analysis, and the next snapshot sees the edit. The pack's own mapping is linked as usual.
        /// </summary>
        private static Task KeyItemsAutoLayoutPicks()
        {
            var measured = new List<(string Id, float Width, float Height)> { ("tracker_default", 300f, 900f), ("tracker_horizontal", 1100f, 400f), ("tracker_vertical", 280f, 1400f) };
            Expect(AP_Atlas.Core.PopTracker.KeyItemsLayouts.PickFitting(measured, 1200f, 500f) == "tracker_horizontal", "a view the horizontal root fits (and the default overflows) doesn't get the horizontal root");
            Expect(AP_Atlas.Core.PopTracker.KeyItemsLayouts.PickFitting(measured, 400f, 1000f) == "tracker_default", "a view only the default fits doesn't keep the default");
            Expect(AP_Atlas.Core.PopTracker.KeyItemsLayouts.PickFitting(measured, 1200f, 1000f) == "tracker_horizontal", "of two layouts that fit, the one filling more of the view (the horizontal) isn't picked");
            Expect(AP_Atlas.Core.PopTracker.KeyItemsLayouts.PickFitting(new List<(string, float, float)> { ("tracker_default", 500f, 400f), ("atlas:horizontal", 400f, 500f) }, 1000f, 1000f) == "tracker_default",
                "a tie in how much of the view two layouts fill doesn't keep the earlier (the pack's own)");
            Expect(AP_Atlas.Core.PopTracker.KeyItemsLayouts.PickFitting(measured, 300f, 300f) == "tracker_default", "with nothing fitting, the least overflowing root (the default: 3× the height, against the horizontal's 3.7× the width) isn't picked");
            Expect(AP_Atlas.Core.PopTracker.KeyItemsLayouts.PickFitting(new List<(string, float, float)>(), 100f, 100f) == null, "no roots gives no pick");
            return Task.CompletedTask;
        }

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

        private static async Task PackDoctorAppliesTileFixes()
        {
            var pack = new PopTracker.LoadedPack
            {
                Manifest = new PopTracker.PopTrackerManifest { Name = "Self-test apply pack", GameName = "Self-test Apply Game", Version = "1.0" },
                SourcePath = Scratch("selftest_apply_pack.zip")
            };
            pack.ItemsByCode["known"] = new PopTracker.PopTrackerItem { Name = "Known Item", CodesRaw = "known" };
            // deku_shield and twin have no item; twin and twin_ both match "Twin" exactly (a clash: only the first is linked by itself).
            pack.ItemGridGroups.Add(new PopTracker.PackItemGrid { LayoutKey = "tracker_default", Rows = new List<List<string>> { new List<string> { "deku_shield", "known", "twin", "twin_" } } });
            var names = new PopTracker.GameNameTable { Game = "Self-test Apply Game", Source = "server" };
            names.Items["Deku Shield"] = 5;
            names.Items["Known Item"] = 6;
            names.Items["Twin"] = 7;
            string key = PopTracker.PackFixes.KeyFor(pack);
            PopTracker.PackFixes.Reset(key);
            try
            {
                var first = await Task.Run(() => PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(pack, names)));
                var unknown = first.Findings.FirstOrDefault(f => f.Key == "tile:unknown:deku_shield");
                Expect(unknown != null && unknown.Suggestions.Count > 0 && unknown.Suggestions[0].Label == "Deku Shield" && unknown.Suggestions[0].Score >= 0.999,
                    "a code no item defines isn't reported with its exact match first");
                var exact = PopTracker.PackDoctor.ExactMatches(first);
                Expect(exact.Select(f => f.Key).OrderBy(k => k).SequenceEqual(new[] { "tile:unknown:deku_shield", "tile:unknown:twin" }),
                    "the exact matches aren't deku_shield and twin alone (twin_ clashes with twin): " + string.Join(", ", exact.Select(f => f.Key)));

                // Linked by the Doctor itself: the fixes are marked automatic, the pass is recorded for the version.
                int linked = PopTracker.PackDoctorService.AutoLinkExact(key, pack, first);
                var fixes = PopTracker.PackFixes.Get(key);
                Expect(linked == 2 && fixes.Tiles.Count == 2 && fixes.Tiles.All(t => t.Automatic && t.ApItemId != null) && fixes.AutoLinkedVersion == PopTracker.PackDoctorService.AutoLinkStamp(pack),
                    $"the automatic pass linked {linked} ({fixes.Tiles.Count} tile fixes, version '{fixes.AutoLinkedVersion}')");

                // The fix defines the item: the next analysis sees the tile, linked, and records the automatic link.
                var second = await Task.Run(() => PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(pack, names)));
                Expect(!second.Findings.Any(f => f.Key == "tile:unknown:deku_shield" || f.Key == "tile:unknown:twin") && second.Findings.Any(f => f.Key == "tile:unknown:twin_"),
                    "after the fix, the code is still reported as undefined (or the clashing one was linked)");
                Expect(second.Pack.ItemsByCode.TryGetValue("deku_shield", out var defined) && defined.Name == "Deku Shield" && second.Index.ItemIdsFor("deku_shield").Contains(5),
                    "the fix didn't define the tile's item, or the index doesn't link it");
                Expect(second.Findings.Any(f => f.Key == "auto:tile:deku_shield" && f.Severity == PopTracker.FindingSeverity.AutoFixed), "the automatic link isn't listed as fixed on its own");
                Expect(second.TilesLinked == 3, $"{second.TilesLinked} tiles are linked, not 3");

                // Undo takes the automatic links back in one step.
                Expect(PopTracker.PackFixes.Undo(key) && PopTracker.PackFixes.Get(key).Tiles.Count == 0, "Undo didn't take the automatic links back");
                var third = await Task.Run(() => PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(pack, names)));
                Expect(third.Findings.Any(f => f.Key == "tile:unknown:deku_shield"), "after Undo, the code isn't reported again");

                // A fix that links a tile to an item the game doesn't have any more is reported, with the tile still linkable.
                PopTracker.PackFixes.Edit(key, "self-test: a stale fix", f => f.Tiles.Add(new PopTracker.TileFix { Subject = "tile:known", Code = "known", ApItemId = 999, ApItemName = "Gone Item" }));
                var fourth = await Task.Run(() => PopTracker.PackDoctor.Analyze(PopTracker.PackDoctor.Prepare(pack, names)));
                var stale = fourth.Findings.FirstOrDefault(f => f.Key == "tile:stalefix:known");
                Expect(stale != null && stale.Actions.HasFlag(PopTracker.FindingActions.LinkItem) && stale.Title.Contains("Gone Item"), "a fix to a vanished item isn't reported");
            }
            finally
            {
                PopTracker.PackFixes.Reset(key);
            }
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
