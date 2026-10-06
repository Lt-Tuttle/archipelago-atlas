using System;
using System.Diagnostics;

namespace AP_Atlas.Core;

/// <summary>
/// The time now by a clock that only moves forward, at a steady pace: the PC's clock when Atlas started, plus the time
/// since (Stopwatch). For timing what happens in a session (how long a suggestion has held, how many changes were made
/// in the last day, when a page was last read), so setting the PC's clock can't shorten or stretch any of it: by the
/// wall clock, a PC that boots a day ahead and is put right minutes later would lift a daily limit at once.
/// Its times stay in memory. A time that's saved, sent to a site or shown is the wall clock's (DateTime.UtcNow);
/// <see cref="FromSaved"/> and <see cref="Shown"/> convert between the two. A wait that ends is a <see cref="Deadline"/>.
/// </summary>
public static class SteadyClock
{
    private static readonly long Started = Stopwatch.GetTimestamp();
    private static readonly DateTime StartedUtc = DateTime.UtcNow;

    /// <summary>Now, on the steady clock.</summary>
    public static DateTime UtcNow => StartedUtc + Stopwatch.GetElapsedTime(Started);

    /// <summary>
    /// A time saved in an earlier session (the wall clock's), on the steady clock: now, less how long ago the wall clock
    /// says it was. Null when it's more than five minutes ahead of the wall clock: the PC's clock was put back since, so
    /// how long ago it was isn't known. One a little ahead (a small correction) is taken as now.
    /// </summary>
    public static DateTime? FromSaved(DateTime savedUtc)
    {
        var ago = Deadline.WallClock() - savedUtc;
        if (ago < -TimeSpan.FromMinutes(5)) return null;
        return ago > TimeSpan.Zero ? Minus(UtcNow, ago) : UtcNow;
    }

    /// <summary>A time on the steady clock as the wall clock shows it now (for a time of day): the wall clock's now, less how long ago it was.</summary>
    public static DateTime Shown(DateTime steadyUtc) => Minus(Deadline.WallClock(), UtcNow - steadyUtc);

    // A time less a span, kept within DateTime's range (a damaged saved time can be anything).
    private static DateTime Minus(DateTime time, TimeSpan span) =>
        span.Ticks >= 0
            ? span.Ticks <= time.Ticks ? time - span : DateTime.MinValue
            : -span.Ticks <= DateTime.MaxValue.Ticks - time.Ticks ? time - span : DateTime.MaxValue;
}
