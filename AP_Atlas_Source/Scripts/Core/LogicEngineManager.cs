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

public class LogicEngineManager
{
    public List<WorldItemInfo> LastItemPool { get; private set; } = new List<WorldItemInfo>();

    private AP_Atlas.Core.EngineSetup.EngineInstall _install;
    private readonly Action<string> _logger;

    public LogicEngineManager(AP_Atlas.Core.EngineSetup.EngineInstall install, Action<string> logger = null)
    {
        _install = install;
        _logger = logger ?? (msg => GD.Print(msg));
    }

    public AP_Atlas.Core.EngineSetup.EngineInstall Install => _install;

    /// <summary>Points a stopped manager at another engine (the user switched engines or finished setup).</summary>
    public void SetInstall(AP_Atlas.Core.EngineSetup.EngineInstall install)
    {
        if (_engineProcess == null || _engineProcess.HasExited) _install = install;
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

    private System.Diagnostics.Process _engineProcess;
    private StreamWriter _engineWriter;
    private StreamReader _engineReader;

    // A ReadLineAsync that outlived a timed-out request. StreamReader allows only one outstanding read,
    // so the next request must keep awaiting this task rather than starting another.
    private Task<string> _pendingRead;

    // Every request carries an id that the bridge echoes back, so a late reply to a timed-out
    // request is discarded instead of being mistaken for the answer to the next one.
    private int _nextRequestId = 0;

    public async Task<bool> StartEngineAsync(string game, string playerName, int slot, Dictionary<string, object> slotData, IEnumerable<long> allLocations = null, string yamlPath = null,
        string apworldOverride = null, string expectedChecksum = null)
    {
        if (_engineProcess != null && !_engineProcess.HasExited) return true;
        LastStartError = null;
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
            _logger($"Starting the logic bridge ({_install.Describe()})...");
            _stopping = false;
            _engineProcess = new System.Diagnostics.Process { StartInfo = _install.StartInfo("UltimateBridge"), EnableRaisingEvents = true };
            var started = _engineProcess;
            _engineProcess.Exited += (_, _) =>
            {
                if (_stopping || !ReferenceEquals(started, _engineProcess)) return;
                int code = -1;
                try { code = started.ExitCode; } catch { }
                _logger($"The logic engine process exited unexpectedly (exit code {code}).");
                EngineExited?.Invoke(code);
            };
            // stderr must be drained continuously: if its pipe buffer fills, the bridge blocks on its next log write.
            _engineProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) _logger("PYTHON: " + e.Data);
            };
            _engineProcess.Start();
            AP_Atlas.Core.EngineSetup.ProcessJob.Track(_engineProcess, _install.Root);
            _engineProcess.BeginErrorReadLine();

            _engineWriter = _engineProcess.StandardInput;
            _engineReader = _engineProcess.StandardOutput;
            _pendingRead = null;

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
            var response = await SendRequestAsync(initReq, 180000);
            if (response == null)
            {
                bool crashed = _engineProcess?.HasExited == true;
                LastStartError = new EngineStartError
                {
                    Code = crashed ? "crashed" : "no_response",
                    Message = crashed ? "The engine closed while starting (see the slot's debug log)." : "The engine didn't answer within 3 minutes."
                };
                _logger("Logic Engine Start Failed: " + LastStartError.Message);
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

    /// <summary>Locations reachable only with the world's glitch/sequence-break logic, from the last update.</summary>
    public HashSet<long> LastGlitchedLocations { get; private set; } = new HashSet<long>();

    /// <summary>Why the last query failed (when GetReachableLocationsAsync returned null).</summary>
    public string LastQueryFailure { get; private set; }

    /// <summary>
    /// The locations in logic with these items. Returns null when the engine couldn't answer (stopped, crashed,
    /// timed out twice, or reported an error): never an empty list in place of an answer, so a failure can't be
    /// mistaken for "these items unlock nothing".
    /// </summary>
    public async Task<List<long>> GetReachableLocationsAsync(List<long> itemIds, IEnumerable<long> missingLocations = null)
    {
        LastQueryFailure = null;
        if (_engineProcess == null || _engineProcess.HasExited)
        {
            LastQueryFailure = "the logic engine isn't running";
            return null;
        }

        try
        {
            var updateReq = new Dictionary<string, object>
            {
                { "action", "update" },
                { "items", itemIds }
            };
            if (missingLocations != null)
            {
                updateReq["missing_locations"] = new List<long>(missingLocations);
            }

            // A big world on a slow PC can take a while; a second, longer wait picks up a late answer.
            var response = await SendRequestAsync(updateReq, 30000);
            if (response == null && _engineProcess != null && !_engineProcess.HasExited)
            {
                _logger("GetReachableLocationsAsync: no answer within 30 seconds; asking again.");
                response = await SendRequestAsync(updateReq, 90000);
            }
            if (response == null)
            {
                LastQueryFailure = _engineProcess == null || _engineProcess.HasExited ? "the logic engine stopped" : "the logic engine stopped answering";
                _logger("GetReachableLocationsAsync: " + LastQueryFailure + ".");
                return null;
            }

            if (response["error"] != null)
            {
                LastQueryFailure = "the logic engine reported an error: " + response["error"];
                _logger("GetReachableLocationsAsync: bridge error: " + response["error"] + "\n" + response["trace"]);
                return null;
            }

            var excludedToken = response["excluded"];
            if (excludedToken != null)
            {
                LastExcludedLocations = new HashSet<long>(excludedToken.ToObject<List<long>>());
            }
            var glitchedToken = response["glitched"];
            if (glitchedToken != null)
            {
                LastGlitchedLocations = new HashSet<long>(glitchedToken.ToObject<List<long>>());
            }

            var reachableToken = response["reachable"];
            if (reachableToken != null)
            {
                return reachableToken.ToObject<List<long>>();
            }
            LastQueryFailure = "the logic engine's answer had no locations";
        }
        catch (Exception ex)
        {
            LastQueryFailure = "the logic engine failed: " + ex.Message;
            _logger("GetReachableLocationsAsync Exception: " + ex.Message);
        }

        return null;
    }

    /// <summary>Raised (on a process thread) when the engine process exits without being stopped by Atlas.</summary>
    public event Action<int> EngineExited;

    private bool _stopping;

    /// <summary>
    /// Reads games' item and location name tables from the local Archipelago install (offline). Runs a short-lived
    /// "AtlasNames" process; no tracker engine is started. Returns null if Archipelago isn't set up or it failed.
    /// </summary>
    public async Task<(Dictionary<string, AP_Atlas.Core.PopTracker.GameNameTable> Tables, List<string> KnownGames)?> FetchLocalNamesAsync(IEnumerable<string> games)
    {
        if (_install == null || !_install.CanLaunch) return null;
        AP_Atlas.Core.EngineSetup.AtlasEngine.InstallBridge(_install);
        System.Diagnostics.Process process = null;
        try
        {
            process = new System.Diagnostics.Process { StartInfo = _install.StartInfo("AtlasNames") };
            process.ErrorDataReceived += (_, e) => { };
            process.Start();
            AP_Atlas.Core.EngineSetup.ProcessJob.Track(process, _install.Root);
            process.BeginErrorReadLine();
            await process.StandardInput.WriteLineAsync(Newtonsoft.Json.JsonConvert.SerializeObject(new { games = games.ToList() }));
            await process.StandardInput.FlushAsync();

            var deadline = DateTime.Now.AddSeconds(90);
            while (DateTime.Now < deadline)
            {
                var readTask = process.StandardOutput.ReadLineAsync();
                var done = await Task.WhenAny(readTask, Task.Delay(deadline - DateTime.Now));
                if (done != readTask) break;
                string line = readTask.Result;
                if (line == null) break;
                line = line.Trim();
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
            try { if (process != null && !process.HasExited) process.Kill(); } catch { }
            process?.Dispose();
        }
    }

    /// <summary>Asks the bridge why a location is or isn't in logic for the current items. Null if the engine isn't running or timed out.</summary>
    public async Task<LogicExplanation> ExplainLocationAsync(long locationId, bool analyze = true)
    {
        if (_engineProcess == null || _engineProcess.HasExited) return null;
        try
        {
            var req = new Dictionary<string, object>
            {
                { "action", "explain" },
                { "location", locationId },
                { "analyze", analyze },
                { "budget", 6.0 }
            };
            var response = await SendRequestAsync(req, 15000);
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

    // One request at a time: the bridge answers in order over a single pipe, and an explain issued while a
    // logic update is in flight must not read the update's reply.
    private readonly System.Threading.SemaphoreSlim _requestLock = new System.Threading.SemaphoreSlim(1, 1);

    /// <summary>Writes one request line and waits for the matching response. Returns null on timeout or if the bridge exits.</summary>
    private async Task<JObject> SendRequestAsync(Dictionary<string, object> request, int timeoutMs)
    {
        await _requestLock.WaitAsync();
        try
        {
            if (_engineWriter == null || _engineReader == null) return null;
            return await SendRequestLockedAsync(request, timeoutMs);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<JObject> SendRequestLockedAsync(Dictionary<string, object> request, int timeoutMs)
    {
        int id = ++_nextRequestId;
        request["id"] = id;
        await _engineWriter.WriteLineAsync(Newtonsoft.Json.JsonConvert.SerializeObject(request));
        await _engineWriter.FlushAsync();

        var timeoutTask = Task.Delay(timeoutMs);
        while (true)
        {
            _pendingRead ??= _engineReader.ReadLineAsync();
            var completedTask = await Task.WhenAny(_pendingRead, timeoutTask);
            if (completedTask == timeoutTask) return null; // leave _pendingRead for the next request

            string line = await _pendingRead;
            _pendingRead = null;
            if (line == null) return null; // bridge exited

            line = line.Trim();
            if (!(line.StartsWith("{") && line.EndsWith("}")))
            {
                if (!string.IsNullOrEmpty(line)) _logger("PYTHON: " + line);
                continue;
            }

            JObject response;
            try { response = JObject.Parse(line); }
            catch (Exception)
            {
                _logger("PYTHON: " + line);
                continue;
            }

            // Responses without an id (e.g. a bridge boot failure) can't be stale, so accept them.
            var responseId = response["id"];
            if (responseId != null && responseId.Type == JTokenType.Integer && (int)responseId != id)
            {
                _logger($"Discarding stale bridge response for request {responseId} (waiting for {id}).");
                continue;
            }
            return response;
        }
    }

    public void StopEngine()
    {
        _stopping = true;
        var process = _engineProcess;
        _engineProcess = null;
        _engineWriter = null;
        _engineReader = null;
        _pendingRead = null;
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(); } catch { }
        try { process.Dispose(); } catch { }
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
