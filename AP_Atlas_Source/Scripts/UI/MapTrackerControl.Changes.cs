#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The Map Tracker's Changes, beside Notices (the owner's round 12): everything changed on this slot's map in plain words, each
    /// with Undo and, where it has a place on the map, Show on map: the checks hidden for this slot, and the fixes to its map pack
    /// (every slot using the pack shares them), the user's and Atlas's (marked, with why Atlas was sure).
    /// </summary>
    public partial class MapTrackerControl
    {
        /// <summary>One change: its key ("hidden:&lt;id&gt;", or the fix's subject), its line, whether Atlas chose it and why, its Undo, its Show on map (null: none).</summary>
        public sealed record ChangeRow(string Key, string Text, bool ByAtlas, string? Why, Action Undo, Action? Show);

        /// <summary>The checks hidden for this slot, with their names (set by the slot).</summary>
        public Func<IReadOnlyList<(long Id, string Name)>>? HiddenList { get; set; }

        private Button? _changesTab;
        private ScrollContainer? _changesPane;
        private VBoxContainer? _changesBox;
        private bool _changesShown;

        /// <summary>Whether the Changes are shown (rather than the maps or the notices).</summary>
        public bool ChangesShown => _changesShown;

        /// <summary>How many changes there are (the tab's count).</summary>
        public int ChangeCount => (HiddenList?.Invoke().Count ?? 0) + (_pack != null && Fixing is { } fix ? PackFixes.Get(fix.Key).Count : 0);

        /// <summary>The Changes tab's text as shown, with its count (for tests).</summary>
        public string ChangesTabText => _changesTab?.Text ?? "";

        /// <summary>Shows the Changes.</summary>
        public void ShowChanges() => ShowPane(Pane.Changes);

        /// <summary>Every change: the hidden checks first (by name), then the pack's fixes, newest first.</summary>
        public List<ChangeRow> Changes()
        {
            var rows = new List<ChangeRow>();
            foreach (var (id, name) in HiddenList?.Invoke() ?? Array.Empty<(long, string)>())
            {
                long check = id;
                rows.Add(new ChangeRow("hidden:" + id.ToString(CultureInfo.InvariantCulture), PackChangeWords.Hidden(name, Translate), false, null,
                    () => HideChecks?.Invoke(new[] { check }, false), HasLocation(check) ? () => RevealLocation(check) : null));
            }
            if (_pack != null && Fixing is { } fix)
                foreach (var change in PackChanges.Of(PackFixes.Get(fix.Key), fix.Original, Translate))
                {
                    var c = change;
                    rows.Add(new ChangeRow(c.Subject, c.Text, c.ByAtlas, c.Why,
                        () => fix.Apply(Translate("Undone: {0}").Replace("{0}", c.Text), () => PackChanges.Undo(fix.Key, c)), ShowFor(c.Spot)));
                }
            return rows;
        }

        /// <summary>Presses a change's Undo, or its Show on map (for tests, and the cards' buttons): the row as it is now.</summary>
        public void PressChange(string key, bool show)
        {
            var row = Changes().FirstOrDefault(r => r.Key == key);
            if (row == null) return;
            if (show) row.Show?.Invoke();
            else row.Undo();
        }

        /// <summary>The changes changed (a fix, an undo, a check hidden or shown, the pack loaded or gone): the tab's count, and the pane when shown.</summary>
        public void RefreshChanges()
        {
            UpdateChangesTab();
            if (_changesShown) RenderChanges();
        }

        private Action? ShowFor(PackChangeSpot? spot)
        {
            if (spot == null || _pack == null) return null;
            if (spot.LocationId is long id && HasLocation(id)) return () => RevealLocation(id);
            string? map = spot.MapId;
            if (string.IsNullOrEmpty(map) && spot.PinPath != null && _pack.Locations.FirstOrDefault(l => l.FullPath == spot.PinPath) is { } pin) map = MapsOf(pin).FirstOrDefault();
            if (string.IsNullOrEmpty(map) || !_pack.Maps.ContainsKey(map)) return null;
            string mapId = map, path = spot.PinPath ?? "";
            return () => RevealPin(mapId, path);
        }

        private ScrollContainer BuildChangesPane()
        {
            _changesPane = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, Visible = false };
            var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            margin.AddThemeConstantOverride("margin_left", 10);
            margin.AddThemeConstantOverride("margin_right", 10);
            margin.AddThemeConstantOverride("margin_top", 6);
            margin.AddThemeConstantOverride("margin_bottom", 10);
            _changesBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _changesBox.AddThemeConstantOverride("separation", 8);
            margin.AddChild(_changesBox);
            _changesPane.AddChild(margin);
            return _changesPane;
        }

        private void UpdateChangesTab()
        {
            if (_changesTab == null) return;
            int n = ChangeCount;
            _changesTab.Text = n > 0 ? Translate("Changes ({0})").Replace("{0}", n.ToString(CultureInfo.InvariantCulture)) : Translate("Changes");
        }

        private void RenderChanges()
        {
            if (_changesBox == null) return;
            foreach (Node child in _changesBox.GetChildren())
            {
                _changesBox.RemoveChild(child);
                child.QueueFree();
            }
            var rows = Changes();
            if (rows.Count == 0)
            {
                var none = Kit.Subtle(Translate("Nothing changed: the map shows the pack as its author made it, and no check is hidden. Fixes you make (right-click a pin or tile) and the ones Atlas makes are listed here, each with Undo."));
                none.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                _changesBox.AddChild(none);
                return;
            }
            foreach (var row in rows) _changesBox.AddChild(ChangeCard(row));
        }

        private Control ChangeCard(ChangeRow row)
        {
            var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            card.SetMeta("change_row", row.Key);
            card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = ThemeColors.SurfaceSunken,
                BorderColor = row.ByAtlas ? ThemeColors.Accent : ThemeColors.TextMuted,
                BorderWidthLeft = 3,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                ContentMarginLeft = 10,
                ContentMarginRight = 8,
                ContentMarginTop = 6,
                ContentMarginBottom = 6
            });
            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 4);
            var title = new Label { Text = row.Text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(120, 0) };
            title.AddThemeColorOverride("font_color", ThemeColors.Text);
            column.AddChild(title);
            if (row.ByAtlas)
            {
                var by = Kit.Subtle(PackChangeWords.ByAtlas(row.Why, Translate));
                by.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                by.CustomMinimumSize = new Vector2(120, 0);
                column.AddChild(by);
            }
            var buttons = new HFlowContainer();
            buttons.AddThemeConstantOverride("h_separation", 6);
            buttons.AddThemeConstantOverride("v_separation", 4);
            string key = row.Key;
            buttons.AddChild(Kit.Button(Translate("Undo"), Translate("Takes this change back; a card offers to bring it back."), () => PressChange(key, show: false), small: true));
            if (row.Show != null) buttons.AddChild(Kit.Button(Translate("Show on map"), Translate("Shows where it is on the map."), () => PressChange(key, show: true), small: true, flat: true));
            column.AddChild(buttons);
            card.AddChild(column);
            return card;
        }
    }
}
