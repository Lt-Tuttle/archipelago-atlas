using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AP_Atlas.Core.Connections;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.MessageLog.Parts;
using Archipelago.MultiClient.Net.Models;

namespace AP_Atlas.Core
{
    /// <summary>What changed in a slot since its views last heard; several at once when they arrive together.</summary>
    [Flags]
    public enum SlotChange
    {
        None = 0,
        /// <summary>Items were received.</summary>
        Items = 1,
        /// <summary>Locations were checked.</summary>
        Checks = 2,
        /// <summary>The slot's hints changed, or a hint message named a location.</summary>
        Hints = 4,
        /// <summary>New lines in the text client (server messages, or Atlas's own).</summary>
        Messages = 8,
        /// <summary>The slot reached its goal.</summary>
        Goal = 16,
        /// <summary>The connection dropped.</summary>
        Connection = 32,
        /// <summary>Logic changed: its results, its engine's state, or why it isn't running.</summary>
        Logic = 64,
        /// <summary>A logic engine finished starting: its item pool and world are new.</summary>
        EngineStarted = 128,
        /// <summary>Race mode changed for this slot: the server said whether the room is a race, or its settings changed.</summary>
        Race = 256,
    }

    /// <summary>One line of a slot's text client: a server message, or one of Atlas's own (BBCode).</summary>
    public sealed class ChatEntry
    {
        public LogMessage? APMessage { get; init; }
        public string? SystemMessage { get; init; }
        public bool IsSystemMessage => SystemMessage != null;

        /// <summary>Counts up from 1 for each slot, so a view knows which lines it has shown.</summary>
        public long Sequence { get; init; }

        /// <summary>A server message from before the slot's views existed (shown, but not announced again).</summary>
        public bool Early { get; init; }
    }

    /// <summary>
    /// One connected slot, apart from its views: the session and who the slot is, the session's events, and what follows
    /// from them (the text client's lines, hints, the goal, and its logic, <see cref="Logic"/>), plus the questions Atlas
    /// asks the server about it. It hears every event even while no view of it is in the window, and tells its views at
    /// most once per frame what changed: a burst of 300 items is one refresh, not 300.
    /// </summary>
    /// <remarks>
    /// Session events arrive on network threads; they're queued and applied on the main thread when the change is raised.
    /// Everything else here is read and changed on the main thread only.
    /// </remarks>
    public sealed class SlotModel : IDisposable
    {
        /// <summary>How many text client lines are kept.</summary>
        public const int ChatLimit = 1000;

        private readonly object _queueLock = new();
        private readonly Action<string> _log;
        private readonly List<LogMessage> _incoming = new();
        private Hint[]? _incomingHints;
        private string? _closedReason;
        private SlotChange _pending;
        private bool _flushScheduled, _disposed;

        private readonly List<ChatEntry> _chat = new();
        private long _nextSequence = 1;
        private readonly HashSet<long> _hintedLocations = new();
        private bool _goalStatusAsked;

        /// <param name="settings">Atlas's settings (which engine logic runs on, the slot's linked YAML).</param>
        /// <param name="log">The slot's debug log.</param>
        public SlotModel(ConnectedSlot connected, AppSettings settings, Action<string> log)
        {
            _log = log;
            Slot = connected.Slot;
            Session = connected.Session;
            SlotData = connected.Login.SlotData ?? new Dictionary<string, object>();
            DataChecksums = connected.DataChecksums;
            AddSystemMessage($"[color=lime]Connected to {Game} as {SlotName}![/color]");

            // Session hooks (network threads). From here on the model hears every message itself.
            Session.MessageLog.OnMessageReceived += OnMessage;
            Session.Socket.SocketClosed += OnSocketClosed;
            Session.Items.ItemReceived += OnItemReceived;
            Session.Locations.CheckedLocationsUpdated += OnChecked;
            // What the server sent before this model existed, then what it heard itself (the same message can be in both).
            var early = connected.TakeEarlyMessages();
            lock (_queueLock)
            {
                var heard = _incoming.ToList();
                _incoming.Clear();
                foreach (var message in early) AddEntry(new ChatEntry { APMessage = message, Sequence = _nextSequence++, Early = true });
                _incoming.AddRange(heard.Where(message => !early.Contains(message)));
            }
            Schedule(SlotChange.Messages);
            // The slot's hints (as finder or receiver), now and on every change.
            Session.Hints.TrackHints(OnHints, true);
            // Race mode's settings, and logic. Both start once the slot is set up (its views, if any, hear how it goes).
            RaceRules.Changed += OnRaceRulesChanged;
            Logic = new SlotLogic(this, settings, log);
            AP_Atlas.UI.Ui.Defer(null, () =>
            {
                if (_disposed) return;
                Async.Fire(DetectRaceModeAsync(), $"checking whether {SlotName}'s room is a race");
                Logic.Start();
            }, $"starting {SlotName}");
        }

        public SlotId Slot { get; }
        public ArchipelagoSession Session { get; }
        public string ProfileId => Slot.ProfileId;
        public string SlotName => Slot.SlotName;

        /// <summary>The options the server sent with the login.</summary>
        public Dictionary<string, object> SlotData { get; }

        /// <summary>Each game's data checksum from the server's room info.</summary>
        public IReadOnlyDictionary<string, string> DataChecksums { get; }

        /// <summary>The slot's logic: its engine and what's in logic, step by step.</summary>
        public SlotLogic Logic { get; }

        public string Game => Session.ConnectionInfo?.Game ?? "";
        public int PlayerSlot => Session.ConnectionInfo?.Slot ?? -1;
        public int Team => Session.ConnectionInfo?.Team ?? -1;

        /// <summary>The text client's lines, oldest first (the last <see cref="ChatLimit"/>).</summary>
        public IReadOnlyList<ChatEntry> Chat => _chat;

        /// <summary>The hints this slot is in (as finder or receiver), as the server last sent them.</summary>
        public Hint[] CurrentHints { get; private set; } = Array.Empty<Hint>();

        /// <summary>Unfound hinted locations in this slot's world (they color the map).</summary>
        public IReadOnlySet<long> HintedLocations => _hintedLocations;

        /// <summary>The server says this slot reached its goal (its goal message, or a status check).</summary>
        public bool GoalCompleted { get; private set; }

        /// <summary>The slot has ended (replaced, closed or deleted): it hears nothing more, and its engine is stopped.</summary>
        public bool Ended => _disposed;

        /// <summary>
        /// What changed, raised on the main thread at most once per frame (more often only if events keep arriving while
        /// it's raised). Never raised after <see cref="Dispose"/>.
        /// </summary>
        public event Action<SlotChange>? Changed;

        /// <summary>Adds one of Atlas's own lines (BBCode) to the text client.</summary>
        public void AddSystemMessage(string bbcode)
        {
            if (_disposed) return;
            AddEntry(new ChatEntry { SystemMessage = bbcode, Sequence = _nextSequence++ });
            Schedule(SlotChange.Messages);
        }

        /// <summary>Asks the server, once per connection, whether this slot already reached its goal.</summary>
        public void EnsureGoalStatus()
        {
            if (_goalStatusAsked || _disposed) return;
            _goalStatusAsked = true;
            Async.Then(ClientStatusAsync(PlayerSlot), status =>
            {
                if (status == ArchipelagoClientState.ClientGoal) Schedule(SlotChange.Goal, goalReached: true);
            }, "asking the server whether a slot reached its goal");
        }

        // =====================================================================
        // Race mode
        // =====================================================================

        /// <summary>The server reports this room as a race (false until it answers: <see cref="RaceStateKnown"/>).</summary>
        public bool IsRaceRoom { get; private set; }

        /// <summary>The server has answered whether this room is a race (<see cref="IsRaceRoom"/> is meaningful).</summary>
        public bool RaceStateKnown { get; private set; }

        /// <summary>Race restrictions apply: no "why" explanations from the logic engine.</summary>
        public bool RaceRestricted => RaceRules.IsActive(IsRaceRoom);

        /// <summary>Race restrictions hide all in-logic information for this slot.</summary>
        public bool LogicHidden => RaceRules.HidesLogic(IsRaceRoom);

        private bool _raceAnnounced;

        private async Task DetectRaceModeAsync()
        {
            try
            {
                bool race = await Session.DataStorage.GetRaceModeAsync();
                if (_disposed) return;
                IsRaceRoom = race;
                RaceStateKnown = true;
                _log($"Race mode reported by the server: {race}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log("Could not read the room's race mode: " + ex.Message);
            }
            ApplyRaceRules();
        }

        /// <summary>Race mode's settings changed: it's announced again if it applies.</summary>
        private void OnRaceRulesChanged()
        {
            _raceAnnounced = false;
            ApplyRaceRules();
        }

        /// <summary>Applies race restrictions (after detection or a settings change), and says once that they apply.</summary>
        private void ApplyRaceRules()
        {
            if (_disposed) return;
            bool restricted = RaceRestricted;
            if (restricted && !_raceAnnounced)
            {
                _raceAnnounced = true;
                string what = LogicHidden ? "all logic information is hidden" : "logic explanations are disabled";
                string why = IsRaceRoom ? "this room is in race mode" : "race mode is set to Always On";
                Logger.LogInfo($"[color=orange][{SlotName}] Race mode: {what} ({why}).[/color]");
            }
            else if (!restricted) _raceAnnounced = false;
            Logic.ForgetExplanations();
            Schedule(SlotChange.Race);
        }

        // =====================================================================
        // Exclusions, and logic as the slot shows it (race mode and exclusions applied)
        // =====================================================================

        /// <summary>The key this slot's notes, flags and exclusions are kept under.</summary>
        public string AnnotationKey => Annotations.SlotKey(ProfileId, SlotName);

        /// <summary>Excluded by your choice if you made one, else by the seed (as the logic engine reads its options).</summary>
        public bool IsExcluded(long location) => Annotations.GetExclusionOverride(AnnotationKey, location) ?? Logic.ExcludedBySeed(location);

        /// <summary>Why a location is excluded or included: "seed", "you", "included by you", or null when it's a normal check.</summary>
        public string? ExclusionSource(long location)
        {
            var mine = Annotations.GetExclusionOverride(AnnotationKey, location);
            if (mine == true) return "you";
            if (mine == false) return Logic.ExcludedBySeed(location) ? "included by you" : null;
            return Logic.ExcludedBySeed(location) ? "seed" : null;
        }

        public int TotalLocationsCount => Session.Locations.AllLocations.Count;
        public int CheckedLocationsCount => Session.Locations.AllLocationsChecked.Count;

        /// <summary>Whether logic reaches the location; null while logic isn't running or race mode hides it.</summary>
        public bool? IsLocationInLogic(long location) => !Logic.Running || LogicHidden ? null : Logic.Reachable.Contains(location);

        /// <summary>Whether logic has reached the location (false while race mode hides logic).</summary>
        public bool IsLocationReachable(long location) => !LogicHidden && Logic.Reachable.Contains(location);

        /// <summary>Whether the location is reachable only with glitches (false while race mode hides logic).</summary>
        public bool IsGlitchedLocation(long location) => !LogicHidden && Logic.IsGlitched(location);

        /// <summary>Checks in logic that aren't done or excluded (0 while race mode hides logic).</summary>
        public int ActiveLogicCount
        {
            get
            {
                if (LogicHidden) return 0;
                // Polled every 0.5 s by the sidebar; AllLocationsChecked is a list, so hash it once per call.
                var checkedLocations = new HashSet<long>(Session.Locations.AllLocationsChecked);
                int count = 0;
                foreach (long location in Logic.Reachable)
                    if (!checkedLocations.Contains(location) && !IsExcluded(location)) count++;
                return count;
            }
        }

        /// <summary>The checks of a logic step that aren't excluded (by the seed or by you).</summary>
        public List<long> ShownLocations(IReadOnlyList<long>? locations) =>
            locations == null ? new List<long>() : locations.Where(location => !IsExcluded(location)).ToList();

        /// <summary>When logic first reached a location: step number (0 = open from the start), overall order and the item that opened it.</summary>
        public (int Step, int Order, string ItemName)? UnlockStepOf(long locationId)
        {
            if (LogicHidden) return null;
            int step = 0, order = 0;
            foreach (var entry in Logic.Steps)
            {
                var shown = ShownLocations(entry.Locations);
                if (shown.Count == 0) continue;
                if (!entry.IsStart) step++;
                foreach (long location in shown)
                {
                    order++;
                    if (location == locationId) return (entry.IsStart ? 0 : step, order, entry.ItemName);
                }
            }
            return null;
        }

        /// <summary>The logic steps a received item opened, with how many checks each.</summary>
        public List<(int Step, int Count)> StepsUnlockedBy(string itemName)
        {
            var result = new List<(int, int)>();
            if (LogicHidden) return result;
            int step = 0;
            foreach (var entry in Logic.Steps)
            {
                if (entry.IsStart) continue;
                int shown = ShownLocations(entry.Locations).Count;
                if (shown == 0) continue;
                step++;
                if (string.Equals(entry.ItemName, itemName, StringComparison.OrdinalIgnoreCase)) result.Add((step, shown));
            }
            return result;
        }

        /// <summary>How many logic steps opened at least one check that isn't excluded (0 while race mode hides logic).</summary>
        public int LogicStepCount => LogicHidden ? 0 : Logic.Steps.Count(entry => !entry.IsStart && ShownLocations(entry.Locations).Count > 0);

        /// <summary>Why this slot's logic is only approximate (null when it matches the seed as far as Atlas can check).</summary>
        public string? LogicAccuracyWarning
        {
            get
            {
                if (!Logic.Running) return null;
                if (Logic.ApworldMatchesSeed == false)
                    return $"Logic may be off: this seed was made with a different version of the {Game} apworld than the one installed" +
                           (Logic.Engine.LastWorldVersion != null ? $" ({Logic.Engine.LastWorldVersion})" : "") + ".";
                var info = Logic.Engine.LastYamlInfo;
                if (info?["match"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && !(bool)info["match"]!)
                    return $"Logic is approximate: your world was rebuilt, but it doesn't match the seed ({info["missing"]} locations missing, {info["extra"]} extra). " +
                           "Usually the game keeps some options out of the server's data. Link the YAML used to generate the seed for exact logic.";
                return null;
            }
        }

        // =====================================================================
        // Questions for the server (cached, so each is asked once)
        // =====================================================================

        private readonly Dictionary<long, Task<ScoutedItemInfo?>> _scoutCache = new();
        private readonly Dictionary<string, Task<Dictionary<string, string[]>?>> _itemGroupCache = new();
        private readonly Dictionary<string, Task<Dictionary<string, string[]>?>> _locationGroupCache = new();
        private readonly Dictionary<int, (DateTime At, Task<ArchipelagoClientState?> Task)> _statusCache = new();

        private bool Connected => !_disposed && Session.Socket.Connected;

        /// <summary>What a checked location held. Only for checked locations, so it never spoils anything.</summary>
        public Task<ScoutedItemInfo?> ScoutCheckedLocationAsync(long locationId)
        {
            if (!Connected || !Session.Locations.AllLocationsChecked.Contains(locationId)) return Task.FromResult<ScoutedItemInfo?>(null);
            if (!_scoutCache.TryGetValue(locationId, out var task))
            {
                task = ScoutOneAsync(locationId);
                _scoutCache[locationId] = task;
            }
            return task;
        }

        private async Task<ScoutedItemInfo?> ScoutOneAsync(long locationId)
        {
            try
            {
                var result = await Session.Locations.ScoutLocationsAsync(HintCreationPolicy.None, locationId);
                return result != null && result.TryGetValue(locationId, out var info) ? info : null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Logger.LogDebug($"[{SlotName}] Scout of location {locationId} failed: {ex.Message}");
                // Asked again next time; called on the main thread (the await continues there).
                _scoutCache.Remove(locationId);
                return null;
            }
        }

        /// <summary>The server's item name groups for a game (fetched once per game per connection).</summary>
        public Task<Dictionary<string, string[]>?> ItemGroupsAsync(string game) =>
            CachedGroupsAsync(_itemGroupCache, game, g => Session.DataStorage.GetItemNameGroupsAsync(g));

        /// <summary>The server's location name groups for a game (fetched once per game per connection).</summary>
        public Task<Dictionary<string, string[]>?> LocationGroupsAsync(string game) =>
            CachedGroupsAsync(_locationGroupCache, game, g => Session.DataStorage.GetLocationNameGroupsAsync(g));

        private Task<Dictionary<string, string[]>?> CachedGroupsAsync(Dictionary<string, Task<Dictionary<string, string[]>?>> cache, string game,
            Func<string, Task<Dictionary<string, string[]>>> fetch)
        {
            if (string.IsNullOrEmpty(game) || !Connected) return Task.FromResult<Dictionary<string, string[]>?>(null);
            if (!cache.TryGetValue(game, out var task))
            {
                task = FetchGroupsAsync(() => fetch(game));
                cache[game] = task;
            }
            return task;
        }

        private async Task<Dictionary<string, string[]>?> FetchGroupsAsync(Func<Task<Dictionary<string, string[]>>> fetch)
        {
            try { return await fetch(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Logger.LogDebug($"[{SlotName}] Name groups couldn't be read: {ex.Message}");
                return null;
            }
        }

        /// <summary>A player's client status (connected / playing / goal), refreshed at most every 30 seconds.</summary>
        public Task<ArchipelagoClientState?> ClientStatusAsync(int player)
        {
            if (!Connected) return Task.FromResult<ArchipelagoClientState?>(null);
            if (_statusCache.TryGetValue(player, out var cached) && (DateTime.Now - cached.At).TotalSeconds < 30) return cached.Task;
            var task = FetchStatusAsync(player);
            _statusCache[player] = (DateTime.Now, task);
            return task;
        }

        private async Task<ArchipelagoClientState?> FetchStatusAsync(int player)
        {
            try { return await Session.DataStorage.GetClientStatusAsync(player, Team); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Logger.LogDebug($"[{SlotName}] Status of player {player} couldn't be read: {ex.Message}");
                return null;
            }
        }

        // =====================================================================
        // Session events (network threads): queued, then applied once per frame
        // =====================================================================

        private void OnMessage(LogMessage message)
        {
            lock (_queueLock) _incoming.Add(message);
            Schedule(SlotChange.Messages);
        }

        private void OnItemReceived(ReceivedItemsHelper helper) => Schedule(SlotChange.Items);

        private void OnChecked(System.Collections.ObjectModel.ReadOnlyCollection<long> newlyChecked) => Schedule(SlotChange.Checks);

        private void OnHints(Hint[] hints)
        {
            lock (_queueLock) _incomingHints = hints ?? Array.Empty<Hint>();
            Schedule(SlotChange.Hints);
        }

        private void OnSocketClosed(string reason)
        {
            lock (_queueLock) _closedReason = string.IsNullOrWhiteSpace(reason) ? "the connection closed" : reason;
            Schedule(SlotChange.Connection | SlotChange.Messages);
        }

        private bool _goalReachedPending;

        /// <summary>A part of the model (its logic) changed: the views hear it with everything else this frame.</summary>
        internal void Report(SlotChange change) => Schedule(change);

        private void Schedule(SlotChange change, bool goalReached = false)
        {
            lock (_queueLock)
            {
                if (_disposed) return;
                _pending |= change;
                if (goalReached) _goalReachedPending = true;
                if (_flushScheduled) return;
                _flushScheduled = true;
            }
            AP_Atlas.UI.Ui.Defer(null, Flush, $"updating {SlotName}");
        }

        /// <summary>Applies what arrived since the last frame and tells the views once (main thread).</summary>
        private void Flush()
        {
            using var __perf = PerfMonitor.Measure($"[{SlotName}] Slot update");
            SlotChange change;
            List<LogMessage> messages;
            Hint[]? hints;
            string? closed;
            bool goalReached;
            lock (_queueLock)
            {
                _flushScheduled = false;
                if (_disposed) return;
                change = _pending;
                _pending = SlotChange.None;
                messages = _incoming.ToList();
                _incoming.Clear();
                hints = _incomingHints;
                _incomingHints = null;
                closed = _closedReason;
                _closedReason = null;
                goalReached = _goalReachedPending;
                _goalReachedPending = false;
            }
            foreach (var message in messages)
            {
                AddEntry(new ChatEntry { APMessage = message, Sequence = _nextSequence++ });
                if (message is GoalLogMessage goal && goal.IsActivePlayer) goalReached = true;
                if (message is HintItemSendLogMessage hint)
                {
                    foreach (var part in hint.Parts.OfType<LocationMessagePart>()) _hintedLocations.Add(part.LocationId);
                    change |= SlotChange.Hints;
                }
            }
            if (hints != null)
            {
                CurrentHints = hints;
                int me = PlayerSlot;
                foreach (var h in hints)
                    if (h.FindingPlayer == me && !h.Found) _hintedLocations.Add(h.LocationId);
            }
            if (closed != null) AddEntry(new ChatEntry { SystemMessage = $"[color=red]Connection lost: {closed}[/color]", Sequence = _nextSequence++ });
            if (goalReached && !GoalCompleted)
            {
                GoalCompleted = true;
                change |= SlotChange.Goal;
            }
            // New items or checks: logic works out what they changed (bursts coalesce into one run).
            if ((change & (SlotChange.Items | SlotChange.Checks)) != 0) Logic.Refresh();
            if (change == SlotChange.None) return;
            try { Changed?.Invoke(change); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A failing view mustn't stop the next frame's updates.
                Async.Report(ex, $"updating {SlotName}'s views", tellUser: false);
            }
        }

        private void AddEntry(ChatEntry entry)
        {
            _chat.Add(entry);
            if (_chat.Count > ChatLimit) _chat.RemoveRange(0, _chat.Count - ChatLimit);
        }

        /// <summary>
        /// Stops listening to the session and stops the slot's logic engine (the slot was replaced, closed or deleted).
        /// Pending changes are dropped and nothing is raised afterwards. Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            lock (_queueLock)
            {
                if (_disposed) return;
                _disposed = true;
                _incoming.Clear();
            }
            Session.MessageLog.OnMessageReceived -= OnMessage;
            Session.Socket.SocketClosed -= OnSocketClosed;
            Session.Items.ItemReceived -= OnItemReceived;
            Session.Locations.CheckedLocationsUpdated -= OnChecked;
            RaceRules.Changed -= OnRaceRulesChanged;
            Logic.Dispose();
            Changed = null;
        }
    }
}
