using System.Diagnostics;
using System.Text;
using AP_Atlas.Core.Connections;
using AP_Atlas.Core.Testing;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// A server that sends what no real server would: messages that aren't JSON, unknown commands, logins naming players that
/// can't be, rooms naming thousands of games, data packages with parts missing, floods, a close in the middle of a login.
/// Each time the slot carries on (or ends cleanly), memory stays bounded and the log says what happened once.
/// </summary>
public sealed partial class SessionManagerTests
{
    private async Task<(SessionManager Manager, ConnectedSlot Slot, List<string> Errors)> ConnectedAsync(FakeArchipelagoServer server, SessionManagerOptions? options = null)
    {
        var manager = Manager(options);
        var errors = new List<string>();
        manager.SocketError += (_, message) => { lock (errors) errors.Add(message); };
        var result = await Connect(manager, Login(server));
        Assert.Equal(ConnectOutcome.Connected, result.Outcome);
        return (manager, result.Slot!, errors);
    }

    [Fact]
    public async Task Messages_the_library_cant_read_leave_the_slot_connected_and_are_reported_once()
    {
        await using var server = Server();
        var (manager, slot, errors) = await ConnectedAsync(server);
        Assert.True(ServerPackets.IsFirst(slot.Session));

        foreach (string junk in new[] { "not json at all", "{}", "[1, 2, 3]", "[{\"cmd\": \"NoSuchCommand\", \"x\": 1}]", "[{\"cmd\": \"PrintJSON\"", "[{\"cmd\": \"ReceivedItems\", \"index\": 0}]" })
            await server.SendRawAsync(junk);
        await server.SendRawAsync(new byte[] { 0xff, 0xfe, 0x00, 0x01 }, binary: true);
        for (int i = 0; i < 200; i++) await server.SendRawAsync("garbage{" + i);
        // Then a real packet: the connection still reads.
        await server.SendToSlotAsync("Tester", FakeArchipelagoServer.ReceivedItems(0, new[] { 1000L }));
        await WaitFor(() => slot.Session.Items.AllItemsReceived.Count == 1, "the item after the junk", manager);

        Assert.True(manager.IsLoggedIn(slot.Slot));
        lock (errors) Assert.Single(errors); // the first, then counted until SocketErrorSpacing has passed
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_room_that_closes_in_the_middle_of_a_login_ends_the_attempt_at_once()
    {
        await using var server = Server();
        server.CloseOnLogin = true;
        var manager = Manager(new SessionManagerOptions { LoginTimeout = TimeSpan.FromSeconds(10), ReconnectDelays = new[] { TimeSpan.FromSeconds(1) }, Jitter = 0 });
        var started = Stopwatch.StartNew();

        var result = await Connect(manager, Login(server));

        // Not after the library's own 4 s wait for an answer, nor Atlas's 10 s limit.
        Assert.Equal(ConnectOutcome.Unreachable, result.Outcome);
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        Assert.Contains("closed", result.Message);
        Assert.False(manager.IsLoggedIn(new SlotId("p1", "Tester")));
    }

    [Fact]
    public async Task A_connection_that_breaks_in_the_middle_of_a_login_ends_the_attempt_at_once()
    {
        // No close frame, only an error from the socket: the login still ends at once, not after the library's 4 s.
        await using var server = Server();
        server.ResetOnLogin = true;
        var manager = Manager(new SessionManagerOptions { LoginTimeout = TimeSpan.FromSeconds(10), ReconnectDelays = new[] { TimeSpan.FromSeconds(1) }, Jitter = 0 });
        var started = Stopwatch.StartNew();

        var result = await Connect(manager, Login(server));

        Assert.Equal(ConnectOutcome.Unreachable, result.Outcome);
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        Assert.False(manager.IsLoggedIn(new SlotId("p1", "Tester")));
    }

    [Fact]
    public async Task A_login_naming_players_that_cant_be_is_made_usable()
    {
        await using var server = Server();
        string longName = new('n', 100_000);
        server.EditConnected = (_, connected) =>
        {
            // Out of range, below zero, numbered in the billions (the library would set aside gigabytes for it), twice, a gap
            // (slots 2 to 4, 6), another team, a name of 100,000 characters; slot info for slot 1 only, a group with no
            // members list, and no list of missing locations.
            connected["players"] = new JArray(
                new JObject { ["team"] = 0, ["slot"] = 2_000_000_000, ["name"] = "Huge", ["alias"] = "Huge" },
                new JObject { ["team"] = 999_999, ["slot"] = 1, ["name"] = "Far team", ["alias"] = "Far team" },
                new JObject { ["team"] = -1, ["slot"] = 1, ["name"] = "Negative", ["alias"] = "Negative" },
                new JObject { ["team"] = 0, ["slot"] = 1, ["name"] = "Tester", ["alias"] = "Tester" },
                new JObject { ["team"] = 0, ["slot"] = 1, ["name"] = "Impostor", ["alias"] = "Impostor" },
                new JObject { ["team"] = 0, ["slot"] = 5, ["name"] = longName, ["alias"] = longName },
                new JObject { ["team"] = 2, ["slot"] = 3, ["name"] = "Other team", ["alias"] = "Other team" });
            connected["slot_info"] = new JObject
            {
                ["1"] = new JObject { ["name"] = "Tester", ["game"] = "Test Game", ["type"] = 1, ["group_members"] = new JArray() },
                ["7"] = new JObject { ["name"] = "Link", ["game"] = "Test Game", ["type"] = 2, ["group_members"] = null }
            };
            connected["missing_locations"] = null;
            connected["checked_locations"] = new JArray(2000);
            return connected;
        };
        var (manager, slot, errors) = await ConnectedAsync(server);
        var players = slot.Session.Players;

        Assert.DoesNotContain(players.AllPlayers, player => player == null);
        Assert.Equal(3, players.Players.Count); // teams 0 to 2
        Assert.Equal("Tester", players.ActivePlayer.Name);
        Assert.Equal("Tester", players.GetPlayerName(1));
        Assert.Equal("Player 3", players.GetPlayerAlias(3));
        Assert.Equal(NameLimits.MaxName, players.GetPlayerAlias(5)!.Length);
        Assert.Equal(NameLimits.MaxName, players.GetPlayerName(5)!.Length);
        Assert.Equal("Link", players.GetPlayerName(7));
        Assert.Equal("Other team", players.GetPlayerInfo(2, 3)!.Name);
        Assert.Empty(slot.Session.Locations.AllMissingLocations);
        Assert.Contains(2000, slot.Session.Locations.AllLocationsChecked);

        // A room update naming players the login didn't: the library would fail on the first and skip the rest.
        await server.SendToSlotAsync("Tester", new JObject
        {
            ["cmd"] = "RoomUpdate",
            ["players"] = new JArray(
                new JObject { ["team"] = 9, ["slot"] = 1, ["name"] = "Nobody", ["alias"] = "Nobody" },
                new JObject { ["team"] = 0, ["slot"] = 50_000, ["name"] = "Nobody", ["alias"] = "Nobody" },
                new JObject { ["team"] = 0, ["slot"] = 1, ["name"] = "Tester", ["alias"] = "Renamed" })
        });
        await WaitFor(() => players.GetPlayerAlias(1) == "Renamed", "the rename in the room update", manager);

        // A scout's answer naming a location twice: the library's table of it would fail and leave the scout waiting for good.
        var scout = slot.Session.Locations.ScoutLocationsAsync(2000);
        Assert.False(scout.IsCompleted);
        var item = new JObject { ["item"] = 1000, ["location"] = 2000, ["player"] = 1, ["flags"] = 1, ["class"] = "NetworkItem" };
        await server.SendToSlotAsync("Tester", new JObject { ["cmd"] = "LocationInfo", ["locations"] = new JArray(item, item.DeepClone()) });
        var scouted = await scout.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Single(scouted);

        lock (errors) Assert.Empty(errors);
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_room_naming_thousands_of_games_and_a_data_package_with_parts_missing_are_used_safely()
    {
        await using var server = Server();
        string Hex(int i) => i.ToString("x40");
        server.EditRoomInfo = info =>
        {
            var games = (JArray)info["games"]!;
            var checksums = (JObject)info["datapackage_checksums"]!;
            for (int i = 0; i < 5_000; i++)
            {
                games.Add($"Game {i}");
                checksums[$"Game {i}"] = Hex(i);
            }
            games.Add(new string('g', 100_000));
            games.Add("Test Game"); // twice
            return info;
        };
        server.EditDataPackage = package =>
        {
            // Before the game asked for: one without its item names, one whose name is 100,000 characters.
            var games = (JObject)package["data"]!["games"]!;
            var sent = new JObject
            {
                ["Broken Game"] = new JObject { ["item_name_to_id"] = null, ["location_name_to_id"] = new JObject { ["Somewhere"] = 1 }, ["checksum"] = Hex(1) },
                [new string('h', 100_000)] = new JObject { ["item_name_to_id"] = new JObject(), ["location_name_to_id"] = new JObject(), ["checksum"] = Hex(2) }
            };
            foreach (var game in games.Properties()) sent[game.Name] = game.Value;
            package["data"]!["games"] = sent;
            return package;
        };
        // The login waits while the library asks for a thousand games' names (it sends them before it reads on): a slow machine
        // (CI's) took longer than the quick tests' 2 s.
        var (manager, slot, errors) = await ConnectedAsync(server, new SessionManagerOptions { LoginTimeout = TimeSpan.FromSeconds(15), ReconnectDelays = new[] { TimeSpan.FromSeconds(1) }, Jitter = 0 });

        // The library asks for each game's names in a packet of its own: at most MaxGames, each once.
        await WaitFor(() => server.Count("GetDataPackage") >= ServerPackets.MaxGames, "the data package requests", manager);
        await WaitFor(() => slot.Session.Items.GetItemName(1000, "Test Game") == "Sword", "the test game's names", manager);
        await Task.Delay(300, Ct);
        Assert.Equal(ServerPackets.MaxGames, server.Count("GetDataPackage"));
        var asked = server.Received.Where(p => (string?)p["cmd"] == "GetDataPackage").SelectMany(p => p["games"]!.ToObject<string[]>()!).ToList();
        Assert.Equal(asked.Count, asked.Distinct().Count());
        Assert.DoesNotContain(asked, game => game.Length > NameLimits.MaxName);
        Assert.Equal("Somewhere", slot.Session.Locations.GetLocationNameFromId(1, "Broken Game"));
        lock (errors) Assert.Empty(errors);
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_message_of_megabytes_and_floods_of_lines_and_bounces_keep_the_slot_and_its_memory_bounded()
    {
        await using var server = Server();
        var (manager, slot, errors) = await ConnectedAsync(server);
        await WaitFor(() => slot.EarlyMessageCount > 0, "the join line", manager);

        // One chat line of 8 MB.
        string big = new('m', 8 * 1024 * 1024);
        await server.BroadcastAsync(server.Chat(big));
        await WaitFor(() => slot.EarlyMessageCount >= 2, "the 8 MB line", manager);

        // 20,000 lines and 20,000 bounces in one message, before the window has taken the slot's lines over.
        var flood = Enumerable.Range(0, 20_000).Select(i => server.Chat("line " + i))
            .Concat(Enumerable.Range(0, 20_000).Select(i => new JObject { ["cmd"] = "Bounced", ["tags"] = new JArray("DeathLink"), ["data"] = new JObject { ["n"] = i } }))
            .ToArray();
        var lastLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        slot.Session.MessageLog.OnMessageReceived += message => { if (message.ToString().EndsWith("line 19999", StringComparison.Ordinal)) lastLine.TrySetResult(); };
        await server.BroadcastAsync(flood);
        await lastLine.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(manager.IsLoggedIn(slot.Slot));
        Assert.Equal(SlotInbox.MaxEarly, slot.EarlyMessageCount);
        var kept = slot.ReceiveMessages(_ => { });
        Assert.EndsWith("line 19999", kept[^1].ToString(), StringComparison.Ordinal); // the newest are kept
        lock (errors) Assert.Empty(errors);
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_librarys_abandoned_connection_attempt_is_recognised_and_nothing_else_is()
    {
        // The library stops waiting for a socket's connection after 4 s without checking how it ends. Windows takes about 2 s
        // to refuse each of the two tries for an address without a scheme, so the failure turns up later, unchecked.
        var leftovers = new List<Exception>();
        void Unobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.ToString().Contains("ConnectToProvidedUri", StringComparison.Ordinal)) lock (leftovers) leftovers.Add(e.Exception);
        }
        TaskScheduler.UnobservedTaskException += Unobserved;
        try
        {
            var session = AtlasSessions.Create("127.0.0.1:9", new DataPackageStore(_dir.Path));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct));
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(15))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                lock (leftovers) if (leftovers.Count > 0) break;
                await Task.Delay(250, Ct);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Unobserved;
        }
        lock (leftovers)
        {
            Assert.NotEmpty(leftovers);
            Assert.All(leftovers, failure => Assert.True(LibraryLeftovers.IsAbandonedConnect(failure)));
        }
        Assert.False(LibraryLeftovers.IsAbandonedConnect(new AggregateException(new InvalidOperationException("something else"))));
        Assert.False(LibraryLeftovers.IsAbandonedConnect(new AggregateException(new System.Net.WebSockets.WebSocketException("not the library's"))));
        Assert.False(LibraryLeftovers.IsAbandonedConnect(null));
    }
}
