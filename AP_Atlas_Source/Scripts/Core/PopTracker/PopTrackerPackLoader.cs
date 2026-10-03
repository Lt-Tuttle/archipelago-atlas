using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Godot;

namespace AP_Atlas.Core.PopTracker
{
    public class LoadedPack
    {
        public PopTrackerManifest Manifest { get; set; } = new PopTrackerManifest();
        public Dictionary<string, PopTrackerItem> ItemsByCode { get; set; } = new Dictionary<string, PopTrackerItem>();
        public List<List<string>> ItemGrids { get; set; } = new List<List<string>>(); // A flattened list of all item rows
        public Dictionary<string, ImageTexture> Images { get; set; } = new Dictionary<string, ImageTexture>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PopTrackerMap> Maps { get; set; } = new Dictionary<string, PopTrackerMap>(StringComparer.OrdinalIgnoreCase);
        public List<PopTrackerLocation> Locations { get; set; } = new List<PopTrackerLocation>();
    }

    public static class PopTrackerPackLoader
    {
        private static Dictionary<string, (DateTime lastWrite, LoadedPack pack)> _inspectionCache = new Dictionary<string, (DateTime, LoadedPack)>();

        public static string GetPacksDirectory()
        {
            string dir = Path.Combine(DataManager.GetDataDirectory(), "packs");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        public static LoadedPack InspectZipPack(string zipPath, Action<string> logDebug = null)
        {

            var info = new System.IO.FileInfo(zipPath);
            if (!info.Exists) return null;

            if (_inspectionCache.TryGetValue(zipPath, out var cached))
            {
                if (cached.lastWrite == info.LastWriteTimeUtc) return cached.pack;
            }

            var pack = TryLoadZipPack(zipPath, null, logDebug);
            if (pack != null) _inspectionCache[zipPath] = (info.LastWriteTimeUtc, pack);
            return pack;
        }

        public static LoadedPack LoadPackForGame(string targetGameName, Action<string> logDebug = null)
        {
            string packsDir = GetPacksDirectory();
            if (!Directory.Exists(packsDir)) return null;

            foreach (var file in Directory.GetFiles(packsDir, "*.zip"))
            {
                var pack = TryLoadZipPack(file, targetGameName, logDebug);
                if (pack != null)
                {
                    return pack; // Found it
                }
            }

            return null;
        }

        private static LoadedPack TryLoadZipPack(string zipPath, string targetGameName, Action<string> logDebug = null)
        {
            try
            {
                if (logDebug != null) logDebug($"[PopTracker] Checking zip: {System.IO.Path.GetFileName(zipPath)}");
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    // 1. Read manifest to check game name
                    var manifestEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith("pack.json", StringComparison.OrdinalIgnoreCase));

                    PopTrackerManifest manifest = null;
                    string rootPrefix = "";

                    if (manifestEntry != null)
                    {
                        string json = ReadStringFromEntry(manifestEntry);
                        try
                        {
                            manifest = JsonConvert.DeserializeObject<PopTrackerManifest>(json);
                        }
                        catch (Exception e)
                        {
                            if (logDebug != null) logDebug($"[PopTracker] Failed to parse manifest in {zipPath}: {e.Message}");
                        }

                        string manifestName = manifestEntry.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) ? "manifest.json" : "pack.json";
                        rootPrefix = manifestEntry.FullName.Substring(0, manifestEntry.FullName.Length - manifestName.Length);
                    }
                    else
                    {
                        if (logDebug != null) logDebug($"[PopTracker] No manifest.json or pack.json found. Attempting folder heuristic...");
                        var mapsFolder = archive.Entries.FirstOrDefault(e => e.FullName.IndexOf("/maps/", StringComparison.OrdinalIgnoreCase) >= 0 || e.FullName.StartsWith("maps/", StringComparison.OrdinalIgnoreCase));
                        if (mapsFolder != null)
                        {
                            int idx = mapsFolder.FullName.IndexOf("maps/", StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0) rootPrefix = mapsFolder.FullName.Substring(0, idx);
                        }
                        else
                        {
                            if (logDebug != null) logDebug($"[PopTracker] Could not find any maps directory in {zipPath}. Skipping.");
                            return null;
                        }

                        manifest = new PopTrackerManifest { Name = targetGameName, GameName = targetGameName };
                    }

                    if (manifest == null)
                    {
                        if (logDebug != null) logDebug($"[PopTracker] Skipping {zipPath} - Invalid manifest.");
                        return null;
                    }

                    // If manifest doesn't match game name and we aren't faking it
                    if (!string.IsNullOrEmpty(targetGameName) && !IsGameNameMatch(manifest.GameName, targetGameName) && !IsGameNameMatch(manifest.Name, targetGameName))
                    {
                        if (logDebug != null) logDebug($"[PopTracker] Skipping {zipPath} - Game Name '{manifest.GameName}' or Tracker Name '{manifest.Name}' does not match '{targetGameName}'.");
                        return null; // Not the right game
                    }

                    if (logDebug != null) logDebug($"[PopTracker] Match found! Extracting pack '{manifest.Name}' for game '{targetGameName}' with root prefix '{rootPrefix}'...");
                    var pack = new LoadedPack { Manifest = manifest };

                    // 2. Read items
                    var itemEntries = archive.Entries.Where(e => e.FullName.StartsWith(rootPrefix + "items/", StringComparison.OrdinalIgnoreCase) && (e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase)));
                    foreach (var entry in itemEntries)
                    {
                        string json = ReadStringFromEntry(entry);
                        try
                        {
                            var itemsList = JsonConvert.DeserializeObject<List<PopTrackerItem>>(json);
                            if (itemsList != null)
                            {
                                foreach (var item in itemsList)
                                {
                                    var codes = item.GetCodes();
                                    foreach (var code in codes)
                                    {
                                        pack.ItemsByCode[code] = item;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (logDebug != null) logDebug($"[PopTracker] Error: Failed to parse items json {entry.FullName}: {ex.Message}");
                            GD.PrintErr($"Failed to parse items json {entry.FullName}: {ex.Message}");
                        }
                    }

                    // 3. Read layouts to extract grid rows
                    var layoutEntries = archive.Entries.Where(e => e.FullName.StartsWith(rootPrefix + "layouts/", StringComparison.OrdinalIgnoreCase) && (e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase))).ToList();

                    layoutEntries.Sort((a, b) =>
                    {
                        bool aItems = a.FullName.EndsWith("items.json", StringComparison.OrdinalIgnoreCase);
                        bool bItems = b.FullName.EndsWith("items.json", StringComparison.OrdinalIgnoreCase);
                        bool aTracker = a.FullName.EndsWith("tracker.json", StringComparison.OrdinalIgnoreCase);
                        bool bTracker = b.FullName.EndsWith("tracker.json", StringComparison.OrdinalIgnoreCase);

                        if (aItems && !bItems) return -1;
                        if (!aItems && bItems) return 1;
                        if (aTracker && !bTracker) return -1;
                        if (!aTracker && bTracker) return 1;
                        return a.FullName.CompareTo(b.FullName);
                    });

                    foreach (var entry in layoutEntries)
                    {
                        string json = ReadStringFromEntry(entry);
                        try
                        {
                            var jToken = JToken.Parse(json);
                            int gridCount = pack.ItemGrids.Count;
                            ExtractItemGrids(jToken, pack.ItemGrids);

                            // If we found grids in this layout, stop reading other files to prevent duplicate alternate layouts
                            if (pack.ItemGrids.Count > gridCount)
                            {
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            if (logDebug != null) logDebug($"[PopTracker] Error: Failed to parse layout json {entry.FullName}: {ex.Message}");
                            GD.PrintErr($"Failed to parse layout json {entry.FullName}: {ex.Message}");
                        }
                    }

                    // 4. Load Images
                    var imageEntries = archive.Entries.Where(e => e.FullName.StartsWith(rootPrefix + "images/", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith("/"));
                    foreach (var entry in imageEntries)
                    {
                        string localPath = entry.FullName.Substring(rootPrefix.Length).TrimStart('/');
                        // e.g. "images/items/Annex Key.png"

                        // Godot image loading
                        byte[] buffer;
                        using (var stream = entry.Open())
                        using (var ms = new MemoryStream())
                        {
                            stream.CopyTo(ms);
                            buffer = ms.ToArray();
                        }

                        Image img = new Image();
                        Error err = Error.Failed;
                        if (localPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                            err = img.LoadPngFromBuffer(buffer);
                        else if (localPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                            err = img.LoadWebpFromBuffer(buffer);
                        else if (localPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || localPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                            err = img.LoadJpgFromBuffer(buffer);

                        if (err == Error.Ok)
                        {
                            var texture = ImageTexture.CreateFromImage(img);
                            pack.Images["/" + localPath] = texture; // Match the /images/... pathing from items.json
                            pack.Images[localPath] = texture; // And without slash just in case
                        }
                    }

                    // 5. Read maps
                    var mapEntries = archive.Entries.Where(e => e.FullName.StartsWith(rootPrefix + "maps/", StringComparison.OrdinalIgnoreCase) && (e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase)));
                    foreach (var entry in mapEntries)
                    {
                        string json = ReadStringFromEntry(entry);
                        try
                        {
                            var settings = new JsonLoadSettings { CommentHandling = CommentHandling.Ignore };
                            var token = Newtonsoft.Json.Linq.JToken.Parse(json, settings);
                            if (token is Newtonsoft.Json.Linq.JArray arr)
                            {
                                int idx = 0;
                                foreach (var item in arr)
                                {
                                    var map = item.ToObject<PopTrackerMap>();
                                    if (map != null)
                                    {
                                        map.Id = !string.IsNullOrEmpty(map.Name) ? map.Name : System.IO.Path.GetFileNameWithoutExtension(entry.FullName) + "_" + idx;
                                        if (string.IsNullOrEmpty(map.MapBg) && !string.IsNullOrEmpty(map.Img)) map.MapBg = map.Img;
                                        pack.Maps[map.Id] = map;
                                    }
                                    idx++;
                                }
                            }
                            else if (token is Newtonsoft.Json.Linq.JObject obj)
                            {
                                if (obj.ContainsKey("name") || obj.ContainsKey("map_bg") || obj.ContainsKey("img"))
                                {
                                    var singleMap = obj.ToObject<PopTrackerMap>();
                                    if (singleMap != null)
                                    {
                                        singleMap.Id = !string.IsNullOrEmpty(singleMap.Name) ? singleMap.Name : System.IO.Path.GetFileNameWithoutExtension(entry.FullName);
                                        if (string.IsNullOrEmpty(singleMap.MapBg) && !string.IsNullOrEmpty(singleMap.Img)) singleMap.MapBg = singleMap.Img;
                                        pack.Maps[singleMap.Id] = singleMap;
                                    }
                                }
                                else
                                {
                                    foreach (var kvp in obj)
                                    {
                                        if (kvp.Value is Newtonsoft.Json.Linq.JObject mapObj)
                                        {
                                            var map = mapObj.ToObject<PopTrackerMap>();
                                            if (map != null)
                                            {
                                                map.Id = kvp.Key;
                                                if (string.IsNullOrEmpty(map.MapBg) && !string.IsNullOrEmpty(map.Img)) map.MapBg = map.Img;
                                                pack.Maps[map.Id] = map;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (logDebug != null) logDebug($"[PopTracker] Error: Failed to parse map JSON {entry.FullName}: {ex.Message}");
                            GD.PrintErr($"Failed to parse map JSON {entry.FullName}: {ex.Message}");
                        }
                    }

                    // 6. Read locations
                    var locationEntries = archive.Entries.Where(e => e.FullName.StartsWith(rootPrefix + "locations/", StringComparison.OrdinalIgnoreCase) && (e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase)));
                    foreach (var entry in locationEntries)
                    {
                        string json = ReadStringFromEntry(entry);
                        try
                        {
                            var settings = new JsonLoadSettings { CommentHandling = CommentHandling.Ignore };
                            var token = Newtonsoft.Json.Linq.JToken.Parse(json, settings);
                            if (token is Newtonsoft.Json.Linq.JArray arr)
                            {
                                foreach (var item in arr)
                                {
                                    var loc = item.ToObject<PopTrackerLocation>();
                                    if (loc != null) ExtractLocationsRecursive(loc, pack.Locations);
                                }
                            }
                            else if (token is Newtonsoft.Json.Linq.JObject obj)
                            {
                                if (obj.ContainsKey("name") || obj.ContainsKey("x"))
                                {
                                    var singleLoc = obj.ToObject<PopTrackerLocation>();
                                    if (singleLoc != null) ExtractLocationsRecursive(singleLoc, pack.Locations);
                                }
                                else
                                {
                                    foreach (var kvp in obj)
                                    {
                                        if (kvp.Value is Newtonsoft.Json.Linq.JObject locObj)
                                        {
                                            var loc = locObj.ToObject<PopTrackerLocation>();
                                            if (loc != null)
                                            {
                                                if (string.IsNullOrEmpty(loc.Name)) loc.Name = kvp.Key;
                                                ExtractLocationsRecursive(loc, pack.Locations);
                                            }
                                        }
                                        else if (kvp.Value is Newtonsoft.Json.Linq.JArray locArr)
                                        {
                                            foreach (var item in locArr)
                                            {
                                                var loc = item.ToObject<PopTrackerLocation>();
                                                if (loc != null)
                                                {
                                                    if (string.IsNullOrEmpty(loc.Name)) loc.Name = kvp.Key;
                                                    ExtractLocationsRecursive(loc, pack.Locations);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (logDebug != null) logDebug($"[PopTracker] Error: Failed to parse location JSON {entry.FullName}: {ex.Message}");
                            GD.PrintErr($"Failed to parse location JSON {entry.FullName}: {ex.Message}");
                        }
                    }

                    if (logDebug != null) logDebug($"[PopTracker] Extraction Complete! Items: {pack.ItemsByCode.Count}, Layout Grids: {pack.ItemGrids.Count}, Maps: {pack.Maps.Count}, Locations: {pack.Locations.Count}");

                    // Link Textures
                    foreach (var map in pack.Maps.Values)
                    {
                        if (!string.IsNullOrEmpty(map.MapBg))
                        {
                            string key = "/" + map.MapBg.Replace("\\", "/");
                            if (pack.Images.ContainsKey(key))
                            {
                                map.BackgroundTexture = pack.Images[key];
                            }
                            else if (pack.Images.ContainsKey(map.MapBg))
                            {
                                map.BackgroundTexture = pack.Images[map.MapBg];
                            }
                        }
                    }

                    return pack;
                }
            }
            catch (Exception e)
            {
                GD.PrintErr($"Error loading zip pack {zipPath}: {e.Message}");
                return null;
            }
        }

        private static void ExtractLocationsRecursive(PopTrackerLocation loc, List<PopTrackerLocation> outList)
        {
            if (loc.Children != null && loc.Children.Count > 0)
            {
                foreach (var child in loc.Children)
                {
                    ExtractLocationsRecursive(child, outList);
                }
            }
            if (loc.MapLocations != null && loc.MapLocations.Count > 0)
            {
                outList.Add(loc);
            }
            else if (!string.IsNullOrEmpty(loc.MapRef) || loc.Sections.Count > 0)
            {
                outList.Add(loc);
            }
        }

        private static string ReadStringFromEntry(ZipArchiveEntry entry)
        {
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        private static void ExtractItemGrids(JToken token, List<List<string>> outputGrids)
        {
            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                if (obj["type"]?.ToString() == "itemgrid" && obj["rows"] is JArray rowsArray)
                {
                    foreach (var rowToken in rowsArray)
                    {
                        if (rowToken is JArray colArray)
                        {
                            var rowList = colArray.Select(c => c.ToString()).ToList();
                            if (rowList.Count > 0)
                            {
                                outputGrids.Add(rowList);
                            }
                        }
                    }
                }
                else
                {
                    foreach (var property in obj.Properties())
                    {
                        ExtractItemGrids(property.Value, outputGrids);
                    }
                }
            }
            else if (token.Type == JTokenType.Array)
            {
                foreach (var child in token)
                {
                    ExtractItemGrids(child, outputGrids);
                }
            }
        }

        public static bool IsGameNameMatch(string manifestName, string targetName)
        {
            if (string.IsNullOrEmpty(manifestName) || string.IsNullOrEmpty(targetName)) return false;
            if (string.Equals(manifestName, targetName, StringComparison.OrdinalIgnoreCase)) return true;

            string m = NormalizeName(manifestName);
            string t = NormalizeName(targetName);

            if (string.Equals(m, t, StringComparison.OrdinalIgnoreCase)) return true;

            if (m.Contains(":") && m.Substring(m.IndexOf(":") + 1).Trim().Equals(t, StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Contains(":") && t.Substring(t.IndexOf(":") + 1).Trim().Equals(m, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static string NormalizeName(string name)
        {
            string roman = name;
            roman = Regex.Replace(roman, @"\bVIII\b", "8", RegexOptions.IgnoreCase);
            roman = Regex.Replace(roman, @"\bVII\b", "7", RegexOptions.IgnoreCase);
            roman = Regex.Replace(roman, @"\bVI\b", "6", RegexOptions.IgnoreCase);
            roman = Regex.Replace(roman, @"\bIV\b", "4", RegexOptions.IgnoreCase);
            roman = Regex.Replace(roman, @"\bV\b", "5", RegexOptions.IgnoreCase);
            roman = Regex.Replace(roman, @"\bIII\b", "3", RegexOptions.IgnoreCase);
            roman = Regex.Replace(roman, @"\bII\b", "2", RegexOptions.IgnoreCase);
            return roman;
        }
    }
}
