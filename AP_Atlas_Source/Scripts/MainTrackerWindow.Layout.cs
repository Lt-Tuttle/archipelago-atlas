#nullable disable
using System.Linq;
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;

/// <summary>Building the window: its panels, theme, fonts and the connection tab.</summary>
public partial class MainTrackerWindow : Control, AP_Atlas.UI.IPropertiesHost
{
    private Texture2D CreateIconFromSvg(string svgString)
    {
        var img = new Godot.Image();
        img.LoadSvgFromString(svgString);
        return ImageTexture.CreateFromImage(img);
    }
    private TabBar _bottomTabs;
    private PanelContainer _bottomContent;
    private VBoxContainer _sysLogVBox;
    private VBoxContainer _debugLogVBox;
    private void BuildBottomPanel()
    {
        _bottomPane = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(0, 150) };
        _bottomPane.AddThemeConstantOverride("separation", 0);
        var bottomHeader = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _bottomPane.AddChild(bottomHeader);
        _bottomTabs = new TabBar { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ApplyTabBarStyle(_bottomTabs, 8);
        _bottomTabs.AddTab("Chat");
        _bottomTabs.AddTab("System Log");
        _bottomTabs.AddTab("Debug Log");
        bottomHeader.AddChild(_bottomTabs);
        bottomHeader.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill }); // Spacer
        var bottomMenuBtn = new Button { Text = "...", Flat = true, AccessibilityName = Tr("More options") };
        _bottomMenuBtn = bottomMenuBtn;
        bottomMenuBtn.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.TextMuted);
        var extraItems = new System.Collections.Generic.Dictionary<string, System.Action> {
            { "Copy System Log", () => DisplayServer.ClipboardSet(_consoleOutput.GetParsedText()) },
            { "Clear System Log", () => _consoleOutput.Clear() },
            { "Copy Debug Log", () => DisplayServer.ClipboardSet(_debugLogConsole.GetParsedText()) },
            { "Clear Debug Log", () => _debugLogConsole.Clear() }
        };
        AttachFontMenuPopup(bottomMenuBtn,
            () => _appSettings.ConsoleFontSize,
            (newSize) => { _appSettings.ConsoleFontSize = newSize; ApplyUIScale(); DataManager.SaveSettings(_appSettings); },
            extraItems,
            item => !item.Contains("Debug Log") || _appSettings.DeveloperMode // the Debug Log's items are for developer mode
        );
        bottomHeader.AddChild(bottomMenuBtn);
        _terminalStage = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var contentStyle = GetVSCodePanelStyle();
        contentStyle.CornerRadiusTopLeft = 0;
        _terminalStage.AddThemeStyleboxOverride("panel", contentStyle);
        _bottomPane.AddChild(_terminalStage);
        _sysLogVBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _consoleOutput = new AP_Atlas.UI.SafeRichText { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, ScrollFollowing = true, SelectionEnabled = true };
        _sysLogVBox.AddChild(_consoleOutput);
        _systemLog = new AP_Atlas.UI.LogPane(_consoleOutput);
        _terminalStage.AddChild(_sysLogVBox);
        _debugLogVBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _debugLogConsole = new AP_Atlas.UI.SafeRichText { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, ScrollFollowing = true, SelectionEnabled = true };
        _debugLogVBox.AddChild(_debugLogConsole);
        _debugLog = new AP_Atlas.UI.LogPane(_debugLogConsole);
        _terminalStage.AddChild(_debugLogVBox);
        _bottomTabs.TabSelected += (long tab) =>
        {
            _currentTerminalTab = (int)tab;
            RefreshTerminalView();
        };
        _contentSplit.AddChild(_bottomPane);
    }
    private Label _sidebarTitle;
    private Button _sidebarMenuBtn;
    private Label _propsTitle;
    private Button _propsMenuBtn;
    private HBoxContainer _sidebarHeaderBox;
    private HBoxContainer _midLeftHeaderBox;
    private HBoxContainer _propsHeaderBox;
    private Button _bottomMenuBtn;

    /// <summary>
    /// A "..." menu: the font size, then <paramref name="extraItems"/> as buttons; <paramref name="itemShown"/> says, as
    /// the menu opens, which of them show now.
    /// </summary>
    private void AttachFontMenuPopup(Button menuBtn, System.Func<int> getFontSize, System.Action<int> setFontSize, System.Collections.Generic.Dictionary<string, System.Action> extraItems = null, System.Func<string, bool> itemShown = null)
    {
        var itemButtons = new System.Collections.Generic.List<(string Item, Button Button)>();
        var popup = new PopupPanel { Transient = true };
        var customPopupStyle = new StyleBoxFlat
        {
            BgColor = AP_Atlas.Core.ThemeColors.SurfacePanel,
            BorderColor = AP_Atlas.Core.ThemeColors.BorderSoft,
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
        var minusBtn = new Button { Text = "-", CustomMinimumSize = new Godot.Vector2(28, 28), AccessibilityName = Tr("Smaller text") };
        var plusBtn = new Button { Text = "+", CustomMinimumSize = new Godot.Vector2(28, 28), AccessibilityName = Tr("Larger text") };
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
                itemButtons.Add((kvp.Key, btn));
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
            foreach (var (item, button) in itemButtons) button.Visible = itemShown?.Invoke(item) ?? true;
            popup.Popup();
        };
    }
    private HBoxContainer CreateSidebarHeader(string titleText, out Label titleLabel, out Button menuBtn, System.Func<int> getFontSize, System.Action<int> setFontSize)
    {
        var headerBox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Name = "HeaderBox" };
        titleLabel = new Label { Name = "FixedHeaderTitle", Text = titleText, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Left };
        titleLabel.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.TextMuted);
        headerBox.AddChild(titleLabel);
        menuBtn = new Button { Text = "...", Flat = true, AccessibilityName = Tr("More options") };
        menuBtn.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.TextMuted);
        AttachFontMenuPopup(menuBtn, getFontSize, setFontSize);
        headerBox.AddChild(menuBtn);
        return headerBox;
    }
    private void SetupModernTheme()
    {
        var theme = new Theme();
        AP_Atlas.Core.ThemeColors.SetAccent(_appSettings.ThemeAccentColor);
        var accentColor = AP_Atlas.Core.ThemeColors.Accent;
        var textOnAccent = AP_Atlas.Core.ThemeColors.TextOnAccent;
        var panelBg = new StyleBoxFlat
        {
            BgColor = AP_Atlas.Core.ThemeColors.SurfacePanel,
            BorderColor = AP_Atlas.Core.ThemeColors.Border, // Dark VS Code border
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
            BgColor = AP_Atlas.Core.ThemeColors.Surface,
            BorderColor = AP_Atlas.Core.ThemeColors.Border,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1
        };
        theme.SetStylebox("panel", "TabContainer", tabPanel);
        var tabSelected = new StyleBoxFlat
        {
            BgColor = AP_Atlas.Core.ThemeColors.Surface,
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
            BgColor = AP_Atlas.Core.ThemeColors.SurfaceRaised,
            BorderWidthTop = 0,
            BorderColor = AP_Atlas.Core.ThemeColors.Border,
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
        var tabActive = new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.Surface, BorderWidthTop = 2, BorderColor = accentColor, ContentMarginLeft = 15, ContentMarginRight = 15, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("tab_selected", "TabContainer", tabActive);
        var tabInactive = new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfaceRaised, ContentMarginLeft = 15, ContentMarginRight = 15, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("tab_unselected", "TabContainer", tabInactive);
        var btnNormal = new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.Control, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("normal", "Button", btnNormal);
        var btnHover = new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.ControlHover, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("hover", "Button", btnHover);
        var btnPressed = new StyleBoxFlat { BgColor = accentColor, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("pressed", "Button", btnPressed);
        theme.SetStylebox("hover_pressed", "Button", btnPressed);
        // Every state needs the same content margins: Godot 4.3 measured a button with its current stylebox and didn't
        // re-measure on Disabled changes, so a button created disabled with Godot's thinner default clipped its text.
        var btnDisabled = new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfaceRaised, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("disabled", "Button", btnDisabled);
        // The focus ring: the keyboard's place, drawn over the control's own look, for every control that can take the focus.
        var focusRing = new StyleBoxFlat { DrawCenter = false, BorderColor = AP_Atlas.Core.ThemeColors.Heading, BorderWidthTop = 2, BorderWidthBottom = 2, BorderWidthLeft = 2, BorderWidthRight = 2, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 };
        foreach (string focusable in new[] { "Button", "CheckBox", "CheckButton", "OptionButton", "MenuButton", "LinkButton", "LineEdit", "TextEdit", "Tree", "ItemList", "ColorPickerButton" })
            theme.SetStylebox("focus", focusable, focusRing);
        theme.SetColor("font_disabled_color", "Button", new Godot.Color(1, 1, 1, 0.35f));
        theme.SetColor("font_pressed_color", "Button", textOnAccent);
        theme.SetColor("font_hover_pressed_color", "Button", textOnAccent);
        var cbNormal = new StyleBoxEmpty();
        theme.SetStylebox("normal", "CheckBox", cbNormal);
        theme.SetStylebox("hover", "CheckBox", cbNormal);
        theme.SetStylebox("pressed", "CheckBox", cbNormal);
        theme.SetStylebox("hover_pressed", "CheckBox", cbNormal);
        theme.SetStylebox("disabled", "CheckBox", cbNormal);
        var lineEdit = new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.Input, CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2, ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6, BorderWidthBottom = 1, BorderColor = accentColor };
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
        theme.SetStylebox("panel", "PopupMenu", new StyleBoxFlat
        {
            BgColor = AP_Atlas.Core.ThemeColors.SurfacePanel,
            BorderColor = AP_Atlas.Core.ThemeColors.BorderSoft,
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
        });
        // Every control's text colours from the palette: Godot's own defaults are white, which vanish on a light theme.
        var text = AP_Atlas.Core.ThemeColors.Text;
        var muted = AP_Atlas.Core.ThemeColors.TextSubtle;
        var disabled = AP_Atlas.Core.ThemeColors.Disabled;
        foreach (string type in new[] { "Label", "Button", "CheckBox", "CheckButton", "OptionButton", "MenuButton", "LinkButton", "LineEdit", "TextEdit", "Tree", "ItemList", "PopupMenu", "TabBar", "TabContainer", "RichTextLabel", "SpinBox", "ProgressBar", "AcceptDialog", "Window" })
        {
            theme.SetColor("font_color", type, text);
            theme.SetColor("font_disabled_color", type, disabled);
        }
        foreach (string type in new[] { "Button", "CheckBox", "CheckButton", "OptionButton", "MenuButton", "LinkButton" })
        {
            theme.SetColor("font_hover_color", type, text);
            theme.SetColor("font_focus_color", type, text);
            theme.SetColor("font_hover_pressed_color", type, textOnAccent);
            theme.SetColor("font_pressed_color", type, textOnAccent);
        }
        theme.SetColor("font_disabled_color", "Button", new Godot.Color(text, 0.35f));
        theme.SetColor("placeholder_color", "LineEdit", muted);
        theme.SetColor("placeholder_color", "TextEdit", muted);
        theme.SetColor("font_selected_color", "LineEdit", text);
        theme.SetColor("default_color", "RichTextLabel", text);
        theme.SetColor("font_selected_color", "Tree", text);
        theme.SetColor("font_selected_color", "ItemList", text);
        theme.SetColor("font_hover_color", "PopupMenu", text);
        theme.SetColor("font_accelerator_color", "PopupMenu", muted);
        theme.SetColor("font_selected_color", "TabBar", text);
        theme.SetColor("font_unselected_color", "TabBar", muted);
        theme.SetColor("font_hovered_color", "TabBar", text);
        theme.SetColor("font_selected_color", "TabContainer", text);
        theme.SetColor("font_unselected_color", "TabContainer", muted);
        theme.SetColor("font_hovered_color", "TabContainer", text);
        theme.SetColor("title_color", "Window", text);
        theme.SetColor("title_color", "AcceptDialog", text);
        theme.SetColor("guide_color", "Tree", AP_Atlas.Core.ThemeColors.BorderSoft);
        theme.SetColor("title_button_color", "Tree", text);
        theme.SetStylebox("panel", "Tree", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfaceSunken, BorderColor = AP_Atlas.Core.ThemeColors.Border, BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4 });
        theme.SetStylebox("panel", "ItemList", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfaceSunken, BorderColor = AP_Atlas.Core.ThemeColors.Border, BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4 });
        theme.SetStylebox("normal", "TextEdit", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.Input, BorderColor = AP_Atlas.Core.ThemeColors.BorderSoft, BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 4, ContentMarginBottom = 4 });
        theme.SetStylebox("normal", "RichTextLabel", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfaceSunken, ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4 });
        theme.SetStylebox("panel", "AcceptDialog", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfacePanel, BorderColor = AP_Atlas.Core.ThemeColors.BorderSoft, BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 12, ContentMarginBottom = 12 });
        // An embedded window's title strip is drawn by its border's top edge, so that edge takes a surface colour, not the line's.
        theme.SetStylebox("embedded_border", "Window", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfacePanel, BorderColor = AP_Atlas.Core.ThemeColors.SurfaceRaised, BorderWidthTop = 28, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ExpandMarginTop = 28, ExpandMarginLeft = 1, ExpandMarginRight = 1, ExpandMarginBottom = 1 });
        this.Theme = theme;
        // Popups and windows (dialogs, the Pack Doctor) live under the root, not this control, so share the theme there too.
        if (IsInsideTree()) GetTree().Root.Theme = theme;
        RenderingServer.SetDefaultClearColor(AP_Atlas.Core.ThemeColors.Surface);
        if (_globalStatusBar != null)
        {
            var statusStyle = new StyleBoxFlat { BgColor = accentColor, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 2, ContentMarginBottom = 2 };
            _globalStatusBar.AddThemeStyleboxOverride("panel", statusStyle);
            _globalStatusLabel?.AddThemeColorOverride("font_color", textOnAccent);
            _statusConnectedLabel?.AddThemeColorOverride("font_color", textOnAccent);
        }
        _activityBar?.ApplyAccent(AP_Atlas.Core.ThemeColors.Accent);
        if (_bottomTabs != null) ApplyTabBarStyle(_bottomTabs, 8);
        ApplyUIScale();
    }
    /// <summary>Recolours the window for a new accent (the Settings page's choice), and remembers it.</summary>
    private void ApplyAccent(string hex)
    {
        _appSettings.ThemeAccentColor = hex;
        DataManager.SaveSettings(_appSettings);
        SetupModernTheme();
        AP_Atlas.UI.Kit.RecolourHeadings(GetTree().Root);
        _homePage?.RefreshWordmark();
        RefreshProfileListStyles();
        UpdateSidebar();
        _settingsPage?.RefreshRows(); // the preset choice and the custom colour show the accent as it is now
    }
    /// <summary>The zoom choices, in percent, as the Settings page and Ctrl+= / Ctrl+- step through them.</summary>
    internal static readonly int[] ZoomSteps = { 75, 90, 100, 110, 125, 150, 175, 200 };

    /// <summary>Scales the whole window (every control, text and picture) by the zoom setting.</summary>
    private void ApplyZoom() => GetWindow().ContentScaleFactor = _appSettings.UiZoom / 100f;

    private void SetZoom(int percent)
    {
        _appSettings.UiZoom = Math.Clamp(percent, 50, 200);
        DataManager.SaveSettings(_appSettings);
        ApplyZoom();
        _settingsPage?.RefreshRows();
    }

    /// <summary>One zoom step up (+1) or down (-1); from between two steps, to the next one that way.</summary>
    private void ZoomBy(int direction)
    {
        int index = Array.IndexOf(ZoomSteps, _appSettings.UiZoom);
        if (index < 0)
        {
            int above = Array.FindIndex(ZoomSteps, step => step > _appSettings.UiZoom);
            index = above < 0 ? ZoomSteps.Length : above; // the step above, so one down lands on the step below
            if (direction > 0) index--;
        }
        SetZoom(ZoomSteps[Math.Clamp(index + direction, 0, ZoomSteps.Length - 1)]);
    }

    /// <summary>The tool Atlas opens on, as the startup setting says: Home, the tool shown when it closed, or Multiworlds.</summary>
    internal static AP_Atlas.UI.Tool StartupTool(AppSettings settings) => settings.StartupPage switch
    {
        "multiworlds" => AP_Atlas.UI.Tool.Connections,
        "last" => AP_Atlas.UI.Tool.Named(settings.LastTool) ?? AP_Atlas.UI.Tool.Home,
        _ => AP_Atlas.UI.Tool.Home
    };

    private void ApplyUIScale()
    {
        using var _ = AP_Atlas.Core.PerfMonitor.Measure("Apply font sizes (whole UI)");

        int globalSize = _appSettings.GlobalFontSize;
        if (this.Theme == null) this.Theme = new Theme();
        // Any Theme mutation re-themes every control in the window (forcing all text to re-layout),
        // even when the value is unchanged, so only touch it when the size actually changed.
        SetThemeFontSizeIfChanged(this.Theme, "PopupMenu", globalSize);
        SetThemeFontSizeIfChanged(this.Theme, "MenuButton", globalSize);
        if (_menuHbox != null) SetFontSizeRecursive(_menuHbox, globalSize);
        if (_toolTitle != null) SetFontSizeOverride(_toolTitle, "font_size", globalSize);
        if (_bottomTabs != null) SetFontSizeOverride(_bottomTabs, "font_size", globalSize);
        if (_globalStatusBar != null) SetFontSizeRecursive(_globalStatusBar, globalSize);
        if (_sidebarTitle != null) SetFontSizeOverride(_sidebarTitle, "font_size", globalSize);
        if (_midLeftTitle != null) SetFontSizeOverride(_midLeftTitle, "font_size", globalSize);
        if (_propsTitle != null) SetFontSizeOverride(_propsTitle, "font_size", globalSize);
        if (_sidebar != null) SetFontSizeRecursive(_sidebar, _appSettings.SlotsFontSize);
        if (_midLeftSidebar != null) SetFontSizeRecursive(_midLeftSidebar, _appSettings.ExplorerFontSize);
        if (_propertiesSidebar != null) SetFontSizeRecursive(_propertiesSidebar, _appSettings.PropertiesFontSize);
        if (_contentStage != null) SetFontSizeRecursive(_contentStage, _appSettings.ContentFontSize);
        if (_terminalStage != null) SetFontSizeRecursive(_terminalStage, _appSettings.ConsoleFontSize);
    }
    private static void SetThemeFontSizeIfChanged(Theme theme, string themeType, int size)
    {
        if (theme.HasFontSize("font_size", themeType) && theme.GetFontSize("font_size", themeType) == size) return;
        theme.SetFontSize("font_size", themeType, size);
    }
    /// <summary>
    /// Adds a font size override only when it differs from the current one: every override change makes the
    /// control re-shape its text, which is expensive across thousands of chat lines and tree rows.
    /// </summary>
    private static void SetFontSizeOverride(Control c, StringName name, int size)
    {
        if (c.HasThemeFontSizeOverride(name) && c.GetThemeFontSize(name) == size) return;
        c.AddThemeFontSizeOverride(name, size);
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
            if (c is Label || c is Button || c is LineEdit || c is CheckBox || c is TabContainer || c is Tree || c is ItemList)
            {
                SetFontSizeOverride(c, "font_size", finalSize);
                if (c is Tree t) SetFontSizeOverride(t, "title_button_font_size", finalSize);
            }
            else if (c is RichTextLabel rtl)
            {
                SetFontSizeOverride(rtl, "normal_font_size", finalSize);
                SetFontSizeOverride(rtl, "mono_font_size", finalSize);
            }
            if (c is MenuButton mb)
            {
                SetFontSizeRecursive(mb.GetPopup(), size);
            }
        }
        else if (node is PopupMenu pm)
        {
            if (!(pm.HasThemeFontSizeOverride("font_size") && pm.GetThemeFontSize("font_size") == size))
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
        title.SetMeta("font_size_ratio", 1.7f);
        rightVbox.AddChild(title);
        rightVbox.AddChild(new HSeparator());
        // What's typed goes into the multiworld at once (so nothing is lost when another is selected); Save writes it to disk.
        _nameInput = new LineEdit { PlaceholderText = "Profile Name", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _nameInput.TextChanged += text => EditSelected(profile => profile.Name = text);
        rightVbox.AddChild(new Label { Text = "Profile Name:" });
        rightVbox.AddChild(_nameInput);
        _roomLinkInput = new LineEdit { PlaceholderText = Tr("https://archipelago.gg/room/… (optional)"), SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = Tr("Room link") };
        _roomLinkInput.TextChanged += text => EditSelected(profile => profile.RoomLink = text.Trim());
        var roomRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        roomRow.AddThemeConstantOverride("separation", 6);
        roomRow.AddChild(_roomLinkInput);
        _fillFromRoomButton = AP_Atlas.UI.Kit.Button(Tr("Fill from link"), Tr("Reads the room's status page once (the port it runs on and its players) and fills in the server address and the slots. Atlas asks first, and never requests the room's own page."), FillFromRoomLink);
        roomRow.AddChild(_fillFromRoomButton);
        rightVbox.AddChild(new Label { Text = Tr("Room link:") });
        rightVbox.AddChild(roomRow);
        _serverInput = new LineEdit { PlaceholderText = "Server (e.g. archipelago.gg:38281)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _serverInput.TextChanged += text => EditSelected(profile => profile.ServerUrl = text.Trim());
        rightVbox.AddChild(new Label { Text = "Server Address:" });
        rightVbox.AddChild(_serverInput);
        _passwordInput = new LineEdit { PlaceholderText = "Password (Optional)", Secret = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _passwordInput.TextChanged += text => EditSelected(profile => profile.Password = text);
        rightVbox.AddChild(new Label { Text = "Password:" });
        rightVbox.AddChild(_passwordInput);
        _cheeseInput = new LineEdit { PlaceholderText = "Cheese Tracker link, or the archipelago.gg room link (optional)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _cheeseInput.TextChanged += (_) => MarkDirty();
        rightVbox.AddChild(new Label { Text = "Cheese Tracker:", TooltipText = "Links this multiworld to its Cheese Tracker page (the Cheese Tracker tab shows it; its Settings explain what Atlas does with it)", MouseFilter = MouseFilterEnum.Pass });
        rightVbox.AddChild(_cheeseInput);
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
    /// <summary>Styles a VS Code-like tab strip whose selected tab is underlined (top border) in the accent color.</summary>
    private static void ApplyTabBarStyle(TabBar bar, int cornerRadius)
    {
        StyleBoxFlat Tab(Godot.Color bg, Godot.Color border) => new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = cornerRadius,
            CornerRadiusTopRight = cornerRadius,
            BorderWidthTop = 2,
            BorderColor = border,
            ContentMarginLeft = 24,
            ContentMarginRight = 24,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };
        var clear = new Godot.Color(0, 0, 0, 0);
        bar.AddThemeStyleboxOverride("tab_unselected", Tab(clear, clear));
        bar.AddThemeStyleboxOverride("tab_hovered", Tab(AP_Atlas.Core.ThemeColors.SurfaceRaised, clear));
        bar.AddThemeStyleboxOverride("tab_selected", Tab(AP_Atlas.Core.ThemeColors.Surface, AP_Atlas.Core.ThemeColors.Accent));
    }
    private StyleBoxFlat GetVSCodePanelStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = AP_Atlas.Core.ThemeColors.Surface,
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            BorderColor = AP_Atlas.Core.ThemeColors.BorderSoft,
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
}
