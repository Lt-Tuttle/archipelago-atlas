using System.Collections.Generic;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The Lucide icons Atlas uses (https://lucide.dev, ISC licence, see THIRD_PARTY_NOTICES.md), as they ship in
    /// Lucide 1.52.0. Their stroke is "currentColor": <see cref="Svg"/> puts a colour in its place.
    /// </summary>
    public static class LucideIcons
    {
        private static readonly Dictionary<string, string> Icons = new()
        {
            ["map"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M14.106 5.553a2 2 0 0 0 1.788 0l3.659-1.83A1 1 0 0 1 21 4.619v12.764a1 1 0 0 1-.553.894l-4.553 2.277a2 2 0 0 1-1.788 0l-4.212-2.106a2 2 0 0 0-1.788 0l-3.659 1.83A1 1 0 0 1 3 19.381V6.618a1 1 0 0 1 .553-.894l4.553-2.277a2 2 0 0 1 1.788 0z"" /><path d=""M15 5.764v15"" /><path d=""M9 3.236v15"" /></svg>",
            ["key-round"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M2.586 17.414A2 2 0 0 0 2 18.828V21a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h.172a2 2 0 0 0 1.414-.586l.814-.814a6.5 6.5 0 1 0-4-4z"" /><circle cx=""16.5"" cy=""7.5"" r="".5"" fill=""currentColor"" /></svg>",
            ["route"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><circle cx=""6"" cy=""19"" r=""3"" /><path d=""M9 19h8.5a3.5 3.5 0 0 0 0-7h-11a3.5 3.5 0 0 1 0-7H15"" /><circle cx=""18"" cy=""5"" r=""3"" /></svg>",
            ["logs"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M3 5h1"" /><path d=""M3 12h1"" /><path d=""M3 19h1"" /><path d=""M8 5h1"" /><path d=""M8 12h1"" /><path d=""M8 19h1"" /><path d=""M13 5h8"" /><path d=""M13 12h8"" /><path d=""M13 19h8"" /></svg>",
            ["lightbulb"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M15 14c.2-1 .7-1.7 1.5-2.5 1-.9 1.5-2.2 1.5-3.5A6 6 0 0 0 6 8c0 1 .2 2.2 1.5 3.5.7.7 1.3 1.5 1.5 2.5"" /><path d=""M9 18h6"" /><path d=""M10 22h4"" /></svg>",
            ["users"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"" /><path d=""M16 3.128a4 4 0 0 1 0 7.744"" /><path d=""M22 21v-2a4 4 0 0 0-3-3.87"" /><circle cx=""9"" cy=""7"" r=""4"" /></svg>",
            ["orbit"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M20.341 6.484A10 10 0 0 1 10.266 21.85"" /><path d=""M3.659 17.516A10 10 0 0 1 13.74 2.152"" /><circle cx=""12"" cy=""12"" r=""3"" /><circle cx=""19"" cy=""5"" r=""2"" /><circle cx=""5"" cy=""19"" r=""2"" /></svg>",
            ["globe"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><circle cx=""12"" cy=""12"" r=""10"" /><path d=""M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20"" /><path d=""M2 12h20"" /></svg>",
            ["package"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M11 21.73a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73z"" /><path d=""M12 22V12"" /><polyline points=""3.29 7 12 12 20.71 7"" /><path d=""m7.5 4.27 9 5.15"" /></svg>",
            ["cpu"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M12 20v2"" /><path d=""M12 2v2"" /><path d=""M17 20v2"" /><path d=""M17 2v2"" /><path d=""M2 12h2"" /><path d=""M2 17h2"" /><path d=""M2 7h2"" /><path d=""M20 12h2"" /><path d=""M20 17h2"" /><path d=""M20 7h2"" /><path d=""M7 20v2"" /><path d=""M7 2v2"" /><rect x=""4"" y=""4"" width=""16"" height=""16"" rx=""2"" /><rect x=""8"" y=""8"" width=""8"" height=""8"" rx=""1"" /></svg>",
            ["settings"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915"" /><circle cx=""12"" cy=""12"" r=""3"" /></svg>",
            ["house"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8"" /><path d=""M3 10a2 2 0 0 1 .709-1.528l7-6a2 2 0 0 1 2.582 0l7 6A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"" /></svg>",
        };

        /// <summary>The icon's SVG in a colour ("#E0E0E0"), or null for a name Atlas doesn't ship.</summary>
        public static string? Svg(string name, string color) =>
            Icons.TryGetValue(name, out var svg) ? svg.Replace("currentColor", color) : null;

        /// <summary>Every icon Atlas ships, by its Lucide name.</summary>
        public static IEnumerable<string> Names => Icons.Keys;
    }
}
