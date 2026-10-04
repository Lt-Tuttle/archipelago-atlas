using System.IO;
using Godot;
using Newtonsoft.Json;
using System.Collections.Generic;

public class MapCameraSave
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Zoom { get; set; }
}

public class AppSettings
{
    public int SlotsFontSize { get; set; } = 14;
    public int ExplorerFontSize { get; set; } = 14;
    public int PropertiesFontSize { get; set; } = 14;
    public int ContentFontSize { get; set; } = 14;
    public int ConsoleFontSize { get; set; } = 14;
    public int GlobalFontSize { get; set; } = 14;
    public float ScaleSidebar { get; set; } = 1.0f;
    public float ScaleContent { get; set; } = 1.0f;
    public float ScaleConsole { get; set; } = 1.0f;
    public float ScaleStatusBar { get; set; } = 1.0f;
    public string ThemeAccentColor { get; set; } = "#8A2BE2";
    public string ArchipelagoInstallationPath { get; set; } = "";
    /// <summary>"Portable" (Atlas's own engine) or "Existing" (the install above). Empty until the user chooses.</summary>
    public string EngineMode { get; set; } = "";
    /// <summary>Slot key ("profileId|slotName") → the player YAML the user linked to that slot.</summary>
    public Dictionary<string, string> SlotYamlPaths { get; set; } = new Dictionary<string, string>();

    // Layout State
    public int MainSplitOffset { get; set; } = 300;
    public int SplitRightSidebarOffset { get; set; } = 0;
    public int SplitCenterRightOffset { get; set; } = 0;
    public int SplitContentOffset { get; set; } = 0;

    // Map Tracker State
    public int MapSplitOffset { get; set; } = 350;
    public int MapFontSize { get; set; } = 15;
    public float MapNodeScale { get; set; } = 1.0f;
    /// <summary>How the map shows checks your seed excludes: 0 show, 1 dim, 2 hide.</summary>
    public int MapExcludedMode { get; set; } = 1;
    /// <summary>How the map shows pins whose checks aren't in your seed: 0 show, 1 dim, 2 hide.</summary>
    public int MapNotInSeedMode { get; set; } = 1;
    public bool MapHideChecked { get; set; } = false;
    public int MapSortIndex { get; set; } = 0;
    public float KeyItemZoom { get; set; } = 1.0f;
    public Dictionary<string, MapCameraSave> MapCameras { get; set; } = new Dictionary<string, MapCameraSave>();

    // Properties panel: titles of sections the user collapsed, and the flag color used by the F shortcut.
    public List<string> CollapsedPropertySections { get; set; } = new List<string> { "Advanced" };
    public int LastFlagColor { get; set; } = 3;

    // Race mode: when restrictions apply, and whether they hide all logic or only the "why" explanations.
    public AP_Atlas.Core.RaceModeSetting RaceMode { get; set; } = AP_Atlas.Core.RaceModeSetting.FollowServer;
    public bool RaceModeHidesAllLogic { get; set; } = false;

    /// <summary>Reconnect a slot whose connection dropped (a few tries over about 30 minutes, then stop).</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>When a seed was made with another apworld version, find and use that version automatically (from trusted sources only).</summary>
    public bool AutoFixApworldVersions { get; set; } = true;

    /// <summary>Optional URL of a newer apworld source list (Atlas's format). Empty: the bundled list and GitHub releases.</summary>
    public string ApworldSourcesUrl { get; set; } = "";
    /// <summary>Download sources the user trusts ("github.com/owner/repo" or a host), so they're asked only once.</summary>
    public List<string> ApprovedApworldSources { get; set; } = new List<string>();
    /// <summary>Game → GitHub projects ("owner/name") the user added as sources of its apworld.</summary>
    public Dictionary<string, List<string>> ExtraApworldRepos { get; set; } = new Dictionary<string, List<string>>();

    // Window State
    public int WindowWidth { get; set; } = 1024;
    public int WindowHeight { get; set; } = 768;
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public bool WindowMaximized { get; set; } = false;
}
public class SlotStats
{
    public int TotalCount { get; set; }
    public int CompleteCount { get; set; }
    public int LogicCount { get; set; }
    public string GameName { get; set; }
    public System.DateTime LastUpdated { get; set; } = System.DateTime.Now;
}

public class MultiworldProfile
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Multiworld";
    public string ServerUrl { get; set; } = "archipelago.gg:38281";
    public string Password { get; set; } = "";
    public List<string> Slots { get; set; } = new List<string>();
    public List<string> ActiveSlots { get; set; } = new List<string>();
    public Dictionary<string, SlotStats> SavedStats { get; set; } = new Dictionary<string, SlotStats>();

    // Legacy support
    public string SlotName { get; set; } = "";
}

public class OfflineLocation
{
    public long Id { get; set; }
    public string Name { get; set; }
    public bool IsChecked { get; set; }
}

public class OfflineCircle
{
    public int CircleId { get; set; }
    public List<OfflineLocation> Locations { get; set; } = new List<OfflineLocation>();
}

public class OfflineSlotCache
{
    public string ProfileId { get; set; }
    public string SlotName { get; set; }
    public string Game { get; set; }
    public List<OfflineCircle> Circles { get; set; } = new List<OfflineCircle>();
    public List<string> ChatHistory { get; set; } = new List<string>();
}

public static class DataManager
{
    private static string _dataDir;

    /// <summary>
    /// Where Atlas keeps everything (PortableData next to the program). ATLAS_DATA_DIR overrides it, which the
    /// self-test uses so it never touches real data.
    /// </summary>
    public static string GetDataDirectory()
    {
        if (_dataDir != null) return _dataDir;
        string overridden = System.Environment.GetEnvironmentVariable("ATLAS_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden)) _dataDir = Path.GetFullPath(overridden);
        else if (OS.HasFeature("editor")) _dataDir = ProjectSettings.GlobalizePath("res://PortableData");
        else _dataDir = Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()), "PortableData");
        return _dataDir;
    }

    /// <summary>Raised when a save failed even after retries (disk full, permissions): the file name and why.</summary>
    public static event System.Action<string, string> SaveFailed;

    private static void Save(string path, object value)
    {
        try { AP_Atlas.Core.SafeFile.WriteJson(path, value); }
        catch (System.Exception ex)
        {
            AP_Atlas.Core.Logger.LogError($"Couldn't save {Path.GetFileName(path)}: {ex.Message}");
            try { SaveFailed?.Invoke(Path.GetFileName(path), ex.Message); } catch { }
        }
    }

    public static AppSettings LoadSettings()
    {
        var settings = AP_Atlas.Core.SafeFile.ReadJson(Path.Combine(GetDataDirectory(), "settings.json"), () => new AppSettings());
        // Older or hand-edited files can leave collections null.
        settings.MapCameras ??= new Dictionary<string, MapCameraSave>();
        settings.CollapsedPropertySections ??= new List<string>();
        settings.SlotYamlPaths ??= new Dictionary<string, string>();
        settings.EngineMode ??= "";
        settings.ArchipelagoInstallationPath ??= "";
        settings.ApworldSourcesUrl ??= "";
        settings.ApprovedApworldSources ??= new List<string>();
        settings.ExtraApworldRepos = new Dictionary<string, List<string>>(settings.ExtraApworldRepos ?? new Dictionary<string, List<string>>(), System.StringComparer.OrdinalIgnoreCase);
        if (settings.MapNodeScale <= 0 || float.IsNaN(settings.MapNodeScale)) settings.MapNodeScale = 1f;
        return settings;
    }

    public static void SaveSettings(AppSettings settings) => Save(Path.Combine(GetDataDirectory(), "settings.json"), settings);

    public static List<MultiworldProfile> LoadProfiles()
    {
        string path = Path.Combine(GetDataDirectory(), "profiles.json");
        var list = AP_Atlas.Core.SafeFile.ReadJson(path, () => new List<MultiworldProfile>());
        list.RemoveAll(p => p == null);

        foreach (var profile in list)
        {
            profile.Slots ??= new List<string>();
            profile.ActiveSlots ??= new List<string>();
            profile.Slots.RemoveAll(string.IsNullOrWhiteSpace);
            if (!string.IsNullOrEmpty(profile.SlotName))
            {
                if (!profile.Slots.Contains(profile.SlotName))
                {
                    profile.Slots.Add(profile.SlotName);
                }
                profile.SlotName = ""; // Clear it so we don't migrate again
            }

            // Cleanup orphaned ActiveSlots (slots that were deleted but stayed pinned due to previous bug)
            if (profile.ActiveSlots != null)
            {
                profile.ActiveSlots.RemoveAll(s => !profile.Slots.Contains(s));
            }

            if (profile.SavedStats == null)
            {
                profile.SavedStats = new Dictionary<string, SlotStats>();
            }
        }

        return list;
    }

    public static void SaveProfiles(List<MultiworldProfile> profiles) => Save(Path.Combine(GetDataDirectory(), "profiles.json"), profiles);

    public static void SaveOfflineCache(OfflineSlotCache cache)
    {
        string filename = $"offline_{cache.ProfileId}_{cache.SlotName}.json";
        foreach (char c in Path.GetInvalidFileNameChars()) filename = filename.Replace(c, '_');
        Save(Path.Combine(GetDataDirectory(), filename), cache);
    }

    public static OfflineSlotCache LoadOfflineCache(string profileId, string slotName)
    {
        string filename = $"offline_{profileId}_{slotName}.json";
        foreach (char c in Path.GetInvalidFileNameChars()) filename = filename.Replace(c, '_');
        return AP_Atlas.Core.SafeFile.ReadJson<OfflineSlotCache>(Path.Combine(GetDataDirectory(), filename), () => null);
    }

    // =====================================================================
    // Slot data (the seed's options as the server sent them at login), kept for offline use
    // =====================================================================

    public class SavedSlotData
    {
        public string ProfileId { get; set; }
        public string SlotName { get; set; }
        public string Game { get; set; }
        public System.DateTime Saved { get; set; } = System.DateTime.Now;
        public Newtonsoft.Json.Linq.JToken SlotData { get; set; }
    }

    private static string SlotDataDir => Path.Combine(GetDataDirectory(), "slot_data");

    private static string SlotDataFile(string profileId, string slotName)
    {
        string name = $"{profileId}_{slotName}.json";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(SlotDataDir, name);
    }

    public static void SaveSlotData(string profileId, string slotName, string game, Dictionary<string, object> slotData)
    {
        if (slotData == null) return;
        try
        {
            var saved = new SavedSlotData { ProfileId = profileId, SlotName = slotName, Game = game, SlotData = Newtonsoft.Json.Linq.JToken.FromObject(slotData) };
            AP_Atlas.Core.SafeFile.WriteJson(SlotDataFile(profileId, slotName), saved);
        }
        catch (System.Exception ex)
        {
            AP_Atlas.Core.Logger.LogWarning($"Couldn't save slot data for {slotName}: {ex.Message}");
        }
    }

    public static SavedSlotData LoadSlotData(string profileId, string slotName)
    {
        try
        {
            return AP_Atlas.Core.SafeFile.ReadJson<SavedSlotData>(SlotDataFile(profileId, slotName), () => null);
        }
        catch { return null; }
    }

    /// <summary>The most recently saved slot data of any slot playing a game (for checking that game's pack offline).</summary>
    public static SavedSlotData LatestSlotDataForGame(string game)
    {
        if (string.IsNullOrEmpty(game) || !Directory.Exists(SlotDataDir)) return null;
        SavedSlotData best = null;
        foreach (var file in Directory.GetFiles(SlotDataDir, "*.json"))
        {
            try
            {
                var s = JsonConvert.DeserializeObject<SavedSlotData>(File.ReadAllText(file));
                if (s != null && string.Equals(s.Game, game, System.StringComparison.OrdinalIgnoreCase) && (best == null || s.Saved > best.Saved)) best = s;
            }
            catch { }
        }
        return best;
    }

    public static void DeleteOfflineCache(string profileId, string slotName)
    {
        string filename = $"offline_{profileId}_{slotName}.json";
        foreach (char c in Path.GetInvalidFileNameChars()) filename = filename.Replace(c, '_');
        string path = Path.Combine(GetDataDirectory(), filename);
        if (File.Exists(path)) File.Delete(path);
    }
}
