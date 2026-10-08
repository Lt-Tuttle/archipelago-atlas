using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.EngineSetup;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core
{
    /// <summary>One step of a slot's logic: the item that opened it, and the locations it opened.</summary>
    public sealed record LogicStep(string ItemName, IReadOnlyList<long> Locations)
    {
        /// <summary>The first step's name: what's in logic before any item.</summary>
        public const string StartName = "Starting Logic";

        public bool IsStart => ItemName == StartName;
    }

    /// <summary>
    /// A slot's logic: its logic engine (started, restarted after a failure, paused while the engine is updated) and what
    /// the engine says, worked out item by item in the order the items arrived: what's in logic, which item opened what,
    /// and whether the goal can be reached. Part of the slot's <see cref="SlotModel"/>, so it keeps running whether or not
    /// a view of the slot is in the window; it reports through the model's Changed (<see cref="SlotChange.Logic"/>).
    /// </summary>
    /// <remarks>
    /// Main thread only (engine events from other threads are handed over). Each engine run has a number: an answer, a
    /// failure or a restart timer that belongs to an engine since stopped is recognised and ignored, so it can't record
    /// stale steps or count one crash twice.
    /// </remarks>
    public sealed class SlotLogic : IDisposable
    {
        private static readonly int[] RestartDelaysSeconds = { 2, 10, 30 };

        // Items worked out per request: a big release can't keep an engine busy for minutes, and the other slots sharing
        // it are answered in between.
        private const int StepsPerRequest = 10;

        private static EngineStartError Paused() =>
            new() { Code = "paused", Message = "Logic is paused while the Atlas Engine is updated. It resumes by itself when the update finishes." };

        private readonly SlotModel _model;
        private readonly AppSettings _settings;
        private readonly Action<string> _log;
        private readonly List<long> _inventory = new();
        private readonly List<LogicStep> _steps = new();
        private readonly HashSet<long> _reachable = new();
        // When the engine failed this slot lately, on the steady clock (setting the PC's clock can't clear or stretch the count).
        private readonly List<DateTime> _recentFailures = new();
        private readonly HashSet<string> _seedApworldFailed = new();
        private readonly Dictionary<long, Task<LogicExplanation?>> _explainCache = new();
        private int _explainCacheItems = -1;
        private int _evaluated;
        private bool _startingDone, _busy, _dirty, _disposed;
        private int _run;

        public SlotLogic(SlotModel model, AppSettings settings, Action<string> log)
        {
            _model = model;
            _settings = settings;
            _log = log;
            // The multiworld's slots share a few engine processes (EnginePools).
            Engine = new LogicEngineManager(AtlasEngine.Resolve(settings), log, model.ProfileId, model.SlotName);
            Engine.EngineLost += OnEngineLost;
            AtlasEngine.Changed += OnEngineSetupChanged;
            AtlasEngine.PartsChanged += OnEnginePartsChanged;
            AtlasEngine.PauseRequested += OnEnginePauseRequested;
        }

        /// <summary>The engine process and what it last reported (item pool, world, versions).</summary>
        public LogicEngineManager Engine { get; }

        /// <summary>The engine is running for this slot.</summary>
        public bool Running { get; private set; }

        /// <summary>The engine is starting.</summary>
        public bool Booting { get; private set; }

        /// <summary>Logic has a first answer, or there won't be one (no locations, or the engine couldn't start).</summary>
        public bool Loaded { get; private set; }

        /// <summary>Logic is running and has worked out every item received so far (not starting or rebuilding).</summary>
        public bool Settled => Running && _startingDone && !_busy && !_dirty && Problem == null;

        /// <summary>Why logic isn't running (null while it runs or starts).</summary>
        public EngineStartError? Problem { get; private set; }

        /// <summary>The engine's state in a few words, for the status line ("Engine Running", "Restarting…"); null before it starts.</summary>
        public string? Status { get; private set; }

        /// <summary>Whether the slot's goal can be completed with what it has now (go mode). Null: not known.</summary>
        public bool? GoalInLogic { get; private set; }

        /// <summary>
        /// Whether the installed apworld's data matches the seed's (checksums of names, ids and groups; null when either
        /// side didn't report one). It can't see rule-only changes, which the location check and seed tests cover.
        /// </summary>
        public bool? ApworldMatchesSeed { get; private set; }

        /// <summary>Every location logic has reached so far (it isn't told when one is checked).</summary>
        public IReadOnlySet<long> Reachable => _reachable;

        /// <summary>The steps so far: what was open from the start, then what each progression item opened, in order.</summary>
        public IReadOnlyList<LogicStep> Steps => _steps;

        /// <summary>How many of the slot's progression items logic has worked out.</summary>
        public int EvaluatedItems => _evaluated;

        /// <summary>Changes whenever the engine starts or stops, so a view knows its results were replaced, not added to.</summary>
        public int Run => _run;

        /// <summary>The slot's own engine failures in the last ten minutes (a fourth pauses logic); a lost engine it shares doesn't count.</summary>
        public int RecentFailures => _recentFailures.Count;

        /// <summary>The seed excluded this location (as the engine read the seed's options).</summary>
        public bool ExcludedBySeed(long location) => Engine.LastExcludedLocations?.Contains(location) == true;

        /// <summary>The location is reachable only with glitches, as of the last answer.</summary>
        public bool IsGlitched(long location) => Engine.LastGlitchedLocations?.Contains(location) == true;

        /// <summary>The player YAML linked to this slot, if the file still exists.</summary>
        public string? LinkedYamlPath =>
            _settings.SlotYamlPaths != null && _settings.SlotYamlPaths.TryGetValue(Annotations.SlotKey(_model.ProfileId, _model.SlotName), out var path) &&
            System.IO.File.Exists(path) ? path : null;

        /// <summary>The seed's apworld (Atlas's cached copy) failed to load in this engine, so it isn't offered again.</summary>
        public bool SeedApworldFailed(string checksum) => _seedApworldFailed.Contains(checksum);

        /// <summary>Offers the seed's apworld again (the user supplied a new copy).</summary>
        public void RetrySeedApworld(string checksum) => _seedApworldFailed.Remove(checksum);

        private ArchipelagoSession Session => _model.Session;

        private string? ServerChecksum =>
            _model.DataChecksums.TryGetValue(_model.Game, out var checksum) && !string.IsNullOrEmpty(checksum) ? checksum : null;

        // =====================================================================
        // Starting, restarting, stopping
        // =====================================================================

        /// <summary>Starts the engine for this slot (when it isn't running or starting already).</summary>
        public void Start() => Async.Fire(StartAsync(), $"starting logic for {_model.SlotName}");

        private async Task StartAsync()
        {
            if (_disposed || Booting || Running) return;
            if (AtlasEngine.SetupRunning)
            {
                // Never start an engine whose files may be changing; setup's Changed event brings us back.
                Problem = Paused();
                Status = "Logic paused while the engine updates";
                Notify();
                return;
            }
            if (Session.Locations.AllLocations.Count == 0)
            {
                _log("InitializeLogicEngine: 0 locations detected (TextOnly client). Skipping engine.");
                Status = "Nothing to track (text only)";
                Loaded = true;
                Notify();
                return;
            }

            int run = ++_run;
            Booting = true;
            Problem = null;
            Engine.SetInstall(AtlasEngine.Resolve(_settings));
            Status = "Starting the logic engine…";
            Notify();
            try
            {
                _log($"InitializeLogicEngine: StartEngineAsync Game='{_model.Game}', Slot='{_model.SlotName}', engine={Engine.Install.Describe()}");
                // No ConfigureAwait(false): the continuation must resume on Godot's main thread.
                bool started = await Engine.StartEngineAsync(_model.Game, _model.SlotName, _model.PlayerSlot, _model.SlotData, Session.Locations.AllLocations,
                    LinkedYamlPath, SeedApworldFile(), ServerChecksum);
                if (_disposed || run != _run) return; // stopped meanwhile (an engine update, or the slot ended)
                _log($"InitializeLogicEngine: StartEngineAsync returned {started}");
                if (started)
                {
                    Running = true;
                    ReportStart();
                    Notify(SlotChange.Logic | SlotChange.EngineStarted);
                    Refresh();
                }
                else if (Engine.LastStartLoss is { Mine: false } loss)
                {
                    // The engine this slot shares was lost answering another slot: not this slot's failure, so it starts again.
                    RestartAfter(SharedLoss(loss), RestartDelaysSeconds[0]);
                    Loaded = true;
                }
                else
                {
                    Problem = Engine.LastStartError ?? new EngineStartError { Code = "error", Message = "The logic engine couldn't start." };
                    Status = ProblemStatus(Problem);
                    _log($"Logic engine not started ({Problem.Code}): {Problem.Message}" +
                         (Problem.Details != null ? "\n" + Problem.Details.ToString(Newtonsoft.Json.Formatting.None) : ""));
                    Loaded = true;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (_disposed || run != _run) return;
                _log("Logic Engine Init Error: " + ex.Message);
                Problem = new EngineStartError { Code = "error", Message = ex.Message };
                Status = "Engine error";
                Loaded = true;
            }
            finally
            {
                if (run == _run) Booting = false;
                if (!_disposed) Notify();
            }
        }

        /// <summary>Starts the engine again from scratch (after setup, a new YAML or apworld, or a failure), with a fresh set of automatic restarts.</summary>
        public void Restart()
        {
            if (_disposed || Booting) return;
            StopRun();
            _recentFailures.Clear(); // the user asked: give it a fresh set of automatic restarts
            Start();
        }

        /// <summary>Ends the current engine run: stops the engine and forgets its results. Anything still on its way from it is ignored.</summary>
        private void StopRun()
        {
            _run++;
            Running = false;
            Booting = false;
            _busy = false;
            _dirty = false;
            Engine.StopEngine();
            _evaluated = 0;
            _startingDone = false;
            _inventory.Clear();
            _steps.Clear();
            _reachable.Clear();
            _explainCache.Clear();
            GoalInLogic = null;
            Notify();
        }

        /// <summary>Atlas's cached copy of the seed's apworld version, if it has one that loads (used in place of the installed copy).</summary>
        private string? SeedApworldFile()
        {
            string? checksum = ServerChecksum;
            if (checksum == null || _seedApworldFailed.Contains(checksum)) return null;
            // The version the user chose for this slot's seed comes first (even one whose data differs: they chose it).
            if (_settings.SlotApworlds != null && _settings.SlotApworlds.TryGetValue(Annotations.SlotKey(_model.ProfileId, _model.SlotName), out var chosen)
                && string.Equals(chosen.SeedChecksum, checksum, StringComparison.OrdinalIgnoreCase) && string.Equals(chosen.Game, _model.Game, StringComparison.OrdinalIgnoreCase)
                && System.IO.File.Exists(chosen.File))
                return chosen.File;
            return ApworldSources.CachedFor(_model.Game, checksum)?.File;
        }

        /// <summary>Says how the world was rebuilt, and warns when it or the apworld doesn't match the seed.</summary>
        private void ReportStart()
        {
            string game = _model.Game, slot = _model.SlotName;
            var info = Engine.LastYamlInfo;
            string? source = info?["source"]?.ToString();
            string? file = info?["file"]?.ToString();
            bool? match = info?["match"]?.Type == JTokenType.Boolean ? (bool?)info["match"] : null;
            string how = source switch
            {
                "not_needed" => "from the server's data (no YAML needed)",
                "slot_data" => "from the options in the server's slot data",
                "linked" => $"from your linked YAML {file}",
                "players" => $"from {file} in the Players folder",
                _ => "by the engine"
            };
            _log($"Logic engine running: world rebuilt {how}; locations {info?["got"]} of {info?["expected"]} expected, {info?["missing"]} missing, {info?["extra"]} extra.");
            string? serverChecksum = ServerChecksum, localChecksum = Engine.LastDataChecksum;
            ApworldMatchesSeed = serverChecksum == null || localChecksum == null ? null : serverChecksum == localChecksum;
            var applied = Engine.LastApworldOverride;
            if (applied?["used"]?.Type == JTokenType.Boolean && (bool)applied["used"]!)
                _log($"Using {game} {applied["world_version"]} ({applied["file"]}) from Atlas's cache for this seed, in place of the installed copy (which isn't changed).");
            else if (applied?["error"] != null)
            {
                // The cached version couldn't load in this engine: don't offer it again this session.
                _seedApworldFailed.Add(serverChecksum ?? "");
                Logger.LogWarning($"[{slot}] The seed's {game} apworld couldn't be loaded in this engine ({applied["error"]}); using the installed copy.");
            }
            if (ApworldMatchesSeed == false)
            {
                string version = Engine.LastWorldVersion != null ? $" (version {Engine.LastWorldVersion})" : "";
                Status = "Logic running (apworld differs from the seed's)";
                Logger.LogWarning($"[{slot}] The installed {game} apworld{version} isn't the one this seed was generated with: its data " +
                    $"(checksum {Short(localChecksum)}) differs from the server's ({Short(serverChecksum)}). " +
                    "Atlas will look for the seed's version.");
                return;
            }
            if (ApworldMatchesSeed == true) _log($"The {game} apworld in use matches the seed's data (checksum {Short(localChecksum)}).");
            if (match == false)
            {
                int missing = info!["missing"]?.ToObject<int>() ?? 0, extra = info["extra"]?.ToObject<int>() ?? 0;
                Status = $"Logic running (world differs: {missing} missing, {extra} extra)";
                Logger.LogWarning($"[{slot}] The rebuilt world doesn't match the server ({missing} locations missing, {extra} extra). Logic may be off. " +
                    "Linking the YAML used to generate the seed, or matching the game's apworld version, usually fixes this.");
            }
            else Status = "Logic running";
        }

        /// <summary>A checksum's first 8 characters, for messages.</summary>
        private static string Short(string? checksum) => checksum == null ? "" : checksum[..Math.Min(8, checksum.Length)];

        private static string ProblemStatus(EngineStartError e) => e.Code switch
        {
            "no_engine" => "Logic needs the Atlas Engine",
            "world_missing" => "The game isn't installed in the engine",
            "yaml_needed" => "Logic needs the player's YAML",
            "generation_failed" => "The world couldn't be rebuilt",
            "ut_disabled" => "Logic is disabled by the game",
            "no_response" => "The engine didn't answer",
            "crashed" => "The engine crashed",
            _ => "Engine error"
        };

        // =====================================================================
        // Working out logic, item by item
        // =====================================================================

        /// <summary>Works out logic for what arrived since the last time (items or checks). Bursts are coalesced: never two at once.</summary>
        public void Refresh()
        {
            if (_disposed || !Running) return;
            if (_busy)
            {
                _dirty = true;
                return;
            }
            Async.Fire(EvaluateAsync(_run), $"updating logic for {_model.SlotName}");
        }

        private async Task EvaluateAsync(int run)
        {
            _busy = true;
            try
            {
                do
                {
                    _dirty = false;
                    if (!await EvaluateStepsAsync(run)) return;
                } while (_dirty);
                // The last answer was for everything received so far, so its goal flag describes the slot now.
                GoalInLogic = Engine.LastGoalReachable;
                Loaded = true;
                Notify();
            }
            catch (LogicEngineFailure failure)
            {
                // A lost engine says whose failure it was: another slot's request may have brought down the engine they share.
                var loss = Engine.EngineLoss;
                Fail(run, loss is { Mine: false } ? SharedLoss(loss) : failure.Message, counted: loss?.Mine ?? true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (run != _run) return;
                _log($"UpdateLogic FATAL EXCEPTION: {ex}");
                CrashGuard.Record($"[{_model.SlotName}] Logic evaluation failed", ex);
                Fail(run, "an internal error while reading logic (" + ex.Message + ")");
            }
            finally
            {
                if (run == _run) _busy = false;
            }
        }

        /// <summary>
        /// Asks the engine what each new progression item opened, in the order they arrived, a few items per request. False
        /// when the run ended meanwhile (nothing more was recorded).
        /// </summary>
        private async Task<bool> EvaluateStepsAsync(int run)
        {
            var missing = Session.Locations.AllLocations.Except(Session.Locations.AllLocationsChecked).ToList();
            if (missing.Count == 0) return true;

            var poolProgression = PoolProgressionIds();
            var progression = Session.Items.AllItemsReceived
                .Where(i => i.Flags.HasFlag(ItemFlags.Advancement) || i.Flags.HasFlag(ItemFlags.NeverExclude) || poolProgression.Contains(i.ItemId))
                .ToList();
            if (progression.Count == _evaluated && _startingDone) return true;

            bool first = true;
            do
            {
                bool start = !_startingDone;
                var chunk = progression.Skip(_evaluated).Take(StepsPerRequest).ToList();
                // A failed answer stops here, before anything of it is recorded: those items are worked out again after recovery.
                var answer = await Engine.GetStepsAsync(_inventory.ToList(), chunk.Select(item => item.ItemId).ToList(), first ? missing : null, start);
                if (run != _run) return false;
                if (answer == null) throw new LogicEngineFailure(Engine.LastQueryFailure);
                first = false;
                using var __perf = PerfMonitor.Measure($"[{_model.SlotName}] Logic step");
                if (start)
                {
                    _startingDone = true;
                    if (answer.Start.Count > 0)
                    {
                        _reachable.UnionWith(answer.Start);
                        // Excluded checks stay in the steps (hidden when shown), so changing an exclusion needs no re-run.
                        _steps.Add(new LogicStep(LogicStep.StartName, answer.Start.ToList()));
                    }
                }
                for (int i = 0; i < chunk.Count; i++)
                {
                    _inventory.Add(chunk[i].ItemId);
                    _evaluated++;
                    var opened = answer.Opened[i].Where(_reachable.Add).ToList();
                    if (opened.Count > 0) _steps.Add(new LogicStep(Session.Items.GetItemName(chunk[i].ItemId) ?? "Unknown Item", opened));
                }
            } while (_evaluated < progression.Count);
            return true;
        }

        private List<WorldItemInfo>? _poolProgressionSource;
        private HashSet<long> _poolProgression = new();

        /// <summary>
        /// Items the world itself classes as progression. Items an admin sends with a server command (/send) arrive with no
        /// flags, so the world's own classification counts as well as the server's flags. The pool only changes when the
        /// engine starts, and every engine start begins with a reset, so the evaluated list stays in step.
        /// </summary>
        private HashSet<long> PoolProgressionIds()
        {
            var pool = Engine.LastItemPool;
            if (!ReferenceEquals(pool, _poolProgressionSource))
            {
                _poolProgressionSource = pool;
                _poolProgression = pool == null ? new HashSet<long>() : new HashSet<long>(pool.Where(p => (p.Flags & 1) != 0).Select(p => p.Id));
            }
            return _poolProgression;
        }

        /// <summary>The engine's "why" for a location, cached until this slot's items change. Null when the engine isn't running.</summary>
        public Task<LogicExplanation?> ExplainAsync(long locationId)
        {
            if (_disposed || !Running) return Task.FromResult<LogicExplanation?>(null);
            if (_explainCacheItems != _evaluated || _busy)
            {
                _explainCache.Clear();
                _explainCacheItems = _evaluated;
            }
            if (!_explainCache.TryGetValue(locationId, out var task))
            {
                task = Engine.ExplainLocationAsync(locationId);
                _explainCache[locationId] = task;
            }
            return task;
        }

        /// <summary>Forgets the cached "why" answers (race rules changed).</summary>
        public void ForgetExplanations() => _explainCache.Clear();

        // =====================================================================
        // Failures, and the engine's setup changing
        // =====================================================================

        private sealed class LogicEngineFailure : Exception
        {
            public LogicEngineFailure(string? reason) : base(string.IsNullOrEmpty(reason) ? "the logic engine stopped answering" : reason) { }
        }

        /// <summary>The slot's engine was lost: it crashed or got stuck (any thread).</summary>
        private void OnEngineLost(EngineLoss loss)
        {
            int run = Volatile.Read(ref _run);
            string reason = loss.Mine ? "the engine " + loss.Reason : SharedLoss(loss);
            AP_Atlas.UI.Ui.Defer(null, () => Fail(run, reason, counted: loss.Mine), $"handling {_model.SlotName}'s logic engine stopping");
        }

        private static string SharedLoss(EngineLoss loss) => $"the logic engine it shares with {loss.Culprit ?? "another slot"} stopped ({loss.Reason})";

        /// <summary>
        /// The engine failed mid-session (a failed answer, or the engine was lost: one failure usually shows both ways, and
        /// is handled once). Nothing half-done is kept: logic is reset and rebuilt from scratch, after 2 s, 10 s, then 30 s.
        /// A fourth failure of the slot's own within ten minutes pauses logic with an explanation rather than restarting
        /// forever. An engine lost while answering another slot that shares it isn't this slot's failure: it starts again
        /// after 2 s, and it doesn't count.
        /// </summary>
        private void Fail(int run, string reason, bool counted = true)
        {
            if (_disposed || run != _run || !Running) return; // an engine since stopped, or this failure was already handled
            _log("Logic engine failure: " + reason + (counted ? "" : " (not this slot's)"));
            StopRun();
            Loaded = true;
            if (AtlasEngine.SetupRunning)
            {
                // Stopped for an engine update, not a crash: wait for the update instead of counting a failure.
                Problem = Paused();
                Status = "Logic paused while the engine updates";
                Notify();
                return;
            }

            var now = SteadyClock.UtcNow;
            _recentFailures.RemoveAll(t => (now - t).TotalMinutes > 10);
            if (counted) _recentFailures.Add(now);
            int failures = _recentFailures.Count;
            string slot = _model.SlotName;
            if (!counted) RestartAfter(reason, RestartDelaysSeconds[0]);
            else if (failures <= RestartDelaysSeconds.Length) RestartAfter(reason, RestartDelaysSeconds[failures - 1]);
            else
            {
                Problem = new EngineStartError { Code = "crashed", Message = $"The logic engine stopped {failures} times in 10 minutes ({reason}). Logic is paused so it can't show anything wrong. The slot's Debug Log has details." };
                Status = "Logic paused after repeated failures";
                Logger.LogError($"[{slot}] The logic engine failed {failures} times in 10 minutes ({reason}); logic is paused.");
            }
            Notify();
        }

        /// <summary>Says logic stopped, and starts it again after <paramref name="seconds"/>.</summary>
        private void RestartAfter(string reason, int seconds)
        {
            string slot = _model.SlotName;
            Problem = new EngineStartError { Code = "restarting", Message = $"The logic engine stopped ({reason}). Restarting it in {seconds} s; logic will be rebuilt from scratch." };
            Status = "Restarting the logic engine…";
            Logger.LogWarning($"[{slot}] The logic engine stopped ({reason}); restarting in {seconds} s.");
            Async.Fire(RestartLaterAsync(_run, seconds), $"restarting logic for {slot}");
        }

        private async Task RestartLaterAsync(int run, int seconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            // Not if anything happened meanwhile (the user restarted it, an update paused it, the slot ended).
            if (_disposed || run != _run || Running || Booting || Problem?.Code != "restarting") return;
            await StartAsync();
        }

        /// <summary>The engine this slot uses is about to be updated (any thread): stop it now; logic resumes when the update is done.</summary>
        private void OnEnginePauseRequested(string root)
        {
            var install = Engine.Install;
            if (install == null || !string.Equals(System.IO.Path.GetFullPath(install.Root ?? "").TrimEnd('\\'), System.IO.Path.GetFullPath(root ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            // Right away: setup waits for the process to exit before replacing files.
            Engine.StopEngine();
            AP_Atlas.UI.Ui.Defer(null, () =>
            {
                if (_disposed) return;
                bool wasActive = Running || Booting;
                StopRun();
                if (wasActive || Problem != null)
                {
                    Problem = Paused();
                    Status = "Logic paused while the engine updates";
                    Notify();
                }
            }, $"pausing {_model.SlotName}'s logic for an engine update");
        }

        /// <summary>
        /// A part of the engine changed (any thread): a slot whose logic runs on that engine restarts it on the new
        /// parts (its pool has retired the process that loaded the old ones), without a reconnect. An apworld change
        /// concerns only the slots of its game.
        /// </summary>
        private void OnEnginePartsChanged(IReadOnlyList<AtlasEngine.EngineChange> changes) => AP_Atlas.UI.Ui.Defer(null, () =>
        {
            if (_disposed || !(Running || Booting)) return;
            if (!changes.Any(Concerns)) return;
            var parts = changes.Where(Concerns).Select(c => c.Kind == "apworld" ? $"{c.Game}'s apworld" : c.Kind).Distinct().ToList();
            _log($"Restarting {_model.SlotName}'s logic: the engine's parts changed ({string.Join(", ", parts)}).");
            Status = "Restarting logic on the updated engine…";
            if (Booting) StopRun();
            Restart();
        }, $"restarting {_model.SlotName}'s logic on the updated engine");

        private bool Concerns(AtlasEngine.EngineChange change)
        {
            if (!string.Equals(FullRoot(change.Root), FullRoot(Engine.Install.Root), StringComparison.OrdinalIgnoreCase)) return false;
            return change.Kind != "apworld" || change.Game == null || string.Equals(change.Game, _model.Game, StringComparison.OrdinalIgnoreCase);
        }

        private static string FullRoot(string root)
        {
            try { return System.IO.Path.GetFullPath(string.IsNullOrEmpty(root) ? "." : root).TrimEnd('\\', '/'); }
            catch (ArgumentException) { return root ?? ""; }
        }

        /// <summary>Setup finished or changed (any thread): a slot that was waiting on the engine tries again.</summary>
        private void OnEngineSetupChanged() => AP_Atlas.UI.Ui.Defer(null, () =>
        {
            if (_disposed || Running || Booting || Problem == null) return;
            if (Problem.Code is "no_engine" or "world_missing" or "no_response" or "crashed" or "error" or "paused") Start();
        }, $"restarting {_model.SlotName}'s logic after an engine change");

        private void Notify(SlotChange change = SlotChange.Logic) => _model.Report(change);

        /// <summary>Stops the engine and listening to Atlas's engine setup (the slot ended). Safe to call more than once.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _run++;
            AtlasEngine.Changed -= OnEngineSetupChanged;
            AtlasEngine.PartsChanged -= OnEnginePartsChanged;
            AtlasEngine.PauseRequested -= OnEnginePauseRequested;
            Engine.EngineLost -= OnEngineLost;
            Engine.StopEngine();
        }
    }
}
