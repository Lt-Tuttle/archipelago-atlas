using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.Games;

/// <summary>Where a game stands, as the card on top of its page says it.</summary>
public enum GameStanding
{
    /// <summary>The Atlas Engine isn't set up: an add needs it first.</summary>
    EngineFirst,
    /// <summary>Adding it installs something: its logic (a community game's apworld, an official game's world) or its map pack.</summary>
    Add,
    /// <summary>Its logic and a map pack are here.</summary>
    Ready,
    /// <summary>Its logic is here; no map pack is installed, and Atlas's list knows none.</summary>
    ReadyNoMap,
    /// <summary>Atlas doesn't know where its logic comes from (no project in the list or added by the user).</summary>
    Unknown
}

/// <summary>A game's standing from its part of an add, and the release its card's Update offers. Nothing here reads a file or the network.</summary>
public static class GameStandings
{
    /// <summary>
    /// The engine first; else something to add; else its row: ready, ready without a map, or nothing known of its logic.
    /// (A one-game plan never waits for GitHub's hourly limit: it spends at most 4 of <see cref="AddGamesPlan.GitHubBudget"/> requests.)
    /// </summary>
    public static GameStanding Of(AddPlan plan, GamePlan game) =>
        !plan.EngineReady ? GameStanding.EngineFirst
        : game.Items.Count > 0 ? GameStanding.Add
        : game.Row switch
        {
            AddRow.Ready => GameStanding.Ready,
            AddRow.NoMapKnown => GameStanding.ReadyNoMap,
            _ => GameStanding.Unknown
        };

    /// <summary>
    /// The release to offer as an update: the newest full release from the installed release's own project (a fork or a
    /// re-upload GitHub's name search found is never offered here; the versions table lists them), with a download (a label
    /// that looks like a pre-release isn't one, as the add plan reads them), newer by its number than the installed one, that <paramref name="ruledOut"/>
    /// doesn't rule out (what Atlas found looking inside it). Null when none is, or when the installed file isn't a listed
    /// release (there's nothing to compare it with).
    /// </summary>
    public static ApworldChoice? Update(IEnumerable<ApworldChoice> choices, Func<ApworldChoice, bool> ruledOut)
    {
        var list = choices.ToList();
        var mine = list.FirstOrDefault(c => c.Installed);
        if (mine == null) return null;
        return list.Where(c => c.Url != null && !c.Installed && !c.Prerelease && !AddGamesPlan.LooksPrerelease(c.Version)
                && string.Equals(c.Source, mine.Source, StringComparison.OrdinalIgnoreCase)
                && ApworldChoices.VersionComparer.Instance.Compare(c.Version, mine.Version) > 0 && !ruledOut(c))
            .OrderByDescending(c => c.Version, ApworldChoices.VersionComparer.Instance)
            .FirstOrDefault();
    }
}
