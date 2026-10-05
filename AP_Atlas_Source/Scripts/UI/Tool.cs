using System;
using System.Collections.Generic;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>Where a tool works: for the whole app, for a multiworld, or for one connected slot.</summary>
    public enum ToolScope
    {
        App,
        Multiworld,
        Slot
    }

    /// <summary>
    /// One of Atlas's tools. Today each is a tab; in the new shell they become panels that can be docked and popped out.
    /// A slot tool shows the selected slot's own view; the window registers the views of the others.
    /// </summary>
    public sealed class Tool
    {
        private Tool(string id, string title, ToolScope scope)
        {
            Id = id;
            Title = title;
            Scope = scope;
        }

        /// <summary>A stable name for the tool (for saved layouts and settings; never shown).</summary>
        public string Id { get; }

        public string Title { get; }

        public ToolScope Scope { get; }

        /// <summary>A slot tool's view in a connected slot.</summary>
        public Func<SlotTrackerControl, Control?>? SlotView { get; private init; }

        /// <summary>What a slot tool shows in the explorer, if anything, under <see cref="ExplorerTitle"/>.</summary>
        public Func<SlotTrackerControl, Control?>? SlotExplorer { get; private init; }

        public string ExplorerTitle { get; private init; } = "";

        public static readonly Tool Connections = new("connections", "Connections", ToolScope.App);
        public static readonly Tool MapPacks = new("map-packs", "Map Packs", ToolScope.App);
        public static readonly Tool MapTracker = new("map-tracker", "Map Tracker", ToolScope.Slot)
        {
            SlotView = slot => slot.MapTracker,
            SlotExplorer = slot => slot.MapTracker?.SidebarContent,
            ExplorerTitle = "Maps"
        };
        public static readonly Tool KeyItems = new("key-items", "Key Items", ToolScope.Slot) { SlotView = slot => slot.ProgressionTracker };
        public static readonly Tool LogicTracker = new("logic-tracker", "Logic Tracker", ToolScope.Slot) { SlotView = slot => slot.LogicTrackerView };
        public static readonly Tool ItemHistory = new("item-history", "Item History", ToolScope.Slot) { SlotView = slot => slot.ItemHistoryView };
        public static readonly Tool Hints = new("hints", "Hints", ToolScope.Slot) { SlotView = slot => slot.HintsView };
        public static readonly Tool CheeseTracker = new("cheese-tracker", "Cheese Tracker", ToolScope.Multiworld);
        public static readonly Tool SphereTracker = new("sphere-tracker", "Sphere Tracker", ToolScope.Multiworld);

        /// <summary>Every tool, in tab order.</summary>
        public static IReadOnlyList<Tool> All { get; } = new[] { Connections, MapPacks, MapTracker, KeyItems, LogicTracker, ItemHistory, Hints, CheeseTracker, SphereTracker };

        /// <summary>The tool's place in <see cref="All"/> (its tab).</summary>
        public int Index
        {
            get
            {
                for (int i = 0; i < All.Count; i++)
                    if (All[i] == this) return i;
                throw new InvalidOperationException(Title + " isn't in the tool list");
            }
        }

        public override string ToString() => Title;
    }
}
