# How The Archipelago Atlas is built

This is a map of the code as it is today, for anyone starting to work on Atlas. Planned restructuring is noted at the end.

## The big picture

- **Platform:** Atlas is a Godot 4.7.2 (.NET) desktop app written in C# 14 on .NET 10. It runs in low-processor mode (Godot draws only when something on screen changes) on Godot's Compatibility renderer: OpenGL 3.3, with Godot's built-in Direct3D fallback (ANGLE). That renderer was chosen by measurement: against Forward+ and Mobile it started fastest and used the least memory, and every screen looked the same.
- **UI:** built in code from Godot Controls; there are almost no scene files.
- **Archipelago connections:** one `ArchipelagoSession` per slot, from Archipelago.MultiClient.Net, always made by `AtlasSessions`.
- **Logic:** comes from the **Atlas Engine**, a Python process that runs Archipelago and the Universal Tracker.

```
MainTrackerWindow (the shell)
 ├─ SlotTrackerControl  × one per connected slot (its panel and views)
 │   ├─ SlotModel                      (the slot apart from its views)
 │   │   ├─ ArchipelagoSession         (MultiClient.Net: items, checks, hints, chat)
 │   │   └─ SlotLogic → LogicEngineManager → Python   (atlas_bridge.py: "what's in logic, and why")
 │   ├─ map pack context              (PopTracker pack + Lua scripts in MoonSharp)
 │   └─ views: Logic Tracker, Item History, Chat
 ├─ views: Map Tracker, Key Items, Hints, Cheese Tracker, Sphere Tracker, Map Packs, Connections
 ├─ PropertiesPanel                    (details and "why" for anything clicked)
 └─ services: CheeseTrackerService, SphereService (they read SlotModel), PackDoctorService
```

## Projects

- **`AP_Atlas.Core/`**: everything that doesn't need Godot. It has no Godot reference, so it can't touch the window from a worker thread, and its tests run with plain `dotnet test`.
  - `SafeFile`, `Logger`, `Async`, `AtlasVersion`, `Secrets`.
  - `SafeZip`, `ImageHeader` and `ImageBudget`: reading zips and images that come from outside Atlas (see "Files from outside Atlas", below).
  - `Bbcode`: escaping text from outside Atlas for rich text, and the allowlist every piece of markup passes (`Safe`), which also gives a long run of text breaks (a zero-width space every 64 characters) and takes linear time, whatever the text.
  - `BoundedLineReader`: reading a program's output a line at a time, with a limit on how much of a line is kept.
  - `LuaText` (stripping a pack script's comments in linear time) and `RegexDefaults` (every regular expression's time limit).
  - `Commands`: every action the user can ask for, by id, with the key bound to it (the defaults, with the user's rebinds on top) and one way to run it, wherever it's asked from. Keys are written one way ("Ctrl+Shift+P", `Normalize`).
  - `WordSearch`: finding things by typed words (every typed word starts a word of the thing's texts); the command palette and the Settings page use it.
  - `Markdown`: Atlas's own documents, from Markdown to the BBCode its rich text reads (Atlas's own tags only; links only to https pages); `Sections` splits a document at a heading level.
  - `Docs`: the documents built into Atlas (the guide, the changelog, the credits, the third-party notices, the licence), embedded from the repository's files as it's built, so there is one source.
  - `Deadline` (when a wait ends) and `SteadyClock` (when something happened in this session). Both measure with a monotonic clock, so setting the PC's clock (a time sync after a wrong start-up time) can't stretch a wait, end one early or lift a limit. The PC's clock is only for times that are saved, shown or sent; a saved time enters the steady clock once, as it loads (`SteadyClock.FromSaved`).
  - `PoliteHttp` and `GitHubApi`.
  - `YamlExclusions`.
  - The Cheese Tracker client, models, table rules, advisor and key store (`CheeseTracker/`).
  - The Sphere Tracker parser, models and tables (`Spheres/`).
  - `Engine/`:
    - `EngineDownloader`.
    - `EnginePool` and `EngineSeat`: a multiworld's logic engines. Up to `EnginePool.EnginesFor(processors)` processes (half the cores, 1 to 4); each slot gets its own until then, then joins the engine with the fewest. A slot's seat tags its requests with its key. An engine that crashes or doesn't answer in time (`stuckIfLate`; a "why" isn't) is stopped and only its slots lose it, each told whether its own request was the one being answered (`EngineLoss.Mine`, from `EngineProcess.Answering`). A slot that leaves has its world dropped; an engine whose last slot leaves is stopped.
    - `EngineProcess`: one logic engine process. One request at a time; only an answer carrying the request's id counts (a late answer, or a line the engine printed, never does); a reader of its own reads and parses its output off the main thread; a bridge that couldn't load ("boot_failed") or an engine that ended fails the waiting request at once; stopping it kills its whole process tree.
    - `ProcessJob`: engine processes close with Atlas, and setup can tell which run from an engine folder.
  - `Connections/`:
    - `SessionManager`: every connection to an Archipelago server (connecting one at a time, the login time limit, drops, careful reconnects, closing), and the room's text: one connection per multiworld team receives it and passes each line to every slot of the team (`SessionManager.Text.cs`).
    - `AtlasSessions`: the only place sessions are made; each keeps the games' names in `DataPackageStore` (the data folder, below), and gives its connection's thread back when it ends (`Finished`).
    - `NameLimits`: a name a server sends (an item, a location, a hint's entrance) is cut to 500 characters, keeping it apart from the rest (the start, "…" and a hash). The library makes its names from a game's data package and then hands the package to Atlas's store; the store's provider cuts the names and puts a lookup made from them in the library's place (`LibraryCache.ReplaceNames`), in the same call.
  - `Testing/FakeArchipelagoServer` (internal): a fake Archipelago server on the test computer, for the unit tests and the UI test. Atlas never starts it otherwise.
  - `Testing/FakeCheeseServer` (internal): a stand-in for the Cheese Tracker site on the test computer, for the self-test and the UI test. The real site is never contacted.
  - `Testing/FakeLogicEngine` (internal) and `fake_engine.py`: a fake logic engine in a stand-in Archipelago folder, answering the engine's requests from simple rules, for the unit tests and the UI test. Atlas never sets one up otherwise; the UI test runs it on a Python already on the computer (`AtlasEngine.TestPython`, which only the UI test sets).
- **`AP_Atlas.Core.Tests/`**: its xUnit tests.
- **`AP_Atlas_Source/`**: the Godot app, below. It starts the library's `Logger`: Godot's output for echoes, and the data folder's `logs/` once that folder has been checked.

## Folders (`AP_Atlas_Source/Scripts`)

| Folder | What's there |
|---|---|
| `MainTrackerWindow*.cs` | **The shell**, in partial files:<br>• `MainTrackerWindow.cs`: the fields, startup (`_Ready`), shutdown and the Properties host.<br>• `.Layout`: the panels, theme and fonts.<br>• `.WindowParts`: showing and hiding the window's parts (the View menu, remembered in the settings) and focus mode.<br>• `.SlotPicker`: the slot picker in the tool header (which connected slot the slot tools show) and Ctrl+Tab through the slots.<br>• `.Menu`: the menu bar and the keyboard: every plain action is a command (`AP_Atlas.Core.Commands`), run from its menu item or its key.<br>• `.Settings`: the Settings page's content: every setting in sections, each reading and writing its own place in the settings and applying itself at once; the keyboard section (every command's key, rebound by pressing a key, with conflicts and resets), whose rebinds go to `KeyBindings` in the settings.<br>• `.Home`: Home's hooks into the window: what it reads (the engine, the multiworlds, the packs, the live slots) and what its buttons do, with the one-click connect of a multiworld's every slot.<br>• `.Notices`: toasts, failure notices and the logs.<br>• `.Connections`: connecting, careful reconnects and open sessions.<br>• `.Profiles`: the multiworld editor.<br>• `.Sidebar`: the slot cards.<br>• `.Views`: the tabs, explorer, content stage and terminal.<br>• `.Windows`: the Pack Doctor, Cheese, Sphere, engine, race-mode and Help windows (Privacy is a section of the Settings page). |
| `Core/SlotTrackerControl*.cs` | **Everything about one connected slot**, in partial files:<br>• `SlotTrackerControl.cs`: startup, its session events and the queries Properties uses.<br>• `.Race`: race mode.<br>• `.Apworld`: matching the seed's apworld version.<br>• `.MapPack`: the pack, game names, its index and the pack's scripts.<br>• `.Logic`: the logic engine.<br>• `.LogicView`, `.History`, `.Chat`: its views. |
| `Core/` | **Infrastructure:**<br>• `DataManager` (settings and profiles), `CrashGuard`, `GodotLog` (Godot's own errors and warnings, into Atlas's log).<br>• `SlotModel`: one connected slot apart from its views: the session and its events, the text client's lines, hints and goal, its logic (`SlotLogic`), race mode and exclusions, logic as the slot shows it (race mode and exclusions applied), and the questions Atlas asks the server about it. It tells its views at most once per frame what changed. Services read it; views read the slot's panel, which forwards to it.<br>• `SlotLogic`: the slot's logic engine (started, restarted after a failure, paused while the engine is updated) and what's in logic, worked out item by item (a few items per request). Each engine run is numbered, so an answer or a failure from an engine since stopped is ignored.<br>• `Annotations` (notes, flags, special items, exclusions), `RaceRules`, `ThemeColors`, `Inspect`.<br>• `ExternalLinks`: the only way to open links and folders.<br>• `Permissions`: what the user allowed. |
| `Core/Engine/` | **The Atlas Engine:**<br>• `AtlasEngine`: setup of the portable engine (Python, Archipelago, Universal Tracker, packages), health checks, rollback, and the user's own install with their consent.<br>• `EngineInstall`: where an engine lives and how to start its components.<br>• `ApworldSources`: apworld versions, matched to each seed.<br>• `SeedVerifier`, `GameSweep`.<br>• `Python/`: the bridge (`atlas_bridge.py`) and the portable runner. |
| `Core/PopTracker/` | **Map packs:**<br>• `PopTrackerPackLoader` reads pack zips (through `SafeZip`): their structure and an index of their images.<br>• `PackImages`: a pack's images, decoded only while a slot or the Pack Doctor window uses the pack (the pack used last keeps them; others are freed at once), each one's size checked from its header first (`DecodeImage`). The Pack Doctor learns which images decode, and their sizes, without keeping them.<br>• `PackScriptHost` runs pack Lua in a sandbox.<br>• `PackIndex`, `LuaMappingReader`, `GameNames`.<br>• The Pack Doctor (`PackDoctor`, `PackDoctorService`, `PackFixes`: local fixes with undo).<br>• Key Items (`ProgressionTrackerControl`). |
| `Core/CheeseTracker/` | **Cheese Tracker:** the service: rooms and linking, opt-in automation (the client and rules are in `AP_Atlas.Core`). |
| `Core/Spheres/` | **Sphere Tracker:** the service that reads the host's spheretracker.de room (the parser and tables are in `AP_Atlas.Core`). |
| `UI/` | **Windows and views:**<br>• Properties, Hints, Map Tracker, the Cheese and Sphere tabs.<br>• The Pack Doctor and Atlas Engine windows.<br>• The permission dialog (Allow once / Always allow / Don't allow) and `PrivacyPanel`, the Settings page's Privacy &amp; permissions section: every permission with its state, each kept answer and trusted source with a way back, and where Atlas goes online; shared dialogs.<br>• `TreeSubscriptions`: a view's event subscriptions, made while it's in the window (also after a move) and removed while it isn't.<br>• `ViewRefresh`: a view's refresh that runs only while the view shows, at most once a frame; asked for while hidden, it runs when the view shows. Every slot view, the hints, the map's colors and the Sphere tab use it.<br>• `LogPane`: writes to the System Log and Debug Log from any thread, once per frame, keeping their last lines.<br>• `Tool`: every tool (id, title, scope: the app, a multiworld or a slot; its group on the activity bar and its icon), in the bar's order (Ctrl+1 to Ctrl+9; a later tool brings its own key, Settings: Ctrl+,); a slot tool names the slot's view of it.<br>• `ActivityBar`: the column of icons on the far left, one button per tool in three captioned groups and one for the engine window; the lit button follows the tool the window shows.<br>• `LucideIcons`: the Lucide icons Atlas ships, as SVG text, coloured as they're loaded.<br>• `CommandKeys`: keys as Godot reports them and as commands name them, both ways.<br>• `CommandPalette`: Ctrl+Shift+P, the same commands as the menus, narrowed by what's typed; it frees itself when it closes.<br>• `SettingsPage`: the Settings page's frame: sections, rows of each kind (toggle, choice, number, action, key), the search over their words, the section list for the explorer, and a jump to a section.<br>• `HomePage`: where Atlas opens: the getting-started checklist, the multiworlds, the tool and feature cards, the tip, the links and the footer; everything on it ships with Atlas.<br>• `LucideTextures`: Lucide icons as coloured textures, kept once each.<br>• `HelpWindow`: Help: the guide's sections, what's new, the credits and disclaimer and the licences as topics, rendered from the documents built in; one window at a time.<br>• `KeyCapture`: a button showing a command's key that, pressed, takes the next key press as the key (Escape keeps it, Backspace means none; a modifier alone isn't a key). |
| `Core/SelfTest*.cs` | **The self-test:** run with `Tools/run_selftest.ps1`. |
| `MainTrackerWindow.VisualCheck.cs` | **The visual check:** pictures of the main screens, compared with an earlier run; run with `Tools/run_visualcheck.ps1`. |
| `MainTrackerWindow.UiTest.cs` | **The UI test:** drives the window the way a user would, against the fake Archipelago server (connecting, a dropped connection, disconnecting), and runs a slot's logic on the fake logic engine (items and checks, a crash, an engine update, a restart). It also checks, with weak references and forced collections, that a slot that ends, and a window that closes (the Pack Doctor, the engine window, Privacy, the race mode dialog), leave nothing in memory, and that closed windows leave Godot's node count where it was. `Tools/run_selftest.ps1` runs it after the self-test. |

## Where data lives

Everything is in one **data folder**:
- **In a build:** `PortableData` next to Atlas's program file.
- **When run from source:** `AP_Atlas_Source/PortableData`.
- **Override:** `ATLAS_DATA_DIR`. The self-test always points it at an empty scratch folder.

Atlas reads or writes outside this folder only with the user's permission. Nothing else goes outside it either:
- **The connection library** would keep the games' names in `%LocalAppData%\Archipelago\Cache`. It has no setting for that, so `AtlasSessions` gives each session Atlas's `DataPackageStore` first: the library makes its own cache only if a session has none when the server's room info arrives. `AtlasSessions` checks the library's internals are exactly as expected and refuses to connect if they aren't, and a unit test reads the whole library for any other way it could reach the disk, the registry or other programs.
- **Godot** keeps its log and shader cache in `%APPDATA%`; both are off in `project.godot`, and `GodotLog` copies Godot's errors and warnings into Atlas's log. Godot still creates an empty `%APPDATA%\Godot\app_userdata\The Archipelago Atlas` folder when it starts; it has no setting to stop that.
- **Engine processes** start through `EngineInstall.StartInfo` or `AtlasEngine.SetupStartInfo`, which point their temp folder, `%LocalAppData%`, `%AppData%` and pip's cache into `engine/`, and stop pip from reading the user's own settings. The portable runner (`atlas_run.py`) also pins Archipelago's cache and Python's temp folder there.
- **The footprint check:** `run_selftest.ps1` and `run_visualcheck.ps1` give Atlas empty stand-ins for `APPDATA`, `LOCALAPPDATA`, `TEMP` and `TMP`, and fail if any file appears in them.

| Path | Holds |
|---|---|
| `settings.json` | App settings, including what the user allowed (`PermissionsAllowed`) and the encrypted Cheese Tracker key. |
| `profiles.json` | Multiworlds and their slots. Room passwords are DPAPI-encrypted (`PasswordProtected`). |
| `annotations.json` | Notes, flags, special items, exclusions. |
| `slot_data/`, `name_cache/` | What servers sent, kept for offline use. |
| `datapackage_cache/` | Each game's names as servers sent them, one file per game version (`DataPackageStore`). Versions unused for three months are removed. |
| `packs/` | Installed map pack zips. |
| `pack_fixes/` | Pack Doctor fixes. |
| `cheese/`, `spheres/` | Cheese Tracker and Sphere Tracker caches. |
| `engine/` | The portable engine:<br>• `python/`, `archipelago/`, `downloads/`, `backups/`.<br>• `apworld_cache/` (apworld versions for seeds).<br>• `engine.json`.<br>• `install_changes.json` (what Atlas changed in the user's own Archipelago install, for undo).<br>• `temp/`, `user/` and `pip_cache/`: engine processes' temporary files, their stand-ins for `%LocalAppData%` and `%AppData%` (Archipelago's cache), and pip's downloads. |
| `logs/` | `atlas_log.txt` (rotated) and crash reports. |

- **Every save goes through `SafeFile`:** an atomic write, the previous version kept as `.bak`, and automatic recovery from damage. A damaged file is set aside as `.corrupt-<time>`.
  - A file another program holds (an antivirus or sync tool) is read again for about a second and a half. It's never mistaken for damage; if it stays held, Atlas uses the fallback and refuses to save over it until it reads again.
  - `SafeFile.Delete` removes the backup and any temp file too, so a deleted file can't come back from its backup.
  - Settings that change in bursts (splitters, the map camera) are saved with `DataManager.SaveSettingsSoon`: once, half a second after the last change. Closing Atlas writes a pending save, and saves the profiles too.
  - A failed save of the user's own data (settings, profiles, notes and flags, Pack Doctor fixes) is shown to them (`DataManager.SaveFailed`); caches Atlas can rebuild are only logged.
- **Threads:** the window, settings, profiles, PackFixes and GameNames belong to the main thread.
  - Background work hands results back with `Ui.Defer`.
  - A save of settings or profiles that arrives from another thread moves to the main thread and is logged as a mistake.
  - The Pack Doctor reads a snapshot (`PackDoctor.Prepare`), then analyses it on a worker thread.
  - A logic engine's output is read by its own reader (`EngineProcess`), off the main thread.
  - A slot's pack scripts run on a background queue (`PackScriptRunner`), one piece of work at a time; the queue keeps the scripts it started, so items fed while they start reach them. Reading which options the seed settings reflect (a scan of all the pack's scripts) happens there too, when they start.
- **No failure goes unseen:** work nobody awaits (button handlers, background checks, closing connections) starts with `Async.Fire(task, "what it's doing")`. A failure is logged with that description and, unless the work is routine, shown to the user in plain words. There's no `async void` and no discarded task (`_ = …`): the guard rails reject both.

## How things talk to each other

- **Archipelago servers:** websockets through MultiClient.Net, compressed, all through `SessionManager`:
  - One connection at a time; a login has 10 seconds, and one that finishes later is closed (refused or not). A connection Atlas gave up on or closed while it was still opening is closed as it opens, without logging in: the connection library can't close a connection that isn't open yet. A login that fails because Atlas let it go is "cancelled" (nothing to report, nothing to try again).
  - **Threads:** the library's send loop blocks a thread pool thread for as long as its connection is open, and closing the connection doesn't wake it. `AtlasSessions` raises the pool's minimum by one per open connection, and `SessionManager` calls `AtlasSessions.Finished` whenever a session ends (closed, dropped, found dead, or a late login): one more queued packet wakes the loop, which finds its connection closed and ends without sending it. Its "socket closed" error isn't passed on.
  - A drop is noticed from the socket's close, or by a check twice a second (a server that died sends none).
  - Reconnects wait 15 s, 30 s, 1, 2, 5 and 10 minutes (±20%), then stop; a refusal stops them at once.
  - Closing Atlas sends every session a close frame. Session events arrive on network threads and move to the main thread with `Ui.Defer(owner, …)`, which skips the work if its owner (a slot, window or tab) was closed meanwhile, and logs a failure.
  - **The room's text:** per multiworld team, one connection (its text connection) receives it; the others log in with NoText, so the server sends them none.
    - The text connection's room lines (items found, chat, joins, goals) go to every slot of its team, in order. A line meant for one connection (a command's answer, a hint's line, the tutorial) stays with that connection's slot.
    - A command switches its slot's connection to text first, so the answer arrives. A slot without text shows its new hints from its hint list (`SlotModel`).
    - When the text connection ends, another of the team's connections takes over, preferring one that receives text already: the server tells the whole room about every tag change.
    - A slot logging in without text shows the room's lines from its own join line on, as its own connection would with text: the server sends lines in order, so the text connection's lines before that one were the room's before the slot joined.
      - The join line may reach Atlas while the slot logs in (the lines from it on are replayed) or just after (the room's lines are held until it passes).
      - It waits only through a text connection the server had confirmed before its login began: one that logged in with text, or one a line has come through since. Through a connection switched on meanwhile, the join line may never come (the server may have let the slot in first), so the slot shows the lines from while it logged in.
      - If the join line doesn't come within 10 seconds (a server that doesn't announce joins), or the text connection changes hands meanwhile, the held lines are shown with the next one: when Atlas can't tell, it shows rather than drops.
    - A line may reach a slot through another slot's connection, so its "active player" flags are that connection's. Who "you" are is decided by slot and team (`SlotModel.IsThisSlot`).
- **Web sites** (Cheese Tracker, spheretracker.de, GitHub, PyPI, python.org): only through `PoliteHttp`:
  - One request at a time per site, at least a second apart.
  - Backoff of 1, 2, 5, 10, then 30 minutes; `Retry-After` is honoured.
  - Waits are `Deadline`s. A time the site names ("try again at", GitHub's rate-limit reset) is measured from the time it sent with its answer (its `Date` header), so a PC clock that's wrong can't shorten it.
  - Size and time limits; no redirects (downloads follow a few https redirects).
  - GitHub calls go through `GitHubApi`, which honours its rate-limit headers and uses ETags.
  - **Atlas never requests an archipelago.gg room page**, because that wakes the room.
- **The logic engine:** a few Python processes per multiworld, shared by its slots (`EnginePools`, `EnginePool`), speaking JSON lines over stdin and stdout (`EngineProcess`). Engines run in a kill-on-close job object, so they never outlive Atlas. Deleting a multiworld forgets its pools (`EnginePools.Forget`).
  - A request names its slot with `key`; the bridge keeps each slot's world apart (`slots[key]`), and `drop` frees one. Slots of one multiworld can share an engine because they share the seed's apworld versions (a seed's apworld is swapped in per game, process-wide).
  - The bridge empties the Universal Tracker's class-level world cache (`cached_multiworlds`) before each start and after a drop: it's shared by every slot in the process and would otherwise keep every world.
  - `SlotLogic` restarts a slot whose engine was lost; only a loss its own request caused counts toward pausing its logic.
  - Logic is asked about a slot's new progression items with `steps`: the items worked out already (`base`), up to 10 new ones in the order they arrived, and the first time `start`. The bridge answers what each opened (the locations in logic after it that weren't before it, each worked out as `update` would), and the final state's excluded and glitched locations and goal. It keeps each slot's last answer, so the base isn't worked out again.
  - Each request carries an id, and each answer echoes it.
  - Standard output is Atlas's channel: answers only. The runner (`atlas_run.py`) and the bridge send everything else that prints (worlds as they load, the tracker) to standard error, which Atlas logs; components write answers with `send()`.
  - A bridge that can't load says so with `{"event": "boot_failed"}`.
  - The bridge's request loop dispatches to `handle_init`, `handle_update` and `handle_explain`, with a slot's state in a `Slot`. What it can't do but carries on without, it notes on standard error.
- **Map pack scripts:** Lua in MoonSharp, sandboxed, on a per-slot script queue (`PackScriptRunner`).
  - Each piece of work (init.lua, the clear handlers, one item or check, one read) runs under `ScriptLimits`: 50 million steps, 1 GB allocated and 10 s, and recursion is stopped while the thread still has stack to spare. The 40-pack corpus check (`ATLAS_SELFTEST_PACKS`) runs every pack as a slot does and reports each pack's busiest piece of work; the most is 1.5 million steps.
  - A `Watchdog`, attached as MoonSharp's debugger, sees every step wherever it runs: in pcall, a coroutine, a sort's comparison, or Lua that one of Atlas's functions calls. MoonSharp's own limit (a coroutine's `AutoYieldCounter`) misses Lua run from callbacks, and each coroutine allocates 2 MB.
  - A stop is an exception Lua can't catch. It's sticky, because `table.sort` drops errors that aren't Lua's: the next step throws it again, and the end of the work checks it. It stops the scripts for good, and `StopReason` says why. Lua errors are only recorded, and the scripts carry on.
  - The pack's scripts are compiled before the watchdog is attached: with a debugger attached, every compile writes out all the code loaded so far (5 s for a pack of 755 files). `require` and `ScriptHost:LoadScript` use the compiled chunks.
  - Atlas's functions for the scripts are made with `Callback` (a failure is a Lua error at the script's call), and Atlas calls Lua through `CallLua` (a failure that isn't a Lua error means the interpreter broke, and stops the scripts).
  - MoonSharp's library throws .NET exceptions for some arguments where Lua raises an error (`math.random(1e20)`, `string.format("%c", -1)`, `os.date` of a time out of range). Each library function is wrapped so its own failures are Lua errors; a failure from Lua it ran in turn (a sort's comparison) is left alone.
  - `collectgarbage`, `string.rep` and `table.concat` are checked versions; the `json` and `dynamic` modules aren't loaded.
  - Compiling is guarded too. MoonSharp's compiler recurses about once per level of nesting (up to 1.7 KB of stack each), and code nested a few thousand levels deep would overflow it before any script runs. Every compile goes through `Compile`, whether it's the pack's files or code a script loads (`load`, `loadsafe`, `loadfile`, `loadfilesafe`, `dofile`, `require`, `LoadScript`):
    - `LuaNesting` (in Core, unit-tested) counts how deeply the source nests from its tokens, without compiling it. Code deeper than 1,000 is refused; Lua itself stops at 200, and the corpus's deepest script is 59.
    - The compiler runs on a thread of its own with a 16 MB stack (`OnCompilerThread`), about ten times what 1,000 levels need, whatever thread the compile started on.
    - Binary chunks (`string.dump`) aren't loaded, and a script can't load more than a million characters of code at once.

## Files from outside Atlas

Map packs and apworlds are zips from outside Atlas, and both a zip's headers and an image's can lie. A "zip bomb" of a few kilobytes can unpack to gigabytes, and an image file of a few bytes can claim billions of pixels, which a decoder sets aside memory for before it reads them.
- **Zips** are opened and read only through `SafeZip` (Core, unit-tested):
  - A zip64 zip is refused before .NET lists its files. Only zip64 can list more than 65,534 files, and .NET lists as many as a zip64 record says (about 600 bytes each). The end record is found as .NET finds it: the last one in the final 64 KB.
  - Each file's bytes are counted as it unpacks: no more than 64 MB of text or 256 MB for an image, and no more than 128 MB of text or 1 GB of images from one zip. .NET stops a compressed file at the size its headers give, but reads a stored one to the end of its bytes, so the count is what holds.
  - A refusal is an `InvalidDataException`, as .NET's own for a damaged zip. The pack loader notes the file in the pack's load issues and carries on without it; the script host stops the pack's scripts before they start (`StopReason`), so the refusal shows wherever a stop does.
  - Only the engine's setup unpacks a zip into a folder, and only its own downloads, checked against pinned SHA-256 hashes first.
- **Images** (a pack's, or one the user chooses for a Pack Doctor fix) are decoded only by `PackImages.DecodeImage`:
  - `ImageHeader` reads the size from the header without decoding: PNG's IHDR, WebP's VP8X, VP8L or VP8 header, and JPEG's first frame. JPEG markers are found as decoders find them (stray bytes and `FF 00` skipped), so a file can't show the check a small frame while the decoder finds a huge one hidden in between.
  - `ImageBudget` refuses an image over 16,384 a side, or one that would take the pack's images past 2 GB (one budget per decoding of a pack). A file whose size can't be read isn't decoded either.
  - The real corpus (43 packs, 6,750 images) has a longest side of 12,560, at most 821 MB of images in a pack, 3.7 MB of text and 210 MB of image files; the corpus check (`ATLAS_SELFTEST_PACKS`) would fail if any were refused.

**Text from outside Atlas** (a pack's names, a server's or another player's messages, a site's data, a zip's file names) is shown as written, never read as markup:
- Godot's rich text reads the paths in `[img]`, `[font]` and `[dropcap]` tags with its resource loader, which opens files beside the path first (`path.remap`). A network path there would make Windows connect to the computer it names, offering it the user's Windows sign-in.
- **Only `SafeRichText` (UI) reads BBCode.** Its markup passes `Bbcode.Safe`, an allowlist: Atlas's own tags (`b`, `i`, `u`, `s`, `code`, `color`, `bgcolor`, `url`, `lb`, `rb`) work, and any other `[` becomes `[lb]` and shows as text. That covers file tags, sizes, tables, and tags a later Godot adds. Its `Text` is hidden by a nested type, so `label.Text = …` doesn't compile; markup goes in through `Markup` or `Append`.
- **Outside text is escaped where it's put into markup** (`Bbcode.Escape`, or helpers that do: `Bbcode.Colored`, Properties' `Esc`, `Link` and `Colored`, the Cheese Tracker tab's `Colored`, the text client's `EscapeBBCode`). Escaping is what keeps outside text from adding Atlas's own tags, such as a fake link.
- **Log messages are plain text.** `Logger` escapes them for the window, and a line's colour is an argument (`LogInfo(message, color)`), never markup in the message.
- **Tests:**
  - The self-test plants `probe.png.remap` and `probe.ttf.remap`. It shows Godot reads them through rich text that isn't `SafeRichText`, so the probe works, and never through `SafeRichText` or the log.
  - The UI test sends a chat line, a map pack's details and log lines naming the probe, with tags of Atlas's own, through the real window.

**No line can be too long.** .NET's own line readers (`ReadLine`, `BeginOutputReadLine`) hold a whole line however long it is, so one endless line would fill Atlas's memory, and running out on the thread reading it ends Atlas.
- `BoundedLineReader` keeps a line up to a limit and reads the rest only to drop it, then says how much it cut. It reads the engine's answers, the engine's log, the engine setup's programs and the game-names reader:
  - **Answers** are kept up to `AnswerLimit` (64 Mi characters). Every installed game's names come to about 10 million. A longer answer line means the engine is broken: `EngineProcess` ends it as a crash, so its pool starts its slots again.
  - **A line of a program's log** is kept up to `LogLimit` (16 Ki characters).
- `Logger` cuts a message at `MaxMessageLength` (64 Ki), and the text client cuts a line at `MaxChatLine` (32,000; a server's longest real line, `!players` in a 1,000-player room, is about 19,000).
- The fake engine can write an endless line on either stream (its `flood` rule) for the tests.

**No pattern runs away.** A regular expression that backtracks can take hours on one crafted input.
- Patterns over large outside text run in linear time: an apworld's source (`GameOfApworld`, `RegexOptions.NonBacktracking`) and a pack's scripts' comments (`LuaText`, a scanner, because the pattern needs a backreference, which the non-backtracking engine lacks).
- Every regular expression has a 2-second limit besides.

Text from outside can be any length, and a long paragraph was the slowest thing Atlas could be given: a megabyte-long item name froze the window for five minutes. A long paragraph of rich text wraps at spaces, with breaks in long runs (`SafeRichText`, `Bbcode.Safe`); a server's names are cut at 500 characters (`NameLimits`); the text client shows a line up to 4,000 characters and lays out about that much new text per frame; the logs show a message up to 4,000 (`Logger.Shown`; the file keeps 64,000). The UI test's long-text scenario sends megabytes of each and checks no frame holds more than 150 ms of work. `RegexDefaults` sets it as `AP_Atlas.Core` loads (a module initializer), before any code makes one; .NET reads the default once, and libraries' patterns get it too.
- The self-test checks the limit is in effect in Atlas, and that crafted inputs take well under a second, which falling back to the limit couldn't.

## Safety rules the code enforces

`Tools/check_guards.ps1` runs in CI and fails the build if any of these slip:

| Only allowed in | What |
|---|---|
| `ExternalLinks` | Opening anything through Windows: https pages and existing folders only; never a file. |
| `PoliteHttp` | Creating web clients. |
| `AtlasEngine` | Reading the Windows registry, and the install search. The search only runs after the user agrees, from the Engine window's Find button. |
| nowhere | `UseShellExecute = true`. |
| `AtlasSessions` | Creating an Archipelago session. |
| `SessionManager` | Connecting one. Sending chat or commands, and changing a connection's tags. |
| `AtlasEngine`, `EngineInstall` (and the self-test) | Starting a program. |
| `PackScriptHost` | Running Lua (MoonSharp): each piece of a pack's scripts' work runs under its limits. Inside it, one place calls Lua (`CallLua`), one compiles it, and three helpers make the functions scripts call (`Callback`, `Checked`, `AsLuaErrors`). |
| `SessionManager` | Closing a server connection (one place, `DisconnectAsync`), and giving back its thread. |
| the self-test | Forcing a garbage collection (it pauses all of Atlas). |
| `AtlasEngine` | Looking up the user's own folders, for the install search the user agreed to. |
| a reviewed list | Saving a whole file without `SafeFile`: the log, a crash report, an export the user chose, files the engine setup regenerates, a store that writes a temporary file and moves it, and the tests. |
| `SafeZip` | Reading a zip (opening one to read, or unpacking one of its files). Making a zip is allowed anywhere. |
| `AtlasEngine` | Unpacking a zip into a folder: only the engine's hash-checked downloads. |
| `PackImages.DecodeImage` (and the visual check, for its own screenshots) | Decoding an image: its size is checked against an `ImageBudget` first. |
| `SafeRichText` (and the self-test's check that its probe works) | Rich text that reads BBCode: only Atlas's own tags get through. A paragraph over 300 characters wraps at spaces: Godot's "word smart" wrapping takes time that grows with the square of a paragraph's length. |
| nowhere | Markup in a log message: messages are plain text, and a line's colour is an argument. |
| `BoundedLineReader` | Reading a program's output: a line is kept only up to a limit. |
| `RegexDefaults` | Setting regular expressions' time limit; nowhere may opt out (`InfiniteMatchTimeout`). |
| `PackScriptHost` (its compiler, which needs a big stack) and the self-test | Starting a thread of its own. |
| nowhere | An empty catch that doesn't say why on its line. |
| a line that says why (`// wall clock: …`), and the tests | Timing by the PC's clock. In memory, a wait is a `Deadline` and a moment `SteadyClock.UtcNow`; the wall clock is for times that are saved, shown or sent. |

The build treats every warning as an error, and the guard rails fail on a stray control character, or an invisible one (a zero-width space, a direction override), in any file in the repository. MoonSharp and Archipelago.MultiClient.Net are pinned at the versions whose insides Atlas was checked against. The number of classes without nullable checks can only go down. A pre-push hook (`.githooks/pre-push`) runs the guard rails, the build, formatting and the unit tests before anything reaches CI.

Downloads that become code are pinned:
- The engine's Python, pip, Archipelago and Universal Tracker are each checked against a fixed SHA-256.
- Archipelago's Python packages install from `Scripts/Core/Engine/Data/engine_packages.lock.txt` with `--require-hashes` (rebuild it with `Tools/build_engine_lock.py`).
- Apworlds download only from sources the user trusts.

## Building, testing, releasing

- **Building and testing:** see [CONTRIBUTING.md](../CONTRIBUTING.md).
- **CI** (`.github/workflows/ci.yml`): guard rails → build → format check → unit tests → hash-checked Godot → import → self-test and UI test, with the footprint check.
- **Releases** (`.github/workflows/release.yml`): run when a `v*` tag is pushed. The version is set once, in `Directory.Build.props`.

## Planned restructuring (roadmap Phase 1)

These are known structural debts, scheduled before the new shell is built:
- **Large classes:** `MainTrackerWindow` and `SlotTrackerControl` are split into partial files by job, and connections moved into `SessionManager`. Slot state has moved out of the UI node into `SlotModel` (the session's events, chat, hints, goal, logic, race mode and exclusions), and the services read the model. The map pack and its scripts stay with the views that show them. The window lists its slots itself (`_slots`), not by where their panels are.
- **Re-parenting:** the Cheese and Sphere tabs and Properties follow their events through `TreeSubscriptions` (made on entering the tree, removed on leaving it), so they can be moved (docking, pop-outs); the UI test proves it. A slot's views are pushed their updates by the slot, so they can move too, and the slot's own panel ends only through `EndSlot()` (the slot replaced or deleted, or the panel freed with the window), never by leaving the tree.
- **Tools** are one list (`Tool`); the new shell will build its activity bar, menus and panels from it.
- **Coalesced refreshes:** done. One refresh per frame, hidden views skipped (`ViewRefresh`), and the text client draws a slice of lines per frame. The UI test's scale scenario measures a 1,000-player room with 20 slots connected; with one text connection per multiworld, its worst frame during bursts is about the 100 ms target (80 to 150 ms on the development PC and on CI), and no frame holds more than about 50 ms of Atlas's own work here; garbage collection pauses are the rest. Since step 6c, logic asks the engine a few times per burst rather than once per item (116 requests in that room instead of about 1,200). What's left is mostly garbage collection while a burst's item lines and data arrive: about 45 MB allocated between two frames in that room, part of it the fake server's own work.

Done in Phase 1 so far: the Godot-free `AP_Atlas.Core` library with unit tests, `SessionManager`, `TreeSubscriptions`, the tool list, `SlotModel` with the slot's logic (services read it), the fake logic engine, the UI test, one text connection per multiworld, and a few shared logic engines per multiworld.
