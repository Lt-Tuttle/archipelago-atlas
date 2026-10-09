#nullable enable
using System.Linq;
using System.Threading.Tasks;
using AP_Atlas.Core.EngineSetup;
using Godot;

/// <summary>The solo test's hooks into the window: the Games page's installs, the pack manager, the connect path and the profiles.</summary>
public partial class MainTrackerWindow
{
    /// <summary>"Test this game…" on a game's page.</summary>
    private void StartSoloTest(string game) => AP_Atlas.UI.SoloTestDialog.Open(this, game, text => Tr(text), SoloHooks());

    private SoloTestHooks SoloHooks() => new SoloTestHooks
    {
        Settings = _appSettings,
        InstallApworldAsync = game => _gamesPage != null ? _gamesPage.InstallNewestAsync(game, this) : Task.FromResult(false),
        ChoosePackAsync = _ => Task.FromResult<PackChoice?>(null), // the dialog takes this over
        InstallPackAsync = (game, repo) => _packManagerPanel.InstallPackFromRepoAsync(game, repo, confirm: false),
        AddProfile = profile =>
        {
            _profiles.Add(profile);
            DataManager.SaveProfiles(_profiles);
            RefreshProfileList();
            UpdateSidebar();
        },
        RemoveProfile = profile =>
        {
            if (_profiles.Contains(profile)) DeleteProfile(profile);
        },
        ConnectSlotAsync = ConnectForSoloTestAsync,
        Scrubber = () => _crashReports?.Scrubber() ?? new AP_Atlas.Core.Reports.Scrubber(System.Environment.UserName),
        AtlasVersionLine = () => AP_Atlas.Core.AtlasVersion.Display,
    };

    /// <summary>Connects a slot the way its row's button does and waits for its view (null when it didn't connect in a minute).</summary>
    internal async Task<SlotTrackerControl?> ConnectForSoloTestAsync(MultiworldProfile profile, string slot)
    {
        await OnConnectSlotPressedAsync(slot, profile);
        var until = AP_Atlas.Core.Deadline.In(SoloTestRunner.ConnectWait);
        while (!until.Passed)
        {
            var view = ActiveSlotNodes().OfType<SlotTrackerControl>().FirstOrDefault(s => GodotObject.IsInstanceValid(s) && s.ProfileId == profile.Id && s.SlotName == slot);
            if (view != null) return view;
            await Task.Delay(100);
        }
        return null;
    }
}
