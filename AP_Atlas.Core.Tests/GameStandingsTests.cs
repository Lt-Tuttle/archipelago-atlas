using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.Tests;

/// <summary>Where a game stands on its page's card, from its part of an add, and the release its Update offers.</summary>
public sealed class GameStandingsTests
{
    private static IndexVersionFacts V(string version) => new(version, $"https://github.com/someone/game/releases/download/v{version}/game.apworld", "aa" + version, 100);

    private static IndexPackFacts P(string repo) => new(repo, "v1", "pack.zip", "bb" + repo, 1000, 95, []);

    private static GameStanding Standing(AddGameFacts facts, bool engineReady = true)
    {
        var plan = AddGamesPlan.Make([facts], new AddContext { EngineReady = engineReady, WorldsZipBytes = 1000 });
        return GameStandings.Of(plan, plan.Games[0]);
    }

    [Fact]
    public void Without_the_engine_the_engine_comes_first() =>
        Assert.Equal(GameStanding.EngineFirst, Standing(new AddGameFacts { Game = "Official", Official = true }, engineReady: false));

    [Fact]
    public void A_game_with_something_to_add_is_one_to_add()
    {
        Assert.Equal(GameStanding.Add, Standing(new AddGameFacts { Game = "A", Versions = [V("1.0.0")] }));
        Assert.Equal(GameStanding.Add, Standing(new AddGameFacts { Game = "B", LogicInstalled = true, Packs = [P("x/pack")] }));
        Assert.Equal(GameStanding.Add, Standing(new AddGameFacts { Game = "C", Official = true, WorldsToUnpack = [new WorldFolderFacts("c", 500, false)], PackInstalled = true }));
    }

    [Fact]
    public void Logic_and_a_pack_is_ready_and_logic_with_no_pack_known_is_ready_without_a_map()
    {
        Assert.Equal(GameStanding.Ready, Standing(new AddGameFacts { Game = "A", LogicInstalled = true, PackInstalled = true }));
        Assert.Equal(GameStanding.ReadyNoMap, Standing(new AddGameFacts { Game = "B", LogicInstalled = true }));
    }

    [Fact]
    public void A_game_whose_logic_comes_from_nowhere_known_is_unknown() =>
        Assert.Equal(GameStanding.Unknown, Standing(new AddGameFacts { Game = "Mystery" }));

    private static ApworldChoice C(string version, bool installed = false, bool pre = false, string? url = "https://github.com/a/b/releases/download/x/b.apworld", string source = "github.com/a/b") =>
        new(version, source, url, "s" + version, null, installed, false, null, pre);

    [Fact]
    public void Update_offers_the_newest_full_release_newer_than_the_installed_one() =>
        Assert.Equal("1.10.0", GameStandings.Update([C("2.0.0-beta", pre: true), C("1.10.0"), C("1.9.0"), C("1.2.0", installed: true), C("1.0.0")], _ => false)?.Version);

    [Fact]
    public void Update_passes_by_a_label_that_looks_like_a_pre_release_and_a_release_ruled_out() =>
        Assert.Equal("2.0.0", GameStandings.Update([C("3.0.0"), C("2.1.0-rc1"), C("2.0.0"), C("1.0.0", installed: true)], c => c.Version == "3.0.0")?.Version);

    [Fact]
    public void Update_comes_only_from_the_installed_release_s_own_project()
    {
        Assert.Equal("1.1.0", GameStandings.Update([C("9.9.9", source: "github.com/fork/b"), C("1.1.0"), C("1.0.0", installed: true)], _ => false)?.Version);
        Assert.Null(GameStandings.Update([C("9.9.9", source: "github.com/fork/b"), C("1.0.0", installed: true)], _ => false));
    }

    [Fact]
    public void No_update_without_a_listed_installed_release_a_newer_one_or_a_download()
    {
        Assert.Null(GameStandings.Update([C("2.0.0"), C("1.0.0")], _ => false));
        Assert.Null(GameStandings.Update([C("2.0.0", installed: true), C("1.0.0")], _ => false));
        Assert.Null(GameStandings.Update([C("2.0.0", url: null), C("1.0.0", installed: true)], _ => false));
    }
}
