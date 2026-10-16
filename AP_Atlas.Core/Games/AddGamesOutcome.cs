using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.Games;

public enum AddStepOutcome { NotNeeded, Pending, Done, Failed, Unavailable, Cancelled }

/// <summary>Why the guide's own pack wasn't taken: Atlas's list lacks it and GitHub may not be asked, or the batch's allowance is spent.</summary>
public enum AddPackNote { None, GuideNotListedNoLookup, GuideNotListedBudgetSpent }

/// <summary>One part of adding a game (its logic, its map): how it went, and in plain words what was installed or why not.</summary>
public sealed record AddStep(AddStepOutcome Outcome, string? Detail = null)
{
    public static readonly AddStep NotNeeded = new(AddStepOutcome.NotNeeded);
    public static readonly AddStep Pending = new(AddStepOutcome.Pending);
    public static readonly AddStep Cancelled = new(AddStepOutcome.Cancelled);
    public static AddStep Done(string? detail) => new(AddStepOutcome.Done, detail);
    public static AddStep Failed(string reason) => new(AddStepOutcome.Failed, reason);
    public static AddStep Unavailable(string reason) => new(AddStepOutcome.Unavailable, reason);
}

/// <summary>What happened to one game of a batch: its logic (apworld or Archipelago's world) and its map pack.</summary>
public sealed class AddGameResult
{
    public AddGameResult(string game, bool logic, bool map)
    {
        Game = game;
        Logic = logic ? AddStep.Pending : AddStep.NotNeeded;
        Map = map ? AddStep.Pending : AddStep.NotNeeded;
    }

    public string Game { get; }
    public AddStep Logic { get; set; }
    public AddStep Map { get; set; }
    public string? PackFile { get; set; }
    public string? PackRepo { get; set; }
    /// <summary>The pack installed is the one the game's own setup guide names.</summary>
    public bool PackFromGuide { get; set; }
    /// <summary>The first tracker project the game's setup guide names, when it names one.</summary>
    public string? GuideRepo { get; set; }
    /// <summary>Why the guide's pack wasn't the one installed, for the summary's "Find it on GitHub…".</summary>
    public AddPackNote PackNote { get; set; }
    /// <summary>Its logic ships with Archipelago (an official game's world): its version is Archipelago's.</summary>
    public bool LogicShips { get; set; }
    /// <summary>Its apworld's version is the one its map pack was measured on (Atlas's list said so), not just the newest.</summary>
    public bool LogicFitsPack { get; set; }
    /// <summary>Nothing failed, was stopped or was left unfinished.</summary>
    public bool Ok => Fine(Logic) && Fine(Map);
    private static bool Fine(AddStep step) => step.Outcome is not (AddStepOutcome.Failed or AddStepOutcome.Cancelled or AddStepOutcome.Pending);
}

/// <summary>
/// A pack a batch installed, once the Pack Doctor settled on it: the fixes Atlas made by itself in it, and how many of the
/// game's checks have a pin (Placed of Total; 0 of 0 when the game's names weren't known). Round 13 dropped "left": it counted
/// one row per unlinked pin section (Dark Souls II: 1593) and wasn't what the map's Notices show.
/// </summary>
public sealed record PackToLook(string Game, string PackKey, string PackName, string File, int Fixed, int Placed, int Total);

/// <summary>What an Add games batch did, for the summary card: a row per game, the packs to look at, and how GitHub was used.</summary>
public sealed class AddGamesOutcome
{
    private readonly List<AddGameResult> _games = new();

    public AddGamesOutcome(IEnumerable<(string Game, bool Logic, bool Map)> games)
    {
        foreach (var (game, logic, map) in games)
            if (Find(game) == null) _games.Add(new AddGameResult(game, logic, map));
    }

    public IReadOnlyList<AddGameResult> Games => _games;

    /// <summary>A game of the batch (by name, any case).</summary>
    public AddGameResult For(string game) => Find(game) ?? throw new ArgumentException(game + " isn't in this batch.", nameof(game));

    private AddGameResult? Find(string game) => _games.FirstOrDefault(g => string.Equals(g.Game, game, StringComparison.OrdinalIgnoreCase));

    public List<PackToLook> PacksToLook { get; } = new();
    public bool Cancelled { get; set; }
    /// <summary>The engine's health check failed for a reason of its own (the tracker, a missing API), when it did.</summary>
    public string? EngineProblem { get; set; }
    /// <summary>The GitHub API requests the batch allowed itself (at most; anonymous GitHub allows about 60 an hour).</summary>
    public int GitHubRequests { get; set; }
    public int LogicAdded => _games.Count(g => g.Logic.Outcome == AddStepOutcome.Done);
    public int MapsAdded => _games.Count(g => g.Map.Outcome == AddStepOutcome.Done);
    public IEnumerable<AddGameResult> Failures => _games.Where(g => g.Logic.Outcome == AddStepOutcome.Failed || g.Map.Outcome == AddStepOutcome.Failed);

    /// <summary>The batch ended: every part still pending takes this word (stopped, or what went wrong).</summary>
    public void Finish(AddStep unfinished)
    {
        foreach (var g in _games)
        {
            if (g.Logic.Outcome == AddStepOutcome.Pending) g.Logic = unfinished;
            if (g.Map.Outcome == AddStepOutcome.Pending) g.Map = unfinished;
        }
    }
}
