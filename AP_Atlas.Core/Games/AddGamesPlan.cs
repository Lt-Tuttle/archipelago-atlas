using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core.Games;

/// <summary>A version of a game's apworld in Atlas's game list: its label, its download, its published SHA-256 and its size in bytes (0 when the list doesn't say).</summary>
public sealed record IndexVersionFacts(string Version, string Url, string? Sha256, long Size);

/// <summary>
/// A map pack in Atlas's game list as the add plan weighs it: its project, release and file (or "zipball": the project's
/// current files), its SHA-256 and size as the pack lab downloaded it (0 when unknown), its fit and the lab's notes.
/// </summary>
public sealed record IndexPackFacts(string Repo, string Release, string Asset, string? Sha256, long Size, int? Fit, IReadOnlyList<string> Notes)
{
    /// <summary>The project's current files (it has no release with a pack): fetched through GitHub's API, never the same bytes twice for certain.</summary>
    public bool CurrentFiles => string.Equals(Asset, AddGamesPlan.Zipball, StringComparison.OrdinalIgnoreCase);

    /// <summary>One download fetches it: a release's zip, or the project's current files (not, say, a pack the lab took from elsewhere).</summary>
    public bool Downloadable => (CurrentFiles || Asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        && IndexPackDownloads.For(Repo, Release, Asset, Sha256, IndexPackDownloads.GitHubSite, IndexPackDownloads.ApiSite) != null;
}

/// <summary>A world folder of Archipelago's release a game needs unpacked: its name, its size unpacked, and whether it declares packages of its own.</summary>
public sealed record WorldFolderFacts(string Folder, long Bytes, bool Requirements);

/// <summary>What Atlas knows of one game ticked to add; the window reads it from the game list, the engine and the packs folder.</summary>
public sealed record AddGameFacts
{
    public required string Game { get; init; }

    /// <summary>The game ships with Archipelago: its logic is a world unpacked from Archipelago's release, not a community apworld.</summary>
    public bool Official { get; init; }

    /// <summary>The engine has the game's logic now (its apworld loaded in the last check, or its world unpacked).</summary>
    public bool LogicInstalled { get; init; }

    /// <summary>The game list's versions of its apworld (any order).</summary>
    public IReadOnlyList<IndexVersionFacts> Versions { get; init; } = [];

    /// <summary>A project ("owner/name") whose releases must be read first because the list has no version of it (one the user added); null for none.</summary>
    public string? LiveProject { get; init; }

    /// <summary>An official game's world folders still to unpack (the game's and those it imports).</summary>
    public IReadOnlyList<WorldFolderFacts> WorldsToUnpack { get; init; } = [];

    /// <summary>The tracker projects ("owner/name") the game's own setup guide links to, in its order.</summary>
    public IReadOnlyList<string> GuideRepos { get; init; } = [];

    /// <summary>Whether the guide could be read now (its logic or Archipelago's release is here); when not, the pack is picked again once the logic is in.</summary>
    public bool GuideRead { get; init; }

    /// <summary>The game list's map packs for the game.</summary>
    public IReadOnlyList<IndexPackFacts> Packs { get; init; } = [];

    /// <summary>A map pack for the game is installed already.</summary>
    public bool PackInstalled { get; init; }
}

/// <summary>What the whole add depends on besides the games.</summary>
public sealed record AddContext
{
    /// <summary>Archipelago's release zip is kept (worlds unpack without a download).</summary>
    public bool WorldsZipPresent { get; init; }

    /// <summary>The release zip's size in bytes (the worlds index's), 0 when unknown.</summary>
    public long WorldsZipBytes { get; init; }

    /// <summary>The apworld sources the user trusts already ("github.com/owner/name", as Settings keeps them).</summary>
    public IReadOnlyCollection<string> TrustedProjects { get; init; } = [];

    /// <summary>The Atlas Engine can take apworlds (it's set up).</summary>
    public bool EngineReady { get; init; } = true;
}

public enum AddItemKind
{
    /// <summary>A community game's apworld, downloaded, kept in the game's folder and copied into the engine.</summary>
    Apworld,
    /// <summary>An official game's world folders, unpacked from Archipelago's release.</summary>
    World,
    /// <summary>A map pack, downloaded into the packs folder.</summary>
    Pack
}

/// <summary>Why a pack was picked.</summary>
public enum PackReason
{
    /// <summary>The game's setup guide names its project, and Atlas's list has its pack.</summary>
    Guide,
    /// <summary>The game's setup guide names its project, which Atlas's list doesn't have: its releases are read first.</summary>
    GuideProject,
    /// <summary>The best fit in Atlas's list.</summary>
    BestFit
}

/// <summary>One thing an add installs.</summary>
public sealed record AddItem
{
    public required AddItemKind Kind { get; init; }
    public required string Game { get; init; }

    /// <summary>Where it comes from ("github.com/owner/name", or a host); null for a world (Archipelago's release, named once for the plan).</summary>
    public string? Project { get; init; }

    /// <summary>An apworld's version label; null when the project's releases are read first.</summary>
    public string? Version { get; init; }

    /// <summary>The download; null for a world, a project's current files (fetched through GitHub's API) and anything read from GitHub first.</summary>
    public string? Url { get; init; }

    /// <summary>The SHA-256 the download is checked against; null when none is published, or for a project's current files.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Bytes downloaded; 0 when not listed (a world downloads nothing of its own).</summary>
    public long Size { get; init; }

    /// <summary>Bytes on this PC afterwards: an apworld twice (the kept release and the engine's copy), a pack once, a world unpacked.</summary>
    public long Disk { get; init; }

    /// <summary>GitHub's release list is read first (the size is known only then).</summary>
    public bool Live { get; init; }

    /// <summary>Requests against GitHub's anonymous hourly limit (at most).</summary>
    public int GitHubRequests { get; init; }

    /// <summary>A pack's project, release and file ("zipball": its current files).</summary>
    public string? Repo { get; init; }
    public string? Release { get; init; }
    public string? Asset { get; init; }
    public bool CurrentFiles { get; init; }
    public PackReason? Reason { get; init; }

    /// <summary>A world's folders to unpack.</summary>
    public IReadOnlyList<WorldFolderFacts> Folders { get; init; } = [];

    /// <summary>A download whose size isn't listed.</summary>
    public bool SizeUnknown => Kind != AddItemKind.World && Size <= 0;
}

/// <summary>What adding a game gets, in plain words (<see cref="AddGamesPlan.Words"/>).</summary>
public enum AddRow
{
    Ready,
    LogicAndMap,
    Logic,
    LogicNoMapKnown,
    LogicMapFromGuide,
    Map,
    MapLogicShips,
    NoMapKnown,
    MapOnly,
    NothingKnown,
    MapWaits,
    Waits
}

/// <summary>One game's part of the add.</summary>
public sealed record GamePlan(string Game, AddRow Row, IReadOnlyList<AddItem> Items)
{
    /// <summary>
    /// The pack is picked again after the logic is in (the game's guide can be read then): its pack from Atlas's list, else, when
    /// <see cref="GuideLookupReserved"/>, the project the guide names, read from GitHub.
    /// </summary>
    public bool RepickPackAfterLogic { get; init; }

    /// <summary>
    /// GitHub's requests for the project the game's guide may name are kept in the plan (Atlas's list has no pack for it, and the
    /// guide can be read only once the logic is in), so the one confirm covers them (round 12: FireRed's tracker isn't in the list).
    /// </summary>
    public bool GuideLookupReserved { get; init; }

    /// <summary>No map pack for the game is installed: adding it looks for one (the plan's pick, or one picked again after the logic is in).</summary>
    public bool WantsPack { get; init; }

    public long DownloadBytes => Items.Sum(i => i.Size);
    public long DiskBytes => Items.Sum(i => i.Disk);
    public int UnknownSizes => Items.Count(i => i.SizeUnknown);
    public bool LogicComing => Items.Any(i => i.Kind is AddItemKind.Apworld or AddItemKind.World);
}

/// <summary>The whole add: each game's part, Archipelago's release once, and the totals the one confirm states.</summary>
public sealed record AddPlan
{
    public required IReadOnlyList<GamePlan> Games { get; init; }

    /// <summary>Archipelago's release is downloaded first (an official game's world, the zip not kept): the engine-setup permission covers it.</summary>
    public bool NeedsArchipelagoRelease { get; init; }

    /// <summary>The release's size in bytes when downloaded (0 when unknown or not needed).</summary>
    public long ArchipelagoReleaseBytes { get; init; }

    /// <summary>Everything downloaded, in bytes, of what lists its size.</summary>
    public long DownloadBytes { get; init; }

    /// <summary>What it all takes on this PC, in bytes: the downloads, the apworlds' engine copies and the worlds unpacked (each folder once).</summary>
    public long DiskBytes { get; init; }

    /// <summary>Downloads whose size isn't listed.</summary>
    public int UnknownSizes { get; init; }

    /// <summary>Requests against GitHub's anonymous hourly limit, at most (never over <see cref="AddGamesPlan.GitHubBudget"/>).</summary>
    public int GitHubRequests { get; init; }

    /// <summary>A release list is read from GitHub: the GitHub lookups permission is asked in the same confirm.</summary>
    public bool NeedsGitHubLookups { get; init; }

    /// <summary>Every apworld's source, as the confirm names them.</summary>
    public IReadOnlyList<string> ApworldProjects { get; init; } = [];

    /// <summary>The apworld sources the user doesn't trust yet: pressing the confirm trusts them.</summary>
    public IReadOnlyList<string> ProjectsToTrust { get; init; } = [];

    /// <summary>Every map pack's project, as the confirm names them.</summary>
    public IReadOnlyList<string> PackProjects { get; init; } = [];

    /// <summary>World folders that declare packages of their own (installed from PyPI with the engine's rules; their size isn't listed).</summary>
    public IReadOnlyList<string> WorldsWithPackages { get; init; } = [];

    /// <summary>The engine can't take apworlds yet (set it up first).</summary>
    public bool EngineReady { get; init; } = true;

    public IEnumerable<AddItem> Items => Games.SelectMany(g => g.Items);

    /// <summary>Whether the add leaves at least <paramref name="floorBytes"/> free of <paramref name="freeBytes"/>.</summary>
    public bool FitsIn(long freeBytes, long floorBytes) => freeBytes - DiskBytes >= floorBytes;
}

/// <summary>
/// What adding games installs, worked out before anything is downloaded, from what Atlas knows offline (its game list
/// records every file's size, the worlds index every world's): per game a community game's apworld (the newest release
/// in the list), an official game's world folders, and one map pack (the one the game's guide names when the list has it,
/// else the list's best fit that doesn't wait behind); Archipelago's release once; the sizes, the unknowns, the projects
/// to trust, and GitHub's requests kept under a budget. Nothing here reads a file or the network.
/// </summary>
public static class AddGamesPlan
{
    /// <summary>The asset the game list writes for a project's current files.</summary>
    public const string Zipball = "zipball";

    /// <summary>
    /// The most of GitHub's anonymous hourly limit (about 60) one add may spend: a third, so Atlas's other lookups (the seed's
    /// version, the update check) still have room.
    /// </summary>
    public const int GitHubBudget = 20;

    /// <summary>Reading a project's apworld releases: one request.</summary>
    public const int LiveListingRequests = 1;

    /// <summary>Trying a project's pack: its releases, its newest release when the list has none with a pack, its current files.</summary>
    public const int LivePackRequests = 3;

    /// <summary>A project's current files come through GitHub's API: one request.</summary>
    public const int CurrentFilesRequests = 1;

    private static readonly Regex RepoName = new(@"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$");

    /// <summary>What the user approves for a download: "github.com/owner/name" for a GitHub link, else the host (ApworldSources.SourceKey's rule).</summary>
    public static string SourceKey(string? url)
    {
        var m = Regex.Match(url ?? "", @"^https://github\.com/([^/]+/[^/]+)/", RegexOptions.IgnoreCase);
        if (m.Success) return "github.com/" + m.Groups[1].Value;
        try { return new Uri(url!).Host; }
        catch (Exception ex) when (ex is UriFormatException or ArgumentNullException) { return url ?? ""; }
    }

    /// <summary>Whether the text is a GitHub project's "owner/name".</summary>
    public static bool IsRepo(string? repo) => repo != null && RepoName.IsMatch(repo);

    /// <summary>A link Atlas downloads from: https, or plain http on this PC (a test's own site), as the polite client allows.</summary>
    public static bool IsDownloadable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>
    /// The newest full release in the list by its number, a pre-release only when there is none, from a link Atlas
    /// downloads from (<see cref="IsDownloadable"/>); null for none.
    /// </summary>
    public static IndexVersionFacts? Newest(IEnumerable<IndexVersionFacts> versions) =>
        versions.Where(v => IsDownloadable(v.Url))
            .Select((v, i) => (v, i))
            .OrderBy(x => LooksPrerelease(x.v.Version))
            .ThenByDescending(x => x.v.Version, ApworldChoices.VersionComparer.Instance).ThenBy(x => x.i)
            .Select(x => x.v).FirstOrDefault();

    /// <summary>
    /// A label whose part after its numbers (build metadata after '+' aside) has a letter: "1.6.0-beta", "1.0.0-rc6",
    /// "1.2.1-TEST"; "20260822-1" and "0.1.0+build" are full releases. The list records no pre-release flag, so the label says.
    /// </summary>
    public static bool LooksPrerelease(string? version)
    {
        string label = ApworldChoices.NormalizeLabel(version);
        int plus = label.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) label = label[..plus];
        var m = Regex.Match(label, @"^v?\d+(?:\.\d+)*(.*)$", RegexOptions.IgnoreCase);
        return m.Success && m.Groups[1].Value.Any(char.IsLetter);
    }

    /// <summary>A pack Atlas picks: its project, the list's entry (null: the project's releases are read first) and why.</summary>
    public sealed record PackPick(string Repo, IndexPackFacts? Listed, PackReason Reason)
    {
        public int GitHubRequests => Listed == null ? LivePackRequests : Listed.CurrentFiles ? CurrentFilesRequests : 0;
    }

    /// <summary>
    /// The pack for a game: the first project its setup guide names that the list has a usable pack of; a guide project the
    /// list doesn't have at all is tried from GitHub when the requests allow; else the list's best: those whose notes make
    /// them wait behind last, then the best fit (a project's current files a little behind a release), then the list's order.
    /// A pack the lab measured under half linked isn't taken (<see cref="PackProjectPick.CloseEnough"/>), nor one that
    /// needs more of GitHub than <paramref name="gitHubRequestsLeft"/>; a guide's project is read from GitHub only when
    /// <paramref name="mayReadProjects"/>. Null when nothing fits.
    /// </summary>
    public static PackPick? ChoosePack(IReadOnlyList<IndexPackFacts> packs, IReadOnlyList<string> guideRepos, int gitHubRequestsLeft, bool mayReadProjects = true)
    {
        var usable = packs.Select((p, i) => (p, i))
            .Where(x => x.p.Downloadable && Usable(x.p) && (!x.p.CurrentFiles || gitHubRequestsLeft >= CurrentFilesRequests))
            .OrderBy(x => IndexPackNotes.WaitsBehind(x.p.Notes)).ThenByDescending(x => Score(x.p)).ThenBy(x => x.i)
            .Select(x => x.p).ToList();
        foreach (string repo in guideRepos)
        {
            if (usable.FirstOrDefault(p => SameRepo(p.Repo, repo)) is { } listed) return new PackPick(listed.Repo, listed, PackReason.Guide);
            // The list has the project but its pack didn't fit (or can't be fetched now): not tried again from GitHub.
            if (packs.Any(p => SameRepo(p.Repo, repo))) continue;
            if (mayReadProjects && IsRepo(repo) && gitHubRequestsLeft >= LivePackRequests) return new PackPick(repo, null, PackReason.GuideProject);
        }
        return usable.Count > 0 ? new PackPick(usable[0].Repo, usable[0], PackReason.BestFit) : null;
    }

    /// <summary>Why the pack taken isn't the one the game's guide names (a project Atlas's list lacks), for the summary.</summary>
    public static AddPackNote GuideNote(IReadOnlyList<IndexPackFacts> packs, IReadOnlyList<string> guideRepos, PackPick? pick, bool mayReadProjects)
    {
        if (pick?.Reason is PackReason.Guide or PackReason.GuideProject) return AddPackNote.None;
        bool unlisted = GuideUnlistedRepo(packs, guideRepos) != null;
        return !unlisted ? AddPackNote.None : mayReadProjects ? AddPackNote.GuideNotListedBudgetSpent : AddPackNote.GuideNotListedNoLookup;
    }

    /// <summary>The first project the guide names that Atlas's list lacks (null for none).</summary>
    public static string? GuideUnlistedRepo(IReadOnlyList<IndexPackFacts> packs, IReadOnlyList<string> guideRepos) =>
        guideRepos.FirstOrDefault(r => IsRepo(r) && !packs.Any(p => SameRepo(p.Repo, r)));

    private static bool Usable(IndexPackFacts pack) => pack.Fit == null || pack.Fit >= PackProjectPick.CloseEnough * 100;

    private static double Score(IndexPackFacts pack) => (pack.Fit ?? -1) - (pack.CurrentFiles ? PackProjectPick.CurrentFilesWeight * 100 : 0);

    private static bool SameRepo(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The plan for the ticked games, in their order (a game ticked twice counts once).</summary>
    public static AddPlan Make(IEnumerable<AddGameFacts> games, AddContext context)
    {
        int left = GitHubBudget;
        var plans = new List<GamePlan>();
        var guideCandidates = new List<int>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var facts in games)
        {
            if (string.IsNullOrWhiteSpace(facts.Game) || !seen.Add(facts.Game)) continue;
            var items = new List<AddItem>();
            bool logicKnown = facts.LogicInstalled;
            if (!facts.LogicInstalled)
            {
                if (facts.Official)
                {
                    if (facts.WorldsToUnpack.Count > 0)
                    {
                        long bytes = facts.WorldsToUnpack.Sum(w => w.Bytes);
                        items.Add(new AddItem { Kind = AddItemKind.World, Game = facts.Game, Folders = facts.WorldsToUnpack, Disk = bytes });
                        logicKnown = true;
                    }
                    // Its logic ships with Archipelago: an engine not set up yet can't say which folders, but where it comes from is known.
                    else if (!context.EngineReady) logicKnown = true;
                }
                else if (Newest(facts.Versions) is { } newest)
                {
                    long size = Math.Max(0, newest.Size);
                    items.Add(new AddItem
                    {
                        Kind = AddItemKind.Apworld,
                        Game = facts.Game,
                        Project = SourceKey(newest.Url),
                        Version = ApworldChoices.NormalizeLabel(newest.Version),
                        Url = newest.Url,
                        Sha256 = string.IsNullOrWhiteSpace(newest.Sha256) ? null : newest.Sha256,
                        Size = size,
                        Disk = 2 * size
                    });
                    logicKnown = true;
                }
                else if (IsRepo(facts.LiveProject))
                {
                    if (left < LiveListingRequests)
                    {
                        plans.Add(new GamePlan(facts.Game, AddRow.Waits, []));
                        continue;
                    }
                    items.Add(new AddItem { Kind = AddItemKind.Apworld, Game = facts.Game, Project = "github.com/" + facts.LiveProject, Live = true, GitHubRequests = LiveListingRequests });
                    left -= LiveListingRequests;
                    logicKnown = true;
                }
            }
            bool packWaits = false;
            if (!facts.PackInstalled)
            {
                var pick = ChoosePack(facts.Packs, facts.GuideRepos, left);
                if (pick != null)
                {
                    items.Add(PackItem(facts.Game, pick));
                    left -= pick.GitHubRequests;
                }
                else packWaits = ChoosePack(facts.Packs, facts.GuideRepos, int.MaxValue) != null;
            }
            bool logicComing = items.Any(i => i.Kind is AddItemKind.Apworld or AddItemKind.World);
            bool packComing = items.Any(i => i.Kind == AddItemKind.Pack);
            AddRow row = !logicKnown ? (packComing ? AddRow.MapOnly : packWaits ? AddRow.MapWaits : AddRow.NothingKnown)
                : packWaits ? AddRow.MapWaits
                : logicComing ? (packComing ? (facts.Official ? AddRow.MapLogicShips : AddRow.LogicAndMap) : facts.PackInstalled ? AddRow.Logic : AddRow.LogicNoMapKnown)
                : packComing ? (facts.Official ? AddRow.MapLogicShips : AddRow.Map)
                : facts.PackInstalled ? AddRow.Ready : AddRow.NoMapKnown;
            bool repick = logicComing && !facts.GuideRead && !facts.PackInstalled;
            plans.Add(new GamePlan(facts.Game, row, items) { RepickPackAfterLogic = repick, WantsPack = !facts.PackInstalled });
            if (repick && row == AddRow.LogicNoMapKnown) guideCandidates.Add(plans.Count - 1);
        }
        // The requests for the project a guide may name come last: a listed pack or a release list the plan is sure of never
        // waits behind a guess (round 12: FireRed's tracker isn't in the list; its guide is read once its logic is in).
        foreach (int i in guideCandidates)
        {
            if (left < LivePackRequests) break;
            left -= LivePackRequests;
            plans[i] = plans[i] with { Row = AddRow.LogicMapFromGuide, GuideLookupReserved = true };
        }

        var all = plans.SelectMany(p => p.Items).ToList();
        bool release = !context.WorldsZipPresent && all.Any(i => i.Kind == AddItemKind.World);
        long releaseBytes = release ? Math.Max(0, context.WorldsZipBytes) : 0;
        var folders = all.SelectMany(i => i.Folders).GroupBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        long download = all.Sum(i => i.Size) + releaseBytes;
        long disk = all.Where(i => i.Kind != AddItemKind.World).Sum(i => i.Disk) + folders.Sum(f => f.Bytes) + releaseBytes;
        var trusted = new HashSet<string>(context.TrustedProjects, StringComparer.OrdinalIgnoreCase);
        var apworldProjects = all.Where(i => i.Kind == AddItemKind.Apworld && i.Project != null).Select(i => i.Project!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new AddPlan
        {
            Games = plans,
            NeedsArchipelagoRelease = release,
            ArchipelagoReleaseBytes = releaseBytes,
            DownloadBytes = download,
            DiskBytes = disk,
            UnknownSizes = all.Count(i => i.SizeUnknown) + (release && releaseBytes == 0 ? 1 : 0),
            GitHubRequests = GitHubBudget - left,
            NeedsGitHubLookups = all.Any(i => i.Live) || plans.Any(p => p.GuideLookupReserved),
            ApworldProjects = apworldProjects,
            ProjectsToTrust = apworldProjects.Where(p => !trusted.Contains(p)).ToList(),
            PackProjects = all.Where(i => i.Kind == AddItemKind.Pack).Select(i => "github.com/" + i.Repo).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            WorldsWithPackages = folders.Where(f => f.Requirements).Select(f => f.Folder).ToList(),
            EngineReady = context.EngineReady
        };
    }

    private static AddItem PackItem(string game, PackPick pick)
    {
        var listed = pick.Listed;
        if (listed == null)
            return new AddItem { Kind = AddItemKind.Pack, Game = game, Project = "github.com/" + pick.Repo, Repo = pick.Repo, Live = true, GitHubRequests = LivePackRequests, Reason = pick.Reason };
        long size = Math.Max(0, listed.Size);
        var download = listed.CurrentFiles ? null : IndexPackDownloads.For(listed.Repo, listed.Release, listed.Asset, listed.Sha256, IndexPackDownloads.GitHubSite, IndexPackDownloads.ApiSite);
        return new AddItem
        {
            Kind = AddItemKind.Pack,
            Game = game,
            Project = "github.com/" + listed.Repo,
            Repo = listed.Repo,
            Release = listed.Release,
            Asset = listed.Asset,
            CurrentFiles = listed.CurrentFiles,
            Url = download?.Url,
            Sha256 = listed.CurrentFiles ? null : download?.Sha256,
            Size = size,
            Disk = size,
            GitHubRequests = pick.GitHubRequests,
            Reason = pick.Reason
        };
    }

    /// <summary>A game's row in plain words (English; the window translates it).</summary>
    public static string Words(GamePlan game) => game.Row switch
    {
        AddRow.Ready => "ready",
        AddRow.LogicAndMap => "logic and map",
        AddRow.Logic => "logic (its map pack is installed)",
        AddRow.LogicNoMapKnown => "logic (no map pack is known yet)",
        AddRow.LogicMapFromGuide => "logic, then the map pack its setup guide names, if it names one",
        AddRow.Map => "map (its logic is installed)",
        AddRow.MapLogicShips => "map (its logic ships with Archipelago)",
        AddRow.NoMapKnown => "ready, but no map pack is known yet",
        AddRow.MapOnly => "map (Atlas doesn't know where its logic comes from yet)",
        AddRow.NothingKnown => "nothing to add: Atlas doesn't know where its logic comes from yet",
        AddRow.MapWaits => game.LogicComing ? "logic (its map pack waits for GitHub's hourly limit)" : "its map pack waits for GitHub's hourly limit",
        AddRow.Waits => "waits for GitHub's hourly limit",
        _ => ""
    };
}
