#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>How a pack element was paired with an Archipelago id.</summary>
    public enum MatchSource
    {
        /// <summary>The pack's own mapping script (what PopTracker uses).</summary>
        MappingScript,
        /// <summary>Exact name match.</summary>
        Name,
        /// <summary>A looser name match (e.g. the part before " - ").</summary>
        LooseName,
        /// <summary>The pin's name and the section's together ("Brawler" / "Wave 10 Completed" ↔ "Wave 10 Completed (Brawler)"), or "A - B" ↔ "B (A)".</summary>
        ComposedName,
        /// <summary>A fix the user made in the Pack Doctor.</summary>
        UserFix,
        /// <summary>
        /// Named like the pin's other sections: the mapping gives "Item 2" … "Item 8" of Kokiri Forest's shop the game's
        /// "KF Shop Item 2" … "KF Shop Item 8", so "Item 1" is "KF Shop Item 1".
        /// </summary>
        NeighbourName,
        /// <summary>
        /// The only location whose name ends in the section's, after its area ("Basement Pot 1" ↔ "Waynehouse: Basement Pot 1"):
        /// packs name a check as the game does, without the game's area.
        /// </summary>
        TailName,
    }

    /// <summary>
    /// Pairs a loaded pack with one game's Archipelago ids: pins/sections ↔ location ids and item codes ↔ item ids.
    /// Mapping scripts are trusted first, then names; every pairing records its source so the Pack Doctor can
    /// report coverage and let the user correct it.
    /// </summary>
    public sealed class PackIndex
    {
        public sealed class SectionMatch
        {
            public PopTrackerLocation Pin;
            /// <summary>Null when the pin itself is the check (no sections).</summary>
            public PopTrackerSection Section;
            public MatchSource Source;
            /// <summary>The match this one copies: the section refers to that one's ("ref") and shows its check. Null for a match of its own.</summary>
            public SectionMatch Via;
        }

        public LoadedPack Pack { get; }

        /// <summary>AP location id → the pins/sections that show it.</summary>
        public Dictionary<long, List<SectionMatch>> ByLocation { get; } = new Dictionary<long, List<SectionMatch>>();

        /// <summary>Pin → AP location ids it covers.</summary>
        public Dictionary<PopTrackerLocation, List<long>> IdsByPin { get; } = new Dictionary<PopTrackerLocation, List<long>>();

        /// <summary>Mapping-script paths that point at no pin or section in the pack.</summary>
        public List<(long? Id, string Path)> DanglingPaths { get; } = new List<(long?, string)>();

        /// <summary>Mapping-script location ids that aren't locations of this game (stale script).</summary>
        public List<long> UnknownMappedLocationIds { get; } = new List<long>();

        /// <summary>
        /// The base added to location_mapping.lua's keys when they are offsets from the game's ids (the pack's script adds it
        /// before the lookup: Streets of Rage, Tevi, CrossCode, Psychonauts, A Hat in Time), or null when the keys are the ids.
        /// </summary>
        public long? LocationMappingOffset { get; private set; }

        /// <summary>The base added to item_mapping.lua's keys when they are offsets from the game's item ids, else null.</summary>
        public long? ItemMappingOffset { get; private set; }

        /// <summary>Mapping-script item ids that aren't items of this game.</summary>
        public List<long> UnknownMappedItemIds { get; } = new List<long>();

        /// <summary>Names a mapping script is keyed by (["Passage - The Martyr"] = …) that aren't locations, or items, of this game.</summary>
        public List<string> UnknownMappedLocationNames { get; } = new List<string>();
        public List<string> UnknownMappedItemNames { get; } = new List<string>();

        /// <summary>
        /// Sections that refer to another ("ref") which isn't in the pack: they're paired by their own names instead, as
        /// ordinary sections.
        /// </summary>
        public List<(PopTrackerLocation Pin, PopTrackerSection Section)> UnresolvedReferences { get; } = new List<(PopTrackerLocation, PopTrackerSection)>();

        /// <summary>
        /// Sections whose "ref" isn't in the pack but whose own name says which section it means: "Basement Side Room - Gold
        /// Skulltula" with ref "Forest Temple/Basement Chest Room/Gold Skulltula" shows "Forest Temple/Basement Side
        /// Room/Gold Skulltula" (the area from the ref, the room from its name, the same check). With the path Atlas used.
        /// </summary>
        public List<(PopTrackerLocation Pin, PopTrackerSection Section, string Repaired)> RepairedReferences { get; } = new List<(PopTrackerLocation, PopTrackerSection, string)>();

        /// <summary>
        /// Whether the pack's location mapping names this section (or section-less pin) at all, an id the game doesn't have
        /// included: a section it never names is one the pack doesn't autotrack either.
        /// </summary>
        public bool MappingNames(PopTrackerLocation pin, PopTrackerSection section) => _mappingNamed.Contains((pin, section));

        private readonly HashSet<(PopTrackerLocation, PopTrackerSection)> _mappingNamed = new HashSet<(PopTrackerLocation, PopTrackerSection)>();

        /// <summary>How many sections refer to another one that's in the pack (they show its check, linked or not).</summary>
        public int ReferenceSections => _refTargets.Count;

        // Sections that refer to another, each resolved (through any chain of refs) to the section it shows; and each such
        // target's referring sections. A pin without sections can be a target (its section is null then).
        private readonly Dictionary<PopTrackerSection, (PopTrackerLocation Pin, PopTrackerSection Section)> _refTargets =
            new Dictionary<PopTrackerSection, (PopTrackerLocation, PopTrackerSection)>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(PopTrackerLocation, PopTrackerSection), List<(PopTrackerLocation Pin, PopTrackerSection Section)>> _referrers =
            new Dictionary<(PopTrackerLocation, PopTrackerSection), List<(PopTrackerLocation, PopTrackerSection)>>();

        /// <summary>Whether a section refers to another one in the pack (it shows that one's check, and isn't paired by itself).</summary>
        public bool IsReference(PopTrackerSection section) => section != null && _refTargets.ContainsKey(section);

        /// <summary>
        /// The "ref" of a section that refers to one the pack doesn't have (pin path and section name, as a finding's
        /// subject names them), else null. Such a section is meant to show a check another section shows.
        /// </summary>
        public string UnresolvedReferenceOf(string pinPath, string sectionName) =>
            _unresolved.TryGetValue((pinPath ?? "") + "|" + (sectionName ?? ""), out var target) ? target : null;

        private readonly Dictionary<string, string> _unresolved = new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly Dictionary<long, string> _locationNames;
        private readonly Dictionary<string, long> _locationIdsByName;
        private readonly Dictionary<long, string> _itemNames;
        private readonly Dictionary<string, List<long>> _itemIdsByCode = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// When a mapping's keys are offsets from the game's ids (see <see cref="MappingOffset"/>), shifts them onto the ids
        /// and returns the base; otherwise leaves them and returns null.
        /// </summary>
        private static long? Rebase(Dictionary<long, string> names, Func<Dictionary<long, List<string>>[], bool> agrees, params Dictionary<long, List<string>>[] maps)
        {
            if (names.Count == 0) return null;
            var keys = maps.SelectMany(m => m.Keys).Distinct().ToList();
            long? by = MappingOffset.Infer(keys, names.Keys.ToList(), out _);
            if (by == null) return null;
            Shift(maps, by.Value);
            // The numbers fit, but do the names? Another game numbered the same way (a pack named for the wrong game) fits
            // by number alone; its sections name nothing of this game, and it's left as it was.
            if (agrees(maps)) return by;
            Shift(maps, -by.Value);
            return null;
        }

        private static void Shift(Dictionary<long, List<string>>[] maps, long by)
        {
            foreach (var map in maps)
            {
                var shifted = map.Select(kv => (Key: unchecked(kv.Key + by), kv.Value)).ToList();
                map.Clear();
                foreach (var (key, value) in shifted)
                {
                    if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
                    foreach (var v in value) if (!list.Contains(v)) list.Add(v);
                }
            }
        }

        /// <summary>The share of the mapping's known ids whose path ends in words of the game's name for it, or the reverse (a quarter is enough).</summary>
        private bool PathsAgreeWithNames(Dictionary<long, List<string>> mappingById)
        {
            int known = 0, agree = 0;
            foreach (var kv in mappingById)
            {
                if (!_locationNames.TryGetValue(kv.Key, out var name) || string.IsNullOrEmpty(name)) continue;
                known++;
                string looseName = Loose(name);
                if (kv.Value.Any(path => path.Split('/').Reverse().Take(2).Select(Loose).Any(seg => seg.Length >= 3 && (looseName.Contains(seg, StringComparison.Ordinal) || seg.Contains(looseName, StringComparison.Ordinal)))))
                    agree++;
            }
            return known > 0 && agree * 4 >= known;
        }

        /// <summary>The share of the mapping's known item ids whose codes (or the tiles they name) read as the game's name for the item.</summary>
        private bool CodesAgreeWithNames(Dictionary<long, List<string>> itemMapping)
        {
            int known = 0, agree = 0;
            foreach (var kv in itemMapping)
            {
                if (!_itemNames.TryGetValue(kv.Key, out var name) || string.IsNullOrEmpty(name)) continue;
                known++;
                string looseName = Loose(name);
                bool any = false;
                foreach (string code in kv.Value)
                {
                    string looseCode = Loose(code);
                    string looseTile = Pack.ItemsByCode.TryGetValue(code, out var def) && def?.Name != null ? Loose(def.Name) : "";
                    if ((looseCode.Length >= 3 && (looseName.Contains(looseCode, StringComparison.Ordinal) || looseCode.Contains(looseName, StringComparison.Ordinal)))
                        || (looseTile.Length >= 3 && (looseName.Contains(looseTile, StringComparison.Ordinal) || looseTile.Contains(looseName, StringComparison.Ordinal)))) { any = true; break; }
                }
                if (any) agree++;
            }
            return known > 0 && agree * 4 >= known;
        }

        /// <param name="locationNames">This game's location id → name (all locations of the slot or the game).</param>
        /// <param name="itemNames">This game's item id → name (may be empty when unknown).</param>
        public PackIndex(LoadedPack pack, IReadOnlyDictionary<long, string> locationNames, IReadOnlyDictionary<long, string> itemNames)
        {
            Pack = pack;
            _locationNames = locationNames?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<long, string>();
            _itemNames = itemNames?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<long, string>();
            _locationIdsByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _locationNames)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                _locationIdsByName[kv.Value] = kv.Key;
            }
            BuildLocations();
            BuildItems();
        }

        // =====================================================================
        // Locations
        // =====================================================================

        private void BuildLocations()
        {
            var pinsByPath = new Dictionary<string, List<PopTrackerLocation>>(StringComparer.OrdinalIgnoreCase);
            var pinsByName = new Dictionary<string, List<PopTrackerLocation>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in Pack.Locations)
            {
                if (!string.IsNullOrEmpty(pin.FullPath))
                {
                    // Several pins can share a path (Satisfactory lists its tiers once with their checks and once on the overworld
                    // with refs to them; Choo-Choo Charles has two world maps): a path resolves to the one that has the named section.
                    if (!pinsByPath.TryGetValue(pin.FullPath, out var same)) pinsByPath[pin.FullPath] = same = new List<PopTrackerLocation>();
                    same.Add(pin);
                }
                if (!pinsByName.TryGetValue(pin.Name ?? "", out var list)) pinsByName[pin.Name ?? ""] = list = new List<PopTrackerLocation>();
                list.Add(pin);
            }

            // 0. Sections that refer to another ("ref"): PopTracker shows the target's check in their place and ignores
            //    the rest of what they say, so each takes the target's links (below, as they're made) and isn't paired
            //    on its own. One whose target isn't in the pack stays an ordinary section.
            ResolveReferences(pinsByPath, pinsByName);

            // 1. Mapping script, keyed by id, or by the game's own name for the location (71 corpus packs: ["Passage - The
            //    Martyr"] = {"@Achievements/The Martyr/The Martyr"}), which the names turn into its id. An id or name the game
            //    doesn't have (an outdated script) is set aside, so the section can still be paired by name below.
            var mappingById = new Dictionary<long, List<string>>(Pack.LocationMappingById);
            var codesById = new Dictionary<long, List<string>>(Pack.LocationCodesById);
            foreach (var kv in Pack.LocationMappingByName)
            {
                if (!TryLocationId(kv.Key, out long named))
                {
                    if (_locationNames.Count > 0) UnknownMappedLocationNames.Add(kv.Key);
                    continue;
                }
                foreach (string value in kv.Value)
                {
                    var into = value.StartsWith("@") ? mappingById : codesById;
                    if (!into.TryGetValue(named, out var list)) into[named] = list = new List<string>();
                    if (!list.Contains(value)) list.Add(value);
                }
            }
            // 1a. Keys that are offsets from the game's ids (the pack's script subtracts a base before it looks an id up):
            //     the base that puts them on the game's ids is added, so they are read as the pack means them.
            LocationMappingOffset = Rebase(_locationNames, maps => PathsAgreeWithNames(maps[0]), mappingById, codesById);
            foreach (var kv in mappingById)
            {
                foreach (var path in kv.Value)
                    if (TargetOf(ResolvePath(path, pinsByPath, pinsByName)) is { } named) _mappingNamed.Add((named.Pin, named.Section));
                if (_locationNames.Count > 0 && !_locationNames.ContainsKey(kv.Key))
                {
                    UnknownMappedLocationIds.Add(kv.Key);
                    continue;
                }
                foreach (var path in kv.Value)
                {
                    var match = TargetOf(ResolvePath(path, pinsByPath, pinsByName));
                    if (match == null) { DanglingPaths.Add((kv.Key, path)); continue; }
                    Add(kv.Key, match.Value.Pin, match.Value.Section, MatchSource.MappingScript);
                }
            }

            // 1b. Mapping script, keyed by id, naming an item code rather than a path: the section that hosts that item shows
            //     the location (PopTracker sets the hosted item when the location is checked). A value that is no item's code
            //     is a section's name, found wherever it is (Spyro 3's mapping names its 3,899 sections so).
            if (codesById.Count > 0)
            {
                var sectionsByName = new Dictionary<string, List<(PopTrackerLocation Pin, PopTrackerSection Section)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var pin in Pack.Locations)
                    foreach (var sec in pin.Sections ?? new List<PopTrackerSection>())
                    {
                        if (string.IsNullOrWhiteSpace(sec?.Name)) continue;
                        if (!sectionsByName.TryGetValue(sec.Name.Trim(), out var list)) sectionsByName[sec.Name.Trim()] = list = new List<(PopTrackerLocation, PopTrackerSection)>();
                        list.Add((pin, sec));
                    }
                var sectionsByLoose = new Dictionary<string, List<(PopTrackerLocation Pin, PopTrackerSection Section)>>(StringComparer.Ordinal);
                foreach (var kv in sectionsByName)
                {
                    string loose = Loose(kv.Key);
                    if (!sectionsByLoose.TryGetValue(loose, out var list)) sectionsByLoose[loose] = list = new List<(PopTrackerLocation, PopTrackerSection)>();
                    list.AddRange(kv.Value);
                }
                var hosts = new Dictionary<string, List<(PopTrackerLocation Pin, PopTrackerSection Section)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var pin in Pack.Locations)
                    foreach (var sec in pin.Sections ?? new List<PopTrackerSection>())
                        foreach (string code in HostedCodes(sec))
                        {
                            if (!hosts.TryGetValue(code, out var list)) hosts[code] = list = new List<(PopTrackerLocation, PopTrackerSection)>();
                            list.Add((pin, sec));
                        }
                foreach (var kv in codesById)
                {
                    if (_locationNames.Count > 0 && !_locationNames.ContainsKey(kv.Key))
                    {
                        if (!UnknownMappedLocationIds.Contains(kv.Key)) UnknownMappedLocationIds.Add(kv.Key);
                        continue;
                    }
                    foreach (string code in kv.Value)
                    {
                        if (hosts.TryGetValue(code, out var list))
                        {
                            foreach (var (pin, sec) in list) Add(kv.Key, pin, sec, MatchSource.MappingScript);
                            continue;
                        }
                        if (Pack.ItemsByCode.ContainsKey(code)) continue; // an item nothing hosts: nothing to show
                        if (sectionsByName.TryGetValue(code.Trim(), out var named) || sectionsByLoose.TryGetValue(Loose(code), out named))
                        {
                            foreach (var (pin, sec) in named) { Add(kv.Key, pin, sec, MatchSource.MappingScript); _mappingNamed.Add((pin, sec)); }
                        }
                        else DanglingPaths.Add((kv.Key, code));
                    }
                }
            }

            // 2. Mapping script without ids: the path's last named segment is the AP location name.
            foreach (var path in Pack.UnkeyedLocationPaths)
            {
                var match = TargetOf(ResolvePath(path, pinsByPath, pinsByName));
                if (match != null) _mappingNamed.Add((match.Value.Pin, match.Value.Section));
                string name = LocationNameFromPath(path);
                if (match == null || name == null || !_locationIdsByName.TryGetValue(name, out long id))
                {
                    DanglingPaths.Add((null, path));
                    continue;
                }
                Add(id, match.Value.Pin, match.Value.Section, MatchSource.MappingScript);
            }

            // 3. Names, for sections the scripts didn't cover. Hint markers (PopTracker's "… - hint" sections) aren't checks.
            var holdingNoCheck = new List<(PopTrackerLocation Pin, PopTrackerSection Section)>();
            foreach (var pin in Pack.Locations)
            {
                if (pin.Sections != null && pin.Sections.Count > 0)
                {
                    foreach (var sec in pin.Sections)
                    {
                        if (IsReference(sec)) continue;
                        if (IsHintMarker(pin, sec)) { HintMarkerSections++; continue; }
                        if (IsCovered(pin, sec)) continue;
                        if (HoldsNoCheck(sec)) { holdingNoCheck.Add((pin, sec)); continue; }
                        string name = !string.IsNullOrEmpty(sec.Name) ? sec.Name : pin.Name;
                        if (TryNameMatch(name, out long id, out var source) || TryComposedMatch(pin, sec, out id, out source) || TryTailMatch(sec, name, out id, out source)) Add(id, pin, sec, source);
                    }
                }
                else if (IsHintMarker(pin, null)) HintMarkerSections++;
                else if (!IsCovered(pin, null) && (TryNameMatch(pin.Name, out long id, out var source) || TryComposedMatch(pin, null, out id, out source)))
                {
                    Add(id, pin, null, source);
                }
            }

            // 3b. Sections named like their neighbours: when the mapping gives two or more of a pin's sections game names that
            //     are one prefix and the section's name ("KF Shop " + "Item 2"), a section left over takes the same prefix
            //     ("KF Shop Item 1"), if the game has that location and no pin shows it yet. (The Ship of Harkinian pack leaves
            //     every shop's "Item 1" out of its mapping; by their own name alone, all eight looked like Kokiri Forest's.)
            foreach (var pin in Pack.Locations)
            {
                if (pin.Sections == null || pin.Sections.Count < 3) continue;
                var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int named = 0;
                foreach (var sec in pin.Sections)
                {
                    if (string.IsNullOrWhiteSpace(sec?.Name) || IsReference(sec)) continue;
                    var ids = IdsFor(pin, sec);
                    if (ids.Count != 1 || !ByLocation[ids[0]].Any(m => m.Pin == pin && m.Section == sec && m.Source == MatchSource.MappingScript)) continue;
                    string game = LocationName(ids[0]) ?? "", own = sec.Name.Trim();
                    if (game.Length <= own.Length || !game.EndsWith(own, StringComparison.OrdinalIgnoreCase)) continue;
                    prefixes.Add(game.Substring(0, game.Length - own.Length));
                    named++;
                }
                if (named < 2 || prefixes.Count != 1) continue;
                string prefix = prefixes.First();
                foreach (var sec in pin.Sections)
                {
                    if (string.IsNullOrWhiteSpace(sec?.Name) || IsReference(sec) || IsHintMarker(pin, sec) || HoldsNoCheck(sec) || IsCovered(pin, sec)) continue;
                    if (_locationIdsByName.TryGetValue(prefix + sec.Name.Trim(), out long id) && !ByLocation.ContainsKey(id)) Add(id, pin, sec, MatchSource.NeighbourName);
                }
            }

            // 4. A section holding no check (a boss or entrance toggle) shows a location only when nothing else does and its
            //    name is the location's: placed is better than nowhere (Super Metroid's "Ridley" pin, a boss toggle, is the
            //    only place the game's "Ridley" can go).
            foreach (var (pin, sec) in holdingNoCheck)
            {
                string name = !string.IsNullOrEmpty(sec.Name) ? sec.Name : pin.Name;
                if ((TryNameMatch(name, out long id, out var source) || TryComposedMatch(pin, sec, out id, out source) || TryTailMatch(sec, name, out id, out source)) && !ByLocation.ContainsKey(id)) Add(id, pin, sec, source);
                else NoCheckSections++;
            }
        }

        private void ResolveReferences(Dictionary<string, List<PopTrackerLocation>> pinsByPath, Dictionary<string, List<PopTrackerLocation>> pinsByName)
        {
            foreach (var pin in Pack.Locations)
            {
                if (pin.Sections == null) continue;
                foreach (var sec in pin.Sections)
                {
                    if (sec == null || string.IsNullOrWhiteSpace(sec.Ref)) continue;
                    var target = FollowReference(sec, pinsByPath, pinsByName);
                    if (target == null && RepairReference(sec, pinsByPath, pinsByName) is { } repaired)
                    {
                        target = repaired.Target;
                        RepairedReferences.Add((pin, sec, repaired.Path));
                    }
                    if (target == null)
                    {
                        UnresolvedReferences.Add((pin, sec));
                        _unresolved[(pin.FullPath ?? "") + "|" + (sec.Name ?? "")] = sec.Ref;
                        continue;
                    }
                    _refTargets[sec] = target.Value;
                    if (!_referrers.TryGetValue(target.Value, out var list)) _referrers[target.Value] = list = new List<(PopTrackerLocation, PopTrackerSection)>();
                    list.Add((pin, sec));
                }
            }
        }

        /// <summary>
        /// The section a reference shows, through a chain of references (a few at most): an ordinary section, or a pin
        /// without sections. Null when it isn't in the pack, goes round in a circle, or names a pin with several sections
        /// without saying which (PopTracker can't show one either).
        /// </summary>
        private static (PopTrackerLocation Pin, PopTrackerSection Section)? FollowReference(PopTrackerSection section,
            Dictionary<string, List<PopTrackerLocation>> pinsByPath, Dictionary<string, List<PopTrackerLocation>> pinsByName)
        {
            var seen = new HashSet<PopTrackerSection>(ReferenceEqualityComparer.Instance) { section };
            var current = ResolvePath("@" + section.Ref.Trim().TrimStart('@'), pinsByPath, pinsByName);
            for (int hop = 0; current != null && hop < 8; hop++)
            {
                var (pin, target) = current.Value;
                if (target == null) return pin.Sections == null || pin.Sections.Count == 0 ? current : null;
                if (string.IsNullOrWhiteSpace(target.Ref)) return current;
                if (!seen.Add(target)) return null;
                current = ResolvePath("@" + target.Ref.Trim().TrimStart('@'), pinsByPath, pinsByName);
            }
            return null;
        }

        /// <summary>
        /// The section a reference that isn't in the pack means, from the section's own name: packs name an overview map's
        /// copy "Room - Check" after the section it shows, so the ref's area (its first part), the room and the check from the
        /// name, make the path. Only when that section is in the pack and is the same check the ref names (its last part).
        /// </summary>
        private static ((PopTrackerLocation Pin, PopTrackerSection Section) Target, string Path)? RepairReference(PopTrackerSection section,
            Dictionary<string, List<PopTrackerLocation>> pinsByPath, Dictionary<string, List<PopTrackerLocation>> pinsByName)
        {
            var parts = section.Ref.Trim().TrimStart('@').Split('/');
            string own = (section.Name ?? "").Trim();
            int dash = own.LastIndexOf(" - ", StringComparison.Ordinal);
            if (parts.Length < 2 || dash <= 0) return null;
            string room = own.Substring(0, dash).Trim(), check = own.Substring(dash + 3).Trim();
            if (!string.Equals(check, parts[^1].Trim(), StringComparison.OrdinalIgnoreCase)) return null;
            string path = parts[0].Trim() + "/" + room + "/" + check;
            var target = ResolvePath("@" + path, pinsByPath, pinsByName);
            if (target == null || target.Value.Section == null || !string.IsNullOrWhiteSpace(target.Value.Section.Ref)) return null;
            return (target.Value, path);
        }

        /// <summary>A mapping path that lands on a reference stands for the section the reference shows.</summary>
        private (PopTrackerLocation Pin, PopTrackerSection Section)? TargetOf((PopTrackerLocation Pin, PopTrackerSection Section)? match) =>
            match?.Section != null && _refTargets.TryGetValue(match.Value.Section, out var target) ? target : match;

        private bool IsCovered(PopTrackerLocation pin, PopTrackerSection section) =>
            IdsByPin.TryGetValue(pin, out var ids) && ids.Any(id => ByLocation[id].Any(m => m.Pin == pin && m.Section == section));

        /// <summary>Sections and pins that are hint markers in PopTracker packs ("Brawler - hint" / "Wave 1 Completed - hint", "Overworld Hints"): not checks.</summary>
        public static bool IsHintMarker(PopTrackerLocation pin, PopTrackerSection section)
        {
            static bool Marker(string name)
            {
                string n = (name ?? "").Trim().ToLowerInvariant();
                return n.EndsWith(" - hint", StringComparison.Ordinal) || n.EndsWith(" hint", StringComparison.Ordinal) || n.EndsWith(" hints", StringComparison.Ordinal) || n == "hint" || n == "hints";
            }
            if (section != null && Marker(section.Name)) return true;
            if (Marker(pin?.Name)) return true;
            foreach (string part in (pin?.FullPath ?? "").Split('/')) if (Marker(part)) return true;
            return false;
        }

        /// <summary>How many of the pack's sections are hint markers (left out of the pairing).</summary>
        public int HintMarkerSections { get; private set; }

        /// <summary>How many sections hold no check by PopTracker's count and nothing linked (left out of the pairing).</summary>
        public int NoCheckSections { get; private set; }

        /// <summary>How many sections can only be inspected (every access rule "{...}") and nothing linked: markers, not checks.</summary>
        public int InspectOnlySections => _inspectOnlySections ??= CountInspectOnly();
        private int? _inspectOnlySections;

        private int CountInspectOnly()
        {
            int n = 0;
            foreach (var pin in Pack.Locations)
            {
                if (pin.Sections != null && pin.Sections.Count > 0)
                {
                    foreach (var sec in pin.Sections)
                        if (!IsReference(sec) && !IsHintMarker(pin, sec) && !HoldsNoCheck(sec) && !IsCovered(pin, sec) && IsInspectOnly(pin, sec)) n++;
                }
                else if (!IsHintMarker(pin, null) && !IsCovered(pin, null) && IsInspectOnly(pin, null)) n++;
            }
            return n;
        }

        /// <summary>
        /// A section (or a section-less pin) that can only ever be inspected: every access rule it has (else its pin's) is
        /// PopTracker's "{...}" form, which makes it checkable for a look, never obtainable. Packs use it for markers that
        /// aren't checks: the player's position, entrance logic, hint signs, settings toggles (37 corpus packs, 14,000
        /// sections). A marker that a mapping or a name links is a check like any other.
        /// </summary>
        public static bool IsInspectOnly(PopTrackerLocation pin, PopTrackerSection section)
        {
            var rules = section?.AccessRulesRaw ?? pin?.AccessRulesRaw;
            if (rules == null) return false;
            var texts = new List<string>();
            void Collect(JToken token)
            {
                if (token is JArray array) foreach (var child in array) Collect(child);
                else if (token != null && token.Type == JTokenType.String) texts.Add(((string)token ?? "").Trim());
            }
            Collect(rules);
            return texts.Count > 0 && texts.All(t => t.Length >= 2 && t[0] == '{' && t[t.Length - 1] == '}');
        }

        /// <summary>
        /// A section PopTracker counts no check in: item_count 0, or a section that hosts an item (a toggle: an entrance, a
        /// boss) without an item_count. A mapping can still link one (a location mapped to the item it hosts).
        /// </summary>
        public static bool HoldsNoCheck(PopTrackerSection section) => section != null && section.ItemCount == 0;

        private static IEnumerable<string> HostedCodes(PopTrackerSection section) =>
            string.IsNullOrWhiteSpace(section?.HostedItem) ? Enumerable.Empty<string>() : section.HostedItem.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0);

        /// <summary>
        /// Whether a section (or a section-less pin) is one of the pack's checks: linked to a location, or neither a hint
        /// marker nor a section without a check (a reference counts as its target does).
        /// </summary>
        public bool CountsAsCheck(PopTrackerLocation pin, PopTrackerSection section)
        {
            if (IsCovered(pin, section)) return true;
            if (section != null && _refTargets.TryGetValue(section, out var target)) (pin, section) = target;
            return !IsHintMarker(pin, section) && !HoldsNoCheck(section) && !IsInspectOnly(pin, section);
        }

        /// <summary>
        /// Names many packs build from the pin and the section: "Wave 10 Completed (Brawler)" from pin "Brawler" and
        /// section "Wave 10 Completed" (or "Beachstickball (10 Hits)" from pin "Beachstickball" and section "10 Hits",
        /// "Brawler - Wave 10 Completed", "Brawler: Wave 10 Completed", "Brawler Wave 10 Completed"); and a pin or section
        /// "A - B" for the game's "B (A)".
        /// </summary>
        private bool TryComposedMatch(PopTrackerLocation pin, PopTrackerSection section, out long id, out MatchSource source)
        {
            source = MatchSource.ComposedName;
            id = 0;
            var candidates = new List<string>();
            string own = section != null && !string.IsNullOrEmpty(section.Name) ? section.Name.Trim() : null;
            string pinName = (pin?.Name ?? "").Trim();
            void With(string area, string name)
            {
                if (area.Length == 0 || name.Length == 0) return;
                candidates.Add(name + " (" + area + ")");
                candidates.Add(area + " (" + name + ")");
                candidates.Add(area + " - " + name);
                candidates.Add(area + ": " + name);
                candidates.Add(area + " " + name);
                candidates.Add(name + " [" + area + "]");
            }
            // A section that repeats its pin's name ("Hub 2-5 - Hub 2-5, item 9" under "Hub 2-5") is the rest of it.
            if (own != null && pinName.Length > 0 && own.StartsWith(pinName + " - ", StringComparison.OrdinalIgnoreCase))
            {
                string rest = own.Substring(pinName.Length + 3).Trim();
                candidates.Add(rest);
                With(pinName, rest);
            }
            if (own != null) With(pinName, own);
            // The pin's parents, nearest first, each whole and as its code (the part before ":" or " - "): Duke Nukem 3D's
            // "E3L5 3DRealms Atomic Health" is the section under "Episode 3/E3L5: Movie Set".
            string core = own ?? pinName;
            var path = (pin?.FullPath ?? "").Split('/');
            var ancestors = new List<string>();
            for (int i = path.Length - 2; i >= 0; i--)
            {
                string ancestor = path[i].Trim();
                if (ancestor.Length == 0 || ancestors.Contains(ancestor)) continue;
                ancestors.Add(ancestor);
                int cut = ancestor.IndexOf(':');
                int dashCut = ancestor.IndexOf(" - ", StringComparison.Ordinal);
                if (dashCut > 0 && (cut < 0 || dashCut < cut)) cut = dashCut;
                if (cut > 0) ancestors.Add(ancestor.Substring(0, cut).Trim());
            }
            foreach (string ancestor in ancestors)
            {
                With(ancestor, core);
                if (own != null && !string.Equals(core, pinName, StringComparison.OrdinalIgnoreCase)) With(ancestor, pinName + " " + own);
            }
            // A section's leading parenthetical belongs to the pin: "(Left) Item Near Center Warp" under "Amity Square" is the game's
            // "Amity Square (Left) - Item Near Center Warp".
            if (own != null && own.StartsWith("(") && own.IndexOf(") ", StringComparison.Ordinal) > 0)
            {
                int close = own.IndexOf(") ", StringComparison.Ordinal);
                string paren = own.Substring(0, close + 1), rest = own.Substring(close + 2).Trim();
                foreach (string area in new[] { pinName }.Concat(ancestors))
                {
                    if (area.Length == 0) continue;
                    candidates.Add(area + " " + paren + " - " + rest);
                    candidates.Add(area + " " + paren + " " + rest);
                    candidates.Add(area + " " + paren + ": " + rest);
                }
            }
            string reorder = own ?? pinName;
            int dash = reorder.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) candidates.Add(reorder[(dash + 3)..].Trim() + " (" + reorder[..dash].Trim() + ")");
            foreach (string candidate in candidates)
                if (_locationIdsByName.TryGetValue(candidate, out id)) return true;
            // As written, case and punctuation aside, when that names one location ("Clear Room 4" for "Clear Room 04").
            _idsByLooseName ??= BuildLooseNames();
            foreach (string candidate in candidates)
                if (_idsByLooseName.TryGetValue(Loose(candidate), out id)) return true;
            return false;
        }

        // Location names by their tail after the area ("Waynehouse: Basement Pot 1" → "Basement Pot 1"), and how many pins each
        // section name is on; both built when first needed.
        private Dictionary<string, List<long>> _idsByTail;
        private Dictionary<string, int> _pinsPerSectionName;

        /// <summary>
        /// A section named as the game names the check without its area ("Basement Pot 1" for "Waynehouse: Basement Pot 1",
        /// "Double Banana 1" for "Arcade 2: Double Banana 1"): when exactly one location's name ends so after its area, nothing
        /// shows that location yet, and the section's name is on one pin only (a "Chest" on many pins says nothing), that
        /// location is meant.
        /// </summary>
        private bool TryTailMatch(PopTrackerSection section, string name, out long id, out MatchSource source)
        {
            source = MatchSource.TailName;
            id = 0;
            if (section == null || string.IsNullOrWhiteSpace(name)) return false;
            _idsByTail ??= BuildTails();
            _pinsPerSectionName ??= Pack.Locations
                .SelectMany(p => (p.Sections ?? new List<PopTrackerSection>()).Select(s => s?.Name?.Trim()).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            string own = name.Trim();
            if (_pinsPerSectionName.TryGetValue(own, out int pins) && pins > 1) return false;
            if (!_idsByTail.TryGetValue(own, out var ids) || ids.Count != 1 || ByLocation.ContainsKey(ids[0])) return false;
            id = ids[0];
            return true;
        }

        private Dictionary<string, List<long>> BuildTails()
        {
            var tails = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _locationIdsByName)
            {
                // The area is the part before the first ": " or " - ".
                int colon = kv.Key.IndexOf(": ", StringComparison.Ordinal), dash = kv.Key.IndexOf(" - ", StringComparison.Ordinal);
                int cut = colon > 0 && (dash < 0 || colon < dash) ? colon + 2 : dash > 0 ? dash + 3 : -1;
                if (cut <= 0 || cut >= kv.Key.Length) continue;
                string tail = kv.Key.Substring(cut).Trim();
                if (tail.Length == 0) continue;
                if (!tails.TryGetValue(tail, out var list)) tails[tail] = list = new List<long>();
                list.Add(kv.Value);
            }
            return tails;
        }

        private bool TryNameMatch(string name, out long id, out MatchSource source)
        {
            source = MatchSource.Name;
            id = 0;
            if (string.IsNullOrEmpty(name)) return false;
            if (_locationIdsByName.TryGetValue(name, out id)) return true;
            // Loose: AP names like "Area: Thing - detail" vs pack names "Area: Thing".
            source = MatchSource.LooseName;
            foreach (var kv in _locationIdsByName)
            {
                int dash = kv.Key.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0 && string.Equals(kv.Key.Substring(0, dash).Trim(), name, StringComparison.OrdinalIgnoreCase)) { id = kv.Value; return true; }
            }
            return false;
        }

        /// <summary>"@A/B/C" → pin "A/B" + section "C"; a trailing "/" means the pin itself (or its only section).</summary>
        private static (PopTrackerLocation Pin, PopTrackerSection Section)? ResolvePath(string path,
            Dictionary<string, List<PopTrackerLocation>> pinsByPath, Dictionary<string, List<PopTrackerLocation>> pinsByName)
        {
            var parts = path.TrimStart('@').Split('/');
            if (parts.Length < 2) return null;
            string sectionName = parts[parts.Length - 1].Trim();
            string pinPath = string.Join("/", parts.Take(parts.Length - 1)).Trim();

            if (!pinsByPath.TryGetValue(pinPath, out var pins))
            {
                // The script may address the pin without all its parents, or the pack may nest differently.
                pins = pinsByPath.FirstOrDefault(kv => kv.Key.EndsWith("/" + pinPath, StringComparison.OrdinalIgnoreCase)).Value;
                if (pins == null && pinsByName.TryGetValue(parts[parts.Length - 2].Trim(), out var named) && named.Count == 1) pins = named;
            }
            // Of the pins sharing the path, the one that has the section named (the first otherwise, as PopTracker finds it).
            (PopTrackerLocation Pin, PopTrackerSection Section)? Resolve(PopTrackerLocation pin)
            {
                if (string.IsNullOrEmpty(sectionName))
                    return (pin, pin.Sections != null && pin.Sections.Count == 1 ? pin.Sections[0] : null);
                var section = pin.Sections?.FirstOrDefault(s => string.Equals(s.Name, sectionName, StringComparison.OrdinalIgnoreCase));
                if (section == null && (pin.Sections == null || pin.Sections.Count == 0) && string.Equals(pin.Name, sectionName, StringComparison.OrdinalIgnoreCase))
                    return (pin, null);
                return section == null ? null : (pin, section);
            }
            foreach (var pin in pins ?? new List<PopTrackerLocation>())
                if (Resolve(pin) is { } found) return found;
            // As the pack's author wrote it: a section named case and punctuation aside ("Hub 1-5, item 1" for "Hub 1-5 Item 1"),
            // or a child pin of that name with one section ("AWESOME Shop/Purchase 1" for a ref "AWESOME Shop/purchase 1"; the
            // parent, with children only, isn't a pin itself). Satisfactory's overworld refers to its tiers' checks so, 555 times.
            if (string.IsNullOrEmpty(sectionName)) return null;
            string loose = Loose(sectionName);
            foreach (var pin in pins ?? new List<PopTrackerLocation>())
            {
                var section = pin.Sections?.FirstOrDefault(s => Loose(s.Name) == loose);
                if (section != null) return (pin, section);
            }
            var child = pinsByPath.TryGetValue(pinPath + "/" + sectionName, out var exact) ? exact
                : pinsByPath.FirstOrDefault(kv => Loose(kv.Key) == Loose(pinPath + "/" + sectionName)).Value;
            var childPin = child?.FirstOrDefault(p => p.Sections != null && p.Sections.Count > 0) ?? child?.FirstOrDefault();
            return childPin == null ? null : (childPin, childPin.Sections != null && childPin.Sections.Count == 1 ? childPin.Sections[0] : null);
        }

        /// <summary>A name as its author might write it another time: lower case, letters and digits only, numbers without leading zeros ("Room 04" is "Room 4").</summary>
        private static string Loose(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new System.Text.StringBuilder(name.Length);
            int i = 0;
            while (i < name.Length)
            {
                char c = name[i];
                if (char.IsDigit(c))
                {
                    int start = i;
                    while (i < name.Length && char.IsDigit(name[i])) i++;
                    int first = start;
                    while (first < i - 1 && name[first] == '0') first++;
                    sb.Append(name, first, i - first);
                    continue;
                }
                if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
                i++;
            }
            return sb.ToString();
        }

        private static string LocationNameFromPath(string path)
        {
            var parts = path.TrimStart('@').Split('/').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            return parts.Count == 0 ? null : parts[parts.Count - 1];
        }

        private void Add(long id, PopTrackerLocation pin, PopTrackerSection section, MatchSource source, SectionMatch via = null)
        {
            if (!ByLocation.TryGetValue(id, out var matches)) ByLocation[id] = matches = new List<SectionMatch>();
            if (matches.Any(m => m.Pin == pin && m.Section == section)) return;
            var match = new SectionMatch { Pin = pin, Section = section, Source = source, Via = via };
            matches.Add(match);
            if (!IdsByPin.TryGetValue(pin, out var ids)) IdsByPin[pin] = ids = new List<long>();
            if (!ids.Contains(id)) ids.Add(id);
            // The sections that refer to this one show the same check (each refers to its final target, so this is one step).
            if (via == null && _referrers.TryGetValue((pin, section), out var referrers))
                foreach (var (referringPin, referring) in referrers) Add(id, referringPin, referring, source, match);
        }

        /// <summary>Applies a user fix: shows a location at a pin/section (and at the sections that refer to it).</summary>
        public void Link(long id, PopTrackerLocation pin, PopTrackerSection section) => Add(id, pin, section, MatchSource.UserFix);

        public void Unlink(long id, PopTrackerLocation pin)
        {
            if (ByLocation.TryGetValue(id, out var matches)) Remove(id, matches, matches.Where(m => m.Pin == pin).ToList());
            if (IdsByPin.TryGetValue(pin, out var ids)) ids.Remove(id);
        }

        /// <summary>Takes every location off one section (or a section-less pin), and off the sections that refer to it.</summary>
        public void ClearSection(PopTrackerLocation pin, PopTrackerSection section)
        {
            foreach (long id in IdsFor(pin).ToList())
            {
                if (!ByLocation.TryGetValue(id, out var matches)) continue;
                var removed = matches.Where(m => m.Pin == pin && m.Section == section).ToList();
                if (removed.Count > 0) Remove(id, matches, removed);
            }
        }

        // Removes matches of one location, with the copies the sections referring to them had.
        private void Remove(long id, List<SectionMatch> matches, List<SectionMatch> removed)
        {
            var gone = new HashSet<SectionMatch>(removed);
            gone.UnionWith(matches.Where(m => m.Via != null && gone.Contains(m.Via)).ToList());
            matches.RemoveAll(gone.Contains);
            foreach (var pin in gone.Select(m => m.Pin).Distinct())
                if (!matches.Any(m => m.Pin == pin) && IdsByPin.TryGetValue(pin, out var ids)) ids.Remove(id);
            if (matches.Count == 0) ByLocation.Remove(id);
        }

        public List<long> IdsFor(PopTrackerLocation pin) => IdsByPin.TryGetValue(pin, out var ids) ? ids : new List<long>();

        /// <summary>The ids one section of a pin stands for (every id of the pin when the section is null).</summary>
        public List<long> IdsFor(PopTrackerLocation pin, PopTrackerSection section) =>
            section == null ? IdsFor(pin) : IdsFor(pin).Where(id => ByLocation.TryGetValue(id, out var matches) && matches.Any(m => m.Pin == pin && m.Section == section)).ToList();

        /// <summary>Sections (or section-less pins) no AP location was paired with.</summary>
        public IEnumerable<(PopTrackerLocation Pin, PopTrackerSection Section)> UnmatchedSections()
        {
            foreach (var pin in Pack.Locations)
            {
                if (pin.Sections != null && pin.Sections.Count > 0)
                {
                    // A reference shows its target's check: the target is the one reported. A section without a check isn't one.
                    foreach (var sec in pin.Sections) if (!IsReference(sec) && !IsHintMarker(pin, sec) && !HoldsNoCheck(sec) && !IsCovered(pin, sec) && !IsInspectOnly(pin, sec)) yield return (pin, sec);
                }
                else if (!IsHintMarker(pin, null) && !IsCovered(pin, null) && !IsInspectOnly(pin, null)) yield return (pin, null);
            }
        }

        /// <summary>AP locations of this game that no pin shows.</summary>
        public IEnumerable<long> UnplacedLocations() => _locationNames.Keys.Where(id => !ByLocation.ContainsKey(id));

        public string LocationName(long id) => _locationNames.TryGetValue(id, out var n) ? n : null;

        public IReadOnlyDictionary<long, string> LocationNames => _locationNames;

        // =====================================================================
        // Items
        // =====================================================================

        /// <summary>A location's id by its name as a mapping script keys it: as written, or case and punctuation aside when that's unique.</summary>
        private bool TryLocationId(string name, out long id)
        {
            id = 0;
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (_locationIdsByName.TryGetValue(name.Trim(), out id)) return true;
            _idsByLooseName ??= BuildLooseNames();
            return _idsByLooseName.TryGetValue(Loose(name), out id);
        }

        private Dictionary<string, long> _idsByLooseName;

        private Dictionary<string, long> BuildLooseNames()
        {
            var loose = new Dictionary<string, long>(StringComparer.Ordinal);
            var clashing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in _locationIdsByName)
            {
                string key = Loose(kv.Key);
                if (key.Length == 0) continue;
                if (loose.ContainsKey(key)) clashing.Add(key);
                else loose[key] = kv.Value;
            }
            foreach (string key in clashing) loose.Remove(key);
            return loose;
        }

        private void BuildItems()
        {
            // A mapping keyed by the item's name (["Boo Portrait"] = {"boo_portrait"}, the Paper Mario packs) is the game's name for it.
            var itemsByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _itemNames) if (!string.IsNullOrEmpty(kv.Value)) itemsByName[kv.Value] = kv.Key;
            var itemMapping = new Dictionary<long, List<string>>(Pack.ItemMapping);
            foreach (var kv in Pack.ItemMappingByName)
            {
                if (!itemsByName.TryGetValue(kv.Key.Trim(), out long named))
                {
                    if (_itemNames.Count > 0) UnknownMappedItemNames.Add(kv.Key);
                    continue;
                }
                if (!itemMapping.TryGetValue(named, out var list)) itemMapping[named] = list = new List<string>();
                list.AddRange(kv.Value.Where(c => !list.Contains(c)));
            }
            ItemMappingOffset = Rebase(_itemNames, maps => CodesAgreeWithNames(maps[0]), itemMapping);
            foreach (var kv in itemMapping)
            {
                // An id the game doesn't have (outdated script): set aside so the tile can pair by name instead.
                if (_itemNames.Count > 0 && !_itemNames.ContainsKey(kv.Key))
                {
                    UnknownMappedItemIds.Add(kv.Key);
                    continue;
                }
                foreach (var code in kv.Value) AddItemCode(code, kv.Key);
            }
            // Names for pack items the script doesn't cover.
            if (_itemNames.Count == 0) return;
            var idsByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _itemNames) if (!string.IsNullOrEmpty(kv.Value)) idsByName[kv.Value] = kv.Key;
            foreach (var kv in Pack.ItemsByCode)
            {
                if (_itemIdsByCode.ContainsKey(kv.Key)) continue;
                if (kv.Value?.Name != null && idsByName.TryGetValue(kv.Value.Name, out long id)) AddItemCode(kv.Key, id);
                // A stage's code: the stage that has it may be named for the game's item ("Hookshot" of "Progressive Hookshot").
                else if (kv.Value?.Stages != null)
                    foreach (var stage in kv.Value.Stages)
                        if (stage?.Name != null && (stage.Codes ?? "").Split(',').Any(c => string.Equals(c.Trim(), kv.Key, StringComparison.OrdinalIgnoreCase))
                            && idsByName.TryGetValue(stage.Name, out long stageId))
                        {
                            AddItemCode(kv.Key, stageId);
                            break;
                        }
            }
            // A tile named as the game names the item, case and punctuation aside ("HM01 - Cut" for "HM01 Cut"), when that's unique.
            var looseItems = new Dictionary<string, long>(StringComparer.Ordinal);
            var clashing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in idsByName)
            {
                string key = Loose(kv.Key);
                if (key.Length == 0) continue;
                if (looseItems.ContainsKey(key)) clashing.Add(key);
                else looseItems[key] = kv.Value;
            }
            foreach (string key in clashing) looseItems.Remove(key);
            foreach (var kv in Pack.ItemsByCode)
                if (!_itemIdsByCode.ContainsKey(kv.Key) && !string.IsNullOrEmpty(kv.Value?.Name) && looseItems.TryGetValue(Loose(kv.Value.Name), out long id)) AddItemCode(kv.Key, id);
            // Tiles named as the game's items plus one word they share ("Airborne Egg Aiming Silo", "Beak Bayonet Silo": Banjo-Tooie's
            // silo moves): when three or more tiles end in the same word and each is the game's item without it, the word is the
            // pack's own label.
            var byLastWord = new Dictionary<string, List<(string Code, long Id)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Pack.ItemsByCode)
            {
                if (_itemIdsByCode.ContainsKey(kv.Key) || string.IsNullOrWhiteSpace(kv.Value?.Name)) continue;
                string name = kv.Value.Name.Trim();
                int space = name.LastIndexOf(' ');
                if (space <= 0) continue;
                string head = name.Substring(0, space).Trim(), last = name.Substring(space + 1);
                if (head.Length == 0 || !(idsByName.TryGetValue(head, out long id) || looseItems.TryGetValue(Loose(head), out id))) continue;
                if (!byLastWord.TryGetValue(last, out var list)) byLastWord[last] = list = new List<(string, long)>();
                list.Add((kv.Key, id));
            }
            foreach (var group in byLastWord.Values.Where(g => g.Count >= 3))
                foreach (var (code, id) in group) AddItemCode(code, id);
            // A tile named like a linked tile is the same item under another code (A Link to the Past's "Eastern Palace Small
            // Key" is "ep_smallkey", which the mapping links, and "ep_smallkey_drop" for its key-drop layout, which it doesn't).
            var idsByTileName = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Pack.ItemsByCode)
                if (!string.IsNullOrEmpty(kv.Value?.Name) && _itemIdsByCode.TryGetValue(kv.Key, out var linked) && linked.Count > 0 && !idsByTileName.ContainsKey(kv.Value.Name))
                    idsByTileName[kv.Value.Name] = linked;
            foreach (var kv in Pack.ItemsByCode)
                if (!_itemIdsByCode.ContainsKey(kv.Key) && !string.IsNullOrEmpty(kv.Value?.Name) && idsByTileName.TryGetValue(kv.Value.Name, out var same))
                    foreach (long id in same) AddItemCode(kv.Key, id);
        }

        private void AddItemCode(string code, long id)
        {
            if (!_itemIdsByCode.TryGetValue(code, out var ids)) _itemIdsByCode[code] = ids = new List<long>();
            if (!ids.Contains(id)) ids.Add(id);
        }

        /// <summary>Pairs a pack item code with an AP item (user fix).</summary>
        public void LinkItem(string code, long id) => AddItemCode(code, id);

        public void UnlinkItem(string code) => _itemIdsByCode.Remove(code);

        /// <summary>AP item ids a grid code stands for (empty when unknown).</summary>
        public List<long> ItemIdsFor(string code) => code != null && _itemIdsByCode.TryGetValue(code, out var ids) ? ids : new List<long>();

        public string ItemName(long id) => _itemNames.TryGetValue(id, out var n) ? n : null;

        public IReadOnlyDictionary<long, string> ItemNames => _itemNames;

        /// <summary>
        /// How many of a grid code's AP items were received. A code mapped to several ids (e.g. progressive
        /// stages) counts all of them.
        /// </summary>
        public int ReceivedCount(string code, IReadOnlyDictionary<long, int> receivedById, Func<string, int> receivedByName)
        {
            var ids = ItemIdsFor(code);
            if (ids.Count > 0) return ids.Sum(id => receivedById.TryGetValue(id, out int n) ? n : 0);
            // Unmapped: fall back to the pack item's display name.
            return Pack.ItemsByCode.TryGetValue(code, out var item) && item.Name != null ? receivedByName(item.Name) : 0;
        }
    }
}
