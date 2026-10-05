#nullable disable
using System;
using System.IO;

namespace AP_Atlas.Core
{
    public static class Logger
    {
        private static string _logFilePath;
        private static readonly object _fileLock = new object();
        /// <summary>A line for the window's logs (BBCode, level). Raised on whichever thread logged it.</summary>
        public static event Action<string, string> OnLogMessage;

        /// <summary>The time shown on log lines in Atlas's window. The visual check fixes it, so its pictures don't change with the clock.</summary>
        public static Func<DateTime> DisplayClock { get; set; } = () => DateTime.Now;

        /// <summary>
        /// Where every line is echoed besides the file. The app points these at Godot's output when it starts; until then
        /// (and in tests) lines go to the console.
        /// </summary>
        public static Action<string> Echo { get; set; } = Console.WriteLine;

        /// <inheritdoc cref="Echo"/>
        public static Action<string> EchoError { get; set; } = Console.Error.WriteLine;

        /// <summary>
        /// Starts writing the log to atlas_log.txt in <paramref name="folder"/> (the app's data folder's logs), archiving a
        /// large one first. Before this is called, lines are only echoed: nothing is written until the app has checked its
        /// data folder (a refused test run writes nothing). Later calls change nothing.
        /// </summary>
        public static void UseFolder(string folder)
        {
            lock (_fileLock)
            {
                if (_logFilePath != null) return;
                try
                {
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, "atlas_log.txt");
                    RotateIfLarge(folder, path);
                    _logFilePath = path;
                }
                catch (Exception ex)
                {
                    EchoError("Logging to file is unavailable: " + ex.Message);
                }
            }
        }

        private static void RotateIfLarge(string logDir, string logFile)
        {
            try
            {
                if (File.Exists(logFile) && new FileInfo(logFile).Length > 5 * 1024 * 1024)
                    File.Move(logFile, Path.Combine(logDir, "atlas_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"));
                // Keep the ten most recent archived logs.
                var old = new DirectoryInfo(logDir).GetFiles("atlas_log_*.txt");
                Array.Sort(old, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = 10; i < old.Length; i++) { try { old[i].Delete(); } catch { } }
            }
            catch { } // housekeeping only: logging carries on in the same file
        }

        private static void WriteLog(string level, string message)
        {
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logEntry = $"[{time}] [{level}] {message}";
            try
            {
                // Called from network and process threads as well as the main thread.
                if (_logFilePath != null) lock (_fileLock) File.AppendAllText(_logFilePath, logEntry + System.Environment.NewLine);
            }
            catch (Exception ex)
            {
                EchoError("Failed to write to log: " + ex.Message);
            }
            Echo(logEntry);
        }

        public static void LogInfo(string message)
        {
            WriteLog("INFO", message);
            OnLogMessage?.Invoke($"[color=gray][{DisplayClock():HH:mm:ss}][/color] {message}\n", "INFO");
        }

        public static void LogWarning(string message)
        {
            WriteLog("WARN", message);
            OnLogMessage?.Invoke($"[color=gray][{DisplayClock():HH:mm:ss}][/color] [color=yellow]WARN: {message}[/color]\n", "WARN");
        }

        public static void LogError(string message)
        {
            WriteLog("ERROR", message);
            OnLogMessage?.Invoke($"[color=gray][{DisplayClock():HH:mm:ss}][/color] [color=red]ERROR: {message}[/color]\n", "ERROR");
        }

        public static void LogDebug(string message)
        {
            WriteLog("DEBUG", message);
            // Debug does not typically go to UI console unless verbosity is high
        }
    }
}
