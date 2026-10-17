using System;
using System.Collections.Generic;

namespace AP_Atlas.Core.EngineSetup;

/// <summary>
/// The engine's versions against the ones Atlas pins: whether a portable engine's Archipelago needs updating, whether a
/// built core is the one the pins name, and whether the game worlds Atlas unpacks fit the core they load on.
/// </summary>
public static class EngineVersions
{
    /// <summary>Whether an installed Archipelago isn't the pinned one (older or newer); false while none is installed.</summary>
    public static bool NeedsArchipelagoUpdate(string? installed, string pinned) =>
        !string.IsNullOrEmpty(installed) && !string.Equals(installed, pinned, StringComparison.Ordinal);

    /// <summary>What a core's stamp says that the pins don't, in one plain line, or null when it's the pinned core.</summary>
    public static string? CoreStampProblem(string? stampArchipelago, string pinnedArchipelago, string? stampTracker, string pinnedTracker)
    {
        var parts = new List<string>();
        if (!string.Equals(stampArchipelago, pinnedArchipelago, StringComparison.Ordinal))
            parts.Add($"the core has Archipelago {Named(stampArchipelago)}; Atlas pins {pinnedArchipelago}");
        if (!string.Equals(stampTracker, pinnedTracker, StringComparison.Ordinal))
            parts.Add($"the core has Universal Tracker {Named(stampTracker)}; Atlas pins {pinnedTracker}");
        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// Whether game worlds from one Archipelago's release (the worlds index's) may load on a core: the same version, or a
    /// core that doesn't say which it is.
    /// </summary>
    public static bool WorldsFitCore(string? coreArchipelago, string indexArchipelago) =>
        string.IsNullOrEmpty(coreArchipelago) || string.Equals(coreArchipelago, indexArchipelago, StringComparison.Ordinal);

    private static string Named(string? version) => string.IsNullOrEmpty(version) ? "(none named)" : version;
}
