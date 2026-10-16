using System;

namespace AP_Atlas.Core;

/// <summary>
/// Whether busy dots show beside a bar (round 13): a step begins, or says where it is, with them; the bar moving within the
/// step (a download) stops them. It keeps the raw high-water fraction, so a step's start reported again isn't taken for
/// movement (round 13's review: compared with the bar's rounded value, a health check starting at 0.1f looked like movement
/// past 0.1, and its dots went out for the whole check).
/// </summary>
public sealed class ProgressDots
{
    private double _shown;

    /// <summary>The furthest the bar has been (0 to 1).</summary>
    public double Shown => _shown;

    /// <summary>Whether the dots show.</summary>
    public bool Busy { get; private set; } = true;

    /// <summary>A run starts: the bar at 0, the dots on.</summary>
    public void Reset()
    {
        _shown = 0;
        Busy = true;
    }

    /// <summary>A step's report: its text (null when it names none) and the bar (negative: none). Returns <see cref="Busy"/>.</summary>
    public bool Step(string? text, double fraction)
    {
        Busy = text != null || !(fraction > _shown);
        if (fraction >= 0) _shown = Math.Max(_shown, fraction);
        return Busy;
    }

    /// <summary>The bar alone moved (a download's progress, negative: none). Returns <see cref="Busy"/>.</summary>
    public bool Bar(double fraction)
    {
        if (fraction > _shown) Busy = false;
        if (fraction >= 0) _shown = Math.Max(_shown, fraction);
        return Busy;
    }
}
