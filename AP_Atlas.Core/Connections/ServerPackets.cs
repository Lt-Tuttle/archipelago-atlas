using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Packets;

namespace AP_Atlas.Core.Connections
{
    /// <summary>
    /// What a server sends, made safe before the connection library or Atlas reads it. A server (or anyone listening on a
    /// room's port) may send anything, and the library trusts it: a player numbered in the millions makes it set aside a
    /// list that size (gigabytes), a player missing from the slot info or a gap in the numbers leaves it with no players or
    /// empty places that fail every later lookup, a room listing thousands of games makes Atlas ask for each one's data
    /// package, and a duplicate location in a scout's answer leaves the scout waiting for good. Each packet is cleaned in
    /// place by the first of the session's packet listeners (<see cref="Install"/>), so every listener after it, the
    /// library's own and Atlas's, sees the same safe packet. What was changed is logged once per kind and connection.
    /// </summary>
    /// <remarks>
    /// The limits are far beyond real rooms (Atlas's target is a 1,000-player multiworld; teams are rare) and keep the
    /// library's player table, which has a place for every team and slot number up to the highest, under 160,000 places.
    /// </remarks>
    public sealed class ServerPackets
    {
        /// <summary>Teams are numbered 0 to <c>MaxTeams - 1</c>.</summary>
        public const int MaxTeams = 16;
        /// <summary>Slots are numbered 1 to <see cref="MaxSlots"/> (0 is the server).</summary>
        public const int MaxSlots = 10_000;
        /// <summary>The most games a room's info or a data package may name.</summary>
        public const int MaxGames = 1_000;

        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private readonly string _slot;
        private readonly object _lock = new();
        // What has been logged on this connection: each kind once.
        private readonly HashSet<string> _logged = new();
        // The player table the library built from the login (Connected): every team up to this one, every slot up to that one.
        private int _maxTeam = -1, _maxSlot;

        /// <param name="slot">The slot this connection logs in to, for the log.</param>
        public ServerPackets(string slot) => _slot = slot;

        /// <summary>Why this version of the connection library can't have its packets cleaned first, or null when it can.</summary>
        internal static string? Problem { get; } = FindProblem();

        private static FieldInfo? ListenersField(Type? type)
        {
            for (; type != null; type = type.BaseType)
                if (type.GetField(nameof(IArchipelagoSocketHelper.PacketReceived), InstanceFields | BindingFlags.DeclaredOnly) is { } field
                    && field.FieldType == typeof(ArchipelagoSocketHelperDelagates.PacketReceivedHandler))
                    return field;
            return null;
        }

        private static string? FindProblem() =>
            ListenersField(typeof(ArchipelagoSocketHelper)) == null ? "its socket's packet listeners are kept differently" : null;

        /// <summary>
        /// Puts a new guard for <paramref name="slot"/> first among the session's packet listeners (the library's own
        /// listeners were added as the session was made). Returns the guard.
        /// </summary>
        /// <exception cref="NotSupportedException">This version of the library keeps its listeners differently.</exception>
        internal static ServerPackets Install(ArchipelagoSession session, string slot)
        {
            if (Problem != null) throw Unsupported(Problem);
            var guard = new ServerPackets(slot);
            object socket = session.Socket;
            var field = ListenersField(socket.GetType()) ?? throw Unsupported("its socket's packet listeners are kept differently");
            var first = new ArchipelagoSocketHelperDelagates.PacketReceivedHandler(guard.Clean);
            field.SetValue(socket, Delegate.Combine(first, (Delegate?)field.GetValue(socket)));
            return guard;
        }

        /// <summary>Whether the guard is the first listener of the session's packets (for tests).</summary>
        internal static bool IsFirst(ArchipelagoSession session) =>
            ListenersField(session.Socket.GetType())?.GetValue(session.Socket) is Delegate listeners
            && listeners.GetInvocationList()[0].Target is ServerPackets;

        private static NotSupportedException Unsupported(string why) => new(
            $"This version of the Archipelago connection library can't have a server's packets checked before it reads them ({why}), " +
            "so Atlas won't connect with it. Please report this.");

        /// <summary>
        /// Makes one packet safe, in place. Never throws: a failure here would keep the packet from every other listener
        /// (the library stops at the first that fails), so it's logged instead and the packet goes on as it came.
        /// </summary>
        public void Clean(ArchipelagoPacketBase packet)
        {
            try
            {
                switch (packet)
                {
                    case RoomInfoPacket info: CleanRoomInfo(info); break;
                    case DataPackagePacket data: CleanDataPackage(data); break;
                    case ConnectedPacket connected: CleanConnected(connected); break;
                    case RoomUpdatePacket update: CleanRoomUpdate(update); break;
                    case ReceivedItemsPacket items when items.Items == null:
                        items.Items = Array.Empty<NetworkItem>();
                        break;
                    case LocationInfoPacket scouts: CleanScouts(scouts); break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Note("failed", $"A packet from the server couldn't be checked ({packet.PacketType}: {ex.Message}); it was passed on as it came.");
            }
        }

        private void CleanRoomInfo(RoomInfoPacket info)
        {
            var games = UsableGames(info.Games ?? Array.Empty<string>(), out int dropped);
            if (dropped > 0) Note("room-games", $"The server's room info named {dropped} game(s) Atlas doesn't use (more than {MaxGames}, repeated, or names too long); it asks only for the others' names.");
            info.Games = games;
            if (info.DataPackageChecksums != null)
            {
                var kept = new HashSet<string>(games);
                info.DataPackageChecksums = info.DataPackageChecksums.Where(entry => kept.Contains(entry.Key) && entry.Value != null)
                    .ToDictionary(entry => entry.Key, entry => entry.Value);
            }
        }

        private void CleanDataPackage(DataPackagePacket data)
        {
            data.DataPackage ??= new DataPackage();
            var sent = data.DataPackage.Games ?? new Dictionary<string, GameData>();
            var keep = new HashSet<string>(UsableGames(sent.Where(entry => entry.Value != null).Select(entry => entry.Key), out _));
            var games = new Dictionary<string, GameData>();
            foreach (var (name, game) in sent)
            {
                if (!keep.Contains(name)) continue;
                game.ItemLookup ??= new Dictionary<string, long>();
                game.LocationLookup ??= new Dictionary<string, long>();
                games[name] = game;
            }
            if (games.Count < sent.Count) Note("package-games", $"A data package from the server held {sent.Count - games.Count} game(s) Atlas doesn't use (empty, more than {MaxGames}, or names too long).");
            data.DataPackage.Games = games;
        }

        /// <summary>The games worth asking for: named, not repeated, not too long, at most <see cref="MaxGames"/>.</summary>
        private static string[] UsableGames(IEnumerable<string> names, out int dropped)
        {
            var all = names.ToList();
            var usable = all.Where(name => !string.IsNullOrEmpty(name) && name.Length <= NameLimits.MaxName).Distinct().Take(MaxGames).ToArray();
            dropped = all.Count - usable.Length;
            return usable;
        }

        private void CleanConnected(ConnectedPacket connected)
        {
            connected.LocationsChecked ??= Array.Empty<long>();
            connected.MissingChecks ??= Array.Empty<long>();
            if (connected.Team is < 0 or >= MaxTeams)
            {
                Note("own-team", $"The server put this slot on team {connected.Team}, which can't be; Atlas takes it as the first team.");
                connected.Team = 0;
            }
            var sent = connected.Players ?? Array.Empty<NetworkPlayer>();
            var players = new Dictionary<(int Team, int Slot), NetworkPlayer>();
            foreach (var player in sent)
                if (player.Team is >= 0 and < MaxTeams && player.Slot is >= 1 and <= MaxSlots) players.TryAdd((player.Team, player.Slot), player);
            if (players.Count < sent.Length)
                Note("players", $"The server's login listed {sent.Length - players.Count} player(s) Atlas can't use (numbered outside {MaxTeams} teams of {MaxSlots} slots, or twice).");
            int maxTeam = players.Keys.Select(key => key.Team).Append(connected.Team).Max();
            // Groups (item links) may be numbered in the slot info only.
            var infoSlots = connected.SlotInfo?.Keys.Where(slot => slot is >= 1 and <= MaxSlots) ?? Enumerable.Empty<int>();
            int maxSlot = players.Keys.Select(key => key.Slot).Concat(infoSlots).Append(connected.Slot is >= 1 and <= MaxSlots ? connected.Slot : 0).Max();
            // The library sets aside a place for every team and slot number up to the highest, and fails on an empty one:
            // a number nobody has gets a stand-in player.
            int filled = 0;
            var list = new List<NetworkPlayer>((maxTeam + 1) * maxSlot);
            for (int team = 0; team <= maxTeam; team++)
                for (int slot = 1; slot <= maxSlot; slot++)
                {
                    if (!players.TryGetValue((team, slot), out var player))
                    {
                        // A group (item link) may be named in the slot info only.
                        string? known = connected.SlotInfo != null && connected.SlotInfo.TryGetValue(slot, out var entry) ? entry.Name : null;
                        player = new NetworkPlayer { Team = team, Slot = slot, Name = known ?? "" };
                        if (string.IsNullOrEmpty(known)) filled++;
                    }
                    string name = NameLimits.Cap(string.IsNullOrEmpty(player.Name) ? $"Player {slot}" : player.Name);
                    list.Add(new NetworkPlayer { Team = team, Slot = slot, Name = name, Alias = NameLimits.Cap(string.IsNullOrEmpty(player.Alias) ? name : player.Alias) });
                }
            if (filled > 0) Note("gaps", $"The server's login left {filled} player number(s) without a player; Atlas shows them as \"Player <number>\".");
            connected.Players = list.ToArray();
            if (connected.SlotInfo != null)
            {
                // The library looks every player's slot up in the slot info, and a group's members in it.
                var info = new Dictionary<int, NetworkSlot>();
                int unusable = 0;
                foreach (var (slot, entry) in connected.SlotInfo)
                    if (slot is < 1 or > MaxSlots) unusable++;
                    else
                        info[slot] = new NetworkSlot
                        {
                            Name = NameLimits.Cap(entry.Name ?? ""),
                            Game = NameLimits.Cap(entry.Game ?? ""),
                            Type = entry.Type,
                            GroupMembers = (entry.GroupMembers ?? Array.Empty<int>()).Where(member => member is >= 1 and <= MaxSlots).Distinct().ToArray()
                        };
                int missing = 0;
                for (int slot = 1; slot <= maxSlot; slot++)
                    if (!info.ContainsKey(slot))
                    {
                        missing++;
                        var player = list[(connected.Team * maxSlot) + slot - 1];
                        info[slot] = new NetworkSlot { Name = player.Name, Game = "", Type = SlotType.Player, GroupMembers = Array.Empty<int>() };
                    }
                if (missing > 0 || unusable > 0)
                    Note("slot-info", $"The server's slot info missed {missing} slot(s) or named slots that don't exist; Atlas fills in or leaves out those.");
                connected.SlotInfo = info;
            }
            lock (_lock)
            {
                _maxTeam = maxTeam;
                _maxSlot = maxSlot;
            }
        }

        private void CleanRoomUpdate(RoomUpdatePacket update)
        {
            if (update.Players == null || update.Players.Length == 0) return;
            int maxTeam, maxSlot;
            lock (_lock) (maxTeam, maxSlot) = (_maxTeam, _maxSlot);
            // The library changes the names of players it has, and fails on one it hasn't.
            var known = update.Players.Where(p => p.Team >= 0 && p.Team <= maxTeam && p.Slot >= 1 && p.Slot <= maxSlot)
                .Select(p => new NetworkPlayer { Team = p.Team, Slot = p.Slot, Name = NameLimits.Cap(p.Name ?? ""), Alias = NameLimits.Cap(p.Alias ?? p.Name ?? "") })
                .ToArray();
            if (known.Length < update.Players.Length) Note("update-players", $"A room update from the server named {update.Players.Length - known.Length} player(s) the login didn't; Atlas leaves them out.");
            update.Players = known;
        }

        private void CleanScouts(LocationInfoPacket scouts)
        {
            var sent = scouts.Locations ?? Array.Empty<NetworkItem>();
            // The library makes a table by location, which fails (and leaves the scout waiting for good) on a repeated one.
            var distinct = sent.GroupBy(item => item.Location).Select(group => group.First()).ToArray();
            if (distinct.Length < sent.Length) Note("scouts", $"A scout's answer from the server named {sent.Length - distinct.Length} location(s) twice; Atlas keeps the first.");
            scouts.Locations = distinct;
        }

        /// <summary>Logs what was changed, once per kind on this connection (a server that keeps sending it doesn't fill the log).</summary>
        private void Note(string kind, string message)
        {
            lock (_lock)
                if (!_logged.Add(kind)) return;
            Logger.LogWarning(_slot.Length > 0 ? $"[{_slot}] {message}" : message);
        }
    }
}
