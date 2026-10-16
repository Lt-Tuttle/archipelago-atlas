using System;
using System.Collections.Generic;
using System.Globalization;

namespace AP_Atlas.Core.PopTracker;

/// <summary>A change to a slot's map in plain words (the Map Tracker's Changes, Review fixes): what it's about and what it does now.</summary>
public static class PackChangeWords
{
    /// <summary>A pin's own name from its full path ("Firelink/Andre" → "Andre").</summary>
    public static string PinName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    public static string Tile(string tile, string? item, bool hidden, bool picture, Func<string, string> tr)
    {
        var parts = new List<string>();
        if (item != null) parts.Add(tr("shows {0}").Replace("{0}", item));
        if (hidden) parts.Add(tr("hidden"));
        if (picture) parts.Add(tr("your picture"));
        return tr("Tile {0}: {1}").Replace("{1}", parts.Count > 0 ? string.Join(", ", parts) : tr("as the pack has it")).Replace("{0}", tile);
    }

    public static string AddedTile(string item, Func<string, string> tr) => tr("Tile added for {0}").Replace("{0}", item);

    public static string Grid(string grid, bool? hidden, int? size, bool reordered, Func<string, string> tr)
    {
        var parts = new List<string>();
        if (hidden != null) parts.Add(hidden.Value ? tr("hidden") : tr("shown"));
        if (size != null) parts.Add(tr("tile size {0}").Replace("{0}", size.Value.ToString(CultureInfo.InvariantCulture)));
        if (reordered) parts.Add(tr("reordered"));
        return tr("Grid {0}: {1}").Replace("{1}", string.Join(", ", parts)).Replace("{0}", grid);
    }

    /// <summary>A pin (or one of its sections) linked to a check, or marked as no check (<paramref name="location"/> null).</summary>
    public static string Link(string pin, string? section, string? location, Func<string, string> tr)
    {
        string where = string.IsNullOrEmpty(section) || section == pin ? pin : pin + " / " + section;
        return location == null ? tr("{0}: not a check").Replace("{0}", where) : tr("{0} shows {1}").Replace("{1}", location).Replace("{0}", where);
    }

    public static string PinMoved(string pin, string map, Func<string, string> tr) => tr("Pin {0} moved on {1}").Replace("{1}", map).Replace("{0}", pin);
    public static string PinRemoved(string pin, string map, Func<string, string> tr) => tr("Pin {0} removed from {1}").Replace("{1}", map).Replace("{0}", pin);

    public static string PinAdded(string pin, string map, int checks, Func<string, string> tr) =>
        (checks == 1 ? tr("Pin {0} added on {1} (1 check)") : tr("Pin {0} added on {1} ({2} checks)").Replace("{2}", checks.ToString(CultureInfo.InvariantCulture)))
            .Replace("{1}", map).Replace("{0}", pin);

    public static string MapPicture(string map, Func<string, string> tr) => tr("Map {0}: your picture").Replace("{0}", map);

    public static string Hidden(string check, Func<string, string> tr) => tr("{0} hidden: left out of the Logic Tracker, the map and the in-logic counts").Replace("{0}", check);

    /// <summary>Why Atlas made a fix: its own reason, else for Fix what Atlas can's older fixes that it took the closest name; null for "the names match".</summary>
    public static string? WhyAtlas(string? reason, string? source) =>
        !string.IsNullOrWhiteSpace(reason) ? reason : source == "suggested" ? "it took the closest name (Fix what Atlas can)" : null;

    /// <summary>A Fix what Atlas can link's reason, with its real score: "its name matches 88% (Fix what Atlas can)".</summary>
    public static string SuggestedReason(double score) =>
        "its name matches " + ((int)Math.Round(score * 100)).ToString(CultureInfo.InvariantCulture) + "% (Fix what Atlas can)";

    /// <summary>Under a change Atlas made: why it was sure.</summary>
    public static string ByAtlas(string? why, Func<string, string> tr) =>
        string.IsNullOrWhiteSpace(why) ? tr("Atlas did this: the names match.") : tr("Atlas did this: {0}.").Replace("{0}", why.Trim().TrimEnd('.'));
}
