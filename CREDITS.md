# Credits

The Archipelago Atlas exists thanks to the projects and people below. Each entry names who made it, where its official releases are, and its license. The full license texts of everything built into Atlas are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Built into Atlas

| Component | Made by | Official releases | License |
|---|---|---|---|
| Godot Engine 4.3 | Juan Linietsky, Ariel Manzur and the Godot contributors | https://godotengine.org/download | MIT |
| .NET 8 runtime | Microsoft and the .NET Foundation contributors | https://dotnet.microsoft.com/download | MIT |
| Archipelago.MultiClient.Net 6.7.1 | Hussein Farran, Jarno Westhof and contributors | https://github.com/ArchipelagoMW/Archipelago.MultiClient.Net/releases | MIT |
| Newtonsoft.Json 13.0.4 | James Newton-King | https://github.com/JamesNK/Newtonsoft.Json/releases | MIT |
| MoonSharp 2.0.0 | Marco Mastropaolo (parts from KopiLua) | https://github.com/moonsharp-devs/moonsharp/releases | BSD 3-Clause |
| Google Sans and Google Sans Code fonts | The Google Sans Project Authors (Google) | https://github.com/googlefonts/googlesans, https://github.com/googlefonts/googlesans-code | SIL OFL 1.1 |
| Feather icons | Cole Bemis | https://github.com/feathericons/feather/releases | MIT |

Atlas's icon is AI-generated, drawn in the style of [Archipelago](https://archipelago.gg)'s logo (Archipelago is MIT-licensed).

## Downloaded by the Atlas Engine

When you set up the Atlas Engine, Atlas downloads these from their official sources, each checked against a fixed SHA-256. They aren't included in Atlas itself.

| Component | Made by | Official releases | License |
|---|---|---|---|
| Python 3.12.10 (embeddable) | Python Software Foundation | https://www.python.org/downloads/release/python-31210/ | PSF License |
| pip 26.2.1 | Python Packaging Authority | https://pypi.org/project/pip/26.2.1/ | MIT |
| Archipelago 0.6.7 | The Archipelago contributors | https://github.com/ArchipelagoMW/Archipelago/releases/tag/0.6.7 | MIT |
| Universal Tracker v0.3.4 | FarisTheAncient and contributors | https://github.com/FarisTheAncient/Archipelago/releases/tag/Tracker_v0.3.4 | MIT |

Archipelago's Python packages, from [PyPI](https://pypi.org) (exact versions, every file hash-checked):

| Package | Made by | Project | License |
|---|---|---|---|
| bsdiff4 1.2.6 | Ilan Schnell | https://github.com/ilanschnell/bsdiff4 | BSD |
| certifi 2026.2.25 | Kenneth Reitz and contributors | https://github.com/certifi/python-certifi | MPL 2.0 |
| charset-normalizer 3.5.2 | Ahmed R. Tahri | https://github.com/jawah/charset_normalizer | MIT |
| colorama 0.4.6 | Jonathan Hartley | https://github.com/tartley/colorama | BSD |
| cymem 2.0.13 | Matthew Honnibal (Explosion) | https://github.com/explosion/cymem | MIT |
| idna 3.20 | Kim Davies | https://github.com/kjd/idna | BSD 3-Clause |
| jellyfish 1.2.1 | James Turk | https://github.com/jamesturk/jellyfish | MIT |
| Jinja2 3.1.6 | Pallets | https://github.com/pallets/jinja | BSD 3-Clause |
| MarkupSafe 3.0.4 | Pallets | https://github.com/pallets/markupsafe | BSD 3-Clause |
| orjson 3.11.7 | ijl | https://github.com/ijl/orjson | MPL 2.0 and (Apache 2.0 or MIT) |
| pathspec 1.0.4 | Caleb P. Burns | https://github.com/cpburnz/python-pathspec | MPL 2.0 |
| platformdirs 4.9.4 | The tox-dev team | https://github.com/tox-dev/platformdirs | MIT |
| Pymem 1.14.0 | Fabien Reboia | https://github.com/srounet/Pymem | MIT |
| PyYAML 6.0.3 | Kirill Simonov and the YAML project | https://github.com/yaml/pyyaml | MIT |
| requests 2.34.2 | Kenneth Reitz and the PSF | https://github.com/psf/requests | Apache 2.0 |
| schema 0.7.8 | Vladimir Keleshev | https://github.com/keleshev/schema | MIT |
| setuptools 80.10.2 | Python Packaging Authority | https://github.com/pypa/setuptools | MIT |
| typing_extensions 4.15.0 | Guido van Rossum, Jukka Lehtosalo, Łukasz Langa, Michael Lee | https://github.com/python/typing_extensions | PSF 2.0 |
| urllib3 2.8.0 | Andrey Petrov and the urllib3 team | https://github.com/urllib3/urllib3 | MIT |
| websockets 13.1 | Aymeric Augustin | https://github.com/python-websockets/websockets | BSD 3-Clause |

## Content you add

- **Map packs** (PopTracker format) are made by their authors and keep their own licenses. Atlas shows each pack's author and where it came from.
- **Apworlds** are made by each game's developers. Atlas shows each one's source, and their authors as listed in the apworld.

## Services Atlas works with

- **Archipelago** and **archipelago.gg** (https://archipelago.gg): the multiworld servers and website Atlas connects to.
- **Cheese Tracker** by Chris Howie (https://cheesetrackers.theincrediblewheelofchee.se; source: https://github.com/cdhowie/cheese-trackers, AGPL-3.0). Atlas uses its public API with your own key; it contains none of its code.
- **spheretracker.de** (https://spheretracker.de): Atlas reads the room pages your multiworld's host shares. The site doesn't name its author.
- **GitHub**, **PyPI** and **python.org**: release information and downloads for apworlds, map packs and the engine.

## Inspiration

- **Hydra Text Client** by SWCreeperKing (https://github.com/SWCreeperKing/HydraTextClient_Rewrite): the Archipelago text client that inspired Atlas. No code from it is used.
- **PopTracker** by black-sliver (https://github.com/black-sliver/PopTracker): map tracking, and the pack format Atlas reads. Atlas contains none of its code (PopTracker is GPL-3.0).
- **Universal Tracker** (https://github.com/FarisTheAncient/Archipelago): logic tracking for any game.
- **Archipelago's text client and web trackers**: what a tracker should show.
- **Cheese Tracker**: the Cheese Tracker tab's filters and sorting.
- **spheretracker.de**: sphere tracking.
- **Visual Studio Code**: the window layout.
- **Archipelago Alerts** by wrjones104 (https://github.com/wrjones104/ap-tracker, Apache-2.0): alerts for async games.
- **Eijebong's Archipelago-index** (https://github.com/Eijebong/Archipelago-index, archived April 2026): Atlas's bundled list of apworld download links and their SHA-256 hashes was built from it.
- **Archipelago Games Library** by MK-404 (https://mk-404.github.io/Archipelago-Games-Library/): a great browsable list of Archipelago games (linked, not used).

## Made with AI assistance

The Archipelago Atlas is designed, directed and tested by [Lt-Tuttle](https://github.com/Lt-Tuttle). Most of its code was written with an AI assistant (Anthropic's Claude). The icon is AI-generated.

## Unofficial

The Archipelago Atlas is a community tool. It is not affiliated with or endorsed by the Archipelago project.
