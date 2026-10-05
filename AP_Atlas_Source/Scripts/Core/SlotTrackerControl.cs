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
        ClientStatusAsync(PlayerSlot).ContinueWith(t =>
        {
            if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result == ArchipelagoClientState.ClientGoal)
                Callable.From(() =>
                {
                    if (!GodotObject.IsInstanceValid(this)) return;
                    GoalCompleted = true;
                    RaiseStateChanged();
                }).CallDeferred();
        });
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
        _logicEngine.EngineExited += code => Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(this) && _engineRunning) HandleEngineFailure($"the engine process exited with code {code}");
        }).CallDeferred();
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
        Callable.From(DetectRaceModeAsync).CallDeferred();
        Callable.From(RequestGameNames).CallDeferred();
        Callable.From(OfferYamlExclusionsAsync).CallDeferred();
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
            FeedNewItemsToScripts();
            UpdateKeyItemsUI();
            QueueLogicRefresh();
            RaiseStateChanged();
        }).CallDeferred();
    }

    private void OnCheckedLocationsUpdated(System.Collections.ObjectModel.ReadOnlyCollection<long> newCheckedLocations)
    {
        Callable.From(() =>
        {
            FeedNewChecksToScripts();
            QueueLogicRefresh();
            RaiseStateChanged();
        }).CallDeferred();
    }

    private void OnHintsUpdated(Archipelago.MultiClient.Net.Models.Hint[] hints)
    {
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            CurrentHints = hints ?? Array.Empty<Archipelago.MultiClient.Net.Models.Hint>();
            int me = Session.ConnectionInfo.Slot;
            foreach (var h in hints)
            {
                // Unfound hints for items in this world drive the "hinted" colors on the map.
                if (h.FindingPlayer == me && !h.Found) _knownHintedLocations.Add(h.LocationId);
            }
            _hintTracker?.SetHints(hints);
            RaiseStateChanged();
        }).CallDeferred();
    }

    /// <summary>Whether this slot's logic engine considers the location reachable; null while the engine isn't running or logic is hidden.</summary>
    public bool? IsLocationInLogic(long locationId)
    {
        if (!_engineRunning || LogicHidden) return null;
        return _knownReachableLocations.Contains(locationId);
    }

    // =====================================================================
    // Race mode
    // =====================================================================

    /// <summary>The server reports this room as a race (null until asked).</summary>
    public bool IsRaceRoom { get; private set; }

    /// <summary>The server has answered whether this room is a race (IsRaceRoom is meaningful).</summary>
    public bool RaceStateKnown { get; private set; }

    /// <summary>Race restrictions apply: no "why" explanations from the logic engine.</summary>
    public bool RaceRestricted => AP_Atlas.Core.RaceRules.IsActive(IsRaceRoom);

    /// <summary>Race restrictions hide all in-logic information for this slot.</summary>
    public bool LogicHidden => AP_Atlas.Core.RaceRules.HidesLogic(IsRaceRoom);

    private bool _raceStateAnnounced;

    private async void DetectRaceModeAsync()
    {
        try
        {
            bool race = await Session.DataStorage.GetRaceModeAsync();
            if (!GodotObject.IsInstanceValid(this)) return;
            IsRaceRoom = race;
            RaceStateKnown = true;
            AppendDebugLog($"Race mode reported by the server: {race}");
        }
        catch (Exception ex)
        {
            AppendDebugLog("Could not read the room's race mode: " + ex.Message);
        }
        ApplyRaceRules();
    }

    /// <summary>Re-applies race restrictions to every view (after detection or a settings change).</summary>
    private void ApplyRaceRules()
    {
        if (!GodotObject.IsInstanceValid(this) || Session == null) return;
        bool restricted = RaceRestricted;
        if (restricted && !_raceStateAnnounced)
        {
            _raceStateAnnounced = true;
            string what = LogicHidden ? "all logic information is hidden" : "logic explanations are disabled";
            string why = IsRaceRoom ? "this room is in race mode" : "race mode is set to Always On";
            AP_Atlas.Core.Logger.LogInfo($"[color=orange][{_slotName}] Race mode: {what} ({why}).[/color]");
        }
        else if (!restricted) _raceStateAnnounced = false;

        _explainCache.Clear();
        SyncLogicViews();
        RaiseStateChanged();
    }

    /// <summary>
    /// Shows logic, or why there is none: race mode, or an engine problem with buttons to fix it. While logic is unknown
    /// the map uses neutral colors (open / hinted) instead of calling every check out of logic.
    /// </summary>
    private PanelContainer _accuracyBanner;
    private Label _accuracyText;
    private Button _accuracyLinkYaml;

    /// <summary>Why this slot's logic is only approximate (null when the rebuild matches the seed as far as Atlas can check).</summary>
    public string LogicAccuracyWarning
    {
        get
        {
            if (!_engineRunning) return null;
            if (ApworldMatchesSeed == false)
                return $"Logic may be off: this seed was made with a different version of the {Game} apworld than the one installed" +
                       (InstalledWorldVersion != null ? $" ({InstalledWorldVersion})" : "") + "." +
                       (_apworldFixStatus != null ? " " + _apworldFixStatus : "");
            var info = _logicEngine?.LastYamlInfo;
            if (info?["match"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && !(bool)info["match"])
                return $"Logic is approximate: your world was rebuilt, but it doesn't match the seed ({info["missing"]} locations missing, {info["extra"]} extra). " +
                       "Usually the game keeps some options out of the server's data. Link the YAML used to generate the seed for exact logic.";
            return null;
        }
    }

    private void SyncAccuracyBanner()
    {
        if (_accuracyBanner == null) return;
        string warning = LogicHidden ? null : LogicAccuracyWarning;
        _accuracyBanner.Visible = warning != null;
        _accuracyText.Text = "⚠ " + warning;
        if (warning != _lastAccuracyWarning)
        {
            _lastAccuracyWarning = warning;
            AccuracyChanged?.Invoke();
        }
        bool versionProblem = warning != null && ApworldMatchesSeed == false;
        _accuracyLinkYaml.Visible = warning != null && !versionProblem;
        _accuracyFix.Visible = versionProblem && !_apworldFixRunning;
        _accuracyAddSource.Visible = versionProblem && !_apworldFixRunning;
        _accuracyChooseApworld.Visible = versionProblem && !_apworldFixRunning;
    }

    // =====================================================================
    // Using the apworld version the seed was made with
    // =====================================================================

    private Button _accuracyFix, _accuracyChooseApworld, _accuracyAddSource;
    private string _lastAccuracyWarning;

    /// <summary>Raised when this slot's accuracy warning or fix status changes (Properties shows it too).</summary>
    public event Action AccuracyChanged;
    private string _apworldFixStatus;
    private bool _apworldFixRunning;
    private readonly HashSet<string> _apworldFixAttempted = new HashSet<string>();
    private readonly HashSet<string> _apworldOverrideFailed = new HashSet<string>();

    /// <summary>Atlas's cached copy of the seed's apworld version for this slot, if it has one (used in place of the installed copy).</summary>
    private string SeedApworldFile()
    {
        string checksum = ServerChecksumFor(Game);
        if (checksum == null || _apworldOverrideFailed.Contains(checksum)) return null;
        return AP_Atlas.Core.EngineSetup.ApworldSources.CachedFor(Game, checksum)?.File;
    }

    /// <summary>
    /// Gets the apworld version this seed was made with and restarts logic on it, for this slot only (the installed
    /// copy and other slots are untouched). Uses Atlas's cache when it has the version; otherwise looks where the
    /// game's versions are published: the repository the installed copy came from (found by its SHA-256), the one in
    /// the community index, and any the user added. Without interaction it only downloads from trusted repositories.
    /// </summary>
    public async void FixApworldVersion(bool interactive)
    {
        string game = Game, checksum = ServerChecksumFor(game);
        if (checksum == null || _apworldFixRunning || Session == null) return;
        _apworldFixAttempted.Add(checksum);
        if (AP_Atlas.Core.EngineSetup.ApworldSources.CachedFor(game, checksum) != null && !_apworldOverrideFailed.Contains(checksum))
        {
            AppendDebugLog($"Atlas already has the {game} apworld this seed was made with; restarting logic on it.");
            RetryLogicEngine();
            return;
        }
        // Looking on GitHub contacts a site: automatically only once the user allowed it; a button press asks.
        if (!AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups))
        {
            if (!interactive)
            {
                _apworldFixStatus = "Press \"Fix automatically\" to look up the seed's version on GitHub.";
                SyncAccuracyBanner();
                return;
            }
            AP_Atlas.UI.PermissionDialog.Ask(GetTree().Root, _appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null,
                $"For {_slotName}: Atlas looks for the {game} apworld this seed was made with.", allowed =>
                {
                    if (!GodotObject.IsInstanceValid(this)) return;
                    if (allowed) FixApworldVersion(interactive: true);
                    else
                    {
                        _apworldFixStatus = "Not looked up: Atlas didn't contact GitHub.";
                        SyncAccuracyBanner();
                    }
                });
            return;
        }

        // Where are this game's versions published? (Reads release lists only; nothing is downloaded.)
        _apworldFixRunning = true;
        _apworldFixStatus = $"Finding where {game} versions are published…";
        SyncAccuracyBanner();
        var install = _logicEngine.Install;
        List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo> repos;
        try
        {
            repos = await System.Threading.Tasks.Task.Run(() => AP_Atlas.Core.EngineSetup.ApworldSources.ReposForAsync(_appSettings, install, game, AppendDebugLog, System.Threading.CancellationToken.None));
        }
        catch (Exception ex)
        {
            AppendDebugLog("Couldn't look up where the apworld is published: " + ex.Message);
            repos = new List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo>();
        }
        if (!GodotObject.IsInstanceValid(this)) return;
        _apworldFixRunning = false;
        if (repos.Count == 0)
        {
            _apworldFixStatus = $"Atlas doesn't know where {game} versions are published. Add the project's GitHub link, or choose the apworld file the seed's host used.";
            SyncAccuracyBanner();
            return;
        }

        var trusted = repos.Where(r => r.Approved).ToList();
        var untrusted = repos.Where(r => !r.Approved).ToList();
        if (!interactive)
        {
            if (trusted.Count > 0) SearchSeedApworld(game, checksum, trusted, untrusted);
            else
            {
                _apworldFixStatus = "Fix automatically looks in " + string.Join(" and ", repos.Select(r => $"{r.Display} ({r.Reason})")) + ". You'll be asked to trust them once.";
                SyncAccuracyBanner();
            }
            return;
        }
        if (untrusted.Count == 0)
        {
            SearchSeedApworld(game, checksum, repos, new List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo>());
            return;
        }
        var dialog = new ConfirmationDialog
        {
            Title = "Use the seed's apworld version",
            DialogText = $"Look for the {game} apworld this seed was made with, and use it for {_slotName}?\n\nAtlas will check versions published in:\n" +
                         string.Join("\n", repos.Select(r => $"  •  {r.Display}: {r.Reason}{(r.Approved ? " (trusted)" : "")}")) +
                         "\n\nOnly this slot's logic uses it: your Archipelago install isn't changed. Each file is checked against its published SHA-256 when one exists.\n\n" +
                         "Apworlds are programs that run inside the logic engine. Only continue if you trust these sources.",
            DialogAutowrap = true,
            MinSize = new Vector2I(600, 0),
            OkButtonText = "Look and use it"
        };
        var trust = new CheckBox { Text = "Trust these from now on (fix future seeds automatically)", ButtonPressed = true };
        dialog.AddChild(trust);
        dialog.Confirmed += () =>
        {
            if (trust.ButtonPressed) foreach (var r in untrusted) AP_Atlas.Core.EngineSetup.ApworldSources.ApproveRepo(_appSettings, r.Repo);
            dialog.QueueFree();
            SearchSeedApworld(game, checksum, repos, new List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo>());
        };
        dialog.Canceled += () => dialog.QueueFree();
        GetTree().Root.AddChild(dialog);
        dialog.PopupCentered();
    }

    private async void SearchSeedApworld(string game, string checksum, List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo> repos, List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo> notSearched)
    {
        _apworldFixRunning = true;
        _apworldFixStatus = $"Looking for the {game} version this seed was made with…";
        SyncAccuracyBanner();
        var install = _logicEngine.Install;
        (AP_Atlas.Core.EngineSetup.ApworldVersion Version, string File) found = (null, null);
        string failure = null;
        try
        {
            found = await System.Threading.Tasks.Task.Run(() => AP_Atlas.Core.EngineSetup.ApworldSources.FindMatchingAsync(install, game, checksum, repos.Select(r => r.Repo), line =>
            {
                AppendDebugLog(line);
                string trimmed = line.Trim();
                Callable.From(() =>
                {
                    if (!GodotObject.IsInstanceValid(this) || !_apworldFixRunning) return;
                    _apworldFixStatus = $"Looking for the seed's version… {trimmed}";
                    SyncAccuracyBanner();
                }).CallDeferred();
            }, System.Threading.CancellationToken.None));
        }
        catch (Exception ex) { failure = ex.Message; }
        if (!GodotObject.IsInstanceValid(this)) return;
        _apworldFixRunning = false;
        if (found.File != null)
        {
            _apworldFixStatus = null;
            string from = found.Version?.Url != null ? " from " + AP_Atlas.Core.EngineSetup.ApworldSources.SourceKey(found.Version.Url) : "";
            AP_Atlas.Core.Logger.LogInfo($"[{_slotName}] Found the {game} apworld this seed was made with ({found.Version?.Version}{from}); restarting logic on it.");
            ShowToast?.Invoke($"{_slotName}: using {game} {found.Version?.Version}{from}, the version this seed was made with.", Colors.LimeGreen);
            RetryLogicEngine();
            return;
        }
        _apworldFixStatus = failure != null
            ? $"Couldn't look for the seed's version: {failure}"
            : notSearched.Count > 0
                ? $"Not in the trusted sources. Fix automatically also looks in {string.Join(" and ", notSearched.Select(r => r.Display))}."
                : $"None of the published {game} versions matches this seed. Add the project the seed's host used, or choose their apworld file.";
        if (notSearched.Count > 0) _apworldFixAttempted.Remove(checksum); // let the user widen the search
        SyncAccuracyBanner();
    }

    /// <summary>Adds a GitHub project (pasted link) as a source of this game's apworld, then looks there for the seed's version.</summary>
    public void AddApworldSource()
    {
        string game = Game;
        var dialog = new ConfirmationDialog
        {
            Title = $"Add a source for {game}",
            DialogText = $"Paste the GitHub link of the project that publishes the {game} apworld (its repository or releases page).\n" +
                         "Atlas will trust it and download versions from it to match your seeds.",
            DialogAutowrap = true,
            MinSize = new Vector2I(560, 0),
            OkButtonText = "Add and look"
        };
        var input = new LineEdit { PlaceholderText = "https://github.com/owner/project/releases" };
        dialog.AddChild(input);
        dialog.RegisterTextEnter(input);
        dialog.Confirmed += async () =>
        {
            string link = input.Text;
            dialog.QueueFree();
            _apworldFixStatus = "Checking that project's releases…";
            SyncAccuracyBanner();
            var (repo, problem) = await System.Threading.Tasks.Task.Run(() => AP_Atlas.Core.EngineSetup.ApworldSources.AddUserRepoAsync(_appSettings, game, link, System.Threading.CancellationToken.None));
            if (!GodotObject.IsInstanceValid(this)) return;
            if (repo == null)
            {
                _apworldFixStatus = "Couldn't add it: " + problem + ".";
                SyncAccuracyBanner();
                return;
            }
            AppendDebugLog($"Added github.com/{repo} as a source of {game} apworlds.");
            _apworldFixAttempted.Remove(ServerChecksumFor(game) ?? "");
            FixApworldVersion(interactive: true);
        };
        dialog.Canceled += () => dialog.QueueFree();
        GetTree().Root.AddChild(dialog);
        dialog.PopupCentered();
        input.GrabFocus();
    }

    /// <summary>Uses an apworld file the user picked (e.g. from the seed's host), if it's the seed's version.</summary>
    public void ChooseSeedApworld()
    {
        string game = Game, checksum = ServerChecksumFor(game);
        if (checksum == null) return;
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.apworld ; Archipelago world" },
            UseNativeDialog = true,
            Title = $"The {game} apworld this seed was made with"
        };
        dialog.FileSelected += async path =>
        {
            dialog.QueueFree();
            _apworldFixRunning = true;
            _apworldFixStatus = "Checking that file…";
            SyncAccuracyBanner();
            var install = _logicEngine.Install;
            var (entry, problem) = await System.Threading.Tasks.Task.Run(() => AP_Atlas.Core.EngineSetup.ApworldSources.AddUserFileAsync(install, path, game, checksum, System.Threading.CancellationToken.None));
            if (!GodotObject.IsInstanceValid(this)) return;
            _apworldFixRunning = false;
            if (entry != null)
            {
                _apworldFixStatus = null;
                _apworldOverrideFailed.Remove(checksum);
                ShowToast?.Invoke($"{_slotName}: using your {game} apworld, which matches this seed.", Colors.LimeGreen);
                RetryLogicEngine();
                return;
            }
            _apworldFixStatus = $"That file can't be used: {problem}.";
            SyncAccuracyBanner();
        };
        dialog.Canceled += () => dialog.QueueFree();
        GetTree().Root.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    private void SyncLogicViews()
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        SyncAccuracyBanner();
        bool problem = !LogicHidden && !_engineRunning && EngineProblem != null;
        if (_mapTracker != null) _mapTracker.LogicHidden = LogicHidden || !_engineRunning;
        if (_logicTree != null) _logicTree.Visible = !LogicHidden && !problem;
        if (_logicFlaggedOnly != null) _logicFlaggedOnly.Visible = !LogicHidden && !problem;
        if (_logicNotice == null) return;
        _logicNotice.Visible = LogicHidden || problem;
        foreach (Node n in _logicNoticeActions.GetChildren()) n.QueueFree();
        if (LogicHidden)
        {
            _logicNoticeText.Text = "Logic is hidden by race mode.\nChange this under Settings → Race Mode.";
            return;
        }
        if (!problem) return;
        _logicNoticeText.Text = EngineProblemText(EngineProblem);
        void AddButton(string text, string tip, Action action)
        {
            var b = new Button { Text = text, TooltipText = tip };
            b.Pressed += action;
            _logicNoticeActions.AddChild(b);
        }
        switch (EngineProblem.Code)
        {
            case "yaml_needed":
            case "generation_failed":
                AddButton("Link YAML…", "Choose this player's YAML; Atlas remembers it for this slot", PickYaml);
                AddButton("Atlas Engine…", "Open the engine setup", () => OpenEngineSetup?.Invoke());
                break;
            case "ut_disabled":
                break;
            case "restarting":
                AddButton("Restart now", "Start the logic engine again now", RetryLogicEngine);
                break;
            case "no_engine":
            case "world_missing":
                AddButton("Set up Atlas Engine…", "Download and check what logic needs", () => OpenEngineSetup?.Invoke());
                AddButton("Try again", "Start the logic engine again", RetryLogicEngine);
                break;
            default:
                AddButton("Try again", "Start the logic engine again", RetryLogicEngine);
                AddButton("Atlas Engine…", "Open the engine setup and run a health check", () => OpenEngineSetup?.Invoke());
                break;
        }
    }

    private string EngineProblemText(EngineStartError e)
    {
        switch (e.Code)
        {
            case "no_engine":
                return "Logic needs the Atlas Engine.\n" + e.Message + "\nAtlas can download everything it needs (about 50 MB, no installer).";
            case "world_missing":
                return $"{Game} isn't installed in the logic engine.\nAdd its apworld under Atlas Engine → Games.";
            case "yaml_needed":
                return $"{Game} can't rebuild your world from the server's data alone.\nLink this player's YAML (the file used to generate the seed).";
            case "generation_failed":
                return $"Your world couldn't be rebuilt.\n{e.Message}\nLink the YAML used to generate the seed, or check the game's apworld version.";
            case "ut_disabled":
                return $"The author of {Game}'s apworld asked trackers not to compute its logic.\nEverything else in Atlas still works.";
            case "restarting":
                return e.Message;
            default:
                return "The logic engine couldn't start.\n" + e.Message;
        }
    }

    private static string ProblemStatus(EngineStartError e) => e.Code switch
    {
        "no_engine" => "Not Set Up",
        "world_missing" => "Game Not Installed",
        "yaml_needed" => "YAML Needed",
        "generation_failed" => "World Rebuild Failed",
        "ut_disabled" => "Disabled By Game",
        "no_response" => "No Response",
        "crashed" => "Engine Crashed",
        _ => "Engine Error"
    };

    // --- Engine state for the setup window and Properties ---

    /// <summary>Why logic isn't running for this slot (null while it runs or starts).</summary>
    public EngineStartError EngineProblem { get; private set; }

    public bool EngineBooting => _engineBooting;

    /// <summary>How the engine rebuilt this slot's world: which YAML (or none) and whether its locations match the server's.</summary>
    public Newtonsoft.Json.Linq.JObject EngineYamlInfo => _engineRunning ? _logicEngine?.LastYamlInfo : null;

    public Newtonsoft.Json.Linq.JObject EngineVersions => _logicEngine?.LastVersions;

    /// <summary>
    /// Whether the installed apworld's data matches the seed's (checksums of names, ids and groups; null when either
    /// side didn't report one). It can't see rule-only changes, which the location check and seed tests cover.
    /// </summary>
    public bool? ApworldMatchesSeed { get; private set; }

    public string InstalledWorldVersion => _engineRunning ? _logicEngine?.LastWorldVersion : null;

    /// <summary>True when this slot runs on Atlas's cached copy of the seed's apworld version instead of the installed one.</summary>
    public bool UsingSeedApworld => _engineRunning && _logicEngine?.LastApworldOverride?["used"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && (bool)_logicEngine.LastApworldOverride["used"];

    /// <summary>Opens the Atlas Engine setup window (set by MainTrackerWindow).</summary>
    public Action OpenEngineSetup { get; set; }

    private bool _engineBooting;

    /// <summary>The player YAML linked to this slot, if the file still exists.</summary>
    public string LinkedYamlPath =>
        _appSettings.SlotYamlPaths != null && _appSettings.SlotYamlPaths.TryGetValue(AnnotationKey, out var p) && System.IO.File.Exists(p) ? p : null;

    /// <summary>The linked YAML as stored (even if the file has since moved).</summary>
    public string LinkedYamlSetting =>
        _appSettings.SlotYamlPaths != null && _appSettings.SlotYamlPaths.TryGetValue(AnnotationKey, out var p) ? p : null;

    /// <summary>Links (or with null, unlinks) a player YAML to this slot and restarts its logic with it.</summary>
    public void LinkYaml(string path)
    {
        _appSettings.SlotYamlPaths ??= new Dictionary<string, string>();
        if (string.IsNullOrEmpty(path)) _appSettings.SlotYamlPaths.Remove(AnnotationKey);
        else _appSettings.SlotYamlPaths[AnnotationKey] = path;
        DataManager.SaveSettings(_appSettings);
        RetryLogicEngine();
    }

    public void PickYaml()
    {
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.yaml, *.yml ; Archipelago player YAML" },
            UseNativeDialog = true,
            Title = $"YAML for {_slotName} ({Game})"
        };
        // Start where the user last picked a YAML (or their chosen install's Players folder). Atlas never searches for YAMLs.
        string linkedFolder = LinkedYamlSetting != null ? System.IO.Path.GetDirectoryName(LinkedYamlSetting) : null;
        string chosenPlayers = string.IsNullOrWhiteSpace(_appSettings.ArchipelagoInstallationPath) ? null : System.IO.Path.Combine(_appSettings.ArchipelagoInstallationPath, "Players");
        string start = new[] { _appSettings.LastYamlFolder, linkedFolder, chosenPlayers }.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && System.IO.Directory.Exists(d));
        if (start != null) dialog.CurrentDir = start;
        dialog.FileSelected += path =>
        {
            dialog.QueueFree();
            _appSettings.LastYamlFolder = System.IO.Path.GetDirectoryName(path) ?? "";
            var check = AP_Atlas.Core.YamlExclusions.Read(path, Game, _slotName);
            if (check.Error != null && !check.Error.StartsWith("Several"))
            {
                ShowToast?.Invoke($"That YAML can't be used for {_slotName}: {check.Error}", Colors.Salmon);
                return;
            }
            LinkYaml(path);
            ShowToast?.Invoke($"Linked {System.IO.Path.GetFileName(path)} to {_slotName}. Restarting logic…", Colors.Gray);
        };
        dialog.Canceled += () => dialog.QueueFree();
        GetTree().Root.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    /// <summary>Starts this slot's logic engine again (after setup, a new YAML, or a failure).</summary>
    public void RetryLogicEngine()
    {
        if (!GodotObject.IsInstanceValid(this) || Session == null || _engineBooting) return;
        if (_logicBusy)
        {
            // Let the running evaluation finish first, so it can't append steps from the old engine.
            GetTree().CreateTimer(0.5).Timeout += RetryLogicEngine;
            return;
        }
        if (_engineRunning)
        {
            _logicEngine.StopEngine();
            _engineRunning = false;
        }
        ResetLogicState();
        _recentEngineFailures.Clear(); // the user asked: give it a fresh set of automatic restarts
        InitializeLogicEngine();
    }

    /// <summary>The engine this slot uses is about to be updated: stop logic now; it resumes when the update is done.</summary>
    private void OnEnginePauseRequested(string root)
    {
        // Stop the process right away (any thread): setup waits for it to exit before replacing files.
        var install = _logicEngine?.Install;
        if (install == null || !string.Equals(System.IO.Path.GetFullPath(install.Root ?? "").TrimEnd('\\'), System.IO.Path.GetFullPath(root ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        _logicEngine.StopEngine();
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(this) || Session == null) return;
            bool wasActive = _engineRunning || _engineBooting;
            _engineRunning = false;
            ResetLogicState();
            if (wasActive || EngineProblem != null)
            {
                EngineProblem = new EngineStartError { Code = "paused", Message = "Logic is paused while the Atlas Engine is updated. It resumes by itself when the update finishes." };
                SetStatus("Paused (engine update)");
                SyncLogicViews();
                RaiseStateChanged();
            }
        }).CallDeferred();
    }

    /// <summary>Setup finished or changed: a slot that was waiting on the engine tries again.</summary>
    private void OnEngineChanged() => Callable.From(() =>
    {
        if (!GodotObject.IsInstanceValid(this) || Session == null || _engineRunning || _engineBooting || EngineProblem == null) return;
        if (EngineProblem.Code is "no_engine" or "world_missing" or "no_response" or "crashed" or "error" or "paused") InitializeLogicEngine();
    }).CallDeferred();

    private void OnRaceRulesChanged()
    {
        _raceStateAnnounced = false;
        ApplyRaceRules();
    }

    /// <summary>Re-evaluates the hint table (e.g. after another slot's logic changed).</summary>
    public void RefreshHints() => _hintTracker?.Refresh();

    private void RaiseStateChanged()
    {
        if (_mapTracker != null && Session != null)
        {
            _mapTracker.UpdateLogicColors(_knownReachableLocations, Session.Locations.AllLocationsChecked, _knownHintedLocations);
        }
        _hintTracker?.Refresh();
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
            Pack = pack;
            RebuildPackIndex();
            StartPackScripts();
            AppendDebugLog($"[MapTracker] Loaded pack '{pack.Manifest?.Name}' for {game}.");
            // First use of a pack (or a new version): let the Pack Doctor check it in the background.
            _ = AP_Atlas.Core.PopTracker.PackDoctorService.CheckAsync(pack);
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

    /// <summary>
    /// Asks the server for this game's data package (item and location name tables) once per connection.
    /// It's stored and saved, so the Pack Doctor and other slots can use it offline later.
    /// </summary>
    /// <summary>Each game's data checksum from the server's RoomInfo (set by MainTrackerWindow at connect).</summary>
    public IReadOnlyDictionary<string, string> ServerDataChecksums { get; set; }

    public string ServerChecksumFor(string game) =>
        game != null && ServerDataChecksums != null && ServerDataChecksums.TryGetValue(game, out var c) && !string.IsNullOrEmpty(c) ? c : null;

    private void RequestGameNames()
    {
        if (Session == null || string.IsNullOrEmpty(Game)) return;
        // Names are fixed by the checksum: when the stored copy has the same one, there's nothing to download.
        string checksum = ServerChecksumFor(Game);
        var stored = AP_Atlas.Core.PopTracker.GameNames.Server(Game);
        if (checksum != null && stored != null && stored.Version == checksum && stored.Locations.Count > 0)
        {
            AppendDebugLog($"Names for {Game} are already stored for checksum {checksum[..Math.Min(8, checksum.Length)]}; not downloading them again.");
            return;
        }
        Session.Socket.PacketReceived += OnDataPackagePacket;
        _ = Session.Socket.SendPacketAsync(new GetDataPackagePacket { Games = new[] { Game } });
    }

    private void OnDataPackagePacket(ArchipelagoPacketBase packet)
    {
        if (packet is not DataPackagePacket dp || dp.DataPackage?.Games == null) return;
        string game = Game;
        if (!dp.DataPackage.Games.TryGetValue(game, out var data) || data.ItemLookup == null) return;
        Session.Socket.PacketReceived -= OnDataPackagePacket;
        var table = new AP_Atlas.Core.PopTracker.GameNameTable
        {
            Game = game,
            Source = AP_Atlas.Core.PopTracker.GameNames.ServerSource,
            Fetched = DateTime.Now,
            Version = data.Checksum ?? "",
            Items = new Dictionary<string, long>(data.ItemLookup),
            Locations = new Dictionary<string, long>(data.LocationLookup ?? new Dictionary<string, long>())
        };
        Callable.From(() =>
        {
            AP_Atlas.Core.PopTracker.GameNames.Store(table);
            if (!GodotObject.IsInstanceValid(this)) return;
            RebuildPackIndex();
            // The server's names are the most accurate; re-check the pack against them.
            if (Pack != null) _ = AP_Atlas.Core.PopTracker.PackDoctorService.CheckAsync(Pack);
        }).CallDeferred();
    }

    /// <summary>The pairing of the loaded pack with this slot's locations and items.</summary>
    public AP_Atlas.Core.PopTracker.PackIndex PackIndex { get; private set; }

    /// <summary>
    /// (Re)pairs the pack with this slot: location names from the session, item names from the engine's pool,
    /// received items and the pack's mapped ids. Rebuilt when the pool arrives or the user fixes the pack.
    /// </summary>
    public void RebuildPackIndex()
    {
        if (Pack == null || Session == null) return;
        using var __perf = AP_Atlas.Core.PerfMonitor.Measure($"[{_slotName}] Pair map pack with slot");
        var locationNames = new Dictionary<long, string>();
        foreach (var id in Session.Locations.AllLocations) locationNames[id] = Session.Locations.GetLocationNameFromId(id) ?? "";
        var itemNames = new Dictionary<long, string>();
        // The game's full item table (server data package, else the local install), then what this slot has seen.
        var table = AP_Atlas.Core.PopTracker.GameNames.Best(Game);
        if (table != null) foreach (var kv in table.ItemsById()) itemNames[kv.Key] = kv.Value;
        foreach (var p in _logicEngine?.LastItemPool ?? new List<WorldItemInfo>()) if (p.Id != 0 && p.Name != null) itemNames[p.Id] = p.Name;
        foreach (var i in Session.Items.AllItemsReceived) if (i.ItemName != null) itemNames[i.ItemId] = i.ItemName;
        foreach (var id in Pack.ItemMapping.Keys)
        {
            if (itemNames.ContainsKey(id)) continue;
            string n = Session.Items.GetItemName(id, Game);
            if (!string.IsNullOrEmpty(n)) itemNames[id] = n;
        }
        // The user's Pack Doctor fixes apply to a light copy; the author's pack stays as loaded.
        EffectivePack = AP_Atlas.Core.PopTracker.PackFixes.Effective(Pack);
        PackIndex = new AP_Atlas.Core.PopTracker.PackIndex(EffectivePack, locationNames, itemNames);
        AP_Atlas.Core.PopTracker.PackFixes.ApplyLinks(PackIndex);
        _mapTracker?.LoadPack(EffectivePack, PackIndex);
        _progressionTracker?.SetPack(EffectivePack, PackIndex);
        StateChanged?.Invoke();
    }

    // =====================================================================
    // The pack's own scripts (item states and seed-setting indicators, exactly as the pack defines them)
    // =====================================================================

    /// <summary>The options and data the server sent this slot at login.</summary>
    public Dictionary<string, object> SlotDataSnapshot => _slotData;

    /// <summary>The pack's scripts running for this slot (null until ready, or if the pack has none).</summary>
    public AP_Atlas.Core.PopTracker.PackScriptHost PackScripts { get; private set; }

    private System.Threading.Tasks.Task _scriptQueue = System.Threading.Tasks.Task.CompletedTask;
    private int _scriptItemsQueued;
    private readonly HashSet<long> _scriptLocationsQueued = new HashSet<long>();

    /// <summary>Runs work on the scripts' background queue, in order (scripts aren't thread-safe).</summary>
    private void QueueScriptWork(Action work, Action onMainThread = null)
    {
        _scriptQueue = _scriptQueue.ContinueWith(_ =>
        {
            try { work(); }
            catch (Exception ex) { AP_Atlas.Core.Logger.LogWarning($"[{_slotName}] Pack script: {ex.Message}"); }
            if (onMainThread != null) Callable.From(() => { if (GodotObject.IsInstanceValid(this)) onMainThread(); }).CallDeferred();
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>Starts the pack's scripts: init, the clear handler with this slot's options, then every item and check so far.</summary>
    private void StartPackScripts()
    {
        if (Pack == null || Session == null) return;
        var pack = Pack;
        int me = PlayerSlot, team = Team;
        var slotData = Newtonsoft.Json.Linq.JToken.FromObject(_slotData ?? new Dictionary<string, object>());
        var items = Session.Items.AllItemsReceived.Select(i => (i.ItemId, i.ItemName, Player: i.Player?.Slot ?? 0)).ToList();
        var locations = Session.Locations.AllLocationsChecked.Select(id => (Id: id, Name: Session.Locations.GetLocationNameFromId(id) ?? "")).ToList();
        _scriptItemsQueued = items.Count;
        _scriptLocationsQueued.Clear();
        foreach (var l in locations) _scriptLocationsQueued.Add(l.Id);
        AP_Atlas.Core.PopTracker.PackScriptHost host = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        QueueScriptWork(() =>
        {
            host = AP_Atlas.Core.PopTracker.PackScriptHost.Load(pack);
            if (host == null) return;
            host.Initialize();
            host.Clear(me, team, slotData);
            for (int i = 0; i < items.Count; i++) host.ApplyItem(i, items[i].ItemId, items[i].ItemName, items[i].Player);
            foreach (var (id, name) in locations) host.ApplyLocation(id, name);
        }, () =>
        {
            PackScripts = host;
            if (host == null) { AppendDebugLog($"[MapTracker] The pack has no scripts/init.lua; Key Items use the pack's item mappings only."); return; }
            AppendDebugLog($"[MapTracker] Ran the pack's scripts in {sw.ElapsedMilliseconds} ms: {host.Errors.Count} error(s)" +
                           (host.UnsupportedApis.Count > 0 ? $", unsupported APIs: {string.Join(", ", host.UnsupportedApis)}" : ""));
            foreach (var e in host.Errors) AppendDebugLog("[MapTracker] Pack script error: " + e);
            UpdateKeyItemsUI();
            StateChanged?.Invoke();
        });
    }

    /// <summary>Feeds items received since the scripts last saw the list.</summary>
    private void FeedNewItemsToScripts()
    {
        if (Pack == null || Session == null) return;
        var all = Session.Items.AllItemsReceived;
        if (all.Count <= _scriptItemsQueued) return;
        var fresh = all.Skip(_scriptItemsQueued).Select((i, n) => (Index: _scriptItemsQueued + n, i.ItemId, i.ItemName, Player: i.Player?.Slot ?? 0)).ToList();
        _scriptItemsQueued = all.Count;
        QueueScriptWork(() => { foreach (var f in fresh) PackScripts?.ApplyItem(f.Index, f.ItemId, f.ItemName, f.Player); }, UpdateKeyItemsUI);
    }

    private void FeedNewChecksToScripts()
    {
        if (Pack == null || Session == null) return;
        var fresh = Session.Locations.AllLocationsChecked.Where(id => _scriptLocationsQueued.Add(id))
            .Select(id => (Id: id, Name: Session.Locations.GetLocationNameFromId(id) ?? "")).ToList();
        if (fresh.Count == 0) return;
        QueueScriptWork(() => { foreach (var (id, name) in fresh) PackScripts?.ApplyLocation(id, name); });
    }

    /// <summary>A tile's state from the pack's scripts, or null when they aren't running.</summary>
    public AP_Atlas.Core.PopTracker.PackScriptHost.TileState ScriptStateOf(string code)
    {
        var host = PackScripts;
        if (host == null || !host.Cleared) return null;
        // StateOf reads the scripts' objects; serialize with the queue by only reading when it's idle.
        if (!_scriptQueue.IsCompleted) return null;
        return host.StateOf(code);
    }

    /// <summary>The seed's settings as the pack shows them (its settings grid plus anything set from slot data).</summary>
    public List<AP_Atlas.Core.PopTracker.PackScriptHost.SettingInfo> SeedSettings()
    {
        var host = PackScripts;
        if (host == null || !host.Cleared || !_scriptQueue.IsCompleted) return new List<AP_Atlas.Core.PopTracker.PackScriptHost.SettingInfo>();
        var settingCodes = (EffectivePack ?? Pack).ItemGridGroups.Where(g => g.LooksLikeSettings).SelectMany(g => g.Rows.SelectMany(r => r));
        return host.Settings(settingCodes);
    }

    /// <summary>The loaded pack with the user's fixes applied (what the views show).</summary>
    public AP_Atlas.Core.PopTracker.LoadedPack EffectivePack { get; private set; }

    private void OnPackFixesChanged(string packKey)
    {
        if (Pack != null && AP_Atlas.Core.PopTracker.PackFixes.KeyFor(Pack) == packKey) RebuildPackIndex();
    }

    private async void InitializeLogicEngine()
    {
        if (_engineBooting || _engineRunning) return;
        if (AP_Atlas.Core.EngineSetup.AtlasEngine.SetupRunning)
        {
            // Never start an engine whose files may be changing; setup's Changed event brings us back.
            EngineProblem = new EngineStartError { Code = "paused", Message = "Logic is paused while the Atlas Engine is updated. It resumes by itself when the update finishes." };
            SetStatus("Paused (engine update)");
            SyncLogicViews();
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

        _engineBooting = true;
        EngineProblem = null;
        _logicEngine.SetInstall(AP_Atlas.Core.EngineSetup.AtlasEngine.Resolve(_appSettings));
        SyncLogicViews();
        SetStatus("Booting Engine...");
        try
        {
            AppendDebugLog($"InitializeLogicEngine: StartEngineAsync Game='{Session.ConnectionInfo.Game}', Slot='{_slotName}', engine={_logicEngine.Install.Describe()}");
            // No ConfigureAwait(false): the continuation must resume on Godot's main thread.
            bool started = await _logicEngine.StartEngineAsync(
                Session.ConnectionInfo.Game, _slotName, Session.ConnectionInfo.Slot, _slotData, Session.Locations.AllLocations, LinkedYamlPath,
                SeedApworldFile(), ServerChecksumFor(Game));

            if (!GodotObject.IsInstanceValid(this)) return;
            AppendDebugLog($"InitializeLogicEngine: StartEngineAsync returned {started}");

            if (started)
            {
                _engineRunning = true;
                ReportEngineStart();
                QueueLogicRefresh();
                UpdateItemHistoryUI(); // item pool is now available for "Not Yet Collected"
                RebuildPackIndex();    // ...and item names for pairing pack items that have no mapping
                UpdateKeyItemsUI();
            }
            else
            {
                EngineProblem = _logicEngine.LastStartError ?? new EngineStartError { Code = "error", Message = "The logic engine couldn't start." };
                SetStatus(ProblemStatus(EngineProblem));
                AppendDebugLog($"Logic engine not started ({EngineProblem.Code}): {EngineProblem.Message}" +
                               (EngineProblem.Details != null ? "\n" + EngineProblem.Details.ToString(Newtonsoft.Json.Formatting.None) : ""));
                IsFullyLoaded = true;
            }
        }
        catch (Exception ex)
        {
            AppendDebugLog("Logic Engine Init Error: " + ex.Message);
            EngineProblem = new EngineStartError { Code = "error", Message = ex.Message };
            SetStatus("Engine Error");
            IsFullyLoaded = true;
        }
        finally
        {
            _engineBooting = false;
            if (GodotObject.IsInstanceValid(this))
            {
                SyncLogicViews();
                RaiseStateChanged();
            }
        }
    }

    /// <summary>Says how the world was rebuilt, and warns when its locations don't match the server's.</summary>
    private void ReportEngineStart()
    {
        var info = _logicEngine.LastYamlInfo;
        string source = info?["source"]?.ToString();
        string file = info?["file"]?.ToString();
        bool? match = info?["match"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean ? (bool?)info["match"] : null;
        string how = source switch
        {
            "not_needed" => "from the server's data (no YAML needed)",
            "slot_data" => "from the options in the server's slot data",
            "linked" => $"from your linked YAML {file}",
            "players" => $"from {file} in the Players folder",
            _ => "by the engine"
        };
        AppendDebugLog($"Logic engine running: world rebuilt {how}; locations {info?["got"]} of {info?["expected"]} expected, {info?["missing"]} missing, {info?["extra"]} extra.");
        string serverChecksum = ServerChecksumFor(Game), localChecksum = _logicEngine.LastDataChecksum;
        ApworldMatchesSeed = serverChecksum == null || localChecksum == null ? null : serverChecksum == localChecksum;
        var applied = _logicEngine.LastApworldOverride;
        if (applied?["used"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && (bool)applied["used"])
            AppendDebugLog($"Using {Game} {applied["world_version"]} ({applied["file"]}) from Atlas's cache for this seed, in place of the installed copy (which isn't changed).");
        else if (applied?["error"] != null)
        {
            // The cached version couldn't load in this engine: don't offer it again this session.
            _apworldOverrideFailed.Add(serverChecksum ?? "");
            AP_Atlas.Core.Logger.LogWarning($"[{_slotName}] The seed's {Game} apworld couldn't be loaded in this engine ({applied["error"]}); using the installed copy.");
        }
        if (ApworldMatchesSeed == false)
        {
            string version = _logicEngine.LastWorldVersion != null ? $" (version {_logicEngine.LastWorldVersion})" : "";
            SetStatus("Running (apworld differs from the seed's)");
            AP_Atlas.Core.Logger.LogWarning($"[{_slotName}] The installed {Game} apworld{version} isn't the one this seed was generated with: its data " +
                $"(checksum {localChecksum[..Math.Min(8, localChecksum.Length)]}) differs from the server's ({serverChecksum[..Math.Min(8, serverChecksum.Length)]}). " +
                "Atlas will look for the seed's version.");
            SyncAccuracyBanner();
            // Fix it without asking when the download source is already trusted (or the version is cached).
            if (_appSettings.AutoFixApworldVersions && !_apworldFixAttempted.Contains(serverChecksum))
                FixApworldVersion(interactive: false);
            return;
        }
        if (ApworldMatchesSeed == true) AppendDebugLog($"The {Game} apworld in use matches the seed's data (checksum {localChecksum[..Math.Min(8, localChecksum.Length)]}).");
        if (match == false)
        {
            int missing = info["missing"]?.ToObject<int>() ?? 0, extra = info["extra"]?.ToObject<int>() ?? 0;
            SetStatus($"Running (world differs: {missing} missing, {extra} extra)");
            AP_Atlas.Core.Logger.LogWarning($"[{_slotName}] The rebuilt world doesn't match the server ({missing} locations missing, {extra} extra). Logic may be off. " +
                "Linking the YAML used to generate the seed, or matching the game's apworld version, usually fixes this.");
        }
        else SetStatus("Engine Running");
        SyncAccuracyBanner();
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
            // The last answer was for everything received so far, so its goal flag describes the slot now.
            GoalInLogic = _logicEngine.LastGoalReachable;

            RenderLogicTree();
            IsFullyLoaded = true;
            RaiseStateChanged();
        }
        catch (LogicEngineFailure failure)
        {
            HandleEngineFailure(failure.Message);
        }
        catch (Exception ex)
        {
            AppendDebugLog($"UpdateLogic FATAL EXCEPTION: {ex}");
            AP_Atlas.Core.CrashGuard.Record($"[{_slotName}] Logic evaluation failed", ex);
            HandleEngineFailure("an internal error while reading logic (" + ex.Message + ")");
        }
        finally
        {
            _logicBusy = false;
        }
    }

    private sealed class LogicEngineFailure : Exception
    {
        public LogicEngineFailure(string reason) : base(string.IsNullOrEmpty(reason) ? "the logic engine stopped answering" : reason) { }
    }

    private bool _startingLogicDone;
    private readonly List<DateTime> _recentEngineFailures = new List<DateTime>();
    private static readonly int[] RestartDelaysSeconds = { 2, 10, 30 };

    /// <summary>
    /// The engine failed mid-session. Nothing half-done is kept: logic is reset and rebuilt from scratch on a fresh
    /// engine, after 2 s, 10 s, then 30 s. A fourth failure within ten minutes pauses logic with an explanation
    /// rather than restarting forever.
    /// </summary>
    private void HandleEngineFailure(string reason)
    {
        if (!GodotObject.IsInstanceValid(this) || Session == null) return;
        AppendDebugLog("Logic engine failure: " + reason);
        _engineRunning = false;
        _logicEngine.StopEngine();
        ResetLogicState();
        if (AP_Atlas.Core.EngineSetup.AtlasEngine.SetupRunning)
        {
            // Stopped for an engine update, not a crash: wait for the update instead of counting a failure.
            EngineProblem = new EngineStartError { Code = "paused", Message = "Logic is paused while the Atlas Engine is updated. It resumes by itself when the update finishes." };
            SetStatus("Paused (engine update)");
            SyncLogicViews();
            RaiseStateChanged();
            return;
        }

        var now = DateTime.Now;
        _recentEngineFailures.RemoveAll(t => (now - t).TotalMinutes > 10);
        _recentEngineFailures.Add(now);
        int failures = _recentEngineFailures.Count;
        if (failures <= RestartDelaysSeconds.Length)
        {
            int delay = RestartDelaysSeconds[failures - 1];
            EngineProblem = new EngineStartError { Code = "restarting", Message = $"The logic engine stopped ({reason}). Restarting it in {delay} s; logic will be rebuilt from scratch." };
            SetStatus("Restarting…");
            AP_Atlas.Core.Logger.LogWarning($"[{_slotName}] The logic engine stopped ({reason}); restarting in {delay} s.");
            GetTree().CreateTimer(delay).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(this) && !_engineRunning && !_engineBooting && EngineProblem?.Code == "restarting") InitializeLogicEngine();
            };
        }
        else
        {
            EngineProblem = new EngineStartError { Code = "crashed", Message = $"The logic engine stopped {failures} times in 10 minutes ({reason}). Logic is paused so it can't show anything wrong. The slot's Debug Log has details." };
            SetStatus("Engine Paused (repeated failures)");
            AP_Atlas.Core.Logger.LogError($"[{_slotName}] The logic engine failed {failures} times in 10 minutes ({reason}); logic is paused.");
        }
        IsFullyLoaded = true;
        SyncLogicViews();
        RaiseStateChanged();
    }

    /// <summary>Forgets every logic result, so the next evaluation rebuilds the Logic Tracker from scratch.</summary>
    private void ResetLogicState()
    {
        _lastEvaluatedItemCount = 0;
        _startingLogicDone = false;
        _chronologicalInventory.Clear();
        _progressionLog.Clear();
        _knownReachableLocations.Clear();
        _explainCache.Clear();
        GoalInLogic = null;
        RenderLogicTree(forceFull: true);
    }

    private async System.Threading.Tasks.Task EvaluateLogicStepAsync()
    {
        var missingLocs = Session.Locations.AllLocations.Except(Session.Locations.AllLocationsChecked).ToList();
        if (missingLocs.Count == 0) return;

        var poolProgression = PoolProgressionIds();
        var currentProgression = Session.Items.AllItemsReceived
            .Where(i => i.Flags.HasFlag(ItemFlags.Advancement) || i.Flags.HasFlag(ItemFlags.NeverExclude) || poolProgression.Contains(i.ItemId))
            .ToList();

        if (currentProgression.Count == _lastEvaluatedItemCount && _startingLogicDone) return;

        if (_lastEvaluatedItemCount == 0 && _progressionLog.Count == 0 && !_startingLogicDone)
        {
            var initialReachable = await _logicEngine.GetReachableLocationsAsync(new List<long>(), missingLocs)
                ?? throw new LogicEngineFailure(_logicEngine.LastQueryFailure);
            _startingLogicDone = true;
            if (initialReachable.Count > 0)
            {
                _knownReachableLocations.UnionWith(initialReachable);
                // Excluded checks stay in the log (hidden when shown) so changing an exclusion needs no re-run.
                _progressionLog.Add(("Starting Logic", initialReachable.ToList()));
            }
        }

        for (int i = _lastEvaluatedItemCount; i < currentProgression.Count; i++)
        {
            var netItem = currentProgression[i];
            var inventory = new List<long>(_chronologicalInventory) { netItem.ItemId };

            // A failed answer stops here, before anything is recorded: the step is evaluated again after recovery.
            var stepReachable = await _logicEngine.GetReachableLocationsAsync(inventory, missingLocs)
                ?? throw new LogicEngineFailure(_logicEngine.LastQueryFailure);
            _chronologicalInventory.Add(netItem.ItemId);
            _lastEvaluatedItemCount = i + 1;

            var newlyUnlocked = new List<long>();
            foreach (var loc in stepReachable)
            {
                if (_knownReachableLocations.Add(loc)) newlyUnlocked.Add(loc);
            }
            if (newlyUnlocked.Count > 0)
            {
                string itemName = Session.Items.GetItemName(netItem.ItemId) ?? "Unknown Item";
                _progressionLog.Add((itemName, newlyUnlocked));
            }
        }

        _lastEvaluatedItemCount = currentProgression.Count;
    }

    private List<WorldItemInfo> _poolProgressionSource;
    private HashSet<long> _poolProgression = new HashSet<long>();

    /// <summary>
    /// Items the world itself classes as progression. Items an admin sends with a server command (/send) arrive with no
    /// flags, so the world's own classification counts as well as the server's flags. The pool only changes when the
    /// engine starts, and every engine start begins with a reset, so the evaluated list stays in step.
    /// </summary>
    private HashSet<long> PoolProgressionIds()
    {
        var pool = _logicEngine?.LastItemPool;
        if (!ReferenceEquals(pool, _poolProgressionSource))
        {
            _poolProgressionSource = pool;
            _poolProgression = pool == null ? new HashSet<long>() : new HashSet<long>(pool.Where(p => (p.Flags & 1) != 0).Select(p => p.Id));
        }
        return _poolProgression;
    }

    /// <summary>Excluded by your choice if you made one, else by the seed (as the logic engine reads its options).</summary>
    private bool IsExcluded(long loc) =>
        AP_Atlas.Core.Annotations.GetExclusionOverride(AnnotationKey, loc) ?? IsExcludedBySeed(loc);

    public bool IsExcludedBySeed(long loc) => _logicEngine?.LastExcludedLocations?.Contains(loc) == true;

    /// <summary>Why a location is excluded or included: "seed", "you", or null when it's a normal check.</summary>
    public string ExclusionSource(long loc)
    {
        var mine = AP_Atlas.Core.Annotations.GetExclusionOverride(AnnotationKey, loc);
        if (mine == true) return "you";
        if (mine == false) return IsExcludedBySeed(loc) ? "included by you" : null;
        return IsExcludedBySeed(loc) ? "seed" : null;
    }

    /// <summary>
    /// Excludes the locations a player YAML lists (location names or location group names).
    /// Returns how many were newly excluded and the names that matched nothing in this seed.
    /// </summary>
    public async System.Threading.Tasks.Task<(int Applied, int Listed, List<string> Unknown)> ApplyYamlExclusionsAsync(List<string> names)
    {
        var ids = new HashSet<long>();
        var unknown = new List<string>();
        if (Session == null) return (0, 0, names);
        var inSeed = new HashSet<long>(Session.Locations.AllLocations);
        Dictionary<string, string[]> groups = null;
        bool groupsFetched = false;
        foreach (var name in names)
        {
            long id = Session.Locations.GetLocationIdFromName(Game, name);
            if (id >= 0 && inSeed.Contains(id)) { ids.Add(id); continue; }
            if (id >= 0) continue; // a real location that this seed doesn't have (turned off by its options)
            if (!groupsFetched)
            {
                groups = await LocationGroupsAsync(Game);
                groupsFetched = true;
            }
            var group = groups?.FirstOrDefault(g => string.Equals(g.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (group == null) { unknown.Add(name); continue; }
            foreach (var member in group)
            {
                long memberId = Session.Locations.GetLocationIdFromName(Game, member);
                if (memberId >= 0 && inSeed.Contains(memberId)) ids.Add(memberId);
            }
        }
        if (!GodotObject.IsInstanceValid(this)) return (0, ids.Count, unknown);
        var toExclude = ids.Where(id => !IsExcluded(id)).ToList();
        if (toExclude.Count > 0)
        {
            // Seed-excluded ones just drop a manual "include"; the rest become your exclusions.
            var bySeed = toExclude.Where(IsExcludedBySeed).ToList();
            if (bySeed.Count > 0) AP_Atlas.Core.Annotations.SetExclusionOverrides(AnnotationKey, bySeed, null);
            var mine = toExclude.Where(id => !IsExcludedBySeed(id)).ToList();
            if (mine.Count > 0) AP_Atlas.Core.Annotations.SetExclusionOverrides(AnnotationKey, mine, true);
        }
        return (toExclude.Count, ids.Count, unknown);
    }

    /// <summary>Shown when the YAML linked to this slot lists excluded locations (once per distinct list).</summary>
    public Action<string, Color, string, Action> ShowActionToast { get; set; }

    /// <summary>
    /// Offers the excluded locations of the YAML the user linked to this slot. Atlas reads only that file: it never looks
    /// through folders for YAMLs.
    /// </summary>
    private async void OfferYamlExclusionsAsync()
    {
        string path = LinkedYamlPath, game = Game, slot = _slotName;
        if (path == null) return;
        var yaml = await System.Threading.Tasks.Task.Run(() => AP_Atlas.Core.YamlExclusions.Read(path, game, slot));
        if (!GodotObject.IsInstanceValid(this) || Session == null || yaml.Error != null || yaml.Names.Count == 0) return;
        string signature = System.IO.Path.GetFileName(yaml.File) + "|" + string.Join("|", yaml.Names.OrderBy(n => n));
        if (!AP_Atlas.Core.Annotations.FirstOfferOfYamlExclusions(AnnotationKey, signature)) return;
        string file = System.IO.Path.GetFileName(yaml.File);
        AppendDebugLog($"[Exclusions] {file} lists {yaml.Names.Count} excluded location(s) for {slot}.");
        ShowActionToast?.Invoke($"{file} excludes {yaml.Names.Count} location(s) for {slot}. Apply them to the tracker?", Colors.Gray, "Apply", async () =>
        {
            var (applied, listed, unknown) = await ApplyYamlExclusionsAsync(yaml.Names);
            ShowToast?.Invoke($"Excluded {applied} location(s) from {file}" + (unknown.Count > 0 ? $" ({unknown.Count} name(s) not found)" : ""), Colors.Gray);
        });
    }

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
        _logicFlaggedOnly = new Button { ToggleMode = true, Text = "Flagged / special only", TooltipText = "Show only checks you flagged, noted or marked special." };
        _logicFlaggedOnly.Toggled += _ => ApplyAllLogicMarkers();
        toolbar.AddChild(_logicFlaggedOnly);
        _engineStatusLabel = new Label { Text = "Engine: Offline", SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right };
        toolbar.AddChild(_engineStatusLabel);

        // Shown when the logic can't be trusted as exact: the rebuilt world or the apworld doesn't match the seed.
        _accuracyBanner = new PanelContainer { Visible = false };
        _accuracyBanner.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Godot.Color("#3a2a10"),
            BorderColor = Colors.Orange,
            BorderWidthLeft = 3,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        });
        var bannerRow = new HBoxContainer();
        bannerRow.AddThemeConstantOverride("separation", 10);
        _accuracyText = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _accuracyText.AddThemeColorOverride("font_color", Colors.Orange);
        bannerRow.AddChild(_accuracyText);
        _accuracyLinkYaml = new Button { Text = "Link YAML…", TooltipText = "Choose the YAML used to generate this seed; Atlas remembers it for this slot" };
        _accuracyLinkYaml.Pressed += PickYaml;
        bannerRow.AddChild(_accuracyLinkYaml);
        _accuracyFix = new Button { Text = "Fix automatically", TooltipText = "Find the apworld version this seed was made with and use it for this slot (your install isn't changed)" };
        _accuracyFix.Pressed += () => FixApworldVersion(interactive: true);
        bannerRow.AddChild(_accuracyFix);
        _accuracyChooseApworld = new Button { Text = "Choose apworld file…", TooltipText = "Use the apworld file the seed's host gave you, if it's this seed's version" };
        _accuracyChooseApworld.Pressed += ChooseSeedApworld;
        bannerRow.AddChild(_accuracyChooseApworld);
        _accuracyAddSource = new Button { Text = "Add a source…", TooltipText = "Paste the GitHub link of the project the seed's apworld comes from" };
        _accuracyAddSource.Pressed += AddApworldSource;
        bannerRow.AddChild(_accuracyAddSource);
        _accuracyBanner.AddChild(bannerRow);
        vbox.AddChild(_accuracyBanner);

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
        AP_Atlas.Core.TreePicks.Hook(_logicTree, (selected, column) =>
        {
            var meta = selected.GetMetadata(0);
            if (meta.VariantType == Variant.Type.Int)
            {
                long locId = meta.AsInt64();
                // "Unlocked By" column inspects the unlocking item; anything else the location.
                if (column == 2 && selected.GetText(2) != "Starting Logic") Inspect(ItemTargetByName(selected.GetText(2)));
                else Inspect(LocationTarget(locId));
            }
            else if (meta.VariantType == Variant.Type.String) Inspect(ItemTargetByName(meta.AsString()));
        });
        vbox.AddChild(_logicTree);
        _logicTree.CreateItem();

        // Shown instead of the list when there's no logic to show: race mode, or an engine problem with its fixes.
        _logicNotice = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center, Visible = false };
        _logicNotice.AddThemeConstantOverride("separation", 14);
        _logicNoticeText = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _logicNoticeText.AddThemeColorOverride("font_color", Colors.Gray);
        _logicNotice.AddChild(_logicNoticeText);
        _logicNoticeActions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _logicNoticeActions.AddThemeConstantOverride("separation", 10);
        _logicNotice.AddChild(_logicNoticeActions);
        vbox.AddChild(_logicNotice);
    }

    private VBoxContainer _logicNotice;
    private Label _logicNoticeText;
    private HBoxContainer _logicNoticeActions;
    private Button _logicFlaggedOnly;

    /// <summary>Flag/special/note marker on a Logic Tracker row, and its visibility under the "flagged only" filter.</summary>
    private void ApplyLogicRowMarker(TreeItem row, long locId)
    {
        var a = AP_Atlas.Core.Annotations.Get(AnnotationKey, AP_Atlas.Core.Annotations.LocationKey(locId));
        bool special = AP_Atlas.Core.Annotations.IsSpecialLocation(Game, Session.Locations.GetLocationNameFromId(locId));
        row.SetIcon(1, AP_Atlas.Core.Annotations.MarkerIcon(a?.Flag ?? 0, special, !string.IsNullOrWhiteSpace(a?.Note)));
        row.SetIconMaxWidth(1, Math.Max(16, _appSettings.ContentFontSize * 2));
        bool marked = a != null || special;
        row.Visible = _logicFlaggedOnly == null || !_logicFlaggedOnly.ButtonPressed || marked;
    }

    private void ApplyAllLogicMarkers()
    {
        if (_logicTree == null || Session == null) return;
        foreach (var kv in _logicRows) ApplyLogicRowMarker(kv.Value, kv.Key);
        // Hide step dividers with no visible rows under the filter.
        bool filtering = _logicFlaggedOnly?.ButtonPressed == true;
        TreeItem divider = null;
        bool anyVisible = false;
        for (var child = _logicTree.GetRoot()?.GetFirstChild(); child != null; child = child.GetNext())
        {
            bool isDivider = child.GetText(0).StartsWith("●");
            if (isDivider)
            {
                if (divider != null) divider.Visible = !filtering || anyVisible;
                divider = child;
                anyVisible = false;
            }
            else if (child.Visible) anyVisible = true;
        }
        if (divider != null) divider.Visible = !filtering || anyVisible;
    }

    // Incremental render state. Godot's Tree re-shapes the text of every new cell (~0.05 ms each), so a full
    // Clear()+rebuild of a few thousand cells costs hundreds of milliseconds. Steps are only ever appended and
    // rows only change color, so after the first build we append and recolor in place.
    private int _logicRenderedSteps = 0;
    private int _logicOrderCount = 1;
    private int _logicSectionIndex = 1;
    private TreeItem _logicPlaceholder;
    private readonly Dictionary<long, TreeItem> _logicRows = new Dictionary<long, TreeItem>();
    private readonly HashSet<long> _logicRowsShownChecked = new HashSet<long>();

    private void RenderLogicTree(bool forceFull = false)
    {
        using var __perf = AP_Atlas.Core.PerfMonitor.Measure($"[{_slotName}] Logic Tracker refresh");
        if (_logicTree == null || Session == null) return;

        bool full = forceFull || _logicTree.GetRoot() == null || _logicRenderedSteps > _progressionLog.Count ||
                    (_logicPlaceholder != null && _progressionLog.Count > 0);
        if (full)
        {
            _logicTree.Clear();
            _logicTree.CreateItem();
            _logicRows.Clear();
            _logicRowsShownChecked.Clear();
            _logicPlaceholder = null;
            _logicRenderedSteps = 0;
            _logicOrderCount = 1;
            _logicSectionIndex = 1;
        }
        var root = _logicTree.GetRoot();

        if (_progressionLog.Count == 0)
        {
            _logicPlaceholder ??= _logicTree.CreateItem(root);
            _logicPlaceholder.SetText(1, _engineRunning ? "No reachable checks found yet." : "Waiting for Logic Engine...");
            _logicPlaceholder.SetCustomColor(1, Colors.Gray);
            return;
        }

        var checkedLocs = new HashSet<long>(Session.Locations.AllLocationsChecked);
        var dividerBg = new Godot.Color("#252836");
        var dividerFg = AP_Atlas.Core.ThemeColors.Accent;

        for (; _logicRenderedSteps < _progressionLog.Count; _logicRenderedSteps++)
        {
            var step = _progressionLog[_logicRenderedSteps];
            var shown = ShownLocs(step.UnlockedLocs);
            if (shown.Count == 0) continue;
            bool isBase = step.ItemName == "Starting Logic";

            var divider = _logicTree.CreateItem(root);
            divider.SetText(0, isBase ? "● Base" : $"● Step {_logicSectionIndex}");
            divider.SetText(1, isBase ? "── Base Logic (Starting Reachable) ──" : $"── Unlocked by: {step.ItemName} ({shown.Count} checks) ──");
            divider.SetText(2, isBase ? "Starting Checks" : step.ItemName);
            for (int c = 0; c < 3; c++)
            {
                divider.SetCustomBgColor(c, dividerBg);
                divider.SetCustomColor(c, dividerFg);
            }
            // Selecting a step divider inspects the item that unlocked it.
            if (isBase) { for (int c = 0; c < 3; c++) divider.SetSelectable(c, false); }
            else divider.SetMetadata(0, step.ItemName);
            if (!isBase) _logicSectionIndex++;

            foreach (var locId in shown)
            {
                var row = _logicTree.CreateItem(root);
                row.SetText(0, _logicOrderCount.ToString());
                row.SetText(1, Session.Locations.GetLocationNameFromId(locId) ?? "Unknown Check");
                row.SetText(2, step.ItemName);
                row.SetMetadata(0, locId);
                var rowBg = (_logicOrderCount % 2 == 0) ? new Godot.Color("#16161C") : new Godot.Color("#1F1F27");
                for (int c = 0; c < 3; c++) row.SetCustomBgColor(c, rowBg);
                bool isChecked = checkedLocs.Contains(locId);
                ColorLogicRow(row, isChecked);
                if (isChecked) _logicRowsShownChecked.Add(locId);
                _logicRows[locId] = row;
                ApplyLogicRowMarker(row, locId);
                _logicOrderCount++;
            }
        }

        // Grey out rows whose check was completed since they were drawn (only those cells change).
        foreach (var kv in _logicRows)
        {
            bool isChecked = checkedLocs.Contains(kv.Key);
            if (isChecked == _logicRowsShownChecked.Contains(kv.Key)) continue;
            ColorLogicRow(kv.Value, isChecked);
            if (isChecked) _logicRowsShownChecked.Add(kv.Key);
            else _logicRowsShownChecked.Remove(kv.Key);
        }
        if (_logicFlaggedOnly?.ButtonPressed == true) ApplyAllLogicMarkers();
    }

    private static void ColorLogicRow(TreeItem row, bool isChecked)
    {
        row.SetCustomColor(0, isChecked ? Colors.DimGray : Colors.LightGray);
        row.SetCustomColor(1, isChecked ? Colors.DimGray : Colors.White);
        row.SetCustomColor(2, isChecked ? Colors.DimGray : Colors.Plum);
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

    private void UpdateItemHistoryUI()
    {
        using var __perf = AP_Atlas.Core.PerfMonitor.Measure($"[{_slotName}] Item History refresh");
        if (_itemHistoryTree == null || _uncollectedTree == null || Session == null) return;

        string search = _itemSearchBox?.Text?.Trim() ?? "";
        int sortMode = _optHistorySort?.Selected ?? 0;
        var fullPool = _logicEngine?.LastItemPool;
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
            _uncollectedHeaderLabel.Text = _engineRunning ? "Not Yet Collected (0 items)" : "Not Yet Collected (Waiting for Logic Engine...)";
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
        // Only a real reconnect has server messages from before; Atlas's own "Connected to…" line doesn't count.
        if (_chatHistory.Any(e => !e.IsSystemMessage)) { AppendSystemMessage("[color=gray]--- Reconnected ---[/color]"); }

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
            if (msg is GoalLogMessage goal && goal.IsActivePlayer && !GoalCompleted)
            {
                GoalCompleted = true;
                RaiseStateChanged();
            }
            ProcessSingleMessage(msg);
            AnnounceSpecialItem(msg);
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
            string link = null;
            string prefix = "";
            string partText = (part.Text ?? "").Replace("[", "[lb]");

            if (part is Archipelago.MultiClient.Net.MessageLog.Parts.ItemMessagePart itemPart)
            {
                if (itemPart.Flags.HasFlag(ItemFlags.Advancement)) color = "plum";
                else if (itemPart.Flags.HasFlag(ItemFlags.NeverExclude)) color = "slateblue";
                else if (itemPart.Flags.HasFlag(ItemFlags.Trap)) color = "salmon";
                else color = "cyan";
                link = $"I|{itemPart.Player}|{itemPart.ItemId}";
                // Special items (per game) stand out wherever they're mentioned.
                string itemGame = Session.Players.GetPlayerInfo(itemPart.Player)?.Game;
                if (AP_Atlas.Core.Annotations.IsSpecialItem(itemGame, part.Text))
                {
                    prefix = $"[color=#{AP_Atlas.Core.Annotations.SpecialColor.ToHtml(false)}]◆[/color]";
                    partText = $"[bgcolor=#{AP_Atlas.Core.Annotations.SpecialBg.ToHtml(false)}]{partText}[/bgcolor]";
                }
                if (itemPart.Player == PlayerSlot)
                {
                    int flag = AP_Atlas.Core.Annotations.GetFlag(AnnotationKey, AP_Atlas.Core.Annotations.ItemKey(itemPart.ItemId));
                    if (flag > 0) prefix = $"[color=#{AP_Atlas.Core.Annotations.FlagColor(flag).ToHtml(false)}]●[/color]" + prefix;
                }
            }
            else if (part is Archipelago.MultiClient.Net.MessageLog.Parts.LocationMessagePart locPart)
            {
                color = "green";
                link = $"L|{locPart.Player}|{locPart.LocationId}";
                if (locPart.Player == PlayerSlot)
                {
                    int flag = AP_Atlas.Core.Annotations.GetFlag(AnnotationKey, AP_Atlas.Core.Annotations.LocationKey(locPart.LocationId));
                    if (flag > 0) prefix = $"[color=#{AP_Atlas.Core.Annotations.FlagColor(flag).ToHtml(false)}]●[/color]";
                }
            }
            else if (part is Archipelago.MultiClient.Net.MessageLog.Parts.PlayerMessagePart playerPart)
            {
                color = playerPart.IsActivePlayer ? "magenta" : "yellow";
                link = $"P|{playerPart.SlotId}";
            }
            else
            {
                string colorName = part.Color.ToString().ToLower();
                if (colorName != "none" && colorName != "") { color = colorName; }
            }

            string colored = $"{prefix}[color={color}]{partText}[/color]";
            text += link == null ? colored : $"[url={link}]{colored}[/url]";
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
            MetaUnderlined = false,
            Text = text
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

        ScrollChatToBottom();
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
            : send.IsReceiverTheActivePlayer
                ? $"◆ You received {itemName} from {sender}"
                : $"◆ {sender} found {receiver}'s {itemName}";

        var now = DateTime.Now;
        foreach (var stale in _recentSpecialToasts.Where(kv => (now - kv.Value).TotalSeconds > 10).Select(kv => kv.Key).ToList())
            _recentSpecialToasts.Remove(stale);
        string key = $"{send.Item.LocationId}|{send.Sender?.Slot}|{msg.GetType().Name}";
        if (_recentSpecialToasts.ContainsKey(key)) return;
        _recentSpecialToasts[key] = now;
        ShowToast?.Invoke(text, AP_Atlas.Core.Annotations.SpecialColor);
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
            task = ScoutOne(locationId);
            _scoutCache[locationId] = task;
        }
        return task;
    }

    private async System.Threading.Tasks.Task<ScoutedItemInfo> ScoutOne(long locationId)
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
        CachedGroups(_itemGroupCache, game, g => Session.DataStorage.GetItemNameGroupsAsync(g));

    public System.Threading.Tasks.Task<Dictionary<string, string[]>> LocationGroupsAsync(string game) =>
        CachedGroups(_locationGroupCache, game, g => Session.DataStorage.GetLocationNameGroupsAsync(g));

    private System.Threading.Tasks.Task<Dictionary<string, string[]>> CachedGroups(
        Dictionary<string, System.Threading.Tasks.Task<Dictionary<string, string[]>>> cache, string game,
        Func<string, System.Threading.Tasks.Task<Dictionary<string, string[]>>> fetch)
    {
        if (string.IsNullOrEmpty(game) || Session == null || !Session.Socket.Connected)
            return System.Threading.Tasks.Task.FromResult<Dictionary<string, string[]>>(null);
        if (!cache.TryGetValue(game, out var task))
        {
            task = SafeFetch(() => fetch(game));
            cache[game] = task;
        }
        return task;
    }

    private static async System.Threading.Tasks.Task<T> SafeFetch<T>(Func<System.Threading.Tasks.Task<T>> fetch) where T : class
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
        var task = FetchStatus(player);
        _statusCache[player] = (DateTime.Now, task);
        return task;
    }

    private async System.Threading.Tasks.Task<ArchipelagoClientState?> FetchStatus(int player)
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
