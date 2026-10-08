using System.Diagnostics;
using AP_Atlas.Core.EngineSetup;
using AP_Atlas.Core.Testing;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// A multiworld's engine pool (<see cref="EnginePool"/>), against the fake engine: an engine per slot up to the pool's
/// limit, then shared; each slot's own world in a shared engine; a crash or a stuck engine lost only by its slots, and
/// counted only against the slot whose request it was answering; a slot leaving its engine.
/// </summary>
public class EnginePoolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(30);

    private static async Task<string> PythonOrSkipAsync()
    {
        string? python = await TestEnvironment.PythonAsync(Ct);
        if (python == null) Assert.Skip("Python isn't installed here (CI runs this test).");
        return python!;
    }

    private static EnginePool Pool(string python, FakeLogicEngine engine, int max) =>
        new(() => EngineProcess.Start(FakeEngines.StartInfo(python, engine), engine.Root, _ => { }), max, _ => { });

    /// <summary>Joins a slot and starts its world (all three locations missing).</summary>
    private static async Task<EngineSeat> JoinAsync(EnginePool pool, string key, params long[] locations)
    {
        var seat = pool.Join(key);
        var ready = await seat.AskAsync(FakeEngines.Init(locations.Length > 0 ? locations : new long[] { 2000, 2001, 2002 }), Plenty, ct: Ct);
        Assert.Equal("ready", (string?)ready.Reply?["status"]);
        return seat;
    }

    /// <summary>What's in logic with these items, for the slot's own missing locations (as its start set them).</summary>
    private static async Task<long[]> ReachableAsync(EngineSeat seat, params long[] items)
    {
        var answer = await seat.AskAsync(new JObject { ["action"] = "update", ["items"] = new JArray(items) }, Plenty, ct: Ct);
        Assert.True(answer.Reply != null, answer.Why);
        return FakeEngines.Ids(answer.Reply!["reachable"]);
    }

    [Fact]
    public void A_pool_runs_half_as_many_engines_as_processors_from_one_to_four()
    {
        Assert.Equal(new[] { 1, 1, 2, 3, 4, 4 }, new[] { 1, 2, 4, 6, 8, 16 }.Select(EnginePool.EnginesFor));
    }

    [Fact]
    public async Task Each_slot_gets_an_engine_until_the_limit_then_joins_the_least_busy()
    {
        string python = await PythonOrSkipAsync();
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        var pool = Pool(python, engine, max: 2);

        var alice = await JoinAsync(pool, "Alice");
        var bob = await JoinAsync(pool, "Bob");
        Assert.Equal(new[] { 1, 1 }, pool.SlotsPerEngine);
        var carol = await JoinAsync(pool, "Carol");
        Assert.Equal(new[] { 2, 1 }, pool.SlotsPerEngine);
        var dave = await JoinAsync(pool, "Dave");

        Assert.Equal(new[] { 2, 2 }, pool.SlotsPerEngine);
        Assert.Equal(alice.EngineNumber, carol.EngineNumber);
        Assert.Equal(bob.EngineNumber, dave.EngineNumber);
        Assert.NotEqual(alice.EngineNumber, bob.EngineNumber);
        Assert.Equal(2, engine.Starts);
        foreach (var seat in new[] { alice, bob, carol, dave }) seat.Leave();
    }

    [Fact]
    public async Task Retired_engines_take_no_new_slots_and_stop_when_their_last_slot_leaves()
    {
        string python = await PythonOrSkipAsync();
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        var pool = Pool(python, engine, max: 2);
        var alice = await JoinAsync(pool, "Alice");
        Assert.Equal(1, pool.LiveEngines);

        pool.Retire(); // the engine's parts changed: Alice's process loaded the old ones
        Assert.Equal(0, pool.LiveEngines);
        var bob = await JoinAsync(pool, "Bob"); // a fresh engine, though the retired one has room
        Assert.NotEqual(alice.EngineNumber, bob.EngineNumber);
        Assert.Equal(new[] { 1, 1 }, pool.SlotsPerEngine);
        Assert.Equal(2, engine.Starts);
        Assert.Equal(1, pool.LiveEngines);

        alice.Leave(); // the retired engine's last slot: it stops
        await Task.Delay(500, Ct);
        Assert.Equal(new[] { 1 }, pool.SlotsPerEngine);
        bob.Leave();
    }

    [Fact]
    public async Task Slots_sharing_an_engine_each_keep_their_own_world()
    {
        string python = await PythonOrSkipAsync();
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        var pool = Pool(python, engine, max: 1);

        // Alice still has every location to check; Carol only the Cave Chest.
        var alice = await JoinAsync(pool, "Alice", 2000, 2001, 2002);
        var carol = await JoinAsync(pool, "Carol", 2000);

        Assert.Equal(new long[] { 2000, 2001 }, await ReachableAsync(alice, 1000));
        Assert.Equal(new long[] { 2000 }, await ReachableAsync(carol, 1000));
        Assert.Equal(new long[] { 2000, 2001, 2002 }, await ReachableAsync(alice, 1000, 1001));
        Assert.Equal(new[] { "Alice", "Carol", "Alice" }, engine.Requests("update").Select(r => (string?)r["key"]));
        alice.Leave();
        carol.Leave();
    }

    [Fact]
    public async Task A_crash_is_lost_only_by_its_engines_slots_and_counts_for_the_slot_it_was_answering()
    {
        string python = await PythonOrSkipAsync();
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        engine.CrashOnItem = 1001;
        engine.Apply();
        var pool = Pool(python, engine, max: 2);
        var alice = await JoinAsync(pool, "Alice");
        var bob = await JoinAsync(pool, "Bob");
        var carol = await JoinAsync(pool, "Carol"); // shares Alice's engine
        var carolLost = new TaskCompletionSource<EngineLoss>(TaskCreationOptions.RunContinuationsAsynchronously);
        carol.Lost += loss => carolLost.TrySetResult(loss);
        bool bobLost = false;
        bob.Lost += _ => bobLost = true;

        var crashed = await alice.AskAsync(FakeEngines.Update(new long[] { 1000, 1001 }, 2000, 2001, 2002), Plenty, ct: Ct);

        Assert.Equal(EngineFailure.Ended, crashed.Failure);
        Assert.True(alice.Loss is { Mine: true, Culprit: "Alice" }, alice.Loss?.ToString());
        var loss = await carolLost.Task.WaitAsync(Plenty, Ct);
        Assert.False(loss.Mine);
        Assert.Equal("Alice", loss.Culprit);
        Assert.False(carol.Running);
        // Bob's engine carries on.
        Assert.True(bob.Running);
        Assert.Equal(new long[] { 2000, 2001 }, await ReachableAsync(bob, 1000));
        Assert.False(bobLost);
        Assert.Equal(new[] { 1 }, pool.SlotsPerEngine);
        bob.Leave();
    }

    [Fact]
    public async Task An_engine_that_stops_answering_is_stopped_and_only_its_slots_lose_it()
    {
        string python = await PythonOrSkipAsync();
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        var pool = Pool(python, engine, max: 1);
        var alice = await JoinAsync(pool, "Alice");
        var carol = await JoinAsync(pool, "Carol");
        var carolLost = new TaskCompletionSource<EngineLoss>(TaskCreationOptions.RunContinuationsAsynchronously);
        carol.Lost += loss => carolLost.TrySetResult(loss);
        engine.Delays["update"] = 5;
        engine.Apply();

        var stuck = await alice.AskAsync(FakeEngines.Update(new long[] { 1000 }, 2000, 2001, 2002), TimeSpan.FromSeconds(0.5), ct: Ct);

        Assert.Equal(EngineFailure.TimedOut, stuck.Failure);
        Assert.True(alice.Loss is { Mine: true, Culprit: "Alice" }, alice.Loss?.ToString());
        var loss = await carolLost.Task.WaitAsync(Plenty, Ct);
        Assert.False(loss.Mine);
        Assert.Empty(pool.SlotsPerEngine);
        // A slot that starts again gets a new engine.
        engine.Delays.Clear();
        engine.Apply();
        var again = await JoinAsync(pool, "Alice");
        Assert.Equal(2, engine.Starts);
        again.Leave();
    }

    [Fact]
    public async Task A_why_that_takes_long_does_not_stop_the_engine()
    {
        string python = await PythonOrSkipAsync();
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        var pool = Pool(python, engine, max: 1);
        var alice = await JoinAsync(pool, "Alice");
        await ReachableAsync(alice, 1000);
        engine.Delays["explain"] = 2;
        engine.Apply();

        var why = await alice.AskAsync(new JObject { ["action"] = "explain", ["location"] = 2002, ["analyze"] = true }, TimeSpan.FromSeconds(0.5), stuckIfLate: false, ct: Ct);

        Assert.Equal(EngineFailure.TimedOut, why.Failure);
        Assert.Null(alice.Loss);
        Assert.True(alice.Running);
        // Its answer comes late and is thrown away; the next request is answered after it.
        Assert.Equal(new long[] { 2000, 2001, 2002 }, await ReachableAsync(alice, 1000, 1001));
        alice.Leave();
    }

    [Fact]
    public async Task A_slot_that_leaves_has_its_world_dropped_and_the_last_one_stops_the_engine()
    {
        string python = await PythonOrSkipAsync();
        using var dir = new TempFolder();
        var engine = FakeEngines.SmallWorld(dir);
        engine.SpawnChild = true; // shows when the engine's process ends
        engine.Apply();
        var pool = Pool(python, engine, max: 1);
        var alice = await JoinAsync(pool, "Alice");
        var bob = await JoinAsync(pool, "Bob");
        using var child = Process.GetProcessById(Assert.Single(engine.Children));

        bob.Leave();

        await WaitForAsync(() => engine.Requests("drop").Any(r => (string?)r["key"] == "Bob"), "Bob's world to be dropped");
        Assert.Equal(new long[] { 2000, 2001 }, await ReachableAsync(alice, 1000));
        Assert.False(child.HasExited);
        var gone = await bob.AskAsync(FakeEngines.Update(Array.Empty<long>(), 2000), Plenty, ct: Ct);
        Assert.Equal(EngineFailure.Stopped, gone.Failure);

        alice.Leave();

        Assert.Empty(pool.SlotsPerEngine);
        await child.WaitForExitAsync(Ct).WaitAsync(Plenty, Ct);
        Assert.Null(alice.Loss);
    }

    private static async Task WaitForAsync(Func<bool> done, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!done())
        {
            if (waited.Elapsed > Plenty) Assert.Fail("Timed out waiting for " + what);
            await Task.Delay(25, Ct);
        }
    }
}
