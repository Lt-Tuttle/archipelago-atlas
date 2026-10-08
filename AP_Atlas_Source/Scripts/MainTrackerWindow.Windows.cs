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
    /// <summary>Opens the Atlas Engine setup window.</summary>
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

    /// <summary>Settings → Privacy &amp; permissions: what the user allowed Atlas to do without asking, and trusted sources.</summary>
    private void OpenPrivacy() => ShowSettings("privacy");
    /// <summary>
    /// The short way to a working engine (Home's checklist, the "logic needs the engine" cards, Settings): the download
    /// asks its permission once, then the setup panel shows the step it's on in plain words. An engine that already works
    /// opens the full window instead, where its parts and games are managed.
    /// </summary>
    public void OpenEngineSetup()
    {
        var engine = AP_Atlas.Core.EngineSetup.AtlasEngine.Current;
        if (engine.CanLaunch && AP_Atlas.Core.EngineSetup.AtlasEngine.ProblemWith(engine) == null)
        {
            OpenEngineWindow();
            return;
        }
        if (engine.Mode != AP_Atlas.Core.EngineSetup.EngineMode.Portable)
        {
            OpenEngineWindow(); // an install of the user's own is chosen and checked there
            return;
        }
        AP_Atlas.UI.PermissionDialog.Ask(this, _appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, null, allowed =>
        {
            if (!allowed) return;
            AP_Atlas.UI.EngineSetupPanel.Open(this, text => Tr(text), lines => OpenEngineWindow(lines));
        });
    }

    /// <summary>The full engine window: every part, the games and the slots, with the log (Tools → Atlas Engine, the bar's bottom button).</summary>
    public void OpenEngineWindow(IReadOnlyList<string> logLines = null)
    {
        var window = AP_Atlas.UI.AtlasEngineWindow.Open(this, _appSettings, () => ActiveSlotNodes().OfType<SlotTrackerControl>(), _appSettings.ContentFontSize);
        window.OpenGamesPage = () => ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Games);
        if (logLines != null && logLines.Count > 0) window.AppendLog(logLines);
    }

    /// <summary>Shows the Map Packs page and searches GitHub for a game's packs: one search, because the user pressed for it (the map's empty state, a game's page).</summary>
    private void FindMapPack(string game)
    {
        ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.MapPacks);
        _packManagerPanel.SearchForGame(game);
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
