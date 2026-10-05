using System;
using System.Diagnostics;
using System.IO;
using AP_Atlas.Core.Connections;
using AP_Atlas.Core.EngineSetup;
using Godot;

namespace AP_Atlas.Core
{
    /// <summary>Self-tests for keeping everything in Atlas's own folder: Godot's log, connections and engine processes.</summary>
    public static partial class SelfTest
    {
        /// <summary>
        /// Godot's log file and shader cache are off (Godot keeps them in %APPDATA%, outside Atlas's folder), and Godot's
        /// own warnings reach Atlas's log instead.
        /// </summary>
        private static void GodotWritesNothingOutside()
        {
            foreach (string setting in new[] { "debug/file_logging/enable_file_logging", "debug/file_logging/enable_file_logging.pc", "rendering/shader_compiler/shader_cache/enabled" })
                Expect(!ProjectSettings.GetSetting(setting).AsBool(), $"{setting} is on: Godot would write to %APPDATA%");
            string marker = "Self-test " + Guid.NewGuid().ToString("N")[..8] + ": Godot's warnings reach Atlas's log";
            GD.PushWarning(marker);
            string log = Path.Combine(DataManager.GetDataDirectory(), "logs", "atlas_log.txt");
            Expect(File.Exists(log) && File.ReadAllText(log).Contains("[WARN] Godot: " + marker), "a Godot warning didn't reach Atlas's log");
        }

        /// <summary>Every connection keeps the games' names in the data folder, never in the connection library's own cache.</summary>
        private static void ConnectionsKeepNamesInside()
        {
            var store = DataManager.DataPackages;
            Expect(Inside(store.Folder, DataManager.GetDataDirectory()), $"the names are kept in {store.Folder}, outside the data folder");
            var session = AtlasSessions.Create("localhost:38281", store); // never connected
            Expect(ReferenceEquals(AtlasSessions.StoreOf(session), store), "a new session doesn't use Atlas's store");
        }

        /// <summary>
        /// Engine processes (bridge components and setup steps) keep temporary files and caches in the engine folder,
        /// and ignore the user's own pip settings.
        /// </summary>
        private static void EngineProcessesKeepFilesInside()
        {
            string? before = System.Environment.GetEnvironmentVariable("PIP_INDEX_URL");
            System.Environment.SetEnvironmentVariable("PIP_INDEX_URL", "https://example.invalid/simple");
            try
            {
                Check(EngineInstall.Portable().StartInfo("AtlasCheck"), "a bridge component");
                Check(EngineInstall.Existing(Scratch("ArchipelagoInstall")).StartInfo("AtlasCheck"), "a component in an Archipelago install");
                Check(AtlasEngine.SetupStartInfo(AtlasEngine.PythonExe, new[] { "-m", "pip", "--version" }, AtlasEngine.EngineDir), "a setup step");
            }
            finally { System.Environment.SetEnvironmentVariable("PIP_INDEX_URL", before); }

            static void Check(ProcessStartInfo info, string what)
            {
                foreach (string name in new[] { "TEMP", "TMP", "LOCALAPPDATA", "WIN_PD_OVERRIDE_LOCAL_APPDATA", "APPDATA", "WIN_PD_OVERRIDE_APPDATA", "PIP_CACHE_DIR" })
                {
                    info.Environment.TryGetValue(name, out string? value);
                    Expect(value != null && Inside(value, AtlasEngine.EngineDir), $"{what}: {name} is {value ?? "not set"}, outside the engine folder");
                }
                Expect(Directory.Exists(info.Environment["TEMP"] ?? ""), $"{what}: its temp folder doesn't exist (Python would fall back to another one)");
                Expect(info.Environment.TryGetValue("PIP_CONFIG_FILE", out string? config) && config == "nul", $"{what}: pip would read the user's pip.ini");
                Expect(!info.Environment.ContainsKey("PIP_INDEX_URL"), $"{what}: the user's own pip settings reach it");
            }
        }

        private static bool Inside(string path, string folder) =>
            Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
