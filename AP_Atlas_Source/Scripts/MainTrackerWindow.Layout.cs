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
        _bottomPane = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(0, BottomPaneMinHeight) };
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
        var bottomMenuBtn = new Button { Text = "...", ThemeTypeVariation = "QuietButton", AccessibilityName = Tr("More options") };
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
        _consoleOutput = new AP_Atlas.UI.SafeRichText { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, ScrollFollowing = true, SelectionEnabled = true, ThemeTypeVariation = LogTextVariation };
        _sysLogVBox.AddChild(_consoleOutput);
        _systemLog = new AP_Atlas.UI.LogPane(_consoleOutput);
        _systemLog.Appended += () => MarkTerminalTabNew(1);
        _terminalStage.AddChild(_sysLogVBox);
        _debugLogVBox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _debugLogConsole = new AP_Atlas.UI.SafeRichText { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, ScrollFollowing = true, SelectionEnabled = true, ThemeTypeVariation = LogTextVariation };
        _debugLogVBox.AddChild(_debugLogConsole);
        _debugLog = new AP_Atlas.UI.LogPane(_debugLogConsole);
        _terminalStage.AddChild(_debugLogVBox);
        // The Chat tab without a slot says what it will show, rather than an empty box.
        _chatEmptyHint = new CenterContainer { Name = "ChatEmptyHint", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
        var chatHint = AP_Atlas.UI.Kit.Subtle(Tr("The selected slot's room shows here: chat, hints, items found and sent, and its commands."));
        chatHint.HorizontalAlignment = HorizontalAlignment.Center;
        chatHint.CustomMinimumSize = new Godot.Vector2(360, 0);
        _chatEmptyHint.AddChild(chatHint);
        _terminalStage.AddChild(_chatEmptyHint);
        _bottomTabs.TabSelected += (long tab) =>
        {
            _currentTerminalTab = (int)tab;
            ClearTerminalTabNew((int)tab);
            RefreshTerminalView();
        };
        _contentSplit.AddChild(_bottomPane);
    }

    // ---- "New lines" on the bottom pane's tabs: a small dot on a tab that isn't showing, cleared when it is ----

    /// <summary>A small disc in the palette's "pending" colour (an 8 px texture, drawn once per call; it's tiny).</summary>
    private static Texture2D NewLinesDot()
    {
        var image = Godot.Image.CreateEmpty(8, 8, false, Godot.Image.Format.Rgba8);
        image.Fill(new Color(0, 0, 0, 0));
        var colour = AP_Atlas.Core.ThemeColors.Pending;
        for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
                if ((x - 3.5f) * (x - 3.5f) + (y - 3.5f) * (y - 3.5f) <= 3.6f * 3.6f) image.SetPixel(x, y, colour);
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>Marks a bottom pane tab as holding lines not seen yet, unless it's the one showing.</summary>
    private void MarkTerminalTabNew(int tab)
    {
        if (_bottomTabs == null || tab < 0 || tab >= _bottomTabs.TabCount || _bottomTabs.IsTabHidden(tab)) return;
        bool showing = _bottomPane != null && _bottomPane.Visible && _currentTerminalTab == tab;
        if (showing || _bottomTabs.GetTabIcon(tab) != null) return;
        _bottomTabs.SetTabIcon(tab, NewLinesDot());
        _bottomTabs.SetTabTooltip(tab, Tr("New lines"));
    }

    private void ClearTerminalTabNew(int tab)
    {
        if (_bottomTabs == null || tab < 0 || tab >= _bottomTabs.TabCount || _bottomTabs.GetTabIcon(tab) == null) return;
        _bottomTabs.SetTabIcon(tab, null);
        _bottomTabs.SetTabTooltip(tab, "");
    }

    /// <summary>Whether a bottom pane tab carries the "new lines" dot (for tests).</summary>
    public bool TerminalTabHasNew(int tab) => _bottomTabs != null && tab >= 0 && tab < _bottomTabs.TabCount && _bottomTabs.GetTabIcon(tab) != null;
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
                var btn = new Button { Text = kvp.Key, ThemeTypeVariation = "QuietButton", Alignment = HorizontalAlignment.Left };
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
        menuBtn = new Button { Text = "...", ThemeTypeVariation = "QuietButton", AccessibilityName = Tr("More options") };
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
        // A primary button: the one main action of a page, filled with the accent (its text in the colour that reads on it).
        theme.SetTypeVariation(AP_Atlas.UI.Kit.PrimaryButton, "Button");
        StyleBoxFlat Primary(Godot.Color bg) => new StyleBoxFlat { BgColor = bg, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("normal", AP_Atlas.UI.Kit.PrimaryButton, Primary(accentColor));
        theme.SetStylebox("hover", AP_Atlas.UI.Kit.PrimaryButton, Primary(accentColor.Lightened(0.12f)));
        theme.SetStylebox("pressed", AP_Atlas.UI.Kit.PrimaryButton, Primary(accentColor.Darkened(0.15f)));
        theme.SetStylebox("hover_pressed", AP_Atlas.UI.Kit.PrimaryButton, Primary(accentColor.Darkened(0.15f)));
        foreach (string state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
            theme.SetColor(state, AP_Atlas.UI.Kit.PrimaryButton, textOnAccent);
        // A quiet button (the kit's "flat"): no background until hovered, a tint of the accent while pressed, so a press
        // is seen. Godot's own "flat" drew nothing in any state.
        theme.SetTypeVariation("QuietButton", "Button");
        theme.SetStylebox("normal", "QuietButton", new StyleBoxEmpty { ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 });
        theme.SetStylebox("disabled", "QuietButton", new StyleBoxEmpty { ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 });
        theme.SetStylebox("hover", "QuietButton", btnHover);
        var quietPressed = new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.AccentTint, BorderWidthLeft = 2, BorderColor = accentColor, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5 };
        theme.SetStylebox("pressed", "QuietButton", quietPressed);
        theme.SetStylebox("hover_pressed", "QuietButton", quietPressed);
        theme.SetStylebox("focus", "QuietButton", focusRing);
        theme.SetColor("font_pressed_color", "QuietButton", AP_Atlas.Core.ThemeColors.Text);
        theme.SetColor("font_hover_pressed_color", "QuietButton", AP_Atlas.Core.ThemeColors.Text);
        var cbNormal = new StyleBoxEmpty();
        theme.SetStylebox("normal", "CheckBox", cbNormal);
        theme.SetStylebox("hover", "CheckBox", cbNormal);
        theme.SetStylebox("pressed", "CheckBox", cbNormal);
        theme.SetStylebox("hover_pressed", "CheckBox", cbNormal);
        theme.SetStylebox("disabled", "CheckBox", cbNormal);
        // A switch is its own picture (ApplyCheckIcons): no button box around it, on or off.
        foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" }) theme.SetStylebox(state, "CheckButton", cbNormal);
        var lineEdit = new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.Input, CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2, ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6, BorderWidthBottom = 1, BorderColor = accentColor };
        theme.SetStylebox("normal", "LineEdit", lineEdit);
        // A box that can't be typed in now (nothing selected): the panel's surface with a soft edge, not Godot's grey slab.
        theme.SetStylebox("read_only", "LineEdit", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfacePanel, CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2, ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6, BorderWidthBottom = 1, BorderColor = AP_Atlas.Core.ThemeColors.BorderSoft });
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
        Font monoFont;
        if (GD.Load<FontFile>("res://Assets/Fonts/GoogleSansCode-Regular.ttf") is { } codeFile)
        {
            codeFile.MultichannelSignedDistanceField = true;
            monoFont = codeFile;
        }
        else
        {
            monoFont = new SystemFont { MultichannelSignedDistanceField = true, FontNames = new[] { "Google Sans Code", "Consolas", "monospace" } };
        }
        // Rich text reads like the labels around it: the same face, with a bold and an italic drawn from it (Godot's own
        // bold comes from its built-in font, a second typeface wherever text was emphasised). Code and logs keep the code
        // font: [code] through mono_font, the logs through the LogText variation.
        var prose = theme.DefaultFont;
        var slant = new Transform2D(1f, 0.2f, 0f, 1f, 0f, 0f);
        // The emboldened and slanted faces are drawn from a copy without the distance field: emboldening a distance-field
        // font smears its edges, while a plain one is rasterised for each size and stays clean.
        Font drawn = prose;
        if (prose is FontFile proseFile && proseFile.Duplicate() is FontFile plain)
        {
            plain.MultichannelSignedDistanceField = false;
            drawn = plain;
        }
        theme.SetFont("normal_font", "RichTextLabel", prose);
        theme.SetFont("bold_font", "RichTextLabel", new FontVariation { BaseFont = drawn, VariationEmbolden = 0.6f });
        theme.SetFont("italics_font", "RichTextLabel", new FontVariation { BaseFont = drawn, VariationTransform = slant });
        theme.SetFont("bold_italics_font", "RichTextLabel", new FontVariation { BaseFont = drawn, VariationEmbolden = 0.6f, VariationTransform = slant });
        theme.SetFont("mono_font", "RichTextLabel", monoFont);
        theme.SetTypeVariation(LogTextVariation, "RichTextLabel");
        theme.SetFont("normal_font", LogTextVariation, monoFont);
        theme.SetFont("bold_font", LogTextVariation, new FontVariation { BaseFont = monoFont, VariationEmbolden = 0.45f });
        ApplyCheckIcons(theme);
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
            // A pressed button sits on the accent; a checked box or switch has no fill, so its text keeps the text colour.
            bool fill = type is not ("CheckBox" or "CheckButton");
            theme.SetColor("font_hover_pressed_color", type, fill ? textOnAccent : text);
            theme.SetColor("font_pressed_color", type, fill ? textOnAccent : text);
        }
        theme.SetColor("font_disabled_color", "Button", new Godot.Color(text, 0.35f));
        // Godot 4 names it font_placeholder_color; its default (a pale grey at 60%) all but vanished on the Light theme.
        theme.SetColor("font_placeholder_color", "LineEdit", muted);
        theme.SetColor("font_uneditable_color", "LineEdit", AP_Atlas.Core.ThemeColors.TextMuted);
        theme.SetColor("font_placeholder_color", "TextEdit", muted);
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
        theme.SetColor("title_button_color", "Tree", AP_Atlas.Core.ThemeColors.TextMuted);
        StyleBoxFlat ColumnTitle(Godot.Color bg) => new StyleBoxFlat { BgColor = bg, BorderColor = AP_Atlas.Core.ThemeColors.Border, BorderWidthBottom = 1, ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 4, ContentMarginBottom = 4 };
        theme.SetStylebox("title_button_normal", "Tree", ColumnTitle(AP_Atlas.Core.ThemeColors.SurfaceRaised));
        theme.SetStylebox("title_button_hover", "Tree", ColumnTitle(AP_Atlas.Core.ThemeColors.ControlHover));
        theme.SetStylebox("title_button_pressed", "Tree", ColumnTitle(AP_Atlas.Core.ThemeColors.ControlHover));
        theme.SetStylebox("panel", "Tree", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfaceSunken, BorderColor = AP_Atlas.Core.ThemeColors.Border, BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4 });
        theme.SetStylebox("panel", "ItemList", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfaceSunken, BorderColor = AP_Atlas.Core.ThemeColors.Border, BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4 });
        theme.SetStylebox("normal", "TextEdit", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.Input, BorderColor = AP_Atlas.Core.ThemeColors.BorderSoft, BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 4, ContentMarginBottom = 4 });
        theme.SetStylebox("normal", "RichTextLabel", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfaceSunken, ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4 });
        theme.SetStylebox("panel", "AcceptDialog", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfacePanel, BorderColor = AP_Atlas.Core.ThemeColors.BorderSoft, BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 12, ContentMarginBottom = 12 });
        // An embedded window's title strip is drawn by its border's top edge, so that edge takes a surface colour, not the line's.
        theme.SetStylebox("embedded_border", "Window", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.SurfacePanel, BorderColor = AP_Atlas.Core.ThemeColors.SurfaceRaised, BorderWidthTop = 28, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ExpandMarginTop = 28, ExpandMarginLeft = 1, ExpandMarginRight = 1, ExpandMarginBottom = 1 });
        this.Theme = theme;
        _alerts?.UseTheme(theme);
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

    /// <summary>Scales the whole window (every control, text and picture) by Windows' display scale and the zoom setting, and refits the panes.</summary>
    private void ApplyZoom()
    {
        AP_Atlas.UI.WindowFit.ApplyRootScale(GetWindow(), _appSettings, width => { if (_contentStage != null) ApplyWindowParts(width); }); // the panes exist once the window is built
    }

    /// <summary>
    /// Settings from before the zoom was relative to Windows' scale: a zoom set to make up for the scale (150% on a 150%
    /// monitor) becomes 100%, the size Windows gives other apps. Once, when the file is first loaded by this Atlas.
    /// </summary>
    private void MigrateZoom()
    {
        if (_appSettings.SettingsVersion >= AppSettings.CurrentSettingsVersion) return;
        if (_appSettings.SettingsVersion < 1)
        {
            float windows = AP_Atlas.UI.WindowFit.WindowsScale(GetWindow().CurrentScreen);
            if (_appSettings.UiZoom != 100 && windows > 1.01f)
            {
                int relative = (int)Math.Round(_appSettings.UiZoom / windows);
                _appSettings.UiZoom = ZoomSteps.OrderBy(step => Math.Abs(step - relative)).First();
            }
        }
        // 2: the map list's order became most checks first by default (the A–Z of older files was the old default, not a choice).
        if (_appSettings.SettingsVersion < 2) _appSettings.MapSortIndex = 1;
        _appSettings.SettingsVersion = AppSettings.CurrentSettingsVersion;
        DataManager.SaveSettings(_appSettings);
    }

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
                    iconBtn.ThemeTypeVariation = "QuietButton";
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
                foreach (string slot in AP_Atlas.UI.Kit.RichTextSizeSlots) SetFontSizeOverride(rtl, slot, finalSize);
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
    private GridContainer _editorGrid; // the Multiworlds editor's label | box columns
    /// <summary>The Multiworlds editor's grid of labels and boxes (for tests).</summary>
    internal GridContainer EditorGrid => _editorGrid;
    private void BuildConnectionTab()
    {
        _connectionSidebarContent = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var listScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _connectionSidebarContent.AddChild(listScroll);
        _profileListContainer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        listScroll.AddChild(_profileListContainer);
        var addButton = new Button { Text = Tr("+ Add Multiworld") };
        addButton.Pressed += OnAddProfilePressed;
        _connectionSidebarContent.AddChild(addButton);
        _midLeftVBox.AddChild(_connectionSidebarContent);
        _connectionPanel = new MarginContainer { Name = "Connection", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _connectionPanel.AddThemeConstantOverride("margin_left", 20);
        _connectionPanel.AddThemeConstantOverride("margin_top", 20);
        _connectionPanel.AddThemeConstantOverride("margin_right", 20);
        _connectionPanel.AddThemeConstantOverride("margin_bottom", 20);
        _contentStage.AddChild(_connectionPanel);
        // The whole editor scrolls as one (a short window used to squeeze the slot rows under the boxes above them).
        var editorScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _connectionPanel.AddChild(editorScroll);
        var rightVbox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        editorScroll.AddChild(rightVbox);
        var title = new Label { Text = Tr("Multiworld Details"), HorizontalAlignment = HorizontalAlignment.Left };
        title.SetMeta("font_size_ratio", 1.5f);
        rightVbox.AddChild(title);
        rightVbox.AddChild(new HSeparator());
        // What's typed goes into the multiworld at once (so nothing is lost when another is selected); Save writes it to disk.
        // Each box beside its label and an (i) that says what goes there, where it comes from and whether it's needed, in two
        // aligned columns (the labels used to sit above the boxes, which cost a row of height each).
        Button Info(string title, string what, string from, string required) =>
            AP_Atlas.UI.Kit.InfoButton(Tr(title), Tr(what), Tr(from), Tr(required), () => OpenHelp("guide:Multiworlds"), text => Tr(text));
        HBoxContainer Labelled(string label, Button info)
        {
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            row.AddThemeConstantOverride("separation", 2);
            row.AddChild(new Label { Text = Tr(label) });
            row.AddChild(info);
            return row;
        }
        var grid = _editorGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 6);
        rightVbox.AddChild(grid);
        void Field(string label, Button info, Control box)
        {
            grid.AddChild(Labelled(label, info));
            box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            grid.AddChild(box);
        }
        _nameInput = new LineEdit { PlaceholderText = Tr("A name for this multiworld"), SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = Tr("Name") };
        _nameInput.TextChanged += text => EditSelected(profile => profile.Name = text);
        Field("Name:", Info("Name", "Any name that tells this multiworld apart from your others: the async's name, the host's, the date.", "Yours to choose. It shows on Home, in the slots panel and in Properties.", "Required."), _nameInput);
        _roomLinkInput = new LineEdit { PlaceholderText = Tr("https://archipelago.gg/room/… (optional)"), SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = Tr("Room link") };
        _roomLinkInput.TextChanged += text => EditSelected(profile => profile.RoomLink = text.Trim());
        var roomRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        roomRow.AddThemeConstantOverride("separation", 6);
        roomRow.AddChild(_roomLinkInput);
        _fillFromRoomButton = AP_Atlas.UI.Kit.Button(Tr("Fill from link"), Tr("Reads the room's status page once (the port it runs on and its players) and fills in the server address and the slots. Atlas asks first, and never requests the room's own page."), FillFromRoomLink);
        roomRow.AddChild(_fillFromRoomButton);
        Field("Room link:", Info("Room link", "The room's address on archipelago.gg, as the host shared it: https://archipelago.gg/room/…", "From the host, or the multiworld's Discord thread. Fill from link reads the room's status page once, with your permission, and fills in the server address and the slots.", "Optional. Without it, type the server address and the slots yourself."), roomRow);
        _serverInput = new LineEdit { PlaceholderText = Tr("archipelago.gg:12345"), SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = Tr("Server address") };
        _serverInput.TextChanged += text => EditSelected(profile => profile.ServerUrl = text.Trim());
        Field("Server address:", Info("Server address", "The server and the room's port, the way the room page shows them: archipelago.gg:12345. The port changes when a room is restarted.", "From the room page on archipelago.gg, or Fill from link. For a room hosted elsewhere, its host's address and port.", "Required to connect."), _serverInput);
        _passwordInput = new LineEdit { PlaceholderText = Tr("Password (optional)"), Secret = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = Tr("Password") };
        _passwordInput.TextChanged += text => EditSelected(profile => profile.Password = text);
        _passwordToggle = AP_Atlas.UI.Kit.Button("", Tr("Show the password"), () => SetPasswordShown(_passwordInput.Secret));
        _passwordToggle.Icon = AP_Atlas.UI.LucideTextures.Get("eye", AP_Atlas.Core.ThemeColors.TextMuted, 1.0f);
        var passwordRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        passwordRow.AddThemeConstantOverride("separation", 6);
        passwordRow.AddChild(_passwordInput);
        passwordRow.AddChild(_passwordToggle);
        Field("Password:", Info("Password", "The room's password, if the host set one.", "From the host. Atlas keeps it encrypted for your Windows account and sends it only to this room's server.", "Optional: most rooms have none."), passwordRow);
        _cheeseInput = new LineEdit { PlaceholderText = Tr("Cheese Tracker link, or the archipelago.gg room link (optional)"), SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = Tr("Cheese Tracker link") };
        _cheeseInput.TextChanged += (_) => MarkDirty();
        Field("Cheese Tracker:", Info("Cheese Tracker", "The multiworld's page on Cheese Tracker, the shared tracker many asyncs use, or the archipelago.gg room link, which Atlas looks up there.", "From the host or the Discord thread, if the async uses Cheese Tracker. Save checks it online and links it; the Cheese Tracker tab then shows it.", "Optional."), _cheeseInput);
        _sphereInput = new LineEdit { PlaceholderText = Tr("https://spheretracker.de/room/… (optional)"), SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = Tr("Sphere Tracker link") };
        _sphereInput.TextChanged += (_) => MarkDirty();
        Field("Sphere Tracker:", Info("Sphere Tracker", "The host's room on spheretracker.de (spheretracker.de/room/…), which shows the multiworld sphere by sphere.", "Only from the host: Atlas uses the host's room and no other (anything else would be cheating), and asks you to confirm when it can't tell the host made it. Save checks it online.", "Optional. Hidden in race mode."), _sphereInput);
        rightVbox.AddChild(new HSeparator());
        rightVbox.AddChild(Labelled("Slots:", Info("Slots", "The names of the slots you play in this multiworld, exactly as in your YAMLs (a slot is one player's game).", "From your YAMLs, or Fill from link (the room's players). Connect a slot from its row; its stats and links are kept under its name.", "At least one, to connect.")));
        // The slot rows take the spare height; the editor's scroll takes over when they need more.
        _slotsListVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        rightVbox.AddChild(_slotsListVBox);
        var buttonRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin };
        buttonRow.AddThemeConstantOverride("separation", 8);
        rightVbox.AddChild(buttonRow);
        _addSlotButton = AP_Atlas.UI.Kit.Button(Tr("+ Add Slot"), Tr("Adds a slot: its name, its game, its YAML, whether its logic is ready in the Atlas Engine, and Connect now."), OnAddSlotPressed);
        buttonRow.AddChild(_addSlotButton);
        _addYamlButton = AP_Atlas.UI.Kit.Button(Tr("Add YAML…"), Tr("Keeps a player YAML in Atlas's YAML folder and adds a slot for each player it names, with the YAML tied to that slot."), OnAddYamlPressed);
        buttonRow.AddChild(_addYamlButton);
        buttonRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill }); // Save and Delete stand apart from the adding
        _saveButton = new Button { Text = Tr("Save"), TooltipText = Tr("Writes this multiworld to disk and checks its Cheese Tracker and Sphere Tracker links.") };
        _saveButton.Pressed += OnSaveProfilePressed;
        buttonRow.AddChild(_saveButton);
        _deleteButton = new Button { Text = Tr("Delete Multiworld"), TooltipText = Tr("Deletes this multiworld and its slots from Atlas (it asks first).") };
        _deleteButton.Pressed += OnDeleteProfilePressed;
        buttonRow.AddChild(_deleteButton);
        rightVbox.AddChild(new HSeparator());
        _statusLabel = new Label { Text = Tr("Status: Disconnected"), HorizontalAlignment = HorizontalAlignment.Left };
        _statusLabel.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.TextMuted);
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
    /// <summary>The rich text variation for logs: the code font, so columns of output line up.</summary>
    internal const string LogTextVariation = "LogText";

    /// <summary>
    /// Check boxes, radio buttons, switches, and the check marks in menus and tables, drawn from the palette: an outline in
    /// the subtle text colour when off, the accent with a mark in the colour that reads on it when on. Godot's own are light
    /// grey, which all but vanish on the Light theme. SVG textures re-rasterise for the zoom, so they stay sharp.
    /// </summary>
    private static void ApplyCheckIcons(Theme theme)
    {
        string Hex(Godot.Color c) => "#" + c.ToHtml(false);
        string edge = Hex(AP_Atlas.Core.ThemeColors.TextSubtle), off = Hex(AP_Atlas.Core.ThemeColors.Disabled);
        string accent = Hex(AP_Atlas.Core.ThemeColors.Accent), mark = Hex(AP_Atlas.Core.ThemeColors.TextOnAccent);
        Texture2D Svg(int w, int h, string body) => DpiTexture.CreateFromString($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\">{body}</svg>");
        string Box(string stroke, string fill) => $"<rect x=\"1.5\" y=\"1.5\" width=\"15\" height=\"15\" rx=\"3.5\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.5\"/>";
        string Tick(string colour) => $"<path d=\"M5 9.2 7.8 12 13 6.2\" fill=\"none\" stroke=\"{colour}\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>";
        string Ring(string stroke) => $"<circle cx=\"9\" cy=\"9\" r=\"7.25\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"1.5\"/>";
        string Dot(string fill) => $"<circle cx=\"9\" cy=\"9\" r=\"3.75\" fill=\"{fill}\"/>";
        var uncheckedIcon = Svg(18, 18, Box(edge, "none"));
        var checkedIcon = Svg(18, 18, Box(accent, accent) + Tick(mark));
        var uncheckedDisabled = Svg(18, 18, Box(off, "none"));
        var checkedDisabled = Svg(18, 18, Box(off, off) + Tick(mark));
        var radio = Svg(18, 18, Ring(edge));
        var radioOn = Svg(18, 18, Ring(accent) + Dot(accent));
        var radioDisabled = Svg(18, 18, Ring(off));
        var radioOnDisabled = Svg(18, 18, Ring(off) + Dot(off));
        foreach (string type in new[] { "CheckBox", "PopupMenu", "Tree" })
        {
            theme.SetIcon("checked", type, checkedIcon);
            theme.SetIcon("unchecked", type, uncheckedIcon);
            theme.SetIcon("radio_checked", type, radioOn);
            theme.SetIcon("radio_unchecked", type, radio);
        }
        theme.SetIcon("checked_disabled", "CheckBox", checkedDisabled);
        theme.SetIcon("unchecked_disabled", "CheckBox", uncheckedDisabled);
        theme.SetIcon("radio_checked_disabled", "CheckBox", radioOnDisabled);
        theme.SetIcon("radio_unchecked_disabled", "CheckBox", radioDisabled);
        theme.SetIcon("checked_disabled", "Tree", checkedDisabled);
        theme.SetIcon("unchecked_disabled", "Tree", uncheckedDisabled);
        // Switches: a pill with its knob; the accent with a knob that reads on it when on.
        string Pill(string stroke, string fill) => $"<rect x=\"1\" y=\"1\" width=\"34\" height=\"18\" rx=\"9\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.5\"/>";
        string Knob(int x, string fill) => $"<circle cx=\"{x}\" cy=\"10\" r=\"5.5\" fill=\"{fill}\"/>";
        var switchOff = Svg(36, 20, Pill(edge, "none") + Knob(10, edge));
        var switchOn = Svg(36, 20, Pill(accent, accent) + Knob(26, mark));
        var switchOffDisabled = Svg(36, 20, Pill(off, "none") + Knob(10, off));
        var switchOnDisabled = Svg(36, 20, Pill(off, off) + Knob(26, mark));
        foreach (string suffix in new[] { "", "_mirrored" })
        {
            theme.SetIcon("unchecked" + suffix, "CheckButton", switchOff);
            theme.SetIcon("checked" + suffix, "CheckButton", switchOn);
            theme.SetIcon("unchecked_disabled" + suffix, "CheckButton", switchOffDisabled);
            theme.SetIcon("checked_disabled" + suffix, "CheckButton", switchOnDisabled);
        }
        theme.SetConstant("h_separation", "CheckBox", 8);
        theme.SetConstant("h_separation", "CheckButton", 8);
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
