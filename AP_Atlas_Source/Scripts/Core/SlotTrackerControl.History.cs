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

        // The collected items are a table like every other in Atlas (sorted by a column's title, searched, exported); its
        // search box and menus sit in the filter bar, and the search narrows the uncollected items too.
        _historyTable = new AP_Atlas.UI.AtlasTable("item-history", _appSettings, text => text)
        {
            Toast = (text, color) => ShowToast?.Invoke(text, color),
            EmptyText = "Nothing received yet.",
            NoMatchText = "No items match the current filters."
        };
        _historyTable.SetColumns(new List<AP_Atlas.UI.AtlasTable.Column>
        {
            new() { Id = "order", Title = "Order", MinWidth = 60, Ratio = 1, Align = HorizontalAlignment.Right },
            new() { Id = "item", Title = "Item", MinWidth = 140, Ratio = 4 },
            new() { Id = "from", Title = "From", MinWidth = 100, Ratio = 3 },
            new() { Id = "location", Title = "Location", MinWidth = 140, Ratio = 4 }
        }, defaultSort: "order");
        _historyTable.Customize = (item, row) =>
        {
            int column = _historyTable.ShownIndexOf("item");
            var received = Session?.Items.AllItemsReceived;
            if (column < 0 || received == null || row.Tag is not int index || index < 0 || index >= received.Count) return;
            ApplyItemMarker(item, column, received[index].ItemId, received[index].ItemName);
        };
        // The column clicked decides what to inspect: the sender, the location it came from, or the item.
        _historyTable.CellPicked += (row, columnId) =>
        {
            var received = Session?.Items.AllItemsReceived;
            if (received == null || row.Tag is not int index || index < 0 || index >= received.Count) return;
            var item = received[index];
            if (columnId == "from" && item.Player != null) Inspect(PlayerTarget(item.Player.Slot));
            else if (columnId == "location" && item.Player != null && item.LocationId > 0) Inspect(LocationTargetFor(item.Player.Slot, item.LocationId));
            else Inspect(ReceivedItemTarget(index));
        };

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
        var search = _historyTable.Take(_historyTable.SearchBox);
        search.PlaceholderText = "Search items, senders, locations...";
        search.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        filterBar.AddChild(search);
        filterBar.AddChild(_historyTable.Take(_historyTable.ColumnsMenu));
        filterBar.AddChild(_historyTable.Take(_historyTable.ExportMenu));
        _historyTable.Toolbar.Visible = false;

        _filterItemsProgression.Toggled += (b) => UpdateItemHistoryUI();
        _filterItemsUseful.Toggled += (b) => UpdateItemHistoryUI();
        _filterItemsFiller.Toggled += (b) => UpdateItemHistoryUI();
        _filterItemsTrap.Toggled += (b) => UpdateItemHistoryUI();
        _filterItemsMarked.Toggled += (b) => UpdateItemHistoryUI();
        search.TextChanged += (txt) => UpdateItemHistoryUI(); // the table searches itself; the uncollected items follow

        vbox.AddChild(new HSeparator { CustomMinimumSize = new Godot.Vector2(0, 10) });

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(split);

        var leftVBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(leftVBox);
        var collectedHeader = new HBoxContainer();
        collectedHeader.AddThemeConstantOverride("separation", 10);
        collectedHeader.AddChild(new Label { Text = "Collected" });
        collectedHeader.AddChild(_historyTable.Take(_historyTable.CountLabel));
        leftVBox.AddChild(collectedHeader);
        leftVBox.AddChild(_historyTable);

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
        if (isProgression) return AP_Atlas.Core.ThemeColors.Progression;
        if (isUseful) return AP_Atlas.Core.ThemeColors.Useful;
        if (isTrap) return AP_Atlas.Core.ThemeColors.Error;
        return AP_Atlas.Core.ThemeColors.TextSubtle;
    }

    private static string ItemClassName(int flags) =>
        (flags & 1) != 0 ? "Progression" : (flags & 2) != 0 ? "Useful" : (flags & 4) != 0 ? "Trap" : "Filler";

    private static readonly string[] ItemClassOrder = { "Progression", "Useful", "Filler", "Trap" };

    // Incremental render state (see the note on the Logic Tracker state above): the trees are rebuilt only
    // when a filter, the search text, the sort mode or the item pool changes; new items are appended and
    // remaining counts are edited in place.
    // The search the uncollected tree was last built for (another one rebuilds it).
    private string _uncollectedSearch;
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
        if (_historyTable == null || _uncollectedTree == null || Session == null) return;

        string search = _historyTable.SearchBox.Text.Trim();
        var fullPool = Model.Logic.Engine.LastItemPool;
        var allItems = Session.Items.AllItemsReceived;
        bool markedOnly = _filterItemsMarked?.ButtonPressed == true;

        // 1. Collected items: a row each, made once (a resync from the server starts over), the class filters applied here
        // and the search by the table.
        if (_historyRows.Count > allItems.Count) _historyRows.Clear();
        for (int i = _historyRows.Count; i < allItems.Count; i++) _historyRows.Add(HistoryRow(i, allItems[i]));
        var shown = new List<AP_Atlas.UI.AtlasTable.Row>(allItems.Count);
        for (int i = 0; i < allItems.Count; i++)
        {
            var item = allItems[i];
            bool prog = item.Flags.HasFlag(ItemFlags.Advancement);
            bool useful = item.Flags.HasFlag(ItemFlags.NeverExclude);
            bool trap = item.Flags.HasFlag(ItemFlags.Trap);
            if (!PassesItemFilter(prog, useful, trap)) continue;
            if (markedOnly && !IsItemMarked(item.ItemId, item.ItemName)) continue;
            shown.Add(_historyRows[i]);
        }
        _historyTable.TotalCount = allItems.Count;
        _historyTable.SetRows(shown);

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

        bool full = search != _uncollectedSearch || _uncollectedTree.GetRoot() == null;
        _uncollectedSearch = search;
        if (full) RebuildUncollectedTree(remaining, search);
        else UpdateUncollectedTreeInPlace(remaining);

        int shownRemaining = _uncollectedShownQty.Values.Sum();
        _uncollectedHeaderLabel.Text = (string.IsNullOrEmpty(search) && AllItemFiltersOn)
            ? $"Not Yet Collected ({totalRemaining} remaining)"
            : $"Not Yet Collected ({shownRemaining} shown / {totalRemaining} remaining)";
    }

    /// <summary>A received item's row: its place in the order (sorted as a number), name, sender and location, in its class's colour.</summary>
    private AP_Atlas.UI.AtlasTable.Row HistoryRow(int index, ItemInfo item)
    {
        bool prog = item.Flags.HasFlag(ItemFlags.Advancement);
        bool useful = item.Flags.HasFlag(ItemFlags.NeverExclude);
        bool trap = item.Flags.HasFlag(ItemFlags.Trap);
        var color = ItemClassColor(prog, useful, trap);
        string itemName = item.ItemName ?? "Unknown Item";
        string sender = Session.Players.GetPlayerAlias(item.Player) ?? "Server";
        string locationName = item.LocationName ?? "Unknown Location";
        return new AP_Atlas.UI.AtlasTable.Row
        {
            Key = index.ToString(),
            Tag = index,
            Cells = new[]
            {
                new AP_Atlas.UI.AtlasTable.Cell((index + 1).ToString(), color, null, index),
                new AP_Atlas.UI.AtlasTable.Cell(itemName, color),
                new AP_Atlas.UI.AtlasTable.Cell(sender, color, "Click to inspect the sender"),
                new AP_Atlas.UI.AtlasTable.Cell(locationName, color, "Click to inspect the location it came from")
            }
        };
    }

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
                group.SetCustomBgColor(c, AP_Atlas.Core.ThemeColors.SurfacePanel);
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
