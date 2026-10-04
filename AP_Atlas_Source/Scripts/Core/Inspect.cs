using System;

namespace AP_Atlas.Core
{
    public enum InspectKind
    {
        /// <summary>The selected slot's overview (also the empty-state summary).</summary>
        Slot,
        /// <summary>A location in some player's world.</summary>
        Location,
        /// <summary>An item in some player's game; optionally one specific received copy.</summary>
        Item,
        /// <summary>A player (slot) in the multiworld.</summary>
        Player,
        /// <summary>A hint (identified by the finding player and location).</summary>
        Hint,
        /// <summary>A map-pack location pin (may cover several AP locations).</summary>
        PackLocation,
        /// <summary>A map of a map pack.</summary>
        Map,
        /// <summary>An installed map pack (zip).</summary>
        Pack,
        /// <summary>A multiworld profile on the Connections tab.</summary>
        Profile,
    }

    /// <summary>
    /// Something the Properties panel can show. Holds identities only (ids, names, slot numbers), never UI nodes,
    /// so history entries stay valid when views rebuild or slots reconnect.
    /// </summary>
    public sealed class InspectTarget
    {
        public InspectKind Kind { get; init; }

        /// <summary>The multiworld profile the target belongs to.</summary>
        public string ProfileId { get; init; }

        /// <summary>The connected slot it was selected from; its session resolves names when the owner isn't connected here.</summary>
        public string ViewSlot { get; init; }

        /// <summary>Owner slot number: a location's world, an item's game (receiving player), or the player itself.</summary>
        public int Player { get; init; } = -1;

        public long LocationId { get; init; }
        public long ItemId { get; init; }
        public string ItemName { get; init; }

        /// <summary>For Item: the index in ViewSlot's received-items list of one specific copy, or -1.</summary>
        public int ReceiptIndex { get; init; } = -1;

        public string PackPath { get; init; }
        public string MapId { get; init; }
        public string PackLocationName { get; init; }

        /// <summary>Profile-level slot name (Slot kind, or Profile kind with a slot row selected).</summary>
        public string SlotName { get; init; }

        public string Key => Kind switch
        {
            InspectKind.Slot => $"slot|{ProfileId}|{SlotName}",
            InspectKind.Location => $"loc|{ProfileId}|{Player}|{LocationId}",
            InspectKind.Item => $"item|{ProfileId}|{Player}|{ItemId}|{ItemName}|{ReceiptIndex}",
            InspectKind.Player => $"player|{ProfileId}|{Player}",
            InspectKind.Hint => $"hint|{ProfileId}|{Player}|{LocationId}",
            InspectKind.PackLocation => $"pin|{ProfileId}|{ViewSlot}|{MapId}|{PackLocationName}",
            InspectKind.Map => $"map|{ProfileId}|{ViewSlot}|{MapId}",
            InspectKind.Pack => $"pack|{PackPath}",
            InspectKind.Profile => $"profile|{ProfileId}|{SlotName}",
            _ => Kind.ToString()
        };

        public static InspectTarget ForSlot(string profileId, string slotName) =>
            new InspectTarget { Kind = InspectKind.Slot, ProfileId = profileId, SlotName = slotName, ViewSlot = slotName };

        public static InspectTarget ForLocation(string profileId, string viewSlot, int player, long locationId) =>
            new InspectTarget { Kind = InspectKind.Location, ProfileId = profileId, ViewSlot = viewSlot, Player = player, LocationId = locationId };

        public static InspectTarget ForItem(string profileId, string viewSlot, int player, long itemId, string itemName, int receiptIndex = -1) =>
            new InspectTarget { Kind = InspectKind.Item, ProfileId = profileId, ViewSlot = viewSlot, Player = player, ItemId = itemId, ItemName = itemName, ReceiptIndex = receiptIndex };

        public static InspectTarget ForPlayer(string profileId, string viewSlot, int player) =>
            new InspectTarget { Kind = InspectKind.Player, ProfileId = profileId, ViewSlot = viewSlot, Player = player };

        public static InspectTarget ForHint(string profileId, string viewSlot, int findingPlayer, long locationId) =>
            new InspectTarget { Kind = InspectKind.Hint, ProfileId = profileId, ViewSlot = viewSlot, Player = findingPlayer, LocationId = locationId };

        public static InspectTarget ForPackLocation(string profileId, string viewSlot, string mapId, string pinName) =>
            new InspectTarget { Kind = InspectKind.PackLocation, ProfileId = profileId, ViewSlot = viewSlot, MapId = mapId, PackLocationName = pinName };

        public static InspectTarget ForMap(string profileId, string viewSlot, string mapId) =>
            new InspectTarget { Kind = InspectKind.Map, ProfileId = profileId, ViewSlot = viewSlot, MapId = mapId };

        public static InspectTarget ForPack(string packPath) =>
            new InspectTarget { Kind = InspectKind.Pack, PackPath = packPath };

        public static InspectTarget ForProfile(string profileId, string slotName = null) =>
            new InspectTarget { Kind = InspectKind.Profile, ProfileId = profileId, SlotName = slotName };
    }

    /// <summary>Wires a Tree so every pick (mouse click, including re-clicking the selected row, or keyboard) reports the row and column.</summary>
    public static class TreePicks
    {
        /// <param name="onPick">Row and clicked column (-1 for keyboard selection).</param>
        public static void Hook(Godot.Tree tree, Action<Godot.TreeItem, int> onPick)
        {
            ulong lastMouseFrame = ulong.MaxValue;
            tree.ItemMouseSelected += (position, button) =>
            {
                if (button != (long)Godot.MouseButton.Left) return;
                lastMouseFrame = Godot.Engine.GetProcessFrames();
                var item = tree.GetSelected();
                if (item != null) onPick(item, tree.GetColumnAtPosition((Godot.Vector2I)position));
            };
            // item_selected fires before item_mouse_selected; defer so a mouse pick in the same frame wins.
            tree.ItemSelected += () => Godot.Callable.From(() =>
            {
                if (Godot.Engine.GetProcessFrames() == lastMouseFrame) return;
                var item = tree.GetSelected();
                if (item != null) onPick(item, -1);
            }).CallDeferred();
        }
    }

    /// <summary>The hub between views and the Properties panel: views call Inspect() when the user selects something. Main thread only.</summary>
    public static class Inspector
    {
        public static event Action<InspectTarget> Requested;

        public static void Inspect(InspectTarget target)
        {
            if (target == null) return;
            Requested?.Invoke(target);
        }
    }
}
