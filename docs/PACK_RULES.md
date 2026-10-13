# How Atlas reads map packs: the rules, and the packs that taught them

Atlas reads PopTracker packs the way PopTracker does, and pairs what they show with Archipelago's own names. Every rule here is general (a reading of PopTracker, or of how pack authors name things), was proven on the pack that taught it and on the whole corpus (no pack links or places fewer checks: `AP_Atlas_Source/Tests/packs_baseline.json`), and has a self-test. A fix that would help one pack alone isn't a rule; the Pack Doctor's suggestions and "Fix what Atlas can" are for those rows.

The pack lab (CONTRIBUTING, "The pack lab") is how new rules are found: the corpus check over every pack, findings grouped by kind, the gallery's pictures read like a screen.

## Reading a pack's files and scripts (as PopTracker does)

| Rule | Taught by |
|---|---|
| A section's `ref` takes its target's links (fixes included); a `ref` to a section that isn't in the pack is repaired from the section's own "Room - Check" name, and listed for the author. | Dark Souls II; Ship of Harkinian |
| A mapping the scripts build at run time is read from the tables they leave; hexadecimal ids and `[BASE + n]` are read; the table named for the mapping is the one taken. | Dark Souls II |
| A section hosting an item without an `item_count` holds no check (entrance and boss toggles), unless it alone can place a location; a location mapped to an item code shows in the section hosting it. | Super Metroid, Super Mario 64, A Link to the Past |
| Lua 5.3's `\u{XXXX}` escapes, and its `//` and bitwise operators, are translated before MoonSharp (Lua 5.2) compiles a script; `string.format` writes any value for `%s`; items have the overlay methods. | Dark Souls III; Ship of Harkinian; A Link to the Past; the corpus |
| `Archipelago.CheckedLocations`, `MissingLocations`, `Seed`, `ClientStatus` and the players (`GetPlayerAlias`, `GetPlayerName`, `GetPlayerGame`) are there for the scripts, kept up per check. | Dark Souls III; A Link to the Past |
| A progressive item answers to its stages' codes; a stage's code links by the stage's name. | Ship of Harkinian |
| A `static` item and a `composite_toggle` are decoration, not tiles to link; a grid whose `item_size` is under 12 is decoration (route marks); the pack's own switches (`setting_*`, items from a settings file) aren't items. | Ship of Harkinian; A Link to the Past |
| A tile the location mapping lights (a boss marker) or the scripts set (a goal, a drop) is said so, not reported as never lighting. | A Link to the Past |
| A tile named like a linked tile is the same item under another code. | A Link to the Past (key-drop layouts) |
| A map no tab shows and no pin is on is left out of the map list; the Map Tracker opens on the map with the most pins. | Ship of Harkinian; A Link to the Past |
| Key Items draws a pack's root layout as the pack lays it out: arrays side by side or stacked, docks, groups under headers, referenced layouts in place; maps and settings grids left to their own tools. | A Link to the Past |
| Scripts run under limits (steps, memory, time, nesting) and can't freeze or crash Atlas; a stop is said in Key Items, the log, Properties and the Doctor. | The corpus (HITMAN's 5,771 checks) |

## Pairing names with the game's

| Rule | Taught by |
|---|---|
| Names compare lower-case, letters and digits only; `w/` = with, a slash between words = or, `&` = and; a slash also reads as a gap. | Dark Souls III |
| A pin naming the only location of its kind in its area links by itself, with the reason; numbered copies pair by their number (a "1" matches the unnumbered first). | Dark Souls III; Dark Souls Remastered |
| A section named like its neighbours links like them: the mapping gives "Item 2" to "Item 8" the game's "KF Shop Item 2" and so on, so "Item 1" is "KF Shop Item 1". | Ship of Harkinian |
| Two sections with a generic name ("Item 1", "Chest") on different pins are two checks, not one check shown twice; a name that is the location's own is one check wherever it's shown. | Ship of Harkinian |
| A section the pack's own thorough mapping doesn't name is "untracked by the pack too" (often the goal), not a pairing Atlas missed. | Ship of Harkinian |
| Hint markers ("… - hint") aren't checks; composed names ("Wave 10 Completed" under "Brawler" is "Wave 10 Completed (Brawler)") pair. | Brotato |
| Exact matches are linked on a pack's first check, as automatic fixes with Undo; the pass runs again when Atlas's rules change (`PackDoctorService.LinkRules`), after taking back Atlas's own guesses the new rules read differently. The user's own links are never touched. | Ship of Harkinian |

## Versions

| Rule | Taught by |
|---|---|
| A project's newest release can be for a newer apworld than the engine's: releases are measured against the game's names (`PackReleaseFit`) and the one that fits is taken; a fork kept up with the apworld wins over an outdated original, but a fork's old copy doesn't beat the original's release that fits as well. | Dark Souls III; A Link to the Past |
| The game's own setup guide's tracker link is the author's recommendation and comes first; the apworld release whose guide names the tracker is Atlas's pick. | Dark Souls III; Dark Souls II |
| A pack under 75% linked against the seed's names says so on the map, with a search for a release that fits. | Dark Souls III |

## What isn't a rule

- Ids a mapping lists that this apworld doesn't have: the pack is for another version. Reported, never guessed.
- Pins off their map's picture, typos in refs, missing pictures: the pack's own, in the author's section of the report.
- A few rows of wording the rules don't cover: the Doctor's suggestions, "Fix what Atlas can", and the user's choice.
