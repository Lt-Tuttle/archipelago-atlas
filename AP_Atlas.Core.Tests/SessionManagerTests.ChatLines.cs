using AP_Atlas.Core.Connections;
using AP_Atlas.Core.Testing;
using Archipelago.MultiClient.Net.MessageLog.Messages;

namespace AP_Atlas.Core.Tests;

/// <summary>The text client's filters: each kind of the room's lines, as the connection library reads a real server's, and what each filter hides.</summary>
public sealed partial class SessionManagerTests
{
    [Fact]
    public async Task Each_line_of_the_room_has_its_kind_and_the_filters_hide_only_their_own()
    {
        await using var server = Server("Alice", "Bob");
        var manager = Manager();
        var slots = await ConnectAll(manager, server, "p1", "Alice", "Bob");
        var alice = new Lines(slots[0]);

        await server.BroadcastAsync(server.Joined("Carol", "Tracker"), server.Left("Carol"), server.TagsChangedLine("Carol", new[] { "Tracker" }, new[] { "Tracker", "NoText" }),
            server.Goal(2), server.Released(2), server.Collected(2), FakeArchipelagoServer.Countdown(3), server.Chat("hello"), server.Chat("!alias Lamp"),
            FakeArchipelagoServer.ItemSend(2, 1, 1000, 2000, flags: 0), FakeArchipelagoServer.ItemSend(2, 1, 1000, 2001, flags: 1),
            FakeArchipelagoServer.ItemSend(2, 1, 1000, 2002, flags: 4), FakeArchipelagoServer.HintLine(FakeArchipelagoServer.Hint(finder: 2, receiver: 1, location: 2003, item: 1000, flags: 0)));
        await slots[0].SayAsync("!hint Sword");
        await WaitFor(() => alice.Count("Command received") == 1 && alice.Count("[Hint]") == 1 && alice.Count("countdown") == 1, "the room's lines");

        var join = alice.Find("Carol (Team #1) tracking");
        var leave = alice.Find("has left the game");
        var tags = alice.Find("has changed tags");
        var goal = alice.Find("has completed their goal");
        var release = alice.Find("has released");
        var collect = alice.Find("has collected");
        var countdown = alice.Find("countdown");
        var chat = alice.Find("Alice: hello");
        var echo = alice.Find("Alice: !alias Lamp");
        var answer = alice.Find("Command received");
        // The three item lines in the order sent: filler, progression, trap (the hint line is an item line too: left out).
        var items = alice.Of<ItemSendLogMessage>().Where(line => line is not HintItemSendLogMessage).ToList();
        Assert.Equal(3, items.Count);
        var filler = items[0];
        var hint = alice.Find("[Hint]");

        Assert.Equal(ChatLineKind.System, ChatLines.KindOf(join));
        Assert.Equal(ChatLineKind.System, ChatLines.KindOf(leave));
        Assert.Equal(ChatLineKind.System, ChatLines.KindOf(tags));
        Assert.Equal(ChatLineKind.System, ChatLines.KindOf(answer));
        Assert.IsType<CommandResultLogMessage>(answer);
        Assert.Equal(ChatLineKind.System, ChatLines.KindOf(echo));
        Assert.Equal(ChatLineKind.Event, ChatLines.KindOf(goal));
        Assert.Equal(ChatLineKind.Event, ChatLines.KindOf(release));
        Assert.Equal(ChatLineKind.Event, ChatLines.KindOf(collect));
        Assert.Equal(ChatLineKind.Event, ChatLines.KindOf(countdown));
        Assert.Equal(ChatLineKind.Chat, ChatLines.KindOf(chat));
        Assert.Equal(ChatLineKind.Item, ChatLines.KindOf(filler));
        Assert.Equal(ChatLineKind.Hint, ChatLines.KindOf(hint));

        // System off: the room's bookkeeping goes; the game's events and what players say stay.
        var systemOff = new HashSet<string> { ChatLines.System };
        foreach (var line in new[] { join, leave, tags, answer, echo }) Assert.False(ChatLines.Shows(line, systemOff), line.ToString());
        foreach (var line in new[] { goal, release, collect, countdown, chat, filler, hint }) Assert.True(ChatLines.Shows(line, systemOff), line.ToString());

        // Chat off hides what players say, not a command they type (System decides that).
        var chatOff = new HashSet<string> { ChatLines.Chat };
        Assert.False(ChatLines.Shows(chat, chatOff));
        Assert.True(ChatLines.Shows(echo, chatOff));

        // An item's kind filters item lines and hint lines alike (as before); Hints off hides only hint lines.
        Assert.Equal(ChatLines.Filler, ChatLines.ItemKey((ItemSendLogMessage)filler));
        Assert.Equal(ChatLines.Progression, ChatLines.ItemKey(items[1]));
        Assert.Equal(ChatLines.Traps, ChatLines.ItemKey(items[2]));
        var fillerOff = new HashSet<string> { ChatLines.Filler };
        Assert.False(ChatLines.Shows(filler, fillerOff));
        Assert.False(ChatLines.Shows(hint, fillerOff));
        var hintsOff = new HashSet<string> { ChatLines.Hints };
        Assert.False(ChatLines.Shows(hint, hintsOff));
        Assert.True(ChatLines.Shows(filler, hintsOff));
        Assert.True(ChatLines.Shows(goal, new HashSet<string> { ChatLines.Chat, ChatLines.Hints, ChatLines.System, ChatLines.Progression, ChatLines.Useful, ChatLines.Filler, ChatLines.Traps }));
        await manager.CloseAllAsync(TimeSpan.FromSeconds(5));
    }
}
