#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.Games;
using AP_Atlas.Core.PopTracker;

namespace AP_Atlas.Core.EngineSetup
{
    /// <summary>What the installer needs from the window: the map pack service's quiet installs and the game's chosen pack.</summary>
    public sealed class GameAdderHooks
    {
        public required AppSettings Settings { get; init; }
        /// <summary>Atlas's translation of a line for the user.</summary>
        public required Func<string, string> Tr { get; init; }
        /// <summary>Installs a pack Atlas's game list names, quietly (MapPackManagerControl.InstallIndexPackAsync): the file, or why not.</summary>
        public required Func<string, IndexPackDownload, Action<long, long>?, CancellationToken, Task<(string? File, string? Problem)>> InstallIndexPackAsync { get; init; }
        /// <summary>Installs a project's newest released pack, quietly, one release tried (MapPackManagerControl.InstallFittingReleaseAsync).</summary>
        public required Func<string, string, Action<string>?, Task<(bool Ok, string Detail, string? File)>> InstallProjectPackAsync { get; init; }
        /// <summary>Makes a pack (its zip's file name) the one the game's slots use, and tells them.</summary>
        public required Action<string, string> PreferPack { get; init; }
    }

    /// <summary>The confirm's answer: its tick "Don't ask again for Atlas's downloads", and whether it named GitHub's release lists (the plan needed them).</summary>
    public sealed record AddGamesConsent(bool AlwaysAllowDownloads, bool AllowGitHubLookups);

    /// <summary>What a batch did: a row per game, and per pack it installed what Atlas fixed by itself and what's left (Outcome.PacksToLook).</summary>
    public sealed record GameAdderRun(AddGamesOutcome Outcome);

    /// <summary>
    /// Adds the games of an Add games plan once its one confirm is pressed. It trusts the plan's apworld projects and allows
    /// the engine's downloads; downloads each community game's apworld by the address and SHA-256 Atlas's game list names
    /// (no GitHub listing read); adds them all to the engine with the official games' worlds and ONE health check (one logic
    /// restart per connected slot); reads the added games' names; installs each game's map pack quietly (the one its setup
    /// guide names when Atlas's list has it, else the plan's); then waits for the Pack Doctor's checks (whose first pass makes
    /// the confident fixes) and notes per pack what was fixed and what's left. One bar, Cancel between steps, a result per game. It never throws. One batch
    /// at a time. Main thread: the map pack service's installs touch the window; downloads and the engine run on workers.
    /// </summary>
    public sealed class GameAdder
    {
        private static readonly TimeSpan DoctorWait = TimeSpan.FromSeconds(120);

        /// <summary>For the UI test only: where the list's pack releases are downloaded from, in place of github.com.</summary>
        internal static string? TestGitHubSite { get; set; }

        private static int _running;

        /// <summary>Whether a batch is running.</summary>
        public static bool Busy => Volatile.Read(ref _running) != 0;

        private readonly GameAdderHooks _hooks;

        public GameAdder(GameAdderHooks hooks) => _hooks = hooks;

        private string T(string text) => _hooks.Tr(text);

        private static void Log(string line) => Logger.LogInfo("[Add games] " + line);

        public async Task<GameAdderRun> RunAsync(AddPlan plan, AddGamesConsent consent, Action<AddGamesStatus>? progress, CancellationToken ct)
        {
            var outcome = new AddGamesOutcome(plan.Games.Select(g => (g.Game, g.LogicComing, g.WantsPack)));
            if (Interlocked.Exchange(ref _running, 1) != 0)
            {
                outcome.Finish(AddStep.Failed(T("another Install games is running; try again once it's done")));
                return new GameAdderRun(outcome);
            }
            // A game Atlas can't place says why: the engine first when it isn't ready (an official game's world waits for it).
            string? notReady = plan.EngineReady ? null : AtlasEngine.Current is { } engineNow && AtlasEngine.ProblemWith(engineNow) is { } why ? T(why) : T("The Atlas Engine isn't set up yet.");
            foreach (var g in plan.Games)
                if (g.Row == AddRow.Waits) outcome.For(g.Game).Logic = AddStep.Unavailable(T("it waits for GitHub's hourly limit: add it again in an hour"));
                else if (g.Row is AddRow.NothingKnown or AddRow.MapOnly) outcome.For(g.Game).Logic = AddStep.Unavailable(notReady ?? T("Atlas doesn't know where its logic comes from yet"));
            var bar = AddGamesProgress.For(plan.Games.Select(Work).ToList(), engine: plan.Games.Any(g => g.LogicComing), worldsDownloadBytes: plan.ArchipelagoReleaseBytes);
            var hold = PackDoctorService.HoldPrompts();
            var b = new Batch(plan, outcome, bar, progress, _hooks.Tr, ct);
            try
            {
                ct.ThrowIfCancellationRequested(); // a cancelled run trusts and allows nothing
                long? free = AtlasEngine.FreeBytes(DataManager.GetDataDirectory());
                if (free != null && !plan.FitsIn(free.Value, AtlasEngine.FreeSpaceFloorBytes))
                    outcome.Finish(AddStep.Failed(T("there isn't room: adding these games takes about {0} MB, and Atlas keeps {1} MB free for its engine")
                        .Replace("{0}", (plan.DiskBytes / (1 << 20)).ToString(CultureInfo.InvariantCulture)).Replace("{1}", (AtlasEngine.FreeSpaceFloorBytes / (1 << 20)).ToString(CultureInfo.InvariantCulture))));
                else
                {
                    Trust(plan, consent);
                    var install = AtlasEngine.Current;
                    string? engineProblem = install == null ? T("The Atlas Engine isn't set up yet.") : AtlasEngine.ProblemWith(install) is { } problem ? T(problem) : null;
                    string? writeProblem = engineProblem ?? (AtlasEngine.MayWriteTo(install!) ? null
                        : T("Atlas needs your OK before it adds files to your Archipelago install (Atlas Engine → Advanced → My Archipelago install → Allow…)."));
                    var files = await DownloadApworldsAsync(b, writeProblem);
                    await AddToEngineAsync(b, install, engineProblem, files);
                    await ReadNamesAsync(b);
                    var packs = await InstallPacksAsync(b, install);
                    await GatherFixesAsync(b, packs);
                    outcome.Finish(AddStep.Failed(T("it wasn't done (the log says why)")));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome.Cancelled = true;
                outcome.Finish(AddStep.Cancelled);
                Log("Stopped by the user.");
            }
            catch (Exception ex)
            {
                Logger.LogError("[Add games] " + ex);
                outcome.Finish(AddStep.Failed(T("something went wrong (the log has the details); try again")));
            }
            finally
            {
                b.Finished = true;
                outcome.GitHubRequests = b.GitHubRequests;
                // The packs its summary speaks of: their own "Atlas fixed" cards are dropped (the summary says it, with Review fixes).
                hold.Release(outcome.PacksToLook.Select(p => p.PackKey).ToList());
                Volatile.Write(ref _running, 0);
            }
            progress?.Invoke(new AddGamesStatus(outcome.Cancelled ? T("Stopped.") : T("Done."), bar.Complete()));
            Log($"Ended: logic for {outcome.LogicAdded}, maps for {outcome.MapsAdded}, {outcome.Failures.Count()} with a problem, {outcome.PacksToLook.Sum(p => p.Fixed)} fix(es) by Atlas in {outcome.PacksToLook.Count} pack(s), {outcome.GitHubRequests} GitHub request(s).");
            return new GameAdderRun(outcome);
        }

        private static AddGamesProgress.GameWork Work(GamePlan g) => new(
            g.Items.Any(i => i.Kind == AddItemKind.Apworld), g.Items.Where(i => i.Kind == AddItemKind.Apworld).Sum(i => i.Size),
            g.WantsPack, g.Items.Where(i => i.Kind == AddItemKind.Pack).Sum(i => i.Size));

        /// <summary>The confirm's press: every apworld source it named is trusted; the engine's downloads and GitHub's release lists are allowed as it said.</summary>
        private void Trust(AddPlan plan, AddGamesConsent consent)
        {
            foreach (string key in plan.ApworldProjects) ApworldSources.ApproveKey(_hooks.Settings, key);
            if (consent.AlwaysAllowDownloads) Permissions.SetAlways(_hooks.Settings, Permissions.EngineSetup, null, true);
            // Archipelago's release, and the packages a world declares (installed under the same permission), as the confirm said.
            else if ((plan.NeedsArchipelagoRelease || plan.WorldsWithPackages.Count > 0) && !Permissions.IsAllowed(_hooks.Settings, Permissions.EngineSetup))
                Permissions.AllowForSession(Permissions.EngineSetup);
            if (plan.NeedsGitHubLookups && consent.AllowGitHubLookups && !Permissions.IsAllowed(_hooks.Settings, Permissions.GitHubLookups)) Permissions.AllowForSession(Permissions.GitHubLookups);
        }

        private async Task<List<(string Game, string File, string Version)>> DownloadApworldsAsync(Batch b, string? problem)
        {
            var files = new List<(string, string, string)>();
            for (int i = 0; i < b.Plan.Games.Count; i++)
            {
                var g = b.Plan.Games[i];
                var item = g.Items.FirstOrDefault(x => x.Kind == AddItemKind.Apworld);
                if (item == null) continue;
                b.Ct.ThrowIfCancellationRequested();
                var row = b.Outcome.For(g.Game);
                if (problem != null) { row.Logic = AddStep.Failed(problem); continue; }
                b.Enter(AddGamesProgress.ApworldPart(i), T("Getting {0}'s apworld…").Replace("{0}", g.Game));
                ApworldVersion? version = item.Live ? await NewestReleaseAsync(b, g.Game, item)
                    : new ApworldVersion { Version = item.Version, Url = item.Url, Sha256 = item.Sha256, Origin = "index" };
                if (version == null)
                {
                    row.Logic = AddStep.Failed(T("its project has no release Atlas can install, or GitHub couldn't be asked; try again later"));
                    continue;
                }
                try
                {
                    string file = await Task.Run(() => ApworldSources.DownloadAsync(g.Game, ApworldSources.Find(g.Game)?.Apworld, version, Log, b.Ct), b.Ct);
                    files.Add((g.Game, file, version.Version ?? "?"));
                }
                catch (OperationCanceledException) when (b.Ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[Add games] {g.Game}'s apworld: {ex}");
                    row.Logic = AddStep.Failed(DownloadReason(ex));
                }
            }
            return files;
        }

        /// <summary>A project the user added: its release list read (one request), the newest full release with a usable link.</summary>
        private async Task<ApworldVersion?> NewestReleaseAsync(Batch b, string game, AddItem item)
        {
            if (!Permissions.IsAllowed(_hooks.Settings, Permissions.GitHubLookups) || item.Project == null) return null;
            string repo = item.Project.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase) ? item.Project["github.com/".Length..] : item.Project;
            b.Spend(item.GitHubRequests);
            var problems = new List<string>();
            var versions = await Task.Run(() => ApworldSources.VersionsAsync(game, new[] { repo }, ApworldSources.Find(game)?.Apworld, b.Ct, problems), b.Ct);
            foreach (string p in problems) Log($"{game}: {p}");
            return versions.Where(v => !v.Prerelease && AddGamesPlan.IsDownloadable(v.Url))
                .OrderByDescending(v => v.Version, ApworldChoices.VersionComparer.Instance).FirstOrDefault();
        }

        private string DownloadReason(Exception ex) => ex switch
        {
            InvalidDataException => T("the download isn't the file Atlas's game list names (its SHA-256 differs), so nothing was kept"),
            FileNotFoundException => T("its project no longer offers that file"),
            _ => T("it couldn't be downloaded ({0}); try again later").Replace("{0}", ex.Message)
        };

        private async Task AddToEngineAsync(Batch b, EngineInstall? install, string? problem, List<(string Game, string File, string Version)> files)
        {
            var worldGames = b.Plan.Games.Where(g => g.Items.Any(i => i.Kind == AddItemKind.World)).Select(g => g.Game).ToList();
            if (problem != null)
                foreach (string game in worldGames) b.Outcome.For(game).Logic = AddStep.Failed(problem);
            if (problem != null || install == null || (files.Count == 0 && worldGames.Count == 0)) return;
            b.Ct.ThrowIfCancellationRequested();
            bool zip = !AtlasEngine.WorldsZipPresent && worldGames.Any(g => AtlasEngine.WorldsToUnpack(install, g).Count > 0);
            b.Enter(AddGamesProgress.EnginePart, zip
                ? T("Downloading Archipelago's game worlds ({0}, once), then adding the games to the Atlas Engine and checking them…").Replace("{0}", AtlasEngine.WorldsZipSize)
                : T("Adding the games to the Atlas Engine and checking them (a few seconds)…"));
            var done = await Task.Run(() => AtlasEngine.InstallApworldsAsync(install, files.Select(f => (f.Game, f.File)).ToList(), worldGames, AtlasEngine.MayDownloadWorlds, Log, b.Ct));
            if (done.Check != null && !done.Check.Passed) b.Outcome.EngineProblem = done.Check.Problem;
            foreach (var (game, file, label) in files)
            {
                var r = done.Apworlds.FirstOrDefault(a => string.Equals(a.Source, file, StringComparison.OrdinalIgnoreCase));
                b.Outcome.For(game).Logic = r == null ? (done.Cancelled ? AddStep.Cancelled : AddStep.Failed(T("it wasn't added to the engine (the log says why)")))
                    : r.Loaded ? AddStep.Done(label)
                    : AddStep.Failed(T("it was installed, but {0}").Replace("{0}", r.Problem));
            }
            foreach (string game in worldGames)
            {
                bool missing = done.WorldsMissing.Contains(game, StringComparer.OrdinalIgnoreCase);
                b.Outcome.For(game).Logic = missing
                    ? (done.Cancelled ? AddStep.Cancelled : AddStep.Failed(T("Archipelago's release couldn't be downloaded or its world unpacked (the log says why); try again later")))
                    : done.Check?.Error != null ? AddStep.Failed(T("the engine's health check failed: {0}").Replace("{0}", done.Check.Problem))
                    : done.Check != null && !done.Check.Games.Contains(game, StringComparer.OrdinalIgnoreCase) ? AddStep.Failed(T("its world is unpacked, but it doesn't load in the engine"))
                    : AddStep.Done(null);
            }
            if (done.Cancelled) b.Ct.ThrowIfCancellationRequested();
        }

        private async Task ReadNamesAsync(Batch b)
        {
            var games = b.Plan.Games.Where(g => g.WantsPack && b.Outcome.For(g.Game).Logic.Outcome == AddStepOutcome.Done).Select(g => g.Game).ToList();
            if (games.Count == 0) return;
            b.Ct.ThrowIfCancellationRequested();
            b.Enter(AddGamesProgress.NamesPart, T("Reading the added games' names for their map packs…"));
            try { await PackDoctorService.RefreshLocalNamesAsync(games); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogWarning("[Add games] The added games' names couldn't be read: " + ex.Message); }
        }

        private async Task<List<(string Game, string File)>> InstallPacksAsync(Batch b, EngineInstall? install)
        {
            var installed = new List<(string, string)>();
            bool lookups = Permissions.IsAllowed(_hooks.Settings, Permissions.GitHubLookups);
            int spare = Math.Max(0, AddGamesPlan.GitHubBudget - b.Plan.GitHubRequests);
            for (int i = 0; i < b.Plan.Games.Count; i++)
            {
                var g = b.Plan.Games[i];
                // A game that waits for GitHub's limit planned no pack: none is installed outside the confirm's totals.
                if (!g.WantsPack || g.Row == AddRow.Waits) continue;
                b.Ct.ThrowIfCancellationRequested();
                var row = b.Outcome.For(g.Game);
                // The plan's pick's requests, or the ones it kept for the project the guide may name.
                int plannedCost = g.Items.FirstOrDefault(x => x.Kind == AddItemKind.Pack)?.GitHubRequests ?? (g.GuideLookupReserved ? AddGamesPlan.LivePackRequests : 0);
                var listed = ApworldSources.PackFactsFor(g.Game);
                var guide = GuideRepos(install, g.Game);
                // Picked again now that the game's logic (and so its setup guide) is in: the guide's pack when the list has it.
                var pick = AddGamesPlan.ChoosePack(listed, guide, spare + plannedCost, lookups);
                // The project the summary names: the guide's first one Atlas's list lacks, when that's what wasn't installed.
                row.GuideRepo = AddGamesPlan.GuideUnlistedRepo(listed, guide) ?? guide.FirstOrDefault();
                row.PackNote = AddGamesPlan.GuideNote(listed, guide, pick, lookups);
                if (pick == null)
                {
                    row.Map = AddStep.Unavailable(g.Row == AddRow.MapWaits ? T("its map pack waits for GitHub's hourly limit: add it again in an hour")
                        : row.PackNote != AddPackNote.None ? T("the one its setup guide names wasn't installed (below)")
                        : T("no map pack is known for it yet"));
                    spare += plannedCost; // its planned (or kept) requests weren't spent: a later game's guide may use them
                    continue;
                }
                spare += plannedCost - pick.GitHubRequests;
                string? file = null, problem = null;
                var from = pick;
                if (pick.Listed == null)
                {
                    b.Enter(AddGamesProgress.PackPart(i), T("Looking for {0}'s map pack at github.com/{1}, which its setup guide names…").Replace("{0}", g.Game).Replace("{1}", pick.Repo));
                    b.Spend(pick.GitHubRequests);
                    var (ok, detail, found) = await _hooks.InstallProjectPackAsync(g.Game, pick.Repo, null);
                    if (ok && found != null) { file = found; row.Map = AddStep.Done(detail); }
                    else
                    {
                        Log($"{g.Game}: the guide's project github.com/{pick.Repo} gave no pack ({detail}).");
                        problem = detail;
                        from = AddGamesPlan.ChoosePack(listed, Array.Empty<string>(), spare, mayReadProjects: false);
                        if (from != null) spare -= from.GitHubRequests;
                    }
                }
                if (file == null && from?.Listed is { } pack)
                {
                    b.Enter(AddGamesProgress.PackPart(i), T("Installing {0}'s map pack from github.com/{1}…").Replace("{0}", g.Game).Replace("{1}", pack.Repo));
                    (file, problem) = await InstallListedAsync(b, g.Game, pack);
                    if (file != null) row.Map = AddStep.Done("github.com/" + pack.Repo + (pack.CurrentFiles ? "" : " " + pack.Release));
                }
                if (file != null)
                {
                    row.PackFile = file;
                    row.PackRepo = from!.Repo;
                    row.PackFromGuide = from.Reason is PackReason.Guide or PackReason.GuideProject;
                    _hooks.PreferPack(g.Game, Path.GetFileName(file));
                    installed.Add((g.Game, file));
                }
                else row.Map = AddStep.Failed(T("its map pack couldn't be installed: {0}").Replace("{0}", problem ?? T("the log says why")));
            }
            return installed;
        }

        private async Task<(string? File, string? Problem)> InstallListedAsync(Batch b, string game, IndexPackFacts pack)
        {
            var download = IndexPackDownloads.For(pack.Repo, pack.Release, pack.Asset, pack.Sha256,
                TestGitHubSite ?? IndexPackDownloads.GitHubSite, GitHubApi.TestSite ?? GitHubApi.Site);
            if (download == null) return (null, T("Atlas's game list names it in a way Atlas can't download"));
            if (download.Zipball) b.Spend(AddGamesPlan.CurrentFilesRequests); // the project's current files come through GitHub's API
            try { return await _hooks.InstallIndexPackAsync(game, download, b.Within, b.Ct); }
            catch (OperationCanceledException) when (b.Ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Add games] {game}'s map pack: {ex}");
                return (null, ex.Message);
            }
        }

        /// <summary>The tracker projects the game's own setup guide names (its installed apworld's, or its unpacked world's), in order.</summary>
        private static IReadOnlyList<string> GuideRepos(EngineInstall? install, string game)
        {
            if (install == null) return Array.Empty<string>();
            try { return ApworldSources.GuideTrackerProjects(install, game).Select(p => p.Repo).ToList(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Logger.LogWarning($"[Add games] Couldn't read {game}'s setup guide: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private async Task GatherFixesAsync(Batch b, List<(string Game, string File)> packs)
        {
            if (packs.Count == 0) return;
            b.Enter(AddGamesProgress.DoctorPart, T("Checking the map packs against their games and fixing what Atlas can…"));
            foreach (var (game, file) in packs)
            {
                b.Ct.ThrowIfCancellationRequested();
                if (await LookAsync(game, file, b.Ct) is { } look) b.Outcome.PacksToLook.Add(look);
            }
        }

        /// <summary>
        /// What Atlas fixed by itself in a pack and what's left for the user, once the Doctor has settled on it (its automatic pass
        /// runs in the pack's first check); null when it can't be read or wasn't checked, or there's nothing to say. Main thread.
        /// </summary>
        internal static async Task<PackToLook?> LookAsync(string game, string file, CancellationToken ct)
        {
            LoadedPack? original;
            try { original = PopTrackerPackLoader.InspectZipPack(file); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning($"[Add games] {Path.GetFileName(file)} couldn't be read for its check: {ex.Message}");
                return null;
            }
            if (original == null) return null;
            string key = PackFixes.KeyFor(original);
            var report = await SettledReportAsync(original, key, ct);
            if (report == null) return null;
            var fixes = PackFixes.Get(key);
            int made = fixes.Tiles.Count(t => t.Source == "automatic") + fixes.Links.Count(l => l.Source == "automatic");
            int left = report.NeedsReview.Count();
            return made == 0 && left == 0 ? null : new PackToLook(game, key, original.Manifest?.Name ?? Path.GetFileNameWithoutExtension(file), file, made, left);
        }

        /// <summary>
        /// The pack's report once the Doctor is done with it: the rescan's check (or one queued after an edit) is waited for,
        /// one check is run with the names as they are now, and the check its automatic links queue is waited for too.
        /// </summary>
        private static async Task<DoctorReport?> SettledReportAsync(LoadedPack original, string key, CancellationToken ct)
        {
            await DoctorIdleAsync(key, ct);
            await PackDoctorService.CheckAsync(original, prompt: false);
            await DoctorIdleAsync(key, ct);
            return PackDoctorService.Reports.TryGetValue(key, out var report) ? report : null;
        }

        private static Task DoctorIdleAsync(string key, CancellationToken ct) => PackDoctorService.WhenIdleAsync(key, DoctorWait, ct);

        /// <summary>A running batch: its plan, rows, bar and the line it shows.</summary>
        private sealed class Batch
        {
            private readonly Action<AddGamesStatus>? _progress;
            private readonly Func<string, string> _tr;
            private string _doing = "";
            private long _shown;
            private int _part; // bumped on every Enter (main thread): a late download callback of an earlier part is dropped

            public Batch(AddPlan plan, AddGamesOutcome outcome, AddGamesProgress bar, Action<AddGamesStatus>? progress, Func<string, string> tr, CancellationToken ct)
            {
                Plan = plan;
                Outcome = outcome;
                Bar = bar;
                _progress = progress;
                _tr = tr;
                Ct = ct;
            }

            public AddPlan Plan { get; }
            public AddGamesOutcome Outcome { get; }
            public AddGamesProgress Bar { get; }
            public CancellationToken Ct { get; }
            public bool Finished { get; set; }

            private int _gitHub;
            /// <summary>The GitHub API requests the batch made (at most).</summary>
            public int GitHubRequests => _gitHub;
            public void Spend(int requests) => _gitHub += Math.Max(0, requests);

            /// <summary>A part begins: the line ("Game 2 of 5: …" for a game's part) and the bar move to it. Main thread.</summary>
            public void Enter(string part, string text)
            {
                Interlocked.Increment(ref _part);
                var step = Bar.Enter(part);
                _doing = step.Game > 0
                    ? _tr("Game {0} of {1}: {2}").Replace("{0}", step.Game.ToString(CultureInfo.InvariantCulture)).Replace("{1}", step.Games.ToString(CultureInfo.InvariantCulture)).Replace("{2}", text)
                    : text;
                Interlocked.Exchange(ref _shown, 0);
                Log(_doing);
                _progress?.Invoke(new AddGamesStatus(_doing, step.Fraction));
            }

            /// <summary>A download in the running part moved (any thread): the bar follows, every quarter megabyte.</summary>
            public void Within(long done, long total)
            {
                if (total <= 0 || (done - Interlocked.Read(ref _shown) < 256 * 1024 && done != total)) return;
                Interlocked.Exchange(ref _shown, done);
                // Read while this part's download still runs (the next Enter comes only after it ends): its part, not a later one.
                int part = Volatile.Read(ref _part);
                AP_Atlas.UI.Ui.Defer(null, () =>
                {
                    if (!Finished && part == Volatile.Read(ref _part)) _progress?.Invoke(new AddGamesStatus(_doing, Bar.Within((float)done / total)));
                }, "showing the Add games progress");
            }
        }
    }
}
