#nullable disable
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Models;
using Color = Godot.Color;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Per-slot hint table: hints for the slot's items ("My Items") and hints at the slot's locations
    /// ("My Locations"), with status editing, in-logic indicators, toasts for new hints and a hint request bar.
    /// SlotTrackerControl feeds it via SetHints() from Session.Hints.TrackHints.
    /// </summary>
    public partial class HintTrackerControl : MarginContainer
    {
        private enum Col { Status, Item, Receiver, Location, Finder, Entrance, Logic }
        private static readonly string[] ColumnTitles = { "Status", "Item", "For", "Location", "In World Of", "Entrance", "In Logic" };

        // Statuses a receiver may pick (Found is set by the server only). Order matches the dropdown text.
        private static readonly HintStatus[] EditableStatuses = { HintStatus.Priority, HintStatus.NoPriority, HintStatus.Avoid, HintStatus.Unspecified };
        private const string EditableStatusOptions = "Priority,No Priority,Avoid,Unspecified";

        private ArchipelagoSession _session;
        private string _slotName;
        private Func<long, bool?> _ownLogic;
        private Func<int, long, bool?> _otherSlotLogic;
        private Action<string, Color> _toast;

        private Hint[] _hints = Array.Empty<Hint>();
        private readonly HashSet<string> _seenHintKeys = new HashSet<string>();
        private bool _receivedInitialHints = false;


        // Hint request autocomplete sources (names in the slot's own game).
        private List<string> _itemNames = new List<string>();
        private List<string> _locationNames = new List<string>();

        private Button _btnMyItems;
        private Button _btnMyLocations;
        private Button _btnShowFound;
        private LineEdit _searchBox;
        private Label _summaryLabel;
        private AtlasTable _table;
        private Tree _tree;
        private AppSettings _settings;
        private ItemList _suggestions;
        private OptionButton _requestMode;
        private LineEdit _requestInput;
        private Button _requestButton;
        private Label _pointsLabel;
        private Label _requestFeedback;
        private Timer _pointsTimer;

        private sealed class Row
        {
            public Hint Hint;
            public string Key;
            public string ItemName;
            public string ReceiverName;
            public string FinderName;
            public string LocationName;
            public string Entrance;
            public bool? InLogic;
            public bool IsMyItem;
            public bool IsMyLocation;
        }

        private readonly Dictionary<string, Row> _rowsByKey = new Dictionary<string, Row>();

        public enum HintPart { Hint, Item, Location, Receiver, Finder }

        /// <summary>Raised when the user picks a hint row; the part is the column they clicked.</summary>
        public event Action<Hint, HintPart> HintPicked;

        /// <summary>Markers for a hint: item (flag, special, note) then location (flag, special, note). Set by the owner.</summary>
        public Func<Hint, (int ItemFlag, bool ItemSpecial, bool ItemNote, int LocFlag, bool LocSpecial, bool LocNote)> MarkerLookup { get; set; }

        private Button _btnMarkedOnly;
        private bool _suppressPick;

        /// <summary>Race mode hides in-logic information for this slot. Set by the owner.</summary>
        public Func<bool> LogicHidden { get; set; }

        /// <summary>Sends a command as the slot (through its model, so the server's answer reaches its text client).</summary>
        public Func<string, System.Threading.Tasks.Task> Say { get; set; }

        private int MySlot => _session?.ConnectionInfo?.Slot ?? -1;

        public void Initialize(ArchipelagoSession session, string slotName, Func<long, bool?> ownLogic,
            Func<int, long, bool?> otherSlotLogic, Action<string, Color> toast, AppSettings settings)
        {
            _session = session;
            _settings = settings;
            _slotName = slotName;
            _ownLogic = ownLogic;
            _otherSlotLogic = otherSlotLogic;
            _toast = toast;

            Name = "Hints";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            AddThemeConstantOverride("margin_left", 10);
            AddThemeConstantOverride("margin_top", 10);
            AddThemeConstantOverride("margin_right", 10);
            AddThemeConstantOverride("margin_bottom", 10);

            BuildUI();
            RequestNameLists();
            Refresh();
        }

        // =====================================================================
        // UI construction
        // =====================================================================

        private void BuildUI()
        {
            var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            vbox.AddThemeConstantOverride("separation", 8);
            AddChild(vbox);

            // --- Filter toolbar ---
            var toolbar = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            toolbar.AddThemeConstantOverride("separation", 10);
            vbox.AddChild(toolbar);

            toolbar.AddChild(new Label { Text = "Show: " });
            _btnMyItems = new Button { ToggleMode = true, ButtonPressed = true, Text = "My Items", TooltipText = "Hints for items this slot will receive (where your items are hidden).", CustomMinimumSize = new Vector2(100, 0) };
            _btnMyLocations = new Button { ToggleMode = true, ButtonPressed = true, Text = "My Locations", TooltipText = "Hints for items hidden in this slot's world (what others are waiting on from you).", CustomMinimumSize = new Vector2(100, 0) };
            _btnShowFound = new Button { ToggleMode = true, ButtonPressed = false, Text = "Show Found", TooltipText = "Include hints whose item has already been found.", CustomMinimumSize = new Vector2(100, 0) };
            toolbar.AddChild(_btnMyItems);
            toolbar.AddChild(_btnMyLocations);
            toolbar.AddChild(new VSeparator());
            toolbar.AddChild(_btnShowFound);
            _btnMarkedOnly = new Button { ToggleMode = true, Text = "Flagged / special only", TooltipText = "Only hints whose item or location you flagged, noted or marked special." };
            _btnMarkedOnly.Toggled += _ => Render();
            toolbar.AddChild(_btnMarkedOnly);
            toolbar.AddChild(new VSeparator());
            _table = new AtlasTable("hints", _settings, text => text) { Toast = _toast, EmptyText = "No hints yet. Request one below, or use !hint in Chat.", NoMatchText = "No hints match the current filters." };
            _tree = _table.Tree;
            _searchBox = _table.Take(_table.SearchBox);
            _searchBox.PlaceholderText = "Search items, players, locations, entrances...";
            _searchBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            toolbar.AddChild(_searchBox);
            toolbar.AddChild(_table.Take(_table.CountLabel));
            _summaryLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
            _summaryLabel.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
            toolbar.AddChild(_summaryLabel);
            toolbar.AddChild(_table.Take(_table.ColumnsMenu));
            toolbar.AddChild(_table.Take(_table.ExportMenu));
            _table.Toolbar.Visible = false;

            _btnMyItems.Toggled += _ => Render();
            _btnMyLocations.Toggled += _ => Render();
            _btnShowFound.Toggled += _ => Render();

            // --- Hint table: Archipelago's columns and colours; the status of your own unfound hints is editable. ---
            int[] ratios = { 2, 4, 3, 5, 3, 3, 2 };
            int[] minWidths = { 110, 160, 110, 200, 110, 100, 90 };
            _table.SetColumns(Enumerable.Range(0, ColumnTitles.Length)
                .Select(c => new AtlasTable.Column { Id = ((Col)c).ToString().ToLowerInvariant(), Title = ColumnTitles[c], MinWidth = minWidths[c], Ratio = ratios[c] }).ToList(), defaultSort: "status");
            _table.Customize = CustomizeRow;
            _tree.ItemEdited += OnTreeItemEdited;
            _table.Activated += picked => CopyHint(picked.Tag as Row);
            // Clicking a cell inspects that part of the hint: the item, a player, the location, or the hint itself.
            _table.CellPicked += (picked, columnId) =>
            {
                if (picked.Tag is not Row row || _suppressPick) return;
                var part = columnId switch
                {
                    "item" => HintPart.Item,
                    "location" => HintPart.Location,
                    "receiver" => HintPart.Receiver,
                    "finder" => HintPart.Finder,
                    _ => HintPart.Hint
                };
                HintPicked?.Invoke(row.Hint, part);
            };
            vbox.AddChild(_table);

            // --- Autocomplete suggestions (shown above the request bar while typing) ---
            _suggestions = new ItemList { Visible = false, CustomMinimumSize = new Vector2(0, 150), SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
            _suggestions.ItemClicked += (index, _, mouseButton) =>
            {
                if (mouseButton == (long)MouseButton.Left) AcceptSuggestion((int)index);
            };
            vbox.AddChild(_suggestions);

            // --- Hint request bar ---
            var requestPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            requestPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = ThemeColors.SurfaceSunken,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                ContentMarginLeft = 10,
                ContentMarginRight = 10,
                ContentMarginTop = 8,
                ContentMarginBottom = 8
            });
            vbox.AddChild(requestPanel);

            var requestVBox = new VBoxContainer();
            requestPanel.AddChild(requestVBox);
            var requestRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            requestRow.AddThemeConstantOverride("separation", 8);
            requestVBox.AddChild(requestRow);

            requestRow.AddChild(new Label { Text = "Request hint:" });
            _requestMode = new OptionButton { TooltipText = "Item: where is one of my items (!hint).\nLocation: what is at one of my locations (!hint_location)." };
            _requestMode.AddItem("Item");
            _requestMode.AddItem("Location");
            _requestMode.ItemSelected += _ => UpdateSuggestions();
            requestRow.AddChild(_requestMode);

            _requestInput = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "Start typing a name..." };
            _requestInput.TextChanged += _ => UpdateSuggestions();
            _requestInput.TextSubmitted += _ => SubmitHintRequest();
            _requestInput.GuiInput += OnRequestInputGuiInput;
            _requestInput.FocusExited += () => Ui.Defer(this, () => { if (!_requestInput.HasFocus()) _suggestions.Visible = false; });
            requestRow.AddChild(_requestInput);

            _requestButton = new Button { Text = "Hint", CustomMinimumSize = new Vector2(80, 0) };
            _requestButton.Pressed += SubmitHintRequest;
            requestRow.AddChild(_requestButton);

            _pointsLabel = new Label { CustomMinimumSize = new Vector2(260, 0), HorizontalAlignment = HorizontalAlignment.Right };
            requestRow.AddChild(_pointsLabel);

            _requestFeedback = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _requestFeedback.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            requestVBox.AddChild(_requestFeedback);

            // Hint points change with every check and RoomUpdate; poll cheaply while the tab is visible.
            _pointsTimer = new Timer { WaitTime = 1.0, Autostart = true };
            _pointsTimer.Timeout += () => { if (IsVisibleInTree()) UpdatePointsLabel(); };
            AddChild(_pointsTimer);
        }

        // =====================================================================
        // Data in
        // =====================================================================

        /// <summary>Called on the main thread whenever the server's hint list for this slot changes.</summary>
        public void SetHints(Hint[] hints)
        {
            _hints = hints ?? Array.Empty<Hint>();

            var newHints = new List<Hint>();
            foreach (var h in _hints)
            {
                if (_seenHintKeys.Add(KeyOf(h)) && _receivedInitialHints && !h.Found) newHints.Add(h);
            }
            bool notify = _receivedInitialHints;
            _receivedInitialHints = true;

            Refresh();
            if (notify && newHints.Count > 0) AnnounceNewHints(newHints);
        }

        private ViewRefresh _refresh;

        /// <summary>Recomputes logic/points (e.g. after items or checks change) and redraws: now if the view shows, else when it does.</summary>
        public void Refresh() => (_refresh ??= new ViewRefresh(this, RefreshNow, "refreshing the hints")).Request();

        private void RefreshNow()
        {
            using var __perf = AP_Atlas.Core.PerfMonitor.Measure($"[{_slotName}] Hints refresh");
            RebuildRows();
            Render();
            UpdatePointsLabel();
        }

        private static string KeyOf(Hint h) => h.FindingPlayer + ":" + h.LocationId;

        private string _revealKey;

        /// <summary>Selects and scrolls to a hint, loosening filters that would hide it.</summary>
        public void Reveal(int findingPlayer, long locationId)
        {
            _refresh?.Flush();
            string key = findingPlayer + ":" + locationId;
            if (!_rowsByKey.TryGetValue(key, out var row)) return;
            if (row.Hint.Found) _btnShowFound.ButtonPressed = true;
            if (row.IsMyItem && !row.IsMyLocation) _btnMyItems.ButtonPressed = true;
            if (row.IsMyLocation && !row.IsMyItem) _btnMyLocations.ButtonPressed = true;
            _btnMarkedOnly.ButtonPressed = false;
            _searchBox.Text = "";
            _revealKey = key;
            Render();
        }

        /// <summary>Filters the table to hints mentioning a player.</summary>
        public void FilterByText(string text)
        {
            _refresh?.Flush();
            _searchBox.Text = text ?? "";
            Render();
        }

        private void RebuildRows()
        {
            _rowsByKey.Clear();
            if (_session == null) return;
            int me = MySlot;
            foreach (var h in _hints)
            {
                var receiverGame = _session.Players.GetPlayerInfo(h.ReceivingPlayer)?.Game;
                var finderGame = _session.Players.GetPlayerInfo(h.FindingPlayer)?.Game;
                var row = new Row
                {
                    Hint = h,
                    Key = KeyOf(h),
                    ItemName = _session.Items.GetItemName(h.ItemId, receiverGame) ?? $"Item {h.ItemId}",
                    ReceiverName = PlayerName(h.ReceivingPlayer),
                    FinderName = PlayerName(h.FindingPlayer),
                    LocationName = _session.Locations.GetLocationNameFromId(h.LocationId, finderGame) ?? $"Location {h.LocationId}",
                    Entrance = string.IsNullOrEmpty(h.Entrance) ? "" : h.Entrance,
                    IsMyItem = h.ReceivingPlayer == me,
                    IsMyLocation = h.FindingPlayer == me,
                };
                if (!h.Found)
                {
                    row.InLogic = h.FindingPlayer == me ? _ownLogic?.Invoke(h.LocationId) : _otherSlotLogic?.Invoke(h.FindingPlayer, h.LocationId);
                }
                _rowsByKey[row.Key] = row;
            }
        }

        private string PlayerName(int slot)
        {
            string name = _session.Players.GetPlayerAlias(slot);
            if (string.IsNullOrEmpty(name)) name = _session.Players.GetPlayerName(slot);
            return string.IsNullOrEmpty(name) ? $"Slot {slot}" : name;
        }

        // =====================================================================
        // Rendering
        // =====================================================================

        private static HintStatus EffectiveStatus(Hint h) => h.Found ? HintStatus.Found : h.Status;

        private static int StatusRank(HintStatus s) => s switch
        {
            HintStatus.Priority => 0,
            HintStatus.Unspecified => 1,
            HintStatus.NoPriority => 2,
            HintStatus.Avoid => 3,
            _ => 4 // Found
        };

        private static string StatusText(HintStatus s) => s switch
        {
            HintStatus.Priority => "Priority",
            HintStatus.NoPriority => "No Priority",
            HintStatus.Avoid => "Avoid",
            HintStatus.Found => "Found",
            _ => "Unspecified"
        };

        private static Color StatusColor(HintStatus s) => s switch
        {
            HintStatus.Priority => ThemeColors.HintPriority,
            HintStatus.NoPriority => ThemeColors.HintNoPriority,
            HintStatus.Avoid => ThemeColors.Error,
            HintStatus.Found => ThemeColors.Success,
            _ => ThemeColors.TextMuted
        };

        private static Color ItemColor(ItemFlags flags)
        {
            if (flags.HasFlag(ItemFlags.Advancement)) return ThemeColors.Progression;
            if (flags.HasFlag(ItemFlags.NeverExclude)) return ThemeColors.Useful;
            if (flags.HasFlag(ItemFlags.Trap)) return ThemeColors.Trap;
            return ThemeColors.Filler;
        }

        private bool ServerSupportsHintStatus =>
            _session?.RoomState?.Version != null && _session.RoomState.Version >= new Version(0, 6, 0);

        private IEnumerable<Row> FilteredRows()
        {
            bool myItems = _btnMyItems.ButtonPressed;
            bool myLocations = _btnMyLocations.ButtonPressed;
            bool showFound = _btnShowFound.ButtonPressed;

            foreach (var r in _rowsByKey.Values)
            {
                if (r.Hint.Found && !showFound) continue;
                // A hint can be both (an item for you, in your own world); show it if either toggle is on.
                if (!((myItems && r.IsMyItem) || (myLocations && r.IsMyLocation))) continue;
                if (_btnMarkedOnly?.ButtonPressed == true && MarkerLookup != null)
                {
                    var m = MarkerLookup(r.Hint);
                    if (m.ItemFlag == 0 && !m.ItemSpecial && !m.ItemNote && m.LocFlag == 0 && !m.LocSpecial && !m.LocNote) continue;
                }
                yield return r;
            }
        }

        private void Render()
        {
            if (_table == null) return;
            bool canEditStatus = ServerSupportsHintStatus;
            bool hidden = LogicHidden?.Invoke() == true;
            var rows = new List<AtlasTable.Row>();
            // Ties in any sort keep this order: in logic first, then by item name.
            foreach (var r in FilteredRows().OrderBy(r => r.InLogic == true ? 0 : r.InLogic == null ? 1 : 2).ThenBy(r => r.ItemName, StringComparer.OrdinalIgnoreCase))
            {
                var h = r.Hint;
                var status = EffectiveStatus(h);
                var m = MarkerLookup?.Invoke(h);
                var cells = new AtlasTable.Cell[ColumnTitles.Length];
                cells[(int)Col.Status] = new AtlasTable.Cell(StatusText(status), h.Found ? null : StatusColor(status),
                    r.IsMyItem && !h.Found && canEditStatus ? "Click to change this hint's priority (shared with everyone in the room)." : null, StatusRank(status));
                cells[(int)Col.Item] = new AtlasTable.Cell(r.ItemName, h.Found ? null : ItemColor(h.ItemFlags), "Click to inspect the item. Double-click to copy this hint.")
                {
                    Icon = m is { } mi ? Annotations.MarkerIcon(mi.ItemFlag, mi.ItemSpecial, mi.ItemNote) : null,
                    Background = m?.ItemSpecial == true ? Annotations.SpecialBg : null
                };
                cells[(int)Col.Receiver] = new AtlasTable.Cell(r.ReceiverName, h.Found ? null : r.IsMyItem ? ThemeColors.You : ThemeColors.Player);
                cells[(int)Col.Location] = new AtlasTable.Cell(r.LocationName, h.Found ? null : ThemeColors.Location, "Click to inspect the location.")
                {
                    Icon = m is { } ml ? Annotations.MarkerIcon(ml.LocFlag, ml.LocSpecial, ml.LocNote) : null,
                    Background = m?.LocSpecial == true ? Annotations.SpecialBg : null
                };
                cells[(int)Col.Finder] = new AtlasTable.Cell(r.FinderName, h.Found ? null : r.IsMyLocation ? ThemeColors.You : ThemeColors.Player);
                cells[(int)Col.Entrance] = new AtlasTable.Cell(r.Entrance, ThemeColors.TextSubtle);
                cells[(int)Col.Logic] = LogicCell(r, hidden);
                rows.Add(new AtlasTable.Row { Key = r.Key, Tag = r, Cells = cells, Dim = h.Found });
            }
            _table.TotalCount = _rowsByKey.Count;
            _table.SetRows(rows);
            if (_revealKey != null) _table.Select(_revealKey, scrollTo: true);
            _revealKey = null;

            int open = _rowsByKey.Values.Count(r => !r.Hint.Found);
            int openMine = _rowsByKey.Values.Count(r => !r.Hint.Found && r.IsMyItem);
            int inLogic = _rowsByKey.Values.Count(r => !r.Hint.Found && r.InLogic == true);
            _summaryLabel.Text = hidden
                ? $"{open} open · {openMine} for you"
                : $"{open} open · {openMine} for you · {inLogic} in logic";
        }

        private AtlasTable.Cell LogicCell(Row r, bool hidden)
        {
            if (r.Hint.Found) return new AtlasTable.Cell("", null, null, 3);
            if (r.InLogic == true) return new AtlasTable.Cell("✔ In logic", ThemeColors.Success, null, 0);
            if (r.InLogic == false) return new AtlasTable.Cell("✖ Not yet", ThemeColors.Error, null, 2);
            if (hidden) return new AtlasTable.Cell("Hidden", ThemeColors.TextSubtle, "Hidden by race mode (Settings → Race Mode).", 1);
            return new AtlasTable.Cell("Unknown", ThemeColors.TextSubtle, r.IsMyLocation
                ? "The logic engine for this slot isn't running."
                : $"Connect {r.FinderName} in Atlas (same multiworld profile) to see their logic.", 1);
        }

        /// <summary>The status of your own unfound hints is a dropdown (the server shares the choice with the room).</summary>
        private void CustomizeRow(TreeItem item, AtlasTable.Row picked)
        {
            if (picked.Tag is not Row r) return;
            int status = _table.ShownIndexOf("status");
            if (status < 0 || !(r.IsMyItem && !r.Hint.Found && ServerSupportsHintStatus)) return;
            item.SetCellMode(status, TreeItem.TreeCellMode.Range);
            item.SetText(status, EditableStatusOptions);
            item.SetRange(status, Math.Max(0, Array.IndexOf(EditableStatuses, EffectiveStatus(r.Hint))));
            item.SetEditable(status, true);
        }

        // =====================================================================
        // Interaction
        // =====================================================================

        private void OnTreeItemEdited()
        {
            var edited = _tree.GetEdited();
            int status = _table.ShownIndexOf("status");
            if (status < 0 || _tree.GetEditedColumn() != status) return;
            var row = _table.RowAt(edited)?.Tag as Row;
            if (row == null || _session == null) return;

            int index = (int)edited.GetRange(status);
            if (index < 0 || index >= EditableStatuses.Length) return;
            var newStatus = EditableStatuses[index];
            if (newStatus == row.Hint.Status) return;

            try
            {
                // The server identifies a hint by the player whose world holds the location.
                _session.Hints.UpdateHintStatus(row.Hint.FindingPlayer, row.Hint.LocationId, newStatus);
                SetFeedback($"Marked '{row.ItemName}' as {StatusText(newStatus)}.", ThemeColors.TextSubtle);
            }
            catch (Exception ex)
            {
                SetFeedback($"Could not update hint status: {ex.Message}", ThemeColors.Error);
            }
        }

        /// <summary>A double-click (or Enter) copies the hint as a line of text.</summary>
        private void CopyHint(Row row)
        {
            if (row == null) return;
            string text = $"{row.ReceiverName}'s {row.ItemName} is at {row.LocationName} in {row.FinderName}'s world" +
                          (string.IsNullOrEmpty(row.Entrance) ? "" : $" ({row.Entrance})") +
                          (row.Hint.Found ? " (found)" : "");
            DisplayServer.ClipboardSet(text);
            SetFeedback("Copied: " + text, ThemeColors.TextSubtle);
        }

        private void AnnounceNewHints(List<Hint> newHints)
        {
            if (_toast == null) return;
            var rows = newHints.Select(h => _rowsByKey.TryGetValue(KeyOf(h), out var r) ? r : null).Where(r => r != null).ToList();
            if (rows.Count == 0) return;
            if (rows.Count > 3)
            {
                _toast($"[{_slotName}] {rows.Count} new hints", ThemeColors.Accent);
                return;
            }
            foreach (var r in rows)
            {
                string msg = r.IsMyItem
                    ? $"[{_slotName}] Hint: your {r.ItemName} is at {r.LocationName} ({r.FinderName})"
                    : $"[{_slotName}] Hint: {r.ReceiverName}'s {r.ItemName} is at your {r.LocationName}";
                _toast(msg, ItemColor(r.Hint.ItemFlags));
            }
        }

        // =====================================================================
        // Hint requests
        // =====================================================================

        private bool IsLocationMode => _requestMode.Selected == 1;

        private void RequestNameLists()
        {
            if (_session == null) return;
            string game = _session.ConnectionInfo?.Game;

            // Locations are known from the session; prefer unchecked ones since hinting a checked location is pointless.
            _locationNames = _session.Locations.AllMissingLocations
                .Select(id => _session.Locations.GetLocationNameFromId(id, game))
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Item names come from the game's data package. The slot fetches it once per connection into GameNames
            // (shared, so the server is asked once); use it now if it's already there, else when it arrives.
            if (string.IsNullOrEmpty(game)) return;
            GameNames.Updated += OnGameNamesUpdated;
            OnGameNamesUpdated(game);
        }

        private void OnGameNamesUpdated(string game)
        {
            string myGame = _session?.ConnectionInfo?.Game;
            if (!GodotObject.IsInstanceValid(this) || game == null || !string.Equals(game, myGame, StringComparison.OrdinalIgnoreCase)) return;
            var table = GameNames.Best(myGame);
            if (table == null || table.Items.Count == 0) return;
            _itemNames = table.Items.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            UpdateSuggestions();
        }

        private void UpdateSuggestions()
        {
            if (_suggestions == null) return;
            string text = _requestInput.Text.Trim();
            _suggestions.Clear();
            if (text.Length < 2 || !_requestInput.HasFocus())
            {
                _suggestions.Visible = false;
                return;
            }
            var source = IsLocationMode ? _locationNames : _itemNames;
            var matches = source
                .Where(n => n.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(n => n.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(n => n.Length)
                .Take(12)
                .ToList();
            // Hide the list when the only match is exactly what's typed.
            if (matches.Count == 0 || (matches.Count == 1 && string.Equals(matches[0], text, StringComparison.OrdinalIgnoreCase)))
            {
                _suggestions.Visible = false;
                return;
            }
            foreach (var m in matches) _suggestions.AddItem(m);
            _suggestions.Visible = true;
        }

        private void AcceptSuggestion(int index)
        {
            if (index < 0 || index >= _suggestions.ItemCount) return;
            _requestInput.Text = _suggestions.GetItemText(index);
            _requestInput.CaretColumn = _requestInput.Text.Length;
            _suggestions.Visible = false;
            _requestInput.GrabFocus();
        }

        private void OnRequestInputGuiInput(InputEvent @event)
        {
            if (!_suggestions.Visible || @event is not InputEventKey key || !key.Pressed) return;
            int count = _suggestions.ItemCount;
            if (count == 0) return;
            var selected = _suggestions.GetSelectedItems();
            int current = selected.Length > 0 ? selected[0] : -1;

            if (key.Keycode == Key.Down || key.Keycode == Key.Up)
            {
                int next = key.Keycode == Key.Down ? Math.Min(count - 1, current + 1) : Math.Max(0, current - 1);
                _suggestions.Select(next);
                _suggestions.EnsureCurrentIsVisible();
                _requestInput.AcceptEvent();
            }
            else if ((key.Keycode == Key.Tab || key.Keycode == Key.Enter || key.Keycode == Key.KpEnter) && current >= 0)
            {
                AcceptSuggestion(current);
                _requestInput.AcceptEvent();
            }
            else if (key.Keycode == Key.Escape)
            {
                _suggestions.Visible = false;
                _requestInput.AcceptEvent();
            }
        }

        private void SubmitHintRequest()
        {
            string name = _requestInput.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            if (_session == null || !_session.Socket.Connected)
            {
                SetFeedback("Not connected. Reconnect the slot to request hints.", ThemeColors.Error);
                return;
            }
            string command = (IsLocationMode ? "!hint_location " : "!hint ") + name;
            AP_Atlas.Core.Async.Fire(Say(command), "sending your hint request");
            _requestInput.Text = "";
            _suggestions.Visible = false;
            SetFeedback($"Sent \"{command}\". The server's reply appears in Chat; new hints show up here automatically.", ThemeColors.TextSubtle);
        }

        private void UpdatePointsLabel()
        {
            if (_pointsLabel == null || _session?.RoomState == null) return;
            int points = _session.RoomState.HintPoints;
            int cost = _session.RoomState.HintCost;
            string afford = cost <= 0 ? "free" : $"can afford {points / cost}";
            _pointsLabel.Text = $"Points: {points} · Cost: {cost} · {afford}";
            _pointsLabel.AddThemeColorOverride("font_color", cost <= 0 || points >= cost ? ThemeColors.Success : ThemeColors.Error);
            _requestButton.Disabled = !_session.Socket.Connected;
        }

        private void SetFeedback(string text, Color color)
        {
            _requestFeedback.Text = text;
            _requestFeedback.AddThemeColorOverride("font_color", color);
        }

        /// <summary>Called by the owning SlotTrackerControl when the slot goes away.</summary>
        public void Detach()
        {
            GameNames.Updated -= OnGameNamesUpdated;
        }
    }
}
