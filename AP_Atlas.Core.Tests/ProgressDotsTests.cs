namespace AP_Atlas.Core.Tests;

/// <summary>The busy dots beside a bar: on at a step, off while the bar moves within it.</summary>
public sealed class ProgressDotsTests
{
    [Fact]
    public void A_steps_start_reported_again_is_not_movement()
    {
        // Round 13's review: "Step 3 of 3: Running the health check…" starts at 1/10 = 0.1f, then the phase reports 0.1f again.
        var dots = new ProgressDots();
        Assert.True(dots.Step("Running the health check…", 1f / 10f));
        Assert.True(dots.Bar(1f / 10f));
        Assert.True(dots.Step(null, 1f / 10f));
        // A full setup's phases: 4/43, 32/43 and 40/43 aren't three-decimal numbers either.
        foreach (float start in new[] { 4f / 43f, 32f / 43f, 40f / 43f })
        {
            Assert.True(dots.Step("next phase", start));
            Assert.True(dots.Bar(start));
        }
        Assert.Equal(40f / 43f, dots.Shown, 6);
    }

    [Fact]
    public void A_download_moving_the_bar_stops_them_and_the_next_step_brings_them_back()
    {
        var dots = new ProgressDots();
        Assert.True(dots.Step("Downloading…", 0.2));
        Assert.False(dots.Bar(0.25));
        Assert.False(dots.Bar(0.2)); // the bar never goes back
        Assert.True(dots.Step("Checking…", 0.3));
        Assert.False(dots.Step(null, 0.35)); // a text-less report that moves the bar is movement too
        Assert.True(dots.Step(null, -1));
        dots.Reset();
        Assert.Equal((true, 0d), (dots.Busy, dots.Shown));
    }
}
