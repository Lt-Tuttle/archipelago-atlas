#nullable disable
using System.Linq;
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;

/// <summary>Opening the other windows and tabs: the Pack Doctor, Cheese Tracker and Sphere Tracker settings, Privacy, the engine, race mode.</summary>
public partial class MainTrackerWindow : Control, AP_Atlas.UI.IPropertiesHost
{
    /// <summary>The Pack Doctor found things to review (or an update replaced fixes): log it and offer to open it.</summary>
    private void OnPackReviewSuggested(string packKey, string message)
    {
        LogToSystem(message, "orange");
        if (!AP_Atlas.Core.PopTracker.PackDoctorService.Reports.TryGetValue(packKey, out var report) || report?.Pack == null) return;
        string path = report.Pack.SourcePath;
        ShowToast(message, AP_Atlas.Core.ThemeColors.Warning, "Review", () => OpenPackDoctor(path));
    }
    /// <summary>The Pack Doctor fixed things in a pack by itself: one card says what, with Review fixes (each undoable, and Undo all).</summary>
    private void OnPackAutoFixed(AP_Atlas.Core.PopTracker.PackDoctorService.AutoFixNote note)
    {
        string message = AP_Atlas.Core.PopTracker.AutoFixWords.Card(note.PackName, note.Fixed, note.Left, text => Tr(text));
        LogToSystem(message, "green");
        ShowToast(message, AP_Atlas.Core.ThemeColors.Success, Tr("Review fixes"), () => ReviewFixes(note.Key, note.File));
    }

    /// <summary>Review fixes: what Atlas fixed by itself in a pack, each with Undo, and Undo all.</summary>
    internal void ReviewFixes(string packKey, string zipPath) => AP_Atlas.UI.ReviewFixesDialog.Open(this, packKey, zipPath);

    /// <summary>
    /// "Fix what Atlas can" for a pack, from the card, the Notices, a pack row or the Doctor: what it will change first (the
    /// preview), then the result as a card.
    /// </summary>
    public void FixWhatAtlasCan(string packKey)
    {
        if (!AP_Atlas.Core.PopTracker.PackDoctorService.Reports.TryGetValue(packKey, out var report) || report?.Pack == null) { ShowToast(Tr("The pack hasn't been checked yet."), AP_Atlas.Core.ThemeColors.Warning); return; }
        string name = report.Pack.Manifest?.Name ?? packKey;
        string path = report.Pack.SourcePath;
        var plan = AP_Atlas.Core.PopTracker.PackBulk.Suggested(report, AP_Atlas.Core.PopTracker.PackFixes.Get(packKey));
        AP_Atlas.UI.FixPreviewDialog.Show(this, Tr("Fix what Atlas can: {0}").Replace("{0}", name), plan, report, () =>
        {
            var original = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(path);
            if (original == null) { ShowToast(Tr("Couldn't read that map pack."), AP_Atlas.Core.ThemeColors.Error); return; }
            var result = AP_Atlas.Core.PopTracker.PackDoctorService.ApplyPlan(packKey, original, report, plan);
            ShowToast(result.Describe(name), result.Nothing ? AP_Atlas.Core.ThemeColors.TextMuted : AP_Atlas.Core.ThemeColors.Success, Tr("Open the Doctor"), () => OpenPackDoctor(path, "Overview"));
        }, nothing => ShowToast(name + ": " + nothing, AP_Atlas.Core.ThemeColors.TextMuted));
    }

    /// <summary>Opens the Pack Doctor for a pack zip at a pin (its full path), on the Maps tab with the pin selected.</summary>
    public void OpenPackDoctorAtPin(string zipPath, string pinPath)
    {
        var original = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(zipPath);
        if (original == null) { ShowToast("Couldn't read that map pack.", AP_Atlas.Core.ThemeColors.Error); return; }
        AP_Atlas.UI.PackDoctorWindow.Open(this, original, _appSettings.ContentFontSize, "Maps", pinPath, _appSettings);
    }

    /// <summary>Opens the Pack Doctor for a pack zip.</summary>
    public void OpenPackDoctor(string zipPath, string startTab = null)
    {
        var original = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(zipPath);
        if (original == null) { ShowToast("Couldn't read that map pack.", AP_Atlas.Core.ThemeColors.Error); return; }
        AP_Atlas.UI.PackDoctorWindow.Open(this, original, _appSettings.ContentFontSize, startTab, settings: _appSettings);
    }
    /// <summary>Opens the Cheese Tracker tab's Settings (the API key, links, automatic updates, the site).</summary>
    public void OpenCheeseSettings() => ShowCheeseTab(AP_Atlas.UI.CheeseTrackerTab.SettingsView);
    /// <summary>Switches to the Cheese Tracker tab showing a multiworld (profile id), "My slots" or Settings.</summary>
    public void ShowCheeseTab(string view)
    {
        _cheeseTab.ShowView(view);
        ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.CheeseTracker);
    }
    /// <summary>Switches to the Sphere Tracker tab showing a slot (SphereTrackerTab.SlotView) or its Settings.</summary>
    public void ShowSphereTab(string view)
    {
        _sphereTab.ShowView(view);
        ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.SphereTracker);
    }
    /// <summary>Window → Notifications: everything the alert feed said this session; the open window is brought back if there is one.</summary>
    private void OpenNotifications()
    {
        var open = GetChildren().OfType<AP_Atlas.UI.NotificationsDialog>().FirstOrDefault();
        if (open != null)
        {
            open.GrabFocus();
            return;
        }
        var dialog = new AP_Atlas.UI.NotificationsDialog(_alertLog, _appSettings, text => Tr(text), ShowToast);
        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>Help: the guide, what's new, the credits and the licences, at a topic; the open window is brought back if there is one.</summary>
    private void OpenHelp(string pageId)
    {
        var open = GetChildren().OfType<AP_Atlas.UI.HelpWindow>().FirstOrDefault();
        if (open != null)
        {
            open.Select(pageId);
            open.GrabFocus();
            return;
        }
        new AP_Atlas.UI.HelpWindow(text => Tr(text), url =>
        {
            if (!AP_Atlas.Core.ExternalLinks.OpenWeb(url)) ShowToast(Tr("Couldn't open the link."), AP_Atlas.Core.ThemeColors.Error);
        }).Open(this, pageId);
    }

    /// <summary>A document from outside Atlas's own (an apworld's bundled guide) in the Help window.</summary>
    private void OpenDocument(string id, string title, string markdown)
    {
        OpenHelp(id);
        GetChildren().OfType<AP_Atlas.UI.HelpWindow>().FirstOrDefault()?.ShowDocument(id, title, markdown);
    }

    /// <summary>Settings → Privacy &amp; permissions: what the user allowed Atlas to do without asking, and trusted sources.</summary>
    private void OpenPrivacy() => ShowSettings("privacy");
    /// <summary>
    /// The short way to a working engine (Home's checklist, the "logic needs the engine" cards): the download asks its
    /// permission once, then the setup panel shows the step it's on in plain words. An engine that already works (or an
    /// install of the user's own) shows the Atlas Engine page instead, where its parts are managed.
    /// </summary>
    public void OpenEngineSetup()
    {
        var engine = AP_Atlas.Core.EngineSetup.AtlasEngine.Current;
        if (engine.CanLaunch && AP_Atlas.Core.EngineSetup.AtlasEngine.ProblemWith(engine) == null)
        {
            ShowEnginePage();
            return;
        }
        if (engine.Mode != AP_Atlas.Core.EngineSetup.EngineMode.Portable)
        {
            ShowEnginePage(); // an install of the user's own is chosen and checked there
            return;
        }
        // The engine that ships with Atlas downloads nothing to get ready: no question then.
        if (!AP_Atlas.Core.EngineSetup.AtlasEngine.SetupDownloads(engine))
        {
            AP_Atlas.UI.EngineSetupPanel.Open(this, text => Tr(text), lines => ShowEnginePage(lines));
            return;
        }
        AP_Atlas.UI.PermissionDialog.Ask(this, _appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, null, allowed =>
        {
            if (!allowed) return;
            AP_Atlas.UI.EngineSetupPanel.Open(this, text => Tr(text), lines => ShowEnginePage(lines));
        });
    }

    /// <summary>
    /// The Atlas Engine page (the bar's Engine button, Tools → Atlas Engine, Settings). With <paramref name="logLines"/>
    /// (a run's Show details), its Advanced part open with those lines in its log.
    /// </summary>
    public void ShowEnginePage(IReadOnlyList<string> logLines = null)
    {
        void Show()
        {
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Engine);
            if (logLines != null) _enginePage?.ShowDetails(logLines);
        }
        // Unsaved edits on the Multiworlds page ask first; Cancel leaves the page (and Advanced's fold) as it was.
        if (logLines != null && _currentTool == AP_Atlas.UI.Tool.Connections) GuardUnsaved(Show);
        else Show();
    }

    /// <summary>Verify logic against a seed (Debug, developer mode): the Atlas Engine page with Advanced open on the seed test.</summary>
    private void ShowSeedVerify()
    {
        ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Engine);
        _enginePage?.ShowVerify();
    }

    /// <summary>
    /// Shows the game's page and searches GitHub for its packs: one search, because the user pressed for it (the map's
    /// empty state, a game's page). The results dialog shows over the page; a chosen pack downloads with its progress there.
    /// </summary>
    private void FindMapPack(string game)
    {
        ShowGamePage(game);
        _packManagerPanel.SearchForGame(game);
    }

    /// <summary>Shows the game a pack is for on the Games page, or All map packs when it's for none Atlas lists.</summary>
    public void ShowPackGame(string packZip)
    {
        ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Games);
        _gamesPage?.SelectPackGame(packZip);
    }

    /// <summary>Shows the Games page on a game (its map packs, versions and setup).</summary>
    public void ShowGamePage(string game)
    {
        ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Games);
        if (!string.IsNullOrEmpty(game)) _gamesPage?.Select(game);
    }

    /// <summary>
    /// The map's "Find a pack release that fits this seed": the pack's own project's newest releases, each measured against
    /// the seed's names (the connection's), the newest that fits installed and made the game's pack, so the game's slots
    /// switch to it. A pack whose project isn't known (no GitHub address in its manifest): a GitHub search for the game.
    /// </summary>
    private void FindFittingPackRelease(string game, string packZip)
    {
        var pack = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(packZip);
        string source = AP_Atlas.Core.Reports.SoloTestReport.PublicSource(pack?.Manifest?.VersionsUrl);
        string repo = source != null && source.StartsWith("github.com/", StringComparison.Ordinal) ? source.Substring("github.com/".Length) : null;
        if (repo == null)
        {
            FindMapPack(game);
            return;
        }
        ShowToast(Tr("Trying github.com/{0}'s releases against this seed…").Replace("{0}", repo), AP_Atlas.Core.ThemeColors.Info);
        AP_Atlas.Core.Async.Fire(async () =>
        {
            var (ok, detail, file) = await _packManagerPanel.InstallFittingReleaseAsync(game, repo, null);
            AP_Atlas.UI.Ui.Defer(this, () =>
            {
                if (ok && file != null)
                {
                    _appSettings.PreferredPacks[game] = System.IO.Path.GetFileName(file);
                    DataManager.SaveSettingsSoon(_appSettings);
                    AP_Atlas.Core.PopTracker.PopTrackerPackLoader.NotifyPacksChanged();
                }
                ShowToast(ok ? Tr("{0}'s map pack: {1}.").Replace("{0}", game).Replace("{1}", detail) : Tr("No release of github.com/{0} could be tried: {1}.").Replace("{0}", repo).Replace("{1}", detail),
                    ok ? AP_Atlas.Core.ThemeColors.Success : AP_Atlas.Core.ThemeColors.Warning);
            });
        }, "finding a pack release that fits the seed");
    }
    private static string DescribeRaceMode()
    {
        string when = AP_Atlas.Core.RaceRules.Mode switch
        {
            AP_Atlas.Core.RaceModeSetting.AlwaysOn => "always on",
            AP_Atlas.Core.RaceModeSetting.Off => "off",
            _ => "on in rooms the server marks as races"
        };
        if (AP_Atlas.Core.RaceRules.Mode == AP_Atlas.Core.RaceModeSetting.Off) return "off.";
        return when + (AP_Atlas.Core.RaceRules.HideAllLogic ? "; hides all logic." : "; hides logic explanations.");
    }
    private void ShowRaceModeInfo()
    {
        var dialog = new AcceptDialog
        {
            Title = "Race Mode",
            DialogText =
                "Some races and community events limit which tracker features are allowed. Race mode makes Atlas follow those limits.\n\n" +
                "When race mode is on for a slot:\n" +
                "  • Properties never asks the logic engine WHY a location is or isn't in logic\n" +
                "    (no \"opens with\" items, access rules or region paths).\n\n" +
                "With \"Hide all logic\" also ticked:\n" +
                "  • The Logic Tracker tab is hidden.\n" +
                "  • Map pins show open / hinted / checked only, never in or out of logic.\n" +
                "  • Hints, slot cards and Properties show no in-logic information.\n\n" +
                "Always available: items, checks, hints you paid for, chat, notes and flags.\n" +
                "Never done in any mode: looking up the contents of unchecked locations.\n\n" +
                "\"Follow the server\" turns race mode on automatically in rooms the server marks as races.\n" +
                "If your event's rules are stricter, use \"Always on\" with \"Hide all logic\"."
        };
        dialog.Confirmed += () => dialog.QueueFree();
        dialog.Canceled += () => dialog.QueueFree();
        AddChild(dialog);
        dialog.PopupCentered();
    }
}
