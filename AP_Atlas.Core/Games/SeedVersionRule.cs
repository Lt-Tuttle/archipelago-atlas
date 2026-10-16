using System;
using System.Collections.Generic;

namespace AP_Atlas.Core.Games;

/// <summary>How far a running slot's logic can be trusted, as Atlas can check it (every view's warning comes from this).</summary>
public enum LogicAccuracy
{
    /// <summary>Nothing Atlas can check says otherwise.</summary>
    AsChecked,

    /// <summary>The seed was made with another apworld version, and logic doesn't work with the one that runs: the seed's version is wanted.</summary>
    SeedVersion,

    /// <summary>The rebuilt world differs from the server's for another reason (usually options the server's data lacks: the YAML).</summary>
    RebuildDiffers
}

/// <summary>
/// The connect-time version rule: a slot runs the newest installed version of its game's apworld, and Atlas looks for the
/// version its seed was made with only when the seed differs (the data checksums differ) AND logic doesn't work: the start
/// failed rebuilding the world (generation_failed, or yaml_needed), the rebuilt world's locations don't match the
/// server's, or Atlas's copy of the seed's version couldn't be loaded. A different version whose rebuild matches runs as
/// it is. The trust check (whether the world rebuilds the same with other seeds) is not a version signal: an unsure world
/// is unsure in every version, and has its own checklist banner.
/// </summary>
public static class SeedVersionRule
{
    // The start failures a version can cause: the world couldn't be rebuilt.
    private static readonly HashSet<string> RebuildFailures = new(StringComparer.Ordinal) { "generation_failed", "yaml_needed" };

    /// <summary>Logic doesn't work with the apworld that ran.</summary>
    /// <param name="startError">Why the start failed (null: it started).</param>
    /// <param name="rebuildMatch">Whether the rebuilt world's locations match the server's (null: not compared).</param>
    /// <param name="seedApworldFailed">Atlas's copy of the seed's version was given to the engine and couldn't be loaded.</param>
    public static bool LogicFails(string? startError, bool? rebuildMatch, bool seedApworldFailed) =>
        (startError != null && RebuildFailures.Contains(startError)) || rebuildMatch == false || seedApworldFailed;

    /// <summary>Whether Atlas should look for the seed's version: the seed differs and logic doesn't work.</summary>
    public static bool WantsSeedVersion(bool? apworldMatchesSeed, string? startError, bool? rebuildMatch, bool seedApworldFailed) =>
        apworldMatchesSeed == false && LogicFails(startError, rebuildMatch, seedApworldFailed);

    /// <summary>The one classification every view warns from (a slot whose logic isn't running has its problem instead).</summary>
    public static LogicAccuracy Classify(bool running, bool? apworldMatchesSeed, bool? rebuildMatch, bool seedApworldFailed) =>
        !running ? LogicAccuracy.AsChecked
        : WantsSeedVersion(apworldMatchesSeed, null, rebuildMatch, seedApworldFailed) ? LogicAccuracy.SeedVersion
        : rebuildMatch == false ? LogicAccuracy.RebuildDiffers
        : LogicAccuracy.AsChecked;
}
