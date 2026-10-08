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

/// <summary>
/// Per-slot controller. Its <see cref="AP_Atlas.Core.SlotModel"/> owns the session, its events and the slot's logic; this
/// owns every per-slot view, and updates them when the model reports changes (once per frame).
/// The node itself renders only the Text Client (mounted in the bottom terminal pane);
/// the other views are exposed as properties and mounted into the top content pane by MainTrackerWindow.
/// </summary>
public partial class SlotTrackerControl : MarginContainer
{
    /// <summary>The slot itself: its session and the session's events, the chat, hints and goal.</summary>
    public AP_Atlas.Core.SlotModel Model { get; }
    public ArchipelagoSession Session => Model.Session;
    public string ProfileId => Model.ProfileId;
    public string SlotName => _slotName;
    public bool IsFullyLoaded => Model.Logic.Loaded;
    public int TotalLocationsCount => Model.TotalLocationsCount;
    public int CheckedLocationsCount => Model.CheckedLocationsCount;
    public int ActiveLogicCount => Model.ActiveLogicCount;

    /// <summary>Raised on the main thread whenever items, checks, hints or logic change.</summary>
    public event Action StateChanged;

    /// <summary>Whether this slot's goal can be completed with what it has now (go mode). Null: not known.</summary>
    public bool? GoalInLogic => Model.Logic.GoalInLogic;

    /// <summary>The Logic Tracker says the goal is in logic (go mode).</summary>
    public bool GoModeShown => _goModeLabel != null && _goModeLabel.Visible;

    /// <summary>What the Logic Tracker shows instead of its list (race mode, an engine problem), or null while the list shows.</summary>
    public string LogicNoticeShown => _logicNotice != null && _logicNotice.Visible ? _logicNoticeText.Text : null;

    /// <summary>The server says this slot reached its goal (its goal message, or a status check after connecting).</summary>
    public bool GoalCompleted => Model.GoalCompleted;

    /// <summary>Asks the server, once per connection, whether this slot already reached its goal.</summary>
    public void EnsureGoalStatus() => Model.EnsureGoalStatus();

    /// <summary>Logic is running and finished evaluating every item received (not starting or rebuilding).</summary>
    public bool LogicSettled => Model.Logic.Settled;

    // --- Per-slot views (mounted by MainTrackerWindow) ---
    public AP_Atlas.UI.MapTrackerControl MapTracker => _mapTracker;
    public AP_Atlas.Core.PopTracker.ProgressionTrackerControl ProgressionTracker => _progressionTracker;
    public Control LogicTrackerView => _logicView;
    public Control ItemHistoryView => _historyView;
    public Control HintsView => _hintTracker;

    /// <summary>
    /// Set by MainTrackerWindow before the node enters the tree: answers "is location X in logic for slot N?"
    /// using another connected slot of the same multiworld, or null when that slot isn't tracked here.
    /// </summary>
    public Func<int, long, bool?> ResolveOtherSlotLogic { get; set; }

    /// <summary>Set by MainTrackerWindow: shows a toast notification.</summary>
    public Action<string, Color> ShowToast { get; set; }

    private AP_Atlas.UI.MapTrackerControl _mapTracker;
    private AP_Atlas.Core.PopTracker.ProgressionTrackerControl _progressionTracker;
    private MarginContainer _logicView;
    private MarginContainer _historyView;
    private AP_Atlas.UI.HintTrackerControl _hintTracker;

    private string _slotName;
    private AppSettings _appSettings;
    private Dictionary<string, object> _slotData;
    private Action<string> _updateGlobalStatus;
    private Action<string> _appendDebugLog;

    // --- Logic Tracker view ---
    private Label _engineStatusLabel, _goModeLabel;
    private Tree _logicTree;

    // --- Item History view ---
    private AP_Atlas.UI.AtlasTable _historyTable;
    // A row per item received, in order, made once each (the markers are drawn as the table fills).
    private readonly List<AP_Atlas.UI.AtlasTable.Row> _historyRows = new();
    private Tree _uncollectedTree;
    private Label _uncollectedHeaderLabel;
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

    private bool _nextChatAltBg = false;

    public SlotTrackerControl(AP_Atlas.Core.SlotModel model, AppSettings appSettings, Action<string> updateGlobalStatus, Action<string> appendDebugLog)
    {
        Model = model;
        _slotName = model.SlotName;
        _appSettings = appSettings;
        _updateGlobalStatus = updateGlobalStatus;
        _slotData = model.SlotData;
        _appendDebugLog = appendDebugLog;
        Name = _slotName;
    }

    public override void _Ready()
    {
        using var __perf = AP_Atlas.Core.PerfMonitor.Measure($"[{_slotName}] Building its views");
        AddThemeConstantOverride("margin_left", 10);
        AddThemeConstantOverride("margin_top", 10);
        AddThemeConstantOverride("margin_right", 10);
        AddThemeConstantOverride("margin_bottom", 10);

        _progressionTracker = new AP_Atlas.Core.PopTracker.ProgressionTrackerControl();
        _progressionTracker.Initialize(Session, Model.Logic.Engine, ProfileId, _slotName, _appSettings, AppendDebugLog);

        _progressionTracker.ItemPicked += name => Inspect(ItemTargetByName(name));
        _progressionTracker.ScriptState = ScriptStateOf;
        _progressionTracker.SeedSettings = SeedSettings;
        _progressionTracker.ScriptStopReason = () => PackScripts?.StopReason;
        _progressionTracker.EmptyState = LogicEmptyState;
        _progressionTracker.RefreshMarkers(); // the empty state shows from the start, not after the first item
        _progressionTracker.MarkerLookup = name =>
        {
            var a = AP_Atlas.Core.Annotations.Get(AnnotationKey, AP_Atlas.Core.Annotations.ItemKey(FindItemId(name)));
            return (a?.Flag ?? 0, AP_Atlas.Core.Annotations.IsSpecialItem(Game, name), !string.IsNullOrWhiteSpace(a?.Note));
        };

        _mapTracker = new AP_Atlas.UI.MapTrackerControl(_appSettings);
        _mapTracker.SetSession(Session);
        _mapTracker.GameName = Game;
        _mapTracker.FindPackRequested += () => FindMapPack?.Invoke(Game);
        _mapTracker.PinPicked += (mapId, pinName, ids) =>
        {
            // A pin with one check is that location; a pin covering several opens the pin's own view.
            if (ids.Count == 1) Inspect(LocationTarget(ids[0]));
            else Inspect(AP_Atlas.Core.InspectTarget.ForPackLocation(ProfileId, _slotName, mapId, pinName));
        };
        _mapTracker.MapPicked += mapId => Inspect(AP_Atlas.Core.InspectTarget.ForMap(ProfileId, _slotName, mapId));
        _mapTracker.FollowToggled += on =>
        {
            _appSettings.MapFollowGame[FollowKey] = on;
            DataManager.SaveSettingsSoon(_appSettings);
        };
        _mapTracker.IsExcluded = IsExcluded;
        _mapTracker.MarkerLookup = id =>
        {
            var a = AP_Atlas.Core.Annotations.Get(AnnotationKey, AP_Atlas.Core.Annotations.LocationKey(id));
            return (a?.Flag ?? 0, AP_Atlas.Core.Annotations.IsSpecialLocation(Game, Session.Locations.GetLocationNameFromId(id)));
        };

        BuildLogicTrackerView();
        BuildItemHistoryView();
        BuildTextClientTab();
        // Each view refreshes only while it shows (at most once a frame); a hidden one catches up when it's shown.
        _logicRefresh = new AP_Atlas.UI.ViewRefresh(_logicView, () =>
        {
            bool full = _logicFullPending;
            _logicFullPending = false;
            RenderLogicTreeNow(full);
        }, $"drawing {_slotName}'s Logic Tracker");
        _historyRefresh = new AP_Atlas.UI.ViewRefresh(_historyView, RenderItemHistory, $"drawing {_slotName}'s Item History");
        _keyItemsRefresh = new AP_Atlas.UI.ViewRefresh(_progressionTracker, _progressionTracker.UpdateFromSession, $"drawing {_slotName}'s Key Items");
        _chatRefresh = new AP_Atlas.UI.ViewRefresh(this, ShowNewChatLinesNow, $"drawing {_slotName}'s text client");

        _hintTracker = new AP_Atlas.UI.HintTrackerControl();
        _hintTracker.Initialize(Session, _slotName, IsLocationInLogic,
            (slot, loc) => ResolveOtherSlotLogic?.Invoke(slot, loc),
            (msg, color) => ShowToast?.Invoke(msg, color), _appSettings);
        _hintTracker.LogicHidden = () => LogicHidden;
        _hintTracker.Say = text => Model.SayAsync(text);
        _hintTracker.HintPicked += (hint, part) =>
        {
            string receiverGame = Session.Players.GetPlayerInfo(hint.ReceivingPlayer)?.Game;
            switch (part)
            {
                case AP_Atlas.UI.HintTrackerControl.HintPart.Item:
                    Inspect(ItemTargetFor(hint.ReceivingPlayer, hint.ItemId, Session.Items.GetItemName(hint.ItemId, receiverGame)));
                    break;
                case AP_Atlas.UI.HintTrackerControl.HintPart.Location:
                    Inspect(LocationTargetFor(hint.FindingPlayer, hint.LocationId));
                    break;
                case AP_Atlas.UI.HintTrackerControl.HintPart.Receiver:
                    Inspect(PlayerTarget(hint.ReceivingPlayer));
                    break;
                case AP_Atlas.UI.HintTrackerControl.HintPart.Finder:
                    Inspect(PlayerTarget(hint.FindingPlayer));
                    break;
                default:
                    Inspect(HintTarget(hint));
                    break;
            }
        };
        _hintTracker.MarkerLookup = hint =>
        {
            // Items are marked in the receiver's game; locations in the finder's world (flags only for this slot's own).
            string receiverGame = Session.Players.GetPlayerInfo(hint.ReceivingPlayer)?.Game;
            string finderGame = Session.Players.GetPlayerInfo(hint.FindingPlayer)?.Game;
            var itemA = hint.ReceivingPlayer == PlayerSlot ? AP_Atlas.Core.Annotations.Get(AnnotationKey, AP_Atlas.Core.Annotations.ItemKey(hint.ItemId)) : null;
            var locA = hint.FindingPlayer == PlayerSlot ? AP_Atlas.Core.Annotations.Get(AnnotationKey, AP_Atlas.Core.Annotations.LocationKey(hint.LocationId)) : null;
            bool itemSpecial = AP_Atlas.Core.Annotations.IsSpecialItem(receiverGame, Session.Items.GetItemName(hint.ItemId, receiverGame));
            bool locSpecial = AP_Atlas.Core.Annotations.IsSpecialLocation(finderGame, Session.Locations.GetLocationNameFromId(hint.LocationId, finderGame));
            return (itemA?.Flag ?? 0, itemSpecial, !string.IsNullOrWhiteSpace(itemA?.Note), locA?.Flag ?? 0, locSpecial, !string.IsNullOrWhiteSpace(locA?.Note));
        };
        AP_Atlas.Core.Annotations.Changed += OnAnnotationsChanged;
        AP_Atlas.Core.PopTracker.PackFixes.Changed += OnPackFixesChanged;
        AP_Atlas.Core.PopTracker.PopTrackerPackLoader.PacksChanged += OnPacksChanged;
        // Keep this slot's options for offline use (setting indicators, the Pack Doctor).
        DataManager.SaveSlotData(ProfileId, _slotName, Game, _slotData);
        AP_Atlas.UI.Ui.Defer(this, RequestGameNames);
        AP_Atlas.UI.Ui.Defer(this, OfferYamlExclusions);
        _specialSignature = string.Join("|", AP_Atlas.Core.Annotations.SpecialItemNames(Game).OrderBy(n => n));
        _exclusionSignature = ExclusionSignature();

        AP_Atlas.Core.ThemeColors.AccentChanged += OnAccentChanged;
        // The model hears the session (items, checks, hints, messages) and runs logic, and reports here once per frame.
        Model.Changed += OnModelChanged;

        AP_Atlas.UI.Ui.Defer(this, LoadMapPack);
        AP_Atlas.UI.Ui.Defer(this, RefreshAllViews);
    }

    public void AppendDebugLog(string msg)
    {
        GD.Print(msg);
        _appendDebugLog?.Invoke(msg);
    }

    private void SetStatus(string msg)
    {
        string clean = msg.Replace("Status: ", "");
        if (_engineStatusLabel != null) _engineStatusLabel.Text = clean;
        _updateGlobalStatus?.Invoke($"{_slotName}: {clean}");
    }

    // =====================================================================
    // The model's changes (main thread, at most once per frame)
    // =====================================================================

    /// <summary>Updates the views for everything that changed since the last frame, then tells the window once.</summary>
    private void OnModelChanged(AP_Atlas.Core.SlotChange change)
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        bool items = change.HasFlag(AP_Atlas.Core.SlotChange.Items);
        bool checks = change.HasFlag(AP_Atlas.Core.SlotChange.Checks);
        if (items)
        {
            UpdateItemHistoryUI();
            FeedNewItemsToScripts();
            UpdateKeyItemsUI();
        }
        if (checks) FeedNewChecksToScripts();
        if (change.HasFlag(AP_Atlas.Core.SlotChange.EngineStarted)) OnEngineStarted();
        if (change.HasFlag(AP_Atlas.Core.SlotChange.Logic)) ShowLogic();
        else if (change.HasFlag(AP_Atlas.Core.SlotChange.Race)) SyncLogicViews();
        if (change.HasFlag(AP_Atlas.Core.SlotChange.Hints)) _hintTracker?.SetHints(Model.CurrentHints);
        if (change.HasFlag(AP_Atlas.Core.SlotChange.Messages)) OnNewChatLines();
        RaiseStateChanged();
    }

    /// <summary>Whether this slot's logic engine considers the location reachable; null while the engine isn't running or logic is hidden.</summary>
    public bool? IsLocationInLogic(long locationId) => Model.IsLocationInLogic(locationId);

    // =====================================================================
    // Properties panel support
    // =====================================================================

    private Button _filterItemsMarked;

    public LogicEngineManager LogicEngine => Model.Logic.Engine;
    public bool EngineRunning => Model.Logic.Running;
    /// <summary>The slot's own engine failures in the last ten minutes (see SlotLogic.RecentFailures).</summary>
    public int EngineFailures => Model.Logic.RecentFailures;
    public AP_Atlas.Core.PopTracker.LoadedPack Pack { get; private set; }
    public Archipelago.MultiClient.Net.Models.Hint[] CurrentHints => Model.CurrentHints;
    public IReadOnlyList<AP_Atlas.Core.ChatEntry> ChatHistory => Model.Chat;
    public string AnnotationKey => Model.AnnotationKey;
    public string Game => Session?.ConnectionInfo?.Game ?? "";
    public int PlayerSlot => Session?.ConnectionInfo?.Slot ?? -1;
    public int Team => Session?.ConnectionInfo?.Team ?? -1;
    public bool IsLocationReachable(long id) => Model.IsLocationReachable(id);
    public bool IsLocationHinted(long id) => Model.HintedLocations.Contains(id);
    public bool IsExcludedLocation(long id) => IsExcluded(id);
    public bool IsGlitchedLocation(long id) => Model.IsGlitchedLocation(id);
    public int ReachableCount => Model.Logic.Reachable.Count;
    public string MapPackName => Pack?.Manifest?.Name;

    // --- Targets (identities for the Properties panel) ---

    public AP_Atlas.Core.InspectTarget LocationTarget(long locationId) =>
        AP_Atlas.Core.InspectTarget.ForLocation(ProfileId, _slotName, PlayerSlot, locationId);

    public AP_Atlas.Core.InspectTarget LocationTargetFor(int player, long locationId) =>
        AP_Atlas.Core.InspectTarget.ForLocation(ProfileId, _slotName, player, locationId);

    public AP_Atlas.Core.InspectTarget ItemTarget(long itemId, string itemName) =>
        AP_Atlas.Core.InspectTarget.ForItem(ProfileId, _slotName, PlayerSlot, itemId, itemName);

    public AP_Atlas.Core.InspectTarget ItemTargetFor(int player, long itemId, string itemName) =>
        AP_Atlas.Core.InspectTarget.ForItem(ProfileId, _slotName, player, itemId, itemName);

    public AP_Atlas.Core.InspectTarget ReceivedItemTarget(int receiptIndex)
    {
        var item = Session.Items.AllItemsReceived[receiptIndex];
        return AP_Atlas.Core.InspectTarget.ForItem(ProfileId, _slotName, PlayerSlot, item.ItemId, item.ItemName, receiptIndex);
    }

    /// <summary>An item of this slot's game by name (id from the item pool, or the data package).</summary>
    public AP_Atlas.Core.InspectTarget ItemTargetByName(string itemName)
    {
        long id = FindItemId(itemName);
        return ItemTarget(id, itemName);
    }

    public long FindItemId(string itemName)
    {
        if (string.IsNullOrEmpty(itemName)) return 0;
        var pooled = Model.Logic.Engine.LastItemPool?.FirstOrDefault(p => string.Equals(p.Name, itemName, StringComparison.OrdinalIgnoreCase));
        if (pooled != null) return pooled.Id;
        var received = Session?.Items?.AllItemsReceived?.FirstOrDefault(i => string.Equals(i.ItemName, itemName, StringComparison.OrdinalIgnoreCase));
        if (received != null) return received.ItemId;
        // Not in the pool or received yet: check this slot's own hints.
        var hinted = CurrentHints.FirstOrDefault(h => h.ReceivingPlayer == PlayerSlot &&
            string.Equals(Session.Items.GetItemName(h.ItemId, Game), itemName, StringComparison.OrdinalIgnoreCase));
        return hinted?.ItemId ?? 0;
    }

    public AP_Atlas.Core.InspectTarget PlayerTarget(int player) =>
        AP_Atlas.Core.InspectTarget.ForPlayer(ProfileId, _slotName, player);

    public AP_Atlas.Core.InspectTarget HintTarget(Archipelago.MultiClient.Net.Models.Hint h) =>
        AP_Atlas.Core.InspectTarget.ForHint(ProfileId, _slotName, h.FindingPlayer, h.LocationId);

    private static void Inspect(AP_Atlas.Core.InspectTarget target) => AP_Atlas.Core.Inspector.Inspect(target);

    // --- Logic history ---

    /// <summary>When this slot's logic first reached a location: step number (0 = base logic), overall order and unlocking item.</summary>
    public (int Step, int Order, string ItemName)? UnlockStepOf(long locationId) => Model.UnlockStepOf(locationId);

    /// <summary>Logic steps a received item opened, with how many checks each.</summary>
    public List<(int Step, int Count)> StepsUnlockedBy(string itemName) => Model.StepsUnlockedBy(itemName);

    public int LogicStepCount => Model.LogicStepCount;

    /// <summary>The checks of a logic step that aren't excluded (by the seed or by you).</summary>
    private List<long> ShownLocs(IReadOnlyList<long> locs) => Model.ShownLocations(locs);

    // --- Cached lookups (one bridge or server request per question) ---

    /// <summary>The engine's "why" for a location, cached until this slot's items change. Null in race mode or while logic isn't running.</summary>
    public System.Threading.Tasks.Task<LogicExplanation> ExplainLocationAsync(long locationId)
    {
        // Race mode: never ask the engine why.
        if (RaceRestricted) return System.Threading.Tasks.Task.FromResult<LogicExplanation>(null);
        return Model.Logic.ExplainAsync(locationId);
    }

    /// <summary>What a checked location held. Only for checked locations, so it never spoils anything.</summary>
    public System.Threading.Tasks.Task<ScoutedItemInfo> ScoutCheckedLocationAsync(long locationId) => Model.ScoutCheckedLocationAsync(locationId);

    /// <summary>The server's item name groups for a game (fetched once per game per connection).</summary>
    public System.Threading.Tasks.Task<Dictionary<string, string[]>> ItemGroupsAsync(string game) => Model.ItemGroupsAsync(game);

    public System.Threading.Tasks.Task<Dictionary<string, string[]>> LocationGroupsAsync(string game) => Model.LocationGroupsAsync(game);

    /// <summary>A player's client status (connected / playing / goal), refreshed at most every 30 seconds.</summary>
    public System.Threading.Tasks.Task<ArchipelagoClientState?> ClientStatusAsync(int player) => Model.ClientStatusAsync(player);

    // --- Markers (flags, notes, special) ---

    private bool IsItemMarked(long itemId, string itemName) =>
        AP_Atlas.Core.Annotations.Get(AnnotationKey, AP_Atlas.Core.Annotations.ItemKey(itemId)) != null ||
        AP_Atlas.Core.Annotations.IsSpecialItem(Game, itemName);

    private void ApplyItemMarker(TreeItem row, int column, long itemId, string itemName)
    {
        var a = AP_Atlas.Core.Annotations.Get(AnnotationKey, AP_Atlas.Core.Annotations.ItemKey(itemId));
        bool special = AP_Atlas.Core.Annotations.IsSpecialItem(Game, itemName);
        row.SetIcon(column, AP_Atlas.Core.Annotations.MarkerIcon(a?.Flag ?? 0, special, !string.IsNullOrWhiteSpace(a?.Note)));
        row.SetIconMaxWidth(column, Math.Max(16, _appSettings.ContentFontSize * 2));
        if (special) row.SetCustomBgColor(column, AP_Atlas.Core.Annotations.SpecialBg);
    }

    private string _specialSignature = "";
    private string _exclusionSignature = "";

    private string ExclusionSignature() =>
        string.Join(",", AP_Atlas.Core.Annotations.ExclusionOverridesFor(AnnotationKey).OrderBy(kv => kv.Key).Select(kv => kv.Key + (kv.Value ? "x" : "i")));

    /// <summary>Re-applies flag/note/special markers everywhere after the user changed one.</summary>
    private void OnAnnotationsChanged()
    {
        if (Session == null) return;
        string exclusions = ExclusionSignature();
        if (exclusions != _exclusionSignature)
        {
            // An exclusion was switched: redraw the logic list and map counts without the excluded checks.
            _exclusionSignature = exclusions;
            RenderLogicTree(forceFull: true);
            RaiseStateChanged();
        }
        ApplyAllLogicMarkers();

        if (_filterItemsMarked?.ButtonPressed == true) UpdateItemHistoryUI(); // membership changed: the rows shown change
        else
        {
            _historyTable?.Render(); // the markers are drawn as the table fills its rows on screen
            foreach (var kv in _uncollectedRows) ApplyItemMarker(kv.Value, 0, kv.Key.id, kv.Key.name);
        }

        _hintTracker?.Refresh();
        _progressionTracker?.RefreshMarkers();
        _mapTracker?.RefreshMarkers();

        // Chat highlights special items; redraw only when this game's special list actually changed.
        string sig = string.Join("|", AP_Atlas.Core.Annotations.SpecialItemNames(Game).OrderBy(n => n));
        if (sig != _specialSignature)
        {
            _specialSignature = sig;
            RedrawChat();
        }
    }

    // --- Reveal ("Find in…") ---

    public void RevealLogicRow(long locationId)
    {
        _logicRefresh?.Flush();
        if (_logicTree == null || !_logicRows.TryGetValue(locationId, out var row)) return;
        if (!row.Visible && _logicFlaggedOnly != null) _logicFlaggedOnly.ButtonPressed = false;
        row.Select(1);
        _logicTree.ScrollToItem(row, true);
    }

    /// <summary>Whether the Logic Tracker lists the location (from the slot's logic, so it's right while the view is hidden too).</summary>
    public bool HasLogicRow(long locationId) => Model.UnlockStepOf(locationId) != null;

    public void RevealHistory(AP_Atlas.Core.InspectTarget target)
    {
        _historyRefresh?.Flush();
        if (_historyTable == null) return;
        string key = target.ReceiptIndex.ToString();
        if (target.ReceiptIndex >= 0 && _historyTable.ShownRows.Any(row => row.Key == key))
        {
            _historyTable.Select(key, scrollTo: true);
            return;
        }
        // Not a specific copy (or filtered out): search by name.
        _historyTable.SearchBox.Text = target.ItemName ?? "";
        _historyTable.Render();
        UpdateItemHistoryUI();
    }

    public void RevealHint(int findingPlayer, long locationId) => _hintTracker?.Reveal(findingPlayer, locationId);

    public void FilterHints(string text) => _hintTracker?.FilterByText(text);

    /// <summary>This game's special items: how many distinct ones this slot has received, of how many marked.</summary>
    public (int Collected, int Total) SpecialItemProgress()
    {
        var names = AP_Atlas.Core.Annotations.SpecialItemNames(Game).ToList();
        if (names.Count == 0 || Session == null) return (0, names.Count);
        var received = new HashSet<string>(Session.Items.AllItemsReceived.Select(i => i.ItemName ?? ""), StringComparer.OrdinalIgnoreCase);
        return (names.Count(received.Contains), names.Count);
    }

    /// <summary>This game's special locations: how many this slot has checked, of how many marked that exist in this world.</summary>
    public (int Checked, int Total) SpecialLocationProgress()
    {
        var names = AP_Atlas.Core.Annotations.SpecialLocationNames(Game).ToList();
        if (names.Count == 0 || Session == null) return (0, 0);
        var checkedNames = new HashSet<string>(Session.Locations.AllLocationsChecked.Select(id => Session.Locations.GetLocationNameFromId(id) ?? ""), StringComparer.OrdinalIgnoreCase);
        var allNames = new HashSet<string>(Session.Locations.AllLocations.Select(id => Session.Locations.GetLocationNameFromId(id) ?? ""), StringComparer.OrdinalIgnoreCase);
        var present = names.Where(allNames.Contains).ToList();
        return (present.Count(checkedNames.Contains), present.Count);
    }

    public bool RevealOnMap(long locationId) => _mapTracker?.RevealLocation(locationId) ?? false;

    public bool IsOnMap(long locationId) => _mapTracker?.HasLocation(locationId) ?? false;

    public void RevealKeyItem(string itemName)
    {
        _keyItemsRefresh?.Flush();
        _progressionTracker?.RevealItem(itemName);
    }

    private void OnAccentChanged()
    {
        RenderLogicTree(forceFull: true);
        UpdateKeyItemsUI();
    }

    private bool _ended;

    /// <summary>Whether the slot has ended (replaced, deleted, or freed with the window).</summary>
    public bool Ended => _ended;

    /// <summary>
    /// Ends the slot: it stops listening (to its model and Atlas's shared events), closes its model (which stops the
    /// slot's logic engine) and frees its views. The window calls it when the slot is replaced or deleted. Leaving the
    /// tree doesn't end it, so the panel can be moved (docking, pop-outs) without losing its engine, views or events.
    /// </summary>
    public void EndSlot()
    {
        if (_ended) return;
        _ended = true;
        AP_Atlas.Core.ThemeColors.AccentChanged -= OnAccentChanged;
        AP_Atlas.Core.Annotations.Changed -= OnAnnotationsChanged;
        AP_Atlas.Core.PopTracker.PackFixes.Changed -= OnPackFixesChanged;
        AP_Atlas.Core.PopTracker.PopTrackerPackLoader.PacksChanged -= OnPacksChanged;
        StopWatchingForScripts();
        Session.Socket.PacketReceived -= OnDataPackagePacket;
        Model.Changed -= OnModelChanged;
        Model.Dispose();
        _hintTracker?.Detach();

        // The per-slot views live in the shared content pane (or nowhere), not under this node,
        // so they must be freed explicitly to avoid orphaned nodes.
        foreach (Node view in new Node[] { _mapTracker?.SidebarContent, _mapTracker, _progressionTracker, _logicView, _historyView, _hintTracker })
        {
            if (view != null && GodotObject.IsInstanceValid(view)) view.QueueFree();
        }
        // Its pack's images can be freed now (they're kept while another slot uses the pack, or it's the last one used).
        _packImages?.Dispose();
        _packImages = null;
    }

    public override void _Notification(int what)
    {
        // Freed without being ended (with the window as Atlas closes): end it now, so nothing calls back into a freed panel
        // and its engine stops.
        if (what == NotificationPredelete) EndSlot();
    }
}
