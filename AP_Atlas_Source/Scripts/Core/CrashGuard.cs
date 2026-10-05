using System;
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
                Record("A background task failed without being checked", e.Exception);
                e.SetObserved();
            };
        }

        /// <summary>Logs an exception and writes a crash report file. Never throws.</summary>
        public static void Record(string what, Exception ex)
        {
            try { Logger.LogError($"{what}: {ex?.GetType().Name}: {ex?.Message}"); } catch { }
            try
            {
                string dir = Path.Combine(DataManager.GetDataDirectory(), "logs");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt");
                File.WriteAllText(file, $"{DateTime.Now:O}\n{what}\n\n{ex}\n");
                // Keep the folder from growing without bound.
                var reports = new DirectoryInfo(dir).GetFiles("crash_*.txt");
                Array.Sort(reports, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = 30; i < reports.Length; i++) { try { reports[i].Delete(); } catch { } }
            }
            catch { } // the last resort: there's nowhere left to report that the report itself failed
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
                try { Logger.LogWarning("Couldn't check for another running Atlas: " + ex.Message); } catch { }
                return true;
            }
        }
    }
}
