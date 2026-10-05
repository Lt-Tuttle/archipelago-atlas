#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Newtonsoft.Json;

namespace AP_Atlas.Core
{
    /// <summary>A per-slot flag (0 = none, 1..5 = flag color) and free-text note on a location or item.</summary>
    public class Annotation
    {
        public int Flag { get; set; }
        public string Note { get; set; } = "";
        public DateTime Updated { get; set; } = DateTime.Now;

        [JsonIgnore] public bool IsEmpty => Flag == 0 && string.IsNullOrWhiteSpace(Note);
    }

    /// <summary>A per-game "special" mark on an item or location name (e.g. required to goal), with an optional note.</summary>
    public class SpecialEntry
    {
        public string Note { get; set; } = "";
    }

    public class AnnotationData
    {
        /// <summary>"profileId|slotName" → entity key ("L:{locationId}" / "I:{itemId}") → annotation.</summary>
        public Dictionary<string, Dictionary<string, Annotation>> Slots { get; set; } = new();

        /// <summary>Game name → entity key ("L:{locationName}" / "I:{itemName}") → special entry. Names, so lists carry across seeds.</summary>
        public Dictionary<string, Dictionary<string, SpecialEntry>> Games { get; set; } = new();

        public List<string> FlagLabels { get; set; } = new List<string>(Annotations.DefaultFlagLabels);

        /// <summary>"profileId|slotName" → location id → true (you excluded it) / false (you included it despite the seed).</summary>
        public Dictionary<string, Dictionary<long, bool>> ExclusionOverrides { get; set; } = new();

        /// <summary>"profileId|slotName" → signature of the YAML exclusion list last offered, so it's offered once.</summary>
        public Dictionary<string, string> YamlExclusionsOffered { get; set; } = new();
    }

    /// <summary>The file format for sharing one game's special list.</summary>
    public class SpecialListFile
    {
        public string Format { get; set; } = "ap-atlas-special/1";
        public string Game { get; set; } = "";
        public List<SpecialListEntry> Items { get; set; } = new();
        public List<SpecialListEntry> Locations { get; set; } = new();
    }

    public class SpecialListEntry
    {
        public string Name { get; set; } = "";
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// Notes, flags and special marks, saved in PortableData/annotations.json. All access is on the main thread.
    /// Flags and notes belong to one slot; special marks belong to a game and apply to every slot playing it.
    /// </summary>
    public static class Annotations
    {
        public static readonly string[] DefaultFlagLabels = { "Avoid", "Later", "Important", "Go", "Info" };
        public static readonly Color[] FlagColors =
        {
            new Color("#E5484D"), // red
            new Color("#F5A524"), // orange
            new Color("#FFD43B"), // gold
            new Color("#46C46A"), // green
            new Color("#3B9EFF"), // blue
        };
        public static readonly Color SpecialColor = new Color("#FFC53D");
        public static readonly Color SpecialBg = new Color("#3A2F0B");

        /// <summary>Raised after any change (main thread).</summary>
        public static event Action Changed;

        private static AnnotationData _data;
        private static AnnotationData Data => _data ??= Load();

        public static string SlotKey(string profileId, string slotName) => profileId + "|" + slotName;
        public static string LocationKey(long locationId) => "L:" + locationId;
        public static string ItemKey(long itemId) => "I:" + itemId;
        public static string SpecialLocationKey(string name) => "L:" + name;
        public static string SpecialItemKey(string name) => "I:" + name;

        // =====================================================================
        // Flags and notes (per slot)
        // =====================================================================

        public static Annotation Get(string slotKey, string entityKey)
        {
            if (slotKey == null || entityKey == null) return null;
            return Data.Slots.TryGetValue(slotKey, out var map) && map.TryGetValue(entityKey, out var a) ? a : null;
        }

        public static int GetFlag(string slotKey, string entityKey) => Get(slotKey, entityKey)?.Flag ?? 0;

        public static void SetFlag(string slotKey, string entityKey, int flag)
        {
            Update(slotKey, entityKey, a => a.Flag = Math.Clamp(flag, 0, FlagColors.Length));
        }

        public static void SetNote(string slotKey, string entityKey, string note)
        {
            Update(slotKey, entityKey, a => a.Note = note?.Trim() ?? "");
        }

        /// <summary>All flagged/noted entity keys for a slot.</summary>
        public static IReadOnlyDictionary<string, Annotation> ForSlot(string slotKey) =>
            slotKey != null && Data.Slots.TryGetValue(slotKey, out var map) ? map : new Dictionary<string, Annotation>();

        private static void Update(string slotKey, string entityKey, Action<Annotation> change)
        {
            if (slotKey == null || entityKey == null) return;
            if (!Data.Slots.TryGetValue(slotKey, out var map)) Data.Slots[slotKey] = map = new Dictionary<string, Annotation>();
            if (!map.TryGetValue(entityKey, out var a)) map[entityKey] = a = new Annotation();
            change(a);
            a.Updated = DateTime.Now;
            if (a.IsEmpty) map.Remove(entityKey);
            if (map.Count == 0) Data.Slots.Remove(slotKey);
            SaveAndNotify();
        }

        // =====================================================================
        // Exclusion overrides (per slot)
        // =====================================================================

        /// <summary>Your choice for a location: true = excluded, false = included, null = use the seed's setting.</summary>
        public static bool? GetExclusionOverride(string slotKey, long locationId) =>
            slotKey != null && Data.ExclusionOverrides.TryGetValue(slotKey, out var map) && map.TryGetValue(locationId, out var v) ? v : null;

        public static void SetExclusionOverride(string slotKey, long locationId, bool? excluded)
        {
            if (slotKey == null) return;
            if (!Data.ExclusionOverrides.TryGetValue(slotKey, out var map)) Data.ExclusionOverrides[slotKey] = map = new Dictionary<long, bool>();
            if (excluded == null) map.Remove(locationId);
            else map[locationId] = excluded.Value;
            if (map.Count == 0) Data.ExclusionOverrides.Remove(slotKey);
            SaveAndNotify();
        }

        /// <summary>Sets several locations at once (one save, one Changed event).</summary>
        public static void SetExclusionOverrides(string slotKey, IEnumerable<long> locationIds, bool? excluded)
        {
            if (slotKey == null) return;
            if (!Data.ExclusionOverrides.TryGetValue(slotKey, out var map)) Data.ExclusionOverrides[slotKey] = map = new Dictionary<long, bool>();
            foreach (long id in locationIds)
            {
                if (excluded == null) map.Remove(id);
                else map[id] = excluded.Value;
            }
            if (map.Count == 0) Data.ExclusionOverrides.Remove(slotKey);
            SaveAndNotify();
        }

        public static void ClearExclusionOverrides(string slotKey)
        {
            if (slotKey == null || !Data.ExclusionOverrides.Remove(slotKey)) return;
            SaveAndNotify();
        }

        public static IReadOnlyDictionary<long, bool> ExclusionOverridesFor(string slotKey) =>
            slotKey != null && Data.ExclusionOverrides.TryGetValue(slotKey, out var map) ? map : new Dictionary<long, bool>();

        /// <summary>True the first time a slot sees this YAML exclusion list (and records it as seen).</summary>
        public static bool FirstOfferOfYamlExclusions(string slotKey, string signature)
        {
            if (slotKey == null) return false;
            if (Data.YamlExclusionsOffered.TryGetValue(slotKey, out var seen) && seen == signature) return false;
            Data.YamlExclusionsOffered[slotKey] = signature;
            Save();
            return true;
        }

        // =====================================================================
        // Special marks (per game, by name)
        // =====================================================================

        public static SpecialEntry GetSpecial(string game, string specialKey)
        {
            if (string.IsNullOrEmpty(game) || specialKey == null) return null;
            return Data.Games.TryGetValue(game, out var map) && map.TryGetValue(specialKey, out var e) ? e : null;
        }

        public static bool IsSpecialItem(string game, string itemName) =>
            itemName != null && GetSpecial(game, SpecialItemKey(itemName)) != null;

        public static bool IsSpecialLocation(string game, string locationName) =>
            locationName != null && GetSpecial(game, SpecialLocationKey(locationName)) != null;

        public static void SetSpecial(string game, string specialKey, bool special, string note = null)
        {
            if (string.IsNullOrEmpty(game) || specialKey == null) return;
            if (!Data.Games.TryGetValue(game, out var map)) Data.Games[game] = map = new Dictionary<string, SpecialEntry>();
            if (!special)
            {
                map.Remove(specialKey);
                if (map.Count == 0) Data.Games.Remove(game);
            }
            else
            {
                if (!map.TryGetValue(specialKey, out var e)) map[specialKey] = e = new SpecialEntry();
                if (note != null) e.Note = note.Trim();
            }
            SaveAndNotify();
        }

        public static IEnumerable<string> SpecialItemNames(string game) => SpecialNames(game, "I:");
        public static IEnumerable<string> SpecialLocationNames(string game) => SpecialNames(game, "L:");

        private static IEnumerable<string> SpecialNames(string game, string prefix)
        {
            if (string.IsNullOrEmpty(game) || !Data.Games.TryGetValue(game, out var map)) return Enumerable.Empty<string>();
            return map.Keys.Where(k => k.StartsWith(prefix)).Select(k => k.Substring(prefix.Length)).ToList();
        }

        public static IEnumerable<string> GamesWithSpecials() => Data.Games.Keys.ToList();

        /// <summary>Writes one game's special list (items, locations and notes) to a shareable JSON file.</summary>
        public static int ExportSpecials(string game, string path)
        {
            var file = new SpecialListFile { Game = game };
            if (Data.Games.TryGetValue(game, out var map))
            {
                foreach (var kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var entry = new SpecialListEntry { Name = kv.Key.Substring(2), Note = kv.Value.Note ?? "" };
                    if (kv.Key.StartsWith("I:")) file.Items.Add(entry);
                    else file.Locations.Add(entry);
                }
            }
            File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
            return file.Items.Count + file.Locations.Count;
        }

        /// <summary>Merges a shared special list into the game it names. Existing notes are kept unless the file has one.</summary>
        public static (string Game, int Added) ImportSpecials(string path)
        {
            var file = JsonConvert.DeserializeObject<SpecialListFile>(File.ReadAllText(path));
            if (file == null || string.IsNullOrWhiteSpace(file.Game)) throw new InvalidDataException("The file does not name a game.");
            if (!Data.Games.TryGetValue(file.Game, out var map)) Data.Games[file.Game] = map = new Dictionary<string, SpecialEntry>();
            int added = 0;
            void Merge(string key, SpecialListEntry e)
            {
                if (string.IsNullOrWhiteSpace(e?.Name)) return;
                if (!map.TryGetValue(key, out var existing)) { map[key] = existing = new SpecialEntry(); added++; }
                if (!string.IsNullOrWhiteSpace(e.Note)) existing.Note = e.Note.Trim();
            }
            foreach (var e in file.Items ?? new()) Merge(SpecialItemKey(e.Name), e);
            foreach (var e in file.Locations ?? new()) Merge(SpecialLocationKey(e.Name), e);
            SaveAndNotify();
            return (file.Game, added);
        }

        // =====================================================================
        // Flag labels
        // =====================================================================

        public static string FlagLabel(int flag)
        {
            if (flag < 1 || flag > FlagColors.Length) return "";
            var labels = Data.FlagLabels;
            return flag - 1 < labels.Count && !string.IsNullOrWhiteSpace(labels[flag - 1]) ? labels[flag - 1] : DefaultFlagLabels[flag - 1];
        }

        public static Color FlagColor(int flag) => flag >= 1 && flag <= FlagColors.Length ? FlagColors[flag - 1] : Colors.Transparent;

        public static void SetFlagLabel(int flag, string label)
        {
            if (flag < 1 || flag > FlagColors.Length) return;
            while (Data.FlagLabels.Count < FlagColors.Length) Data.FlagLabels.Add(DefaultFlagLabels[Data.FlagLabels.Count]);
            Data.FlagLabels[flag - 1] = string.IsNullOrWhiteSpace(label) ? DefaultFlagLabels[flag - 1] : label.Trim();
            SaveAndNotify();
        }

        // =====================================================================
        // Markers for tables (Tree cell icons)
        // =====================================================================

        private static readonly Dictionary<(int flag, bool special, bool note), ImageTexture> _iconCache = new();

        /// <summary>
        /// A small strip showing a flag dot, a special diamond and a note mark, in that order, for whichever apply.
        /// Null when none apply. Cached per combination.
        /// </summary>
        public static Texture2D MarkerIcon(int flag, bool special, bool note)
        {
            if (flag == 0 && !special && !note) return null;
            var key = (flag, special, note);
            if (_iconCache.TryGetValue(key, out var cached)) return cached;

            const int cell = 24;
            int marks = (flag > 0 ? 1 : 0) + (special ? 1 : 0) + (note ? 1 : 0);
            var img = Image.CreateEmpty(cell * marks, cell, false, Image.Format.Rgba8);
            img.Fill(Colors.Transparent);
            int x0 = 0;
            if (flag > 0) { DrawDot(img, x0, cell, FlagColor(flag)); x0 += cell; }
            if (special) { DrawDiamond(img, x0, cell, SpecialColor); x0 += cell; }
            if (note) DrawNote(img, x0, cell, new Color("#C8C8D0"));
            var tex = ImageTexture.CreateFromImage(img);
            _iconCache[key] = tex;
            return tex;
        }

        private static void DrawDot(Image img, int x0, int cell, Color c)
        {
            float r = cell * 0.32f, cx = x0 + cell / 2f - 0.5f, cy = cell / 2f - 0.5f;
            for (int x = x0; x < x0 + cell; x++)
                for (int y = 0; y < cell; y++)
                {
                    float d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    if (d <= r * r) img.SetPixel(x, y, c);
                    else if (d <= (r + 1.2f) * (r + 1.2f)) img.SetPixel(x, y, new Color(0, 0, 0, 0.8f));
                }
        }

        private static void DrawDiamond(Image img, int x0, int cell, Color c)
        {
            float r = cell * 0.40f, cx = x0 + cell / 2f - 0.5f, cy = cell / 2f - 0.5f;
            for (int x = x0; x < x0 + cell; x++)
                for (int y = 0; y < cell; y++)
                {
                    float d = Math.Abs(x - cx) + Math.Abs(y - cy);
                    if (d <= r) img.SetPixel(x, y, c);
                    else if (d <= r + 1.5f) img.SetPixel(x, y, new Color(0, 0, 0, 0.8f));
                }
        }

        private static void DrawNote(Image img, int x0, int cell, Color c)
        {
            // A small page with three text lines.
            int left = x0 + cell / 5, right = x0 + cell - cell / 5, top = cell / 6, bottom = cell - cell / 6;
            for (int x = left; x <= right; x++)
                for (int y = top; y <= bottom; y++)
                {
                    bool border = x == left || x == right || y == top || y == bottom;
                    bool line = (y - top) % 5 == 3 && x > left + 2 && x < right - 2;
                    if (border || line) img.SetPixel(x, y, c);
                    else img.SetPixel(x, y, new Color(0.1f, 0.1f, 0.12f, 0.9f));
                }
        }

        // =====================================================================
        // Persistence
        // =====================================================================

        private static string FilePath => Path.Combine(DataManager.GetDataDirectory(), "annotations.json");

        private static AnnotationData Load()
        {
            try
            {
                {
                    var data = SafeFile.ReadJson<AnnotationData>(FilePath, () => null);
                    if (data != null)
                    {
                        data.Slots ??= new();
                        data.Games ??= new();
                        data.ExclusionOverrides ??= new();
                        data.YamlExclusionsOffered ??= new();
                        data.FlagLabels ??= new List<string>(DefaultFlagLabels);
                        // Case-insensitive game names, so "Dark Souls III" and "Dark Souls Iii" from different sources match.
                        data.Games = new Dictionary<string, Dictionary<string, SpecialEntry>>(data.Games, StringComparer.OrdinalIgnoreCase);
                        return data;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Could not read annotations.json ({ex.Message}); starting with no notes or flags.");
            }
            return new AnnotationData
            {
                Games = new Dictionary<string, Dictionary<string, SpecialEntry>>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private static void SaveAndNotify()
        {
            Save();
            Changed?.Invoke();
        }

        private static void Save()
        {
            try
            {
                // Crash-safe: temp file, flushed, swapped in; the previous version kept as .bak.
                SafeFile.WriteJson(FilePath, Data);
            }
            catch (Exception ex)
            {
                // Notes, flags and exclusions are the user's own work: they hear about it.
                DataManager.ReportSaveFailure("annotations.json", ex);
            }
        }
    }
}
