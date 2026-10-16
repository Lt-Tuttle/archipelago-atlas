#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.EngineSetup;
using AP_Atlas.Core.Games;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The add plan's facts as the Games page knows them: per game what Atlas's game list, the engine and the packs folder
    /// say, for <see cref="AddGamesPlan.Make"/>, and what the Add games screen reads. Main thread (it reads the page's lists).
    /// Nothing is downloaded or asked; a game whose logic is here (or whose world is in Archipelago's kept release) has its
    /// setup guide read, one look inside.
    /// </summary>
    public sealed partial class GamesPage
    {
        /// <summary>The plan for adding these games (by the names the page lists), with the page's lists read again first.</summary>
        internal AddPlan PlanAdding(IEnumerable<string> games)
        {
            var source = ReadForAdding();
            return AddGamesPlan.Make(games.Select(source.Facts).ToList(), source.Context);
        }

        /// <summary>
        /// What the Add games screen reads as it opens (and again before its confirm): the page's lists read again, the
        /// plan's context, every game the page lists (those of your multiworlds marked: they come first, ticked where they
        /// need something) and a game's facts on request (<see cref="AddFactsFor"/>; the screen reads them a few per frame).
        /// </summary>
        internal AddGamesSource ReadForAdding()
        {
            _packs = ReadPacks();
            _games = CollectGames();
            var install = AtlasEngine.Current;
            return new AddGamesSource(AddContextNow(install), _games.Select(g => new AddGamesSource.Entry(g.Game, g.Played)).ToList(), game => AddFactsFor(game, install));
        }

        /// <summary>What an add depends on besides the games, as Atlas stands now.</summary>
        private AddContext AddContextNow(EngineInstall? install) => new()
        {
            WorldsZipPresent = AtlasEngine.WorldsZipPresent,
            WorldsZipBytes = AtlasEngine.WorldsZipBytes,
            TrustedProjects = _hooks.Settings.ApprovedApworldSources ?? new List<string>(),
            EngineReady = install != null && install.CanLaunch
        };

        /// <summary>One game's part of an add from the page's lists as they are (its page's card; nothing is read again).</summary>
        private (AddPlan Plan, GamePlan Game) PlanFor(string game, EngineInstall? install)
        {
            var plan = AddGamesPlan.Make(new[] { AddFactsFor(game, install) }, AddContextNow(install));
            return (plan, plan.Games[0]);
        }


        /// <summary>What Atlas knows of one game for the plan (see the class).</summary>
        internal AddGameFacts AddFactsFor(string game, EngineInstall? install)
        {
            var entry = _games.FirstOrDefault(g => string.Equals(g.Game, game, StringComparison.OrdinalIgnoreCase));
            var source = ApworldSources.Find(game);
            bool portable = install != null && install.Mode == EngineMode.Portable && install.CanLaunch;
            // An installed apworld takes the place of Archipelago's own world (WorldsToUnpack's rule): then it's a community game.
            bool hasApworld = install != null && ApworldSources.InstalledCopies(install, game).Count > 0;
            bool official = !hasApworld && (AtlasEngine.Worlds.FolderFor(game) != null || entry?.Section == Section.Official);
            var folders = portable ? AtlasEngine.WorldsToUnpack(install!, game) : new List<string>();
            bool logic = official ? install != null && install.CanLaunch && folders.Count == 0 : entry?.InEngine == true;
            var versions = official || source == null || source.Official
                ? new List<IndexVersionFacts>()
                : source.Versions.Select(v => new IndexVersionFacts(v.Version ?? "", v.Url ?? "", v.Sha256, v.Size)).ToList();
            string? extra = _hooks.Settings.ExtraApworldRepos != null && _hooks.Settings.ExtraApworldRepos.TryGetValue(game, out var added) ? added.FirstOrDefault() : null;
            bool guideRead = logic || (official && portable && AtlasEngine.WorldsZipPresent);
            return new AddGameFacts
            {
                Game = entry?.Game ?? game,
                Official = official,
                LogicInstalled = logic,
                Versions = versions,
                LiveProject = !official && versions.Count == 0 ? (source?.Repo ?? extra) : null,
                WorldsToUnpack = folders.Select(f => AtlasEngine.Worlds.Worlds.TryGetValue(f, out var w) ? new WorldFolderFacts(f, w.Bytes, w.Requirements) : new WorldFolderFacts(f, 0, false)).ToList(),
                GuideRepos = guideRead ? MapPackManagerControl.GuideRecommendations(game).Select(r => r.Repo).ToList() : new List<string>(),
                GuideRead = guideRead,
                Packs = ApworldSources.PackFactsFor(game),
                PackInstalled = PacksFor(game).Count > 0
            };
        }
    }
}
