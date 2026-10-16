using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.Tests;

/// <summary>One rule for game names across a pack's manifest, Atlas's game list and the engine (round 12: FireRed's pack).</summary>
public sealed class GameNameRuleTests
{
    [Theory]
    [InlineData("Pokémon FireRed and LeafGreen", "Pokemon FireRed and LeafGreen")]
    [InlineData("Pokémon FireRed/LeafGreen", "Pokemon FireRed and LeafGreen")]
    [InlineData("Mario & Luigi: Superstar Saga", "Mario & Luigi Superstar Saga")]
    [InlineData("Mario and Luigi Superstar Saga", "Mario & Luigi Superstar Saga")]
    [InlineData("Scooby Doo Night of 100 Frights", "Scooby-Doo! Night of 100 Frights")]
    [InlineData("The Legend of Zelda: Oracle of Seasons", "The Legend of Zelda - Oracle of Seasons")]
    [InlineData("Golden Sun: The Lost Age", "Golden Sun The Lost Age")]
    [InlineData("Dark Souls 2", "Dark Souls II")]
    public void Spellings_of_one_game_are_the_same(string a, string b) => Assert.True(GameNameRule.Same(a, b));

    [Theory]
    [InlineData("Dark Souls III", "Dark Souls II")]
    [InlineData("Archipela-Go!", "Archipelago")]
    [InlineData("", "")]
    [InlineData(null, "Anything")]
    public void Different_games_and_nothing_are_not(string? a, string b) => Assert.False(GameNameRule.Same(a, b));

    [Fact]
    public void A_word_holding_and_keeps_it_and_words_stay_apart()
    {
        Assert.Equal("sandopolis zone", GameNameRule.Key("Sandopolis Zone"));
        Assert.Equal("pokemon firered leafgreen", GameNameRule.Key("Pokémon FireRed/LeafGreen"));
        Assert.Equal("", GameNameRule.Key("  "));
    }

    [Fact]
    public void A_pack_may_name_its_game_after_a_colon_but_a_display_name_never_does()
    {
        Assert.True(GameNameRule.PackIsFor("Zelda: Oracle of Seasons", "Oracle of Seasons"));
        Assert.Null(GameNameRule.Resolve("Manual: Grapple Dog", ["Grapple Dog"]));
        Assert.Equal("Grapple Dog", GameNameRule.ResolvePack("Manual: Grapple Dog", ["Grapple Dog"]));
    }

    [Fact]
    public void Exact_text_wins_and_two_games_that_read_the_same_are_no_match()
    {
        Assert.Equal("Archipelago", GameNameRule.Resolve("archipelago", ["Archipela-Go!", "Archipelago"]));
        Assert.Equal("Pokemon FireRed and LeafGreen", GameNameRule.ResolvePack("Pokémon FireRed and LeafGreen", ["Pokemon Emerald", "Pokemon FireRed and LeafGreen"]));
        Assert.Null(GameNameRule.ResolvePack("Pokémon X", ["Pokemon X", "Pokemon-X"]));
        Assert.Equal("Pokemon X", GameNameRule.ResolvePack("Pokemon X", ["Pokemon-X", "Pokemon X"]));
        Assert.Equal("Archipelago", GameNameRule.Resolve("archipelago", ["Archipelago!", "Archipelago"]));
    }
}
