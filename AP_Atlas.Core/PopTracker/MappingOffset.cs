using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.PopTracker;

/// <summary>
/// Some packs key their mapping scripts by offsets from the game's ids rather than by the ids: their archipelago.lua
/// subtracts a base before the lookup (Streets of Rage's <c>LOCATION_MAPPING[id - (0xDEAFF00D + 50)]</c>; Tevi, CrossCode,
/// Psychonauts and A Hat in Time do the same). Read as ids, every key is "an id the game doesn't have". This finds the base
/// that puts the keys on the game's ids, when one does.
/// </summary>
public static class MappingOffset
{
    /// <summary>Fewer keys than this say nothing about a base.</summary>
    public const int MinKeys = 4;

    /// <summary>The share of the keys the base has to put on the game's ids.</summary>
    public const double MinShare = 0.9;

    /// <summary>
    /// The base to add to the mapping's keys so they are the game's ids, or null when the keys are the ids already (half or
    /// more are known), too few, or no base puts nine in ten of them on the game's ids. <paramref name="hits"/> says how many it does.
    /// </summary>
    public static long? Infer(IReadOnlyCollection<long> keys, IReadOnlyCollection<long> gameIds, out int hits)
    {
        hits = 0;
        if (keys == null || gameIds == null || keys.Count < MinKeys || gameIds.Count == 0) return null;
        var ids = gameIds as HashSet<long> ?? new HashSet<long>(gameIds);
        int known = keys.Count(ids.Contains);
        if (known * 2 >= keys.Count) return null;
        long first = keys.Min();
        long bestBase = 0;
        int best = 0;
        // The lowest key lands on some id of the game: try each as its target.
        foreach (long target in ids)
        {
            long by = unchecked(target - first);
            if (by == 0) continue;
            int n = 0;
            foreach (long key in keys)
                if (ids.Contains(unchecked(key + by))) n++;
            if (n > best) { best = n; bestBase = by; }
        }
        if (best < keys.Count * MinShare) return null;
        hits = best;
        return bestBase;
    }
}
