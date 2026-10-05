#nullable disable
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
using AP_Atlas.Core.Testing;
using Newtonsoft.Json;

namespace AP_Atlas.Core
{
    public static partial class SelfTest
    {
        // =====================================================================
        // Cheese Tracker (against a fake Cheese Tracker on this PC; the real site is never contacted)
        // =====================================================================

        private const string TestTrackerId = FakeCheeseServer.TrackerId;

        private static void CheeseLinksParse()
        {
            string site = CheeseClient.DefaultInstance;
            var cases = new (string Text, CtLink.LinkKind? Kind, string Url)[]
            {
                ($"{site}/tracker/{TestTrackerId}", CtLink.LinkKind.CheeseTracker, $"{site}/tracker/{TestTrackerId}"),
                ($"cheesetrackers.theincrediblewheelofchee.se/tracker/{TestTrackerId}?x=1#top", CtLink.LinkKind.CheeseTracker, $"{site}/tracker/{TestTrackerId}"),
                ($"https://archipelago.gg/room/{TestTrackerId}", CtLink.LinkKind.ApRoom, $"https://archipelago.gg/room/{TestTrackerId}"),
                ($"https://archipelago.gg/tracker/{TestTrackerId}/0/3", CtLink.LinkKind.ApTracker, $"https://archipelago.gg/tracker/{TestTrackerId}"),
                ($"http://archipelago.gg/room/{TestTrackerId}", null, null), // plain http to a real site
                ("https://archipelago.gg/room/AbCdEfGhIjKlMnOpQrStU", null, null), // too short
                ("https://archipelago.gg/room/AbCdEfGhIjKlMnOpQrStUz", null, null), // not a 16-byte id
                ($"https://archipelago.gg/seed/{TestTrackerId}", null, null),
                ("not a link", null, null),
                ("", null, null)
            };
            foreach (var (text, kind, url) in cases)
            {
                var link = CtLink.Parse(text, site, out string error);
                if (kind == null) Expect(link == null && error != null, $"\"{text}\" was accepted");
                else Expect(link != null && link.Kind == kind && link.Url == url, $"\"{text}\" read as {link?.Kind.ToString() ?? error}, {link?.Url}");
            }
        }

        private static void CheeseRowsMatch()
        {
            var t = new CtTracker
            {
                Games = new List<CtGame>
                {
                    new CtGame { Id = 1, Position = 1, Name = "Alice", Game = "Dark Souls III" },
                    new CtGame { Id = 2, Position = 2, Name = "Bob", Game = "Hollow Knight" },
                    new CtGame { Id = 3, Position = 3, Name = "Twin", Game = "Celeste" },
                    new CtGame { Id = 4, Position = 4, Name = "Twin", Game = "Celeste" }
                }
            };
            CtGame M(int number, string name, string game) => CheeseTrackerService.MatchRow(t, number, name, game, out _);
            Expect(M(1, "Alice", "Dark Souls III")?.Id == 1, "a slot wasn't found by its number");
            Expect(M(2, "Renamed", "Hollow Knight")?.Id == 2, "an aliased slot wasn't found by its number");
            Expect(M(2, "Bob", "Celeste") == null, "a number whose game differs was taken as a match");
            Expect(M(0, "Bob", "Hollow Knight")?.Id == 2, "a slot wasn't found by name and game while its number is unknown");
            Expect(M(0, "Twin", "Celeste") == null, "a name on two rows was guessed");
            Expect(M(4, "Twin", "Celeste")?.Id == 4, "the number didn't settle two rows with one name");
            Expect(M(0, "Nobody", "Celeste") == null, "an unknown name matched");
        }

        private static void CheeseSuggestions()
        {
            var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            CtGame Row(string progression, string completion = "incomplete") => new CtGame { Progression = progression, Completion = completion, ClaimedByUserId = 7 };
            SlotSnapshot Snap(int inLogic, bool? goal = false, string untrusted = null, bool live = true, int remaining = 10, bool goalDone = false) =>
                new SlotSnapshot { Live = live, InLogic = inLogic, GoalInLogic = goal, Untrusted = untrusted, Remaining = remaining, GoalCompleted = goalDone };

            // BK only after five quiet minutes; an item restarts the clock; good news after one minute.
            var st = new CheeseAdvisor.Stability();
            var a = CheeseAdvisor.Advise(Snap(0), Row("unblocked"), st, now);
            Expect(a.Status == "bk" && !a.Ready, "BK wasn't suggested, or was ready at once");
            Expect(!CheeseAdvisor.Advise(Snap(0), Row("unblocked"), st, now.AddMinutes(4)).Ready, "BK was ready before 5 minutes");
            Expect(CheeseAdvisor.Advise(Snap(0), Row("unblocked"), st, now.AddMinutes(5)).Ready, "BK wasn't ready after 5 minutes");
            a = CheeseAdvisor.Advise(Snap(3), Row("bk"), st, now.AddMinutes(6));
            Expect(a.Status == "unblocked" && !a.Ready, "an unblocked suggestion was ready at once");
            Expect(CheeseAdvisor.Advise(Snap(3), Row("bk"), st, now.AddMinutes(7)).Ready, "unblocked wasn't ready after a minute");

            Expect(CheeseAdvisor.Advise(Snap(3, goal: true), Row("unblocked"), new CheeseAdvisor.Stability(), now).Status == "go", "go mode wasn't suggested");
            Expect(CheeseAdvisor.Advise(Snap(3), Row("unblocked"), new CheeseAdvisor.Stability(), now).InSync, "an agreeing status wasn't in sync");
            Expect(CheeseAdvisor.Advise(Snap(0), Row("soft_bk"), new CheeseAdvisor.Stability(), now).Status == null, "Soft BK was overridden");
            Expect(CheeseAdvisor.Advise(Snap(0, goal: true), Row("soft_bk"), new CheeseAdvisor.Stability(), now).Status == "go", "go mode wasn't suggested over Soft BK");
            Expect(CheeseAdvisor.Advise(Snap(0), Row("go"), new CheeseAdvisor.Stability(), now).Status == null, "go mode was overridden");
            Expect(CheeseAdvisor.Advise(Snap(0, untrusted: "the apworld doesn't match"), Row("unblocked"), new CheeseAdvisor.Stability(), now).Status == null, "unverified logic made a suggestion");
            Expect(CheeseAdvisor.Advise(Snap(0, live: false), Row("unblocked"), new CheeseAdvisor.Stability(), now).Status == null, "an offline slot got a suggestion");
            Expect(CheeseAdvisor.Advise(Snap(0, goalDone: true), Row("unblocked"), new CheeseAdvisor.Stability(), now).Status == null, "a slot past its goal got a suggestion");
            Expect(CheeseAdvisor.Advise(Snap(0, remaining: 0), Row("unblocked"), new CheeseAdvisor.Stability(), now).Status == null, "a fully checked slot got a suggestion");
            Expect(CheeseAdvisor.Advise(Snap(0), Row("unblocked", "done"), new CheeseAdvisor.Stability(), now).Status == null, "a completed slot got a suggestion");
        }

        private static void CheeseAutomaticRules()
        {
            var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            var ready = new CheeseAdvice { Status = "unblocked", Ready = true, Reason = "3 checks in logic" };
            CtGame Row(string progression, int? claimedBy = 7, string completion = "incomplete") => new CtGame { Progression = progression, ClaimedByUserId = claimedBy, Completion = completion };
            var none = new List<DateTime>();
            bool pause;

            Expect(CheeseAdvisor.AutoBlocker(ready, Row("bk"), 7, "bk", none, now, out pause) == null && !pause, "refused on your own claimed slot");
            Expect(CheeseAdvisor.AutoBlocker(new CheeseAdvice { Status = "unblocked", Ready = false }, Row("bk"), 7, "bk", none, now, out _) != null, "applied before it settled");
            Expect(CheeseAdvisor.AutoBlocker(ready, Row("bk", claimedBy: 9), 7, "bk", none, now, out pause) != null && !pause, "changed someone else's slot");
            Expect(CheeseAdvisor.AutoBlocker(ready, Row("bk", claimedBy: null), 7, "bk", none, now, out _) != null, "changed an unclaimed slot");
            Expect(CheeseAdvisor.AutoBlocker(ready, Row("bk"), null, "bk", none, now, out _) != null, "changed a slot without an account");
            Expect(CheeseAdvisor.AutoBlocker(ready, Row("soft_bk"), 7, "bk", none, now, out pause) != null && !pause, "Soft BK wasn't simply left alone");
            Expect(CheeseAdvisor.AutoBlocker(ready, Row("bk", completion: "goal"), 7, "bk", none, now, out _) != null, "changed a completed slot");
            Expect(CheeseAdvisor.AutoBlocker(ready, Row("go"), 7, "bk", none, now, out pause) != null && pause, "an outside change didn't pause automatic updates");
            Expect(CheeseAdvisor.AutoBlocker(ready, Row("bk"), 7, "bk", new List<DateTime> { now.AddMinutes(-2) }, now, out pause) != null && !pause, "changed twice within 5 minutes");
            var many = Enumerable.Range(0, CheeseAdvisor.AutoPerDay).Select(i => now.AddMinutes(-10 - i * 30)).ToList();
            Expect(CheeseAdvisor.AutoBlocker(ready, Row("bk"), 7, "bk", many, now, out pause) != null && pause, "the daily limit didn't pause automatic updates");
        }

        private static void CheeseTableRules()
        {
            var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            var t = new CtTracker { YellowHours = 24, RedHours = 48 };
            CheeseRow R(int id, string name, string progression = "unblocked", string completion = "incomplete", string owner = null, string game = "Clique",
                string notes = "", string lastActivity = null, bool mine = false, int done = 0, string availability = "claimed") =>
                new CheeseRow
                {
                    ProfileId = "p",
                    ProfileName = "MW",
                    Tracker = t,
                    Mine = mine,
                    Game = new CtGame { Id = id, Position = id, Name = name, Game = game, Progression = progression, Completion = completion, OwnerName = owner, Notes = notes, LastActivity = lastActivity, ChecksDone = done, ChecksTotal = 10, Availability = availability }
                };

            Expect(CheeseTable.NaturalCompare("Slot 2", "Slot 10") < 0 && CheeseTable.NaturalCompare("slot10", "Slot9") > 0 && CheeseTable.NaturalCompare("a", "A") == 0, "names don't sort naturally");

            var rows = new List<CheeseRow>
            {
                R(1, "Slot 10", "bk", owner: "Bob", lastActivity: "2026-10-04T11:00:00Z"),
                R(2, "Slot 2", owner: "Alice", lastActivity: "2026-10-01T12:00:00Z", mine: true),
                R(3, "Slot 1", "go", "done", owner: "Carol", done: 10),
                R(4, "Open slot", "unknown", game: "Hollow Knight", notes: "needs a claw", availability: "open")
            };
            List<int> Passing(CheeseFilter filter, bool claimedByYou = false) => rows.Where(r => CheeseTable.Passes(r, filter, claimedByYou)).Select(r => r.Game.Id).ToList();

            // Like Cheese Tracker: a progression filter never hides completed slots.
            var f = new CheeseFilter();
            f.HiddenProgression.Add("go");
            Expect(Passing(f).Count == 4, "a progression filter hid a completed slot");
            f.HiddenProgression.Add("bk");
            Expect(!Passing(f).Contains(1), "a hidden progression status was shown");
            f.Clear();
            f.HiddenCompletion.Add("done");
            Expect(!Passing(f).Contains(3) && Passing(f).Contains(1), "the completion filter is wrong");
            f.Clear();
            f.HiddenAvailability.Add("open");
            Expect(!Passing(f).Contains(4), "the availability filter is wrong");
            f.Clear();
            f.Owner = "";
            Expect(Passing(f).SequenceEqual(new[] { 4 }), "Unclaimed didn't show only the unclaimed slot");
            f.Owner = "alice";
            Expect(Passing(f).SequenceEqual(new[] { 2 }), "an owner filter is wrong");
            f.Owner = CheeseFilter.You;
            Expect(CheeseTable.Passes(rows[1], f, true) && !CheeseTable.Passes(rows[0], f, false), "the You filter is wrong");
            f.Clear();
            f.Game = "Hollow Knight";
            Expect(Passing(f).SequenceEqual(new[] { 4 }), "the game filter is wrong");
            f.Clear();
            f.Text = "CLAW";
            Expect(Passing(f).SequenceEqual(new[] { 4 }), "text search doesn't look in notes");
            f.Text = "carol";
            Expect(Passing(f).SequenceEqual(new[] { 3 }) && f.IsActive, "text search doesn't look at owners");
            f.Clear();
            Expect(!f.IsActive && Passing(f).Count == 4, "a cleared filter still filters");

            List<int> Ids(string column, bool descending, bool mineFirst) => CheeseTable.Sort(rows, column, descending, mineFirst, now).Select(r => r.Game.Id).ToList();
            Expect(Ids("name", false, false).SequenceEqual(new[] { 4, 3, 2, 1 }), "name sort: " + string.Join(",", Ids("name", false, false)));
            Expect(Ids("name", false, true)[0] == 2, "mine first didn't put your slot on top");
            var activity = Ids("activity", true, false);
            Expect(activity.Take(2).OrderBy(x => x).SequenceEqual(new[] { 3, 4 }) && activity[2] == 2 && activity[3] == 1, "activity sort (least active first): " + string.Join(",", activity));
            var status = Ids("status", false, false);
            Expect(status.First() == 1 && status.Last() == 3, "status sort doesn't put BK first and completed slots last: " + string.Join(",", status));
            Expect(Ids("checks", true, false)[0] == 3, "checks sort is wrong");
            Expect(CheeseTable.DefaultDescending("activity") && CheeseTable.DefaultDescending("hints") && !CheeseTable.DefaultDescending("name"), "default sort directions differ from Cheese Tracker's");

            Expect(CheeseTable.ActivityLevel(rows[0].Game, t, now) == 0 && CheeseTable.ActivityLevel(rows[1].Game, t, now) == 2 &&
                   CheeseTable.ActivityLevel(rows[3].Game, t, now) == 2 && CheeseTable.ActivityLevel(rows[2].Game, t, now) == 0, "activity colours don't follow the tracker's thresholds");
            Expect(CheeseTable.ActivityText(rows[1].Game, now) == "3.0d" && CheeseTable.ActivityText(rows[3].Game, now) == "Never", "activity text: " + CheeseTable.ActivityText(rows[1].Game, now));

            // Cheese Tracker's hint count leaves out found, trash, own-world and useless hints.
            var games = rows.ToDictionary(r => r.Game.Id, r => r.Game);
            var hints = new List<CtHint>
            {
                new CtHint { Id = 1, FinderGameId = 1, ReceiverGameId = 2 },
                new CtHint { Id = 2, FinderGameId = 1, ReceiverGameId = 2, Found = true },
                new CtHint { Id = 3, FinderGameId = 1, ReceiverGameId = 2, Classification = "trash" },
                new CtHint { Id = 4, FinderGameId = 1, ReceiverGameId = 1 },
                new CtHint { Id = 5, FinderGameId = 1, ReceiverGameId = 3 },
                new CtHint { Id = 6, FinderGameId = 1, ReceiverGameId = null, ItemLinkName = "Group" }
            };
            Expect(CheeseTable.CountUnfoundHints(rows[0].Game, hints, games) == 2, "hint count: " + CheeseTable.CountUnfoundHints(rows[0].Game, hints, games));
            Expect(CheeseTable.HintState(hints[4], games) == "useless" && CheeseTable.HintState(hints[1], games) == "found" && CheeseTable.HintState(hints[0], games) == "notfound", "hint states are wrong");
        }

        private static void CheeseKeyEncrypted()
        {
            if (!CheeseKeyStore.Available)
            {
                Print("  (not Windows: Atlas doesn't store the key at all)");
                return;
            }
            string key = Guid.NewGuid().ToString();
            string stored = CheeseKeyStore.Protect(key);
            Expect(stored != null, "the key couldn't be encrypted");
            Expect(!stored.Contains(key) && !Encoding.UTF8.GetString(Convert.FromBase64String(stored)).Contains(key), "the stored form contains the key");
            Expect(CheeseKeyStore.Unprotect(stored) == key, "the key didn't decrypt back");
            Expect(CheeseKeyStore.Unprotect("not base64!") == null, "garbage decrypted");
            var tampered = Convert.FromBase64String(stored);
            tampered[tampered.Length / 2] ^= 0xFF;
            Expect(CheeseKeyStore.Unprotect(Convert.ToBase64String(tampered)) == null, "a tampered key decrypted");
        }

        private static async Task CheeseBacksOff()
        {
            CheeseClient.Spacing = TimeSpan.Zero;
            CheeseClient.ResetForTests();
            try
            {
                using var server = new FakeCheeseServer();
                var client = new CheeseClient(server.Site);

                server.FailNext(503, "120");
                var r = await client.GetTrackerAsync(TestTrackerId);
                Expect(r.Outcome == CtOutcome.ServerError, $"a 503 came back as {r.Outcome}");
                int asked = server.RequestCount;
                r = await client.GetTrackerAsync(TestTrackerId);
                Expect(r.Outcome == CtOutcome.Waiting && server.RequestCount == asked, "a failing site was asked again at once");
                var wait = CheeseClient.WaitingFor(server.Site);
                Expect(wait != null && wait.Value.UntilUtc - DateTime.UtcNow > TimeSpan.FromSeconds(100), "the site's Retry-After wasn't honored");

                CheeseClient.StopWaiting(server.Site);
                r = await client.GetTrackerAsync(TestTrackerId);
                Expect(r.Ok && r.Value.Games.Count == 4, "reading didn't work again after the wait");

                server.FailNext(429, null);
                r = await client.GetTrackerAsync(TestTrackerId);
                Expect(r.Outcome == CtOutcome.RateLimited && CheeseClient.WaitingFor(server.Site) != null, "a 429 didn't make Atlas wait");
                CheeseClient.StopWaiting(server.Site);

                server.FailNext(404, null);
                r = await client.GetTrackerAsync(TestTrackerId);
                Expect(r.Outcome == CtOutcome.NotFound && CheeseClient.WaitingFor(server.Site) == null, "a missing tracker made Atlas wait as if the site were down");

                // Nothing listens on port 1 here: an unreachable site is left alone too.
                var dead = new CheeseClient("http://127.0.0.1:1");
                r = await dead.GetTrackerAsync(TestTrackerId);
                Expect(r.Outcome == CtOutcome.Unreachable && CheeseClient.WaitingFor("http://127.0.0.1:1") != null, $"an unreachable site came back as {r.Outcome} with no wait");
            }
            finally
            {
                CheeseClient.Spacing = TimeSpan.FromSeconds(1);
                CheeseClient.ResetForTests();
            }
        }

        private static async Task CheeseReadsNeverStick()
        {
            CheeseClient.Spacing = TimeSpan.Zero;
            CheeseClient.ResetForTests();
            using var server = new FakeCheeseServer();
            var settings = new AppSettings { CheeseInstanceUrl = server.Site };
            var profile = new MultiworldProfile { Name = "Test MW", Slots = new List<string> { "Me" } };
            profile.SavedStats["Me"] = new SlotStats { GameName = "Clique", SlotNumber = 1 };
            var profiles = new List<MultiworldProfile> { profile };
            CheeseTrackerService New() => new CheeseTrackerService(settings, () => profiles, () => Enumerable.Empty<SlotModel>(), () => { });
            var cheese = New();
            CheeseTrackerService fresh = null;
            try
            {
                Expect(await cheese.LinkAsync(profile.Id, $"{server.Site}/tracker/{TestTrackerId}") == null, "linking failed");
                server.FailNext(503, "600");
                Expect(await cheese.RefreshAsync(profile.Id) != null && CheeseClient.WaitingFor(server.Site) != null, "a site in trouble wasn't left alone");

                // While the site is left alone a read ends at once: it must not leave the multiworld "updating" for good.
                fresh = New();
                int before = server.RequestCount;
                fresh.Watch(profile.Id);
                var room = fresh.RoomView(profile.Id);
                Expect(server.RequestCount == before && room != null && !room.Busy && room.Problem != null, "a read while the site is left alone stuck as \"updating\" (or asked the site)");
                CheeseClient.StopWaiting(server.Site);
                Expect(await fresh.RefreshAsync(profile.Id) == null && server.RequestCount == before + 1, "reading didn't work again once the wait ended");
            }
            finally
            {
                cheese.Free();
                fresh?.Free();
                CheeseClient.Spacing = TimeSpan.FromSeconds(1);
                CheeseClient.ResetForTests();
            }
        }

        private static async Task CheeseChangesAreSafe()
        {
            CheeseClient.Spacing = TimeSpan.Zero;
            CheeseClient.ResetForTests();
            using var server = new FakeCheeseServer();
            using var otherSite = new FakeCheeseServer();
            var settings = new AppSettings { CheeseInstanceUrl = server.Site };
            var profile = new MultiworldProfile { Name = "Test MW", Slots = new List<string> { "Me", "Friend", "Open" } };
            profile.SavedStats["Me"] = new SlotStats { GameName = "Clique", SlotNumber = 1 };
            profile.SavedStats["Friend"] = new SlotStats { GameName = "Clique", SlotNumber = 2 };
            profile.SavedStats["Open"] = new SlotStats { GameName = "Clique", SlotNumber = 3 };
            var elsewhere = new MultiworldProfile { Name = "Elsewhere", Slots = new List<string> { "Me" } };
            elsewhere.SavedStats["Me"] = new SlotStats { GameName = "Clique", SlotNumber = 1 };
            var stranger = new MultiworldProfile { Name = "Stranger", Slots = new List<string> { "Someone" } };
            var profiles = new List<MultiworldProfile> { profile, elsewhere, stranger };
            var cheese = new CheeseTrackerService(settings, () => profiles, () => Enumerable.Empty<SlotModel>(), () => { });
            try
            {
                Expect(await cheese.LinkAsync(stranger.Id, $"{server.Site}/tracker/{TestTrackerId}") != null && stranger.CheeseTrackerUrl == "",
                    "a tracker that doesn't list the profile's slots was linked");
                Expect(await cheese.LinkAsync(profile.Id, $"{server.Site}/tracker/{TestTrackerId}") == null, "linking failed");
                Expect(server.Requests.All(q => q.Auth == null), "a key was sent before there was one");

                // Without a key Atlas reads, but changes nothing.
                Expect(await cheese.SetProgressionAsync(profile.Id, "Me", "bk") != null && server.Requests.All(q => q.Method != "PUT"), "a change was made without a key");

                Expect(await cheese.SetKeyAsync("not a key") != null, "a malformed key was accepted");
                Expect(await cheese.SetKeyAsync(Guid.NewGuid().ToString()) != null, "a key the site rejects was kept");
                Expect(await cheese.SetKeyAsync(server.ValidKey) == null, "a good key was refused");
                Expect(settings.CheeseUserId == 7 && settings.CheeseUserName == "Tester", "the key's account wasn't recorded");
                Expect(!string.IsNullOrEmpty(settings.CheeseApiKeyProtected) && !settings.CheeseApiKeyProtected.Contains(server.ValidKey), "the key was stored in plain text");

                // Someone edits the notes on the site after Atlas's last read: the change keeps their edit.
                server.Edit(g => { if (g.Id == 11) g.Notes = "new from the site"; });
                int before = server.RequestCount;
                Expect(await cheese.SetProgressionAsync(profile.Id, "Me", "bk") == null, "setting BK failed");
                var sent = server.Requests.Skip(before).ToList();
                Expect(sent.Count >= 2 && sent[0].Method == "GET" && sent[^1].Method == "PUT", "the slot wasn't read again before the change");
                var body = JsonConvert.DeserializeObject<CtGameUpdate>(sent[^1].Body, CheeseClient.Json);
                Expect(body.Progression == "bk" && body.Notes == "new from the site", "the change didn't keep the newer notes");
                Expect(body.LastChecked != "2026-10-01T10:00:00.123456Z", "BK didn't reset the inactivity clock");
                Expect(sent[^1].Auth == "Bearer " + server.ValidKey && sent[^1].Owner == null, "the change wasn't made as the key's account, or sent a claim precondition");

                // Values Atlas doesn't change go back exactly as the site wrote them.
                server.Edit(g => { if (g.Id == 11) g.LastChecked = "2026-10-02T11:22:33.987654Z"; });
                Expect(await cheese.SetNotesAsync(profile.Id, "Me", "hello") == null, "saving notes failed");
                body = JsonConvert.DeserializeObject<CtGameUpdate>(server.Requests[^1].Body, CheeseClient.Json);
                Expect(body.LastChecked == "2026-10-02T11:22:33.987654Z" && body.Notes == "hello" && body.Progression == "bk", "an unchanged value was rewritten");

                // Someone else's slot is never changed.
                before = server.RequestCount;
                Expect(await cheese.SetProgressionAsync(profile.Id, "Friend", "bk") != null, "someone else's slot was changed");
                Expect(server.Requests.Skip(before).All(q => q.Method != "PUT"), "a change for someone else's slot was sent");
                Expect(cheese.SetAuto(profile.Id, "Friend", true) != null, "automatic updates were allowed on someone else's slot");
                Expect(cheese.SetAuto(profile.Id, "Open", true) != null, "automatic updates were allowed on an unclaimed slot");
                Expect(cheese.SetAuto(profile.Id, "Me", true) == null && settings.CheeseAutoLastSet.GetValueOrDefault(CheeseTrackerService.SlotKey(profile.Id, "Me")) == "bk",
                    "automatic updates weren't allowed on your own slot, or didn't start from its status");

                // Claiming an open slot says who held it before.
                Expect(await cheese.ClaimAsync(profile.Id, "Open") == null, "claiming an open slot failed");
                var claim = server.Requests[^1];
                Expect(claim.Owner != null && server.Game(13).ClaimedByUserId == 7 && server.Game(13).Availability == "claimed", "the claim wasn't made properly");

                // The tab's rows: every slot, which are yours in Atlas, and the hint count.
                var tabRows = cheese.RowsOf(profile.Id);
                var me = tabRows.FirstOrDefault(x => x.Game.Id == 11);
                Expect(tabRows.Count == 4 && me?.SlotName == "Me" && me.Mine && me.UnfoundHints == 1 && tabRows.First(x => x.Game.Id == 14).SlotName == null,
                    "the tab's rows don't map to the profile's slots, or the hint count is wrong");

                // From the tab, any row can be changed by id: claiming an open slot that isn't in the profile works,
                // someone else's row is refused without sending anything.
                before = server.RequestCount;
                Expect(await cheese.SetProgressionAsync(CheeseTarget.Row(profile.Id, 14), "bk") != null && server.Requests.Skip(before).All(q => q.Method != "PUT"),
                    "an unclaimed slot was changed without claiming it (Cheese Tracker protects those)");
                Expect(await cheese.ClaimAsync(CheeseTarget.Row(profile.Id, 14)) == null && server.Game(14).ClaimedByUserId == 7, "claiming an open row by id failed");
                Expect(await cheese.SetProgressionAsync(CheeseTarget.Row(profile.Id, 14), "bk") == null && server.Game(14).Progression == "bk", "a slot just claimed couldn't be changed");
                before = server.RequestCount;
                Expect(await cheese.SetProgressionAsync(CheeseTarget.Row(profile.Id, 12), "bk") != null && server.Requests.Skip(before).All(q => q.Method != "PUT"),
                    "someone else's row was changed by id");

                // The cache keeps times exactly and leaves hints out.
                var reread = new CheeseTrackerService(settings, () => profiles, () => Enumerable.Empty<SlotModel>(), () => { });
                try
                {
                    var cachedRoom = reread.RoomView(profile.Id);
                    var cachedMe = cachedRoom?.Tracker?.Games.FirstOrDefault(g => g.Id == 11);
                    Expect(cachedMe?.LastChecked == "2026-10-02T11:22:33.987654Z" && cachedRoom.Tracker.Hints.Count == 0, "the cached tracker changed a time or kept hints");
                }
                finally
                {
                    reread.Free();
                }

                // A tracker on another Cheese Tracker site is read without the key and never changed.
                Expect(await cheese.LinkAsync(elsewhere.Id, $"{otherSite.Site}/tracker/{TestTrackerId}") == null, "linking a tracker on another site failed");
                Expect(await cheese.SetProgressionAsync(elsewhere.Id, "Me", "bk") != null, "a tracker on another site was changed");
                Expect(otherSite.Requests.All(q => q.Auth == null && q.Method == "GET"), "the key or a change went to another site");

                // A key regenerated on the site is noticed and no longer used (the site accepts changes without one).
                server.ValidKey = Guid.NewGuid().ToString();
                var fresh = new CheeseTrackerService(settings, () => profiles, () => Enumerable.Empty<SlotModel>(), () => { });
                try
                {
                    before = server.RequestCount;
                    Expect(await fresh.SetProgressionAsync(profile.Id, "Me", "unblocked") != null && fresh.KeyRejected, "a dead key wasn't noticed");
                    Expect(server.Requests.Skip(before).All(q => q.Method != "PUT"), "a change was sent with a dead key");
                }
                finally
                {
                    fresh.Free();
                }
            }
            finally
            {
                cheese.Free();
                CheeseClient.Spacing = TimeSpan.FromSeconds(1);
                CheeseClient.ResetForTests();
            }
        }
    }
}
