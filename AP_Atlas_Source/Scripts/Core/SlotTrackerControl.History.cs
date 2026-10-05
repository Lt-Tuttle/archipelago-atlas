#nullable disable
using Godot;
using System;
using System.Collections.Generic;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Packets;
using Archipelago.MultiClient.Net.Models;
using System.Linq;
using System.Threading.Tasks;
using Color = Godot.Color;

/// <summary>The Item History view.</summary>
public partial class SlotTrackerControl : MarginContainer
{
    // =====================================================================
    // Item History view
    // =====================================================================

    private void BuildItemHistoryView()
    {
        _historyView = new MarginContainer { Name = "Item History", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _historyView.AddThemeConstantOverride("margin_left", 10);
        _historyView.AddThemeConstantOverride("margin_top", 10);
        _historyView.AddThemeConstantOverride("margin_right", 10);
        _historyView.AddThemeConstantOverride("margin_bottom", 10);

        var vbox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _historyView.AddChild(vbox);

        var filterBar = new HBoxContainer();
        filterBar.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(filterBar);

        filterBar.AddChild(new Label { Text = "Filters: " });
        _filterItemsProgression = new Button { ToggleMode = true, Text = "Progression", ButtonPressed = true, CustomMinimumSize = new Godot.Vector2(100, 0) };
        _filterItemsUseful = new Button { ToggleMode = true, Text = "Useful", ButtonPressed = true, CustomMinimumSize = new Godot.Vector2(100, 0) };
        _filterItemsFiller = new Button { ToggleMode = true, Text = "Filler", ButtonPressed = true, CustomMinimumSize = new Godot.Vector2(100, 0) };
        _filterItemsTrap = new Button { ToggleMode = true, Text = "Traps", ButtonPressed = true, CustomMinimumSize = new Godot.Vector2(100, 0) };
        filterBar.AddChild(_filterItemsProgression);
        filterBar.AddChild(_filterItemsUseful);
        filterBar.AddChild(_filterItemsFiller);
        filterBar.AddChild(_filterItemsTrap);
        _filterItemsMarked = new Button { ToggleMode = true, Text = "Flagged / special only", TooltipText = "Show only items you flagged, noted or marked special." };
        filterBar.AddChild(_filterItemsMarked);
        filterBar.AddChild(new VSeparator { CustomMinimumSize = new Godot.Vector2(10, 0) });

        filterBar.AddChild(new Label { Text = "Search: " });
        _itemSearchBox = new LineEdit { PlaceholderText = "Search items, senders, locations...", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true };
        filterBar.AddChild(_itemSearchBox);
        filterBar.AddChild(new VSeparator());

        filterBar.AddChild(new Label { Text = "Sort: " });
        _optHistorySort = new OptionButton();
        _optHistorySort.AddItem("Chronological (Oldest First)");
        _optHistorySort.AddItem("Chronological (Newest First)");
        _optHistorySort.AddItem("Alphabetical");
        filterBar.AddChild(_optHistorySort);

        _filterItemsProgression.Toggled += (b) => UpdateItemHistoryUI();
        _filterItemsUseful.Toggled += (b) => UpdateItemHistoryUI();
        _filterItemsFiller.Toggled += (b) => UpdateItemHistoryUI();
        _filterItemsTrap.Toggled += (b) => UpdateItemHistoryUI();
        _filterItemsMarked.Toggled += (b) => UpdateItemHistoryUI();
        _itemSearchBox.TextChanged += (txt) => UpdateItemHistoryUI();
        _optHistorySort.ItemSelected += (idx) => UpdateItemHistoryUI();

        vbox.AddChild(new HSeparator { CustomMinimumSize = new Godot.Vector2(0, 10) });

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(split);

        var leftVBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(leftVBox);
        _collectedHeaderLabel = new Label { Text = "Collected (0)" };
        leftVBox.AddChild(_collectedHeaderLabel);
        _itemHistoryTree = new Tree { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, Columns = 4, ColumnTitlesVisible = true, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row };
        _itemHistoryTree.AddThemeConstantOverride("v_separation", 6);
        _itemHistoryTree.SetColumnTitle(0, "Order");
        _itemHistoryTree.SetColumnTitle(1, "Item");
        _itemHistoryTree.SetColumnTitle(2, "From");
        _itemHistoryTree.SetColumnTitle(3, "Location");
        _itemHistoryTree.SetColumnExpandRatio(0, 1);
        _itemHistoryTree.SetColumnExpandRatio(1, 4);
        _itemHistoryTree.SetColumnExpandRatio(2, 3);
        _itemHistoryTree.SetColumnExpandRatio(3, 4);
        _itemHistoryTree.SetColumnCustomMinimumWidth(0, 45);
        _itemHistoryTree.SetColumnCustomMinimumWidth(1, 140);
        _itemHistoryTree.SetColumnCustomMinimumWidth(2, 100);
        _itemHistoryTree.SetColumnCustomMinimumWidth(3, 140);
        _itemHistoryTree.CreateItem();
        AP_Atlas.Core.TreePicks.Hook(_itemHistoryTree, (selected, column) =>
        {
            var meta = selected.GetMetadata(0);
            if (meta.VariantType != Variant.Type.Int) return;
            int index = meta.AsInt32();
            var received = Session.Items.AllItemsReceived;
            if (index < 0 || index >= received.Count) return;
            // The column clicked decides what to inspect: the sender, the location it came from, or the item.
            var item = received[index];
            if (column == 2 && item.Player != null) Inspect(PlayerTarget(item.Player.Slot));
            else if (column == 3 && item.Player != null && item.LocationId > 0) Inspect(LocationTargetFor(item.Player.Slot, item.LocationId));
            else Inspect(ReceivedItemTarget(index));
        });
        leftVBox.AddChild(_itemHistoryTree);

        var rightVBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(rightVBox);
        _uncollectedHeaderLabel = new Label { Text = "Not Yet Collected (0)" };
        rightVBox.AddChild(_uncollectedHeaderLabel);
        _uncollectedTree = new Tree { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, Columns = 3, ColumnTitlesVisible = true, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row };
        _uncollectedTree.AddThemeConstantOverride("v_separation", 6);
        _uncollectedTree.SetColumnTitle(0, "Item");
        _uncollectedTree.SetColumnTitle(1, "Classification");
        _uncollectedTree.SetColumnTitle(2, "Qty");
        _uncollectedTree.SetColumnExpandRatio(0, 5);
        _uncollectedTree.SetColumnExpandRatio(1, 3);
        _uncollectedTree.SetColumnExpandRatio(2, 1);
        _uncollectedTree.SetColumnCustomMinimumWidth(0, 160);
        _uncollectedTree.SetColumnCustomMinimumWidth(1, 100);
        _uncollectedTree.SetColumnCustomMinimumWidth(2, 50);
        _uncollectedTree.CreateItem();
        AP_Atlas.Core.TreePicks.Hook(_uncollectedTree, (selected, column) =>
        {
            var meta = selected.GetMetadata(0);
            if (meta.VariantType != Variant.Type.String) return;
            string s = meta.AsString();
            if (!s.StartsWith("item|")) return;
            var parts = s.Split('|', 3);
            if (parts.Length == 3 && long.TryParse(parts[1], out long id)) Inspect(ItemTarget(id, parts[2]));
        });
        // Remember which classification groups the user opened/closed across rebuilds.
        _uncollectedTree.ItemCollapsed += item =>
        {
            var meta = item.GetMetadata(0);
            if (meta.VariantType == Variant.Type.String) _uncollectedGroupCollapsed[meta.AsString()] = item.Collapsed;
        };
        rightVBox.AddChild(_uncollectedTree);
    }

    private bool AllItemFiltersOn =>
        _filterItemsProgression.ButtonPressed && _filterItemsUseful.ButtonPressed &&
        _filterItemsFiller.ButtonPressed && _filterItemsTrap.ButtonPressed;

    private bool PassesItemFilter(bool isProgression, bool isUseful, bool isTrap)
    {
        bool isFiller = !isProgression && !isUseful && !isTrap;
        if (isProgression && !_filterItemsProgression.ButtonPressed) return false;
        if (isUseful && !_filterItemsUseful.ButtonPressed) return false;
        if (isTrap && !_filterItemsTrap.ButtonPressed) return false;
        if (isFiller && !_filterItemsFiller.ButtonPressed) return false;
        return true;
    }

    private static Color ItemClassColor(bool isProgression, bool isUseful, bool isTrap)
    {
        if (isProgression) return Colors.Plum;
        if (isUseful) return Colors.SlateBlue;
        if (isTrap) return Colors.Salmon;
        return Colors.DimGray;
    }

    private static string ItemClassName(int flags) =>
        (flags & 1) != 0 ? "Progression" : (flags & 2) != 0 ? "Useful" : (flags & 4) != 0 ? "Trap" : "Filler";

    private static readonly string[] ItemClassOrder = { "Progression", "Useful", "Filler", "Trap" };

    // Incremental render state (see the note on the Logic Tracker state above): the trees are rebuilt only
    // when a filter, the search text, the sort mode or the item pool changes; new items are appended and
    // remaining counts are edited in place.
    private string _historyConfigKey;
    private int _historyRenderedCount = 0;
    private int _historyShownCount = 0;
    private readonly Dictionary<(long id, string name, int flags), TreeItem> _uncollectedRows = new();
    private readonly Dictionary<(long id, string name, int flags), int> _uncollectedShownQty = new();
    private readonly Dictionary<string, TreeItem> _uncollectedGroups = new Dictionary<string, TreeItem>();
    // Filler and traps are the bulk of most pools; keep them collapsed (cheap to show) unless the user opens them.
    private readonly Dictionary<string, bool> _uncollectedGroupCollapsed = new Dictionary<string, bool>
    {
        ["Progression"] = false,
        ["Useful"] = false,
        ["Filler"] = true,
        ["Trap"] = true
    };

    private AP_Atlas.UI.ViewRefresh _historyRefresh, _keyItemsRefresh;

    /// <summary>Redraws Item History: now if it shows, else when it does.</summary>
    private void UpdateItemHistoryUI() => _historyRefresh?.Request();

    private void RenderItemHistory()
    {
        using var __perf = AP_Atlas.Core.PerfMonitor.Measure($"[{_slotName}] Item History refresh");
        if (_itemHistoryTree == null || _uncollectedTree == null || Session == null) return;

        string search = _itemSearchBox?.Text?.Trim() ?? "";
        int sortMode = _optHistorySort?.Selected ?? 0;
        var fullPool = Model.Logic.Engine.LastItemPool;
        int poolCount = fullPool?.Count ?? 0;
        var allItems = Session.Items.AllItemsReceived;

        bool markedOnly = _filterItemsMarked?.ButtonPressed == true;
        string configKey = $"{_filterItemsProgression.ButtonPressed}{_filterItemsUseful.ButtonPressed}{_filterItemsFiller.ButtonPressed}{_filterItemsTrap.ButtonPressed}{markedOnly}|{search}|{sortMode}|{poolCount}";
        bool full = configKey != _historyConfigKey || allItems.Count < _historyRenderedCount || _itemHistoryTree.GetRoot() == null;
        _historyConfigKey = configKey;

        // 1. Collected items
        if (full)
        {
            _itemHistoryTree.Clear();
            _itemHistoryTree.CreateItem();
            _historyRenderedCount = 0;
            _historyShownCount = 0;
        }
        var historyRoot = _itemHistoryTree.GetRoot();

        var newRows = new List<(int index, long itemId, string itemName, string sender, string locationName, bool prog, bool useful, bool trap)>();
        for (int i = _historyRenderedCount; i < allItems.Count; i++)
        {
            var item = allItems[i];
            bool prog = item.Flags.HasFlag(ItemFlags.Advancement);
            bool useful = item.Flags.HasFlag(ItemFlags.NeverExclude);
            bool trap = item.Flags.HasFlag(ItemFlags.Trap);
            if (!PassesItemFilter(prog, useful, trap)) continue;
            if (markedOnly && !IsItemMarked(item.ItemId, item.ItemName)) continue;

            string itemName = item.ItemName ?? "Unknown Item";
            string sender = Session.Players.GetPlayerAlias(item.Player) ?? "Server";
            string locationName = item.LocationName ?? "Unknown Location";
            if (!string.IsNullOrEmpty(search) &&
                itemName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                sender.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                locationName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;

            newRows.Add((i + 1, item.ItemId, itemName, sender, locationName, prog, useful, trap));
        }
        _historyRenderedCount = allItems.Count;

        if (sortMode == 2)
        {
            newRows = newRows.OrderBy(x => x.itemName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        foreach (var d in newRows)
        {
            int position = -1; // append (oldest first)
            if (sortMode == 1) position = 0; // newest first
            else if (sortMode == 2 && !full) position = AlphabeticalInsertIndex(historyRoot, d.itemName);

            var row = _itemHistoryTree.CreateItem(historyRoot, position);
            row.SetText(0, d.index.ToString());
            row.SetText(1, d.itemName);
            row.SetText(2, d.sender);
            row.SetText(3, d.locationName);
            var fg = ItemClassColor(d.prog, d.useful, d.trap);
            // Stripe by receive order so inserting rows never has to restripe the others.
            var bg = (d.index % 2 == 0) ? Color.FromHtml("#16161C") : Color.FromHtml("#1F1F27");
            for (int c = 0; c < 4; c++) { row.SetCustomColor(c, fg); row.SetCustomBgColor(c, bg); }
            row.SetMetadata(0, d.index - 1);
            row.SetTooltipText(2, "Click to inspect the sender");
            row.SetTooltipText(3, "Click to inspect the location it came from");
            ApplyItemMarker(row, 1, d.itemId, d.itemName);
        }
        _historyShownCount += newRows.Count;

        _collectedHeaderLabel.Text = (string.IsNullOrEmpty(search) && AllItemFiltersOn)
            ? $"Collected ({allItems.Count})"
            : $"Collected ({_historyShownCount} shown / {allItems.Count} total)";

        // 2. Not yet collected (needs the engine's item pool)
        if (fullPool == null || fullPool.Count == 0)
        {
            _uncollectedTree.Clear();
            _uncollectedTree.CreateItem();
            _uncollectedRows.Clear();
            _uncollectedShownQty.Clear();
            _uncollectedGroups.Clear();
            _uncollectedHeaderLabel.Text = Model.Logic.Running ? "Not Yet Collected (0 items)" : "Not Yet Collected (Waiting for Logic Engine...)";
            return;
        }

        var remaining = ComputeRemainingPool(fullPool, allItems);
        int totalRemaining = remaining.Values.Sum();

        if (full || _uncollectedTree.GetRoot() == null) RebuildUncollectedTree(remaining, search);
        else UpdateUncollectedTreeInPlace(remaining);

        int shownRemaining = _uncollectedShownQty.Values.Sum();
        _uncollectedHeaderLabel.Text = (string.IsNullOrEmpty(search) && AllItemFiltersOn)
            ? $"Not Yet Collected ({totalRemaining} remaining)"
            : $"Not Yet Collected ({shownRemaining} shown / {totalRemaining} remaining)";
    }

    private static int AlphabeticalInsertIndex(TreeItem root, string name)
    {
        int index = 0;
        for (var child = root.GetFirstChild(); child != null; child = child.GetNext(), index++)
        {
            if (string.Compare(child.GetText(1), name, StringComparison.OrdinalIgnoreCase) > 0) return index;
        }
        return -1;
    }

    /// <summary>Remaining quantity per pool entry after subtracting received items (matched by id, then by name).</summary>
    private static Dictionary<(long id, string name, int flags), int> ComputeRemainingPool(
        List<WorldItemInfo> fullPool, IReadOnlyList<Archipelago.MultiClient.Net.Models.ItemInfo> received)
    {
        var collectedCounts = new Dictionary<long, int>();
        var collectedNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in received)
        {
            collectedCounts[item.ItemId] = collectedCounts.GetValueOrDefault(item.ItemId) + 1;
            string itemName = item.ItemName ?? "Unknown Item";
            collectedNameCounts[itemName] = collectedNameCounts.GetValueOrDefault(itemName) + 1;
        }

        var poolGrouped = new Dictionary<(long id, string name, int flags), int>();
        foreach (var pItem in fullPool)
        {
            var key = (pItem.Id, pItem.Name ?? "Unknown Item", pItem.Flags);
            poolGrouped[key] = poolGrouped.GetValueOrDefault(key) + 1;
        }

        var result = new Dictionary<(long id, string name, int flags), int>();
        foreach (var kvp in poolGrouped)
        {
            int totalQty = kvp.Value;
            int got = 0;
            if (collectedCounts.TryGetValue(kvp.Key.id, out int byId) && byId > 0)
            {
                got = Math.Min(totalQty, byId);
                collectedCounts[kvp.Key.id] -= got;
            }
            else if (collectedNameCounts.TryGetValue(kvp.Key.name, out int byName) && byName > 0)
            {
                got = Math.Min(totalQty, byName);
                collectedNameCounts[kvp.Key.name] -= got;
            }
            if (totalQty - got > 0) result[kvp.Key] = totalQty - got;
        }
        return result;
    }

    private void RebuildUncollectedTree(Dictionary<(long id, string name, int flags), int> remaining, string search)
    {
        _uncollectedTree.Clear();
        var root = _uncollectedTree.CreateItem();
        _uncollectedRows.Clear();
        _uncollectedShownQty.Clear();
        _uncollectedGroups.Clear();

        var visible = remaining
            .Where(kv =>
            {
                int f = kv.Key.flags;
                if (!PassesItemFilter((f & 1) != 0, (f & 2) != 0, (f & 4) != 0)) return false;
                if (_filterItemsMarked?.ButtonPressed == true && !IsItemMarked(kv.Key.id, kv.Key.name)) return false;
                if (string.IsNullOrEmpty(search)) return true;
                return kv.Key.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       ItemClassName(f).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
            })
            .GroupBy(kv => ItemClassName(kv.Key.flags))
            .ToDictionary(g => g.Key, g => g.OrderBy(kv => kv.Key.name, StringComparer.OrdinalIgnoreCase).ToList());

        foreach (var className in ItemClassOrder)
        {
            if (!visible.TryGetValue(className, out var entries) || entries.Count == 0) continue;
            var group = _uncollectedTree.CreateItem(root);
            group.SetMetadata(0, className);
            int flags = entries[0].Key.flags;
            var fg = ItemClassColor((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0);
            for (int c = 0; c < 3; c++)
            {
                group.SetSelectable(c, false);
                group.SetCustomBgColor(c, new Godot.Color("#252836"));
                group.SetCustomColor(c, fg);
            }
            _uncollectedGroups[className] = group;

            int stripe = 0;
            foreach (var kv in entries)
            {
                var row = _uncollectedTree.CreateItem(group);
                row.SetText(0, kv.Key.name);
                row.SetText(1, className);
                row.SetText(2, kv.Value.ToString());
                var bg = (stripe++ % 2 == 0) ? Color.FromHtml("#16161C") : Color.FromHtml("#1F1F27");
                for (int c = 0; c < 3; c++) { row.SetCustomColor(c, fg); row.SetCustomBgColor(c, bg); }
                row.SetMetadata(0, $"item|{kv.Key.id}|{kv.Key.name}");
                ApplyItemMarker(row, 0, kv.Key.id, kv.Key.name);
                _uncollectedRows[kv.Key] = row;
                _uncollectedShownQty[kv.Key] = kv.Value;
            }
            group.Collapsed = _uncollectedGroupCollapsed.GetValueOrDefault(className);
        }
        UpdateUncollectedGroupHeaders();
    }

    private void UpdateUncollectedTreeInPlace(Dictionary<(long id, string name, int flags), int> remaining)
    {
        foreach (var key in _uncollectedRows.Keys.ToList())
        {
            int qty = remaining.GetValueOrDefault(key);
            if (qty == _uncollectedShownQty[key]) continue;
            if (qty <= 0)
            {
                _uncollectedRows[key].Free();
                _uncollectedRows.Remove(key);
                _uncollectedShownQty.Remove(key);
            }
            else
            {
                _uncollectedRows[key].SetText(2, qty.ToString());
                _uncollectedShownQty[key] = qty;
            }
        }
        UpdateUncollectedGroupHeaders();
    }

    private void UpdateUncollectedGroupHeaders()
    {
        foreach (var className in _uncollectedGroups.Keys.ToList())
        {
            var group = _uncollectedGroups[className];
            int qty = _uncollectedShownQty.Where(kv => ItemClassName(kv.Key.flags) == className).Sum(kv => kv.Value);
            if (qty == 0)
            {
                group.Free();
                _uncollectedGroups.Remove(className);
                continue;
            }
            group.SetText(0, $"{className} ({qty} remaining)");
            group.SetText(2, qty.ToString());
        }
    }

    /// <summary>Redraws Key Items: now if it shows, else when it does.</summary>
    private void UpdateKeyItemsUI() => _keyItemsRefresh?.Request();
}
