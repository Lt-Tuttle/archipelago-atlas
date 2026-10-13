#nullable disable
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Godot;

namespace AP_Atlas.Core.PopTracker
{
    public class PopTrackerSection
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("access_rules")] public JToken AccessRulesRaw { get; set; }
        /// <summary>The section's item_count as the pack gives it (null: not given).</summary>
        [JsonProperty("item_count")] public int? ItemCountGiven { get; set; }
        /// <summary>Item codes the section holds, comma-separated (PopTracker's "hosted_item"): a toggle shown in the section.</summary>
        [JsonProperty("hosted_item")] public string HostedItem { get; set; }
        /// <summary>How many checks the section holds, as PopTracker counts them: item_count, else 0 for a section that hosts an item, else 1.</summary>
        [JsonIgnore] public int ItemCount => ItemCountGiven ?? (string.IsNullOrWhiteSpace(HostedItem) ? 1 : 0);
        [JsonProperty("visibility_rules")] public JToken VisibilityRulesRaw { get; set; }
        [JsonProperty("clear_rules")] public JToken ClearRulesRaw { get; set; }
        /// <summary>
        /// "Location/Section" of another section this one stands for (PopTracker's "ref"): the same check shown on
        /// another map, everything else this section says ignored. Null for an ordinary section.
        /// </summary>
        [JsonProperty("ref")] public string Ref { get; set; }
    }

    public class PopTrackerMapLocation
    {
        [JsonProperty("map")] public string Map { get; set; } = "";
        [JsonProperty("x")] public float X { get; set; } = 0f;
        [JsonProperty("y")] public float Y { get; set; } = 0f;
        /// <summary>The pin's own size on this map (0: the map's).</summary>
        [JsonProperty("size")] public float Size { get; set; } = 0f;
        /// <summary>The pin's own border (below 0: the map's).</summary>
        [JsonProperty("border_thickness")] public float BorderThickness { get; set; } = -1f;
        /// <summary>The pin's own shape ("rect", "diamond", ...), or null for the map's.</summary>
        [JsonProperty("shape")] public string Shape { get; set; }
    }

    public class PopTrackerLocation
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("x")] public float X { get; set; } = 0f;
        [JsonProperty("y")] public float Y { get; set; } = 0f;
        [JsonProperty("map")] public string MapRef { get; set; } = "";
        [JsonProperty("map_locations")] public List<PopTrackerMapLocation> MapLocations { get; set; }
        [JsonProperty("access_rules")] public JToken AccessRulesRaw { get; set; }
        [JsonProperty("sections")] public List<PopTrackerSection> Sections { get; set; } = new List<PopTrackerSection>();
        [JsonProperty("children")] public List<PopTrackerLocation> Children { get; set; } = new List<PopTrackerLocation>();

        // Sometimes locations define item_count directly at the root if there are no sections
        [JsonProperty("item_count")] public int ItemCount { get; set; } = 1;

        /// <summary>"Parent/Child/Name" as mapping scripts address it ("@Parent/Child/Name/Section"). Set at load.</summary>
        [JsonIgnore] public string FullPath { get; set; } = "";
    }

    public class PopTrackerMap
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("map_bg")] public string MapBg { get; set; } = "";
        [JsonProperty("img")] public string Img { get; set; } = "";
        [JsonProperty("location_size")] public float LocationSize { get; set; } = 0f;
        /// <summary>The border of the map's pins (below 0: Atlas's).</summary>
        [JsonProperty("location_border_thickness")] public float LocationBorderThickness { get; set; } = -1f;
        /// <summary>The shape of the map's pins ("rect", "diamond", ...), or null for the Appearance setting.</summary>
        [JsonProperty("location_shape")] public string LocationShape { get; set; }

        // Metadata not bound via JSON, populated at load time
        [JsonIgnore] public string Id { get; set; } = "";
        [JsonIgnore] public Godot.ImageTexture BackgroundTexture { get; set; } = null;

        /// <summary>
        /// The background to show: <see cref="BackgroundTexture"/> while it's decoded and not freed since (a pack nobody
        /// uses has its images freed, see PackImages), else null.
        /// </summary>
        [JsonIgnore] public Godot.ImageTexture Background => BackgroundTexture is { } texture && Godot.GodotObject.IsInstanceValid(texture) ? texture : null;
    }

    public class PopTrackerManifest
    {
        [JsonProperty("name")] public string Name { get; set; } = "Unknown";
        [JsonProperty("game_name")] public string GameName { get; set; } = "Unknown";
        [JsonProperty("version")] public string Version { get; set; } = "1.0.0";
        [JsonProperty("package_version")] public string PackageVersion { get; set; } = "";
        [JsonProperty("author")] public string Author { get; set; } = "Unknown";
        [JsonProperty("versions_url")] public string VersionsUrl { get; set; } = "";
        [JsonProperty("package_uid")] public string PackageUid { get; set; } = "";

        /// <summary>Variant uid → { display_name, flags }. The first ("standard" when present) is what PopTracker opens by default.</summary>
        [JsonProperty("variants")] public JObject Variants { get; set; }

        public string GetActualVersion()
        {
            if (!string.IsNullOrEmpty(PackageVersion)) return PackageVersion;
            return Version;
        }
    }

    public class PopTrackerItemStage
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("img")] public string Img { get; set; } = "";
        /// <summary>The picture PopTracker shows for the stage while the item isn't active (else the stage's picture, dimmed).</summary>
        [JsonProperty("disabled_img")] public string DisabledImg { get; set; } = "";
        [JsonProperty("codes")] public string Codes { get; set; } = "";
    }

    public class PopTrackerItem
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("type")] public string Type { get; set; } = "toggle"; // toggle, progressive, consumable
        [JsonProperty("img")] public string Img { get; set; } = "";
        /// <summary>The picture PopTracker shows while the item isn't active (else its picture, dimmed).</summary>
        [JsonProperty("disabled_img")] public string DisabledImg { get; set; } = "";

        // Sometimes codes is a string, sometimes an array. We can use a custom converter or JToken.
        [JsonProperty("codes")] public JToken CodesRaw { get; set; }

        [JsonProperty("stages")] public List<PopTrackerItemStage> Stages { get; set; } = new List<PopTrackerItemStage>();
        [JsonProperty("max_quantity")] public int MaxQuantity { get; set; } = 1;

        /// <summary>
        /// The item's codes. PopTracker allows a comma-separated string ("cinders, abyss") or an array, so both are
        /// split and trimmed; an item answers to any of its codes.
        /// </summary>
        public List<string> GetCodes()
        {
            var list = new List<string>();
            if (CodesRaw == null) return list;

            IEnumerable<string> raw = CodesRaw.Type == JTokenType.Array
                ? CodesRaw.Select(t => t.ToString())
                : new[] { CodesRaw.ToString() };
            foreach (var part in raw.SelectMany(r => r.Split(',')))
            {
                string code = part.Trim();
                if (code.Length > 0 && !list.Contains(code)) list.Add(code);
            }
            return list;
        }

        /// <summary>
        /// Every code the item answers to: its own, then each stage's (a progressive item's codes are often on its stages
        /// only, as Ship of Harkinian's hookshot and spiritual stones are; PopTracker finds the item by any of them).
        /// </summary>
        public List<string> AllCodes()
        {
            var list = GetCodes();
            foreach (var stage in Stages ?? new List<PopTrackerItemStage>())
                foreach (var part in (stage?.Codes ?? "").Split(','))
                {
                    string code = part.Trim();
                    if (code.Length > 0 && !list.Contains(code)) list.Add(code);
                }
            return list;
        }

        /// <summary>The pack file the item came from ("items/settings_main.json"); not part of the pack's JSON.</summary>
        [JsonIgnore] public string SourceFile { get; set; } = "";

        /// <summary>
        /// One of the pack's own switches rather than a game item: a code that starts with "setting", or an item from a
        /// settings file (Ship of Harkinian's "Auto Tab" and "Show Out of Logic Checks"). No Archipelago item lights it.
        /// </summary>
        public bool IsPackSwitch(string code) =>
            (code ?? "").StartsWith("setting", System.StringComparison.OrdinalIgnoreCase)
            || System.IO.Path.GetFileName(SourceFile ?? "").Contains("setting", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A picture or label in the grid (PopTracker's "static" items: a dungeon's name over its column), or a tile made of two
        /// others (a "composite_toggle": its parts are the items to link): nothing to track by itself.
        /// </summary>
        public bool IsDecoration => string.Equals(Type, "static", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(Type, "composite_toggle", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One itemgrid from the pack's layouts, with the group it sits under.</summary>
    public class PackItemGrid
    {
        /// <summary>Header of the enclosing layout group (e.g. "Items", "Settings"), if any.</summary>
        public string Header { get; set; } = "";
        /// <summary>The layout key the grid was found under (e.g. "shared_item_grid").</summary>
        public string LayoutKey { get; set; } = "";
        /// <summary>The pack's tile size for this grid (PopTracker default 32).</summary>
        public int ItemSize { get; set; } = 32;
        public List<List<string>> Rows { get; set; } = new List<List<string>>();
        /// <summary>Looks like tracker options (e.g. "Shuffle Weapons") rather than Archipelago items; hidden by default.</summary>
        public bool LooksLikeSettings { get; set; }
        /// <summary>
        /// Decoration, not tiles: a grid whose item_size is too small for a tile (A Link to the Past's "Route" grids of 3 px
        /// black squares, drawn on by the pack's route mode). Key Items leaves it out, and the Doctor says nothing of it.
        /// </summary>
        public bool Decorative { get; set; }
    }
}
