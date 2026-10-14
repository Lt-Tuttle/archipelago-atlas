#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AP_Atlas.Core.PopTracker
{
    public enum FindingSeverity
    {
        /// <summary>Worth knowing; nothing to do.</summary>
        Info,
        /// <summary>Atlas handled it automatically.</summary>
        AutoFixed,
        /// <summary>Probably wrong; review recommended.</summary>
        Warning,
        /// <summary>Something is broken for the user.</summary>
        Problem,
    }

    [Flags]
    public enum FindingActions
    {
        None = 0,
        LinkItem = 1,         // pick the AP item a tile tracks
        LinkLocation = 2,     // pick the AP location a pin section shows
        AddImage = 4,         // give a tile an image
        ToggleGrid = 8,       // show/hide a grid
        AddPin = 16,          // place a pin for an AP location
        ReplaceMapImage = 32, // replace a map background
        HideTile = 64,        // hide a tile
        RestoreFix = 128,     // put a set-aside fix back
        ResetFixes = 256,     // undo the user's fixes listed in RelatedSubjects
    }

    public sealed class Suggestion
    {
        public string Label = "";
        public long Id;
        /// <summary>0..1, how sure the match is.</summary>
        public double Score;
        /// <summary>Why it counts as sure when the names aren't the same ("the only 'Firelink Armor' in FS"); null for a plain score.</summary>
        public string Reason;
    }

    public sealed class Finding
    {
        /// <summary>Stable key, used to remember "ignore".</summary>
        public string Key = "";
        public string Category = "";
        public FindingSeverity Severity;
        public string Title = "";
        public string Detail = "";
        /// <summary>The thing it's about (fix subject), e.g. "tile:abyss", "link:Pin|Section", "map:RC".</summary>
        public string Subject = "";
        public FindingActions Actions;
        public List<Suggestion> Suggestions = new List<Suggestion>();
        /// <summary>The name the suggestions were made for (the pin section's, the tile's).</summary>
        public string Query = "";
        /// <summary>For grouped findings: the individual entries.</summary>
        public List<string> Details = new List<string>();
        /// <summary>Fix subjects the finding is about (for ResetFixes).</summary>
        public List<string> RelatedSubjects = new List<string>();
        public bool Ignored;
    }

    public sealed class DoctorReport
    {
        public LoadedPack Pack;
        public GameNameTable Names;
        public PackIndex Index;
        public List<Finding> Findings = new List<Finding>();

        public int TilesTotal, TilesLinked, TilesByScript;
        /// <summary>Grid cells that aren't items to link: labels and pictures ("static" items), and the pack's own switches.</summary>
        public int TileDecorations, TileSwitches;
        /// <summary>Tiles a check sets (named by the location mapping) and tiles the pack's own scripts set: not items to link either.</summary>
        public int TilesByCheck, TilesScripted;
        /// <summary>Tiles of the pack's own display grids (an enemizer table): none names an item of the game while other grids do; not items to link.</summary>
        public int TileDisplay;
        /// <summary>Grid codes that items the pack's scripts make (CreateLuaItem) answer to: not undefined cells.</summary>
        public HashSet<string> ScriptItemCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int SectionsTotal, SectionsLinked, SectionsByScript, SectionsByName, SectionsLoose, SectionsByFix;
        public int ApLocationsTotal, ApLocationsPlaced;
        /// <summary>How Atlas's pack lab measured this very file (by its SHA-256): pin sections linked in percent and the counts, or null when the game list doesn't know it.</summary>
        public int? LabFit;
        public string LabChecks;
        /// <summary>The locations of the seed a connected slot of this game plays (empty when none is connected): the Recommended tab puts their rows first.</summary>
        public HashSet<long> SeedLocationIds = new HashSet<long>();
        /// <summary>Game locations no pin holds, by name (the first forty, sorted), for reports.</summary>
        public List<string> UnplacedSample = new List<string>();

        /// <summary>Seed settings as the pack's scripts read them from the latest saved slot data (empty if none).</summary>
        public List<PackScriptHost.SettingInfo> Settings = new List<PackScriptHost.SettingInfo>();
        public bool ScriptsRan;
        /// <summary>Which slot's saved options were used ("SlotName, 2026-10-03 14:15").</summary>
        public string SlotDataSource;

        /// <summary>Findings that need the user (warnings and problems not ignored).</summary>
        public IEnumerable<Finding> NeedsReview => Findings.Where(f => !f.Ignored && f.Severity >= FindingSeverity.Warning);
    }

    /// <summary>
    /// Checks a map pack against the game's real item and location names and reports what's wrong, what Atlas
    /// fixed on its own, and what the user should decide, with ranked suggestions. <see cref="Prepare"/> reads its
    /// inputs on the main thread; <see cref="Analyze"/> then works only from them, so it can run on a worker thread.
    /// </summary>
    public static class PackDoctor
    {
        /// <summary>
        /// What the analysis reads from the shared stores, read on the main thread by <see cref="Prepare"/>: PackFixes and
        /// GameNames aren't thread-safe, and the effective pack can load the user's fixed images (textures).
        /// </summary>
        public sealed class Inputs
        {
            public LoadedPack Original { get; init; }
            public LoadedPack Effective { get; init; }
            /// <summary>A copy of the pack's fixes: an edit made during the analysis can't change them under it.</summary>
            public PackFixFile Fixes { get; init; }
            public GameNameTable Names { get; init; }
            public int ItemNameDiffs { get; init; }
            public int LocationNameDiffs { get; init; }
        }

        /// <summary>Reads what <see cref="Analyze"/> needs. Main thread only.</summary>
        public static Inputs Prepare(LoadedPack original, GameNameTable names)
        {
            var fixes = PackFixes.Get(PackFixes.KeyFor(original));
            var (itemDiffs, locDiffs) = names == null ? (0, 0) : GameNames.CompareSources(names.Game);
            return new Inputs
            {
                Original = original,
                Effective = PackFixes.Effective(original),
                Fixes = Newtonsoft.Json.JsonConvert.DeserializeObject<PackFixFile>(Newtonsoft.Json.JsonConvert.SerializeObject(fixes)),
                Names = names,
                ItemNameDiffs = itemDiffs,
                LocationNameDiffs = locDiffs
            };
        }

        /// <summary>Analyses prepared inputs. Pure: it reads only them, so it's safe on a worker thread.</summary>
        public static DoctorReport Analyze(Inputs inputs, DataManager.SavedSlotData slotData = null)
        {
            var original = inputs.Original;
            var names = inputs.Names;
            var effective = inputs.Effective;
            var fixes = inputs.Fixes;
            var report = new DoctorReport { Pack = effective, Names = names };
            var ignored = new HashSet<string>(fixes.Ignored);

            var locNames = names?.LocationsById() ?? new Dictionary<long, string>();
            var itemNames = names?.ItemsById() ?? new Dictionary<long, string>();
            var index = new PackIndex(effective, locNames, itemNames);
            PackFixes.ApplyLinks(index, fixes);
            report.Index = index;

            void Add(Finding f)
            {
                f.Ignored = ignored.Contains(f.Key);
                report.Findings.Add(f);
            }

            // ---- Names ----
            if (names == null)
            {
                Add(new Finding
                {
                    Key = "names:none",
                    Category = "Names",
                    Severity = FindingSeverity.Problem,
                    Title = "No Archipelago name list for this game yet",
                    Detail = $"Connect a {original.Manifest?.GameName} slot once, or set your Archipelago install path (Settings), so the Doctor can check the pack against the game's real items and locations."
                });
            }
            else
            {
                var (itemDiffs, locDiffs) = (inputs.ItemNameDiffs, inputs.LocationNameDiffs);
                if (itemDiffs + locDiffs > 0)
                {
                    Add(new Finding
                    {
                        Key = "names:diff",
                        Category = "Names",
                        Severity = FindingSeverity.Warning,
                        Title = "Your installed apworld differs from the server's",
                        Detail = $"{itemDiffs} item and {locDiffs} location names differ between your local Archipelago install and the last connected server. The server's names are used; update the local apworld if you generate games with it."
                    });
                }
            }

            // ---- Files (from loading) ----
            foreach (var issue in original.LoadIssues)
            {
                var sev = issue.Contains("repaired") || issue.Contains("too small") ? FindingSeverity.AutoFixed
                        : issue.Contains("couldn't be decoded") ? FindingSeverity.Warning
                        : FindingSeverity.Problem;
                Add(new Finding { Key = "file:" + issue, Category = "Files", Severity = sev, Title = issue });
            }
            if (original.ItemMapping.Count == 0 && original.LocationMappingById.Count == 0 && original.UnkeyedLocationPaths.Count == 0)
            {
                Add(new Finding
                {
                    Key = "file:nomapping",
                    Category = "Files",
                    Severity = FindingSeverity.Info,
                    Title = "The pack has no autotracking mapping scripts",
                    Detail = "Items and pins are paired with Archipelago by name only, so review the Key Items and Locations sections."
                });
            }

            AnalyzeScripts(original, effective, slotData, Add, report);
            AnalyzeItems(original, effective, index, itemNames, Add, report);
            AnalyzeLocations(effective, index, locNames, Add, report);
            AnalyzeMaps(effective, index, Add);

            // ---- The fixes themselves: the Doctor's automatic links (for the record), and links to items the game no longer has ----
            foreach (var t in fixes.Tiles.Where(t => t.Automatic && t.ApItemId != null))
                Add(new Finding
                {
                    Key = "auto:" + t.Subject,
                    Category = "Key Items",
                    Severity = FindingSeverity.AutoFixed,
                    Title = $"Linked tile '{t.Code}' to {t.ApItemName} " + (t.Source == "suggested" ? "(the closest name, taken by Fix what Atlas can)" : "(" + (string.IsNullOrEmpty(t.Reason) ? "the names match" : t.Reason) + ")"),
                    Detail = "Made by the Doctor on the pack's first check. Reset it under Your fixes if it's wrong.",
                    Subject = t.Subject,
                    Actions = FindingActions.ResetFixes,
                    RelatedSubjects = { t.Subject }
                });
            foreach (var l in fixes.Links.Where(l => l.Automatic && l.ApLocationId != null))
                Add(new Finding
                {
                    Key = "auto:" + l.Subject,
                    Category = "Locations",
                    Severity = FindingSeverity.AutoFixed,
                    Title = $"Linked '{(string.IsNullOrEmpty(l.SectionName) ? l.PinPath : l.SectionName)}' to {l.ApLocationName} " + (l.Source == "suggested" ? "(the closest name, taken by Fix what Atlas can)" : "(" + (string.IsNullOrEmpty(l.Reason) ? "the names match" : l.Reason) + ")"),
                    Detail = "Made by the Doctor on the pack's first check. Reset it under Your fixes if it's wrong.",
                    Subject = l.Subject,
                    Actions = FindingActions.ResetFixes,
                    RelatedSubjects = { l.Subject }
                });
            if (itemNames.Count > 0)
            {
                foreach (var t in fixes.Tiles.Where(t => t.ApItemId != null && !itemNames.ContainsKey(t.ApItemId.Value)))
                    Add(new Finding
                    {
                        Key = "tile:stalefix:" + t.Code,
                        Category = "Key Items",
                        Severity = FindingSeverity.Warning,
                        Title = $"Your fix links tile '{t.Code}' to \"{t.ApItemName}\", an item this game doesn't have",
                        Detail = "The game's names may have changed since. Link the tile again, or reset the fix.",
                        Subject = "tile:" + t.Code,
                        Actions = FindingActions.LinkItem | FindingActions.ResetFixes,
                        RelatedSubjects = { "tile:" + t.Code },
                        Suggestions = Suggest(t.ApItemName ?? t.Code, itemNames, 5)
                    });
            }

            // ---- The user's links that send several pin sections to one location (usually a wrong match) ----
            // Packs often show one check on two maps (an overview and the area map) under the same name; that's fine.
            // Differently named checks sharing one location is the sign of a wrong match.
            foreach (var group in fixes.Links.Where(l => l.ApLocationId != null).GroupBy(l => l.ApLocationId.Value)
                         .Where(g => g.Select(l => CheckIdentity(l.PinPath, l.SectionName, l.ApLocationName)).Distinct().Count() > 1))
            {
                string name = group.First().ApLocationName ?? $"location {group.Key}";
                Add(new Finding
                {
                    Key = "fixes:duplicate:" + group.Key,
                    Category = "Your fixes",
                    Severity = FindingSeverity.Warning,
                    Title = $"Your fixes link {group.Count()} pin sections to the same location, \"{name}\"",
                    Detail = "Each check usually has its own location, so all but one of these are likely wrong. Undo them and the Doctor will suggest a match for each again.",
                    Actions = FindingActions.ResetFixes,
                    RelatedSubjects = group.Select(l => l.Subject).ToList(),
                    Details = group.Select(l => $"{l.PinPath} / {(string.IsNullOrEmpty(l.SectionName) ? "(pin)" : l.SectionName)}").ToList()
                });
            }

            // ---- Fixes the author's update superseded ----
            foreach (var s in fixes.Superseded)
            {
                Add(new Finding
                {
                    Key = "superseded:" + s.Subject + s.When.Ticks,
                    Category = "Your fixes",
                    Severity = FindingSeverity.Info,
                    Title = $"A fix was set aside after the pack updated to {s.PackVersion}",
                    Detail = $"The author changed '{s.Subject.Substring(s.Subject.IndexOf(':') + 1)}', so their version is used now. Restore your fix if you still need it.",
                    Subject = s.Subject,
                    Actions = FindingActions.RestoreFix
                });
            }

            report.Findings = report.Findings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Category)
                .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return report;
        }

        // =====================================================================
        // The pack's scripts and seed settings
        // =====================================================================

        /// <summary>
        /// Runs the pack's scripts with the latest saved options for its game: reports script errors, unsupported
        /// PopTracker features, and each setting indicator (with options it reads that the slot data lacks).
        /// </summary>
        private static void AnalyzeScripts(LoadedPack original, LoadedPack effective, DataManager.SavedSlotData slotData, Action<Finding> add, DoctorReport report)
        {
            PackScriptHost host;
            try { host = PackScriptHost.Load(original); }
            catch (Exception ex)
            {
                add(new Finding { Key = "script:load", Category = "Scripts", Severity = FindingSeverity.Warning, Title = "The pack's scripts couldn't be read", Detail = ex.Message });
                return;
            }
            if (host == null)
            {
                add(new Finding
                {
                    Key = "script:none",
                    Category = "Scripts",
                    Severity = FindingSeverity.Info,
                    Title = "The pack has no scripts (scripts/init.lua)",
                    Detail = "Key Items use the pack's item mappings and names only; there are no seed-setting indicators to read."
                });
                return;
            }
            host.Initialize();
            if (slotData?.SlotData != null) host.Clear(0, 0, slotData.SlotData);
            report.ScriptsRan = host.Errors.Count == 0 && !host.Stopped;
            // Items the scripts make (ScriptHost:CreateLuaItem) answer to codes no file defines: a grid cell with one isn't empty.
            foreach (var code in effective.ItemGridGroups.Where(g => !g.LooksLikeSettings && !g.Decorative).SelectMany(g => g.Rows).SelectMany(r => r).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!string.IsNullOrWhiteSpace(code) && !effective.ItemsByCode.ContainsKey(code) && host.ScriptItemProvides(code)) report.ScriptItemCodes.Add(code);
            if (host.Stopped)
            {
                add(new Finding
                {
                    Key = "script:stopped",
                    Category = "Scripts",
                    Severity = FindingSeverity.Problem,
                    Title = "Atlas stopped the pack's scripts",
                    Detail = $"Stopped because {host.StopReason}.\nAtlas stops a pack's scripts when a piece of their work runs far longer, deeper or bigger than a real pack's, " +
                             "so a broken script can't freeze or crash it. Key Items fall back to the pack's item mappings."
                });
            }

            foreach (var e in host.Errors)
            {
                // A file the scripts load that isn't in the pack: PopTracker prints the same and goes on, so it's the pack's own
                // note, not a failure of the scripts (fourteen corpus packs load a template's snes.lua they never shipped).
                bool missingFile = e.StartsWith("LoadScript: '", StringComparison.Ordinal);
                add(new Finding
                {
                    Key = (missingFile ? "script:file:" : "script:error:") + e,
                    Category = "Scripts",
                    Severity = FindingSeverity.Warning,
                    Title = missingFile ? "The pack's scripts load a file that isn't in the pack" : "The pack's script hit an error",
                    Detail = e + (missingFile ? "\nAs in PopTracker, the rest of the scripts ran without it." : "\nKey Items fall back to the pack's item mappings where the script can't help.")
                });
            }
            if (host.UnsupportedApis.Count > 0)
            {
                add(new Finding
                {
                    Key = "script:unsupported",
                    Category = "Scripts",
                    Severity = FindingSeverity.Info,
                    Title = $"The pack's script uses {host.UnsupportedApis.Count} PopTracker feature(s) Atlas doesn't emulate",
                    Detail = "They did nothing; tracking that depends on them may be incomplete.",
                    Details = host.UnsupportedApis.OrderBy(a => a).ToList()
                });
            }
            if (host.FollowsMaps)
            {
                add(new Finding
                {
                    Key = "script:follows-maps",
                    Category = "Scripts",
                    Severity = FindingSeverity.Info,
                    Title = host.ReadsGameMemory ? "Live map following: the pack switches maps from the game's memory, which Atlas can't read"
                        : "Live map following: supported",
                    Detail = host.ReadsGameMemory
                        ? "The pack's scripts read the game while it runs (PopTracker's memory watches). Atlas only talks to the room, so the map won't follow the game."
                        : "The pack's scripts switch the map to where the player is, from what the game's client tells the room. The Map Tracker follows it while \"Follow the game's current map\" is on (Display)."
                });
            }
            if (host.IgnoredWrites.Count > 0)
            {
                add(new Finding
                {
                    Key = "script:writes",
                    Category = "Scripts",
                    Severity = FindingSeverity.Info,
                    Title = "The pack's script tries to write the room's data storage; Atlas never does that for a pack",
                    Detail = "Reading is enough for tracking. The writes did nothing.",
                    Details = host.IgnoredWrites.OrderBy(k => k).ToList()
                });
            }
            var unread = host.LoadedFiles.Where(f => !IsReadByAtlas(f.Path, host.Variant)).Select(f => $"{f.Kind}: {f.Path}").Distinct().ToList();
            if (unread.Count > 0)
            {
                add(new Finding
                {
                    Key = "script:unread-files",
                    Category = "Scripts",
                    Severity = FindingSeverity.Warning,
                    Title = $"The pack's script loads {unread.Count} file(s) from places Atlas doesn't read",
                    Detail = "Atlas reads a pack's items, maps, locations and layouts folders (and its default variant's own). Files outside them aren't shown.",
                    Details = unread
                });
            }
            if (!host.HasClearHandler && !host.HasItemHandler)
            {
                add(new Finding
                {
                    Key = "script:noautotracking",
                    Category = "Scripts",
                    Severity = FindingSeverity.Info,
                    Title = "The pack's scripts don't register Archipelago handlers",
                    Detail = "Items are tracked from the pack's item mappings and names only."
                });
            }

            if (slotData?.SlotData == null)
            {
                bool hasSettings = effective.ItemGridGroups.Any(g => g.LooksLikeSettings);
                add(new Finding
                {
                    Key = "settings:noslotdata",
                    Category = "Seed settings",
                    Severity = FindingSeverity.Info,
                    Title = "Connect a slot of this game once to check its seed settings",
                    Detail = hasSettings
                        ? "The pack shows seed settings (e.g. its Settings grid). Atlas saves each slot's options when it connects, then reads them with the pack's script."
                        : "Atlas saves each slot's options when it connects, then checks what the pack's script reads from them."
                });
                return;
            }

            report.SlotDataSource = $"{slotData.SlotName}, {slotData.Saved:yyyy-MM-dd HH:mm}";
            var settingCodes = effective.ItemGridGroups.Where(g => g.LooksLikeSettings).SelectMany(g => g.Rows.SelectMany(r => r));
            report.Settings = host.Settings(settingCodes);
            if (report.Settings.Count > 0)
            {
                add(new Finding
                {
                    Key = "settings:summary",
                    Category = "Seed settings",
                    Severity = FindingSeverity.Info,
                    Title = $"{report.Settings.Count} seed settings are read from the slot's options by the pack's script",
                    Detail = $"Using {report.SlotDataSource}'s options. These show under \"Seed settings\" in Key Items and Properties, not as items.",
                    Details = report.Settings.OrderBy(s => s.Name).Select(s =>
                        $"{s.Name}: {s.StageName ?? (s.On ? "on" : "off")}" + (s.OptionPath != null ? $"  ({s.OptionPath}{(s.OptionMissing ? " — missing" : " = " + ValueText(s.OptionValue))})" : "")).ToList()
                });
            }
            foreach (var s in report.Settings.Where(s => s.OptionMissing))
            {
                add(new Finding
                {
                    Key = "settings:missing:" + s.Code,
                    Category = "Seed settings",
                    Severity = FindingSeverity.Warning,
                    Title = $"\"{s.Name}\" reads {s.OptionPath}, which the slot data doesn't have",
                    Detail = $"The indicator stays at its default. The pack may target a different version of the game's apworld (checked with {report.SlotDataSource}'s options)."
                });
            }
        }

        private static string ValueText(Newtonsoft.Json.Linq.JToken v) => PackScriptHost.SettingInfo.FormatValue(v);

        /// <summary>Whether Atlas reads a pack file a script loads: one in the items, maps, locations or layouts folder (or the variant's own).</summary>
        private static bool IsReadByAtlas(string path, string variant)
        {
            path = (path ?? "").Replace('\\', '/').TrimStart('/');
            if (!string.IsNullOrEmpty(variant) && path.StartsWith(variant + "/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(variant.Length + 1);
            return new[] { "items/", "maps/", "locations/", "layouts/" }.Any(folder => path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
        }

        // =====================================================================
        // Key Items
        // =====================================================================

        private static void AnalyzeItems(LoadedPack original, LoadedPack pack, PackIndex index, Dictionary<long, string> itemNames, Action<Finding> add, DoctorReport report)
        {
            foreach (var grid in pack.ItemGridGroups.Where(g => g.LooksLikeSettings))
            {
                var names = grid.Rows.SelectMany(r => r).Where(c => pack.ItemsByCode.ContainsKey(c)).Select(c => pack.ItemsByCode[c].Name).ToList();
                add(new Finding
                {
                    Key = "grid:hidden:" + PackFixes.GridSubject(grid),
                    Category = "Key Items",
                    Severity = FindingSeverity.AutoFixed,
                    Title = $"The \"{(string.IsNullOrEmpty(grid.Header) ? grid.LayoutKey : grid.Header)}\" grid shows seed settings, not items",
                    Detail = string.Join(", ", names),
                    Subject = PackFixes.GridSubject(grid),
                    Actions = FindingActions.ToggleGrid
                });
            }

            var mappedCodes = new HashSet<string>(original.ItemMapping.Values.SelectMany(v => v), StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Each grid's unlinked tiles are held back until every grid is counted: a grid none of whose tiles names an item of
            // the game, in a pack whose other grids do, is the pack's own display, said once (below).
            var grids = new List<(PackItemGrid Grid, int Linked, List<Finding> Unlinked)>();
            foreach (var grid in pack.ItemGridGroups.Where(g => !g.LooksLikeSettings && !g.Decorative))
            {
                int gridLinked = 0;
                var gridUnlinked = new List<Finding>();
                grids.Add((grid, 0, gridUnlinked));
                foreach (var code in grid.Rows.SelectMany(r => r).Where(c => !string.IsNullOrWhiteSpace(c)))
                {
                    if (!seen.Add(code)) continue;
                    if (pack.ItemsByCode.TryGetValue(code, out var shown) && (shown.IsDecoration || shown.IsPackSwitch(code)))
                    {
                        // A label or picture, a tile made of two others, or one of the pack's own switches: no game item lights it (as in PopTracker).
                        if (shown.IsDecoration) report.TileDecorations++;
                        else report.TileSwitches++;
                        continue;
                    }
                    // A tile a check sets (a boss marker lit by the boss's location) or the pack's scripts set (a goal marker,
                    // a drop worked out from the seed): not an item's tile, so not one to link. Said once, as information.
                    if (shown != null && index.ItemIdsFor(code).Count == 0 && (original.CodesSetByChecks.Contains(code) || original.ScriptCodes.Contains(code)))
                    {
                        bool byCheck = original.CodesSetByChecks.Contains(code);
                        if (byCheck) report.TilesByCheck++;
                        else report.TilesScripted++;
                        add(new Finding
                        {
                            Key = (byCheck ? "tile:bycheck:" : "tile:scripted:") + code,
                            Category = "Key Items",
                            Severity = FindingSeverity.Info,
                            Title = byCheck ? $"Tile \"{shown.Name}\" is set by a check, not by an item" : $"Tile \"{shown.Name}\" is set by the pack's scripts, not by an item",
                            Detail = byCheck ? "The pack's location mapping lights it when its check is done (a boss beaten, say)." : "The pack's own scripts work it out (a goal, a drop); no Archipelago item stands for it.",
                            Subject = "tile:" + code
                        });
                        continue;
                    }
                    if (shown == null && report.ScriptItemCodes.Contains(code))
                    {
                        // An item the pack's scripts make (Super Metroid's transitions, A Link to the Past's enemizer): the scripts set it.
                        report.TilesScripted++;
                        add(new Finding
                        {
                            Key = "tile:scriptitem:" + code,
                            Category = "Key Items",
                            Severity = FindingSeverity.Info,
                            Title = $"Tile \"{code}\" is an item the pack's scripts make",
                            Detail = "The pack's own scripts create and set it (PopTracker's CreateLuaItem); no Archipelago item stands for it.",
                            Subject = "tile:" + code
                        });
                        continue;
                    }
                    if (!pack.ItemsByCode.TryGetValue(code, out var item))
                    {
                        add(new Finding
                        {
                            Key = "tile:unknown:" + code,
                            Category = "Key Items",
                            Severity = FindingSeverity.Warning,
                            Title = $"The grid uses code \"{code}\", which no pack item defines",
                            Detail = "That cell shows as an empty space. Link it to an Archipelago item to add a tile there, or hide it.",
                            Subject = "tile:" + code,
                            Actions = FindingActions.LinkItem | FindingActions.HideTile,
                            Suggestions = Suggest(code, itemNames, 5)
                        });
                        continue;
                    }
                    report.TilesTotal++;
                    var ids = index.ItemIdsFor(code);
                    if (ids.Count > 0)
                    {
                        report.TilesLinked++;
                        gridLinked++;
                        if (mappedCodes.Contains(code)) report.TilesByScript++;
                    }
                    else if (itemNames.Count > 0)
                    {
                        gridUnlinked.Add(new Finding
                        {
                            Key = "tile:unlinked:" + code,
                            Category = "Key Items",
                            Severity = FindingSeverity.Warning,
                            Title = $"Tile \"{item.Name}\" isn't linked to an Archipelago item",
                            Detail = "It will never show as collected. Pick the Archipelago item it stands for, or hide the tile.",
                            Subject = "tile:" + code,
                            Actions = FindingActions.LinkItem | FindingActions.HideTile,
                            Suggestions = Suggest(item.Name, itemNames, 6)
                        });
                    }
                    if (!pack.HasDecodableImage(TileImagePath(item, code)))
                    {
                        add(new Finding
                        {
                            Key = "tile:noimage:" + code,
                            Category = "Key Items",
                            Severity = FindingSeverity.Warning,
                            Title = $"Tile \"{item.Name}\" has no image",
                            Detail = string.IsNullOrEmpty(item.Img) ? "The pack doesn't name one." : $"'{item.Img}' isn't in the pack or couldn't be read.",
                            Subject = "tile:" + code,
                            Actions = FindingActions.AddImage | FindingActions.HideTile
                        });
                    }
                }
                grids[grids.Count - 1] = (grid, gridLinked, gridUnlinked);
            }
            foreach (var (grid, linked, unlinked) in grids)
            {
                if (linked == 0 && unlinked.Count >= DisplayGridTiles && report.TilesLinked > 0)
                {
                    // The pack's own display (A Link to the Past's enemizer tables, Super Mario 64's star doors): nothing from the
                    // room lights it. Said once, not once per tile, and not counted among the tiles to link.
                    report.TilesTotal -= unlinked.Count;
                    report.TileDisplay += unlinked.Count;
                    add(new Finding
                    {
                        Key = "grid:display:" + PackFixes.GridSubject(grid),
                        Category = "Key Items",
                        Severity = FindingSeverity.Info,
                        Title = $"The \"{GridName(grid)}\" grid's {unlinked.Count} tiles show the pack's own state, not the game's items",
                        Detail = "None of them names an item of the game while the pack's other grids do: the pack's own display (an enemizer table, a puzzle board). Nothing lights them from the room.",
                        Subject = PackFixes.GridSubject(grid),
                        Details = unlinked.Select(f => f.Title).Take(40).ToList()
                    });
                }
                else foreach (var f in unlinked) add(f);
            }

            if (index.UnknownMappedItemNames.Count > 0)
            {
                add(new Finding
                {
                    Key = "itemmap:stale-names",
                    Category = "Key Items",
                    Severity = FindingSeverity.Warning,
                    Title = $"item_mapping.lua names {index.UnknownMappedItemNames.Count} items this game doesn't have",
                    Detail = "The script is keyed by item names; these aren't the game's (another version, or a typo).",
                    Details = index.UnknownMappedItemNames.Take(200).ToList()
                });
            }
            if (index.ItemMappingOffset is { } itemBase)
            {
                add(new Finding
                {
                    Key = "itemmap:offset",
                    Category = "Key Items",
                    Severity = FindingSeverity.Info,
                    Title = $"item_mapping.lua is keyed by offsets from the game's item ids (base {itemBase})",
                    Detail = "The pack's own script adds the base before it looks an id up; Atlas read the keys with it, so they are the game's items."
                });
            }
            foreach (var id in index.UnknownMappedItemIds)
            {
                add(new Finding
                {
                    Key = "itemmap:stale:" + id,
                    Category = "Key Items",
                    Severity = FindingSeverity.Warning,
                    Title = $"item_mapping.lua lists item id {id}, which isn't an item of this game",
                    Detail = $"Codes: {string.Join(", ", original.ItemMapping[id])}. The script may be outdated for your Archipelago version."
                });
            }
        }

        /// <summary>A grid with this many unlinked tiles and none linked, beside grids that link, shows the pack's own state.</summary>
        public const int DisplayGridTiles = 6;

        /// <summary>Numbered sections named alike (this many or more) that match nothing of the game's are one family, said once.</summary>
        public const int FamilySize = 8;

        private static string GridName(PackItemGrid grid) => string.IsNullOrEmpty(grid.Header) ? grid.LayoutKey : grid.Header;

        /// <summary>The family a numbered section name belongs to ("Pot #7" → "pot #"): its normalized name with each number as "#", or null for a name without a number.</summary>
        private static string FamilyOf(string name)
        {
            string key = Normalize(name ?? "");
            if (key.Length == 0 || !key.Any(char.IsDigit)) return null;
            return System.Text.RegularExpressions.Regex.Replace(key, @"\d+", "#");
        }

        /// <summary>
        /// What identifies a check for "is this the same check?": the section's name, or the pin's own name for a
        /// section-less pin, normalized. The same check on two maps gives the same key.
        /// </summary>
        public static string SameCheckKey(string pinPath, string sectionName)
        {
            string name = !string.IsNullOrEmpty(sectionName) ? sectionName : (pinPath ?? "").TrimEnd('/').Split('/').Last();
            return Normalize(name);
        }

        /// <summary>
        /// Which check a section is, to tell one check shown twice from two checks: a name that is the location's own names
        /// it by itself (an overview's copy and the area's pin are one check), but a name that isn't ("Item 1", "Chest")
        /// is only the same check on the same pin: eight shops' "Item 1" are eight checks.
        /// </summary>
        public static string CheckIdentity(string pinPath, string sectionName, string locationName)
        {
            string key = SameCheckKey(pinPath, sectionName);
            if (!string.IsNullOrEmpty(locationName) && Normalize(locationName) == key) return key;
            return Normalize(pinPath ?? "") + "|" + key;
        }

        public static string TileImagePath(PopTrackerItem item, string code)
        {
            if (!string.IsNullOrEmpty(item.Img)) return item.Img;
            if (item.Stages != null && item.Stages.Count > 0 && !string.IsNullOrEmpty(item.Stages[0].Img)) return item.Stages[0].Img;
            return "images/items/" + code + ".png";
        }

        // =====================================================================
        // Locations
        // =====================================================================

        private static void AnalyzeLocations(LoadedPack pack, PackIndex index, Dictionary<long, string> locNames, Action<Finding> add, DoctorReport report)
        {
            foreach (var pin in pack.Locations)
            {
                var sections = pin.Sections != null && pin.Sections.Count > 0 ? pin.Sections.Cast<PopTrackerSection>().ToList() : new List<PopTrackerSection> { null };
                foreach (var sec in sections)
                {
                    // Hint markers and sections that hold no check (an entrance or boss toggle) aren't checks to link.
                    if (!index.CountsAsCheck(pin, sec)) continue;
                    report.SectionsTotal++;
                    var match = index.IdsFor(pin)
                        .SelectMany(id => index.ByLocation.TryGetValue(id, out var m) ? m : new List<PackIndex.SectionMatch>())
                        .FirstOrDefault(m => m.Pin == pin && m.Section == sec);
                    if (match == null) continue;
                    report.SectionsLinked++;
                    switch (match.Source)
                    {
                        case MatchSource.MappingScript: report.SectionsByScript++; break;
                        case MatchSource.Name: report.SectionsByName++; break;
                        case MatchSource.LooseName: report.SectionsLoose++; break;
                        case MatchSource.ComposedName: report.SectionsByName++; break;
                        case MatchSource.NeighbourName: report.SectionsByName++; break;
                        case MatchSource.TailName: report.SectionsByName++; break;
                        case MatchSource.UserFix: report.SectionsByFix++; break;
                    }
                }
            }
            report.ApLocationsTotal = locNames.Count;
            report.ApLocationsPlaced = locNames.Keys.Count(id => index.ByLocation.ContainsKey(id));

            if (locNames.Count == 0) return;
            var unplaced = new HashSet<long>(index.UnplacedLocations());
            var unplacedNames = locNames.Where(kv => unplaced.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            report.UnplacedSample = unplacedNames.Values.OrderBy(n => n, StringComparer.Ordinal).Take(40).ToList();

            var context = SuggestContext.For(index, locNames, learnFromMapping: true);
            // A pack whose own mapping names nearly every check (PopTracker's autotracking) but not this one: it doesn't
            // track it either (Ship of Harkinian's "Ganon", the goal): said as such, not as a pairing Atlas missed.
            bool mappingThorough = report.SectionsTotal >= 20 && report.SectionsByScript >= 0.8 * report.SectionsTotal;
            var unmatchedFindings = new List<(Finding Finding, bool Candidate)>();
            foreach (var (pin, sec) in index.UnmatchedSections())
            {
                string name = sec != null && !string.IsNullOrEmpty(sec.Name) ? sec.Name : pin.Name;
                if (name != null && name.StartsWith("atlas:")) continue;
                if (string.IsNullOrWhiteSpace(name)) name = $"(unnamed pin in {pin.FullPath.TrimEnd('/')})";
                string subject = $"link:{pin.FullPath}|{sec?.Name ?? ""}";
                string reference = index.UnresolvedReferenceOf(pin.FullPath, sec?.Name ?? "");
                bool untracked = reference == null && mappingThorough && !index.MappingNames(pin, sec);
                unmatchedFindings.Add((new Finding
                {
                    Key = (untracked ? "loc:untracked:" : "loc:unmatched:") + pin.FullPath + "|" + (sec?.Name ?? ""),
                    Category = "Locations",
                    Severity = untracked ? FindingSeverity.Info : FindingSeverity.Warning,
                    Title = untracked ? $"Pin section \"{name}\" isn't tracked by the pack's own mapping either" : $"Pin section \"{name}\" isn't linked to an Archipelago location",
                    Detail = reference != null
                        ? $"On pin '{pin.FullPath}'. It's meant to show the check of \"{reference}\", which isn't in the pack, so it stays red. Pick the location it stands for, or ignore it."
                        : untracked
                        ? $"On pin '{pin.FullPath}'. The pack's autotracking names nearly every check but not this one, and no location of the game has its name: in PopTracker too it doesn't clear by itself (often a goal or an event, like the final boss). Pick a location if it stands for one, or ignore it."
                        : $"On pin '{pin.FullPath}'. It stays red and never clears. Pick the location it stands for, or ignore it if the game doesn't have it.",
                    Subject = subject,
                    Actions = FindingActions.LinkLocation,
                    Query = name,
                    // Prefer locations no pin shows yet; they're the likely partner. Scored with the pin's context (its
                    // path, the areas the pack's own links teach).
                    Suggestions = SuggestFor(pin, sec, unplacedNames.Count > 0 ? unplacedNames : locNames, 6, context)
                }, Candidate: reference == null && !untracked));
            }
            AssignOneToOne(unmatchedFindings.Select(u => u.Finding).ToList());
            // Numbered sections named alike ("Pot #1" to "Pot #40", "Hotland Kill 1" to "Kill 12") that match nothing of the
            // game's: another version or fork of the apworld has them, this one doesn't. Said once per family, not per section.
            var families = unmatchedFindings.Where(u => u.Candidate && (u.Finding.Suggestions.Count == 0 || u.Finding.Suggestions[0].Score < 0.6))
                .GroupBy(u => FamilyOf(u.Finding.Query)).Where(g => g.Key != null && g.Count() >= FamilySize).ToList();
            var folded = new HashSet<Finding>(families.SelectMany(g => g.Select(u => u.Finding)));
            foreach (var (finding, _) in unmatchedFindings) if (!folded.Contains(finding)) add(finding);
            foreach (var family in families)
            {
                add(new Finding
                {
                    Key = "loc:family:" + family.Key,
                    Category = "Locations",
                    Severity = FindingSeverity.Info,
                    Title = $"{family.Count()} pin sections named like \"{family.First().Finding.Query}\" aren't locations of this game",
                    Detail = "None of them is close to any of the game's location names: another version or fork of the apworld has them (enemy or pot shuffle, say), this one doesn't. They stay red; nothing to choose.",
                    Details = family.Select(u => u.Finding.Query).Take(40).ToList()
                });
            }

            var loose = index.ByLocation.SelectMany(kv => kv.Value.Where(m => m.Source == MatchSource.LooseName && m.Via == null).Select(m => (Id: kv.Key, Match: m))).ToList();
            if (loose.Count > 0)
            {
                add(new Finding
                {
                    Key = "loc:loose",
                    Category = "Locations",
                    Severity = FindingSeverity.Info,
                    Title = $"{loose.Count} pin sections were matched by partial name",
                    Detail = "These matched the part of an Archipelago name before \" - \". They're usually right; check any that look off.",
                    Details = loose.Take(200).Select(l => $"{l.Match.Pin.FullPath} / {l.Match.Section?.Name ?? l.Match.Pin.Name}  →  {index.LocationName(l.Id)}").ToList()
                });
            }

            var composed = index.ByLocation.SelectMany(kv => kv.Value.Where(m => m.Source == MatchSource.ComposedName && m.Via == null).Select(m => (Id: kv.Key, Match: m))).ToList();
            if (composed.Count > 0)
            {
                add(new Finding
                {
                    Key = "loc:composed",
                    Category = "Locations",
                    Severity = FindingSeverity.Info,
                    Title = $"{composed.Count} pin sections were matched by their pin's name and their own",
                    Detail = "A pin \"Brawler\" with a section \"Wave 10 Completed\" was paired with the game's \"Wave 10 Completed (Brawler)\" (or \"A - B\" with \"B (A)\"). They're usually right; check any that look off.",
                    Details = composed.Take(200).Select(l => $"{l.Match.Pin.FullPath} / {l.Match.Section?.Name ?? l.Match.Pin.Name}  →  {index.LocationName(l.Id)}").ToList()
                });
            }
            var tails = index.ByLocation.SelectMany(kv => kv.Value.Where(m => m.Source == MatchSource.TailName && m.Via == null).Select(m => (Id: kv.Key, Match: m))).ToList();
            if (tails.Count > 0)
            {
                add(new Finding
                {
                    Key = "loc:tail",
                    Category = "Locations",
                    Severity = FindingSeverity.Info,
                    Title = $"{tails.Count} pin sections were matched as the only location ending in their name",
                    Detail = "A section \"Basement Pot 1\" was paired with the game's \"Waynehouse: Basement Pot 1\", the one location whose name ends so after its area. They're usually right; check any that look off.",
                    Details = tails.Take(200).Select(l => $"{l.Match.Pin.FullPath} / {l.Match.Section?.Name ?? l.Match.Pin.Name}  →  {index.LocationName(l.Id)}").ToList()
                });
            }
            var neighbours = index.ByLocation.SelectMany(kv => kv.Value.Where(m => m.Source == MatchSource.NeighbourName && m.Via == null).Select(m => (Id: kv.Key, Match: m))).ToList();
            if (neighbours.Count > 0)
            {
                add(new Finding
                {
                    Key = "loc:neighbours",
                    Category = "Locations",
                    Severity = FindingSeverity.Info,
                    Title = $"{neighbours.Count} pin sections were named like their neighbours",
                    Detail = "The pack's mapping gives a pin's other sections the game's names (\"Item 2\" is \"KF Shop Item 2\"), but not these: each took the same form (\"Item 1\" is \"KF Shop Item 1\").",
                    Details = neighbours.Take(200).Select(l => $"{l.Match.Pin.FullPath} / {l.Match.Section?.Name ?? l.Match.Pin.Name}  →  {index.LocationName(l.Id)}").ToList()
                });
            }
            if (index.HintMarkerSections > 0)
            {
                add(new Finding
                {
                    Key = "loc:hints",
                    Category = "Locations",
                    Severity = FindingSeverity.Info,
                    Title = $"{index.HintMarkerSections} sections are hint markers, not checks",
                    Detail = "Sections and pins named \"… - hint\" (or \"… Hints\") are PopTracker's hint tracking. Atlas leaves them out of the pairing and the counts."
                });
            }
            if (index.InspectOnlySections > 0)
            {
                add(new Finding
                {
                    Key = "loc:inspect",
                    Category = "Locations",
                    Severity = FindingSeverity.Info,
                    Title = $"{index.InspectOnlySections} sections can only be looked at: markers, not checks",
                    Detail = "Every access rule of theirs is PopTracker's \"{…}\" form (checkable for a look, never obtainable): the player's position, entrance logic, hint signs. Atlas leaves them out of the pairing and the counts; one that a mapping or a name links is a check like any other."
                });
            }
            if (index.NoCheckSections > 0)
            {
                add(new Finding
                {
                    Key = "loc:no-check",
                    Category = "Locations",
                    Severity = FindingSeverity.Info,
                    Title = $"{index.NoCheckSections} sections hold an item to click, not a check",
                    Detail = "Sections that host an item without an item_count (an entrance, a boss) count no check in PopTracker. Atlas leaves them out of the pairing and the counts; a location the pack's mapping sends to their item still shows there."
                });
            }
            int badRefs = index.UnresolvedReferences.Count + index.RepairedReferences.Count;
            if (badRefs > 0)
            {
                // Said for the pack's author (the report includes it); the user's map is fine where Atlas followed the
                // section's own name, or where the names paired.
                add(new Finding
                {
                    Key = "loc:refs",
                    Category = "Locations",
                    Severity = FindingSeverity.Info,
                    Title = badRefs == 1 ? "A section refers to a section that isn't in the pack" : $"{badRefs} sections refer to sections that aren't in the pack",
                    Detail = index.UnresolvedReferences.Count == 0
                        ? "A section with \"ref\" shows another section's check on its map. These name one the pack doesn't have; each one's own name says which it means, and Atlas followed that."
                        : "A section with \"ref\" shows another section's check on its map. These name one the pack doesn't have, so Atlas followed the section's own name where it says which it means, and paired the rest by their own names.",
                    Details = index.RepairedReferences.Take(200).Select(r => $"{r.Pin.FullPath} / {r.Section.Name}  →  ref \"{r.Section.Ref}\" (Atlas followed \"{r.Repaired}\")")
                        .Concat(index.UnresolvedReferences.Take(200).Select(r => $"{r.Pin.FullPath} / {r.Section.Name}  →  ref \"{r.Section.Ref}\"")).ToList()
                });
            }

            if (index.DanglingPaths.Count > 0)
            {
                add(new Finding
                {
                    Key = "locmap:dangling",
                    Category = "Locations",
                    Severity = FindingSeverity.Warning,
                    Title = $"location_mapping.lua points at {index.DanglingPaths.Count} pins/sections that don't exist",
                    Detail = "Those Archipelago locations have no pin from the script. Names may still pair some of them.",
                    Details = index.DanglingPaths.Take(200).Select(d => (d.Id != null ? $"[{d.Id}] " : "") + d.Path).ToList()
                });
            }
            if (index.LocationMappingOffset is { } locationBase)
            {
                add(new Finding
                {
                    Key = "locmap:offset",
                    Category = "Locations",
                    Severity = FindingSeverity.Info,
                    Title = $"location_mapping.lua is keyed by offsets from the game's ids (base {locationBase})",
                    Detail = "The pack's own script adds the base before it looks an id up; Atlas read the keys with it, so they are the game's locations."
                });
            }
            if (index.UnknownMappedLocationIds.Count > 0)
            {
                add(new Finding
                {
                    Key = "locmap:stale",
                    Category = "Locations",
                    Severity = FindingSeverity.Warning,
                    Title = $"location_mapping.lua lists {index.UnknownMappedLocationIds.Count} ids that aren't locations of this game",
                    Detail = "The script may be outdated for your Archipelago version.",
                    Details = index.UnknownMappedLocationIds.Take(200).Select(id => id.ToString()).ToList()
                });
            }
            if (index.UnknownMappedLocationNames.Count > 0)
            {
                add(new Finding
                {
                    Key = "locmap:stale-names",
                    Category = "Locations",
                    Severity = FindingSeverity.Warning,
                    Title = $"location_mapping.lua names {index.UnknownMappedLocationNames.Count} locations this game doesn't have",
                    Detail = "The script is keyed by location names; these aren't the game's (another version, or a typo).",
                    Details = index.UnknownMappedLocationNames.Take(200).ToList()
                });
            }
            if (unplaced.Count > 0)
            {
                add(new Finding
                {
                    Key = "loc:unplaced",
                    Category = "Locations",
                    Severity = FindingSeverity.Info,
                    Title = $"{unplaced.Count} Archipelago locations have no pin on any map",
                    Detail = "Some may be turned off by your game's options. You can place a pin for any of them.",
                    Actions = FindingActions.AddPin,
                    Details = unplaced.Select(id => locNames[id]).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(400).ToList()
                });
            }
        }

        // =====================================================================
        // Maps
        // =====================================================================

        private static void AnalyzeMaps(LoadedPack pack, PackIndex index, Action<Finding> add)
        {
            var pinsByMap = new Dictionary<string, List<(PopTrackerLocation Pin, float X, float Y)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in pack.Locations)
            {
                if (!string.IsNullOrEmpty(pin.MapRef)) Add(pinsByMap, pin.MapRef, (pin, pin.X, pin.Y));
                foreach (var ml in pin.MapLocations ?? new List<PopTrackerMapLocation>()) if (!string.IsNullOrEmpty(ml.Map)) Add(pinsByMap, ml.Map, (pin, ml.X, ml.Y));
            }
            var unused = pack.UnusedMaps();
            foreach (var map in pack.Maps.Values)
            {
                if (unused.Contains(map.Id))
                {
                    // Declared, but no tab shows it and no pin is on it: PopTracker never shows it, so neither does Atlas.
                    add(new Finding
                    {
                        Key = "map:unused:" + map.Id,
                        Category = "Maps",
                        Severity = FindingSeverity.Info,
                        Title = $"Map \"{map.Name}\" isn't used by the pack",
                        Detail = "None of the pack's tabs shows it and no pin is on it, so Atlas leaves it out of the map list" + (map.Background == null && !pack.HasMapBackground(map) ? " (its picture isn't in the pack either)." : "."),
                        Subject = "map:" + map.Id
                    });
                    continue;
                }
                bool broken = pack.BrokenImages.Any(b => b.EndsWith(map.MapBg?.TrimStart('/') ?? "\0", StringComparison.OrdinalIgnoreCase));
                // A background replaced by a fix, or the pack's own (decoded or not: the analysis needn't decode it).
                if (map.Background == null && !pack.HasMapBackground(map))
                {
                    add(new Finding
                    {
                        Key = "map:nobg:" + map.Id,
                        Category = "Maps",
                        Severity = FindingSeverity.Problem,
                        Title = $"Map \"{map.Name}\" has no background",
                        Detail = broken ? $"'{map.MapBg}' is in the pack but couldn't be decoded (often a JPEG format Godot can't read). Replace it with a PNG or re-saved JPEG."
                                        : $"'{map.MapBg}' isn't in the pack. Replace it with an image file.",
                        Subject = "map:" + map.Id,
                        Actions = FindingActions.ReplaceMapImage
                    });
                }
                pinsByMap.TryGetValue(map.Id, out var pins);
                if (pins == null || pins.Count == 0)
                {
                    add(new Finding
                    {
                        Key = "map:empty:" + map.Id,
                        Category = "Maps",
                        Severity = FindingSeverity.Info,
                        Title = $"Map \"{map.Name}\" has no pins",
                        Subject = "map:" + map.Id
                    });
                    continue;
                }
                Godot.Vector2? imageSize = map.Background?.GetSize() ?? (pack.MapBackgroundPath(map) is { } bgPath && pack.ImageSize(bgPath) is { } known ? new Godot.Vector2(known.X, known.Y) : null);
                if (imageSize is { } size)
                {
                    var outside = pins.Where(p => p.X < 0 || p.Y < 0 || p.X > size.X || p.Y > size.Y).ToList();
                    if (outside.Count > 0)
                    {
                        add(new Finding
                        {
                            Key = "map:outside:" + map.Id,
                            Category = "Maps",
                            Severity = FindingSeverity.Warning,
                            Title = $"{outside.Count} pins on \"{map.Name}\" sit outside the map image",
                            Detail = "Drag them onto the map in the pin editor.",
                            Subject = "map:" + map.Id,
                            Details = outside.Select(o => $"{o.Pin.FullPath} at {o.X:0},{o.Y:0}").ToList()
                        });
                    }
                }
            }
        }

        private static void Add<T>(Dictionary<string, List<T>> d, string key, T value)
        {
            if (!d.TryGetValue(key, out var list)) d[key] = list = new List<T>();
            list.Add(value);
        }

        // =====================================================================
        // Recommendations: the rows the Doctor can fix from its suggestions, and which clash
        // =====================================================================

        /// <summary>Findings the Doctor can fix with one of its suggestions (unlinked tiles and pin sections), best first.</summary>
        public static List<Finding> Recommendations(DoctorReport report) =>
            report?.Findings
                .Where(f => !f.Ignored && f.Suggestions.Count > 0 && (f.Actions.HasFlag(FindingActions.LinkItem) || f.Actions.HasFlag(FindingActions.LinkLocation)))
                .OrderByDescending(f => f.Suggestions[0].Score)
                .ToList() ?? new List<Finding>();

        /// <summary>What a recommendation row is about: the tile's item name (or its code), or the section (or pin) name.</summary>
        public static string RowName(DoctorReport report, Finding f)
        {
            string subject = f.Subject.Substring(f.Subject.IndexOf(':') + 1);
            if (f.Actions.HasFlag(FindingActions.LinkItem))
                return report?.Pack != null && report.Pack.ItemsByCode.TryGetValue(subject, out var item) && !string.IsNullOrEmpty(item.Name) ? item.Name : subject;
            var parts = subject.Split('|');
            return parts.Length > 1 && parts[1].Length > 0 ? parts[1] : parts[0].Split('/').Last();
        }

        /// <summary>
        /// Rows whose chosen match clashes: two rows that would give the same item or location to two things (the
        /// strongest keeps it; one check shown on two maps under the same name is fine), and a pin whose chosen location
        /// a differently named check already shows. Finding key → why.
        /// </summary>
        public static Dictionary<string, string> Conflicts(DoctorReport report, List<Finding> recs, Func<Finding, Suggestion> chosen)
        {
            var conflicts = new Dictionary<string, string>();
            foreach (var group in recs.GroupBy(f => (f.Actions.HasFlag(FindingActions.LinkItem), chosen(f).Id)).Where(g => g.Count() > 1))
            {
                var ordered = group.OrderByDescending(f => chosen(f).Score).ToList();
                foreach (var f in ordered.Skip(1))
                    conflicts[f.Key] = $"Same match as \"{RowName(report, ordered[0])}\" (stronger); pick another for this row.";
            }
            foreach (var f in recs.Where(f => f.Actions.HasFlag(FindingActions.LinkLocation) && !conflicts.ContainsKey(f.Key)))
            {
                var id = chosen(f).Id;
                if (report?.Index == null || !report.Index.ByLocation.TryGetValue(id, out var shownBy) || shownBy.Count == 0) continue;
                var parts = f.Subject.Substring(f.Subject.IndexOf(':') + 1).Split('|');
                // A section referring to one the pack doesn't have is meant to show another section's check: no clash.
                if (report.Index.UnresolvedReferenceOf(parts[0], parts.Length > 1 ? parts[1] : "") != null) continue;
                string location = report.Index.LocationName(id);
                string mine = CheckIdentity(parts[0], parts.Length > 1 ? parts[1] : "", location);
                var other = shownBy.FirstOrDefault(m => m.Via == null && CheckIdentity(m.Pin.FullPath, m.Section?.Name, location) != mine);
                if (other != null) conflicts[f.Key] = $"Already shown by a different check: \"{other.Section?.Name ?? other.Pin.Name}\" on pin {other.Pin.FullPath}.";
            }
            foreach (var key in conflicts.Keys.ToList())
            {
                var f = recs.First(r => r.Key == key);
                if (!conflicts[key].StartsWith("Same match as")) continue;
                var rivals = recs.Where(r => r != f && r.Actions == f.Actions && chosen(r).Id == chosen(f).Id).ToList();
                // One check shown twice is fine; for pin sections that's told by more than the name (see CheckIdentity).
                string Identity(Finding row)
                {
                    if (!row.Actions.HasFlag(FindingActions.LinkLocation)) return RowName(report, row).ToLowerInvariant();
                    var parts = row.Subject.Substring(row.Subject.IndexOf(':') + 1).Split('|');
                    return CheckIdentity(parts[0], parts.Length > 1 ? parts[1] : "", chosen(row).Label);
                }
                if (rivals.All(r => Identity(r) == Identity(f))) conflicts.Remove(key);
            }
            return conflicts;
        }

        /// <summary>
        /// The recommendations whose best match is exact (the same name once case and punctuation are ignored) and
        /// clashes with nothing: what the Doctor links by itself on a pack's first check.
        /// </summary>
        public static List<Finding> ExactMatches(DoctorReport report)
        {
            var recs = Recommendations(report).Where(f => f.Suggestions[0].Score >= 0.999 || IsOnlyOneOfItsKind(report, f)).ToList();
            var conflicts = Conflicts(report, recs, f => f.Suggestions[0]);
            return recs.Where(f => !conflicts.ContainsKey(f.Key)).ToList();
        }

        /// <summary>
        /// A pin whose identifying part ("FS: Firelink Armor" of "FS: Firelink Armor - shop after beating KFF boss") names
        /// exactly one of the game's locations in that area, the best suggestion being it: the detail differs in wording
        /// only, and nothing else it could be. The suggestion is marked sure, with the reason. With two or more of a kind
        /// ("FS: Soul Arrow - shop", "- Orbeck"), the detail decides and the Doctor only suggests.
        /// </summary>
        private static bool IsOnlyOneOfItsKind(DoctorReport report, Finding f)
        {
            if (string.IsNullOrEmpty(f.Query) || report?.Names?.Locations == null || !f.Actions.HasFlag(FindingActions.LinkLocation) || f.Suggestions[0].Score < 0.6) return false;
            var q = Prepare(f.Query);
            if (q.Core.Length == 0 || q.Area.Length == 0) return false;
            var best = Prepare(f.Suggestions[0].Label);
            if (best.Area != q.Area || best.Core != q.Core) return false;
            // Numbered copies ("Fading Soul 2" and "Fading Soul - boss arena #2") are told apart by their number: the only
            // one of its kind is the only one with that number (a "1" being the game's unnumbered first copy).
            bool numbered = q.Numbers.Count > 0 || best.Numbers.Count > 0;
            if (numbered && !SameCopy(q.Numbers, best.Numbers)) return false;
            int same = 0;
            foreach (var name in report.Names.Locations.Keys)
            {
                var p = Prepare(name);
                if (p.Area == q.Area && p.Core == q.Core && (!numbered || SameCopy(p.Numbers, q.Numbers)) && ++same > 1) return false;
            }
            if (same != 1) return false;
            int colon = f.Query.IndexOf(':');
            string rest = colon > 0 ? f.Query.Substring(colon + 1).Trim() : f.Query;
            int dash = rest.IndexOf(" - ", StringComparison.Ordinal);
            f.Suggestions[0].Score = 1.0;
            f.Suggestions[0].Reason = $"the only '{(dash > 0 ? rest.Substring(0, dash) : rest).Trim()}' in {(colon > 0 ? f.Query.Substring(0, colon).Trim() : "its area")}";
            return true;
        }

        /// <summary>Whether two names' copy numbers say the same copy: the same numbers, or a "1" against none.</summary>
        private static bool SameCopy(HashSet<string> a, HashSet<string> b) =>
            a.SetEquals(b) || (a.Count == 0 && b.Count == 1 && b.Contains("1")) || (b.Count == 0 && a.Count == 1 && a.Contains("1"));

        // =====================================================================
        // Suggestions in context (general rules: nothing here names a pack or a game)
        // =====================================================================

        /// <summary>
        /// What a pack teaches about its own places, for scoring a section against the game's locations: the places its
        /// pins' paths name, and the game's area tags it writes out in full (learned from the links already made: when the
        /// game's "UP: Andre" sits on a pin under "Undead Parish", again and again, "UP" means Undead Parish).
        /// </summary>
        public sealed class SuggestContext
        {
            /// <summary>Loose names of every place the pack's pin paths name (three letters or more).</summary>
            public HashSet<string> Places = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>A game's area tag (loose) → the pack's place it stands for (loose).</summary>
            public Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            /// <summary>Section names (normalized) the pack gives sections on three or more pins: labels, not names ("Yarn", "Chest").</summary>
            public HashSet<string> GenericSections = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>Reads the places and learns the aliases from the index's links (the mapping's too, unless told not to).</summary>
            public static SuggestContext For(PackIndex index, IReadOnlyDictionary<long, string> locNames, bool learnFromMapping)
            {
                var ctx = new SuggestContext();
                if (index?.Pack == null) return ctx;
                var pinsBySection = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var pin in index.Pack.Locations)
                {
                    foreach (string seg in Segments(pin)) if (seg.Length >= 3) ctx.Places.Add(seg);
                    foreach (string sec in (pin.Sections ?? new List<PopTrackerSection>()).Select(s => Normalize(s?.Name ?? "")).Where(s => s.Length > 0).Distinct())
                        pinsBySection[sec] = pinsBySection.GetValueOrDefault(sec) + 1;
                }
                foreach (var kv in pinsBySection) if (kv.Value >= 3) ctx.GenericSections.Add(kv.Key);
                // Co-occurrence of a location's area tag with the pin path's places, over the links already made.
                var together = new Dictionary<(string Area, string Place), int>();
                var areaLinks = new Dictionary<string, int>(StringComparer.Ordinal);
                var placeAreas = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var kv in index.ByLocation)
                {
                    if (locNames == null || !locNames.TryGetValue(kv.Key, out var locName)) continue;
                    string area = AreaOf(locName);
                    if (area.Length == 0) continue;
                    foreach (var m in kv.Value)
                    {
                        if (m.Via != null || m.Pin == null || (!learnFromMapping && m.Source == MatchSource.MappingScript)) continue;
                        var segs = Segments(m.Pin).Where(s => s.Length >= 3).ToList();
                        if (segs.Any(s => Related(s, area))) continue; // written out already: nothing to learn
                        areaLinks[area] = areaLinks.GetValueOrDefault(area) + 1;
                        foreach (string seg in segs.Distinct())
                        {
                            together[(area, seg)] = together.GetValueOrDefault((area, seg)) + 1;
                            if (!placeAreas.TryGetValue(seg, out var set)) placeAreas[seg] = set = new HashSet<string>(StringComparer.Ordinal);
                            set.Add(area);
                        }
                    }
                }
                foreach (var area in areaLinks.Keys)
                {
                    // The place seen with this tag most, and (between equals) the one seen with the fewest other tags: a
                    // map's top level ("Overworld") is seen with every tag and says nothing.
                    var best = together.Where(t => t.Key.Area == area && t.Value >= 2 && t.Value >= 0.6 * areaLinks[area])
                        .OrderByDescending(t => t.Value).ThenBy(t => placeAreas[t.Key.Place].Count).Select(t => t.Key.Place).FirstOrDefault();
                    if (best != null && placeAreas[best].Count <= 3 && best.Length > area.Length) ctx.Aliases[area] = best;
                }
                return ctx;
            }
        }

        /// <summary>A pin's path as loose place names (each part, the pin's own name last).</summary>
        private static IEnumerable<string> Segments(PopTrackerLocation pin) =>
            (pin?.FullPath ?? pin?.Name ?? "").Split('/').Select(Squash).Where(s => s.Length > 0);

        /// <summary>Letters and digits only, lower case: "Undead Parish" → "undeadparish".</summary>
        private static string Squash(string s) => Normalize(s ?? "").Replace(" ", "");

        /// <summary>
        /// A location name's area tag, loose: the part before a short ":" ("UP: Andre" → "up") or else before the first " - "
        /// ("Route 218 - Pokédex Upgrade" → "route218"); empty when it has none.
        /// </summary>
        internal static string AreaOf(string locationName)
        {
            if (string.IsNullOrEmpty(locationName)) return "";
            int colon = locationName.IndexOf(':');
            if (colon > 0 && colon <= 24) return Squash(locationName.Substring(0, colon));
            int dash = locationName.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash <= 40) return Squash(locationName.Substring(0, dash));
            return "";
        }

        /// <summary>Whether a place and an area tag name the same place, one within the other (three letters or more).</summary>
        private static bool Related(string place, string area) =>
            place.Length >= 3 && area.Length >= 3 && (place == area || place.Contains(area, StringComparison.Ordinal) || area.Contains(place, StringComparison.Ordinal));

        /// <summary>
        /// The best locations for a pin section, scored with its context: the section's name alone and composed with its
        /// pin's and parents' names (a section "Chest" under "Crystal Den"); a location whose area tag is the pin's place
        /// (written out, or by an alias the pack taught) scores a little higher, one whose area is another place of this
        /// pack's clearly lower ("Oreburgh Gate B1F - …" for a pin on Stark Mountain).
        /// </summary>
        public static List<Suggestion> SuggestFor(PopTrackerLocation pin, PopTrackerSection section, IReadOnlyDictionary<long, string> candidates, int max, SuggestContext context)
        {
            string name = section != null && !string.IsNullOrEmpty(section.Name) ? section.Name : pin?.Name;
            if (string.IsNullOrWhiteSpace(name) || candidates == null || candidates.Count == 0) return new List<Suggestion>();
            var queries = new List<Prepared> { Prepare(name) };
            if (section != null && pin != null)
            {
                var parts = (pin.FullPath ?? pin.Name ?? "").Split('/').Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                foreach (string part in parts.AsEnumerable().Reverse().Take(3))
                    if (!Normalize(name).Contains(Normalize(part), StringComparison.Ordinal)) queries.Add(Prepare(part + " " + name));
                // A section without a copy number takes its pin's ("Boss" on pin "Ruins Boss 2" is the second): the name
                // alone mustn't win against the copy the pin names.
                bool generic = context != null && context.GenericSections.Contains(Normalize(name));
                if (queries.Count > 1 && ((queries[0].Numbers.Count == 0 && Prepare(pin.Name ?? "").Numbers.Count > 0) || generic)) queries.RemoveAt(0);
                // A generic label ("Yarn" on pin "Swamp Tree B") leaves the pin to name the check.
                if (generic && !string.IsNullOrWhiteSpace(pin.Name)) queries.Add(Prepare(pin.Name));
            }
            var places = Segments(pin).Where(s => s.Length >= 3).ToList();
            // The words that say which one: the section's own, and its pin's (not the parents', which hold every side).
            var sideWords = new HashSet<string>(Prepare(name).Tokens.Concat(section != null && pin != null ? Prepare(pin.Name ?? "").Tokens : Enumerable.Empty<string>()), StringComparer.Ordinal);
            var scored = new List<Suggestion>();
            foreach (var kv in candidates)
            {
                var c = Prepare(kv.Value);
                double s = 0;
                foreach (var q in queries) s = Math.Max(s, Score(q, c));
                if (s < 0.35) continue;
                if (s < 0.999 && Contradicts(sideWords, c.Tokens)) s *= 0.8;
                if (context != null && places.Count > 0 && s < 0.999)
                {
                    string area = AreaOf(kv.Value);
                    if (area.Length > 0)
                    {
                        string full = context.Aliases.TryGetValue(area, out var alias) ? alias : area;
                        bool here = places.Any(p => Related(p, full) || Related(p, area));
                        bool elsewhere = !here && context.Places.Any(p => p == full || (full.Length >= 5 && p.Contains(full, StringComparison.Ordinal)));
                        if (here) s = Math.Min(0.99, s * 1.05);
                        else if (elsewhere) s *= 0.85;
                    }
                }
                scored.Add(new Suggestion { Id = kv.Key, Label = kv.Value, Score = s });
            }
            var ranked = scored.Where(x => x.Score >= 0.35).OrderByDescending(x => x.Score).ToList();
            // A query without a number against a numbered family ("Scraps" against "Scraps 1" to "Scraps 20"): any of them,
            // so none with confidence (the first copy's small bonus mustn't make it a pick).
            if (ranked.Count > 1 && queries.All(q => q.Numbers.Count == 0) && ranked[0].Score >= TieConfidence)
            {
                string family = Family(ranked[0].Label);
                if (family.Length > 0 && ranked.Skip(1).Any(x => x.Id != ranked[0].Id && Family(x.Label) == family))
                    foreach (var copy in ranked.Where(x => Family(x.Label) == family)) copy.Score = Math.Min(copy.Score, TieConfidence - 0.01);
            }
            // Two different locations scoring alike: the names can't tell which ("Scraps" on one pin of many, "Scraps 1"
            // to "Scraps 20"). Still offered, never as a confident pick.
            if (ranked.Count > 1 && ranked[0].Score >= TieConfidence && ranked[1].Score >= ranked[0].Score - TieMargin && ranked[1].Id != ranked[0].Id)
                foreach (var tied in ranked.Where(x => x.Score >= ranked[0].Score - TieMargin)) tied.Score = Math.Min(tied.Score, TieConfidence - 0.01);
            return ranked.Take(max).ToList();
        }

        /// <summary>A name without its numbers (normalized): the numbered family it belongs to; empty when it has no number.</summary>
        private static string Family(string label)
        {
            string n = Normalize(label ?? "");
            if (!n.Any(char.IsDigit)) return "";
            return string.Join(" ", n.Split(' ').Where(w => !w.All(char.IsDigit)));
        }

        /// <summary>Scores within this of the best are a tie between them.</summary>
        private const double TieMargin = 0.005;

        /// <summary>A tie is said under this: below what "Fix what Atlas can" takes.</summary>
        private const double TieConfidence = 0.85;

        /// <summary>Words naming one of two sides; a name with one and not the other contradicts a name with the other.</summary>
        private static readonly (string A, string B)[] Opposites =
        {
            ("left", "right"), ("top", "bottom"), ("upper", "lower"), ("north", "south"), ("east", "west"),
            ("inside", "outside"), ("front", "back"), ("before", "after"), ("first", "last"), ("entrance", "exit"),
        };

        /// <summary>Whether a query and a candidate name opposite sides ("Top Left" and "Bottom Left").</summary>
        private static bool Contradicts(HashSet<string> query, HashSet<string> candidate)
        {
            foreach (var (a, b) in Opposites)
                if ((query.Contains(a) && !query.Contains(b) && candidate.Contains(b) && !candidate.Contains(a))
                    || (query.Contains(b) && !query.Contains(a) && candidate.Contains(a) && !candidate.Contains(b))) return true;
            return false;
        }

        /// <summary>
        /// One location, one place: the rows' best suggestions are given out strongest first, each location once (two
        /// rows that are the same check on two maps may share it); a row whose best went to a stronger row has its next
        /// free one put first. Nothing is dropped: the order of each row's list changes, and clashes the Doctor reports
        /// become fewer.
        /// </summary>
        internal static void AssignOneToOne(List<Finding> rows)
        {
            var pairs = rows.Where(r => r.Suggestions.Count > 0)
                .SelectMany(r => r.Suggestions.Select((s, i) => (Row: r, Suggestion: s, Rank: i)))
                .OrderByDescending(p => p.Suggestion.Score).ThenBy(p => p.Rank).ToList();
            var taken = new Dictionary<long, string>();
            var given = new Dictionary<Finding, Suggestion>();
            foreach (var (row, suggestion, _) in pairs)
            {
                if (given.ContainsKey(row)) continue;
                string identity = Normalize(row.Query ?? "");
                if (taken.TryGetValue(suggestion.Id, out var holder) && holder != identity) continue;
                taken[suggestion.Id] = identity;
                given[row] = suggestion;
            }
            foreach (var (row, suggestion) in given)
            {
                int at = row.Suggestions.IndexOf(suggestion);
                if (at <= 0) continue;
                row.Suggestions.RemoveAt(at);
                row.Suggestions.Insert(0, suggestion);
            }
        }

        /// <summary>
        /// How right the Doctor's suggestions are, measured on the pack's own mapping: each section the mapping links whose
        /// name isn't its location's (the cases a person would have to decide), its links set aside, gets the suggestion
        /// the Doctor would make; the top one is right when it's one of the locations the mapping gives that section (a
        /// section holding several checks is right with any). A section whose name is exactly another location's is left
        /// out: the pack's mapping is the stale one there (another version), not the suggestion. At most
        /// <paramref name="most"/> sections. <paramref name="plain"/> measures the name-only suggestions instead
        /// (ATLAS_SELFTEST_SUGGEST_PLAIN=1), for comparison.
        /// </summary>
        public static (int Tried, int Right, int Strong, int StrongRight) EvaluateSuggestions(DoctorReport report, bool plain = false, int most = 300, List<string> misses = null)
        {
            if (report?.Index == null || report.Names?.Locations == null) return (0, 0, 0, 0);
            var locNames = report.Names.Locations.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
            var context = SuggestContext.For(report.Index, locNames, learnFromMapping: false);
            // Each mapped section with every location the mapping gives it.
            var bySection = new Dictionary<(PopTrackerLocation Pin, PopTrackerSection Section), HashSet<long>>();
            foreach (var kv in report.Index.ByLocation.OrderBy(kv => kv.Key))
                foreach (var m in kv.Value.Where(x => x.Via == null && x.Source == MatchSource.MappingScript))
                {
                    if (!bySection.TryGetValue((m.Pin, m.Section), out var ids)) bySection[(m.Pin, m.Section)] = ids = new HashSet<long>();
                    ids.Add(kv.Key);
                }
            var exactNames = new HashSet<string>(locNames.Values.Select(n => Normalize(n)), StringComparer.Ordinal);
            // A section (or its one-section pin) named exactly as another location than the mapping gives it: the pack's
            // mapping is for another version there. When that's common, the mapping can't be the measure for this pack.
            var cases = new List<(PopTrackerLocation Pin, PopTrackerSection Section, HashSet<long> Ids, string Name)>();
            int stale = 0;
            foreach (var ((pin, section), ids) in bySection)
            {
                string name = section != null && !string.IsNullOrEmpty(section.Name) ? section.Name : pin.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var mappedNames = new HashSet<string>(ids.Select(id => locNames.TryGetValue(id, out var n) ? Normalize(n) : ""), StringComparer.Ordinal);
                if (mappedNames.Contains(Normalize(name))) continue; // named as the game names it
                bool pinIsTheName = pin.Sections != null && pin.Sections.Count == 1 && !string.IsNullOrEmpty(pin.Name);
                if (exactNames.Contains(Normalize(name)) || (pinIsTheName && exactNames.Contains(Normalize(pin.Name)) && !mappedNames.Contains(Normalize(pin.Name)))) { stale++; continue; }
                cases.Add((pin, section, ids, name));
            }
            if (stale >= 3 && stale >= 0.1 * (stale + cases.Count)) return (0, 0, 0, 0);
            int tried = 0, right = 0, strong = 0, strongRight = 0;
            foreach (var (pin, section, ids, name) in cases)
            {
                if (tried >= most) break;
                tried++;
                var top = (plain ? Suggest(name, locNames, 1) : SuggestFor(pin, section, locNames, 1, context)).FirstOrDefault();
                bool ok = top != null && ids.Contains(top.Id);
                if (ok) right++;
                else misses?.Add($"{pin.FullPath} | {name}  ->  {top?.Label ?? "(none)"} ({top?.Score:0.00})  |  truth: {string.Join(" / ", ids.Take(3).Select(id => locNames.GetValueOrDefault(id)))}");
                if (top != null && top.Score >= 0.85)
                {
                    strong++;
                    if (ok) strongRight++;
                }
            }
            return (tried, right, strong, strongRight);
        }

        // =====================================================================
        // Similarity
        // =====================================================================

        /// <summary>The best-matching names for a query, highest score first.</summary>
        public static List<Suggestion> Suggest(string query, IReadOnlyDictionary<long, string> candidates, int max)
        {
            if (string.IsNullOrWhiteSpace(query) || candidates == null || candidates.Count == 0) return new List<Suggestion>();
            var q = Prepare(query);
            return candidates
                .Select(kv => new Suggestion { Id = kv.Key, Label = kv.Value, Score = Score(q, Prepare(kv.Value)) })
                .Where(s => s.Score >= 0.35)
                .OrderByDescending(s => s.Score)
                .Take(max)
                .ToList();
        }

        private sealed class Prepared
        {
            public string Text = "";
            /// <summary>The text with a slash, "&amp;" and "w/" read as gaps (the way Atlas read them before), for exactness only.</summary>
            public string Plain = "";
            public HashSet<string> Tokens = new HashSet<string>();
            public HashSet<string> Bigrams = new HashSet<string>();
            /// <summary>"Area: Thing - detail" → area "pw2" (letters/digits only), core "soul of sister friede".</summary>
            public string Area = "";
            public string Core = "";
            /// <summary>Numbers outside the area tag ("Fading Soul 2", "#3", "x2"), to tell numbered copies apart.</summary>
            public HashSet<string> Numbers = new HashSet<string>();
        }

        private static readonly Dictionary<string, Prepared> _prepCache = new Dictionary<string, Prepared>();

        private static Prepared Prepare(string s)
        {
            lock (_prepCache)
            {
                if (_prepCache.TryGetValue(s, out var cached)) return cached;
            }
            var p = new Prepared { Text = Normalize(s), Plain = Normalize(s, equivalences: false) };
            p.Tokens = new HashSet<string>(p.Text.Split(' ').Where(t => t.Length > 1));
            // Many games name checks "Area: Thing - where": the "Thing" is what identifies it.
            int colon = s.IndexOf(':');
            string rest = colon > 0 && colon <= 12 ? s.Substring(colon + 1) : s;
            if (colon > 0 && colon <= 12) p.Area = Normalize(s.Substring(0, colon)).Replace(" ", "");
            int dash = rest.IndexOf(" - ", StringComparison.Ordinal);
            // The identifying part without bare numbers (those are compared separately).
            p.Core = string.Join(" ", Normalize(dash > 0 ? rest.Substring(0, dash) : rest).Split(' ').Where(w => !w.All(char.IsDigit)));
            // Copy numbers come from the identifying part ("Fading Soul 2") and explicit "#n" markers, not from
            // trailing location details ("snowfield tower, 3F"), which don't tell copies apart.
            string identifying = (dash > 0 ? rest.Substring(0, dash) : rest) + " " + string.Join(" ", System.Text.RegularExpressions.Regex.Matches(rest, @"#\s*\d+").Select(m => m.Value));
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(identifying, @"\d+"))
                p.Numbers.Add(m.Value.TrimStart('0').Length == 0 ? "0" : m.Value.TrimStart('0'));
            string text = p.Text;
            string compact = text.Replace(" ", "");
            for (int i = 0; i + 1 < compact.Length; i++) p.Bigrams.Add(compact.Substring(i, 2));
            lock (_prepCache)
            {
                if (_prepCache.Count > 20000) _prepCache.Clear();
                _prepCache[s] = p;
            }
            return p;
        }

        /// <summary>
        /// Lower case, letters and digits only, one space between words; a few ways of writing the same thing read the same
        /// first: "w/Sirris" and "with Sirris", "Yoel/Yuria" and "Yoel or Yuria", "&amp;" and "and". Without
        /// <paramref name="equivalences"/>, those marks are gaps like any other ("Pharros/elevator room" is
        /// "pharros elevator room", as a pack writing "Pharros - elevator room" has it).
        /// </summary>
        private static string Normalize(string s, bool equivalences = true)
        {
            string t = s.ToLowerInvariant();
            if (equivalences && (t.Contains('/') || t.Contains('&')))
            {
                t = System.Text.RegularExpressions.Regex.Replace(t, @"\bw/(?=\w)", "with ");
                t = System.Text.RegularExpressions.Regex.Replace(t, @"(?<=\w)/(?=\w)", " or ");
                t = t.Replace("&", " and ");
            }
            // An apostrophe is part of its word ("Kingdom's", "Collector's"), not a gap: a pack writing "Kingdoms_Edge" means the same.
            if (t.IndexOf('\'') >= 0 || t.IndexOf('\u2019') >= 0) t = t.Replace("'", "").Replace("\u2019", "");
            var sb = new StringBuilder();
            foreach (char c in t) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            return string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>
        /// How alike two names are (0..1). The identifying part ("Soul of Sister Friede" in "PW2: Soul of Sister
        /// Friede - boss drop") counts most: same thing in a related area ("PW" vs "PW2") scores high, the same
        /// thing in a different area lower, and otherwise a blend of character and word overlap.
        /// </summary>
        private static double Score(Prepared a, Prepared b)
        {
            if (a.Text.Length == 0 || b.Text.Length == 0) return 0;
            if (a.Text == b.Text || a.Plain == b.Plain) return 1;
            return Math.Clamp(NameScore(a, b) * NumberFactor(a.Numbers, b.Numbers), 0, 0.99);
        }

        /// <summary>
        /// Numbered copies ("Fading Soul 2" vs "Fading Soul - boss arena #2") must pair by number: the same numbers
        /// get a small bonus, different numbers a clear penalty, and a "1" matches an unnumbered name (games often
        /// leave the first copy unnumbered).
        /// </summary>
        private static double NumberFactor(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 && b.Count == 0) return 1.0;
            if (a.SetEquals(b)) return 1.04;
            var onlyA = a.Except(b).ToList();
            var onlyB = b.Except(a).ToList();
            bool firstCopy = (onlyA.Count == 1 && onlyA[0] == "1" && onlyB.Count == 0) || (onlyB.Count == 1 && onlyB[0] == "1" && onlyA.Count == 0);
            if (firstCopy) return 1.02;
            return onlyA.Count > 0 && onlyB.Count > 0 ? 0.78 : 0.88;
        }

        private static double NameScore(Prepared a, Prepared b)
        {
            double overall = BlendScore(a, b);
            if (a.Core.Length > 0 && b.Core.Length > 0)
            {
                double area = a.Area.Length == 0 || b.Area.Length == 0 ? 0.92
                    : a.Area == b.Area ? 1.0
                    : a.Area.StartsWith(b.Area) || b.Area.StartsWith(a.Area) ? 0.96 // PW / PW1 / PW2
                    : 0.75;
                // The same thing in the same area: the detail decides between copies ("- shop", "- Yoel/Yuria shop", "- Orbeck").
                if (a.Core == b.Core) return Math.Max(overall, 0.97 * area * (0.98 + 0.02 * overall));
                double core = BlendScore(Prepare(a.Core), Prepare(b.Core));
                if (core >= 0.85) overall = Math.Max(overall, core * 0.95 * area);
            }
            return overall;
        }

        /// <summary>Blend of character-bigram (Dice) and word overlap, with a bonus when one name contains the other.</summary>
        private static double BlendScore(Prepared a, Prepared b)
        {
            if (a.Text.Length == 0 || b.Text.Length == 0) return 0;
            if (a.Text == b.Text) return 1;
            int shared = a.Bigrams.Count(b.Bigrams.Contains);
            double dice = a.Bigrams.Count + b.Bigrams.Count == 0 ? 0 : 2.0 * shared / (a.Bigrams.Count + b.Bigrams.Count);
            int tokShared = a.Tokens.Count(b.Tokens.Contains);
            double tokens = a.Tokens.Count == 0 || b.Tokens.Count == 0 ? 0 : (double)tokShared / Math.Min(a.Tokens.Count, b.Tokens.Count);
            double score = 0.6 * dice + 0.4 * tokens;
            if (a.Text.Contains(b.Text) || b.Text.Contains(a.Text)) score = Math.Max(score, 0.85);
            return Math.Min(1, score);
        }

        // =====================================================================
        // Report to the pack author
        // =====================================================================

        /// <summary>A ready-to-paste issue for the pack's repository, listing what's wrong with the pack itself.</summary>
        public static string AuthorReport(DoctorReport report)
        {
            var m = report.Pack.Manifest;
            var sb = new StringBuilder();
            sb.AppendLine($"## Issues found in {m?.Name} {m?.GetActualVersion()}");
            sb.AppendLine();
            sb.AppendLine($"Checked against Archipelago's {report.Names?.Game} names ({report.Names?.Source}, {report.Names?.Version}).");
            sb.AppendLine();
            // Only problems in the pack (not the user's settings or local fixes).
            var relevant = report.Findings.Where(f => f.Category != "Names" && f.Category != "Your fixes" && (f.Severity != FindingSeverity.Info || f.Key == "loc:refs")).ToList();
            if (relevant.Count == 0)
            {
                sb.AppendLine("No problems found.");
                return sb.ToString();
            }
            // Unlinked pin sections read best as one list with the likely Archipelago name for each.
            var unlinked = relevant.Where(f => f.Key.StartsWith("loc:unmatched:")).ToList();
            relevant = relevant.Except(unlinked).ToList();
            foreach (var group in relevant.GroupBy(f => f.Category).Concat(unlinked.Count > 0 ? new[] { unlinked.GroupBy(_ => "Locations").First() } : Array.Empty<IGrouping<string, Finding>>()))
            {
                sb.AppendLine($"### {group.Key}");
                if (group.All(f => f.Key.StartsWith("loc:unmatched:")))
                {
                    sb.AppendLine($"{group.Count()} pin sections don't match any {report.Names?.Game} location (they never clear):");
                    foreach (var f in group)
                    {
                        string what = f.Title.Replace("Pin section ", "").Replace(" isn't linked to an Archipelago location", "");
                        var best = f.Suggestions.FirstOrDefault();
                        sb.AppendLine($"- {what}" + (best != null && best.Score >= 0.6 ? $" — likely `{best.Label}`" : ""));
                    }
                    sb.AppendLine();
                    continue;
                }
                // Atlas's own links, one line: for the author they mean "your names differ a little from the game's".
                var linked = group.Where(f => f.Severity == FindingSeverity.AutoFixed && f.Title.StartsWith("Linked ", StringComparison.Ordinal)).ToList();
                if (linked.Count > 0)
                {
                    var pairs = linked.Select(f => { string t = f.Title.Substring(7); int paren = t.LastIndexOf(" (", StringComparison.Ordinal); return (paren > 0 ? t.Substring(0, paren) : t).Replace("' to ", "' → "); }).ToList();
                    sb.AppendLine($"- Atlas paired {linked.Count} of the pack's names with the game's where they differ a little (pack → game): " + string.Join("; ", pairs.Take(8)) + (pairs.Count > 8 ? $"; …and {pairs.Count - 8} more" : ""));
                }
                foreach (var f in group.Except(linked))
                {
                    sb.AppendLine($"- {f.Title}" + (string.IsNullOrEmpty(f.Detail) || f.Severity == FindingSeverity.AutoFixed ? "" : $" — {f.Detail}"));
                    if (f.Suggestions.Count > 0 && f.Suggestions[0].Score >= 0.75) sb.AppendLine($"  - Likely Archipelago name: `{f.Suggestions[0].Label}`");
                    foreach (var d in f.Details.Take(25)) sb.AppendLine($"  - {d}");
                    if (f.Details.Count > 25) sb.AppendLine($"  - …and {f.Details.Count - 25} more");
                }
                sb.AppendLine();
            }
            sb.AppendLine("_Generated by The Archipelago Atlas Pack Doctor._");
            return sb.ToString();
        }
    }
}
