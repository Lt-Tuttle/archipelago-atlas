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
| A mapping keyed by the game's own name (`["Passage - The Martyr"] = {"@…"}`, `["Boo Portrait"] = {"boo_portrait"}`) is read through the game's names (as written, or case and punctuation aside when that's unique); a name the game lacks is listed, never guessed. A location mapping value without `@` that is no item's code names a section, found wherever it is. | Rain World, Paper Mario, FFXIV, Pokémon Crystal and Platinum (71 packs); Spyro 3, Super Mario 64 (29 packs) |
| `Tracker.BulkUpdate` pauses the code watches: items that change meanwhile run their watches once each when a script sets it back (87 corpus packs set it round their clear handler's item loop); a `"*"` watch is given the item's codes. | The corpus; Oracle of Seasons, A Link to the Past |
| `Archipelago.CheckedLocations`, `MissingLocations`, `Seed`, `ClientStatus` and the players (`GetPlayerAlias`, `GetPlayerName`, `GetPlayerGame`) are there for the scripts, kept up per check. | Dark Souls III; A Link to the Past |
| A progressive item answers to its stages' codes; a stage's code links by the stage's name. | Ship of Harkinian |
| A `static` item and a `composite_toggle` are decoration, not tiles to link; a grid whose `item_size` is under 12 is decoration (route marks); the pack's own switches (`setting_*`, items from a settings file) aren't items. | Ship of Harkinian; A Link to the Past |
| A tile the location mapping lights (a boss marker) or the scripts set (a goal, a drop) is said so, not reported as never lighting. | A Link to the Past |
| A tile named like a linked tile is the same item under another code. | A Link to the Past (key-drop layouts) |
| A tile named as the game names the item, case and punctuation aside ("HM01 - Cut" for "HM01 Cut"), pairs when that's unique; tiles named as the game's items plus one word three or more of them share ("… Silo") pair without it. | Pokémon Platinum; Banjo-Tooie (four packs) |
| A map no tab shows and no pin is on is left out of the map list; the Map Tracker opens on the map with the most pins. | Ship of Harkinian; A Link to the Past |
| Key Items draws a pack's root layout as the pack lays it out: arrays side by side or stacked, docks, groups under headers, referenced layouts in place; maps and settings grids left to their own tools. | A Link to the Past |
| Scripts run under limits (steps, memory, time, nesting) and can't freeze or crash Atlas; a stop is said in Key Items, the log, Properties and the Doctor. The memory limit is on what the scripts hold (the heap's growth), not on what they allocate and drop. | The corpus (HITMAN's 5,771 checks); A Link to the Past (18 million steps of init churned a gigabyte) |
| Several pins can share one path (a tier listed once with its checks and once on the overworld with refs to them; two world maps): a mapping path or a ref resolves to the pin that has the named section, as PopTracker finds it. A path's section is matched as its author wrote it (case and punctuation aside), or a child pin of that name with one section. | Satisfactory (555 refs); Choo-Choo Charles (56 mapping paths) |
| Lua 5.4's `<const>` and `<close>` are taken off locals; a block that declares a local and ends in a label its `goto`s jump to (the "continue" idiom) is wrapped in `do … end`, which MoonSharp accepts where Lua 5.4 needs no help; PopTracker's `Highlight` levels are there. | Hades; Anodyne, Cat Quest, Donkey Kong 64; Kingdom Hearts II |
| An image is decoded by its header, not its name: packs name JPEGs and WebPs ".png". | Mega Man 3; Undertale; Civilization VI |
| An item the scripts make (`ScriptHost:CreateLuaItem`) answers to its codes: a grid cell with one isn't an undefined cell, it's the scripts' own tile, and Key Items draws it with the name and picture the script set (`Name`, `Icon`) and the state its `ProvidesCodeFunc` gives. | Super Metroid (transitions); A Link to the Past (enemizer); Celeste (Open World) |
| A file the scripts load that the pack doesn't have is the pack's own note (PopTracker prints the same and goes on), not a failure of the scripts. | Fourteen packs loading a template's snes.lua |
| A mapping keyed by offsets from the game's ids (the pack's archipelago.lua subtracts a base before the lookup: `LOCATION_MAPPING[id - (0xDEAFF00D + 50)]`) is read with the base that puts nine in ten of its keys on the game's ids (`MappingOffset`), for locations and items, when the names agree too (a quarter of the paths or codes read as the game's name for the id); the Doctor says which base (`locmap:offset`, `itemmap:offset`). Keys that are the game's ids already, that no base fits, or that fit by number alone (Tevi's and CrossCode's packs number another version's list: nine in ten keys land on an id, but on the wrong location) are left as they are. | Streets of Rage; A Hat in Time; Psychonauts; Tevi (the refusal) |
| A `goto` that leaves a loop for the label right after it is a `break` (`LuaGoto`): MoonSharp runs the jump but leaves the loop's values on its stack, and the enclosing loop's next step fails with "Can't assign number to type Tuple". | The Legend of Zelda: Oracle of Seasons (dungeon images) |
| A map is listed by the title of the tab that shows it alone, as PopTracker's window names it ("Hoenn" for "hoennmap", "Spirit's Grave" for "d1"); a tab that abbreviates a readable name ("SFM" for "Sacred Forest Meadow") doesn't rename it. | Pokémon Emerald; Oracle of Ages; 193 more |
| A progressive item that received nothing shows its first stage's picture (or the item's or stage's `disabled_img`), dimmed, as PopTracker does: 266 corpus packs keep such items' pictures on their stages only, and Key Items drew their names on plain tiles. | Ship of Harkinian; Stardew Valley; 264 more |
| Setting an item's `Icon`, `Name`, badge or overlay runs no code watch; only its state does (`Active`, `CurrentStage`, `AcquiredCount`, `MinCount`, `MaxCount`), as in PopTracker. A "*" watch that sets icons would otherwise call itself. | The Legend of Zelda: Oracle of Seasons |

## Pairing names with the game's

| Rule | Taught by |
|---|---|
| Names compare lower-case, letters and digits only; `w/` = with, a slash between words = or, `&` = and; a slash also reads as a gap. | Dark Souls III |
| A pin naming the only location of its kind in its area links by itself, with the reason; numbered copies pair by their number (a "1" matches the unnumbered first). | Dark Souls III; Dark Souls Remastered |
| A section named like its neighbours links like them: the mapping gives "Item 2" to "Item 8" the game's "KF Shop Item 2" and so on, so "Item 1" is "KF Shop Item 1". | Ship of Harkinian |
| Two sections with a generic name ("Item 1", "Chest") on different pins are two checks, not one check shown twice; a name that is the location's own is one check wherever it's shown. | Ship of Harkinian |
| A section the pack's own thorough mapping doesn't name is "untracked by the pack too" (often the goal), not a pairing Atlas missed. | Ship of Harkinian |
| Hint markers ("… - hint") aren't checks; composed names ("Wave 10 Completed" under "Brawler" is "Wave 10 Completed (Brawler)") pair. | Brotato |
| An apostrophe is part of its word ("Collector's_Map" is "Collectors_Map"), not a gap. | Hollow Knight |
| A pin's name and a section's compose either way: "Beachstickball (10 Hits)" from pin "Beachstickball" and section "10 Hits", as well as "Wave 10 Completed (Brawler)". The pin's parents compose too, whole or as their code before ":" or " - " ("E3L5 3DRealms Atomic Health" under "E3L5: Movie Set"); a section's leading parenthetical belongs to the pin ("Amity Square (Left) - Item Near Center Warp"); a section that repeats its pin's name is the rest of it ("Hub 2-5, item 9"); each candidate is also matched case and punctuation aside when that's unique ("Clear Room 04"). | A Short Hike; Brotato; Duke Nukem 3D; Pokémon Platinum (manual); Satisfactory; Hades II |
| A section named as the game names the check without its area ("Basement Pot 1" for "Waynehouse: Basement Pot 1") links when exactly one location ends so after its area, nothing shows it yet, and the name is on one pin only. | Hylics 2; A Short Hike |
| Exact matches are linked on a pack's first check, as automatic fixes with Undo; the pass runs again when Atlas's rules change (`PackDoctorService.LinkRules`), after taking back Atlas's own guesses the new rules read differently. The user's own links are never touched. | Ship of Harkinian |

### Suggestions (what the Doctor offers when nothing links by itself)

Measured, not guessed: most packs ship a mapping that says which location each section is, so hiding that answer and asking
for a suggestion gives a right-or-wrong score on thousands of real cases (`PackDoctor.EvaluateSuggestions`, printed by the
corpus check). On the design side (28,105 sections named otherwise than the game): the top suggestion was right for
45% with the name alone, and is right for 78% now; of the picks scored 85%+ (what "Fix what Atlas can" applies),
51% were right before and 91% now. On the held-out games, never looked at while these rules were
written: 38% → 83%, and 51% → 92% at 85%+.

| Rule | Taught by |
|---|---|
| A section is scored with its context: its name alone and composed with its pin's and parents' names ("Chest" under "Crystal Den"). | Many: generic section names |
| A location whose area tag is the pin's place scores a little higher; one whose area is another place of this pack's, clearly lower. The pack's own links teach its abbreviations ("UP:" is Undead Parish when the game's "UP: …" sit under that place again and again). | Dark Souls; Pokémon Platinum (wrong-area picks) |
| A section without a copy number takes its pin's ("Boss" on pin "Ruins Boss 2"). | Lunacid |
| Opposite words contradict ("Top Left" isn't "Bottom Left": left/right, top/bottom, upper/lower, the compass, inside/outside, front/back, before/after, first/last, entrance/exit). | A Link to the Past (Ganon's Tower) |
| A generic label (a section name the pack gives on three or more pins: "Yarn", "Scraps", "Chest") says nothing by itself; the pin's name speaks for it. | A Hat in Time; Banjo-Tooie |
| Two different locations scoring alike, or a nameless copy of a numbered family ("Scraps" against "Scraps 1" to "Scraps 20"), are offered but never as a confident pick: only the pack's mapping knows which. | Choo-Choo Charles |
| One location goes to one place: rows are given their best suggestions strongest first, each location once; a row whose best went to a stronger row has its next one first. | Anodyne |
| A section that can only be looked at (every access rule PopTracker's "{…}": the player's position, entrance logic, hint signs) isn't a check, unless something links it. | Anodyne; TTYD; 35 more |

### Fix by example (what a person teaches)

When a person links two rows by hand, `PackPatterns` (AP_Atlas.Core) looks for the rule that makes both names: the check's
name built from the row's own names (the section's, the pin's, the pin's group or area, a number) and fixed text between
them. It reads nothing but the rows and the game's names, so it works for packs no one has seen. What keeps it right:

| Rule | Taught by |
|---|---|
| Offered only when it reproduces both examples exactly; a row is linked only to a name the game has, that nothing shows yet, that no other row would get, and that no other rule fitting the examples names differently. | The method |
| It reaches only rows like the examples: a name it doesn't use that both examples share (their pin "Cooked", their group, their sections' "Large Health Energy #") the row shares too. | Stardew Valley (Cooked and Shipped pins of one dish); Shadow the Hedgehog (one stage's counts); Mega Man 3 |
| A number is never fixed text: "Scraps 1" copied from two first copies is wrong for the next pin's second. | Choo-Choo Charles; Kirby's Dream Land 3 |

Measured on the packs' own mappings (`PackDoctor.EvaluatePatterns`): the sections a person would fix by hand (their top
suggestion isn't right at 85%+) are fixed in order, and after each the best rule the hand links teach is applied to the
rest. On the design side, 10,894 fixed by hand taught rules that linked 5,103 more, 99% of them right (65% before the last
two rules). On the held-out games: 2,668 by hand, 719 more by rules, 99% right.

### The holdout

A quarter of the corpus's games (`Tests/corpus_holdout.json`, made once by `Tools/corpus_holdout.py`, by game so nothing of a
held-out game leaks into a rule) is never looked at while rules are written. A rule ships when it helps the design side and
also helps, or leaves unchanged, the holdout; no pack anywhere gets worse.

## Versions

| Rule | Taught by |
|---|---|
| A project's newest release can be for a newer apworld than the engine's: releases are measured against the game's names (`PackReleaseFit`) and the one that fits is taken; a fork kept up with the apworld wins over an outdated original, but a fork's old copy doesn't beat the original's release that fits as well. | Dark Souls III; A Link to the Past |
| The game's own setup guide's tracker link is the author's recommendation and comes first; the apworld release whose guide names the tracker is Atlas's pick. | Dark Souls III; Dark Souls II |
| A pack under 75% linked against the seed's names says so on the map, with a search for a release that fits. | Dark Souls III |

## Reporting

| Rule | Taught by |
|---|---|
| A grid none of whose tiles names an item of the game, in a pack whose other grids do, shows the pack's own state (an enemizer table): said once per grid, its tiles not counted among those to link. | A Link to the Past (2,057 rows); Super Mario 64; Shivers |
| Numbered sections named alike ("Pot #1" to "Pot #40") that match nothing of the game's are one family (another version or fork of the apworld has them): said once, nothing to choose. | A Link to the Past (door and pot shuffle forks); Undertale |

### Checks no pin shows (offered, previewed)

| Rule | Taught by |
|---|---|
| A check with no pin goes beside a pin of a check from its area (the name's tag), on the map where most of that area's checks are, nearest their middle; checks put by one pin go round it. Offered in the guide with a preview, never by itself; the pins are Atlas's, drawn quieter. | The method; measured on the packs' own pins |

Measured by taking the packs' own pins away and placing each check again (`PackPlacement.Evaluate`): on the design side
36,257 placed, 97% on the pin's own map, 57% of those within a tenth of the map's spread of pins; on the held-out games
10,488 placed, 100% on the right map, 46% that close. Good enough to put a check in the right part of the right map for a
person to nudge, which is all it's offered as.

## Where new rules come from

The corpus (with its holdout) and what users send: the Pack Doctor's **Suggest them to Atlas** sends the fixes a person made
by hand, and `Tools/review_suggestions.py` groups them by shape ("clear {section} spear weapon", "{pin} - {section}", a
marker named "space-#") across packs. A shape seen in two or more packs is a candidate; it becomes a rule only as every
rule does: a method that works from what any pack carries, proven on the corpus and the holdout, no pack worse.

## What isn't a rule

- Ids a mapping lists that this apworld doesn't have: the pack is for another version. Reported, never guessed.
- Two fixers built for step 6 and not shipped, because nothing in the corpus needed them (the rule: a rule helps two or
  more packs): a tile without a picture given the pack's picture named like the item (164 such tiles in all 398 packs,
  none with a picture of that name), and a name-keyed mapping's older names followed to the checks a newer version calls
  them (no pack keys its mapping by names of another version). They come back if user suggestions show the pattern.
- A pack named for another game than the one its ids belong to (Celeste's Open World pack says "Celeste" and lists Celeste (Open World)'s ids; Hades II's says "Hades"): which game a pack is for is the pick rule's job; the ids are reported as stale, and an offset that fits by number alone (another game numbered the same way) is refused when the names don't agree.
- Pins off their map's picture, typos in refs, missing pictures: the pack's own, in the author's section of the report.
- A few rows of wording the rules don't cover: the Doctor's suggestions, "Fix what Atlas can", and the user's choice.
- The clear handler's errors when the lab runs it without a slot (`slot_data.settings.x` on an empty table): a real seed's data is the test; `Tools/corpus_kinds.py` lists them apart, and the solo batch judges them.
