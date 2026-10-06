using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Godot;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>
    /// A map pack's images, decoded only while something uses the pack: a connected slot (its map and Key Items) or the
    /// Pack Doctor window. Reading a pack (the Map Packs tab, the Pack Doctor's checks, Properties) takes only its
    /// structure, so installed packs cost nothing until one is used. Images are most of a pack: opening the Map Packs tab
    /// with three large packs installed used to take 948 MB of textures.
    /// The pack released most recently keeps its images, so a slot that reconnects doesn't decode them again; older ones
    /// are freed at once. Thread-safe; decoding takes a while for a big pack, so it's done off the main thread.
    /// </summary>
    public static class PackImages
    {
        /// <summary>How many packs nothing uses keep their images: the most recently released.</summary>
        public const int KeptUnused = 1;

        private static readonly object _lock = new();
        private static readonly Dictionary<LoadedPack, int> _users = new();
        // Packs nothing uses that still have their images, the most recently released last.
        private static readonly List<LoadedPack> _kept = new();
        private static long _decoded;

        /// <summary>How many images have been decoded into textures since Atlas started (for tests).</summary>
        public static long Decoded => Interlocked.Read(ref _decoded);

        /// <summary>How many uses a pack has now (for tests).</summary>
        public static int UsersOf(LoadedPack pack)
        {
            lock (_lock) return _users.TryGetValue(pack, out int users) ? users : 0;
        }

        /// <summary>Whether an image file is one Atlas can decode (PNG, JPEG, WebP).</summary>
        public static bool CanDecode(string path) =>
            path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Uses a pack's images until the result is disposed, decoding them first if they aren't (not on the main thread:
        /// a big pack takes seconds). Disposing more than once is harmless.
        /// </summary>
        public static IDisposable Use(LoadedPack pack)
        {
            lock (_lock)
            {
                _users[pack] = _users.TryGetValue(pack, out int users) ? users + 1 : 1;
                _kept.Remove(pack);
            }
            lock (pack.ImageLock)
            {
                if (!pack.ImagesLoaded) Decode(pack, keep: true, new ImageBudget());
            }
            return new PackUse(pack);
        }

        /// <summary>
        /// Finds the pack's images that can't be decoded, for the Pack Doctor, without keeping any (no textures). Once per
        /// pack, and not needed once its images were decoded for use. Not on the main thread.
        /// </summary>
        public static void Check(LoadedPack pack) => Check(pack, new ImageBudget());

        /// <summary>As <see cref="Check(LoadedPack)"/>, within the given budget (a test's small one).</summary>
        internal static void Check(LoadedPack pack, ImageBudget budget)
        {
            lock (pack.ImageLock)
            {
                if (!pack.ImagesChecked) Decode(pack, keep: false, budget);
            }
        }

        private sealed class PackUse : IDisposable
        {
            private LoadedPack? _pack;

            public PackUse(LoadedPack pack) => _pack = pack;

            public void Dispose()
            {
                var pack = Interlocked.Exchange(ref _pack, null);
                if (pack != null) Release(pack);
            }
        }

        private static void Release(LoadedPack pack)
        {
            List<LoadedPack> free;
            lock (_lock)
            {
                if (!_users.TryGetValue(pack, out int users)) return;
                if (users > 1)
                {
                    _users[pack] = users - 1;
                    return;
                }
                _users.Remove(pack);
                _kept.Add(pack);
                int over = _kept.Count - KeptUnused;
                if (over <= 0) return;
                free = _kept.GetRange(0, over);
                _kept.RemoveRange(0, over);
            }
            foreach (var old in free) Free(old);
        }

        /// <summary>Frees a pack's images, unless it was used (or kept) again meanwhile.</summary>
        private static void Free(LoadedPack pack)
        {
            Dictionary<string, ImageTexture> images;
            lock (pack.ImageLock)
            {
                lock (_lock)
                {
                    if (_users.ContainsKey(pack) || _kept.Contains(pack)) return;
                }
                if (!pack.ImagesLoaded) return;
                images = pack.Images;
                pack.Images = new Dictionary<string, ImageTexture>(StringComparer.OrdinalIgnoreCase);
                foreach (var map in pack.Maps.Values) map.BackgroundTexture = null;
                pack.ImagesLoaded = false;
            }
            // Freed now, not whenever .NET next collects: a texture's memory is Godot's, which .NET doesn't see. A view
            // still showing one keeps it (Godot counts its own references), but nothing shows a pack nobody uses.
            foreach (var texture in images.Values.Distinct()) texture.Dispose();
        }

        // Under the pack's ImageLock. Reads each of the pack's images from its zip, and either keeps them as textures (and
        // links the maps' backgrounds) or only notes which can't be decoded. The same images are refused either way: the
        // limits (SafeZip's, and the ImageBudget's) are taken in the same order.
        private static void Decode(LoadedPack pack, bool keep, ImageBudget budget)
        {
            var images = new Dictionary<string, ImageTexture>(StringComparer.OrdinalIgnoreCase);
            var sizes = new Dictionary<string, Vector2I>(StringComparer.OrdinalIgnoreCase);
            var broken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var issues = new List<string>();
            bool firstLook = !pack.ImagesChecked;
            try
            {
                using var archive = SafeZip.Open(pack.SourcePath);
                // Each image is indexed under two paths ("/images/x.png" and "images/x.png"): decode it once.
                foreach (var image in pack.ImageEntries.GroupBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase))
                {
                    string localPath = image.Select(entry => entry.Key).FirstOrDefault(path => !path.StartsWith('/')) ?? image.First().Key.TrimStart('/');
                    var entry = archive.GetEntry(image.Key);
                    Image? decoded = null;
                    string? why = null;
                    if (entry != null)
                    {
                        // Too big to read, or damaged: this image is blank, and the rest are still read.
                        try { decoded = DecodeImage(archive.ReadImage(entry), localPath, budget, out why); }
                        catch (InvalidDataException ex) { why = ex.Message; }
                    }
                    if (decoded == null)
                    {
                        broken.Add(localPath);
                        issues.Add(entry == null ? $"Image '{localPath}' isn't in the pack's zip any more." : $"Image '{localPath}' couldn't be decoded ({why}).");
                        // Godot logs only a bare ERR_PARSE_ERROR; name the image (once per pack) so the user knows what's missing.
                        if (firstLook)
                            Logger.LogWarning($"Map pack '{Path.GetFileName(pack.SourcePath)}': could not decode image '{localPath}' ({why ?? "it isn't in the zip"}). Anything using it (e.g. a map background) will appear blank.");
                        continue;
                    }
                    foreach (var path in image.Select(entry => entry.Key)) sizes[path] = new Vector2I(decoded.GetWidth(), decoded.GetHeight());
                    if (!keep)
                    {
                        decoded.Dispose();
                        continue;
                    }
                    var texture = ImageTexture.CreateFromImage(decoded);
                    decoded.Dispose(); // the texture has its own copy
                    Interlocked.Increment(ref _decoded);
                    foreach (var path in image.Select(entry => entry.Key)) images[path] = texture;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // The zip was removed or replaced while Atlas used it: whatever decoded is used, the rest shows blank.
                issues.Add($"The pack's images couldn't be read ({ex.Message}).");
                if (firstLook) Logger.LogWarning($"Map pack '{Path.GetFileName(pack.SourcePath)}': couldn't read its images ({ex.Message}).");
            }

            if (firstLook)
            {
                // New collections, not changed ones: other threads may be reading the old.
                pack.BrokenImages = new HashSet<string>(pack.BrokenImages.Concat(broken), StringComparer.OrdinalIgnoreCase);
                pack.ImageSizes = sizes;
                pack.LoadIssues = pack.LoadIssues.Concat(issues).Distinct().ToList();
                pack.ImagesChecked = true;
            }
            if (!keep) return;
            pack.Images = images;
            foreach (var map in pack.Maps.Values)
                map.BackgroundTexture = pack.MapBackgroundPath(map) is { } path && images.TryGetValue(path, out var texture) ? texture : null;
            pack.ImagesLoaded = true;
        }

        /// <summary>
        /// Decodes an image file from outside Atlas (PNG, JPEG or WebP, by its name), or says why it can't be: a decoder sets
        /// aside width × height × 4 bytes from the header alone, so the size the header gives is taken from the budget first
        /// (see <see cref="ImageBudget"/>). Every image Atlas decodes from a pack, or from a file the user chose, comes here.
        /// </summary>
        internal static Image? DecodeImage(byte[] file, string path, ImageBudget budget, out string? why)
        {
            why = budget.Take(file);
            if (why != null) return null;
            var image = new Image();
            Error error;
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) error = image.LoadPngFromBuffer(file);
            else if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) error = image.LoadWebpFromBuffer(file);
            else error = image.LoadJpgFromBuffer(file);
            if (error == Error.Ok && !image.IsEmpty()) return image;
            why = error == Error.Ok ? "empty image" : error.ToString();
            image.Dispose();
            return null;
        }
    }
}
