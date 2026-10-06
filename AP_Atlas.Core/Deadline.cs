using System;
using System.Diagnostics;

namespace AP_Atlas.Core;

/// <summary>
/// When a wait ends (a site's backoff, the next read of a page, the next request to a site), measured with a monotonic
/// clock (Stopwatch), so setting the PC's clock can't stretch or skip it. Measured by the wall clock, a PC that boots a
/// year ahead and is put right minutes later would leave a site alone for a year. A deadline keeps the wall-clock time
/// it was meant for too, only for showing ("tries again after 14:05").
/// </summary>
public readonly record struct Deadline
{
    // A Stopwatch timestamp. 0 (the default): none, passed already; long.MaxValue: never.
    private readonly long _at;

    private Deadline(long at, DateTime shownUtc)
    {
        _at = at;
        ShownUtc = shownUtc;
    }

    /// <summary>When the wait ends by the wall clock as it began: for showing (off by however much the clock was changed since).</summary>
    public DateTime ShownUtc { get; }

    /// <summary>No wait: passed already.</summary>
    public static Deadline None => default;

    /// <summary>A wait that never ends, until it's replaced.</summary>
    public static Deadline Never => new(long.MaxValue, DateTime.MaxValue);

    /// <summary>The wall clock: for showing when a wait ends (and SteadyClock's conversions). Tests move it.</summary>
    internal static Func<DateTime> WallClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>A wait of this long from now (none if it isn't positive; ten years at most).</summary>
    public static Deadline In(TimeSpan wait)
    {
        if (wait <= TimeSpan.Zero) return None;
        if (wait > TimeSpan.FromDays(3650)) wait = TimeSpan.FromDays(3650);
        return new Deadline(Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency), WallClock() + wait);
    }

    /// <summary>Whether the wait is over.</summary>
    public bool Passed => _at != long.MaxValue && Stopwatch.GetTimestamp() >= _at;

    /// <summary>How long is left (zero once it's over; <see cref="TimeSpan.MaxValue"/> for <see cref="Never"/>).</summary>
    public TimeSpan Left => _at == long.MaxValue ? TimeSpan.MaxValue
        : Passed ? TimeSpan.Zero
        : TimeSpan.FromSeconds((double)(_at - Stopwatch.GetTimestamp()) / Stopwatch.Frequency);

    /// <summary>Whether this wait ends later than another (so a shorter one never replaces it).</summary>
    public bool EndsAfter(Deadline other) => _at > other._at;
}
