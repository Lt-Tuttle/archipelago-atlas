using System;
using System.Collections.Generic;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// A tab strip that never scrolls. When space runs short it first narrows the tabs' side padding, and only if
    /// that isn't enough does it wrap onto another row. Styled like the VS Code tab bar (accent line on the
    /// selected tab). Drop-in for the subset of TabBar the main window uses.
    /// </summary>
    public partial class WrappingTabStrip : HFlowContainer
    {
        /// <summary>Raised when the user picks a tab (not when CurrentTab is set from code).</summary>
        public event Action<int> TabSelected;

        private static readonly int[] PaddingSteps = { 24, 18, 14, 10, 6 };
        private const int TabGap = 2;
        private const int WrappedPadding = 14;

        private readonly List<Button> _tabs = new List<Button>();
        private readonly ButtonGroup _group = new ButtonGroup();
        private int _cornerRadius = 12;
        private Color _accent = Colors.White;
        private int _padding = PaddingSteps[0];
        private int _current = -1;

        public WrappingTabStrip()
        {
            AddThemeConstantOverride("h_separation", TabGap);
            AddThemeConstantOverride("v_separation", 2);
            Resized += FitPadding;
        }

        public int TabCount => _tabs.Count;

        public int CurrentTab
        {
            get => _current;
            set
            {
                if (value < 0 || value >= _tabs.Count) return;
                _current = value;
                _tabs[value].SetPressedNoSignal(true);
            }
        }

        public string GetTabTitle(int index) => index >= 0 && index < _tabs.Count ? _tabs[index].Text : "";

        public void AddTab(string title)
        {
            int index = _tabs.Count;
            var b = new Button
            {
                Text = title,
                ToggleMode = true,
                ButtonGroup = _group,
                FocusMode = FocusModeEnum.None,
                MouseDefaultCursorShape = CursorShape.PointingHand
            };
            b.AddThemeColorOverride("font_color", new Color("#9A9A9A"));
            b.AddThemeColorOverride("font_hover_color", new Color("#E0E0E0"));
            b.AddThemeColorOverride("font_pressed_color", Colors.White);
            b.AddThemeColorOverride("font_hover_pressed_color", Colors.White);
            b.Pressed += () =>
            {
                _current = index;
                TabSelected?.Invoke(index);
            };
            _tabs.Add(b);
            AddChild(b);
            ApplyStyles();
            if (_current < 0) CurrentTab = 0;
            FitPadding();
        }

        /// <summary>Restyles the tabs (call again when the accent color changes).</summary>
        public void ApplyStyle(int cornerRadius, Color accent)
        {
            _cornerRadius = cornerRadius;
            _accent = accent;
            ApplyStyles();
        }

        /// <summary>Sets the tab font size and refits.</summary>
        public void SetFontSize(int size)
        {
            foreach (var b in _tabs)
            {
                if (b.HasThemeFontSizeOverride("font_size") && b.GetThemeFontSize("font_size") == size) continue;
                b.AddThemeFontSizeOverride("font_size", size);
            }
            FitPadding();
        }

        private StyleBoxFlat TabStyle(Color bg, Color border) => new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = _cornerRadius,
            CornerRadiusTopRight = _cornerRadius,
            BorderWidthTop = 2,
            BorderColor = border,
            ContentMarginLeft = _padding,
            ContentMarginRight = _padding,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };

        private void ApplyStyles()
        {
            var clear = new Color(0, 0, 0, 0);
            var normal = TabStyle(clear, clear);
            var hover = TabStyle(new Color("#2a2d2e"), clear);
            var selected = TabStyle(new Color("#1e1e1e"), _accent);
            foreach (var b in _tabs)
            {
                b.AddThemeStyleboxOverride("normal", normal);
                b.AddThemeStyleboxOverride("disabled", normal);
                b.AddThemeStyleboxOverride("hover", hover);
                b.AddThemeStyleboxOverride("pressed", selected);
                b.AddThemeStyleboxOverride("hover_pressed", selected);
                b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            }
        }

        /// <summary>Uses the widest side padding at which every tab fits on one row; wraps only below the smallest.</summary>
        private void FitPadding()
        {
            if (_tabs.Count == 0 || Size.X <= 0) return;
            float textWidth = 0;
            foreach (var b in _tabs)
            {
                var font = b.GetThemeFont("font");
                int fontSize = b.GetThemeFontSize("font_size");
                textWidth += font?.GetStringSize(b.Text, HorizontalAlignment.Left, -1, fontSize).X ?? 0;
            }
            float gaps = TabGap * (_tabs.Count - 1);

            // If even the tightest padding needs two rows, wrap with comfortable padding rather than cramped tabs.
            int padding = WrappedPadding;
            foreach (int p in PaddingSteps)
            {
                if (textWidth + gaps + p * 2 * _tabs.Count + 2 <= Size.X) { padding = p; break; }
            }
            if (padding == _padding) return;
            _padding = padding;
            ApplyStyles();
        }
    }
}
