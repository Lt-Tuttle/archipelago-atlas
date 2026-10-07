using System;
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The shared UI kit: the buttons, headings and lines of text every page and window builds from, so they look and
    /// behave the same everywhere and change in one place. Their colours come from <see cref="ThemeColors"/>; their size
    /// follows the pane they're in (a "font_size_ratio" scales it, as MainTrackerWindow.SetFontSizeRecursive applies).
    /// </summary>
    public static class Kit
    {
        /// <summary>
        /// A button that runs an action; disabled when it can't be used now (the tooltip still says what it would do).
        /// The focus policy, once for every button: it takes the keyboard focus (Tab reaches it, Enter or Space presses
        /// it, the theme's ring shows it), unless <paramref name="focusable"/> is false for a button beside a field the
        /// user keeps typing into. A button showing only a symbol ("◀", "+") is named for screen readers by its tooltip.
        /// </summary>
        /// <param name="flat">No background until hovered: a quiet button beside text.</param>
        /// <param name="small">Slightly smaller text: an action in a row of many.</param>
        public static Button Button(string text, string? tooltip, Action onPressed, bool enabled = true, bool flat = false, bool small = false, bool focusable = true)
        {
            var button = new Button { Text = text, TooltipText = tooltip ?? "", Disabled = !enabled, Flat = flat, FocusMode = focusable ? Control.FocusModeEnum.All : Control.FocusModeEnum.None };
            if (small) button.SetMeta("font_size_ratio", 0.9f);
            if (IsSymbolOnly(text) && !string.IsNullOrEmpty(tooltip)) button.AccessibilityName = tooltip;
            button.Pressed += () => onPressed();
            return button;
        }

        /// <summary>Text with no letter or digit (a glyph such as "◀" or "…"): no name for a screen reader to read.</summary>
        public static bool IsSymbolOnly(string? text) => string.IsNullOrEmpty(text) || !System.Linq.Enumerable.Any(text, char.IsLetterOrDigit);

        /// <summary>A heading in the accent's heading colour, <paramref name="ratio"/> times the pane's text size. It follows the accent when that changes.</summary>
        public static Label Heading(string text, float ratio = 1.15f)
        {
            var label = new Label { Text = text };
            label.SetMeta("font_size_ratio", ratio);
            label.SetMeta(HeadingMeta, true);
            label.AddThemeColorOverride("font_color", ThemeColors.Heading);
            return label;
        }

        /// <summary>A line of text that wraps to its pane's width, in <paramref name="color"/> (the ordinary text colour by default).</summary>
        public static Label Text(string text, Color? color = null)
        {
            var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            label.AddThemeColorOverride("font_color", color ?? ThemeColors.Text);
            return label;
        }

        /// <summary>Secondary text: what explains or qualifies.</summary>
        public static Label Muted(string text) => Text(text, ThemeColors.TextMuted);

        /// <summary>Quiet text: a caption, a note, what doesn't apply.</summary>
        public static Label Subtle(string text) => Text(text, ThemeColors.TextSubtle);

        /// <summary>Recolours every heading under <paramref name="root"/> for the accent in use (after it changes).</summary>
        public static void RecolourHeadings(Node root)
        {
            if (root is Label label && label.HasMeta(HeadingMeta)) label.AddThemeColorOverride("font_color", ThemeColors.Heading);
            foreach (var child in root.GetChildren()) RecolourHeadings(child);
        }

        private const string HeadingMeta = "kit_heading";
    }
}
