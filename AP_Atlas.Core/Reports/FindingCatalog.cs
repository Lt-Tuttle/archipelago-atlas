#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.Reports
{
    /// <summary>One problem as the Pack Doctor (or the solo test) found it: its key ("tile:noimage:jack"), area, severity, title.</summary>
    public sealed record SoloRawFinding(string Key, string Area, string Severity, string Title, string? Detail = null, IReadOnlyList<string>? Details = null);

    /// <summary>
    /// The findings of one kind, grouped for the report: how many, a few examples, and the catalog's explanation (what it
    /// means for the user, what Atlas did, and a line for the pack's author when the pack can fix it).
    /// </summary>
    public sealed record SoloFinding(string Kind, string Area, string Severity, int Count, string Summary, IReadOnlyList<string> Examples,
        string ForUser, string AtlasDid, string? ForAuthor);

    /// <summary>
    /// What each kind of finding means, in plain words: the one place the report's explanations live. A kind is a finding
    /// key's first one or two parts ("tile:noimage"); every kind the Pack Doctor and the solo test produce has an entry
    /// (the unit tests hold the list). An unknown kind still reports, with a general explanation.
    /// </summary>
    public static class FindingCatalog
    {
        /// <summary>One kind's explanation. {n} in the summary is the count.</summary>
        public sealed record Entry(string Summary, string ForUser, string AtlasDid, string? ForAuthor = null);

        /// <summary>The severities in the order the report shows them.</summary>
        public static readonly IReadOnlyList<string> SeverityOrder = new[] { "Problem", "Warning", "Info", "AutoFixed", "Ignored" };

        private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal)
        {
            // ---- The game's names ----
            ["names:none"] = new("Atlas had no list of the game's item and location names when it checked the pack",
                "Pins and tiles can't be matched to the game's checks and items until the names are known.",
                "Nothing yet: the names come with a connected slot or the Atlas Engine."),
            ["names:diff"] = new("The installed apworld's names differ from the server's",
                "A pin or tile named after the other version may not match.",
                "Used the server's names, which are the ones the seed has."),
            // ---- The pack's files ----
            ["file"] = new("{n} of the pack's files are missing or can't be read",
                "Whatever uses them (a map picture, a tile's image, a script) shows blank or does nothing.",
                "Showed everything else; a missing picture or image is named where it would be.",
                "These files are named by the pack but aren't in it, or can't be read."),
            ["file:nomapping"] = new("The pack has no autotracking mapping scripts",
                "Atlas pairs the pack's pins and tiles with the game's checks and items by their names only.",
                "Paired by name, and linked exact name matches by itself."),
            // ---- Atlas's own and the user's fixes ----
            ["auto"] = new("Atlas linked {n} pins or tiles whose names match the game's exactly",
                "They work without anyone choosing them.",
                "Linked them on the pack's first check; each can be undone under Your fixes."),
            ["tile:stalefix"] = new("{n} of your tile fixes name items this game doesn't have",
                "Those tiles don't light up.",
                "Kept your fix, and flagged it for you to link again or reset."),
            ["fixes:duplicate"] = new("Your fixes link several pin sections to the same location",
                "Only one of them is likely right; the others light up together.",
                "Kept your fixes, and flagged them."),
            ["superseded"] = new("{n} of your fixes were set aside after the pack updated",
                "The author's newer version is used instead of your fix.",
                "Set your fix aside (it can be restored)."),
            // ---- Scripts ----
            ["script:load"] = new("The pack's scripts couldn't be read",
                "Seed settings and script-driven tracking don't show.",
                "Used the pack's item mappings and names instead.",
                "The pack's scripts couldn't be read."),
            ["script:none"] = new("The pack has no scripts",
                "There are no seed-setting indicators; items are tracked by name.",
                "Tracked by the pack's mappings and names."),
            ["script:stopped"] = new("Atlas stopped the pack's scripts",
                "A script ran far longer or deeper than a real pack's; tracking that depends on it stops.",
                "Stopped the scripts so Atlas stays responsive; Key Items use the pack's item mappings.",
                "A script of the pack runs away (it was stopped after a very long or deep run)."),
            ["script:error"] = new("The pack's scripts hit {n} error(s)",
                "What the failing part does (often loading a file) doesn't happen.",
                "Went on past the error; the rest of the scripts ran.",
                "The pack's scripts fail here (often a file they load that isn't in the pack)."),
            ["script:unsupported"] = new("The pack's scripts use PopTracker features Atlas doesn't emulate",
                "Tracking that depends on them (usually reading the game's memory) is missing.",
                "Ignored those calls; everything else runs."),
            ["script:follows-maps"] = new("Live map following",
                "Whether the map can switch to where you are in the game.",
                "Follows the game's map when the pack says where you are through the room's data."),
            ["script:writes"] = new("The pack's scripts try to write the room's data storage",
                "Nothing: reading is enough for tracking.",
                "Never writes for a pack; the writes did nothing."),
            ["script:unread-files"] = new("The pack's scripts load files from places Atlas doesn't read",
                "Anything defined only there doesn't show.",
                "Read the pack's usual folders.",
                "Files are loaded from outside the usual items, maps, locations and layouts folders."),
            ["script:noautotracking"] = new("The pack's scripts don't register Archipelago handlers",
                "Items are tracked from the pack's mappings and names.",
                "Tracked by the mappings and names."),
            // ---- Seed settings ----
            ["settings:noslotdata"] = new("The seed settings weren't checked (no slot of this game connected)",
                "Nothing until a slot connects.",
                "Waits for a slot's options."),
            ["settings:summary"] = new("Seed settings read from the slot's options",
                "They show under Seed settings in Key Items and Properties.",
                "Read them through the pack's scripts."),
            ["settings:missing"] = new("{n} seed-setting indicators read options this seed doesn't have",
                "Those indicators stay at their default.",
                "Left them at their default.",
                "Indicators read slot options this apworld version doesn't send (the pack may target another version)."),
            // ---- Key Items ----
            ["grid:hidden"] = new("{n} grids show seed settings, not items",
                "They show under Seed settings instead of among the items.",
                "Moved them there by itself."),
            ["tile:unknown"] = new("{n} grid cells use codes no pack item defines",
                "Those cells are empty unless Atlas can name them.",
                "Left the cells out, or showed the linked item's name where one is linked.",
                "The item grid uses codes that no item in the pack defines."),
            ["tile:unlinked"] = new("{n} tiles aren't linked to one of the game's items",
                "They never light up when the item arrives.",
                "Left them unlit and listed them for review.",
                "These tiles name no item of the game (no code or name matches)."),
            ["tile:noimage"] = new("{n} tiles have no image",
                "Each shows as a framed tile with the item's name instead of a picture.",
                "Showed the item's name on the tile, and named the missing file in its tooltip.",
                "These tiles' images aren't in the pack."),
            ["itemmap:stale"] = new("item_mapping.lua lists {n} item ids that aren't items of this game",
                "Nothing visible; those lines are never used.",
                "Ignored them.",
                "item_mapping.lua lists ids the apworld doesn't have (it may be for another apworld version)."),
            // ---- Locations and maps ----
            ["loc:unmatched"] = new("{n} pin sections aren't linked to one of the game's checks",
                "They stay red and never clear.",
                "Left them as they are and listed them for review (each can be linked or ignored in the Pack Doctor).",
                "These pin sections match no location of the apworld by name or mapping."),
            ["loc:loose"] = new("{n} pin sections were matched by a partial name",
                "They're usually right.",
                "Matched them on the name's part before \" - \"."),
            ["locmap:dangling"] = new("location_mapping.lua points at {n} pins or sections that don't exist",
                "Those checks have no pin from the script (names may still pair some).",
                "Paired what it could by name.",
                "location_mapping.lua names pins or sections that aren't in the pack's locations."),
            ["locmap:stale"] = new("location_mapping.lua lists {n} ids that aren't locations of this game",
                "Nothing visible; those lines are never used.",
                "Ignored them.",
                "location_mapping.lua lists ids the apworld doesn't have (it may be for another apworld version)."),
            ["loc:unplaced"] = new("Some of the game's locations have no pin on any map",
                "Locations your options turn off don't matter; the seed's own unplaced checks are listed separately below.",
                "Shows every check in the Logic Tracker, pin or not."),
            ["map:nobg"] = new("{n} maps have no picture",
                "The map's pins show without the picture behind them.",
                "Showed the pins and named the missing picture on the map.",
                "These maps' pictures aren't in the pack, or can't be read."),
            ["map:empty"] = new("{n} maps have no pins",
                "Nothing to check on them.",
                "Showed them as they are."),
            ["map:outside"] = new("Pins sit outside their map's picture",
                "They're out of sight unless the map is panned past its edge.",
                "Showed them where they are.",
                "These pins' coordinates fall outside their map's picture."),
            // ---- The solo test's own ----
            ["logic:differs"] = new("Atlas's logic differs from the generator's at {n} sphere(s)",
                "Some checks show in logic too early or too late.",
                "Reported the spheres and checks where it differs."),
            ["sphere0:notreached"] = new("{n} checks the generator can reach at the start weren't in Atlas's logic at connect",
                "They looked out of logic though they weren't.",
                "Listed them."),
            ["sphere0:beyond"] = new("{n} checks were in Atlas's logic at connect but not in the generator's sphere 0",
                "They looked in logic though they weren't yet.",
                "Listed them."),
            ["seed:unplaced"] = new("{n} of this seed's checks have no pin on any of the pack's maps",
                "They're tracked in the Logic Tracker but can't be found on the map.",
                "Counted them; they show in the Logic Tracker.",
                "These locations of the apworld have no pin (with the test's default options)."),
            ["setup:guide"] = new("The game's apworld comes with a setup guide",
                "It says what the game itself needs to connect (a client, a mod, a patcher).",
                "Offers it on the game's page (Setup guide, from the apworld)."),
        };

        /// <summary>Every kind the catalog explains.</summary>
        public static IReadOnlyCollection<string> Kinds => Entries.Keys;

        /// <summary>A finding key's kind: the longest catalog kind it starts with ("tile:noimage:jack" → "tile:noimage"), else its first part.</summary>
        public static string KindOf(string key)
        {
            string best = "";
            foreach (string kind in Entries.Keys)
                if ((key == kind || key.StartsWith(kind + ":", StringComparison.Ordinal)) && kind.Length > best.Length) best = kind;
            if (best.Length > 0) return best;
            int colon = key.IndexOf(':');
            return colon < 0 ? key : key[..colon];
        }

        /// <summary>The explanation of a kind; a general one for a kind the catalog doesn't know.</summary>
        public static Entry Explain(string kind) => Entries.TryGetValue(kind, out var entry) ? entry
            : new Entry("Findings of kind \"" + kind + "\"", "See the examples.", "Reported them.");

        /// <summary>
        /// Groups findings by kind (and severity), most serious first, then most frequent: Brotato's thousands of lines
        /// become a few groups, each with up to <paramref name="examples"/> examples.
        /// </summary>
        public static List<SoloFinding> Group(IEnumerable<SoloRawFinding> raw, int examples = 8)
        {
            return raw.GroupBy(f => (Kind: KindOf(f.Key), f.Severity))
                .Select(g =>
                {
                    var entry = Explain(g.Key.Kind);
                    var items = g.ToList();
                    // One finding: its own words and evidence; several: their titles.
                    var shown = items.Count == 1
                        ? new[] { items[0].Title }.Concat(items[0].Detail is { Length: > 0 } d ? new[] { d } : Array.Empty<string>()).Concat(items[0].Details ?? Array.Empty<string>()).Take(examples).ToList()
                        : items.Select(f => f.Title).Distinct().Take(examples).ToList();
                    bool ignored = g.Key.Severity == "Ignored";
                    return new SoloFinding(g.Key.Kind, items[0].Area, g.Key.Severity, items.Count, entry.Summary.Replace("{n}", items.Count.ToString()), shown,
                        entry.ForUser, ignored ? "You chose to ignore these in the Pack Doctor." : entry.AtlasDid, ignored ? null : entry.ForAuthor);
                })
                .OrderBy(f => Rank(f.Severity)).ThenByDescending(f => f.Count).ThenBy(f => f.Kind, StringComparer.Ordinal)
                .ToList();
        }

        private static int Rank(string severity)
        {
            int i = -1;
            for (int k = 0; k < SeverityOrder.Count; k++) if (SeverityOrder[k] == severity) i = k;
            return i < 0 ? SeverityOrder.Count : i;
        }
    }
}
