using System.IO.Compression;
using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.Tests;

/// <summary>What a release of an apworld says about itself, which release Atlas picks, and where the facts are kept.</summary>
public sealed class ReleaseFactsTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Apworld(string name, params (string Entry, string Text)[] files)
    {
        string path = _temp.File(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(text);
        }
        return path;
    }

    [Fact]
    public void A_release_names_the_trackers_its_guide_links_and_the_Archipelago_its_manifest_asks_for()
    {
        string file = Apworld("ds2.apworld",
            ("dark_souls_2/archipelago.json", """{"game": "Dark Souls II", "world_version": "0.6.0", "minimum_ap_version": "0.6.2", "maximum_ap_version": "0.6.9"}"""),
            ("dark_souls_2/docs/setup_en.md", "# Setup\n\n- [Map tracker](https://github.com/AkagiCritMagnet/DS2-Tracker)\n- [PopTracker](https://github.com/black-sliver/PopTracker)\n"),
            ("dark_souls_2/docs/en_Dark Souls II.md", "See also the [tracker](https://github.com/other/ds2-tracker/releases)."),
            ("dark_souls_2/__init__.py", "game = 'Dark Souls II'"));
        var record = ReleaseFacts.Read(file, "github.com/WildBunnie/DarkSoulsII-Archipelago", "0.6.0-alpha.2", prerelease: true);
        Assert.True(record.HasGuide);
        Assert.Equal(new[] { "AkagiCritMagnet/DS2-Tracker", "other/ds2-tracker" }, record.GuideTrackers);
        Assert.Equal("Dark Souls II", record.Game);
        Assert.Equal("0.6.0", record.WorldVersion);
        Assert.Equal("0.6.2", record.MinimumAp);
        Assert.Equal("0.6.9", record.MaximumAp);
        Assert.True(record.Prerelease);
    }

    [Fact]
    public void A_release_without_docs_or_manifest_says_nothing_and_a_broken_manifest_is_passed_over()
    {
        var bare = ReleaseFacts.Read(Apworld("bare.apworld", ("ds2/__init__.py", "")), "github.com/a/b", "0.5.5", false);
        Assert.False(bare.HasGuide);
        Assert.Empty(bare.GuideTrackers);
        Assert.Null(bare.MinimumAp);
        var broken = ReleaseFacts.Read(Apworld("broken.apworld", ("ds2/archipelago.json", "{not json")), "github.com/a/b", "0.5.6", false);
        Assert.Null(broken.Game);
        string notZip = _temp.File("not_a_zip.apworld");
        File.WriteAllText(notZip, "not a zip");
        Assert.Throws<InvalidDataException>(() => ReleaseFacts.Read(notZip, "x", "1", false));
    }

    [Theory]
    [InlineData("0.6.2", null, "0.6.7", null)]
    [InlineData("0.7.0", null, "0.6.7", "needs Archipelago 0.7.0 or newer; the engine has 0.6.7")]
    [InlineData(null, "0.5.1", "0.6.7", "is for Archipelago 0.5.1 at most; the engine has 0.6.7")]
    [InlineData("0.7.0", null, null, null)]
    public void A_manifest_says_which_Archipelago_a_release_runs_on(string? minimum, string? maximum, string? engine, string? problem) =>
        Assert.Equal(problem, ReleaseFacts.ArchipelagoProblem(new ReleaseRecord { Version = "1", MinimumAp = minimum, MaximumAp = maximum }, engine));

    private static ReleaseRecord Release(string version, bool pre = false, string? tracker = null, int? linkedOf100 = null, string? minimumAp = null, string? loadError = null)
    {
        var r = new ReleaseRecord { Project = "github.com/a/b", Version = version, Prerelease = pre, MinimumAp = minimumAp, LoadError = loadError };
        if (tracker != null) r.GuideTrackers.Add(tracker);
        if (linkedOf100 is { } linked) r.PackFits["pack"] = new PackFitRecord { SectionsTotal = 100, SectionsLinked = linked, MappedIds = 50, UnknownIds = 0 };
        return r;
    }

    [Fact]
    public void The_pre_release_whose_guide_names_the_tracker_beats_a_newer_full_release_without_docs()
    {
        var releases = new[] { Release("0.6.1"), Release("0.6.0-alpha.2", pre: true, tracker: "AkagiCritMagnet/DS2-Tracker"), Release("0.5.5") };
        var pick = ReleaseChoice.Best(releases, null, "0.6.7");
        Assert.Equal("0.6.0-alpha.2", pick!.Release.Version);
        Assert.Equal("its own setup guide names the map tracker github.com/AkagiCritMagnet/DS2-Tracker (a pre-release)", pick.Why);
    }

    [Fact]
    public void A_measured_fit_beats_the_guide_and_the_newest_fitting_release_wins()
    {
        var releases = new[] { Release("3.0", linkedOf100: 40), Release("2.0", tracker: "x/y", linkedOf100: 50), Release("1.5", linkedOf100: 97), Release("1.0", linkedOf100: 99) };
        var pick = ReleaseChoice.Best(releases, "pack", "0.6.7");
        Assert.Equal("1.5", pick!.Release.Version);
        Assert.Equal("your map pack fits it: 97% of its pins link", pick.Why);
        // With nothing fitting and no guide naming a tracker, the best measured.
        var poor = ReleaseChoice.Best(new[] { Release("3.0", linkedOf100: 40), Release("2.0", linkedOf100: 60) }, "pack", "0.6.7");
        Assert.Equal("2.0", poor!.Release.Version);
        Assert.StartsWith("your map pack fits it best", poor.Why);
    }

    [Fact]
    public void Releases_that_cant_run_here_are_passed_over_and_with_nothing_said_the_newest_full_release_is_taken()
    {
        var releases = new[] { Release("2.0", minimumAp: "0.9.0", tracker: "x/y"), Release("1.9", loadError: "boom", tracker: "x/y"), Release("1.8-beta", pre: true), Release("1.7") };
        var pick = ReleaseChoice.Best(releases, null, "0.6.7");
        Assert.Equal("1.7", pick!.Release.Version);
        Assert.Equal("the newest full release", pick.Why);
        Assert.Null(ReleaseChoice.Best(new[] { Release("2.0", loadError: "boom") }, null, "0.6.7"));
    }

    [Fact]
    public void Why_a_release_doesnt_work_names_the_first_reason()
    {
        Assert.Equal("it couldn't be loaded in the engine", ReleaseFacts.WhyNot(Release("1", loadError: "x", minimumAp: "9.0"), "0.6.7", null, false));
        Assert.Equal("it needs Archipelago 9.0 or newer; the engine has 0.6.7", ReleaseFacts.WhyNot(Release("1", minimumAp: "9.0"), "0.6.7", null, false));
        Assert.Equal("your map pack places 40% of its pins with it", ReleaseFacts.WhyNot(Release("1", linkedOf100: 40), "0.6.7", "pack", anotherFits: true));
        Assert.Null(ReleaseFacts.WhyNot(Release("1", linkedOf100: 40), "0.6.7", "pack", anotherFits: false));
    }

    [Fact]
    public void The_store_keeps_one_record_per_release_in_the_games_folder_and_keeps_its_pack_fits()
    {
        var store = ReleaseFactsStore.Load(_temp.Path, "Dark Souls II");
        Assert.Empty(store.Records);
        Assert.False(File.Exists(ReleaseFactsStore.PathFor(_temp.Path, "Dark Souls II")));
        var first = Release("v0.6.0", linkedOf100: 97);
        first.Sha256 = "AA";
        store.Put(first);
        var again = new ReleaseRecord { Project = "github.com/a/b", Version = "0.6.0", Sha256 = "aa", GuideTrackers = { "x/y" } };
        store.Put(again);
        var reloaded = ReleaseFactsStore.Load(_temp.Path, "Dark Souls II");
        var only = Assert.Single(reloaded.Records);
        Assert.Equal(new[] { "x/y" }, only.GuideTrackers);
        Assert.Equal(97, only.FitOf("pack")!.SectionsLinked);
        Assert.Same(only, reloaded.Find("github.com/A/B", "v0.6.0"));
        Assert.StartsWith(Path.Combine(_temp.Path, "games"), ReleaseFactsStore.PathFor(_temp.Path, "Dark Souls II"));
    }
}
