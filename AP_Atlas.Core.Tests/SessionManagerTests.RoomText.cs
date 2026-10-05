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
