using System;
using System.Linq;

/// <summary>
/// The Settings page's content (the page itself is <see cref="AP_Atlas.UI.SettingsPage"/>): every setting in sections,
/// each reading and writing its own place in the settings and applying itself at once.
/// </summary>
public partial class MainTrackerWindow
{
    private AP_Atlas.UI.SettingsPage? _settingsPage;
    private AP_Atlas.UI.PrivacyPanel? _privacyPanel;

    private const int MinFontSize = 8, MaxFontSize = 32;

    /// <summary>The accent colours the page offers, the default first.</summary>
    /// <summary>Recolours what's cheap to recolour for the new theme (the frame, headings, lists), remembers it, and says a restart finishes it.</summary>
    private void ApplyTheme(string key)
    {
        if (_appSettings.Theme == key) return;
        _appSettings.Theme = key;
        ApplyPalette();
    }

    /// <summary>Colour-blind-safe colours on or off: the theme's other palette, applied like a theme change.</summary>
    private void ApplyColourBlindSafe(bool on)
    {
        if (_appSettings.ColourBlindSafe == on) return;
        _appSettings.ColourBlindSafe = on;
        ApplyPalette();
    }

    private void ApplyPalette()
    {
        DataManager.SaveSettings(_appSettings);
        AP_Atlas.Core.ThemeColors.SetPalette(AP_Atlas.Core.ThemeColors.PaletteForSetting(_appSettings.Theme, _appSettings.ColourBlindSafe));
        SetupModernTheme();
        AP_Atlas.UI.Kit.RecolourHeadings(GetTree().Root);
        _homePage?.RefreshWordmark();
        RefreshProfileListStyles();
        UpdateSidebar();
        ShowToast(Tr("Theme changed. Restart Atlas to apply it everywhere."), AP_Atlas.Core.ThemeColors.Info);
    }

    internal static readonly (string Name, string Hex)[] AccentPresets =
    {
        ("Atlas Purple (default)", AP_Atlas.Core.ThemeColors.DefaultAccentHex),
        ("Pikachu Yellow", "#FFD700"),
        ("Dark Moon Violet", "#8246DC"),
        ("Mario Hat Red", "#EB2828"),
        ("Kokiri Green", "#3CD250"),
        ("Master Sword Blue", "#32A0FF"),
        ("Bonfire Orange", "#FF781E"),
    };

    /// <summary>The Map Tracker's pin shapes, as the page offers them.</summary>
    /// <summary>What each pin colour means, for its row on the Settings page.</summary>
    private static string MapColourDescription(AP_Atlas.Core.Maps.MapPinState state) => state switch
    {
        AP_Atlas.Core.Maps.MapPinState.InLogic => "Every open check at the pin is in logic (PopTracker's bright green). A pin with some checks in logic is drawn half in this colour, half in the out-of-logic colour.",
        AP_Atlas.Core.Maps.MapPinState.SequenceBreak => "Reachable only with the game's glitch or sequence-break logic (PopTracker's yellow).",
        AP_Atlas.Core.Maps.MapPinState.OutOfLogic => "None of the pin's open checks is in logic yet (PopTracker's red).",
        AP_Atlas.Core.Maps.MapPinState.HintedInLogic => "A hinted check at the pin, and it's in logic (sky blue).",
        AP_Atlas.Core.Maps.MapPinState.HintedOutOfLogic => "A hinted check at the pin, none of them in logic yet (violet).",
        AP_Atlas.Core.Maps.MapPinState.HintedUnknown => "A hinted check at the pin while logic isn't known (lavender).",
        AP_Atlas.Core.Maps.MapPinState.Checked => "Every check at the pin is done (a dark grey).",
        _ => "Logic isn't known: the engine isn't running yet, or logic is hidden (see Settings → Multiworld)."
    };

    private static readonly (string Key, string Title)[] MarkerStyles = { ("round", "Round"), ("square", "Square"), ("diamond", "Diamond") };

    /// <summary>What Atlas opens on, as the page offers it.</summary>
    private static readonly (string Key, string Title)[] StartupPages = { ("home", "Home"), ("last", "Where I left off"), ("multiworlds", "Multiworlds") };

    /// <summary>The race mode choices, in the order the page lists them.</summary>
    private static readonly AP_Atlas.Core.RaceModeSetting[] RaceModes =
    {
        AP_Atlas.Core.RaceModeSetting.FollowServer,
        AP_Atlas.Core.RaceModeSetting.AlwaysOn,
        AP_Atlas.Core.RaceModeSetting.Off,
    };

    private AP_Atlas.UI.SettingsPage BuildSettingsPage()
    {
        var page = new AP_Atlas.UI.SettingsPage(text => Tr(text)) { Visible = false };
        _settingsPage = page;

        page.AddSection("multiworld", "Multiworld");
        page.AddToggle("multiworld", "auto-reconnect", "Reconnect dropped connections automatically",
            "A few tries over about 20 minutes, then Atlas stops, so it never keeps a closed room busy.",
            () => _appSettings.AutoReconnect, on =>
            {
                _appSettings.AutoReconnect = on;
                _sessions.AutoReconnect = on; // turning it off calls off waiting reconnects
                DataManager.SaveSettings(_appSettings);
            });
        page.AddToggle("multiworld", "apworld-versions", "Use each seed's apworld version automatically",
            "When a seed was made with another version of a game's apworld, Atlas finds that version and uses it for that slot. It asks before looking anything up on GitHub, downloads only from sources you've trusted, and never changes your Archipelago install.",
            () => _appSettings.AutoFixApworldVersions, on =>
            {
                _appSettings.AutoFixApworldVersions = on;
                DataManager.SaveSettings(_appSettings);
            });
        page.AddChoice("multiworld", "race-mode", "Race mode", "When Atlas keeps to what a race allows.",
            new[] { "Follow the server (on in race rooms)", "Always on", "Off" },
            () => Array.IndexOf(RaceModes, AP_Atlas.Core.RaceRules.Mode),
            index =>
            {
                AP_Atlas.Core.RaceRules.SetMode(RaceModes[index]);
                ReportRaceMode();
            });
        page.AddToggle("multiworld", "race-hide-all", "Hide all logic while race mode is on", "Not just the explanations.",
            () => AP_Atlas.Core.RaceRules.HideAllLogic, on =>
            {
                AP_Atlas.Core.RaceRules.SetHideAllLogic(on);
                ReportRaceMode();
            });
        page.AddAction("multiworld", "race-info", "What does race mode change?", "", "Show", ShowRaceModeInfo);

        page.AddSection("appearance", "Appearance");
        page.AddChoice("appearance", "theme", "Theme", "Dark, Light or High contrast, or Windows's light or dark mode. The window's frame, headings and lists change at once; everything takes the new colours after a restart.",
            AP_Atlas.Core.ThemeColors.ThemeChoices.Select(choice => choice.Title).ToArray(),
            () => Math.Max(0, Array.FindIndex(AP_Atlas.Core.ThemeColors.ThemeChoices, choice => choice.Key == _appSettings.Theme)),
            index => ApplyTheme(AP_Atlas.Core.ThemeColors.ThemeChoices[index].Key));
        page.AddToggle("appearance", "colour-blind-safe", "Colour-blind-safe colours",
            "Blue for in logic, connected and done, orange for out of logic and errors, and the hint and item kinds told apart the same way, so nothing rests on red against green. Everything takes the new colours after a restart.",
            () => _appSettings.ColourBlindSafe, ApplyColourBlindSafe);
        // The presets, or "Custom" while the accent is one picked below.
        page.AddChoice("appearance", "accent", "Accent colour", "Headings, selections, the status bar and the lit activity bar button.",
            AccentPresets.Select(preset => preset.Name).Append("Custom").ToArray(),
            () =>
            {
                int preset = Array.FindIndex(AccentPresets, preset => string.Equals(preset.Hex, _appSettings.ThemeAccentColor, StringComparison.OrdinalIgnoreCase));
                return preset < 0 ? AccentPresets.Length : preset;
            },
            index =>
            {
                if (index < AccentPresets.Length) ApplyAccent(AccentPresets[index].Hex);
            });
        page.AddColour("appearance", "custom-accent", "Custom accent", "Any colour. Headings and links are lightened or darkened as needed, so they always read.",
            () => AP_Atlas.Core.ThemeColors.Accent,
            colour => ApplyAccent("#" + colour.ToHtml(false).ToUpperInvariant()));
        page.AddChoice("appearance", "ui-zoom", "Zoom", "The whole window, text and pictures alike, relative to Windows' display scale: 100% is the size Windows gives other apps. Ctrl+= and Ctrl+- step through these; Ctrl+0 goes back to 100%.",
            ZoomSteps.Select(step => step + "%").ToArray(),
            () => Array.IndexOf(ZoomSteps, _appSettings.UiZoom),
            index => SetZoom(ZoomSteps[index]));
        page.AddChoice("appearance", "map-markers", "Map pins", "The shape of the Map Tracker's pins. Their size is under the map's Display options.",
            MarkerStyles.Select(style => style.Title).ToArray(),
            () => Math.Max(0, Array.FindIndex(MarkerStyles, style => style.Key == _appSettings.MapMarkerStyle)),
            index =>
            {
                _appSettings.MapMarkerStyle = MarkerStyles[index].Key;
                DataManager.SaveSettings(_appSettings);
                AP_Atlas.UI.MapTrackerControl.RedrawAll();
            });
        // The pins' colours, one row per state (PopTracker's by default), and a way back to them.
        foreach (var state in AP_Atlas.Core.Maps.MapPinLogic.All)
        {
            var shown = state;
            string key = AP_Atlas.Core.Maps.MapPinLogic.Key(state);
            page.AddColour("appearance", "map-colour-" + key, "Map pins: " + AP_Atlas.Core.Maps.MapPinLogic.Title(state).ToLowerInvariant(), MapColourDescription(state),
                () => AP_Atlas.Core.ThemeColors.MapColour(shown),
                colour =>
                {
                    _appSettings.MapColours[key] = "#" + colour.ToHtml(false).ToUpperInvariant();
                    AP_Atlas.Core.ThemeColors.SetMapColours(_appSettings.MapColours);
                    DataManager.SaveSettings(_appSettings);
                    AP_Atlas.UI.MapTrackerControl.RedrawAll();
                });
        }
        page.AddAction("appearance", "map-colours-reset", "Map pins: PopTracker's colours", "Puts every pin colour back to PopTracker's (or, with colour-blind-safe colours on, the safe set).", "Reset", () =>
        {
            _appSettings.MapColours.Clear();
            AP_Atlas.Core.ThemeColors.SetMapColours(_appSettings.MapColours);
            DataManager.SaveSettings(_appSettings);
            AP_Atlas.UI.MapTrackerControl.RedrawAll();
            page.RefreshRows();
        });
        AddFontSizeSetting(page, "menu-font-size", "Menu and tab font size", "The menu bar, the tool header and the bottom pane's tabs (14 to begin with).",
            () => _appSettings.GlobalFontSize, size => _appSettings.GlobalFontSize = size);
        AddFontSizeSetting(page, "slots-font-size", "Slots panel font size", "The slot cards (14 to begin with).",
            () => _appSettings.SlotsFontSize, size => _appSettings.SlotsFontSize = size);
        AddFontSizeSetting(page, "explorer-font-size", "Explorer font size", "The tool's own list beside its content (14 to begin with).",
            () => _appSettings.ExplorerFontSize, size => _appSettings.ExplorerFontSize = size);
        AddFontSizeSetting(page, "content-font-size", "Content font size", "The tools themselves (14 to begin with).",
            () => _appSettings.ContentFontSize, size => _appSettings.ContentFontSize = size);
        AddFontSizeSetting(page, "properties-font-size", "Properties font size", "The Properties panel (14 to begin with).",
            () => _appSettings.PropertiesFontSize, size => _appSettings.PropertiesFontSize = size);
        AddFontSizeSetting(page, "console-font-size", "Bottom pane font size", "Chat and the logs (14 to begin with).",
            () => _appSettings.ConsoleFontSize, size => _appSettings.ConsoleFontSize = size);

        page.AddSection("behaviour", "Behaviour");
        page.AddChoice("behaviour", "startup", "Open on", "What Atlas shows when it starts: Home, the tool you had open when it closed, or Multiworlds.",
            StartupPages.Select(choice => choice.Title).ToArray(),
            () => Math.Max(0, Array.FindIndex(StartupPages, choice => choice.Key == _appSettings.StartupPage)),
            index =>
            {
                _appSettings.StartupPage = StartupPages[index].Key;
                DataManager.SaveSettings(_appSettings);
            });

        page.AddSection("window", "Window");
        AddPartSetting(page, "slots-panel", "Slots panel", "The connected slots' cards, on the left.", "view.slots-panel");
        AddPartSetting(page, "explorer", "Explorer", "The tool's own list beside its content: maps, packs, multiworlds, sections.", "view.explorer");
        AddPartSetting(page, "properties-panel", "Properties panel", "Details of whatever is selected, on the right.", "view.properties-panel");
        AddPartSetting(page, "bottom-pane", "Bottom pane", "Chat, the System Log and the Debug Log.", "view.bottom-pane");
        AddPartSetting(page, "status-bar", "Status bar", "", "view.status-bar");
        page.AddToggle("window", "system-log-tab", "System Log tab", "In the bottom pane: what Atlas did and any trouble it met.",
            () => _appSettings.ShowSystemLogTab, on =>
            {
                _appSettings.ShowSystemLogTab = on;
                DataManager.SaveSettings(_appSettings);
                ApplyConsoleTabs();
            });

        page.AddSection("tools", "Tools");
        page.AddAction("tools", "engine", "Atlas Engine", "The logic engine: its install, version and mode.", "Open…", OpenEngineSetup);
        page.AddAction("tools", "cheese", "Cheese Tracker", "Your API key, links and automatic updates.", "Open…", OpenCheeseSettings);
        page.AddAction("tools", "spheres", "Sphere Tracker", "The host's spheretracker.de room for each multiworld.", "Open…",
            () => ShowSphereTab(AP_Atlas.UI.SphereTrackerTab.SettingsView));

        page.AddSection("privacy", "Privacy & permissions", "Atlas never looks or writes outside its own folder, or goes online for something new, without asking: Allow once, Always allow or Don't allow. Every answer it keeps is listed here and can be taken back.");
        _privacyPanel = new AP_Atlas.UI.PrivacyPanel(_appSettings, text => Tr(text));
        page.AddBlock("privacy", "privacy", "permissions allowed trusted apworld sources online sites GitHub Cheese Tracker spheretracker", _privacyPanel, _privacyPanel.Refresh);

        page.AddSection("keyboard", "Keyboard", "Every command and its key. Press a key's button, then the key you want: Backspace for no key, Escape to keep it. A key bound to two commands runs the first.");
        foreach (var command in _commands!.All)
        {
            var bound = command;
            page.AddKey("keyboard", "key." + bound.Id, bound.Title, bound.Menu,
                () => _commands.ShortcutOf(bound.Id), () => bound.DefaultShortcut, key => SetKeyBinding(bound.Id, key), () => ConflictsOf(bound.Id));
        }

        AddUpdateSettings(page);

        page.AddSection("advanced", "Advanced");
        page.AddToggle("advanced", "developer-mode", "Developer mode",
            "Shows Atlas's diagnostics: the Debug Log tab in the bottom pane, frame hitch warnings on the status bar and the Debug Log's menu items. Everything is written to the log file either way.",
            () => _appSettings.DeveloperMode, on =>
            {
                _appSettings.DeveloperMode = on;
                DataManager.SaveSettings(_appSettings);
                ApplyConsoleTabs();
            });

        page.AddSection("data", "Data");
        page.AddAction("data", "data-folder", "Atlas's data folder",
            "Your multiworlds, settings, logs, map packs and the engine live in " + DataManager.GetDataDirectory() + ", and nowhere else." + (_dataFolderWhy == null ? "" : " " + _dataFolderWhy),
            "Open folder", () => _commands!.Run("file.data-folder"));
        return page;
    }

    private void AddFontSizeSetting(AP_Atlas.UI.SettingsPage page, string id, string title, string description, Func<int> get, Action<int> set)
    {
        page.AddNumber("appearance", id, title, description, MinFontSize, MaxFontSize, get, size =>
        {
            set(size);
            ApplyUIScale();
            DataManager.SaveSettings(_appSettings);
        });
    }

    /// <summary>A window part's toggle: the same setting the View menu's check item changes.</summary>
    private void AddPartSetting(AP_Atlas.UI.SettingsPage page, string id, string title, string description, string commandId) =>
        page.AddToggle("window", id, title, description, () => PartShown(commandId), on => SetPart(commandId, on));

    /// <summary>Binds a command to a key ("" for none), or back to its default (null), and shows the keys as they are now.</summary>
    private void SetKeyBinding(string commandId, string? key)
    {
        if (key == null) _appSettings.KeyBindings.Remove(commandId);
        else _appSettings.KeyBindings[commandId] = key;
        DataManager.SaveSettings(_appSettings);
        RefreshShortcutsShown();
    }

    /// <summary>The other commands bound to a command's key, by title; "" while it has the key to itself.</summary>
    private string ConflictsOf(string commandId)
    {
        string key = _commands!.ShortcutOf(commandId);
        if (key.Length == 0) return "";
        return string.Join(", ", _commands.All.Where(other => other.Id != commandId && _commands.ShortcutOf(other.Id) == key).Select(other => Tr(other.Title)));
    }

    private void ReportRaceMode()
    {
        LogToSystem($"Race mode: {DescribeRaceMode()}", "orange");
        UpdateSidebar();
    }

    /// <summary>Shows the Settings page, at a section when one is named (a tool's gear button, Multiworld → Race Mode…).</summary>
    private void ShowSettings(string? sectionId = null)
    {
        if (_currentTool == AP_Atlas.UI.Tool.Settings) _settingsPage?.OnShown(); // drawn again: something may have changed meanwhile
        else ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Settings);
        if (sectionId != null) _settingsPage?.ShowSection(sectionId);
    }
}
