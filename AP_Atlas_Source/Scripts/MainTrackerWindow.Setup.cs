#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>The setup window (Set up Atlas): its hooks into the window, and when it opens by itself.</summary>
public partial class MainTrackerWindow
{
    private AP_Atlas.UI.SetupSteps? _setupSteps;

    /// <summary>The setup steps Home's line and the setup window share.</summary>
    private AP_Atlas.UI.SetupSteps SetupSteps => _setupSteps ??= new AP_Atlas.UI.SetupSteps(new AP_Atlas.UI.SetupSteps.Hooks
    {
        EngineReady = () => AP_Atlas.Core.EngineSetup.AtlasEngine.Current.CanLaunch,
        Profiles = () => _profiles,
        AnyPackInstalled = AnyMapPackInstalled,
        PagesDone = () => _appSettings.SetupPagesDone,
        MarkPageDone = id =>
        {
            if (_appSettings.SetupPagesDone.Contains(id)) return;
            _appSettings.SetupPagesDone.Add(id);
            DataManager.SaveSettingsSoon(_appSettings);
        },
        IsSkipped = id => _appSettings.SkippedHomeSteps.Contains(id),
        SetSkipped = (id, skipped) =>
        {
            bool has = _appSettings.SkippedHomeSteps.Contains(id);
            if (skipped == has) return;
            if (skipped) _appSettings.SkippedHomeSteps.Add(id);
            else _appSettings.SkippedHomeSteps.Remove(id);
            DataManager.SaveSettingsSoon(_appSettings);
        },
    });

    /// <summary>Help → Set up Atlas…, Home's Resume and the first start: the setup window, at the first step left (resume) or from its Welcome page.</summary>
    internal AP_Atlas.UI.SetupWizard OpenSetupWizard(bool resume) => AP_Atlas.UI.SetupWizard.Open(this, SetupHooks(), resume);

    private AP_Atlas.UI.SetupWizard.Hooks SetupHooks()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        return new AP_Atlas.UI.SetupWizard.Hooks
        {
            Tr = text => Tr(text),
            Settings = _appSettings,
            Steps = SetupSteps,
            ApplyTheme = ApplyTheme,
            ApplyColourBlindSafe = ApplyColourBlindSafe,
            SetZoom = SetZoom,
            ZoomSteps = ZoomSteps,
            EngineReady = () => AP_Atlas.Core.EngineSetup.AtlasEngine.Current.CanLaunch,
            OpenEngineWindow = lines => OpenEngineWindow(lines),
            Profiles = () => _profiles,
            SaveMultiworld = SaveSetupMultiworld,
            PickYaml = PickYamlFor,
            KeepYaml = KeepYamlForSlots,
            KnownGames = () => _gamesPage?.GameNames() ?? new List<string>(),
            GameStatus = SlotGameStatus,
            ApworldInstalled = game =>
            {
                var install = AP_Atlas.Core.EngineSetup.AtlasEngine.Current;
                return install != null && install.CanLaunch && (AP_Atlas.Core.EngineSetup.AtlasEngine.LastCheck(install)?.Games.Contains(game, System.StringComparer.OrdinalIgnoreCase) ?? false);
            },
            KnownProject = game => AP_Atlas.Core.EngineSetup.ApworldSources.Find(game)?.Repo
                ?? (_appSettings.ExtraApworldRepos != null && _appSettings.ExtraApworldRepos.TryGetValue(game, out var repos) ? repos.FirstOrDefault() : null),
            InstallApworldAsync = (game, parent) => _gamesPage != null ? _gamesPage.InstallNewestAsync(game, parent) : Task.FromResult(false),
            PackInstalledFor = game => AP_Atlas.Core.EngineSetup.SoloTestRunner.InstalledPackFor(game) != null,
            RecommendedPack = game => AP_Atlas.Core.MapPackManagerControl.GuideRecommendations(game).Select(r => r.Repo).FirstOrDefault(),
            SearchPacksAsync = async game =>
            {
                var (candidates, problem) = await AP_Atlas.Core.MapPackManagerControl.SearchPacksAsync(game);
                return (candidates.Select(c => new AP_Atlas.UI.SetupPackCandidate(c.Repo, c.Description ?? "", c.Stars, c.Fork, c.Recommended)).ToList(), problem);
            },
            InstallPackAsync = async (game, repo, say) =>
            {
                var (ok, detail, file) = await _packManagerPanel.InstallFittingReleaseAsync(game, repo, say);
                return (ok, detail, file);
            },
            PreferPack = (game, file) =>
            {
                _appSettings.PreferredPacks[game] = file;
                DataManager.SaveSettingsSoon(_appSettings);
            },
            LinkCheese = LinkCheeseFromEditor,
            LinkSphere = (parent, profile, text) => AP_Atlas.Core.Async.Fire(LinkSphereFromEditorAsync(parent, profile, text), "linking the sphere tracker"),
            DailyUpdateChecks = on =>
            {
                if (on) AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.UpdateChecks, null, true);
                _appSettings.UpdateCheckDaily = on;
                _appSettings.UpdateOffered = true; // the one-time card needn't ask what this page just did
                DataManager.SaveSettings(_appSettings);
                _settingsPage?.RefreshRows();
            },
            CrashReportsAlways = always =>
            {
                if (always != AP_Atlas.Core.Permissions.IsAlwaysAllowed(_appSettings, AP_Atlas.Core.Permissions.CrashReports))
                    AP_Atlas.Core.Permissions.SetAlways(_appSettings, AP_Atlas.Core.Permissions.CrashReports, null, always);
                _settingsPage?.RefreshRows();
            },
            IsSlotLive = IsSlotLive,
            ConnectSlot = (profile, slot) => OnConnectSlotPressed(slot, profile),
        };
    }

    /// <summary>
    /// The setup window's multiworld: a new one, or the existing one the draft names, with its name, room link, server and
    /// password, its slots (added to an existing one's; Atlas's placeholder slot gives way), each slot's game noted and
    /// its YAML tied to it; saved, listed and selected on the Multiworlds page.
    /// </summary>
    internal MultiworldProfile SaveSetupMultiworld(AP_Atlas.UI.SetupMultiworldDraft draft)
    {
        var profile = draft.ExistingId == null ? null : _profiles.FirstOrDefault(p => p.Id == draft.ExistingId);
        bool isNew = profile == null;
        profile ??= new MultiworldProfile();
        profile.Name = draft.Name.Trim();
        profile.RoomLink = draft.RoomLink ?? "";
        profile.ServerUrl = (draft.Server ?? "").Trim();
        profile.Password = draft.Password ?? "";
        if (profile.Slots.All(AP_Atlas.UI.SetupSteps.IsPlaceholderSlot)) profile.Slots.Clear();
        foreach (string slot in draft.Slots.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct())
        {
            if (!profile.Slots.Contains(slot)) profile.Slots.Add(slot);
            if (draft.GameBySlot.TryGetValue(slot, out var game) && !string.IsNullOrWhiteSpace(game))
            {
                if (!profile.SavedStats.TryGetValue(slot, out var stats)) profile.SavedStats[slot] = stats = new SlotStats();
                if (string.IsNullOrWhiteSpace(stats.GameName)) stats.GameName = game;
            }
            if (draft.YamlBySlot.TryGetValue(slot, out var yaml) && !string.IsNullOrEmpty(yaml)) _appSettings.SlotYamlPaths[AP_Atlas.Core.Annotations.SlotKey(profile.Id, slot)] = yaml;
        }
        if (isNew) _profiles.Add(profile);
        DataManager.SaveProfiles(_profiles);
        DataManager.SaveSettings(_appSettings);
        RefreshProfileList();
        SelectProfile(profile);
        UpdateSidebar();
        _homePage?.Refresh();
        return profile;
    }
}
