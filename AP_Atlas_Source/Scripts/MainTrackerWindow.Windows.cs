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
        LogToSystem($"[color=orange]{message}[/color]");
        if (!AP_Atlas.Core.PopTracker.PackDoctorService.Reports.TryGetValue(packKey, out var report) || report?.Pack == null) return;
        string path = report.Pack.SourcePath;
        ShowToast(message, Godot.Colors.Orange, "Review", () => OpenPackDoctor(path));
    }
    /// <summary>Opens the Pack Doctor for a pack zip.</summary>
    public void OpenPackDoctor(string zipPath, string startTab = null)
    {
        var original = AP_Atlas.Core.PopTracker.PopTrackerPackLoader.InspectZipPack(zipPath);
        if (original == null) { ShowToast("Couldn't read that map pack.", Godot.Colors.Salmon); return; }
        AP_Atlas.UI.PackDoctorWindow.Open(this, original, _appSettings.ContentFontSize, startTab);
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
    /// <summary>Settings → Privacy &amp; permissions: what the user allowed Atlas to do without asking, and trusted sources.</summary>
    private void OpenPrivacy()
    {
        var window = new AP_Atlas.UI.PrivacyWindow(_appSettings, _appSettings.ContentFontSize);
        AddChild(window);
        window.PopupCentered();
    }
    public void OpenEngineSetup()
    {
        AP_Atlas.UI.AtlasEngineWindow.Open(this, _appSettings,
            () => ActiveSlotNodes().OfType<SlotTrackerControl>(),
            () => ActiveSlotNodes().OfType<SlotTrackerControl>().Select(s => s.Game)
                .Concat(_profiles.SelectMany(p => p.SavedStats.Values.Select(st => st.GameName))),
            _appSettings.ContentFontSize);
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
