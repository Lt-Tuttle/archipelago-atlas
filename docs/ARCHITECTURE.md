# How The Archipelago Atlas is built

This is a map of the code as it is today, for anyone starting to work on Atlas. Planned restructuring is noted at the end.

## The big picture

- **Platform:** Atlas is a Godot 4.7.2 (.NET) desktop app written in C# 14 on .NET 10. It runs in low-processor mode (Godot draws only when something on screen changes) on Godot's Compatibility renderer: OpenGL 3.3, with Godot's built-in Direct3D fallback (ANGLE). That renderer was chosen by measurement: against Forward+ and Mobile it started fastest and used the least memory, and every screen looked the same.
- **UI:** built in code from Godot Controls; there are almost no scene files.
- **Archipelago connections:** one `ArchipelagoSession` per slot, from Archipelago.MultiClient.Net.
- **Logic:** comes from the **Atlas Engine**, a Python process that runs Archipelago and the Universal Tracker.

```
MainTrackerWindow (the shell)
 ├─ SlotTrackerControl  × one per connected slot
 │   ├─ ArchipelagoSession            (MultiClient.Net: items, checks, hints, chat)
 │   ├─ LogicEngineManager → Python    (atlas_bridge.py: "what's in logic, and why")
 │   ├─ map pack context              (PopTracker pack + Lua scripts in MoonSharp)
 │   └─ views: Logic Tracker, Item History, Chat
 ├─ views: Map Tracker, Key Items, Hints, Cheese Tracker, Sphere Tracker, Map Packs, Connections
 ├─ PropertiesPanel                    (details and "why" for anything clicked)
 └─ services: CheeseTrackerService, SphereService, PackDoctorService
```

## Folders (`AP_Atlas_Source/Scripts`)

| Folder | What's there |
|---|---|
| `MainTrackerWindow.cs` | **The shell:**<br>• Menus, the tab strip, the slot cards, the explorer, the terminal (chat and logs), toasts, theme and fonts.<br>• The Connections editor, connecting and reconnecting.<br>• The race-mode UI. |
| `Core/SlotTrackerControl.cs` | **Everything about one connected slot:**<br>• Its session events.<br>• Its logic engine, map pack and Lua script queue.<br>• The Logic Tracker, Item History and Chat views.<br>• Queries for Properties. |
| `Core/` | **Infrastructure:**<br>• `DataManager` (settings and profiles), `SafeFile` (crash-safe saves), `Logger`, `CrashGuard`, `Async` (work nobody awaits).<br>• `Annotations` (notes, flags, special items, exclusions), `RaceRules`, `ThemeColors`, `Inspect`.<br>• `YamlExclusions`, `AtlasVersion`. |
| `Core/` (safety) | **Protections:**<br>• `PoliteHttp`: every web request.<br>• `GitHubApi`: GitHub's rules on top of it.<br>• `ExternalLinks`: the only way to open links and folders.<br>• `Permissions`: what the user allowed.<br>• `Secrets`: DPAPI encryption. |
| `Core/Engine/` | **The Atlas Engine:**<br>• `AtlasEngine`: setup of the portable engine (Python, Archipelago, Universal Tracker, packages), health checks, rollback, and the user's own install with their consent.<br>• `EngineInstall`, `EngineDownloader`, `ProcessJob` (engine processes close with Atlas).<br>• `ApworldSources`: apworld versions, matched to each seed.<br>• `SeedVerifier`, `GameSweep`.<br>• `Python/`: the bridge (`atlas_bridge.py`) and the portable runner. |
| `Core/PopTracker/` | **Map packs:**<br>• `PopTrackerPackLoader` reads pack zips.<br>• `PackScriptHost` runs pack Lua in a sandbox.<br>• `PackIndex`, `LuaMappingReader`, `GameNames`.<br>• The Pack Doctor (`PackDoctor`, `PackDoctorService`, `PackFixes`: local fixes with undo).<br>• Key Items (`ProgressionTrackerControl`). |
| `Core/CheeseTracker/` | **Cheese Tracker:** the API client, rooms and linking, status suggestions (`CheeseAdvisor`), opt-in automation, the table rules, the encrypted key. |
| `Core/Spheres/` | **Sphere Tracker:** reads the host's spheretracker.de room (parser, tables, service). |
| `UI/` | **Windows and views:**<br>• Properties, Hints, Map Tracker, the Cheese and Sphere tabs.<br>• The Pack Doctor and Atlas Engine windows.<br>• Privacy, the permission dialog, shared dialogs, the tab strip. |
| `Core/SelfTest*.cs` | **The self-test:** run with `Tools/run_selftest.ps1`. |
| `MainTrackerWindow.VisualCheck.cs` | **The visual check:** pictures of the main screens, compared with an earlier run; run with `Tools/run_visualcheck.ps1`. |

## Where data lives

Everything is in one **data folder**:
- **In a build:** `PortableData` next to Atlas's program file.
- **When run from source:** `AP_Atlas_Source/PortableData`.
- **Override:** `ATLAS_DATA_DIR`. The self-test always points it at an empty scratch folder.

Atlas reads or writes outside this folder only with the user's permission.

| Path | Holds |
|---|---|
| `settings.json` | App settings, including what the user allowed (`PermissionsAllowed`) and the encrypted Cheese Tracker key. |
| `profiles.json` | Multiworlds and their slots. Room passwords are DPAPI-encrypted (`PasswordProtected`). |
| `annotations.json` | Notes, flags, special items, exclusions. |
| `slot_data/`, `name_cache/` | What servers sent, kept for offline use. |
| `packs/` | Installed map pack zips. |
| `pack_fixes/` | Pack Doctor fixes. |
| `cheese/`, `spheres/` | Cheese Tracker and Sphere Tracker caches. |
| `engine/` | The portable engine:<br>• `python/`, `archipelago/`, `downloads/`, `backups/`.<br>• `apworld_cache/` (apworld versions for seeds).<br>• `engine.json`.<br>• `install_changes.json` (what Atlas changed in the user's own Archipelago install, for undo). |
| `logs/` | `atlas_log.txt` (rotated) and crash reports. |

- **Every save goes through `SafeFile`:** an atomic write, the previous version kept as `.bak`, and automatic recovery from damage. A damaged file is set aside as `.corrupt-<time>`.
  - A file another program holds (an antivirus or sync tool) is read again for about a second and a half. It's never mistaken for damage; if it stays held, Atlas uses the fallback and refuses to save over it until it reads again.
  - `SafeFile.Delete` removes the backup and any temp file too, so a deleted file can't come back from its backup.
- **No failure goes unseen:** work nobody awaits (button handlers, background checks, closing connections) starts with `Async.Fire(task, "what it's doing")`. A failure is logged with that description and, unless the work is routine, shown to the user in plain words. There's no `async void` and no discarded task (`_ = …`): the guard rails reject both.

## How things talk to each other

- **Archipelago servers:** websockets through MultiClient.Net, compressed. Reconnects are capped and backed off, and stop when the server refuses. Session events arrive on network threads and move to the main thread with `CallDeferred`.
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

Downloads that become code are pinned:
- The engine's Python, pip, Archipelago and Universal Tracker are each checked against a fixed SHA-256.
- Archipelago's Python packages install from `Scripts/Core/Engine/Data/engine_packages.lock.txt` with `--require-hashes` (rebuild it with `Tools/build_engine_lock.py`).
- Apworlds download only from sources the user trusts.

## Building, testing, releasing

- **Building and testing:** see [CONTRIBUTING.md](../CONTRIBUTING.md).
- **CI** (`.github/workflows/ci.yml`): guard rails → build → format check → hash-checked Godot → import → self-test.
- **Releases** (`.github/workflows/release.yml`): run when a `v*` tag is pushed. The version is set once, in `AP_Atlas.csproj`.

## Planned restructuring (roadmap Phase 1)

These are known structural debts, scheduled before the new shell is built:
- **Large classes:** `MainTrackerWindow` (about 3000 lines) and `SlotTrackerControl` (about 2900 lines) will be split. Slot state moves out of the UI node into a `SlotModel`, and connections into a `SessionManager`.
- **Re-parenting:** views will subscribe in `_EnterTree` and unsubscribe in `_ExitTree`, so they can be re-parented (docking, pop-outs).
- **Coalesced refreshes:** one refresh per frame, with hidden views skipped.
- **A Godot-free `AP_Atlas.Core` library** with unit tests.
- **One engine process per multiworld** instead of per slot.
