namespace AP_Atlas.Core.Tests;

/// <summary>A game's setup guide names the tracker pack its author recommends; Atlas reads that link.</summary>
public sealed class GuideLinksTests
{
    private const string Ds3Guide = """
        # Dark Souls III Setup Guide

        [Dark Souls III AP Client]: https://github.com/nex3/Dark-Souls-III-Archipelago-client/releases/latest

        - [Map tracker](https://github.com/TVV1GK/DS3_AP_Maptracker)
        - [PopTracker](https://github.com/black-sliver/PopTracker) runs it.
        - A [guide](https://archipelago.gg/tutorial/Dark%20Souls%20III/setup/en).

        [reported upstream]: https://github.com/thefifthmatt/SoulsRandomizers/issues
        """;

    [Fact]
    public void The_guides_tracker_link_is_the_recommendation_and_the_rest_is_left_out()
    {
        var found = GuideLinks.TrackerProjects(Ds3Guide);
        Assert.Single(found);
        Assert.Equal(("TVV1GK/DS3_AP_Maptracker", "Map tracker"), found[0]);
    }

    [Fact]
    public void A_reference_link_a_bare_address_and_a_tracker_named_in_the_address_count_once_each()
    {
        string guide = """
            See the [pack]: https://github.com/palex00/aquaria-ap-tracker/releases
            Also https://github.com/palex00/aquaria-ap-tracker and https://github.com/Mysteryem/ww-poptracker.
            """;
        var found = GuideLinks.TrackerProjects(guide);
        Assert.Equal(new[] { "palex00/aquaria-ap-tracker", "Mysteryem/ww-poptracker" }, found.Select(f => f.Repo).ToArray());
    }

    [Theory]
    [InlineData("https://github.com/TVV1GK/DS3_AP_Maptracker", "TVV1GK/DS3_AP_Maptracker")]
    [InlineData("https://github.com/TVV1GK/DS3_AP_Maptracker/releases/tag/v1.0.6", "TVV1GK/DS3_AP_Maptracker")]
    [InlineData("https://www.github.com/a/b.git", "a/b")]
    [InlineData("https://github.com/onlyowner", null)]
    [InlineData("https://gitlab.com/a/b", null)]
    [InlineData("", null)]
    public void A_projects_name_comes_from_its_address(string url, string? expected) => Assert.Equal(expected, GuideLinks.RepoOf(url));

    [Fact]
    public void A_guide_without_a_tracker_link_recommends_nothing()
    {
        Assert.Empty(GuideLinks.TrackerProjects("# Setup\n\nInstall the client from https://github.com/some/client/releases."));
        Assert.Empty(GuideLinks.TrackerProjects(null));
    }
}
