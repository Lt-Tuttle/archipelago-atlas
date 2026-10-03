using System.IO;
using Godot;
using Newtonsoft.Json;
using System.Collections.Generic;

public class MapCameraSave {
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
    
    // Layout State
    public int MainSplitOffset { get; set; } = 300;
    public int SplitRightSidebarOffset { get; set; } = 0;
    public int SplitCenterRightOffset { get; set; } = 0;
    public int SplitContentOffset { get; set; } = 0;
    
    // Map Tracker State
    public int MapSplitOffset { get; set; } = 350;
    public int MapFontSize { get; set; } = 15;
    public float MapNodeScale { get; set; } = 1.0f;
    public int MapSortIndex { get; set; } = 0;
    public float KeyItemZoom { get; set; } = 1.0f;
    public Dictionary<string, MapCameraSave> MapCameras { get; set; } = new Dictionary<string, MapCameraSave>();

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
    public static string GetDataDirectory()
    {
        if (OS.HasFeature("editor"))
        {
            return ProjectSettings.GlobalizePath("res://PortableData");
        }
        else
        {
            string exePath = OS.GetExecutablePath();
            return Path.Combine(Path.GetDirectoryName(exePath), "PortableData");
        }
    }

    public static AppSettings LoadSettings()
    {
        string path = Path.Combine(GetDataDirectory(), "settings.json");
        if (!File.Exists(path)) return new AppSettings();
        return JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
    {
        string dir = GetDataDirectory();
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), JsonConvert.SerializeObject(settings, Formatting.Indented));
    }

    public static List<MultiworldProfile> LoadProfiles()
    {
        string dir = GetDataDirectory();
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, "profiles.json");
        if (!File.Exists(path)) return new List<MultiworldProfile>();

        string json = File.ReadAllText(path);
        var list = JsonConvert.DeserializeObject<List<MultiworldProfile>>(json) ?? new List<MultiworldProfile>();
        
        foreach(var profile in list)
        {
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

    public static void SaveProfiles(List<MultiworldProfile> profiles)
    {
        string dir = GetDataDirectory();
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, "profiles.json");
        string json = JsonConvert.SerializeObject(profiles, Formatting.Indented);
        File.WriteAllText(path, json);
    }

    public static void SaveOfflineCache(OfflineSlotCache cache)
    {
        string dir = GetDataDirectory();
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        string filename = $"offline_{cache.ProfileId}_{cache.SlotName}.json";
        foreach (char c in Path.GetInvalidFileNameChars()) filename = filename.Replace(c, '_');
        File.WriteAllText(Path.Combine(dir, filename), JsonConvert.SerializeObject(cache, Formatting.Indented));
    }

    public static OfflineSlotCache LoadOfflineCache(string profileId, string slotName)
    {
        string filename = $"offline_{profileId}_{slotName}.json";
        foreach (char c in Path.GetInvalidFileNameChars()) filename = filename.Replace(c, '_');
        string path = Path.Combine(GetDataDirectory(), filename);
        if (!File.Exists(path)) return null;
        try {
            return JsonConvert.DeserializeObject<OfflineSlotCache>(File.ReadAllText(path));
        } catch { return null; }
    }

    public static void DeleteOfflineCache(string profileId, string slotName)
    {
        string filename = $"offline_{profileId}_{slotName}.json";
        foreach (char c in Path.GetInvalidFileNameChars()) filename = filename.Replace(c, '_');
        string path = Path.Combine(GetDataDirectory(), filename);
        if (File.Exists(path)) File.Delete(path);
    }
}

