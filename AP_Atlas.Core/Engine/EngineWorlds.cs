using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace AP_Atlas.Core.EngineSetup;

/// <summary>
/// The index of Archipelago's game worlds the Atlas Engine unpacks on demand (Scripts/Core/Engine/Data/
/// archipelago_worlds.json, made by Tools/make_worlds_index.py for the pinned Archipelago): which folder of Archipelago's
/// release zip holds each game, which other world folders it imports, whether it declares packages of its own, and the
/// folders the core keeps. The engine's core is the zip without the on-demand folders.
/// </summary>
public sealed class WorldsIndex
{
    [JsonProperty("archipelago")] public string Archipelago { get; set; } = "";
    [JsonProperty("zip_sha256")] public string ZipSha256 { get; set; } = "";
    /// <summary>The zip's one top folder, with its slash ("Archipelago-0.6.7/").</summary>
    [JsonProperty("zip_root")] public string ZipRoot { get; set; } = "";
    /// <summary>The release zip's size in bytes (what downloading it costs, for the add's total); 0 when the index doesn't say.</summary>
    [JsonProperty("zip_bytes")] public long ZipBytes { get; set; }
    /// <summary>World folders the core keeps (the generic world, the health check's game, the shared libraries).</summary>
    [JsonProperty("core_worlds")] public List<string> CoreWorlds { get; set; } = new();
    [JsonProperty("worlds")] public Dictionary<string, WorldEntry> Worlds { get; set; } = new(StringComparer.Ordinal);

    public static WorldsIndex Parse(string json)
    {
        var index = JsonConvert.DeserializeObject<WorldsIndex>(json) ?? throw new FormatException("The worlds index is empty.");
        if (string.IsNullOrEmpty(index.Archipelago) || !index.ZipRoot.EndsWith('/') || index.Worlds.Count == 0) throw new FormatException("The worlds index is incomplete.");
        foreach (var (folder, entry) in index.Worlds)
            foreach (string need in entry.Needs)
                if (!index.Worlds.ContainsKey(need)) throw new FormatException($"The worlds index says {folder} needs {need}, which it doesn't list.");
        return index;
    }

    /// <summary>Every game the index lists, by name.</summary>
    public IEnumerable<string> Games => Worlds.Values.SelectMany(w => w.Games).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The on-demand folder that holds a game's world, or null (not one of this Archipelago's games, or in the core).</summary>
    public string? FolderFor(string? game) =>
        game == null ? null : Worlds.FirstOrDefault(w => w.Value.Games.Contains(game, StringComparer.OrdinalIgnoreCase)).Key;

    /// <summary>A folder and every folder it imports, directly or not: what has to be unpacked for it to load. The folder first.</summary>
    public List<string> Closure(string folder)
    {
        var seen = new List<string>();
        var todo = new Stack<string>();
        todo.Push(folder);
        while (todo.Count > 0)
        {
            string next = todo.Pop();
            if (seen.Contains(next) || !Worlds.TryGetValue(next, out var entry)) continue;
            seen.Add(next);
            foreach (string need in entry.Needs.OrderByDescending(n => n, StringComparer.Ordinal)) todo.Push(need);
        }
        return seen;
    }

    /// <summary>The on-demand folder a zip entry belongs to ("Archipelago-0.6.7/worlds/alttp/Rom.py" → "alttp"), or null.</summary>
    public string? WorldOfEntry(string entryName)
    {
        string name = entryName.Replace('\\', '/');
        string prefix = ZipRoot + "worlds/";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;
        int slash = name.IndexOf('/', prefix.Length);
        if (slash < 0) return null;
        string folder = name.Substring(prefix.Length, slash - prefix.Length);
        return Worlds.ContainsKey(folder) ? folder : null;
    }

    /// <summary>Whether a zip entry is part of the engine's core: under the zip's root, and not in an on-demand world folder.</summary>
    public bool IsCoreEntry(string entryName) => entryName.Replace('\\', '/').StartsWith(ZipRoot, StringComparison.Ordinal) && WorldOfEntry(entryName) == null;

    /// <summary>A world folder's own path in the zip ("Archipelago-0.6.7/worlds/alttp/").</summary>
    public string EntryPrefix(string folder) => ZipRoot + "worlds/" + folder + "/";
}

/// <summary>One on-demand world folder: the games it registers, the folders it imports, whether it declares packages, its size unpacked.</summary>
public sealed class WorldEntry
{
    [JsonProperty("games")] public List<string> Games { get; set; } = new();
    [JsonProperty("needs")] public List<string> Needs { get; set; } = new();
    [JsonProperty("requirements")] public bool Requirements { get; set; }
    [JsonProperty("bytes")] public long Bytes { get; set; }
}

/// <summary>
/// The world folders Atlas unpacked into the engine's user folder, and from which Archipelago version
/// (<c>ap/worlds/.atlas_worlds.json</c>): after an update to another Archipelago, they're replaced.
/// </summary>
public sealed class UnpackedWorlds
{
    [JsonProperty("archipelago")] public string Archipelago { get; set; } = "";
    [JsonProperty("folders")] public List<string> Folders { get; set; } = new();
}
