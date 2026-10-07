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
        DataManager.SaveSettings(_appSettings);
        AP_Atlas.Core.ThemeColors.SetPalette(AP_Atlas.Core.ThemeColors.PaletteForSetting(key));
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
        page.AddChoice("appearance", "accent", "Accent colour", "Headings, selections, the status bar and the lit activity bar button.",
            AccentPresets.Select(preset => preset.Name).ToArray(),
            () => Array.FindIndex(AccentPresets, preset => string.Equals(preset.Hex, _appSettings.ThemeAccentColor, StringComparison.OrdinalIgnoreCase)),
            index => ApplyAccent(AccentPresets[index].Hex));
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
        page.AddToggle("window", "debug-log-tab", "Debug Log tab", "In the bottom pane: Atlas's own diagnostics.",
            () => _appSettings.ShowDebugLogTab, on =>
            {
                _appSettings.ShowDebugLogTab = on;
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

        page.AddSection("data", "Data");
        page.AddAction("data", "data-folder", "Atlas's data folder", "Your multiworlds, settings, logs, map packs and the engine live here, and nowhere else.", "Open folder",
            () => _commands!.Run("file.data-folder"));
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
