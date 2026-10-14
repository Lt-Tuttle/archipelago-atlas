#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Fixing the pack where the problem shows: "Fix a problem" in the map's toolbar, and right-click on the map, a pin or a
    /// "Not on the map" row. Every choice is a Pack Doctor fix (<see cref="PackFixActions"/>), applied at once with an Undo.
    /// </summary>
    public partial class MapTrackerControl
    {
        /// <summary>What the menus fix with; null while the slot has no pack (the menus then say so).</summary>
        public PackFixContext? Fixing { get; set; }

        /// <summary>A pin being moved, or a check being placed: the next left click on the map puts it there.</summary>
        private (string What, Action<Vector2> Put)? _placing;
        private PanelContainer? _placeBar;
        private Label? _placeNote;

        /// <summary>What the next click on the map will do (for tests), or null.</summary>
        public string? Placing => _placing?.What;

        /// <summary>The "Fix a problem" menu's lines (for tests and the button).</summary>
        public List<FixItem> FixProblemItems()
        {
            var items = MapItems(null);
            items.Insert(0, new FixItem(Translate("A pin is wrong: right-click it"), null));
            return items;
        }

        /// <summary>The map's menu: its picture, a check missing here (a point on the map: right-click), the Doctor.</summary>
        public List<FixItem> MapItems(Vector2? at)
        {
            var items = new List<FixItem>();
            var fix = Fixing;
            if (fix == null || _pack == null || string.IsNullOrEmpty(_currentMapId) || !_pack.Maps.TryGetValue(_currentMapId, out var map))
            {
                items.Add(new FixItem(Translate("No map pack to fix"), null));
                return items;
            }
            string mapId = _currentMapId;
            string shown = _pack.DisplayNameOfMap(map.Name);
            var candidates = PackFixActions.MapPictureCandidates(fix.Original, map);
            if (!HasPicture(map))
                items.Add(new FixItem(candidates.Count > 0
                    ? Translate("This map's picture is missing: use the pack's {0}").Replace("{0}", candidates[0])
                    : Translate("This map's picture is missing: none of the pack's pictures is named like it"), candidates.Count > 0 ? () => UseMapPicture(fix, mapId, shown, candidates[0]) : null));
            items.Add(new FixItem(Translate("Choose this map's picture from the pack…"), () =>
                PackPicturePicker.Open(this, Translate("A picture for {0}").Replace("{0}", shown), fix.Original, map.Name, candidates, path => UseMapPicture(fix, mapId, shown, path))));
            items.Add(new FixItem(Translate("Choose a picture file for this map…"), () =>
                PackFixMenus.PickImageFile(this, Translate("A picture for {0}").Replace("{0}", shown), path =>
                    fix.Apply(Translate("{0} shows your picture.").Replace("{0}", shown), () => PackFixActions.SetMapImage(fix.Original, mapId, PackFixes.ImportImage(fix.Key, path))))));
            if (PackFixActions.HasMapImageFix(fix.Original, mapId))
                items.Add(new FixItem(Translate("Use the pack's own picture again"), () => fix.Apply(Translate("{0} shows the pack's picture again.").Replace("{0}", shown), () => PackFixActions.ResetMapImage(fix.Original, mapId))));
            items.Add(PackFixMenus.Separator);
            if (at is Vector2 point)
                items.Add(new FixItem(Translate("Add a missing check here…"), () => PickUnplaced(fix, name => Translate("A pin for {0} here").Replace("{0}", name), (id, name) =>
                    fix.Apply(Translate("Placed {0} on {1}.").Replace("{0}", name).Replace("{1}", shown), () => PackFixActions.PlacePin(fix.Original, id, name, mapId, point.X, point.Y)))));
            else
                items.Add(new FixItem(Translate("Place a missing check on this map…"), () => PickUnplaced(fix, name => name, (id, name) => StartPlacing(id, name))));
            items.Add(new FixItem(Translate("Something else… (open the Pack Doctor)"), () => fix.OpenDoctor?.Invoke("Maps", null)));
            items.Add(new FixItem(Translate("Report this to the pack's author…"), () => fix.OpenDoctor?.Invoke("Report", null)));
            return items;
        }

        /// <summary>A pin's menu: move it, which check each section shows, not a check, remove it, the Doctor.</summary>
        public List<FixItem> PinItems(PopTrackerLocation pin, float x, float y)
        {
            var items = new List<FixItem>();
            var fix = Fixing;
            if (fix == null) { items.Add(new FixItem(Translate("No map pack to fix"), null)); return items; }
            string mapId = _currentMapId;
            bool added = pin.FullPath.StartsWith("atlas/", StringComparison.Ordinal);
            items.Add(new FixItem(pin.Name, null));
            items.Add(new FixItem(Translate("Move this pin"), () => StartMoving(pin, added)));
            var sections = pin.Sections != null && pin.Sections.Count > 0 ? pin.Sections.ToList() : new List<PopTrackerSection> { null! };
            if (!added)
                foreach (var sec in sections)
                {
                    string? secName = sec?.Name;
                    string label = string.IsNullOrEmpty(secName) ? Translate("This pin") : $"\"{secName}\"";
                    string shownName = string.IsNullOrEmpty(secName) ? pin.Name : secName;
                    items.Add(new FixItem(Translate("{0}: show a different check…").Replace("{0}", label), () => PickLocation(fix, pin, sec)));
                    items.Add(new FixItem(Translate("{0} isn't a check").Replace("{0}", label), () =>
                        fix.Apply(Translate("\"{0}\" isn't counted as a check any more.").Replace("{0}", shownName), () => PackFixActions.NotACheck(fix.Original, pin.FullPath, secName ?? ""))));
                }
            items.Add(new FixItem(Translate("Remove this pin from the map"), () =>
                fix.Apply(Translate("Removed {0} from the map.").Replace("{0}", pin.Name), () => PackFixActions.RemovePin(fix.Original, pin.FullPath, pin.Name, mapId, added))));
            items.Add(PackFixMenus.Separator);
            items.Add(new FixItem(Translate("Something else… (open the Pack Doctor at this pin)"), () => fix.OpenDoctor?.Invoke("Maps", pin.FullPath)));
            items.Add(new FixItem(Translate("Report this to the pack's author…"), () => fix.OpenDoctor?.Invoke("Report", null)));
            return items;
        }

        /// <summary>A "Not on the map" row's menu: place it on this map.</summary>
        public List<FixItem> UnplacedItems(long id, string name)
        {
            if (Fixing == null || string.IsNullOrEmpty(_currentMapId)) return new List<FixItem> { new FixItem(Translate("No map pack to fix"), null) };
            return new List<FixItem>
            {
                new FixItem(name, null),
                new FixItem(Translate("Place it on this map…"), () => StartPlacing(id, name)),
            };
        }

        private void UseMapPicture(PackFixContext fix, string mapId, string shown, string path) =>
            fix.Apply(Translate("{0} shows the pack's {1}.").Replace("{0}", shown).Replace("{1}", path), () => PackFixActions.SetMapImage(fix.Original, mapId, PackFixActions.ImportPackImage(fix.Original, path)));

        /// <summary>Asks which of the seed's checks no pin shows (the likeliest by the hint's name first).</summary>
        private void PickUnplaced(PackFixContext fix, Func<string, string> title, Action<long, string> chosen)
        {
            var names = fix.Index.LocationNames;
            var unplaced = fix.Index.UnplacedLocations().Where(names.ContainsKey).ToDictionary(id => id, id => names[id]);
            NamePickerDialog.Open(this, Translate("Which check is missing from the map?"), null, unplaced.Count > 0 ? unplaced : names, new List<Suggestion>(), chosen);
        }

        /// <summary>Asks which location a section (or a section-less pin) shows: the Doctor's suggestions for it first.</summary>
        private void PickLocation(PackFixContext fix, PopTrackerLocation pin, PopTrackerSection? section)
        {
            var names = fix.Index.LocationNames;
            var unplaced = fix.Index.UnplacedLocations().Where(names.ContainsKey).ToDictionary(id => id, id => names[id]);
            var context = PackDoctor.SuggestContext.For(fix.Index, names, learnFromMapping: true);
            var suggestions = PackDoctor.SuggestFor(pin, section, unplaced.Count > 0 ? unplaced : names, 8, context);
            string? secName = section?.Name;
            string query = string.IsNullOrEmpty(secName) ? pin.Name : $"{pin.Name} / {secName}";
            NamePickerDialog.Open(this, Translate("Which check is this?"), query, names, suggestions, (id, name) =>
                fix.Apply(Translate("\"{0}\" now shows {1}.").Replace("{0}", string.IsNullOrEmpty(secName) ? pin.Name : secName).Replace("{1}", name),
                    () => PackFixActions.LinkSection(fix.Original, pin.FullPath, secName ?? "", id, name)));
        }

        private void StartMoving(PopTrackerLocation pin, bool added)
        {
            var fix = Fixing;
            if (fix == null) return;
            string mapId = _currentMapId;
            StartPlace(Translate("Click where {0} goes").Replace("{0}", pin.Name), at =>
                fix.Apply(Translate("Moved {0}.").Replace("{0}", pin.Name), () => PackFixActions.MovePin(fix.Original, pin.FullPath, pin.Name, mapId, at.X, at.Y, added)));
        }

        private void StartPlacing(long id, string name)
        {
            var fix = Fixing;
            if (fix == null) return;
            string mapId = _currentMapId;
            StartPlace(Translate("Click where {0} goes").Replace("{0}", name), at =>
                fix.Apply(Translate("Placed {0}.").Replace("{0}", name), () => PackFixActions.PlacePin(fix.Original, id, name, mapId, at.X, at.Y)));
        }

        /// <summary>The next left click on the map puts the pin there; the bar says so, with Cancel.</summary>
        private void StartPlace(string what, Action<Vector2> put)
        {
            _placing = (what, put);
            if (_placeBar != null && _placeNote != null)
            {
                _placeNote.Text = what + " " + Translate("(Escape cancels)");
                _placeBar.Visible = true;
            }
        }

        /// <summary>Stops a move or a placement without a fix.</summary>
        public void CancelPlacing()
        {
            _placing = null;
            _pickingFor = null;
            if (_placeBar != null) _placeBar.Visible = false;
        }

        /// <summary>A left click on the map while placing: the pin goes there (for tests too, in map pixels).</summary>
        public void PlaceAt(Vector2 at)
        {
            if (_placing is not { } placing) return;
            CancelPlacing();
            placing.Put(at);
        }

        /// <summary>The bar that says what the next click does (built with the map's notes).</summary>
        private Control BuildPlaceBar()
        {
            _placeBar = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkEnd, MouseFilter = MouseFilterEnum.Pass, Visible = false };
            _placeBar.SetMeta("place_bar", true);
            _placeBar.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = ThemeColors.SurfaceSunken,
                BorderColor = ThemeColors.Accent,
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft = 10,
                ContentMarginRight = 10,
                ContentMarginTop = 6,
                ContentMarginBottom = 6
            });
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            row.AddThemeConstantOverride("separation", 8);
            _placeNote = new Label { MouseFilter = MouseFilterEnum.Ignore };
            _placeNote.AddThemeColorOverride("font_color", ThemeColors.Text);
            row.AddChild(_placeNote);
            row.AddChild(Kit.Button(Translate("Cancel"), Translate("Leaves the pin where it is"), CancelPlacing, small: true));
            _placeBar.AddChild(row);
            return _placeBar;
        }

        public override void _UnhandledKeyInput(InputEvent @event)
        {
            if (_placing != null && @event is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                CancelPlacing();
                GetViewport().SetInputAsHandled();
            }
        }
    }
}
