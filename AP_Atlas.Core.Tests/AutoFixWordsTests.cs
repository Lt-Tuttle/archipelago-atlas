using AP_Atlas.Core.PopTracker;

namespace AP_Atlas.Core.Tests;

/// <summary>What Atlas says after fixing a pack by itself.</summary>
public sealed class AutoFixWordsTests
{
    private static string Tr(string text) => text;

    [Fact]
    public void The_card_says_what_was_fixed_and_what_is_left()
    {
        Assert.Equal("Atlas fixed 12 things in Emerald Tracker by itself. 3 are left for you on the map's Notices.", AutoFixWords.Card("Emerald Tracker", 12, 3, Tr));
        Assert.Equal("Atlas fixed 1 thing in X by itself.", AutoFixWords.Card("X", 1, 0, Tr));
    }

    [Fact]
    public void Add_games_says_it_per_pack()
    {
        Assert.Equal("Pack (Game): Atlas fixed 2 things by itself. 1 is left for you on the map's Notices.", AutoFixWords.Summary("Pack", "Game", 2, 1, Tr));
        Assert.Equal("Pack (Game): nothing Atlas could fix by itself. 4 are left for you on the map's Notices.", AutoFixWords.Summary("Pack", "Game", 0, 4, Tr));
    }
}
