using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using AP_Atlas.Core.Updates;

/// <summary>
/// Updates: the <see cref="UpdateService"/> wired to the window (the permission dialog, cards, the status bar, the notes
/// dialog, the restart that swaps the files), the Settings section, the supervisor mode an update's previous Atlas runs
/// in, and the release build's self-check of the whole path.
/// </summary>
public partial class MainTrackerWindow
{
    private UpdateService? _updates;
    private Label? _updateStatusLabel;
    private Button? _updateCheckButton, _updateDownloadButton, _updateRestartButton, _updateRollBackButton;

    /// <summary>The release build's self-check (Tools/build_release.ps1): "1" stages a copy of this build and updates into it, "2" is the Atlas that started, "fail" one that ends before its window, "restored" the one put back.</summary>
    private const string SelfCheckVariable = "ATLAS_UPDATE_SELFCHECK";
    private static string? SelfCheckPhase => System.Environment.GetEnvironmentVariable(SelfCheckVariable);

    /// <summary>Test hook: where the updater takes Atlas to be installed (the UI test gives a scratch folder; a development run can't update itself).</summary>
    internal static string? TestInstallDir;

    private static bool SupervisorRequested => UpdateLauncher.ParseSupervisorArgs(OS.GetCmdlineUserArgs()) != null;

    /// <summary>
    /// Supervisor mode (--supervise-update, headless, no claim on the data folder): watches the Atlas an update started
    /// and puts the previous version back if it doesn't bring up its window; then exits.
    /// </summary>
    private void RunSupervisor() => AP_Atlas.Core.Async.Fire(RunSupervisorAsync(), "supervising an update", tellUser: false);

    private async Task RunSupervisorAsync()
    {
        int code = 2;
        try
        {
            var args = UpdateLauncher.ParseSupervisorArgs(OS.GetCmdlineUserArgs())!.Value;
            AP_Atlas.Core.Logger.UseFolder(Path.Combine(args.DataDir, "logs"), "atlas_update_log");
            AP_Atlas.Core.Logger.LogInfo($"Update supervisor ({AP_Atlas.Core.AtlasVersion.Full}): watching process {args.NewPid} in {args.InstallDir}.");
            Dictionary<string, string>? restored = SelfCheckPhase != null ? new Dictionary<string, string> { [SelfCheckVariable] = "restored" } : null;
            code = await UpdateService.SuperviseAsync(args.InstallDir, args.DataDir, args.NewPid, UpdateService.StartTimeout, restored, headless: SelfCheckPhase != null);
        }
        catch (Exception ex)
        {
            AP_Atlas.Core.Logger.LogError("The update supervisor failed: " + ex);
        }
        GetTree().Quit(code);
    }

    /// <summary>The service for this run: a released Atlas can update itself; the editor can't (tests give a scratch folder).</summary>
    private void SetUpUpdates()
    {
        string? installDir = TestInstallDir ?? (OS.HasFeature("editor") ? null : Path.GetDirectoryName(OS.GetExecutablePath()));
        var current = SemVer.TryParse(AP_Atlas.Core.AtlasVersion.Display, out var version) ? version : new SemVer(0, 0, 0);
        _updates = new UpdateService(_appSettings, () => DataManager.SaveSettings(_appSettings), installDir, DataManager.GetDataDirectory(), current)
        {
            PermissionAllowed = () => AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.UpdateChecks),
            AskPermission = AskUpdatePermissionAsync,
            Notice = (text, kind, actionText, action) => ShowToast(text,
                kind == NoticeKind.Error ? AP_Atlas.Core.ThemeColors.Error : kind == NoticeKind.Warning ? AP_Atlas.Core.ThemeColors.Warning : AP_Atlas.Core.ThemeColors.Info,
                actionText, action),
            Status = text => { if (text.Length > 0) ShowStatus(text); },
            ShowRelease = ShowReleaseNotes,
            RequestRestart = RestartToUpdate,
            OpenWeb = url => { if (!AP_Atlas.Core.ExternalLinks.OpenWeb(url)) ShowToast(Tr("Couldn't open the link."), AP_Atlas.Core.ThemeColors.Error); }
        };
        _updates.Changed += () => _settingsPage?.RefreshRows();
    }

    /// <summary>Asks for the update-check permission (Allow once / Always allow / Don't allow), unless already allowed.</summary>
    private Task<bool> AskUpdatePermissionAsync()
    {
        var decided = new TaskCompletionSource<bool>();
        AP_Atlas.UI.PermissionDialog.Ask(this, _appSettings, AP_Atlas.Core.Permissions.UpdateChecks, null, UpdateChecker.ReleasesPage, allowed => decided.TrySetResult(allowed));
        return decided.Task;
    }

    /// <summary>
    /// Once the window is ready: the hand-over note of an update, the self-check's next step, the one-time offer to turn the
    /// daily check on (the second start, never the first), and the daily check itself 90 s on, left alone while a slot
    /// is logging in.
    /// </summary>
    private void StartUpdateChecks()
    {
        if (SelfCheckPhase == "fail")
        {
            // The self-check's Atlas that ends before saying it's ready (the note stays "starting" for the supervisor to see).
            GetTree().Quit(3);
            return;
        }
        _updates!.ConfirmStarted();
        if (SelfCheckPhase is { } phase)
        {
            RunUpdateSelfCheck(phase);
            return;
        }
        _appSettings.StartCount++;
        DataManager.SaveSettings(_appSettings);
        if (!_appSettings.UpdateOffered && !_appSettings.UpdateCheckDaily && _appSettings.StartCount >= 2) OfferUpdateChecks();
        var timer = new Godot.Timer { WaitTime = 90, OneShot = true, Autostart = true };
        timer.Timeout += () =>
        {
            timer.QueueFree();
            if (_connectingSlots.Count == 0) AP_Atlas.Core.Async.Fire(_updates.CheckAsync(manual: false), "checking for a newer Atlas", tellUser: false);
        };
        AddChild(timer);
    }

    /// <summary>The one-time offer, as a card: turning the daily check on asks for the permission first.</summary>
    internal void OfferUpdateChecks()
    {
        _appSettings.UpdateOffered = true;
        DataManager.SaveSettings(_appSettings);
        ShowToast(Tr("Atlas can check GitHub once a day for a newer version. Turn the daily check on?"), AP_Atlas.Core.ThemeColors.Info, Tr("Turn on"),
            () => AP_Atlas.Core.Async.Fire(TurnOnDailyChecksAsync(), "turning the update check on", tellUser: true));
    }

    /// <summary>Turns the daily check on, after the permission (asked if it isn't kept), and checks right away.</summary>
    internal async Task<bool> TurnOnDailyChecksAsync()
    {
        if (!_updates!.PermissionAllowed() && !await AskUpdatePermissionAsync())
        {
            _settingsPage?.RefreshRows();
            return false;
        }
        _appSettings.UpdateCheckDaily = true;
        DataManager.SaveSettings(_appSettings);
        _settingsPage?.RefreshRows();
        await _updates.CheckAsync(manual: true);
        return true;
    }

    private void ShowReleaseNotes(ReleaseInfo release)
    {
        var dialog = new AP_Atlas.UI.UpdateDialog(release, _updates!.Current, text => Tr(text),
            () => AP_Atlas.Core.Async.Fire(_updates.DownloadAndStageAsync(release), "downloading an update", tellUser: true));
        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>Restart to update: with slots connected, asks first; then closes everything, puts the staged version in place, starts it and quits.</summary>
    private void RestartToUpdate()
    {
        string version = _updates!.StagedVersion ?? "?";
        int connected = ActiveSlotNodes().OfType<SlotTrackerControl>().Count();
        if (connected > 0)
        {
            AP_Atlas.UI.Dialogs.Confirm(this, Tr("Restart to update"),
                Tr("Atlas will close {0} connection(s), install version {1} and start again.").Replace("{0}", connected.ToString()).Replace("{1}", version),
                Tr("Restart"), () => GracefulShutdown(ApplyStagedUpdate));
        }
        else GracefulShutdown(ApplyStagedUpdate);
    }

    /// <summary>The swap and the start of the new Atlas, once everything is closed: without a window when this run has none (the self-check passes its phase to the new Atlas).</summary>
    private string? ApplyStagedUpdate() =>
        _updates!.Apply(SelfCheckPhase == "1" ? new Dictionary<string, string> { [SelfCheckVariable] = System.Environment.GetEnvironmentVariable("ATLAS_UPDATE_SELFCHECK_NEW") ?? "2" } : null,
            headless: DisplayServer.GetName() == "headless");

    /// <summary>Roll back: the previous version becomes the staged one, and the same restart puts it in place (watched like any update).</summary>
    private void RollBackToPrevious()
    {
        string? version = _updates!.PreviousVersion;
        if (version == null) return;
        AP_Atlas.UI.Dialogs.Confirm(this, Tr("Go back to the previous version"),
            Tr("Atlas will close its connections, put version {0} back and start again. The version you have now is kept in its place.").Replace("{0}", version),
            Tr("Go back"), () =>
            {
                string? problem = _updates.StagePrevious();
                if (problem != null)
                {
                    ShowToast(problem, AP_Atlas.Core.ThemeColors.Error);
                    return;
                }
                GracefulShutdown(ApplyStagedUpdate);
            });
    }

    /// <summary>
    /// The release build's self-check: phase "1" stages a copy of this build and restarts into it for real (the swap, the
    /// new process, the supervisor, the hand-over); "2" and "restored" are the Atlases that came up and only need to have
    /// confirmed the note (done above) before they quit.
    /// </summary>
    private void RunUpdateSelfCheck(string phase)
    {
        switch (phase)
        {
            case "1":
                string? problem = _updates!.StageCopyOfInstall();
                if (problem != null)
                {
                    AP_Atlas.Core.Logger.LogError("Self-check: " + problem);
                    GetTree().Quit(4);
                    return;
                }
                AP_Atlas.Core.Logger.LogInfo("Self-check: a copy of this build is staged as an update; restarting into it.");
                GracefulShutdown(() =>
                {
                    string? failure = ApplyStagedUpdate();
                    if (failure != null)
                    {
                        AP_Atlas.Core.Logger.LogError("Self-check: " + failure);
                        AP_Atlas.UI.Ui.Defer(this, () => GetTree().Quit(5));
                    }
                    return failure;
                });
                break;
            default:
                AP_Atlas.UI.Ui.Defer(this, () => GetTree().Quit(0));
                break;
        }
    }

    /// <summary>Settings → Updates: the daily check, the channel, and what's known (a newer version, a staged one, the previous one) with its buttons.</summary>
    private void AddUpdateSettings(AP_Atlas.UI.SettingsPage page)
    {
        page.AddSection("updates", "Updates",
            "Atlas asks before it checks GitHub, downloads nothing until you say so, checks every download against the release's SHA-256, never touches your data folder, and keeps the version you had so you can go back.");
        page.AddToggle("updates", "daily-check", "Check for a newer Atlas daily",
            "One small request to GitHub a day at most, for the list of Atlas's releases (your permission is asked the first time).",
            () => _appSettings.UpdateCheckDaily, on =>
            {
                if (on) AP_Atlas.Core.Async.Fire(TurnOnDailyChecksAsync(), "turning the update check on", tellUser: true);
                else
                {
                    _appSettings.UpdateCheckDaily = false;
                    DataManager.SaveSettings(_appSettings);
                }
            });
        var channels = new[] { "auto", "stable", "beta" };
        page.AddChoice("updates", "channel", "Versions", "Stable releases only, or beta versions too. Automatic follows the version you run: a beta takes betas, a release stable ones.",
            new[] { "Automatic", "Stable", "Beta" }, () => Math.Max(0, Array.IndexOf(channels, _appSettings.UpdateChannel)), index =>
            {
                _appSettings.UpdateChannel = channels[index];
                DataManager.SaveSettings(_appSettings);
            });
        var block = new VBoxContainer();
        block.AddThemeConstantOverride("separation", 6);
        _updateStatusLabel = AP_Atlas.UI.Kit.Text("");
        _updateStatusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        block.AddChild(_updateStatusLabel);
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        _updateCheckButton = AP_Atlas.UI.Kit.Button(Tr("Check now"), Tr("Ask GitHub for the list of Atlas's releases (your permission is asked first)"),
            () => AP_Atlas.Core.Async.Fire(_updates!.CheckAsync(manual: true), "checking for a newer Atlas", tellUser: true));
        _updateDownloadButton = AP_Atlas.UI.Kit.Button(Tr("See what's new…"), Tr("The newer version's notes, and the choice to download it"),
            () => { if (_updates?.Available is { } release) ShowReleaseNotes(release); });
        _updateRestartButton = AP_Atlas.UI.Kit.Button(Tr("Restart to update"), Tr("Close Atlas, put the downloaded version in place and start it"), RestartToUpdate);
        _updateRollBackButton = AP_Atlas.UI.Kit.Button(Tr("Go back…"), Tr("Put the previous version back and restart"), RollBackToPrevious);
        foreach (var button in new[] { _updateCheckButton, _updateDownloadButton, _updateRestartButton, _updateRollBackButton }) buttons.AddChild(button);
        block.AddChild(buttons);
        page.AddBlock("updates", "status", "update version newer download install restart go back previous channel check", block, RefreshUpdateBlock);
    }

    private void RefreshUpdateBlock()
    {
        if (_updates == null || _updateStatusLabel == null) return;
        var lines = new List<string> { Tr("This is The Archipelago Atlas {0} ({1} versions).").Replace("{0}", _updates.Current.ToString()).Replace("{1}", _updates.Channel) };
        if (!_updates.CanApply) lines.Add(Tr("A development run can check for versions but not install one."));
        if (_updates.Busy) lines.Add(Tr("Downloading…"));
        else if (_updates.StagedVersion is { } staged) lines.Add(Tr("Version {0} is downloaded, checked and ready to install.").Replace("{0}", staged));
        else if (_updates.Available is { } release) lines.Add(Tr("Version {0} is available.").Replace("{0}", release.Version.ToString()));
        else if (_appSettings.UpdateLastCheckUtc is { } last) lines.Add(Tr("Last checked {0}.").Replace("{0}", last.ToLocalTime().ToString("g"))); // wall clock: a saved time, shown
        if (_updates.PreviousVersion is { } previous) lines.Add(Tr("The previous version, {0}, is kept.").Replace("{0}", previous));
        _updateStatusLabel.Text = string.Join("\n", lines);
        _updateCheckButton!.Disabled = _updates.Busy;
        _updateDownloadButton!.Visible = _updates.Available != null && _updates.StagedVersion == null && !_updates.Busy;
        _updateRestartButton!.Visible = _updates.StagedVersion != null && _updates.CanApply && !_updates.Busy;
        _updateRollBackButton!.Visible = _updates.PreviousVersion != null && _updates.CanApply && !_updates.Busy;
    }
}
