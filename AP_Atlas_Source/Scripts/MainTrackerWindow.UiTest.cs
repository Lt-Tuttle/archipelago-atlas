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
        _appSettings.FixTipDismissed = true; // the map's one tip stays off the scenarios' maps; its own scenario shows it
        // A window a desktop user would have (1600x1000): every pane fits at 100%, so the scenarios see Atlas whole.
        GetTree().Root.Size = new Vector2I(1600, 1000);
        await UiTestWaitAsync(0.3);
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
        await ScenarioAsync("Closed windows are freed: the Pack Doctor, the race mode dialog and the setup window, opened and closed again and again, leave nothing in memory and no node behind",
            ClosedWindowsAreFreedAsync);
        await ScenarioAsync("Moving views: the Cheese and Sphere tabs and Properties keep following their events when moved to another parent (docking, pop-outs), and stop while out of the window",
            ViewsKeepTheirEventsWhenMovedAsync);
        await ScenarioAsync("Tools: every tool's tab shows its own view, and each slot tool the connected slot's view (or asks for a slot when none is connected)",
            EveryToolShowsItsViewAsync);
        await ScenarioAsync("Menu bar and keys: the six menus hold every command with its key shown; Ctrl+1 to 9 switch tools, a rebind in the settings takes over at once, F1 lists the keys as they are, and About names the version",
            MenuBarAndKeysAsync);
        await ScenarioAsync("Command palette: Ctrl+Shift+P opens it ready to type, it lists every command with its key, a typed word narrows it to the commands whose words start that way, Enter runs the pick and closes it, Escape only closes it",
            CommandPaletteAsync);
        await ScenarioAsync("Activity bar: Home alone at the top, then ATLAS TOOLS, EXTERNAL TOOLS and, at the bottom, ATLAS CONFIG, each caption on two lines on an accent band; every tool in its group, in order, with a shipped icon, its short name under it, its full title for screen readers and its key in the tooltip; the group captions sit on accent bands; pressing a button shows the tool, switching a tool any other way lights its button and names it in the header; the Engine button, the last, shows the Atlas Engine page",
            ActivityBarAsync);
        await ScenarioAsync("Dialogs: a confirmation (with and without a requirement), a prompt and Save changes stand as tall as their content, under four fifths of the window, with their buttons and content inside (a wrapped line measured before its width used to make every dialog as tall as the screen); a short question keeps its details under More information and grows to them; one the user resized keeps that size when its content changes or it's as tall as the window (it snapped back to its first size); a dialog's frame stands out in the accent with a shadow; a question asked from another window opens over that window, inside Atlas's",
            DialogsStandAsTallAsTheirContentAsync);
        await ScenarioAsync("Slot picker: the tool header lists the connected slots with the selected one chosen; picking one shows its view, Ctrl+Tab and Ctrl+Shift+Tab go through them around the end, a slot selected elsewhere shows as picked, a tool that isn't per slot hides it, and a slot that ends leaves it",
            SlotPickerAsync);
        await ScenarioAsync("Window parts: the View menu hides and shows the slots panel, the explorer (whatever tool shows), Properties, the bottom pane and the status bar, remembering each; each tool's explorer has its own width (Games starts at 360, the Map Tracker at 340, Multiworlds at 300); focus mode leaves the content alone and, off again, brings each part back as the user had it",
            WindowPartsAsync);
        await ScenarioAsync("Settings page: Ctrl+, shows it with the search box ready and the sections in the explorer; each kind of row changes its setting at once and saves it (a toggle, a choice, a number, a window part, a bottom pane tab); a setting changed elsewhere shows as it is; typed words narrow the rows; a section jump scrolls",
            SettingsPageAsync);
        await ScenarioAsync("Keyboard shortcuts: every command's key is a row of the Settings page; press the button, then a key, and the command runs on it at once, the menus and the bar show it and it's saved; a key two commands share is said on both rows; a reset brings the default back; Backspace means no key, Escape keeps it, a modifier alone isn't one, and losing the focus ends the wait; the F1 list leads here",
            KeyboardShortcutsAsync);
        await ScenarioAsync("Privacy & permissions: a section of the Settings page lists every permission Atlas can ask for with its state (asks each time, allowed until Atlas closes, always allowed, not until Atlas restarts) and every trusted apworld source; a kept answer and a trusted source can be taken back, which is saved; the section is found by its words",
            PrivacyAsync);
        await ScenarioAsync("Home: Ctrl+1 shows it on its own; every tool has a button with a line and every link is https; a first start shows the Set up Atlas block (its button opens the setup window on Home, Add a multiworld… the Multiworlds page) and the setup line counts nothing done; a tip shows and Next tip goes around; a multiworld is listed with its line and one click connects its slot (its game learnt from the room), its row says so and Open shows it on its page; New… makes a multiworld (its slots typed, no YAML) in a dialog on Home; Install games… sits beside New… and Paste a room link…, and beside Add a multiworld… on a first start, opening over Home; Paste a room link… readies the room link box; the columns fit the page; with every step settled the line says so",
            HomeAsync);
        await ScenarioAsync("Games page: the Games tool lists every game in its group (community games from the index, the games your multiworlds play as added by you), with the games of your own slots first (from their saved stats and their linked YAMLs, each once), typed words narrow the list, a game's page opens on one card saying where it stands (Atlas can't add it yet, the engine first without one, Install this game once its project is known, Doctor… once a pack arrives) with its map packs, files and Advanced (the versions) in closed folds and no YAML anywhere; the game's folders are inside Atlas's data folder; once GitHub may be asked, the page lists every version of every project (the game's own, and one of the same name found by one search), newest first with pre-releases marked and the newest full release named, and downloads nothing without a press; the game's page still has no YAML; the release-files dialog fits the window with a check box per file; a Discord home is named on its link",
            GamesPageAsync);
        await ScenarioAsync("Help: the guide opens on its first topic with a topic per section; a topic shows its section, What's new the changelog, Credits & disclaimer the author and the credits, Licences Atlas's licence; a second Help command uses the same window at its topic; Home's What's new card lists the newest changes and leads here",
            HelpAsync);
        await ScenarioAsync("Alerts: what Atlas tells the user stacks at the bottom right without overlapping, at most a few at once, every card in the history newest first with its kind; a card's button runs its action and the card goes, the × takes one away, the plain ones go after their hold; Window → Notifications lists the history, marks it seen, and Clear empties it",
            AlertsAsync);
        await ScenarioAsync("UI kit: a button from the kit runs its action once per press, honours enabled and keeps its tooltip; its text lines take the palette's colours; every heading from it wears the accent's heading colour and follows an accent change, wherever it is; the alert feed names a card's kind from the palette",
            UiKitAsync);
        await ScenarioAsync("Developer mode: off, the Debug Log tab, its command, the Debug Log's menu items, the hitch warnings on the status bar and the testing tools (Tools → Debug, the Games page's Debug parts, their key rows; the commands refused) are hidden, and a diagnostic line reaches the Debug Log and the file, never the System Log; on, each shows and Verify logic against a seed opens the Engine page's seed test; the status bar's right end counts the connected slots, and a hidden tab that was current gives way to Chat",
            DeveloperModeAsync);
        await ScenarioAsync("Accessibility: every button takes the keyboard focus (Tab reaches it, Enter presses it; a kit button can opt out beside a field), the theme draws a focus ring on every kind of control that takes the focus, Tab from the Settings search box moves on, a symbol-only kit button is named by its tooltip, and no button anywhere in the window shows only a symbol without a name for screen readers",
            AccessibilityAsync);
        await ScenarioAsync("Multiworlds page: the editor scrolls as one with each label beside its box; a room link fills in the server address and the slots from the room's status page (asked first; never the room's page) and is kept, and asks before replacing a typed address; Add YAML keeps the file in Atlas's YAML folder and adds a slot per player it names (a placeholder name asks for the slot's name), each tied to it, and a slot's details show its game, YAML and map following (saved per slot); unsaved edits ask Save / Don't save / Cancel before another multiworld is selected, one is added, the page is left or Atlas closes (Cancel keeps everything, Don't save puts the multiworld back, Save writes it to disk); the eye shows the password until another multiworld is selected; every box has an (i) that explains it and leads to the guide; the Sphere Tracker box keeps a refused link and says why, and links the host's room once the user confirms the host made it; Home shows no address for a multiworld without one; a slot's rename takes effect on Enter or on leaving the field, never per keystroke, and its saved stats follow it; after a reconnect gives up, one status read (at most every ten minutes) offers the room's new port, or says the room is asleep; a connection the server refuses shows as a card; Save brings the SLOTS list up to date",
            MultiworldsPageAsync);
        await ScenarioAsync("Add a slot: + Add Slot opens a dialog (no placeholder row is added): Connect now follows the server address; a duplicate name keeps it open and says so; typed words find the game and the engine line says whether its logic is ready (Set up the Atlas Engine without an engine); no YAML is asked for; Add keeps the slot with its game on disk and connects it",
            AddSlotDialogAsync);
        await ScenarioAsync("Updates: nothing is asked of GitHub without the permission and the daily setting; with them, one read a day finds a newer version of the channel (an unchanged list is confirmed, never sent again) and offers it as a card with its notes; a download whose hash doesn't match the release's SHA256SUMS is refused and nothing is staged, a good one is staged ready for a restart; the swap moves the installed files aside and the release's in (PortableData untouched), the supervisor puts the previous version back when the new Atlas ends before its window and the Atlas put back says so, a started one confirms the note; a folder Atlas can't write to is said so",
            UpdatesWithoutGodotErrorsAsync);
        await ScenarioAsync("Data folder: a program folder that can't be written to needs a choice (the override and a writable folder don't, and nothing outside Atlas's folder is touched then); the dialog names the folder and the problem, Continue takes the local app data folder by default and makes it, another folder that can't be used is refused with the reason and the dialog stays, a usable one is taken and remembered in the pointer file, and Quit chooses nothing",
            DataFolderAsync);
        await ScenarioAsync("Engine setup panel: the short setup asks the engine permission first and starts nothing until it's answered; the panel says the step it's on in plain words (never pip's lines) with one bar for the whole setup that never goes down, says why a run stopped with Try again and Show details, Try again runs it again, Show details shows the Atlas Engine page with the run's log, and Don't allow starts nothing",
            EngineSetupPanelAsync);
        await ScenarioAsync("Setup window: Resume opens at the first step left; the pages are Welcome, The Atlas Engine (run from source without one), Your multiworld and slots, Install games and Ready, with no Look, Trackers, Reports, YAML or game box; the engine's permission is a row on the page, Allow once starts the run whose progress shows in the rail while the window moves on; a room link brings the status read's row, and once allowed the room is read once (the server and the players to tick); cleared, its players go; Next saves the multiworld with its slot, selects it and connects the slot, whose game comes from the room; Back keeps the slot once and Next again updates the same multiworld; Install games is on the page with the slot's game first and ticked, its Add waiting for the engine and its engine lead leading to the engine's page; Skip goes on; Ready says what's left with Finish and three links to Settings, a link putting the window out of the way until Resume; Finish keeps the slot connected; nothing opens on top of the window; Home's line counts what was done and Resume opens at the step left, as does Help → Set up Atlas…",
            SetupWizardAsync);
        await ScenarioAsync("Engine page slots: the Atlas Engine page's Your slots offers a YAML only when it would help (a linked one shows Change YAML and Unlink; unlinked, nothing); shown plainly, its Advanced part stays folded, Show details opens it; Copy log says so in the status line; the seed test shows only in developer mode",
            EnginePageSlotsAsync);
        await ScenarioAsync("Display scale: at Windows' 125% to 200% on 1080p, 1440p and 4K screens, and in an 1100-wide window at 125%, the window is drawn at that scale with a smallest size that fits, every pane stays inside it without overlapping (Properties hides itself when the width can't hold it, its setting kept), Home's cards take the columns the width allows, the Atlas Engine page fits the content area, and About, Help, the shortcuts, a confirmation and the command palette each fit the window they open in",
            DisplayScaleAsync);
        await ScenarioAsync("Crash reports: a problem last time is offered as a card; the dialog shows the whole report as it would be sent, with the user's name, paths, the server, the slot and an e-mail replaced by marks; Send once posts a Sentry envelope to the project (scrubbed, with the note) and the file isn't offered again; with Always send, the next is sent without asking; Don't send dismisses it and nothing is posted; Help → Report a problem shows each part scrubbed (players seen in rooms too) with a tick and the event as it would be sent, posts nothing before Send, sends only the ticked parts with the note and shows the report's id, keeps a copy, and Just save it and Cancel post nothing; a build without an address offers nothing and greys Send with the reason",
            CrashReportsAsync);
        await ScenarioAsync("Customization: a custom accent of any colour recolours Atlas, is saved, and its headings and links still read (the preset choice says Custom); colour-blind-safe colours are the theme's other palette, held to the contrast rule, saved and undone; the zoom choice, Ctrl+= and Ctrl+- scale the window and are saved; the pin shape is saved and every map redraws, a diamond being a square on its corner; the page Atlas opens on is saved and the last tool shown is remembered for it",
            CustomizationAsync);
        await ScenarioAsync("Tables: a click on a column's title sorts by it (the column's own first direction, then the other; the arrow says which), typed words narrow the rows, a column hides and shows, pinned rows stay on top, the rows export as TSV, Markdown, Discord parts and a CSV file, the sort and the hidden columns are saved, and unchanged rows are updated in place with the selection kept; thousands of rows lay out only the ones on screen (a frame no longer than a few rows take), which the scroll bar, the wheel and the keys move through",
            TablesAsync);
        await ScenarioAsync("Confirmations and empty states: deleting a slot or a multiworld asks first and cancelling keeps them; without the logic engine, Key Items and the Logic Tracker both say what logic needs; go mode isn't claimed while unknown",
            ConfirmationsAndEmptyStatesAsync);
        await ScenarioAsync("Bursts: 40 items and 40 chat lines arriving together reach the slot once each, at most one update of the window in each frame they arrive over (not 80)",
            BurstIsOneUpdateAsync, attempts: 2);
        await ScenarioAsync("Long text: a game's names and a hint's entrance of a megabyte, and a chat line of 100,000 characters without a space, hold up no frame for 150 ms, shown or not (names are cut to 500 characters; a long run of text gets breaks)",
            LongTextHoldsUpNothingAsync, attempts: 2);
        await ScenarioAsync("Hostile server: a login naming players that can't be, messages that aren't JSON, thousands of items and hints with unknown numbers, floods of lines, bounces and checks, and a room update naming strangers leave the slot connected, hold up no frame for 150 ms as they arrive or as a tool shows them, log no error and put one line about the unreadable messages in the log; a scout the server never answers is given up after a time and asked again next time",
            HostileServerAsync, attempts: 2);
        await ScenarioAsync("Room text: one connection per multiworld receives the room's text and every slot's text client shows each line once, named from that slot's view; a command typed into a quiet slot gets its answer; new hints show in the slots they concern; when the text slot leaves, another takes over",
            RoomTextReachesEverySlotAsync);
        await ScenarioAsync("Moving a slot's panel: out of the window and docked elsewhere, the slot keeps its connection, views and updates, and shows what arrived meanwhile",
            SlotPanelMovesWholeAsync);
        await ScenarioAsync("Map packs: a slot's pack has its images while the slot is connected (its map shows them), the Pack Doctor's while its window is open (its overview saying what Atlas's pack lab measured for that very file); then they're freed once another pack is used",
            PackImagesFollowTheirUsersAsync);
        await ScenarioAsync("Maps without a picture and checks without a pin: a map whose pack ships no usable picture lists its locations (named in a note, each opening in Properties) instead of dots on nothing, and a map with its picture shows its pins; the seed's checks no pin places are listed under Not on the map and counted on the map, and a check that's done leaves the list; Follow my checks is on for a pack that can't follow the game, shows the map of a check made on another map, leaves the map alone while the user zooms it, and is kept per slot",
            PicturelessMapsAsync);
        await ScenarioAsync("Fix a problem where it shows: the Map Tracker's and Key Items' fix menus (a button, right-click on the map, a pin, a tile) give a map without a picture one of the pack's, move a pin, change which check a section shows, mark one as not a check, place a missing check, give a tile another picture; each a Pack Doctor fix the slot shows at once, with Undo; a check made in play that no pin shows becomes a notice (a release's burst doesn't), counted on the Map button, and its Pick its pin links the pin pressed next",
            FixInPlaceAsync);
        await ScenarioAsync("Pack Doctor: on a pack's first check, a pin section whose name matches a location exactly (once punctuation is ignored) is linked by itself as an automatic fix, and a tile named so is the index's own link; the pack's row on its game's page offers Review N, which opens the Doctor on Recommended; the Recommended tab lists what's left, Apply on a row writes the fix, the row goes after the check and the status says so; the rows are grouped by kind with what to do under each, filters by words, kind and map narrow them, Ignore selected hides the selection in one step and Undo brings it back; Undo takes the automatic link back; the Map Tracker's Attention group lists the pack's problems for this seed in plain words and opens the Doctor on the fixing tab; Fix what Atlas can (the pack row's Fix, the Attention group's button) links the confident matches and sets the rest aside in one undoable step, said in a card",
            PackDoctorAsync);
        await ScenarioAsync("Guided fixer: the Pack Doctor opens on Make this pack work, whose steps say what's left in order; two rows linked by hand in the one-by-one review teach a rule that links the rest, shown in a preview first and applied as one undoable step; the review's keys skip, say not a check, undo and take the first guess; the pack is measured against this seed's names",
            GuidedFixerAsync);
        await ScenarioAsync("Sharing fixes: the author's report lists what the user fixed by hand; a pack's fixes export to one file with their pictures and import into a pack without them (a preview first, one undo step), where both fixed the same thing the user's stays unless they tick the file's; Suggest them to Atlas shows the user's own fixes ticked and exactly what goes, sends only the ticked ones (scrubbed, with the pack's facts) as one event with the fixes attached, and keeps a copy",
            SharingFixesAsync);
        await ScenarioAsync("Start a pack, and the last fixer: a game with no map pack gets Start a pack on its empty map, which makes a PopTracker pack from the game's names (every location a pin grouped by area, every item in Key Items, its own mapping) that the slot uses at once with every location linked; the guide places a check no pin shows beside its area's pin after a preview, drawn as Atlas's",
            StartPackAndFixersAsync);
        await ScenarioAsync("Map packs reach connected slots: Key Items offers the pack's layouts and two Atlas builds by item group, kept per slot; the chat's filters sit in one row; the Chat and System Log tabs carry a dot for lines that arrived while they weren't showing, cleared when shown; a slot without a pack offers \"Find a map pack for <game>…\" on its map, which shows the game's page and searches GitHub once; a pack chosen from the results downloads with a progress line, installs with a card and reaches the slot's map; Atlas picks a game's pack by itself (of the projects found, one with more stars whose pack doesn't fit the game's names is passed over for the one that fits); a pack installed while a slot is connected shows on its map at once, without a reconnect; deleted, the map says there's no pack again; another pack for the game takes its place; a pack half of whose pins link to the seed's checks says so on the map, and its button makes its project's release found for the seed the game's pack, which the slot switches to",
            PacksReachConnectedSlotsAsync);
        await ScenarioAsync("Live map following: a pack whose scripts follow the game switches the map to the tab the room's data storage names, at connect and when it changes (Atlas asks the server about the key and never writes the room's data); with the switch off, the map stays; the switch is remembered per slot",
            LiveMapFollowingAsync);
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
        await ScenarioAsync("Logic banner: when the engine runs but the game's apworld is missing, the Logic Tracker's banner offers Install this game… when Atlas's game list knows the game (Install games opens with it ticked, and the empty map offers its pack), else the Games page (not the engine setup), and pressing it shows the Games page on the game",
            LogicBannerAsync);
        await ScenarioAsync("Seed versions, logic matches: a slot whose seed was made with another apworld version, but whose rebuilt world matches the server's, runs logic on the installed version with no warning anywhere, and Atlas looks for nothing",
            SeedVersionDiffersButLogicMatchesAsync);
        await ScenarioAsync("Seed versions, found: when the seed was made with another version and logic doesn't match it, Atlas tries the versions it knows from trusted projects, newest first, without asking GitHub (the banner says so meanwhile), uses the first whose data is the seed's for that slot, and a card says which",
            SeedVersionFoundAsync);
        await ScenarioAsync("Seed versions, not found: when a world that can't be rebuilt was made with another version, Atlas tries three known versions at most, then the Logic banner offers Choose the version…, which opens the picker with the versions tried",
            SeedVersionNotFoundAsync);
        await ScenarioAsync("Found entrances: a world that learns the entrances found in the game from the room's data storage (as Universal Tracker's own client does) has the slot ask the server about its key and read it at connect; each value is passed to the engine and what it opens is a step of its own; a new engine hears them again; Atlas never writes the key",
            FoundEntrancesAsync);
        await ScenarioAsync("Logic you can trust: once a slot's logic starts, the engine rebuilds its world with other seeds; the checks the rebuilds disagree about, and those the rebuilt world lacks, aren't called in or out of logic but listed under Checklist with one short banner (its tooltip names the missing ones; the approximate-logic banner stays away; Link YAML… only while no YAML is linked); Show logic anyway (remembered per slot) shows them, and List them as a checklist puts them back; the slot's check is kept for its game and apworld, and the game's page and Add a slot say it (Check this game, under Debug, says it up front too); a world the engine can't rebuild says why in a few words and lists every check left as a checklist under its notice",
            LogicYouCanTrustAsync);
        await ScenarioAsync("Priority checks: the engine names the seed's priority checks when a slot starts (whatever option set them); they're starred in the Logic Tracker, on the map and in Properties, the slot card counts those in logic, and Priority only (remembered) lists them alone; a YAML's ignored Archipelago stamp is said in Properties",
            PriorityChecksAsync);
        await ScenarioAsync("Solo test: Test this game… on a game's page runs the whole chain against the fake engine (the apworld already there, a pack installed, a YAML from the engine, a seed generated again without the game's patch output when the first try wants a ROM, a server on this PC, the AtlasTest slot connected with the YAML, logic and the pack scored) and writes a scrubbed report with a JSON twin; the owner's notes land in it once; Stop ends the server and removes the test multiworld; a generation that fails outright ends the chain with the report saying so",
            SoloTestAsync);
        await ScenarioAsync("Solo test questions: a question the test asks (trusting the apworld's project) opens on the test's own window, so a click on that window can't bury it; Cancel ends the test at once even while the question waits; closing the window then answers the question \"no\", and a permission question whose window goes is a \"no\" that isn't remembered",
            SoloTestQuestionsAsync);
        await ScenarioAsync("Solo batch: Test every ready game… on the Games page runs the solo test over the games with a pack without a server or a slot (Host and Connect skipped, nothing installed, no multiworld made), writes a report per game saying so and a summary table naming each game's logic and pack scores",
            SoloBatchAsync);
        await ScenarioAsync("Release facts: \"Which version works with my map pack?\" asks once for the game (the downloads' size and the project it trusts), downloads its newest releases into the game's folder (nothing is installed), reads what each one's guide and manifest say and loads each in a throwaway engine process to measure the installed map pack against its checks; the versions table says what works here and greys what doesn't with the reason (another Archipelago; the pack not fitting while another release fits); Atlas's pick is the newest release the pack fits, a pre-release too; the installed version's card shows its facts with ✓, ✗ and – and offers Switch to the pick; asked again, nothing is asked or downloaded twice; the game's card offers Update to the newest full release until the look rules it out",
            ReleaseFactsAsync);
        await ScenarioAsync("Game worlds on demand: a game that ships with Archipelago is listed on the Games page; its slot's logic waits for its world and says so, nothing downloaded; \"Get the game's world…\" asks the engine's permission, then Archipelago's release is downloaded once (checked against its hash) and only that game's world and the world it imports are unpacked into the engine's user folder, recorded with Archipelago's version, and the slot's logic starts; an installed apworld for a game takes precedence; after an update to another Archipelago the unpacked worlds are removed",
            GameWorldsOnDemandAsync);
        await ScenarioAsync("Adding games, the plan: a game list fetched with sizes plans a community game's newest release and the best pack that doesn't wait behind, with the download, the disk and the project to trust totalled and nothing asked of GitHub; the list's sizes reach the versions Atlas reads",
            AddGamesPlanAsync);
        await ScenarioAsync("Install games installer: a plan of a community game, a game that ships with Archipelago and one whose apworld can't be downloaded runs as one batch: the apworld comes by the address and SHA-256 Atlas's game list names with no GitHub listing read (the requests for a game whose pack only its guide can name are kept in the plan), the official world is unpacked from Archipelago's release downloaded once, ONE engine health check covers both and the slots hear of the changed parts once; each map pack is installed from the list (the game's setup guide's own when the list has it, over the best fit) and made the game's pack, with no card and no Pack Doctor prompt, even from a connected slot of the game, whose logic restarts once (each pack's confident fixes made by Atlas, its card dropped since the summary says it); the apworld's project is trusted and the engine's downloads allowed for the session (always, with the tick); a second press while it runs is refused; one bar that never goes down says Game N of 3; the result names each game's logic and map, the failure with its reason, and per pack what Atlas fixed by itself; a cancelled run downloads and checks nothing",
            AddGamesInstallerAsync);
        await ScenarioAsync("Install games, the guide's own pack: a community game Atlas's list has no pack for keeps GitHub's requests for the project its setup guide names, the one confirm allows GitHub lookups until Atlas closes, and once its logic is in the guide's project's release is installed and made the game's pack (round 12: FireRed's tracker); the requests kept for a guide that names nothing are handed back to a later game's guide",
            AddGamesGuidePackAsync);
        await ScenarioAsync("Install games screen: Add a slot's Logic line offers Install this game…, which opens Install games over it with the game ticked; the empty map leads with Get a map pack for a game Atlas's list has a pack for; Home's Install games… lists every game, the games of your multiworlds first and ticked where they need something, each row in plain words with its download, a game with nothing to add not tickable, typed words narrowing the list and the totals below; Add opens ONE confirm, a line per game, its projects and what it trusts behind From N projects and Don't ask again, with nothing downloaded until Install; Back keeps the ticks; the run shows moving dots while a step runs without bar movement, and ends in a summary of a line per game with one number per item (a map that fits poorly offers the version that fits), the project trusted and the downloads always allowed by the tick; the Games overview's Install games… shows an added game as ready; Cancel during a batch stops it with nothing downloaded, and Try those again ticks what's left",
            AddGamesScreenAsync);
        await ScenarioAsync("Setup window, adding games: with the engine ready the window has no engine page; Next on Your multiworld and slots saves the multiworld and connects both slots, so Atlas learns their two games from the room, and meanwhile their Logic banners say the setup window adds the game with nothing to press; Install games on the page comes with both games first and ticked, its buttons the window's own (Install 2 games…, the one confirm with its Back beside Next and the window's Back and Skip aside, Install, the bar with the list on the left locked, the summary with what Atlas fixed in each pack; every word fits the page); nothing opens on top of the window; the slots' logic starts by itself once their games are in; Ready says you're set up",
            SetupAddGamesAsync);
        await ScenarioAsync("Atlas fixes packs by itself: a pack's first check links its exact match and its confident one (85%+) as Atlas's own fixes, leaves the 60% one on the map's Notices, and one card says what it fixed, with Review fixes; the Changes tab beside Notices lists both as Atlas's and a check hidden on the map, each with Undo (and Show on map); Review fixes' Undo takes one back and declines it, so neither the next check nor a new Atlas's pass makes it again and no card follows; Undo all takes the rest, and the Doctor's Undo brings it back; the map's one tip shows until Got it, for good",
            PackAutoFixAsync);
        await ScenarioAsync("BK: with logic running, checks left and none of them in logic, the Logic Tracker and Key Items say so with the count done, and the slot card says BK; an item that opens a check ends it",
            BkAsync);
        await ScenarioAsync("Hidden checks: a check logic wrongly calls in logic is hidden from the Logic Tracker's row menu (a right click), a map pin's menu, a Not on the map row's menu or Properties; it leaves the list, the map and the in-logic counts (logic itself unchanged) until unhidden, with Undo on the card; Show hidden lists it greyed and Show hidden checks dims its pin (both remembered); hiding the last check in logic makes the slot BK, and the slot's Unhide all (asked first) shows them all again",
            HiddenChecksAsync);
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
            // Reports scrub the room's players and server from now on.
            var seen = AP_Atlas.Core.Reports.SeenNames.Read(_crashReports!.ReportsDir);
            UiTestExpect(seen.Names.Contains("Tester") && seen.Servers.Contains(profile.ServerUrl.Trim()), $"the room's players and server weren't kept for scrubbing reports: {string.Join(", ", seen.Names)} / {string.Join(", ", seen.Servers)}");

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
    /// Windows that close leave nothing behind: the Pack Doctor and the race mode dialog,
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

        var setup = OpenSetupWizard(resume: false);
        await UiTestWaitAsync(0.3);
        setup.EmitSignal(Window.SignalName.CloseRequested);

        await UiTestWaitForAsync(() => !IsInstanceValid(doctor) && !IsInstanceValid(race)
            && !IsInstanceValid(shortcuts) && !IsInstanceValid(about) && !IsInstanceValid(palette) && !IsInstanceValid(setup) ? this : null, "the closed windows to be freed");
        return new List<(string What, System.WeakReference Weak)>
        {
            ("a closed Pack Doctor window", new System.WeakReference(doctor)),
            ("a closed race mode dialog", new System.WeakReference(race)),
            ("a closed Keyboard Shortcuts dialog", new System.WeakReference(shortcuts)),
            ("a closed About dialog", new System.WeakReference(about)),
            ("a closed Help window", new System.WeakReference(help)),
            ("a closed Notifications window", new System.WeakReference(notifications)),
            ("a closed command palette", new System.WeakReference(palette)),
            ("a closed setup window", new System.WeakReference(setup)),
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
                // The long sections (a multiworld's Slots) start collapsed, the short ones open; a section the user opens stays open.
                UiTestExpect(_propertiesPanel.SectionCollapsed("Slots") == true && _propertiesPanel.SectionCollapsed("Overview") == false,
                    $"Slots collapsed: {_propertiesPanel.SectionCollapsed("Slots")}, Overview collapsed: {_propertiesPanel.SectionCollapsed("Overview")}");
                _propertiesPanel.ToggleSection("Slots");
                UiTestExpect(_propertiesPanel.SectionCollapsed("Slots") == false && _appSettings.ExpandedPropertySections.Contains("Slots"), "opening Slots wasn't remembered");
                _propertiesPanel.ToggleSection("Slots");
                UiTestExpect(_propertiesPanel.SectionCollapsed("Slots") == true && !_appSettings.ExpandedPropertySections.Contains("Slots"), "closing Slots again wasn't remembered");
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
            [AP_Atlas.UI.Tool.CheeseTracker] = _cheeseTab,
            [AP_Atlas.UI.Tool.SphereTracker] = _sphereTab,
            [AP_Atlas.UI.Tool.Settings] = _settingsPage!,
            [AP_Atlas.UI.Tool.Home] = _homePage!,
            [AP_Atlas.UI.Tool.Games] = _gamesPage!,
            [AP_Atlas.UI.Tool.Engine] = _enginePage!,
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
        host.ShowTool(AP_Atlas.UI.Tool.Games);
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
        UiTestExpect(tools.GetItemText(0) == "Home" && ShortcutShown(tools, 0) == "Ctrl+1" && tools.GetItemText(1) == "Map Tracker" && ShortcutShown(tools, 1) == "Ctrl+2",
            $"the Tools menu's first items are \"{tools.GetItemText(0)}\" ({ShortcutShown(tools, 0)}) and \"{tools.GetItemText(1)}\" ({ShortcutShown(tools, 1)})");
        UiTestExpect(tools.GetItemIndex(DebugSubmenuItemId) < 0 && _debugMenu != null && _commandItems[_debugMenu].Count == 6,
            "the Tools menu shows Debug outside developer mode, or its submenu doesn't hold the six testing tools");

        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        _commands!.Run("tool.games");
        UiTestExpect(ShownContent() == _gamesPage, "the Games command didn't show Games");
        UiTestExpect(_commands.Find("tool.map-packs") == null, "Map Packs is still a command of its own (it's part of Games)");
        UiTestExpect(_commands.ShortcutOf("tool.games") == "" && _commands.ShortcutOf("view.zoom-reset") == "Ctrl+0", "Ctrl+0 belongs to Reset Zoom, and Games has no default key");
        _commands.Run("tool.engine");
        UiTestExpect(ShownContent() == _enginePage && _commands.Find("tools.engine") == null && _commands.ShortcutOf("tool.engine") == "",
            "Tools → Atlas Engine didn't show the Engine page, or the engine window's command is still there, or the page took a key");
        await PressAsync("Ctrl+9");
        UiTestExpect(ShownContent() == _sphereTab, "Ctrl+9 didn't show the Sphere Tracker");
        // A rebind in the settings takes over at once (as a user might type it), and the old key means nothing.
        _appSettings.KeyBindings["tool.games"] = "ctrl+f6";
        try
        {
            await PressAsync("Ctrl+F6");
            UiTestExpect(ShownContent() == _gamesPage, "a rebound key (Ctrl+F6) didn't show Games");
            host.ShowTool(AP_Atlas.UI.Tool.SphereTracker);
            await PressAsync("Ctrl+F7"); // nothing's key
            UiTestExpect(ShownContent() == _sphereTab, "a key that isn't anyone's switched tools");
            await PressAsync("F1");
            var shortcuts = await UiTestWaitForAsync(() => GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => d.Title == "Keyboard Shortcuts"), "the shortcuts dialog");
            // The table lays out only the rows on screen: the list is its shown rows.
            var rows = shortcuts.FindChildren("Table_keys", "", true, false).OfType<AP_Atlas.UI.AtlasTable>().First().ShownRows.Select(r => r.Cells.Select(c => c.Text).ToArray()).ToList();
            UiTestExpect(rows.Any(r => r[0] == "Games" && r[1] == "Ctrl+F6") && rows.Any(r => r[0] == "Map Tracker" && r[1] == "Ctrl+2"),
                "the shortcuts list doesn't show the keys as they are now");
            shortcuts.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
        }
        finally
        {
            _appSettings.KeyBindings.Remove("tool.games");
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
        await PressAsync("Ctrl+1");
        UiTestExpect(ShownContent() == _homePage, "Ctrl+1 didn't switch tools while the search box had the focus");
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
        UiTestExpect(page.IsShown("key.debug.check-game"), "a Debug command's key row doesn't show in developer mode");
        developer.ButtonPressed = false;
        UiTestExpect(_bottomTabs.IsTabHidden(2) && !_appSettings.DeveloperMode, "the Debug Log tab didn't go away with developer mode");
        UiTestExpect(!page.IsShown("key.debug.check-game") && !page.IsAvailable("key.debug.check-game"), "a Debug command's key row stays outside developer mode");

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
        UiTestExpect(page.RowIds.Where(page.IsAvailable).All(page.IsShown) && !page.RowIds.Where(id => !page.IsAvailable(id)).Any(page.IsShown) && !page.NothingMatches,
            "clearing the search didn't bring every row back, or brought a Debug command's key outside developer mode");

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
        const string RowId = "key.tool.games";
        var tools = _menuHbox.GetChildren().OfType<MenuButton>().ToList()[3].GetPopup();
        int item = tools.GetItemIndex((int)_commandItems[tools].First(entry => entry.Value == "tool.games").Key);
        var capture = (AP_Atlas.UI.KeyCapture)page.ControlOf(RowId);
        var reset = page.KeyResetOf(RowId);
        var conflict = page.KeyConflictOf(RowId);
        var otherConflict = page.KeyConflictOf("key.tool.map-tracker");
        ShowSettings("keyboard");
        await UiTestWaitAsync(0.1);
        UiTestExpect(ShownContent() == page && page.ScrollPosition > 0, "the keyboard section didn't show");
        UiTestExpect(capture.Key == "" && reset.Disabled && !conflict.Visible, $"Games' row shows \"{capture.Text}\" (no default key), reset {(reset.Disabled ? "off" : "on")}, conflict {conflict.Visible}");
        try
        {
            // Press the key's button, then a key: the command runs on it at once, the menus and the bar show it, and it's saved.
            capture.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(capture.Capturing, "pressing the key's button didn't start waiting for a key");
            capture.EmitSignal(Control.SignalName.GuiInput, AP_Atlas.UI.CommandKeys.ToEvent("Ctrl+F6")!);
            UiTestExpect(!capture.Capturing && capture.Key == "Ctrl+F6" && _appSettings.KeyBindings.GetValueOrDefault("tool.games") == "Ctrl+F6"
                && DataManager.LoadSettings().KeyBindings.GetValueOrDefault("tool.games") == "Ctrl+F6", "the pressed key wasn't taken, or wasn't saved");
            UiTestExpect(!reset.Disabled, "a changed key leaves the reset off");
            UiTestExpect(ShortcutShown(tools, item) == "Ctrl+F6" && _activityBar.ButtonOf(AP_Atlas.UI.Tool.Games).TooltipText.Contains("Ctrl+F6"), "the menus and the bar don't show the new key");
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            await PressAsync("Ctrl+F6");
            UiTestExpect(ShownContent() == _gamesPage, "the new key doesn't run the command");
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            await PressAsync("Ctrl+0");
            UiTestExpect(ShownContent() == _connectionPanel, "the old key still runs the command");
            host.ShowTool(AP_Atlas.UI.Tool.Settings);

            // A key another command has: both rows say so.
            capture.EmitSignal(BaseButton.SignalName.Pressed);
            capture.EmitSignal(Control.SignalName.GuiInput, AP_Atlas.UI.CommandKeys.ToEvent("Ctrl+2")!);
            UiTestExpect(conflict.Visible && conflict.TooltipText.Contains("Map Tracker") && otherConflict.Visible && otherConflict.TooltipText.Contains("Games"),
                $"a shared key isn't shown on both rows: {conflict.Visible} \"{conflict.TooltipText}\", {otherConflict.Visible} \"{otherConflict.TooltipText}\"");

            // Reset: the default key again, the rebind forgotten.
            reset.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(!_appSettings.KeyBindings.ContainsKey("tool.games") && capture.Key == "" && reset.Disabled && !conflict.Visible && !otherConflict.Visible
                && ShortcutShown(tools, item) == "", "the reset didn't bring the default (no key) back");

            // Backspace: no key at all.
            capture.EmitSignal(BaseButton.SignalName.Pressed);
            capture.EmitSignal(Control.SignalName.GuiInput, AP_Atlas.UI.CommandKeys.ToEvent("Backspace")!);
            UiTestExpect(capture.Key == "" && capture.Text == "None" && _appSettings.KeyBindings.GetValueOrDefault("tool.games") == "" && ShortcutShown(tools, item) == "",
                $"Backspace didn't take the key away: \"{capture.Text}\", menu \"{ShortcutShown(tools, item)}\"");
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            await PressAsync("Ctrl+0");
            UiTestExpect(ShownContent() == _connectionPanel, "a key taken away still runs the command");
            host.ShowTool(AP_Atlas.UI.Tool.Settings);
            reset.EmitSignal(BaseButton.SignalName.Pressed);

            // Escape keeps the key; a modifier on its own isn't a key; losing the focus ends the wait.
            capture.EmitSignal(BaseButton.SignalName.Pressed);
            capture.EmitSignal(Control.SignalName.GuiInput, AP_Atlas.UI.CommandKeys.ToEvent("Escape")!);
            UiTestExpect(!capture.Capturing && capture.Key == "" && !_appSettings.KeyBindings.ContainsKey("tool.games"), "Escape didn't keep the key as it was");
            capture.EmitSignal(BaseButton.SignalName.Pressed);
            capture.EmitSignal(Control.SignalName.GuiInput, new InputEventKey { Pressed = true, Keycode = Key.Ctrl, CtrlPressed = true });
            UiTestExpect(capture.Capturing && capture.Key == "", "a modifier on its own was taken as the key");
            capture.EmitSignal(Control.SignalName.FocusExited);
            UiTestExpect(!capture.Capturing && capture.Key == "", "losing the focus didn't end the wait");

            // The rows are found by the command's words; the F1 list leads here.
            page.Search("games");
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
            _appSettings.KeyBindings.Remove("tool.games");
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

    private async Task SoloTestQuestionsAsync()
    {
        StartFakeEngine(UiTestPython()).Apply();
        var checkBefore = AtlasEngine.State.LastCheck;
        AtlasEngine.State.LastCheck = new EngineCheckResult { Games = new List<string> { "Test Game" }, TrackerLoads = true };
        // The window's hooks, with the apworld install standing in for the Games page's: it asks the trust question where
        // the window's hook would (the test's own window while it shows) and waits for the answer.
        TaskCompletionSource<bool>? answer = null;
        var real = SoloHooks();
        var hooks = new AP_Atlas.Core.EngineSetup.SoloTestHooks
        {
            Settings = real.Settings,
            InstallApworldAsync = async game =>
            {
                answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                AP_Atlas.UI.Dialogs.Confirm(SoloQuestionParent(), "Trust this project?", "Install the Question Game apworld from github.com/uitest/question-game?", "Trust and install",
                    () => answer.TrySetResult(true), null, () => answer.TrySetResult(false));
                return (await answer.Task, null);
            },
            EnsureWorldAsync = real.EnsureWorldAsync,
            PickPackAsync = real.PickPackAsync,
            ChoosePackAsync = real.ChoosePackAsync,
            InstallPackAsync = real.InstallPackAsync,
            RecommendedPack = real.RecommendedPack,
            PreferPack = real.PreferPack,
            AddProfile = real.AddProfile,
            RemoveProfile = real.RemoveProfile,
            ConnectSlotAsync = real.ConnectSlotAsync,
            Scrubber = real.Scrubber,
            AtlasVersionLine = real.AtlasVersionLine,
            Notice = real.Notice,
        };
        string reports = AP_Atlas.Core.EngineSetup.SoloTestRunner.ReportsFolder;
        var dialog = AP_Atlas.UI.SoloTestDialog.Open(this, "Question Game", text => Tr(text), hooks);
        var holder = new Control();
        AddChild(holder);
        try
        {
            dialog.StartForTests();
            var question = await UiTestWaitForAsync(() => dialog.GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == "Trust this project?"), "the test's question");
            UiTestExpect(question.GetParent() == dialog && question.Visible && question.Exclusive, "the test's question didn't open on the test's own window");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            dialog.CancelForTests();
            await UiTestWaitForAsync(() => dialog.StepStates()[AP_Atlas.Core.Reports.SoloTestStep.Report] == AP_Atlas.Core.Reports.SoloStepOutcome.Done ? dialog : null, "the cancelled test to end", 10);
            UiTestExpect(dialog.StepStates()[AP_Atlas.Core.Reports.SoloTestStep.Apworld] == AP_Atlas.Core.Reports.SoloStepOutcome.Cancelled && !dialog.Runner.Busy && clock.Elapsed < TimeSpan.FromSeconds(5),
                $"Cancel didn't end the test while its question waited: the apworld step is {dialog.StepStates()[AP_Atlas.Core.Reports.SoloTestStep.Apworld]}, busy {dialog.Runner.Busy}, {clock.Elapsed.TotalSeconds:0.0} s");
            UiTestExpect(answer != null && !answer.Task.IsCompleted, "the question was answered before anyone answered it");
            // Closing the test's window (nothing runs now) takes its question with it, answered "no".
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitForAsync(() => answer!.Task.IsCompleted ? answer : null, "the question to be answered when its window closed");
            UiTestExpect(!await answer!.Task, "a question whose window closed counted as a yes");
            // A permission question whose window goes away is a "no", and nothing is remembered for it.
            bool? decided = null;
            AP_Atlas.UI.PermissionDialog.Ask(holder, _appSettings, AP_Atlas.Core.Permissions.WriteArchipelago, "uitest-question-scope", null, allowed => decided = allowed);
            UiTestExpect(holder.GetChildren().OfType<ConfirmationDialog>().Any(), "the permission question wasn't asked");
            holder.QueueFree();
            await UiTestWaitForAsync(() => decided != null ? (object)decided : null, "the permission question to answer when its window went");
            UiTestExpect(decided == false && !AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.WriteArchipelago, "uitest-question-scope"),
                $"a permission question whose window went answered {decided}");
        }
        finally
        {
            AtlasEngine.State.LastCheck = checkBefore;
            if (IsInstanceValid(dialog) && !dialog.IsQueuedForDeletion()) dialog.QueueFree();
            if (IsInstanceValid(holder) && !holder.IsQueuedForDeletion()) holder.QueueFree();
            AP_Atlas.Core.EngineSetup.SoloTestRunner.StopAll();
            if (System.IO.Directory.Exists(reports)) foreach (string file in System.IO.Directory.GetFiles(reports, "Question_Game-*")) System.IO.File.Delete(file);
        }
    }

    private async Task SoloTestAsync()
    {
        var engine = StartFakeEngine(UiTestPython());
        await using var server = LogicWorldServer();
        server.Slots.Add(AP_Atlas.Core.EngineSetup.SoloTestRunner.SlotName);
        engine.HostPort = server.Url.Port;
        engine.GenerateError = "FileNotFoundError: Zelda no Densetsu.sfc";
        engine.Apply();
        var checkBefore = AtlasEngine.State.LastCheck;
        AtlasEngine.State.LastCheck = new EngineCheckResult { Games = new List<string> { "Test Game" }, TrackerLoads = true };
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_solo_pack.zip");
        FakeMapPack.Write(zip, "UI test solo pack", "Test Game", initLua: FakeMapPack.FollowingInitLua);
        AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        string reports = AP_Atlas.Core.EngineSetup.SoloTestRunner.ReportsFolder;
        AP_Atlas.UI.SoloTestDialog? Dialog() => GetChildren().OfType<AP_Atlas.UI.SoloTestDialog>().FirstOrDefault(d => !d.IsQueuedForDeletion());
        // An apworld file with a bundled setup guide, as a real one ships it (the fake engine doesn't read the folder).
        string worlds = AtlasEngine.UserWorldsDir;
        System.IO.Directory.CreateDirectory(worlds);
        string apworld = System.IO.Path.Combine(worlds, "test_game.apworld");
        static void WriteApworld(string path)
        {
            using var stream = System.IO.File.Create(path);
            using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
            foreach (var (name, text) in new[]
            {
                ("test_game/archipelago.json", "{\"game\": \"Test Game\"}"),
                ("test_game/docs/setup_en.md", "# Test Game setup\n\nInstall the Test Game client from its release.\n"),
                ("test_game/docs/en_Test Game.md", "# Test Game\n\nA game for tests.\n")
            })
            {
                using var writer = new System.IO.StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        WriteApworld(apworld);
        bool developerBefore = _appSettings.DeveloperMode;
        SetDeveloperModeForTests(true); // the solo test is a testing tool (Debug)
        try
        {
            host.ShowTool(AP_Atlas.UI.Tool.Games);
            _gamesPage!.Select("Test Game");
            await UiTestWaitAsync(0.2);
            // The apworld's own documents open in the Help window as topics of their own.
            UiTestExpect(_gamesPage.LinkTexts().Contains(Tr("Setup guide (from the apworld)")) && _gamesPage.LinkTexts().Contains(Tr("About the game (from the apworld)")),
                $"the game's page doesn't offer the apworld's documents: {string.Join(", ", _gamesPage.LinkTexts())}");
            _gamesPage.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.Text == Tr("Setup guide (from the apworld)")).EmitSignal(BaseButton.SignalName.Pressed);
            var help = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.HelpWindow>().FirstOrDefault(), "the Help window");
            UiTestExpect(help.CurrentPageId == "apworld:Test Game:setup" && help.ShownText.Contains("Install the Test Game client"), $"the Help window shows {help.CurrentPageId}, not the apworld's guide");
            help.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitAsync(0.1);
            var button = _gamesPage.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.HasMeta("solo_test_button"));
            UiTestExpect(button != null && !button.Disabled, "the game's page has no enabled Test this game… button");
            button!.EmitSignal(BaseButton.SignalName.Pressed);
            var dialog = await UiTestWaitForAsync(Dialog, "the solo test dialog");
            // GitHub's pack results as rows, each naming its project (three bare Install buttons showed in the owner's beta).
            dialog.ShowPackCandidates("Test Game", new List<AP_Atlas.Core.MapPackManagerControl.PackCandidate>
            {
                new() { Repo = "packs/test-game-poptracker", Stars = 3, Description = "A PopTracker pack for Test Game" },
                new() { Repo = "fork/test-game-poptracker", Stars = 4, Fork = true }
            }, null);
            await UiTestWaitAsync(0.2);
            var packRows = dialog.FindChildren("*", nameof(HBoxContainer), true, false).OfType<HBoxContainer>().Where(r => r.HasMeta("pack_row")).ToList();
            var rowLabels = packRows.Select(r => r.GetChildren().OfType<Label>().FirstOrDefault()).ToList();
            UiTestExpect(packRows.Count == 2 && rowLabels.All(l => l != null && l.IsVisibleInTree() && l.Size.X > 100 && l.Size.Y > 8)
                && rowLabels[0]!.Text.StartsWith("packs/test-game-poptracker  ★3") && rowLabels[1]!.Text.StartsWith("fork/test-game-poptracker  ★4 · a fork"),
                $"the pack rows don't name their projects: {string.Join(" | ", rowLabels.Select(l => l == null ? "no label" : l.Text + " " + l.Size))}");
            dialog.HidePackChoiceForTests();
            dialog.StartForTests();
            await UiTestWaitForAsync(() => dialog.StepStates()[AP_Atlas.Core.Reports.SoloTestStep.Report] == AP_Atlas.Core.Reports.SoloStepOutcome.Done ? dialog : null, "the chain to end", 90);
            var states = dialog.StepStates();
            UiTestExpect(states[AP_Atlas.Core.Reports.SoloTestStep.Apworld] == AP_Atlas.Core.Reports.SoloStepOutcome.Skipped && states[AP_Atlas.Core.Reports.SoloTestStep.MapPack] == AP_Atlas.Core.Reports.SoloStepOutcome.Skipped
                && states.Where(s => s.Key is not AP_Atlas.Core.Reports.SoloTestStep.Apworld and not AP_Atlas.Core.Reports.SoloTestStep.MapPack).All(s => s.Value == AP_Atlas.Core.Reports.SoloStepOutcome.Done),
                $"the steps ended as {string.Join(", ", states.Select(s => s.Key + ":" + s.Value))}");
            // The engine saw one template request, two generations (the second without the game's patch output), one host and one seed test.
            var generates = engine.ComponentRequests("AtlasGenerate");
            UiTestExpect(engine.ComponentRequests("AtlasYamlTemplate").Count == 1 && generates.Count == 2 && generates[1]["skip_patch_games"]?.ToString().Contains("Test Game") == true
                && engine.ComponentRequests("AtlasHost").Count == 1 && engine.ComponentRequests("AtlasSeedTest").Count == 1,
                $"the engine saw {engine.ComponentRequests("AtlasYamlTemplate").Count} template, {generates.Count} generate, {engine.ComponentRequests("AtlasHost").Count} host and {engine.ComponentRequests("AtlasSeedTest").Count} seed test requests");
            // The test multiworld points at the hosted server; the slot logged in by its name with the YAML linked, and its logic settled.
            var runner = dialog.Runner;
            var profile = runner.Profile;
            UiTestExpect(profile != null && profile.Name == "Solo test: Test Game" && profile.ServerUrl == "ws://127.0.0.1:" + server.Url.Port && runner.ServerAddress == profile.ServerUrl,
                $"the test multiworld is {profile?.Name} at {profile?.ServerUrl} (server {runner.ServerAddress})");
            UiTestExpect(server.Count("Connect") == 1, $"{server.Count("Connect")} logins reached the test server");
            string yamlKey = AP_Atlas.Core.Annotations.SlotKey(profile!.Id, AP_Atlas.Core.EngineSetup.SoloTestRunner.SlotName);
            var init = engine.Requests("init").LastOrDefault();
            UiTestExpect(init != null && (string?)init["player_name"] == "AtlasTest" && ((string?)init["yaml_path"] ?? "").EndsWith("AtlasTest.yaml", StringComparison.OrdinalIgnoreCase)
                && _appSettings.SlotYamlPaths.TryGetValue(yamlKey, out var linked) && linked.EndsWith("AtlasTest.yaml", StringComparison.OrdinalIgnoreCase),
                $"the slot's logic didn't start from the test YAML: {init?["yaml_path"]}");
            var slot = SlotView(profile.Id, AP_Atlas.Core.EngineSetup.SoloTestRunner.SlotName);
            UiTestExpect(slot != null && slot.LogicSettled && slot.ActiveLogicCount == 1, $"the test slot's logic: settled {slot?.LogicSettled}, {slot?.ActiveLogicCount} in logic");
            // The report: both files, the eight headings in order, scrubbed; the JSON twin parses.
            var mds = System.IO.Directory.GetFiles(reports, "Test_Game-*.md");
            UiTestExpect(mds.Length == 1 && System.IO.File.Exists(System.IO.Path.ChangeExtension(mds[0], ".json")), $"{mds.Length} report(s) written");
            string md = await System.IO.File.ReadAllTextAsync(mds[0]);
            int last = -1;
            foreach (string heading in AP_Atlas.Core.Reports.SoloTestReport.Headings)
            {
                int at = md.IndexOf(heading, StringComparison.Ordinal);
                UiTestExpect(at > last, $"the report lacks {heading} in its place");
                last = at;
            }
            UiTestExpect(md.Contains("exact") && md.Contains("patch output was skipped") && md.Contains("engine\\solo\\Test Game\\players\\AtlasTest.yaml") && !md.Contains(DataManager.GetDataDirectory()) && !md.Contains(":\\"), "the report isn't scored and scrubbed as expected");
            var json = Newtonsoft.Json.Linq.JObject.Parse(await System.IO.File.ReadAllTextAsync(System.IO.Path.ChangeExtension(mds[0], ".json")));
            UiTestExpect((int?)json["Schema"] == 2 && (int?)json["Generation"]?["Sphere0"] == 1 && (bool?)json["Logic"]?["Exact"] == true && (int?)json["Logic"]?["ReachableAtConnect"] == 1
                && json["Logic"]?["NotReached"] is Newtonsoft.Json.Linq.JArray { Count: 0 } && json["Logic"]?["BeyondSphere0"] is Newtonsoft.Json.Linq.JArray { Count: 0 } && md.Contains("Sphere 0 and the live logic at connect: identical"), "the JSON twin lacks the numbers, or sphere 0 wasn't compared by name");
            // The findings: grouped and explained, the apworld's setup guide among them; the author's section names the pack.
            UiTestExpect(md.Contains("### Note: The game's apworld comes with a setup guide (Setup)") && md.Contains("Written for the author of UI test solo pack")
                && json["Findings"] is Newtonsoft.Json.Linq.JArray { Count: > 0 }, "the report's findings or the author's section are missing");
            // The owner's notes land once, even saved twice.
            dialog.FillNotesForTests(new AP_Atlas.Core.Reports.SoloOwnerNotes("A pin in the sea.", "", "Nothing", ""));
            dialog.SaveNotesForTests();
            dialog.SaveNotesForTests();
            md = await System.IO.File.ReadAllTextAsync(mds[0]);
            UiTestExpect(md.Split("A pin in the sea.").Length == 2 && md.Split(AP_Atlas.Core.Reports.SoloTestReport.NotesHeading).Length == 2, "the notes weren't written once");
            // The window doesn't block Atlas; Close while the server runs hides it, the Games page brings it back and can stop the server.
            UiTestExpect(!dialog.Exclusive, "the solo test's window blocks the rest of Atlas");
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(GodotObject.IsInstanceValid(dialog) && !dialog.IsQueuedForDeletion() && !dialog.Visible && runner.ServerAddress != null, "Close while the server runs didn't just hide the window");
            await UiTestWaitAsync(0.1);
            button = _gamesPage.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.HasMeta("solo_test_button"));
            var stopButton = _gamesPage.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.HasMeta("solo_stop_button"));
            UiTestExpect(button != null && button.Text == Tr("Show the test") && stopButton != null, "the Games page doesn't say the test runs");
            button!.EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(dialog.Visible && Dialog() == dialog, "the Games page didn't bring the same window back");
            // Stop: the server ends, the slot disconnects and the test multiworld goes.
            int running = AP_Atlas.Core.EngineSetup.ProcessJob.RunningUnder(AtlasEngine.ArchipelagoDir);
            stopButton!.EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitForAsync(() => !_profiles.Contains(profile) && SlotView(profile.Id, AP_Atlas.Core.EngineSetup.SoloTestRunner.SlotName) == null ? this : null, "the test multiworld to go");
            await UiTestWaitForAsync(() => AP_Atlas.Core.EngineSetup.ProcessJob.RunningUnder(AtlasEngine.ArchipelagoDir) < running ? this : null, "the test server to end");
            UiTestExpect(runner.ServerAddress == null && !_appSettings.SlotYamlPaths.ContainsKey(yamlKey), "the server address or the YAML link stayed");
            await UiTestWaitAsync(0.1);
            button = _gamesPage.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.HasMeta("solo_test_button"));
            UiTestExpect(button != null && button.Text == Tr("Test this game…") && !_gamesPage.FindChildren("*", nameof(Button), true, false).OfType<Button>().Any(b => b.HasMeta("solo_stop_button")), "the Games page still says the test runs");
            UiTestExpect(_gamesPage.Steps().Any(s => s.Title == Tr("A solo test") && s.Done), "the game's page doesn't find the report it just wrote (Last tested)");
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(!GodotObject.IsInstanceValid(dialog) || dialog.IsQueuedForDeletion(), "Close with nothing running didn't close the window");
            // A generation that fails outright: the chain ends there, the report says so.
            engine.GenerateError = "ValueError: fill failed";
            engine.Apply();
            foreach (string old in mds) System.IO.File.Delete(old);
            button!.EmitSignal(BaseButton.SignalName.Pressed);
            dialog = await UiTestWaitForAsync(Dialog, "the dialog again");
            dialog.StartForTests();
            await UiTestWaitForAsync(() => dialog.StepStates()[AP_Atlas.Core.Reports.SoloTestStep.Report] == AP_Atlas.Core.Reports.SoloStepOutcome.Done ? dialog : null, "the second chain to end", 60);
            states = dialog.StepStates();
            UiTestExpect(states[AP_Atlas.Core.Reports.SoloTestStep.Generate] == AP_Atlas.Core.Reports.SoloStepOutcome.Failed && states[AP_Atlas.Core.Reports.SoloTestStep.Host] == AP_Atlas.Core.Reports.SoloStepOutcome.Pending
                && states[AP_Atlas.Core.Reports.SoloTestStep.Connect] == AP_Atlas.Core.Reports.SoloStepOutcome.Pending, $"a failed generation didn't end the chain: {string.Join(", ", states.Select(s => s.Key + ":" + s.Value))}");
            var failed = System.IO.Directory.GetFiles(reports, "Test_Game-*.md");
            UiTestExpect(failed.Length >= 1 && (await System.IO.File.ReadAllTextAsync(failed.OrderBy(f => f).Last())).Contains("Not scored: generation failed"), "the report of a failed generation doesn't say so");
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitAsync(0.1);
        }
        finally
        {
            AtlasEngine.State.LastCheck = checkBefore;
            Dialog()?.EmitSignal(AcceptDialog.SignalName.Confirmed);
            AP_Atlas.Core.EngineSetup.SoloTestRunner.StopAll();
            foreach (var solo in _profiles.Where(p => p.Name.StartsWith("Solo test:", StringComparison.Ordinal)).ToList()) DeleteProfile(solo);
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            AP_Atlas.Core.SafeFile.Delete(zip);
            AP_Atlas.Core.SafeFile.Delete(apworld);
            SetDeveloperModeForTests(developerBefore);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
            if (System.IO.Directory.Exists(reports)) foreach (string file in System.IO.Directory.GetFiles(reports, "Test_Game-*")) System.IO.File.Delete(file);
        }
    }

    private async Task ReleaseFactsAsync()
    {
        var engine = StartFakeEngine(UiTestPython());
        engine.Apply();
        var checkBefore = AtlasEngine.State.LastCheck;
        AtlasEngine.State.LastCheck = new EngineCheckResult { Games = new List<string> { "Test Game" }, TrackerLoads = true };
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var page = _gamesPage ?? throw new InvalidOperationException("The Games page wasn't built.");
        string worlds = AtlasEngine.UserWorldsDir;
        System.IO.Directory.CreateDirectory(worlds);
        string installedFile = System.IO.Path.Combine(worlds, "test_game.apworld");
        string packZip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_release_pack.zip");
        string data = DataManager.GetDataDirectory();
        // Four releases, newest first: 3.0.0 needs an Archipelago newer than the engine's; the 2.1.0 pre-release's guide names
        // the map tracker and its checks are the pack's; 2.0.0 and 1.0.0 (installed) have no guide and half the pack's checks.
        static byte[] Apworld(string version, string? tracker, string? minimumAp, params string[] locations)
        {
            using var memory = new System.IO.MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                void Text(string name, string text)
                {
                    using var writer = new System.IO.StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(text);
                }
                Text("test_game/archipelago.json", "{\"game\": \"Test Game\", \"world_version\": \"" + version + "\"" + (minimumAp != null ? ", \"minimum_ap_version\": \"" + minimumAp + "\"" : "") + "}");
                if (tracker != null) Text("test_game/docs/setup_en.md", "# Setup\n\n- [Map tracker](https://github.com/" + tracker + ")\n");
                var names = new JObject();
                long id = 2000;
                foreach (string location in locations) names[location] = id++;
                Text("test_game/fake_world.json", new JObject { ["game"] = "Test Game", ["data_checksum"] = "data-" + version, ["world_version"] = version, ["locations"] = names }.ToString());
            }
            return memory.ToArray();
        }
        var releases = new (string Tag, bool Pre, byte[] Bytes)[]
        {
            ("3.0.0", false, Apworld("3.0.0", "packs/test-game-poptracker", "9.9.9", "Cave Chest", "Far Chest")),
            ("v2.1.0-beta", true, Apworld("2.1.0", "packs/test-game-poptracker", null, "Cave Chest", "Far Chest")),
            ("2.0.0", false, Apworld("2.0.0", null, null, "Cave Chest")),
            ("1.0.0", false, Apworld("1.0.0", null, null, "Cave Chest"))
        };
        static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        await using var github = new AP_Atlas.Core.Testing.FakeWebSite();
        github.Answer = (path, _) =>
        {
            static byte[] Json(string s) => System.Text.Encoding.UTF8.GetBytes(s);
            if (path.StartsWith("/repos/owner/test-game/releases", StringComparison.Ordinal))
                return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, Json("[" + string.Join(",", releases.Select(r =>
                    $"{{\"tag_name\":\"{r.Tag}\",\"draft\":false,\"prerelease\":{(r.Pre ? "true" : "false")},\"published_at\":\"2026-09-01T00:00:00Z\",\"assets\":[{{\"name\":\"test_game.apworld\",\"browser_download_url\":\"{github.Site}/dl/{r.Tag}/test_game.apworld\",\"digest\":\"sha256:{Sha(r.Bytes)}\",\"size\":{r.Bytes.Length}}}]}}")) + "]"), "application/json");
            if (path.StartsWith("/search/repositories", StringComparison.Ordinal)) return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, Json("{\"items\":[]}"), "application/json");
            foreach (var r in releases)
                if (path == $"/dl/{r.Tag}/test_game.apworld") return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, r.Bytes, "application/octet-stream");
            return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(404, Array.Empty<byte>());
        };
        await System.IO.File.WriteAllBytesAsync(installedFile, releases[3].Bytes);
        FakeMapPack.Write(packZip, "UI test release pack", "Test Game");
        AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(packZip);
        AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
        int Downloads() => github.Requests.Count(r => r.StartsWith("/dl/", StringComparison.Ordinal));
        ConfirmationDialog? Question() => page.GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == AP_Atlas.Core.Permissions.ReleaseLook.Title && !d.IsQueuedForDeletion());
        try
        {
            AP_Atlas.Core.GitHubApi.TestSite = github.Site;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null, true);
            AP_Atlas.Core.EngineSetup.ApworldSources.AddUserRepo(_appSettings, "Test Game", "owner/test-game");
            _appSettings.ApprovedApworldSources.Remove("github.com/owner/test-game"); // adding a project trusts it; here the question's answer does
            host.ShowTool(AP_Atlas.UI.Tool.Games);
            page.Refresh();
            page.Select("Test Game");
            await UiTestWaitForAsync(() => page.VersionRows().Count == 4 ? page : null, "the game's four releases", 20);
            UiTestExpect(Downloads() == 0 && page.VersionFacts.All(f => f.Works.Length == 0 && !f.Greyed), "a release was looked inside before the press");
            // The card: ready (its logic and its pack are here), offering the newest full release, which nothing has ruled out yet.
            UiTestExpect(page.GameCardShown.Standing == AP_Atlas.Core.Games.GameStanding.Ready && page.GameCardShown.Line.Contains("Logic: 1.0.0 from github.com/owner/test-game") && page.UpdateOffered == "3.0.0",
                $"before the look the card shows {page.GameCardShown} with Update to {page.UpdateOffered}");
            // The installed version's card before the look: the pack isn't measured against it yet.
            UiTestExpect(page.CardFactsShown.Count == 4 && page.CardFactsShown[0].Good == null && page.CardFactsShown[0].Text.Contains("isn't measured against it yet"),
                $"the card before the look says: {string.Join(" | ", page.CardFactsShown.Select(f => f.Text))}");
            // The press asks the look-inside permission once (for every game), naming this time's releases, their size and project.
            var worldsBefore = System.IO.Directory.GetFiles(worlds, "*.apworld").Select(System.IO.Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            page.LookInsideForTests("Test Game");
            var question = await UiTestWaitForAsync(Question, "the look-inside question");
            string asked = string.Join(" ", question.FindChildren("*", nameof(Label), true, false).OfType<Label>().Select(l => l.Text));
            UiTestExpect(asked.Contains("4 release(s) of Test Game") && asked.Contains("about 0.") && asked.Contains("github.com/owner/test-game") && asked.Contains("Nothing is installed"),
                $"the question doesn't say what it downloads and from where: {asked}");
            question.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitForAsync(() => page.StatusText.StartsWith("Looked inside 4 release(s)", StringComparison.Ordinal) ? page : null, "the look to end", 60);
            var loads = engine.ComponentRequests("AtlasChecksum");
            UiTestExpect(Downloads() == 4 && loads.Count == 4 && loads.All(l => l["names"]?.Value<bool>() == true),
                $"the look didn't download and load each release once: {Downloads()} downloads, {loads.Count} loads");
            UiTestExpect(AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.ReleaseLook) && AP_Atlas.Core.EngineSetup.ApworldSources.IsRepoApproved(_appSettings, "owner/test-game"), "the permission wasn't kept, or the project not trusted");
            var inEngine = System.IO.Directory.GetFiles(worlds, "*.apworld").Select(System.IO.Path.GetFileName).ToList();
            UiTestExpect(inEngine.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).SequenceEqual(worldsBefore) && Sha(await System.IO.File.ReadAllBytesAsync(installedFile)) == Sha(releases[3].Bytes),
                $"something was installed: the engine's worlds hold {string.Join(", ", inEngine)}");
            var kept = System.IO.Directory.GetFiles(AP_Atlas.Core.Games.GameFiles.ApworldsFolder(data, "Test Game"), "*.apworld", System.IO.SearchOption.AllDirectories);
            UiTestExpect(kept.Length >= 4 && System.IO.File.Exists(AP_Atlas.Core.Games.ReleaseFactsStore.PathFor(data, "Test Game")),
                $"the releases weren't kept in the game's folder with their facts: {kept.Length} kept");
            UiTestExpect(page.StatusText.Contains("Atlas's pick: 2.1.0-beta") && page.StatusText.Contains("your map pack fits it (a pre-release): 100%"), $"the look's line says: {page.StatusText}");
            // The table: what works here, and what doesn't greyed with the reason.
            await UiTestWaitForAsync(() => page.VersionFacts.Count == 4 && page.VersionFacts.Any(f => f.Works.Length > 0) ? page : null, "the table's facts");
            var facts = page.VersionFacts;
            UiTestExpect(facts.Select(f => f.Version).SequenceEqual(new[] { "3.0.0", "2.1.0-beta", "2.0.0", "1.0.0" }), $"the rows are {string.Join(", ", facts.Select(f => f.Version))}");
            UiTestExpect(facts[0].Greyed && facts[0].Works == "It needs Archipelago 9.9.9 or newer; the engine has 0.6.7", $"3.0.0 says: {facts[0].Works}");
            UiTestExpect(!facts[1].Greyed && facts[1].Works.StartsWith("Atlas's pick", StringComparison.Ordinal) && facts[1].Works.Contains("map pack 100%") && facts[1].Works.Contains("its guide names a tracker"), $"2.1.0-beta says: {facts[1].Works}");
            UiTestExpect(facts[2].Greyed && facts[3].Greyed && facts[2].Works == "Your map pack places 50% of its pins with it", $"2.0.0 says: {facts[2].Works}; 1.0.0: {facts[3].Works}");
            // The installed version's card: ✗ for the pack, – for the guide it lacks, ✓ for the Archipelago it runs on; Switch to the pick.
            var card = page.CardFactsShown;
            UiTestExpect(card.Count == 4 && card[0].Good == false && card[0].Text.Contains("doesn't fit it well: 50%") && card[1].Good == null && card[1].Text == "It bundles no setup guide."
                && card[2].Good == true && card[2].Text.Contains("Archipelago 0.6.7") && card[3].Good == null,
                $"the card says: {string.Join(" | ", card.Select(f => (f.Good == true ? "✓ " : f.Good == false ? "✗ " : "– ") + f.Text))}");
            var switchButton = page.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.HasMeta("switch_button"));
            UiTestExpect(switchButton != null && switchButton.GetMeta("switch_button").AsString() == "2.1.0-beta" && switchButton.TooltipText.Contains("your map pack fits it"),
                $"the card doesn't offer to switch to the pick: {switchButton?.Text}");
            // The look ruled out every newer full release (3.0.0 needs another Archipelago; the pack doesn't fit 2.0.0 while it fits the pre-release): no Update.
            UiTestExpect(page.UpdateOffered == null, $"the card still offers Update to {page.UpdateOffered}");
            // Asked again: no question, nothing downloaded again.
            page.LookInsideForTests("Test Game");
            await UiTestWaitAsync(0.3);
            UiTestExpect(Question() == null, "the question was asked a second time for the game");
            await UiTestWaitForAsync(() => engine.ComponentRequests("AtlasChecksum").Count == 8 && page.StatusText.StartsWith("Looked inside 4 release(s)", StringComparison.Ordinal) ? page : null, "the second look to end", 60);
            UiTestExpect(Downloads() == 4, $"the second look downloaded again ({Downloads()} downloads)");
            // A solo test's install with the game's releases not read yet this session: reading them, looking inside and
            // installing follow one another (the beta's Ship of Harkinian test stopped at once: the look was refused while
            // the reading's run was still marked busy).
            page.ForgetVersionsForTests("Test Game");
            var chained = page.InstallBestAsync("Test Game", null, look: true);
            await UiTestWaitForAsync(() => chained.IsCompleted ? page : null, "the install after reading the releases", 60);
            // (The fake engine's health check doesn't pass here, so the install itself reports a failure: what matters is that
            // it ran, with Atlas's pick.)
            UiTestExpect(page.StatusText.Contains("Test Game 2.1.0-beta") && Sha(await System.IO.File.ReadAllBytesAsync(installedFile)) == Sha(releases[1].Bytes),
                $"the install after reading the releases didn't run with the pick; the page says: {page.StatusText}");
        }
        finally
        {
            Question()?.EmitSignal(AcceptDialog.SignalName.Canceled);
            AtlasEngine.State.LastCheck = checkBefore;
            AP_Atlas.Core.GitHubApi.TestSite = null;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null, false);
            _appSettings.ExtraApworldRepos.Remove("Test Game");
            _appSettings.ApprovedApworldSources.Remove("github.com/owner/test-game");
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            AP_Atlas.Core.SafeFile.Delete(installedFile);
            AP_Atlas.Core.SafeFile.Delete(packZip);
            AP_Atlas.Core.SafeFile.Delete(AP_Atlas.Core.Games.ReleaseFactsStore.PathFor(data, "Test Game"));
            string kept = System.IO.Path.Combine(AP_Atlas.Core.Games.GameFiles.ApworldsFolder(data, "Test Game"), "127.0.0.1");
            if (System.IO.Directory.Exists(kept)) System.IO.Directory.Delete(kept, true);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
        }
    }

    private async Task AddGamesPlanAsync()
    {
        // Atlas's game list for the test: a community game with two releases and two packs (the better fit made for another version), every size recorded.
        const string game = "Plan Test Game";
        string list = """{"format":"atlas-apworld-sources/1","built":"9999-12-31","source":"ui-test","games":[{"game":"Plan Test Game","apworld":"plan_test","repo":"planner/plan-test","versions":[{"version":"1.0.0","url":"https://github.com/planner/plan-test/releases/download/v1.0.0/plan_test.apworld","sha256":"aa","size":1000},{"version":"1.2.0","url":"https://github.com/planner/plan-test/releases/download/v1.2.0/plan_test.apworld","sha256":"bb","size":3000}],"packs":[{"repo":"mapper/noted","release":"v2","asset":"noted.zip","sha256":"cc","size":9000,"fit":99,"notes":["other_version"]},{"repo":"mapper/plain","release":"v1","asset":"plain.zip","sha256":"dd","size":5000,"fit":80}]}]}""";
        await using var site = new AP_Atlas.Core.Testing.FakeWebSite();
        site.Respond = path => path == "/list.json" ? (200, list) : (404, "");
        // GitHub is the fake site too: nothing a test does may reach the real one.
        AP_Atlas.Core.GitHubApi.TestSite = site.Site;
        AP_Atlas.Core.GitHubApi.ResetForTests();
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        try
        {
            string said = await ApworldSources.RefreshAsync(site.Site + "/list.json", System.Threading.CancellationToken.None);
            UiTestExpect(ApworldSources.Find(game)?.Versions.Sum(v => v.Size) == 4000 && ApworldSources.PacksFor(game).Sum(p => p.Size) == 14000, "the game list's sizes weren't read: " + said);
            host.ShowTool(AP_Atlas.UI.Tool.Games);
            _gamesPage!.Refresh();
            var plan = _gamesPage.PlanAdding(new[] { game });
            var row = plan.Games.Single();
            UiTestExpect(row.Items.Single(i => i.Kind == AP_Atlas.Core.Games.AddItemKind.Apworld).Version == "1.2.0"
                && row.Items.Single(i => i.Kind == AP_Atlas.Core.Games.AddItemKind.Pack).Repo == "mapper/plain",
                "the plan didn't take the newest release and the pack that doesn't wait behind: " + string.Join(", ", row.Items.Select(i => i.Version ?? i.Repo)));
            UiTestExpect(plan.DownloadBytes == 8000 && plan.DiskBytes == 11000 && plan.UnknownSizes == 0 && plan.GitHubRequests == 0
                && plan.ProjectsToTrust.SequenceEqual(new[] { "github.com/planner/plan-test" }) && plan.PackProjects.SequenceEqual(new[] { "github.com/mapper/plain" }),
                $"the plan's totals are wrong: {plan.DownloadBytes} down, {plan.DiskBytes} on disk, {plan.UnknownSizes} unknown");
            UiTestExpect(AP_Atlas.Core.Games.AddGamesPlan.Words(row) == "logic + map" && row.RepickPackAfterLogic, "the game's row doesn't say what adding it gets");
            UiTestExpect(site.Requests.SequenceEqual(new[] { "/list.json" }), "planning asked GitHub: " + string.Join(", ", site.Requests));
            // The list's versions carry their sizes into what Atlas reads of a project (here: GitHub lists no release).
            var versions = await ApworldSources.VersionsAsync(ApworldSources.Find(game), System.Threading.CancellationToken.None);
            UiTestExpect(versions.Single(v => v.Version == "1.2.0").Size == 3000, "a listed version lost its size");
        }
        finally
        {
            ApworldSources.ForgetRefreshed();
            AP_Atlas.Core.GitHubApi.TestSite = null;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
        }
    }

    private async Task AddGamesInstallerAsync()
    {
        // The fake engine's health check reads the engine's worlds folder; its world's data is Alpha's (the room's below).
        const string AlphaChecksum = "add0add0add0add0add0add0add0add0add0add0";
        var engine = StartFakeEngine(UiTestPython(), fake =>
        {
            fake.CheckWorldsDir = AtlasEngine.UserWorldsDir;
            fake.DataChecksum = AlphaChecksum;
        });
        const string alpha = "Add Alpha Game", beta = "Add Beta Game", gamma = "Add Gamma Game";
        static byte[] Zip(params (string Name, string Text)[] files)
        {
            using var memory = new System.IO.MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, text) in files)
                {
                    using var writer = new System.IO.StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(text);
                }
            return memory.ToArray();
        }
        static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        static bool Is(AP_Atlas.Core.Games.AddStep step, AP_Atlas.Core.Games.AddStepOutcome outcome) => step.Outcome == outcome;
        string data = DataManager.GetDataDirectory();
        string scratch = System.IO.Path.Combine(data, "uitest_add_games");
        System.IO.Directory.CreateDirectory(scratch);
        // A community game's apworld; Archipelago's pretend release with an official game's world whose setup guide names its tracker.
        byte[] alphaApworld = Zip(("add_alpha/archipelago.json", "{\"game\": \"" + alpha + "\", \"world_version\": \"1.2.0\"}"), ("add_alpha/__init__.py", "# " + alpha + "\n"));
        byte[] release = Zip(("FakeArchipelago/Utils.py", "# core\n"), ("FakeArchipelago/worlds/add_beta/__init__.py", "# " + beta + "\n"),
            ("FakeArchipelago/worlds/add_beta/archipelago.json", "{\"game\": \"" + beta + "\"}"),
            ("FakeArchipelago/worlds/add_beta/docs/setup_en.md", "# Setup\n\n- [Map tracker](https://github.com/packs/beta-poptracker)\n"),
            ("FakeArchipelago/worlds/add_beta/docs/en_" + beta + ".md", "# " + beta + "\n"));
        // Alpha's pins are near matches of its names (as the Pack Doctor scenario's), so the Doctor has something to say.
        string alphaZip = System.IO.Path.Combine(scratch, "alpha.zip"), betaZip = System.IO.Path.Combine(scratch, "beta.zip"), otherZip = System.IO.Path.Combine(scratch, "other.zip");
        FakeMapPack.Write(alphaZip, "UI test alpha pack", alpha,
            locationsJson: """[{"name":"Cave","sections":[{"name":"Chest"}],"map_locations":[{"map":"World","x":10,"y":10}]},""" +
                           """{"name":"Far","sections":[{"name":"Lantern"}],"map_locations":[{"map":"World","x":500,"y":10}]}]""");
        FakeMapPack.Write(betaZip, "UI test beta pack", beta);
        FakeMapPack.Write(otherZip, "UI test other beta pack", beta);
        byte[] alphaPack = await System.IO.File.ReadAllBytesAsync(alphaZip), betaPack = await System.IO.File.ReadAllBytesAsync(betaZip), otherPack = await System.IO.File.ReadAllBytesAsync(otherZip);
        static async Task<string?> ShaOfAsync(string path) => System.IO.File.Exists(path) ? Sha(await System.IO.File.ReadAllBytesAsync(path)) : null;
        await using var site = new FakeWebSite();
        site.Answer = (path, _) => path switch
        {
            "/dl/add_alpha.apworld" => new FakeWebSite.FullAnswer(200, alphaApworld, "application/octet-stream"),
            "/dl/archipelago.zip" => new FakeWebSite.FullAnswer(200, release, "application/zip"),
            "/gh/packs/alpha-poptracker/releases/download/v1.0/alpha_pack.zip" => new FakeWebSite.FullAnswer(200, alphaPack, "application/zip"),
            "/gh/packs/beta-poptracker/releases/download/v1/beta_pack.zip" => new FakeWebSite.FullAnswer(200, betaPack, "application/zip"),
            "/gh/packs/beta-other/releases/download/v2/beta_other.zip" => new FakeWebSite.FullAnswer(200, otherPack, "application/zip"),
            _ => new FakeWebSite.FullAnswer(404, Array.Empty<byte>())
        };
        var list = new ApworldSourceList
        {
            Format = "atlas-apworld-sources/1",
            Built = "9999-12-31",
            Source = "ui-test",
            Games = new List<ApworldSource>
            {
                new ApworldSource { Game = alpha, Apworld = "add_alpha", Repo = "owner/add-alpha",
                    Versions = { new ApworldVersion { Version = "1.2.0", Url = site.Site + "/dl/add_alpha.apworld", Sha256 = Sha(alphaApworld), Size = alphaApworld.Length },
                                 new ApworldVersion { Version = "1.3.0", Url = site.Site + "/dl/add_alpha_newer.apworld", Sha256 = new string('0', 64), Size = alphaApworld.Length } },
                    // The pack lab measured Alpha's pack on 1.2.0: that version is installed, not the newest full release (round 13).
                    Packs = { new IndexPack { Repo = "packs/alpha-poptracker", Release = "v1.0", Asset = "alpha_pack.zip", Sha256 = Sha(alphaPack), Fit = 100, Checks = "2 of 2", ApworldVersion = "1.2.0" } } },
                new ApworldSource { Game = beta, Official = true,
                    Packs = { new IndexPack { Repo = "packs/beta-other", Release = "v2", Asset = "beta_other.zip", Sha256 = Sha(otherPack), Fit = 90 },
                              new IndexPack { Repo = "packs/beta-poptracker", Release = "v1", Asset = "beta_pack.zip", Sha256 = Sha(betaPack), Fit = 60 } } },
                new ApworldSource { Game = gamma, Apworld = "add_gamma", Repo = "owner/add-gamma",
                    Versions = { new ApworldVersion { Version = "0.1.0", Url = site.Site + "/dl/add_gamma.apworld", Sha256 = new string('0', 64) } } }
            }
        };
        var index = WorldsIndex.Parse(new JObject
        {
            ["archipelago"] = AtlasEngine.ArchipelagoVersion,
            ["zip_sha256"] = Sha(release),
            ["zip_root"] = "FakeArchipelago/",
            ["zip_bytes"] = release.Length,
            ["core_worlds"] = new JArray("generic"),
            ["worlds"] = new JObject { ["add_beta"] = new JObject { ["games"] = new JArray(beta), ["needs"] = new JArray(), ["requirements"] = false, ["bytes"] = 10 } }
        }.ToString());
        string worlds = AtlasEngine.UserWorldsDir, packs = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory();
        string installedApworld = System.IO.Path.Combine(worlds, "add_alpha.apworld");
        var checkBefore = AtlasEngine.State.LastCheck;
        var parts = new List<IReadOnlyList<AtlasEngine.EngineChange>>();
        void OnParts(IReadOnlyList<AtlasEngine.EngineChange> changes) { lock (parts) parts.Add(changes); }
        int prompts = 0;
        void OnFixed(AP_Atlas.Core.PopTracker.PackDoctorService.AutoFixNote note) => prompts++;
        void OnReview(string key, string message) => prompts++;
        // Wren, a slot of Alpha connected before the batch: the room's names give the Doctor its near matches, and its pack
        // loads (a prompted check) while the batch runs.
        await using var server = new FakeArchipelagoServer();
        server.SlotName = "Wren";
        server.SlotGame = alpha;
        server.Games[alpha] = new FakeGame(AlphaChecksum,
            new Dictionary<string, long> { ["Sword"] = 1000, ["Shield"] = 1001, ["Rupee"] = 1002, ["Gem"] = 1099 },
            new Dictionary<string, long> { ["Cave Chest Room"] = 2000, ["Far Lantern Box"] = 2001, ["Tower Top"] = 2002, ["Gem Vault"] = 2003 });
        var profile = new MultiworldProfile { Name = "UI test add games", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Wren");
        _profiles.Add(profile);
        int WrenInits() => engine.Requests("init").Count(r => (string?)r["player_name"] == "Wren");
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        string? lookKey = null;
        try
        {
            ApworldSources.UseListForTests(list);
            AtlasEngine.TestWorlds = (index, site.Site + "/dl/archipelago.zip", Sha(release));
            GameAdder.TestGitHubSite = site.Site + "/gh";
            // GitHub's API is the fake site too: nothing reaches the real one, and a listing read would show in its requests.
            AP_Atlas.Core.GitHubApi.TestSite = site.Site;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, false);
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null, false);
            foreach (string leftover in new[] { "alpha_pack.zip", "beta_pack.zip", "beta_other.zip" }) AP_Atlas.Core.SafeFile.Delete(System.IO.Path.Combine(packs, leftover));
            AP_Atlas.Core.SafeFile.Delete(AtlasEngine.WorldsZipPath);
            await OnConnectSlotPressedAsync("Wren", profile);
            var wren = await UiTestWaitForAsync(() => SlotView(profile.Id, "Wren"), "Wren's view");
            await UiTestWaitForAsync(() => wren.LogicSettled ? wren : null, "Wren's logic to start");
            // The packs folder read once: from now on the rescan checks a new pack, with a prompt unless its install was quiet.
            _packManagerPanel.Rescan();
            await UiTestWaitForAsync(() => !_packManagerPanel.Scanning ? this : null, "the packs folder to be read");
            var newestCard = _alertLog.Entries.FirstOrDefault();
            // The plan, as the Games page collects it: Alpha's newest release and best pack, Beta's world from Archipelago's
            // release (its guide can't be read yet, so its pack is picked again once the world is in), the fake site to trust.
            host.ShowTool(AP_Atlas.UI.Tool.Games);
            _gamesPage!.Refresh();
            var plan = _gamesPage.PlanAdding(new[] { alpha, beta, gamma });
            UiTestExpect(plan.NeedsArchipelagoRelease && plan.ArchipelagoReleaseBytes == release.Length
                && plan.GitHubRequests == AP_Atlas.Core.Games.AddGamesPlan.LivePackRequests && plan.Games.Single(g => g.Game == gamma).GuideLookupReserved
                && plan.ProjectsToTrust.SequenceEqual(new[] { "127.0.0.1" })
                && plan.Games.Single(g => g.Game == beta).Items.Single(i => i.Kind == AP_Atlas.Core.Games.AddItemKind.Pack).Repo == "packs/beta-other"
                && plan.Games.Single(g => g.Game == beta).RepickPackAfterLogic
                && plan.Games.Single(g => g.Game == alpha).Items.Single(i => i.Kind == AP_Atlas.Core.Games.AddItemKind.Apworld) is { Version: "1.2.0", FitsPack: true },
                "the plan isn't the list's best fit for Beta with Archipelago's release once and the fake site to trust, or Alpha's version isn't the one its pack was measured on");
            AtlasEngine.PartsChanged += OnParts;
            AP_Atlas.Core.PopTracker.PackDoctorService.AutoFixed += OnFixed;
            AP_Atlas.Core.PopTracker.PackDoctorService.ReviewSuggested += OnReview;
            int checksBefore = engine.ComponentRequests("AtlasCheck").Count, initsBefore = WrenInits();
            var statuses = new List<AP_Atlas.Core.Games.AddGamesStatus>();
            var running = AddGamesAsync(plan, new AddGamesConsent(false, false), statuses.Add, System.Threading.CancellationToken.None);
            // One batch at a time: a second press while it runs changes nothing and says why.
            var refused = await AddGamesAsync(plan, new AddGamesConsent(false, false), null, System.Threading.CancellationToken.None);
            UiTestExpect(refused.Outcome.Games.All(g => Is(g.Logic, AP_Atlas.Core.Games.AddStepOutcome.Failed) && (g.Logic.Detail ?? "").Contains("another Install games")),
                "a second batch ran while the first was running");
            var run = await running.WaitAsync(TimeSpan.FromSeconds(120));
            var o = run.Outcome;
            UiTestExpect(engine.ComponentRequests("AtlasCheck").Count - checksBefore == 1, $"{engine.ComponentRequests("AtlasCheck").Count - checksBefore} engine health checks ran instead of one for the batch");
            lock (parts) UiTestExpect(parts.Count == 1 && parts[0].Any(p => p.Kind == "apworld" && p.Game == alpha) && parts[0].Any(p => p.Kind == "world" && p.Game == beta),
                $"the slots weren't told the engine's changed parts once: {parts.Count} notice(s)");
            UiTestExpect(await ShaOfAsync(installedApworld) == Sha(alphaApworld)
                && System.IO.File.Exists(System.IO.Path.Combine(worlds, "add_beta", "__init__.py"))
                && await ShaOfAsync(System.IO.Path.Combine(packs, "alpha_pack.zip")) == Sha(alphaPack)
                && await ShaOfAsync(System.IO.Path.Combine(packs, "beta_pack.zip")) == Sha(betaPack)
                && !System.IO.File.Exists(System.IO.Path.Combine(packs, "beta_other.zip")),
                "the apworld, the official world or the packs aren't in place");
            UiTestExpect(_appSettings.PreferredPacks.TryGetValue(alpha, out var alphaPreferred) && alphaPreferred == "alpha_pack.zip"
                && _appSettings.PreferredPacks.TryGetValue(beta, out var betaPreferred) && betaPreferred == "beta_pack.zip", "the installed packs weren't made their games' packs");
            var asked = site.Requests;
            UiTestExpect(asked.Count(r => r == "/dl/add_alpha.apworld") == 1 && asked.Count(r => r == "/dl/archipelago.zip") == 1
                && asked.Count(r => r == "/gh/packs/alpha-poptracker/releases/download/v1.0/alpha_pack.zip") == 1
                && asked.Count(r => r == "/gh/packs/beta-poptracker/releases/download/v1/beta_pack.zip") == 1 && !asked.Any(r => r.Contains("beta_other")),
                $"the downloads weren't each file once by its listed address: {string.Join(", ", asked)}");
            UiTestExpect(!asked.Any(r => r.StartsWith("/repos/", StringComparison.Ordinal) || r.StartsWith("/search/", StringComparison.Ordinal)) && o.GitHubRequests == 0,
                $"the batch asked GitHub's API: {string.Join(", ", asked)}");
            var cards = _alertLog.Entries.TakeWhile(e => !ReferenceEquals(e, newestCard)).Select(e => e.Message).ToList();
            UiTestExpect(prompts == 0 && !cards.Any(m => m.Contains("Installed UI test") || m.Contains("Atlas fixed") || m.Contains("Pack Doctor")) && AP_Atlas.UI.FixPreviewDialog.Showing == null,
                $"a pack's card or a Pack Doctor prompt came during the batch: {prompts} prompt(s); {string.Join(" | ", cards)}");
            // The result rows.
            var rowA = o.For(alpha);
            var rowB = o.For(beta);
            var rowC = o.For(gamma);
            UiTestExpect(!o.Cancelled && Is(rowA.Logic, AP_Atlas.Core.Games.AddStepOutcome.Done) && rowA.Logic.Detail == "1.2.0" && Is(rowA.Map, AP_Atlas.Core.Games.AddStepOutcome.Done)
                && !rowA.PackFromGuide && rowA.PackRepo == "packs/alpha-poptracker"
                && rowA.LogicFitsPack && !rowA.LogicShips
                && Is(rowB.Logic, AP_Atlas.Core.Games.AddStepOutcome.Done) && rowB.Logic.Detail == AtlasEngine.ArchipelagoVersion && rowB.LogicShips
                && Is(rowB.Map, AP_Atlas.Core.Games.AddStepOutcome.Done) && rowB.PackFromGuide
                && rowB.PackRepo == "packs/beta-poptracker" && rowB.GuideRepo == "packs/beta-poptracker"
                && Is(rowC.Logic, AP_Atlas.Core.Games.AddStepOutcome.Failed) && rowC.Logic.Detail == "its project no longer offers that file" && Is(rowC.Map, AP_Atlas.Core.Games.AddStepOutcome.Unavailable)
                && o.Failures.Select(f => f.Game).SequenceEqual(new[] { gamma }),
                $"the result rows: {string.Join(" | ", o.Games.Select(g => $"{g.Game}: {g.Logic} / {g.Map}"))}");
            // What Atlas fixed by itself in Alpha's pack: its confident match, as Atlas's own fix, with no card (the summary says it).
            var look = o.PacksToLook.FirstOrDefault(p => p.Game == alpha);
            lookKey = look?.PackKey;
            var alphaFixes = look == null ? null : AP_Atlas.Core.PopTracker.PackFixes.Get(look.PackKey);
            UiTestExpect(look != null && look.Fixed >= 1 && alphaFixes!.Links.Any(l => l.Subject == "link:Cave|Chest" && l.ApLocationId == 2000 && l.Source == "automatic")
                && look.Fixed == alphaFixes.Links.Count(l => l.Source == "automatic") + alphaFixes.Tiles.Count(t => t.Source == "automatic")
                && look.Total > 0 && look.Placed >= 1 && look.Placed <= look.Total && o.PacksToLook.Any(p => p.Game == beta),
                $"Alpha's pack isn't listed with what Atlas fixed by itself and its pinned checks: {look?.Fixed} fixed, {look?.Placed}/{look?.Total} pinned; {o.PacksToLook.Count} pack(s)");
            UiTestExpect(ApworldSources.IsApproved(_appSettings, site.Site + "/dl/add_alpha.apworld") && AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.EngineSetup)
                && !AP_Atlas.Core.Permissions.IsAlwaysAllowed(_appSettings, AP_Atlas.Core.Permissions.EngineSetup),
                "the project wasn't trusted, or the engine's downloads weren't allowed for this session only");
            var fractions = statuses.Select(s => s.Fraction).ToList();
            UiTestExpect(fractions.Zip(fractions.Skip(1), (x, y) => y >= x).All(up => up) && statuses[^1].Fraction == 1f && statuses[^1].Text == "Done."
                && new[] { "Game 1 of 3: ", "Game 2 of 3: ", "Game 3 of 3: " }.All(p => statuses.Any(s => s.Text.StartsWith(p, StringComparison.Ordinal)))
                && statuses.Any(s => s.Text.EndsWith("map pack (1 of 2)…", StringComparison.Ordinal)) && statuses.Any(s => s.Text.EndsWith("map pack (2 of 2)…", StringComparison.Ordinal)),
                $"the progress: {string.Join(" | ", statuses.Select(s => $"{s.Fraction:0.00} {s.Text}"))}");
            UiTestExpect(AtlasEngine.LastCheck(AtlasEngine.Current)?.Games is { } loaded && loaded.Contains(alpha) && loaded.Contains(beta), "the engine's check doesn't list the added games");
            // Wren's logic restarted once for the whole batch (one notice of the changed parts), and only once.
            await UiTestWaitForAsync(() => wren.LogicSettled && WrenInits() == initsBefore + 1 ? wren : null, "Wren's logic to restart once", 30);
            await UiTestWaitAsync(1.0);
            UiTestExpect(WrenInits() == initsBefore + 1, $"Wren's logic started {WrenInits() - initsBefore} times for the batch, not once");
            // The control, under holds of the test's own: the pack's pass run again (its fixes reset, a new stamp) does raise its
            // card, so the batch's hold is what kept it back; a hold released for a pack its holder speaks of drops it, one released
            // for none raises it once.
            await UiTestWaitForAsync(() => !AP_Atlas.Core.PopTracker.PackDoctorService.IsChecking(look!.PackKey) && !AP_Atlas.Core.PopTracker.PackDoctorService.IsBusy(look.PackKey) ? this : null, "the Doctor to be idle");
            var alphaOriginal = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(System.IO.Path.Combine(packs, "alpha_pack.zip")) ?? throw new InvalidOperationException("Alpha's pack couldn't be read");
            async Task PassAgainAsync()
            {
                AP_Atlas.Core.PopTracker.PackFixes.Reset(look!.PackKey); // its fixes and what was declined gone, as on a first install
                await AP_Atlas.Core.PopTracker.PackDoctorService.WhenIdleAsync(look.PackKey, TimeSpan.FromSeconds(30));
                AP_Atlas.Core.PopTracker.PackFixes.SetAutoLinkedVersion(look.PackKey, "");
                await AP_Atlas.Core.PopTracker.PackDoctorService.CheckAsync(alphaOriginal, prompt: true);
                await AP_Atlas.Core.PopTracker.PackDoctorService.WhenIdleAsync(look.PackKey, TimeSpan.FromSeconds(30));
            }
            var answered = AP_Atlas.Core.PopTracker.PackDoctorService.HoldPrompts();
            await PassAgainAsync();
            answered.Release(new[] { look!.PackKey });
            UiTestExpect(prompts == 0, $"a held card for a pack its holder speaks of was raised: {prompts}");
            var held = AP_Atlas.Core.PopTracker.PackDoctorService.HoldPrompts();
            await PassAgainAsync();
            UiTestExpect(prompts == 0, $"a card came while held: {prompts}");
            held.Release(Array.Empty<string>());
            UiTestExpect(prompts == 1, $"the pass's card came {prompts} time(s) once its hold ended, not once");
            // A cancelled run downloads, checks, trusts and allows nothing: what the first run granted is taken back first.
            int requestsBefore = site.Requests.Count;
            _appSettings.ApprovedApworldSources.Remove("127.0.0.1");
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            using (var cts = new System.Threading.CancellationTokenSource())
            {
                await cts.CancelAsync();
                var stopped = await AddGamesAsync(plan, new AddGamesConsent(false, false), null, cts.Token);
                UiTestExpect(stopped.Outcome.Cancelled && stopped.Outcome.Games.All(g => g.Logic.Outcome is AP_Atlas.Core.Games.AddStepOutcome.Cancelled or AP_Atlas.Core.Games.AddStepOutcome.NotNeeded
                        && g.Map.Outcome is AP_Atlas.Core.Games.AddStepOutcome.Cancelled or AP_Atlas.Core.Games.AddStepOutcome.NotNeeded)
                    && site.Requests.Count == requestsBefore && engine.ComponentRequests("AtlasCheck").Count - checksBefore == 1
                    && !ApworldSources.IsApproved(_appSettings, site.Site + "/dl/add_alpha.apworld")
                    && !AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.EngineSetup),
                    "a cancelled run did something, or trusted the project or allowed the engine's downloads");
            }
            // Cancel pressed while the batch runs (as its first game's apworld is fetched): it stops there, the engine
            // unchanged (no check, no logic restart) and no partial download left behind.
            int initsNow = WrenInits();
            using (var cts = new System.Threading.CancellationTokenSource())
            {
                var midway = await AddGamesAsync(plan, new AddGamesConsent(false, false), status =>
                {
                    if (status.Text.StartsWith("Game 1 of 3: ", StringComparison.Ordinal)) cts.Cancel();
                }, cts.Token);
                await UiTestWaitAsync(0.5);
                string alphaFolder = AP_Atlas.Core.Games.GameFiles.ApworldsFolder(data, alpha);
                UiTestExpect(midway.Outcome.Cancelled && midway.Outcome.For(alpha).Logic.Outcome == AP_Atlas.Core.Games.AddStepOutcome.Cancelled
                    && midway.Outcome.For(beta).Logic.Outcome == AP_Atlas.Core.Games.AddStepOutcome.Cancelled
                    && engine.ComponentRequests("AtlasCheck").Count - checksBefore == 1 && WrenInits() == initsNow
                    && System.IO.Directory.GetFiles(packs, "*.part").Length == 0
                    && (!System.IO.Directory.Exists(alphaFolder) || System.IO.Directory.GetFiles(alphaFolder, "*.part", System.IO.SearchOption.AllDirectories).Length == 0),
                    $"a cancel during the batch didn't stop it there: {string.Join(" | ", midway.Outcome.Games.Select(g => $"{g.Game}: {g.Logic} / {g.Map}"))}");
            }
            // The tick "Don't ask again for Atlas's downloads": always allowed; an empty plan checks nothing.
            await AddGamesAsync(new AP_Atlas.Core.Games.AddPlan { Games = Array.Empty<AP_Atlas.Core.Games.GamePlan>() }, new AddGamesConsent(true, false), null, System.Threading.CancellationToken.None);
            UiTestExpect(AP_Atlas.Core.Permissions.IsAlwaysAllowed(_appSettings, AP_Atlas.Core.Permissions.EngineSetup) && engine.ComponentRequests("AtlasCheck").Count - checksBefore == 1,
                "the tick didn't keep the engine's downloads allowed, or an empty plan ran a check");
        }
        finally
        {
            AtlasEngine.PartsChanged -= OnParts;
            AP_Atlas.Core.PopTracker.PackDoctorService.AutoFixed -= OnFixed;
            AP_Atlas.Core.PopTracker.PackDoctorService.ReviewSuggested -= OnReview;
            ApworldSources.UseListForTests(null);
            AtlasEngine.TestWorlds = null;
            GameAdder.TestGitHubSite = null;
            AP_Atlas.Core.GitHubApi.TestSite = null;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, false);
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            // Restored in memory only (the state is saved by the engine's own steps): engine.json keeps the fake check, which
            // is the test data folder's.
            AtlasEngine.State.LastCheck = checkBefore;
            if (lookKey != null) AP_Atlas.Core.PopTracker.PackDoctorService.Reports.Remove(lookKey);
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            foreach (string source in new[] { "github.com/owner/add-alpha", "github.com/owner/add-gamma", "127.0.0.1" }) _appSettings.ApprovedApworldSources.Remove(source);
            _appSettings.PreferredPacks.Remove(alpha);
            _appSettings.PreferredPacks.Remove(beta);
            DataManager.SaveSettings(_appSettings);
            AP_Atlas.Core.SafeFile.Delete(installedApworld);
            if (System.IO.Directory.Exists(System.IO.Path.Combine(worlds, "add_beta"))) System.IO.Directory.Delete(System.IO.Path.Combine(worlds, "add_beta"), true);
            AP_Atlas.Core.SafeFile.Delete(System.IO.Path.Combine(worlds, ".atlas_worlds.json"));
            AP_Atlas.Core.SafeFile.Delete(AtlasEngine.WorldsZipPath);
            foreach (string file in new[] { "alpha_pack.zip", "beta_pack.zip", "beta_other.zip" }) AP_Atlas.Core.SafeFile.Delete(System.IO.Path.Combine(packs, file));
            foreach (string game in new[] { alpha, gamma })
            {
                string kept = AP_Atlas.Core.Games.GameFiles.ApworldsFolder(data, game);
                if (System.IO.Directory.Exists(kept)) System.IO.Directory.Delete(kept, true);
            }
            if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
        }
    }

    private async Task AddGamesGuidePackAsync()
    {
        StartFakeEngine(UiTestPython(), fake => fake.CheckWorldsDir = AtlasEngine.UserWorldsDir);
        const string delta = "Add Delta Game", echo = "Add Echo Game", foxtrot = "Add Foxtrot Game";
        static byte[] Zip(params (string Name, string Text)[] files)
        {
            using var memory = new System.IO.MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, text) in files)
                {
                    using var writer = new System.IO.StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(text);
                }
            return memory.ToArray();
        }
        static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        string data = DataManager.GetDataDirectory(), scratch = System.IO.Path.Combine(data, "uitest_guide_pack");
        System.IO.Directory.CreateDirectory(scratch);
        // Its apworld's guide names a tracker project Atlas's list doesn't have.
        byte[] apworld = Zip(("add_delta/archipelago.json", "{\"game\": \"" + delta + "\", \"world_version\": \"1.0.0\"}"), ("add_delta/__init__.py", "# " + delta + "\n"),
            ("add_delta/docs/setup_en.md", "# Setup\n\n- [Map tracker](https://github.com/packs/delta-tracker/releases/latest)\n"));
        string packZip = System.IO.Path.Combine(scratch, "delta_pack.zip");
        FakeMapPack.Write(packZip, "UI test delta pack", delta);
        byte[] pack = await System.IO.File.ReadAllBytesAsync(packZip);
        // Echo's guide names no tracker; Foxtrot's names one Atlas's list doesn't have.
        byte[] echoWorld = Zip(("add_echo/archipelago.json", "{\"game\": \"" + echo + "\", \"world_version\": \"1.0.0\"}"), ("add_echo/__init__.py", "# " + echo + "\n"),
            ("add_echo/docs/setup_en.md", "# Setup\n\nNothing to track here.\n"));
        byte[] foxWorld = Zip(("add_foxtrot/archipelago.json", "{\"game\": \"" + foxtrot + "\", \"world_version\": \"1.0.0\"}"), ("add_foxtrot/__init__.py", "# " + foxtrot + "\n"),
            ("add_foxtrot/docs/setup_en.md", "# Setup\n\n- [Map tracker](https://github.com/packs/foxtrot-tracker/releases/latest)\n"));
        string foxZip = System.IO.Path.Combine(scratch, "foxtrot_pack.zip");
        FakeMapPack.Write(foxZip, "UI test foxtrot pack", foxtrot);
        byte[] foxPack = await System.IO.File.ReadAllBytesAsync(foxZip);
        await using var site = new FakeWebSite();
        static byte[] Json(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        string release = $"{{\"tag_name\":\"v1\",\"name\":\"v1\",\"body\":\"\",\"assets\":[{{\"name\":\"delta_pack.zip\",\"browser_download_url\":\"{site.Site}/dl/delta_pack.zip\",\"size\":{pack.Length}}}]}}";
        // GitHub's answers as TryProjectAsync reads them: the release list, then the latest release.
        string foxRelease = $"{{\"tag_name\":\"v1\",\"name\":\"v1\",\"body\":\"\",\"assets\":[{{\"name\":\"foxtrot_pack.zip\",\"browser_download_url\":\"{site.Site}/dl/foxtrot_pack.zip\",\"size\":{foxPack.Length}}}]}}";
        site.Answer = (path, _) => path switch
        {
            "/dl/add_delta.apworld" => new FakeWebSite.FullAnswer(200, apworld, "application/octet-stream"),
            "/dl/delta_pack.zip" => new FakeWebSite.FullAnswer(200, pack, "application/zip"),
            "/dl/add_echo.apworld" => new FakeWebSite.FullAnswer(200, echoWorld, "application/octet-stream"),
            "/dl/add_foxtrot.apworld" => new FakeWebSite.FullAnswer(200, foxWorld, "application/octet-stream"),
            "/dl/foxtrot_pack.zip" => new FakeWebSite.FullAnswer(200, foxPack, "application/zip"),
            _ when path.StartsWith("/repos/packs/foxtrot-tracker/releases/latest", StringComparison.Ordinal) => new FakeWebSite.FullAnswer(200, Json(foxRelease), "application/json"),
            _ when path.StartsWith("/repos/packs/foxtrot-tracker/releases", StringComparison.Ordinal) => new FakeWebSite.FullAnswer(200, Json("[" + foxRelease + "]"), "application/json"),
            _ when path.StartsWith("/repos/packs/delta-tracker/releases/latest", StringComparison.Ordinal) => new FakeWebSite.FullAnswer(200, Json(release), "application/json"),
            _ when path.StartsWith("/repos/packs/delta-tracker/releases", StringComparison.Ordinal) => new FakeWebSite.FullAnswer(200, Json("[" + release + "]"), "application/json"),
            _ => new FakeWebSite.FullAnswer(404, Array.Empty<byte>())
        };
        var list = new ApworldSourceList
        {
            Format = "atlas-apworld-sources/1",
            Built = "9999-12-31",
            Source = "ui-test",
            Games = new List<ApworldSource>
            {
                // The list has the guide's project only as a copy no download fetches (round 13: Ship of Harkinian): read from GitHub all the same.
                new ApworldSource { Game = delta, Apworld = "add_delta", Repo = "owner/add-delta",
                    Versions = { new ApworldVersion { Version = "1.0.0", Url = site.Site + "/dl/add_delta.apworld", Sha256 = Sha(apworld), Size = apworld.Length } },
                    Packs = { new IndexPack { Repo = "packs/delta-tracker", Release = "v1", Asset = "(from the beta's packs folder)", Sha256 = Sha(pack), Fit = 100, Checks = "1 of 1", Size = pack.Length } } },
                new ApworldSource { Game = echo, Apworld = "add_echo", Repo = "owner/add-echo",
                    Versions = { new ApworldVersion { Version = "1.0.0", Url = site.Site + "/dl/add_echo.apworld", Sha256 = Sha(echoWorld), Size = echoWorld.Length } } },
                // Foxtrot's list has a pack measured on its 1.0.0 (the plan installs that version for it), but once its logic is in,
                // its guide's project is what's installed: the summary mustn't claim the version is the one its pack was measured on.
                new ApworldSource { Game = foxtrot, Apworld = "add_foxtrot", Repo = "owner/add-foxtrot",
                    Versions = { new ApworldVersion { Version = "1.0.0", Url = site.Site + "/dl/add_foxtrot.apworld", Sha256 = Sha(foxWorld), Size = foxWorld.Length } },
                    Packs = { new IndexPack { Repo = "packs/foxtrot-listed", Release = "v1", Asset = "foxtrot_listed.zip", Sha256 = new string('f', 64), Fit = 100, Checks = "1 of 1", Size = foxPack.Length, ApworldVersion = "1.0.0" } } }
            }
        };
        string worlds = AtlasEngine.UserWorldsDir;
        var checkBefore = AtlasEngine.State.LastCheck;
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        AP_Atlas.Core.Games.AddGameResult? row = null, foxRow = null;
        string? deltaKey = null, foxKey = null;
        try
        {
            ApworldSources.UseListForTests(list);
            AP_Atlas.Core.GitHubApi.TestSite = site.Site;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null, false);
            host.ShowTool(AP_Atlas.UI.Tool.Games);
            _gamesPage!.Refresh();
            var plan = _gamesPage.PlanAdding(new[] { delta });
            var g = plan.Games.Single();
            UiTestExpect(g.GuideLookupReserved && g.Row == AP_Atlas.Core.Games.AddRow.LogicMapFromGuide && plan.NeedsGitHubLookups && plan.GitHubRequests == AP_Atlas.Core.Games.AddGamesPlan.LivePackRequests
                && AP_Atlas.Core.Games.AddGamesWords.Confirm(plan, false, null, 0, t => t) is { GitHub: not null } w && w.ProjectsTooltip.Contains(delta),
                $"the plan doesn't keep the guide's requests or the confirm doesn't say so: {g.Row}, {plan.GitHubRequests} request(s)");
            // Run as if the rest of GitHub's budget were spent elsewhere: only the requests the plan kept pay for the guide's project.
            var run = await AddGamesAsync(plan with { GitHubRequests = AP_Atlas.Core.Games.AddGamesPlan.GitHubBudget }, new AddGamesConsent(false, true), null, System.Threading.CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(120));
            row = run.Outcome.For(delta);
            if (row.PackFile != null && AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(row.PackFile) is { } installed) deltaKey = AP_Atlas.Core.PopTracker.PackFixes.KeyFor(installed);
            UiTestExpect(row.Logic.Outcome == AP_Atlas.Core.Games.AddStepOutcome.Done && row.Map.Outcome == AP_Atlas.Core.Games.AddStepOutcome.Done
                && row.PackFromGuide && row.PackRepo == "packs/delta-tracker" && row.PackFile != null && System.IO.File.Exists(row.PackFile)
                && _appSettings.PreferredPacks.TryGetValue(delta, out var preferred) && preferred == System.IO.Path.GetFileName(row.PackFile),
                $"the guide's pack wasn't installed and made the game's: {row.Logic} / {row.Map} ({row.PackRepo})");
            UiTestExpect(run.Outcome.GitHubRequests <= AP_Atlas.Core.Games.AddGamesPlan.LivePackRequests && site.Requests.Any(r => r.StartsWith("/repos/packs/delta-tracker/releases", StringComparison.Ordinal))
                && AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups) && !AP_Atlas.Core.Permissions.IsAlwaysAllowed(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups),
                $"GitHub was asked more than kept, or the lookups weren't allowed for this session only: {run.Outcome.GitHubRequests}");
            // The requests kept for a guide that names nothing are handed back: a later game's guide (with none kept of its own
            // here, the rest of the budget spent elsewhere) gets them.
            var second = _gamesPage.PlanAdding(new[] { echo, foxtrot });
            var handBack = second with
            {
                GitHubRequests = AP_Atlas.Core.Games.AddGamesPlan.GitHubBudget,
                Games = second.Games.Select(x => x.Game == foxtrot ? x with { GuideLookupReserved = false, Row = AP_Atlas.Core.Games.AddRow.LogicNoMapKnown } : x).ToList()
            };
            UiTestExpect(handBack.Games.Count == 2 && handBack.Games[0].Game == echo && handBack.Games[0].GuideLookupReserved && !handBack.Games[1].GuideLookupReserved
                && handBack.Games[1].Items.Single(i => i.Kind == AP_Atlas.Core.Games.AddItemKind.Apworld).FitsPack,
                $"the second plan isn't Echo with its guide's requests kept, then Foxtrot: {string.Join(", ", handBack.Games.Select(x => $"{x.Game} {x.Row}"))}");
            var run2 = await AddGamesAsync(handBack, new AddGamesConsent(false, true), null, System.Threading.CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(120));
            var echoRow = run2.Outcome.For(echo);
            foxRow = run2.Outcome.For(foxtrot);
            if (foxRow.PackFile != null && AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(foxRow.PackFile) is { } foxInstalled) foxKey = AP_Atlas.Core.PopTracker.PackFixes.KeyFor(foxInstalled);
            UiTestExpect(echoRow.Logic.Outcome == AP_Atlas.Core.Games.AddStepOutcome.Done && echoRow.Map.Outcome == AP_Atlas.Core.Games.AddStepOutcome.Unavailable
                && foxRow.Map.Outcome == AP_Atlas.Core.Games.AddStepOutcome.Done && foxRow.PackRepo == "packs/foxtrot-tracker"
                && foxRow.Logic.Outcome == AP_Atlas.Core.Games.AddStepOutcome.Done && !foxRow.LogicFitsPack,
                $"the requests Echo's guide didn't use weren't handed back to Foxtrot's, or its logic claims the guide's pack was measured on it: {echoRow.Map} / {foxRow.Map} / {foxRow.LogicFitsPack}");
        }
        finally
        {
            ApworldSources.UseListForTests(null);
            AP_Atlas.Core.GitHubApi.TestSite = null;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            AtlasEngine.State.LastCheck = checkBefore;
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            foreach (string source in new[] { "github.com/owner/add-delta", "github.com/owner/add-echo", "github.com/owner/add-foxtrot", "127.0.0.1" }) _appSettings.ApprovedApworldSources.Remove(source);
            foreach (string game in new[] { delta, echo, foxtrot }) _appSettings.PreferredPacks.Remove(game);
            DataManager.SaveSettings(_appSettings);
            foreach (string? packKey in new[] { deltaKey, foxKey }) if (packKey != null) AP_Atlas.Core.PopTracker.PackDoctorService.Reports.Remove(packKey);
            foreach (string file in new[] { "add_delta.apworld", "add_echo.apworld", "add_foxtrot.apworld" }) AP_Atlas.Core.SafeFile.Delete(System.IO.Path.Combine(worlds, file));
            foreach (var installedRow in new[] { row, foxRow }) if (installedRow?.PackFile != null) AP_Atlas.Core.SafeFile.Delete(installedRow.PackFile);
            foreach (string game in new[] { delta, echo, foxtrot })
            {
                string kept = AP_Atlas.Core.Games.GameFiles.ApworldsFolder(data, game);
                if (System.IO.Directory.Exists(kept)) System.IO.Directory.Delete(kept, true);
            }
            if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
        }
    }

    private async Task AddGamesScreenAsync()
    {
        StartFakeEngine(UiTestPython(), fake => fake.CheckWorldsDir = AtlasEngine.UserWorldsDir);
        const string alpha = "Add Screen Alpha", beta = "Add Screen Beta", gamma = "Add Screen Gamma", nowhere = "Add Screen Nowhere";
        static byte[] Zip(params (string Name, string Text)[] files)
        {
            using var memory = new System.IO.MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, text) in files)
                {
                    using var writer = new System.IO.StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(text);
                }
            return memory.ToArray();
        }
        static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        string data = DataManager.GetDataDirectory();
        string scratch = System.IO.Path.Combine(data, "uitest_add_screen");
        System.IO.Directory.CreateDirectory(scratch);
        byte[] alphaApworld = Zip(("add_screen_alpha/archipelago.json", "{\"game\": \"" + alpha + "\", \"world_version\": \"1.2.0\"}"), ("add_screen_alpha/__init__.py", "# " + alpha + "\n"));
        byte[] betaApworld = Zip(("add_screen_beta/archipelago.json", "{\"game\": \"" + beta + "\"}"), ("add_screen_beta/__init__.py", "# " + beta + "\n"));
        string alphaZip = System.IO.Path.Combine(scratch, "alpha.zip");
        FakeMapPack.Write(alphaZip, "UI test screen pack", alpha);
        byte[] alphaPack = await System.IO.File.ReadAllBytesAsync(alphaZip);
        await using var site = new FakeWebSite();
        site.Answer = (path, _) => path switch
        {
            "/dl/add_screen_alpha.apworld" => new FakeWebSite.FullAnswer(200, alphaApworld, "application/octet-stream"),
            "/dl/add_screen_beta.apworld" => new FakeWebSite.FullAnswer(200, betaApworld, "application/octet-stream"),
            "/gh/packs/screen-alpha/releases/download/v1/screen_alpha.zip" => new FakeWebSite.FullAnswer(200, alphaPack, "application/zip"),
            _ => new FakeWebSite.FullAnswer(404, Array.Empty<byte>())
        };
        var gammaSource = new ApworldSource { Game = gamma };
        var list = new ApworldSourceList
        {
            Format = "atlas-apworld-sources/1",
            Built = "9999-12-31",
            Source = "ui-test",
            Games = new List<ApworldSource>
            {
                new ApworldSource { Game = alpha, Apworld = "add_screen_alpha", Repo = "owner/add-screen-alpha",
                    Versions = { new ApworldVersion { Version = "1.2.0", Url = site.Site + "/dl/add_screen_alpha.apworld", Sha256 = Sha(alphaApworld), Size = alphaApworld.Length } },
                    Packs = { new IndexPack { Repo = "packs/screen-alpha", Release = "v1", Asset = "screen_alpha.zip", Sha256 = Sha(alphaPack), Size = alphaPack.Length, Fit = 100 } } },
                new ApworldSource { Game = beta, Apworld = "add_screen_beta", Repo = "owner/add-screen-beta",
                    Versions = { new ApworldVersion { Version = "0.3.0", Url = site.Site + "/dl/add_screen_beta.apworld", Sha256 = Sha(betaApworld), Size = betaApworld.Length } } },
                new ApworldSource { Game = nowhere },
                gammaSource
            }
        };
        // No official game of this test's is ticked: nothing may reach Archipelago's real release (the index lists one world,
        // as an index must, for a game no multiworld plays).
        var noWorlds = WorldsIndex.Parse(new JObject
        {
            ["archipelago"] = AtlasEngine.ArchipelagoVersion,
            ["zip_sha256"] = new string('0', 64),
            ["zip_root"] = "X/",
            ["zip_bytes"] = 1,
            ["core_worlds"] = new JArray(),
            ["worlds"] = new JObject { ["add_screen_shipped"] = new JObject { ["games"] = new JArray("Add Screen Shipped"), ["needs"] = new JArray(), ["requirements"] = false, ["bytes"] = 1 } }
        }.ToString());
        string worlds = AtlasEngine.UserWorldsDir, packs = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory();
        var checkBefore = AtlasEngine.State.LastCheck;
        var profile = new MultiworldProfile { Name = "UI test add screen" };
        profile.Slots.Clear();
        profile.Slots.Add("Quill");
        profile.SavedStats["Quill"] = new SlotStats { GameName = alpha };
        // Two more games the multiworld plays: Beta, addable from the start, and Gamma, which nothing is known of until its
        // project joins the list while Add games shows.
        profile.Slots.Add("Rook");
        profile.SavedStats["Rook"] = new SlotStats { GameName = beta };
        profile.Slots.Add("Kite");
        profile.SavedStats["Kite"] = new SlotStats { GameName = gamma };
        _profiles.Add(profile);
        RefreshProfileList();
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        GameAdderRun? run = null;
        AP_Atlas.UI.AddGamesDialog? Showing() => AP_Atlas.UI.AddGamesDialog.Showing is { } d && IsInstanceValid(d) && !d.IsQueuedForDeletion() ? d : null;
        async Task<AP_Atlas.UI.AddGamesDialog> OpenedAsync(string what)
        {
            var opened = await UiTestWaitForAsync(Showing, what);
            await UiTestWaitForAsync(() => opened.Panel.FactsPending == 0 ? opened : null, what + "'s rows", 20);
            return opened;
        }
        // Only the test's games run: any game a leftover multiworld plays is unticked first.
        void OnlyTicked(AP_Atlas.UI.AddGamesPanel p, string keep) { foreach (string g in p.Ticked.ToList()) if (g != keep) p.Tick(g, false); }
        AP_Atlas.UI.AddSlotDialog? AddSlot() => GetChildren().OfType<AP_Atlas.UI.AddSlotDialog>().FirstOrDefault(d => !d.IsQueuedForDeletion());
        try
        {
            ApworldSources.UseListForTests(list);
            AtlasEngine.TestWorlds = (noWorlds, site.Site + "/dl/none.zip", new string('0', 64));
            GameAdder.TestGitHubSite = site.Site + "/gh";
            AP_Atlas.Core.GitHubApi.TestSite = site.Site;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, false);
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null, false);
            _appSettings.ApprovedApworldSources.Remove("127.0.0.1"); // the confirm names it as a project to trust

            // Add a slot: the game's logic isn't in the engine and the list knows it: Add this game… opens Add games over the dialog.
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            SelectProfile(profile);
            _addSlotButton!.EmitSignal(BaseButton.SignalName.Pressed);
            var addSlot = await UiTestWaitForAsync(AddSlot, "the Add a slot dialog");
            addSlot.ChooseGame(alpha);
            UiTestExpect(addSlot.StatusActionText == "Install this game…", $"the engine line reads \"{addSlot.StatusText}\" with \"{addSlot.StatusActionText}\"");
            addSlot.PressStatusActionForTests();
            var overSlot = await OpenedAsync("Add games from Add a slot");
            UiTestExpect(overSlot.GetParent() == addSlot && !overSlot.Exclusive && overSlot.Panel.IsTicked(alpha), "Add games didn't open over Add a slot, non-exclusive, with the game ticked");
            // Past its list (its confirm here; a running batch or a summary alike), Add games outlives Add a slot: closing Add
            // a slot hands it to the window, where it stays as it was.
            overSlot.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(overSlot.Panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Confirm, $"Add on the dialog over Add a slot didn't show the confirm: {overSlot.Panel.CurrentStage}");
            addSlot.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            UiTestExpect(AddSlot() == null && Showing() == overSlot && overSlot.GetParent() == this && overSlot.Visible && overSlot.Panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Confirm,
                "closing Add a slot took Add games with it, or left it hidden or on another stage");
            overSlot.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            UiTestExpect(Showing() == null, "closing Add games on its confirm left it open");

            // The empty map leads with the pack Atlas's list knows.
            var map = new AP_Atlas.UI.MapTrackerControl(_appSettings);
            try
            {
                map.GameName = alpha;
                UiTestExpect(!map.AddGameButton.Visible, "the empty map offers a listed pack it wasn't told of");
                bool asked = false;
                map.AddGameRequested += () => asked = true;
                map.PackListed = true;
                UiTestExpect(map.AddGameButton.Visible && map.AddGameButton.Text == "Get a map pack for " + alpha + "…", $"the empty map's button says \"{map.AddGameButton.Text}\"");
                map.AddGameButton.EmitSignal(BaseButton.SignalName.Pressed);
                UiTestExpect(asked, "Get a map pack didn't ask for Add games");
            }
            finally { map.Free(); }

            // Home's Add games…: the list, its words, the search and the totals; nothing asked of any site.
            host.ShowTool(AP_Atlas.UI.Tool.Home);
            var home = _homePage!;
            home.AddGamesButton.EmitSignal(BaseButton.SignalName.Pressed);
            var dialog = await OpenedAsync("Add games from Home");
            var panel = dialog.Panel;
            UiTestExpect(ShownContent() == home && dialog.GetParent() == this && !panel.EngineLeadShown, "Add games left Home, or leads with an engine that's set up");
            await UiTestDialogSizedAsync(dialog, "the Add games dialog");
            UiTestExpect(panel.IsTicked(alpha) && panel.IsTicked(beta) && !panel.IsTicked(gamma) && !panel.RowTickable(gamma),
                $"every game of the multiworld that needs something isn't ticked (or one with nothing to add is): {string.Join(", ", panel.Ticked)}");
            OnlyTicked(panel, alpha);
            UiTestExpect(panel.Ticked.SequenceEqual(new[] { alpha }) && panel.Table.ShownRows.TakeWhile(r => r.Pinned).Any(r => r.Key == alpha)
                && panel.RowWords(alpha) == "logic + map" && panel.RowSize(alpha) == "under 0.1 MB",
                $"the game of the multiworld isn't first and ticked with its words: {panel.RowWords(alpha)} / {panel.RowSize(alpha)}");
            UiTestExpect(panel.RowTickable(beta) && panel.RowWords(beta) == "logic + the guide's map" && !panel.RowTickable(nowhere) && panel.RowWords(nowhere) == "nothing (logic unknown)",
                $"the rows' words: {panel.RowWords(beta)} | {panel.RowWords(nowhere)}");
            panel.Tick(nowhere, true);
            UiTestExpect(!panel.IsTicked(nowhere), "a game with nothing to add was ticked");
            // Read again (as after an engine change): Gamma, addable now that its project is listed, is ticked then; Beta, which
            // the user unticked, stays unticked.
            gammaSource.Repo = "owner/add-screen-gamma";
            gammaSource.Versions.Add(new ApworldVersion { Version = "0.1.0", Url = site.Site + "/dl/add_screen_gamma.apworld", Sha256 = new string('c', 64), Size = 100 });
            panel.Start();
            UiTestExpect(panel.RowTickable(gamma) && panel.IsTicked(gamma) && !panel.IsTicked(beta) && panel.IsTicked(alpha),
                $"a re-read didn't tick the game that became addable, or ticked one the user unticked: {string.Join(", ", panel.Ticked)}");
            panel.Tick(gamma, false);
            await UiTestWaitForAsync(() => panel.FactsPending == 0 ? panel : null, "the rows read again", 20);
            await UiTestWaitAsync(0.1); // the rows' layout
            UiTestColumnFits(panel.Table, 2, "the list's Installs column");
            UiTestColumnFits(panel.Table, 3, "the list's Download column");
            UiTestExpect(panel.TotalsText == "1 game · under 0.1 MB to download · under 0.1 MB on this PC" && dialog.GetOkButton().Text == "Install 1 game…" && !dialog.GetOkButton().Disabled,
                $"the totals read \"{panel.TotalsText}\" and the button \"{dialog.GetOkButton().Text}\"");
            panel.Table.SearchBox.Text = "nowhere";
            panel.Table.SearchBox.EmitSignal(LineEdit.SignalName.TextChanged, "nowhere");
            await UiTestWaitAsync(0.35);
            UiTestExpect(panel.Table.ShownRows.Select(r => r.Key).SequenceEqual(new[] { nowhere }), "typed words didn't narrow the list");
            panel.Table.SearchBox.Text = "";
            panel.Table.SearchBox.EmitSignal(LineEdit.SignalName.TextChanged, "");
            await UiTestWaitAsync(0.35);
            // The one confirm: every project named, what it trusts, Don't ask again; nothing downloaded yet. Back keeps the ticks.
            dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Confirm && panel.ConfirmText.Contains("127.0.0.1") && panel.ConfirmText.Contains("github.com/packs/screen-alpha")
                && panel.ConfirmText.Contains("Installing trusts the new one, 127.0.0.1") && panel.ConfirmText.StartsWith("Install 1 game · under 0.1 MB", StringComparison.Ordinal)
                && panel.ConfirmLines().SequenceEqual(new[] { alpha + " · logic + map · under 0.1 MB" }) && dialog.GetOkButton().Text == "Install"
                && panel.DontAskAgain is { Visible: true, ButtonPressed: false } && site.Requests.Count == 0 && dialog.SecondaryButton.Text == "Back",
                $"the confirm reads: {panel.ConfirmText}; its lines: {string.Join(" | ", panel.ConfirmLines())}");
            await UiTestDialogSizedAsync(dialog, "the Add games confirm");
            UiTestTextFits(dialog, "the Add games confirm");
            dialog.SecondaryButton.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Pick && panel.IsTicked(alpha), "Back didn't return to the list with the ticks");
            dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(panel.ConfirmedPlan!.Games.Select(g => g.Game).SequenceEqual(new[] { alpha }), "the confirm's plan isn't the ticked game");
            panel.DontAskAgain!.ButtonPressed = true;
            // The dots, per step the batch reports: on while a step runs without bar movement, off while a download moves the bar.
            var dotsSeen = new List<(bool Moves, float Fraction, bool Busy)>();
            panel.StatusChanged += s => dotsSeen.Add((s.BarMoves, s.Fraction, panel.BusyRunning));
            dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Running && dialog.GetOkButton().Text == "Cancel" && !dialog.Exclusive
                && panel.BusyRunning && dialog.GetOkButton().TooltipText.Length > 0, "the run doesn't show one bar with Cancel (what it keeps in its tooltip) and the dots, Atlas usable");
            // Closed while it runs (the window's ×), it hides and the batch goes on; it comes back with the summary.
            dialog.Hide();
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            UiTestExpect(!dialog.IsQueuedForDeletion() && Showing() == dialog && panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Running, "closing Add games while it runs stopped it or freed it");
            run = await UiTestWaitForAsync(() => panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Summary ? panel.Result : null, "the summary", 120);
            UiTestExpect(dialog.Visible && !run.Outcome.Cancelled, "the batch closed out of sight was stopped, or didn't come back with its summary");
            UiTestExpect(dotsSeen.Where(d => d.Fraction < 1f).All(d => d.Busy == !d.Moves) && dotsSeen.Any(d => !d.Moves && d.Fraction < 1f),
                $"the dots didn't follow the steps: {string.Join(", ", dotsSeen.Select(d => $"{d.Fraction:0.00}{(d.Moves ? " moving" : "")}: {(d.Busy ? "dots" : "none")}"))}");
            await UiTestDialogSizedAsync(dialog, "the Add games summary");
            UiTestTextFits(dialog, "the Add games summary");
            var lines = panel.SummaryLines();
            var alphaRow = run.Outcome.For(alpha);
            var alphaLook = run.Outcome.PacksToLook.FirstOrDefault(p => p.Game == alpha);
            UiTestExpect(alphaRow.Logic.Outcome == AP_Atlas.Core.Games.AddStepOutcome.Done && lines.Contains("logic 1.2.0")
                && lines.Contains(AP_Atlas.Core.Games.AddGamesWords.MapChip(alphaRow, alphaLook, t => t).Text) && (panel.ChipTooltip(alpha, "map") ?? "").Contains("github.com/packs/screen-alpha v1")
                && lines[0] == "1 game installed", $"the summary: {string.Join(" | ", lines)}");
            UiTestExpect(lines.Contains(AP_Atlas.Core.Games.AddGamesWords.Tip) && !panel.BusyRunning && !panel.BusyAnimating, "the summary doesn't end with the tip once a map came in, or the dots still run");
            UiTestExpect(AP_Atlas.Core.Permissions.IsAlwaysAllowed(_appSettings, AP_Atlas.Core.Permissions.EngineSetup) && ApworldSources.IsApproved(_appSettings, site.Site + "/dl/add_screen_alpha.apworld")
                && !site.Requests.Any(r => r.StartsWith("/repos/", StringComparison.Ordinal)), "the tick didn't keep the downloads allowed, the project wasn't trusted, or GitHub's API was asked");
            // Per pack the batch installed, what Atlas fixed by itself (with Review); nothing to answer: Done.
            UiTestExpect(run.Outcome.PacksToLook.Where(p => p.Fixed > 0).All(p => lines.Contains(AP_Atlas.Core.Games.AddGamesWords.FixedChip(p, t => t)!.Text)) && dialog.GetOkButton().Text == "Done",
                $"the summary's fixes, or its button ({dialog.GetOkButton().Text}): {string.Join(" | ", lines)}");
            // The dots: they step on screen while running, and stop for good when the work ends (Atlas idles again).
            var dots = new AP_Atlas.UI.BusyDots("Working");
            AddChild(dots);
            dots.Running = true;
            await UiTestWaitAsync(0.6);
            UiTestExpect(dots.Animating && dots.Ticks >= 1, $"the dots didn't step: {dots.Ticks}");
            dots.Running = false;
            int stepped = dots.Ticks;
            await UiTestWaitAsync(0.5);
            UiTestExpect(!dots.Animating && dots.Ticks == stepped && !dots.Running && dots.Visible, "the dots kept stepping after the work ended, or gave up their place");
            dots.QueueFree();
            dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed); // Done
            await UiTestWaitForAsync(() => Showing() == null ? this : null, "Done to close Add games");

            // Install games… at the top of the Games list: the added game is ready; Cancel mid-batch stops with nothing downloaded; Try those again.
            host.ShowTool(AP_Atlas.UI.Tool.Games);
            _gamesPage!.ShowOverviewForTests();
            var overview = _gamesPage.SidebarContent.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.HasMeta("add_games_button"));
            UiTestExpect(overview != null && overview.Text == "Install games…", "the Games list has no Install games… at its top");
            overview!.EmitSignal(BaseButton.SignalName.Pressed);
            dialog = await OpenedAsync("Add games from the Games overview");
            panel = dialog.Panel;
            OnlyTicked(panel, beta);
            UiTestExpect(!panel.IsTicked(alpha) && panel.RowWords(alpha) == "ready" && !panel.RowTickable(alpha), $"the added game reads \"{panel.RowWords(alpha)}\"");
            panel.Tick(beta, true);
            dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(panel.DontAskAgain is { Visible: false }, "Don't ask again shows once it's always allowed");
            int requestsBefore = site.Requests.Count;
            panel.StatusChanged += status => { if (status.Text.StartsWith("Game 1 of 1: ", StringComparison.Ordinal)) dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed); };
            dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            var stopped = await UiTestWaitForAsync(() => panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Summary ? panel.Result : null, "the stopped batch's summary", 60);
            UiTestExpect(stopped.Outcome.Cancelled && stopped.Outcome.For(beta).Logic.Outcome == AP_Atlas.Core.Games.AddStepOutcome.Cancelled && site.Requests.Count == requestsBefore
                && panel.SummaryLines()[0].StartsWith("Stopped", StringComparison.Ordinal) && dialog.SecondaryButton.Visible && dialog.SecondaryButton.Text == "Try those again",
                $"Cancel didn't stop the batch with nothing downloaded: {string.Join(" | ", panel.SummaryLines())}");
            dialog.SecondaryButton.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Pick && panel.IsTicked(beta), "Try those again didn't tick what's left");
            UiTestExpect(panel.Table.CountLabel.Text.EndsWith(" games", StringComparison.Ordinal), $"the list counts \"{panel.Table.CountLabel.Text}\"");
            // A made-up summary (nothing read or changed): a line per game, Review where Atlas fixed something, the way to the
            // version that fits beside a map that pins under three quarters of its checks, Find one… where there's no map, and
            // Done; or the game asked for meanwhile, which goes back to the list with it ticked.
            panel.ShowSummaryForTests(VisualCheckSummarySample());
            var made = panel.FindChildren("*", nameof(Button), true, false).OfType<Button>().ToList();
            int reviewButtons = made.Count(b => b.HasMeta("review_fixes"));
            var madeLines = panel.SummaryLines();
            UiTestExpect(dialog.GetOkButton().Text == "Done" && reviewButtons == 2
                && made.Where(b => b.HasMeta("find_version")).Select(b => b.GetMeta("find_version").AsString()).SequenceEqual(new[] { "Example Saga" })
                && made.Where(b => b.HasMeta("find_pack")).Select(b => b.GetMeta("find_pack").AsString()).SequenceEqual(new[] { "Demo Odyssey" })
                && panel.ChipState("Example Saga", "map") == "Warn" && panel.ChipState("Sample Quest", "map") == "Ok" && panel.ChipState("Demo Odyssey", "map") == "Skipped"
                && madeLines.SequenceEqual(new[] { "4 games installed", "logic 2.1.0", "map 1129/1150", "12 fixes", "logic 0.9.3", "map 31/1288", "35 fixes", "logic 0.6.7", "map 420/420", "logic 1.0.0", "no map", AP_Atlas.Core.Games.AddGamesWords.Tip })
                && (panel.ChipTooltip("Model Kingdom", "logic") ?? "") == "Ships with Archipelago 0.6.7.",
                $"the made-up summary: {dialog.GetOkButton().Text}, {reviewButtons} Review; {string.Join(" | ", madeLines)}");
            await UiTestDialogSizedAsync(dialog, "the made-up summary");
            UiTestTextFits(dialog, "the made-up summary");
            // Its button finds the version the map fits (the Games page's "Which version works with my map pack?" for the game),
            // and the dots, on a panel of the test's own: on while a step runs without the bar moving, off while it moves.
            string? askedVersion = null;
            var real = AddGamesPanelHooks();
            var probe = new AP_Atlas.UI.AddGamesPanel(new AP_Atlas.UI.AddGamesPanel.Hooks
            {
                Tr = real.Tr,
                Settings = real.Settings,
                Source = () => AP_Atlas.UI.AddGamesSource.Empty,
                RunAsync = real.RunAsync,
                OpenEngineSetup = () => { },
                FindPack = _ => { },
                FindVersion = g => askedVersion = g,
                InstallPack = (_, _) => { },
                StartPack = _ => { },
                FreeBytes = () => null
            });
            try
            {
                AddChild(probe);
                probe.ShowSummaryForTests(VisualCheckSummarySample());
                probe.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.HasMeta("find_version"))?.EmitSignal(BaseButton.SignalName.Pressed);
                UiTestExpect(askedVersion == "Example Saga", $"Find the version that fits asked for {askedVersion ?? "nothing"}");
                var shipped = new AP_Atlas.Core.Games.AddGamesOutcome(new[] { ("Model Kingdom", true, true) });
                shipped.For("Model Kingdom").Logic = AP_Atlas.Core.Games.AddStep.Done("0.6.7");
                shipped.For("Model Kingdom").LogicShips = true;
                shipped.For("Model Kingdom").Map = AP_Atlas.Core.Games.AddStep.Done("github.com/model/kingdom-map v3");
                shipped.PacksToLook.Add(new AP_Atlas.Core.Games.PackToLook("Model Kingdom", "model kingdom map", "Model Kingdom map", "Model Kingdom map.zip", 0, 300, 420));
                probe.ShowSummaryForTests(new GameAdderRun(shipped));
                UiTestExpect(probe.ChipState("Model Kingdom", "map") == "Warn" && !probe.FindChildren("*", nameof(Button), true, false).OfType<Button>().Any(b => b.HasMeta("find_version"))
                    && !(probe.ChipTooltip("Model Kingdom", "map") ?? "").Contains("Find the version that fits"),
                    "a game that ships with Archipelago offers a version of its own that fits its pack");
                probe.ShowRunningForTests(2, new AP_Atlas.Core.Games.AddGamesStatus("Game 1 of 2: Downloading Sample Quest's logic…", 0.1f), holdStill: false);
                bool atStep = probe.BusyRunning;
                probe.StatusForTests(new AP_Atlas.Core.Games.AddGamesStatus("Game 1 of 2: Downloading Sample Quest's logic…", 0.2f, BarMoves: true));
                bool whileMoving = probe.BusyRunning;
                probe.StatusForTests(new AP_Atlas.Core.Games.AddGamesStatus("Adding to the Atlas Engine and checking it…", 0.3f));
                bool nextStep = probe.BusyRunning;
                await UiTestWaitAsync(0.5);
                bool animating = probe.BusyAnimating;
                probe.ShowSummaryForTests(VisualCheckSummarySample());
                await UiTestWaitAsync(0.3);
                UiTestExpect(atStep && !whileMoving && nextStep && animating && !probe.BusyRunning && !probe.BusyAnimating,
                    $"the dots: at a step {atStep}, while the bar moves {whileMoving}, at the next step {nextStep} (stepping {animating}); after, {probe.BusyRunning}/{probe.BusyAnimating}");
            }
            finally { probe.QueueFree(); }
            panel.Preselect(beta);
            UiTestExpect(dialog.GetOkButton().Text == "Install " + beta + "…", $"a game asked for on the summary isn't offered: {dialog.GetOkButton().Text}");
            dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Pick && panel.Ticked.SequenceEqual(new[] { beta }), $"the list didn't come back with the game asked for ticked: {string.Join(", ", panel.Ticked)}");
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
        }
        finally
        {
            Showing()?.QueueFree();
            AddSlot()?.QueueFree();
            ApworldSources.UseListForTests(null);
            AtlasEngine.TestWorlds = null;
            GameAdder.TestGitHubSite = null;
            AP_Atlas.Core.GitHubApi.TestSite = null;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, false);
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            AtlasEngine.State.LastCheck = checkBefore;
            foreach (var p in run?.Outcome.PacksToLook ?? new List<AP_Atlas.Core.Games.PackToLook>()) AP_Atlas.Core.PopTracker.PackDoctorService.Reports.Remove(p.PackKey);
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            _appSettings.ApprovedApworldSources.Remove("127.0.0.1");
            _appSettings.PreferredPacks.Remove(alpha);
            DataManager.SaveSettings(_appSettings);
            AP_Atlas.Core.SafeFile.Delete(System.IO.Path.Combine(worlds, "add_screen_alpha.apworld"));
            AP_Atlas.Core.SafeFile.Delete(System.IO.Path.Combine(packs, "screen_alpha.zip"));
            foreach (string game in new[] { alpha, beta })
            {
                string kept = AP_Atlas.Core.Games.GameFiles.ApworldsFolder(data, game);
                if (System.IO.Directory.Exists(kept)) System.IO.Directory.Delete(kept, true);
            }
            if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
        }
    }

    private async Task PackAutoFixAsync()
    {
        const string game = "Handoff Test Game";
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_handoff_pack.zip");
        // "Shrine" / "Bell!" is the game's "Bell" once punctuation is ignored (exact); "Cave" / "Chest" lies inside "Cave Chest Room"
        // (confident, about 88%); "Tower" / "Peak" shares a word with "Tower Top" (about 60%: left for the user).
        FakeMapPack.Write(zip, "UI test handoff pack", game,
            locationsJson: """[{"name":"Shrine","sections":[{"name":"Bell!"}],"map_locations":[{"map":"World","x":30,"y":10}]},""" +
                           """{"name":"Cave","sections":[{"name":"Chest"}],"map_locations":[{"map":"World","x":10,"y":10}]},""" +
                           """{"name":"Tower","sections":[{"name":"Peak"}],"map_locations":[{"map":"World","x":50,"y":20}]}]""");
        await using var server = new FakeArchipelagoServer { SlotGame = game };
        server.SlotName = "Kestrel";
        server.Games[game] = new FakeGame("4a4d0ff0123456789abcdef0123456789abcdef0",
            new Dictionary<string, long> { ["Sword"] = 1000, ["Shield"] = 1001 },
            new Dictionary<string, long> { ["Bell"] = 2000, ["Cave Chest Room"] = 2001, ["Tower Top"] = 2002 });
        var profile = new MultiworldProfile { Name = "UI test auto fix", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Kestrel");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var notes = new List<AP_Atlas.Core.PopTracker.PackDoctorService.AutoFixNote>();
        void OnFixed(AP_Atlas.Core.PopTracker.PackDoctorService.AutoFixNote note) => notes.Add(note);
        string key = "";
        bool tipBefore = _appSettings.FixTipDismissed;
        int leftHolds = AP_Atlas.Core.PopTracker.PackDoctorService.ResetPromptHoldsForTests();
        if (leftHolds > 0) GD.Print($"UITEST INFO Atlas fixes packs: {leftHolds} hold(s) on the Doctor's cards were left by an earlier scenario");
        if (AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(zip) is { } fresh)
        {
            // As on the pack's first day: no fixes, nothing declined, and no record of Atlas's pass (Reset everything keeps that).
            string freshKey = AP_Atlas.Core.PopTracker.PackFixes.KeyFor(fresh);
            AP_Atlas.Core.PopTracker.PackFixes.Reset(freshKey);
            AP_Atlas.Core.PopTracker.PackFixes.SetAutoLinkedVersion(freshKey, "");
            AP_Atlas.Core.PopTracker.PackDoctorService.Reports.Remove(freshKey);
        }
        try
        {
            AP_Atlas.Core.PopTracker.PackDoctorService.AutoFixed += OnFixed;
            _appSettings.FixTipDismissed = false;
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
            await OnConnectSlotPressedAsync("Kestrel", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Kestrel"), "Kestrel's view");
            var pack = await UiTestWaitForAsync(() => slot.Pack, "Kestrel to load the pack");
            key = AP_Atlas.Core.PopTracker.PackFixes.KeyFor(pack);
            var tip = await UiTestWaitForAsync(() => slot.MapTracker.FixTipShown, "the map's tip");
            UiTestExpect(tip.Contains("right-click", StringComparison.OrdinalIgnoreCase) && tip.Contains("Undo") && tip.Contains("Changes"), $"the map's tip: {tip}");
            // The first check: the exact and the confident match linked by Atlas itself; the 60% one waits on the Notices; one card.
            var note = await UiTestWaitForAsync(() => notes.FirstOrDefault(n => n.Key == key), "the card saying what Atlas fixed", 40);
            var file = AP_Atlas.Core.PopTracker.PackFixes.Get(key);
            UiTestExpect(file.Links.Any(l => l.Subject == "link:Shrine|Bell!" && l.ApLocationId == 2000 && l.Source == "automatic")
                && file.Links.Any(l => l.Subject == "link:Cave|Chest" && l.ApLocationId == 2001 && l.Source == "automatic")
                && !file.Links.Any(l => l.Subject == "link:Tower|Peak") && file.Ignored.Count == 0,
                $"Atlas's pass: {string.Join(", ", file.Links.Select(l => $"{l.Subject}→{l.ApLocationName} ({l.Source})"))}; {file.Ignored.Count} ignored");
            UiTestExpect(note.Fixed == 2 && notes.Count == 1, $"the card: {note.Fixed} fixed, {notes.Count} card(s)");
            UiTestExpect(_alertLog.Entries.Any(e => e.Message.StartsWith("Atlas fixed 2 things in UI test handoff pack by itself.", StringComparison.Ordinal))
                && !_alertLog.Entries.Any(e => e.Message.Contains("left for you on the map's Notices")),
                "no card on the feed says what Atlas fixed, or one still counts what's left");
            await UiTestWaitForAsync(() => slot.MapTracker.AttentionRows().Any(r => r.Key == "loc:unmatched:Tower|Peak") ? slot : null, "the 60% pin on the Notices");
            UiTestExpect(!slot.MapTracker.AttentionRows().Any(r => r.Key is "loc:unmatched:Cave|Chest" or "loc:unmatched:Shrine|Bell!"), "a fixed pin is still on the Notices");
            // Changes: both fixes marked as Atlas's, and a check hidden on the map; each with Undo.
            slot.SetChecksHidden(new long[] { 2002 }, true);
            await UiTestWaitForAsync(() => slot.MapTracker.ChangesTabText == "Changes (3)" ? slot : null, "the Changes tab to count the hidden check and both fixes");
            slot.MapTracker.ShowChanges();
            await UiTestWaitAsync(0.1);
            var rows = slot.MapTracker.Changes();
            UiTestExpect(slot.MapTracker.ChangesShown && !slot.MapTracker.NoticesShown
                && rows.Any(r => r.Key == "hidden:2002" && r.Text.StartsWith("Tower Top hidden", StringComparison.Ordinal) && !r.ByAtlas)
                && rows.Any(r => r.Key == "link:Cave|Chest" && r.ByAtlas && r.Text == "Cave / Chest shows Cave Chest Room" && r.Show != null)
                && rows.Any(r => r.Key == "link:Shrine|Bell!" && r.ByAtlas),
                $"the Changes tab: {string.Join(" | ", rows.Select(r => $"{r.Key}: {r.Text}"))}");
            UiTestExpect(slot.MapTracker.SidebarContent.FindChildren("*", nameof(PanelContainer), true, false).OfType<PanelContainer>().Count(c => c.HasMeta("change_row")) == 3,
                "the Changes pane doesn't show a card per change");
            slot.MapTracker.ShowMap("Broken");
            UiTestExpect(slot.MapTracker.CurrentMapId == "Broken", "the map didn't switch away before Show on map");
            slot.MapTracker.PressChange("link:Cave|Chest", show: true);
            UiTestExpect(slot.MapTracker.CurrentMapId == "World", $"Show on map showed {slot.MapTracker.CurrentMapId}");
            slot.MapTracker.PressChange("hidden:2002", show: false);
            await UiTestWaitForAsync(() => !slot.Model.HiddenChecks.Contains(2002) && slot.MapTracker.ChangesTabText == "Changes (2)" ? slot : null, "the hidden check's Undo");
            UiTestExpect(slot.MapTracker.SidebarContent.FindChildren("*", nameof(PanelContainer), true, false).OfType<PanelContainer>().Count(c => c.HasMeta("change_row")) == 2,
                "the Changes pane didn't follow the Undo");
            // Review fixes (the card's button): each of Atlas's fixes with Undo; an Undo takes it back and declines it.
            ReviewFixes(key, zip);
            var review = await UiTestWaitForAsync(() => AP_Atlas.UI.ReviewFixesDialog.Showing, "Review fixes");
            UiTestExpect(review.RowTexts().OrderBy(t => t, StringComparer.Ordinal).SequenceEqual(new[] { "Cave / Chest shows Cave Chest Room", "Shrine / Bell! shows Bell" }),
                $"Review fixes lists {string.Join(" | ", review.RowTexts())}");
            review.PressUndo("link:Cave|Chest");
            var declined = AP_Atlas.Core.PopTracker.PackFixes.Get(key);
            UiTestExpect(!declined.Links.Any(l => l.Subject == "link:Cave|Chest") && declined.Declined.Contains("link:Cave|Chest") && review.RowTexts().Count == 1, "Undo didn't take the fix back and decline it");
            // A check, and one after a new stamp (a new Atlas), leave the declined fix to the user; no card comes.
            await AP_Atlas.Core.PopTracker.PackDoctorService.WhenIdleAsync(key, TimeSpan.FromSeconds(30));
            AP_Atlas.Core.PopTracker.PackFixes.SetAutoLinkedVersion(key, "");
            await AP_Atlas.Core.PopTracker.PackDoctorService.CheckAsync(pack, prompt: true);
            await AP_Atlas.Core.PopTracker.PackDoctorService.WhenIdleAsync(key, TimeSpan.FromSeconds(30));
            UiTestExpect(!AP_Atlas.Core.PopTracker.PackFixes.Get(key).Links.Any(l => l.Subject == "link:Cave|Chest") && notes.Count == 1, $"the pass made the declined fix again, or a card came ({notes.Count})");
            await UiTestWaitForAsync(() => slot.MapTracker.AttentionRows().Any(r => r.Key == "loc:unmatched:Cave|Chest") ? slot : null, "the undone pin back on the Notices");
            // Undo all: the rest goes, declined too; the Doctor's Undo brings it back.
            review.PressUndoAll();
            UiTestExpect(AP_Atlas.Core.PopTracker.PackFixes.Get(key).Links.Count == 0 && AP_Atlas.Core.PopTracker.PackFixes.Get(key).Declined.Contains("link:Shrine|Bell!") && review.RowTexts().Count == 0,
                "Undo all left a fix of Atlas's");
            UiTestExpect(AP_Atlas.Core.PopTracker.PackFixes.Undo(key) && AP_Atlas.Core.PopTracker.PackFixes.Get(key).Links.Any(l => l.Subject == "link:Shrine|Bell!" && l.Source == "automatic")
                && !AP_Atlas.Core.PopTracker.PackFixes.Get(key).Declined.Contains("link:Shrine|Bell!"), "Undo didn't bring back what Undo all took");
            review.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            // Got it: for good.
            slot.MapTracker.PressFixTipGotIt();
            UiTestExpect(_appSettings.FixTipDismissed && slot.MapTracker.FixTipShown == null, "Got it didn't put the tip away");
            slot.RebuildPackIndex();
            UiTestExpect(slot.MapTracker.FixTipShown == null, "the tip came back when the map loaded the pack again");
        }
        finally
        {
            AP_Atlas.Core.PopTracker.PackDoctorService.AutoFixed -= OnFixed;
            _appSettings.FixTipDismissed = tipBefore;
            AP_Atlas.UI.ReviewFixesDialog.Showing?.QueueFree();
            foreach (var dialog in GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>()) dialog.QueueFree();
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            if (key.Length > 0) AP_Atlas.Core.PopTracker.PackFixes.Reset(key);
            AP_Atlas.Core.SafeFile.Delete(zip);
        }
    }

    private async Task GameWorldsOnDemandAsync()
    {
        var engine = StartFakeEngine(UiTestPython());
        engine.Apply();
        const string game = "On Demand Game", other = "Other Demand Game";
        // A pretend Archipelago release: the game's world, a world it imports, another game's world and a core file, served
        // by a fake site at the address the index names.
        static byte[] Zip(params (string Name, string Text)[] files)
        {
            using var memory = new System.IO.MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, text) in files)
                {
                    using var writer = new System.IO.StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(text);
                }
            return memory.ToArray();
        }
        byte[] release = Zip(("FakeArchipelago/Utils.py", "# core\n"), ("FakeArchipelago/worlds/on_demand/__init__.py", "# On Demand Game\n"),
            ("FakeArchipelago/worlds/on_demand/docs/setup_en.md", "# Setup\n\nPlay it.\n"), ("FakeArchipelago/worlds/on_demand/docs/en_On Demand Game.md", "# On Demand Game\n"),
            ("FakeArchipelago/worlds/shared_lib/__init__.py", "# shared\n"), ("FakeArchipelago/worlds/other_demand/__init__.py", "# other\n"));
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(release)).ToLowerInvariant();
        var index = WorldsIndex.Parse(new JObject
        {
            ["archipelago"] = AtlasEngine.ArchipelagoVersion,
            ["zip_sha256"] = sha,
            ["zip_root"] = "FakeArchipelago/",
            ["zip_bytes"] = release.Length,
            ["core_worlds"] = new JArray("generic"),
            ["worlds"] = new JObject
            {
                ["on_demand"] = new JObject { ["games"] = new JArray(game), ["needs"] = new JArray("shared_lib"), ["requirements"] = false, ["bytes"] = 10 },
                ["shared_lib"] = new JObject { ["games"] = new JArray("Shared Lib Game"), ["needs"] = new JArray(), ["requirements"] = false, ["bytes"] = 5 },
                ["other_demand"] = new JObject { ["games"] = new JArray(other), ["needs"] = new JArray(), ["requirements"] = false, ["bytes"] = 5 }
            }
        }.ToString());
        await using var site = new AP_Atlas.Core.Testing.FakeWebSite();
        site.Answer = (path, _) => path == "/dl/archipelago.zip" ? new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, release, "application/zip") : new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(404, Array.Empty<byte>());
        AtlasEngine.TestWorlds = (index, site.Site + "/dl/archipelago.zip", sha);
        await using var server = LogicWorldServer();
        server.Games[game] = server.Games["Test Game"];
        server.SlotGame = game;
        server.Slots.Add("Unpacker");
        var parts = new List<AtlasEngine.EngineChange>();
        void OnParts(IReadOnlyList<AtlasEngine.EngineChange> changes) { lock (parts) parts.AddRange(changes); }
        AtlasEngine.PartsChanged += OnParts;
        var profile = new MultiworldProfile { Name = "UI test worlds", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Unpacker");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var install = AtlasEngine.Current;
        string worlds = AtlasEngine.UserWorldsDir;
        string otherApworld = System.IO.Path.Combine(worlds, "other_demand.apworld");
        AP_Atlas.Core.Permissions.ResetSessionForTests();
        AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, false);
        try
        {
            // The game's world and the one it imports are to be unpacked; nothing is downloaded before a slot needs it.
            UiTestExpect(AtlasEngine.WorldsToUnpack(install, game).SequenceEqual(new[] { "on_demand", "shared_lib" }) && !AtlasEngine.WorldsZipPresent,
                $"the game would unpack {string.Join(", ", AtlasEngine.WorldsToUnpack(install, game))}");
            // The Games page lists Archipelago's own games, each saying its world is unpacked when a slot needs it.
            host.ShowTool(AP_Atlas.UI.Tool.Games);
            _gamesPage!.Refresh();
            UiTestExpect(_gamesPage.Games.Any(g => g.Game == game && g.Section == AP_Atlas.UI.GamesPage.Section.Official && g.InEngine), "the Games page doesn't list a game that ships with Archipelago");
            // Adding the two games plans their worlds unpacked (each folder once) from Archipelago's release, downloaded once at its listed size; nothing is fetched to plan.
            var adding = _gamesPage.PlanAdding(new[] { game, other });
            UiTestExpect(adding.NeedsArchipelagoRelease && adding.ArchipelagoReleaseBytes == release.Length && adding.DownloadBytes == release.Length && adding.DiskBytes == release.Length + 20
                && adding.Games[0].Items.Single().Folders.Select(f => f.Folder).SequenceEqual(new[] { "on_demand", "shared_lib" })
                && adding.Games[0].Row == AP_Atlas.Core.Games.AddRow.LogicMapFromGuide && adding.Games[0].GuideLookupReserved && site.Requests.Count == 0,
                $"the add plan for a game that ships with Archipelago is wrong: {adding.DownloadBytes} down, {adding.DiskBytes} on disk");
            // The slot's logic can't start without the release: its banner says so and offers to get it; nothing went online.
            await OnConnectSlotPressedAsync("Unpacker", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Unpacker"), "the slot's view");
            host.ShowTool(AP_Atlas.UI.Tool.LogicTracker);
            await UiTestWaitForAsync(() => slot.EngineProblem?.Code == "world_download" && slot.LogicNoticeButtons().Contains("Get the game's world…") ? slot : null, "the banner offering the game's world");
            UiTestExpect(site.Requests.Count == 0, "Archipelago's release was downloaded before anyone allowed it");
            // The button asks the engine's permission; Allow once downloads the release once, unpacks the two worlds, and logic starts.
            slot.PressLogicNotice("Get the game's world…");
            var question = await UiTestWaitForAsync(() => slot.GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == AP_Atlas.Core.Permissions.EngineSetup.Title && !d.IsQueuedForDeletion()), "the engine's permission question");
            question.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitForAsync(() => slot.Model.Logic.Running || slot.EngineProblem is { Code: not "world_download" } ? slot : null, "the slot's logic to start once its world was unpacked", 30);
            UiTestExpect(slot.Model.Logic.Running, $"the slot's logic didn't start: {slot.EngineProblem?.Code} {slot.EngineProblem?.Message}");
            UiTestExpect(site.Requests.Count(r => r == "/dl/archipelago.zip") == 1 && System.IO.File.Exists(System.IO.Path.Combine(worlds, "on_demand", "__init__.py"))
                && System.IO.Directory.Exists(System.IO.Path.Combine(worlds, "shared_lib")) && !System.IO.Directory.Exists(System.IO.Path.Combine(worlds, "other_demand")) && !System.IO.File.Exists(System.IO.Path.Combine(worlds, "Utils.py")),
                "the release wasn't downloaded once with only the game's worlds unpacked");
            UiTestExpect(AtlasEngine.WorldsZipPresent && AtlasEngine.WorldsToUnpack(install, game).Count == 0, "the release isn't kept, or the game still wants unpacking");
            var afterwards = _gamesPage!.PlanAdding(new[] { game });
            UiTestExpect(!afterwards.Items.Any() && !afterwards.NeedsArchipelagoRelease && afterwards.Games[0].Row == AP_Atlas.Core.Games.AddRow.NoMapKnown, "a game whose world is unpacked still plans something");
            lock (parts) UiTestExpect(parts.Any(p => p.Kind == "world" && p.Game == game), "the engines weren't told a game's world arrived");
            var record = AP_Atlas.Core.SafeFile.ReadJson(System.IO.Path.Combine(worlds, ".atlas_worlds.json"), () => new UnpackedWorlds());
            UiTestExpect(record?.Archipelago == AtlasEngine.ArchipelagoVersion && record.Folders.OrderBy(f => f).SequenceEqual(new[] { "on_demand", "shared_lib" }), "the unpacked worlds weren't recorded with Archipelago's version");
            UiTestExpect(ApworldSources.BundledDocuments(install, game).Any(d => d.Setup), "the unpacked world's setup guide isn't found");
            // The solo test of a game that ships with Archipelago, with the release not here: its own window asks the engine's
            // permission (the beta's Dark Souls III test failed at once without asking), then the game's world is unpacked.
            AP_Atlas.Core.SafeFile.Delete(AtlasEngine.WorldsZipPath);
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            // GitHub is the fake site too: nothing a test does may reach the real one.
            AP_Atlas.Core.GitHubApi.TestSite = site.Site;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            var solo = AP_Atlas.UI.SoloTestDialog.Open(this, other, text => Tr(text), SoloHooks());
            try
            {
                solo.StartForTests();
                var soloQuestion = await UiTestWaitForAsync(() => solo.GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == AP_Atlas.Core.Permissions.EngineSetup.Title && !d.IsQueuedForDeletion()),
                    "the solo test's window to ask for Archipelago's release");
                soloQuestion.EmitSignal(AcceptDialog.SignalName.Confirmed);
                await UiTestWaitForAsync(() => System.IO.File.Exists(System.IO.Path.Combine(worlds, "other_demand", "__init__.py")) ? solo : null, "the solo test to unpack the game's world", 30);
                UiTestExpect(site.Requests.Count(r => r == "/dl/archipelago.zip") == 2, "the solo test didn't download the release once, after asking");
            }
            finally
            {
                solo.CancelForTests();
                await UiTestWaitForAsync(() => !solo.Runner.Busy ? solo : null, "the solo test to stop", 30);
                foreach (var asked in solo.GetChildren().OfType<ConfirmationDialog>().ToList()) asked.EmitSignal(AcceptDialog.SignalName.Canceled);
                solo.EmitSignal(AcceptDialog.SignalName.Confirmed);
                await UiTestWaitAsync(0.2);
                foreach (var report in System.IO.Directory.GetFiles(AP_Atlas.Core.EngineSetup.SoloTestRunner.ReportsFolder, "Other_Demand_Game-*")) System.IO.File.Delete(report);
                AP_Atlas.Core.GitHubApi.TestSite = null;
                AP_Atlas.Core.GitHubApi.ResetForTests();
            }
            System.IO.Directory.Delete(System.IO.Path.Combine(worlds, "other_demand"), true);
            // An apworld installed for a game takes precedence: its world isn't unpacked. Its guide comes from the kept release meanwhile.
            UiTestExpect(AtlasEngine.WorldsToUnpack(install, other).SequenceEqual(new[] { "other_demand" }), "another game's world isn't wanted");
            await System.IO.File.WriteAllBytesAsync(otherApworld, Zip(("other_demand/archipelago.json", "{\"game\": \"" + other + "\"}")));
            UiTestExpect(AtlasEngine.WorldsToUnpack(install, other).Count == 0, "an installed apworld didn't take precedence over the game's world");
            // After an update to another Archipelago, what was unpacked from the older one is removed on the next start.
            AP_Atlas.Core.SafeFile.WriteJson(System.IO.Path.Combine(worlds, ".atlas_worlds.json"), new UnpackedWorlds { Archipelago = "0.0.1", Folders = { "on_demand", "shared_lib" } });
            AtlasEngine.Initialize(_appSettings, System.IO.Path.GetDirectoryName(OS.GetExecutablePath()));
            UiTestExpect(!System.IO.Directory.Exists(System.IO.Path.Combine(worlds, "on_demand")) && !System.IO.Directory.Exists(System.IO.Path.Combine(worlds, "shared_lib")) && System.IO.File.Exists(otherApworld),
                "the worlds of another Archipelago weren't removed (or an installed apworld was)");
        }
        finally
        {
            AtlasEngine.PartsChanged -= OnParts;
            AtlasEngine.TestWorlds = null;
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            foreach (string folder in new[] { "on_demand", "shared_lib", "other_demand" })
                if (System.IO.Directory.Exists(System.IO.Path.Combine(worlds, folder))) System.IO.Directory.Delete(System.IO.Path.Combine(worlds, folder), true);
            AP_Atlas.Core.SafeFile.Delete(otherApworld);
            AP_Atlas.Core.SafeFile.Delete(System.IO.Path.Combine(worlds, ".atlas_worlds.json"));
            AP_Atlas.Core.SafeFile.Delete(AtlasEngine.WorldsZipPath);
        }
    }

    private async Task SoloBatchAsync()
    {
        var engine = StartFakeEngine(UiTestPython());
        engine.Apply();
        var checkBefore = AtlasEngine.State.LastCheck;
        AtlasEngine.State.LastCheck = new EngineCheckResult { Games = new List<string> { "Test Game", "Packless Game" }, TrackerLoads = true };
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_batch_pack.zip");
        FakeMapPack.Write(zip, "UI test batch pack", "Test Game");
        AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        string reports = AP_Atlas.Core.EngineSetup.SoloTestRunner.ReportsFolder;
        int profilesBefore = _profiles.Count;
        AP_Atlas.UI.SoloBatchDialog? Dialog() => GetChildren().OfType<AP_Atlas.UI.SoloBatchDialog>().FirstOrDefault(d => !d.IsQueuedForDeletion());
        bool developerBefore = _appSettings.DeveloperMode;
        SetDeveloperModeForTests(true); // Test every ready game… is a testing tool (Debug)
        try
        {
            host.ShowTool(AP_Atlas.UI.Tool.Games);
            _gamesPage!.ShowOverviewForTests();
            await UiTestWaitAsync(0.2);
            var button = _gamesPage.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.HasMeta("solo_batch_button"));
            UiTestExpect(button != null && !button.Disabled, "the Games overview has no enabled Test every ready game… button");
            // Atlas's game list: the overview offers Update game list, and the list ships official games' packs with their measured fit (no community source for them).
            UiTestExpect(_gamesPage.FindChildren("*", nameof(Button), true, false).OfType<Button>().Any(b => b.HasMeta("update_game_list")), "the Games overview has no Update game list button");
            var listed = AP_Atlas.Core.EngineSetup.ApworldSources.List.Games;
            UiTestExpect(listed.Count(g => g.Official) > 50 && listed.Any(g => g.Official && g.Packs.Count > 0 && g.Packs[0].Fit != null) && listed.Any(g => !g.Official && g.LoadsWith == "0.6.7")
                && !_gamesPage.GameNames().Contains("A Hat in Time") || _gamesPage.GameNames().Contains("A Hat in Time") && AP_Atlas.Core.EngineSetup.ApworldSources.Find("A Hat in Time")?.Repo == null,
                "the bundled game list lacks official games' packs with their fit, or lists an official game as a community source");
            button!.EmitSignal(BaseButton.SignalName.Pressed);
            var dialog = await UiTestWaitForAsync(Dialog, "the batch dialog");
            // Only the game with a pack is ready; the other joins with the box ticked.
            UiTestExpect(AP_Atlas.Core.EngineSetup.SoloBatch.ReadyGames(AtlasEngine.Current, false).SequenceEqual(new[] { "Test Game" })
                && AP_Atlas.Core.EngineSetup.SoloBatch.ReadyGames(AtlasEngine.Current, true).SequenceEqual(new[] { "Packless Game", "Test Game" }), "the ready games aren't the ones with a pack (or every game with the box ticked)");
            dialog.StartForTests();
            await UiTestWaitForAsync(() => dialog.Done ? dialog : null, "the batch to end", 90);
            UiTestExpect(dialog.SummaryPath != null && System.IO.File.Exists(dialog.SummaryPath), "the batch wrote no summary");
            string summary = await System.IO.File.ReadAllTextAsync(dialog.SummaryPath!);
            UiTestExpect(summary.Contains("| Test Game | ok | exact") && summary.Contains("placed") && !summary.Contains("Packless"), $"the summary doesn't score Test Game: {summary}");
            UiTestExpect(dialog.Rows().Count == 1 && dialog.Rows()[0].Text.Contains("exact"), $"the dialog's row doesn't say the outcome: {string.Join(" | ", dialog.Rows().Select(r => r.Text))}");
            var mds = System.IO.Directory.GetFiles(reports, "Test_Game-*.md");
            UiTestExpect(mds.Length == 1, $"{mds.Length} report(s) written for Test Game");
            string md = await System.IO.File.ReadAllTextAsync(mds[0]);
            UiTestExpect(md.Contains("| Host | Skipped") && md.Contains("| Connect | Skipped") && md.Contains("Not connected (a batch run)") && md.Contains("exact") && md.Contains("Game locations on a map"),
                "the game's report doesn't say the batch skipped the server and still scored logic and the pack");
            UiTestExpect(engine.ComponentRequests("AtlasHost").Count == 0 && engine.ComponentRequests("AtlasGenerate").Count == 1 && engine.ComponentRequests("AtlasSeedTest").Count == 1,
                $"the engine saw {engine.ComponentRequests("AtlasHost").Count} host, {engine.ComponentRequests("AtlasGenerate").Count} generate and {engine.ComponentRequests("AtlasSeedTest").Count} seed test requests");
            UiTestExpect(_profiles.Count == profilesBefore, "a batch run made a multiworld");
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitAsync(0.1);
        }
        finally
        {
            AtlasEngine.State.LastCheck = checkBefore;
            Dialog()?.EmitSignal(AcceptDialog.SignalName.Confirmed);
            SetDeveloperModeForTests(developerBefore);
            AP_Atlas.Core.EngineSetup.SoloTestRunner.StopAll();
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            AP_Atlas.Core.SafeFile.Delete(zip);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
            if (System.IO.Directory.Exists(reports)) foreach (string file in System.IO.Directory.GetFiles(reports).Where(f => System.IO.Path.GetFileName(f).StartsWith("Test_Game-") || System.IO.Path.GetFileName(f).StartsWith("batch-"))) System.IO.File.Delete(file);
        }
    }

    private async Task LogicBannerAsync()
    {
        var engine = StartFakeEngine(UiTestPython());
        engine.StartError = ("world_missing", "Test Game isn't installed in the logic engine.");
        engine.Apply();
        await using var server = LogicWorldServer();
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        try
        {
            // Atlas's game list knows Test Game (an apworld and a pack): the banner offers Add this game…, the empty map its pack.
            ApworldSources.UseListForTests(new ApworldSourceList
            {
                Format = "atlas-apworld-sources/1",
                Built = "9999-12-31",
                Source = "ui-test",
                Games = new List<ApworldSource> {
                new ApworldSource { Game = "Test Game", Apworld = "test_game", Repo = "owner/test-game",
                    Versions = { new ApworldVersion { Version = "1.0.0", Url = "https://github.com/owner/test-game/releases/download/v1/test_game.apworld", Sha256 = new string('a', 64), Size = 1000 } },
                    Packs = { new IndexPack { Repo = "packs/test-game", Release = "v1", Asset = "test_game.zip", Sha256 = new string('b', 64), Size = 2000, Fit = 90 } } } }
            });
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            host.ShowTool(AP_Atlas.UI.Tool.LogicTracker);
            await UiTestWaitForAsync(() => slot.EngineProblem?.Code == "world_missing" && slot.LogicNoticeButtons().Contains("Install this game…") ? slot : null, "the banner's Add this game button");
            UiTestExpect(!slot.LogicNoticeButtons().Contains("Set up Atlas Engine…") && !slot.LogicNoticeButtons().Contains("Open the Games page") && slot.MapTracker.AddGameButton.Visible,
                $"the banner offers {string.Join(", ", slot.LogicNoticeButtons())}, or the empty map doesn't offer the listed pack");
            slot.PressLogicNotice("Install this game…");
            var addGames = await UiTestWaitForAsync(() => AP_Atlas.UI.AddGamesDialog.Showing, "Add games from the banner");
            UiTestExpect(addGames.Panel.IsTicked("Test Game"), "Add games didn't open with the game ticked");
            addGames.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            // A game Atlas doesn't know where its logic comes from: the banner keeps the Games page, where its project is added.
            ApworldSources.UseListForTests(null);
            slot.PressLogicNotice("Try again");
            await UiTestWaitForAsync(() => slot.EngineProblem?.Code == "world_missing" && slot.LogicNoticeButtons().Contains("Open the Games page") ? slot : null, "the banner's Games page button");
            slot.PressLogicNotice("Open the Games page");
            await UiTestWaitForAsync(() => ShownContent() == _gamesPage && _gamesPage!.SelectedGame == "Test Game" ? _gamesPage : null, "the Games page on the game");
        }
        finally
        {
            ApworldSources.UseListForTests(null);
            AP_Atlas.UI.AddGamesDialog.Showing?.QueueFree();
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
        }
    }

    private async Task FoundEntrancesAsync()
    {
        // The Locked Door also lies behind the Door Hall, an entrance the world connects only once the game reports it.
        var engine = StartFakeEngine(UiTestPython(), fake =>
        {
            fake.EntranceKey = "fake_entrances_{team}_{player}";
            fake.BehindEntrance[2001] = "Door Hall";
        });
        await using var server = LogicWorldServer();
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            host.ShowTool(AP_Atlas.UI.Tool.LogicTracker);
            await UiTestWaitForAsync(() => slot.LogicSettled ? slot : null, "the slot's logic to start");
            int team = slot.Session.ConnectionInfo.Team, number = slot.Session.ConnectionInfo.Slot;
            string key = $"fake_entrances_{team}_{number}";
            var init = engine.Requests("init").Single();
            UiTestExpect((int?)init["team"] == team && (int?)init["slot"] == number, $"the engine wasn't told the slot's team and number: {init}");
            // And the room's seed, from which the world's rebuild draws its generation seed (the same logic on every start).
            UiTestExpect(!string.IsNullOrEmpty(slot.Session.RoomState.Seed) && (string?)init["seed_name"] == slot.Session.RoomState.Seed, $"the engine wasn't told the room's seed: {init}");
            var keys = slot.Model.Logic.Engine.LastEntranceKeys;
            UiTestExpect(keys.SequenceEqual(new[] { key }), $"the engine named the keys [{string.Join(", ", keys)}], not {key}");
            // The slot asks the server about the key and reads it, as Universal Tracker's client does.
            try
            {
                await UiTestWaitForAsync(() => server.Received.Any(p => (string?)p["cmd"] == "SetNotify" && p["keys"]?.ToObject<string[]>()?.Contains(key) == true)
                    && server.Received.Any(p => (string?)p["cmd"] == "Get" && p["keys"]?.ToObject<string[]>()?.Contains(key) == true) ? server : null,
                    "the slot to ask the server about its found entrances");
            }
            catch (TimeoutException ex)
            {
                var asked = server.Received.Where(p => (string?)p["cmd"] is "SetNotify" or "Get").Select(p => (string?)p["cmd"] + " " + p["keys"]?.ToString(Newtonsoft.Json.Formatting.None));
                throw new InvalidOperationException($"{ex.Message} (the server was asked: {string.Join("; ", asked)})");
            }

            // The Sword alone doesn't open the door: its hall isn't found yet.
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(0, new long[] { 1000 }, flags: 1));
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.Session.Items.AllItemsReceived.Count == 1 && engine.Requests("steps").Count >= 2 ? slot : null, "the Sword's step");
            ExpectLogic(slot, "with the Sword and the hall not found", inLogic: new long[] { 2000 }, outOfLogic: new long[] { 2001, 2002, 2003 }, goal: false, active: 1);

            // The game reports the hall: the engine hears of it, and the door is in logic, opened by the entrances found.
            await server.SetAsync(key, new JArray("Door Hall"));
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.IsLocationInLogic(2001) == true ? slot : null, "the found hall to open the door");
            ExpectLogic(slot, "with the hall found", inLogic: new long[] { 2000, 2001 }, outOfLogic: new long[] { 2002, 2003 }, goal: false, active: 2);
            var told = engine.Requests("entrances")[^1];
            UiTestExpect(told["values"]?[key] is JArray found && found.Select(v => (string?)v).SequenceEqual(new[] { "Door Hall" }) && Ids(told["base"]).SequenceEqual(new long[] { 1000 }),
                $"the engine wasn't told the entrances found with the slot's items: {told}");
            UiTestExpect(slot.UnlockStepOf(2001)?.ItemName == AP_Atlas.Core.LogicStep.EntrancesName, $"the door's step is {slot.UnlockStepOf(2001)}");
            await ExpectTreeAsync(slot, "with the hall found", "── Base Logic (Starting Reachable) ──", "Cave Chest",
                $"── Unlocked by: {AP_Atlas.Core.LogicStep.EntrancesName} (1 checks) ──", "Locked Door");

            // A new engine (Restart logic) hears the entrances found again, without asking the room.
            int told1 = engine.Requests("entrances").Count;
            slot.RetryLogicEngine();
            await UiTestWaitForAsync(() => slot.LogicSettled && engine.Starts == 2 && engine.Requests("entrances").Count > told1 ? slot : null, "the new engine to hear the entrances found");
            ExpectLogic(slot, "after restarting logic", inLogic: new long[] { 2000, 2001 }, outOfLogic: new long[] { 2002, 2003 }, goal: false, active: 2);
            UiTestExpect(server.Count("Set") == 0, "Atlas wrote the room's data storage");
        }
        finally
        {
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
        }
    }

    private async Task LogicYouCanTrustAsync()
    {
        // The rebuilds disagree about the Tower Top, and the rebuilt world lacks the Gem Vault.
        var engine = StartFakeEngine(UiTestPython(), fake =>
        {
            fake.Unsure.Add(2002);
            fake.RebuildLacks.Add(2003);
            fake.RebuildSource = "slot_data";
        });
        await using var server = LogicWorldServer();
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        string? linkedYaml = null;
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            host.ShowTool(AP_Atlas.UI.Tool.LogicTracker);
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.Model.Logic.Trust != null ? slot : null, "the slot's logic to be checked");
            UiTestExpect(engine.Requests("check").Count == 1, $"the engine was asked to check the world {engine.Requests("check").Count} times, not once");
            UiTestExpect(slot.Model.Logic.Trust is { Built: true, Same: false, LogicDiffers: true }, $"the check says {slot.Model.Logic.Trust}");
            // The slot's check is kept for its game and the apworld it ran on (named by its version).
            var kept = await UiTestWaitForAsync(() => GameTrustRecords.Newest("Test Game") is { LogicDiffers: true } r ? r : null, "the slot's check to be kept for its game");
            UiTestExpect(kept.Apworld.Length > 0 && kept.Version != null && kept.Examples.Contains("song_list"), $"the kept check names no apworld or version: {kept.Apworld} {kept.Version}");
            UiTestExpect(slot.Model.Logic.Unsure.SetEquals(new long[] { 2002, 2003 }), $"unsure: {string.Join(", ", slot.Model.Logic.Unsure)}");
            // Neither in nor out of logic: listed under Checklist, with the banner saying why.
            UiTestExpect(slot.IsLocationInLogic(2000) == true && slot.IsLocationInLogic(2001) == false && slot.IsLocationInLogic(2002) == null && slot.IsLocationInLogic(2003) == null,
                $"the unsure checks are called in or out of logic: {string.Join(", ", new long[] { 2000, 2001, 2002, 2003 }.Select(id => $"{id}={slot.IsLocationInLogic(id)?.ToString() ?? "null"}"))}" +
                $" (hidden {slot.LogicHidden}, running {slot.EngineRunning}, shown anyway {slot.Model.ShowUnsureLogic}, unsure shown [{string.Join(", ", slot.Model.UnsureShown)}])");
            UiTestExpect(slot.IsLocationUnsure(2002) && !slot.IsLocationUnsure(2001), "Properties wouldn't call the Tower Top unsure");
            await ExpectTreeAsync(slot, "with logic unsure about two checks", "── Base Logic (Starting Reachable) ──", "Cave Chest",
                "── Checklist: logic unsure for this seed (2) ──", "Tower Top", "Gem Vault");
            UiTestExpect(slot.UnsureBannerText?.Contains("Logic unsure: 2 checks") == true, $"the banner says \"{slot.UnsureBannerText}\"");
            // One banner: the Gem Vault the rebuilt world lacks is named in its tooltip with what differs, and the
            // approximate-logic banner stays away (Properties still says it).
            UiTestExpect(slot.UnsureBannerTip?.Contains("Not in the rebuilt world: Gem Vault") == true && slot.UnsureBannerTip.Contains("song_list"),
                $"the banner's tooltip says \"{slot.UnsureBannerTip}\"");
            UiTestExpect(slot.AccuracyBannerShown == null && slot.LogicAccuracyWarning?.Contains("(1 locations missing, 0 extra)") == true,
                $"the accuracy banner shows \"{slot.AccuracyBannerShown}\"; the warning is \"{slot.LogicAccuracyWarning}\"");
            // Rebuilt from the slot data's options with no YAML linked: the one banner offers Link YAML…, and the warning says so.
            UiTestExpect(slot.UnsureBannerLinksYaml && slot.LogicAccuracyWarning?.Contains("Link the YAML used for the seed") == true,
                $"with no YAML linked, the banner offers Link YAML… {slot.UnsureBannerLinksYaml}; the warning is \"{slot.LogicAccuracyWarning}\"");
            // A YAML linked: neither asks for one any more (from the next answer on).
            linkedYaml = System.IO.Path.Combine(DataManager.GetDataDirectory(), "Tester linked.yaml");
            AP_Atlas.Core.SafeFile.WriteAllText(linkedYaml, "name: Tester\ngame: Test Game\n");
            _appSettings.SlotYamlPaths[slot.AnnotationKey] = linkedYaml;

            // The Sword and the Shield: the door comes into logic; the tower stays unsure.
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(0, new long[] { 1000, 1001 }, flags: 1));
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.IsLocationInLogic(2001) == true ? slot : null, "the Sword to open the door");
            UiTestExpect(slot.IsLocationInLogic(2002) == null && slot.ActiveLogicCount == 2, $"the tower's logic is {slot.IsLocationInLogic(2002)}; {slot.ActiveLogicCount} in logic");
            UiTestExpect(!slot.UnsureBannerLinksYaml && slot.LogicAccuracyWarning?.Contains("Link the YAML") == false && slot.UnsureBannerText != null,
                $"with a YAML linked, the banner offers Link YAML… {slot.UnsureBannerLinksYaml}; the warning is \"{slot.LogicAccuracyWarning}\"");

            // Show logic anyway: the tower shows as the rebuild sees it, remembered for the slot; then back to the checklist.
            slot.PressUnsureBanner();
            await UiTestWaitForAsync(() => slot.IsLocationInLogic(2002) == true ? slot : null, "Show logic anyway to show the tower's logic");
            UiTestExpect(DataManager.LoadSettings().ShowUnsureLogic.Contains(slot.AnnotationKey) || _appSettings.ShowUnsureLogic.Contains(slot.AnnotationKey),
                "Show logic anyway wasn't remembered for the slot");
            await ExpectTreeAsync(slot, "with unsure logic shown", "── Base Logic (Starting Reachable) ──", "Cave Chest",
                "── Unlocked by: Sword (1 checks) ──", "Locked Door", "── Unlocked by: Shield (1 checks) ──", "Tower Top");
            UiTestExpect(slot.UnsureBannerText?.Contains("Showing unsure logic: 2 checks") == true, $"the banner says \"{slot.UnsureBannerText}\"");
            slot.PressUnsureBanner();
            await UiTestWaitForAsync(() => slot.IsLocationInLogic(2002) == null ? slot : null, "List them as a checklist to hide the tower's logic again");

            // Check this game (the Games page's check of one game) says up front that some checks will be unsure, and so
            // does Add a slot's line about the game.
            var checkBefore = AtlasEngine.State.LastCheck;
            AtlasEngine.State.LastCheck = new EngineCheckResult { Games = new List<string> { "Test Game" }, TrackerLoads = true };
            try
            {
                var install = AtlasEngine.Resolve(_appSettings);
                await GameSweep.RunAsync(install, _ => { }, System.Threading.CancellationToken.None, new[] { "Test Game" });
                UiTestExpect((bool?)engine.ComponentRequests("AtlasGameSweep").LastOrDefault()?["trust"] == true, "Check this game didn't ask whether a slot's logic is the seed's");
                var trust = GameSweep.LastFor(install, "Test Game")?.Trust;
                UiTestExpect(trust is { LogicDiffers: true } && trust.Summary.Contains("unsure for every seed"), $"the check says: {trust?.Summary ?? "nothing"}");
                UiTestExpect(SlotGameStatus("Test Game").Text.Contains("unsure for every seed"), $"Add a slot says: {SlotGameStatus("Test Game").Text}");
                UiTestExpect(SlotGameStatus("Test Game").Text.Contains("Found by a slot's logic on"), $"Add a slot doesn't say whose check it is: {SlotGameStatus("Test Game").Text}");
                host.ShowTool(AP_Atlas.UI.Tool.Games);
                _gamesPage!.Select("Test Game");
                UiTestExpect(_gamesPage.TrustLine.Contains("unsure for every seed") && _gamesPage.TrustLine.Contains("Found by a slot's logic on"), $"the game's page says: {_gamesPage.TrustLine}");
                // Without a slot's check, both say what Check this game found, dated by that game's own check.
                GameTrustRecords.ForgetForTests("Test Game");
                string today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture); // wall clock: the check's date
                string status = SlotGameStatus("Test Game").Text;
                _gamesPage.Select("Test Game");
                UiTestExpect(status.Contains("unsure for every seed") && status.Contains("Found by Check this game on " + today) && !status.Contains('{'),
                    $"without a slot's check, Add a slot says: {status}");
                UiTestExpect(_gamesPage.TrustLine.Contains("Found by Check this game on " + today) && !_gamesPage.TrustLine.Contains('{'), $"without a slot's check, the game's page says: {_gamesPage.TrustLine}");
                host.ShowTool(AP_Atlas.UI.Tool.LogicTracker);
            }
            finally
            {
                AtlasEngine.State.LastCheck = checkBefore;
            }

            // A world the engine can't rebuild: every check left is listed as a checklist under the notice.
            engine.StartError = ("generation_failed", "None of the YAMLs tried could rebuild this world.");
            engine.StartReason = "Needs Archipelago 0.6.8";
            engine.Apply();
            _appSettings.SlotYamlPaths.Remove(slot.AnnotationKey);
            slot.RetryLogicEngine();
            await UiTestWaitForAsync(() => slot.EngineProblem?.Code == "generation_failed" ? slot : null, "the rebuild to fail");
            // Why, in a few words; nothing linked, so Link YAML….
            UiTestExpect(slot.LogicNoticeShown?.StartsWith("✖ World not rebuilt: Needs Archipelago 0.6.8") == true && slot.LogicNoticeButtons().Contains("Link YAML…"),
                $"the notice says \"{slot.LogicNoticeShown}\" with [{string.Join(", ", slot.LogicNoticeButtons())}]");
            await ExpectTreeAsync(slot, "with logic not running", "── Checklist: logic isn't running (4) ──",
                "Cave Chest", "Locked Door", "Tower Top", "Gem Vault");
        }
        finally
        {
            _appSettings.ShowUnsureLogic.Clear();
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (linkedYaml != null)
            {
                foreach (var key in _appSettings.SlotYamlPaths.Where(entry => entry.Value == linkedYaml).Select(entry => entry.Key).ToList()) _appSettings.SlotYamlPaths.Remove(key);
                System.IO.File.Delete(linkedYaml);
            }
            DeleteProfile(profile);
        }
    }

    /// <summary>The seed's priority checks: known at start, ★ on rows, pins and Properties, the card's count, Priority only.</summary>
    private async Task PriorityChecksAsync()
    {
        // The Locked Door and the Gem Vault are priority checks, the Tower Top excluded; the YAML's 0.6.8 stamp was ignored.
        var engine = StartFakeEngine(UiTestPython(), fake =>
        {
            fake.Priority.AddRange(new long[] { 2001, 2003 });
            fake.Excluded.Add(2002);
            fake.RelaxedRequires = "0.6.8";
        });
        await using var server = LogicWorldServer();
        server.SlotName = "Quill";
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Quill");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        bool priorityOnlyBefore = _appSettings.LogicPriorityOnly;
        try
        {
            _appSettings.LogicPriorityOnly = false;
            await OnConnectSlotPressedAsync("Quill", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Quill"), "the slot's view");
            host.ShowTool(AP_Atlas.UI.Tool.LogicTracker);
            await UiTestWaitForAsync(() => slot.LogicSettled ? slot : null, "the slot's logic");
            UiTestExpect(slot.HasPriorityChecks && slot.IsPriorityCheck(2001) && slot.IsPriorityCheck(2003) && !slot.IsPriorityCheck(2000), "the priority checks aren't known");
            UiTestExpect(slot.IsExcludedBySeed(2002), "the seed's excluded check isn't known");
            UiTestExpect(slot.PriorityOnlyOffered && slot.PriorityInLogicCount == 0, $"Priority only offered {slot.PriorityOnlyOffered}; {slot.PriorityInLogicCount} in logic with nothing received");
            UiTestExpect(slot.MapTracker.MarkerLookup(2001).Priority && !slot.MapTracker.MarkerLookup(2000).Priority, "the map's pins don't know which checks are priority");
            // Converted once: Properties reads the slot data on every render.
            UiTestExpect(slot.SlotDataJson != null && ReferenceEquals(slot.SlotDataJson, slot.SlotDataJson), "the slot data is converted again on every read");

            // The Sword opens the Locked Door: a priority check in logic, starred, counted on the slot card.
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(0, new long[] { 1000 }, flags: 1));
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.IsLocationInLogic(2001) == true ? slot : null, "the Sword to open the door");
            UiTestExpect(slot.PriorityInLogicCount == 1, $"{slot.PriorityInLogicCount} priority checks in logic, not 1");
            UiTestExpect(slot.LogicRow(2001)?.GetIcon(1)?.GetInstanceId() == AP_Atlas.Core.Annotations.MarkerIcon(0, false, false, priority: true).GetInstanceId() && slot.LogicRow(2000)?.GetIcon(1) == null,
                "the priority row has no ★, or another row has one");
            await UiTestWaitForAsync(() => _activeSessionsList.FindChildren("PriorityKpi", nameof(VBoxContainer), true, false).OfType<VBoxContainer>()
                .FirstOrDefault(c => c.Visible && c.GetChildOrNull<Label>(1)?.Text == "1"), "the slot card to count 1 priority check in logic");

            // Priority only lists the starred row alone, remembered; off, every row again.
            slot.ShowPriorityOnly(true);
            UiTestExpect(_appSettings.LogicPriorityOnly && slot.LogicRowShown(2001) && !slot.LogicRowShown(2000), "Priority only lists another row, or wasn't remembered");
            UiTestExpect(!slot.MapTracker.MarkerLookup(2002).Priority, "a non-priority check has a star");
            // Find in the Logic Tracker on a check without a star switches Priority only off, so the row shows.
            slot.RevealLogicRow(2000);
            UiTestExpect(slot.LogicRowShown(2000) && !_appSettings.LogicPriorityOnly, "Find in the Logic Tracker left Priority only hiding the Cave Chest");
            slot.ShowPriorityOnly(true);
            slot.ShowPriorityOnly(false);
            UiTestExpect(slot.LogicRowShown(2001) && slot.LogicRowShown(2000), "Priority only off didn't list every row again");

            // Properties: the check says so; the slot counts them, and says the YAML's stamp was ignored.
            AP_Atlas.Core.Inspector.Inspect(slot.LocationTarget(2001));
            await UiTestWaitForAsync(() => _propertiesPanel.SectionBody("Overview") is { } body && ShowsText(body, "★ Priority") ? slot : null, "the Locked Door's Properties to say it's a priority check");
            AP_Atlas.Core.Inspector.Inspect(AP_Atlas.Core.InspectTarget.ForSlot(profile.Id, "Quill"));
            await UiTestWaitForAsync(() => _propertiesPanel.SectionBody("Progress") is { } body && ShowsText(body, "★ Priority in logic") ? slot : null, "the slot's Properties to count its priority checks");
            UiTestExpect(ShowsText(_propertiesPanel.SectionBody("Accuracy")!, "Archipelago 0.6.8 (ignored)"), "Properties doesn't say the YAML's version stamp was ignored");

            // Race mode hiding logic hides the stars too: rows, pins, Properties and the card ask the same gate.
            AP_Atlas.Core.RaceRules.SetMode(AP_Atlas.Core.RaceModeSetting.AlwaysOn);
            AP_Atlas.Core.RaceRules.SetHideAllLogic(true);
            UiTestExpect(!slot.IsPriorityCheck(2001) && !slot.HasPriorityChecks && !slot.MapTracker.MarkerLookup(2001).Priority && slot.PriorityInLogicCount == 0,
                "race mode shows the priority checks");
            // A marker change while logic is hidden redraws the rows' icons without the star.
            var star = AP_Atlas.Core.Annotations.MarkerIcon(0, false, false, priority: true).GetInstanceId();
            slot.SetChecksHidden(new long[] { 2000 }, true);
            slot.SetChecksHidden(new long[] { 2000 }, false);
            await UiTestWaitForAsync(() => slot.LogicRow(2001) is { } hiddenRow && hiddenRow.GetIcon(1)?.GetInstanceId() != star ? slot : null, "the priority row drawn without its ★ under race mode");
            AP_Atlas.Core.RaceRules.SetHideAllLogic(false);
            AP_Atlas.Core.RaceRules.SetMode(AP_Atlas.Core.RaceModeSetting.FollowServer);
            UiTestExpect(slot.IsPriorityCheck(2001), "race mode off didn't show the priority checks again");
            await UiTestWaitForAsync(() => slot.LogicRow(2001)?.GetIcon(1)?.GetInstanceId() == star ? slot : null, "the priority row's ★ to come back after race mode");
        }
        finally
        {
            AP_Atlas.Core.RaceRules.SetHideAllLogic(false);
            AP_Atlas.Core.RaceRules.SetMode(AP_Atlas.Core.RaceModeSetting.FollowServer);
            _appSettings.LogicPriorityOnly = priorityOnlyBefore;
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
        }
    }

    private async Task HomeAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var home = _homePage ?? throw new InvalidOperationException("Home wasn't built.");
        host.ShowTool(AP_Atlas.UI.Tool.Games);
        await PressAsync("Ctrl+1");
        UiTestExpect(ShownContent() == home && _activityBar.Selected == AP_Atlas.UI.Tool.Home && !_midLeftSidebar.Visible, "Ctrl+1 didn't show Home on its own");
        // Every tool but Home has a button with a line of its own; every link is https, and the Discord one is the official invite.
        var noBlurb = AP_Atlas.UI.Tool.All.Where(t => t != AP_Atlas.UI.Tool.Home && AP_Atlas.UI.HomePage.Blurb(t).Length == 0).Select(t => t.Title).ToList();
        UiTestExpect(noBlurb.Count == 0, $"tools without a line: {string.Join(", ", noBlurb)}");
        UiTestExpect(home.Links.Count > 0 && home.Links.All(l => l.Url.StartsWith("https://", StringComparison.Ordinal)) && home.Links.Any(l => l.Url == "https://discord.gg/8Z65BR2"),
            "a link isn't https, or the Discord one is missing");
        // A first start: Home greets it with the welcome card (nothing pops up), the multiworlds card offers Add a multiworld…,
        // and the setup line counts nothing done (the engine's step follows the engine) with Resume beside it.
        var steps = SetupSteps;
        UiTestExpect(home.WelcomeShown && home.WelcomeSetUpButton.Visible && home.RecentProfileIds.Count == 0 && home.SetUpButton == null && home.AddMultiworldButton != null,
            "a first start isn't greeted by the welcome card, or the setup's button shows twice");
        // Add games… sits beside New… and Paste a room link… always, and beside Add a multiworld… on a first start; it opens over Home.
        UiTestExpect(home.AddGamesFirstButton != null && home.AddGamesButton.Visible, "Home doesn't offer Add games… on a first start");
        home.AddGamesFirstButton!.EmitSignal(BaseButton.SignalName.Pressed);
        var homeAdd = await UiTestWaitForAsync(() => AP_Atlas.UI.AddGamesDialog.Showing, "Add games from Home's first start");
        UiTestExpect(ShownContent() == home, "Add games… left Home");
        homeAdd.EmitSignal(AcceptDialog.SignalName.Canceled);
        await UiTestWaitAsync(0.1);
        UiTestExpect(AP_Atlas.UI.AddGamesDialog.Showing == null, "closing Add games left it open");
        UiTestExpect(!GetChildren().OfType<AP_Atlas.UI.SetupWizard>().Any(w => !w.IsQueuedForDeletion()), "the setup window opened by itself");
        UiTestExpect(steps.Done(AP_Atlas.UI.SetupSteps.Engine) == AP_Atlas.Core.EngineSetup.AtlasEngine.Current.CanLaunch
            && AP_Atlas.UI.SetupSteps.All.Where(s => s != AP_Atlas.UI.SetupSteps.Engine).All(s => !steps.Done(s) && !steps.Skipped(s)),
            $"steps done before anything happened: {string.Join(", ", AP_Atlas.UI.SetupSteps.All.Where(steps.Settled).Select(s => s.Id))}");
        UiTestExpect(home.SetupLine.StartsWith("Setup ", StringComparison.Ordinal) && home.SetupLine.Contains($" of {steps.Total} done") && home.ResumeButton.Visible, $"the setup line reads \"{home.SetupLine}\"");
        // Set up Atlas opens the setup window on its Welcome page while nothing is set up, on Home; closing it leaves Home as it was.
        home.WelcomeSetUpButton.EmitSignal(BaseButton.SignalName.Pressed);
        var wizard = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.SetupWizard>().FirstOrDefault(w => w.Visible && !w.IsQueuedForDeletion()), "the setup window from Home");
        UiTestExpect(wizard.CurrentPageId == "welcome" && ShownContent() == home, $"Set up Atlas… opened the window on {wizard.CurrentPageId}, or left Home");
        wizard.EmitSignal(Window.SignalName.CloseRequested);
        await UiTestWaitAsync(0.2);
        UiTestExpect(!IsInstanceValid(wizard) || wizard.IsQueuedForDeletion(), "closing the setup window didn't free it");
        // Look around first puts the welcome card away (remembered); the multiworlds card offers Set up Atlas… then.
        home.LookAroundButton.EmitSignal(BaseButton.SignalName.Pressed);
        UiTestExpect(!home.WelcomeShown && _appSettings.WelcomeDismissed && home.SetUpButton != null && home.ResumeButton.Visible,
            "Look around first didn't put the welcome card away, or nothing offers the setup afterwards");
        _appSettings.WelcomeDismissed = false;
        home.Refresh();
        // The block's "Add a multiworld…" takes the user to the Multiworlds page with the new one selected, its name ready to
        // type; a second Add selects it again instead of adding another.
        int beforeAdd = _profiles.Count;
        home.AddMultiworldButton!.EmitSignal(BaseButton.SignalName.Pressed);
        UiTestExpect(ShownContent() == _connectionPanel && _selectedProfile != null && _selectedProfile.Name == "New Multiworld" && _profiles.Count == beforeAdd + 1,
            "Add a multiworld… didn't show the new multiworld on the Multiworlds page");
        UiTestExpect(_nameInput.HasFocus(), "the new multiworld's name box isn't ready to type into");
        var untouched = _selectedProfile;
        OnAddProfilePressed();
        UiTestExpect(_profiles.Count == beforeAdd + 1 && _selectedProfile == untouched, "a second Add made another untouched multiworld");
        DeleteProfile(untouched);
        host.ShowTool(AP_Atlas.UI.Tool.Home);
        // The engine changing (setup finished) makes Home re-read its state: the setup line follows without a visit.
        int refreshes = home.RefreshCount;
        AP_Atlas.Core.EngineSetup.AtlasEngine.NotifyChanged();
        await UiTestWaitAsync(0.1);
        UiTestExpect(home.RefreshCount > refreshes, "Home didn't re-read Atlas's state after an engine change");
        // A tip shows; Next tip shows the next one, around the end.
        int tip = home.TipIndex;
        UiTestExpect(tip >= 0 && home.TipText == AP_Atlas.UI.HomePage.Tips[tip], "no tip shows");
        home.NextTip();
        UiTestExpect(home.TipIndex == (tip + 1) % AP_Atlas.UI.HomePage.Tips.Length && home.TipText == AP_Atlas.UI.HomePage.Tips[home.TipIndex], "Next tip didn't show the next one");

        await using var server = new FakeArchipelagoServer();
        server.Slots.Add("Quill"); // the slot the quick dialog adds (no "Tester" in it: the crash report scenario checks its log for that word)
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
            // A multiworld with a server and a named slot counts as done when Home shows again; a pack alone doesn't make the
            // games step done (a slot's game must be known, from its first connect, and ready). The setup line names the first
            // step left, and the multiworld is listed with its line and a Connect button.
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            host.ShowTool(AP_Atlas.UI.Tool.Home);
            UiTestExpect(steps.Done(AP_Atlas.UI.SetupSteps.Multiworld) && !steps.Done(AP_Atlas.UI.SetupSteps.Games),
                $"after a multiworld and a pack, the steps done are {string.Join(", ", AP_Atlas.UI.SetupSteps.All.Where(steps.Done).Select(s => s.Id))}");
            UiTestExpect(home.SetupLine.Contains("Next: " + steps.Next!.Title + "."), $"the setup line doesn't name the first step left: \"{home.SetupLine}\"");
            UiTestExpect(home.RecentProfileIds.SequenceEqual(new[] { profile.Id }) && !home.ConnectButtonOf(profile.Id).Disabled && home.SetUpButton == null, "the new multiworld isn't listed with a Connect button");
            string line = home.RowLineOf(profile.Id);
            UiTestExpect(line.Contains(server.Url.ToString()) && line.Contains("Slots: 1"), $"the multiworld's line reads \"{line}\"");
            // One click connects its slot; the row says so with its chip, and the button says Connected.
            home.ConnectButtonOf(profile.Id).EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            await UiTestWaitForAsync(() => home.ConnectButtonOf(profile.Id).Disabled ? home : null, "the row to say the slot is connected");
            UiTestExpect(home.ConnectButtonOf(profile.Id).Text == "Connected" && home.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(l => l.Text == "1 connected"),
                "a connected multiworld's row still offers to connect, or lacks its chip");
            UiTestExpect(profile.SavedStats.TryGetValue("Tester", out var learnt) && learnt.GameName == "Test Game", "the slot's game wasn't learnt from the room at connect");
            // Open shows that multiworld on the Multiworlds page.
            home.OpenButtonOf(profile.Id).EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(ShownContent() == _connectionPanel && _selectedProfile == profile, "Open didn't show the multiworld on its page");
            host.ShowTool(AP_Atlas.UI.Tool.Home);
            // A tool's button shows the tool.
            home.ToolCardOf(AP_Atlas.UI.Tool.Games).EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(ShownContent() == _gamesPage, "the Games button didn't show Games");
            host.ShowTool(AP_Atlas.UI.Tool.Home);
            // The columns: side by side when the page is wide enough, and the right column ends inside the page either way.
            await UiTestWaitAsync(0.2);
            UiTestExpect(home.TwoColumns == (home.Size.X - 40 >= AP_Atlas.UI.HomePage.TwoColumnsFrom) && home.RightColumnRight <= home.GlobalPosition.X + home.Size.X + 1,
                $"Home's columns: two {home.TwoColumns} in a page {home.Size.X} wide, the right column ending at {home.RightColumnRight}");
            // New…: a small dialog on Home (no page jump); Create saves the multiworld with its slot and selects it.
            AcceptDialog? QuickDialog(string id) => GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => d.HasMeta("quick_setup") && d.GetMeta("quick_setup").AsString() == id && !d.IsQueuedForDeletion());
            int before = _profiles.Count;
            ((Button)home.FindChild("NewMultiworld", true, false)).EmitSignal(BaseButton.SignalName.Pressed);
            var newDialog = await UiTestWaitForAsync(() => QuickDialog("new-multiworld"), "the New multiworld dialog");
            UiTestExpect(ShownContent() == home, "New… left Home");
            await UiTestDialogSizedAsync(newDialog, "the New multiworld dialog");
            UiTestExpect(newDialog.FindChild("AddYamlButton", true, false) == null && !newDialog.FindChildren("*", nameof(Button), true, false).OfType<Button>().Any(b => b.Text.Contains("YAML")), "New multiworld still offers a YAML");
            ((LineEdit)newDialog.FindChild("NameBox", true, false)).Text = "Quick MW";
            ((LineEdit)newDialog.FindChild("ServerBox", true, false)).Text = server.Url.ToString();
            var slotsBox = (VBoxContainer)newDialog.FindChild("SlotsBox", true, false);
            slotsBox.GetChildren().OfType<HBoxContainer>().First().GetChild<LineEdit>(0).Text = "Quill";
            newDialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            var added = _profiles.FirstOrDefault(p => p.Name == "Quick MW");
            UiTestExpect(added != null && _profiles.Count == before + 1 && added.Slots.SequenceEqual(new[] { "Quill" }) && added.ServerUrl == server.Url.ToString() && _selectedProfile == added
                && DataManager.LoadProfiles().Any(p => p.Id == added.Id && p.Slots.Contains("Quill")) && ShownContent() == home && QuickDialog("new-multiworld") == null,
                "the New multiworld dialog didn't make the multiworld with its slot, saved and selected, with Home staying");
            UiTestExpect(home.RecentProfileIds.Contains(added!.Id), "the new multiworld isn't listed");
            // Paste a room link… makes a new multiworld on the Multiworlds page with its room link box ready.
            ((Button)home.FindChild("RoomLinkMultiworld", true, false)).EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(ShownContent() == _connectionPanel && _selectedProfile != null && _selectedProfile.Name == "New Multiworld" && _roomLinkInput.HasFocus(), "Paste a room link… didn't ready the room link box");
            DeleteProfile(_selectedProfile!);
            host.ShowTool(AP_Atlas.UI.Tool.Home);
            // Every step settled (a page whose steps say so): "You're set up.", no Resume.
            var done = new AP_Atlas.UI.HomePage(text => Tr(text), new AP_Atlas.UI.HomePage.Hooks
            {
                Profiles = () => new[] { added },
                Steps = new AP_Atlas.UI.SetupSteps(new AP_Atlas.UI.SetupSteps.Hooks
                {
                    EngineReady = () => true,
                    Profiles = () => new[] { added },
                    SlotGamesReady = () => true,
                }),
            });
            AddChild(done);
            done.Refresh();
            UiTestExpect(done.SetupLine == "You're set up." && !done.ResumeButton.Visible, $"with every step settled, the line reads \"{done.SetupLine}\" and Resume shows {done.ResumeButton.Visible}");
            RemoveChild(done);
            done.QueueFree();
        }
        finally
        {
            AP_Atlas.Core.SafeFile.Delete(zip);
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
            foreach (var quick in _profiles.Where(p => p.Name == "Quick MW").ToList()) DeleteProfile(quick);
        }
    }

    private async Task SetupWizardAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        const string roomId = "AbCdEfGhIjKlMnOpQrStUw"; // a base64 UUID, as archipelago.gg names rooms
        var pagesBefore = _appSettings.SetupPagesDone.ToList();
        var skippedBefore = _appSettings.SkippedHomeSteps.ToList();
        // The fakes: the engine's setup, the room's status page and the room's server.
        int runs = 0;
        AtlasEngine.TestSetUp = async (log, progress, ct) =>
        {
            runs++;
            AtlasEngine.ReportStep("Downloading Python 3.12.10 from python.org…");
            progress(0.5f);
            await Task.Delay(150, ct);
            AtlasEngine.ReportStep("Running the health check…");
            return true;
        };
        await using var server = new FakeArchipelagoServer();
        server.Slots.Add("Lantern");
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        await using var site = new FakeWebSite();
        site.Respond = path => path == "/api/room_status/" + roomId
            ? (200, """{"tracker": "x", "players": [["Lantern", "Bob"]], "last_port": 40123, "last_activity": "Sun, 06 Nov 1994 08:49:37 GMT", "timeout": 7200, "downloads": []}""")
            : (404, "");
        string roomLink = site.Site + "/room/" + roomId;
        AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, false);
        AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.RoomStatusReads, null, false);
        AP_Atlas.Core.Permissions.ResetSessionForTests();
        _appSettings.SetupPagesDone.Clear();
        _appSettings.SkippedHomeSteps.Clear();
        AP_Atlas.UI.SetupWizard? Wizard() => GetChildren().OfType<AP_Atlas.UI.SetupWizard>().FirstOrDefault(w => !w.IsQueuedForDeletion());
        List<string> ReadyLines(AP_Atlas.UI.SetupWizard w) => w.PageOf("ready").FindChildren("*", nameof(Label), true, false).OfType<Label>().Where(l => l.Visible && !l.IsQueuedForDeletion()).Select(l => l.Text).ToList();
        // Nothing opens on top of the setup window: no other window of the main window's, and none of the root's beyond those there now.
        var rootWindowsBefore = GetTree().Root.GetChildren().OfType<Window>().ToList();
        try
        {
            // Atlas's game list knows the slot's game, so Add games can tick it (nothing downloads: the engine isn't set up).
            ApworldSources.UseListForTests(new ApworldSourceList
            {
                Format = "atlas-apworld-sources/1",
                Built = "9999-12-31",
                Source = "ui-test",
                Games = new List<ApworldSource>
                {
                    new ApworldSource { Game = "Test Game", Apworld = "test_game", Repo = "owner/test-game",
                        Versions = { new ApworldVersion { Version = "1.0.0", Url = site.Site + "/dl/test_game.apworld", Sha256 = new string('0', 64), Size = 1000 } } }
                }
            });
            host.ShowTool(AP_Atlas.UI.Tool.Home);
            // Untouched while no multiworld has a server and a named slot (an earlier scenario's leftover would make it begun).
            var leftovers = _profiles.Where(p => !string.IsNullOrWhiteSpace(p.ServerUrl) && p.Slots.Any(s => !AP_Atlas.UI.SetupSteps.IsPlaceholderSlot(s))).Select(p => p.Name).ToList();
            UiTestExpect(SetupSteps.Untouched == (leftovers.Count == 0 && !SetupSteps.Done(AP_Atlas.UI.SetupSteps.Games)),
                $"the setup counts as untouched {SetupSteps.Untouched} with multiworlds set up: {string.Join(", ", leftovers)}");
            // Another multiworld, for the page's choice: its slot mustn't follow a change of choice.
            var other = new MultiworldProfile { Name = "Wizard Other", ServerUrl = "localhost:2" };
            other.Slots.Clear();
            other.Slots.Add("Alice");
            _profiles.Add(other);
            // The Welcome page: no Back or Skip, Start leads on; the pages are Welcome, the engine's (run from source without one),
            // Your multiworld and slots, Add games and Ready.
            var wizard = OpenSetupWizard(resume: false);
            await UiTestWaitAsync(0.3);
            UiTestExpect(wizard.CurrentPageId == "welcome" && !wizard.Exclusive && !wizard.BackButton.Visible && !wizard.SkipButton.Visible && wizard.NextButton.Text == "Start",
                $"the setup window opened on {wizard.CurrentPageId}, or the Welcome page has Back or Skip, or the window is exclusive");
            UiTestExpect(wizard.PageIdsShown.SequenceEqual(new[] { "welcome", AP_Atlas.UI.SetupSteps.Engine.Id, AP_Atlas.UI.SetupSteps.Multiworld.Id, AP_Atlas.UI.SetupSteps.Games.Id, "ready" }),
                $"the setup window's pages: {string.Join(", ", wizard.PageIdsShown)}");
            await wizard.NextForTestsAsync();
            // The engine: the permission is a row on the page; Allow once starts the run, whose progress shows in the rail, and the window moves on.
            UiTestExpect(wizard.CurrentPageId == AP_Atlas.UI.SetupSteps.Engine.Id, "Start didn't lead to the engine page");
            var enginePage = wizard.PageOf(AP_Atlas.UI.SetupSteps.Engine.Id);
            var consent = enginePage.FindChildren("*", nameof(PanelContainer), true, false).OfType<PanelContainer>().FirstOrDefault(p => p.HasMeta("consent") && p.GetMeta("consent").AsString() == AP_Atlas.Core.Permissions.EngineSetup.Id);
            UiTestExpect(consent != null && consent.Visible && runs == 0, "the engine page doesn't ask the permission on the page before starting");
            UiTestNothingOnTopOfSetup(rootWindowsBefore, "on the engine page");
            ((Button)consent!.FindChild("AllowOnce", true, false)).EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(1.0);
            UiTestExpect(wizard.EngineRun.Visible && (wizard.EngineRun.Running || wizard.EngineRun.Succeeded),
                $"Allow once didn't start the run: visible {wizard.EngineRun.Visible}, running {wizard.EngineRun.Running}, succeeded {wizard.EngineRun.Succeeded}, step \"{wizard.EngineRun.StepText}\", outcome \"{wizard.EngineRun.OutcomeText}\", allowed {AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.EngineSetup)}, runs {runs}, engine busy {AtlasEngine.SetupRunning}");
            await UiTestWaitForAsync(() => wizard.EngineRun.StepText.Contains("Downloading Python") || wizard.EngineRun.StepText.Contains("Ready") ? wizard : null, "the rail to show the run's first step");
            UiTestExpect(AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.EngineSetup) && !AP_Atlas.Core.Permissions.IsAlwaysAllowed(_appSettings, AP_Atlas.Core.Permissions.EngineSetup), "Allow once wasn't kept for the session only");
            await wizard.NextForTestsAsync();
            UiTestExpect(wizard.CurrentPageId == AP_Atlas.UI.SetupSteps.Multiworld.Id, "the window didn't move on while the engine installs");
            await UiTestWaitForAsync(() => !wizard.EngineRun.Running && wizard.EngineRun.Succeeded ? wizard : null, "the pretend setup to finish");
            UiTestExpect(runs == 1 && wizard.EngineRun.OutcomeText.Contains("is set up"), $"the run ended with \"{wizard.EngineRun.OutcomeText}\" after {runs} run(s)");
            // Your multiworld and slots: no YAML and no game box; each slot's game comes from the room.
            var multiworldPage = wizard.PageOf(AP_Atlas.UI.SetupSteps.Multiworld.Id);
            UiTestExpect(multiworldPage.FindChild("AddYamlButton", true, false) == null && !multiworldPage.FindChildren("*", nameof(Button), true, false).OfType<Button>().Any(b => b.Text.Contains("YAML"))
                && !multiworldPage.FindChildren("*", nameof(LineEdit), true, false).OfType<LineEdit>().Any(e => e.PlaceholderText.StartsWith("Game", StringComparison.Ordinal)),
                "the multiworld page still offers a YAML or a game box");
            // Choosing another multiworld fills in its slot; choosing New again takes it away (nothing of the other is saved).
            var slotsBox = (VBoxContainer)wizard.FindChild("SlotsBox", true, false);
            List<string> Rows() => slotsBox.GetChildren().OfType<HBoxContainer>().Where(r => !r.IsQueuedForDeletion()).Select(r => r.GetChild<LineEdit>(0).Text).ToList();
            var which = (OptionButton)wizard.FindChild("WhichMultiworld", true, false);
            int otherIndex = Enumerable.Range(0, which.ItemCount).First(i => which.GetItemText(i) == "Wizard Other");
            which.Selected = otherIndex;
            which.EmitSignal(OptionButton.SignalName.ItemSelected, otherIndex);
            UiTestExpect(Rows().Contains("Alice") && ((LineEdit)wizard.FindChild("NameBox", true, false)).Text == "Wizard Other", $"choosing another multiworld shows the rows [{string.Join(", ", Rows())}]");
            which.Selected = 0;
            which.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
            UiTestExpect(!Rows().Contains("Alice") && Rows().Count == 1 && ((LineEdit)wizard.FindChild("NameBox", true, false)).Text == "",
                $"choosing New again kept the other multiworld's rows [{string.Join(", ", Rows())}]");
            // A room link brings the status read's row; allowed, the room is read once at once (the server and the players to tick).
            ((LineEdit)wizard.FindChild("NameBox", true, false)).Text = "Wizard MW";
            var roomBox = (LineEdit)wizard.FindChild("RoomLinkBox", true, false);
            roomBox.Text = roomLink;
            roomBox.EmitSignal(LineEdit.SignalName.TextChanged, roomLink);
            var roomConsent = multiworldPage.FindChildren("*", nameof(PanelContainer), true, false).OfType<PanelContainer>().FirstOrDefault(p => p.HasMeta("consent") && p.GetMeta("consent").AsString() == AP_Atlas.Core.Permissions.RoomStatusReads.Id);
            UiTestExpect(roomConsent != null && roomConsent.Visible && site.Requests.Count == 0, $"a room link didn't bring the status read's row (or read before it was allowed): parsed {AP_Atlas.Core.Rooms.RoomLinks.Parse(roomLink, out var parseError) != null} ({parseError}), allowed {AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.RoomStatusReads)}, row {(roomConsent == null ? "none" : roomConsent.Visible.ToString())}, host visible {roomConsent?.GetParent<Control>().Visible}, requests {site.Requests.Count}");
            ((Button)roomConsent!.FindChild("AllowOnce", true, false)).EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(!roomConsent.IsVisibleInTree() || !roomConsent.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.Name == "AllowOnce").IsVisibleInTree(), "the answered row still asks");
            await UiTestWaitForAsync(() => wizard.RoomPlayers.Count == 2 ? wizard : null, "the room's players once its read was allowed");
            UiTestExpect(site.Requests.Count == 1 && site.Requests[0] == "/api/room_status/" + roomId && ((LineEdit)wizard.FindChild("ServerBox", true, false)).Text == "127.0.0.1:40123"
                && wizard.RoomPlayers.SequenceEqual(new[] { "Lantern", "Bob" }), $"the room wasn't read once for its server and players ({string.Join(", ", site.Requests)})");
            var playersBox = (VBoxContainer)wizard.FindChild("PlayersBox", true, false);
            UiTestExpect(playersBox.GetChildren().OfType<CheckBox>().Select(c => c.Text).SequenceEqual(new[] { "Lantern", "Bob" }), "the page doesn't list the room's players to tick");
            // The link cleared, its players go and nothing is read again; the test server typed, and the slot typed by its name.
            roomBox.Text = "";
            roomBox.EmitSignal(LineEdit.SignalName.TextChanged, "");
            UiTestExpect(wizard.RoomPlayers.Count == 0 && !playersBox.GetChildren().OfType<CheckBox>().Any(c => !c.IsQueuedForDeletion()) && site.Requests.Count == 1, "clearing the link didn't take the players, or read again");
            // A room whose status can't be read (gone, or its site down): the server typed by hand stands in for it.
            string goneLink = site.Site + "/room/ZzYyXxWwVvUuTtSsRrQqPw"; // a whole id: 22 characters, its last one's spare bits clear
            roomBox.Text = goneLink;
            roomBox.EmitSignal(LineEdit.SignalName.TextChanged, goneLink);
            await UiTestWaitForAsync(() => site.Requests.Count == 2 ? wizard : null, "the gone room's read");
            await UiTestWaitAsync(0.2);
            ((LineEdit)wizard.FindChild("ServerBox", true, false)).Text = server.Url.ToString();
            var row = await UiTestWaitForAsync(() => slotsBox.GetChildren().OfType<HBoxContainer>().FirstOrDefault(r => !r.IsQueuedForDeletion()), "a slot row to type into");
            UiTestExpect(row.GetChildren().OfType<LineEdit>().Count() == 1, "a slot row still has a game box");
            // A slot typed wrong: Next saves the multiworld, the login is refused, and the page says so.
            row.GetChild<LineEdit>(0).Text = "Lanter";
            UiTestNothingOnTopOfSetup(rootWindowsBefore, "on the multiworld page");
            await wizard.NextForTestsAsync();
            UiTestExpect(wizard.CurrentPageId == AP_Atlas.UI.SetupSteps.Multiworld.Id && _profiles.Count(p => p.Name == "Wizard MW") == 1
                && wizard.PageOf(AP_Atlas.UI.SetupSteps.Multiworld.Id).FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(l => l.Visible && l.Text.StartsWith("Lanter didn't connect", StringComparison.Ordinal)),
                $"a refused slot: page {wizard.CurrentPageId}, {_profiles.Count(p => p.Name == "Wizard MW")} multiworld(s) saved, or the page doesn't say why");
            // Corrected, Next again updates the same multiworld (the wrong slot goes) and connects the slot: its game comes from the room.
            row.GetChild<LineEdit>(0).Text = "Lantern";
            await wizard.NextForTestsAsync();
            var saved = wizard.SavedProfile;
            UiTestExpect(wizard.CurrentPageId == AP_Atlas.UI.SetupSteps.Games.Id && saved != null && saved.Name == "Wizard MW" && saved.ServerUrl == server.Url.ToString() && saved.Slots.SequenceEqual(new[] { "Lantern" })
                && _profiles.Count(p => p.Name == "Wizard MW") == 1 && !saved.SavedStats.ContainsKey("Lanter")
                && _profiles.Contains(saved) && _selectedProfile == saved && DataManager.LoadProfiles().Any(p => p.Id == saved.Id),
                $"Next didn't save the multiworld and move on: page {wizard.CurrentPageId}, {saved?.Name} {saved?.ServerUrl} [{string.Join(", ", saved?.Slots ?? new List<string>())}]");
            UiTestExpect(SlotView(saved!.Id, "Lantern") != null && saved.SavedStats.TryGetValue("Lantern", out var stats) && stats.GameName == "Test Game", "Next didn't connect the slot, or its game didn't come from the room");
            UiTestExpect(SetupSteps.Done(AP_Atlas.UI.SetupSteps.Multiworld) && !SetupSteps.Done(AP_Atlas.UI.SetupSteps.Games), "after Next, the multiworld step isn't done, or a game without its logic counts as ready");
            // Back keeps what was typed (the slot once); Next again updates the same multiworld.
            wizard.BackButton.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(wizard.CurrentPageId == AP_Atlas.UI.SetupSteps.Multiworld.Id && ((LineEdit)wizard.FindChild("NameBox", true, false)).Text == "Wizard MW"
                && slotsBox.GetChildren().OfType<HBoxContainer>().Where(r => !r.IsQueuedForDeletion()).Select(r => r.GetChild<LineEdit>(0).Text).Count(t => t == "Lantern") == 1,
                $"Back lost the name, or the slot shows {slotsBox.GetChildren().OfType<HBoxContainer>().Count(r => !r.IsQueuedForDeletion() && r.GetChild<LineEdit>(0).Text == "Lantern")} times");
            await wizard.NextForTestsAsync();
            UiTestExpect(wizard.CurrentPageId == AP_Atlas.UI.SetupSteps.Games.Id && _profiles.Count(p => p.Name == "Wizard MW") == 1 && wizard.SavedProfile == saved,
                $"Next again made another multiworld ({_profiles.Count(p => p.Name == "Wizard MW")}) or didn't move on ({wizard.CurrentPageId})");
            // Add games, on the page: the slot's game first and ticked; Add waits for the engine, whose lead leads to the engine's page here.
            var panel = await UiTestWaitForAsync(() => wizard.AddGamesPanelShown is { FactsPending: 0 } p ? p : null, "Add games on the page", 20);
            UiTestOnlyTicked(panel, "Test Game");
            await UiTestWaitAsync(0.1);
            UiTestExpect(wizard.IsAncestorOf(panel) && AP_Atlas.UI.AddGamesDialog.Showing == null && panel.IsTicked("Test Game")
                && panel.Table.ShownRows.TakeWhile(r => r.Pinned).Any(r => r.Key == "Test Game") && panel.EngineLeadShown
                && wizard.NextButton.Text == "Install 1 game…" && wizard.NextButton.Disabled && wizard.SkipButton.Visible && wizard.BackButton.Visible && !wizard.SecondaryButton.Visible,
                $"Add games on the page: ticked {string.Join(", ", panel.Ticked)}, lead {panel.EngineLeadShown}, Next \"{wizard.NextButton.Text}\" disabled {wizard.NextButton.Disabled}, Skip {wizard.SkipButton.Visible}, Back {wizard.BackButton.Visible}, second {wizard.SecondaryButton.Visible}");
            UiTestNothingOnTopOfSetup(rootWindowsBefore, "on Add games");
            // On the page, the list's columns fit what shows of it: nothing is cut at the right edge.
            var tree = panel.Table.Tree;
            float columnsWide = Enumerable.Range(0, tree.Columns).Sum(i => (float)tree.GetColumnWidth(i));
            ScrollContainer? pageScroll = null;
            for (Node? n = panel.GetParent(); n != null && n != wizard; n = n.GetParent())
                if (n is ScrollContainer s) { pageScroll = s; break; }
            UiTestExpect(pageScroll != null && columnsWide <= tree.Size.X + 0.5f && tree.GetGlobalRect().End.X <= pageScroll.GetGlobalRect().End.X + 0.5f,
                $"the list is wider than the page: columns {columnsWide}, list {tree.Size.X} ending at {tree.GetGlobalRect().End.X}, page ending at {pageScroll?.GetGlobalRect().End.X}");
            panel.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.Text == "Set up the Atlas Engine…").EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(wizard.CurrentPageId == AP_Atlas.UI.SetupSteps.Engine.Id && wizard.Visible, "the engine lead didn't lead to the engine's page here");
            wizard.RailButtonOf(AP_Atlas.UI.SetupSteps.Games.Id).EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(wizard.CurrentPageId == AP_Atlas.UI.SetupSteps.Games.Id && panel.IsTicked("Test Game"), "the list on the left didn't lead back with the tick kept");
            // Nothing ticked, Next goes on and settles the step as skipped (it isn't done); Back, then Skip this step, does the same.
            UiTestOnlyTicked(panel);
            await UiTestWaitAsync(0.1);
            UiTestExpect(wizard.NextButton.Text == "Next" && !wizard.NextButton.Disabled, $"with nothing ticked, Next says \"{wizard.NextButton.Text}\" (disabled {wizard.NextButton.Disabled})");
            await wizard.NextForTestsAsync();
            UiTestExpect(wizard.CurrentPageId == "ready" && SetupSteps.Skipped(AP_Atlas.UI.SetupSteps.Games) && !SetupSteps.Done(AP_Atlas.UI.SetupSteps.Games),
                $"Next with nothing to add: page {wizard.CurrentPageId}, the games step skipped {SetupSteps.Skipped(AP_Atlas.UI.SetupSteps.Games)}");
            wizard.BackButton.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(wizard.CurrentPageId == AP_Atlas.UI.SetupSteps.Games.Id && wizard.SkipButton.Visible, $"Back from Ready led to {wizard.CurrentPageId}");
            wizard.SkipButton.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(wizard.CurrentPageId == "ready" && SetupSteps.Skipped(AP_Atlas.UI.SetupSteps.Games), "Skip didn't go on from Add games");
            // Ready: what's left, Finish, and three links to Settings; a link puts the window out of the way until Resume.
            UiTestExpect(ReadyLines(wizard).Any(l => l.StartsWith("Not everything is set up yet:", StringComparison.Ordinal) && l.Contains("Right-click a pin or tile to fix it."))
                && !ReadyLines(wizard).Any(l => l.StartsWith("You're set up.", StringComparison.Ordinal)) && ReadyLines(wizard).Any(l => l.StartsWith("✗ The Atlas Engine isn't set up", StringComparison.Ordinal))
                && wizard.NextButton.Text == "Finish" && !wizard.NextButton.Disabled
                && new[] { "LookLink", "TrackersLink", "ReportsLink" }.All(n => wizard.FindChild(n, true, false) is Button), $"Ready says: {string.Join(" | ", ReadyLines(wizard))}");
            UiTestNothingOnTopOfSetup(rootWindowsBefore, "on the Ready page");
            ((Button)wizard.FindChild("ReportsLink", true, false)).EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(ShownContent() == _settingsPage && !wizard.Visible && IsInstanceValid(wizard) && !wizard.IsQueuedForDeletion(), "a Ready link didn't show Settings with the window out of the way");
            UiTestExpect(OpenSetupWizard(resume: true) == wizard && wizard.Visible && wizard.CurrentPageId == "ready", "Resume didn't bring the window back as it was");
            await wizard.NextForTestsAsync();
            await UiTestWaitForAsync(() => !IsInstanceValid(wizard) || wizard.IsQueuedForDeletion() ? this : null, "Finish to close the window");
            UiTestExpect(SlotView(saved.Id, "Lantern") != null, "Finish disconnected the slot");
            // Home's setup line counts what was done and names the step left (the engine: the pretend setup leaves no engine); Resume opens there.
            host.ShowTool(AP_Atlas.UI.Tool.Home);
            var home = _homePage!;
            UiTestExpect(home.SetupLine == "Setup 2 of 3 done. Next: The Atlas Engine." && home.ResumeButton.Visible, $"Home's setup line reads \"{home.SetupLine}\"");
            home.ResumeButton.EmitSignal(BaseButton.SignalName.Pressed);
            var resumed = await UiTestWaitForAsync(Wizard, "the setup window from Resume");
            UiTestExpect(resumed.CurrentPageId == AP_Atlas.UI.SetupSteps.Engine.Id, $"Resume opened on {resumed.CurrentPageId}, not the engine");
            // The rail: the pages before are reachable, the ones past the furthest reached aren't; Help → Set up Atlas… brings the same window.
            UiTestExpect(!resumed.RailButtonOf("welcome").Disabled && resumed.RailButtonOf("ready").Disabled, "the rail doesn't keep the pages past the furthest reached for Next");
            _commands!.Run("help.setup");
            await UiTestWaitAsync(0.1);
            UiTestExpect(Wizard() == resumed, "Help → Set up Atlas… opened a second window");
            // Welcome, which isn't a setup step, is ticked once the window has gone past it.
            UiTestExpect(resumed.RailButtonOf("welcome").TooltipText == "Done", $"Welcome's tick: \"{resumed.RailButtonOf("welcome").TooltipText}\"");
            resumed.EmitSignal(Window.SignalName.CloseRequested);
            await UiTestWaitAsync(0.2);
            // Everything settled (the engine skipped), Resume opens on Ready in a new window that never saw the multiworld page:
            // it is about the multiworld selected on the Multiworlds page, or else the one played most recently.
            SetupSteps.SetSkipped(AP_Atlas.UI.SetupSteps.Engine, true);
            var selectedBefore = _selectedProfile;
            _selectedProfile = saved;
            var ready = OpenSetupWizard(resume: true);
            await UiTestWaitAsync(0.2);
            UiTestExpect(ready.CurrentPageId == "ready" && ready.ShownProfile == saved && ReadyLines(ready).Any(l => l.StartsWith("✓ Wizard MW:", StringComparison.Ordinal))
                && !ReadyLines(ready).Any(l => l.Contains("No multiworld yet", StringComparison.Ordinal)), $"Ready after Resume says: {string.Join(" | ", ReadyLines(ready))}");
            ready.EmitSignal(Window.SignalName.CloseRequested);
            await UiTestWaitAsync(0.2);
            _selectedProfile = null;
            var newer = new MultiworldProfile { Name = "Newer MW", ServerUrl = "localhost:1" };
            newer.SavedStats["Me"] = new SlotStats { GameName = "Test Game", LastUpdated = DateTime.Now.AddMinutes(1) }; // wall clock: a saved stat's date, as Atlas saves it
            _profiles.Add(newer);
            ready = OpenSetupWizard(resume: true);
            await UiTestWaitAsync(0.2);
            UiTestExpect(ready.ShownProfile == newer && ReadyLines(ready).Any(l => l.StartsWith("✓ Newer MW:", StringComparison.Ordinal)), $"with nothing selected, Ready isn't about the newest multiworld: {string.Join(" | ", ReadyLines(ready))}");
            ready.EmitSignal(Window.SignalName.CloseRequested);
            await UiTestWaitAsync(0.2);
            _selectedProfile = selectedBefore;
        }
        finally
        {
            AtlasEngine.TestSetUp = null;
            ApworldSources.UseListForTests(null);
            AP_Atlas.UI.FixPreviewDialog.Showing?.QueueFree();
            foreach (var w in GetChildren().OfType<AP_Atlas.UI.SetupWizard>()) w.QueueFree();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, false);
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.RoomStatusReads, null, false);
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            _appSettings.SetupPagesDone = pagesBefore;
            _appSettings.SkippedHomeSteps = skippedBefore;
            DataManager.SaveSettings(_appSettings);
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            foreach (var p in _profiles.Where(p => p.Name is "Wizard MW" or "Newer MW" or "Wizard Other").ToList()) DeleteProfile(p);
        }
    }

    /// <summary>Fails when a window other than the setup window shows: one of the main window's own, or one of the root's beyond those given.</summary>
    private void UiTestNothingOnTopOfSetup(IReadOnlyList<Window> rootWindowsBefore, string when)
    {
        var others = GetChildren().OfType<Window>().Where(w => w is not AP_Atlas.UI.SetupWizard && w.Visible && !w.IsQueuedForDeletion()).Select(w => w.Name.ToString()).ToList();
        others.AddRange(GetTree().Root.GetChildren().OfType<Window>().Except(rootWindowsBefore).Where(w => w.Visible).Select(w => w.Name.ToString()));
        UiTestExpect(others.Count == 0, $"something opened on top of the setup window {when}: {string.Join(", ", others)}");
    }

    /// <summary>Only the scenario's games stay ticked: a game a leftover multiworld plays is unticked.</summary>
    private static void UiTestOnlyTicked(AP_Atlas.UI.AddGamesPanel panel, params string[] keep)
    {
        foreach (string g in panel.Ticked.ToList()) if (!keep.Contains(g)) panel.Tick(g, false);
    }

    private async Task SetupAddGamesAsync()
    {
        // The installed apworlds' data checksum: the room's, so the slots' logic runs once their games are in.
        const string checksum = "5e7a5e7a5e7a5e7a5e7a5e7a5e7a5e7a5e7a5e7a";
        const string alpha = "Setup Alpha", beta = "Setup Beta";
        var engine = StartFakeEngine(UiTestPython(), fake =>
        {
            fake.CheckWorldsDir = AtlasEngine.UserWorldsDir;
            fake.DataChecksum = checksum;
            fake.StartError = ("world_missing", "The game isn't installed in the logic engine.");
        });
        static byte[] Zip(params (string Name, string Text)[] files)
        {
            using var memory = new System.IO.MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, text) in files)
                {
                    using var writer = new System.IO.StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(text);
                }
            return memory.ToArray();
        }
        static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        string data = DataManager.GetDataDirectory();
        string scratch = System.IO.Path.Combine(data, "uitest_setup_add");
        System.IO.Directory.CreateDirectory(scratch);
        byte[] alphaApworld = Zip(("setup_alpha/archipelago.json", "{\"game\": \"" + alpha + "\", \"world_version\": \"1.2.0\"}"), ("setup_alpha/__init__.py", "# " + alpha + "\n"));
        byte[] betaApworld = Zip(("setup_beta/archipelago.json", "{\"game\": \"" + beta + "\"}"), ("setup_beta/__init__.py", "# " + beta + "\n"));
        string alphaZip = System.IO.Path.Combine(scratch, "alpha.zip");
        FakeMapPack.Write(alphaZip, "UI test setup pack", alpha);
        byte[] alphaPack = await System.IO.File.ReadAllBytesAsync(alphaZip);
        await using var site = new FakeWebSite();
        site.Answer = (path, _) => path switch
        {
            "/dl/setup_alpha.apworld" => new FakeWebSite.FullAnswer(200, alphaApworld, "application/octet-stream"),
            "/dl/setup_beta.apworld" => new FakeWebSite.FullAnswer(200, betaApworld, "application/octet-stream"),
            "/gh/packs/setup-alpha/releases/download/v1/setup_alpha.zip" => new FakeWebSite.FullAnswer(200, alphaPack, "application/zip"),
            _ => new FakeWebSite.FullAnswer(404, Array.Empty<byte>())
        };
        var list = new ApworldSourceList
        {
            Format = "atlas-apworld-sources/1",
            Built = "9999-12-31",
            Source = "ui-test",
            Games = new List<ApworldSource>
            {
                new ApworldSource { Game = alpha, Apworld = "setup_alpha", Repo = "owner/setup-alpha",
                    Versions = { new ApworldVersion { Version = "1.2.0", Url = site.Site + "/dl/setup_alpha.apworld", Sha256 = Sha(alphaApworld), Size = alphaApworld.Length } },
                    Packs = { new IndexPack { Repo = "packs/setup-alpha", Release = "v1", Asset = "setup_alpha.zip", Sha256 = Sha(alphaPack), Size = alphaPack.Length, Fit = 100 } } },
                new ApworldSource { Game = beta, Apworld = "setup_beta", Repo = "owner/setup-beta",
                    Versions = { new ApworldVersion { Version = "0.3.0", Url = site.Site + "/dl/setup_beta.apworld", Sha256 = Sha(betaApworld), Size = betaApworld.Length } } }
            }
        };
        // No official game is added: nothing may reach Archipelago's real release (the index lists one world, as an index
        // must, for a game no multiworld plays).
        var noWorlds = WorldsIndex.Parse(new JObject
        {
            ["archipelago"] = AtlasEngine.ArchipelagoVersion,
            ["zip_sha256"] = new string('0', 64),
            ["zip_root"] = "X/",
            ["zip_bytes"] = 1,
            ["core_worlds"] = new JArray(),
            ["worlds"] = new JObject { ["setup_shipped"] = new JObject { ["games"] = new JArray("Setup Shipped"), ["needs"] = new JArray(), ["requirements"] = false, ["bytes"] = 1 } }
        }.ToString());
        string worlds = AtlasEngine.UserWorldsDir, packs = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory();
        var checkBefore = AtlasEngine.State.LastCheck;
        // The room: two slots (no "Tester" in their names: the crash report scenario checks its log for that word), each its own game.
        await using var server = new FakeArchipelagoServer();
        server.SlotName = "Wren";
        server.Slots.Add("Kite");
        server.SlotGames["Wren"] = alpha;
        server.SlotGames["Kite"] = beta;
        // Its locations are near matches of the pack's own names, not the same: the batch's summary asks to fix the pack.
        foreach (string game in new[] { alpha, beta })
            server.Games[game] = new FakeGame(checksum,
                new Dictionary<string, long> { ["Sword"] = 1000, ["Shield"] = 1001, ["Rupee"] = 1002, ["Gem"] = 1099 },
                new Dictionary<string, long> { ["Cave Chest Room"] = 2000, ["Far Lantern Box"] = 2001, ["Tower Top"] = 2002, ["Gem Vault"] = 2003 });
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var rootWindowsBefore = GetTree().Root.GetChildren().OfType<Window>().ToList();
        GameAdderRun? run = null;
        try
        {
            ApworldSources.UseListForTests(list);
            AtlasEngine.TestWorlds = (noWorlds, site.Site + "/dl/none.zip", new string('0', 64));
            GameAdder.TestGitHubSite = site.Site + "/gh";
            AP_Atlas.Core.GitHubApi.TestSite = site.Site;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, false);
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null, false);
            _appSettings.ApprovedApworldSources.Remove("127.0.0.1"); // the confirm names it as a project to trust
            host.ShowTool(AP_Atlas.UI.Tool.Home);

            // With the engine ready, the window has no engine page.
            var wizard = OpenSetupWizard(resume: false);
            await UiTestWaitAsync(0.3);
            UiTestExpect(wizard.PageIdsShown.SequenceEqual(new[] { "welcome", AP_Atlas.UI.SetupSteps.Multiworld.Id, AP_Atlas.UI.SetupSteps.Games.Id, "ready" }),
                $"with the engine ready, the setup window's pages are {string.Join(", ", wizard.PageIdsShown)}");
            await wizard.NextForTestsAsync();
            UiTestExpect(wizard.CurrentPageId == AP_Atlas.UI.SetupSteps.Multiworld.Id, $"Start led to {wizard.CurrentPageId}");
            // Your multiworld and slots: the server and two slots typed; Next saves the multiworld and connects both.
            ((LineEdit)wizard.FindChild("NameBox", true, false)).Text = "Setup Add MW";
            ((LineEdit)wizard.FindChild("ServerBox", true, false)).Text = server.Url.ToString();
            var slotsBox = (VBoxContainer)wizard.FindChild("SlotsBox", true, false);
            var first = await UiTestWaitForAsync(() => slotsBox.GetChildren().OfType<HBoxContainer>().FirstOrDefault(r => !r.IsQueuedForDeletion()), "a slot row to type into");
            first.GetChild<LineEdit>(0).Text = "Wren";
            ((Button)wizard.FindChild("AddSlotRow", true, false)).EmitSignal(BaseButton.SignalName.Pressed);
            slotsBox.GetChildren().OfType<HBoxContainer>().Last(r => !r.IsQueuedForDeletion()).GetChild<LineEdit>(0).Text = "Kite";
            await wizard.NextForTestsAsync();
            var profile = wizard.SavedProfile;
            UiTestExpect(wizard.CurrentPageId == AP_Atlas.UI.SetupSteps.Games.Id && profile != null && profile.Slots.SequenceEqual(new[] { "Wren", "Kite" }),
                $"Next didn't save both slots and move on: page {wizard.CurrentPageId}, slots [{string.Join(", ", profile?.Slots ?? new List<string>())}]");
            var wren = SlotView(profile!.Id, "Wren");
            var kite = SlotView(profile.Id, "Kite");
            UiTestExpect(wren != null && kite != null && profile.SavedStats.TryGetValue("Wren", out var wrenStats) && wrenStats.GameName == alpha
                && profile.SavedStats.TryGetValue("Kite", out var kiteStats) && kiteStats.GameName == beta,
                "Next didn't connect both slots, or their games didn't come from the room");
            // Their Logic banners say the setup window adds the game, with nothing to press.
            foreach (var slot in new[] { wren!, kite! })
            {
                await UiTestWaitForAsync(() => slot.EngineProblem?.Code == "world_missing" ? slot : null, $"{slot.SlotName}'s logic to find its game missing", 20);
                UiTestExpect(slot.LogicNoticeShown?.Contains("The setup window adds") == true && slot.LogicNoticeButtons().Count == 0 && slot.LogicEmptyState("").Contains("The setup window adds"),
                    $"{slot.SlotName}'s Logic banner during the setup says \"{slot.LogicNoticeShown}\" with [{string.Join(", ", slot.LogicNoticeButtons())}]; the empty view \"{slot.LogicEmptyState("")}\"");
            }
            // Add games on the page: both games first and ticked, Add 2 games… enabled (the engine is ready), nothing on top.
            var panel = await UiTestWaitForAsync(() => wizard.AddGamesPanelShown is { FactsPending: 0 } p ? p : null, "Add games on the page", 20);
            UiTestOnlyTicked(panel, alpha, beta);
            await UiTestWaitAsync(0.1);
            UiTestColumnFits(panel.Table, 2, "the setup window's Installs column");
            UiTestExpect(panel.IsTicked(alpha) && panel.IsTicked(beta) && !panel.EngineLeadShown && wizard.NextButton.Text == "Install 2 games…" && !wizard.NextButton.Disabled,
                $"Add games on the page: ticked {string.Join(", ", panel.Ticked)}, lead {panel.EngineLeadShown}, Next \"{wizard.NextButton.Text}\" disabled {wizard.NextButton.Disabled}");
            UiTestNothingOnTopOfSetup(rootWindowsBefore, "on Add games");
            // The one confirm: its Back beside Next, the window's Back and Skip aside, nothing downloaded; Back keeps the ticks.
            await wizard.NextForTestsAsync();
            await UiTestWaitAsync(0.15); // the confirm's layout
            UiTestTextFits(panel, "the setup window's confirm");
            UiTestExpect(panel.GetCombinedMinimumSize().X <= 621, $"the setup window's confirm needs {panel.GetCombinedMinimumSize().X:0} of its 620-wide page");
            UiTestExpect(panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Confirm && wizard.NextButton.Text == "Install" && wizard.SecondaryButton is { Visible: true, Text: "Back" }
                && !wizard.BackButton.Visible && !wizard.SkipButton.Visible && site.Requests.Count == 0 && wizard.CurrentPageId == AP_Atlas.UI.SetupSteps.Games.Id,
                $"the confirm's footer: Next \"{wizard.NextButton.Text}\", second \"{wizard.SecondaryButton.Text}\" {wizard.SecondaryButton.Visible}, Back {wizard.BackButton.Visible}, Skip {wizard.SkipButton.Visible}, requests {site.Requests.Count}");
            UiTestNothingOnTopOfSetup(rootWindowsBefore, "on the confirm");
            wizard.SecondaryButton.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Pick && panel.IsTicked(alpha) && panel.IsTicked(beta) && wizard.BackButton.Visible, "the confirm's Back didn't return to the list with the ticks");
            await wizard.NextForTestsAsync();
            UiTestExpect(panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Confirm, "Next didn't show the confirm again");
            // The games' logic starts once they're in.
            engine.StartError = null;
            engine.Apply();
            // Install: the bar with Cancel on Next (what stopping keeps in its tooltip), the list on the left locked while it runs.
            string? footerWhileRunning = null;
            bool railLocked = false;
            panel.StatusChanged += _ =>
            {
                if (footerWhileRunning != null || wizard.Busy || !IsInstanceValid(wizard)) return;
                footerWhileRunning = wizard.NextButton.Text + (wizard.NextButton.TooltipText.Length > 0 ? "" : " (no tooltip)");
                railLocked = wizard.RailButtonOf("welcome").Disabled;
            };
            await wizard.NextForTestsAsync();
            run = await UiTestWaitForAsync(() => panel.CurrentStage == AP_Atlas.UI.AddGamesPanel.Stage.Summary ? panel.Result : null, "the summary", 120);
            var lines = panel.SummaryLines();
            UiTestExpect(footerWhileRunning == "Cancel" && railLocked && !wizard.RailButtonOf("welcome").Disabled,
                $"while it ran, Next said \"{footerWhileRunning}\" and the list was locked {railLocked}; after, locked {wizard.RailButtonOf("welcome").Disabled}");
            UiTestExpect(!run.Outcome.Cancelled && run.Outcome.For(alpha).Logic.Outcome == AP_Atlas.Core.Games.AddStepOutcome.Done && run.Outcome.For(beta).Logic.Outcome == AP_Atlas.Core.Games.AddStepOutcome.Done
                && lines.Contains("logic 1.2.0"), $"the summary: {string.Join(" | ", lines)}");
            await UiTestWaitAsync(0.15); // the summary's layout
            UiTestTextFits(panel, "the setup window's summary");
            UiTestExpect(panel.GetCombinedMinimumSize().X <= 621, $"the setup window's summary needs {panel.GetCombinedMinimumSize().X:0} of its 620-wide page");
            UiTestNothingOnTopOfSetup(rootWindowsBefore, "on the summary");
            // The summary says per pack what Atlas fixed by itself; nothing asks and nothing opened on the window: Next goes on.
            UiTestExpect(run.Outcome.PacksToLook.Count > 0 && wizard.NextButton.Text == "Next" && !wizard.NextButton.Disabled,
                $"after the summary, Next says \"{wizard.NextButton.Text}\" ({run.Outcome.PacksToLook.Count} pack line(s))");
            // The slots' logic starts by itself once their games are in.
            await UiTestWaitForAsync(() => wren!.EngineRunning && kite!.EngineRunning ? wren : null, "both slots' logic to start once their games are in", 30);
            // Each slot's logic started twice as the games came in (the engine set up, then the new apworld): the first start,
            // overtaken, wrote none of the results the running one reads.
            UiTestExpect(wren!.Model.Logic.Engine.LastStartError == null && kite!.Model.Logic.Engine.LastStartError == null,
                $"an overtaken start left its result: Wren \"{wren.Model.Logic.Engine.LastStartError?.Message}\", Kite \"{kite!.Model.Logic.Engine.LastStartError?.Message}\"");
            // Ready: the games step is done (the games of this multiworld; another multiworld's game not ready would keep it open).
            var otherGames = _profiles.Where(p => p != profile).SelectMany(p => p.Slots.Where(s => !AP_Atlas.UI.SetupSteps.IsPlaceholderSlot(s))
                .Select(s => p.SavedStats.TryGetValue(s, out var st) ? st.GameName : null)).Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
            await wizard.NextForTestsAsync();
            UiTestExpect(wizard.CurrentPageId == "ready", $"Next after the summary led to {wizard.CurrentPageId}");
            var ownRows = _gamesPage!.PlanAdding(new[] { alpha, beta }).Games.Select(g => g.Row).ToList();
            UiTestExpect(ownRows.All(r => r is AP_Atlas.Core.Games.AddRow.Ready or AP_Atlas.Core.Games.AddRow.NoMapKnown), $"the added games' rows: {string.Join(", ", ownRows)}");
            if (otherGames.Count == 0)
            {
                UiTestExpect(SetupSteps.Done(AP_Atlas.UI.SetupSteps.Games) && SetupSteps.AllSettled, $"with every game ready, the steps settled are {string.Join(", ", AP_Atlas.UI.SetupSteps.All.Where(SetupSteps.Settled).Select(s => s.Id))}");
                var readyLine = wizard.PageOf("ready").FindChildren("*", nameof(Label), true, false).OfType<Label>().FirstOrDefault(l => l.HasMeta("setup_ready_line"));
                UiTestExpect(readyLine != null && readyLine.Text.StartsWith("You're set up.", StringComparison.Ordinal), $"the Ready page says \"{readyLine?.Text}\"");
            }
            else GD.Print($"UITEST INFO Setup window, adding games: other multiworlds play {string.Join(", ", otherGames)}, so the games step's done isn't checked");
            await wizard.NextForTestsAsync();
            await UiTestWaitForAsync(() => !IsInstanceValid(wizard) || wizard.IsQueuedForDeletion() ? this : null, "Finish to close the window");
            UiTestExpect(_setupQuietProfile == null, "the Logic banners are still kept quiet after the window closed");
        }
        finally
        {
            foreach (var w in GetChildren().OfType<AP_Atlas.UI.SetupWizard>()) w.QueueFree();
            AP_Atlas.UI.FixPreviewDialog.Showing?.QueueFree();
            ApworldSources.UseListForTests(null);
            AtlasEngine.TestWorlds = null;
            GameAdder.TestGitHubSite = null;
            AP_Atlas.Core.GitHubApi.TestSite = null;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, false);
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            AtlasEngine.State.LastCheck = checkBefore;
            foreach (var p in run?.Outcome.PacksToLook ?? new List<AP_Atlas.Core.Games.PackToLook>()) AP_Atlas.Core.PopTracker.PackDoctorService.Reports.Remove(p.PackKey);
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            foreach (var p in _profiles.Where(p => p.Name == "Setup Add MW").ToList()) DeleteProfile(p);
            _appSettings.ApprovedApworldSources.Remove("127.0.0.1");
            _appSettings.PreferredPacks.Remove(alpha);
            DataManager.SaveSettings(_appSettings);
            AP_Atlas.Core.SafeFile.Delete(System.IO.Path.Combine(worlds, "setup_alpha.apworld"));
            AP_Atlas.Core.SafeFile.Delete(System.IO.Path.Combine(worlds, "setup_beta.apworld"));
            AP_Atlas.Core.SafeFile.Delete(System.IO.Path.Combine(packs, "setup_alpha.zip"));
            foreach (string game in new[] { alpha, beta })
            {
                string kept = AP_Atlas.Core.Games.GameFiles.ApworldsFolder(data, game);
                if (System.IO.Directory.Exists(kept)) System.IO.Directory.Delete(kept, true);
            }
            if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
        }
    }

    private async Task GamesPageAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var page = _gamesPage ?? throw new InvalidOperationException("The Games page wasn't built.");
        var profile = new MultiworldProfile { Name = "Games test" };
        profile.Slots.Clear();
        profile.Slots.Add("Me");
        profile.SavedStats["Me"] = new SlotStats { GameName = "Atlas Test Game", LastUpdated = DateTime.Now };
        _profiles.Add(profile);
        string yaml = System.IO.Path.Combine(DataManager.GetDataDirectory(), "uitest_two_games.yaml");
        string packsDir = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory();
        string gamePack = System.IO.Path.Combine(packsDir, "uitest_games_pack.zip"), strayPack = System.IO.Path.Combine(packsDir, "uitest_stray_pack.zip"), installedPack = System.IO.Path.Combine(packsDir, "uitest_installed_pack.zip");
        await using var github = new AP_Atlas.Core.Testing.FakeWebSite();
        try
        {
            host.ShowTool(AP_Atlas.UI.Tool.Games);
            await UiTestWaitAsync(0.2);
            UiTestExpect(ShownContent() == page && page.SidebarContent.IsVisibleInTree(), "the Games tool didn't show its page and its list");
            // The groups: the community index's games, and a game a multiworld plays that nothing else knows, as added by you.
            UiTestExpect(page.Games.Count(g => g.Section == AP_Atlas.UI.GamesPage.Section.Community) > 10, "the community index's games aren't listed");
            UiTestExpect(page.Games.Any(g => g.Game == "Atlas Test Game" && g.Section == AP_Atlas.UI.GamesPage.Section.Yours), "a game a multiworld plays isn't listed as added by you");
            // Typed words narrow the list.
            var search = page.SidebarContent.FindChildren("*", nameof(LineEdit), true, false).OfType<LineEdit>().First();
            search.Text = "atlas test";
            search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
            UiTestExpect(page.ShownGames.SequenceEqual(new[] { "Atlas Test Game" }), $"searching \"atlas test\" lists {string.Join(", ", page.ShownGames)}");
            search.Text = "";
            search.EmitSignal(LineEdit.SignalName.TextChanged, "");
            // The games of the user's own slots come first, each listed once.
            UiTestExpect(page.ShownSections.Count > 1 && page.ShownSections[0] == "In your multiworlds (1)" && page.ShownGames[0] == "Atlas Test Game" && page.ShownGames.Count(g => g == "Atlas Test Game") == 1,
                $"the list doesn't open with the slots' games: {string.Join(" | ", page.ShownSections)}; first {page.ShownGames.FirstOrDefault()}");
            // A game's page: the steps, none done yet.
            var tree = page.SidebarContent.FindChildren("*", nameof(Tree), true, false).OfType<Tree>().First();
            TreeItem? Row(TreeItem? item, string game)
            {
                for (var i = item?.GetFirstChild(); i != null; i = i.GetNext())
                {
                    if (i.GetMetadata(0).VariantType == Variant.Type.String && i.GetMetadata(0).AsString() == game) return i;
                    if (Row(i, game) is { } found) return found;
                }
                return null;
            }
            var row = Row(tree.GetRoot(), "Atlas Test Game") ?? throw new InvalidOperationException("the test game has no row");
            row.Select(0);
            await UiTestWaitAsync(0.1);
            UiTestExpect(page.SelectedGame == "Atlas Test Game", "selecting a game didn't show its page");
            // One card on top (no source of its logic known yet: Atlas can't add it, with Add a project… and Choose a file…; no
            // engine: the engine first), the rest in closed folds (Advanced holds the versions and file installs); no YAML on
            // the page, nothing of the testing tools outside developer mode.
            bool engineReady = AP_Atlas.Core.EngineSetup.AtlasEngine.Current is { CanLaunch: true };
            Button? PageButton(string meta) => page.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.HasMeta(meta) && !b.IsQueuedForDeletion());
            UiTestExpect(page.GameCardShown.Standing == (engineReady ? AP_Atlas.Core.Games.GameStanding.Unknown : AP_Atlas.Core.Games.GameStanding.EngineFirst) && page.UpdateOffered == null && !page.DebugShown,
                $"the card shows {page.GameCardShown} with Update to {page.UpdateOffered}, or Debug shows outside developer mode (engine ready: {engineReady})");
            UiTestExpect(new[] { "games:map-packs", "games:files", "games:advanced" }.All(key => AP_Atlas.UI.Kit.FoldedState(page, key) == true), "the game's other parts aren't folded");
            UiTestExpect(PageButton("find_pack")?.Text == "Find a map pack…" && PageButton("game_doctor") == null, "the card doesn't offer to find a pack, or offers Doctor… without one");
            bool NoYaml() => AP_Atlas.UI.Kit.FoldedState(page, "games:yaml") == null
                && !page.FindChildren("*", nameof(Button), true, false).OfType<Button>().Any(b => b.Text.Contains("YAML"))
                && !page.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(l => l.Text.Contains("YAML"));
            UiTestExpect(NoYaml(), "the game's page speaks of a YAML");
            // A slot's linked YAML still names its games for the list (the YAML itself isn't on the Games page).
            AP_Atlas.Core.SafeFile.WriteAllText(yaml, "name: Me\ngame:\n  Atlas Test Game: 1\n  Other Test Game: 1\n");
            // A slot's linked YAML puts its games at the top too, before the slot ever connects.
            string meKey = AP_Atlas.Core.Annotations.SlotKey(profile.Id, "Me");
            _appSettings.SlotYamlPaths[meKey] = yaml;
            page.Refresh();
            UiTestExpect(page.ShownSections[0] == "In your multiworlds (2)" && page.ShownGames.Take(2).OrderBy(g => g).SequenceEqual(new[] { "Atlas Test Game", "Other Test Game" }),
                $"a linked YAML's games aren't listed first: {string.Join(" | ", page.ShownSections)}; {string.Join(", ", page.ShownGames.Take(3))}");
            _appSettings.SlotYamlPaths.Remove(meKey);
            // The game's folders are Atlas's own.
            string data = System.IO.Path.GetFullPath(DataManager.GetDataDirectory());
            UiTestExpect(System.IO.Path.GetFullPath(AP_Atlas.Core.Games.GameFiles.GameFolder(data, "Atlas Test Game")).StartsWith(data, StringComparison.OrdinalIgnoreCase)
                && System.IO.Path.GetFullPath(AP_Atlas.Core.Games.GameFiles.YamlsFolder(data)).StartsWith(data, StringComparison.OrdinalIgnoreCase), "a game's folder isn't inside Atlas's data folder");

            // Every project's versions. GitHub is a fake site: the project added for the game publishes 1.0.0; a search for
            // projects of the same name finds a fork publishing 1.2.0 and a 1.3.0 pre-release (and an unrelated project,
            // left out). Once the permission is given, the page reads them by itself, one search, and downloads nothing.
            // Each release its own file (its own SHA-256): the same hash would mean the same file published twice.
            string Release(string tag, bool pre, char digest) =>
                $"{{\"tag_name\":\"{tag}\",\"draft\":false,\"prerelease\":{(pre ? "true" : "false")},\"published_at\":\"2026-09-0{(pre ? 2 : 1)}T00:00:00Z\"," +
                $"\"assets\":[{{\"name\":\"atlas_test.apworld\",\"browser_download_url\":\"{github.Site}/dl/{tag}/atlas_test.apworld\",\"digest\":\"sha256:{new string(digest, 64)}\",\"size\":1000}}," +
                $"{{\"name\":\"atlas_template.yaml\",\"browser_download_url\":\"{github.Site}/dl/{tag}/atlas_template.yaml\",\"size\":2000}},{{\"name\":\"client.zip\",\"browser_download_url\":\"{github.Site}/dl/{tag}/client.zip\",\"size\":300000}}]}}";
            github.Respond = path =>
                path.StartsWith("/search/repositories") ? (200, "{\"items\":[{\"full_name\":\"fork/atlas-test\",\"name\":\"atlas-test\"},{\"full_name\":\"other/atlas-test-maps\",\"name\":\"atlas-test-maps\"}]}")
                : path.StartsWith("/repos/owner/atlas-test/releases") ? (200, "[" + Release("1.0.0", false, 'a') + "]")
                : path.StartsWith("/repos/fork/atlas-test/releases") ? (200, "[" + Release("v1.3.0-beta", true, 'b') + "," + Release("1.2.0", false, 'c') + "]")
                : (404, "{}");
            AP_Atlas.Core.GitHubApi.TestSite = github.Site;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null, true);
            AP_Atlas.Core.EngineSetup.ApworldSources.AddUserRepo(_appSettings, "Atlas Test Game", "owner/atlas-test");
            page.Select("Atlas Test Game");
            await UiTestWaitForAsync(() => page.StatusText.Contains("version(s) from") ? page : null, "the projects' releases to be read", 20);
            await UiTestWaitForAsync(() => page.VersionRows().Count > 0 ? page : null, "the versions table");
            var versions = page.VersionRows();
            UiTestExpect(versions.Select(v => v.Version).SequenceEqual(new[] { "1.3.0-beta", "1.2.0", "1.0.0" }), $"the versions aren't every project's, newest first: {string.Join(", ", versions.Select(v => v.Version + " (" + v.Project + ")"))}");
            UiTestExpect(versions[0].Prerelease && versions[0].Project == "github.com/fork/atlas-test" && !versions[1].Prerelease && versions[2].Project == "github.com/owner/atlas-test",
                $"the table doesn't name each version's project or mark the pre-release: {string.Join("; ", versions.Select(v => $"{v.Version} {v.Project}{(v.Prerelease ? " pre" : "")}"))}");
            UiTestExpect(page.VersionsLine.Contains("Newest known: 1.2.0 from github.com/fork/atlas-test") && page.VersionsLine.Contains("fork or re-upload"), $"the line above the table says: {page.VersionsLine}");
            // A check box per row says which version "Install the selected version" takes.
            UiTestExpect(page.PickedVersion == null, "a version is ticked before any press");
            page.PickVersionForTests("1.2.0");
            UiTestExpect(page.PickedVersion == "1.2.0", $"ticking 1.2.0 picked {page.PickedVersion}");
            UiTestExpect(github.Requests.Count(r => r.StartsWith("/search/")) == 1, $"GitHub was searched {github.Requests.Count(r => r.StartsWith("/search/"))} times, not once");
            UiTestExpect(!github.Requests.Any(r => r.StartsWith("/dl/")), "a version was downloaded without a press");
            // With its project known, the card offers to add the game: its logic (from the project's newest release; its size is
            // read from GitHub then), then the map pack its setup guide names, read once its logic is in. The versions live under
            // the closed Advanced fold.
            UiTestExpect(engineReady ? page.GameCardShown.Standing == AP_Atlas.Core.Games.GameStanding.Add && page.GameCardShown.Line.Contains("then the map pack its setup guide names, if it names one")
                    && page.GameCardShown.Line.Contains("size not listed") && PageButton("add_game") != null
                : page.GameCardShown.Standing == AP_Atlas.Core.Games.GameStanding.EngineFirst, $"with its project known the card shows {page.GameCardShown}");
            var advanced = AP_Atlas.UI.Kit.SectionBody(page, "games:advanced");
            UiTestExpect(advanced != null && AP_Atlas.UI.Kit.FoldedState(page, "games:advanced") == true && advanced.FindChildren("*", nameof(Button), true, false).OfType<Button>().Any(b => b.Text == "Add a project…"),
                "the versions and their buttons aren't under the closed Advanced fold");
            if (engineReady)
            {
                // Add this game… opens Add games with the game ticked (nothing is asked of any site before its confirm).
                PageButton("add_game")!.EmitSignal(BaseButton.SignalName.Pressed);
                var fromCard = await UiTestWaitForAsync(() => AP_Atlas.UI.AddGamesDialog.Showing, "Add games from the game's card");
                UiTestExpect(fromCard.Panel.IsTicked("Atlas Test Game"), "the card's Add this game… didn't tick the game");
                fromCard.EmitSignal(AcceptDialog.SignalName.Canceled);
                await UiTestWaitAsync(0.1);
            }
            // Map packs live on the Games page. A pack Atlas hasn't read yet still counts for its game (its manifest is read);
            // its row shows in the game's Map packs section once the rescan has read it, and not on another game's page; All
            // map packs, the entry at the list's foot, lists every pack, found by typed words, a pack for no game Atlas lists
            // apart; a row's Delete… asks first, and the pack goes from the folder and the list.
            System.IO.Directory.CreateDirectory(packsDir);
            // Another spelling of the game (an accent) still names it (GameNameRule), and a community game nothing plays gets a pack ("/" for "and").
            FakeMapPack.Write(gamePack, "UI test games pack", "Atlas Tést Game");
            FakeMapPack.Write(strayPack, "UI test stray pack", "No Such Game At All");
            FakeMapPack.Write(installedPack, "UI test installed pack", "Pokémon FireRed/LeafGreen");
            // A pack arriving for a game whose page shows (a rescan's redraw) puts its Doctor… on the card.
            page.RefreshPacks();
            await UiTestWaitForAsync(() => PageButton("game_doctor"), "the card to see the pack that arrived (Doctor… for it)");
            page.Refresh();
            UiTestExpect(string.Equals(System.IO.Path.GetFullPath(PageButton("game_doctor")?.GetMeta("game_doctor").AsString() ?? ""), System.IO.Path.GetFullPath(gamePack), StringComparison.OrdinalIgnoreCase)
                && PageButton("find_pack")?.Text == "Find another map pack…", "a pack Atlas hasn't read yet doesn't count for its game on the card");
            _packManagerPanel.Rescan();
            Node? PackRow(string zip) => page.FindChildren("*", "", true, false).FirstOrDefault(n => IsInstanceValid(n) && !n.IsQueuedForDeletion() && n.HasMeta("pack_row") && n.GetMeta("pack_row").AsString() == zip);
            await UiTestWaitForAsync(() => PackRow(gamePack), "the game's pack row in its Map packs section");
            UiTestExpect(PackRow(strayPack) == null && page.FindChildren("*", "", true, false).Any(n => n.HasMeta("game_packs") && n.GetMeta("game_packs").AsString() == "Atlas Test Game"),
                "another game's pack is on this game's page, or the page has no Map packs section");
            // A game with only a map pack installed (its manifest spelled "Pokémon FireRed/LeafGreen") is listed once, under Installed,
            // after your multiworlds' games and before Official and Community.
            page.Refresh();
            UiTestExpect(page.ShownSections.Count > 1 && page.ShownSections[1].StartsWith("Installed (", StringComparison.Ordinal)
                && page.ShownSectionOf.TryGetValue("Pokemon FireRed and LeafGreen", out var frlgGroup) && frlgGroup == page.ShownSections[1]
                && page.ShownSectionOf["Atlas Test Game"] == page.ShownSections[0] && page.ShownGames.Count(g => g == "Pokemon FireRed and LeafGreen") == 1,
                $"a game with a map pack installed isn't listed once under Installed: {string.Join(" | ", page.ShownSections)}");
            TreeItem? allEntry = null;
            for (var item = tree.GetRoot()?.GetFirstChild(); item != null; item = item.GetNext())
                if (item.GetMetadata(0).VariantType == Variant.Type.Int) allEntry = item;
            UiTestExpect(allEntry != null && allEntry.GetNext() == null && allEntry.GetText(0) == $"All map packs ({System.IO.Directory.GetFiles(packsDir, "*.zip").Length})", $"the list's foot has no All map packs entry: {allEntry?.GetText(0)}");
            allEntry!.Select(0);
            await UiTestWaitForAsync(() => page.AllPacksShown && PackRow(strayPack) != null && PackRow(gamePack) != null ? page : null, "All map packs to list every pack");
            UiTestExpect(page.UnmatchedPacks.Contains("UI test stray pack") && !page.UnmatchedPacks.Contains("UI test games pack") && page.ShownPacks.Contains("UI test games pack"),
                $"All map packs doesn't set apart the pack for no game Atlas lists: shown {string.Join(", ", page.ShownPacks)}; for no game {string.Join(", ", page.UnmatchedPacks)}");
            page.FilterPacks("stray");
            UiTestExpect(page.ShownPacks.SequenceEqual(new[] { "UI test stray pack" }), $"the filter left {string.Join(", ", page.ShownPacks)}");
            page.FilterPacks("");
            var delete = PackRow(strayPack)!.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.HasMeta("delete_pack"));
            delete.EmitSignal(BaseButton.SignalName.Pressed);
            var sure = await UiTestWaitForAsync(() => GetTree().Root.FindChildren("*", nameof(ConfirmationDialog), true, false).OfType<ConfirmationDialog>()
                .FirstOrDefault(d => IsInstanceValid(d) && d.Visible && d.Title == "Delete map pack"), "Delete… to ask first");
            UiTestExpect(System.IO.File.Exists(strayPack), "the pack was deleted before the question was answered");
            sure.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitForAsync(() => !System.IO.File.Exists(strayPack) && PackRow(strayPack) == null && PackRow(gamePack) != null ? page : null, "the deleted pack to leave the folder and the list");
            page.Select("Atlas Test Game");
            // Still no YAML on the game's page: a slot's YAML lives on its Details and the Logic Tracker's banner.
            UiTestExpect(NoYaml(), "the game's page speaks of a YAML");
            // The release-files dialog: sized, a check box per file (the release's files besides the apworld), Select all; nothing downloads without Download.
            page.ListReleaseFilesForTests("Atlas Test Game");
            var files = await UiTestWaitForAsync(() => page.GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.HasMeta("release_files") && !d.IsQueuedForDeletion()), "the release-files dialog");
            await UiTestWaitForAsync(() => files.FindChildren("*", nameof(CheckBox), true, false).OfType<CheckBox>().Count() == 2 ? files : null, "the release's two files as check boxes");
            var ticks = files.FindChildren("*", nameof(CheckBox), true, false).OfType<CheckBox>().ToList();
            UiTestExpect(ticks.All(t => !t.ButtonPressed) && ticks.Any(t => t.Text.StartsWith("atlas_template.yaml")) && ticks.Any(t => t.Text.StartsWith("client.zip")), $"the files aren't listed unticked: {string.Join(", ", ticks.Select(t => t.Text))}");
            UiTestExpect(files.Size.X <= GetTree().Root.Size.X && files.Size.Y <= GetTree().Root.Size.Y && files.Size.X >= 600, $"the dialog is {files.Size}");
            files.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.Text == "Select all").EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(ticks.All(t => t.ButtonPressed), "Select all didn't tick every file");
            files.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            UiTestExpect(!github.Requests.Any(r => r.StartsWith("/dl/")), "a file was downloaded without Download");
            // A community game whose home is its Discord thread says so on its link.
            page.Select("Dark Souls Remastered");
            UiTestExpect(page.LinkTexts().Contains("Discord thread ↗"), $"a Discord home isn't named: {string.Join(", ", page.LinkTexts())}");
        }
        finally
        {
            AP_Atlas.Core.GitHubApi.TestSite = null;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null, false);
            _appSettings.ExtraApworldRepos.Remove("Atlas Test Game");
            _appSettings.ApprovedApworldSources.Remove("github.com/owner/atlas-test");
            AP_Atlas.Core.SafeFile.Delete(yaml);
            foreach (string zip in new[] { gamePack, strayPack, installedPack }) AP_Atlas.Core.SafeFile.Delete(zip);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            host.ShowTool(AP_Atlas.UI.Tool.Home);
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
        // The table lays out only the rows on screen: the history is its shown rows.
        var table = dialog.FindChildren("Table_notifications", "", true, false).OfType<AP_Atlas.UI.AtlasTable>().First();
        var rows = table.ShownRows.Select(r => r.Cells.Select(c => c.Text).ToArray()).ToList();
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

    /// <summary>Developer mode on or off as its Settings toggle does it (not saved): for scenarios that use the testing tools (Debug).</summary>
    private void SetDeveloperModeForTests(bool on)
    {
        _appSettings.DeveloperMode = on;
        ApplyConsoleTabs();
        ApplyDeveloperMode();
    }

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
        // Off: the testing tools are hidden everywhere: no Tools → Debug, no Debug on the Games page, no key rows, refused.
        var toolsMenu = _menuHbox.GetChildren().OfType<MenuButton>().First(m => m.Text == "Tools").GetPopup();
        UiTestExpect(toolsMenu.GetItemIndex(DebugSubmenuItemId) < 0 && !_commands.Run("debug.check-every-game") && !_commands.Find("debug.test-game")!.Shown(),
            "the Tools menu has Debug, or a Debug command runs or shows, with developer mode off");
        UiTestExpect(!page.IsShown("key.debug.test-game"), "a Debug command's key row shows with developer mode off");
        _gamesPage!.ShowOverviewForTests();
        UiTestExpect(!_gamesPage.DebugShown && !_gamesPage.FindChildren("*", nameof(Button), true, false).OfType<Button>().Any(b => b.HasMeta("solo_batch_button") || b.Text == Tr("Check every game")),
            "the Games overview offers the testing tools outside developer mode");

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
        // On: Tools → Debug holds the testing tools, their key rows show, the Games page has its Debug parts, and Verify
        // logic against a seed opens the Engine page's seed test.
        int debugIndex = toolsMenu.GetItemIndex(DebugSubmenuItemId);
        UiTestExpect(debugIndex >= 0 && toolsMenu.GetItemText(debugIndex) == Tr("Debug") && _debugMenu!.ItemCount == 8, "Tools → Debug isn't there in developer mode");
        UiTestExpect(page.IsShown("key.debug.test-game"), "a Debug command's key row is missing in developer mode");
        _gamesPage.ShowOverviewForTests();
        UiTestExpect(_gamesPage.DebugShown && _gamesPage.FindChildren("*", nameof(Button), true, false).OfType<Button>().Any(b => b.HasMeta("solo_batch_button")), "the Games overview has no Debug part in developer mode");
        string anyGame = _gamesPage.GameNames().First();
        _gamesPage.Select(anyGame);
        UiTestExpect(_gamesPage.DebugShown && _gamesPage.Steps().Any(s => s.Title == Tr("A solo test")), $"{anyGame}'s page has no Debug part with the solo test in developer mode");
        bool advancedFolded = AP_Atlas.UI.Kit.FoldedState(_enginePage!, AP_Atlas.UI.EnginePage.AdvancedKey) != false;
        UiTestExpect(_commands.Run("debug.verify-seed"), "Verify Logic Against a Seed… didn't run in developer mode");
        await UiTestWaitAsync(0.2);
        UiTestExpect(ShownContent() == _enginePage && _enginePage.AdvancedOpen && _enginePage.VerifyShown, "Verify Logic Against a Seed… didn't open the seed test");
        if (advancedFolded) AP_Atlas.UI.Kit.ToggleFold(_enginePage, AP_Atlas.UI.EnginePage.AdvancedKey); // Advanced as it was
        host.ShowTool(AP_Atlas.UI.Tool.Connections);

        // Off again while the Debug Log is current: Chat takes its place.
        developer.ButtonPressed = false;
        UiTestExpect(_bottomTabs.IsTabHidden(2) && _currentTerminalTab == 0, "hiding the Debug Log while it was current didn't give way to Chat");
        UiTestExpect(toolsMenu.GetItemIndex(DebugSubmenuItemId) < 0 && GodotObject.IsInstanceValid(_debugMenu) && !page.IsShown("key.debug.test-game"),
            "turning developer mode off left Tools → Debug or its key rows, or freed the submenu");

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

    private async Task AddSlotDialogAsync()
    {
        await using var server = new FakeArchipelagoServer();
        server.Slots.Add("Carol"); // the room knows the slot
        var profile = new MultiworldProfile { Name = "Add slot test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        profile.SavedStats["Tester"] = new SlotStats { GameName = "Test Game" };
        var bare = new MultiworldProfile { Name = "Bare" };
        string carolKey = AP_Atlas.Core.Annotations.SlotKey(profile.Id, "Carol");
        _profiles.Add(profile);
        _profiles.Add(bare);
        RefreshProfileList();
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        AP_Atlas.UI.AddSlotDialog? Dialog() => GetChildren().OfType<AP_Atlas.UI.AddSlotDialog>().FirstOrDefault(d => !d.IsQueuedForDeletion());
        try
        {
            // Without a server address Connect now starts off, with one on; the press adds no placeholder row.
            SelectProfile(bare);
            _addSlotButton!.EmitSignal(BaseButton.SignalName.Pressed);
            var dialog = await UiTestWaitForAsync(Dialog, "the Add a slot dialog");
            UiTestExpect(dialog.Title == "Add a slot" && !dialog.ConnectNow.ButtonPressed && !bare.Slots.Contains("New Slot"), "the dialog for a multiworld without a server starts with Connect now on, or the press added a slot");
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            SelectProfile(profile);
            _addSlotButton.EmitSignal(BaseButton.SignalName.Pressed);
            dialog = await UiTestWaitForAsync(Dialog, "the dialog for a multiworld with a server");
            UiTestExpect(dialog.ConnectNow.ButtonPressed, "Connect now isn't on for a multiworld with a server address");
            await UiTestDialogSizedAsync(dialog, "the Add a slot dialog");
            // A duplicate name keeps the dialog open and says so; nothing is added.
            dialog.NameInput.Text = "Tester";
            dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(Dialog() == dialog && dialog.ProblemText.Contains("Tester") && profile.Slots.Count == 1, $"a duplicate name was taken, or not explained (\"{dialog.ProblemText}\")");
            await UiTestDialogSizedAsync(dialog, "the Add a slot dialog with its problem line");
            // The game: typed words narrow the list; the engine line follows the choice (no engine in this folder: Set up the Atlas Engine).
            dialog.GameSearch.Text = "test game";
            dialog.GameSearch.EmitSignal(LineEdit.SignalName.TextChanged, dialog.GameSearch.Text);
            UiTestExpect(dialog.ShownGames.Contains("Test Game") && dialog.CurrentGame == "Test Game", $"typing \"test game\" lists {string.Join(", ", dialog.ShownGames)} (game: {dialog.CurrentGame})");
            UiTestExpect(dialog.StatusText.Contains("isn't set up") && dialog.StatusActionText == "Set up the Atlas Engine", $"the engine line reads \"{dialog.StatusText}\" with \"{dialog.StatusActionText}\"");
            // No YAML in the dialog: logic asks for one only when the server's data can't rebuild the world.
            UiTestExpect(!dialog.FindChildren("*", nameof(Button), true, false).OfType<Button>().Any(b => b.Text.Contains("YAML"))
                && !dialog.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(l => l.Text.Contains("YAML")), "the Add a slot dialog still offers a YAML");
            dialog.NameInput.Text = "Carol";
            // Add with Connect now: the slot is on disk with its game, the dialog is gone, and the slot connects.
            dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Carol"), "the new slot's view");
            UiTestExpect(profile.Slots.SequenceEqual(new[] { "Tester", "Carol" }) && profile.SavedStats.TryGetValue("Carol", out var carol) && carol.GameName == "Test Game"
                && !_appSettings.SlotYamlPaths.ContainsKey(carolKey)
                && DataManager.LoadProfiles().Any(p => p.Id == profile.Id && p.Slots.Contains("Carol")) && Dialog() == null,
                "the slot wasn't added with its game, saved to disk, with the dialog closed (and no YAML tied to it)");
        }
        finally
        {
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            if (_profiles.Contains(bare)) DeleteProfile(bare);
        }
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
        string yamlFile = System.IO.Path.Combine(DataManager.GetDataDirectory(), "uitest_two_players.yaml");
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

            // Unsaved edits never vanish silently: selecting another multiworld asks Save / Don't save / Cancel.
            SelectProfile(profile);
            void TypeInto(LineEdit box, string text)
            {
                box.Text = text;
                box.EmitSignal(LineEdit.SignalName.TextChanged, text);
            }
            ConfirmationDialog? Question() => GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == "Unsaved changes" && !d.IsQueuedForDeletion());
            Button DontSaveOf(ConfirmationDialog ask) => ask.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.Text == "Don't save");
            TypeInto(_nameInput, "Room test, edited");
            UiTestExpect(profile.Name == "Room test, edited" && _dirty, "typing a name didn't reach the multiworld");
            // The eye shows the password; another multiworld hides it again.
            UiTestExpect(_passwordInput.Secret, "the password shows by default");
            TypeInto(_passwordInput, "hunter2");
            _passwordToggle!.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(!_passwordInput.Secret && _passwordToggle.TooltipText == "Hide the password" && _passwordToggle.AccessibilityName == "Hide the password", "the eye didn't show the password, or isn't named for it");
            SelectProfileGuarded(other);
            var ask = await UiTestWaitForAsync(Question, "the Save / Don't save / Cancel question");
            UiTestExpect(_selectedProfile == profile, "another multiworld was selected before the question was answered");
            // Cancel: nothing moves and the edits stay.
            ask.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            UiTestExpect(Question() == null && _selectedProfile == profile && _dirty && _nameInput.Text == "Room test, edited" && profile.Password == "hunter2", "Cancel changed something");
            // Don't save: the multiworld goes back to how it was, the other is selected.
            SelectProfileGuarded(other);
            ask = await UiTestWaitForAsync(Question, "the question again");
            DontSaveOf(ask).EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(_selectedProfile == other && profile.Name == "Room test" && profile.Password == "" && _passwordInput.Secret && !DataManager.LoadProfiles().Any(p => p.Name == "Room test, edited"),
                $"Don't save kept the edit (\"{profile.Name}\", password \"{profile.Password}\"), or didn't move on");
            // Save: written to disk, then the other is selected.
            SelectProfileGuarded(profile);
            UiTestExpect(_selectedProfile == profile && Question() == null, "an unedited multiworld asked before another was selected");
            TypeInto(_nameInput, "Room test, saved");
            SelectProfileGuarded(other);
            ask = await UiTestWaitForAsync(Question, "the question before Save");
            ask.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(_selectedProfile == other && !_dirty && DataManager.LoadProfiles().Any(p => p.Id == profile.Id && p.Name == "Room test, saved" && p.RoomLink == roomLink),
                "Save didn't write the edit to disk, or didn't move on");
            SelectProfileGuarded(profile);
            UiTestExpect(_nameInput.Text == "Room test, saved" && !_dirty, "the saved name isn't shown");
            // Save brings the SLOTS list up to date (it kept the first name and slot until something else rebuilt it): the question's
            // Save above, then the page's Save after a slot is renamed (a rename alone doesn't reach it).
            string[] SidebarSlots() => _activeSessionsList.GetChildren().OfType<PanelContainer>()
                .Where(c => !c.IsQueuedForDeletion() && c.HasMeta("profile_id") && c.GetMeta("profile_id").AsString() == profile.Id)
                .Select(c => c.GetMeta("slot_name").AsString()).ToArray();
            bool SidebarNames(string name) => _activeSessionsList.GetChildren().OfType<PanelContainer>().Where(c => !c.IsQueuedForDeletion())
                .Any(c => c.GetChildren().OfType<Label>().Any(l => l.Text == name));
            UiTestExpect(SidebarNames("Room test, saved"), "the SLOTS list doesn't show the saved name");
            var bobBox = SlotRows().First(r => r.GetMeta("slot_name").AsString() == "Bob").GetChildren().OfType<LineEdit>().First();
            bobBox.Text = "Bobby";
            bobBox.EmitSignal(LineEdit.SignalName.TextSubmitted, "Bobby");
            // The rename reaches the SLOTS list at once: its card never connects a name the multiworld no longer has.
            UiTestExpect(_dirty && SidebarSlots().SequenceEqual(new[] { "Alice", "Bobby" }), $"a rename didn't reach the SLOTS list: {string.Join(", ", SidebarSlots())}");
            // A card left behind by a change made elsewhere (made here by hand) doesn't connect: it says why and the list catches up.
            profile.Slots[1] = "Zed";
            var staleConnect = _activeSessionsList.FindChildren("*", nameof(Button), true, false).OfType<Button>()
                .First(b => !b.IsQueuedForDeletion() && b.HasMeta("slot_name") && b.GetMeta("slot_name").AsString() == "Bobby" && b.GetMeta("profile_id").AsString() == profile.Id);
            staleConnect.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(_alertLog.Entries.Any(e => e.Message.StartsWith("Bobby isn't a slot of ", StringComparison.Ordinal)) && SidebarSlots().SequenceEqual(new[] { "Alice", "Zed" })
                && !_connectingSlots.Contains(SlotKey(profile.Id, "Bobby")),
                $"a stale card's Connect connected, or did nothing without a word: {string.Join(", ", SidebarSlots())}");
            profile.Slots[1] = "Bobby";
            UpdateSidebar();
            // Don't save leaves a slot connecting (or connected) under its new name with that name, and its card.
            _connectingSlots.Add(SlotKey(profile.Id, "Bobby"));
            SelectProfileGuarded(other);
            ask = await UiTestWaitForAsync(Question, "the question with a slot connecting under its new name");
            DontSaveOf(ask).EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            _connectingSlots.Remove(SlotKey(profile.Id, "Bobby"));
            UiTestExpect(_selectedProfile == other && profile.Slots.SequenceEqual(new[] { "Alice", "Bobby" }) && SidebarSlots().SequenceEqual(new[] { "Alice", "Bobby" }),
                $"Don't save renamed a connecting slot back under its connection: {string.Join(", ", profile.Slots)}; SLOTS {string.Join(", ", SidebarSlots())}");
            SelectProfileGuarded(profile);
            _saveButton!.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(!_dirty && SidebarSlots().SequenceEqual(new[] { "Alice", "Bobby" }) && DataManager.LoadProfiles().Any(p => p.Id == profile.Id && p.Slots.SequenceEqual(new[] { "Alice", "Bobby" })),
                $"Save didn't keep the SLOTS list and the file up to date: {string.Join(", ", SidebarSlots())}");
            bobBox = SlotRows().First(r => r.GetMeta("slot_name").AsString() == "Bobby").GetChildren().OfType<LineEdit>().First();
            bobBox.Text = "Bob";
            bobBox.EmitSignal(LineEdit.SignalName.TextSubmitted, "Bob");
            _saveButton.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(SidebarSlots().SequenceEqual(new[] { "Alice", "Bob" }), "the slot's old name didn't come back to the SLOTS list");
            // Leaving the page, closing Atlas and Add ask too; Cancel keeps everything as it is.
            TypeInto(_nameInput, "Room test, leaving");
            host.ShowTool(AP_Atlas.UI.Tool.Home);
            ask = await UiTestWaitForAsync(Question, "the question on leaving the page");
            UiTestExpect(ShownContent() == _connectionPanel, "the page was left before the question was answered");
            ask.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            UiTestExpect(ShownContent() == _connectionPanel && _dirty && _nameInput.Text == "Room test, leaving", "Cancel on leaving the page changed something");
            _Notification((int)NotificationWMCloseRequest);
            ask = await UiTestWaitForAsync(Question, "the question on closing Atlas");
            ask.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            UiTestExpect(!_shuttingDown && _dirty, "Cancel on closing didn't keep Atlas open with the edit");
            int count = _profiles.Count;
            OnAddProfilePressed();
            ask = await UiTestWaitForAsync(Question, "the question on Add");
            UiTestExpect(_profiles.Count == count, "a multiworld was added before the question was answered");
            DontSaveOf(ask).EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(_profiles.Count == count + 1 && _selectedProfile != profile && profile.Name == "Room test, saved", "Don't save on Add didn't add the multiworld with the edit dropped");
            DeleteProfile(_selectedProfile!);
            SelectProfile(profile);
            // Add YAML: the file is kept in Atlas's YAML folder, a slot is added per player it names (a placeholder name asks
            // for the slot's room name), each tied to the YAML; the page is dirty until saved. A slot's details show the YAML
            // and the follow switch, which saves for the slot.
            AP_Atlas.Core.SafeFile.WriteAllText(yamlFile, "name: Carol\ngame: Atlas Test Game\n---\nname: Dave{number}\ngame:\n  Atlas Test Game: 1\n  Other Test Game: 1\n");
            AddYamlToProfile(yamlFile);
            var nameAsk = await UiTestWaitForAsync(() => GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == "Slot name" && !d.IsQueuedForDeletion()), "the question for the placeholder name");
            UiTestExpect(nameAsk.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(l => l.Text.Contains("Dave{number}")), "the question doesn't show the YAML's pattern");
            var nameBox = nameAsk.FindChildren("*", nameof(LineEdit), true, false).OfType<LineEdit>().First();
            nameBox.Text = "Dave";
            nameAsk.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitAsync(0.2);
            string yamlsFolder = System.IO.Path.GetFullPath(AP_Atlas.Core.Games.GameFiles.YamlsFolder(DataManager.GetDataDirectory()));
            string carolKey = AP_Atlas.Core.Annotations.SlotKey(profile.Id, "Carol"), daveKey = AP_Atlas.Core.Annotations.SlotKey(profile.Id, "Dave");
            UiTestExpect(profile.Slots.SequenceEqual(new[] { "Alice", "Bob", "Carol", "Dave" }) && _dirty, $"the YAML's players weren't added as slots: {string.Join(", ", profile.Slots)} (dirty: {_dirty})");
            UiTestExpect(_appSettings.SlotYamlPaths.TryGetValue(carolKey, out var carolYaml) && System.IO.Path.GetFullPath(carolYaml).StartsWith(yamlsFolder, StringComparison.OrdinalIgnoreCase)
                && _appSettings.SlotYamlPaths.TryGetValue(daveKey, out var daveYaml) && daveYaml == carolYaml, "the kept YAML isn't tied to each of its slots");
            UiTestExpect(profile.SavedStats.TryGetValue("Carol", out var carolStats) && carolStats.GameName == "Atlas Test Game" && !profile.SavedStats.ContainsKey("Dave"),
                "a one-game player's game wasn't noted (or a two-game player's was guessed)");
            UiTestExpect(SlotRows().Select(row => row.GetMeta("slot_name").AsString()).SequenceEqual(new[] { "Alice", "Bob", "Carol", "Dave" }), "the slot rows don't show the added slots");
            ToggleSlotDetails("Carol");
            await UiTestWaitAsync(0.1);
            var carolDetails = SlotDetailsOf("Carol") ?? throw new InvalidOperationException("Carol's details didn't open");
            var labels = carolDetails.FindChildren("*", nameof(Label), true, false).OfType<Label>().Select(l => l.Text).ToList();
            UiTestExpect(labels.Contains("Atlas Test Game") && labels.Any(l => l == System.IO.Path.GetFileName(carolYaml!)), $"the details don't name the game and the YAML: {string.Join(" | ", labels)}");
            var followSwitch = carolDetails.FindChildren("*", nameof(CheckBox), true, false).OfType<CheckBox>().First();
            UiTestExpect(followSwitch.ButtonPressed, "following the game's map isn't on to begin with");
            followSwitch.ButtonPressed = false;
            UiTestExpect(_appSettings.MapFollowGame.TryGetValue(carolKey, out bool follows) && !follows, "turning the follow switch off in the details didn't save for the slot");
            _appSettings.MapFollowGame.Remove(carolKey);
            // Don't save: the added slots go (the YAML stays kept, for next time).
            SelectProfileGuarded(other);
            ask = await UiTestWaitForAsync(Question, "the question after Add YAML");
            DontSaveOf(ask).EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(profile.Slots.SequenceEqual(new[] { "Alice", "Bob" }) && System.IO.File.Exists(carolYaml), "Don't save didn't drop the added slots, or dropped the kept YAML");
            _appSettings.SlotYamlPaths.Remove(carolKey);
            _appSettings.SlotYamlPaths.Remove(daveKey);
            SelectProfile(profile);
            // Fill from link asks before it replaces a typed server address; Cancel reads nothing.
            _roomLinkInput.Text = roomLink;
            int reads = site.Requests.Count;
            _fillFromRoomButton!.EmitSignal(BaseButton.SignalName.Pressed);
            var fillAsk = await UiTestWaitForAsync(() => GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == "Fill in from the room?"), "the question before replacing the address");
            fillAsk.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            UiTestExpect(site.Requests.Count == reads && profile.ServerUrl == "127.0.0.1:40000", "Cancel on Fill from link read the room, or changed the address");
            // Every box has an (i) that explains it in plain words and leads to the guide's Multiworlds section.
            var infos = _connectionPanel!.FindChildren("*", nameof(Button), true, false).OfType<Button>().Where(b => b.HasMeta("info_button")).ToList();
            UiTestExpect(infos.Count == 7 && infos.All(b => !string.IsNullOrEmpty(b.AccessibilityName) && AP_Atlas.UI.Kit.InfoOf(b) != null), $"{infos.Count} (i) buttons, not one per box, or unnamed");
            // The editor scrolls as one (the slot rows used to be squeezed under the boxes), each label beside its box in two aligned columns.
            IEnumerable<Node> Ancestors(Node node)
            {
                for (var up = node.GetParent(); up != null; up = up.GetParent()) yield return up;
            }
            var editorScroll = Ancestors(_nameInput!).OfType<ScrollContainer>().FirstOrDefault();
            UiTestExpect(editorScroll != null && Ancestors(_slotsListVBox).OfType<ScrollContainer>().FirstOrDefault() == editorScroll,
                "the editor's boxes and its slot rows don't scroll as one");
            var serverLabel = EditorGrid.GetChildren().OfType<HBoxContainer>().First(h => h.GetChildren().OfType<Label>().Any(l => l.Text == "Server address:"));
            UiTestExpect(EditorGrid.Columns == 2 && _serverInput!.GetParent() == EditorGrid && serverLabel.GlobalPosition.X < _serverInput.GlobalPosition.X
                && Math.Abs(serverLabel.GlobalPosition.Y + serverLabel.Size.Y / 2 - (_serverInput.GlobalPosition.Y + _serverInput.Size.Y / 2)) <= 4,
                $"the server label isn't beside its box: label at {serverLabel.GlobalPosition} ({serverLabel.Size}), box at {_serverInput.GlobalPosition} ({_serverInput.Size})");
            var serverInfo = infos.First(b => AP_Atlas.UI.Kit.InfoOf(b)!.Value.Title == "Server address");
            serverInfo.EmitSignal(BaseButton.SignalName.Pressed);
            var explain = await UiTestWaitForAsync(() => serverInfo.GetChildren().OfType<AcceptDialog>().FirstOrDefault(), "the server box's explanation");
            UiTestExpect(explain.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(l => l.Text.Contains("archipelago.gg:12345")), "the explanation doesn't show the address's shape");
            explain.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.Text == "Guide").EmitSignal(BaseButton.SignalName.Pressed);
            var help = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.HelpWindow>().FirstOrDefault(), "the Help window");
            UiTestExpect(help.CurrentPageId == "guide:Multiworlds", $"Guide opened \"{help.CurrentPageId}\"");
            help.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            // The Sphere Tracker box: a link that isn't a room keeps its text and says why; the host's room links once the user confirms the host made it.
            await using var sphereSite = new FakeWebSite { ContentType = "text/html" };
            const string sphereRoom = "HostRoom";
            sphereSite.Respond = path => path.StartsWith("/room/" + sphereRoom, StringComparison.Ordinal) ? (200, AP_Atlas.Core.SelfTest.SphereRoomPage("AbCdEfGhIjKlMnOpQrStUx", "HostPerson")) : (404, "");
            TypeInto(_sphereInput!, "https://example.com/room/x");
            _saveButton!.EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitForAsync(() => _statusLabel.Text.StartsWith("Sphere Tracker:", StringComparison.Ordinal) ? _statusLabel : null, "the sphere box's refusal");
            UiTestExpect(_sphereInput.Text == "https://example.com/room/x" && string.IsNullOrEmpty(profile.SphereTrackerUrl) && _dirty, "a refused sphere link was dropped or linked");
            AP_Atlas.Core.Spheres.SphereSite.TestSite = sphereSite.Site; // the fake site stands in for spheretracker.de
            TypeInto(_sphereInput, sphereSite.Site + "/room/" + sphereRoom);
            _saveButton.EmitSignal(BaseButton.SignalName.Pressed);
            var hostAsk = await UiTestWaitForAsync(() => GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == "Is this the host's room?"), "the host question");
            UiTestExpect(hostAsk.GetOkButton().Disabled, "Link was offered before the host statement was confirmed");
            hostAsk.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.ToggleMode).ButtonPressed = true;
            hostAsk.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitForAsync(() => string.IsNullOrEmpty(profile.SphereTrackerUrl) ? null : profile, "the linked room");
            UiTestExpect(profile.SphereTrackerUrl.Contains("/room/" + sphereRoom) && _sphereInput.Text == profile.SphereTrackerUrl && !_dirty, $"the room wasn't linked as the host's (\"{profile.SphereTrackerUrl}\")");
            // Home's line for a multiworld without a server address shows no empty address.
            host.ShowTool(AP_Atlas.UI.Tool.Home);
            UiTestExpect(ShownContent() != _connectionPanel && Question() == null, "leaving the page with everything saved asked, or didn't leave");
            _homePage!.Refresh();
            var lines = _homePage.FindChildren("*", nameof(Label), true, false).OfType<Label>().Select(l => l.Text).Where(t => t.StartsWith("Slots: ", StringComparison.Ordinal)).ToList();
            UiTestExpect(lines.Contains("Slots: 0"), $"Home's line for a multiworld without a server reads: {string.Join(" | ", lines)}");
            host.ShowTool(AP_Atlas.UI.Tool.Connections);

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
            AP_Atlas.Core.Spheres.SphereSite.TestSite = null;
            if (_dirty) DiscardProfileEdits(); // so leaving the page below asks nothing
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
        float windows = AP_Atlas.UI.WindowFit.WindowsScale(GetWindow().CurrentScreen);
        UiTestExpect(zoom.Selected == Array.IndexOf(ZoomSteps, 100) && Mathf.IsEqualApprox(GetWindow().ContentScaleFactor, windows), "the zoom isn't 100% to begin with");
        int step150 = Array.IndexOf(ZoomSteps, 150);
        zoom.Select(step150);
        zoom.EmitSignal(OptionButton.SignalName.ItemSelected, step150);
        UiTestExpect(_appSettings.UiZoom == 150 && Mathf.IsEqualApprox(GetWindow().ContentScaleFactor, windows * 1.5f) && DataManager.LoadSettings().UiZoom == 150, "picking 150% didn't scale the window, or wasn't saved");
        await PressAsync("Ctrl+=");
        UiTestExpect(_appSettings.UiZoom == 175 && Mathf.IsEqualApprox(GetWindow().ContentScaleFactor, windows * 1.75f) && zoom.Selected == Array.IndexOf(ZoomSteps, 175), "Ctrl+= didn't zoom in a step, or the choice doesn't show it");
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
        UiTestExpect(_appSettings.UiZoom == 100 && Mathf.IsEqualApprox(GetWindow().ContentScaleFactor, windows), "the zoom didn't go back to 100%");
        await PressAsync("Ctrl+=");
        await PressAsync("Ctrl+0");
        UiTestExpect(_appSettings.UiZoom == 100, "Ctrl+0 didn't reset the zoom");

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

        // The pins' colours: PopTracker's by default; a changed one is saved and reaches every map's legend; the reset brings PopTracker's back.
        var map = new AP_Atlas.UI.MapTrackerControl(_appSettings);
        AddChild(map);
        try
        {
            var inLogic = AP_Atlas.Core.Maps.MapPinState.InLogic;
            UiTestExpect(AP_Atlas.Core.ThemeColors.MapColour(inLogic) == Color.FromHtml("#20FF20") && AP_Atlas.Core.ThemeColors.MapColour(AP_Atlas.Core.Maps.MapPinState.OutOfLogic) == Color.FromHtml("#CF1010"),
                "the pins don't start in PopTracker's green and red");
            var pinPicker = (ColorPickerButton)page.ControlOf("map-colour-in-logic");
            pinPicker.Color = Color.FromHtml("#00A0FF");
            pinPicker.EmitSignal(ColorPickerButton.SignalName.ColorChanged, pinPicker.Color);
            await UiTestWaitAsync(0.6);
            UiTestExpect(AP_Atlas.Core.ThemeColors.MapColour(inLogic) == Color.FromHtml("#00A0FF") && DataManager.LoadSettings().MapColours.GetValueOrDefault("in-logic") == "#00A0FF",
                "a changed pin colour wasn't applied or saved");
            await UiTestWaitAsync(0.1);
            UiTestExpect(map.LegendEntries.Any(e => e.Color == Color.FromHtml("#00A0FF") && e.Text == "In logic"), "the legend didn't take the new colour");
            ((Button)page.ControlOf("map-colours-reset")).EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(AP_Atlas.Core.ThemeColors.MapColour(inLogic) == Color.FromHtml("#20FF20") && _appSettings.MapColours.Count == 0 && pinPicker.Color == Color.FromHtml("#20FF20"),
                "the reset didn't bring PopTracker's colours back");
            map.Logic = AP_Atlas.UI.MapTrackerControl.LogicShown.NotRunning;
            UiTestExpect(map.LegendEntries.Any(e => e.Text.Contains("not running")) && !map.LegendEntries.Any(e => e.Text == "In logic"), "the legend doesn't say logic isn't running");
            map.Logic = AP_Atlas.UI.MapTrackerControl.LogicShown.Hidden;
            UiTestExpect(map.LegendEntries.Any(e => e.Text.Contains("race mode")), "the legend doesn't say race mode hides logic");
        }
        finally
        {
            map.QueueFree();
        }

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
        // Map Packs is part of Games now: settings saved with it as the last tool, or a key bound to it, load as Games.
        _appSettings.LastTool = "map-packs";
        _appSettings.KeyBindings["tool.map-packs"] = "Ctrl+F6";
        DataManager.SaveSettings(_appSettings);
        var migrated = DataManager.LoadSettings();
        UiTestExpect(migrated.LastTool == "games" && StartupTool(migrated) == AP_Atlas.UI.Tool.Games && migrated.KeyBindings.GetValueOrDefault("tool.games") == "Ctrl+F6" && !migrated.KeyBindings.ContainsKey("tool.map-packs"),
            $"settings saved with Map Packs load as {migrated.LastTool}, its key {migrated.KeyBindings.GetValueOrDefault("tool.games")}");
        _appSettings.KeyBindings.Remove("tool.map-packs");
        _appSettings.KeyBindings["tools.engine"] = "Ctrl+F7";
        DataManager.SaveSettings(_appSettings);
        migrated = DataManager.LoadSettings();
        UiTestExpect(migrated.KeyBindings.GetValueOrDefault("tool.engine") == "Ctrl+F7" && !migrated.KeyBindings.ContainsKey("tools.engine"), "a key given to the engine window's command didn't go to the Atlas Engine page's");
        _appSettings.KeyBindings.Remove("tools.engine");
        _appSettings.LastTool = "hints";
        DataManager.SaveSettings(_appSettings);
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
        // A quiet (flat) kit button still shows a hover and a press: the theme's variation, not Godot's "flat".
        var quiet = AP_Atlas.UI.Kit.Button("Quiet", "a quiet one", () => { }, flat: true);
        AddChild(quiet);
        UiTestExpect(quiet.ThemeTypeVariation == AP_Atlas.UI.Kit.QuietButton && !quiet.Flat && quiet.GetThemeStylebox("hover") is StyleBoxFlat && quiet.GetThemeStylebox("pressed") is StyleBoxFlat
            && quiet.GetThemeStylebox("normal") is StyleBoxEmpty, "a quiet kit button has no hover or pressed look");
        quiet.QueueFree();
        // A checked box or switch keeps the text colour (no accent fill behind it, unlike a pressed button): readable in every theme.
        var box = new CheckBox { Text = "Checked", ButtonPressed = true };
        var toggle = new CheckButton { Text = "On", ButtonPressed = true };
        AddChild(box);
        AddChild(toggle);
        UiTestExpect(box.GetThemeColor("font_pressed_color") == AP_Atlas.Core.ThemeColors.Text && box.GetThemeColor("font_hover_pressed_color") == AP_Atlas.Core.ThemeColors.Text
            && toggle.GetThemeColor("font_pressed_color") == AP_Atlas.Core.ThemeColors.Text && button.GetThemeColor("font_pressed_color") == AP_Atlas.Core.ThemeColors.TextOnAccent,
            "a checked box's text isn't the text colour (or a pressed button's isn't the colour on the accent)");
        box.QueueFree();
        toggle.QueueFree();
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
        UiTestExpect(wordmarkBefore != null && wordmark.CustomMinimumSize.Y == 48 && wordmarkBefore.GetHeight() == 96, $"Home's wordmark isn't drawn at 48 px (twice that for sharpness): {wordmarkBefore?.GetSize()}");
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
        table.MaxRows = int.MaxValue;

        // Only the rows on screen are laid out: 3,000 rows make as many tree items as fit, the scroll bar stands for them all,
        // and showing them costs a frame no more than a few rows do.
        table.CustomMinimumSize = new Vector2(700, 420);
        await UiTestWaitAsync(0.2); // laid out at its new size
        var many = Enumerable.Range(0, 3000).Select(i => Row($"r{i}", $"Row {i:D4}", i, $"note {i}")).ToList();
        table.SortBy("count");
        table.SortBy("count"); // the second click: upward, r0 first
        AP_Atlas.Core.HitchMonitor.ResetWorst();
        table.SetRows(many);
        await UiTestWaitAsync(0.3); // the rows are measured a frame later and the window fitted
        double manyMs = AP_Atlas.Core.HitchMonitor.WorstWorkMs;
        int items = table.Tree.GetRoot()!.GetChildCount();
        UiTestExpect(table.ShownRows.Count == 3000 && items >= 3 && items <= table.VisibleRowCount && items < 100,
            $"3,000 rows made {items} tree items (expected the rows on screen, {table.VisibleRowCount})");
        UiTestExpect(table.Scroll.Visible && (int)table.Scroll.MaxValue == 3000 && (int)table.Scroll.Page == table.VisibleRowCount, $"the scroll bar doesn't stand for every row ({table.Scroll.MaxValue}, page {table.Scroll.Page})");
        UiTestExpect(manyMs < 100, $"3,000 rows took {manyMs:0} ms of a frame to show");
        UiTestExpect(table.RowAt(table.Tree.GetRoot()!.GetFirstChild())?.Key == "r0" && table.ExportText(AP_Atlas.Core.ExportFormat.Tsv).Split('\n').Length >= 3001, "the first row isn't on screen, or the export lost rows that aren't");
        // Scrolling (the table's call, the bar) changes which rows the items hold; the end clamps.
        table.ScrollTo(1500);
        UiTestExpect(table.FirstShownIndex == 1500 && table.RowAt(table.Tree.GetRoot()!.GetFirstChild())?.Key == "r1500", $"scrolling to row 1,500 shows row {table.FirstShownIndex} first");
        table.Scroll.Value = 2999;
        int last = 3000 - table.VisibleRowCount;
        UiTestExpect(table.FirstShownIndex == last && table.RowAt(table.Tree.GetRoot()!.GetFirstChild())?.Key == $"r{last}", $"the scroll bar's end shows row {table.FirstShownIndex} first (expected {last})");
        // Selecting a row far away brings it on screen with the selection on it; Down on the last row on screen scrolls one.
        string? picked = null;
        table.SelectionChanged += row => picked = row?.Key;
        table.Select("r42", scrollTo: true);
        UiTestExpect(table.Selected?.Key == "r42" && table.RowAt(table.Tree.GetSelected())?.Key == "r42" && table.FirstShownIndex <= 42 && 42 < table.FirstShownIndex + table.VisibleRowCount && picked == null,
            $"selecting row 42 didn't bring it on screen with the selection (first {table.FirstShownIndex}; a pick was reported: {picked != null})");
        int bottom = table.FirstShownIndex + table.VisibleRowCount - 1;
        table.Select($"r{bottom}");
        int firstBefore = table.FirstShownIndex;
        table.Tree.EmitSignal(Control.SignalName.GuiInput, new InputEventKey { Keycode = Key.Down, Pressed = true });
        UiTestExpect(table.FirstShownIndex == firstBefore + 1 && picked == $"r{bottom + 1}" && table.RowAt(table.Tree.GetSelected())?.Key == $"r{bottom + 1}",
            $"Down on the bottom row didn't scroll one row and pick the next (first {table.FirstShownIndex}, picked {picked})");
        table.Tree.EmitSignal(Control.SignalName.GuiInput, new InputEventKey { Keycode = Key.End, Pressed = true });
        UiTestExpect(picked == "r2999" && table.FirstShownIndex == last, "End didn't pick the last row and scroll to it");
        table.Tree.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Factor = 1 });
        UiTestExpect(table.FirstShownIndex == last - 3, $"the wheel didn't scroll three rows up ({table.FirstShownIndex})");
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
            UiTestExpect(slot.MapTracker.Logic == AP_Atlas.UI.MapTrackerControl.LogicShown.NotRunning, $"without the engine the map's logic is {slot.MapTracker.Logic}, not 'not running'");

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
        // On a fresh settings file (this run's), Properties starts wider than its minimum.
        UiTestExpect(_appSettings.SplitCenterRightOffset == -PropertiesStartWidth && _propertiesSidebar.Size.X >= PropertiesStartWidth - 2,
            $"Properties starts {_propertiesSidebar.Size.X} wide (split offset {_appSettings.SplitCenterRightOffset}), not {PropertiesStartWidth}");
        // And the bottom pane takes about a third of the content's height (the split's half squeezed the Multiworlds editor), never under its minimum.
        float contentHeight = _contentSplit.Size.Y - 8, bottomShare = bottomPane.Size.Y / contentHeight;
        UiTestExpect(_appSettings.SplitContentOffset > 0 && bottomPane.Size.Y >= BottomPaneMinHeight && bottomShare >= 0.25f && bottomShare <= 0.42f,
            $"the bottom pane starts {bottomPane.Size.Y} of {contentHeight} high (split offset {_appSettings.SplitContentOffset}), not about a third");
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
        host.ShowTool(AP_Atlas.UI.Tool.Games);
        await UiTestWaitAsync(0.05);
        UiTestExpect(_midLeftSidebar.Visible && Checked("view.explorer"), "Games' explorer isn't shown to begin with");
        // Each tool's explorer has its own width: Games starts wider than the minimum (given the room: the slots panel and
        // Properties step aside in this narrow test window, since the split clamps to what fits), Multiworlds at it; a drag is kept per tool.
        _commands!.Run("view.slots-panel");
        _commands.Run("view.properties-panel");
        await UiTestWaitAsync(0.05);
        UiTestExpect(_explorerSplit.SplitOffsets[0] == 360 && _midLeftSidebar.Size.X >= 358, $"Games' explorer starts {_midLeftSidebar.Size.X} wide (offset {_explorerSplit.SplitOffsets[0]}), not 360");
        _explorerSplit.EmitSignal(SplitContainer.SignalName.Dragged, 520);
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        await UiTestWaitAsync(0.05);
        UiTestExpect(_explorerSplit.SplitOffsets[0] == 300 && _midLeftSidebar.Size.X < 318 && !_appSettings.ExplorerSplitOffsets.ContainsKey("connections"),
            $"the Multiworlds explorer is {_midLeftSidebar.Size.X} wide (offset {_explorerSplit.SplitOffsets[0]}) after a drag on Games'");
        host.ShowTool(AP_Atlas.UI.Tool.Games);
        await UiTestWaitAsync(0.05);
        UiTestExpect(_appSettings.ExplorerSplitOffsets.TryGetValue("games", out int gamesOffset) && gamesOffset == 520 && _explorerSplit.SplitOffsets[0] == 520 && _midLeftSidebar.Size.X >= 518,
            $"the drag on Games' explorer isn't kept for it (offset {gamesOffset}, {_midLeftSidebar.Size.X} wide)");
        _appSettings.ExplorerSplitOffsets.Remove("games");
        ApplyExplorerOffset(AP_Atlas.UI.Tool.Games);
        _commands.Run("view.slots-panel");
        _commands.Run("view.properties-panel");
        await UiTestWaitAsync(0.05);
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
            host.ShowTool(AP_Atlas.UI.Tool.Games);
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

    /// <summary>Every shown row's text in a column fits its width (round 13: "Installing it gets" was cut off). Call after the rows' layout (a short wait).</summary>
    private void UiTestColumnFits(AP_Atlas.UI.AtlasTable table, int column, string what)
    {
        var font = table.Tree.GetThemeFont("font");
        int size = table.Tree.GetThemeFontSize("font_size"), width = table.Tree.GetColumnWidth(column);
        foreach (var row in table.ShownRows)
        {
            float text = font.GetStringSize(row.Cells[column].Text, HorizontalAlignment.Left, -1, size).X;
            UiTestExpect(text + 16 <= width, $"{what}: \"{row.Cells[column].Text}\" needs {text + 16:0} of {width}");
        }
    }

    /// <summary>No label in a window is cut (round 13): an unwrapped label is as wide as its text, unless it trims with its whole text in a tooltip. Call after layout.</summary>
    private void UiTestTextFits(Node root, string what)
    {
        foreach (var label in root.FindChildren("*", nameof(Label), true, false).OfType<Label>().Where(l => l.IsVisibleInTree() && l.AutowrapMode == TextServer.AutowrapMode.Off && l.Text.Length > 0))
        {
            float needed = label.GetThemeFont("font").GetStringSize(label.Text, HorizontalAlignment.Left, -1, label.GetThemeFontSize("font_size")).X;
            bool trimmedWithTip = label.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming && (label.TooltipText.Length > 0 || (label.GetParent() as Control)?.TooltipText.Length > 0);
            UiTestExpect(needed <= label.Size.X + 1 || trimmedWithTip, $"{what}: \"{label.Text}\" needs {needed:0} but has {label.Size.X:0}");
        }
    }

    /// <summary>A dialog stands as tall as its content, well under the window's height, with its OK button and its content inside it.</summary>
    private async Task UiTestDialogSizedAsync(AcceptDialog dialog, string what)
    {
        await UiTestWaitAsync(0.15); // the content's layout and the shrink that follows
        var area = AP_Atlas.UI.WindowFit.AvailableLogical(dialog).Size;
        float natural = dialog.GetContentsMinimumSize().Y;
        int expected = Math.Max((int)Math.Ceiling(natural), dialog.MinSize.Y);
        UiTestExpect(dialog.Size.Y <= expected + 8, $"{what} is {dialog.Size.Y} tall for content of {natural}");
        UiTestExpect(dialog.Size.Y < area.Y * 0.8f, $"{what} stands {dialog.Size.Y} tall in a window of {area.Y}");
        var ok = dialog.GetOkButton();
        var okRect = ok.GetGlobalRect();
        UiTestExpect(ok.IsVisibleInTree() && okRect.Position.Y >= 0 && okRect.End.Y <= dialog.Size.Y + 1 && okRect.End.X <= dialog.Size.X + 1, $"{what}'s OK button is outside the window: {okRect} in {dialog.Size}");
        var content = dialog.GetChildren().OfType<Control>().FirstOrDefault();
        UiTestExpect(content == null || content.GetGlobalRect().End.Y <= okRect.Position.Y + 1, $"{what}'s content runs under its buttons");
    }

    private async Task DialogsStandAsTallAsTheirContentAsync()
    {
        const string fourLines = "A confirmation whose text runs to several lines once it's wrapped at the dialog's width, so the dialog has to measure it at that width and not before, which is where a dialog as tall as the screen came from; the fix sizes the window to the content after it has laid out.";
        ConfirmationDialog? Find(string title) => GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == title && !d.IsQueuedForDeletion());
        AP_Atlas.UI.Dialogs.Confirm(this, "Tall?", fourLines, "OK", () => { });
        var confirm = await UiTestWaitForAsync(() => Find("Tall?"), "the confirmation");
        await UiTestDialogSizedAsync(confirm, "a confirmation");
        confirm.EmitSignal(AcceptDialog.SignalName.Canceled);
        AP_Atlas.UI.Dialogs.Confirm(this, "Tall with a requirement?", fourLines, "OK", () => { }, "I have read this");
        var required = await UiTestWaitForAsync(() => Find("Tall with a requirement?"), "the confirmation with a requirement");
        await UiTestDialogSizedAsync(required, "a confirmation with a requirement");
        required.EmitSignal(AcceptDialog.SignalName.Canceled);
        AP_Atlas.UI.Dialogs.Prompt(this, "A name?", fourLines, "", "Player1", "OK", _ => { });
        var prompt = await UiTestWaitForAsync(() => Find("A name?"), "the prompt");
        await UiTestDialogSizedAsync(prompt, "a prompt");
        prompt.EmitSignal(AcceptDialog.SignalName.Canceled);
        AP_Atlas.UI.Dialogs.SaveChanges(this, "Something", () => { }, () => { }, () => { }, text => Tr(text));
        var save = await UiTestWaitForAsync(() => Find("Unsaved changes"), "Save changes");
        await UiTestDialogSizedAsync(save, "Save changes");
        save.EmitSignal(AcceptDialog.SignalName.Canceled);
        // A question says the one thing to decide; the details wait under "More information", and the dialog grows to them.
        AP_Atlas.UI.Dialogs.Confirm(this, "Short?", "Do the thing?", "OK", () => { }, null, null, fourLines);
        var brief = await UiTestWaitForAsync(() => Find("Short?"), "the short confirmation");
        await UiTestDialogSizedAsync(brief, "a short confirmation");
        var more = brief.FindChildren("MoreInfoButton", "", true, false).OfType<Button>().First();
        var folded = brief.FindChildren("*", nameof(Label), true, false).OfType<Label>().First(l => l.Text == fourLines);
        UiTestExpect(!folded.IsVisibleInTree(), "the details show before More information is pressed");
        float shortHeight = brief.Size.Y;
        more.EmitSignal(BaseButton.SignalName.Pressed);
        await UiTestDialogSizedAsync(brief, "a confirmation with its details open");
        UiTestExpect(folded.IsVisibleInTree() && brief.Size.Y > shortHeight, $"More information didn't show the details, or the dialog didn't grow to them ({shortHeight} → {brief.Size.Y})");
        // Resized by the user, a dialog keeps that size when its content changes, and when it's as tall as the window
        // (it snapped back to its first size).
        var userSize = new Vector2I(brief.Size.X + 160, brief.Size.Y + 120);
        brief.Size = userSize;
        await UiTestWaitAsync(0.1);
        more.EmitSignal(BaseButton.SignalName.Pressed);
        await UiTestWaitAsync(0.25);
        UiTestExpect(AP_Atlas.UI.WindowFit.UserSized(brief) && brief.Size == userSize, $"the dialog the user resized went back to {brief.Size} (the user made it {userSize})");
        var tallest = AP_Atlas.UI.WindowFit.AvailableLogical(brief).Size.Y - 8;
        brief.Size = new Vector2I(userSize.X, tallest);
        AP_Atlas.UI.WindowFit.Fit(brief);
        AP_Atlas.UI.WindowFit.RequestShrink(brief);
        await UiTestWaitAsync(0.25);
        UiTestExpect(brief.Size == new Vector2I(userSize.X, tallest), $"the dialog the user made as tall as the window was shrunk to {brief.Size}");
        // A dialog stands out: its frame's edge and title strip are the accent, with a shadow.
        UiTestExpect(brief.GetThemeStylebox("embedded_border") is StyleBoxFlat frame && frame.BorderColor == AP_Atlas.Core.ThemeColors.Accent && frame.ShadowSize > 0,
            "a dialog's frame doesn't stand out from the page");
        brief.EmitSignal(AcceptDialog.SignalName.Canceled);
        // A question asked from another window (a test's window) opens over that window, inside Atlas's.
        var asker = new AcceptDialog { Title = "Asking window", Exclusive = false };
        asker.AddChild(new Label { Text = "A window that asks something", CustomMinimumSize = new Vector2(360, 120) });
        AddChild(asker);
        asker.Popup(new Rect2I(new Vector2I(60, 80), new Vector2I(420, 220)));
        await UiTestWaitAsync(0.1);
        var inside = new Control();
        asker.AddChild(inside);
        AP_Atlas.UI.Dialogs.Confirm(inside, "Over the asker?", "Answer this?", "OK", () => { });
        var nested = await UiTestWaitForAsync(() => inside.GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == "Over the asker?"), "the question asked from another window");
        await UiTestDialogSizedAsync(nested, "a question asked from another window");
        var askerCentre = asker.Position + asker.Size / 2;
        var nestedCentre = nested.Position + nested.Size / 2;
        var visible = GetViewport().GetVisibleRect();
        UiTestExpect(Math.Abs(nestedCentre.X - askerCentre.X) <= 40 && nested.Position.X >= 0 && nested.Position.Y >= 0 && nested.Position.X + nested.Size.X <= visible.Size.X && nested.Position.Y + nested.Size.Y <= visible.Size.Y,
            $"the question stands at {nested.Position} ({nested.Size}), not over the window that asked at {asker.Position} ({asker.Size})");
        nested.EmitSignal(AcceptDialog.SignalName.Canceled);
        asker.QueueFree();
        await UiTestWaitAsync(0.1);
    }

    private async Task ActivityBarAsync()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var order = string.Join(",", _activityBar.Order.Select(t => t.Title));
        UiTestExpect(order == "Home,Map Tracker,Key Items,Logic Tracker,Item History,Hints,Multiworlds,Cheese Tracker,Sphere Tracker,Games,Settings,Atlas Engine", $"the activity bar's order is {order}");
        UiTestExpect(_activityBar.CaptionOf(AP_Atlas.UI.Tool.Home) == "" && _activityBar.CaptionOf(AP_Atlas.UI.Tool.MapTracker) == "ATLAS\nTOOLS" && _activityBar.CaptionOf(AP_Atlas.UI.Tool.Connections) == "ATLAS\nTOOLS"
            && _activityBar.CaptionOf(AP_Atlas.UI.Tool.CheeseTracker) == "EXTERNAL\nTOOLS" && _activityBar.CaptionOf(AP_Atlas.UI.Tool.Games) == "ATLAS\nCONFIG",
            "the groups aren't captioned as designed");
        var noIcon = AP_Atlas.UI.Tool.All.Where(t => !AP_Atlas.UI.LucideIcons.Names.Contains(t.Icon) || _activityBar.ButtonOf(t).Icon == null).Select(t => t.Title).ToList();
        UiTestExpect(noIcon.Count == 0, $"tools without a shipped icon: {string.Join(", ", noIcon)}");
        // The names under the icons (the window of the test is tall enough), the full titles for screen readers, the captions on bands.
        UiTestExpect(!_activityBar.Compact, "the bar shows icons alone in a window tall enough for the names");
        var misnamed = AP_Atlas.UI.Tool.All.Where(t => _activityBar.ButtonOf(t).Text != t.ShortTitle || _activityBar.ButtonOf(t).AccessibilityName != t.Title).Select(t => t.Title).ToList();
        UiTestExpect(misnamed.Count == 0 && _activityBar.CaptionOf(AP_Atlas.UI.Tool.Engine) == "ATLAS\nCONFIG", $"bar buttons without their short name under the icon and their full title for screen readers: {string.Join(", ", misnamed)}");
        var shortNames = string.Join(",", _activityBar.Order.Select(t => t.ShortTitle));
        UiTestExpect(shortNames == "Home,Map,Items,Logic,History,Hints,Worlds,Cheese,Spheres,Games,Settings,Engine", $"the short names are {shortNames}");
        var bands = _activityBar.FindChildren("*", nameof(PanelContainer), true, false).OfType<PanelContainer>().Where(p => p != _activityBar).ToList();
        UiTestExpect(bands.Count == 3 && bands.All(b => b.GetThemeStylebox("panel") is StyleBoxFlat box && box.BgColor == AP_Atlas.Core.ThemeColors.AccentTint), $"{bands.Count} caption bands, not three in the accent's tint");
        UiTestExpect(bands.All(b => b.GetChild<Label>(0).Text.Split('\n').Length == 2 && b.GetChild<Label>(0).Visible) && _activityBar.ButtonOf(AP_Atlas.UI.Tool.Home).GetIndex() == 0,
            "the captions aren't two lines each, or Home has a band above it");
        UiTestExpect(_activityBar.ButtonOf(AP_Atlas.UI.Tool.Hints).TooltipText.Contains("Ctrl+6"), $"the Hints button's tooltip is \"{_activityBar.ButtonOf(AP_Atlas.UI.Tool.Hints).TooltipText}\"");

        // Pressing a button shows the tool; switching a tool any other way lights its button and names it in the header.
        host.ShowTool(AP_Atlas.UI.Tool.Connections);
        _activityBar.ButtonOf(AP_Atlas.UI.Tool.Games).EmitSignal(BaseButton.SignalName.Pressed);
        await UiTestWaitAsync(0.05);
        UiTestExpect(ShownContent() == _gamesPage && _activityBar.Selected == AP_Atlas.UI.Tool.Games, "pressing the Games button didn't show Games");
        await PressAsync("Ctrl+9");
        UiTestExpect(ShownContent() == _sphereTab && _activityBar.Selected == AP_Atlas.UI.Tool.SphereTracker
            && _activityBar.ButtonOf(AP_Atlas.UI.Tool.SphereTracker).ButtonPressed && !_activityBar.ButtonOf(AP_Atlas.UI.Tool.Games).ButtonPressed,
            "Ctrl+9 didn't light the Sphere Tracker's button alone");
        UiTestExpect(_toolTitle.Text == "Sphere Tracker", $"the tool header says \"{_toolTitle.Text}\"");

        // The Engine button (the bar's last) shows the Atlas Engine page, lit like any tool.
        _activityBar.ButtonOf(AP_Atlas.UI.Tool.Engine).EmitSignal(BaseButton.SignalName.Pressed);
        await UiTestWaitAsync(0.05);
        UiTestExpect(ShownContent() == _enginePage && _activityBar.Selected == AP_Atlas.UI.Tool.Engine && _activityBar.ButtonOf(AP_Atlas.UI.Tool.Engine).ButtonPressed && _toolTitle.Text == "Atlas Engine",
            "the Engine button didn't show the Atlas Engine page");
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
        int listed = Rows(tree).Count, expected = _commands!.All.Count(c => c.Shown() && c.Enabled()) - 1; // not itself; the Debug Log and Tools → Debug wait for developer mode
        UiTestExpect(listed == expected, $"the palette lists {listed} commands, not every command that can run but itself ({expected})");
        UiTestExpect(Rows(tree).Any(r => r[0] == "Map Tracker" && r[1] == "Ctrl+2"), "the palette doesn't show a command's key");

        void Type(string text)
        {
            input.Text = text;
            input.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        Type("gam");
        await UiTestWaitAsync(0.05);
        var rows = Rows(tree);
        UiTestExpect(rows.Count == 1 && rows[0][0] == "Games", $"typing \"gam\" lists {string.Join("; ", rows.Select(r => r[0]))}");
        Type("zzz");
        await UiTestWaitAsync(0.05);
        UiTestExpect(Rows(tree).Count == 0 && palette.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(l => l.Visible && l.Text == "No command matches."),
            "a word nothing matches doesn't say so");
        Type("gam");
        await UiTestWaitAsync(0.05);
        // Enter runs the pick and closes the palette, which frees itself.
        input.EmitSignal(Control.SignalName.GuiInput, AP_Atlas.UI.CommandKeys.ToEvent("Enter")!);
        await UiTestWaitForAsync(() => ShownContent() == _gamesPage ? this : null, "Games to show after Enter in the palette");
        await UiTestWaitForAsync(() => !IsInstanceValid(palette) ? this : null, "the palette to close and free itself");

        // Escape closes it and runs nothing.
        await PressAsync("Ctrl+Shift+P");
        var again = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.CommandPalette>().FirstOrDefault(p => p.Visible), "the command palette again");
        again.FindChildren("*", nameof(LineEdit), true, false).OfType<LineEdit>().First().EmitSignal(Control.SignalName.GuiInput, AP_Atlas.UI.CommandKeys.ToEvent("Escape")!);
        await UiTestWaitForAsync(() => !IsInstanceValid(again) ? this : null, "the palette to close on Escape");
        UiTestExpect(ShownContent() == _gamesPage, "Escape ran a command");
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

    private async Task HostileServerAsync()
    {
        // What no real server sends, all at once: the connection library and Atlas must take it without a crash, a freeze
        // or a flood of log lines (the unit tests, SessionManagerTests.Hostile.cs, take each kind apart).
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("4057113e4057113e4057113e4057113e4057113e",
            new Dictionary<string, long> { ["Sword"] = 1000, ["Shield"] = 1001 }, new Dictionary<string, long> { ["Chest"] = 2000, ["Cave"] = 2001 });
        string longName = new('n', 100_000);
        server.EditConnected = (_, connected) =>
        {
            connected["players"] = new JArray(
                new JObject { ["team"] = 0, ["slot"] = 1, ["name"] = "Tester", ["alias"] = "Tester" },
                new JObject { ["team"] = 0, ["slot"] = 2_000_000_000, ["name"] = "Huge", ["alias"] = "Huge" },
                new JObject { ["team"] = 5_000, ["slot"] = 1, ["name"] = "Far", ["alias"] = "Far" },
                new JObject { ["team"] = 0, ["slot"] = 4, ["name"] = longName, ["alias"] = longName });
            connected["checked_locations"] = new JArray(2000);
            connected["missing_locations"] = new JArray(2001);
            return connected;
        };
        var profile = new MultiworldProfile { Name = "UI test (hostile server)", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        int socketErrors = 0, errors = 0;
        void Count(string line, string level)
        {
            if (line.Contains("Socket Error", StringComparison.Ordinal)) System.Threading.Interlocked.Increment(ref socketErrors);
            if (level == "ERROR") System.Threading.Interlocked.Increment(ref errors);
        }
        AP_Atlas.Core.Logger.OnLogMessage += Count;
        try
        {
            // Shown after a multiworld of several slots (whose slot list has a Connect All button), as in the full run: the
            // connecting overlay once reached for that freed button and logged an error for every connection.
            var several = new MultiworldProfile { Name = "UI test (several slots)", ServerUrl = server.Url.ToString() };
            several.Slots.Clear();
            several.Slots.AddRange(new[] { "One", "Two" });
            _profiles.Add(several);
            SelectProfile(several);
            await UiTestWaitAsync(0.1);
            SelectProfile(profile);
            DeleteProfile(several);
            await UiTestWaitAsync(0.1);
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            await UiTestWaitAsync(0.5);
            AP_Atlas.Core.HitchMonitor.ResetWorst();
            // The fake server runs inside Atlas: what it sends is written on a thread of its own, so the frames measure Atlas's work only.
            await Task.Run(async () =>
            {
                for (int i = 0; i < 100; i++) await server.SendRawAsync("not json " + i);
                // 5,000 items: repeated, unknown, negative, from players nobody is.
                var items = Enumerable.Range(0, 5_000).Select(i => new JObject
                {
                    ["item"] = (i % 4) switch { 0 => 1000L, 1 => 990_000L + i, 2 => -i, _ => 1001L },
                    ["location"] = (i % 3) switch { 0 => 2000L, 1 => 880_000L + i, _ => -i },
                    ["player"] = (i % 5) switch { 0 => 1, 1 => 77, 2 => 0, 3 => -3, _ => 4 },
                    ["flags"] = i % 8,
                    ["class"] = "NetworkItem"
                });
                await server.SendToSlotAsync("Tester", new JObject { ["cmd"] = "ReceivedItems", ["index"] = 0, ["items"] = new JArray(items) });
                await server.AddHintsAsync(Enumerable.Range(0, 2_000).Select(i =>
                    FakeArchipelagoServer.Hint(1, 1, 770_000 + i, 660_000 + i, found: i % 2 == 0, status: i % 5, entrance: i % 100 == 0 ? longName : "")).ToArray());
                await server.BroadcastAsync(Enumerable.Range(0, 5_000).Select(i => server.Chat("flood " + i)).ToArray());
                await server.SendToSlotAsync("Tester", Enumerable.Range(0, 5_000).Select(i => new JObject { ["cmd"] = "Bounced", ["tags"] = new JArray("DeathLink"), ["data"] = new JObject { ["source"] = i == 0 ? longName : "Someone" } }).ToArray());
                await server.SendToSlotAsync("Tester", new JObject
                {
                    ["cmd"] = "RoomUpdate",
                    ["checked_locations"] = new JArray(Enumerable.Range(0, 10_000).Select(i => 550_000L + i)),
                    ["players"] = new JArray(new JObject { ["team"] = 3, ["slot"] = 9, ["name"] = "Stranger", ["alias"] = "Stranger" })
                });
            });
            await UiTestWaitForAsync(() => slot.Session.Items.AllItemsReceived.Count == 5_000 && slot.Model.CurrentHints?.Length == 2_000 &&
                slot.ChatHistory.Any(e => e.APMessage?.ToString().EndsWith("flood 4999", StringComparison.Ordinal) == true) ? slot : null,
                "the items, the hints and the flood of lines", seconds: 60);
            await UiTestWaitAsync(0.5);
            var steps = new List<(string What, double WorkMs, string Report)> { ("arriving", AP_Atlas.Core.HitchMonitor.WorstWorkMs, AP_Atlas.Core.HitchMonitor.WorstWorkReport) };
            foreach (var tool in new[] { AP_Atlas.UI.Tool.ItemHistory, AP_Atlas.UI.Tool.Hints, AP_Atlas.UI.Tool.LogicTracker, AP_Atlas.UI.Tool.KeyItems, AP_Atlas.UI.Tool.MapTracker, AP_Atlas.UI.Tool.Connections })
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
            GD.Print("UITEST INFO Hostile server: the worst frame's work " + string.Join(", ", steps.Select(s => $"{s.What} {s.WorkMs:0} ms")));

            UiTestExpect(_sessions.IsLoggedIn(new AP_Atlas.Core.Connections.SlotId(profile.Id, "Tester")), "the slot didn't stay connected");
            var players = slot.Session.Players;
            UiTestExpect(players.AllPlayers.All(p => p != null) && players.GetPlayerAlias(1) == "Tester" && players.GetPlayerAlias(4)?.Length == AP_Atlas.Core.Connections.NameLimits.MaxName,
                "the login's players weren't made usable");
            UiTestExpect(socketErrors == 1, $"the unreadable messages put {socketErrors} socket error line(s) in the log (one is expected)");
            UiTestExpect(errors == 0, $"{errors} error line(s) were logged");
            var slowest = steps.MaxBy(s => s.WorkMs);
            UiTestExpect(slowest.WorkMs < 150, $"a frame's work took {slowest.WorkMs:0} ms ({slowest.What}), garbage collection left out (the guard is 150 ms): {slowest.Report}");

            // Item History's class filters choose the rows: without Progression, the 2,500 items whose flags lack it.
            host.ShowTool(AP_Atlas.UI.Tool.ItemHistory);
            var historyTable = slot.ItemHistoryView.FindChildren("Table_item-history", "", true, false).OfType<AP_Atlas.UI.AtlasTable>().FirstOrDefault();
            var progressionToggle = slot.ItemHistoryView.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.Text == "Progression");
            UiTestExpect(historyTable != null && progressionToggle != null, "Item History's table or its Progression filter wasn't found");
            progressionToggle!.ButtonPressed = false;
            await UiTestWaitAsync(0.3);
            UiTestExpect(historyTable!.ShownRows.Count == 2500 && historyTable.CountLabel.Text == "2,500 of 5,000 rows",
                $"without Progression, Item History shows {historyTable.ShownRows.Count} rows ({historyTable.CountLabel.Text}); 2,500 of 5,000 expected");
            progressionToggle.ButtonPressed = true;
            await UiTestWaitAsync(0.3);
            UiTestExpect(historyTable.ShownRows.Count == 5000, $"with every filter on, Item History shows {historyTable.ShownRows.Count} rows");

            // A scout the server never answers (the fake doesn't) is given up after a time, and asked again the next time.
            var scoutLimit = AP_Atlas.Core.SlotModel.ScoutTimeout;
            AP_Atlas.Core.SlotModel.ScoutTimeout = TimeSpan.FromSeconds(1);
            try
            {
                int scouts = server.Count("LocationScouts");
                var scouted = await slot.Model.ScoutCheckedLocationAsync(2000).WaitAsync(TimeSpan.FromSeconds(5));
                UiTestExpect(scouted == null && server.Count("LocationScouts") == scouts + 1, $"a scout the server never answers wasn't given up ({server.Count("LocationScouts") - scouts} sent)");
                await slot.Model.ScoutCheckedLocationAsync(2000).WaitAsync(TimeSpan.FromSeconds(5));
                UiTestExpect(server.Count("LocationScouts") == scouts + 2, "a scout that was given up wasn't asked again the next time");
            }
            finally
            {
                AP_Atlas.Core.SlotModel.ScoutTimeout = scoutLimit;
            }
        }
        finally
        {
            AP_Atlas.Core.Logger.OnLogMessage -= Count;
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
            AP_Atlas.Core.Inspector.Inspect(AP_Atlas.Core.InspectTarget.ForPack(pack));
            await UiTestWaitForAsync(() => PanelShowsText(_propertiesPanel, "Game " + hostile) ? slot : null, "the pack's details in Properties to show its game as written");

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

            // Atlas's game list knows this very file (by its SHA-256): the Doctor's overview says what the pack lab measured.
            await using var listSite = new AP_Atlas.Core.Testing.FakeWebSite();
            string zipSha = AP_Atlas.Core.EngineSetup.ApworldSources.Sha256OfPack(zip);
            listSite.Respond = path => path == "/list.json"
                ? (200, """{"format":"atlas-apworld-sources/1","built":"9999-12-31","source":"ui-test","games":[{"game":"Test Game","versions":[],"packs":[{"repo":"tests/pack","release":"v1","asset":"p.zip","sha256":"SHA","fit":100,"checks":"2 of 2"}]}]}""".Replace("SHA", zipSha))
                : (404, "");
            await AP_Atlas.Core.EngineSetup.ApworldSources.RefreshAsync(listSite.Site + "/list.json", System.Threading.CancellationToken.None);

            // The Pack Doctor window uses the pack while it's open (on its Overview: the guide is its first tab).
            OpenPackDoctor(zip, "Overview");
            var window = await UiTestWaitForAsync(() => GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>().FirstOrDefault(), "the Pack Doctor window");
            var labLine = await UiTestWaitForAsync(() => window.FindChildren("*", nameof(Label), true, false).OfType<Label>().FirstOrDefault(l => IsInstanceValid(l) && !l.IsQueuedForDeletion() && l.HasMeta("lab_line")),
                "the overview's line on what the pack lab measured for this file");
            UiTestExpect(labLine.Text.StartsWith("Atlas's pack lab linked 100% of this pack's checks (2 of 2); here ", StringComparison.Ordinal), $"the overview's lab line says \"{labLine.Text}\"");
            AP_Atlas.Core.EngineSetup.ApworldSources.ForgetRefreshed();
            await UiTestWaitForAsync(() => AP_Atlas.Core.PopTracker.PackImages.UsersOf(pack) == 2 && pack.ImagesLoaded ? window : null, "the Pack Doctor window to use the pack's images");
            // The overview's coverage rows: the name, the bar and the count on one line, what the count is made of under it.
            // (The overview is built again when the check's report arrives: each look finds the rows afresh.)
            VBoxContainer? Coverage() => window.FindChildren("*", nameof(VBoxContainer), true, false).OfType<VBoxContainer>()
                .FirstOrDefault(b => IsInstanceValid(b) && !b.IsQueuedForDeletion() && b.HasMeta("coverage_row") && b.GetMeta("coverage_row").AsString() == "Pin sections linked" && b.Size.X > 0);
            await UiTestWaitForAsync(() => Coverage() is { } b && b.GetChildCount() == 2 && b.GetChild(1) is MarginContainer && b.GetChild<HBoxContainer>(0).Size.Y is > 0 and < 40 ? b : null,
                "the overview's coverage row with its detail under the bar (beside it, the row stood many lines tall)");
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

    private async Task FixInPlaceAsync()
    {
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_fix_pack.zip");
        FakeMapPack.Write(zip, "UI test fix pack", "Test Game", locationsJson:
            """[{"name":"Cave","sections":[{"name":"Cave Chest"}],"map_locations":[{"map":"World","x":10,"y":10}]},""" +
            """{"name":"Far","sections":[{"name":"Far Chest"}],"map_locations":[{"map":"World","x":500,"y":10}]},""" +
            """{"name":"Lone","sections":[{"name":"Lone Chest"}],"map_locations":[{"map":"Broken","x":40,"y":40}]}]""");
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("aa11bb22cc33dd44ee55ff66aa77bb88cc99dd00", new Dictionary<string, long> { ["Sword"] = 1000, ["Shield"] = 1001 },
            new Dictionary<string, long> { ["Cave Chest"] = 2000, ["Far Chest"] = 2001, ["Lone Chest"] = 2002, ["Hidden Chest"] = 2003 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        string key = "";
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            var pack = await UiTestWaitForAsync(() => slot.Pack is { ImagesChecked: true } p ? p : null, "the slot's map pack, its images checked");
            key = AP_Atlas.Core.PopTracker.PackFixes.KeyFor(pack);
            var fixes = () => AP_Atlas.Core.PopTracker.PackFixes.Get(key);
            host.ShowTool(AP_Atlas.UI.Tool.MapTracker);
            var map = slot.MapTracker;
            await UiTestWaitForAsync(() => map.Fixing, "the map's fix menus to know the slot's pack");
            UiTestExpect(GetTree().Root.FindChildren("*", nameof(Button), true, false).Any(b => b.HasMeta("fix_problem") && b.GetMeta("fix_problem").AsString() == "map"), "the Map Tracker has no Fix a problem button (it sits with the maps list)");
            void Run(List<AP_Atlas.UI.FixItem> items, string startsWith)
            {
                var item = items.FirstOrDefault(i => i.Label.StartsWith(startsWith, StringComparison.Ordinal));
                UiTestExpect(item?.Run != null, $"the fix menu has no \"{startsWith}\": {string.Join(" | ", items.Select(i => i.Label))}");
                item!.Run!();
            }
            T Dialog<T>() where T : Node => GetTree().Root.GetChildren().OfType<T>().LastOrDefault(d => IsInstanceValid(d) && !d.IsQueuedForDeletion())
                ?? throw new InvalidOperationException("no " + typeof(T).Name + " opened");

            // A map without a picture: no picture of the pack's is named like it, so the menu says so; one chosen from the
            // pack's pictures is its picture at once.
            string broken = pack.Maps.Values.First(m => m.Name == "Broken").Id, world = pack.Maps.Values.First(m => m.Name == "World").Id;
            map.ShowMap(broken);
            await UiTestWaitAsync(0.1);
            var mapItems = map.FixProblemItems();
            UiTestExpect(mapItems.Any(i => i.Label.StartsWith("This map's picture is missing: none", StringComparison.Ordinal) && i.Run == null), "the menu doesn't say the map's picture is missing with no picture named like it");
            Run(mapItems, "Choose this map's picture from the pack");
            var picker = Dialog<AP_Atlas.UI.PackPicturePicker>();
            UiTestExpect(picker.Listed.Contains("images/world.png") && !picker.Listed.Contains("images/broken.png"), $"the pack's pictures aren't listed (or a broken one is): {string.Join(", ", picker.Listed)}");
            picker.ChooseForTests("images/world.png");
            await UiTestWaitForAsync(() => fixes().MapImages.Any(m => m.MapId == broken) ? map : null, "the map's picture fix");
            map.ShowMap(broken);
            await UiTestWaitForAsync(() => !map.ChecklistShown ? map : null, "the map to show the chosen picture (not its checklist)");

            // A pin moved: the next click on the map puts it there.
            map.ShowMap(world);
            await UiTestWaitAsync(0.1);
            // A right click on a pin opens its fix menu (the menus' lines are checked through PinItems below).
            async Task RightClickAsync(Control target, string what)
            {
                target.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false });
                var menu = await UiTestWaitForAsync(() => target.GetTree().Root.FindChildren("*", nameof(PopupMenu), true, false).OfType<PopupMenu>().FirstOrDefault(m => m.HasMeta("fix_menu") && m.Visible), "a fix menu on a right click on " + what);
                menu.Hide();
            }
            await RightClickAsync(map.FindChildren("*", "", true, false).OfType<AP_Atlas.UI.MapPinButton>().First(), "a pin");
            var cave = slot.EffectivePack.Locations.First(l => l.Name == "Cave");
            Run(map.PinItems(cave, 10, 10), "Move this pin");
            UiTestExpect(map.Placing?.StartsWith("Click where Cave goes", StringComparison.Ordinal) == true, $"moving a pin doesn't ask where it goes: {map.Placing}");
            map.PlaceAt(new Vector2(100, 120));
            await UiTestWaitForAsync(() => fixes().Pins.FirstOrDefault(p => p.PinPath == "Cave" && p.MapId == world && p.X == 100 && p.Y == 120), "the pin's new place");
            UiTestExpect(map.Placing == null, "the map still waits for a click after the pin was placed");

            // Which check a section shows, chosen from the Doctor's suggestions; another section marked as not a check.
            cave = slot.EffectivePack.Locations.First(l => l.Name == "Cave");
            Run(map.PinItems(cave, 100, 120), "\"Cave Chest\": show a different check");
            Dialog<AP_Atlas.UI.NamePickerDialog>().ChooseForTests(2003);
            await UiTestWaitForAsync(() => fixes().Links.FirstOrDefault(l => l.PinPath == "Cave" && l.SectionName == "Cave Chest" && l.ApLocationId == 2003), "the section's new check");
            await UiTestWaitForAsync(() => slot.PackIndex.ByLocation.TryGetValue(2003, out var shown) && shown.Any(m => m.Pin.Name == "Cave") ? slot : null, "the slot to show the section's new check");
            var far = slot.EffectivePack.Locations.First(l => l.Name == "Far");
            Run(map.PinItems(far, 500, 10), "\"Far Chest\" isn't a check");
            await UiTestWaitForAsync(() => fixes().Ignored.Contains("loc:unmatched:Far|Far Chest") && fixes().Links.Any(l => l.PinPath == "Far" && l.ApLocationId == null) ? map : null, "the section marked as not a check");

            // A check missing from the map, placed where the map was right-clicked (Cave Chest's own check has no pin now).
            Run(map.MapItems(new Vector2(50, 60)), "Add a missing check here");
            Dialog<AP_Atlas.UI.NamePickerDialog>().ChooseForTests(2000);
            await UiTestWaitForAsync(() => fixes().AddedPins.FirstOrDefault(p => p.ApLocationIds.Contains(2000) && p.MapId == world && p.X == 50 && p.Y == 60), "the placed check's pin");

            // Learning while playing: Far Chest has no pin now (marked as not a check above). Checking it makes a notice,
            // counted on the Map button; a burst (a release, a collect) makes none; Pick its pin, then a press on the pin, links it.
            var noChecks = new System.Collections.ObjectModel.ReadOnlyCollection<long>(new List<long>());
            map.UpdateLogicColors(null, noChecks, null);
            map.UpdateLogicColors(null, new System.Collections.ObjectModel.ReadOnlyCollection<long>(Enumerable.Range(9000, AP_Atlas.UI.MapTrackerControl.PlayBurst + 1).Select(i => (long)i).ToList()), null);
            UiTestExpect(!map.Notices().Any(n => n.FromPlay), "a burst of checks (a release or a collect) made notices");
            map.UpdateLogicColors(null, noChecks, null);
            map.UpdateLogicColors(null, new System.Collections.ObjectModel.ReadOnlyCollection<long>(new List<long> { 2001 }), null);
            const string unshown = "play:unshown:2001";
            var notice = map.Notices().FirstOrDefault(n => n.Key == unshown);
            UiTestExpect(notice is { FromPlay: true } && notice.Title.Contains("Far Chest", StringComparison.Ordinal) && notice.Actions.Count == 2,
                $"checking Far Chest (no pin) made no notice that names it: {string.Join(" | ", map.Notices().Select(n => n.Key + ": " + n.Title))}");
            await UiTestWaitForAsync(() => _activityBar.BadgeOf(AP_Atlas.UI.Tool.MapTracker) == map.NoticeCount && map.NoticeCount > 0 ? map : null, "the Map button to count the notices");
            map.ShowNotices(true);
            await UiTestWaitAsync(0.1);
            UiTestExpect(map.SidebarContent.FindChildren("*", nameof(PanelContainer), true, false).OfType<PanelContainer>().Any(c => c.HasMeta("play_notice") && (string)c.GetMeta("play_notice") == unshown && c.IsVisibleInTree()),
                "the Notices pane doesn't show the play notice's card");
            map.PressNotice(unshown, 0);
            UiTestExpect(map.PickingFor == "Far Chest" && !map.NoticesShown && map.Placing?.StartsWith("Click the pin that shows Far Chest", StringComparison.Ordinal) == true,
                $"Pick its pin didn't wait for a pin: picking {map.PickingFor}, bar {map.Placing}");
            far = slot.EffectivePack.Locations.First(l => l.Name == "Far");
            UiTestExpect(map.PressPinForPicking(far) && map.PickingFor == null, "the pin pressed wasn't taken for the notice's check");
            await UiTestWaitForAsync(() => fixes().Links.FirstOrDefault(l => l.PinPath == "Far" && l.SectionName == "Far Chest" && l.ApLocationId == 2001), "the picked pin's link");
            await UiTestWaitForAsync(() => !map.Notices().Any(n => n.Key == unshown) ? map : null, "the notice to go once its check has a pin");
            UiTestExpect(_activityBar.BadgeOf(AP_Atlas.UI.Tool.MapTracker) == map.NoticeCount, "the Map button's count didn't follow the notices");
            map.UpdateLogicColors(null, noChecks, null);

            // Key Items: a tile given one of the pack's pictures (the shield's own can't be read), then Undo.
            host.ShowTool(AP_Atlas.UI.Tool.KeyItems);
            var items = slot.ProgressionTracker;
            await UiTestWaitForAsync(() => items.Fixing, "Key Items' fix menus to know the slot's pack");
            UiTestExpect(items.FindChildren("*", nameof(Button), true, false).Any(b => b.HasMeta("fix_problem")), "Key Items has no Fix a problem button");
            await UiTestWaitForAsync(() => items.FindChildren("*", "", true, false).OfType<Control>().FirstOrDefault(c => c.HasMeta("tile") && c.GetMeta("tile").AsString() == "shield"), "the shield's tile");
            await RightClickAsync(items.FindChildren("*", "", true, false).OfType<Control>().First(c => c.HasMeta("tile") && c.GetMeta("tile").AsString() == "shield"), "a tile");
            Run(items.TileItems("shield", "Shield"), "Choose this tile's picture from the pack");
            Dialog<AP_Atlas.UI.PackPicturePicker>().ChooseForTests("images/sword.png");
            await UiTestWaitForAsync(() => fixes().Tiles.FirstOrDefault(x => x.Code == "shield" && !string.IsNullOrEmpty(x.ImageFile)), "the tile's picture fix");
            await UiTestWaitForAsync(() => items.TileKinds().Any(x => x.Code == "shield" && x.Picture) ? items : null, "the shield's tile to show its new picture");
            UiTestExpect(AP_Atlas.Core.PopTracker.PackFixes.Undo(key) && fixes().Tiles.All(x => x.Code != "shield" || string.IsNullOrEmpty(x.ImageFile)), "Undo didn't take the tile's picture back");
            await UiTestWaitForAsync(() => items.TileKinds().Any(x => x.Code == "shield" && !x.Picture) ? items : null, "the shield's tile to show its name again after Undo");
        }
        finally
        {
            foreach (var dialog in GetTree().Root.GetChildren().Where(n => n is AP_Atlas.UI.PackPicturePicker or AP_Atlas.UI.NamePickerDialog)) dialog.QueueFree();
            if (key.Length > 0) AP_Atlas.Core.PopTracker.PackFixes.Reset(key);
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            AP_Atlas.Core.SafeFile.Delete(zip);
        }
    }

    private async Task PicturelessMapsAsync()
    {
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_pictureless_pack.zip");
        // "Broken" is a map whose picture can't be read; "Hidden Chest" has no pin anywhere.
        // The game has its own checksum here: names other scenarios cached for "Test Game" lack these locations.
        FakeMapPack.Write(zip, "UI test pictureless pack", "Test Game", locationsJson:
            """[{"name":"Cave","sections":[{"name":"Cave Chest"}],"map_locations":[{"map":"World","x":10,"y":10}]},""" +
            """{"name":"Far","sections":[{"name":"Far Chest"}],"map_locations":[{"map":"World","x":500,"y":10}]},""" +
            """{"name":"Lone","sections":[{"name":"Lone Chest","access_rules":["sword"],"visibility_rules":["sword"]}],"map_locations":[{"map":"Broken","x":40,"y":40}]}]""");
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("fedcba9876543210fedcba9876543210fe1c7e57", new Dictionary<string, long> { ["Sword"] = 1000 },
            new Dictionary<string, long> { ["Cave Chest"] = 2000, ["Far Chest"] = 2001, ["Lone Chest"] = 2002, ["Hidden Chest"] = 2003 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            var pack = await UiTestWaitForAsync(() => slot.Pack is { ImagesChecked: true } p ? p : null, "the slot's map pack, its images checked");
            host.ShowTool(AP_Atlas.UI.Tool.MapTracker);
            var map = slot.MapTracker;
            int picked = 0;
            string sent = "nothing";
            map.PinPicked += (_, name, ids) => { sent = name + " with " + ids.Count + " check(s)"; if (name == "Lone" && ids.Count == 1) picked++; };
            // The map without a picture is a checklist, its picture named, its location a row that opens in Properties.
            map.ShowMap(pack.Maps.Values.First(m => m.Name == "Broken").Id);
            await UiTestWaitAsync(0.1);
            UiTestExpect(map.ChecklistShown && map.ChecklistRows().SequenceEqual(new[] { "Lone Chest" }) && map.ChecklistNote.Contains("images/broken.png"),
                $"the map without a picture isn't a checklist naming its picture: {map.ChecklistShown}, rows {string.Join(", ", map.ChecklistRows())}, note {map.ChecklistNote}");
            map.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.HasMeta("checklist_row")).EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(picked == 1, $"a checklist row doesn't open its location (it sent {sent})");
            // Properties keeps the pack's detail out of sight: the Map section names the maps; the pin, its section and the
            // pack's rules are under the folded Advanced.
            await UiTestWaitForAsync(() => _propertiesPanel.SectionCollapsed("Map") != null ? slot : null, "the Lone Chest in Properties");
            UiTestExpect(_propertiesPanel.SectionCollapsed("Map") == false && _propertiesPanel.SectionCollapsed("Advanced") == true
                && !ShowsText(_propertiesPanel.SectionBody("Map")!, "Pack access rules") && ShowsText(_propertiesPanel.SectionBody("Map")!, "Broken")
                && ShowsText(_propertiesPanel.SectionBody("Advanced")!, "Pack access rules") && ShowsText(_propertiesPanel.SectionBody("Advanced")!, "Visibility rules")
                && ShowsText(_propertiesPanel.SectionBody("Advanced")!, "Lone"), "the pack's rules aren't under Advanced (or the Map section lost its maps)");
            // The map with its picture shows its pins.
            map.ShowMap(pack.Maps.Values.First(m => m.Name == "World").Id);
            await UiTestWaitAsync(0.1);
            UiTestExpect(!map.ChecklistShown, "a map with its picture is drawn as a checklist");
            // The check no pin places: listed and counted; done, it leaves.
            await UiTestWaitForAsync(() => map.UnplacedNames.SequenceEqual(new[] { "Hidden Chest" }) ? map : null, "the check without a pin under Not on the map");
            UiTestExpect(map.UnplacedNote?.Contains("1 check not on these maps") == true, $"the map doesn't count the check without a pin: {map.UnplacedNote}");
            UiTestExpect(map.SidebarContent.FindChildren("*", nameof(Button), true, false).OfType<Button>().Any(b => b.HasMeta("unplaced_row") && b.GetMeta("unplaced_row").AsString() == "Hidden Chest" && b.ClipText), "Not on the map has no row for the check, or its row can widen the list");
            await server.BroadcastAsync(FakeArchipelagoServer.LocationsChecked(2003));
            await UiTestWaitForAsync(() => map.UnplacedNames.Count == 0 && map.UnplacedNote == null ? map : null, "the done check to leave Not on the map");
            // Follow my checks: on for a pack that can't follow the game; a check on another map shows that map.
            string worldId = pack.Maps.Values.First(m => m.Name == "World").Id, brokenId = pack.Maps.Values.First(m => m.Name == "Broken").Id;
            UiTestExpect(map.FollowChecks && map.FollowChecksBox.Visible && !map.FollowAvailable, "Follow my checks isn't on for a pack that can't follow the game");
            map.ShowMap(worldId);
            await UiTestWaitAsync(AP_Atlas.UI.MapTrackerControl.UserMoveHold.TotalSeconds + 0.2); // the map's own view change on switching isn't the user's, but be sure
            await server.BroadcastAsync(FakeArchipelagoServer.LocationsChecked(2002));
            await UiTestWaitForAsync(() => map.CurrentMapId == brokenId ? map : null, "the map to follow the check to its map");
            // While the user looks around, a check leaves the map where it is.
            map.ShowMap(worldId);
            await UiTestWaitAsync(AP_Atlas.UI.MapTrackerControl.UserMoveHold.TotalSeconds + 0.2);
            map.Canvas.SetZoom(map.Canvas.Zoom * 1.5f);
            map.FollowChecksBox.ButtonPressed = false;
            UiTestExpect(_appSettings.MapFollowChecks.TryGetValue(AP_Atlas.Core.Annotations.SlotKey(profile.Id, "Tester"), out bool kept) && !kept, "turning Follow my checks off isn't kept for the slot");
            map.FollowChecksBox.ButtonPressed = true;
            await server.BroadcastAsync(FakeArchipelagoServer.LocationsChecked(2001));
            await UiTestWaitForAsync(() => slot.Session.Locations.AllLocationsChecked.Count >= 3 ? map : null, "the check to arrive");
            UiTestExpect(map.CurrentMapId == worldId, "the map moved while the user was zooming it");
        }
        finally
        {
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            AP_Atlas.Core.SafeFile.Delete(zip);
        }
    }

    private async Task StartPackAndFixersAsync()
    {
        string packs = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory();
        string started = System.IO.Path.Combine(packs, "UI test starter pack.zip");
        string placeZip = System.IO.Path.Combine(packs, "uitest_place_pack.zip");
        var profiles = new List<MultiworldProfile>();
        string placeKey = "";
        try
        {
            // 1. Start a pack: a game with none, its names from the room.
            await using (var server = new FakeArchipelagoServer { SlotGame = "Starter Game" })
            {
                server.Slots.Add("Starter");
                server.Games["Starter Game"] = new FakeGame("57a47e0123456789abcdef0123456789abcdef01",
                    new Dictionary<string, long> { ["Sword"] = 1000, ["Shield"] = 1001 },
                    new Dictionary<string, long> { ["UP: Andre"] = 5000, ["UP: Bell Key"] = 5001, ["Route 1 - Item Ball"] = 5002 });
                var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
                profile.Slots.Clear();
                profile.Slots.Add("Starter");
                _profiles.Add(profile);
                profiles.Add(profile);
                await OnConnectSlotPressedAsync("Starter", profile);
                var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Starter"), "the slot's view");
                var start = await UiTestWaitForAsync(() => slot.MapTracker.StartPackButton is { } b && b.Text == "Start a pack for Starter Game…" ? b : null, "the empty map's Start a pack button");
                UiTestExpect(slot.Pack == null, "the game had a pack already");
                start.EmitSignal(BaseButton.SignalName.Pressed);
                var dialog = await UiTestWaitForAsync(() => AP_Atlas.UI.StartPackDialog.Showing, "the Start a pack dialog");
                dialog.ChooseForTests("UI test starter pack");
                await UiTestWaitForAsync(() => System.IO.File.Exists(started) ? slot : null, "the started pack's file");
                var pack = await UiTestWaitForAsync(() => slot.Pack is { } p && p.Manifest?.Name == "UI test starter pack" ? p : null, "the slot to use the started pack");
                await UiTestWaitForAsync(() => new long[] { 5000, 5001, 5002 }.All(id => slot.PackIndex.ByLocation.ContainsKey(id)) ? slot : null, "every location linked through the started pack's mapping");
                UiTestExpect(pack.Locations.Count(l => l.Name == "Andre" || l.Name == "Bell Key" || l.Name == "Item Ball") == 3 && pack.Maps.Count == 1,
                    $"the started pack's pins or map aren't as made: {string.Join(", ", pack.Locations.Select(l => l.FullPath))}");
                UiTestExpect(slot.PackIndex.ItemIdsFor("sword").Contains(1000) && slot.PackIndex.ItemIdsFor("shield").Contains(1001), "the started pack's items aren't linked to the game's");
            }

            // 2. The last fixer, on a pack whose "UP: Lantern" has no pin.
            AP_Atlas.Core.Testing.FakeMapPack.Write(placeZip, "UI test place pack", "Place Test Game",
                locationsJson: """[{"name":"Church","sections":[{"name":"UP: Bell Key"},{"name":"UP: Altar"}],"map_locations":[{"map":"World","x":100,"y":100}]},""" +
                               """{"name":"Hall","sections":[{"name":"UB: Torch"}],"map_locations":[{"map":"World","x":600,"y":400}]}]""");
            await using (var server = new FakeArchipelagoServer { SlotGame = "Place Test Game" })
            {
                server.Slots.Add("Placer");
                server.Games["Place Test Game"] = new FakeGame("91ace0123456789abcdef0123456789abcdef012",
                    new Dictionary<string, long> { ["Sword"] = 1000, ["Shield"] = 1001 },
                    new Dictionary<string, long> { ["UP: Bell Key"] = 6000, ["UP: Altar"] = 6001, ["UP: Lantern"] = 6002, ["UB: Torch"] = 6003 });
                var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
                profile.Slots.Clear();
                profile.Slots.Add("Placer");
                _profiles.Add(profile);
                profiles.Add(profile);
                await OnConnectSlotPressedAsync("Placer", profile);
                var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Placer"), "the slot's view");
                var pack = await UiTestWaitForAsync(() => slot.Pack is { ImagesChecked: true } p && p.Manifest?.Name == "UI test place pack" ? p : null, "the place pack");
                placeKey = AP_Atlas.Core.PopTracker.PackFixes.KeyFor(pack);
                string key = placeKey;
                // The guide places "UP: Lantern" beside the Church (the UP checks' pin), after a preview.
                OpenPackDoctor(placeZip);
                var window = await UiTestWaitForAsync(() => GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>().FirstOrDefault(w => w.Title.EndsWith("UI test place pack", StringComparison.Ordinal)), "the Pack Doctor window");
                await UiTestWaitForAsync(() => window.HasReport && window.PlannedPlacements().Count == 1 ? window : null, "the guide's plan to place the lantern");
                var plan = window.PlannedPlacements()[0];
                UiTestExpect(plan.Id == 6002 && plan.Near == "Church" && plan.MapId.Length > 0, $"the lantern isn't planned beside the Church: {plan}");
                window.PreviewPlacements();
                var confirm = await UiTestWaitForAsync(() => window.GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == "Place checks near their area"), "the placement's preview");
                UiTestExpect(!AP_Atlas.Core.PopTracker.PackFixes.Get(key).AddedPins.Any(), "a pin was placed before the preview's Place");
                confirm.EmitSignal(AcceptDialog.SignalName.Confirmed);
                var added = await UiTestWaitForAsync(() => AP_Atlas.Core.PopTracker.PackFixes.Get(key).AddedPins.FirstOrDefault(a => a.ApLocationIds.Contains(6002)), "the lantern's pin");
                UiTestExpect(added.Source == AP_Atlas.Core.PopTracker.PackPlacement.Source && Math.Abs(added.X - 100) < 80 && Math.Abs(added.Y - 100) < 80, $"the lantern's pin isn't Atlas's beside the Church: {added.Source} at {added.X},{added.Y}");
                await UiTestWaitForAsync(() => slot.EffectivePack.Locations.FirstOrDefault(l => l.PlacedByAtlas) is { } placed && slot.PackIndex.ByLocation.ContainsKey(6002) ? placed : null, "the slot to show the placed pin as Atlas's");
                window.EmitSignal(Window.SignalName.CloseRequested);
            }
        }
        finally
        {
            foreach (var w in GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>()) w.QueueFree();
            foreach (var profile in profiles) if (_profiles.Contains(profile)) DeleteProfile(profile);
            if (placeKey.Length > 0) AP_Atlas.Core.PopTracker.PackFixes.Reset(placeKey);
            AP_Atlas.Core.SafeFile.Delete(started);
            AP_Atlas.Core.SafeFile.Delete(placeZip);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
        }
    }

    private async Task SharingFixesAsync()
    {
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_share_pack.zip");
        string exported = System.IO.Path.Combine(DataManager.GetDataDirectory(), "uitest_shared_fixes.zip");
        FakeMapPack.Write(zip, "UI test share pack", "Share Test Game",
            locationsJson: """[{"name":"Cave","sections":[{"name":"Chest"}],"map_locations":[{"map":"World","x":10,"y":10}]},""" +
                           """{"name":"Far","sections":[{"name":"Lantern"}],"map_locations":[{"map":"World","x":500,"y":10}]}]""");
        await using var server = new FakeArchipelagoServer { SlotGame = "Share Test Game" };
        server.Slots.Add("Sharer"); // the fake room knows only the slots it lists
        server.Games["Share Test Game"] = new FakeGame("5ba7e0123456789abcdef0123456789abcdef012",
            new Dictionary<string, long> { ["Sword"] = 1000, ["Shield"] = 1001 },
            new Dictionary<string, long> { ["Cave Chest Room"] = 4000, ["Far Lantern Box"] = 4001 });
        await using var sentry = new AP_Atlas.Core.Testing.FakeWebSite();
        sentry.Answer = (path, _) => new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(path == "/api/43/envelope/" ? 200 : 404, System.Text.Encoding.UTF8.GetBytes("{\"id\":\"1\"}"));
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Sharer");
        _profiles.Add(profile);
        string key = "";
        TestFixesDsn = $"http://abc123@127.0.0.1:{new Uri(sentry.Site).Port}/43";
        SetUpCrashReports();
        AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.FixSuggestions, null, false);
        AP_Atlas.Core.Permissions.DenyForSession(AP_Atlas.Core.Permissions.FixSuggestions);
        try
        {
            await OnConnectSlotPressedAsync("Sharer", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Sharer"), "the slot's view");
            var pack = await UiTestWaitForAsync(() => slot.Pack is { ImagesChecked: true } p ? p : null, "the share pack, its images checked");
            key = AP_Atlas.Core.PopTracker.PackFixes.KeyFor(pack);
            var fixes = () => AP_Atlas.Core.PopTracker.PackFixes.Get(key);
            var original = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(zip)!;
            await UiTestWaitForAsync(() => AP_Atlas.Core.PopTracker.PackDoctorService.Reports.ContainsKey(key) ? slot : null, "the pack's first check");
            // Fixed by hand: a section's check, a marker that isn't one, a tile's picture.
            AP_Atlas.Core.PopTracker.PackFixActions.LinkSection(original, "Cave", "Chest", 4000, "Cave Chest Room");
            AP_Atlas.Core.PopTracker.PackFixActions.NotACheck(original, "Far", "Lantern");
            AP_Atlas.Core.PopTracker.PackFixActions.SetTileImage(original, "shield", AP_Atlas.Core.PopTracker.PackFixActions.ImportPackImage(original, "images/sword.png"));
            string picture = fixes().Tiles.First(x => x.Code == "shield").ImageFile;

            // The author's report says what fixed it.
            OpenPackDoctor(zip, "Report");
            var window = await UiTestWaitForAsync(() => GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>().FirstOrDefault(w => w.Title.EndsWith("UI test share pack", StringComparison.Ordinal)), "the Pack Doctor window");
            await UiTestWaitForAsync(() => window.HasReport ? window : null, "the Doctor's report");
            string report = window.AuthorReportText();
            UiTestExpect(report.Contains("### What fixed it") && report.Contains("section `Chest` is **Cave Chest Room**") && report.Contains("section `Lantern` isn't a check") && report.Contains("tile `shield` needed another picture"),
                "the author's report doesn't say what fixed it: " + report);

            // Export, then the fixes gone; import brings them back (with the picture) in one step.
            window.ExportFixesTo(exported);
            UiTestExpect(System.IO.File.Exists(exported), "the fixes file wasn't written");
            AP_Atlas.Core.PopTracker.PackFixes.Reset(key);
            UiTestExpect(fixes().Count == 0, "the fixes weren't reset");
            window.ImportFixesFrom(exported);
            var import = await UiTestWaitForAsync(() => AP_Atlas.UI.ImportFixesDialog.Showing, "the import's preview");
            UiTestExpect(import.Plan.Adds.Count == 3 && import.Plan.Clashes.Count == 0 && fixes().Count == 0, $"the import's preview is wrong, or it changed something first: adds {import.Plan.Adds.Count}, clashes {import.Plan.Clashes.Count}");
            import.ChooseForTests(true);
            await UiTestWaitForAsync(() => fixes().Links.Any(l => l.Subject == "link:Cave|Chest" && l.ApLocationId == 4000) && fixes().Ignored.Contains("loc:unmatched:Far|Lantern") ? window : null, "the imported links");
            var tile = fixes().Tiles.FirstOrDefault(x => x.Code == "shield");
            UiTestExpect(tile?.ImageFile is { Length: > 0 } kept && kept != picture && System.IO.File.Exists(System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PackFixes.AssetDir(key), kept)),
                $"the imported tile's picture isn't kept under its own name: {tile?.ImageFile}");

            // Both fixed the same thing: the user's stays unless they tick the file's.
            AP_Atlas.Core.PopTracker.PackFixActions.LinkSection(original, "Cave", "Chest", 4001, "Far Lantern Box");
            window.ImportFixesFrom(exported);
            import = await UiTestWaitForAsync(() => AP_Atlas.UI.ImportFixesDialog.Showing, "the second import's preview");
            var clash = import.Plan.Clashes.FirstOrDefault(r => r.Subject == "link:Cave|Chest");
            UiTestExpect(clash != null && clash.Yours!.Contains("Far Lantern Box") && clash.Theirs.Contains("Cave Chest Room"), $"the clash isn't shown with both sides: {string.Join(" | ", import.Plan.Clashes.Select(r => r.Theirs + " / " + r.Yours))}");
            import.TakeTheirs(clash!.Key);
            import.ChooseForTests(true);
            await UiTestWaitForAsync(() => fixes().Links.Any(l => l.Subject == "link:Cave|Chest" && l.ApLocationId == 4000) ? window : null, "the file's fix taken where ticked");
            UiTestExpect(AP_Atlas.Core.PopTracker.PackFixes.Undo(key) && fixes().Links.Any(l => l.Subject == "link:Cave|Chest" && l.ApLocationId == 4001), "one Undo didn't take the import back");

            // Suggest them to Atlas: the user's own fixes, ticked; untick one; what's shown is what goes.
            await UiTestWaitForAsync(() => window.HasReport ? window : null, "the report after the undo");
            window.SuggestFixes();
            var suggest = await UiTestWaitForAsync(() => AP_Atlas.UI.SuggestFixesDialog.Showing, "the suggestion dialog");
            // The user's own fixes: the section's check and the marker (the tile's picture isn't a name to suggest).
            UiTestExpect(suggest.Ticked.Count == 2 && suggest.PreviewText.Contains("\"chose\": \"Far Lantern Box\"") && !suggest.PreviewText.Contains("Sharer"),
                $"the suggestion doesn't list the user's two fixes, or names the slot: {suggest.Ticked.Count}\n{suggest.PreviewText}");
            int lantern = suggest.Ticked.ToList().FindIndex(x => x.PackWrote == "Far / Lantern");
            suggest.SetTicked(lantern, false);
            UiTestExpect(!suggest.PreviewText.Contains("Far / Lantern"), "an unticked fix is still in what would be sent");
            string shown = suggest.PreviewText;
            suggest.Choose(AP_Atlas.Core.Reports.ReportDecision.SendOnce);
            var posted = await UiTestWaitForAsync(() => sentry.Received.FirstOrDefault(), "the suggestion to be posted");
            string body = System.Text.Encoding.UTF8.GetString(posted.Body);
            UiTestExpect(posted.Path == "/api/43/envelope/" && body.Contains("\"type\":\"attachment\"") && body.Contains("\"chose\": \"Far Lantern Box\""), "the suggestion wasn't posted with its fixes attached: " + body);
            UiTestExpect(!body.Contains("Far / Lantern") && !body.Contains("Sharer") && body.Replace("\r", "").Contains(shown.Replace("\r", "").Trim()), "what was sent isn't what was shown: " + body);
            UiTestExpect(AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.FixSuggestions) && !AP_Atlas.Core.Permissions.IsAlwaysAllowed(_appSettings, AP_Atlas.Core.Permissions.FixSuggestions),
                "Send once didn't allow it for this session only");
            string copies = System.IO.Path.Combine(DataManager.GetDataDirectory(), "reports", AP_Atlas.Core.Reports.FixSuggestionService.Folder);
            await UiTestWaitForAsync(() => System.IO.Directory.Exists(copies) && System.IO.Directory.GetFiles(copies).Length > 0 ? window : null, "the copy of what was sent");
            window.EmitSignal(Window.SignalName.CloseRequested);
        }
        finally
        {
            foreach (var w in GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>()) w.QueueFree();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.FixSuggestions, null, false);
            TestFixesDsn = null;
            SetUpCrashReports();
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            if (key.Length > 0) AP_Atlas.Core.PopTracker.PackFixes.Reset(key);
            AP_Atlas.Core.SafeFile.Delete(zip);
            AP_Atlas.Core.SafeFile.Delete(exported);
        }
    }

    private async Task GuidedFixerAsync()
    {
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_guide_pack.zip");
        // The pin "Varatha" (a spear) has four rooms the game names "Clear Room n Spear Weapon", and "Stygius" (a sword) two named
        // "... Sword Weapon": a room's own name fits both weapons alike, so they wait for a person; two linked by hand teach the rule,
        // which reaches only the spear's rooms (both examples are on its pin). "Cave" / "Chest" and "Far" / "Lantern" are close to their checks.
        FakeMapPack.Write(zip, "UI test guide pack", "Guide Test Game",
            locationsJson: """[{"name":"Varatha","sections":[{"name":"Room 1"},{"name":"Room 2"},{"name":"Room 3"},{"name":"Room 4"}],"map_locations":[{"map":"World","x":200,"y":100}]},""" +
                           """{"name":"Stygius","sections":[{"name":"Room 1"},{"name":"Room 2"}],"map_locations":[{"map":"World","x":300,"y":100}]},""" +
                           """{"name":"Cave","sections":[{"name":"Chest"}],"map_locations":[{"map":"World","x":10,"y":10}]},""" +
                           """{"name":"Far","sections":[{"name":"Lantern"}],"map_locations":[{"map":"World","x":500,"y":10}]}]""");
        await using var server = new FakeArchipelagoServer { SlotGame = "Guide Test Game" };
        server.Games["Guide Test Game"] = new FakeGame("6d1de0123456789abcdef0123456789abcdef012",
            new Dictionary<string, long> { ["Sword"] = 1000 },
            new Dictionary<string, long>
            {
                ["Clear Room 1 Spear Weapon"] = 3000,
                ["Clear Room 2 Spear Weapon"] = 3001,
                ["Clear Room 3 Spear Weapon"] = 3002,
                ["Clear Room 4 Spear Weapon"] = 3003,
                ["Clear Room 1 Sword Weapon"] = 3007,
                ["Clear Room 2 Sword Weapon"] = 3008,
                ["Cave Chest Room"] = 3004,
                ["Far Lantern Box"] = 3005,
                ["Lonely Spot"] = 3006
            });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        string key = "";
        try
        {
            // The spear's rooms were declined before (as Review fixes' Undo leaves them): Atlas's own pass would link Room 3 and
            // Room 4 (each the only one of its number) by itself, and this scenario is about the rows a person decides.
            var fresh = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(zip) ?? throw new InvalidOperationException("the guide pack couldn't be read");
            string freshKey = AP_Atlas.Core.PopTracker.PackFixes.KeyFor(fresh);
            AP_Atlas.Core.PopTracker.PackFixes.Reset(freshKey);
            AP_Atlas.Core.PopTracker.PackFixes.Decline(freshKey, new[] { "link:Varatha|Room 1", "link:Varatha|Room 2", "link:Varatha|Room 3", "link:Varatha|Room 4" }, "UI test: declined before");
            AP_Atlas.Core.PopTracker.PackFixes.SetAutoLinkedVersion(freshKey, ""); // a fix file left from an earlier run keeps no record of the pass
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            var pack = await UiTestWaitForAsync(() => slot.Pack, "the slot to load the guide pack");
            key = AP_Atlas.Core.PopTracker.PackFixes.KeyFor(pack);
            var fixes = () => AP_Atlas.Core.PopTracker.PackFixes.Get(key);
            OpenPackDoctor(zip);
            var window = await UiTestWaitForAsync(() => GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>().FirstOrDefault(w => w.Title.EndsWith("UI test guide pack", StringComparison.Ordinal)), "the Pack Doctor window");
            UiTestExpect(window.CurrentTabTitle == AP_Atlas.UI.PackDoctorWindow.GuideTab, $"the Doctor opened on {window.CurrentTabTitle}, not Make this pack work");
            await UiTestWaitForAsync(() => window.HasReport && window.GuideSteps().Any(s => s.Id == "triage") ? window : null, "the guide's steps");
            await UiTestWaitAsync(1.0); // the report after the first check's automatic pass
            UiTestExpect(window.GuideSteps().First(s => s.Id == "triage").Count >= 4, $"the guide has too few rows to decide: {string.Join(", ", window.RecommendedRows().Select(r => $"{r.Key} {r.Score:0.00}"))}; links {string.Join(", ", AP_Atlas.Core.PopTracker.PackFixes.Get(key).Links.Select(l => l.Subject + "=" + l.ApLocationName))}");
            var steps = window.GuideSteps();
            UiTestExpect(new[] { "confident", "example", "triage", "unplaced", "finish" }.All(id => steps.Any(s => s.Id == id))
                && steps.First(s => s.Id == "unplaced").Count >= 1 && !steps.First(s => s.Id == "triage").Done,
                $"the guide's steps are wrong: {string.Join(" | ", steps.Select(s => $"{s.Id} {s.Count} {(s.Done ? "done" : "")}"))}");

            // One by one: skip to a Space section, choose its check by name (C), twice; the two links teach the rule.
            window.StartTriage();
            UiTestExpect(window.TriageCurrent != null, "the one-by-one review shows no row");
            // Ctrl+Z with no decision made here undoes nothing (never Atlas's own pass, which it would decline).
            int atlasBefore = fixes().Links.Count(l => l.Source == "automatic");
            UiTestExpect(atlasBefore > 0 && !window.TriageKey(Key.Z, ctrl: true) && fixes().Links.Count(l => l.Source == "automatic") == atlasBefore && fixes().Declined.All(s => s.StartsWith("link:Varatha|", StringComparison.Ordinal)),
                $"Ctrl+Z before any decision in the review took back Atlas's fixes: {atlasBefore} before, {fixes().Links.Count(l => l.Source == "automatic")} after; declined {string.Join(", ", fixes().Declined)}");
            async Task LinkSpaceByHandAsync()
            {
                for (int guard = 0; guard < 10 && window.TriageCurrent is { } current && !current.Contains("Varatha|Room", StringComparison.Ordinal); guard++)
                    UiTestExpect(window.TriageKey(Key.S), "S didn't skip the row");
                string row = window.TriageCurrent ?? throw new InvalidOperationException("no spear room left in the review");
                long id = 2999 + int.Parse(row.Substring(row.Length - 1), System.Globalization.CultureInfo.InvariantCulture); // the spear's "Room n" is "Clear Room n Spear Weapon"
                UiTestExpect(window.TriageKey(Key.C), "C didn't open the name picker");
                var picker = await UiTestWaitForAsync(() => GetTree().Root.GetChildren().OfType<AP_Atlas.UI.NamePickerDialog>().LastOrDefault(d => !d.IsQueuedForDeletion()), "the name picker");
                picker.ChooseForTests(id);
                string section = row.Substring(row.IndexOf('|') + 1);
                await UiTestWaitForAsync(() => fixes().Links.FirstOrDefault(l => l.Subject == "link:Varatha|" + section && l.ApLocationId == id && l.Source == null), "the hand-made link");
                UiTestExpect(window.TriageCurrent != row, "the review stayed on the row just linked");
            }
            await LinkSpaceByHandAsync();
            UiTestExpect(window.Patterns().Count == 0, "one hand-made link offered a rule");
            await LinkSpaceByHandAsync();
            var handIds = fixes().Links.Where(l => l.Source == null && l.ApLocationId != null).Select(l => l.ApLocationId!.Value).ToHashSet();
            var rules = await UiTestWaitForAsync(() => window.Patterns() is { Count: > 0 } found ? found : null, "a rule from the two hand-made links");
            UiTestExpect(rules[0].Matches.Count == 2 && rules[0].Matches.All(m => m.Id is >= 3000 and <= 3003 && !handIds.Contains(m.Id) && m.Key.EndsWith((m.Id - 2999).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)),
                $"the rule doesn't link the spear's other two rooms (and only them): {rules[0].Description}: {string.Join(", ", rules[0].Matches.Select(m => m.Key + " -> " + m.Name))}");
            window.PreviewPattern(0);
            var preview = await UiTestWaitForAsync(() => AP_Atlas.UI.FixPreviewDialog.Showing, "the rule's preview");
            UiTestExpect(preview.Plan.Link.Count == 2 && preview.Plan.Source == "example" && !fixes().Links.Any(l => l.Source == "example"),
                $"the rule's preview is wrong, or it changed something before Apply: {preview.Plan.Summary()}");
            preview.ChooseForTests(true);
            await UiTestWaitForAsync(() => fixes().Links.Count(l => l.Source == "example") == 2 ? window : null, "the rule's two links");

            // The review's keys on what's left: N says it isn't a check, Ctrl+Z takes that back, Enter takes the first guess.
            await UiTestWaitForAsync(() => window.HasReport && !window.RecommendedRows().Any(r => r.Key.Contains("Varatha|", StringComparison.Ordinal)) ? window : null, "the linked rows to leave the review");
            window.TriageAgain(); // the rows skipped on the way to the Space sections
            await UiTestWaitForAsync(() => window.TriageCurrent is { } now && !now.Contains("Varatha|", StringComparison.Ordinal) ? window : null, "the review to show a row left");
            string rest = window.TriageCurrent!;
            string subject = "link:" + rest.Substring("loc:unmatched:".Length);
            UiTestExpect(window.TriageKey(Key.N), "N didn't mark the row");
            await UiTestWaitForAsync(() => fixes().Ignored.Contains(rest) ? window : null, "the row marked as not a check");
            UiTestExpect(window.TriageKey(Key.Z, ctrl: true), "Ctrl+Z did nothing");
            await UiTestWaitForAsync(() => !fixes().Ignored.Contains(rest) && window.TriageCurrent == rest ? window : null, "Ctrl+Z to bring the row back to the review");
            UiTestExpect(window.TriageKey(Key.Enter), "Enter didn't take the first guess");
            await UiTestWaitForAsync(() => fixes().Links.FirstOrDefault(l => l.Subject == subject && l.ApLocationId != null), "the first guess's link");
            UiTestExpect(window.TriageKey(Key.Escape) && window.TriageCurrent == null, "Escape didn't leave the review");

            // Which version the pack fits: this seed's names are measured.
            await window.CompareVersionsAsync();
            UiTestExpect(window.VersionFits?.Any(v => v.Label.StartsWith("This seed's names", StringComparison.Ordinal) && v.Total >= 8) == true,
                $"the pack wasn't measured against this seed's names: {string.Join(" | ", window.VersionFits?.Select(v => $"{v.Label} {v.Linked}/{v.Total}") ?? Array.Empty<string>())}");
            window.EmitSignal(Window.SignalName.CloseRequested);
        }
        finally
        {
            foreach (var dialog in GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>()) dialog.QueueFree();
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            if (key.Length > 0) AP_Atlas.Core.PopTracker.PackFixes.Reset(key);
            AP_Atlas.Core.SafeFile.Delete(zip);
        }
    }

    private async Task PackDoctorAsync()
    {
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_doctor_pack.zip");
        // "Sword!" is the game's "Sword" once punctuation is ignored: the index links it by itself (no fix). The pin "Cave" / "Chest" is close to
        // "Cave Chest Room" and "Far" / "Lantern" to "Far Lantern Box", neither exact (a game location named "Cave Chest" would pair by itself
        // from the pin's name and the section's); "Shrine" / "Bell!" is the game's "Bell" once punctuation is ignored, which the index doesn't
        // read for sections, so the Doctor's first check links it as an automatic fix.
        FakeMapPack.Write(zip, "UI test doctor pack", "Doctor Test Game",
            itemsJson: """[{"name":"Sword!","type":"toggle","img":"images/sword.png","codes":"sword"},{"name":"Shield","type":"toggle","img":"images/broken.png","codes":"shield"}]""",
            locationsJson: """[{"name":"Cave","sections":[{"name":"Chest"}],"map_locations":[{"map":"World","x":10,"y":10}]},""" +
                           """{"name":"Far","sections":[{"name":"Lantern"}],"map_locations":[{"map":"World","x":500,"y":10}]},""" +
                           """{"name":"Shrine","sections":[{"name":"Bell!"}],"map_locations":[{"map":"World","x":30,"y":10}]}]""");
        await using var server = new FakeArchipelagoServer { SlotGame = "Doctor Test Game" };
        server.Games["Doctor Test Game"] = new FakeGame("d0c70123456789abcdef0123456789abcdef0123",
            new Dictionary<string, long> { ["Sword"] = 1000, ["Shield"] = 1001 }, new Dictionary<string, long> { ["Cave Chest Room"] = 2000, ["Far Lantern Box"] = 2001, ["Bell"] = 2002 });
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        string key = "";
        try
        {
            // Cave and Far were declined before (as Review fixes' Undo leaves them): Atlas's pass leaves them to the user here, so the
            // Recommended tab has them; it links the exact Bell by itself.
            var fresh = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(zip) ?? throw new InvalidOperationException("the doctor pack couldn't be read");
            string freshKey = AP_Atlas.Core.PopTracker.PackFixes.KeyFor(fresh);
            AP_Atlas.Core.PopTracker.PackFixes.Reset(freshKey);
            AP_Atlas.Core.PopTracker.PackFixes.Decline(freshKey, new[] { "link:Cave|Chest", "link:Far|Lantern" }, "UI test: declined before");
            AP_Atlas.Core.PopTracker.PackFixes.SetAutoLinkedVersion(freshKey, ""); // a fix file left from an earlier run keeps no record of the pass
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            var pack = await UiTestWaitForAsync(() => slot.Pack, "the slot to load the doctor pack");
            key = AP_Atlas.Core.PopTracker.PackFixes.KeyFor(pack);
            // The first check links the exact match by itself; the tiles the index links by name get no fix.
            var auto = await UiTestWaitForAsync(() => AP_Atlas.Core.PopTracker.PackFixes.Get(key).Links.FirstOrDefault(l => l.Subject == "link:Shrine|Bell!"), "the Doctor to link the exact match by itself");
            UiTestExpect(auto.Automatic && auto.ApLocationId == 2002 && auto.ApLocationName == "Bell", $"the automatic link is wrong: {auto.ApLocationName} ({auto.ApLocationId}), automatic {auto.Automatic}");
            UiTestExpect(AP_Atlas.Core.PopTracker.PackFixes.Get(key).Tiles.Count == 0 && slot.PackIndex.ItemIdsFor("sword").Contains(1000), "a tile the index links by name got a fix too, or \"Sword!\" wasn't paired with \"Sword\"");
            // The pack's row (in its game's Map packs section on the Games page) says what's left to review, and its button
            // opens the Doctor on the Recommended tab, which lists the pin that isn't exact and not the linked tile; Apply
            // writes the fix and says so.
            ShowGamePage("Doctor Test Game");
            var review = await UiTestWaitForAsync(() => _packManagerPanel.ReviewButtonFor(key) is { Visible: true } b
                && b.Text == $"Review {AP_Atlas.Core.PopTracker.PackDoctorService.Reports[key].NeedsReview.Count()}" ? b : null, "the pack row's Review button with the count to review");
            review.EmitSignal(BaseButton.SignalName.Pressed);
            var window = await UiTestWaitForAsync(() => GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>().FirstOrDefault(), "the Pack Doctor window");
            UiTestExpect(window.CurrentTabTitle == "Recommended", $"the Review button opened the Doctor on {window.CurrentTabTitle}, not Recommended");
            // The report after the automatic link (a check queued by the link, or the window's own) no longer suggests the linked tile.
            await UiTestWaitForAsync(() => window.HasReport && !window.RecommendedRows().Any(r => r.Key == "tile:unlinked:sword") ? window : null, "the report after the automatic link, without the linked tile");
            var rows = window.RecommendedRows();
            var pin = rows.FirstOrDefault(r => r.Key == "loc:unmatched:Cave|Chest");
            UiTestExpect(pin.Key != null && pin.Score < 0.999, $"the pin isn't a suggestion: {string.Join(", ", rows.Select(r => r.Key))}");
            UiTestExpect(slot.ProgressionTracker != null && slot.PackIndex.ItemIdsFor("sword").Contains(1000), "the slot's Key Items don't use the automatic link");
            window.ApplyRecommendation(pin.Key);
            await UiTestWaitForAsync(() => AP_Atlas.Core.PopTracker.PackFixes.Get(key).Links.FirstOrDefault(l => l.Subject == "link:Cave|Chest" && l.ApLocationId == 2000), "the pin's link to be written");
            await UiTestWaitForAsync(() => window.StatusText.StartsWith("Applied") && window.StatusText.Contains("Done") ? window : null, "the status to say the apply landed");
            UiTestExpect(!window.RecommendedRows().Any(r => r.Key == pin.Key), "the applied row is still suggested after the check");
            // The rows are grouped by kind with what to do under each; filters narrow them; the selection is ignored in one step, and Undo brings it back.
            var far = window.RecommendedRows().FirstOrDefault(r => r.Key == "loc:unmatched:Far|Lantern");
            UiTestExpect(far.Key != null, $"the Far pin isn't a suggestion: {string.Join(", ", window.RecommendedRows().Select(r => r.Key))}");
            UiTestExpect(window.RecommendedGroupTitles().Any(t => t.Contains("pin sections aren't linked")) && (window.RecommendedToDo(far.Key) ?? "").StartsWith("Pick the check this pin stands for", StringComparison.Ordinal),
                $"the Recommended tab lacks its group or the row's what-to-do line: {string.Join(" | ", window.RecommendedGroupTitles())} / {window.RecommendedToDo(far.Key)}");
            window.SetRecommendedFilter("nothing-like-this", 0, null);
            UiTestExpect(window.RecommendedShownKeys().Count == 0, "a filter by words didn't narrow the rows");
            window.SetRecommendedFilter("", 2, null);
            UiTestExpect(!window.RecommendedShownKeys().Contains(far.Key), "the tiles-only filter still shows a pin");
            window.SetRecommendedFilter("Far", 0, "World");
            UiTestExpect(window.RecommendedShownKeys().SequenceEqual(new[] { far.Key }), $"the word and map filters didn't leave the Far pin alone: {string.Join(", ", window.RecommendedShownKeys())}");
            window.SelectShownRecommendations();
            window.IgnoreSelectedRecommendations();
            // Every bulk action previews first: the plan names the row it sets aside, and nothing changes until Apply.
            var ignorePreview = await UiTestWaitForAsync(() => AP_Atlas.UI.FixPreviewDialog.Showing, "the preview of Ignore selected");
            UiTestExpect(ignorePreview.Plan.SetAside.Count == 1 && ignorePreview.Plan.SetAside[0].Key == far.Key && ignorePreview.Plan.Link.Count == 0 && !AP_Atlas.Core.PopTracker.PackFixes.Get(key).Ignored.Contains(far.Key),
                $"the preview doesn't show the one row it sets aside, or something changed before Apply: {ignorePreview.Plan.Summary()}");
            ignorePreview.ChooseForTests(true);
            await UiTestWaitForAsync(() => window.HasReport && AP_Atlas.Core.PopTracker.PackFixes.Get(key).Ignored.Contains(far.Key) && !window.RecommendedRows().Any(r => r.Key == far.Key) ? window : null, "the Far pin to be ignored");
            UiTestExpect(window.StatusText.Contains("Ignored"), $"the status doesn't say what was ignored: {window.StatusText}");
            AP_Atlas.Core.PopTracker.PackFixes.Undo(key);
            await UiTestWaitForAsync(() => !AP_Atlas.Core.PopTracker.PackFixes.Get(key).Ignored.Contains(far.Key) ? window : null, "Undo to bring the ignored pin back");
            window.SetRecommendedFilter("", 0, null);
            // Undo twice: the pin link, then the automatic link.
            UiTestExpect(AP_Atlas.Core.PopTracker.PackFixes.Undo(key) && AP_Atlas.Core.PopTracker.PackFixes.Undo(key) && AP_Atlas.Core.PopTracker.PackFixes.Get(key).Links.Count == 0,
                "Undo didn't take the automatic link back");
            window.EmitSignal(Window.SignalName.CloseRequested);
            await UiTestWaitForAsync(() => !GodotObject.IsInstanceValid(window) || window.IsQueuedForDeletion() || !window.Visible ? this : null, "the Doctor window to close");
            // The Map Tracker's Attention group lists the pack's problems for this seed (both pins, back after the undos) and opens the Doctor on the fixing tab.
            await UiTestWaitForAsync(() => slot.MapTracker.AttentionRows().Any(r => r.Key == far.Key) && slot.MapTracker.AttentionRows().Any(r => r.Key == pin.Key) ? slot : null, "the Attention group to list both unlinked pins");
            // After the undos the Bell pin is unlinked again, so it's listed too; the broken map picture and the Shield's missing image as well.
            UiTestExpect(slot.MapTracker.AttentionRows().All(r => r.Text.Length > 0 && r.Tip.Contains("Opens the Pack Doctor"))
                && new[] { "loc:unmatched:Shrine|Bell!", "tile:noimage:shield", "map:nobg:Broken" }.All(k => slot.MapTracker.AttentionRows().Any(r => r.Key == k)),
                $"the Attention rows aren't in plain words with their way to the Doctor, or miss a problem: {string.Join(" | ", slot.MapTracker.AttentionRows().Select(r => r.Key + ": " + r.Text))}");
            UiTestExpect(slot.MapTracker.Notices().Any(n => n.Key == "map:nobg:Broken" && n.Actions.Count == 2 && n.Actions[0].Label == "Fix it here" && n.Detail.Length > 0),
                "the missing map picture's notice doesn't say what it means or offer to fix it here");
            slot.MapTracker.ShowNotices(true);
            await UiTestWaitAsync(0.1);
            UiTestExpect(slot.MapTracker.SidebarContent.FindChildren("*", nameof(PanelContainer), true, false).OfType<PanelContainer>().Any(c => c.HasMeta("attention_row") && (string)c.GetMeta("attention_row") == far.Key),
                "the Notices pane doesn't show the pack's notice as a card");
            slot.MapTracker.ShowNotices(false);
            slot.MapTracker.PressAttention(far.Key);
            var reopened = await UiTestWaitForAsync(() => GetTree().Root.GetChildren().OfType<AP_Atlas.UI.PackDoctorWindow>().FirstOrDefault(w => w.Visible), "the Doctor window from the Attention row");
            UiTestExpect(reopened.CurrentTabTitle == AP_Atlas.UI.PackDoctorWindow.GuideTab, $"the pack's notice opened the Doctor on {reopened.CurrentTabTitle}, not its guide");
            reopened.EmitSignal(Window.SignalName.CloseRequested);
            // "Fix what Atlas can" (the pack row's Fix, the Attention group's button and the card all run it): the exact pin is linked,
            // the pins without a confident match are set aside, one undoable step; a card says what was done.
            ShowGamePage("Doctor Test Game");
            await UiTestWaitForAsync(() => _packManagerPanel.FixButtonFor(key) is { Visible: true } b ? b : null, "the pack row's Fix button");
            int cardsBefore = _alertLog.Entries.Count;
            slot.MapTracker.PressFixAll();
            var fixPreview = await UiTestWaitForAsync(() => AP_Atlas.UI.FixPreviewDialog.Showing, "the preview of Fix what Atlas can");
            UiTestExpect(fixPreview.Plan.Link.Count + fixPreview.Plan.SetAside.Count >= 3 && fixPreview.Plan.Summary().Contains("Undo") && !AP_Atlas.Core.PopTracker.PackFixes.Get(key).Links.Any(),
                $"the preview of Fix what Atlas can doesn't cover the three pins, or something changed before Apply: {fixPreview.Plan.Summary()}");
            fixPreview.ChooseForTests(true);
            await UiTestWaitForAsync(() => AP_Atlas.Core.PopTracker.PackFixes.Get(key).Links.Any(l => l.Subject == "link:Shrine|Bell!" && l.ApLocationId == 2002 && l.Automatic) ? slot : null, "the Bell to be linked by Fix what Atlas can");
            // Each pin is linked (its best match scores enough) or set aside; either way nothing fixable is left, and the card says so.
            var fixedFile = AP_Atlas.Core.PopTracker.PackFixes.Get(key);
            bool Handled(string subject, string? findingKey) => fixedFile.Ignored.Contains(findingKey) || fixedFile.Links.Any(l => l.Subject == subject);
            UiTestExpect(Handled("link:Far|Lantern", far.Key) && Handled("link:Cave|Chest", pin.Key),
                $"the pins weren't linked or set aside: ignored {string.Join(", ", fixedFile.Ignored)}, links {string.Join(", ", fixedFile.Links.Select(l => l.Subject))}");
            UiTestExpect(_alertLog.Entries.Count > cardsBefore && _alertLog.Entries[0].Message.Contains("linked") && _alertLog.Entries[0].Message.Contains("Undo"),
                $"no card said what was fixed: {(_alertLog.Entries.Count > 0 ? _alertLog.Entries[0].Message : "none")}");
            await UiTestWaitForAsync(() => AP_Atlas.Core.PopTracker.PackDoctorService.Reports.TryGetValue(key, out var after) && AP_Atlas.Core.PopTracker.PackDoctor.Recommendations(after).Count == 0 ? slot : null, "nothing left to suggest after the fix");
            UiTestExpect(AP_Atlas.Core.PopTracker.PackFixes.Undo(key) && AP_Atlas.Core.PopTracker.PackFixes.Get(key).Ignored.Count == 0 && AP_Atlas.Core.PopTracker.PackFixes.Get(key).Tiles.Count == 0 && AP_Atlas.Core.PopTracker.PackFixes.Get(key).Links.Count == 0,
                "one Undo didn't reverse the whole fix");
        }
        finally
        {
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            if (key.Length > 0) AP_Atlas.Core.PopTracker.PackFixes.Reset(key);
            AP_Atlas.Core.SafeFile.Delete(zip);
        }
    }

    private async Task PacksReachConnectedSlotsAsync()
    {
        string packs = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory();
        string zip = System.IO.Path.Combine(packs, "uitest_late_pack.zip"), second = System.IO.Path.Combine(packs, "uitest_late_pack_2.zip");
        string fromGitHub = System.IO.Path.Combine(packs, "uitest_github_pack.zip"), packSource = System.IO.Path.Combine(DataManager.GetDataDirectory(), "uitest_github_pack_source.zip");
        string guideApworld = System.IO.Path.Combine(AtlasEngine.UserWorldsDir, "test_game_guide.apworld"); // its setup guide names a tracker
        string folderDocs = System.IO.Path.Combine(AtlasEngine.ArchipelagoDir, "worlds", "uitest_folder_world", "docs"); // a world shipped as a folder
        await using var github = new AP_Atlas.Core.Testing.FakeWebSite();
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
            UiTestExpect(!System.IO.Directory.GetFiles(packs, "*.zip").Any(f => AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(f)?.Manifest?.GameName == "Test Game"),
                "a pack for the test game was left installed by another scenario");
            // A check hidden before the slot connects: its map's Changes tab counts it though no pack ever loads.
            AP_Atlas.Core.Annotations.SetHidden(AP_Atlas.Core.Annotations.SlotKey(profile.Id, "Tester"), new long[] { 2000 }, true);
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            ShowTextClient(slot);
            host.ShowTool(AP_Atlas.UI.Tool.MapTracker);
            await UiTestWaitAsync(0.5);
            UiTestExpect(slot.Pack == null && slot.MapTracker.ShowingEmptyState, "a slot without a pack doesn't say so on its map");
            UiTestExpect(slot.MapTracker.ChangesTabText == "Changes (1)", $"a slot without a pack doesn't count the check it hid: \"{slot.MapTracker.ChangesTabText}\"");
            slot.SetChecksHidden(new long[] { 2000 }, false);

            // The chat's filters sit in one row (seven toggles: the kinds of line, then the kinds of item), and still filter.
            var filterRow = slot.FindChildren("*", nameof(HFlowContainer), true, false).OfType<HFlowContainer>().First(f => f.HasMeta("chat_filters"));
            var toggles = filterRow.GetChildren().OfType<Button>().ToList();
            UiTestExpect(toggles.Count == 7 && toggles.All(t => t.ToggleMode && t.ButtonPressed) && toggles.Select(t => t.Text).SequenceEqual(new[] { "Chat", "Hints", "System", "Progression", "Useful", "Filler", "Traps" }),
                $"the chat's filters aren't one row of seven toggles: {string.Join(", ", toggles.Select(t => t.Text))}");
            // The bottom pane's tabs carry a dot for lines not seen yet: a chat line while System Log shows marks Chat, showing Chat
            // clears it; a log line while Chat shows marks System Log; a line on the showing tab marks nothing.
            ShowTerminalTab(1);
            await UiTestWaitAsync(0.2);
            ClearTerminalTabNew(1);
            UiTestExpect(!TerminalTabHasNew(0) && !TerminalTabHasNew(1), "a tab starts with a dot");
            await server.BroadcastAsync(server.Chat("a line for the dot"));
            await UiTestWaitForAsync(() => TerminalTabHasNew(0) ? slot : null, "the Chat tab's dot after a chat line");
            ShowTerminalTab(0);
            UiTestExpect(!TerminalTabHasNew(0), "showing Chat didn't clear its dot");
            ClearTerminalTabNew(1);
            LogToSystem("a log line for the dot", null);
            await UiTestWaitForAsync(() => TerminalTabHasNew(1) ? slot : null, "the System Log tab's dot after a log line");
            await server.BroadcastAsync(server.Chat("another line while Chat shows"));
            await UiTestWaitAsync(0.3);
            UiTestExpect(!TerminalTabHasNew(0), "a chat line marked the showing Chat tab");
            ShowTerminalTab(1);
            UiTestExpect(!TerminalTabHasNew(1), "showing System Log didn't clear its dot");
            ShowTerminalTab(0);

            // System off hides the room's bookkeeping (joins, leaves, tag changes, "!" commands), which then lights no dot;
            // a goal still shows. The choice is remembered (and shared by every slot); System on brings the lines back.
            var filtersBefore = new HashSet<string>(_appSettings.ChatFiltersOff);
            try
            {
                var system = slot.ChatFilterButtons[AP_Atlas.Core.Connections.ChatLines.System];
                system.ButtonPressed = false;
                UiTestExpect(_appSettings.ChatFiltersOff.SetEquals(new[] { AP_Atlas.Core.Connections.ChatLines.System }), "turning System off wasn't remembered");
                ShowTerminalTab(1);
                await UiTestWaitAsync(0.2);
                ClearTerminalTabNew(0);
                await server.BroadcastAsync(server.Joined("Visitor", "Tracker"), server.Left("Visitor"),
                    server.TagsChangedLine("Visitor", new[] { "Tracker" }, new[] { "Tracker", "NoText" }), server.Chat("!alias Lamp"));
                await UiTestWaitAsync(0.4);
                UiTestExpect(!TerminalTabHasNew(0), "lines the filters hide lit the Chat tab's dot");
                await server.BroadcastAsync(server.Goal(1));
                await UiTestWaitForAsync(() => TerminalTabHasNew(0) ? slot : null, "a goal line to light the Chat tab's dot");
                ShowTerminalTab(0);
                await UiTestWaitForAsync(() => PanelShowsText(slot, "has completed their goal") ? slot : null, "the goal line with System off");
                UiTestExpect(!PanelShowsText(slot, "!alias Lamp") && !PanelShowsText(slot, "Visitor (Team #1)"), "System off still shows joins, leaves or commands");
                system.ButtonPressed = true;
                await UiTestWaitForAsync(() => PanelShowsText(slot, "!alias Lamp") && PanelShowsText(slot, "Visitor (Team #1) has left the game.") ? slot : null, "System on to show those lines again");
            }
            finally
            {
                _appSettings.ChatFiltersOff = filtersBefore;
            }

            // "Find a map pack for Test Game…" on the empty map: the window shows the game's page and searches GitHub (a fake
            // site) once for the game; the pack chosen from what it found downloads with a progress line on that page, installs
            // with a card, reaches the slot's map, and is listed in the game's Map packs section.
            FakeMapPack.Write(packSource, "UI test GitHub pack", "Test Game");
            byte[] packBytes = await System.IO.File.ReadAllBytesAsync(packSource);
            AP_Atlas.Core.SafeFile.Delete(packSource);
            github.Answer = (path, _) =>
            {
                static byte[] Json(string s) => System.Text.Encoding.UTF8.GetBytes(s);
                if (path.StartsWith("/search/repositories"))
                    return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, Json("{\"items\":[{\"full_name\":\"packs/test-game-poptracker\",\"description\":\"A PopTracker pack for Test Game\",\"html_url\":\"https://github.com/packs/test-game-poptracker\",\"stargazers_count\":3}]}"), "application/json");
                if (path.StartsWith("/repos/packs/test-game-poptracker/releases/latest"))
                    return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, Json($"{{\"tag_name\":\"v2\",\"assets\":[{{\"name\":\"uitest_github_pack_v2.zip\",\"browser_download_url\":\"{github.Site}/dl/uitest_github_pack_v2.zip\",\"size\":{packBytes.Length}}}]}}"), "application/json");
                // Two releases with a pack (and a draft): the question offers both, newest first, with the notes of the chosen one.
                if (path.StartsWith("/repos/packs/test-game-poptracker/releases?"))
                    return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, Json(
                        $"[{{\"tag_name\":\"v3\",\"name\":\"unfinished\",\"draft\":true,\"assets\":[{{\"name\":\"x.zip\",\"browser_download_url\":\"{github.Site}/dl/x.zip\",\"size\":1}}]}}," +
                        $"{{\"tag_name\":\"v2\",\"name\":\"For the next apworld\",\"body\":\"## Warning\\nFor apworld 4.x; take **v1** for archipelago.gg.\",\"assets\":[{{\"name\":\"uitest_github_pack_v2.zip\",\"browser_download_url\":\"{github.Site}/dl/uitest_github_pack_v2.zip\",\"size\":{packBytes.Length}}}]}}," +
                        $"{{\"tag_name\":\"v1\",\"name\":\"v1\",\"body\":\"\",\"assets\":[{{\"name\":\"uitest_github_pack.zip\",\"browser_download_url\":\"{github.Site}/dl/uitest_github_pack.zip\",\"size\":{packBytes.Length}}}]}}]"), "application/json");
                if (path == "/dl/uitest_github_pack.zip") return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, packBytes, "application/zip");
                return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(404, Array.Empty<byte>());
            };
            AP_Atlas.Core.GitHubApi.TestSite = github.Site;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            // The game's own setup guide (in its apworld's documents) names a tracker project the search doesn't return: it
            // comes first, marked as recommended, and the game's page offers it.
            // A world that ships with Archipelago keeps its documents as plain files (worlds/<world>/docs/), found by its game page's name.
            System.IO.Directory.CreateDirectory(folderDocs);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(folderDocs, "en_Test Game.md"), "# Test Game\n");
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(folderDocs, "setup_en.md"), "# Setup\n\nUse the [map tracker](https://github.com/folder/test-game-tracker).\n");
            UiTestExpect(AP_Atlas.Core.MapPackManagerControl.GuideRecommendations("Test Game").Select(r => r.Repo).SequenceEqual(new[] { "folder/test-game-tracker" }),
                $"a shipped world's guide isn't read from its folder: {string.Join(", ", AP_Atlas.Core.MapPackManagerControl.GuideRecommendations("Test Game").Select(r => r.Repo))}");
            System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(folderDocs)!, true);
            System.IO.Directory.CreateDirectory(AtlasEngine.UserWorldsDir);
            static void WriteGuideApworld(string path)
            {
                using var stream = System.IO.File.Create(path);
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
                foreach (var (name, text) in new[] { ("test_game/archipelago.json", "{\"game\": \"Test Game\"}"), ("test_game/docs/setup_en.md", "# Setup\n\n- [Map tracker](https://github.com/fork/test-game-poptracker)\n") })
                {
                    using var writer = new System.IO.StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(text);
                }
            }
            WriteGuideApworld(guideApworld);
            UiTestExpect(AP_Atlas.Core.MapPackManagerControl.GuideRecommendations("Test Game").Select(r => r.Repo).SequenceEqual(new[] { "fork/test-game-poptracker" }), "the guide's tracker project isn't read from the apworld");
            host.ShowTool(AP_Atlas.UI.Tool.Games);
            _gamesPage.Select("Test Game");
            await UiTestWaitForAsync(() => _gamesPage.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.HasMeta("recommended_pack")), "the game's page to offer the recommended pack");
            host.ShowTool(AP_Atlas.UI.Tool.MapTracker);
            await UiTestWaitAsync(0.2);
            UiTestExpect(slot.MapTracker.FindPackButton.Text == "Find a map pack for Test Game…", $"the empty map's button says \"{slot.MapTracker.FindPackButton.Text}\"");
            slot.MapTracker.FindPackButton.EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitForAsync(() => ShownContent() == _gamesPage && _gamesPage.SelectedGame == "Test Game" ? _gamesPage : null, "the game's page to show");
            UiTestExpect(_gamesPage.IsAncestorOf(_packManagerPanel.ProgressRow), "a download's progress line isn't on the Games page");
            var results = await UiTestWaitForAsync(() => _packManagerPanel.GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => d.Title == "Map packs on GitHub"), "the search results", 20);
            UiTestExpect(github.Requests.Count(r => r.StartsWith("/search/")) == 1 && github.Requests.First(r => r.StartsWith("/search/")).Contains("fork%3Atrue"),
                $"GitHub was searched {github.Requests.Count(r => r.StartsWith("/search/"))} times for one game, or without its forks: {github.Requests.FirstOrDefault(r => r.StartsWith("/search/"))}");
            var installButtons = results.FindChildren("*", nameof(Button), true, false).OfType<Button>().Where(b => b.HasMeta("candidate_repo")).ToList();
            UiTestExpect(installButtons.Select(b => b.GetMeta("candidate_repo").AsString()).SequenceEqual(new[] { "fork/test-game-poptracker", "packs/test-game-poptracker" })
                && results.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(l => l.HasMeta("recommended_pack") && l.Text == "Guide's pick"),
                $"the guide's project isn't first and marked: {string.Join(", ", installButtons.Select(b => b.GetMeta("candidate_repo").AsString()))}");
            installButtons[1].EmitSignal(BaseButton.SignalName.Pressed);
            var question = await UiTestWaitForAsync(() => _packManagerPanel.GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == "Install a map pack"), "the question before the download", 20);
            UiTestExpect(!System.IO.File.Exists(fromGitHub) && !github.Requests.Any(r => r.StartsWith("/dl/")), "the pack was downloaded before the question was answered");
            // The project has two releases with a pack: both offered, newest first, the draft left out, the chosen one's notes shown.
            var releasePicker = question.FindChildren("Releases", nameof(OptionButton), true, false).OfType<OptionButton>().FirstOrDefault();
            UiTestExpect(question.HasMeta("release_picker") && releasePicker != null && releasePicker.ItemCount == 2 && releasePicker.GetItemText(0).StartsWith("v2 · For the next apworld") && releasePicker.GetItemText(1) == "v1",
                $"the question doesn't offer the project's releases: {(releasePicker == null ? "no picker" : string.Join(" | ", Enumerable.Range(0, releasePicker.ItemCount).Select(releasePicker.GetItemText)))}");
            var releaseNotes = question.FindChildren("*", nameof(Label), true, false).OfType<Label>().FirstOrDefault(l => l.Text.Contains("For apworld 4.x"));
            UiTestExpect(releaseNotes != null && !releaseNotes.Text.Contains("##") && !releaseNotes.Text.Contains("**"), "the newest release's notes aren't shown as plain text");
            releasePicker?.Select(1);
            releasePicker?.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
            UiTestExpect(question.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(l => l.Text.Contains("uitest_github_pack.zip (")), "the chosen release's file isn't named");
            question.EmitSignal(AcceptDialog.SignalName.Confirmed);
            var fromSearch = await UiTestWaitForAsync(() => slot.Pack, "the slot to load the pack found on GitHub", 20);
            UiTestExpect(fromSearch.Manifest.Name == "UI test GitHub pack" && System.IO.File.Exists(fromGitHub), "the pack from GitHub wasn't installed for the slot");
            UiTestExpect(!IsInstanceValid(results) || results.IsQueuedForDeletion() || !results.Visible, "the results dialog stayed open after the pack installed");
            UiTestExpect(_packManagerPanel.DownloadText.StartsWith("Downloading uitest_github_pack.zip") && _packManagerPanel.DownloadText.Contains(" of ") && !_packManagerPanel.Downloading,
                $"no progress line was shown for the download: \"{_packManagerPanel.DownloadText}\"");
            UiTestExpect(_alertLog.Entries.Any(e => e.Message.Contains("Installed UI test GitHub pack") && e.Message.Contains("Test Game")), "no card said the pack was installed");
            await UiTestWaitForAsync(() => _gamesPage.FindChildren("*", "", true, false).FirstOrDefault(n => IsInstanceValid(n) && !n.IsQueuedForDeletion() && n.HasMeta("pack_row") && n.GetMeta("pack_row").AsString() == fromGitHub),
                "the installed pack's row in the game's Map packs section");
            // Gone again, for the rest of the scenario.
            AP_Atlas.Core.SafeFile.Delete(fromGitHub);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
            await UiTestWaitForAsync(() => slot.Pack == null && slot.MapTracker.ShowingEmptyState ? slot : null, "the map to say the pack from GitHub is gone");
            // The solo test's way: no question, the newest release that fits (v2's file can't be downloaded, so v1 is tried and kept).
            var said = new List<string>();
            var fitting = await _packManagerPanel.InstallFittingReleaseAsync("Test Game", "packs/test-game-poptracker", said.Add);
            UiTestExpect(fitting.Ok && System.IO.File.Exists(fromGitHub) && fitting.File == fromGitHub && fitting.Detail.StartsWith("took v1:") && said.Any(s => s.StartsWith("trying github.com/packs/test-game-poptracker v2")),
                $"the fitting release wasn't installed without a question: {fitting.Ok}, \"{fitting.Detail}\", said {string.Join(" | ", said)}");
            // The releases tried were downloaded beside the packs, never among them, and are gone once one is kept.
            string trying = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fromGitHub)!, AP_Atlas.Core.MapPackManagerControl.TryingFolder);
            UiTestExpect(!System.IO.Directory.Exists(trying) || System.IO.Directory.GetFileSystemEntries(trying).Length == 0, "the releases tried for fit were left behind");
            AP_Atlas.Core.SafeFile.Delete(fromGitHub);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
            await UiTestWaitForAsync(() => slot.Pack == null && slot.MapTracker.ShowingEmptyState ? slot : null, "the map to say the fitting pack is gone");
            // Atlas picks the game's pack by itself: of the projects GitHub finds, one with more stars whose pack is for
            // other names is passed over for the one whose release fits (none installed until then, nothing left behind).
            FakeMapPack.Write(packSource, "UI test wrong pack", "Test Game",
                locationsJson: """[{"name":"Nowhere","sections":[{"name":"Thing 1"},{"name":"Thing 2"}],"map_locations":[{"map":"World","x":10,"y":10}]}]""");
            byte[] wrongBytes = await System.IO.File.ReadAllBytesAsync(packSource);
            AP_Atlas.Core.SafeFile.Delete(packSource);
            var answered = github.Answer;
            github.Answer = (path, headers) =>
            {
                static byte[] Json(string s) => System.Text.Encoding.UTF8.GetBytes(s);
                if (path.StartsWith("/search/repositories"))
                    return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, Json("{\"items\":[{\"full_name\":\"packs/test-game-poptracker\",\"stargazers_count\":3},{\"full_name\":\"big/wrong-pack\",\"stargazers_count\":40}]}"), "application/json");
                if (path.StartsWith("/repos/big/wrong-pack/releases?"))
                    return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, Json($"[{{\"tag_name\":\"v9\",\"assets\":[{{\"name\":\"wrong.zip\",\"browser_download_url\":\"{github.Site}/dl/wrong.zip\",\"size\":{wrongBytes.Length}}}]}}]"), "application/json");
                if (path == "/dl/wrong.zip") return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, wrongBytes, "application/zip");
                return answered!(path, headers);
            };
            said.Clear();
            var picked = await _packManagerPanel.InstallBestPackAsync("Test Game", said.Add);
            github.Answer = answered;
            UiTestExpect(picked.Ok && picked.Repo == "packs/test-game-poptracker" && picked.File == fromGitHub && System.IO.File.Exists(fromGitHub)
                && picked.Detail.Contains("big/wrong-pack v9: 0% of its pins link") && said.Any(l => l.StartsWith("trying github.com/big/wrong-pack v9")),
                $"Atlas didn't pick the pack that fits: {picked.Ok}, {picked.Repo}, \"{picked.Detail}\", said {string.Join(" | ", said)}");
            UiTestExpect(!System.IO.Directory.Exists(trying) || System.IO.Directory.GetFileSystemEntries(trying).Length == 0, "the packs tried for the pick were left behind");
            AP_Atlas.Core.SafeFile.Delete(fromGitHub);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
            await UiTestWaitForAsync(() => slot.Pack == null && slot.MapTracker.ShowingEmptyState ? slot : null, "the map to say the picked pack is gone");
            host.ShowTool(AP_Atlas.UI.Tool.MapTracker);
            await UiTestWaitAsync(0.2);

            // Installed while connected: the map shows it at once.
            FakeMapPack.Write(zip, "UI test late pack", "Test Game");
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
            var pack = await UiTestWaitForAsync(() => slot.Pack, "the slot to load the pack installed while it was connected");
            await UiTestWaitForAsync(() => slot.MapTracker.Pack != null && !slot.MapTracker.ShowingEmptyState ? slot.MapTracker : null, "the map to show the new pack");
            UiTestExpect(pack.Manifest.Name == "UI test late pack" && slot.Session?.Socket.Connected == true, "the new pack isn't the one installed, or the slot reconnected");
            // An unrelated change leaves the slot's pack alone.
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
            await UiTestWaitAsync(0.3);
            UiTestExpect(ReferenceEquals(slot.Pack, pack), "a change that didn't touch the slot's pack reloaded it");
            // Deleted: the map says there's no pack again, and its images are let go.
            AP_Atlas.Core.SafeFile.Delete(zip);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
            await UiTestWaitForAsync(() => slot.Pack == null && slot.MapTracker.ShowingEmptyState ? slot : null, "the map to say the pack is gone");
            UiTestExpect(AP_Atlas.Core.PopTracker.PackImages.UsersOf(pack) == 0, "the deleted pack's images are still used");
            // Another pack for the game takes its place. It has two variants: the map offers them, the pack's default in use;
            // picking the other reads the pack again as that variant (its own items) and is remembered for the slot.
            FakeMapPack.Write(second, "UI test second pack", "Test Game", versionsUrl: "https://raw.githubusercontent.com/packs/test-game-poptracker/main/versions.json",
                itemsJson: """[{"name":"Sword","type":"toggle","img":"images/sword.png","codes":"sword"},{"name":"Shield","type":"toggle","img":"images/broken.png","codes":"shield"},""" +
                    """{"name":"Progressive Bow","type":"progressive","stages":[{"name":"Bow","img":"images/sword.png","codes":"bow"},{"name":"Silver Bow","img":"images/sword.png","codes":"silver_bow"}]}]""",
                files: new Dictionary<string, string> { ["var_b/items/items.json"] = """[{"name":"Lantern","type":"toggle","img":"images/sword.png","codes":"lantern"}]""" },
                variantsJson: """{"standard":{"display_name":"Standard"},"var_b":{"display_name":"Variant B"}}""",
                layoutsJson: """{"tracker_default":{"type":"itemgrid","rows":[["sword","shield","bow"]]},"tracker_horizontal":{"type":"group","header":"Gear","content":{"type":"array","orientation":"horizontal","content":[{"type":"itemgrid","rows":[["sword"]]},{"type":"layout","key":"shield_grid"},{"type":"map","maps":["World"]}]}},"shield_grid":{"type":"itemgrid","rows":[["shield"]]}}""");
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
            var replacement = await UiTestWaitForAsync(() => slot.Pack, "the second pack");
            UiTestExpect(replacement.Manifest.Name == "UI test second pack" && !slot.MapTracker.ShowingEmptyState, "the second pack didn't take the first one's place");
            UiTestExpect(replacement.Variant == "standard" && slot.ProgressionTracker.VariantOptions.SequenceEqual(new[] { "standard", "var_b" }) && slot.ProgressionTracker.VariantPicker.Selected == 0,
                $"Key Items doesn't offer the pack's variants with the default chosen: {string.Join(", ", slot.ProgressionTracker.VariantOptions)}");
            UiTestExpect(!slot.MapTracker.VariantRowVisible, "the map offers a variant choice though no variant changes its maps");
            slot.ProgressionTracker.VariantPicker.Selected = 1;
            slot.ProgressionTracker.VariantPicker.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
            var asVariant = await UiTestWaitForAsync(() => slot.Pack is { Variant: "var_b" } p ? p : null, "the slot to read the pack again as the chosen variant");
            string slotKey = AP_Atlas.Core.Annotations.SlotKey(profile.Id, "Tester");
            UiTestExpect(asVariant.ItemsByCode.ContainsKey("lantern") && !asVariant.ItemsByCode.ContainsKey("sword") && _appSettings.PackVariants.TryGetValue(slotKey, out var kept) && kept == "var_b",
                "the variant's own items weren't read, or the choice wasn't kept for the slot");
            _appSettings.PackVariants.Remove(slotKey);
            // Back to the standard variant (its items are the ones the layouts name).
            slot.ProgressionTracker.VariantPicker.Selected = 0;
            slot.ProgressionTracker.VariantPicker.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
            await UiTestWaitForAsync(() => slot.Pack is { Variant: "standard" } p ? p : null, "the slot to read the pack again as the standard variant");
            _appSettings.PackVariants.Remove(slotKey);
            // Key Items offers the pack's two layouts and Atlas's two built ones; a pack layout draws its own rows, a built one its
            // blocks side by side; the choice is kept for the slot.
            host.ShowTool(AP_Atlas.UI.Tool.KeyItems);
            await UiTestWaitAsync(0.2);
            var keyItems = slot.ProgressionTracker;
            UiTestExpect(keyItems.LayoutChoices().SequenceEqual(new[] { "tracker_default", "tracker_horizontal", AP_Atlas.Core.PopTracker.KeyItemsLayouts.Vertical, AP_Atlas.Core.PopTracker.KeyItemsLayouts.Horizontal }),
                $"Key Items offers {string.Join(", ", keyItems.LayoutChoices())}");
            // Atlas's pick (every layout fits this small pack: the one filling the view best) is named first in the picker and
            // drawn until the user chooses.
            await UiTestWaitForAsync(() => keyItems.AutoLayout != null ? keyItems : null, "Atlas to settle on the layout that fits");
            UiTestExpect(keyItems.LayoutPicker.GetItemText(0).StartsWith("Auto (", StringComparison.Ordinal) && keyItems.LayoutPicker.Selected == 0 && keyItems.CurrentLayout == keyItems.AutoLayout,
                $"the picker doesn't name Atlas's pick first, or it isn't drawn: {keyItems.LayoutPicker.GetItemText(0)}, {keyItems.CurrentLayout} in use, {keyItems.AutoLayout} picked");
            keyItems.SetLayout("tracker_default");
            UiTestExpect(keyItems.VisualShape().Rows == 1 && keyItems.VisualShape().Built == null, $"the pack's default layout isn't drawn as its one row: {keyItems.VisualShape()}");
            // Nothing received: the progressive item (its pictures on its stages) shows its first stage's picture, dimmed, as
            // PopTracker does; the shield's picture can't be read, so its name stands on a plain tile.
            var tiles = keyItems.TileKinds();
            UiTestExpect(tiles.Any(t => t.Code == "bow" && t.Picture) && tiles.Any(t => t.Code == "sword" && t.Picture) && tiles.Any(t => t.Code == "shield" && !t.Picture),
                $"the tiles don't show what PopTracker shows: {string.Join(", ", tiles.Select(t => t.Code + (t.Picture ? " picture" : " name")))}");
            keyItems.SetLayout("tracker_horizontal");
            UiTestExpect(keyItems.VisualShape().Rows == 2 && keyItems.VisualShape().Built == null, $"the pack's horizontal layout isn't drawn as its two rows: {keyItems.VisualShape()}");
            // Drawn as the pack lays it out: the group under its header, its two grids side by side (the referenced layout in its place), the map left out.
            var nodes = keyItems.LayoutNodes();
            UiTestExpect(nodes.Any(n => n.Kind == "group" && n.Parts == 2) && nodes.Any(n => n.Kind == "array:horizontal" && n.Parts == 2),
                $"the pack's layout isn't drawn as it lays it out: {string.Join(", ", nodes.Select(n => n.Kind + "(" + n.Parts + ")"))}");
            keyItems.SetLayout(AP_Atlas.Core.PopTracker.KeyItemsLayouts.Horizontal);
            UiTestExpect(keyItems.VisualShape().Built == AP_Atlas.Core.PopTracker.KeyItemsLayouts.Horizontal && _appSettings.KeyItemsLayout.TryGetValue(slotKey, out var layoutKept) && layoutKept == AP_Atlas.Core.PopTracker.KeyItemsLayouts.Horizontal,
                "the built horizontal layout isn't drawn as blocks side by side, or wasn't kept for the slot");
            _appSettings.KeyItemsLayout.Remove(slotKey);
            host.ShowTool(AP_Atlas.UI.Tool.MapTracker);
            // Half of this pack's pins link to the seed's one check: the map says so, and its button tries the pack's project's
            // releases against the seed (v2's file can't be downloaded, v1 is kept as the best) and makes the one kept the
            // game's pack, which the slot switches to.
            await UiTestWaitForAsync(() => slot.MapTracker.PackFitNote is { } fitNote && fitNote.StartsWith("Only 50% of this map pack's pins link to this seed's checks", StringComparison.Ordinal) ? slot : null,
                "the map to say its pack doesn't fit the seed");
            AP_Atlas.Core.SafeFile.Delete(fromGitHub);
            slot.MapTracker.PressFindFitting();
            var fitted = await UiTestWaitForAsync(() => slot.Pack is { Manifest.Name: "UI test GitHub pack" } p ? p : null, "the slot to switch to the pack release found for the seed", 20);
            UiTestExpect(_appSettings.PreferredPacks.TryGetValue("Test Game", out var preferredPack) && preferredPack == System.IO.Path.GetFileName(fromGitHub)
                && _alertLog.Entries.Any(e => e.Message.StartsWith("Test Game's map pack: took v1", StringComparison.Ordinal)),
                $"the release found wasn't made the game's pack, or no card said so: {(_alertLog.Entries.Count > 0 ? _alertLog.Entries[0].Message : "none")}");
        }
        finally
        {
            AP_Atlas.Core.GitHubApi.TestSite = null;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            _appSettings.PreferredPacks.Remove("Test Game");
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            foreach (string file in new[] { zip, second, fromGitHub, packSource, guideApworld }) AP_Atlas.Core.SafeFile.Delete(file);
            if (System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(folderDocs)!)) System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(folderDocs)!, true);
        }
    }

    private async Task LiveMapFollowingAsync()
    {
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_follow_pack.zip");
        FakeMapPack.Write(zip, "UI test following pack", "Test Game", initLua: FakeMapPack.FollowingInitLua, layoutsJson: FakeMapPack.TwoTabLayout);
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        server.DataStorage["atlas_test_map"] = "Elsewhere"; // where the game's client says the player is
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        string key = AP_Atlas.Core.Annotations.SlotKey(profile.Id, "Tester");
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            ShowTextClient(slot);
            host.ShowTool(AP_Atlas.UI.Tool.MapTracker);
            var map = slot.MapTracker;
            // At connect: the key is read, and the map shows the tab it names.
            await UiTestWaitForAsync(() => map.CurrentMapId == "Broken" ? map : null, "the map to follow the game to the tab the room names");
            UiTestExpect(map.FollowAvailable && map.FollowGame && map.FollowBox.Visible, "the follow switch isn't offered, or isn't on");
            UiTestExpect(server.Received.Any(p => (string?)p["cmd"] == "SetNotify" && p["keys"]?.ToObject<string[]>()?.Contains("atlas_test_map") == true),
                "the server wasn't asked to tell Atlas about the key");
            // A change: the map follows.
            await server.SetAsync("atlas_test_map", "Overworld");
            await UiTestWaitForAsync(() => map.CurrentMapId == "World" ? map : null, "the map to follow the game back");
            // Off: the map stays where the user put it, and the choice is remembered for the slot.
            map.FollowBox.ButtonPressed = false;
            UiTestExpect(_appSettings.MapFollowGame.TryGetValue(key, out bool following) && !following, "turning following off wasn't remembered");
            await server.SetAsync("atlas_test_map", "Elsewhere");
            await UiTestWaitAsync(1.0);
            UiTestExpect(map.CurrentMapId == "World", $"the map followed the game with the switch off (it shows {map.CurrentMapId})");
            UiTestExpect(server.Count("Set") == 0, "Atlas wrote the room's data storage for the pack");
        }
        finally
        {
            _appSettings.MapFollowGame.Remove(key);
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (_profiles.Contains(profile)) DeleteProfile(profile);
            AP_Atlas.Core.SafeFile.Delete(zip);
        }
    }

    private async Task ConnectedSlotLetsAtlasIdleAsync()
    {
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_idle_pack.zip");
        // Bigger than the view, so there's room to drag; a third pin sets its own size and shape, as PopTracker packs may.
        FakeMapPack.Write(zip, "UI test pack", "Test Game", mapWidth: 1600, mapHeight: 1000, locationsJson:
            """[{"name":"Cave","sections":[{"name":"Chest"}],"map_locations":[{"map":"World","x":10,"y":10}]},""" +
            """{"name":"Far","sections":[{"name":"Chest"}],"map_locations":[{"map":"World","x":500,"y":10}]},""" +
            """{"name":"Big","sections":[{"name":"Chest"}],"map_locations":[{"map":"World","x":300,"y":300,"size":40,"shape":"diamond","border_thickness":4}]}]""");
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
            // A fit made while the view was still a token size (a hidden view holds its scrollbars' own) is no fit, and
            // Atlas's own fit is made again as the view grows, until the user moves the map: Dark Souls III's map opened
            // at 5% in the owner's beta (2026-10-11), fitted to the 8 px it had while hidden.
            var holder = new Control { Size = new Vector2(8, 8) };
            GetTree().Root.AddChild(holder);
            var probe = new AP_Atlas.UI.MapCanvas();
            holder.AddChild(probe);
            probe.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            try
            {
                probe.SetMap(null, new Vector2(1600, 800));
                probe.FitToView(remember: false);
                await UiTestWaitAsync(0.1);
                UiTestExpect(!probe.HasView && probe.AutoView, $"an 8 px view counts as a view ({probe.ViewSize}), or the fit isn't kept for later");
                holder.Size = new Vector2(800, 400);
                await UiTestWaitAsync(0.1);
                UiTestExpect(Mathf.IsEqualApprox(probe.Zoom, 0.475f, 0.01f), $"the map wasn't fitted once the view grew: zoom {probe.Zoom} in {probe.ViewSize}");
                holder.Size = new Vector2(1600, 800);
                await UiTestWaitAsync(0.1);
                UiTestExpect(Mathf.IsEqualApprox(probe.Zoom, 0.95f, 0.01f), $"the map wasn't fitted again as the view grew: zoom {probe.Zoom} in {probe.ViewSize}");
                probe.ZoomAtCenter(1 / AP_Atlas.UI.MapCanvas.WheelStep);
                await UiTestWaitAsync(0.1);
                float userZoom = probe.Zoom;
                holder.Size = new Vector2(400, 200);
                await UiTestWaitAsync(0.1);
                UiTestExpect(!probe.AutoView && Mathf.IsEqualApprox(probe.Zoom, userZoom), $"the user's zoom {userZoom} was changed by a resize: {probe.Zoom}");
            }
            finally
            {
                holder.QueueFree();
            }
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
            // A pin that sets its own size and shape keeps them.
            var big = canvas.Pins.First(p => p.Key.StartsWith("Big@", StringComparison.Ordinal));
            UiTestExpect(Mathf.IsEqualApprox(big.MapSize, (40f + 2 * 4f) * _appSettings.MapNodeScale) && Mathf.IsEqualApprox(big.MapBorder, 4f * _appSettings.MapNodeScale) && Mathf.IsEqualApprox(big.Control.Rotation, Mathf.Pi / 4)
                && big.Control.GetThemeStylebox("normal") is StyleBoxFlat bigStyle && bigStyle.BorderWidthTop == Math.Max(1, (int)Math.Round(big.MapBorder * (canvas.ScreenSizeOf(big) / big.MapSize))),
                $"the pack's own pin size, shape and border weren't kept (size {big.MapSize}, turned {big.Control.Rotation})");
            // Zoomed far out and back: every pin is as big as the zoom says and sits on its point (a shrunk pin used to keep its old size).
            canvas.SetZoom(AP_Atlas.UI.MapCanvas.MinZoom);
            canvas.SetZoom(canvas.Zoom * 3);
            var misplaced = canvas.Pins.Where(p => !Mathf.IsEqualApprox(p.Control.Size.X, canvas.ScreenSizeOf(p), 0.01f)
                || p.Control.Position.DistanceTo(new Vector2(p.X * canvas.Zoom, p.Y * canvas.Zoom) - p.Control.Size / 2) > 0.5f).Select(p => $"{p.Key} {p.Control.Size.X} for {canvas.ScreenSizeOf(p)}").ToList();
            UiTestExpect(misplaced.Count == 0, $"after zooming out, pins aren't the size the zoom says or off their point: {string.Join(", ", misplaced)}");
            // The border shrank with the pin and never swallows its colour.
            var swallowed = canvas.Pins.Where(p => p.MapBorder > 0 && p.Control.GetThemeStylebox("normal") is StyleBoxFlat s
                && (s.BorderWidthTop > Math.Max(1, (int)(canvas.ScreenSizeOf(p) / 2) - 1) || s.BorderWidthTop != Math.Clamp((int)Math.Round(p.MapBorder * (canvas.ScreenSizeOf(p) / p.MapSize)), 1, Math.Max(1, (int)(canvas.ScreenSizeOf(p) / 2) - 1)))).Select(p => p.Key).ToList();
            UiTestExpect(swallowed.Count == 0, $"after zooming out, a pin's border didn't scale with it: {string.Join(", ", swallowed)}");
            UiTestExpect(!slot.MapTracker.NodeSizeSlider.Scrollable, "the pin size slider moves with the wheel (scrolling the explorer over it changed every pin)");
            canvas.FitToView();
            for (int i = 0; i < notches; i++) canvas.ZoomAtCenter(AP_Atlas.UI.MapCanvas.WheelStep);
            await UiTestWaitAsync(0.1);
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
            // The map shows no logic here (no engine runs in this test), and says race mode hides it exactly when the slot does.
            UiTestExpect(slot.MapTracker.LogicHidden && (slot.MapTracker.Logic == AP_Atlas.UI.MapTrackerControl.LogicShown.Hidden) == slot.LogicHidden,
                $"the map's logic is {slot.MapTracker.Logic} while the slot hides logic: {slot.LogicHidden}");
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
    private static FakeLogicEngine StartFakeEngine(string python, Action<FakeLogicEngine>? setup = null)
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
        setup?.Invoke(engine);
        engine.Apply();
        AtlasEngine.TestPython = python;
        AtlasEngine.InstallBridge(EngineInstall.Portable());
        return engine;
    }

    /// <summary>A fake server whose room has the fake engine's world (one slot, "Tester"), under the given data checksum.</summary>
    private static FakeArchipelagoServer LogicWorldServer(string checksum = LogicWorldChecksum)
    {
        var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame(checksum,
            new Dictionary<string, long> { ["Sword"] = 1000, ["Shield"] = 1001, ["Rupee"] = 1002, ["Gem"] = 1099 },
            new Dictionary<string, long> { ["Cave Chest"] = 2000, ["Locked Door"] = 2001, ["Tower Top"] = 2002, ["Gem Vault"] = 2003 });
        return server;
    }

    // The installed apworld's data in the seed-version scenarios: another version than the seeds'.
    private const string InstalledVersionChecksum = "0afe0afe0afe0afe0afe0afe0afe0afe0afe0afe";

    /// <summary>A stand-in apworld for the fake engine (fake_world.json): its data checksum and version, and whether its rebuilt world matches the server's.</summary>
    private static byte[] StandInApworld(string version, string checksum, bool match)
    {
        using var memory = new System.IO.MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Text(string name, string text)
            {
                using var writer = new System.IO.StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
            Text("test_game/archipelago.json", "{\"game\": \"Test Game\", \"world_version\": \"" + version + "\"}");
            Text("test_game/fake_world.json", new JObject { ["game"] = "Test Game", ["data_checksum"] = checksum, ["world_version"] = version, ["match"] = match }.ToString());
        }
        return memory.ToArray();
    }

    /// <summary>
    /// A seed-version scenario: the fake engine (its installed world another version than the seed's), the slot "Quill"
    /// connected to a room of the logic world under <paramref name="seed"/> (each scenario its own, so no version Atlas
    /// identified or tried in another counts), and Atlas's game list knowing Test Game's releases at a fake site from
    /// owner/seed-versions, a project the user trusts. Another project the user added but doesn't trust lists the seed's
    /// version too, read earlier this session: it must never be downloaded. Every download waits for the gate.
    /// </summary>
    private async Task WithSeedVersionsAsync(string seed, Action<FakeLogicEngine> setup, (string Version, string Checksum, bool Match)[] releases,
        Func<SlotTrackerControl, FakeLogicEngine, AP_Atlas.Core.Testing.FakeWebSite, System.Threading.ManualResetEventSlim, Task> body)
    {
        const string Trusted = "owner/seed-versions", Stranger = "stranger/seed-versions";
        var engine = StartFakeEngine(UiTestPython(), fake =>
        {
            fake.DataChecksum = InstalledVersionChecksum;
            setup(fake);
        });
        var bytes = releases.ToDictionary(r => r.Version, r => StandInApworld(r.Version, r.Checksum, r.Match));
        byte[] strangers = StandInApworld("9.9.9", seed, true);
        static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();
        using var gate = new System.Threading.ManualResetEventSlim(false);
        await using var site = new AP_Atlas.Core.Testing.FakeWebSite();
        site.Answer = (path, _) =>
        {
            if (path.StartsWith($"/repos/{Stranger}/releases", StringComparison.Ordinal))
                return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, System.Text.Encoding.UTF8.GetBytes(
                    $"[{{\"tag_name\":\"9.9.9\",\"draft\":false,\"prerelease\":false,\"assets\":[{{\"name\":\"test_game.apworld\",\"browser_download_url\":\"http://localhost:{new Uri(site.Site).Port}/dl/stranger/test_game.apworld\",\"digest\":\"sha256:{Sha(strangers)}\",\"size\":{strangers.Length}}}]}}]"),
                    "application/json");
            if (path.StartsWith("/dl/", StringComparison.Ordinal))
            {
                gate.Wait(TimeSpan.FromSeconds(30)); // the scenario sees Atlas trying first
                if (path == "/dl/stranger/test_game.apworld") return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, strangers, "application/octet-stream");
                foreach (var (version, file) in bytes)
                    if (path == $"/dl/{version}/test_game.apworld") return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, file, "application/octet-stream");
            }
            return new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(404, Array.Empty<byte>());
        };
        var list = ApworldSources.List;
        var entry = new ApworldSource
        {
            Game = "Test Game",
            Apworld = "test_game",
            Repo = Trusted,
            Versions = releases.Select(r => new ApworldVersion { Version = r.Version, Url = $"{site.Site}/dl/{r.Version}/test_game.apworld", Sha256 = Sha(bytes[r.Version]) }).ToList()
        };
        await using var server = LogicWorldServer(seed);
        server.SlotName = "Quill";
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Quill");
        _profiles.Add(profile);
        bool autoBefore = _appSettings.AutoFixApworldVersions;
        string kept = System.IO.Path.Combine(AP_Atlas.Core.Games.GameFiles.ApworldsFolder(DataManager.GetDataDirectory(), "Test Game"), "127.0.0.1");
        try
        {
            // GitHub may not be asked: the picker shows only what Atlas identified, and the tries never ask it.
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null, false);
            ApworldSources.ForgetSeedTriesForTests();
            ApworldSources.UseListForTests(new ApworldSourceList
            {
                Format = list.Format,
                Built = list.Built,
                Source = list.Source,
                Games = list.Games.Where(g => !string.Equals(g.Game, "Test Game", StringComparison.OrdinalIgnoreCase)).Append(entry).ToList()
            });
            _appSettings.AutoFixApworldVersions = true;
            // Trust is where the downloads come from: the fake site (the stranger's link is on another host, "localhost").
            ApworldSources.Approve(_appSettings, site.Site + "/");
            _appSettings.ExtraApworldRepos["Test Game"] = new List<string> { Stranger };
            // The stranger's release list, read earlier this session (as the Games page reads it): known, never tried.
            AP_Atlas.Core.GitHubApi.TestSite = site.Site;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            var listed = await ApworldSources.VersionsAsync("Test Game", new[] { Stranger }, "test_game", System.Threading.CancellationToken.None);
            UiTestExpect(listed.Count == 1, $"the untrusted project's release list wasn't read: {listed.Count} versions");
            int before = site.Requests.Count;
            await OnConnectSlotPressedAsync("Quill", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Quill"), "the slot's view");
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.LogicTracker);
            await body(slot, engine, site, gate);
            var since = site.Requests.Skip(before).ToList();
            UiTestExpect(since.All(r => r.StartsWith("/dl/", StringComparison.Ordinal)), $"Atlas asked GitHub while looking for the seed's version: {string.Join(", ", since)}");
            UiTestExpect(!since.Contains("/dl/stranger/test_game.apworld"), "a release from a project the user doesn't trust was downloaded");
        }
        finally
        {
            gate.Set();
            foreach (var picker in GetTree().Root.GetChildren().OfType<AP_Atlas.UI.ApworldPickerDialog>()) picker.QueueFree();
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
            _appSettings.AutoFixApworldVersions = autoBefore;
            _appSettings.SlotApworlds.Remove(AP_Atlas.Core.Annotations.SlotKey(profile.Id, "Quill"));
            _appSettings.ApprovedApworldSources.Remove("127.0.0.1");
            AP_Atlas.Core.Permissions.ResetSessionForTests();
            _appSettings.ExtraApworldRepos.Remove("Test Game");
            ApworldSources.UseListForTests(null);
            AP_Atlas.Core.GitHubApi.TestSite = null;
            AP_Atlas.Core.GitHubApi.ResetForTests();
            if (System.IO.Directory.Exists(kept)) System.IO.Directory.Delete(kept, true);
        }
    }

    /// <summary>(a) Another version, but the rebuilt world matches the server's: logic runs on the installed version, nothing warns, nothing is looked for.</summary>
    private Task SeedVersionDiffersButLogicMatchesAsync() =>
        WithSeedVersionsAsync("5eeda5eeda5eeda5eeda5eeda5eeda5eeda5eeda", _ => { },
            new[] { ("4.2.0", "5eeda5eeda5eeda5eeda5eeda5eeda5eeda5eeda", true), ("4.1.0", "data-4.1.0", true) },
            async (slot, engine, site, gate) =>
            {
                gate.Set(); // a download would go through at once: none may happen
                await UiTestWaitForAsync(() => slot.LogicSettled ? slot : null, "the slot's logic to start");
                await UiTestWaitAsync(1.0); // time for a search to start, if one wrongly did
                UiTestExpect(slot.ApworldMatchesSeed == false && slot.EngineYamlInfo?["match"]?.Value<bool>() == true, "the engine didn't report another version with a matching world");
                UiTestExpect(!slot.SeedVersionWanted && slot.LogicAccuracy == AP_Atlas.Core.Games.LogicAccuracy.AsChecked && slot.LogicAccuracyWarning == null && slot.AccuracyBannerShown == null,
                    $"logic that matches the server warns about the version: {slot.LogicAccuracyWarning}");
                UiTestExpect(!site.Requests.Any(r => r.StartsWith("/dl/", StringComparison.Ordinal)) && engine.ComponentRequests("AtlasChecksum").Count == 0 && slot.ChosenApworld == null,
                    $"Atlas looked for the seed's version though logic works: {string.Join(", ", site.Requests)}");
                UiTestExpect(!_alertLog.Entries.Any(e => e.Message.StartsWith("Quill:", StringComparison.Ordinal) && e.Message.Contains("another version")), "a card said the version differs");
                ExpectLogic(slot, "on the installed version", inLogic: new long[] { 2000 }, outOfLogic: new long[] { 2001, 2002, 2003 }, goal: false, active: 1);
                ShowEnginePage();
                await UiTestWaitAsync(0.3);
                var (glyph, text) = _enginePage!.SlotStateForTests(slot);
                UiTestExpect(glyph == "✔" && text.Contains("though the seed was made with another"), $"the Atlas Engine page says {glyph} {text}");
                ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Games);
                _gamesPage!.Select("Test Game");
                await UiTestWaitAsync(0.2);
                var lines = _gamesPage.SlotLines();
                UiTestExpect(lines.Any(l => l.StartsWith("Quill:") && l.Contains("not the version its seed was made with") && !l.Contains("5eed")), $"the Games page says: {string.Join(" | ", lines)}");
            });

    /// <summary>(b) Another version and a world that doesn't match: Atlas tries the trusted versions it knows, newest first, says so meanwhile, and uses the seed's for the slot.</summary>
    private Task SeedVersionFoundAsync()
    {
        const string seed = "5eedb5eedb5eedb5eedb5eedb5eedb5eedb5eedb";
        return WithSeedVersionsAsync(seed, fake => fake.RebuildMatch = false,
            new[] { ("5.3.0", "data-5.3.0", true), ("5.2.0", seed, true), ("5.1.0", seed, true) },
            async (slot, engine, site, gate) =>
            {
                await UiTestWaitForAsync(() => slot.AccuracyBannerShown?.Contains("trying the seed's version") == true ? slot : null, "the banner to say Atlas is trying the seed's version");
                UiTestExpect(slot.SeedVersionWanted && slot.AccuracyBannerShown!.Contains("another version of Test Game") && !slot.ChooseVersionOffered && !slot.AccuracyBannerShown.Contains("5eed"),
                    $"while trying, the banner says: {slot.AccuracyBannerShown}");
                gate.Set();
                await UiTestWaitForAsync(() => slot.ChosenApworld?.Version == "5.2.0" && slot.LogicSettled && slot.ApworldMatchesSeed == true ? slot : null, "the slot to run on the seed's version", 60);
                var downloads = site.Requests.Where(r => r.StartsWith("/dl/", StringComparison.Ordinal)).ToList();
                UiTestExpect(downloads.SequenceEqual(new[] { "/dl/5.3.0/test_game.apworld", "/dl/5.2.0/test_game.apworld" }),
                    $"Atlas downloaded {string.Join(", ", downloads)}, not the newest first stopping at the match");
                UiTestExpect(engine.ComponentRequests("AtlasChecksum").Count == 2, $"{engine.ComponentRequests("AtlasChecksum").Count} versions were loaded to read their data");
                var init = engine.Requests("init")[^1];
                UiTestExpect(((string?)init["apworld_override"])?.Replace('\\', '/').Contains("/5.2.0/") == true && (string?)init["expected_checksum"] == seed,
                    $"logic didn't restart on the seed's version: {init["apworld_override"]}");
                UiTestExpect(slot.UsingSeedApworld && slot.LogicAccuracyWarning == null && slot.AccuracyBannerShown == null, $"after the switch, the banner says: {slot.AccuracyBannerShown}");
                UiTestExpect(_alertLog.Entries.Any(e => e.Message.StartsWith("Quill:", StringComparison.Ordinal) && e.Message.Contains("5.2.0") && e.Message.Contains("found")), "no card said which version Atlas found");
                UiTestExpect(DataManager.LoadSettings().SlotApworlds.TryGetValue(slot.AnnotationKey, out var chosen) && chosen.MatchesSeed && chosen.SeedChecksum == seed,
                    "the version wasn't kept for the slot and its seed");
            });
    }

    /// <summary>(c) A world that can't be rebuilt, on a seed of another version none of three known ones matches: then the Logic banner offers the picker.</summary>
    private Task SeedVersionNotFoundAsync()
    {
        const string seed = "5eedc5eedc5eedc5eedc5eedc5eedc5eedc5eedc";
        return WithSeedVersionsAsync(seed, fake => fake.StartError = ("generation_failed", "None of the YAMLs tried could rebuild this world."),
            new[] { ("6.3.0", "data-6.3.0", true), ("6.2.0", "data-6.2.0", true), ("6.1.0", "data-6.1.0", true), ("6.0.0", seed, true) },
            async (slot, engine, site, gate) =>
            {
                await UiTestWaitForAsync(() => slot.EngineProblem?.Code == "generation_failed" && slot.LogicNoticeShown?.Contains("trying the seed's version") == true ? slot : null,
                    "the Logic banner to say Atlas is trying the seed's version");
                UiTestExpect(!slot.LogicNoticeButtons().Contains("Choose the version…"), $"while trying, the banner offers: {string.Join(", ", slot.LogicNoticeButtons())}");
                gate.Set();
                await UiTestWaitForAsync(() => slot.LogicNoticeButtons().Contains("Choose the version…") ? slot : null, "the Logic banner to offer Choose the version…", 60);
                var downloads = site.Requests.Where(r => r.StartsWith("/dl/", StringComparison.Ordinal)).ToList();
                UiTestExpect(downloads.SequenceEqual(new[] { "/dl/6.3.0/test_game.apworld", "/dl/6.2.0/test_game.apworld", "/dl/6.1.0/test_game.apworld" }),
                    $"Atlas downloaded {string.Join(", ", downloads)}, not three at most, newest first");
                UiTestExpect(slot.ChosenApworld == null && slot.LogicNoticeShown!.Contains("None of the versions Atlas knows") && !slot.LogicNoticeShown.Contains("5eed"), $"the banner says: {slot.LogicNoticeShown}");
                UiTestExpect(_alertLog.Entries.Any(e => e.Message.StartsWith("Quill:", StringComparison.Ordinal) && e.Message.Contains("couldn't find")), "no card said Atlas couldn't find the seed's version");
                UiTestExpect(site.Requests.Where(r => !r.StartsWith("/dl/", StringComparison.Ordinal)).All(r => r.StartsWith("/repos/stranger/seed-versions/releases", StringComparison.Ordinal)),
                    $"Atlas asked GitHub while looking for the seed's version: {string.Join(", ", site.Requests)}");
                slot.PressLogicNotice("Choose the version…");
                var picker = await UiTestWaitForAsync(() => GetTree().Root.GetChildren().OfType<AP_Atlas.UI.ApworldPickerDialog>().FirstOrDefault(d => !d.IsQueuedForDeletion()), "the version picker");
                UiTestExpect(new[] { "6.3.0", "6.2.0", "6.1.0" }.All(v => picker.Choices.Any(c => c.Version == v && c.Downloaded && !c.MatchesSeed)) && !picker.Choices.Any(c => c.MatchesSeed),
                    $"the picker lists {string.Join(", ", picker.Choices.Select(c => c.Version))}");
                picker.EmitSignal(AcceptDialog.SignalName.Canceled);
            });
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
            // With the room's flags: the engine counts it as progression from them, as Universal Tracker's own client does.
            UiTestExpect(sword["items"]?[0] is JArray swordItem && (int)swordItem[1] == 1, $"the Sword went to the engine without its progression flag: {sword["items"]}");
            // Why the tower isn't in logic: the engine names the one item it lacks.
            var why = await slot.ExplainLocationAsync(2002);
            UiTestExpect(why is { InLogic: false } && why.SingleUnlocks?.SequenceEqual(new[] { "Shield" }) == true,
                $"the tower's \"why\" didn't name the Shield: {(why == null ? "no answer" : Newtonsoft.Json.JsonConvert.SerializeObject(why))}");
            // Properties: the plain answer in Logic; the access rule (the engine's own detail) in a folded Rules part of it.
            var foldsBefore = (_appSettings.CollapsedPropertySections.ToList(), _appSettings.ExpandedPropertySections.ToList());
            AP_Atlas.Core.Inspector.Inspect(slot.LocationTarget(2002));
            await UiTestWaitForAsync(() => _propertiesPanel.SectionCollapsed("Rules") != null ? slot : null, "the tower's Rules part in Properties");
            UiTestExpect(_propertiesPanel.SectionCollapsed("Logic") == true && _propertiesPanel.SectionCollapsed("Rules") == true, "Logic or its Rules part doesn't start folded");
            UiTestExpect(ShowsText(_propertiesPanel.SectionBody("Rules")!, "Has Shield") && ShowsText(_propertiesPanel.SectionBody("Logic")!, "Opens with any one of"),
                "the access rule isn't in Rules, or the plain answer left Logic");
            _propertiesPanel.ToggleSection("Rules");
            UiTestExpect(_propertiesPanel.SectionCollapsed("Rules") == false && _appSettings.ExpandedPropertySections.Contains("Rules"), "opening Rules wasn't remembered");
            _propertiesPanel.ToggleSection("Rules");
            // A slot's dense sections start folded too; its overview doesn't.
            AP_Atlas.Core.Inspector.Inspect(AP_Atlas.Core.InspectTarget.ForSlot(profile.Id, "Tester"));
            await UiTestWaitForAsync(() => _propertiesPanel.SectionCollapsed("Overview") != null ? slot : null, "the slot in Properties");
            UiTestExpect(_propertiesPanel.SectionCollapsed("Overview") == false && _propertiesPanel.SectionCollapsed("Accuracy") == true
                && _propertiesPanel.SectionCollapsed("Room") == true && _propertiesPanel.SectionCollapsed("Tracking") == true, "the slot's Accuracy, Room or Tracking doesn't start folded");
            // Slot data makes its rows only when it's opened (a big world's is thousands).
            UiTestExpect(_propertiesPanel.SectionCollapsed("Slot data") == true && !ShowsText(_propertiesPanel.SectionBody("Slot data")!, "slot_name"),
                "the folded Slot data made its rows");
            _propertiesPanel.ToggleSection("Slot data");
            UiTestExpect(ShowsText(_propertiesPanel.SectionBody("Slot data")!, "slot_name"), "opening Slot data didn't make its rows");
            _propertiesPanel.ToggleSection("Slot data");
            (_appSettings.CollapsedPropertySections, _appSettings.ExpandedPropertySections) = foldsBefore;

            // The Shield, sent without flags (as the server's /send does): still progression, because the world says so.
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(1, new long[] { 1001 }));
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.IsLocationInLogic(2002) == true ? slot : null, "the Shield to open the tower");
            ExpectLogic(slot, "with the Sword and the Shield", inLogic: new long[] { 2000, 2001, 2002 }, outOfLogic: new long[] { 2003 }, goal: true, active: 3);
            await ExpectGoModeAsync(slot, true, "with the Sword and the Shield");
            UiTestExpect(slot.UnlockStepOf(2002) == (2, 3, "Shield") && slot.LogicStepCount == 2, $"the tower's step is {slot.UnlockStepOf(2002)} of {slot.LogicStepCount}");
            await ExpectTreeAsync(slot, "with the Sword and the Shield", "── Base Logic (Starting Reachable) ──", "Cave Chest", "── Unlocked by: Sword (1 checks) ──", "Locked Door",
                "── Unlocked by: Shield (1 checks) ──", "Tower Top");

            // A start inventory item: the server sends it with location -2 and no flags, and it isn't in the item pool, yet the
            // engine must hear of it (a start inventory's progression opened nothing before).
            int askedBeforeStart = engine.Requests("steps").Count;
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(2, new long[] { 1002 }, location: -2));
            await UiTestWaitForAsync(() => engine.Requests("steps").Count > askedBeforeStart ? slot : null, "the engine to be asked about the start inventory item");
            await UiTestWaitForAsync(() => slot.LogicSettled ? slot : null, "logic to settle after the start inventory item");
            UiTestExpect(Ids(engine.Requests("steps")[^1]["items"]).SequenceEqual(new long[] { 1002 }), "the start inventory item wasn't the one the engine was asked about");
            // Filler changes nothing about logic, so the engine isn't asked.
            int asked = engine.Requests("steps").Count;
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(3, new long[] { 1002 }));
            await UiTestWaitForAsync(() => slot.Session.Items.AllItemsReceived.Count == 4 ? slot : null, "the Rupee to arrive");
            await UiTestWaitAsync(0.3);
            UiTestExpect(engine.Requests("steps").Count == asked && slot.LogicSettled, "the engine was asked about filler");

            // A check done in the game: it's no longer counted as one to do.
            await server.BroadcastAsync(FakeArchipelagoServer.LocationsChecked(2000));
            await UiTestWaitForAsync(() => slot.ActiveLogicCount == 2 ? slot : null, "the checked chest to leave the count");
            // The Logic Tracker lists the checks left: the done chest goes, and its step with it; Show checked brings them back.
            await ExpectTreeAsync(slot, "with the chest checked", "── Unlocked by: Sword (1 checks) ──", "Locked Door",
                "── Unlocked by: Shield (1 checks) ──", "Tower Top");
            slot.ShowCheckedLogic(true);
            await ExpectTreeAsync(slot, "with Show checked", "── Base Logic (Starting Reachable) ──", "Cave Chest", "── Unlocked by: Sword (1 checks) ──", "Locked Door",
                "── Unlocked by: Shield (1 checks) ──", "Tower Top");
            UiTestExpect(_appSettings.LogicShowChecked, "Show checked wasn't remembered");
            slot.ShowCheckedLogic(false);
            // Find in Logic Tracker (Properties' button) on the done chest turns Show checked on, so its row shows, selected.
            slot.RevealLogicRow(2000);
            UiTestExpect(_appSettings.LogicShowChecked && slot.SelectedLogicRow == "Cave Chest", $"revealing a done check: Show checked {_appSettings.LogicShowChecked}, selected {slot.SelectedLogicRow}");
            slot.ShowCheckedLogic(false);

            // The engine crashes on the Gem. Nothing half-done is kept: logic is unknown until a new engine has rebuilt it
            // from scratch, which starts after 2 seconds (the first of its restarts).
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(4, new long[] { 1099 }, flags: 1));
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
            // The start inventory item (1002) is rebuilt with the rest: the engine must hear of it again.
            UiTestExpect(asks.SequenceEqual(new[] { "True: [] then [1000,1001,1002,1099]" }), $"the new engine was asked: {string.Join("; ", asks)}");
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

            // The engine's parts changed (its game's apworld added or updated): the slot restarts by itself on a fresh
            // engine (its pool retired the old process), without a reconnect, and a card says so. Another game's apworld
            // leaves it alone.
            string root = AtlasEngine.Resolve(_appSettings).Root;
            AtlasEngine.RaisePartsChangedForTests(new[] { new AtlasEngine.EngineChange("apworld", root, "Test Game") });
            await UiTestWaitForAsync(() => slot.LogicSettled && engine.Starts == 5 ? slot : null, "logic to restart on its own after the engine's parts changed");
            ExpectLogic(slot, "after the engine's parts changed", inLogic: new long[] { 2001, 2002, 2003 }, outOfLogic: Array.Empty<long>(), goal: true, active: 3);
            UiTestExpect(_alertLog.Entries.Any(e => e.Message.Contains("Restarting logic for Tester")), "no card said logic was restarting on the updated engine");
            AtlasEngine.RaisePartsChangedForTests(new[] { new AtlasEngine.EngineChange("apworld", root, "Other Game") });
            await UiTestWaitAsync(0.5);
            UiTestExpect(engine.Starts == 5 && slot.LogicSettled, "a change to another game's apworld restarted the slot");

            // A version chosen for the slot's seed (the version picker's choice): kept for the slot and the seed, and logic
            // restarts on a fresh engine with it, without a reconnect.
            string chosenFile = System.IO.Path.Combine(AP_Atlas.Core.Games.GameFiles.ApworldsFolder(DataManager.GetDataDirectory(), "Test Game"), "yours", "test_game.apworld");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(chosenFile)!);
            AP_Atlas.Core.SafeFile.WriteAllText(chosenFile, "a stand-in");
            slot.UseApworld(chosenFile, "9.9.9", "your file", matchesSeed: false);
            await UiTestWaitForAsync(() => slot.LogicSettled && engine.Starts == 6 ? slot : null, "logic to restart on the version chosen for the slot");
            UiTestExpect(slot.ChosenApworld?.File == chosenFile && slot.ChosenApworld.Version == "9.9.9" && DataManager.LoadSettings().SlotApworlds.ContainsKey(slot.AnnotationKey),
                "the version chosen for the slot wasn't kept for it and its seed");
            _appSettings.SlotApworlds.Remove(slot.AnnotationKey);

            // The Games page says which version each connected slot runs: with the engine's world data differing from the
            // seed's but its rebuilt world matching the server's, the installed version runs and nothing warns.
            engine.DataChecksum = "0000000000000000000000000000000000000000";
            engine.Apply();
            slot.RetryLogicEngine();
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.ApworldMatchesSeed == false ? slot : null, "the engine to report that the seed's data differs");
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Games);
            _gamesPage!.Select("Test Game");
            await UiTestWaitAsync(0.2);
            var lines = _gamesPage.SlotLines();
            UiTestExpect(lines.Any(l => l.StartsWith("Tester:") && l.Contains("not the version its seed was made with")) && slot.LogicAccuracyWarning == null && !slot.SeedVersionWanted,
                $"the Games page doesn't say the slot runs the installed version, or logic warns: {string.Join(" | ", lines)} / {slot.LogicAccuracyWarning}");
            engine.DataChecksum = LogicWorldChecksum;
            engine.Apply();
            slot.RetryLogicEngine();
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.ApworldMatchesSeed == true ? slot : null, "the engine to match the seed again");
            _gamesPage.Select("Test Game");
            await UiTestWaitAsync(0.2);
            UiTestExpect(_gamesPage.SlotLines().Any(l => l.StartsWith("Tester:") && l.Contains("matches its seed")), $"the Games page doesn't say the slot's apworld matches: {string.Join(" | ", _gamesPage.SlotLines())}");
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.LogicTracker);

            // An answer that doesn't say what each new item opened is a failure, never taken for "it opened nothing": logic
            // starts again (after 2 s: the restart above gave it a fresh set of tries).
            engine.ShortSteps = true;
            engine.Apply();
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(5, new long[] { 1000 }, flags: 1));
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

    private async Task BkAsync()
    {
        StartFakeEngine(UiTestPython());
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
            UiTestExpect(!slot.Bk && !slot.BkShown && string.IsNullOrEmpty(slot.ProgressionTracker.Banner), "a slot with a check in logic is said to be BK");
            // The one check in logic is done: nothing is in logic and three remain. BK, said on the Logic Tracker, Key Items and the slot card.
            await server.BroadcastAsync(FakeArchipelagoServer.LocationsChecked(2000));
            await UiTestWaitForAsync(() => slot.Bk && slot.BkShown ? slot : null, "the Logic Tracker to say BK");
            UiTestExpect(slot.BkText.StartsWith("BK: 1 of 4 checks done") && slot.ProgressionTracker.Banner == slot.BkText, $"the BK banner says \"{slot.BkText}\"; Key Items says \"{slot.ProgressionTracker.Banner}\"");
            await UiTestWaitForAsync(() => _activeSessionsList.FindChildren("StatusFooter", nameof(Label), true, false).OfType<Label>().FirstOrDefault(l => l.Text.Contains("BK")), "the slot card to say BK");
            // An item from another player opens the door: BK is over everywhere.
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(0, new long[] { 1000 }, flags: 1));
            await UiTestWaitForAsync(() => !slot.Bk && !slot.BkShown && string.IsNullOrEmpty(slot.ProgressionTracker.Banner) ? slot : null, "BK to end when a check opens");
            await UiTestWaitForAsync(() => _activeSessionsList.FindChildren("StatusFooter", nameof(Label), true, false).OfType<Label>().FirstOrDefault(l => l.Text.Contains("Live") && !l.Text.Contains("BK")), "the slot card to stop saying BK");
        }
        finally
        {
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
            AtlasEngine.TestPython = null;
        }
    }

    private async Task HiddenChecksAsync()
    {
        StartFakeEngine(UiTestPython());
        string zip = System.IO.Path.Combine(AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory(), "uitest_hidden_pack.zip");
        FakeMapPack.Write(zip, "UI test hidden pack", "Test Game", locationsJson:
            """[{"name":"Cave","sections":[{"name":"Cave Chest"}],"map_locations":[{"map":"World","x":10,"y":10}]},""" +
            """{"name":"Door","sections":[{"name":"Locked Door"}],"map_locations":[{"map":"World","x":200,"y":10}]},""" +
            """{"name":"Tower","sections":[{"name":"Tower Top"}],"map_locations":[{"map":"World","x":400,"y":10}]}]""");
        await using var server = LogicWorldServer();
        var profile = new MultiworldProfile { Name = "UI test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        bool logicShowHidden = _appSettings.LogicShowHidden, mapShowHidden = _appSettings.MapShowHidden;
        _appSettings.LogicShowHidden = false;
        _appSettings.MapShowHidden = false;
        string key = "";
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            key = slot.AnnotationKey;
            host.ShowTool(AP_Atlas.UI.Tool.LogicTracker);
            // The Sword opens the door: the chest and the door are in logic.
            await UiTestWaitForAsync(() => slot.LogicSettled ? slot : null, "the slot's logic to start");
            await server.BroadcastAsync(FakeArchipelagoServer.ReceivedItems(0, new long[] { 1000 }, flags: 1));
            await UiTestWaitForAsync(() => slot.LogicSettled && slot.ActiveLogicCount == 2 && slot.LogicRowShown(2001) ? slot : null, "the door to come into logic");
            await UiTestWaitForAsync(() => slot.IsOnMap(2001) ? slot : null, "the slot's map pack to place the door");
            void Run(List<AP_Atlas.UI.FixItem> items, string startsWith)
            {
                var item = items.FirstOrDefault(i => i.Label.StartsWith(startsWith, StringComparison.Ordinal));
                UiTestExpect(item?.Run != null, $"the menu has no \"{startsWith}\": {string.Join(" | ", items.Select(i => i.Label))}");
                item!.Run!();
            }

            // 1. The Logic Tracker: a right click on a check's row opens its menu; Hide takes the row out of the list and the
            //    count while logic still calls it in logic, and the card's Undo brings it back.
            slot.LogicRow(2001)!.Select(1);
            slot.LogicTree.EmitSignal(Tree.SignalName.ItemMouseSelected, Vector2.Zero, (long)MouseButton.Right);
            var menu = await UiTestWaitForAsync(() => GetTree().Root.FindChildren("*", nameof(PopupMenu), true, false).OfType<PopupMenu>()
                .FirstOrDefault(m => IsInstanceValid(m) && m.HasMeta("fix_menu") && m.Visible), "the row's menu on a right click");
            menu.Hide();
            var rowItems = slot.LogicRowItems(2001);
            UiTestExpect(rowItems.Any(i => i.Label == "Exclude") && rowItems.Any(i => i.Label == "Flag" && i.Submenu?.Count == 5) && rowItems.Any(i => i.Label == "Show on map")
                && rowItems.Any(i => i.Label == "Copy name"), "the row's menu lacks Exclude, Flag ▸, Show on map or Copy name: " + string.Join(" | ", rowItems.Select(i => i.Label)));
            Run(rowItems, "Hide");
            await UiTestWaitForAsync(() => !slot.LogicRowShown(2001) && slot.ActiveLogicCount == 1 ? slot : null, "the hidden door to leave the list and the count");
            UiTestExpect(slot.IsLocationInLogic(2001) == true && AP_Atlas.Core.Annotations.IsHidden(key, 2001), "hiding the door changed its logic, or wasn't kept");
            UiTestExpect(slot.ShowHiddenText == "Show hidden (1)", $"the Show hidden button says \"{slot.ShowHiddenText}\"");
            var card = await UiTestWaitForAsync(() => _alerts.Cards.FirstOrDefault(c => c.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(l => l.Text.Contains("Hid \"Locked Door\""))),
                "the card saying the door was hidden");
            _alerts.ActionButtonOf(card)!.EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitForAsync(() => slot.LogicRowShown(2001) && slot.ActiveLogicCount == 2 && slot.ShowHiddenText == null ? slot : null, "the card's Undo to bring the door back");

            // Show hidden lists a hidden check greyed (remembered), and it still doesn't count; finding it turns Show hidden on.
            slot.SetChecksHidden(new long[] { 2001 }, true);
            await UiTestWaitForAsync(() => !slot.LogicRowShown(2001) ? slot : null, "the door hidden again");
            slot.ShowHiddenLogic(true);
            UiTestExpect(slot.LogicRowShown(2001) && slot.LogicRowGreyed(2001) && _appSettings.LogicShowHidden && slot.ActiveLogicCount == 1,
                $"Show hidden: listed {slot.LogicRowShown(2001)}, greyed {slot.LogicRowGreyed(2001)}, remembered {_appSettings.LogicShowHidden}, {slot.ActiveLogicCount} in logic");
            slot.ShowHiddenLogic(false);
            slot.RevealLogicRow(2001);
            UiTestExpect(slot.LogicRowShown(2001) && _appSettings.LogicShowHidden && slot.SelectedLogicRow == "Locked Door", "finding a hidden check in the Logic Tracker didn't show its row");
            slot.ShowHiddenLogic(false);

            // 2. The map: the door's pin goes and its check leaves the counts; Show hidden checks dims it and says why
            //    (remembered); the pin's menu unhides and hides each of its checks.
            host.ShowTool(AP_Atlas.UI.Tool.MapTracker);
            var map = slot.MapTracker;
            AP_Atlas.UI.MapPinButton? Pin(string name) => map.FindChildren("*", "", true, false).OfType<AP_Atlas.UI.MapPinButton>()
                .FirstOrDefault(p => IsInstanceValid(p) && !p.IsQueuedForDeletion() && p.TooltipText.StartsWith(name + "\n", StringComparison.Ordinal));
            await UiTestWaitForAsync(() => Pin("Cave") != null && Pin("Tower") != null && Pin("Door") == null ? map : null, "the map without the hidden door's pin");
            UiTestExpect(map.LocationState(2001).Contains("hidden by you"), $"the door's state on the map is \"{map.LocationState(2001)}\"");
            map.ShowHiddenBox.ButtonPressed = true;
            var dimmed = await UiTestWaitForAsync(() => Pin("Door") is { } p && p.Modulate.A < 1f ? p : null, "the hidden door's pin, dimmed with Show hidden checks");
            UiTestExpect(dimmed.TooltipText.Contains("Hidden by you") && _appSettings.MapShowHidden, $"the dimmed pin says \"{dimmed.TooltipText}\" (remembered: {_appSettings.MapShowHidden})");
            map.ShowHiddenBox.ButtonPressed = false;
            await UiTestWaitForAsync(() => Pin("Door") == null ? map : null, "the hidden door's pin to go again");
            var door = slot.EffectivePack.Locations.First(l => l.Name == "Door");
            Run(map.PinItems(door, 200, 10), "Unhide \"Locked Door\"");
            await UiTestWaitForAsync(() => !AP_Atlas.Core.Annotations.IsHidden(key, 2001) && Pin("Door") != null && slot.ActiveLogicCount == 2 ? map : null, "the pin's menu to unhide the door");
            Run(map.PinItems(door, 200, 10), "Hide \"Locked Door\"");
            await UiTestWaitForAsync(() => AP_Atlas.Core.Annotations.IsHidden(key, 2001) && Pin("Door") == null && slot.ActiveLogicCount == 1 ? map : null, "the pin's menu to hide the door");
            // A check no pin shows: its row's menu hides it too, and it leaves Not on the map.
            UiTestExpect(map.UnplacedNames.Contains("Gem Vault"), "the vault isn't under Not on the map: " + string.Join(", ", map.UnplacedNames));
            UiTestExpect(map.PinItems(door, 200, 10).Any(i => i.Label == "Unhide \"Locked Door\""), "the hidden door's pin menu doesn't offer Unhide");
            UiTestExpect(map.UnplacedItems(2003, "Gem Vault").Any(i => i.Label == "Hide \"Gem Vault\""), "a check out of logic isn't offered a plain Hide");
            Run(map.UnplacedItems(2003, "Gem Vault"), "Hide \"Gem Vault\"");
            await UiTestWaitForAsync(() => !map.UnplacedNames.Contains("Gem Vault") ? map : null, "the hidden vault to leave Not on the map");
            // Show on map on the hidden door turns Show hidden checks on, so its pin is there to see.
            UiTestExpect(slot.RevealOnMap(2001), "Show on map didn't find the hidden door");
            await UiTestWaitForAsync(() => Pin("Door") is { } p && p.Modulate.A < 1f && _appSettings.MapShowHidden ? p : null, "Show on map to draw the hidden door's pin");
            map.ShowHiddenBox.ButtonPressed = false;
            await UiTestWaitForAsync(() => Pin("Door") == null ? map : null, "the hidden door's pin to go again");

            // 3. Properties: a hidden check says so, Unhide and Hide switch it; the slot counts what's hidden.
            AP_Atlas.Core.Inspector.Inspect(slot.LocationTarget(2001));
            var unhide = await UiTestWaitForAsync(() => _propertiesPanel.ActionButton("Unhide"), "Properties' Unhide on the hidden door");
            UiTestExpect(ShowsText(_propertiesPanel, "logic still calls it in logic"), "Properties doesn't say the door is hidden");
            unhide.EmitSignal(BaseButton.SignalName.Pressed);
            var hide = await UiTestWaitForAsync(() => !AP_Atlas.Core.Annotations.IsHidden(key, 2001) ? _propertiesPanel.ActionButton("Hide") : null, "Properties' Unhide, then its Hide");
            hide.EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitForAsync(() => AP_Atlas.Core.Annotations.IsHidden(key, 2001) ? slot : null, "Properties' Hide");

            // 4. BK: the chest is done and the only check left in logic is hidden, so nothing is; the banner says what's hidden.
            host.ShowTool(AP_Atlas.UI.Tool.LogicTracker);
            await server.BroadcastAsync(FakeArchipelagoServer.LocationsChecked(2000));
            await UiTestWaitForAsync(() => slot.Bk && slot.BkShown ? slot : null, "hiding the last check in logic to make the slot BK");
            UiTestExpect(slot.BkText.Contains("(1 hidden by you)"), $"the BK banner says \"{slot.BkText}\" (the door in logic is hidden; the vault is out of logic)");

            // The slot's Unhide all, asked first, shows every hidden check again.
            AP_Atlas.Core.Inspector.Inspect(AP_Atlas.Core.InspectTarget.ForSlot(profile.Id, "Tester"));
            var unhideAll = await UiTestWaitForAsync(() => _propertiesPanel.ActionButton("Unhide all…"), "the slot's Unhide all in Properties");
            UiTestExpect(ShowsText(_propertiesPanel, "Hidden by you"), "the slot's Properties doesn't count what's hidden");
            unhideAll.EmitSignal(BaseButton.SignalName.Pressed);
            var confirm = await UiTestWaitForAsync(() => GetTree().Root.FindChildren("*", nameof(ConfirmationDialog), true, false).OfType<ConfirmationDialog>()
                .FirstOrDefault(d => IsInstanceValid(d) && d.Visible && d.Title == "Unhide all"), "Unhide all to ask first");
            confirm.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            await UiTestWaitForAsync(() => AP_Atlas.Core.Annotations.HiddenFor(key).Count == 0 && !slot.Bk && slot.ActiveLogicCount == 1 ? slot : null, "Unhide all to bring the door back into logic");
        }
        finally
        {
            host.ShowTool(AP_Atlas.UI.Tool.Connections);
            if (key.Length > 0) AP_Atlas.Core.Annotations.ClearHidden(key);
            DeleteProfile(profile);
            _appSettings.LogicShowHidden = logicShowHidden;
            _appSettings.MapShowHidden = mapShowHidden;
            AP_Atlas.Core.SafeFile.Delete(zip);
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

            // The chest hidden (logic is wrong about it, the user says): nothing is in logic, as the row already says.
            slot.SetChecksHidden(new long[] { 2000 }, true);
            await UiTestWaitForAsync(() => _cheese.SlotView(profile.Id, "Tester")?.Advice is { InSync: true, Reason: "Nothing in logic" } a ? a : null,
                "the suggestion with the only check in logic hidden to say nothing is in logic, as the row does");
            slot.SetChecksHidden(new long[] { 2000 }, false);
            await UiTestWaitForAsync(() => _cheese.SlotView(profile.Id, "Tester")?.Advice is { Status: "unblocked" } a ? a : null, "the suggestion with the chest shown again");

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
            AP_Atlas.Core.Annotations.ClearHidden(AP_Atlas.Core.Annotations.SlotKey(profile.Id, "Tester"));
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
            for (var row = tree.GetRoot()?.GetFirstChild(); row != null; row = row.GetNext())
                if (row.Visible) shown.Add(row.GetText(1));
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

    // Items as the engine is asked about them: [id, flags] (ids alone in older requests).
    private static long[] Ids(JToken? list) => list?.Select(id => id is JArray pair ? (long)pair[0] : (long)id).ToArray() ?? Array.Empty<long>();

    /// <summary>Thrown by a scenario that can't run here (it reports SKIP instead of PASS or FAIL).</summary>
    private sealed class UiTestSkip(string reason) : Exception(reason);

    /// <summary>
    /// Whether a panel's labels hold this text (BBCode included), leaving out the zero-width spaces Bbcode.Safe puts in a
    /// long run of text, which a reader doesn't see.
    /// </summary>
    private static bool PanelShows(Node panel, string text) =>
        panel.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().Any(label => label.Text.Replace(ZeroWidthSpace, "").Contains(text));

    /// <summary>Whether a panel's labels show this text, as read on screen (BBCode applied; zero-width spaces left out).</summary>
    /// <summary>Whether any label or rich text under <paramref name="root"/> shows this text.</summary>
    private static bool ShowsText(Node root, string text) =>
        PanelShowsText(root, text) || root.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(label => label.Text.Contains(text, StringComparison.Ordinal));

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
    /// <summary>The Updates scenario, failing if Godot reports a misuse meanwhile (a download's progress once reached the status bar from the download's thread).</summary>
    private async Task UpdatesWithoutGodotErrorsAsync()
    {
        int godotErrors = AP_Atlas.Core.GodotLog.Errors;
        try
        {
            // What the updater says while it downloads comes from the download's thread.
            await Task.Run(() => _updates!.Status("Downloading (a test line from another thread)"));
            await UiTestWaitAsync(0.3);
            UiTestExpect(_globalStatusLabel?.Text == "Downloading (a test line from another thread)", "the updater's status line from another thread didn't reach the status bar");
            await UpdatesAsync();
        }
        finally
        {
            godotErrors = AP_Atlas.Core.GodotLog.Errors - godotErrors;
        }
        UiTestExpect(godotErrors == 0, $"Godot reported {godotErrors} error(s) (the log file has them)");
    }

    /// <summary>The short setup: the permission first, then the panel's plain-words steps, Try again and Show details; never pip's lines.</summary>
    /// <summary>
    /// Atlas at Windows' display scale on real screens (1080p to 4K at 125% to 200%) and in a narrow window. Every pane stays inside the
    /// window without overlapping (Properties hides itself when the width can't hold it, its setting untouched), Home's
    /// cards take the columns the width allows, and every window and dialog fits the window it opens in.
    /// </summary>
    private async Task DisplayScaleAsync()
    {
        var root = GetTree().Root;
        var sizeBefore = root.Size;
        bool propertiesBefore = _appSettings.ShowPropertiesPanel;
        try
        {
            // Real screens: 4K at 150% and 200%, 1440p at 150%, 1080p at 125% and 150%, and an 1100x900 window at 125% (880 logical units wide: Properties can't fit there).
            foreach (var (scale, size) in new[] { (1.5f, new Vector2I(3840, 2160)), (2.0f, new Vector2I(3840, 2160)), (1.5f, new Vector2I(2560, 1440)), (1.25f, new Vector2I(1920, 1080)), (1.5f, new Vector2I(1920, 1080)), (1.25f, new Vector2I(1100, 900)) })
            {
                AP_Atlas.UI.WindowFit.TestScale = scale;
                AP_Atlas.UI.WindowFit.TestUsableRect = new Rect2I(Vector2I.Zero, size);
                root.Size = size;
                _appSettings.ShowPropertiesPanel = true;
                ApplyZoom();
                await UiTestWaitAsync(0.4);
                string at = $"at {scale}x in {size.X}x{size.Y}";
                GD.Print($"UITEST INFO root {at}: mode {root.Mode}, size {root.Size}, min {root.MinSize}, factor {root.ContentScaleFactor}, visible {GetViewport().GetVisibleRect().Size}");
                UiTestExpect(Mathf.IsEqualApprox(root.ContentScaleFactor, scale), $"the window isn't drawn at Windows' scale {at}");
                UiTestExpect(root.MinSize.X <= size.X && root.MinSize.Y <= size.Y, $"the window's smallest size {root.MinSize} doesn't fit the screen {at}");
                var visible = GetViewport().GetVisibleRect();
                var panes = new (string Name, Control? Node)[] { ("the activity bar", _activityBar), ("the slots panel", _sidebar), ("the explorer", _midLeftSidebar), ("the content", _contentStage), ("Properties", _propertiesSidebar), ("the status bar", _globalStatusBar) }
                    .Where(p => p.Node != null && p.Node.IsVisibleInTree()).ToList();
                foreach (var (name, node) in panes)
                {
                    var rect = node!.GetGlobalRect();
                    UiTestExpect(rect.Position.X >= -0.5f && rect.Position.Y >= -0.5f && rect.End.X <= visible.Size.X + 0.5f && rect.End.Y <= visible.Size.Y + 0.5f, $"{name} is off the window {at}: {rect} in {visible.Size}");
                }
                var across = panes.Where(p => p.Name != "the status bar").Select(p => p.Node!.GetGlobalRect()).OrderBy(r => r.Position.X).ToList();
                for (int i = 1; i < across.Count; i++)
                    UiTestExpect(across[i - 1].End.X <= across[i].Position.X + 0.5f || across[i - 1].Position.X == across[i].Position.X, $"panes overlap {at}: one ends at {across[i - 1].End.X}, the next starts at {across[i].Position.X}");
                if (size.X == 1100)
                    UiTestExpect(AutoHiddenParts.Contains("view.properties-panel") && _appSettings.ShowPropertiesPanel, $"Properties should hide itself {at} with its setting kept");
                else
                    UiTestExpect(!AutoHiddenParts.Contains("view.properties-panel"), $"Properties hid itself {at} though it fits");
                // A short window (720 logical units: 1080p at 150%, the 1100x900 window) shows the bar's icons alone; one 900 or more
                // tall (1440p at 150%, 4K at 150% and 200%) keeps the names; in between, whichever fits.
                float tall = size.Y / scale;
                if (tall < 800) UiTestExpect(_activityBar.Compact, $"the activity bar keeps its names {at} ({tall:0} logical units tall)");
                if (tall >= 900) UiTestExpect(!_activityBar.Compact, $"the activity bar drops its names {at} ({tall:0} logical units tall)");
                UiTestExpect(_activityBar.ButtonOf(AP_Atlas.UI.Tool.Engine).GetGlobalRect().End.Y <= _activityBar.GetGlobalRect().End.Y + 0.5f, $"the bar's last button is cut off {at}");

                ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Home);
                await UiTestWaitAsync(0.3);
                var home = _homePage!;
                float width = _contentStage!.Size.X;
                UiTestExpect(home.RightColumnRight <= home.GlobalPosition.X + home.Size.X + 1 && (home.TwoColumns || width - 40 < AP_Atlas.UI.HomePage.TwoColumnsFrom), $"Home's columns don't fit {width} {at} (two: {home.TwoColumns}, right edge {home.RightColumnRight})");

                // Every window and dialog fits the window it opens in (embedded here: a headless run has no native windows).
                async Task FitsAsync<T>(Func<T?> find, string what, Action close) where T : Window
                {
                    var window = await UiTestWaitForAsync(find, what);
                    await UiTestWaitAsync(0.3);
                    var area = AP_Atlas.UI.WindowFit.AvailableLogical(window).Size;
                    UiTestExpect(window.Position.X >= 0 && window.Position.Y >= 0 && window.Position.X + window.Size.X <= area.X && window.Position.Y + window.Size.Y <= area.Y,
                        $"{what} doesn't fit {at}: at {window.Position} size {window.Size} in {area}");
                    close();
                    await UiTestWaitAsync(0.2);
                }
                // The Atlas Engine page at its widest (Advanced open, its run shown, a long install path) needs no more width
                // than the content area had: a wider page would widen the stage past its place (the stage always holds the page).
                string? installPath = _appSettings.ArchipelagoInstallationPath;
                _appSettings.ArchipelagoInstallationPath = @"C:\Users\Someone With A Long Name\AppData\Local\Programs\Archipelago Multiworld Randomizer";
                try
                {
                    ShowEnginePage(Array.Empty<string>());
                    _enginePage!.RunView.Visible = true;
                    _enginePage.OnShown();
                    await UiTestWaitAsync(0.3);
                    var stage = _contentStage!.GetGlobalRect();
                    UiTestExpect(stage.End.X <= visible.Size.X + 0.5f && stage.End.Y <= visible.Size.Y + 0.5f && _contentStage.Size.X <= width + 0.5f,
                        $"the Atlas Engine page doesn't fit the content area {at}: the stage is {stage} (was {width} wide) in {visible.Size}");
                }
                finally
                {
                    _appSettings.ArchipelagoInstallationPath = installPath;
                    _enginePage!.RunView.Visible = false;
                }
                ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Home);
                _commands!.Run("help.about");
                await FitsAsync(() => GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => d.Title == "About The Archipelago Atlas"), "the About dialog", () => GetChildren().OfType<AcceptDialog>().First(d => d.Title == "About The Archipelago Atlas").GetOkButton().EmitSignal(BaseButton.SignalName.Pressed));
                _commands.Run("help.guide");
                await FitsAsync(() => GetChildren().OfType<AP_Atlas.UI.HelpWindow>().FirstOrDefault(), "the Help window", () => GetChildren().OfType<AP_Atlas.UI.HelpWindow>().First().GetOkButton().EmitSignal(BaseButton.SignalName.Pressed));
                _commands.Run("help.shortcuts");
                await FitsAsync(() => GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => d.Title == "Keyboard Shortcuts"), "the shortcuts dialog", () => GetChildren().OfType<AcceptDialog>().First(d => d.Title == "Keyboard Shortcuts").GetOkButton().EmitSignal(BaseButton.SignalName.Pressed));
                AP_Atlas.UI.Dialogs.Confirm(this, "Fit?", "A confirmation that must fit the window at any scale, however long its text runs on, and it runs on for a while to be sure the label wraps.", "OK", () => { });
                await FitsAsync(() => GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == "Fit?"), "a confirmation", () => GetChildren().OfType<ConfirmationDialog>().First(d => d.Title == "Fit?").EmitSignal(ConfirmationDialog.SignalName.Canceled));
                await PressAsync("Ctrl+Shift+P");
                await FitsAsync(() => GetChildren().OfType<AP_Atlas.UI.CommandPalette>().FirstOrDefault(), "the command palette", () => GetChildren().OfType<AP_Atlas.UI.CommandPalette>().First().Hide());
            }
        }
        finally
        {
            AP_Atlas.UI.WindowFit.TestScale = null;
            AP_Atlas.UI.WindowFit.TestUsableRect = null;
            _appSettings.ShowPropertiesPanel = propertiesBefore;
            root.Size = sizeBefore;
            ApplyZoom();
            await UiTestWaitAsync(0.3);
        }
    }

    /// <summary>The Atlas Engine page's Your slots offers a YAML only when it would help; Advanced, Copy log and the seed test.</summary>
    private async Task EnginePageSlotsAsync()
    {
        await using var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame("0123456789abcdef0123456789abcdef01234567",
            new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        var profile = new MultiworldProfile { Name = "Engine page test", ServerUrl = server.Url.ToString() };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        string yaml = System.IO.Path.Combine(DataManager.GetDataDirectory(), "uitest_slot.yaml");
        await System.IO.File.WriteAllTextAsync(yaml, "name: Tester\ngame: Test Game\n");
        try
        {
            await OnConnectSlotPressedAsync("Tester", profile);
            var slot = await UiTestWaitForAsync(() => SlotView(profile.Id, "Tester"), "the slot's view");
            await UiTestWaitForAsync(() => slot.EngineProblem != null ? slot : null, "the slot to report its engine problem (no engine is set up here)");
            UiTestExpect(!slot.YamlWouldHelp, $"a YAML is offered for \"{slot.EngineProblem?.Code}\", which no YAML would help");
            ShowEnginePage();
            await UiTestWaitAsync(0.3);
            var window = _enginePage!;
            UiTestExpect(ShownContent() == window, "the Atlas Engine page didn't show");
            // Shown plainly, Advanced stays as the user left it (folded by default); Show details opens it.
            if (window.AdvancedOpen) AP_Atlas.UI.Kit.ToggleFold(window, AP_Atlas.UI.EnginePage.AdvancedKey);
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Home);
            ShowEnginePage();
            await UiTestWaitAsync(0.2);
            UiTestExpect(ShownContent() == window && !window.AdvancedOpen, "showing the page opened its Advanced part");
            UiTestExpect(!window.VerifyShown, "the seed test shows outside developer mode");
            bool developerBefore = _appSettings.DeveloperMode;
            try
            {
                _appSettings.DeveloperMode = true;
                window.OnShown();
                UiTestExpect(window.VerifyShown, "the seed test doesn't show in developer mode");
            }
            finally
            {
                _appSettings.DeveloperMode = developerBefore;
                window.OnShown();
            }
            window.RefreshSlotsNow();
            UiTestExpect(!window.SlotButtonTexts.Any(t => t.StartsWith("Link YAML")) && window.SlotButtonTexts.Contains("Restart logic"),
                $"the Slots list offers a YAML nobody asked for: {string.Join(", ", window.SlotButtonTexts)}");
            slot.LinkYaml(yaml);
            window.RefreshSlotsNow();
            UiTestExpect(window.SlotButtonTexts.Contains("Change YAML…") && window.SlotButtonTexts.Contains("Unlink"),
                $"a linked YAML isn't offered for a change: {string.Join(", ", window.SlotButtonTexts)}");
            slot.LinkYaml(null);
            window.RefreshSlotsNow();
            UiTestExpect(!window.SlotButtonTexts.Any(t => t.Contains("YAML")), $"an unlinked YAML still shows: {string.Join(", ", window.SlotButtonTexts)}");
            // Show details opens Advanced with another run's lines in the log; Copy log says so.
            ShowEnginePage(new[] { "a line from another run" });
            UiTestExpect(window.AdvancedOpen && window.LogText.Contains("a line from another run"), "Show details didn't open Advanced with the lines in the log");
            await UiTestWaitAsync(0.2);
            UiTestExpect(window.LogInView, "Show details didn't bring the log into view");
            var copy = window.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text == "Copy log");
            copy.EmitSignal(BaseButton.SignalName.Pressed);
            UiTestExpect(window.StatusText == "Log copied.", $"Copy log said \"{window.StatusText}\"");
            AP_Atlas.UI.Kit.ToggleFold(window, AP_Atlas.UI.EnginePage.AdvancedKey);
        }
        finally
        {
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Connections);
            DeleteProfile(profile);
            AP_Atlas.Core.SafeFile.Delete(yaml);
        }
    }

    private async Task EngineSetupPanelAsync()
    {
        var kind = AP_Atlas.Core.Permissions.EngineSetup;
        AP_Atlas.Core.Permissions.SetAlways(_appSettings, kind, null, false);
        int runs = 0;
        AP_Atlas.Core.EngineSetup.AtlasEngine.TestSetUp = async (log, progress, ct) =>
        {
            runs++;
            log("pretend: pip would print this");
            AP_Atlas.Core.EngineSetup.AtlasEngine.ReportStep("Downloading Python 3.12.10 from python.org…");
            progress(0.5f);
            await Task.Delay(150, ct);
            AP_Atlas.Core.EngineSetup.AtlasEngine.ReportStep("Running the health check…");
            await Task.Delay(100, ct);
            return runs > 1; // the first run fails, Try again succeeds
        };
        try
        {
            UiTestExpect(!AP_Atlas.Core.EngineSetup.AtlasEngine.Current.CanLaunch, "the test's data folder has no engine, so the short setup applies");
            ConfirmationDialog? Dialog() => GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d => d.Title == kind.Title);
            AP_Atlas.UI.EngineSetupPanel? Panel() => GetChildren().OfType<AP_Atlas.UI.EngineSetupPanel>().FirstOrDefault();

            // Allow once: the panel runs the setup and says what it's doing in plain words.
            OpenEngineSetup();
            var ask = await UiTestWaitForAsync(Dialog, "the engine permission dialog");
            UiTestExpect(Panel() == null, "nothing starts before the permission is answered");
            ask.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            var panel = await UiTestWaitForAsync(Panel, "the setup panel");
            await UiTestWaitForAsync(() => panel.StepText.Contains("Downloading Python") ? panel : null, "the panel's first step");
            UiTestExpect(!panel.StepText.Contains("pretend"), "pip's lines don't reach the panel");
            await UiTestWaitForAsync(() => panel.StepText.Contains("health check") ? panel : null, "the panel's second step");
            UiTestExpect(panel.ProgressValue >= 0.5, $"the bar went back down between steps ({panel.ProgressValue:0.00})");
            await UiTestWaitForAsync(() => panel.TryAgainShown ? panel : null, "the failed run's Try again");
            UiTestExpect(panel.OutcomeText.Contains("pretend setup failed"), "the panel says why the setup stopped: " + panel.OutcomeText);
            UiTestExpect(panel.DetailsShown, "Show details is offered after a failure");
            UiTestExpect(runs == 1, "the setup ran once");

            // Try again runs it again; this time it finishes.
            panel.EmitSignal(AcceptDialog.SignalName.CustomAction, "try-again");
            await UiTestWaitForAsync(() => panel.OutcomeText.Contains("is set up") ? panel : null, "the second run's Ready");
            UiTestExpect(runs == 2 && !panel.TryAgainShown, "after success there's nothing to try again");

            // Show details shows the Atlas Engine page, its Advanced part open with the run's lines in its log.
            panel.EmitSignal(AcceptDialog.SignalName.CustomAction, "details");
            await UiTestWaitForAsync(() => ShownContent() == _enginePage ? _enginePage : null, "the Atlas Engine page");
            UiTestExpect(_enginePage!.AdvancedOpen && _enginePage.LogText.Contains("pretend: pip would print this"), "Show details didn't open the page's Advanced part with the run's log");
            await UiTestWaitAsync(0.2);
            UiTestExpect(Panel() == null, "Show details left the panel over the page");
            UiTestExpect(_enginePage.LogInView, "Show details didn't bring the run's log into view");
            AP_Atlas.UI.Kit.ToggleFold(_enginePage, AP_Atlas.UI.EnginePage.AdvancedKey);
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Connections);

            // Don't allow: nothing starts, and the answer is kept for the session.
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, kind, null, false); // forgets "Allow once"
            OpenEngineSetup();
            var again = await UiTestWaitForAsync(Dialog, "the engine permission dialog, asked again");
            again.EmitSignal(ConfirmationDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.4);
            UiTestExpect(Panel() == null && runs == 2, "Don't allow starts nothing");
            UiTestExpect(AP_Atlas.Core.Permissions.DeniedThisSession(kind), "the refusal is remembered for the session");
        }
        finally
        {
            AP_Atlas.Core.EngineSetup.AtlasEngine.TestSetUp = null;
            AP_Atlas.Core.Permissions.AllowForSession(kind);
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, kind, null, false);
            foreach (var p in GetChildren().OfType<AP_Atlas.UI.EngineSetupPanel>()) p.QueueFree();
        }
    }

    private async Task DataFolderAsync()
    {
        string data = DataManager.GetDataDirectory();
        string program = System.IO.Path.Combine(data, "uitest-program"), local = System.IO.Path.Combine(data, "uitest-localappdata");
        System.IO.Directory.CreateDirectory(program);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(program, AP_Atlas.Core.DataFolder.PortableName), "a file where the folder should be");
        TestLocalAppData = local;
        try
        {
            var resolution = AP_Atlas.Core.DataFolder.Resolve(null, program, local, AP_Atlas.Core.DataFolder.Probe);
            UiTestExpect(resolution.Source == AP_Atlas.Core.DataFolder.Source.NeedsChoice && resolution.Problem != null && !System.IO.Directory.Exists(local),
                "a program folder that can't be written didn't ask, or something outside was touched before asking");
            string fallback = AP_Atlas.Core.DataFolder.DefaultFallback(local), pointer = AP_Atlas.Core.DataFolder.PointerPath(local);
            string? decided = null;
            bool? remembered = null;
            bool quit = false;
            AP_Atlas.UI.DataFolderDialog Open()
            {
                decided = null;
                remembered = null;
                var dialog = new AP_Atlas.UI.DataFolderDialog(resolution.Problem!, resolution.Portable, fallback, pointer, AP_Atlas.Core.DataFolder.Probe,
                    (folder, chosen) => { decided = folder; remembered = chosen; }, () => quit = true, text => Tr(text));
                AddChild(dialog);
                AP_Atlas.UI.WindowFit.Pop(dialog, 660);
                return dialog;
            }
            // The default: the local app data folder, made, nothing remembered.
            var dialog = Open();
            string text = string.Join(" ", dialog.FindChildren("*", nameof(Label), true, false).OfType<Label>().Select(l => l.Text));
            UiTestExpect(text.Contains(resolution.Portable) && text.Contains(resolution.Problem!), "the dialog doesn't name the folder that can't be used and why");
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(decided == fallback && remembered == false && System.IO.Directory.Exists(fallback) && !System.IO.File.Exists(pointer), $"Continue didn't take the local app data folder ({decided}, remembered {remembered})");
            // Another folder that can't be used: refused with the reason, the dialog stays. A usable one: taken, to be remembered.
            dialog = Open();
            dialog.UseAnother = true;
            dialog.AnotherPath = System.IO.Path.Combine(program, AP_Atlas.Core.DataFolder.PortableName, "inside");
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(decided == null && GodotObject.IsInstanceValid(dialog) && dialog.Problem.Length > 0, $"a folder that can't be used was taken, or the dialog closed ({dialog.Problem})");
            string chosen = System.IO.Path.Combine(data, "uitest-chosen");
            dialog.AnotherPath = chosen;
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitAsync(0.1);
            UiTestExpect(decided == chosen && remembered == true, $"a usable folder wasn't taken ({decided}, remembered {remembered})");
            AP_Atlas.Core.DataFolder.WritePointer(local, chosen);
            var again = AP_Atlas.Core.DataFolder.Resolve(null, program, local, AP_Atlas.Core.DataFolder.Probe);
            UiTestExpect(again.Source == AP_Atlas.Core.DataFolder.Source.Chosen && again.Path == chosen, "the remembered folder isn't taken on the next start");
            // Quit chooses nothing.
            dialog = Open();
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.1);
            UiTestExpect(quit && decided == null, "Quit decided something");
        }
        finally
        {
            TestLocalAppData = null;
            foreach (var open in GetChildren().OfType<AP_Atlas.UI.DataFolderDialog>().ToList()) open.QueueFree();
            foreach (string folder in new[] { program, local, System.IO.Path.Combine(data, "uitest-chosen") })
                if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, recursive: true);
        }
    }

    private async Task CrashReportsAsync()
    {
        await using var site = new AP_Atlas.Core.Testing.FakeWebSite();
        site.Answer = (path, _) => new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(path == "/api/42/envelope/" ? 200 : 404, System.Text.Encoding.UTF8.GetBytes("{\"id\":\"1\"}"), "application/json");
        string user = System.Environment.UserName;
        var profile = new MultiworldProfile { Name = "UI test (crash reports)", ServerUrl = "archipelago.gg:38281" };
        profile.Slots.Clear();
        profile.Slots.Add("Tester");
        _profiles.Add(profile);
        string logs = System.IO.Path.Combine(DataManager.GetDataDirectory(), "logs");
        var planted = new List<string>();
        string Plant()
        {
            System.IO.Directory.CreateDirectory(logs);
            string path = System.IO.Path.Combine(logs, $"crash_uitest_{planted.Count + 1}.txt");
            System.IO.File.WriteAllText(path, $"{DateTime.Now:O}\nUnhandled exception\n\nSystem.IO.IOException: {user} lost C:\\Users\\{user}\\Documents\\save.txt for Tester at archipelago.gg:38281 (mail {user}@example.com)\n   at MainTrackerWindow.Go() in C:\\Users\\{user}\\Atlas\\Scripts\\MainTrackerWindow.cs:line 7\n");
            planted.Add(path);
            return path;
        }
        Control? CardWith(string text) => _alerts.Cards.FirstOrDefault(card => card.FindChildren("*", nameof(Label), true, false).OfType<Label>().Any(label => label.Text.Contains(text)));
        bool Mentions(string text) => text.Contains(user, StringComparison.OrdinalIgnoreCase) || text.Contains("Users\\") || text.Contains("Tester") || text.Contains("archipelago.gg") || text.Contains("@example.com");
        TestSentryDsn = $"http://abc123@127.0.0.1:{new Uri(site.Site).Port}/42";
        SetUpCrashReports();
        var reports = _crashReports!;
        AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.CrashReports, null, false);
        AP_Atlas.Core.Permissions.DenyForSession(AP_Atlas.Core.Permissions.CrashReports);
        try
        {
            UiTestExpect(reports.Available && reports.Pending().Count == 0, "with an address given, reporting should be available with nothing pending");
            // 1. A problem last time: a card, then the dialog with the report as it would be sent; Send once posts it.
            Plant();
            await reports.OfferAsync();
            var card = await UiTestWaitForAsync(() => CardWith("had a problem"), "the crash report card");
            _alerts.ActionButtonOf(card)!.EmitSignal(BaseButton.SignalName.Pressed);
            var dialog = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.CrashReportDialog>().FirstOrDefault(), "the crash report dialog");
            string shown = dialog.FindChildren("*", nameof(TextEdit), true, false).OfType<TextEdit>().First().Text;
            UiTestExpect(!Mentions(shown) && shown.Contains("<user>") && shown.Contains("<name>") && shown.Contains("<host>") && shown.Contains("<email>") && shown.Contains("\\\\save.txt"),
                "the report shown still names the user, a path, the slot, the server or the e-mail, or lost the file's name: " + shown);
            dialog.Note = $"I was {user}, connecting Tester";
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await UiTestWaitForAsync(() => site.Received.Count == 1 ? site : null, "the report to be posted");
            var request = site.Received[0];
            string body = System.Text.Encoding.UTF8.GetString(request.Body);
            UiTestExpect(request.Method == "POST" && request.Path == "/api/42/envelope/" && request.Headers.TryGetValue("x-sentry-auth", out var auth) && auth.Contains("sentry_key=abc123")
                && request.Headers.TryGetValue("content-type", out var type) && type == "application/x-sentry-envelope", "the report wasn't posted as a Sentry envelope with its key");
            UiTestExpect(!Mentions(body) && body.Contains("I was <user>, connecting <name>") && body.Contains("\"release\":\"atlas@") && body.Contains("\"type\":\"event\""),
                "the posted report isn't scrubbed, or lacks the note or the build: " + body);
            await UiTestWaitForAsync(() => CardWith("was sent"), "the sent card");
            UiTestExpect(reports.Pending().Count == 0 && AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.CrashReports)
                && !AP_Atlas.Core.Permissions.IsAlwaysAllowed(_appSettings, AP_Atlas.Core.Permissions.CrashReports), "after Send once, the file is still pending or the permission isn't for this session only");
            // 2. Always send: the next problem goes without a card or a dialog.
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.CrashReports, null, true);
            Plant();
            await reports.OfferAsync();
            await UiTestWaitForAsync(() => site.Received.Count == 2 ? site : null, "the second report, sent without asking");
            await UiTestWaitAsync(0.3);
            UiTestExpect(CardWith("had a problem") == null && GetChildren().OfType<AP_Atlas.UI.CrashReportDialog>().Any() == false && reports.Pending().Count == 0, "with Always send, a card or the dialog still appeared");
            // 3. Don't send: dismissed, nothing posted, not offered again.
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.CrashReports, null, false);
            AP_Atlas.Core.Permissions.AllowForSession(AP_Atlas.Core.Permissions.CrashReports); // Send once earlier in the session: the next problem is still asked about
            Plant();
            await reports.OfferAsync();
            card = await UiTestWaitForAsync(() => CardWith("had a problem"), "the third card");
            _alerts.ActionButtonOf(card)!.EmitSignal(BaseButton.SignalName.Pressed);
            dialog = await UiTestWaitForAsync(() => GetChildren().OfType<AP_Atlas.UI.CrashReportDialog>().FirstOrDefault(), "the third dialog");
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            await UiTestWaitAsync(0.5);
            UiTestExpect(site.Received.Count == 2 && reports.Pending().Count == 0, "Don't send posted something, or left the file pending");
            // Without the permission, nothing is sent even when the code asks.
            await reports.SendAllAsync(new[] { planted[^1] }, null);
            await UiTestWaitAsync(0.3);
            UiTestExpect(site.Received.Count == 2, "a report was sent without the permission");
            await reports.OfferAsync();
            await UiTestWaitAsync(0.3);
            UiTestExpect(CardWith("had a problem") == null, "a dismissed report was offered again");
            // 4. Report a problem: every part scrubbed (a player seen in a room too) with a tick, the event as it would be sent;
            //    nothing posted before Send; Send posts the ticked parts with the note and shows the report's id, a copy kept.
            reports.RememberSeen(new[] { "Seenplayer" }, "play.example.org:4242");
            AP_Atlas.Core.Logger.LogInfo($"UI test marker: {user} at C:\\Users\\{user}\\marker.txt met Seenplayer at play.example.org:4242");
            int posted = site.Received.Count;
            int zipsBefore = System.IO.Directory.Exists(reports.ReportsDir) ? System.IO.Directory.GetFiles(reports.ReportsDir, "atlas-report-*.zip").Length : 0;
            AP_Atlas.UI.ProblemReportDialog OpenProblem()
            {
                _commands!.Run("help.report-problem");
                return AP_Atlas.UI.ProblemReportDialog.Showing ?? throw new InvalidOperationException("Report a problem opened no dialog");
            }
            AcceptDialog? Result(string id) => GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => IsInstanceValid(d) && !d.IsQueuedForDeletion() && d.HasMeta("problem_result") && d.GetMeta("problem_result").AsString() == id);
            var problem = OpenProblem();
            var parts = problem.Ticked.ToList();
            var logPart = parts.FirstOrDefault(p => p.Name == "atlas_log.txt");
            var systemPart = parts.FirstOrDefault(p => p.Name == "system.txt");
            UiTestExpect(logPart != null && systemPart != null && systemPart.Text.Contains("Godot") && parts.All(p => !Mentions(p.Text)), "the report's parts are missing or not scrubbed");
            UiTestExpect(logPart!.Text.Contains("UI test marker: <user> at <path>\\marker.txt met <name> at <host>"), "the log's marker line isn't scrubbed of the user, the path, the seen player and their server");
            problem.Note = $"It froze when {user} met Tester";
            UiTestExpect(problem.PreviewText.Contains("\"kind\": \"problem-report\"") && problem.PreviewText.Contains("It froze when <user> met <name>") && !Mentions(problem.PreviewText),
                "the event shown isn't the problem report with the scrubbed note: " + problem.PreviewText);
            await UiTestWaitAsync(0.2);
            UiTestExpect(site.Received.Count == posted, "Report a problem posted something before Send");
            problem.SetTicked(parts.IndexOf(systemPart!), false);
            UiTestExpect(problem.CanSend, "Send is greyed with parts ticked: " + problem.WhyNotSend);
            problem.Choose(AP_Atlas.UI.ProblemChoice.Send);
            await UiTestWaitForAsync(() => site.Received.Count == posted + 1 ? site : null, "the problem report to be posted");
            string sent = System.Text.Encoding.UTF8.GetString(site.Received[^1].Body);
            var shownId = await UiTestWaitForAsync(() => GetChildren().OfType<AcceptDialog>().FirstOrDefault(d => IsInstanceValid(d) && d.HasMeta("problem_result") && d.GetMeta("problem_result").AsString().Length > 0), "the sent report's id");
            string id = shownId.GetMeta("problem_result").AsString();
            UiTestExpect(id.Length == 8 && sent.Contains("\"event_id\":\"" + id) && sent.Contains("\"report_id\":\"" + id + "\"") && sent.Contains("\"filename\":\"atlas_log.txt\"")
                && !sent.Contains("\"filename\":\"system.txt\"") && sent.Contains("It froze when <user> met <name>") && !Mentions(sent),
                $"the posted report isn't the ticked parts with the note under id {id}: " + sent[..Math.Min(sent.Length, 2000)]);
            var zips = System.IO.Directory.GetFiles(reports.ReportsDir, "atlas-report-*.zip");
            UiTestExpect(zips.Length == zipsBefore + 1, $"{zips.Length - zipsBefore} copies of the sent report were kept");
            shownId.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            // Just save it: a zip in Atlas's folder, nothing posted.
            problem = OpenProblem();
            problem.Choose(AP_Atlas.UI.ProblemChoice.Save);
            var saved = await UiTestWaitForAsync(() => Result(""), "the saved report's dialog");
            zips = System.IO.Directory.GetFiles(reports.ReportsDir, "atlas-report-*.zip");
            UiTestExpect(zips.Length == zipsBefore + 2, $"Just save it wrote {zips.Length - zipsBefore - 1} zips");
            string newest = zips.OrderByDescending(System.IO.File.GetLastWriteTimeUtc).ThenByDescending(z => z, StringComparer.Ordinal).First();
            using (var zip = AP_Atlas.Core.SafeZip.Open(newest))
            {
                string log = zip.ReadText(zip.GetEntry("atlas_log.txt") ?? throw new InvalidOperationException("the saved report has no log"));
                string system = zip.ReadText(zip.GetEntry("system.txt") ?? throw new InvalidOperationException("the saved report has no system information"));
                UiTestExpect(log.Contains("UI test marker: <user> at <path>\\marker.txt") && !Mentions(log) && zip.GetEntry("README.txt") != null && system.Contains("Godot") && !Mentions(system),
                    "the saved report's log isn't scrubbed, or it lacks its parts");
            }
            UiTestExpect(site.Received.Count == posted + 1, "Just save it posted something");
            saved!.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            // Cancel: nothing posted or written.
            problem = OpenProblem();
            problem.Choose(AP_Atlas.UI.ProblemChoice.Cancel);
            await UiTestWaitAsync(0.3);
            UiTestExpect(site.Received.Count == posted + 1 && System.IO.Directory.GetFiles(reports.ReportsDir, "atlas-report-*.zip").Length == zipsBefore + 2, "Cancel posted or wrote something");
            // 5. A build without an address: nothing is offered (the file stays for Report a problem), and Send is greyed with the reason.
            TestSentryDsn = "";
            SetUpCrashReports();
            Plant();
            await _crashReports!.OfferAsync();
            await UiTestWaitAsync(0.3);
            UiTestExpect(!_crashReports.Available && CardWith("had a problem") == null, "a build without an address offered a report");
            problem = OpenProblem();
            UiTestExpect(!problem.CanSend && problem.WhyNotSend.Contains("no address"), $"without an address, Send isn't greyed with the reason: \"{problem.WhyNotSend}\"");
            problem.Choose(AP_Atlas.UI.ProblemChoice.Cancel);
        }
        finally
        {
            AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.CrashReports, null, false);
            TestSentryDsn = null;
            SetUpCrashReports();
            foreach (var open in GetChildren().OfType<AP_Atlas.UI.CrashReportDialog>().ToList()) open.QueueFree();
            AP_Atlas.UI.ProblemReportDialog.Showing?.QueueFree();
            foreach (var open in GetChildren().OfType<AcceptDialog>().Where(d => d.HasMeta("problem_result")).ToList()) open.QueueFree();
            foreach (string path in planted) System.IO.File.Delete(path);
            DeleteProfile(profile);
        }
    }

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
