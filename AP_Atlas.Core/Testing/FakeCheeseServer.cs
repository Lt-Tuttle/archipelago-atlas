using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.CheeseTracker;
using Newtonsoft.Json;

namespace AP_Atlas.Core.Testing;

/// <summary>
/// A small stand-in for Cheese Tracker, for tests, on this computer only (loopback). It behaves like the real API for
/// what Atlas uses: reads need no key, status changes are accepted without one, claim changes need the key and the
/// prior owner, and /api/user/self needs the key. It can also fail the next request on purpose. It records every request.
/// The real site is never contacted. Used by the self-test and the UI test; Atlas itself never starts it.
/// </summary>
internal sealed class FakeCheeseServer : IAsyncDisposable
{
    /// <summary>The id of the tracker it serves.</summary>
    public const string TrackerId = "AbCdEfGhIjKlMnOpQrStUw";

    /// <summary>One request it received.</summary>
    public sealed class Request
    {
        public string Method = "", Path = "";
        public string? Auth, Owner, Body;
    }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Request> _requests = new();
    // Requests in progress, kept so none is a forgotten task.
    private readonly List<Task> _serving = new();
    private readonly Task _accepting; // ends when stopped
    private readonly CtTracker _tracker;
    private int _failStatus;
    private string? _failRetryAfter;

    /// <param name="tracker">The tracker it serves (default: a four-slot "Self-test multiworld" of Clique).</param>
    public FakeCheeseServer(CtTracker? tracker = null)
    {
        _tracker = tracker ?? new CtTracker
        {
            Id = 1,
            TrackerId = TrackerId,
            Title = "Self-test multiworld",
            Games = new List<CtGame>
            {
                new CtGame { Id = 11, Position = 1, Name = "Me", Game = "Clique", ClaimedByUserId = 7, OwnerName = "Tester", Availability = "claimed", Progression = "unblocked", Notes = "old", LastChecked = "2026-10-01T10:00:00.123456Z", LastActivity = "2026-10-01T09:00:00Z" },
                new CtGame { Id = 12, Position = 2, Name = "Friend", Game = "Clique", ClaimedByUserId = 9, OwnerName = "Friend", Availability = "claimed", Progression = "unblocked" },
                new CtGame { Id = 13, Position = 3, Name = "Open", Game = "Clique", Availability = "open" },
                new CtGame { Id = 14, Position = 4, Name = "Spare", Game = "Clique", Availability = "open" }
            },
            Hints = new List<CtHint> { new CtHint { Id = 1, FinderGameId = 11, ReceiverGameId = 12, Item = "Button", Location = "The Button" } }
        };
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Site = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _accepting = AcceptAsync();
    }

    /// <summary>Its address, as a Cheese Tracker site: http://127.0.0.1:port.</summary>
    public string Site { get; }

    /// <summary>The API key it accepts (user 7, "Tester").</summary>
    public string ValidKey { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The tracker's page, as a user would paste it.</summary>
    public string TrackerUrl => $"{Site}/tracker/{TrackerId}";

    public List<Request> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public int RequestCount
    {
        get { lock (_requests) return _requests.Count; }
    }

    /// <summary>The next request fails with this HTTP status (and Retry-After, if given).</summary>
    public void FailNext(int status, string? retryAfter)
    {
        lock (_requests)
        {
            _failStatus = status;
            _failRetryAfter = retryAfter;
        }
    }

    /// <summary>Changes every slot's row, as another user's edits on the site would.</summary>
    public void Edit(Action<CtGame> edit)
    {
        lock (_requests) foreach (var g in _tracker.Games) edit(g);
    }

    public CtGame Game(int id)
    {
        lock (_requests) return _tracker.Games.First(g => g.Id == id);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return; // stopped
            }
            lock (_serving)
            {
                _serving.RemoveAll(task => task.IsCompleted);
                _serving.Add(HandleAsync(client));
            }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int headerEnd = -1;
                while (headerEnd < 0)
                {
                    int n = await stream.ReadAsync(chunk, _stop.Token).ConfigureAwait(false);
                    if (n <= 0) return;
                    await buffer.WriteAsync(chunk.AsMemory(0, n), _stop.Token).ConfigureAwait(false);
                    headerEnd = IndexOf(buffer, "\r\n\r\n");
                }
                var lines = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd).Split("\r\n");
                var first = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                }
                int length = headers.TryGetValue("Content-Length", out var cl) ? int.Parse(cl) : 0;
                while (buffer.Length - (headerEnd + 4) < length)
                {
                    int n = await stream.ReadAsync(chunk, _stop.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    await buffer.WriteAsync(chunk.AsMemory(0, n), _stop.Token).ConfigureAwait(false);
                }
                string body = Encoding.UTF8.GetString(buffer.GetBuffer(), headerEnd + 4, (int)Math.Min(length, buffer.Length - (headerEnd + 4)));
                var (status, json, extra) = Respond(first[0], first[1], headers, body);
                byte[] payload = Encoding.UTF8.GetBytes(json ?? "");
                string head = $"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n{extra}\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(payload, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // A test that ended early closes connections mid-request.
        }
    }

    private static int IndexOf(MemoryStream buffer, string marker)
    {
        var bytes = buffer.GetBuffer();
        for (int i = 0; i + marker.Length <= buffer.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < marker.Length && match; j++) match = bytes[i + j] == marker[j];
            if (match) return i;
        }
        return -1;
    }

    private (int Status, string? Json, string ExtraHeaders) Respond(string method, string path, Dictionary<string, string> headers, string body)
    {
        lock (_requests)
        {
            headers.TryGetValue("Authorization", out var auth);
            headers.TryGetValue("x-if-owner-is", out var owner);
            _requests.Add(new Request { Method = method, Path = path, Auth = auth, Owner = owner, Body = body });
            if (_failStatus != 0)
            {
                int status = _failStatus;
                string extra = _failRetryAfter != null ? $"Retry-After: {_failRetryAfter}\r\n" : "";
                _failStatus = 0;
                _failRetryAfter = null;
                return (status, "{}", extra);
            }
            bool keyOk = auth == "Bearer " + ValidKey;
            if (method == "GET" && path == "/api/user/self")
                return keyOk ? (200, JsonConvert.SerializeObject(new { id = 7, discord_username = "Tester" }), "") : (401, "", "");
            if (method == "POST" && path == "/api/tracker")
                return (200, JsonConvert.SerializeObject(new { tracker_id = TrackerId }), "");
            if (method == "GET" && path == "/api/tracker/" + TrackerId)
                return (200, JsonConvert.SerializeObject(_tracker, CheeseClient.Json), "");
            if (method == "PUT" && path.StartsWith("/api/tracker/" + TrackerId + "/game/", StringComparison.Ordinal))
            {
                int id = int.Parse(path.Substring(path.LastIndexOf('/') + 1));
                var game = _tracker.Games.FirstOrDefault(g => g.Id == id);
                if (game == null) return (404, "", "");
                var update = JsonConvert.DeserializeObject<CtGameUpdate>(body, CheeseClient.Json)!;
                bool claimChange = update.ClaimedByUserId != game.ClaimedByUserId || update.DiscordUsername != game.DiscordUsername;
                if (claimChange)
                {
                    if (owner == null) return (428, "", "");
                    var expected = JsonConvert.DeserializeObject<CtOwner>(owner)!;
                    if (expected.ClaimedByUserId != game.ClaimedByUserId || expected.DiscordUsername != game.DiscordUsername) return (412, "", "");
                    if (update.ClaimedByUserId != null && !keyOk) return (401, "", "");
                }
                update.ApplyTo(game);
                return (200, JsonConvert.SerializeObject(game, CheeseClient.Json), "");
            }
            return (404, "", "");
        }
    }

    /// <summary>Stops listening; requests in progress end at once.</summary>
    /// <summary>Stops the server, and waits (up to 5 seconds) for its requests and its accept loop to end.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Task[] running;
        lock (_serving) running = _serving.Append(_accepting).ToArray();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(5)));
        _stop.Dispose();
    }
}
