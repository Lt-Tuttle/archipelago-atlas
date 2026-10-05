using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core
{
    public enum WebOutcome
    {
        Ok,
        NotFound,
        Unauthorized,
        Forbidden,
        /// <summary>412: a precondition (like "the claim is still X") no longer holds.</summary>
        Precondition,
        /// <summary>The site refused the request as malformed, or redirected it.</summary>
        Rejected,
        RateLimited,
        ServerError,
        Unreachable,
        BadResponse,
        /// <summary>The answer was larger than the caller reads; Atlas stopped reading it.</summary>
        TooLarge,
        /// <summary>Atlas is leaving the site alone after a failure; nothing was sent.</summary>
        Waiting
    }

    public sealed class WebResponse
    {
        public WebOutcome Outcome { get; init; }
        public int Status { get; init; }
        public string Text { get; init; }
        /// <summary>What the caller's reader made of the answer (<see cref="PoliteHttp.GetParsedAsync"/>).</summary>
        public object Value { get; init; }
        /// <summary>How much of the answer was read (after decompression).</summary>
        public long Bytes { get; init; }
        public string Message { get; init; }
        public bool Ok => Outcome == WebOutcome.Ok;
    }

    /// <summary>
    /// Web requests made with care for the volunteer-run sites Atlas reads (Cheese Tracker, Archipelago's web host): one
    /// request at a time per site, at least a second apart, and after a failure the site is left alone for a growing while
    /// (1, 2, 5, 10, then 30 minutes, or longer if it asks). Answers are compressed in transit, have a size limit and a
    /// time limit that covers reading them whole, and redirects aren't followed (so a key is never sent anywhere but the
    /// site it was given for).
    /// </summary>
    public static class PoliteHttp
    {
        private const int MaxResponseBytes = 32 * 1024 * 1024;
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
        private static readonly HttpClient Http = CreateHttp();

        private sealed class SiteState
        {
            public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            public DateTime LastRequest = DateTime.MinValue;
            public DateTime WaitUntil = DateTime.MinValue;
            public int Failures;
            public string WaitReason;
        }

        private static readonly Dictionary<string, SiteState> Sites = new Dictionary<string, SiteState>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan[] Backoff = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30) };

        /// <summary>Least time between two requests to one site (the self-test shortens it).</summary>
        internal static TimeSpan Spacing = TimeSpan.FromSeconds(1);

        private static HttpClient CreateHttp()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AllowAutoRedirect = false
            };
            // Each request has its own time limit, covering the whole answer (HttpClient's own only lasts until the headers).
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AtlasVersion.UserAgent);
            return client;
        }

        /// <summary>"https://host[:port]" in lower case. Plain http is allowed only for this PC (a local test server).</summary>
        public static string NormalizeSite(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) return null;
            if (!url.Contains("://")) url = "https://" + url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) return null;
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant();
        }

        public static bool SameSite(string a, string b)
        {
            string x = NormalizeSite(a), y = NormalizeSite(b);
            return x != null && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static SiteState StateOf(string site)
        {
            lock (Sites)
            {
                if (!Sites.TryGetValue(site, out var s)) Sites[site] = s = new SiteState();
                return s;
            }
        }

        /// <summary>When Atlas will next contact a site after a failure, and why it's waiting (null: not waiting).</summary>
        public static (DateTime UntilUtc, string Reason)? WaitingFor(string site)
        {
            site = NormalizeSite(site);
            if (site == null) return null;
            var s = StateOf(site);
            return DateTime.UtcNow < s.WaitUntil ? (s.WaitUntil, s.WaitReason) : null;
        }

        /// <summary>Ends a wait early (the user pressed "Try now").</summary>
        public static void StopWaiting(string site)
        {
            site = NormalizeSite(site);
            if (site != null) StateOf(site).WaitUntil = DateTime.MinValue;
        }

        internal static void ResetForTests()
        {
            lock (Sites) Sites.Clear();
        }

        /// <summary>
        /// Sends one request to <paramref name="site"/> (already normalized) and reads the answer as text (up to 32 MB,
        /// within 30 seconds). <paramref name="siteName"/> names the site in messages ("Cheese Tracker", "archipelago.gg").
        /// </summary>
        public static Task<WebResponse> SendAsync(string site, string siteName, HttpMethod method, string path, HttpContent content = null,
            IDictionary<string, string> headers = null, string bearer = null, string accept = "application/json", CancellationToken ct = default) =>
            SendCoreAsync(site, siteName, method, path, content, headers, bearer, accept, DefaultTimeout, MaxResponseBytes, null, ct);

        /// <summary>
        /// Reads a page that may be large: its text goes to <paramref name="parse"/> (on a worker thread) as it arrives, so it's
        /// never held whole. At most <paramref name="maxBytes"/> are read (more: <see cref="WebOutcome.TooLarge"/>), within
        /// <paramref name="timeout"/>. What <paramref name="parse"/> returns is the answer's <see cref="WebResponse.Value"/>.
        /// </summary>
        public static Task<WebResponse> GetParsedAsync(string site, string siteName, string path, Func<TextReader, object> parse, long maxBytes,
            TimeSpan timeout, string accept = "text/html", CancellationToken ct = default) =>
            SendCoreAsync(site, siteName, HttpMethod.Get, path, null, null, null, accept, timeout, maxBytes, parse, ct);

        private static async Task<WebResponse> SendCoreAsync(string site, string siteName, HttpMethod method, string path, HttpContent content,
            IDictionary<string, string> headers, string bearer, string accept, TimeSpan timeout, long maxBytes, Func<TextReader, object> parse,
            CancellationToken ct)
        {
            if (site == null) return Fail(WebOutcome.Rejected, 0, $"That isn't an address Atlas can use for {siteName} (it must be https).");
            var state = StateOf(site);
            await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (DateTime.UtcNow < state.WaitUntil)
                    return Fail(WebOutcome.Waiting, 0, $"{state.WaitReason}. Atlas tries again after {state.WaitUntil.ToLocalTime():HH:mm}.");
                var gap = state.LastRequest + Spacing - DateTime.UtcNow;
                if (gap > TimeSpan.Zero) await Task.Delay(gap, ct).ConfigureAwait(false);
                state.LastRequest = DateTime.UtcNow;

                using var request = new HttpRequestMessage(method, site + "/" + path.TrimStart('/'));
                // A key goes to this site and nowhere else (redirects aren't followed).
                if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                if (!string.IsNullOrEmpty(accept)) request.Headers.Accept.ParseAdd(accept);
                if (headers != null)
                    foreach (var kv in headers) request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                request.Content = content;

                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(timeout);
                string slow = $"it didn't answer within {Describe(timeout)}";

                HttpResponseMessage response;
                try
                {
                    response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    string why = ex is OperationCanceledException ? slow : ex.GetBaseException().Message;
                    StartWaiting(state, null, $"{siteName} couldn't be reached ({why})");
                    return Fail(WebOutcome.Unreachable, 0, $"{siteName} couldn't be reached ({why}).");
                }

                using (response)
                {
                    int code = (int)response.StatusCode;
                    if (code == 429 || code >= 500)
                    {
                        string reason = code == 429 ? $"{siteName} asked Atlas to slow down" : $"{siteName} had a problem (HTTP {code})";
                        StartWaiting(state, RetryAfter(response), reason);
                        return Fail(code == 429 ? WebOutcome.RateLimited : WebOutcome.ServerError, code, reason + ".");
                    }
                    // Anything else means the site is up: no waiting.
                    state.Failures = 0;
                    state.WaitUntil = DateTime.MinValue;
                    if (code >= 300 && code < 400) return Fail(WebOutcome.Rejected, code, $"{siteName} redirected the request (HTTP {code}).");
                    if (code < 200 || code >= 300)
                    {
                        var outcome = code switch
                        {
                            401 => WebOutcome.Unauthorized,
                            403 => WebOutcome.Forbidden,
                            404 => WebOutcome.NotFound,
                            412 => WebOutcome.Precondition,
                            _ => WebOutcome.Rejected
                        };
                        return Fail(outcome, code, $"{siteName} refused the request (HTTP {code}).");
                    }
                    string tooLarge = $"{siteName}'s answer is larger than {maxBytes / (1024 * 1024)} MB, more than Atlas reads.";
                    if (response.Content.Headers.ContentLength > maxBytes) return Fail(WebOutcome.TooLarge, code, tooLarge);
                    Stream stream = null;
                    // A stalled answer is cut off at the time limit (a blocked read only ends when its stream closes).
                    using var stall = limit.Token.Register(() =>
                    {
                        try { stream?.Dispose(); } catch { }
                        try { response.Dispose(); } catch { }
                    });
                    try
                    {
                        stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                        var limited = new LimitedStream(stream, maxBytes);
                        if (parse == null)
                        {
                            string text = await ReadAllAsync(limited, limit.Token).ConfigureAwait(false);
                            return new WebResponse { Outcome = WebOutcome.Ok, Status = code, Text = text, Bytes = limited.BytesRead };
                        }
                        object value = await Task.Run(() =>
                        {
                            using var reader = new StreamReader(limited, Encoding.UTF8, true, 1 << 16, leaveOpen: true);
                            return parse(reader);
                        }).ConfigureAwait(false);
                        return new WebResponse { Outcome = WebOutcome.Ok, Status = code, Value = value, Bytes = limited.BytesRead };
                    }
                    catch (Exception) when (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ct);
                    }
                    catch (TooLargeException)
                    {
                        return Fail(WebOutcome.TooLarge, code, tooLarge);
                    }
                    catch (Exception) when (limit.IsCancellationRequested)
                    {
                        string reason = $"{siteName} was too slow (its answer took over {Describe(timeout)})";
                        StartWaiting(state, null, reason);
                        return Fail(WebOutcome.Unreachable, code, reason + ".");
                    }
                    catch (Exception ex)
                    {
                        return Fail(WebOutcome.BadResponse, code, $"{siteName}'s answer couldn't be read ({ex.GetBaseException().Message}).");
                    }
                }
            }
            finally
            {
                state.Gate.Release();
            }
        }

        private static string Describe(TimeSpan t) =>
            t.TotalSeconds < 120 ? $"{(int)t.TotalSeconds} seconds" : $"{(int)t.TotalMinutes} minutes";

        private static WebResponse Fail(WebOutcome outcome, int status, string message) =>
            new WebResponse { Outcome = outcome, Status = status, Message = message };

        private static void StartWaiting(SiteState site, TimeSpan? asked, string reason)
        {
            site.Failures++;
            var wait = Backoff[Math.Min(site.Failures, Backoff.Length) - 1];
            if (asked.HasValue && asked.Value > wait) wait = asked.Value < TimeSpan.FromHours(6) ? asked.Value : TimeSpan.FromHours(6);
            site.WaitUntil = DateTime.UtcNow + wait;
            site.WaitReason = reason;
            Logger.LogWarning($"{reason}; Atlas leaves it alone until {site.WaitUntil.ToLocalTime():HH:mm}.");
        }

        private static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header?.Delta != null) return header.Delta;
            if (header?.Date != null) return header.Date.Value - DateTimeOffset.UtcNow;
            return null;
        }

        private static async Task<string> ReadAllAsync(Stream stream, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0) buffer.Write(chunk, 0, read);
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        private sealed class TooLargeException : IOException
        {
        }

        /// <summary>Reads through to another stream, counting, and stops past a size limit.</summary>
        private sealed class LimitedStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _max;

            public LimitedStream(Stream inner, long max)
            {
                _inner = inner;
                _max = max;
            }

            public long BytesRead { get; private set; }

            private int Count(int n)
            {
                BytesRead += n;
                if (BytesRead > _max) throw new TooLargeException();
                return n;
            }

            public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));
            public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer));

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                Count(await _inner.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false));

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
                Count(await _inner.ReadAsync(buffer, ct).ConfigureAwait(false));

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
