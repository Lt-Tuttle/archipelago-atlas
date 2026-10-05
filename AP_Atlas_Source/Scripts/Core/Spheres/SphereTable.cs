#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AP_Atlas.Core.CheeseTracker;

namespace AP_Atlas.Core.Spheres
{
    /// <summary>The Sphere Tracker tab's table rules (kept free of UI so the self-test checks them).</summary>
    public static class SphereTable
    {
        /// <summary>Cells of a table filtered by text and sorted by a column (numbers as numbers, text in natural order).</summary>
        public static List<List<string>> FilterAndSort(PageTable table, string text, int column, bool descending)
        {
            text = (text ?? "").Trim();
            var rows = table.Rows.Where(r => text.Length == 0 || r.Any(c => c != null && c.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (column < 0 || column >= table.Columns.Count) return rows;
            rows.Sort((a, b) =>
            {
                string x = a[column], y = b[column];
                int c = double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double dx) &&
                        double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out double dy)
                    ? dx.CompareTo(dy) : CheeseTable.NaturalCompare(x, y);
                return descending ? -c : c;
            });
            return rows;
        }

        /// <summary>Matches a slot's whole name in a cell, ignoring case ("DarkTuttle1", not "DarkTuttle10").</summary>
        public static Regex SlotPattern(string slot) =>
            new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(slot ?? "") + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>Whether a row names the slot in any of its cells.</summary>
        public static bool Names(IEnumerable<string> row, Regex slot) => row.Any(cell => slot.IsMatch(cell ?? ""));

        public static int ColumnOf(PageTable table, string name) =>
            table?.Columns.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) ?? -1;

        /// <summary>The room's open locations (Sphere, Finder, Location, Game): every slot's unfound locations, or null.</summary>
        public static PageTable OpenLocations(SphereData data) =>
            data?.Tables.FirstOrDefault(t => ColumnOf(t, "Sphere") >= 0 && ColumnOf(t, "Finder") >= 0 && ColumnOf(t, "Location") >= 0);

        /// <summary>The room's summary (Sphere, Unfound, Found, Total per sphere or range of spheres), or null.</summary>
        public static PageTable Summary(SphereData data) =>
            data?.Tables.FirstOrDefault(t => ColumnOf(t, "Sphere") >= 0 && ColumnOf(t, "Unfound") >= 0);

        /// <summary>
        /// The rows of a slot: by its Finder (or Slot) column when the table has one, so a location that merely mentions the
        /// name doesn't count; otherwise rows naming it in any cell.
        /// </summary>
        public static bool IsSlotRow(PageTable table, List<string> row, string slot, Regex pattern)
        {
            int column = ColumnOf(table, "Finder");
            if (column < 0) column = ColumnOf(table, "Slot");
            return column >= 0 ? column < row.Count && string.Equals(row[column], slot, StringComparison.OrdinalIgnoreCase) : Names(row, pattern);
        }

        /// <summary>The (first) sphere number in a cell: "91", "Sphere 91", "Sphere 86 - Sphere 90" (86); null if none.</summary>
        public static int? SphereNumber(string cell)
        {
            var m = Regex.Match(cell ?? "", @"\d+", RegexOptions.None, TimeSpan.FromSeconds(1));
            return m.Success && int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
        }

        /// <summary>The earliest sphere that still has unfound locations, and how many, from the room's summary; null if none.</summary>
        public static (int Sphere, int Unfound)? EarliestOpen(PageTable summary)
        {
            int sphere = ColumnOf(summary, "Sphere"), unfound = ColumnOf(summary, "Unfound");
            if (sphere < 0 || unfound < 0) return null;
            foreach (var row in summary.Rows)
            {
                if (unfound >= row.Count || !int.TryParse(row[unfound], NumberStyles.Integer, CultureInfo.InvariantCulture, out int open) || open <= 0) continue;
                var n = SphereNumber(row[sphere]);
                if (n != null) return (n.Value, open);
            }
            return null;
        }

        /// <summary>A slot's open locations in a room, sorted by sphere: (rows, earliest sphere).</summary>
        public static (List<List<string>> Rows, int? Earliest) OpenFor(PageTable open, string slot)
        {
            if (open == null) return (new List<List<string>>(), null);
            int sphere = ColumnOf(open, "Sphere");
            var pattern = SlotPattern(slot);
            var rows = open.Rows.Where(r => IsSlotRow(open, r, slot, pattern)).ToList();
            var spheres = rows.Select(r => sphere >= 0 && sphere < r.Count ? SphereNumber(r[sphere]) : null).Where(n => n != null).Select(n => n.Value).ToList();
            return (rows, spheres.Count > 0 ? spheres.Min() : null);
        }
    }
}
