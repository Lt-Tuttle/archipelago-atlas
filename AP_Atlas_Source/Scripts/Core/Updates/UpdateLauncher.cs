using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace AP_Atlas.Core.Updates;

/// <summary>
/// The updater's one place that starts a program: the Atlas just installed (as the user would start it), and the Atlas it
/// replaced, headless, to watch the new one start and put itself back if it doesn't. Neither is put under the engine's
/// job object: they must outlive this Atlas.
/// </summary>
public static class UpdateLauncher
{
    /// <summary>The user argument (after Godot's "--") that makes an Atlas the supervisor of an update.</summary>
    public const string SuperviseArg = "--supervise-update";

    /// <summary>
    /// Starts the Atlas at <paramref name="exePath"/> with its own folder as its working folder and this Atlas's environment
    /// (plus <paramref name="environment"/>); <paramref name="headless"/> without a window, as a test run is.
    /// </summary>
    public static Process Start(string exePath, IDictionary<string, string>? environment = null, bool headless = false)
    {
        var info = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
            UseShellExecute = false,
            CreateNoWindow = false
        };
        if (headless) info.ArgumentList.Add("--headless");
        if (environment != null)
            foreach (var (name, value) in environment) info.Environment[name] = value;
        return Process.Start(info) ?? throw new InvalidOperationException("Windows didn't start " + exePath + ".");
    }

    /// <summary>
    /// Starts the previous Atlas headless as the supervisor: it watches the process <paramref name="newPid"/> for the
    /// hand-over note and puts the previous version back if the new one doesn't start.
    /// </summary>
    public static Process StartSupervisor(string previousExePath, string installDir, string dataDir, int newPid)
    {
        var info = new ProcessStartInfo(previousExePath)
        {
            WorkingDirectory = Path.GetDirectoryName(previousExePath) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in new[] { "--headless", "--", SuperviseArg, "--install-dir", installDir, "--data-dir", dataDir, "--new-pid", newPid.ToString(CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(arg);
        info.Environment["ATLAS_DATA_DIR"] = dataDir;
        return Process.Start(info) ?? throw new InvalidOperationException("Windows didn't start the update supervisor.");
    }

    /// <summary>
    /// Loads what starting a program needs, for before an update moves this Atlas's files: once they are in previous/, the
    /// runtime would look for anything not loaded yet at their old paths, where the new version's files are by then.
    /// </summary>
    public static void Prepare()
    {
        using var self = Process.GetCurrentProcess();
        var info = new ProcessStartInfo(self.Id.ToString(CultureInfo.InvariantCulture)) { UseShellExecute = false };
        info.ArgumentList.Add("--headless");
        info.Environment["ATLAS_DATA_DIR"] = "";
    }

    /// <summary>The supervisor's arguments as Godot hands them over, or null when these aren't a supervisor's.</summary>
    public static (string InstallDir, string DataDir, int NewPid)? ParseSupervisorArgs(IReadOnlyList<string> userArgs)
    {
        if (userArgs.Count == 0 || userArgs[0] != SuperviseArg) return null;
        string? installDir = null, dataDir = null;
        int newPid = 0;
        for (int i = 1; i + 1 < userArgs.Count; i += 2)
        {
            switch (userArgs[i])
            {
                case "--install-dir": installDir = userArgs[i + 1]; break;
                case "--data-dir": dataDir = userArgs[i + 1]; break;
                case "--new-pid": int.TryParse(userArgs[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out newPid); break;
            }
        }
        if (installDir == null || dataDir == null || newPid <= 0) return null;
        return (installDir, dataDir, newPid);
    }
}
