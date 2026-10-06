using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The colours Cheese Tracker's own ideas are shown in, matching the site: its statuses (<see cref="Of"/>), its hint
    /// priorities and how long a slot has been quiet. Everything else on the Cheese pages takes Atlas's ThemeColors.
    /// </summary>
    public static class CheeseColors
    {
        /// <summary>A hint priority: critical, then progression (<see cref="AP_Atlas.Core.ThemeColors.Progression"/>), then quality of life.</summary>
        public static readonly Color Critical = Colors.Tomato;
        public static readonly Color Qol = Colors.CornflowerBlue;

        /// <summary>Between fine and overdue: a slot quiet for a while, a ping preference of "sparingly".</summary>
        public static readonly Color Caution = Colors.Gold;

        /// <summary>A slot that isn't finished, where the site's statuses are summed up.</summary>
        public static readonly Color Incomplete = Colors.LightSlateGray;

        public static Color Of(string status) => status switch
        {
            "unblocked" => Colors.WhiteSmoke,
            "bk" => Colors.Tomato,
            "soft_bk" => Colors.Gold,
            "go" => Colors.LimeGreen,
            "all_checks" or "goal" => Colors.SkyBlue,
            "done" => Colors.LimeGreen,
            "released" => Colors.Gray,
            "incomplete" => Colors.LightGray,
            _ => Colors.Gray
        };
    }
}
