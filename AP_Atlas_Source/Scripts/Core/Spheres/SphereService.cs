#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using Newtonsoft.Json;
using FileAccess = System.IO.FileAccess;

namespace AP_Atlas.Core.Spheres
{
    /// <summary>A multiworld's spheretracker.de room, as the UI shows it.</summary>
    public sealed class SphereSourceView
    {
        /// <summary>The room's page (Atlas reads it, and "Open" shows it).</summary>
        public string Url { get; init; }
        public SphereData Data { get; init; }
        public DateTime? FetchedUtc { get; init; }
        public string Problem { get; init; }
        /// <summary>Being read, or the last read is being loaded from disk.</summary>
        public bool Busy { get; init; }
        /// <summary>The size of the page when Atlas last read it (0: not read).</summary>
        public long PageBytes { get; init; }
        /// <summary>How often Atlas reads it while it's shown.</summary>
        public TimeSpan ReadEvery { get; init; }
    }

    /// <summary>A multiworld's sphere tracker and whether it may be shown.</summary>
    public sealed class SphereView
    {
        public MultiworldProfile Profile { get; init; }
        /// <summary>Why nothing is shown right now (race mode), or null.</summary>
        public string HiddenBecause { get; init; }
        /// <summary>The host's spheretracker.de room (null unless linked, or while hidden).</summary>
        public SphereSourceView Room { get; init; }
        /// <summary>The multiworld's Archipelago tracker id, as Cheese Tracker knows it (null: unknown).</summary>
        public string TrackerId { get; init; }
        /// <summary>Who runs the multiworld's Cheese Tracker (its organizer), or null.</summary>
        public string Organizer { get; init; }
        public bool Linked => !string.IsNullOrWhiteSpace(Profile?.SphereTrackerUrl);
    }

    /// <summary>What Atlas found reading a room link, before linking it (<see cref="SphereService.CheckRoomAsync"/>).</summary>
    public sealed class SphereRoomCheck
    {
        public string ProfileId { get; init; }
        /// <summary>Why the room can't be linked at all, or null.</summary>
        public string Error { get; init; }
        public string Url { get; init; }
        /// <summary>What the room page holds (null when Atlas can't read its layout).</summary>
        public SphereData Data { get; init; }
        /// <summary>Why the room's layout couldn't be read, or null.</summary>
        public string Problem { get; init; }
        public long PageBytes { get; init; }
        /// <summary>Who created the room ("" if the page doesn't say).</summary>
        public string Creator { get; init; } = "";
        /// <summary>Who runs the multiworld's Cheese Tracker (null if Atlas doesn't know).</summary>
        public string Organizer { get; init; }

        /// <summary>The room was created by the multiworld's organizer (its Cheese Tracker owner): taken as the host's room.</summary>
        public bool ByOrganizer => SphereService.SameName(Creator, Organizer);
    }

    /// <summary>
    /// Sphere trackers for Atlas's multiworlds: only the spheretracker.de room a multiworld's host created and shared.
    /// A room is linked only when it exists (rooms need a login to create; Atlas never creates one), isn't another
    /// multiworld's (when Cheese Tracker says which tracker this one has), and was created by the multiworld's organizer on
    /// Cheese Tracker, or the user confirms its creator (the room page names them) is the host. Nothing
    /// is shown or read while race mode applies. A room is read only while the Sphere Tracker tab shows it, every 10
    /// minutes (a large page less often), Refresh at most every minute; after a failure Atlas waits longer each time, and
    /// it never asks the site to refresh a room. Main thread only.
    /// </summary>
    public sealed class SphereService
    {
        private const long MB = 1024 * 1024;

        // Limits (the self-test lowers them).
        internal static long MaxPageBytes = 32 * MB;
        internal static TimeSpan PageTimeout = TimeSpan.FromSeconds(60);
        internal static long LargePageBytes = 4 * MB;
        internal static long HugePageBytes = 16 * MB;
        internal static TimeSpan RefreshSpacing = TimeSpan.FromMinutes(1);
        internal static TimeSpan LargeRefreshSpacing = TimeSpan.FromMinutes(5);

        /// <summary>How often a shown room is read: every 10 minutes, a large page every 30, a huge one every 60.</summary>
        public static TimeSpan ReadEveryFor(long pageBytes) =>
            pageBytes > HugePageBytes ? TimeSpan.FromMinutes(60) : pageBytes > LargePageBytes ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(10);

        private static TimeSpan RefreshSpacingFor(long pageBytes) => pageBytes > LargePageBytes ? LargeRefreshSpacing : RefreshSpacing;

        private static TimeSpan RetryDelay(int failures) => TimeSpan.FromMinutes(failures switch { <= 1 => 2, 2 => 5, 3 => 15, 4 => 30, _ => 60 });

        /// <summary>Raised on the main thread when anything shown changed.</summary>
        public event Action Changed;

        private readonly Func<IReadOnlyList<MultiworldProfile>> _profiles;
        private readonly Func<IEnumerable<SlotTrackerControl>> _slots;
        private readonly Action _saveProfiles;
        private readonly Func<MultiworldProfile, (string TrackerUrl, string Organizer)> _hostOf;

        private sealed class Source
        {
            public string Url;
            public SphereData Data;
            public DateTime FetchedUtc = DateTime.MinValue;
            public long PageBytes;
            public string Problem;
            public int Failures;
            public DateTime NotBeforeUtc = DateTime.MinValue;
            public DateTime LastManualUtc = DateTime.MinValue;
            public Task<string> Pending;
            /// <summary>The last read being loaded from disk.</summary>
            public Task Loading;
            /// <summary>The last read being saved to disk.</summary>
            public Task Saving;
            /// <summary>Unlinked or replaced: whatever is still being read for it is dropped.</summary>
            public volatile bool Forgotten;
        }

        private sealed record Parsed(SphereData Data, string Error, List<string> TrackerIds, string Creator);

        private readonly Dictionary<string, Source> _sources = new Dictionary<string, Source>();

        /// <param name="hostOf">
        /// What Cheese Tracker knows of the multiworld: its Archipelago tracker link and its organizer (who runs the Cheese
        /// Tracker); nulls when it isn't linked.
        /// </param>
        public SphereService(Func<IReadOnlyList<MultiworldProfile>> profiles, Func<IEnumerable<SlotTrackerControl>> slots, Action saveProfiles,
            Func<MultiworldProfile, (string TrackerUrl, string Organizer)> hostOf)
        {
            _profiles = profiles;
            _slots = slots;
            _saveProfiles = saveProfiles;
            _hostOf = hostOf;
            DropArchipelagoCaches();
        }

        /// <summary>The same person: names compared ignoring case and spaces (Discord shows "ElderWisp" or "elderwisp").</summary>
        public static bool SameName(string a, string b)
        {
            static string Plain(string s) => new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
            return Plain(a).Length > 0 && Plain(a) == Plain(b);
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); } catch (Exception ex) { Logger.LogWarning("Sphere view refresh failed: " + ex.Message); }
        }

        private MultiworldProfile ProfileOf(string profileId) => _profiles().FirstOrDefault(p => p.Id == profileId);

        // =====================================================================
        // What may be shown
        // =====================================================================

        /// <summary>
        /// Why spheres are hidden for this multiworld (race mode), or null. The room's race state is remembered from a
        /// connected slot, so a race room stays hidden while offline too.
        /// </summary>
        public string HiddenBecause(MultiworldProfile profile)
        {
            if (profile == null) return "That multiworld no longer exists.";
            var live = _slots().FirstOrDefault(s => GodotObject.IsInstanceValid(s) && s.ProfileId == profile.Id && s.Session != null && s.RaceStateKnown);
            if (live != null && profile.RaceRoom != live.IsRaceRoom)
            {
                profile.RaceRoom = live.IsRaceRoom;
                _saveProfiles();
            }
            bool race = profile.RaceRoom == true;
            if (!RaceRules.IsActive(race)) return null;
            return race ? "This multiworld's room is a race, so race mode applies (Settings → Race Mode)." : "Race mode is set to Always On (Settings → Race Mode).";
        }

        /// <summary>The multiworld's Archipelago tracker id, as Cheese Tracker knows it, or null.</summary>
        public string KnownTrackerId(MultiworldProfile profile)
        {
            string url = profile == null || _hostOf == null ? null : _hostOf(profile).TrackerUrl;
            var link = string.IsNullOrWhiteSpace(url) ? null : ApTrackerLink.Parse(url, out _);
            return link?.Kind == ApTrackerLink.LinkKind.Tracker ? link.Id : null;
        }

        /// <summary>Who runs the multiworld's Cheese Tracker (its organizer), or null.</summary>
        public string OrganizerOf(MultiworldProfile profile)
        {
            string name = profile == null || _hostOf == null ? null : _hostOf(profile).Organizer;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }

        public SphereView ViewOf(string profileId)
        {
            var profile = ProfileOf(profileId);
            if (profile == null) return null;
            string hidden = HiddenBecause(profile);
            SphereSourceView room = null;
            if (hidden == null && !string.IsNullOrWhiteSpace(profile.SphereTrackerUrl))
            {
                var s = SourceOf(profile.Id, profile.SphereTrackerUrl);
                room = new SphereSourceView
                {
                    Url = s.Url,
                    Data = s.Data,
                    FetchedUtc = s.FetchedUtc == DateTime.MinValue ? null : s.FetchedUtc,
                    Problem = s.Problem ?? WaitingProblem(s.Url),
                    Busy = s.Pending != null || s.Loading != null,
                    PageBytes = s.PageBytes,
                    ReadEvery = ReadEveryFor(s.PageBytes)
                };
            }
            return new SphereView { Profile = profile, HiddenBecause = hidden, Room = room, TrackerId = KnownTrackerId(profile), Organizer = OrganizerOf(profile) };
        }

        private static string WaitingProblem(string url)
        {
            var wait = PoliteHttp.WaitingFor(PoliteHttp.NormalizeSite(url));
            return wait == null ? null : $"{wait.Value.Reason}. Atlas tries again after {wait.Value.UntilUtc.ToLocalTime():HH:mm}.";
        }

        // =====================================================================
        // Reading (only while the tab shows a multiworld's room)
        // =====================================================================

        private Source SourceOf(string profileId, string url)
        {
            if (_sources.TryGetValue(profileId, out var s))
            {
                if (s.Url == url) return s;
                s.Forgotten = true; // the link changed: the old room is no longer this multiworld's
            }
            s = new Source { Url = url };
            _sources[profileId] = s;
            StartLoading(profileId, s);
            return s;
        }

        /// <summary>The linked room's source, or null when there's none or spheres are hidden.</summary>
        private Source ShownSource(MultiworldProfile profile) =>
            profile == null || string.IsNullOrWhiteSpace(profile.SphereTrackerUrl) || HiddenBecause(profile) != null ? null : SourceOf(profile.Id, profile.SphereTrackerUrl);

        private static bool Due(Source s)
        {
            if (s.Pending != null || s.Loading != null || DateTime.UtcNow < s.NotBeforeUtc) return false;
            if (s.FetchedUtc == DateTime.MinValue || s.FetchedUtc > DateTime.UtcNow.AddMinutes(5)) return true; // never read, or the clock moved back
            return DateTime.UtcNow - s.FetchedUtc >= ReadEveryFor(s.PageBytes);
        }

        /// <summary>Read (or asked to be read) moments ago: reading again would only show the same page and load the site.</summary>
        private static bool ReadRecently(Source s)
        {
            var last = s.LastManualUtc > s.FetchedUtc ? s.LastManualUtc : s.FetchedUtc;
            return last <= DateTime.UtcNow && DateTime.UtcNow - last < RefreshSpacingFor(s.PageBytes);
        }

        /// <summary>The tab shows this multiworld's room: read it if it's due.</summary>
        public void Watch(string profileId)
        {
            var s = ShownSource(ProfileOf(profileId));
            if (s != null && Due(s)) AP_Atlas.Core.Async.Fire(FetchAsync(s, profileId), "reading spheretracker.de");
        }

        /// <summary>True when the room was read moments ago (Refresh would do nothing).</summary>
        public bool ReadRecently(string profileId)
        {
            var s = ShownSource(ProfileOf(profileId));
            return s != null && ReadRecently(s);
        }

        /// <summary>Reads the room now (at most every minute, or 5 for a large page). Returns an error, or null.</summary>
        public async Task<string> RefreshAsync(string profileId, bool tryNow = false)
        {
            var profile = ProfileOf(profileId);
            if (profile == null) return "That multiworld no longer exists.";
            string hidden = HiddenBecause(profile);
            if (hidden != null) return hidden;
            var s = ShownSource(profile);
            if (s == null || ReadRecently(s)) return null;
            s.LastManualUtc = DateTime.UtcNow;
            if (tryNow) PoliteHttp.StopWaiting(PoliteHttp.NormalizeSite(s.Url));
            s.NotBeforeUtc = DateTime.MinValue;
            return await FetchAsync(s, profileId);
        }

        private Task<string> FetchAsync(Source s, string profileId)
        {
            if (s.Pending != null) return s.Pending;
            var read = FetchCoreAsync(s, profileId);
            // A read can end at once (the site is being left alone after trouble); it has cleared Pending already, and
            // setting it now would leave the room "reading" for good.
            if (!read.IsCompleted) s.Pending = read;
            RaiseChanged();
            return read;
        }

        private static Task<WebResponse> ReadPageAsync(string url)
        {
            var uri = new Uri(url);
            return PoliteHttp.GetParsedAsync(PoliteHttp.NormalizeSite(url), uri.Host, uri.PathAndQuery, reader =>
            {
                string html = reader.ReadToEnd();
                var data = SphereParser.ParseSphereSite(html, out string error);
                return new Parsed(data, error, SphereParser.TrackerIdsIn(html), SphereParser.CreatorIn(html));
            }, MaxPageBytes, PageTimeout);
        }

        private async Task<string> FetchCoreAsync(Source s, string profileId)
        {
            try
            {
                var r = await ReadPageAsync(s.Url);
                if (s.Forgotten) return null;
                if (!r.Ok)
                {
                    s.Problem = r.Outcome switch
                    {
                        WebOutcome.NotFound => "spheretracker.de has no room at this link any more.",
                        WebOutcome.Rejected when r.Status >= 300 && r.Status < 400 =>
                            "spheretracker.de sent Atlas elsewhere: the room was removed, or it now needs you to log in. Open it in your browser.",
                        WebOutcome.TooLarge => $"This room's page is over {MaxPageBytes / MB} MB, more than Atlas reads, so Atlas won't read it again on its own (Refresh tries again).",
                        _ => r.Message
                    };
                    if (r.Outcome == WebOutcome.Waiting)
                    {
                        // Nothing was sent: try again once the site's wait is over.
                        s.NotBeforeUtc = PoliteHttp.WaitingFor(PoliteHttp.NormalizeSite(s.Url))?.UntilUtc ?? DateTime.UtcNow + RetryDelay(1);
                        return s.Problem;
                    }
                    s.Failures++;
                    // A room that's gone, refused or too large isn't asked for again until the user acts; other failures
                    // wait longer each time (2, 5, 15, 30, then 60 minutes).
                    bool stop = r.Outcome is WebOutcome.NotFound or WebOutcome.Forbidden or WebOutcome.TooLarge || r.Status is >= 300 and < 400;
                    s.NotBeforeUtc = stop ? DateTime.MaxValue : DateTime.UtcNow + RetryDelay(s.Failures);
                    if (stop) Logger.LogWarning($"Sphere Tracker: {s.Problem}");
                    return s.Problem;
                }
                var parsed = (Parsed)r.Value;
                if (parsed.Data == null)
                {
                    s.Problem = parsed.Error;
                    s.Failures++;
                    var wait = RetryDelay(s.Failures);
                    var every = ReadEveryFor(r.Bytes);
                    s.NotBeforeUtc = DateTime.UtcNow + (wait > every ? wait : every);
                    return parsed.Error;
                }
                s.Data = parsed.Data;
                s.FetchedUtc = DateTime.UtcNow;
                s.PageBytes = r.Bytes;
                s.Problem = null;
                s.Failures = 0;
                s.NotBeforeUtc = DateTime.MinValue;
                SaveCacheInBackground(profileId, s);
                return null;
            }
            catch (Exception ex)
            {
                s.Problem = "Reading the sphere tracker failed: " + ex.Message;
                s.Failures++;
                s.NotBeforeUtc = DateTime.UtcNow + RetryDelay(s.Failures);
                return s.Problem;
            }
            finally
            {
                s.Pending = null;
                RaiseChanged();
            }
        }

        // =====================================================================
        // Linking the host's room
        // =====================================================================

        /// <summary>
        /// Reads a room link once to see whether it can be linked: it must be a room, the room must exist (the site redirects
        /// for rooms nobody created) and not be another multiworld's (when Cheese Tracker knows this multiworld's tracker and
        /// the page links to a different one). The result says who created the room, and whether that's the multiworld's
        /// organizer on Cheese Tracker.
        /// </summary>
        public async Task<SphereRoomCheck> CheckRoomAsync(string profileId, string text)
        {
            var profile = ProfileOf(profileId);
            if (profile == null) return new SphereRoomCheck { ProfileId = profileId, Error = "That multiworld no longer exists." };
            string url = SphereSite.Normalize(text, out string error, out string roomId);
            if (url == null) return new SphereRoomCheck { ProfileId = profileId, Error = error };
            var r = await ReadPageAsync(url);
            if (!r.Ok)
                return new SphereRoomCheck
                {
                    ProfileId = profileId,
                    Error = r.Status is >= 300 and < 400
                        ? "spheretracker.de has no room at that link: the host hasn't created it (or it needs you to log in). Check the link with your host."
                        : r.Outcome == WebOutcome.NotFound ? "spheretracker.de has no room at that link." : r.Message
                };
            var parsed = (Parsed)r.Value;
            if (parsed.Data == null && parsed.Error != null && parsed.Error.Contains("hasn't been created"))
                return new SphereRoomCheck { ProfileId = profileId, Error = parsed.Error };
            string known = KnownTrackerId(profile);
            if (known != null && !string.Equals(roomId, known, StringComparison.Ordinal) && parsed.TrackerIds.Count > 0 && !parsed.TrackerIds.Contains(known))
                return new SphereRoomCheck
                {
                    ProfileId = profileId,
                    Error = $"That room is for another multiworld: it links to Archipelago tracker {parsed.TrackerIds[0]}, but {profile.Name}'s is {known} (from Cheese Tracker). Ask your host for this multiworld's room."
                };
            return new SphereRoomCheck
            {
                ProfileId = profileId,
                Url = url,
                Data = parsed.Data,
                Problem = parsed.Data == null ? parsed.Error : null,
                PageBytes = r.Bytes,
                Creator = parsed.Creator ?? "",
                Organizer = OrganizerOf(profile)
            };
        }

        /// <summary>
        /// Links a checked room: only when it was created by the multiworld's organizer on Cheese Tracker, or the user
        /// confirms its creator is the host (<paramref name="hostConfirmed"/>). Returns an error, or null.
        /// </summary>
        public string LinkRoom(SphereRoomCheck check, bool hostConfirmed)
        {
            var profile = ProfileOf(check?.ProfileId);
            if (profile == null) return "That multiworld no longer exists.";
            if (check.Error != null) return check.Error;
            if (!check.ByOrganizer && !hostConfirmed)
                return check.Creator.Length > 0
                    ? $"Only link this room if {check.Creator}, who created it, is {profile.Name}'s host."
                    : $"Only link a room that {profile.Name}'s host created and shared.";
            profile.SphereTrackerUrl = check.Url;
            _saveProfiles();
            Forget(profile.Id);
            var s = SourceOf(profile.Id, check.Url);
            s.Data = check.Data;
            s.FetchedUtc = DateTime.UtcNow;
            s.PageBytes = check.PageBytes;
            // A room whose layout Atlas can't read yet still links (its page opens in the browser).
            s.Problem = check.Problem;
            if (check.Data != null) SaveCacheInBackground(profile.Id, s);
            string who = check.Creator.Length == 0 ? "its creator isn't shown; you confirmed the host made it"
                : check.ByOrganizer ? $"created by {check.Creator}, who runs its Cheese Tracker"
                : $"created by {check.Creator}; you confirmed they're the host";
            Logger.LogInfo($"Sphere Tracker: {profile.Name} is linked to its host's room {check.Url} ({who}).");
            RaiseChanged();
            return null;
        }

        /// <summary>For the self-test: checks and links a room in one go.</summary>
        internal async Task<string> LinkSphereSiteAsync(string profileId, string text, bool hostConfirmed)
        {
            var check = await CheckRoomAsync(profileId, text);
            return LinkRoom(check, hostConfirmed);
        }

        public void UnlinkSphereSite(string profileId)
        {
            var profile = ProfileOf(profileId);
            if (profile == null) return;
            profile.SphereTrackerUrl = "";
            _saveProfiles();
            Forget(profile.Id);
            RaiseChanged();
        }

        /// <summary>A multiworld was deleted: drop what Atlas read for it.</summary>
        public void ForgetProfile(string profileId) => Forget(profileId);

        private void Forget(string profileId)
        {
            if (_sources.Remove(profileId, out var s)) s.Forgotten = true;
            try { SafeFile.Delete(CachePath(profileId)); }
            catch (Exception ex) { Logger.LogWarning($"Couldn't delete a sphere cache: {ex.Message}"); }
        }

        /// <summary>For the self-test: waits until the multiworld's room has loaded, finished reading and been saved.</summary>
        internal async Task WhenIdleAsync(string profileId)
        {
            var profile = ProfileOf(profileId);
            if (profile == null || string.IsNullOrWhiteSpace(profile.SphereTrackerUrl)) return;
            var s = SourceOf(profile.Id, profile.SphereTrackerUrl);
            if (s.Loading != null) await s.Loading;
            if (s.Pending != null) await s.Pending;
            if (s.Saving != null) await s.Saving;
        }

        // =====================================================================
        // Cache: the last read of each room, shown until the next one (read and written off the main thread)
        // =====================================================================

        private const int CacheVersion = 5;

        private sealed class CacheFile
        {
            public int Version { get; set; }
            public string Url { get; set; }
            public DateTime FetchedUtc { get; set; }
            public long PageBytes { get; set; }
            public string Title { get; set; } = "";
            public string RoomName { get; set; } = "";
            public string Creator { get; set; } = "";
            public DateTime? UpdatedUtc { get; set; }
            public List<PageTable> Tables { get; set; } = new List<PageTable>();
            public List<string> TrackerIds { get; set; } = new List<string>();
        }

        private sealed record Cached(SphereData Data, DateTime FetchedUtc, long PageBytes);

        private static string CacheFolder => Path.Combine(DataManager.GetDataDirectory(), "spheres");

        private static string CachePath(string profileId)
        {
            string name = profileId;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(CacheFolder, name + "_st.json.gz");
        }

        /// <summary>Atlas no longer reads Archipelago's own sphere page: what an earlier version kept from it goes.</summary>
        private static void DropArchipelagoCaches()
        {
            try
            {
                if (!Directory.Exists(CacheFolder)) return;
                foreach (var file in Directory.GetFiles(CacheFolder, "*_ap.json*")) File.Delete(file);
                foreach (var file in Directory.GetFiles(CacheFolder, "*_st.json")) File.Delete(file);
            }
            catch (Exception ex)
            {
                Logger.LogDebug("Old sphere caches not removed: " + ex.Message);
            }
        }

        private void StartLoading(string profileId, Source s)
        {
            string path = CachePath(profileId);
            if (!File.Exists(path)) return;
            var load = LoadAsync(s, path);
            if (!load.IsCompleted) s.Loading = load;
        }

        private async Task LoadAsync(Source s, string path)
        {
            try
            {
                string url = s.Url;
                var cached = await Task.Run(() => ReadCache(path, url));
                // Keep a read that finished meanwhile (it's newer).
                if (cached == null || s.Forgotten || s.FetchedUtc >= cached.FetchedUtc) return;
                s.Data = cached.Data;
                s.FetchedUtc = cached.FetchedUtc;
                s.PageBytes = cached.PageBytes;
            }
            catch (Exception ex)
            {
                Logger.LogDebug("Sphere cache not read: " + ex.Message);
            }
            finally
            {
                s.Loading = null;
                RaiseChanged();
            }
        }

        private static Cached ReadCache(string path, string url)
        {
            try
            {
                CacheFile file;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gz = new GZipStream(stream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, Encoding.UTF8))
                using (var json = new JsonTextReader(reader))
                    file = JsonSerializer.CreateDefault().Deserialize<CacheFile>(json);
                if (file == null || file.Version != CacheVersion || file.Url != url) return null;
                var data = new SphereData
                {
                    Title = file.Title ?? "",
                    RoomName = file.RoomName ?? "",
                    Creator = file.Creator ?? "",
                    UpdatedUtc = file.UpdatedUtc == null ? null : DateTime.SpecifyKind(file.UpdatedUtc.Value, DateTimeKind.Utc),
                    Tables = file.Tables ?? new List<PageTable>(),
                    TrackerIds = file.TrackerIds ?? new List<string>()
                };
                return new Cached(data, DateTime.SpecifyKind(file.FetchedUtc, DateTimeKind.Utc), file.PageBytes);
            }
            catch (Exception ex)
            {
                // Only a cache: a damaged one is dropped and the room read again.
                Logger.LogDebug($"Sphere cache {Path.GetFileName(path)} unreadable ({ex.Message}); dropped.");
                try { SafeFile.Delete(path); }
                catch (Exception deleteFailure) { Logger.LogDebug($"Couldn't delete it either: {deleteFailure.Message}"); }
                return null;
            }
        }

        private static void SaveCacheInBackground(string profileId, Source s)
        {
            var data = s.Data;
            var file = new CacheFile
            {
                Version = CacheVersion,
                Url = s.Url,
                FetchedUtc = s.FetchedUtc,
                PageBytes = s.PageBytes,
                Title = data.Title,
                RoomName = data.RoomName,
                Creator = data.Creator,
                UpdatedUtc = data.UpdatedUtc,
                Tables = data.Tables,
                TrackerIds = data.TrackerIds
            };
            string path = CachePath(profileId);
            s.Saving = Task.Run(() =>
            {
                try
                {
                    using var buffer = new MemoryStream();
                    using (var gz = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                    using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
                    using (var json = new JsonTextWriter(writer))
                        JsonSerializer.CreateDefault().Serialize(json, file);
                    if (s.Forgotten) return;
                    SafeFile.WriteAllBytes(path, buffer.ToArray());
                    // Unlinked while this was written: don't leave it behind.
                    if (s.Forgotten) SafeFile.Delete(path);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug("Sphere cache not saved: " + ex.Message);
                }
            });
        }
    }
}
