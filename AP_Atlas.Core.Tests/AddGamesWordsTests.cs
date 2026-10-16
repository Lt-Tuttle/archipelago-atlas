using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.Tests;

/// <summary>Add games' words: sizes, the totals, the one confirm and the summary's lines.</summary>
public sealed class AddGamesWordsTests
{
    private const long Mb = 1024 * 1024;
    private static string Tr(string text) => text;

    private static AddGameFacts Community(string game, long apworld, long pack) => new()
    {
        Game = game,
        Versions = new[] { new IndexVersionFacts("1.0.0", $"https://github.com/owner/{game}/releases/download/v1/{game}.apworld", "aa", apworld) },
        Packs = pack < 0 ? Array.Empty<IndexPackFacts>() : new[] { new IndexPackFacts("packs/" + game, "v1", game + ".zip", "bb", pack, 90, Array.Empty<string>()) }
    };

    [Fact]
    public void Sizes_are_said_in_megabytes_with_what_isn_t_listed()
    {
        Assert.Equal("", AddGamesWords.Megabytes(0, Tr));
        Assert.Equal("under 0.1 MB", AddGamesWords.Megabytes(1000, Tr));
        Assert.Equal("about 3.4 MB", AddGamesWords.Megabytes((long)(3.4 * Mb), Tr));
        Assert.Equal("about 48 MB", AddGamesWords.Megabytes(48 * Mb, Tr));
        Assert.Equal("about 13 MB", AddGamesWords.Megabytes((long)(12.6 * Mb), Tr));
        Assert.Equal("about 48 MB, besides 2 files whose size isn't listed", AddGamesWords.Download(48 * Mb, 2, Tr));
        Assert.Equal("about 48 MB, besides 1 file whose size isn't listed", AddGamesWords.Download(48 * Mb, 1, Tr));
        Assert.Equal("size not listed", AddGamesWords.Download(0, 1, Tr));
        Assert.Equal("3 sizes not listed", AddGamesWords.Download(0, 3, Tr));
    }

    [Fact]
    public void The_totals_count_the_games_that_add_something()
    {
        var plan = AddGamesPlan.Make(new[] { Community("alpha", 3 * Mb, 2 * Mb) }, new AddContext());
        Assert.Equal("1 game: about 5 MB to download, about 8 MB on this PC.", AddGamesWords.Totals(plan, Tr));
        Assert.Equal("Tick the games to add: the totals show here.", AddGamesWords.Totals(AddGamesPlan.Make(Array.Empty<AddGameFacts>(), new AddContext()), Tr));
    }

    [Fact]
    public void The_confirm_names_every_project_and_what_pressing_it_trusts()
    {
        var plan = AddGamesPlan.Make(new[] { Community("alpha", 3 * Mb, 2 * Mb) }, new AddContext());
        var w = AddGamesWords.Confirm(plan, gitHubAllowed: true, freeBytes: null, floorBytes: 700 * Mb, Tr);
        Assert.Equal("Install 1 game", w.Title);
        Assert.Equal("Atlas downloads about 5 MB and uses about 8 MB on this PC.", w.Lead);
        Assert.Contains("The games' logic (apworlds) from github.com/owner/alpha.", w.Sources);
        Assert.Contains("Map packs from github.com/packs/alpha.", w.Sources);
        Assert.Contains("Pressing Download and install trusts github.com/owner/alpha;", w.Trust);
        Assert.Null(w.GitHub);
        Assert.Null(w.Room);
        var trusted = AddGamesPlan.Make(new[] { Community("alpha", 3 * Mb, 2 * Mb) }, new AddContext { TrustedProjects = new[] { "github.com/owner/alpha" } });
        Assert.DoesNotContain("Pressing Download and install trusts", AddGamesWords.Confirm(trusted, true, null, 700 * Mb, Tr).Trust);
    }

    [Fact]
    public void GitHub_is_asked_only_for_a_release_list_and_a_full_disk_stops_the_confirm()
    {
        var live = AddGamesPlan.Make(new[] { new AddGameFacts { Game = "mine", LiveProject = "me/mine" } }, new AddContext());
        Assert.Equal("First Atlas reads the releases of github.com/me/mine on GitHub (at most 4 requests); pressing Download and install allows that until Atlas closes. It also reads the map pack project that the setup guide of mine names, once the logic is in.",
            AddGamesWords.Confirm(live, gitHubAllowed: false, null, 700 * Mb, Tr).GitHub);
        Assert.Null(AddGamesWords.Confirm(live, gitHubAllowed: true, null, 700 * Mb, Tr).GitHub);
        var plan = AddGamesPlan.Make(new[] { Community("alpha", 3 * Mb, 2 * Mb) }, new AddContext());
        Assert.Equal("There isn't room: these games take about 8 MB on this PC, and Atlas keeps about 700 MB free for its engine. Free some space or tick fewer games.",
            AddGamesWords.Confirm(plan, true, freeBytes: 704 * Mb, floorBytes: 700 * Mb, Tr).Room);
    }

    [Fact]
    public void An_official_game_names_Archipelago_s_release_once_and_its_packages()
    {
        var facts = new AddGameFacts { Game = "Zelda", Official = true, WorldsToUnpack = new[] { new WorldFolderFacts("alttp", 5 * Mb, true) } };
        var w = AddGamesWords.Confirm(AddGamesPlan.Make(new[] { facts }, new AddContext { WorldsZipBytes = 22 * Mb }), true, null, 700 * Mb, Tr);
        Assert.Contains("Archipelago's own release (about 22 MB, once) for the games that ship with it.", w.Sources);
        Assert.Contains("Some of their worlds (alttp) need packages of their own, installed from PyPI; their size isn't listed.", w.Sources);
        // No pack listed: the one its setup guide names comes from GitHub, so it isn't all Archipelago's release.
        Assert.Contains("A map pack from the project that the setup guide of Zelda names, read on GitHub once the logic is in.", w.Sources);
        Assert.Equal("Map packs' scripts run inside Atlas's sandbox: only continue if you trust these projects.", w.Trust);
        Assert.NotEqual("Everything comes from Archipelago's own release, checked against its published SHA-256.", w.Trust);
        var installed = AddGamesWords.Confirm(AddGamesPlan.Make(new[] { facts with { PackInstalled = true } }, new AddContext { WorldsZipBytes = 22 * Mb }), true, null, 700 * Mb, Tr);
        Assert.Equal("Everything comes from Archipelago's own release, checked against its published SHA-256.", installed.Trust);
    }

    [Fact]
    public void A_release_Atlas_keeps_and_a_pack_alone_are_said_too()
    {
        var shipped = AddGamesPlan.Make(new[] { new AddGameFacts { Game = "Zelda", Official = true, WorldsToUnpack = new[] { new WorldFolderFacts("alttp", 5 * Mb, false) } } },
            new AddContext { WorldsZipPresent = true, WorldsZipBytes = 22 * Mb });
        var w = AddGamesWords.Confirm(shipped, true, null, 700 * Mb, Tr);
        Assert.Equal("Nothing needs downloading; Atlas uses about 5 MB on this PC.", w.Lead);
        Assert.Contains("The games that ship with Archipelago are unpacked from its release, which Atlas has already.", w.Sources);
        var packOnly = AddGamesPlan.Make(new[] { new AddGameFacts { Game = "beta", LogicInstalled = true, Packs = new[] { new IndexPackFacts("packs/beta", "v1", "beta.zip", "bb", 2 * Mb, 90, Array.Empty<string>()) } } }, new AddContext());
        Assert.StartsWith("Map packs' scripts run inside Atlas's sandbox", AddGamesWords.Confirm(packOnly, true, null, 700 * Mb, Tr).Trust);
        var logicOnly = new AddGamesOutcome(new[] { ("a", true, false) });
        logicOnly.For("a").Logic = AddStep.Done(null);
        Assert.Equal("Done: logic added for 1 of 1.", AddGamesWords.Headline(logicOnly, Tr));
        Assert.Equal("Logic: added.", AddGamesWords.LogicLine(logicOnly.For("a"), Tr));
        Assert.Equal("Map: a map pack is installed already.", AddGamesWords.MapLine(logicOnly.For("a"), Tr));
        var mapOnly = new AddGamesOutcome(new[] { ("b", false, true) });
        mapOnly.For("b").Map = AddStep.Done(null);
        Assert.Equal("Done: map packs added for 1 of 1.", AddGamesWords.Headline(mapOnly, Tr));
        Assert.Equal("Map: installed.", AddGamesWords.MapLine(mapOnly.For("b"), Tr));
        Assert.Equal("Done: nothing needed adding.", AddGamesWords.Headline(new AddGamesOutcome(new[] { ("c", false, false) }), Tr));
        mapOnly.For("b").GuideRepo = "x/y";
        mapOnly.For("b").PackNote = AddPackNote.GuideNotListedBudgetSpent;
        Assert.Equal("Its setup guide names github.com/x/y; this add's share of GitHub's hourly limit was spent before Atlas could try it.", AddGamesWords.GuideLine(mapOnly.For("b"), Tr));
        mapOnly.For("b").PackNote = AddPackNote.None;
        Assert.Null(AddGamesWords.GuideLine(mapOnly.For("b"), Tr));
    }

    [Fact]
    public void The_summary_says_per_game_what_was_added_and_why_not()
    {
        var o = new AddGamesOutcome(new[] { ("alpha", true, true), ("beta", true, true), ("gamma", false, true) });
        o.For("alpha").Logic = AddStep.Done("1.2.0");
        o.For("alpha").Map = AddStep.Done("github.com/packs/alpha v1");
        o.For("alpha").PackFromGuide = true;
        o.For("beta").Logic = AddStep.Failed("its project no longer offers that file");
        o.For("beta").Map = AddStep.Unavailable("no map pack is known for it yet");
        o.For("gamma").Map = AddStep.Done("github.com/packs/gamma v2");
        Assert.Equal("Done: logic added for 1 of 2, map packs for 2 of 3. One needs a look below.", AddGamesWords.Headline(o, Tr));
        Assert.Equal("Logic: version 1.2.0 added.", AddGamesWords.LogicLine(o.For("alpha"), Tr));
        Assert.Equal("Map: github.com/packs/alpha v1, the one its setup guide names.", AddGamesWords.MapLine(o.For("alpha"), Tr));
        Assert.Equal("Logic: not added: its project no longer offers that file.", AddGamesWords.LogicLine(o.For("beta"), Tr));
        Assert.Equal("Map: no map pack is known for it yet.", AddGamesWords.MapLine(o.For("beta"), Tr));
        Assert.Equal("Logic: already in the Atlas Engine.", AddGamesWords.LogicLine(o.For("gamma"), Tr));
        o.For("beta").Logic = AddStep.Failed("The Atlas Engine isn't set up yet.");
        Assert.Equal("Logic: not added: The Atlas Engine isn't set up yet.", AddGamesWords.LogicLine(o.For("beta"), Tr)); // one full stop
        o.For("beta").GuideRepo = "x/y";
        o.For("beta").PackNote = AddPackNote.GuideNotListedNoLookup;
        Assert.Equal("Its setup guide names github.com/x/y, which Atlas's game list doesn't have.", AddGamesWords.GuideLine(o.For("beta"), Tr));
        o.Cancelled = true;
        Assert.Equal("Stopped. Before it stopped, Atlas added logic for 1 and map packs for 2; nothing half-done was kept.", AddGamesWords.Headline(o, Tr));
    }
}
