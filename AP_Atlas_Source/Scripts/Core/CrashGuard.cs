#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core
{
    /// <summary>
    /// Last-line protection: records any exception nothing else caught (with a crash report in PortableData/logs),
    /// and keeps a second copy of Atlas from running on the same data folder, where two writers would fight over
    /// the same files and open duplicate server connections.
    /// </summary>
    public static class CrashGuard
    {
        private static bool _installed;
        private static Mutex _instanceMutex;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Record("Unhandled exception" + (e.IsTerminating ? " (Atlas is closing)" : ""), e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                if (AP_Atlas.Core.Connections.LibraryLeftovers.IsAbandonedConnect(e.Exception))
                {
                    // Not a crash: a connection attempt the library gave up on (already reported as failed) ended later.
                    Logger.LogDebug("The connection library's abandoned connection attempt ended: " + e.Exception.InnerException?.Message);
                    e.SetObserved();
                    return;
                }
                Record("A background task failed without being checked", e.Exception);
                e.SetObserved();
            };
        }

        /// <summary>Logs an exception and writes a crash report file. Never throws.</summary>
        public static void Record(string what, Exception ex)
        {
            try { Logger.LogError($"{what}: {ex?.GetType().Name}: {ex?.Message}"); } catch { } // logging failed too: the report file below still records it
            try
            {
                string dir = Path.Combine(DataManager.GetDataDirectory(), "logs");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt");
                File.WriteAllText(file, $"{DateTime.Now:O}\n{what}\n\n{ex}\n");
                // Keep the folder from growing without bound.
                var reports = new DirectoryInfo(dir).GetFiles("crash_*.txt");
                Array.Sort(reports, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = 30; i < reports.Length; i++)
                {
                    try { reports[i].Delete(); }
                    catch { } // one that can't be deleted now goes after the next crash
                }
            }
            catch { } // the last resort: there's nowhere left to report that the report itself failed
        }

        /// <summary>
        /// Gives the data folder up before Atlas exits, so the Atlas it starts in its place (an update) can take it. On the
        /// thread that took it.
        /// </summary>
        public static void ReleaseInstance()
        {
            var mutex = _instanceMutex;
            _instanceMutex = null;
            if (mutex == null) return;
            try { mutex.ReleaseMutex(); }
            catch (Exception ex) when (ex is ApplicationException or ObjectDisposedException)
            {
                // Not held (another Atlas had it): nothing to give up.
            }
            mutex.Dispose();
        }

        /// <summary>
        /// The other Atlas processes started from this program file, with whether each has a window and its age (for
        /// <see cref="InstanceCheck.LeftoversToEnd"/>). Only processes of this exact file: another program, or another
        /// Atlas folder, is never listed. A process that can't be looked at is left out.
        /// </summary>
        public static List<InstanceCheck.Twin> Twins()
        {
            var twins = new List<InstanceCheck.Twin>();
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return twins;
            using var self = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(self.ProcessName))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == self.Id || !string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                        twins.Add(new InstanceCheck.Twin(process.Id, process.MainWindowHandle != IntPtr.Zero, DateTime.Now - process.StartTime)); // wall clock: Windows gives a process's start only as a local time
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        // It ended meanwhile, or Windows won't show it to this user: not one to offer.
                    }
                }
            }
            return twins;
        }

        /// <summary>Ends the leftover processes (and what they started), then takes the data folder; false when it's still held.</summary>
        public static bool EndLeftoversAndAcquire(IReadOnlyList<int> ids)
        {
            foreach (int id in ids)
            {
                try
                {
                    using var process = Process.GetProcessById(id);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    Logger.LogWarning($"Ended an earlier Atlas (process {id}) that had no window and still held the data folder.");
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Gone already (fine), or not ours to end (the folder stays held and the caller says so).
                    Logger.LogWarning($"Couldn't end the earlier Atlas (process {id}): {ex.Message}");
                }
            }
            _instanceMutex?.Dispose();
            _instanceMutex = null;
            return TryAcquireInstance();
        }

        /// <summary>True when this is the only Atlas using its data folder (held until Atlas exits).</summary>
        public static bool TryAcquireInstance()
        {
            try
            {
                string dir = Path.GetFullPath(DataManager.GetDataDirectory()).ToLowerInvariant();
                string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir)))[..16];
                _instanceMutex = new Mutex(true, @"Local\AP_Atlas_" + id, out bool created);
                if (created) return true;
                // An instance that crashed leaves an abandoned mutex: taking it over is fine.
                try { return _instanceMutex.WaitOne(0); }
                catch (AbandonedMutexException) { return true; }
            }
            catch (Exception ex)
            {
                // If the OS won't give us a mutex, don't block startup over it.
                try { Logger.LogWarning("Couldn't check for another running Atlas: " + ex.Message); } catch { } // logging failed: startup goes on without the warning
                return true;
            }
        }
    }
}
