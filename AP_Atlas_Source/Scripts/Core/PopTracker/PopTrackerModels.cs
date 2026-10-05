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
        [JsonProperty("item_count")] public int ItemCount { get; set; } = 1;
        [JsonProperty("visibility_rules")] public JToken VisibilityRulesRaw { get; set; }
        [JsonProperty("clear_rules")] public JToken ClearRulesRaw { get; set; }
    }

    public class PopTrackerMapLocation
    {
        [JsonProperty("map")] public string Map { get; set; } = "";
        [JsonProperty("x")] public float X { get; set; } = 0f;
        [JsonProperty("y")] public float Y { get; set; } = 0f;
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

        // Metadata not bound via JSON, populated at load time
        [JsonIgnore] public string Id { get; set; } = "";
        [JsonIgnore] public Godot.ImageTexture BackgroundTexture { get; set; } = null;
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
        [JsonProperty("codes")] public string Codes { get; set; } = "";
    }

    public class PopTrackerItem
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("type")] public string Type { get; set; } = "toggle"; // toggle, progressive, consumable
        [JsonProperty("img")] public string Img { get; set; } = "";

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
    }
}
