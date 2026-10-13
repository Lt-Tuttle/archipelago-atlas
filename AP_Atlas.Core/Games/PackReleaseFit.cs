using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.Games;

/// <summary>
/// How well one release of a pack fits the game's names, from the Pack Doctor's check: the share of its pin sections
/// that link to a location, and the ids its mapping script lists that the game doesn't have (an outdated or too-new
/// script). A project's newest release can be for a newer apworld than the engine's; this says so without asking.
/// </summary>
public sealed record PackReleaseFit(string Tag, int SectionsTotal, int SectionsLinked, int MappedIds, int UnknownIds)
{
    /// <summary>The least share of linked sections a fitting release has.</summary>
    public const double LinkedEnough = 0.75;
    /// <summary>The most of its mapped ids a fitting release may list that the game doesn't have.</summary>
    public const double UnknownAtMost = 0.02;

    public double LinkedShare => SectionsTotal <= 0 ? 0 : (double)SectionsLinked / SectionsTotal;
    public double UnknownShare => MappedIds <= 0 ? 0 : (double)UnknownIds / MappedIds;
    /// <summary>Good enough to stop looking: most sections link, and the script names the game's own ids.</summary>
    public bool Fits => LinkedShare >= LinkedEnough && UnknownShare <= UnknownAtMost;

    /// <summary>"v1.0.6: 97% of its pins link, 0 stale ids".</summary>
    public string Describe() => $"{Tag}: {Math.Round(LinkedShare * 100)}% of its pins link, {UnknownIds} stale id{(UnknownIds == 1 ? "" : "s")}";
}

public static class PackReleasePick
{
    /// <summary>At most this many of a project's releases are tried, newest first.</summary>
    public const int MostTried = 3;

    /// <summary>
    /// Of the releases tried (newest first), the first that fits; else the best: the most linked sections, then the
    /// fewest stale ids, then the newer. Null for none tried.
    /// </summary>
    public static PackReleaseFit? Best(IReadOnlyList<PackReleaseFit> tried)
    {
        if (tried.Count == 0) return null;
        return tried.FirstOrDefault(t => t.Fits)
            ?? tried.Select((t, i) => (t, i)).OrderByDescending(x => x.t.LinkedShare).ThenBy(x => x.t.UnknownShare).ThenBy(x => x.i).First().t;
    }

    /// <summary>What was tried and taken, for a report line: "took v1.0.6 (97% of its pins link, 0 stale ids); v2.0.1: 88%…, 310 stale ids".</summary>
    public static string Describe(IReadOnlyList<PackReleaseFit> tried, PackReleaseFit chosen)
    {
        var others = tried.Where(t => !ReferenceEquals(t, chosen)).Select(t => t.Describe()).ToList();
        return "took " + chosen.Describe() + (others.Count > 0 ? "; also tried " + string.Join("; ", others) : "");
    }
}

/// <summary>
/// Which project's pack Atlas takes for a game by itself, when the game's guide names none (or its project has no pack
/// that fits): the projects GitHub finds are tried in turn (the guide's first, then by stars), each one's releases
/// measured against the game's names as <see cref="PackReleasePick"/> does, and the first project whose best release fits
/// is taken. A project kept up with the game's newest apworld (often a fork) fits where the original doesn't.
/// </summary>
public static class PackProjectPick
{
    /// <summary>At most this many projects are tried.</summary>
    public const int MostProjects = 4;
    /// <summary>At most this many files are downloaded across them.</summary>
    public const int MostDownloads = 8;
    /// <summary>With none fitting, the closest is taken only when at least this share of its pins link.</summary>
    public const double CloseEnough = 0.5;

    /// <summary>
    /// The projects' best releases in the order they were tried (null: nothing of it could be read): the first that fits;
    /// else the one whose pins link the most (then the fewest stale ids, then the earlier), if it links at least
    /// <see cref="CloseEnough"/>; else none (-1).
    /// </summary>
    public static int Choose(IReadOnlyList<PackReleaseFit?> bests)
    {
        for (int i = 0; i < bests.Count; i++)
            if (bests[i]?.Fits == true) return i;
        int chosen = -1;
        for (int i = 0; i < bests.Count; i++)
        {
            if (bests[i] is not { } fit || fit.LinkedShare < CloseEnough) continue;
            if (chosen < 0 || fit.LinkedShare > bests[chosen]!.LinkedShare
                || (fit.LinkedShare == bests[chosen]!.LinkedShare && fit.UnknownShare < bests[chosen]!.UnknownShare)) chosen = i;
        }
        return chosen;
    }
}
