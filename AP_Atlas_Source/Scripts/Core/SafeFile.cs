using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace AP_Atlas.Core
{
    /// <summary>
    /// Crash-safe persistence for every file Atlas saves. A write goes to a temp file that is flushed to disk, then
    /// atomically replaces the real file while the previous version is kept as ".bak". A read that finds the file
    /// damaged sets it aside as ".corrupt-…", recovers the ".bak", and reports it, so a crash, power loss, full disk
    /// or a sync tool touching the file mid-write never loses a user's profiles, settings, notes or fixes.
    /// </summary>
    public static class SafeFile
    {
        private static readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised (any thread) when a damaged file was recovered from its backup or reset: (file, what happened).</summary>
        public static event Action<string, string> Recovered;

        private static object LockFor(string path) => _locks.GetOrAdd(Path.GetFullPath(path), _ => new object());

        /// <summary>Writes text atomically, keeping the previous version as path.bak. Retries briefly if the file is locked.</summary>
        public static void WriteAllText(string path, string content)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (LockFor(path))
            {
                string tmp = path + ".tmp";
                Retry(() =>
                {
                    using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                    {
                        writer.Write(content);
                        writer.Flush();
                        stream.Flush(true);
                    }
                    if (File.Exists(path))
                    {
                        try { File.Replace(tmp, path, path + ".bak", true); }
                        catch (PlatformNotSupportedException) { File.Copy(path, path + ".bak", true); File.Move(tmp, path, true); }
                    }
                    else File.Move(tmp, path);
                });
            }
        }

        public static void WriteJson<T>(string path, T value, Formatting formatting = Formatting.Indented) =>
            WriteAllText(path, JsonConvert.SerializeObject(value, formatting));

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (LockFor(path))
            {
                string tmp = path + ".tmp";
                Retry(() =>
                {
                    using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    File.Move(tmp, path, true);
                });
            }
        }

        /// <summary>
        /// Reads JSON. Missing → fallback. Damaged or unreadable → the damaged file is kept aside, the .bak is used if
        /// it reads, else fallback; either way it's logged and Recovered is raised. Never throws.
        /// </summary>
        public static T ReadJson<T>(string path, Func<T> fallback) where T : class
        {
            lock (LockFor(path))
            {
                if (!File.Exists(path))
                {
                    // A crash between writing the temp file and swapping it in leaves only path.tmp / path.bak.
                    var orphan = TryParse<T>(path + ".bak", out _);
                    if (orphan != null)
                    {
                        Report(path, "was missing; restored the last good copy");
                        try { File.Copy(path + ".bak", path, true); } catch { }
                        return orphan;
                    }
                    return fallback();
                }
                var value = TryParse<T>(path, out string error);
                if (value != null) return value;

                string kept = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(path, kept, true); } catch { kept = null; }
                var backup = TryParse<T>(path + ".bak", out _);
                if (backup != null)
                {
                    try { File.Copy(path + ".bak", path, true); } catch { }
                    Report(path, $"was damaged ({error}); restored the last good copy" + (kept != null ? $" (the damaged file is kept as {Path.GetFileName(kept)})" : ""));
                    return backup;
                }
                Report(path, $"was damaged ({error}) and had no usable backup; starting fresh" + (kept != null ? $" (the damaged file is kept as {Path.GetFileName(kept)})" : ""));
                return fallback();
            }
        }

        private static T TryParse<T>(string path, out string error) where T : class
        {
            error = null;
            try
            {
                if (!File.Exists(path)) { error = "missing"; return null; }
                string text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text) || text.Contains('\0')) { error = "empty or zero-filled"; return null; }
                var value = JsonConvert.DeserializeObject<T>(text);
                if (value == null) error = "empty";
                return value;
            }
            catch (Exception ex)
            {
                error = ex.Message.Length > 120 ? ex.Message.Substring(0, 120) + "…" : ex.Message;
                return null;
            }
        }

        private static void Report(string path, string what)
        {
            try { Logger.LogWarning($"{Path.GetFileName(path)} {what}."); } catch { }
            try { Recovered?.Invoke(path, what); } catch { }
        }

        private static void Retry(Action action)
        {
            // Antivirus scanners and sync tools (OneDrive, Dropbox) briefly lock files they're reading.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
        }
    }
}
