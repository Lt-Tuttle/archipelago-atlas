# The Archipelago Atlas: the guide

Atlas is a desktop companion for [Archipelago](https://archipelago.gg) multiworlds: your slots' maps, logic, hints and items, and the trackers your group uses, in one window. This guide is built into Atlas (Help → Guide) and lives in the repository as `docs/GUIDE.md`; both are the same text.

Atlas is an unofficial community tool: it isn't affiliated with or endorsed by the Archipelago project.

## Home

Atlas opens on Home (Ctrl+8) unless Settings → Behaviour → Open on says otherwise. Its header carries Atlas's icon and wordmark, which take the theme's colours and your accent. It has:

- **Getting started:** the steps to a working Atlas, each with a tick once it's done and a button that does it: set up the Atlas Engine, add a multiworld, connect a slot, install a map pack, and link Cheese Tracker if your multiworld uses it.
- **Your multiworlds:** the ones you've played most recently, each with a Connect button that connects every slot of it in turn.
- **Tools:** a card per tool.
- A tip, which changes each time Home shows.
- **Links** to Archipelago's own pages and to the community's tools. Links open in your browser when you click them; Atlas never fetches them by itself.

## The window

- The **activity bar** on the far left holds every tool in three groups: the slot tools (Map Tracker, Key Items, Logic Tracker, Item History, Hints), the multiworld tools (Cheese Tracker, Sphere Tracker) and Atlas's own pages (Home, Multiworlds, Map Packs, Settings), with the Atlas Engine's button at the bottom. Ctrl+1 to Ctrl+9 open the tools in the bar's order; Map Packs is Ctrl+0 and Settings Ctrl+, (comma).
- The **Slots panel** lists every connected slot as a card with its status. Click a card to make that slot the one the slot tools show. Ctrl+Tab and Ctrl+Shift+Tab go through the connected slots, and the picker in the tool header does the same.
- The **explorer**, beside the content, is the tool's own list: the maps of a pack, the multiworlds, the packs, the Settings sections.
- **Properties**, on the right, shows everything about whatever you last selected anywhere: a location, an item, a player, a hint, a map, a pack or a multiworld. It keeps a history (◀ ▶, also Alt+Left and Alt+Right), ⌂ shows the selected slot's summary, and ⧉ (Ctrl+Shift+C) copies everything shown.
- The **bottom pane** holds Chat (the text client: the room's messages, and a line to type commands such as `!hint`), the System Log (what Atlas did, and any trouble it met) and, in developer mode (Settings → Advanced), the Debug Log (Atlas's own diagnostics). The status bar under it says what Atlas is doing and how many slots are connected.
- The **View** menu shows or hides each part, and remembers it. **Focus mode** (F9) leaves the content and its header alone, with the menu bar; F9 again brings everything back. F11 is full screen.
- The **command palette** (Ctrl+Shift+P) lists every command in the menus: type a few letters of its name and press Enter.
- **Tables** (Hints, Cheese Tracker, Sphere Tracker, Notifications, the shortcuts list) all work the same way: click a column's title to sort by it (again to turn the order; the arrow says which), right-click a title to hide or show columns, type words in the search box to narrow the rows (each word must start a word of a cell), and use Export to copy the rows shown as TSV for a spreadsheet, as a Markdown table, as Discord messages (split to fit), or to save them as a CSV file. Right-click a row for its actions and Copy row. The sort and the hidden columns are remembered per table.

## Multiworlds

The Multiworlds page (Ctrl+9) holds your multiworlds: each has a name, a server address (as archipelago.gg shows it, for example `archipelago.gg:38281`), a password if the room has one, an optional Cheese Tracker link (the tracker's page, or the archipelago.gg room link), and the slots you play in it. Passwords are stored encrypted for your Windows account.

Connect a slot from its row, or every slot with Connect All Slots; Home's Connect does the same. A connected slot stays connected until you disconnect it or close Atlas. A dropped connection reconnects by itself: a few tries over about 20 minutes, then Atlas stops, so a closed room is never kept busy. You can turn automatic reconnects off in Settings → Multiworld.

Atlas connects to the room's server and nothing else for a connection. It never requests a room's web page (which would wake a sleeping room); open the room page yourself when you want to.

## Map Tracker

The Map Tracker (Ctrl+1) shows the selected slot's checks on the game's map, from a PopTracker map pack (see Map Packs). The explorer lists the pack's maps, sorted A–Z or by most checks. Drag to pan and scroll to zoom.

Each pin is coloured by what Atlas knows: checked, hinted, in logic, or not in logic (its shape, round, square or diamond, is under Settings → Appearance). Click a pin and Properties shows its locations, with why each is or isn't in logic when the engine runs. Under Display: the pin size, whether excluded locations and locations not in this seed are shown, dimmed or hidden, and Hide checked pins.

Without a pack for the game, the Map Tracker says so and points you to Map Packs.

- The map scrolls and zooms like a document: the wheel zooms around the cursor, any mouse button drags it, **Fit** (and −, +) are above the display options, and the view is remembered per map. The **legend** under the display options says what the pin colours mean; a pin's tooltip says its state and the checks it covers.

## Key Items

Key Items (Ctrl+2) shows the progression items you've received. The Visual view is the pack's own item grid (when the pack has one), with each tile as the pack's scripts show it; the Text view lists the items. Collected and Missing filter the list, the search box narrows it, and Item Size changes the tiles. Seed settings the pack shows are read from the slot's options.

## Logic Tracker

- Once everything your goal needs is reachable, the Logic Tracker says **GO MODE** above its list, your slot's card says "Go mode!", and Properties shows a Goal row.

The Logic Tracker (Ctrl+3) lists the slot's locations in the order they came into logic: Order, Location and Unlocked By (the item that opened it), from Archipelago's own logic running in the Atlas Engine. Properties explains any location: the items that open it, its access rule and the path to it.

The banner above the table says how far the logic can be trusted. It's exact when the slot's apworld matches the version the seed was made with; when it doesn't, Atlas can find and use that version (Fix automatically, which asks before looking anything up on GitHub and downloads only from sources you trust), or you can give it the apworld file the seed's host shared (Choose apworld file…), the YAML the seed was generated with (Link YAML…), or the GitHub project the apworld comes from (Add a source…).

In race mode the explanations are off, and with Hide all logic the tracker is hidden (see Race mode).

## Item History

Item History (Ctrl+4) lists everything the slot has received: Order, Item, From (who found it) and Location, sorted oldest or newest first or alphabetically, with a search over items, senders and locations. Below it, the items still out there, by classification and quantity.

## Hints

Hints (Ctrl+5) lists the slot's hints in one table: Status, Item, For, Location, In World Of, Entrance and In Logic. My Items shows hints for items this slot will receive, My Locations hints for items hidden in this slot's world; Show Found includes hints whose item has been found, and Flagged / special only keeps the ones you've flagged or marked special. The search box narrows the table.

A hint's status (Priority, No priority, Avoid, Unspecified) can be changed from the table for hints you own, as the Archipelago text client would. The bar at the bottom asks the server for a hint: Item (where is one of my items) or Location (what is at one of my locations), with the name completed as you type. New hints show as a toast.

## Cheese Tracker

Cheese Tracker is the community tracker for async multiworlds. The Cheese Tracker tool (Ctrl+6) shows your multiworlds' trackers the way the site does: sortable columns, filters by status, availability, owner, game and text, "mine first", and a status summary, with the selected slot's notes, hints and actions below. The explorer lists My slots (yours in every linked multiworld), each multiworld, and the settings.

Reading a tracker needs nothing. To change statuses, claim slots and edit notes as you, Atlas needs your API key from Cheese Tracker's settings page; it's stored encrypted for your Windows account and sent only to the site. Atlas reads a linked tracker every 10 minutes while one of its slots is connected or the tool shows it, and right before any change, so a change never overwrites someone else's newer edit.

Atlas suggests BK, Unblocked or Go mode from its logic, and changes a slot automatically only when you turn that on for a slot claimed by your account. It never touches Soft BK, Goal, Done or Forfeit, makes at most one change every 5 minutes, pauses if anyone else changes the status, and never changes a slot claimed by someone else.

## Sphere Tracker

The Sphere Tracker (Ctrl+7) shows the spheretracker.de room a multiworld's host created and shared, for one slot at a time: the slot's open locations with their spheres, its earliest open sphere, and the multiworld's earliest. The room's other tables are there as the page has them. Link the host's room to the multiworld in the tool's settings; Atlas reads it, and never creates rooms of its own.

Spheres give away the seed's structure, so nothing of this shows in race mode.

## Map Packs and the Pack Doctor

Map Packs (Ctrl+0) lists the PopTracker packs in Atlas's packs folder (`PortableData/packs`, as zips) and installs new ones from a zip; when you ask, it looks for a game's packs on GitHub and shows what it found. A pack is read for its structure only; its images are decoded while a slot or the Pack Doctor uses it.

The **Pack Doctor** checks a pack against the game's real Archipelago names and reports what's wrong, what Atlas fixed on its own, and what you should decide, with suggestions. You can fix a pack locally: Key Items tiles, which location a pin shows, pin positions, added pins and map images, with undo and a per-fix reset, and a report ready to send to the pack's author. Fixes live in Atlas's own folder (`PortableData/pack_fixes`), never in the pack's zip. The doctor runs when a pack is installed, updated or first used, and when you open it from the page.

A pack's own scripts run in a sandbox with no file, network or OS access, and under limits on how much work one step may do; a runaway script is stopped and said so, and Atlas carries on.

## The Atlas Engine

Logic, maps and hints need Archipelago's own code. The Atlas Engine is Atlas's portable copy: Python and Archipelago, in Atlas's own folder, each download pinned to an exact version and checked against a fixed hash. Set up everything (from the engine window, the bar's bottom button, or Home) downloads what's missing and runs a health check; Test every game rebuilds each installed game.

Atlas can run logic on your own Archipelago install instead. That needs your consent: the engine window shows exactly what Atlas would add (its bridge apworld in the install's worlds folder) before anything is written, and Remove Atlas's files undoes it.

When a seed was made with another version of a game's apworld, Atlas finds that version and uses it for that slot, after asking before it looks anything up on GitHub, and downloading only from sources you've trusted (Settings → Privacy & permissions lists them). A multiworld's slots share a small pool of engine processes.

## Settings

Settings (Ctrl+,) holds every setting in sections the explorer lists, and a search box that finds a setting by any word of its name, description or section: Multiworld (automatic reconnects, each seed's apworld version, race mode), Appearance (the theme, the colours, the zoom, the font sizes, the map's pins), Behaviour (what Atlas opens on), Window (which parts show, the bottom pane's tabs), Tools (the engine, Cheese Tracker and Sphere Tracker settings), Privacy & permissions, Keyboard, Advanced (developer mode) and Data (Atlas's data folder). Each setting applies and is saved as you change it.

Under **Appearance**: the theme (Follow Windows, Dark, Light, High contrast; a change restyles the window at once and reaches everything after a restart); colour-blind-safe colours (blue for in logic, connected and done, orange for out of logic and errors, with the hint and item kinds told apart the same way, so nothing rests on red against green; also after a restart); the accent colour, a preset or any colour you pick (headings and links are lightened or darkened as needed, so they always read); the zoom of the whole window (also Ctrl+= and Ctrl+-, and Reset Zoom in the View menu); a text size for each part of the window; and the shape of the Map Tracker's pins (round, square or diamond).

Under **Behaviour**: what Atlas opens on: Home, the tool you had open when it closed, or Multiworlds.

Under **Advanced**: developer mode, which shows Atlas's diagnostics: the Debug Log tab, frame hitch warnings on the status bar and the Debug Log's menu items. Everything is written to the log file either way.

## Keyboard shortcuts

Every command's key is listed under Help → Keyboard Shortcuts (F1) and can be changed in Settings → Keyboard: press the key's button, then the key you want; Backspace means no key, Escape keeps it, and a reset brings Atlas's key back. The defaults:

| Key | Does |
|---|---|
| Ctrl+1 … Ctrl+7 | Map Tracker, Key Items, Logic Tracker, Item History, Hints, Cheese Tracker, Sphere Tracker |
| Ctrl+8, Ctrl+9, Ctrl+0, Ctrl+, | Home, Multiworlds, Map Packs, Settings |
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

Online, Atlas reaches the archipelago.gg rooms you connect to; Cheese Tracker (your instance) and spheretracker.de (the host's room) for the multiworlds you link; GitHub for apworld releases, when you allow it; and python.org, pypa.io and PyPI when you set up the Atlas Engine. Every request is polite: capped, backed off when a site asks, and never repeated to wake a sleeping room.

## Troubleshooting

- **The logs:** the System Log tab shows what Atlas did; the Debug Log (developer mode, under Settings → Advanced) its diagnostics. The log files are in `PortableData/logs` (File → Open Atlas's Data Folder), and a crash leaves a report there too.
- **Logic says "needs the engine":** set up the Atlas Engine (Home, or the bar's bottom button).
- **A pack looks wrong for the game:** open it in the Pack Doctor from Map Packs; it says what it found and lets you fix it locally.
- **A slot won't reconnect:** after a few tries over about 20 minutes Atlas stops on purpose. Connect it again from the Multiworlds page; if the room's port changed, update the server address.
- **Everything Atlas keeps** (multiworlds, settings, logs, packs, fixes, the engine) is in `PortableData`, next to Atlas; nothing is written anywhere else.
