#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AP_Atlas.Core.EngineSetup
{
    public class GameSweepResult
    {
        [JsonProperty("game")] public string Game { get; set; }
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("locations")] public int Locations { get; set; }
        [JsonProperty("in_logic")] public int InLogic { get; set; }
        [JsonProperty("yamlless")] public bool Yamlless { get; set; }
        [JsonProperty("ut_disabled")] public bool TrackerDisabled { get; set; }
        [JsonProperty("data_checksum")] public string Checksum { get; set; }
        [JsonProperty("world_version")] public string WorldVersion { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
        [JsonProperty("seconds")] public double Seconds { get; set; }

        /// <summary>Plain wording: what this test shows about the game.</summary>
        [JsonIgnore]
        public string Summary =>
            TrackerDisabled ? "its author disabled the Universal Tracker for it"
            : Ok ? $"rebuilds and computes logic ({Locations} locations with default options)"
            : "not testable with default options" + (Error != null ? $" ({Error})" : Yamlless ? " (it rebuilds from a seed's server data)" : "") +
              "; verifying against a real seed tests it properly";
    }

    public class GameSweepRecord
    {
        public string Engine { get; set; }
        public DateTime Tested { get; set; }
        public Dictionary<string, GameSweepResult> Results { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rebuilds every installed game with default options and computes its starting logic, so a world that can't be
    /// tracked shows up before a user connects one. One engine process streams a line per game; if it dies part-way
    /// (a world that crashes Python), the rest are retried in a new process and the culprit is marked.
    /// </summary>
    public static class GameSweep
    {
        private static string StorePath => Path.Combine(AtlasEngine.EngineDir, "game_sweep.json");
        private static Dictionary<string, GameSweepRecord> _store;
        private static Dictionary<string, GameSweepRecord> Store => _store ??= SafeFile.ReadJson(StorePath, () => new Dictionary<string, GameSweepRecord>(StringComparer.OrdinalIgnoreCase));

        public static GameSweepRecord LastFor(EngineInstall install)
        {
            lock (Store) return Store.TryGetValue(install.Root ?? "", out var r) ? r : null;
        }

        /// <summary>The last check of one game in this engine, or null.</summary>
        public static GameSweepResult LastFor(EngineInstall install, string game)
        {
            lock (Store)
                return Store.TryGetValue(install?.Root ?? "", out var r) && r.Results.TryGetValue(game ?? "", out var result) ? result : null;
        }

        /// <param name="only">Check only these games (their results join the earlier ones); null checks every game.</param>
        public static Task<GameSweepRecord> RunAsync(EngineInstall install, Action<string> log, CancellationToken ct, IReadOnlyList<string> only = null) =>
            AtlasEngine.ExclusiveAsync(async () =>
            {
                var check = AtlasEngine.LastCheck(install) ?? await AtlasEngine.RunCheckAsync(install, log, ct);
                var games = (check.Games ?? new List<string>()).Where(g => g != "Archipelago" && g != "Universal Tracker")
                    .Where(g => only == null || only.Contains(g, StringComparer.OrdinalIgnoreCase)).ToList();
                if (games.Count == 1) log?.Invoke($"Checking {games[0]}: it's rebuilt with default options and its starting logic computed…");
                else log?.Invoke($"Testing {games.Count} games: each is rebuilt with default options and its starting logic computed…");
                var results = new Dictionary<string, GameSweepResult>(StringComparer.OrdinalIgnoreCase);
                var remaining = new List<string>(games);
                for (int attempt = 0; attempt < 5 && remaining.Count > 0; attempt++)
                {
                    string request = JsonConvert.SerializeObject(new { games = remaining });
                    await AtlasEngine.RunComponentAsync(install, "AtlasGameSweep", request, line =>
                    {
                        string t = line.Trim();
                        if (!t.StartsWith("{") || !t.Contains("\"game\"")) return;
                        try
                        {
                            var r = JsonConvert.DeserializeObject<GameSweepResult>(t);
                            if (r?.Game != null) lock (results) results[r.Game] = r;
                        }
                        catch (Exception ex) { Logger.LogDebug("A game test reply couldn't be read: " + ex.Message); }
                    }, _ => { }, ct, TimeSpan.FromMinutes(10));
                    var missing = remaining.Where(g => !results.ContainsKey(g)).ToList();
                    if (missing.Count == 0) break;
                    // The process stopped part-way: the first unanswered game is the likely culprit.
                    results[missing[0]] = new GameSweepResult { Game = missing[0], Error = "the engine stopped while testing this game" };
                    remaining = missing.Skip(1).ToList();
                }
                var record = new GameSweepRecord { Engine = install.Describe(), Tested = DateTime.Now, Results = results }; // wall clock: shown as a date
                lock (Store)
                {
                    // One game's check joins the earlier results of the others.
                    if (only != null && Store.TryGetValue(install.Root ?? "", out var earlier))
                        foreach (var (game, result) in earlier.Results)
                            if (!record.Results.ContainsKey(game)) record.Results[game] = result;
                    Store[install.Root ?? ""] = record;
                    try { SafeFile.WriteJson(StorePath, Store); } catch (Exception ex) { Logger.LogWarning("Couldn't save the game test results: " + ex.Message); }
                }
                int ok = results.Values.Count(r => r.Ok);
                log?.Invoke($"{ok} of {results.Count} games rebuild and compute logic with default options.");
                foreach (var r in results.Values.Where(r => !r.Ok).OrderBy(r => r.Game)) log?.Invoke($"  {r.Game}: {r.Summary}");
                return record;
            }, ct);
    }
}
