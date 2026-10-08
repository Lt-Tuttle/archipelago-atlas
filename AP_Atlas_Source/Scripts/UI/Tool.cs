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

    /// <summary>The group a tool sits in on the activity bar, top to bottom: the slot tools, the multiworld tools, Atlas's own pages.</summary>
    public enum ToolGroup
    {
        Slot,
        Multiworld,
        Atlas
    }

    /// <summary>
    /// One of Atlas's tools: a button on the activity bar, an item in the Tools menu (Ctrl+1 to Ctrl+9, in the bar's order),
    /// and the view the content area shows; a tool past the ninth brings its own key. In the new shell they become panels
    /// that can be docked and popped out.
    /// A slot tool shows the selected slot's own view; the window registers the views of the others.
    /// </summary>
    public sealed class Tool
    {
        private Tool(string id, string title, string shortTitle, ToolScope scope, ToolGroup group, string icon)
        {
            Id = id;
            Title = title;
            ShortTitle = shortTitle;
            Scope = scope;
            Group = group;
            Icon = icon;
        }

        /// <summary>A stable name for the tool (for saved layouts, settings and key bindings; never shown).</summary>
        public string Id { get; }

        public string Title { get; }

        /// <summary>One short word for under the tool's icon on the activity bar ("Items", "Spheres", "Worlds").</summary>
        public string ShortTitle { get; }

        public ToolScope Scope { get; }

        public ToolGroup Group { get; }

        /// <summary>The tool's icon on the activity bar: a Lucide icon Atlas ships (<see cref="LucideIcons"/>).</summary>
        public string Icon { get; }

        /// <summary>A slot tool's view in a connected slot.</summary>
        public Func<SlotTrackerControl, Control?>? SlotView { get; private init; }

        /// <summary>What a slot tool shows in the explorer, if anything, under <see cref="ExplorerTitle"/>.</summary>
        public Func<SlotTrackerControl, Control?>? SlotExplorer { get; private init; }

        public string ExplorerTitle { get; private init; } = "";

        /// <summary>A tool's own default key, for a tool past the first nine (which get Ctrl+1 to Ctrl+9).</summary>
        public string DefaultKey { get; private init; } = "";

        public static readonly Tool MapTracker = new("map-tracker", "Map Tracker", "Map", ToolScope.Slot, ToolGroup.Slot, "map")
        {
            SlotView = slot => slot.MapTracker,
            SlotExplorer = slot => slot.MapTracker?.SidebarContent,
            ExplorerTitle = "Maps"
        };
        public static readonly Tool KeyItems = new("key-items", "Key Items", "Items", ToolScope.Slot, ToolGroup.Slot, "key-round") { SlotView = slot => slot.ProgressionTracker };
        public static readonly Tool LogicTracker = new("logic-tracker", "Logic Tracker", "Logic", ToolScope.Slot, ToolGroup.Slot, "route") { SlotView = slot => slot.LogicTrackerView };
        public static readonly Tool ItemHistory = new("item-history", "Item History", "History", ToolScope.Slot, ToolGroup.Slot, "logs") { SlotView = slot => slot.ItemHistoryView };
        public static readonly Tool Hints = new("hints", "Hints", "Hints", ToolScope.Slot, ToolGroup.Slot, "lightbulb") { SlotView = slot => slot.HintsView };
        public static readonly Tool CheeseTracker = new("cheese-tracker", "Cheese Tracker", "Cheese", ToolScope.Multiworld, ToolGroup.Multiworld, "users");
        public static readonly Tool SphereTracker = new("sphere-tracker", "Sphere Tracker", "Spheres", ToolScope.Multiworld, ToolGroup.Multiworld, "orbit");
        public static readonly Tool Home = new("home", "Home", "Home", ToolScope.App, ToolGroup.Atlas, "house");
        public static readonly Tool Connections = new("connections", "Multiworlds", "Worlds", ToolScope.App, ToolGroup.Atlas, "globe");
        public static readonly Tool MapPacks = new("map-packs", "Map Packs", "Packs", ToolScope.App, ToolGroup.Atlas, "package"); // the tenth tool: no number key (Ctrl+0 resets the zoom)
        public static readonly Tool Settings = new("settings", "Settings", "Settings", ToolScope.App, ToolGroup.Atlas, "settings") { DefaultKey = "Ctrl+," };

        /// <summary>Every tool, in the activity bar's order, top to bottom (Ctrl+1 to Ctrl+9).</summary>
        public static IReadOnlyList<Tool> All { get; } = new[] { MapTracker, KeyItems, LogicTracker, ItemHistory, Hints, CheeseTracker, SphereTracker, Home, Connections, MapPacks, Settings };

        /// <summary>The tool with an id, or null for none (a setting from another version).</summary>
        public static Tool? Named(string? id) => System.Linq.Enumerable.FirstOrDefault(All, tool => tool.Id == id);

        /// <summary>The tool's place in <see cref="All"/>.</summary>
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
