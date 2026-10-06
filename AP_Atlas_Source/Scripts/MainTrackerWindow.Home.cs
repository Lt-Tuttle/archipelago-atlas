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
