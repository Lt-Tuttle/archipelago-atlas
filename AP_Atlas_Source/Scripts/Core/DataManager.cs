#nullable disable
using System.IO;
using System.Linq;
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
    public string ThemeAccentColor { get; set; } = "#8A2BE2";
    /// <summary>"follow" (Windows's light or dark mode), "dark", "light" or "high-contrast".</summary>
    public string Theme { get; set; } = "follow";
    /// <summary>The theme's colour-blind-safe palette (blue and orange in place of green and red) instead of its usual one.</summary>
    public bool ColourBlindSafe { get; set; } = false;
    /// <summary>The whole window's zoom, in percent (100 = as designed).</summary>
    public int UiZoom { get; set; } = 100;
    /// <summary>The Map Tracker's pin shape: "round", "square" or "diamond".</summary>
    public string MapMarkerStyle { get; set; } = "round";
    /// <summary>What Atlas opens on: "home", "last" (the tool shown when it closed) or "multiworlds".</summary>
    public string StartupPage { get; set; } = "home";
    /// <summary>The tool shown last (its id), for "last".</summary>
    public string LastTool { get; set; } = "";
    public string ArchipelagoInstallationPath { get; set; } = "";
    /// <summary>"Portable" (Atlas's own engine) or "Existing" (the install above). Empty until the user chooses.</summary>
    public string EngineMode { get; set; } = "";
    /// <summary>Slot key ("profileId|slotName") → the player YAML the user linked to that slot.</summary>
    public Dictionary<string, string> SlotYamlPaths { get; set; } = new Dictionary<string, string>();
    /// <summary>The folder the YAML picker last took a file from (it opens there next time). Atlas never searches for YAMLs.</summary>
    public string LastYamlFolder { get; set; } = "";
    /// <summary>What the user chose "Always allow" for (see Permissions): "kind" or "kind|scope".</summary>
    public List<string> PermissionsAllowed { get; set; } = new List<string>();
    /// <summary>Keys the user rebound: command id ("tool.map-tracker") → shortcut ("Ctrl+3"), or "" for none. Help → Keyboard Shortcuts lists the ids.</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = new Dictionary<string, string>();

    // Layout State
    public int MainSplitOffset { get; set; } = 300;
    public int SplitRightSidebarOffset { get; set; } = 0;
    public int SplitCenterRightOffset { get; set; } = 0;
    public int SplitContentOffset { get; set; } = 0;

    // Map Tracker State
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

    // Cheese Tracker (cheesetrackers.theincrediblewheelofchee.se, or another instance the user runs)
    /// <summary>The Cheese Tracker site Atlas talks to. The API key below is only ever sent to this site.</summary>
    public string CheeseInstanceUrl { get; set; } = AP_Atlas.Core.CheeseTracker.CheeseClient.DefaultInstance;
    /// <summary>The user's Cheese Tracker API key, encrypted for this Windows account (DPAPI). Empty: none.</summary>
    public string CheeseApiKeyProtected { get; set; } = "";
    /// <summary>The Cheese Tracker account the key belongs to (checked when the key was added).</summary>
    public int? CheeseUserId { get; set; }
    public string CheeseUserName { get; set; } = "";
    /// <summary>Slot keys ("profileId|slotName") whose Cheese Tracker status Atlas keeps updated by itself.</summary>
    public List<string> CheeseAutoSlots { get; set; } = new List<string>();
    /// <summary>Slot key → the progression status Atlas last set (or adopted). Anything else means someone changed it.</summary>
    public Dictionary<string, string> CheeseAutoLastSet { get; set; } = new Dictionary<string, string>();
    /// <summary>Slot key → why automatic updates are paused for it (until the user resumes them).</summary>
    public Dictionary<string, string> CheeseAutoPaused { get; set; } = new Dictionary<string, string>();
    // The Cheese Tracker tab: what it shows (filters reset each session, like Cheese Tracker's; the table's sort is in Tables).
    public string CheeseTabView { get; set; } = "";
    public bool CheeseMineFirst { get; set; } = true;
    public bool CheeseChecksAsPercent { get; set; } = false;
    public int CheeseTableSplitOffset { get; set; } = 0;
    // The Sphere Tracker tab: the slot (or the settings) it last showed.
    public string SphereTabView { get; set; } = "";
    /// <summary>Each table's remembered sort and hidden columns, by the table's id (AtlasTable).</summary>
    public Dictionary<string, TablePrefs> Tables { get; set; } = new Dictionary<string, TablePrefs>();

    // Window State
    public int WindowWidth { get; set; } = 1024;
    public int WindowHeight { get; set; } = 768;
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public bool WindowMaximized { get; set; } = false;
    // Which parts of the window show (the View menu). Focus mode isn't remembered.
    public bool ShowSlotsPanel { get; set; } = true;
    public bool ShowExplorer { get; set; } = true;
    public bool ShowPropertiesPanel { get; set; } = true;
    public bool ShowBottomPane { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;
    public bool ShowSystemLogTab { get; set; } = true;
    /// <summary>Shows Atlas's diagnostics: the Debug Log tab, frame hitch warnings on the status bar, the Debug Log's menu items.</summary>
    public bool DeveloperMode { get; set; } = false;

    // ---- Updates (Settings -> Updates) ----
    /// <summary>Read Atlas's releases list on GitHub once a day (needs the update-check permission as well).</summary>
    public bool UpdateCheckDaily { get; set; } = false;
    /// <summary>"auto" (a beta build follows the beta channel, a release the stable one), "stable" or "beta".</summary>
    public string UpdateChannel { get; set; } = "auto";
    /// <summary>When the releases list was last read: the wall clock, for "once a day" across restarts.</summary>
    public System.DateTime? UpdateLastCheckUtc { get; set; }
    /// <summary>Whether the one-time offer to turn the daily check on was shown.</summary>
    public bool UpdateOffered { get; set; } = false;
    /// <summary>How many times Atlas has started (the offer comes on the second start, never the first).</summary>
    public int StartCount { get; set; } = 0;
}
public class SlotStats
{
    public int TotalCount { get; set; }
    public int CompleteCount { get; set; }
    public int LogicCount { get; set; }
    public string GameName { get; set; }
    /// <summary>The slot's player number in its multiworld (matches Cheese Tracker rows while offline). 0: unknown.</summary>
    public int SlotNumber { get; set; }
    public System.DateTime LastUpdated { get; set; } = System.DateTime.Now;
}

public class MultiworldProfile
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Multiworld";
    public string ServerUrl { get; set; } = "archipelago.gg:38281";

    /// <summary>The room's link, as the host shared it (https://archipelago.gg/room/…), for the one status read Atlas makes about it; empty when none.</summary>
    public string RoomLink { get; set; } = "";

    /// <summary>The room password, in memory only: profiles.json keeps it encrypted for this Windows account (PasswordProtected).</summary>
    [JsonIgnore] public string Password { get; set; } = "";

    /// <summary>
    /// The room password encrypted with Windows' data protection for this account (never plain text). One that can't be
    /// decrypted here (another account or PC) is kept as it was, so it isn't lost if the folder moves back.
    /// </summary>
    [JsonProperty("PasswordProtected")]
    public string PasswordProtected
    {
        get => string.IsNullOrEmpty(Password) ? _unreadablePassword ?? "" : AP_Atlas.Core.Secrets.Protect(Password, AP_Atlas.Core.Secrets.RoomPassword) ?? "";
        set
        {
            _unreadablePassword = null;
            if (string.IsNullOrEmpty(value)) return;
            string plain = AP_Atlas.Core.Secrets.Unprotect(value, AP_Atlas.Core.Secrets.RoomPassword);
            if (plain != null) Password = plain;
            else _unreadablePassword = value;
        }
    }

    private string _unreadablePassword;

    /// <summary>A saved password couldn't be decrypted on this PC or Windows account: it has to be entered again.</summary>
    [JsonIgnore] public bool PasswordUnreadable => _unreadablePassword != null && string.IsNullOrEmpty(Password);

    /// <summary>Older files kept the password in plain text: it's read once, then saved encrypted.</summary>
    [JsonProperty("Password")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0051", Justification = "Json.NET sets it when it reads an older profile")]
    private string LegacyPassword
    {
        set
        {
            if (string.IsNullOrEmpty(value) || !string.IsNullOrEmpty(Password)) return;
            Password = value;
            HadPlainTextPassword = true;
        }
    }

    /// <summary>This profile was read with its password in plain text (it's encrypted on the next save).</summary>
    [JsonIgnore] public bool HadPlainTextPassword { get; private set; }
    public List<string> Slots { get; set; } = new List<string>();
    public List<string> ActiveSlots { get; set; } = new List<string>();
    public Dictionary<string, SlotStats> SavedStats { get; set; } = new Dictionary<string, SlotStats>();
    /// <summary>This multiworld's Cheese Tracker page ("https://…/tracker/&lt;id&gt;"). Empty: not linked.</summary>
    public string CheeseTrackerUrl { get; set; } = "";
    /// <summary>The spheretracker.de room this multiworld's host created and shared. Empty: none.</summary>
    public string SphereTrackerUrl { get; set; } = "";
    /// <summary>Whether the room is a race, as last reported by the server (null: never connected since this was kept).</summary>
    public bool? RaceRoom { get; set; }

    // Legacy support
    public string SlotName { get; set; } = "";
}

/// <summary>What a table remembers: the column it sorts by (and which way), and the columns hidden.</summary>
public class TablePrefs
{
    public string SortColumn { get; set; } = "";
    public bool SortDescending { get; set; }
    public List<string> HiddenColumns { get; set; } = new List<string>();
}

public static class DataManager
{
    private static string _dataDir;

    /// <summary>
    /// Settles the data folder (the window does it first thing, from <see cref="AP_Atlas.Core.DataFolder"/>): the portable
    /// folder, or the one the user chose when it can't be written. Before anything reads or writes.
    /// </summary>
    public static void SetDataDirectory(string folder) => _dataDir = Path.GetFullPath(folder);

    /// <summary>
    /// Where Atlas keeps everything (PortableData next to the program, unless the window settled another folder first).
    /// ATLAS_DATA_DIR overrides it, which the self-test uses so it never touches real data.
    /// </summary>
    public static string GetDataDirectory()
    {
        if (_dataDir != null) return _dataDir;
        string overridden = System.Environment.GetEnvironmentVariable("ATLAS_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden)) _dataDir = Path.GetFullPath(overridden);
        else if (OS.HasFeature("editor"))
        {
            _dataDir = ProjectSettings.GlobalizePath("res://PortableData");
            // Run from source, the data folder sits inside the Godot project: .gdignore keeps Godot from importing what's in it.
            // (Logger finds its folder through here, so a failure goes straight to Godot's output.)
            try
            {
                string ignore = Path.Combine(_dataDir, ".gdignore");
                if (!File.Exists(ignore)) AP_Atlas.Core.SafeFile.WriteAllText(ignore, "");
            }
            catch (System.Exception ex) { GD.PrintErr("Couldn't mark the data folder for Godot to skip: " + ex.Message); }
        }
        else _dataDir = Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()), "PortableData");
        return _dataDir;
    }

    private static AP_Atlas.Core.Connections.DataPackageStore _dataPackages;

    /// <summary>
    /// The games' names as servers sent them (data packages), kept in the data folder's datapackage_cache: every
    /// connection uses it instead of the connection library's own cache outside Atlas's folder. Any thread.
    /// </summary>
    public static AP_Atlas.Core.Connections.DataPackageStore DataPackages =>
        System.Threading.LazyInitializer.EnsureInitialized(ref _dataPackages,
            () => new AP_Atlas.Core.Connections.DataPackageStore(Path.Combine(GetDataDirectory(), "datapackage_cache")));

    /// <summary>Raised when a save failed even after retries (disk full, permissions): the file name and why.</summary>
    public static event System.Action<string, string> SaveFailed;

    private static void Save(string path, object value)
    {
        try { AP_Atlas.Core.SafeFile.WriteJson(path, value); }
        catch (System.Exception ex) { ReportSaveFailure(Path.GetFileName(path), ex); }
    }

    /// <summary>Logs a failed save of the user's own data and raises SaveFailed, so they hear about it. Never throws.</summary>
    public static void ReportSaveFailure(string file, System.Exception error)
    {
        try { AP_Atlas.Core.Logger.LogError($"Couldn't save {file}: {error.Message}"); }
        catch (System.Exception logFailure) { GD.PrintErr($"Couldn't save {file} ({error.Message}), and couldn't log it: {logFailure.Message}"); }
        try { SaveFailed?.Invoke(file, error.Message); }
        catch (System.Exception handlerFailure) { GD.PrintErr($"Couldn't show that {file} wasn't saved: {handlerFailure.Message}"); }
    }

    private static AppSettings _settingsToSave;
    private static int _settingsSaveRequest;

    /// <summary>
    /// Saves settings half a second after the last call, so a burst of changes is written once (dragging a splitter
    /// reports every mouse move, and each save is flushed to disk). Called off the main thread, it hops there first.
    /// An immediate SaveSettings, or FlushPendingSaves when Atlas closes, writes a pending save at once.
    /// </summary>
    public static void SaveSettingsSoon(AppSettings settings)
    {
        if (Engine.GetMainLoop() is not SceneTree tree) { SaveSettings(settings); return; }
        if (OS.GetThreadCallerId() != OS.GetMainThreadId())
        {
            AP_Atlas.UI.Ui.Defer(null, () => SaveSettingsSoon(settings), "saving settings");
            return;
        }
        _settingsToSave = settings;
        int request = ++_settingsSaveRequest;
        tree.CreateTimer(0.5).Timeout += () =>
        {
            if (request == _settingsSaveRequest) FlushPendingSaves();
        };
    }

    /// <summary>Writes a settings save that SaveSettingsSoon is still waiting on, if there is one.</summary>
    public static void FlushPendingSaves()
    {
        var settings = _settingsToSave;
        _settingsToSave = null;
        if (settings != null) SaveSettings(settings);
    }

    public static AppSettings LoadSettings()
    {
        var settings = AP_Atlas.Core.SafeFile.ReadJson(Path.Combine(GetDataDirectory(), "settings.json"), () => new AppSettings());
        // Older or hand-edited files can leave collections null.
        settings.MapCameras ??= new Dictionary<string, MapCameraSave>();
        settings.CollapsedPropertySections ??= new List<string>();
        settings.SlotYamlPaths ??= new Dictionary<string, string>();
        settings.LastYamlFolder ??= "";
        settings.PermissionsAllowed ??= new List<string>();
        settings.KeyBindings ??= new Dictionary<string, string>();
        settings.EngineMode ??= "";
        settings.ArchipelagoInstallationPath ??= "";
        settings.ApworldSourcesUrl ??= "";
        settings.ApprovedApworldSources ??= new List<string>();
        settings.ExtraApworldRepos = new Dictionary<string, List<string>>(settings.ExtraApworldRepos ?? new Dictionary<string, List<string>>(), System.StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(settings.CheeseInstanceUrl)) settings.CheeseInstanceUrl = AP_Atlas.Core.CheeseTracker.CheeseClient.DefaultInstance;
        settings.CheeseApiKeyProtected ??= "";
        settings.CheeseUserName ??= "";
        settings.CheeseAutoSlots ??= new List<string>();
        settings.CheeseAutoLastSet ??= new Dictionary<string, string>();
        settings.CheeseAutoPaused ??= new Dictionary<string, string>();
        settings.CheeseTabView ??= "";
        settings.SphereTabView ??= "";
        settings.Tables ??= new Dictionary<string, TablePrefs>();
        if (string.IsNullOrWhiteSpace(settings.Theme)) settings.Theme = "follow";
        foreach (var key in new List<string>(settings.Tables.Keys))
        {
            settings.Tables[key] ??= new TablePrefs();
            settings.Tables[key].SortColumn ??= "";
            settings.Tables[key].HiddenColumns ??= new List<string>();
        }
        if (settings.MapNodeScale <= 0 || float.IsNaN(settings.MapNodeScale)) settings.MapNodeScale = 1f;
        settings.UiZoom = System.Math.Clamp(settings.UiZoom, 50, 200);
        if (settings.MapMarkerStyle is not ("round" or "square" or "diamond")) settings.MapMarkerStyle = "round";
        if (settings.StartupPage is not ("home" or "last" or "multiworlds")) settings.StartupPage = "home";
        settings.LastTool ??= "";
        return settings;
    }

    public static void SaveSettings(AppSettings settings)
    {
        if (HopToMainThread("settings", () => SaveSettings(settings))) return;
        // This save includes whatever SaveSettingsSoon was waiting to write.
        if (ReferenceEquals(_settingsToSave, settings)) _settingsToSave = null;
        Save(Path.Combine(GetDataDirectory(), "settings.json"), settings);
    }

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
            profile.CheeseTrackerUrl ??= "";
            profile.SphereTrackerUrl ??= "";
        }

        // Passwords from an older plain-text file: save encrypted now, twice, so the .bak copy is encrypted too.
        if (list.Any(p => p.HadPlainTextPassword))
        {
            SaveProfiles(list);
            SaveProfiles(list);
            AP_Atlas.Core.Logger.LogInfo("Room passwords are now saved encrypted for this Windows account.");
        }
        return list;
    }

    /// <summary>
    /// Damaged copies of profiles.json that Atlas set aside (".corrupt-…") which still hold a room password in plain
    /// text (from before passwords were encrypted). The user is offered to delete them.
    /// </summary>
    public static List<string> PlainTextPasswordCopies()
    {
        var found = new List<string>();
        try
        {
            foreach (var file in Directory.GetFiles(GetDataDirectory(), "profiles.json.corrupt-*"))
            {
                try
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(file), "\"Password\"\\s*:\\s*\"[^\"]")) found.Add(file);
                }
                catch (System.Exception ex) { AP_Atlas.Core.Logger.LogDebug($"Couldn't check {Path.GetFileName(file)} for plain-text passwords: {ex.Message}"); }
            }
        }
        catch (System.Exception ex) { AP_Atlas.Core.Logger.LogDebug("Couldn't look for old damaged copies of profiles.json: " + ex.Message); }
        return found;
    }

    public static void SaveProfiles(List<MultiworldProfile> profiles)
    {
        if (HopToMainThread("profiles", () => SaveProfiles(profiles))) return;
        Save(Path.Combine(GetDataDirectory(), "profiles.json"), profiles);
    }

    /// <summary>
    /// Settings and profiles are changed on the main thread, so they're saved there too: a save from another thread
    /// could catch them mid-change. One that arrives from elsewhere moves to the main thread, and the log says so (it's
    /// a mistake to fix). Returns true when it moved.
    /// </summary>
    private static bool HopToMainThread(string what, System.Action save)
    {
        if (Engine.GetMainLoop() is not SceneTree || OS.GetThreadCallerId() == OS.GetMainThreadId()) return false;
        AP_Atlas.Core.Logger.LogWarning($"The {what} were saved from a background thread; saving them on the main thread instead.");
        AP_Atlas.Core.Logger.LogDebug(System.Environment.StackTrace);
        AP_Atlas.UI.Ui.Defer(null, save, $"saving the {what}");
        return true;
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
            catch (System.Exception ex) { AP_Atlas.Core.Logger.LogDebug($"Skipped saved slot data {Path.GetFileName(file)}: {ex.Message}"); }
        }
        return best;
    }

    public static void DeleteOfflineCache(string profileId, string slotName)
    {
        string filename = $"offline_{profileId}_{slotName}.json";
        foreach (char c in Path.GetInvalidFileNameChars()) filename = filename.Replace(c, '_');
        string path = Path.Combine(GetDataDirectory(), filename);
        // With its backup: otherwise the next read would bring the deleted cache back.
        try { AP_Atlas.Core.SafeFile.Delete(path); }
        catch (System.Exception ex) { AP_Atlas.Core.Logger.LogWarning($"Couldn't delete {filename}: {ex.Message}"); }
    }
}
