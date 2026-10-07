using System;
using System.IO;

namespace AP_Atlas.Core.Updates;

/// <summary>
/// Unpacks a downloaded (hash-checked) release next to Atlas's data, ready to be swapped in, and checks that what came
/// out is a complete Atlas.
/// </summary>
public static class UpdateStage
{
    /// <summary>Unpacks the zip into <paramref name="nextDir"/> (emptied first). The program folder inside it, or what went wrong.</summary>
    public static (string? ProgramDir, string? Error) Unpack(string zipPath, string nextDir)
    {
        try
        {
            if (Directory.Exists(nextDir)) Directory.Delete(nextDir, recursive: true);
            SafeZip.UnpackTo(zipPath, nextDir, SafeZip.ProgramLimit, SafeZip.ProgramTotal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try { if (Directory.Exists(nextDir)) Directory.Delete(nextDir, recursive: true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { } // the folder is emptied before the next try
            return (null, "The update couldn't be unpacked: " + ex.Message);
        }
        string programDir = Path.Combine(nextDir, UpdateLayout.ProgramFolder);
        if (!Directory.Exists(programDir)) return (null, $"The update doesn't hold a {UpdateLayout.ProgramFolder} folder.");
        string? problem = UpdateLayout.Validate(programDir);
        return problem == null ? (programDir, null) : (null, "The update is incomplete: " + problem + ".");
    }

    /// <summary>The version a staged program folder's VERSION.txt names (written when it was staged), or null.</summary>
    public static string? StagedVersion(string nextDir) => UpdateInstaller.VersionIn(nextDir);
}
