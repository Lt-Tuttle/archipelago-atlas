using System;
using Godot;

namespace AP_Atlas.Core
{
    public static partial class SelfTest
    {
        /// <summary>
        /// Every text and state colour of the palette reads on every surface text is drawn on (the accessibility contrast
        /// rule: 4.5 to 1 for text, 3 to 1 for quieter text and marks), and text on the accent reads for every accent Atlas
        /// offers. A theme or palette added later is held to the same rule by the same check.
        /// </summary>
        private static void PaletteReadsOnEverySurface()
        {
            Expect(Math.Abs(ThemeColors.Contrast(Colors.White, Colors.Black) - 21) < 0.01 && Math.Abs(ThemeColors.Contrast(Colors.Gray, Colors.Gray) - 1) < 0.01,
                "the contrast measure is off (white on black is 21 to 1, a colour on itself 1 to 1)");
            foreach (var palette in Palette.All)
            {
                foreach (var (surfaceName, surface) in palette.Surfaces)
                {
                    foreach (var (name, color, min) in palette.TextColors)
                    {
                        double contrast = ThemeColors.Contrast(color, surface);
                        Expect(contrast >= min, $"{palette.Name}: {name} on {surfaceName} reads at {contrast:0.0} to 1; it needs {min:0.0}");
                    }
                }
                // Headings and links take the accent, lightened or darkened for the palette's surfaces.
                var heading = palette.IsDark ? new Color(ThemeColors.DefaultAccentHex).Lightened(0.2f) : new Color(ThemeColors.DefaultAccentHex).Darkened(0.15f);
                Expect(ThemeColors.Contrast(heading, palette.Surface) >= 3.0, $"{palette.Name}: a heading in the default accent reads at {ThemeColors.Contrast(heading, palette.Surface):0.0} to 1");
            }
            foreach (var (name, hex) in global::MainTrackerWindow.AccentPresets)
            {
                var accent = new Color(hex);
                double contrast = ThemeColors.Contrast(ThemeColors.TextOn(accent), accent);
                Expect(contrast >= 4.5, $"text on the {name} accent reads at {contrast:0.0} to 1");
            }
        }
    }
}
