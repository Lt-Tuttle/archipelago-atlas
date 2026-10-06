namespace AP_Atlas.Core.Tests;

public class WordSearchTests
{
    [Theory]
    [InlineData("Theme Accent Color", "Theme|Accent|Color")]
    [InlineData("Privacy & Permissions…", "Privacy|Permissions")]
    [InlineData("Reconnect dropped connections automatically (a few tries, then it stops):", "Reconnect|dropped|connections|automatically|a|few|tries|then|it|stops")]
    [InlineData("view.slots-panel", "view|slots|panel")]
    [InlineData("Slot  ·  Multiworld", "Slot|Multiworld")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void Words_are_split_at_spaces_and_the_punctuation_that_joins_them(string? text, string expected) =>
        Assert.Equal(expected, string.Join("|", WordSearch.Words(text)));

    [Theory]
    [InlineData("", true)] // nothing typed matches everything
    [InlineData("rec", true)] // the start of a word
    [InlineData("RECONNECT drop", true)] // every typed word, in any case
    [InlineData("auto multi", true)] // words from different texts: the title and the section
    [InlineData("minutes connections", true)] // the description too
    [InlineData("onnect", false)] // not from the start of a word
    [InlineData("rec zzz", false)] // every typed word must match
    public void Every_typed_word_must_start_a_word_of_one_of_the_texts(string typed, bool matches) =>
        Assert.Equal(matches, WordSearch.Matches(typed, "Reconnect dropped connections automatically", "A few tries over about 20 minutes.", "Multiworld"));

    [Fact]
    public void Missing_texts_are_skipped() => Assert.True(WordSearch.Matches("one", null, "", "One two"));
}
