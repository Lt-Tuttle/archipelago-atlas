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
  - `PoliteHttp` and `GitHubApi`.
  - `YamlExclusions`.
  - The Cheese Tracker client, models, table rules, advisor and key store (`CheeseTracker/`).
  - The Sphere Tracker parser, models and tables (`Spheres/`).
  - `EngineDownloader` (`Engine/`).
  - `Connections/`:
    - `SessionManager`: every connection to an Archipelago server (connecting one at a time, the login time limit, drops, careful reconnects, closing).
    - `AtlasSessions`: the only place sessions are made; each keeps the games' names in `DataPackageStore` (the data folder, below).
  - `Testing/FakeArchipelagoServer` (internal): a fake Archipelago server on the test computer, for the unit tests and the UI test. Atlas never starts it otherwise.
  - `Testing/FakeCheeseServer` (internal): a stand-in for the Cheese Tracker site on the test computer, for the self-test and the UI test. The real site is never contacted.
  - `Testing/FakeLogicEngine` (internal) and `fake_engine.py`: a fake logic engine in a stand-in Archipelago folder, answering the engine's requests from simple rules, for the unit tests and the UI test. Atlas never sets one up otherwise; the UI test runs it on a Python already on the computer (`AtlasEngine.TestPython`, which only the UI test sets).
- **`AP_Atlas.Core.Tests/`**: its xUnit tests.
- **`AP_Atlas_Source/`**: the Godot app, below. It starts the library's `Logger`: Godot's output for echoes, and the data folder's `logs/` once that folder has been checked.

## Folders (`AP_Atlas_Source/Scripts`)

| Folder | What's there |
|---|---|
| `MainTrackerWindow*.cs` | **The shell**, in partial files:<br>• `MainTrackerWindow.cs`: the fields, startup (`_Ready`), shutdown and the Properties host.<br>• `.Layout`: the panels, theme and fonts.<br>• `.Notices`: toasts, failure notices and the logs.<br>• `.Connections`: connecting, careful reconnects and open sessions.<br>• `.Profiles`: the multiworld editor.<br>• `.Sidebar`: the slot cards.<br>• `.Views`: the tabs, explorer, content stage and terminal.<br>• `.Windows`: the Pack Doctor, Cheese, Sphere, Privacy, engine and race-mode windows. |
| `Core/SlotTrackerControl*.cs` | **Everything about one connected slot**, in partial files:<br>• `SlotTrackerControl.cs`: startup, its session events and the queries Properties uses.<br>• `.Race`: race mode.<br>• `.Apworld`: matching the seed's apworld version.<br>• `.MapPack`: the pack, game names, its index and the pack's scripts.<br>• `.Logic`: the logic engine.<br>• `.LogicView`, `.History`, `.Chat`: its views. |
| `Core/` | **Infrastructure:**<br>• `DataManager` (settings and profiles), `CrashGuard`, `GodotLog` (Godot's own errors and warnings, into Atlas's log).<br>• `SlotModel`: one connected slot apart from its views: the session and its events, the text client's lines, hints and goal, its logic (`SlotLogic`), race mode and exclusions, logic as the slot shows it (race mode and exclusions applied), and the questions Atlas asks the server about it. It tells its views at most once per frame what changed. Services read it; views read the slot's panel, which forwards to it.<br>• `SlotLogic`: the slot's logic engine (started, restarted after a failure, paused while the engine is updated) and what's in logic, worked out item by item. Each engine run is numbered, so an answer or a failure from an engine since stopped is ignored.<br>• `Annotations` (notes, flags, special items, exclusions), `RaceRules`, `ThemeColors`, `Inspect`.<br>• `ExternalLinks`: the only way to open links and folders.<br>• `Permissions`: what the user allowed. |
| `Core/Engine/` | **The Atlas Engine:**<br>• `AtlasEngine`: setup of the portable engine (Python, Archipelago, Universal Tracker, packages), health checks, rollback, and the user's own install with their consent.<br>• `EngineInstall`, `ProcessJob` (engine processes close with Atlas).<br>• `ApworldSources`: apworld versions, matched to each seed.<br>• `SeedVerifier`, `GameSweep`.<br>• `Python/`: the bridge (`atlas_bridge.py`) and the portable runner. |
| `Core/PopTracker/` | **Map packs:**<br>• `PopTrackerPackLoader` reads pack zips.<br>• `PackScriptHost` runs pack Lua in a sandbox.<br>• `PackIndex`, `LuaMappingReader`, `GameNames`.<br>• The Pack Doctor (`PackDoctor`, `PackDoctorService`, `PackFixes`: local fixes with undo).<br>• Key Items (`ProgressionTrackerControl`). |
| `Core/CheeseTracker/` | **Cheese Tracker:** the service: rooms and linking, opt-in automation (the client and rules are in `AP_Atlas.Core`). |
| `Core/Spheres/` | **Sphere Tracker:** the service that reads the host's spheretracker.de room (the parser and tables are in `AP_Atlas.Core`). |
| `UI/` | **Windows and views:**<br>• Properties, Hints, Map Tracker, the Cheese and Sphere tabs.<br>• The Pack Doctor and Atlas Engine windows.<br>• Privacy, the permission dialog, shared dialogs, the tab strip.<br>• `TreeSubscriptions`: a view's event subscriptions, made while it's in the window (also after a move) and removed while it isn't.<br>• `Tool`: every tool (id, title, scope: the app, a multiworld or a slot), in tab order; a slot tool names the slot's view of it. |
| `Core/SelfTest*.cs` | **The self-test:** run with `Tools/run_selftest.ps1`. |
| `MainTrackerWindow.VisualCheck.cs` | **The visual check:** pictures of the main screens, compared with an earlier run; run with `Tools/run_visualcheck.ps1`. |
| `MainTrackerWindow.UiTest.cs` | **The UI test:** drives the window the way a user would, against the fake Archipelago server (connecting, a dropped connection, disconnecting), and runs a slot's logic on the fake logic engine (items and checks, a crash, an engine update, a restart); `Tools/run_selftest.ps1` runs it after the self-test. |

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
  - The logic engine's pipes change only under its request lock.
- **No failure goes unseen:** work nobody awaits (button handlers, background checks, closing connections) starts with `Async.Fire(task, "what it's doing")`. A failure is logged with that description and, unless the work is routine, shown to the user in plain words. There's no `async void` and no discarded task (`_ = …`): the guard rails reject both.

## How things talk to each other

- **Archipelago servers:** websockets through MultiClient.Net, compressed, all through `SessionManager`:
  - One connection at a time; a login has 10 seconds, and one that finishes later is closed.
  - A drop is noticed from the socket's close, or by a check twice a second (a server that died sends none).
  - Reconnects wait 15 s, 30 s, 1, 2, 5 and 10 minutes (±20%), then stop; a refusal stops them at once.
  - Closing Atlas sends every session a close frame. Session events arrive on network threads and move to the main thread with `Ui.Defer(owner, …)`, which skips the work if its owner (a slot, window or tab) was closed meanwhile, and logs a failure.
- **Web sites** (Cheese Tracker, spheretracker.de, GitHub, PyPI, python.org): only through `PoliteHttp`:
  - One request at a time per site, at least a second apart.
  - Backoff of 1, 2, 5, 10, then 30 minutes; `Retry-After` is honoured.
  - Size and time limits; no redirects (downloads follow a few https redirects).
  - GitHub calls go through `GitHubApi`, which honours its rate-limit headers and uses ETags.
  - **Atlas never requests an archipelago.gg room page**, because that wakes the room.
- **The logic engine:** a Python process per slot, speaking JSON lines over stdin/stdout. Engines run in a kill-on-close job object, so they never outlive Atlas.
- **Map pack scripts:** Lua in MoonSharp, sandboxed, on a per-slot script queue.

## Safety rules the code enforces

`Tools/check_guards.ps1` runs in CI and fails the build if any of these slip:

| Only allowed in | What |
|---|---|
| `ExternalLinks` | Opening anything through Windows: https pages and existing folders only; never a file. |
| `PoliteHttp` | Creating web clients. |
| `AtlasEngine` | Reading the Windows registry, and the install search. The search only runs after the user agrees, from the Engine window's Find button. |
| nowhere | `UseShellExecute = true`. |
| `AtlasSessions` | Creating an Archipelago session. |
| `SessionManager` | Connecting one. |
| `AtlasEngine`, `EngineInstall` (and the self-test) | Starting a program. |

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
- **Coalesced refreshes:** one refresh per frame, with hidden views skipped.
- **One engine process per multiworld** instead of per slot.

Done in Phase 1 so far: the Godot-free `AP_Atlas.Core` library with unit tests, `SessionManager`, `TreeSubscriptions`, the tool list, `SlotModel` with the slot's logic (services read it), the fake logic engine, and the UI test.
