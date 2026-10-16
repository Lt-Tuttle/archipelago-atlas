using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core.Games;

/// <summary>A pack Atlas's game list names, as a download: its address, the file name it keeps, the SHA-256 to check (none for current files).</summary>
public sealed record IndexPackDownload(string Repo, string Url, string FileName, string? Sha256, bool Zipball);

/// <summary>
/// Where a pack Atlas's game list names is downloaded from, without asking GitHub which releases there are: the release's
/// own file (github.com/owner/name/releases/download/tag/file, checked against the SHA-256 the pack lab measured), or, for
/// a pack listed as its project's current files, the project's zipball from GitHub's API (no hash: the files may have
/// changed since the lab measured them). Null when the entry doesn't name a project, a release and a file plainly.
/// </summary>
public static class IndexPackDownloads
{
    public const string CurrentFiles = "zipball";

    /// <summary>Where release files and GitHub's API live (a test passes its own site).</summary>
    public const string GitHubSite = "https://github.com", ApiSite = "https://api.github.com";
    private static readonly Regex RepoName = new(@"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$", RegexOptions.CultureInvariant);

    public static IndexPackDownload? For(string? repo, string? release, string? asset, string? sha256, string githubSite, string apiSite)
    {
        if (string.IsNullOrWhiteSpace(repo) || !RepoName.IsMatch(repo) || repo.Split('/').Any(Dots)) return null;
        if (string.Equals(asset, CurrentFiles, StringComparison.OrdinalIgnoreCase))
            return new(repo, apiSite.TrimEnd('/') + "/repos/" + repo + "/zipball", repo.Replace('/', '_') + ".zip", null, true);
        if (string.IsNullOrWhiteSpace(release) || string.IsNullOrWhiteSpace(asset) || asset.Contains('/') || asset.Contains('\\') || Dots(asset)) return null;
        var segments = release.Split('/');
        if (segments.Any(s => s.Length == 0 || Dots(s))) return null;
        string tag = string.Join("/", segments.Select(Uri.EscapeDataString));
        return new(repo, $"{githubSite.TrimEnd('/')}/{repo}/releases/download/{tag}/{Uri.EscapeDataString(asset)}", asset,
            string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant(), false);
    }

    private static bool Dots(string segment) => segment is "." or "..";
}
