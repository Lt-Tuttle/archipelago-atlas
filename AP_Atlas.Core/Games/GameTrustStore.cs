using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace AP_Atlas.Core.Games;

/// <summary>
/// What a slot's logic check found about a game (its world rebuilt with other seeds: the same, or logic unsure for some
/// checks), for one apworld: kept so the game's page and Add a slot can say it before the next slot connects.
/// </summary>
public sealed class TrustRecord
{
    public string Game { get; set; } = "";
    /// <summary>The apworld the check ran on: its SHA-256 (lower case), or "archipelago-&lt;version&gt;" for a world that ships with Archipelago.</summary>
    public string Apworld { get; set; } = "";
    /// <summary>The version as people name it (its release tag, else the world's own version), or null.</summary>
    public string? Version { get; set; }
    public bool Same { get; set; }
    public bool LogicDiffers { get; set; }
    public List<string> Examples { get; set; } = new();
    /// <summary>How the slot's world was rebuilt (the engine's yaml source): not_needed, slot_data, linked or players.</summary>
    public string? YamlSource { get; set; }
    /// <summary>When the check was made (wall clock: shown as a date).</summary>
    public DateTime Checked { get; set; }

    /// <summary>The slot's world was rebuilt from a player YAML (linked, or in the Players folder): the game needs one.</summary>
    [JsonIgnore]
    public bool UsedYaml => YamlSource is "linked" or "players";
}

/// <summary>
/// The kept trust records, one per game and apworld, in one file beside the game checks (<c>engine\game_trust.json</c>).
/// Nothing is written until a record is kept.
/// </summary>
public sealed class GameTrustStore
{
    public const string FileName = "game_trust.json";

    private readonly string _path;
    private readonly List<TrustRecord> _records;

    private GameTrustStore(string path, List<TrustRecord> records)
    {
        _path = path;
        _records = records;
    }

    public static string PathFor(string engineDir) => Path.Combine(engineDir, FileName);

    /// <summary>The key of a world that ships with Archipelago (it has no apworld file of its own).</summary>
    public static string ArchipelagoKey(string version) => ArchipelagoPrefix + version;

    /// <summary>Whether a key is a world's that ships with Archipelago (<see cref="ArchipelagoKey"/>).</summary>
    public static bool IsArchipelagoKey(string key) => key.StartsWith(ArchipelagoPrefix, StringComparison.OrdinalIgnoreCase);

    private const string ArchipelagoPrefix = "archipelago-";

    public static GameTrustStore Load(string path)
    {
        var records = SafeFile.ReadJson(path, () => new List<TrustRecord>()) ?? new List<TrustRecord>();
        records.RemoveAll(r => r == null || string.IsNullOrEmpty(r.Game) || string.IsNullOrEmpty(r.Apworld));
        foreach (var r in records) r.Examples ??= new List<string>();
        return new GameTrustStore(path, records);
    }

    public IReadOnlyList<TrustRecord> Records => _records;

    /// <summary>The game's record for one apworld, or null.</summary>
    public TrustRecord? Find(string game, string apworld) =>
        _records.FirstOrDefault(r => string.Equals(r.Game, game, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Apworld, apworld, StringComparison.OrdinalIgnoreCase));

    /// <summary>The game's newest record, whichever apworld it was for, or null.</summary>
    public TrustRecord? Newest(string game) =>
        _records.Where(r => string.Equals(r.Game, game, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Checked).FirstOrDefault();

    /// <summary>Keeps a record in place of the game's earlier one for the same apworld, and saves.</summary>
    public void Remove(string game)
    {
        if (_records.RemoveAll(r => string.Equals(r.Game, game, StringComparison.OrdinalIgnoreCase)) > 0) SafeFile.WriteJson(_path, _records);
    }

    /// <summary>Keeps a record in place of the game's earlier one for the same apworld, and saves.</summary>
    public void Put(TrustRecord record)
    {
        _records.RemoveAll(r => string.Equals(r.Game, record.Game, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Apworld, record.Apworld, StringComparison.OrdinalIgnoreCase));
        _records.Add(record);
        SafeFile.WriteJson(_path, _records);
    }
}
