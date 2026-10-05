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
        /// <summary>The GitHub repository ("owner/name") it's published in, when known.</summary>
        [JsonIgnore] public string Repo { get; set; }
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

        // =====================================================================
        // Repositories: where a game's apworld versions are published
        // =====================================================================

        /// <summary>"owner/name" from a GitHub link in any form (repository, releases page, a release, a download link), or null.</summary>
        public static string ParseRepo(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            var m = Regex.Match(text, @"github\.com[/:]([A-Za-z0-9_.\-]+)/([A-Za-z0-9_.\-]+?)(?:\.git)?(?:[/#?].*)?$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value + "/" + m.Groups[2].Value;
            m = Regex.Match(text, @"^([A-Za-z0-9_.\-]+)/([A-Za-z0-9_.\-]+)$");
            return m.Success ? m.Groups[1].Value + "/" + m.Groups[2].Value : null;
        }

        private sealed class ReleaseAsset
        {
            public string Tag, Name, Url, Sha256;
            public bool Prerelease;
        }

        /// <summary>A repository's published .apworld files; <see cref="Problem"/> is set when GitHub couldn't be asked (nothing is known then).</summary>
        private sealed class RepoListing
        {
            public readonly List<ReleaseAsset> Assets = new();
            public string Problem;
        }

        private static readonly Dictionary<string, RepoListing> _repoAssets = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Every .apworld a repository published in its GitHub releases (one API call per repository per session). Only a real
        /// answer is kept: a rate limit or a network failure is reported, never remembered as "no apworlds".
        /// </summary>
        private static async Task<RepoListing> RepoAssetsAsync(string repo, CancellationToken ct)
        {
            lock (_repoAssets) if (_repoAssets.TryGetValue(repo, out var known)) return known;
            var listing = new RepoListing();
            var assets = listing.Assets;
            var answer = await GitHubApi.GetAsync($"/repos/{repo}/releases?per_page=50", ct);
            if (answer.Ok && answer.Json is JArray releases)
            {
                foreach (var release in releases)
                {
                    if (release["draft"]?.Value<bool>() == true) continue;
                    foreach (var asset in release["assets"] as JArray ?? new JArray())
                    {
                        string name = asset["name"]?.ToString() ?? "";
                        if (!name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase)) continue;
                        string digest = asset["digest"]?.ToString();
                        assets.Add(new ReleaseAsset
                        {
                            Tag = release["tag_name"]?.ToString() ?? "",
                            Name = name,
                            Url = asset["browser_download_url"]?.ToString(),
                            Sha256 = digest != null && digest.StartsWith("sha256:") ? digest.Substring(7).ToLowerInvariant() : null,
                            Prerelease = release["prerelease"]?.Value<bool>() == true
                        });
                    }
                }
            }
            else if (!answer.NotFound)
            {
                listing.Problem = answer.Ok ? "GitHub's answer wasn't a list of releases." : answer.Message;
                Logger.LogWarning($"Couldn't list {repo}'s releases: {listing.Problem}");
            }
            if (listing.Problem == null) lock (_repoAssets) _repoAssets[repo] = listing;
            return listing;
        }

        /// <summary>A repository's versions of one apworld (by file name; a release with a single .apworld counts too).</summary>
        private static async Task<List<ApworldVersion>> RepoVersionsAsync(string repo, string apworldName, CancellationToken ct, List<string> problems = null)
        {
            var listing = await RepoAssetsAsync(repo, ct);
            if (listing.Problem != null) problems?.Add($"github.com/{repo}: {listing.Problem}");
            var assets = listing.Assets;
            var versions = new List<ApworldVersion>();
            foreach (var group in assets.GroupBy(a => a.Tag))
            {
                var asset = group.FirstOrDefault(a => apworldName != null && string.Equals(a.Name, apworldName + ".apworld", StringComparison.OrdinalIgnoreCase))
                            ?? (group.Count() == 1 ? group.First() : null);
                if (asset?.Url == null) continue;
                versions.Add(new ApworldVersion { Version = group.Key.TrimStart('v', 'V'), Url = asset.Url, Sha256 = asset.Sha256, Origin = "github", Repo = repo });
            }
            return versions;
        }

        /// <summary>A repository Atlas looks in for a game's apworld versions, and why.</summary>
        public class ApworldRepo
        {
            public string Repo { get; set; }
            public string Reason { get; set; }
            public bool Approved { get; set; }
            public bool InstalledFrom { get; set; }
            public string Display => "github.com/" + Repo;
        }

        public static bool IsRepoApproved(AppSettings settings, string repo) => IsApproved(settings, $"https://github.com/{repo}/");

        public static void ApproveRepo(AppSettings settings, string repo) => Approve(settings, $"https://github.com/{repo}/");

        /// <summary>Copies of a game's apworld in an engine's world folders, with their SHA-256 (to recognise their published source).</summary>
        public static List<(string File, string Sha256)> InstalledCopies(EngineInstall install, string game)
        {
            var copies = new List<(string, string)>();
            if (install == null) return copies;
            foreach (var dir in install.WorldFolders())
                foreach (var file in EngineInstall.SafeFiles(dir, "*.apworld"))
                {
                    var info = FileFacts(file);
                    if (info.Sha256 != null && string.Equals(info.Game, game, StringComparison.OrdinalIgnoreCase)) copies.Add((file, info.Sha256));
                }
            return copies;
        }

        private static readonly Dictionary<string, (DateTime Modified, long Size, string Game, string Sha256)> _fileFacts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>An apworld file's game and SHA-256, worked out once per change of the file.</summary>
        private static (string Game, string Sha256) FileFacts(string file)
        {
            try
            {
                var fi = new FileInfo(file);
                lock (_fileFacts)
                    if (_fileFacts.TryGetValue(file, out var f) && f.Modified == fi.LastWriteTimeUtc && f.Size == fi.Length) return (f.Game, f.Sha256);
                string game = AtlasEngine.GameOfApworld(file), sha = EngineDownloader.Sha256Of(file);
                lock (_fileFacts) _fileFacts[file] = (fi.LastWriteTimeUtc, fi.Length, game, sha);
                return (game, sha);
            }
            catch { return (null, null); }
        }

        private class ProvenanceRecord
        {
            public string Repo { get; set; }
            public string Tag { get; set; }
            public DateTime Checked { get; set; }
        }

        private static string ProvenancePath => Path.Combine(CacheDir, "installed_sources.json");
        private static Dictionary<string, ProvenanceRecord> _provenance;
        private static Dictionary<string, ProvenanceRecord> Provenance => _provenance ??= SafeFile.ReadJson(ProvenancePath, () => new Dictionary<string, ProvenanceRecord>(StringComparer.OrdinalIgnoreCase));

        /// <summary>Where an installed apworld file was published ("owner/name" and release tag), if Atlas has found out.</summary>
        public static (string Repo, string Tag) KnownSourceOf(string sha256)
        {
            if (string.IsNullOrEmpty(sha256)) return (null, null);
            lock (Provenance) return Provenance.TryGetValue(sha256, out var r) && r.Repo != null ? (r.Repo, r.Tag) : (null, null);
        }

        /// <summary>
        /// Finds the GitHub repository that published one of these exact files (by the SHA-256 GitHub records for every
        /// release download, so nothing is downloaded): the known repositories first, then repositories about the game
        /// and repositories with the same name as a known one (forks and re-uploads often have no fork link). Results
        /// are kept for a week, so GitHub's rate limits are barely touched.
        /// </summary>
        public static async Task<(string Repo, string Tag)> FindInstalledSourceAsync(string game, IReadOnlyCollection<string> installedSha256, IEnumerable<string> knownRepos, Action<string> log, CancellationToken ct)
        {
            if (installedSha256 == null || installedSha256.Count == 0) return (null, null);
            var hashes = new HashSet<string>(installedSha256.Select(h => h.ToLowerInvariant()));
            lock (Provenance)
                foreach (var h in hashes)
                    if (Provenance.TryGetValue(h, out var record) && (record.Repo != null || (DateTime.Now - record.Checked).TotalDays < 7))
                        return (record.Repo, record.Tag);

            // Set when any lookup couldn't be done: then "not found" isn't known, so it isn't remembered.
            bool incomplete = false;
            async Task<(string, string)> CheckAsync(string repo)
            {
                var listing = await RepoAssetsAsync(repo, ct);
                if (listing.Problem != null) incomplete = true;
                var hit = listing.Assets.FirstOrDefault(a => a.Sha256 != null && hashes.Contains(a.Sha256));
                return hit != null ? (repo, hit.Tag) : (null, null);
            }

            var known = knownRepos.Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            (string Repo, string Tag) found = (null, null);
            foreach (var repo in known)
            {
                found = await CheckAsync(repo);
                if (found.Repo != null) break;
            }
            if (found.Repo == null)
            {
                log?.Invoke($"Looking on GitHub for where your installed {game} apworld was published…");
                var candidates = new List<string>();
                async Task SearchAsync(string query, Func<JToken, bool> keep)
                {
                    var answer = await GitHubApi.GetAsync($"/search/repositories?q={Uri.EscapeDataString(query)}&per_page=20", ct);
                    if (!answer.Ok)
                    {
                        incomplete = true;
                        Logger.LogWarning($"GitHub search for {game} apworlds didn't work: {answer.Message}");
                        return;
                    }
                    foreach (var item in answer.Json?["items"] as JArray ?? new JArray())
                    {
                        string name = item["full_name"]?.ToString();
                        if (name != null && keep(item) && !known.Contains(name, StringComparer.OrdinalIgnoreCase) && !candidates.Contains(name, StringComparer.OrdinalIgnoreCase))
                            candidates.Add(name);
                    }
                }
                // Repositories about the game (precise), then ones named exactly like a known project (forks, re-uploads).
                await SearchAsync($"\"{game}\" archipelago in:name,description", _ => true);
                foreach (var name in known.Select(r => r.Split('/').Last()).Distinct(StringComparer.OrdinalIgnoreCase))
                    await SearchAsync($"{name} in:name", item => string.Equals(item["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase));
                foreach (var repo in candidates.Take(10))
                {
                    found = await CheckAsync(repo);
                    if (found.Repo != null) break;
                }
            }
            // A find is kept; "not found" only when every lookup really happened (not after a rate limit or a failure).
            if (found.Repo != null || !incomplete)
            {
                lock (Provenance)
                {
                    foreach (var h in hashes) Provenance[h] = new ProvenanceRecord { Repo = found.Repo, Tag = found.Tag, Checked = DateTime.Now };
                    try { SafeFile.WriteJson(ProvenancePath, Provenance); }
                    catch (Exception ex) { Logger.LogWarning("Couldn't save where installed apworlds came from: " + ex.Message); }
                }
            }
            else log?.Invoke("GitHub couldn't be fully checked just now, so Atlas will look again next time.");
            if (found.Repo != null) log?.Invoke($"Your installed {game} apworld is {found.Tag} from github.com/{found.Repo}.");
            return found;
        }

        /// <summary>
        /// Where to look for a game's apworld versions, in order: the repository your installed copy came from (the
        /// group playing likely uses it too), the one in the community index, and any you added.
        /// </summary>
        public static async Task<List<ApworldRepo>> ReposForAsync(AppSettings settings, EngineInstall install, string game, Action<string> log, CancellationToken ct)
        {
            var source = Find(game);
            var known = new List<string>();
            if (source?.Repo != null) known.Add(source.Repo);
            if (settings?.ExtraApworldRepos != null && settings.ExtraApworldRepos.TryGetValue(game, out var added))
                foreach (var r in added) if (!known.Contains(r, StringComparer.OrdinalIgnoreCase)) known.Add(r);

            var installed = InstalledCopies(install, game);
            var (from, tag) = await FindInstalledSourceAsync(game, installed.Select(c => c.Sha256).ToList(), known, log, ct);

            var repos = new List<ApworldRepo>();
            if (from != null) repos.Add(new ApworldRepo { Repo = from, Reason = $"your installed copy ({tag}) came from here", InstalledFrom = true });
            if (source?.Repo != null && !repos.Any(r => r.Repo.Equals(source.Repo, StringComparison.OrdinalIgnoreCase)))
                repos.Add(new ApworldRepo { Repo = source.Repo, Reason = "listed in the community index" });
            foreach (var r in known.Where(k => !repos.Any(x => x.Repo.Equals(k, StringComparison.OrdinalIgnoreCase))))
                repos.Add(new ApworldRepo { Repo = r, Reason = "added by you" });
            foreach (var r in repos) r.Approved = IsRepoApproved(settings, r.Repo);
            return repos;
        }

        /// <summary>
        /// Adds a repository (pasted link) as a source of a game's apworld. It must publish .apworld files in its
        /// releases. Adding it trusts it: Atlas may download from it to match seeds.
        /// </summary>
        public static async Task<(string Repo, string Problem)> AddUserRepoAsync(AppSettings settings, string game, string link, CancellationToken ct)
        {
            string repo = ParseRepo(link);
            if (repo == null) return (null, "that isn't a GitHub repository link (e.g. https://github.com/owner/project/releases)");
            lock (_repoAssets) _repoAssets.Remove(repo);
            var listing = await RepoAssetsAsync(repo, ct);
            if (listing.Problem != null) return (null, $"GitHub couldn't be checked just now ({listing.Problem.TrimEnd('.')}); try again later");
            if (listing.Assets.Count == 0) return (null, $"github.com/{repo} has no .apworld files in its releases");
            settings.ExtraApworldRepos ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (!settings.ExtraApworldRepos.TryGetValue(game, out var list)) settings.ExtraApworldRepos[game] = list = new List<string>();
            if (!list.Contains(repo, StringComparer.OrdinalIgnoreCase)) list.Add(repo);
            ApproveRepo(settings, repo);
            DataManager.SaveSettings(settings);
            return (repo, null);
        }

        // =====================================================================
        // Versions, downloads, identification
        // =====================================================================

        /// <summary>
        /// Known versions of a game's apworld from these repositories (newest first within each repository, repositories
        /// in the given order), including the community index's versions for its repository. The same file published
        /// twice (same SHA-256) appears once.
        /// </summary>
        public static async Task<List<ApworldVersion>> VersionsAsync(string game, IEnumerable<string> repos, string apworldName, CancellationToken ct, List<string> problems = null)
        {
            var source = Find(game);
            apworldName ??= source?.Apworld;
            var result = new List<ApworldVersion>();
            foreach (var repo in repos.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var versions = await RepoVersionsAsync(repo, apworldName, ct, problems);
                if (source != null && string.Equals(source.Repo, repo, StringComparison.OrdinalIgnoreCase))
                    foreach (var v in source.Versions)
                        if (!versions.Any(x => x.Url == v.Url || (x.Sha256 != null && v.Sha256 != null && x.Sha256.Equals(v.Sha256, StringComparison.OrdinalIgnoreCase))))
                            versions.Add(new ApworldVersion { Version = v.Version, Url = v.Url, Sha256 = v.Sha256, Origin = "index", Repo = repo });
                foreach (var v in versions.OrderByDescending(v => SortKey(v.Version)))
                    if (!result.Any(x => x.Url == v.Url || (x.Sha256 != null && v.Sha256 != null && x.Sha256.Equals(v.Sha256, StringComparison.OrdinalIgnoreCase))))
                        result.Add(v);
            }
            return result;
        }

        /// <summary>Every known version of a game's apworld from the listed source and any repositories the user added (newest first).</summary>
        public static Task<List<ApworldVersion>> VersionsAsync(ApworldSource source, CancellationToken ct) =>
            VersionsAsync(source.Game, new[] { source.Repo }.Concat(ExtraReposOf(source.Game)).Where(r => r != null), source.Apworld, ct);

        private static AppSettings _settingsForExtras;

        /// <summary>Lets the sources read the user's added repositories (set at startup).</summary>
        public static void Initialize(AppSettings settings) => _settingsForExtras = settings;

        private static IEnumerable<string> ExtraReposOf(string game) =>
            _settingsForExtras?.ExtraApworldRepos != null && _settingsForExtras.ExtraApworldRepos.TryGetValue(game, out var list) ? list : Enumerable.Empty<string>();

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

        /// <summary>Downloads one version into the cache (kept for reuse), verified against its published SHA-256 when there is one.</summary>
        public static async Task<string> DownloadAsync(string game, string apworldName, ApworldVersion version, Action<string> log, CancellationToken ct)
        {
            string fileName = Path.GetFileName(new Uri(version.Url).AbsolutePath);
            if (!fileName.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase)) fileName = (apworldName ?? "world") + ".apworld";
            // Per source and version (two projects may publish the same version label), and the file name stays as
            // published: Archipelago imports the package named after it.
            string origin = Regex.Replace(SourceKey(version.Url).Replace("github.com/", ""), @"[^\w.\-+]", "_");
            string target = Path.Combine(CacheDir, origin, Regex.Replace(NormalizeVersion(version.Version), @"[^\w.\-+]", "_"), fileName);
            if (File.Exists(target) && (version.Sha256 == null || EngineDownloader.Sha256Of(target).Equals(version.Sha256, StringComparison.OrdinalIgnoreCase))) return target;
            log?.Invoke($"Downloading {game} {version.Version} from {SourceKey(version.Url)}" + (version.Sha256 != null ? " (checked against its published SHA-256)…" : " (no published hash to check against)…"));
            await EngineDownloader.DownloadAsync(version.Url, target, version.Sha256, null, ct);
            return target;
        }

        public static Task<string> DownloadAsync(ApworldSource source, ApworldVersion version, Action<string> log, CancellationToken ct) =>
            DownloadAsync(source.Game, source.Apworld, version, log, ct);

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
        /// Finds the version of a game's apworld whose data matches a seed's checksum: tries the versions published in
        /// these repositories (in order, newest first, up to maxTries), each downloaded (hash-checked) and loaded in a
        /// throwaway process. Files identified before aren't loaded again. Returns the matching file, or null.
        /// </summary>
        public static Task<(ApworldVersion Version, string File)> FindMatchingAsync(EngineInstall install, string game, string seedChecksum, IEnumerable<string> repos, Action<string> log, CancellationToken ct, int maxTries = 40) =>
            AtlasEngine.ExclusiveAsync(async () =>
            {
                var cached = CachedFor(game, seedChecksum);
                if (cached != null)
                {
                    log?.Invoke($"Already have {game} {cached.Version ?? "?"} matching the seed.");
                    return (new ApworldVersion { Version = cached.Version, Url = cached.File }, cached.File);
                }
                var repoList = repos.ToList();
                string apworldName = Find(game)?.Apworld ?? InstalledCopies(install, game).Select(c => Path.GetFileNameWithoutExtension(c.File)).FirstOrDefault();
                var problems = new List<string>();
                var versions = await VersionsAsync(game, repoList, apworldName, ct, problems);
                foreach (var p in problems) log?.Invoke("  Couldn't read the releases of " + p);
                var installedHashes = new HashSet<string>(InstalledCopies(install, game).Select(c => c.Sha256), StringComparer.OrdinalIgnoreCase);
                versions = versions.Where(v => v.Sha256 == null || !installedHashes.Contains(v.Sha256)).ToList();
                log?.Invoke($"Looking for the {game} apworld the seed was made with among {versions.Count} version(s) in {string.Join(", ", repoList.Select(r => "github.com/" + r))}…");
                int tried = 0;
                foreach (var version in versions)
                {
                    if (tried++ >= maxTries) { log?.Invoke($"Stopped after {maxTries} versions."); break; }
                    string label = $"{version.Version} ({SourceKey(version.Url).Replace("github.com/", "")})";
                    string file;
                    try { file = await DownloadAsync(game, apworldName, version, log, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { log?.Invoke($"  {label}: couldn't download ({ex.Message})"); continue; }
                    string checksum;
                    CachedApworld known;
                    lock (CacheIndex) known = CacheIndex.FirstOrDefault(c => string.Equals(c.File, file, StringComparison.OrdinalIgnoreCase));
                    if (known != null) checksum = known.Checksum;
                    else
                    {
                        var id = await IdentifyAsync(install, file, game, ct);
                        if (id.Error != null) { log?.Invoke($"  {label}: couldn't be loaded here ({FirstLine(id.Error)})"); continue; }
                        // Every identified version is remembered, so another seed made with it is matched instantly.
                        Remember(id.Game, id.Checksum, version.Version, file, $"{SourceKey(version.Url)} {version.Version}");
                        checksum = id.Checksum;
                    }
                    bool match = string.Equals(checksum, seedChecksum, StringComparison.OrdinalIgnoreCase);
                    log?.Invoke($"  {label}: data {Short(checksum)}{(match ? " ✔ matches the seed" : "")}");
                    if (match) return (version, file);
                }
                log?.Invoke(problems.Count > 0 && versions.Count == 0
                    ? "GitHub couldn't be checked just now, so Atlas couldn't look for the seed's version. It will try again later."
                    : $"None of the versions tried matches the seed's data ({Short(seedChecksum)}). The seed may use an unreleased or unlisted build: ask its host for the apworld, or add the project it came from.");
                return ((ApworldVersion)null, (string)null);
            }, ct);

        private static string Short(string checksum) => string.IsNullOrEmpty(checksum) ? "?" : checksum.Substring(0, Math.Min(8, checksum.Length));

        private static string FirstLine(string s) => (s ?? "").Trim().Split('\n').Last().Trim();
    }
}
