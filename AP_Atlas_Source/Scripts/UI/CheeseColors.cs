using Godot;

namespace AP_Atlas.UI
{
    /// <summary>The colours Cheese Tracker statuses are shown in.</summary>
    public static class CheeseColors
    {
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
