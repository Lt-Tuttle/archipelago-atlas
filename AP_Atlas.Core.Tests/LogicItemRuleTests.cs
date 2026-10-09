using AP_Atlas.Core;

namespace AP_Atlas.Core.Tests;

public class LogicItemRuleTests
{
    [Theory]
    [InlineData(true, false, false, 2000, true)]   // progression by the server's flags
    [InlineData(false, true, false, 2000, true)]   // "never exclude" counts as progression
    [InlineData(false, false, true, 2000, true)]   // progression by the world's pool (a /send gives no flags)
    [InlineData(false, false, false, 2000, false)] // filler from a location
    [InlineData(false, false, false, -2, true)]    // a start inventory item: no flags, not in the pool, location -2
    [InlineData(false, false, false, -1, true)]    // a server command's gift
    public void A_start_inventory_counts_without_flags_or_a_pool_entry(bool advancement, bool neverExclude, bool inPool, long location, bool expected)
    {
        Assert.Equal(expected, LogicItemRule.Counts(advancement, neverExclude, inPool, location));
    }
}
