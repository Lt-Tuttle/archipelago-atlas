using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Godot;
using Color = Godot.Color;

namespace AP_Atlas.UI
{
    public partial class PackDoctorWindow
    {
        private string _selectedTileCode;
        private int _selectedGridIndex = -1;

        /// <summary>
        /// Every grid of the pack as Key Items will show it, each tile outlined by how it's linked
        /// (green: the pack's script, blue: by name, gold: your fix, red: not linked); click a tile to edit it.
        /// </summary>
        private Control BuildItemsTab()
        {
            var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            var leftScroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.6f };
            var left = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            left.AddThemeConstantOverride("separation", 8);
            leftScroll.AddChild(left);
            split.AddChild(leftScroll);

            var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            right.AddThemeConstantOverride("separation", 8);
            split.AddChild(right);

            left.AddChild(Note("Outline: green = linked by the pack's script · blue = by name · gold = your fix · red = not linked · purple = seed setting (dimmed when off). Click a tile to edit it."));
            var legendRow = new HBoxContainer();
            legendRow.AddChild(Btn("Add a tile for an item…", "Add a tile for an Archipelago item the grid doesn't show", AddTileForItem));
            left.AddChild(legendRow);

            var pack = _report.Pack;
            var fixes = PackFixes.Get(_key);
            var userLinked = new HashSet<string>(fixes.Tiles.Where(t => t.ApItemId != null).Select(t => t.Code).Concat(fixes.AddedTiles.Select(a => a.Code)), StringComparer.OrdinalIgnoreCase);
            var scriptCodes = new HashSet<string>(_original.ItemMapping.Where(kv => !_report.Index.UnknownMappedItemIds.Contains(kv.Key)).SelectMany(kv => kv.Value), StringComparer.OrdinalIgnoreCase);

            for (int gi = 0; gi < pack.ItemGridGroups.Count; gi++)
            {
                var grid = pack.ItemGridGroups[gi];
                int gridIndex = gi;
                string subject = PackFixes.GridSubject(grid);
                var header = new HBoxContainer();
                header.AddThemeConstantOverride("separation", 8);
                var title = Heading(string.IsNullOrEmpty(grid.Header) ? grid.LayoutKey : grid.Header);
                if (grid.LooksLikeSettings) title.AddThemeColorOverride("font_color", Muted);
                header.AddChild(title);
                var show = new CheckButton { Text = grid.LooksLikeSettings ? "Seed settings (switch on to treat as items)" : "Items (switch off for seed settings)", ButtonPressed = !grid.LooksLikeSettings, FocusMode = Control.FocusModeEnum.None, TooltipText = "Items count toward Key Items. Seed settings are lit from the slot's options by the pack's script." };
                show.Toggled += on => SetGridHidden(subject, !on);
                header.AddChild(show);
                header.AddChild(new Label { Text = "Tile size" });
                var size = new SpinBox { MinValue = 16, MaxValue = 128, Step = 2, Value = grid.ItemSize, TooltipText = "The pack's tile size for this grid (relative sizes are kept in Key Items)" };
                size.ValueChanged += v => PackFixes.Edit(_key, "Change tile size", f => UpsertGrid(f, subject).ItemSize = (int)v);
                header.AddChild(size);
                left.AddChild(header);

                foreach (var row in grid.Rows)
                {
                    var flow = new HFlowContainer();
                    flow.AddThemeConstantOverride("h_separation", 4);
                    flow.AddThemeConstantOverride("v_separation", 4);
                    foreach (var code in row)
                    {
                        if (string.IsNullOrWhiteSpace(code)) { flow.AddChild(new Control { CustomMinimumSize = new Vector2(56, 56) }); continue; }
                        flow.AddChild(TileButton(pack, code, gridIndex, scriptCodes, userLinked, grid.LooksLikeSettings));
                    }
                    left.AddChild(flow);
                }
                left.AddChild(new HSeparator());
            }

            BuildTileInspector(right, pack, scriptCodes, userLinked);
            return split;
        }

        private static readonly Color SettingColor = Colors.MediumPurple;

        private Control TileButton(LoadedPack pack, string code, int gridIndex, HashSet<string> scriptCodes, HashSet<string> userLinked, bool isSettingsGrid = false)
        {
            pack.ItemsByCode.TryGetValue(code, out var item);
            var ids = _report.Index.ItemIdsFor(code);
            var setting = isSettingsGrid ? _report.Settings.FirstOrDefault(s => item != null && item.GetCodes().Contains(s.Code, StringComparer.OrdinalIgnoreCase)) : null;
            // Settings tiles aren't items: purple, and dimmed when the seed has that setting off.
            Color outline = isSettingsGrid ? SettingColor
                : item == null ? Bad
                : userLinked.Contains(code) ? Fixed
                : ids.Count == 0 ? Bad
                : scriptCodes.Contains(code) ? Good
                : Colors.DeepSkyBlue;

            var panel = new PanelContainer { CustomMinimumSize = new Vector2(56, 56), MouseDefaultCursorShape = Control.CursorShape.PointingHand };
            bool selected = code == _selectedTileCode;
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = selected ? new Color("#30303a") : new Color("#202027"),
                BorderColor = outline,
                BorderWidthTop = selected ? 4 : 2,
                BorderWidthBottom = selected ? 4 : 2,
                BorderWidthLeft = selected ? 4 : 2,
                BorderWidthRight = selected ? 4 : 2,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4
            });
            var tex = item == null ? null : pack.FindImage(PackDoctor.TileImagePath(item, code));
            if (tex != null)
            {
                panel.AddChild(new TextureRect
                {
                    Texture = tex,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    CustomMinimumSize = new Vector2(52, 52),
                    MouseFilter = Control.MouseFilterEnum.Ignore
                });
            }
            else
            {
                var l = new Label { Text = item?.Name ?? code, AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, CustomMinimumSize = new Vector2(52, 52), MouseFilter = Control.MouseFilterEnum.Ignore };
                l.SetMeta("font_size_ratio", 0.6);
                panel.AddChild(l);
            }
            string apName = ids.Count > 0 ? (_report.Index.ItemName(ids[0]) ?? $"id {ids[0]}") : "not linked";
            panel.TooltipText = isSettingsGrid
                ? $"{item?.Name ?? code}\nSeed setting" + (setting != null ? $": {setting.StageName ?? (setting.On ? "on" : "off")}" + (setting.OptionPath != null ? $"\nOption: {setting.OptionPath}" : "") : "")
                : $"{item?.Name ?? code}\ncode: {code}\nTracks: {apName}";
            if (isSettingsGrid && setting != null && !setting.On) panel.Modulate = new Color(1, 1, 1, 0.45f);
            panel.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                {
                    _selectedTileCode = code;
                    _selectedGridIndex = gridIndex;
                    RenderCurrentTab();
                }
            };
            return panel;
        }

        private void BuildTileInspector(VBoxContainer box, LoadedPack pack, HashSet<string> scriptCodes, HashSet<string> userLinked)
        {
            if (_selectedTileCode == null)
            {
                box.AddChild(Heading("Tile"));
                box.AddChild(Note("Select a tile on the left to change which Archipelago item it tracks, give it an image, move it, or hide it."));
                return;
            }
            string code = _selectedTileCode;
            pack.ItemsByCode.TryGetValue(code, out var item);
            var ids = _report.Index.ItemIdsFor(code);
            box.AddChild(Heading(item?.Name ?? code));
            box.AddChild(Note($"Code: {code}" + (item != null ? $" · type: {item.Type}" + (item.GetCodes().Count > 1 ? $" · all codes: {string.Join(", ", item.GetCodes())}" : "") : " · not defined by the pack")));

            var tex = item == null ? null : pack.FindImage(PackDoctor.TileImagePath(item, code));
            if (tex != null) box.AddChild(new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspect, CustomMinimumSize = new Vector2(96, 96) });
            else box.AddChild(Note("No image.", Warn));

            // A seed-setting indicator: no item to link; show what the pack's script read.
            var grid = _selectedGridIndex >= 0 && _selectedGridIndex < pack.ItemGridGroups.Count ? pack.ItemGridGroups[_selectedGridIndex] : null;
            if (grid != null && grid.LooksLikeSettings)
            {
                var setting = _report.Settings.FirstOrDefault(s => item != null && item.GetCodes().Contains(s.Code, StringComparer.OrdinalIgnoreCase));
                var head = new Label { Text = "Seed setting indicator", AutowrapMode = TextServer.AutowrapMode.WordSmart };
                head.AddThemeColorOverride("font_color", SettingColor);
                box.AddChild(head);
                if (setting == null)
                {
                    box.AddChild(Note(_report.SlotDataSource == null
                        ? "Connect a slot of this game once: Atlas saves its options and the pack's script lights this from them."
                        : "The pack's script didn't set this from the slot's options."));
                }
                else
                {
                    box.AddChild(Note($"State: {setting.StageName ?? (setting.On ? "on" : "off")} (from {_report.SlotDataSource})", setting.On ? Good : Colors.LightGray));
                    if (setting.OptionPath != null)
                        box.AddChild(Note(setting.OptionMissing
                            ? $"Reads {setting.OptionPath}, which that slot's data doesn't have."
                            : $"Reads {setting.OptionPath} = {setting.ValueText}",
                            setting.OptionMissing ? Warn : Colors.LightGray));
                }
                box.AddChild(Note("Seed settings show in their own group in Key Items and in the slot's Properties; they never count as items. To count this grid as items instead, switch it on above."));
                return;
            }

            string source = ids.Count == 0 ? "not linked" : userLinked.Contains(code) ? "your fix" : scriptCodes.Contains(code) ? "the pack's mapping script" : "matching name";
            var tracks = new Label
            {
                Text = ids.Count == 0 ? "Tracks: nothing (never shows as collected)" : $"Tracks: {string.Join(", ", ids.Select(id => _report.Index.ItemName(id) ?? $"id {id}"))}  ({source})",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            tracks.AddThemeColorOverride("font_color", ids.Count == 0 ? Bad : Colors.White);
            box.AddChild(tracks);

            var actions = new HFlowContainer();
            actions.AddThemeConstantOverride("h_separation", 6);
            actions.AddThemeConstantOverride("v_separation", 6);
            actions.AddChild(Btn("Choose item…", "Pick the Archipelago item this tile tracks", () => PickTileItem(code, item?.Name ?? code, null)));
            actions.AddChild(Btn("Replace image…", "Use an image file for this tile", () => PickTileImage(code), item != null));
            actions.AddChild(Btn("◀ Move", "Move left in its row", () => MoveTile(code, -1)));
            actions.AddChild(Btn("Move ▶", "Move right in its row", () => MoveTile(code, 1)));
            bool added = code.StartsWith("atlas_item_");
            if (added) actions.AddChild(Btn("Remove tile", "Remove this added tile", () => PackFixes.Edit(_key, "Remove added tile", f => f.AddedTiles.RemoveAll(a => a.Code == code))));
            else actions.AddChild(Btn("Hide tile", "Remove it from Key Items (reset brings it back)", () => SetTileHidden(code, true)));
            var tileFix = PackFixes.Get(_key).Tiles.FirstOrDefault(t => t.Code == code);
            if (tileFix != null) actions.AddChild(Btn("Reset tile", "Back to the author's version", () => PackFixes.Reset(_key, "tiles", tileFix.Subject)));
            box.AddChild(actions);

            var hidden = PackFixes.Get(_key).Tiles.Where(t => t.Hidden).ToList();
            if (hidden.Count > 0)
            {
                box.AddChild(Heading("Hidden tiles"));
                foreach (var h in hidden)
                {
                    var row = new HBoxContainer();
                    row.AddChild(new Label { Text = _original.ItemsByCode.TryGetValue(h.Code, out var hi) ? hi.Name : h.Code, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
                    string hc = h.Code;
                    row.AddChild(Btn("Show", "Show this tile again", () => SetTileHidden(hc, false)));
                    box.AddChild(row);
                }
            }
        }

        /// <summary>Moves a tile within its row; stored as the grid's row order.</summary>
        private void MoveTile(string code, int delta)
        {
            var grid = _selectedGridIndex >= 0 && _selectedGridIndex < _report.Pack.ItemGridGroups.Count ? _report.Pack.ItemGridGroups[_selectedGridIndex] : null;
            if (grid == null) return;
            var rows = grid.Rows.Select(r => new List<string>(r)).ToList();
            var row = rows.FirstOrDefault(r => r.Contains(code));
            if (row == null) return;
            int i = row.IndexOf(code), j = i + delta;
            if (j < 0 || j >= row.Count) return;
            (row[i], row[j]) = (row[j], row[i]);
            string subject = PackFixes.GridSubject(_original.ItemGridGroups.ElementAtOrDefault(_selectedGridIndex) ?? grid);
            PackFixes.Edit(_key, "Reorder tiles", f => UpsertGrid(f, subject).Rows = rows);
        }

        private void AddTileForItem()
        {
            NamePickerDialog.Open(this, "Add a tile for which Archipelago item?", null, ItemNames, new List<Suggestion>(), (id, name) =>
            {
                int gridIndex = Math.Max(0, _report.Pack.ItemGridGroups.Where(g => !g.LooksLikeSettings).ToList().FindIndex(g => g == _report.Pack.ItemGridGroups.ElementAtOrDefault(_selectedGridIndex)));
                PackFixes.Edit(_key, $"Add tile for {name}", f =>
                {
                    f.AddedTiles.RemoveAll(a => a.ApItemId == id);
                    f.AddedTiles.Add(new AddedTile { Subject = "added:item:" + id, ApItemId = id, ApItemName = name, GridIndex = gridIndex });
                });
                _selectedTileCode = "atlas_item_" + id;
                SetStatus($"Added a tile for {name}. Give it an image with \"Replace image…\".");
            });
        }
    }
}
