using AP_Atlas.Core.EngineSetup;
using AP_Atlas.Core.Games;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>What adding games installs, worked out offline: per game its logic and one map pack, the sizes, the unknowns, the projects to trust, GitHub's requests.</summary>
public sealed class AddGamesPlanTests
{
    private const string Sha = "aa";

    private static IndexVersionFacts V(string version, long size, string repo = "someone/game-apworld") =>
        new(version, $"https://github.com/{repo}/releases/download/v{version}/game.apworld", Sha + version, size);

    private static IndexPackFacts P(string repo, int? fit, long size = 1000, string asset = "pack.zip", params string[] notes) =>
        new(repo, asset == AddGamesPlan.Zipball ? "current files" : "v1", asset, "bb" + repo, size, fit, notes);

    private static AddGameFacts Community(string game, params IndexVersionFacts[] versions) => new() { Game = game, Versions = versions };

    private static AddPlan Plan(params AddGameFacts[] games) => AddGamesPlan.Make(games, new AddContext { WorldsZipBytes = 22_424_521 });

    [Fact]
    public void A_community_game_gets_its_newest_release_and_its_best_fitting_pack()
    {
        var plan = Plan(Community("Game A", V("1.2.0", 300), V("1.10.0", 500), V("1.9.9", 400)) with { Packs = [P("x/low", 60), P("y/high", 95, size: 2000)] });
        var game = Assert.Single(plan.Games);
        Assert.Equal(AddRow.LogicAndMap, game.Row);
        Assert.Equal("logic and map", AddGamesPlan.Words(game));
        var apworld = Assert.Single(game.Items, i => i.Kind == AddItemKind.Apworld);
        Assert.Equal("1.10.0", apworld.Version);
        Assert.Equal(Sha + "1.10.0", apworld.Sha256);
        Assert.Equal("github.com/someone/game-apworld", apworld.Project);
        var pack = Assert.Single(game.Items, i => i.Kind == AddItemKind.Pack);
        Assert.Equal("y/high", pack.Repo);
        Assert.Equal(PackReason.BestFit, pack.Reason);
        Assert.Equal("https://github.com/y/high/releases/download/v1/pack.zip", pack.Url);
        Assert.Equal(0, pack.GitHubRequests);
        // Download: the apworld and the pack; disk: the apworld twice (the kept release and the engine's copy) and the pack.
        Assert.Equal(500 + 2000, plan.DownloadBytes);
        Assert.Equal(2 * 500 + 2000, plan.DiskBytes);
        Assert.Equal(0, plan.UnknownSizes);
        Assert.Equal(0, plan.GitHubRequests);
        Assert.False(plan.NeedsArchipelagoRelease);
        Assert.False(plan.NeedsGitHubLookups);
    }

    [Fact]
    public void A_version_label_is_said_as_the_Games_page_says_it_and_an_http_link_is_never_taken()
    {
        var plan = Plan(Community("Game A", V("v0.0.22-0", 10), new IndexVersionFacts("9.0.0", "http://example.com/game.apworld", null, 10)));
        var apworld = Assert.Single(plan.Items);
        Assert.Equal("0.0.22.0", apworld.Version);
    }

    [Fact]
    public void A_pack_that_waits_behind_is_taken_only_when_nothing_else_fits()
    {
        var waits = P("a/other-version", 98, notes: IndexPackNotes.OtherVersion);
        Assert.Equal("b/plain", AddGamesPlan.ChoosePack([waits, P("b/plain", 70)], [], 0)!.Repo);
        Assert.Equal("a/other-version", AddGamesPlan.ChoosePack([waits], [], 0)!.Repo);
        // A pack the lab measured under half linked isn't taken at all; one it couldn't measure is, after the measured ones.
        Assert.Null(AddGamesPlan.ChoosePack([P("c/poor", 49)], [], 0));
        Assert.Equal("d/unmeasured", AddGamesPlan.ChoosePack([P("d/unmeasured", null)], [], 0)!.Repo);
        Assert.Equal("e/measured", AddGamesPlan.ChoosePack([P("d/unmeasured", null), P("e/measured", 55)], [], 0)!.Repo);
    }

    [Fact]
    public void A_release_beats_current_files_that_fit_as_well_and_current_files_cost_a_request()
    {
        var current = P("a/current", 99, asset: AddGamesPlan.Zipball);
        var release = P("b/release", 96);
        Assert.Equal("b/release", AddGamesPlan.ChoosePack([current, release], [], 5)!.Repo);
        var pick = AddGamesPlan.ChoosePack([current, P("c/far", 80)], [], 5)!;
        Assert.Equal("a/current", pick.Repo);
        Assert.Equal(AddGamesPlan.CurrentFilesRequests, pick.GitHubRequests);
        // With no request left, current files can't be fetched: the release that fits is taken instead.
        Assert.Equal("c/far", AddGamesPlan.ChoosePack([current, P("c/far", 80)], [], 0)!.Repo);
    }

    [Fact]
    public void Current_files_are_not_checked_against_the_labs_hash_and_have_no_direct_link()
    {
        var plan = Plan(new AddGameFacts { Game = "Game A", LogicInstalled = true, Packs = [P("a/current", 99, size: 1234, asset: AddGamesPlan.Zipball)] });
        var pack = Assert.Single(plan.Items);
        Assert.True(pack.CurrentFiles);
        Assert.Null(pack.Url);
        Assert.Null(pack.Sha256);
        Assert.Equal(1234, pack.Size);
        Assert.Equal(1, plan.GitHubRequests);
        Assert.Equal(AddRow.Map, plan.Games[0].Row);
    }

    [Fact]
    public void A_pack_the_lab_took_from_elsewhere_is_never_picked()
    {
        Assert.Null(AddGamesPlan.ChoosePack([P("a/beta", 100, asset: "(from the beta's packs folder)")], [], 5));
        Assert.Null(AddGamesPlan.ChoosePack([P("not a repo", 100)], [], 5));
    }

    [Fact]
    public void The_guides_project_comes_first_when_the_list_has_its_pack()
    {
        var packs = new[] { P("best/fit", 99), P("Guide/Pack", 80) };
        var pick = AddGamesPlan.ChoosePack(packs, ["guide/pack"], 0)!;
        Assert.Equal("Guide/Pack", pick.Repo);
        Assert.Equal(PackReason.Guide, pick.Reason);
        // The list has the guide's project but its pack doesn't fit: it isn't tried from GitHub; the best fit is taken.
        Assert.Equal("best/fit", AddGamesPlan.ChoosePack([P("best/fit", 99), P("guide/pack", 20)], ["guide/pack"], 20)!.Repo);
    }

    [Fact]
    public void A_guide_project_the_list_does_not_have_is_read_from_GitHub_only_when_the_requests_allow()
    {
        var pick = AddGamesPlan.ChoosePack([P("best/fit", 99)], ["new/tracker"], AddGamesPlan.LivePackRequests)!;
        Assert.Equal("new/tracker", pick.Repo);
        Assert.Null(pick.Listed);
        Assert.Equal(PackReason.GuideProject, pick.Reason);
        Assert.Equal("best/fit", AddGamesPlan.ChoosePack([P("best/fit", 99)], ["new/tracker"], AddGamesPlan.LivePackRequests - 1)!.Repo);

        var plan = Plan(new AddGameFacts { Game = "Game A", LogicInstalled = true, GuideRead = true, GuideRepos = ["new/tracker"] });
        var pack = Assert.Single(plan.Items);
        Assert.True(pack.Live);
        Assert.True(pack.SizeUnknown);
        Assert.Equal(1, plan.UnknownSizes);
        Assert.Equal(AddGamesPlan.LivePackRequests, plan.GitHubRequests);
        Assert.Equal(new[] { "github.com/new/tracker" }, plan.PackProjects);
    }

    [Fact]
    public void An_official_game_unpacks_its_world_and_Archipelagos_release_is_downloaded_once()
    {
        var lib = new WorldFolderFacts("shared_lib", 5, false);
        var plan = Plan(
            new AddGameFacts { Game = "Official A", Official = true, WorldsToUnpack = [new("world_a", 10, false), lib], Packs = [P("a/pack", 90, size: 100)] },
            new AddGameFacts { Game = "Official B", Official = true, WorldsToUnpack = [new("world_b", 20, true), lib] });
        Assert.True(plan.NeedsArchipelagoRelease);
        Assert.Equal(22_424_521, plan.ArchipelagoReleaseBytes);
        Assert.Equal(AddRow.MapLogicShips, plan.Games[0].Row);
        Assert.Equal("map (its logic ships with Archipelago)", AddGamesPlan.Words(plan.Games[0]));
        Assert.Equal(AddRow.LogicNoMapKnown, plan.Games[1].Row);
        // Each folder is unpacked once, though both games need the shared one; the release stays kept on disk.
        Assert.Equal(22_424_521 + 100, plan.DownloadBytes);
        Assert.Equal(22_424_521 + 100 + 10 + 20 + 5, plan.DiskBytes);
        Assert.Equal(new[] { "world_b" }, plan.WorldsWithPackages);
        Assert.Empty(plan.ApworldProjects);
        Assert.Equal(15, plan.Games[0].DiskBytes - 100);
    }

    [Fact]
    public void A_kept_release_downloads_nothing_and_an_unknown_release_size_is_counted()
    {
        var game = new AddGameFacts { Game = "Official A", Official = true, WorldsToUnpack = [new("world_a", 10, false)] };
        var kept = AddGamesPlan.Make([game], new AddContext { WorldsZipPresent = true, WorldsZipBytes = 22_424_521 });
        Assert.False(kept.NeedsArchipelagoRelease);
        Assert.Equal(0, kept.DownloadBytes);
        Assert.Equal(10, kept.DiskBytes);
        var unknown = AddGamesPlan.Make([game], new AddContext());
        Assert.True(unknown.NeedsArchipelagoRelease);
        Assert.Equal(1, unknown.UnknownSizes);
    }

    [Theory]
    [InlineData(true, true, false, AddRow.Ready)]
    [InlineData(true, false, false, AddRow.NoMapKnown)]
    [InlineData(false, true, false, AddRow.Logic)]
    [InlineData(true, false, true, AddRow.Map)]
    public void A_games_row_says_what_adding_it_gets(bool logicInstalled, bool packInstalled, bool packKnown, AddRow row)
    {
        var plan = Plan(Community("Game A", V("1.0.0", 10)) with { LogicInstalled = logicInstalled, PackInstalled = packInstalled, Packs = packKnown ? [P("a/pack", 90)] : [] });
        Assert.Equal(row, plan.Games[0].Row);
        Assert.NotEqual("", AddGamesPlan.Words(plan.Games[0]));
        if (row == AddRow.Ready) Assert.Empty(plan.Items);
    }

    [Fact]
    public void A_game_whose_logic_nobody_knows_gets_its_map_or_nothing()
    {
        var plan = Plan(new AddGameFacts { Game = "Unknown A", Packs = [P("a/pack", 90)] }, new AddGameFacts { Game = "Unknown B" });
        Assert.Equal(AddRow.MapOnly, plan.Games[0].Row);
        Assert.Equal(AddRow.NothingKnown, plan.Games[1].Row);
        Assert.Empty(plan.Games[1].Items);
    }

    [Fact]
    public void A_project_the_user_added_is_read_from_GitHub_with_the_permission_asked_and_its_size_unknown()
    {
        var plan = Plan(new AddGameFacts { Game = "Mine", LiveProject = "me/my-apworld" });
        var apworld = Assert.Single(plan.Items);
        Assert.True(apworld.Live);
        Assert.Null(apworld.Version);
        Assert.True(plan.NeedsGitHubLookups);
        Assert.Equal(1, plan.UnknownSizes);
        Assert.Equal(new[] { "github.com/me/my-apworld" }, plan.ProjectsToTrust);
        Assert.Equal(AddGamesPlan.LiveListingRequests, plan.GitHubRequests);
    }

    [Fact]
    public void GitHubs_requests_never_go_over_the_budget_and_what_doesnt_fit_waits()
    {
        var games = Enumerable.Range(0, AddGamesPlan.GitHubBudget + 5)
            .Select(n => new AddGameFacts { Game = $"Game {n}", LogicInstalled = true, Packs = [P($"p/current{n}", 90, asset: AddGamesPlan.Zipball)] }).ToList();
        games.Add(new AddGameFacts { Game = "Mine", LiveProject = "me/my-apworld" });
        var plan = Plan(games.ToArray());
        Assert.Equal(AddGamesPlan.GitHubBudget, plan.GitHubRequests);
        Assert.Equal(AddGamesPlan.GitHubBudget, plan.Items.Count(i => i.CurrentFiles));
        Assert.Equal(5, plan.Games.Count(g => g.Row == AddRow.MapWaits));
        Assert.Equal(AddRow.Waits, plan.Games.Single(g => g.Game == "Mine").Row);
        Assert.Empty(plan.Games.Single(g => g.Game == "Mine").Items);
        Assert.False(plan.NeedsGitHubLookups);
    }

    [Fact]
    public void Only_sources_not_trusted_yet_are_trusted_by_the_confirm_and_every_one_is_named()
    {
        var plan = AddGamesPlan.Make(
            [Community("Game A", V("1.0.0", 10, "trusted/one")), Community("Game B", V("1.0.0", 10, "New/Two")), Community("Game C", V("2.0.0", 10, "new/two"))],
            new AddContext { TrustedProjects = ["github.com/Trusted/One"] });
        Assert.Equal(new[] { "github.com/trusted/one", "github.com/New/Two" }, plan.ApworldProjects);
        Assert.Equal(new[] { "github.com/New/Two" }, plan.ProjectsToTrust);
    }

    [Fact]
    public void A_game_ticked_twice_counts_once_and_a_pack_is_picked_again_after_the_logic_when_its_guide_couldnt_be_read()
    {
        var game = Community("Game A", V("1.0.0", 10)) with { Packs = [P("a/pack", 90)] };
        var plan = Plan(game, game with { Game = "game a" });
        Assert.Single(plan.Games);
        Assert.True(plan.Games[0].RepickPackAfterLogic);
        Assert.False(Plan(game with { GuideRead = true }).Games[0].RepickPackAfterLogic);
    }

    [Fact]
    public void The_free_space_floor_is_kept()
    {
        var plan = Plan(Community("Game A", V("1.0.0", 100)));
        Assert.True(plan.FitsIn(1200, 1000));
        Assert.False(plan.FitsIn(1199, 1000));
    }

    [Theory]
    [InlineData("https://github.com/Owner/Name/releases/download/v1/x.apworld", "github.com/Owner/Name")]
    [InlineData("https://github.com/Eijebong/Archipelago-index/raw/main/apworlds/x.apworld", "github.com/Eijebong/Archipelago-index")]
    [InlineData("https://example.com/x.apworld", "example.com")]
    [InlineData("not a link", "not a link")]
    [InlineData(null, "")]
    public void A_downloads_source_is_its_GitHub_project_or_its_host(string? url, string key) => Assert.Equal(key, AddGamesPlan.SourceKey(url));

    [Fact]
    public void The_shipped_game_list_knows_the_size_of_every_pack_and_most_newest_releases()
    {
        var list = JObject.Parse(File.ReadAllText(TestEnvironment.RepoFile("AP_Atlas_Source", "Scripts", "Core", "Engine", "Data", "apworld_sources.json")));
        Assert.Equal("atlas-apworld-sources/1", (string?)list["format"]);
        var games = list["games"]!.Children<JObject>().ToList();
        var packs = games.SelectMany(g => g["packs"]?.Children<JObject>() ?? Enumerable.Empty<JObject>()).ToList();
        Assert.NotEmpty(packs);
        Assert.All(packs, p => Assert.True((long?)p["size"] > 0, $"{p["repo"]} {p["asset"]} has no size"));
        var newest = games.Where(g => g["official"]?.Value<bool>() != true)
            .Select(g => AddGamesPlan.Newest((g["versions"] as JArray ?? new JArray()).Children<JObject>()
                .Select(v => new IndexVersionFacts((string)v["version"]!, (string)v["url"]!, (string?)v["sha256"], (long?)v["size"] ?? 0))))
            .Where(v => v != null).ToList();
        // The lab fetched most games' newest release; the rest come from the community index's own list, which has no size.
        Assert.True(newest.Count(v => v!.Size > 0) * 4 > newest.Count * 3, $"{newest.Count(v => v!.Size > 0)} of {newest.Count} newest releases have a size");
    }

    [Fact]
    public void The_shipped_worlds_index_knows_the_release_zips_size()
    {
        var index = WorldsIndex.Parse(File.ReadAllText(TestEnvironment.RepoFile("AP_Atlas_Source", "Scripts", "Core", "Engine", "Data", "archipelago_worlds.json")));
        Assert.Equal(22_424_521, index.ZipBytes);
        Assert.All(index.Worlds, w => Assert.True(w.Value.Bytes > 0, w.Key + " has no size"));
    }

    [Fact]
    public void A_link_on_this_PC_is_taken_and_any_other_plain_http_link_is_not()
    {
        Assert.True(AddGamesPlan.IsDownloadable("http://127.0.0.1:5000/dl/x.apworld"));
        Assert.False(AddGamesPlan.IsDownloadable("http://example.com/x.apworld"));
        Assert.False(AddGamesPlan.IsDownloadable("not a link"));
    }

    [Fact]
    public void A_guide_project_is_never_read_from_GitHub_without_the_permission_and_the_reason_is_kept()
    {
        var packs = new[] { P("best/fit", 99) };
        var pick = AddGamesPlan.ChoosePack(packs, ["new/tracker"], 20, mayReadProjects: false)!;
        Assert.Equal(("best/fit", PackReason.BestFit), (pick.Repo, pick.Reason));
        Assert.Equal(AddPackNote.GuideNotListedNoLookup, AddGamesPlan.GuideNote(packs, ["new/tracker"], pick, false));
        Assert.Equal(AddPackNote.GuideNotListedBudgetSpent, AddGamesPlan.GuideNote(packs, ["new/tracker"], AddGamesPlan.ChoosePack(packs, ["new/tracker"], 2)!, true));
        Assert.Equal(AddPackNote.None, AddGamesPlan.GuideNote(packs, ["best/fit"], AddGamesPlan.ChoosePack(packs, ["best/fit"], 0)!, true));
    }

    [Theory]
    [InlineData("1.6.0-beta", "1.5.0", "1.5.0")]
    [InlineData("0.3.0", "0.4.0-alpha+unreleased", "0.3.0")]
    [InlineData("20260822-1", "0.1.0+20251208-1", "20260822-1")]
    [InlineData("1.2.1-TEST", "0.6.0", "0.6.0")]
    [InlineData("1.6.0-beta", null, "1.6.0-beta")]
    public void The_newest_full_release_wins_over_a_newer_pre_release(string first, string? second, string expected)
    {
        var versions = new[] { first, second }.OfType<string>().Select(v => V(v, 1)).ToList();
        Assert.Equal(expected, AddGamesPlan.Newest(versions)!.Version);
    }

    [Fact]
    public void A_game_that_waits_for_GitHub_plans_no_pack_either()
    {
        var games = Enumerable.Range(0, AddGamesPlan.GitHubBudget)
            .Select(n => new AddGameFacts { Game = $"Game {n}", LogicInstalled = true, Packs = [P($"p/current{n}", 90, asset: AddGamesPlan.Zipball)] }).ToList();
        games.Add(new AddGameFacts { Game = "Mine", LiveProject = "me/my-apworld", Packs = [P("me/my-pack", 95)] });
        var mine = Plan(games.ToArray()).Games.Single(g => g.Game == "Mine");
        Assert.Equal(AddRow.Waits, mine.Row);
        Assert.Empty(mine.Items);
        Assert.False(mine.WantsPack);
    }

    [Fact]
    public void An_official_game_is_known_before_the_engine_is_set_up()
    {
        var game = new AddGameFacts { Game = "Official A", Official = true, Packs = [P("a/pack", 90)] };
        var notReady = AddGamesPlan.Make([game], new AddContext { EngineReady = false }).Games.Single();
        Assert.Equal(AddRow.MapLogicShips, notReady.Row);
        var noPack = AddGamesPlan.Make([game with { Packs = [] }], new AddContext { EngineReady = false }).Games.Single().Row;
        Assert.DoesNotContain(noPack, new[] { AddRow.NothingKnown, AddRow.MapOnly });
        // With the engine ready, an official game with nothing to unpack and no logic is a fact Atlas can't place.
        Assert.Equal(AddRow.MapOnly, AddGamesPlan.Make([game], new AddContext()).Games.Single().Row);
    }

    [Fact]
    public void Every_game_says_whether_it_wants_a_map_pack() =>
        Assert.Equal(new[] { true, false }, Plan(Community("A", V("1.0.0", 1)), Community("B", V("1.0.0", 1)) with { PackInstalled = true }).Games.Select(g => g.WantsPack));
}
