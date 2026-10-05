#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace AP_Atlas.Core.EngineSetup
{
    public enum EngineMode
    {
        /// <summary>Atlas's own Python + Archipelago source in PortableData/engine (no installer, no admin).</summary>
        Portable,
        /// <summary>An Archipelago install the user already has (run through ArchipelagoLauncher.exe).</summary>
        Existing
    }

    /// <summary>One Archipelago engine: where it lives and how to start a bridge component in it.</summary>
    public sealed class EngineInstall
    {
        public EngineMode Mode { get; }

        /// <summary>The Archipelago folder (holds custom_worlds, Players, host.yaml).</summary>
        public string Root { get; }

        private EngineInstall(EngineMode mode, string root)
        {
            Mode = mode;
            Root = root ?? "";
        }

        public static EngineInstall Portable() => new EngineInstall(EngineMode.Portable, AtlasEngine.ArchipelagoDir);

        public static EngineInstall Existing(string installPath) => new EngineInstall(EngineMode.Existing, installPath);

        public string Describe() => Mode == EngineMode.Portable ? "Atlas portable engine" : $"Archipelago install at {Root}";

        /// <summary>Whether the engine's program files are present (it can start, though a tracker may still be missing).</summary>
        public bool CanLaunch =>
            Mode == EngineMode.Portable
                ? File.Exists(AtlasEngine.PythonExe) && File.Exists(Path.Combine(Root, "Utils.py")) && File.Exists(AtlasEngine.RunnerPath)
                : !string.IsNullOrEmpty(Root) && File.Exists(Path.Combine(Root, "ArchipelagoLauncher.exe"));

        /// <summary>Where Atlas installs apworlds (the bridge, the tracker, games). Never created outside a real install.</summary>
        public string WorldsDir
        {
            get
            {
                if (string.IsNullOrEmpty(Root) || !Directory.Exists(Root)) return null;
                string custom = Path.Combine(Root, "custom_worlds");
                if (Mode == EngineMode.Portable || Directory.Exists(custom)) return custom;
                string lib = Path.Combine(Root, "lib", "worlds");
                if (Directory.Exists(lib)) return lib;
                // A real install always has one of these; don't litter an arbitrary folder the user picked.
                return File.Exists(Path.Combine(Root, "ArchipelagoLauncher.exe")) ? custom : null;
            }
        }

        /// <summary>Every folder Archipelago loads worlds from.</summary>
        public IEnumerable<string> WorldFolders()
        {
            if (string.IsNullOrEmpty(Root)) yield break;
            foreach (var dir in new[] { Path.Combine(Root, "custom_worlds"), Path.Combine(Root, "lib", "worlds"), Path.Combine(Root, "worlds") })
                if (Directory.Exists(dir)) yield return dir;
        }

        public string PlayersDir => string.IsNullOrEmpty(Root) ? null : Path.Combine(Root, "Players");

        /// <summary>The Universal Tracker apworld, found by its contents (any file name, any worlds folder). Null if none.</summary>
        public string FindTracker()
        {
            foreach (var dir in WorldFolders())
            {
                // Unpacked (a source checkout or a developer install).
                if (File.Exists(Path.Combine(dir, "tracker", "TrackerCore.py"))) return Path.Combine(dir, "tracker");
                foreach (var file in SafeFiles(dir, "*.apworld"))
                    if (ApworldHasEntry(file, e => e.EndsWith("/TrackerCore.py", StringComparison.OrdinalIgnoreCase))) return file;
            }
            return null;
        }

        /// <summary>Every Universal Tracker copy (two at once conflict).</summary>
        public List<string> FindAllTrackers()
        {
            var found = new List<string>();
            foreach (var dir in WorldFolders())
                foreach (var file in SafeFiles(dir, "*.apworld"))
                    if (ApworldHasEntry(file, e => e.EndsWith("/TrackerCore.py", StringComparison.OrdinalIgnoreCase))) found.Add(file);
            return found;
        }

        public bool HasTracker => FindTracker() != null;

        /// <summary>How to start one bridge component (UltimateBridge, AtlasNames, AtlasCheck) with stdin/stdout piped.</summary>
        public ProcessStartInfo StartInfo(string component)
        {
            ProcessStartInfo info;
            if (Mode == EngineMode.Portable)
            {
                // -u: unbuffered; -X utf8: UTF-8 pipes whatever the system code page is.
                info = new ProcessStartInfo { FileName = AtlasEngine.PythonExe, WorkingDirectory = Root };
                foreach (var arg in new[] { "-u", "-X", "utf8", AtlasEngine.RunnerPath, Root, component }) info.ArgumentList.Add(arg);
            }
            else
            {
                info = new ProcessStartInfo { FileName = Path.Combine(Root, "ArchipelagoLauncher.exe"), WorkingDirectory = Root };
                info.ArgumentList.Add(component);
            }
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.StandardOutputEncoding = System.Text.Encoding.UTF8;
            return info;
        }

        // =====================================================================
        // Apworld helpers
        // =====================================================================

        internal static IEnumerable<string> SafeFiles(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern); }
            catch { return Array.Empty<string>(); }
        }

        internal static bool ApworldHasEntry(string file, Func<string, bool> match)
        {
            try
            {
                using var zip = ZipFile.OpenRead(file);
                return zip.Entries.Any(e => match(e.FullName.Replace('\\', '/')));
            }
            catch { return false; }
        }
    }
}
