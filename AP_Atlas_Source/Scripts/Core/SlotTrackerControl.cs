using Godot;
using System;
using System.Collections.Generic;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Packets;
using System.Linq;

/// <summary>
/// Per-slot controller. Owns the Archipelago session, the logic engine and every per-slot view.
/// The node itself renders only the Text Client (mounted in the bottom terminal pane);
/// the other views are exposed as properties and mounted into the top content pane by MainTrackerWindow.
/// </summary>
public partial class SlotTrackerControl : MarginContainer
{
    public ArchipelagoSession Session { get; private set; }
    public string ProfileId { get; private set; }
    public string SlotName => _slotName;
    public bool IsFullyLoaded { get; private set; } = false;
    public int TotalLocationsCount => Session?.Locations?.AllLocations?.Count ?? 0;
    public int CheckedLocationsCount => Session?.Locations?.AllLocationsChecked?.Count ?? 0;
    public int ActiveLogicCount
    {
        get
        {
            if (_knownReachableLocations == null || Session == null) return 0;
            int count = 0;
            foreach (var loc in _knownReachableLocations)
            {
                if (!Session.Locations.AllLocationsChecked.Contains(loc) &&
                    (_logicEngine == null || !_logicEngine.LastExcludedLocations.Contains(loc)))
                {
                    count++;
                }
            }
            return count;
        }
    }

    /// <summary>Raised on the main thread whenever items, checks, hints or logic change.</summary>
    public event Action StateChanged;

    // --- Per-slot views (mounted by MainTrackerWindow) ---
    public AP_Atlas.UI.MapTrackerControl MapTracker => _mapTracker;
    public AP_Atlas.Core.PopTracker.ProgressionTrackerControl ProgressionTracker => _progressionTracker;
    public Control LogicTrackerView => _logicView;
    public Control ItemHistoryView => _historyView;

    private AP_Atlas.UI.MapTrackerControl _mapTracker;
    private AP_Atlas.Core.PopTracker.ProgressionTrackerControl _progressionTracker;
    private MarginContainer _logicView;
    private MarginContainer _historyView;

    private string _slotName;
    private AppSettings _appSettings;
    private Dictionary<string, object> _slotData;
    private Action<string> _updateGlobalStatus;
    private Action<string> _appendDebugLog;

    // --- Logic state ---
    private LogicEngineManager _logicEngine;
    private bool _engineRunning = false;
    private bool _logicBusy = false;
    private bool _logicDirty = false;
    private int _lastEvaluatedItemCount = 0;
    private readonly List<long> _chronologicalInventory = new List<long>();
    private readonly List<(string ItemName, List<long> UnlockedLocs)> _progressionLog = new();
    private readonly HashSet<long> _knownReachableLocations = new HashSet<long>();
    private readonly HashSet<long> _knownHintedLocations = new HashSet<long>();

    // --- Logic Tracker view ---
    private Label _engineStatusLabel;
    private Tree _logicTree;

    // --- Item History view ---
    private Tree _itemHistoryTree;
    private Tree _uncollectedTree;
    private Label _collectedHeaderLabel;
    private Label _uncollectedHeaderLabel;
    private LineEdit _itemSearchBox;
    private OptionButton _optHistorySort;
    private Button _filterItemsProgression;
    private Button _filterItemsUseful;
    private Button _filterItemsFiller;
    private Button _filterItemsTrap;

    // --- Text Client ---
    private ScrollContainer _chatScroll;
    private VBoxContainer _chatVBox;
    private LineEdit _chatInput;
    private Button _filterHints;
    private Button _filterChat;
    private Button _filterSystem;
    private Button _filterProgression;
    private Button _filterUseful;
    private Button _filterFiller;
    private Button _filterTrap;

    public class ChatEntry
    {
        public LogMessage APMessage { get; set; }
        public string SystemMessage { get; set; }
        public bool IsSystemMessage => SystemMessage != null;
    }

    private List<ChatEntry> _chatHistory = new();
    private bool _nextChatAltBg = false;

    public SlotTrackerControl(ArchipelagoSession session, string profileId, string slotName, AppSettings appSettings, Dictionary<string, object> slotData, Action<string> updateGlobalStatus, Action<string> appendDebugLog)
    {
        ProfileId = profileId;
        Session = session;
        _slotName = slotName;
        _appSettings = appSettings;
        _updateGlobalStatus = updateGlobalStatus;
        _slotData = slotData;
        _appendDebugLog = appendDebugLog;
        Name = slotName;
    }

    public override void _Ready()
    {
        AddThemeConstantOverride("margin_left", 10);
        AddThemeConstantOverride("margin_top", 10);
        AddThemeConstantOverride("margin_right", 10);
        AddThemeConstantOverride("margin_bottom", 10);

        _logicEngine = new LogicEngineManager(_appSettings.ArchipelagoInstallationPath, AppendDebugLog);

        _progressionTracker = new AP_Atlas.Core.PopTracker.ProgressionTrackerControl();
        _progressionTracker.Initialize(Session, _logicEngine, ProfileId, _slotName, AppendDebugLog);

        _mapTracker = new AP_Atlas.UI.MapTrackerControl(_appSettings);
        _mapTracker.SetSession(Session);

        BuildLogicTrackerView();
        BuildItemHistoryView();
        BuildTextClientTab();

        AppendSystemMessage($"[color=lime]Connected to {Session.ConnectionInfo.Game} as {_slotName}![/color]");

        // Session hooks. All of these fire on network threads, so every handler marshals to the main thread.
        Session.MessageLog.OnMessageReceived += OnAPMessageReceived;
        Session.Socket.SocketClosed += OnSocketClosed;
        Session.Items.ItemReceived += OnItemReceived;
        Session.Locations.CheckedLocationsUpdated += OnCheckedLocationsUpdated;

        Callable.From(LoadMapPackAsync).CallDeferred();
        Callable.From(InitializeLogicEngine).CallDeferred();
        Callable.From(RefreshAllViews).CallDeferred();
    }

    public void AppendDebugLog(string msg)
    {
        GD.Print(msg);
        _appendDebugLog?.Invoke(msg);
    }

    private void SetStatus(string msg)
    {
        string clean = msg.Replace("Status: ", "");
        if (_engineStatusLabel != null) _engineStatusLabel.Text = "Engine: " + clean;
        _updateGlobalStatus?.Invoke($"[{_slotName}] {clean}");
    }

    // =====================================================================
    // Session event handlers (network thread -> main thread)
    // =====================================================================

    private void OnItemReceived(ReceivedItemsHelper helper)
    {
        Callable.From(() =>
        {
            UpdateItemHistoryUI();
            UpdateKeyItemsUI();
            QueueLogicRefresh();
            RaiseStateChanged();
        }).CallDeferred();
    }

    private void OnCheckedLocationsUpdated(System.Collections.ObjectModel.ReadOnlyCollection<long> newCheckedLocations)
    {
        Callable.From(() =>
        {
            QueueLogicRefresh();
            RaiseStateChanged();
        }).CallDeferred();
    }

    private void RaiseStateChanged()
    {
        if (_mapTracker != null && Session != null)
        {
            _mapTracker.UpdateLogicColors(_knownReachableLocations, Session.Locations.AllLocationsChecked, _knownHintedLocations);
        }
        StateChanged?.Invoke();
    }

    private void RefreshAllViews()
    {
        UpdateItemHistoryUI();
        UpdateKeyItemsUI();
        RenderLogicTree();
        RaiseStateChanged();
    }

    // =====================================================================
    // Map pack
    // =====================================================================

    private async void LoadMapPackAsync()
    {
        string game = Session?.ConnectionInfo?.Game;
        if (string.IsNullOrEmpty(game)) return;

        var pack = await System.Threading.Tasks.Task.Run(() =>
            AP_Atlas.Core.PopTracker.PopTrackerPackLoader.LoadPackForGame(game, null));

        if (!GodotObject.IsInstanceValid(this) || _mapTracker == null) return;

        if (pack != null)
        {
            _mapTracker.LoadPack(pack);
            AppendDebugLog($"[MapTracker] Loaded pack '{pack.Manifest?.Name}' for {game}.");
            RaiseStateChanged();
        }
        else
        {
            AppendDebugLog($"[MapTracker] No installed map pack matches '{game}'.");
        }
    }

    // =====================================================================
    // Logic engine
    // =====================================================================

    private async void InitializeLogicEngine()
    {
        if (!_logicEngine.IsEngineInstalled())
        {
            SetStatus("Engine Missing (Use Settings Menu)");
            IsFullyLoaded = true;
            RaiseStateChanged();
            return;
        }

        if (Session.Locations.AllLocations.Count == 0)
        {
            AppendDebugLog("InitializeLogicEngine: 0 locations detected (TextOnly client). Skipping engine.");
            SetStatus("No Locations (TextOnly)");
            IsFullyLoaded = true;
            RaiseStateChanged();
            return;
        }

        SetStatus("Booting Engine...");
        try
        {
            AppendDebugLog($"InitializeLogicEngine: StartEngineAsync Game='{Session.ConnectionInfo.Game}', Slot='{_slotName}'");
            // No ConfigureAwait(false): the continuation must resume on Godot's main thread.
            bool started = await _logicEngine.StartEngineAsync(
                Session.ConnectionInfo.Game, _slotName, Session.ConnectionInfo.Slot, _slotData, Session.Locations.AllLocations);

            if (!GodotObject.IsInstanceValid(this)) return;
            AppendDebugLog($"InitializeLogicEngine: StartEngineAsync returned {started}");

            if (started)
            {
                _engineRunning = true;
                SetStatus("Engine Running");
                QueueLogicRefresh();
                UpdateItemHistoryUI(); // item pool is now available for "Not Yet Collected"
                UpdateKeyItemsUI();
            }
            else
            {
                SetStatus("Engine Boot Failed");
                IsFullyLoaded = true;
                RaiseStateChanged();
            }
        }
        catch (Exception ex)
        {
            AppendDebugLog("Logic Engine Init Error: " + ex.Message);
            SetStatus("Engine Error");
            IsFullyLoaded = true;
            RaiseStateChanged();
        }
    }

    /// <summary>Coalesces refresh requests so concurrent item bursts never run the engine loop twice in parallel.</summary>
    private void QueueLogicRefresh()
    {
        if (!_engineRunning) return;
        if (_logicBusy) { _logicDirty = true; return; }
        UpdateLogicAsync();
    }

    private async void UpdateLogicAsync()
    {
        _logicBusy = true;
        try
        {
            do
            {
                _logicDirty = false;
                await EvaluateLogicStepAsync();
                if (!GodotObject.IsInstanceValid(this)) return;
            } while (_logicDirty);

            RenderLogicTree();
            IsFullyLoaded = true;
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            AppendDebugLog($"UpdateLogic FATAL EXCEPTION: {ex.Message}");
            IsFullyLoaded = true;
        }
        finally
        {
            _logicBusy = false;
        }
    }

    private async System.Threading.Tasks.Task EvaluateLogicStepAsync()
    {
        var missingLocs = Session.Locations.AllLocations.Except(Session.Locations.AllLocationsChecked).ToList();
        if (missingLocs.Count == 0) return;

        var currentProgression = Session.Items.AllItemsReceived
            .Where(i => i.Flags.HasFlag(ItemFlags.Advancement) || i.Flags.HasFlag(ItemFlags.NeverExclude))
            .ToList();

        if (currentProgression.Count == _lastEvaluatedItemCount && _progressionLog.Count > 0) return;

        if (_lastEvaluatedItemCount == 0 && _progressionLog.Count == 0)
        {
            var initialReachable = await _logicEngine.GetReachableLocationsAsync(new List<long>(), missingLocs);
            if (initialReachable != null && initialReachable.Count > 0)
            {
                _knownReachableLocations.UnionWith(initialReachable);
                var validInitial = initialReachable.Where(l => !IsExcluded(l)).ToList();
                if (validInitial.Count > 0) _progressionLog.Add(("Starting Logic", validInitial));
            }
        }

        for (int i = _lastEvaluatedItemCount; i < currentProgression.Count; i++)
        {
            var netItem = currentProgression[i];
            _chronologicalInventory.Add(netItem.ItemId);

            var stepReachable = await _logicEngine.GetReachableLocationsAsync(_chronologicalInventory, missingLocs);
            if (stepReachable == null) continue;

            var newlyUnlocked = new List<long>();
            foreach (var loc in stepReachable)
            {
                if (_knownReachableLocations.Add(loc) && !IsExcluded(loc)) newlyUnlocked.Add(loc);
            }
            if (newlyUnlocked.Count > 0)
            {
                string itemName = Session.Items.GetItemName(netItem.ItemId) ?? "Unknown Item";
                _progressionLog.Add((itemName, newlyUnlocked));
            }
        }

        _lastEvaluatedItemCount = currentProgression.Count;
    }

    private bool IsExcluded(long loc) =>
        _logicEngine.LastExcludedLocations != null && _logicEngine.LastExcludedLocations.Contains(loc);

    // =====================================================================
    // Logic Tracker view
    // =====================================================================

    private void BuildLogicTrackerView()
    {
        _logicView = new MarginContainer { Name = "Logic Tracker", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _logicView.AddThemeConstantOverride("margin_left", 10);
        _logicView.AddThemeConstantOverride("margin_top", 10);
        _logicView.AddThemeConstantOverride("margin_right", 10);
        _logicView.AddThemeConstantOverride("margin_bottom", 10);

        var vbox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _logicView.AddChild(vbox);

        var toolbar = new HBoxContainer();
        vbox.AddChild(toolbar);
        _engineStatusLabel = new Label { Text = "Engine: Offline", SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right };
        toolbar.AddChild(_engineStatusLabel);

        _logicTree = new Tree { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, Columns = 3 };
        _logicTree.SetColumnTitle(0, "Order");
        _logicTree.SetColumnTitle(1, "Location");
        _logicTree.SetColumnTitle(2, "Unlocked By");
        _logicTree.SetColumnExpandRatio(0, 1);
        _logicTree.SetColumnExpandRatio(1, 6);
        _logicTree.SetColumnExpandRatio(2, 4);
        _logicTree.SetColumnCustomMinimumWidth(0, 50);
        _logicTree.SetColumnCustomMinimumWidth(1, 250);
        _logicTree.SetColumnCustomMinimumWidth(2, 150);
        _logicTree.HideRoot = true;
        _logicTree.ColumnTitlesVisible = true;
        _logicTree.SelectMode = Tree.SelectModeEnum.Row;
        _logicTree.AddThemeConstantOverride("v_separation", 6);
        vbox.AddChild(_logicTree);
        _logicTree.CreateItem();
    }

    private void RenderLogicTree()
    {
        if (_logicTree == null || Session == null) return;
        _logicTree.Clear();
        var root = _logicTree.CreateItem();

        if (_progressionLog.Count == 0)
        {
            var empty = _logicTree.CreateItem(root);
            empty.SetText(1, _engineRunning ? "No reachable checks found yet." : "Waiting for Logic Engine...");
            empty.SetCustomColor(1, Colors.Gray);
            return;
        }

        var dividerBg = new Godot.Color("#252836");
        var dividerFg = new Godot.Color("#4DD0E1");
        int orderCount = 1;
        int sectionIndex = 1;
        foreach (var step in _progressionLog)
        {
            if (step.UnlockedLocs == null || step.UnlockedLocs.Count == 0) continue;
            bool isBase = step.ItemName == "Starting Logic";

            var divider = _logicTree.CreateItem(root);
            divider.SetText(0, isBase ? "● Base" : $"● Step {sectionIndex}");
            divider.SetText(1, isBase ? "── Base Logic (Starting Reachable) ──" : $"── Unlocked by: {step.ItemName} ({step.UnlockedLocs.Count} checks) ──");
            divider.SetText(2, isBase ? "Starting Checks" : step.ItemName);
            for (int c = 0; c < 3; c++)
            {
                divider.SetSelectable(c, false);
                divider.SetCustomBgColor(c, dividerBg);
                divider.SetCustomColor(c, dividerFg);
            }
            sectionIndex++;

            foreach (var locId in step.UnlockedLocs)
            {
                var row = _logicTree.CreateItem(root);
                row.SetText(0, orderCount.ToString());
                row.SetText(1, Session.Locations.GetLocationNameFromId(locId) ?? "Unknown Check");
                row.SetText(2, step.ItemName);

                var rowBg = (orderCount % 2 == 0) ? new Godot.Color("#16161C") : new Godot.Color("#1F1F27");
                bool isChecked = Session.Locations.AllLocationsChecked.Contains(locId);
                for (int c = 0; c < 3; c++) row.SetCustomBgColor(c, rowBg);
                row.SetCustomColor(0, isChecked ? Colors.DimGray : Colors.LightGray);
                row.SetCustomColor(1, isChecked ? Colors.DimGray : Colors.White);
                row.SetCustomColor(2, isChecked ? Colors.DimGray : Colors.Plum);
                orderCount++;
            }
        }
    }

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

    private void UpdateItemHistoryUI()
    {
        if (_itemHistoryTree == null || _uncollectedTree == null || Session == null) return;

        _itemHistoryTree.Clear();
        var historyRoot = _itemHistoryTree.CreateItem();
        _uncollectedTree.Clear();
        var uncollectedRoot = _uncollectedTree.CreateItem();

        string search = _itemSearchBox?.Text?.Trim() ?? "";

        // 1. Collected items
        var allItems = Session.Items.AllItemsReceived.ToList();
        var collectedCounts = new Dictionary<long, int>();
        var collectedNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var displayCollected = new List<(int index, string itemName, string sender, string locationName, bool prog, bool useful, bool trap)>();

        for (int i = 0; i < allItems.Count; i++)
        {
            var item = allItems[i];
            collectedCounts[item.ItemId] = collectedCounts.GetValueOrDefault(item.ItemId) + 1;
            string itemName = item.ItemName ?? "Unknown Item";
            collectedNameCounts[itemName] = collectedNameCounts.GetValueOrDefault(itemName) + 1;

            bool prog = item.Flags.HasFlag(ItemFlags.Advancement);
            bool useful = item.Flags.HasFlag(ItemFlags.NeverExclude);
            bool trap = item.Flags.HasFlag(ItemFlags.Trap);
            if (!PassesItemFilter(prog, useful, trap)) continue;

            string sender = Session.Players.GetPlayerAlias(item.Player) ?? "Server";
            string locationName = item.LocationName ?? "Unknown Location";
            if (!string.IsNullOrEmpty(search) &&
                itemName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                sender.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                locationName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;

            displayCollected.Add((i + 1, itemName, sender, locationName, prog, useful, trap));
        }

        int sortMode = _optHistorySort?.Selected ?? 0;
        if (sortMode == 1) displayCollected.Reverse();
        else if (sortMode == 2) displayCollected = displayCollected.OrderBy(x => x.itemName).ToList();

        int shown = 0;
        foreach (var d in displayCollected)
        {
            var row = _itemHistoryTree.CreateItem(historyRoot);
            row.SetText(0, d.index.ToString());
            row.SetText(1, d.itemName);
            row.SetText(2, d.sender);
            row.SetText(3, d.locationName);
            var fg = ItemClassColor(d.prog, d.useful, d.trap);
            var bg = (shown % 2 == 0) ? Color.FromHtml("#16161C") : Color.FromHtml("#1F1F27");
            for (int c = 0; c < 4; c++) { row.SetCustomColor(c, fg); row.SetCustomBgColor(c, bg); }
            shown++;
        }

        _collectedHeaderLabel.Text = (string.IsNullOrEmpty(search) && AllItemFiltersOn)
            ? $"Collected ({allItems.Count})"
            : $"Collected ({shown} shown / {allItems.Count} total)";

        // 2. Not yet collected (needs the engine's item pool)
        var fullPool = _logicEngine?.LastItemPool;
        if (fullPool == null || fullPool.Count == 0)
        {
            _uncollectedHeaderLabel.Text = _engineRunning ? "Not Yet Collected (0 items)" : "Not Yet Collected (Waiting for Logic Engine...)";
            return;
        }

        var poolGrouped = new Dictionary<(long id, string name, int flags), int>();
        foreach (var pItem in fullPool)
        {
            var key = (pItem.Id, pItem.Name ?? "Unknown Item", pItem.Flags);
            poolGrouped[key] = poolGrouped.GetValueOrDefault(key) + 1;
        }

        var remainingById = new Dictionary<long, int>(collectedCounts);
        var remainingByName = new Dictionary<string, int>(collectedNameCounts, StringComparer.OrdinalIgnoreCase);
        var uncollected = new List<(long id, string name, int flags, int qty)>();
        int totalRemaining = 0;

        foreach (var kvp in poolGrouped)
        {
            int totalQty = kvp.Value;
            int received = 0;
            if (remainingById.TryGetValue(kvp.Key.id, out int byId) && byId > 0)
            {
                received = Math.Min(totalQty, byId);
                remainingById[kvp.Key.id] -= received;
            }
            else if (remainingByName.TryGetValue(kvp.Key.name, out int byName) && byName > 0)
            {
                received = Math.Min(totalQty, byName);
                remainingByName[kvp.Key.name] -= received;
            }
            int remaining = totalQty - received;
            if (remaining > 0)
            {
                uncollected.Add((kvp.Key.id, kvp.Key.name, kvp.Key.flags, remaining));
                totalRemaining += remaining;
            }
        }

        static int Priority(int flags)
        {
            if ((flags & 1) != 0) return 0; // Progression
            if ((flags & 2) != 0) return 1; // Useful
            if ((flags & 4) != 0) return 3; // Trap
            return 2;                       // Filler
        }

        bool alpha = _optHistorySort != null && _optHistorySort.Selected == 2;
        uncollected.Sort((a, b) =>
        {
            if (!alpha)
            {
                int p = Priority(a.flags).CompareTo(Priority(b.flags));
                if (p != 0) return p;
            }
            return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
        });

        int uShown = 0;
        foreach (var u in uncollected)
        {
            bool prog = (u.flags & 1) != 0;
            bool useful = (u.flags & 2) != 0;
            bool trap = (u.flags & 4) != 0;
            if (!PassesItemFilter(prog, useful, trap)) continue;

            string classText = prog ? "Progression" : (useful ? "Useful" : (trap ? "Trap" : "Filler"));
            if (!string.IsNullOrEmpty(search) &&
                u.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                classText.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;

            var row = _uncollectedTree.CreateItem(uncollectedRoot);
            row.SetText(0, u.name);
            row.SetText(1, classText);
            row.SetText(2, u.qty.ToString());
            var fg = ItemClassColor(prog, useful, trap);
            var bg = (uShown % 2 == 0) ? Color.FromHtml("#16161C") : Color.FromHtml("#1F1F27");
            for (int c = 0; c < 3; c++) { row.SetCustomColor(c, fg); row.SetCustomBgColor(c, bg); }
            uShown++;
        }

        _uncollectedHeaderLabel.Text = (string.IsNullOrEmpty(search) && AllItemFiltersOn)
            ? $"Not Yet Collected ({totalRemaining} remaining)"
            : $"Not Yet Collected ({uShown} shown / {totalRemaining} remaining)";
    }

    private void UpdateKeyItemsUI()
    {
        _progressionTracker?.UpdateFromSession();
    }

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

    private void OnSocketClosed(string reason)
    {
        Callable.From(() =>
        {
            AppendSystemMessage($"[color=red]Connection lost: {reason}[/color]");
            RaiseStateChanged();
        }).CallDeferred();
    }

    private void AppendSystemMessage(string bbcodeText, bool isReplay = false)
    {
        if (!isReplay)
        {
            _chatHistory.Add(new ChatEntry { SystemMessage = bbcodeText });
            if (_chatHistory.Count > 1000) _chatHistory.RemoveAt(0);
        }

        if (_filterSystem != null && !_filterSystem.ButtonPressed && !isReplay) return;

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

        var lbl = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Text = bbcodeText
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

        ScrollChatToBottom();
    }

    public void InjectEarlyMessages(IEnumerable<LogMessage> msgs)
    {
        if (_chatHistory.Count > 0) { AppendSystemMessage("[color=gray]--- Reconnected ---[/color]"); }

        foreach (var msg in msgs)
        {
            _chatHistory.Add(new ChatEntry { APMessage = msg });
            if (_chatHistory.Count > 1000) _chatHistory.RemoveAt(0);
            Callable.From(() => ProcessSingleMessage(msg)).CallDeferred();
        }
    }

    private void OnAPMessageReceived(LogMessage msg)
    {
        Callable.From(() =>
        {
            _chatHistory.Add(new ChatEntry { APMessage = msg });
            if (_chatHistory.Count > 1000) _chatHistory.RemoveAt(0);
            ProcessSingleMessage(msg);
        }).CallDeferred();
    }

    private void ProcessSingleMessage(LogMessage msg)
    {
        if (msg is HintItemSendLogMessage hintMsg)
        {
            var parts = hintMsg.Parts.OfType<Archipelago.MultiClient.Net.MessageLog.Parts.LocationMessagePart>();
            foreach (var part in parts)
            {
                _knownHintedLocations.Add(part.LocationId);
            }
            RaiseStateChanged();
        }
        if (ShouldFilterMessage(msg)) return;
        AppendMessageToChat(msg);
    }

    private void RedrawChat()
    {
        foreach (Node child in _chatVBox.GetChildren()) child.QueueFree();
        _nextChatAltBg = false;
        foreach (var entry in _chatHistory)
        {
            if (entry.IsSystemMessage)
            {
                if (_filterSystem == null || _filterSystem.ButtonPressed) AppendSystemMessage(entry.SystemMessage, true);
            }
            else
            {
                if (!ShouldFilterMessage(entry.APMessage))
                {
                    AppendMessageToChat(entry.APMessage);
                }
            }
        }
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

    private void AppendMessageToChat(LogMessage msg)
    {
        string text = "";
        foreach (var part in msg.Parts)
        {
            string color = "white";

            if (part is Archipelago.MultiClient.Net.MessageLog.Parts.ItemMessagePart itemPart)
            {
                if (itemPart.Flags.HasFlag(ItemFlags.Advancement)) color = "plum";
                else if (itemPart.Flags.HasFlag(ItemFlags.NeverExclude)) color = "slateblue";
                else if (itemPart.Flags.HasFlag(ItemFlags.Trap)) color = "salmon";
                else color = "cyan";
            }
            else if (part is Archipelago.MultiClient.Net.MessageLog.Parts.LocationMessagePart)
            {
                color = "green";
            }
            else if (part is Archipelago.MultiClient.Net.MessageLog.Parts.PlayerMessagePart playerPart)
            {
                color = playerPart.IsActivePlayer ? "magenta" : "yellow";
            }
            else
            {
                string colorName = part.Color.ToString().ToLower();
                if (colorName != "none" && colorName != "") { color = colorName; }
            }

            text += $"[color={color}]{part.Text}[/color]";
        }

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

        var lbl = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Text = text
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

        ScrollChatToBottom();
    }

    private async void ScrollChatToBottom()
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
        Session.Socket.SendPacketAsync(new SayPacket { Text = text });
    }

    public override void _ExitTree()
    {
        if (Session != null)
        {
            Session.MessageLog.OnMessageReceived -= OnAPMessageReceived;
            Session.Socket.SocketClosed -= OnSocketClosed;
            Session.Items.ItemReceived -= OnItemReceived;
            Session.Locations.CheckedLocationsUpdated -= OnCheckedLocationsUpdated;
        }
        _logicEngine?.StopEngine();

        // The per-slot views live in the shared content pane (or nowhere), not under this node,
        // so they must be freed explicitly to avoid orphaned nodes.
        foreach (Node view in new Node[] { _mapTracker?.SidebarContent, _mapTracker, _progressionTracker, _logicView, _historyView })
        {
            if (view != null && GodotObject.IsInstanceValid(view)) view.QueueFree();
        }
    }
}
