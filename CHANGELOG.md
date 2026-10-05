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
