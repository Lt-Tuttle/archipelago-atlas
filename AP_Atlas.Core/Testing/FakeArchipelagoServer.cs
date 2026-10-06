using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Testing;

/// <summary>One game in the fake server's room: its data package checksum and names.</summary>
internal sealed record FakeGame(string Checksum, Dictionary<string, long> Items, Dictionary<string, long> Locations);

/// <summary>
/// A small Archipelago server for tests, on this computer only (loopback): it greets each client with the room's info,
/// answers data package requests and logins as a real server does, and records what clients send. It can also
/// misbehave on purpose: stay silent, answer slowly, refuse connections, drop clients without a close, or close one
/// properly as a room that shuts down.
/// Nothing leaves the computer, and nothing reaches a real server. Used by the unit tests and by Atlas's UI test
/// (ATLAS_UITEST); Atlas itself never starts it.
/// </summary>
internal sealed class FakeArchipelagoServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _serving = new();
    private readonly List<TcpClient> _connected = new();
    // Open websockets, each with a lock: a socket can't send two messages at once (a reply and a broadcast).
    private readonly List<(WebSocket Socket, SemaphoreSlim Sending)> _sockets = new();
    // Each websocket's connection, to cut one off (under _sockets' lock).
    private readonly Dictionary<WebSocket, TcpClient> _tcp = new();
    // The data storage keys each client asked to be told about (SetNotify).
    private readonly Dictionary<WebSocket, HashSet<string>> _notify = new();
    // Each logged-in client's team, slot and tags (from its login and any ConnectUpdate since).
    private readonly Dictionary<WebSocket, (int Team, int Slot, string[] Tags)> _clients = new();
    private int _tagChanges;
    private readonly List<JObject> _received = new();
    private readonly List<(TimeSpan At, string What)> _timeline = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _accepting;
    private int _accepted, _closesReceived, _ended;

    public FakeArchipelagoServer()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public Uri Url => new($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    /// <summary>The room's games by name.</summary>
    public Dictionary<string, FakeGame> Games { get; } = new();

    /// <summary>
    /// The server's data storage, as clients read it with Get. Archipelago's read-only keys answer as on a fresh room when
    /// not set here: no race mode, every client status unknown, no hints.
    /// </summary>
    public Dictionary<string, JToken> DataStorage { get; } = new();

    /// <summary>The slots that may log in (slot numbers follow this order), and the game they all play.</summary>
    public List<string> Slots { get; } = new() { "Tester" };
    public string SlotGame { get; set; } = "Test Game";

    /// <summary>
    /// Slots on a team other than the first (teams count from 0), by name; every other slot is on team 0. Each team
    /// numbers its own slots from 1, in the order of <see cref="Slots"/>.
    /// </summary>
    public Dictionary<string, int> Teams { get; } = new();

    /// <summary>The first slot's name.</summary>
    public string SlotName
    {
        get => Slots[0];
        set => Slots[0] = value;
    }

    /// <summary>Accept connections but never send the room's info (a server that hangs).</summary>
    public bool Silent { get; set; }

    /// <summary>How long to wait before answering a login.</summary>
    public TimeSpan LoginDelay { get; set; }

    /// <summary>How long to wait before answering a new connection's websocket handshake (a slow server).</summary>
    public TimeSpan HandshakeDelay { get; set; }

    /// <summary>
    /// Tell the room a client joined this long before answering its login (0: just after, as a real server does). The
    /// room's lines and a login travel on different connections, so a client may see them in either order; this makes
    /// the order a test needs certain. The joining client itself doesn't hear its join then.
    /// </summary>
    public TimeSpan JoinLead { get; set; }

    /// <summary>
    /// Answer every new connection "503 Service Unavailable" instead of opening a websocket (a server that's down behind its
    /// gateway). A definitive answer, so the client's network stack doesn't quietly try again on a new connection.
    /// </summary>
    public bool RefuseConnections { get; set; }

    /// <summary>Every packet clients have sent so far ("GetDataPackage", "Connect", …), in order.</summary>
    public List<JObject> Received
    {
        get { lock (_received) return _received.ToList(); }
    }

    /// <summary>How many packets of this command clients have sent.</summary>
    public int Count(string command) => Received.Count(p => (string?)p["cmd"] == command);

    /// <summary>How many connections were made to the server (including refused ones).</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>How many clients closed their connection properly (with a close frame).</summary>
    public int ClosesReceived => Volatile.Read(ref _closesReceived);

    /// <summary>How many connections have ended, however they ended.</summary>
    public int Ended => Volatile.Read(ref _ended);

    /// <summary>How many open, logged-in connections receive the room's text (those without the NoText tag).</summary>
    public int TextClients
    {
        get
        {
            lock (_sockets)
                lock (_clients)
                    return _sockets.Count(entry => entry.Socket.State == WebSocketState.Open && _clients.TryGetValue(entry.Socket, out var client) && !client.Tags.Contains("NoText"));
        }
    }

    /// <summary>How many times a client changed its tags after logging in (a real server tells the whole room each time).</summary>
    public int TagChanges => Volatile.Read(ref _tagChanges);

    /// <summary>The tags a slot's open connection has now (null when it has none).</summary>
    public string[]? TagsOf(string slotName)
    {
        lock (_sockets)
            lock (_clients)
                return _clients.Where(client => client.Key.State == WebSocketState.Open && NameOf(client.Value.Team, client.Value.Slot) == slotName)
                    .Select(client => client.Value.Tags).FirstOrDefault();
    }

    /// <summary>When each login arrived ("Connect") and was answered ("Connected"), in order.</summary>
    public List<(TimeSpan At, string What)> Timeline
    {
        get { lock (_timeline) return _timeline.ToList(); }
    }

    /// <summary>Cuts every client off without a close frame, as a server that crashed or lost its network would.</summary>
    public void DropClients()
    {
        List<TcpClient> clients;
        lock (_connected)
        {
            clients = _connected.ToList();
            _connected.Clear();
        }
        foreach (var client in clients) Reset(client);
    }

    /// <summary>Cuts one slot's connections off without a close frame, as when that one client crashed or lost its network.</summary>
    public void DropClient(string slotName)
    {
        List<TcpClient> clients;
        lock (_sockets)
            lock (_clients)
                clients = _clients.Where(client => NameOf(client.Value.Team, client.Value.Slot) == slotName && _tcp.ContainsKey(client.Key))
                    .Select(client => _tcp[client.Key]).ToList();
        foreach (var client in clients) Reset(client);
    }

    /// <summary>Closes one slot's connections properly (with a close frame), as a server does when its room shuts down.</summary>
    public async Task CloseClientAsync(string slotName)
    {
        List<(WebSocket Socket, SemaphoreSlim Sending)> sockets;
        lock (_sockets)
            lock (_clients)
                sockets = _sockets.Where(entry => _clients.TryGetValue(entry.Socket, out var client) && NameOf(client.Team, client.Slot) == slotName).ToList();
        foreach (var (socket, sending) in sockets)
        {
            await sending.WaitAsync();
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "The room shut down.", CancellationToken.None); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException or InvalidOperationException) { } // already gone
            finally { sending.Release(); }
        }
    }

    private static void Reset(TcpClient client)
    {
        try
        {
            client.Client.LingerState = new LingerOption(true, 0); // reset, not a graceful close
            client.Close();
        }
        catch (ObjectDisposedException) { }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            Interlocked.Increment(ref _accepted);
            lock (_connected) _connected.Add(client);
            lock (_serving) _serving.Add(ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                if (HandshakeDelay > TimeSpan.Zero) await Task.Delay(HandshakeDelay, _stop.Token);
                if (!await HandshakeAsync(stream, RefuseConnections, _stop.Token)) return;
                using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
                using var sending = new SemaphoreSlim(1, 1);
                lock (_sockets)
                {
                    _sockets.Add((socket, sending));
                    _tcp[socket] = client;
                }
                var buffer = new byte[64 * 1024];
                using var message = new MemoryStream();
                if (!Silent) await SendAsync(socket, sending, new JArray(RoomInfo()));
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Interlocked.Increment(ref _closesReceived);
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        return;
                    }
                    await message.WriteAsync(buffer.AsMemory(0, result.Count), _stop.Token);
                    if (!result.EndOfMessage) continue;
                    var packets = JArray.Parse(Encoding.UTF8.GetString(message.ToArray())).OfType<JObject>().ToList();
                    message.SetLength(0);
                    foreach (var packet in packets)
                    {
                        lock (_received) _received.Add(packet);
                        string? cmd = (string?)packet["cmd"];
                        if (cmd == "ConnectUpdate" && packet["tags"] is JArray newTags)
                        {
                            await ChangeTagsAsync(socket, newTags.ToObject<string[]>() ?? Array.Empty<string>());
                            continue;
                        }
                        if (cmd == "Say")
                        {
                            await SayAsync(socket, sending, (string?)packet["text"] ?? "");
                            continue;
                        }
                        if ((string?)packet["cmd"] == "SetNotify")
                        {
                            lock (_notify)
                            {
                                if (!_notify.TryGetValue(socket, out var keys)) _notify[socket] = keys = new HashSet<string>();
                                keys.UnionWith(packet["keys"]?.ToObject<string[]>() ?? Array.Empty<string>());
                            }
                        }
                        var reply = await AnswerAsync(packet);
                        bool loggedIn = cmd == "Connect" && (string?)reply?.First?["cmd"] == "Connected";
                        if (!loggedIn)
                        {
                            if (reply != null) await SendAsync(socket, sending, reply);
                            continue;
                        }
                        // As a real server does after a login: it knows the client's team and tags, and tells the room it joined.
                        string name = (string?)packet["name"] ?? "";
                        string[] tags = packet["tags"]?.ToObject<string[]>() ?? Array.Empty<string>();
                        if (JoinLead > TimeSpan.Zero)
                        {
                            await BroadcastAsync(Joined(name, tags));
                            await Task.Delay(JoinLead, _stop.Token);
                        }
                        await SendAsync(socket, sending, reply!);
                        lock (_clients) _clients[socket] = (TeamOf(name), SlotNumber(name), tags);
                        if (JoinLead == TimeSpan.Zero) await BroadcastAsync(Joined(name, tags));
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { } // the client or the test ended it
            finally
            {
                lock (_connected) _connected.Remove(client);
                lock (_sockets)
                {
                    _sockets.RemoveAll(entry => entry.Socket.State != WebSocketState.Open);
                    foreach (var gone in _tcp.Keys.Where(s => s.State != WebSocketState.Open).ToList()) _tcp.Remove(gone);
                }
                lock (_notify) foreach (var gone in _notify.Keys.Where(s => s.State != WebSocketState.Open).ToList()) _notify.Remove(gone);
                lock (_clients) foreach (var gone in _clients.Keys.Where(s => s.State != WebSocketState.Open).ToList()) _clients.Remove(gone);
                Interlocked.Increment(ref _ended);
            }
        }
    }

    /// <summary>
    /// The websocket opening handshake (RFC 6455), or a 503 when refusing. No compression is offered, so the client sends
    /// plain text.
    /// </summary>
    private static async Task<bool> HandshakeAsync(NetworkStream stream, bool refuse, CancellationToken ct)
    {
        var request = new StringBuilder();
        var one = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (request.Length > 16384 || await stream.ReadAsync(one, ct) == 0) return false;
            request.Append((char)one[0]);
        }
        string? key = request.ToString().Split("\r\n").Select(line => line.Split(':', 2))
            .FirstOrDefault(p => p.Length == 2 && p[0].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))?[1].Trim();
        if (key == null) return false;
        if (refuse)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
            return false;
        }
        // The protocol itself fixes SHA-1 here.
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), ct);
        return true;
    }

    private static Task SendAsync(WebSocket socket, SemaphoreSlim sending, JArray packets) =>
        SendAsync(socket, sending, Encoding.UTF8.GetBytes(packets.ToString(Newtonsoft.Json.Formatting.None)));

    private static async Task SendAsync(WebSocket socket, SemaphoreSlim sending, byte[] message)
    {
        await sending.WaitAsync();
        try { await socket.SendAsync(message, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { sending.Release(); }
    }

    /// <summary>
    /// Sends packets, as one message, to every connected client (as a server does when items arrive or someone talks).
    /// Text (PrintJSON only) goes, as on a real server, only to logged-in clients without NoText, on every team.
    /// </summary>
    public Task BroadcastAsync(params JObject[] packets) => SendToAsync(_ => true, packets);

    /// <summary>Sends packets to one team's clients only, as a real server sends item lines (text skips NoText clients).</summary>
    public Task BroadcastToTeamAsync(int team, params JObject[] packets) => SendToAsync(client => client?.Team == team, packets);

    /// <summary>
    /// Sends packets, as one message, to the open connections <paramref name="to"/> accepts (it's given each one's login,
    /// null when it hasn't logged in). Text goes only to logged-in clients without NoText.
    /// </summary>
    private async Task SendToAsync(Func<(int Team, int Slot, string[] Tags)?, bool> to, JObject[] packets)
    {
        bool text = packets.All(packet => (string?)packet["cmd"] == "PrintJSON");
        // Under both locks.
        bool Receives(WebSocket socket)
        {
            if (socket.State != WebSocketState.Open) return false;
            (int Team, int Slot, string[] Tags)? client = _clients.TryGetValue(socket, out var found) ? found : null;
            if (text && (client == null || client.Value.Tags.Contains("NoText"))) return false;
            return to(client);
        }
        List<(WebSocket Socket, SemaphoreSlim Sending)> sockets;
        lock (_sockets)
        {
            lock (_clients) sockets = _sockets.Where(entry => Receives(entry.Socket)).ToList();
        }
        if (sockets.Count == 0) return;
        // Written once for everyone: the fake runs inside the program it tests, so its own work must stay small.
        byte[] message = Encoding.UTF8.GetBytes(new JArray(packets).ToString(Newtonsoft.Json.Formatting.None));
        foreach (var (socket, sending) in sockets)
        {
            try { await SendAsync(socket, sending, message); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException) { } // that client just left
        }
    }

    // Under _clients' lock.
    private bool NoText(WebSocket socket) => _clients.TryGetValue(socket, out var client) && client.Tags.Contains("NoText");

    private int TeamOf(string name) => Teams.TryGetValue(name, out int team) ? team : 0;

    /// <summary>A slot's number on its team (from 1), or 0 for a name that isn't a slot.</summary>
    private int SlotNumber(string name)
    {
        int team = TeamOf(name), number = 0;
        foreach (string slot in Slots)
        {
            if (TeamOf(slot) == team) number++;
            if (slot == name) return number;
        }
        return 0;
    }

    private string? NameOf(int team, int slot) => Slots.Where(name => TeamOf(name) == team).ElementAtOrDefault(slot - 1);

    private JObject Joined(string name, string[] tags) => new()
    {
        ["cmd"] = "PrintJSON",
        ["type"] = "Join",
        ["team"] = TeamOf(name),
        ["slot"] = SlotNumber(name),
        ["tags"] = new JArray(tags),
        ["data"] = new JArray(new JObject { ["text"] = $"{name} (Team #{TeamOf(name) + 1}) tracking {SlotGame} has joined." })
    };

    /// <summary>A list of tags as a real server writes it in its messages (Python's list).</summary>
    private static string PythonList(IEnumerable<string> tags) => "[" + string.Join(", ", tags.Select(tag => $"'{tag}'")) + "]";

    /// <summary>A client changed its tags (ConnectUpdate): as a real server does, the whole room is told.</summary>
    private async Task ChangeTagsAsync(WebSocket socket, string[] tags)
    {
        string[] old;
        int team, slot;
        lock (_clients)
        {
            if (!_clients.TryGetValue(socket, out var client)) return;
            (team, slot, old) = client;
            _clients[socket] = (team, slot, tags);
        }
        if (old.ToHashSet().SetEquals(tags)) return;
        Interlocked.Increment(ref _tagChanges);
        string name = NameOf(team, slot) ?? "Someone";
        Note($"TagsChanged {name}");
        await BroadcastAsync(new JObject
        {
            ["cmd"] = "PrintJSON",
            ["type"] = "TagsChanged",
            ["team"] = team,
            ["slot"] = slot,
            ["tags"] = new JArray(tags),
            ["data"] = new JArray(new JObject { ["text"] = $"{name} (Team #{team + 1}) has changed tags from {PythonList(old)} to {PythonList(tags)}." })
        });
    }

    /// <summary>
    /// A client said something. As on a real server, the room hears it as chat (everything but an !admin command), and a
    /// command's answer goes only to the client that sent it, and only if it receives text.
    /// </summary>
    private async Task SayAsync(WebSocket socket, SemaphoreSlim sending, string text)
    {
        int team, slot;
        bool quiet;
        lock (_clients)
        {
            (team, slot) = _clients.TryGetValue(socket, out var client) ? (client.Team, client.Slot) : (0, 0);
            quiet = NoText(socket);
        }
        if (!text.StartsWith("!admin", StringComparison.Ordinal))
            await BroadcastAsync(new JObject
            {
                ["cmd"] = "PrintJSON",
                ["type"] = "Chat",
                ["team"] = team,
                ["slot"] = slot,
                ["message"] = text,
                ["data"] = new JArray(new JObject { ["text"] = $"{NameOf(team, slot) ?? "Someone"}: {text}" })
            });
        if (text.StartsWith('!') && !quiet)
            await SendAsync(socket, sending, new JArray(new JObject
            {
                ["cmd"] = "PrintJSON",
                ["type"] = "CommandResult",
                ["data"] = new JArray(new JObject { ["text"] = $"Command received: {text}" })
            }));
    }

    /// <summary>
    /// New hints on team 0, as a real server makes them (a player's !hint, say): each joins the hint lists of its finding
    /// and receiving slots, whose listeners are told (once per list), and its line goes to those two slots' clients that
    /// receive text. Hints from <see cref="Hint"/>.
    /// </summary>
    public async Task AddHintsAsync(params JObject[] hints)
    {
        // Lines are written only for slots with a text client: the fake runs inside the program it tests, so its own
        // work must stay small.
        HashSet<int> textSlots;
        lock (_clients) textSlots = _clients.Values.Where(client => client.Team == 0 && !client.Tags.Contains("NoText")).Select(client => client.Slot).ToHashSet();
        var changed = new HashSet<string>();
        var lines = new Dictionary<int, List<JObject>>();
        lock (DataStorage)
        {
            foreach (var hint in hints)
            {
                int finder = (int)hint["finding_player"]!, receiver = (int)hint["receiving_player"]!;
                long location = (long)hint["location"]!;
                foreach (int slot in new[] { receiver, finder }.Distinct())
                {
                    string key = $"_read_hints_0_{slot}";
                    if (!DataStorage.TryGetValue(key, out var stored) || stored is not JArray list) DataStorage[key] = list = new JArray();
                    // A hint already in the other slot's list is copied as it's added.
                    if (!list.Any(h => (int)h["finding_player"]! == finder && (long)h["location"]! == location)) list.Add(hint);
                    changed.Add(key);
                    if (!textSlots.Contains(slot)) continue;
                    if (!lines.TryGetValue(slot, out var mine)) lines[slot] = mine = new List<JObject>();
                    mine.Add(HintLine(hint));
                }
            }
        }
        foreach (string key in changed) await NotifyAsync(key);
        foreach (var (slot, messages) in lines) await SendToAsync(client => client?.Team == 0 && client.Value.Slot == slot, messages.ToArray());
    }

    /// <summary>A hint's line (PrintJSON "Hint"), as a real server writes it.</summary>
    public static JObject HintLine(JObject hint)
    {
        int finder = (int)hint["finding_player"]!, receiver = (int)hint["receiving_player"]!, flags = (int)hint["item_flags"]!, status = (int)hint["status"]!;
        long location = (long)hint["location"]!, item = (long)hint["item"]!;
        string statusText = status switch { 40 => "(found)", 10 => "(no priority)", 20 => "(avoid)", 30 => "(priority)", _ => "(unspecified)" };
        return new JObject
        {
            ["cmd"] = "PrintJSON",
            ["type"] = "Hint",
            ["receiving"] = receiver,
            ["item"] = new JObject { ["item"] = item, ["location"] = location, ["player"] = finder, ["flags"] = flags, ["class"] = "NetworkItem" },
            ["found"] = (bool)hint["found"]!,
            ["data"] = new JArray(
                new JObject { ["text"] = "[Hint]: " },
                new JObject { ["type"] = "player_id", ["text"] = receiver.ToString() },
                new JObject { ["text"] = "'s " },
                new JObject { ["type"] = "item_id", ["text"] = item.ToString(), ["player"] = receiver, ["flags"] = flags },
                new JObject { ["text"] = " is at " },
                new JObject { ["type"] = "location_id", ["text"] = location.ToString(), ["player"] = finder },
                new JObject { ["text"] = " in " },
                new JObject { ["type"] = "player_id", ["text"] = finder.ToString() },
                new JObject { ["text"] = "'s World" },
                new JObject { ["text"] = ". " },
                new JObject { ["type"] = "hint_status", ["hint_status"] = status, ["text"] = statusText })
        };
    }

    /// <summary>
    /// Changes a data storage value and tells the clients that asked to be told about that key (SetNotify), as a real
    /// server does: a slot's hints, say, reach only that slot's clients.
    /// </summary>
    public async Task SetAsync(string key, JToken value)
    {
        lock (DataStorage) DataStorage[key] = value.DeepClone();
        await NotifyAsync(key);
    }

    /// <summary>Tells the clients that asked about a key (SetNotify) its value now, in one message written once.</summary>
    private async Task NotifyAsync(string key)
    {
        List<(WebSocket Socket, SemaphoreSlim Sending)> listeners;
        lock (_sockets)
        {
            lock (_notify)
                listeners = _sockets.Where(entry => entry.Socket.State == WebSocketState.Open && _notify.TryGetValue(entry.Socket, out var keys) && keys.Contains(key)).ToList();
        }
        if (listeners.Count == 0) return;
        string value;
        lock (DataStorage) value = DataStorage[key].ToString(Newtonsoft.Json.Formatting.None);
        // SetReply's packet, written around the stored value instead of copying it into one.
        byte[] message = Encoding.UTF8.GetBytes(
            $"[{{\"cmd\":\"SetReply\",\"key\":{Newtonsoft.Json.JsonConvert.ToString(key)},\"value\":{value},\"original_value\":null,\"slot\":0}}]");
        foreach (var (socket, sending) in listeners)
        {
            try { await SendAsync(socket, sending, message); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException) { } // that client just left
        }
    }

    /// <summary>
    /// A ReceivedItems packet: the slot gets these items (item ids, from location 0, sent by player 0), with these item
    /// flags (1 progression, 2 useful, 4 trap; 0 is filler, and also what an item sent with the server's /send has).
    /// </summary>
    public static JObject ReceivedItems(int index, IEnumerable<long> items, int flags = 0) => new()
    {
        ["cmd"] = "ReceivedItems",
        ["index"] = index,
        ["items"] = new JArray(items.Select(item => new JObject { ["item"] = item, ["location"] = 0, ["player"] = 0, ["flags"] = flags, ["class"] = "NetworkItem" }))
    };

    /// <summary>
    /// A PrintJSON ItemSend line, as the server relays one: <paramref name="finder"/> found <paramref name="item"/> for
    /// <paramref name="receiver"/> at <paramref name="location"/> (slot numbers; item and location ids).
    /// </summary>
    public static JObject ItemSend(int finder, int receiver, long item, long location, int flags = 1) => new()
    {
        ["cmd"] = "PrintJSON",
        ["type"] = "ItemSend",
        ["receiving"] = receiver,
        ["item"] = new JObject { ["item"] = item, ["location"] = location, ["player"] = finder, ["flags"] = flags, ["class"] = "NetworkItem" },
        ["data"] = new JArray(
            new JObject { ["type"] = "player_id", ["text"] = finder.ToString() },
            new JObject { ["text"] = " sent " },
            new JObject { ["type"] = "item_id", ["text"] = item.ToString(), ["player"] = receiver, ["flags"] = flags },
            new JObject { ["text"] = " to " },
            new JObject { ["type"] = "player_id", ["text"] = receiver.ToString() },
            new JObject { ["text"] = " (" },
            new JObject { ["type"] = "location_id", ["text"] = location.ToString(), ["player"] = finder },
            new JObject { ["text"] = ")" })
    };

    /// <summary>A SetReply: a data storage value changed (sent to clients that asked to be told, as for a slot's hints).</summary>
    public static JObject SetReply(string key, JToken value) => new()
    {
        ["cmd"] = "SetReply",
        ["key"] = key,
        ["value"] = value,
        ["original_value"] = JValue.CreateNull(),
        ["slot"] = 0
    };

    /// <summary>
    /// A hint, as the server keeps it in a slot's hint list (`_read_hints_{team}_{slot}`). Its status is Archipelago's
    /// HintStatus: 0 unspecified, 10 no priority, 20 avoid, 30 priority (a found hint's is 40).
    /// </summary>
    public static JObject Hint(int finder, int receiver, long location, long item, bool found = false, int flags = 1, int status = 0) => new()
    {
        ["receiving_player"] = receiver,
        ["finding_player"] = finder,
        ["location"] = location,
        ["item"] = item,
        ["found"] = found,
        ["entrance"] = "",
        ["item_flags"] = flags,
        ["status"] = found ? 40 : status,
        ["class"] = "Hint"
    };

    /// <summary>A RoomUpdate packet: the slot has checked these locations (as when its player checks them in the game).</summary>
    public static JObject LocationsChecked(params long[] locations) => new()
    {
        ["cmd"] = "RoomUpdate",
        ["checked_locations"] = new JArray(locations)
    };

    /// <summary>A slot of team 0 completed its goal, as the server tells the room.</summary>
    public JObject Goal(int slot) => new()
    {
        ["cmd"] = "PrintJSON",
        ["type"] = "Goal",
        ["team"] = 0,
        ["slot"] = slot,
        ["data"] = new JArray(new JObject { ["text"] = $"{NameOf(0, slot)} (Team #1) has completed their goal." })
    };

    /// <summary>A chat line from slot 1, as the server relays it.</summary>
    public JObject Chat(string message) => new()
    {
        ["cmd"] = "PrintJSON",
        ["type"] = "Chat",
        ["team"] = 0,
        ["slot"] = 1,
        ["message"] = message,
        ["data"] = new JArray(new JObject { ["text"] = $"{Slots[0]}: {message}" })
    };

    private void Note(string what)
    {
        lock (_timeline) _timeline.Add((_clock.Elapsed, what));
    }

    private async Task<JArray?> AnswerAsync(JObject packet)
    {
        switch ((string?)packet["cmd"])
        {
            case "GetDataPackage":
                var wanted = packet["games"]?.ToObject<string[]>() ?? Games.Keys.ToArray();
                var games = new JObject();
                foreach (string gameName in wanted)
                    if (Games.TryGetValue(gameName, out var game))
                        games[gameName] = new JObject
                        {
                            ["item_name_to_id"] = JObject.FromObject(game.Items),
                            ["location_name_to_id"] = JObject.FromObject(game.Locations),
                            ["checksum"] = game.Checksum
                        };
                return new JArray(new JObject { ["cmd"] = "DataPackage", ["data"] = new JObject { ["games"] = games } });
            case "Get":
                var keys = packet["keys"]?.ToObject<string[]>() ?? Array.Empty<string>();
                var values = new JObject();
                foreach (string key in keys) values[key] = Stored(key);
                var retrieved = new JObject { ["cmd"] = "Retrieved", ["keys"] = values };
                // As a real server does: the request's other fields come back with the answer.
                foreach (var field in packet.Properties().Where(f => f.Name is not ("cmd" or "keys"))) retrieved[field.Name] = field.Value.DeepClone();
                return new JArray(retrieved);
            case "Connect":
                string name = (string?)packet["name"] ?? "";
                Note("Connect " + name);
                if (LoginDelay > TimeSpan.Zero) await Task.Delay(LoginDelay, _stop.Token);
                Note("Connected " + name);
                if (!Slots.Contains(name)) return new JArray(new JObject { ["cmd"] = "ConnectionRefused", ["errors"] = new JArray("InvalidSlot") });
                // As a real server does: the login and the slot's items so far (the room hears it joined just after).
                return new JArray(Connected(name), new JObject { ["cmd"] = "ReceivedItems", ["index"] = 0, ["items"] = new JArray() });
            default:
                return null;
        }
    }

    private JToken Stored(string key)
    {
        lock (DataStorage)
        {
            if (DataStorage.TryGetValue(key, out var value)) return value.DeepClone();
        }
        if (key == "_read_race_mode") return 0;
        if (key.StartsWith("_read_client_status_", StringComparison.Ordinal)) return 0;
        if (key.StartsWith("_read_hints_", StringComparison.Ordinal)) return new JArray();
        return JValue.CreateNull();
    }

    private JObject RoomInfo() => new()
    {
        ["cmd"] = "RoomInfo",
        ["version"] = Version(),
        ["generator_version"] = Version(),
        ["tags"] = new JArray("AP"),
        ["password"] = false,
        ["permissions"] = new JObject { ["release"] = 2, ["collect"] = 2, ["remaining"] = 2 },
        ["hint_cost"] = 10,
        ["location_check_points"] = 1,
        ["games"] = new JArray(Games.Keys),
        ["datapackage_checksums"] = new JObject(Games.Select(g => new JProperty(g.Key, g.Value.Checksum))),
        ["seed_name"] = "atlas-test-seed",
        ["time"] = 1700000000.0
    };

    private JObject Connected(string name) => new()
    {
        ["cmd"] = "Connected",
        ["team"] = TeamOf(name),
        ["slot"] = SlotNumber(name),
        ["players"] = new JArray(Slots.Select(s => new JObject { ["team"] = TeamOf(s), ["slot"] = SlotNumber(s), ["alias"] = s, ["name"] = s, ["class"] = "NetworkPlayer" })),
        ["missing_locations"] = new JArray(Games.TryGetValue(SlotGame, out var game) ? game.Locations.Values.Cast<object>().ToArray() : Array.Empty<object>()),
        ["checked_locations"] = new JArray(),
        ["slot_data"] = new JObject { ["slot_name"] = name },
        ["slot_info"] = new JObject(Slots.Where(s => TeamOf(s) == TeamOf(name)).Select(s => new JProperty(SlotNumber(s).ToString(),
            new JObject { ["name"] = s, ["game"] = SlotGame, ["type"] = 1, ["group_members"] = new JArray(), ["class"] = "NetworkSlot" }))),
        ["hint_points"] = 0
    };

    private static JObject Version() => new() { ["major"] = 0, ["minor"] = 6, ["build"] = 7, ["class"] = "Version" };

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        DropClients();
        Task[] running;
        lock (_serving) running = _serving.Append(_accepting).ToArray();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(5)));
        _stop.Dispose();
    }
}
