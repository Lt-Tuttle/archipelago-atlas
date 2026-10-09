using AP_Atlas.Core.Games;
using AP_Atlas.Core.Reports;
using Newtonsoft.Json;

namespace AP_Atlas.Core.Tests;

public class SoloTestReportTests
{
    private static SoloTestResult Full() => new("Dark Souls III", new DateTime(2026, 10, 8, 14, 2, 0),
        new SoloVersions("Dark Souls III", "3.0.0", "github.com/nex3/DS3", "0123456789abcdef", "Atlas portable engine", "DS3 pack", "1.4", "https://example.org/versions.json", "0.1.0-beta.1"),
        new[] { new SoloStepRecord(SoloTestStep.Apworld, SoloStepOutcome.Skipped, "already in the engine", 0.2), new SoloStepRecord(SoloTestStep.Generate, SoloStepOutcome.Done, @"seed A1B2 in C:\Users\kimj\x", 94.2) },
        new SoloGeneration("A1B2", "41097051284819253226", 512, 512, 17, 23, 94.2, true, null, 41, "template"),
        new SoloLogic(true, 17, 0, 0, "exact: Atlas's logic matches the seed at every one of 17 spheres", true, 23, 512, "linked", null, 28, 5, new[] { "Firelink Shrine: Coiled Sword" }, Array.Empty<string>()),
        new SoloPins(512, 500, 300, 290, 10, 2, 7, new[] { "Undead Asylum: Dungeon Cell Key", "Firelink Shrine: Homeward Bone" }, new[] { "Pin \"Asylum\" matches no check" }), new SoloKeyItems(80, 78, 60),
        new SoloScripts(true, new[] { "Archipelago.LocationChecks" }, new[] { "ds3_map" }, Array.Empty<string>(), true, false, false, null, 1), "127.0.0.1:54321", null,
        FindingCatalog.Group(new[]
        {
            new SoloRawFinding("tile:noimage:jack", "Key Items", "Warning", "Tile \"jack\" has no image"),
            new SoloRawFinding("tile:noimage:golem", "Key Items", "Warning", "Tile \"golem\" has no image"),
            new SoloRawFinding("map:nobg:Crates", "Maps", "Problem", "Map \"Crates\" has no background", "'images/maps/Crates.png' isn't in the pack."),
            new SoloRawFinding("auto:tile:x", "Key Items", "AutoFixed", "Linked tile 'x' to X (the names match)"),
            new SoloRawFinding("loc:unmatched:Asylum|", "Locations", "Ignored", "Pin section \"Asylum\" isn't linked"),
        }));

    [Fact]
    public void Markdown_has_every_heading_in_order_and_says_not_scored_for_missing_parts()
    {
        var scrubber = new Scrubber("kimj", names: new[] { "Tester" });
        string full = SoloTestReport.Markdown(Full(), scrubber);
        int last = -1;
        foreach (string heading in SoloTestReport.Headings)
        {
            int at = full.IndexOf(heading + Environment.NewLine, StringComparison.Ordinal);
            Assert.True(at > last, heading + " is missing or out of order");
            last = at;
        }
        Assert.Contains("(none yet)", full);
        Assert.Contains("500 of 512 (98%)", full);
        Assert.Contains("(41097051284819253226)", full);
        Assert.Contains("28 of 512 locations reachable (23 to do, 5 excluded by the seed)", full);
        Assert.Contains("- Not on any map (12; the first 2): Undead Asylum: Dungeon Cell Key; Firelink Shrine: Homeward Bone", full);
        Assert.Contains("- To review: Pin \"Asylum\" matches no check", full);
        Assert.Contains("In sphere 0 but not reachable at connect (1): Firelink Shrine: Coiled Sword", full);
        Assert.Contains("identical", SoloTestReport.Markdown(Full() with { Logic = Full().Logic! with { NotReached = null } }, scrubber));
        Assert.Contains("patch output was skipped", full);

        var bare = Full() with { Generation = null, Logic = null, Pins = null, KeyItems = null, Scripts = null };
        string sparse = SoloTestReport.Markdown(bare, scrubber);
        foreach (string heading in SoloTestReport.Headings) Assert.Contains(heading, sparse);
        Assert.Equal(5, sparse.Split("Not scored:").Length - 1);
        Assert.Contains("the pack wasn't used", sparse);
        Assert.Contains("didn't finish during the test", SoloTestReport.Markdown(bare with { Logic = Full().Logic }, scrubber));
        Assert.Contains("no map pack", SoloTestReport.Markdown(bare with { Versions = bare.Versions with { PackName = null } }, scrubber));
    }

    [Fact]
    public void Paths_and_addresses_are_scrubbed_and_numbers_survive()
    {
        var scrubber = new Scrubber("kimj", names: new[] { "Tester" });
        string md = SoloTestReport.Markdown(Full(), scrubber);
        Assert.DoesNotContain(@"C:\Users", md);
        Assert.Contains("<path>", md);
        Assert.Contains("| Generate | Done | 94.2 s |", md);
        string json = SoloTestReport.Json(Full(), scrubber);
        Assert.Contains("<ip>", json);
        Assert.DoesNotContain("127.0.0.1", json);
        Assert.DoesNotContain("kimj", json);
        Assert.Contains("\"Sphere0\": 23", json);
        Assert.Contains("\"Step\": \"Generate\"", json);
        Assert.Contains("\"Outcome\": \"Done\"", json);
        var back = JsonConvert.DeserializeObject<SoloTestResult>(json);
        Assert.NotNull(back);
        Assert.Equal(2, back!.Schema);
        Assert.Equal(3, back.Findings!.Count(f => f.Severity != "Ignored"));
        Assert.Equal(23, back.Generation!.Sphere0);
    }

    [Fact]
    public void Notes_replace_earlier_notes_and_the_file_stem_is_stable()
    {
        var scrubber = new Scrubber(null);
        string md = SoloTestReport.Markdown(Full(), scrubber);
        var notes = new SoloOwnerNotes("The Firelink pin sits in the sea.", "", "A ROM? No: the game itself.", "fine");
        string once = SoloTestReport.WithNotes(md, notes);
        string twice = SoloTestReport.WithNotes(once, notes with { Other = "changed" });
        Assert.Equal(1, twice.Split(SoloTestReport.NotesHeading).Length - 1);
        Assert.Contains("Firelink pin", twice);
        Assert.Contains("changed", twice);
        Assert.DoesNotContain("fine", twice.Substring(twice.IndexOf(SoloTestReport.NotesHeading, StringComparison.Ordinal)));
        Assert.Contains("(nothing)", twice);
        Assert.Equal("Dark_Souls_III-20261008-1402", SoloTestReport.FileStem("Dark Souls III", new DateTime(2026, 10, 8, 14, 2, 0)));
        Assert.StartsWith(SoloTestReport.StemPrefix("Dark Souls III"), SoloTestReport.FileStem("Dark Souls III", DateTime.Now));
        Assert.Equal("n/a", SoloTestReport.Percent(3, 0));
        Assert.Equal("github.com/routhken/Dark_Souls_Remastered_tracker", SoloTestReport.PublicSource("https://raw.githubusercontent.com/routhken/Dark_Souls_Remastered_tracker/refs/heads/main/versions.json"));
        Assert.Equal("github.com/owner/pack", SoloTestReport.PublicSource("https://github.com/owner/pack/releases"));
        Assert.Equal("example.org/packs/versions.json", SoloTestReport.PublicSource("https://example.org/packs/versions.json"));
        Assert.Null(SoloTestReport.PublicSource(""));
        Assert.Null(SoloTestReport.PublicSource("file:///C:/x.json"));
        Assert.Contains("from github.com/a/b", SoloTestReport.Markdown(Full() with { Versions = Full().Versions with { PackSource = "github.com/a/b" } }, new Scrubber(null)));
        Assert.Equal("50%", SoloTestReport.Percent(1, 2));
    }

    [Fact]
    public void Findings_are_grouped_explained_and_the_author_gets_only_the_packs_problems()
    {
        string md = SoloTestReport.Markdown(Full(), new Scrubber(null));
        int problem = md.IndexOf("### Problem: 1 maps have no picture (Maps)", StringComparison.Ordinal);
        int check = md.IndexOf("### Check: 2 tiles have no image (Key Items)", StringComparison.Ordinal);
        int handled = md.IndexOf("### Handled: Atlas linked 1 pins or tiles", StringComparison.Ordinal);
        Assert.True(problem > 0 && check > problem && handled > check, "the groups aren't there, most serious first");
        Assert.Contains("- Found: Tile \"jack\" has no image; Tile \"golem\" has no image", md);
        Assert.Contains("- Found: Map \"Crates\" has no background; 'images/maps/Crates.png' isn't in the pack.", md);
        Assert.Contains("- Atlas: You chose to ignore these in the Pack Doctor.", md);
        string author = md.Substring(md.IndexOf(SoloTestReport.AuthorHeading, StringComparison.Ordinal));
        author = author.Substring(0, author.IndexOf(SoloTestReport.NotesHeading, StringComparison.Ordinal));
        Assert.Contains("These tiles' images aren't in the pack. (2)", author);
        Assert.Contains("These maps' pictures aren't in the pack, or can't be read. (1)", author);
        Assert.DoesNotContain("names match", author);
        Assert.DoesNotContain("Asylum", author);
        Assert.Contains("No map pack was tested.", SoloTestReport.Markdown(Full() with { Versions = Full().Versions with { PackName = null } }, new Scrubber(null)));
        Assert.Contains("Not gathered", SoloTestReport.Markdown(Full() with { Findings = null }, new Scrubber(null)));
    }

    [Fact]
    public void Every_kind_the_Doctor_and_the_test_produce_is_explained()
    {
        string[] kinds =
        {
            "names:none", "names:diff", "file", "file:nomapping", "auto", "tile:stalefix", "fixes:duplicate", "superseded",
            "script:load", "script:none", "script:stopped", "script:error", "script:unsupported", "script:follows-maps", "script:writes",
            "script:unread-files", "script:noautotracking", "settings:noslotdata", "settings:summary", "settings:missing", "grid:hidden",
            "tile:unknown", "tile:unlinked", "tile:noimage", "itemmap:stale", "loc:unmatched", "loc:loose", "locmap:dangling", "locmap:stale",
            "loc:unplaced", "map:nobg", "map:empty", "map:outside",
            "logic:differs", "sphere0:notreached", "sphere0:beyond", "seed:unplaced", "setup:guide",
        };
        var missing = kinds.Where(k => !FindingCatalog.Kinds.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "no explanation for: " + string.Join(", ", missing));
        Assert.Equal("tile:noimage", FindingCatalog.KindOf("tile:noimage:characters/base/jack"));
        Assert.Equal("file", FindingCatalog.KindOf("file:Image 'x.png' isn't in the pack's zip any more."));
        Assert.Equal("file:nomapping", FindingCatalog.KindOf("file:nomapping"));
        Assert.Equal("auto", FindingCatalog.KindOf("auto:tile:x"));
        Assert.Equal("brand", FindingCatalog.KindOf("brand:new"));
        Assert.Contains("brand", FindingCatalog.Explain("brand").Summary);
    }

    [Fact]
    public void Write_makes_both_files_in_the_folder()
    {
        using var dir = new TempFolder();
        var (md, json) = SoloTestReport.Write(dir.Path, Full(), new Scrubber(null), new DateTime(2026, 10, 8, 14, 2, 0));
        Assert.True(File.Exists(md) && File.Exists(json));
        Assert.EndsWith("Dark_Souls_III-20261008-1402.md", md);
        Assert.Contains("# Solo test: Dark Souls III", File.ReadAllText(md));
    }
}

public class SoloYamlTests
{
    [Fact]
    public void A_template_with_comments_and_a_bom_passes()
    {
        string text = "\uFEFF# Archipelago options\nname: AtlasTest\ndescription: Atlas solo test\ngame: Dark Souls III\nDark Souls III:\n  progression_balancing: 50\n  accessibility: full\n  # a comment\n  enable_dlc: 'false'\n";
        var check = SoloYaml.Inspect(text);
        Assert.True(check.Ok, string.Join("; ", check.Problems));
        Assert.Equal("Dark Souls III", check.Game);
        Assert.Equal(3, check.Options);
    }

    [Theory]
    [InlineData("name: Player{number}\ngame: X\nX:\n  a: 1\n", "name")]
    [InlineData("name: AtlasTest\ngame: X\n", "no section")]
    [InlineData("name: AtlasTest\nX:\n  a: 1\n", "no game")]
    [InlineData("name: AtlasTest\ngame: X\nX:\n  a: 1\n---\nname: AtlasTest\ngame: Y\nY:\n  b: 1\n", "2 documents")]
    [InlineData("", "no document")]
    public void Problems_are_named(string text, string expected)
    {
        var check = SoloYaml.Inspect(text);
        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }
}
