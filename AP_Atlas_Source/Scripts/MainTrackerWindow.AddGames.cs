#nullable enable
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.EngineSetup;

/// <summary>Add games: the installer's hooks into the window (the map pack service's quiet installs, the games' chosen packs). Commit 2 adds the screen here.</summary>
public partial class MainTrackerWindow
{
    /// <summary>Runs an Add games plan once its confirm is pressed (see <see cref="GameAdder"/>). Main thread.</summary>
    internal Task<GameAdderRun> AddGamesAsync(AP_Atlas.Core.Games.AddPlan plan, AddGamesConsent consent, System.Action<AP_Atlas.Core.Games.AddGamesStatus>? progress, CancellationToken ct) =>
        new GameAdder(AddGamesHooks()).RunAsync(plan, consent, progress, ct);

    private GameAdderHooks AddGamesHooks() => new GameAdderHooks
    {
        Settings = _appSettings,
        Tr = text => Tr(text),
        InstallIndexPackAsync = async (game, pack, progress, ct) => _packManagerPanel != null && IsInstanceValid(_packManagerPanel)
            ? await _packManagerPanel.InstallIndexPackAsync(game, pack, progress!, ct)
            : (null, Tr("the map pack service isn't there")),
        InstallProjectPackAsync = async (game, repo, say) =>
        {
            if (_packManagerPanel == null || !IsInstanceValid(_packManagerPanel)) return (false, Tr("the map pack service isn't there"), null);
            var (ok, detail, file) = await _packManagerPanel.InstallFittingReleaseAsync(game, repo, say!, quiet: true, most: 1);
            return (ok, detail, file);
        },
        // As a pick does: the game's pack, saved, and the slots told again (the install told them before it was the game's).
        PreferPack = (game, file) =>
        {
            _appSettings.PreferredPacks[game] = file;
            DataManager.SaveSettingsSoon(_appSettings);
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
        },
    };
}
