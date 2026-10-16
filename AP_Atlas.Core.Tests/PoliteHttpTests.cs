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

    [Fact]
    public async Task A_cancelled_download_leaves_no_partial_file()
    {
        var spacing = PoliteHttp.Spacing;
        PoliteHttp.Spacing = TimeSpan.Zero;
        try
        {
            byte[] big = new byte[16 << 20];
            await using var site = new AP_Atlas.Core.Testing.FakeWebSite();
            site.Answer = (path, _) => new AP_Atlas.Core.Testing.FakeWebSite.FullAnswer(200, big, "application/octet-stream");
            using var temp = new TempFolder();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            string dest = temp.File("pack.zip");
            // The first bytes in, the user cancels.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PoliteHttp.DownloadAsync(site.Site + "/big.zip", "the test site", dest, null,
                64L << 20, TimeSpan.FromMinutes(1), (done, _) => { if (done > 0) cts.Cancel(); }, cts.Token));
            Assert.Empty(Directory.GetFiles(temp.Path));
        }
        finally
        {
            PoliteHttp.Spacing = spacing;
        }
    }
}
