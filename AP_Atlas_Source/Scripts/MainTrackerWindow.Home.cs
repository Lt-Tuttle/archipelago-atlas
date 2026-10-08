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
            SetUpEngine = OpenEngineSetup,
            AddMultiworld = OnAddProfilePressed,
            QuickAddMultiworld = () => AP_Atlas.UI.QuickSetup.NewMultiworld(this, QuickHooks()),
            QuickConnect = () => AP_Atlas.UI.QuickSetup.ConnectSlots(this, QuickHooks()),
            QuickFindPack = () => AP_Atlas.UI.QuickSetup.FindPack(this, QuickHooks()),
            QuickLinkCheese = () => AP_Atlas.UI.QuickSetup.LinkCheese(this, QuickHooks()),
            QuickLinkSphere = () => AP_Atlas.UI.QuickSetup.LinkSphere(this, QuickHooks()),
            OpenSphereSettings = () => ShowSphereTab(AP_Atlas.UI.SphereTrackerTab.SettingsView),
            OpenEngineWindow = () => OpenEngineWindow(),
            IsStepSkipped = id => _appSettings.SkippedHomeSteps.Contains(id),
            SetStepSkipped = (id, skipped) =>
            {
                if (skipped) { if (!_appSettings.SkippedHomeSteps.Contains(id)) _appSettings.SkippedHomeSteps.Add(id); }
                else _appSettings.SkippedHomeSteps.Remove(id);
                DataManager.SaveSettingsSoon(_appSettings);
            },
            ShowTool = host.ShowTool,
            Connect = ConnectEverySlot,
            OpenCheeseSettings = OpenCheeseSettings,
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

    /// <summary>What Home's quick-setup dialogs need from the window.</summary>
    private AP_Atlas.UI.QuickSetup.Hooks QuickHooks() => new AP_Atlas.UI.QuickSetup.Hooks
    {
        Tr = text => Tr(text),
        Profiles = () => _profiles,
        IsSlotLive = IsSlotLive,
        ConnectSlot = (profile, slot) => OnConnectSlotPressed(slot, profile),
        ConnectAll = ConnectEverySlot,
        PlayedGames = () => _profiles.SelectMany(p => p.SavedStats.Values.Select(s => s.GameName))
            .Concat(ActiveSlotNodes().OfType<SlotTrackerControl>().Select(slot => slot.Game))
            .Where(game => !string.IsNullOrWhiteSpace(game)).Distinct(System.StringComparer.OrdinalIgnoreCase).ToList(),
        FindPack = FindMapPack,
        PickYaml = PickYamlFor,
        KeepYaml = KeepYamlForSlots,
        Create = draft => CreateProfileFromDraft(draft),
        LinkCheese = LinkCheeseFromEditor,
        LinkSphere = LinkSphereFromEditor,
    };

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

    /// <summary>The engine changed (any thread): Home's checklist ticks the engine step as soon as the setup is done.</summary>
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
