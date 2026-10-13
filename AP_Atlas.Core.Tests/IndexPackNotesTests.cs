using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.Tests;

/// <summary>The pack lab's notes about a pack, said in words, and which notes make a pack wait behind the others.</summary>
public sealed class IndexPackNotesTests
{
    [Fact]
    public void Each_note_is_a_sentence_in_a_fixed_order()
    {
        var said = IndexPackNotes.Sentences(new[] { "no_pictures", "other_version", "scripts_stop" });
        Assert.Equal(3, said.Count);
        Assert.StartsWith("Made for another version", said[0]);
        Assert.StartsWith("Its scripts stop", said[1]);
        Assert.StartsWith("Some of its maps", said[2]);
    }

    [Fact]
    public void A_pack_for_another_game_names_it_either_way()
    {
        Assert.Equal("Its checks are Celeste (Open World)'s, not this game's.", IndexPackNotes.Sentences(new[] { "other_game" }, forGame: "Celeste (Open World)").Single());
        Assert.Equal("Its manifest names Celeste, but its checks are this game's.", IndexPackNotes.Sentences(null, namedGame: "Celeste").Single());
    }

    [Fact]
    public void Codes_this_Atlas_does_not_know_are_left_out()
    {
        Assert.Empty(IndexPackNotes.Sentences(new[] { "something_newer" }));
        Assert.Empty(IndexPackNotes.Sentences(null));
    }

    [Theory]
    [InlineData("other_version", true)]
    [InlineData("other_game", true)]
    [InlineData("scripts_stop", true)]
    [InlineData("no_pictures", false)]
    [InlineData("something_newer", false)]
    public void Only_a_pack_that_would_track_badly_waits_behind(string note, bool waits) =>
        Assert.Equal(waits, IndexPackNotes.WaitsBehind(new[] { note }));
}
