#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net.Enums;
using Godot;
using Newtonsoft.Json;

namespace AP_Atlas.Core.CheeseTracker
{
    public enum CheeseOwnership
    {
        Nobody,
        /// <summary>Claimed by the account of Atlas's API key.</summary>
        You,
        /// <summary>Claimed without signing in, under the same name as the user's account.</summary>
        YouByName,
        SomeoneElse
    }

    /// <summary>A row to change: one of a profile's slots (by name), or any row of its tracker (by id).</summary>
    public readonly record struct CheeseTarget(string ProfileId, string SlotName, int RowId)
    {
        public static CheeseTarget Slot(string profileId, string slotName) => new CheeseTarget(profileId, slotName, 0);
        public static CheeseTarget Row(string profileId, int rowId) => new CheeseTarget(profileId, null, rowId);

        /// <summary>A slot target shares the key the service uses for that slot elsewhere (busy, errors, automatic updates).</summary>
        public string Key => SlotName != null ? CheeseTrackerService.SlotKey(ProfileId, SlotName) : $"{ProfileId}#{RowId}";
    }

    /// <summary>A linked tracker, as the UI shows it.</summary>
    public sealed class CheeseRoomView
    {
        public string Link { get; init; }
        public string Site { get; init; }
        public CtTracker Tracker { get; init; }
        public DateTime? FetchedUtc { get; init; }
        public string Problem { get; init; }
        public bool Busy { get; init; }
        /// <summary>The tracker is on another Cheese Tracker site than the one in Settings, so Atlas only reads it.</summary>
        public bool OtherSite { get; init; }
    }

    /// <summary>One slot, as the UI shows it.</summary>
    public sealed class CheeseSlotView
    {
        public CheeseRoomView Room { get; init; }
        public CtGame Row { get; init; }
        /// <summary>Why the tracker has no row for this slot.</summary>
        public string NotFound { get; init; }
        public CheeseOwnership Ownership { get; init; }
        /// <summary>Null when Atlas may change this slot, otherwise why not.</summary>
        public string CannotEdit { get; init; }
        /// <summary>Null while the slot isn't connected.</summary>
        public CheeseAdvice Advice { get; init; }
        public bool AutoOn { get; init; }
        public string AutoPaused { get; init; }
        public bool Busy { get; init; }
        public string LastError { get; init; }
    }

    /// <summary>
    /// Keeps Atlas's multiworlds in step with their Cheese Tracker pages. It reads a linked tracker every 10 minutes while
    /// one of its slots is connected (and right before any change, so a change never undoes someone else's), suggests
    /// progression statuses from Atlas's logic, applies them for slots the user switched to automatic, and makes the
    /// changes the user asks for. It never changes a slot claimed by someone else.
    /// </summary>
    public partial class CheeseTrackerService : Node
    {
        public static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ManualRefreshSpacing = TimeSpan.FromSeconds(30);

        /// <summary>Raised on the main thread when anything shown about Cheese Tracker changed.</summary>
        public event Action Changed;
        /// <summary>Something the user should see now (automatic updates paused, a key stopped working).</summary>
        public event Action<string> Notice;
        /// <summary>A profile was linked or unlinked (its id).</summary>
        public event Action<string> LinkChanged;

        private readonly AppSettings _settings;
        private readonly Func<IReadOnlyList<MultiworldProfile>> _profiles;
        private readonly Func<IEnumerable<SlotTrackerControl>> _slots;
        private readonly Action _saveProfiles;

        private sealed class Room
        {
            public string Link, Site, TrackerId;
            public CtTracker Tracker;
            public DateTime FetchedUtc = DateTime.MinValue;
            /// <summary>The tracker was read from the site in this session (not just loaded from the cache).</summary>
            public bool FromNetwork;
            public string Problem;
            public DateTime NotBeforeUtc = DateTime.MinValue;
            public DateTime LastManualUtc = DateTime.MinValue;
            public Task<string> Pending;
        }

        private readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>();
        private readonly Dictionary<string, CheeseAdvisor.Stability> _stability = new Dictionary<string, CheeseAdvisor.Stability>();
        private readonly Dictionary<string, CheeseAdvice> _advice = new Dictionary<string, CheeseAdvice>();
        private readonly Dictionary<string, List<DateTime>> _autoTimes = new Dictionary<string, List<DateTime>>();
        private readonly HashSet<string> _busy = new HashSet<string>();
        private readonly HashSet<string> _autoPending = new HashSet<string>();
        private readonly Dictionary<string, string> _errors = new Dictionary<string, string>();
        private readonly Dictionary<string, DateTime> _watchedUntil = new Dictionary<string, DateTime>();
        private string _apiKey;
        private bool _keyRejected;

        public CheeseTrackerService(AppSettings settings, Func<IReadOnlyList<MultiworldProfile>> profiles, Func<IEnumerable<SlotTrackerControl>> slots, Action saveProfiles)
        {
            Name = "CheeseTracker";
            _settings = settings;
            _profiles = profiles;
            _slots = slots;
            _saveProfiles = saveProfiles;
            _apiKey = CheeseKeyStore.Unprotect(settings.CheeseApiKeyProtected);
            if (_apiKey == null && !string.IsNullOrEmpty(settings.CheeseApiKeyProtected))
                Logger.LogWarning("Cheese Tracker: the saved API key can't be decrypted here (another Windows account or PC). Add it again in Cheese Tracker → Settings.");
        }

        public override void _Ready()
        {
            var timer = new Timer { WaitTime = 15, Autostart = true };
            timer.Timeout += Tick;
            AddChild(timer);
        }

        public static string SlotKey(string profileId, string slotName) => profileId + "|" + slotName;

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); } catch (Exception ex) { Logger.LogWarning("Cheese Tracker view refresh failed: " + ex.Message); }
        }

        // =====================================================================
        // Account and site
        // =====================================================================

        /// <summary>The Cheese Tracker site from Settings ("https://host").</summary>
        public string Site => CheeseClient.NormalizeSite(_settings.CheeseInstanceUrl) ?? CheeseClient.DefaultInstance;
        public bool HasKey => _apiKey != null && !_keyRejected;
        public bool KeyRejected => _apiKey != null && _keyRejected;
        public string AccountName => string.IsNullOrEmpty(_settings.CheeseUserName) ? null : _settings.CheeseUserName;

        /// <summary>Checks a key with Cheese Tracker and keeps it, encrypted, if it works. Returns an error or null.</summary>
        public async Task<string> SetKeyAsync(string key)
        {
            key = (key ?? "").Trim();
            if (!Guid.TryParse(key, out _)) return "That doesn't look like a Cheese Tracker API key (a long code like 1b4e28ba-2fa1-41d2-883f-0016d3cca427).";
            if (!CheeseKeyStore.Available) return "Atlas can only store the key safely on Windows.";
            var r = await new CheeseClient(Site, key).GetSelfAsync();
            if (!r.Ok)
                return r.Outcome == CtOutcome.Unauthorized
                    ? "Cheese Tracker didn't accept that key. Copy it again from its Settings page (generating a new key replaces the old one)."
                    : r.Message;
            string stored = CheeseKeyStore.Protect(key);
            if (stored == null) return "Windows couldn't encrypt the key, so Atlas didn't keep it.";
            _settings.CheeseApiKeyProtected = stored;
            _settings.CheeseUserId = r.Value.Id;
            _settings.CheeseUserName = r.Value.Name ?? "";
            DataManager.SaveSettings(_settings);
            _apiKey = key;
            _keyRejected = false;
            _keyCheck = Task.CompletedTask;
            Logger.LogInfo($"Cheese Tracker: signed in as {r.Value.Name}.");
            RaiseChanged();
            return null;
        }

        private Task _keyCheck;

        /// <summary>
        /// Checks once per session that the saved key still works. Cheese Tracker accepts status changes without a key,
        /// so a key regenerated on the site would otherwise go unnoticed (and changes would stop being made as you).
        /// Everyone waiting on it shares the one check.
        /// </summary>
        public Task VerifyKeyAsync()
        {
            if (_apiKey == null || _keyRejected) return Task.CompletedTask;
            if (_keyCheck != null) return _keyCheck;
            var check = VerifyKeyCoreAsync();
            // A check that ended at once (the site is being left alone) has already said whether to ask again.
            if (!check.IsCompleted) _keyCheck = check;
            return check;
        }

        private async Task VerifyKeyCoreAsync()
        {
            var r = await new CheeseClient(Site, _apiKey).GetSelfAsync();
            if (!IsInstanceValid(this)) return;
            if (r.Outcome == CtOutcome.Unauthorized)
            {
                RejectKey();
                return;
            }
            if (!r.Ok)
            {
                _keyCheck = null; // couldn't tell (the site is having trouble): ask again later
                return;
            }
            if (r.Value.Id != _settings.CheeseUserId || r.Value.Name != _settings.CheeseUserName)
            {
                _settings.CheeseUserId = r.Value.Id;
                _settings.CheeseUserName = r.Value.Name ?? "";
                DataManager.SaveSettings(_settings);
                RaiseChanged();
            }
        }

        public void RemoveKey()
        {
            _settings.CheeseApiKeyProtected = "";
            _settings.CheeseUserId = null;
            _settings.CheeseUserName = "";
            DataManager.SaveSettings(_settings);
            _apiKey = null;
            _keyRejected = false;
            _keyCheck = null;
            RaiseChanged();
        }

        /// <summary>Changes the Cheese Tracker site. The key belongs to the old site, so it's removed. Returns an error or null.</summary>
        public string SetSite(string url)
        {
            string site = CheeseClient.NormalizeSite(url);
            if (site == null) return "Use the site's https address, like " + CheeseClient.DefaultInstance + ".";
            if (CheeseClient.SameSite(site, Site)) return null;
            if (_apiKey != null) RemoveKey();
            _settings.CheeseInstanceUrl = site;
            DataManager.SaveSettings(_settings);
            RaiseChanged();
            return null;
        }

        // =====================================================================
        // Rooms
        // =====================================================================

        private MultiworldProfile ProfileOf(string profileId) => _profiles().FirstOrDefault(p => p.Id == profileId);

        private Room RoomOf(MultiworldProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.CheeseTrackerUrl)) return null;
            if (_rooms.TryGetValue(profile.Id, out var room) && room.Link == profile.CheeseTrackerUrl) return room;
            // Profiles only ever hold Cheese Tracker page links (an Archipelago link is looked up when it's linked).
            var link = CtLink.Parse(profile.CheeseTrackerUrl, Site, out _);
            if (link == null || link.Kind == CtLink.LinkKind.ApRoom) return null;
            room = new Room { Link = profile.CheeseTrackerUrl, Site = link.Site, TrackerId = link.Id };
            LoadCache(profile.Id, room);
            _rooms[profile.Id] = room;
            return room;
        }

        private CheeseClient ClientFor(string site) =>
            CheeseClient.SameSite(site, Site) && _apiKey != null && !_keyRejected ? new CheeseClient(Site, _apiKey) : new CheeseClient(site);

        private string WaitingProblem(string site)
        {
            var wait = CheeseClient.WaitingFor(site);
            return wait == null ? null : $"{wait.Value.Reason}. Atlas tries again after {wait.Value.UntilUtc.ToLocalTime():HH:mm}.";
        }

        public CheeseRoomView RoomView(string profileId)
        {
            var room = RoomOf(ProfileOf(profileId));
            if (room == null) return null;
            return new CheeseRoomView
            {
                Link = room.Link,
                Site = room.Site,
                Tracker = room.Tracker,
                FetchedUtc = room.FetchedUtc == DateTime.MinValue ? null : room.FetchedUtc,
                Problem = room.Problem ?? WaitingProblem(room.Site),
                Busy = room.Pending != null,
                OtherSite = !CheeseClient.SameSite(room.Site, Site)
            };
        }

        /// <summary>Properties is showing this multiworld: keep it fresh for a few minutes even with no slot connected.</summary>
        public void Watch(string profileId)
        {
            var profile = ProfileOf(profileId);
            var room = RoomOf(profile);
            if (room == null) return;
            _watchedUntil[profileId] = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            if (Due(room)) AP_Atlas.Core.Async.Fire(FetchAsync(profile, room), "reading Cheese Tracker");
        }

        private static bool Due(Room room) =>
            room.Pending == null && DateTime.UtcNow >= room.NotBeforeUtc &&
            (!room.FromNetwork || DateTime.UtcNow - room.FetchedUtc >= PollEvery);

        /// <summary>Reads the tracker now (at most every 30 seconds). Returns an error, or null.</summary>
        public async Task<string> RefreshAsync(string profileId, bool tryNow = false)
        {
            var profile = ProfileOf(profileId);
            var room = RoomOf(profile);
            if (room == null) return "This multiworld isn't linked to Cheese Tracker.";
            if (DateTime.UtcNow - room.LastManualUtc < ManualRefreshSpacing) return "Refreshed less than 30 seconds ago.";
            room.LastManualUtc = DateTime.UtcNow;
            if (tryNow) CheeseClient.StopWaiting(room.Site);
            room.NotBeforeUtc = DateTime.MinValue;
            return await FetchAsync(profile, room);
        }

        private Task<string> FetchAsync(MultiworldProfile profile, Room room)
        {
            if (room.Pending != null) return room.Pending;
            var read = FetchCoreAsync(profile, room);
            // A read can end at once (the site is being left alone after trouble); it has cleared Pending already, and
            // setting it now would leave the multiworld "updating" for good.
            if (!read.IsCompleted) room.Pending = read;
            RaiseChanged();
            return read;
        }

        private async Task<string> FetchCoreAsync(MultiworldProfile profile, Room room)
        {
            try
            {
                var r = await ClientFor(room.Site).GetTrackerAsync(room.TrackerId);
                if (!IsInstanceValid(this)) return null;
                if (r.Ok)
                {
                    room.Tracker = r.Value;
                    room.FetchedUtc = DateTime.UtcNow;
                    room.FromNetwork = true;
                    room.Problem = null;
                    SaveCache(profile.Id, room);
                    return null;
                }
                room.Problem = r.Outcome switch
                {
                    CtOutcome.NotFound => "Cheese Tracker has no tracker at this link any more. Link this multiworld again.",
                    CtOutcome.Forbidden => "Cheese Tracker won't show this tracker (its room isn't on a site it tracks).",
                    _ => r.Message
                };
                // A gone or refused tracker isn't asked again until the user acts; other failures retry in a few minutes
                // (the client also leaves a failing site alone).
                room.NotBeforeUtc = r.Outcome is CtOutcome.NotFound or CtOutcome.Forbidden ? DateTime.MaxValue : DateTime.UtcNow + TimeSpan.FromMinutes(2);
                if (r.Outcome == CtOutcome.Unauthorized && _apiKey != null) RejectKey();
                return room.Problem;
            }
            finally
            {
                room.Pending = null;
                RaiseChanged();
            }
        }

        // =====================================================================
        // Linking
        // =====================================================================

        /// <summary>
        /// Links a multiworld to its Cheese Tracker page. An Archipelago room or tracker link works too: Cheese Tracker
        /// looks up that room's tracker (and starts one if nobody has yet). The tracker must list at least one of the
        /// profile's slots. Returns an error, or null.
        /// </summary>
        public async Task<string> LinkAsync(string profileId, string text)
        {
            var profile = ProfileOf(profileId);
            if (profile == null) return "That multiworld no longer exists.";
            if (string.IsNullOrWhiteSpace(text))
            {
                Unlink(profileId);
                return null;
            }
            var link = CtLink.Parse(text, Site, out string error);
            if (link == null) return error;

            string trackerLink = link.Url;
            if (link.Kind != CtLink.LinkKind.CheeseTracker)
            {
                var resolved = await new CheeseClient(Site).ResolveTrackerAsync(link.Url);
                if (!resolved.Ok)
                    return resolved.Outcome switch
                    {
                        CtOutcome.Forbidden => $"Cheese Tracker doesn't track rooms on {new Uri(link.Site).Host}.",
                        CtOutcome.NotFound => "Cheese Tracker couldn't find that room. Check the link (Archipelago deletes rooms after a long time without activity).",
                        CtOutcome.Rejected => "Cheese Tracker didn't accept that link. Use the room's page (…/room/…) or its tracker (…/tracker/…).",
                        _ => resolved.Message
                    };
                trackerLink = $"{Site}/tracker/{resolved.Value}";
            }

            var parsed = CtLink.Parse(trackerLink, Site, out _);
            var room = new Room { Link = trackerLink, Site = parsed.Site, TrackerId = parsed.Id };
            var fetched = await ClientFor(room.Site).GetTrackerAsync(room.TrackerId);
            if (!fetched.Ok) return fetched.Outcome == CtOutcome.NotFound ? "There's no Cheese Tracker page at that link." : fetched.Message;

            int found = profile.Slots.Count(name => MatchRow(fetched.Value, SlotNumberOf(profile, name), name, GameOf(profile, name), out _) != null);
            if (profile.Slots.Count > 0 && found == 0)
                return $"None of {profile.Name}'s slots ({string.Join(", ", profile.Slots.Take(4))}) is on that tracker ({fetched.Value.Games.Count} slots, e.g. {string.Join(", ", fetched.Value.Games.Take(3).Select(g => g.Name))}). Is it for another multiworld?";

            foreach (var name in profile.Slots) TurnAutoOff(SlotKey(profile.Id, name));
            profile.CheeseTrackerUrl = trackerLink;
            _saveProfiles();
            room.Tracker = fetched.Value;
            room.FetchedUtc = DateTime.UtcNow;
            room.FromNetwork = true;
            _rooms[profile.Id] = room;
            SaveCache(profile.Id, room);
            Logger.LogInfo($"Cheese Tracker: {profile.Name} is linked to {trackerLink} ({found} of {profile.Slots.Count} slots found).");
            LinkChanged?.Invoke(profile.Id);
            RaiseChanged();
            return null;
        }

        public void Unlink(string profileId)
        {
            var profile = ProfileOf(profileId);
            if (profile == null) return;
            foreach (var name in profile.Slots) TurnAutoOff(SlotKey(profile.Id, name));
            profile.CheeseTrackerUrl = "";
            _saveProfiles();
            _rooms.Remove(profileId);
            try { SafeFile.Delete(CachePath(profileId)); }
            catch (Exception ex) { Logger.LogWarning($"Couldn't delete {profile.Name}'s Cheese Tracker cache: {ex.Message}"); }
            LinkChanged?.Invoke(profileId);
            RaiseChanged();
        }

        /// <summary>Looks for this multiworld among the trackers on the user's dashboard: same room address, same slots.</summary>
        public async Task<(string Link, string Title, string Error)> FindOnDashboardAsync(string profileId)
        {
            var profile = ProfileOf(profileId);
            if (profile == null) return (null, null, "That multiworld no longer exists.");
            if (!HasKey) return (null, null, "Add your API key first (Cheese Tracker → Settings): the dashboard belongs to your account.");
            var client = new CheeseClient(Site, _apiKey);
            var dashboard = await client.GetDashboardAsync();
            if (!dashboard.Ok) return (null, null, dashboard.Message);
            var (host, port) = HostAndPort(profile.ServerUrl);
            var candidates = dashboard.Value
                .Where(t => host != null && string.Equals(t.RoomHost, host, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.LastPort == port)
                .Take(3)
                .ToList();
            foreach (var c in candidates)
            {
                var t = await client.GetTrackerAsync(c.TrackerId);
                if (!t.Ok) continue;
                if (profile.Slots.Any(name => MatchRow(t.Value, SlotNumberOf(profile, name), name, GameOf(profile, name), out _) != null))
                    return ($"{Site}/tracker/{c.TrackerId}", string.IsNullOrEmpty(t.Value.Title) ? c.Title : t.Value.Title, null);
            }
            return (null, null, candidates.Count == 0
                ? $"None of the trackers on your dashboard is for a room on {host ?? "this server"}. Paste the tracker's link instead."
                : "No tracker on your dashboard lists this multiworld's slots. Paste the tracker's link instead.");
        }

        private static (string Host, int Port) HostAndPort(string server)
        {
            server = (server ?? "").Trim();
            if (server.Length == 0) return (null, 0);
            if (!server.Contains("://")) server = "ws://" + server;
            return Uri.TryCreate(server, UriKind.Absolute, out var uri) ? (uri.Host, uri.Port) : (null, 0);
        }

        // =====================================================================
        // Slots
        // =====================================================================

        private SlotTrackerControl LiveSlot(string profileId, string slotName) =>
            _slots().FirstOrDefault(s => IsInstanceValid(s) && s.ProfileId == profileId && s.SlotName == slotName && s.Session != null);

        private int SlotNumberOf(MultiworldProfile profile, string slotName)
        {
            var live = LiveSlot(profile.Id, slotName);
            if (live != null) return live.PlayerSlot;
            return profile.SavedStats != null && profile.SavedStats.TryGetValue(slotName, out var st) ? st.SlotNumber : 0;
        }

        private string GameOf(MultiworldProfile profile, string slotName)
        {
            var live = LiveSlot(profile.Id, slotName);
            if (live != null) return live.Game;
            return profile.SavedStats != null && profile.SavedStats.TryGetValue(slotName, out var st) ? st.GameName : null;
        }

        /// <summary>
        /// A slot's row: the row with its player number and game, or (number unknown) the only row with its name and game.
        /// Never a guess: a number whose game differs, or a name on several rows, is no match.
        /// </summary>
        public static CtGame MatchRow(CtTracker tracker, int slotNumber, string slotName, string game, out string why)
        {
            why = null;
            if (tracker?.Games == null || tracker.Games.Count == 0)
            {
                why = "The tracker has no slots yet.";
                return null;
            }
            bool SameGame(CtGame g) => string.IsNullOrEmpty(game) || string.Equals(g.Game, game, StringComparison.OrdinalIgnoreCase);
            if (slotNumber > 0)
            {
                var byNumber = tracker.Games.Where(g => g.Position == slotNumber).ToList();
                if (byNumber.Count == 1)
                {
                    if (SameGame(byNumber[0])) return byNumber[0];
                    why = $"Slot {slotNumber} on this tracker is {byNumber[0].Name} playing {byNumber[0].Game}, not {game}. Is the link for another multiworld?";
                    return null;
                }
            }
            var byName = tracker.Games.Where(g => string.Equals(g.Name, slotName, StringComparison.Ordinal) && SameGame(g)).ToList();
            if (byName.Count == 1) return byName[0];
            why = byName.Count > 1 ? $"Several slots on this tracker are called {slotName}; connect it once so Atlas knows its number."
                : $"{slotName} isn't on this tracker.";
            return null;
        }

        private CtGame RowFor(MultiworldProfile profile, Room room, string slotName, out string why) =>
            MatchRow(room?.Tracker, SlotNumberOf(profile, slotName), slotName, GameOf(profile, slotName), out why);

        public CheeseOwnership OwnershipOf(CtGame row)
        {
            if (row == null) return CheeseOwnership.Nobody;
            if (row.ClaimedByUserId != null) return row.ClaimedByUserId == _settings.CheeseUserId ? CheeseOwnership.You : CheeseOwnership.SomeoneElse;
            if (!string.IsNullOrEmpty(row.DiscordUsername))
                return !string.IsNullOrEmpty(_settings.CheeseUserName) && string.Equals(row.DiscordUsername, _settings.CheeseUserName, StringComparison.OrdinalIgnoreCase)
                    ? CheeseOwnership.YouByName : CheeseOwnership.SomeoneElse;
            return CheeseOwnership.Nobody;
        }

        /// <summary>Why Atlas can't change rows on this tracker at all (site, key), or null.</summary>
        private string EditBlocker(Room room)
        {
            if (!CheeseClient.SameSite(room.Site, Site))
                return $"This tracker is on {new Uri(room.Site).Host}, not {new Uri(Site).Host} (Cheese Tracker → Settings), so Atlas only reads it.";
            if (_apiKey == null) return "Add your Cheese Tracker API key (Cheese Tracker → Settings) so Atlas can make changes as you.";
            if (_keyRejected) return "Cheese Tracker no longer accepts Atlas's API key (was it regenerated?). Add it again in Cheese Tracker → Settings.";
            return null;
        }

        private static string ClaimedBy(CtGame row) => string.IsNullOrEmpty(row.OwnerName) ? "someone else" : row.OwnerName;

        /// <summary>
        /// Why this row can't be changed (other than claimed) by the user, or null. Like Cheese Tracker's own page, which
        /// protects other people's slots by default: only slots that are yours, or public, can be changed.
        /// </summary>
        private string Protection(CtGame row) => OwnershipOf(row) switch
        {
            CheeseOwnership.SomeoneElse => $"{row.Name} is claimed by {ClaimedBy(row)}. Atlas only changes your own slots.",
            CheeseOwnership.Nobody when row.Availability != "public" => $"Nobody has claimed {row.Name}. Claim it first: like Cheese Tracker's own page, Atlas only changes slots that are yours (or public).",
            _ => null
        };

        /// <summary>Whether the user can claim this row (the site and key allow changes, and it's unclaimed or claimed under their name).</summary>
        public bool CanClaim(string profileId, CtGame row)
        {
            var room = RoomOf(ProfileOf(profileId));
            return room != null && row != null && EditBlocker(room) == null && OwnershipOf(row) is CheeseOwnership.Nobody or CheeseOwnership.YouByName;
        }

        public CheeseSlotView SlotView(string profileId, string slotName)
        {
            var profile = ProfileOf(profileId);
            var room = RoomOf(profile);
            if (room == null) return null;
            string key = SlotKey(profileId, slotName);
            string notFound = null;
            var row = room.Tracker == null ? null : RowFor(profile, room, slotName, out notFound);
            var owner = OwnershipOf(row);
            string cannot = row == null ? null : EditBlocker(room) ?? Protection(row);
            var live = LiveSlot(profileId, slotName);
            return new CheeseSlotView
            {
                Room = RoomView(profileId),
                Row = row,
                NotFound = notFound,
                Ownership = owner,
                CannotEdit = cannot,
                Advice = live != null && row != null ? LiveAdvice(live, row) : null,
                AutoOn = _settings.CheeseAutoSlots.Contains(key),
                AutoPaused = _settings.CheeseAutoPaused.GetValueOrDefault(key),
                Busy = _busy.Contains(key),
                LastError = _errors.GetValueOrDefault(key)
            };
        }

        /// <summary>Profiles linked to a Cheese Tracker page.</summary>
        public List<MultiworldProfile> LinkedProfiles() => _profiles().Where(p => !string.IsNullOrWhiteSpace(p.CheeseTrackerUrl)).ToList();

        /// <summary>Every row of a linked tracker, with the profile slot each one is (if any), for the Cheese Tracker tab.</summary>
        public List<CheeseRow> RowsOf(string profileId)
        {
            var profile = ProfileOf(profileId);
            var room = RoomOf(profile);
            var tracker = room?.Tracker;
            if (tracker?.Games == null) return new List<CheeseRow>();
            var slotByRow = new Dictionary<int, string>();
            foreach (var name in profile.Slots)
            {
                var row = RowFor(profile, room, name, out _);
                if (row != null && !slotByRow.ContainsKey(row.Id)) slotByRow[row.Id] = name;
            }
            var byId = new Dictionary<int, CtGame>();
            foreach (var g in tracker.Games) byId.TryAdd(g.Id, g);
            var hintsByFinder = (tracker.Hints ?? new List<CtHint>()).GroupBy(h => h.FinderGameId).ToDictionary(group => group.Key, group => group.ToList());
            return tracker.Games.Select(g => new CheeseRow
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                SlotName = slotByRow.GetValueOrDefault(g.Id),
                Game = g,
                Tracker = tracker,
                Mine = slotByRow.ContainsKey(g.Id) || OwnershipOf(g) is CheeseOwnership.You or CheeseOwnership.YouByName,
                UnfoundHints = CheeseTable.CountUnfoundHints(g, hintsByFinder.GetValueOrDefault(g.Id), byId)
            }).ToList();
        }

        /// <summary>Why Atlas can't change this row (site, key, someone else's claim), or null when it can.</summary>
        public string CannotEdit(string profileId, CtGame row)
        {
            var room = RoomOf(ProfileOf(profileId));
            if (room == null || row == null) return "This multiworld isn't linked to Cheese Tracker.";
            return EditBlocker(room) ?? Protection(row);
        }

        /// <summary>Whether a change to this row or slot is in flight.</summary>
        public bool IsBusy(CheeseTarget target) => _busy.Contains(target.Key);

        /// <summary>The slot card's badge: the status (and a ready suggestion), or null when the slot isn't on a tracker.</summary>
        public (string Text, Color Color, string Tooltip)? Badge(string profileId, string slotName)
        {
            var profile = ProfileOf(profileId);
            var room = RoomOf(profile);
            if (room?.Tracker == null) return null;
            var row = RowFor(profile, room, slotName, out _);
            if (row == null) return null;
            string status = CtStatus.Headline(row);
            string text = CtStatus.Label(status);
            string tip = $"Cheese Tracker: {CtStatus.Label(row.Progression)}, {CtStatus.Label(row.Completion)}";
            var advice = LiveSlot(profileId, slotName) != null ? _advice.GetValueOrDefault(SlotKey(profileId, slotName)) : null;
            if (advice?.Status != null && advice.Ready)
            {
                text += " → " + CtStatus.Label(advice.Status) + "?";
                tip += $"\nAtlas suggests {CtStatus.Label(advice.Status)}: {advice.Reason}. Open the slot's Properties to apply it.";
                return (text, Colors.Yellow, tip);
            }
            return (text, AP_Atlas.UI.CheeseColors.Of(status), tip);
        }

        // =====================================================================
        // Changes
        // =====================================================================

        /// <summary>
        /// Changes a slot's row. The row is read again first, the change is applied to that fresh copy, and only then
        /// sent, so nobody's notes or status are overwritten with an old copy.
        /// <paramref name="mutate"/> returns null to go ahead, "" to quietly do nothing, or an error to show.
        /// </summary>
        public Task<string> ChangeAsync(string profileId, string slotName, string description, Func<CtGameUpdate, CtGame, string> mutate,
            bool automatic = false, bool claimChange = false) =>
            ChangeAsync(CheeseTarget.Slot(profileId, slotName), description, mutate, automatic, claimChange);

        /// <summary>Changes one of a profile's slots, or any row of its tracker by id (claiming an open slot, say).</summary>
        public async Task<string> ChangeAsync(CheeseTarget target, string description, Func<CtGameUpdate, CtGame, string> mutate,
            bool automatic = false, bool claimChange = false)
        {
            var profile = ProfileOf(target.ProfileId);
            var room = RoomOf(profile);
            if (room == null) return "This multiworld isn't linked to Cheese Tracker.";
            string key = target.Key;
            if (!_busy.Add(key)) return "Atlas is already updating this slot.";
            RaiseChanged();
            try
            {
                await VerifyKeyAsync();
                if (!IsInstanceValid(this)) return null;
                string blocked = EditBlocker(room);
                if (blocked != null) return Failed(key, blocked, automatic);

                // Always a fresh read for the change itself (a read already in flight may predate someone's edit).
                if (room.Pending != null) await room.Pending;
                string fetchError = await FetchAsync(profile, room);
                if (!IsInstanceValid(this)) return null;
                if (fetchError != null) return Failed(key, fetchError, automatic);
                string why = "That slot is no longer on the tracker.";
                var row = target.SlotName != null ? RowFor(profile, room, target.SlotName, out why) : room.Tracker?.Games.FirstOrDefault(g => g.Id == target.RowId);
                if (row == null) return Failed(key, why, automatic);
                string name = target.SlotName ?? row.Name;
                string protectedBy = claimChange ? null : Protection(row);
                if (protectedBy != null) return Failed(key, protectedBy, automatic);

                var before = CtGameUpdate.From(row);
                var update = CtGameUpdate.From(row);
                string refused = mutate(update, row);
                if (refused != null) return refused.Length == 0 ? null : Failed(key, refused, automatic);
                if (update.SameAs(before)) return null;

                var r = await ClientFor(room.Site).UpdateGameAsync(room.TrackerId, row.Id, update, claimChange ? CtOwner.Of(row) : null);
                if (!IsInstanceValid(this)) return null;
                if (!r.Ok)
                {
                    if (r.Outcome == CtOutcome.Unauthorized) RejectKey();
                    if (r.Outcome is CtOutcome.OwnerChanged or CtOutcome.Forbidden or CtOutcome.NotFound) room.FetchedUtc = DateTime.MinValue;
                    string message = r.Outcome switch
                    {
                        CtOutcome.OwnerChanged => $"Someone changed who has {name} just now. Refresh and try again.",
                        CtOutcome.Unauthorized => "Cheese Tracker no longer accepts Atlas's API key (was it regenerated?). Add it again in Cheese Tracker → Settings.",
                        CtOutcome.Forbidden => $"Cheese Tracker didn't allow that change to {name}.",
                        CtOutcome.NotFound => $"{name}'s row is gone from the tracker.",
                        _ => r.Message
                    };
                    return Failed(key, message, automatic);
                }

                // Keep what the server saved (it can upgrade the completion status from the Archipelago tracker).
                CtGameUpdate.From(r.Value).ApplyTo(row);
                if (claimChange) row.OwnerName = row.ClaimedByUserId != null ? _settings.CheeseUserName : row.DiscordUsername;
                // Atlas knows about this status, so automatic updates carry on from it (a choice made in Atlas also
                // ends a pause: the user is looking at it).
                if (_settings.CheeseAutoSlots.Contains(key) && (row.Progression != before.Progression || !automatic))
                {
                    _settings.CheeseAutoLastSet[key] = row.Progression;
                    if (!automatic) _settings.CheeseAutoPaused.Remove(key);
                    DataManager.SaveSettings(_settings);
                }
                _errors.Remove(key);
                SaveCache(profile.Id, room);
                Logger.LogInfo($"Cheese Tracker: {name} {description}.");
                return null;
            }
            finally
            {
                _busy.Remove(key);
                RaiseChanged();
            }
        }

        private string Failed(string key, string message, bool automatic)
        {
            _errors[key] = message;
            if (automatic) Logger.LogWarning($"Cheese Tracker: an automatic update didn't go through: {message}");
            return message;
        }

        private void RejectKey()
        {
            if (_keyRejected) return;
            _keyRejected = true;
            Logger.LogWarning("Cheese Tracker no longer accepts Atlas's API key; Atlas stopped using it.");
            Notice?.Invoke("Cheese Tracker no longer accepts Atlas's API key. Add it again in Cheese Tracker → Settings.");
        }

        public Task<string> SetProgressionAsync(string profileId, string slotName, string status) => SetProgressionAsync(CheeseTarget.Slot(profileId, slotName), status);

        public Task<string> SetProgressionAsync(CheeseTarget target, string status) =>
            ChangeAsync(target, "set to " + CtStatus.Label(status), (u, row) =>
            {
                u.Progression = status;
                // Like Cheese Tracker's own page: choosing a BK status also says "still here" (resets the inactivity clock).
                if (status is "bk" or "soft_bk") u.LastChecked = CtTime.Format(DateTime.UtcNow);
                return null;
            });

        public Task<string> SetCompletionAsync(string profileId, string slotName, string status) => SetCompletionAsync(CheeseTarget.Slot(profileId, slotName), status);

        public Task<string> SetCompletionAsync(CheeseTarget target, string status) =>
            ChangeAsync(target, "marked " + CtStatus.Label(status), (u, row) =>
            {
                u.Completion = status;
                return null;
            });

        public Task<string> StillBkAsync(string profileId, string slotName) => StillBkAsync(CheeseTarget.Slot(profileId, slotName));

        public Task<string> StillBkAsync(CheeseTarget target) =>
            ChangeAsync(target, "marked still BK", (u, row) =>
            {
                u.LastChecked = CtTime.Format(DateTime.UtcNow);
                return null;
            });

        public Task<string> SetNotesAsync(string profileId, string slotName, string notes) => SetNotesAsync(CheeseTarget.Slot(profileId, slotName), notes);

        public Task<string> SetNotesAsync(CheeseTarget target, string notes) =>
            ChangeAsync(target, "notes updated", (u, row) =>
            {
                u.Notes = (notes ?? "").Trim();
                return null;
            });

        public Task<string> SetPingAsync(string profileId, string slotName, string ping) => SetPingAsync(CheeseTarget.Slot(profileId, slotName), ping);

        public Task<string> SetPingAsync(CheeseTarget target, string ping) =>
            ChangeAsync(target, "ping preference set to " + CtStatus.Label(ping), (u, row) =>
            {
                u.Ping = ping;
                return null;
            });

        public Task<string> ClaimAsync(string profileId, string slotName) => ClaimAsync(CheeseTarget.Slot(profileId, slotName));

        public Task<string> ClaimAsync(CheeseTarget target) =>
            ChangeAsync(target, "claimed as " + _settings.CheeseUserName, (u, row) =>
            {
                if (_settings.CheeseUserId == null) return "Add your API key first: a claim belongs to your Cheese Tracker account.";
                switch (OwnershipOf(row))
                {
                    case CheeseOwnership.You: return "";
                    case CheeseOwnership.SomeoneElse:
                        return $"{ClaimedBy(row)} has claimed {row.Name}. Ask them or the organizer before taking it over.";
                }
                u.ClaimedByUserId = _settings.CheeseUserId;
                u.DiscordUsername = null;
                if (u.Availability is "unknown" or "open") u.Availability = "claimed";
                return null;
            }, claimChange: true);

        public Task<string> DisclaimAsync(string profileId, string slotName) => DisclaimAsync(CheeseTarget.Slot(profileId, slotName));

        public async Task<string> DisclaimAsync(CheeseTarget target)
        {
            string error = await ChangeAsync(target, "claim released", (u, row) =>
            {
                switch (OwnershipOf(row))
                {
                    case CheeseOwnership.Nobody: return "";
                    case CheeseOwnership.SomeoneElse: return "Only whoever claimed a slot can release it.";
                }
                u.ClaimedByUserId = null;
                u.DiscordUsername = null;
                if (u.Availability == "claimed") u.Availability = "open";
                u.Ping = "never";
                return null;
            }, claimChange: true);
            if (error == null && target.SlotName != null) TurnAutoOff(target.Key);
            return error;
        }

        // =====================================================================
        // Automatic updates
        // =====================================================================

        /// <summary>Turns automatic updates on or off for a slot. Returns an error, or null.</summary>
        public string SetAuto(string profileId, string slotName, bool on)
        {
            string key = SlotKey(profileId, slotName);
            if (!on)
            {
                TurnAutoOff(key);
                RaiseChanged();
                return null;
            }
            var view = SlotView(profileId, slotName);
            if (view?.Row == null) return "This slot isn't on a linked tracker.";
            if (view.CannotEdit != null) return view.CannotEdit;
            if (view.Ownership != CheeseOwnership.You)
                return "Claim this slot first: Atlas only updates slots claimed by your Cheese Tracker account by itself.";
            if (!_settings.CheeseAutoSlots.Contains(key)) _settings.CheeseAutoSlots.Add(key);
            // From now on Atlas may change the status it sees now; any other change pauses it.
            _settings.CheeseAutoLastSet[key] = view.Row.Progression;
            _settings.CheeseAutoPaused.Remove(key);
            DataManager.SaveSettings(_settings);
            Logger.LogInfo($"Cheese Tracker: Atlas keeps {slotName}'s status updated automatically.");
            RaiseChanged();
            return null;
        }

        /// <summary>Resumes paused automatic updates, starting from the status the slot has now.</summary>
        public void ResumeAuto(string profileId, string slotName)
        {
            string key = SlotKey(profileId, slotName);
            var row = SlotView(profileId, slotName)?.Row;
            if (row != null) _settings.CheeseAutoLastSet[key] = row.Progression;
            _settings.CheeseAutoPaused.Remove(key);
            DataManager.SaveSettings(_settings);
            RaiseChanged();
        }

        private void TurnAutoOff(string key)
        {
            bool changed = _settings.CheeseAutoSlots.Remove(key) | _settings.CheeseAutoPaused.Remove(key) | _settings.CheeseAutoLastSet.Remove(key);
            if (changed) DataManager.SaveSettings(_settings);
        }

        private void PauseAuto(string key, string slotName, string reason)
        {
            if (_settings.CheeseAutoPaused.ContainsKey(key)) return;
            _settings.CheeseAutoPaused[key] = reason;
            DataManager.SaveSettings(_settings);
            Logger.LogWarning($"Cheese Tracker: automatic updates for {slotName} paused: {reason}.");
            Notice?.Invoke($"Automatic Cheese Tracker updates for {slotName} are paused: {reason}. Resume them in the slot's Properties.");
            RaiseChanged();
        }

        private List<DateTime> AutoTimesOf(string key)
        {
            if (!_autoTimes.TryGetValue(key, out var list)) _autoTimes[key] = list = new List<DateTime>();
            list.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromDays(1));
            return list;
        }

        // =====================================================================
        // The tick: reads due trackers, updates suggestions, applies automatic ones
        // =====================================================================

        private void Tick()
        {
            try
            {
                var now = DateTime.UtcNow;
                var connected = _slots().Where(s => IsInstanceValid(s) && s.Session != null).ToList();
                foreach (var profile in _profiles())
                {
                    if (string.IsNullOrWhiteSpace(profile.CheeseTrackerUrl)) continue;
                    bool active = connected.Any(s => s.ProfileId == profile.Id) || (_watchedUntil.TryGetValue(profile.Id, out var until) && until > now);
                    var room = active ? RoomOf(profile) : null;
                    if (room == null) continue;
                    AP_Atlas.Core.Async.Fire(VerifyKeyAsync(), "checking your Cheese Tracker API key");
                    if (Due(room)) AP_Atlas.Core.Async.Fire(FetchAsync(profile, room), "reading Cheese Tracker");
                }
                foreach (var slot in connected) EvaluateSlot(slot, now);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Cheese Tracker check failed: " + ex.Message);
            }
        }

        private static SlotSnapshot SnapshotOf(SlotTrackerControl s)
        {
            bool live = s.Session?.Socket?.Connected == true && s.IsFullyLoaded;
            string untrusted =
                s.LogicHidden ? "race mode hides logic" :
                !s.EngineRunning ? "its logic engine isn't running" :
                !s.LogicSettled ? "its logic is still being worked out" :
                s.ApworldMatchesSeed == false ? "its apworld doesn't match the seed" :
                s.LogicAccuracyWarning != null ? "its logic has an accuracy warning (Properties → Accuracy)" : null;
            return new SlotSnapshot
            {
                Live = live,
                Untrusted = untrusted,
                InLogic = untrusted == null ? s.ActiveLogicCount : 0,
                Remaining = s.TotalLocationsCount - s.CheckedLocationsCount,
                GoalInLogic = s.GoalInLogic,
                GoalCompleted = s.GoalCompleted
            };
        }

        /// <summary>
        /// The suggestion for a connected slot worked out right now, for display (the tick's copy decides automation and
        /// the card badge). How long it has held still comes from the tick, so "confirming" reads the same in both.
        /// </summary>
        private CheeseAdvice LiveAdvice(SlotTrackerControl slot, CtGame row)
        {
            var held = _stability.GetValueOrDefault(SlotKey(slot.ProfileId, slot.SlotName));
            var copy = new CheeseAdvisor.Stability { Status = held?.Status, SinceUtc = held?.SinceUtc ?? DateTime.UtcNow };
            return CheeseAdvisor.Advise(SnapshotOf(slot), row, copy, DateTime.UtcNow);
        }

        private void EvaluateSlot(SlotTrackerControl slot, DateTime nowUtc)
        {
            var profile = ProfileOf(slot.ProfileId);
            var room = RoomOf(profile);
            if (room?.Tracker == null) return;
            string key = SlotKey(slot.ProfileId, slot.SlotName);
            var row = RowFor(profile, room, slot.SlotName, out _);
            slot.EnsureGoalStatus();

            if (!_stability.TryGetValue(key, out var stability)) _stability[key] = stability = new CheeseAdvisor.Stability();
            var advice = CheeseAdvisor.Advise(SnapshotOf(slot), row, stability, nowUtc);
            bool changed = !advice.SameAs(_advice.GetValueOrDefault(key));
            _advice[key] = advice;
            if (changed) RaiseChanged();

            if (row == null || !_settings.CheeseAutoSlots.Contains(key) || _settings.CheeseAutoPaused.ContainsKey(key) || _busy.Contains(key) || _autoPending.Contains(key)) return;
            // A status that already agrees with Atlas's logic is adopted, whoever set it.
            if (advice.InSync && _settings.CheeseAutoLastSet.GetValueOrDefault(key) != row.Progression)
            {
                _settings.CheeseAutoLastSet[key] = row.Progression;
                DataManager.SaveSettings(_settings);
            }
            string blocker = CheeseAdvisor.AutoBlocker(advice, row, HasKey ? _settings.CheeseUserId : null,
                _settings.CheeseAutoLastSet.GetValueOrDefault(key), AutoTimesOf(key), nowUtc, out bool pause);
            if (pause)
            {
                PauseAuto(key, slot.SlotName, blocker);
                return;
            }
            if (blocker == null) AP_Atlas.Core.Async.Fire(ApplyAutomaticallyAsync(slot, advice), "updating your slot on Cheese Tracker");
        }

        private async Task ApplyAutomaticallyAsync(SlotTrackerControl slot, CheeseAdvice advice)
        {
            string key = SlotKey(slot.ProfileId, slot.SlotName), slotName = slot.SlotName, profileId = slot.ProfileId;
            if (!_autoPending.Add(key)) return;
            try
            {
                // The server knows best whether the goal is done: ask once more before changing anything.
                var status = await slot.ClientStatusAsync(slot.PlayerSlot);
                if (!IsInstanceValid(this) || status == ArchipelagoClientState.ClientGoal) return;
                await ChangeAsync(profileId, slotName, $"set to {CtStatus.Label(advice.Status)} automatically ({advice.Reason})", (u, row) =>
                {
                    // Checked again against the row as it is on Cheese Tracker right now.
                    string blocker = CheeseAdvisor.AutoBlocker(advice, row, HasKey ? _settings.CheeseUserId : null,
                        _settings.CheeseAutoLastSet.GetValueOrDefault(key), AutoTimesOf(key), DateTime.UtcNow, out bool pause);
                    if (pause) PauseAuto(key, slotName, blocker);
                    if (blocker != null) return "";
                    // Counted even if the site then fails, so a failing change is retried at most every 5 minutes.
                    AutoTimesOf(key).Add(DateTime.UtcNow);
                    u.Progression = advice.Status;
                    if (advice.Status == "bk") u.LastChecked = CtTime.Format(DateTime.UtcNow);
                    return null;
                }, automatic: true);
            }
            finally
            {
                _autoPending.Remove(key);
            }
        }

        // =====================================================================
        // Cache (the last read of each tracker, shown while offline; hints aren't kept)
        // =====================================================================

        private sealed class CacheFile
        {
            public string Link { get; set; }
            public DateTime FetchedUtc { get; set; }
            public CtTracker Tracker { get; set; }
        }

        private static string CachePath(string profileId)
        {
            string name = profileId;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(DataManager.GetDataDirectory(), "cheese", name + ".json");
        }

        private static void LoadCache(string profileId, Room room)
        {
            var cached = SafeFile.ReadJson<CacheFile>(CachePath(profileId), () => null, CheeseClient.Json);
            if (cached?.Tracker == null || cached.Link != room.Link) return;
            room.Tracker = cached.Tracker;
            room.FetchedUtc = DateTime.SpecifyKind(cached.FetchedUtc, DateTimeKind.Utc);
        }

        private static void SaveCache(string profileId, Room room)
        {
            if (room.Tracker == null) return;
            try
            {
                var copy = JsonConvert.DeserializeObject<CtTracker>(JsonConvert.SerializeObject(room.Tracker, CheeseClient.Json), CheeseClient.Json);
                copy.Hints = new List<CtHint>();
                SafeFile.WriteAllText(CachePath(profileId), JsonConvert.SerializeObject(new CacheFile { Link = room.Link, FetchedUtc = room.FetchedUtc, Tracker = copy }, Formatting.None, CheeseClient.Json));
            }
            catch (Exception ex)
            {
                Logger.LogDebug("Cheese Tracker cache not saved: " + ex.Message);
            }
        }
    }
}
