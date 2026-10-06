#nullable disable
using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Godot;
using Newtonsoft.Json.Linq;
using System.Linq;

public class WorldItemInfo
{
    [Newtonsoft.Json.JsonProperty("id")]
    public long Id { get; set; }

    [Newtonsoft.Json.JsonProperty("name")]
    public string Name { get; set; }

    [Newtonsoft.Json.JsonProperty("flags")]
    public int Flags { get; set; }
}

/// <summary>The bridge's answer to "why is this location (not) in logic?" for the current items.</summary>
public class LogicExplanation
{
    public class EntranceInfo
    {
        [Newtonsoft.Json.JsonProperty("name")] public string Name { get; set; } = "";
        [Newtonsoft.Json.JsonProperty("from")] public string From { get; set; } = "";
        [Newtonsoft.Json.JsonProperty("reachable")] public bool Reachable { get; set; }
        [Newtonsoft.Json.JsonProperty("rule")] public string Rule { get; set; } = "";
    }

    [Newtonsoft.Json.JsonProperty("error")] public string Error { get; set; }
    [Newtonsoft.Json.JsonProperty("location")] public string Location { get; set; } = "";
    [Newtonsoft.Json.JsonProperty("region")] public string Region { get; set; } = "";
    [Newtonsoft.Json.JsonProperty("progress_type")] public string ProgressType { get; set; } = "";
    [Newtonsoft.Json.JsonProperty("rule")] public string Rule { get; set; } = "";
    [Newtonsoft.Json.JsonProperty("in_logic")] public bool InLogic { get; set; }
    [Newtonsoft.Json.JsonProperty("region_reachable")] public bool RegionReachable { get; set; }
    [Newtonsoft.Json.JsonProperty("glitched")] public bool Glitched { get; set; }
    [Newtonsoft.Json.JsonProperty("entrances")] public List<EntranceInfo> Entrances { get; set; } = new List<EntranceInfo>();
    [Newtonsoft.Json.JsonProperty("candidates")] public int Candidates { get; set; }
    [Newtonsoft.Json.JsonProperty("unreachable_with_all")] public bool UnreachableWithAll { get; set; }
    [Newtonsoft.Json.JsonProperty("single_unlocks")] public List<string> SingleUnlocks { get; set; }
    [Newtonsoft.Json.JsonProperty("required")] public List<string> Required { get; set; }
    [Newtonsoft.Json.JsonProperty("partial")] public bool Partial { get; set; }
}

/// <summary>
/// The logic engine pools: one per multiworld and engine, whose few processes the multiworld's slots share
/// (AP_Atlas.Core.EngineSetup.EnginePool). An engine's own output goes to the debug log.
/// </summary>
public static class EnginePools
{
    private static readonly object Lock = new();
    private static readonly Dictionary<(string Multiworld, string Engine), AP_Atlas.Core.EngineSetup.EnginePool> Pools = new();

    /// <summary>
    /// For the UI test only (a guard rail checks): how many engines a multiworld's pool made from now on runs, in place of
    /// the number for this PC, so its slots share one. Null otherwise.
    /// </summary>
    internal static int? TestMaxEngines { get; set; }

    public static AP_Atlas.Core.EngineSetup.EnginePool For(string multiworld, AP_Atlas.Core.EngineSetup.EngineInstall install)
    {
        string engine = (install.Mode + ":" + Path.GetFullPath(string.IsNullOrEmpty(install.Root) ? "." : install.Root).TrimEnd('\\', '/')).ToLowerInvariant();
        lock (Lock)
        {
            if (!Pools.TryGetValue((multiworld, engine), out var pool))
            {
                Pools[(multiworld, engine)] = pool = new AP_Atlas.Core.EngineSetup.EnginePool(
                    () => AP_Atlas.Core.EngineSetup.EngineProcess.Start(install.StartInfo("UltimateBridge"), install.Root, line => AP_Atlas.Core.Logger.LogDebug("[Logic engine] " + line)),
                    TestMaxEngines ?? AP_Atlas.Core.EngineSetup.EnginePool.EnginesFor(System.Environment.ProcessorCount),
                    message => AP_Atlas.Core.Logger.LogInfo("[Logic engines] " + message));
            }
            return pool;
        }
    }

    /// <summary>
    /// Forgets a deleted multiworld's pools. Its slots have left them (each engine stopped with its last slot), and
    /// nothing would use them again: kept, they would pile up over a long session.
    /// </summary>
    public static void Forget(string multiworld)
    {
        lock (Lock)
            foreach (var key in Pools.Keys.Where(key => key.Multiworld == multiworld).ToList()) Pools.Remove(key);
    }

    /// <summary>How many pools a multiworld has (for the UI test).</summary>
    internal static int CountFor(string multiworld)
    {
        lock (Lock) return Pools.Keys.Count(key => key.Multiworld == multiworld);
    }
}

public class LogicEngineManager
{
    public List<WorldItemInfo> LastItemPool { get; private set; } = new List<WorldItemInfo>();

    private AP_Atlas.Core.EngineSetup.EngineInstall _install;
    private readonly Action<string> _logger;
    // Whose engines the slot shares (its multiworld), and its name among them.
    private readonly string _multiworld, _key;

    /// <param name="multiworld">The slot's multiworld: its slots share a few engine processes (see EnginePools).</param>
    /// <param name="key">The slot's name in its multiworld's engines.</param>
    public LogicEngineManager(AP_Atlas.Core.EngineSetup.EngineInstall install, Action<string> logger = null, string multiworld = null, string key = null)
    {
        _install = install;
        _logger = logger ?? (msg => GD.Print(msg));
        _multiworld = multiworld ?? "";
        _key = key;
    }

    public AP_Atlas.Core.EngineSetup.EngineInstall Install => _install;

    /// <summary>Points a stopped manager at another engine (the user switched engines or finished setup).</summary>
    public void SetInstall(AP_Atlas.Core.EngineSetup.EngineInstall install)
    {
        if (_seat is not { Running: true }) _install = install;
    }

    /// <summary>Why the last start failed (code: world_missing, yaml_needed, generation_failed, ut_disabled, no_engine, no_response, crashed), or null.</summary>
    public EngineStartError LastStartError { get; private set; }

    /// <summary>How the slot's world was rebuilt: which YAML (or none) and whether its locations match the server's.</summary>
    public JObject LastYamlInfo { get; private set; }

    /// <summary>Python / Archipelago / Universal Tracker versions the engine reported.</summary>
    public JObject LastVersions { get; private set; }

    /// <summary>Whether the seed's cached apworld was used in place of the installed one (used / matches / error), from the last start.</summary>
    public JObject LastApworldOverride { get; private set; }

    /// <summary>The installed apworld's data checksum (compare with the server's for the seed), from the last start.</summary>
    public string LastDataChecksum { get; private set; }

    /// <summary>The installed apworld's declared version, if it declares one.</summary>
    public string LastWorldVersion { get; private set; }

    // The slot's seat in one of its multiworld's engines (EnginePool), while it has one.
    private AP_Atlas.Core.EngineSetup.EngineSeat _seat;

    /// <summary>How the slot's engine was lost, if it was (whether the slot's own request brought it down).</summary>
    public AP_Atlas.Core.EngineSetup.EngineLoss EngineLoss => _seat?.Loss;

    /// <summary>From the last start that failed: how its engine was lost, if that's why (null otherwise).</summary>
    public AP_Atlas.Core.EngineSetup.EngineLoss LastStartLoss { get; private set; }

    public async Task<bool> StartEngineAsync(string game, string playerName, int slot, Dictionary<string, object> slotData, IEnumerable<long> allLocations = null, string yamlPath = null,
        string apworldOverride = null, string expectedChecksum = null)
    {
        if (_seat is { Running: true }) return true;
        LastStartError = null;
        LastStartLoss = null;
        LastGoalReachable = null;
        LastApworldOverride = null;
        string problem = AP_Atlas.Core.EngineSetup.AtlasEngine.ProblemWith(_install);
        if (problem != null)
        {
            LastStartError = new EngineStartError { Code = "no_engine", Message = problem };
            return false;
        }

        AP_Atlas.Core.EngineSetup.AtlasEngine.InstallBridge(_install, _logger);

        try
        {
            var seat = EnginePools.For(_multiworld, _install).Join(_key ?? playerName);
            _seat = seat;
            _logger($"Starting logic in logic engine {seat.EngineNumber} ({_install.Describe()})...");
            // Only this seat's loss counts: one the slot has since left says nothing.
            seat.Lost += loss =>
            {
                if (ReferenceEquals(seat, _seat)) EngineLost?.Invoke(loss);
            };

            var initReq = new Dictionary<string, object>
            {
                { "action", "init" },
                { "game", game },
                { "player_name", playerName },
                { "slot", slot },
                { "slot_data", slotData },
                { "all_locations", allLocations != null ? new List<long>(allLocations) : new List<long>() },
                { "yaml_path", string.IsNullOrEmpty(yamlPath) ? null : yamlPath },
                // The seed's apworld version from Atlas's cache, used for this slot in place of the installed copy.
                { "apworld_override", string.IsNullOrEmpty(apworldOverride) ? null : apworldOverride },
                { "expected_checksum", string.IsNullOrEmpty(expectedChecksum) ? null : expectedChecksum }
            };

            // Loading every game and rebuilding the world can take a while on a slow disk or a big install.
            var answer = await seat.AskAsync(JObject.FromObject(initReq), TimeSpan.FromMinutes(3));
            var response = answer.Reply;
            if (response == null)
            {
                LastStartError = answer.Failure switch
                {
                    AP_Atlas.Core.EngineSetup.EngineFailure.TimedOut => new EngineStartError { Code = "no_response", Message = "The engine didn't answer within 3 minutes." },
                    AP_Atlas.Core.EngineSetup.EngineFailure.CouldNotLoad => new EngineStartError { Code = "error", Message = "The engine couldn't load: " + answer.Why + ". The slot's debug log has the details." },
                    _ => new EngineStartError { Code = "crashed", Message = "The engine closed while starting (see the slot's debug log)." }
                };
                _logger("Logic Engine Start Failed: " + LastStartError.Message);
                LastStartLoss = seat.Loss;
                StopEngine();
                return false;
            }
            LastVersions = response["versions"] as JObject;
            LastApworldOverride = (response["yaml"]?["apworld_override"] ?? response["apworld_override"]) as JObject;

            if (response["status"]?.ToString() == "error")
            {
                LastStartError = new EngineStartError
                {
                    Code = response["code"]?.ToString() ?? "error",
                    Message = response["message"]?.ToString() ?? "The engine couldn't start this slot.",
                    Details = response
                };
                _logger($"Logic Engine Start Failed ({LastStartError.Code}): {LastStartError.Message}");
                StopEngine();
                return false;
            }

            if (response["status"]?.ToString() == "ready")
            {
                LastYamlInfo = response["yaml"] as JObject;
                LastDataChecksum = response["data_checksum"]?.ToString();
                LastWorldVersion = response["world_version"]?.ToString();
                var poolToken = response["item_pool"];
                if (poolToken != null)
                {
                    LastItemPool = poolToken.ToObject<List<WorldItemInfo>>() ?? new List<WorldItemInfo>();
                    _logger($"Logic Engine Start: Received {LastItemPool.Count} total items in item pool.");
                }
                return true;
            }

            LastStartError = new EngineStartError { Code = "error", Message = response["error"]?.ToString() ?? "The engine couldn't start this slot.", Details = response };
            _logger("Logic Engine Start Failed: " + response.ToString(Newtonsoft.Json.Formatting.None));
            StopEngine();
            return false;
        }
        catch (Exception ex)
        {
            LastStartError = new EngineStartError { Code = "error", Message = ex.Message };
            _logger("Logic Engine Start Exception: " + ex.Message);
            StopEngine();
            return false;
        }
    }

    public HashSet<long> LastExcludedLocations { get; private set; } = new HashSet<long>();

    /// <summary>Locations reachable only with the world's glitch/sequence-break logic, from the last answer.</summary>
    public HashSet<long> LastGlitchedLocations { get; private set; } = new HashSet<long>();

    /// <summary>From the last answer: whether the slot's goal can be completed with those items (null: the engine can't tell).</summary>
    public bool? LastGoalReachable { get; private set; }

    /// <summary>Why the last query failed (when GetStepsAsync returned null).</summary>
    public string LastQueryFailure { get; private set; }

    /// <summary>What a slot's new items open, in the order they arrived (see <see cref="GetStepsAsync"/>).</summary>
    /// <param name="Start">What's in logic with the items before them (only when asked for).</param>
    /// <param name="Opened">For each new item: the locations in logic after it that weren't before it.</param>
    public sealed record LogicSteps(IReadOnlyList<long> Start, IReadOnlyList<IReadOnlyList<long>> Opened);

    /// <summary>
    /// What each of <paramref name="items"/> opens, after <paramref name="before"/> (the items worked out already), in one
    /// request: the engine works out each in turn, as it would one by one. With <paramref name="start"/>, what's in logic
    /// with just <paramref name="before"/> comes too. The answer is read into lists off the main thread. Returns null when
    /// the engine couldn't answer (stopped, crashed, stuck, or reported an error): never an empty answer in place of one,
    /// so a failure can't be mistaken for "these items unlock nothing".
    /// </summary>
    public async Task<LogicSteps> GetStepsAsync(IReadOnlyList<long> before, IReadOnlyList<long> items, IEnumerable<long> missingLocations, bool start)
    {
        LastQueryFailure = null;
        var seat = _seat;
        if (seat is not { Running: true })
        {
            LastQueryFailure = "the logic engine isn't running";
            return null;
        }

        try
        {
            var request = new JObject { ["action"] = "steps", ["base"] = new JArray(before), ["items"] = new JArray(items), ["start"] = start };
            if (missingLocations != null) request["missing_locations"] = new JArray(missingLocations);
            // A big world on a slow PC can take a while; an engine that takes two minutes over a few items is stuck, and is
            // started again.
            var answer = await seat.AskAsync(request, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            var response = answer.Reply;
            if (response == null)
            {
                LastQueryFailure = answer.Failure == AP_Atlas.Core.EngineSetup.EngineFailure.TimedOut ? "the logic engine stopped answering" : "the logic engine stopped";
                _logger("GetStepsAsync: " + LastQueryFailure + " (" + answer.Why + ").");
                return null;
            }
            if (response["error"] != null)
            {
                LastQueryFailure = "the logic engine reported an error: " + response["error"];
                _logger("GetStepsAsync: bridge error: " + response["error"] + "\n" + response["trace"]);
                return null;
            }
            if (response["steps"] is not JArray steps || steps.Count != items.Count || (start && response["start"] is not JArray))
            {
                LastQueryFailure = "the logic engine's answer didn't say what each item opened";
                _logger("GetStepsAsync: " + LastQueryFailure + ".");
                return null;
            }
            var opened = steps.Select(step => (IReadOnlyList<long>)(step.ToObject<List<long>>() ?? new List<long>())).ToList();
            var startList = start ? response["start"].ToObject<List<long>>() : new List<long>();
            if (response["excluded"] is JArray excluded) LastExcludedLocations = new HashSet<long>(excluded.ToObject<List<long>>());
            if (response["glitched"] is JArray glitched) LastGlitchedLocations = new HashSet<long>(glitched.ToObject<List<long>>());
            var goal = response["goal"];
            LastGoalReachable = goal?.Type == JTokenType.Boolean ? (bool)goal : null;
            return new LogicSteps(startList, opened);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastQueryFailure = "the logic engine failed: " + ex.Message;
            _logger("GetStepsAsync Exception: " + ex.Message);
            return null;
        }
    }

    /// <summary>Raised (on any thread) when the slot's engine is lost: it crashed, or got stuck (whose request it was answering says whose failure it is).</summary>
    public event Action<AP_Atlas.Core.EngineSetup.EngineLoss> EngineLost;

    /// <summary>
    /// Reads games' item and location name tables from the local Archipelago install (offline). Runs a short-lived
    /// "AtlasNames" process; no tracker engine is started. Returns null if Archipelago isn't set up or it failed.
    /// </summary>
    public async Task<(Dictionary<string, AP_Atlas.Core.PopTracker.GameNameTable> Tables, List<string> KnownGames)?> FetchLocalNamesAsync(IEnumerable<string> games)
    {
        if (_install == null || !_install.CanLaunch) return null;
        AP_Atlas.Core.EngineSetup.AtlasEngine.InstallBridge(_install);
        System.Diagnostics.Process process = null;
        Task errors = Task.CompletedTask;
        try
        {
            process = new System.Diagnostics.Process { StartInfo = _install.StartInfo("AtlasNames") };
            process.Start();
            AP_Atlas.Core.EngineSetup.ProcessJob.Track(process, _install.Root);
            // Its error output is read and dropped (a full pipe would block it), and its answer is read without trusting
            // its length: every installed game's names come to about 10 million characters, far below AnswerLimit.
            errors = AP_Atlas.Core.BoundedLineReader.ForEachAsync(process.StandardError, AP_Atlas.Core.BoundedLineReader.LogLimit, null);
            var answers = new AP_Atlas.Core.BoundedLineReader(process.StandardOutput, AP_Atlas.Core.BoundedLineReader.AnswerLimit);
            // A read still waiting when the time runs out ends quietly once the process is stopped (below).
            async Task<AP_Atlas.Core.OutputLine?> NextLineAsync()
            {
                try { return await answers.ReadLineAsync(); }
                catch (Exception ex) when (ex is System.IO.IOException or ObjectDisposedException or InvalidOperationException) { return null; } // stopped: no more lines
            }
            await process.StandardInput.WriteLineAsync(Newtonsoft.Json.JsonConvert.SerializeObject(new { games = games.ToList() }));
            await process.StandardInput.FlushAsync();

            // A monotonic deadline: setting the PC's clock can't stretch the wait, or make it negative.
            var deadline = AP_Atlas.Core.Deadline.In(TimeSpan.FromSeconds(90));
            while (!deadline.Passed)
            {
                var readTask = NextLineAsync();
                var done = await Task.WhenAny(readTask, Task.Delay(deadline.Left));
                if (done != readTask) break;
                if (await readTask is not { } read) break; // already finished: this only takes its result
                if (read.Cut > 0)
                {
                    _logger($"AtlasNames: its answer was longer than {AP_Atlas.Core.BoundedLineReader.AnswerLimit:N0} characters, so Atlas didn't read it.");
                    return null;
                }
                string line = read.Text.Trim();
                if (!line.StartsWith("{")) continue;
                JObject reply;
                try { reply = JObject.Parse(line); } catch { continue; }
                if (reply["error"] != null)
                {
                    _logger("AtlasNames failed: " + reply["error"]);
                    return null;
                }
                if (reply["games"] is not JObject gamesObj) continue;
                var tables = new Dictionary<string, AP_Atlas.Core.PopTracker.GameNameTable>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in gamesObj.Properties())
                {
                    tables[prop.Name] = new AP_Atlas.Core.PopTracker.GameNameTable
                    {
                        Game = prop.Name,
                        Source = AP_Atlas.Core.PopTracker.GameNames.LocalSource,
                        Fetched = DateTime.Now,
                        Version = prop.Value["version"]?.ToString() ?? "",
                        Items = prop.Value["items"]?.ToObject<Dictionary<string, long>>() ?? new Dictionary<string, long>(),
                        Locations = prop.Value["locations"]?.ToObject<Dictionary<string, long>>() ?? new Dictionary<string, long>()
                    };
                }
                var known = reply["known"]?.ToObject<List<string>>() ?? new List<string>();
                return (tables, known);
            }
            _logger("AtlasNames: no reply from the local Archipelago install within 90 seconds.");
            return null;
        }
        catch (Exception ex)
        {
            _logger("AtlasNames exception: " + ex.Message);
            return null;
        }
        finally
        {
            // The whole tree: anything the component started ends with it.
            try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { } // it ended meanwhile
            await errors; // ends with the process
            process?.Dispose();
        }
    }

    /// <summary>Asks the bridge why a location is or isn't in logic for the current items. Null if the engine isn't running or timed out.</summary>
    public async Task<LogicExplanation> ExplainLocationAsync(long locationId, bool analyze = true)
    {
        var seat = _seat;
        if (seat is not { Running: true }) return null;
        try
        {
            var req = new Dictionary<string, object>
            {
                { "action", "explain" },
                { "location", locationId },
                { "analyze", analyze },
                { "budget", 6.0 }
            };
            // A "why" that takes long is answered late or not at all; it's no reason to stop the engine.
            var response = (await seat.AskAsync(JObject.FromObject(req), TimeSpan.FromSeconds(15), stuckIfLate: false)).Reply;
            if (response == null) return null;
            if (response["trace"] != null) _logger("ExplainLocationAsync: bridge error: " + response["error"] + "\n" + response["trace"]);
            return response.ToObject<LogicExplanation>();
        }
        catch (Exception ex)
        {
            _logger("ExplainLocationAsync Exception: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The slot is done with its engine: its world is dropped there, and an engine left with no slots stops (with its whole
    /// process tree). A request waiting on it ends as unanswered. Any thread.
    /// </summary>
    public void StopEngine()
    {
        var seat = _seat;
        _seat = null;
        seat?.Leave();
    }

    /// <summary>Whether this engine can run logic (program files and the Universal Tracker are present).</summary>
    public bool IsEngineInstalled() => _install != null && AP_Atlas.Core.EngineSetup.AtlasEngine.ProblemWith(_install) == null;
}

/// <summary>Why the logic engine couldn't start a slot.</summary>
public class EngineStartError
{
    public string Code { get; set; }
    public string Message { get; set; }
    public JObject Details { get; set; }
}
