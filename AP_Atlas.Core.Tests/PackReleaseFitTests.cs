using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.Tests;

/// <summary>Which of a project's releases fits the game's names: the newest that does, else the best tried.</summary>
public sealed class PackReleaseFitTests
{
    private static readonly PackReleaseFit ForNewerApworld = new("v2.0.1", 2116, 1860, 1600, 310);
    private static readonly PackReleaseFit ForThisApworld = new("v1.0.6", 1507, 1470, 1558, 0);
    private static readonly PackReleaseFit Outdated = new("v4.1.1", 444, 400, 437, 129);

    [Fact]
    public void A_release_whose_script_names_ids_the_game_lacks_doesnt_fit_though_most_of_its_pins_link()
    {
        Assert.False(ForNewerApworld.Fits);
        Assert.True(ForThisApworld.Fits);
        Assert.False(Outdated.Fits);
    }

    [Fact]
    public void The_newest_fitting_release_wins_over_a_newer_one_that_doesnt()
    {
        Assert.Same(ForThisApworld, PackReleasePick.Best(new[] { ForNewerApworld, ForThisApworld }));
        Assert.Same(ForThisApworld, PackReleasePick.Best(new[] { ForThisApworld, ForNewerApworld }));
    }

    [Fact]
    public void With_none_fitting_the_best_linked_is_taken_and_the_newer_on_a_tie()
    {
        var older = new PackReleaseFit("v4.0", 444, 400, 437, 129);
        Assert.Same(Outdated, PackReleasePick.Best(new[] { Outdated, older }));
        var worse = new PackReleaseFit("v4.0", 444, 300, 437, 129);
        Assert.Same(Outdated, PackReleasePick.Best(new[] { worse, Outdated }));
        Assert.Null(PackReleasePick.Best(Array.Empty<PackReleaseFit>()));
    }

    [Fact]
    public void The_report_line_names_what_was_taken_and_what_else_was_tried()
    {
        string line = PackReleasePick.Describe(new[] { ForNewerApworld, ForThisApworld }, ForThisApworld);
        Assert.Equal("took v1.0.6: 98% of its pins link, 0 stale ids; also tried v2.0.1: 88% of its pins link, 310 stale ids", line);
        Assert.Equal("took v4.1.1: 90% of its pins link, 129 stale ids", PackReleasePick.Describe(new[] { Outdated }, Outdated));
    }

    [Fact]
    public void Of_several_projects_the_first_that_fits_is_taken_though_another_has_more_stars()
    {
        // Dark Souls III: the original pack (more stars, tried first) is for the 2.x apworld; the fork kept up fits.
        var original = new PackReleaseFit("v4.1.1", 1200, 400, 437, 129);
        Assert.Equal(1, PackProjectPick.Choose(new PackReleaseFit?[] { original, ForThisApworld, ForNewerApworld }));
        Assert.Equal(0, PackProjectPick.Choose(new PackReleaseFit?[] { ForThisApworld, null }));
    }

    [Fact]
    public void With_none_fitting_the_closest_is_taken_only_when_half_its_pins_link()
    {
        var half = new PackReleaseFit("a", 100, 60, 100, 10);
        var more = new PackReleaseFit("b", 100, 70, 100, 30);
        var few = new PackReleaseFit("c", 100, 30, 0, 0);
        Assert.Equal(2, PackProjectPick.Choose(new PackReleaseFit?[] { few, half, more }));
        Assert.Equal(-1, PackProjectPick.Choose(new PackReleaseFit?[] { few, null }));
        Assert.Equal(-1, PackProjectPick.Choose(Array.Empty<PackReleaseFit?>()));
        var itemsOnly = new PackReleaseFit("items", 0, 0, 0, 0);
        Assert.Equal(-1, PackProjectPick.Choose(new PackReleaseFit?[] { itemsOnly }));
    }
}
