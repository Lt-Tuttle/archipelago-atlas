using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AP_Atlas.Core.Games;

/// <summary>The one confirm's lines (<see cref="AddGamesWords.Confirm"/>).</summary>
public sealed record AddGamesConfirmWords(string Title, string Lead, IReadOnlyList<string> Sources, string? GitHub, string Trust, string? Room);

/// <summary>
/// The Add games screen's words, from a plan or a batch's outcome: English templates through the caller's translation,
/// numbers filled in after. A size ("about 48 MB, besides 2 files whose size isn't listed"), the totals line, the one
/// confirm's lines (what downloads, from which projects, what pressing it trusts, GitHub's release lists, the room left),
/// and the summary's headline and lines per game. Nothing here reads a file or the network.
/// </summary>
public static class AddGamesWords
{
    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>A size in megabytes ("about 3.4 MB", "about 48 MB"; "under 0.1 MB" below that); "" for none.</summary>
    public static string Megabytes(long bytes, Func<string, string> tr)
    {
        if (bytes <= 0) return "";
        double mb = bytes / 1048576.0;
        if (mb < 0.05) return tr("under 0.1 MB");
        string number = mb >= 10 ? Math.Round(mb).ToString("0", CultureInfo.InvariantCulture) : Math.Round(mb, 1).ToString("0.#", CultureInfo.InvariantCulture);
        return tr("about {0} MB").Replace("{0}", number);
    }

    /// <summary>What downloads come to, saying how many don't list their size; "" for nothing.</summary>
    public static string Download(long bytes, int unknown, Func<string, string> tr)
    {
        string size = Megabytes(bytes, tr);
        if (unknown <= 0) return size;
        if (size.Length == 0) return unknown == 1 ? tr("size not listed") : tr("{0} sizes not listed").Replace("{0}", N(unknown));
        return (unknown == 1 ? tr("{0}, besides 1 file whose size isn't listed") : tr("{0}, besides {1} files whose size isn't listed").Replace("{1}", N(unknown))).Replace("{0}", size);
    }

    /// <summary>The games a plan adds something to (a ready game ticked adds nothing).</summary>
    public static int Adding(AddPlan plan) => plan.Games.Count(g => g.Items.Count > 0);

    /// <summary>The line under the list: how many games, what they download and what they take on this PC.</summary>
    public static string Totals(AddPlan plan, Func<string, string> tr)
    {
        int games = Adding(plan);
        if (games == 0) return tr("Tick the games to add: the totals show here.");
        string count = games == 1 ? tr("1 game") : tr("{0} games").Replace("{0}", N(games));
        string down = Download(plan.DownloadBytes, plan.UnknownSizes, tr), disk = Megabytes(plan.DiskBytes, tr);
        return tr("{0}: {1} to download, {2} on this PC.")
            .Replace("{1}", down.Length > 0 ? down : tr("nothing")).Replace("{2}", disk.Length > 0 ? disk : tr("next to nothing")).Replace("{0}", count);
    }

    /// <summary>
    /// The one confirm: the title, what downloads and what it takes, every project the downloads come from (apworlds,
    /// Archipelago's release, the packages worlds declare, map packs), GitHub's release lists when one is read and GitHub
    /// lookups aren't allowed yet, what pressing it trusts, and, when the add leaves less than the engine's floor free, why it can't start.
    /// </summary>
    public static AddGamesConfirmWords Confirm(AddPlan plan, bool gitHubAllowed, long? freeBytes, long floorBytes, Func<string, string> tr)
    {
        int games = Adding(plan);
        string title = games == 1 ? tr("Add 1 game") : tr("Add {0} games").Replace("{0}", N(games));
        string down = Download(plan.DownloadBytes, plan.UnknownSizes, tr), disk = Megabytes(plan.DiskBytes, tr);
        string lead = down.Length == 0
            ? tr("Nothing needs downloading; Atlas uses {0} on this PC.").Replace("{0}", disk.Length > 0 ? disk : tr("next to nothing"))
            : tr("Atlas downloads {0} and uses {1} on this PC.").Replace("{1}", disk.Length > 0 ? disk : down).Replace("{0}", down);
        var sources = new List<string>();
        if (plan.ApworldProjects.Count > 0) sources.Add(tr("The games' logic (apworlds) from {0}.").Replace("{0}", string.Join(", ", plan.ApworldProjects)));
        if (plan.NeedsArchipelagoRelease)
            sources.Add(tr("Archipelago's own release ({0}, once) for the games that ship with it.")
                .Replace("{0}", plan.ArchipelagoReleaseBytes > 0 ? Megabytes(plan.ArchipelagoReleaseBytes, tr) : tr("its size isn't listed")));
        else if (plan.Items.Any(i => i.Kind == AddItemKind.World))
            sources.Add(tr("The games that ship with Archipelago are unpacked from its release, which Atlas has already."));
        if (plan.WorldsWithPackages.Count > 0)
            sources.Add(tr("Some of their worlds ({0}) need packages of their own, installed from PyPI; their size isn't listed.").Replace("{0}", string.Join(", ", plan.WorldsWithPackages)));
        if (plan.PackProjects.Count > 0) sources.Add(tr("Map packs from {0}.").Replace("{0}", string.Join(", ", plan.PackProjects)));
        string? gitHub = null;
        var live = plan.Items.Where(i => i.Live && i.Project != null).Select(i => i.Project!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (live.Count > 0 && !gitHubAllowed)
            gitHub = tr("First Atlas reads the releases of {0} on GitHub ({1}); pressing Download and install allows that until Atlas closes.")
                .Replace("{1}", plan.GitHubRequests == 1 ? tr("at most 1 request") : tr("at most {0} requests").Replace("{0}", N(plan.GitHubRequests)))
                .Replace("{0}", string.Join(", ", live));
        bool apworlds = plan.ApworldProjects.Count > 0, packs = plan.PackProjects.Count > 0;
        string trust = apworlds && packs ? tr("Apworlds are programs that run inside Atlas's logic engine, and map packs' scripts run inside Atlas's sandbox: only continue if you trust these projects.")
            : apworlds ? tr("Apworlds are programs that run inside Atlas's logic engine: only continue if you trust these projects.")
            : packs ? tr("Map packs' scripts run inside Atlas's sandbox: only continue if you trust these projects.")
            : tr("Everything comes from Archipelago's own release, checked against its published SHA-256.");
        if (plan.ProjectsToTrust.Count > 0)
            trust += " " + tr("Pressing Download and install trusts {0}; Settings → Privacy & permissions lists the projects you trust and takes one back.").Replace("{0}", string.Join(", ", plan.ProjectsToTrust));
        string? room = freeBytes is long free && !plan.FitsIn(free, floorBytes)
            ? tr("There isn't room: these games take {0} on this PC, and Atlas keeps {1} free for its engine. Free some space or tick fewer games.")
                .Replace("{1}", Megabytes(floorBytes, tr)).Replace("{0}", disk.Length > 0 ? disk : down)
            : null;
        return new AddGamesConfirmWords(title, lead, sources, gitHub, trust, room);
    }

    /// <summary>The summary's first line: what was added of what was wanted (or that it stopped), and how many need a look.</summary>
    public static string Headline(AddGamesOutcome outcome, Func<string, string> tr)
    {
        if (outcome.Cancelled)
            return tr("Stopped. Before it stopped, Atlas added logic for {0} and map packs for {1}; nothing half-done was kept.")
                .Replace("{0}", N(outcome.LogicAdded)).Replace("{1}", N(outcome.MapsAdded));
        int logic = outcome.Games.Count(g => g.Logic.Outcome != AddStepOutcome.NotNeeded), maps = outcome.Games.Count(g => g.Map.Outcome != AddStepOutcome.NotNeeded);
        string done = logic > 0 && maps > 0 ? tr("Done: logic added for {0} of {1}, map packs for {2} of {3}.").Replace("{0}", N(outcome.LogicAdded)).Replace("{1}", N(logic)).Replace("{2}", N(outcome.MapsAdded)).Replace("{3}", N(maps))
            : logic > 0 ? tr("Done: logic added for {0} of {1}.").Replace("{0}", N(outcome.LogicAdded)).Replace("{1}", N(logic))
            : maps > 0 ? tr("Done: map packs added for {0} of {1}.").Replace("{0}", N(outcome.MapsAdded)).Replace("{1}", N(maps))
            : tr("Done: nothing needed adding.");
        int failures = outcome.Failures.Count();
        return failures == 0 ? done : done + " " + (failures == 1 ? tr("One needs a look below.") : tr("{0} need a look below.").Replace("{0}", N(failures)));
    }

    private static string Reason(AddStep step, Func<string, string> tr) =>
        string.IsNullOrWhiteSpace(step.Detail) ? tr("the log says why") : step.Detail.Trim().TrimEnd('.');

    /// <summary>A game's logic in the summary ("Logic: version 1.2.0 added.", "Logic: not added: …").</summary>
    public static string LogicLine(AddGameResult game, Func<string, string> tr) => game.Logic.Outcome switch
    {
        AddStepOutcome.NotNeeded => tr("Logic: already in the Atlas Engine."),
        AddStepOutcome.Done => string.IsNullOrWhiteSpace(game.Logic.Detail) || game.Logic.Detail == "?" ? tr("Logic: added.") : tr("Logic: version {0} added.").Replace("{0}", game.Logic.Detail),
        AddStepOutcome.Failed => tr("Logic: not added: {0}.").Replace("{0}", Reason(game.Logic, tr)),
        AddStepOutcome.Unavailable => tr("Logic: {0}.").Replace("{0}", Reason(game.Logic, tr)),
        AddStepOutcome.Cancelled => tr("Logic: stopped before it was added."),
        _ => tr("Logic: not done (the log says why).")
    };

    /// <summary>A game's map pack in the summary ("Map: github.com/x v1, the one its setup guide names.").</summary>
    public static string MapLine(AddGameResult game, Func<string, string> tr) => game.Map.Outcome switch
    {
        AddStepOutcome.NotNeeded => tr("Map: a map pack is installed already."),
        AddStepOutcome.Done => string.IsNullOrWhiteSpace(game.Map.Detail)
            ? (game.PackFromGuide ? tr("Map: installed, the one its setup guide names.") : tr("Map: installed."))
            : (game.PackFromGuide ? tr("Map: {0}, the one its setup guide names.") : tr("Map: {0}.")).Replace("{0}", game.Map.Detail.Trim().TrimEnd('.')),
        AddStepOutcome.Failed => tr("Map: not installed: {0}.").Replace("{0}", Reason(game.Map, tr)),
        AddStepOutcome.Unavailable => tr("Map: {0}.").Replace("{0}", Reason(game.Map, tr)),
        AddStepOutcome.Cancelled => tr("Map: stopped before it was installed."),
        _ => tr("Map: not done (the log says why).")
    };

    /// <summary>Why the guide's own pack wasn't the one taken, when Atlas's list lacks it (null otherwise).</summary>
    public static string? GuideLine(AddGameResult game, Func<string, string> tr) =>
        game.GuideRepo == null ? null : game.PackNote switch
        {
            AddPackNote.GuideNotListedNoLookup => tr("Its setup guide names github.com/{0}, which Atlas's game list doesn't have.").Replace("{0}", game.GuideRepo),
            AddPackNote.GuideNotListedBudgetSpent => tr("Its setup guide names github.com/{0}; this add's share of GitHub's hourly limit was spent before Atlas could try it.").Replace("{0}", game.GuideRepo),
            _ => null
        };
}
