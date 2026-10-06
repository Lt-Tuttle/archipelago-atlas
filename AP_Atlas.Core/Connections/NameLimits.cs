using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Archipelago.MultiClient.Net.Models;

namespace AP_Atlas.Core.Connections
{
    /// <summary>
    /// The longest name Atlas takes from a server: a game's item and location names, and a hint's entrance. They come from
    /// apworlds, which anyone can write, and a megabyte-long name held up the window for minutes while it was laid out.
    /// A longer one is cut, and stays apart from every other name: its end is a short hash of the whole of it. Across the 83
    /// official games (16,934 item names, 86,431 location names) the longest is 111 characters.
    /// </summary>
    public static class NameLimits
    {
        public const int MaxName = 500;

        /// <summary>The name, or its first characters, "…" and eight hex digits of a hash of the whole name when it's too long.</summary>
        [return: NotNullIfNotNull(nameof(name))]
        public static string? Cap(string? name)
        {
            if (name == null || name.Length <= MaxName) return name;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
            int keep = MaxName - 1 - hash.Length;
            if (char.IsHighSurrogate(name[keep - 1])) keep--; // never half a character
            return name[..keep] + "…" + hash;
        }

        /// <summary>A game's names with every one too long cut; the same object when none is.</summary>
        public static GameData Capped(GameData data, out bool changed)
        {
            changed = TooLong(data.ItemLookup) || TooLong(data.LocationLookup);
            if (!changed) return data;
            var copy = new GameData();
            // Everything the data package holds is kept, whatever a later version of the library adds; then the names are cut.
            foreach (var property in typeof(GameData).GetProperties().Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0))
                property.SetValue(copy, property.GetValue(data));
            copy.ItemLookup = CappedKeys(data.ItemLookup);
            copy.LocationLookup = CappedKeys(data.LocationLookup);
            return copy;
        }

        private static bool TooLong(Dictionary<string, long>? names) => names != null && names.Keys.Any(name => name.Length > MaxName);

        private static Dictionary<string, long> CappedKeys(Dictionary<string, long>? names)
        {
            var capped = new Dictionary<string, long>(names?.Count ?? 0);
            // Two cut names that came out the same (the same 491 characters, and the same hash) keep the first.
            if (names != null)
                foreach (var (name, id) in names)
                    capped.TryAdd(Cap(name), id);
            return capped;
        }
    }
}
