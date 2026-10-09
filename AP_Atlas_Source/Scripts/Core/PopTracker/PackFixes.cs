#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Newtonsoft.Json;

namespace AP_Atlas.Core.PopTracker
{
    // =====================================================================
    // Fix file model. Each fix names its subject and records a stamp of the author's version of that subject
    // when the fix was made; if a pack update changes the author's version, the author wins and the fix is
    // set aside (restorable).
    // =====================================================================

    public abstract class PackFixBase
    {
        /// <summary>What the fix is about, e.g. "tile:abyss", "link:Firelink Shrine/Pin|Section", "pin:Area/Pin@FS".</summary>
        public string Subject { get; set; } = "";
        /// <summary>The author's version of the subject when the fix was made (see PackFixes.AuthorStamp).</summary>
        public string AuthorStamp { get; set; } = "";
        public DateTime Made { get; set; } = DateTime.Now;
        /// <summary>"automatic" when the Doctor made the fix itself (an exact name match); null for the user's own.</summary>
        public string Source { get; set; }

        [JsonIgnore]
        public bool Automatic => Source is "automatic" or "suggested";
    }

    /// <summary>A Key Items tile: which AP item it tracks, hidden, or a replacement image.</summary>
    public class TileFix : PackFixBase
    {
        public string Code { get; set; } = "";
        public long? ApItemId { get; set; }
        public string ApItemName { get; set; }
        public bool Hidden { get; set; }
        /// <summary>Image file under the pack's fix folder.</summary>
        public string ImageFile { get; set; }
    }

    /// <summary>A tile the user added for an AP item the pack's grid doesn't show.</summary>
    public class AddedTile : PackFixBase
    {
        public long ApItemId { get; set; }
        public string ApItemName { get; set; } = "";
        public string ImageFile { get; set; }
        /// <summary>Index into the pack's visible grids where the tile goes (appended to its last row).</summary>
        public int GridIndex { get; set; }
        public string Code => "atlas_item_" + ApItemId;
    }

    /// <summary>A whole grid: shown/hidden (overrides the settings guess), tile size, or reordered rows.</summary>
    public class GridFix : PackFixBase
    {
        public bool? Hidden { get; set; }
        public int? ItemSize { get; set; }
        public List<List<string>> Rows { get; set; }
    }

    /// <summary>Which AP location a pin section shows (null = shows none).</summary>
    public class LocationLinkFix : PackFixBase
    {
        public string PinPath { get; set; } = "";
        /// <summary>Section name, or "" for a pin with no sections.</summary>
        public string SectionName { get; set; } = "";
        public long? ApLocationId { get; set; }
        public string ApLocationName { get; set; }
    }

    /// <summary>A pin moved on one map, or removed.</summary>
    public class PinFix : PackFixBase
    {
        public string PinPath { get; set; } = "";
        public string MapId { get; set; } = "";
        public float? X { get; set; }
        public float? Y { get; set; }
        public bool Removed { get; set; }
    }

    /// <summary>A pin the user added (e.g. for an AP location the pack never placed).</summary>
    public class AddedPin : PackFixBase
    {
        public string Name { get; set; } = "";
        public string MapId { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public List<long> ApLocationIds { get; set; } = new List<long>();
        public string PinPath => "atlas/" + Name;
    }

    /// <summary>A map's background replaced with a local image.</summary>
    public class MapImageFix : PackFixBase
    {
        public string MapId { get; set; } = "";
        public string ImageFile { get; set; } = "";
    }

    public class PackFixFile
    {
        public string PackKey { get; set; } = "";
        public string PackName { get; set; } = "";
        /// <summary>Pack version the fixes were last checked against for author changes.</summary>
        public string PackVersion { get; set; } = "";

        /// <summary>Pack version the user last reviewed in the Doctor (no prompts until the pack changes).</summary>
        public string ReviewedVersion { get; set; } = "";

        /// <summary>Pack version whose exact name matches the Doctor linked by itself (once per version).</summary>
        public string AutoLinkedVersion { get; set; } = "";

        public List<TileFix> Tiles { get; set; } = new List<TileFix>();
        public List<AddedTile> AddedTiles { get; set; } = new List<AddedTile>();
        public List<GridFix> Grids { get; set; } = new List<GridFix>();
        public List<LocationLinkFix> Links { get; set; } = new List<LocationLinkFix>();
        public List<PinFix> Pins { get; set; } = new List<PinFix>();
        public List<AddedPin> AddedPins { get; set; } = new List<AddedPin>();
        public List<MapImageFix> MapImages { get; set; } = new List<MapImageFix>();

        /// <summary>Doctor findings the user chose to ignore (by finding key).</summary>
        public List<string> Ignored { get; set; } = new List<string>();

        /// <summary>Fixes set aside because the author changed the same thing in an update (restorable).</summary>
        public List<SupersededFix> Superseded { get; set; } = new List<SupersededFix>();

        [JsonIgnore]
        public int Count => Tiles.Count + AddedTiles.Count + Grids.Count + Links.Count + Pins.Count + AddedPins.Count + MapImages.Count;
    }

    public class SupersededFix
    {
        public string Kind { get; set; } = "";
        public string Subject { get; set; } = "";
        public string FixJson { get; set; } = "";
        public string PackVersion { get; set; } = "";
        public DateTime When { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// The user's local fixes to map packs, one file per pack in PortableData/pack_fixes. Fixes never touch the
    /// zip: <see cref="Effective"/> applies them to a light copy of the loaded pack (images shared), and
    /// <see cref="ApplyLinks"/> applies pairing fixes to a <see cref="PackIndex"/>. Main thread only.
    /// </summary>
    public static class PackFixes
    {
        /// <summary>Raised with the pack key after any change (fix, undo, reset).</summary>
        public static event Action<string> Changed;

        private static readonly Dictionary<string, PackFixFile> _files = new Dictionary<string, PackFixFile>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Stack<(string Json, string Description)>> _undo = new Dictionary<string, Stack<(string, string)>>(StringComparer.OrdinalIgnoreCase);

        public static string Dir => Path.Combine(DataManager.GetDataDirectory(), "pack_fixes");

        public static string KeyFor(LoadedPack pack)
        {
            string key = !string.IsNullOrWhiteSpace(pack?.Manifest?.PackageUid) ? pack.Manifest.PackageUid : Path.GetFileNameWithoutExtension(pack?.SourcePath ?? "pack");
            foreach (char c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
            return key;
        }

        public static string AssetDir(string packKey) => Path.Combine(Dir, packKey);

        public static PackFixFile Get(string packKey)
        {
            if (_files.TryGetValue(packKey, out var file)) return file;
            string path = Path.Combine(Dir, packKey + ".json");
            // A damaged file is kept aside and its last good copy restored (see SafeFile).
            file = SafeFile.ReadJson<PackFixFile>(path, () => null) ?? new PackFixFile { PackKey = packKey };
            _files[packKey] = file;
            return file;
        }

        private static void Save(PackFixFile file)
        {
            try
            {
                SafeFile.WriteJson(Path.Combine(Dir, file.PackKey + ".json"), file);
            }
            catch (Exception ex)
            {
                // Pack Doctor fixes are the user's own work: they hear about it.
                DataManager.ReportSaveFailure($"the Pack Doctor fixes for {file.PackKey}", ex);
            }
        }

        /// <summary>Changes a pack's fixes as one undoable step.</summary>
        public static void Edit(string packKey, string description, Action<PackFixFile> change)
        {
            var file = Get(packKey);
            if (!_undo.TryGetValue(packKey, out var stack)) _undo[packKey] = stack = new Stack<(string, string)>();
            stack.Push((JsonConvert.SerializeObject(file), description));
            change(file);
            Save(file);
            Changed?.Invoke(packKey);
        }

        /// <summary>Records that the user reviewed this pack version (not an undoable edit; no Changed event).</summary>
        public static void SetReviewedVersion(string packKey, string version)
        {
            var file = Get(packKey);
            file.ReviewedVersion = version;
            if (string.IsNullOrEmpty(file.PackVersion)) file.PackVersion = version;
            Save(file);
        }

        /// <summary>Records that the Doctor's automatic pass ran for this pack version (not an undoable edit; no Changed event).</summary>
        public static void SetAutoLinkedVersion(string packKey, string version)
        {
            var file = Get(packKey);
            file.AutoLinkedVersion = version;
            Save(file);
        }

        public static string UndoDescription(string packKey) =>
            _undo.TryGetValue(packKey, out var s) && s.Count > 0 ? s.Peek().Description : null;

        public static bool Undo(string packKey)
        {
            if (!_undo.TryGetValue(packKey, out var stack) || stack.Count == 0) return false;
            var (json, _) = stack.Pop();
            var restored = JsonConvert.DeserializeObject<PackFixFile>(json) ?? new PackFixFile { PackKey = packKey };
            _files[packKey] = restored;
            Save(restored);
            Changed?.Invoke(packKey);
            return true;
        }

        /// <summary>Removes fixes: one subject, a category ("tiles", "grids", "links", "pins", "maps"), or everything (null).</summary>
        public static void Reset(string packKey, string category = null, string subject = null)
        {
            Edit(packKey, subject != null ? $"Reset {subject}" : category != null ? $"Reset all {category}" : "Reset all fixes", f =>
            {
                bool Match(PackFixBase x) => subject == null || x.Subject == subject;
                if (category is null or "tiles") { f.Tiles.RemoveAll(Match); f.AddedTiles.RemoveAll(Match); }
                if (category is null or "grids") f.Grids.RemoveAll(Match);
                if (category is null or "links") f.Links.RemoveAll(Match);
                if (category is null or "pins") { f.Pins.RemoveAll(Match); f.AddedPins.RemoveAll(Match); }
                if (category is null or "maps") f.MapImages.RemoveAll(Match);
                if (category == null && subject == null) f.Ignored.Clear();
            });
        }

        /// <summary>
        /// Copies an image file into the pack's fix folder and returns its stored name. Refused (with why, for the user) if
        /// Atlas wouldn't show it: too big a file, not a PNG, JPEG or WebP image, or too many pixels.
        /// </summary>
        public static string ImportImage(string packKey, string sourcePath)
        {
            if (ImageFileRefusal(sourcePath) is string why) throw new InvalidDataException(why);
            string dir = AssetDir(packKey);
            Directory.CreateDirectory(dir);
            string name = Path.GetFileNameWithoutExtension(sourcePath) + "_" + DateTime.Now.ToString("yyyyMMddHHmmss") + Path.GetExtension(sourcePath).ToLowerInvariant();
            File.Copy(sourcePath, Path.Combine(dir, name), true);
            return name;
        }

        public static ImageTexture LoadImage(string packKey, string fileName)
        {
            try
            {
                string path = Path.Combine(AssetDir(packKey), fileName);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > SafeZip.ImageLimit) return null;
                using var img = PackImages.DecodeImage(File.ReadAllBytes(path), path, new ImageBudget(), out _);
                return img == null ? null : ImageTexture.CreateFromImage(img);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; } // gone, locked or misnamed: shown blank
        }

        /// <summary>Why Atlas wouldn't show an image file the user chose, or null. Checked as a pack's images are.</summary>
        private static string ImageFileRefusal(string path)
        {
            if (!PackImages.CanDecode(path)) return "Atlas shows PNG, JPEG and WebP images";
            var info = new FileInfo(path);
            if (info.Length > SafeZip.ImageLimit) return $"it's larger than {SafeZip.Size(SafeZip.ImageLimit)}, more than Atlas reads from one image";
            return new ImageBudget().Take(File.ReadAllBytes(path));
        }

        // =====================================================================
        // Author stamps: "author wins" on update
        // =====================================================================

        /// <summary>A compact signature of the author's version of a subject, to notice when an update changes it.</summary>
        public static string AuthorStamp(LoadedPack original, string subject)
        {
            int colon = subject.IndexOf(':');
            if (colon < 0) return "";
            string kind = subject.Substring(0, colon), what = subject.Substring(colon + 1);
            switch (kind)
            {
                case "tile":
                    return original.ItemsByCode.TryGetValue(what, out var item)
                        ? $"{item.Name}|{item.Img}|{string.Join(",", item.GetCodes())}|{string.Join(",", original.ItemMapping.Where(kv => kv.Value.Contains(what)).Select(kv => kv.Key))}"
                        : "(absent)";
                case "grid":
                    var grid = original.ItemGridGroups.FirstOrDefault(g => GridSubject(g) == subject);
                    return grid == null ? "(absent)" : $"{grid.ItemSize}|{string.Join(";", grid.Rows.Select(r => string.Join(",", r)))}";
                case "link":
                    {
                        var parts = what.Split('|');
                        string paths = string.Join(";", original.LocationMappingById.Where(kv => kv.Value.Any(p => p.TrimStart('@').StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)))
                            .OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + string.Join(",", kv.Value)));
                        var pin = original.Locations.FirstOrDefault(l => l.FullPath == parts[0]);
                        return pin == null ? "(absent)" : $"{string.Join(",", pin.Sections.Select(s => s.Name))}|{paths}";
                    }
                case "pin":
                    {
                        string path = what.Substring(0, Math.Max(0, what.LastIndexOf('@')));
                        var pin = original.Locations.FirstOrDefault(l => l.FullPath == path);
                        return pin == null ? "(absent)" : $"{pin.MapRef}:{pin.X},{pin.Y}|{string.Join(";", (pin.MapLocations ?? new List<PopTrackerMapLocation>()).Select(m => $"{m.Map}:{m.X},{m.Y}"))}";
                    }
                case "map":
                    {
                        if (!original.Maps.TryGetValue(what, out var map)) return "(absent)";
                        // "ok" when the author's background is in the pack and decodes, decoded now or not (a stamp
                        // mustn't depend on whether anything uses the pack). Known once its images were checked.
                        if (!original.ImagesChecked) PackImages.Check(original);
                        return $"{map.MapBg}|{(original.HasMapBackground(map) ? "ok" : "missing")}";
                    }
            }
            return "";
        }

        public static string GridSubject(PackItemGrid g) =>
            $"grid:{g.LayoutKey}:{g.Rows.SelectMany(r => r).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? ""}";

        /// <summary>
        /// After a pack update: sets aside every fix whose subject the author changed (author wins) and returns a
        /// sentence per fix for the user. Called when a pack's version differs from the one the fixes were made on.
        /// </summary>
        public static List<string> ResolveAuthorChanges(LoadedPack original)
        {
            string key = KeyFor(original);
            var file = Get(key);
            var notes = new List<string>();
            string version = original.Manifest?.GetActualVersion() ?? "";
            if (file.Count == 0 || file.PackVersion == version) return notes;

            void Check<T>(List<T> list, string kind, Func<T, string> describe) where T : PackFixBase
            {
                foreach (var fix in list.ToList())
                {
                    if (string.IsNullOrEmpty(fix.AuthorStamp) || AuthorStamp(original, fix.Subject) == fix.AuthorStamp) continue;
                    list.Remove(fix);
                    file.Superseded.Add(new SupersededFix { Kind = kind, Subject = fix.Subject, FixJson = JsonConvert.SerializeObject(fix), PackVersion = version });
                    notes.Add(describe(fix));
                }
            }
            Check(file.Tiles, "tile", f => $"Your fix to the '{f.Code}' tile was set aside: the author changed that item in {version}.");
            Check(file.Grids, "grid", f => $"Your change to a Key Items grid was set aside: the author changed that grid in {version}.");
            Check(file.Links, "link", f => $"Your link for '{f.PinPath} / {f.SectionName}' was set aside: the author changed that pin's mapping in {version}.");
            Check(file.Pins, "pin", f => $"Your move/removal of pin '{f.PinPath}' was set aside: the author changed that pin in {version}.");
            Check(file.MapImages, "map", f => $"Your image for map '{f.MapId}' was set aside: the author changed that map in {version}.");
            file.PackVersion = version;
            Save(file);
            if (notes.Count > 0) Changed?.Invoke(key);
            return notes;
        }

        /// <summary>Puts a set-aside fix back.</summary>
        public static void Restore(string packKey, SupersededFix s, LoadedPack original)
        {
            Edit(packKey, "Restore " + s.Subject, f =>
            {
                f.Superseded.Remove(f.Superseded.FirstOrDefault(x => x.Subject == s.Subject && x.When == s.When));
                PackFixBase fix = s.Kind switch
                {
                    "tile" => JsonConvert.DeserializeObject<TileFix>(s.FixJson),
                    "grid" => JsonConvert.DeserializeObject<GridFix>(s.FixJson),
                    "link" => JsonConvert.DeserializeObject<LocationLinkFix>(s.FixJson),
                    "pin" => JsonConvert.DeserializeObject<PinFix>(s.FixJson),
                    "map" => JsonConvert.DeserializeObject<MapImageFix>(s.FixJson),
                    _ => null
                };
                if (fix == null) return;
                fix.AuthorStamp = AuthorStamp(original, fix.Subject); // now made against the new version
                switch (fix)
                {
                    case TileFix t: f.Tiles.RemoveAll(x => x.Subject == t.Subject); f.Tiles.Add(t); break;
                    case GridFix g: f.Grids.RemoveAll(x => x.Subject == g.Subject); f.Grids.Add(g); break;
                    case LocationLinkFix l: f.Links.RemoveAll(x => x.Subject == l.Subject); f.Links.Add(l); break;
                    case PinFix p: f.Pins.RemoveAll(x => x.Subject == p.Subject); f.Pins.Add(p); break;
                    case MapImageFix m: f.MapImages.RemoveAll(x => x.Subject == m.Subject); f.MapImages.Add(m); break;
                }
            });
        }

        // =====================================================================
        // Applying fixes
        // =====================================================================

        /// <summary>
        /// The pack as the user sees it: a light copy of the author's pack with tile, grid, pin and map fixes applied.
        /// Images and item definitions are shared; pins, grids and maps are copied so the original stays untouched.
        /// Returns the original when there are no fixes.
        /// </summary>
        public static LoadedPack Effective(LoadedPack original)
        {
            if (original == null) return null;
            string key = KeyFor(original);
            var f = Get(key);
            if (f.Count == 0) return original;

            var e = new LoadedPack
            {
                Manifest = original.Manifest,
                SourcePath = original.SourcePath,
                RootPrefix = original.RootPrefix,
                TabMaps = original.TabMaps,
                ItemsByCode = new Dictionary<string, PopTrackerItem>(original.ItemsByCode, StringComparer.OrdinalIgnoreCase),
                Images = new Dictionary<string, ImageTexture>(original.Images, StringComparer.OrdinalIgnoreCase),
                ImageEntries = original.ImageEntries,
                ImageSizes = original.ImageSizes,
                ImagesLoaded = original.ImagesLoaded,
                ImagesChecked = original.ImagesChecked,
                ItemMapping = original.ItemMapping,
                LocationMappingById = original.LocationMappingById,
                UnkeyedLocationPaths = original.UnkeyedLocationPaths,
                LoadIssues = original.LoadIssues,
                BrokenImages = original.BrokenImages,
                ItemGridGroups = original.ItemGridGroups.Select(g => new PackItemGrid
                {
                    Header = g.Header,
                    LayoutKey = g.LayoutKey,
                    ItemSize = g.ItemSize,
                    LooksLikeSettings = g.LooksLikeSettings,
                    Rows = g.Rows.Select(r => new List<string>(r)).ToList()
                }).ToList(),
                Variant = original.Variant,
                VariantKey = original.VariantKey,
                Maps = original.Maps.ToDictionary(kv => kv.Key, kv => new PopTrackerMap
                {
                    Name = kv.Value.Name,
                    MapBg = kv.Value.MapBg,
                    Img = kv.Value.Img,
                    LocationSize = kv.Value.LocationSize,
                    LocationBorderThickness = kv.Value.LocationBorderThickness,
                    LocationShape = kv.Value.LocationShape,
                    Id = kv.Value.Id,
                    BackgroundTexture = kv.Value.BackgroundTexture
                }, StringComparer.OrdinalIgnoreCase),
                Locations = original.Locations.Select(ClonePin).ToList()
            };

            // The root layouts share the merged list's grids: the copies made above stand in for the originals in each root.
            var copies = original.ItemGridGroups.Zip(e.ItemGridGroups, (o, c) => (Original: o, Copy: c)).ToDictionary(p => p.Original, p => p.Copy);
            foreach (var kv in original.LayoutGrids)
                e.LayoutGrids[kv.Key] = kv.Value.Select(g => copies.TryGetValue(g, out var copy) ? copy : g).ToList();

            // Grids: shown/hidden, size, reordered rows.
            foreach (var gf in f.Grids)
            {
                var grid = e.ItemGridGroups.FirstOrDefault(g => GridSubject(g) == gf.Subject);
                if (grid == null) continue;
                if (gf.Hidden != null) grid.LooksLikeSettings = gf.Hidden.Value;
                if (gf.ItemSize != null) grid.ItemSize = gf.ItemSize.Value;
                if (gf.Rows != null) grid.Rows = gf.Rows.Select(r => new List<string>(r)).ToList();
            }

            // Tiles: a link for a code no pack item defines adds the item (so the cell shows a tile and the index can pair
            // it); hidden; replacement image.
            foreach (var tf in f.Tiles)
            {
                if (tf.ApItemId != null && !tf.Hidden && !e.ItemsByCode.ContainsKey(tf.Code))
                    e.ItemsByCode[tf.Code] = new PopTrackerItem { Name = string.IsNullOrEmpty(tf.ApItemName) ? tf.Code : tf.ApItemName, Type = "toggle", Img = "", CodesRaw = tf.Code };
                if (tf.Hidden)
                {
                    foreach (var g in e.ItemGridGroups) foreach (var r in g.Rows) r.RemoveAll(c => string.Equals(c, tf.Code, StringComparison.OrdinalIgnoreCase));
                }
                if (!string.IsNullOrEmpty(tf.ImageFile) && e.ItemsByCode.TryGetValue(tf.Code, out var item))
                {
                    var tex = LoadImage(key, tf.ImageFile);
                    if (tex == null) continue;
                    string imgKey = "atlasfix/" + tf.ImageFile;
                    e.Images[imgKey] = tex;
                    e.ItemsByCode[tf.Code] = CloneItem(item, imgKey);
                }
            }

            // Added tiles.
            var visibleGrids = e.ItemGridGroups.Where(g => !g.LooksLikeSettings).ToList();
            foreach (var at in f.AddedTiles)
            {
                string imgKey = null;
                // An added tile's image can come from the tile itself or from a later "Replace image" on it.
                string imageFile = f.Tiles.FirstOrDefault(t => t.Code == at.Code && !string.IsNullOrEmpty(t.ImageFile))?.ImageFile ?? at.ImageFile;
                if (!string.IsNullOrEmpty(imageFile))
                {
                    var tex = LoadImage(key, imageFile);
                    if (tex != null) { imgKey = "atlasfix/" + imageFile; e.Images[imgKey] = tex; }
                }
                e.ItemsByCode[at.Code] = new PopTrackerItem { Name = at.ApItemName, Type = "toggle", Img = imgKey ?? "", CodesRaw = at.Code };
                PackItemGrid target = visibleGrids.Count == 0 ? null : visibleGrids[Math.Clamp(at.GridIndex, 0, visibleGrids.Count - 1)];
                if (target == null)
                {
                    target = new PackItemGrid { Header = "Added", LayoutKey = "atlas" };
                    e.ItemGridGroups.Add(target);
                    visibleGrids.Add(target);
                }
                if (target.Rows.Count == 0) target.Rows.Add(new List<string>());
                if (!target.Rows.Any(r => r.Contains(at.Code))) target.Rows[target.Rows.Count - 1].Add(at.Code);
            }

            // Pins: moved or removed.
            foreach (var pf in f.Pins)
            {
                var pin = e.Locations.FirstOrDefault(l => l.FullPath == pf.PinPath);
                if (pin == null) continue;
                if (pf.Removed) { e.Locations.Remove(pin); continue; }
                if (pf.X == null || pf.Y == null) continue;
                if (string.Equals(pin.MapRef, pf.MapId, StringComparison.OrdinalIgnoreCase)) { pin.X = pf.X.Value; pin.Y = pf.Y.Value; }
                foreach (var ml in pin.MapLocations ?? new List<PopTrackerMapLocation>())
                {
                    if (string.Equals(ml.Map, pf.MapId, StringComparison.OrdinalIgnoreCase)) { ml.X = pf.X.Value; ml.Y = pf.Y.Value; }
                }
            }

            // Added pins (one section per AP location; linked in ApplyLinks).
            foreach (var ap in f.AddedPins)
            {
                e.Locations.Add(new PopTrackerLocation
                {
                    Name = ap.Name,
                    FullPath = ap.PinPath,
                    MapLocations = new List<PopTrackerMapLocation> { new PopTrackerMapLocation { Map = ap.MapId, X = ap.X, Y = ap.Y } },
                    Sections = ap.ApLocationIds.Select(id => new PopTrackerSection { Name = "atlas:" + id }).ToList()
                });
            }

            // Map backgrounds.
            foreach (var mf in f.MapImages)
            {
                if (!e.Maps.TryGetValue(mf.MapId, out var map)) continue;
                var tex = LoadImage(key, mf.ImageFile);
                if (tex != null) map.BackgroundTexture = tex;
            }
            return e;
        }

        /// <summary>Applies the user's pairing fixes (sections ↔ AP locations, tiles ↔ AP items) to an index.</summary>
        public static void ApplyLinks(PackIndex index) => ApplyLinks(index, Get(KeyFor(index.Pack)));

        /// <summary>Applies the given fixes' pairings; with a copy of the fixes, this is safe on a worker thread.</summary>
        public static void ApplyLinks(PackIndex index, PackFixFile f)
        {
            var e = index.Pack;
            foreach (var lf in f.Links)
            {
                var pin = e.Locations.FirstOrDefault(l => l.FullPath == lf.PinPath);
                if (pin == null) continue;
                var section = ResolveSection(pin, lf.SectionName);
                // Clear what that section showed, then link the chosen location (if any).
                foreach (var id in index.IdsFor(pin).ToList())
                {
                    if (index.ByLocation.TryGetValue(id, out var matches) && matches.Any(m => m.Pin == pin && m.Section == section))
                    {
                        matches.RemoveAll(m => m.Pin == pin && m.Section == section);
                        if (!matches.Any(m => m.Pin == pin)) index.IdsFor(pin).Remove(id);
                        if (matches.Count == 0) index.ByLocation.Remove(id);
                    }
                }
                if (lf.ApLocationId != null) index.Link(lf.ApLocationId.Value, pin, section);
            }
            foreach (var ap in f.AddedPins)
            {
                var pin = e.Locations.FirstOrDefault(l => l.FullPath == ap.PinPath);
                if (pin == null) continue;
                foreach (var id in ap.ApLocationIds)
                {
                    var section = pin.Sections.FirstOrDefault(s => s.Name == "atlas:" + id);
                    index.Link(id, pin, section);
                }
            }
            foreach (var tf in f.Tiles.Where(t => t.ApItemId != null))
            {
                index.UnlinkItem(tf.Code);
                index.LinkItem(tf.Code, tf.ApItemId.Value);
            }
            foreach (var at in f.AddedTiles) index.LinkItem(at.Code, at.ApItemId);
        }

        /// <summary>
        /// The section a fix names. "" means a section with a blank name when the pin has one (packs often give a
        /// single-check pin one unnamed section), else the pin itself (null).
        /// </summary>
        public static PopTrackerSection ResolveSection(PopTrackerLocation pin, string sectionName)
        {
            if (pin?.Sections == null || pin.Sections.Count == 0) return null;
            string name = sectionName ?? "";
            return pin.Sections.FirstOrDefault(s => (s.Name ?? "") == name)
                   ?? (name.Length == 0 && pin.Sections.Count == 1 ? pin.Sections[0] : null);
        }

        private static PopTrackerLocation ClonePin(PopTrackerLocation p) => new PopTrackerLocation
        {
            Name = p.Name,
            X = p.X,
            Y = p.Y,
            MapRef = p.MapRef,
            MapLocations = p.MapLocations?.Select(m => new PopTrackerMapLocation { Map = m.Map, X = m.X, Y = m.Y, Size = m.Size, BorderThickness = m.BorderThickness, Shape = m.Shape }).ToList(),
            AccessRulesRaw = p.AccessRulesRaw,
            Sections = p.Sections,
            Children = new List<PopTrackerLocation>(),
            ItemCount = p.ItemCount,
            FullPath = p.FullPath
        };

        private static PopTrackerItem CloneItem(PopTrackerItem i, string img) => new PopTrackerItem
        {
            Name = i.Name,
            Type = i.Type,
            Img = img,
            CodesRaw = i.CodesRaw,
            Stages = new List<PopTrackerItemStage>(),
            MaxQuantity = i.MaxQuantity
        };
    }
}
