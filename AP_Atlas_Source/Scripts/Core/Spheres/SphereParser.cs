using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using AP_Atlas.Core.CheeseTracker;

namespace AP_Atlas.Core.Spheres
{
    /// <summary>
    /// Reads a host's spheretracker.de room page: its tables, kept as they are (titled by the heading above each, or its
    /// caption), the room's name, who created it and when it was last updated, and the Archipelago trackers it links to
    /// (which multiworld the room is for).
    /// </summary>
    public static class SphereParser
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(10);
        private static readonly Regex TableRx = new Regex(@"<table\b[^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex RowRx = new Regex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex HeaderCellRx = new Regex(@"<th\b[^>]*>(.*?)</th>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex CellRx = new Regex(@"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex HeadingRx = new Regex(@"<(h[1-4]|caption)\b[^>]*>(.*?)</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex TitleRx = new Regex(@"<title\b[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex ScriptRx = new Regex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex TagRx = new Regex(@"<[^>]+>", RegexOptions.Singleline, RegexTimeout);
        private static readonly Regex SpaceRx = new Regex(@"\s+", RegexOptions.None, RegexTimeout);
        // Archipelago tracker links only: a room's links to itself (spheretracker.de/room/…) say nothing about its multiworld.
        private static readonly Regex RoomNameRx = new Regex(@"<h2\b[^>]*>(.*?)</h2\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex CreatorRx = new Regex(@">\s*Created by\s+([^<]{1,100}?)\s*<", RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex UpdatedRx = new Regex(@"id=""last-room-update-label""[^>]*?\bdata-utc=""([^""]+)""", RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex TrackerLinkRx = new Regex(@"/(?:tracker|sphere_tracker|generic_tracker)/([A-Za-z0-9_-]{22})(?![A-Za-z0-9_-])", RegexOptions.None, RegexTimeout);

        /// <summary>A cell's text: tags removed, entities decoded, whitespace collapsed.</summary>
        public static string CellText(string html) =>
            SpaceRx.Replace(WebUtility.HtmlDecode(TagRx.Replace(html ?? "", " ")), " ").Trim();

        /// <summary>Every table on a page, with the heading just above it as its title.</summary>
        public static List<PageTable> ReadTables(string html)
        {
            html = ScriptRx.Replace(html ?? "", " ");
            var headings = HeadingRx.Matches(html).Select(m => (m.Index, Text: CellText(m.Groups[2].Value))).Where(h => h.Text.Length > 0).ToList();
            var tables = new List<PageTable>();
            foreach (Match table in TableRx.Matches(html))
            {
                var t = new PageTable();
                string inner = table.Groups[1].Value;
                var caption = HeadingRx.Match(inner);
                t.Title = caption.Success && caption.Groups[1].Value.Equals("caption", StringComparison.OrdinalIgnoreCase)
                    ? CellText(caption.Groups[2].Value)
                    : headings.LastOrDefault(h => h.Index < table.Index).Text ?? "";
                foreach (Match row in RowRx.Matches(inner))
                {
                    string rowHtml = row.Groups[1].Value;
                    var header = HeaderCellRx.Matches(rowHtml).Select(m => CellText(m.Groups[1].Value)).ToList();
                    var cells = CellRx.Matches(rowHtml).Select(m => CellText(m.Groups[1].Value)).ToList();
                    if (header.Count > 0 && cells.Count == 0)
                    {
                        if (t.Columns.Count == 0) t.Columns = header;
                        continue;
                    }
                    if (cells.Count == 0) continue;
                    // A row may start with a heading cell (a row label) followed by data cells.
                    if (header.Count > 0) cells.InsertRange(0, header);
                    t.Rows.Add(cells);
                }
                int width = Math.Max(t.Columns.Count, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Count));
                for (int i = t.Columns.Count; i < width; i++) t.Columns.Add($"Column {i + 1}");
                foreach (var r in t.Rows)
                    while (r.Count < width) r.Add("");
                if (width > 0) tables.Add(t);
            }
            return tables;
        }

        /// <summary>The Archipelago tracker ids a page links to (as Archipelago writes them: 22 characters).</summary>
        public static List<string> TrackerIdsIn(string html) =>
            TrackerLinkRx.Matches(html ?? "").Select(m => m.Groups[1].Value).Where(CtLink.IsId).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>Who created the room, from its "Created by …" line, or "".</summary>
        public static string CreatorIn(string html)
        {
            var m = CreatorRx.Match(ScriptRx.Replace(html ?? "", " "));
            return m.Success ? CellText(m.Groups[1].Value) : "";
        }

        /// <summary>A spheretracker.de room page: its tables as they are, or null and why there are none.</summary>
        public static SphereData ParseSphereSite(string html, out string error)
        {
            error = null;
            html ??= "";
            string text = CellText(ScriptRx.Replace(html, " "));
            if (text.IndexOf("Room not created", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                error = "spheretracker.de says this room hasn't been created (the host creates it).";
                return null;
            }
            var data = new SphereData { Title = TitleOf(html), Tables = ReadTables(html), TrackerIds = TrackerIdsIn(html), Creator = CreatorIn(html) };
            string body = ScriptRx.Replace(html, " ");
            var name = RoomNameRx.Match(body);
            if (name.Success) data.RoomName = CellText(name.Groups[1].Value);
            var updated = UpdatedRx.Match(html);
            if (updated.Success && DateTimeOffset.TryParse(updated.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var when))
                data.UpdatedUtc = when.UtcDateTime;
            if (data.Tables.Count == 0)
            {
                error = html.IndexOf("/auth/login", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("Login", StringComparison.OrdinalIgnoreCase) >= 0 && text.Length < 400
                    ? "spheretracker.de shows this room only after logging in. Open it in your browser."
                    : "Atlas couldn't find any tables on this spheretracker.de page. Open it in your browser.";
                return null;
            }
            return data;
        }

        private static string TitleOf(string html)
        {
            var m = TitleRx.Match(html);
            return m.Success ? CellText(m.Groups[1].Value) : "";
        }
    }
}
