using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AP_Atlas.Core.Updates;

/// <summary>How serious a notice from the updater is (the window picks the colour).</summary>
public enum NoticeKind
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Keeps Atlas up to date, with the user's say at every step: the daily check (its own permission, once a day at most),
/// the offer of a newer version with its notes, the download checked against the release's SHA-256, the copy staged in
/// Atlas's data folder, the swap when the user restarts (every file moved aside, never overwritten; PortableData never
/// touched), the hand-over note the new Atlas confirms, the previous version kept for Roll back, and the supervisor that
/// puts it back when the new Atlas doesn't start. The window gives it the dialogs and cards; tests give it a scratch
/// install folder.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Logged by the Atlas an update started, once it has its window (the release build's self-check looks for it).</summary>
    public const string ReadyLogLine = "UPDATE READY";
    /// <summary>Logged by the Atlas put back after an update that didn't start.</summary>
    public const string RolledBackLogLine = "UPDATE ROLLED BACK";
    /// <summary>"Once a day": a check is due this long after the last (a little under a day, so a daily habit doesn't drift).</summary>
    public static readonly TimeSpan CheckSpacing = TimeSpan.FromHours(23);
    /// <summary>How long the supervisor gives the new Atlas to bring up its window.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);

    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly string? _installDir;
    private readonly string _dataDir;
    private readonly SemVer _current;

    /// <param name="installDir">Where Atlas's program files are; null when this run can't update itself (a development run).</param>
    public UpdateService(AppSettings settings, Action saveSettings, string? installDir, string dataDir, SemVer current)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _installDir = installDir;
        _dataDir = dataDir;
        _current = current;
    }

    /// <summary>Asks the user for the update-check permission (Allow once / Always allow / Don't allow).</summary>
    public Func<Task<bool>> AskPermission { get; set; } = () => Task.FromResult(false);
    /// <summary>Whether the update-check permission is allowed now.</summary>
    public Func<bool> PermissionAllowed { get; set; } = () => false;
    /// <summary>Tells the user something, as a card: the text, how serious it is, and an action with its button text.</summary>
    public Action<string, NoticeKind, string?, Action?> Notice { get; set; } = (_, _, _, _) => { };
    /// <summary>A line for the status bar.</summary>
    public Action<string> Status { get; set; } = _ => { };
    /// <summary>Shows a newer release with its notes and the choice to update.</summary>
    public Action<ReleaseInfo> ShowRelease { get; set; } = _ => { };
    /// <summary>Asked to close everything and restart into the staged version (the window's job).</summary>
    public Action RequestRestart { get; set; } = () => { };
    /// <summary>Opens an https page in the browser.</summary>
    public Action<string> OpenWeb { get; set; } = _ => { };
    /// <summary>Something the Settings page shows changed (a version found, staged, kept).</summary>
    public event Action? Changed;

    public SemVer Current => _current;
    public bool CanApply => _installDir != null;
    public string UpdatesDir => Path.Combine(_dataDir, UpdateInstaller.UpdatesFolder);
    public string DownloadDir => Path.Combine(UpdatesDir, UpdateInstaller.DownloadFolder);
    public string NextDir => Path.Combine(UpdatesDir, UpdateInstaller.NextFolder);
    public string PreviousDir => Path.Combine(UpdatesDir, UpdateInstaller.PreviousFolder);
    public string FailedDir => Path.Combine(UpdatesDir, UpdateInstaller.FailedFolder);
    private string StagedProgramDir => Path.Combine(NextDir, UpdateLayout.ProgramFolder);

    /// <summary>The newer release the last check found, if any.</summary>
    public ReleaseInfo? Available { get; private set; }
    /// <summary>A download or unpack is under way.</summary>
    public bool Busy { get; private set; }
    /// <summary>"stable" or "beta", from the setting and the running version.</summary>
    public string Channel => ReleaseInfo.ChannelFor(_settings.UpdateChannel, _current);
    /// <summary>The version staged and ready to install, or null.</summary>
    public string? StagedVersion => Directory.Exists(StagedProgramDir) && UpdateLayout.Validate(StagedProgramDir) == null ? UpdateInstaller.VersionIn(NextDir) : null;
    /// <summary>The version kept from before the last update, or null.</summary>
    public string? PreviousVersion => Directory.Exists(PreviousDir) && UpdateLayout.Validate(PreviousDir) == null ? UpdateInstaller.VersionIn(PreviousDir) : null;
    /// <summary>Whether the daily check hasn't run for a day (the saved time is a wall-clock time).</summary>
    public bool CheckDue => _settings.UpdateLastCheckUtc is not { } last || Deadline.WallClock() - last >= CheckSpacing; // wall clock: a saved time, across restarts

    /// <summary>
    /// Reads Atlas's releases on GitHub, when allowed: by itself only with the daily check on, the permission kept and a
    /// day gone by; by hand (<paramref name="manual"/>) after asking for the permission if needed. Null when it didn't run.
    /// </summary>
    public async Task<UpdateChecker.Outcome?> CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (!manual && (!_settings.UpdateCheckDaily || !PermissionAllowed() || !CheckDue)) return null;
        if (manual && !PermissionAllowed() && !await AskPermission()) return null;
        if (manual) Status("Checking GitHub for a newer Atlas…");
        var outcome = await UpdateChecker.CheckAsync(_current, Channel, ct);
        _settings.UpdateLastCheckUtc = Deadline.WallClock(); // wall clock: saved, so "once a day" holds across restarts
        _saveSettings();
        if (!outcome.Ok)
        {
            Logger.LogWarning("Checking for a newer Atlas: " + outcome.Error);
            if (manual)
            {
                Status("");
                Notice("Couldn't check for a newer Atlas: " + outcome.Error, NoticeKind.Warning, null, null);
            }
        }
        else
        {
            Available = outcome.Newer;
            if (outcome.Newer is { } newer)
            {
                Logger.LogInfo($"Atlas {newer.Version} is available ({Channel} channel); this is {_current}.");
                if (manual) Status($"Atlas {newer.Version} is available.");
                Notice($"The Archipelago Atlas {newer.Version} is available (you have {_current}).", NoticeKind.Info, "See what's new", () => ShowRelease(newer));
            }
            else if (manual)
            {
                Status($"Atlas is up to date ({_current}).");
                Notice($"Atlas is up to date: {_current} is the newest {Channel} version.", NoticeKind.Info, null, null);
            }
        }
        Changed?.Invoke();
        return outcome;
    }

    /// <summary>
    /// Downloads the release (checked against its SHA256SUMS.txt) and unpacks it into the data folder, ready for a restart.
    /// Nothing in Atlas's program folder changes here.
    /// </summary>
    public async Task<bool> DownloadAndStageAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        if (Busy) return false;
        if (_installDir == null)
        {
            Notice("Updates apply to the released Atlas; this is a development run.", NoticeKind.Warning, null, null);
            return false;
        }
        if (!IsWritable(_installDir))
        {
            Notice($"Atlas can't update itself here: its folder isn't writable. Download {release.Version} from GitHub and unzip it over this copy; your data folder stays as it is.",
                NoticeKind.Warning, "Open the releases page", () => OpenWeb(UpdateChecker.ReleasesPage));
            return false;
        }
        if (!SameDrive(_installDir, UpdatesDir))
        {
            Notice($"Atlas can't update itself here: its data folder is on a different drive from its program folder, so the new files can't be moved into place. Download {release.Version} from GitHub and unzip it over this copy; your data folder stays as it is.",
                NoticeKind.Warning, "Open the releases page", () => OpenWeb(UpdateChecker.ReleasesPage));
            return false;
        }
        if (!HasRoomFor(release))
        {
            Notice($"There isn't enough free space on this drive to update (about {3 * release.ZipSize / (1024 * 1024) + 100} MB is needed).", NoticeKind.Warning, null, null);
            return false;
        }
        Busy = true;
        Changed?.Invoke();
        try
        {
            Status($"Downloading Atlas {release.Version}…");
            var (zip, error) = await UpdateDownloader.DownloadAsync(release, DownloadDir,
                (done, total) => Status(total > 0 ? $"Downloading Atlas {release.Version}: {done >> 20} MB of {total >> 20} MB" : $"Downloading Atlas {release.Version}: {done >> 20} MB"), ct);
            if (error != null)
            {
                Fail("The update couldn't be downloaded: " + error);
                return false;
            }
            Status($"Unpacking Atlas {release.Version}…");
            string zipPath = zip!;
            var (programDir, unpackError) = await Task.Run(() => UpdateStage.Unpack(zipPath, NextDir), ct);
            if (unpackError != null || programDir == null)
            {
                Fail(unpackError ?? "The update couldn't be unpacked.");
                return false;
            }
            SafeFile.WriteAllText(Path.Combine(NextDir, UpdateInstaller.VersionFile), release.Version.ToString());
            TryDelete(DownloadDir);
            Logger.LogInfo($"Atlas {release.Version} is downloaded, checked and staged in {NextDir}.");
            Status($"Atlas {release.Version} is ready to install.");
            Notice($"Atlas {release.Version} is ready. Restart to update?", NoticeKind.Info, "Restart to update", RequestRestart);
            return true;
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    private void Fail(string message)
    {
        Logger.LogWarning(message);
        Status("");
        Notice(message, NoticeKind.Error, null, null);
        TryDelete(DownloadDir);
        TryDelete(NextDir);
    }

    /// <summary>
    /// Puts the staged version in place of this one and starts it, with the previous Atlas (this one, moved aside) as the
    /// supervisor. For once every connection is closed and everything saved: on success the caller quits at once. What
    /// went wrong, or null. <paramref name="newEnvironment"/> is for the release build's self-check; <paramref name="headless"/>
    /// starts the new Atlas without a window, as this one runs in a test.
    /// </summary>
    public string? Apply(IDictionary<string, string>? newEnvironment = null, bool headless = false)
    {
        if (_installDir == null) return "Updates apply to the released Atlas; this is a development run.";
        string? toVersion = StagedVersion;
        if (toVersion == null) return "No update is staged.";
        string fromVersion = _current.ToString();
        // After the swap this Atlas's files are in previous/, while the paths the runtime knows them by hold the new
        // version's: everything the rest of this method needs is loaded first.
        UpdateLauncher.Prepare();
        try
        {
            UpdateInstaller.Swap(_installDir, StagedProgramDir, PreviousDir, fromVersion, line => Logger.LogInfo(line));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.LogError("The update couldn't be put in place: " + ex.Message);
            return "The update couldn't be put in place: " + ex.Message;
        }
        UpdateInstaller.WriteMarker(UpdatesDir, new UpdateMarker { FromVersion = fromVersion, ToVersion = toVersion, State = UpdateMarker.Starting, StartedUtc = Deadline.WallClock() }); // wall clock: a saved time, shown later
        TryDelete(NextDir);
        CrashGuard.ReleaseInstance();
        try
        {
            var started = UpdateLauncher.Start(Path.Combine(_installDir, UpdateLayout.Exe), newEnvironment, headless);
            UpdateLauncher.StartSupervisor(Path.Combine(PreviousDir, UpdateLayout.Exe), _installDir, _dataDir, started.Id);
            Logger.LogInfo($"Atlas {toVersion} started (process {started.Id}); version {fromVersion} is kept in {PreviousDir} and watching it start.");
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Logger.LogError("The updated Atlas couldn't be started: " + ex.Message);
            string message = "The updated Atlas couldn't be started: " + ex.Message;
            try
            {
                UpdateInstaller.RollBack(_installDir, PreviousDir, FailedDir, toVersion, line => Logger.LogInfo(line));
                UpdateInstaller.DeleteMarker(UpdatesDir);
                message += " The previous version was put back.";
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Logger.LogError("...and the previous version couldn't be put back: " + back.Message);
                message += " The previous version couldn't be put back either: " + back.Message;
            }
            CrashGuard.TryAcquireInstance();
            return message;
        }
    }

    /// <summary>
    /// Makes the previous version the staged one (so Roll back is an update to it, watched like any other), for
    /// <see cref="Apply"/> to put in place. What went wrong, or null.
    /// </summary>
    public string? StagePrevious()
    {
        if (_installDir == null) return "Updates apply to the released Atlas; this is a development run.";
        string? version = PreviousVersion;
        if (version == null) return "There is no previous version to go back to.";
        try
        {
            TryDelete(NextDir);
            Directory.CreateDirectory(NextDir);
            Directory.Move(PreviousDir, StagedProgramDir);
            SafeFile.Delete(Path.Combine(StagedProgramDir, UpdateInstaller.VersionFile));
            SafeFile.WriteAllText(Path.Combine(NextDir, UpdateInstaller.VersionFile), version);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The previous version couldn't be prepared: " + ex.Message;
        }
    }

    /// <summary>
    /// For the release build's self-check: stages a copy of the installed program as if it were a newer release, so the
    /// whole update path (swap, start, supervisor, hand-over) runs for real on a copy of the build.
    /// </summary>
    public string? StageCopyOfInstall()
    {
        if (_installDir == null) return "Updates apply to the released Atlas; this is a development run.";
        try
        {
            TryDelete(NextDir);
            Directory.CreateDirectory(StagedProgramDir);
            foreach (string entry in Directory.EnumerateFileSystemEntries(_installDir))
            {
                string name = Path.GetFileName(entry);
                if (name.Equals(UpdateLayout.PortableData, StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(StagedProgramDir, name);
                if (Directory.Exists(entry)) CopyTree(entry, target);
                else File.Copy(entry, target);
            }
            SafeFile.WriteAllText(Path.Combine(NextDir, UpdateInstaller.VersionFile), _current.ToString());
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The copy couldn't be staged: " + ex.Message;
        }
    }

    /// <summary>
    /// On every start: the hand-over note. The Atlas an update started marks it ready (and says so); the Atlas put back
    /// after a failed update says what happened; an old note is cleared.
    /// </summary>
    public void ConfirmStarted()
    {
        var marker = UpdateInstaller.ReadMarker(UpdatesDir);
        if (marker == null) return;
        if (marker.State == UpdateMarker.Starting && marker.ToVersion == _current.ToString())
        {
            marker.State = UpdateMarker.Ready;
            UpdateInstaller.WriteMarker(UpdatesDir, marker);
            Logger.LogInfo($"{ReadyLogLine}: Atlas {marker.ToVersion} started after the update from {marker.FromVersion}.");
            Notice($"Atlas updated to {marker.ToVersion} (from {marker.FromVersion}). Going back is under Settings → Updates.", NoticeKind.Info, null, null);
        }
        else if (marker.State == UpdateMarker.RolledBack)
        {
            Logger.LogWarning($"{RolledBackLogLine}: the update to {marker.ToVersion} didn't start ({marker.Reason}); version {marker.FromVersion} was put back.");
            Notice($"The update to Atlas {marker.ToVersion} didn't start ({marker.Reason}), so version {marker.FromVersion} was put back. The log has the details.", NoticeKind.Warning, null, null);
            UpdateInstaller.DeleteMarker(UpdatesDir);
        }
        else
        {
            // Ready from the last update, or a note for another version (an update applied, then this version started by hand).
            Logger.LogInfo($"Clearing an old update note ({marker.State}, {marker.FromVersion} → {marker.ToVersion}).");
            UpdateInstaller.DeleteMarker(UpdatesDir);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The supervisor, in the previous Atlas (headless): waits for the new Atlas to mark the note ready; if it ends first
    /// or runs out of time, puts the previous version back and starts it. The exit code: 0 started, 1 put back, 2 couldn't
    /// be put back. <paramref name="restoredEnvironment"/> goes to the Atlas put back, started without a window when
    /// <paramref name="headless"/> (the self-check).
    /// </summary>
    public static async Task<int> SuperviseAsync(string installDir, string dataDir, int newPid, TimeSpan timeout, IDictionary<string, string>? restoredEnvironment = null, bool headless = false)
    {
        string updatesDir = Path.Combine(dataDir, UpdateInstaller.UpdatesFolder);
        string previousDir = Path.Combine(updatesDir, UpdateInstaller.PreviousFolder);
        string failedDir = Path.Combine(updatesDir, UpdateInstaller.FailedFolder);
        Process? process = null;
        try { process = Process.GetProcessById(newPid); }
        catch (ArgumentException) { /* already gone */ }
        bool HasExited()
        {
            if (process == null) return true;
            try { return process.HasExited; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
        }
        var watch = await UpdateInstaller.WatchAsync(updatesDir, HasExited, timeout);
        if (watch == UpdateInstaller.Watch.Started)
        {
            Logger.LogInfo("The updated Atlas started; the previous version stays in " + previousDir + " for Roll back.");
            return 0;
        }
        string reason = watch == UpdateInstaller.Watch.Exited ? "it closed before its window appeared" : "it didn't bring up its window in time";
        Logger.LogWarning("The updated Atlas didn't start (" + reason + "); putting the previous version back.");
        if (watch == UpdateInstaller.Watch.TimedOut && process != null && !HasExited())
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Logger.LogWarning("Couldn't stop it: " + ex.Message); }
        }
        var marker = UpdateInstaller.ReadMarker(updatesDir) ?? new UpdateMarker();
        try
        {
            UpdateInstaller.RollBack(installDir, previousDir, failedDir, marker.ToVersion, line => Logger.LogInfo(line));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.LogError("The previous version couldn't be put back: " + ex.Message);
            return 2;
        }
        UpdateInstaller.WriteMarker(updatesDir, new UpdateMarker { FromVersion = marker.FromVersion, ToVersion = marker.ToVersion, State = UpdateMarker.RolledBack, StartedUtc = marker.StartedUtc, Reason = reason });
        try
        {
            UpdateLauncher.Start(Path.Combine(installDir, UpdateLayout.Exe), restoredEnvironment, headless);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Logger.LogError("The previous version was put back but couldn't be started: " + ex.Message);
        }
        return 1;
    }

    private static bool IsWritable(string dir)
    {
        string probe = Path.Combine(dir, ".atlas-write-check");
        try
        {
            using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The swap is moves, which stay moves only within one drive: across drives they would be copies, and a running program's files can't be copied away.</summary>
    private static bool SameDrive(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return true; // unknown: the swap itself says if a move fails
        }
    }

    private bool HasRoomFor(ReleaseInfo release)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(_installDir!));
            if (string.IsNullOrEmpty(root)) return true;
            return new DriveInfo(root).AvailableFreeSpace >= 3 * release.ZipSize + 100L * 1024 * 1024;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // unknown: the download itself says if the drive fills
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogWarning($"Couldn't remove {dir}: {ex.Message}"); }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string dir in Directory.EnumerateDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
