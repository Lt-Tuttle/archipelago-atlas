using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core.Updates;

/// <summary>
/// What a release of Atlas is made of, and where the updater keeps its work inside Atlas's data folder.
/// </summary>
public static class UpdateLayout
{
    /// <summary>The folder a release zip unpacks to.</summary>
    public const string ProgramFolder = "TheArchipelagoAtlas";
    public const string Exe = "TheArchipelagoAtlas.exe";
    public const string Pck = "TheArchipelagoAtlas.pck";
    public const string DataFolder = "data_AP_Atlas_windows_x86_64";
    public const string MainAssembly = "AP_Atlas.dll";
    /// <summary>The user's data, next to the program: never moved, never replaced.</summary>
    public const string PortableData = "PortableData";

    /// <summary>Why a folder isn't a complete Atlas, or null when it is.</summary>
    public static string? Validate(string programDir)
    {
        if (!File.Exists(Path.Combine(programDir, Exe))) return $"{Exe} is missing";
        if (!File.Exists(Path.Combine(programDir, Pck))) return $"{Pck} is missing";
        if (!File.Exists(Path.Combine(programDir, DataFolder, MainAssembly))) return $"{DataFolder}\\{MainAssembly} is missing";
        return null;
    }
}

/// <summary>The hand-over note between the Atlas that installed an update and the one it started (updates/pending.json).</summary>
public sealed class UpdateMarker
{
    public const string Starting = "starting";
    public const string Ready = "ready";
    public const string RolledBack = "rolled-back";

    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    /// <summary><see cref="Starting"/> once the files are swapped, <see cref="Ready"/> once the new Atlas has its window, <see cref="RolledBack"/> when it didn't.</summary>
    public string State { get; set; } = "";
    // Shown to the user later, so it's the wall clock.
    public DateTime StartedUtc { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>
/// Puts a staged release in place of the running one and keeps what it replaces, so it can be put back by hand (Roll
/// back) or by the supervisor when the new Atlas doesn't start. Everything is a move on the same drive, file by file:
/// Windows lets a running program's files be moved aside (never overwritten) but not a folder that holds one of them be
/// renamed, so folders are made as needed and removed once empty. Only the entries a release has are moved; the user's
/// PortableData and anything else in the folder stay where they are.
/// </summary>
public static class UpdateInstaller
{
    public const string UpdatesFolder = "updates";
    public const string DownloadFolder = "download";
    public const string NextFolder = "next";
    public const string PreviousFolder = "previous";
    public const string FailedFolder = "failed";
    public const string MarkerFile = "pending.json";
    /// <summary>In the previous version's folder: which version it is.</summary>
    public const string VersionFile = "VERSION.txt";

    public static string MarkerPath(string updatesDir) => Path.Combine(updatesDir, MarkerFile);

    /// <summary>The hand-over note, or null when there is none.</summary>
    public static UpdateMarker? ReadMarker(string updatesDir)
    {
        string path = MarkerPath(updatesDir);
        if (!File.Exists(path)) return null;
        var marker = SafeFile.ReadJson<UpdateMarker>(path, () => new UpdateMarker());
        return string.IsNullOrEmpty(marker.State) ? null : marker;
    }

    public static void WriteMarker(string updatesDir, UpdateMarker marker)
    {
        Directory.CreateDirectory(updatesDir);
        SafeFile.WriteJson(MarkerPath(updatesDir), marker);
    }

    public static void DeleteMarker(string updatesDir) => SafeFile.Delete(MarkerPath(updatesDir));

    /// <summary>The version kept in a previous (or failed) folder, or null.</summary>
    public static string? VersionIn(string folder)
    {
        string path = Path.Combine(folder, VersionFile);
        if (!File.Exists(path)) return null;
        string text = File.ReadAllText(path).Trim();
        return text.Length > 0 && text.Length < 64 ? text : null;
    }

    /// <summary>
    /// Moves the installed program's entries (those the staged release also has) into <paramref name="previousDir"/>, then
    /// the staged release's entries into <paramref name="installDir"/>. If anything fails, every move is undone before
    /// the exception goes on.
    /// </summary>
    public static void Swap(string installDir, string stagedProgramDir, string previousDir, string installedVersion, Action<string> log)
    {
        string? problem = UpdateLayout.Validate(stagedProgramDir);
        if (problem != null) throw new InvalidOperationException("The staged release is incomplete: " + problem + ".");
        if (Directory.Exists(previousDir)) Directory.Delete(previousDir, recursive: true);
        Directory.CreateDirectory(previousDir);
        var moves = new List<(string From, string To)>();
        var made = new List<string>();
        try
        {
            foreach (string entry in Directory.GetFileSystemEntries(stagedProgramDir))
            {
                string name = Path.GetFileName(entry);
                if (IsNeverMoved(name)) continue;
                string installed = Path.Combine(installDir, name);
                if (!File.Exists(installed) && !Directory.Exists(installed)) continue;
                MoveTree(installed, Path.Combine(previousDir, name), moves, made);
            }
            SafeFile.WriteAllText(Path.Combine(previousDir, VersionFile), installedVersion);
            foreach (string entry in Directory.GetFileSystemEntries(stagedProgramDir))
            {
                string name = Path.GetFileName(entry);
                if (IsNeverMoved(name)) continue;
                MoveTree(entry, Path.Combine(installDir, name), moves, made);
            }
            log($"Swapped in the staged release: {moves.Count} files moved; the previous version is kept in {previousDir}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log("The swap failed and is being undone: " + ex.Message);
            Undo(moves, made, log);
            throw;
        }
    }

    /// <summary>
    /// Puts the previous version back: the installed entries it also has go to <paramref name="failedDir"/>, its own
    /// entries into <paramref name="installDir"/>. Returns the version put back (from its VERSION.txt), or null.
    /// </summary>
    public static string? RollBack(string installDir, string previousDir, string failedDir, string failedVersion, Action<string> log)
    {
        if (!Directory.Exists(previousDir)) throw new InvalidOperationException("There is no previous version to put back.");
        string? problem = UpdateLayout.Validate(previousDir);
        if (problem != null) throw new InvalidOperationException("The previous version is incomplete: " + problem + ".");
        string? version = VersionIn(previousDir);
        if (Directory.Exists(failedDir)) Directory.Delete(failedDir, recursive: true);
        Directory.CreateDirectory(failedDir);
        var moves = new List<(string From, string To)>();
        var made = new List<string>();
        try
        {
            foreach (string entry in Directory.GetFileSystemEntries(previousDir))
            {
                string name = Path.GetFileName(entry);
                if (IsNeverMoved(name) || name == VersionFile) continue;
                string installed = Path.Combine(installDir, name);
                if (!File.Exists(installed) && !Directory.Exists(installed)) continue;
                MoveTree(installed, Path.Combine(failedDir, name), moves, made);
            }
            SafeFile.WriteAllText(Path.Combine(failedDir, VersionFile), failedVersion);
            foreach (string entry in Directory.GetFileSystemEntries(previousDir))
            {
                string name = Path.GetFileName(entry);
                if (IsNeverMoved(name) || name == VersionFile) continue;
                MoveTree(entry, Path.Combine(installDir, name), moves, made);
            }
            SafeFile.Delete(Path.Combine(previousDir, VersionFile));
            log($"Put the previous version back ({version ?? "unknown"}): {moves.Count} files moved; the version that failed is kept in {failedDir}.");
            return version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log("Putting the previous version back failed and is being undone: " + ex.Message);
            Undo(moves, made, log);
            throw;
        }
    }

    public enum Watch
    {
        /// <summary>The new Atlas reached its window and said so.</summary>
        Started,
        /// <summary>The new Atlas ended before saying so.</summary>
        Exited,
        /// <summary>The new Atlas neither said so nor ended in time.</summary>
        TimedOut
    }

    /// <summary>
    /// Waits for the Atlas just started to mark the hand-over note <see cref="UpdateMarker.Ready"/>, or to end, or for
    /// the time to run out. <paramref name="hasExited"/> says whether the new process is gone.
    /// </summary>
    public static async Task<Watch> WatchAsync(string updatesDir, Func<bool> hasExited, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = Deadline.In(timeout);
        while (!deadline.Passed)
        {
            if (ReadMarker(updatesDir)?.State == UpdateMarker.Ready) return Watch.Started;
            if (hasExited())
            {
                // The note may have been written just before the process ended (a build that quits once ready, in tests).
                await Task.Delay(300, ct).ConfigureAwait(false);
                return ReadMarker(updatesDir)?.State == UpdateMarker.Ready ? Watch.Started : Watch.Exited;
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return Watch.TimedOut;
    }

    private static bool IsNeverMoved(string name) =>
        name.Equals(UpdateLayout.PortableData, StringComparison.OrdinalIgnoreCase) || name.Equals(UpdatesFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moves a file, or a folder's files one by one into <paramref name="to"/> (made if need be, as are the folders within
    /// it), then removes each folder emptied. Windows renames a running program's files but not a folder that holds one of
    /// them: this is how the running Atlas's own files move aside, and how the previous version's move back while its
    /// supervisor runs from them. Every file moved and every folder made is recorded for <see cref="Undo"/>.
    /// </summary>
    private static void MoveTree(string from, string to, List<(string From, string To)> moves, List<string> made)
    {
        if (!Directory.Exists(from))
        {
            MakeFolder(Path.GetDirectoryName(to)!, made);
            // A scanner holds a file for a moment; one another program keeps open without sharing fails after the retries.
            SafeFile.Retry(() => File.Move(from, to, overwrite: false));
            moves.Add((from, to));
            return;
        }
        MakeFolder(to, made);
        foreach (string entry in Directory.GetFileSystemEntries(from))
            MoveTree(entry, Path.Combine(to, Path.GetFileName(entry)), moves, made);
        RemoveIfEmpty(from);
    }

    private static void MakeFolder(string dir, List<string> made)
    {
        if (Directory.Exists(dir)) return;
        Directory.CreateDirectory(dir);
        made.Add(dir);
    }

    /// <summary>Removes a folder whose files have moved out. One a program still holds open stays, empty, for the files moving in.</summary>
    private static void RemoveIfEmpty(string dir)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
            if (!entries.MoveNext()) Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // held open by a program: it stays, empty, for the files moving in
    }

    private static void Undo(List<(string From, string To)> moves, List<string> made, Action<string> log)
    {
        for (int i = moves.Count - 1; i >= 0; i--)
        {
            var (from, to) = moves[i];
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                SafeFile.Retry(() => File.Move(to, from, overwrite: false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Couldn't move {to} back to {from}: {ex.Message}");
            }
        }
        for (int i = made.Count - 1; i >= 0; i--) RemoveIfEmpty(made[i]);
    }
}
