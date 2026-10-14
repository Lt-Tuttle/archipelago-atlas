#nullable enable
using System;
using System.Collections.Generic;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// What a view needs to fix its slot's map pack in place ("Fix a problem", right-click): the author's pack (the fixes
    /// are stamped against it), the slot's index (names, links, what isn't placed), and the window's ways to open the Pack
    /// Doctor and to say what was done with an Undo. Set by the slot each time it pairs the pack.
    /// </summary>
    public sealed class PackFixContext
    {
        /// <summary>The author's pack as read from its zip (never the effective one).</summary>
        public LoadedPack Original { get; init; } = null!;
        /// <summary>The slot's index over the effective pack (its location and item names are the seed's).</summary>
        public PackIndex Index { get; init; } = null!;
        /// <summary>Opens the Pack Doctor on a tab, or at a pin (its full path) on the Maps tab.</summary>
        public Action<string?, string?>? OpenDoctor { get; init; }
        /// <summary>Says what was done, with an action (text, colour, action label, action).</summary>
        public Action<string, Color, string, Action>? Say { get; init; }

        /// <summary>The pack's key (its fix file).</summary>
        public string Key => PackFixes.KeyFor(Original);

        /// <summary>Runs a fix and says so with Undo; a fix that can't be made says why.</summary>
        public void Apply(string done, Action fix)
        {
            try
            {
                fix();
                string key = Key;
                Say?.Invoke(done, ThemeColors.Success, MapTrackerControl.Translate("Undo"), () => PackFixes.Undo(key));
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
            {
                Say?.Invoke(MapTrackerControl.Translate("That fix couldn't be made: ") + ex.Message, ThemeColors.Warning, MapTrackerControl.Translate("Open the Pack Doctor"), () => OpenDoctor?.Invoke("Overview", null));
            }
        }
    }

    /// <summary>One line of a fix menu: a label and what it does (no action: a heading or a hint, shown greyed).</summary>
    public sealed record FixItem(string Label, Action? Run);

    /// <summary>The fix menus' shared pieces: the popup itself, and choosing a picture file.</summary>
    public static class PackFixMenus
    {
        /// <summary>Shows a fix menu at the mouse (frees itself when closed).</summary>
        public static PopupMenu Show(Control owner, IReadOnlyList<FixItem> items)
        {
            var menu = new PopupMenu();
            menu.SetMeta("fix_menu", true);
            var actions = new Dictionary<long, Action>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Label.Length == 0) { menu.AddSeparator(); continue; }
                menu.AddItem(item.Label, i);
                int index = menu.GetItemIndex(i);
                if (item.Run == null) menu.SetItemDisabled(index, true);
                else actions[i] = item.Run;
            }
            menu.IdPressed += id => { if (actions.TryGetValue(id, out var run)) run(); };
            menu.PopupHide += () => menu.QueueFree();
            owner.AddChild(menu);
            menu.Position = (Vector2I)owner.GetGlobalMousePosition();
            menu.Popup();
            return menu;
        }

        /// <summary>A separator line in a fix menu.</summary>
        public static FixItem Separator => new FixItem("", null);

        /// <summary>Asks for a picture file (PNG, JPEG or WebP) and hands its path on; nothing is read until one is chosen.</summary>
        public static void PickImageFile(Node parent, string title, Action<string> onPicked)
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.png, *.jpg, *.jpeg, *.webp ; Images" },
                UseNativeDialog = true,
                Title = title
            };
            dialog.FileSelected += path =>
            {
                onPicked(path);
                dialog.QueueFree();
            };
            dialog.Canceled += () => dialog.QueueFree();
            parent.GetTree().Root.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }
    }
}
