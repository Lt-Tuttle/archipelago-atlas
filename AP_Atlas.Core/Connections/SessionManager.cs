using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Packets;

namespace AP_Atlas.Core.Connections
{
    /// <summary>One slot of one multiworld.</summary>
    public readonly record struct SlotId(string ProfileId, string SlotName);

    /// <summary>How to log in to a slot: the server address as the user typed it, and the room password (null for none).</summary>
    public sealed record SlotLogin(SlotId Slot, string Server, string? Password);

    /// <summary>How a connection attempt ended.</summary>
    public enum ConnectOutcome
    {
        /// <summary>Logged in.</summary>
        Connected,
        /// <summary>The server answered and said no (wrong slot, game, version or password): trying again won't help.</summary>
        Refused,
        /// <summary>Atlas can't try at all (an address it can't read, a connection library it can't use): trying again won't help.</summary>
        Failed,
        /// <summary>The server couldn't be reached, or the connection broke: it may work later.</summary>
        Unreachable,
        /// <summary>No answer within the time limit. The attempt was closed (and so is a login that finishes later).</summary>
        TimedOut,
        /// <summary>Atlas is closing, or the attempt was called off.</summary>
        Cancelled
    }

    /// <summary>What a connection attempt came to. <see cref="Slot"/> is set when it connected.</summary>
    public sealed record ConnectResult(ConnectOutcome Outcome, string Message, ConnectedSlot? Slot = null);

    /// <summary>A slot that logged in: its connection, and its text client's lines (kept until its window takes them over).</summary>
    public sealed class ConnectedSlot
    {
        private readonly SessionManager _manager;
        private readonly SlotInbox _inbox;

        internal ConnectedSlot(SessionManager manager, SlotId slot, ArchipelagoSession session, LoginSuccessful login,
            IReadOnlyDictionary<string, string> dataChecksums, SlotInbox inbox)
        {
            _manager = manager;
            Slot = slot;
            Session = session;
            Login = login;
            DataChecksums = dataChecksums;
            _inbox = inbox;
        }

        public SlotId Slot { get; }
        public ArchipelagoSession Session { get; }
        public LoginSuccessful Login { get; }

        /// <summary>The slot's team in its multiworld (teams count from 0).</summary>
        public int Team => Login.Team;

        /// <summary>Each game's data checksum from the server's room info, sent before the login.</summary>
        public IReadOnlyDictionary<string, string> DataChecksums { get; }

        /// <summary>
        /// Hands the slot's text client lines over to <paramref name="receive"/>: returns those that arrived before, in
        /// order, and passes on each later one as it arrives (on network threads). The room's lines come from the
        /// multiworld's text connection (<see cref="SessionManager"/>); lines meant for this slot alone come from its own
        /// connection. Call once.
        /// </summary>
        public IReadOnlyList<LogMessage> ReceiveMessages(Action<LogMessage> receive) => _inbox.TakeOver(receive);

        /// <summary>Stops passing lines on (the slot ended). Safe to call more than once.</summary>
        public void StopReceivingMessages() => _inbox.Close();

        /// <summary>
        /// Whether the slot's own connection receives text: it's its multiworld team's text connection, or a command was
        /// sent from it. When it doesn't, lines the server sends to the slot alone (a new hint's line) don't arrive; the
        /// slot's hint list still does.
        /// </summary>
        public bool ReceivesText => _manager.ReceivesText(Session);

        /// <summary>
        /// Says something in the room as this slot: chat, or a command (starting with "!"). A command's answer goes only
        /// to the connection that sent it, and only if it receives text, so a slot that doesn't is switched on first
        /// (the room is told its tags changed).
        /// </summary>
        public Task SayAsync(string text) => _manager.SayAsync(Session, text);

        internal void Deliver(LogMessage message) => _inbox.Deliver(message);

        internal void Preload(IReadOnlyCollection<LogMessage> messages) => _inbox.Preload(messages);

        /// <summary>How many lines are being kept until the window takes over (for tests).</summary>
        internal int EarlyMessageCount => _inbox.Count;
    }

    /// <summary>Tuning for <see cref="SessionManager"/>; the tests shorten the waits.</summary>
    public sealed class SessionManagerOptions
    {
        /// <summary>How long a connection and login may take before the attempt is closed.</summary>
        public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The waits before each automatic reconnect (about 20 minutes in all), then Atlas stops. Deliberately few and slow:
        /// a connection attempt wakes a sleeping archipelago.gg room, so Atlas must never keep a closed room busy.
        /// </summary>
        public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } = new[] { 15, 30, 60, 120, 300, 600 }.Select(s => TimeSpan.FromSeconds(s)).ToArray();

        /// <summary>
        /// How far each wait may vary either way (0.2 = ±20%), so many trackers that lost the same server don't all come
        /// back in the same second.
        /// </summary>
        public double Jitter { get; init; } = 0.2;

        /// <summary>
        /// How long a slot that logged in without text waits for its own join line before showing the room's lines it
        /// held meanwhile (see SessionManager.Text.cs). The line normally comes moments after the login; a server that
        /// doesn't announce joins never sends it.
        /// </summary>
        public TimeSpan JoinLineWait { get; init; } = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Atlas's connections to Archipelago servers: connecting and logging in, noticing when a connection drops,
    /// reconnecting carefully, and closing everything properly. Godot-free, so its tests run against a fake server.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Connections are made one at a time, so a whole multiworld doesn't hit its server at once.</item>
    /// <item>Every session is made by <see cref="AtlasSessions"/> (the games' names stay in Atlas's folder).</item>
    /// <item>A login that takes too long is closed, and so is one that finishes after Atlas gave up on it, so nothing
    /// is left open on the server.</item>
    /// <item>A dropped connection (a close from the server, or a socket that died without one, see
    /// <see cref="CheckForDrops"/>) is reconnected after the waits in <see cref="SessionManagerOptions.ReconnectDelays"/>,
    /// if <see cref="AutoReconnect"/> is on. A refusal stops it, and so does running out of tries.</item>
    /// <item>Closing Atlas closes every session, including ones still connecting, with a proper close frame.</item>
    /// <item>Only one connection per multiworld team receives the room's text; the others log in with NoText, and each
    /// slot's text client still gets every line (see SessionManager.Text.cs).</item>
    /// </list>
    /// Events are raised on network and timer threads, never while the manager's lock is held: the window hands them
    /// to the main thread.
    /// </remarks>
    public sealed partial class SessionManager
    {
        private readonly DataPackageStore _store;
        private readonly SessionManagerOptions _options;
        private readonly SemaphoreSlim _connectGate = new(1, 1);
        private readonly object _lock = new();
        // Every session Atlas opened and hasn't closed, including ones still connecting or logging in.
        private readonly HashSet<ArchipelagoSession> _open = new();
        // Sessions whose connection hasn't opened yet, and those of them Atlas closed meanwhile: the library can't close a
        // connection that isn't open, so each is closed as it opens (left alone, it would stay open on the server). Weak, so
        // a connection that never opens leaves nothing behind. Guarded by _lock.
        private readonly ConditionalWeakTable<ArchipelagoSession, object> _opening = new();
        private readonly ConditionalWeakTable<ArchipelagoSession, object> _closeOnOpen = new();
        // Sessions that finished logging in, and their slot. Only these count as dropped when their socket closes.
        private readonly Dictionary<ArchipelagoSession, ConnectedSlot> _loggedIn = new();
        // What each slot logged in with, for reconnecting.
        private readonly Dictionary<SlotId, SlotLogin> _logins = new();
        private readonly Dictionary<SlotId, PendingReconnect> _reconnects = new();
        private bool _closing;
        // Multiworlds that were deleted: a login for one of their slots that's still under way is closed when it finishes.
        private readonly HashSet<string> _forgottenProfiles = new();
        private volatile bool _autoReconnect = true;

        public SessionManager(DataPackageStore store, SessionManagerOptions? options = null)
        {
            _store = store;
            _options = options ?? new SessionManagerOptions();
        }

        /// <summary>Whether a dropped connection is reconnected automatically (the user's setting). Turning it off cancels pending reconnects.</summary>
        public bool AutoReconnect
        {
            get => _autoReconnect;
            set
            {
                _autoReconnect = value;
                if (!value) CancelReconnects(_ => true);
            }
        }

        /// <summary>A logged-in slot's connection ended without Atlas closing it: the slot and why.</summary>
        public event Action<SlotId, string>? Dropped;

        /// <summary>An automatic reconnect is waiting: the slot, which try (1-based), how many tries there are, and the wait.</summary>
        public event Action<SlotId, int, int, TimeSpan>? ReconnectScheduled;

        /// <summary>An automatic reconnect logged in. The previous session for the slot was already closed.</summary>
        public event Action<ConnectedSlot>? Reconnected;

        /// <summary>Automatic reconnecting stopped for good: the slot, and the server's refusal (null when every try went unanswered).</summary>
        public event Action<SlotId, string?>? ReconnectStopped;

        /// <summary>How many automatic reconnects are tried before Atlas stops.</summary>
        public int ReconnectTries => _options.ReconnectDelays.Count;

        /// <summary>A session reported a socket error: the slot and the message.</summary>
        public event Action<SlotId, string>? SocketError;

        /// <summary>Whether the slot has a logged-in session whose socket is still open.</summary>
        public bool IsLoggedIn(SlotId slot)
        {
            lock (_lock) return _loggedIn.Any(entry => entry.Value.Slot == slot && IsOpen(entry.Key));
        }

        /// <summary>Whether an automatic reconnect is waiting or under way for the slot.</summary>
        public bool IsReconnecting(SlotId slot)
        {
            lock (_lock) return _reconnects.ContainsKey(slot);
        }

        /// <summary>
        /// Connects and logs in to a slot (waiting for any other connection attempt to finish first). Cancels an automatic
        /// reconnect waiting for the same slot: the user took over. A slot's previous session is closed once the new one
        /// has logged in.
        /// </summary>
        public Task<ConnectResult> ConnectAsync(SlotLogin login, CancellationToken ct = default)
        {
            CancelReconnects(slot => slot == login.Slot);
            return ConnectCoreAsync(login, ct);
        }

        /// <summary>Closes a slot's session on purpose (with a close frame) and cancels any automatic reconnect for it.</summary>
        public Task DisconnectAsync(SlotId slot)
        {
            CancelReconnects(s => s == slot);
            List<ArchipelagoSession> sessions;
            lock (_lock)
            {
                _logins.Remove(slot);
                sessions = _loggedIn.Where(entry => entry.Value.Slot == slot).Select(entry => entry.Key).ToList();
            }
            return Task.WhenAll(sessions.Select(CloseAsync));
        }

        /// <summary>Closes every slot of a multiworld that was deleted, and forgets how to log in to them.</summary>
        public Task ForgetProfileAsync(string profileId)
        {
            CancelReconnects(slot => slot.ProfileId == profileId);
            List<ArchipelagoSession> sessions;
            lock (_lock)
            {
                _forgottenProfiles.Add(profileId);
                foreach (var slot in _logins.Keys.Where(s => s.ProfileId == profileId).ToList()) _logins.Remove(slot);
                foreach (var slot in _teams.Keys.Where(s => s.ProfileId == profileId).ToList()) _teams.Remove(slot);
                sessions = _loggedIn.Where(entry => entry.Value.Slot.ProfileId == profileId).Select(entry => entry.Key).ToList();
            }
            return Task.WhenAll(sessions.Select(CloseAsync));
        }

        /// <summary>A multiworld's server address or password changed: automatic reconnects use the new ones.</summary>
        public void UpdateLogins(string profileId, string server, string? password)
        {
            lock (_lock)
                foreach (var slot in _logins.Keys.Where(s => s.ProfileId == profileId).ToList())
                    _logins[slot] = _logins[slot] with { Server = server, Password = password };
        }

        /// <summary>
        /// Notices logged-in connections whose socket died without a close event (a server that crashed or lost power only
        /// produces a socket error). Cheap; the window calls it twice a second.
        /// </summary>
        public void CheckForDrops()
        {
            List<KeyValuePair<ArchipelagoSession, ConnectedSlot>> gone;
            var switchOn = new List<ConnectedSlot>();
            lock (_lock)
            {
                if (_closing) return;
                gone = _loggedIn.Where(entry => !IsOpen(entry.Key)).ToList();
                foreach (var (session, _) in gone)
                {
                    _loggedIn.Remove(session);
                    _open.Remove(session);
                }
                foreach (var (session, _) in gone)
                    if (EndText(session) is { } next) switchOn.Add(next);
            }
            foreach (var (session, _) in gone) AtlasSessions.Finished(session);
            SwitchOnText(switchOn);
            foreach (var (_, slot) in gone) OnDropped(slot.Slot, "no close from the server");
        }

        /// <summary>
        /// Closes every session, including ones still connecting, with a close frame, and cancels automatic reconnects. Used
        /// when Atlas closes; nothing connects afterwards. Returns how many sessions there were, and whether all confirmed
        /// closing within <paramref name="cap"/> (the system closes the rest as Atlas exits).
        /// </summary>
        public async Task<(int Sessions, bool InTime)> CloseAllAsync(TimeSpan cap)
        {
            List<ArchipelagoSession> sessions;
            lock (_lock)
            {
                _closing = true;
                sessions = _open.ToList();
            }
            CancelReconnects(_ => true);
            if (sessions.Count == 0) return (0, true);
            var all = Task.WhenAll(sessions.Select(CloseAsync));
            var first = await Task.WhenAny(all, Task.Delay(cap)).ConfigureAwait(false);
            return (sessions.Count, first == all);
        }

        // =====================================================================
        // Connecting
        // =====================================================================

        private async Task<ConnectResult> ConnectCoreAsync(SlotLogin login, CancellationToken ct)
        {
            try { await _connectGate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new ConnectResult(ConnectOutcome.Cancelled, "Cancelled."); }
            try
            {
                bool text;
                lock (_lock)
                {
                    if (_closing) return new ConnectResult(ConnectOutcome.Cancelled, "Atlas is closing.");
                    if (_forgottenProfiles.Contains(login.Slot.ProfileId)) return new ConnectResult(ConnectOutcome.Cancelled, "Its multiworld was deleted.");
                    text = WantsText(login.Slot);
                    // A slot logging in without text gets the room's lines that arrive meanwhile (its own join among them).
                    if (!text) StartReplay(login.Slot.ProfileId);
                }
                try
                {
                    ArchipelagoSession session;
                    try { session = AtlasSessions.Create(login.Server, _store, login.Slot.SlotName); }
                    catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException)
                    {
                        return new ConnectResult(ConnectOutcome.Failed, ex.Message);
                    }
                    lock (_lock)
                    {
                        _open.Add(session);
                        _opening.AddOrUpdate(session, _lock);
                    }
                    return await LogInAsync(session, login, text, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (!text) lock (_lock) _replays.Remove(login.Slot.ProfileId);
                }
            }
            finally
            {
                _connectGate.Release();
            }
        }

        private async Task<ConnectResult> LogInAsync(ArchipelagoSession session, SlotLogin login, bool text, CancellationToken ct)
        {
            // The connection's lines, from the start: each goes where it belongs as it arrives (OnText).
            var inbox = new SlotInbox();
            session.MessageLog.OnMessageReceived += message => OnText(session, inbox, message);
            // The room info, sent before the login, carries each game's data checksum.
            var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void OnPacket(ArchipelagoPacketBase packet)
            {
                if (packet is RoomInfoPacket info && info.DataPackageChecksums != null)
                    lock (checksums) foreach (var entry in info.DataPackageChecksums) checksums[entry.Key] = entry.Value;
            }
            session.Socket.PacketReceived += OnPacket;
            session.Socket.ErrorReceived += (_, message) => OnSocketError(session, login.Slot, message);
            session.Socket.SocketClosed += reason => OnSocketClosed(session, reason);
            session.Socket.SocketOpened += () => OnSocketOpened(session);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Socket.SocketClosed += _ => closed.TrySetResult();

            var attempt = TryLogInAsync(session, login, text, closed.Task);
            using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var first = await Task.WhenAny(attempt, Task.Delay(_options.LoginTimeout, giveUp.Token)).ConfigureAwait(false);
            await giveUp.CancelAsync().ConfigureAwait(false); // ends the delay if the login won
            session.Socket.PacketReceived -= OnPacket;
            if (first != attempt)
            {
                inbox.Close();
                await CloseAsync(session).ConfigureAwait(false);
                // The login may still finish after Atlas gave up on it: close it then too, so it isn't left open on the server.
                Async.Fire(CloseWhenDoneAsync(attempt, session), "closing a connection that finished after it timed out", tellUser: false);
                return ct.IsCancellationRequested
                    ? new ConnectResult(ConnectOutcome.Cancelled, "Cancelled.")
                    : new ConnectResult(ConnectOutcome.TimedOut, $"No answer within {_options.LoginTimeout.TotalSeconds:0.#} seconds.");
            }

            var result = await attempt.ConfigureAwait(false);
            if (result is LoginSuccessful success)
            {
                Dictionary<string, string> snapshot;
                lock (checksums) snapshot = new Dictionary<string, string>(checksums, StringComparer.OrdinalIgnoreCase);
                var connected = new ConnectedSlot(this, login.Slot, session, success, snapshot, inbox);
                List<ArchipelagoSession>? previous = null;
                var switchOn = new List<ConnectedSlot>();
                string why = "Atlas is closing.";
                lock (_lock)
                {
                    if (_forgottenProfiles.Contains(login.Slot.ProfileId)) why = "Its multiworld was deleted while it logged in.";
                    else if (!_closing && _open.Contains(session))
                    {
                        // Forget the slot's earlier session before closing it, so its closing isn't taken for a drop.
                        previous = _loggedIn.Where(entry => entry.Value.Slot == login.Slot).Select(entry => entry.Key).ToList();
                        _loggedIn[session] = connected;
                        foreach (var old in previous) _loggedIn.Remove(old);
                        _logins[login.Slot] = login;
                        switchOn = StartText(session, connected, text, previous);
                    }
                }
                if (previous != null)
                {
                    SwitchOnText(switchOn);
                    foreach (var old in previous) Async.Fire(CloseAsync(old), "closing a slot's previous connection", tellUser: false);
                    return new ConnectResult(ConnectOutcome.Connected, "Connected.", connected);
                }
                // Atlas started closing (or closed this session, or its multiworld was deleted) while it logged in.
                inbox.Close();
                await CloseAsync(session).ConfigureAwait(false);
                return new ConnectResult(ConnectOutcome.Cancelled, why);
            }

            inbox.Close();
            // A login that failed because Atlas let it go (closing, a deleted multiworld, or closed while it logged in) was
            // cancelled: nothing to report, and nothing to try again.
            string? cancelled = null;
            lock (_lock)
            {
                if (_forgottenProfiles.Contains(login.Slot.ProfileId)) cancelled = "Its multiworld was deleted while it logged in.";
                else if (_closing) cancelled = "Atlas is closing.";
                else if (!_open.Contains(session)) cancelled = "Atlas closed the connection while it logged in.";
            }
            await CloseAsync(session).ConfigureAwait(false);
            if (cancelled != null) return new ConnectResult(ConnectOutcome.Cancelled, cancelled);
            var failure = (LoginFailure)result;
            string errors = string.Join(", ", failure.Errors ?? Array.Empty<string>());
            // Only a refusal (wrong slot, game, version, password) is final; a server that couldn't be reached may come back.
            bool refused = (failure.ErrorCodes ?? Array.Empty<ConnectionRefusedError>()).Any(code => code != ConnectionRefusedError.UnknownError);
            return new ConnectResult(refused ? ConnectOutcome.Refused : ConnectOutcome.Unreachable, errors.Length > 0 ? errors : "The connection failed.");
        }

        /// <summary>
        /// Connects the socket and logs in, with or without the room's text; a connection that can't be made, or that
        /// closes before the server answers, becomes a failed login.
        /// </summary>
        private async Task<LoginResult> TryLogInAsync(ArchipelagoSession session, SlotLogin login, bool text, Task closed)
        {
            try
            {
                // The library waits for the room's info for good: a connection that closes first (Atlas closed it, or the
                // server did) ends the login.
                var connecting = session.ConnectAsync();
                if (await Task.WhenAny(connecting, closed).ConfigureAwait(false) != connecting)
                    return new LoginFailure("The connection closed before the server answered.");
                await connecting.ConfigureAwait(false);
                // Atlas gave up on this login (its time limit passed, or Atlas is closing) while it connected: no late login.
                lock (_lock)
                {
                    if (!_open.Contains(session)) return new LoginFailure("Atlas closed the connection before it logged in.");
                }
                var loggingIn = session.LoginAsync("", login.Slot.SlotName, ItemsHandlingFlags.AllItems, new Version(0, 5, 0),
                    text ? TextTags : QuietTags, null, string.IsNullOrEmpty(login.Password) ? null : login.Password, true);
                // The library waits for an answer for 4 seconds even when the server closed the connection meanwhile (a
                // room shutting down): a close ends the login at once.
                if (await Task.WhenAny(loggingIn, closed).ConfigureAwait(false) != loggingIn)
                    return new LoginFailure("The connection closed before the server answered the login.");
                return await loggingIn.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Can't reach the server, a refused or broken connection, a bad address: the caller reports it.
                return new LoginFailure(ex.GetBaseException().Message);
            }
        }

        /// <summary>
        /// A login that timed out may still connect and finish later: its connection is closed then, however the login
        /// ended (a refused login's connection is still open).
        /// </summary>
        private async Task CloseWhenDoneAsync(Task<LoginResult> attempt, ArchipelagoSession session)
        {
            LoginResult late = await attempt.ConfigureAwait(false);
            lock (_lock) _open.Add(session); // so CloseAsync closes it (it was forgotten when the attempt timed out)
            await CloseAsync(session).ConfigureAwait(false);
            if (late.Successful) Logger.LogDebug("A login that finished after its time limit was closed.");
        }

        // =====================================================================
        // Closing and drops
        // =====================================================================

        /// <summary>
        /// Forgets a session, then closes its socket with a close frame, and gives its thread back
        /// (<see cref="AtlasSessions.Finished"/>). Safe to call more than once, and on a socket that never opened or
        /// already broke (there's nothing to close then, which isn't a failure).
        /// </summary>
        private async Task CloseAsync(ArchipelagoSession session)
        {
            ConnectedSlot? switchOn;
            lock (_lock)
            {
                _loggedIn.Remove(session);
                switchOn = EndText(session);
                if (!_open.Remove(session)) return;
            }
            if (switchOn != null) SwitchOnText(new[] { switchOn });
            if (!await DisconnectAsync(session).ConfigureAwait(false))
            {
                // Not open: if it's still opening, it's closed as it opens (OnSocketOpened). If it opened just now, it's
                // closed here.
                bool opening;
                lock (_lock)
                {
                    opening = _opening.TryGetValue(session, out _);
                    if (opening) _closeOnOpen.AddOrUpdate(session, _lock);
                }
                if (opening) return;
                if (session.Socket.Connected) await DisconnectAsync(session).ConfigureAwait(false);
            }
            AtlasSessions.Finished(session);
        }

        /// <summary>Closes a session's connection with a close frame. False if there was nothing to close (not open, or gone).</summary>
        private static async Task<bool> DisconnectAsync(ArchipelagoSession session)
        {
            try
            {
                var closing = session.Socket.DisconnectAsync();
                if (closing != null) await closing.ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException
                                           or System.Net.WebSockets.WebSocketException or System.IO.IOException)
            {
                Logger.LogDebug("A server connection had nothing left to close: " + ex.Message);
                return false;
            }
        }

        /// <summary>A connection opened. If Atlas closed its session while it opened, it's closed now.</summary>
        private void OnSocketOpened(ArchipelagoSession session)
        {
            bool close;
            lock (_lock)
            {
                _opening.Remove(session);
                close = _closeOnOpen.Remove(session);
            }
            if (close) Async.Fire(CloseOpenedAsync(session), "closing a connection that opened after Atlas closed it", tellUser: false);
        }

        private static async Task CloseOpenedAsync(ArchipelagoSession session)
        {
            await DisconnectAsync(session).ConfigureAwait(false);
            AtlasSessions.Finished(session);
        }

        private void OnSocketClosed(ArchipelagoSession session, string reason)
        {
            ConnectedSlot? slot, switchOn;
            lock (_lock)
            {
                // Atlas forgets a session before closing it on purpose, so one still logged in here dropped by itself.
                if (!_loggedIn.Remove(session, out slot)) return;
                _open.Remove(session);
                switchOn = EndText(session);
            }
            AtlasSessions.Finished(session);
            if (switchOn != null) SwitchOnText(new[] { switchOn });
            OnDropped(slot.Slot, string.IsNullOrWhiteSpace(reason) ? "the server closed the connection" : reason);
        }

        /// <summary>How often a connection's socket errors are reported after its first: the ones between are counted.</summary>
        internal static readonly TimeSpan SocketErrorSpacing = TimeSpan.FromSeconds(30);

        // Each connection's socket errors since the last one reported. Guarded by _lock.
        private sealed class ErrorTally
        {
            public DateTime? LastReported; // a SteadyClock time
            public int Unreported;
        }

        private readonly ConditionalWeakTable<ArchipelagoSession, ErrorTally> _errors = new();

        private void OnSocketError(ArchipelagoSession session, SlotId slot, string message)
        {
            int skipped;
            lock (_lock)
            {
                // A connection Atlas is done with reports a closed socket when its send loop ends (AtlasSessions.Finished): not news.
                if (!_open.Contains(session)) return;
                // A server that sends a stream of packets the library can't read (each is an error) is reported once, then at
                // most every SocketErrorSpacing with a count, so it can't fill the log or keep the window busy.
                var tally = _errors.GetValue(session, _ => new ErrorTally());
                if (tally.LastReported is { } last && SteadyClock.UtcNow - last < SocketErrorSpacing)
                {
                    tally.Unreported++;
                    return;
                }
                skipped = tally.Unreported;
                tally.Unreported = 0;
                tally.LastReported = SteadyClock.UtcNow;
            }
            string text = skipped > 0 ? $"{message} (and {skipped} more since the last one reported)" : message;
            Raise(() => SocketError?.Invoke(slot, text), "reporting a socket error");
        }

        private void OnDropped(SlotId slot, string reason)
        {
            Raise(() => Dropped?.Invoke(slot, reason), "reporting a dropped connection");
            if (AutoReconnect) ScheduleReconnect(slot);
        }

        /// <summary>
        /// Raises an event. A failing handler is logged, never thrown into the connection library's network code or a
        /// reconnect, where it would stop the session or the retries.
        /// </summary>
        private static void Raise(Action raise, string doing)
        {
            try { raise(); }
            catch (Exception ex) { Async.Report(ex, doing, tellUser: false); }
        }

        private static bool IsOpen(ArchipelagoSession session)
        {
            try { return session.Socket.Connected; }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return false; }
        }

        // =====================================================================
        // Reconnecting
        // =====================================================================

        private sealed class PendingReconnect
        {
            public int Tries;
            public readonly CancellationTokenSource Cancel = new();
        }

        private void ScheduleReconnect(SlotId slot)
        {
            PendingReconnect pending;
            int tries = _options.ReconnectDelays.Count;
            int attempt;
            TimeSpan wait;
            lock (_lock)
            {
                if (_closing || !_logins.ContainsKey(slot)) return;
                if (!_reconnects.TryGetValue(slot, out var existing)) _reconnects[slot] = existing = new PendingReconnect();
                pending = existing;
                if (pending.Tries >= tries)
                {
                    _reconnects.Remove(slot);
                    attempt = -1;
                    wait = TimeSpan.Zero;
                }
                else
                {
                    attempt = ++pending.Tries;
                    double spread = 1 + (_options.Jitter == 0 ? 0 : (Random.Shared.NextDouble() * 2 - 1) * _options.Jitter);
                    wait = TimeSpan.FromTicks((long)(_options.ReconnectDelays[attempt - 1].Ticks * spread));
                }
            }
            if (attempt < 0)
            {
                Raise(() => ReconnectStopped?.Invoke(slot, null), "reporting that reconnecting stopped");
                return;
            }
            Raise(() => ReconnectScheduled?.Invoke(slot, attempt, tries, wait), "reporting a reconnect");
            Async.Fire(ReconnectLaterAsync(slot, pending, wait), $"reconnecting {slot.SlotName}", tellUser: false);
        }

        private async Task ReconnectLaterAsync(SlotId slot, PendingReconnect pending, TimeSpan wait)
        {
            var token = pending.Cancel.Token;
            await Task.Delay(wait, token).ConfigureAwait(false);
            SlotLogin? login;
            lock (_lock)
            {
                if (_closing || !_reconnects.TryGetValue(slot, out var current) || current != pending || !_logins.TryGetValue(slot, out login)) return;
            }
            if (IsLoggedIn(slot))
            {
                // Already back (the user connected it meanwhile).
                Finish(slot, pending);
                return;
            }
            var result = await ConnectCoreAsync(login, token).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case ConnectOutcome.Connected:
                    Finish(slot, pending);
                    Raise(() => Reconnected?.Invoke(result.Slot!), "reporting a reconnect");
                    break;
                case ConnectOutcome.Refused:
                case ConnectOutcome.Failed:
                    Finish(slot, pending);
                    Raise(() => ReconnectStopped?.Invoke(slot, result.Message), "reporting that reconnecting stopped");
                    break;
                case ConnectOutcome.Cancelled:
                    break;
                default:
                    // Unreachable or timed out: wait longer, then try again (or stop after the last try).
                    lock (_lock)
                        if (!_reconnects.TryGetValue(slot, out var current) || current != pending) return;
                    ScheduleReconnect(slot);
                    break;
            }
        }

        private void Finish(SlotId slot, PendingReconnect pending)
        {
            lock (_lock)
                if (_reconnects.TryGetValue(slot, out var current) && current == pending) _reconnects.Remove(slot);
        }

        private void CancelReconnects(Func<SlotId, bool> which)
        {
            List<PendingReconnect> cancelled;
            lock (_lock)
            {
                var slots = _reconnects.Keys.Where(which).ToList();
                cancelled = slots.Select(slot => _reconnects[slot]).ToList();
                foreach (var slot in slots) _reconnects.Remove(slot);
            }
            foreach (var pending in cancelled) pending.Cancel.Cancel();
        }
    }

}
