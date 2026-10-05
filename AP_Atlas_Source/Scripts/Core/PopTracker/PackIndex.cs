#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

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
        /// <summary>A fix the user made in the Pack Doctor.</summary>
        UserFix,
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

        /// <summary>Mapping-script item ids that aren't items of this game.</summary>
        public List<long> UnknownMappedItemIds { get; } = new List<long>();

        private readonly Dictionary<long, string> _locationNames;
        private readonly Dictionary<string, long> _locationIdsByName;
        private readonly Dictionary<long, string> _itemNames;
        private readonly Dictionary<string, List<long>> _itemIdsByCode = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

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
            var pinsByPath = new Dictionary<string, PopTrackerLocation>(StringComparer.OrdinalIgnoreCase);
            var pinsByName = new Dictionary<string, List<PopTrackerLocation>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in Pack.Locations)
            {
                if (!string.IsNullOrEmpty(pin.FullPath)) pinsByPath[pin.FullPath] = pin;
                if (!pinsByName.TryGetValue(pin.Name ?? "", out var list)) pinsByName[pin.Name ?? ""] = list = new List<PopTrackerLocation>();
                list.Add(pin);
            }

            // 1. Mapping script, keyed by id. An id the game doesn't have (an outdated script) is set aside, so the
            //    section can still be paired by name below.
            foreach (var kv in Pack.LocationMappingById)
            {
                if (_locationNames.Count > 0 && !_locationNames.ContainsKey(kv.Key))
                {
                    UnknownMappedLocationIds.Add(kv.Key);
                    continue;
                }
                foreach (var path in kv.Value)
                {
                    var match = ResolvePath(path, pinsByPath, pinsByName);
                    if (match == null) { DanglingPaths.Add((kv.Key, path)); continue; }
                    Add(kv.Key, match.Value.Pin, match.Value.Section, MatchSource.MappingScript);
                }
            }

            // 2. Mapping script without ids: the path's last named segment is the AP location name.
            foreach (var path in Pack.UnkeyedLocationPaths)
            {
                var match = ResolvePath(path, pinsByPath, pinsByName);
                string name = LocationNameFromPath(path);
                if (match == null || name == null || !_locationIdsByName.TryGetValue(name, out long id))
                {
                    DanglingPaths.Add((null, path));
                    continue;
                }
                Add(id, match.Value.Pin, match.Value.Section, MatchSource.MappingScript);
            }

            // 3. Names, for sections the scripts didn't cover.
            foreach (var pin in Pack.Locations)
            {
                if (pin.Sections != null && pin.Sections.Count > 0)
                {
                    foreach (var sec in pin.Sections)
                    {
                        if (IsCovered(pin, sec)) continue;
                        string name = !string.IsNullOrEmpty(sec.Name) ? sec.Name : pin.Name;
                        if (TryNameMatch(name, out long id, out var source)) Add(id, pin, sec, source);
                    }
                }
                else if (!IsCovered(pin, null) && TryNameMatch(pin.Name, out long id, out var source))
                {
                    Add(id, pin, null, source);
                }
            }
        }

        private bool IsCovered(PopTrackerLocation pin, PopTrackerSection section) =>
            IdsByPin.TryGetValue(pin, out var ids) && ids.Any(id => ByLocation[id].Any(m => m.Pin == pin && m.Section == section));

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
            Dictionary<string, PopTrackerLocation> pinsByPath, Dictionary<string, List<PopTrackerLocation>> pinsByName)
        {
            var parts = path.TrimStart('@').Split('/');
            if (parts.Length < 2) return null;
            string sectionName = parts[parts.Length - 1].Trim();
            string pinPath = string.Join("/", parts.Take(parts.Length - 1)).Trim();

            PopTrackerLocation pin = null;
            if (!pinsByPath.TryGetValue(pinPath, out pin))
            {
                // The script may address the pin without all its parents, or the pack may nest differently.
                pin = pinsByPath.FirstOrDefault(kv => kv.Key.EndsWith("/" + pinPath, StringComparison.OrdinalIgnoreCase)).Value;
                if (pin == null && pinsByName.TryGetValue(parts[parts.Length - 2].Trim(), out var named) && named.Count == 1) pin = named[0];
            }
            if (pin == null) return null;

            if (string.IsNullOrEmpty(sectionName))
                return (pin, pin.Sections != null && pin.Sections.Count == 1 ? pin.Sections[0] : null);
            var section = pin.Sections?.FirstOrDefault(s => string.Equals(s.Name, sectionName, StringComparison.OrdinalIgnoreCase));
            if (section == null && (pin.Sections == null || pin.Sections.Count == 0) && string.Equals(pin.Name, sectionName, StringComparison.OrdinalIgnoreCase))
                return (pin, null);
            return section == null ? null : (pin, section);
        }

        private static string LocationNameFromPath(string path)
        {
            var parts = path.TrimStart('@').Split('/').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            return parts.Count == 0 ? null : parts[parts.Count - 1];
        }

        private void Add(long id, PopTrackerLocation pin, PopTrackerSection section, MatchSource source)
        {
            if (!ByLocation.TryGetValue(id, out var matches)) ByLocation[id] = matches = new List<SectionMatch>();
            if (matches.Any(m => m.Pin == pin && m.Section == section)) return;
            matches.Add(new SectionMatch { Pin = pin, Section = section, Source = source });
            if (!IdsByPin.TryGetValue(pin, out var ids)) IdsByPin[pin] = ids = new List<long>();
            if (!ids.Contains(id)) ids.Add(id);
        }

        /// <summary>Applies a user fix: shows a location at a pin/section (replacing other pairings for that section).</summary>
        public void Link(long id, PopTrackerLocation pin, PopTrackerSection section) => Add(id, pin, section, MatchSource.UserFix);

        public void Unlink(long id, PopTrackerLocation pin)
        {
            if (ByLocation.TryGetValue(id, out var matches)) matches.RemoveAll(m => m.Pin == pin);
            if (IdsByPin.TryGetValue(pin, out var ids)) ids.Remove(id);
        }

        public List<long> IdsFor(PopTrackerLocation pin) => IdsByPin.TryGetValue(pin, out var ids) ? ids : new List<long>();

        /// <summary>Sections (or section-less pins) no AP location was paired with.</summary>
        public IEnumerable<(PopTrackerLocation Pin, PopTrackerSection Section)> UnmatchedSections()
        {
            foreach (var pin in Pack.Locations)
            {
                if (pin.Sections != null && pin.Sections.Count > 0)
                {
                    foreach (var sec in pin.Sections) if (!IsCovered(pin, sec)) yield return (pin, sec);
                }
                else if (!IsCovered(pin, null)) yield return (pin, null);
            }
        }

        /// <summary>AP locations of this game that no pin shows.</summary>
        public IEnumerable<long> UnplacedLocations() => _locationNames.Keys.Where(id => !ByLocation.ContainsKey(id));

        public string LocationName(long id) => _locationNames.TryGetValue(id, out var n) ? n : null;

        public IReadOnlyDictionary<long, string> LocationNames => _locationNames;

        // =====================================================================
        // Items
        // =====================================================================

        private void BuildItems()
        {
            foreach (var kv in Pack.ItemMapping)
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
            }
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
