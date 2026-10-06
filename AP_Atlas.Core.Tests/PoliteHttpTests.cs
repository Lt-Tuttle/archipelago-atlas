namespace AP_Atlas.Core.Tests;

// Reads wait times, which DeadlineTests and SteadyClockTests move the wall clock under: runs one at a time with them.
[Collection(nameof(DeadlineTests))]
public class PoliteHttpTests
{
    [Fact]
    public async Task A_sites_wait_and_its_reason_are_read_together()
    {
        // One thread keeps switching a site between a short wait and a long one, each with its own reason; another reads
        // what the window would show. A half-changed read would pair one wait's time with the other's reason.
        const string site = "https://wait-state.test";
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                PoliteHttp.WaitForTests(site, TimeSpan.FromMinutes(1), "short");
                PoliteHttp.WaitForTests(site, TimeSpan.FromHours(5), "long");
            }
        }, TestContext.Current.CancellationToken);
        int reads = 0, mismatched = 0;
        while (!stop.IsCancellationRequested)
        {
            if (PoliteHttp.WaitingFor(site) is not { } wait) continue;
            reads++;
            bool longWait = wait.UntilUtc - DateTime.UtcNow > TimeSpan.FromHours(1);
            if (longWait != (wait.Reason == "long")) mismatched++;
        }
        await writer;
        PoliteHttp.StopWaiting(site);
        Assert.True(reads > 1000, $"only {reads} reads");
        Assert.Equal(0, mismatched);
        Assert.Null(PoliteHttp.WaitingFor(site));
    }
}
