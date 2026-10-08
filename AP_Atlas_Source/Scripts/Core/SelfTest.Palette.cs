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
        /// <summary>
        /// The Map Tracker's pin colours, in every palette: the six states differ from each other (by colour, not only by
        /// lightness: PopTracker's green and yellow are both light), and the five that mark something to do stand out from
        /// the pin's border at 3 to 1, as a mark must. PopTracker's dark grey for checked is quiet on purpose.
        /// </summary>
        private static void MapPinColoursAreDistinct()
        {
            var states = AP_Atlas.Core.Maps.MapPinLogic.All;
            foreach (var palette in Palette.All)
            {
                for (int i = 0; i < states.Length; i++)
                {
                    var a = ThemeColors.DefaultMapColour(states[i], palette);
                    for (int j = i + 1; j < states.Length; j++)
                    {
                        var b = ThemeColors.DefaultMapColour(states[j], palette);
                        double distance = Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));
                        Expect(distance >= 0.25, $"{palette.Name}: the map's {states[i]} and {states[j]} pins are too alike ({distance:0.00})");
                    }
                    if (states[i] == AP_Atlas.Core.Maps.MapPinState.Checked) continue;
                    double contrast = ThemeColors.Contrast(a, ThemeColors.MapPinBorder);
                    Expect(contrast >= 3.0, $"{palette.Name}: a {states[i]} pin reads at {contrast:0.0} to 1 against its border");
                }
            }
        }

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
            // Any accent the user picks: its headings and links read on every palette's surface.
            foreach (string hex in new[] { "#000000", "#FFFFFF", "#1E1E1E", "#808080", "#0000FF", "#FFFF00", ThemeColors.DefaultAccentHex })
            {
                foreach (var palette in Palette.All)
                {
                    double heading = ThemeColors.Contrast(ThemeColors.HeadingFor(new Color(hex), palette), palette.Surface);
                    double link = ThemeColors.Contrast(ThemeColors.LinkFor(new Color(hex), palette), palette.Surface);
                    Expect(heading >= 3.0 && link >= 4.5, $"{palette.Name}: with the accent {hex}, a heading reads at {heading:0.0} to 1 and a link at {link:0.0}");
                }
            }
        }
    }
}
