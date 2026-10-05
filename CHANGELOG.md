# Changelog

All notable changes to The Archipelago Atlas are listed here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Development toward the first public beta, 0.1.0.

### Added
- **Connections:**
  - Several multiworlds and slots at once.
  - Compressed connections.
  - Careful auto-reconnect: capped and backed off, and it stops when the server refuses.
- **Trackers:**
  - Map (PopTracker packs).
  - Key Items.
  - Logic Tracker.
  - Item History.
  - Hints (status editing, hint requests, points).
- **Properties panel:** details and "why" for locations, items, players, hints, maps, packs and profiles. It has history, notes and flags.
- **Atlas Engine:**
  - A portable logic engine (Python, Archipelago and Universal Tracker), set up with hash-checked downloads, verification and rollback.
  - Each seed's apworld version is matched automatically.
- **Pack Doctor:** checks map packs, fixes them with undo, and writes reports for pack authors.
- **Cheese Tracker:**
  - Slot statuses, hints and room overview.
  - Logic-based status suggestions.
  - Opt-in automatic updates for your own claimed slots.
  - A Cheese Tracker tab with its filters and sorting.
- **Sphere Tracker:** reads only the host's spheretracker.de room, read-only. It's hidden in race mode.
- **Race mode:** hides spoiler-adjacent information.
- **YAML exclusions:** applies a slot's excluded locations from its YAML.
- **Reliability:**
  - Crash-safe saves with backups and recovery.
  - A polite web client (one request at a time per site, backoff, size and time limits).
  - Engine processes that close with Atlas.
  - A self-test suite (`AP_Atlas_Source/Tools/run_selftest.ps1`).
- **Project:**
  - MIT license and font licences.
  - A single version number (`0.1.0-beta.1`), shown in the window title and sent in the User-Agent.
  - `CREDITS.md` and `THIRD_PARTY_NOTICES.md` credit every component, service and inspiration, with license texts.
  - `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md` and `docs/ARCHITECTURE.md`.
  - Guard rails (`Tools/check_guards.ps1`) run in CI.
  - Microsoft's async analyzers check every build. An unobserved task, `async void`, a blocking wait, or `ContinueWith` without a scheduler fails it.
  - Code that doesn't need Godot lives in its own library, `AP_Atlas.Core`, with xUnit tests that run with `dotnet test` (and in CI). The version is set once, in `Directory.Build.props`.
  - Nullable reference checks for all new code. Older files are annotated as they're reworked, and the guard rails keep their number from growing.
  - An optional self-test (`ATLAS_SELFTEST_SETUP=1`) sets up the portable engine from nothing and health-checks it.
  - A visual check (`Tools/run_visualcheck.ps1`): pictures of the main screens, compared pixel by pixel with an earlier run.
- **Settings → Privacy & permissions:** everything you've allowed Atlas to do without asking, and every apworld source you trust, each with a way to take it back.

### Changed
- **Platform:** Atlas now runs on Godot 4.7.2 and .NET 10 (it was on Godot 4.3 and .NET 8, whose support ends in November 2026). Every screen was compared before and after, pixel by pixel.
- **Lighter at rest:** Atlas redraws only when something on screen changes, so it uses next to no CPU or graphics card while you're not using it.
- **Saving:** dragging a splitter or moving a map no longer writes your settings to disk on every mouse movement. They're saved once you stop, and when Atlas closes.
- **Renderer:** Atlas now draws with Godot's Compatibility renderer (OpenGL 3.3, with a Direct3D fallback built into Godot), which runs on more graphics cards. On the test PC it starts about a quarter faster and uses about a third less video memory, and every screen looks the same.
- **Atlas asks before going outside its folder:**
  - Atlas no longer searches the PC for Archipelago at startup. Find in Atlas Engine asks first and says exactly where it will look.
  - Atlas no longer reads every YAML in your Archipelago's Players folder. It reads only the YAML you link to a slot, and the picker opens where you last picked one.
- **Your Archipelago install:**
  - Atlas adds its bridge to your own install only after you allow it, and records each change.
  - **Remove Atlas's files** undoes everything it added or moved.
- **Automatic apworld fixes:** looking up a seed's apworld version on GitHub by itself needs your OK. Pressing **Fix** asks once, with an "Always allow" option.
- **Map packs:**
  - "Search GitHub for Packs…" shows the results for you to choose from, instead of installing the first hit. It runs at most two searches per game.
  - Updates: the check reads PopTracker's own versions format, compares versions by number, and asks before opening a download page.
  - Deleting a pack asks first.
  - Imported files are checked to be real packs.
- **All web requests, including GitHub and map packs, now go through one polite client.**
  - GitHub's rate limits are waited out, not hammered.
  - Unchanged answers come from a cache.
- **Engine downloads name every source:** looking for a seed's apworld version lists each GitHub project it will try, and why, before downloading.
- **Dependencies:** Archipelago.MultiClient.Net 6.7.1 and Newtonsoft.Json 13.0.4 now come from NuGet, instead of copied DLLs.

### Security
- **Room passwords are saved encrypted for your Windows account.** Older plain-text profiles are converted, along with their backup. You're offered to delete damaged old copies that still hold passwords.
- **Links:** only https web pages and existing folders are opened. A map pack can no longer get a file opened (the Pack Doctor saves a copy of an image where you choose instead).
- **The engine's pip** is a pinned, hash-checked wheel. `get-pip.py` was unpinned, and is no longer used.
- **Archipelago's Python packages** install only as the exact files in Atlas's hash list (`--require-hashes`).
- **A world's own requirements** can no longer point pip at another package index or local files.
- **Deeply nested JSON** is refused instead of overflowing the stack (Newtonsoft.Json 13).

### Fixed
- **Files held by another program:**
  - If an antivirus or sync tool (OneDrive, Dropbox) was reading your settings or profiles at the moment Atlas read them, Atlas took the file for damaged and put its older backup in its place. Your latest changes were lost.
  - Now Atlas reads the file again until it's free. If it stays held, Atlas carries on without it, says so, and won't save over it.
- **Closing a slot or window while it was busy:** updates that arrived afterwards (from the server, the engine or a download) could fail against the closed view. They're now skipped, and any other failure in a window update is logged.
- **Closing Atlas** now saves your multiworlds as well, so what changed during the session (slot stats, links) is kept.
- **Notes, flags, exclusions and Pack Doctor fixes:** a save that fails is now shown to you, not only logged.
- **Deleted data came back:** deleting a slot's saved data, or unlinking Cheese Tracker, left a backup that the next read restored. Deleting now removes the backup too.
- **Stopping or restarting the logic engine** while it was answering a logic question could break that question. Worse, the next engine's startup could wait out its 3-minute limit, because the old question read the new engine's first answer. The engine's connection now changes only between questions.
- **Settings changed by background work:** adding an apworld source from a link changed and saved your settings from a background thread, where it could collide with other changes. Settings are now changed and saved on the main thread only.
- **Pack Doctor:** its background check read your fixes and the name lists while you might be editing them, and could load images off the main thread. It now works from a snapshot taken first.
- **Hidden failures:** a dozen places that ignored a failure now log what went wrong. Putting files back into your Archipelago install after a failed step says so if a file can't be put back.
- **No silent failures in background work:**
  - Every check, download and connection Atlas runs in the background now reports a failure instead of losing it. The log says what was being done, and you're told in plain words (at most once every 10 minutes for the same work).
  - Before, 28 such tasks could fail without a trace.
  - Reading a map pack, or a slot's saved options, now logs why it failed instead of showing nothing.
- **Apworld fixes:** choosing an apworld file that couldn't be copied (a full disk, say) left that slot's apworld fixer stuck until you reconnected.
- **Chat and hint requests:** a message or `!hint` request that couldn't be sent now says so, instead of looking sent.
- **Engine setup:** unpacking Python and Archipelago can now be cancelled, and waiting for a finished engine step's output is bounded by the step's time limit.
- **Memory:** an unused, hidden menu was created at startup and kept for the whole session. Godot now reports nothing left over when Atlas closes.
- **Window icon:** Atlas's Windows icon setting was in the wrong section, so Godot ignored it and used the PNG.
- **Running from source:** Godot no longer imports the files in Atlas's data folder (it had left 160 `.import` files there).
- **GitHub failures:** a rate-limited or failed GitHub check was cached as "no apworlds" or "not found" (for up to 7 days). Now only real answers are kept.
- **Map pack updates:** the update check missed updates published in PopTracker's versions format.
