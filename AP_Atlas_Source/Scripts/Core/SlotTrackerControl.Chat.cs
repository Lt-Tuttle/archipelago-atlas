#nullable disable
using Godot;
using System;
using System.Collections.Generic;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Models;
using System.Linq;
using System.Threading.Tasks;
using Color = Godot.Color;

/// <summary>The text client: chat and server messages, rendered by this node and shown in the terminal pane.</summary>
public partial class SlotTrackerControl : MarginContainer
{
    // =====================================================================
    // Text Client (rendered by this node, mounted in the terminal pane)
    // =====================================================================

    private void BuildTextClientTab()
    {
        var vbox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(vbox);

        var filterMargin = new MarginContainer();
        filterMargin.AddThemeConstantOverride("margin_bottom", 10);
        vbox.AddChild(filterMargin);

        var filterVBox = new VBoxContainer();
        filterVBox.AddThemeConstantOverride("separation", 10);
        filterMargin.AddChild(filterVBox);

        // Row 1: Message Types
        var msgRow = new HBoxContainer();
        msgRow.AddThemeConstantOverride("separation", 10);
        msgRow.AddChild(new Label { Text = "Message Types: " });

        _filterChat = new Button { ToggleMode = true, Text = "Chat", ButtonPressed = true, CustomMinimumSize = new Godot.Vector2(100, 0) };
        _filterHints = new Button { ToggleMode = true, Text = "Hints", ButtonPressed = true, CustomMinimumSize = new Godot.Vector2(100, 0) };
        _filterSystem = new Button { ToggleMode = true, Text = "System", ButtonPressed = true, CustomMinimumSize = new Godot.Vector2(100, 0) };

        msgRow.AddChild(_filterChat);
        msgRow.AddChild(_filterHints);
        msgRow.AddChild(_filterSystem);
        filterVBox.AddChild(msgRow);

        // Row 2: Item Types
        var itemRow = new HBoxContainer();
        itemRow.AddThemeConstantOverride("separation", 10);
        itemRow.AddChild(new Label { Text = "Item Types: " });

        _filterProgression = new Button { ToggleMode = true, Text = "Progression", ButtonPressed = true, CustomMinimumSize = new Godot.Vector2(100, 0) };
        _filterUseful = new Button { ToggleMode = true, Text = "Useful", ButtonPressed = true, CustomMinimumSize = new Godot.Vector2(100, 0) };
        _filterFiller = new Button { ToggleMode = true, Text = "Filler", ButtonPressed = true, CustomMinimumSize = new Godot.Vector2(100, 0) };
        _filterTrap = new Button { ToggleMode = true, Text = "Traps", ButtonPressed = true, CustomMinimumSize = new Godot.Vector2(100, 0) };

        itemRow.AddChild(_filterProgression);
        itemRow.AddChild(_filterUseful);
        itemRow.AddChild(_filterFiller);
        itemRow.AddChild(_filterTrap);
        filterVBox.AddChild(itemRow);

        _filterHints.Toggled += (b) => RedrawChat();
        _filterProgression.Toggled += (b) => RedrawChat();
        _filterUseful.Toggled += (b) => RedrawChat();
        _filterFiller.Toggled += (b) => RedrawChat();
        _filterTrap.Toggled += (b) => RedrawChat();
        _filterChat.Toggled += (b) => RedrawChat();
        _filterSystem.Toggled += (b) => RedrawChat();

        _chatScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        vbox.AddChild(_chatScroll);

        _chatVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _chatVBox.AddThemeConstantOverride("separation", 0);
        _chatScroll.AddChild(_chatVBox);

        var inputHbox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(inputHbox);

        _chatInput = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "Type a command (e.g. !help) or message..." };
        _chatInput.TextSubmitted += OnChatSubmitted;
        inputHbox.AddChild(_chatInput);

        var sendBtn = new Button { Text = "Send" };
        sendBtn.Pressed += () => OnChatSubmitted(_chatInput.Text);
        inputHbox.AddChild(sendBtn);
    }

    /// <summary>The newest text client line shown (the model numbers its lines).</summary>
    private long _shownSequence;

    /// <summary>Shows the model's lines this view hasn't shown yet; announces new special items (not early ones).</summary>
    private AP_Atlas.UI.ViewRefresh _chatRefresh;
    private long _announcedSequence;

    /// <summary>A frame's share of drawing lines; the rest continue next frame, so a flood (a release) never holds one up.</summary>
    private static readonly long ChatSliceTicks = System.Diagnostics.Stopwatch.Frequency * 6 / 1000;

    /// <summary>New lines in the model: special items are announced at once (whichever slot is showing); the lines are drawn when the panel shows.</summary>
    private void OnNewChatLines()
    {
        foreach (var entry in Model.Chat)
        {
            if (entry.Sequence <= _announcedSequence) continue;
            _announcedSequence = entry.Sequence;
            if (entry.IsSystemMessage || entry.Early) continue;
            if (entry.Hint != null) AnnounceSpecialHint(entry.Hint);
            else AnnounceSpecialItem(entry.APMessage);
        }
        _chatRefresh?.Request();
    }

    // Characters given to the text client's lines in this frame's slice: Godot lays them out after the slice, at the end of
    // the frame, so the slice counts them as well as its own time.
    private int _sliceChars;

    /// <summary>Draws the lines not shown yet, for about 6 ms or <see cref="MaxChatLine"/> characters; the rest continue next frame.</summary>
    private void ShowNewChatLinesNow()
    {
        using var __perf = AP_Atlas.Core.PerfMonitor.Measure($"[{_slotName}] Text client lines");
        long until = System.Diagnostics.Stopwatch.GetTimestamp() + ChatSliceTicks;
        bool drew = false;
        _sliceChars = 0;
        foreach (var entry in Model.Chat)
        {
            if (entry.Sequence <= _shownSequence) continue;
            if (drew && (System.Diagnostics.Stopwatch.GetTimestamp() > until || _sliceChars >= MaxChatLine))
            {
                _chatRefresh.ContinueNextFrame();
                break;
            }
            _shownSequence = entry.Sequence;
            drew = true;
            if (entry.IsSystemMessage)
            {
                if (_filterSystem == null || _filterSystem.ButtonPressed) ShowSystemLine(entry.SystemMessage);
                continue;
            }
            if (entry.Hint != null)
            {
                if (_filterHints == null || _filterHints.ButtonPressed) AppendHintToChat(entry.Hint);
                continue;
            }
            if (!ShouldFilterMessage(entry.APMessage)) AppendMessageToChat(entry.APMessage);
        }
        if (drew) ScrollChatToBottom();
    }

    /// <summary>Renders one of Atlas's own lines (BBCode).</summary>
    private void ShowSystemLine(string bbcodeText)
    {
        _sliceChars += bbcodeText.Length;
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var style = new StyleBoxFlat
        {
            BgColor = _nextChatAltBg ? new Godot.Color("#2a2a2a") : new Godot.Color("#1e1e1e"),
            ContentMarginLeft = 5,
            ContentMarginRight = 5,
            ContentMarginTop = 2,
            ContentMarginBottom = 2
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var lbl = new AP_Atlas.UI.SafeRichText
        {
            FitContent = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Markup = bbcodeText
        };

        int fontSize = _appSettings.ConsoleFontSize;
        lbl.AddThemeFontSizeOverride("normal_font_size", fontSize);
        lbl.AddThemeFontSizeOverride("mono_font_size", fontSize);

        panel.AddChild(lbl);
        _chatVBox.AddChild(panel);

        _nextChatAltBg = !_nextChatAltBg;

        if (_chatVBox.GetChildCount() > 1000)
        {
            _chatVBox.GetChild(0).QueueFree();
        }
    }

    /// <summary>Draws every line again (a filter changed), a slice per frame like new lines.</summary>
    private void RedrawChat()
    {
        foreach (Node child in _chatVBox.GetChildren()) child.QueueFree();
        _nextChatAltBg = false;
        _shownSequence = 0;
        _chatRefresh?.Request();
    }

    private bool ShouldFilterMessage(LogMessage msg)
    {
        if (msg is HintItemSendLogMessage && (_filterHints == null || !_filterHints.ButtonPressed)) return true;
        if (msg is ChatLogMessage && (_filterChat == null || !_filterChat.ButtonPressed)) return true;

        if (msg is ItemSendLogMessage itemMsg)
        {
            var itemPart = itemMsg.Parts.OfType<Archipelago.MultiClient.Net.MessageLog.Parts.ItemMessagePart>().FirstOrDefault();
            if (itemPart != null)
            {
                if (itemPart.Flags.HasFlag(ItemFlags.Advancement) && (_filterProgression == null || !_filterProgression.ButtonPressed)) return true;
                else if (itemPart.Flags.HasFlag(ItemFlags.NeverExclude) && (_filterUseful == null || !_filterUseful.ButtonPressed)) return true;
                else if (itemPart.Flags.HasFlag(ItemFlags.Trap) && (_filterTrap == null || !_filterTrap.ButtonPressed)) return true;
                else if (!itemPart.Flags.HasFlag(ItemFlags.Advancement) && !itemPart.Flags.HasFlag(ItemFlags.NeverExclude) && !itemPart.Flags.HasFlag(ItemFlags.Trap) && (_filterFiller == null || !_filterFiller.ButtonPressed)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The most of one line the text client shows, in characters, and about the most new text it lays out in one frame
    /// (at least one line; the rest waits for the next frame). A longer line (another player's chat message of a megabyte,
    /// say) is cut, with a note of how much: laying a line out takes the window about 20 ms per thousand characters. The
    /// longest lines a server sends, !players in a room of a few hundred players, are cut too.
    /// </summary>
    internal const int MaxChatLine = 4_000;

    private void AppendMessageToChat(LogMessage msg)
    {
        var text = new System.Text.StringBuilder();
        int room = MaxChatLine;
        long cut = 0;
        foreach (var part in msg.Parts)
        {
            string shown = part.Text ?? "";
            if (shown.Length > room)
            {
                cut += shown.Length - room;
                shown = shown[..room];
            }
            room -= shown.Length;
            if (shown.Length == 0) continue;
            text.Append(part switch
            {
                Archipelago.MultiClient.Net.MessageLog.Parts.ItemMessagePart item => ItemBBCode(item.Player, item.ItemId, shown, item.Flags),
                Archipelago.MultiClient.Net.MessageLog.Parts.LocationMessagePart location => LocationBBCode(location.Player, location.LocationId, shown),
                Archipelago.MultiClient.Net.MessageLog.Parts.PlayerMessagePart player => PlayerBBCode(player.SlotId, shown),
                // A colour the server set (a hint's status, an entrance); a background colour isn't the text's.
                _ => TextBBCode(shown, part.IsBackgroundColor ? null : part.PaletteColor)
            });
        }
        if (cut > 0) text.Append(AP_Atlas.Core.Bbcode.Colored($"… ({cut:N0} more characters)", "gray"));
        AppendChatLine(text.ToString());
    }

    /// <summary>A hint shown from the slot's hint list, worded and coloured as the server's hint lines are.</summary>
    private void AppendHintToChat(Hint hint)
    {
        string receiverGame = Session.Players.GetPlayerInfo(hint.ReceivingPlayer)?.Game;
        string finderGame = Session.Players.GetPlayerInfo(hint.FindingPlayer)?.Game;
        var text = new System.Text.StringBuilder()
            .Append(TextBBCode("[Hint]: ", null))
            .Append(PlayerBBCode(hint.ReceivingPlayer, PlayerAlias(hint.ReceivingPlayer)))
            .Append(TextBBCode("'s ", null))
            .Append(ItemBBCode(hint.ReceivingPlayer, hint.ItemId, Session.Items.GetItemName(hint.ItemId, receiverGame) ?? $"Item: {hint.ItemId}", hint.ItemFlags))
            .Append(TextBBCode(" is at ", null))
            .Append(LocationBBCode(hint.FindingPlayer, hint.LocationId, Session.Locations.GetLocationNameFromId(hint.LocationId, finderGame) ?? $"Location: {hint.LocationId}"))
            .Append(TextBBCode(" in ", null))
            .Append(PlayerBBCode(hint.FindingPlayer, PlayerAlias(hint.FindingPlayer)));
        if (string.IsNullOrEmpty(hint.Entrance)) text.Append(TextBBCode("'s World", null));
        else text.Append(TextBBCode("'s World at ", null)).Append(TextBBCode(hint.Entrance, Archipelago.MultiClient.Net.Colors.PaletteColor.Blue));
        text.Append(TextBBCode(". ", null)).Append(TextBBCode(HintStatusText(hint.Status), Archipelago.MultiClient.Net.Colors.ColorUtils.GetColor(hint.Status)));
        AppendChatLine(text.ToString());
    }

    /// <summary>A hint's status as the server words it.</summary>
    private static string HintStatusText(HintStatus status) => status switch
    {
        HintStatus.Found => "(found)",
        HintStatus.Unspecified => "(unspecified)",
        HintStatus.NoPriority => "(no priority)",
        HintStatus.Avoid => "(avoid)",
        HintStatus.Priority => "(priority)",
        _ => "(unknown)"
    };

    /// <summary>A player's name as the server shows it (their alias).</summary>
    private string PlayerAlias(int slot)
    {
        string alias = Session.Players.GetPlayerAlias(slot);
        return string.IsNullOrEmpty(alias) ? $"Slot: {slot}" : alias;
    }

    private static string EscapeBBCode(string text) => AP_Atlas.Core.Bbcode.Escape(text);

    /// <summary>Text in the colour the server gave it (white when none).</summary>
    private static string TextBBCode(string text, Archipelago.MultiClient.Net.Colors.PaletteColor? color) =>
        $"[color={color?.ToString().ToLowerInvariant() ?? "white"}]{EscapeBBCode(text)}[/color]";

    /// <summary>
    /// A player's name, linked to them. This slot's own name stands out; it's decided here, by slot number, because a
    /// line may have come through another slot's connection. (Lines naming players are their team's alone.)
    /// </summary>
    private string PlayerBBCode(int slot, string name) =>
        $"[url=P|{slot}][color={(slot == PlayerSlot ? "magenta" : "yellow")}]{EscapeBBCode(name)}[/color][/url]";

    /// <summary>An item's name, coloured by kind, marked when special or flagged, and linked to it.</summary>
    private string ItemBBCode(int player, long itemId, string name, ItemFlags flags)
    {
        string color = flags.HasFlag(ItemFlags.Advancement) ? "plum" : flags.HasFlag(ItemFlags.NeverExclude) ? "slateblue" : flags.HasFlag(ItemFlags.Trap) ? "salmon" : "cyan";
        string prefix = "", text = EscapeBBCode(name);
        // Special items (per game) stand out wherever they're mentioned.
        string itemGame = Session.Players.GetPlayerInfo(player)?.Game;
        if (AP_Atlas.Core.Annotations.IsSpecialItem(itemGame, name))
        {
            prefix = $"[color=#{AP_Atlas.Core.Annotations.SpecialColor.ToHtml(false)}]◆[/color]";
            text = $"[bgcolor=#{AP_Atlas.Core.Annotations.SpecialBg.ToHtml(false)}]{text}[/bgcolor]";
        }
        if (player == PlayerSlot)
        {
            int flag = AP_Atlas.Core.Annotations.GetFlag(AnnotationKey, AP_Atlas.Core.Annotations.ItemKey(itemId));
            if (flag > 0) prefix = $"[color=#{AP_Atlas.Core.Annotations.FlagColor(flag).ToHtml(false)}]●[/color]" + prefix;
        }
        return $"[url=I|{player}|{itemId}]{prefix}[color={color}]{text}[/color][/url]";
    }

    /// <summary>A location's name, marked when this slot flagged it, and linked to it.</summary>
    private string LocationBBCode(int player, long locationId, string name)
    {
        string prefix = "";
        if (player == PlayerSlot)
        {
            int flag = AP_Atlas.Core.Annotations.GetFlag(AnnotationKey, AP_Atlas.Core.Annotations.LocationKey(locationId));
            if (flag > 0) prefix = $"[color=#{AP_Atlas.Core.Annotations.FlagColor(flag).ToHtml(false)}]●[/color]";
        }
        return $"[url=L|{player}|{locationId}]{prefix}[color=green]{EscapeBBCode(name)}[/color][/url]";
    }

    /// <summary>Adds a line (BBCode with links) to the text client.</summary>
    private void AppendChatLine(string text)
    {
        _sliceChars += text.Length;
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var style = new StyleBoxFlat
        {
            BgColor = _nextChatAltBg ? new Godot.Color("#2a2a2a") : new Godot.Color("#1e1e1e"),
            ContentMarginLeft = 5,
            ContentMarginRight = 5,
            ContentMarginTop = 2,
            ContentMarginBottom = 2
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var lbl = new AP_Atlas.UI.SafeRichText
        {
            FitContent = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MetaUnderlined = false,
            Markup = text
        };
        lbl.MetaClicked += meta => OnChatLinkClicked(meta.AsString());
        lbl.MetaHoverStarted += _ => { lbl.MetaUnderlined = true; lbl.MouseDefaultCursorShape = CursorShape.PointingHand; };
        lbl.MetaHoverEnded += _ => { lbl.MetaUnderlined = false; lbl.MouseDefaultCursorShape = CursorShape.Arrow; };

        int fontSize = _appSettings.ConsoleFontSize;
        lbl.AddThemeFontSizeOverride("normal_font_size", fontSize);
        lbl.AddThemeFontSizeOverride("mono_font_size", fontSize);

        panel.AddChild(lbl);
        _chatVBox.AddChild(panel);

        _nextChatAltBg = !_nextChatAltBg;

        if (_chatVBox.GetChildCount() > 1000)
        {
            _chatVBox.GetChild(0).QueueFree();
        }
    }

    /// <summary>Chat names are links: "I|player|itemId", "L|player|locationId", "P|player".</summary>
    private void OnChatLinkClicked(string meta)
    {
        var parts = meta.Split('|');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int player)) return;
        switch (parts[0])
        {
            case "I" when parts.Length == 3 && long.TryParse(parts[2], out long itemId):
                string game = Session.Players.GetPlayerInfo(player)?.Game;
                Inspect(ItemTargetFor(player, itemId, Session.Items.GetItemName(itemId, game)));
                break;
            case "L" when parts.Length == 3 && long.TryParse(parts[2], out long locId):
                Inspect(LocationTargetFor(player, locId));
                break;
            case "P":
                Inspect(PlayerTarget(player));
                break;
        }
    }

    // Several connected slots of one multiworld all receive the same broadcast; toast it once.
    // Toasts shown in the last few seconds, and when (on the steady clock).
    private static readonly Dictionary<string, DateTime> _recentSpecialToasts = new();

    /// <summary>Toasts when a special item is received, found by anyone, or hinted.</summary>
    private void AnnounceSpecialItem(LogMessage msg)
    {
        if (msg is not ItemSendLogMessage send || send.Item == null) return;
        string game = send.Item.ItemGame;
        string itemName = send.Item.ItemName;
        if (!AP_Atlas.Core.Annotations.IsSpecialItem(game, itemName)) return;

        string receiver = send.Receiver?.Alias ?? send.Receiver?.Name ?? "someone";
        string sender = send.Sender?.Alias ?? send.Sender?.Name ?? "someone";
        string location = send.Item.LocationDisplayName ?? send.Item.LocationName ?? "a location";
        string text = msg is HintItemSendLogMessage
            ? $"◆ Hinted: {receiver}'s {itemName} is at {location} ({sender}'s world)"
            : Model.IsThisSlot(send.Receiver)
                ? $"◆ You received {itemName} from {sender}"
                : $"◆ {sender} found {receiver}'s {itemName}";
        ToastSpecialOnce($"{send.Item.LocationId}|{send.Sender?.Slot}|{msg.GetType().Name}", text);
    }

    /// <summary>Toasts a new hint shown from the hint list, when its item is special.</summary>
    private void AnnounceSpecialHint(Hint hint)
    {
        string game = Session.Players.GetPlayerInfo(hint.ReceivingPlayer)?.Game;
        string itemName = Session.Items.GetItemName(hint.ItemId, game);
        if (!AP_Atlas.Core.Annotations.IsSpecialItem(game, itemName)) return;
        string finderGame = Session.Players.GetPlayerInfo(hint.FindingPlayer)?.Game;
        string location = Session.Locations.GetLocationNameFromId(hint.LocationId, finderGame) ?? "a location";
        // Keyed as a hint's line is, so the same hint isn't toasted again by a slot that got its line.
        ToastSpecialOnce($"{hint.LocationId}|{hint.FindingPlayer}|{nameof(HintItemSendLogMessage)}",
            $"◆ Hinted: {PlayerAlias(hint.ReceivingPlayer)}'s {itemName} is at {location} ({PlayerAlias(hint.FindingPlayer)}'s world)");
    }

    private void ToastSpecialOnce(string key, string text)
    {
        var now = AP_Atlas.Core.SteadyClock.UtcNow;
        foreach (var stale in _recentSpecialToasts.Where(kv => (now - kv.Value).TotalSeconds > 10).Select(kv => kv.Key).ToList())
            _recentSpecialToasts.Remove(stale);
        if (_recentSpecialToasts.ContainsKey(key)) return;
        _recentSpecialToasts[key] = now;
        ShowToast?.Invoke(text, AP_Atlas.Core.Annotations.SpecialColor);
    }

    private void ScrollChatToBottom() => AP_Atlas.Core.Async.Fire(ScrollChatToBottomAsync(), "scrolling the chat", tellUser: false);

    private async Task ScrollChatToBottomAsync()
    {
        if (!IsInsideTree()) return;
        await ToSignal(GetTree(), "process_frame");
        if (!GodotObject.IsInstanceValid(_chatScroll)) return;
        var scrollBar = _chatScroll.GetVScrollBar();
        _chatScroll.ScrollVertical = (int)scrollBar.MaxValue;
    }

    private void OnChatSubmitted(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (Session == null || !Session.Socket.Connected) return;

        _chatInput.Text = "";
        AP_Atlas.Core.Async.Fire(Model.SayAsync(text), "sending your chat message");
    }
}
