using AP_Atlas.Core.PopTracker;

namespace AP_Atlas.Core.Tests;

/// <summary>What Atlas says after fixing a pack by itself.</summary>
public sealed class AutoFixWordsTests
{
    private static string Tr(string text) => text;

    [Fact]
    public void The_card_says_what_was_fixed()
    {
        Assert.Equal("Atlas fixed 12 things in Emerald Tracker by itself.", AutoFixWords.Card("Emerald Tracker", 12, Tr));
        Assert.Equal("Atlas fixed 1 thing in X by itself.", AutoFixWords.Card("X", 1, Tr));
    }
}
