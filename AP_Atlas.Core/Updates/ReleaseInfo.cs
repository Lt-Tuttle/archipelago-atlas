using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Updates;

/// <summary>
/// One release of Atlas as GitHub lists it: its version, whether it's a pre-release, its notes, and the two files a
/// release carries (the zip and SHA256SUMS.txt). A release without both can't be installed and is never offered.
/// </summary>
public sealed record ReleaseInfo(
    string Tag,
    SemVer Version,
    bool PreRelease,
    DateTime? PublishedUtc,
    string Notes,
    string? ZipName,
    string? ZipUrl,
    long ZipSize,
    string? SumsUrl)
{
    /// <summary>The name of the release's zip for Windows, as Tools/build_release.ps1 writes it.</summary>
    public const string ZipPrefix = "TheArchipelagoAtlas-";
    public const string ZipSuffix = "-win-x64.zip";
    public const string SumsName = "SHA256SUMS.txt";

    public bool Installable => ZipUrl != null && SumsUrl != null && ZipName != null;

    /// <summary>
    /// The releases in GitHub's answer (the releases API, newest first), drafts left out, those whose tag isn't a version
    /// left out. Anything in the answer is outside text: read, never trusted beyond its shape.
    /// </summary>
    public static IReadOnlyList<ReleaseInfo> Parse(JToken? json)
    {
        var releases = new List<ReleaseInfo>();
        if (json is not JArray array) return releases;
        foreach (var item in array.OfType<JObject>())
        {
            if (item.Value<bool?>("draft") == true) continue;
            string tag = item.Value<string>("tag_name") ?? "";
            if (!SemVer.TryParse(tag, out var version)) continue;
            string? zipName = null, zipUrl = null, sumsUrl = null;
            long zipSize = 0;
            foreach (var asset in (item["assets"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string name = asset.Value<string>("name") ?? "";
                string? url = asset.Value<string>("browser_download_url");
                if (url == null || !IsDownloadable(url)) continue;
                if (name.StartsWith(ZipPrefix, StringComparison.Ordinal) && name.EndsWith(ZipSuffix, StringComparison.Ordinal) && name.Length < 120)
                {
                    zipName = name;
                    zipUrl = url;
                    zipSize = asset.Value<long?>("size") ?? 0;
                }
                else if (name == SumsName)
                {
                    sumsUrl = url;
                }
            }
            DateTime? published = DateTime.TryParse(item.Value<string>("published_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) ? when : null;
            string notes = item.Value<string>("body") ?? "";
            if (notes.Length > 20000) notes = notes.Substring(0, 20000) + "…";
            releases.Add(new ReleaseInfo(tag, version, item.Value<bool?>("prerelease") == true, published, notes, zipName, zipUrl, zipSize, sumsUrl));
        }
        return releases;
    }

    /// <summary>An https address, or http on this PC (the fake site tests use): what the polite client downloads from.</summary>
    private static bool IsDownloadable(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host == "localhost")));

    /// <summary>
    /// The newest installable release of a channel ("stable": releases only; "beta": pre-releases too), or null when there
    /// is none.
    /// </summary>
    public static ReleaseInfo? Newest(IEnumerable<ReleaseInfo> releases, string channel)
    {
        bool stableOnly = !string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase);
        ReleaseInfo? best = null;
        foreach (var release in releases)
        {
            if (!release.Installable || (stableOnly && release.PreRelease)) continue;
            if (best == null || release.Version > best.Version) best = release;
        }
        return best;
    }

    /// <summary>The channel a setting means: "stable" or "beta", or for "auto" the channel the running version belongs to.</summary>
    public static string ChannelFor(string? setting, SemVer current) =>
        string.Equals(setting, "beta", StringComparison.OrdinalIgnoreCase) ? "beta"
        : string.Equals(setting, "stable", StringComparison.OrdinalIgnoreCase) ? "stable"
        : current.IsPreRelease ? "beta" : "stable";
}
