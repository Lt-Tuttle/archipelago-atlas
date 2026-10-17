using AP_Atlas.Core.EngineSetup;

namespace AP_Atlas.Core.Tests;

/// <summary>The engine's versions against Atlas's pins: a portable engine's update, a built core's stamp, and which worlds fit a core.</summary>
public sealed class EngineVersionsTests
{
    [Theory]
    [InlineData("0.6.7", "0.6.7", false)]
    [InlineData("0.6.7", "0.6.8", true)] // older: updated
    [InlineData("0.6.9", "0.6.8", true)] // newer: taken back to the pin
    [InlineData("0.6.10", "0.6.1", true)]
    [InlineData(null, "0.6.8", false)] // nothing installed yet: that's setup, not an update
    [InlineData("", "0.6.8", false)]
    public void An_archipelago_other_than_the_pin_needs_updating(string? installed, string pinned, bool needs) =>
        Assert.Equal(needs, EngineVersions.NeedsArchipelagoUpdate(installed, pinned));

    [Fact]
    public void A_core_stamp_is_checked_part_by_part()
    {
        Assert.Null(EngineVersions.CoreStampProblem("0.6.8", "0.6.8", "v0.3.4", "v0.3.4"));

        string? archipelago = EngineVersions.CoreStampProblem("0.6.7", "0.6.8", "v0.3.4", "v0.3.4");
        Assert.Equal("the core has Archipelago 0.6.7; Atlas pins 0.6.8.", archipelago);

        string? tracker = EngineVersions.CoreStampProblem("0.6.8", "0.6.8", "v0.3.3", "v0.3.4");
        Assert.Equal("the core has Universal Tracker v0.3.3; Atlas pins v0.3.4.", tracker);

        string? both = EngineVersions.CoreStampProblem("0.6.9", "0.6.8", "v0.3.5", "v0.3.4");
        Assert.NotNull(both);
        Assert.Contains("Archipelago 0.6.9", both);
        Assert.Contains("Universal Tracker v0.3.5", both);
        Assert.DoesNotContain("\n", both);

        Assert.Equal("the core has Archipelago (none named); Atlas pins 0.6.8.", EngineVersions.CoreStampProblem(null, "0.6.8", "v0.3.4", "v0.3.4"));
        Assert.Equal("the core has Universal Tracker (none named); Atlas pins v0.3.4.", EngineVersions.CoreStampProblem("0.6.8", "0.6.8", "", "v0.3.4"));
    }

    [Theory]
    [InlineData("0.6.8", "0.6.8", true)]
    [InlineData("0.6.7", "0.6.8", false)] // an engine still on the older core: its worlds wait for the update
    [InlineData("0.6.9", "0.6.8", false)]
    [InlineData(null, "0.6.8", true)] // a core that doesn't say: nothing to hold back
    [InlineData("", "0.6.8", true)]
    [InlineData("0.6.8 ", "0.6.8", false)] // compared exactly
    public void Worlds_fit_only_the_core_of_their_own_archipelago(string? core, string index, bool fits) =>
        Assert.Equal(fits, EngineVersions.WorldsFitCore(core, index));
}
