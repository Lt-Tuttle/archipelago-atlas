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
            if (_knownReachableLocations == null || Session == null || LogicHidden) return 0;
            // Polled every 0.5s by the sidebar; AllLocationsChecked is a list, so hash it once per call.
            var checkedLocs = new HashSet<long>(Session.Locations.AllLocationsChecked);
            int count = 0;
            foreach (var loc in _knownReachableLocations)
            {
                if (!checkedLocs.Contains(loc) && !IsExcluded(loc))
                {
                    count++;
                }
            }
            return count;
        }
    }

    /// <summary>Raised on the main thread whenever items, checks, hints or logic change.</summary>
    public event Action StateChanged;

    /// <summary>Whether this slot's goal can be completed with what it has now (go mode). Null: not known.</summary>
    public bool? GoalInLogic { get; private set; }

    /// <summary>The server says this slot reached its goal (its goal message, or a status check after connecting).</summary>
    public bool GoalCompleted { get; private set; }
    private bool _goalStatusAsked;

    /// <summary>Asks the server, once per connection, whether this slot already reached its goal.</summary>
    public void EnsureGoalStatus()
    {
        if (_goalStatusAsked || Session == null) return;
        _goalStatusAsked = true;
        AP_Atlas.Core.Async.Then(ClientStatusAsync(PlayerSlot), status =>
        {
            if (status == ArchipelagoClientState.ClientGoal)
                AP_Atlas.UI.Ui.Defer(this, () =>
                {
                    GoalCompleted = true;
                    RaiseStateChanged();
                });
        }, "asking the server whether this slot reached its goal");
    }

    /// <summary>Logic is running and finished evaluating every item received (not starting or rebuilding).</summary>
    public bool LogicSettled => _engineRunning && _startingLogicDone && !_logicBusy && !_logicDirty && EngineProblem == null;

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

        _logicEngine = new LogicEngineManager(AP_Atlas.Core.EngineSetup.AtlasEngine.Resolve(_appSettings), AppendDebugLog);
        _logicEngine.EngineExited += code => AP_Atlas.UI.Ui.Defer(this, () =>
        {
            if (_engineRunning) HandleEngineFailure($"the engine process exited with code {code}");
        });
        AP_Atlas.Core.EngineSetup.AtlasEngine.Changed += OnEngineChanged;
        AP_Atlas.Core.EngineSetup.AtlasEngine.PauseRequested += OnEnginePauseRequested;

        _progressionTracker = new AP_Atlas.Core.PopTracker.ProgressionTrackerControl();
        _progressionTracker.Initialize(Session, _logicEngine, ProfileId, _slotName, _appSettings, AppendDebugLog);

        _progressionTracker.ItemPicked += name => Inspect(ItemTargetByName(name));
        _progressionTracker.ScriptState = ScriptStateOf;
        _progressionTracker.SeedSettings = SeedSettings;
        _progressionTracker.MarkerLookup = name =>
        {
            var a = AP_Atlas.Core.Annotations.Get(AnnotationKey, AP_Atlas.Core.Annotations.ItemKey(FindItemId(name)));
            return (a?.Flag ?? 0, AP_Atlas.Core.Annotations.IsSpecialItem(Game, name), !string.IsNullOrWhiteSpace(a?.Note));
        };

        _mapTracker = new AP_Atlas.UI.MapTrackerControl(_appSettings);
        _mapTracker.SetSession(Session);
        _mapTracker.PinPicked += (mapId, pinName, ids) =>
        {
            // A pin with one check is that location; a pin covering several opens the pin's own view.
            if (ids.Count == 1) Inspect(LocationTarget(ids[0]));
            else Inspect(AP_Atlas.Core.InspectTarget.ForPackLocation(ProfileId, _slotName, mapId, pinName));
        };
        _mapTracker.MapPicked += mapId => Inspect(AP_Atlas.Core.InspectTarget.ForMap(ProfileId, _slotName, mapId));
        _mapTracker.IsExcluded = IsExcluded;
        _mapTracker.MarkerLookup = id =>
        {
            var a = AP_Atlas.Core.Annotations.Get(AnnotationKey, AP_Atlas.Core.Annotations.LocationKey(id));
            return (a?.Flag ?? 0, AP_Atlas.Core.Annotations.IsSpecialLocation(Game, Session.Locations.GetLocationNameFromId(id)));
        };

        BuildLogicTrackerView();
        BuildItemHistoryView();
        BuildTextClientTab();

        _hintTracker = new AP_Atlas.UI.HintTrackerControl();
        _hintTracker.Initialize(Session, _slotName, IsLocationInLogic,
            (slot, loc) => ResolveOtherSlotLogic?.Invoke(slot, loc),
            (msg, color) => ShowToast?.Invoke(msg, color));
        _hintTracker.LogicHidden = () => LogicHidden;
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
        AP_Atlas.Core.RaceRules.Changed += OnRaceRulesChanged;
        AP_Atlas.Core.PopTracker.PackFixes.Changed += OnPackFixesChanged;
        // Keep this slot's options for offline use (setting indicators, the Pack Doctor).
        DataManager.SaveSlotData(ProfileId, _slotName, Game, _slotData);
        AP_Atlas.UI.Ui.Defer(this, DetectRaceMode);
        AP_Atlas.UI.Ui.Defer(this, RequestGameNames);
        AP_Atlas.UI.Ui.Defer(this, OfferYamlExclusions);
        _specialSignature = string.Join("|", AP_Atlas.Core.Annotations.SpecialItemNames(Game).OrderBy(n => n));
        _exclusionSignature = ExclusionSignature();

        AppendSystemMessage($"[color=lime]Connected to {Session.ConnectionInfo.Game} as {_slotName}![/color]");

        // Session hooks. All of these fire on network threads, so every handler marshals to the main thread.
        Session.MessageLog.OnMessageReceived += OnAPMessageReceived;
        Session.Socket.SocketClosed += OnSocketClosed;
        Session.Items.ItemReceived += OnItemReceived;
        Session.Locations.CheckedLocationsUpdated += OnCheckedLocationsUpdated;
        AP_Atlas.Core.ThemeColors.AccentChanged += OnAccentChanged;
        // Streams this slot's hints (as finder or receiver) now and on every change.
        Session.Hints.TrackHints(OnHintsUpdated, true);

        AP_Atlas.UI.Ui.Defer(this, LoadMapPack);
        AP_Atlas.UI.Ui.Defer(this, InitializeLogicEngine);
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
        if (_engineStatusLabel != null) _engineStatusLabel.Text = "Engine: " + clean;
        _updateGlobalStatus?.Invoke($"[{_slotName}] {clean}");
    }

    // =====================================================================
    // Session event handlers (network thread -> main thread)
    // =====================================================================

    private void OnItemReceived(ReceivedItemsHelper helper)
    {
        AP_Atlas.UI.Ui.Defer(this, () =>
        {
            UpdateItemHistoryUI();
            FeedNewItemsToScripts();
            UpdateKeyItemsUI();
            QueueLogicRefresh();
            RaiseStateChanged();
        });
    }

    private void OnCheckedLocationsUpdated(System.Collections.ObjectModel.ReadOnlyCollection<long> newCheckedLocations)
    {
        AP_Atlas.UI.Ui.Defer(this, () =>
        {
            FeedNewChecksToScripts();
            QueueLogicRefresh();
            RaiseStateChanged();
        });
    }

    private void OnHintsUpdated(Archipelago.MultiClient.Net.Models.Hint[] hints)
    {
        AP_Atlas.UI.Ui.Defer(this, () =>
        {
            CurrentHints = hints ?? Array.Empty<Archipelago.MultiClient.Net.Models.Hint>();
            int me = Session.ConnectionInfo.Slot;
            foreach (var h in hints)
            {
                // Unfound hints for items in this world drive the "hinted" colors on the map.
                if (h.FindingPlayer == me && !h.Found) _knownHintedLocations.Add(h.LocationId);
            }
            _hintTracker?.SetHints(hints);
            RaiseStateChanged();
        });
    }

    /// <summary>Whether this slot's logic engine considers the location reachable; null while the engine isn't running or logic is hidden.</summary>
    public bool? IsLocationInLogic(long locationId)
    {
        if (!_engineRunning || LogicHidden) return null;
        return _knownReachableLocations.Contains(locationId);
    }

    // =====================================================================
    // Properties panel support
    // =====================================================================

    private Button _filterItemsMarked;

    public LogicEngineManager LogicEngine => _logicEngine;
    public bool EngineRunning => _engineRunning;
    public AP_Atlas.Core.PopTracker.LoadedPack Pack { get; private set; }
    public Archipelago.MultiClient.Net.Models.Hint[] CurrentHints { get; private set; } = Array.Empty<Archipelago.MultiClient.Net.Models.Hint>();
    public IReadOnlyList<ChatEntry> ChatHistory => _chatHistory;
    public string AnnotationKey => AP_Atlas.Core.Annotations.SlotKey(ProfileId, _slotName);
    public string Game => Session?.ConnectionInfo?.Game ?? "";
    public int PlayerSlot => Session?.ConnectionInfo?.Slot ?? -1;
    public int Team => Session?.ConnectionInfo?.Team ?? -1;
    public bool IsLocationReachable(long id) => !LogicHidden && _knownReachableLocations.Contains(id);
    public bool IsLocationHinted(long id) => _knownHintedLocations.Contains(id);
    public bool IsExcludedLocation(long id) => IsExcluded(id);
    public bool IsGlitchedLocation(long id) => !LogicHidden && _logicEngine?.LastGlitchedLocations?.Contains(id) == true;
    public int ReachableCount => _knownReachableLocations.Count;
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
        var pooled = _logicEngine?.LastItemPool?.FirstOrDefault(p => string.Equals(p.Name, itemName, StringComparison.OrdinalIgnoreCase));
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
    public (int Step, int Order, string ItemName)? UnlockStepOf(long locationId)
    {
        if (LogicHidden) return null;
        int step = 0, order = 0;
        foreach (var entry in _progressionLog)
        {
            var shown = ShownLocs(entry.UnlockedLocs);
            if (shown.Count == 0) continue;
            bool isBase = entry.ItemName == "Starting Logic";
            if (!isBase) step++;
            foreach (var loc in shown)
            {
                order++;
                if (loc == locationId) return (isBase ? 0 : step, order, entry.ItemName);
            }
        }
        return null;
    }

    /// <summary>Logic steps a received item opened, with how many checks each.</summary>
    public List<(int Step, int Count)> StepsUnlockedBy(string itemName)
    {
        var result = new List<(int, int)>();
        if (LogicHidden) return result;
        int step = 0;
        foreach (var entry in _progressionLog)
        {
            if (entry.ItemName == "Starting Logic") continue;
            int shown = ShownLocs(entry.UnlockedLocs).Count;
            if (shown == 0) continue;
            step++;
            if (string.Equals(entry.ItemName, itemName, StringComparison.OrdinalIgnoreCase)) result.Add((step, shown));
        }
        return result;
    }

    public int LogicStepCount => _progressionLog.Count(e => e.ItemName != "Starting Logic" && ShownLocs(e.UnlockedLocs).Count > 0);

    /// <summary>The checks of a logic step that aren't excluded (by the seed or by you).</summary>
    private List<long> ShownLocs(List<long> locs) =>
        locs == null ? new List<long>() : locs.Where(l => !IsExcluded(l)).ToList();

    // --- Cached lookups (one bridge or server request per question) ---

    private readonly Dictionary<long, System.Threading.Tasks.Task<LogicExplanation>> _explainCache = new();
    private int _explainCacheItemCount = -1;

    /// <summary>The engine's "why" for a location, cached until this slot's items change.</summary>
    public System.Threading.Tasks.Task<LogicExplanation> ExplainLocationAsync(long locationId)
    {
        // Race mode: never ask the engine why.
        if (!_engineRunning || _logicEngine == null || RaceRestricted) return System.Threading.Tasks.Task.FromResult<LogicExplanation>(null);
        if (_explainCacheItemCount != _lastEvaluatedItemCount || _logicBusy)
        {
            _explainCache.Clear();
            _explainCacheItemCount = _lastEvaluatedItemCount;
        }
        if (!_explainCache.TryGetValue(locationId, out var task))
        {
            task = _logicEngine.ExplainLocationAsync(locationId);
            _explainCache[locationId] = task;
        }
        return task;
    }

    private readonly Dictionary<long, System.Threading.Tasks.Task<ScoutedItemInfo>> _scoutCache = new();

    /// <summary>What a checked location held. Only for checked locations, so it never spoils anything.</summary>
    public System.Threading.Tasks.Task<ScoutedItemInfo> ScoutCheckedLocationAsync(long locationId)
    {
        if (Session == null || !Session.Socket.Connected || !Session.Locations.AllLocationsChecked.Contains(locationId))
            return System.Threading.Tasks.Task.FromResult<ScoutedItemInfo>(null);
        if (!_scoutCache.TryGetValue(locationId, out var task))
        {
            task = ScoutOneAsync(locationId);
            _scoutCache[locationId] = task;
        }
        return task;
    }

    private async System.Threading.Tasks.Task<ScoutedItemInfo> ScoutOneAsync(long locationId)
    {
        try
        {
            var result = await Session.Locations.ScoutLocationsAsync(HintCreationPolicy.None, locationId);
            return result != null && result.TryGetValue(locationId, out var info) ? info : null;
        }
        catch (Exception ex)
        {
            AppendDebugLog($"Scout of location {locationId} failed: {ex.Message}");
            _scoutCache.Remove(locationId);
            return null;
        }
    }

    private readonly Dictionary<string, System.Threading.Tasks.Task<Dictionary<string, string[]>>> _itemGroupCache = new();
    private readonly Dictionary<string, System.Threading.Tasks.Task<Dictionary<string, string[]>>> _locationGroupCache = new();

    /// <summary>The server's item name groups for a game (fetched once per game per connection).</summary>
    public System.Threading.Tasks.Task<Dictionary<string, string[]>> ItemGroupsAsync(string game) =>
        CachedGroupsAsync(_itemGroupCache, game, g => Session.DataStorage.GetItemNameGroupsAsync(g));

    public System.Threading.Tasks.Task<Dictionary<string, string[]>> LocationGroupsAsync(string game) =>
        CachedGroupsAsync(_locationGroupCache, game, g => Session.DataStorage.GetLocationNameGroupsAsync(g));

    private System.Threading.Tasks.Task<Dictionary<string, string[]>> CachedGroupsAsync(
        Dictionary<string, System.Threading.Tasks.Task<Dictionary<string, string[]>>> cache, string game,
        Func<string, System.Threading.Tasks.Task<Dictionary<string, string[]>>> fetch)
    {
        if (string.IsNullOrEmpty(game) || Session == null || !Session.Socket.Connected)
            return System.Threading.Tasks.Task.FromResult<Dictionary<string, string[]>>(null);
        if (!cache.TryGetValue(game, out var task))
        {
            task = SafeFetchAsync(() => fetch(game));
            cache[game] = task;
        }
        return task;
    }

    private static async System.Threading.Tasks.Task<T> SafeFetchAsync<T>(Func<System.Threading.Tasks.Task<T>> fetch) where T : class
    {
        try { return await fetch(); }
        catch { return null; }
    }

    private readonly Dictionary<int, (DateTime At, System.Threading.Tasks.Task<ArchipelagoClientState?> Task)> _statusCache = new();

    /// <summary>A player's client status (connected / playing / goal), refreshed at most every 30 seconds.</summary>
    public System.Threading.Tasks.Task<ArchipelagoClientState?> ClientStatusAsync(int player)
    {
        if (Session == null || !Session.Socket.Connected) return System.Threading.Tasks.Task.FromResult<ArchipelagoClientState?>(null);
        if (_statusCache.TryGetValue(player, out var cached) && (DateTime.Now - cached.At).TotalSeconds < 30) return cached.Task;
        var task = FetchStatusAsync(player);
        _statusCache[player] = (DateTime.Now, task);
        return task;
    }

    private async System.Threading.Tasks.Task<ArchipelagoClientState?> FetchStatusAsync(int player)
    {
        try { return await Session.DataStorage.GetClientStatusAsync(player, Team); }
        catch { return null; }
    }

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

        if (_filterItemsMarked?.ButtonPressed == true)
        {
            _historyConfigKey = null; // membership changed: rebuild
            UpdateItemHistoryUI();
        }
        else if (_itemHistoryTree?.GetRoot() != null)
        {
            var received = Session.Items.AllItemsReceived;
            for (var row = _itemHistoryTree.GetRoot().GetFirstChild(); row != null; row = row.GetNext())
            {
                var meta = row.GetMetadata(0);
                if (meta.VariantType != Variant.Type.Int) continue;
                int i = meta.AsInt32();
                if (i >= 0 && i < received.Count) ApplyItemMarker(row, 1, received[i].ItemId, received[i].ItemName);
            }
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
        if (_logicTree == null || !_logicRows.TryGetValue(locationId, out var row)) return;
        if (!row.Visible && _logicFlaggedOnly != null) _logicFlaggedOnly.ButtonPressed = false;
        row.Select(1);
        _logicTree.ScrollToItem(row, true);
    }

    public bool HasLogicRow(long locationId) => _logicRows.ContainsKey(locationId);

    public void RevealHistory(AP_Atlas.Core.InspectTarget target)
    {
        if (_itemHistoryTree == null) return;
        if (target.ReceiptIndex >= 0)
        {
            for (var row = _itemHistoryTree.GetRoot()?.GetFirstChild(); row != null; row = row.GetNext())
            {
                var meta = row.GetMetadata(0);
                if (meta.VariantType == Variant.Type.Int && meta.AsInt32() == target.ReceiptIndex)
                {
                    row.Select(1);
                    _itemHistoryTree.ScrollToItem(row, true);
                    return;
                }
            }
        }
        // Not a specific copy (or filtered out): search by name.
        _itemSearchBox.Text = target.ItemName ?? "";
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

    public void RevealKeyItem(string itemName) => _progressionTracker?.RevealItem(itemName);

    private void OnAccentChanged()
    {
        RenderLogicTree(forceFull: true);
        UpdateKeyItemsUI();
    }

    public override void _ExitTree()
    {
        AP_Atlas.Core.EngineSetup.AtlasEngine.Changed -= OnEngineChanged;
        AP_Atlas.Core.EngineSetup.AtlasEngine.PauseRequested -= OnEnginePauseRequested;
        AP_Atlas.Core.ThemeColors.AccentChanged -= OnAccentChanged;
        AP_Atlas.Core.Annotations.Changed -= OnAnnotationsChanged;
        AP_Atlas.Core.RaceRules.Changed -= OnRaceRulesChanged;
        AP_Atlas.Core.PopTracker.PackFixes.Changed -= OnPackFixesChanged;
        if (Session != null)
        {
            Session.Socket.PacketReceived -= OnDataPackagePacket;
            Session.MessageLog.OnMessageReceived -= OnAPMessageReceived;
            Session.Socket.SocketClosed -= OnSocketClosed;
            Session.Items.ItemReceived -= OnItemReceived;
            Session.Locations.CheckedLocationsUpdated -= OnCheckedLocationsUpdated;
        }
        _logicEngine?.StopEngine();
        _hintTracker?.Detach();

        // The per-slot views live in the shared content pane (or nowhere), not under this node,
        // so they must be freed explicitly to avoid orphaned nodes.
        foreach (Node view in new Node[] { _mapTracker?.SidebarContent, _mapTracker, _progressionTracker, _logicView, _historyView, _hintTracker })
        {
            if (view != null && GodotObject.IsInstanceValid(view)) view.QueueFree();
        }
    }
}
