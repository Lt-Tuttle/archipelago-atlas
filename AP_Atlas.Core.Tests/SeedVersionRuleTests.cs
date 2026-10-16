using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// The connect-time version rule: the newest installed version runs, and the seed's version is looked for only when the
/// seed differs and logic doesn't work. Every view warns from one classification.
/// </summary>
public class SeedVersionRuleTests
{
    [Theory]
    // The seed differs but logic works: the installed version runs.
    [InlineData(false, null, true, false, false)]
    // The seed differs and the rebuilt world doesn't match the server's.
    [InlineData(false, null, false, false, true)]
    // The seed differs and the world couldn't be rebuilt.
    [InlineData(false, "generation_failed", null, false, true)]
    [InlineData(false, "yaml_needed", null, false, true)]
    // The seed differs and Atlas's copy of the seed's version couldn't be loaded.
    [InlineData(false, null, true, true, true)]
    // Failures no version causes.
    [InlineData(false, "world_missing", null, false, false)]
    [InlineData(false, "crashed", null, false, false)]
    [InlineData(false, "no_response", null, false, false)]
    [InlineData(false, "restarting", null, false, false)]
    // The same version, or not known: never a version problem, whatever logic does.
    [InlineData(true, null, false, false, false)]
    [InlineData(true, "generation_failed", null, true, false)]
    [InlineData(null, "generation_failed", false, true, false)]
    // The seed differs but nothing says logic doesn't work (no locations compared).
    [InlineData(false, null, null, false, false)]
    public void The_seeds_version_is_wanted_only_when_the_seed_differs_and_logic_does_not_work(bool? matchesSeed, string? startError, bool? rebuildMatch, bool seedCopyFailed, bool wanted) =>
        Assert.Equal(wanted, SeedVersionRule.WantsSeedVersion(matchesSeed, startError, rebuildMatch, seedCopyFailed));

    [Theory]
    [InlineData(false, false, false, false, LogicAccuracy.AsChecked)]   // not running: the problem says it
    [InlineData(true, false, true, false, LogicAccuracy.AsChecked)]     // another version, matching: no warning
    [InlineData(true, false, false, false, LogicAccuracy.SeedVersion)]
    [InlineData(true, false, true, true, LogicAccuracy.SeedVersion)]
    [InlineData(true, true, false, false, LogicAccuracy.RebuildDiffers)] // the same version, a world that differs: the YAML
    [InlineData(true, null, false, false, LogicAccuracy.RebuildDiffers)]
    [InlineData(true, true, true, false, LogicAccuracy.AsChecked)]
    public void Every_view_warns_from_one_classification(bool running, bool? matchesSeed, bool? rebuildMatch, bool seedCopyFailed, LogicAccuracy expected) =>
        Assert.Equal(expected, SeedVersionRule.Classify(running, matchesSeed, rebuildMatch, seedCopyFailed));
}
