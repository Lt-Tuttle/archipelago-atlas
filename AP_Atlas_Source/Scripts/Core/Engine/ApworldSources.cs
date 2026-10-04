using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.EngineSetup
{
    public class ApworldVersion
    {
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        /// <summary>The published SHA-256 (the community index's lock file or GitHub's asset digest); null when none is published.</summary>
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        /// <summary>"index" (the bundled or refreshed list) or "github" (found in the project's releases).</summary>
        [JsonIgnore] public string Origin { get; set; } = "index";
    }

    public class ApworldSource
    {
        [JsonProperty("game")] public string Game { get; set; }
        [JsonProperty("display_name")] public string DisplayName { get; set; }
        [JsonProperty("apworld")] public string Apworld { get; set; }
        [JsonProperty("home")] public string Home { get; set; }
        [JsonProperty("repo")] public string Repo { get; set; }
        [JsonProperty("versions")] public List<ApworldVersion> Versions { get; set; } = new();
    }

    /// <summary>An apworld file Atlas has on disk and has identified: which game, which data checksum.</summary>
    public class CachedApworld
    {
        public string Game { get; set; }
        public string Checksum { get; set; }
        public string Version { get; set; }
        public string File { get; set; }
        /// <summary>Where it came from: a download source key, or "your file".</summary>
        public string Source { get; set; }
        public DateTime Added { get; set; } = DateTime.Now;
    }

    public class ApworldSourceList
    {
        [JsonProperty("format")] public string Format { get; set; }
        [JsonProperty("built")] public string Built { get; set; }
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("games")] public List<ApworldSource> Games { get; set; } = new();
    }

    /// <summary>
    /// Where game apworlds can be downloaded: a list bundled with Atlas (built from the community Archipelago-index,
    /// with each version's SHA-256), optionally refreshed from a URL in settings, plus the project's GitHub releases,
    /// looked up on demand. Every download is verified against its published hash when one exists, and nothing is
    /// downloaded without the user's approval of the source (apworlds are programs that run in the engine).
    /// </summary>
    public static class ApworldSources
    {
        private const string Format = "atlas-apworld-sources/1";
        private static ApworldSourceList _list;
        private static readonly Dictionary<string, List<ApworldVersion>> _releaseCache = new(StringComparer.OrdinalIgnoreCase);

        private static string RefreshedPath => Path.Combine(AtlasEngine.EngineDir, "apworld_sources.json");
        private static string CacheDir => Path.Combine(AtlasEngine.EngineDir, "apworld_cache");
        private static string CacheIndexPath => Path.Combine(CacheDir, "index.json");

        /// <summary>The cache index file (for engine components that pick the seed's version themselves).</summary>
        public static string CacheIndexFile => CacheIndexPath;
        private static List<CachedApworld> _cacheIndex;
        private static List<CachedApworld> CacheIndex => _cacheIndex ??= SafeFile.ReadJson(CacheIndexPath, () => new List<CachedApworld>());

        /// <summary>
        /// A cached apworld whose data matches this checksum (a seed's), if Atlas has one. Slots run with it in place
        /// of the installed copy, so the user's install is never changed and different seeds can use different versions.
        /// </summary>
        public static CachedApworld CachedFor(string game, string checksum)
        {
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(checksum)) return null;
            lock (CacheIndex)
                return CacheIndex.Where(c => string.Equals(c.Game, game, StringComparison.OrdinalIgnoreCase)
                                             && string.Equals(c.Checksum, checksum, StringComparison.OrdinalIgnoreCase)
                                             && File.Exists(c.File))
                    .OrderByDescending(c => c.Added).FirstOrDefault();
        }

        private static void Remember(string game, string checksum, string version, string file, string source)
        {
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(checksum) || string.IsNullOrEmpty(file)) return;
            lock (CacheIndex)
            {
                CacheIndex.RemoveAll(c => string.Equals(c.File, file, StringComparison.OrdinalIgnoreCase) || !File.Exists(c.File));
                CacheIndex.Add(new CachedApworld { Game = game, Checksum = checksum, Version = version, File = file, Source = source });
                try { SafeFile.WriteJson(CacheIndexPath, CacheIndex); }
                catch (Exception ex) { Logger.LogWarning("Couldn't save the apworld cache index: " + ex.Message); }
            }
        }

        /// <summary>
        /// Adds an apworld the user chose (e.g. from the seed's host) to the cache, if its data matches the expected
        /// checksum. Returns the cache entry, or an explanation of why it doesn't match.
        /// </summary>
        public static async Task<(CachedApworld Entry, string Problem)> AddUserFileAsync(EngineInstall install, string file, string game, string expectedChecksum, CancellationToken ct)
        {
            var id = await IdentifyAsync(install, file, game, ct);
            if (id.Error != null) return (null, "it couldn't be loaded: " + FirstLine(id.Error));
            if (!string.Equals(id.Game, game, StringComparison.OrdinalIgnoreCase)) return (null, $"it's for {id.Game}, not {game}");
            if (!string.Equals(id.Checksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
                return (null, $"it's a different version (data {Short(id.Checksum)}{(id.WorldVersion != null ? ", version " + id.WorldVersion : "")}) than the seed's ({Short(expectedChecksum)})");
            string target = Path.Combine(CacheDir, "yours", id.Checksum.Substring(0, Math.Min(12, id.Checksum.Length)), Path.GetFileName(file));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target, true);
            Remember(id.Game, id.Checksum, id.WorldVersion, target, "your file");
            return (CachedFor(game, expectedChecksum), null);
        }

        public static ApworldSourceList List => _list ??= Load();

        private static ApworldSourceList Load()
        {
            ApworldSourceList bundled = null;
            try
            {
                using var stream = typeof(ApworldSources).Assembly.GetManifestResourceStream("AtlasEngine.apworld_sources.json");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                bundled = JsonConvert.DeserializeObject<ApworldSourceList>(reader.ReadToEnd());
            }
            catch (Exception ex) { Logger.LogWarning("Couldn't read the bundled apworld source list: " + ex.Message); }
            var refreshed = SafeFile.ReadJson<ApworldSourceList>(RefreshedPath, () => null);
            if (refreshed?.Format == Format && string.CompareOrdinal(refreshed.Built ?? "", bundled?.Built ?? "") > 0) return refreshed;
            return bundled ?? new ApworldSourceList { Format = Format, Games = new() };
        }

        /// <summary>The source for a game (by its Archipelago game name), or null if no source is known.</summary>
        public static ApworldSource Find(string game) =>
            string.IsNullOrEmpty(game) ? null :
            List.Games.FirstOrDefault(g => string.Equals(g.Game, game, StringComparison.OrdinalIgnoreCase))
            ?? List.Games.FirstOrDefault(g => string.Equals(g.DisplayName, game, StringComparison.OrdinalIgnoreCase));

        /// <summary>Downloads a newer list from a URL (e.g. a maintained fork of the index), validated before it's used.</summary>
        public static async Task<string> RefreshAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url)) return "No refresh URL is set.";
            string text = await EngineDownloader.GetStringAsync(url, ct);
            var list = JsonConvert.DeserializeObject<ApworldSourceList>(text);
            if (list?.Format != Format || list.Games == null || list.Games.Count == 0)
                return "That URL didn't return an Atlas apworld source list; the current list is unchanged.";
            if (list.Games.Any(g => g.Versions.Any(v => !v.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))))
                return "That list has non-HTTPS download links; it wasn't used.";
            SafeFile.WriteJson(RefreshedPath, list, Formatting.None);
            _list = null;
            return $"Source list updated: {list.Games.Count} games (built {list.Built}).";
        }

        /// <summary>
        /// Every known version of a game's apworld, newest first: the list's, plus the project's GitHub releases (one
        /// API call per game per session, so it's light on GitHub's rate limits).
        /// </summary>
        public static async Task<List<ApworldVersion>> VersionsAsync(ApworldSource source, CancellationToken ct)
        {
            var versions = source.Versions.Select(v => new ApworldVersion { Version = v.Version, Url = v.Url, Sha256 = v.Sha256, Origin = "index" }).ToList();
            if (!string.IsNullOrEmpty(source.Repo))
            {
                List<ApworldVersion> releases;
                lock (_releaseCache) _releaseCache.TryGetValue(source.Repo, out releases);
                if (releases == null)
                {
                    releases = new List<ApworldVersion>();
                    try
                    {
                        string json = await EngineDownloader.GetStringAsync($"https://api.github.com/repos/{source.Repo}/releases?per_page=40", ct);
                        foreach (var release in JArray.Parse(json))
                        {
                            if (release["draft"]?.Value<bool>() == true) continue;
                            var assets = (release["assets"] as JArray ?? new JArray())
                                .Where(a => (a["name"]?.ToString() ?? "").EndsWith(".apworld", StringComparison.OrdinalIgnoreCase)).ToList();
                            var asset = assets.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), source.Apworld + ".apworld", StringComparison.OrdinalIgnoreCase))
                                        ?? (assets.Count == 1 ? assets[0] : null);
                            if (asset == null) continue;
                            string digest = asset["digest"]?.ToString();
                            releases.Add(new ApworldVersion
                            {
                                Version = (release["tag_name"]?.ToString() ?? "").TrimStart('v', 'V'),
                                Url = asset["browser_download_url"]?.ToString(),
                                Sha256 = digest != null && digest.StartsWith("sha256:") ? digest.Substring(7) : null,
                                Origin = "github"
                            });
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Logger.LogWarning($"Couldn't list {source.Repo}'s releases: {ex.Message}"); }
                    lock (_releaseCache) _releaseCache[source.Repo] = releases;
                }
                foreach (var r in releases)
                    if (!string.IsNullOrEmpty(r.Url) && !versions.Any(v => v.Url == r.Url || NormalizeVersion(v.Version) == NormalizeVersion(r.Version)))
                        versions.Add(r);
            }
            return versions.OrderByDescending(v => SortKey(v.Version)).ToList();
        }

        private static string NormalizeVersion(string v) => (v ?? "").Trim().TrimStart('v', 'V').ToLowerInvariant();

        /// <summary>Sorts "0.10.1" after "0.9.3" (numeric parts first, then any suffix).</summary>
        private static string SortKey(string version)
        {
            var parts = Regex.Matches(NormalizeVersion(version), @"\d+").Select(m => m.Value.PadLeft(8, '0')).Take(4);
            return string.Join(".", parts) + "|" + NormalizeVersion(version);
        }

        // --- Approval ---

        /// <summary>What the user approves: the GitHub project, or the host for other links.</summary>
        public static string SourceKey(string url)
        {
            var m = Regex.Match(url ?? "", @"^https://github\.com/([^/]+/[^/]+)/", RegexOptions.IgnoreCase);
            if (m.Success) return "github.com/" + m.Groups[1].Value;
            try { return new Uri(url).Host; } catch { return url ?? ""; }
        }

        public static bool IsApproved(AppSettings settings, string url) =>
            settings?.ApprovedApworldSources?.Contains(SourceKey(url), StringComparer.OrdinalIgnoreCase) == true;

        public static void Approve(AppSettings settings, string url)
        {
            if (settings == null) return;
            settings.ApprovedApworldSources ??= new List<string>();
            string key = SourceKey(url);
            if (!settings.ApprovedApworldSources.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                settings.ApprovedApworldSources.Add(key);
                DataManager.SaveSettings(settings);
            }
        }

        // --- Download and identify ---

        /// <summary>Downloads one version into the cache (kept for reuse), verified against its published SHA-256 when there is one.</summary>
        public static async Task<string> DownloadAsync(ApworldSource source, ApworldVersion version, Action<string> log, CancellationToken ct)
        {
            string fileName = Path.GetFileName(new Uri(version.Url).AbsolutePath);
            if (!fileName.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase)) fileName = source.Apworld + ".apworld";
            // The file name must stay as published: Archipelago imports the package named after it.
            string target = Path.Combine(CacheDir, Regex.Replace(NormalizeVersion(version.Version), @"[^\w.\-+]", "_"), fileName);
            if (File.Exists(target) && (version.Sha256 == null || EngineDownloader.Sha256Of(target).Equals(version.Sha256, StringComparison.OrdinalIgnoreCase))) return target;
            log?.Invoke($"Downloading {source.Game} {version.Version} from {SourceKey(version.Url)}" + (version.Sha256 != null ? " (checked against its published SHA-256)…" : " (no published hash to check against)…"));
            await EngineDownloader.DownloadAsync(version.Url, target, version.Sha256, null, ct);
            return target;
        }

        /// <summary>The data checksum of a candidate apworld file (loaded in a throwaway engine process).</summary>
        public static async Task<(string Game, string Checksum, string WorldVersion, string Error)> IdentifyAsync(EngineInstall install, string file, string game, CancellationToken ct)
        {
            JObject result = null;
            var errors = new StringBuilder();
            string request = JsonConvert.SerializeObject(new { apworld = Path.GetFullPath(file), game });
            await AtlasEngine.RunComponentAsync(install, "AtlasChecksum", request, line =>
            {
                string t = line.Trim();
                if (t.StartsWith("{")) { try { result = JObject.Parse(t); } catch { } }
            }, err => { if (errors.Length < 2000) errors.AppendLine(err); }, ct, TimeSpan.FromMinutes(3));
            if (result == null) return (null, null, null, "The engine didn't report a checksum.");
            return (result["game"]?.ToString(), result["data_checksum"]?.ToString(), result["world_version"]?.ToString(), result["error"]?.ToString());
        }

        /// <summary>
        /// Finds the version of a game's apworld whose data matches a seed's checksum: tries known versions newest
        /// first (up to maxTries), each downloaded (hash-checked) and loaded in a throwaway process. Returns the
        /// matching file, or null.
        /// </summary>
        public static Task<(ApworldVersion Version, string File)> FindMatchingAsync(EngineInstall install, string game, string seedChecksum, Action<string> log, CancellationToken ct, int maxTries = 15) =>
            AtlasEngine.Exclusive(async () =>
            {
                var cached = CachedFor(game, seedChecksum);
                if (cached != null)
                {
                    log?.Invoke($"Already have {game} {cached.Version ?? "?"} matching the seed.");
                    return (new ApworldVersion { Version = cached.Version, Url = cached.File }, cached.File);
                }
                var source = Find(game) ?? throw new Exception($"No download source is known for {game}.");
                var versions = await VersionsAsync(source, ct);
                log?.Invoke($"Looking for the {game} apworld the seed was made with among {versions.Count} known version(s)…");
                int tried = 0;
                foreach (var version in versions)
                {
                    if (tried++ >= maxTries) { log?.Invoke($"Stopped after {maxTries} versions."); break; }
                    string file;
                    try { file = await DownloadAsync(source, version, log, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { log?.Invoke($"  {version.Version}: couldn't download ({ex.Message})"); continue; }
                    var id = await IdentifyAsync(install, file, game, ct);
                    if (id.Error != null) { log?.Invoke($"  {version.Version}: couldn't be loaded here ({FirstLine(id.Error)})"); continue; }
                    // Every identified version is remembered, so another seed made with it is matched instantly.
                    Remember(id.Game, id.Checksum, version.Version, file, SourceKey(version.Url));
                    bool match = string.Equals(id.Checksum, seedChecksum, StringComparison.OrdinalIgnoreCase);
                    log?.Invoke($"  {version.Version}: data {Short(id.Checksum)}{(match ? " ✔ matches the seed" : "")}");
                    if (match) return (version, file);
                }
                log?.Invoke($"None of the versions tried matches the seed's data ({Short(seedChecksum)}). The seed may use an unreleased or unlisted build: ask its host for the apworld.");
                return ((ApworldVersion)null, (string)null);
            }, ct);

        private static string Short(string checksum) => string.IsNullOrEmpty(checksum) ? "?" : checksum.Substring(0, Math.Min(8, checksum.Length));

        private static string FirstLine(string s) => (s ?? "").Trim().Split('\n').Last().Trim();
    }
}
