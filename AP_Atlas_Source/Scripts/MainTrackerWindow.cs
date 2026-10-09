#nullable disable
using System.Linq;
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;

public partial class MainTrackerWindow : Control, AP_Atlas.UI.IPropertiesHost
{
    private HSplitContainer _mainSplit;
    private PanelContainer _sidebar;
    private PanelContainer _contentStage;
    private PanelContainer _terminalStage;
    private VBoxContainer _activeSessionsList;
    private Control _connectionPanel;
    private AP_Atlas.Core.MapPackManagerControl _packManagerPanel;
    // Connection Tab
    private VBoxContainer _profileListContainer;
    private Control _connectionSidebarContent;
    private VBoxContainer _midLeftVBox;

    private LineEdit _nameInput;
    private LineEdit _serverInput;
    private LineEdit _passwordInput;
    private VBoxContainer _slotsListVBox;
    private Button _addSlotButton;
    private Button _addYamlButton;
    private Button _connectAllBtn;
    private Button _saveButton;
    private Button _deleteButton;
    private Label _statusLabel;
    private Control _connectingOverlay;
    private AP_Atlas.UI.SafeRichText _consoleOutput;
    private AP_Atlas.UI.LogPane _systemLog, _debugLog;
    private Label _globalStatusLabel;
    /// <summary>The status bar's right end: how many slots are connected.</summary>
    private Label _statusConnectedLabel;
    private HBoxContainer _menuHbox;
    private PanelContainer _globalStatusBar;
    private AppSettings _appSettings;
    /// <summary>The tool whose tab is showing.</summary>
    private AP_Atlas.UI.Tool _currentTool = AP_Atlas.UI.Tool.Home;
    private readonly AP_Atlas.Core.AlertLog _alertLog = new();
    private AP_Atlas.UI.AlertFeed _alerts;
    /// <summary>The views of the tools that aren't per slot: the view, its explorer content and title, and what to do when it's shown.</summary>
    private Dictionary<AP_Atlas.UI.Tool, (Control View, Control Explorer, string ExplorerTitle, Action Shown)> _toolViews = new();
    private int _currentTerminalTab = 0;
    private AP_Atlas.UI.ActivityBar _activityBar;
    private Label _toolTitle;
    private PanelContainer _midLeftSidebar;
    private StyleBoxFlat _midLeftStyle;
    private StyleBoxFlat _contentStageStyle;
    private PanelContainer _propertiesSidebar;
    private Label _midLeftTitle;
    private VBoxContainer _midLeftContent;
    private HSplitContainer _explorerSplit; // explorer | content; its offset is the shown tool's own (ApplyExplorerOffset)
    private AP_Atlas.UI.PropertiesPanel _propertiesPanel;
    private List<MultiworldProfile> _profiles = new();
    private AP_Atlas.Core.CheeseTracker.CheeseTrackerService _cheese;
    private LineEdit _cheeseInput;
    private LineEdit _sphereInput;
    private AP_Atlas.UI.GamesPage _gamesPage;
    private Button _passwordToggle;
    private AP_Atlas.UI.CheeseTrackerTab _cheeseTab;
    private AP_Atlas.Core.Spheres.SphereService _spheres;
    private AP_Atlas.UI.SphereTrackerTab _sphereTab;
    private MultiworldProfile _selectedProfile = null;
    private VSplitContainer _contentSplit;
    private AP_Atlas.UI.SafeRichText _debugLogConsole;

    // Set when _Ready subscribes to the shared services, so _ExitTree undoes exactly what _Ready did.
    private bool _subscribed;

    public override void _ExitTree()
    {
        AP_Atlas.Core.GodotLog.Uninstall();
        // A self-test or a refused test run never subscribed. Touching the services here would start them, and the
        // logger would create its folder in a data folder the run had refused.
        if (!_subscribed) return;
        AP_Atlas.Core.SafeFile.Recovered -= OnFileRecovered;
        DataManager.SaveFailed -= OnSaveFailed;
        AP_Atlas.Core.Async.Failed -= OnBackgroundWorkFailed;
        AP_Atlas.Core.Logger.OnLogMessage -= OnLogMessageReceived;
        AP_Atlas.Core.Annotations.Changed -= OnAnnotationsChanged;
        AP_Atlas.Core.PopTracker.PackDoctorService.ReviewSuggested -= OnPackReviewSuggested;
        AP_Atlas.Core.PopTracker.PackDoctorService.FixSuggested -= OnPackFixSuggested;
        AP_Atlas.Core.EngineSetup.AtlasEngine.PartsChanged -= OnEnginePartsChanged;
        AP_Atlas.Core.EngineSetup.AtlasEngine.Changed -= OnEngineChangedForHome;
        AP_Atlas.Core.EngineSetup.SoloTestRunner.StateChanged -= OnSoloTestChanged;
    }

    // Slot cards show special-item progress, so redraw them when special marks change.
    private void OnAnnotationsChanged() => AP_Atlas.UI.Ui.Defer(this, UpdateSidebar);

    public override void _Ready()
    {
        // Log lines also go to Godot's output (the log file starts once the data folder has been checked, below).
        AP_Atlas.Core.Logger.Echo = line => GD.Print(line);
        AP_Atlas.Core.Logger.EchoError = line => GD.PrintErr(line);
        // Godot's own errors go to Atlas's log too (Godot's log file is off: it would be kept outside Atlas's folder).
        AP_Atlas.Core.GodotLog.Install();
        AP_Atlas.Core.CrashGuard.Install();
        if (AP_Atlas.Core.SelfTest.Requested)
        {
            RunSelfTest();
            return;
        }
        // The update supervisor (the Atlas an update replaced, started headless): no window, no claim on the data folder.
        if (SupervisorRequested)
        {
            RunSupervisor();
            return;
        }
        // The visual check (testing only) refuses a real data folder before anything in it is read or written.
        if (VisualCheckRequested && !VisualCheckAllowed()) return;
        // The UI test, too, refuses a real data folder before anything in it is read or written.
        if (UiTestRequested && !UiTestAllowed()) return;
        // The data folder first: nothing below reads or writes before it's settled (a folder that can't be written asks).
        SettleDataFolder(ContinueStartup);
    }

    private void ContinueStartup()
    {
        if (!AP_Atlas.Core.CrashGuard.TryAcquireInstance())
        {
            // An earlier Atlas whose window is gone but whose process lives on holds the folder: offer to end it (a built
            // Atlas only: from the editor, the program file is Godot's, shared with other projects).
            var leftovers = OS.HasFeature("editor") ? new List<int>() : AP_Atlas.Core.InstanceCheck.LeftoversToEnd(AP_Atlas.Core.CrashGuard.Twins());
            if (leftovers.Count > 0)
            {
                AP_Atlas.UI.Dialogs.Confirm(this, Tr("Atlas didn't finish closing"),
                    Tr("An earlier Atlas from this folder is still running without a window: it didn't finish closing last time, and it holds your data folder. End it and start? Everything it had to save was saved as it closed."),
                    Tr("End it and start"), () =>
                    {
                        if (AP_Atlas.Core.CrashGuard.EndLeftoversAndAcquire(leftovers)) ContinueStartup();
                        else
                        {
                            OS.Alert("The earlier Atlas couldn't be ended. Close it in Task Manager (it's listed as The Archipelago Atlas), then start Atlas again.", "Atlas is already open");
                            GetTree().Quit();
                        }
                    }, onCancel: () => GetTree().Quit());
                return;
            }
            OS.Alert("The Archipelago Atlas is already running from this folder.\n\nOnly one copy can use the same data at a time, " +
                     "so your profiles and notes can't be overwritten by a second window.", "Atlas is already open");
            GetTree().Quit();
            return;
        }
        AP_Atlas.Core.Logger.UseFolder(System.IO.Path.Combine(DataManager.GetDataDirectory(), "logs"));
        // Damaged-file recoveries and failed saves are shown as toasts once the UI exists.
        _subscribed = true;
        AP_Atlas.Core.SafeFile.Recovered += OnFileRecovered;
        DataManager.SaveFailed += OnSaveFailed;
        AP_Atlas.Core.Async.Failed += OnBackgroundWorkFailed;
        GetTree().AutoAcceptQuit = false;
        AP_Atlas.Core.Logger.OnLogMessage += OnLogMessageReceived;
        AP_Atlas.Core.Annotations.Changed += OnAnnotationsChanged;
        AP_Atlas.Core.Logger.LogInfo($"The Archipelago Atlas {AP_Atlas.Core.AtlasVersion.Full} started.");
        LogDataFolder();
        // Games' names nobody has used for three months (older versions pile up as games update).
        var dataPackages = DataManager.DataPackages;
        AP_Atlas.Core.Async.Fire(System.Threading.Tasks.Task.Run(() => dataPackages.RemoveUnused(System.TimeSpan.FromDays(90))),
            "tidying the stored game names", tellUser: false);
        GetWindow().Title = "The Archipelago Atlas " + AP_Atlas.Core.AtlasVersion.Display;
        _appSettings = DataManager.LoadSettings();
        // The visual check pictures one theme at a time (dark unless asked), never the PC's Windows mode.
        if (VisualCheckRequested) _appSettings.Theme = System.Environment.GetEnvironmentVariable("ATLAS_VISUALCHECK_THEME") is { Length: > 0 } theme ? theme : "dark";
        AP_Atlas.Core.ThemeColors.SetPalette(AP_Atlas.Core.ThemeColors.PaletteForSetting(_appSettings.Theme, _appSettings.ColourBlindSafe));
        AP_Atlas.Core.ThemeColors.SetMapColours(_appSettings.MapColours);
        AP_Atlas.UI.MapTrackerControl.Translate = text => Tr(text);
        AP_Atlas.UI.Kit.Translate = text => Tr(text);
        // Every dialog and window fits the screen it opens on, and the window follows Windows' display scale.
        AP_Atlas.UI.WindowFit.Watch(GetTree(), () => _appSettings);
        MigrateZoom();
        ApplyZoom();
        // The alert feed: what Atlas tells the user, stacked at the bottom right, kept for Window → Notifications.
        _alerts = new AP_Atlas.UI.AlertFeed(_alertLog, text => Tr(text), () => _appSettings.GlobalFontSize, VisualCheckRequested);
        AddChild(_alerts);
        AP_Atlas.Core.ThemeColors.SetAccent(_appSettings.ThemeAccentColor);
        AP_Atlas.Core.RaceRules.Initialize(_appSettings);
        SetUpUpdates();
        SetUpCrashReports();
        AP_Atlas.Core.EngineSetup.AtlasEngine.Initialize(_appSettings);
        // Before any slot exists: a changed engine part retires the pools' processes first, then the slots restart.
        EnginePools.Initialize();
        AP_Atlas.Core.EngineSetup.AtlasEngine.PartsChanged += OnEnginePartsChanged;
        AP_Atlas.Core.EngineSetup.AtlasEngine.Changed += OnEngineChangedForHome;
        AP_Atlas.Core.EngineSetup.SoloTestRunner.StateChanged += OnSoloTestChanged;
        AP_Atlas.Core.PopTracker.PackDoctorService.Initialize(_appSettings);
        // The Doctor's Recommended tab puts the rows about a connected slot's seed first.
        AP_Atlas.Core.PopTracker.PackDoctorService.SeedLocations = game => ActiveSlotNodes().OfType<SlotTrackerControl>()
            .Where(s => GodotObject.IsInstanceValid(s) && s.Session?.Locations != null && string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s.Session.Locations.AllLocations).ToList();
        AP_Atlas.Core.PopTracker.PackDoctorService.ReviewSuggested += OnPackReviewSuggested;
        AP_Atlas.Core.PopTracker.PackDoctorService.FixSuggested += OnPackFixSuggested;
        _profiles = DataManager.LoadProfiles();
        StartSessions();
        // Services read the slots themselves (their models), never their panels.
        _cheese = new AP_Atlas.Core.CheeseTracker.CheeseTrackerService(_appSettings, () => _profiles, SlotModels, () => DataManager.SaveProfiles(_profiles));
        AddChild(_cheese);
        _cheese.Notice += message => ShowToast(message, AP_Atlas.Core.ThemeColors.Warning);
        _cheese.Changed += () => _propertiesPanel?.QueueRefresh();
        _cheese.LinkChanged += id =>
        {
            if (_selectedProfile?.Id == id && _cheeseInput != null) _cheeseInput.Text = _selectedProfile.CheeseTrackerUrl ?? "";
        };
        // Cheese Tracker knows each linked multiworld's Archipelago tracker and organizer: a host's sphere room must be for
        // that tracker, and is taken as the host's when its creator is that organizer.
        _spheres = new AP_Atlas.Core.Spheres.SphereService(() => _profiles, SlotModels,
            () => DataManager.SaveProfiles(_profiles), p =>
            {
                var tracker = _cheese?.RoomView(p.Id)?.Tracker;
                return (tracker?.UpstreamUrl, tracker?.OwnerName);
            });
        // The window where it was (kept within its screen, whole), or its first size and place at Windows' scale.
        var window = GetTree().Root;
        AP_Atlas.UI.WindowFit.PlaceRoot(window, _appSettings, _appSettings.Fresh);
        if (_appSettings.WindowMaximized)
        {
            window.Mode = Window.ModeEnum.Maximized;
        }
        window.SizeChanged += () => AP_Atlas.UI.Ui.DeferQuiet(this, () => ApplyWindowParts());
        SetAnchorsPreset(LayoutPreset.FullRect);
        var rootVbox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rootVbox.SetAnchorsPreset(LayoutPreset.FullRect);
        rootVbox.AddThemeConstantOverride("separation", 0);
        AddChild(rootVbox);
        BuildMenuBar(rootVbox);
        _iconConnect = CreateIconFromSvg(@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#E0E0E0"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71""></path><path d=""M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71""></path></svg>");
        _iconCheck = CreateIconFromSvg(@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#4ade80"" stroke-width=""3"" stroke-linecap=""round"" stroke-linejoin=""round""><polyline points=""20 6 9 17 4 12""></polyline></svg>");
        _iconDisconnect = CreateIconFromSvg(@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""-4 -4 32 32"" fill=""none"" stroke=""#E0E0E0"" stroke-width=""2.5"" stroke-linecap=""round"" stroke-linejoin=""round""><line x1=""2"" y1=""2"" x2=""22"" y2=""22""></line><path d=""M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71""></path><path d=""M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71""></path></svg>");
        _iconDelete = CreateIconFromSvg(@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#f87171"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><polyline points=""3 6 5 6 21 6""></polyline><path d=""M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2""></path><line x1=""10"" y1=""11"" x2=""10"" y2=""17""></line><line x1=""14"" y1=""11"" x2=""14"" y2=""17""></line></svg>");
        var interiorMargin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        interiorMargin.AddThemeConstantOverride("margin_left", 8);
        interiorMargin.AddThemeConstantOverride("margin_top", 8);
        interiorMargin.AddThemeConstantOverride("margin_right", 8);
        interiorMargin.AddThemeConstantOverride("margin_bottom", 8);
        rootVbox.AddChild(interiorMargin);
        var appWorkspaceHBox = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        appWorkspaceHBox.AddThemeConstantOverride("separation", 8);
        interiorMargin.AddChild(appWorkspaceHBox);
        // --- 0. THE ACTIVITY BAR: every tool, in its group; the lit one is the tool the content area shows ---
        _activityBar = new AP_Atlas.UI.ActivityBar(text => Tr(text), tool => _commands.ShortcutOf("tool." + tool.Id));
        _activityBar.ToolPressed += tool => ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(tool);
        _activityBar.EnginePressed += () => OpenEngineWindow();
        appWorkspaceHBox.AddChild(_activityBar);
        _mainSplit = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffsets = new[] { _appSettings.MainSplitOffset } };
        _mainSplit.Dragged += (offset) => { _appSettings.MainSplitOffset = (int)offset; DataManager.SaveSettingsSoon(_appSettings); };
        _mainSplit.AddThemeConstantOverride("separation", 8);
        appWorkspaceHBox.AddChild(_mainSplit);
        // --- 1. FAR LEFT SLOTS SIDEBAR ---
        _sidebar = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(SlotsMinWidth, 0) };
        _sidebar.AddThemeStyleboxOverride("panel", GetVSCodePanelStyle());
        _mainSplit.AddChild(_sidebar);
        var sidebarMargin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sidebarMargin.AddThemeConstantOverride("margin_left", 8);
        sidebarMargin.AddThemeConstantOverride("margin_top", 8);
        sidebarMargin.AddThemeConstantOverride("margin_right", 8);
        sidebarMargin.AddThemeConstantOverride("margin_bottom", 8);
        _sidebar.AddChild(sidebarMargin);
        var sidebarVBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sidebarMargin.AddChild(sidebarVBox);
        _sidebarHeaderBox = CreateSidebarHeader("SLOTS", out _sidebarTitle, out _sidebarMenuBtn,
            () => _appSettings.SlotsFontSize,
            (newSize) => { _appSettings.SlotsFontSize = newSize; ApplyUIScale(); DataManager.SaveSettings(_appSettings); }
        );
        sidebarVBox.AddChild(_sidebarHeaderBox);
        var sessionScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sidebarVBox.AddChild(sessionScroll);
        _activeSessionsList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sessionScroll.AddChild(_activeSessionsList);
        // --- 2. REST OF LAYOUT ---
        // Layout: [slots] | [tabs over (explorer | content) over terminal] | [properties].
        // The explorer lives inside the content area, so showing or hiding it only resizes the content stage:
        // the tabs and terminal never move, and the tab bar's menu sits at the content's top-right corner.
        // Properties starts wider than its minimum on a fresh settings file (a negative offset widens the second pane: 0 is
        // the first pane's end, clamped to the second's minimum); the user's own drag is kept after that.
        if (_appSettings.Fresh && _appSettings.SplitCenterRightOffset == 0) _appSettings.SplitCenterRightOffset = -PropertiesStartWidth;
        var centerRightSplit = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffsets = new[] { _appSettings.SplitCenterRightOffset } };
        centerRightSplit.Dragged += (offset) => { _appSettings.SplitCenterRightOffset = (int)offset; DataManager.SaveSettingsSoon(_appSettings); };
        centerRightSplit.AddThemeConstantOverride("separation", 8);
        _mainSplit.AddChild(centerRightSplit);
        var rightColumn = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        centerRightSplit.AddChild(rightColumn);
        var rightOfSidebarSplit = _explorerSplit = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffsets = new[] { _appSettings.SplitRightSidebarOffset } };
        // A drag is kept for the tool that shows: each tool's explorer has its own width (ApplyExplorerOffset).
        rightOfSidebarSplit.Dragged += (offset) =>
        {
            if (_currentTool != null) _appSettings.ExplorerSplitOffsets[_currentTool.Id] = (int)offset;
            DataManager.SaveSettingsSoon(_appSettings);
        };
        rightOfSidebarSplit.AddThemeConstantOverride("separation", 8);
        // --- 3. MID LEFT EXPLORER SIDEBAR ---
        _midLeftSidebar = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(ExplorerMinWidth, 0), Visible = false };
        _midLeftStyle = GetVSCodePanelStyle();
        _midLeftStyle.CornerRadiusTopLeft = 0; // Seamless connection to the tab bar while the explorer is shown
        _midLeftSidebar.AddThemeStyleboxOverride("panel", _midLeftStyle);
        rightOfSidebarSplit.AddChild(_midLeftSidebar);
        var midLeftMargin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        midLeftMargin.AddThemeConstantOverride("margin_left", 8);
        midLeftMargin.AddThemeConstantOverride("margin_top", 8);
        midLeftMargin.AddThemeConstantOverride("margin_right", 8);
        midLeftMargin.AddThemeConstantOverride("margin_bottom", 8);
        _midLeftSidebar.AddChild(midLeftMargin);
        _midLeftVBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        midLeftMargin.AddChild(_midLeftVBox);
        _midLeftHeaderBox = CreateSidebarHeader("EXPLORER", out _midLeftTitle, out Button midLeftMenuBtn,
            () => _appSettings.ExplorerFontSize,
            (newSize) => { _appSettings.ExplorerFontSize = newSize; ApplyUIScale(); DataManager.SaveSettings(_appSettings); }
        );
        _midLeftVBox.AddChild(_midLeftHeaderBox);
        _midLeftContent = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _midLeftVBox.AddChild(_midLeftContent);
        // --- 4. CENTER STAGE AND BOTTOM TABS ---
        // --- 5. GLOBAL TAB BAR ---
        // The tool header: which tool the content area shows (the activity bar switches it).
        var globalTabHBox = _toolHeader = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _toolTitle = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
        _toolTitle.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.TextMuted);
        _toolTitle.AddThemeConstantOverride("margin_left", 8);
        globalTabHBox.AddChild(_toolTitle);
        globalTabHBox.AddChild(BuildSlotPicker());
        var contentMenuBtn = new Button { Text = "...", ThemeTypeVariation = "QuietButton", AccessibilityName = Tr("More options") };
        contentMenuBtn.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.TextMuted);
        AttachFontMenuPopup(contentMenuBtn,
            () => _appSettings.ContentFontSize,
            (newSize) => { _appSettings.ContentFontSize = newSize; ApplyUIScale(); DataManager.SaveSettings(_appSettings); }
        );
        globalTabHBox.AddChild(contentMenuBtn);
        rightColumn.AddChild(globalTabHBox);
        _contentSplit = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffsets = new[] { _appSettings.SplitContentOffset } };
        _contentSplit.Dragged += (offset) => { _appSettings.SplitContentOffset = (int)offset; DataManager.SaveSettingsSoon(_appSettings); };
        _contentSplit.Resized += () => AP_Atlas.UI.Ui.Defer(this, PlaceBottomPaneOnFresh); // once, on a fresh settings file: a third for the bottom pane
        _contentSplit.AddThemeConstantOverride("separation", 8);
        rightColumn.AddChild(_contentSplit);
        // --- 5. CONTENT STAGE ---
        var contentWrapper = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        contentWrapper.AddThemeConstantOverride("separation", 0);
        _contentStage = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(ContentMinWidth, 0) };
        _contentStageStyle = GetVSCodePanelStyle();
        _contentStageStyle.CornerRadiusTopLeft = 0; // Seamless connection (rounded again while the explorer is shown)
        _contentStage.AddThemeStyleboxOverride("panel", _contentStageStyle);
        contentWrapper.AddChild(_contentStage);
        // The explorer shares the top half with the content stage only, so the terminal below keeps its full width.
        rightOfSidebarSplit.AddChild(contentWrapper);
        _contentSplit.AddChild(rightOfSidebarSplit);
        // --- 6. FAR RIGHT PROPERTIES SIDEBAR ---
        _propertiesSidebar = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(PropertiesMinWidth, 0), Visible = true };
        _propertiesSidebar.AddThemeStyleboxOverride("panel", GetVSCodePanelStyle());
        centerRightSplit.AddChild(_propertiesSidebar);
        var propsMargin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        propsMargin.AddThemeConstantOverride("margin_left", 8);
        propsMargin.AddThemeConstantOverride("margin_top", 8);
        propsMargin.AddThemeConstantOverride("margin_right", 8);
        propsMargin.AddThemeConstantOverride("margin_bottom", 8);
        _propertiesSidebar.AddChild(propsMargin);
        var propsVBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        propsMargin.AddChild(propsVBox);
        _propsHeaderBox = CreateSidebarHeader("PROPERTIES", out _propsTitle, out _propsMenuBtn,
            () => _appSettings.PropertiesFontSize,
            (newSize) => { _appSettings.PropertiesFontSize = newSize; ApplyUIScale(); DataManager.SaveSettings(_appSettings); }
        );
        propsVBox.AddChild(_propsHeaderBox);
        _propertiesPanel = new AP_Atlas.UI.PropertiesPanel(this);
        propsVBox.AddChild(_propertiesPanel);
        _globalStatusBar = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var statusStyle = new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.Accent, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 2, ContentMarginBottom = 2 };
        _globalStatusBar.AddThemeStyleboxOverride("panel", statusStyle);
        var statusHBox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _globalStatusBar.AddChild(statusHBox);
        _globalStatusLabel = new Label { Text = Tr("Ready"), SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Left };
        statusHBox.AddChild(_globalStatusLabel);
        _statusConnectedLabel = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Right };
        statusHBox.AddChild(_statusConnectedLabel);
        rootVbox.AddChild(_globalStatusBar);
        BuildBottomPanel();
        ApplyWindowParts(); // the parts the user hid last time stay hidden
        ApplyConsoleTabs();
        _packManagerPanel = new AP_Atlas.Core.MapPackManagerControl(
            LogToSystem,
            () => ShowConnectingOverlay("Managing Map Packs..."),
            () => HideConnectingOverlay(),
            () =>
            {
                var games = new System.Collections.Generic.HashSet<string>();
                foreach (Node n in ActiveSlotNodes())
                {
                    var g = (n as SlotTrackerControl)?.Session?.ConnectionInfo?.Game;
                    if (!string.IsNullOrEmpty(g)) games.Add(g);
                }
                foreach (var p in _profiles)
                    foreach (var s in p.SavedStats.Values)
                        if (!string.IsNullOrEmpty(s.GameName)) games.Add(s.GameName);
                return games;
            },
            () =>
            {
                var sessions = new System.Collections.Generic.List<ArchipelagoSession>();
                foreach (Node n in ActiveSlotNodes())
                {
                    var s = (n as SlotTrackerControl)?.Session;
                    if (s != null && s.Socket.Connected) sessions.Add(s);
                }
                return sessions;
            }
        );
        _packManagerPanel.FontSize = () => _appSettings.ContentFontSize;
        _packManagerPanel.Visible = false;
        _packManagerPanel.OpenDoctor = (path, tab) => OpenPackDoctor(path, tab);
        _packManagerPanel.FixPack = FixWhatAtlasCan;
        _packManagerPanel.Toast = ShowToast;
        _packManagerPanel.OnDataRefreshed += () =>
        {
            SetFontSizeRecursive(_packManagerPanel, _appSettings.ContentFontSize);
            SetFontSizeRecursive(_packManagerPanel.SidebarContent, _appSettings.ExplorerFontSize);
        };
        _contentStage.AddChild(_packManagerPanel);
        _packManagerPanel.SidebarContent.Visible = false;
        _midLeftVBox.AddChild(_packManagerPanel.SidebarContent);
        _cheeseTab = new AP_Atlas.UI.CheeseTrackerTab(_appSettings, _cheese, () => _profiles, ShowToast) { Visible = false };
        _contentStage.AddChild(_cheeseTab);
        _cheeseTab.SidebarContent.Visible = false;
        _midLeftVBox.AddChild(_cheeseTab.SidebarContent);
        _sphereTab = new AP_Atlas.UI.SphereTrackerTab(_appSettings, _spheres, () => _profiles, ShowToast) { Visible = false };
        _contentStage.AddChild(_sphereTab);
        _sphereTab.SidebarContent.Visible = false;
        _midLeftVBox.AddChild(_sphereTab.SidebarContent);
        BuildConnectionTab();
        var settingsPage = BuildSettingsPage();
        _contentStage.AddChild(settingsPage);
        settingsPage.SectionList.Visible = false;
        _midLeftVBox.AddChild(settingsPage.SectionList);
        _toolViews = new Dictionary<AP_Atlas.UI.Tool, (Control, Control, string, Action)>
        {
            [AP_Atlas.UI.Tool.Connections] = (_connectionPanel, _connectionSidebarContent, "Multiworlds", null),
            [AP_Atlas.UI.Tool.MapPacks] = (_packManagerPanel, _packManagerPanel.SidebarContent, "Packs", null),
            [AP_Atlas.UI.Tool.CheeseTracker] = (_cheeseTab, _cheeseTab.SidebarContent, "Cheese Tracker", _cheeseTab.OnShown),
            [AP_Atlas.UI.Tool.SphereTracker] = (_sphereTab, _sphereTab.SidebarContent, "Sphere Tracker", _sphereTab.OnShown),
            [AP_Atlas.UI.Tool.Settings] = (settingsPage, settingsPage.SectionList, "Sections", settingsPage.OnShown),
        };
        var homePage = BuildHomePage();
        _contentStage.AddChild(homePage);
        _toolViews[AP_Atlas.UI.Tool.Home] = (homePage, null, "", homePage.OnShown);
        _gamesPage = new AP_Atlas.UI.GamesPage(new AP_Atlas.UI.GamesHooks
        {
            Settings = _appSettings,
            Tr = text => Tr(text),
            Profiles = () => _profiles,
            LiveSlots = () => ActiveSlotNodes().OfType<SlotTrackerControl>().Where(GodotObject.IsInstanceValid),
            Toast = ShowToast,
            ShowTool = tool => ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(tool),
            OpenEngineSetup = OpenEngineSetup,
            FindPack = FindMapPack,
            StartSoloTest = StartSoloTest,
            StartSoloBatch = StartSoloBatch,
            StopSoloTest = StopSoloTest,
            OpenDocument = OpenDocument
        });
        _gamesPage.Visible = false;
        _contentStage.AddChild(_gamesPage);
        _gamesPage.SidebarContent.Visible = false;
        _midLeftVBox.AddChild(_gamesPage.SidebarContent);
        _toolViews[AP_Atlas.UI.Tool.Games] = (_gamesPage, _gamesPage.SidebarContent, "Games", _gamesPage.OnShown);
        ReportEngineAtStartup();
        OfferToDeletePlainTextPasswordCopies();
        var statusTimer = new Godot.Timer { WaitTime = 0.5f, Autostart = true };
        statusTimer.Timeout += UpdateSlotStatuses;
        AddChild(statusTimer);
        // Reports long frames (with what caused them) to the System Log and status bar.
        AddChild(new AP_Atlas.Core.HitchMonitor(OnHitch));
        SetupModernTheme();
        RefreshProfileList();
        SelectProfile(null);
        UpdateSidebar();
        ShowTool(StartupTool(_appSettings));
        _uiReady = true;
        foreach (var (msg, color) in _pendingNotices) ShowToast(msg, color);
        _pendingNotices.Clear();
        if (FirstRunSelfCheck)
        {
            // The release build's check: the window came up on the fallback folder; that's all it asks.
            AP_Atlas.UI.Ui.Defer(this, () => GetTree().Quit(0));
            return;
        }
        StartUpdateChecks();
        StartCrashReports();
        if (VisualCheckRequested) AP_Atlas.UI.Ui.Defer(this, RunVisualCheck);
        if (UiTestRequested) AP_Atlas.UI.Ui.Defer(this, RunUiTest);
    }

    /// <summary>Reliability self-test mode (ATLAS_SELFTEST=1): runs the checks and exits with their result.</summary>
    private void RunSelfTest() => AP_Atlas.Core.Async.Fire(RunSelfTestAsync(), "running the self-test", tellUser: false);

    private async Task RunSelfTestAsync()
    {
        int code = 1;
        try { code = await AP_Atlas.Core.SelfTest.RunAsync(); }
        catch (System.Exception ex) { GD.PrintErr("SELFTEST CRASHED: " + ex); }
        GetTree().Quit(code);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            // Closing with unsaved edits on the Multiworlds page asks first.
            if (HasUnsavedEdits && !_shuttingDown) GuardUnsaved(GracefulShutdown);
            else GracefulShutdown();
        }
        // Moved to a monitor with another display scale: the window follows it (the notification reaches every node under the window).
        else if (what == NotificationWMDpiChange && _appSettings != null)
        {
            AP_Atlas.UI.Ui.DeferQuiet(this, ApplyZoom);
        }
    }

    // =====================================================================
    // Automatic reconnect
    // =====================================================================

    // =====================================================================
    // Race mode
    // =====================================================================

    // =====================================================================
    // Properties panel host
    // =====================================================================

    AppSettings AP_Atlas.UI.IPropertiesHost.Settings => _appSettings;

    SlotTrackerControl AP_Atlas.UI.IPropertiesHost.SelectedSlot =>
        _currentSelectedSlot != null && GodotObject.IsInstanceValid(_currentSelectedSlot) ? _currentSelectedSlot : null;

    IEnumerable<SlotTrackerControl> AP_Atlas.UI.IPropertiesHost.ConnectedSlots => ActiveSlotNodes().OfType<SlotTrackerControl>();

    IReadOnlyList<MultiworldProfile> AP_Atlas.UI.IPropertiesHost.Profiles => _profiles;

    bool AP_Atlas.UI.IPropertiesHost.IsSlotConnecting(string profileId, string slotName) => _connectingSlots.Contains(SlotKey(profileId, slotName));

    void AP_Atlas.UI.IPropertiesHost.SelectSlot(SlotTrackerControl slot)
    {
        if (slot == null || !GodotObject.IsInstanceValid(slot)) return;
        _currentSelectedSlot = slot;
        RefreshContextViews();
        _sphereTab?.FollowSlot(slot.ProfileId, slot.SlotName);
    }

    void AP_Atlas.UI.IPropertiesHost.ConnectSlot(string profileId, string slotName)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null) OnConnectSlotPressed(slotName, profile);
    }

    void AP_Atlas.UI.IPropertiesHost.DisconnectSlot(string profileId, string slotName) => DisconnectSlot(profileId, slotName);

    void AP_Atlas.UI.IPropertiesHost.ShowTool(AP_Atlas.UI.Tool tool)
    {
        // Leaving the Multiworlds page with unsaved edits asks first: Save / Don't save / Cancel.
        if (_currentTool == AP_Atlas.UI.Tool.Connections && tool != AP_Atlas.UI.Tool.Connections && HasUnsavedEdits)
        {
            _activityBar?.Select(AP_Atlas.UI.Tool.Connections);
            GuardUnsaved(() =>
            {
                _activityBar?.Select(tool);
                if (_currentTool != tool) ShowTool(tool);
            });
            return;
        }
        _activityBar?.Select(tool);
        if (_currentTool != tool) ShowTool(tool);
    }

    void AP_Atlas.UI.IPropertiesHost.SelectProfile(string profileId)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null) SelectProfileGuarded(profile);
    }

    void AP_Atlas.UI.IPropertiesHost.Toast(string message, Godot.Color color) => ShowToast(message, color);

    AP_Atlas.Core.CheeseTracker.CheeseTrackerService AP_Atlas.UI.IPropertiesHost.Cheese => _cheese;

    void AP_Atlas.UI.IPropertiesHost.OpenCheeseSettings() => OpenCheeseSettings();

    void AP_Atlas.UI.IPropertiesHost.ShowCheeseTab(string profileId) => ShowCheeseTab(profileId ?? AP_Atlas.UI.CheeseTrackerTab.MineView);

}
