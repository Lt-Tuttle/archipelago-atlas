#nullable disable
using System;
using System.IO;

namespace AP_Atlas.Core
{
    public static class Logger
    {
        private static string _logFilePath;

        /// <summary>The log file Atlas writes now (null before the data folder is known).</summary>
        public static string CurrentLogPath => _logFilePath;
        private static readonly object _fileLock = new object();
        // Set after a failed write, so a log file that can't be written (its folder deleted, a full disk) is reported
        // once, not on every line; cleared when a write works again.
        private static bool _fileFailing;
        /// <summary>
        /// A line for the window's logs (BBCode, level). Raised on whichever thread logged it. Messages are plain text: they
        /// often hold text from outside Atlas (a pack's or a server's), so it's escaped here and shows as written.
        /// </summary>
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
        public static void UseFolder(string folder) => UseFolder(folder, "atlas_log");

        /// <summary>
        /// As <see cref="UseFolder(string)"/>, writing to <paramref name="stem"/>.txt (the update supervisor keeps its own
        /// file, since the Atlas it watches writes atlas_log.txt in the same folder).
        /// </summary>
        public static void UseFolder(string folder, string stem)
        {
            lock (_fileLock)
            {
                if (_logFilePath != null) return;
                try
                {
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, stem + ".txt");
                    RotateIfLarge(folder, path, stem);
                    _logFilePath = path;
                }
                catch (Exception ex)
                {
                    EchoError("Logging to file is unavailable: " + ex.Message);
                }
            }
        }

        private static void RotateIfLarge(string logDir, string logFile, string stem)
        {
            try
            {
                if (File.Exists(logFile) && new FileInfo(logFile).Length > 5 * 1024 * 1024)
                    File.Move(logFile, Path.Combine(logDir, stem + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"));
                // Keep the ten most recent archived logs.
                var old = new DirectoryInfo(logDir).GetFiles(stem + "_*.txt");
                Array.Sort(old, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = 10; i < old.Length; i++)
                {
                    try { old[i].Delete(); }
                    catch { } // one that can't be deleted now goes the next time the log is archived
                }
            }
            catch { } // housekeeping only: logging carries on in the same file
        }

        /// <summary>
        /// The most of one message that's logged, in characters. A longer one (a program's endless line quoted in full, say)
        /// is cut, with a note of how much, so one message can't fill the log file or hold up the window laying it out.
        /// </summary>
        public const int MaxMessageLength = 64 << 10;

        // The message, cut at MaxMessageLength.
        private static string Bounded(string message) =>
            message == null || message.Length <= MaxMessageLength ? message : message[..MaxMessageLength] + $"… ({message.Length - MaxMessageLength:N0} more characters)";

        /// <summary>
        /// The most of one message the window's logs show, in characters (the log file keeps up to <see cref="MaxMessageLength"/>):
        /// laying a line out takes the window about 20 ms per thousand characters.
        /// </summary>
        public const int MaxShownLength = 4_000;

        /// <summary>A message as the window's logs show it: cut at <see cref="MaxShownLength"/>, saying how much the log file has besides.</summary>
        public static string Shown(string message) =>
            message == null || message.Length <= MaxShownLength ? message : message[..MaxShownLength] + $"… ({message.Length - MaxShownLength:N0} more characters in the log file)";

        private static string Line(string level, string message) => $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";

        private static void AppendToFile(string line)
        {
            // Called from network and process threads as well as the main thread.
            lock (_fileLock)
            {
                if (_logFilePath == null) return;
                File.AppendAllText(_logFilePath, line + System.Environment.NewLine);
                _fileFailing = false;
            }
        }

        private static void WriteLog(string level, string message)
        {
            string logEntry = Line(level, message);
            try
            {
                AppendToFile(logEntry);
            }
            catch (Exception ex)
            {
                bool first;
                lock (_fileLock)
                {
                    first = !_fileFailing;
                    _fileFailing = true;
                }
                if (first) EchoError("Failed to write to log: " + ex.Message + " (shown once until writing works again)");
            }
            Echo(logEntry);
        }

        /// <summary>Stops writing to the log file (a test's folder is about to be deleted). Lines are still echoed.</summary>
        internal static void StopUsingFolder()
        {
            lock (_fileLock)
            {
                _logFilePath = null;
                _fileFailing = false;
            }
        }

        /// <summary>
        /// Writes a line to the log file only: no echo and no line in the window. For Godot's own errors and warnings, which
        /// Godot reports on any thread, sometimes while holding its own locks: this never calls back into Godot. Lines
        /// before <see cref="UseFolder"/> are dropped (Godot has already printed them to its console).
        /// </summary>
        public static void RecordOnly(string level, string message)
        {
            try { AppendToFile(Line(level, Bounded(message))); }
            catch { } // nowhere safe to report it from inside Godot's error reporting; the console already has the line
        }

        /// <summary>Logs plain text (shown as written), in the window in a colour if given ("orange", "#8A2BE2").</summary>
        public static void LogInfo(string message, string color = null)
        {
            message = Bounded(message);
            WriteLog("INFO", message);
            OnLogMessage?.Invoke($"{Stamp()} {Bbcode.Colored(Shown(message), color)}\n", "INFO");
        }

        public static void LogWarning(string message)
        {
            message = Bounded(message);
            WriteLog("WARN", message);
            OnLogMessage?.Invoke($"{Stamp()} {Bbcode.Colored("WARN: " + Shown(message), "yellow")}\n", "WARN");
        }

        public static void LogError(string message)
        {
            message = Bounded(message);
            WriteLog("ERROR", message);
            OnLogMessage?.Invoke($"{Stamp()} {Bbcode.Colored("ERROR: " + Shown(message), "red")}\n", "ERROR");
        }

        // When a line was logged, as the window shows it.
        private static string Stamp() => Bbcode.Colored($"[{DisplayClock():HH:mm:ss}]", "gray");

        public static void LogDebug(string message)
        {
            WriteLog("DEBUG", Bounded(message));
            // Debug does not typically go to UI console unless verbosity is high
        }

        /// <summary>The level of <see cref="LogDiagnostic"/> lines, as <see cref="OnLogMessage"/> names it.</summary>
        public const string DiagnosticLevel = "DIAG";

        /// <summary>A diagnostic (a frame hitch and what ran in it): the log file and the Debug Log, never the System Log.</summary>
        public static void LogDiagnostic(string message)
        {
            message = Bounded(message);
            WriteLog(DiagnosticLevel, message);
            OnLogMessage?.Invoke($"{Stamp()} {Bbcode.Colored(Shown(message), "gray")}\n", DiagnosticLevel);
        }
    }
}
