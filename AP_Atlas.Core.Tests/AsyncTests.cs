namespace AP_Atlas.Core.Tests;

public class AsyncTests
{
    [Fact]
    public async Task Failures_are_reported_once_with_what_was_being_done()
    {
        var ct = TestContext.Current.CancellationToken;
        var reported = new List<(string Doing, Exception Error)>();
        void OnFailed(string doing, Exception error) { lock (reported) reported.Add((doing, error)); }
        Async.Failed += OnFailed;
        try
        {
            Async.Fire(Task.Run(async () => { await Task.Delay(30, ct); throw new InvalidOperationException("late failure"); }, ct), "testing a late failure");
            Async.Fire(() => throw new ArgumentException("early failure"), "testing an early failure");
            Async.Fire(Task.FromException(new IOException("already failed")), "testing a failed task");
            Async.Fire(Task.FromCanceled(new CancellationToken(true)), "testing a cancellation");
            Async.Fire(Task.Run(() => throw new OperationCanceledException(), ct), "testing a cancellation from inside");
            Async.Fire(Task.Run(() => throw new InvalidOperationException("routine"), ct), "testing routine work", tellUser: false);
            Async.Fire(Task.CompletedTask, "testing success");
            Async.Fire((Task?)null, "testing nothing");

            for (int i = 0; i < 100; i++)
            {
                lock (reported) if (reported.Count >= 3) break;
                await Task.Delay(20, ct);
            }
            await Task.Delay(200, ct);

            lock (reported)
            {
                Assert.Equal(3, reported.Count);
                Assert.Contains(reported, r => r.Doing == "testing a late failure" && r.Error is InvalidOperationException && r.Error.Message == "late failure");
                Assert.Contains(reported, r => r.Doing == "testing an early failure" && r.Error is ArgumentException);
                Assert.Contains(reported, r => r.Doing == "testing a failed task" && r.Error is IOException);
            }
        }
        finally
        {
            Async.Failed -= OnFailed;
        }
    }

    [Fact]
    public async Task Then_passes_the_result_or_nothing_when_the_task_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<string?>();
        var done = new SemaphoreSlim(0);
        void Record(string? value) { lock (results) results.Add(value); done.Release(); }

        Async.Then(Task.FromResult<string?>("answer"), Record, "testing a result");
        Async.Then(Task.FromException<string?>(new IOException("no answer")), Record, "testing a failure");

        Assert.True(await done.WaitAsync(2000, ct) && await done.WaitAsync(2000, ct));
        lock (results)
        {
            Assert.Contains("answer", results);
            Assert.Contains(null, results);
        }
    }
}
