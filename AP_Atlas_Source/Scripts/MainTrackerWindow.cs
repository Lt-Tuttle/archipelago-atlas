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
    private Control _landingPage;
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
    private Button _connectAllBtn;
    private Button _saveButton;
    private Button _deleteButton;
    private Label _statusLabel;
    private Control _connectingOverlay;
    private AP_Atlas.UI.SafeRichText _consoleOutput;
    private AP_Atlas.UI.LogPane _systemLog, _debugLog;
    private Label _globalStatusLabel;
    private HBoxContainer _menuHbox;
    private PanelContainer _globalStatusBar;
    private AppSettings _appSettings;
    /// <summary>The tool whose tab is showing.</summary>
    private AP_Atlas.UI.Tool _currentTool = AP_Atlas.UI.Tool.Connections;
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
    private AP_Atlas.UI.PropertiesPanel _propertiesPanel;
    private List<MultiworldProfile> _profiles = new();
    private AP_Atlas.Core.CheeseTracker.CheeseTrackerService _cheese;
    private LineEdit _cheeseInput;
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
        // The visual check (testing only) refuses a real data folder before anything in it is read or written.
        if (VisualCheckRequested && !VisualCheckAllowed()) return;
        // The UI test, too, refuses a real data folder before anything in it is read or written.
        if (UiTestRequested && !UiTestAllowed()) return;
        if (!AP_Atlas.Core.CrashGuard.TryAcquireInstance())
        {
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
        // Games' names nobody has used for three months (older versions pile up as games update).
        var dataPackages = DataManager.DataPackages;
        AP_Atlas.Core.Async.Fire(System.Threading.Tasks.Task.Run(() => dataPackages.RemoveUnused(System.TimeSpan.FromDays(90))),
            "tidying the stored game names", tellUser: false);
        GetWindow().Title = "The Archipelago Atlas " + AP_Atlas.Core.AtlasVersion.Display;
        _appSettings = DataManager.LoadSettings();
        AP_Atlas.Core.ThemeColors.SetAccent(_appSettings.ThemeAccentColor);
        AP_Atlas.Core.RaceRules.Initialize(_appSettings);
        AP_Atlas.Core.EngineSetup.AtlasEngine.Initialize(_appSettings);
        AP_Atlas.Core.PopTracker.PackDoctorService.Initialize(_appSettings);
        AP_Atlas.Core.PopTracker.PackDoctorService.ReviewSuggested += OnPackReviewSuggested;
        _profiles = DataManager.LoadProfiles();
        StartSessions();
        // Services read the slots themselves (their models), never their panels.
        _cheese = new AP_Atlas.Core.CheeseTracker.CheeseTrackerService(_appSettings, () => _profiles, SlotModels, () => DataManager.SaveProfiles(_profiles));
        AddChild(_cheese);
        _cheese.Notice += message => ShowToast(message, Colors.Orange);
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
        // Restore window state
        var window = GetTree().Root;
        if (_appSettings.WindowWidth > 0 && _appSettings.WindowHeight > 0)
        {
            window.Size = new Vector2I(_appSettings.WindowWidth, _appSettings.WindowHeight);
        }
        if (_appSettings.WindowX >= 0 && _appSettings.WindowY >= 0)
        {
            // Only set position if it's roughly on screen
            var screenRect = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
            if (screenRect.HasPoint(new Vector2I(_appSettings.WindowX, _appSettings.WindowY)))
            {
                window.Position = new Vector2I(_appSettings.WindowX, _appSettings.WindowY);
            }
        }
        if (_appSettings.WindowMaximized)
        {
            window.Mode = Window.ModeEnum.Maximized;
        }
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
        _activityBar.EnginePressed += OpenEngineSetup;
        appWorkspaceHBox.AddChild(_activityBar);
        _mainSplit = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffsets = new[] { _appSettings.MainSplitOffset } };
        _mainSplit.Dragged += (offset) => { _appSettings.MainSplitOffset = (int)offset; DataManager.SaveSettingsSoon(_appSettings); };
        _mainSplit.AddThemeConstantOverride("separation", 8);
        appWorkspaceHBox.AddChild(_mainSplit);
        // --- 1. FAR LEFT SLOTS SIDEBAR ---
        _sidebar = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(400, 0) };
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
        var centerRightSplit = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffsets = new[] { _appSettings.SplitCenterRightOffset } };
        centerRightSplit.Dragged += (offset) => { _appSettings.SplitCenterRightOffset = (int)offset; DataManager.SaveSettingsSoon(_appSettings); };
        centerRightSplit.AddThemeConstantOverride("separation", 8);
        _mainSplit.AddChild(centerRightSplit);
        var rightColumn = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        centerRightSplit.AddChild(rightColumn);
        var rightOfSidebarSplit = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffsets = new[] { _appSettings.SplitRightSidebarOffset } };
        rightOfSidebarSplit.Dragged += (offset) => { _appSettings.SplitRightSidebarOffset = (int)offset; DataManager.SaveSettingsSoon(_appSettings); };
        rightOfSidebarSplit.AddThemeConstantOverride("separation", 8);
        // --- 3. MID LEFT EXPLORER SIDEBAR ---
        _midLeftSidebar = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(250, 0), Visible = false };
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
        var globalTabHBox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _toolTitle = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
        _toolTitle.AddThemeColorOverride("font_color", Colors.LightGray);
        _toolTitle.AddThemeConstantOverride("margin_left", 8);
        globalTabHBox.AddChild(_toolTitle);
        globalTabHBox.AddChild(BuildSlotPicker());
        var contentMenuBtn = new Button { Text = "...", Flat = true, FocusMode = FocusModeEnum.None };
        contentMenuBtn.AddThemeColorOverride("font_color", Colors.LightGray);
        AttachFontMenuPopup(contentMenuBtn,
            () => _appSettings.ContentFontSize,
            (newSize) => { _appSettings.ContentFontSize = newSize; ApplyUIScale(); DataManager.SaveSettings(_appSettings); }
        );
        globalTabHBox.AddChild(contentMenuBtn);
        rightColumn.AddChild(globalTabHBox);
        _contentSplit = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffsets = new[] { _appSettings.SplitContentOffset } };
        _contentSplit.Dragged += (offset) => { _appSettings.SplitContentOffset = (int)offset; DataManager.SaveSettingsSoon(_appSettings); };
        _contentSplit.AddThemeConstantOverride("separation", 8);
        rightColumn.AddChild(_contentSplit);
        // --- 5. CONTENT STAGE ---
        var contentWrapper = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        contentWrapper.AddThemeConstantOverride("separation", 0);
        _contentStage = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _contentStageStyle = GetVSCodePanelStyle();
        _contentStageStyle.CornerRadiusTopLeft = 0; // Seamless connection (rounded again while the explorer is shown)
        _contentStage.AddThemeStyleboxOverride("panel", _contentStageStyle);
        contentWrapper.AddChild(_contentStage);
        // The explorer shares the top half with the content stage only, so the terminal below keeps its full width.
        rightOfSidebarSplit.AddChild(contentWrapper);
        _contentSplit.AddChild(rightOfSidebarSplit);
        // --- 6. FAR RIGHT PROPERTIES SIDEBAR ---
        _propertiesSidebar = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(250, 0), Visible = true };
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
        _globalStatusLabel = new Label { Text = "Ready", SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Left };
        statusHBox.AddChild(_globalStatusLabel);
        rootVbox.AddChild(_globalStatusBar);
        BuildBottomPanel();
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
        _packManagerPanel.Visible = false;
        _packManagerPanel.OpenDoctor = path => OpenPackDoctor(path);
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
        _toolViews = new Dictionary<AP_Atlas.UI.Tool, (Control, Control, string, Action)>
        {
            [AP_Atlas.UI.Tool.Connections] = (_connectionPanel, _connectionSidebarContent, "Multiworlds", null),
            [AP_Atlas.UI.Tool.MapPacks] = (_packManagerPanel, _packManagerPanel.SidebarContent, "Packs", null),
            [AP_Atlas.UI.Tool.CheeseTracker] = (_cheeseTab, _cheeseTab.SidebarContent, "Cheese Tracker", _cheeseTab.OnShown),
            [AP_Atlas.UI.Tool.SphereTracker] = (_sphereTab, _sphereTab.SidebarContent, "Sphere Tracker", _sphereTab.OnShown),
        };
        BuildLandingPage();
        ReportEngineAtStartup();
        OfferToDeletePlainTextPasswordCopies();
        var statusTimer = new Godot.Timer { WaitTime = 0.5f, Autostart = true };
        statusTimer.Timeout += UpdateSlotStatuses;
        AddChild(statusTimer);
        // Reports long frames (with what caused them) to the System Log and status bar.
        AddChild(new AP_Atlas.Core.HitchMonitor(msg => { if (_globalStatusLabel != null) _globalStatusLabel.Text = msg; }));
        SetupModernTheme();
        RefreshProfileList();
        SelectProfile(null);
        UpdateSidebar();
        SwapContentView(null);
        _uiReady = true;
        foreach (var (msg, color) in _pendingNotices) ShowToast(msg, color);
        _pendingNotices.Clear();
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
            GracefulShutdown();
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
        _activityBar?.Select(tool);
        if (_currentTool != tool) ShowTool(tool);
    }

    void AP_Atlas.UI.IPropertiesHost.SelectProfile(string profileId)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null) SelectProfile(profile);
    }

    void AP_Atlas.UI.IPropertiesHost.Toast(string message, Godot.Color color) => ShowToast(message, color);

    AP_Atlas.Core.CheeseTracker.CheeseTrackerService AP_Atlas.UI.IPropertiesHost.Cheese => _cheese;

    void AP_Atlas.UI.IPropertiesHost.OpenCheeseSettings() => OpenCheeseSettings();

    void AP_Atlas.UI.IPropertiesHost.ShowCheeseTab(string profileId) => ShowCheeseTab(profileId ?? AP_Atlas.UI.CheeseTrackerTab.MineView);

}
