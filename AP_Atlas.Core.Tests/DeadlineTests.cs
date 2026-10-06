namespace AP_Atlas.Core.Tests;

// Moves the shared wall clock: these run one at a time, and put it back.
[Collection(nameof(DeadlineTests))]
public class DeadlineTests
{
    [Fact]
    public async Task A_wait_lasts_its_length_whatever_the_wall_clock_does()
    {
        var real = Deadline.WallClock;
        try
        {
            var shortWait = Deadline.In(TimeSpan.FromMilliseconds(300));
            var longWait = Deadline.In(TimeSpan.FromMinutes(5));
            // The clock is put back a year, then forward two: neither wait moves.
            Deadline.WallClock = () => DateTime.UtcNow.AddYears(-1);
            Assert.False(shortWait.Passed);
            Deadline.WallClock = () => DateTime.UtcNow.AddYears(1);
            Assert.False(longWait.Passed);
            Assert.InRange(longWait.Left, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
            await Task.Delay(400, TestContext.Current.CancellationToken);
            Assert.True(shortWait.Passed);
            Assert.Equal(TimeSpan.Zero, shortWait.Left);
            Assert.False(longWait.Passed);
        }
        finally
        {
            Deadline.WallClock = real;
        }
    }

    [Fact]
    public void A_wait_shows_its_end_by_the_wall_clock_as_it_began()
    {
        var real = Deadline.WallClock;
        try
        {
            var then = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            Deadline.WallClock = () => then;
            var wait = Deadline.In(TimeSpan.FromMinutes(30));
            Assert.Equal(then.AddMinutes(30), wait.ShownUtc);
            // Setting the clock afterwards changes neither when it ends nor the time it shows.
            Deadline.WallClock = () => then.AddHours(5);
            Assert.Equal(then.AddMinutes(30), wait.ShownUtc);
            Assert.InRange(wait.Left, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));
        }
        finally
        {
            Deadline.WallClock = real;
        }
    }

    [Fact]
    public void None_has_passed_and_never_never_does()
    {
        Assert.True(Deadline.None.Passed);
        Assert.True(default(Deadline).Passed);
        Assert.True(Deadline.In(TimeSpan.Zero).Passed);
        Assert.False(Deadline.Never.Passed);
        Assert.Equal(TimeSpan.MaxValue, Deadline.Never.Left);
        Assert.True(Deadline.Never.EndsAfter(Deadline.In(TimeSpan.FromHours(6))));
        Assert.True(Deadline.In(TimeSpan.FromMinutes(2)).EndsAfter(Deadline.In(TimeSpan.FromMinutes(1))));
        Assert.False(Deadline.None.EndsAfter(Deadline.In(TimeSpan.FromMinutes(1))));
    }
}
