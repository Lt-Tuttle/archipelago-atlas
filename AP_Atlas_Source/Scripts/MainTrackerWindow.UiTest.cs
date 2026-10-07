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
        // A scenario that judges time (a frame's work under a guard) may run twice: a shared CI runner, not Atlas, fails
        // the first attempt now and then, and a real regression fails both. Every attempt is printed.
        async Task ScenarioAsync(string name, Func<Task> body, int attempts = 1)
        {
            if (!string.IsNullOrEmpty(only) && !name.Contains(only, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                GD.Print($"UITEST SKIP {name}: not chosen (ATLAS_UITEST_ONLY)");
                return;
            }
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await body();
                    passed++;
                    GD.Print("UITEST PASS " + name + (attempt > 1 ? $" (on attempt {attempt})" : ""));
                    return;
                }
                catch (UiTestSkip skip)
                {
                    skipped++;
                    GD.Print($"UITEST SKIP {name}: {skip.Message}");
                    return;
                }
                catch (Exception ex) when (attempt < attempts)
                {
                    GD.Print($"UITEST RETRY {name}: attempt {attempt} failed: {ex.Message}");
                    await UiTestWaitAsync(1.0); // whatever held the machine up may pass
                }
                catch (Exception ex)
                {
                    failed++;
                    GD.Print($"UITEST FAIL {name}: {ex.Message}");
                    return;
                }
            }
        }

        await UiTestWaitAsync(1.0); // the window settles, as on a first run
        await ScenarioAsync("Startup: Atlas opens on Home", () =>
        {
            UiTestExpect(ShownContent() == _homePage && _activityBar.Selected == AP_Atlas.UI.Tool.Home, $"Atlas opened on {ShownContent()?.Name ?? "nothing"}");
            return Task.CompletedTask;
        });
        await ScenarioAsync("Connecting: a slot logs in through the window and gets its view; a dropped connection comes back by itself with a new view; disconnecting closes it for good",
            ConnectingThroughTheWindowAsync);
        await ScenarioAsync("Deleting a multiworld while one of its slots connects: the connection is closed, and no slot is left for it",
            DeletingWhileConnectingLeavesNothingAsync);
        await ScenarioAsync("Ended slots are freed: a slot a dropped connection replaced, and a deleted multiworld's slot, leave nothing that keeps their model, session or view in memory",
            EndedSlotsAreFreedAsync);
        await ScenarioAsync("Ended slots with logic are freed: a deleted multiworld's engine stops, its engine pool is forgotten, and its slot's model and logic leave memory",
            EndedSlotsLogicIsFreedAsync);
        await ScenarioAsync("Closed windows are freed: the Pack Doctor, the Atlas Engine window and the race mode dialog, opened and closed again and again, leave nothing in memory and no node behind",
            ClosedWindowsAreFreedAsync);
        await ScenarioAsync("Moving views: the Cheese and Sphere tabs and Properties keep following their events when moved to another parent (docking, pop-outs), and stop while out of the window",
            ViewsKeepTheirEventsWhenMovedAsync);
        await ScenarioAsync("Tools: every tool's tab shows its own view, and each slot tool the connected slot's view (or asks for a slot when none is connected)",
            EveryToolShowsItsViewAsync);
        await ScenarioAsync("Menu bar and keys: the six menus hold every command with its key shown; Ctrl+1 to 9 switch tools, a rebind in the settings takes over at once, F1 lists the keys as they are, and About names the version",
            MenuBarAndKeysAsync);
        await ScenarioAsync("Command palette: Ctrl+Shift+P opens it ready to type, it lists every command with its key, a typed word narrows it to the commands whose words start that way, Enter runs the pick and closes it, Escape only closes it",
            CommandPaletteAsync);
        await ScenarioAsync("Activity bar: every tool in its group, in order, with a shipped icon and its key in the tooltip; pressing a button shows the tool, switching a tool any other way lights its button and names it in the header; the engine button opens the engine window",
            ActivityBarAsync);
        await ScenarioAsync("Slot picker: the tool header lists the connected slots with the selected one chosen; picking one shows its view, Ctrl+Tab and Ctrl+Shift+Tab go through them around the end, a slot selected elsewhere shows as picked, a tool that isn't per slot hides it, and a slot that ends leaves it",
            SlotPickerAsync);
        await ScenarioAsync("Window parts: the View menu hides and shows the slots panel, the explorer (whatever tool shows), Properties, the bottom pane and the status bar, remembering each; focus mode leaves the content alone and, off again, brings each part back as the user had it",
            WindowPartsAsync);
        await ScenarioAsync("Settings page: Ctrl+, shows it with the search box ready and the sections in the explorer; each kind of row changes its setting at once and saves it (a toggle, a choice, a number, a window part, a bottom pane tab); a setting changed elsewhere shows as it is; typed words narrow the rows; a section jump scrolls",
            SettingsPageAsync);
        await ScenarioAsync("Keyboard shortcuts: every command's key is a row of the Settings page; press the button, then a key, and the command runs on it at once, the menus and the bar show it and it's saved; a key two commands share is said on both rows; a reset brings the default back; Backspace means no key, Escape keeps it, a modifier alone isn't one, and losing the focus ends the wait; the F1 list leads here",
            KeyboardShortcutsAsync);
        await ScenarioAsync("Privacy & permissions: a section of the Settings page lists every permission Atlas can ask for with its state (asks each time, allowed until Atlas closes, always allowed, not until Atlas restarts) and every trusted apworld source; a kept answer and a trusted source can be taken back, which is saved; the section is found by its words",
            PrivacyAsync);
        await ScenarioAsync("Home: Ctrl+8 shows it on its own; every tool has a card with a line and every link is https; the checklist ticks the engine as it is and nothing else in a fresh folder, then a multiworld and a pack once they exist; a tip shows and Next tip goes around; the multiworld is listed and one click connects its slot, ticks the step and says Connected; a tool's card shows the tool",
            HomeAsync);
        await ScenarioAsync("Help: the guide opens on its first topic with a topic per section; a topic shows its section, What's new the changelog, Credits & disclaimer the author and the credits, Licences Atlas's licence; a second Help command uses the same window at its topic; Home's What's new card lists the newest changes and leads here",
            HelpAsync);
        await ScenarioAsync("Alerts: what Atlas tells the user stacks at the bottom right without overlapping, at most a few at once, every card in the history newest first with its kind; a card's button runs its action and the card goes, the × takes one away, the plain ones go after their hold; Window → Notifications lists the history, marks it seen, and Clear empties it",
            AlertsAsync);
        await ScenarioAsync("UI kit: a button from the kit runs its action once per press, honours enabled and keeps its tooltip; its text lines take the palette's colours; every heading from it wears the accent's heading colour and follows an accent change, wherever it is; the alert feed names a card's kind from the palette",
            UiKitAsync);
        await ScenarioAsync("Developer mode: off, the Debug Log tab, its command, the Debug Log's menu items and the hitch warnings on the status bar are hidden, and a diagnostic line reaches the Debug Log and the file, never the System Log; on, each shows; the status bar's right end counts the connected slots, and a hidden tab that was current gives way to Chat",
            DeveloperModeAsync);
        await ScenarioAsync("Accessibility: every button takes the keyboard focus (Tab reaches it, Enter presses it; a kit button can opt out beside a field), the theme draws a focus ring on every kind of control that takes the focus, Tab from the Settings search box moves on, a symbol-only kit button is named by its tooltip, and no button anywhere in the window shows only a symbol without a name for screen readers",
            AccessibilityAsync);
        await ScenarioAsync("Multiworlds page: a room link fills in the server address and the slots from the room's status page (asked first; never the room's page) and is kept; edits of a multiworld are kept when another is selected and written to disk; a slot's rename takes effect on Enter or on leaving the field, never per keystroke, and its saved stats follow it; after a reconnect gives up, one status read (at most every ten minutes) offers the room's new port, or says the room is asleep; a connection the server refuses shows as a card",
            MultiworldsPageAsync);
        await ScenarioAsync("Updates: nothing is asked of GitHub without the permission and the daily setting; with them, one read a day finds a newer version of the channel (an unchanged list is confirmed, never sent again) and offers it as a card with its notes; a download whose hash doesn't match the release's SHA256SUMS is refused and nothing is staged, a good one is staged ready for a restart; the swap moves the installed files aside and the release's in (PortableData untouched), the supervisor puts the previous version back when the new Atlas ends before its window and the Atlas put back says so, a started one confirms the note; a folder Atlas can't write to is said so",
            UpdatesAsync);
        await ScenarioAsync("Customization: a custom accent of any colour recolours Atlas, is saved, and its headings and links still read (the preset choice says Custom); colour-blind-safe colours are the theme's other palette, held to the contrast rule, saved and undone; the zoom choice, Ctrl+= and Ctrl+- scale the window and are saved; the pin shape is saved and every map redraws, a diamond being a square on its corner; the page Atlas opens on is saved and the last tool shown is remembered for it",
            CustomizationAsync);
        await ScenarioAsync("Tables: a click on a column's title sorts by it (the column's own first direction, then the other; the arrow says which), typed words narrow the rows, a column hides and shows, pinned rows stay on top, the rows export as TSV, Markdown, Discord parts and a CSV file, the sort and the hidden columns are saved, and unchanged rows are updated in place with the selection kept",
            TablesAsync);
        await ScenarioAsync("Confirmations and empty states: deleting a slot or a multiworld asks first and cancelling keeps them; without the logic engine, Key Items and the Logic Tracker both say what logic needs; go mode isn't claimed while unknown",
            ConfirmationsAndEmptyStatesAsync);
        await ScenarioAsync("Bursts: 40 items and 40 chat lines arriving together reach the slot once each, at most one update of the window in each frame they arrive over (not 80)",
            BurstIsOneUpdateAsync, attempts: 2);
        await ScenarioAsync("Long text: a game's names and a hint's entrance of a megabyte, and a chat line of 100,000 characters without a space, hold up no frame for 150 ms, shown or not (names are cut to 500 characters; a long run of text gets breaks)",
            LongTextHoldsUpNothingAsync, attempts: 2);
        await ScenarioAsync("Room text: one connection per multiworld receives the room's text and every slot's text client shows each line once, named from that slot's view; a command typed into a quiet slot gets its answer; new hints show in the slots they concern; when the text slot leaves, another takes over",
            RoomTextReachesEverySlotAsync);
        await ScenarioAsync("Moving a slot's panel: out of the window and docked elsewhere, the slot keeps its connection, views and updates, and shows what arrived meanwhile",
            SlotPanelMovesWholeAsync);
        await ScenarioAsync("Map packs: a slot's pack has its images while the slot is connected (its map shows them), the Pack Doctor's while its window is open; then they're freed once another pack is used",
            PackImagesFollowTheirUsersAsync);
        await ScenarioAsync("Idle: a connected slot doesn't keep Atlas redrawing: nothing of its map runs every frame (the wheel still zooms it and a drag still moves it; Fit fits; the view is remembered), and its card isn't re-styled while nothing changes",
            ConnectedSlotLetsAtlasIdleAsync);
        await ScenarioAsync("Map pack scripts: a slot whose pack's script runs away keeps working: the script is stopped in seconds without holding up a frame, and Key Items and the log say why",
            RunawayPackScriptAsync, attempts: 2);
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
            ScaleStaysResponsiveAsync, attempts: 2);
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

    /// <summary>
    /// Windows that close leave nothing behind: the Pack Doctor, the Atlas Engine window and the race mode dialog,
    /// each opened and closed three times. A closed window still subscribed to a static event, kept in a static field or
    /// only hidden would stay for the rest of the session, with all it holds.
    /// </summary>
    private async Task ClosedWindowsAreFreedAsync()
    {
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_windows_pack.zip");
        FakeMapPack.Write(zip, "UI test pack", "Test Game");
        try
        {
            // Once first, so what a window makes once for the session (a theme, a font) is in the counts to compare with.
            await OpenAndCloseWindowsAsync(zip);
            await CollectEverythingAsync();
            double nodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount), orphans = Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount);
            var closed = new List<(string What, System.WeakReference Weak)>();
            for (int i = 0; i < 3; i++) closed.AddRange(await OpenAndCloseWindowsAsync(zip));
            await CollectEverythingAsync();
            foreach (var (what, weak) in closed)
                UiTestExpect(!weak.IsAlive, $"{what} is still in memory after it closed");
            double nodesAfter = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount), orphansAfter = Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount);
            GD.Print($"UITEST INFO Closed windows: nodes {nodes} -> {nodesAfter}, orphaned {orphans} -> {orphansAfter}");
            UiTestExpect(nodesAfter <= nodes && orphansAfter <= orphans,
                $"after the windows opened and closed three more times, Godot holds {nodesAfter - nodes} more nodes ({orphansAfter - orphans} more out of the window)");
        }
        finally
        {
            AP_Atlas.Core.SafeFile.Delete(zip);
        }
    }

    /// <summary>Lets queued frees and work run, then collects everything nothing references any more.</summary>
    private async Task CollectEverythingAsync()
    {
        await UiTestWaitAsync(0.5);
        for (int i = 0; i < 3; i++)
        {
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            await UiTestWaitAsync(0.1);
        }
    }

    // Apart from the scenario, so only weak references come back: an async method keeps its own locals until it ends.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<List<(string What, System.WeakReference Weak)>> OpenAndCloseWindowsAsync(string zip)
    {
        OpenPackDoctor(zip);
        var doctor = await UiTestWaitForAsync(() => GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>().FirstOrDefault(), "the Pack Doctor window");
        await UiTestWaitAsync(0.3);
        doctor.EmitSignal(Window.SignalName.CloseRequested);

        OpenEngineSetup();
        var engine = await UiTestWaitForAsync(() => GetTree().Root.GetChildren().OfType<AP_Atlas.UI.AtlasEngineWindow>().FirstOrDefault(), "the Atlas Engine window");
        await UiTestWaitAsync(0.3);
        engine.EmitSignal(Window.SignalName.CloseRequested);

        ShowRaceModeInfo();
        var race = await UiTestWaitForAsync(() => GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => d.Title == "Race Mode"), "the race mode dialog");
        await UiTestWaitAsync(0.3);
        race.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);

        _commands!.Run("help.shortcuts");
        var shortcuts = await UiTestWaitForAsync(() => GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => d.Title == "Keyboard Shortcuts"), "the shortcuts dialog");
        await UiTestWaitAsync(0.3);
        shortcuts.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);

        _commands.Run("help.about");
        var about = await UiTestWaitForAsync(() => GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => d.Title == "About The Archipelago Atlas"), "the About dialog");
        await UiTestWaitAsync(0.3);
        about.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);

        _commands.Run("help.guide");
        var help = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.HelpWindow>().FirstOrDefault(), "the Help window");
        await UiTestWaitAsync(0.3);
        help.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);

        _commands.Run("window.notifications");
        var notifications = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.NotificationsDialog>().FirstOrDefault(), "the Notifications window");
        await UiTestWaitAsync(0.3);
        notifications.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);

        _commands.Run(AP_Atlas.UI.CommandPalette.OwnCommandId);
        var palette = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.CommandPalette>().FirstOrDefault(p => p.Visible), "the command palette");
        await UiTestWaitAsync(0.3);
        palette.Hide();

        await UiTestWaitForAsync(() => !IsInstanceValid(doctor) && !IsInstanceValid(engine) && !IsInstanceValid(race)
            && !IsInstanceValid(shortcuts) && !IsInstanceValid(about) && !IsInstanceValid(palette) ? this : null, "the closed windows to be freed");
        return new List<(string What, System.WeakReference Weak)>
        {
            ("a closed Pack Doctor window", new System.WeakReference(doctor)),
            ("a closed Atlas Engine window", new System.WeakReference(engine)),
            ("a closed race mode dialog", new System.WeakReference(race)),
            ("a closed Keyboard Shortcuts dialog", new System.WeakReference(shortcuts)),
            ("a closed About dialog", new System.WeakReference(about)),
            ("a closed Help window", new System.WeakReference(help)),
            ("a closed Notifications window", new System.WeakReference(notifications)),
            ("a closed command palette", new System.WeakReference(palette)),
        };
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
            [AP_Atlas.UI.Tool.Settings] = _settingsPage!,
            [AP_Atlas.UI.Tool.Home] = _homePage!,
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

        // Atlas opens on Home, so start from another tab, as a user would.
        host.ShowTool(AP_Atlas.UI.Tool.MapPacks);
        // No slot connected: the tools that aren't per slot show their own view, and the slot tools ask for a slot.
        foreach (var tool in AP_Atlas.UI.Tool.All)
        {
            host.ShowTool(tool);
            await UiTestWaitAsync(0.05);
            UiTestExpect(_activityBar.Selected == tool, $"the activity bar isn't on {tool.Title}");
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

    private async Task MenuBarAndKeysAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        // The six menus, in order, and every command in one of them with its key shown.
        var menus = _menuHbox.GetChildren().OfType<MenuButton>().ToList();
        UiTestExpect(string.Join(",", menus.Select(m => m.Text)) == "File,Multiworld,View,Tools,Window,Help", $"the menus are {string.Join(", ", menus.Select(m => m.Text))}");
        var placed = _commandItems.Values.SelectMany(menu => menu.Values).ToHashSet();
        var missing = _commands!.All.Select(c => c.Id).Where(id => !placed.Contains(id)).ToList();
        UiTestExpect(missing.Count == 0, $"commands in no menu: {string.Join(", ", missing)}");
        var tools = menus[3].GetPopup();
        UiTestExpect(tools.GetItemText(0) == "Map Tracker" && ShortcutShown(tools, 0) == "Ctrl+1", $"the Tools menu's first item is \"{tools.GetItemText(0)}\" with \"{ShortcutShown(tools, 0)}\"");

        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        await PressAsync("Ctrl+0");
        UiTestExpect(ShownContent() == _packManagerPanel, "Ctrl+0 didn't show Map Packs");
        await PressAsync("Ctrl+7");
        UiTestExpect(ShownContent() == _sphereTab, "Ctrl+7 didn't show the Sphere Tracker");
        // A rebind in the settings takes over at once (as a user might type it), and the old key means nothing.
        _appSettings.KeyBindings["tool.map-packs"] = "ctrl+f6";
        try
        {
            await PressAsync("Ctrl+F6");
            UiTestExpect(ShownContent() == _packManagerPanel, "a rebound key (Ctrl+F6) didn't show Map Packs");
            host.ShowTool(AP_Atlas.UI.Tool.SphereTracker);
            await PressAsync("Ctrl+0");
            UiTestExpect(ShownContent() == _sphereTab, "the old key still works after a rebind");
            await PressAsync("F1");
            var shortcuts = await UiTestWaitForAsync(() => GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => d.Title == "Keyboard Shortcuts"), "the shortcuts dialog");
            var rows = Rows(shortcuts.FindChildren("*", nameof(Tree), true, false).OfType<Tree>().First());
            UiTestExpect(rows.Any(r => r[0] == "Map Packs" && r[1] == "Ctrl+F6") && rows.Any(r => r[0] == "Map Tracker" && r[1] == "Ctrl+1"),
                "the shortcuts list doesn't show the keys as they are now");
            shortcuts.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
        }
        finally
        {
            _appSettings.KeyBindings.Remove("tool.map-packs");
        }
        _commands.Run("help.about");
        var about = await UiTestWaitForAsync(() => GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => d.Title == "About The Archipelago Atlas"), "the About dialog");
        var aboutDialog = (AP_Atlas.UI.AboutDialog)about;
        UiTestExpect(aboutDialog.ShownText.TrimStart().StartsWith("The Archipelago Atlas " + AP_Atlas.Core.AtlasVersion.Display, StringComparison.Ordinal) && aboutDialog.ShownText.Contains("Lt-Tuttle"), "About's heading doesn't name the version, or the text the author");
        UiTestExpect(aboutDialog.SystemInfo.Contains("Godot ") && aboutDialog.SystemInfo.Contains(".NET") && aboutDialog.SystemInfo.Contains("Logic engine:")
            && !aboutDialog.SystemInfo.Contains(System.Environment.UserName), $"the system information is wrong, or names the user: {aboutDialog.SystemInfo}");
        UiTestExpect(aboutDialog.ShownText.Contains("Where Atlas goes online") && aboutDialog.ShownText.Contains("spheretracker.de"), "About has no privacy statement");
        about.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
        await UiTestWaitAsync(0.2);
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
    }

    private async Task SettingsPageAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var page = _settingsPage ?? throw new InvalidOperationException("The Settings page wasn't built.");
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        await PressAsync("Ctrl+,");
        UiTestExpect(ShownContent() == page && _activityBar.Selected == AP_Atlas.UI.Tool.Settings, "Ctrl+, didn't show the Settings page");
        UiTestExpect(page.SearchHasFocus, "the search box isn't ready to type into");
        UiTestExpect(_midLeftSidebar.Visible && page.SectionList.Visible, "the explorer doesn't list the sections");
        // The tools' keys still work while the search box has the focus.
        await PressAsync("Ctrl+0");
        UiTestExpect(ShownContent() == _packManagerPanel, "Ctrl+0 didn't switch tools while the search box had the focus");
        host.ShowTool(AP_Atlas.UI.Tool.Settings);

        // Each kind of row changes its setting at once and saves it.
        var reconnect = (CheckButton)page.ControlOf("auto-reconnect");
        UiTestExpect(reconnect.ButtonPressed && _appSettings.AutoReconnect, "auto-reconnect isn't on to begin with");
        reconnect.ButtonPressed = false;
        UiTestExpect(!_appSettings.AutoReconnect && !_sessions.AutoReconnect && !DataManager.LoadSettings().AutoReconnect, "turning auto-reconnect off didn't take, or wasn't saved");
        reconnect.ButtonPressed = true;
        UiTestExpect(_appSettings.AutoReconnect && _sessions.AutoReconnect, "auto-reconnect didn't come back");

        var race = (OptionButton)page.ControlOf("race-mode");
        UiTestExpect(race.Selected == 0 && AP_Atlas.Core.RaceRules.Mode == AP_Atlas.Core.RaceModeSetting.FollowServer, "race mode isn't following the server to begin with");
        race.Select(1);
        race.EmitSignal(OptionButton.SignalName.ItemSelected, 1); // Select() alone tells no one, unlike the user's pick
        UiTestExpect(AP_Atlas.Core.RaceRules.Mode == AP_Atlas.Core.RaceModeSetting.AlwaysOn, "picking Always on didn't set race mode");
        race.Select(0);
        race.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        UiTestExpect(AP_Atlas.Core.RaceRules.Mode == AP_Atlas.Core.RaceModeSetting.FollowServer, "race mode didn't go back to following the server");

        var accent = (OptionButton)page.ControlOf("accent");
        UiTestExpect(accent.Selected == 0, $"the accent choice shows item {accent.Selected}, not the default");
        accent.Select(1);
        accent.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        UiTestExpect(_appSettings.ThemeAccentColor == "#FFD700" && AP_Atlas.Core.ThemeColors.Accent == new Color("#FFD700") && DataManager.LoadSettings().ThemeAccentColor == "#FFD700",
            "picking an accent didn't recolour Atlas, or wasn't saved");
        accent.Select(0);
        accent.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        UiTestExpect(_appSettings.ThemeAccentColor == AP_Atlas.Core.ThemeColors.DefaultAccentHex && AP_Atlas.Core.ThemeColors.Accent == new Color(AP_Atlas.Core.ThemeColors.DefaultAccentHex),
            "the accent didn't go back to the default");

        // The theme: picking one recolours the frame at once and is saved; "Follow Windows" takes the PC's mode.
        var theme = (OptionButton)page.ControlOf("theme");
        int light = Array.FindIndex(AP_Atlas.Core.ThemeColors.ThemeChoices, choice => choice.Key == "light");
        theme.Select(light);
        theme.EmitSignal(OptionButton.SignalName.ItemSelected, light);
        UiTestExpect(ReferenceEquals(AP_Atlas.Core.ThemeColors.Current, AP_Atlas.Core.Palette.Light) && _appSettings.Theme == "light" && DataManager.LoadSettings().Theme == "light"
            && Theme!.GetStylebox("panel", "PanelContainer") is StyleBoxFlat lightPanel && lightPanel.BgColor == AP_Atlas.Core.Palette.Light.SurfacePanel,
            "picking the Light theme didn't take the light palette, restyle the window, or save");
        theme.Select(0);
        theme.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        UiTestExpect(_appSettings.Theme == "follow" && ReferenceEquals(AP_Atlas.Core.ThemeColors.Current, AP_Atlas.Core.ThemeColors.PaletteForSetting("follow")), "the theme didn't go back to following Windows");

        var fontSize = (SpinBox)page.ControlOf("menu-font-size");
        UiTestExpect((int)fontSize.Value == _appSettings.GlobalFontSize, "the font size row doesn't show the setting");
        fontSize.Value = 16;
        UiTestExpect(_appSettings.GlobalFontSize == 16 && _toolTitle.GetThemeFontSize("font_size") == 16 && DataManager.LoadSettings().GlobalFontSize == 16, "a new font size didn't apply, or wasn't saved");
        fontSize.Value = 14;
        UiTestExpect(_appSettings.GlobalFontSize == 14 && _toolTitle.GetThemeFontSize("font_size") == 14, "the font size didn't go back");

        var slots = (CheckButton)page.ControlOf("slots-panel");
        slots.ButtonPressed = false;
        UiTestExpect(!_sidebar.Visible && !_appSettings.ShowSlotsPanel, "hiding the slots panel from Settings didn't take");
        slots.ButtonPressed = true;
        UiTestExpect(_sidebar.Visible && _appSettings.ShowSlotsPanel, "the slots panel didn't come back");

        var developer = (CheckButton)page.ControlOf("developer-mode");
        UiTestExpect(!developer.ButtonPressed && _bottomTabs.IsTabHidden(2), "developer mode is on, or the Debug Log tab shows, to begin with");
        developer.ButtonPressed = true;
        UiTestExpect(!_bottomTabs.IsTabHidden(2) && DataManager.LoadSettings().DeveloperMode, "developer mode didn't show the Debug Log tab, or wasn't saved");
        developer.ButtonPressed = false;
        UiTestExpect(_bottomTabs.IsTabHidden(2) && !_appSettings.DeveloperMode, "the Debug Log tab didn't go away with developer mode");

        // A setting changed elsewhere shows as it is when the page shows again.
        _commands!.Run("view.status-bar");
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        host.ShowTool(AP_Atlas.UI.Tool.Settings);
        UiTestExpect(!((CheckButton)page.ControlOf("status-bar")).ButtonPressed, "the status bar's row doesn't show that the View menu hid it");
        _commands.Run("view.status-bar");
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        host.ShowTool(AP_Atlas.UI.Tool.Settings);
        UiTestExpect(((CheckButton)page.ControlOf("status-bar")).ButtonPressed, "the status bar's row doesn't show that it's back");

        // Search: typed words narrow the rows to those whose title, description or section has a word starting that way.
        page.Search("race");
        var shown = page.RowIds.Where(page.IsShown).ToList();
        UiTestExpect(shown.Count > 0 && shown.All(id => id.StartsWith("race", StringComparison.Ordinal) || id.StartsWith("key.", StringComparison.Ordinal)) && shown.Contains("race-mode") && shown.Contains("key.multiworld.race-mode") && !shown.Contains("auto-reconnect"), $"searching \"race\" shows {string.Join(", ", shown)}");
        UiTestExpect(!page.NothingMatches, "the page says nothing matches while rows show");
        page.Search("qzx");
        UiTestExpect(!page.RowIds.Any(page.IsShown) && page.NothingMatches, "a search nothing matches doesn't say so");
        page.Search("");
        UiTestExpect(page.RowIds.All(page.IsShown) && !page.NothingMatches, "clearing the search didn't bring every row back");

        // Jumping to a section scrolls to it (the explorer's buttons, a tool's gear button).
        page.ShowSection("data");
        await UiTestWaitAsync(0.1);
        UiTestExpect(page.ScrollPosition > 0, "jumping to the last section didn't scroll");
        page.ShowSection("multiworld");
        await UiTestWaitAsync(0.1);
        UiTestExpect(page.ScrollPosition == 0, "jumping to the first section didn't scroll back to the top");
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
    }

    private async Task KeyboardShortcutsAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var page = _settingsPage ?? throw new InvalidOperationException("The Settings page wasn't built.");
        const string RowId = "key.tool.map-packs";
        var tools = _menuHbox.GetChildren().OfType<MenuButton>().ToList()[3].GetPopup();
        int item = tools.GetItemIndex((int)_commandItems[tools].First(entry => entry.Value == "tool.map-packs").Key);
        var capture = (AP_Atlas.UI.KeyCapture)page.ControlOf(RowId);
        var reset = page.KeyResetOf(RowId);
        var conflict = page.KeyConflictOf(RowId);
        var otherConflict = page.KeyConflictOf("key.tool.map-tracker");
        ShowSettings("keyboard");
        await UiTestWaitAsync(0.1);
        UiTestExpect(ShownContent() == page && page.ScrollPosition > 0, "the keyboard section didn't show");
        UiTestExpect(capture.Key == "Ctrl+0" && capture.Text == "Ctrl+0" && reset.Disabled && !conflict.Visible, $"Map Packs' row shows \"{capture.Text}\", reset {(reset.Disabled ? "off" : "on")}, conflict {conflict.Visible}");
        try
        {
            // Press the key's button, then a key: the command runs on it at once, the menus and the bar show it, and it's saved.
            capture.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(capture.Capturing && capture.Text != "Ctrl+0", "pressing the key's button didn't start waiting for a key");
            capture.EmitSignal(Control.SignalName.GuiInput, AP_Atlas.UI.CommandKeys.ToEvent("Ctrl+F6")!);
            UiTestExpect(!capture.Capturing && capture.Key == "Ctrl+F6" && _appSettings.KeyBindings.GetValueOrDefault("tool.map-packs") == "Ctrl+F6"
                && DataManager.LoadSettings().KeyBindings.GetValueOrDefault("tool.map-packs") == "Ctrl+F6", "the pressed key wasn't taken, or wasn't saved");
            UiTestExpect(!reset.Disabled, "a changed key leaves the reset off");
            UiTestExpect(ShortcutShown(tools, item) == "Ctrl+F6" && _activityBar.ButtonOf(AP_Atlas.UI.Tool.MapPacks).TooltipText.Contains("Ctrl+F6"), "the menus and the bar don't show the new key");
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            await PressAsync("Ctrl+F6");
            UiTestExpect(ShownContent() == _packManagerPanel, "the new key doesn't run the command");
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            await PressAsync("Ctrl+0");
            UiTestExpect(ShownContent() == _connectionPanel, "the old key still runs the command");
            host.ShowTool(AP_Atlas.UI.Tool.Settings);

            // A key another command has: both rows say so.
            capture.EmitSignal(BaseButton.SignalName.Pressed);
            capture.EmitSignal(Control.SignalName.GuiInput, AP_Atlas.UI.CommandKeys.ToEvent("Ctrl+1")!);
            UiTestExpect(conflict.Visible && conflict.TooltipText.Contains("Map Tracker") && otherConflict.Visible && otherConflict.TooltipText.Contains("Map Packs"),
                $"a shared key isn't shown on both rows: {conflict.Visible} \"{conflict.TooltipText}\", {otherConflict.Visible} \"{otherConflict.TooltipText}\"");

            // Reset: the default key again, the rebind forgotten.
            reset.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(!_appSettings.KeyBindings.ContainsKey("tool.map-packs") && capture.Key == "Ctrl+0" && reset.Disabled && !conflict.Visible && !otherConflict.Visible
                && ShortcutShown(tools, item) == "Ctrl+0", "the reset didn't bring the default key back");

            // Backspace: no key at all.
            capture.EmitSignal(BaseButton.SignalName.Pressed);
            capture.EmitSignal(Control.SignalName.GuiInput, AP_Atlas.UI.CommandKeys.ToEvent("Backspace")!);
            UiTestExpect(capture.Key == "" && capture.Text == "None" && _appSettings.KeyBindings.GetValueOrDefault("tool.map-packs") == "" && ShortcutShown(tools, item) == "",
                $"Backspace didn't take the key away: \"{capture.Text}\", menu \"{ShortcutShown(tools, item)}\"");
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            await PressAsync("Ctrl+0");
            UiTestExpect(ShownContent() == _connectionPanel, "a key taken away still runs the command");
            host.ShowTool(AP_Atlas.UI.Tool.Settings);
            reset.EmitSignal(BaseButton.SignalName.Pressed);

            // Escape keeps the key; a modifier on its own isn't a key; losing the focus ends the wait.
            capture.EmitSignal(BaseButton.SignalName.Pressed);
            capture.EmitSignal(Control.SignalName.GuiInput, AP_Atlas.UI.CommandKeys.ToEvent("Escape")!);
            UiTestExpect(!capture.Capturing && capture.Key == "Ctrl+0" && !_appSettings.KeyBindings.ContainsKey("tool.map-packs"), "Escape didn't keep the key as it was");
            capture.EmitSignal(BaseButton.SignalName.Pressed);
            capture.EmitSignal(Control.SignalName.GuiInput, new InputEventKey { Pressed = true, Keycode = Key.Ctrl, CtrlPressed = true });
            UiTestExpect(capture.Capturing && capture.Key == "Ctrl+0", "a modifier on its own was taken as the key");
            capture.EmitSignal(Control.SignalName.FocusExited);
            UiTestExpect(!capture.Capturing && capture.Text == "Ctrl+0", "losing the focus didn't end the wait");

            // The rows are found by the command's words; the F1 list leads here.
            page.Search("map packs");
            UiTestExpect(page.IsShown(RowId) && !page.IsShown("auto-reconnect"), "the key row isn't found by its command's name");
            page.Search("");
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            await PressAsync("F1");
            var dialog = await UiTestWaitForAsync(() => GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => d.Title == "Keyboard Shortcuts"), "the shortcuts dialog");
            var change = dialog.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.Text == "Change Keys…");
            change.EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.2);
            UiTestExpect(ShownContent() == page && page.ScrollPosition > 0, "the F1 list's Change Keys… didn't open the keyboard settings");
        }
        finally
        {
            _appSettings.KeyBindings.Remove("tool.map-packs");
            DataManager.SaveSettings(_appSettings);
            RefreshShortcutsShown();
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
        }
    }

    private async Task PrivacyAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var page = _settingsPage ?? throw new InvalidOperationException("The Settings page wasn't built.");
        var panel = _privacyPanel ?? throw new InvalidOperationException("The privacy panel wasn't built.");
        var write = AP_Atlas.Core.Permissions.WriteArchipelago;
        var find = AP_Atlas.Core.Permissions.FindArchipelago;
        var lookups = AP_Atlas.Core.Permissions.GitHubLookups;
        const string Source = "github.com/example/some-apworld";
        _commands!.Run("tools.privacy");
        await UiTestWaitAsync(0.1);
        UiTestExpect(ShownContent() == page && page.ScrollPosition > 0, "Tools → Privacy & Permissions… didn't show the section");
        UiTestExpect(AP_Atlas.Core.Permissions.All.All(kind => panel.StateOf(kind) == "Asks each time") && panel.TakeBackButtons.Count == 0 && panel.StopTrustingButtons.Count == 0,
            $"with nothing allowed the panel shows {string.Join(", ", AP_Atlas.Core.Permissions.All.Select(panel.StateOf))}, {panel.TakeBackButtons.Count} to take back, {panel.StopTrustingButtons.Count} to stop trusting");
        try
        {
            // Every kind of answer shows as it is when the section shows again; a kept answer and a trusted source can be taken back, and that's saved.
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, write, @"C:\Games\Archipelago", true);
            AP_Atlas.Core.Permissions.AllowForSession(find);
            AP_Atlas.Core.Permissions.DenyForSession(lookups);
            _appSettings.ApprovedApworldSources.Add(Source);
            _commands.Run("tools.privacy");
            UiTestExpect(panel.StateOf(write) == "Always allowed" && panel.StateOf(find) == "Allowed until Atlas closes" && panel.StateOf(lookups) == "Not until Atlas restarts",
                $"the answers show as {panel.StateOf(write)} / {panel.StateOf(find)} / {panel.StateOf(lookups)}");
            UiTestExpect(panel.TakeBackButtons.Count == 1 && panel.StopTrustingButtons.Count == 1, $"{panel.TakeBackButtons.Count} to take back, {panel.StopTrustingButtons.Count} to stop trusting");
            panel.TakeBackButtons[0].EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(AP_Atlas.Core.Permissions.Granted(_appSettings).Count == 0 && DataManager.LoadSettings().PermissionsAllowed.Count == 0 && panel.StateOf(write) == "Asks each time" && panel.TakeBackButtons.Count == 0,
                "taking a kept answer back didn't take, or wasn't saved");
            panel.StopTrustingButtons[0].EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(_appSettings.ApprovedApworldSources.Count == 0 && DataManager.LoadSettings().ApprovedApworldSources.Count == 0 && panel.StopTrustingButtons.Count == 0,
                "stopping trusting a source didn't take, or wasn't saved");
            // The section is found by its words.
            page.Search("trusted sources");
            UiTestExpect(page.IsShown("privacy") && !page.IsShown("auto-reconnect"), "the privacy section isn't found by its words");
            page.Search("");
        }
        finally
        {
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, write, @"C:\Games\Archipelago", false);
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            _appSettings.ApprovedApworldSources.RemoveAll(s => s == Source);
            DataManager.SaveSettings(_appSettings);
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
        }
    }

    private async Task HomeAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var home = _homePage ?? throw new InvalidOperationException("Home wasn't built.");
        host.ShowTool(AP_Atlas.UI.Tool.MapPacks);
        await PressAsync("Ctrl+8");
        UiTestExpect(ShownContent() == home && _activityBar.Selected == AP_Atlas.UI.Tool.Home && !_midLeftSidebar.Visible, "Ctrl+8 didn't show Home on its own");
        // Every tool but Home has a card with a line of its own; every link is https, and the Discord one is the official invite.
        var noBlurb = AP_Atlas.UI.Tool.All.Where(t => t != AP_Atlas.UI.Tool.Home && AP_Atlas.UI.HomePage.Blurb(t).Length == 0).Select(t => t.Title).ToList();
        UiTestExpect(noBlurb.Count == 0, $"tools without a card line: {string.Join(", ", noBlurb)}");
        UiTestExpect(home.Links.Count > 0 && home.Links.All(l => l.Url.StartsWith("https://", StringComparison.Ordinal)) && home.Links.Any(l => l.Url == "https://discord.gg/8Z65BR2"),
            "a link isn't https, or the Discord one is missing");
        // The checklist reads Atlas's state: nothing done in a fresh folder (the engine step follows the engine).
        UiTestExpect(home.StepDone("engine") == AP_Atlas.Core.EngineSetup.AtlasEngine.Current.CanLaunch, "the engine step doesn't follow the engine");
        UiTestExpect(!home.StepDone("multiworld") && !home.StepDone("connect") && !home.StepDone("pack") && !home.StepDone("cheese"),
            $"steps done before anything happened: {string.Join(", ", home.StepIds.Where(home.StepDone))}");
        UiTestExpect(home.RecentProfileIds.Count == 0, "multiworlds listed while there are none");
        // A tip shows; Next tip shows the next one, around the end.
        int tip = home.TipIndex;
        UiTestExpect(tip >= 0 && home.TipText.Contains(AP_Atlas.UI.HomePage.Tips[tip]), "no tip shows");
        home.NextTip();
        UiTestExpect(home.TipIndex == (tip + 1) % AP_Atlas.UI.HomePage.Tips.Length && home.TipText.Contains(AP_Atlas.UI.HomePage.Tips[home.TipIndex]), "Next tip didn't show the next one");

        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "Home test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_home_pack.zip");
        FakeMapPack.Write(zip, "Home test pack", "Test Game");
        try
        {
            // A multiworld and a pack: their steps tick when Home shows again, and the multiworld is listed with a Connect button.
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            host.ShowTool(AP_Atlas.UI.Tool.Home);
            UiTestExpect(home.StepDone("multiworld") && home.StepDone("pack") && !home.StepDone("connect"),
                $"after a multiworld and a pack, the steps done are {string.Join(", ", home.StepIds.Where(home.StepDone))}");
            UiTestExpect(home.RecentProfileIds.SequenceEqual(new[] { profile.Id }) && !home.ConnectButtonOf(profile.Id).Disabled, "the new multiworld isn't listed with a Connect button");
            // One click connects its slot; the step ticks and the button says so.
            home.ConnectButtonOf(profile.Id).EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            await UiTestWaitForAsync(() => home.StepDone("connect") ? home : null, "the connect step's tick");
            UiTestExpect(home.ConnectButtonOf(profile.Id).Disabled && home.ConnectButtonOf(profile.Id).Text == "Connected", "a connected multiworld's button still offers to connect");
            // A tool's card shows the tool.
            home.ToolCardOf(AP_Atlas.UI.Tool.MapPacks).EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(ShownContent() == _packManagerPanel, "the Map Packs card didn't show Map Packs");
        }
        finally
        {
            AP_Atlas.Core.SafeFile.Delete(zip);
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
        }
    }

    private async Task HelpAsync()
    {
        _commands!.Run("help.guide");
        var help = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.HelpWindow>().FirstOrDefault(), "the Help window");
        try
        {
            UiTestExpect(help.PageIds.Count > 12 && help.PageIds[0] == "guide:Home" && help.CurrentPageId == "guide:Home", $"the guide opens on {help.CurrentPageId} with {help.PageIds.Count} topics");
            help.Select("guide:Hints");
            UiTestExpect(help.CurrentPageId == "guide:Hints" && help.ShownText.Contains("My Items"), "the Hints topic doesn't show the Hints section");
            help.Select(AP_Atlas.UI.HelpWindow.WhatsNew);
            UiTestExpect(help.ShownText.Contains("Added"), "What's new doesn't show the changelog");
            help.Select(AP_Atlas.UI.HelpWindow.Credits);
            UiTestExpect(help.ShownText.Contains("Lt-Tuttle") && help.ShownText.Contains("Godot"), "Credits & disclaimer doesn't name the author and the credits");
            help.Select(AP_Atlas.UI.HelpWindow.Licences);
            UiTestExpect(help.ShownText.Contains("MIT License"), "the licences don't show Atlas's licence");
            help.Select(AP_Atlas.UI.HelpWindow.GodotComponents);
            UiTestExpect(help.ShownText.Contains("FreeType") && help.ShownText.Contains("Licence: Expat") && help.ShownText.Contains("Permission is hereby granted") && help.ShownText.Contains("GODOT_COPYRIGHT.txt"),
                "Godot's components topic doesn't show the engine's components, their licences and the licence texts");
            // A second Help command uses the same window, at its topic.
            _commands.Run("help.whats-new");
            await UiTestWaitAsync(0.1);
            UiTestExpect(GetChildren().OfType<AP_Atlas.UI.HelpWindow>().Count() == 1 && help.CurrentPageId == AP_Atlas.UI.HelpWindow.WhatsNew,
                "a second Help command opened another window, or didn't show its topic");
            // Home's What's new card lists the newest changes and leads here.
            var home = _homePage ?? throw new InvalidOperationException("Home wasn't built.");
            UiTestExpect(home.WhatsNewLines.Count > 0, "Home's What's new card is empty");
            help.Select("guide:Home");
            home.WhatsNewButton.EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(help.CurrentPageId == AP_Atlas.UI.HelpWindow.WhatsNew, "Home's card didn't open What's new");
        }
        finally
        {
            help.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.2);
        }
    }

    private async Task AlertsAsync()
    {
        var feed = _alerts ?? throw new InvalidOperationException("The alert feed wasn't built.");
        int before = feed.Log.Entries.Count;
        var mine = new List<Control>();
        for (int i = 0; i < AP_Atlas.UI.AlertFeed.MostShown + 2; i++) mine.Add(feed.Show($"alert {i}", Colors.Orange, null, null));
        await UiTestWaitAsync(0.2);
        // At most the few newest show, stacked without overlapping; every one is in the history, newest first, with its kind.
        var shown = feed.Cards.Where(mine.Contains).ToList();
        UiTestExpect(shown.Count == AP_Atlas.UI.AlertFeed.MostShown && feed.Cards.Count <= AP_Atlas.UI.AlertFeed.MostShown, $"{feed.Cards.Count} cards show at once ({shown.Count} of this scenario's); the most is {AP_Atlas.UI.AlertFeed.MostShown}");
        var rects = feed.Cards.Select(c => c.GetGlobalRect()).OrderBy(r => r.Position.Y).ToList();
        for (int i = 1; i < rects.Count; i++)
            UiTestExpect(rects[i - 1].End.Y <= rects[i].Position.Y + 0.5f, $"cards overlap: one ends at {rects[i - 1].End.Y}, the next starts at {rects[i].Position.Y}");
        UiTestExpect(rects.Count == 0 || rects[^1].End.X <= GetViewport().GetVisibleRect().Size.X && rects[^1].End.Y <= GetViewport().GetVisibleRect().Size.Y, "a card is off the window");
        UiTestExpect(feed.Log.Entries.Count == before + AP_Atlas.UI.AlertFeed.MostShown + 2 && feed.Log.Entries[0].Message == $"alert {AP_Atlas.UI.AlertFeed.MostShown + 1}"
            && feed.Log.Entries[0].Kind == AP_Atlas.Core.AlertKind.Warning, "the history isn't every card, newest first, with its kind");
        // A card with a button: pressing it runs the action and the card goes. The × takes a card away too.
        bool ran = false;
        var withAction = feed.Show("do it", Colors.LightGreen, "Do", () => ran = true);
        await UiTestWaitAsync(0.1);
        feed.ActionButtonOf(withAction)!.EmitSignal(BaseButton.SignalName.Pressed);
        await UiTestWaitAsync(0.7);
        UiTestExpect(ran && !feed.Cards.Contains(withAction) && !GodotObject.IsInstanceValid(withAction), "the card's button didn't run its action, or the card stayed");
        var closed = feed.Show("close me", Colors.Salmon, null, null);
        await UiTestWaitAsync(0.1);
        feed.CloseButtonOf(closed).EmitSignal(BaseButton.SignalName.Pressed);
        await UiTestWaitAsync(0.7);
        UiTestExpect(!feed.Cards.Contains(closed), "the × didn't take the card away");
        // The plain cards go by themselves after their hold.
        await UiTestWaitAsync(AP_Atlas.UI.AlertFeed.Hold + 0.8);
        UiTestExpect(!feed.Cards.Any(mine.Contains), $"{feed.Cards.Count(mine.Contains)} cards stayed past their hold");
        // The Notifications window lists the history newest first, marks it seen; Clear empties it.
        UiTestExpect(feed.Log.Unseen > 0, "nothing counts as unseen before the window opens");
        _commands!.Run("window.notifications");
        var dialog = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.NotificationsDialog>().FirstOrDefault(), "the Notifications window");
        var rows = Rows(dialog.FindChildren("*", nameof(Tree), true, false).OfType<Tree>().First());
        UiTestExpect(rows.Count == feed.Log.Entries.Count && rows[0][2] == "close me" && rows[0][1] == "Error" && feed.Log.Unseen == 0,
            $"the window shows {rows.Count} rows (the log has {feed.Log.Entries.Count}), the first \"{(rows.Count > 0 ? rows[0][2] : "")}\", unseen {feed.Log.Unseen}");
        dialog.EmitSignal(AcceptDialog.SignalName.CustomAction, "clear");
        await UiTestWaitAsync(0.1);
        UiTestExpect(feed.Log.Entries.Count == 0 && dialog.RowCount == 0, "Clear didn't empty the history");
        dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
        await UiTestWaitAsync(0.2);
    }

    /// <summary>A test input for the custom accent: near black, which no heading could wear as it is (not a colour Atlas draws with).</summary>
    private const string NearBlackHex = "#101010";

    private async Task DeveloperModeAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var page = _settingsPage ?? throw new InvalidOperationException("The Settings page wasn't built.");
        var developer = (CheckButton)page.ControlOf("developer-mode");
        UiTestExpect(!_appSettings.DeveloperMode && _bottomTabs.IsTabHidden(2), "developer mode is on to begin with");

        // Off: the command is refused, the menu items are hidden, a hitch leaves the status bar alone.
        UiTestExpect(!_commands!.Run("view.debug-log"), "the Debug Log command ran with developer mode off");
        var viewMenu = _menuHbox.GetChildren().OfType<MenuButton>().First(m => m.Text == "View").GetPopup();
        int debugItem = Enumerable.Range(0, viewMenu.ItemCount).First(i => viewMenu.GetItemText(i) == Tr("Debug Log"));
        viewMenu.EmitSignal(PopupMenu.SignalName.AboutToPopup);
        UiTestExpect(viewMenu.IsItemDisabled(debugItem), "the View menu's Debug Log item isn't greyed with developer mode off");
        Button MenuItem(string text) => _bottomMenuBtn.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.Text == text);
        var popup = _bottomMenuBtn.GetChildren().OfType<PopupPanel>().First();
        _bottomMenuBtn.EmitSignal(BaseButton.SignalName.Pressed);
        UiTestExpect(!MenuItem("Copy Debug Log").Visible && !MenuItem("Clear Debug Log").Visible && MenuItem("Copy System Log").Visible, "the Debug Log's menu items show with developer mode off");
        popup.Hide();
        string status = _globalStatusLabel.Text;
        OnHitch("Hitch 200 ms: a probe");
        UiTestExpect(_globalStatusLabel.Text == status, "a hitch reached the status bar with developer mode off");

        // A diagnostic line: the Debug Log and the file, never the System Log.
        string probe = "diagnostic probe " + Guid.NewGuid().ToString("N");
        AP_Atlas.Core.Logger.LogDiagnostic(probe);
        await UiTestWaitAsync(0.2);
        UiTestExpect(_debugLogConsole.GetParsedText().Contains(probe) && !_consoleOutput.GetParsedText().Contains(probe), "a diagnostic line reached the System Log, or missed the Debug Log");
        string logFile = AP_Atlas.Core.Logger.CurrentLogPath;
        UiTestExpect(logFile != null && (await System.IO.File.ReadAllTextAsync(logFile)).Contains(probe), "a diagnostic line didn't reach the log file");

        // On: the tab, the command, the menu items and the hitch warnings.
        developer.ButtonPressed = true;
        UiTestExpect(_appSettings.DeveloperMode && !_bottomTabs.IsTabHidden(2), "developer mode didn't show the Debug Log tab");
        UiTestExpect(_commands.Run("view.debug-log") && _currentTerminalTab == 2 && _debugLogVBox.Visible, "the Debug Log command didn't show the Debug Log");
        viewMenu.EmitSignal(PopupMenu.SignalName.AboutToPopup);
        UiTestExpect(!viewMenu.IsItemDisabled(debugItem), "the View menu's Debug Log item stays greyed in developer mode");
        _bottomMenuBtn.EmitSignal(BaseButton.SignalName.Pressed);
        UiTestExpect(MenuItem("Copy Debug Log").Visible && MenuItem("Clear Debug Log").Visible, "the Debug Log's menu items don't show in developer mode");
        popup.Hide();
        OnHitch("Hitch 200 ms: a probe");
        UiTestExpect(_globalStatusLabel.Text == "Hitch 200 ms: a probe", "a hitch didn't reach the status bar in developer mode");
        ShowStatus(Tr("Ready"));

        // Off again while the Debug Log is current: Chat takes its place.
        developer.ButtonPressed = false;
        UiTestExpect(_bottomTabs.IsTabHidden(2) && _currentTerminalTab == 0, "hiding the Debug Log while it was current didn't give way to Chat");

        // The status bar's right end counts the connected slots.
        UiTestExpect(_statusConnectedLabel.Text == "", $"the status bar counts slots while none is connected: \"{_statusConnectedLabel.Text}\"");
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "UI test count", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        int statusMark = _statusShown.Count;
        await OnConnectSlotPressedAsync("Tester", profile);
        var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
        var statuses = _statusShown.Skip(statusMark).ToList();
        UiTestExpect(statuses.Contains(Tr("Connecting {0}…").Replace("{0}", "Tester")) && statuses.Contains(Tr("{0} connected; starting its logic…").Replace("{0}", "Tester")),
            $"the status bar didn't say the slot was connecting, then connected: {string.Join(" | ", statuses)}");
        UiTestExpect(!statuses.Any(s => s.Contains("Booting") || s.Contains("Socket") || s.StartsWith("[") || s.Contains("Error")), $"the status bar speaks in code: {string.Join(" | ", statuses)}");
        int connected = ActiveSlotNodes().OfType<SlotTrackerControl>().Count();
        string expected = connected == 1 ? Tr("1 slot connected") : Tr("{0} slots connected").Replace("{0}", connected.ToString());
        UiTestExpect(_statusConnectedLabel.Text == expected, $"the status bar says \"{_statusConnectedLabel.Text}\" with {connected} slot(s) connected");
        DeleteProfile(profile);
        await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester") == null ? this : null, "the slot to end");
        int left = ActiveSlotNodes().OfType<SlotTrackerControl>().Count();
        UiTestExpect(_statusConnectedLabel.Text == (left == 0 ? "" : left == 1 ? Tr("1 slot connected") : Tr("{0} slots connected").Replace("{0}", left.ToString())), $"the count didn't follow the slot's end: \"{_statusConnectedLabel.Text}\"");
        host.ShowTool(AP_Atlas.UI.Tool.Home);
    }

    private async Task AccessibilityAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var page = _settingsPage ?? throw new InvalidOperationException("The Settings page wasn't built.");

        // A kit button takes the focus and Enter presses it; one beside a field can opt out.
        int presses = 0;
        var button = AP_Atlas.UI.Kit.Button("Press", "What it does", () => presses++);
        AddChild(button);
        UiTestExpect(button.FocusMode == Control.FocusModeEnum.All, "a kit button refuses the keyboard focus");
        button.GrabFocus();
        UiTestExpect(button.HasFocus(), "a kit button couldn't take the focus");
        await PressAsync("Enter"); // a button presses on the key's release, as a mouse button does
        var release = AP_Atlas.UI.CommandKeys.ToEvent("Enter") ?? throw new InvalidOperationException("Enter isn't a key");
        release.Pressed = false;
        GetViewport().PushInput(release);
        await UiTestWaitAsync(0.1);
        UiTestExpect(presses == 1, $"Enter on a focused button ran it {presses} times, not once");
        var quiet = AP_Atlas.UI.Kit.Button("Send", "Beside a field", () => { }, focusable: false);
        UiTestExpect(quiet.FocusMode == Control.FocusModeEnum.None, "a kit button told not to take the focus takes it");
        button.QueueFree();

        // A symbol-only kit button is named by its tooltip; one with words isn't renamed.
        var symbol = AP_Atlas.UI.Kit.Button("◀", "Back", () => { });
        var worded = AP_Atlas.UI.Kit.Button("Back", "Go back", () => { });
        UiTestExpect(symbol.AccessibilityName == "Back" && worded.AccessibilityName == "", "a symbol-only kit button isn't named by its tooltip, or a worded one is renamed");
        UiTestExpect(AP_Atlas.UI.Kit.IsSymbolOnly("…") && AP_Atlas.UI.Kit.IsSymbolOnly("") && !AP_Atlas.UI.Kit.IsSymbolOnly("Fit") && !AP_Atlas.UI.Kit.IsSymbolOnly("+1"), "symbol-only text isn't told from words");

        // The theme's focus ring, on every kind of control that takes the focus.
        var theme = Theme ?? throw new InvalidOperationException("the window has no theme");
        foreach (string type in new[] { "Button", "CheckBox", "CheckButton", "OptionButton", "MenuButton", "LineEdit", "TextEdit", "Tree", "ItemList" })
        {
            var ring = theme.HasStylebox("focus", type) ? theme.GetStylebox("focus", type) as StyleBoxFlat : null;
            UiTestExpect(ring != null && !ring.DrawCenter && ring.BorderWidthTop >= 2 && ring.BorderColor.A > 0.9f, $"the theme draws no focus ring for {type}");
        }

        // Tab from the Settings search box moves the focus on.
        host.ShowTool(AP_Atlas.UI.Tool.Settings);
        await UiTestWaitAsync(0.1);
        var search = page.FindChildren("*", nameof(LineEdit), true, false).OfType<LineEdit>().First();
        search.GrabFocus();
        var next = search.FindNextValidFocus();
        UiTestExpect(next != null && next != search && next.FocusMode == Control.FocusModeEnum.All, $"Tab from the search box leads to {next?.Name ?? "nothing"}");

        // No button anywhere shows only a symbol without a name; no button refuses the focus (the palette's list and the hint suggestions aren't buttons).
        host.ShowTool(AP_Atlas.UI.Tool.Home);
        ShowToast("An accessibility probe", AP_Atlas.Core.ThemeColors.Info); // an alert card's buttons are scanned too
        await UiTestWaitAsync(0.1);
        var buttons = GetTree().Root.FindChildren("*", nameof(BaseButton), true, false).OfType<BaseButton>().ToList();
        GD.Print($"UITEST INFO Accessibility: {buttons.Count} buttons scanned ({buttons.Count(b => b is CheckButton)} toggles, {buttons.Count(b => b is OptionButton)} choices, {buttons.Count(b => string.IsNullOrEmpty(b.AccessibilityName))} without a name of their own)");
        UiTestExpect(buttons.Count(b => b is CheckButton) >= 10, $"the scan sees only {buttons.Count(b => b is CheckButton)} toggles: the Settings page's rows aren't in it");
        var unnamed = buttons.Where(b => AP_Atlas.UI.Kit.IsSymbolOnly((b as Button)?.Text ?? "") && string.IsNullOrEmpty(b.AccessibilityName))
            .Select(b => $"{b.GetPath()} (text \"{(b as Button)?.Text}\", tooltip \"{b.TooltipText}\")").ToList();
        UiTestExpect(unnamed.Count == 0, $"{unnamed.Count} buttons show only a symbol and have no name: {string.Join(", ", unnamed.Take(8))}");
        var refusing = buttons.Where(b => b.FocusMode == Control.FocusModeEnum.None).Select(b => b.GetPath().ToString()).ToList();
        UiTestExpect(refusing.Count == 0, $"{refusing.Count} buttons refuse the keyboard focus: {string.Join(", ", refusing.Take(8))}");
    }

    private async Task MultiworldsPageAsync()
    {
        const string roomId = "AbCdEfGhIjKlMnOpQrStUw";
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        await using var site = new FakeWebSite();
        int port = 40000;
        string lastActivity = DateTime.UtcNow.ToString("R");
        site.Respond = path => path == "/api/room_status/" + roomId
            ? (200, $$"""{"tracker": "x", "players": [["Alice", "Bob"]], "last_port": {{port}}, "last_activity": "{{lastActivity}}", "timeout": 7200, "downloads": []}""")
            : (404, "");
        string roomLink = site.Site + "/room/" + roomId;
        AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.RoomStatusReads, null, true);
        var profile = new MultiworldProfile { Name = "Room test" };
        profile.Slots.Add("Player1"); // as New Multiworld makes one
        var other = new MultiworldProfile { Name = "Other" };
        _profiles.Add(profile);
        _profiles.Add(other);
        RefreshProfileList();
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        try
        {
            // The room link fills in the server (the link's host and the room's port) and the slots (the room's players), and is kept.
            SelectProfile(profile);
            UiTestExpect(profile.Slots.Count == 1 && IsDefaultSlotName(profile.Slots[0]), "a new multiworld doesn't start with Atlas's placeholder slot");
            _roomLinkInput.Text = roomLink;
            _roomLinkInput.EmitSignal(LineEdit.SignalName.TextChanged, roomLink);
            await FillFromRoomLinkAsync();
            UiTestExpect(profile.ServerUrl == "127.0.0.1:40000" && _serverInput.Text == profile.ServerUrl, $"the server wasn't filled in from the room: \"{profile.ServerUrl}\"");
            UiTestExpect(profile.Slots.SequenceEqual(new[] { "Alice", "Bob" }), $"the slots weren't filled in from the room's players: {string.Join(", ", profile.Slots)}");
            UiTestExpect(profile.RoomLink == roomLink && site.Requests.Count == 1 && site.Requests[0] == "/api/room_status/" + roomId, $"the room link wasn't kept, or the site saw {string.Join(", ", site.Requests)}");
            // A slot's row carries its name (the Connect All Slots row doesn't); the old rows go at the frame's end.
            HBoxContainer[] SlotRows() => _slotsListVBox.GetChildren().OfType<HBoxContainer>().Where(row => !row.IsQueuedForDeletion() && row.HasMeta("slot_name")).ToArray();
            UiTestExpect(SlotRows().Select(row => row.GetMeta("slot_name").AsString()).SequenceEqual(new[] { "Alice", "Bob" }), $"the slot rows don't show the room's players ({string.Join(", ", SlotRows().Select(row => row.GetMeta("slot_name").AsString()))})");
            // A tracker link isn't a room link; nothing is read.
            _roomLinkInput.Text = site.Site + "/tracker/" + roomId;
            await FillFromRoomLinkAsync();
            UiTestExpect(site.Requests.Count == 1 && _alertLog.Entries.Any(e => e.Message.Contains("room's own link")), "a tracker link was read as a room link, or the user wasn't told");

            // Edits are kept when another multiworld is selected, and written to disk.
            SelectProfile(profile);
            _nameInput.Text = "Room test, edited";
            _nameInput.EmitSignal(LineEdit.SignalName.TextChanged, _nameInput.Text);
            UiTestExpect(profile.Name == "Room test, edited" && _dirty, "typing a name didn't reach the multiworld");
            SelectProfile(other);
            SelectProfile(profile);
            UiTestExpect(_nameInput.Text == "Room test, edited" && !_dirty && DataManager.LoadProfiles().Any(p => p.Id == profile.Id && p.Name == "Room test, edited" && p.RoomLink == roomLink),
                "the edited name was lost when another multiworld was selected, or wasn't written to disk");

            // A slot's rename: not per keystroke; on Enter, with its saved stats; a duplicate is refused.
            profile.SavedStats["Alice"] = new SlotStats { GameName = "Test Game" };
            var aliceRow = SlotRows().First(row => row.GetMeta("slot_name").AsString() == "Alice");
            var aliceEdit = aliceRow.GetChild<LineEdit>(0);
            aliceEdit.Text = "Alicia";
            aliceEdit.EmitSignal(LineEdit.SignalName.TextChanged, "Alicia");
            UiTestExpect(profile.Slots[0] == "Alice", "a keystroke renamed the slot");
            aliceEdit.EmitSignal(LineEdit.SignalName.TextSubmitted, "Alicia");
            UiTestExpect(profile.Slots[0] == "Alicia" && profile.SavedStats.ContainsKey("Alicia") && !profile.SavedStats.ContainsKey("Alice") && aliceRow.GetMeta("slot_name").AsString() == "Alicia",
                "Enter didn't rename the slot with its saved stats");
            aliceEdit.Text = "Bob";
            aliceEdit.EmitSignal(LineEdit.SignalName.FocusExited);
            UiTestExpect(profile.Slots[0] == "Alicia" && aliceEdit.Text == "Alicia", "a duplicate name was taken, or the field wasn't put back");

            // A connection the server refuses shows as a card.
            await using var server = new FakeArchipelagoServer();
            server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567", new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
            var refusedProfile = new MultiworldProfile { Name = "Refusing", ServerUrl = server.Url.ToString() };
            refusedProfile.Slots.Clear();
            refusedProfile.Slots.Add("Nobody");
            _profiles.Add(refusedProfile);
            int cards = _alertLog.Entries.Count;
            await OnConnectSlotPressedAsync("Nobody", refusedProfile);
            // The log keeps its newest card first: the cards since a count are at its front.
            IEnumerable<AP_Atlas.Core.AlertEntry> CardsSince(int count) => _alertLog.Entries.Take(Math.Max(0, _alertLog.Entries.Count - count));
            var refusedCard = await UiTestWaitForAsync(() => CardsSince(cards).FirstOrDefault(e => e.Message.Contains("Nobody")), "a card about the refused login");
            UiTestExpect(refusedCard.Message.Contains("refused"), $"the refused login's card says \"{refusedCard.Message}\"");
            DeleteProfile(refusedProfile);

            // After a reconnect gives up: the room moved to another port, offered as a card whose action takes it.
            port = 40123;
            cards = _alertLog.Entries.Count;
            OnReconnectStopped(new SlotId(profile.Id, "Alicia"), null);
            Control? CardWith(string text) => _alerts.Cards.FirstOrDefault(card => card.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(label => label.Text.Contains(text))); // a card's message is a label
            var moved = await UiTestWaitForAsync(() => CardWith("port 40123"), "the moved-port card");
            var usePort = _alerts.ActionButtonOf(moved);
            UiTestExpect(usePort != null && usePort.Text == "Use port 40123", $"the card's action is \"{usePort?.Text}\"");
            UiTestExpect(site.Requests.Count == 2, $"the site saw {site.Requests.Count} requests, not one more");
            OnReconnectStopped(new SlotId(profile.Id, "Alicia"), null);
            // A read that shouldn't happen would only reach the site after the polite spacing between requests to one site, so the wait outlasts it.
            await UiTestWaitAsync(AP_Atlas.Core.PoliteHttp.Spacing.TotalSeconds + 1);
            UiTestExpect(site.Requests.Count == 2, "a second failure within ten minutes read the status again");
            usePort!.EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.3);
            UiTestExpect(profile.ServerUrl == "127.0.0.1:40123" && DataManager.LoadProfiles().Any(p => p.Id == profile.Id && p.ServerUrl == "127.0.0.1:40123"), "taking the new port didn't change the server address, or wasn't saved");

            // The room is asleep: said as a card that opens the room page (in the browser, on the user's click only).
            lastActivity = DateTime.UtcNow.AddHours(-3).ToString("R"); // wall clock: the fake room's last activity is the server's own time, three hours ago
            _roomChecks.Clear(); // ten minutes later
            cards = _alertLog.Entries.Count;
            OnReconnectStopped(new SlotId(profile.Id, "Alicia"), null);
            var asleep = await UiTestWaitForAsync(() => CardWith("asleep"), "the asleep card");
            var openPage = _alerts.ActionButtonOf(asleep);
            UiTestExpect(openPage != null && openPage.Text == "Open the room page", $"the asleep card's action is \"{openPage?.Text}\"");
            UiTestExpect(!site.Requests.Any(path => path.StartsWith("/room/", StringComparison.Ordinal)), "the room's page was requested");

        }
        finally
        {
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.RoomStatusReads, null, false);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            if (_profiles.Contains(other)) DeleteProfile(other);
            host.ShowTool(AP_Atlas.UI.Tool.Home);
        }
    }

    private async Task CustomizationAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var page = _settingsPage ?? throw new InvalidOperationException("The Settings page wasn't built.");
        host.ShowTool(AP_Atlas.UI.Tool.Settings);

        // A custom accent: any colour, saved once the picking settles; headings and links still read on the surface.
        var picker = (ColorPickerButton)page.ControlOf("custom-accent");
        picker.Color = new Color(NearBlackHex);
        picker.EmitSignal(ColorPickerButton.SignalName.ColorChanged, picker.Color);
        UiTestExpect(_appSettings.ThemeAccentColor != NearBlackHex, "the custom accent was applied before the picking settled");
        await UiTestWaitAsync(0.6);
        UiTestExpect(_appSettings.ThemeAccentColor == NearBlackHex && AP_Atlas.Core.ThemeColors.Accent == new Color(NearBlackHex) && DataManager.LoadSettings().ThemeAccentColor == NearBlackHex,
            "a custom accent didn't recolour Atlas, or wasn't saved");
        double headingContrast = AP_Atlas.Core.ThemeColors.Contrast(AP_Atlas.Core.ThemeColors.Heading, AP_Atlas.Core.ThemeColors.Current.Surface);
        double linkContrast = AP_Atlas.Core.ThemeColors.Contrast(AP_Atlas.Core.ThemeColors.Link, AP_Atlas.Core.ThemeColors.Current.Surface);
        UiTestExpect(headingContrast >= 3.0 && linkContrast >= 4.5, $"with a near-black accent, headings read at {headingContrast:0.0} to 1 and links at {linkContrast:0.0}");
        var accent = (OptionButton)page.ControlOf("accent");
        UiTestExpect(accent.Selected == AccentPresets.Length && accent.GetItemText(accent.Selected) == Tr("Custom"), "the preset choice doesn't say Custom for a custom accent");
        accent.Select(0);
        accent.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        UiTestExpect(_appSettings.ThemeAccentColor == AP_Atlas.Core.ThemeColors.DefaultAccentHex && picker.Color == AP_Atlas.Core.ThemeColors.Accent, "the accent didn't go back to the default, or the picker doesn't show it");

        // Colour-blind-safe colours: the theme's other palette (one of the palettes the self-test holds to the contrast rule), saved, and undone.
        var safe = (CheckButton)page.ControlOf("colour-blind-safe");
        var usualSuccess = AP_Atlas.Core.ThemeColors.Success;
        var usualPalette = AP_Atlas.Core.ThemeColors.Current;
        safe.ButtonPressed = true;
        UiTestExpect(_appSettings.ColourBlindSafe && DataManager.LoadSettings().ColourBlindSafe
            && AP_Atlas.Core.ThemeColors.Current.Name == usualPalette.Name + AP_Atlas.Core.Palette.ColourBlindSafeSuffix
            && AP_Atlas.Core.Palette.All.Contains(AP_Atlas.Core.ThemeColors.Current)
            && AP_Atlas.Core.ThemeColors.Success != usualSuccess && AP_Atlas.Core.ThemeColors.Text == usualPalette.Text,
            "colour-blind-safe colours didn't take the theme's other palette, or weren't saved");
        safe.ButtonPressed = false;
        UiTestExpect(!_appSettings.ColourBlindSafe && ReferenceEquals(AP_Atlas.Core.ThemeColors.Current, usualPalette), "the usual colours didn't come back");

        // Zoom: the choice scales the window at once and is saved; Ctrl+= and Ctrl+- step through the choices.
        var zoom = (OptionButton)page.ControlOf("ui-zoom");
        UiTestExpect(zoom.Selected == Array.IndexOf(ZoomSteps, 100) && Mathf.IsEqualApprox(GetWindow().ContentScaleFactor, 1f), "the zoom isn't 100% to begin with");
        int step150 = Array.IndexOf(ZoomSteps, 150);
        zoom.Select(step150);
        zoom.EmitSignal(OptionButton.SignalName.ItemSelected, step150);
        UiTestExpect(_appSettings.UiZoom == 150 && Mathf.IsEqualApprox(GetWindow().ContentScaleFactor, 1.5f) && DataManager.LoadSettings().UiZoom == 150, "picking 150% didn't scale the window, or wasn't saved");
        await PressAsync("Ctrl+=");
        UiTestExpect(_appSettings.UiZoom == 175 && Mathf.IsEqualApprox(GetWindow().ContentScaleFactor, 1.75f) && zoom.Selected == Array.IndexOf(ZoomSteps, 175), "Ctrl+= didn't zoom in a step, or the choice doesn't show it");
        await PressAsync("Ctrl+-");
        await PressAsync("Ctrl+-");
        UiTestExpect(_appSettings.UiZoom == 125, $"two Ctrl+- didn't zoom out two steps (at {_appSettings.UiZoom}%)");
        _appSettings.UiZoom = 130; // between two steps: a step down lands on the step below, a step up on the one above
        ZoomBy(-1);
        UiTestExpect(_appSettings.UiZoom == 125, $"from 130%, a step down went to {_appSettings.UiZoom}%");
        _appSettings.UiZoom = 130;
        ZoomBy(1);
        UiTestExpect(_appSettings.UiZoom == 150, $"from 130%, a step up went to {_appSettings.UiZoom}%");
        int step100 = Array.IndexOf(ZoomSteps, 100);
        zoom.Select(step100);
        zoom.EmitSignal(OptionButton.SignalName.ItemSelected, step100);
        UiTestExpect(_appSettings.UiZoom == 100 && Mathf.IsEqualApprox(GetWindow().ContentScaleFactor, 1f), "the zoom didn't go back to 100%");

        // The pin shape: saved, every map redraws; a diamond is a square turned on its corner, a round pin's corners are as wide as it is.
        var markers = (OptionButton)page.ControlOf("map-markers");
        int redraws = 0;
        Action redrawn = () => redraws++;
        AP_Atlas.UI.MapTrackerControl.DisplayOptionsChanged += redrawn;
        try
        {
            markers.Select(2);
            markers.EmitSignal(OptionButton.SignalName.ItemSelected, 2);
        }
        finally
        {
            AP_Atlas.UI.MapTrackerControl.DisplayOptionsChanged -= redrawn;
        }
        UiTestExpect(_appSettings.MapMarkerStyle == "diamond" && DataManager.LoadSettings().MapMarkerStyle == "diamond" && redraws == 1, "picking Diamond wasn't saved, or the maps weren't told to redraw");
        var pin = new Button { Size = new Vector2(20, 20) };
        AddChild(pin);
        var style = new StyleBoxFlat();
        AP_Atlas.UI.MapTrackerControl.ShapePin(style, pin, "diamond", 20);
        UiTestExpect(style.CornerRadiusTopLeft == 0 && Mathf.IsEqualApprox(pin.Rotation, Mathf.Pi / 4) && pin.PivotOffset == new Vector2(10, 10), "a diamond pin isn't a square turned on its corner around its centre");
        AP_Atlas.UI.MapTrackerControl.ShapePin(style, pin, "square", 20);
        UiTestExpect(style.CornerRadiusTopLeft == 0 && pin.Rotation == 0f, "a square pin has rounded corners or is turned");
        AP_Atlas.UI.MapTrackerControl.ShapePin(style, pin, "round", 20);
        UiTestExpect(style.CornerRadiusTopLeft == 20 && style.CornerRadiusBottomRight == 20 && pin.Rotation == 0f, "a round pin's corners aren't as wide as the pin");
        pin.QueueFree();
        markers.Select(0);
        markers.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        UiTestExpect(_appSettings.MapMarkerStyle == "round", "the pins didn't go back to round");

        // Where Atlas opens: the choice is saved; the last tool shown is remembered for "where I left off"; an unknown tool means Home.
        var startup = (OptionButton)page.ControlOf("startup");
        startup.Select(2);
        startup.EmitSignal(OptionButton.SignalName.ItemSelected, 2);
        UiTestExpect(_appSettings.StartupPage == "multiworlds" && DataManager.LoadSettings().StartupPage == "multiworlds" && StartupTool(_appSettings) == AP_Atlas.UI.Tool.Connections,
            "picking Multiworlds wasn't saved, or doesn't open on Multiworlds");
        startup.Select(1);
        startup.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        host.ShowTool(AP_Atlas.UI.Tool.Hints);
        await UiTestWaitAsync(0.8);
        UiTestExpect(_appSettings.LastTool == "hints" && DataManager.LoadSettings().LastTool == "hints" && StartupTool(_appSettings) == AP_Atlas.UI.Tool.Hints,
            "the last tool shown wasn't remembered for where I left off");
        UiTestExpect(StartupTool(new AppSettings { StartupPage = "last", LastTool = "no-such-tool" }) == AP_Atlas.UI.Tool.Home && StartupTool(new AppSettings()) == AP_Atlas.UI.Tool.Home,
            "an unknown last tool, or the default, doesn't open on Home");
        host.ShowTool(AP_Atlas.UI.Tool.Settings);
        startup.Select(0);
        startup.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        UiTestExpect(_appSettings.StartupPage == "home", "opening on Home didn't come back");
    }

    private async Task UiKitAsync()
    {
        // A button from the kit runs its action once per press, honours "enabled" and keeps its tooltip; a small one is scaled down.
        int presses = 0;
        var button = AP_Atlas.UI.Kit.Button("Press", "What it does", () => presses++);
        AddChild(button);
        button.EmitSignal(BaseButton.SignalName.Pressed);
        UiTestExpect(presses == 1 && button.TooltipText == "What it does" && !button.Disabled, $"a kit button ran its action {presses} times, or lost its tooltip");
        var off = AP_Atlas.UI.Kit.Button("Off", null, () => presses++, enabled: false, small: true);
        off.EmitSignal(BaseButton.SignalName.Pressed);
        UiTestExpect(off.Disabled && off.TooltipText == "" && off.HasMeta("font_size_ratio") && presses == 2, "a disabled small kit button isn't disabled, or isn't small");
        // Its text lines wrap and take the palette's colours.
        UiTestExpect(AP_Atlas.UI.Kit.Text("t").GetThemeColor("font_color") == AP_Atlas.Core.ThemeColors.Text
            && AP_Atlas.UI.Kit.Muted("t").GetThemeColor("font_color") == AP_Atlas.Core.ThemeColors.TextMuted
            && AP_Atlas.UI.Kit.Subtle("t").GetThemeColor("font_color") == AP_Atlas.Core.ThemeColors.TextSubtle
            && AP_Atlas.UI.Kit.Text("t").AutowrapMode != TextServer.AutowrapMode.Off, "the kit's text lines don't take the palette's colours, or don't wrap");
        // Every heading from the kit wears the accent's heading colour and follows an accent change, wherever it is: one added here, Home's, the Settings page's.
        var heading = AP_Atlas.UI.Kit.Heading("Heading");
        AddChild(heading);
        var headings = GetTree().Root.FindChildren("*", nameof(Label), true, false).OfType<Label>().Where(l => l.HasMeta("kit_heading")).ToList();
        UiTestExpect(headings.Count >= 6 && headings.All(l => l.GetThemeColor("font_color") == AP_Atlas.Core.ThemeColors.Heading), $"of {headings.Count} kit headings, not every one wears the heading colour");
        var wordmark = _homePage?.WordmarkRect ?? throw new InvalidOperationException("Home has no wordmark");
        var wordmarkBefore = wordmark.Texture;
        UiTestExpect(wordmarkBefore != null && wordmark.CustomMinimumSize.Y == 80 && wordmarkBefore.GetHeight() == 160, $"Home's wordmark isn't drawn at 80 px (twice that for sharpness): {wordmarkBefore?.GetSize()}");
        ApplyAccent("#FFD700");
        UiTestExpect(wordmark.Texture != null && !ReferenceEquals(wordmark.Texture, wordmarkBefore), "the wordmark didn't follow the accent");
        var changed = AP_Atlas.Core.ThemeColors.Heading;
        UiTestExpect(AP_Atlas.Core.ThemeColors.Accent == new Color("#FFD700") && changed != new Color(AP_Atlas.Core.ThemeColors.DefaultAccentHex).Lightened(0.2f)
            && headings.All(l => l.GetThemeColor("font_color") == changed), "after an accent change, not every kit heading follows it");
        ApplyAccent(AP_Atlas.Core.ThemeColors.DefaultAccentHex);
        UiTestExpect(headings.All(l => l.GetThemeColor("font_color") == AP_Atlas.Core.ThemeColors.Heading), "after the accent went back, not every kit heading followed");
        // The alert feed names a card's kind from the palette.
        UiTestExpect(AP_Atlas.UI.AlertFeed.KindOf(AP_Atlas.Core.ThemeColors.Danger) == AP_Atlas.Core.AlertKind.Error
            && AP_Atlas.UI.AlertFeed.KindOf(AP_Atlas.Core.ThemeColors.Warning) == AP_Atlas.Core.AlertKind.Warning
            && AP_Atlas.UI.AlertFeed.KindOf(AP_Atlas.Core.ThemeColors.Success) == AP_Atlas.Core.AlertKind.Success
            && AP_Atlas.UI.AlertFeed.KindOf(AP_Atlas.Core.ThemeColors.TextSubtle) == AP_Atlas.Core.AlertKind.Info, "the alert feed doesn't name kinds from the palette");
        button.QueueFree();
        off.QueueFree();
        heading.QueueFree();
        await UiTestWaitAsync(0.1);
    }

    private async Task TablesAsync()
    {
        var table = new AP_Atlas.UI.AtlasTable("uitest-table", _appSettings, text => text) { Toast = ShowToast };
        AddChild(table);
        table.SetColumns(new List<AP_Atlas.UI.AtlasTable.Column>
        {
            new() { Id = "name", Title = "Name", MinWidth = 100 },
            new() { Id = "count", Title = "Count", MinWidth = 60, Align = HorizontalAlignment.Right, DescendingFirst = true },
            new() { Id = "note", Title = "Note", MinWidth = 100 }
        });
        AP_Atlas.UI.AtlasTable.Row Row(string key, string name, int count, string note, bool pinned = false) => new()
        {
            Key = key,
            Pinned = pinned,
            Cells = new[] { new AP_Atlas.UI.AtlasTable.Cell(name), new AP_Atlas.UI.AtlasTable.Cell(count.ToString(), null, null, count), new AP_Atlas.UI.AtlasTable.Cell(note) }
        };
        var rows = new List<AP_Atlas.UI.AtlasTable.Row> { Row("a", "Slot 10", 5, "banana bread"), Row("b", "Slot 2", 12, "apple"), Row("c", "Slot 1", 7, "band camp"), Row("d", "Slot 3", 1, "cherry") };
        table.SetRows(rows);
        await UiTestWaitAsync(0.1);
        string Order() => string.Join(",", table.ShownRows.Select(r => r.Key));
        UiTestExpect(Order() == "a,b,c,d" && table.CountLabel.Text == "4 rows", $"without a sort the rows aren't in the order given: {Order()} ({table.CountLabel.Text})");
        // A title click sorts (a column's own first direction, then the other), and the title says which way.
        table.Tree.EmitSignal(Tree.SignalName.ColumnTitleClicked, 1, (long)MouseButton.Left);
        UiTestExpect(Order() == "b,c,a,d" && table.Tree.GetColumnTitle(1).EndsWith("▼"), $"the first click on Count didn't sort it downward: {Order()} \"{table.Tree.GetColumnTitle(1)}\"");
        table.Tree.EmitSignal(Tree.SignalName.ColumnTitleClicked, 1, (long)MouseButton.Left);
        UiTestExpect(Order() == "d,a,c,b" && table.Tree.GetColumnTitle(1).EndsWith("▲"), $"the second click didn't turn the order: {Order()}");
        table.SortBy("name");
        await UiTestWaitAsync(0.8); // the sort is saved half a second later, apart from the hidden column below
        UiTestExpect(Order() == "c,b,d,a", $"names don't sort naturally (Slot 2 before Slot 10): {Order()}");
        // Pinned rows stay on top whichever way the table sorts.
        rows[3].Pinned = true;
        table.SetRows(rows);
        UiTestExpect(Order() == "d,c,b,a", $"a pinned row isn't first: {Order()}");
        rows[3].Pinned = false;
        // Typed words narrow the rows: each word must start a word of a cell.
        table.SearchBox.Text = "ban";
        table.Render();
        UiTestExpect(Order() == "c,a" && table.CountLabel.Text == "2 of 4 rows", $"the search didn't narrow to the rows with a word starting \"ban\": {Order()} ({table.CountLabel.Text})");
        table.SearchBox.Text = "ban camp";
        table.Render();
        UiTestExpect(Order() == "c", $"two typed words didn't both have to match: {Order()}");
        table.SearchBox.Text = "";
        table.Render();
        // A column hides and shows; the sort and the hidden columns are saved with the settings.
        table.ToggleColumn(2);
        UiTestExpect(table.ShownColumns.Count == 2 && table.Tree.Columns == 2 && table.ShownIndexOf("note") < 0, "hiding a column didn't take it out of the tree");
        await UiTestWaitAsync(0.8); // saved half a second after the change
        var saved = DataManager.LoadSettings().Tables.GetValueOrDefault("uitest-table");
        UiTestExpect(saved != null && saved.SortColumn == "name" && !saved.SortDescending && saved.HiddenColumns.SequenceEqual(new[] { "note" }), "the table's sort and hidden columns weren't saved");
        table.ToggleColumn(2);
        UiTestExpect(table.ShownColumns.Count == 3, "showing the column again didn't bring it back");
        // The rows export as shown: TSV, Markdown, Discord parts, and a CSV file in Atlas's folder.
        string tsv = table.ExportText(AP_Atlas.Core.ExportFormat.Tsv);
        UiTestExpect(tsv.StartsWith("Name\tCount\tNote\nSlot 1\t7\tband camp\n", StringComparison.Ordinal), $"the TSV export isn't the shown rows in order: {tsv.Replace("\n", "|")}");
        UiTestExpect(table.ExportText(AP_Atlas.Core.ExportFormat.Markdown).StartsWith("| Name | Count | Note |\n|---|---|---|\n", StringComparison.Ordinal), "the Markdown export has no header");
        UiTestExpect(table.DiscordParts().Count == 1 && table.DiscordParts()[0].StartsWith("```\n", StringComparison.Ordinal), "the Discord export isn't one code block");
        string csvPath = System.IO.Path.Combine(DataManager.GetDataDirectory(), "uitest-table.csv");
        string? csvProblem = table.SaveCsv(csvPath);
        UiTestExpect(csvProblem == null && (await System.IO.File.ReadAllTextAsync(csvPath)).StartsWith("Name,Count,Note\r\nSlot 1,7,band camp\r\n", StringComparison.Ordinal), "the CSV file wasn't written as shown");
        System.IO.File.Delete(csvPath);
        // Unchanged rows are updated in place (the same tree items), the selection kept; new rows rebuild it, the selection still kept.
        table.Select("b");
        var first = table.Tree.GetRoot()!.GetFirstChild();
        rows[1].Cells[2] = new AP_Atlas.UI.AtlasTable.Cell("apple pie");
        table.SetRows(rows);
        UiTestExpect(ReferenceEquals(first, table.Tree.GetRoot()!.GetFirstChild()) && table.Selected?.Key == "b" && table.Tree.GetSelected() != null && table.RowAt(table.Tree.GetSelected())?.Key == "b"
            && table.Tree.GetRoot()!.GetFirstChild()!.GetNext()!.GetText(2) == "apple pie", "unchanged rows weren't updated in place with the selection kept");
        rows.Add(Row("e", "Slot 4", 3, "date"));
        table.SetRows(rows);
        UiTestExpect(table.ShownRows.Count == 5 && table.Selected?.Key == "b" && table.RowAt(table.Tree.GetSelected())?.Key == "b", "a new row lost the selection");
        // The most rows drawn is a limit the count says.
        table.MaxRows = 2;
        table.Render();
        UiTestExpect(table.ShownRows.Count == 2 && table.CountLabel.Text.Contains("showing the first 2"), $"the row limit isn't applied or said: {table.CountLabel.Text}");
        table.QueueFree();
        await UiTestWaitAsync(0.1);
    }

    private async Task ConfirmationsAndEmptyStatesAsync()
    {
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        profile.Slots.Add("Other");
        _profiles.Add(profile);
        try
        {
            // Without the engine, the logic views say so (the same words), and nothing claims go mode.
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            await UiTestWaitForAsync(() => slot.EngineProblem != null ? slot : null, "the engine's problem");
            var textTree = slot.ProgressionTracker.TextTree;
            string? keyItems = await UiTestWaitForAsync(() => textTree.GetRoot()?.GetFirstChild()?.GetText(0) is { Length: > 0 } text ? text : null, "the Key Items empty state");
            UiTestExpect(keyItems.StartsWith("Logic needs the Atlas Engine", StringComparison.Ordinal), $"Key Items says \"{keyItems}\" without the engine");
            UiTestExpect(slot.LogicNoticeShown?.StartsWith("Logic needs the Atlas Engine", StringComparison.Ordinal) == true, $"the Logic Tracker says \"{slot.LogicNoticeShown}\" without the engine");
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.LogicTracker);
            await UiTestWaitAsync(0.2);
            UiTestExpect(!slot.GoModeShown && slot.GoalInLogic == null, "go mode is claimed while logic is unknown");

            // Deleting a slot asks first; cancelling keeps it, confirming removes it.
            SelectProfile(profile);
            await UiTestWaitAsync(0.1);
            Button DeleteButtonOf(string name) => _slotsListVBox!.FindChildren("*", nameof(Button), true, false).OfType<Button>()
                .Where(b => b.TooltipText == "Delete Slot").ElementAt(profile.Slots.IndexOf(name));
            ConfirmationDialog? Dialog(string title) => GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == title);
            DeleteButtonOf("Other").EmitSignal(BaseButton.SignalName.Pressed);
            var ask = await UiTestWaitForAsync(() => Dialog("Delete slot"), "the slot delete confirmation");
            ask.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            UiTestExpect(profile.Slots.Contains("Other") && Dialog("Delete slot") == null, "cancelling the slot delete didn't keep the slot");
            DeleteButtonOf("Other").EmitSignal(BaseButton.SignalName.Pressed);
            ask = await UiTestWaitForAsync(() => Dialog("Delete slot"), "the slot delete confirmation again");
            ask.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(!profile.Slots.Contains("Other") && profile.Slots.Contains("Tester"), "confirming the slot delete didn't remove that slot alone");
            // Deleting the multiworld asks first; cancelling keeps it.
            _deleteButton!.EmitSignal(BaseButton.SignalName.Pressed);
            var askProfile = await UiTestWaitForAsync(() => Dialog("Delete multiworld"), "the multiworld delete confirmation");
            askProfile.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            UiTestExpect(_profiles.Contains(profile) && SlotView(profile.Id, "Tester") != null, "cancelling the multiworld delete didn't keep it");
        }
        finally
        {
            DeleteProfile(profile);
        }
        await UiTestWaitAsync(0.2);
    }

    private async Task WindowPartsAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var bottomPane = _bottomPane ?? throw new InvalidOperationException("The bottom pane wasn't built.");
        // The View menu's check marks follow the parts as the menu opens.
        var (viewMenu, viewItems) = _commandItems.First(menu => menu.Value.ContainsValue("view.slots-panel"));
        bool Checked(string command)
        {
            viewMenu.EmitSignal(Window.SignalName.AboutToPopup);
            return viewMenu.IsItemChecked(viewMenu.GetItemIndex((int)viewItems.First(item => item.Value == command).Key));
        }
        var parts = new (string Command, Control Node, Func<AppSettings, bool> Shown)[]
        {
            ("view.slots-panel", _sidebar, s => s.ShowSlotsPanel),
            ("view.properties-panel", _propertiesSidebar, s => s.ShowPropertiesPanel),
            ("view.bottom-pane", bottomPane, s => s.ShowBottomPane),
            ("view.status-bar", _globalStatusBar, s => s.ShowStatusBar),
        };
        foreach (var (command, node, shown) in parts)
        {
            UiTestExpect(node.Visible && shown(_appSettings) && Checked(command), $"{command}: the part isn't shown and checked to begin with");
            _commands!.Run(command);
            UiTestExpect(!node.Visible && !shown(_appSettings) && !Checked(command), $"{command} didn't hide the part, or didn't uncheck it");
            UiTestExpect(!shown(DataManager.LoadSettings()), $"{command}: the hidden part isn't remembered in the settings file");
            _commands.Run(command);
            UiTestExpect(node.Visible && shown(_appSettings) && Checked(command) && shown(DataManager.LoadSettings()), $"{command} didn't show the part again");
        }
        // The explorer: hidden by the user, it stays hidden whatever tool shows, until shown again.
        host.ShowTool(AP_Atlas.UI.Tool.MapPacks);
        await UiTestWaitAsync(0.05);
        UiTestExpect(_midLeftSidebar.Visible && Checked("view.explorer"), "Map Packs' explorer isn't shown to begin with");
        _commands!.Run("view.explorer");
        UiTestExpect(!_midLeftSidebar.Visible && !_appSettings.ShowExplorer && !Checked("view.explorer") && !DataManager.LoadSettings().ShowExplorer, "the explorer wasn't hidden, or not remembered");
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        await UiTestWaitAsync(0.05);
        UiTestExpect(!_midLeftSidebar.Visible, "the explorer came back with another tool while the user had hidden it");
        _commands.Run("view.explorer");
        UiTestExpect(_midLeftSidebar.Visible && _appSettings.ShowExplorer && Checked("view.explorer"), "the explorer wasn't shown again");
        // Focus mode: the content and its header alone, with the menu bar; off again, each part as the user had it.
        _commands.Run("view.properties-panel"); // hidden by the user before focus mode
        await PressAsync("F9");
        UiTestExpect(!_sidebar.Visible && !_midLeftSidebar.Visible && !_propertiesSidebar.Visible && !bottomPane.Visible && !_globalStatusBar.Visible && !_activityBar.Visible
            && _toolTitle.Visible && _menuHbox.Visible && _contentStage.Visible, "focus mode doesn't leave the content alone");
        UiTestExpect(_appSettings.ShowSlotsPanel && !_appSettings.ShowPropertiesPanel && Checked("view.slots-panel"), "focus mode changed the user's settings");
        await PressAsync("F9");
        UiTestExpect(_sidebar.Visible && _midLeftSidebar.Visible && !_propertiesSidebar.Visible && bottomPane.Visible && _globalStatusBar.Visible && _activityBar.Visible,
            "leaving focus mode didn't bring each part back as the user had it");
        _commands.Run("view.properties-panel");
        UiTestExpect(_propertiesSidebar.Visible && _appSettings.ShowPropertiesPanel, "Properties didn't come back");
    }

    private async Task SlotPickerAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        await using var server = new FakeArchipelagoServer();
        server.Slots.Clear();
        server.Slots.AddRange(new[] { "Alice", "Bob" });
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.AddRange(new[] { "Alice", "Bob" });
        _profiles.Add(profile);
        try
        {
            await OnConnectSlotPressedAsync("Alice", profile);
            var alice = await UiTestWaitForAsync(() => SlotView(profile.Id, "Alice"), "Alice's view");
            await OnConnectSlotPressedAsync("Bob", profile);
            var bob = await UiTestWaitForAsync(() => SlotView(profile.Id, "Bob"), "Bob's view");
            host.ShowTool(AP_Atlas.UI.Tool.MapTracker);
            await UiTestWaitAsync(0.05);
            var picker = _slotPicker!;
            string Listed() => string.Join(", ", Enumerable.Range(0, picker.ItemCount).Select(i => picker.GetItemText(i)));
            UiTestExpect(picker.Visible && picker.ItemCount == 2 && picker.GetItemText(0).StartsWith("Alice", StringComparison.Ordinal) && picker.GetItemText(1).StartsWith("Bob", StringComparison.Ordinal),
                $"the picker shows {picker.ItemCount} slots ({Listed()})");
            UiTestExpect(_currentSelectedSlot == bob && picker.Selected == 1, "the picker doesn't show the slot that connected last as the one picked");

            // Picking a slot shows its view, as its card would.
            picker.Select(0);
            picker.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
            await UiTestWaitAsync(0.05);
            UiTestExpect(_currentSelectedSlot == alice && ShownContent() == alice.MapTracker, "picking Alice didn't show her map");

            // Ctrl+Tab and Ctrl+Shift+Tab go through the slots, around the end.
            await PressAsync("Ctrl+Tab");
            UiTestExpect(_currentSelectedSlot == bob && picker.Selected == 1, "Ctrl+Tab didn't select the next slot");
            await PressAsync("Ctrl+Tab");
            UiTestExpect(_currentSelectedSlot == alice, "Ctrl+Tab didn't go around to the first slot");
            await PressAsync("Ctrl+Shift+Tab");
            UiTestExpect(_currentSelectedSlot == bob, "Ctrl+Shift+Tab didn't select the previous slot");

            // A slot selected elsewhere shows as picked; a tool that isn't per slot hides the picker; a slot that ends leaves it.
            host.SelectSlot(alice);
            await UiTestWaitAsync(0.05);
            UiTestExpect(picker.Selected == 0, "the picker doesn't follow a slot selected elsewhere");
            host.ShowTool(AP_Atlas.UI.Tool.MapPacks);
            await UiTestWaitAsync(0.05);
            UiTestExpect(!picker.Visible, "the picker shows on a tool that isn't per slot");
            host.ShowTool(AP_Atlas.UI.Tool.Hints);
            // A slot the user disconnects keeps its card (its Connect button connects it again), so it stays pickable too.
            host.DisconnectSlot(profile.Id, "Bob");
            await UiTestWaitForAsync(() => !_sessions.IsLoggedIn(new SlotId(profile.Id, "Bob")) ? this : null, "Bob to disconnect");
            await UiTestWaitAsync(0.2);
            UiTestExpect(picker.Visible && picker.ItemCount == 2 && picker.Selected == 0, $"after Bob disconnected the picker shows {picker.ItemCount} slots ({Listed()}), picked {picker.Selected}");
            // Deleting the multiworld ends its slots: nothing is left to pick.
            DeleteProfile(profile);
            await UiTestWaitForAsync(() => !IsInstanceValid(alice) || alice.Ended ? this : null, "Alice to end");
            await UiTestWaitAsync(0.2);
            UiTestExpect(!picker.Visible && picker.ItemCount == 0, $"after the multiworld was deleted the picker still shows {picker.ItemCount} slots ({Listed()})");
        }
        finally
        {
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
        }
    }

    private async Task ActivityBarAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var order = string.Join(",", _activityBar.Order.Select(t => t.Title));
        UiTestExpect(order == "Map Tracker,Key Items,Logic Tracker,Item History,Hints,Cheese Tracker,Sphere Tracker,Home,Multiworlds,Map Packs,Settings", $"the activity bar's order is {order}");
        UiTestExpect(_activityBar.CaptionOf(AP_Atlas.UI.Tool.MapTracker) == "SLOT" && _activityBar.CaptionOf(AP_Atlas.UI.Tool.CheeseTracker) == "MULTIWORLD" && _activityBar.CaptionOf(AP_Atlas.UI.Tool.MapPacks) == "ATLAS",
            "the groups aren't captioned as designed");
        var noIcon = AP_Atlas.UI.Tool.All.Where(t => !AP_Atlas.UI.LucideIcons.Names.Contains(t.Icon) || _activityBar.ButtonOf(t).Icon == null).Select(t => t.Title).ToList();
        UiTestExpect(noIcon.Count == 0, $"tools without a shipped icon: {string.Join(", ", noIcon)}");
        UiTestExpect(_activityBar.ButtonOf(AP_Atlas.UI.Tool.Hints).TooltipText.Contains("Ctrl+5"), $"the Hints button's tooltip is \"{_activityBar.ButtonOf(AP_Atlas.UI.Tool.Hints).TooltipText}\"");

        // Pressing a button shows the tool; switching a tool any other way lights its button and names it in the header.
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        _activityBar.ButtonOf(AP_Atlas.UI.Tool.MapPacks).EmitSignal(BaseButton.SignalName.Pressed);
        await UiTestWaitAsync(0.05);
        UiTestExpect(ShownContent() == _packManagerPanel && _activityBar.Selected == AP_Atlas.UI.Tool.MapPacks, "pressing the Map Packs button didn't show Map Packs");
        await PressAsync("Ctrl+7");
        UiTestExpect(ShownContent() == _sphereTab && _activityBar.Selected == AP_Atlas.UI.Tool.SphereTracker
            && _activityBar.ButtonOf(AP_Atlas.UI.Tool.SphereTracker).ButtonPressed && !_activityBar.ButtonOf(AP_Atlas.UI.Tool.MapPacks).ButtonPressed,
            "Ctrl+7 didn't light the Sphere Tracker's button alone");
        UiTestExpect(_toolTitle.Text == "Sphere Tracker", $"the tool header says \"{_toolTitle.Text}\"");

        // The engine button opens the engine window.
        _activityBar.EngineButton.EmitSignal(BaseButton.SignalName.Pressed);
        var engine = await UiTestWaitForAsync(() => GetTree().Root.GetChildren().OfType<AP_Atlas.UI.AtlasEngineWindow>().FirstOrDefault(), "the Atlas Engine window");
        await UiTestWaitAsync(0.3);
        engine.EmitSignal(Window.SignalName.CloseRequested);
        await UiTestWaitAsync(0.3);
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
    }

    private async Task CommandPaletteAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        await PressAsync("Ctrl+Shift+P");
        var palette = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.CommandPalette>().FirstOrDefault(p => p.Visible), "the command palette");
        var input = palette.FindChildren("*", nameof(LineEdit), true, false).OfType<LineEdit>().First();
        var tree = palette.FindChildren("*", nameof(Tree), true, false).OfType<Tree>().First();
        UiTestExpect(input.HasFocus(), "the palette's typing box isn't focused");
        int listed = Rows(tree).Count, expected = _commands!.All.Count(c => c.Enabled()) - 1; // the Debug Log waits for developer mode
        UiTestExpect(listed == expected, $"the palette lists {listed} commands, not every command that can run but itself ({expected})");
        UiTestExpect(Rows(tree).Any(r => r[0] == "Map Tracker" && r[1] == "Ctrl+1"), "the palette doesn't show a command's key");

        void Type(string text)
        {
            input.Text = text;
            input.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        Type("map p");
        await UiTestWaitAsync(0.05);
        var rows = Rows(tree);
        UiTestExpect(rows.Count == 1 && rows[0][0] == "Map Packs", $"typing \"map p\" lists {string.Join("; ", rows.Select(r => r[0]))}");
        Type("zzz");
        await UiTestWaitAsync(0.05);
        UiTestExpect(Rows(tree).Count == 0 && palette.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(l => l.Visible && l.Text == "No command matches."),
            "a word nothing matches doesn't say so");
        Type("map p");
        await UiTestWaitAsync(0.05);
        // Enter runs the pick and closes the palette, which frees itself.
        input.EmitSignal(Control.SignalName.GuiInput, AP_Atlas.UI.CommandKeys.ToEvent("Enter")!);
        await UiTestWaitForAsync(() => ShownContent() == _packManagerPanel ? this : null, "Map Packs to show after Enter in the palette");
        await UiTestWaitForAsync(() => !IsInstanceValid(palette) ? this : null, "the palette to close and free itself");

        // Escape closes it and runs nothing.
        await PressAsync("Ctrl+Shift+P");
        var again = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.CommandPalette>().FirstOrDefault(p => p.Visible), "the command palette again");
        again.FindChildren("*", nameof(LineEdit), true, false).OfType<LineEdit>().First().EmitSignal(Control.SignalName.GuiInput, AP_Atlas.UI.CommandKeys.ToEvent("Escape")!);
        await UiTestWaitForAsync(() => !IsInstanceValid(again) ? this : null, "the palette to close on Escape");
        UiTestExpect(ShownContent() == _packManagerPanel, "Escape ran a command");
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
    }

    /// <summary>Presses a key as the user would ("Ctrl+2"), through the window's input.</summary>
    private async Task PressAsync(string key)
    {
        var press = AP_Atlas.UI.CommandKeys.ToEvent(key) ?? throw new ArgumentException($"\"{key}\" isn't a key", nameof(key));
        GetViewport().PushInput(press);
        await UiTestWaitAsync(0.1);
    }

    private static string ShortcutShown(PopupMenu popup, int index) =>
        popup.GetItemShortcut(index)?.Events.Select(v => v.AsGodotObject() as InputEventKey).FirstOrDefault(e => e != null)?.AsText() ?? "";

    private static List<string[]> Rows(Tree tree)
    {
        var rows = new List<string[]>();
        for (var item = tree.GetRoot()?.GetFirstChild(); item != null; item = item.GetNext())
            rows.Add(Enumerable.Range(0, 3).Select(column => column < tree.Columns ? item.GetText(column) : "").ToArray());
        return rows;
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
            await UiTestWaitForAsync(() => slot.MapTracker.FindChildren("*", "TextureRect", true, false).OfType<TextureRect>().FirstOrDefault(s => s.Texture != null), "the map's background on the Map Tracker");

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
        FakeMapPack.Write(zip, "UI test pack", "Test Game", mapWidth: 1600, mapHeight: 1000); // bigger than the view, so there's room to drag
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
            await UiTestWaitForAsync(() => slot.MapTracker.FindChildren("*", "TextureRect", true, false).OfType<TextureRect>().FirstOrDefault(s => s.Texture != null), "the map on the Map Tracker");
            // The map is ordinary controls in a scrolling area: nothing of it runs every frame (a camera would keep Godot
            // redrawing the window all the time), yet the wheel zooms it around the cursor and a drag moves it.
            var canvas = slot.MapTracker.Canvas;
            UiTestExpect(slot.MapTracker.FindChildren("*", "Camera2D", true, false).Count == 0 && !canvas.IsProcessing() && !canvas.Surface.IsProcessing(),
                "the map runs every frame, so Atlas never idles while a slot is connected");
            await UiTestWaitAsync(0.3); // the view settles in its place
            // Fitted: the whole map is in view, and the view is remembered for the map.
            canvas.FitToView();
            UiTestExpect(canvas.MapSize.X * canvas.Zoom <= canvas.ViewSize.X + 0.5f && canvas.MapSize.Y * canvas.Zoom <= canvas.ViewSize.Y + 0.5f, $"after Fit the map ({canvas.MapSize * canvas.Zoom}) doesn't fit the view ({canvas.ViewSize})");
            UiTestExpect(_appSettings.MapCameras.TryGetValue(slot.MapTracker.CurrentMapId, out var savedView) && Mathf.IsEqualApprox(savedView.Zoom, canvas.Zoom), "the map's view isn't remembered");
            // The wheel zooms by a step each notch, until the map is bigger than the view.
            float zoomBefore = canvas.Zoom;
            int notches = 0;
            while (canvas.MapSize.X * canvas.Zoom < canvas.ViewSize.X + 200 && notches < 12) // room to drag 30 px from anywhere
            {
                canvas.Surface.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Position = new Vector2(100, 100) });
                notches++;
                await UiTestWaitAsync(0.05); // the scroll follows a frame later
            }
            UiTestExpect(notches > 0 && Mathf.IsEqualApprox(canvas.Zoom, zoomBefore * Mathf.Pow(AP_Atlas.UI.MapCanvas.WheelStep, notches), 0.001f), $"zooming in {notches} notches took the zoom from {zoomBefore} to {canvas.Zoom}");
            // Pins sit where the zoom puts them.
            var pin = canvas.Pins.First();
            UiTestExpect(pin.Control.Position.DistanceTo(new Vector2(pin.X * canvas.Zoom, pin.Y * canvas.Zoom) - pin.Control.Size / 2) < 0.5f, $"a pin at ({pin.X}, {pin.Y}) sits at {pin.Control.Position} at zoom {canvas.Zoom}");
            // A drag moves the map by as much as the mouse moved.
            await UiTestWaitAsync(0.1);
            var scrollBefore = canvas.ScrollPosition;
            canvas.Surface.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = new Vector2(100, 100), GlobalPosition = new Vector2(100, 100) });
            canvas.Surface.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { Position = new Vector2(70, 100), GlobalPosition = new Vector2(70, 100) });
            canvas.Surface.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = new Vector2(70, 100), GlobalPosition = new Vector2(70, 100) });
            UiTestExpect(Mathf.IsEqualApprox(canvas.ScrollPosition.X, scrollBefore.X + 30f), $"dragging 30 px: the map scrolled from {scrollBefore.X} to {canvas.ScrollPosition.X}");
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
            await ExpectGoModeAsync(slot, false, "with nothing received");
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
            await ExpectGoModeAsync(slot, true, "with the Sword and the Shield");
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
    /// <summary>Go mode reaches the user: the Logic Tracker's label (while it shows) and the slot card's footer say it exactly when the goal is in logic.</summary>
    private async Task ExpectGoModeAsync(SlotTrackerControl slot, bool goal, string when)
    {
        await UiTestWaitForAsync(() => slot.GoModeShown == goal ? slot : null, $"the Logic Tracker's go mode label {when} (expected {(goal ? "shown" : "hidden")})");
        await UiTestWaitForAsync(() => _activeSessionsList.FindChildren("StatusFooter", nameof(Label), true, false).OfType<Label>().FirstOrDefault(l => l.Text.Contains("Go mode") == goal),
            $"the slot card's footer {when} (expected go mode {(goal ? "said" : "not said")})");
    }

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
    private async Task UpdatesAsync()
    {
        string dataDir = DataManager.GetDataDirectory();
        string install = System.IO.Path.Combine(dataDir, "fake-install");
        static void WriteTestFile(string path, string content)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
        }
        static string ReadTestFile(string dir, string relative) => System.IO.File.ReadAllText(System.IO.Path.Combine(dir, relative));
        Control? CardWith(string text) => _alerts.Cards.FirstOrDefault(card => card.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(label => label.Text.Contains(text)));
        WriteTestFile(System.IO.Path.Combine(install, AP_Atlas.Core.Updates.UpdateLayout.Exe), "old exe");
        WriteTestFile(System.IO.Path.Combine(install, AP_Atlas.Core.Updates.UpdateLayout.Pck), "old pck");
        WriteTestFile(System.IO.Path.Combine(install, AP_Atlas.Core.Updates.UpdateLayout.DataFolder, AP_Atlas.Core.Updates.UpdateLayout.MainAssembly), "old dll");
        WriteTestFile(System.IO.Path.Combine(install, "README.md"), "old readme");
        WriteTestFile(System.IO.Path.Combine(install, AP_Atlas.Core.Updates.UpdateLayout.PortableData, "settings.json"), "{}");

        // A release one patch newer, as GitHub lists it, from a site on this PC that also serves the zip and its checksum file.
        var current = _updates!.Current;
        var next = new AP_Atlas.Core.Updates.SemVer(current.Major, current.Minor, current.Patch + 1);
        string zipName = AP_Atlas.Core.Updates.ReleaseInfo.ZipPrefix + next + AP_Atlas.Core.Updates.ReleaseInfo.ZipSuffix;
        static byte[] FakeReleaseZip()
        {
            using var zipStream = new System.IO.MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in new[]
                {
                    (AP_Atlas.Core.Updates.UpdateLayout.ProgramFolder + "/" + AP_Atlas.Core.Updates.UpdateLayout.Exe, "new exe"),
                    (AP_Atlas.Core.Updates.UpdateLayout.ProgramFolder + "/" + AP_Atlas.Core.Updates.UpdateLayout.Pck, "new pck"),
                    (AP_Atlas.Core.Updates.UpdateLayout.ProgramFolder + "/" + AP_Atlas.Core.Updates.UpdateLayout.DataFolder + "/" + AP_Atlas.Core.Updates.UpdateLayout.MainAssembly, "new dll"),
                    (AP_Atlas.Core.Updates.UpdateLayout.ProgramFolder + "/README.md", "new readme")
                })
                {
                    using var writer = new System.IO.StreamWriter(archive.CreateEntry(name).Open(), System.Text.Encoding.UTF8);
                    writer.Write(content);
                }
            }
            return zipStream.ToArray();
        }
        byte[] zip = FakeReleaseZip();
        string zipHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)).ToLowerInvariant();
        string goodSums = $"{zipHash}  {zipName}\n", badSums = $"{new string('0', 64)}  {zipName}\n";
        string sums = goodSums;
        await using var site = new AP_Atlas.Core.Testing.FakeWebSite();
        string releasesJson = new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JObject
        {
            ["tag_name"] = "v" + next,
            ["prerelease"] = false,
            ["draft"] = false,
            ["published_at"] = "2026-10-07T12:00:00Z",
            ["body"] = "## Notes\n\n- Something changed for the better",
            ["assets"] = new Newtonsoft.Json.Linq.JArray(
                new Newtonsoft.Json.Linq.JObject { ["name"] = zipName, ["browser_download_url"] = site.Site + "/dl/" + zipName, ["size"] = zip.Length },
                new Newtonsoft.Json.Linq.JObject { ["name"] = AP_Atlas.Core.Updates.ReleaseInfo.SumsName, ["browser_download_url"] = site.Site + "/dl/" + AP_Atlas.Core.Updates.ReleaseInfo.SumsName, ["size"] = 100 })
        }).ToString();
        site.Answer = (path, headers) =>
        {
            if (path.StartsWith("/repos/", StringComparison.Ordinal))
            {
                var etag = new Dictionary<string, string> { ["ETag"] = "\"r1\"" };
                if (headers.TryGetValue("if-none-match", out var tag) && tag == "\"r1\"") return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(304, Array.Empty<byte>(), "application/json", etag);
                return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, System.Text.Encoding.UTF8.GetBytes(releasesJson), "application/json", etag);
            }
            if (path == "/dl/" + AP_Atlas.Core.Updates.ReleaseInfo.SumsName) return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, System.Text.Encoding.UTF8.GetBytes(sums), "text/plain");
            if (path == "/dl/" + zipName) return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, zip, "application/zip");
            return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(404, Array.Empty<byte>());
        };
        AP_Atlas.Core.GitHubApi.TestSite = site.Site;
        AP_Atlas.Core.GitHubApi.ResetForTests();
        TestInstallDir = install;
        SetUpUpdates();
        var updates = _updates!;
        try
        {
            // Nothing without the permission and the daily setting.
            UiTestExpect(await updates.CheckAsync(manual: false) == null && site.Requests.Count == 0, "a check ran without the permission");
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.UpdateChecks, null, true);
            _appSettings.UpdateCheckDaily = true;
            _appSettings.UpdateLastCheckUtc = null;
            var outcome = await updates.CheckAsync(manual: false);
            UiTestExpect(outcome?.Newer?.Version == next && site.Requests.Count == 1, $"the daily check didn't find {next} (the site saw {site.Requests.Count} requests)");
            var offer = await UiTestWaitForAsync(() => CardWith(next + " is available"), "the card offering the newer version");
            // Not due again today; by hand, the unchanged list is confirmed (304) rather than read again.
            UiTestExpect(await updates.CheckAsync(manual: false) == null && site.Requests.Count == 1, "a second daily check ran before a day passed");
            outcome = await updates.CheckAsync(manual: true);
            UiTestExpect(outcome?.Newer?.Version == next && site.Requests.Count == 2, "the manual check didn't confirm the list and find the version again");
            // The card's action shows the release's notes.
            _alerts.ActionButtonOf(offer)!.EmitSignal(BaseButton.SignalName.Pressed);
            var dialog = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.UpdateDialog>().FirstOrDefault(), "the update dialog");
            UiTestExpect(dialog.ShownText.Contains("Something changed") && dialog.ShownText.Contains(next.ToString()), "the dialog doesn't show the release's notes");
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            // A download whose hash doesn't match the release's checksum file is refused, and nothing is staged.
            var release = outcome!.Newer!;
            sums = badSums;
            UiTestExpect(!await updates.DownloadAndStageAsync(release) && updates.StagedVersion == null && !System.IO.Directory.Exists(updates.NextDir), "a download with the wrong hash was kept");
            UiTestExpect(CardWith("couldn't be downloaded") != null, "the refused download wasn't said");
            sums = goodSums;
            UiTestExpect(await updates.DownloadAndStageAsync(release) && updates.StagedVersion == next.ToString(), "a good download wasn't staged");
            var ready = await UiTestWaitForAsync(() => CardWith("ready"), "the ready card");
            UiTestExpect(_alerts.ActionButtonOf(ready)?.Text == "Restart to update", $"the ready card's action is \"{_alerts.ActionButtonOf(ready)?.Text}\"");
            // The swap (what Restart to update does once everything is closed), on the fake install folder.
            string staged = System.IO.Path.Combine(updates.NextDir, AP_Atlas.Core.Updates.UpdateLayout.ProgramFolder);
            AP_Atlas.Core.Updates.UpdateInstaller.Swap(install, staged, updates.PreviousDir, current.ToString(), _ => { });
            UiTestExpect(ReadTestFile(install, AP_Atlas.Core.Updates.UpdateLayout.Exe) == "new exe" && ReadTestFile(install, "README.md") == "new readme"
                && ReadTestFile(updates.PreviousDir, AP_Atlas.Core.Updates.UpdateLayout.Exe) == "old exe" && ReadTestFile(install, "PortableData/settings.json") == "{}"
                && AP_Atlas.Core.Updates.UpdateInstaller.VersionIn(updates.PreviousDir) == current.ToString(), "the swap didn't move the files as it should");
            // The new Atlas ends before its window: the supervisor puts the previous version back, and the Atlas put back says so.
            AP_Atlas.Core.Updates.UpdateInstaller.WriteMarker(updates.UpdatesDir, new AP_Atlas.Core.Updates.UpdateMarker { FromVersion = current.ToString(), ToVersion = next.ToString(), State = AP_Atlas.Core.Updates.UpdateMarker.Starting, StartedUtc = DateTime.UtcNow }); // wall clock: a saved time, as the updater writes it
            var watch = await AP_Atlas.Core.Updates.UpdateInstaller.WatchAsync(updates.UpdatesDir, () => true, TimeSpan.FromSeconds(5));
            UiTestExpect(watch == AP_Atlas.Core.Updates.UpdateInstaller.Watch.Exited, $"a new Atlas that ended was seen as {watch}");
            AP_Atlas.Core.Updates.UpdateInstaller.RollBack(install, updates.PreviousDir, updates.FailedDir, next.ToString(), _ => { });
            var note = AP_Atlas.Core.Updates.UpdateInstaller.ReadMarker(updates.UpdatesDir)!;
            note.State = AP_Atlas.Core.Updates.UpdateMarker.RolledBack;
            note.Reason = "it closed before its window appeared";
            AP_Atlas.Core.Updates.UpdateInstaller.WriteMarker(updates.UpdatesDir, note);
            UiTestExpect(ReadTestFile(install, AP_Atlas.Core.Updates.UpdateLayout.Exe) == "old exe" && ReadTestFile(updates.FailedDir, AP_Atlas.Core.Updates.UpdateLayout.Exe) == "new exe" && updates.PreviousVersion == null,
                "putting the previous version back didn't restore the files");
            updates.ConfirmStarted();
            UiTestExpect(CardWith("didn't start") != null && AP_Atlas.Core.Updates.UpdateInstaller.ReadMarker(updates.UpdatesDir) == null, "the update put back wasn't said, or its note stayed");
            // The new Atlas starts: it confirms the note, and the watch sees it.
            AP_Atlas.Core.Updates.UpdateInstaller.WriteMarker(updates.UpdatesDir, new AP_Atlas.Core.Updates.UpdateMarker { FromVersion = "0.0.1", ToVersion = current.ToString(), State = AP_Atlas.Core.Updates.UpdateMarker.Starting, StartedUtc = DateTime.UtcNow }); // wall clock: a saved time, as the updater writes it
            updates.ConfirmStarted();
            UiTestExpect(AP_Atlas.Core.Updates.UpdateInstaller.ReadMarker(updates.UpdatesDir)?.State == AP_Atlas.Core.Updates.UpdateMarker.Ready && CardWith("updated to") != null, "the started update wasn't confirmed");
            watch = await AP_Atlas.Core.Updates.UpdateInstaller.WatchAsync(updates.UpdatesDir, () => false, TimeSpan.FromSeconds(5));
            UiTestExpect(watch == AP_Atlas.Core.Updates.UpdateInstaller.Watch.Started, $"a confirmed note was seen as {watch}");
            AP_Atlas.Core.Updates.UpdateInstaller.DeleteMarker(updates.UpdatesDir);
            // A folder Atlas can't write to is said, with the way to update by hand.
            string notAFolder = System.IO.Path.Combine(dataDir, "not-a-folder.txt");
            WriteTestFile(notAFolder, "x");
            TestInstallDir = notAFolder;
            SetUpUpdates();
            UiTestExpect(!await _updates!.DownloadAndStageAsync(release) && CardWith("isn't writable") != null, "an unwritable folder wasn't said");
        }
        finally
        {
            AP_Atlas.Core.GitHubApi.TestSite = null;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.UpdateChecks, null, false);
            _appSettings.UpdateCheckDaily = false;
            _appSettings.UpdateLastCheckUtc = null;
            TestInstallDir = null;
            SetUpUpdates();
        }
    }

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
