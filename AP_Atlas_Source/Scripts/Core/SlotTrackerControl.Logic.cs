#nullable disable
using Godot;
using System;
using System.Collections.Generic;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Packets;
using Archipelago.MultiClient.Net.Models;
using System.Linq;
using System.Threading.Tasks;
using Color = Godot.Color;

/// <summary>The logic engine for this slot: starting it, updating logic, failures and restarts, and YAML exclusions.</summary>
public partial class SlotTrackerControl : MarginContainer
{
    // =====================================================================
    // Logic engine: starting, updating, failures and restarts; exclusions
    // =====================================================================

    private void InitializeLogicEngine() => AP_Atlas.Core.Async.Fire(InitializeLogicEngineAsync(), $"starting logic for {_slotName}");

    private async Task InitializeLogicEngineAsync()
    {
        if (_engineBooting || _engineRunning) return;
        if (AP_Atlas.Core.EngineSetup.AtlasEngine.SetupRunning)
        {
            // Never start an engine whose files may be changing; setup's Changed event brings us back.
            EngineProblem = new EngineStartError { Code = "paused", Message = "Logic is paused while the Atlas Engine is updated. It resumes by itself when the update finishes." };
            SetStatus("Paused (engine update)");
            SyncLogicViews();
            return;
        }
        if (Session.Locations.AllLocations.Count == 0)
        {
            AppendDebugLog("InitializeLogicEngine: 0 locations detected (TextOnly client). Skipping engine.");
            SetStatus("No Locations (TextOnly)");
            IsFullyLoaded = true;
            RaiseStateChanged();
            return;
        }

        _engineBooting = true;
        EngineProblem = null;
        _logicEngine.SetInstall(AP_Atlas.Core.EngineSetup.AtlasEngine.Resolve(_appSettings));
        SyncLogicViews();
        SetStatus("Booting Engine...");
        try
        {
            AppendDebugLog($"InitializeLogicEngine: StartEngineAsync Game='{Session.ConnectionInfo.Game}', Slot='{_slotName}', engine={_logicEngine.Install.Describe()}");
            // No ConfigureAwait(false): the continuation must resume on Godot's main thread.
            bool started = await _logicEngine.StartEngineAsync(
                Session.ConnectionInfo.Game, _slotName, Session.ConnectionInfo.Slot, _slotData, Session.Locations.AllLocations, LinkedYamlPath,
                SeedApworldFile(), ServerChecksumFor(Game));

            if (!GodotObject.IsInstanceValid(this)) return;
            AppendDebugLog($"InitializeLogicEngine: StartEngineAsync returned {started}");

            if (started)
            {
                _engineRunning = true;
                ReportEngineStart();
                QueueLogicRefresh();
                UpdateItemHistoryUI(); // item pool is now available for "Not Yet Collected"
                RebuildPackIndex();    // ...and item names for pairing pack items that have no mapping
                UpdateKeyItemsUI();
            }
            else
            {
                EngineProblem = _logicEngine.LastStartError ?? new EngineStartError { Code = "error", Message = "The logic engine couldn't start." };
                SetStatus(ProblemStatus(EngineProblem));
                AppendDebugLog($"Logic engine not started ({EngineProblem.Code}): {EngineProblem.Message}" +
                               (EngineProblem.Details != null ? "\n" + EngineProblem.Details.ToString(Newtonsoft.Json.Formatting.None) : ""));
                IsFullyLoaded = true;
            }
        }
        catch (Exception ex)
        {
            AppendDebugLog("Logic Engine Init Error: " + ex.Message);
            EngineProblem = new EngineStartError { Code = "error", Message = ex.Message };
            SetStatus("Engine Error");
            IsFullyLoaded = true;
        }
        finally
        {
            _engineBooting = false;
            if (GodotObject.IsInstanceValid(this))
            {
                SyncLogicViews();
                RaiseStateChanged();
            }
        }
    }

    /// <summary>Says how the world was rebuilt, and warns when its locations don't match the server's.</summary>
    private void ReportEngineStart()
    {
        var info = _logicEngine.LastYamlInfo;
        string source = info?["source"]?.ToString();
        string file = info?["file"]?.ToString();
        bool? match = info?["match"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean ? (bool?)info["match"] : null;
        string how = source switch
        {
            "not_needed" => "from the server's data (no YAML needed)",
            "slot_data" => "from the options in the server's slot data",
            "linked" => $"from your linked YAML {file}",
            "players" => $"from {file} in the Players folder",
            _ => "by the engine"
        };
        AppendDebugLog($"Logic engine running: world rebuilt {how}; locations {info?["got"]} of {info?["expected"]} expected, {info?["missing"]} missing, {info?["extra"]} extra.");
        string serverChecksum = ServerChecksumFor(Game), localChecksum = _logicEngine.LastDataChecksum;
        ApworldMatchesSeed = serverChecksum == null || localChecksum == null ? null : serverChecksum == localChecksum;
        var applied = _logicEngine.LastApworldOverride;
        if (applied?["used"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && (bool)applied["used"])
            AppendDebugLog($"Using {Game} {applied["world_version"]} ({applied["file"]}) from Atlas's cache for this seed, in place of the installed copy (which isn't changed).");
        else if (applied?["error"] != null)
        {
            // The cached version couldn't load in this engine: don't offer it again this session.
            _apworldOverrideFailed.Add(serverChecksum ?? "");
            AP_Atlas.Core.Logger.LogWarning($"[{_slotName}] The seed's {Game} apworld couldn't be loaded in this engine ({applied["error"]}); using the installed copy.");
        }
        if (ApworldMatchesSeed == false)
        {
            string version = _logicEngine.LastWorldVersion != null ? $" (version {_logicEngine.LastWorldVersion})" : "";
            SetStatus("Running (apworld differs from the seed's)");
            AP_Atlas.Core.Logger.LogWarning($"[{_slotName}] The installed {Game} apworld{version} isn't the one this seed was generated with: its data " +
                $"(checksum {localChecksum[..Math.Min(8, localChecksum.Length)]}) differs from the server's ({serverChecksum[..Math.Min(8, serverChecksum.Length)]}). " +
                "Atlas will look for the seed's version.");
            SyncAccuracyBanner();
            // Fix it without asking when the download source is already trusted (or the version is cached).
            if (_appSettings.AutoFixApworldVersions && !_apworldFixAttempted.Contains(serverChecksum))
                FixApworldVersion(interactive: false);
            return;
        }
        if (ApworldMatchesSeed == true) AppendDebugLog($"The {Game} apworld in use matches the seed's data (checksum {localChecksum[..Math.Min(8, localChecksum.Length)]}).");
        if (match == false)
        {
            int missing = info["missing"]?.ToObject<int>() ?? 0, extra = info["extra"]?.ToObject<int>() ?? 0;
            SetStatus($"Running (world differs: {missing} missing, {extra} extra)");
            AP_Atlas.Core.Logger.LogWarning($"[{_slotName}] The rebuilt world doesn't match the server ({missing} locations missing, {extra} extra). Logic may be off. " +
                "Linking the YAML used to generate the seed, or matching the game's apworld version, usually fixes this.");
        }
        else SetStatus("Engine Running");
        SyncAccuracyBanner();
    }

    /// <summary>Coalesces refresh requests so concurrent item bursts never run the engine loop twice in parallel.</summary>
    private void QueueLogicRefresh()
    {
        if (!_engineRunning) return;
        if (_logicBusy) { _logicDirty = true; return; }
        UpdateLogic();
    }

    private void UpdateLogic() => AP_Atlas.Core.Async.Fire(UpdateLogicAsync(), $"updating logic for {_slotName}");

    private async Task UpdateLogicAsync()
    {
        _logicBusy = true;
        try
        {
            do
            {
                _logicDirty = false;
                await EvaluateLogicStepAsync();
                if (!GodotObject.IsInstanceValid(this)) return;
            } while (_logicDirty);
            // The last answer was for everything received so far, so its goal flag describes the slot now.
            GoalInLogic = _logicEngine.LastGoalReachable;

            RenderLogicTree();
            IsFullyLoaded = true;
            RaiseStateChanged();
        }
        catch (LogicEngineFailure failure)
        {
            HandleEngineFailure(failure.Message);
        }
        catch (Exception ex)
        {
            AppendDebugLog($"UpdateLogic FATAL EXCEPTION: {ex}");
            AP_Atlas.Core.CrashGuard.Record($"[{_slotName}] Logic evaluation failed", ex);
            HandleEngineFailure("an internal error while reading logic (" + ex.Message + ")");
        }
        finally
        {
            _logicBusy = false;
        }
    }

    private sealed class LogicEngineFailure : Exception
    {
        public LogicEngineFailure(string reason) : base(string.IsNullOrEmpty(reason) ? "the logic engine stopped answering" : reason) { }
    }

    private bool _startingLogicDone;
    private readonly List<DateTime> _recentEngineFailures = new List<DateTime>();
    private static readonly int[] RestartDelaysSeconds = { 2, 10, 30 };

    /// <summary>
    /// The engine failed mid-session. Nothing half-done is kept: logic is reset and rebuilt from scratch on a fresh
    /// engine, after 2 s, 10 s, then 30 s. A fourth failure within ten minutes pauses logic with an explanation
    /// rather than restarting forever.
    /// </summary>
    private void HandleEngineFailure(string reason)
    {
        if (!GodotObject.IsInstanceValid(this) || Session == null) return;
        AppendDebugLog("Logic engine failure: " + reason);
        _engineRunning = false;
        _logicEngine.StopEngine();
        ResetLogicState();
        if (AP_Atlas.Core.EngineSetup.AtlasEngine.SetupRunning)
        {
            // Stopped for an engine update, not a crash: wait for the update instead of counting a failure.
            EngineProblem = new EngineStartError { Code = "paused", Message = "Logic is paused while the Atlas Engine is updated. It resumes by itself when the update finishes." };
            SetStatus("Paused (engine update)");
            SyncLogicViews();
            RaiseStateChanged();
            return;
        }

        var now = DateTime.Now;
        _recentEngineFailures.RemoveAll(t => (now - t).TotalMinutes > 10);
        _recentEngineFailures.Add(now);
        int failures = _recentEngineFailures.Count;
        if (failures <= RestartDelaysSeconds.Length)
        {
            int delay = RestartDelaysSeconds[failures - 1];
            EngineProblem = new EngineStartError { Code = "restarting", Message = $"The logic engine stopped ({reason}). Restarting it in {delay} s; logic will be rebuilt from scratch." };
            SetStatus("Restarting…");
            AP_Atlas.Core.Logger.LogWarning($"[{_slotName}] The logic engine stopped ({reason}); restarting in {delay} s.");
            GetTree().CreateTimer(delay).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(this) && !_engineRunning && !_engineBooting && EngineProblem?.Code == "restarting") InitializeLogicEngine();
            };
        }
        else
        {
            EngineProblem = new EngineStartError { Code = "crashed", Message = $"The logic engine stopped {failures} times in 10 minutes ({reason}). Logic is paused so it can't show anything wrong. The slot's Debug Log has details." };
            SetStatus("Engine Paused (repeated failures)");
            AP_Atlas.Core.Logger.LogError($"[{_slotName}] The logic engine failed {failures} times in 10 minutes ({reason}); logic is paused.");
        }
        IsFullyLoaded = true;
        SyncLogicViews();
        RaiseStateChanged();
    }

    /// <summary>Forgets every logic result, so the next evaluation rebuilds the Logic Tracker from scratch.</summary>
    private void ResetLogicState()
    {
        _lastEvaluatedItemCount = 0;
        _startingLogicDone = false;
        _chronologicalInventory.Clear();
        _progressionLog.Clear();
        _knownReachableLocations.Clear();
        _explainCache.Clear();
        GoalInLogic = null;
        RenderLogicTree(forceFull: true);
    }

    private async System.Threading.Tasks.Task EvaluateLogicStepAsync()
    {
        var missingLocs = Session.Locations.AllLocations.Except(Session.Locations.AllLocationsChecked).ToList();
        if (missingLocs.Count == 0) return;

        var poolProgression = PoolProgressionIds();
        var currentProgression = Session.Items.AllItemsReceived
            .Where(i => i.Flags.HasFlag(ItemFlags.Advancement) || i.Flags.HasFlag(ItemFlags.NeverExclude) || poolProgression.Contains(i.ItemId))
            .ToList();

        if (currentProgression.Count == _lastEvaluatedItemCount && _startingLogicDone) return;

        if (_lastEvaluatedItemCount == 0 && _progressionLog.Count == 0 && !_startingLogicDone)
        {
            var initialReachable = await _logicEngine.GetReachableLocationsAsync(new List<long>(), missingLocs)
                ?? throw new LogicEngineFailure(_logicEngine.LastQueryFailure);
            _startingLogicDone = true;
            if (initialReachable.Count > 0)
            {
                _knownReachableLocations.UnionWith(initialReachable);
                // Excluded checks stay in the log (hidden when shown) so changing an exclusion needs no re-run.
                _progressionLog.Add(("Starting Logic", initialReachable.ToList()));
            }
        }

        for (int i = _lastEvaluatedItemCount; i < currentProgression.Count; i++)
        {
            var netItem = currentProgression[i];
            var inventory = new List<long>(_chronologicalInventory) { netItem.ItemId };

            // A failed answer stops here, before anything is recorded: the step is evaluated again after recovery.
            var stepReachable = await _logicEngine.GetReachableLocationsAsync(inventory, missingLocs)
                ?? throw new LogicEngineFailure(_logicEngine.LastQueryFailure);
            _chronologicalInventory.Add(netItem.ItemId);
            _lastEvaluatedItemCount = i + 1;

            var newlyUnlocked = new List<long>();
            foreach (var loc in stepReachable)
            {
                if (_knownReachableLocations.Add(loc)) newlyUnlocked.Add(loc);
            }
            if (newlyUnlocked.Count > 0)
            {
                string itemName = Session.Items.GetItemName(netItem.ItemId) ?? "Unknown Item";
                _progressionLog.Add((itemName, newlyUnlocked));
            }
        }

        _lastEvaluatedItemCount = currentProgression.Count;
    }

    private List<WorldItemInfo> _poolProgressionSource;
    private HashSet<long> _poolProgression = new HashSet<long>();

    /// <summary>
    /// Items the world itself classes as progression. Items an admin sends with a server command (/send) arrive with no
    /// flags, so the world's own classification counts as well as the server's flags. The pool only changes when the
    /// engine starts, and every engine start begins with a reset, so the evaluated list stays in step.
    /// </summary>
    private HashSet<long> PoolProgressionIds()
    {
        var pool = _logicEngine?.LastItemPool;
        if (!ReferenceEquals(pool, _poolProgressionSource))
        {
            _poolProgressionSource = pool;
            _poolProgression = pool == null ? new HashSet<long>() : new HashSet<long>(pool.Where(p => (p.Flags & 1) != 0).Select(p => p.Id));
        }
        return _poolProgression;
    }

    /// <summary>Excluded by your choice if you made one, else by the seed (as the logic engine reads its options).</summary>
    private bool IsExcluded(long loc) =>
        AP_Atlas.Core.Annotations.GetExclusionOverride(AnnotationKey, loc) ?? IsExcludedBySeed(loc);

    public bool IsExcludedBySeed(long loc) => _logicEngine?.LastExcludedLocations?.Contains(loc) == true;

    /// <summary>Why a location is excluded or included: "seed", "you", or null when it's a normal check.</summary>
    public string ExclusionSource(long loc)
    {
        var mine = AP_Atlas.Core.Annotations.GetExclusionOverride(AnnotationKey, loc);
        if (mine == true) return "you";
        if (mine == false) return IsExcludedBySeed(loc) ? "included by you" : null;
        return IsExcludedBySeed(loc) ? "seed" : null;
    }

    /// <summary>
    /// Excludes the locations a player YAML lists (location names or location group names).
    /// Returns how many were newly excluded and the names that matched nothing in this seed.
    /// </summary>
    public async System.Threading.Tasks.Task<(int Applied, int Listed, List<string> Unknown)> ApplyYamlExclusionsAsync(List<string> names)
    {
        var ids = new HashSet<long>();
        var unknown = new List<string>();
        if (Session == null) return (0, 0, names);
        var inSeed = new HashSet<long>(Session.Locations.AllLocations);
        Dictionary<string, string[]> groups = null;
        bool groupsFetched = false;
        foreach (var name in names)
        {
            long id = Session.Locations.GetLocationIdFromName(Game, name);
            if (id >= 0 && inSeed.Contains(id)) { ids.Add(id); continue; }
            if (id >= 0) continue; // a real location that this seed doesn't have (turned off by its options)
            if (!groupsFetched)
            {
                groups = await LocationGroupsAsync(Game);
                groupsFetched = true;
            }
            var group = groups?.FirstOrDefault(g => string.Equals(g.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (group == null) { unknown.Add(name); continue; }
            foreach (var member in group)
            {
                long memberId = Session.Locations.GetLocationIdFromName(Game, member);
                if (memberId >= 0 && inSeed.Contains(memberId)) ids.Add(memberId);
            }
        }
        if (!GodotObject.IsInstanceValid(this)) return (0, ids.Count, unknown);
        var toExclude = ids.Where(id => !IsExcluded(id)).ToList();
        if (toExclude.Count > 0)
        {
            // Seed-excluded ones just drop a manual "include"; the rest become your exclusions.
            var bySeed = toExclude.Where(IsExcludedBySeed).ToList();
            if (bySeed.Count > 0) AP_Atlas.Core.Annotations.SetExclusionOverrides(AnnotationKey, bySeed, null);
            var mine = toExclude.Where(id => !IsExcludedBySeed(id)).ToList();
            if (mine.Count > 0) AP_Atlas.Core.Annotations.SetExclusionOverrides(AnnotationKey, mine, true);
        }
        return (toExclude.Count, ids.Count, unknown);
    }

    /// <summary>Shown when the YAML linked to this slot lists excluded locations (once per distinct list).</summary>
    public Action<string, Color, string, Action> ShowActionToast { get; set; }

    /// <summary>
    /// Offers the excluded locations of the YAML the user linked to this slot. Atlas reads only that file: it never looks
    /// through folders for YAMLs.
    /// </summary>
    private void OfferYamlExclusions() => AP_Atlas.Core.Async.Fire(OfferYamlExclusionsAsync(), $"reading {_slotName}'s YAML");

    private async Task OfferYamlExclusionsAsync()
    {
        string path = LinkedYamlPath, game = Game, slot = _slotName;
        if (path == null) return;
        var yaml = await System.Threading.Tasks.Task.Run(() => AP_Atlas.Core.YamlExclusions.Read(path, game, slot));
        if (!GodotObject.IsInstanceValid(this) || Session == null || yaml.Error != null || yaml.Names.Count == 0) return;
        string signature = System.IO.Path.GetFileName(yaml.File) + "|" + string.Join("|", yaml.Names.OrderBy(n => n));
        if (!AP_Atlas.Core.Annotations.FirstOfferOfYamlExclusions(AnnotationKey, signature)) return;
        string file = System.IO.Path.GetFileName(yaml.File);
        AppendDebugLog($"[Exclusions] {file} lists {yaml.Names.Count} excluded location(s) for {slot}.");
        ShowActionToast?.Invoke($"{file} excludes {yaml.Names.Count} location(s) for {slot}. Apply them to the tracker?", Colors.Gray, "Apply", () => AP_Atlas.Core.Async.Fire(async () =>
        {
            var (applied, listed, unknown) = await ApplyYamlExclusionsAsync(yaml.Names);
            ShowToast?.Invoke($"Excluded {applied} location(s) from {file}" + (unknown.Count > 0 ? $" ({unknown.Count} name(s) not found)" : ""), Colors.Gray);
        }, $"applying {file}'s excluded locations"));
    }
}
