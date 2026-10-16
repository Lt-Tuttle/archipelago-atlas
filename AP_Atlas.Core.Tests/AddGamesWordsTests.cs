using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.Tests;

/// <summary>Install games' words, few per element: sizes, the totals, the one confirm (a line per game) and the summary's items per game.</summary>
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
    public void Sizes_are_said_in_megabytes_with_how_many_are_unlisted()
    {
        Assert.Equal("", AddGamesWords.Megabytes(0, Tr));
        Assert.Equal("under 0.1 MB", AddGamesWords.Megabytes(1000, Tr));
        Assert.Equal("about 3.4 MB", AddGamesWords.Megabytes((long)(3.4 * Mb), Tr));
        Assert.Equal("about 48 MB", AddGamesWords.Megabytes(48 * Mb, Tr));
        Assert.Equal("about 13 MB", AddGamesWords.Megabytes((long)(12.6 * Mb), Tr));
        Assert.Equal("about 48 MB + 2 unlisted", AddGamesWords.Download(48 * Mb, 2, Tr));
        Assert.Equal("about 48 MB + 1 unlisted", AddGamesWords.Download(48 * Mb, 1, Tr));
        Assert.Equal("size not listed", AddGamesWords.Download(0, 1, Tr));
        Assert.Equal("3 sizes not listed", AddGamesWords.Download(0, 3, Tr));
    }

    [Fact]
    public void The_totals_count_the_games_that_add_something()
    {
        var plan = AddGamesPlan.Make(new[] { Community("alpha", 3 * Mb, 2 * Mb) }, new AddContext());
        Assert.Equal("1 game · about 5 MB to download · about 8 MB on this PC", AddGamesWords.Totals(plan, Tr));
        Assert.Equal("Tick the games to install.", AddGamesWords.Totals(AddGamesPlan.Make(Array.Empty<AddGameFacts>(), new AddContext()), Tr));
    }

    [Fact]
    public void The_confirm_is_a_line_per_game_and_its_projects_with_what_installing_trusts_in_a_tooltip()
    {
        var plan = AddGamesPlan.Make(new[] { Community("alpha", 3 * Mb, 2 * Mb), Community("beta", 1 * Mb, -1) with { GuideRead = true } }, new AddContext());
        var w = AddGamesWords.Confirm(plan, gitHubAllowed: true, freeBytes: null, floorBytes: 700 * Mb, Tr);
        Assert.Equal("Install 2 games · about 6 MB", w.Title);
        Assert.Equal("about 6 MB to download, about 10 MB on this PC.", w.TitleTooltip);
        // One line per game: its name, what it gets, its size; where each part comes from in its tooltip.
        Assert.Equal(new[] { ("alpha", "logic + map", "about 5 MB"), ("beta", "logic (no map known)", "about 1 MB") }, w.Games.Select(g => (g.Game, g.Gets, g.Size)));
        Assert.Equal("Its logic and a map pack.\nLogic 1.0.0: its newest full release.\nLogic from github.com/owner/alpha.\nMap pack from github.com/packs/alpha.", w.Games[0].Tooltip);
        Assert.Equal("From 3 projects", w.Projects);
        Assert.Equal("github.com/owner/alpha: logic · alpha\ngithub.com/owner/beta: logic · beta\ngithub.com/packs/alpha: map · alpha\n\n"
            + "Apworlds and map packs run code. Installing trusts the new ones, github.com/owner/alpha, github.com/owner/beta; Settings → Privacy & permissions takes one back.", w.ProjectsTooltip);
        Assert.Null(w.GitHub);
        Assert.Null(w.Room);
        var trusted = AddGamesPlan.Make(new[] { Community("alpha", 3 * Mb, 2 * Mb) }, new AddContext { TrustedProjects = new[] { "github.com/owner/alpha" } });
        var t = AddGamesWords.Confirm(trusted, true, null, 700 * Mb, Tr);
        Assert.Equal("From 2 projects", t.Projects);
        Assert.EndsWith("\n\nApworlds and map packs run code. Install only from projects you trust.", t.ProjectsTooltip);
        var one = AddGamesPlan.Make(new[] { Community("gamma", 3 * Mb, -1) with { GuideRead = true } }, new AddContext());
        var o = AddGamesWords.Confirm(one, true, null, 700 * Mb, Tr);
        Assert.Equal(("Install 1 game · about 3 MB", "From 1 project"), (o.Title, o.Projects));
        Assert.EndsWith("Apworlds run code. Installing trusts the new one, github.com/owner/gamma; Settings → Privacy & permissions takes it back.", o.ProjectsTooltip);
    }

    [Fact]
    public void GitHub_lookups_are_one_line_when_asked_and_a_full_disk_stops_the_confirm()
    {
        var live = AddGamesPlan.Make(new[] { new AddGameFacts { Game = "mine", LiveProject = "me/mine" } }, new AddContext());
        var w = AddGamesWords.Confirm(live, gitHubAllowed: false, null, 700 * Mb, Tr);
        Assert.Equal("Also allows up to 4 GitHub lookups until Atlas closes.", w.GitHub);
        Assert.Equal(("mine", "logic + the guide's map", "size not listed"), (w.Games[0].Game, w.Games[0].Gets, w.Games[0].Size));
        Assert.Contains("Logic from github.com/me/mine: its newest release is read on GitHub first.", w.Games[0].Tooltip);
        Assert.Contains("Its setup guide's map pack project is read on GitHub once its logic is in.", w.Games[0].Tooltip);
        Assert.Contains("The project mine's setup guide names: map", w.ProjectsTooltip);
        Assert.Null(AddGamesWords.Confirm(live, gitHubAllowed: true, null, 700 * Mb, Tr).GitHub);
        var plan = AddGamesPlan.Make(new[] { Community("alpha", 3 * Mb, 2 * Mb) }, new AddContext());
        Assert.Equal("Not enough room: these games need about 8 MB, and Atlas keeps about 700 MB free.", AddGamesWords.Confirm(plan, true, freeBytes: 704 * Mb, floorBytes: 700 * Mb, Tr).Room);
    }

    [Fact]
    public void An_official_game_names_Archipelago_s_release_once_and_its_packages()
    {
        var facts = new AddGameFacts { Game = "Zelda", Official = true, WorldsToUnpack = new[] { new WorldFolderFacts("alttp", 5 * Mb, true) } };
        var w = AddGamesWords.Confirm(AddGamesPlan.Make(new[] { facts }, new AddContext { WorldsZipBytes = 22 * Mb }), true, null, 700 * Mb, Tr);
        Assert.Equal("Install 1 game · about 22 MB", w.Title);
        Assert.Equal("in Archipelago's release", w.Games[0].Size); // its world downloads nothing of its own
        Assert.Contains("Logic from Archipelago's release (about 22 MB, once for every game that ships with it; checked by its SHA-256).", w.Games[0].Tooltip);
        Assert.Contains("Packages from PyPI for alttp; their size isn't listed.", w.Games[0].Tooltip);
        Assert.StartsWith("Archipelago's release: logic · Zelda\nPyPI: packages · alttp\nThe project Zelda's setup guide names: map\n\nMap packs run scripts. Install only from projects you trust.", w.ProjectsTooltip);
        var installed = AddGamesWords.Confirm(AddGamesPlan.Make(new[] { facts with { PackInstalled = true } }, new AddContext { WorldsZipBytes = 22 * Mb }), true, null, 700 * Mb, Tr);
        Assert.Equal("in Archipelago's release", installed.Games[0].Size);
        Assert.EndsWith("\n\nAll from Archipelago's release (checked by its SHA-256) and PyPI.", installed.ProjectsTooltip);
        // With a pack of its own, the line names the release too, so the lines add up to the title.
        var packed = facts with { Packs = new[] { new IndexPackFacts("packs/zelda", "v1", "zelda.zip", "bb", 3 * Mb, 90, Array.Empty<string>()) } };
        Assert.Equal("about 3 MB + Archipelago's release", AddGamesWords.Confirm(AddGamesPlan.Make(new[] { packed }, new AddContext { WorldsZipBytes = 22 * Mb }), true, null, 700 * Mb, Tr).Games[0].Size);
        var plain = facts with { PackInstalled = true, WorldsToUnpack = new[] { new WorldFolderFacts("alttp", 5 * Mb, false) } };
        Assert.EndsWith("\n\nAll from Archipelago's release, checked by its SHA-256.", AddGamesWords.Confirm(AddGamesPlan.Make(new[] { plain }, new AddContext { WorldsZipBytes = 22 * Mb }), true, null, 700 * Mb, Tr).ProjectsTooltip);
    }

    [Fact]
    public void A_kept_release_downloads_nothing_and_the_heading_counts_the_games_installed()
    {
        var shipped = AddGamesPlan.Make(new[] { new AddGameFacts { Game = "Zelda", Official = true, PackInstalled = true, WorldsToUnpack = new[] { new WorldFolderFacts("alttp", 5 * Mb, false) } } },
            new AddContext { WorldsZipPresent = true, WorldsZipBytes = 22 * Mb });
        var w = AddGamesWords.Confirm(shipped, true, null, 700 * Mb, Tr);
        Assert.Equal(("Install 1 game · nothing to download", "nothing to download, about 5 MB on this PC.", "–"), (w.Title, w.TitleTooltip, w.Games[0].Size));
        Assert.Contains("Logic from Archipelago's release, which Atlas has already.", w.Games[0].Tooltip);
        var packOnly = AddGamesPlan.Make(new[] { new AddGameFacts { Game = "beta", LogicInstalled = true, Packs = new[] { new IndexPackFacts("packs/beta", "v1", "beta.zip", "bb", 2 * Mb, 90, Array.Empty<string>()) } } }, new AddContext());
        Assert.EndsWith("Map packs run scripts. Install only from projects you trust.", AddGamesWords.Confirm(packOnly, true, null, 700 * Mb, Tr).ProjectsTooltip);
        var logicOnly = new AddGamesOutcome(new[] { ("a", true, false) });
        logicOnly.For("a").Logic = AddStep.Done(null);
        Assert.Equal("1 game installed", AddGamesWords.Heading(logicOnly, Tr));
        Assert.Null(AddGamesWords.HeadingTooltip(logicOnly, Tr));
        Assert.Equal(new AddChip(AddChipState.Ok, "logic", null), AddGamesWords.LogicChip(logicOnly.For("a"), Tr));
        Assert.Equal(new AddChip(AddChipState.Already, "map", "A map pack is installed already."), AddGamesWords.MapChip(logicOnly.For("a"), null, Tr));
        Assert.Equal("0 of 1 games installed", AddGamesWords.Heading(new AddGamesOutcome(new[] { ("c", false, false) }), Tr));
    }

    [Fact]
    public void A_games_line_is_one_number_per_item_with_the_details_in_tooltips()
    {
        var o = new AddGamesOutcome(new[] { ("alpha", true, true), ("beta", true, true), ("gamma", false, true), ("delta", true, true) });
        var alpha = o.For("alpha");
        alpha.Logic = AddStep.Done("1.2.0");
        alpha.LogicFitsPack = true;
        alpha.Map = AddStep.Done("github.com/packs/alpha v1");
        alpha.PackFromGuide = true;
        var look = new PackToLook("alpha", "k", "Alpha pack", "f", 35, 1129, 1150);
        Assert.Equal(new AddChip(AddChipState.Ok, "logic 1.2.0", "Version 1.2.0: the one its map pack was measured on."), AddGamesWords.LogicChip(alpha, Tr));
        Assert.Equal(new AddChip(AddChipState.Ok, "map 1129/1150", "From github.com/packs/alpha v1, the one its setup guide names.\n1129 of the game's 1150 checks have a pin."), AddGamesWords.MapChip(alpha, look, Tr));
        Assert.Equal(new AddChip(AddChipState.Ok, "35 fixes", "Atlas fixed 35 things in Alpha pack by itself; Review lists each, with Undo."), AddGamesWords.FixedChip(look, Tr));
        Assert.Equal("1 fix", AddGamesWords.FixedChip(look with { Fixed = 1 }, Tr)!.Text);
        Assert.False(AddGamesWords.PoorFit(look));
        // A pack that pins under three quarters of the game's checks fits poorly: the map is flagged, its tooltip says why.
        var poor = look with { Placed = 31, Total = 1288, Fixed = 0 };
        var map = AddGamesWords.MapChip(alpha, poor, Tr);
        Assert.Equal((AddChipState.Warn, "map 31/1288"), (map.State, map.Text));
        Assert.Contains("the pack likely fits another version of the game", map.Tooltip);
        Assert.True(AddGamesWords.PoorFit(poor));
        Assert.True(AddGamesWords.PoorFit(look with { Placed = 862, Total = 1150 }));
        Assert.False(AddGamesWords.PoorFit(look with { Placed = 863, Total = 1150 }));
        Assert.False(AddGamesWords.PoorFit(look with { Placed = 0, Total = 0 }));
        Assert.Null(AddGamesWords.FixedChip(poor, Tr));
        // An official game's logic says Archipelago's version.
        var beta = o.For("beta");
        beta.Logic = AddStep.Done("0.6.7");
        beta.LogicShips = true;
        beta.Map = AddStep.Unavailable("no map pack is known for it yet");
        Assert.Equal(new AddChip(AddChipState.Ok, "logic 0.6.7", "Ships with Archipelago 0.6.7."), AddGamesWords.LogicChip(beta, Tr));
        // An official game's poor fit: flagged, but no version of the game to find (its logic is Archipelago's world).
        var shipped = new AddGamesOutcome(new[] { ("civ", true, true) }).For("civ");
        shipped.Logic = AddStep.Done("0.6.7");
        shipped.LogicShips = true;
        shipped.Map = AddStep.Done("github.com/p/civ v1");
        var shippedMap = AddGamesWords.MapChip(shipped, poor, Tr);
        Assert.Equal(AddChipState.Warn, shippedMap.State);
        Assert.Contains("another Archipelago version", shippedMap.Tooltip);
        Assert.DoesNotContain("Find the version that fits", shippedMap.Tooltip);
        Assert.DoesNotContain("Find the version that fits", AddGamesWords.MapChip(alpha, poor, Tr, official: true).Tooltip);
        Assert.Equal(new AddChip(AddChipState.Skipped, "no map", "No map pack is known for it yet."), AddGamesWords.MapChip(beta, null, Tr));
        var gamma = o.For("gamma");
        gamma.Map = AddStep.Failed("the download stopped");
        Assert.Equal(new AddChip(AddChipState.Already, "logic", "Already in the Atlas Engine."), AddGamesWords.LogicChip(gamma, Tr));
        Assert.Equal(new AddChip(AddChipState.Bad, "map", "The download stopped."), AddGamesWords.MapChip(gamma, null, Tr));
        var delta = o.For("delta");
        delta.Logic = AddStep.Failed("The Atlas Engine isn't set up yet.");
        Assert.Equal(new AddChip(AddChipState.Bad, "logic", "The Atlas Engine isn't set up yet."), AddGamesWords.LogicChip(delta, Tr)); // one full stop
        delta.GuideRepo = "x/y";
        delta.PackNote = AddPackNote.GuideNotListedNoLookup;
        Assert.StartsWith("Its setup guide names github.com/x/y, which Atlas's game list doesn't have", AddGamesWords.GuideTip(delta, Tr));
        delta.PackNote = AddPackNote.None;
        Assert.Null(AddGamesWords.GuideTip(delta, Tr));
        // alpha and beta are in (an unknown map isn't a failure); gamma's map and delta's logic failed.
        Assert.Equal("2 of 4 games installed", AddGamesWords.Heading(o, Tr));
        o.Cancelled = true;
        Assert.Equal("Stopped · 2 of 4 games installed", AddGamesWords.Heading(o, Tr));
        Assert.Equal("Nothing half-done was kept; what was done stays.", AddGamesWords.HeadingTooltip(o, Tr));
    }

    [Fact]
    public void A_rows_tooltip_says_why_its_logic_version_was_picked()
    {
        var measured = new AddGameFacts
        {
            Game = "Saga",
            Versions = new[]
            {
                new IndexVersionFacts("0.5.5", "https://github.com/o/saga/releases/download/v0.5.5/saga.apworld", "a", 10),
                new IndexVersionFacts("0.6.0-alpha.2", "https://github.com/o/saga/releases/download/v0.6.0-alpha.2/saga.apworld", "b", 10)
            },
            Packs = new[] { new IndexPackFacts("p/saga", "v1", "saga.zip", "c", 10, 97, Array.Empty<string>(), "0.6.0-alpha.2") }
        };
        var game = AddGamesPlan.Make(new[] { measured }, new AddContext()).Games[0];
        Assert.Equal("Logic 0.6.0-alpha.2: the version its map pack was measured on.", AddGamesWords.VersionWhy(game, Tr));
        var newest = AddGamesPlan.Make(new[] { measured with { Packs = Array.Empty<IndexPackFacts>() } }, new AddContext()).Games[0];
        Assert.Equal("Logic 0.5.5: its newest full release.", AddGamesWords.VersionWhy(newest, Tr));
        var onlyPre = AddGamesPlan.Make(new[] { measured with { Packs = Array.Empty<IndexPackFacts>(), Versions = measured.Versions.Skip(1).ToList() } }, new AddContext()).Games[0];
        Assert.Equal("Logic 0.6.0-alpha.2: its newest release (a pre-release; there's no full one).", AddGamesWords.VersionWhy(onlyPre, Tr));
        Assert.Null(AddGamesWords.VersionWhy(AddGamesPlan.Make(new[] { new AddGameFacts { Game = "Z", LogicInstalled = true, Packs = measured.Packs } }, new AddContext()).Games[0], Tr));
    }
}
