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
    /// <summary>
    /// The room's text. A server sends the room's lines (every item found, every chat line) to every connection, so with
    /// many slots connected Atlas would receive, and decode, each line once per slot. Instead, one connection per
    /// multiworld team, its text connection, receives them; the team's other connections log in with Archipelago's NoText
    /// tag and the server sends them no text. Every slot's text client still shows every line:
    /// <list type="bullet">
    /// <item>The room's lines (items found, chat, joins, goals) reach every slot of the team from its text connection.</item>
    /// <item>A line the server sends one connection alone (a command's answer, a hint's line, the tutorial after a
    /// login) stays with that connection's slot. A command is sent only once its slot's connection receives text, so its
    /// answer arrives (<see cref="SayAsync"/>). A slot without text shows its new hints from its hint list instead.</item>
    /// <item>When the text connection ends, another of the team's connections takes over: one that receives text
    /// already, or one that's switched on. The server tells the whole room when a connection's tags change, so text is
    /// switched on only when needed: not when a slot replaces its own text connection, while Atlas closes, or for a
    /// deleted multiworld.</item>
    /// <item>A slot that logs in without text shows the room's lines from its own join line on, as its own connection
    /// would with text. The server sends lines in order, so the text connection's lines before it were the room's before
    /// the slot joined. The join line may reach Atlas while the slot logs in, or just after: the room's lines are held
    /// until it passes. If it doesn't come in time (a server that doesn't announce joins), or the text connection
    /// changes hands meanwhile, the held lines are shown with the next one. The join line surely comes only through a
    /// text connection whose text the server had confirmed before the login began: through one switched on meanwhile, it
    /// may not (the server may have let the slot in first), so the slot shows the lines from while it logged in.</item>
    /// </list>
    /// </summary>
    public sealed partial class SessionManager
    {
        // "Tracker": the server treats it like TextOnly (can't send checks) and announces Atlas as "tracking".
        private static readonly string[] TextTags = { "Tracker" };
        private static readonly string[] QuietTags = { "Tracker", "NoText" };

        // Sessions that receive text: logged in with it, or switched on since.
        private readonly HashSet<ArchipelagoSession> _textEnabled = new();
        // Each multiworld team's text connection, whose room lines go to every slot of the team. Always a logged-in session.
        private readonly Dictionary<(string ProfileId, int Team), ArchipelagoSession> _textSessions = new();
        // Each slot's team, from its last login (a slot's first login can't know it).
        private readonly Dictionary<SlotId, int> _teams = new();
        // Connections the server surely sends text: logged in with it, or a line has come through them since.
        private readonly HashSet<ArchipelagoSession> _textConfirmed = new();
        // What reached Atlas while a slot logged in without text, by multiworld (one login at a time).
        private readonly Dictionary<string, Replay> _replays = new();
        // Slots that logged in without text and wait for their own join line, by connection: their room lines are held meanwhile.
        private readonly Dictionary<ArchipelagoSession, JoinWait> _joinWaits = new();
        private int _textSwitchOns;

        /// <summary>
        /// How many times Atlas switched a connection's text on (for tests: a tag change sent just before its connection
        /// closes may never reach the server, so they count what Atlas sent).
        /// </summary>
        internal int TextSwitchOns => Volatile.Read(ref _textSwitchOns);

        /// <summary>
        /// Under _lock: whether a slot logs in with text. It does when its team has no text connection yet, or that
        /// connection is the slot's own (being replaced). A slot whose team isn't known yet (its first login) gets text
        /// only when its multiworld has no text connection at all; if its team turns out to have none, it's switched on
        /// once logged in.
        /// </summary>
        private bool WantsText(SlotId slot)
        {
            if (_teams.TryGetValue(slot, out int team))
                return !_textSessions.TryGetValue((slot.ProfileId, team), out var current) ||
                       (_loggedIn.TryGetValue(current, out var owner) && owner.Slot == slot);
            return !_textSessions.Keys.Any(key => key.ProfileId == slot.ProfileId);
        }

        /// <summary>Under _lock: a slot of this multiworld starts logging in without text.</summary>
        private void StartReplay(string profileId) =>
            _replays[profileId] = new Replay(_textSessions.Where(entry => entry.Key.ProfileId == profileId && _textConfirmed.Contains(entry.Value))
                .Select(entry => entry.Value).ToHashSet());

        /// <summary>
        /// Under _lock: a slot finished logging in, replacing <paramref name="previous"/> (its earlier connections, already
        /// forgotten). Sets its part in the room's text, and returns the connections to switch text on for (outside the lock).
        /// </summary>
        private List<ConnectedSlot> StartText(ArchipelagoSession session, ConnectedSlot slot, bool text, List<ArchipelagoSession> previous)
        {
            var switchOn = new List<ConnectedSlot>();
            _teams[slot.Slot] = slot.Team;
            if (text)
            {
                _textEnabled.Add(session);
                _textConfirmed.Add(session);
            }
            // An earlier connection of the slot hands its part over (to this one, when it receives text).
            foreach (var old in previous)
                if (EndText(old) is { } next) switchOn.Add(next);
            var team = (slot.Slot.ProfileId, slot.Team);
            if (!_textSessions.ContainsKey(team))
            {
                // The first of its team (or the team lost its text connection meanwhile): this one takes the part.
                _textSessions[team] = session;
                if (_textEnabled.Add(session)) switchOn.Add(slot);
            }
            // Logged in without text: the room's lines start at the slot's own join line. If it reached Atlas while the
            // slot logged in, the lines from it on come first; lines routed from here on reach the slot after these (they're
            // added under the same lock that routes lines). If not, the room's lines are held until it passes (Route), when
            // it surely comes: through a text connection the server had confirmed before the login began.
            if (!text && _replays.TryGetValue(slot.Slot.ProfileId, out var replay))
            {
                var lines = replay.Lines.Where(line => line.Team == slot.Team).Select(line => line.Message).ToList();
                int joined = lines.FindLastIndex(line => IsOwnJoin(slot, line));
                var current = _textSessions[team];
                if (joined >= 0) slot.Preload(lines.GetRange(joined, lines.Count - joined));
                else if (current != session && replay.Confirmed.Contains(current))
                    _joinWaits[session] = new JoinWait(lines, Environment.TickCount64 + (long)_options.JoinLineWait.TotalMilliseconds);
                // Atlas can't tell which of these came before the slot joined: it shows rather than drops.
                else slot.Preload(lines);
            }
            return switchOn;
        }

        /// <summary>
        /// Under _lock: a connection ended, or was replaced. When it was its team's text connection, another of the team's
        /// open connections takes over: one that receives text already if there is one, else one to switch on (returned,
        /// to switch on outside the lock). Nothing takes over while Atlas closes or for a deleted multiworld.
        /// </summary>
        private ConnectedSlot? EndText(ArchipelagoSession session)
        {
            _textEnabled.Remove(session);
            _textConfirmed.Remove(session);
            _joinWaits.Remove(session);
            (string ProfileId, int Team)? part = null;
            foreach (var (team, text) in _textSessions)
            {
                if (text != session) continue;
                part = team;
                break;
            }
            if (part is not { } key) return null;
            _textSessions.Remove(key);
            // A slot waiting for its join line from this connection won't get it from another: what it held shows with the next line.
            foreach (var (waiting, wait) in _joinWaits)
                if (_loggedIn.TryGetValue(waiting, out var owner) && owner.Slot.ProfileId == key.ProfileId && owner.Team == key.Team) wait.Until = 0;
            if (_closing || _forgottenProfiles.Contains(key.ProfileId)) return null;
            var candidates = _loggedIn.Where(entry => entry.Value.Slot.ProfileId == key.ProfileId && entry.Value.Team == key.Team && IsOpen(entry.Key)).ToList();
            if (candidates.Count == 0) return null;
            int pick = candidates.FindIndex(entry => _textEnabled.Contains(entry.Key));
            var (next, slot) = candidates[pick < 0 ? 0 : pick];
            _textSessions[key] = next;
            return _textEnabled.Add(next) ? slot : null;
        }

        /// <summary>Switches these connections' text on. The server tells the room each one's tags changed.</summary>
        private void SwitchOnText(IEnumerable<ConnectedSlot> slots)
        {
            foreach (var slot in slots)
            {
                Interlocked.Increment(ref _textSwitchOns);
                Logger.LogDebug($"[{slot.Slot.SlotName}] Now receives the room's text for its multiworld.");
                Async.Fire(SendTagsAsync(slot.Session, TextTags), $"switching on {slot.Slot.SlotName}'s text", tellUser: false);
            }
        }

        // The packet the library's UpdateConnectionOptions sends (with the login's item handling, so only the tags change).
        // That method blocks a thread until the packet is sent, forever if the connection dies first; this doesn't.
        private static Task SendTagsAsync(ArchipelagoSession session, string[] tags) =>
            session.Socket.SendPacketAsync(new ConnectUpdatePacket { Tags = tags, ItemsHandling = ItemsHandlingFlags.AllItems });

        /// <summary>
        /// A line a connection received (network thread). From its team's text connection, a room line goes to every slot
        /// of the team; a line meant for one connection goes to that connection's slot only; any other connection's copy
        /// of a room line is dropped (the text connection has it too). Before a connection has logged in, and after it's
        /// replaced or closed, its lines go to its own slot's inbox.
        /// </summary>
        private void OnText(ArchipelagoSession session, SlotInbox own, LogMessage message)
        {
            List<(ConnectedSlot Slot, LogMessage Line)>? to = null;
            lock (_lock)
            {
                if (_loggedIn.TryGetValue(session, out var from))
                {
                    // The server sends a connection without text no line at all.
                    _textConfirmed.Add(session);
                    to = new List<(ConnectedSlot, LogMessage)>();
                    if (ForItsSlotOnly(message)) to.Add((from, message));
                    else if (_textSessions.TryGetValue((from.Slot.ProfileId, from.Team), out var text) && text == session)
                    {
                        foreach (var slot in _loggedIn.Values)
                            if (slot.Team == from.Team && slot.Slot.ProfileId == from.Slot.ProfileId) Route(slot, message, to);
                        if (_replays.TryGetValue(from.Slot.ProfileId, out var replay)) replay.Lines.Add((from.Team, message));
                    }
                    else return;
                }
            }
            if (to == null) own.Deliver(message);
            else foreach (var (slot, line) in to) slot.Deliver(line);
        }

        /// <summary>
        /// Under _lock: a room line for one slot of the team. A slot waiting for its own join line (StartText) gets no line
        /// before it: those are held, and dropped once it arrives. If it hasn't come in time, or the slot's text connection
        /// changed hands meanwhile, the held lines go first, then this one: when Atlas can't tell, it shows rather than drops.
        /// </summary>
        private void Route(ConnectedSlot slot, LogMessage message, List<(ConnectedSlot Slot, LogMessage Line)> to)
        {
            if (_joinWaits.TryGetValue(slot.Session, out var wait))
            {
                if (IsOwnJoin(slot, message)) _joinWaits.Remove(slot.Session);
                else if (Environment.TickCount64 < wait.Until)
                {
                    wait.Held.Add(message);
                    return;
                }
                else
                {
                    _joinWaits.Remove(slot.Session);
                    foreach (var held in wait.Held) to.Add((slot, held));
                }
            }
            to.Add((slot, message));
        }

        /// <summary>Whether a line is the slot's own join line: its slot and team, logged in as Atlas logs in without text.</summary>
        private static bool IsOwnJoin(ConnectedSlot slot, LogMessage message) =>
            message is JoinLogMessage join && join.Player?.Slot == slot.Login.Slot && join.Player.Team == slot.Team &&
            join.Tags != null && join.Tags.ToHashSet().SetEquals(QuietTags);

        /// <summary>
        /// A slot logging in without text: the multiworld's text connections whose text the server had confirmed when it
        /// began (its join line surely comes through them), and the room's lines that reach Atlas meanwhile, with their team.
        /// </summary>
        private sealed class Replay
        {
            public Replay(HashSet<ArchipelagoSession> confirmed) => Confirmed = confirmed;

            public HashSet<ArchipelagoSession> Confirmed { get; }

            public List<(int Team, LogMessage Message)> Lines { get; } = new();
        }

        /// <summary>A slot waiting for its own join line: the room's lines held meanwhile, and until when it waits.</summary>
        private sealed class JoinWait
        {
            public JoinWait(List<LogMessage> held, long until)
            {
                Held = held;
                Until = until;
            }

            public List<LogMessage> Held { get; }

            /// <summary>Environment.TickCount64 when the wait ends: 0 once the slot's text connection changed hands.</summary>
            public long Until { get; set; }
        }

        /// <summary>
        /// Lines the server sends one connection, not the room: a command's answer, a hint's line (to each of the hint's
        /// two slots), the tutorial after a login.
        /// </summary>
        private static bool ForItsSlotOnly(LogMessage message) =>
            message is HintItemSendLogMessage or CommandResultLogMessage or AdminCommandResultLogMessage or TutorialLogMessage;

        /// <summary>Whether a connection receives text (its team's text connection, or one a command was sent from).</summary>
        internal bool ReceivesText(ArchipelagoSession session)
        {
            lock (_lock) return _textEnabled.Contains(session);
        }

        /// <summary>
        /// Says something as a slot. A command (as the server reads one: starting with "!") is answered only to the
        /// connection that sent it, and only if it receives text, so that's switched on first; it stays on.
        /// </summary>
        internal async Task SayAsync(ArchipelagoSession session, string text)
        {
            if (text.StartsWith('!'))
            {
                ConnectedSlot? switchOn = null;
                lock (_lock)
                    if (_loggedIn.TryGetValue(session, out var slot) && _textEnabled.Add(session)) switchOn = slot;
                if (switchOn != null)
                {
                    Interlocked.Increment(ref _textSwitchOns);
                    Logger.LogDebug($"[{switchOn.Slot.SlotName}] Now receives text, for its commands' answers.");
                    // Packets on one connection are read in order: the server switches it on before it reads the command.
                    await SendTagsAsync(session, TextTags).ConfigureAwait(false);
                }
            }
            await session.Socket.SendPacketAsync(new SayPacket { Text = text }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A slot's text client lines, as <see cref="SessionManager"/> routes them: kept until the slot's window takes over,
    /// then passed straight on. Thread-safe; nothing is called while its lock is held.
    /// </summary>
    internal sealed class SlotInbox
    {
        /// <summary>
        /// The most lines kept until the window takes over (it does so moments after the login): a server's flood meanwhile
        /// keeps the newest.
        /// </summary>
        internal const int MaxEarly = 2_000;

        private readonly object _lock = new();
        private readonly List<LogMessage> _early = new();
        private Action<LogMessage>? _receive;
        private bool _closed;

        public int Count
        {
            get { lock (_lock) return _early.Count; }
        }

        public void Deliver(LogMessage message)
        {
            Action<LogMessage>? receive;
            lock (_lock)
            {
                if (_closed) return;
                receive = _receive;
                if (receive == null)
                {
                    _early.Add(message);
                    if (_early.Count > MaxEarly) _early.RemoveRange(0, _early.Count - MaxEarly);
                    return;
                }
            }
            receive(message);
        }

        /// <summary>Adds lines that arrived before (for a slot logging in without text); only before the window takes over.</summary>
        public void Preload(IReadOnlyCollection<LogMessage> messages)
        {
            lock (_lock)
            {
                if (_closed || _receive != null) return;
                _early.AddRange(messages);
                if (_early.Count > MaxEarly) _early.RemoveRange(0, _early.Count - MaxEarly);
            }
        }

        /// <summary>Returns the lines kept so far, and passes each later one to <paramref name="receive"/>. Only the first call takes over.</summary>
        public IReadOnlyList<LogMessage> TakeOver(Action<LogMessage> receive)
        {
            lock (_lock)
            {
                if (_closed || _receive != null) return Array.Empty<LogMessage>();
                _receive = receive;
                var early = _early.ToList();
                _early.Clear();
                return early;
            }
        }

        /// <summary>Drops what's kept, and passes nothing on from now (the slot ended, or never logged in).</summary>
        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
                _receive = null;
                _early.Clear();
            }
        }
    }
}
