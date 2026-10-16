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
                // What the pack lab measured for this very file, when Atlas's game list knows it (the overview compares).
                try
                {
                    string sha = await Task.Run(() => AP_Atlas.Core.EngineSetup.ApworldSources.Sha256OfPack(original.SourcePath));
                    if (AP_Atlas.Core.EngineSetup.ApworldSources.PackBySha256(sha) is { } lab && lab.Pack.Fit is int labFit)
                    {
                        report.LabFit = labFit;
                        report.LabChecks = lab.Pack.Checks;
                    }
                }
                catch (System.IO.IOException ex) { Logger.LogDebug("The pack's file couldn't be read for the game list's measure: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { Logger.LogDebug("The pack's file couldn't be read for the game list's measure: " + ex.Message); }
                Reports[key] = report;
                ReportReady?.Invoke(key);

                if (authorNotes.Count > 0)
                    ReviewSuggested?.Invoke(key, $"{original.Manifest?.Name} updated: {authorNotes.Count} of your fixes were replaced by the author's changes.");

                var fixes = PackFixes.Get(key);
                string version = original.Manifest?.GetActualVersion() ?? "";
                // Once per pack version: exact name matches are linked by the Doctor itself (undoable; the edit queues a
                // check with them in, which then lists what's left).
                if (_settings?.PackDoctorAutoLink != false && fixes.AutoLinkedVersion != AutoLinkStamp(original) && names != null)
                {
                    int forgotten = ForgetStaleGuesses(key, original);
                    if (forgotten > 0) Logger.LogInfo($"Pack Doctor took back {forgotten} of its own links in '{original.Manifest?.Name}' that its improved rules read differently; they link again where they can.");
                    int linked = AutoLinkExact(key, original, report);
                    if (linked > 0) Logger.LogInfo($"Pack Doctor linked {linked} exact name match{(linked == 1 ? "" : "es")} in '{original.Manifest?.Name}' by itself (Undo in the Doctor reverses them).");
                }
                int needs = report.NeedsReview.Count();
                bool newOrChanged = fixes.ReviewedVersion != version;
                if (prompt && newOrChanged && needs > 0)
                {
                    int fixable = report.Findings.Count(f => !f.Ignored && (f.Actions.HasFlag(FindingActions.LinkItem) || f.Actions.HasFlag(FindingActions.LinkLocation)));
                    string name = original.Manifest?.Name ?? key, review = $"Pack Doctor: {original.Manifest?.Name} has {needs} thing{(needs == 1 ? "" : "s")} to review.";
                    Prompt(key, fixable > 0 ? () => FixSuggested?.Invoke(key, fixable, name) : () => ReviewSuggested?.Invoke(key, review));
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
        /// The Doctor's analysis of a pack and nothing else a check does: no fixes linked, no prompt, no report kept, its
        /// images left alone. How a release that's only being tried fits the game's names, or (with <paramref name="names"/>)
        /// how the pack fits one table of names (the seed's, the installed apworld's) when the Doctor compares them.
        /// </summary>
        public static async Task<DoctorReport> MeasureAsync(LoadedPack original, GameNameTable names = null)
        {
            if (original == null) return null;
            if (names == null)
            {
                string game = GameNames.ResolveGame(original.Manifest) ?? original.Manifest?.GameName;
                await EnsureLocalNamesAsync(game);
                game = GameNames.ResolveGame(original.Manifest) ?? game;
                names = GameNames.Best(game);
            }
            var inputs = PackDoctor.Prepare(original, names);
            return await Task.Run(() => PackDoctor.Analyze(inputs));
        }

        /// <summary>
        /// The Doctor's linking rules, counted: raised whenever they change (Atlas's version alone doesn't change between
        /// builds of one beta), so the automatic pass runs again for packs checked before. 2: shops' "Item 1" named like
        /// their neighbours, and generic names no longer taken for one check shown twice (2026-10-12).
        /// </summary>
        public const int LinkRules = 2;

        /// <summary>
        /// What the automatic pass is recorded against: the pack's version, Atlas's and its linking rules', so the pass runs
        /// again (for what's still unlinked, after taking back its own clashing guesses) when the rules improve, not only
        /// when the pack changes.
        /// </summary>
        public static string AutoLinkStamp(LoadedPack original) => (original?.Manifest?.GetActualVersion() ?? "") + " / Atlas " + AtlasVersion.Display + " / rules " + LinkRules;

        /// <summary>
        /// Links every recommendation whose best match is exact and clashes with nothing (<see cref="PackDoctor.ExactMatches"/>)
        /// as fixes marked automatic, in one undoable step, and records the pass for the pack's version. Main thread.
        /// Returns how many it linked.
        /// </summary>
        public static int AutoLinkExact(string key, LoadedPack original, DoctorReport report)
        {
            string version = AutoLinkStamp(original);
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
                        file.Tiles.Add(new TileFix { Subject = f.Subject, Code = subject, ApItemId = s.Id, ApItemName = s.Label, Source = "automatic", Reason = s.Reason, AuthorStamp = PackFixes.AuthorStamp(original, f.Subject) });
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
                            Reason = s.Reason,
                            AuthorStamp = PackFixes.AuthorStamp(original, f.Subject)
                        });
                    }
                }
                file.AutoLinkedVersion = version;
            });
            return exact.Count;
        }

        /// <summary>
        /// Atlas's own links (made by its automatic pass or by "Fix what Atlas can") that its improved rules read
        /// differently, taken back when the rules change, so the rules link them again where they can: links that send
        /// different checks to one location (all but one are wrong, and which can't be told: an earlier version linked every
        /// shop's "Item 1" to Kokiri Forest's), and links made for what the pack has changed since as Atlas reads it (a tile
        /// linked when its code seemed undefined, which Atlas now finds on the item's stage). The user's own links are never
        /// touched. Main thread; returns how many it took back.
        /// </summary>
        public static int ForgetStaleGuesses(string key, LoadedPack original)
        {
            var file = PackFixes.Get(key);
            static bool Guess(PackFixBase fix) => fix.Source is "automatic" or "suggested";
            bool Changed(PackFixBase fix) => original != null && !string.IsNullOrEmpty(fix.AuthorStamp) && fix.AuthorStamp != PackFixes.AuthorStamp(original, fix.Subject);
            var stale = file.Links.Where(l => l.ApLocationId != null && Guess(l)).GroupBy(l => l.ApLocationId.Value)
                .Where(g => g.Select(l => PackDoctor.CheckIdentity(l.PinPath, l.SectionName, l.ApLocationName)).Distinct().Count() > 1)
                .SelectMany(g => g.Select(l => l.Subject)).ToHashSet(StringComparer.Ordinal);
            stale.UnionWith(file.Links.Where(l => Guess(l) && Changed(l)).Select(l => l.Subject));
            stale.UnionWith(file.Tiles.Where(t => Guess(t) && Changed(t)).Select(t => t.Subject));
            if (stale.Count == 0) return 0;
            PackFixes.Edit(key, $"Take back {stale.Count} of Atlas's links that its improved rules read differently", f =>
            {
                f.Links.RemoveAll(l => Guess(l) && stale.Contains(l.Subject));
                f.Tiles.RemoveAll(t => Guess(t) && stale.Contains(t.Subject));
            });
            return stale.Count;
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
        public static SuggestedFixResult ApplySuggested(string key, LoadedPack original, DoctorReport report) =>
            report == null ? new SuggestedFixResult() : ApplyPlan(key, original, report, PackBulk.Suggested(report, PackFixes.Get(key)));

        /// <summary>Carries out a "Fix what Atlas can" plan (<see cref="PackBulk.Suggested"/>, as its preview showed it) and says what it did.</summary>
        public static SuggestedFixResult ApplyPlan(string key, LoadedPack original, DoctorReport report, BulkPlan plan)
        {
            var result = new SuggestedFixResult();
            if (plan == null || plan.Nothing) return result;
            foreach (var (f, s) in plan.Link) result.Linked.Add($"{PackDoctor.RowName(report, f)} → {s.Label} ({s.Score:P0})");
            foreach (var f in plan.SetAside)
                result.SetAside.Add(PackDoctor.RowName(report, f) + (f.Suggestions.Count > 0 ? $" (best match {f.Suggestions[0].Label}, {f.Suggestions[0].Score:P0})" : " (no match)"));
            PackBulk.Apply(key, original, plan, $"Fix what Atlas can: link {plan.Link.Count}, set aside {plan.SetAside.Count}");
            Logger.LogInfo($"Pack Doctor fixed what it could in '{original?.Manifest?.Name}': linked {result.Linked.Count}, set aside {result.SetAside.Count}."
                + (result.Linked.Count > 0 ? "\n  Linked: " + string.Join("; ", result.Linked) : "") + (result.SetAside.Count > 0 ? "\n  Set aside: " + string.Join("; ", result.SetAside) : ""));
            return result;
        }

        /// <summary>
        /// A pack new to this version has things Atlas can fix (prompted once per version, instead of <see cref="ReviewSuggested"/>):
        /// the pack's key, how many, and its name.
        /// </summary>
        public static event Action<string, int, string> FixSuggested;

        // Prompts held while Add games runs: its summary asks one question for every pack it installed. Main thread.
        private static int _promptHolds;
        private static readonly List<(string Key, Action Raise)> _heldPrompts = new List<(string, Action)>();

        /// <summary>
        /// Holds the Doctor's prompts (the "Fix now" and "to review" cards) until the hold is released: Add games gathers its
        /// packs' reports into one question, and a pack a connected slot loads meanwhile doesn't ask on its own. Main thread.
        /// </summary>
        public static PromptHold HoldPrompts()
        {
            _promptHolds++;
            return new PromptHold();
        }

        /// <summary>A hold on the Doctor's prompts (<see cref="HoldPrompts"/>).</summary>
        public sealed class PromptHold
        {
            private bool _released;

            /// <summary>Ends the hold: the prompts held meanwhile are raised now (the latest per pack), but for the packs in <paramref name="answered"/>, which the holder asks about itself.</summary>
            public void Release(IReadOnlyCollection<string> answered)
            {
                if (_released) return;
                _released = true;
                if (--_promptHolds > 0) return;
                var held = _heldPrompts.ToList();
                _heldPrompts.Clear();
                foreach (var (key, raise) in held)
                    if (!answered.Contains(key, StringComparer.OrdinalIgnoreCase)) raise();
            }
        }

        private static void Prompt(string key, Action raise)
        {
            if (_promptHolds == 0) { raise(); return; }
            _heldPrompts.RemoveAll(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            _heldPrompts.Add((key, raise));
        }

        /// <summary>Marks a pack's current version as reviewed (no more prompts until it changes).</summary>
        public static void MarkReviewed(LoadedPack original)
        {
            string key = PackFixes.KeyFor(original);
            string version = original.Manifest?.GetActualVersion() ?? "";
            if (PackFixes.Get(key).ReviewedVersion == version) return;
            PackFixes.SetReviewedVersion(key, version);
        }

        /// <summary>
        /// Fetches the names of games just added to the engine in one engine process for all of them (Add games): a table kept
        /// from an older version is replaced, and a fetch tried earlier this session, before the game was in, doesn't block
        /// it. The packs installed next are then measured against the version now in the engine. Main thread.
        /// </summary>
        public static async Task RefreshLocalNamesAsync(IReadOnlyCollection<string> games)
        {
            var wanted = games.Where(g => !string.IsNullOrEmpty(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (wanted.Count == 0 || _settings == null) return;
            foreach (string game in wanted) _localFetchAttempted.Add(game);
            var install = AP_Atlas.Core.EngineSetup.AtlasEngine.Resolve(_settings);
            var engine = new LogicEngineManager(install, msg => Logger.LogInfo("[Pack Doctor] " + msg));
            var result = await engine.FetchLocalNamesAsync(wanted);
            if (result == null) return;
            GameNames.SetKnownGames(result.Value.KnownGames.Concat(AP_Atlas.Core.EngineSetup.AtlasEngine.Worlds.Games).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            foreach (var table in result.Value.Tables.Values) GameNames.Store(table);
        }

        /// <summary>Fetches the game's names from the local install if they aren't stored yet (or on request).</summary>
        public static async Task EnsureLocalNamesAsync(string game, bool force = false)
        {
            if (string.IsNullOrEmpty(game) || _settings == null) return;
            bool have = GameNames.Local(game) != null;
            if (have && !force) return;
            if (!force && !_localFetchAttempted.Add(game)) return;
            var install = AP_Atlas.Core.EngineSetup.AtlasEngine.Resolve(_settings);
            // A game that ships with Archipelago has its world unpacked from the kept zip first (nothing downloaded here).
            string official = AP_Atlas.Core.EngineSetup.AtlasEngine.Worlds.Games.FirstOrDefault(g => PopTrackerPackLoader.IsGameNameMatch(game, g));
            if (official != null && AP_Atlas.Core.EngineSetup.AtlasEngine.WorldsZipPresent && AP_Atlas.Core.EngineSetup.AtlasEngine.WorldsToUnpack(install, official).Count > 0)
                await AP_Atlas.Core.EngineSetup.AtlasEngine.EnsureWorldsAsync(install, new[] { official }, false, msg => Logger.LogInfo("[Pack Doctor] " + msg), System.Threading.CancellationToken.None);
            var engine = new LogicEngineManager(install, msg => Logger.LogInfo("[Pack Doctor] " + msg));
            // Ask for the pack's name as given; the reply also lists every installed game, so a near-miss
            // ("Dark Souls 2" vs "Dark Souls II") can be resolved and fetched in a second pass.
            var result = await engine.FetchLocalNamesAsync(new[] { game });
            if (result == null) return;
            // Archipelago's own games count as known though their worlds aren't unpacked (a pack's game name resolves to them).
            GameNames.SetKnownGames(result.Value.KnownGames.Concat(AP_Atlas.Core.EngineSetup.AtlasEngine.Worlds.Games).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
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
