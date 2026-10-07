using System;
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
                dialog.PopupCentered(new Vector2I(680, 0));
            },
            SystemInfo = () => AP_Atlas.UI.AboutDialog.BuildSystemInfo(EngineLineForAbout(), AtlasVersion.Commit)
        };
        _crashReports.Changed += () => _settingsPage?.RefreshRows();
    }

    /// <summary>Once the window is ready: a problem last time is offered. A test or check run drives the service itself.</summary>
    private void StartCrashReports()
    {
        if (UiTestRequested || VisualCheckRequested || SelfCheckPhase != null) return;
        AP_Atlas.Core.Async.Fire(_crashReports!.OfferAsync(), "offering a crash report", tellUser: false);
    }

    /// <summary>Help → Report a problem: the scrubbed bundle is written, and a dialog says where it went and what to do with it.</summary>
    private void ReportProblem()
    {
        string? path = _crashReports!.WriteBundle();
        if (path == null) return;
        var dialog = new AcceptDialog { Title = Tr("Report a problem"), OkButtonText = Tr("Close") };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        box.AddChild(new Label
        {
            Text = Tr("A report was saved as {0} in Atlas's reports folder. It holds the end of the log, the newest crash reports and the system information, with paths, web and e-mail addresses, servers and slot names replaced by marks. Atlas uploads nothing: open the file and check it, then attach it to an issue on GitHub.").Replace("{0}", Path.GetFileName(path)),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(560, 0)
        });
        dialog.AddChild(box);
        dialog.AddButton(Tr("Open the folder"), false, "folder");
        dialog.AddButton(Tr("Open the issues page"), false, "issues");
        dialog.CustomAction += action =>
        {
            string which = action.ToString();
            if (which == "folder" && !ExternalLinks.OpenFolder(_crashReports.ReportsDir)) ShowToast(Tr("Couldn't open the folder."), ThemeColors.Error);
            else if (which == "issues" && !ExternalLinks.OpenWeb(AtlasVersion.RepoUrl + "/issues")) ShowToast(Tr("Couldn't open the link."), ThemeColors.Error);
        };
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(600, 0));
    }
}
