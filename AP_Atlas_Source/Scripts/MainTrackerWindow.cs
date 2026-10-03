using System.Linq;
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;

public partial class MainTrackerWindow : Control
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

    private Texture2D CreateIconFromSvg(string svgString)
    {
        var img = new Godot.Image();
        img.LoadSvgFromString(svgString);
        return ImageTexture.CreateFromImage(img);
    }

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
    private RichTextLabel _consoleOutput;
    private Label _globalStatusLabel;
    private HBoxContainer _menuHbox;
    private PanelContainer _globalStatusBar;
    private AppSettings _appSettings;
    private int _currentGlobalTab = 0;
    private int _currentTerminalTab = 0;
    private TabBar _workspaceSwitcher;
    private PanelContainer _midLeftSidebar;
    private PanelContainer _propertiesSidebar;
    private Label _midLeftTitle;
    private VBoxContainer _midLeftContent;
    private VBoxContainer _propertiesContent;
    private List<MultiworldProfile> _profiles = new();
    private MultiworldProfile _selectedProfile = null;
    private VSplitContainer _contentSplit;
    private RichTextLabel _debugLogConsole;

    private void BuildLandingPage()
    {
        _landingPage = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var vbox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin };
        vbox.AddThemeConstantOverride("separation", 8);
        _landingPage.AddChild(vbox);
        var logo = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://Assets/Graphics/icon.png"),
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Godot.Vector2(400, 400)
        };
        vbox.AddChild(logo);
        var title = new Label { Text = "The Archipelago Atlas", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 32);
        vbox.AddChild(title);
        _contentStage.AddChild(_landingPage);
        SwapContentView(_landingPage);
    }

    private TabBar _bottomTabs;
    private PanelContainer _bottomContent;
    private VBoxContainer _sysLogVBox;
    private VBoxContainer _debugLogVBox;

    private void BuildBottomPanel()
    {
        var bottomWrapper = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(0, 150) };
        bottomWrapper.AddThemeConstantOverride("separation", 0);
        var bottomHeader = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bottomWrapper.AddChild(bottomHeader);
        _bottomTabs = new TabBar { SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
        var tabEmpty = new StyleBoxFlat { BgColor = new Godot.Color(0, 0, 0, 0), CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, BorderWidthTop = 2, BorderColor = new Godot.Color(0, 0, 0, 0), ContentMarginLeft = 24, ContentMarginRight = 24, ContentMarginTop = 8, ContentMarginBottom = 8 };
        var tabHover = new StyleBoxFlat { BgColor = new Godot.Color("#2a2d2e"), CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, BorderWidthTop = 2, BorderColor = new Godot.Color(0, 0, 0, 0), ContentMarginLeft = 24, ContentMarginRight = 24, ContentMarginTop = 8, ContentMarginBottom = 8 };
        var tabSelected = new StyleBoxFlat { BgColor = new Godot.Color("#1e1e1e"), CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, BorderWidthTop = 2, BorderColor = new Godot.Color(_appSettings.ThemeAccentColor), ContentMarginLeft = 24, ContentMarginRight = 24, ContentMarginTop = 8, ContentMarginBottom = 8 };
        _bottomTabs.AddThemeStyleboxOverride("tab_unselected", tabEmpty);
        _bottomTabs.AddThemeStyleboxOverride("tab_selected", tabSelected);
        _bottomTabs.AddThemeStyleboxOverride("tab_hovered", tabHover);
        _bottomTabs.AddTab("Chat");
        _bottomTabs.AddTab("System Log");
        _bottomTabs.AddTab("Debug Log");
        bottomHeader.AddChild(_bottomTabs);
        bottomHeader.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill }); // Spacer
        var bottomMenuBtn = new Button { Text = "...", Flat = true, FocusMode = FocusModeEnum.None };
        bottomMenuBtn.AddThemeColorOverride("font_color", Colors.LightGray);
        var extraItems = new System.Collections.Generic.Dictionary<string, System.Action> {
            { "Copy System Log", () => DisplayServer.ClipboardSet(_consoleOutput.GetParsedText()) },
            { "Clear System Log", () => _consoleOutput.Text = "" },
            { "Copy Debug Log", () => DisplayServer.ClipboardSet(_debugLogConsole.GetParsedText()) },
            { "Clear Debug Log", () => _debugLogConsole.Text = "" }
        };
        AttachFontMenuPopup(bottomMenuBtn,
            () => _appSettings.ConsoleFontSize,
            (newSize) => { _appSettings.ConsoleFontSize = newSize; ApplyUIScale(); DataManager.SaveSettings(_appSettings); },
            extraItems
        );
        bottomHeader.AddChild(bottomMenuBtn);
        _terminalStage = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var contentStyle = GetVSCodePanelStyle();
        contentStyle.CornerRadiusTopLeft = 0;
        _terminalStage.AddThemeStyleboxOverride("panel", contentStyle);
        bottomWrapper.AddChild(_terminalStage);
        _sysLogVBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _consoleOutput = new RichTextLabel { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, ScrollFollowing = true, SelectionEnabled = true, BbcodeEnabled = true };
        _sysLogVBox.AddChild(_consoleOutput);
        _terminalStage.AddChild(_sysLogVBox);
        _debugLogVBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _debugLogConsole = new RichTextLabel { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, ScrollFollowing = true, SelectionEnabled = true, BbcodeEnabled = true };
        _debugLogVBox.AddChild(_debugLogConsole);
        _terminalStage.AddChild(_debugLogVBox);
        _bottomTabs.TabSelected += (long tab) =>
        {
            _currentTerminalTab = (int)tab;
            RefreshTerminalView();
        };
        _contentSplit.AddChild(bottomWrapper);
    }

    private Label _sidebarTitle;
    private Button _sidebarMenuBtn;
    private Label _propsTitle;
    private Button _propsMenuBtn;
    private HBoxContainer _sidebarHeaderBox;
    private HBoxContainer _midLeftHeaderBox;
    private HBoxContainer _propsHeaderBox;

    private void AttachFontMenuPopup(Button menuBtn, System.Func<int> getFontSize, System.Action<int> setFontSize, System.Collections.Generic.Dictionary<string, System.Action> extraItems = null)
    {
        var popup = new PopupPanel { Transient = true };
        var customPopupStyle = new StyleBoxFlat
        {
            BgColor = new Godot.Color("#252526"),
            BorderColor = new Godot.Color("#444444"),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 16,
            ContentMarginRight = 16,
            ContentMarginTop = 16,
            ContentMarginBottom = 16
        };
        popup.AddThemeStyleboxOverride("panel", customPopupStyle);
        var panelVBox = new VBoxContainer();
        panelVBox.AddThemeConstantOverride("separation", 10);
        var fontRow = new HBoxContainer();
        fontRow.AddThemeConstantOverride("separation", 10);
        var fontLbl = new Label { Text = "Font Size:" };
        var minusBtn = new Button { Text = "-", CustomMinimumSize = new Godot.Vector2(28, 28), FocusMode = FocusModeEnum.None };
        var plusBtn = new Button { Text = "+", CustomMinimumSize = new Godot.Vector2(28, 28), FocusMode = FocusModeEnum.None };
        var valLbl = new Label { Text = $"{getFontSize()}px", CustomMinimumSize = new Godot.Vector2(40, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        minusBtn.Pressed += () =>
        {
            int newSize = System.Math.Max(8, getFontSize() - 1);
            setFontSize(newSize);
            valLbl.Text = $"{newSize}px";
        };
        plusBtn.Pressed += () =>
        {
            int newSize = System.Math.Min(32, getFontSize() + 1);
            setFontSize(newSize);
            valLbl.Text = $"{newSize}px";
        };
        fontRow.AddChild(fontLbl);
        fontRow.AddChild(minusBtn);
        fontRow.AddChild(valLbl);
        fontRow.AddChild(plusBtn);
        panelVBox.AddChild(fontRow);
        if (extraItems != null && extraItems.Count > 0)
        {
            panelVBox.AddChild(new HSeparator());
            foreach (var kvp in extraItems)
            {
                var btn = new Button { Text = kvp.Key, Flat = true, Alignment = HorizontalAlignment.Left };
                var action = kvp.Value;
                btn.Pressed += () =>
                {
                    action.Invoke();
                    popup.Hide();
                };
                panelVBox.AddChild(btn);
            }
        }
        popup.AddChild(panelVBox);
        menuBtn.AddChild(popup);
        var localBtn = menuBtn;
        localBtn.Pressed += () =>
        {
            popup.Position = new Godot.Vector2I((int)localBtn.GlobalPosition.X - 180, (int)localBtn.GlobalPosition.Y + 20);
            valLbl.Text = $"{getFontSize()}px";
            popup.Popup();
        };
    }

    private HBoxContainer CreateSidebarHeader(string titleText, out Label titleLabel, out Button menuBtn, System.Func<int> getFontSize, System.Action<int> setFontSize)
    {
        var headerBox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Name = "HeaderBox" };
        titleLabel = new Label { Name = "FixedHeaderTitle", Text = titleText, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Left };
        titleLabel.AddThemeColorOverride("font_color", Colors.LightGray);
        headerBox.AddChild(titleLabel);
        menuBtn = new Button { Text = "...", Flat = true, FocusMode = FocusModeEnum.None };
        menuBtn.AddThemeColorOverride("font_color", Colors.LightGray);
        AttachFontMenuPopup(menuBtn, getFontSize, setFontSize);
        headerBox.AddChild(menuBtn);
        return headerBox;
    }

    public override void _ExitTree()
    {
        AP_Atlas.Core.Logger.OnLogMessage -= OnLogMessageReceived;
    }

    private void OnLogMessageReceived(string msg, string level)
    {
        if (_consoleOutput != null)
        {
            Callable.From(() => _consoleOutput.AppendText(msg)).CallDeferred();
        }
        if (_debugLogConsole != null)
        {
            Callable.From(() => _debugLogConsole.AppendText(msg)).CallDeferred();
        }
    }

    public override void _Ready()
    {
        GetTree().AutoAcceptQuit = false;
        AP_Atlas.Core.Logger.OnLogMessage += OnLogMessageReceived;
        AP_Atlas.Core.Logger.LogInfo("AP Atlas UI Initialized.");
        _appSettings = DataManager.LoadSettings();
        _profiles = DataManager.LoadProfiles();
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
        _menuHbox = new HBoxContainer();
        rootVbox.AddChild(_menuHbox);
        var fileMenuBtn = new MenuButton { Text = "File" };
        var fileMenu = fileMenuBtn.GetPopup();
        fileMenu.AddItem("Exit", 0);
        fileMenu.IdPressed += (id) => { if (id == 0) GracefulShutdown(); };
        _menuHbox.AddChild(fileMenuBtn);
        var viewMenuBtn = new MenuButton { Text = "View" };
        var viewMenu = viewMenuBtn.GetPopup();
        var consoleMenu = new PopupMenu { Name = "ConsoleMenu" };
        consoleMenu.AddCheckItem("System Log", 100);
        consoleMenu.SetItemChecked(consoleMenu.GetItemIndex(100), true);
        consoleMenu.AddCheckItem("Debug Log", 101);
        consoleMenu.SetItemChecked(consoleMenu.GetItemIndex(101), true);
        consoleMenu.IdPressed += (id) =>
        {
            if (_bottomTabs == null) return;
            int tabIdx = id == 100 ? 1 : 2; // 0 = Chat, 1 = System Log, 2 = Debug Log
            bool nowHidden = !_bottomTabs.IsTabHidden(tabIdx);
            _bottomTabs.SetTabHidden(tabIdx, nowHidden);
            consoleMenu.SetItemChecked(consoleMenu.GetItemIndex((int)id), !nowHidden);
            if (nowHidden && _bottomTabs.CurrentTab == tabIdx) _bottomTabs.CurrentTab = 0;
        };
        viewMenu.AddChild(consoleMenu);
        _iconConnect = CreateIconFromSvg(@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#E0E0E0"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71""></path><path d=""M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71""></path></svg>");
        _iconCheck = CreateIconFromSvg(@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#4ade80"" stroke-width=""3"" stroke-linecap=""round"" stroke-linejoin=""round""><polyline points=""20 6 9 17 4 12""></polyline></svg>");
        _iconDisconnect = CreateIconFromSvg(@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""-4 -4 32 32"" fill=""none"" stroke=""#E0E0E0"" stroke-width=""2.5"" stroke-linecap=""round"" stroke-linejoin=""round""><line x1=""2"" y1=""2"" x2=""22"" y2=""22""></line><path d=""M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71""></path><path d=""M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71""></path></svg>");
        _iconDelete = CreateIconFromSvg(@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#f87171"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><polyline points=""3 6 5 6 21 6""></polyline><path d=""M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2""></path><line x1=""10"" y1=""11"" x2=""10"" y2=""17""></line><line x1=""14"" y1=""11"" x2=""14"" y2=""17""></line></svg>");
        viewMenu.AddSubmenuNodeItem("Bottom Console Tabs", consoleMenu);
        var themeSubMenu = new PopupMenu { Name = "ThemeColorMenu" };
        themeSubMenu.AddItem("Pikachu Yellow", 0);
        themeSubMenu.AddItem("Dark Moon Violet", 1);
        themeSubMenu.AddItem("Mario Hat Red", 2);
        themeSubMenu.AddItem("Kokiri Green", 3);
        themeSubMenu.AddItem("Master Sword Blue", 4);
        themeSubMenu.AddItem("Bonfire Orange", 5);
        themeSubMenu.IdPressed += OnThemeColorMenuPressed;
        viewMenu.AddChild(themeSubMenu);
        viewMenu.AddSubmenuNodeItem("Theme Accent Color", themeSubMenu);
        // Menu & Tab font size: drives the menu bar, popups, top tabs and terminal tabs (GlobalFontSize).
        var fontSubMenu = new PopupMenu { Name = "MenuFontMenu", HideOnItemSelection = false };
        const int FontReadoutId = 0, FontIncreaseId = 1, FontDecreaseId = 2, FontResetId = 3;
        fontSubMenu.AddItem("", FontReadoutId);
        fontSubMenu.SetItemDisabled(fontSubMenu.GetItemIndex(FontReadoutId), true);
        fontSubMenu.AddSeparator();
        fontSubMenu.AddItem("Increase  +", FontIncreaseId);
        fontSubMenu.AddItem("Decrease  Ã¢Ë†â€™", FontDecreaseId);
        fontSubMenu.AddItem("Reset to Default (14px)", FontResetId);
        void RefreshFontReadout() =>
            fontSubMenu.SetItemText(fontSubMenu.GetItemIndex(FontReadoutId), $"Current Size: {_appSettings.GlobalFontSize}px");
        RefreshFontReadout();
        fontSubMenu.AboutToPopup += RefreshFontReadout;
        fontSubMenu.IdPressed += (id) =>
        {
            int size = _appSettings.GlobalFontSize;
            if (id == FontIncreaseId) size = System.Math.Min(32, size + 1);
            else if (id == FontDecreaseId) size = System.Math.Max(8, size - 1);
            else if (id == FontResetId) size = 14;
            else return;
            _appSettings.GlobalFontSize = size;
            ApplyUIScale();
            DataManager.SaveSettings(_appSettings);
            RefreshFontReadout();
        };
        viewMenu.AddChild(fontSubMenu);
        viewMenu.AddSubmenuNodeItem("Menu & Tab Font Size", fontSubMenu);
        _menuHbox.AddChild(viewMenuBtn);
        var settingsMenuBtn = new MenuButton { Text = "Settings" };
        var settingsMenu = settingsMenuBtn.GetPopup();
        settingsMenu.AddItem("Archipelago Engine Path", 0);
        settingsMenu.AddItem("Update Universal Tracker Engine", 1);
        settingsMenu.IdPressed += (id) =>
        {
            if (id == 0) OpenEnginePathDialog();
            else if (id == 1) UpdateUniversalTracker();
        };
        _menuHbox.AddChild(settingsMenuBtn);
        var interiorMargin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        interiorMargin.AddThemeConstantOverride("margin_left", 8);
        interiorMargin.AddThemeConstantOverride("margin_top", 8);
        interiorMargin.AddThemeConstantOverride("margin_right", 8);
        interiorMargin.AddThemeConstantOverride("margin_bottom", 8);
        rootVbox.AddChild(interiorMargin);
        var appWorkspaceHBox = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        appWorkspaceHBox.AddThemeConstantOverride("separation", 8);
        interiorMargin.AddChild(appWorkspaceHBox);
        _mainSplit = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffset = _appSettings.MainSplitOffset };
        _mainSplit.Dragged += (offset) => { _appSettings.MainSplitOffset = (int)offset; DataManager.SaveSettings(_appSettings); };
        _mainSplit.AddThemeConstantOverride("separation", 8);
        interiorMargin.AddChild(_mainSplit);
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
        var sidebar = new MenuButton { Text = "...", Flat = true, FocusMode = FocusModeEnum.None };
        sidebarVBox.AddChild(_sidebarHeaderBox);
        var sessionScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sidebarVBox.AddChild(sessionScroll);
        _activeSessionsList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sessionScroll.AddChild(_activeSessionsList);
        // --- 2. REST OF LAYOUT ---
        var rightOfSidebarSplit = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffset = _appSettings.SplitRightSidebarOffset };
        rightOfSidebarSplit.Dragged += (offset) => { _appSettings.SplitRightSidebarOffset = (int)offset; DataManager.SaveSettings(_appSettings); };
        rightOfSidebarSplit.AddThemeConstantOverride("separation", 8);
        _mainSplit.AddChild(rightOfSidebarSplit);
        // --- 3. MID LEFT EXPLORER SIDEBAR ---
        _midLeftSidebar = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(250, 0), Visible = false };
        _midLeftSidebar.AddThemeStyleboxOverride("panel", GetVSCodePanelStyle());
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
        var rightSideVBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rightOfSidebarSplit.AddChild(rightSideVBox);
        var globalTabHBox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _workspaceSwitcher = new TabBar { SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
        var tabEmpty = new StyleBoxFlat { BgColor = new Godot.Color(0, 0, 0, 0), CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, BorderWidthTop = 2, BorderColor = new Godot.Color(0, 0, 0, 0), ContentMarginLeft = 24, ContentMarginRight = 24, ContentMarginTop = 8, ContentMarginBottom = 8 };
        var tabHover = new StyleBoxFlat { BgColor = new Godot.Color("#2a2d2e"), CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, BorderWidthTop = 2, BorderColor = new Godot.Color(0, 0, 0, 0), ContentMarginLeft = 24, ContentMarginRight = 24, ContentMarginTop = 8, ContentMarginBottom = 8 };
        var tabSelected = new StyleBoxFlat { BgColor = new Godot.Color("#1e1e1e"), CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, BorderWidthTop = 2, BorderColor = new Godot.Color(_appSettings.ThemeAccentColor), ContentMarginLeft = 24, ContentMarginRight = 24, ContentMarginTop = 8, ContentMarginBottom = 8 };
        _workspaceSwitcher.AddThemeStyleboxOverride("tab_unselected", tabEmpty);
        _workspaceSwitcher.AddThemeStyleboxOverride("tab_selected", tabSelected);
        _workspaceSwitcher.AddThemeStyleboxOverride("tab_hovered", tabHover);
        _workspaceSwitcher.AddTab("Connections");
        _workspaceSwitcher.AddTab("Map Packs");
        _workspaceSwitcher.AddTab("Map Tracker");
        _workspaceSwitcher.AddTab("Key Items");
        _workspaceSwitcher.AddTab("Logic Tracker");
        _workspaceSwitcher.AddTab("Item History");
        _workspaceSwitcher.TabSelected += (long tab) => { ChangeGlobalTab((int)tab); };
        globalTabHBox.AddChild(_workspaceSwitcher);
        globalTabHBox.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill }); // Spacer
        var contentMenuBtn = new Button { Text = "...", Flat = true, FocusMode = FocusModeEnum.None };
        contentMenuBtn.AddThemeColorOverride("font_color", Colors.LightGray);
        AttachFontMenuPopup(contentMenuBtn,
            () => _appSettings.ContentFontSize,
            (newSize) => { _appSettings.ContentFontSize = newSize; ApplyUIScale(); DataManager.SaveSettings(_appSettings); }
        );
        globalTabHBox.AddChild(contentMenuBtn);
        rightSideVBox.AddChild(globalTabHBox);
        var centerRightSplit = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffset = _appSettings.SplitCenterRightOffset };
        centerRightSplit.Dragged += (offset) => { _appSettings.SplitCenterRightOffset = (int)offset; DataManager.SaveSettings(_appSettings); };
        centerRightSplit.AddThemeConstantOverride("separation", 8);
        rightSideVBox.AddChild(centerRightSplit);
        _contentSplit = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffset = _appSettings.SplitContentOffset };
        _contentSplit.Dragged += (offset) => { _appSettings.SplitContentOffset = (int)offset; DataManager.SaveSettings(_appSettings); };
        _contentSplit.AddThemeConstantOverride("separation", 8);
        centerRightSplit.AddChild(_contentSplit);
        // --- 5. CONTENT STAGE ---
        var contentWrapper = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        contentWrapper.AddThemeConstantOverride("separation", 0);
        _contentStage = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var contentStyle = GetVSCodePanelStyle();
        contentStyle.CornerRadiusTopLeft = 0; // Seamless connection
        _contentStage.AddThemeStyleboxOverride("panel", contentStyle);
        contentWrapper.AddChild(_contentStage);
        _contentSplit.AddChild(contentWrapper);
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
        var props = new MenuButton { Text = "...", Flat = true, FocusMode = FocusModeEnum.None };
        propsVBox.AddChild(_propsHeaderBox);
        _propertiesContent = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        propsVBox.AddChild(_propertiesContent);
        _globalStatusBar = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var statusStyle = new StyleBoxFlat { BgColor = new Godot.Color("#007acc"), ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 2, ContentMarginBottom = 2 };
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
        _packManagerPanel.OnDataRefreshed += ApplyUIScale;
        _contentStage.AddChild(_packManagerPanel);
        _packManagerPanel.SidebarContent.Visible = false;
        _midLeftVBox.AddChild(_packManagerPanel.SidebarContent);
        BuildConnectionTab();
        BuildLandingPage();
        AutoDetectArchipelagoPath();
        var statusTimer = new Godot.Timer { WaitTime = 0.5f, Autostart = true };
        statusTimer.Timeout += UpdateSlotStatuses;
        AddChild(statusTimer);
        SetupModernTheme();
        RefreshProfileList();
        SelectProfile(null);
        UpdateSidebar();
        SwapContentView(null);
    }

    private void SetupModernTheme()
    {
        var theme = new Theme();
        string accentHex = string.IsNullOrEmpty(_appSettings.ThemeAccentColor) ? "#8A2BE2" : _appSettings.ThemeAccentColor;
        var accentColor = new Godot.Color(accentHex);
        var panelBg = new StyleBoxFlat
        {
            BgColor = new Godot.Color("#252526"),
            BorderColor = new Godot.Color("#181818"), // Dark VS Code border
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4
        };
        theme.SetStylebox("panel", "PanelContainer", panelBg);
        var tabPanel = new StyleBoxFlat
        {
            BgColor = new Godot.Color("#1e1e1e"),
            BorderColor = new Godot.Color("#181818"),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1
        };
        theme.SetStylebox("panel", "TabContainer", tabPanel);
        var tabSelected = new StyleBoxFlat
        {
            BgColor = new Godot.Color("#1e1e1e"),
            BorderWidthTop = 2,
            BorderColor = accentColor,
            BorderWidthBottom = 0,
            ContentMarginLeft = 20,
            ContentMarginRight = 20,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
            ExpandMarginBottom = 1 // Blends into the panel border
        };
        theme.SetStylebox("tab_selected", "TabContainer", tabSelected);
        var tabUnselected = new StyleBoxFlat
        {
            BgColor = new Godot.Color("#2d2d30"),
            BorderWidthTop = 0,
            BorderColor = new Godot.Color("#181818"),
            BorderWidthBottom = 1, // Creates a separator line
            ContentMarginLeft = 20,
            ContentMarginRight = 20,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
        theme.SetStylebox("tab_unselected", "TabContainer", tabUnselected);
        // Generate VSplit Grabber (Horizontal line of dots for vertical splitting)
        var grabberImgV = Image.CreateEmpty(24, 6, false, Image.Format.Rgba8);
        grabberImgV.Fill(new Godot.Color(0, 0, 0, 0));
        for (int i = 0; i < 3; i++)
        {
            grabberImgV.SetPixel(8 + i * 4, 2, new Godot.Color("#666666"));
            grabberImgV.SetPixel(8 + i * 4, 3, new Godot.Color("#666666"));
            grabberImgV.SetPixel(9 + i * 4, 2, new Godot.Color("#666666"));
            grabberImgV.SetPixel(9 + i * 4, 3, new Godot.Color("#666666"));
        }
        var grabberTexV = ImageTexture.CreateFromImage(grabberImgV);
        theme.SetIcon("grabber", "VSplitContainer", grabberTexV);
        // Generate HSplit Grabber (Vertical line of dots for horizontal splitting)
        var grabberImgH = Image.CreateEmpty(6, 24, false, Image.Format.Rgba8);
        grabberImgH.Fill(new Godot.Color(0, 0, 0, 0));
        for (int i = 0; i < 3; i++)
        {
            grabberImgH.SetPixel(2, 8 + i * 4, new Godot.Color("#666666"));
            grabberImgH.SetPixel(3, 8 + i * 4, new Godot.Color("#666666"));
            grabberImgH.SetPixel(2, 9 + i * 4, new Godot.Color("#666666"));
            grabberImgH.SetPixel(3, 9 + i * 4, new Godot.Color("#666666"));
        }
        var grabberTexH = ImageTexture.CreateFromImage(grabberImgH);
        theme.SetIcon("grabber", "HSplitContainer", grabberTexH);
        // Ensure splitters have no default ugly background, just the grabber
        var emptyStyle = new StyleBoxEmpty();
        theme.SetConstant("autohide", "HSplitContainer", 0);
        theme.SetConstant("autohide", "VSplitContainer", 0);
        theme.SetConstant("separation", "HSplitContainer", 8);
        theme.SetConstant("separation", "VSplitContainer", 8);
        var tabActive = new StyleBoxFlat { BgColor = new Godot.Color("#1e1e1e"), BorderWidthTop = 2, BorderColor = accentColor, ContentMarginLeft = 15, ContentMarginRight = 15, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("tab_selected", "TabContainer", tabActive);
        var tabInactive = new StyleBoxFlat { BgColor = new Godot.Color("#2d2d30"), ContentMarginLeft = 15, ContentMarginRight = 15, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("tab_unselected", "TabContainer", tabInactive);
        var btnNormal = new StyleBoxFlat { BgColor = new Godot.Color("#3e3e42"), CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("normal", "Button", btnNormal);
        var btnHover = new StyleBoxFlat { BgColor = new Godot.Color("#4f4f53"), CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("hover", "Button", btnHover);
        var btnPressed = new StyleBoxFlat { BgColor = accentColor, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("pressed", "Button", btnPressed);
        var cbNormal = new StyleBoxEmpty();
        theme.SetStylebox("normal", "CheckBox", cbNormal);
        theme.SetStylebox("hover", "CheckBox", cbNormal);
        theme.SetStylebox("pressed", "CheckBox", cbNormal);
        theme.SetStylebox("focus", "CheckBox", cbNormal);
        theme.SetStylebox("hover_pressed", "CheckBox", cbNormal);
        var lineEdit = new StyleBoxFlat { BgColor = new Godot.Color("#3c3c3c"), CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2, ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6, BorderWidthBottom = 1, BorderColor = accentColor };
        theme.SetStylebox("normal", "LineEdit", lineEdit);
        var sysFont = GD.Load<FontFile>("res://Assets/Fonts/GoogleSans-Regular.ttf");
        if (sysFont != null)
        {
            sysFont.MultichannelSignedDistanceField = true;
            theme.DefaultFont = sysFont;
        }
        else
        {
            var fallback = new SystemFont { MultichannelSignedDistanceField = true };
            fallback.FontNames = new[] { "Google Sans", "Segoe UI", "sans-serif" };
            theme.DefaultFont = fallback;
        }
        var monoFont = GD.Load<FontFile>("res://Assets/Fonts/GoogleSansCode-Regular.ttf");
        if (monoFont != null)
        {
            monoFont.MultichannelSignedDistanceField = true;
            theme.SetFont("normal_font", "RichTextLabel", monoFont);
            theme.SetFont("mono_font", "RichTextLabel", monoFont);
        }
        else
        {
            var monoFallback = new SystemFont { MultichannelSignedDistanceField = true };
            monoFallback.FontNames = new[] { "Google Sans Code", "Consolas", "monospace" };
            theme.SetFont("normal_font", "RichTextLabel", monoFallback);
            theme.SetFont("mono_font", "RichTextLabel", monoFallback);
        }
        this.Theme = theme;
        RenderingServer.SetDefaultClearColor(new Godot.Color("#1e1e1e"));
        if (_globalStatusBar != null)
        {
            var statusStyle = new StyleBoxFlat { BgColor = accentColor, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 2, ContentMarginBottom = 2 };
            _globalStatusBar.AddThemeStyleboxOverride("panel", statusStyle);
        }
        ApplyUIScale();
    }

    private void OnThemeColorMenuPressed(long id)
    {
        if (id == 0) _appSettings.ThemeAccentColor = "#FFD700"; // Pokemon
        else if (id == 1) _appSettings.ThemeAccentColor = "#8246DC"; // Elden Ring
        else if (id == 2) _appSettings.ThemeAccentColor = "#EB2828"; // Mario
        else if (id == 3) _appSettings.ThemeAccentColor = "#3CD250"; // Zelda OoT
        else if (id == 4) _appSettings.ThemeAccentColor = "#32A0FF"; // Zelda ALttP
        else if (id == 5) _appSettings.ThemeAccentColor = "#FF781E"; // Dark Souls
        DataManager.SaveSettings(_appSettings);
        DataManager.SaveProfiles(_profiles);
        SetupModernTheme();
        RefreshProfileListStyles();
    }

    private FileDialog _enginePathDialog;

    private async void UpdateUniversalTracker()
    {
        LogToSystem("[color=yellow]Starting Universal Tracker Update...[/color]");
        _globalStatusLabel.Text = "Updating Universal Tracker...";
        var logicEngine = new LogicEngineManager(_appSettings.ArchipelagoInstallationPath, msg =>
        {
            CallDeferred(nameof(LogToSystem), msg);
        });
        bool success = await logicEngine.DownloadLatestEngineAsync(msg =>
        {
            CallDeferred(nameof(LogToSystem), msg);
            Callable.From(() => _globalStatusLabel.Text = msg.Replace("Status: ", "")).CallDeferred();
        });
        Callable.From(() =>
        {
            if (success)
            {
                LogToSystem("[color=green]Universal Tracker Update Complete![/color]");
                _globalStatusLabel.Text = "Update Complete";
            }
            else
            {
                LogToSystem("[color=red]Universal Tracker Update Failed.[/color]");
                _globalStatusLabel.Text = "Update Failed";
            }
        }).CallDeferred();
    }

    private void OpenEnginePathDialog()
    {
        if (_enginePathDialog == null)
        {
            _enginePathDialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenDir,
                Access = FileDialog.AccessEnum.Filesystem,
                Title = "Select Archipelago Installation Directory",
                UseNativeDialog = true
            };
            _enginePathDialog.DirSelected += (dir) =>
            {
                _appSettings.ArchipelagoInstallationPath = dir;
                DataManager.SaveSettings(_appSettings);
                LogToSystem("[color=green]Archipelago Engine Path set to:[/color] " + dir);
            };
            AddChild(_enginePathDialog);
        }
        _enginePathDialog.CurrentDir = string.IsNullOrEmpty(_appSettings.ArchipelagoInstallationPath) ? OS.GetSystemDir(OS.SystemDir.Documents) : _appSettings.ArchipelagoInstallationPath;
        _enginePathDialog.PopupCentered(new Vector2I(600, 400));
    }

    private void AutoDetectArchipelagoPath()
    {
        if (!string.IsNullOrEmpty(_appSettings.ArchipelagoInstallationPath)) return;
        string programData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);
        string defaultPath = System.IO.Path.Combine(programData, "Archipelago");
        string localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        string localPath = System.IO.Path.Combine(localAppData, "Programs", "Archipelago");
        string[] possiblePaths = { defaultPath, localPath, "C:\\Archipelago" };
        foreach (string path in possiblePaths)
        {
            if (System.IO.Directory.Exists(path) && System.IO.File.Exists(System.IO.Path.Combine(path, "ArchipelagoLauncher.exe")))
            {
                _appSettings.ArchipelagoInstallationPath = path;
                DataManager.SaveSettings(_appSettings);
                LogToSystem("[color=green]Auto-detected Archipelago Engine at:[/color] " + path);
                return;
            }
        }
    }

    private void ShowToast(string message, Godot.Color color)
    {
        var toastPanel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = new Godot.Color(0.1f, 0.1f, 0.1f, 0.9f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderColor = color,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 20,
            ContentMarginRight = 20,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
        toastPanel.AddThemeStyleboxOverride("panel", style);
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 10);
        var circle = new ColorRect { CustomMinimumSize = new Godot.Vector2(10, 10), Color = color, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var label = new Label { Text = message };
        hbox.AddChild(circle);
        hbox.AddChild(label);
        toastPanel.AddChild(hbox);
        var canvas = new CanvasLayer { Layer = 100 };
        canvas.AddChild(toastPanel);
        AddChild(canvas);
        toastPanel.Modulate = new Godot.Color(1, 1, 1, 0);
        // Wait a frame to let Godot calculate the minimum size
        CallDeferred(nameof(AnimateToast), toastPanel, canvas);
    }

    private void AnimateToast(PanelContainer toastPanel, CanvasLayer canvas)
    {
        var winSize = GetWindow().Size;
        var panelSize = toastPanel.Size;
        // Position at bottom-right, slightly offset
        toastPanel.Position = new Godot.Vector2(winSize.X - panelSize.X - 20, winSize.Y - panelSize.Y - 20);
        var tween = CreateTween();
        tween.TweenProperty(toastPanel, "modulate", new Godot.Color(1, 1, 1, 1), 0.3f).SetTrans(Tween.TransitionType.Cubic);
        tween.TweenInterval(3.0f);
        tween.TweenProperty(toastPanel, "modulate", new Godot.Color(1, 1, 1, 0), 0.5f).SetTrans(Tween.TransitionType.Cubic);
        tween.TweenCallback(Callable.From(() => canvas.QueueFree()));
    }

    private void ApplyUIScale()
    {
        int globalSize = _appSettings.GlobalFontSize;
        if (this.Theme == null) this.Theme = new Theme();
        this.Theme.SetFontSize("font_size", "PopupMenu", globalSize);
        this.Theme.SetFontSize("font_size", "MenuButton", globalSize);
        var popupStyle = new StyleBoxFlat
        {
            BgColor = new Godot.Color("#252526"),
            BorderColor = new Godot.Color("#444444"),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 16,
            ContentMarginRight = 16,
            ContentMarginTop = 16,
            ContentMarginBottom = 16
        };
        this.Theme.SetStylebox("panel", "PopupMenu", popupStyle);
        if (_menuHbox != null) SetFontSizeRecursive(_menuHbox, globalSize);
        if (_workspaceSwitcher != null) _workspaceSwitcher.AddThemeFontSizeOverride("font_size", globalSize);
        if (_bottomTabs != null) _bottomTabs.AddThemeFontSizeOverride("font_size", globalSize);
        if (_globalStatusBar != null) SetFontSizeRecursive(_globalStatusBar, globalSize);
        if (_bottomTabs != null) _bottomTabs.AddThemeFontSizeOverride("font_size", globalSize);
        if (_sidebarTitle != null) _sidebarTitle.AddThemeFontSizeOverride("font_size", globalSize);
        if (_midLeftTitle != null) _midLeftTitle.AddThemeFontSizeOverride("font_size", globalSize);
        if (_propsTitle != null) _propsTitle.AddThemeFontSizeOverride("font_size", globalSize);
        if (_sidebar != null) SetFontSizeRecursive(_sidebar, _appSettings.SlotsFontSize);
        if (_midLeftSidebar != null) SetFontSizeRecursive(_midLeftSidebar, _appSettings.ExplorerFontSize);
        if (_propertiesSidebar != null) SetFontSizeRecursive(_propertiesSidebar, _appSettings.PropertiesFontSize);
        if (_contentStage != null) SetFontSizeRecursive(_contentStage, _appSettings.ContentFontSize);
        if (_terminalStage != null) SetFontSizeRecursive(_terminalStage, _appSettings.ConsoleFontSize);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            GracefulShutdown();
        }
    }

    private async void GracefulShutdown()
    {
        try
        {
            var win = GetWindow();
            _appSettings.WindowMaximized = win.Mode == Window.ModeEnum.Maximized;
            if (!_appSettings.WindowMaximized)
            {
                _appSettings.WindowWidth = win.Size.X;
                _appSettings.WindowHeight = win.Size.Y;
                _appSettings.WindowX = win.Position.X;
                _appSettings.WindowY = win.Position.Y;
            }
            DataManager.SaveSettings(_appSettings);
            if (_globalStatusLabel != null) _globalStatusLabel.Text = "Disconnecting sessions...";
            LogToSystem("[color=yellow]Shutting down... Disconnecting active slots...[/color]");
            var disconnectTasks = new System.Collections.Generic.List<Task>();
            if (_contentStage != null)
            {
                foreach (Node n in ActiveSlotNodes())
                {
                    if (n is SlotTrackerControl slot && slot.Session != null && slot.Session.Socket.Connected)
                    {
                        var t = slot.Session.Socket.DisconnectAsync();
                        if (t != null) disconnectTasks.Add(t);
                    }
                }
            }
            if (disconnectTasks.Count > 0)
            {
                await Task.WhenAny(Task.WhenAll(disconnectTasks), Task.Delay(3000));
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Error during shutdown: {ex}");
        }
        finally
        {
            GetTree().Quit();
        }
    }

    public static void SetFontSizeRecursive(Node node, int size)
    {
        if (node is Control c)
        {
            int finalSize = size;
            if (c.HasMeta("font_size_ratio"))
            {
                float ratio = (float)c.GetMeta("font_size_ratio").AsDouble();
                finalSize = System.Math.Max(7, (int)(size * ratio));
            }
            if (c.HasMeta("is_icon_button"))
            {
                if (c is Button iconBtn)
                {
                    iconBtn.ClipText = true;
                    iconBtn.CustomMinimumSize = new Godot.Vector2(36, 36);
                    iconBtn.ExpandIcon = true;
                    iconBtn.IconAlignment = HorizontalAlignment.Center;
                    iconBtn.Flat = true;
                }
            }
            if (c.Name == "FixedHeaderTitle" || c.Name == "HeaderBox") return;
            if (c is Label || c is Button || c is LineEdit || c is CheckBox || c is TabContainer || c is Tree)
            {
                c.AddThemeFontSizeOverride("font_size", finalSize);
                if (c is Tree t) t.AddThemeFontSizeOverride("title_button_font_size", finalSize);
            }
            else if (c is RichTextLabel rtl)
            {
                rtl.AddThemeFontSizeOverride("normal_font_size", finalSize);
                rtl.AddThemeFontSizeOverride("mono_font_size", finalSize);
            }
            if (c is MenuButton mb)
            {
                SetFontSizeRecursive(mb.GetPopup(), size);
            }
        }
        else if (node is PopupMenu pm)
        {
            pm.AddThemeFontSizeOverride("font_size", size);
        }
        foreach (Node child in node.GetChildren())
        {
            SetFontSizeRecursive(child, size);
        }
    }

    private void BuildConnectionTab()
    {
        _connectionSidebarContent = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var listScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _connectionSidebarContent.AddChild(listScroll);
        _profileListContainer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        listScroll.AddChild(_profileListContainer);
        var addButton = new Button { Text = "+ Add Multiworld" };
        addButton.Pressed += OnAddProfilePressed;
        _connectionSidebarContent.AddChild(addButton);
        _midLeftVBox.AddChild(_connectionSidebarContent);
        _connectionPanel = new MarginContainer { Name = "Connection", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _connectionPanel.AddThemeConstantOverride("margin_left", 20);
        _connectionPanel.AddThemeConstantOverride("margin_top", 20);
        _connectionPanel.AddThemeConstantOverride("margin_right", 20);
        _connectionPanel.AddThemeConstantOverride("margin_bottom", 20);
        _contentStage.AddChild(_connectionPanel);
        var rightVbox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _connectionPanel.AddChild(rightVbox);
        var title = new Label { Text = "Multiworld Details", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 24);
        rightVbox.AddChild(title);
        rightVbox.AddChild(new HSeparator());
        _nameInput = new LineEdit { PlaceholderText = "Profile Name", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _nameInput.TextChanged += (_) => MarkDirty();
        rightVbox.AddChild(new Label { Text = "Profile Name:" });
        rightVbox.AddChild(_nameInput);
        _serverInput = new LineEdit { PlaceholderText = "Server (e.g. archipelago.gg:38281)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _serverInput.TextChanged += (_) => MarkDirty();
        rightVbox.AddChild(new Label { Text = "Server Address:" });
        rightVbox.AddChild(_serverInput);
        _passwordInput = new LineEdit { PlaceholderText = "Password (Optional)", Secret = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _passwordInput.TextChanged += (_) => MarkDirty();
        rightVbox.AddChild(new Label { Text = "Password:" });
        rightVbox.AddChild(_passwordInput);
        rightVbox.AddChild(new HSeparator());
        rightVbox.AddChild(new Label { Text = "Multiworld Slots:" });
        var slotScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rightVbox.AddChild(slotScroll);
        _slotsListVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        slotScroll.AddChild(_slotsListVBox);
        var buttonRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        rightVbox.AddChild(buttonRow);
        _addSlotButton = new Button { Text = "+ Add Slot" };
        _addSlotButton.Pressed += OnAddSlotPressed;
        buttonRow.AddChild(_addSlotButton);
        _saveButton = new Button { Text = "Save Settings" };
        _saveButton.Pressed += OnSaveProfilePressed;
        buttonRow.AddChild(_saveButton);
        _deleteButton = new Button { Text = "Delete Profile" };
        _deleteButton.Pressed += OnDeleteProfilePressed;
        buttonRow.AddChild(_deleteButton);
        rightVbox.AddChild(new HSeparator());
        _statusLabel = new Label { Text = "Status: Disconnected", HorizontalAlignment = HorizontalAlignment.Center };
        rightVbox.AddChild(_statusLabel);
    }

    private void LogToSystem(string bbcode)
    {
        AP_Atlas.Core.Logger.LogInfo(bbcode);
    }

    private void LogToDebug(string msg, string slotName = "")
    {
        string time = System.DateTime.Now.ToString("HH:mm:ss");
        string prefix = string.IsNullOrEmpty(slotName) ? "[color=gray]" : $"[color=orange][{slotName}][/color] [color=gray]";
        string formatted = $"{prefix}[{time}][/color] {msg}\n";
        if (_debugLogConsole != null) Callable.From(() => _debugLogConsole.AppendText(formatted)).CallDeferred();
    }

    private void MarkDirty()
    {
        _saveButton.Modulate = Colors.Yellow;
    }

    private SlotTrackerControl _currentSelectedSlot = null;
    private System.Collections.Generic.HashSet<string> _connectingSlots = new System.Collections.Generic.HashSet<string>();
    private int _spinnerIndex = 0;
    private string[] _spinnerFrames = { "/", "-", "\\", "|" };
    private Texture2D _iconConnect;
    private Texture2D _iconCheck;
    private Texture2D _iconDisconnect;
    private Texture2D _iconDelete;

    private void UpdateSidebarHighlighting()
    {
        if (_activeSessionsList == null) return;
        foreach (Node child in _activeSessionsList.GetChildren())
        {
            if (child is PanelContainer card && card.HasMeta("slot_name") && card.HasMeta("profile_id"))
            {
                string slotName = card.GetMeta("slot_name").AsString();
                string profileId = card.GetMeta("profile_id").AsString();
                bool isSelected = _currentSelectedSlot != null &&
                                  _currentSelectedSlot.SlotName == slotName &&
                                  _currentSelectedSlot.ProfileId == profileId;
                var cardStyle = new StyleBoxFlat
                {
                    BgColor = isSelected ? new Godot.Color("#1B261E") : new Godot.Color("#1A1A1F"),
                    CornerRadiusTopLeft = 4,
                    CornerRadiusTopRight = 4,
                    CornerRadiusBottomLeft = 4,
                    CornerRadiusBottomRight = 4,
                    BorderWidthLeft = 1,
                    BorderWidthRight = 1,
                    BorderWidthTop = 1,
                    BorderWidthBottom = 1,
                    BorderColor = isSelected ? new Godot.Color("#4CAF50") : new Godot.Color("#2C2D35"),
                    ContentMarginLeft = 6,
                    ContentMarginRight = 6,
                    ContentMarginTop = 5,
                    ContentMarginBottom = 5
                };
                card.AddThemeStyleboxOverride("panel", cardStyle);
                var cardVBox = card.GetChildOrNull<VBoxContainer>(0);
                if (cardVBox != null)
                {
                    var row = cardVBox.GetChildOrNull<HBoxContainer>(0);
                    if (row != null)
                    {
                        var btn = row.GetChildOrNull<Button>(0);
                        if (btn != null)
                        {
                            if (isSelected)
                            {
                                btn.AddThemeColorOverride("font_color", Colors.White);
                                btn.AddThemeColorOverride("font_hover_color", Colors.White);
                                btn.AddThemeColorOverride("font_pressed_color", Colors.White);
                                btn.AddThemeColorOverride("font_focus_color", Colors.White);
                                var style = new StyleBoxFlat
                                {
                                    BgColor = new Godot.Color("#2E7D32"),
                                    CornerRadiusTopLeft = 4,
                                    CornerRadiusTopRight = 4,
                                    CornerRadiusBottomLeft = 4,
                                    CornerRadiusBottomRight = 4,
                                    ContentMarginLeft = 8,
                                    ContentMarginRight = 8,
                                    ContentMarginTop = 4,
                                    ContentMarginBottom = 4
                                };
                                btn.AddThemeStyleboxOverride("normal", style);
                                btn.AddThemeStyleboxOverride("hover", style);
                                btn.AddThemeStyleboxOverride("pressed", style);
                                btn.AddThemeStyleboxOverride("focus", style);
                            }
                            else
                            {
                                btn.RemoveThemeColorOverride("font_color");
                                btn.RemoveThemeColorOverride("font_hover_color");
                                btn.RemoveThemeColorOverride("font_pressed_color");
                                btn.RemoveThemeColorOverride("font_focus_color");
                                btn.RemoveThemeStyleboxOverride("normal");
                                btn.RemoveThemeStyleboxOverride("hover");
                                btn.RemoveThemeStyleboxOverride("pressed");
                                btn.RemoveThemeStyleboxOverride("focus");
                            }
                        }
                    }
                }
            }
        }
    }

    private void SwapSidebar(string title, Control activeContent = null)
    {
        if (activeContent == null)
        {
            _midLeftSidebar.Visible = false;
            return;
        }
        _midLeftSidebar.Visible = true;
        _midLeftTitle.Text = title.ToUpper();
        if (activeContent.GetParent() != _midLeftVBox)
        {
            activeContent.GetParent()?.RemoveChild(activeContent);
            _midLeftVBox.AddChild(activeContent);
        }
        foreach (Godot.Node c in _midLeftVBox.GetChildren())
        {
            if (c is Control ctrl && ctrl != _midLeftHeaderBox) ctrl.Visible = false;
        }
        activeContent.Visible = true;
    }

    private void SwapContentView(Control target)
    {
        if (_contentStage == null) return;
        bool hasTarget = target != null;
        foreach (Node n in _contentStage.GetChildren())
        {
            if (n is Control c)
            {
                if (c == _landingPage) c.Visible = !hasTarget;
                else c.Visible = (c == target);
            }
        }
    }

    private void RefreshTerminalView()
    {
        if (_terminalStage == null) return;
        foreach (Node n in _terminalStage.GetChildren())
        {
            if (n is Control c)
            {
                if (_currentTerminalTab == 0) // Chat
                {
                    c.Visible = (c == _currentSelectedSlot);
                }
                else if (_currentTerminalTab == 1) // System Log
                {
                    c.Visible = (c == _sysLogVBox);
                }
                else if (_currentTerminalTab == 2) // Debug Log
                {
                    c.Visible = (c == _debugLogVBox);
                }
                else
                {
                    c.Visible = false;
                }
            }
        }
    }

    private void RefreshContextViews()
    {
        UpdateSidebarHighlighting();
        RefreshTerminalView();
        // Tabs 0 (Connections) and 1 (Map Packs) are global and handled by ChangeGlobalTab.
        if (_currentGlobalTab < 2) return;
        if (_currentSelectedSlot == null || !GodotObject.IsInstanceValid(_currentSelectedSlot))
        {
            _currentSelectedSlot = null;
            SwapSidebar("", null);
            ShowNoSlotPlaceholder();
            return;
        }
        Control view = null;
        Control sidebar = null;
        string sidebarTitle = "";
        switch (_currentGlobalTab)
        {
            case 2: view = _currentSelectedSlot.MapTracker; sidebar = _currentSelectedSlot.MapTracker?.SidebarContent; sidebarTitle = "Maps"; break;
            case 3: view = _currentSelectedSlot.ProgressionTracker; break;
            case 4: view = _currentSelectedSlot.LogicTrackerView; break;
            case 5: view = _currentSelectedSlot.ItemHistoryView; break;
        }
        MountInContentStage(view);
        SwapSidebar(sidebarTitle, sidebar);
        SwapContentView(view);
    }

    /// <summary>Slots are mounted in the terminal pane. This is the single place that enumerates them.</summary>
    private System.Collections.Generic.List<Node> ActiveSlotNodes()
    {
        var result = new System.Collections.Generic.List<Node>();
        if (_terminalStage == null) return result;
        foreach (Node n in _terminalStage.GetChildren())
        {
            if (n is SlotTrackerControl && !n.IsQueuedForDeletion()) result.Add(n);
        }
        return result;
    }

    private void MountInContentStage(Control view)
    {
        if (view == null || _contentStage == null) return;
        if (view.GetParent() == _contentStage) return;
        view.GetParent()?.RemoveChild(view);
        _contentStage.AddChild(view);
        ApplyUIScale();
    }

    private Label _noSlotPlaceholder;

    private void ShowNoSlotPlaceholder()
    {
        if (_noSlotPlaceholder == null || !GodotObject.IsInstanceValid(_noSlotPlaceholder))
        {
            _noSlotPlaceholder = new Label
            {
                Text = "Select a connected slot in the SLOTS panel to view this tool.",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            _noSlotPlaceholder.AddThemeColorOverride("font_color", Colors.Gray);
            _contentStage.AddChild(_noSlotPlaceholder);
        }
        SwapContentView(_noSlotPlaceholder);
    }

    private void UpdateSidebar()
    {
        if (_activeSessionsList == null) return;
        foreach (Node child in _activeSessionsList.GetChildren())
        {
            child.QueueFree();
        }
        foreach (var profile in _profiles)
        {
            var profileHeaderPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var profileHeaderStyle = new StyleBoxFlat { BgColor = new Godot.Color(_appSettings.ThemeAccentColor), ContentMarginTop = 4, ContentMarginBottom = 4, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 };
            profileHeaderPanel.AddThemeStyleboxOverride("panel", profileHeaderStyle);
            var header = new Label
            {
                Text = profile.Name,
                HorizontalAlignment = HorizontalAlignment.Center,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            header.AddThemeColorOverride("font_color", Colors.White);
            profileHeaderPanel.AddChild(header);
            _activeSessionsList.AddChild(profileHeaderPanel);
            foreach (var slotName in profile.Slots)
            {
                SlotTrackerControl session = null;
                if (_contentStage != null)
                {
                    foreach (Node active in ActiveSlotNodes())
                    {
                        if (active is SlotTrackerControl slot && slot.ProfileId == profile.Id && slot.SlotName == slotName)
                        {
                            session = slot;
                            break;
                        }
                    }
                }
                bool isSocketConnected = session != null && session.Session != null && session.Session.Socket.Connected;
                bool isFullyLoaded = session != null && session.IsFullyLoaded;
                bool isConnected = isSocketConnected && isFullyLoaded;
                bool isConnecting = _connectingSlots.Contains(slotName) || (isSocketConnected && !isFullyLoaded);
                bool isSelected = session != null && session == _currentSelectedSlot;
                var cardPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                cardPanel.SetMeta("slot_name", slotName);
                cardPanel.SetMeta("profile_id", profile.Id);
                var cardStyle = new StyleBoxFlat
                {
                    BgColor = isSelected ? new Godot.Color("#1B261E") : new Godot.Color("#1A1A1F"),
                    CornerRadiusTopLeft = 4,
                    CornerRadiusTopRight = 4,
                    CornerRadiusBottomLeft = 4,
                    CornerRadiusBottomRight = 4,
                    BorderWidthLeft = 1,
                    BorderWidthRight = 1,
                    BorderWidthTop = 1,
                    BorderWidthBottom = 1,
                    BorderColor = isSelected ? new Godot.Color("#4CAF50") : new Godot.Color("#2C2D35"),
                    ContentMarginLeft = 6,
                    ContentMarginRight = 6,
                    ContentMarginTop = 5,
                    ContentMarginBottom = 5
                };
                cardPanel.AddThemeStyleboxOverride("panel", cardStyle);
                var cardVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                cardVBox.AddThemeConstantOverride("separation", 3);
                cardPanel.AddChild(cardVBox);
                var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 2);
                string gameName = "";
                if (session != null && session.Session != null && session.Session.ConnectionInfo != null && !string.IsNullOrEmpty(session.Session.ConnectionInfo.Game))
                {
                    gameName = session.Session.ConnectionInfo.Game;
                }
                else if (profile.SavedStats != null && profile.SavedStats.TryGetValue(slotName, out var stats) && !string.IsNullOrEmpty(stats.GameName))
                {
                    gameName = stats.GameName;
                }
                var btn = new Button
                {
                    Text = slotName,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    Alignment = HorizontalAlignment.Left
                };
                if (isSelected)
                {
                    btn.AddThemeColorOverride("font_color", Colors.White);
                    btn.AddThemeColorOverride("font_hover_color", Colors.White);
                    btn.AddThemeColorOverride("font_pressed_color", Colors.White);
                    btn.AddThemeColorOverride("font_focus_color", Colors.White);
                    var style = new StyleBoxFlat
                    {
                        BgColor = new Godot.Color("#2E7D32"),
                        CornerRadiusTopLeft = 4,
                        CornerRadiusTopRight = 4,
                        CornerRadiusBottomLeft = 4,
                        CornerRadiusBottomRight = 4,
                        ContentMarginLeft = 8,
                        ContentMarginRight = 8,
                        ContentMarginTop = 4,
                        ContentMarginBottom = 4
                    };
                    btn.AddThemeStyleboxOverride("normal", style);
                    btn.AddThemeStyleboxOverride("hover", style);
                    btn.AddThemeStyleboxOverride("pressed", style);
                    btn.AddThemeStyleboxOverride("focus", style);
                }
                string capturedSlotName = slotName;
                string capturedProfileId = profile.Id;
                btn.Pressed += () =>
                {
                    SlotTrackerControl targetSlot = null;
                    if (_contentStage != null)
                    {
                        foreach (Node active in ActiveSlotNodes())
                        {
                            if (active is SlotTrackerControl slot && slot.ProfileId == capturedProfileId && slot.SlotName == capturedSlotName)
                            {
                                targetSlot = slot;
                                break;
                            }
                        }
                    }
                    if (targetSlot != null) { _currentSelectedSlot = targetSlot; RefreshContextViews(); }
                };
                cardPanel.GuiInput += (ev) =>
                {
                    if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    {
                        btn.EmitSignal("pressed");
                    }
                };
                row.AddChild(btn);
                var connectBtn = new Button
                {
                    Name = "ConnectBtn",
                    Icon = isConnecting ? null : (isConnected ? _iconCheck : _iconConnect),
                    Text = isConnecting ? _spinnerFrames[_spinnerIndex] : "",
                    CustomMinimumSize = new Godot.Vector2(32, 32),
                    SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter,
                    SizeFlagsHorizontal = Godot.Control.SizeFlags.ShrinkCenter,
                    ExpandIcon = true,
                    IconAlignment = HorizontalAlignment.Center,
                    ClipText = true,
                    Disabled = isConnected || isConnecting
                };
                connectBtn.SetMeta("is_icon_button", true);
                connectBtn.SetMeta("slot_name", slotName);
                connectBtn.SetMeta("profile_id", profile.Id);
                connectBtn.TooltipText = isConnecting ? "Connecting..." : (isConnected ? "Connected" : "Connect");
                connectBtn.AddThemeColorOverride("icon_disabled_color", Godot.Colors.White);
                if (isConnected) connectBtn.Modulate = Godot.Colors.White;
                else if (isConnecting) connectBtn.Modulate = Godot.Colors.White;
                else connectBtn.Modulate = Godot.Colors.LimeGreen;
                connectBtn.Pressed += () =>
                {
                    if (!isConnecting && !isConnected) OnConnectSlotPressed(slotName, profile);
                };
                row.AddChild(connectBtn);
                var disconnectBtn = new Button
                {
                    Name = "DisconnectBtn",
                    Icon = _iconDisconnect,
                    Text = "",
                    CustomMinimumSize = new Godot.Vector2(32, 32),
                    SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter,
                    SizeFlagsHorizontal = Godot.Control.SizeFlags.ShrinkCenter,
                    ExpandIcon = true,
                    IconAlignment = HorizontalAlignment.Center,
                    Disabled = !isSocketConnected,
                    TooltipText = "Disconnect"
                };
                disconnectBtn.SetMeta("is_icon_button", true);
                disconnectBtn.AddThemeColorOverride("icon_disabled_color", Godot.Colors.DarkGray);
                if (isSocketConnected) disconnectBtn.Modulate = Godot.Colors.Crimson;
                else disconnectBtn.Modulate = Godot.Colors.DarkGray;
                disconnectBtn.Pressed += () =>
                {
                    DisconnectSlot(profile.Id, slotName);
                };
                row.AddChild(disconnectBtn);
                cardVBox.AddChild(row);
                // Stats calculation and persistence
                int total = 0, complete = 0, logic = 0;
                bool hasData = false;
                System.DateTime lastUpdated = System.DateTime.MinValue;
                if (isConnected && session != null)
                {
                    total = session.TotalLocationsCount;
                    complete = session.CheckedLocationsCount;
                    logic = session.ActiveLogicCount;
                    hasData = true;
                    lastUpdated = System.DateTime.Now;
                    if (profile.SavedStats == null) profile.SavedStats = new Dictionary<string, SlotStats>();
                    if (!profile.SavedStats.ContainsKey(slotName)) profile.SavedStats[slotName] = new SlotStats();
                    profile.SavedStats[slotName].TotalCount = total;
                    profile.SavedStats[slotName].CompleteCount = complete;
                    profile.SavedStats[slotName].LogicCount = logic;
                    profile.SavedStats[slotName].LastUpdated = lastUpdated;
                }
                else if (profile.SavedStats != null && profile.SavedStats.TryGetValue(slotName, out var saved))
                {
                    total = saved.TotalCount;
                    complete = saved.CompleteCount;
                    logic = saved.LogicCount;
                    hasData = true;
                    lastUpdated = saved.LastUpdated;
                }
                int percent = total > 0 ? (int)System.Math.Round((double)complete / total * 100.0) : 0;
                var statsContainer = new PanelContainer { Name = "StatsContainer", SizeFlagsHorizontal = SizeFlags.ExpandFill };
                var statsBg = new StyleBoxFlat
                {
                    BgColor = new Godot.Color("#131317"),
                    CornerRadiusTopLeft = 3,
                    CornerRadiusTopRight = 3,
                    CornerRadiusBottomLeft = 3,
                    CornerRadiusBottomRight = 3,
                    ContentMarginLeft = 4,
                    ContentMarginRight = 4,
                    ContentMarginTop = 3,
                    ContentMarginBottom = 3
                };
                statsContainer.AddThemeStyleboxOverride("panel", statsBg);
                var statsHBox = new HBoxContainer { Name = "StatsHBox", SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
                statsHBox.AddThemeConstantOverride("separation", 2);
                statsContainer.AddChild(statsHBox);
                void AddKpiCol(string title, string val, Godot.Color valColor)
                {
                    var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    col.AddThemeConstantOverride("separation", 0);
                    var lblTitle = new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center };
                    lblTitle.SetMeta("font_size_ratio", 0.55);
                    lblTitle.AddThemeFontSizeOverride("font_size", 9);
                    lblTitle.AddThemeColorOverride("font_color", Colors.Gray);
                    col.AddChild(lblTitle);
                    var lblVal = new Label { Text = val, HorizontalAlignment = HorizontalAlignment.Center };
                    lblVal.SetMeta("font_size_ratio", 0.65);
                    lblVal.AddThemeFontSizeOverride("font_size", 10);
                    lblVal.AddThemeColorOverride("font_color", valColor);
                    col.AddChild(lblVal);
                    statsHBox.AddChild(col);
                }
                AddKpiCol("Total", total.ToString(), Colors.LightGray);
                AddKpiCol("Done", complete.ToString(), Colors.LightCyan);
                AddKpiCol("%", $"{percent}%", percent >= 100 ? Colors.LimeGreen : (percent > 0 ? Colors.Cyan : Colors.LightGray));
                AddKpiCol("Logic", logic.ToString(), logic > 0 ? Colors.LimeGreen : Colors.DimGray);
                cardVBox.AddChild(statsContainer);
                var footerHBox = new HBoxContainer { Name = "FooterHBox", SizeFlagsHorizontal = SizeFlags.ExpandFill };
                var statusFooter = new Label
                {
                    Name = "StatusFooter",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                statusFooter.SetMeta("font_size_ratio", 0.50);
                statusFooter.AddThemeFontSizeOverride("font_size", 8);
                var gameNameFooter = new Label
                {
                    Text = gameName,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                gameNameFooter.AddThemeColorOverride("font_color", Colors.DimGray);
                gameNameFooter.SetMeta("font_size_ratio", 0.50);
                gameNameFooter.AddThemeFontSizeOverride("font_size", 8);
                footerHBox.AddChild(statusFooter);
                footerHBox.AddChild(gameNameFooter);
                if (isConnected)
                {
                    statusFooter.Text = "Ã¢â€”Â Live";
                    statusFooter.AddThemeColorOverride("font_color", Colors.LimeGreen);
                }
                else if (isConnecting)
                {
                    statusFooter.Text = "Ã¢â€”Å’ Connecting...";
                    statusFooter.AddThemeColorOverride("font_color", Colors.Yellow);
                }
                else
                {
                    if (hasData && lastUpdated != System.DateTime.MinValue)
                    {
                        statusFooter.Text = $"Last update: {lastUpdated:MM/dd HH:mm}";
                    }
                    else
                    {
                        statusFooter.Text = "Not connected yet";
                    }
                    statusFooter.AddThemeColorOverride("font_color", Colors.DimGray);
                }
                cardVBox.AddChild(footerHBox);
                _activeSessionsList.AddChild(cardPanel);
            }
            _activeSessionsList.AddChild(new HSeparator { CustomMinimumSize = new Godot.Vector2(0, 5) });
        }
        int sidebarSize = (int)(16 * _appSettings.ScaleSidebar);
        SetFontSizeRecursive(_activeSessionsList, sidebarSize);
    }

    private void RefreshProfileList()
    {
        foreach (Node child in _profileListContainer.GetChildren())
        {
            child.QueueFree();
        }
        foreach (var profile in _profiles)
        {
            var btn = new Button { Text = profile.Name };
            btn.SetMeta("profile_id", profile.Id);
            btn.Pressed += () => SelectProfile(profile);
            _profileListContainer.AddChild(btn);
        }
        ApplyUIScale();
        RefreshProfileListStyles();
    }

    private void RefreshProfileListStyles()
    {
        string accentHex = string.IsNullOrEmpty(_appSettings.ThemeAccentColor) ? "#8A2BE2" : _appSettings.ThemeAccentColor;
        var accentColor = new Godot.Color(accentHex);
        foreach (Node child in _profileListContainer.GetChildren())
        {
            if (child is Button btn && btn.HasMeta("profile_id"))
            {
                string id = btn.GetMeta("profile_id").AsString();
                if (_selectedProfile != null && _selectedProfile.Id == id)
                {
                    var style = new Godot.StyleBoxFlat
                    {
                        BgColor = accentColor,
                        CornerRadiusTopLeft = 4,
                        CornerRadiusTopRight = 4,
                        CornerRadiusBottomLeft = 4,
                        CornerRadiusBottomRight = 4,
                        ContentMarginLeft = 10,
                        ContentMarginRight = 10,
                        ContentMarginTop = 5,
                        ContentMarginBottom = 5
                    };
                    btn.AddThemeStyleboxOverride("normal", style);
                    btn.AddThemeStyleboxOverride("hover", style);
                    btn.AddThemeStyleboxOverride("pressed", style);
                    btn.AddThemeStyleboxOverride("focus", style);
                }
                else
                {
                    btn.RemoveThemeStyleboxOverride("normal");
                    btn.RemoveThemeStyleboxOverride("hover");
                    btn.RemoveThemeStyleboxOverride("pressed");
                    btn.RemoveThemeStyleboxOverride("focus");
                }
            }
        }
    }

    private void SelectProfile(MultiworldProfile profile)
    {
        _selectedProfile = profile;
        _saveButton.Modulate = Colors.White;
        if (profile == null)
        {
            _nameInput.Text = "";
            _serverInput.Text = "";
            _passwordInput.Text = "";
            _nameInput.Editable = false;
            _serverInput.Editable = false;
            _passwordInput.Editable = false;
            _saveButton.Disabled = true;
            _deleteButton.Disabled = true;
            _addSlotButton.Disabled = true;
            return;
        }
        _nameInput.Text = profile.Name;
        _serverInput.Text = profile.ServerUrl;
        _passwordInput.Text = profile.Password;
        _nameInput.Editable = true;
        _serverInput.Editable = true;
        _passwordInput.Editable = true;
        _saveButton.Disabled = false;
        _deleteButton.Disabled = false;
        _addSlotButton.Disabled = false;
        PopulateSlotsList();
        RefreshProfileListStyles();
    }

    private void PopulateSlotsList()
    {
        foreach (Node child in _slotsListVBox.GetChildren())
        {
            child.QueueFree();
        }
        if (_selectedProfile == null)
        {
            var watermark = new Label { Text = "Select a profile to edit slots.", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Godot.Color(0.5f, 0.5f, 0.5f) };
            _slotsListVBox.AddChild(watermark);
            return;
        }
        if (_selectedProfile.Slots.Count == 0)
        {
            var watermark = new Label { Text = "No slots configured. Click 'Add Slot' below.", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Godot.Color(0.5f, 0.5f, 0.5f) };
            _slotsListVBox.AddChild(watermark);
        }
        for (int i = 0; i < _selectedProfile.Slots.Count; i++)
        {
            int index = i;
            string slotName = _selectedProfile.Slots[i];
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.SetMeta("slot_name", slotName);
            row.AddThemeConstantOverride("separation", 5);
            var lineEdit = new LineEdit
            {
                Text = slotName,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                PlaceholderText = "Slot Name (e.g. Player1)"
            };
            lineEdit.TextChanged += (newText) =>
            {
                _selectedProfile.Slots[index] = newText;
                row.SetMeta("slot_name", newText);
                MarkDirty();
            };
            lineEdit.TextSubmitted += (newText) =>
            {
                OnConnectSlotPressed(newText, _selectedProfile);
            };
            row.AddChild(lineEdit);
            var connectBtn = new Button
            {
                Name = "ConnectBtn",
                Icon = _iconConnect,
                Text = "",
                CustomMinimumSize = new Godot.Vector2(36, 36),
                SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter,
                SizeFlagsHorizontal = Godot.Control.SizeFlags.ShrinkCenter,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                ClipText = true
            };
            connectBtn.SetMeta("is_icon_button", true);
            connectBtn.SetMeta("slot_name", slotName);
            connectBtn.TooltipText = "Connect";
            connectBtn.AddThemeColorOverride("icon_disabled_color", Godot.Colors.White);
            connectBtn.Modulate = Godot.Colors.LimeGreen;
            connectBtn.Pressed += () =>
            {
                if (connectBtn.Icon == _iconConnect) OnConnectSlotPressed(lineEdit.Text, _selectedProfile);
            };
            row.AddChild(connectBtn);
            var disconnectBtn = new Button
            {
                Name = "DisconnectBtn",
                Icon = _iconDisconnect,
                Text = "",
                CustomMinimumSize = new Godot.Vector2(36, 36),
                SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter,
                SizeFlagsHorizontal = Godot.Control.SizeFlags.ShrinkCenter,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                Disabled = true,
                TooltipText = "Disconnect"
            };
            disconnectBtn.SetMeta("is_icon_button", true);
            disconnectBtn.AddThemeColorOverride("icon_disabled_color", Godot.Colors.DarkGray);
            disconnectBtn.Modulate = Godot.Colors.DarkGray;
            disconnectBtn.Pressed += () =>
            {
                DisconnectSlot(_selectedProfile.Id, lineEdit.Text);
            };
            row.AddChild(disconnectBtn);
            var delBtn = new Button
            {
                Icon = _iconDelete,
                Text = "",
                CustomMinimumSize = new Godot.Vector2(36, 36),
                SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter,
                SizeFlagsHorizontal = Godot.Control.SizeFlags.ShrinkCenter,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                TooltipText = "Delete Slot"
            };
            delBtn.SetMeta("is_icon_button", true);
            delBtn.Modulate = Godot.Colors.Crimson;
            delBtn.Pressed += () =>
            {
                DisconnectSlot(_selectedProfile.Id, lineEdit.Text);
                _selectedProfile.ActiveSlots.Remove(lineEdit.Text);
                _selectedProfile.Slots.RemoveAt(index);
                MarkDirty();
                PopulateSlotsList();
                RefreshProfileListStyles();
                UpdateSidebar();
            };
            row.AddChild(delBtn);
            _slotsListVBox.AddChild(row);
        }
        if (_selectedProfile.Slots.Count > 1)
        {
            var btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            _connectAllBtn = new Button { Text = "Connect All Slots", CustomMinimumSize = new Godot.Vector2(200, 40) };
            _connectAllBtn.AddThemeColorOverride("font_color", Godot.Colors.LimeGreen);
            _connectAllBtn.Pressed += OnConnectAllPressed;
            btnRow.AddChild(_connectAllBtn);
            _slotsListVBox.AddChild(btnRow);
        }
        int contentSize = (int)(16 * _appSettings.ScaleContent);
        SetFontSizeRecursive(_slotsListVBox, contentSize);
        UpdateSlotStatuses(); // Force immediate update of lights
    }

    private void DisconnectSlot(string profileId, string slotName)
    {
        if (_terminalStage == null) return;
        foreach (Node n in ActiveSlotNodes())
        {
            if (n is SlotTrackerControl slot && slot.ProfileId == profileId && slot.SlotName == slotName)
            {
                LogToSystem("[color=yellow]Disconnected slot: " + slotName + "[/color]");
                if (slot.Session != null && slot.Session.Socket.Connected)
                {
                    slot.Session.Socket.DisconnectAsync();
                }
            }
        }
        DataManager.SaveProfiles(_profiles);
        Callable.From(UpdateSidebar).CallDeferred();
    }

    private void UpdateSlotStatuses()
    {
        _spinnerIndex = (_spinnerIndex + 1) % _spinnerFrames.Length;
        // Also update sidebar buttons directly
        if (_activeSessionsList != null)
        {
            foreach (Node child in _activeSessionsList.GetChildren())
            {
                if (child is PanelContainer card)
                {
                    var cardVBox = card.GetChildOrNull<VBoxContainer>(0);
                    if (cardVBox != null)
                    {
                        var row = cardVBox.GetChildOrNull<HBoxContainer>(0);
                        if (row != null)
                        {
                            var btn = row.GetNodeOrNull<Button>("ConnectBtn");
                            if (btn != null && btn.HasMeta("slot_name"))
                            {
                                string slotName = btn.GetMeta("slot_name").AsString();
                                string profileId = btn.HasMeta("profile_id") ? btn.GetMeta("profile_id").AsString() : null;
                                MultiworldProfile profile = _profiles.FirstOrDefault(p => p.Id == profileId);
                                bool isSocketConnected = false;
                                bool isFullyLoaded = false;
                                SlotTrackerControl activeSlot = null;
                                if (_contentStage != null)
                                {
                                    foreach (Node active in ActiveSlotNodes())
                                    {
                                        if (active is SlotTrackerControl slot && slot.SlotName == slotName && (profileId == null || slot.ProfileId == profileId))
                                        {
                                            activeSlot = slot;
                                            isSocketConnected = slot.Session != null && slot.Session.Socket.Connected;
                                            isFullyLoaded = slot.IsFullyLoaded;
                                            break;
                                        }
                                    }
                                }
                                bool isConnecting = _connectingSlots.Contains(slotName) || (isSocketConnected && !isFullyLoaded);
                                bool isConnected = isSocketConnected && isFullyLoaded;
                                var disconnectBtn = row.GetNodeOrNull<Button>("DisconnectBtn");
                                if (isConnecting)
                                {
                                    btn.Icon = null;
                                    btn.Text = _spinnerFrames[_spinnerIndex];
                                    btn.Disabled = false;
                                    btn.Modulate = Godot.Colors.White;
                                    if (disconnectBtn != null) { disconnectBtn.Disabled = true; disconnectBtn.Modulate = Godot.Colors.DarkGray; }
                                }
                                else if (isConnected)
                                {
                                    btn.Icon = _iconCheck;
                                    btn.Text = "";
                                    btn.Disabled = true;
                                    btn.Modulate = Godot.Colors.LimeGreen;
                                    if (disconnectBtn != null) { disconnectBtn.Disabled = false; disconnectBtn.Modulate = Godot.Colors.Crimson; }
                                }
                                else
                                {
                                    btn.Icon = _iconConnect;
                                    btn.Text = "";
                                    btn.Disabled = false;
                                    btn.Modulate = Godot.Colors.White;
                                    if (disconnectBtn != null) { disconnectBtn.Disabled = true; disconnectBtn.Modulate = Godot.Colors.DarkGray; }
                                }
                                int total = 0, complete = 0, logic = 0;
                                bool hasSaved = false;
                                System.DateTime lastUpdated = System.DateTime.MinValue;
                                if (isConnected && activeSlot != null)
                                {
                                    total = activeSlot.TotalLocationsCount;
                                    complete = activeSlot.CheckedLocationsCount;
                                    logic = activeSlot.ActiveLogicCount;
                                    hasSaved = true;
                                    lastUpdated = System.DateTime.Now;
                                    if (profile != null)
                                    {
                                        if (profile.SavedStats == null) profile.SavedStats = new Dictionary<string, SlotStats>();
                                        if (!profile.SavedStats.ContainsKey(slotName)) profile.SavedStats[slotName] = new SlotStats();
                                        profile.SavedStats[slotName].TotalCount = total;
                                        profile.SavedStats[slotName].CompleteCount = complete;
                                        profile.SavedStats[slotName].LogicCount = logic;
                                        profile.SavedStats[slotName].LastUpdated = lastUpdated;
                                    }
                                }
                                else if (profile != null && profile.SavedStats != null && profile.SavedStats.TryGetValue(slotName, out var saved))
                                {
                                    total = saved.TotalCount;
                                    complete = saved.CompleteCount;
                                    logic = saved.LogicCount;
                                    hasSaved = true;
                                    lastUpdated = saved.LastUpdated;
                                }
                                int percent = total > 0 ? (int)System.Math.Round((double)complete / total * 100.0) : 0;
                                var statsContainer = cardVBox.GetNodeOrNull<PanelContainer>("StatsContainer");
                                if (statsContainer != null)
                                {
                                    var statsHBox = statsContainer.GetNodeOrNull<HBoxContainer>("StatsHBox");
                                    if (statsHBox != null)
                                    {
                                        var totalVBox = statsHBox.GetChildOrNull<VBoxContainer>(0);
                                        var compVBox = statsHBox.GetChildOrNull<VBoxContainer>(1);
                                        var percentVBox = statsHBox.GetChildOrNull<VBoxContainer>(2);
                                        var availVBox = statsHBox.GetChildOrNull<VBoxContainer>(3);
                                        if (totalVBox != null)
                                        {
                                            var valTotal = totalVBox.GetChildOrNull<Label>(1);
                                            if (valTotal != null) valTotal.Text = total.ToString();
                                        }
                                        if (compVBox != null)
                                        {
                                            var valComp = compVBox.GetChildOrNull<Label>(1);
                                            if (valComp != null) valComp.Text = complete.ToString();
                                        }
                                        if (percentVBox != null)
                                        {
                                            var valPercent = percentVBox.GetChildOrNull<Label>(1);
                                            if (valPercent != null)
                                            {
                                                valPercent.Text = $"{percent}%";
                                                if (percent >= 100) valPercent.AddThemeColorOverride("font_color", Colors.LimeGreen);
                                                else if (percent > 0) valPercent.AddThemeColorOverride("font_color", Colors.Cyan);
                                                else valPercent.RemoveThemeColorOverride("font_color");
                                            }
                                        }
                                        if (availVBox != null)
                                        {
                                            var valAvail = availVBox.GetChildOrNull<Label>(1);
                                            if (valAvail != null)
                                            {
                                                valAvail.Text = logic.ToString();
                                                if (logic > 0) valAvail.AddThemeColorOverride("font_color", Colors.LimeGreen);
                                                else valAvail.RemoveThemeColorOverride("font_color");
                                            }
                                        }
                                    }
                                }
                                var statusFooter = cardVBox.GetNodeOrNull<HBoxContainer>("FooterHBox")?.GetNodeOrNull<Label>("StatusFooter");
                                if (statusFooter != null)
                                {
                                    if (isConnected)
                                    {
                                        statusFooter.Text = "Ã¢â€”Â Live";
                                        statusFooter.AddThemeColorOverride("font_color", Colors.LimeGreen);
                                    }
                                    else if (isConnecting)
                                    {
                                        statusFooter.Text = "Ã¢â€”Å’ Connecting...";
                                        statusFooter.AddThemeColorOverride("font_color", Colors.Yellow);
                                    }
                                    else
                                    {
                                        if (hasSaved && lastUpdated != System.DateTime.MinValue)
                                        {
                                            statusFooter.Text = $"Last update: {lastUpdated:MM/dd HH:mm}";
                                        }
                                        else
                                        {
                                            statusFooter.Text = "Not connected yet";
                                        }
                                        statusFooter.AddThemeColorOverride("font_color", Colors.DimGray);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        if (_slotsListVBox == null || _selectedProfile == null) return;
        foreach (Node n in _slotsListVBox.GetChildren())
        {
            if (n is HBoxContainer row && row.HasMeta("slot_name"))
            {
                string slotName = row.GetMeta("slot_name").AsString();
                SlotTrackerControl activeSlot = null;
                if (_contentStage != null)
                {
                    foreach (Node active in ActiveSlotNodes())
                    {
                        if (active is SlotTrackerControl slot && slot.ProfileId == _selectedProfile.Id && slot.SlotName == slotName)
                        {
                            activeSlot = slot;
                            break;
                        }
                    }
                }
                var connectBtn = row.GetNodeOrNull<Button>("ConnectBtn");
                var disconnectBtn = row.GetNodeOrNull<Button>("DisconnectBtn");
                if (connectBtn != null)
                {
                    bool isSocketConnected = activeSlot != null && activeSlot.Session != null && activeSlot.Session.Socket.Connected;
                    bool isConnecting = _connectingSlots.Contains(slotName) || (isSocketConnected && !activeSlot.IsFullyLoaded);
                    bool isConnected = isSocketConnected && activeSlot.IsFullyLoaded;
                    if (isConnecting)
                    {
                        connectBtn.Icon = null;
                        connectBtn.Text = _spinnerFrames[_spinnerIndex];
                        connectBtn.Disabled = false;
                        connectBtn.Modulate = Godot.Colors.White;
                        if (disconnectBtn != null) { disconnectBtn.Disabled = true; disconnectBtn.Modulate = Godot.Colors.DarkGray; }
                    }
                    else if (isConnected)
                    {
                        connectBtn.Icon = _iconCheck;
                        connectBtn.Text = "";
                        connectBtn.Disabled = true;
                        connectBtn.Modulate = Godot.Colors.LimeGreen;
                        if (disconnectBtn != null) { disconnectBtn.Disabled = false; disconnectBtn.Modulate = Godot.Colors.Crimson; }
                    }
                    else
                    {
                        connectBtn.Icon = _iconConnect;
                        connectBtn.Text = "";
                        connectBtn.Disabled = false;
                        connectBtn.Modulate = Godot.Colors.LimeGreen;
                        if (disconnectBtn != null) { disconnectBtn.Disabled = true; disconnectBtn.Modulate = Godot.Colors.DarkGray; }
                    }
                }
            }
        }
    }

    private void OnAddSlotPressed()
    {
        if (_selectedProfile == null)
        {
            var watermark = new Label { Text = "Select a profile to edit slots.", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Godot.Color(0.5f, 0.5f, 0.5f) };
            _slotsListVBox.AddChild(watermark);
            return;
        }
        if (_selectedProfile.Slots.Count == 0)
        {
            var watermark = new Label { Text = "No slots configured. Click 'Add Slot' below.", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Godot.Color(0.5f, 0.5f, 0.5f) };
            _slotsListVBox.AddChild(watermark);
        }
        _selectedProfile.Slots.Add("New Slot");
        MarkDirty();
        PopulateSlotsList();
        RefreshProfileListStyles();
    }

    private void OnAddProfilePressed()
    {
        var newProfile = new MultiworldProfile();
        newProfile.Slots.Add("Player1");
        _profiles.Add(newProfile);
        DataManager.SaveProfiles(_profiles);
        RefreshProfileList();
        SelectProfile(newProfile);
    }

    private void OnSaveProfilePressed()
    {
        if (_selectedProfile != null)
        {
            _selectedProfile.Name = _nameInput.Text;
            _selectedProfile.ServerUrl = _serverInput.Text;
            _selectedProfile.Password = _passwordInput.Text;
            DataManager.SaveProfiles(_profiles);
            _saveButton.Modulate = Colors.White;
            RefreshProfileList();
            LogToSystem($"[color=green]Profile '{_selectedProfile.Name}' saved.[/color]");
        }
    }

    private void OnDeleteProfilePressed()
    {
        if (_selectedProfile != null)
        {
            var confirmDialog = new ConfirmationDialog
            {
                Title = "Delete Profile",
                DialogText = $"Are you sure you want to delete the profile '{_selectedProfile.Name}' and all its slots?",
                Transient = true,
                Exclusive = true
            };
            confirmDialog.Confirmed += () =>
            {
                var profileId = _selectedProfile.Id;
                _profiles.Remove(_selectedProfile);
                DataManager.SaveProfiles(_profiles);
                foreach (Node n in ActiveSlotNodes().ToList())
                {
                    if (n is SlotTrackerControl slot && slot.ProfileId == profileId)
                    {
                        if (_currentSelectedSlot == slot) _currentSelectedSlot = null;
                        if (slot.Session?.Socket != null) _ = slot.Session.Socket.DisconnectAsync();
                        slot.QueueFree();
                    }
                }
                RefreshProfileList();
                SelectProfile(null);
                UpdateSidebar();
                ShowToast("Profile Deleted", Godot.Colors.Orange);
                confirmDialog.QueueFree();
            };
            confirmDialog.Canceled += () => confirmDialog.QueueFree();
            AddChild(confirmDialog);
            confirmDialog.PopupCentered();
        }
    }

    private bool _isConnectingSlot = false;

    private void ShowConnectingOverlay(string message)
    {
        Callable.From(() =>
        {
            if (_connectingOverlay == null)
            {
                _connectingOverlay = new CenterContainer { Name = "ConnectingOverlay" };
                _connectingOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                var popup = new PanelContainer { Name = "PopupPanel" };
                popup.CustomMinimumSize = new Godot.Vector2(450, 150);
                var style = new StyleBoxFlat { BgColor = new Godot.Color(0.12f, 0.12f, 0.15f, 0.95f), CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10, BorderWidthBottom = 2, BorderWidthTop = 2, BorderWidthLeft = 2, BorderWidthRight = 2, BorderColor = Godot.Colors.DarkGray };
                popup.AddThemeStyleboxOverride("panel", style);
                var lbl = new Label { Name = "MessageLabel", HorizontalAlignment = HorizontalAlignment.Center };
                lbl.AddThemeFontSizeOverride("font_size", 28);
                lbl.AddThemeColorOverride("font_color", Godot.Colors.Yellow);
                var popupCenter = new CenterContainer { Name = "CenterContainer" };
                popupCenter.AddChild(lbl);
                popup.AddChild(popupCenter);
                _connectingOverlay.AddChild(popup);
                var canvas = new CanvasLayer { Layer = 50 };
                canvas.AddChild(_connectingOverlay);
                AddChild(canvas);
            }
            var msgLabel = _connectingOverlay.GetNodeOrNull<Label>("PopupPanel/CenterContainer/MessageLabel");
            if (msgLabel != null) msgLabel.Text = message;
            _connectingOverlay.Visible = true;
            _serverInput.Editable = false;
            _nameInput.Editable = false;
            _passwordInput.Editable = false;
            if (_connectAllBtn != null) _connectAllBtn.Disabled = true;
        }).CallDeferred();
    }

    private void HideConnectingOverlay()
    {
        Callable.From(() =>
        {
            if (_connectingOverlay != null) _connectingOverlay.Visible = false;
            _serverInput.Editable = true;
            _nameInput.Editable = true;
            _passwordInput.Editable = true;
            if (_connectAllBtn != null) _connectAllBtn.Disabled = false;
        }).CallDeferred();
    }

    private async void OnConnectAllPressed()
    {
        if (_isConnectingSlot || _selectedProfile == null || _selectedProfile.Slots.Count == 0) return;
        _isConnectingSlot = true;
        try
        {
            AP_Atlas.Core.Logger.LogInfo("Starting sequential connection for all slots...");
            foreach (var slotName in _selectedProfile.Slots)
            {
                if (string.IsNullOrWhiteSpace(slotName)) continue;
                ShowConnectingOverlay($"CONNECTING TO\n{slotName}...");
                await ConnectSlotInternalAsync(slotName, _selectedProfile);
                await ToSignal(GetTree().CreateTimer(1.5f), "timeout");
            }
        }
        finally
        {
            HideConnectingOverlay();
            _isConnectingSlot = false;
        }
    }

    private async void OnConnectSlotPressed(string slotName, MultiworldProfile profile)
    {
        if (_isConnectingSlot) return;
        _isConnectingSlot = true;
        ShowConnectingOverlay($"CONNECTING TO\n{slotName}...");
        await ConnectSlotInternalAsync(slotName, profile);
        HideConnectingOverlay();
        _isConnectingSlot = false;
    }

    private async System.Threading.Tasks.Task<bool> ConnectSlotInternalAsync(string slotName, MultiworldProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ServerUrl))
        {
            _statusLabel.Text = "Status: Server URL cannot be empty";
            _statusLabel.AddThemeColorOverride("font_color", Colors.Red);
            if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Error";
            return false;
        }
        if (string.IsNullOrWhiteSpace(slotName))
        {
            _statusLabel.Text = "Status: Slot Name cannot be empty";
            _statusLabel.AddThemeColorOverride("font_color", Colors.Red);
            if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Error";
            return false;
        }
        _statusLabel.Text = "Status: Connecting to " + slotName + "...";
        _statusLabel.AddThemeColorOverride("font_color", Colors.Yellow);
        if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connecting to " + profile.ServerUrl + " as " + slotName + "...";
        LogToSystem("[color=cyan]Attempting to connect to " + profile.ServerUrl + " as " + slotName + "...[/color]");
        _connectingSlots.Add(slotName);
        UpdateSidebar();
        try
        {
            var session = ArchipelagoSessionFactory.CreateSession(profile.ServerUrl);
            var earlyMessages = new List<Archipelago.MultiClient.Net.MessageLog.Messages.LogMessage>();
            void earlyHandler(Archipelago.MultiClient.Net.MessageLog.Messages.LogMessage msg)
            {
                lock (earlyMessages) earlyMessages.Add(msg);
            }
            session.MessageLog.OnMessageReceived += earlyHandler;
            session.Socket.ErrorReceived += (ex, msg) =>
            {
                LogToSystem("[color=red]Socket Error (" + slotName + "):[/color] " + msg);
                if (_globalStatusLabel != null)
                {
                    Callable.From(() => _globalStatusLabel.Text = "Socket Error: " + msg).CallDeferred();
                }
            };
            session.Socket.SocketClosed += (reason) =>
            {
                LogToSystem("[color=yellow]Socket Closed (" + slotName + "):[/color] " + reason);
                if (_globalStatusLabel != null)
                {
                    Callable.From(() => _globalStatusLabel.Text = "Disconnected: " + reason).CallDeferred();
                }
            };
            var connectTask = Task.Run(() => session.TryConnectAndLogin(
                "",
                slotName,
                Archipelago.MultiClient.Net.Enums.ItemsHandlingFlags.AllItems,
                new Version(0, 5, 0),
                new[] { "TextOnly" },
                null,
                profile.Password == "" ? null : profile.Password
            ));
            var timeoutTask = Task.Delay(10000);
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            if (completedTask == timeoutTask)
            {
                session.MessageLog.OnMessageReceived -= earlyHandler;
                _ = session.Socket.DisconnectAsync();
                Callable.From(() =>
                {
                    _statusLabel.Text = "Status: Connection Timeout";
                    _statusLabel.AddThemeColorOverride("font_color", Colors.Red);
                    if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Timeout (" + slotName + ")";
                    LogToSystem("[color=red]Connection timed out for " + slotName + " after 10 seconds.[/color]");
                    _connectingSlots.Remove(slotName);
                    UpdateSidebar();
                }).CallDeferred();
                return false;
            }
            var result = await connectTask;
            Callable.From(() =>
            {
                if (result.Successful)
                {
                    _statusLabel.Text = "Status: Connected successfully as " + slotName + "!";
                    _statusLabel.AddThemeColorOverride("font_color", Colors.Green);
                    if (_globalStatusLabel != null) _globalStatusLabel.Text = "Booting Engine for " + slotName + "...";
                    LogToSystem("[color=lime]Successfully authenticated as " + slotName + ".[/color]");
                    Dictionary<string, object> slotData = null;
                    try
                    {
                        var loginSuccess = (Archipelago.MultiClient.Net.LoginSuccessful)result;
                        slotData = loginSuccess.SlotData;
                    }
                    catch { }
                    foreach (Node n in ActiveSlotNodes())
                    {
                        if (n is SlotTrackerControl oldSlot && oldSlot.ProfileId == profile.Id && oldSlot.SlotName == slotName)
                        {
                            if (_currentSelectedSlot == oldSlot) _currentSelectedSlot = null;
                            if (oldSlot.Session?.Socket != null) _ = oldSlot.Session.Socket.DisconnectAsync();
                            oldSlot.QueueFree();
                        }
                    }
                    if (!profile.SavedStats.ContainsKey(slotName)) profile.SavedStats[slotName] = new SlotStats();
                    profile.SavedStats[slotName].GameName = session.ConnectionInfo.Game;
                    DataManager.SaveProfiles(_profiles);
                    var slotTracker = new SlotTrackerControl(session, profile.Id, slotName, _appSettings, slotData,
                        (msg) => { if (_globalStatusLabel != null) _globalStatusLabel.Text = msg; },
                        (msg) => { LogToDebug(msg, slotName); }
                    );
                    _terminalStage.AddChild(slotTracker);
                    session.MessageLog.OnMessageReceived -= earlyHandler;
                    _ = session.Socket.DisconnectAsync();
                    lock (earlyMessages)
                    {
                        if (earlyMessages.Count > 0) slotTracker.InjectEarlyMessages(earlyMessages);
                    }
                    _connectingSlots.Remove(slotName);
                    UpdateSidebar();
                    _currentSelectedSlot = slotTracker; RefreshContextViews();
                    var timer = GetTree().CreateTimer(0.1);
                    timer.Timeout += ApplyUIScale;
                }
                else
                {
                    session.MessageLog.OnMessageReceived -= earlyHandler;
                    _ = session.Socket.DisconnectAsync();
                    var loginFailure = (Archipelago.MultiClient.Net.LoginFailure)result;
                    string errs = string.Join(", ", loginFailure.Errors);
                    _statusLabel.Text = "Status: Failed to connect:\n" + errs;
                    _statusLabel.AddThemeColorOverride("font_color", Colors.Red);
                    if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Failed (" + slotName + ")";
                    LogToSystem("[color=red]Authentication Failed for " + slotName + ":[/color] " + errs);
                    _connectingSlots.Remove(slotName);
                    UpdateSidebar();
                }
            }).CallDeferred();
        }
        catch (System.Exception ex)
        {
            Callable.From(() =>
            {
                _statusLabel.Text = "Status: Connection Error:\n" + ex.Message;
                _statusLabel.AddThemeColorOverride("font_color", Colors.Red);
                if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Error";
                LogToSystem("[color=red]Exception during connection:[/color] " + ex.Message);
                _connectingSlots.Remove(slotName);
                UpdateSidebar();
            }).CallDeferred();
        }
        return true;
    }

    private StyleBoxFlat GetVSCodePanelStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = new Godot.Color("#1e1e1e"),
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            BorderColor = new Godot.Color("#444444"),
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 12,
            CornerRadiusBottomRight = 12,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 12,
            ContentMarginBottom = 12
        };
    }

    private void ChangeGlobalTab(int tab)
    {
        _currentGlobalTab = tab;
        if (tab == 0) { SwapSidebar("Connections", _connectionSidebarContent); SwapContentView(_connectionPanel); RefreshContextViews(); }
        else if (tab == 1) { SwapSidebar("Packs", _packManagerPanel.SidebarContent); SwapContentView(_packManagerPanel); RefreshContextViews(); }
        else RefreshContextViews(); // 2 Map Tracker, 3 Key Items, 4 Logic Tracker, 5 Item History
    }
}
