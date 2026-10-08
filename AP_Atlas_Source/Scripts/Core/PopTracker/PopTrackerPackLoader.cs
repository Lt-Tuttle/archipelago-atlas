#nullable disable
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
        public string SourcePath { get; set; } = "";

        /// <summary>Folder inside the zip that holds manifest.json ("" when it's at the root).</summary>
        public string RootPrefix { get; set; } = "";

        /// <summary>The variant this pack was read as (the manifest's default, or the one asked for).</summary>
        public string Variant { get; set; } = "";

        /// <summary>The variant asked for when the pack was read (null: the default), the cache's key besides the zip.</summary>
        public string VariantKey { get; set; }
        public Dictionary<string, PopTrackerItem> ItemsByCode { get; set; } = new Dictionary<string, PopTrackerItem>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every itemgrid in the pack's tracker layout, in display order, with its group header and size.</summary>
        public List<PackItemGrid> ItemGridGroups { get; set; } = new List<PackItemGrid>();

        /// <summary>Rows of the grids that hold items (settings grids left out).</summary>
        public List<List<string>> ItemGrids => ItemGridGroups.Where(g => !g.LooksLikeSettings).SelectMany(g => g.Rows).ToList();

        /// <summary>
        /// The pack's decoded images, by path ("/images/x.png" and "images/x.png"). Empty until something uses the pack
        /// (<see cref="PackImages.Use"/>), and emptied again once it's been unused a while: reading a pack never decodes them.
        /// </summary>
        public Dictionary<string, ImageTexture> Images { get; set; } = new Dictionary<string, ImageTexture>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The pack's images as its zip holds them: path (both forms, as <see cref="Images"/>) → the zip entry's name.</summary>
        public Dictionary<string, string> ImageEntries { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether <see cref="Images"/> holds the pack's images now (see <see cref="PackImages"/>).</summary>
        public bool ImagesLoaded { get; internal set; }

        /// <summary>
        /// Whether the images have been decoded (for use, or only checked), so <see cref="BrokenImages"/> and
        /// <see cref="ImageSizes"/> are complete.
        /// </summary>
        public bool ImagesChecked { get; internal set; }

        /// <summary>Each decodable image's size, by path (both forms, as <see cref="Images"/>), once <see cref="ImagesChecked"/>.</summary>
        public Dictionary<string, Vector2I> ImageSizes { get; set; } = new Dictionary<string, Vector2I>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Held while the pack's images are decoded, checked or freed.</summary>
        internal readonly object ImageLock = new object();

        public Dictionary<string, PopTrackerMap> Maps { get; set; } = new Dictionary<string, PopTrackerMap>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The pack's tabs that show maps: a tab's title → the maps it shows, in order, from every layout file (PopTracker's
        /// "tabbed" layouts). A script switches the window to a tab by its title (Tracker:UiHint "ActivateTab").
        /// </summary>
        public Dictionary<string, List<string>> TabMaps { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public List<PopTrackerLocation> Locations { get; set; } = new List<PopTrackerLocation>();

        /// <summary>From item_mapping.lua: Archipelago item id → pack item codes.</summary>
        public Dictionary<long, List<string>> ItemMapping { get; set; } = new Dictionary<long, List<string>>();

        /// <summary>From location_mapping.lua: Archipelago location id → "@Pin/Path/Section" strings.</summary>
        public Dictionary<long, List<string>> LocationMappingById { get; set; } = new Dictionary<long, List<string>>();

        /// <summary>From location_mapping.lua files that list paths without ids (matched to AP locations by name).</summary>
        public List<string> UnkeyedLocationPaths { get; set; } = new List<string>();

        /// <summary>Problems found while reading the pack, for the Pack Doctor.</summary>
        public List<string> LoadIssues { get; set; } = new List<string>();

        /// <summary>
        /// Images that are in the pack but couldn't be decoded. Known once the images were decoded or checked
        /// (<see cref="ImagesChecked"/>).
        /// </summary>
        public HashSet<string> BrokenImages { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Finds a decoded image by a path as items/maps write it ("images/x.png", "/images/x.png", or without extension).
        /// Null when the pack doesn't have it, or its images aren't decoded now (see <see cref="Images"/>).
        /// </summary>
        public ImageTexture FindImage(string path)
        {
            string key = FindKey(Images, path);
            var texture = key == null ? null : Images[key];
            // Freed with its pack while something still held this copy of the pack: no image, not an exception.
            return texture != null && GodotObject.IsInstanceValid(texture) ? texture : null;
        }

        /// <summary>
        /// Whether the pack has an image that decodes (matched as <see cref="FindImage"/> does), decoded now or not: one
        /// added by a fix, or one of the pack's own that isn't broken. Exact once <see cref="ImagesChecked"/>.
        /// </summary>
        public bool HasDecodableImage(string path)
        {
            if (FindKey(Images, path) != null) return true;
            string key = FindKey(ImageEntries, path);
            return key != null && !BrokenImages.Contains(key.TrimStart('/'));
        }

        /// <summary>A decodable image's size (matched as <see cref="FindImage"/> does), once <see cref="ImagesChecked"/>.</summary>
        public Vector2I? ImageSize(string path)
        {
            string key = FindKey(ImageSizes, path);
            return key == null ? null : ImageSizes[key];
        }

        /// <summary>
        /// The path a map's own background is under: the map's path exactly, with or without a leading slash, as maps are
        /// linked to their backgrounds. Null when the pack doesn't have it.
        /// </summary>
        public string MapBackgroundPath(PopTrackerMap map)
        {
            if (string.IsNullOrEmpty(map?.MapBg)) return null;
            string withSlash = "/" + map.MapBg.Replace("\\", "/");
            if (ImageEntries.ContainsKey(withSlash)) return withSlash;
            return ImageEntries.ContainsKey(map.MapBg) ? map.MapBg : null;
        }

        /// <summary>Whether a map's own background is in the pack and decodes, decoded now or not (exact once <see cref="ImagesChecked"/>).</summary>
        public bool HasMapBackground(PopTrackerMap map) => MapBackgroundPath(map) is { } path && !BrokenImages.Contains(path.TrimStart('/'));

        /// <summary>The key an image is under, matched as items and maps write paths ("images/x.png", "/images/x.png", or without extension).</summary>
        private static string FindKey<TValue>(Dictionary<string, TValue> byPath, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.Replace("\\", "/");
            foreach (string candidate in new[] { p, "/" + p.TrimStart('/'), p.TrimStart('/') })
                if (byPath.ContainsKey(candidate)) return candidate;
            string baseName = p.Contains('.') ? p.Substring(0, p.LastIndexOf('.')) : p;
            foreach (string key in byPath.Keys)
            {
                string keyBase = key.Contains('.') ? key.Substring(0, key.LastIndexOf('.')) : key;
                if (keyBase.TrimStart('/') == baseName.TrimStart('/')) return key;
            }
            return null;
        }
    }

    public static class PopTrackerPackLoader
    {
        // Fully parsed packs keyed by zip path. Read from both the main thread and slot loader threads, so guard with _cacheLock.
        private static readonly Dictionary<string, (DateTime lastWrite, LoadedPack pack)> _packCache = new Dictionary<string, (DateTime, LoadedPack)>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();

        // A pack read as one of its variants is cached beside the default (the zip path alone keys the default).
        private static string CacheKey(string zipPath, string variantKey) => string.IsNullOrEmpty(variantKey) ? zipPath : zipPath + "\n" + variantKey;

        private static LoadedPack GetCachedPack(string zipPath, DateTime lastWriteUtc, string variantKey = null)
        {
            lock (_cacheLock)
            {
                return _packCache.TryGetValue(CacheKey(zipPath, variantKey), out var cached) && cached.lastWrite == lastWriteUtc ? cached.pack : null;
            }
        }

        private static void CachePack(string zipPath, DateTime lastWriteUtc, LoadedPack pack)
        {
            lock (_cacheLock) _packCache[CacheKey(zipPath, pack.VariantKey)] = (lastWriteUtc, pack);
        }

        /// <summary>
        /// The installed packs changed (one installed, deleted or replaced): connected slots load a pack they now have,
        /// and let go of one that's gone. Raised on the main thread.
        /// </summary>
        public static event Action PacksChanged;

        /// <summary>Tells connected slots the installed packs changed (main thread).</summary>
        public static void NotifyPacksChanged() => PacksChanged?.Invoke();

        /// <summary>Whether a loaded pack is still the one in its zip (the file is there, unchanged since it was read).</summary>
        public static bool IsCurrent(LoadedPack pack)
        {
            if (pack == null || string.IsNullOrEmpty(pack.SourcePath)) return false;
            var info = new System.IO.FileInfo(pack.SourcePath);
            return info.Exists && ReferenceEquals(GetCachedPack(pack.SourcePath, info.LastWriteTimeUtc, pack.VariantKey), pack);
        }

        public static string GetPacksDirectory()
        {
            string dir = Path.Combine(DataManager.GetDataDirectory(), "packs");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        /// <summary>True when the zip at this path has already been parsed and is unchanged on disk.</summary>
        public static bool IsPackCached(string zipPath)
        {
            var info = new System.IO.FileInfo(zipPath);
            return info.Exists && GetCachedPack(zipPath, info.LastWriteTimeUtc) != null;
        }

        public static LoadedPack InspectZipPack(string zipPath, Action<string> logDebug = null)
        {
            var info = new System.IO.FileInfo(zipPath);
            if (!info.Exists) return null;

            var cached = GetCachedPack(zipPath, info.LastWriteTimeUtc);
            if (cached != null) return cached;

            var pack = TryLoadZipPack(zipPath, null, null, logDebug);
            if (pack != null) CachePack(zipPath, info.LastWriteTimeUtc, pack);
            return pack;
        }

        public static LoadedPack LoadPackForGame(string targetGameName, Action<string> logDebug = null) => LoadPackForGame(targetGameName, null, logDebug);

        /// <summary>
        /// The installed pack for a game, read as one of its variants (<paramref name="variant"/>; null, or a variant the
        /// manifest doesn't list, means the pack's default): the variant's own files take the place of the base ones.
        /// </summary>
        public static LoadedPack LoadPackForGame(string targetGameName, string variant, Action<string> logDebug)
        {
            string packsDir = GetPacksDirectory();
            if (!Directory.Exists(packsDir)) return null;

            foreach (var file in Directory.GetFiles(packsDir, "*.zip"))
            {
                var info = new System.IO.FileInfo(file);
                // The default read (cached per zip) says which game the pack is for; a variant is read only for the game's pack, and cached beside it.
                var cached = GetCachedPack(file, info.LastWriteTimeUtc);
                if (cached == null)
                {
                    cached = TryLoadZipPack(file, targetGameName, null, logDebug);
                    if (cached == null) continue;
                    CachePack(file, info.LastWriteTimeUtc, cached);
                }
                else if (!IsPackForGame(cached.Manifest, targetGameName)) continue;
                if (VariantOf(cached.Manifest, variant) == cached.Variant) return cached;
                var asVariant = GetCachedPack(file, info.LastWriteTimeUtc, variant);
                if (asVariant == null)
                {
                    asVariant = TryLoadZipPack(file, targetGameName, variant, logDebug);
                    if (asVariant != null) CachePack(file, info.LastWriteTimeUtc, asVariant);
                }
                return asVariant ?? cached;
            }

            return null;
        }

        /// <summary>The variant a pack is read as: the one asked for when the manifest lists it, else the pack's default.</summary>
        public static string VariantOf(PopTrackerManifest manifest, string requested) =>
            !string.IsNullOrEmpty(requested) && manifest?.Variants != null && manifest.Variants.ContainsKey(requested) ? requested : PackScriptHost.DefaultVariant(manifest);

        /// <summary>A pack's variants (id and display name), the default first; one entry for a pack without variants.</summary>
        public static List<(string Id, string Name)> VariantsOf(PopTrackerManifest manifest)
        {
            var list = new List<(string, string)>();
            string first = PackScriptHost.DefaultVariant(manifest);
            if (manifest?.Variants == null) return list;
            foreach (var prop in manifest.Variants.Properties().OrderBy(p => p.Name == first ? 0 : 1))
            {
                string name = (prop.Value as JObject)?["display_name"]?.ToString();
                list.Add((prop.Name, string.IsNullOrWhiteSpace(name) ? prop.Name : name));
            }
            return list;
        }

        private static bool IsPackForGame(PopTrackerManifest manifest, string targetGameName) =>
            manifest != null && (IsGameNameMatch(manifest.GameName, targetGameName) || IsGameNameMatch(manifest.Name, targetGameName));

        private static LoadedPack TryLoadZipPack(string zipPath, string targetGameName, string requestedVariant, Action<string> logDebug = null)
        {
            try
            {
                if (logDebug != null) logDebug($"[PopTracker] Checking zip: {System.IO.Path.GetFileName(zipPath)}");
                using (var archive = SafeZip.Open(zipPath))
                {
                    // 1. Read manifest to check game name
                    var manifestEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith("pack.json", StringComparison.OrdinalIgnoreCase));

                    PopTrackerManifest manifest = null;
                    string rootPrefix = "";

                    if (manifestEntry != null)
                    {
                        string json = archive.ReadText(manifestEntry);
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

                        // Without a manifest the zip's file name is the only hint about which game the pack is for.
                        string fileName = Path.GetFileNameWithoutExtension(zipPath);
                        manifest = new PopTrackerManifest { Name = fileName, GameName = fileName };
                    }

                    if (manifest == null)
                    {
                        if (logDebug != null) logDebug($"[PopTracker] Skipping {zipPath} - Invalid manifest.");
                        return null;
                    }

                    if (!string.IsNullOrEmpty(targetGameName) && !IsPackForGame(manifest, targetGameName))
                    {
                        if (logDebug != null) logDebug($"[PopTracker] Skipping {zipPath} - Game Name '{manifest.GameName}' or Tracker Name '{manifest.Name}' does not match '{targetGameName}'.");
                        return null; // Not the right game
                    }

                    if (logDebug != null) logDebug($"[PopTracker] Match found! Extracting pack '{manifest.Name}' for game '{targetGameName}' with root prefix '{rootPrefix}'...");
                    var pack = new LoadedPack { Manifest = manifest, SourcePath = zipPath, RootPrefix = rootPrefix };

                    // 2. Read items
                    // The variant's own files (the pack's default, or the one asked for) take the place of the base ones of the same name (as PopTracker reads them).
                    string variant = VariantOf(manifest, requestedVariant);
                    pack.Variant = variant;
                    pack.VariantKey = variant != PackScriptHost.DefaultVariant(manifest) ? requestedVariant : null;
                    var itemEntries = FolderEntries(archive, rootPrefix, variant, "items", variantFirst: true);
                    foreach (var entry in itemEntries)
                    {
                        var token = ParseJsonLenient(archive, entry, pack, logDebug);
                        if (token is not JArray itemsArray) continue;
                        foreach (var itemToken in itemsArray)
                        {
                            PopTrackerItem item;
                            try { item = itemToken.ToObject<PopTrackerItem>(); }
                            catch (Exception ex)
                            {
                                pack.LoadIssues.Add($"{entry.FullName}: skipped an item that couldn't be read ({ex.Message}).");
                                continue;
                            }
                            if (item == null) continue;
                            foreach (var code in item.GetCodes())
                            {
                                // A code shared by several items (e.g. "cinders") keeps the first; specific codes stay unique.
                                if (!pack.ItemsByCode.ContainsKey(code)) pack.ItemsByCode[code] = item;
                            }
                        }
                    }

                    // 3. Read layouts: follow the tracker layout through its groups to find the item grids.
                    ExtractLayoutGrids(archive, rootPrefix, pack, logDebug);

                    // 4. Index the images: they're decoded only while something uses the pack (PackImages), since a pack's
                    // images are most of it (three large packs measured 948 MB of textures).
                    var imageEntries = archive.Entries.Where(e => e.FullName.StartsWith(rootPrefix + "images/", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith("/"));
                    foreach (var entry in imageEntries)
                    {
                        string localPath = entry.FullName.Substring(rootPrefix.Length).TrimStart('/');
                        // e.g. "images/items/Annex Key.png"; other file types are left out on purpose.
                        if (!PackImages.CanDecode(localPath)) continue;
                        pack.ImageEntries["/" + localPath] = entry.FullName; // Match the /images/... pathing from items.json
                        pack.ImageEntries[localPath] = entry.FullName; // And without slash just in case
                    }

                    // 5. Read maps
                    var mapEntries = FolderEntries(archive, rootPrefix, variant, "maps", variantFirst: false);
                    foreach (var entry in mapEntries)
                    {
                        string json = ReadText(archive, entry, pack);
                        if (json == null) continue;
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
                    var locationEntries = FolderEntries(archive, rootPrefix, variant, "locations", variantFirst: false);
                    foreach (var entry in locationEntries)
                    {
                        string json = ReadText(archive, entry, pack);
                        if (json == null) continue;
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

                    // 7. Read the autotracking mapping scripts: the pack's own AP id ↔ item code / location path tables.
                    ReadMappingScripts(archive, rootPrefix, pack);
                    pack.LoadIssues = pack.LoadIssues.Distinct().ToList();

                    if (logDebug != null) logDebug($"[PopTracker] Extraction Complete! Items: {pack.ItemsByCode.Count}, Layout Grids: {pack.ItemGridGroups.Count}, Maps: {pack.Maps.Count}, Locations: {pack.Locations.Count}, Item mappings: {pack.ItemMapping.Count}, Location mappings: {pack.LocationMappingById.Count + pack.UnkeyedLocationPaths.Count}, Images: {pack.ImageEntries.Count / 2}");
                    return pack;
                }
            }
            catch (Exception e)
            {
                GD.PrintErr($"Error loading zip pack {zipPath}: {e.Message}");
                return null;
            }
        }

        private static void ExtractLocationsRecursive(PopTrackerLocation loc, List<PopTrackerLocation> outList, string parentPath = "")
        {
            loc.FullPath = string.IsNullOrEmpty(parentPath) ? loc.Name : parentPath + "/" + loc.Name;
            if (loc.Children != null && loc.Children.Count > 0)
            {
                foreach (var child in loc.Children)
                {
                    ExtractLocationsRecursive(child, outList, loc.FullPath);
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

        /// <summary>A text file from the pack's zip, or null when Atlas won't read it (too big, or damaged): the pack notes why.</summary>
        private static string ReadText(SafeZip zip, ZipArchiveEntry entry, LoadedPack pack)
        {
            try { return zip.ReadText(entry); }
            catch (InvalidDataException ex)
            {
                pack.LoadIssues.Add($"{entry.FullName}: wasn't read ({ex.Message}).");
                return null;
            }
        }

        /// <summary>
        /// A pack folder's JSON files: the base folder's, with the default variant's own files ("variant/items/x.json") in
        /// place of the base ones of the same name. Items keep the first definition of a code, so theirs come first.
        /// </summary>
        private static List<ZipArchiveEntry> FolderEntries(SafeZip archive, string rootPrefix, string variant, string folder, bool variantFirst)
        {
            string basePrefix = rootPrefix + folder + "/";
            var baseEntries = archive.Entries.Where(e => e.FullName.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase) && IsJsonFile(e.FullName))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).ToList();
            if (string.IsNullOrEmpty(variant)) return baseEntries;
            string variantPrefix = rootPrefix + variant + "/" + folder + "/";
            var variantEntries = archive.Entries.Where(e => e.FullName.StartsWith(variantPrefix, StringComparison.OrdinalIgnoreCase) && IsJsonFile(e.FullName))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).ToList();
            if (variantEntries.Count == 0) return baseEntries;
            var replaced = new HashSet<string>(variantEntries.Select(e => e.FullName.Substring(variantPrefix.Length)), StringComparer.OrdinalIgnoreCase);
            var kept = baseEntries.Where(e => !replaced.Contains(e.FullName.Substring(basePrefix.Length)));
            return variantFirst ? variantEntries.Concat(kept).ToList() : kept.Concat(variantEntries).ToList();
        }

        /// <summary>
        /// Every "tabbed" layout's tabs that show maps (through nested groups, docks and "layout" references), by title. A
        /// tab title used twice keeps its first maps.
        /// </summary>
        internal static void ExtractTabMaps(IReadOnlyDictionary<string, JToken> layouts, LoadedPack pack)
        {
            List<string> MapsIn(JToken token, HashSet<string> followed, int depth)
            {
                var maps = new List<string>();
                if (token == null || depth > 64) return maps;
                if (token is JArray array)
                {
                    foreach (var child in array) maps.AddRange(MapsIn(child, followed, depth + 1));
                }
                else if (token is JObject obj)
                {
                    string type = (string)obj["type"] ?? "";
                    if (type.Equals("map", StringComparison.OrdinalIgnoreCase) && obj["maps"] is JArray names)
                        maps.AddRange(names.Where(n => n.Type == JTokenType.String).Select(n => (string)n));
                    if (type.Equals("layout", StringComparison.OrdinalIgnoreCase) && (string)obj["key"] is { } key && followed.Add(key) && layouts.TryGetValue(key, out var referenced))
                        maps.AddRange(MapsIn(referenced, followed, depth + 1));
                    foreach (var prop in obj.Properties())
                        if (prop.Name is not "maps" and not "key") maps.AddRange(MapsIn(prop.Value, followed, depth + 1));
                }
                return maps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            void Walk(JToken token, int depth)
            {
                if (token == null || depth > 64) return;
                if (token is JArray array)
                {
                    foreach (var child in array) Walk(child, depth + 1);
                    return;
                }
                if (token is not JObject obj) return;
                if (((string)obj["type"] ?? "").Equals("tabbed", StringComparison.OrdinalIgnoreCase) && obj["tabs"] is JArray tabs)
                {
                    foreach (var tab in tabs.OfType<JObject>())
                    {
                        string title = ((string)tab["title"] ?? "").Trim();
                        if (title.Length == 0 || pack.TabMaps.ContainsKey(title)) continue;
                        var maps = MapsIn(tab["content"], new HashSet<string>(StringComparer.OrdinalIgnoreCase), depth + 1);
                        if (maps.Count > 0) pack.TabMaps[title] = maps;
                    }
                }
                foreach (var prop in obj.Properties()) Walk(prop.Value, depth + 1);
            }
            foreach (var layout in layouts.Values) Walk(layout, 0);
        }

        private static bool IsJsonFile(string name) =>
            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Parses pack JSON the way PopTracker tolerates it: comments allowed, and trailing commas repaired.
        /// Repairs and failures are recorded on the pack for the Doctor.
        /// </summary>
        private static JToken ParseJsonLenient(SafeZip zip, ZipArchiveEntry entry, LoadedPack pack, Action<string> logDebug)
        {
            string json = ReadText(zip, entry, pack);
            if (json == null) return null;
            var settings = new JsonLoadSettings { CommentHandling = CommentHandling.Ignore };
            try
            {
                return JToken.Parse(json, settings);
            }
            catch (Exception first)
            {
                string repaired = Regex.Replace(json, @",(\s*[\]}])", "$1");
                try
                {
                    var token = JToken.Parse(repaired, settings);
                    pack.LoadIssues.Add($"{entry.FullName}: repaired invalid JSON (trailing commas).");
                    return token;
                }
                catch (Exception)
                {
                    pack.LoadIssues.Add($"{entry.FullName}: couldn't be read ({first.Message}).");
                    logDebug?.Invoke($"[PopTracker] Error: Failed to parse {entry.FullName}: {first.Message}");
                    return null;
                }
            }
        }

        // Root layouts PopTracker shows, in the order it prefers them.
        private static readonly string[] RootLayoutKeys = { "tracker_default", "tracker_horizontal", "tracker_vertical", "tracker_broadcast" };

        /// <summary>
        /// Collects the pack's item grids by walking its tracker layout the way PopTracker renders it: through
        /// "layout" references (by key), "group" headers, docks, tabs and arrays. Each grid keeps its group header
        /// and tile size, and grids under a settings-like group are marked so Key Items can leave them out.
        /// </summary>
        private static void ExtractLayoutGrids(SafeZip archive, string rootPrefix, LoadedPack pack, Action<string> logDebug)
        {
            var layouts = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
            var layoutEntries = FolderEntries(archive, rootPrefix, string.IsNullOrEmpty(pack.Variant) ? PackScriptHost.DefaultVariant(pack.Manifest) : pack.Variant, "layouts", variantFirst: false);
            foreach (var entry in layoutEntries)
            {
                if (ParseJsonLenient(archive, entry, pack, logDebug) is JObject obj)
                {
                    foreach (var prop in obj.Properties()) layouts[prop.Name] = prop.Value;
                }
            }
            if (layouts.Count == 0) return;
            ExtractTabMaps(layouts, pack);

            var seen = new HashSet<string>();
            void AddGrid(PackItemGrid grid)
            {
                if (grid.Rows.Count == 0) return;
                string signature = string.Join(";", grid.Rows.Select(r => string.Join(",", r)));
                if (!seen.Add(signature)) return; // the same grid reached through another root layout
                pack.ItemGridGroups.Add(grid);
            }

            void Walk(JToken token, string header, string layoutKey, HashSet<string> visiting)
            {
                if (token is JArray arr)
                {
                    // Consecutive single "item" elements form a row, as PopTracker lays them out.
                    var pendingRow = new List<string>();
                    void FlushRow()
                    {
                        if (pendingRow.Count == 0) return;
                        AddGrid(new PackItemGrid { Header = header, LayoutKey = layoutKey, Rows = new List<List<string>> { new List<string>(pendingRow) } });
                        pendingRow.Clear();
                    }
                    foreach (var child in arr)
                    {
                        if (child is JObject co && co["type"]?.ToString() == "item" && co["item"] != null) { pendingRow.Add(co["item"].ToString()); continue; }
                        FlushRow();
                        Walk(child, header, layoutKey, visiting);
                    }
                    FlushRow();
                    return;
                }
                if (token is not JObject obj) return;
                string type = obj["type"]?.ToString() ?? "";
                switch (type)
                {
                    case "itemgrid":
                        var grid = new PackItemGrid { Header = header, LayoutKey = layoutKey };
                        if (obj["item_size"] != null && int.TryParse(obj["item_size"].ToString().Split(',')[0].Trim(), out int size))
                        {
                            // Some packs write tiny values ("4, 4") that can't be pixels; keep the default and report it.
                            if (size >= 12) grid.ItemSize = size;
                            else pack.LoadIssues.Add($"Layout '{layoutKey}': item_size \"{obj["item_size"]}\" is too small to be a pixel size; using the default.");
                        }
                        if (obj["rows"] is JArray rows)
                        {
                            foreach (var row in rows.OfType<JArray>())
                            {
                                var codes = row.Select(c => c.Type == JTokenType.Null ? "" : c.ToString()).ToList();
                                if (codes.Count > 0) grid.Rows.Add(codes);
                            }
                        }
                        AddGrid(grid);
                        return;
                    case "item":
                        if (obj["item"] != null) AddGrid(new PackItemGrid { Header = header, LayoutKey = layoutKey, Rows = { new List<string> { obj["item"].ToString() } } });
                        return;
                    case "layout":
                        string key = obj["key"]?.ToString();
                        if (key != null && layouts.TryGetValue(key, out var referenced) && visiting.Add(key))
                        {
                            Walk(referenced, header, key, visiting);
                            visiting.Remove(key);
                        }
                        return;
                    case "group":
                        string groupHeader = obj["header"]?.ToString();
                        if (obj["content"] != null) Walk(obj["content"], string.IsNullOrEmpty(groupHeader) ? header : groupHeader, layoutKey, visiting);
                        return;
                    case "map":
                        return;
                }
                if (obj["content"] != null) Walk(obj["content"], header, layoutKey, visiting);
                if (obj["tabs"] is JArray tabs)
                {
                    foreach (var tab in tabs.OfType<JObject>())
                    {
                        string title = tab["title"]?.ToString();
                        if (tab["content"] != null) Walk(tab["content"], string.IsNullOrEmpty(title) ? header : title, layoutKey, visiting);
                    }
                }
            }

            var roots = RootLayoutKeys.Where(layouts.ContainsKey).ToList();
            if (roots.Count == 0) roots = layouts.Keys.Where(k => k.StartsWith("tracker", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var root in roots) Walk(layouts[root], "", root, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root });

            // No tracker layout: fall back to every layout key, in file order.
            if (pack.ItemGridGroups.Count == 0)
            {
                foreach (var kv in layouts) Walk(kv.Value, "", kv.Key, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { kv.Key });
            }

            foreach (var grid in pack.ItemGridGroups) grid.LooksLikeSettings = LooksLikeSettingsGrid(grid, pack);
            // Show item grids before settings grids, keeping the pack's order otherwise.
            pack.ItemGridGroups = pack.ItemGridGroups.OrderBy(g => g.LooksLikeSettings).ToList();
        }

        private static readonly string[] SettingsHeaderWords = { "setting", "option", "config", "mode", "toggle" };
        private static readonly string[] SettingsNamePrefixes = { "Shuffle ", "Setting", "Option", "Enable ", "Randomize ", "Goal" };

        /// <summary>Tracker option grids (e.g. "Shuffle Weapons") rather than Archipelago items.</summary>
        private static bool LooksLikeSettingsGrid(PackItemGrid grid, LoadedPack pack)
        {
            string header = grid.Header ?? "";
            if (SettingsHeaderWords.Any(w => header.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            var names = grid.Rows.SelectMany(r => r)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => pack.ItemsByCode.TryGetValue(c, out var it) ? it.Name ?? "" : "")
                .ToList();
            return names.Count > 0 && names.All(n => SettingsNamePrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        }

        // Item types in item_mapping.lua entries ({"code", "toggle"}); everything else is a code.
        private static readonly HashSet<string> MappingTypeWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "toggle", "progressive", "consumable", "progressive_toggle", "static", "toggle_badged", "composite_toggle", "progressive_toggle_plus"
        };

        /// <summary>Reads item_mapping.lua and location_mapping.lua, wherever the pack keeps them.</summary>
        private static void ReadMappingScripts(SafeZip archive, string rootPrefix, LoadedPack pack)
        {
            var itemScript = archive.Entries.FirstOrDefault(e => e.FullName.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                                                                 e.FullName.EndsWith("item_mapping.lua", StringComparison.OrdinalIgnoreCase));
            if (itemScript != null && ReadText(archive, itemScript, pack) is string itemCode)
            {
                var result = LuaMappingReader.Read(itemCode);
                foreach (var problem in result.Problems) pack.LoadIssues.Add($"{itemScript.FullName}: {problem}");
                foreach (var entry in result.Entries.Where(e => e.Id != null))
                {
                    var codes = entry.Strings.Where(s => !MappingTypeWords.Contains(s) && s.Length > 0).ToList();
                    if (codes.Count == 0) continue;
                    if (!pack.ItemMapping.TryGetValue(entry.Id.Value, out var list)) pack.ItemMapping[entry.Id.Value] = list = new List<string>();
                    list.AddRange(codes.Where(c => !list.Contains(c)));
                }
                if (result.Entries.Count > 0 && pack.ItemMapping.Count == 0)
                    pack.LoadIssues.Add($"{itemScript.FullName}: no entries keyed by item id were found.");
            }

            var locationScript = archive.Entries.FirstOrDefault(e => e.FullName.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                                                                     e.FullName.EndsWith("location_mapping.lua", StringComparison.OrdinalIgnoreCase));
            if (locationScript != null && ReadText(archive, locationScript, pack) is string locationCode)
            {
                var result = LuaMappingReader.Read(locationCode);
                foreach (var problem in result.Problems) pack.LoadIssues.Add($"{locationScript.FullName}: {problem}");
                foreach (var entry in result.Entries)
                {
                    var paths = entry.Strings.Where(s => s.StartsWith("@")).ToList();
                    if (paths.Count == 0) continue;
                    if (entry.Id != null)
                    {
                        if (!pack.LocationMappingById.TryGetValue(entry.Id.Value, out var list)) pack.LocationMappingById[entry.Id.Value] = list = new List<string>();
                        list.AddRange(paths);
                    }
                    else pack.UnkeyedLocationPaths.AddRange(paths);
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
