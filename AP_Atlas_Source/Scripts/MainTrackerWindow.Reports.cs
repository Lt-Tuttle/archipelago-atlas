using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Godot;
using AP_Atlas.Core;
using AP_Atlas.Core.Reports;
using AP_Atlas.Core.Updates;

/// <summary>
/// Crash reports and Help → Report a problem: the <see cref="CrashReportService"/> wired to the window (the cards, the
/// dialog that shows a report before it goes, the system information), the offer on a start after a problem, and the
/// bundle for an issue.
/// </summary>
public partial class MainTrackerWindow
{
    private CrashReportService? _crashReports;

    /// <summary>Test hook: where reports go (the UI test's fake site), or "" for a build that can't report. Null: as built.</summary>
    internal static string? TestSentryDsn;

    /// <summary>Test hook: where pack fix suggestions go (the UI test's fake site), or "" for a build that can't send them. Null: as built.</summary>
    internal static string? TestFixesDsn;

    /// <summary>The service for this run: a released build reports to the address baked in (or ATLAS_SENTRY_DSN, for checking by hand).</summary>
    private void SetUpCrashReports()
    {
        string dsn = TestSentryDsn ?? System.Environment.GetEnvironmentVariable("ATLAS_SENTRY_DSN") ?? AtlasVersion.SentryDsn;
        string environment = OS.HasFeature("editor") ? "development" : AtlasVersion.Display.Contains('-') ? "beta" : "release";
        var context = new ReportContext(AtlasVersion.Full, environment, OS.GetName(), OS.GetVersion(), RuntimeInformation.FrameworkDescription,
            Godot.Engine.GetVersionInfo()["string"].AsString(), RenderingServer.GetCurrentRenderingMethod(), System.Environment.ProcessorCount);
        _crashReports = new CrashReportService(_appSettings, () => _profiles ?? Enumerable.Empty<MultiworldProfile>(), DataManager.GetDataDirectory(), dsn, context)
        {
            PermissionAllowed = () => Permissions.IsAllowed(_appSettings, Permissions.CrashReports),
            PermissionAlways = () => Permissions.IsAlwaysAllowed(_appSettings, Permissions.CrashReports),
            Card = (text, kind, actionText, action) => ShowToast(text,
                kind == NoticeKind.Error ? ThemeColors.Error : kind == NoticeKind.Warning ? ThemeColors.Warning : ThemeColors.Info, actionText, action),
            ShowDialog = (preview, decided) =>
            {
                var dialog = new AP_Atlas.UI.CrashReportDialog(preview, text => Tr(text), decided);
                AddChild(dialog);
                AP_Atlas.UI.WindowFit.Pop(dialog, 680);
            },
            SystemInfo = () => AP_Atlas.UI.AboutDialog.BuildSystemInfo(EngineLineForAbout(), AtlasVersion.Commit)
        };
        _crashReports.Changed += () => _settingsPage?.RefreshRows();
        _crashReports.SavedProblem = path =>
        {
            if (path != null) ShowProblemResult(path, null);
        };
        // Pack fix suggestions go to their own Sentry project, scrubbed as they always were: they hold no log lines.
        string fixesDsn = TestFixesDsn ?? System.Environment.GetEnvironmentVariable("ATLAS_SENTRY_FIXES_DSN") ?? AtlasVersion.SentryFixesDsn;
        FixSuggestionService.Configure(fixesDsn, () => _crashReports!.NarrowScrubber(), context, _appSettings, Path.Combine(DataManager.GetDataDirectory(), CrashReportService.ReportsFolder));
        FixSuggestionService.Card = (text, kind) => ShowToast(text, kind == NoticeKind.Warning ? ThemeColors.Warning : ThemeColors.Info);
    }

    /// <summary>Once the window is ready: a problem last time is offered. A test or check run drives the service itself.</summary>
    private void StartCrashReports()
    {
        if (UiTestRequested || VisualCheckRequested || SelfCheckPhase != null) return;
        AP_Atlas.Core.Async.Fire(_crashReports!.OfferAsync(), "offering a crash report", tellUser: false);
    }

    /// <summary>
    /// Help → Report a problem: the report's parts, scrubbed, each to tick and view, and the event exactly as it would be
    /// sent; Send (the user's say every time), Just save it (a zip for an issue) or Cancel.
    /// </summary>
    private void ReportProblem()
    {
        var service = _crashReports!;
        // One scrubber for the report: the parts, the note as it's typed, what's sent and what's kept.
        var scrubber = service.Scrubber();
        var parts = service.GatherProblem(scrubber);
        if (parts == null) return;
        string eventId = SentryEnvelope.NewEventId();
        var created = DateTime.UtcNow; // wall clock: a time in a report
        var dialog = new AP_Atlas.UI.ProblemReportDialog(parts, (ticked, note) => service.ProblemPreview(eventId, ticked, note, scrubber, created), service.CannotSendProblem, (choice, ticked, note) =>
        {
            if (choice == AP_Atlas.UI.ProblemChoice.Send)
                AP_Atlas.Core.Async.Fire(SendProblemAsync(service, eventId, ticked, note, scrubber, created), "sending a problem report", tellUser: true);
            else if (choice == AP_Atlas.UI.ProblemChoice.Save && service.SaveProblem(ticked, note, scrubber, null) is { } path)
                ShowProblemResult(path, null);
        });
        AddChild(dialog);
        AP_Atlas.UI.WindowFit.Pop(dialog, 720);
    }

    private async System.Threading.Tasks.Task SendProblemAsync(CrashReportService service, string eventId, IReadOnlyList<ProblemPart> parts, string? note, Scrubber scrubber, DateTime created)
    {
        ShowStatus(Tr("Sending the problem report…"));
        string? id = await service.SendProblemAsync(eventId, parts, note, scrubber, created);
        ShowStatus(id != null ? Tr("The problem report was sent as {0}.").Replace("{0}", id) : Tr("The problem report wasn't sent."));
        if (id != null) ShowProblemResult(null, id);
    }

    /// <summary>Says where a report went: sent, with its id to copy; or saved as a zip, with its folder. The issues page either way.</summary>
    private void ShowProblemResult(string? path, string? reportId)
    {
        var dialog = new AcceptDialog { Title = Tr("Report a problem"), OkButtonText = Tr("Close") };
        dialog.SetMeta("problem_result", reportId ?? "");
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        box.AddChild(new Label
        {
            Text = reportId != null
                ? Tr("Thank you: the report went to Atlas's developer as report {0}. Quote that id if you open an issue on GitHub. A copy stays in Atlas's reports folder.").Replace("{0}", reportId)
                : Tr("The report was saved as {0} in Atlas's reports folder, with paths, web and e-mail addresses, servers, slot and player names replaced by marks. Open the file and check it, then attach it to an issue on GitHub.").Replace("{0}", Path.GetFileName(path ?? "")),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(560, 0)
        });
        dialog.AddChild(box);
        if (reportId != null) dialog.AddButton(Tr("Copy the id"), false, "copy");
        else dialog.AddButton(Tr("Open the folder"), false, "folder");
        dialog.AddButton(Tr("Open the issues page"), false, "issues");
        dialog.CustomAction += action =>
        {
            string which = action.ToString();
            if (which == "copy" && reportId != null)
            {
                DisplayServer.ClipboardSet(reportId);
                ShowToast(Tr("Report id copied."), ThemeColors.Info);
            }
            else if (which == "folder" && !ExternalLinks.OpenFolder(_crashReports!.ReportsDir)) ShowToast(Tr("Couldn't open the folder."), ThemeColors.Error);
            else if (which == "issues" && !ExternalLinks.OpenWeb(AtlasVersion.RepoUrl + "/issues")) ShowToast(Tr("Couldn't open the link."), ThemeColors.Error);
        };
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        AP_Atlas.UI.WindowFit.Pop(dialog, 600);
    }
}
