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
        public int SectionsTotal, SectionsLinked, SectionsByScript, SectionsByName, SectionsLoose, SectionsByFix;
        public int ApLocationsTotal, ApLocationsPlaced;

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
    /// fixed on its own, and what the user should decide, with ranked suggestions. Pure analysis: safe to run on
    /// a worker thread (no Godot calls).
    /// </summary>
    public static class PackDoctor
    {
        public static DoctorReport Analyze(LoadedPack original, GameNameTable names, DataManager.SavedSlotData slotData = null)
        {
            var effective = PackFixes.Effective(original);
            var fixes = PackFixes.Get(PackFixes.KeyFor(original));
            var report = new DoctorReport { Pack = effective, Names = names };
            var ignored = new HashSet<string>(fixes.Ignored);

            var locNames = names?.LocationsById() ?? new Dictionary<long, string>();
            var itemNames = names?.ItemsById() ?? new Dictionary<long, string>();
            var index = new PackIndex(effective, locNames, itemNames);
            PackFixes.ApplyLinks(index);
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
                var (itemDiffs, locDiffs) = GameNames.CompareSources(names.Game);
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

            // ---- The user's links that send several pin sections to one location (usually a wrong match) ----
            // Packs often show one check on two maps (an overview and the area map) under the same name; that's fine.
            // Differently named checks sharing one location is the sign of a wrong match.
            foreach (var group in fixes.Links.Where(l => l.ApLocationId != null).GroupBy(l => l.ApLocationId.Value)
                         .Where(g => g.Select(l => SameCheckKey(l.PinPath, l.SectionName)).Distinct().Count() > 1))
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
            report.ScriptsRan = host.Errors.Count == 0;

            foreach (var e in host.Errors)
            {
                add(new Finding
                {
                    Key = "script:error:" + e,
                    Category = "Scripts",
                    Severity = FindingSeverity.Warning,
                    Title = "The pack's script hit an error",
                    Detail = e + "\nKey Items fall back to the pack's item mappings where the script can't help."
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
            foreach (var grid in pack.ItemGridGroups.Where(g => !g.LooksLikeSettings))
            {
                foreach (var code in grid.Rows.SelectMany(r => r).Where(c => !string.IsNullOrWhiteSpace(c)))
                {
                    if (!seen.Add(code)) continue;
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
                        if (mappedCodes.Contains(code)) report.TilesByScript++;
                    }
                    else if (itemNames.Count > 0)
                    {
                        add(new Finding
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
                    if (pack.FindImage(TileImagePath(item, code)) == null)
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

        /// <summary>
        /// What identifies a check for "is this the same check?": the section's name, or the pin's own name for a
        /// section-less pin, normalized. The same check on two maps gives the same key.
        /// </summary>
        public static string SameCheckKey(string pinPath, string sectionName)
        {
            string name = !string.IsNullOrEmpty(sectionName) ? sectionName : (pinPath ?? "").TrimEnd('/').Split('/').Last();
            return Normalize(name);
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
                        case MatchSource.UserFix: report.SectionsByFix++; break;
                    }
                }
            }
            report.ApLocationsTotal = locNames.Count;
            report.ApLocationsPlaced = locNames.Keys.Count(id => index.ByLocation.ContainsKey(id));

            if (locNames.Count == 0) return;
            var unplaced = new HashSet<long>(index.UnplacedLocations());
            var unplacedNames = locNames.Where(kv => unplaced.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

            foreach (var (pin, sec) in index.UnmatchedSections())
            {
                string name = sec != null && !string.IsNullOrEmpty(sec.Name) ? sec.Name : pin.Name;
                if (name != null && name.StartsWith("atlas:")) continue;
                if (string.IsNullOrWhiteSpace(name)) name = $"(unnamed pin in {pin.FullPath.TrimEnd('/')})";
                string subject = $"link:{pin.FullPath}|{sec?.Name ?? ""}";
                add(new Finding
                {
                    Key = "loc:unmatched:" + pin.FullPath + "|" + (sec?.Name ?? ""),
                    Category = "Locations",
                    Severity = FindingSeverity.Warning,
                    Title = $"Pin section \"{name}\" isn't linked to an Archipelago location",
                    Detail = $"On pin '{pin.FullPath}'. It stays red and never clears. Pick the location it stands for, or ignore it if the game doesn't have it.",
                    Subject = subject,
                    Actions = FindingActions.LinkLocation,
                    // Prefer locations no pin shows yet; they're the likely partner.
                    Suggestions = Suggest(name, unplacedNames.Count > 0 ? unplacedNames : locNames, 6)
                });
            }

            var loose = index.ByLocation.SelectMany(kv => kv.Value.Where(m => m.Source == MatchSource.LooseName).Select(m => (Id: kv.Key, Match: m))).ToList();
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
            foreach (var map in pack.Maps.Values)
            {
                if (map.BackgroundTexture == null)
                {
                    bool broken = pack.BrokenImages.Any(b => b.EndsWith(map.MapBg?.TrimStart('/') ?? "\0", StringComparison.OrdinalIgnoreCase));
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
                if (map.BackgroundTexture != null)
                {
                    var size = map.BackgroundTexture.GetSize();
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
            var p = new Prepared { Text = Normalize(s) };
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

        private static string Normalize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
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
            if (a.Text == b.Text) return 1;
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
                if (a.Core == b.Core) return Math.Max(overall, 0.97 * area);
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
            var relevant = report.Findings.Where(f => f.Category != "Names" && f.Category != "Your fixes" && f.Severity != FindingSeverity.Info).ToList();
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
                foreach (var f in group)
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
