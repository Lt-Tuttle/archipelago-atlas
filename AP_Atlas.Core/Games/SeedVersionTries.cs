using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.Games;

/// <summary>
/// Which versions Atlas tries by itself when the connect-time rule fires (<see cref="SeedVersionRule"/>): versions it
/// already knows (the community index's, and release lists read earlier), only from projects the user trusts, newest
/// first, at most <see cref="Max"/>. A version Atlas has identified already isn't a try (its data is known, and isn't the
/// seed's, or it would have been used at once), nor is a copy installed in the engine (its data is what differs).
/// </summary>
public static class SeedVersionTries
{
    /// <summary>The most versions downloaded and loaded per seed and session.</summary>
    public const int Max = 3;

    public static List<ApworldChoice> Pick(IEnumerable<PublishedApworld> known, IEnumerable<IdentifiedApworld> identified, IEnumerable<string> installedSha256,
        IEnumerable<string> trustedSources, string? seedChecksum, int max = Max)
    {
        if (string.IsNullOrEmpty(seedChecksum) || max <= 0) return new List<ApworldChoice>();
        var trusted = new HashSet<string>(trustedSources.Where(s => !string.IsNullOrEmpty(s)), StringComparer.OrdinalIgnoreCase);
        return ApworldChoices.Compose(known, identified, installedSha256, seedChecksum)
            .Where(c => c.Url != null && !c.Downloaded && !c.Installed && !c.MatchesSeed && trusted.Contains(c.Source))
            .OrderByDescending(c => c.Version, ApworldChoices.VersionComparer.Instance)
            .Take(max)
            .ToList();
    }
}
