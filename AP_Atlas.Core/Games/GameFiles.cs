using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AP_Atlas.Core.Games;

/// <summary>
/// Where Atlas keeps a game's own files, inside its data folder: <c>games\&lt;Game&gt;\apworlds</c> (every release of the
/// game's apworld Atlas downloaded, by project and version), <c>games\&lt;Game&gt;\tools</c> (the loose files a game's
/// setup needs, such as a client or a .bat, downloaded from a trusted release; Atlas never runs them) and
/// <c>yamls</c> (player YAMLs the user added, indexed by the games they name; one file can name several).
/// </summary>
public static class GameFiles
{
    /// <summary>A game's name made safe as a folder name: characters Windows refuses become "_", no trailing dots or spaces, at most 80 characters.</summary>
    public static string SafeName(string? game)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var name = new StringBuilder();
        foreach (char c in (game ?? "").Trim())
            name.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        string safe = name.ToString().TrimEnd('.', ' ');
        if (safe.Length > 80) safe = safe.Substring(0, 80).TrimEnd('.', ' ');
        // Windows keeps a few names for devices.
        string stem = safe.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3])))
            safe = "_" + safe;
        return safe.Length == 0 ? "_" : safe;
    }

    public static string GamesFolder(string dataDir) => Path.Combine(dataDir, "games");

    public static string GameFolder(string dataDir, string game) => Path.Combine(GamesFolder(dataDir), SafeName(game));

    public static string ApworldsFolder(string dataDir, string game) => Path.Combine(GameFolder(dataDir, game), "apworlds");

    public static string ToolsFolder(string dataDir, string game) => Path.Combine(GameFolder(dataDir, game), "tools");

    public static string YamlsFolder(string dataDir) => Path.Combine(dataDir, "yamls");

    /// <summary>
    /// The games a player YAML names, in its documents' order: each document's <c>game</c>, or every game a weighted
    /// <c>game</c> map gives a weight above 0.
    /// </summary>
    public static List<string> GamesOf(string yamlText) =>
        YamlExclusions.Players(yamlText).SelectMany(p => p.Games).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>A player YAML in Atlas's YAML folder: its file name there, the games and players it names, when it was added.</summary>
public sealed class YamlEntry
{
    public string File { get; set; } = "";
    public List<string> Games { get; set; } = new();
    public List<string> Players { get; set; } = new();
    /// <summary>When it was added (wall clock: shown to the user).</summary>
    public DateTime Added { get; set; }
}

/// <summary>
/// The player YAMLs the user keeps in Atlas: copied into <see cref="GameFiles.YamlsFolder"/>, listed in its index.json by
/// the games each names, so a game's page lists every YAML that can play it (a multi-game YAML under each of its games).
/// </summary>
public sealed class YamlLibrary
{
    private readonly string _folder;
    private readonly List<YamlEntry> _entries;

    private YamlLibrary(string folder, List<YamlEntry> entries)
    {
        _folder = folder;
        _entries = entries;
    }

    private string IndexPath => Path.Combine(_folder, "index.json");

    /// <summary>The library in this data folder (an empty one when there's none yet; nothing is written until a YAML is added).</summary>
    public static YamlLibrary Load(string dataDir)
    {
        string folder = GameFiles.YamlsFolder(dataDir);
        var entries = SafeFile.ReadJson(Path.Combine(folder, "index.json"), () => new List<YamlEntry>()) ?? new List<YamlEntry>();
        // A YAML deleted by hand drops out of the list.
        entries.RemoveAll(e => string.IsNullOrEmpty(e.File) || !System.IO.File.Exists(Path.Combine(folder, e.File)));
        return new YamlLibrary(folder, entries);
    }

    public string Folder => _folder;

    public IReadOnlyList<YamlEntry> Entries => _entries;

    /// <summary>The full path of an entry's file.</summary>
    public string PathOf(YamlEntry entry) => Path.Combine(_folder, entry.File);

    /// <summary>Every YAML that names this game.</summary>
    public List<YamlEntry> For(string game) =>
        _entries.Where(e => e.Games.Contains(game, StringComparer.OrdinalIgnoreCase)).OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Copies a YAML into the library (a new name if one of that name is there with other content; the same file twice
    /// isn't copied again) and lists the games it names. Returns the entry, or why it can't be added.
    /// </summary>
    public (YamlEntry? Entry, string? Problem) Add(string source)
    {
        string text;
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists) return (null, "the file isn't there");
            if (info.Length > 1_000_000) return (null, "it's over 1 MB, more than a player YAML ever is");
            text = System.IO.File.ReadAllText(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, ex.Message); }
        var players = YamlExclusions.Players(text);
        var games = players.SelectMany(p => p.Games).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (games.Count == 0) return (null, "it doesn't name a game (no \"game:\" in it)");
        Directory.CreateDirectory(_folder);
        string name = Path.GetFileName(source);
        string target = Path.Combine(_folder, name);
        for (int n = 2; System.IO.File.Exists(target); n++)
        {
            if (System.IO.File.ReadAllText(target) == text)
            {
                var existing = _entries.FirstOrDefault(e => string.Equals(e.File, Path.GetFileName(target), StringComparison.OrdinalIgnoreCase));
                if (existing != null) return (existing, null);
                break;
            }
            target = Path.Combine(_folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
        }
        SafeFile.WriteAllText(target, text);
        string file = Path.GetFileName(target);
        _entries.RemoveAll(e => string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase));
        var entry = new YamlEntry { File = file, Games = games, Players = players.Select(p => p.Name).Where(p => p.Length > 0).Distinct().ToList(), Added = DateTime.Now }; // wall clock: shown as a date
        _entries.Add(entry);
        Save();
        return (entry, null);
    }

    /// <summary>Takes a YAML out of the library (its file is deleted from Atlas's folder).</summary>
    public void Remove(YamlEntry entry)
    {
        SafeFile.Delete(PathOf(entry));
        _entries.Remove(entry);
        Save();
    }

    private void Save() => SafeFile.WriteJson(IndexPath, _entries);
}
