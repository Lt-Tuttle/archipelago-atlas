using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>A slot that logged in, with what the server sent before the slot's window existed.</summary>
    public sealed class ConnectedSlot
    {
        private readonly EarlyMessages _early;

        internal ConnectedSlot(SlotId slot, ArchipelagoSession session, LoginSuccessful login, IReadOnlyDictionary<string, string> dataChecksums, EarlyMessages early)
        {
            Slot = slot;
            Session = session;
            Login = login;
            DataChecksums = dataChecksums;
            _early = early;
        }

        public SlotId Slot { get; }
        public ArchipelagoSession Session { get; }
        public LoginSuccessful Login { get; }

        /// <summary>Each game's data checksum from the server's room info, sent before the login.</summary>
        public IReadOnlyDictionary<string, string> DataChecksums { get; }

        /// <summary>
        /// The chat and server messages that arrived before the slot's window took over, in order. Call once, after the
        /// window has subscribed to the session's messages: from then on only the window receives them.
        /// </summary>
        public IReadOnlyList<LogMessage> TakeEarlyMessages() => _early.Take();

        /// <summary>How many messages are being kept (for tests).</summary>
        internal int EarlyMessageCount => _early.Count;
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
    /// </list>
    /// Events are raised on network and timer threads, never while the manager's lock is held: the window hands them
    /// to the main thread.
    /// </remarks>
    public sealed class SessionManager
    {
        private readonly DataPackageStore _store;
        private readonly SessionManagerOptions _options;
        private readonly SemaphoreSlim _connectGate = new(1, 1);
        private readonly object _lock = new();
        // Every session Atlas opened and hasn't closed, including ones still connecting or logging in.
        private readonly HashSet<ArchipelagoSession> _open = new();
        // Sessions that finished logging in, and their slot. Only these count as dropped when their socket closes.
        private readonly Dictionary<ArchipelagoSession, SlotId> _loggedIn = new();
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
            lock (_lock) return _loggedIn.Any(entry => entry.Value == slot && IsOpen(entry.Key));
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
                sessions = _loggedIn.Where(entry => entry.Value == slot).Select(entry => entry.Key).ToList();
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
                sessions = _loggedIn.Where(entry => entry.Value.ProfileId == profileId).Select(entry => entry.Key).ToList();
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
            List<SlotId> dropped;
            lock (_lock)
            {
                if (_closing) return;
                var gone = _loggedIn.Where(entry => !IsOpen(entry.Key)).ToList();
                foreach (var (session, _) in gone)
                {
                    _loggedIn.Remove(session);
                    _open.Remove(session);
                }
                dropped = gone.Select(entry => entry.Value).ToList();
            }
            foreach (var slot in dropped) OnDropped(slot, "no close from the server");
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
                lock (_lock)
                {
                    if (_closing) return new ConnectResult(ConnectOutcome.Cancelled, "Atlas is closing.");
                    if (_forgottenProfiles.Contains(login.Slot.ProfileId)) return new ConnectResult(ConnectOutcome.Cancelled, "Its multiworld was deleted.");
                }
                ArchipelagoSession session;
                try { session = AtlasSessions.Create(login.Server, _store); }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException)
                {
                    return new ConnectResult(ConnectOutcome.Failed, ex.Message);
                }
                lock (_lock) _open.Add(session);
                return await LogInAsync(session, login, ct).ConfigureAwait(false);
            }
            finally
            {
                _connectGate.Release();
            }
        }

        private async Task<ConnectResult> LogInAsync(ArchipelagoSession session, SlotLogin login, CancellationToken ct)
        {
            var early = new EarlyMessages(session);
            // The room info, sent before the login, carries each game's data checksum.
            var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void OnPacket(ArchipelagoPacketBase packet)
            {
                if (packet is RoomInfoPacket info && info.DataPackageChecksums != null)
                    lock (checksums) foreach (var entry in info.DataPackageChecksums) checksums[entry.Key] = entry.Value;
            }
            session.Socket.PacketReceived += OnPacket;
            session.Socket.ErrorReceived += (_, message) => Raise(() => SocketError?.Invoke(login.Slot, message), "reporting a socket error");
            session.Socket.SocketClosed += reason => OnSocketClosed(session, reason);

            var attempt = TryLogInAsync(session, login);
            using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var first = await Task.WhenAny(attempt, Task.Delay(_options.LoginTimeout, giveUp.Token)).ConfigureAwait(false);
            await giveUp.CancelAsync().ConfigureAwait(false); // ends the delay if the login won
            session.Socket.PacketReceived -= OnPacket;
            if (first != attempt)
            {
                early.Take();
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
                List<ArchipelagoSession>? previous = null;
                string why = "Atlas is closing.";
                lock (_lock)
                {
                    if (_forgottenProfiles.Contains(login.Slot.ProfileId)) why = "Its multiworld was deleted while it logged in.";
                    else if (!_closing && _open.Contains(session))
                    {
                        // Forget the slot's earlier session before closing it, so its closing isn't taken for a drop.
                        previous = _loggedIn.Where(entry => entry.Value == login.Slot).Select(entry => entry.Key).ToList();
                        foreach (var old in previous) _loggedIn.Remove(old);
                        _loggedIn[session] = login.Slot;
                        _logins[login.Slot] = login;
                    }
                }
                if (previous != null)
                {
                    foreach (var old in previous) Async.Fire(CloseAsync(old), "closing a slot's previous connection", tellUser: false);
                    Dictionary<string, string> snapshot;
                    lock (checksums) snapshot = new Dictionary<string, string>(checksums, StringComparer.OrdinalIgnoreCase);
                    return new ConnectResult(ConnectOutcome.Connected, "Connected.", new ConnectedSlot(login.Slot, session, success, snapshot, early));
                }
                // Atlas started closing (or closed this session, or its multiworld was deleted) while it logged in.
                early.Take();
                await CloseAsync(session).ConfigureAwait(false);
                return new ConnectResult(ConnectOutcome.Cancelled, why);
            }

            early.Take();
            await CloseAsync(session).ConfigureAwait(false);
            var failure = (LoginFailure)result;
            string errors = string.Join(", ", failure.Errors ?? Array.Empty<string>());
            // Only a refusal (wrong slot, game, version, password) is final; a server that couldn't be reached may come back.
            bool refused = (failure.ErrorCodes ?? Array.Empty<ConnectionRefusedError>()).Any(code => code != ConnectionRefusedError.UnknownError);
            return new ConnectResult(refused ? ConnectOutcome.Refused : ConnectOutcome.Unreachable, errors.Length > 0 ? errors : "The connection failed.");
        }

        /// <summary>Connects the socket and logs in; a connection that can't be made becomes a failed login.</summary>
        private static async Task<LoginResult> TryLogInAsync(ArchipelagoSession session, SlotLogin login)
        {
            try
            {
                await session.ConnectAsync().ConfigureAwait(false);
                return await session.LoginAsync("", login.Slot.SlotName, ItemsHandlingFlags.AllItems, new Version(0, 5, 0),
                    // "Tracker": the server treats it like TextOnly (can't send checks) and announces Atlas as "tracking".
                    new[] { "Tracker" }, null, string.IsNullOrEmpty(login.Password) ? null : login.Password, true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Can't reach the server, a refused or broken connection, a bad address: the caller reports it.
                return new LoginFailure(ex.GetBaseException().Message);
            }
        }

        private async Task CloseWhenDoneAsync(Task<LoginResult> attempt, ArchipelagoSession session)
        {
            LoginResult late = await attempt.ConfigureAwait(false);
            if (!late.Successful) return;
            lock (_lock) _open.Add(session); // so CloseAsync closes it (it was forgotten when the attempt timed out)
            await CloseAsync(session).ConfigureAwait(false);
            Logger.LogDebug("A login that finished after its time limit was closed.");
        }

        // =====================================================================
        // Closing and drops
        // =====================================================================

        /// <summary>
        /// Forgets a session, then closes its socket with a close frame. Safe to call more than once, and on a socket that
        /// never opened or already broke (there's nothing to close then, which isn't a failure).
        /// </summary>
        private async Task CloseAsync(ArchipelagoSession session)
        {
            lock (_lock)
            {
                _loggedIn.Remove(session);
                if (!_open.Remove(session)) return;
            }
            try
            {
                var closing = session.Socket.DisconnectAsync();
                if (closing != null) await closing.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException
                                           or System.Net.WebSockets.WebSocketException or System.IO.IOException)
            {
                Logger.LogDebug("A server connection had nothing left to close: " + ex.Message);
            }
        }

        private void OnSocketClosed(ArchipelagoSession session, string reason)
        {
            SlotId slot;
            lock (_lock)
            {
                // Atlas forgets a session before closing it on purpose, so one still logged in here dropped by itself.
                if (!_loggedIn.Remove(session, out slot)) return;
                _open.Remove(session);
            }
            OnDropped(slot, string.IsNullOrWhiteSpace(reason) ? "the server closed the connection" : reason);
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

    /// <summary>Keeps the messages a session receives until the slot's window takes over.</summary>
    internal sealed class EarlyMessages
    {
        private readonly ArchipelagoSession _session;
        private readonly List<LogMessage> _messages = new();
        private bool _taken;

        public EarlyMessages(ArchipelagoSession session)
        {
            _session = session;
            session.MessageLog.OnMessageReceived += OnMessage;
        }

        private void OnMessage(LogMessage message)
        {
            lock (_messages)
                if (!_taken) _messages.Add(message);
        }

        public int Count
        {
            get { lock (_messages) return _messages.Count; }
        }

        /// <summary>Stops keeping messages and returns those kept. Later calls return nothing.</summary>
        public IReadOnlyList<LogMessage> Take()
        {
            _session.MessageLog.OnMessageReceived -= OnMessage;
            lock (_messages)
            {
                if (_taken) return Array.Empty<LogMessage>();
                _taken = true;
                return _messages.ToList();
            }
        }
    }
}
