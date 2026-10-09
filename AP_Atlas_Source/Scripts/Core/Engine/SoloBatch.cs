#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.PopTracker;
using AP_Atlas.Core.Reports;

namespace AP_Atlas.Core.EngineSetup
{
    /// <summary>One game's line of a batch run's summary.</summary>
    public sealed record SoloBatchRow(string Game, string Outcome, string Logic, string Pins, string Tiles, int ToCheck, string Report);

    /// <summary>How far a batch run is: the game it's on, its place in the list, and that game's current step.</summary>
    public sealed record SoloBatchProgress(string Game, int Index, int Count, SoloTestProgress? Step, SoloBatchRow? Finished);

    /// <summary>
    /// "Test every ready game": the solo test over several games in turn, without a server or a slot (the YAML, the seed,
    /// logic scored against the seed's spheres, the installed pack scored by the Pack Doctor), one report each and a
    /// summary table (reports/solo-tests/batch-&lt;date&gt;.md). Nothing is downloaded or asked; a game the engine
    /// lacks fails its first step. One batch at a time.
    /// </summary>
    public static class SoloBatch
    {
        /// <summary>Raised (on the main thread) when a batch starts or ends.</summary>
        public static event Action? StateChanged;

        public static bool Running { get; private set; }

        private static CancellationTokenSource? _running;

        /// <summary>Ends a running batch after the game it's on (Atlas is closing, or a test needs the engine).</summary>
        public static void CancelRunning() => _running?.Cancel();

        /// <summary>The games the engine has that a batch would test: with an installed map pack, or every one.</summary>
        public static List<string> ReadyGames(EngineInstall? install, bool includeWithoutPack)
        {
            var games = AtlasEngine.LastCheck(install)?.Games ?? new List<string>();
            return games.Where(g => includeWithoutPack || SoloTestRunner.InstalledPackFor(g) != null).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Runs the games in turn; the summary's path, or null when nothing ran. Cancelling ends after the game that's running.</summary>
        public static async Task<string?> RunAsync(IReadOnlyList<string> games, SoloTestHooks hooks, IProgress<SoloBatchProgress>? progress, CancellationToken ct)
        {
            if (Running) throw new InvalidOperationException("A batch is already running.");
            if (SoloTestRunner.Running != null) throw new InvalidOperationException("A solo test is running; stop it first.");
            Running = true;
            StateChanged?.Invoke();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _running = linked;
            ct = linked.Token;
            var rows = new List<SoloBatchRow>();
            var started = DateTime.Now; // wall clock: named in the summary, never compared
            try
            {
                for (int i = 0; i < games.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    string game = games[i];
                    progress?.Report(new SoloBatchProgress(game, i, games.Count, null, null));
                    var runner = new SoloTestRunner(game);
                    SoloTestResult? result = null;
                    try
                    {
                        result = await runner.RunAsync(hooks, new Progress<SoloTestProgress>(p => progress?.Report(new SoloBatchProgress(game, i, games.Count, p, null))), ct, connect: false);
                    }
                    catch (OperationCanceledException)
                    {
                        // The runner wrote its report for what ran; the summary says the batch stopped here.
                    }
                    var row = Summarize(game, result, runner.ReportPath);
                    rows.Add(row);
                    progress?.Report(new SoloBatchProgress(game, i, games.Count, null, row));
                }
                if (rows.Count == 0) return null;
                string path = Path.Combine(SoloTestRunner.ReportsFolder, "batch-" + started.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".md");
                SafeFile.WriteAllText(path, Summary(rows, started, games.Count, hooks.AtlasVersionLine(), ct.IsCancellationRequested));
                return path;
            }
            finally
            {
                _running = null;
                Running = false;
                StateChanged?.Invoke();
            }
        }

        /// <summary>One game's line from its result (a run that ended before its report has an outcome and nothing else).</summary>
        public static SoloBatchRow Summarize(string game, SoloTestResult? result, string? reportPath)
        {
            string report = reportPath == null ? "" : Path.GetFileName(reportPath);
            if (result == null) return new SoloBatchRow(game, "stopped", "not scored", "not scored", "not scored", 0, report);
            var failed = result.Steps.FirstOrDefault(s => s.Outcome == SoloStepOutcome.Failed);
            string outcome = failed != null ? failed.Step + " failed: " + failed.Detail : result.Steps.Any(s => s.Outcome == SoloStepOutcome.Cancelled) ? "stopped" : "ok";
            string logic = result.Logic == null ? "not scored" : result.Logic.Exact == true ? "exact (" + result.Logic.Spheres + " spheres)" : result.Logic.Exact == false ? "differs: " + result.Logic.Late + " late, " + result.Logic.Early + " early" : "not scored";
            string pins = result.Versions.PackName == null ? "no pack" : result.Pins == null ? "not scored" : SoloTestReport.Percent(result.Pins.LocationsPlaced, result.Pins.LocationsTotal) + " placed";
            string tiles = result.Versions.PackName == null ? "no pack" : result.KeyItems == null ? "not scored" : SoloTestReport.Percent(result.KeyItems.TilesLinked, result.KeyItems.TilesTotal) + " linked";
            int toCheck = result.Findings?.Where(f => f.Severity is "Problem" or "Warning").Sum(f => f.Count) ?? 0;
            return new SoloBatchRow(game, outcome, logic, pins, tiles, toCheck, report);
        }

        /// <summary>The summary file's text: a table, one line per game, and how to compare two runs.</summary>
        public static string Summary(IReadOnlyList<SoloBatchRow> rows, DateTime started, int planned, string atlas, bool stopped)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Solo test batch: " + rows.Count + " of " + planned + " games" + (stopped ? " (stopped early)" : ""));
            sb.AppendLine();
            sb.AppendLine("Started " + started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (local time). Written by " + atlas + ". No server and no slot: logic is scored against each seed's spheres, the pack by the Pack Doctor.");
            sb.AppendLine();
            sb.AppendLine("| Game | Outcome | Logic | Pins | Tiles | To check | Report |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in rows)
                sb.AppendLine("| " + Cell(r.Game) + " | " + Cell(r.Outcome) + " | " + Cell(r.Logic) + " | " + Cell(r.Pins) + " | " + Cell(r.Tiles) + " | " + r.ToCheck + " | " + Cell(r.Report) + " |");
            sb.AppendLine();
            sb.AppendLine("Compare two batches with `Tools/solo_diff.py <older reports folder> <newer reports folder>` (each game's newest report in each).");
            return sb.ToString();
        }

        private static string Cell(string text) => (text ?? "").Replace("|", "/").Replace("\n", " ");
    }
}
