#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core;

/// <summary>
/// What to do when another Atlas holds this data folder: an Atlas whose window is gone but whose process lives on (it
/// didn't finish closing) keeps the folder's lock, and the next start could only say "already open" with no Atlas in
/// sight. Pure, so the rule is tested without processes.
/// </summary>
public static class InstanceCheck
{
    /// <summary>An Atlas started from the same program file as this one.</summary>
    public readonly record struct Twin(int Id, bool HasWindow, TimeSpan Age);

    /// <summary>
    /// A process still starting has no window yet: one younger than this isn't taken for a leftover (two quick double
    /// clicks must not offer to end the first).
    /// </summary>
    public static readonly TimeSpan StartingGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The processes it's safe to offer to end: every other Atlas from this program file, when none of them has a window
    /// and all are past <see cref="StartingGrace"/>. Empty when one shows a window (Atlas is open: nothing to end) or there
    /// are none.
    /// </summary>
    public static IReadOnlyList<int> LeftoversToEnd(IEnumerable<Twin> twins)
    {
        var list = twins.ToList();
        if (list.Count == 0 || list.Any(t => t.HasWindow || t.Age < StartingGrace)) return Array.Empty<int>();
        return list.Select(t => t.Id).ToList();
    }
}
