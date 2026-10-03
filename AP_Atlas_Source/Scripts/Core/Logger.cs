using System;
using System.IO;
using Godot;

namespace AP_Atlas.Core
{
    public static class Logger
    {
        private static string _logFilePath;
        public static Action<string, string> OnLogMessage;

        static Logger()
        {
            string dir = DataManager.GetDataDirectory();
            string logDir = Path.Combine(dir, "logs");
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);

            _logFilePath = Path.Combine(logDir, "atlas_log.txt");

            if (File.Exists(_logFilePath) && new FileInfo(_logFilePath).Length > 5 * 1024 * 1024)
            {
                File.Move(_logFilePath, Path.Combine(logDir, "atlas_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"));
            }
        }

        private static void WriteLog(string level, string message)
        {
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logEntry = $"[{time}] [{level}] {message}";
            try
            {
                File.AppendAllText(_logFilePath, logEntry + System.Environment.NewLine);
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
