using AP_Atlas.Core.PopTracker;

namespace AP_Atlas.Core.Tests;

/// <summary>The Changes tab's and Review fixes' lines.</summary>
public sealed class PackChangeWordsTests
{
    private static string Tr(string text) => text;

    [Fact]
    public void A_link_names_the_pin_its_section_and_the_check()
    {
        Assert.Equal("Cave / Chest shows Cave Chest Room", PackChangeWords.Link("Cave", "Chest", "Cave Chest Room", Tr));
        Assert.Equal("Shrine shows Bell", PackChangeWords.Link("Shrine", "", "Bell", Tr));
        Assert.Equal("Shrine / Marker: not a check", PackChangeWords.Link("Shrine", "Marker", null, Tr));
    }

    [Fact]
    public void Tiles_grids_pins_and_maps()
    {
        Assert.Equal("Tile Hookshot: shows Hookshot", PackChangeWords.Tile("Hookshot", "Hookshot", false, false, Tr));
        Assert.Equal("Tile Sword: hidden, your picture", PackChangeWords.Tile("Sword", null, true, true, Tr));
        Assert.Equal("Tile added for Gem", PackChangeWords.AddedTile("Gem", Tr));
        Assert.Equal("Grid Items: hidden, tile size 48, reordered", PackChangeWords.Grid("Items", true, 48, true, Tr));
        Assert.Equal("Pin Andre moved on Firelink Shrine", PackChangeWords.PinMoved(PackChangeWords.PinName("Firelink/Andre"), "Firelink Shrine", Tr));
        Assert.Equal("Pin Andre removed from Firelink Shrine", PackChangeWords.PinRemoved("Andre", "Firelink Shrine", Tr));
        Assert.Equal("Pin Lantern added on World (2 checks)", PackChangeWords.PinAdded("Lantern", "World", 2, Tr));
        Assert.Equal("Map World: your picture", PackChangeWords.MapPicture("World", Tr));
    }

    [Fact]
    public void A_hidden_check_and_why_Atlas_was_sure()
    {
        Assert.Equal("Tower Top hidden: left out of the Logic Tracker, the map and the in-logic counts", PackChangeWords.Hidden("Tower Top", Tr));
        Assert.Equal("Atlas did this: the names match.", PackChangeWords.ByAtlas(null, Tr));
        Assert.Equal("Atlas did this: its name matches 88%.", PackChangeWords.ByAtlas("its name matches 88%", Tr));
    }

    [Fact]
    public void Fix_what_Atlas_can_says_its_real_score_never_that_the_names_match()
    {
        Assert.Equal("its name matches 88% (Fix what Atlas can)", PackChangeWords.SuggestedReason(0.876));
        Assert.Equal("Atlas did this: its name matches 88% (Fix what Atlas can).", PackChangeWords.ByAtlas(PackChangeWords.WhyAtlas(PackChangeWords.SuggestedReason(0.88), "suggested"), Tr));
        Assert.Equal("Atlas did this: it took the closest name (Fix what Atlas can).", PackChangeWords.ByAtlas(PackChangeWords.WhyAtlas(null, "suggested"), Tr));
        Assert.Null(PackChangeWords.WhyAtlas(null, "automatic"));
        Assert.Equal("the only 'Bell' in the game", PackChangeWords.WhyAtlas("the only 'Bell' in the game", "automatic"));
    }
}
