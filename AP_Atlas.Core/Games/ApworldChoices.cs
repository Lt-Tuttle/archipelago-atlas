using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core.Games;

/// <summary>
/// One version of a game's apworld the user can pick for a slot: where it's published (or that it's the user's own
/// file), whether Atlas has it already, whether it's the one installed in the engine, and whether its data is known to
/// match the seed's.
/// </summary>
public sealed record ApworldChoice(string Version, string Source, string? Url, string? Sha256, string? LocalFile, bool Installed, bool MatchesSeed, string? Checksum,
    bool Prerelease = false, DateTime? Published = null)
{
    /// <summary>Atlas has the file (nothing to download).</summary>
    public bool Downloaded => LocalFile != null;
}

/// <summary>
/// A release Atlas knows of: its version label, the project (or "your file"), its download and its published hash; whether
/// the project marked it a pre-release, and when it was published (a time the site names, shown only).
/// </summary>
public sealed record PublishedApworld(string Version, string Source, string? Url, string? Sha256, bool Prerelease = false, DateTime? Published = null);

/// <summary>A file Atlas has already identified: its version, where it came from, the file and the data checksum it reported.</summary>
public sealed record IdentifiedApworld(string Version, string Source, string File, string Checksum);

/// <summary>Builds the list the version picker shows.</summary>
public static class ApworldChoices
{
    /// <summary>
    /// Every version the picker can offer, best first: those known to match the seed, then newest to oldest by version
    /// number (a pre-release after its release). A published version Atlas already identified carries its file and checksum;
    /// files Atlas has that no release lists (the user's own) are offered too.
    /// </summary>
    public static List<ApworldChoice> Compose(IEnumerable<PublishedApworld> published, IEnumerable<IdentifiedApworld> identified, IEnumerable<string> installedSha256, string? seedChecksum)
    {
        var installed = new HashSet<string>(installedSha256.Where(h => !string.IsNullOrEmpty(h)), StringComparer.OrdinalIgnoreCase);
        var known = identified.ToList();
        var choices = new List<ApworldChoice>();
        var used = new HashSet<IdentifiedApworld>();
        foreach (var p in published)
        {
            var have = known.FirstOrDefault(k => SameVersion(k.Version, p.Version) && SameSource(k.Source, p.Source, p.Url));
            if (have != null) used.Add(have);
            bool match = have != null && seedChecksum != null && string.Equals(have.Checksum, seedChecksum, StringComparison.OrdinalIgnoreCase);
            choices.Add(new ApworldChoice(p.Version, p.Source, p.Url, p.Sha256, have?.File, p.Sha256 != null && installed.Contains(p.Sha256), match, have?.Checksum, p.Prerelease, p.Published));
        }
        foreach (var k in known.Where(k => !used.Contains(k)))
        {
            bool match = seedChecksum != null && string.Equals(k.Checksum, seedChecksum, StringComparison.OrdinalIgnoreCase);
            choices.Add(new ApworldChoice(k.Version, k.Source, null, null, k.File, false, match, k.Checksum));
        }
        return choices
            .GroupBy(c => (Key(c.Version), c.Source.ToLowerInvariant()))
            .Select(g => g.OrderByDescending(c => c.Downloaded).First())
            .OrderByDescending(c => c.MatchesSeed)
            .ThenByDescending(c => c.Version, VersionComparer.Instance)
            .ThenBy(c => c.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// One label for one release: "v0.2.5" is "0.2.5", and a build number the community index writes as "0.0.22-0" is
    /// GitHub's tag "0.0.22.0" (a suffix that isn't a number, "1.0.0-beta", is a pre-release and stays).
    /// </summary>
    public static string NormalizeLabel(string? version)
    {
        string v = (version ?? "").Trim();
        v = Regex.Replace(v, @"^[vV](?=\d)", "");
        return Regex.Replace(v, @"^(\d+(?:\.\d+)*)-(\d+)$", "$1.$2");
    }

    /// <summary>Whether two labels name the same release.</summary>
    public static bool SameLabel(string? a, string? b) => Key(a) == Key(b);

    private static string Key(string? version) => NormalizeLabel(version).ToLowerInvariant();

    private static bool SameVersion(string? a, string? b) => Key(a) == Key(b);

    private static bool SameSource(string identifiedSource, string publishedSource, string? url) =>
        identifiedSource.Contains(publishedSource, StringComparison.OrdinalIgnoreCase)
        || (url != null && identifiedSource.Contains(new Uri(url).Host + new Uri(url).AbsolutePath.Split("/releases")[0], StringComparison.OrdinalIgnoreCase));

    /// <summary>Orders version labels by their numbers ("v1.10.0" after "1.9.2"); a pre-release ("1.0.0-beta") before its release; unnumbered labels by text.</summary>
    public sealed class VersionComparer : IComparer<string?>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var (nx, px) = Parse(x);
            var (ny, py) = Parse(y);
            for (int i = 0; i < Math.Max(nx.Count, ny.Count); i++)
            {
                long a = i < nx.Count ? nx[i] : 0, b = i < ny.Count ? ny[i] : 0;
                if (a != b) return a.CompareTo(b);
            }
            if (nx.Count == 0 && ny.Count == 0) return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            // The same numbers: a release beats its pre-release.
            if (px.Length == 0 && py.Length > 0) return 1;
            if (px.Length > 0 && py.Length == 0) return -1;
            return string.Compare(px, py, StringComparison.OrdinalIgnoreCase);
        }

        private static (List<long> Numbers, string PreRelease) Parse(string? version)
        {
            string v = Key(version);
            var m = Regex.Match(v, @"^(\d+(?:\.\d+)*)(.*)$");
            if (!m.Success) return (new List<long>(), v);
            var numbers = m.Groups[1].Value.Split('.').Select(s => long.TryParse(s, out long n) ? n : 0).ToList();
            return (numbers, m.Groups[2].Value.TrimStart('-', '+', '_', ' '));
        }
    }
}
