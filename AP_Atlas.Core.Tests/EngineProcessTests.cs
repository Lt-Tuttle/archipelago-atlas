using System.Diagnostics;
using AP_Atlas.Core.EngineSetup;
using AP_Atlas.Core.Testing;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// Atlas's side of a logic engine (<see cref="EngineProcess"/>), against the fake engine run as Atlas runs the real one:
/// only an answer with its request's id counts, a bridge that can't load or crashes is a failure at once, a late answer
/// is thrown away, and stopping an engine ends everything it started.
/// </summary>
public class EngineProcessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(30);

    /// <summary>What the engine wrote to Atlas's log.</summary>
    private sealed class Log
    {
        private readonly List<string> _lines = new();

        public void Add(string line)
        {
            lock (_lines) _lines.Add(line);
        }

        public bool Has(string text)
        {
            lock (_lines) return _lines.Any(line => line.Contains(text, StringComparison.Ordinal));
        }

        public override string ToString()
        {
            lock (_lines) return string.Join(" | ", _lines);
        }
    }

    private static async Task<string?> PythonOrSkipAsync()
    {
        string? python = await TestEnvironment.PythonAsync(Ct);
        if (python == null) Assert.Skip("Python isn't installed here (CI runs this test).");
        return python;
    }

    [Fact]
    public async Task Only_the_answer_with_its_requests_id_counts()
    {
        string python = (await PythonOrSkipAsync())!;
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        // Before each answer: a log line, a late answer to the request before, and an answer-like line with no id.
        engine.Chatter = true;
        engine.Apply();
        var log = new Log();
        var process = EngineProcess.Start(FakeEngines.StartInfo(python, engine), engine.Root, log.Add);

        var ready = await process.AskAsync(FakeEngines.Init(2000, 2001, 2002), Plenty, Ct);
        var sword = await process.AskAsync(FakeEngines.Update(new long[] { 1000 }, 2000, 2001, 2002), Plenty, Ct);

        Assert.Equal("ready", (string?)ready.Reply?["status"]);
        Assert.Equal(new long[] { 2000, 2001 }, FakeEngines.Ids(sword.Reply?["reachable"]));
        Assert.True(log.Has("PYTHON: Fake engine: thinking about request 2"), log.ToString());
        Assert.True(log.Has("answered request 1 late"), log.ToString());
        Assert.True(log.Has("PYTHON: {\"reachable\": []"), log.ToString());
        process.Stop();
    }

    [Fact]
    public async Task An_answer_that_comes_too_late_is_thrown_away()
    {
        string python = (await PythonOrSkipAsync())!;
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        var log = new Log();
        var process = EngineProcess.Start(FakeEngines.StartInfo(python, engine), engine.Root, log.Add);
        await process.AskAsync(FakeEngines.Init(2000, 2001, 2002), Plenty, Ct);
        engine.Delays["update"] = 1.5;
        engine.Apply();

        var late = await process.AskAsync(FakeEngines.Update(new long[] { 1000, 1001 }, 2000, 2001, 2002), TimeSpan.FromSeconds(0.5), Ct);
        engine.Delays.Clear();
        engine.Apply();
        var next = await process.AskAsync(FakeEngines.Update(Array.Empty<long>(), 2000, 2001, 2002), Plenty, Ct);

        // The engine was still working on the first: its answer (everything reachable) isn't taken for the second's.
        Assert.Equal(EngineFailure.TimedOut, late.Failure);
        Assert.Null(late.Reply);
        Assert.Equal(new long[] { 2000 }, FakeEngines.Ids(next.Reply?["reachable"]));
        Assert.True(log.Has("answered request 2 late"), log.ToString());
        Assert.True(process.Running);
        process.Stop();
    }

    [Fact]
    public async Task A_bridge_that_cannot_load_fails_at_once()
    {
        string python = (await PythonOrSkipAsync())!;
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        engine.BootError = "ModuleNotFoundError: No module named 'worlds.tracker'";
        engine.Apply();
        var log = new Log();
        var process = EngineProcess.Start(FakeEngines.StartInfo(python, engine), engine.Root, log.Add);
        var started = Stopwatch.StartNew();

        // A start may take minutes; a bridge that can't load says so at once.
        var answer = await process.AskAsync(FakeEngines.Init(2000, 2001, 2002), TimeSpan.FromSeconds(60), Ct);

        Assert.Null(answer.Reply);
        Assert.Equal(EngineFailure.CouldNotLoad, answer.Failure);
        Assert.Contains("No module named 'worlds.tracker'", answer.Why);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"the failure took {started.Elapsed.TotalSeconds:0} s");
        Assert.False(process.Running);
        var after = await process.AskAsync(FakeEngines.Update(Array.Empty<long>(), 2000), Plenty, Ct);
        Assert.Equal(EngineFailure.CouldNotLoad, after.Failure);
        process.Stop();
    }

    [Fact]
    public async Task A_crash_fails_the_request_at_once_and_is_reported()
    {
        string python = (await PythonOrSkipAsync())!;
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        engine.CrashOnItem = 1001;
        engine.Apply();
        var log = new Log();
        var process = EngineProcess.Start(FakeEngines.StartInfo(python, engine), engine.Root, log.Add);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += code => exited.TrySetResult(code);
        await process.AskAsync(FakeEngines.Init(2000, 2001, 2002), Plenty, Ct);

        var started = Stopwatch.StartNew();
        var answer = await process.AskAsync(FakeEngines.Update(new long[] { 1000, 1001 }, 2000, 2001, 2002), TimeSpan.FromSeconds(60), Ct);

        Assert.Null(answer.Reply);
        Assert.Equal(EngineFailure.Ended, answer.Failure);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"the failure took {started.Elapsed.TotalSeconds:0} s");
        Assert.Equal(3, await exited.Task.WaitAsync(Plenty, Ct));
        Assert.False(process.Running);
        Assert.Equal(1, engine.Crashes);
    }

    [Fact]
    public async Task Stopping_an_engine_ends_every_process_it_started()
    {
        string python = (await PythonOrSkipAsync())!;
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        engine.SpawnChild = true;
        engine.Apply();
        var log = new Log();
        var process = EngineProcess.Start(FakeEngines.StartInfo(python, engine), engine.Root, log.Add);
        bool exitedByItself = false;
        process.Exited += _ => exitedByItself = true;
        await process.AskAsync(FakeEngines.Init(2000, 2001, 2002), Plenty, Ct);
        using var child = Process.GetProcessById(Assert.Single(engine.Children));

        process.Stop();

        await child.WaitForExitAsync(Ct).WaitAsync(Plenty, Ct);
        Assert.True(child.HasExited);
        Assert.False(process.Running);
        var after = await process.AskAsync(FakeEngines.Update(Array.Empty<long>(), 2000), Plenty, Ct);
        Assert.Equal(EngineFailure.Stopped, after.Failure);
        await Task.Delay(300, Ct);
        Assert.False(exitedByItself, "a stopped engine was reported as having stopped by itself");
    }

    [Fact]
    public async Task An_endless_line_on_the_answer_channel_ends_the_engine_as_a_crash_does()
    {
        string python = (await PythonOrSkipAsync())!;
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        engine.Apply();
        var log = new Log();
        var process = EngineProcess.Start(FakeEngines.StartInfo(python, engine), engine.Root, log.Add);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += code => exited.TrySetResult(code);
        Assert.Equal("ready", (string?)(await process.AskAsync(FakeEngines.Init(2000, 2001, 2002), Plenty, Ct)).Reply?["status"]);
        engine.FloodAnswers = BoundedLineReader.AnswerLimit + 1000;
        engine.Apply();

        var answer = await process.AskAsync(FakeEngines.Update(new long[] { 1000 }, 2000, 2001, 2002), Plenty, Ct);

        Assert.Equal(EngineFailure.Ended, answer.Failure);
        Assert.Contains($"a line of over {BoundedLineReader.AnswerLimit:N0} characters", answer.Why);
        // Ended as a crash is, so its pool starts its slots again on a new engine.
        await exited.Task.WaitAsync(Plenty, Ct);
        Assert.False(process.Running);
        Assert.True(log.Has("Atlas stopped it"), log.ToString());
    }

    [Fact]
    public async Task An_endless_error_line_is_cut_for_the_log_and_the_engine_carries_on()
    {
        string python = (await PythonOrSkipAsync())!;
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        engine.FloodErrors = 1_000_000;
        engine.Apply();
        var log = new Log();
        var process = EngineProcess.Start(FakeEngines.StartInfo(python, engine), engine.Root, log.Add);

        await process.AskAsync(FakeEngines.Init(2000, 2001, 2002), Plenty, Ct);
        var sword = await process.AskAsync(FakeEngines.Update(new long[] { 1000 }, 2000, 2001, 2002), Plenty, Ct);

        Assert.Equal(new long[] { 2000, 2001 }, FakeEngines.Ids(sword.Reply?["reachable"]));
        string cut = $"… ({1_000_000 - BoundedLineReader.LogLimit:N0} more characters)";
        Assert.True(log.Has("PYTHON: " + new string('E', BoundedLineReader.LogLimit) + cut), "the error line wasn't cut for the log");
        process.Stop();
    }
}
