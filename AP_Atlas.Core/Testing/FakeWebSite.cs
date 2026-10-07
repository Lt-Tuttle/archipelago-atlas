using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core.Testing;

/// <summary>
/// A small web site on this PC for tests: answers GET requests by path with whatever <see cref="Respond"/> says, and
/// keeps every path asked for. Nothing leaves the PC (a loopback http site is the one http site the polite client accepts).
/// </summary>
public sealed class FakeWebSite : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _serving = new();
    private readonly List<string> _requests = new();
    private readonly Task _accepting;

    public FakeWebSite()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Site = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _accepting = AcceptAsync();
    }

    /// <summary>"http://127.0.0.1:port".</summary>
    public string Site { get; }

    /// <summary>The answer for a path (its status and body, as JSON unless <see cref="ContentType"/> says otherwise).</summary>
    public Func<string, (int Status, string Body)> Respond { get; set; } = _ => (404, "");

    public string ContentType { get; set; } = "application/json";

    /// <summary>Every path asked for, in order.</summary>
    public IReadOnlyList<string> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
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
                while (headerEnd < 0 && buffer.Length < 65536)
                {
                    int n = await stream.ReadAsync(chunk, _stop.Token).ConfigureAwait(false);
                    if (n <= 0) return;
                    await buffer.WriteAsync(chunk.AsMemory(0, n), _stop.Token).ConfigureAwait(false);
                    headerEnd = IndexOf(buffer, "\r\n\r\n");
                }
                if (headerEnd < 0) return;
                string requestLine = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd).Split("\r\n")[0];
                var parts = requestLine.Split(' ');
                string path = parts.Length > 1 ? parts[1] : "/";
                lock (_requests) _requests.Add(path);
                var (status, body) = Respond(path);
                byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
                string head = $"HTTP/1.1 {status} Test\r\nContent-Type: {ContentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
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

    /// <summary>Stops the site, and waits (up to 5 seconds) for its requests and its accept loop to end.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        List<Task> serving;
        lock (_serving) serving = new List<Task>(_serving);
        serving.Add(_accepting);
        try
        {
            await Task.WhenAll(serving).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopping is best effort; the listener is closed either way.
        }
        _stop.Dispose();
    }
}
