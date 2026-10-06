using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>
/// The menu bar and the keyboard. Every plain action is a command (AP_Atlas.Core.Commands): a menu item runs it by id, a
/// key by the shortcut it's bound to (the user's rebinds in the settings on top of the defaults), and the command palette
/// will too. Settings with a state (check and radio items) stay menu items of their own until the Settings page exists.
/// Menu titles are English here and translated as they're shown (Tr).
/// </summary>
public partial class MainTrackerWindow
{
    private AP_Atlas.Core.Commands? _commands;

    /// <summary>Every command's menu item: (menu, item id) → command id. Item ids from CommandItemBase up; the stateful items keep small ids.</summary>
    private readonly Dictionary<PopupMenu, Dictionary<long, string>> _commandItems = new();
    private const int CommandItemBase = 1000;

    /// <summary>The menus, in bar order.</summary>
    private static readonly string[] MenuNames = { "File", "Multiworld", "View", "Tools", "Window", "Help" };

    private void RegisterCommands()
    {
        _commands = new AP_Atlas.Core.Commands(() => _appSettings?.KeyBindings);
        Add("file.data-folder", "File", "Open Atlas's Data Folder", "", () =>
        {
            if (!AP_Atlas.Core.ExternalLinks.OpenFolder(DataManager.GetDataDirectory())) ShowToast(Tr("Couldn't open the data folder."), Colors.Salmon);
        });
        Add("file.exit", "File", "Exit", "", GracefulShutdown);

        Add("multiworld.new", "Multiworld", "New Multiworld…", "Ctrl+N", OnAddProfilePressed);
        Add("slot.next", "Multiworld", "Next Slot", "Ctrl+Tab", () => CycleSlot(1));
        Add("slot.previous", "Multiworld", "Previous Slot", "Ctrl+Shift+Tab", () => CycleSlot(-1));

        Add(AP_Atlas.UI.CommandPalette.OwnCommandId, "View", "Command Palette…", "Ctrl+Shift+P", OpenCommandPalette);
        Add("view.chat", "View", "Chat", "", () => ShowTerminalTab(0));
        Add("view.system-log", "View", "System Log", "", () => ShowTerminalTab(1));
        Add("view.debug-log", "View", "Debug Log", "", () => ShowTerminalTab(2));
        Add("view.slots-panel", "View", "Slots Panel", "", () => TogglePart("view.slots-panel"));
        Add("view.explorer", "View", "Explorer", "", () => TogglePart("view.explorer"));
        Add("view.properties-panel", "View", "Properties Panel", "", () => TogglePart("view.properties-panel"));
        Add("view.bottom-pane", "View", "Bottom Pane", "", () => TogglePart("view.bottom-pane"));
        Add("view.status-bar", "View", "Status Bar", "", () => TogglePart("view.status-bar"));
        Add("view.focus-mode", "View", "Focus Mode", "F9", ToggleFocusMode);

        // The tools, Ctrl+1 to Ctrl+9 in the tool list's order.
        int number = 1;
        foreach (var tool in AP_Atlas.UI.Tool.All)
        {
            var shown = tool;
            Add("tool." + shown.Id, "Tools", shown.Title, number <= 9 ? "Ctrl+" + number : "", () => ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(shown));
            number++;
        }
        Add("tools.engine", "Tools", "Atlas Engine…", "", OpenEngineSetup);
        Add("tools.cheese-settings", "Tools", "Cheese Tracker Settings…", "", OpenCheeseSettings);
        Add("tools.sphere-settings", "Tools", "Sphere Tracker Settings…", "", () => ShowSphereTab(AP_Atlas.UI.SphereTrackerTab.SettingsView));
        Add("tools.privacy", "Tools", "Privacy & Permissions…", "", OpenPrivacy);

        Add("window.full-screen", "Window", "Full Screen", "F11", ToggleFullScreen);

        Add("help.shortcuts", "Help", "Keyboard Shortcuts", "F1", ShowShortcuts);
        Add("help.race-mode", "Help", "What Does Race Mode Change?", "", ShowRaceModeInfo);
        Add("help.github", "Help", "Atlas on GitHub", "", () =>
        {
            if (!AP_Atlas.Core.ExternalLinks.OpenWeb(AP_Atlas.Core.AtlasVersion.RepoUrl)) ShowToast(Tr("Couldn't open the link."), Colors.Salmon);
        });
        Add("help.about", "Help", "About The Archipelago Atlas", "", ShowAbout);
    }

    private void Add(string id, string menu, string title, string shortcut, Action run) =>
        _commands!.Add(new AP_Atlas.Core.Command(id, menu, title, shortcut, run));

    /// <summary>Builds the menu bar at the top of the window, from the commands and the stateful settings items.</summary>
    private void BuildMenuBar(Container rootVbox)
    {
        RegisterCommands();
        _menuHbox = new HBoxContainer();
        rootVbox.AddChild(_menuHbox);
        var menus = new Dictionary<string, PopupMenu>();
        foreach (string name in MenuNames)
        {
            // A MenuButton would run its items' keys itself, by the key each item showed at startup, bypassing the user's
            // rebinds: the window runs every key through the commands instead (the items still show their keys).
            var button = new MenuButton { Text = Tr(name) };
            button.SetDisableShortcuts(true);
            _menuHbox.AddChild(button);
            var popup = button.GetPopup();
            menus[name] = popup;
            var items = new Dictionary<long, string>();
            _commandItems[popup] = items;
            popup.IdPressed += id =>
            {
                if (items.TryGetValue(id, out var commandId)) _commands!.Run(commandId);
            };
        }
        AddCommandItems(menus["File"], "file.data-folder");
        menus["File"].AddSeparator();
        AddCommandItems(menus["File"], "file.exit");

        AddCommandItems(menus["Multiworld"], "multiworld.new", "slot.next", "slot.previous");
        menus["Multiworld"].AddSeparator();
        AddMultiworldSettings(menus["Multiworld"]);

        AddCommandItems(menus["View"], AP_Atlas.UI.CommandPalette.OwnCommandId);
        menus["View"].AddSeparator();
        AddCommandItems(menus["View"], "view.chat", "view.system-log", "view.debug-log");
        menus["View"].AddSeparator();
        AddCommandCheckItems(menus["View"], PartShown, "view.slots-panel", "view.explorer", "view.properties-panel", "view.bottom-pane", "view.status-bar");
        menus["View"].AddSeparator();
        AddCommandItems(menus["View"], "view.focus-mode");
        menus["View"].AddSeparator();
        AddViewSettings(menus["View"]);

        AddCommandItems(menus["Tools"], AP_Atlas.UI.Tool.All.Select(t => "tool." + t.Id).ToArray());
        menus["Tools"].AddSeparator();
        AddCommandItems(menus["Tools"], "tools.engine", "tools.cheese-settings", "tools.sphere-settings", "tools.privacy");

        AddCommandItems(menus["Window"], "window.full-screen");

        AddCommandItems(menus["Help"], "help.shortcuts");
        menus["Help"].AddSeparator();
        AddCommandItems(menus["Help"], "help.race-mode", "help.github");
        menus["Help"].AddSeparator();
        AddCommandItems(menus["Help"], "help.about");
    }

    /// <summary>Adds commands to a menu, each with its key shown (the menu's own key handling is off: the window's runs every key).</summary>
    private void AddCommandItems(PopupMenu popup, params string[] commandIds) => AddCommandItems(popup, null, commandIds);

    /// <summary>Adds commands that turn something on and off, as check items whose marks follow <paramref name="isOn"/> as the menu opens.</summary>
    private void AddCommandCheckItems(PopupMenu popup, Func<string, bool> isOn, params string[] commandIds) => AddCommandItems(popup, isOn, commandIds);

    private void AddCommandItems(PopupMenu popup, Func<string, bool>? isOn, string[] commandIds)
    {
        var items = _commandItems[popup];
        foreach (string id in commandIds)
        {
            var command = _commands!.Find(id) ?? throw new InvalidOperationException($"No command is \"{id}\".");
            int itemId = CommandItemBase + _commandItems.Values.Sum(menu => menu.Count);
            if (isOn == null) popup.AddItem(Tr(command.Title), itemId);
            else
            {
                popup.AddCheckItem(Tr(command.Title), itemId);
                popup.AboutToPopup += () => popup.SetItemChecked(popup.GetItemIndex(itemId), isOn(id));
            }
            items[itemId] = id;
            string shortcut = _commands.ShortcutOf(id);
            if (shortcut.Length > 0 && AP_Atlas.UI.CommandKeys.ToShortcut(shortcut) is { } key)
                popup.SetItemShortcut(popup.GetItemIndex(itemId), key, false);
        }
    }

    /// <summary>A key press anywhere in the window that nothing else used: runs the command bound to it.</summary>
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (_commands == null || @event is not InputEventKey key) return;
        string? name = AP_Atlas.UI.CommandKeys.Of(key);
        if (name != null && _commands.RunShortcut(name)) GetViewport().SetInputAsHandled();
    }

    /// <summary>Ctrl+Shift+P: the command palette, one at a time (opened again, it's focused).</summary>
    private void OpenCommandPalette()
    {
        var open = GetChildren().OfType<AP_Atlas.UI.CommandPalette>().FirstOrDefault(p => p.Visible);
        if (open != null)
        {
            open.GrabFocus();
            return;
        }
        var palette = new AP_Atlas.UI.CommandPalette(_commands!, text => Tr(text));
        AddChild(palette);
        SetFontSizeRecursive(palette, _appSettings.GlobalFontSize);
        palette.Open(this);
    }

    private void ShowTerminalTab(int tab)
    {
        if (_bottomTabs == null) return;
        _bottomTabs.CurrentTab = tab;
        _currentTerminalTab = tab;
        RefreshTerminalView();
    }

    private void ToggleFullScreen()
    {
        var window = GetWindow();
        if (window.Mode == Window.ModeEnum.Fullscreen)
        {
            window.Mode = _appSettings.WindowMaximized ? Window.ModeEnum.Maximized : Window.ModeEnum.Windowed;
            return;
        }
        _appSettings.WindowMaximized = window.Mode == Window.ModeEnum.Maximized; // what to come back to
        window.Mode = Window.ModeEnum.Fullscreen;
    }

    /// <summary>Help → Keyboard Shortcuts: every command, its key as it is now, and how to change it (a Settings page is coming).</summary>
    private void ShowShortcuts()
    {
        var dialog = new AcceptDialog { Title = Tr("Keyboard Shortcuts"), OkButtonText = Tr("Close"), MinSize = new Vector2I(600, 460) };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        var tree = new Tree { Columns = 3, HideRoot = true, CustomMinimumSize = new Vector2(560, 360), SizeFlagsVertical = SizeFlags.ExpandFill };
        tree.SetColumnTitlesVisible(true);
        tree.SetColumnTitle(0, Tr("Command"));
        tree.SetColumnTitle(1, Tr("Key"));
        tree.SetColumnTitle(2, Tr("Menu"));
        tree.SetColumnExpand(0, true);
        tree.SetColumnExpand(1, false);
        tree.SetColumnExpand(2, false);
        tree.SetColumnCustomMinimumWidth(1, 130);
        tree.SetColumnCustomMinimumWidth(2, 110);
        var root = tree.CreateItem();
        foreach (var command in _commands!.All)
        {
            var row = tree.CreateItem(root);
            row.SetText(0, Tr(command.Title));
            row.SetText(1, _commands.ShortcutOf(command.Id));
            row.SetText(2, Tr(command.Menu));
            row.SetTooltipText(0, command.Id);
        }
        box.AddChild(tree);
        var conflicts = _commands.Conflicts();
        string note = conflicts.Count == 0 ? "" :
            Tr("Keys bound to more than one command (the first wins): ") + string.Join(", ", conflicts.Select(c => c.Shortcut)) + "\n";
        box.AddChild(new Label
        {
            Text = note + Tr("To change a key, edit KeyBindings in settings.json (a command's id shows when you hover its name): \"tool.map-tracker\": \"Ctrl+3\", or \"\" for no key. A Settings page for this is coming."),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });
        dialog.AddChild(box);
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>Help → About: the version, what Atlas is and isn't, who made it. (The full About page comes with the shell.)</summary>
    private void ShowAbout()
    {
        string commit = AP_Atlas.Core.AtlasVersion.Commit;
        var dialog = new AcceptDialog
        {
            Title = Tr("About The Archipelago Atlas"),
            OkButtonText = Tr("Close"),
            DialogText = $"The Archipelago Atlas {AP_Atlas.Core.AtlasVersion.Display}" + (commit.Length > 0 ? $" ({commit})" : "") + "\n\n" +
                         Tr("An unofficial tracker for Archipelago multiworlds. Not affiliated with or endorsed by Archipelago.") + "\n\n" +
                         Tr("Designed, directed and tested by Lt-Tuttle. Most of its code was written with an AI assistant (Anthropic's Claude).") + "\n" +
                         Tr("Open source under the MIT licence.")
        };
        dialog.AddButton(Tr("Atlas on GitHub"), true, "github");
        dialog.CustomAction += action =>
        {
            if (action == "github") _commands!.Run("help.github");
        };
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered();
    }

    // The settings that live in menus until the Settings page exists: check items, radio items and the submenus they open.

    private void AddMultiworldSettings(PopupMenu menu)
    {
        const int AutoReconnectId = 1, AutoFixApworldId = 2;
        menu.AddCheckItem(Tr("Reconnect dropped connections automatically"), AutoReconnectId);
        menu.SetItemChecked(menu.GetItemIndex(AutoReconnectId), _appSettings.AutoReconnect);
        menu.SetItemTooltip(menu.GetItemIndex(AutoReconnectId), Tr("A few tries over about 20 minutes, then Atlas stops so it never keeps a closed room busy."));
        menu.AddCheckItem(Tr("Use each seed's apworld version automatically"), AutoFixApworldId);
        menu.SetItemChecked(menu.GetItemIndex(AutoFixApworldId), _appSettings.AutoFixApworldVersions);
        menu.SetItemTooltip(menu.GetItemIndex(AutoFixApworldId), Tr("When a seed was made with another version of a game's apworld, Atlas finds that version and uses it for that slot.\nIt asks before looking things up on GitHub, downloads only from sources you've trusted, and never changes your Archipelago install."));
        menu.IdPressed += id =>
        {
            if (id == AutoReconnectId)
            {
                _appSettings.AutoReconnect = !_appSettings.AutoReconnect;
                menu.SetItemChecked(menu.GetItemIndex(AutoReconnectId), _appSettings.AutoReconnect);
                DataManager.SaveSettings(_appSettings);
                _sessions.AutoReconnect = _appSettings.AutoReconnect; // turning it off calls off waiting reconnects
            }
            else if (id == AutoFixApworldId)
            {
                _appSettings.AutoFixApworldVersions = !_appSettings.AutoFixApworldVersions;
                menu.SetItemChecked(menu.GetItemIndex(AutoFixApworldId), _appSettings.AutoFixApworldVersions);
                DataManager.SaveSettings(_appSettings);
            }
        };
        menu.AddSeparator();

        // Race mode: when restrictions apply, and how much they hide.
        const int RaceFollowId = 0, RaceOnId = 1, RaceOffId = 2, RaceHideAllId = 10, RaceInfoId = 20;
        var raceMenu = new PopupMenu { Name = "RaceModeMenu", HideOnCheckableItemSelection = false };
        raceMenu.AddRadioCheckItem(Tr("Follow the server (on in race rooms)"), RaceFollowId);
        raceMenu.AddRadioCheckItem(Tr("Always on"), RaceOnId);
        raceMenu.AddRadioCheckItem(Tr("Off"), RaceOffId);
        raceMenu.AddSeparator();
        raceMenu.AddCheckItem(Tr("Hide all logic while on (not just explanations)"), RaceHideAllId);
        raceMenu.AddSeparator();
        raceMenu.AddItem(Tr("What does race mode change?"), RaceInfoId);
        void RefreshRaceMenu()
        {
            raceMenu.SetItemChecked(raceMenu.GetItemIndex(RaceFollowId), AP_Atlas.Core.RaceRules.Mode == AP_Atlas.Core.RaceModeSetting.FollowServer);
            raceMenu.SetItemChecked(raceMenu.GetItemIndex(RaceOnId), AP_Atlas.Core.RaceRules.Mode == AP_Atlas.Core.RaceModeSetting.AlwaysOn);
            raceMenu.SetItemChecked(raceMenu.GetItemIndex(RaceOffId), AP_Atlas.Core.RaceRules.Mode == AP_Atlas.Core.RaceModeSetting.Off);
            raceMenu.SetItemChecked(raceMenu.GetItemIndex(RaceHideAllId), AP_Atlas.Core.RaceRules.HideAllLogic);
        }
        raceMenu.AboutToPopup += RefreshRaceMenu;
        raceMenu.IdPressed += id =>
        {
            switch (id)
            {
                case RaceFollowId: AP_Atlas.Core.RaceRules.SetMode(AP_Atlas.Core.RaceModeSetting.FollowServer); break;
                case RaceOnId: AP_Atlas.Core.RaceRules.SetMode(AP_Atlas.Core.RaceModeSetting.AlwaysOn); break;
                case RaceOffId: AP_Atlas.Core.RaceRules.SetMode(AP_Atlas.Core.RaceModeSetting.Off); break;
                case RaceHideAllId: AP_Atlas.Core.RaceRules.SetHideAllLogic(!AP_Atlas.Core.RaceRules.HideAllLogic); break;
                case RaceInfoId: ShowRaceModeInfo(); return;
            }
            RefreshRaceMenu();
            LogToSystem($"Race mode: {DescribeRaceMode()}", "orange");
            UpdateSidebar();
        };
        menu.AddChild(raceMenu);
        menu.AddSubmenuNodeItem(Tr("Race Mode"), raceMenu);
    }

    private void AddViewSettings(PopupMenu menu)
    {
        // Which bottom console tabs show.
        var consoleMenu = new PopupMenu { Name = "ConsoleMenu" };
        consoleMenu.AddCheckItem(Tr("System Log"), 100);
        consoleMenu.SetItemChecked(consoleMenu.GetItemIndex(100), true);
        consoleMenu.AddCheckItem(Tr("Debug Log"), 101);
        consoleMenu.SetItemChecked(consoleMenu.GetItemIndex(101), true);
        consoleMenu.IdPressed += id =>
        {
            if (_bottomTabs == null) return;
            int tabIdx = id == 100 ? 1 : 2; // 0 = Chat, 1 = System Log, 2 = Debug Log
            bool nowHidden = !_bottomTabs.IsTabHidden(tabIdx);
            _bottomTabs.SetTabHidden(tabIdx, nowHidden);
            consoleMenu.SetItemChecked(consoleMenu.GetItemIndex((int)id), !nowHidden);
            if (nowHidden && _bottomTabs.CurrentTab == tabIdx) _bottomTabs.CurrentTab = 0;
        };
        menu.AddChild(consoleMenu);
        menu.AddSubmenuNodeItem(Tr("Bottom Console Tabs"), consoleMenu);

        var themeSubMenu = new PopupMenu { Name = "ThemeColorMenu" };
        themeSubMenu.AddItem(Tr("Pikachu Yellow"), 0);
        themeSubMenu.AddItem(Tr("Dark Moon Violet"), 1);
        themeSubMenu.AddItem(Tr("Mario Hat Red"), 2);
        themeSubMenu.AddItem(Tr("Kokiri Green"), 3);
        themeSubMenu.AddItem(Tr("Master Sword Blue"), 4);
        themeSubMenu.AddItem(Tr("Bonfire Orange"), 5);
        themeSubMenu.IdPressed += OnThemeColorMenuPressed;
        menu.AddChild(themeSubMenu);
        menu.AddSubmenuNodeItem(Tr("Theme Accent Color"), themeSubMenu);

        // Menu & Tab font size: drives the menu bar, popups, top tabs and terminal tabs (GlobalFontSize).
        var fontSubMenu = new PopupMenu { Name = "MenuFontMenu", HideOnItemSelection = false };
        const int FontReadoutId = 0, FontIncreaseId = 1, FontDecreaseId = 2, FontResetId = 3;
        fontSubMenu.AddItem("", FontReadoutId);
        fontSubMenu.SetItemDisabled(fontSubMenu.GetItemIndex(FontReadoutId), true);
        fontSubMenu.AddSeparator();
        fontSubMenu.AddItem(Tr("Increase  +"), FontIncreaseId);
        fontSubMenu.AddItem(Tr("Decrease  −"), FontDecreaseId);
        fontSubMenu.AddItem(Tr("Reset to Default (14px)"), FontResetId);
        void RefreshFontReadout() =>
            fontSubMenu.SetItemText(fontSubMenu.GetItemIndex(FontReadoutId), Tr("Current Size: {0}px").Replace("{0}", _appSettings.GlobalFontSize.ToString()));
        RefreshFontReadout();
        fontSubMenu.AboutToPopup += RefreshFontReadout;
        fontSubMenu.IdPressed += id =>
        {
            int size = _appSettings.GlobalFontSize;
            if (id == FontIncreaseId) size = Math.Min(32, size + 1);
            else if (id == FontDecreaseId) size = Math.Max(8, size - 1);
            else if (id == FontResetId) size = 14;
            else return;
            _appSettings.GlobalFontSize = size;
            ApplyUIScale();
            DataManager.SaveSettings(_appSettings);
            RefreshFontReadout();
        };
        menu.AddChild(fontSubMenu);
        menu.AddSubmenuNodeItem(Tr("Menu & Tab Font Size"), fontSubMenu);
    }
}
