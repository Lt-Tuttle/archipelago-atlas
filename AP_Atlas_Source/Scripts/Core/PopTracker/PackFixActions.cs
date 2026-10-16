#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>
    /// Every fix a user can make to a map pack, in one place: the Pack Doctor, the Map Tracker's and Key Items' "Fix a
    /// problem" menus and their right-click menus all call these, so a fix made anywhere is the same fix (stored by
    /// <see cref="PackFixes"/>, one undoable step each, stamped against the author's version so an update that changes the
    /// same thing wins). <paramref name="original"/> is always the author's pack as read from its zip, never the
    /// effective one (the stamps compare against the author's version).
    /// </summary>
    public static class PackFixActions
    {
        /// <summary>Links a pin section to a location, or (with no id) shows no location there.</summary>
        public static void LinkSection(LoadedPack original, string pinPath, string? sectionName, long? id, string? name)
        {
            string subject = $"link:{pinPath}|{sectionName}";
            PackFixes.Edit(PackFixes.KeyFor(original), id == null ? $"Unlink {sectionName}" : $"Link {sectionName} to {name}", f =>
            {
                f.Links.RemoveAll(x => x.Subject == subject);
                f.Links.Add(new LocationLinkFix
                {
                    Subject = subject,
                    PinPath = pinPath,
                    SectionName = sectionName ?? "",
                    ApLocationId = id,
                    ApLocationName = name,
                    AuthorStamp = PackFixes.AuthorStamp(original, subject)
                });
            });
        }

        /// <summary>
        /// A section that isn't a check (a marker, a label): shows no location, and the Doctor stops asking about it (its
        /// unmatched finding is ignored). One step: Undo brings both back.
        /// </summary>
        public static void NotACheck(LoadedPack original, string pinPath, string? sectionName)
        {
            string subject = $"link:{pinPath}|{sectionName}";
            PackFixes.Edit(PackFixes.KeyFor(original), $"'{sectionName}' isn't a check", f =>
            {
                f.Links.RemoveAll(x => x.Subject == subject);
                f.Links.Add(new LocationLinkFix { Subject = subject, PinPath = pinPath, SectionName = sectionName ?? "", AuthorStamp = PackFixes.AuthorStamp(original, subject) });
                foreach (string key in new[] { $"loc:unmatched:{pinPath}|{sectionName}", $"loc:untracked:{pinPath}|{sectionName}" })
                    if (!f.Ignored.Contains(key)) f.Ignored.Add(key);
            });
        }

        /// <summary>Links a Key Items tile to an item.</summary>
        public static void LinkTile(LoadedPack original, string code, long id, string name) =>
            PackFixes.Edit(PackFixes.KeyFor(original), $"Link tile '{code}' to {name}", f =>
            {
                var t = UpsertTile(original, f, code);
                t.ApItemId = id;
                t.ApItemName = name;
            });

        /// <summary>Hides or shows a Key Items tile.</summary>
        public static void SetTileHidden(LoadedPack original, string code, bool hidden) =>
            PackFixes.Edit(PackFixes.KeyFor(original), (hidden ? "Hide" : "Show") + $" tile '{code}'", f => UpsertTile(original, f, code).Hidden = hidden);

        /// <summary>Gives a tile a picture already stored in the pack's fix folder (<see cref="PackFixes.ImportImage"/>, <see cref="ImportPackImage"/>).</summary>
        public static void SetTileImage(LoadedPack original, string code, string storedFile) =>
            PackFixes.Edit(PackFixes.KeyFor(original), $"New image for tile '{code}'", f => UpsertTile(original, f, code).ImageFile = storedFile);

        /// <summary>Puts the tile's picture back to the author's (keeps its other fixes).</summary>
        public static void ResetTileImage(LoadedPack original, string code) =>
            PackFixes.Edit(PackFixes.KeyFor(original), $"The author's image for tile '{code}'", f =>
            {
                var t = f.Tiles.FirstOrDefault(x => x.Subject == "tile:" + code);
                if (t != null) t.ImageFile = null;
            });

        /// <summary>The tile fix for a code, made (and stamped) when there's none.</summary>
        public static TileFix UpsertTile(LoadedPack original, PackFixFile f, string code)
        {
            string subject = "tile:" + code;
            var t = f.Tiles.FirstOrDefault(x => x.Subject == subject);
            if (t == null)
            {
                t = new TileFix { Subject = subject, Code = code, AuthorStamp = PackFixes.AuthorStamp(original, subject) };
                f.Tiles.Add(t);
            }
            t.Made = DateTime.Now;
            // Whoever changes it now owns it: Atlas's own fix becomes the user's (PackBulk.Apply sets its own source after).
            t.Source = null;
            t.Reason = null;
            return t;
        }

        /// <summary>Adds a tile for an item the pack shows nowhere, to one of its item grids (by its index among the visible ones).</summary>
        public static void AddTile(LoadedPack original, long id, string name, int gridIndex) =>
            PackFixes.Edit(PackFixes.KeyFor(original), $"Add tile for {name}", f =>
            {
                f.AddedTiles.RemoveAll(a => a.ApItemId == id);
                f.AddedTiles.Add(new AddedTile { Subject = "added:item:" + id, ApItemId = id, ApItemName = name, GridIndex = Math.Max(0, gridIndex) });
            });

        /// <summary>Moves a pin on a map (an added pin is moved itself; the author's gets a position fix for that map).</summary>
        public static void MovePin(LoadedPack original, string pinPath, string pinName, string mapId, float x, float y, bool isAdded)
        {
            string key = PackFixes.KeyFor(original);
            if (isAdded)
            {
                PackFixes.Edit(key, $"Move added pin {pinName}", f =>
                {
                    var ap = f.AddedPins.FirstOrDefault(p => p.PinPath == pinPath);
                    if (ap != null) { ap.X = x; ap.Y = y; ap.MapId = mapId; ap.Made = DateTime.Now; }
                });
                return;
            }
            string subject = $"pin:{pinPath}@{mapId}";
            PackFixes.Edit(key, $"Move pin {pinName}", f =>
            {
                f.Pins.RemoveAll(p => p.Subject == subject);
                f.Pins.Add(new PinFix { Subject = subject, PinPath = pinPath, MapId = mapId, X = x, Y = y, AuthorStamp = PackFixes.AuthorStamp(original, subject) });
            });
        }

        /// <summary>Places a pin for a location at a point of a map (a pin Atlas adds, "atlas/&lt;name&gt;").</summary>
        public static void PlacePin(LoadedPack original, long id, string name, string mapId, float x, float y) =>
            PackFixes.Edit(PackFixes.KeyFor(original), $"Place a pin for {name}", f =>
            {
                f.AddedPins.RemoveAll(p => p.Subject == "added:pin:" + id);
                f.AddedPins.Add(new AddedPin { Subject = "added:pin:" + id, Name = name, MapId = mapId, X = x, Y = y, ApLocationIds = new List<long> { id } });
            });

        /// <summary>Removes a pin from a map: an added one is deleted, the author's is hidden there (Reset brings it back).</summary>
        public static void RemovePin(LoadedPack original, string pinPath, string pinName, string mapId, bool isAdded)
        {
            string key = PackFixes.KeyFor(original);
            if (isAdded)
            {
                PackFixes.Edit(key, $"Delete added pin {pinName}", f => f.AddedPins.RemoveAll(p => p.PinPath == pinPath));
                return;
            }
            string subject = $"pin:{pinPath}@{mapId}";
            PackFixes.Edit(key, $"Remove pin {pinName}", f =>
            {
                f.Pins.RemoveAll(p => p.Subject == subject);
                f.Pins.Add(new PinFix { Subject = subject, PinPath = pinPath, MapId = mapId, Removed = true, AuthorStamp = PackFixes.AuthorStamp(original, subject) });
            });
        }

        /// <summary>Gives a map a picture already stored in the pack's fix folder.</summary>
        public static void SetMapImage(LoadedPack original, string mapId, string storedFile)
        {
            string subject = "map:" + mapId;
            PackFixes.Edit(PackFixes.KeyFor(original), $"New picture for map '{mapId}'", f =>
            {
                f.MapImages.RemoveAll(x => x.Subject == subject);
                f.MapImages.Add(new MapImageFix { Subject = subject, MapId = mapId, ImageFile = storedFile, AuthorStamp = PackFixes.AuthorStamp(original, subject) });
            });
        }

        /// <summary>Puts a map's picture back to the author's.</summary>
        public static void ResetMapImage(LoadedPack original, string mapId) => PackFixes.Reset(PackFixes.KeyFor(original), "maps", "map:" + mapId);

        /// <summary>Whether a fix gave this map a picture of its own.</summary>
        public static bool HasMapImageFix(LoadedPack original, string mapId) =>
            PackFixes.Get(PackFixes.KeyFor(original)).MapImages.Any(m => m.Subject == "map:" + mapId);

        /// <summary>Whether a fix gave this tile a picture of its own.</summary>
        public static bool HasTileImageFix(LoadedPack original, string code) =>
            PackFixes.Get(PackFixes.KeyFor(original)).Tiles.Any(t => t.Subject == "tile:" + code && !string.IsNullOrEmpty(t.ImageFile));

        // =====================================================================
        // The pack's own pictures
        // =====================================================================

        /// <summary>
        /// Copies one of the pack's own pictures into its fix folder (the pack's zip is never written) and returns the stored
        /// name, for <see cref="SetTileImage"/> or <see cref="SetMapImage"/>. Refused (with why) when it can't be read or
        /// shown, as a pack's images are.
        /// </summary>
        public static string ImportPackImage(LoadedPack original, string imagePath)
        {
            string? entryName = EntryOf(original, imagePath) ?? throw new InvalidDataException($"the pack has no picture '{imagePath}'");
            byte[] bytes;
            using (var zip = SafeZip.Open(original.SourcePath))
            {
                var entry = zip.GetEntry(entryName) ?? throw new InvalidDataException($"'{imagePath}' isn't in the pack's zip any more");
                bytes = zip.ReadImage(entry);
            }
            if (new ImageBudget().Take(bytes) is string why) throw new InvalidDataException(why);
            string dir = PackFixes.AssetDir(PackFixes.KeyFor(original));
            Directory.CreateDirectory(dir);
            string name = Path.GetFileNameWithoutExtension(imagePath) + "_" + DateTime.Now.ToString("yyyyMMddHHmmss") + Path.GetExtension(imagePath).ToLowerInvariant();
            SafeFile.WriteAllBytes(Path.Combine(dir, name), bytes);
            return name;
        }

        /// <summary>The zip entry a picture path names (as items and maps write paths), or null.</summary>
        private static string? EntryOf(LoadedPack pack, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.Replace("\\", "/");
            foreach (string candidate in new[] { p, "/" + p.TrimStart('/'), p.TrimStart('/') })
                if (pack.ImageEntries.TryGetValue(candidate, out var entry)) return entry;
            return null;
        }

        /// <summary>Every picture the pack has that decodes (one path each, without a leading slash), sorted by path.</summary>
        public static List<string> Pictures(LoadedPack pack) =>
            pack.ImageEntries.Keys.Select(k => k.TrimStart('/')).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(k => !pack.BrokenImages.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// The pack's pictures ordered by how well their file names match a name ("Deku Shield" finds
        /// "images/items/deku_shield_item_only.png" first), then the rest by path.
        /// </summary>
        public static List<string> PicturesFor(LoadedPack pack, string? name)
        {
            var all = Pictures(pack);
            string wanted = Squash(name);
            if (wanted.Length == 0) return all;
            int Rank(string path)
            {
                string file = Squash(Path.GetFileNameWithoutExtension(path));
                if (file == wanted) return 0;
                if (file.StartsWith(wanted, StringComparison.Ordinal)) return 1;
                if (file.Contains(wanted, StringComparison.Ordinal) || (file.Length >= 3 && wanted.Contains(file, StringComparison.Ordinal))) return 2;
                return Squash(path).Contains(wanted, StringComparison.Ordinal) ? 3 : 9;
            }
            return all.Select(p => (Path: p, Rank: Rank(p))).OrderBy(x => x.Rank).ThenBy(x => x.Path.Length).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(x => x.Path).ToList();
        }

        /// <summary>
        /// Pictures that are likely a map's own when its named one is missing: the named path with another case, extension or
        /// folder, or a picture named like the map. Best first; empty when there's none.
        /// </summary>
        public static List<string> MapPictureCandidates(LoadedPack pack, PopTrackerMap map)
        {
            var found = new List<string>();
            var all = Pictures(pack);
            string named = Squash(Path.GetFileNameWithoutExtension(map.MapBg ?? ""));
            if (named.Length == 0) named = Squash(Path.GetFileNameWithoutExtension(map.Img ?? ""));
            foreach (string wanted in new[] { named, Squash(map.Name), Squash(map.Id) }.Where(w => w.Length >= 3).Distinct())
                foreach (string p in all)
                    if (Squash(Path.GetFileNameWithoutExtension(p)) == wanted && !found.Contains(p)) found.Add(p);
            return found;
        }

        private static string Squash(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
