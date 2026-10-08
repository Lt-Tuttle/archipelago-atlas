# The Archipelago Atlas: the guide

Atlas is a desktop companion for [Archipelago](https://archipelago.gg) multiworlds: your slots' maps, logic, hints and items, and the trackers your group uses, in one window. This guide is built into Atlas (Help → Guide) and lives in the repository as `docs/GUIDE.md`; both are the same text.

Atlas is an unofficial community tool: it isn't affiliated with or endorsed by the Archipelago project.

## Home

Atlas opens on Home (Ctrl+1) unless Settings → Behaviour → Open on says otherwise. Its header carries Atlas's icon and wordmark, which take the theme's colours and your accent. It has:

- **Getting started:** the steps to a working Atlas, each with a button that sets it up in a small window right here, a quieter button to the page where it's done by hand (the step ticks either way, since it reads what Atlas has), and **Skip** for a step that doesn't apply (Show steps lists it again, with Unskip): set up the Atlas Engine (the tick appears the moment the setup finishes); **New…** makes a multiworld (its name, server and password, and its slots, typed or from a YAML, which names a slot and its game); **Connect…** lists every multiworld's slots with a Connect button each; **Find a pack…** searches GitHub once for a map pack of a game your slots play (or another you type) and shows what it found on the Map Packs page; **Link…** links a multiworld to its Cheese Tracker page, or to the host's spheretracker.de room. A step disappears once it's done or skipped, and when every step is done the section says "You're set up." with Show steps to see them again, ticked. The buttons sit beside their text at any window width, and so does the multiworld list's Connect.
- **Your multiworlds:** the ones you've played most recently, each with a Connect button that connects every slot of it in turn.
- **Tools:** a card per tool.
- A tip, which changes each time Home shows.
- **Links** to Archipelago's own pages and to the community's tools. Links open in your browser when you click them; Atlas never fetches them by itself.

## The window

Atlas opens at the size Windows gives other apps (it follows the display scale of the monitor it's on, and follows along when it's moved to a monitor with another scale) and keeps itself, and every dialog and window it opens, within the screen. When the window is too narrow for every part at the zoom you've chosen, Properties gives way first, then the explorer, then the slots panel; they come back as the window widens, and the View menu still shows what you asked for. The Atlas Engine and Pack Doctor windows are windows of their own: move them to another monitor, or behind Atlas, as you like.

- The **activity bar** on the far left holds Home at the top, then three groups, each captioned on two lines on a band in the accent colour: ATLAS TOOLS (Map Tracker, Key Items, Logic Tracker, Item History, Hints, Multiworlds), EXTERNAL TOOLS (Cheese Tracker, Sphere Tracker) and, at the bottom, ATLAS CONFIG (Games, Map Packs, Settings), with the Atlas Engine's button under them. Each button shows its icon with a short name under it (Home; Map, Items, Logic, History, Hints, Worlds; Cheese, Spheres; Games, Packs, Settings; Engine); when the window is too short for the names, the icons stand alone, and the tooltip always gives the tool's full name and its key. Ctrl+1 to Ctrl+9 open Home, Map Tracker, Key Items, Logic Tracker, Item History, Hints, Multiworlds, Cheese Tracker and Sphere Tracker, in the bar's order; Settings is Ctrl+, (comma); Games and Map Packs have no key of their own until you give them one under Settings → Keyboard. A key you changed there is kept.
- The **Slots panel** lists every connected slot as a card with its status. Click a card to make that slot the one the slot tools show. Ctrl+Tab and Ctrl+Shift+Tab go through the connected slots, and the picker in the tool header does the same.
- The **explorer**, beside the content, is the tool's own list: the maps of a pack, the multiworlds, the packs, the Settings sections. Each tool keeps its own explorer width: drag the divider, and that tool remembers it (Map Packs and Games start wider than the rest, for their long names).
- **Properties**, on the right, shows everything about whatever you last selected anywhere: a location, an item, a player, a hint, a map, a pack or a multiworld. It keeps a history (◀ ▶, also Alt+Left and Alt+Right), ⌂ shows the selected slot's summary, and ⧉ (Ctrl+Shift+C) copies everything shown. Its long sections (lists: a multiworld's slots, seed settings, slot data, a location's logic, an item's copies, flagged checks) start collapsed; click a header to open or close a section, and Atlas remembers your choice.
- The **bottom pane** holds Chat (the text client: the room's messages, and a line to type commands such as `!hint`), the System Log (what Atlas did, and any trouble it met) and, in developer mode (Settings → Advanced), the Debug Log (Atlas's own diagnostics). It starts at a third of the height and keeps whatever height you drag it to. The status bar under it says what Atlas is doing and how many slots are connected.
- The **View** menu shows or hides each part, and remembers it. **Focus mode** (F9) leaves the content and its header alone, with the menu bar; F9 again brings everything back. F11 is full screen.
- The **command palette** (Ctrl+Shift+P) lists every command in the menus: type a few letters of its name and press Enter.
- **Tables** (Item History, Hints, Cheese Tracker, Sphere Tracker, Notifications, the shortcuts list) all work the same way: click a column's title to sort by it (again to turn the order; the arrow says which), right-click a title to hide or show columns, type words in the search box to narrow the rows (each word must start a word of a cell), and use Export to copy the rows shown as TSV for a spreadsheet, as a Markdown table, as Discord messages (split to fit), or to save them as a CSV file. Right-click a row for its actions and Copy row. The sort and the hidden columns are remembered per table. Only the rows on screen are laid out, so a table of thousands of rows shows as fast as one of ten; its scroll bar, the wheel and the keys (arrows, Page Up and Down, Home, End) move through them.

## Multiworlds

The Multiworlds page (Ctrl+7) holds your multiworlds: each has a name, a room link, a server address (the server and the room's port, as the room page shows them, for example `archipelago.gg:12345`; the box is empty until you fill it), a password if the room has one (the eye beside the box shows it while you check it), an optional Cheese Tracker link (the tracker's page, or the archipelago.gg room link), an optional Sphere Tracker link (the host's room on spheretracker.de; see Sphere Tracker), and the slots you play in it. Each label sits beside its box, with an (i) that says what goes there, where it comes from and whether it's needed; the whole editor scrolls when the window is short. Passwords are stored encrypted for your Windows account.

Paste the room link the host shared and press **Fill from link**: Atlas asks once to read the room's status page (never the room's page itself), then fills in the server address from the room's port and the slots from its players; if you had typed an address yourself, it asks before replacing it. **Save Settings** writes what you typed to disk and checks the Cheese Tracker and Sphere Tracker links. Nothing you typed vanishes silently: selecting another multiworld, adding one, leaving the page or closing Atlas with unsaved edits asks whether to save them (Save, Don't save or Cancel). A slot's new name takes effect when you press Enter or leave its field, and its saved stats follow it.

Connect a slot from its row, or every slot with Connect All Slots; Home's Connect does the same. A connection that fails says why as a card (the server refused the login, didn't answer in time, couldn't be reached). A connected slot stays connected until you disconnect it or close Atlas. A dropped connection reconnects by itself: a few tries over about 20 minutes, then Atlas stops, so a closed room is never kept busy. You can turn automatic reconnects off in Settings → Multiworld. When the reconnects give up and the multiworld has its room link, Atlas reads the room's status once (at most every ten minutes, and only if you allowed it): if the room moved to another port, a card offers to use it; if the room went to sleep, a card says so and opens the room page in your browser when you ask, which wakes it.

Atlas connects to the room's server and nothing else for a connection. It never requests a room's web page (which would wake a sleeping room); open the room page yourself when you want to.

**+ Add Slot** opens a small dialog with everything a slot needs: its name as the room knows it; its game, found by typing a few letters (every game Atlas knows, yours first); a YAML to tie to it (which fills in the name and the game, with a choice when it names several players); a line saying whether the game's logic is ready in the Atlas Engine, with **Set up the Atlas Engine** when the engine isn't set up or **Install the newest apworld…** when the game's apworld isn't in it; and **Connect now**, on whenever the multiworld has its server address, which saves the slot and connects it as the dialog closes. The version a seed was made with is known only once the slot connects, so the version picker stays there.

**Add YAML…** (beside + Add Slot) keeps a player YAML in Atlas's YAML folder and adds a slot for each player it names, with the YAML tied to that slot (a name with a placeholder such as `{player}` or `{number}` asks what the slot is called in the room); a one-game player's game is noted for the slot. Save Settings keeps the slots; Don't save drops them (the YAML stays kept). Each slot row has **▸ Details**: its game (from its YAML, its saved stats or its connection), its YAML (Link, Change, Unlink; Keep in Atlas's YAML folder when it lives elsewhere), the map pack variant when its pack has several, the apworld version chosen for its seed (Choose the version… while it's connected), and whether the map follows the game.

## Map Tracker

The Map Tracker (Ctrl+2) shows the selected slot's checks on the game's map, from a PopTracker map pack (see Map Packs). The explorer lists the pack's maps, sorted A–Z or by most checks. Drag to pan and scroll to zoom.

Each pin is coloured the way PopTracker colours it: green when every open check there is in logic, half green and half red when some are (the pin is split down the middle; its tooltip says how many), yellow when only the game's sequence-break (glitch) logic reaches them, red when none is, dark grey when all are checked, and a quiet blue while logic isn't known (the engine isn't running, or race mode hides it; the legend says which). A hinted pin has colours of its own that still say its logic: sky blue when a hinted check there is in logic, violet when none is, lavender while logic isn't known. Settings → Appearance changes any of these colours (Reset puts PopTracker's back; with colour-blind-safe colours on, the defaults are blue, orange and yellow, with bluish green and vermilion for hinted pins) and the pins' shape; a pack that sets its own pin size, border or shape for a map or a pin keeps it. Click a pin and Properties shows its locations, with why each is or isn't in logic when the engine runs. Under Display: the pin size (the wheel never changes it; drag the slider, double-click for 100%), whether excluded locations and locations not in this seed are shown, dimmed or hidden, and Hide checked pins.

Without a pack for the game, the Map Tracker says so and offers **Find a map pack for <game>…**: one search of GitHub for the game's PopTracker packs, when you press, with what it found shown on the Map Packs page for you to choose from (nothing is installed until you do). A chosen pack downloads with a progress line on that page, and a card says when it's installed. A pack you install while the slot is connected shows on its map at once, and a pack you delete leaves it; no reconnect.

**Pack variant:** a pack with several variants (its author's versions of the maps and items, such as a game's modes) shows a Pack variant choice above the display options; the pack's default is used until you pick another for that slot, and the choice is kept per slot. The same choice sits in the slot's details on the Multiworlds page.

**Follow the game's current map:** some packs switch the map to where you are in the game, from what the game's client tells the room (Dark Souls Remastered's, for one, when its client is set to share your location). With such a pack, a switch under Fit turns following on or off for that slot (on to begin with). The pack's scripts may read the room's data storage for this; Atlas never writes the room's data storage for a pack.

- The map scrolls and zooms like a document: the wheel zooms around the cursor, any mouse button drags it, **Fit** (and −, +) are above the display options, and the view is remembered per map. The **legend** under the display options says what the pin colours mean; a pin's tooltip says its state and the checks it covers.

The **Chat** tab's filters sit in one row: Chat, Hints and System (the kinds of line), then Progression, Useful, Filler and Traps (the kinds of item in item lines). The Chat and System Log tabs show a small dot when lines arrived while the tab wasn't showing; showing the tab clears it.

## Key Items

Key Items (Ctrl+3) shows the progression items you've received. The Visual view is the pack's own item grid (when the pack has one), with each tile as the pack's scripts show it; the Text view lists the items. Collected and Missing filter the list, the search box narrows it, and Item Size changes the tiles. Seed settings the pack shows are read from the slot's options.

**Layout:** the choice beside Visual / Text lists the pack's own layouts (PopTracker's default, horizontal, vertical and broadcast, whichever the pack has) and two Atlas builds from the pack's item groups: Vertical (by group) stacks each group under its header, Horizontal (by group) sets the groups side by side. The pack's default layout is used until you pick another; the choice is kept per slot.

## Logic Tracker

While logic can't run, a banner above the list says why and offers the way out: Set up Atlas Engine… when the engine isn't set up, **Open the Games page** when the engine runs but the game's apworld isn't in it (the game's page installs it), Link YAML… when the world needs the player's YAML.

**BK** (nothing to do until someone sends you something): while logic runs and checks remain but none of them is in logic, the Logic Tracker and Key Items say so above their lists, with the count done ("BK: 12 of 40 checks done; nothing is in logic right now. Items from other players open the next ones."), and the slot's card says BK. It goes the moment a check comes into logic.

- Once everything your goal needs is reachable, the Logic Tracker says **GO MODE** above its list, your slot's card says "Go mode!", and Properties shows a Goal row.

The Logic Tracker (Ctrl+4) lists the slot's locations in the order they came into logic: Order, Location and Unlocked By (the item that opened it), from Archipelago's own logic running in the Atlas Engine. Properties explains any location: the items that open it, its access rule and the path to it.

The banner above the table says how far the logic can be trusted. It's exact when the slot's apworld matches the version the seed was made with; when it doesn't, Atlas can find and use that version (Fix automatically, which asks before looking anything up on GitHub and downloads only from sources you trust), or you can give it the apworld file the seed's host shared (Choose apworld file…), the YAML the seed was generated with (Link YAML…), or the GitHub project the apworld comes from (Add a source…).

In race mode the explanations are off, and with Hide all logic the tracker is hidden (see Race mode).

## Item History

Item History (Ctrl+5) lists everything the slot has received in a table like every other: Order, Item, From (who found it) and Location. A click on Order's title puts the newest first (another, the oldest), a click on Item sorts alphabetically; the filters keep or hide progression, useful, filler and trap items, or only the ones you flagged; the search narrows the items, senders and locations, and the items still out there below it, by classification and quantity.

## Hints

Hints (Ctrl+6) lists the slot's hints in one table: Status, Item, For, Location, In World Of, Entrance and In Logic. My Items shows hints for items this slot will receive, My Locations hints for items hidden in this slot's world; Show Found includes hints whose item has been found, and Flagged / special only keeps the ones you've flagged or marked special. The search box narrows the table.

A hint's status (Priority, No priority, Avoid, Unspecified) can be changed from the table for hints you own, as the Archipelago text client would. The bar at the bottom asks the server for a hint: Item (where is one of my items) or Location (what is at one of my locations), with the name completed as you type. New hints show as a toast.

## Cheese Tracker

Cheese Tracker is the community tracker for async multiworlds. The Cheese Tracker tool (Ctrl+8) shows your multiworlds' trackers the way the site does: sortable columns, filters by status, availability, owner, game and text, "mine first", and a status summary, with the selected slot's notes, hints and actions below. The explorer lists My slots (yours in every linked multiworld), each multiworld, and the settings.

Reading a tracker needs nothing. To change statuses, claim slots and edit notes as you, Atlas needs your API key from Cheese Tracker's settings page; it's stored encrypted for your Windows account and sent only to the site. Atlas reads a linked tracker every 10 minutes while one of its slots is connected or the tool shows it, and right before any change, so a change never overwrites someone else's newer edit.

Atlas suggests BK, Unblocked or Go mode from its logic, and changes a slot automatically only when you turn that on for a slot claimed by your account. It never touches Soft BK, Goal, Done or Forfeit, makes at most one change every 5 minutes, pauses if anyone else changes the status, and never changes a slot claimed by someone else.

## Sphere Tracker

The Sphere Tracker (Ctrl+9) shows the spheretracker.de room a multiworld's host created and shared, for one slot at a time: the slot's open locations with their spheres, its earliest open sphere, and the multiworld's earliest. The room's other tables are there as the page has them. Link the host's room to the multiworld in the tool's settings, or in the Sphere Tracker box of the Multiworlds page; Atlas reads it, and never creates rooms of its own.

Spheres give away the seed's structure, so nothing of this shows in race mode.

## Games

The Games page (the gamepad on the activity bar) lists every game Atlas knows, opening with **In your multiworlds**: the games of every slot in your multiworlds (from their saved stats and their linked YAMLs), each listed once. The rest come in three groups: **Official** (they ship with Archipelago, so the Atlas Engine has them), **Community** (from the community index) and **Added by you** (projects you added, and apworlds you installed that the index doesn't know). Type a few letters to narrow the list; sort it by name, by what's ready, or by what you played last. ✔ means the game is in the engine with a map pack; ● in the engine; ○ not yet.

A game's page walks its setup through in one place, each step ticked as Atlas finds it done:

- **The apworld in the Atlas Engine:** Install (or Update) downloads the newest full release from the game's projects, after you trust the project once, checks it against its published SHA-256 and installs it; Choose a file installs one you have; Copy from my install takes the game's apworld from the Archipelago install you chose (its two world folders, nothing else). Check this game rebuilds it with default options and computes its starting logic.
- **Every version, every project:** under the apworld step, a table lists every release of the game's apworld from every project Atlas knows (the community index's, the one your installed copy came from, the ones you added), newest first, each naming its project and when it was released, pre-releases marked and the installed one highlighted; a line above says the newest known full release and where it's from. Once you've allowed GitHub lookups, Atlas reads the projects' releases by itself when you open a game's page, and once per game looks for other projects of the same name (forks and re-uploads a seed's host may have taken a newer version from), so the version a host used shows even when the index points at an older project. Nothing is downloaded until you press: tick a row's check box (or click the row) and **Install the selected version** installs it (a pre-release too, if that's what the host used), **Find other projects…** searches again, **Add a project…** takes a link. "Look up releases on GitHub" is offered until the permission is given.
- **A map pack:** "Search GitHub for a pack…" looks for the game's PopTracker packs (one search, when you press) and shows what it found on the Map Packs page, where a chosen pack downloads with a progress line; the Map Packs page also installs a zip you have.
- **Your YAML:** Add YAML keeps a copy in Atlas's YAML folder. A YAML that can roll several games is listed under each of them. Link it to a slot that plays the game in one click; most games don't need one.
- **Files its setup needs:** some games need a client, a patcher or a .bat from their release. "Get from a release" first takes the release (the version a connected slot's seed was made with is chosen first, else the newest full release), then lists its other files with a check box each (Select all, Select none); the ones you tick are downloaded into the game's own folder (checked against their published SHA-256 when there is one), and the page says which release each file came from. A downloaded YAML offers "Add as YAML". Atlas never runs them: the game's setup guide says what to do with each.
- **The link at the bottom** says where it leads: most community games' page is their thread in the Archipelago Discord ("Discord thread ↗"; join the Archipelago Discord first, and if the thread doesn't open, tick the game's channel under the server's Channels & Roles), a project's repository ("Project page ↗"), or the game's page on archipelago.gg ("Setup guide ↗").
- **Add YAML…** has a ▾ with other places to take a YAML from: this game's downloaded files, your Archipelago install's Players folder (when the install is known), and Atlas's own YAML folder.

Under **Your slots**, each connected slot says which version it should run: that the installed apworld matches its seed; that it runs the version chosen for it; that Atlas has the version its seed was made with (**Use it**); or that the seed's data isn't among the known versions, with **Choose the version…** (also on the Logic Tracker's banner): it lists the game's releases newest first, marks the ones Atlas has and the one known to match the seed, and lets you use the version the host used, let Atlas try them one by one (it says how many it may download, stops at the first match, and you can stop it), choose the host's file, or add the host's project. The choice is kept for that slot and its seed; only that slot's logic uses it, and your own Archipelago isn't changed. Atlas doesn't download versions by itself. Every release Atlas downloads is kept in the game's folder, so each multiworld can run the version its seed needs.

The overview (no game selected) also holds the community index Atlas ships (how many games, when it was built) and a box for a newer list's URL, such as a maintained fork of the index.

## Map Packs and the Pack Doctor

Map Packs lists the PopTracker packs in Atlas's packs folder (`PortableData/packs`, as zips) and installs new ones from a zip; when you ask (its own button for the connected games, "Find a map pack…" on a slot's empty map, or "Search GitHub for a pack…" on a game's page), it looks for a game's packs on GitHub and shows what it found, with nothing installed until you choose. A chosen pack's download shows its progress on the page ("Downloading <pack>: 3 of 11 MB"), the results window closes by itself, and a card says when the pack is installed; connected slots of its game show it at once. A pack is read for its structure only; its images are decoded while a slot or the Pack Doctor uses it. A pack row whose check left things to decide shows **Review N**, which opens the Pack Doctor on its Recommended tab (its Overview when nothing is suggested).

The **Pack Doctor** checks a pack against the game's real Archipelago names and reports what's wrong, what Atlas fixed on its own, and what you should decide, with suggestions. On a pack's first check, a tile or pin whose name matches an item or location exactly (ignoring case and punctuation) is linked for you, as a fix marked automatic that Undo or Your fixes can reverse (Settings → Tools turns this off); the Recommended tab lists what's left, with a confidence level and a check box per row: Apply (or Apply N selected) writes the fixes, the pack is checked again, and the status line says what was applied and whether anything still shows. A grid cell whose code no pack item defines becomes a tile once you link it. You can fix a pack locally: Key Items tiles, which location a pin shows, pin positions, added pins and map images, with undo and a per-fix reset, and a report ready to send to the pack's author. Fixes live in Atlas's own folder (`PortableData/pack_fixes`), never in the pack's zip. The doctor runs when a pack is installed, updated or first used, and when you open it from the page.

A pack's own scripts run in a sandbox with no file, network or OS access, and under limits on how much work one step may do; a runaway script is stopped and said so, and Atlas carries on.

## The Atlas Engine

Logic, maps and hints need Archipelago's own code. The Atlas Engine is Atlas's portable copy: Python and Archipelago, in Atlas's own folder, each download pinned to an exact version and checked against a fixed hash.

**Setting it up** is one button: Set up on Home (or on a slot's "logic needs the engine" card, or Settings → Tools). Atlas asks once for the download (Allow once, Always allow or Don't allow; Settings → Privacy & permissions keeps the answer), then a small panel says what it's doing in plain words ("Step 3 of 9: Downloading Archipelago…") with one bar for the whole setup, which never goes back, and Cancel. It ends with Ready, or with what went wrong and Try again; Show details opens the full engine window with the setup's own log.

**The engine window** (Tools → Atlas Engine, or the bar's bottom button) is for managing the engine once it works: each part with its version, Set up everything, a seed test, and each slot's state and YAML. Games are set up on the Games page (the window points there). Its log (what Python and pip print) stays hidden behind Show log until you want it, for a problem report (Copy log says "Log copied." when it has). The window opens as a window of its own, centred on Atlas's screen the first time and where you last left it after that, as does the Pack Doctor's.

**When a part of the engine changes** (an apworld added or updated, Archipelago or its packages updated, a new Universal Tracker), every connected slot whose logic runs on it restarts its logic on the new parts by itself; a card says which slots and which parts. There's nothing to reconnect. The Slots section offers Link YAML only when logic would be helped by one (the engine said it can't rebuild the world from the server's data, or the rebuilt world differs from the server's); a linked YAML can be changed or unlinked there.

Atlas can run logic on your own Archipelago install instead. That needs your consent: the engine window shows exactly what Atlas would add (its bridge apworld in the install's worlds folder) before anything is written, and Remove Atlas's files undoes it.

When a seed was made with another version of a game's apworld, Atlas finds that version and uses it for that slot, after asking before it looks anything up on GitHub, and downloading only from sources you've trusted (Settings → Privacy & permissions lists them). A multiworld's slots share a small pool of engine processes.

## Updates

Atlas keeps itself up to date only with your say. Under Settings → Updates, **Check for a newer Atlas daily** asks your permission the first time (one small request to GitHub a day at most, for the list of Atlas's releases; an unchanged list is confirmed rather than read again), and **Check now** does the same by hand (so does Help → Check for Updates). Atlas offers the daily check once, on its second start, and never checks on the first. **Versions** picks stable releases only, or betas too; Automatic follows the version you run.

When a newer version of your channel exists, a card says so and **See what's new** shows the release's notes with the choice to download it. The download is checked against the release's SHA256SUMS.txt and refused if it doesn't match; it's unpacked into Atlas's data folder, never into the program's. **Restart to update** closes your connections, moves the installed files aside and the new ones in (your data folder isn't touched), and starts the new Atlas while the version you had watches it start from its place under `PortableData/updates/previous`: if the new Atlas doesn't bring up its window, it's put back and tells you so. The previous version stays until the next update, and **Go back** under Settings → Updates restores it the same way. A folder Atlas can't write to, or a data folder on a different drive from the program's, is said, with the releases page to update by hand.

## Settings

Settings (Ctrl+,) holds every setting in sections the explorer lists, and a search box that finds a setting by any word of its name, description or section: Multiworld (automatic reconnects, each seed's apworld version, race mode), Appearance (the theme, the colours, the zoom, the font sizes, the map's pins), Behaviour (what Atlas opens on), Window (which parts show, the bottom pane's tabs), Tools (the engine, Cheese Tracker and Sphere Tracker settings), Privacy & permissions, Keyboard, Advanced (developer mode) and Data (Atlas's data folder). Each setting applies and is saved as you change it.

Under **Appearance**: the theme (Follow Windows, Dark, Light, High contrast; a change restyles the window at once and reaches everything after a restart); colour-blind-safe colours (blue for in logic, connected and done, orange for out of logic and errors, with the hint and item kinds told apart the same way, so nothing rests on red against green; also after a restart); the accent colour, a preset or any colour you pick (headings and links are lightened or darkened as needed, so they always read); the zoom of the whole window, relative to Windows' display scale, so 100% is the size Windows gives every other app (also Ctrl+= and Ctrl+-, and Ctrl+0 to go back to 100%); a text size for each part of the window; the shape of the Map Tracker's pins (round, square or diamond); and the pins' colours, one for each state (PopTracker's by default, with a Reset).

Under **Behaviour**: what Atlas opens on: Home, the tool you had open when it closed, or Multiworlds.

Under **Updates**: the daily check (with its permission), the versions you take (stable or beta), Check now, See what's new, Restart to update and Go back (see Updates).

Under **Advanced**: developer mode, which shows Atlas's diagnostics: the Debug Log tab, frame hitch warnings on the status bar and the Debug Log's menu items. Everything is written to the log file either way.

## Keyboard shortcuts

Every command's key is listed under Help → Keyboard Shortcuts (F1) and can be changed in Settings → Keyboard: press the key's button, then the key you want; Backspace means no key, Escape keeps it, and a reset brings Atlas's key back. The defaults:

| Key | Does |
|---|---|
| Ctrl+1 … Ctrl+9 | Home, Map Tracker, Key Items, Logic Tracker, Item History, Hints, Multiworlds, Cheese Tracker, Sphere Tracker |
| Ctrl+, | Settings |
| Ctrl+=, Ctrl+-, Ctrl+0 | Zoom in, zoom out, back to Windows' size |
| Ctrl+Tab, Ctrl+Shift+Tab | The next and the previous connected slot |
| Ctrl+Shift+P | The command palette |
| Ctrl+N | A new multiworld |
| F9, F11 | Focus mode, full screen |
| F1 | Keyboard shortcuts |
| Alt+Left, Alt+Right | Back and forward in Properties |
| Ctrl+Shift+C | Copy everything Properties shows |

## Race mode

Some races and community events limit which tracker features are allowed. Race mode makes Atlas follow those limits. It follows the room by itself (on in race rooms); Multiworld → Race Mode… sets it always on or off instead, and Hide all logic hides more.

When race mode is on for a slot, Properties never asks the engine why a location is or isn't in logic (no "opens with" items, access rules or region paths), and the Sphere Tracker shows nothing. With Hide all logic, the Logic Tracker is hidden, map pins show open, hinted and checked only, and hints, slot cards and Properties show no in-logic information. Always available: items, checks, hints you paid for, chat, notes and flags.

## Privacy and permissions

Atlas never looks or writes outside its own folder, or goes online for something new, without asking: Allow once, Always allow or Don't allow, at the moment it comes up, with exactly what would happen spelled out. Settings → Privacy & permissions lists every permission Atlas can ask for with its state, every answer it keeps and every trusted apworld source, each with a button to take it back, and where Atlas goes online. Nothing searches your PC at startup.

Online, Atlas reaches the archipelago.gg rooms you connect to; Cheese Tracker (your instance) and spheretracker.de (the host's room) for the multiworlds you link; GitHub for apworld releases, when you allow it; and python.org, pypa.io and PyPI when you set up the Atlas Engine (a permission of its own, asked before the first download). Every request is polite: capped, backed off when a site asks, and never repeated to wake a sleeping room.

**Crash reports.** When Atlas has had a problem it couldn't recover from, the next start offers to send a report to Atlas's developer, through Sentry, a crash-reporting service. See the report shows the whole report exactly as it would be sent: what the code was doing, Atlas's version and the kind of PC, and nothing more; names, paths, servers, slots, chat and log lines are never included, and you can add a note. Send once sends this one, Always send keeps the permission (take it back here), Don't send keeps the report on your PC only. A build without a report address offers nothing.

## Accessibility

- **Keyboard:** Tab and Shift+Tab move between controls, and a ring in your accent colour shows which one has the focus; Enter or Space presses a button; Escape closes a dialog or a menu; the arrow keys move in lists and tables. Every command has a key you can change (Settings → Keyboard), and Ctrl+Shift+P finds any command by name.
- **Screen readers:** Atlas names its controls for Windows's screen readers (Narrator, NVDA, JAWS), which Godot connects to by itself when one is running; a button that shows only a symbol says what it does.
- **Colour and size:** no state is shown by colour alone: it's also said in words or a mark. High contrast and colour-blind-safe colours, any accent, the zoom and a text size for each part of the window are under Settings → Appearance.

## Troubleshooting

- **The logs:** the System Log tab shows what Atlas did; the Debug Log (developer mode, under Settings → Advanced) its diagnostics. The log files are in `PortableData/logs` (File → Open Atlas's Data Folder), and a crash leaves a report there too.
- **Logic says "needs the engine":** set up the Atlas Engine (Home, or the bar's bottom button).
- **"Atlas's folder is too deep for Windows":** Windows limits a file's path to 259 characters, and the engine's packages go a few folders deep below Atlas. Atlas checks before it downloads anything and says so instead of failing partway. Move the whole Atlas folder (with `PortableData`) to a shorter path, such as `C:\Games\Atlas`, and set the engine up again.
- **A pack looks wrong for the game:** open it in the Pack Doctor from Map Packs; it says what it found and lets you fix it locally.
- **A slot won't reconnect:** after a few tries over about 20 minutes Atlas stops on purpose. If the multiworld has its room link (and you allowed the status read), a card offers the room's new port or says the room is asleep; otherwise connect the slot again from the Multiworlds page, after updating the server address if the room's port changed.
- **Everything Atlas keeps** (multiworlds, settings, logs, packs, fixes, the engine) is in `PortableData`, next to Atlas; nothing is written anywhere else.

**Report a problem.** Help → Report a Problem writes a zip in `PortableData/reports`: the end of the log, the update log, the newest crash reports and the system information, with paths, web and e-mail addresses, server and slot names replaced by marks, and a note saying what it holds. Atlas uploads nothing: open the zip, check it, then attach it to an issue on GitHub (the dialog opens the folder and the issues page).

**Atlas is in a folder it can't write to.** Unzipped into Program Files, or another folder Windows protects, Atlas can't keep your data next to itself. It asks once where to keep it: your local app data folder, or a folder you choose, which it remembers in a one-line file (`location.txt`) in your local app data folder. Nothing is written until you choose, and Quit writes nothing. Settings → Data says which folder is in use. To move back next to the program, unzip Atlas into a folder of your own (Documents, say) and copy `PortableData` there.
