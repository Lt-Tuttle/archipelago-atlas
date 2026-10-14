#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>Where a check no pin shows would go: beside the pin of a check from the same area.</summary>
    public sealed record Placement(long Id, string Name, string MapId, float X, float Y, string Near);

    /// <summary>
    /// Places the checks no pin shows near their area (the Pack Doctor's guide, previewed, at the user's say): a check's area
    /// is its name's tag ("UP: Andre" is in "UP"), and its spot is beside the pin of another check of that area, on the map
    /// where most of them are, nearest their middle; checks put by one pin go round it in a ring. General by design: it reads
    /// only the game's names and where the pack already shows checks. The pins it adds are Atlas's (<see cref="Source"/>):
    /// drawn quieter, moved or removed like any added pin.
    /// </summary>
    public static class PackPlacement
    {
        /// <summary>The added pins' source: placed by Atlas near their area.</summary>
        public const string Source = "placed";

        /// <summary>Where each check would go (only checks whose area has pins on a map).</summary>
        public static List<Placement> Plan(LoadedPack pack, PackIndex index, IEnumerable<long> unplaced) =>
            pack == null || index == null ? new List<Placement>() : PlanFrom(pack, index, Spots(index, null), unplaced);

        /// <summary>Where each area's checks are shown (map, spot, pin), leaving one check out (to measure).</summary>
        private static Dictionary<string, List<(string Map, float X, float Y, string Pin)>> Spots(PackIndex index, long? leaveOut)
        {
            var spots = new Dictionary<string, List<(string Map, float X, float Y, string Pin)>>(StringComparer.Ordinal);
            foreach (var (id, matches) in index.ByLocation)
            {
                if (id == leaveOut) continue;
                string area = PackDoctor.AreaOf(index.LocationName(id) ?? "");
                if (area.Length == 0) continue;
                if (!spots.TryGetValue(area, out var list)) spots[area] = list = new List<(string, float, float, string)>();
                foreach (var match in matches)
                    foreach (var (map, x, y) in PlacesOf(match.Pin))
                        list.Add((map, x, y, match.Pin.Name ?? match.Pin.FullPath));
            }
            return spots;
        }

        private static List<Placement> PlanFrom(LoadedPack pack, PackIndex index, Dictionary<string, List<(string Map, float X, float Y, string Pin)>> spots, IEnumerable<long> unplaced)
        {
            var placements = new List<Placement>();
            var ringSize = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var taken = new Dictionary<(string Map, float X, float Y), int>();
            foreach (long id in unplaced.Distinct().OrderBy(id => index.LocationName(id), StringComparer.Ordinal))
            {
                string name = index.LocationName(id) ?? "";
                string area = PackDoctor.AreaOf(name);
                if (area.Length == 0 || !spots.TryGetValue(area, out var near) || near.Count == 0) continue;
                var onMap = near.GroupBy(s => s.Map, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().ToList();
                float cx = onMap.Average(s => s.X), cy = onMap.Average(s => s.Y);
                var anchor = onMap.OrderBy(s => (s.X - cx) * (s.X - cx) + (s.Y - cy) * (s.Y - cy)).ThenBy(s => s.Pin, StringComparer.Ordinal).First();
                float radius = RingOf(pack, anchor.Map, ringSize);
                var key = (anchor.Map, anchor.X, anchor.Y);
                int n = taken.TryGetValue(key, out int count) ? count : 0;
                taken[key] = n + 1;
                // Round the pin: six to a ring, the next ring further out.
                int ring = n / 6 + 1;
                double angle = (n % 6) * Math.PI / 3 + ring * 0.5;
                placements.Add(new Placement(id, name, anchor.Map, anchor.X + (float)(Math.Cos(angle) * radius * ring), anchor.Y + (float)(Math.Sin(angle) * radius * ring), anchor.Pin));
            }
            return placements;
        }

        /// <summary>How far from a pin its neighbours go: a sixtieth of the map's spread of pins, at least 12 map pixels.</summary>
        private static float RingOf(LoadedPack pack, string mapId, Dictionary<string, float> cache)
        {
            if (cache.TryGetValue(mapId, out float known)) return known;
            var all = pack.Locations.SelectMany(PlacesOf).Where(p => string.Equals(p.Map, mapId, StringComparison.OrdinalIgnoreCase)).ToList();
            float spread = all.Count < 2 ? 0 : Math.Max(all.Max(p => p.X) - all.Min(p => p.X), all.Max(p => p.Y) - all.Min(p => p.Y));
            return cache[mapId] = Math.Max(12f, spread / 60f);
        }

        /// <summary>Every spot a pin is drawn at: its map and position, and each of its map_locations.</summary>
        public static IEnumerable<(string Map, float X, float Y)> PlacesOf(PopTrackerLocation pin)
        {
            if (!string.IsNullOrEmpty(pin.MapRef)) yield return (pin.MapRef, pin.X, pin.Y);
            foreach (var place in pin.MapLocations ?? new List<PopTrackerMapLocation>())
                if (!string.IsNullOrEmpty(place.Map)) yield return (place.Map, place.X, place.Y);
        }

        /// <summary>Adds the planned pins, as Atlas's, in one undoable step.</summary>
        public static void Apply(LoadedPack original, IReadOnlyList<Placement> placements)
        {
            if (placements.Count == 0) return;
            PackFixes.Edit(PackFixes.KeyFor(original), $"Place {placements.Count} check{(placements.Count == 1 ? "" : "s")} near their area", file =>
            {
                foreach (var p in placements)
                {
                    string subject = "added:pin:" + p.Id;
                    file.AddedPins.RemoveAll(a => a.Subject == subject);
                    file.AddedPins.Add(new AddedPin { Subject = subject, Name = p.Name, MapId = p.MapId, X = p.X, Y = p.Y, ApLocationIds = new List<long> { p.Id }, Source = Source, Reason = "near " + p.Near });
                }
            });
        }

        /// <summary>
        /// How right the placement is, measured on the pack's own pins: each check a pin shows whose area has other pins is
        /// taken off the map and placed again; says how many could be placed, how many landed on the pin's own map, and of
        /// those how many within a tenth of the map's spread of pins from where the pack has it.
        /// </summary>
        public static (int Tried, int SameMap, int Close) Evaluate(LoadedPack pack, PackIndex index, int most = 400)
        {
            int tried = 0, sameMap = 0, close = 0;
            if (pack == null || index == null) return (0, 0, 0);
            var spreads = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, matches) in index.ByLocation.OrderBy(kv => kv.Key).Take(most * 3))
            {
                if (tried >= most) break;
                var truth = matches.SelectMany(m => PlacesOf(m.Pin)).ToList();
                if (truth.Count == 0 || PackDoctor.AreaOf(index.LocationName(id) ?? "").Length == 0) continue;
                // The same plan, with this check's own pins left out of its area.
                var placed = PlanFrom(pack, index, Spots(index, id), new[] { id }).FirstOrDefault();
                if (placed == null) continue;
                tried++;
                var onSameMap = truth.Where(t => string.Equals(t.Map, placed.MapId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (onSameMap.Count == 0) continue;
                sameMap++;
                float spread = RingOf(pack, placed.MapId, spreads) * 60f;
                if (onSameMap.Any(t => Math.Sqrt((t.X - placed.X) * (t.X - placed.X) + (t.Y - placed.Y) * (t.Y - placed.Y)) <= spread / 10)) close++;
            }
            return (tried, sameMap, close);
        }
    }
}
