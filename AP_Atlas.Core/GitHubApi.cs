#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core
{
    /// <summary>
    /// Reads GitHub's public API (release lists, repository search) through <see cref="PoliteHttp"/>, following GitHub's
    /// rules for anonymous use: about 60 requests an hour, and 10 searches a minute. Atlas reads GitHub's rate-limit headers
    /// and waits until the limit resets instead of asking again. Answers are cached with their ETag, so asking for
    /// something unchanged costs nothing against the limit. A rate-limited or failed lookup is reported as such, never as
    /// "not found".
    /// </summary>
    public static class GitHubApi
    {
        public const string Site = "https://api.github.com";
        private const int MaxCached = 100;

        public sealed class Result
        {
            public WebOutcome Outcome { get; init; }
            public JToken Json { get; init; }
            public string Message { get; init; }
            public bool Ok => Outcome == WebOutcome.Ok;
            public bool NotFound => Outcome == WebOutcome.NotFound;
            /// <summary>The lookup couldn't be done (rate limit, network, GitHub trouble): nothing is known either way.</summary>
            public bool Unknown => !Ok && !NotFound;
        }

        private sealed class Cached
        {
            public string ETag;
            public string Body;
            // When it was last used, on the steady clock: the cache drops the answer used least recently first.
            public DateTime Used;
        }

        private static readonly Dictionary<string, Cached> _cache = new Dictionary<string, Cached>(StringComparer.Ordinal);
        private static readonly object _lock = new object();
        // Until GitHub's limits reset: measured with a monotonic clock (Deadline), so setting the PC's clock can't stretch them.
        private static Deadline _coreWait, _searchWait;

        /// <summary>
        /// Reads one API path (e.g. "/repos/owner/name/releases?per_page=50"). JSON comes back parsed; a 404 is
        /// <see cref="WebOutcome.NotFound"/>; a rate limit is <see cref="WebOutcome.RateLimited"/> until GitHub's reset time.
        /// </summary>
        public static async Task<Result> GetAsync(string pathAndQuery, CancellationToken ct = default)
        {
            bool search = pathAndQuery.StartsWith("/search/", StringComparison.OrdinalIgnoreCase);
            Deadline until;
            lock (_lock) until = search ? _searchWait : _coreWait;
            if (!until.Passed)
                return new Result { Outcome = WebOutcome.RateLimited, Message = $"GitHub's limit for anonymous {(search ? "searches" : "requests")} is used up; Atlas asks again after {until.ShownUtc.ToLocalTime():HH:mm}." };

            Cached cached;
            lock (_lock) _cache.TryGetValue(pathAndQuery, out cached);
            var headers = new Dictionary<string, string> { ["X-GitHub-Api-Version"] = "2022-11-28" };
            if (cached?.ETag != null) headers["If-None-Match"] = cached.ETag;

            var r = await PoliteHttp.SendAsync(TestSite ?? Site, "GitHub", HttpMethod.Get, pathAndQuery, headers: headers, accept: "application/vnd.github+json", ct: ct).ConfigureAwait(false);
            NoteRateLimit(r, search);

            if (r.Outcome == WebOutcome.NotModified && cached != null)
            {
                lock (_lock) cached.Used = SteadyClock.UtcNow;
                return Parse(cached.Body);
            }
            if ((r.Outcome == WebOutcome.Forbidden || r.Outcome == WebOutcome.RateLimited) && IsRateLimit(r))
            {
                lock (_lock) until = search ? _searchWait : _coreWait;
                return new Result { Outcome = WebOutcome.RateLimited, Message = $"GitHub's limit for anonymous {(search ? "searches" : "requests")} is used up; Atlas asks again after {until.ShownUtc.ToLocalTime():HH:mm}." };
            }
            if (r.Outcome == WebOutcome.NotFound) return new Result { Outcome = WebOutcome.NotFound, Message = "GitHub has no such page (it may be private, renamed or deleted)." };
            if (!r.Ok) return new Result { Outcome = r.Outcome, Message = r.Message };

            var result = Parse(r.Text);
            if (result.Ok && r.Header("etag") is string etag)
            {
                lock (_lock)
                {
                    _cache[pathAndQuery] = new Cached { ETag = etag, Body = r.Text, Used = SteadyClock.UtcNow };
                    if (_cache.Count > MaxCached)
                        foreach (var old in _cache.OrderBy(kv => kv.Value.Used).Take(_cache.Count - MaxCached).Select(kv => kv.Key).ToList()) _cache.Remove(old);
                }
            }
            return result;
        }

        private static Result Parse(string text)
        {
            try { return new Result { Outcome = WebOutcome.Ok, Json = JToken.Parse(text ?? "") }; }
            catch (Exception ex) { return new Result { Outcome = WebOutcome.BadResponse, Message = "GitHub's answer couldn't be read (" + ex.Message + ")." }; }
        }

        /// <summary>A 403/429 is a rate limit when GitHub says no requests are left, or names a time to wait.</summary>
        private static bool IsRateLimit(WebResponse r) =>
            r.Header("x-ratelimit-remaining") == "0" || r.Header("retry-after") != null || r.Status == 429;

        /// <summary>Remembers GitHub's reset time when its limit is (about to be) used up, so nothing is sent until then.</summary>
        private static void NoteRateLimit(WebResponse r, bool search)
        {
            Deadline? until = null;
            if (r.Header("retry-after") is string retry && int.TryParse(retry, out int seconds))
                until = Deadline.In(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 6 * 3600)));
            else if (r.Header("x-ratelimit-remaining") == "0" && long.TryParse(r.Header("x-ratelimit-reset"), out long reset))
            {
                // The reset is a time by GitHub's clock, so it's measured from when GitHub sent its answer (its Date header):
                // a PC clock that's wrong can't shorten the wait. Six hours at most; no wait once it's past.
                var resetUtc = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(reset, 0, 253402300799)).UtcDateTime;
                var wait = resetUtc - (r.SentUtc ?? Deadline.WallClock()) + TimeSpan.FromSeconds(2);
                if (wait > TimeSpan.Zero) until = Deadline.In(wait < TimeSpan.FromHours(6) ? wait : TimeSpan.FromHours(6));
            }
            if (until == null) return;
            string resource = r.Header("x-ratelimit-resource");
            bool isSearch = resource != null ? resource.Equals("search", StringComparison.OrdinalIgnoreCase) : search;
            lock (_lock)
            {
                if (isSearch) { if (until.Value.EndsAfter(_searchWait)) _searchWait = until.Value; }
                else if (until.Value.EndsAfter(_coreWait)) _coreWait = until.Value;
            }
            Logger.LogWarning($"GitHub's anonymous {(isSearch ? "search" : "request")} limit is used up; Atlas waits until {until.Value.ShownUtc.ToLocalTime():HH:mm} before asking again.");
        }

        internal static void ResetForTests()
        {
            lock (_lock)
            {
                _cache.Clear();
                _coreWait = _searchWait = Deadline.None;
            }
        }

        /// <summary>For tests: the GitHub API site to use (a local fake server). Null: the real one.</summary>
        internal static string TestSite;
    }
}
