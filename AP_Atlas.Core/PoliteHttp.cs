#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
        Waiting,
        /// <summary>304: the copy Atlas already has is still current (a conditional request).</summary>
        NotModified
    }

    public sealed class WebResponse
    {
        public WebOutcome Outcome { get; init; }
        public int Status { get; init; }
        /// <summary>The answer as text; for a download, the file's SHA-256 (lower-case hex).</summary>
        public string Text { get; init; }
        /// <summary>What the caller's reader made of the answer (<see cref="PoliteHttp.GetParsedAsync"/>).</summary>
        public object Value { get; init; }
        /// <summary>How much of the answer was read (after decompression).</summary>
        public long Bytes { get; init; }
        public string Message { get; init; }
        /// <summary>The answer's headers (names in lower case), when the site answered at all.</summary>
        public IReadOnlyDictionary<string, string> Headers { get; init; } = EmptyHeaders;
        public bool Ok => Outcome == WebOutcome.Ok;

        internal static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();

        public string Header(string name) => Headers != null && Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;

        /// <summary>When the site sent its answer, by the site's own clock (its Date header); null without one.</summary>
        public DateTime? SentUtc =>
            DateTimeOffset.TryParse(Header("date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sent) ? sent.UtcDateTime : null;
    }

    /// <summary>
    /// Every web request Atlas makes goes through here, with care for the volunteer-run sites it reads (Cheese Tracker,
    /// Archipelago's web host, GitHub, PyPI): one request at a time per site, at least a second apart, and after a
    /// failure the site is left alone for a growing while (1, 2, 5, 10, then 30 minutes, or longer if it asks). Answers
    /// are compressed in transit, have a size limit and a time limit that covers reading them whole. Redirects aren't
    /// followed (so a key is never sent anywhere but the site it was given for), except for downloads, which follow a
    /// few https redirects without any key.
    /// </summary>
    public static class PoliteHttp
    {
        private const int MaxResponseBytes = 32 * 1024 * 1024;
        private const int MaxRedirects = 5;
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
        private static readonly HttpClient Http = CreateHttp();

        private sealed class SiteState
        {
            public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            // When the next request may go (Spacing after the last one), and when a wait after failures ends. Deadlines
            // measure with a monotonic clock, so setting the PC's clock can't stretch either.
            public Deadline NextRequest;
            public Deadline Wait;
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

        /// <summary>A full URL split into its site (as <see cref="NormalizeSite"/>) and the path with query; null site when Atlas can't use it.</summary>
        public static (string Site, string PathAndQuery) Split(string url)
        {
            string site = NormalizeSite(url);
            if (site == null || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return (null, null);
            return (site, uri.PathAndQuery);
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
            return s.Wait.Passed ? null : (s.Wait.ShownUtc, s.WaitReason);
        }

        /// <summary>When Atlas will next contact a site after a failure (passed already if it isn't waiting).</summary>
        public static Deadline WaitOf(string site)
        {
            site = NormalizeSite(site);
            return site == null ? Deadline.None : StateOf(site).Wait;
        }

        /// <summary>Ends a wait early (the user pressed "Try now").</summary>
        public static void StopWaiting(string site)
        {
            site = NormalizeSite(site);
            if (site != null) StateOf(site).Wait = Deadline.None;
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
            SendCoreAsync(site, siteName, method, path, content, headers, bearer, accept, DefaultTimeout, MaxResponseBytes, null, null, null, false, ct);

        /// <summary>Reads a full https URL as text (no key is ever sent; redirects aren't followed).</summary>
        public static Task<WebResponse> GetAsync(string url, string siteName, string accept = "application/json", IDictionary<string, string> headers = null,
            long maxBytes = MaxResponseBytes, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var (site, path) = Split(url);
            return SendCoreAsync(site, siteName, HttpMethod.Get, path ?? "/", null, headers, null, accept, timeout ?? DefaultTimeout, maxBytes, null, null, null, false, ct);
        }

        /// <summary>
        /// Reads a page that may be large: its text goes to <paramref name="parse"/> (on a worker thread) as it arrives, so it's
        /// never held whole. At most <paramref name="maxBytes"/> are read (more: <see cref="WebOutcome.TooLarge"/>), within
        /// <paramref name="timeout"/>. What <paramref name="parse"/> returns is the answer's <see cref="WebResponse.Value"/>.
        /// </summary>
        public static Task<WebResponse> GetParsedAsync(string site, string siteName, string path, Func<TextReader, object> parse, long maxBytes,
            TimeSpan timeout, string accept = "text/html", CancellationToken ct = default) =>
            SendCoreAsync(site, siteName, HttpMethod.Get, path, null, null, null, accept, timeout, maxBytes, parse, null, null, false, ct);

        /// <summary>
        /// Downloads a file to <paramref name="destPath"/>: streamed to "destPath.part" and moved into place only when complete
        /// (and, with <paramref name="expectedSha256"/>, only when it is exactly that file). Follows up to five https redirects
        /// (GitHub serves release files from another host). The answer's <see cref="WebResponse.Text"/> is the file's SHA-256.
        /// <paramref name="progress"/> gets (bytes so far, total or -1).
        /// </summary>
        public static async Task<WebResponse> DownloadAsync(string url, string siteName, string destPath, string expectedSha256, long maxBytes, TimeSpan timeout,
            Action<long, long> progress = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath)));
            string part = destPath + ".part";
            string current = url;
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                var (site, path) = Split(current);
                var r = await SendCoreAsync(site, hop == 0 ? siteName : $"{siteName} (via {site})", HttpMethod.Get, path ?? "/", null, null, null, "*/*",
                    timeout, maxBytes, null, part, progress, allowRedirect: true, ct).ConfigureAwait(false);
                if (r.Outcome == WebOutcome.Rejected && r.Status >= 300 && r.Status < 400 && r.Header("location") is string location)
                {
                    if (!Uri.TryCreate(new Uri(current), location, out var next) || NormalizeSite(next.AbsoluteUri) == null)
                        return Fail(WebOutcome.Rejected, r.Status, $"{siteName} redirected the download to an address Atlas won't use ({location}).");
                    current = next.AbsoluteUri;
                    continue;
                }
                if (!r.Ok)
                {
                    TryDelete(part);
                    return r;
                }
                if (!string.IsNullOrEmpty(expectedSha256) && !string.Equals(r.Text, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(part);
                    string host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : siteName;
                    return Fail(WebOutcome.BadResponse, r.Status,
                        $"The download from {host} isn't the expected file (SHA-256 {Short(r.Text)}…, expected {Short(expectedSha256)}…). Nothing was kept.");
                }
                try { File.Move(part, destPath, true); }
                catch (Exception ex)
                {
                    TryDelete(part);
                    return Fail(WebOutcome.BadResponse, r.Status, $"The download couldn't be saved ({ex.Message}).");
                }
                return r;
            }
            TryDelete(part);
            return Fail(WebOutcome.Rejected, 0, $"{siteName} redirected the download too many times.");
        }

        private static string Short(string hash) => string.IsNullOrEmpty(hash) ? "?" : hash.Substring(0, Math.Min(12, hash.Length));

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { } // best effort: a leftover partial download is replaced by the next one
        }

        private static async Task<WebResponse> SendCoreAsync(string site, string siteName, HttpMethod method, string path, HttpContent content,
            IDictionary<string, string> headers, string bearer, string accept, TimeSpan timeout, long maxBytes, Func<TextReader, object> parse,
            string toFile, Action<long, long> progress, bool allowRedirect, CancellationToken ct)
        {
            if (site == null) return Fail(WebOutcome.Rejected, 0, $"That isn't an address Atlas can use for {siteName} (it must be https).");
            var state = StateOf(site);
            await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!state.Wait.Passed)
                    return Fail(WebOutcome.Waiting, 0, $"{state.WaitReason}. Atlas tries again after {state.Wait.ShownUtc.ToLocalTime():HH:mm}.");
                if (!state.NextRequest.Passed) await Task.Delay(state.NextRequest.Left, ct).ConfigureAwait(false);
                state.NextRequest = Deadline.In(Spacing);

                using var request = new HttpRequestMessage(method, site + "/" + (path ?? "").TrimStart('/'));
                // A key goes to this site and nowhere else (redirects aren't followed with it).
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
                    var answerHeaders = HeadersOf(response);
                    if (code == 429 || code >= 500)
                    {
                        string reason = code == 429 ? $"{siteName} asked Atlas to slow down" : $"{siteName} had a problem (HTTP {code})";
                        StartWaiting(state, RetryAfter(response), reason);
                        return Fail(code == 429 ? WebOutcome.RateLimited : WebOutcome.ServerError, code, reason + ".", answerHeaders);
                    }
                    // Anything else means the site is up: no waiting.
                    state.Failures = 0;
                    state.Wait = Deadline.None;
                    if (code == 304) return new WebResponse { Outcome = WebOutcome.NotModified, Status = code, Headers = answerHeaders };
                    if (code >= 300 && code < 400)
                        return Fail(WebOutcome.Rejected, code, allowRedirect ? $"{siteName} moved the file (HTTP {code})." : $"{siteName} redirected the request (HTTP {code}).", answerHeaders);
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
                        return Fail(outcome, code, $"{siteName} refused the request (HTTP {code}).", answerHeaders);
                    }
                    string tooLarge = $"{siteName}'s answer is larger than {maxBytes / (1024 * 1024)} MB, more than Atlas reads.";
                    long? declared = response.Content.Headers.ContentLength;
                    if (declared > maxBytes) return Fail(WebOutcome.TooLarge, code, tooLarge, answerHeaders);
                    Stream stream = null;
                    // A stalled answer is cut off at the time limit (a blocked read only ends when its stream closes).
                    using var stall = limit.Token.Register(() =>
                    {
                        try { stream?.Dispose(); } catch { } // cutting off a stalled read: it may be closed already
                        try { response.Dispose(); } catch { } // likewise
                    });
                    try
                    {
                        stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                        var limited = new LimitedStream(stream, maxBytes);
                        if (toFile != null)
                        {
                            string sha = await CopyToFileAsync(limited, toFile, declared ?? -1, progress, limit.Token).ConfigureAwait(false);
                            return new WebResponse { Outcome = WebOutcome.Ok, Status = code, Text = sha, Bytes = limited.BytesRead, Headers = answerHeaders };
                        }
                        if (parse == null)
                        {
                            string text = await ReadAllAsync(limited, limit.Token).ConfigureAwait(false);
                            return new WebResponse { Outcome = WebOutcome.Ok, Status = code, Text = text, Bytes = limited.BytesRead, Headers = answerHeaders };
                        }
                        object value = await Task.Run(() =>
                        {
                            using var reader = new StreamReader(limited, Encoding.UTF8, true, 1 << 16, leaveOpen: true);
                            return parse(reader);
                        }).ConfigureAwait(false);
                        return new WebResponse { Outcome = WebOutcome.Ok, Status = code, Value = value, Bytes = limited.BytesRead, Headers = answerHeaders };
                    }
                    catch (Exception) when (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ct);
                    }
                    catch (TooLargeException)
                    {
                        return Fail(WebOutcome.TooLarge, code, tooLarge, answerHeaders);
                    }
                    catch (Exception) when (limit.IsCancellationRequested)
                    {
                        string reason = $"{siteName} was too slow (its answer took over {Describe(timeout)})";
                        StartWaiting(state, null, reason);
                        return Fail(WebOutcome.Unreachable, code, reason + ".", answerHeaders);
                    }
                    catch (Exception ex)
                    {
                        return Fail(WebOutcome.BadResponse, code, $"{siteName}'s answer couldn't be read ({ex.GetBaseException().Message}).", answerHeaders);
                    }
                }
            }
            finally
            {
                state.Gate.Release();
            }
        }

        private static async Task<string> CopyToFileAsync(Stream source, string path, long total, Action<long, long> progress, CancellationToken ct)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                var sinceReport = System.Diagnostics.Stopwatch.StartNew();
                bool reported = false;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sha.AppendData(buffer, 0, read);
                    done += read;
                    if (progress != null && (!reported || sinceReport.ElapsedMilliseconds > 150))
                    {
                        reported = true;
                        sinceReport.Restart();
                        progress(done, total);
                    }
                }
                progress?.Invoke(done, total);
                await target.FlushAsync(ct).ConfigureAwait(false);
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        private static IReadOnlyDictionary<string, string> HeadersOf(HttpResponseMessage response)
        {
            var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers) all[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
            foreach (var h in response.Content.Headers) all[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
            return all;
        }

        private static string Describe(TimeSpan t) =>
            t.TotalSeconds < 120 ? $"{(int)t.TotalSeconds} seconds" : $"{(int)t.TotalMinutes} minutes";

        private static WebResponse Fail(WebOutcome outcome, int status, string message, IReadOnlyDictionary<string, string> headers = null) =>
            new WebResponse { Outcome = outcome, Status = status, Message = message, Headers = headers ?? WebResponse.EmptyHeaders };

        private static void StartWaiting(SiteState site, TimeSpan? asked, string reason)
        {
            site.Failures++;
            var wait = Backoff[Math.Min(site.Failures, Backoff.Length) - 1];
            if (asked.HasValue && asked.Value > wait) wait = asked.Value < TimeSpan.FromHours(6) ? asked.Value : TimeSpan.FromHours(6);
            site.Wait = Deadline.In(wait);
            site.WaitReason = reason;
            Logger.LogWarning($"{reason}; Atlas leaves it alone until {site.Wait.ShownUtc.ToLocalTime():HH:mm}.");
        }

        private static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header?.Delta != null) return header.Delta;
            // A date is by the site's clock, so it's measured from when the site sent its answer (its Date header): a PC
            // clock that's wrong can't shorten the wait. Without one, this PC's clock is all there is.
            if (header?.Date != null) return header.Date.Value - (response.Headers.Date ?? DateTimeOffset.UtcNow);
            return null;
        }

        private static async Task<string> ReadAllAsync(Stream stream, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0) await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
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
