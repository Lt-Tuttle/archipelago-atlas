using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>One game in the fake server's room: its data package checksum and names.</summary>
public sealed record FakeGame(string Checksum, Dictionary<string, long> Items, Dictionary<string, long> Locations);

/// <summary>
/// A small Archipelago server for connection tests, on this computer only (loopback): it greets each client with the
/// room's info, answers data package requests and logins as a real server does, and records what clients send. Nothing
/// leaves the computer, and nothing reaches a real server.
/// </summary>
public sealed class FakeArchipelagoServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _clients = new();
    private readonly List<JObject> _received = new();
    private readonly Task _accepting;

    public FakeArchipelagoServer()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public Uri Url => new($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    /// <summary>The room's games by name.</summary>
    public Dictionary<string, FakeGame> Games { get; } = new();

    /// <summary>The one slot that may log in, and its game.</summary>
    public string SlotName { get; set; } = "Tester";
    public string SlotGame { get; set; } = "Test Game";

    /// <summary>Every packet clients have sent so far ("GetDataPackage", "Connect", …), in order.</summary>
    public List<JObject> Received
    {
        get { lock (_received) return _received.ToList(); }
    }

    /// <summary>How many packets of this command clients have sent.</summary>
    public int Count(string command) => Received.Count(p => (string?)p["cmd"] == command);

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            lock (_clients) _clients.Add(ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            if (!await HandshakeAsync(stream, _stop.Token)) return;
            using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            try
            {
                await SendAsync(socket, new JArray(RoomInfo()));
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    var packets = JArray.Parse(Encoding.UTF8.GetString(message.ToArray())).OfType<JObject>().ToList();
                    message.SetLength(0);
                    foreach (var packet in packets)
                    {
                        lock (_received) _received.Add(packet);
                        var reply = Answer(packet);
                        if (reply != null) await SendAsync(socket, reply);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { } // the client or the test ended it
        }
    }

    /// <summary>The websocket opening handshake (RFC 6455). No compression is offered, so the client sends plain text.</summary>
    private static async Task<bool> HandshakeAsync(NetworkStream stream, CancellationToken ct)
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
        // The protocol itself fixes SHA-1 here.
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), ct);
        return true;
    }

    private static Task SendAsync(WebSocket socket, JArray packets) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(packets.ToString(Newtonsoft.Json.Formatting.None)), WebSocketMessageType.Text, true, CancellationToken.None);

    private JArray? Answer(JObject packet)
    {
        switch ((string?)packet["cmd"])
        {
            case "GetDataPackage":
                var wanted = packet["games"]?.ToObject<string[]>() ?? Games.Keys.ToArray();
                var games = new JObject();
                foreach (string name in wanted)
                    if (Games.TryGetValue(name, out var game))
                        games[name] = new JObject
                        {
                            ["item_name_to_id"] = JObject.FromObject(game.Items),
                            ["location_name_to_id"] = JObject.FromObject(game.Locations),
                            ["checksum"] = game.Checksum
                        };
                return new JArray(new JObject { ["cmd"] = "DataPackage", ["data"] = new JObject { ["games"] = games } });
            case "Connect":
                if ((string?)packet["name"] != SlotName)
                    return new JArray(new JObject { ["cmd"] = "ConnectionRefused", ["errors"] = new JArray("InvalidSlot") });
                return new JArray(Connected(), new JObject { ["cmd"] = "ReceivedItems", ["index"] = 0, ["items"] = new JArray() });
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

    private JObject Connected() => new()
    {
        ["cmd"] = "Connected",
        ["team"] = 0,
        ["slot"] = 1,
        ["players"] = new JArray(new JObject { ["team"] = 0, ["slot"] = 1, ["alias"] = SlotName, ["name"] = SlotName, ["class"] = "NetworkPlayer" }),
        ["missing_locations"] = new JArray(Games.TryGetValue(SlotGame, out var game) ? game.Locations.Values.Cast<object>().ToArray() : Array.Empty<object>()),
        ["checked_locations"] = new JArray(),
        ["slot_data"] = new JObject(),
        ["slot_info"] = new JObject
        {
            ["1"] = new JObject { ["name"] = SlotName, ["game"] = SlotGame, ["type"] = 1, ["group_members"] = new JArray(), ["class"] = "NetworkSlot" }
        },
        ["hint_points"] = 0
    };

    private static JObject Version() => new() { ["major"] = 0, ["minor"] = 6, ["build"] = 7, ["class"] = "Version" };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        Task[] running;
        lock (_clients) running = _clients.Append(_accepting).ToArray();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(5)));
        _stop.Dispose();
    }
}
