using System;
using System.IO;

namespace AP_Atlas.Core;

/// <summary>
/// Where Atlas keeps its data, settled before anything is written. Normally PortableData next to the program (the
/// portable zip). When that folder can't be written to (Atlas unzipped into Program Files, say), the user is asked once:
/// their local app data folder (<see cref="DefaultFallback"/>, nothing to remember) or another folder of their choice,
/// which is remembered in a one-line file there (<see cref="PointerFile"/>). An ATLAS_DATA_DIR override (the tests) comes
/// first. Nothing outside Atlas's folder is read or written unless its own folder can't be used.
/// </summary>
public static class DataFolder
{
    public const string PortableName = "PortableData";
    public const string AppFolderName = "The Archipelago Atlas";
    public const string PointerFile = "location.txt";

    public enum Source
    {
        /// <summary>ATLAS_DATA_DIR.</summary>
        Override,
        /// <summary>PortableData next to the program, as shipped.</summary>
        Portable,
        /// <summary>A folder the user chose earlier (the pointer file), because the program's can't be written.</summary>
        Chosen,
        /// <summary>The program's folder can't be written and nothing was chosen yet: ask.</summary>
        NeedsChoice
    }

    /// <summary>The outcome: the folder (null while a choice is needed), where it came from, why the portable one can't be used (null when it can), and the portable one.</summary>
    public sealed record Resolution(string? Path, Source Source, string? Problem, string Portable);

    /// <summary>
    /// Settles the folder: the override, else the portable folder when a file can be made in it, else the folder chosen
    /// earlier when it can still be written, else a choice is needed. <paramref name="probe"/> says why a folder can't be
    /// written (null when it can).
    /// </summary>
    public static Resolution Resolve(string? overridePath, string programDir, string localAppData, Func<string, string?> probe)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return new Resolution(System.IO.Path.GetFullPath(overridePath), Source.Override, null, "");
        string portable = System.IO.Path.Combine(programDir, PortableName);
        string? problem = probe(portable);
        if (problem == null) return new Resolution(portable, Source.Portable, null, portable);
        string? chosen = ReadPointer(localAppData);
        if (chosen != null && probe(chosen) == null) return new Resolution(chosen, Source.Chosen, problem, portable);
        return new Resolution(null, Source.NeedsChoice, problem, portable);
    }

    /// <summary>The folder offered first when the program's can't be written: per user, never needing admin rights.</summary>
    public static string DefaultFallback(string localAppData) => System.IO.Path.Combine(localAppData, AppFolderName, PortableName);

    /// <summary>Where a chosen folder is remembered.</summary>
    public static string PointerPath(string localAppData) => System.IO.Path.Combine(localAppData, AppFolderName, PointerFile);

    /// <summary>The folder the pointer names, or null when there is none (or it isn't a full path).</summary>
    public static string? ReadPointer(string localAppData)
    {
        try
        {
            string path = PointerPath(localAppData);
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path).Trim();
            return text.Length > 0 && System.IO.Path.IsPathRooted(text) ? System.IO.Path.GetFullPath(text) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Remembers a chosen folder (the user was told where, in the dialog).</summary>
    public static void WritePointer(string localAppData, string folder)
    {
        string path = PointerPath(localAppData);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        SafeFile.WriteAllText(path, folder);
    }

    /// <summary>
    /// Why a file can't be made in a folder (made if missing), in plain words, or null when one can. A probe file is
    /// written and removed at once.
    /// </summary>
    public static string? Probe(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = System.IO.Path.Combine(folder, ".atlas-write-check");
            using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Windows doesn't allow writing there";
        }
        catch (IOException ex)
        {
            return ex.Message.TrimEnd('.');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return "that isn't a usable folder path";
        }
    }
}
