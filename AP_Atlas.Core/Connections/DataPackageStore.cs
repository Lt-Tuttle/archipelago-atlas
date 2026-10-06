using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Archipelago.MultiClient.Net.Models;
using Newtonsoft.Json;

namespace AP_Atlas.Core.Connections
{
    /// <summary>
    /// Each game's data package (its item and location names) as servers sent it, kept in Atlas's own folder: one file per
    /// game and version (checksum), so a server sends a game's names once per version instead of on every connection.
    /// </summary>
    /// <remarks>
    /// The connection library would otherwise keep these in %LocalAppData%\Archipelago\Cache, outside Atlas's folder;
    /// <see cref="AtlasSessions"/> gives every session this store instead. It's a cache, not the user's data: a lost or
    /// damaged file is simply asked for again, so it's written without the backup copy SafeFile keeps. Several connections
    /// use it at once, from their own threads.
    /// </remarks>
    public sealed class DataPackageStore
    {
        private readonly object _gate = new();

        public DataPackageStore(string folder) => Folder = Path.GetFullPath(folder);

        /// <summary>Where the files are kept.</summary>
        public string Folder { get; }

        /// <summary>
        /// The stored names for this game and checksum. False when there are none, or the file is damaged or doesn't match
        /// (it's then removed, and the server is asked again).
        /// </summary>
        public bool TryGet(string game, string? checksum, [NotNullWhen(true)] out GameData? data)
        {
            data = null;
            string? path = PathFor(game, checksum);
            if (path == null) return false;
            lock (_gate)
            {
                try
                {
                    if (!File.Exists(path)) return false;
                    var entry = JsonConvert.DeserializeObject<Entry>(File.ReadAllText(path));
                    if (entry?.Data is not { } stored || entry.Game != game || stored.Checksum != checksum || stored.ItemLookup == null || stored.LocationLookup == null)
                    {
                        Logger.LogDebug($"Stored names for {game} don't match their file name; asking the server again.");
                        Remove(path);
                        return false;
                    }
                    MarkUsed(path);
                    data = stored;
                    return true;
                }
                catch (JsonException ex)
                {
                    Logger.LogDebug($"Stored names for {game} are damaged ({ex.Message}); asking the server again.");
                    Remove(path);
                    return false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.LogDebug($"Stored names for {game} couldn't be read ({ex.Message}); asking the server again.");
                    return false;
                }
            }
        }

        /// <summary>Keeps a game's names as a server sent them. Names without a usable checksum aren't kept.</summary>
        public void Save(string game, GameData? data)
        {
            string? path = data == null ? null : PathFor(game, data.Checksum);
            if (path == null) return;
            // A name of its own, so a write that fails halfway never leaves a damaged file under the real name.
            string temp = $"{path}.{Guid.NewGuid():N}.tmp";
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(temp, JsonConvert.SerializeObject(new Entry { Game = game, Data = data }));
                    File.Move(temp, path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.LogDebug($"Names for {game} weren't stored ({ex.Message}); the server will send them again next time.");
                    Remove(temp);
                }
            }
        }

        /// <summary>
        /// Removes names nobody has used for <paramref name="unusedFor"/> (a game's older versions pile up otherwise), and
        /// anything a write left behind. Never creates the folder. Returns how many files were removed.
        /// </summary>
        public int RemoveUnused(TimeSpan unusedFor)
        {
            int removed = 0;
            lock (_gate)
            {
                if (!Directory.Exists(Folder)) return 0;
                var cutoff = DateTime.UtcNow - unusedFor; // wall clock: files' times
                var tempCutoff = DateTime.UtcNow - TimeSpan.FromHours(1); // wall clock: files' times
                foreach (string dir in SafeList(() => Directory.GetDirectories(Folder)))
                {
                    foreach (string file in SafeList(() => Directory.GetFiles(dir)))
                    {
                        bool leftover = file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                        DateTime written;
                        try { written = File.GetLastWriteTimeUtc(file); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                        if (written < (leftover ? tempCutoff : cutoff) && Remove(file)) removed++;
                    }
                    try { if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // in use: next time
                }
            }
            return removed;
        }

        /// <summary>The file for a game's names, or null when the game or checksum can't be stored safely.</summary>
        private string? PathFor(string game, string? checksum)
        {
            if (string.IsNullOrWhiteSpace(game) || !IsChecksum(checksum)) return null;
            string path = Path.GetFullPath(Path.Combine(Folder, FolderName(game), checksum + ".json"));
            // Whatever a server sends as a game's name, the file stays inside the store.
            return path.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        /// <summary>Archipelago's checksums are hexadecimal SHA-1 hashes; anything else isn't used as a file name.</summary>
        private static bool IsChecksum([NotNullWhen(true)] string? checksum) =>
            !string.IsNullOrEmpty(checksum) && checksum.Length <= 128 && checksum.All(char.IsAsciiLetterOrDigit);

        /// <summary>
        /// A folder name for a game: characters Windows doesn't allow are replaced, device names (CON, NUL, …) get a prefix,
        /// and a long name is shortened. The stored file also holds the game's exact name, so two games that end up with the
        /// same folder name can't be mixed up.
        /// </summary>
        internal static string FolderName(string game)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = new StringBuilder(game.Length);
            foreach (char c in game) name.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
            string safe = name.ToString().Trim().TrimEnd('.', ' ');
            if (safe.Length == 0 || safe.Trim('.').Length == 0) safe = "_";
            string stem = safe.Split('.')[0].Trim().ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsAsciiDigit(stem[3])))
                safe = "_" + safe;
            if (safe.Length > 60)
                safe = safe[..48].TrimEnd(' ', '.') + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game)))[..10];
            return safe;
        }

        /// <summary>Marks a file as used (at most once a day), so <see cref="RemoveUnused"/> keeps it.</summary>
        private static void MarkUsed(string path)
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // wall clock: the file's time
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // only bookkeeping
        }

        private static bool Remove(string path)
        {
            try { File.Delete(path); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; } // in use: next time
        }

        private static string[] SafeList(Func<string[]> list)
        {
            try { return list(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        /// <summary>What a file holds: the game's exact name and its data package.</summary>
        private sealed class Entry
        {
            public string Game { get; set; } = "";
            public GameData? Data { get; set; }
        }
    }
}
