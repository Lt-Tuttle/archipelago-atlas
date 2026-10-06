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
