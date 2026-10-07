using System;
using Godot;

namespace AP_Atlas.Core
{
    /// <summary>
    /// Copies Godot's own errors and warnings into Atlas's log. Godot's log file is turned off in project.godot: Godot
    /// keeps it in %APPDATA%, outside Atlas's folder, with a copy of every line Atlas logs. Without this, an engine error
    /// would only reach the console, which a released Atlas doesn't have.
    /// </summary>
    /// <remarks>
    /// Godot calls this on any thread, sometimes while holding its own locks, so it only appends to the log file: no
    /// Godot calls, no echo (that would come straight back here) and no line in the window. Nothing in it may throw:
    /// Godot would report the exception as an error, which would come back here too.
    /// </remarks>
    public partial class GodotLog : Godot.Logger
    {
        private static GodotLog? _installed;

        /// <summary>Starts copying. Called first thing in the main window's _Ready.</summary>
        public static void Install()
        {
            if (_installed != null) return;
            _installed = new GodotLog();
            OS.AddLogger(_installed);
        }

        /// <summary>Stops copying while .NET can still answer (Godot keeps logging as it closes).</summary>
        public static void Uninstall()
        {
            if (_installed == null) return;
            OS.RemoveLogger(_installed);
            _installed = null;
        }

        private static int _errors;

        /// <summary>How many errors Godot has reported so far (the UI test checks a scenario adds none).</summary>
        public static int Errors => System.Threading.Volatile.Read(ref _errors);

        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType,
            Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
        {
            if (errorType != (int)ErrorType.Warning) System.Threading.Interlocked.Increment(ref _errors);
            try
            {
                string what = string.IsNullOrEmpty(rationale) ? code : rationale;
                AP_Atlas.Core.Logger.RecordOnly(errorType == (int)ErrorType.Warning ? "WARN" : "ERROR", $"Godot: {what} ({function}, {file}:{line})");
            }
            catch (Exception)
            {
                // Nowhere safe to report it from here; Godot has already printed the error to its console.
            }
        }

        // Godot's ordinary output isn't copied: Atlas's own lines (which Atlas echoes there) are already in the file.
    }
}
