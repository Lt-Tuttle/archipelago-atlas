using System;
using System.Collections.Generic;
using System.Linq;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.MessageLog.Parts;

namespace AP_Atlas.Core.Connections;

/// <summary>What a line of the room's text is, for the text client's filters.</summary>
public enum ChatLineKind
{
    /// <summary>What a player (or the server's console) says.</summary>
    Chat,
    /// <summary>A hint about an item.</summary>
    Hint,
    /// <summary>The room's own bookkeeping: joins, leaves, tag changes, commands and their answers, the tutorial.</summary>
    System,
    /// <summary>What happens in the game for everyone: a goal, a release, a collect, a countdown. Always shown.</summary>
    Event,
    /// <summary>An item found and sent.</summary>
    Item,
    /// <summary>Anything else the server says. Always shown.</summary>
    Other
}

/// <summary>
/// The text client's filters: which kind each line of the room's text is, and whether the filters the user turned off
/// hide it. The filters are named by keys (<see cref="Chat"/>, <see cref="Hints"/>, …), kept in the settings.
/// </summary>
public static class ChatLines
{
    public const string Chat = "chat", Hints = "hints", System = "system", Progression = "progression", Useful = "useful", Filler = "filler", Traps = "traps";

    /// <summary>The kind of a line. A chat line that starts with "!" is a command the room hears, so it's System.</summary>
    public static ChatLineKind KindOf(LogMessage message) => message switch
    {
        HintItemSendLogMessage => ChatLineKind.Hint,
        ItemSendLogMessage => ChatLineKind.Item,
        ChatLogMessage chat when (chat.Message ?? "").StartsWith("!", StringComparison.Ordinal) => ChatLineKind.System,
        ChatLogMessage or ServerChatLogMessage => ChatLineKind.Chat,
        JoinLogMessage or LeaveLogMessage or TagsChangedLogMessage or CommandResultLogMessage or AdminCommandResultLogMessage or TutorialLogMessage => ChatLineKind.System,
        GoalLogMessage or ReleaseLogMessage or CollectLogMessage or CountdownLogMessage => ChatLineKind.Event,
        _ => ChatLineKind.Other
    };

    /// <summary>The filter an item line's item falls under (progression, useful, traps or filler); null when the line names no item.</summary>
    public static string? ItemKey(ItemSendLogMessage message)
    {
        var item = message.Parts.OfType<ItemMessagePart>().FirstOrDefault();
        if (item == null) return null;
        if (item.Flags.HasFlag(ItemFlags.Advancement)) return Progression;
        if (item.Flags.HasFlag(ItemFlags.NeverExclude)) return Useful;
        if (item.Flags.HasFlag(ItemFlags.Trap)) return Traps;
        return Filler;
    }

    /// <summary>
    /// Whether a line shows with these filters off. A hint line shows while Hints is on and its item's kind is on too (as
    /// an item line would); goals, releases, collects, countdowns and anything else always show.
    /// </summary>
    public static bool Shows(LogMessage message, ICollection<string> off)
    {
        bool ItemShows(ItemSendLogMessage item) => ItemKey(item) is not { } key || !off.Contains(key);
        return KindOf(message) switch
        {
            ChatLineKind.Chat => !off.Contains(Chat),
            ChatLineKind.System => !off.Contains(System),
            ChatLineKind.Hint => !off.Contains(Hints) && ItemShows((ItemSendLogMessage)message),
            ChatLineKind.Item => ItemShows((ItemSendLogMessage)message),
            _ => true
        };
    }
}
