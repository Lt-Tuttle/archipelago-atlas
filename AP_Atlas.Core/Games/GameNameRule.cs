using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AP_Atlas.Core.Games;

/// <summary>
/// When two spellings name one game (the owner's round 12: a pack whose manifest says "Pokémon FireRed and LeafGreen" is for
/// Atlas's "Pokemon FireRed and LeafGreen"). One rule wherever Atlas compares game names across sources (a pack's manifest,
/// Atlas's game list, the engine's games, a slot's game): accents and case don't count, nor punctuation; "&amp;", "/", "+" and
/// the word "and" are left out; Roman numerals II to VIII are their numbers ("Dark Souls II" is "Dark Souls 2", never "Dark
/// Souls III"); words stay apart ("Archipela-Go!" isn't "Archipelago"). A pack may also name a game by the part after a colon
/// ("Zelda: Oracle of Seasons" ~ "Oracle of Seasons"), never a game's other name ("Manual: X" display names would be X).
/// Exact text wins, and a name two different games read as is no match.
/// </summary>
public static class GameNameRule
{
    private static readonly Dictionary<string, string> Roman = new(StringComparer.Ordinal)
    {
        ["ii"] = "2",
        ["iii"] = "3",
        ["iv"] = "4",
        ["v"] = "5",
        ["vi"] = "6",
        ["vii"] = "7",
        ["viii"] = "8"
    };

    // Names are few (the list's, the packs', the engine's): each is worked out once. Cleared past this many (a bound, not a policy).
    private const int MostRemembered = 8192;
    private static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.Ordinal);

    /// <summary>The name as compared ("pokemon firered leafgreen"); "" when there's nothing to compare.</summary>
    public static string Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        if (Known.TryGetValue(name, out var key)) return key;
        key = Compute(name);
        if (Known.Count >= MostRemembered) Known.Clear();
        Known[name] = key;
        return key;
    }

    private static string Compute(string name)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        void EndWord()
        {
            if (word.Length == 0) return;
            string w = word.ToString();
            word.Clear();
            if (w != "and") words.Add(Roman.TryGetValue(w, out var digit) ? digit : w);
        }
        foreach (char c in name.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue; // an accent, split off its letter
            if (char.IsLetterOrDigit(c)) word.Append(char.ToLowerInvariant(c));
            else EndWord();
        }
        EndWord();
        return string.Join(' ', words);
    }

    /// <summary>Two names of one game.</summary>
    public static bool Same(string? a, string? b)
    {
        string key = Key(a);
        return key.Length > 0 && key == Key(b);
    }

    /// <summary>A pack's name (its manifest's game or its title) names the game: the same name, or the part after one's colon is the other.</summary>
    public static bool PackIsFor(string? packName, string? game) =>
        Same(packName, game) || Same(AfterColon(packName), game) || Same(AfterColon(game), packName);

    private static string? AfterColon(string? name)
    {
        int colon = name?.IndexOf(':') ?? -1;
        return colon < 0 ? null : name![(colon + 1)..];
    }

    /// <summary>The candidate a name means: the same text (any case), else the one candidate that reads the same; null for none, or two.</summary>
    public static string? Resolve(string? name, IEnumerable<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var list = candidates.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        return list.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) ?? Unique(list.Where(c => Same(name, c)));
    }

    /// <summary>As <see cref="Resolve"/> for a pack's name: the same text, else one game that reads the same, else one the colon rule finds.</summary>
    public static string? ResolvePack(string? packName, IEnumerable<string> games)
    {
        if (string.IsNullOrWhiteSpace(packName)) return null;
        var list = games.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
        if (list.FirstOrDefault(g => string.Equals(g, packName, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
        var same = list.Where(g => Same(packName, g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (same.Count > 0) return same.Count == 1 ? same[0] : null;
        return Unique(list.Where(g => PackIsFor(packName, g)));
    }

    private static string? Unique(IEnumerable<string> matches)
    {
        var distinct = matches.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }
}
