using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AP_Atlas.Core
{
    /// <summary>
    /// The colours Atlas draws with, by what they mean: text, headings, states, Archipelago's entities and the surfaces.
    /// Code names the meaning (<see cref="TextMuted"/>, <see cref="Error"/>, <see cref="SurfaceSunken"/>), never a colour
    /// of its own, so every colour can change in one place: the accent the user picks today, the Dark, Light and
    /// High-contrast themes and the colour-blind palettes of Phase 2.8 later. The guard rails count the colour literals
    /// left in the code and keep the count from growing.
    /// MainTrackerWindow sets the accent from AppSettings; controls read <see cref="Accent"/> when building styles and
    /// subscribe to <see cref="AccentChanged"/> to restyle what's already on screen (headings made by Kit are recoloured
    /// for them). Status colours that carry meaning are never the only signal: a state is also said in words or a mark.
    /// </summary>
    public static class ThemeColors
    {
        public const string DefaultAccentHex = "#8A2BE2";

        public static Color Accent { get; private set; } = new Color(DefaultAccentHex);

        /// <summary>Raised on the main thread after the accent changes.</summary>
        public static event Action? AccentChanged;

        public static void SetAccent(string? hex)
        {
            var color = new Color(string.IsNullOrEmpty(hex) ? DefaultAccentHex : hex);
            if (color == Accent) return;
            Accent = color;
            AccentChanged?.Invoke();
        }

        /// <summary>The palette in use (Dark until <see cref="SetPalette"/> is called at startup from the theme setting).</summary>
        public static Palette Current { get; private set; } = Palette.Dark;

        /// <summary>Raised on the main thread after the palette changes (the window rebuilds its theme).</summary>
        public static event Action? PaletteChanged;

        /// <summary>The setting's names, as the Settings page offers them, in order.</summary>
        public static readonly (string Key, string Title)[] ThemeChoices =
        {
            ("follow", "Follow Windows"), ("dark", "Dark"), ("light", "Light"), ("high-contrast", "High contrast")
        };

        /// <summary>The palette a theme setting means: "follow" takes Windows's light or dark mode (dark where Godot can't tell).</summary>
        public static Palette PaletteForSetting(string? theme) => theme switch
        {
            "dark" => Palette.Dark,
            "light" => Palette.Light,
            "high-contrast" => Palette.HighContrast,
            _ => DisplayServer.IsDarkModeSupported() && !DisplayServer.IsDarkMode() ? Palette.Light : Palette.Dark
        };

        public static void SetPalette(Palette palette)
        {
            if (ReferenceEquals(palette, Current)) return;
            Current = palette;
            PaletteChanged?.Invoke();
        }

        // ---- The accent's derived colours ----

        /// <summary>Dark tint of the accent for selected rows on the sunken surfaces.</summary>
        public static Color AccentTint => SurfaceSunken.Lerp(Accent, 0.18f);

        /// <summary>Readable text for content drawn on a solid accent background (black on a yellow accent).</summary>
        public static Color TextOnAccent => TextOn(Accent);

        /// <summary>Black or white, whichever reads better on <paramref name="background"/>.</summary>
        public static Color TextOn(Color background) => Contrast(Colors.White, background) >= Contrast(Colors.Black, background) ? Colors.White : Colors.Black;

        /// <summary>Headings and section titles: the accent, lightened to read on dark surfaces, darkened on light ones.</summary>
        public static Color Heading => Current.IsDark ? Accent.Lightened(0.2f) : Accent.Darkened(0.15f);

        /// <summary>Links in rich text (Properties' jumps).</summary>
        public static Color Link => Current.IsDark ? Accent.Lightened(0.35f) : Accent.Darkened(0.05f);

        // ---- Text ----

        /// <summary>Ordinary text, and a value that stands out from its label.</summary>
        public static Color Text => Current.Text;

        /// <summary>Secondary text: a label beside a value, a game's name under a slot's, a line that explains.</summary>
        public static Color TextMuted => Current.TextMuted;

        /// <summary>Quiet text: captions, placeholders, what's done or doesn't apply.</summary>
        public static Color TextSubtle => Current.TextSubtle;

        /// <summary>A control that can't be used now (its icon or text).</summary>
        public static Color Disabled => Current.Disabled;

        // ---- States ----

        public static Color Success => Current.Success;
        public static Color Warning => Current.Warning;
        public static Color Error => Current.Error;
        public static Color Info => Current.Info;

        /// <summary>Something under way or waiting on the user: connecting, unsaved, a suggestion.</summary>
        public static Color Pending => Current.Pending;

        /// <summary>An action that takes something away: delete, disconnect.</summary>
        public static Color Danger => Current.Danger;

        /// <summary>Partly done (a count between none and all).</summary>
        public static Color Progress => Current.Progress;

        /// <summary>What logic would say, when race mode hides it: shown as "open", neither in nor out of logic.</summary>
        public static Color LogicHidden => Current.LogicHidden;

        /// <summary>Dark tint of the warning colour for a banner's background.</summary>
        public static Color WarningTint => SurfaceSunken.Lerp(Warning, 0.22f);

        // ---- Archipelago's entities, as its own text client colours them ----

        /// <summary>Another player's name.</summary>
        public static Color Player => Current.Player;

        /// <summary>The user's own slot, wherever it's named.</summary>
        public static Color You => Current.You;

        public static Color Location => Current.Location;
        public static Color Progression => Current.Progression;
        public static Color Useful => Current.Useful;
        public static Color Trap => Current.Trap;
        public static Color Filler => Current.Filler;

        /// <summary>A hinted location.</summary>
        public static Color Hinted => Current.Hinted;

        public static Color HintPriority => Current.HintPriority;
        public static Color HintNoPriority => Current.HintNoPriority;

        // ---- Map markers (fills with their own contrast; never text) ----

        /// <summary>A hinted location that logic can't reach yet.</summary>
        public static Color HintedOutOfLogic => Current.HintedOutOfLogic;

        /// <summary>A hinted location while race mode hides logic.</summary>
        public static Color HintedNeutral => Current.HintedNeutral;

        // ---- Surfaces ----

        /// <summary>The window's background.</summary>
        public static Color Surface => Current.Surface;

        /// <summary>Panels and pop-ups on the window.</summary>
        public static Color SurfacePanel => Current.SurfacePanel;

        /// <summary>A raised or selected strip: an unselected tab, the selected row of a list.</summary>
        public static Color SurfaceRaised => Current.SurfaceRaised;

        /// <summary>A sunken area: lists, cards, the inside of a tool.</summary>
        public static Color SurfaceSunken => Current.SurfaceSunken;

        /// <summary>The deepest area: a log, Properties' content, an overlay.</summary>
        public static Color SurfaceDeep => Current.SurfaceDeep;

        /// <summary>A button at rest, and hovered.</summary>
        public static Color Control => Current.Control;
        public static Color ControlHover => Current.ControlHover;

        /// <summary>A text field.</summary>
        public static Color Input => Current.Input;

        /// <summary>The line between panels, and the lighter one around a pop-up or a card.</summary>
        public static Color Border => Current.Border;
        public static Color BorderSoft => Current.BorderSoft;

        /// <summary>Alternating rows of a table.</summary>
        public static Color RowEven => Current.RowEven;
        public static Color RowOdd => Current.RowOdd;

        /// <summary>
        /// How much a colour stands out from a background, as accessibility measures it (WCAG's contrast ratio, 1 to 21):
        /// text wants 4.5, large text and marks 3. The self-test holds every palette to it.
        /// </summary>
        public static double Contrast(Color a, Color b)
        {
            double la = RelativeLuminance(a), lb = RelativeLuminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static double RelativeLuminance(Color c)
        {
            static double Lin(float v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }
    }

    /// <summary>One set of the palette's colours. <see cref="Dark"/> is Atlas's; Phase 2.8 adds the others.</summary>
    public sealed class Palette
    {
        public string Name { get; init; } = "";

        /// <summary>Dark surfaces with light text (the accent is lightened for headings), or the other way round.</summary>
        public bool IsDark { get; init; } = true;
        public Color Text { get; init; }
        public Color TextMuted { get; init; }
        public Color TextSubtle { get; init; }
        public Color Disabled { get; init; }
        public Color Success { get; init; }
        public Color Warning { get; init; }
        public Color Error { get; init; }
        public Color Info { get; init; }
        public Color Pending { get; init; }
        public Color Danger { get; init; }
        public Color Progress { get; init; }
        public Color LogicHidden { get; init; }
        public Color Player { get; init; }
        public Color You { get; init; }
        public Color Location { get; init; }
        public Color Progression { get; init; }
        public Color Useful { get; init; }
        public Color Trap { get; init; }
        public Color Filler { get; init; }
        public Color Hinted { get; init; }
        public Color HintPriority { get; init; }
        public Color HintNoPriority { get; init; }
        public Color HintedOutOfLogic { get; init; }
        public Color HintedNeutral { get; init; }
        public Color Surface { get; init; }
        public Color SurfacePanel { get; init; }
        public Color SurfaceRaised { get; init; }
        public Color SurfaceSunken { get; init; }
        public Color SurfaceDeep { get; init; }
        public Color Control { get; init; }
        public Color ControlHover { get; init; }
        public Color Input { get; init; }
        public Color Border { get; init; }
        public Color BorderSoft { get; init; }
        public Color RowEven { get; init; }
        public Color RowOdd { get; init; }

        /// <summary>The text and state colours, for checks that hold every one of them to the same rule.</summary>
        public (string Name, Color Color, double MinContrast)[] TextColors => new[]
        {
            ("Text", Text, 4.5), ("TextMuted", TextMuted, 4.5), ("TextSubtle", TextSubtle, 3.0), ("Disabled", Disabled, 3.0),
            ("Success", Success, 3.0), ("Warning", Warning, 3.0), ("Error", Error, 3.0), ("Info", Info, 3.0), ("Pending", Pending, 3.0),
            ("Danger", Danger, 3.0), ("Progress", Progress, 3.0), ("LogicHidden", LogicHidden, 3.0),
            ("Player", Player, 3.0), ("You", You, 3.0), ("Location", Location, 3.0), ("Progression", Progression, 3.0),
            ("Useful", Useful, 3.0), ("Trap", Trap, 3.0), ("Filler", Filler, 3.0), ("Hinted", Hinted, 3.0),
            ("HintPriority", HintPriority, 3.0), ("HintNoPriority", HintNoPriority, 3.0)
        };

        /// <summary>The surfaces text is drawn on.</summary>
        public (string Name, Color Color)[] Surfaces => new[]
        {
            ("Surface", Surface), ("SurfacePanel", SurfacePanel), ("SurfaceSunken", SurfaceSunken), ("SurfaceDeep", SurfaceDeep)
        };

        /// <summary>Every palette Atlas ships, as the Settings page offers them.</summary>
        public static IReadOnlyList<Palette> All => new[] { Dark, Light, HighContrast };

        /// <summary>A palette by its name, or Dark.</summary>
        public static Palette Named(string? name) => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Dark;

        public static readonly Palette Dark = new()
        {
            Name = "Dark",
            IsDark = true,
            Text = Colors.White,
            TextMuted = Colors.LightGray,
            TextSubtle = Colors.Gray,
            Disabled = Colors.DarkGray,
            Success = Colors.LimeGreen,
            Warning = Colors.Orange,
            Error = Colors.Salmon,
            Info = Colors.SkyBlue,
            Pending = Colors.Yellow,
            Danger = Colors.Crimson,
            Progress = Colors.Cyan,
            LogicHidden = Colors.SteelBlue,
            Player = Colors.Yellow,
            You = Colors.Magenta,
            Location = Colors.LightGreen,
            Progression = Colors.Plum,
            Useful = Colors.MediumSlateBlue, // SlateBlue, Archipelago's own, reads at 2.9 to 1 on a panel
            Trap = Colors.Salmon,
            Filler = Colors.Cyan,
            Hinted = Colors.DeepSkyBlue,
            HintPriority = Colors.Gold,
            HintNoPriority = Colors.MediumSlateBlue,
            HintedOutOfLogic = Colors.Purple,
            HintedNeutral = Colors.MediumPurple,
            Surface = new Color("#1E1E1E"),
            SurfacePanel = new Color("#252526"),
            SurfaceRaised = new Color("#2D2D30"),
            SurfaceSunken = new Color("#1A1A1F"),
            SurfaceDeep = new Color("#121216"),
            Control = new Color("#3E3E42"),
            ControlHover = new Color("#4F4F53"),
            Input = new Color("#3C3C3C"),
            Border = new Color("#181818"),
            BorderSoft = new Color("#444444"),
            RowEven = new Color("#16161C"),
            RowOdd = new Color("#1F1F27")
        };

        /// <summary>Light surfaces with dark text; every colour darkened until it reads on white.</summary>
        public static readonly Palette Light = new()
        {
            Name = "Light",
            IsDark = false,
            Text = new Color("#1E1E24"),
            TextMuted = new Color("#45454E"),
            TextSubtle = new Color("#66666F"),
            Disabled = new Color("#7E7E88"),
            Success = new Color("#1B7A3A"),
            Warning = new Color("#9A5A00"),
            Error = new Color("#B4261E"),
            Info = new Color("#1A63C2"),
            Pending = new Color("#7A6200"),
            Danger = new Color("#B4261E"),
            Progress = new Color("#0B6F82"),
            LogicHidden = new Color("#3A69A8"),
            Player = new Color("#7A5500"),
            You = new Color("#B0178E"),
            Location = new Color("#1B7A3A"),
            Progression = new Color("#7B3E9E"),
            Useful = new Color("#4A50B5"),
            Trap = new Color("#B4261E"),
            Filler = new Color("#0B6F82"),
            Hinted = new Color("#0A68B4"),
            HintPriority = new Color("#7A6200"),
            HintNoPriority = new Color("#4A50B5"),
            HintedOutOfLogic = new Color("#6A1B9A"),
            HintedNeutral = new Color("#7A5BB8"),
            Surface = new Color("#FAFAFB"),
            SurfacePanel = new Color("#F0F0F3"),
            SurfaceRaised = new Color("#E4E4E9"),
            SurfaceSunken = new Color("#FFFFFF"),
            SurfaceDeep = new Color("#F4F4F7"),
            Control = new Color("#E2E2E7"),
            ControlHover = new Color("#D3D3DA"),
            Input = new Color("#FFFFFF"),
            Border = new Color("#CBCBD3"),
            BorderSoft = new Color("#B9B9C3"),
            RowEven = new Color("#FFFFFF"),
            RowOdd = new Color("#F2F2F6")
        };

        /// <summary>Black surfaces, white text, pure colours and white borders, for low vision and bright rooms.</summary>
        public static readonly Palette HighContrast = new()
        {
            Name = "High contrast",
            IsDark = true,
            Text = Colors.White,
            TextMuted = Colors.White,
            TextSubtle = new Color("#E0E0E0"),
            Disabled = new Color("#A8A8A8"),
            Success = new Color("#4DFF88"),
            Warning = new Color("#FFD400"),
            Error = new Color("#FF6B6B"),
            Info = new Color("#5CD6FF"),
            Pending = new Color("#FFFF33"),
            Danger = new Color("#FF6B6B"),
            Progress = new Color("#33E8FF"),
            LogicHidden = new Color("#9DBFFF"),
            Player = new Color("#FFFF33"),
            You = new Color("#FF7DFF"),
            Location = new Color("#8CFF8C"),
            Progression = new Color("#EBB3FF"),
            Useful = new Color("#B8C4FF"),
            Trap = new Color("#FF6B6B"),
            Filler = new Color("#8CFFFF"),
            Hinted = new Color("#5CD6FF"),
            HintPriority = new Color("#FFD400"),
            HintNoPriority = new Color("#B8C4FF"),
            HintedOutOfLogic = new Color("#D98CFF"),
            HintedNeutral = new Color("#C0A8FF"),
            Surface = Colors.Black,
            SurfacePanel = new Color("#0A0A0A"),
            SurfaceRaised = new Color("#1C1C1C"),
            SurfaceSunken = Colors.Black,
            SurfaceDeep = Colors.Black,
            Control = new Color("#222222"),
            ControlHover = new Color("#3A3A3A"),
            Input = Colors.Black,
            Border = Colors.White,
            BorderSoft = Colors.White,
            RowEven = Colors.Black,
            RowOdd = new Color("#161616")
        };
    }
}
