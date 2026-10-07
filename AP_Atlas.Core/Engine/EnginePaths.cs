using System;
using System.IO;

namespace AP_Atlas.Core.EngineSetup;

/// <summary>
/// How deep the engine's files go. Windows refuses paths past 259 characters unless the PC's long-path setting is on
/// (a system-wide registry change Atlas never makes), and pip fails partway when one file passes it. Atlas keeps the
/// install below the limit (setuptools' test folders, the only files past ~92 characters below site-packages, are left
/// out) and says before anything downloads when the folder itself is too deep.
/// </summary>
public static class EnginePaths
{
    /// <summary>What Windows allows for a path without the long-path setting.</summary>
    public const int WindowsPathLimit = 259;

    /// <summary>
    /// The longest file below site-packages after an install, measured in a set-up engine on the pinned packages with
    /// setuptools' test folders left out (a .pyc under setuptools/config/_validate_pyproject/__pycache__), plus a
    /// little room. Measure again when the lock changes.
    /// </summary>
    public const int LongestInstalledFile = 92 + 8;

    /// <summary>Where the engine's packages live, below the data folder.</summary>
    public static readonly string SitePackages = Path.Combine("engine", "python", "Lib", "site-packages");

    /// <summary>The longest path an install from <paramref name="dataDir"/> would write.</summary>
    public static int LongestPath(string dataDir) =>
        Path.GetFullPath(Path.Combine(dataDir, SitePackages)).TrimEnd(Path.DirectorySeparatorChar).Length + 1 + LongestInstalledFile;

    /// <summary>Why the engine can't be set up from <paramref name="dataDir"/>, in plain words, or null when it can.</summary>
    public static string? DepthProblem(string dataDir)
    {
        int longest = LongestPath(dataDir);
        if (longest <= WindowsPathLimit) return null;
        return $"Atlas's folder is too deep for Windows: the engine's longest file would be {longest} characters, and Windows allows {WindowsPathLimit}. " +
               @"Move Atlas to a shorter path, such as C:\Games\Atlas, then set it up again.";
    }
}
