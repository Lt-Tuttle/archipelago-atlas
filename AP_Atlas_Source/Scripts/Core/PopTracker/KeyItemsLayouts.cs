#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>A block of a built Key Items layout: a group's header and its rows of tile codes.</summary>
    public sealed class LayoutBlock
    {
        public string Header { get; init; } = "";
        public List<List<string>> Rows { get; init; } = new();
        public int ItemSize { get; init; } = 32;
    }

    /// <summary>
    /// The layouts Key Items can show: a pack's own root layouts (PopTracker's tracker_default, tracker_horizontal,
    /// tracker_vertical, tracker_broadcast), and two Atlas builds from the pack's item groups, one stacking the groups
    /// and one setting them side by side.
    /// </summary>
    public static class KeyItemsLayouts
    {
        public const string Vertical = "atlas:vertical";
        public const string Horizontal = "atlas:horizontal";

        /// <summary>The picker's first entry: Atlas picks the pack layout that fits the view until the user chooses one.</summary>
        public const string Auto = "atlas:auto";

        /// <summary>
        /// The layout to use when the user hasn't chosen: the first (in the pack's order) whose content fits the view without
        /// scrolling; else the one that overflows the least. A layout's size is its content's minimum once drawn.
        /// </summary>
        public static string? PickFitting(IReadOnlyList<(string Id, float Width, float Height)> measured, float viewWidth, float viewHeight)
        {
            if (measured.Count == 0) return null;
            foreach (var (id, w, h) in measured)
                if (w <= viewWidth + 0.5f && h <= viewHeight + 0.5f) return id;
            string best = measured[0].Id;
            float bestOverflow = float.MaxValue;
            foreach (var (id, w, h) in measured)
            {
                float overflow = Math.Max(viewWidth <= 0 ? 0 : w / viewWidth, viewHeight <= 0 ? 0 : h / viewHeight);
                if (overflow < bestOverflow) { bestOverflow = overflow; best = id; }
            }
            return best;
        }

        /// <summary>Whether a layout id is one Atlas builds (not a pack root).</summary>
        public static bool IsBuilt(string? id) => id == Vertical || id == Horizontal;

        /// <summary>A layout's name as the picker shows it.</summary>
        public static string NameOf(string id) => id switch
        {
            "tracker_default" => "Default (pack)",
            "tracker_horizontal" => "Horizontal (pack)",
            "tracker_vertical" => "Vertical (pack)",
            "tracker_broadcast" => "Broadcast (pack)",
            Vertical => "Vertical (by group)",
            Horizontal => "Horizontal (by group)",
            _ => id + " (pack)"
        };

        /// <summary>
        /// The groups of a pack's visible grids: consecutive grids under the same header (else the same layout key) make
        /// one block, with their rows in order; a grid with neither goes under "Items".
        /// </summary>
        public static List<LayoutBlock> Blocks(IEnumerable<PackItemGrid> grids)
        {
            var blocks = new List<LayoutBlock>();
            foreach (var grid in grids)
            {
                string header = !string.IsNullOrWhiteSpace(grid.Header) ? grid.Header.Trim() : !string.IsNullOrWhiteSpace(grid.LayoutKey) ? grid.LayoutKey.Trim() : "Items";
                var last = blocks.LastOrDefault();
                if (last == null || !string.Equals(last.Header, header, StringComparison.OrdinalIgnoreCase))
                {
                    last = new LayoutBlock { Header = header, ItemSize = grid.ItemSize };
                    blocks.Add(last);
                }
                foreach (var row in grid.Rows) last.Rows.Add(new List<string>(row));
            }
            return blocks;
        }

        /// <summary>Blocks for a game without a pack: its items by category, in rows of <paramref name="perRow"/>.</summary>
        public static List<LayoutBlock> BlocksByCategory(IEnumerable<(string Code, string Category)> items, int perRow = 6)
        {
            var blocks = new List<LayoutBlock>();
            foreach (var group in items.GroupBy(i => i.Category).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var block = new LayoutBlock { Header = group.Key };
                var row = new List<string>();
                foreach (var (code, _) in group)
                {
                    row.Add(code);
                    if (row.Count == Math.Max(1, perRow)) { block.Rows.Add(row); row = new List<string>(); }
                }
                if (row.Count > 0) block.Rows.Add(row);
                blocks.Add(block);
            }
            return blocks;
        }
    }
}
