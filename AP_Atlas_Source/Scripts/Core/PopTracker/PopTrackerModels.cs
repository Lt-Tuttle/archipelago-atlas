using System.Collections.Generic;
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
        
        public string GetActualVersion() {
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

        public List<string> GetCodes()
        {
            var list = new List<string>();
            if (CodesRaw == null) return list;
            
            if (CodesRaw.Type == JTokenType.String)
            {
                list.Add(CodesRaw.ToString());
            }
            else if (CodesRaw.Type == JTokenType.Array)
            {
                foreach (var token in CodesRaw)
                {
                    list.Add(token.ToString());
                }
            }
            return list;
        }
    }
}
