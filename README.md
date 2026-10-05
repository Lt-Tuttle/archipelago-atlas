# The Archipelago Atlas

A desktop companion for [Archipelago](https://archipelago.gg) multiworld randomizer games. It tracks your slots, maps, logic, hints and items in one window, and works alongside community tools like Cheese Tracker.

> **Status: pre-release.** Atlas is being stabilized for its first public beta (0.1.0). It isn't ready for general use yet.

## What it does
- **Connections:** connect to several slots and multiworlds at once, with careful, rate-limited reconnects.
- **Trackers:**
  - Map (PopTracker packs).
  - Key Items.
  - Logic Tracker, powered by Archipelago's own logic through the Atlas Engine.
  - Item History.
  - Hints.
- **Properties panel:** details and "why" for any location, item, player or hint.
- **Cheese Tracker integration:** slot statuses, hints and suggestions.
- **Sphere Tracker:** reads the host's spheretracker.de room, read-only and hidden in race mode.

## Principles
- **Stability first.** Crash-safe saves, explicit errors, tested protections.
- **Light on servers.** Minimal, backed-off traffic to Archipelago and every other site.
- **Your PC is yours.** Atlas asks before it looks outside its own folder or goes online for anything new.

## Unofficial
The Archipelago Atlas is a community tool. It is not affiliated with or endorsed by the Archipelago project.

## Made with AI assistance
Atlas is designed, directed and tested by [Lt-Tuttle](https://github.com/Lt-Tuttle). Most of its code was written with an AI assistant (Anthropic's Claude).

## License and credits
Atlas's own code is released under the [MIT License](LICENSE). It builds on the work of others:
- [Godot Engine](https://godotengine.org).
- [Archipelago](https://github.com/ArchipelagoMW/Archipelago) and [Archipelago.MultiClient.Net](https://github.com/ArchipelagoMW/Archipelago.MultiClient.Net).
- [Universal Tracker](https://github.com/FarisTheAncient/Archipelago).
- [PopTracker](https://github.com/black-sliver/PopTracker) pack authors.
- [Cheese Tracker](https://github.com/cdhowie/cheese-trackers).
- [Hydra Text Client](https://github.com/SWCreeperKing/HydraTextClient_Rewrite).
- The Google Sans and Google Sans Code fonts (SIL Open Font License).

Full credits and third-party licenses will be listed in `CREDITS.md` and `THIRD_PARTY_NOTICES.md`.
