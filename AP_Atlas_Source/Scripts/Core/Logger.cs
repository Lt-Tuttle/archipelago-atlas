using System;
using System.IO;
using Godot;

namespace AP_Atlas.Core
{
    public static class Logger
    {
        private static string _logFilePath;
        private static readonly object _fileLock = new object();
        public static Action<string, string> OnLogMessage;

        static Logger()
        {
            // A failure here would make every later log call throw (a broken type initializer), so nothing may escape.
            try
            {
                string logDir = Path.Combine(DataManager.GetDataDirectory(), "logs");
                Directory.CreateDirectory(logDir);
                _logFilePath = Path.Combine(logDir, "atlas_log.txt");
                RotateIfLarge(logDir);
            }
            catch (Exception ex)
            {
                _logFilePath = null;
                GD.PrintErr("Logging to file is unavailable: " + ex.Message);
            }
        }

        private static void RotateIfLarge(string logDir)
        {
            try
            {
                if (File.Exists(_logFilePath) && new FileInfo(_logFilePath).Length > 5 * 1024 * 1024)
                    File.Move(_logFilePath, Path.Combine(logDir, "atlas_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"));
                // Keep the ten most recent archived logs.
                var old = new DirectoryInfo(logDir).GetFiles("atlas_log_*.txt");
                Array.Sort(old, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = 10; i < old.Length; i++) { try { old[i].Delete(); } catch { } }
            }
            catch { }
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
                GD.PrintErr("Failed to write to log: " + ex.Message);
            }
            GD.Print(logEntry);
        }

        public static void LogInfo(string message)
        {
            WriteLog("INFO", message);
            OnLogMessage?.Invoke($"[color=gray][{DateTime.Now:HH:mm:ss}][/color] {message}\n", "INFO");
        }

        public static void LogWarning(string message)
        {
            WriteLog("WARN", message);
            OnLogMessage?.Invoke($"[color=gray][{DateTime.Now:HH:mm:ss}][/color] [color=yellow]WARN: {message}[/color]\n", "WARN");
        }

        public static void LogError(string message)
        {
            WriteLog("ERROR", message);
            OnLogMessage?.Invoke($"[color=gray][{DateTime.Now:HH:mm:ss}][/color] [color=red]ERROR: {message}[/color]\n", "ERROR");
        }

        public static void LogDebug(string message)
        {
            WriteLog("DEBUG", message);
            // Debug does not typically go to UI console unless verbosity is high
        }
    }
}
