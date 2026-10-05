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

        // Files another program kept Atlas from reading. Their real contents are still on disk, so Atlas never writes
        // over them (what it has in memory is only the fallback) until a later read succeeds.
        private static readonly ConcurrentDictionary<string, bool> _unreadable = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised (any thread) when a damaged file was recovered from its backup or reset: (file, what happened).</summary>
        public static event Action<string, string> Recovered;

        private static object LockFor(string path) => _locks.GetOrAdd(Path.GetFullPath(path), _ => new object());

        /// <summary>Writes text atomically, keeping the previous version as path.bak. Retries briefly if the file is locked.</summary>
        public static void WriteAllText(string path, string content)
        {
            RefuseIfUnread(path);
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
            RefuseIfUnread(path);
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
        /// Deletes a file Atlas saved, together with its backup and any temp file an interrupted save left, so it can't
        /// come back from the backup on the next read. Retries briefly while another program holds a file; throws if it
        /// still can't be deleted (the file itself is then intact).
        /// </summary>
        public static void Delete(string path)
        {
            lock (LockFor(path))
            {
                // The backup goes first: if the file itself then can't be deleted, it's still whole.
                Retry(() => File.Delete(path + ".tmp"));
                Retry(() => File.Delete(path + ".bak"));
                Retry(() => File.Delete(path));
                _unreadable.TryRemove(Path.GetFullPath(path), out _);
            }
        }

        /// <summary>
        /// Reads JSON. Missing → fallback. Damaged → the damaged file is kept aside, the .bak is used if it reads, else
        /// fallback. Held by another program (an antivirus or sync tool) → read again for about a second and a half;
        /// if it still can't be read, it isn't damaged: it's left as it is, never written over this session, and the
        /// fallback is used. Every recovery is logged and raises Recovered. Never throws.
        /// </summary>
        public static T ReadJson<T>(string path, Func<T> fallback, JsonSerializerSettings settings = null) where T : class
        {
            lock (LockFor(path))
            {
                if (!File.Exists(path))
                {
                    // A crash between writing the temp file and swapping it in leaves only path.tmp / path.bak.
                    var orphan = TryParse<T>(path + ".bak", out _, out _, settings);
                    if (orphan != null)
                    {
                        Report(path, "was missing; restored the last good copy");
                        try { File.Copy(path + ".bak", path, true); }
                        catch (Exception ex) { Logger.LogWarning($"Couldn't put back {Path.GetFileName(path)} from its backup: {ex.Message}"); }
                        return orphan;
                    }
                    return fallback();
                }
                T value = null;
                string error = null;
                bool held = false;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    // 100, 200, 400 and 800 ms between tries: about a second and a half in all.
                    if (attempt > 0) Thread.Sleep(50 << attempt);
                    value = TryParse<T>(path, out error, out held, settings);
                    if (!held) break;
                }
                if (value != null)
                {
                    _unreadable.TryRemove(Path.GetFullPath(path), out _);
                    return value;
                }
                if (held)
                {
                    _unreadable[Path.GetFullPath(path)] = true;
                    Report(path, $"couldn't be read because another program is using it ({error}). Atlas carries on without it and won't save over it; restart Atlas once it's free");
                    return fallback();
                }

                string kept = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(path, kept, true); } catch { kept = null; }
                var backup = TryParse<T>(path + ".bak", out _, out _, settings);
                if (backup != null)
                {
                    try { File.Copy(path + ".bak", path, true); }
                    catch (Exception ex) { Logger.LogWarning($"Couldn't put back {Path.GetFileName(path)} from its backup (it's read from the backup until then): {ex.Message}"); }
                    Report(path, $"was damaged ({error}); restored the last good copy" + (kept != null ? $" (the damaged file is kept as {Path.GetFileName(kept)})" : ""));
                    return backup;
                }
                Report(path, $"was damaged ({error}) and had no usable backup; starting fresh" + (kept != null ? $" (the damaged file is kept as {Path.GetFileName(kept)})" : ""));
                return fallback();
            }
        }

        /// <param name="held">The file is there but couldn't be opened (locked, or access denied), so its contents are unknown.</param>
        private static T TryParse<T>(string path, out string error, out bool held, JsonSerializerSettings settings) where T : class
        {
            error = null;
            held = false;
            try
            {
                if (!File.Exists(path)) { error = "missing"; return null; }
                string text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text) || text.Contains('\0')) { error = "empty or zero-filled"; return null; }
                var value = settings == null ? JsonConvert.DeserializeObject<T>(text) : JsonConvert.DeserializeObject<T>(text, settings);
                if (value == null) error = "empty";
                return value;
            }
            catch (Exception ex)
            {
                held = (ex is IOException && ex is not FileNotFoundException && ex is not DirectoryNotFoundException) || ex is UnauthorizedAccessException;
                error = ex.Message.Length > 120 ? ex.Message.Substring(0, 120) + "…" : ex.Message;
                return null;
            }
        }

        private static void RefuseIfUnread(string path)
        {
            if (_unreadable.ContainsKey(Path.GetFullPath(path)))
                throw new IOException($"Atlas couldn't read {Path.GetFileName(path)} earlier (another program was using it), so it won't save over it. Restart Atlas once it's free");
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
