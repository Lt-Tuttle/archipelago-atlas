#nullable enable
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.EngineSetup;
using Godot;

/// <summary>Add games: the screen (a dialog over the window, or over the dialog that asked) and the installer's hooks into the window (the map pack service's quiet installs, the games' chosen packs).</summary>
public partial class MainTrackerWindow
{
    /// <summary>
    /// Opens Add games: a dialog over the window, or over <paramref name="over"/> (a dialog that asked, such as Add a slot),
    /// with <paramref name="game"/> ticked when given. One at a time: an open one comes forward with the game ticked.
    /// </summary>
    internal void OpenAddGames(string? game = null, Node? over = null) =>
        AP_Atlas.UI.AddGamesDialog.Open(over != null && IsInstanceValid(over) && over.IsInsideTree() ? over : this, AddGamesPanelHooks(), game);

    /// <summary>What the Add games screen needs from the window (the dialog's now; the setup window's page holds the same screen).</summary>
    internal AP_Atlas.UI.AddGamesPanel.Hooks AddGamesPanelHooks() => new AP_Atlas.UI.AddGamesPanel.Hooks
    {
        Tr = text => Tr(text),
        Settings = _appSettings,
        Source = () => _gamesPage != null && IsInstanceValid(_gamesPage) ? _gamesPage.ReadForAdding() : AP_Atlas.UI.AddGamesSource.Empty,
        RunAsync = AddGamesAsync,
        OpenEngineSetup = OpenEngineSetup,
        FindPack = FindMapPack,
        InstallPack = (game, repo) => AP_Atlas.Core.Async.Fire(_packManagerPanel.InstallPackFromRepoAsync(game, repo, confirm: true), $"installing {repo}'s pack for {game}"),
        StartPack = game => StartPack(game, null),
        FreeBytes = () => AtlasEngine.FreeBytes(DataManager.GetDataDirectory()),
        // What Atlas fixed by itself in a pack the batch installed: each fix with Undo, and Undo all.
        ReviewFixes = ReviewFixes,
        BatchEnded = _ =>
        {
            _homePage?.Refresh();
            if (_gamesPage != null && IsInstanceValid(_gamesPage) && _gamesPage.IsVisibleInTree()) _gamesPage.Refresh();
        },
    };

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
