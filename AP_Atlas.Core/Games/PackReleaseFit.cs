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
    /// <summary>A pack is taken only when at least this share of its pins link (or it fits).</summary>
    public const double CloseEnough = 0.5;
    /// <summary>
    /// What a fork, and a project's current files (it has no release), give up against the others: forks are often copies
    /// of an older version left as they were, and current files are what's being worked on. A fork that fits clearly
    /// better (Dark Souls III's, kept up with the 3.0 apworld) still wins; one that fits as well as the original doesn't.
    /// </summary>
    public const double ForkWeight = 0.05, CurrentFilesWeight = 0.05;

    /// <summary>One project tried: its best release (null: nothing of it could be read), and what it is.</summary>
    public sealed record Tried(PackReleaseFit? Best, bool Fork, bool CurrentFiles)
    {
        /// <summary>How well it serves: the share of its pins that link, less the share of its stale ids and the weights above.</summary>
        public double Score => Best == null ? double.MinValue
            : Best.LinkedShare - Best.UnknownShare - (Fork ? ForkWeight : 0) - (CurrentFiles ? CurrentFilesWeight : 0);

        /// <summary>Whether it may be taken at all: it fits, or links <see cref="CloseEnough"/>.</summary>
        public bool Usable => Best != null && (Best.Fits || Best.LinkedShare >= CloseEnough);

        /// <summary>Whether no other project needs trying: an original's release that fits.</summary>
        public bool Settles => Best?.Fits == true && !Fork && !CurrentFiles;
    }

    /// <summary>
    /// The projects in the order they were tried: the usable one that serves best (<see cref="Tried.Score"/>; the earlier
    /// on a tie); none (-1) when none is usable.
    /// </summary>
    public static int Choose(IReadOnlyList<Tried> tried)
    {
        int chosen = -1;
        for (int i = 0; i < tried.Count; i++)
            if (tried[i].Usable && (chosen < 0 || tried[i].Score > tried[chosen].Score)) chosen = i;
        return chosen;
    }
}
