using System;
using Godot;

namespace AP_Atlas.Core
{
    /// <summary>
    /// The user's theme accent color, shared by every control that styles itself in code.
    /// MainTrackerWindow sets it from AppSettings; controls read Accent when building styles and
    /// subscribe to AccentChanged to restyle anything already on screen.
    /// Status colors that carry meaning (connected green, delete red, logic/item-class colors) stay fixed.
    /// </summary>
    public static class ThemeColors
    {
        public const string DefaultAccentHex = "#8A2BE2";

        public static Color Accent { get; private set; } = new Color(DefaultAccentHex);

        /// <summary>Raised on the main thread after the accent changes.</summary>
        public static event Action AccentChanged;

        public static void SetAccent(string hex)
        {
            var color = new Color(string.IsNullOrEmpty(hex) ? DefaultAccentHex : hex);
            if (color == Accent) return;
            Accent = color;
            AccentChanged?.Invoke();
        }

        /// <summary>Dark tint of the accent for selected-row backgrounds on the near-black panels.</summary>
        public static Color AccentTint => new Color("#1A1A1F").Lerp(Accent, 0.18f);

        /// <summary>Readable text color for content drawn on a solid accent background (e.g. black on yellow).</summary>
        public static Color TextOnAccent => Accent.Luminance > 0.55f ? Colors.Black : Colors.White;
    }
}
