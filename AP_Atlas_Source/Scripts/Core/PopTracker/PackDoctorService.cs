using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>
    /// Runs the Pack Doctor in the background when a pack is installed, updated or first used, gathers name
    /// tables (local install, and the server's from connected slots), applies "author wins" after updates, and
    /// tells the user when something needs a decision. Main thread only (work runs on the thread pool).
    /// </summary>
    public static class PackDoctorService
    {
        /// <summary>Latest report per pack key (for badges and the window).</summary>
        public static readonly Dictionary<string, DoctorReport> Reports = new Dictionary<string, DoctorReport>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A report finished (pack key).</summary>
        public static event Action<string> ReportReady;

        /// <summary>Asks the user to review a pack: (pack key, message). The host shows a toast with a button.</summary>
        public static event Action<string, string> ReviewSuggested;

        private static AppSettings _settings;
        private static readonly HashSet<string> _running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _localFetchAttempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Initialize(AppSettings settings)
        {
            _settings = settings;
            PackFixes.Changed += key =>
            {
                // Re-check after edits so findings and coverage stay current (no prompt for the user's own edits).
                var pack = Reports.TryGetValue(key, out var r) ? r.Pack : null;
                var original = pack == null ? null : FindOriginal(pack.SourcePath);
                if (original != null) AP_Atlas.Core.Async.Fire(CheckAsync(original, prompt: false), "checking a map pack");
            };
        }

        private static LoadedPack FindOriginal(string zipPath)
        {
            try { return string.IsNullOrEmpty(zipPath) ? null : PopTrackerPackLoader.InspectZipPack(zipPath); }
            catch { return null; }
        }

        /// <summary>
        /// Checks a pack in the background. With prompt, suggests a review when the pack is new or changed since
        /// it was last reviewed and something needs a decision.
        /// </summary>
        public static async Task<DoctorReport> CheckAsync(LoadedPack original, bool prompt = true, bool refreshLocalNames = false)
        {
            if (original == null) return null;
            string key = PackFixes.KeyFor(original);
            if (!_running.Add(key)) return Reports.TryGetValue(key, out var existing) ? existing : null;
            try
            {
                // Author wins: an update that changed something the user fixed sets that fix aside.
                var authorNotes = PackFixes.ResolveAuthorChanges(original);
                if (authorNotes.Count > 0) Logger.LogWarning($"Map pack '{original.Manifest?.Name}' updated: " + string.Join(" ", authorNotes));

                string game = GameNames.ResolveGame(original.Manifest) ?? original.Manifest?.GameName;
                await EnsureLocalNamesAsync(game, refreshLocalNames);
                game = GameNames.ResolveGame(original.Manifest) ?? game;
                var names = GameNames.Best(game);

                var slotData = DataManager.LatestSlotDataForGame(game) ?? DataManager.LatestSlotDataForGame(original.Manifest?.GameName);
                var report = await Task.Run(() => PackDoctor.Analyze(original, names, slotData));
                Reports[key] = report;
                ReportReady?.Invoke(key);

                if (authorNotes.Count > 0)
                    ReviewSuggested?.Invoke(key, $"{original.Manifest?.Name} updated: {authorNotes.Count} of your fixes were replaced by the author's changes.");

                var fixes = PackFixes.Get(key);
                string version = original.Manifest?.GetActualVersion() ?? "";
                int needs = report.NeedsReview.Count();
                bool newOrChanged = fixes.ReviewedVersion != version;
                if (prompt && newOrChanged && needs > 0)
                {
                    ReviewSuggested?.Invoke(key, $"Pack Doctor: {original.Manifest?.Name} has {needs} thing{(needs == 1 ? "" : "s")} to review.");
                }
                return report;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Pack Doctor couldn't check '{original.Manifest?.Name}': {ex.Message}");
                return null;
            }
            finally
            {
                _running.Remove(key);
            }
        }

        /// <summary>Marks a pack's current version as reviewed (no more prompts until it changes).</summary>
        public static void MarkReviewed(LoadedPack original)
        {
            string key = PackFixes.KeyFor(original);
            string version = original.Manifest?.GetActualVersion() ?? "";
            if (PackFixes.Get(key).ReviewedVersion == version) return;
            PackFixes.SetReviewedVersion(key, version);
        }

        /// <summary>Fetches the game's names from the local install if they aren't stored yet (or on request).</summary>
        public static async Task EnsureLocalNamesAsync(string game, bool force = false)
        {
            if (string.IsNullOrEmpty(game) || _settings == null) return;
            bool have = GameNames.Local(game) != null;
            if (have && !force) return;
            if (!force && !_localFetchAttempted.Add(game)) return;
            var engine = new LogicEngineManager(AP_Atlas.Core.EngineSetup.AtlasEngine.Resolve(_settings), msg => Logger.LogInfo("[Pack Doctor] " + msg));
            // Ask for the pack's name as given; the reply also lists every installed game, so a near-miss
            // ("Dark Souls 2" vs "Dark Souls II") can be resolved and fetched in a second pass.
            var result = await engine.FetchLocalNamesAsync(new[] { game });
            if (result == null) return;
            GameNames.SetKnownGames(result.Value.KnownGames);
            foreach (var table in result.Value.Tables.Values) GameNames.Store(table);
            if (result.Value.Tables.Count == 0)
            {
                string resolved = result.Value.KnownGames.FirstOrDefault(g => PopTrackerPackLoader.IsGameNameMatch(game, g));
                if (resolved != null && GameNames.Local(resolved) == null)
                {
                    var second = await engine.FetchLocalNamesAsync(new[] { resolved });
                    if (second != null) foreach (var table in second.Value.Tables.Values) GameNames.Store(table);
                }
            }
        }
    }
}
