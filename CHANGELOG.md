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
- **Settings → Privacy & permissions:** everything you've allowed Atlas to do without asking, and every apworld source you trust, each with a way to take it back.

### Changed
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
- **GitHub failures:** a rate-limited or failed GitHub check was cached as "no apworlds" or "not found" (for up to 7 days). Now only real answers are kept.
- **Map pack updates:** the update check missed updates published in PopTracker's versions format.
