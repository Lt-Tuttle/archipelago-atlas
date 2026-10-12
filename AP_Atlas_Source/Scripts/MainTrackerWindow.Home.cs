using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>Home's hooks into the window: what it reads, and what its buttons do.</summary>
public partial class MainTrackerWindow
{
    private AP_Atlas.UI.HomePage? _homePage;
    private System.Collections.Generic.IReadOnlyList<string>? _whatsNewLines; // read from the changelog built in, once

    private AP_Atlas.UI.HomePage BuildHomePage()
    {
        var host = (AP_Atlas.UI.IPropertiesHost)this;
        var page = new AP_Atlas.UI.HomePage(text => Tr(text), new AP_Atlas.UI.HomePage.Hooks
        {
            EngineReady = () => AP_Atlas.Core.EngineSetup.AtlasEngine.Current.CanLaunch,
            Profiles = () => _profiles,
            AnyPackInstalled = AnyMapPackInstalled,
            IsSlotLive = IsSlotLive,
            ContentFontSize = () => _appSettings.ContentFontSize,
            Steps = SetupSteps,
            OpenSetup = resume => OpenSetupWizard(resume),
            WelcomeDismissed = () => _appSettings.WelcomeDismissed,
            DismissWelcome = () =>
            {
                _appSettings.WelcomeDismissed = true;
                DataManager.SaveSettingsSoon(_appSettings);
            },
            AddMultiworld = OnAddProfilePressed,
            QuickAddMultiworld = () => AP_Atlas.UI.QuickSetup.NewMultiworld(this, QuickHooks()),
            AddFromRoomLink = AddProfileFromRoomLink,
            OpenProfile = profile =>
            {
                host.ShowTool(AP_Atlas.UI.Tool.Connections);
                SelectProfileGuarded(profile);
            },
            ShowTool = host.ShowTool,
            Connect = ConnectEverySlot,
            ShowAbout = ShowAbout,
            WhatsNew = () => _whatsNewLines ??= AP_Atlas.UI.HelpWindow.WhatsNewLines(),
            ShowWhatsNew = () => OpenHelp(AP_Atlas.UI.HelpWindow.WhatsNew),
            OpenWeb = url =>
            {
                if (!AP_Atlas.Core.ExternalLinks.OpenWeb(url)) ShowToast(Tr("Couldn't open the link."), AP_Atlas.Core.ThemeColors.Error);
            },
        });
        page.Visible = false;
        _homePage = page;
        return page;
    }

    /// <summary>What Home's New multiworld dialog needs from the window.</summary>
    private AP_Atlas.UI.QuickSetup.Hooks QuickHooks() => new AP_Atlas.UI.QuickSetup.Hooks
    {
        Tr = text => Tr(text),
        PickYaml = PickYamlFor,
        KeepYaml = KeepYamlForSlots,
        Create = draft => CreateProfileFromDraft(draft),
    };

    /// <summary>Paste a room link…: a new multiworld on the Multiworlds page, its room link box ready to paste into.</summary>
    private void AddProfileFromRoomLink()
    {
        OnAddProfilePressed();
        AP_Atlas.UI.Ui.Defer(this, () =>
        {
            if (_selectedProfile != null && _roomLinkInput != null) _roomLinkInput.GrabFocus();
        });
    }

    /// <summary>
    /// A multiworld from Home's New multiworld dialog: saved, selected on the Multiworlds page (Home stays where it is),
    /// each slot's game noted and its YAML tied to it.
    /// </summary>
    internal MultiworldProfile CreateProfileFromDraft(AP_Atlas.UI.NewMultiworldDraft draft)
    {
        var profile = new MultiworldProfile { Name = draft.Name.Trim(), ServerUrl = (draft.Server ?? "").Trim(), Password = draft.Password ?? "" };
        foreach (string slot in draft.Slots.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct())
        {
            profile.Slots.Add(slot);
            if (draft.GameBySlot.TryGetValue(slot, out var game) && !string.IsNullOrWhiteSpace(game)) profile.SavedStats[slot] = new SlotStats { GameName = game };
            if (draft.YamlBySlot.TryGetValue(slot, out var yaml) && !string.IsNullOrEmpty(yaml)) _appSettings.SlotYamlPaths[AP_Atlas.Core.Annotations.SlotKey(profile.Id, slot)] = yaml;
        }
        _profiles.Add(profile);
        DataManager.SaveProfiles(_profiles);
        DataManager.SaveSettings(_appSettings);
        RefreshProfileList();
        SelectProfile(profile);
        UpdateSidebar();
        _homePage?.Refresh();
        return profile;
    }

    /// <summary>The engine changed (any thread): Home's setup line follows it as soon as the setup is done.</summary>
    private void OnEngineChangedForHome() => AP_Atlas.UI.Ui.Defer(this, () =>
    {
        if (_currentTool == AP_Atlas.UI.Tool.Home) _homePage?.Refresh();
    });

    /// <summary>
    /// The engine's parts changed (any thread): the slots whose logic runs on it restart by themselves; a card says so,
    /// naming the parts.
    /// </summary>
    private void OnEnginePartsChanged(System.Collections.Generic.IReadOnlyList<AP_Atlas.Core.EngineSetup.AtlasEngine.EngineChange> changes) => AP_Atlas.UI.Ui.Defer(this, () =>
    {
        var running = ActiveSlotNodes().OfType<SlotTrackerControl>().Where(slot => slot.EngineRunning || slot.EngineBooting).Select(slot => slot.SlotName).Distinct().ToList();
        if (running.Count == 0) return;
        var parts = changes.Select(DescribeChange).Distinct().ToList();
        ShowToast(Tr("Restarting logic for {0} with {1}…").Replace("{0}", string.Join(", ", running)).Replace("{1}", string.Join(", ", parts)), AP_Atlas.Core.ThemeColors.Info);
    });

    private string DescribeChange(AP_Atlas.Core.EngineSetup.AtlasEngine.EngineChange change) => change.Kind switch
    {
        "apworld" => change.Game != null ? Tr("{0}'s apworld").Replace("{0}", change.Game) : Tr("an apworld"),
        "runtime" => Tr("the new Python"),
        "archipelago" => Tr("the new Archipelago"),
        "packages" => Tr("the new packages"),
        "world-packages" => change.Game != null ? Tr("{0}'s packages").Replace("{0}", change.Game) : Tr("the games' packages"),
        "tracker" => Tr("the new Universal Tracker"),
        "world" => change.Game != null ? Tr("{0}'s world").Replace("{0}", change.Game) : Tr("a game's world"),
        _ => change.Kind
    };

    /// <summary>Whether a map pack is installed: a zip in Atlas's own packs folder, as the Map Packs page lists them.</summary>
    private static bool AnyMapPackInstalled()
    {
        string packs = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.GetPacksDirectory();
        return System.IO.Directory.Exists(packs) && System.IO.Directory.EnumerateFiles(packs, "*.zip").Any();
    }

    /// <summary>Whether a multiworld's slot is connected (or connecting) right now: it has a live panel.</summary>
    private bool IsSlotLive(string profileId, string slotName) =>
        ActiveSlotNodes().OfType<SlotTrackerControl>().Any(slot => slot.ProfileId == profileId && slot.SlotName == slotName);

    /// <summary>Home's one click: connects each of a multiworld's slots that isn't connected, one after another.</summary>
    private void ConnectEverySlot(MultiworldProfile profile) => AP_Atlas.Core.Async.Fire(ConnectEverySlotAsync(profile), $"connecting {profile.Name}");

    private async Task ConnectEverySlotAsync(MultiworldProfile profile)
    {
        foreach (string slot in profile.Slots.ToList())
        {
            if (_shuttingDown) return;
            if (IsSlotLive(profile.Id, slot)) continue;
            await OnConnectSlotPressedAsync(slot, profile);
        }
        _homePage?.Refresh();
    }
}
