namespace AP_Atlas.Core.Tests;

// Moves the shared wall clock (Deadline.WallClock): runs one at a time with DeadlineTests, and puts it back.
[Collection(nameof(DeadlineTests))]
public class SteadyClockTests
{
    [Fact]
    public async Task It_moves_forward_steadily_whatever_the_wall_clock_does()
    {
        var real = Deadline.WallClock;
        try
        {
            var before = SteadyClock.UtcNow;
            Deadline.WallClock = () => DateTime.UtcNow.AddYears(-1);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Deadline.WallClock = () => DateTime.UtcNow.AddYears(1);
            Assert.InRange(SteadyClock.UtcNow - before, TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(10));
            // It began at the PC's clock: while nobody sets that, the two agree.
            Assert.InRange(SteadyClock.UtcNow - DateTime.UtcNow, TimeSpan.FromMinutes(-1), TimeSpan.FromMinutes(1));
        }
        finally
        {
            Deadline.WallClock = real;
        }
    }

    [Fact]
    public void A_saved_time_is_placed_by_how_long_ago_the_wall_clock_says_it_was()
    {
        var real = Deadline.WallClock;
        try
        {
            var then = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            Deadline.WallClock = () => then;
            var now = SteadyClock.UtcNow;
            Assert.InRange(SteadyClock.FromSaved(then.AddMinutes(-10))!.Value - (now - TimeSpan.FromMinutes(10)), TimeSpan.Zero, TimeSpan.FromSeconds(5));
            // A little ahead (a small correction since) is now; far ahead (the clock was put back since) isn't known.
            Assert.InRange(SteadyClock.FromSaved(then.AddMinutes(2))!.Value - now, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            Assert.Null(SteadyClock.FromSaved(then.AddHours(1)));
            // A damaged file's time can be anything: never an exception.
            Assert.Null(SteadyClock.FromSaved(DateTime.MaxValue));
            Deadline.WallClock = () => new DateTime(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(DateTime.MinValue, SteadyClock.FromSaved(DateTime.MinValue));
        }
        finally
        {
            Deadline.WallClock = real;
        }
    }

    [Fact]
    public void A_steady_time_is_shown_by_the_wall_clock_as_it_is_now()
    {
        var real = Deadline.WallClock;
        try
        {
            // The PC's clock was put right since Atlas started (it's 12:00 now): ten minutes ago shows as 11:50.
            var then = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            var tenMinutesAgo = SteadyClock.UtcNow - TimeSpan.FromMinutes(10);
            Deadline.WallClock = () => then;
            Assert.InRange(SteadyClock.Shown(tenMinutesAgo) - then.AddMinutes(-10), TimeSpan.FromSeconds(-5), TimeSpan.Zero);
            // Any time at all can be shown, without an exception.
            Deadline.WallClock = () => new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(DateTime.MinValue, SteadyClock.Shown(DateTime.MinValue));
            Assert.True(SteadyClock.Shown(DateTime.MaxValue) > DateTime.UtcNow.AddYears(1000));
        }
        finally
        {
            Deadline.WallClock = real;
        }
    }
}
