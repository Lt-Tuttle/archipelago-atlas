using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.CheeseTracker
{
    public enum CtOutcome
    {
        Ok,
        /// <summary>No such tracker or slot.</summary>
        NotFound,
        /// <summary>The API key is missing, wrong or was replaced.</summary>
        Unauthorized,
        /// <summary>Not allowed (someone else's claim, a locked tracker, or a room Cheese Tracker doesn't accept).</summary>
        Forbidden,
        /// <summary>The slot's claim changed since Atlas read it.</summary>
        OwnerChanged,
        /// <summary>The site refused the request as malformed.</summary>
        Rejected,
        RateLimited,
        ServerError,
        Unreachable,
        BadResponse,
        /// <summary>Atlas is leaving the site alone after a failure; nothing was sent.</summary>
        Waiting
    }

    public sealed class CtResult<T>
    {
        public CtOutcome Outcome { get; init; }
        public T Value { get; init; }
        public int Status { get; init; }
        public string Message { get; init; }
        public bool Ok => Outcome == CtOutcome.Ok;
    }

    /// <summary>
    /// Talks to one Cheese Tracker site, through <see cref="PoliteHttp"/>: Cheese Tracker is run by volunteers, so one
    /// request at a time per site, at least a second apart, and after a failure the site is left alone for a growing
    /// while. An API key given to a client is only ever sent to that client's own site.
    /// </summary>
    public sealed class CheeseClient
    {
        public const string DefaultInstance = "https://cheesetrackers.theincrediblewheelofchee.se";

        internal static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            // Times stay text, exactly as the server wrote them.
            DateParseHandling = DateParseHandling.None,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            StringEscapeHandling = StringEscapeHandling.EscapeNonAscii
        };

        /// <summary>Least time between two requests to one site (the self-test shortens it).</summary>
        internal static TimeSpan Spacing
        {
            get => PoliteHttp.Spacing;
            set => PoliteHttp.Spacing = value;
        }

        /// <summary>"https://host[:port]", or null when the address can't be used.</summary>
        public string Site { get; }
        private readonly string _apiKey;

        public CheeseClient(string site, string apiKey = null)
        {
            Site = NormalizeSite(site);
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        }

        public bool HasKey => _apiKey != null;

        /// <summary>"https://host[:port]" in lower case. Plain http is allowed only for this PC (a local test server).</summary>
        public static string NormalizeSite(string url) => PoliteHttp.NormalizeSite(url);

        public static bool SameSite(string a, string b) => PoliteHttp.SameSite(a, b);

        /// <summary>When Atlas will next contact a site after a failure, and why it's waiting (null: not waiting).</summary>
        public static (DateTime UntilUtc, string Reason)? WaitingFor(string site) => PoliteHttp.WaitingFor(site);

        /// <summary>Ends a wait early (the user pressed "Try now").</summary>
        public static void StopWaiting(string site) => PoliteHttp.StopWaiting(site);

        internal static void ResetForTests() => PoliteHttp.ResetForTests();

        // =====================================================================
        // Endpoints
        // =====================================================================

        public Task<CtResult<CtTracker>> GetTrackerAsync(string trackerId, CancellationToken ct = default) =>
            SendAsync<CtTracker>(HttpMethod.Get, "api/tracker/" + Uri.EscapeDataString(trackerId ?? ""), null, null, ct);

        /// <summary>The Cheese Tracker id for an Archipelago room or tracker link (Cheese Tracker creates the tracker if nobody has yet).</summary>
        public async Task<CtResult<string>> ResolveTrackerAsync(string archipelagoLink, CancellationToken ct = default)
        {
            var r = await SendAsync<JObject>(HttpMethod.Post, "api/tracker", new { url = archipelagoLink }, null, ct).ConfigureAwait(false);
            if (!r.Ok) return new CtResult<string> { Outcome = r.Outcome, Status = r.Status, Message = r.Message };
            string id = r.Value?["tracker_id"]?.ToString();
            return CtLink.IsId(id)
                ? new CtResult<string> { Outcome = CtOutcome.Ok, Value = id, Status = r.Status }
                : new CtResult<string> { Outcome = CtOutcome.BadResponse, Status = r.Status, Message = "Cheese Tracker's answer had no tracker id." };
        }

        /// <summary>Saves a slot's row. A claim change must say who held the claim before (Cheese Tracker refuses it otherwise).</summary>
        public Task<CtResult<CtGame>> UpdateGameAsync(string trackerId, int gameId, CtGameUpdate update, CtOwner priorOwner, CancellationToken ct = default)
        {
            var headers = priorOwner == null ? null : new Dictionary<string, string> { ["x-if-owner-is"] = JsonConvert.SerializeObject(priorOwner, Json) };
            return SendAsync<CtGame>(HttpMethod.Put, $"api/tracker/{Uri.EscapeDataString(trackerId ?? "")}/game/{gameId}", update, headers, ct);
        }

        /// <summary>Who the API key belongs to (also proves the key works).</summary>
        public Task<CtResult<CtUser>> GetSelfAsync(CancellationToken ct = default) =>
            _apiKey == null ? Task.FromResult(new CtResult<CtUser> { Outcome = CtOutcome.Unauthorized, Message = "No API key." })
                : SendAsync<CtUser>(HttpMethod.Get, "api/user/self", null, null, ct);

        public Task<CtResult<List<CtDashboardTracker>>> GetDashboardAsync(CancellationToken ct = default) =>
            _apiKey == null ? Task.FromResult(new CtResult<List<CtDashboardTracker>> { Outcome = CtOutcome.Unauthorized, Message = "No API key." })
                : SendAsync<List<CtDashboardTracker>>(HttpMethod.Get, "api/dashboard/tracker", null, null, ct);

        // =====================================================================
        // Transport
        // =====================================================================

        private async Task<CtResult<T>> SendAsync<T>(HttpMethod method, string path, object body, Dictionary<string, string> headers, CancellationToken ct)
        {
            var content = body == null ? null : new StringContent(JsonConvert.SerializeObject(body, Json), Encoding.UTF8, "application/json");
            var r = await PoliteHttp.SendAsync(Site, "Cheese Tracker", method, path, content, headers, _apiKey, "application/json", ct).ConfigureAwait(false);
            if (!r.Ok)
            {
                string message = r.Outcome == WebOutcome.Rejected && r.Status >= 300 && r.Status < 400
                    ? r.Message.TrimEnd('.') + "; check the site address in Cheese Tracker → Settings."
                    : r.Message;
                return new CtResult<T> { Outcome = Map(r.Outcome), Status = r.Status, Message = message };
            }
            if (typeof(T) == typeof(string)) return new CtResult<T> { Outcome = CtOutcome.Ok, Value = (T)(object)r.Text, Status = r.Status };
            try
            {
                var value = JsonConvert.DeserializeObject<T>(r.Text, Json);
                if (value == null) return new CtResult<T> { Outcome = CtOutcome.BadResponse, Status = r.Status, Message = "Cheese Tracker's answer was empty." };
                return new CtResult<T> { Outcome = CtOutcome.Ok, Value = value, Status = r.Status };
            }
            catch (Exception ex)
            {
                return new CtResult<T> { Outcome = CtOutcome.BadResponse, Status = r.Status, Message = "Cheese Tracker's answer wasn't what Atlas expected (" + ex.Message + ")." };
            }
        }

        private static CtOutcome Map(WebOutcome outcome) => outcome switch
        {
            WebOutcome.Ok => CtOutcome.Ok,
            WebOutcome.NotFound => CtOutcome.NotFound,
            WebOutcome.Unauthorized => CtOutcome.Unauthorized,
            WebOutcome.Forbidden => CtOutcome.Forbidden,
            WebOutcome.Precondition => CtOutcome.OwnerChanged,
            WebOutcome.RateLimited => CtOutcome.RateLimited,
            WebOutcome.ServerError => CtOutcome.ServerError,
            WebOutcome.Unreachable => CtOutcome.Unreachable,
            WebOutcome.BadResponse or WebOutcome.TooLarge => CtOutcome.BadResponse,
            WebOutcome.Waiting => CtOutcome.Waiting,
            _ => CtOutcome.Rejected
        };
    }
}
