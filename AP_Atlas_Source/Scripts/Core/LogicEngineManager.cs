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

public class LogicEngineManager
{
    public List<WorldItemInfo> LastItemPool { get; private set; } = new List<WorldItemInfo>();

    private const string GitHubApiUrl = "https://api.github.com/repos/FarisTheAncient/Archipelago/releases/latest";
    private readonly string _apPath;
    private readonly System.Net.Http.HttpClient _httpClient;
    private readonly Action<string> _logger;

    public LogicEngineManager(string apPath, Action<string> logger = null)
    {
        _apPath = apPath;
        _logger = logger ?? (msg => GD.Print(msg));
        _httpClient = new System.Net.Http.HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AP_Atlas-AutoUpdater");
    }

    private System.Diagnostics.Process _engineProcess;
    private StreamWriter _engineWriter;
    private StreamReader _engineReader;

    // A ReadLineAsync that outlived a timed-out request. StreamReader allows only one outstanding read,
    // so the next request must keep awaiting this task rather than starting another.
    private Task<string> _pendingRead;

    // Every request carries an id that the bridge echoes back, so a late reply to a timed-out
    // request is discarded instead of being mistaken for the answer to the next one.
    private int _nextRequestId = 0;

    public async Task<bool> StartEngineAsync(string game, string playerName, int slot, Dictionary<string, object> slotData, IEnumerable<long> allLocations = null)
    {
        if (_engineProcess != null && !_engineProcess.HasExited) return true;
        if (!IsEngineInstalled()) return false;

        InstallPythonBridge();

        try
        {
            _logger("Starting Python Bridge (ArchipelagoLauncher.exe UltimateBridge)...");
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(_apPath, "ArchipelagoLauncher.exe"),
                Arguments = "UltimateBridge",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _apPath
            };

            _engineProcess = new System.Diagnostics.Process { StartInfo = startInfo };
            // stderr must be drained continuously: if its pipe buffer fills, the bridge blocks on its next log write.
            _engineProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) _logger("PYTHON: " + e.Data);
            };
            _engineProcess.Start();
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
                { "all_locations", allLocations != null ? new List<long>(allLocations) : new List<long>() }
            };

            var response = await SendRequestAsync(initReq, 60000);
            if (response == null)
            {
                _logger("Logic Engine Start Failed: no response from the bridge within 60 seconds.");
                StopEngine();
                return false;
            }

            if (response["status"]?.ToString() == "ready")
            {
                var poolToken = response["item_pool"];
                if (poolToken != null)
                {
                    LastItemPool = poolToken.ToObject<List<WorldItemInfo>>() ?? new List<WorldItemInfo>();
                    _logger($"Logic Engine Start: Received {LastItemPool.Count} total items in item pool.");
                }
                return true;
            }

            _logger("Logic Engine Start Failed: " + response.ToString(Newtonsoft.Json.Formatting.None));
            StopEngine();
            return false;
        }
        catch (Exception ex)
        {
            _logger("Logic Engine Start Exception: " + ex.Message);
            StopEngine();
            return false;
        }
    }

    public HashSet<long> LastExcludedLocations { get; private set; } = new HashSet<long>();

    public async Task<List<long>> GetReachableLocationsAsync(List<long> itemIds, IEnumerable<long> missingLocations = null)
    {
        if (_engineProcess == null || _engineProcess.HasExited) return new List<long>();

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

            var response = await SendRequestAsync(updateReq, 10000);
            if (response == null)
            {
                _logger("GetReachableLocationsAsync: no response from the bridge within 10 seconds.");
                return new List<long>();
            }

            if (response["error"] != null)
            {
                _logger("GetReachableLocationsAsync: bridge error: " + response["error"] + "\n" + response["trace"]);
                return new List<long>();
            }

            var excludedToken = response["excluded"];
            if (excludedToken != null)
            {
                LastExcludedLocations = new HashSet<long>(excludedToken.ToObject<List<long>>());
            }

            var reachableToken = response["reachable"];
            if (reachableToken != null)
            {
                return reachableToken.ToObject<List<long>>();
            }
        }
        catch (Exception ex)
        {
            _logger("GetReachableLocationsAsync Exception: " + ex.Message);
        }

        return new List<long>();
    }

    /// <summary>Writes one request line and waits for the matching response. Returns null on timeout or if the bridge exits.</summary>
    private async Task<JObject> SendRequestAsync(Dictionary<string, object> request, int timeoutMs)
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
        var process = _engineProcess;
        _engineProcess = null;
        _engineWriter = null;
        _engineReader = null;
        _pendingRead = null;
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(); } catch { }
        try { process.Dispose(); } catch { }
    }

    public string GetWorldsDirectory()
    {
        if (string.IsNullOrEmpty(_apPath) || !Directory.Exists(_apPath)) return null;

        string customWorlds = Path.Combine(_apPath, "custom_worlds");
        string libWorlds = Path.Combine(_apPath, "lib", "worlds");

        if (Directory.Exists(customWorlds)) return customWorlds;
        if (Directory.Exists(libWorlds)) return libWorlds;

        // If neither exists but it's a valid directory, create custom_worlds
        Directory.CreateDirectory(customWorlds);
        return customWorlds;
    }

    public bool IsEngineInstalled()
    {
        string worldsDir = GetWorldsDirectory();
        if (worldsDir == null) return false;

        return File.Exists(Path.Combine(worldsDir, "tracker.apworld"));
    }

    private void InstallPythonBridge()
    {
        string worldsDir = GetWorldsDirectory();
        if (worldsDir == null) return;

        string apworldPath = Path.Combine(worldsDir, "UltimateBridge.apworld");

        string scriptContent = @"import sys
import json
import logging
import traceback

def launch_bridge(*args):
    try:
        from worlds.tracker.TrackerCore import TrackerCore
        from worlds.AutoWorld import AutoWorldRegister
        from NetUtils import NetworkItem
        from BaseClasses import LocationProgressType

        logger = logging.getLogger('UltimateBridge')
        core = TrackerCore(logger, False, False)
        core.run_generator(None, None)

        item_cache = {}
        def get_net_item(item_id):
            if item_id not in item_cache:
                item_cache[item_id] = NetworkItem(item_id, -1, -1, 0)
            return item_cache[item_id]

        while True:
            line = sys.stdin.readline()
            if not line: break

            # Echo the request id on every reply so the C# side can drop late replies.
            rid = None
            try:
                req = json.loads(line)
                rid = req.get('id')
                action = req.get('action')
                
                if action == 'init':
                    game = req.get('game')
                    slot_name = req.get('player_name')
                    slot = req.get('slot', 1)
                    slot_data = req.get('slot_data') or {}
                    all_locations = req.get('all_locations') or []
                    
                    connected_cls = AutoWorldRegister.world_types.get(game)

                    core.set_slot_params(game, 1, slot_name, 1)
                    core.initalize_tracker_core(connected_cls, slot_data)
                    if all_locations:
                        core.set_missing_locations(set(all_locations))
                    
                    item_pool = []
                    mw = getattr(core, 'multiworld', None)
                    if mw:
                        target_player = getattr(core, 'player_id', None)
                        if target_player is None:
                            target_player = getattr(core, 'slot', 1)
                            
                        pool_items = [it for it in mw.itempool if it.player == target_player and it.code is not None]
                        if not pool_items and len(mw.itempool) > 0 and target_player != 1:
                            pool_items = [it for it in mw.itempool if it.player == 1 and it.code is not None]
                            
                        loc_items = [loc.item for loc in mw.get_locations(target_player) if loc.item and (loc.item.player == target_player or loc.item.player == 1) and loc.item.code is not None]
                        pre_items = [it for it in mw.precollected_items.get(target_player, []) if it.code is not None]
                        
                        all_items = pool_items + loc_items + pre_items
                        
                        if not all_items:
                            world = core.get_current_world()
                            if world:
                                item_names = getattr(world, 'item_name_to_id', {})
                                for item_name in item_names:
                                    try:
                                        it = world.create_item(item_name)
                                        if getattr(it, 'code', None) is not None:
                                            all_items.append(it)
                                    except Exception:
                                        pass
                        
                        for it in all_items:
                            flags = 0
                            cls = getattr(it, 'classification', None)
                            if cls is not None:
                                # New AP ItemClassification
                                try:
                                    if cls & 1: flags |= 1
                                    if cls & 2: flags |= 2
                                    if cls & 4: flags |= 4
                                except Exception:
                                    try:
                                        val = cls.value
                                        if val & 1: flags |= 1
                                        if val & 2: flags |= 2
                                        if val & 4: flags |= 4
                                    except Exception:
                                        pass
                            else:
                                # Legacy
                                if getattr(it, 'advancement', False):
                                    flags |= 1
                                if getattr(it, 'never_exclude', False):
                                    flags |= 2
                                if getattr(it, 'trap', False):
                                    flags |= 4
                                    
                            item_pool.append({
                                'id': it.code,
                                'name': getattr(it, 'name', 'Unknown Item'),
                                'flags': flags
                            })
                            
                        # Debug print the first 5 items
                        logger.info('UltimateBridge Item Dump:')
                        for debug_it in item_pool[:5]:
                            logger.info(' - ' + str(debug_it.get('name')) + ': Flags=' + str(debug_it.get('flags')))

                    print(json.dumps({'id': rid, 'status': 'ready', 'item_pool': item_pool}))
                    sys.stdout.flush()
                    
                elif action == 'update':
                    item_ids = req.get('items', [])
                    missing_locs = req.get('missing_locations')
                    if missing_locs is not None:
                        core.set_missing_locations(set(missing_locs))
                        
                    core.set_items_received([get_net_item(i) for i in item_ids])
                    state = core.updateTracker()
                    world = core.get_current_world()
                    reachable_ids = []
                    for loc_name in getattr(state, 'in_logic_locations', []):
                        if loc_name in world.location_name_to_id:
                            reachable_ids.append(world.location_name_to_id[loc_name])
                            
                    excluded_ids = []
                    target_player = getattr(core, 'player_id', None) or 1
                    for loc in core.multiworld.get_locations(target_player):
                        pt = getattr(loc, 'progress_type', None)
                        if pt is not None and getattr(pt, 'name', '') == 'EXCLUDED':
                            if loc.name in world.location_name_to_id:
                                excluded_ids.append(world.location_name_to_id[loc.name])
                            
                    print(json.dumps({'id': rid, 'reachable': reachable_ids, 'excluded': excluded_ids}))
                    sys.stdout.flush()

            except Exception as e:
                print(json.dumps({'id': rid, 'error': str(e), 'trace': traceback.format_exc()}))
                sys.stdout.flush()
                
    except Exception as e:
        print(json.dumps({'error': 'Bridge Boot Failed', 'trace': traceback.format_exc()}))
        sys.stdout.flush()

from worlds.LauncherComponents import Component, components, Type
components.append(Component('UltimateBridge', None, func=launch_bridge, component_type=Type.CLIENT))
";

        try
        {
            // Clean up any stale loose directory
            string looseDir = Path.Combine(worldsDir, "UltimateBridge");
            if (Directory.Exists(looseDir)) Directory.Delete(looseDir, true);

            // Rewrite only when the script changed: another slot's bridge may be loading this file right now.
            if (File.Exists(apworldPath) && ReadInstalledBridgeScript(apworldPath) == scriptContent) return;
            if (File.Exists(apworldPath)) File.Delete(apworldPath);

            using (var zipStream = new FileStream(apworldPath, FileMode.Create))
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("UltimateBridge/__init__.py");
                using (var entryStream = entry.Open())
                using (var writer = new StreamWriter(entryStream))
                {
                    writer.Write(scriptContent);
                }
            }
        }
        catch (Exception ex)
        {
            _logger("Failed to install UltimateBridge.apworld: " + ex.Message);
        }
    }

    private static string ReadInstalledBridgeScript(string apworldPath)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(apworldPath);
            var entry = archive.GetEntry("UltimateBridge/__init__.py");
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }


    public async Task<bool> DownloadLatestEngineAsync(Action<string> onProgress)
    {
        try
        {
            string worldsDir = GetWorldsDirectory();
            if (worldsDir == null)
            {
                onProgress?.Invoke("Error: Archipelago Installation Path is not set or invalid.");
                return false;
            }

            onProgress?.Invoke("Checking for latest Universal Tracker release...");
            var response = await _httpClient.GetStringAsync(GitHubApiUrl);
            var json = JObject.Parse(response);
            var assets = json["assets"] as JArray;

            string downloadUrl = null;
            if (assets != null)
            {
                foreach (var asset in assets)
                {
                    if (asset["name"]?.ToString() == "tracker.apworld")
                    {
                        downloadUrl = asset["browser_download_url"]?.ToString();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                onProgress?.Invoke("Error: Could not find tracker.apworld in latest release.");
                return false;
            }

            onProgress?.Invoke("Downloading tracker.apworld...");
            byte[] fileBytes = await _httpClient.GetByteArrayAsync(downloadUrl);

            string destPath = Path.Combine(worldsDir, "tracker.apworld");
            File.WriteAllBytes(destPath, fileBytes);

            onProgress?.Invoke("Universal Tracker installed successfully.");
            InstallPythonBridge();
            return true;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Error downloading engine: {ex.Message}");
            return false;
        }
    }
}
