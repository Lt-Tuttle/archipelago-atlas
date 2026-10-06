using System.Diagnostics;
using AP_Atlas.Core.Connections;
using AP_Atlas.Core.Testing;
using Archipelago.MultiClient.Net.Exceptions;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// Connecting, time limits, refusals, drops, careful reconnects and closing, against the fake server. The room's text
/// (one text connection per multiworld team) is in SessionManagerTests.RoomText.cs.
/// </summary>
public sealed partial class SessionManagerTests : IDisposable
{
    private const string Checksum = "0123456789abcdef0123456789abcdef01234567";
    private readonly TempFolder _dir = new();

    /// <summary>Short waits, no jitter: the same rules as Atlas uses, quicker.</summary>
    private static SessionManagerOptions Quick(params double[] reconnectSeconds) => new()
    {
        LoginTimeout = TimeSpan.FromSeconds(2),
        ReconnectDelays = (reconnectSeconds.Length > 0 ? reconnectSeconds : new[] { 0.2, 0.3, 0.4 }).Select(TimeSpan.FromSeconds).ToArray(),
        Jitter = 0
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SessionManager Manager(SessionManagerOptions? options = null) => new(new DataPackageStore(_dir.Path), options ?? Quick());

    private static FakeArchipelagoServer Server(params string[] slots)
    {
        var server = new FakeArchipelagoServer();
        server.Games["Test Game"] = new FakeGame(Checksum, new Dictionary<string, long> { ["Sword"] = 1000 }, new Dictionary<string, long> { ["Cave Chest"] = 2000 });
        if (slots.Length > 0)
        {
            server.Slots.Clear();
            server.Slots.AddRange(slots);
        }
        return server;
    }

    private static SlotLogin Login(FakeArchipelagoServer server, string slot = "Tester", string profile = "p1") =>
        new(new SlotId(profile, slot), server.Url.ToString(), null);

    /// <summary>Connects, failing the test after 30 seconds instead of hanging it if the manager never answers.</summary>
    private static Task<ConnectResult> Connect(SessionManager manager, SlotLogin login) =>
        manager.ConnectAsync(login, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

    /// <inheritdoc cref="Connect"/>
    private static Task Disconnect(SessionManager manager, SlotId slot) => manager.DisconnectAsync(slot).WaitAsync(TimeSpan.FromSeconds(30), Ct);

    /// <summary>
    /// Waits for something that happens on the network (failing after 10 seconds). Checks for drops while it waits, as
    /// the window does twice a second.
    /// </summary>
    private static async Task WaitFor(Func<bool> done, string what, SessionManager? manager = null)
    {
        var waited = Stopwatch.StartNew();
        while (!done())
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(10)) Assert.Fail("Timed out waiting for " + what);
            manager?.CheckForDrops();
            await Task.Delay(25, Ct);
        }
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_slot_logs_in_as_a_tracker_and_gets_the_rooms_data_checksums()
    {
        await using var server = Server();
        var manager = Manager();

        var result = await Connect(manager, Login(server));

        Assert.Equal(ConnectOutcome.Connected, result.Outcome);
        var slot = Assert.IsType<ConnectedSlot>(result.Slot);
        Assert.True(manager.IsLoggedIn(slot.Slot));
        Assert.Equal("Tester", slot.Login.SlotData["slot_name"]?.ToString());
        Assert.Equal(Checksum, slot.DataChecksums["Test Game"]);
        Assert.NotNull(AtlasSessions.StoreOf(slot.Session));
        var connect = Assert.Single(server.Received, p => (string?)p["cmd"] == "Connect");
        Assert.Equal("Tester", (string?)connect["name"]);
        Assert.Equal(new[] { "Tracker" }, connect["tags"]!.ToObject<string[]>());
        Assert.Equal(7, (int)connect["items_handling"]!); // every item, so Atlas sees the whole slot
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Messages_before_the_window_takes_over_are_kept_for_it()
    {
        await using var server = Server();
        var manager = Manager();

        var slot = (await Connect(manager, Login(server))).Slot!;
        await WaitFor(() => slot.EarlyMessageCount > 0, "the join message, which arrives just after the login");

        var early = slot.ReceiveMessages(_ => { });
        Assert.Contains(early, m => m.ToString().Contains("Tester (Team #1) tracking Test Game has joined."));
        Assert.Empty(slot.ReceiveMessages(_ => { })); // handed over once
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_server_that_never_answers_times_out_and_the_attempt_is_closed()
    {
        await using var server = Server();
        server.Silent = true;
        var manager = Manager();
        var started = Stopwatch.StartNew();

        var result = await Connect(manager, Login(server));

        Assert.Equal(ConnectOutcome.TimedOut, result.Outcome);
        Assert.InRange(started.Elapsed, TimeSpan.FromSeconds(1.9), TimeSpan.FromSeconds(8));
        await WaitFor(() => server.ClosesReceived == 1, "the attempt to be closed with a close frame");
        Assert.False(manager.IsLoggedIn(new SlotId("p1", "Tester")));
    }

    [Fact]
    public async Task A_login_answered_after_the_time_limit_is_closed_and_not_counted()
    {
        await using var server = Server();
        server.LoginDelay = TimeSpan.FromSeconds(3);
        var manager = Manager();
        var dropped = 0;
        manager.Dropped += (_, _) => Interlocked.Increment(ref dropped);

        var result = await Connect(manager, Login(server));

        Assert.Equal(ConnectOutcome.TimedOut, result.Outcome);
        await WaitFor(() => server.ClosesReceived == 1, "the late login to be closed");
        await Task.Delay(1500, Ct); // past the server's late answer
        Assert.False(manager.IsLoggedIn(new SlotId("p1", "Tester")));
        Assert.Equal(0, dropped);
    }

    [Fact]
    public async Task A_refused_login_says_why_and_is_closed()
    {
        await using var server = Server();
        var manager = Manager();

        var result = await Connect(manager, Login(server, "Nobody"));

        Assert.Equal(ConnectOutcome.Refused, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message), "a refusal says why");
        await WaitFor(() => server.ClosesReceived == 1, "the refused attempt to be closed");
    }

    [Fact]
    public async Task A_server_that_is_down_is_unreachable_and_a_bad_address_is_a_failure()
    {
        await using var server = Server();
        server.RefuseConnections = true;
        var manager = Manager();

        Assert.Equal(ConnectOutcome.Unreachable, (await Connect(manager, Login(server))).Outcome);
        var bad = await Connect(manager, new SlotLogin(new SlotId("p1", "Tester"), "ws://[not an address", null));
        Assert.Equal(ConnectOutcome.Failed, bad.Outcome);
    }

    [Fact]
    public async Task A_dropped_connection_is_reconnected_after_a_wait()
    {
        await using var server = Server();
        var manager = Manager();
        var dropped = new List<SlotId>();
        var scheduled = new List<(int Try, int Of, TimeSpan Wait)>();
        ConnectedSlot? back = null;
        manager.Dropped += (slot, _) => { lock (dropped) dropped.Add(slot); };
        manager.ReconnectScheduled += (_, attempt, of, wait) => { lock (scheduled) scheduled.Add((attempt, of, wait)); };
        manager.Reconnected += slot => Volatile.Write(ref back, slot);
        var first = (await Connect(manager, Login(server))).Slot!;

        server.DropClients();

        await WaitFor(() => Volatile.Read(ref back) != null, "the slot to reconnect", manager);
        Assert.Equal(new[] { first.Slot }, dropped);
        Assert.Equal((1, 3, TimeSpan.FromSeconds(0.2)), Assert.Single(scheduled));
        Assert.NotSame(first.Session, back!.Session);
        Assert.True(manager.IsLoggedIn(back.Slot));
        Assert.False(manager.IsReconnecting(back.Slot));
        Assert.Equal(2, server.Count("Connect"));
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Reconnecting_stops_after_the_last_try_with_one_attempt_per_try()
    {
        await using var server = Server();
        var manager = Manager();
        var stopped = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ReconnectStopped += (_, refusal) => stopped.TrySetResult(refusal);
        var slot = (await Connect(manager, Login(server))).Slot!.Slot;
        server.RefuseConnections = true;
        int before = server.Accepted;

        server.DropClients();

        await WaitFor(() => stopped.Task.IsCompleted, "reconnecting to stop", manager);
        Assert.Null(await stopped.Task); // not refused: every try went unanswered
        Assert.Equal(3, server.Accepted - before); // one connection per try, never more
        Assert.False(manager.IsReconnecting(slot));
        await Task.Delay(800, Ct);
        Assert.Equal(3, server.Accepted - before); // and nothing after stopping
    }

    [Fact]
    public async Task A_refusal_stops_reconnecting()
    {
        await using var server = Server();
        var manager = Manager();
        var stopped = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ReconnectStopped += (_, refusal) => stopped.TrySetResult(refusal);
        await Connect(manager, Login(server));
        server.SlotName = "Renamed"; // the slot is gone from the room

        server.DropClients();

        await WaitFor(() => stopped.Task.IsCompleted, "reconnecting to stop", manager);
        Assert.False(string.IsNullOrWhiteSpace(await stopped.Task), "a refusal says why");
        await Task.Delay(800, Ct);
        Assert.Equal(2, server.Count("Connect")); // one try, then no more
    }

    [Fact]
    public async Task With_automatic_reconnects_off_a_drop_is_only_reported()
    {
        await using var server = Server();
        var manager = Manager();
        manager.AutoReconnect = false;
        int dropped = 0;
        manager.Dropped += (_, _) => Interlocked.Increment(ref dropped);
        var slot = (await Connect(manager, Login(server))).Slot!.Slot;

        server.DropClients();

        await WaitFor(() => Volatile.Read(ref dropped) == 1, "the drop to be reported", manager);
        await Task.Delay(800, Ct);
        Assert.False(manager.IsReconnecting(slot));
        Assert.Equal(1, server.Count("Connect"));
    }

    [Fact]
    public async Task Disconnecting_closes_properly_and_is_not_a_drop()
    {
        await using var server = Server();
        var manager = Manager();
        int dropped = 0;
        manager.Dropped += (_, _) => Interlocked.Increment(ref dropped);
        var slot = (await Connect(manager, Login(server))).Slot!.Slot;

        await Disconnect(manager, slot);

        await WaitFor(() => server.ClosesReceived == 1, "a close frame", manager);
        Assert.False(manager.IsLoggedIn(slot));
        await Task.Delay(500, Ct);
        manager.CheckForDrops();
        Assert.Equal(0, dropped);
        Assert.Equal(1, server.Count("Connect"));
    }

    [Fact]
    public async Task Disconnecting_or_connecting_by_hand_calls_off_a_waiting_reconnect()
    {
        await using var server = Server();
        var manager = Manager(Quick(3.0, 3.0));
        var slot = (await Connect(manager, Login(server))).Slot!.Slot;

        server.DropClients();
        await WaitFor(() => manager.IsReconnecting(slot), "a reconnect to be scheduled", manager);
        await Disconnect(manager, slot);
        Assert.False(manager.IsReconnecting(slot));

        Assert.Equal(ConnectOutcome.Connected, (await Connect(manager, Login(server))).Outcome);
        server.DropClients();
        await WaitFor(() => manager.IsReconnecting(slot), "a reconnect to be scheduled", manager);
        Assert.Equal(ConnectOutcome.Connected, (await Connect(manager, Login(server))).Outcome); // the user took over
        Assert.False(manager.IsReconnecting(slot));

        await Task.Delay(3500, Ct);
        Assert.Equal(3, server.Count("Connect")); // only the user's connections: no reconnect ran
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Closing_Atlas_closes_every_session_and_nothing_connects_afterwards()
    {
        await using var server = Server("One", "Two", "Three");
        await using var silent = Server();
        silent.Silent = true;
        var manager = Manager();
        foreach (string name in new[] { "One", "Two", "Three" })
            Assert.Equal(ConnectOutcome.Connected, (await Connect(manager, Login(server, name))).Outcome);
        var stillConnecting = Connect(manager, Login(silent, "Tester", "p2"));
        await WaitFor(() => silent.Accepted == 1, "the fourth connection to start");

        var (sessions, inTime) = await manager.CloseAllAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, sessions);
        Assert.True(inTime);
        await WaitFor(() => server.ClosesReceived == 3, "close frames for the logged-in sessions");
        await WaitFor(() => silent.Ended == 1, "the connection still logging in to be ended");
        Assert.NotEqual(ConnectOutcome.Connected, (await stillConnecting).Outcome);
        Assert.Equal(ConnectOutcome.Cancelled, (await Connect(manager, Login(server, "One"))).Outcome);
    }

    [Fact]
    public async Task A_connection_Atlas_closes_while_it_opens_is_closed_as_it_opens()
    {
        // The server takes a second to answer the connection's opening handshake, and never sends the room's info: Atlas
        // closes while the connection is still opening.
        await using var silent = Server();
        silent.Silent = true;
        silent.HandshakeDelay = TimeSpan.FromSeconds(1);
        var manager = Manager();
        var connecting = Connect(manager, Login(silent));
        await WaitFor(() => silent.Accepted == 1, "the connection to start opening");

        var (sessions, _) = await manager.CloseAllAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, sessions);
        // The library can't close a connection that isn't open yet: Atlas closes it as it opens, with a close frame (it
        // used to be left open, and the server waited for good).
        await WaitFor(() => silent.ClosesReceived == 1 && silent.Ended == 1, "the connection to be closed as it opened");
        // The login ends there, cancelled, instead of waiting for the room's info for good (and running out of time).
        var result = await connecting.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ConnectOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task Deleting_a_multiworld_while_its_slot_logs_in_closes_that_login()
    {
        await using var server = Server();
        server.LoginDelay = TimeSpan.FromSeconds(0.5);
        var manager = Manager();
        var login = Login(server);

        var connecting = Connect(manager, login);
        await WaitFor(() => server.Count("Connect") == 1, "the login to reach the server");
        await manager.ForgetProfileAsync("p1");
        var result = await connecting;

        Assert.Equal(ConnectOutcome.Cancelled, result.Outcome);
        Assert.Null(result.Slot);
        Assert.False(manager.IsLoggedIn(login.Slot));
        await WaitFor(() => server.ClosesReceived == 1, "the finished login to be closed on the server");
        // And the deleted multiworld's slots don't connect again.
        Assert.Equal(ConnectOutcome.Cancelled, (await Connect(manager, login)).Outcome);
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Connections_are_made_one_at_a_time()
    {
        await using var server = Server("One", "Two", "Three");
        server.LoginDelay = TimeSpan.FromSeconds(0.3);
        var manager = Manager();

        var results = await Task.WhenAll(new[] { "One", "Two", "Three" }.Select(name => Connect(manager, Login(server, name))));

        Assert.All(results, r => Assert.Equal(ConnectOutcome.Connected, r.Outcome));
        // Each login reached the server only after the one before it was answered.
        Assert.Equal(new[] { "Connect", "Connected", "Connect", "Connected", "Connect", "Connected" }, server.Timeline.Select(t => t.What.Split(' ')[0]));
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    private static int MinWorkerThreads()
    {
        ThreadPool.GetMinThreads(out int workers, out _);
        return workers;
    }

    [Fact]
    public async Task A_connection_holds_a_thread_only_while_it_is_open()
    {
        await using var server = Server("Alice", "Bob", "Carol");
        var manager = Manager();
        manager.AutoReconnect = false;
        var dropped = new List<SlotId>();
        manager.Dropped += (slot, _) => { lock (dropped) dropped.Add(slot); };
        int before = LibraryThreads.Holding;

        var slots = await ConnectAll(manager, server, "p1", "Alice", "Bob", "Carol");

        // Each open connection's send loop blocks a pool thread: the pool's minimum covers them.
        Assert.Equal(before + 3, LibraryThreads.Holding);
        Assert.Equal(LibraryThreads.Baseline + before + 3, MinWorkerThreads());

        // However a connection ends, Atlas finishes it and its thread goes back. Closed by Atlas:
        await Disconnect(manager, slots[0].Slot);
        Assert.Equal(before + 2, LibraryThreads.Holding);
        // Closed by the server (its room shut down): the library reports it. Nothing checks for dead connections meanwhile.
        await server.CloseClientAsync("Bob");
        await WaitFor(() => { lock (dropped) return dropped.Contains(slots[1].Slot); }, "Bob's drop to be reported");
        Assert.Equal(before + 1, LibraryThreads.Holding);
        // Cut off: only the check for dead connections notices.
        server.DropClient("Carol");
        await WaitFor(() => LibraryThreads.Holding == before, "Carol's thread to go back", manager);
        Assert.Equal(LibraryThreads.Baseline + before, MinWorkerThreads());
    }

    [Fact]
    public async Task A_finished_connections_send_loop_ends_quietly()
    {
        await using var server = Server();
        var manager = Manager();
        var errors = new List<string>();
        manager.SocketError += (_, message) => { lock (errors) errors.Add(message); };
        var slot = (await Connect(manager, Login(server))).Slot!;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        slot.Session.Socket.ErrorReceived += (error, _) =>
        {
            if (error is ArchipelagoSocketClosedException) ended.TrySetResult();
        };
        // After a send the loop pauses for 20 ms; then it blocks its thread until the next packet.
        await Task.Delay(200, Ct);

        await Disconnect(manager, slot.Slot);

        // Woken, the loop found its connection closed and ended: the library reports that as a closed socket.
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Settle();
        // Which isn't news to the user.
        lock (errors) Assert.Empty(errors);
    }

    [Fact]
    public async Task A_connection_that_opens_after_its_logins_time_limit_is_closed_without_logging_in()
    {
        await using var server = Server();
        server.HandshakeDelay = TimeSpan.FromSeconds(2.5); // past the login's time limit
        var manager = Manager();
        int before = LibraryThreads.Holding;

        var result = await Connect(manager, Login(server, "Nobody"));

        Assert.Equal(ConnectOutcome.TimedOut, result.Outcome);
        // Its connection opens after all. Atlas gave up on it, so it's closed as it opens, without logging in (it used to log
        // in late, and a refused login was left open on the server), and its thread goes back.
        await WaitFor(() => server.ClosesReceived == 1 && server.Ended == 1, "the late connection to be closed");
        Assert.Equal(0, server.Count("Connect"));
        await WaitFor(() => LibraryThreads.Holding == before, "its thread to go back");
    }
}
