using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.EngineSetup
{
    public class SeedTestStep
    {
        [JsonProperty("sphere")] public int Sphere { get; set; }
        [JsonProperty("expected")] public int Expected { get; set; }
        [JsonProperty("reachable")] public int Reachable { get; set; }
        [JsonProperty("late_count")] public int LateCount { get; set; }
        [JsonProperty("early_count")] public int EarlyCount { get; set; }
        [JsonProperty("late")] public List<string> Late { get; set; } = new();
        [JsonProperty("early")] public List<string> Early { get; set; } = new();
    }

    public class SeedTestRebuild
    {
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("file")] public string File { get; set; }
        [JsonProperty("match")] public bool? Match { get; set; }
        [JsonProperty("expected")] public int Expected { get; set; }
        [JsonProperty("got")] public int Got { get; set; }
        [JsonProperty("missing")] public int Missing { get; set; }
        [JsonProperty("extra")] public int Extra { get; set; }
    }

    public class SeedTestPlayer
    {
        [JsonProperty("player")] public int Player { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("game")] public string Game { get; set; }
        [JsonProperty("seed_checksum")] public string SeedChecksum { get; set; }
        [JsonProperty("local_checksum")] public string LocalChecksum { get; set; }
        [JsonProperty("checksum_match")] public bool? ChecksumMatch { get; set; }
        [JsonProperty("rebuild")] public SeedTestRebuild Rebuild { get; set; }
        [JsonProperty("steps")] public List<SeedTestStep> Steps { get; set; } = new();
        [JsonProperty("exact")] public bool Exact { get; set; }
        [JsonProperty("seconds")] public double Seconds { get; set; }
        [JsonProperty("error")] public JObject Error { get; set; }

        [JsonIgnore] public int Late => Steps.Sum(s => s.LateCount);
        [JsonIgnore] public int Early => Steps.Sum(s => s.EarlyCount);

        /// <summary>One plain sentence: how Atlas's logic compares with the real seed, and the most likely reason if not exact.</summary>
        [JsonIgnore]
        public string Verdict
        {
            get
            {
                if (Error != null) return $"couldn't rebuild the world ({Error["code"]}: {Error["message"]})";
                if (Exact) return $"exact: Atlas's logic matches the seed at every one of {Steps.Count} spheres";
                var why = new List<string>();
                if (ChecksumMatch == false) why.Add("the installed apworld isn't the version the seed was made with");
                if (Rebuild?.Match == false) why.Add($"the rebuilt world differs ({Rebuild.Missing} locations missing, {Rebuild.Extra} extra), so it needs the player's YAML");
                if (why.Count == 0) why.Add("the server's data doesn't carry every option this game's rules use (linking the player's YAML may fix it)");
                var firstBad = Steps.FirstOrDefault(s => s.LateCount > 0 || s.EarlyCount > 0);
                string example = firstBad == null ? "" : $" First difference at sphere {firstBad.Sphere}: " +
                    string.Join(", ", firstBad.Late.Take(3).Select(n => n + " (late)").Concat(firstBad.Early.Take(3).Select(n => n + " (early)"))) + ".";
                return $"differs: {Late} location-steps shown in logic too late, {Early} too early; likely because {string.Join("; ", why)}.{example}";
            }
        }
    }

    public class SeedTestReport
    {
        [JsonProperty("seed")] public string Seed { get; set; }
        [JsonProperty("seed_name")] public string SeedName { get; set; }
        [JsonProperty("generator")] public string Generator { get; set; }
        [JsonProperty("spheres")] public int Spheres { get; set; }
        [JsonProperty("players")] public List<SeedTestPlayer> Players { get; set; } = new();
        [JsonProperty("error")] public string Error { get; set; }
    }

    /// <summary>A remembered result: how Atlas's logic for a game (at one apworld version) compared with a real seed.</summary>
    public class SeedVerification
    {
        public string Game { get; set; }
        /// <summary>The installed apworld's data checksum the test ran with (results hold for this version only).</summary>
        public string Checksum { get; set; }
        public string SeedName { get; set; }
        public string SeedFile { get; set; }
        public string Engine { get; set; }
        public DateTime Tested { get; set; } = DateTime.Now;
        public bool Exact { get; set; }
        public int Spheres { get; set; }
        public int Late { get; set; }
        public int Early { get; set; }
        public string Verdict { get; set; }
    }

    /// <summary>
    /// Checks Atlas's logic against ground truth: a seed generated locally records the generator's spheres (which
    /// locations become reachable after which items), and replaying them through the engine must reach exactly the
    /// same locations at every step. Results are remembered per game and apworld version for the accuracy summary.
    /// </summary>
    public static class SeedVerifier
    {
        private static string StorePath => Path.Combine(AtlasEngine.EngineDir, "seed_verifications.json");
        private static List<SeedVerification> _store;

        private static List<SeedVerification> Store => _store ??= SafeFile.ReadJson(StorePath, () => new List<SeedVerification>());

        /// <summary>The latest test result for a game at this apworld version (null if never tested).</summary>
        public static SeedVerification For(string game, string checksum)
        {
            if (string.IsNullOrEmpty(game)) return null;
            lock (Store)
                return Store.Where(v => string.Equals(v.Game, game, StringComparison.OrdinalIgnoreCase) && (checksum == null || v.Checksum == checksum))
                    .OrderByDescending(v => v.Tested).FirstOrDefault();
        }

        public static IReadOnlyList<SeedVerification> All { get { lock (Store) return Store.ToList(); } }

        /// <summary>Where generated seeds usually are: the output folder of the Archipelago install the user chose (Atlas doesn't search).</summary>
        public static string DefaultSeedFolder()
        {
            string root = AtlasEngine.ConfiguredInstallPath;
            if (string.IsNullOrWhiteSpace(root)) return null;
            string output = Path.Combine(root, "output");
            return Directory.Exists(output) ? output : null;
        }

        /// <summary>Runs the seed test for every player in a seed (.zip from Archipelago's output folder, or .archipelago).</summary>
        public static Task<SeedTestReport> VerifyAsync(EngineInstall install, string seedPath, Action<string> log, CancellationToken ct) =>
            AtlasEngine.ExclusiveAsync(async () =>
            {
                log?.Invoke($"Testing logic against {Path.GetFileName(seedPath)} (rebuilding each world and replaying the seed's spheres)…");
                SeedTestReport report = null;
                var errors = new StringBuilder();
                // With the cache index, a seed made with another apworld version is tested on that version when Atlas has it.
                string request = JsonConvert.SerializeObject(new { seed = Path.GetFullPath(seedPath), apworld_cache_index = ApworldSources.CacheIndexFile });
                try
                {
                    await AtlasEngine.RunComponentAsync(install, "AtlasSeedTest", request, line =>
                    {
                        string t = line.Trim();
                        if (t.StartsWith("{") && t.Contains("\"players\""))
                        {
                            try { report = JsonConvert.DeserializeObject<SeedTestReport>(t); } catch { } // not every output line is a reply
                        }
                    }, err => { if (errors.Length < 4000) errors.AppendLine(err); }, ct, TimeSpan.FromMinutes(15));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.AppendLine(ex.Message); }

                report ??= new SeedTestReport { Seed = seedPath, Error = errors.Length > 0 ? errors.ToString().Trim().Split('\n').Last() : "The engine exited without reporting." };
                if (report.Error != null) log?.Invoke("The seed test couldn't run: " + report.Error.Trim().Split('\n').Last());
                foreach (var p in report.Players)
                {
                    log?.Invoke($"{p.Game} ({p.Name}): {p.Verdict}");
                    if (p.Error != null || string.IsNullOrEmpty(p.LocalChecksum)) continue;
                    Remember(new SeedVerification
                    {
                        Game = p.Game,
                        Checksum = p.LocalChecksum,
                        SeedName = report.SeedName,
                        SeedFile = Path.GetFileName(seedPath),
                        Engine = install.Describe(),
                        Exact = p.Exact,
                        Spheres = p.Steps.Count,
                        Late = p.Late,
                        Early = p.Early,
                        Verdict = p.Verdict
                    });
                }
                return report;
            }, ct);

        private static void Remember(SeedVerification result)
        {
            lock (Store)
            {
                Store.RemoveAll(v => string.Equals(v.Game, result.Game, StringComparison.OrdinalIgnoreCase) && v.Checksum == result.Checksum && v.SeedName == result.SeedName);
                Store.Add(result);
                try { SafeFile.WriteJson(StorePath, Store); }
                catch (Exception ex) { Logger.LogWarning("Couldn't save the seed test results: " + ex.Message); }
            }
        }
    }
}
