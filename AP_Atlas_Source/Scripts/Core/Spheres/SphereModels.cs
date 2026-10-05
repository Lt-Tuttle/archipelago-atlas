using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core.CheeseTracker;

namespace AP_Atlas.Core.Spheres
{
    /// <summary>A table read from the host's spheretracker.de room page, kept as the page has it.</summary>
    public sealed class PageTable
    {
        public string Title { get; set; } = "";
        public List<string> Columns { get; set; } = new List<string>();
        public List<List<string>> Rows { get; set; } = new List<List<string>>();
    }

    /// <summary>
    /// What Atlas read from a host's spheretracker.de room: its open-locations page, whose tables list every slot's
    /// unfound locations with their sphere (and a summary per sphere).
    /// </summary>
    public sealed class SphereData
    {
        public List<PageTable> Tables { get; set; } = new List<PageTable>();
        public string Title { get; set; } = "";
        /// <summary>The room's name, as its host named it.</summary>
        public string RoomName { get; set; } = "";
        /// <summary>Who created the room ("Created by …": the creator's Discord name), or "" if the page doesn't say.</summary>
        public string Creator { get; set; } = "";
        /// <summary>When the room's data was last brought up to date from Archipelago (someone pressed Refresh on the site).</summary>
        public DateTime? UpdatedUtc { get; set; }
        /// <summary>The Archipelago trackers the page links to: which multiworld the room is for.</summary>
        public List<string> TrackerIds { get; set; } = new List<string>();
    }

    /// <summary>
    /// A link to an Archipelago room or tracker on archipelago.gg (or another Archipelago web host). Atlas reads it from
    /// Cheese Tracker to know which multiworld a spheretracker.de room should be for.
    /// </summary>
    public sealed class ApTrackerLink
    {
        public enum LinkKind { Room, Tracker }

        public LinkKind Kind { get; private set; }
        /// <summary>"https://host[:port]".</summary>
        public string Site { get; private set; }
        public string Id { get; private set; }

        public string TrackerUrl => Kind == LinkKind.Tracker ? $"{Site}/tracker/{Id}" : null;

        private static readonly string[] CheeseHosts = { "cheesetrackers.theincrediblewheelofchee.se" };

        public static ApTrackerLink Parse(string text, out string error)
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
            if (Array.Exists(CheeseHosts, h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)))
            {
                error = "That's a Cheese Tracker link, not an Archipelago one.";
                return null;
            }
            if (SphereSite.IsSphereSiteHost(uri.Host))
            {
                error = "That's a spheretracker.de link, not an Archipelago one.";
                return null;
            }
            string site = PoliteHttp.NormalizeSite(uri.GetLeftPart(UriPartial.Authority));
            if (site == null)
            {
                error = "Atlas only uses secure (https) links for other sites.";
                return null;
            }
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                error = "Use the room's link (…/room/…) or its tracker (…/tracker/…).";
                return null;
            }
            LinkKind kind;
            switch (segments[0])
            {
                case "room": kind = LinkKind.Room; break;
                case "tracker":
                case "sphere_tracker":
                case "generic_tracker": kind = LinkKind.Tracker; break;
                default:
                    error = "Use the room's link (…/room/…) or its tracker (…/tracker/…).";
                    return null;
            }
            if (!CtLink.IsId(segments[1]))
            {
                error = "The id in that link doesn't look right; copy the whole link again.";
                return null;
            }
            return new ApTrackerLink { Kind = kind, Site = site, Id = segments[1] };
        }
    }

    /// <summary>
    /// spheretracker.de: a site where a multiworld's host creates a sphere tracker room (it needs a login) and shares its
    /// link. Atlas only reads rooms; it never creates one or asks the site to refresh one.
    /// </summary>
    public static class SphereSite
    {
        public const string Host = "spheretracker.de";

        /// <summary>The self-test's stand-in for spheretracker.de (a site on this PC); null otherwise.</summary>
        internal static string TestSite { get; set; }

        public static bool IsSphereSiteHost(string host) =>
            string.Equals(host, Host, StringComparison.OrdinalIgnoreCase) || string.Equals(host, "www." + Host, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A room link as Atlas reads it: the room's open locations ("https://spheretracker.de/room/&lt;id&gt;/all", whichever of
        /// the room's pages was pasted), its room id, or null and why not. Only room links are accepted, and never with
        /// "?refresh": Atlas never asks the site to refresh a room.
        /// </summary>
        public static string Normalize(string text, out string error, out string roomId)
        {
            error = null;
            roomId = null;
            text = (text ?? "").Trim();
            if (text.Length == 0) { error = "Paste the room link your host shared."; return null; }
            if (!text.Contains("://")) text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                error = "That isn't a web link.";
                return null;
            }
            bool test = TestSite != null && PoliteHttp.SameSite(uri.GetLeftPart(UriPartial.Authority), TestSite);
            if (!test && !IsSphereSiteHost(uri.Host))
            {
                error = "That isn't a spheretracker.de link. Paste the room link your host shared (spheretracker.de/room/…).";
                return null;
            }
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || !segments[0].Equals("room", StringComparison.OrdinalIgnoreCase))
            {
                error = "That isn't a spheretracker.de room. Paste the room link your host created and shared (spheretracker.de/room/…).";
                return null;
            }
            roomId = segments[1];
            // spheretracker.de is https only; an http link to it is read over https.
            return (test ? PoliteHttp.NormalizeSite(TestSite) : "https://" + Host) + "/room/" + Uri.EscapeDataString(Uri.UnescapeDataString(roomId)) + "/all";
        }
    }
}
