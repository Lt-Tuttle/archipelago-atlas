#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.UI;
using Godot;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>
    /// Fixing the pack where the problem shows, in Key Items: "Fix a problem" in the toolbar, right-click on a tile (its
    /// picture, which item it is, hiding it) or on the empty grid (a tile for a missing item). Every choice is a Pack
    /// Doctor fix (<see cref="PackFixActions"/>), applied at once with an Undo.
    /// </summary>
    public partial class ProgressionTrackerControl
    {
        /// <summary>What the menus fix with; null while the slot has no pack.</summary>
        public PackFixContext? Fixing { get; set; }

        private static string T(string text) => MapTrackerControl.Translate(text);

        /// <summary>The "Fix a problem" menu's lines (for tests and the button).</summary>
        public List<FixItem> FixProblemItems()
        {
            var items = new List<FixItem> { new FixItem(T("A tile's picture or item is wrong: right-click the tile"), null) };
            items.AddRange(GridItems());
            return items;
        }

        /// <summary>The empty grid's menu: a tile for an item the pack shows nowhere, the Doctor.</summary>
        public List<FixItem> GridItems()
        {
            var fix = Fixing;
            if (fix == null) return new List<FixItem> { new FixItem(T("No map pack to fix"), null) };
            return new List<FixItem>
            {
                new FixItem(T("Add a tile for an item the pack doesn't show…"), () =>
                    NamePickerDialog.Open(this, T("Add a tile for which item?"), null, fix.Index.ItemNames, new List<Suggestion>(), (id, name) =>
                        fix.Apply(T("Added a tile for {0}.").Replace("{0}", name), () => PackFixActions.AddTile(fix.Original, id, name, 0)))),
                PackFixMenus.Separator,
                new FixItem(T("Something else… (open the Pack Doctor)"), () => fix.OpenDoctor?.Invoke("Key Items", null)),
                new FixItem(T("Report this to the pack's author…"), () => fix.OpenDoctor?.Invoke("Report", null)),
            };
        }

        /// <summary>A tile's menu: its picture (the pack's own, a file, the author's again), which item it is, hiding it.</summary>
        public List<FixItem> TileItems(string code, string? itemName)
        {
            var fix = Fixing;
            if (fix == null) return new List<FixItem> { new FixItem(T("No map pack to fix"), null) };
            string shown = string.IsNullOrEmpty(itemName) ? code : itemName!;
            var items = new List<FixItem>
            {
                new FixItem(shown, null),
                new FixItem(T("Choose this tile's picture from the pack…"), () =>
                    PackPicturePicker.Open(this, T("A picture for {0}").Replace("{0}", shown), fix.Original, itemName ?? code, null, path =>
                        fix.Apply(T("{0} shows the pack's {1}.").Replace("{0}", shown).Replace("{1}", path), () => PackFixActions.SetTileImage(fix.Original, code, PackFixActions.ImportPackImage(fix.Original, path))))),
                new FixItem(T("Choose a picture file for this tile…"), () =>
                    PackFixMenus.PickImageFile(this, T("A picture for {0}").Replace("{0}", shown), path =>
                        fix.Apply(T("{0} shows your picture.").Replace("{0}", shown), () => PackFixActions.SetTileImage(fix.Original, code, PackFixes.ImportImage(fix.Key, path))))),
            };
            if (PackFixActions.HasTileImageFix(fix.Original, code))
                items.Add(new FixItem(T("Use the pack's own picture again"), () => fix.Apply(T("{0} shows the pack's picture again.").Replace("{0}", shown), () => PackFixActions.ResetTileImage(fix.Original, code))));
            items.Add(new FixItem(T("This tile is a different item…"), () =>
            {
                var names = fix.Index.ItemNames;
                NamePickerDialog.Open(this, T("Which item is this tile?"), shown, names, PackDoctor.Suggest(shown, names, 8), (id, name) =>
                    fix.Apply(T("The tile now tracks {0}.").Replace("{0}", name), () => PackFixActions.LinkTile(fix.Original, code, id, name)));
            }));
            items.Add(new FixItem(T("Hide this tile"), () => fix.Apply(T("{0} is hidden.").Replace("{0}", shown), () => PackFixActions.SetTileHidden(fix.Original, code, true))));
            items.Add(PackFixMenus.Separator);
            items.Add(new FixItem(T("Something else… (open the Pack Doctor)"), () => fix.OpenDoctor?.Invoke("Key Items", null)));
            items.Add(new FixItem(T("Report this to the pack's author…"), () => fix.OpenDoctor?.Invoke("Report", null)));
            return items;
        }

        /// <summary>Hooks a tile's right click to its menu.</summary>
        private void HookTileMenu(Control tile, string code, string? itemName)
        {
            tile.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Right })
                {
                    PackFixMenus.Show(this, TileItems(code, itemName));
                    tile.AcceptEvent();
                }
            };
        }

        /// <summary>The toolbar's "Fix a problem…" button.</summary>
        private Button FixProblemButton()
        {
            var button = Kit.Button(T("Fix a problem…"), T("A tile showing the wrong picture or the wrong item, an item the pack doesn't show: fixed here, with Undo. Right-click a tile for its own fixes."),
                () => PackFixMenus.Show(this, FixProblemItems()), small: true);
            button.SetMeta("fix_problem", "items");
            return button;
        }
    }
}
