using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AP_Atlas.Core.PopTracker;

namespace AP_Atlas.Core.Games;

/// <summary>One game's line of the confirm: its name, what installing it gets ("logic + map"), its download, and the details for its tooltip.</summary>
public sealed record AddConfirmGame(string Game, string Gets, string Size, string Tooltip);

/// <summary>
/// The one confirm (<see cref="AddGamesWords.Confirm"/>) in few words (the owner's "don't be wordy", round 13): its title with
/// the download ("Install 3 games · about 69 MB", what it takes on this PC in its tooltip), a line per game, "From N projects"
/// (each project and what it's for, and what installing trusts, in its tooltip), the GitHub lookups it allows (null when none
/// are asked), and why it can't start when there isn't room (null otherwise).
/// </summary>
public sealed record AddGamesConfirmWords(string Title, string TitleTooltip, IReadOnlyList<AddConfirmGame> Games, string Projects, string ProjectsTooltip, string? GitHub, string? Room)
{
    /// <summary>Every line, tooltips included, one per line (tests and the log).</summary>
    public string AllText => string.Join("\n", new[] { Title, TitleTooltip }
        .Concat(Games.Select(g => g.Game + " · " + g.Gets + " · " + g.Size + "\n" + g.Tooltip))
        .Append(Projects).Append(ProjectsTooltip).Append(GitHub ?? "").Append(Room ?? "").Where(s => s.Length > 0));
}

/// <summary>How an item of a game's summary line reads: done, there already, done but worth a look, failed, or not done.</summary>
public enum AddChipState { Ok, Already, Warn, Bad, Skipped }

/// <summary>One item of a game's summary line: its state (its icon), its few words with one number ("logic 1.2.0", "map 1129/1150", "35 fixes"), and the details for its tooltip.</summary>
public sealed record AddChip(AddChipState State, string Text, string? Tooltip);

/// <summary>
/// The Install games screen's words, few per element (round 13, "don't be wordy"): English templates through the caller's
/// translation, numbers filled in first and names last. Sizes, the totals line, the one confirm, and the summary's heading and
/// its items per game, every detail in a tooltip. Nothing here reads a file or the network.
/// </summary>
public static class AddGamesWords
{
    /// <summary>The summary's closing tip, once a map came in (its tooltip is the map's own tip, MapTrackerControl.FixTipText).</summary>
    public const string Tip = "A pin or tile wrong on the map? Right-click it to fix it.";

    /// <summary>
    /// The share of the game's checks a pack must pin to fit well; under it, the summary flags the map with the way to the
    /// version it fits (round 13: Dark Souls II's 0.5.5 against a pack measured on 0.6.0-alpha.2 pinned almost none).
    /// </summary>
    public const double FitsWell = 0.75;

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

    /// <summary>What downloads come to, with how many don't list their size ("about 48 MB + 2 unlisted"); "" for nothing.</summary>
    public static string Download(long bytes, int unknown, Func<string, string> tr)
    {
        string size = Megabytes(bytes, tr);
        if (unknown <= 0) return size;
        if (size.Length == 0) return unknown == 1 ? tr("size not listed") : tr("{0} sizes not listed").Replace("{0}", N(unknown));
        return tr("{0} + {1} unlisted").Replace("{1}", N(unknown)).Replace("{0}", size);
    }

    /// <summary>The games a plan adds something to (a ready game ticked adds nothing).</summary>
    public static int Adding(AddPlan plan) => plan.Games.Count(g => g.Items.Count > 0);

    /// <summary>The line under the list: "2 games · about 48 MB to download · about 70 MB on this PC".</summary>
    public static string Totals(AddPlan plan, Func<string, string> tr)
    {
        int games = Adding(plan);
        if (games == 0) return tr("Tick the games to install.");
        string count = games == 1 ? tr("1 game") : tr("{0} games").Replace("{0}", N(games));
        string down = Download(plan.DownloadBytes, plan.UnknownSizes, tr), disk = Megabytes(plan.DiskBytes, tr);
        return count + " · " + (down.Length > 0 ? tr("{0} to download").Replace("{0}", down) : tr("nothing to download"))
            + " · " + tr("{0} on this PC").Replace("{0}", disk.Length > 0 ? disk : tr("next to nothing"));
    }

    /// <summary>
    /// Why a game gets the apworld version it gets (its row's and its confirm line's tooltip): the version its map pack was
    /// measured on, or its newest full release (a pre-release only when there's none); null when no apworld comes from the list.
    /// </summary>
    public static string? VersionWhy(GamePlan game, Func<string, string> tr) =>
        game.Items.FirstOrDefault(i => i.Kind == AddItemKind.Apworld && !string.IsNullOrEmpty(i.Version)) is not { } item ? null
            : (item.FitsPack ? tr("Logic {0}: the version its map pack was measured on.")
                : AddGamesPlan.LooksPrerelease(item.Version) ? tr("Logic {0}: its newest release (a pre-release; there's no full one).")
                : tr("Logic {0}: its newest full release.")).Replace("{0}", item.Version!);

    /// <summary>
    /// The one confirm: the title with what it downloads, a line per game (what it gets and its download; where each part comes
    /// from in its tooltip), "From N projects" (apworld projects, Archipelago's release, PyPI's packages, map pack projects, the
    /// project a setup guide may name; and what pressing it trusts: Atlas remembers the new projects), one line for GitHub's
    /// lookups when they aren't allowed yet, and, when the add leaves less than the engine's floor free, why it can't start.
    /// </summary>
    public static AddGamesConfirmWords Confirm(AddPlan plan, bool gitHubAllowed, long? freeBytes, long floorBytes, Func<string, string> tr)
    {
        int games = Adding(plan);
        string down = Download(plan.DownloadBytes, plan.UnknownSizes, tr), disk = Megabytes(plan.DiskBytes, tr);
        string title = (games == 1 ? tr("Install 1 game") : tr("Install {0} games").Replace("{0}", N(games))) + " · " + (down.Length > 0 ? down : tr("nothing to download"));
        string titleTip = tr("{0} to download, {1} on this PC.").Replace("{1}", disk.Length > 0 ? disk : tr("next to nothing")).Replace("{0}", down.Length > 0 ? down : tr("nothing"));
        string release = plan.ArchipelagoReleaseBytes > 0 ? Megabytes(plan.ArchipelagoReleaseBytes, tr) : tr("size not listed");
        var lines = new List<AddConfirmGame>();
        foreach (var g in plan.Games.Where(g => g.Items.Count > 0))
        {
            bool world = g.Items.Any(i => i.Kind == AddItemKind.World);
            string size = Download(g.DownloadBytes, g.UnknownSizes, tr);
            // Archipelago's release is named on its game's line, so the lines add up to the title (its size is in the tooltip).
            if (world && plan.NeedsArchipelagoRelease) size = size.Length == 0 ? tr("in Archipelago's release") : tr("{0} + Archipelago's release").Replace("{0}", size);
            else if (size.Length == 0) size = "–";
            var tip = new List<string> { tr(AddGamesPlan.Explain(g)) };
            if (VersionWhy(g, tr) is { } why) tip.Add(why);
            foreach (var i in g.Items)
                tip.Add(i.Kind switch
                {
                    AddItemKind.Apworld => (i.Live ? tr("Logic from {0}: its newest release is read on GitHub first.") : tr("Logic from {0}.")).Replace("{0}", i.Project ?? ""),
                    AddItemKind.World => plan.NeedsArchipelagoRelease
                        ? tr("Logic from Archipelago's release ({0}, once for every game that ships with it; checked by its SHA-256).").Replace("{0}", release)
                        : tr("Logic from Archipelago's release, which Atlas has already."),
                    _ => (i.Live ? tr("Map pack from {0}, the project its setup guide names: its newest release is read on GitHub.")
                        : i.Reason == PackReason.Guide ? tr("Map pack from {0}, the one its setup guide names.") : tr("Map pack from {0}.")).Replace("{0}", i.Project ?? "")
                });
            if (g.GuideLookupReserved) tip.Add(tr("Its setup guide's map pack project is read on GitHub once its logic is in."));
            var packages = g.Items.SelectMany(i => i.Folders).Where(f => f.Requirements).Select(f => f.Folder).ToList();
            if (packages.Count > 0) tip.Add(tr("Packages from PyPI for {0}; their size isn't listed.").Replace("{0}", string.Join(", ", packages)));
            lines.Add(new AddConfirmGame(g.Game, tr(AddGamesPlan.Words(g)), size, string.Join("\n", tip)));
        }
        static string Games(IEnumerable<string> names) => string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
        var sources = new List<string>();
        foreach (var project in plan.Items.Where(i => i.Kind == AddItemKind.Apworld && i.Project != null).GroupBy(i => i.Project!, StringComparer.OrdinalIgnoreCase))
            sources.Add(tr("{0}: logic · {1}").Replace("{1}", Games(project.Select(i => i.Game))).Replace("{0}", project.Key));
        var worlds = plan.Items.Where(i => i.Kind == AddItemKind.World).Select(i => i.Game).ToList();
        if (worlds.Count > 0) sources.Add(tr("Archipelago's release: logic · {0}").Replace("{0}", Games(worlds)));
        if (plan.WorldsWithPackages.Count > 0) sources.Add(tr("PyPI: packages · {0}").Replace("{0}", string.Join(", ", plan.WorldsWithPackages)));
        foreach (var project in plan.Items.Where(i => i.Kind == AddItemKind.Pack && i.Project != null).GroupBy(i => i.Project!, StringComparer.OrdinalIgnoreCase))
            sources.Add(tr("{0}: map · {1}").Replace("{1}", Games(project.Select(i => i.Game))).Replace("{0}", project.Key));
        var guided = plan.Games.Where(g => g.GuideLookupReserved).Select(g => g.Game).ToList();
        foreach (string game in guided) sources.Add(tr("The project {0}'s setup guide names: map").Replace("{0}", game));
        string projects = sources.Count == 1 ? tr("From 1 project") : tr("From {0} projects").Replace("{0}", N(sources.Count));
        // What pressing it trusts, said where the projects are (Atlas remembers the new apworld projects; Settings takes one back).
        bool apworlds = plan.ApworldProjects.Count > 0, packs = plan.PackProjects.Count > 0 || guided.Count > 0;
        string? runs = apworlds && packs ? tr("Apworlds and map packs run code.") : apworlds ? tr("Apworlds run code.") : packs ? tr("Map packs run scripts.") : null;
        int fresh = plan.ProjectsToTrust.Count; // only apworld projects are remembered, so fresh > 0 implies runs != null
        string trust = runs == null
            ? (plan.WorldsWithPackages.Count > 0 ? tr("All from Archipelago's release (checked by its SHA-256) and PyPI.") : tr("All from Archipelago's release, checked by its SHA-256."))
            : runs + " " + (fresh == 0 ? tr("Install only from projects you trust.")
                : (fresh == 1 ? tr("Installing trusts the new one, {0}; Settings → Privacy & permissions takes it back.")
                    : tr("Installing trusts the new ones, {0}; Settings → Privacy & permissions takes one back.")).Replace("{0}", string.Join(", ", plan.ProjectsToTrust)));
        // GitHub's lookups are asked here, visibly, when they aren't allowed yet: the one line that explains a choice.
        string? gitHub = plan.NeedsGitHubLookups && !gitHubAllowed
            ? (plan.GitHubRequests == 1 ? tr("Also allows 1 GitHub lookup until Atlas closes.") : tr("Also allows up to {0} GitHub lookups until Atlas closes.").Replace("{0}", N(plan.GitHubRequests)))
            : null;
        string? room = freeBytes is long free && !plan.FitsIn(free, floorBytes)
            ? tr("Not enough room: these games need {0}, and Atlas keeps {1} free.").Replace("{1}", Megabytes(floorBytes, tr)).Replace("{0}", disk.Length > 0 ? disk : down)
            : null;
        return new AddGamesConfirmWords(title, titleTip, lines, projects, string.Join("\n", sources) + "\n\n" + trust, gitHub, room);
    }

    /// <summary>The games of a batch installed: nothing failed or was stopped, and something was added.</summary>
    public static int Installed(AddGamesOutcome outcome) =>
        outcome.Games.Count(g => g.Ok && (g.Logic.Outcome == AddStepOutcome.Done || g.Map.Outcome == AddStepOutcome.Done));

    /// <summary>The summary's heading: "3 games installed", "2 of 3 games installed", or "Stopped · 1 of 3 games installed".</summary>
    public static string Heading(AddGamesOutcome outcome, Func<string, string> tr)
    {
        int all = outcome.Games.Count, done = Installed(outcome);
        string count = done == all
            ? (all == 1 ? tr("1 game installed") : tr("{0} games installed").Replace("{0}", N(all)))
            : tr("{0} of {1} games installed").Replace("{0}", N(done)).Replace("{1}", N(all));
        return outcome.Cancelled ? tr("Stopped") + " · " + count : count;
    }

    /// <summary>The heading's tooltip: what stopping kept (null when it wasn't stopped).</summary>
    public static string? HeadingTooltip(AddGamesOutcome outcome, Func<string, string> tr) =>
        outcome.Cancelled ? tr("Nothing half-done was kept; what was done stays.") : null;

    /// <summary>A step's reason as a tooltip's sentence ("Its project no longer offers that file.").</summary>
    private static string Reason(AddStep step, Func<string, string> tr)
    {
        string why = string.IsNullOrWhiteSpace(step.Detail) ? tr("the log says why") : step.Detail.Trim().TrimEnd('.');
        return char.ToUpperInvariant(why[0]) + why[1..] + ".";
    }

    /// <summary>A game's logic: "logic 1.2.0" (an official game's is Archipelago's version), the version's why in the tooltip.</summary>
    public static AddChip LogicChip(AddGameResult game, Func<string, string> tr)
    {
        var logic = game.Logic;
        switch (logic.Outcome)
        {
            case AddStepOutcome.NotNeeded: return new(AddChipState.Already, tr("logic"), tr("Already in the Atlas Engine."));
            case AddStepOutcome.Done: return LogicDone(game, tr);
            case AddStepOutcome.Failed: return new(AddChipState.Bad, tr("logic"), Reason(logic, tr));
            case AddStepOutcome.Unavailable: return new(AddChipState.Skipped, tr("logic"), Reason(logic, tr));
            case AddStepOutcome.Cancelled: return new(AddChipState.Skipped, tr("logic"), tr("Stopped before it was added."));
            default: return new(AddChipState.Skipped, tr("logic"), tr("Not done (the log says why)."));
        }
    }

    private static AddChip LogicDone(AddGameResult game, Func<string, string> tr)
    {
        string? version = string.IsNullOrWhiteSpace(game.Logic.Detail) || game.Logic.Detail == "?" ? null : game.Logic.Detail.Trim();
        string? tip = version == null ? null
            : game.LogicShips ? tr("Ships with Archipelago {0}.").Replace("{0}", version)
            : game.LogicFitsPack ? tr("Version {0}: the one its map pack was measured on.").Replace("{0}", version)
            : null;
        return new(AddChipState.Ok, version == null ? tr("logic") : tr("logic {0}").Replace("{0}", version), tip);
    }

    /// <summary>
    /// A game's map: "map 1129/1150" (the game's checks with a pin, as the Pack Doctor counted them), a warning under
    /// <see cref="FitsWell"/>, where the pack came from in its tooltip; "no map" when none was installed. An
    /// <paramref name="official"/> game's logic is Archipelago's world (no version of it to switch to): its tooltip says so.
    /// </summary>
    public static AddChip MapChip(AddGameResult game, PackToLook? look, Func<string, string> tr, bool official = false)
    {
        switch (game.Map.Outcome)
        {
            case AddStepOutcome.NotNeeded: return new(AddChipState.Already, tr("map"), tr("A map pack is installed already."));
            case AddStepOutcome.Done: return MapDone(game, look, tr, official || game.LogicShips);
            case AddStepOutcome.Failed: return new(AddChipState.Bad, tr("map"), Reason(game.Map, tr));
            case AddStepOutcome.Unavailable: return new(AddChipState.Skipped, tr("no map"), Reason(game.Map, tr));
            case AddStepOutcome.Cancelled: return new(AddChipState.Skipped, tr("map"), tr("Stopped before it was installed."));
            default: return new(AddChipState.Skipped, tr("map"), tr("Not done (the log says why)."));
        }
    }

    private static AddChip MapDone(AddGameResult game, PackToLook? look, Func<string, string> tr, bool official)
    {
        string from = string.IsNullOrWhiteSpace(game.Map.Detail) ? "" : game.Map.Detail.Trim().TrimEnd('.');
        var tip = new List<string>();
        if (from.Length > 0) tip.Add((game.PackFromGuide ? tr("From {0}, the one its setup guide names.") : tr("From {0}.")).Replace("{0}", from));
        else if (game.PackFromGuide) tip.Add(tr("The one its setup guide names."));
        if (look is not { Total: > 0 }) return new(AddChipState.Ok, tr("map"), tip.Count > 0 ? string.Join("\n", tip) : null);
        tip.Add(tr("{0} of the game's {1} checks have a pin.").Replace("{0}", N(look.Placed)).Replace("{1}", N(look.Total)));
        bool poor = PoorFit(look);
        if (poor) tip.Add(official ? tr("Under three quarters: the pack likely targets another Archipelago version.")
            : tr("Under three quarters: the pack likely fits another version of the game. Find the version that fits… measures it against the game's releases."));
        return new(poor ? AddChipState.Warn : AddChipState.Ok, tr("map {0}/{1}").Replace("{0}", N(look.Placed)).Replace("{1}", N(look.Total)), string.Join("\n", tip));
    }

    /// <summary>What Atlas fixed in the game's pack by itself ("35 fixes"), or null when nothing.</summary>
    public static AddChip? FixedChip(PackToLook? look, Func<string, string> tr) => look is not { Fixed: > 0 } ? null
        : new(AddChipState.Ok, look.Fixed == 1 ? tr("1 fix") : tr("{0} fixes").Replace("{0}", N(look.Fixed)),
            tr("Atlas fixed {0} in {1} by itself; Review lists each, with Undo.").Replace("{0}", AutoFixWords.Things(look.Fixed, tr)).Replace("{1}", look.PackName));

    /// <summary>Under <see cref="FitsWell"/> of the game's checks have a pin: the pack likely fits another version of the game.</summary>
    public static bool PoorFit(PackToLook look) => look.Total > 0 && look.Placed < FitsWell * look.Total;

    /// <summary>The tooltip of "Install github.com/…": why the guide's own pack wasn't the one installed (null when it was, or there's none).</summary>
    public static string? GuideTip(AddGameResult game, Func<string, string> tr) =>
        game.GuideRepo == null ? null : game.PackNote switch
        {
            AddPackNote.GuideNotListedNoLookup => tr("Its setup guide names github.com/{0}, which Atlas's game list doesn't have; Atlas shows what would be downloaded first.").Replace("{0}", game.GuideRepo),
            AddPackNote.GuideNotListedBudgetSpent => tr("Its setup guide names github.com/{0}; this install's share of GitHub's hourly limit ran out before Atlas could try it. Atlas shows what would be downloaded first.").Replace("{0}", game.GuideRepo),
            _ => null
        };
}
