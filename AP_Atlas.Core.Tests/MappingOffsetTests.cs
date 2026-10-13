using AP_Atlas.Core.PopTracker;

namespace AP_Atlas.Core.Tests;

/// <summary>A mapping keyed by offsets from the game's ids is read with the base its script adds.</summary>
public sealed class MappingOffsetTests
{
    private static readonly long[] Game = { 3736072255, 3736072256, 3736072257, 3736072258, 3736072259, 3736072260 };

    [Fact]
    public void The_base_that_puts_every_key_on_the_games_ids_is_found()
    {
        long? by = MappingOffset.Infer(new long[] { 0, 1, 2, 3, 4, 5 }, Game, out int hits);
        Assert.Equal(3736072255, by);
        Assert.Equal(6, hits);
    }

    [Fact]
    public void A_base_may_be_negative_and_may_leave_a_tenth_of_the_keys_unplaced()
    {
        // Keys above the game's ids (the pack kept an older numbering), one of ten with no id behind it.
        var keys = new long[] { 1010, 1011, 1012, 1013, 1014, 1015, 1016, 1017, 1018, 1900 };
        var game = new long[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };
        long? by = MappingOffset.Infer(keys, game, out int hits);
        Assert.Equal(-1000, by);
        Assert.Equal(9, hits);
    }

    [Fact]
    public void Keys_that_are_the_games_ids_already_have_no_base()
    {
        // Four of six known: a couple of stale lines, not an offset.
        Assert.Null(MappingOffset.Infer(new long[] { 3736072255, 3736072256, 3736072257, 3736072258, 1, 2 }, Game, out _));
    }

    [Fact]
    public void Too_few_keys_or_a_poor_fit_give_no_base()
    {
        Assert.Null(MappingOffset.Infer(new long[] { 0, 1, 2 }, Game, out _));
        // Only three of six land anywhere with any base.
        Assert.Null(MappingOffset.Infer(new long[] { 0, 1, 2, 500, 900, 1300 }, Game, out _));
        Assert.Null(MappingOffset.Infer(new long[] { 0, 1, 2, 3 }, new long[0], out _));
    }
}
