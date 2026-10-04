using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>One game's item and location name ↔ id tables, and where they came from.</summary>
    public sealed class GameNameTable
    {
        public string Game { get; set; } = "";
        /// <summary>"server" (a live or saved connection) or "local install" (your installed apworld).</summary>
        public string Source { get; set; } = "";
        public DateTime Fetched { get; set; } = DateTime.Now;
        /// <summary>The data package checksum (server), or the apworld's version/stamp (local).</summary>
        public string Version { get; set; } = "";
        public Dictionary<string, long> Items { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> Locations { get; set; } = new Dictionary<string, long>();

        public Dictionary<long, string> ItemsById() => Items.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        public Dictionary<long, string> LocationsById() => Locations.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
    }

    /// <summary>
    /// Name tables per game from every source the app has: the server's data package (live connections, saved
    /// to disk so the most recent connection stays available offline) and the local Archipelago install (via the
    /// logic bridge). <see cref="Best"/> merges them, preferring the server. Main thread only.
    /// </summary>
    public static class GameNames
    {
        public const string ServerSource = "server";
        public const string LocalSource = "local install";

        /// <summary>Raised (main thread) when a game's tables were added or refreshed.</summary>
        public static event Action<string> Updated;

        private static readonly Dictionary<string, GameNameTable> _server = new Dictionary<string, GameNameTable>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, GameNameTable> _local = new Dictionary<string, GameNameTable>(StringComparer.OrdinalIgnoreCase);
        private static bool _diskLoaded;

        private static string CacheDir => Path.Combine(DataManager.GetDataDirectory(), "name_cache");

        private static string FileFor(string game, string source)
        {
            string name = $"{game} ({source}).json";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(CacheDir, name);
        }

        private static void EnsureDiskLoaded()
        {
            if (_diskLoaded) return;
            _diskLoaded = true;
            try
            {
                if (!Directory.Exists(CacheDir)) return;
                foreach (var file in Directory.GetFiles(CacheDir, "*.json"))
                {
                    try
                    {
                        var table = JsonConvert.DeserializeObject<GameNameTable>(File.ReadAllText(file));
                        if (table == null || string.IsNullOrEmpty(table.Game)) continue;
                        var target = table.Source == LocalSource ? _local : _server;
                        if (!target.TryGetValue(table.Game, out var existing) || existing.Fetched < table.Fetched) target[table.Game] = table;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"Ignoring unreadable name cache '{Path.GetFileName(file)}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not read the name cache: " + ex.Message);
            }
        }

        /// <summary>Stores a table (from a connection or the local install) and saves it for offline use.</summary>
        public static void Store(GameNameTable table)
        {
            if (table == null || string.IsNullOrEmpty(table.Game)) return;
            EnsureDiskLoaded();
            (table.Source == LocalSource ? _local : _server)[table.Game] = table;
            try
            {
                Directory.CreateDirectory(CacheDir);
                SafeFile.WriteJson(FileFor(table.Game, table.Source), table, Formatting.None);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Could not save names for {table.Game}: {ex.Message}");
            }
            Updated?.Invoke(table.Game);
        }

        public static GameNameTable Server(string game)
        {
            EnsureDiskLoaded();
            return game != null && _server.TryGetValue(game, out var t) ? t : null;
        }

        public static GameNameTable Local(string game)
        {
            EnsureDiskLoaded();
            return game != null && _local.TryGetValue(game, out var t) ? t : null;
        }

        /// <summary>
        /// The most accurate table available: the server's (live or the most recent connection), with names only
        /// the local install knows added in. Null when neither source has the game.
        /// </summary>
        public static GameNameTable Best(string game)
        {
            var server = Server(game);
            var local = Local(game);
            if (server == null) return local;
            if (local == null) return server;
            var merged = new GameNameTable
            {
                Game = server.Game,
                Source = $"{ServerSource} + {LocalSource}",
                Fetched = server.Fetched,
                Version = server.Version,
                Items = new Dictionary<string, long>(server.Items),
                Locations = new Dictionary<string, long>(server.Locations)
            };
            foreach (var kv in local.Items) merged.Items.TryAdd(kv.Key, kv.Value);
            foreach (var kv in local.Locations) merged.Locations.TryAdd(kv.Key, kv.Value);
            return merged;
        }

        /// <summary>
        /// The Archipelago game a pack is for, by the same loose matching the pack loader uses ("Dark Souls 2" ↔
        /// "Dark Souls II"), among games with stored names or known to the local install. Null if none matches.
        /// </summary>
        public static string ResolveGame(PopTrackerManifest manifest)
        {
            if (manifest == null) return null;
            EnsureDiskLoaded();
            var candidates = _server.Keys.Concat(_local.Keys).Concat(KnownGames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var name in new[] { manifest.GameName, manifest.Name })
            {
                if (string.IsNullOrEmpty(name)) continue;
                var exact = candidates.FirstOrDefault(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
                var loose = candidates.FirstOrDefault(g => PopTrackerPackLoader.IsGameNameMatch(name, g));
                if (loose != null) return loose;
            }
            return null;
        }

        /// <summary>Every game the local Archipelago install knows (from the last local fetch).</summary>
        public static List<string> KnownGames { get; private set; } = new List<string>();

        public static void SetKnownGames(IEnumerable<string> games)
        {
            KnownGames = games?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        }

        /// <summary>Names the two sources disagree on (a sign the installed apworld isn't the server's version).</summary>
        public static (int ItemDiffs, int LocationDiffs) CompareSources(string game)
        {
            var server = Server(game);
            var local = Local(game);
            if (server == null || local == null) return (0, 0);
            int Diff(Dictionary<string, long> a, Dictionary<string, long> b) =>
                a.Count(kv => !b.TryGetValue(kv.Key, out var id) || id != kv.Value) + b.Keys.Count(k => !a.ContainsKey(k));
            return (Diff(server.Items, local.Items), Diff(server.Locations, local.Locations));
        }
    }
}
