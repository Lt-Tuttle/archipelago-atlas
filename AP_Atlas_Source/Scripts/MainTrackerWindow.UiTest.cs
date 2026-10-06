using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AP_Atlas.Core.CheeseTracker;
using AP_Atlas.Core.Connections;
using AP_Atlas.Core.EngineSetup;
using AP_Atlas.Core.Testing;
using Godot;
using Newtonsoft.Json.Linq;

/// <summary>
/// The UI test, for testing only. Set ATLAS_UITEST=1 and ATLAS_DATA_DIR=&lt;an empty scratch folder&gt;, then start Atlas
/// (headless is fine). Atlas builds its window as on a first run, then drives it the way a user would, against a fake
/// Archipelago server on this computer, and prints "UITEST PASS/FAIL/SKIP" lines. Logic runs on a fake engine
/// (FakeLogicEngine) with the Python named in ATLAS_UITEST_PYTHON; without one, that scenario is skipped. Exit code: 0
/// nothing failed, 1 something failed, 2 refused (the data folder isn't an empty scratch folder).
/// Tools/run_selftest.ps1 runs it after the self-test (passing the Python on the PATH), and CI runs that.
/// </summary>
public partial class MainTrackerWindow
{
    private static bool UiTestRequested => System.Environment.GetEnvironmentVariable("ATLAS_UITEST") == "1";

    /// <summary>The same reconnect rules as always, with short waits, so the test doesn't take twenty minutes.</summary>
    private static SessionManagerOptions UiTestSessions => new()
    {
        LoginTimeout = TimeSpan.FromSeconds(5),
        ReconnectDelays = new[] { TimeSpan.FromSeconds(0.3), TimeSpan.FromSeconds(0.3) },
        Jitter = 0
    };

    /// <summary>Called first thing in _Ready: refuses (and quits) unless the data folder is an empty scratch folder.</summary>
    private bool UiTestAllowed()
    {
        string? problem = AP_Atlas.Core.SelfTest.ScratchFolderProblem(null);
        if (problem == null) return true;
        GD.PrintErr("UITEST REFUSED: " + problem);
        GetTree().Quit(2);
        return false;
    }

    private void RunUiTest() => AP_Atlas.Core.Async.Fire(RunUiTestAsync(), "running the UI test", tellUser: false);

    private async Task RunUiTestAsync()
    {
        int passed = 0, failed = 0, skipped = 0;
        // For working on one scenario: ATLAS_UITEST_ONLY=<part of its name> runs only the scenarios that match.
        string? only = System.Environment.GetEnvironmentVariable("ATLAS_UITEST_ONLY");
        async Task ScenarioAsync(string name, Func<Task> body)
        {
            if (!string.IsNullOrEmpty(only) && !name.Contains(only, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                GD.Print($"UITEST SKIP {name}: not chosen (ATLAS_UITEST_ONLY)");
                return;
            }
            try
            {
                await body();
                passed++;
                GD.Print("UITEST PASS " + name);
            }
            catch (UiTestSkip skip)
            {
                skipped++;
                GD.Print($"UITEST SKIP {name}: {skip.Message}");
            }
            catch (Exception ex)
            {
                failed++;
                GD.Print($"UITEST FAIL {name}: {ex.Message}");
            }
        }

        await UiTestWaitAsync(1.0); // the window settles, as on a first run
        await ScenarioAsync("Connecting: a slot logs in through the window and gets its view; a dropped connection comes back by itself with a new view; disconnecting closes it for good",
            ConnectingThroughTheWindowAsync);
        await ScenarioAsync("Deleting a multiworld while one of its slots connects: the connection is closed, and no slot is left for it",
            DeletingWhileConnectingLeavesNothingAsync);
        await ScenarioAsync("Ended slots are freed: a slot a dropped connection replaced, and a deleted multiworld's slot, leave nothing that keeps their model, session or view in memory",
            EndedSlotsAreFreedAsync);
        await ScenarioAsync("Ended slots with logic are freed: a deleted multiworld's engine stops, its engine pool is forgotten, and its slot's model and logic leave memory",
            EndedSlotsLogicIsFreedAsync);
        await ScenarioAsync("Moving views: the Cheese and Sphere tabs and Properties keep following their events when moved to another parent (docking, pop-outs), and stop while out of the window",
            ViewsKeepTheirEventsWhenMovedAsync);
        await ScenarioAsync("Tools: every tool's tab shows its own view, and each slot tool the connected slot's view (or asks for a slot when none is connected)",
            EveryToolShowsItsViewAsync);
        await ScenarioAsync("Bursts: 40 items and 40 chat lines arriving together reach the slot once each, at most one update of the window in each frame they arrive over (not 80)",
            BurstIsOneUpdateAsync);
        await ScenarioAsync("Long text: a game's names and a hint's entrance of a megabyte, and a chat line of 100,000 characters without a space, hold up no frame for 150 ms, shown or not (names are cut to 500 characters; a long run of text gets breaks)",
            LongTextHoldsUpNothingAsync);
        await ScenarioAsync("Room text: one connection per multiworld receives the room's text and every slot's text client shows each line once, named from that slot's view; a command typed into a quiet slot gets its answer; new hints show in the slots they concern; when the text slot leaves, another takes over",
            RoomTextReachesEverySlotAsync);
        await ScenarioAsync("Moving a slot's panel: out of the window and docked elsewhere, the slot keeps its connection, views and updates, and shows what arrived meanwhile",
            SlotPanelMovesWholeAsync);
        await ScenarioAsync("Map packs: a slot's pack has its images while the slot is connected (its map shows them), the Pack Doctor's while its window is open; then they're freed once another pack is used",
            PackImagesFollowTheirUsersAsync);
        await ScenarioAsync("Idle: a connected slot doesn't keep Atlas redrawing: its map's camera doesn't run every frame (zooming, dragging and resizing still move the map), and its card isn't re-styled while nothing changes",
            ConnectedSlotLetsAtlasIdleAsync);
        await ScenarioAsync("Map pack scripts: a slot whose pack's script runs away keeps working: the script is stopped in seconds without holding up a frame, and Key Items and the log say why",
            RunawayPackScriptAsync);
        await ScenarioAsync("Text from outside: another player's chat line, a map pack's details and log lines quoting them show markup as written, and Atlas opens no file it names",
            OutsideTextShowsAsWrittenAsync);
        await ScenarioAsync("Race rooms: a room the server calls a race restricts its slots (no \"why\" answers), and the Sphere Tracker hides that multiworld's spheres",
            RaceRoomRestrictsAsync);
        await ScenarioAsync("Logic: the slot's logic follows its items and checks step by step; after an engine crash, an engine update or a restart it's rebuilt from scratch on a new engine; race mode can hide it",
            LogicFollowsTheSlotAsync);
        await ScenarioAsync("Shared engines: a multiworld's slots share its engines; when one slot's request brings an engine down, a slot sharing it (even one still starting) starts again in 2 s, and only the slot whose request it was counts the failure",
            SharedEnginesAsync);
        await ScenarioAsync("Cheese Tracker: its suggestion for a connected slot follows the slot's logic (unblocked, then go mode), says nothing while race mode hides logic, and changes nothing by itself",
            CheeseFollowsTheSlotAsync);
        await ScenarioAsync("Scale: in a 1,000-player room with 20 slots connected (one receiving the room's text), Atlas's work for bursts of items, item lines and hints never holds up a frame for 150 ms (target 100 ms; 250 ms with garbage collection), nor connecting a slot for 300 ms (400 ms with it); the logs keep their last lines",
            ScaleStaysResponsiveAsync);
        await _sessions.CloseAllAsync(TimeSpan.FromSeconds(3));
        GD.Print($"UITEST DONE: {passed} passed, {failed} failed, {skipped} skipped");
        GetTree().Quit(failed == 0 ? 0 : 1);
    }

    private async Task ConnectingThroughTheWindowAsync()
    {
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var slot = new SlotId(profile.Id, "Tester");
        try
        {
            // As the slot's Connect button does.
            await OnConnectSlotPressedAsync("Tester", profile);
            var first = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            UiTestExpect(_sessions.IsLoggedIn(slot), "the slot isn't logged in");
            UiTestExpect(first.Session?.Socket.Connected == true, "the slot's view has no open session");
            UiTestExpect(!_connectingSlots.Contains(SlotKey(profile.Id, "Tester")), "the slot still shows as connecting");

            // The server drops everyone: Atlas reconnects by itself and builds a new view.
            server.DropClients();
            var second = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester") is { } view && view != first && _sessions.IsLoggedIn(slot) ? view : null,
                "the slot to reconnect");
            UiTestExpect(!IsInstanceValid(first) || first.IsQueuedForDeletion(), "the dropped slot's view is still there");
            UiTestExpect(second.Session?.Socket.Connected == true, "the new view has no open session");
            UiTestExpect(server.Count("Connect") == 2, $"{server.Count("Connect")} logins reached the server instead of 2");

            // As the slot's Disconnect button does: the session closes properly, and nothing reconnects it.
            DisconnectSlot(profile.Id, "Tester");
            await UiTestWaitForAsync(() => server.ClosesReceived >= 1 && !_sessions.IsLoggedIn(slot) ? this : null, "the session to close");
            await UiTestWaitAsync(1.0);
            UiTestExpect(server.Count("Connect") == 2 && !_sessions.IsReconnecting(slot), "the slot reconnected after it was disconnected");

            // The sidebar card's Connect button (the card is kept through connecting and disconnecting) connects it again.
            var connectButton = _activeSessionsList.FindChildren("ConnectBtn", "Button", true, false).OfType<Button>()
                .SingleOrDefault(b => b.GetMeta("profile_id").AsString() == profile.Id);
            UiTestExpect(connectButton != null, "the slot has no Connect button in the sidebar");
            connectButton!.EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitForAsync(() => _sessions.IsLoggedIn(slot) && server.Count("Connect") == 3 && SlotView(profile.Id, "Tester") is { } third && third != second ? this : null,
                "the sidebar's Connect button to connect the slot again");
        }
        finally
        {
            DeleteProfile(profile);
        }
    }

    private async Task DeletingWhileConnectingLeavesNothingAsync()
    {
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        server.LoginDelay = TimeSpan.FromSeconds(0.5);
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var slot = new SlotId(profile.Id, "Tester");
        // Connect, and delete the multiworld while the server is still answering the login.
        var connecting = OnConnectSlotPressedAsync("Tester", profile);
        await UiTestWaitForAsync(() => server.Count("Connect") == 1 ? this : null, "the login to reach the server");
        DeleteProfile(profile);
        await connecting;
        await UiTestWaitForAsync(() => server.ClosesReceived >= 1 ? this : null, "the finished login to be closed");
        await UiTestWaitAsync(0.3);
        UiTestExpect(SlotView(profile.Id, "Tester") == null, "a slot was built for the deleted multiworld");
        UiTestExpect(!_sessions.IsLoggedIn(slot), "the deleted multiworld's slot stayed connected");
    }

    /// <summary>
    /// A slot that ends leaves nothing that keeps it in memory: no event, cache, timer, closure or queued work holds its
    /// model, session or view. Multiworlds run for days, so a slot that leaked on every reconnect, or a deleted
    /// multiworld that stayed, would grow Atlas without end. Checked after a dropped connection (its view is replaced)
    /// and after deleting the multiworld.
    /// </summary>
    private async Task EndedSlotsAreFreedAsync()
    {
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var ended = await ConnectDropAndDeleteAsync(server);
        // Their last queued work runs, the freed views go at the end of a frame, and then everything left is collected.
        await UiTestWaitAsync(1.0);
        for (int i = 0; i < 3; i++)
        {
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            await UiTestWaitAsync(0.1);
        }
        foreach (var (what, weak) in ended)
            UiTestExpect(!weak.IsAlive, $"{what} is still in memory after it ended");
    }

    // Apart from the scenario, so only weak references come back: an async method keeps its own locals until it ends.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<List<(string What, System.WeakReference Weak)>> ConnectDropAndDeleteAsync(FakeArchipelagoServer server)
    {
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var ended = new List<(string What, System.WeakReference Weak)>();
        await OnConnectSlotPressedAsync("Tester", profile);
        var first = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
        ShowTextClient(first);
        await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(0, new long[] { 1000 }), server.Chat("before the drop"));
        await UiTestWaitForAsync(() => PanelShows(first, "before the drop") ? first : null, "the slot to show a line");
        ended.Add(("the slot model a dropped connection replaced", new System.WeakReference(first.Model)));
        ended.Add(("the slot view a dropped connection replaced", new System.WeakReference(first)));

        // The server drops everyone: Atlas builds a new view and model for the slot.
        server.DropClients();
        var second = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester") is { } view && view != first ? view : null, "the slot to reconnect");
        ShowTextClient(second);
        ended.Add(("a deleted multiworld's slot model", new System.WeakReference(second.Model)));
        ended.Add(("a deleted multiworld's session", new System.WeakReference(second.Session)));
        ended.Add(("a deleted multiworld's slot view", new System.WeakReference(second)));

        DeleteProfile(profile);
        await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester") == null ? this : null, "the deleted multiworld's slot to go");
        return ended;
    }

    /// <summary>
    /// A deleted multiworld's slot with logic leaves nothing behind: its engine stops (it was the engine's last slot),
    /// its multiworld's engine pool is forgotten, and neither its model nor its logic stays in memory.
    /// </summary>
    private async Task EndedSlotsLogicIsFreedAsync()
    {
        var engine = StartFakeEngine(UiTestPython());
        try
        {
            await using var server = LogicWorldServer();
            var (profileId, ended) = await ConnectLogicAndDeleteAsync(server);
            await UiTestWaitAsync(1.0);
            for (int i = 0; i < 3; i++)
            {
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                await UiTestWaitAsync(0.1);
            }
            foreach (var (what, weak) in ended)
                UiTestExpect(!weak.IsAlive, $"{what} is still in memory after it ended");
            UiTestExpect(EnginePools.CountFor(profileId) == 0, "the deleted multiworld's engine pool is still kept");
            var running = engine.StartedPids.Where(UiTestProcessRuns).ToList();
            UiTestExpect(running.Count == 0, $"the deleted multiworld's engine is still running (process {string.Join(", ", running)})");
        }
        finally
        {
            AtlasEngine.TestPython = null;
        }
    }

    // Apart from the scenario, so only weak references come back (see ConnectDropAndDeleteAsync).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<(string ProfileId, List<(string What, System.WeakReference Weak)> Ended)> ConnectLogicAndDeleteAsync(FakeArchipelagoServer server)
    {
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        await OnConnectSlotPressedAsync("Tester", profile);
        var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
        await UiTestWaitForAsync(() => slot.LogicSettled && slot.IsLocationInLogic(2000) == true ? slot : null, "the slot's logic to start");
        UiTestExpect(EnginePools.CountFor(profile.Id) == 1, "the slot's multiworld has no engine pool");
        var ended = new List<(string What, System.WeakReference Weak)>
        {
            ("a deleted multiworld's slot model, with logic", new System.WeakReference(slot.Model)),
            ("its slot's logic", new System.WeakReference(slot.Model.Logic)),
        };
        DeleteProfile(profile);
        await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester") == null ? this : null, "the deleted multiworld's slot to go");
        return (profile.Id, ended);
    }

    private static bool UiTestProcessRuns(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; } // no such process: it ended
    }

    private async Task ViewsKeepTheirEventsWhenMovedAsync()
    {
        // Who listens to the events these views follow, counted from the events themselves.
        var events = new (string Name, Func<int> Listeners)[]
        {
            ("Cheese Tracker changes", () => UiTestListeners(typeof(AP_Atlas.Core.CheeseTracker.CheeseTrackerService), _cheese, "Changed")),
            ("Cheese Tracker links", () => UiTestListeners(typeof(AP_Atlas.Core.CheeseTracker.CheeseTrackerService), _cheese, "LinkChanged")),
            ("Sphere Tracker changes", () => UiTestListeners(typeof(AP_Atlas.Core.Spheres.SphereService), _spheres, "Changed")),
            ("race mode changes", () => UiTestListeners(typeof(AP_Atlas.Core.RaceRules), null, "Changed")),
            ("Properties requests", () => UiTestListeners(typeof(AP_Atlas.Core.Inspector), null, "Requested")),
            ("notes and flags changes", () => UiTestListeners(typeof(AP_Atlas.Core.Annotations), null, "Changed")),
        };
        int[] Counts() => events.Select(e => e.Listeners()).ToArray();
        void ExpectCounts(int[] expected, string when)
        {
            var now = Counts();
            for (int i = 0; i < events.Length; i++)
                UiTestExpect(now[i] == expected[i], $"{events[i].Name} have {now[i]} listeners {when}, expected {expected[i]}");
        }

        var elsewhere = new VBoxContainer { Name = "UiTestElsewhere" };
        AddChild(elsewhere);
        var profile = new MultiworldProfile { Name = "UI test" };
        _profiles.Add(profile);
        try
        {
            var before = Counts();
            UiTestExpect(UiTestListeners(typeof(AP_Atlas.Core.Inspector), null, "Requested") == 1, "Properties isn't the one listener for requests");
            // Each view and the events it follows: while it's out of the window, exactly those lose one listener.
            var views = new (Control View, string[] Follows)[]
            {
                (_cheeseTab, new[] { "Cheese Tracker changes", "Cheese Tracker links" }),
                (_sphereTab, new[] { "Sphere Tracker changes", "race mode changes" }),
                (_propertiesPanel, new[] { "Properties requests", "notes and flags changes" }),
            };
            foreach (var (view, follows) in views)
            {
                var home = view.GetParent();
                int place = view.GetIndex();
                home.RemoveChild(view);
                ExpectCounts(before.Select((count, i) => follows.Contains(events[i].Name) ? count - 1 : count).ToArray(), $"while {view.Name} is out of the window");
                elsewhere.AddChild(view);
                ExpectCounts(before, $"after {view.Name} moved");
                elsewhere.RemoveChild(view);
                home.AddChild(view);
                home.MoveChild(view, place);
                ExpectCounts(before, $"after {view.Name} moved back");
            }

            // And it really answers: Properties, moved away, still shows what's asked for.
            var panelHome = _propertiesPanel.GetParent();
            int panelPlace = _propertiesPanel.GetIndex();
            panelHome.RemoveChild(_propertiesPanel);
            elsewhere.AddChild(_propertiesPanel);
            try
            {
                var target = AP_Atlas.Core.InspectTarget.ForProfile(profile.Id);
                AP_Atlas.Core.Inspector.Inspect(target);
                await UiTestWaitAsync(0.1);
                UiTestExpect(_propertiesPanel.Current?.Key == target.Key, "Properties didn't follow a request after it was moved");
            }
            finally
            {
                elsewhere.RemoveChild(_propertiesPanel);
                panelHome.AddChild(_propertiesPanel);
                panelHome.MoveChild(_propertiesPanel, panelPlace);
            }
        }
        finally
        {
            DeleteProfile(profile);
            elsewhere.QueueFree();
        }
    }

    /// <summary>How many handlers an event has, read from the event's own field (an instance event, or a static one when <paramref name="owner"/> is null).</summary>
    private static int UiTestListeners(Type type, object? owner, string eventName)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | (owner == null ? System.Reflection.BindingFlags.Static : System.Reflection.BindingFlags.Instance);
        var field = type.GetField(eventName, flags) ?? throw new InvalidOperationException($"{type.Name}.{eventName} isn't an event Atlas can count");
        return (field.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private async Task EveryToolShowsItsViewAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        // What each tool must show, written out here (not read from the tool list), so a wrong entry there can't pass.
        var ownViews = new Dictionary<AP_Atlas.UI.Tool, Control>
        {
            [AP_Atlas.UI.Tool.Connections] = _connectionPanel,
            [AP_Atlas.UI.Tool.MapPacks] = _packManagerPanel,
            [AP_Atlas.UI.Tool.CheeseTracker] = _cheeseTab,
            [AP_Atlas.UI.Tool.SphereTracker] = _sphereTab,
        };
        var slotViews = new Dictionary<AP_Atlas.UI.Tool, Func<SlotTrackerControl, Control>>
        {
            [AP_Atlas.UI.Tool.MapTracker] = s => s.MapTracker,
            [AP_Atlas.UI.Tool.KeyItems] = s => s.ProgressionTracker,
            [AP_Atlas.UI.Tool.LogicTracker] = s => s.LogicTrackerView,
            [AP_Atlas.UI.Tool.ItemHistory] = s => s.ItemHistoryView,
            [AP_Atlas.UI.Tool.Hints] = s => s.HintsView,
        };
        UiTestExpect(ownViews.Count + slotViews.Count == AP_Atlas.UI.Tool.All.Count, "a tool is missing from this test's table");

        // Atlas opens on its welcome page with Connections as the current tab, so start from another tab, as a user would.
        host.ShowTool(AP_Atlas.UI.Tool.MapPacks);
        // No slot connected: the tools that aren't per slot show their own view, and the slot tools ask for a slot.
        foreach (var tool in AP_Atlas.UI.Tool.All)
        {
            host.ShowTool(tool);
            await UiTestWaitAsync(0.05);
            UiTestExpect(_workspaceSwitcher.CurrentTab == tool.Index, $"the tab bar isn't on {tool.Title}");
            Control expected = ownViews.TryGetValue(tool, out var own) ? own : _noSlotPlaceholder;
            UiTestExpect(ShownContent() == expected, $"{tool.Title} shows {ShownContent()?.Name ?? "nothing"} without a slot");
        }

        // A connected slot: each slot tool shows that slot's view of it.
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            foreach (var (tool, view) in slotViews)
            {
                host.ShowTool(tool);
                await UiTestWaitAsync(0.05);
                UiTestExpect(ShownContent() == view(slot), $"{tool.Title} doesn't show the connected slot's view");
            }
        }
        finally
        {
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
        }
    }

    private async Task LongTextHoldsUpNothingAsync()
    {
        // Text from outside that took minutes to lay out: a game's item and location named with a megabyte each (an apworld's
        // names reach Atlas as the server sends them), a hint whose entrance is a megabyte, and a chat line of 100,000
        // characters without a space. They arrive while the text client isn't showing, laid out in a narrow column, which
        // is where a long run of text was slowest; then every view, and the text client, show them.
        string longItem = "Sword of " + new string('x', 1_000_000), longLocation = "Cave of " + new string('y', 1_000_000);
        await using var server = new FakeArchipelagoServer();
        // Its own checksum: a game version's names are kept by checksum, and the other scenarios' Test Game has other names.
        server.Games["Test Game"] = new FakeGame("10a6a6e510a6a6e510a6a6e510a6a6e510a6a6e5",
            new Dictionary<string, long> { [longItem] = 1000, ["Shield"] = 1001 }, new Dictionary<string, long> { [longLocation] = 2000, ["Chest"] = 2001 });
        var profile = new MultiworldProfile { Name = "UI test (long text)", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            await UiTestWaitAsync(0.5);
            AP_Atlas.Core.HitchMonitor.ResetWorst();
            // One chat line without a space, one of ordinary words: laid out with Godot's "word smart" wrapping, 8,000
            // characters of either took about 5 seconds.
            string longLine = new string('w', 100_000), longWords = string.Concat(Enumerable.Repeat("words ", 20_000));
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(0, new[] { 1000L }));
            await server.BroadcastAsync(FakeArchipelagoServer.ItemSend(1, 1, 1000, 2000));
            await server.AddHintsAsync(FakeArchipelagoServer.Hint(1, 1, 2000, 1000, entrance: "Gate of " + new string('z', 1_000_000)));
            await server.BroadcastAsync(server.Chat(longLine), server.Chat(longWords));
            await UiTestWaitForAsync(() => slot.Session.Items.AllItemsReceived.Count == 1 && slot.Model.CurrentHints?.Length == 1 &&
                slot.ChatHistory.Any(e => e.APMessage?.ToString().Contains(longLine[..1000]) == true) &&
                slot.ChatHistory.Any(e => e.APMessage?.ToString().Contains(longWords[..1000]) == true) ? slot : null, "the item, the hint and the chat lines");
            await UiTestWaitAsync(0.5);
            var steps = new List<(string What, double WorkMs, string Report)> { ("arriving", AP_Atlas.Core.HitchMonitor.WorstWorkMs, AP_Atlas.Core.HitchMonitor.WorstWorkReport) };

            // The names Atlas uses are cut, and stay apart: the start of the name, "…" and a hash.
            int max = AP_Atlas.Core.Connections.NameLimits.MaxName;
            string? item = slot.Session.Items.GetItemName(1000, "Test Game"), location = slot.Session.Locations.GetLocationNameFromId(2000, "Test Game");
            string? entrance = slot.Model.CurrentHints?[0].Entrance;
            UiTestExpect(item?.Length == max && item.StartsWith("Sword of xxx", StringComparison.Ordinal) && location?.Length == max &&
                location.StartsWith("Cave of yyy", StringComparison.Ordinal) && entrance?.Length == max && entrance.StartsWith("Gate of zzz", StringComparison.Ordinal),
                $"names from outside weren't cut to {max} characters: the item's has {item?.Length}, the location's {location?.Length}, the entrance's {entrance?.Length}");

            foreach (var tool in new[] { AP_Atlas.UI.Tool.ItemHistory, AP_Atlas.UI.Tool.Hints, AP_Atlas.UI.Tool.LogicTracker, AP_Atlas.UI.Tool.KeyItems, AP_Atlas.UI.Tool.MapTracker })
            {
                AP_Atlas.Core.HitchMonitor.ResetWorst();
                host.ShowTool(tool);
                await UiTestWaitAsync(0.5);
                steps.Add((tool.Title, AP_Atlas.Core.HitchMonitor.WorstWorkMs, AP_Atlas.Core.HitchMonitor.WorstWorkReport));
            }
            AP_Atlas.Core.HitchMonitor.ResetWorst();
            ShowTextClient(slot);
            await UiTestWaitAsync(0.5);
            steps.Add(("the text client", AP_Atlas.Core.HitchMonitor.WorstWorkMs, AP_Atlas.Core.HitchMonitor.WorstWorkReport));
            GD.Print("UITEST INFO Long text: the worst frame's work " + string.Join(", ", steps.Select(s => $"{s.What} {s.WorkMs:0} ms")));
            var slowest = steps.MaxBy(s => s.WorkMs);
            UiTestExpect(slowest.WorkMs < 150, $"a frame's work took {slowest.WorkMs:0} ms ({slowest.What}), garbage collection left out (the guard is 150 ms): {slowest.Report}");
        }
        finally
        {
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
        }
    }

    private async Task BurstIsOneUpdateAsync()
    {
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            // The room's join message arrives just after the login: the model hands it to the chat.
            await UiTestWaitForAsync(() => slot.ChatHistory.Any(e => e.APMessage?.ToString().Contains("has joined") == true) ? slot : null,
                "the join message in the slot's chat");
            await UiTestWaitAsync(0.3); // the connection's own updates settle

            int updates = 0;
            void Counted() => updates++;
            // When the burst's packets reach the slot (on the connection's thread), and when frames start: a slow machine takes
            // several frames to unpack a burst, and the window may update once in each of them.
            long firstArrival = 0, lastArrival = 0;
            void Arrived()
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                System.Threading.Interlocked.CompareExchange(ref firstArrival, now, 0);
                System.Threading.Interlocked.Exchange(ref lastArrival, now);
            }
            void ItemArrived(Archipelago.MultiClient.Net.Helpers.ReceivedItemsHelper helper) => Arrived();
            void LineArrived(Archipelago.MultiClient.Net.MessageLog.Messages.LogMessage message) => Arrived();
            var frameStarts = new List<long>();
            void FrameStarted() => frameStarts.Add(System.Diagnostics.Stopwatch.GetTimestamp());
            slot.StateChanged += Counted;
            slot.Session.Items.ItemReceived += ItemArrived;
            slot.Session.MessageLog.OnMessageReceived += LineArrived;
            GetTree().ProcessFrame += FrameStarted;
            try
            {
                long before = slot.ChatHistory.Count == 0 ? 0 : slot.ChatHistory[^1].Sequence;
                var burst = new List<JObject> { FakeArchipelagoServer.ReceivedItems(0, Enumerable.Range(0, 40).Select(i => 1000L + i)) };
                burst.AddRange(Enumerable.Range(1, 40).Select(i => server.Chat($"burst line {i}")));
                await server.BroadcastAsync(burst.ToArray());

                int BurstLines() => slot.ChatHistory.Count(e => e.Sequence > before && e.APMessage?.ToString().Contains("burst line") == true);
                await UiTestWaitForAsync(() => slot.Session.Items.AllItemsReceived.Count == 40 && BurstLines() == 40 ? slot : null, "the burst to arrive");
                await UiTestWaitAsync(0.3);
                // At most one update in each frame the burst arrived over, and two more for what it set off: a burst that
                // arrives within one frame (as on the development PC) is at most 3 updates, where one per item would be 80.
                long first = System.Threading.Interlocked.Read(ref firstArrival), last = System.Threading.Interlocked.Read(ref lastArrival);
                int arrivalFrames = 1 + frameStarts.Count(start => start > first && start <= last);
                UiTestExpect(updates >= 1, "the burst never reached the window");
                UiTestExpect(updates <= arrivalFrames + 2, $"40 items and 40 lines arriving together (over {arrivalFrames} frame{(arrivalFrames == 1 ? "" : "s")}) caused {updates} updates of the window");
                var lines = slot.ChatHistory.Where(e => e.Sequence > before && e.APMessage?.ToString().Contains("burst line") == true)
                    .Select(e => e.APMessage!.ToString()).ToList();
                UiTestExpect(lines.Distinct().Count() == 40 && lines.Count == 40, "a burst line is missing or shown twice");
            }
            finally
            {
                slot.StateChanged -= Counted;
                slot.Session.Items.ItemReceived -= ItemArrived;
                slot.Session.MessageLog.OnMessageReceived -= LineArrived;
                GetTree().ProcessFrame -= FrameStarted;
            }
        }
        finally
        {
            DeleteProfile(profile);
        }
    }

    private async Task RoomTextReachesEverySlotAsync()
    {
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        server.Slots.Clear();
        server.Slots.AddRange(new[] { "Tester", "Second", "Third" });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.AddRange(server.Slots);
        _profiles.Add(profile);
        try
        {
            foreach (string name in profile.Slots) await OnConnectSlotPressedAsync(name, profile);
            var slots = new List<SlotTrackerControl>();
            foreach (string name in profile.Slots) slots.Add(await UiTestWaitForAsync(() => SlotView(profile.Id, name), $"{name}'s view"));
            var (tester, second, third) = (slots[0], slots[1], slots[2]);
            UiTestExpect(server.TextClients == 1, $"{server.TextClients} connections receive the room's text, not 1");
            UiTestExpect(tester.Model.ReceivesText && !second.Model.ReceivesText && !third.Model.ReceivesText, "the first slot isn't the only one receiving the room's text");
            int Lines(SlotTrackerControl slot, string text) => slot.ChatHistory.Count(e => e.APMessage?.ToString().Contains(text) == true);
            // Each slot's join line reached it (Second's and Third's through Tester's connection).
            foreach (var slot in slots)
                await UiTestWaitForAsync(() => Lines(slot, $"{slot.SlotName} (Team #1) tracking Test Game has joined.") == 1 ? slot : null, $"{slot.SlotName}'s join line in its text client");

            // A room line, once in every slot, its players named from each slot's own view: in Third's, Third stands out.
            await server.BroadcastAsync(FakeArchipelagoServer.ItemSend(2, 3, 1000, 2000));
            await UiTestWaitForAsync(() => slots.All(s => Lines(s, "Second sent Sword to Third (Cave Chest)") == 1) ? slots : null, "the item line in every slot's text client");
            ShowTextClient(third);
            await UiTestWaitForAsync(() => PanelShows(third, "[color=magenta]Third[/color]") && PanelShows(third, "[color=yellow]Second[/color]") ? third : null,
                "Third's own name to stand out in its text client");
            // A goal line counts for the slot whose goal it is, not the slot whose connection heard it.
            await server.BroadcastAsync(server.Goal(3));
            await UiTestWaitForAsync(() => third.Model.GoalCompleted ? third : null, "Third to reach its goal");
            await UiTestWaitAsync(0.3);
            UiTestExpect(!tester.Model.GoalCompleted && !second.Model.GoalCompleted, "another slot's goal line counted as this slot's");

            // New hints. Tester's world holds Third's Sword: Tester (with text) gets the hint's line; Third (without) shows
            // it from its hint list, worded and coloured as the server's line; Second isn't concerned.
            int HintLines(SlotTrackerControl slot) => slot.ChatHistory.Count(e => e.Hint != null || e.APMessage is Archipelago.MultiClient.Net.MessageLog.Messages.HintItemSendLogMessage);
            await server.AddHintsAsync(FakeArchipelagoServer.Hint(finder: 1, receiver: 3, location: 2000, item: 1000, status: 30));
            await UiTestWaitForAsync(() => HintLines(tester) == 1 && HintLines(third) == 1 ? slots : null, "the hint in Tester's and Third's text clients");
            UiTestExpect(third.ChatHistory.Count(e => e.Hint != null) == 1 && tester.ChatHistory.Count(e => e.Hint != null) == 0, "the hint wasn't shown from the hint list in Third alone");
            await UiTestWaitForAsync(() => PanelShowsText(third, "[Hint]: Third's Sword is at Cave Chest in Tester's World. (priority)") &&
                PanelShows(third, "[color=plum](priority)[/color]") ? third : null, "the hint, worded and coloured as the server's lines, in Third's text client");
            // The server's own line has its status coloured too.
            ShowTextClient(tester);
            await UiTestWaitForAsync(() => PanelShows(tester, "[color=plum](priority)[/color]") ? tester : null, "the hint's status in its colour in Tester's text client");

            // A command typed into a quiet slot's text client: its connection gets text first, so the answer shows there, only.
            var input = third.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(l => l.PlaceholderText.StartsWith("Type a command"));
            input.Text = "!hint Sword";
            input.EmitSignal(LineEdit.SignalName.TextSubmitted, input.Text);
            await UiTestWaitForAsync(() => Lines(third, "Command received: !hint Sword") == 1 ? third : null, "the command's answer in Third's text client");
            UiTestExpect(server.TagChanges == 1 && third.Model.ReceivesText, $"Third's text wasn't switched on, once ({server.TagChanges} tag changes)");
            await UiTestWaitForAsync(() => slots.All(s => Lines(s, "Third: !hint Sword") == 1) ? slots : null, "the command, as chat, in every slot's text client");
            UiTestExpect(Lines(tester, "Command received") == 0 && Lines(second, "Command received") == 0, "the command's answer reached other slots");
            // Third receives text now: its next new hint comes as the server's line, shown once.
            await server.AddHintsAsync(FakeArchipelagoServer.Hint(finder: 3, receiver: 3, location: 2000, item: 1000));
            await UiTestWaitForAsync(() => HintLines(third) == 2 ? third : null, "Third's own new hint in its text client");
            await UiTestWaitAsync(0.3);
            UiTestExpect(third.ChatHistory.Count(e => e.Hint != null) == 1 && HintLines(third) == 2, "Third, which receives text now, showed its new hint twice");
            UiTestExpect(HintLines(tester) == 1 && HintLines(second) == 0, "a hint reached a slot it doesn't concern");

            // The text slot disconnects. Third, which receives text already, takes over (not Second, before it in order), so
            // the room is told nothing more.
            DisconnectSlot(profile.Id, "Tester");
            await UiTestWaitForAsync(() => !_sessions.IsLoggedIn(new SlotId(profile.Id, "Tester")) ? this : null, "Tester to disconnect");
            await server.BroadcastAsync(server.Chat("after Tester left"));
            await UiTestWaitForAsync(() => Lines(second, "after Tester left") == 1 && Lines(third, "after Tester left") == 1 ? slots : null, "the room's lines to keep reaching the other slots");
            UiTestExpect(server.TextClients == 1 && server.TagChanges == 1 && !second.Model.ReceivesText,
                $"after Tester left, {server.TextClients} connections receive text, tags changed {server.TagChanges} times, and Second receives text: {second.Model.ReceivesText}");
        }
        finally
        {
            DeleteProfile(profile);
        }
    }

    /// <summary>Shows a slot's text client in the bottom pane, as clicking its card on the Chat tab does.</summary>
    private void ShowTextClient(SlotTrackerControl slot)
    {
        _bottomTabs.CurrentTab = 0;
        _currentTerminalTab = 0;
        _currentSelectedSlot = slot;
        RefreshContextViews();
    }

    private async Task SlotPanelMovesWholeAsync()
    {
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var elsewhere = new VBoxContainer { Name = "UiTestDock" };
        AddChild(elsewhere);
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            var slotId = new SlotId(profile.Id, "Tester");
            var views = new Control[] { slot.MapTracker, slot.ProgressionTracker, slot.LogicTrackerView, slot.ItemHistoryView, slot.HintsView };
            var home = slot.GetParent();
            int place = slot.GetIndex();

            // Out of the window: a burst arrives meanwhile. The slot takes it; the panel draws it once it shows again.
            home.RemoveChild(slot);
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(0, new long[] { 1000, 1000, 1000 }), server.Chat("while the panel was moved"));
            await UiTestWaitForAsync(() => slot.Session.Items.AllItemsReceived.Count == 3 &&
                slot.ChatHistory.Any(e => e.APMessage?.ToString().Contains("while the panel was moved") == true) ? slot : null,
                "the burst to reach the slot while its panel was out of the window");
            // Docked somewhere else.
            elsewhere.AddChild(slot);
            await UiTestWaitForAsync(() => PanelShows(slot, "while the panel was moved") ? slot : null, "the docked panel to show what arrived while it was out of the window");
            await UiTestWaitAsync(0.1);
            UiTestExpect(!slot.Ended && _sessions.IsLoggedIn(slotId), "moving the panel ended the slot or its connection");
            UiTestExpect(views.All(v => v != null && IsInstanceValid(v) && !v.IsQueuedForDeletion()), "moving the panel freed the slot's views");
            // The window still counts it among its slots (the sidebar, the tools), and its services still read it.
            UiTestExpect(SlotView(profile.Id, "Tester") == slot, "the window lost the slot while its panel was elsewhere");
            UiTestExpect(SlotModels().Contains(slot.Model), "the services lost the slot while its panel was elsewhere");
            // It keeps updating where it is now, and after going back.
            await server.BroadcastAsync(server.Chat("after the move"));
            await UiTestWaitForAsync(() => PanelShows(slot, "after the move") ? slot : null, "the moved panel to keep updating");
            elsewhere.RemoveChild(slot);
            home.AddChild(slot);
            home.MoveChild(slot, place);
            await server.BroadcastAsync(server.Chat("back home"));
            await UiTestWaitForAsync(() => PanelShows(slot, "back home") ? slot : null, "the panel to keep updating after moving back");
        }
        finally
        {
            elsewhere.QueueFree();
            DeleteProfile(profile);
        }
    }

    /// <summary>
    /// Text from outside Atlas that names a file in markup (another player's chat line, a map pack's details, a log line
    /// quoting either) shows as written, and Atlas opens no file. Godot would open the path's .remap first, which this
    /// test plants: reading it shows in the log (the resource it names can't be loaded).
    /// </summary>
    private async Task OutsideTextShowsAsWrittenAsync()
    {
        string dir = System.IO.Path.Combine(DataManager.GetDataDirectory(), "markup_probe");
        System.IO.Directory.CreateDirectory(dir);
        string id = System.Guid.NewGuid().ToString("N")[..8];
        string png = System.IO.Path.Combine(dir, "probe.png").Replace('\\', '/');
        await System.IO.File.WriteAllTextAsync(png + ".remap", "[remap]\npath=\"res://atlas_probe_" + id + ".tres\"\n");
        // A tag that opens a file (which SafeRichText stops on its own), and tags of Atlas's own (which only escaping stops):
        // the text shows exactly as written only if it was escaped too.
        string hostile = "[img]" + png + "[/img] [b]bold[/b] [url=0]a link[/url]";
        string log = System.IO.Path.Combine(DataManager.GetDataDirectory(), "logs", "atlas_log.txt");
        bool Opened() => System.IO.File.Exists(log) && System.IO.File.ReadAllText(log).Contains("atlas_probe_" + id);

        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            ShowTextClient(slot);

            // Another player's chat line.
            await server.BroadcastAsync(server.Chat("look at " + hostile));
            await UiTestWaitForAsync(() => PanelShowsText(this, "look at " + hostile) ? slot : null, "the chat line to show as written");

            // A chat line of a megabyte: shown cut at MaxChatLine, saying how much (the fake server's line is "{slot}: {message}").
            string longLine = new string('w', 1_000_000);
            await server.BroadcastAsync(server.Chat(longLine));
            int cut = server.Slots[0].Length + 2 + longLine.Length - SlotTrackerControl.MaxChatLine;
            await UiTestWaitForAsync(() => PanelShowsText(this, $"… ({cut:N0} more characters)") ? slot : null, "the megabyte chat line to show cut");

            // The System Log and the Debug Log, quoting it.
            LogToSystem("A line quoting " + hostile, "orange");
            LogToDebug("A debug line quoting " + hostile, "Tester");
            await UiTestWaitForAsync(() => _consoleOutput.GetParsedText().Replace(ZeroWidthSpace, "").Contains("A line quoting " + hostile) &&
                _debugLogConsole.GetParsedText().Replace(ZeroWidthSpace, "").Contains("A debug line quoting " + hostile) ? slot : null, "the logs to show the lines as written");

            // A map pack's details: its manifest is the pack author's text.
            string pack = System.IO.Path.Combine(dir, "uitest_markup_pack.zip");
            FakeMapPack.Write(pack, "UI test markup pack", "Game " + hostile);
            _packManagerPanel.ShowPackDetails(pack);
            UiTestExpect(PanelShowsText(_packManagerPanel, "Game: Game " + hostile), "the pack's details don't show its game as written");

            await UiTestWaitAsync(0.1);
            UiTestExpect(!Opened(), "Atlas opened a file named in text from outside");
        }
        finally
        {
            DeleteProfile(profile);
        }
    }

    private async Task PackImagesFollowTheirUsersAsync()
    {
        string packs = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory();
        string zip = System.IO.Path.Combine(packs, "uitest_pack.zip"), other = System.IO.Path.Combine(packs, "uitest_other_pack.zip");
        FakeMapPack.Write(zip, "UI test pack", "Test Game");
        FakeMapPack.Write(other, "UI test other pack", "Another Game");
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            var pack = await UiTestWaitForAsync(() => slot.Pack, "the slot's map pack");
            UiTestExpect(AP_Atlas.Core.PopTracker.PackImages.UsersOf(pack) == 1 && pack.ImagesLoaded, "the connected slot doesn't use its pack's images");
            // The map shows its background.
            ShowTextClient(slot);
            host.ShowTool(AP_Atlas.UI.Tool.MapTracker);
            await UiTestWaitForAsync(() => slot.MapTracker.FindChildren("*", "Sprite2D", true, false).OfType<Sprite2D>().FirstOrDefault(s => s.Texture != null), "the map's background on the Map Tracker");

            // The Pack Doctor window uses the pack while it's open.
            OpenPackDoctor(zip);
            var window = await UiTestWaitForAsync(() => GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>().FirstOrDefault(), "the Pack Doctor window");
            await UiTestWaitForAsync(() => AP_Atlas.Core.PopTracker.PackImages.UsersOf(pack) == 2 && pack.ImagesLoaded ? window : null, "the Pack Doctor window to use the pack's images");
            await UiTestWaitAsync(0.3); // the window takes the use on the main thread, after the images are decoded
            window.EmitSignal(Window.SignalName.CloseRequested);
            await UiTestWaitForAsync(() => AP_Atlas.Core.PopTracker.PackImages.UsersOf(pack) == 1 ? window : null, "the closed Pack Doctor window to stop using the pack");

            // The slot ends: the pack keeps its images (the last one released) until another pack is used.
            DeleteProfile(profile);
            await UiTestWaitForAsync(() => slot.Ended ? slot : null, "the slot to end");
            UiTestExpect(AP_Atlas.Core.PopTracker.PackImages.UsersOf(pack) == 0 && pack.ImagesLoaded, "the ended slot still uses its pack, or its pack lost its images too soon");
            var otherPack = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(other) ?? throw new InvalidOperationException("the other test pack wasn't read");
            using (AP_Atlas.Core.PopTracker.PackImages.Use(otherPack)) { }
            UiTestExpect(!pack.ImagesLoaded, "the ended slot's pack kept its images after another pack was used");
        }
        finally
        {
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            foreach (string file in new[] { zip, other }) AP_Atlas.Core.SafeFile.Delete(file);
        }
    }

    private async Task ConnectedSlotLetsAtlasIdleAsync()
    {
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_idle_pack.zip");
        FakeMapPack.Write(zip, "UI test pack", "Test Game");
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            ShowTextClient(slot);
            host.ShowTool(AP_Atlas.UI.Tool.MapTracker);
            await UiTestWaitForAsync(() => slot.MapTracker.FindChildren("*", "Sprite2D", true, false).OfType<Sprite2D>().FirstOrDefault(s => s.Texture != null), "the map on the Map Tracker");
            // The map's camera doesn't run every frame (it would keep Godot redrawing the window all the time), yet the
            // mouse wheel still zooms the map and dragging still moves it.
            var camera = slot.MapTracker.FindChildren("*", "Camera2D", true, false).OfType<Camera2D>().Single();
            var mapView = slot.MapTracker.FindChildren("*", "SubViewportContainer", true, false).OfType<SubViewportContainer>().Single();
            var mapViewport = (SubViewport)camera.GetViewport();
            UiTestExpect(!camera.CanProcess(), "the map's camera runs every frame, so Atlas never idles while a slot is connected");
            // What the map shows follows the camera: centered on its position, at its zoom.
            string? Follows()
            {
                var shown = mapViewport.CanvasTransform;
                var expected = (Vector2)mapViewport.Size / 2 - camera.Position * camera.Zoom;
                return Mathf.IsEqualApprox(shown.X.X, camera.Zoom.X) && shown.Origin.DistanceTo(expected) < 0.5f ? null : $"the map shows {shown} for a camera at {camera.Position}, zoom {camera.Zoom.X}";
            }
            await UiTestWaitAsync(0.3); // the view settles in its place
            float zoomBefore = camera.Zoom.X;
            mapView.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true });
            UiTestExpect(Mathf.IsEqualApprox(camera.Zoom.X, zoomBefore * 1.1f) && Follows() == null, $"zooming in: {Follows()}");
            var positionBefore = camera.Position;
            mapView.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = new Vector2(100, 100) });
            mapView.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { Position = new Vector2(130, 100) });
            mapView.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = new Vector2(130, 100) });
            await UiTestWaitAsync(0.1); // a moved node's new place reaches the camera at the frame's end
            UiTestExpect(Mathf.IsEqualApprox(camera.Position.X, positionBefore.X - 30f / camera.Zoom.X, 0.01f) && Follows() == null, $"dragging 30 px: {Follows() ?? $"the camera moved from {positionBefore} to {camera.Position}"}");
            mapViewport.Size += new Vector2I(40, 20);
            UiTestExpect(Follows() == null, $"a new view size: {Follows()}");
            // The slot's card refreshes its statuses twice a second. Once the slot has settled (its statuses can still change
            // just after it connects), nothing changes and its labels aren't re-styled: each restyle redraws the window, so
            // Atlas would never idle.
            int restyled = 0;
            void Restyled() => restyled++;
            var cardLabels = _activeSessionsList.FindChildren("*", "Label", true, false).OfType<Label>().ToList();
            UiTestExpect(cardLabels.Count > 0, "the slot has no card in the sidebar");
            foreach (var label in cardLabels) label.ThemeChanged += Restyled;
            try
            {
                var settling = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    restyled = 0;
                    await UiTestWaitAsync(1.2); // a little over two status refreshes
                    if (restyled == 0) break;
                    UiTestExpect(settling.Elapsed.TotalSeconds < 15, $"the slot card's labels were still being re-styled after 15 s ({restyled} times in the last 1.2 s)");
                }
            }
            finally
            {
                foreach (var label in cardLabels) label.ThemeChanged -= Restyled;
            }
        }
        finally
        {
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
            AP_Atlas.Core.SafeFile.Delete(zip);
        }
    }

    private async Task RunawayPackScriptAsync()
    {
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_runaway_pack.zip");
        FakeMapPack.Write(zip, "UI test runaway pack", "Test Game", initLua: """
            Archipelago:AddItemHandler("test", function(index, item_id, item_name, player_number)
                if item_id == 1001 then
                    while true do end
                end
                local sword = Tracker:FindObjectForCode("sword")
                sword.AcquiredCount = sword.AcquiredCount + 1
            end)
            """);
        await using var server = new FakeArchipelagoServer();
        // Its own checksum: other scenarios' Test Game (the same checksum, so the same names) has no Cursed Gem.
        server.Games["Test Game"] = new FakeGame("feedfacefeedfacefeedfacefeedfacefeedface",
            new Dictionary<string, long> { ["Sword"] = 1000, ["Cursed Gem"] = 1001 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var warnings = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Logged(string line, string level) => warnings.Enqueue(line);
        AP_Atlas.Core.Logger.OnLogMessage += Logged;
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            ShowTextClient(slot);
            host.ShowTool(AP_Atlas.UI.Tool.KeyItems);
            await UiTestWaitForAsync(() => slot.PackScripts, "the pack's scripts to start");
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(0, new long[] { 1000 }));
            await UiTestWaitForAsync(() => slot.ScriptStateOf("sword")?.Count == 1 ? slot : null, "the scripts to count the sword");

            // The cursed gem sends the item handler into a loop, on the scripts' own thread: the window keeps drawing, and
            // the limits stop it in seconds.
            AP_Atlas.Core.HitchMonitor.ResetWorst();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(1, new long[] { 1001 }));
            string note = await UiTestWaitForAsync(() => slot.ProgressionTracker.ShownScriptNote, "Key Items to say the pack's scripts were stopped", seconds: 60);
            double worst = AP_Atlas.Core.HitchMonitor.WorstFrameMs;
            GD.Print($"UITEST INFO Runaway script: stopped and shown in {clock.Elapsed.TotalSeconds:0.0} s; the worst frame took {worst:0} ms");
            UiTestExpect(note.Contains("scripts were stopped: the item handler (for Cursed Gem) was still running after") && note.Contains("stuck in a loop"),
                $"Key Items says: {note}");
            // The window's line (BBCode) shows the message as written: its "[" escaped.
            UiTestExpect(warnings.Any(line => line.Contains(AP_Atlas.Core.Bbcode.Escape("[Tester] The map pack's scripts were stopped: the item handler (for Cursed Gem)"))),
                "the log doesn't say the pack's scripts were stopped");
            UiTestExpect(worst < 150, $"a frame took {worst:0} ms while the script ran away: {AP_Atlas.Core.HitchMonitor.WorstFrameReport}");

            // Properties says why the seed settings are gone.
            AP_Atlas.Core.Inspector.Inspect(AP_Atlas.Core.InspectTarget.ForSlot(profile.Id, "Tester"));
            await UiTestWaitForAsync(() => PanelShowsText(_propertiesPanel, "The map pack's scripts were stopped, so they can't show this seed's options: the item handler (for Cursed Gem)")
                ? slot : null, "Properties to say why the seed settings are gone");

            // The slot carries on: its tiles come from the pack's item list (the scripts' state is gone), and what arrives
            // still reaches it.
            UiTestExpect(slot.ScriptStateOf("sword") == null, "a stopped pack's tiles still come from its scripts");
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(2, new long[] { 1000 }));
            await server.BroadcastAsync(server.Chat("still here"));
            await UiTestWaitForAsync(() => PanelShows(slot, "still here") && slot.Session?.Items.AllItemsReceived.Count == 3 ? slot : null,
                "the slot to keep receiving after its scripts were stopped");
        }
        finally
        {
            AP_Atlas.Core.Logger.OnLogMessage -= Logged;
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            AP_Atlas.Core.SafeFile.Delete(zip);
        }
    }

    private async Task RaceRoomRestrictsAsync()
    {
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        server.DataStorage["_read_race_mode"] = 1;
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        try
        {
            UiTestExpect(_spheres.HiddenBecause(profile) == null, "spheres were hidden before anything said the room is a race");
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            await UiTestWaitForAsync(() => slot.Model.RaceStateKnown ? slot : null, "the server to say whether the room is a race");
            UiTestExpect(slot.IsRaceRoom && slot.RaceRestricted, "a race room didn't restrict its slot");
            UiTestExpect(await slot.ExplainLocationAsync(2000) == null, "a race room's slot answered \"why\"");
            // The Sphere Tracker reads it from the slot, and remembers it for the multiworld (while offline too).
            UiTestExpect(_spheres.HiddenBecause(profile)?.Contains("race") == true, "the Sphere Tracker didn't hide a race room's spheres");
            UiTestExpect(profile.RaceRoom == true, "the multiworld didn't remember that its room is a race");
        }
        finally
        {
            DeleteProfile(profile);
        }
    }

    /// <summary>The Python the fake logic engine runs on (ATLAS_UITEST_PYTHON). Without one, the scenario is skipped.</summary>
    private static string UiTestPython()
    {
        string? python = System.Environment.GetEnvironmentVariable("ATLAS_UITEST_PYTHON");
        if (string.IsNullOrWhiteSpace(python) || !System.IO.File.Exists(python))
            throw new UiTestSkip("no Python to run the fake logic engine on (Tools/run_selftest.ps1 passes the one on the PATH in ATLAS_UITEST_PYTHON)");
        return python;
    }

    // The logic world's names differ from the other scenarios' "Test Game", so its data package has its own checksum (as
    // on a real server), or Atlas would rightly use the names it stored for that checksum.
    private const string LogicWorldChecksum = "5eed5eed5eed5eed5eed5eed5eed5eed5eed5eed";

    /// <summary>
    /// A fake engine where the portable engine goes, as setup leaves it (the runner and the bridge), run on that Python.
    /// Its world: a chest open from the start, a door behind the Sword, a tower behind the Sword and the Shield, a vault
    /// behind the Gem. The goal needs the Sword and the Shield.
    /// </summary>
    private static FakeLogicEngine StartFakeEngine(string python)
    {
        var engine = new FakeLogicEngine(AtlasEngine.ArchipelagoDir);
        engine.Pool.AddRange(new[] { new FakeItem(1000, "Sword", 1), new FakeItem(1001, "Shield", 1), new FakeItem(1002, "Rupee", 0), new FakeItem(1099, "Gem", 1) });
        engine.Locations.AddRange(new[]
        {
            new FakeLocation(2000, "Cave Chest"), new FakeLocation(2001, "Locked Door", 1000), new FakeLocation(2002, "Tower Top", 1000, 1001),
            new FakeLocation(2003, "Gem Vault", 1099)
        });
        engine.Goal = new long[] { 1000, 1001 };
        engine.DataChecksum = LogicWorldChecksum;
        engine.Apply();
        AtlasEngine.TestPython = python;
        AtlasEngine.InstallBridge(EngineInstall.Portable());
        return engine;
    }

    /// <summary>A fake server whose room has the fake engine's world (one slot, "Tester").</summary>
    private static FakeArchipelagoServer LogicWorldServer()
    {
        var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame(LogicWorldChecksum,
            new Dictionary<string, long> { ["Sword"] = 1000, ["Shield"] = 1001, ["Rupee"] = 1002, ["Gem"] = 1099 },
            new Dictionary<string, long> { ["Cave Chest"] = 2000, ["Locked Door"] = 2001, ["Tower Top"] = 2002, ["Gem Vault"] = 2003 });
        return server;
    }

    private async Task LogicFollowsTheSlotAsync()
    {
        const string checksum = LogicWorldChecksum;
        var engine = StartFakeEngine(UiTestPython());
        engine.CrashOnItem = 1099; // once
        engine.Apply();
        await using var server = LogicWorldServer();
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.LogicTracker);
            await UiTestWaitForAsync(() => slot.LogicSettled ? slot : null, "the slot's logic to start");
            var init = engine.Requests("init").Single();
            UiTestExpect((string?)init["game"] == "Test Game" && (string?)init["player_name"] == "Tester" && (int?)init["slot"] == 1,
                $"the engine was started for the wrong slot: {init}");
            UiTestExpect(Ids(init["all_locations"]).SequenceEqual(new long[] { 2000, 2001, 2002, 2003 }), $"the engine was given the wrong locations: {init["all_locations"]}");
            UiTestExpect((string?)init["expected_checksum"] == checksum, "the engine wasn't told the seed's data checksum");
            UiTestExpect(slot.ApworldMatchesSeed == true && slot.EngineProblem == null, "the engine's world doesn't match the seed's");

            // Nothing received yet: only the chest.
            ExpectLogic(slot, "with nothing received", inLogic: new long[] { 2000 }, outOfLogic: new long[] { 2001, 2002, 2003 }, goal: false, active: 1);
            await ExpectTreeAsync(slot, "with nothing received", "── Base Logic (Starting Reachable) ──", "Cave Chest");

            // The Sword, which the server marks as progression: one step, opening the door.
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(0, new long[] { 1000 }, flags: 1));
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.IsLocationInLogic(2001) == true ? slot : null, "the Sword to open the door");
            ExpectLogic(slot, "with the Sword", inLogic: new long[] { 2000, 2001 }, outOfLogic: new long[] { 2002, 2003 }, goal: false, active: 2);
            UiTestExpect(slot.UnlockStepOf(2001) == (1, 2, "Sword"), $"the door's step is {slot.UnlockStepOf(2001)}, not step 1 (the 2nd check) by the Sword");
            var sword = engine.Requests("steps")[^1];
            UiTestExpect(Ids(sword["base"]).Length == 0 && Ids(sword["items"]).SequenceEqual(new long[] { 1000 }), $"the engine wasn't asked what the Sword opens: {sword}");
            // Why the tower isn't in logic: the engine names the one item it lacks.
            var why = await slot.ExplainLocationAsync(2002);
            UiTestExpect(why is { InLogic: false } && why.SingleUnlocks?.SequenceEqual(new[] { "Shield" }) == true,
                $"the tower's \"why\" didn't name the Shield: {(why == null ? "no answer" : Newtonsoft.Json.JsonConvert.SerializeObject(why))}");

            // The Shield, sent without flags (as the server's /send does): still progression, because the world says so.
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(1, new long[] { 1001 }));
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.IsLocationInLogic(2002) == true ? slot : null, "the Shield to open the tower");
            ExpectLogic(slot, "with the Sword and the Shield", inLogic: new long[] { 2000, 2001, 2002 }, outOfLogic: new long[] { 2003 }, goal: true, active: 3);
            UiTestExpect(slot.UnlockStepOf(2002) == (2, 3, "Shield") && slot.LogicStepCount == 2, $"the tower's step is {slot.UnlockStepOf(2002)} of {slot.LogicStepCount}");
            await ExpectTreeAsync(slot, "with the Sword and the Shield", "── Base Logic (Starting Reachable) ──", "Cave Chest", "── Unlocked by: Sword (1 checks) ──", "Locked Door",
                "── Unlocked by: Shield (1 checks) ──", "Tower Top");

            // Filler changes nothing about logic, so the engine isn't asked.
            int asked = engine.Requests("steps").Count;
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(2, new long[] { 1002 }));
            await UiTestWaitForAsync(() => slot.Session.Items.AllItemsReceived.Count == 3 ? slot : null, "the Rupee to arrive");
            await UiTestWaitAsync(0.3);
            UiTestExpect(engine.Requests("steps").Count == asked && slot.LogicSettled, "the engine was asked about filler");

            // A check done in the game: it's no longer counted as one to do.
            await server.BroadcastAsync(FakeArchipelagoServer.LocationsChecked(2000));
            await UiTestWaitForAsync(() => slot.ActiveLogicCount == 2 ? slot : null, "the checked chest to leave the count");

            // The engine crashes on the Gem. Nothing half-done is kept: logic is unknown until a new engine has rebuilt it
            // from scratch, which starts after 2 seconds (the first of its restarts).
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(3, new long[] { 1099 }, flags: 1));
            await UiTestWaitForAsync(() => slot.EngineProblem?.Code == "restarting" ? slot : null, "the crash to be noticed");
            var sinceCrash = System.Diagnostics.Stopwatch.StartNew();
            // A crash shows twice (the failed request, the ended process), but counts once: the first restart is after
            // 2 s. Counted twice, it would be the second restart's 10 s.
            await UiTestWaitAsync(1.0);
            UiTestExpect(engine.Crashes == 1, $"the engine crashed {engine.Crashes} times, not once");
            string restarting = slot.EngineProblem?.Message ?? "";
            UiTestExpect(restarting.Contains("in 2 s"), $"one crash wasn't counted once: {restarting}");
            UiTestExpect(slot.IsLocationInLogic(2001) == null && slot.LogicStepCount == 0, "logic from the crashed engine was kept");
            await UiTestWaitForAsync(() => slot.LogicSettled && engine.Starts == 2 ? slot : null, "logic to be rebuilt on a new engine");
            UiTestExpect(sinceCrash.Elapsed < TimeSpan.FromSeconds(6),
                $"the new engine took {sinceCrash.Elapsed.TotalSeconds:0.0} s to start after one crash, not about 2 s (as if it had crashed twice)");
            ExpectLogic(slot, "after the restart", inLogic: new long[] { 2001, 2002, 2003 }, outOfLogic: Array.Empty<long>(), goal: true, active: 3);
            // The new engine was asked about everything again, from the start and in the order it arrived: what was in logic
            // before any item, and what each item opened, in one request.
            var journal = engine.Journal();
            var restarted = journal.Last(entry => (string?)entry["event"] == "start");
            var rebuilt = journal.SkipWhile(entry => entry != restarted).Select(entry => entry["request"]).OfType<JObject>().ToList();
            UiTestExpect(rebuilt.Count > 0 && (string?)rebuilt[0]["action"] == "init", "the new engine wasn't started for the slot first");
            var asks = rebuilt.Where(r => (string?)r["action"] == "steps").Select(r => $"{(bool?)r["start"]}: [{string.Join(",", Ids(r["base"]))}] then [{string.Join(",", Ids(r["items"]))}]").ToList();
            UiTestExpect(asks.SequenceEqual(new[] { "True: [] then [1000,1001,1099]" }), $"the new engine was asked: {string.Join("; ", asks)}");
            UiTestExpect(slot.UnlockStepOf(2003) == (3, 3, "Gem") && slot.EngineProblem == null, $"the vault's step is {slot.UnlockStepOf(2003)}");

            // An engine update, as setup runs one: it pauses the slots using the engine, changes its files, then lets
            // them resume. Logic is unknown while paused, and rebuilt on a new engine afterwards.
            var update = AtlasEngine.ExclusiveAsync(async () =>
            {
                await AtlasEngine.StopEnginesUsingAsync(engine.Root, _ => { }, System.Threading.CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(1)); // the update itself
                return true;
            }, System.Threading.CancellationToken.None);
            await UiTestWaitForAsync(() => slot.EngineProblem?.Code == "paused" ? slot : null, "logic to pause for the engine update");
            UiTestExpect(!slot.EngineRunning && slot.IsLocationInLogic(2001) == null, "logic kept running while the engine was updated");
            UiTestExpect(await update, "the engine update didn't finish");
            await UiTestWaitForAsync(() => slot.LogicSettled && engine.Starts == 3 ? slot : null, "logic to resume after the engine update");
            ExpectLogic(slot, "after the engine update", inLogic: new long[] { 2001, 2002, 2003 }, outOfLogic: Array.Empty<long>(), goal: true, active: 3);

            // Restart logic (the button): a new engine, and the same logic.
            slot.RetryLogicEngine();
            await UiTestWaitForAsync(() => slot.LogicSettled && engine.Starts == 4 ? slot : null, "logic to restart");
            ExpectLogic(slot, "after restarting logic", inLogic: new long[] { 2001, 2002, 2003 }, outOfLogic: Array.Empty<long>(), goal: true, active: 3);
            UiTestExpect(engine.Crashes == 1 && slot.EngineProblem == null, "the engine failed again");

            // An answer that doesn't say what each new item opened is a failure, never taken for "it opened nothing": logic
            // starts again (after 2 s: the restart above gave it a fresh set of tries).
            engine.ShortSteps = true;
            engine.Apply();
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(4, new long[] { 1000 }, flags: 1));
            await UiTestWaitForAsync(() => slot.EngineProblem?.Code == "restarting" ? slot : null, "a broken answer to be a failure");
            UiTestExpect(slot.EngineProblem?.Message?.Contains("didn't say what each item opened") == true && slot.EngineProblem.Message.Contains("in 2 s"),
                $"a broken answer: {slot.EngineProblem?.Message}");
            engine.ShortSteps = false;
            engine.Apply();
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.EngineProblem == null ? slot : null, "logic to start again after the broken answer");
            ExpectLogic(slot, "after the broken answer", inLogic: new long[] { 2001, 2002, 2003 }, outOfLogic: Array.Empty<long>(), goal: true, active: 3);

            // Race mode set to hide all logic: every view of it is hidden at once, and shown again when it's off.
            AP_Atlas.Core.RaceRules.SetMode(AP_Atlas.Core.RaceModeSetting.AlwaysOn);
            AP_Atlas.Core.RaceRules.SetHideAllLogic(true);
            UiTestExpect(slot.IsLocationInLogic(2001) == null && slot.ActiveLogicCount == 0 && slot.UnlockStepOf(2001) == null && slot.LogicStepCount == 0,
                "race mode didn't hide logic");
            UiTestExpect(await slot.ExplainLocationAsync(2001) == null, "race mode answered \"why\"");
            await UiTestWaitForAsync(() => !slot.LogicTrackerView.FindChildren("*", "Tree", true, false).OfType<Tree>().Single().Visible ? slot : null,
                "the Logic Tracker to hide its list in race mode");
            AP_Atlas.Core.RaceRules.SetHideAllLogic(false);
            AP_Atlas.Core.RaceRules.SetMode(AP_Atlas.Core.RaceModeSetting.FollowServer);
            ExpectLogic(slot, "with race mode off again", inLogic: new long[] { 2001, 2002, 2003 }, outOfLogic: Array.Empty<long>(), goal: true, active: 3);
        }
        finally
        {
            AP_Atlas.Core.RaceRules.SetHideAllLogic(false);
            AP_Atlas.Core.RaceRules.SetMode(AP_Atlas.Core.RaceModeSetting.FollowServer);
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
            AtlasEngine.TestPython = null;
        }
    }

    private async Task SharedEnginesAsync()
    {
        var engine = StartFakeEngine(UiTestPython());
        // The Gem, which only Alice gets, crashes the engine answering her: twice.
        engine.CrashOnItem = 1099;
        engine.CrashTimes = 2;
        engine.Apply();
        await using var server = LogicWorldServer();
        server.Slots.Clear();
        server.Slots.AddRange(new[] { "Alice", "Bob" });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.AddRange(new[] { "Alice", "Bob" });
        _profiles.Add(profile);
        EnginePools.TestMaxEngines = 1; // the two slots share one engine on any PC
        try
        {
            await OnConnectSlotPressedAsync("Alice", profile);
            var alice = await UiTestWaitForAsync(() => SlotView(profile.Id, "Alice"), "Alice's view");
            await UiTestWaitForAsync(() => alice.LogicSettled ? alice : null, "Alice's logic to start");

            // The engine takes 3 s over Alice's Gem before it crashes. Bob connects meanwhile: his start waits behind it, and
            // fails with it. That's not his failure: he starts again in 2 s, as Alice does (her first failure).
            engine.Delays["steps"] = 3;
            engine.Apply();
            await server.SendToSlotAsync("Alice", FakeArchipelagoServer.ReceivedItems(0, new long[] { 1099 }, flags: 1));
            await UiTestWaitForAsync(() => engine.Requests("steps").Any(r => (string?)r["key"] == "Alice" && Ids(r["items"]).Contains(1099)) ? alice : null,
                "the engine to work on Alice's Gem");
            await OnConnectSlotPressedAsync("Bob", profile);
            var bob = await UiTestWaitForAsync(() => SlotView(profile.Id, "Bob"), "Bob's view");
            await UiTestWaitForAsync(() => bob.EngineProblem?.Code == "restarting" ? bob : null, "Bob to lose the engine while starting");
            string bobStarting = bob.EngineProblem?.Message ?? "";
            UiTestExpect(engine.Crashes == 1 && bobStarting.Contains("shares with Alice") && bobStarting.Contains("in 2 s"), $"Bob, starting when the engine crashed: {bobStarting}");
            await UiTestWaitForAsync(() => alice.EngineProblem?.Code == "restarting" ? alice : null, "Alice to lose the engine");
            UiTestExpect(alice.EngineProblem?.Message?.Contains("in 2 s") == true, $"Alice's first failure: {alice.EngineProblem?.Message}");

            // Both start again on one new engine; the Gem doesn't crash it yet.
            engine.Delays.Clear();
            engine.CrashTimes = 1;
            engine.Apply();
            await UiTestWaitForAsync(() => alice.LogicSettled && bob.LogicSettled ? alice : null, "both slots' logic to start again", seconds: 30);
            var pool = EnginePools.For(profile.Id, AtlasEngine.Resolve(_appSettings));
            UiTestExpect(pool.SlotsPerEngine.SequenceEqual(new[] { 2 }) && engine.Starts == 2,
                $"the two slots run on engines with {string.Join(", ", pool.SlotsPerEngine)} slots ({engine.Starts} started), not sharing a second one");

            // A new item for Alice, and the Gem brings the engine down again, answering her: her second failure waits 10 s.
            // Bob, running, loses it too: still not his failure, so he's back in 2 s, long before her.
            engine.CrashTimes = 2;
            engine.Apply();
            await server.SendToSlotAsync("Alice", FakeArchipelagoServer.ReceivedItems(1, new long[] { 1000 }, flags: 1));
            await UiTestWaitForAsync(() => engine.Crashes == 2 && bob.EngineProblem?.Code == "restarting" ? bob : null, "Bob to lose the engine again", seconds: 30);
            string bobRunning = bob.EngineProblem?.Message ?? "";
            UiTestExpect(bobRunning.Contains("shares with Alice") && bobRunning.Contains("in 2 s"), $"Bob, running when the engine crashed again: {bobRunning}");
            UiTestExpect(alice.EngineProblem?.Message?.Contains("in 10 s") == true, $"Alice's second failure: {alice.EngineProblem?.Message}");
            UiTestExpect(alice.EngineFailures == 2 && bob.EngineFailures == 0, $"failures counted: Alice {alice.EngineFailures} (not 2), Bob {bob.EngineFailures} (not 0)");
            await UiTestWaitForAsync(() => bob.LogicSettled ? bob : null, "Bob's logic to start again", seconds: 30);
            UiTestExpect(alice.EngineProblem?.Code == "restarting", "Alice started again before her 10 s were up");
            await UiTestWaitForAsync(() => alice.LogicSettled ? alice : null, "Alice's logic to start again", seconds: 60);
            UiTestExpect(pool.SlotsPerEngine.SequenceEqual(new[] { 2 }) && engine.Crashes == 2 && alice.IsLocationInLogic(2003) == true,
                $"after both restarts: engines {string.Join(", ", pool.SlotsPerEngine)}, {engine.Crashes} crashes, the vault in Alice's logic {alice.IsLocationInLogic(2003)}");
        }
        finally
        {
            EnginePools.TestMaxEngines = null;
            DeleteProfile(profile);
            AtlasEngine.TestPython = null;
        }
    }

    private async Task CheeseFollowsTheSlotAsync()
    {
        StartFakeEngine(UiTestPython());
        await using var server = LogicWorldServer();
        // The multiworld's Cheese Tracker page: the slot is on it, marked BK.
        await using var site = new FakeCheeseServer(new CtTracker
        {
            Id = 1,
            TrackerId = FakeCheeseServer.TrackerId,
            Title = "UI test multiworld",
            Games = new List<CtGame> { new CtGame { Id = 21, Position = 1, Name = "Tester", Game = "Test Game", Availability = "open", Progression = "bk" } },
            Hints = new List<CtHint>()
        });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        string? siteBefore = _appSettings.CheeseInstanceUrl;
        var spacingBefore = CheeseClient.Spacing;
        _appSettings.CheeseInstanceUrl = site.Site;
        CheeseClient.Spacing = TimeSpan.Zero;
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            await UiTestWaitForAsync(() => slot.LogicSettled ? slot : null, "the slot's logic to start");
            UiTestExpect(await _cheese.LinkAsync(profile.Id, site.TrackerUrl) == null, "the multiworld couldn't be linked to its Cheese Tracker page");
            string Describe(CheeseAdvice? a) => a == null ? "none" : $"{a.Status ?? "no status"} ({a.Reason ?? a.Quiet})";

            // The chest is in logic: unblocked, suggested but not yet applied (good news must hold for a minute first).
            var view = _cheese.SlotView(profile.Id, "Tester");
            UiTestExpect(view?.Row?.Id == 21, "the slot wasn't matched to its row on Cheese Tracker");
            UiTestExpect(view!.Advice is { Status: "unblocked", Reason: "1 check in logic", Ready: false }, $"the suggestion with one check in logic was {Describe(view.Advice)}");

            // Race mode hiding logic: no suggestion at all.
            AP_Atlas.Core.RaceRules.SetMode(AP_Atlas.Core.RaceModeSetting.AlwaysOn);
            AP_Atlas.Core.RaceRules.SetHideAllLogic(true);
            var hidden = _cheese.SlotView(profile.Id, "Tester")?.Advice;
            UiTestExpect(hidden is { Status: null } && hidden.Quiet?.Contains("race mode hides logic") == true, $"the suggestion in race mode was {Describe(hidden)}");
            AP_Atlas.Core.RaceRules.SetHideAllLogic(false);
            AP_Atlas.Core.RaceRules.SetMode(AP_Atlas.Core.RaceModeSetting.FollowServer);

            // The Sword and the Shield bring the goal into logic: go mode.
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(0, new long[] { 1000, 1001 }, flags: 1));
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.GoalInLogic == true ? slot : null, "the goal to come into logic");
            var go = _cheese.SlotView(profile.Id, "Tester")?.Advice;
            UiTestExpect(go is { Status: "go", Reason: "Your goal is in logic" }, $"the suggestion with the goal in logic was {Describe(go)}");

            // Suggestions only: without automatic updates turned on, nothing on Cheese Tracker was changed.
            UiTestExpect(site.Requests.All(request => request.Method == "GET"), "Atlas changed Cheese Tracker without being asked");
        }
        finally
        {
            AP_Atlas.Core.RaceRules.SetHideAllLogic(false);
            AP_Atlas.Core.RaceRules.SetMode(AP_Atlas.Core.RaceModeSetting.FollowServer);
            DeleteProfile(profile);
            _appSettings.CheeseInstanceUrl = siteBefore;
            CheeseClient.Spacing = spacingBefore;
            AtlasEngine.TestPython = null;
        }
    }

    private async Task ScaleStaysResponsiveAsync()
    {
        const int roomSize = 1000, connected = 20, locations = 300;
        const string checksum = "5ca1e5ca1e5ca1e5ca1e5ca1e5ca1e5ca1e5ca1e";
        string python = UiTestPython();
        // The game every slot plays: 300 locations, a sixth open from the start and the rest each behind one of 50
        // progression items; 50 filler items besides.
        var engine = new FakeLogicEngine(AtlasEngine.ArchipelagoDir);
        var items = Enumerable.Range(0, 100).Select(i => new FakeItem(1000 + i, $"Item {i}", i < 50 ? 1 : 0)).ToList();
        engine.Pool.AddRange(items);
        engine.Locations.AddRange(Enumerable.Range(0, locations).Select(i =>
            i % 6 == 0 ? new FakeLocation(2000 + i, $"Location {i}") : new FakeLocation(2000 + i, $"Location {i}", 1000 + i % 50)));
        engine.Goal = Enumerable.Range(0, 50).Select(i => 1000L + i).ToArray();
        engine.DataChecksum = checksum;
        engine.Apply();
        AtlasEngine.TestPython = python;
        AtlasEngine.InstallBridge(EngineInstall.Portable());

        await using var server = new FakeArchipelagoServer();
        server.Slots.Clear();
        server.Slots.AddRange(Enumerable.Range(1, roomSize).Select(i => $"Player{i:0000}"));
        server.SlotGame = "Scale Game";
        server.Games["Scale Game"] = new FakeGame(checksum, items.ToDictionary(i => i.Name, i => i.Id),
            Enumerable.Range(0, locations).ToDictionary(i => $"Location {i}", i => 2000L + i));
        var profile = new MultiworldProfile { Name = "UI test (scale)", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.AddRange(server.Slots.Take(connected));
        _profiles.Add(profile);
        try
        {
            // Connecting the 20 slots, one after another, with their engines starting. The first pays one-time costs (code
            // compiled on first use), so the rest are measured.
            await OnConnectSlotPressedAsync(profile.Slots[0], profile);
            await UiTestWaitForAsync(() => SlotView(profile.Id, profile.Slots[0]) is { LogicSettled: true } first ? first : null, "the first slot's logic to start", seconds: 60);
            await UiTestWaitAsync(0.5);
            AP_Atlas.Core.HitchMonitor.ResetWorst();
            foreach (string name in profile.Slots.Skip(1)) await OnConnectSlotPressedAsync(name, profile);
            var slots = new List<SlotTrackerControl>();
            foreach (string name in profile.Slots) slots.Add(await UiTestWaitForAsync(() => SlotView(profile.Id, name), $"{name}'s view"));
            await UiTestWaitForAsync(() => slots.All(s => s.LogicSettled) ? slots : null, "every slot's logic to start", seconds: 120);
            await UiTestWaitAsync(1.0);
            double connecting = AP_Atlas.Core.HitchMonitor.WorstFrameMs, connectingWork = AP_Atlas.Core.HitchMonitor.WorstWorkMs;
            string connectingReport = AP_Atlas.Core.HitchMonitor.WorstFrameReport, connectingWorkReport = AP_Atlas.Core.HitchMonitor.WorstWorkReport;
            UiTestExpect(server.TextClients == 1, $"{server.TextClients} of the {connected} connections receive the room's text, not 1");
            // The 20 slots share a few engine processes: as many as the pool runs on this PC, each with its share.
            var engines = EnginePools.For(profile.Id, AP_Atlas.Core.EngineSetup.AtlasEngine.Resolve(_appSettings)).SlotsPerEngine;
            int maxEngines = AP_Atlas.Core.EngineSetup.EnginePool.EnginesFor(System.Environment.ProcessorCount);
            UiTestExpect(engines.Count == Math.Min(maxEngines, connected) && engines.Sum() == connected && engines.Max() - engines.Min() <= 1,
                $"the {connected} slots run on {engines.Count} engines ({string.Join(", ", engines)} slots each), not {Math.Min(maxEngines, connected)} sharing them evenly");
            // The multiworld was already in the sidebar: its cards update in place as slots connect.
            int sidebarRebuilds = AP_Atlas.Core.HitchMonitor.Step("Rebuild SLOTS sidebar").Runs;
            int sphereRedraws = AP_Atlas.Core.HitchMonitor.Step("Sphere Tracker tab: refresh").Runs;
            GD.Print($"UITEST INFO Scale: connecting {connected - 1} more slots, the worst frame took {connecting:0} ms ({connectingWork:0} ms of work, garbage collection left out)");

            // What a busy room sends, three times over: every connected slot gets 20 progression items, 100 item lines
            // between players across the room, and 50 new hints (each at its own location, so none repeats).
            AP_Atlas.Core.HitchMonitor.ResetWorst();
            var random = new Random(1);
            int received = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int round = 0; round < 3; round++)
            {
                int from = received;
                await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(from, Enumerable.Range(0, 20).Select(i => 1000L + (from + i) % 50), flags: 1));
                received += 20;
                await server.BroadcastAsync(Enumerable.Range(0, 100).Select(_ => FakeArchipelagoServer.ItemSend(random.Next(1, roomSize + 1), random.Next(1, roomSize + 1),
                    1000 + random.Next(100), 2000 + random.Next(locations))).ToArray());
                int first = round * 50;
                await server.AddHintsAsync(Enumerable.Range(1, connected).SelectMany(slot => Enumerable.Range(first, 50).Select(i =>
                    FakeArchipelagoServer.Hint(random.Next(connected + 1, roomSize + 1), slot, 2000 + i, 1000 + random.Next(100)))).ToArray());
                int expected = received;
                await UiTestWaitForAsync(() => slots.All(s => s.Session.Items.AllItemsReceived.Count == expected) ? slots : null, "a burst's items", seconds: 60);
                await UiTestWaitAsync(0.5);
            }
            // Logic has caught up when every slot has worked out all 60 of its progression items.
            await UiTestWaitForAsync(() => slots.All(s => s.LogicSettled && s.Model.Logic.EvaluatedItems == 60) ? slots : null, "logic to catch up", seconds: 120);
            await UiTestWaitAsync(1.0);
            double worst = AP_Atlas.Core.HitchMonitor.WorstFrameMs, worstWork = AP_Atlas.Core.HitchMonitor.WorstWorkMs;
            // Every slot shows the 300 item lines once, and its 150 hints once: the text slot as the server's lines, the
            // others from their hint lists.
            foreach (var slot in slots)
            {
                int itemLines = slot.ChatHistory.Count(e => e.APMessage is Archipelago.MultiClient.Net.MessageLog.Messages.ItemSendLogMessage and not Archipelago.MultiClient.Net.MessageLog.Messages.HintItemSendLogMessage);
                int hintLines = slot.ChatHistory.Count(e => e.Hint != null || e.APMessage is Archipelago.MultiClient.Net.MessageLog.Messages.HintItemSendLogMessage);
                UiTestExpect(itemLines == 300 && hintLines == 150, $"{slot.SlotName}'s text client has {itemLines} item lines (not 300) and {hintLines} hints (not 150)");
            }
            var chatLines = AP_Atlas.Core.HitchMonitor.Step("Text client lines");
            var hintRefreshes = AP_Atlas.Core.HitchMonitor.Step("Hints refresh");
            var memory = GC.GetGCMemoryInfo();
            GD.Print($"UITEST INFO Scale: {connected} slots in a {roomSize}-player room; the bursts took {stopwatch.Elapsed.TotalSeconds:0.0} s ({engine.Requests("steps").Count} logic requests in all); the worst frame took {worst:0} ms ({worstWork:0} ms of work, garbage collection left out); " +
                     $"text client lines took at most {chatLines.WorstFrameMs:0} ms of a frame ({chatLines.Runs} runs, at most {chatLines.MostRunsInFrame} in a frame); " +
                     $".NET heap {memory.HeapSizeBytes / 1048576.0:0} MB, committed {memory.TotalCommittedBytes / 1048576.0:0} MB, process {System.Environment.WorkingSet / 1048576.0:0} MB, " +
                     $"GC paused {GC.GetTotalPauseDuration().TotalMilliseconds:0} ms in all; {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):0} nodes, " +
                     $"{Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount):0} orphaned, {Performance.GetMonitor(Performance.Monitor.ObjectCount):0} objects; generations " +
                     string.Join("/", memory.GenerationInfo.ToArray().Select(g => $"{g.SizeAfterBytes / 1048576.0:0}")) + " MB. The worst frame: " +
                     AP_Atlas.Core.HitchMonitor.WorstFrameReport.ReplaceLineEndings(" | "));
            // The target is 100 ms. The worst frame takes about 80 to 150 ms on the development PC and on CI, depending on how
            // many logic answers land in one frame and on garbage collection, which pauses everything for as long as the
            // machine takes (61 ms in one frame on a slow CI machine, whose work in that frame took 89). So the guard is on
            // Atlas's work, collections left out as in the step timings, with room for slower machines; a whole frame,
            // collections included, may not take 250 ms. That one connection receives the room's text is checked above.
            UiTestExpect(worstWork < 150, $"a frame's work took {worstWork:0} ms during the bursts, garbage collection left out (the guard is 150 ms): {AP_Atlas.Core.HitchMonitor.WorstWorkReport}");
            UiTestExpect(worst < 250, $"a frame took {worst:0} ms during the bursts, garbage collection included (the guard is 250 ms): {AP_Atlas.Core.HitchMonitor.WorstFrameReport}");
            // Logic works out a burst's items a few per request (each slot gets 60 progression items in all): at most 10 in
            // one, and far fewer requests than items.
            var logicRequests = engine.Requests("steps");
            UiTestExpect(logicRequests.All(r => Ids(r["items"]).Length <= 10) && logicRequests.Count < connected * 60 / 3,
                $"logic asked {logicRequests.Count} times, with up to {logicRequests.Max(r => Ids(r["items"]).Length)} items at once");
            // Connecting is a click with a spinner, not play: its work is guarded at 300 ms against things getting worse (a whole
            // frame, garbage collection included, at 400 ms). Building a slot's views only when first shown (with the new
            // shell) is what brings it under 100 ms.
            UiTestExpect(connectingWork < 300, $"a frame's work took {connectingWork:0} ms while slots connected, garbage collection left out (the guard is 300 ms): {connectingWorkReport}");
            UiTestExpect(connecting < 400, $"a frame took {connecting:0} ms while slots connected, garbage collection included (the guard is 400 ms): {connectingReport}");
            UiTestExpect(sidebarRebuilds == 0, $"the sidebar was rebuilt {sidebarRebuilds} times while slots connected");
            UiTestExpect(sphereRedraws == 0, $"the Sphere Tracker tab, not showing, redrew {sphereRedraws} times while slots connected");
            // Only what shows does work: the hints views aren't showing, and only the selected slot's text client draws, a
            // slice of lines per frame. A slice draws for about 6 ms; with its last line and the scroll, a frame's share takes
            // 8 to 20 ms here, and 15 to 50 ms on CI's shared machines (whose other work can hold up the window's thread).
            // Drawing a burst in one go (about 1 ms a line) takes 130 ms here and two or three times that on CI, so 100 ms
            // tells the two apart; a frame that long also fails the guard above.
            UiTestExpect(hintRefreshes.Runs == 0, $"hints views that weren't showing refreshed {hintRefreshes.Runs} times");
            UiTestExpect(chatLines.Runs > 0 && chatLines.WorstFrameMs < 100,
                $"drawing text client lines took {chatLines.WorstFrameMs:0} ms of one frame ({chatLines.Runs} runs, at most {chatLines.MostRunsInFrame} in a frame)");
            // The log views keep their last lines (the log file keeps everything).
            for (int i = 0; i < AP_Atlas.UI.LogPane.Lines + 500; i++) AP_Atlas.Core.Logger.LogInfo($"UI test line {i}");
            await UiTestWaitAsync(0.2);
            UiTestExpect(_consoleOutput.GetParagraphCount() <= AP_Atlas.UI.LogPane.Lines + 200 && _debugLogConsole.GetParagraphCount() <= AP_Atlas.UI.LogPane.Lines + 200,
                $"the logs keep {_consoleOutput.GetParagraphCount()} and {_debugLogConsole.GetParagraphCount()} lines");
            UiTestExpect(_debugLogConsole.GetParsedText().Contains($"UI test line {AP_Atlas.UI.LogPane.Lines + 499}"), "the debug log lost its newest line");
        }
        finally
        {
            DeleteProfile(profile);
            AtlasEngine.TestPython = null;
        }
    }

    /// <summary>Checks what the slot reports as in logic, its goal, and how many checks it has left in logic.</summary>
    private static void ExpectLogic(SlotTrackerControl slot, string when, long[] inLogic, long[] outOfLogic, bool goal, int active)
    {
        foreach (long id in inLogic) UiTestExpect(slot.IsLocationInLogic(id) == true, $"location {id} isn't in logic {when}");
        foreach (long id in outOfLogic) UiTestExpect(slot.IsLocationInLogic(id) == false, $"location {id} is in logic {when}");
        UiTestExpect(slot.GoalInLogic == goal, $"the goal is {(slot.GoalInLogic?.ToString() ?? "unknown")} {when}, not {goal}");
        UiTestExpect(slot.ActiveLogicCount == active, $"{slot.ActiveLogicCount} checks are left in logic {when}, not {active}");
    }

    /// <summary>Waits for the Logic Tracker to show these rows (the location column), top to bottom.</summary>
    private async Task ExpectTreeAsync(SlotTrackerControl slot, string when, params string[] rows)
    {
        var tree = slot.LogicTrackerView.FindChildren("*", "Tree", true, false).OfType<Tree>().Single();
        List<string> Shown()
        {
            var shown = new List<string>();
            for (var row = tree.GetRoot()?.GetFirstChild(); row != null; row = row.GetNext()) shown.Add(row.GetText(1));
            return shown;
        }
        try
        {
            await UiTestWaitForAsync(() => Shown().SequenceEqual(rows) ? tree : null, "the Logic Tracker's rows");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"the Logic Tracker shows [{string.Join(" | ", Shown())}] {when}");
        }
    }

    private static long[] Ids(JToken? list) => list?.Select(id => (long)id).ToArray() ?? Array.Empty<long>();

    /// <summary>Thrown by a scenario that can't run here (it reports SKIP instead of PASS or FAIL).</summary>
    private sealed class UiTestSkip(string reason) : Exception(reason);

    /// <summary>
    /// Whether a panel's labels hold this text (BBCode included), leaving out the zero-width spaces Bbcode.Safe puts in a
    /// long run of text, which a reader doesn't see.
    /// </summary>
    private static bool PanelShows(Node panel, string text) =>
        panel.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().Any(label => label.Text.Replace(ZeroWidthSpace, "").Contains(text));

    /// <summary>Whether a panel's labels show this text, as read on screen (BBCode applied; zero-width spaces left out).</summary>
    private static bool PanelShowsText(Node panel, string text) =>
        panel.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().Any(label => label.GetParsedText().Replace(ZeroWidthSpace, "").Contains(text));

    private static readonly string ZeroWidthSpace = ((char)0x200B).ToString();

    /// <summary>The one view showing in the content area, or null if none or several are.</summary>
    private Control? ShownContent()
    {
        var shown = _contentStage.GetChildren().OfType<Control>().Where(c => c.Visible).ToList();
        return shown.Count == 1 ? shown[0] : null;
    }

    private SlotTrackerControl? SlotView(string profileId, string slotName) =>
        ActiveSlotNodes().OfType<SlotTrackerControl>()
            .FirstOrDefault(s => IsInstanceValid(s) && !s.IsQueuedForDeletion() && s.ProfileId == profileId && s.SlotName == slotName);

    /// <summary>Waits (letting the window run) until <paramref name="find"/> returns something, for up to 20 seconds (or <paramref name="seconds"/>).</summary>
    private async Task<T> UiTestWaitForAsync<T>(Func<T?> find, string what, double seconds = 20) where T : class
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (find() is { } found) return found;
            if (waited.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("timed out waiting for " + what);
            await UiTestWaitAsync(0.05);
        }
    }

    private async Task UiTestWaitAsync(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void UiTestExpect(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }
}
