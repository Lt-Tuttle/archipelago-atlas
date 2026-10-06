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
        Add("multiworld.race-mode", "Multiworld", "Race Mode…", "", () => ShowSettings("multiworld"));

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

        // The tools, Ctrl+1 to Ctrl+9 in the tool list's order; a later tool brings its own key (Map Packs: Ctrl+0, Settings: Ctrl+,).
        int number = 1;
        foreach (var tool in AP_Atlas.UI.Tool.All)
        {
            var shown = tool;
            Add("tool." + shown.Id, "Tools", shown.Title, number <= 9 ? "Ctrl+" + number : shown.DefaultKey, () => ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(shown));
            number++;
        }
        Add("tools.engine", "Tools", "Atlas Engine…", "", OpenEngineSetup);
        Add("tools.cheese-settings", "Tools", "Cheese Tracker Settings…", "", OpenCheeseSettings);
        Add("tools.sphere-settings", "Tools", "Sphere Tracker Settings…", "", () => ShowSphereTab(AP_Atlas.UI.SphereTrackerTab.SettingsView));
        Add("tools.privacy", "Tools", "Privacy & Permissions…", "", OpenPrivacy);

        Add("window.notifications", "Window", "Notifications…", "", OpenNotifications);
        Add("window.full-screen", "Window", "Full Screen", "F11", ToggleFullScreen);

        Add("help.guide", "Help", "Guide", "", () => OpenHelp(null));
        Add("help.whats-new", "Help", "What's New", "", () => OpenHelp(AP_Atlas.UI.HelpWindow.WhatsNew));
        Add("help.shortcuts", "Help", "Keyboard Shortcuts", "F1", ShowShortcuts);
        Add("help.race-mode", "Help", "What Does Race Mode Change?", "", ShowRaceModeInfo);
        Add("help.github", "Help", "Atlas on GitHub", "", () =>
        {
            if (!AP_Atlas.Core.ExternalLinks.OpenWeb(AP_Atlas.Core.AtlasVersion.RepoUrl)) ShowToast(Tr("Couldn't open the link."), Colors.Salmon);
        });
        Add("help.credits", "Help", "Credits & Disclaimer", "", () => OpenHelp(AP_Atlas.UI.HelpWindow.Credits));
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
        AddCommandItems(menus["Multiworld"], "multiworld.race-mode");

        AddCommandItems(menus["View"], AP_Atlas.UI.CommandPalette.OwnCommandId);
        menus["View"].AddSeparator();
        AddCommandItems(menus["View"], "view.chat", "view.system-log", "view.debug-log");
        menus["View"].AddSeparator();
        AddCommandCheckItems(menus["View"], PartShown, "view.slots-panel", "view.explorer", "view.properties-panel", "view.bottom-pane", "view.status-bar");
        menus["View"].AddSeparator();
        AddCommandItems(menus["View"], "view.focus-mode");

        AddCommandItems(menus["Tools"], AP_Atlas.UI.Tool.All.Select(t => "tool." + t.Id).ToArray());
        menus["Tools"].AddSeparator();
        AddCommandItems(menus["Tools"], "tools.engine", "tools.cheese-settings", "tools.sphere-settings", "tools.privacy");

        AddCommandItems(menus["Window"], "window.notifications");
        menus["Window"].AddSeparator();
        AddCommandItems(menus["Window"], "window.full-screen");

        AddCommandItems(menus["Help"], "help.guide", "help.whats-new");
        menus["Help"].AddSeparator();
        AddCommandItems(menus["Help"], "help.shortcuts");
        menus["Help"].AddSeparator();
        AddCommandItems(menus["Help"], "help.race-mode", "help.github");
        menus["Help"].AddSeparator();
        AddCommandItems(menus["Help"], "help.credits", "help.about");
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

    /// <summary>Help → About: the version, what sets Atlas apart, where it goes online, and the system information with a Copy button.</summary>
    private void ShowAbout()
    {
        var dialog = new AP_Atlas.UI.AboutDialog(text => Tr(text), EngineLineForAbout(),
            url =>
            {
                if (!AP_Atlas.Core.ExternalLinks.OpenWeb(url)) ShowToast(Tr("Couldn't open the link."), Colors.Salmon);
            },
            () => OpenHelp(null), message => ShowToast(message, Colors.LightGreen));
        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>The logic engine as About names it: its mode and Archipelago's version, or that it isn't set up.</summary>
    private static string EngineLineForAbout()
    {
        var engine = AP_Atlas.Core.EngineSetup.AtlasEngine.Current;
        if (!engine.CanLaunch) return "not set up";
        // The portable engine is the pinned Archipelago; the user's own install isn't named by its path (a path can name the user).
        return engine.Mode == AP_Atlas.Core.EngineSetup.EngineMode.Portable
            ? "Atlas portable engine, Archipelago " + AP_Atlas.Core.EngineSetup.AtlasEngine.ArchipelagoVersion
            : "your Archipelago install";
    }

    /// <summary>Shows the keys as they are now in the menus and on the activity bar (after a rebind).</summary>
    private void RefreshShortcutsShown()
    {
        foreach (var (popup, items) in _commandItems)
        {
            foreach (var (itemId, commandId) in items)
            {
                string shortcut = _commands!.ShortcutOf(commandId);
                popup.SetItemShortcut(popup.GetItemIndex((int)itemId), shortcut.Length > 0 ? AP_Atlas.UI.CommandKeys.ToShortcut(shortcut) : null, false);
            }
        }
        _activityBar?.RefreshKeys();
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
            Text = note + Tr("To change a key: Settings → Keyboard."),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });
        dialog.AddChild(box);
        dialog.AddButton(Tr("Change Keys…"), false, "keys");
        dialog.CustomAction += action =>
        {
            if (action != "keys") return;
            dialog.QueueFree();
            ShowSettings("keyboard");
        };
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>Help → About: the version, what Atlas is and isn't, who made it. (The full About page comes with the shell.)</summary>
}
