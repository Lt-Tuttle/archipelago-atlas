using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Packets;
using Godot;
using Color = Godot.Color;
using Logger = AP_Atlas.Core.Logger;

namespace AP_Atlas.UI
{
    public partial class PropertiesPanel
    {
        private static readonly Color Good = Colors.LimeGreen;
        private static readonly Color Bad = Colors.Salmon;
        private static readonly Color Muted = Colors.DimGray;
        private static readonly Color Warn = Colors.Orange;

        private (string SlotKey, string EntityKey)? _currentAnnotation;

        // Packs already read in the background; a pack that fails to parse is shown without details instead of retried.
        private readonly HashSet<string> _packReadsAttempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void BuildFor(InspectTarget t)
        {
            _currentAnnotation = null;
            switch (t?.Kind)
            {
                case null: BuildSlot(null); break;
                case InspectKind.Slot: BuildSlot(t); break;
                case InspectKind.Location: BuildLocation(t); break;
                case InspectKind.Item: BuildItem(t); break;
                case InspectKind.Player: BuildPlayer(t); break;
                case InspectKind.Hint: BuildHint(t); break;
                case InspectKind.PackLocation: BuildPin(t); break;
                case InspectKind.Map: BuildMap(t); break;
                case InspectKind.Pack: BuildPack(t); break;
                case InspectKind.Profile: BuildProfile(t); break;
            }
        }

        // =====================================================================
        // Lookups
        // =====================================================================

        private SlotTrackerControl SlotByName(string profileId, string slotName) =>
            _host.ConnectedSlots.FirstOrDefault(s => IsInstanceValid(s) && s.ProfileId == profileId && s.SlotName == slotName && s.Session != null);

        /// <summary>The connected slot whose session answers questions about this target.</summary>
        private SlotTrackerControl ViewSlot(InspectTarget t) =>
            SlotByName(t.ProfileId, t.ViewSlot) ??
            _host.ConnectedSlots.FirstOrDefault(s => IsInstanceValid(s) && s.ProfileId == t.ProfileId && s.Session != null);

        /// <summary>The connected slot that owns a player number (same profile and team), if any.</summary>
        private SlotTrackerControl SlotForPlayer(SlotTrackerControl view, int player)
        {
            if (view == null) return null;
            if (view.PlayerSlot == player) return view;
            return _host.ConnectedSlots.FirstOrDefault(s => IsInstanceValid(s) && s.Session != null &&
                s.ProfileId == view.ProfileId && s.Team == view.Team && s.PlayerSlot == player);
        }

        private static string PlayerName(ArchipelagoSession s, int slot)
        {
            if (slot == 0) return "Archipelago (server)";
            string name = s?.Players.GetPlayerAlias(slot);
            if (string.IsNullOrEmpty(name)) name = s?.Players.GetPlayerName(slot);
            return string.IsNullOrEmpty(name) ? $"Slot {slot}" : name;
        }

        private static string RealName(ArchipelagoSession s, int slot) => s?.Players.GetPlayerName(slot) ?? "";

        private static string GameOf(ArchipelagoSession s, int slot) => slot == 0 ? "Archipelago" : s?.Players.GetPlayerInfo(slot)?.Game ?? "";

        private static (string Text, Color Color) ClassOf(ItemFlags flags)
        {
            if (flags.HasFlag(ItemFlags.Advancement)) return ("Progression", Colors.Plum);
            if (flags.HasFlag(ItemFlags.NeverExclude)) return ("Useful", Colors.SlateBlue);
            if (flags.HasFlag(ItemFlags.Trap)) return ("Trap", Colors.Salmon);
            return ("Filler", Colors.Cyan);
        }

        private static (string Text, Color Color) ClassOfPoolFlags(int flags) =>
            (flags & 1) != 0 ? ("Progression", Colors.Plum) : (flags & 2) != 0 ? ("Useful", Colors.SlateBlue) : (flags & 4) != 0 ? ("Trap", Colors.Salmon) : ("Filler", Colors.Cyan);

        private static string StatusText(HintStatus s, bool found) => found ? "Found" : s switch
        {
            HintStatus.Priority => "Priority",
            HintStatus.NoPriority => "No Priority",
            HintStatus.Avoid => "Avoid",
            _ => "Unspecified"
        };

        private static Color StatusColor(HintStatus s, bool found) => found ? Good : s switch
        {
            HintStatus.Priority => Colors.Gold,
            HintStatus.NoPriority => Colors.SlateBlue,
            HintStatus.Avoid => Bad,
            _ => Colors.LightGray
        };

        private static string YesNo(bool v) => v ? "Yes" : "No";

        private IEnumerable<Hint> AllKnownHints(SlotTrackerControl view)
        {
            // Each slot tracks its own hints; merge those of every connected slot in the same multiworld.
            var seen = new HashSet<string>();
            foreach (var s in _host.ConnectedSlots.Where(s => IsInstanceValid(s) && view != null && s.ProfileId == view.ProfileId && s.Team == view.Team))
            {
                foreach (var h in s.CurrentHints)
                {
                    if (seen.Add(h.FindingPlayer + ":" + h.LocationId)) yield return h;
                }
            }
        }

        private InspectTarget PlayerT(SlotTrackerControl view, int player) =>
            view == null ? null : InspectTarget.ForPlayer(view.ProfileId, view.SlotName, player);

        private InspectTarget LocationT(SlotTrackerControl view, int player, long id) =>
            view == null ? null : InspectTarget.ForLocation(view.ProfileId, view.SlotName, player, id);

        private InspectTarget ItemT(SlotTrackerControl view, int player, long id, string name, int receipt = -1) =>
            view == null ? null : InspectTarget.ForItem(view.ProfileId, view.SlotName, player, id, name, receipt);

        private InspectTarget HintT(SlotTrackerControl view, Hint h) =>
            view == null ? null : InspectTarget.ForHint(view.ProfileId, view.SlotName, h.FindingPlayer, h.LocationId);

        private string PlayerLink(SlotTrackerControl view, int player) =>
            LinkTo(PlayerName(view?.Session, player), PlayerT(view, player), player == view?.PlayerSlot ? Colors.Magenta : Colors.Yellow);

        private string LocationLink(SlotTrackerControl view, int player, long id)
        {
            string name = view?.Session?.Locations.GetLocationNameFromId(id, GameOf(view.Session, player)) ?? $"Location {id}";
            return LinkTo(name, LocationT(view, player, id), Colors.LightGreen);
        }

        private string ItemLink(SlotTrackerControl view, int player, long id, ItemFlags flags, int receipt = -1)
        {
            string name = view?.Session?.Items.GetItemName(id, GameOf(view.Session, player)) ?? $"Item {id}";
            return LinkTo(name, ItemT(view, player, id, name, receipt), ClassOf(flags).Color);
        }

        /// <summary>Makes a slot current and switches to a tab (used by Find/Show actions).</summary>
        private void Go(SlotTrackerControl slot, int tab)
        {
            if (slot != null && slot != _host.SelectedSlot) _host.SelectSlot(slot);
            _host.ShowGlobalTab(tab);
        }

        private void NotConnected(string kind, string title)
        {
            SetHeader(kind, title, Colored("No connected slot of this multiworld; connect one to see live details.", Muted), Muted, "Offline");
        }

        // =====================================================================
        // Slot summary (also the empty state)
        // =====================================================================

        /// <summary>
        /// How far this slot's information can be trusted, item by item: what comes straight from the server, how the
        /// logic was rebuilt and checked, whether the apworld matches the seed, whether its logic was proven against a
        /// real seed, and how much of the seed the map pack covers.
        /// </summary>
        private void BuildAccuracy(SlotTrackerControl slot)
        {
            Section("Accuracy");
            Row("Checks, items, hints", Colored("From the server (exact)", Good), "Atlas shows these exactly as the server reports them");
            if (slot.LogicHidden) { Row("Logic", Colored("Hidden by race mode", Muted)); return; }
            if (!slot.EngineRunning)
            {
                Row("Logic", Colored(slot.EngineProblem != null ? "Not running: " + slot.EngineProblem.Message : "Not running", Muted));
                return;
            }

            var info = slot.EngineYamlInfo;
            string source = info?["source"]?.ToString();
            string file = info?["file"]?.ToString();
            PlainRow("Logic rebuilt from", source switch
            {
                "not_needed" => "The server's data (this game needs no YAML)",
                "slot_data" => $"Options in the server's data ({info?["options_from_slot_data"]} of {info?["options_total"]})",
                "linked" => "Your linked YAML " + file,
                "players" => file + " in the Players folder",
                _ => "The engine"
            });

            var match = info?["match"];
            if (match?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                Row("Locations vs server", (bool)match
                    ? Colored($"All {info["expected"]} match", Good)
                    : Colored($"{info["missing"]} missing, {info["extra"]} extra: logic is approximate", Warn),
                    "The rebuilt world's locations compared with the list the server sent for this slot");

            string serverChecksum = slot.ServerChecksumFor(slot.Game), localChecksum = slot.LogicEngine?.LastDataChecksum;
            string Short(string c) => string.IsNullOrEmpty(c) ? "?" : c.Substring(0, Math.Min(8, c.Length));
            if (slot.ApworldMatchesSeed == true)
                Row("Apworld vs seed", Colored(slot.UsingSeedApworld
                        ? $"Same version: Atlas's copy of {slot.InstalledWorldVersion ?? "the seed's version"} (data {Short(localChecksum)})"
                        : $"Same version (data {Short(localChecksum)})", Good),
                    slot.UsingSeedApworld ? "This slot runs on the apworld version its seed was made with, from Atlas's cache. Your Archipelago install isn't changed."
                        : "The installed apworld's data checksum equals the one the server reports for this seed");
            else if (slot.ApworldMatchesSeed == false)
                Row("Apworld vs seed", Colored($"Different version (installed {Short(localChecksum)}, seed {Short(serverChecksum)})", Warn),
                    "Fix automatically on the Logic Tracker's banner finds the seed's version and uses it for this slot (your install isn't changed)");
            else PlainRow("Apworld vs seed", "Not reported by the server or engine");

            var installedCopy = AP_Atlas.Core.EngineSetup.ApworldSources.InstalledCopies(slot.LogicEngine?.Install, slot.Game).FirstOrDefault();
            if (installedCopy.Sha256 != null)
            {
                var (repo, tag) = AP_Atlas.Core.EngineSetup.ApworldSources.KnownSourceOf(installedCopy.Sha256);
                PlainRow("Installed apworld", repo != null ? $"{tag} from github.com/{repo}" : System.IO.Path.GetFileName(installedCopy.File) + " (source not looked up yet)");
            }

            var tested = AP_Atlas.Core.EngineSetup.SeedVerifier.For(slot.Game, localChecksum);
            if (tested == null)
                Row("Proven on a real seed", Colored("Not yet", Muted), "Atlas Engine → Verify logic against a seed replays a generated seed's playthrough to prove this game's logic");
            else if (tested.Exact)
                Row("Proven on a real seed", Colored($"Exact on seed {tested.SeedName} ({tested.Spheres} spheres, {tested.Tested:d})", Good), tested.Verdict);
            else
                Row("Proven on a real seed", Colored($"Differs on seed {tested.SeedName}: {tested.Late} late, {tested.Early} early", Warn), tested.Verdict);

            if (slot.PackIndex != null)
            {
                var all = slot.Session.Locations.AllLocations;
                int placed = all.Count(id => slot.PackIndex.ByLocation.ContainsKey(id));
                int pct = all.Count > 0 ? (int)Math.Round(100.0 * placed / all.Count) : 0;
                Row("Map pack coverage", Colored($"{placed} / {all.Count} of your locations have a pin ({pct}%)", pct >= 95 ? Good : pct >= 70 ? Colors.White : Warn),
                    "Locations with no pin still appear in the Logic Tracker; the Pack Doctor can add or link pins");
            }

            string warning = slot.LogicAccuracyWarning;
            if (warning != null) AddText(Colored("⚠ " + warning, Warn));
            else if (match?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && (bool)match && slot.ApworldMatchesSeed != false)
                AddHint(tested?.Exact == true ? "Everything Atlas can check matches the seed, and this game's logic is proven exact on a real seed."
                    : "Everything Atlas can check matches the seed. Verifying against a generated seed would prove the rules too.");
        }

        private void BuildSlot(InspectTarget t)
        {
            var slot = t == null ? _host.SelectedSlot : SlotByName(t.ProfileId, t.SlotName);
            if (slot != null && !IsInstanceValid(slot)) slot = null;

            if (slot == null && t == null)
            {
                _kindLabel.Text = "SUMMARY";
                SetHeader("Summary", "Nothing selected", Colored("Select a slot, or click any item, location, player, hint, map, pack or profile to see everything about it here.", Muted), Muted, "");
                BuildOverviewOfConnections();
                return;
            }
            if (slot == null)
            {
                BuildOfflineSlot(t);
                return;
            }

            var s = slot.Session;
            bool live = s.Socket.Connected;
            var profile = _host.Profiles.FirstOrDefault(p => p.Id == slot.ProfileId);
            SetHeader("Slot", slot.SlotName,
                $"{Colored(slot.Game, Colors.LightGray)}  ·  {LinkTo(profile?.Name ?? "Profile", InspectTarget.ForProfile(slot.ProfileId), LinkColor)}",
                live ? Good : Bad, live ? "Live" : "Disconnected");

            BeginActions();
            if (slot != _host.SelectedSlot) AddAction("Select slot", "Make this the selected slot", () => _host.SelectSlot(slot));
            if (live) AddAction("Disconnect", "Close this slot's connection", () => _host.DisconnectSlot(slot.ProfileId, slot.SlotName));
            AddAction("Map", "Map Tracker", () => Go(slot, 2));
            AddAction("Key Items", "Key Items", () => Go(slot, 3));
            AddAction("Logic", "Logic Tracker", () => Go(slot, 4));
            AddAction("History", "Item History", () => Go(slot, 5));
            AddAction("Hints", "Hints", () => Go(slot, 6));
            AddAction("Export specials…", $"Save {slot.Game}'s special items and locations to a file you can share", () => ExportSpecials(slot.Game));
            AddAction("Import specials…", "Load a shared special list", ImportSpecials);
            AddAction("Flag labels…", "Rename the five flag colors", ShowFlagLabelDialog);
            AddAction("Exclusions from YAML…", "Exclude the locations your player YAML lists under exclude_locations (the server doesn't share them with trackers)", () => ImportYamlExclusions(slot));
            int myExclusions = Annotations.ExclusionOverridesFor(slot.AnnotationKey).Count;
            if (myExclusions > 0)
                AddAction("Reset exclusions…", $"Forget the {myExclusions} location(s) you excluded or included, and use the seed's exclusions only", () => ConfirmResetExclusions(slot, myExclusions));
            EndActions();

            Section("Overview");
            PlainRow("Game", slot.Game);
            PlainRow("Player slot", slot.PlayerSlot.ToString());
            PlainRow("Team", (slot.Team + 1).ToString());
            Row("Profile", LinkTo(profile?.Name ?? "?", InspectTarget.ForProfile(slot.ProfileId)));
            PlainRow("Server", profile?.ServerUrl);
            Row("Connection", live ? Colored("Connected", Good) : Colored("Disconnected", Bad));
            PlainRow("Seed", s.RoomState?.Seed);
            PlainRow("Server version", s.RoomState?.Version?.ToString());
            PlainRow("Generated with", s.RoomState?.GeneratorVersion?.ToString());

            Section("Progress");
            int total = slot.TotalLocationsCount, done = slot.CheckedLocationsCount;
            int pct = total > 0 ? (int)Math.Round(100.0 * done / total) : 0;
            Row("Locations", Colored($"{done} / {total} checked ({pct}%)", pct >= 100 ? Good : Colors.White));
            PlainRow("Remaining", (total - done).ToString());
            if (slot.LogicHidden)
            {
                Row("Logic", Colored("Hidden by race mode", Muted));
            }
            else if (slot.EngineRunning)
            {
                Row("In logic now", Colored(slot.ActiveLogicCount.ToString(), slot.ActiveLogicCount > 0 ? Good : Muted));
                var checkedSet = new HashSet<long>(s.Locations.AllLocationsChecked);
                int glitched = slot.LogicEngine.LastGlitchedLocations.Count(id => !checkedSet.Contains(id));
                if (glitched > 0) Row("Sequence breaks", Colored(glitched.ToString(), Warn), "Reachable only with the game's glitch/sequence-break logic");
                int excluded = s.Locations.AllLocations.Count(slot.IsExcludedLocation);
                int yours = Annotations.ExclusionOverridesFor(slot.AnnotationKey).Count;
                if (excluded > 0 || yours > 0)
                    PlainRow("Excluded locations", yours == 0 ? excluded.ToString() : $"{excluded} ({yours} changed by you)");
                PlainRow("Logic steps", slot.LogicStepCount.ToString());
            }
            else
            {
                Row("Logic", Colored("Logic engine not running", Muted));
            }
            var received = s.Items.AllItemsReceived;
            PlainRow("Items received", received.Count.ToString());
            var pool = slot.LogicEngine?.LastItemPool;
            if (pool != null && pool.Count > 0)
            {
                int progTotal = pool.Count(p => (p.Flags & 1) != 0);
                int progGot = received.Count(i => i.Flags.HasFlag(ItemFlags.Advancement));
                Row("Progression items", Colored($"{Math.Min(progGot, progTotal)} / {progTotal}", Colors.Plum));
                PlainRow("Item pool size", pool.Count.ToString());
            }

            BuildAccuracy(slot);
            BuildCheese(slot.ProfileId, slot.SlotName, live: true);

            Section("Hints");
            var room = s.RoomState;
            if (room != null)
            {
                PlainRow("Hint points", room.HintPoints.ToString());
                PlainRow("Hint cost", $"{room.HintCost} ({room.HintCostPercentage}% of locations)");
                PlainRow("Can afford", room.HintCost > 0 ? (room.HintPoints / room.HintCost).ToString() : "Unlimited");
                PlainRow("Points per check", room.LocationCheckPoints.ToString());
            }
            int me = slot.PlayerSlot;
            var openForMe = slot.CurrentHints.Where(h => !h.Found && h.ReceivingPlayer == me).ToList();
            var openInMine = slot.CurrentHints.Where(h => !h.Found && h.FindingPlayer == me).ToList();
            Row("Open hints for you", Link(openForMe.Count.ToString(), () => Go(slot, 6)));
            Row("Open hints in your world", Link(openInMine.Count.ToString(), () => Go(slot, 6)));
            int inLogicForMe = openInMine.Count(h => slot.IsLocationReachable(h.LocationId));
            if (openInMine.Count > 0 && !slot.LogicHidden) PlainRow("…of those in logic", inLogicForMe.ToString(), inLogicForMe > 0 ? Good : Muted);

            BuildSpecialProgress(slot);
            BuildFlaggedList(slot);
            BuildSeedSettings(slot.SeedSettings(), slot.PackScripts != null, Newtonsoft.Json.Linq.JToken.FromObject(slot.SlotDataSnapshot ?? new Dictionary<string, object>()), live: true);

            Section("Room");
            if (room != null)
            {
                PlainRow("Release", room.ReleasePermissions.ToString());
                PlainRow("Collect", room.CollectPermissions.ToString());
                PlainRow("Remaining", room.RemainingPermissions.ToString());
                PlainRow("Password", YesNo(room.HasPassword));
                if (room.ServerTags != null && room.ServerTags.Count > 0) PlainRow("Server tags", string.Join(", ", room.ServerTags));
                PlainRow("Players", s.Players.AllPlayers.Count(p => p.Slot > 0 && !p.IsGroup).ToString());
            }

            Section("Tracking");
            PlainRow("Logic engine", slot.EngineRunning ? "Running" : "Not running", slot.EngineRunning ? Good : Muted);
            string raceWhy = slot.IsRaceRoom ? "the room is a race" : "set to Always On";
            Row("Race mode", !slot.RaceRestricted
                    ? Colored(slot.IsRaceRoom ? "Off (the room is a race, but restrictions are turned off)" : "Off", Colors.LightGray)
                    : Colored(slot.LogicHidden ? $"On, all logic hidden ({raceWhy})" : $"On, logic explanations off ({raceWhy})", Warn),
                "Settings → Race Mode");
            PlainRow("Map pack", slot.MapPackName ?? "None installed for this game");

            Section("Advanced");
            PlainRow("Profile id", slot.ProfileId);
            PlainRow("Notes key", slot.AnnotationKey);
            PlainRow("Chat messages kept", slot.ChatHistory.Count.ToString());
        }

        private void BuildOverviewOfConnections()
        {
            Section("Connections");
            if (_host.Profiles.Count == 0)
            {
                AddHint("No multiworld profiles yet. Add one on the Connections tab.");
                return;
            }
            foreach (var p in _host.Profiles)
            {
                int live = _host.ConnectedSlots.Count(s => IsInstanceValid(s) && s.ProfileId == p.Id);
                Row(p.Name, LinkTo($"{live} of {p.Slots.Count} slots connected", InspectTarget.ForProfile(p.Id)));
            }
            Section("Shortcuts");
            AddHint("Alt+Left / Alt+Right or the mouse back/forward buttons: history.  Ctrl+Shift+C: copy everything shown.  F: flag or unflag the selected location or item.  Right-click any value to copy it.");
        }

        private void BuildOfflineSlot(InspectTarget t)
        {
            var profile = _host.Profiles.FirstOrDefault(p => p.Id == t.ProfileId);
            SetHeader("Slot", t.SlotName ?? "Slot", LinkTo(profile?.Name ?? "Profile", InspectTarget.ForProfile(t.ProfileId)), Muted,
                _host.IsSlotConnecting(t.ProfileId, t.SlotName) ? "Connecting" : "Not connected");
            BeginActions();
            if (profile != null) AddAction("Connect", "Connect this slot", () => _host.ConnectSlot(t.ProfileId, t.SlotName));
            EndActions();
            BuildSavedStats(profile, t.SlotName);
            BuildCheese(t.ProfileId, t.SlotName, live: false);
            BuildOfflineSeedSettings(t.ProfileId, t.SlotName);
        }

        // =====================================================================
        // Seed settings (read by the map pack's own scripts from the slot's options)
        // =====================================================================

        // Offline slots: scripts run once per saved slot data, in the background.
        private static readonly Dictionary<string, (DateTime Saved, List<PackScriptHost.SettingInfo> Settings)> _offlineSettings = new();

        private void BuildOfflineSeedSettings(string profileId, string slotName)
        {
            var saved = DataManager.LoadSlotData(profileId, slotName);
            if (saved?.SlotData == null) return;
            string key = profileId + "|" + slotName;
            if (_offlineSettings.TryGetValue(key, out var cached) && cached.Saved == saved.Saved)
            {
                BuildSeedSettings(cached.Settings, true, saved.SlotData, live: false, savedAt: saved.Saved);
                return;
            }
            Section("Seed settings");
            AddHint("Reading this slot's saved options with the map pack…");
            var current = StillCurrent();
            Async.Then(System.Threading.Tasks.Task.Run(() =>
            {
                var pack = PopTrackerPackLoader.LoadPackForGame(saved.Game, null);
                var host = pack == null ? null : PackScriptHost.Load(pack);
                if (host == null) return new List<PackScriptHost.SettingInfo>();
                host.Initialize();
                host.Clear(0, 0, saved.SlotData);
                var codes = PackFixes.Effective(pack).ItemGridGroups.Where(g => g.LooksLikeSettings).SelectMany(g => g.Rows.SelectMany(r => r));
                return host.Settings(codes);
            }), list =>
            {
                list ??= new List<PackScriptHost.SettingInfo>();
                Callable.From(() =>
                {
                    _offlineSettings[key] = (saved.Saved, list);
                    current(() => Render(keepScroll: true));
                }).CallDeferred();
            }, $"reading {saved.Game}'s saved options with its map pack");
        }

        private void BuildSeedSettings(List<PackScriptHost.SettingInfo> settings, bool scriptsRan, Newtonsoft.Json.Linq.JToken slotData, bool live, DateTime? savedAt = null)
        {
            Section("Seed settings");
            if (!live && savedAt != null) AddHint($"From this slot's options as of {savedAt:yyyy-MM-dd HH:mm}.");
            if (settings.Count == 0)
            {
                AddHint(scriptsRan ? "The map pack doesn't show any of this seed's options." : "Shown once the map pack's script has read this slot's options.");
            }
            foreach (var s in settings.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                string state = s.StageName ?? (s.On ? "On" : "Off");
                string value = s.ValueText;
                string detail = s.OptionPath == null ? "" : s.OptionMissing ? Colored($"  ({s.OptionPath} isn't in the slot data)", Warn) : Colored($"  ({s.OptionPath} = {value})", Muted);
                Row(s.Name, Colored(state, s.On ? Good : Colors.LightGray) + detail, "Set by the map pack's script from this slot's options");
            }

            // Everything the server sent, for any game: the options live under "options" in most worlds.
            if (slotData is Newtonsoft.Json.Linq.JObject obj && obj.Count > 0)
            {
                Section("Slot data");
                AddHint("The options and data the server sent this slot at login.");
                int shown = 0;
                foreach (var (path, val) in Flatten(obj, ""))
                {
                    if (++shown > 250) { AddHint("…more (copy the panel to see everything)."); break; }
                    PlainRow(path, val, Colors.LightGray);
                }
            }
        }

        /// <summary>Leaf values of a JSON object as "a.b.c" → text (long arrays summarized).</summary>
        private static IEnumerable<(string Path, string Value)> Flatten(Newtonsoft.Json.Linq.JToken token, string prefix)
        {
            switch (token)
            {
                case Newtonsoft.Json.Linq.JObject o:
                    foreach (var p in o.Properties())
                        foreach (var x in Flatten(p.Value, prefix.Length == 0 ? p.Name : prefix + "." + p.Name)) yield return x;
                    break;
                case Newtonsoft.Json.Linq.JArray a when a.Count > 12 || a.Any(e => e is Newtonsoft.Json.Linq.JContainer):
                    yield return (prefix, $"[{a.Count} entries]");
                    break;
                default:
                    yield return (prefix, token.Type == Newtonsoft.Json.Linq.JTokenType.String ? token.ToString() : token.ToString(Newtonsoft.Json.Formatting.None));
                    break;
            }
        }

        private void BuildSavedStats(MultiworldProfile profile, string slotName)
        {
            Section("Last known");
            if (profile?.SavedStats == null || slotName == null || !profile.SavedStats.TryGetValue(slotName, out var st))
            {
                AddHint("Never connected from this app.");
                return;
            }
            PlainRow("Game", st.GameName);
            int pct = st.TotalCount > 0 ? (int)Math.Round(100.0 * st.CompleteCount / st.TotalCount) : 0;
            PlainRow("Locations", $"{st.CompleteCount} / {st.TotalCount} checked ({pct}%)");
            PlainRow("In logic", st.LogicCount.ToString());
            PlainRow("Last update", st.LastUpdated.ToString("yyyy-MM-dd HH:mm"));
            var specials = Annotations.SpecialItemNames(st.GameName).ToList();
            if (specials.Count > 0) PlainRow("Special items marked", specials.Count.ToString(), Annotations.SpecialColor);
        }

        private void BuildSpecialProgress(SlotTrackerControl slot)
        {
            var itemNames = Annotations.SpecialItemNames(slot.Game).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var locNames = Annotations.SpecialLocationNames(slot.Game).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            Section("◆ Special");
            if (itemNames.Count == 0 && locNames.Count == 0)
            {
                AddHint($"Nothing marked special for {slot.Game} yet. Open any item or location and tick \"Special\", or import a shared list.");
                return;
            }
            var s = slot.Session;
            if (itemNames.Count > 0)
            {
                var (got, total) = slot.SpecialItemProgress();
                Row("Items", Colored($"{got} / {total} received", got >= total ? Good : Annotations.SpecialColor));
                var receivedNames = new HashSet<string>(s.Items.AllItemsReceived.Select(i => i.ItemName ?? ""), StringComparer.OrdinalIgnoreCase);
                foreach (var name in itemNames)
                {
                    bool have = receivedNames.Contains(name);
                    string note = Annotations.GetSpecial(slot.Game, Annotations.SpecialItemKey(name))?.Note;
                    AddText((have ? Colored("✔ ", Good) : Colored("○ ", Muted)) + LinkTo(name, slot.ItemTargetByName(name), have ? Colors.LightGray : Annotations.SpecialColor) +
                            (string.IsNullOrWhiteSpace(note) ? "" : "  " + Colored(note, Muted)));
                }
            }
            if (locNames.Count > 0)
            {
                var (chk, total) = slot.SpecialLocationProgress();
                Row("Locations", Colored($"{chk} / {total} checked", chk >= total && total > 0 ? Good : Annotations.SpecialColor));
                var checkedSet = new HashSet<long>(s.Locations.AllLocationsChecked);
                foreach (var name in locNames)
                {
                    long id = s.Locations.GetLocationIdFromName(slot.Game, name);
                    if (id <= 0) { AddText(Colored("? " + name + " (not in this world)", Muted)); continue; }
                    bool done = checkedSet.Contains(id);
                    bool inLogic = slot.IsLocationReachable(id);
                    string mark = done ? Colored("✔ ", Good) : inLogic ? Colored("● ", Good) : Colored("○ ", Muted);
                    AddText(mark + LinkTo(name, slot.LocationTarget(id), done ? Colors.LightGray : Annotations.SpecialColor));
                }
            }
        }

        private void BuildFlaggedList(SlotTrackerControl slot)
        {
            var marks = Annotations.ForSlot(slot.AnnotationKey);
            if (marks.Count == 0) return;
            Section("Flagged & noted");
            var s = slot.Session;
            foreach (var group in marks.OrderBy(kv => kv.Value.Flag == 0 ? 99 : kv.Value.Flag).ThenBy(kv => kv.Key).Take(150))
            {
                var a = group.Value;
                string key = group.Key;
                if (!long.TryParse(key.Substring(2), out long id)) continue;
                string link;
                if (key.StartsWith("L:"))
                    link = LinkTo(s.Locations.GetLocationNameFromId(id, slot.Game) ?? key, slot.LocationTarget(id), Colors.LightGreen);
                else
                {
                    string name = s.Items.GetItemName(id, slot.Game) ?? key;
                    link = LinkTo(name, slot.ItemTarget(id, name), Colors.Plum);
                }
                string dot = a.Flag > 0 ? $"[color={Hex(Annotations.FlagColor(a.Flag))}]●[/color] " : "    ";
                string note = string.IsNullOrWhiteSpace(a.Note) ? "" : "  " + Colored(a.Note.Length > 80 ? a.Note.Substring(0, 80) + "…" : a.Note, Muted);
                AddText(dot + link + note);
            }
        }

        // =====================================================================
        // Location
        // =====================================================================

        private void BuildLocation(InspectTarget t)
        {
            var view = ViewSlot(t);
            if (view == null) { NotConnected("Location", $"Location {t.LocationId}"); return; }
            var s = view.Session;
            int ownerPlayer = t.Player;
            string game = GameOf(s, ownerPlayer);
            string name = s.Locations.GetLocationNameFromId(t.LocationId, game) ?? $"Location {t.LocationId}";
            var owner = SlotForPlayer(view, ownerPlayer);
            string ownerName = PlayerName(s, ownerPlayer);
            var hint = AllKnownHints(view).FirstOrDefault(h => h.FindingPlayer == ownerPlayer && h.LocationId == t.LocationId);

            bool? isChecked = owner != null ? owner.Session.Locations.AllLocationsChecked.Contains(t.LocationId) : hint?.Found == true ? true : null;
            bool? inLogic = owner?.IsLocationInLogic(t.LocationId);
            bool glitched = owner?.IsGlitchedLocation(t.LocationId) == true;
            bool excluded = owner?.IsExcludedLocation(t.LocationId) == true;
            bool special = Annotations.IsSpecialLocation(game, name);

            (string badge, Color color) = isChecked == true ? ("Checked", Muted)
                : owner?.LogicHidden == true ? ("Open", Colors.SteelBlue)
                : inLogic == true ? ("In logic", Good)
                : glitched ? ("Sequence break", Warn)
                : inLogic == false ? ("Out of logic", Bad)
                : ("Logic unknown", Colors.Gray);
            SetHeader("Location", name,
                $"{Colored(game, Colors.LightGray)}  ·  {PlayerLink(view, ownerPlayer)}'s world" + (special ? "  " + Colored("◆ Special", Annotations.SpecialColor) : ""),
                color, badge);

            // --- Quick actions ---
            BeginActions();
            if (owner != null && owner.IsOnMap(t.LocationId))
                AddAction("Show on map", "Open the Map Tracker centered on this location", () => { Go(owner, 2); owner.RevealOnMap(t.LocationId); });
            if (owner != null && owner.HasLogicRow(t.LocationId) && !owner.LogicHidden)
                AddAction("Logic Tracker", "Find this check in the Logic Tracker", () => { Go(owner, 4); owner.RevealLogicRow(t.LocationId); });
            if (hint != null)
            {
                var hintHolder = _host.ConnectedSlots.FirstOrDefault(x => IsInstanceValid(x) && x.CurrentHints.Any(h => h.FindingPlayer == hint.FindingPlayer && h.LocationId == hint.LocationId)) ?? view;
                AddAction("Hints", "Find this hint in the Hints tab", () => { Go(hintHolder, 6); hintHolder.RevealHint(hint.FindingPlayer, hint.LocationId); });
            }
            if (owner != null && isChecked == false && hint == null)
                AddAction("Request hint", "Ask the server what's here (!hint_location; costs hint points)", () => RequestHint(owner, "!hint_location " + name, name));
            AddAction("Copy", "Copy the location name", () => CopyText(name));
            AddFlagMenu(Annotations.SlotKey(t.ProfileId, RealName(s, ownerPlayer)), Annotations.LocationKey(t.LocationId));
            AddSpecialToggle(game, Annotations.SpecialLocationKey(name));
            if (owner != null)
            {
                // Overrides only diverge from the seed: toggling back returns to its default.
                string ownerKey = owner.AnnotationKey;
                long locId = t.LocationId;
                bool bySeed = owner.IsExcludedBySeed(locId);
                if (excluded)
                    AddAction("Include", bySeed ? "Count this seed-excluded check again in logic counts, the Logic Tracker and the map" : "Stop excluding this check",
                        () => Annotations.SetExclusionOverride(ownerKey, locId, bySeed ? false : null));
                else
                    AddAction("Exclude", bySeed ? "Back to the seed's default (excluded)" : "Leave this check out of logic counts, the Logic Tracker and the map (only in Atlas; the server isn't told)",
                        () => Annotations.SetExclusionOverride(ownerKey, locId, bySeed ? null : true));
            }
            if (owner != null && owner != _host.SelectedSlot) AddAction($"Switch to {owner.SlotName}", "Select this location's slot", () => _host.SelectSlot(owner));
            EndActions();

            // --- Overview ---
            Section("Overview");
            PlainRow("Name", name);
            Row("World", PlayerLink(view, ownerPlayer));
            PlainRow("Game", game);
            Row("Checked", isChecked == null ? Colored("Unknown (that world isn't connected here)", Muted) : isChecked.Value ? Colored("Yes", Good) : Colored("No", Colors.White));
            switch (owner?.ExclusionSource(t.LocationId))
            {
                case "seed": Row("Excluded", Colored("Yes, by the seed's options (never holds progression)", Muted)); break;
                case "you": Row("Excluded", Colored("Yes, by you (left out of logic counts, the Logic Tracker and the map; the server isn't told)", Muted)); break;
                case "included by you": Row("Excluded", Colored("No: the seed excludes it, but you included it", Colors.LightGray)); break;
            }
            if (hint != null) Row("Hinted", LinkTo(StatusText(hint.Status, hint.Found), HintT(view, hint), StatusColor(hint.Status, hint.Found)));
            var specialEntry = Annotations.GetSpecial(game, Annotations.SpecialLocationKey(name));
            if (specialEntry != null) Row("◆ Special", Colored(string.IsNullOrWhiteSpace(specialEntry.Note) ? "Marked special for " + game : specialEntry.Note, Annotations.SpecialColor));

            // --- Contents ---
            Section("Contents");
            if (hint != null)
            {
                Row("Item", ItemLink(view, hint.ReceivingPlayer, hint.ItemId, hint.ItemFlags));
                Row("For", PlayerLink(view, hint.ReceivingPlayer));
                Row("Classification", Colored(ClassOf(hint.ItemFlags).Text, ClassOf(hint.ItemFlags).Color));
                if (!string.IsNullOrEmpty(hint.Entrance)) PlainRow("Entrance", hint.Entrance);
            }
            else if (isChecked == true && owner != null)
            {
                var placeholder = AddText(Colored("Looking up what was here…", Muted));
                var current = StillCurrent();
                Async.Then(owner.ScoutCheckedLocationAsync(t.LocationId), info =>
                {
                    Callable.From(() => current(() =>
                    {
                        if (!IsInstanceValid(placeholder)) return;
                        placeholder.Text = info == null
                            ? Colored("Couldn't look this up.", Muted)
                            : $"{ItemLink(view, info.Player?.Slot ?? -1, info.ItemId, info.Flags)} for {PlayerLink(view, info.Player?.Slot ?? -1)}";
                    })).CallDeferred();
                }, "looking up what was at a checked location");
            }
            else
            {
                AddHint("Unknown until it's checked or hinted.");
            }

            // --- Logic ---
            Section("Logic");
            if (owner == null)
            {
                AddHint($"Connect {ownerName} in Atlas (same multiworld profile) to see this world's logic.");
            }
            else if (owner.LogicHidden)
            {
                AddHint("Hidden by race mode (Settings → Race Mode).");
            }
            else if (!owner.EngineRunning)
            {
                AddHint("The logic engine isn't running for this slot.");
            }
            else
            {
                Row("In logic", inLogic == true ? Colored("Yes", Good) : Colored("No", Bad));
                if (glitched) Row("Sequence break", Colored("Reachable with the game's glitch logic", Warn));
                var step = owner.UnlockStepOf(t.LocationId);
                if (step != null)
                {
                    var (stepNo, order, itemName) = step.Value;
                    Row("Reached at", stepNo == 0
                        ? Colored($"Base logic (order #{order})", Colors.LightGray)
                        : $"{Colored($"Step {stepNo} (order #{order}) by ", Colors.LightGray)}{LinkTo(itemName, owner.ItemTargetByName(itemName), Colors.Plum)}");
                }
                if (isChecked == true)
                {
                    AddHint("Already checked, so the engine isn't asked why.");
                }
                else if (owner.RaceRestricted)
                {
                    AddHint("Race mode: the logic engine isn't asked why a location is or isn't in logic.");
                }
                else
                {
                    BuildExplanation(owner, t.LocationId);
                }
            }

            // --- Map ---
            BuildLocationMapSection(owner, t.LocationId);

            // --- Groups ---
            BuildGroups(view, game, name, isItem: false);

            // --- Notes ---
            BuildAnnotations(Annotations.SlotKey(t.ProfileId, RealName(s, ownerPlayer)), Annotations.LocationKey(t.LocationId), game, Annotations.SpecialLocationKey(name), ownerName);

            Section("Advanced");
            PlainRow("Location id", t.LocationId.ToString());
            PlainRow("World slot", ownerPlayer.ToString());
            PlainRow("Viewed from", view.SlotName);
            if (owner != null) PlainRow("Tracked by", owner.SlotName);
        }

        private void BuildExplanation(SlotTrackerControl owner, long locationId)
        {
            var status = AddText(Colored("Asking the logic engine why…", Muted));
            var container = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            container.AddThemeConstantOverride("separation", 4);
            Target.AddChild(container);
            var current = StillCurrent();
            int fontSize = _host.Settings.PropertiesFontSize;

            Async.Then(owner.ExplainLocationAsync(locationId), ex =>
            {
                Callable.From(() => current(() =>
                {
                    if (!IsInstanceValid(container)) return;
                    // Build into the container as if it were the current section.
                    var saved = _currentSectionBody;
                    _currentSectionBody = container;
                    try
                    {
                        if (ex == null) { status.Text = Colored("The logic engine didn't answer (it may be busy). Reselect to retry.", Muted); return; }
                        if (!string.IsNullOrEmpty(ex.Error)) { status.Text = Colored(ex.Error, Muted); return; }
                        status.Text = "";
                        status.Visible = false;
                        if (!string.IsNullOrEmpty(ex.Region)) Row("Region", Colored(ex.Region, Colors.LightGray) + (ex.RegionReachable ? Colored("  (reachable)", Good) : Colored("  (not reachable yet)", Bad)));
                        if (!string.IsNullOrEmpty(ex.ProgressType) && ex.ProgressType != "DEFAULT") PlainRow("Progress type", ex.ProgressType);
                        if (!ex.InLogic)
                        {
                            if (ex.UnreachableWithAll)
                                AddText(Colored("Not reachable even with every remaining item of this world. It likely needs another world's progress, an event, or a setting.", Warn));
                            else if (ex.SingleUnlocks != null && ex.SingleUnlocks.Count > 0)
                                Row("Opens with any one of", string.Join(", ", ex.SingleUnlocks.Select(n => LinkTo(n, owner.ItemTargetByName(n), Colors.Plum))));
                            else if (ex.Required != null && ex.Required.Count > 0)
                                Row("Needs all of", string.Join(", ", ex.Required.Select(n => LinkTo(n, owner.ItemTargetByName(n), Colors.Plum))));
                            else if (ex.Required != null)
                                AddHint("Needs a combination of items; no single item is required on every route.");
                            if (ex.Partial) AddHint($"Partial answer: stopped after the time limit ({ex.Candidates} candidate items).");
                        }
                        if (!string.IsNullOrWhiteSpace(ex.Rule))
                        {
                            AddText(Colored("Access rule", Colors.Gray));
                            AddRule(ex.Rule, "No requirement beyond reaching the region.");
                        }
                        if (ex.Entrances != null && ex.Entrances.Count > 0)
                        {
                            AddText(Colored($"Ways into {ex.Region}", Colors.Gray));
                            foreach (var e in ex.Entrances)
                            {
                                AddText((e.Reachable ? Colored("✔ ", Good) : Colored("✖ ", Bad)) + Colored(e.Name, Colors.LightGray) +
                                        (string.IsNullOrEmpty(e.From) ? "" : Colored("  from " + e.From, Muted)));
                                if (!e.Reachable) AddRule(e.Rule, $"No requirement beyond reaching {(string.IsNullOrEmpty(e.From) ? "the previous region" : e.From)}.");
                            }
                        }
                    }
                    finally
                    {
                        _currentSectionBody = saved;
                        MainTrackerWindow.SetFontSizeRecursive(container, fontSize);
                    }
                })).CallDeferred();
            }, "asking the logic engine about a location");
        }

        /// <summary>
        /// Shows an access rule from the engine: "ALWAYS"/"NEVER" as plain sentences, rule-builder text
        /// ("Has X and Missing Y") with Has/Missing colored, and function source as a code block.
        /// </summary>
        private void AddRule(string rule, string alwaysText)
        {
            if (string.IsNullOrWhiteSpace(rule)) return;
            if (rule == "ALWAYS") { AddText(Colored(alwaysText, Colors.LightGray)); return; }
            if (rule == "NEVER") { AddText(Colored("Never: this rule can't be satisfied.", Bad)); return; }
            bool isSource = rule.Contains("lambda") || rule.Contains("def ") || rule.Contains("state.") || rule.Contains("return ");
            if (isSource) { AddCode(rule); return; }
            string bb = Esc(rule)
                .Replace("Missing ", $"[color={Hex(Bad)}]Missing[/color] ")
                .Replace("Has ", $"[color={Hex(Good)}]Has[/color] ");
            AddText(bb);
        }

        private void BuildLocationMapSection(SlotTrackerControl owner, long locationId)
        {
            var map = owner?.MapTracker;
            if (map?.Pack == null) return;
            var pins = map.PinsFor(locationId);
            Section("Map");
            PlainRow("Pack", map.Pack.Manifest?.Name);
            if (pins.Count == 0)
            {
                AddHint("This map pack doesn't place this location on any map.");
                return;
            }
            foreach (var (pin, section, maps) in pins)
            {
                string firstMap = maps.FirstOrDefault() ?? "";
                Row("Pin", LinkTo(pin.Name, InspectTarget.ForPackLocation(owner.ProfileId, owner.SlotName, firstMap, pin.Name)));
                if (section != null && !string.IsNullOrEmpty(section.Name) && section.Name != pin.Name) PlainRow("Section", section.Name);
                if (maps.Count > 0)
                    Row("Maps", string.Join(", ", maps.Select(m => LinkTo(map.Pack.Maps.TryGetValue(m, out var pm) ? pm.Name : m, InspectTarget.ForMap(owner.ProfileId, owner.SlotName, m)))));
                var rules = section?.AccessRulesRaw ?? pin.AccessRulesRaw;
                if (rules != null && rules.HasValues) { AddText(Colored("Pack access rules", Colors.Gray)); AddCode(rules.ToString(Newtonsoft.Json.Formatting.None)); }
                var vis = section?.VisibilityRulesRaw;
                if (vis != null && vis.HasValues) { AddText(Colored("Visibility rules", Colors.Gray)); AddCode(vis.ToString(Newtonsoft.Json.Formatting.None)); }
            }
        }

        private void BuildGroups(SlotTrackerControl view, string game, string name, bool isItem)
        {
            if (view == null || string.IsNullOrEmpty(name)) return;
            Section(isItem ? "Item groups" : "Location groups");
            var placeholder = AddText(Colored("Loading…", Muted));
            var current = StillCurrent();
            var task = isItem ? view.ItemGroupsAsync(game) : view.LocationGroupsAsync(game);
            Async.Then(task, groups =>
            {
                Callable.From(() => current(() =>
                {
                    if (!IsInstanceValid(placeholder)) return;
                    var mine = groups?.Where(g => g.Value != null && g.Value.Contains(name) && g.Key != "Everywhere" && g.Key != "Everything").Select(g => g.Key).OrderBy(k => k).ToList();
                    placeholder.Text = groups == null ? Colored("Not available.", Muted)
                        : mine.Count == 0 ? Colored("Not in any named group.", Muted)
                        : Colored(string.Join(", ", mine), Colors.LightGray);
                })).CallDeferred();
            }, "reading the game's item and location groups");
        }

        // =====================================================================
        // Item
        // =====================================================================

        private void BuildItem(InspectTarget t)
        {
            var view = ViewSlot(t);
            if (view == null) { NotConnected("Item", t.ItemName ?? $"Item {t.ItemId}"); return; }
            var s = view.Session;
            int ownerPlayer = t.Player;
            string game = GameOf(s, ownerPlayer);
            string name = !string.IsNullOrEmpty(t.ItemName) ? t.ItemName : s.Items.GetItemName(t.ItemId, game) ?? $"Item {t.ItemId}";
            var owner = SlotForPlayer(view, ownerPlayer);
            string ownerName = PlayerName(s, ownerPlayer);

            // Copies received by the owner (when it's connected here).
            var copies = new List<(int Index, ItemInfo Info)>();
            if (owner != null)
            {
                var all = owner.Session.Items.AllItemsReceived;
                for (int i = 0; i < all.Count; i++)
                {
                    if ((t.ItemId != 0 && all[i].ItemId == t.ItemId) || (t.ItemId == 0 && string.Equals(all[i].ItemName, name, StringComparison.OrdinalIgnoreCase)))
                        copies.Add((i, all[i]));
                }
            }
            var pool = owner?.LogicEngine?.LastItemPool?.Where(p => (t.ItemId != 0 && p.Id == t.ItemId) || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            int poolCount = pool?.Count ?? 0;
            var hintsForItem = AllKnownHints(view).Where(h => h.ReceivingPlayer == ownerPlayer &&
                (h.ItemId == t.ItemId || string.Equals(s.Items.GetItemName(h.ItemId, game), name, StringComparison.OrdinalIgnoreCase))).ToList();

            (string Text, Color Color) cls = copies.Count > 0 ? ClassOf(copies[0].Info.Flags)
                : poolCount > 0 ? ClassOfPoolFlags(pool[0].Flags)
                : hintsForItem.Count > 0 ? ClassOf(hintsForItem[0].ItemFlags)
                : ("Unknown class", Colors.Gray);
            bool special = Annotations.IsSpecialItem(game, name);

            SetHeader("Item", name,
                $"{Colored(game, Colors.LightGray)}  ·  for {PlayerLink(view, ownerPlayer)}" + (special ? "  " + Colored("◆ Special", Annotations.SpecialColor) : ""),
                cls.Color, cls.Text);

            BeginActions();
            if (owner != null && copies.Count > 0)
                AddAction("Item History", "Find this item in Item History", () => { Go(owner, 5); owner.RevealHistory(t); });
            if (owner != null)
                AddAction("Key Items", "Show this item in Key Items", () => { Go(owner, 3); owner.RevealKeyItem(name); });
            if (owner != null && (poolCount == 0 || copies.Count < poolCount) && hintsForItem.All(h => h.Found))
                AddAction("Request hint", "Ask the server where this item is (!hint; costs hint points)", () => RequestHint(owner, "!hint " + name, name));
            AddAction("Copy", "Copy the item name", () => CopyText(name));
            if (t.ItemId != 0) AddFlagMenu(Annotations.SlotKey(t.ProfileId, RealName(s, ownerPlayer)), Annotations.ItemKey(t.ItemId));
            AddSpecialToggle(game, Annotations.SpecialItemKey(name));
            if (owner != null && owner != _host.SelectedSlot) AddAction($"Switch to {owner.SlotName}", "Select this item's slot", () => _host.SelectSlot(owner));
            EndActions();

            Section("Overview");
            PlainRow("Name", name);
            PlainRow("Game", game);
            Row("Belongs to", PlayerLink(view, ownerPlayer));
            Row("Classification", Colored(cls.Text, cls.Color));
            if (owner != null)
            {
                string got = poolCount > 0 ? $"{copies.Count} of {poolCount}" : copies.Count.ToString();
                Row("Received", Colored(got, copies.Count > 0 ? Good : Colors.White));
            }
            else
            {
                Row("Received", Colored($"Unknown ({ownerName} isn't connected here)", Muted));
            }
            var specialEntry = Annotations.GetSpecial(game, Annotations.SpecialItemKey(name));
            if (specialEntry != null) Row("◆ Special", Colored(string.IsNullOrWhiteSpace(specialEntry.Note) ? "Marked special for " + game : specialEntry.Note, Annotations.SpecialColor));

            // This specific copy (from an Item History row).
            if (owner != null && t.ReceiptIndex >= 0 && t.ReceiptIndex < owner.Session.Items.AllItemsReceived.Count)
            {
                var info = owner.Session.Items.AllItemsReceived[t.ReceiptIndex];
                Section("This copy");
                PlainRow("Received", $"#{t.ReceiptIndex + 1} of {owner.Session.Items.AllItemsReceived.Count}");
                int sender = info.Player?.Slot ?? 0;
                Row("From", PlayerLink(view, sender));
                if (info.LocationId > 0) Row("Found at", LocationLink(view, sender, info.LocationId));
                else if (info.LocationId == -1) PlainRow("Found at", "Starting inventory / server");
                else if (info.LocationId == -2) PlainRow("Found at", "Cheat console");
                PlainRow("Sender's game", info.LocationGame ?? GameOf(s, sender));
                PlainRow("Flags", info.Flags.ToString());
            }

            if (copies.Count > 0)
            {
                Section($"All copies ({copies.Count})");
                foreach (var (index, info) in copies.Take(40))
                {
                    int sender = info.Player?.Slot ?? 0;
                    string where = info.LocationId > 0 ? LocationLink(view, sender, info.LocationId) : Colored("start / server", Muted);
                    AddText(LinkTo($"#{index + 1}", ItemT(view, ownerPlayer, info.ItemId, name, index), Colors.LightGray) + "  from " + PlayerLink(view, sender) + " at " + where);
                }
                if (copies.Count > 40) AddHint($"…and {copies.Count - 40} more.");
            }

            if (hintsForItem.Count > 0)
            {
                Section("Hinted locations");
                foreach (var h in hintsForItem.OrderBy(h => h.Found))
                {
                    var finder = SlotForPlayer(view, h.FindingPlayer);
                    bool? logic = finder?.IsLocationInLogic(h.LocationId);
                    string logicText = h.Found ? "" : logic == true ? Colored("  in logic", Good) : logic == false ? Colored("  not in logic", Bad) : "";
                    AddText(LinkTo(StatusText(h.Status, h.Found), HintT(view, h), StatusColor(h.Status, h.Found)) + "  " +
                            LocationLink(view, h.FindingPlayer, h.LocationId) + " in " + PlayerLink(view, h.FindingPlayer) + "'s world" + logicText);
                }
            }

            if (owner != null && owner.EngineRunning && !owner.LogicHidden)
            {
                Section("Logic impact");
                var steps = owner.StepsUnlockedBy(name);
                if (steps.Count == 0) AddHint(copies.Count > 0 ? "Receiving it hasn't opened any new checks." : "Not received yet.");
                foreach (var (step, count) in steps)
                    AddText(Link($"Step {step}", () => { Go(owner, 4); }, Colors.LightGray) + Colored($": opened {count} check{(count == 1 ? "" : "s")}", Colors.LightGray));
            }

            // Map pack item definition (Key Items).
            var packItem = owner?.Pack?.ItemsByCode.Values.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (packItem != null)
            {
                Section("Map pack item");
                PlainRow("Type", packItem.Type);
                var codes = packItem.GetCodes();
                if (codes.Count > 0) PlainRow("Codes", string.Join(", ", codes));
                if (packItem.Stages != null && packItem.Stages.Count > 0)
                    PlainRow("Stages", string.Join(" → ", packItem.Stages.Select(st => string.IsNullOrEmpty(st.Name) ? "?" : st.Name)));
                if (packItem.MaxQuantity > 1) PlainRow("Max quantity", packItem.MaxQuantity.ToString());
            }

            BuildGroups(view, game, name, isItem: true);

            if (t.ItemId != 0)
                BuildAnnotations(Annotations.SlotKey(t.ProfileId, RealName(s, ownerPlayer)), Annotations.ItemKey(t.ItemId), game, Annotations.SpecialItemKey(name), ownerName);
            else
                BuildAnnotations(null, null, game, Annotations.SpecialItemKey(name), ownerName);

            Section("Advanced");
            PlainRow("Item id", t.ItemId != 0 ? t.ItemId.ToString() : "Unknown");
            PlainRow("Owner slot", ownerPlayer.ToString());
            if (copies.Count > 0) PlainRow("Flags", copies[0].Info.Flags.ToString());
            if (poolCount > 0) PlainRow("Pool flags", pool[0].Flags.ToString());
            PlainRow("Viewed from", view.SlotName);
        }

        // =====================================================================
        // Player
        // =====================================================================

        private void BuildPlayer(InspectTarget t)
        {
            var view = ViewSlot(t);
            if (view == null) { NotConnected("Player", $"Slot {t.Player}"); return; }
            var s = view.Session;
            int p = t.Player;
            var info = s.Players.GetPlayerInfo(p);
            string name = PlayerName(s, p);
            string game = GameOf(s, p);
            var connected = SlotForPlayer(view, p);
            bool isYou = p == view.PlayerSlot;
            var profile = _host.Profiles.FirstOrDefault(pr => pr.Id == view.ProfileId);
            string realName = RealName(s, p);
            bool inProfile = profile != null && profile.Slots.Contains(realName);

            SetHeader("Player", name, $"{Colored(game, Colors.LightGray)}  ·  slot {p}",
                isYou ? Colors.Magenta : connected != null ? Good : Colors.Yellow,
                isYou ? "You" : connected != null ? "Connected here" : info?.IsGroup == true ? "Group" : "Player");

            BeginActions();
            if (connected != null && connected != _host.SelectedSlot) AddAction("Switch to slot", "Select this player's slot", () => _host.SelectSlot(connected));
            if (connected != null) AddAction("Slot summary", "Show this slot's summary", () => Inspect(InspectTarget.ForSlot(connected.ProfileId, connected.SlotName)));
            if (connected == null && inProfile) AddAction("Connect", $"Connect {realName} (it's in this profile)", () => _host.ConnectSlot(profile.Id, realName));
            AddAction("Hints with player", "Show hints mentioning this player", () => { Go(view, 6); view.FilterHints(name); });
            AddAction("Copy", "Copy the player name", () => CopyText(name));
            EndActions();

            Section("Overview");
            PlainRow("Name", realName);
            if (!string.IsNullOrEmpty(info?.Alias) && info.Alias != realName) PlainRow("Alias", info.Alias);
            PlainRow("Slot", p.ToString());
            PlainRow("Team", ((info?.Team ?? view.Team) + 1).ToString());
            PlainRow("Game", game);
            if (info?.IsGroup == true) PlainRow("Type", "Item link group");
            if (info?.Groups != null && info.Groups.Length > 0) PlainRow("Groups", string.Join(", ", info.Groups.Select(g => g.Name)));
            var statusText = AddPendingRow("Status");
            var current = StillCurrent();
            Async.Then(view.ClientStatusAsync(p), st =>
            {
                Callable.From(() => current(() =>
                {
                    if (!IsInstanceValid(statusText)) return;
                    statusText.Text = st switch
                    {
                        ArchipelagoClientState.ClientGoal => Colored("Goal complete", Good),
                        ArchipelagoClientState.ClientPlaying => Colored("Playing", Colors.LightGreen),
                        ArchipelagoClientState.ClientReady => Colored("Ready", Colors.LightGray),
                        ArchipelagoClientState.ClientConnected => Colored("Connected", Colors.LightGray),
                        ArchipelagoClientState.ClientUnknown => Colored("Not connected / unknown", Muted),
                        _ => Colored("Unavailable", Muted)
                    };
                })).CallDeferred();
            }, "asking the server for a player's status");

            if (!isYou)
            {
                Section($"With {view.SlotName}");
                var fromThem = s.Items.AllItemsReceived.Where(i => i.Player?.Slot == p).ToList();
                Row("Items you received from them", Colored(fromThem.Count.ToString(), Colors.White) +
                    (fromThem.Count > 0 ? Colored($"  ({fromThem.Count(i => i.Flags.HasFlag(ItemFlags.Advancement))} progression)", Colors.Plum) : ""));
                int sentThisSession = view.ChatHistory.Count(c => c.APMessage is ItemSendLogMessage m && m is not HintItemSendLogMessage &&
                    m.Sender?.Slot == view.PlayerSlot && m.Receiver?.Slot == p);
                PlainRow("Items you sent them (this session)", sentThisSession.ToString());
                var hints = AllKnownHints(view).Where(h => !h.Found).ToList();
                var theirsInYours = hints.Where(h => h.ReceivingPlayer == p && h.FindingPlayer == view.PlayerSlot).ToList();
                var yoursInTheirs = hints.Where(h => h.ReceivingPlayer == view.PlayerSlot && h.FindingPlayer == p).ToList();
                if (theirsInYours.Count > 0)
                {
                    AddText(Colored($"Their items in your world ({theirsInYours.Count})", Colors.Gray));
                    foreach (var h in theirsInYours.Take(25))
                        AddText("  " + ItemLink(view, h.ReceivingPlayer, h.ItemId, h.ItemFlags) + " at " + LocationLink(view, h.FindingPlayer, h.LocationId) +
                                (view.IsLocationReachable(h.LocationId) ? Colored("  in logic", Good) : ""));
                }
                if (yoursInTheirs.Count > 0)
                {
                    AddText(Colored($"Your items in their world ({yoursInTheirs.Count})", Colors.Gray));
                    foreach (var h in yoursInTheirs.Take(25))
                        AddText("  " + ItemLink(view, h.ReceivingPlayer, h.ItemId, h.ItemFlags) + " at " + LocationLink(view, h.FindingPlayer, h.LocationId));
                }
            }

            if (connected != null)
            {
                Section("Their progress");
                int total = connected.TotalLocationsCount, done = connected.CheckedLocationsCount;
                PlainRow("Locations", $"{done} / {total} checked");
                if (connected.EngineRunning && !connected.LogicHidden) PlainRow("In logic now", connected.ActiveLogicCount.ToString(), Good);
                PlainRow("Items received", connected.Session.Items.AllItemsReceived.Count.ToString());
            }
        }

        /// <summary>A row whose value is filled in later; returns the value label.</summary>
        private RichTextLabel AddPendingRow(string label)
        {
            Row(label, Colored("…", Muted));
            var row = Target.GetChild(Target.GetChildCount() - 1);
            return row.GetChild(1) as RichTextLabel;
        }

        // =====================================================================
        // Hint
        // =====================================================================

        private void BuildHint(InspectTarget t)
        {
            var view = ViewSlot(t);
            if (view == null) { NotConnected("Hint", "Hint"); return; }
            var s = view.Session;
            var h = AllKnownHints(view).FirstOrDefault(x => x.FindingPlayer == t.Player && x.LocationId == t.LocationId);
            if (h == null)
            {
                SetHeader("Hint", "Hint", Colored("This hint isn't known to any connected slot.", Muted), Muted, "Unknown");
                return;
            }
            string receiverGame = GameOf(s, h.ReceivingPlayer);
            string finderGame = GameOf(s, h.FindingPlayer);
            string itemName = s.Items.GetItemName(h.ItemId, receiverGame) ?? $"Item {h.ItemId}";
            string locName = s.Locations.GetLocationNameFromId(h.LocationId, finderGame) ?? $"Location {h.LocationId}";
            var receiverSlot = SlotForPlayer(view, h.ReceivingPlayer);
            var finderSlot = SlotForPlayer(view, h.FindingPlayer);
            string text = $"{PlayerName(s, h.ReceivingPlayer)}'s {itemName} is at {locName} in {PlayerName(s, h.FindingPlayer)}'s world" +
                          (string.IsNullOrEmpty(h.Entrance) ? "" : $" ({h.Entrance})") + (h.Found ? " (found)" : "");

            SetHeader("Hint", $"{itemName}", $"at {LocationLink(view, h.FindingPlayer, h.LocationId)}", StatusColor(h.Status, h.Found), StatusText(h.Status, h.Found));

            BeginActions();
            bool canEdit = !h.Found && receiverSlot != null && receiverSlot.Session.RoomState?.Version != null && receiverSlot.Session.RoomState.Version >= new Version(0, 6, 0);
            if (canEdit)
            {
                foreach (var (status, label) in new[] { (HintStatus.Priority, "Priority"), (HintStatus.NoPriority, "No Priority"), (HintStatus.Avoid, "Avoid"), (HintStatus.Unspecified, "Unspecified") })
                {
                    var b = AddAction(label, $"Mark this hint {label} (shared with everyone in the room)", () =>
                    {
                        try
                        {
                            receiverSlot.Session.Hints.UpdateHintStatus(h.FindingPlayer, h.LocationId, status);
                            _host.Toast($"Marked {itemName} as {label}", Colors.Gray);
                        }
                        catch (Exception ex) { _host.Toast("Couldn't change the hint: " + ex.Message, Bad); }
                    }, h.Status != status);
                    b.AddThemeColorOverride("font_color", StatusColor(status, false));
                }
            }
            var holder = _host.ConnectedSlots.FirstOrDefault(x => IsInstanceValid(x) && x.CurrentHints.Any(y => y.FindingPlayer == h.FindingPlayer && y.LocationId == h.LocationId)) ?? view;
            AddAction("Find in Hints", "Show this hint in the Hints tab", () => { Go(holder, 6); holder.RevealHint(h.FindingPlayer, h.LocationId); });
            if (finderSlot != null && finderSlot.IsOnMap(h.LocationId))
                AddAction("Show on map", "Open the map at this location", () => { Go(finderSlot, 2); finderSlot.RevealOnMap(h.LocationId); });
            AddAction("Copy", "Copy the hint as a sentence", () => CopyText(text));
            EndActions();

            Section("Hint");
            Row("Item", ItemLink(view, h.ReceivingPlayer, h.ItemId, h.ItemFlags));
            Row("Classification", Colored(ClassOf(h.ItemFlags).Text, ClassOf(h.ItemFlags).Color));
            Row("For", PlayerLink(view, h.ReceivingPlayer));
            Row("Location", LocationLink(view, h.FindingPlayer, h.LocationId));
            Row("In world of", PlayerLink(view, h.FindingPlayer));
            if (!string.IsNullOrEmpty(h.Entrance)) PlainRow("Entrance", h.Entrance);
            Row("Status", Colored(StatusText(h.Status, h.Found), StatusColor(h.Status, h.Found)));
            Row("Found", h.Found ? Colored("Yes", Good) : Colored("No", Colors.White));
            if (!h.Found)
            {
                bool? logic = finderSlot?.IsLocationInLogic(h.LocationId);
                Row("In logic", logic == true ? Colored("Yes", Good) : logic == false ? Colored("No", Bad)
                    : finderSlot?.LogicHidden == true || view.LogicHidden ? Colored("Hidden by race mode", Muted)
                    : Colored($"Unknown (connect {PlayerName(s, h.FindingPlayer)} here to see)", Muted));
            }
            PlainRow("As text", text);

            Section("Advanced");
            PlainRow("Item id", h.ItemId.ToString());
            PlainRow("Location id", h.LocationId.ToString());
            PlainRow("Finding slot", h.FindingPlayer.ToString());
            PlainRow("Receiving slot", h.ReceivingPlayer.ToString());
            PlainRow("Item flags", h.ItemFlags.ToString());
        }

        // =====================================================================
        // Map pack: pin, map, pack
        // =====================================================================

        private void BuildPin(InspectTarget t)
        {
            var view = ViewSlot(t);
            var map = view?.MapTracker;
            var pin = map?.FindPin(t.MapId, t.PackLocationName);
            if (pin == null) { SetHeader("Map pin", t.PackLocationName ?? "Pin", Colored("This pin isn't in the loaded map pack.", Muted), Muted, ""); return; }
            var ids = map.GetLocationIds(pin);
            var checkedSet = new HashSet<long>(view.Session.Locations.AllLocationsChecked);
            int done = ids.Count(checkedSet.Contains);
            string mapName = map.Pack.Maps.TryGetValue(t.MapId ?? "", out var pm) ? pm.Name : t.MapId;

            SetHeader("Map pin", pin.Name, $"on {LinkTo(mapName ?? "?", InspectTarget.ForMap(view.ProfileId, view.SlotName, t.MapId))}",
                ids.Count == 0 ? Muted : done == ids.Count ? Muted : view.LogicHidden ? Colors.SteelBlue
                : ids.Any(id => !checkedSet.Contains(id) && view.IsLocationReachable(id)) ? Good : Bad,
                ids.Count == 0 ? "Not in this world" : $"{done} / {ids.Count} checked");

            BeginActions();
            if (ids.Count > 0) AddAction("Show on map", "Center the map on this pin", () => { Go(view, 2); view.RevealOnMap(ids[0]); });
            AddAction("Copy", "Copy the pin name", () => CopyText(pin.Name));
            EndActions();

            Section("Checks");
            if (pin.Sections != null && pin.Sections.Count > 0)
            {
                foreach (var sec in pin.Sections)
                {
                    string secName = !string.IsNullOrEmpty(sec.Name) ? sec.Name : pin.Name;
                    long id = view.Session.Locations.GetLocationIdFromName(view.Game, secName);
                    if (id <= 0)
                    {
                        AddText(Colored("? " + secName + "  (no matching location in this world)", Muted));
                        continue;
                    }
                    string state = map.LocationState(id);
                    var c = state == "checked" ? Muted : view.LogicHidden ? Colors.SteelBlue : state.Contains("in logic") ? Good : Bad;
                    AddText(LinkTo(secName, view.LocationTarget(id), Colors.LightGreen) + "  " + Colored(state, c) + (sec.ItemCount > 1 ? Colored($"  ×{sec.ItemCount}", Muted) : ""));
                }
            }
            else if (ids.Count > 0)
            {
                foreach (var id in ids) AddText(LocationLink(view, view.PlayerSlot, id) + "  " + Colored(map.LocationState(id), Colors.LightGray));
            }
            else AddHint("This pin has no sections that match this world's locations.");

            Section("Pack data");
            var maps = MapTrackerControl.MapsOf(pin);
            if (maps.Count > 0) Row("Maps", string.Join(", ", maps.Select(m => LinkTo(map.Pack.Maps.TryGetValue(m, out var mm) ? mm.Name : m, InspectTarget.ForMap(view.ProfileId, view.SlotName, m)))));
            if (pin.AccessRulesRaw != null && pin.AccessRulesRaw.HasValues) { AddText(Colored("Access rules", Colors.Gray)); AddCode(pin.AccessRulesRaw.ToString(Newtonsoft.Json.Formatting.None)); }
            if (pin.Children != null && pin.Children.Count > 0) PlainRow("Child locations", pin.Children.Count.ToString());
            PlainRow("Position", $"{pin.X:0}, {pin.Y:0}");
        }

        private void BuildMap(InspectTarget t)
        {
            var view = ViewSlot(t);
            var map = view?.MapTracker;
            if (map?.Pack == null || !map.Pack.Maps.TryGetValue(t.MapId ?? "", out var pm))
            {
                SetHeader("Map", t.MapId ?? "Map", Colored("This map isn't in the loaded map pack.", Muted), Muted, "");
                return;
            }
            var pins = map.PinsOnMap(t.MapId);
            var checkedSet = new HashSet<long>(view.Session.Locations.AllLocationsChecked);
            int inLogic = 0, outLogic = 0, done = 0, hinted = 0;
            var pinRows = new List<(string Name, int Open, int InLogic)>();
            foreach (var (loc, _, _) in pins)
            {
                var ids = map.GetLocationIds(loc);
                int open = 0, reach = 0;
                foreach (var id in ids)
                {
                    if (checkedSet.Contains(id)) { done++; continue; }
                    open++;
                    if (view.IsLocationReachable(id)) { inLogic++; reach++; } else outLogic++;
                    if (view.IsLocationHinted(id)) hinted++;
                }
                pinRows.Add((loc.Name, open, reach));
            }

            bool hidden = view.LogicHidden;
            SetHeader("Map", pm.Name, Colored(map.Pack.Manifest?.Name ?? "", Colors.LightGray),
                hidden ? Colors.SteelBlue : inLogic > 0 ? Good : Muted, hidden ? $"{inLogic + outLogic} open" : $"{inLogic} in logic");
            BeginActions();
            AddAction("Open map", "Show this map in the Map Tracker", () => { Go(view, 2); map.ShowMap(t.MapId); });
            EndActions();

            Section("Overview");
            PlainRow("Pins", pins.Count.ToString());
            if (hidden) PlainRow("Open", (inLogic + outLogic).ToString(), Colors.SteelBlue);
            else
            {
                PlainRow("In logic", inLogic.ToString(), Good);
                PlainRow("Out of logic", outLogic.ToString(), Bad);
            }
            PlainRow("Hinted", hinted.ToString(), Colors.DeepSkyBlue);
            PlainRow("Checked", done.ToString(), Muted);
            if (pm.BackgroundTexture != null) PlainRow("Image", $"{pm.BackgroundTexture.GetWidth()} × {pm.BackgroundTexture.GetHeight()}");
            if (pm.LocationSize > 0) PlainRow("Pin size", pm.LocationSize.ToString("0"));

            Section("Pins with open checks");
            // Logic hidden: order by name only, so the list order doesn't reveal logic.
            var openPins = pinRows.Where(r => r.Open > 0).OrderByDescending(r => hidden ? 0 : r.InLogic).ThenBy(r => r.Name).ToList();
            if (openPins.Count == 0) AddHint("Everything on this map is checked.");
            foreach (var r in openPins.Take(80))
                AddText(LinkTo(r.Name, InspectTarget.ForPackLocation(view.ProfileId, view.SlotName, t.MapId, r.Name)) +
                        Colored($"  {r.Open} open", Colors.LightGray) + (r.InLogic > 0 ? Colored($", {r.InLogic} in logic", Good) : ""));

            Section("Advanced");
            PlainRow("Map id", pm.Id);
            PlainRow("Image path", string.IsNullOrEmpty(pm.Img) ? pm.MapBg : pm.Img);
        }

        private void BuildPack(InspectTarget t)
        {
            string path = t.PackPath;
            // Parsing a pack reads the whole zip (images included); do it off the main thread the first time.
            if (File.Exists(path) && !PopTrackerPackLoader.IsPackCached(path) && _packReadsAttempted.Add(path))
            {
                SetHeader("Map pack", Path.GetFileNameWithoutExtension(path), Colored("Reading the pack…", Muted), Colors.Gray, "");
                var current = StillCurrent();
                Async.Fire(System.Threading.Tasks.Task.Run(() =>
                {
                    try { PopTrackerPackLoader.InspectZipPack(path); }
                    catch (Exception ex) { Logger.LogWarning($"Couldn't read the map pack {Path.GetFileName(path)}: {ex.Message}"); }
                }).ContinueWith(_ => Callable.From(() => current(() => Render(keepScroll: true))).CallDeferred(), System.Threading.Tasks.TaskScheduler.Default), "reading a map pack", tellUser: false);
                return;
            }
            LoadedPack pack = null;
            try { pack = File.Exists(path) ? PopTrackerPackLoader.InspectZipPack(path) : null; } catch { }
            var m = pack?.Manifest;
            string title = m?.Name ?? Path.GetFileNameWithoutExtension(path ?? "Map pack");
            var users = _host.ConnectedSlots.Where(sl => IsInstanceValid(sl) && sl.MapPackName != null && sl.MapPackName == m?.Name).ToList();
            SetHeader("Map pack", title, Colored(m?.GameName ?? "", Colors.LightGray), users.Count > 0 ? Good : Colors.Gray, users.Count > 0 ? "In use" : "Installed");

            BeginActions();
            if (File.Exists(path)) AddAction("Open folder", "Show the pack file in Explorer", () => AP_Atlas.Core.ExternalLinks.OpenFolder(Path.GetDirectoryName(path)));
            if (!string.IsNullOrEmpty(m?.VersionsUrl)) AddAction("Versions page", m.VersionsUrl, () => AP_Atlas.Core.ExternalLinks.OpenWeb(m.VersionsUrl));
            AddAction("Map Packs", "Open the Map Packs tab", () => _host.ShowGlobalTab(1));
            if (File.Exists(path)) AddAction("Pack Doctor…", "Check this pack against the game and fix problems locally", () => _host.OpenPackDoctor(path));
            EndActions();

            Section("Overview");
            PlainRow("Name", m?.Name);
            PlainRow("Game", m?.GameName);
            PlainRow("Version", m?.GetActualVersion());
            PlainRow("Author", m?.Author);
            if (!string.IsNullOrEmpty(m?.VersionsUrl)) Row("Versions URL", Link(m.VersionsUrl, () => AP_Atlas.Core.ExternalLinks.OpenWeb(m.VersionsUrl)));
            if (pack != null)
            {
                PlainRow("Maps", pack.Maps.Count.ToString());
                PlainRow("Locations", pack.Locations.Count.ToString());
                PlainRow("Items", pack.ItemsByCode.Count.ToString());
            }
            if (users.Count > 0)
                Row("Used by", string.Join(", ", users.Select(u => LinkTo(u.SlotName, InspectTarget.ForSlot(u.ProfileId, u.SlotName)))));

            Section("File");
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                PlainRow("Path", fi.FullName);
                PlainRow("Size", $"{fi.Length / 1024.0 / 1024.0:0.0} MB");
                PlainRow("Modified", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
            }
            else AddHint("The pack file no longer exists.");
        }

        // =====================================================================
        // Profile
        // =====================================================================

        private void BuildProfile(InspectTarget t)
        {
            var profile = _host.Profiles.FirstOrDefault(p => p.Id == t.ProfileId);
            if (profile == null) { SetHeader("Profile", "Profile", Colored("This profile was deleted.", Muted), Muted, ""); return; }
            var live = _host.ConnectedSlots.Where(s => IsInstanceValid(s) && s.ProfileId == profile.Id).ToList();
            SetHeader("Profile", profile.Name, Colored(profile.ServerUrl, Colors.LightGray), live.Count > 0 ? Good : Colors.Gray,
                $"{live.Count} / {profile.Slots.Count} connected");

            BeginActions();
            var notConnected = profile.Slots.Where(n => live.All(l => l.SlotName != n) && !_host.IsSlotConnecting(profile.Id, n)).ToList();
            if (notConnected.Count > 0) AddAction("Connect all", "Connect every slot in this profile", () => { foreach (var n in notConnected) _host.ConnectSlot(profile.Id, n); });
            AddAction("Edit", "Open this profile on the Connections tab", () => { _host.ShowGlobalTab(0); _host.SelectProfile(profile.Id); });
            AddAction("Copy server", "Copy the server address", () => CopyText(profile.ServerUrl));
            EndActions();

            Section("Overview");
            PlainRow("Name", profile.Name);
            PlainRow("Server", profile.ServerUrl);
            PlainRow("Password", string.IsNullOrEmpty(profile.Password) ? "None" : "Set");
            PlainRow("Slots", profile.Slots.Count.ToString());

            Section("Slots");
            foreach (var name in profile.Slots)
            {
                var slot = live.FirstOrDefault(l => l.SlotName == name);
                string state = slot != null ? Colored("● Live", Good) : _host.IsSlotConnecting(profile.Id, name) ? Colored("◌ Connecting", Colors.Yellow) : Colored("Offline", Muted);
                string stats = "";
                if (profile.SavedStats != null && profile.SavedStats.TryGetValue(name, out var st))
                    stats = Colored($"  {st.GameName}  {st.CompleteCount}/{st.TotalCount}", Colors.LightGray);
                AddText(LinkTo(name, InspectTarget.ForSlot(profile.Id, name), name == t.SlotName ? Colors.White : LinkColor) + "  " + state + stats);
            }

            if (!string.IsNullOrEmpty(t.SlotName))
            {
                var slot = live.FirstOrDefault(l => l.SlotName == t.SlotName);
                if (slot == null) BuildSavedStats(profile, t.SlotName);
            }

            BuildCheeseRoom(profile);

            Section("Advanced");
            PlainRow("Profile id", profile.Id);
        }

        // =====================================================================
        // Notes, flags and special marks
        // =====================================================================

        private void AddFlagMenu(string slotKey, string entityKey)
        {
            if (slotKey == null || entityKey == null) return;
            if (_actionBar == null) BeginActions();
            int flag = Annotations.GetFlag(slotKey, entityKey);
            var mb = new MenuButton
            {
                Text = flag > 0 ? "● " + Annotations.FlagLabel(flag) : "Flag ▾",
                TooltipText = "Flag this (F toggles your last flag)",
                Flat = false,
                FocusMode = FocusModeEnum.None
            };
            mb.SetMeta("font_size_ratio", 0.9);
            if (flag > 0) mb.AddThemeColorOverride("font_color", Annotations.FlagColor(flag));
            var popup = mb.GetPopup();
            for (int i = 1; i <= Annotations.FlagColors.Length; i++)
            {
                popup.AddItem("● " + Annotations.FlagLabel(i), i);
                popup.SetItemIconModulate(popup.GetItemIndex(i), Annotations.FlagColor(i));
            }
            popup.AddSeparator();
            popup.AddItem("Clear flag", 0);
            popup.AddItem("Rename flags…", 99);
            popup.IdPressed += id =>
            {
                if (id == 99) { ShowFlagLabelDialog(); return; }
                if (id > 0) { _host.Settings.LastFlagColor = (int)id; DataManager.SaveSettings(_host.Settings); }
                Annotations.SetFlag(slotKey, entityKey, (int)id);
            };
            _actionBar.AddChild(mb);
            _currentAnnotation = (slotKey, entityKey);
        }

        private void AddSpecialToggle(string game, string specialKey)
        {
            if (string.IsNullOrEmpty(game)) return;
            bool special = Annotations.GetSpecial(game, specialKey) != null;
            var b = AddAction(special ? "◆ Special" : "◇ Special", special ? $"Unmark as special for {game}" : $"Mark as special for every {game} slot (e.g. needed to goal)",
                () => Annotations.SetSpecial(game, specialKey, !special));
            if (special) b.AddThemeColorOverride("font_color", Annotations.SpecialColor);
        }

        private bool ToggleFlagShortcut()
        {
            if (_currentAnnotation == null) return false;
            var (slotKey, entityKey) = _currentAnnotation.Value;
            int flag = Annotations.GetFlag(slotKey, entityKey);
            Annotations.SetFlag(slotKey, entityKey, flag > 0 ? 0 : Math.Clamp(_host.Settings.LastFlagColor, 1, Annotations.FlagColors.Length));
            return true;
        }

        private void BuildAnnotations(string slotKey, string entityKey, string game, string specialKey, string ownerName)
        {
            Section("Notes & flags");
            if (slotKey != null && entityKey != null)
            {
                _currentAnnotation = (slotKey, entityKey);
                var a = Annotations.Get(slotKey, entityKey);
                int flag = a?.Flag ?? 0;

                var flags = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                flags.AddThemeConstantOverride("h_separation", 4);
                flags.AddThemeConstantOverride("v_separation", 4);
                for (int i = 0; i <= Annotations.FlagColors.Length; i++)
                {
                    int f = i;
                    var b = new Button
                    {
                        Text = f == 0 ? "None" : "● " + Annotations.FlagLabel(f),
                        ToggleMode = true,
                        ButtonPressed = flag == f,
                        FocusMode = FocusModeEnum.None,
                        TooltipText = f == 0 ? "No flag" : $"Flag: {Annotations.FlagLabel(f)}"
                    };
                    b.SetMeta("font_size_ratio", 0.85);
                    if (f > 0) b.AddThemeColorOverride("font_color", Annotations.FlagColor(f));
                    b.Pressed += () =>
                    {
                        if (f > 0) { _host.Settings.LastFlagColor = f; DataManager.SaveSettings(_host.Settings); }
                        Annotations.SetFlag(slotKey, entityKey, f);
                    };
                    flags.AddChild(b);
                }
                Target.AddChild(flags);
                _plainText.AppendLine("Flag: " + (flag > 0 ? Annotations.FlagLabel(flag) : "none"));

                AddText(Colored($"Note (only for {ownerName})", Colors.Gray));
                Target.AddChild(MakeNoteEditor(a?.Note ?? "", "Anything you want to remember about this…",
                    text => Annotations.SetNote(slotKey, entityKey, text)));
                if (!string.IsNullOrWhiteSpace(a?.Note)) _plainText.AppendLine("Note: " + a.Note);
                if (a != null) AddHint("Last changed " + a.Updated.ToString("yyyy-MM-dd HH:mm"));
            }

            if (!string.IsNullOrEmpty(game) && specialKey != null)
            {
                var special = Annotations.GetSpecial(game, specialKey);
                var check = new CheckButton
                {
                    Text = $"◆ Special for every {game} slot",
                    ButtonPressed = special != null,
                    FocusMode = FocusModeEnum.None,
                    TooltipText = "Special marks belong to the game, so they apply in every multiworld (e.g. items needed to goal)."
                };
                check.AddThemeColorOverride("font_color", Annotations.SpecialColor);
                check.AddThemeColorOverride("font_pressed_color", Annotations.SpecialColor);
                check.Toggled += on => Annotations.SetSpecial(game, specialKey, on);
                Target.AddChild(check);
                if (special != null)
                {
                    Target.AddChild(MakeNoteEditor(special.Note ?? "", $"Why it matters (shared by every {game} slot)…",
                        text => Annotations.SetSpecial(game, specialKey, true, text)));
                    if (!string.IsNullOrWhiteSpace(special.Note)) _plainText.AppendLine("Special note: " + special.Note);
                }
            }
        }

        /// <summary>A multi-line note field that saves when you pause typing or leave it.</summary>
        private Control MakeNoteEditor(string initial, string placeholder, Action<string> save)
        {
            var edit = new TextEdit
            {
                Text = initial,
                PlaceholderText = placeholder,
                WrapMode = TextEdit.LineWrappingMode.Boundary,
                ScrollFitContentHeight = true,
                CustomMinimumSize = new Vector2(0, 64),
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            string lastSaved = initial;
            var timer = new Timer { OneShot = true, WaitTime = 1.0 };
            void Flush()
            {
                if (edit.Text == lastSaved) return;
                lastSaved = edit.Text;
                save(edit.Text);
            }
            timer.Timeout += Flush;
            edit.AddChild(timer);
            edit.TextChanged += () => timer.Start();
            edit.FocusExited += Flush;
            edit.TreeExiting += Flush;
            return edit;
        }

        private void ShowFlagLabelDialog()
        {
            var dialog = new ConfirmationDialog { Title = "Flag labels", OkButtonText = "Save" };
            var box = new VBoxContainer();
            var edits = new List<LineEdit>();
            for (int i = 1; i <= Annotations.FlagColors.Length; i++)
            {
                var row = new HBoxContainer();
                var dot = new Label { Text = "●" };
                dot.AddThemeColorOverride("font_color", Annotations.FlagColor(i));
                row.AddChild(dot);
                var edit = new LineEdit { Text = Annotations.FlagLabel(i), CustomMinimumSize = new Vector2(260, 0) };
                row.AddChild(edit);
                edits.Add(edit);
                box.AddChild(row);
            }
            dialog.AddChild(box);
            dialog.Confirmed += () =>
            {
                for (int i = 0; i < edits.Count; i++) Annotations.SetFlagLabel(i + 1, edits[i].Text);
                dialog.QueueFree();
            };
            dialog.Canceled += () => dialog.QueueFree();
            GetTree().Root.AddChild(dialog);
            dialog.PopupCentered();
        }

        private void ExportSpecials(string game)
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.SaveFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.json ; Atlas special list" },
                UseNativeDialog = true,
                Title = $"Export {game} special list",
                CurrentFile = $"{game} specials.json"
            };
            dialog.FileSelected += path =>
            {
                try
                {
                    int n = Annotations.ExportSpecials(game, path);
                    _host.Toast($"Exported {n} special entries for {game}", Colors.Gray);
                }
                catch (Exception ex) { _host.Toast("Export failed: " + ex.Message, Bad); }
                dialog.QueueFree();
            };
            dialog.Canceled += () => dialog.QueueFree();
            GetTree().Root.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        private void ImportYamlExclusions(SlotTrackerControl slot)
        {
            string players = string.IsNullOrEmpty(_host.Settings?.ArchipelagoInstallationPath) ? "" : System.IO.Path.Combine(_host.Settings.ArchipelagoInstallationPath, "Players");
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.yaml, *.yml ; Archipelago player YAML" },
                UseNativeDialog = true,
                Title = $"Exclusions for {slot.SlotName}: choose your {slot.Game} YAML"
            };
            if (System.IO.Directory.Exists(players)) dialog.CurrentDir = players;
            dialog.FileSelected += path => Async.Fire(async () =>
            {
                dialog.QueueFree();
                var yaml = YamlExclusions.Read(path, slot.Game, slot.SlotName);
                if (yaml.Error != null) { _host.Toast("Couldn't use that YAML: " + yaml.Error, Bad); return; }
                if (yaml.Names.Count == 0) { _host.Toast($"{System.IO.Path.GetFileName(path)} has no exclude_locations for {slot.Game}.", Colors.Gray); return; }
                if (!IsInstanceValid(slot)) return;
                var (applied, listed, unknown) = await slot.ApplyYamlExclusionsAsync(yaml.Names);
                string msg = applied > 0 ? $"Excluded {applied} location(s)" : listed > 0 ? "Those locations were already excluded" : "None of those locations are in this seed";
                if (unknown.Count > 0)
                {
                    msg += $"; {unknown.Count} name(s) not found";
                    Logger.LogWarning($"[{slot.SlotName}] exclude_locations names not found in {slot.Game}: {string.Join(", ", unknown)}");
                }
                _host.Toast(msg, applied > 0 ? Good : Colors.Gray);
            }, $"applying excluded locations from {System.IO.Path.GetFileName(path)}");
            dialog.Canceled += () => dialog.QueueFree();
            GetTree().Root.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        private void ConfirmResetExclusions(SlotTrackerControl slot, int count)
        {
            var dialog = new ConfirmationDialog
            {
                Title = "Reset exclusions",
                DialogText = $"Forget the {count} location(s) you excluded or included for {slot.SlotName}?\nThe seed's own exclusions stay.",
                OkButtonText = "Reset"
            };
            dialog.Confirmed += () => { Annotations.ClearExclusionOverrides(slot.AnnotationKey); dialog.QueueFree(); };
            dialog.Canceled += () => dialog.QueueFree();
            GetTree().Root.AddChild(dialog);
            dialog.PopupCentered();
        }

        private void ImportSpecials()
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.json ; Atlas special list" },
                UseNativeDialog = true,
                Title = "Import a special list"
            };
            dialog.FileSelected += path =>
            {
                try
                {
                    var (game, added) = Annotations.ImportSpecials(path);
                    _host.Toast($"Imported {added} new special entries for {game}", Annotations.SpecialColor);
                }
                catch (Exception ex) { _host.Toast("Import failed: " + ex.Message, Bad); }
                dialog.QueueFree();
            };
            dialog.Canceled += () => dialog.QueueFree();
            GetTree().Root.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        // =====================================================================
        // Shared actions
        // =====================================================================

        private void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            DisplayServer.ClipboardSet(text);
            _host.Toast("Copied: " + text, Colors.Gray);
        }

        /// <summary>Sends a !hint command after confirming the hint point cost.</summary>
        private void RequestHint(SlotTrackerControl slot, string command, string what)
        {
            var s = slot.Session;
            if (s == null || !s.Socket.Connected) { _host.Toast("That slot isn't connected.", Bad); return; }
            var room = s.RoomState;
            string cost = room != null && room.HintCost > 0 ? $"This costs {room.HintCost} of your {room.HintPoints} hint points." : "";
            var dialog = new ConfirmationDialog
            {
                Title = "Request hint",
                DialogText = $"Ask the server for a hint about \"{what}\" as {slot.SlotName}?\n{cost}",
                OkButtonText = "Request"
            };
            dialog.Confirmed += () =>
            {
                Async.Fire(s.Socket.SendPacketAsync(new SayPacket { Text = command }), "sending your hint request");
                _host.Toast("Requested: " + command, Colors.Gray);
                dialog.QueueFree();
            };
            dialog.Canceled += () => dialog.QueueFree();
            GetTree().Root.AddChild(dialog);
            dialog.PopupCentered();
        }
    }
}
