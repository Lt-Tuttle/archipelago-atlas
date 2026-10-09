#nullable disable
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

        /// <summary>The locations of the seed a connected slot of a game plays (set by the window; null or empty when none).</summary>
        public static Func<string, IEnumerable<long>> SeedLocations { get; set; }

        private static AppSettings _settings;
        private static readonly HashSet<string> _running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Packs whose fixes changed while a check ran: checked once more when it ends, so the edit isn't lost to the snapshot.
        private static readonly HashSet<string> _checkAgain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a pack will be checked again after the check that's running (its fixes changed meanwhile): the report
        /// the running check delivers isn't the last word. False once the last report of a sequence is delivered.
        /// </summary>
        public static bool IsBusy(string packKey) => _checkAgain.Contains(packKey);

        /// <summary>Whether a check of the pack is running now (a check asked for meanwhile answers with the last report, or null).</summary>
        public static bool IsChecking(string packKey) => _running.Contains(packKey);
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
            catch (Exception ex)
            {
                Logger.LogWarning($"Pack Doctor couldn't read the pack to check it again: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Checks a pack in the background. With prompt, suggests a review when the pack is new or changed since
        /// it was last reviewed and something needs a decision.
        /// </summary>
        public static async Task<DoctorReport> CheckAsync(LoadedPack original, bool prompt = true, bool refreshLocalNames = false)
        {
            if (original == null) return null;
            string key = PackFixes.KeyFor(original);
            if (!_running.Add(key))
            {
                _checkAgain.Add(key);
                return Reports.TryGetValue(key, out var existing) ? existing : null;
            }
            try
            {
                // Which of the pack's images decode (and their sizes): checked off the main thread, without keeping them,
                // when nothing that uses the pack has decoded them. The fixes' stamps and the checks below read them.
                if (!original.ImagesChecked) await Task.Run(() => PackImages.Check(original));

                // Author wins: an update that changed something the user fixed sets that fix aside.
                var authorNotes = PackFixes.ResolveAuthorChanges(original);
                if (authorNotes.Count > 0) Logger.LogWarning($"Map pack '{original.Manifest?.Name}' updated: " + string.Join(" ", authorNotes));

                string game = GameNames.ResolveGame(original.Manifest) ?? original.Manifest?.GameName;
                await EnsureLocalNamesAsync(game, refreshLocalNames);
                game = GameNames.ResolveGame(original.Manifest) ?? game;
                var names = GameNames.Best(game);

                var slotData = DataManager.LatestSlotDataForGame(game) ?? DataManager.LatestSlotDataForGame(original.Manifest?.GameName);
                var inputs = PackDoctor.Prepare(original, names);
                var report = await Task.Run(() => PackDoctor.Analyze(inputs, slotData));
                try { report.SeedLocationIds = new HashSet<long>(SeedLocations?.Invoke(game) ?? Enumerable.Empty<long>()); }
                catch (Exception ex) { Logger.LogDebug("The seed's locations for the Pack Doctor couldn't be read: " + ex.Message); }
                Reports[key] = report;
                ReportReady?.Invoke(key);

                if (authorNotes.Count > 0)
                    ReviewSuggested?.Invoke(key, $"{original.Manifest?.Name} updated: {authorNotes.Count} of your fixes were replaced by the author's changes.");

                var fixes = PackFixes.Get(key);
                string version = original.Manifest?.GetActualVersion() ?? "";
                // Once per pack version: exact name matches are linked by the Doctor itself (undoable; the edit queues a
                // check with them in, which then lists what's left).
                if (_settings?.PackDoctorAutoLink != false && fixes.AutoLinkedVersion != version && names != null)
                {
                    int linked = AutoLinkExact(key, original, report);
                    if (linked > 0) Logger.LogInfo($"Pack Doctor linked {linked} exact name match{(linked == 1 ? "" : "es")} in '{original.Manifest?.Name}' by itself (Undo in the Doctor reverses them).");
                }
                int needs = report.NeedsReview.Count();
                bool newOrChanged = fixes.ReviewedVersion != version;
                if (prompt && newOrChanged && needs > 0)
                {
                    int fixable = report.Findings.Count(f => !f.Ignored && (f.Actions.HasFlag(FindingActions.LinkItem) || f.Actions.HasFlag(FindingActions.LinkLocation)));
                    if (fixable > 0) FixSuggested?.Invoke(key, fixable, original.Manifest?.Name ?? key);
                    else ReviewSuggested?.Invoke(key, $"Pack Doctor: {original.Manifest?.Name} has {needs} thing{(needs == 1 ? "" : "s")} to review.");
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
                if (_checkAgain.Remove(key)) AP_Atlas.Core.Async.Fire(CheckAsync(original, prompt: false), "checking a map pack again after an edit");
            }
        }

        /// <summary>
        /// Links every recommendation whose best match is exact and clashes with nothing (<see cref="PackDoctor.ExactMatches"/>)
        /// as fixes marked automatic, in one undoable step, and records the pass for the pack's version. Main thread.
        /// Returns how many it linked.
        /// </summary>
        public static int AutoLinkExact(string key, LoadedPack original, DoctorReport report)
        {
            string version = original?.Manifest?.GetActualVersion() ?? "";
            var exact = PackDoctor.ExactMatches(report);
            if (exact.Count == 0)
            {
                PackFixes.SetAutoLinkedVersion(key, version);
                return 0;
            }
            PackFixes.Edit(key, $"Link {exact.Count} exact name match{(exact.Count == 1 ? "" : "es")} automatically", file =>
            {
                foreach (var f in exact)
                {
                    var s = f.Suggestions[0];
                    string subject = f.Subject.Substring(f.Subject.IndexOf(':') + 1);
                    if (f.Actions.HasFlag(FindingActions.LinkItem))
                    {
                        file.Tiles.RemoveAll(t => t.Subject == f.Subject);
                        file.Tiles.Add(new TileFix { Subject = f.Subject, Code = subject, ApItemId = s.Id, ApItemName = s.Label, Source = "automatic", AuthorStamp = PackFixes.AuthorStamp(original, f.Subject) });
                    }
                    else
                    {
                        var parts = subject.Split('|');
                        file.Links.RemoveAll(l => l.Subject == f.Subject);
                        file.Links.Add(new LocationLinkFix
                        {
                            Subject = f.Subject,
                            PinPath = parts[0],
                            SectionName = parts.Length > 1 ? parts[1] : "",
                            ApLocationId = s.Id,
                            ApLocationName = s.Label,
                            Source = "automatic",
                            AuthorStamp = PackFixes.AuthorStamp(original, f.Subject)
                        });
                    }
                }
                file.AutoLinkedVersion = version;
            });
            return exact.Count;
        }

        /// <summary>What "Fix what Atlas can" did: the rows linked (name → match, score) and the rows set aside (ignored).</summary>
        public sealed class SuggestedFixResult
        {
            public List<string> Linked = new List<string>();
            public List<string> SetAside = new List<string>();
            public bool Nothing => Linked.Count == 0 && SetAside.Count == 0;

            /// <summary>One line for a card or the status line.</summary>
            public string Describe(string packName) => Nothing ? $"{packName}: nothing to fix."
                : $"{packName}: linked {Linked.Count} match{(Linked.Count == 1 ? "" : "es")} and set aside {SetAside.Count} {(SetAside.Count == 1 ? "row" : "rows")} with no confident match. Undo in the Pack Doctor reverses all of it.";
        }

        /// <summary>The confidence a match needs for "Fix what Atlas can" to link it.</summary>
        public const double SuggestedMinScore = 0.85;

        /// <summary>
        /// Fixes what Atlas can without anyone opening the Doctor (the owner's choice, 2026-10-09): every tile and pin
        /// section still to decide gets its best match when it scores 85% or more and clashes with nothing, and is set
        /// aside (ignored) otherwise; one undoable step, each link marked "suggested". Main thread. The full list goes
        /// to the log; the result says the counts.
        /// </summary>
        public static SuggestedFixResult ApplySuggested(string key, LoadedPack original, DoctorReport report)
        {
            var result = new SuggestedFixResult();
            if (report == null) return result;
            var fixable = report.Findings.Where(f => !f.Ignored && (f.Actions.HasFlag(FindingActions.LinkItem) || f.Actions.HasFlag(FindingActions.LinkLocation))).ToList();
            var recs = fixable.Where(f => f.Suggestions.Count > 0).ToList();
            var conflicts = PackDoctor.Conflicts(report, recs, f => f.Suggestions[0]);
            var link = recs.Where(f => f.Suggestions[0].Score >= SuggestedMinScore - 0.0001 && !conflicts.ContainsKey(f.Key)).ToList();
            var aside = fixable.Where(f => !link.Contains(f)).ToList();
            if (link.Count == 0 && aside.Count == 0) return result;
            PackFixes.Edit(key, $"Fix what Atlas can: link {link.Count}, set aside {aside.Count}", file =>
            {
                foreach (var f in link)
                {
                    var s = f.Suggestions[0];
                    string subject = f.Subject.Substring(f.Subject.IndexOf(':') + 1);
                    if (f.Actions.HasFlag(FindingActions.LinkItem))
                    {
                        file.Tiles.RemoveAll(t => t.Subject == f.Subject);
                        file.Tiles.Add(new TileFix { Subject = f.Subject, Code = subject, ApItemId = s.Id, ApItemName = s.Label, Source = "suggested", AuthorStamp = PackFixes.AuthorStamp(original, f.Subject) });
                    }
                    else
                    {
                        var parts = subject.Split('|');
                        file.Links.RemoveAll(l => l.Subject == f.Subject);
                        file.Links.Add(new LocationLinkFix
                        {
                            Subject = f.Subject,
                            PinPath = parts[0],
                            SectionName = parts.Length > 1 ? parts[1] : "",
                            ApLocationId = s.Id,
                            ApLocationName = s.Label,
                            Source = "suggested",
                            AuthorStamp = PackFixes.AuthorStamp(original, f.Subject)
                        });
                    }
                    result.Linked.Add($"{PackDoctor.RowName(report, f)} → {s.Label} ({s.Score:P0})");
                }
                foreach (var f in aside)
                {
                    if (!file.Ignored.Contains(f.Key)) file.Ignored.Add(f.Key);
                    result.SetAside.Add(PackDoctor.RowName(report, f) + (f.Suggestions.Count > 0 ? $" (best match {f.Suggestions[0].Label}, {f.Suggestions[0].Score:P0})" : " (no match)"));
                }
            });
            Logger.LogInfo($"Pack Doctor fixed what it could in '{original?.Manifest?.Name}': linked {result.Linked.Count}, set aside {result.SetAside.Count}."
                + (result.Linked.Count > 0 ? "\n  Linked: " + string.Join("; ", result.Linked) : "") + (result.SetAside.Count > 0 ? "\n  Set aside: " + string.Join("; ", result.SetAside) : ""));
            return result;
        }

        /// <summary>
        /// A pack new to this version has things Atlas can fix (prompted once per version, instead of <see cref="ReviewSuggested"/>):
        /// the pack's key, how many, and its name.
        /// </summary>
        public static event Action<string, int, string> FixSuggested;

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
