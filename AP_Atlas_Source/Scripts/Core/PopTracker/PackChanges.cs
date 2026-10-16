#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>Where a change shows on the map: a location, or a pin on a map (a map alone for its picture); null when it has no place there.</summary>
    public sealed record PackChangeSpot(long? LocationId, string? MapId, string? PinPath);

    /// <summary>
    /// One fix to a pack as the Map Tracker's Changes and Review fixes list it: its kind (as PackFixes.Reset takes it), its subject,
    /// its line, who made it (the fix's Source: null the user, "automatic" Atlas by itself, "suggested" Fix what Atlas can,
    /// "example" a rule from the user's examples), why, when, and where it shows.
    /// </summary>
    public sealed record PackChange(string Category, string Subject, string Text, string? Source, string? Why, DateTime Made, PackChangeSpot? Spot)
    {
        /// <summary>Atlas chose it (by itself, or on Fix what Atlas can).</summary>
        public bool ByAtlas => Source is "automatic" or "suggested";
    }

    /// <summary>
    /// A pack's fixes as changes, named from the author's pack, newest first; each undone in one step (Atlas's own declined, so
    /// its automatic pass leaves them to the user). Main thread.
    /// </summary>
    public static class PackChanges
    {
        /// <param name="unaskedOnly">Only what Atlas made by itself (Review fixes).</param>
        public static List<PackChange> Of(PackFixFile file, LoadedPack? pack, Func<string, string> tr, bool unaskedOnly = false)
        {
            string Map(string mapId) => pack != null && pack.Maps.TryGetValue(mapId ?? "", out var map) ? pack.DisplayNameOfMap(map.Name) : mapId ?? "";
            string TileName(string code) => pack != null && pack.ItemsByCode.TryGetValue(code, out var item) && !string.IsNullOrEmpty(item.Name) ? item.Name : code;
            var changes = new List<PackChange>();
            foreach (var t in file.Tiles)
                changes.Add(new PackChange("tiles", t.Subject, PackChangeWords.Tile(TileName(t.Code), t.ApItemId != null ? t.ApItemName ?? t.ApItemId.ToString() : null, t.Hidden, t.ImageFile != null, tr), t.Source, PackChangeWords.WhyAtlas(t.Reason, t.Source), t.Made, null));
            foreach (var t in file.AddedTiles)
                changes.Add(new PackChange("tiles", t.Subject, PackChangeWords.AddedTile(t.ApItemName, tr), t.Source, t.Reason, t.Made, null));
            foreach (var g in file.Grids)
                changes.Add(new PackChange("grids", g.Subject, PackChangeWords.Grid(g.Subject.Substring(g.Subject.IndexOf(':') + 1), g.Hidden, g.ItemSize, g.Rows != null, tr), g.Source, g.Reason, g.Made, null));
            foreach (var l in file.Links)
                changes.Add(new PackChange("links", l.Subject, PackChangeWords.Link(PackChangeWords.PinName(l.PinPath), l.SectionName, l.ApLocationId != null ? l.ApLocationName ?? l.ApLocationId.ToString() : null, tr),
                    l.Source, PackChangeWords.WhyAtlas(l.Reason, l.Source), l.Made, new PackChangeSpot(l.ApLocationId, null, l.PinPath)));
            foreach (var p in file.Pins)
                changes.Add(new PackChange("pins", p.Subject, p.Removed ? PackChangeWords.PinRemoved(PackChangeWords.PinName(p.PinPath), Map(p.MapId), tr) : PackChangeWords.PinMoved(PackChangeWords.PinName(p.PinPath), Map(p.MapId), tr),
                    p.Source, p.Reason, p.Made, new PackChangeSpot(null, p.MapId, p.PinPath)));
            foreach (var p in file.AddedPins)
                changes.Add(new PackChange("pins", p.Subject, PackChangeWords.PinAdded(p.Name, Map(p.MapId), p.ApLocationIds.Count, tr), p.Source, p.Reason, p.Made,
                    new PackChangeSpot(p.ApLocationIds.Count > 0 ? p.ApLocationIds[0] : null, p.MapId, p.PinPath)));
            foreach (var m in file.MapImages)
                changes.Add(new PackChange("maps", m.Subject, PackChangeWords.MapPicture(Map(m.MapId), tr), m.Source, m.Reason, m.Made, new PackChangeSpot(null, m.MapId, null)));
            return changes.Where(c => !unaskedOnly || c.Source == "automatic").OrderByDescending(c => c.Made).ToList();
        }

        /// <summary>Takes one change back, in one undoable step (Atlas's own is declined: PackFixes.Remove); a section marked "not a check" is asked about again.</summary>
        public static void Undo(string key, PackChange change) =>
            PackFixes.Edit(key, "Undo: " + change.Text, f =>
            {
                PackFixes.Remove(f, change.Category, change.Subject);
                if (change.Category == "links" && change.Spot?.LocationId == null)
                {
                    string what = change.Subject.Substring(change.Subject.IndexOf(':') + 1);
                    f.Ignored.RemoveAll(k => k == "loc:unmatched:" + what || k == "loc:untracked:" + what);
                }
            });

        /// <summary>Review fixes' Undo all: every fix Atlas made by itself among <paramref name="changes"/>, taken back and declined in one step.</summary>
        public static void UndoAll(string key, IReadOnlyCollection<PackChange> changes)
        {
            var subjects = changes.Where(c => c.Source == "automatic").Select(c => c.Subject).Distinct(StringComparer.Ordinal).ToList();
            PackFixes.Decline(key, subjects, $"Undo {subjects.Count} of Atlas's fixes");
        }
    }
}
