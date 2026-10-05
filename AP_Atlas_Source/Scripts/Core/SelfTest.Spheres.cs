using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.CheeseTracker;
using AP_Atlas.Core.Spheres;

namespace AP_Atlas.Core
{
    public static partial class SelfTest
    {
        // =====================================================================
        // Sphere Tracker: only the spheretracker.de room a multiworld's host created (against a fake site on this PC;
        // the real sites are never contacted)
        // =====================================================================

        private const string TestRoomId = "QrStUvWxYzAbCdEfGhIjKg";
        private const string OtherRoomId = "ZyXwVuTsRqPoNmLkJiHgFA";

        /// <summary>A room page laid out like spheretracker.de's open-locations page (names made up).</summary>
        private static string SphereRoomPage(string trackerId, string creator, string rows = null) =>
            "<!doctype html><html><head><title>Room | Sphere Tracker</title><style>table{}</style></head><body>" +
            "<header><h1 class=\"topbar-title\">Sphere Tracker</h1></header><script>var t = '<table><tr><td>x</td></tr></table>';</script>" +
            "<section class=\"card\"><a href=\"/room/x/claim\" class=\"btn nav\">Claim your Slots</a>" +
            "<div><h2 style=\"margin:0;\">Count Down #7</h2></div>" +
            (creator == null ? "" : $"<p style=\"margin:8px 0 0;\">\n      Created by {creator}\n    </p>") +
            $"<p><a href=\"https://archipelago.gg/tracker/{trackerId}\" target=\"_blank\">Go to Tracker</a></p>" +
            "<span>Last update: <strong id=\"last-room-update-label\" data-utc=\"2026-10-05T05:57:17.043736+00:00\" style=\"color:#e2e8f0;\">-</strong></span>" +
            $"</section><a href=\"/room/{OtherRoomId}/all\">Show Open Locations</a>" +
            "<table class=\"room-table\"><tr><th>Sphere</th><th>Finder</th><th> Location </th><th> Game </th></tr>" +
            (rows ??
             "<tr data-row-id=\"1\"><td>84</td><td>Burner</td><td><div><span>Waterfront &amp; Falling</span></div></td><td>Burnout 3</td></tr>" +
             "<tr data-row-id=\"2\"><td>91</td><td>Me</td><td><div><span>FSBT: Armor of the Sun - crow for Siegbräu</span></div></td><td>Dark Souls III</td></tr>" +
             "<tr data-row-id=\"3\"><td>93</td><td>Me</td><td><div><span>Firelink: Me2's gift</span></div></td><td>Dark Souls III</td></tr>" +
             "<tr data-row-id=\"4\"><td>95</td><td>Me2</td><td><div><span>A gift from Me</span></div></td><td>Dark Souls III</td></tr>") +
            "</table><h3>Summary</h3><table><tr><th>Sphere</th><th>Unfound</th><th>Found</th><th>Total</th></tr>" +
            "<tr><td>Sphere 1 - Sphere 83</td><td>None</td><td>96177</td><td>96177</td></tr>" +
            "<tr><td><button type=\"button\" class=\"summary-sphere-button\" data-slot-counts-url=\"/room/x/sphere/84/slot-counts\">Sphere 84</button></td><td>1</td><td>232</td><td>233</td></tr>" +
            "<tr><td>Sphere 91</td><td>2</td><td>64</td><td>66</td></tr></table>" +
            "<h3>Sphere</h3><table><tr><th>Slot</th><th><button type=\"button\">Open Locations</button></th></tr></table></body></html>";

        private static void SphereLinksParse()
        {
            // Archipelago links (Atlas reads them from Cheese Tracker, to know which multiworld a room should be for).
            var rooms = new (string Text, ApTrackerLink.LinkKind? Kind, string Site)[]
            {
                ($"https://archipelago.gg/room/{TestRoomId}", ApTrackerLink.LinkKind.Room, "https://archipelago.gg"),
                ($"archipelago.gg/tracker/{TestTrackerId}/0/3", ApTrackerLink.LinkKind.Tracker, "https://archipelago.gg"),
                ($"https://ap.example.org:8080/tracker/{TestTrackerId}", ApTrackerLink.LinkKind.Tracker, "https://ap.example.org:8080"),
                ($"http://archipelago.gg/room/{TestRoomId}", null, null), // plain http to a real site
                ($"https://archipelago.gg/seed/{TestRoomId}", null, null),
                ("https://archipelago.gg/room/short", null, null),
                ($"{CheeseClient.DefaultInstance}/tracker/{TestTrackerId}", null, null),
                ($"https://spheretracker.de/room/{TestTrackerId}", null, null),
                ("", null, null)
            };
            foreach (var (text, kind, site) in rooms)
            {
                var link = ApTrackerLink.Parse(text, out string error);
                if (kind == null) Expect(link == null && error != null, $"\"{text}\" was accepted as an Archipelago link");
                else Expect(link != null && link.Kind == kind && link.Site == site, $"\"{text}\" read as {link?.Kind.ToString() ?? error} on {link?.Site}");
            }
            Expect(ApTrackerLink.Parse($"https://archipelago.gg/tracker/{TestTrackerId}/0/3", out _).TrackerUrl == $"https://archipelago.gg/tracker/{TestTrackerId}",
                "a tracker link's plain form is wrong");

            // spheretracker.de: room links only, whichever of the room's pages, read as its open locations (never ?refresh).
            const string Room = "cwEj7kIAtD566-NXUU1xj0vo2oPYBY3h";
            string open = $"https://spheretracker.de/room/{Room}/all";
            var sites = new (string Text, string Url)[]
            {
                ($"https://spheretracker.de/room/{Room}", open),
                ($"https://spheretracker.de/room/{Room}/all", open),
                ($"https://spheretracker.de/room/{Room}/collected", open),
                ($"www.spheretracker.de/room/{Room}/", open),
                ($"http://spheretracker.de/room/{Room}/all", open), // read over https
                ($"https://spheretracker.de/room/{Room}/all?refresh=1", open),
                ($"spheretracker.de/room/{Room}?share=abc&Refresh=1", open),
                ("https://spheretracker.de/dashboard", null),
                ("https://spheretracker.de/dashboard/open", null),
                ("https://spheretracker.de/settings", null),
                ("https://spheretracker.de/auth/login?next=/settings", null),
                ("https://spheretracker.de/room", null),
                ("https://spheretracker.de/", null),
                ($"https://spheretracker.de/tracker/{TestTrackerId}", null),
                ($"https://archipelago.gg/room/{TestRoomId}", null),
                ($"https://archipelago.gg/sphere_tracker/{TestTrackerId}", null),
                ("", null)
            };
            foreach (var (text, url) in sites)
            {
                string got = SphereSite.Normalize(text, out string error, out string roomId);
                if (url == null) Expect(got == null && error != null, $"\"{text}\" was accepted as a spheretracker.de room");
                else Expect(got == url && roomId == Room, $"\"{text}\" read as {got ?? error} (room {roomId})");
            }
        }

        private static void SpherePagesParse()
        {
            // The room's open-locations page: its name, creator, last update, tracker, and tables.
            var data = SphereParser.ParseSphereSite(SphereRoomPage(TestTrackerId, "HostPerson"), out string error);
            Expect(data != null && data.Tables.Count == 3, $"the room page wasn't read ({error}, {data?.Tables.Count} tables)");
            Expect(data.RoomName == "Count Down #7" && data.Creator == "HostPerson", $"the room's name or creator was read as \"{data.RoomName}\" / \"{data.Creator}\"");
            Expect(data.UpdatedUtc == new DateTime(2026, 10, 5, 5, 57, 17, 43, DateTimeKind.Utc).AddTicks(7360), $"the room's last update was read as {data.UpdatedUtc:o}");
            Expect(data.TrackerIds.SequenceEqual(new[] { TestTrackerId }), "the tracker the room is for wasn't found (or a room link was taken for one)");
            var open = SphereTable.OpenLocations(data);
            var summary = SphereTable.Summary(data);
            Expect(open == data.Tables[0] && summary == data.Tables[1], "the open locations or the summary weren't recognised");
            Expect(open.Rows[0][2] == "Waterfront & Falling" && open.Rows[1][2] == "FSBT: Armor of the Sun - crow for Siegbräu", "a location was read wrongly");
            var (mine, earliest) = SphereTable.OpenFor(open, "me");
            Expect(mine.Count == 2 && earliest == 91 && mine.All(r => r[1] == "Me"), "a slot's open locations are wrong (by its Finder column, any case)");
            Expect(SphereTable.EarliestOpen(summary) == (84, 1), "the multiworld's earliest open sphere is wrong");
            Expect(SphereParser.ParseSphereSite(SphereRoomPage(TestTrackerId, null), out _)?.Creator == "", "a creator was found on a page that names none");

            // Tables in general: titled by the heading above (or their caption); a row may start with a label.
            string room = "<html><head><title>Room</title></head><body><h2>Spheres</h2>" +
                          "<table><thead><tr><th>Sphere</th><th>Player</th></tr></thead><tbody><tr><td>1</td><td>Me</td></tr><tr><td>2</td><td>You</td></tr></tbody></table>" +
                          "<h3>Ignored heading</h3><table><caption>Progress</caption><tr><th>Name</th></tr><tr><th>Me</th><td>5</td></tr></table></body></html>";
            data = SphereParser.ParseSphereSite(room, out error);
            Expect(data != null && data.Tables.Count == 2 && data.Tables[0].Title == "Spheres" && data.Tables[0].Rows.Count == 2, "a page's tables were read wrongly: " + error);
            Expect(data.Tables[1].Title == "Progress" && data.Tables[1].Columns.SequenceEqual(new[] { "Name", "Column 2" }) && data.Tables[1].Rows[0].SequenceEqual(new[] { "Me", "5" }),
                "a captioned table with a row label was read wrongly");
            Expect(SphereParser.TrackerIdsIn("/tracker/short /tracker/AbCdEfGhIjKlMnOpQrStUz /tracker/AbCdEfGhIjKlMnOpQrStUwX").Count == 0,
                "something that isn't an Archipelago tracker id was taken for one");
            Expect(SphereParser.ParseSphereSite("<html><body><p>Room not created. Please log in to create this Room</p></body></html>", out error) == null &&
                   error.Contains("hasn't been created"), "a room nobody created was read as a room");
            Expect(SphereParser.ParseSphereSite("<html><body><a href=\"/auth/login\">Login</a></body></html>", out error) == null && error.Contains("logging in"),
                "a login page wasn't recognised");
        }

        private static void SphereTableRules()
        {
            // A slot's rows: its whole name, in any case.
            var me = SphereTable.SlotPattern("DarkTuttle1");
            Expect(SphereTable.Names(new[] { "DarkTuttle1" }, me) && SphereTable.Names(new[] { "x", "darktuttle1 (Dark Souls II)" }, me) &&
                   SphereTable.Names(new[] { "Sphere 3: Bob, DarkTuttle1" }, me), "a row naming the slot wasn't found");
            Expect(!SphereTable.Names(new[] { "DarkTuttle10" }, me) && !SphereTable.Names(new[] { "MyDarkTuttle1" }, me) && !SphereTable.Names(new string[] { null, "" }, me),
                "a row naming another slot was taken for this one");
            var odd = SphereTable.SlotPattern("[DS] Tuttle (2)");
            Expect(SphereTable.Names(new[] { "[DS] Tuttle (2)" }, odd) && !SphereTable.Names(new[] { "[DS] Tuttle (2)x" }, odd), "a slot name with symbols didn't match itself only");
            // With a Finder column, only that column counts; without one, any cell naming the slot.
            var withFinder = new PageTable { Columns = new List<string> { "Sphere", "Finder", "Location" } };
            Expect(SphereTable.IsSlotRow(withFinder, new List<string> { "1", "darktuttle1", "x" }, "DarkTuttle1", me) &&
                   !SphereTable.IsSlotRow(withFinder, new List<string> { "1", "Bob", "Gift from DarkTuttle1" }, "DarkTuttle1", me), "a slot's rows weren't found by its Finder column");
            var plain = new PageTable { Columns = new List<string> { "Sphere", "Who" } };
            Expect(SphereTable.IsSlotRow(plain, new List<string> { "1", "Bob and DarkTuttle1" }, "DarkTuttle1", me), "a slot's rows weren't found without a Finder column");
            // Sphere numbers in cells.
            Expect(SphereTable.SphereNumber("91") == 91 && SphereTable.SphereNumber("Sphere 84") == 84 && SphereTable.SphereNumber("Sphere 86 - Sphere 90") == 86 &&
                   SphereTable.SphereNumber("None") == null && SphereTable.SphereNumber(null) == null, "a sphere number was read wrongly");
            Expect(SphereTable.EarliestOpen(new PageTable { Columns = new List<string> { "Sphere", "Unfound" }, Rows = new List<List<string>> { new() { "Sphere 1 - Sphere 9", "None" } } }) == null,
                "an earliest open sphere was found where none is open");

            // The same person on Cheese Tracker and spheretracker.de: names ignoring case and spaces.
            Expect(SphereService.SameName("ElderWisp", "elderwisp") && SphereService.SameName("Elder Wisp", "elderwisp") &&
                   !SphereService.SameName("ElderWisp", "elderwisp2") && !SphereService.SameName("", "") && !SphereService.SameName("x", null), "names were compared wrongly");

            // Natural order (numbers by value, letters ignoring case), shared with Cheese Tracker.
            int N(string a, string b) => Math.Sign(CheeseTable.NaturalCompare(a, b));
            Expect(N("Chest 9", "chest 10") < 0 && N("a02", "a2") == 0 && N("a010", "a9") > 0 && N("x", "x1") < 0 && N("B", "a") > 0 && N("", "a") < 0 &&
                   N("Room 3b", "Room 3a") > 0 && N("v1.10", "v1.9") > 0 && N(null, null) == 0, "natural order changed");

            // The room's tables: numbers sort as numbers, text naturally; search.
            var table = new PageTable
            {
                Columns = new List<string> { "Player", "Sphere" },
                Rows = new List<List<string>> { new() { "Chest 10", "10" }, new() { "Chest 9", "9" }, new() { "Bow", "9.5" } }
            };
            Expect(SphereTable.FilterAndSort(table, "", 1, false).Select(r => r[0]).SequenceEqual(new[] { "Chest 9", "Bow", "Chest 10" }), "numbers didn't sort as numbers");
            Expect(SphereTable.FilterAndSort(table, "", 1, true).Select(r => r[0]).SequenceEqual(new[] { "Chest 10", "Bow", "Chest 9" }), "reversed sorting");
            Expect(SphereTable.FilterAndSort(table, "", 0, false).Select(r => r[0]).SequenceEqual(new[] { "Bow", "Chest 9", "Chest 10" }), "text didn't sort naturally");
            Expect(SphereTable.FilterAndSort(table, "chest", -1, false).Count == 2, "searching");
        }

        private static async Task SpheresHostRoomOnly()
        {
            PoliteHttp.Spacing = TimeSpan.Zero;
            PoliteHttp.ResetForTests();
            using var server = new FakeWebServer();
            SphereSite.TestSite = server.Site;
            string Path(string room) => $"/room/{room}/all";
            server.Page(Path("organizer"), 200, "text/html", SphereRoomPage(TestTrackerId, "HostPerson")); // created by the multiworld's organizer
            server.Page(Path("someone"), 200, "text/html", SphereRoomPage(TestTrackerId, "SomePlayer")); // created by someone else
            server.Page(Path("nobody"), 200, "text/html", SphereRoomPage(TestTrackerId, null)); // the page doesn't say who
            server.Page(Path("other"), 200, "text/html", SphereRoomPage(OtherRoomId, "HostPerson")); // another multiworld's room
            server.Page(Path("uncreated"), 302, "text/html", "", "Location: /dashboard\r\n"); // nobody created it: the site sends you to its dashboard
            server.Page(Path("notcreated"), 200, "text/html", "<html><body><p>Room not created. Please log in to create this Room</p></body></html>");
            server.Page(Path("unreadable"), 200, "text/html", "<html><body><p>Created by HostPerson</p><p>Loading…</p></body></html>"); // a layout Atlas can't read (yet)
            var settings = new AppSettings();
            var profile = new MultiworldProfile { Name = "Test MW", Slots = new List<string> { "Me" } };
            var profiles = new List<MultiworldProfile> { profile };
            // What Cheese Tracker says about the multiworld: its Archipelago tracker and its organizer.
            string tracker = $"https://archipelago.gg/tracker/{TestTrackerId}", organizer = "hostperson";
            SphereService New() => new SphereService(() => profiles, () => Enumerable.Empty<SlotTrackerControl>(), () => { }, p => (tracker, organizer));
            var spheres = New();
            RaceRules.Initialize(settings);
            try
            {
                // Nothing linked: nothing read.
                spheres.Watch(profile.Id);
                Expect(await spheres.RefreshAsync(profile.Id) == null && server.RequestCount == 0 && spheres.ViewOf(profile.Id).Room == null, "something was read with no room linked");

                // Only room links: anything else is refused before asking the site.
                foreach (var link in new[] { $"{server.Site}/dashboard", $"{server.Site}/", $"https://archipelago.gg/room/{TestRoomId}", "https://example.com/room/x" })
                    Expect((await spheres.CheckRoomAsync(profile.Id, link)).Error != null, $"\"{link}\" was taken as a room");
                Expect(server.RequestCount == 0, "the site was asked about a link that isn't a room");

                // The room must exist and not be another multiworld's.
                foreach (var room in new[] { "uncreated", "notcreated", "missing" })
                    Expect((await spheres.CheckRoomAsync(profile.Id, $"{server.Site}/room/{room}")).Error != null, $"the room \"{room}\" was taken");
                var check = await spheres.CheckRoomAsync(profile.Id, $"{server.Site}/room/other");
                Expect(check.Error != null && check.Error.Contains("another multiworld"), "another multiworld's room was taken: " + check.Error);
                Expect(spheres.LinkRoom(check, hostConfirmed: true) != null && profile.SphereTrackerUrl == "", "a refused room was linked when the user confirmed it");

                // Created by the multiworld's organizer on Cheese Tracker: taken as the host's at once (read once, as its open locations, never ?refresh).
                int before = server.RequestCount;
                check = await spheres.CheckRoomAsync(profile.Id, $"{server.Site}/room/organizer?refresh=1");
                Expect(check.Error == null && check.ByOrganizer && check.Creator == "HostPerson", $"a room by the organizer wasn't recognised ({check.Error}, by \"{check.Creator}\")");
                Expect(spheres.LinkRoom(check, hostConfirmed: false) == null && profile.SphereTrackerUrl == $"{server.Site}{Path("organizer")}", "the organizer's room wasn't linked");
                Expect(server.RequestCount == before + 1 && server.Requests.All(r => !r.Path.Contains("refresh", StringComparison.OrdinalIgnoreCase)),
                    "linking read the room more than once, or asked the site to refresh it");
                var view = spheres.ViewOf(profile.Id);
                Expect(view.Room?.Data?.Creator == "HostPerson" && view.Organizer == "hostperson" && SphereTable.OpenFor(SphereTable.OpenLocations(view.Room.Data), "Me").Rows.Count == 2,
                    "the linked room wasn't read");
                spheres.Watch(profile.Id);
                await Task.Delay(150);
                Expect(server.RequestCount == before + 1, "a room read moments ago was read again");
                Expect(spheres.ReadRecently(profile.Id) && await spheres.RefreshAsync(profile.Id) == null && server.RequestCount == before + 1,
                    "Refresh read the room again under a minute after reading it");

                // Created by someone else, by nobody named, or with no organizer known: only when the user confirms the host made it.
                check = await spheres.CheckRoomAsync(profile.Id, $"{server.Site}/room/someone");
                string error = spheres.LinkRoom(check, hostConfirmed: false);
                Expect(!check.ByOrganizer && error != null && error.Contains("SomePlayer") && profile.SphereTrackerUrl.EndsWith(Path("organizer")),
                    "a room by someone else was linked without the user confirming they're the host: " + error);
                Expect(spheres.LinkRoom(check, hostConfirmed: true) == null && profile.SphereTrackerUrl.EndsWith(Path("someone")), "a room the user confirmed wasn't linked");
                check = await spheres.CheckRoomAsync(profile.Id, $"{server.Site}/room/nobody");
                Expect(check.Creator == "" && !check.ByOrganizer && spheres.LinkRoom(check, false) != null && spheres.LinkRoom(check, true) == null,
                    "a room that doesn't name its creator wasn't left to the user");
                organizer = null;
                check = await spheres.CheckRoomAsync(profile.Id, $"{server.Site}/room/organizer");
                Expect(!check.ByOrganizer && check.Organizer == null && spheres.LinkRoom(check, false) != null, "a room was taken as the host's with no organizer known");
                organizer = "hostperson";
                // A room Atlas can't read yet still links (it opens in the browser), its problem shown.
                check = await spheres.CheckRoomAsync(profile.Id, $"{server.Site}/room/unreadable");
                Expect(check.Error == null && check.ByOrganizer && spheres.LinkRoom(check, false) == null && spheres.ViewOf(profile.Id).Room.Problem != null,
                    "a room Atlas can't read yet wasn't linked with its problem shown");
                // When Cheese Tracker doesn't say which tracker the multiworld has, a room for another one can't be told apart.
                tracker = null;
                Expect((await spheres.CheckRoomAsync(profile.Id, $"{server.Site}/room/other")).Error == null, "a room was refused without knowing this multiworld's tracker");
                tracker = $"https://archipelago.gg/tracker/{TestTrackerId}";
                Expect(spheres.LinkRoom(await spheres.CheckRoomAsync(profile.Id, $"{server.Site}/room/organizer"), false) == null, "relinking the organizer's room failed");

                // Race mode hides the room and reads nothing: always on, or following a race room.
                settings.RaceMode = RaceModeSetting.AlwaysOn;
                before = server.RequestCount;
                Expect(spheres.ViewOf(profile.Id) is { Room: null, HiddenBecause: not null }, "the room showed while race mode is always on");
                Expect(await spheres.RefreshAsync(profile.Id, tryNow: true) != null, "Refresh didn't say race mode hides the room");
                spheres.Watch(profile.Id);
                await Task.Delay(150);
                Expect(server.RequestCount == before, "the room was read in race mode");
                settings.RaceMode = RaceModeSetting.FollowServer;
                profile.RaceRoom = true;
                Expect(spheres.ViewOf(profile.Id) is { Room: null, HiddenBecause: not null }, "the room showed in a race room");
                settings.RaceMode = RaceModeSetting.Off;
                Expect(spheres.ViewOf(profile.Id) is { Room: not null, HiddenBecause: null }, "the room stayed hidden with race mode off");
                settings.RaceMode = RaceModeSetting.FollowServer;
                profile.RaceRoom = false;

                // The last read is kept for next time (a recent one isn't read again after a restart); unlinking drops it.
                await spheres.WhenIdleAsync(profile.Id);
                var reread = New();
                reread.ViewOf(profile.Id);
                await reread.WhenIdleAsync(profile.Id);
                var kept = reread.ViewOf(profile.Id).Room?.Data;
                Expect(kept?.Tables.Count == 3 && kept.Creator == "HostPerson" && kept.RoomName == "Count Down #7" && kept.UpdatedUtc != null, "the last read wasn't kept whole");
                before = server.RequestCount;
                reread.Watch(profile.Id);
                await Task.Delay(150);
                Expect(server.RequestCount == before, "a room read moments before a restart was read again");
                spheres.UnlinkSphereSite(profile.Id);
                Expect(spheres.ViewOf(profile.Id).Room == null && profile.SphereTrackerUrl == "", "unlinking didn't stop using the room");
                profile.SphereTrackerUrl = $"{server.Site}{Path("organizer")}"; // as if linked again elsewhere: what was read before must be gone
                var afterUnlink = New();
                afterUnlink.ViewOf(profile.Id);
                await afterUnlink.WhenIdleAsync(profile.Id);
                Expect(afterUnlink.ViewOf(profile.Id).Room?.Data == null, "a room's last read was kept after unlinking it");

                // A site in trouble is left alone; a read meanwhile ends at once and must not leave the room "reading".
                server.Page(Path("organizer"), 503, "text/html", "Busy", "Retry-After: 600\r\n");
                Expect(await spheres.RefreshAsync(profile.Id) != null && PoliteHttp.WaitingFor(server.Site) != null, "a site in trouble wasn't reported, or wasn't left alone");
                spheres.ForgetProfile(profile.Id); // a fresh read: due at once
                before = server.RequestCount;
                spheres.Watch(profile.Id);
                var waiting = spheres.ViewOf(profile.Id).Room;
                Expect(server.RequestCount == before && !waiting.Busy && waiting.Problem != null, "a read while the site is left alone stuck as \"reading\" (or asked the site)");
                server.Page(Path("organizer"), 200, "text/html", SphereRoomPage(TestTrackerId, "HostPerson"));
                PoliteHttp.StopWaiting(server.Site);
                Expect(await spheres.RefreshAsync(profile.Id) == null && spheres.ViewOf(profile.Id).Room?.Data != null, "reading didn't work again once the wait ended");
                Expect(server.Requests.All(r => r.Method == "GET"), "Atlas sent something other than reads");
            }
            finally
            {
                SphereSite.TestSite = null;
                RaceRules.Initialize(null);
                PoliteHttp.Spacing = TimeSpan.FromSeconds(1);
                PoliteHttp.ResetForTests();
                spheres.ForgetProfile(profile.Id);
            }
        }

        private static async Task SpheresLargePagesAndStalls()
        {
            PoliteHttp.Spacing = TimeSpan.Zero;
            PoliteHttp.ResetForTests();
            long maxBefore = SphereService.MaxPageBytes, largeBefore = SphereService.LargePageBytes, hugeBefore = SphereService.HugePageBytes;
            var timeoutBefore = SphereService.PageTimeout;
            using var server = new FakeWebServer();
            SphereSite.TestSite = server.Site;
            string path = $"/room/{TestTrackerId}/all";
            var profile = new MultiworldProfile { Name = "Big MW", Slots = new List<string> { "Me" }, SphereTrackerUrl = server.Site + path };
            var profiles = new List<MultiworldProfile> { profile };
            var spheres = new SphereService(() => profiles, () => Enumerable.Empty<SlotTrackerControl>(), () => { }, p => (null, null));
            RaceRules.Initialize(new AppSettings());
            string page = SphereRoomPage(TestTrackerId, "HostPerson",
                string.Concat(Enumerable.Range(0, 400).Select(i => $"<tr><td>{1 + i / 100}</td><td>Player{i}</td><td>Spot {i}</td><td>Clique</td></tr>")));
            try
            {
                // A page larger than Atlas reads is reported and not tried again on its own; Refresh tries again.
                SphereService.MaxPageBytes = page.Length / 2;
                server.Page(path, 200, "text/html", page);
                spheres.Watch(profile.Id);
                await spheres.WhenIdleAsync(profile.Id);
                var view = spheres.ViewOf(profile.Id).Room;
                Expect(server.RequestCount == 1 && view.Data == null && view.Problem?.Contains("more than Atlas reads") == true, "a page that's too large wasn't reported: " + view.Problem);
                spheres.Watch(profile.Id);
                await Task.Delay(150);
                Expect(server.RequestCount == 1, "a page that's too large was tried again on its own");
                Expect(await spheres.RefreshAsync(profile.Id) != null && server.RequestCount == 2, "Refresh didn't try a page that was too large again");

                // The same without its size given up front (as a compressed answer arrives): reading stops at the limit.
                server.Page(path, 200, "text/html", page, noLength: true);
                spheres.ForgetProfile(profile.Id);
                spheres.Watch(profile.Id);
                await spheres.WhenIdleAsync(profile.Id);
                view = spheres.ViewOf(profile.Id).Room;
                Expect(server.RequestCount == 3 && view.Data == null && view.Problem?.Contains("more than Atlas reads") == true,
                    "a page too large, with no size given, was read anyway: " + view.Problem);

                // A large page is read less often.
                SphereService.MaxPageBytes = maxBefore;
                SphereService.LargePageBytes = page.Length / 4;
                SphereService.HugePageBytes = page.Length * 4;
                spheres.ForgetProfile(profile.Id);
                spheres.Watch(profile.Id);
                await spheres.WhenIdleAsync(profile.Id);
                view = spheres.ViewOf(profile.Id).Room;
                Expect(SphereTable.OpenLocations(view.Data)?.Rows.Count == 400 && view.ReadEvery == TimeSpan.FromMinutes(30) && view.PageBytes >= page.Length,
                    $"a large page wasn't read less often (every {view.ReadEvery.TotalMinutes} min, {view.PageBytes} bytes, {view.Problem})");

                // A stalled answer is cut off at the time limit, the site is left alone a while, and nothing stays stuck.
                SphereService.PageTimeout = TimeSpan.FromSeconds(2);
                server.Stall(path, page.Substring(0, 300));
                spheres.ForgetProfile(profile.Id);
                var clock = Stopwatch.StartNew();
                string error = await spheres.RefreshAsync(profile.Id);
                clock.Stop();
                view = spheres.ViewOf(profile.Id).Room;
                Expect(error != null && !view.Busy && clock.Elapsed < TimeSpan.FromSeconds(10), $"a stalled answer wasn't cut off ({clock.Elapsed.TotalSeconds:0.0} s: {error})");
                Expect(PoliteHttp.WaitingFor(server.Site) != null, "a site that stalled wasn't left alone for a while");
                server.Page(path, 200, "text/html", page);
                PoliteHttp.StopWaiting(server.Site);
                spheres.ForgetProfile(profile.Id);
                Expect(await spheres.RefreshAsync(profile.Id) == null && SphereTable.OpenLocations(spheres.ViewOf(profile.Id).Room?.Data)?.Rows.Count == 400,
                    "reading didn't work again after a stalled answer");
            }
            finally
            {
                SphereService.MaxPageBytes = maxBefore;
                SphereService.LargePageBytes = largeBefore;
                SphereService.HugePageBytes = hugeBefore;
                SphereService.PageTimeout = timeoutBefore;
                SphereSite.TestSite = null;
                RaceRules.Initialize(null);
                PoliteHttp.Spacing = TimeSpan.FromSeconds(1);
                PoliteHttp.ResetForTests();
                spheres.ForgetProfile(profile.Id);
            }
        }

        /// <summary>A small web host on 127.0.0.1 serving fixed pages, recording each request.</summary>
        private sealed class FakeWebServer : IDisposable
        {
            public sealed class Request
            {
                public string Method, Path;
            }

            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly List<Request> _requests = new List<Request>();
            private readonly Dictionary<string, (int Status, string Type, string Body, string Extra, bool NoLength, bool Stall)> _pages =
                new Dictionary<string, (int, string, string, string, bool, bool)>();

            public string Site { get; }

            public FakeWebServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Site = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
                _ = AcceptLoop();
            }

            /// <summary>Serves a page; <paramref name="noLength"/> leaves its size out (it ends when the connection closes).</summary>
            public void Page(string path, int status, string type, string body, string extraHeaders = "", bool noLength = false)
            {
                lock (_requests) _pages[path] = (status, type, body, extraHeaders, noLength, false);
            }

            /// <summary>Sends the start of a page, then nothing more until the server is disposed.</summary>
            public void Stall(string path, string start)
            {
                lock (_requests) _pages[path] = (200, "text/html", start, "", false, true);
            }

            public List<Request> Requests
            {
                get { lock (_requests) return _requests.ToList(); }
            }

            public int RequestCount
            {
                get { lock (_requests) return _requests.Count; }
            }

            private async Task AcceptLoop()
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }
                    _ = Task.Run(() => HandleAsync(client));
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
                            int n = await stream.ReadAsync(chunk).ConfigureAwait(false);
                            if (n <= 0) return;
                            buffer.Write(chunk, 0, n);
                            headerEnd = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        }
                        var first = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd).Split("\r\n")[0].Split(' ');
                        (int Status, string Type, string Body, string Extra, bool NoLength, bool Stall) page;
                        lock (_requests)
                        {
                            _requests.Add(new Request { Method = first[0], Path = first[1] });
                            string path = first[1].Split('?')[0];
                            page = _pages.TryGetValue(path, out var p) ? p : (404, "text/html", "Not Found", "", false, false);
                        }
                        byte[] payload = Encoding.UTF8.GetBytes(page.Body ?? "");
                        string length = page.NoLength ? "" : $"Content-Length: {(page.Stall ? payload.Length * 10 : payload.Length)}\r\n";
                        string head = $"HTTP/1.1 {page.Status} Test\r\nContent-Type: {page.Type}; charset=utf-8\r\n{length}Connection: close\r\n{page.Extra}\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
                        await stream.WriteAsync(payload).ConfigureAwait(false);
                        if (page.Stall) await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // A test that ended early closes connections mid-request.
                }
            }

            public void Dispose()
            {
                _stop.Cancel();
                try { _listener.Stop(); } catch { }
            }
        }
    }
}
