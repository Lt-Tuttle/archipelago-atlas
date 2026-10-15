using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AP_Atlas.Core.Reports;

/// <summary>
/// The players' names and the servers Atlas has seen in the rooms it connected to, kept in the reports folder
/// (<see cref="FileName"/>) so a crash or problem report scrubs them too: a log line can name another slot's player, or a
/// multiworld since deleted. Short or common words (<see cref="StopWords"/>) are left out, since scrubbing them would mangle
/// ordinary text. Capped at <see cref="MaxEach"/> of each, the oldest going first; written only when something new arrives.
/// Room passwords are never kept here (the scrubber takes them from the multiworlds in memory).
/// </summary>
public sealed class SeenNames
{
    public const string FileName = "seen_names.json";
    public const int MaxEach = 1000;
    public const int MinLength = 3;

    /// <summary>Words that are names in some room and ordinary words in every log: never scrubbed as names.</summary>
    public static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "server", "archipelago", "player", "players", "slot", "slots", "team", "admin", "host", "item", "items", "location",
        "locations", "game", "games", "test", "tracker", "client", "atlas", "none", "null", "unknown", "the", "and", "you"
    };

    public List<string> Names { get; set; } = new();
    public List<string> Servers { get; set; } = new();

    /// <summary>What was seen so far (nothing when the file is missing or unreadable).</summary>
    public static SeenNames Read(string reportsDir)
    {
        var seen = SafeFile.ReadJson(Path.Combine(reportsDir, FileName), () => new SeenNames()) ?? new SeenNames();
        seen.Names ??= new List<string>();
        seen.Servers ??= new List<string>();
        return seen;
    }

    /// <summary>A name worth scrubbing: long enough, and not an ordinary word.</summary>
    public static bool Usable(string? name)
    {
        string trimmed = name?.Trim() ?? "";
        return trimmed.Length >= MinLength && !StopWords.Contains(trimmed);
    }

    /// <summary>Adds the names and servers not seen yet; true when the file was written (nothing new: nothing written).</summary>
    public static bool Remember(string reportsDir, IEnumerable<string?> names, IEnumerable<string?> servers)
    {
        var seen = Read(reportsDir);
        bool changed = Add(seen.Names, names.Where(Usable).Select(n => n!.Trim()));
        changed |= Add(seen.Servers, servers.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        if (!changed) return false;
        Directory.CreateDirectory(reportsDir);
        SafeFile.WriteJson(Path.Combine(reportsDir, FileName), seen);
        return true;
    }

    private static bool Add(List<string> list, IEnumerable<string> items)
    {
        var known = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (string item in items)
        {
            if (!known.Add(item)) continue;
            list.Add(item);
            changed = true;
        }
        if (list.Count > MaxEach) list.RemoveRange(0, list.Count - MaxEach);
        return changed;
    }
}
