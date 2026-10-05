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
/// misbehave on purpose: stay silent, answer slowly, refuse connections, or drop every client without a close.
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

    /// <summary>The slots that may log in (slot numbers follow this order), and the game they all play.</summary>
    public List<string> Slots { get; } = new() { "Tester" };
    public string SlotGame { get; set; } = "Test Game";

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
        foreach (var client in clients)
        {
            try
            {
                client.Client.LingerState = new LingerOption(true, 0); // reset, not a graceful close
                client.Close();
            }
            catch (ObjectDisposedException) { }
        }
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
                if (!await HandshakeAsync(stream, RefuseConnections, _stop.Token)) return;
                using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
                using var sending = new SemaphoreSlim(1, 1);
                lock (_sockets) _sockets.Add((socket, sending));
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
                        var reply = await AnswerAsync(packet);
                        if (reply != null) await SendAsync(socket, sending, reply);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { } // the client or the test ended it
            finally
            {
                lock (_connected) _connected.Remove(client);
                lock (_sockets) _sockets.RemoveAll(entry => entry.Socket.State != WebSocketState.Open);
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

    private static async Task SendAsync(WebSocket socket, SemaphoreSlim sending, JArray packets)
    {
        await sending.WaitAsync();
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(packets.ToString(Newtonsoft.Json.Formatting.None)), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { sending.Release(); }
    }

    /// <summary>Sends packets, as one message, to every connected client (as a server does when items arrive or someone talks).</summary>
    public async Task BroadcastAsync(params JObject[] packets)
    {
        List<(WebSocket Socket, SemaphoreSlim Sending)> sockets;
        lock (_sockets) sockets = _sockets.Where(entry => entry.Socket.State == WebSocketState.Open).ToList();
        foreach (var (socket, sending) in sockets)
        {
            try { await SendAsync(socket, sending, new JArray(packets)); }
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

    /// <summary>A RoomUpdate packet: the slot has checked these locations (as when its player checks them in the game).</summary>
    public static JObject LocationsChecked(params long[] locations) => new()
    {
        ["cmd"] = "RoomUpdate",
        ["checked_locations"] = new JArray(locations)
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
            case "Connect":
                string name = (string?)packet["name"] ?? "";
                Note("Connect " + name);
                if (LoginDelay > TimeSpan.Zero) await Task.Delay(LoginDelay, _stop.Token);
                int index = Slots.IndexOf(name);
                Note("Connected " + name);
                if (index < 0) return new JArray(new JObject { ["cmd"] = "ConnectionRefused", ["errors"] = new JArray("InvalidSlot") });
                // As a real server does: the login, the slot's items so far, and the room's join message.
                return new JArray(Connected(index + 1, name), new JObject { ["cmd"] = "ReceivedItems", ["index"] = 0, ["items"] = new JArray() },
                    new JObject
                    {
                        ["cmd"] = "PrintJSON",
                        ["type"] = "Join",
                        ["team"] = 0,
                        ["slot"] = index + 1,
                        ["tags"] = new JArray("Tracker"),
                        ["data"] = new JArray(new JObject { ["text"] = $"{name} (Team #1) tracking {SlotGame} has joined." })
                    });
            default:
                return null;
        }
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

    private JObject Connected(int slot, string name) => new()
    {
        ["cmd"] = "Connected",
        ["team"] = 0,
        ["slot"] = slot,
        ["players"] = new JArray(Slots.Select((s, i) => new JObject { ["team"] = 0, ["slot"] = i + 1, ["alias"] = s, ["name"] = s, ["class"] = "NetworkPlayer" })),
        ["missing_locations"] = new JArray(Games.TryGetValue(SlotGame, out var game) ? game.Locations.Values.Cast<object>().ToArray() : Array.Empty<object>()),
        ["checked_locations"] = new JArray(),
        ["slot_data"] = new JObject { ["slot_name"] = name },
        ["slot_info"] = new JObject(Slots.Select((s, i) => new JProperty((i + 1).ToString(),
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
