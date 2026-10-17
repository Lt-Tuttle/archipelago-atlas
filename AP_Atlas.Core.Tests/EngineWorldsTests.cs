using AP_Atlas.Core.EngineSetup;

namespace AP_Atlas.Core.Tests;

/// <summary>The index of Archipelago's game worlds the engine unpacks on demand.</summary>
public sealed class EngineWorldsTests
{
    private const string Small = """
        {
          "archipelago": "0.6.7",
          "zip_sha256": "aa",
          "zip_root": "Archipelago-0.6.7/",
          "core_worlds": ["_bizhawk", "checksfinder", "generic"],
          "worlds": {
            "alttp": {"games": ["A Link to the Past"], "needs": [], "requirements": false, "bytes": 10},
            "sm": {"games": ["Super Metroid"], "needs": [], "requirements": false, "bytes": 5},
            "smz3": {"games": ["SMZ3"], "needs": ["sm", "alttp"], "requirements": false, "bytes": 7},
            "soe": {"games": ["Secret of Evermore"], "needs": [], "requirements": true, "bytes": 3},
            "loop_a": {"games": ["A"], "needs": ["loop_b"], "requirements": false, "bytes": 1},
            "loop_b": {"games": ["B"], "needs": ["loop_a"], "requirements": false, "bytes": 1}
          }
        }
        """;

    [Fact]
    public void A_game_is_found_by_name_and_its_folder_comes_with_what_it_imports()
    {
        var index = WorldsIndex.Parse(Small);
        Assert.Equal("alttp", index.FolderFor("a link to the past"));
        Assert.Null(index.FolderFor("ChecksFinder"));
        Assert.Null(index.FolderFor(null));
        Assert.Equal(new[] { "smz3", "alttp", "sm" }, index.Closure("smz3"));
        Assert.Equal(new[] { "loop_a", "loop_b" }, index.Closure("loop_a"));
        Assert.Empty(index.Closure("nowhere"));
        Assert.Contains("Secret of Evermore", index.Games);
    }

    [Theory]
    [InlineData("Archipelago-0.6.7/worlds/alttp/Rom.py", "alttp", false)]
    [InlineData("Archipelago-0.6.7/worlds/alttp/", "alttp", false)]
    [InlineData("Archipelago-0.6.7/worlds/generic/__init__.py", null, true)]
    [InlineData("Archipelago-0.6.7/worlds/AutoWorld.py", null, true)]
    [InlineData("Archipelago-0.6.7/Utils.py", null, true)]
    [InlineData("Elsewhere/Utils.py", null, false)]
    public void The_core_is_the_zip_without_the_on_demand_world_folders(string entry, string? world, bool core)
    {
        var index = WorldsIndex.Parse(Small);
        Assert.Equal(world, index.WorldOfEntry(entry));
        Assert.Equal(core, index.IsCoreEntry(entry));
        Assert.Equal("Archipelago-0.6.7/worlds/sm/", index.EntryPrefix("sm"));
    }

    [Fact]
    public void An_index_that_names_a_folder_it_doesnt_list_is_refused()
    {
        Assert.Throws<FormatException>(() => WorldsIndex.Parse(Small.Replace("\"needs\": [\"sm\", \"alttp\"]", "\"needs\": [\"sm\", \"zelda\"]")));
        Assert.Throws<FormatException>(() => WorldsIndex.Parse("""{"archipelago": "0.6.7", "zip_root": "x", "worlds": {}}"""));
    }

    [Fact]
    public void The_shipped_index_lists_Archipelagos_games_and_keeps_the_health_checks_game_in_the_core()
    {
        var index = WorldsIndex.Parse(File.ReadAllText(TestEnvironment.RepoFile("AP_Atlas_Source", "Scripts", "Core", "Engine", "Data", "archipelago_worlds.json")));
        Assert.Equal($"Archipelago-{index.Archipelago}/", index.ZipRoot);
        Assert.Contains("checksfinder", index.CoreWorlds);
        Assert.Contains("generic", index.CoreWorlds);
        Assert.Equal("alttp", index.FolderFor("A Link to the Past"));
        Assert.Equal("dark_souls_3", index.FolderFor("Dark Souls III"));
        Assert.True(index.Worlds.Count > 70);
        Assert.All(index.Worlds, w => Assert.NotEmpty(w.Value.Games));
    }
}
