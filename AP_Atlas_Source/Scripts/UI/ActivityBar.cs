using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The column of icons on the far left of the window: every tool, in three labelled groups (the slot tools, the
    /// multiworld tools, then Atlas's own pages at the bottom), and a button for the Atlas Engine window. One button is
    /// lit: the tool the window shows (<see cref="Select"/> follows the window, however the tool was switched). Each
    /// button's tooltip names the tool and its key.
    /// </summary>
    public partial class ActivityBar : PanelContainer
    {
        /// <summary>Wide enough for the group captions.</summary>
        public const int Width = 64;

        private readonly Func<string, string> _tr;
        private readonly Func<Tool, string> _keyOf;
        private readonly ButtonGroup _group = new();
        private readonly Dictionary<Tool, Button> _buttons = new();
        private readonly Dictionary<Tool, string> _captions = new();
        private readonly List<Tool> _order = new();
        private readonly List<Button> _styled = new();
        private Button _engine = null!;
        private Color _accent = AP_Atlas.Core.ThemeColors.Accent;

        /// <summary>The user pressed a tool's button.</summary>
        public event Action<Tool>? ToolPressed;

        /// <summary>The user pressed the Atlas Engine button.</summary>
        public event Action? EnginePressed;

        /// <param name="tr">Translates text as it's shown.</param>
        /// <param name="keyOf">The key that shows a tool ("Ctrl+3"), or "" for none: shown in its tooltip.</param>
        public ActivityBar(Func<string, string> tr, Func<Tool, string> keyOf)
        {
            _tr = tr;
            _keyOf = keyOf;
        }

        /// <summary>The tools in the bar's order, top to bottom.</summary>
        public IReadOnlyList<Tool> Order => _order;

        /// <summary>The lit tool, if any.</summary>
        public Tool? Selected { get; private set; }

        public Button ButtonOf(Tool tool) => _buttons[tool];

        public Button EngineButton => _engine;

        /// <summary>The caption of the group a tool sits in ("SLOT", "MULTIWORLD", "ATLAS"), as written in the code.</summary>
        public string CaptionOf(Tool tool) => _captions[tool];

        public override void _Ready()
        {
            CustomMinimumSize = new Vector2(Width, 0);
            SizeFlagsVertical = SizeFlags.ExpandFill;
            AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color("#181818"),
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6,
                ContentMarginTop = 6,
                ContentMarginBottom = 6,
                ContentMarginLeft = 4,
                ContentMarginRight = 4
            });
            var column = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            column.AddThemeConstantOverride("separation", 2);
            AddChild(column);
            AddGroup(column, "SLOT", ToolGroup.Slot);
            AddGroup(column, "MULTIWORLD", ToolGroup.Multiworld);
            column.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill }); // Atlas's pages sit at the bottom
            AddGroup(column, "ATLAS", ToolGroup.Atlas);
            _engine = MakeButton("cpu", _tr("Atlas Engine"));
            _engine.Pressed += () => EnginePressed?.Invoke();
            column.AddChild(_engine);
            ApplyAccent(_accent);
        }

        private void AddGroup(VBoxContainer column, string caption, ToolGroup group)
        {
            var label = new Label { Text = _tr(caption), HorizontalAlignment = HorizontalAlignment.Center };
            label.AddThemeFontSizeOverride("font_size", 9);
            label.AddThemeColorOverride("font_color", new Color("#8a8a8a"));
            column.AddChild(label);
            foreach (var tool in Tool.All.Where(t => t.Group == group))
            {
                string key = _keyOf(tool);
                var button = MakeButton(tool.Icon, key.Length > 0 ? $"{_tr(tool.Title)}  ({key})" : _tr(tool.Title));
                button.ToggleMode = true;
                button.ButtonGroup = _group;
                var shown = tool;
                button.Pressed += () => ToolPressed?.Invoke(shown);
                _buttons[tool] = button;
                _captions[tool] = caption;
                _order.Add(tool);
                column.AddChild(button);
            }
        }

        private Button MakeButton(string icon, string tooltip)
        {
            var button = new Button
            {
                TooltipText = tooltip,
                CustomMinimumSize = new Vector2(Width - 8, 44),
                FocusMode = FocusModeEnum.All,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                MouseDefaultCursorShape = CursorShape.PointingHand
            };
            // Lucide's icons are strokes in "currentColor": drawn white, then tinted by the button's state.
            string? svg = LucideIcons.Svg(icon, "#FFFFFF");
            if (svg != null)
            {
                var image = new Image();
                image.LoadSvgFromString(svg, 2f);
                button.Icon = ImageTexture.CreateFromImage(image);
            }
            button.AddThemeConstantOverride("icon_max_width", 22);
            button.AddThemeColorOverride("icon_normal_color", new Color("#9a9a9a"));
            button.AddThemeColorOverride("icon_hover_color", Colors.White);
            button.AddThemeColorOverride("icon_pressed_color", Colors.White);
            button.AddThemeColorOverride("icon_hover_pressed_color", Colors.White);
            button.AddThemeColorOverride("icon_focus_color", Colors.White);
            _styled.Add(button);
            return button;
        }

        /// <summary>Styles every button for the accent colour: a lit button has a bar of it on its left edge.</summary>
        public void ApplyAccent(Color accent)
        {
            _accent = accent;
            foreach (var button in _styled)
            {
                button.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
                button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
                button.AddThemeStyleboxOverride("hover", Box(new Color("#232323"), null));
                button.AddThemeStyleboxOverride("pressed", Box(new Color("#262626"), accent));
                button.AddThemeStyleboxOverride("hover_pressed", Box(new Color("#2a2a2a"), accent));
            }
        }

        private static StyleBoxFlat Box(Color background, Color? edge)
        {
            var box = new StyleBoxFlat { BgColor = background, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 };
            if (edge is { } color)
            {
                box.BorderWidthLeft = 3;
                box.BorderColor = color;
            }
            return box;
        }

        /// <summary>Lights a tool's button (the window shows that tool now), and no other.</summary>
        public void Select(Tool tool)
        {
            Selected = tool;
            foreach (var (other, button) in _buttons) button.SetPressedNoSignal(other == tool);
        }
    }
}
