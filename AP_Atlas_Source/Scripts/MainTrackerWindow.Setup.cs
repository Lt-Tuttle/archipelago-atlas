#nullable enable
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>The setup window (Set up Atlas): its hooks into the window, the steps Home's line counts, and what the window does for it (connecting its slots, keeping their Logic banners quiet while it adds their games).</summary>
public partial class MainTrackerWindow
{
    private AP_Atlas.UI.SetupSteps? _setupSteps;

    /// <summary>The setup steps Home's line and the setup window share.</summary>
    private AP_Atlas.UI.SetupSteps SetupSteps => _setupSteps ??= new AP_Atlas.UI.SetupSteps(new AP_Atlas.UI.SetupSteps.Hooks
    {
        EngineReady = () => AP_Atlas.Core.EngineSetup.AtlasEngine.Current.CanLaunch,
        Profiles = () => _profiles,
        SlotGamesReady = SlotGamesReady,
        PagesDone = () => _appSettings.SetupPagesDone,
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

    private ulong _slotGamesReadyFrame = ulong.MaxValue;
    private bool _slotGamesReady;

    /// <summary>
    /// Whether every game the user's slots play is ready: its logic in the engine, its map pack installed or none known
    /// (Add games' "ready" rows). False while no slot's game is known. Read at most once a frame (Home and the setup
    /// window's list ask several times; each read goes over the packs, the games and their guides).
    /// </summary>
    private bool SlotGamesReady()
    {
        ulong frame = Godot.Engine.GetProcessFrames();
        if (frame == _slotGamesReadyFrame) return _slotGamesReady;
        _slotGamesReadyFrame = frame;
        var games = _profiles.SelectMany(p => p.Slots.Where(s => !AP_Atlas.UI.SetupSteps.IsPlaceholderSlot(s))
                .Select(s => p.SavedStats.TryGetValue(s, out var stats) ? stats.GameName : null))
            .Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g!).Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
        _slotGamesReady = games.Count > 0 && _gamesPage != null && IsInstanceValid(_gamesPage)
            && _gamesPage.PlanAdding(games).Games.All(g => g.Row is AP_Atlas.Core.Games.AddRow.Ready or AP_Atlas.Core.Games.AddRow.NoMapKnown);
        return _slotGamesReady;
    }

    /// <summary>Help → Set up Atlas…, Home's welcome card and Resume: the setup window, at the first step left (resume) or from its Welcome page.</summary>
    internal AP_Atlas.UI.SetupWizard OpenSetupWizard(bool resume) => AP_Atlas.UI.SetupWizard.Open(this, SetupHooks(), resume);

    private AP_Atlas.UI.SetupWizard.Hooks SetupHooks() => new AP_Atlas.UI.SetupWizard.Hooks
    {
        Tr = text => Tr(text),
        Settings = _appSettings,
        Steps = SetupSteps,
        EngineReady = () => AP_Atlas.Core.EngineSetup.AtlasEngine.Current.CanLaunch,
        ShowEnginePage = lines => ShowEnginePage(lines),
        Profiles = () => _profiles,
        SelectedProfile = () => _selectedProfile,
        SaveMultiworld = SaveSetupMultiworld,
        IsSlotLive = IsSlotLive,
        ConnectSlotAsync = ConnectForSetupAsync,
        QuietLogicFor = SetSetupQuiet,
        AddGames = AddGamesPanelHooks,
        OpenPackDoctor = (zip, tab) => OpenPackDoctor(zip, tab),
        ShowSettings = section => ShowSettings(section),
    };

    /// <summary>
    /// The setup window's connect: as the Multiworlds page's Connect, waiting for the slot's view (its game is then the
    /// room's, in the multiworld's saved stats) or its failure (said on the status bar and as a card); whether it connected.
    /// </summary>
    private async Task<bool> ConnectForSetupAsync(MultiworldProfile profile, string slot)
    {
        if (IsSlotLive(profile.Id, slot)) return true;
        await OnConnectSlotPressedAsync(slot, profile);
        // ShowConnectResult runs a moment later (deferred): until then the slot is still connecting.
        var wait = AP_Atlas.Core.Deadline.In(System.TimeSpan.FromSeconds(5));
        while (_connectingSlots.Contains(SlotKey(profile.Id, slot)) && !wait.Passed && !_shuttingDown)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _homePage?.Refresh();
        return IsSlotLive(profile.Id, slot);
    }

    /// <summary>The multiworld whose slots' Logic banners stay quiet about a missing game: the setup window adds it next (null: none).</summary>
    private string? _setupQuietProfile;

    private void SetSetupQuiet(string? profileId)
    {
        if (_setupQuietProfile == profileId) return;
        _setupQuietProfile = profileId;
        if (_shuttingDown) return; // the slots are going with the window
        foreach (var slot in ActiveSlotNodes().OfType<SlotTrackerControl>().Where(GodotObject.IsInstanceValid)) slot.RefreshLogicNotice();
    }

    /// <summary>
    /// The setup window's multiworld: a new one, or the existing one the draft names, with its name, room link, server and
    /// password and its slots (added to an existing one's; Atlas's placeholder slot gives way), less the slots the window
    /// added earlier and the user took back (disconnected first, as the Multiworlds page removes a slot); saved, listed and
    /// selected on the Multiworlds page. Each slot's game comes from the room when it connects.
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
        foreach (string gone in draft.Drop.Where(profile.Slots.Contains).ToList())
        {
            DisconnectSlot(profile.Id, gone);
            profile.ActiveSlots.Remove(gone);
            profile.Slots.Remove(gone);
            profile.SavedStats.Remove(gone);
        }
        if (profile.Slots.All(AP_Atlas.UI.SetupSteps.IsPlaceholderSlot)) profile.Slots.Clear();
        foreach (string slot in draft.Slots.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct())
            if (!profile.Slots.Contains(slot)) profile.Slots.Add(slot);
        if (isNew) _profiles.Add(profile);
        DataManager.SaveProfiles(_profiles);
        RefreshProfileList();
        SelectProfile(profile);
        UpdateSidebar();
        _homePage?.Refresh();
        return profile;
    }
}
