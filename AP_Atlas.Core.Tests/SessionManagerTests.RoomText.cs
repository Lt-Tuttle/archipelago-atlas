using AP_Atlas.Core.Connections;
using AP_Atlas.Core.Testing;
using Archipelago.MultiClient.Net.MessageLog.Messages;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// The room's text: one connection per multiworld team receives it (the others log in with NoText), and every slot
/// still gets every line, once and in order, against a fake server that sends text as a real one does.
/// </summary>
public sealed partial class SessionManagerTests
{
    private static readonly string[] TextTags = { "Tracker" };
    private static readonly string[] QuietTags = { "Tracker", "NoText" };

    /// <summary>A slot's text client lines, taken over as its window takes them, in the order they reached it.</summary>
    private sealed class Lines
    {
        private readonly List<LogMessage> _lines = new();

        public Lines(ConnectedSlot slot)
        {
            // Held while taking over, so a line passed on meanwhile lands after the earlier ones.
            lock (_lines) _lines.AddRange(slot.ReceiveMessages(Add));
        }

        private void Add(LogMessage line)
        {
            lock (_lines) _lines.Add(line);
        }

        public List<string> All
        {
            get { lock (_lines) return _lines.Select(line => line.ToString()).ToList(); }
        }

        /// <summary>How many lines contain this text.</summary>
        public int Count(string text) => All.Count(line => line.Contains(text, StringComparison.Ordinal));

        public int CountOf<T>() where T : LogMessage
        {
            lock (_lines) return _lines.OfType<T>().Count();
        }
    }

    private async Task<ConnectedSlot[]> ConnectAll(SessionManager manager, FakeArchipelagoServer server, string profile, params string[] slots)
    {
        var connected = new List<ConnectedSlot>();
        foreach (string slot in slots)
        {
            var result = await Connect(manager, Login(server, slot, profile));
            Assert.Equal(ConnectOutcome.Connected, result.Outcome);
            connected.Add(result.Slot!);
        }
        return connected.ToArray();
    }

    /// <summary>Lets anything still on its way arrive, so a line that would come twice has come twice.</summary>
    private static Task Settle() => Task.Delay(300, Ct);

    [Fact]
    public async Task One_connection_per_multiworld_receives_the_rooms_text_and_every_slot_shows_every_line_once_in_order()
    {
        await using var server = Server("Alice", "Bob", "Carol");
        var manager = Manager();
        var slots = await ConnectAll(manager, server, "p1", "Alice", "Bob", "Carol");

        // The first slot logs in with text; the others with NoText, so the server sends them none.
        Assert.Equal(TextTags, server.TagsOf("Alice"));
        Assert.Equal(QuietTags, server.TagsOf("Bob"));
        Assert.Equal(QuietTags, server.TagsOf("Carol"));
        Assert.Equal(1, server.TextClients);
        Assert.Equal(new[] { true, false, false }, slots.Select(s => s.ReceivesText));
        Assert.Equal(0, server.TagChanges);

        var lines = slots.Select(s => new Lines(s)).ToArray();
        await server.BroadcastAsync(Enumerable.Range(1, 30).Select(i => server.Chat($"line {i}")).ToArray());
        await server.BroadcastAsync(FakeArchipelagoServer.ItemSend(2, 3, 1000, 2000));
        await WaitFor(() => lines.All(l => l.Count("line 30") == 1 && l.Count("Bob sent Sword to Carol") == 1), "the room's lines to reach every slot");
        await Settle();
        var expected = Enumerable.Range(1, 30).Select(i => $"Alice: line {i}").ToList();
        foreach (var l in lines)
        {
            Assert.Equal(expected, l.All.Where(line => line.Contains(": line ", StringComparison.Ordinal)).ToList());
            Assert.Equal(1, l.Count("Bob sent Sword to Carol (Cave Chest)"));
        }
        // Each slot has the join lines from its own login on, once: Carol's in every slot, Bob's in Alice's and his own.
        Assert.Equal(new[] { 1, 1, 1 }, lines.Select(l => l.Count("Carol (Team #1) tracking Test Game has joined.")));
        Assert.Equal(new[] { 1, 1, 0 }, lines.Select(l => l.Count("Bob (Team #1) tracking Test Game has joined.")));
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_command_from_a_slot_without_text_switches_its_text_on_first_so_the_answer_reaches_it()
    {
        await using var server = Server("Alice", "Bob", "Carol");
        var manager = Manager();
        var slots = await ConnectAll(manager, server, "p1", "Alice", "Bob", "Carol");
        var (alice, bob, carol) = (new Lines(slots[0]), new Lines(slots[1]), new Lines(slots[2]));

        await slots[1].SayAsync("!hint Sword");

        await WaitFor(() => bob.Count("Command received: !hint Sword") == 1, "the command's answer to reach Bob");
        Assert.Equal(1, server.TagChanges);
        Assert.Equal(TextTags, server.TagsOf("Bob"));
        Assert.True(slots[1].ReceivesText);
        // The room heard the command (as chat) and the tag change: each slot shows each once. The answer is Bob's alone.
        await WaitFor(() => new[] { alice, bob, carol }.All(l => l.Count("Bob: !hint Sword") == 1 && l.Count("has changed tags") == 1),
            "the room's lines about the command to reach every slot");
        await Settle();
        Assert.Equal(new[] { 0, 1, 0 }, new[] { alice, bob, carol }.Select(l => l.CountOf<CommandResultLogMessage>()));
        Assert.Equal(new[] { 1, 1, 1 }, new[] { alice, bob, carol }.Select(l => l.Count("Bob: !hint Sword")));

        // A second command needs no change; chat from a slot without text needs none either.
        await slots[1].SayAsync("!hint Sword");
        await slots[2].SayAsync("hello from Carol");
        await WaitFor(() => bob.CountOf<CommandResultLogMessage>() == 2 && new[] { alice, bob, carol }.All(l => l.Count("Carol: hello from Carol") == 1),
            "the second command's answer and Carol's chat");
        Assert.Equal(1, server.TagChanges);
        Assert.Equal(1, manager.TextSwitchOns);
        Assert.False(slots[2].ReceivesText);
        Assert.Equal(QuietTags, server.TagsOf("Carol"));
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_line_meant_for_one_slot_stays_with_that_slot()
    {
        await using var server = Server("Alice", "Bob");
        var manager = Manager();
        var slots = await ConnectAll(manager, server, "p1", "Alice", "Bob");
        var (alice, bob) = (new Lines(slots[0]), new Lines(slots[1]));

        // Alice's world holds Bob's Sword: the server sends the hint's line to both slots' connections that receive
        // text (Alice's only). Bob, without text, gets the hint in his hint list (his window shows it from there).
        await server.AddHintsAsync(FakeArchipelagoServer.Hint(finder: 1, receiver: 2, location: 2000, item: 1000));

        await WaitFor(() => alice.CountOf<HintItemSendLogMessage>() == 1, "the hint's line to reach Alice");
        await Settle();
        Assert.Equal(0, bob.CountOf<HintItemSendLogMessage>());
        Assert.Equal(1, alice.Count("[Hint]: Bob's Sword is at Cave Chest in Alice's World. (unspecified)"));
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task When_the_text_connection_ends_another_slot_takes_over()
    {
        await using var server = Server("Alice", "Bob", "Carol");
        var manager = Manager();
        var slots = await ConnectAll(manager, server, "p1", "Alice", "Bob", "Carol");
        var (bob, carol) = (new Lines(slots[1]), new Lines(slots[2]));

        // Disconnected on purpose: another slot is switched on (the room is told, once).
        await Disconnect(manager, slots[0].Slot);
        await WaitFor(() => server.TagChanges == 1 && server.TextClients == 1, "another slot to take over the room's text");
        Assert.True(slots[1].ReceivesText ^ slots[2].ReceivesText);
        await server.BroadcastAsync(server.Chat("after Alice left"));
        await WaitFor(() => bob.Count("after Alice left") == 1 && carol.Count("after Alice left") == 1, "the room's lines to keep reaching both slots");

        // Alice comes back without text: her team has a text connection.
        var alice = (await Connect(manager, Login(server, "Alice"))).Slot!;
        Assert.Equal(QuietTags, server.TagsOf("Alice"));
        Assert.False(alice.ReceivesText);
        Assert.Equal(1, server.TextClients);

        // Dropped by the server: another takes over again, and the dropped slot comes back without text.
        string text = slots[1].ReceivesText ? "Bob" : "Carol";
        server.DropClient(text);
        await WaitFor(() => server.TagChanges == 2 && server.TextClients == 1, "another slot to take over after the drop", manager);
        await WaitFor(() => manager.IsLoggedIn(new SlotId("p1", text)) && server.TagsOf(text) != null, $"{text} to reconnect", manager);
        await Settle();
        Assert.Equal(QuietTags, server.TagsOf(text));
        Assert.Equal(1, server.TextClients);
        Assert.Equal(2, server.TagChanges);
        Assert.Equal(2, manager.TextSwitchOns);
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Replacing_the_text_slots_connection_keeps_its_text_without_a_tag_change()
    {
        await using var server = Server("Alice", "Bob");
        var manager = Manager();
        var slots = await ConnectAll(manager, server, "p1", "Alice", "Bob");
        var bob = new Lines(slots[1]);

        var alice = (await Connect(manager, Login(server, "Alice"))).Slot!;
        await WaitFor(() => server.ClosesReceived == 1, "Alice's old connection to close");
        Assert.True(alice.ReceivesText);
        Assert.Equal(TextTags, server.TagsOf("Alice"));
        Assert.Equal(1, server.TextClients);

        var lines = new Lines(alice);
        await server.BroadcastAsync(server.Chat("after the reconnect"));
        await WaitFor(() => lines.Count("after the reconnect") == 1 && bob.Count("after the reconnect") == 1, "the room's lines to reach both slots");
        await Settle();
        Assert.Equal(1, lines.Count("after the reconnect"));
        Assert.Equal(1, bob.Count("after the reconnect"));
        Assert.Equal(0, server.TagChanges);
        Assert.Equal(0, manager.TextSwitchOns);
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Deleting_a_multiworld_or_closing_Atlas_switches_no_text_on()
    {
        await using var server = Server("Alice", "Bob");
        var manager = Manager();
        await ConnectAll(manager, server, "deleted", "Alice", "Bob");
        await manager.ForgetProfileAsync("deleted").WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await WaitFor(() => server.ClosesReceived == 2, "the deleted multiworld's connections to close");
        // Counted where Atlas decides: a tag change sent just before its connection closes may never reach the server.
        Assert.Equal(0, manager.TextSwitchOns);

        await ConnectAll(manager, server, "closing", "Alice", "Bob");
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
        await WaitFor(() => server.ClosesReceived == 4, "Atlas's connections to close");
        await Settle();
        Assert.Equal(0, manager.TextSwitchOns);
        Assert.Equal(0, server.TagChanges);
    }

    [Fact]
    public async Task Each_team_has_its_own_text_connection_and_its_own_lines()
    {
        await using var server = Server("Alice", "Bob", "Xena", "Yuri");
        server.Teams["Xena"] = 1;
        server.Teams["Yuri"] = 1;
        var manager = Manager();
        var slots = await ConnectAll(manager, server, "p1", "Alice", "Bob", "Xena", "Yuri");

        // Xena's team wasn't known before she logged in (without text: her multiworld had a text connection); hers had
        // none, so she was switched on. Yuri, logging in after, joined her team without text.
        await WaitFor(() => server.TextClients == 2 && server.TagChanges == 1, "each team to have one text connection");
        Assert.Equal(1, manager.TextSwitchOns);
        Assert.Equal(new[] { TextTags, QuietTags, TextTags, QuietTags }, new[] { "Alice", "Bob", "Xena", "Yuri" }.Select(server.TagsOf));
        var lines = slots.Select(s => new Lines(s)).ToArray();

        await server.BroadcastToTeamAsync(1, FakeArchipelagoServer.ItemSend(1, 2, 1000, 2000));
        await server.BroadcastToTeamAsync(0, FakeArchipelagoServer.ItemSend(2, 1, 1000, 2000));
        await server.BroadcastAsync(server.Chat("to every team"));
        await WaitFor(() => lines.All(l => l.Count("to every team") == 1) && lines.Take(2).All(l => l.Count("Bob sent Sword to Alice") == 1) &&
                            lines.Skip(2).All(l => l.Count("Xena sent Sword to Yuri") == 1), "each team's lines to reach its slots");
        await Settle();
        Assert.Equal(new[] { 1, 1, 0, 0 }, lines.Select(l => l.Count("Bob sent Sword to Alice")));
        Assert.Equal(new[] { 0, 0, 1, 1 }, lines.Select(l => l.Count("Xena sent Sword to Yuri")));
        Assert.Equal(new[] { 1, 1, 1, 1 }, lines.Select(l => l.Count("to every team")));
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    private const string BobJoined = "Bob (Team #1) tracking Test Game has joined.";

    [Fact]
    public async Task A_slot_logging_in_without_text_gets_no_line_from_before_it_joined()
    {
        await using var server = Server("Alice", "Bob");
        var manager = Manager();
        var alice = new Lines((await ConnectAll(manager, server, "p1", "Alice"))[0]);
        // The room talks while the server holds Bob's login: the line reaches Atlas (through Alice's connection) while
        // Bob logs in, before he joined. His own connection would never have had it.
        server.HoldLogins();
        var connecting = Connect(manager, Login(server, "Bob"));
        await WaitFor(() => server.Count("Connect") == 2, "Bob's login to reach the server");
        await server.BroadcastAsync(server.Chat("before Bob joined"));
        await WaitFor(() => alice.Count("before Bob joined") == 1, "the line to reach Atlas");
        server.ReleaseLogins();
        var bob = new Lines((await connecting).Slot!);

        await WaitFor(() => alice.Count(BobJoined) == 1, "Bob's join line to reach Atlas");
        await server.BroadcastAsync(server.Chat("after Bob joined"));
        await WaitFor(() => bob.Count("after Bob joined") == 1, "the next line to reach Bob");
        await Settle();
        Assert.Equal(new[] { BobJoined, "Alice: after Bob joined" }, bob.All);
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_slot_logging_in_without_text_starts_at_its_own_join_line_even_when_that_comes_late()
    {
        await using var server = Server("Alice", "Bob", "Carol");
        var manager = Manager();
        var alice = new Lines((await ConnectAll(manager, server, "p1", "Alice"))[0]);
        // Bob's login is answered, but the room hears he joined only later: lines before his join line reach Atlas after
        // he logged in. They were still the room's before he joined, and so are other clients' join lines: another
        // tracker joining Carol's slot as Atlas joins, and Bob's own game joining his slot.
        server.HoldJoins();
        var bob = new Lines((await Connect(manager, Login(server, "Bob"))).Slot!);
        await server.BroadcastAsync(server.Chat("before Bob's join line"));
        await server.BroadcastAsync(server.Joined("Carol", "Tracker", "NoText"));
        await server.BroadcastAsync(server.Joined("Bob", "AP"));
        await WaitFor(() => alice.Count("Bob (Team #1) playing Test Game has joined.") == 1, "the lines to reach Atlas");

        server.ReleaseJoins();
        await WaitFor(() => alice.Count(BobJoined) == 1, "Bob's join line to reach Atlas");
        await server.BroadcastAsync(server.Chat("after Bob's join line"));
        await WaitFor(() => bob.Count("after Bob's join line") == 1, "the next line to reach Bob");
        await Settle();
        Assert.Equal(new[] { BobJoined, "Alice: after Bob's join line" }, bob.All);
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_connection_that_logged_in_with_text_carries_the_next_logins_join_line_at_once()
    {
        await using var server = Server("Alice", "Bob");
        var manager = Manager();
        // The room hears of joins only later, so no line has come through Alice's connection when Bob logs in. It
        // logged in with text, though: the server surely sends it Bob's join line.
        server.HoldJoins();
        var alice = new Lines((await ConnectAll(manager, server, "p1", "Alice"))[0]);
        var bob = new Lines((await Connect(manager, Login(server, "Bob"))).Slot!);
        await server.BroadcastAsync(server.Chat("before Bob's join line"));
        await WaitFor(() => alice.Count("before Bob's join line") == 1, "the line to reach Atlas");
        server.ReleaseJoins();
        await WaitFor(() => alice.Count(BobJoined) == 1, "Bob's join line to reach Atlas");
        await server.BroadcastAsync(server.Chat("after Bob's join line"));

        await WaitFor(() => bob.Count("after Bob's join line") == 1, "the next line to reach Bob");
        Assert.Equal(0, bob.Count("before Bob's join line"));
        Assert.Equal(1, bob.Count(BobJoined));
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_text_connection_switched_on_earlier_carries_a_later_logins_join_line()
    {
        await using var server = Server("Alice", "Bob", "Carol");
        var manager = Manager();
        var slots = await ConnectAll(manager, server, "p1", "Alice", "Bob");
        var bob = new Lines(slots[1]);
        // Alice leaves: Bob takes over the room's text, and the room's lines come through his connection.
        await Disconnect(manager, slots[0].Slot);
        await WaitFor(() => bob.Count("Bob (Team #1) has changed tags") == 1, "Bob's connection to receive the room's text");

        server.HoldJoins();
        var carol = new Lines((await Connect(manager, Login(server, "Carol"))).Slot!);
        await server.BroadcastAsync(server.Chat("before Carol's join line"));
        await WaitFor(() => bob.Count("before Carol's join line") == 1, "the line to reach Atlas");
        server.ReleaseJoins();
        await WaitFor(() => bob.Count("Carol (Team #1) tracking Test Game has joined.") == 1, "Carol's join line to reach Atlas");
        await server.BroadcastAsync(server.Chat("after Carol's join line"));

        await WaitFor(() => carol.Count("after Carol's join line") == 1, "the next line to reach Carol");
        Assert.Equal(new[] { "Carol (Team #1) tracking Test Game has joined.", "Alice: after Carol's join line" }, carol.All);
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Without_its_join_line_a_slot_shows_the_lines_it_held_once_its_wait_is_over()
    {
        await using var server = Server("Alice", "Bob");
        var manager = Manager(new SessionManagerOptions { LoginTimeout = TimeSpan.FromSeconds(2), JoinLineWait = TimeSpan.FromMilliseconds(300) });
        var alice = new Lines((await ConnectAll(manager, server, "p1", "Alice"))[0]);
        // As with a server that doesn't announce joins: Bob's join line never comes.
        server.HoldJoins();
        var bob = new Lines((await Connect(manager, Login(server, "Bob"))).Slot!);
        await server.BroadcastAsync(server.Chat("first"));
        await WaitFor(() => alice.Count("first") == 1, "the first line to reach Atlas");
        await Task.Delay(500, Ct);
        Assert.Equal(0, bob.Count("first"));

        await server.BroadcastAsync(server.Chat("second"));

        await WaitFor(() => bob.Count("second") == 1, "the next line to reach Bob");
        Assert.Equal(new[] { "Alice: first", "Alice: second" }, bob.All);
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_slot_waiting_for_its_join_line_gets_the_rooms_lines_when_its_text_connection_ends()
    {
        await using var server = Server("Alice", "Bob");
        // A long wait: only the text connection's change can end it here.
        var manager = Manager(new SessionManagerOptions { LoginTimeout = TimeSpan.FromSeconds(2), JoinLineWait = TimeSpan.FromMinutes(5) });
        var slots = await ConnectAll(manager, server, "p1", "Alice");
        var alice = new Lines(slots[0]);
        server.HoldJoins();
        var bob = new Lines((await Connect(manager, Login(server, "Bob"))).Slot!);
        await server.BroadcastAsync(server.Chat("held for Bob"));
        await WaitFor(() => alice.Count("held for Bob") == 1, "the line to reach Atlas");

        // Alice leaves: Bob takes over the room's text, and his join line won't come through his own connection.
        await Disconnect(manager, slots[0].Slot);
        await WaitFor(() => server.TextClients == 1 && server.TagChanges == 1, "Bob to take over the room's text");
        await server.BroadcastAsync(server.Chat("after Alice left"));

        await WaitFor(() => bob.Count("after Alice left") == 1, "the room's lines to reach Bob");
        // What he held is shown too, in order: when Atlas can't tell, it shows rather than drops.
        var lines = bob.All;
        Assert.True(lines.IndexOf("Alice: held for Bob") is >= 0 and var held && held < lines.IndexOf("Alice: after Alice left"), string.Join(" | ", lines));
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_slot_whose_text_connection_was_only_just_switched_on_gets_its_teams_lines()
    {
        // Every team has the same slots, as in a real room.
        await using var server = Server("Alice", "Bob", "Xena", "Yuri");
        server.Teams["Xena"] = 1;
        server.Teams["Yuri"] = 1;
        var manager = Manager();
        var alice = new Lines((await ConnectAll(manager, server, "p1", "Alice"))[0]);
        // Xena's team has no text connection, so hers is switched on once she's logged in. The server holds that switch
        // until it has let Yuri in, so his join line never comes through her connection: he can't wait for it. Her
        // switch then comes through her connection while his login is answered.
        server.HoldTagChanges();
        await ConnectAll(manager, server, "p1", "Xena");
        server.JoinLead = TimeSpan.FromSeconds(1);
        var connecting = Connect(manager, Login(server, "Yuri"));
        await WaitFor(() => alice.Count("Yuri (Team #2) tracking Test Game has joined.") == 1, "the room to hear Yuri joined");
        server.ReleaseTagChanges();
        var yuri = new Lines((await connecting).Slot!);

        await server.BroadcastToTeamAsync(1, FakeArchipelagoServer.ItemSend(1, 2, 1000, 2000));

        await WaitFor(() => yuri.Count("Xena sent Sword to Yuri") == 1, "his team's lines to reach Yuri");
        // Atlas can't tell whether the lines from while he logged in came before he joined: it shows them.
        Assert.Equal(1, yuri.Count("Xena (Team #2) has changed tags"));
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_slot_logging_in_without_text_gets_the_rooms_lines_from_while_it_logged_in()
    {
        await using var server = Server("Alice", "Bob");
        var manager = Manager();
        await ConnectAll(manager, server, "p1", "Alice");
        // The room hears Bob joined before his login is answered (the two travel on different connections, so either
        // order can happen): the line reaches Atlas through Alice's connection while Bob is still logging in.
        server.JoinLead = TimeSpan.FromMilliseconds(300);

        var bob = (await Connect(manager, Login(server, "Bob"))).Slot!;

        var lines = new Lines(bob);
        await Settle();
        Assert.Equal(1, lines.Count("Bob (Team #1) tracking Test Game has joined."));
        Assert.False(bob.ReceivesText);
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }
}
