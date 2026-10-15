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

    /// <summary>"Test every ready game…" on the Games page's overview.</summary>
    private void StartSoloBatch() => AP_Atlas.UI.SoloBatchDialog.Open(this, text => Tr(text), SoloHooks());

    /// <summary>"Stop the test server" on the Games page: the running test's server, slot and multiworld go.</summary>
    private void StopSoloTest() => SoloTestRunner.Running?.StopServer(SoloHooks());

    /// <summary>The Games page follows the test's state (its step says when one runs).</summary>
    private void OnSoloTestChanged() => AP_Atlas.UI.Ui.Defer(this, () => { if (_gamesPage != null && _gamesPage.Visible) _gamesPage.Refresh(); });

    private SoloTestHooks SoloHooks() => new SoloTestHooks
    {
        Settings = _appSettings,
        // The release Atlas picks, after looking inside the newest few (asked once per game, on the test's window).
        InstallApworldAsync = game => _gamesPage != null ? _gamesPage.InstallBestAsync(game, SoloQuestionParent(), look: true) : Task.FromResult<(bool, string?)>((false, null)),
        EnsureWorldAsync = EnsureGameWorldAsync,
        PickPackAsync = async (game, say) => _packManagerPanel != null ? await _packManagerPanel.InstallBestPackAsync(game, say) : (false, "the map pack service isn't there", null, null),
        ChoosePackAsync = _ => Task.FromResult<PackChoice?>(null), // the dialog takes this over
        // Without asking (Start covered the downloads): the newest release that fits the game's names.
        InstallPackAsync = (game, repo, say) => _packManagerPanel.InstallFittingReleaseAsync(game, repo, say),
        RecommendedPack = game => AP_Atlas.Core.MapPackManagerControl.GuideRecommendations(game).Select(r => r.Repo).FirstOrDefault(),
        PreferPack = (game, file) => { _appSettings.PreferredPacks[game] = file; DataManager.SaveSettingsSoon(_appSettings); },
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
        Scrubber = () => _crashReports?.NarrowScrubber() ?? new AP_Atlas.Core.Reports.Scrubber(System.Environment.UserName),
        AtlasVersionLine = () => AP_Atlas.Core.AtlasVersion.Display,
        Notice = text => ShowToast(text, AP_Atlas.Core.ThemeColors.Text),
    };

    /// <summary>
    /// Where the test's questions (the GitHub lookup, trusting the apworld's project) open: on the test's window while it
    /// shows, so they stay above it and a click on it brings them forward instead of burying them (a question on the main
    /// window sat behind the test's window and held the rest of Atlas); else on the main window.
    /// </summary>
    /// <summary>
    /// A game's world that ships with Archipelago, unpacked: from the kept zip, else downloaded once after the engine-setup
    /// permission (asked on the test's own window when it shows). Whether it's there.
    /// </summary>
    private async Task<bool> EnsureGameWorldAsync(string game)
    {
        var install = AP_Atlas.Core.EngineSetup.AtlasEngine.Current;
        if (!AP_Atlas.Core.EngineSetup.AtlasEngine.WorldsZipPresent && !AP_Atlas.Core.EngineSetup.AtlasEngine.MayDownloadWorlds)
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            AP_Atlas.UI.PermissionDialog.Ask(SoloQuestionParent(), _appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, null, allowed => answer.TrySetResult(allowed));
            if (!await answer.Task) return false;
        }
        return await AP_Atlas.Core.EngineSetup.AtlasEngine.EnsureWorldsAsync(install, new[] { game }, AP_Atlas.Core.EngineSetup.AtlasEngine.MayDownloadWorlds,
            line => AP_Atlas.Core.Logger.LogInfo("[Atlas Engine] " + line), System.Threading.CancellationToken.None);
    }

    private Node SoloQuestionParent() =>
        GetChildren().OfType<AP_Atlas.UI.SoloTestDialog>().FirstOrDefault(d => d.Visible && !d.IsQueuedForDeletion()) ?? (Node)this;

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
