using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Newtonsoft.Json;

namespace AP_Atlas.Core.CheeseTracker
{
    /// <summary>One multiworld room on Cheese Tracker, as GET /api/tracker/{id} returns it.</summary>
    public sealed class CtTracker
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("tracker_id")] public string TrackerId { get; set; } = "";
        [JsonProperty("updated_at")] public string UpdatedAt { get; set; }
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("owner_ct_user_id")] public int? OwnerUserId { get; set; }
        [JsonProperty("owner_discord_username")] public string OwnerName { get; set; }
        [JsonProperty("upstream_url")] public string UpstreamUrl { get; set; } = "";
        [JsonProperty("room_link")] public string RoomLink { get; set; } = "";
        [JsonProperty("room_host")] public string RoomHost { get; set; }
        [JsonProperty("last_port")] public int? LastPort { get; set; }
        [JsonProperty("global_ping_policy")] public string GlobalPingPolicy { get; set; }
        [JsonProperty("inactivity_threshold_yellow_hours")] public int YellowHours { get; set; } = 24;
        [JsonProperty("inactivity_threshold_red_hours")] public int RedHours { get; set; } = 48;
        [JsonProperty("require_authentication_to_claim")] public bool RequireAuthToClaim { get; set; }
        [JsonProperty("games")] public List<CtGame> Games { get; set; } = new List<CtGame>();
        [JsonProperty("hints")] public List<CtHint> Hints { get; set; } = new List<CtHint>();
    }

    /// <summary>One slot's row on a tracker. Times stay as the server wrote them, so sending one back changes nothing.</summary>
    public sealed class CtGame
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("tracker_id")] public int TrackerDbId { get; set; }
        /// <summary>The slot's player number in the multiworld (the "#" column of the Archipelago tracker).</summary>
        [JsonProperty("position")] public int Position { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("game")] public string Game { get; set; } = "";
        [JsonProperty("tracker_status")] public string TrackerStatus { get; set; } = "";
        [JsonProperty("checks_done")] public int ChecksDone { get; set; }
        [JsonProperty("checks_total")] public int ChecksTotal { get; set; }
        [JsonProperty("last_activity")] public string LastActivity { get; set; }
        [JsonProperty("discord_username")] public string DiscordUsername { get; set; }
        [JsonProperty("discord_ping")] public string Ping { get; set; } = "never";
        [JsonProperty("last_checked")] public string LastChecked { get; set; }
        [JsonProperty("notes")] public string Notes { get; set; } = "";
        [JsonProperty("claimed_by_ct_user_id")] public int? ClaimedByUserId { get; set; }
        [JsonProperty("availability_status")] public string Availability { get; set; } = "unknown";
        [JsonProperty("completion_status")] public string Completion { get; set; } = "incomplete";
        [JsonProperty("progression_status")] public string Progression { get; set; } = "unknown";
        /// <summary>Who has the slot: the signed-in claimer's Discord name, or the name typed by someone who wasn't signed in.</summary>
        [JsonProperty("effective_discord_username")] public string OwnerName { get; set; }
        [JsonProperty("user_is_away")] public bool OwnerAway { get; set; }

        public bool IsClaimed => ClaimedByUserId != null || !string.IsNullOrEmpty(DiscordUsername);
        public bool IsComplete => Completion != "incomplete";
        public DateTime? LastActivityUtc => CtTime.Parse(LastActivity);
        public DateTime? LastCheckedUtc => CtTime.Parse(LastChecked);
    }

    public sealed class CtHint
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("finder_game_id")] public int FinderGameId { get; set; }
        [JsonProperty("receiver_game_id")] public int? ReceiverGameId { get; set; }
        [JsonProperty("item")] public string Item { get; set; } = "";
        [JsonProperty("location")] public string Location { get; set; } = "";
        [JsonProperty("entrance")] public string Entrance { get; set; } = "";
        [JsonProperty("found")] public bool Found { get; set; }
        [JsonProperty("classification")] public string Classification { get; set; } = "unset";
        [JsonProperty("item_link_name")] public string ItemLinkName { get; set; } = "";
    }

    /// <summary>The account an API key belongs to.</summary>
    public sealed class CtUser
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("discord_username")] public string Name { get; set; } = "";
    }

    /// <summary>A tracker on the user's Cheese Tracker dashboard.</summary>
    public sealed class CtDashboardTracker
    {
        [JsonProperty("tracker_id")] public string TrackerId { get; set; } = "";
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("owner_discord_username")] public string OwnerName { get; set; }
        [JsonProperty("room_link")] public string RoomLink { get; set; } = "";
        [JsonProperty("room_host")] public string RoomHost { get; set; }
        [JsonProperty("last_port")] public int? LastPort { get; set; }
    }

    /// <summary>
    /// The body of PUT /api/tracker/{id}/game/{gameId}. The server replaces every one of these fields, so an update always
    /// starts from a freshly read row and changes only what was asked.
    /// </summary>
    public sealed class CtGameUpdate
    {
        [JsonProperty("claimed_by_ct_user_id")] public int? ClaimedByUserId { get; set; }
        [JsonProperty("discord_username")] public string DiscordUsername { get; set; }
        [JsonProperty("discord_ping")] public string Ping { get; set; } = "never";
        [JsonProperty("availability_status")] public string Availability { get; set; } = "unknown";
        [JsonProperty("completion_status")] public string Completion { get; set; } = "incomplete";
        [JsonProperty("progression_status")] public string Progression { get; set; } = "unknown";
        [JsonProperty("last_checked")] public string LastChecked { get; set; }
        [JsonProperty("notes")] public string Notes { get; set; } = "";

        public static CtGameUpdate From(CtGame g) => new CtGameUpdate
        {
            ClaimedByUserId = g.ClaimedByUserId,
            DiscordUsername = g.DiscordUsername,
            Ping = g.Ping,
            Availability = g.Availability,
            Completion = g.Completion,
            Progression = g.Progression,
            LastChecked = g.LastChecked,
            Notes = g.Notes ?? ""
        };

        public bool SameAs(CtGameUpdate o) =>
            o != null && ClaimedByUserId == o.ClaimedByUserId && DiscordUsername == o.DiscordUsername && Ping == o.Ping &&
            Availability == o.Availability && Completion == o.Completion && Progression == o.Progression &&
            LastChecked == o.LastChecked && (Notes ?? "") == (o.Notes ?? "");

        /// <summary>Copies the saved fields back into a row (the server's answer to an update).</summary>
        public void ApplyTo(CtGame g)
        {
            g.ClaimedByUserId = ClaimedByUserId;
            g.DiscordUsername = DiscordUsername;
            g.Ping = Ping;
            g.Availability = Availability;
            g.Completion = Completion;
            g.Progression = Progression;
            g.LastChecked = LastChecked;
            g.Notes = Notes ?? "";
        }
    }

    /// <summary>Who claimed a row before a claim change (the "x-if-owner-is" precondition).</summary>
    public sealed class CtOwner
    {
        [JsonProperty("claimed_by_ct_user_id")] public int? ClaimedByUserId { get; set; }
        [JsonProperty("discord_username")] public string DiscordUsername { get; set; }

        public static CtOwner Of(CtGame g) => new CtOwner { ClaimedByUserId = g.ClaimedByUserId, DiscordUsername = g.DiscordUsername };
    }

    public static class CtTime
    {
        public static DateTime? Parse(string text) =>
            !string.IsNullOrEmpty(text) && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;

        public static string Format(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        /// <summary>"12 min", "3.5 h", "1.2 days".</summary>
        public static string Ago(DateTime? utc)
        {
            if (utc == null) return "never";
            var span = DateTime.UtcNow - utc.Value;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalDays < 1) return $"{span.TotalHours:0.#} h ago";
            return $"{span.TotalDays:0.#} days ago";
        }
    }

    /// <summary>Cheese Tracker's slot statuses, with the labels and colors its site uses.</summary>
    public static class CtStatus
    {
        public static readonly string[] ProgressionIds = { "unknown", "unblocked", "bk", "soft_bk", "go" };
        public static readonly string[] CompletionIds = { "incomplete", "all_checks", "goal", "done", "released" };
        public static readonly string[] PingIds = { "liberally", "sparingly", "hints", "see_notes", "never" };

        public static string Label(string id) => id switch
        {
            "unknown" => "Unknown",
            "unblocked" => "Unblocked",
            "bk" => "BK",
            "soft_bk" => "Soft BK",
            "go" => "Go mode",
            "incomplete" => "Incomplete",
            "all_checks" => "All checks",
            "goal" => "Goal",
            "done" => "Done",
            "released" => "Forfeit",
            "open" => "Open",
            "claimed" => "Claimed",
            "public" => "Public",
            "liberally" => "Liberally",
            "sparingly" => "Sparingly",
            "hints" => "For hints",
            "see_notes" => "See notes",
            "never" => "Never",
            null => "",
            _ => id
        };

        public static Color ColorOf(string id) => id switch
        {
            "unblocked" => Colors.WhiteSmoke,
            "bk" => Colors.Tomato,
            "soft_bk" => Colors.Gold,
            "go" => Colors.LimeGreen,
            "all_checks" or "goal" => Colors.SkyBlue,
            "done" => Colors.LimeGreen,
            "released" => Colors.Gray,
            "incomplete" => Colors.LightGray,
            _ => Colors.Gray
        };

        /// <summary>What a slot's badge shows: its completion once it's complete, otherwise how it's progressing.</summary>
        public static string Headline(CtGame g) => g == null ? null : g.IsComplete ? g.Completion : g.Progression;
    }

    /// <summary>
    /// A link the user pasted: a Cheese Tracker page, or an Archipelago room or tracker page (Cheese Tracker can look
    /// those up). Ids are the 22-character form both sites use.
    /// </summary>
    public sealed class CtLink
    {
        public enum LinkKind { CheeseTracker, ApRoom, ApTracker }

        public LinkKind Kind { get; private set; }
        /// <summary>"https://host[:port]" of the page.</summary>
        public string Site { get; private set; }
        public string Id { get; private set; }
        /// <summary>The link in its plain form ("https://host/tracker/id").</summary>
        public string Url { get; private set; }

        /// <summary>Hosts known to run Cheese Tracker, besides the one in Settings.</summary>
        private static readonly string[] KnownCheeseHosts = { "cheesetrackers.theincrediblewheelofchee.se" };

        public static CtLink Parse(string text, string cheeseSite, out string error)
        {
            error = null;
            text = (text ?? "").Trim();
            if (text.Length == 0) { error = "Paste a link first."; return null; }
            if (!text.Contains("://")) text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                error = "That isn't a web link.";
                return null;
            }
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || (segments[0] != "tracker" && segments[0] != "room"))
            {
                error = "Use a Cheese Tracker page (…/tracker/…), or an Archipelago room (…/room/…) or tracker (…/tracker/…) link.";
                return null;
            }
            string id = segments[1];
            if (!IsId(id))
            {
                error = "The id in that link doesn't look right; copy the whole link again.";
                return null;
            }
            string site = CheeseClient.NormalizeSite(uri.GetLeftPart(UriPartial.Authority));
            if (site == null)
            {
                error = "Atlas only uses secure (https) links for other sites.";
                return null;
            }
            string cheeseHost = Uri.TryCreate(cheeseSite ?? "", UriKind.Absolute, out var cu) ? cu.Host : null;
            bool isCheese = segments[0] == "tracker" &&
                (string.Equals(uri.Host, cheeseHost, StringComparison.OrdinalIgnoreCase) || Array.Exists(KnownCheeseHosts, h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)));
            var kind = isCheese ? LinkKind.CheeseTracker : segments[0] == "room" ? LinkKind.ApRoom : LinkKind.ApTracker;
            return new CtLink { Kind = kind, Site = site, Id = id, Url = $"{site}/{(kind == LinkKind.ApRoom ? "room" : "tracker")}/{id}" };
        }

        /// <summary>A 16-byte id in URL-safe base64 without padding (exactly what both sites accept).</summary>
        public static bool IsId(string id)
        {
            if (id == null || id.Length != 22) return false;
            foreach (char c in id)
                if (!(c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-' || c == '_')) return false;
            // The last character carries 2 bits; the 4 bits after them must be zero for a 16-byte value.
            return "AQgw".IndexOf(id[21]) >= 0;
        }
    }
}
