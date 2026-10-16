using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.Tests;

/// <summary>What slots' logic checks found, kept per game and apworld beside the game checks.</summary>
public sealed class GameTrustStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static TrustRecord Record(string game, string apworld, bool same, int day, string? yaml = null) => new()
    {
        Game = game,
        Apworld = apworld,
        Version = "1.0",
        Same = same,
        LogicDiffers = !same,
        Examples = same ? new List<string>() : new List<string> { "song_list" },
        YamlSource = yaml,
        Checked = new DateTime(2026, 10, day)
    };

    [Fact]
    public void A_record_is_kept_per_game_and_apworld_and_the_newest_is_found()
    {
        string path = GameTrustStore.PathFor(_temp.Path);
        var store = GameTrustStore.Load(path);
        Assert.Empty(store.Records);
        Assert.False(File.Exists(path));
        store.Put(Record("Muse Dash", "aa", same: true, day: 1));
        store.Put(Record("Muse Dash", GameTrustStore.ArchipelagoKey("0.6.7"), same: false, day: 2));
        store.Put(Record("muse dash", "AA", same: false, day: 3)); // the same apworld again: it replaces the first
        var reloaded = GameTrustStore.Load(path);
        Assert.Equal(2, reloaded.Records.Count);
        Assert.False(reloaded.Find("Muse Dash", "aa")!.Same);
        Assert.Equal("AA", reloaded.Newest("MUSE DASH")!.Apworld);
        Assert.Contains(reloaded.Records, r => r.Apworld == "archipelago-0.6.7");
        Assert.Null(reloaded.Newest("Another Game"));
        Assert.True(GameTrustStore.IsArchipelagoKey(GameTrustStore.ArchipelagoKey("0.6.7")) && !GameTrustStore.IsArchipelagoKey("aa"));
        reloaded.Remove("MUSE DASH");
        Assert.Empty(GameTrustStore.Load(path).Records);
    }

    [Fact]
    public void A_record_says_whether_the_world_was_rebuilt_from_a_YAML_and_bad_rows_are_dropped()
    {
        Assert.True(Record("A", "x", true, 1, "linked").UsedYaml);
        Assert.True(Record("A", "x", true, 1, "players").UsedYaml);
        Assert.False(Record("A", "x", true, 1, "slot_data").UsedYaml);
        Assert.False(Record("A", "x", true, 1).UsedYaml);
        string path = GameTrustStore.PathFor(_temp.Path);
        File.WriteAllText(path, """[{"Game":"A","Apworld":"x","Examples":null},{"Game":"","Apworld":"y"},{"Game":"B"}]""");
        var store = GameTrustStore.Load(path);
        Assert.Equal("A", Assert.Single(store.Records).Game);
        Assert.Empty(store.Records[0].Examples);
    }
}
