#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>What a shared fixes file says about itself: its format, and the pack it was made for.</summary>
    public sealed class FixShareManifest
    {
        public string Format { get; set; } = "";
        public string PackName { get; set; } = "";
        public string PackVersion { get; set; } = "";
        public string? PackSha256 { get; set; }
        public DateTime Made { get; set; }
    }

    /// <summary>A shared fixes file, read: its manifest, the fixes, and the pictures they use (by file name).</summary>
    public sealed class SharedFixes
    {
        public FixShareManifest Manifest { get; init; } = new();
        public PackFixFile Fixes { get; init; } = new();
        public Dictionary<string, byte[]> Images { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One fix of a shared file, as importing it would treat it: what it's about, the file's, and the user's own (null: none).</summary>
    public sealed record ImportRow(string Kind, string Subject, string Theirs, string? Yours)
    {
        /// <summary>The row's key for the "use theirs" ticks.</summary>
        public string Key => Kind + "|" + Subject;
    }

    /// <summary>What importing a shared file does: fixes it adds, ones the user has already, ones where both differ (the user's stays unless ticked), and findings it sets aside.</summary>
    public sealed class ImportPlan
    {
        public List<ImportRow> Adds { get; } = new();
        public List<ImportRow> Same { get; } = new();
        public List<ImportRow> Clashes { get; } = new();
        public List<string> Ignored { get; } = new();
        /// <summary>Why the file may not fit this pack exactly (another version), or null.</summary>
        public string? Warning { get; set; }
        public bool Nothing => Adds.Count == 0 && Clashes.Count == 0 && Ignored.Count == 0;
    }

    /// <summary>
    /// A pack's fixes as one file to share (the owner's choice, 2026-10-14): a zip with the fixes, the pictures they use and
    /// the pack they were made for. Importing previews what it adds, and where both people fixed the same thing the user's
    /// own fix stays unless they tick the file's; one undo step. Read through SafeZip's limits; pictures are checked like
    /// any other before they're kept.
    /// </summary>
    public static class PackFixShare
    {
        public const string Format = "atlas-pack-fixes/1";
        public const string ManifestEntry = "manifest.json";
        public const string FixesEntry = "fixes.json";
        public const string ImagesFolder = "images/";

        private static readonly JsonSerializerSettings Json = new() { MaxDepth = 64 };

        /// <summary>The pack's fixes as a zip's bytes (to be saved where the user chose).</summary>
        public static byte[] Export(LoadedPack original)
        {
            string key = PackFixes.KeyFor(original);
            var fixes = Clone(PackFixes.Get(key));
            fixes.Superseded.Clear(); // the user's own business: set aside by their pack's updates
            var manifest = new FixShareManifest
            {
                Format = Format,
                PackName = original.Manifest?.Name ?? key,
                PackVersion = original.Manifest?.GetActualVersion() ?? "",
                PackSha256 = TryHash(original.SourcePath),
                Made = DateTime.Now, // wall clock: shown only
            };
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                Write(zip, ManifestEntry, JsonConvert.SerializeObject(manifest, Formatting.Indented, Json));
                Write(zip, FixesEntry, JsonConvert.SerializeObject(fixes, Formatting.Indented, Json));
                foreach (string name in ImagesOf(fixes).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string path = Path.Combine(PackFixes.AssetDir(key), name);
                    if (!File.Exists(path)) continue;
                    string entryName = ImagesFolder + Path.GetFileName(name);
                    using var entry = zip.CreateEntry(entryName).Open();
                    byte[] bytes = File.ReadAllBytes(path);
                    entry.Write(bytes, 0, bytes.Length);
                }
            }
            return stream.ToArray();
        }

        private static void Write(ZipArchive zip, string name, string text)
        {
            using var entry = zip.CreateEntry(name).Open();
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            entry.Write(bytes, 0, bytes.Length);
        }

        private static string? TryHash(string? path)
        {
            try { return string.IsNullOrEmpty(path) ? null : AP_Atlas.Core.EngineSetup.ApworldSources.Sha256OfPack(path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>Reads a shared fixes file. Throws <see cref="InvalidDataException"/> when it isn't one (or a picture in it isn't one).</summary>
        public static SharedFixes Read(string zipPath)
        {
            using var zip = SafeZip.Open(zipPath);
            var manifestEntry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("it isn't a fixes file of Atlas's (no manifest.json)");
            var manifest = JsonConvert.DeserializeObject<FixShareManifest>(zip.ReadText(manifestEntry), Json) ?? throw new InvalidDataException("its manifest can't be read");
            if (manifest.Format != Format) throw new InvalidDataException($"it's a format this Atlas doesn't read ({manifest.Format})");
            var fixesEntry = zip.GetEntry(FixesEntry) ?? throw new InvalidDataException("it has no fixes.json");
            var fixes = JsonConvert.DeserializeObject<PackFixFile>(zip.ReadText(fixesEntry), Json) ?? throw new InvalidDataException("its fixes can't be read");
            var images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var budget = new ImageBudget();
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(ImagesFolder, StringComparison.Ordinal) && e.Name.Length > 0))
            {
                byte[] bytes = zip.ReadImage(entry);
                if (budget.Take(bytes) is string why) throw new InvalidDataException($"its picture {entry.Name} isn't one Atlas takes: {why}");
                images[entry.Name] = bytes;
            }
            return new SharedFixes { Manifest = manifest, Fixes = fixes, Images = images };
        }

        /// <summary>What importing a file into the pack would do. Throws <see cref="InvalidDataException"/> for a file made for another pack.</summary>
        public static ImportPlan Plan(LoadedPack original, SharedFixes shared)
        {
            string name = original.Manifest?.Name ?? "";
            if (!string.Equals(shared.Manifest.PackName, name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"these fixes are for the pack \"{shared.Manifest.PackName}\", not \"{name}\"");
            var plan = new ImportPlan();
            string version = original.Manifest?.GetActualVersion() ?? "";
            if (!string.Equals(shared.Manifest.PackVersion, version, StringComparison.OrdinalIgnoreCase))
                plan.Warning = $"These fixes were made for version {shared.Manifest.PackVersion} of the pack; you have {version}. Any fix of something the author changed since is set aside on the next check (restorable under Your fixes).";
            var mine = PackFixes.Get(PackFixes.KeyFor(original));
            foreach (var (kind, theirs, ours) in Lists(shared.Fixes, mine))
                foreach (var fix in theirs)
                {
                    var own = ours.FirstOrDefault(x => x.Subject == fix.Subject);
                    var row = new ImportRow(kind, fix.Subject, Describe(fix), own == null ? null : Describe(own));
                    if (own == null) plan.Adds.Add(row);
                    else if (row.Theirs == row.Yours) plan.Same.Add(row);
                    else plan.Clashes.Add(row);
                }
            plan.Ignored.AddRange(shared.Fixes.Ignored.Where(k => !mine.Ignored.Contains(k)).Distinct());
            return plan;
        }

        /// <summary>
        /// Imports a file as one undoable step: everything it adds, and the clashes whose keys are in <paramref name="takeTheirs"/>
        /// (the user's own fixes stay for the rest); the pictures they use are kept in the pack's fix folder.
        /// </summary>
        public static void Apply(LoadedPack original, SharedFixes shared, ImportPlan plan, ISet<string> takeTheirs)
        {
            string key = PackFixes.KeyFor(original);
            var take = new HashSet<string>(plan.Adds.Select(r => r.Key).Concat(plan.Clashes.Where(r => takeTheirs.Contains(r.Key)).Select(r => r.Key)), StringComparer.Ordinal);
            var incoming = Clone(shared.Fixes);
            // The pictures first (each under a new name), so the fixes can name them.
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string dir = PackFixes.AssetDir(key);
            foreach (var (kind, theirs, _) in Lists(incoming, new PackFixFile()))
                foreach (var fix in theirs.Where(f => take.Contains(kind + "|" + f.Subject)))
                    foreach (string image in ImagesOf(fix))
                    {
                        string leaf = Path.GetFileName(image);
                        if (renamed.ContainsKey(leaf) || !shared.Images.TryGetValue(leaf, out var bytes)) continue;
                        Directory.CreateDirectory(dir);
                        string stored = Path.GetFileNameWithoutExtension(leaf) + "_shared_" + DateTime.Now.ToString("yyyyMMddHHmmss") + Path.GetExtension(leaf).ToLowerInvariant(); // wall clock: a file's name
                        SafeFile.WriteAllBytes(Path.Combine(dir, stored), bytes);
                        renamed[leaf] = stored;
                    }
            PackFixes.Edit(key, $"Import {take.Count} fix{(take.Count == 1 ? "" : "es")} from a shared file", file =>
            {
                foreach (var (kind, theirs, ours) in Lists(incoming, file))
                    foreach (var fix in theirs.Where(f => take.Contains(kind + "|" + f.Subject)))
                    {
                        Rename(fix, renamed);
                        fix.Made = DateTime.Now;
                        ours.RemoveAll(x => x.Subject == fix.Subject);
                        ours.Add(fix);
                    }
                foreach (string ignored in plan.Ignored)
                    if (!file.Ignored.Contains(ignored)) file.Ignored.Add(ignored);
            });
        }

        // ---- The fix lists, side by side ----

        /// <summary>Each kind of fix: its name, the file's list and the user's (the user's as the real list, to change).</summary>
        private static IEnumerable<(string Kind, List<PackFixBase> Theirs, ListProxy Ours)> Lists(PackFixFile theirs, PackFixFile ours)
        {
            yield return ("tiles", theirs.Tiles.Cast<PackFixBase>().ToList(), new ListProxy(ours.Tiles));
            yield return ("added tiles", theirs.AddedTiles.Cast<PackFixBase>().ToList(), new ListProxy(ours.AddedTiles));
            yield return ("grids", theirs.Grids.Cast<PackFixBase>().ToList(), new ListProxy(ours.Grids));
            yield return ("links", theirs.Links.Cast<PackFixBase>().ToList(), new ListProxy(ours.Links));
            yield return ("pins", theirs.Pins.Cast<PackFixBase>().ToList(), new ListProxy(ours.Pins));
            yield return ("added pins", theirs.AddedPins.Cast<PackFixBase>().ToList(), new ListProxy(ours.AddedPins));
            yield return ("map pictures", theirs.MapImages.Cast<PackFixBase>().ToList(), new ListProxy(ours.MapImages));
        }

        /// <summary>A typed fix list seen as fixes of any kind (to find, remove and add).</summary>
        private sealed class ListProxy
        {
            private readonly System.Collections.IList _list;
            public ListProxy(System.Collections.IList list) => _list = list;
            public PackFixBase? FirstOrDefault(Func<PackFixBase, bool> match) => _list.Cast<PackFixBase>().FirstOrDefault(match);
            public void RemoveAll(Func<PackFixBase, bool> match)
            {
                foreach (var item in _list.Cast<PackFixBase>().Where(match).ToList()) _list.Remove(item);
            }
            public void Add(PackFixBase fix) => _list.Add(fix);
        }

        private static IEnumerable<string> ImagesOf(PackFixFile fixes) =>
            fixes.Tiles.Select(t => t.ImageFile).Concat(fixes.AddedTiles.Select(t => t.ImageFile)).Concat(fixes.MapImages.Select(m => m.ImageFile))
                .Where(n => !string.IsNullOrEmpty(n))!;

        private static IEnumerable<string> ImagesOf(PackFixBase fix) => fix switch
        {
            TileFix { ImageFile: { Length: > 0 } image } => new[] { image },
            AddedTile { ImageFile: { Length: > 0 } image } => new[] { image },
            MapImageFix { ImageFile: { Length: > 0 } image } => new[] { image },
            _ => Array.Empty<string>(),
        };

        /// <summary>A fix's picture under its kept name (a picture the file doesn't hold: none).</summary>
        private static void Rename(PackFixBase fix, Dictionary<string, string> renamed)
        {
            string? Name(string? image) => string.IsNullOrEmpty(image) ? image : renamed.TryGetValue(Path.GetFileName(image), out var kept) ? kept : null;
            switch (fix)
            {
                case TileFix tile: tile.ImageFile = Name(tile.ImageFile); break;
                case AddedTile added: added.ImageFile = Name(added.ImageFile); break;
                case MapImageFix map: map.ImageFile = Name(map.ImageFile) ?? ""; break;
            }
        }

        /// <summary>A fix in words (what importing shows, and how two fixes are told apart).</summary>
        public static string Describe(PackFixBase fix) => fix switch
        {
            TileFix t => $"Tile '{t.Code}': " + string.Join(", ", new[] { t.ApItemId != null ? $"tracks {t.ApItemName}" : null, t.Hidden ? "hidden" : null, string.IsNullOrEmpty(t.ImageFile) ? null : "a picture" }.Where(x => x != null)),
            AddedTile a => $"Added tile for {a.ApItemName}",
            GridFix g => $"Grid {g.Subject}: " + string.Join(", ", new[] { g.Hidden == true ? "hidden" : g.Hidden == false ? "shown" : null, g.ItemSize != null ? $"tile size {g.ItemSize}" : null, g.Rows != null ? "reordered" : null }.Where(x => x != null)),
            LocationLinkFix l => $"{l.PinPath} / {(string.IsNullOrEmpty(l.SectionName) ? "(pin)" : l.SectionName)} → {(l.ApLocationId == null ? "nothing" : l.ApLocationName)}",
            PinFix p => p.Removed ? $"Removed pin {p.PinPath}" : $"Moved pin {p.PinPath} on {p.MapId} to {p.X:0},{p.Y:0}",
            AddedPin p => $"Added pin '{p.Name}' on {p.MapId} at {p.X:0},{p.Y:0}",
            MapImageFix m => $"Map '{m.MapId}' has a picture",
            _ => fix.Subject,
        };

        private static PackFixFile Clone(PackFixFile file) =>
            JsonConvert.DeserializeObject<PackFixFile>(JsonConvert.SerializeObject(file, Json), Json) ?? new PackFixFile();
    }
}
