#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Godot;
using Color = Godot.Color;

namespace AP_Atlas.UI
{
    public partial class PackDoctorWindow
    {
        private string _locationFilter = "";

        // =====================================================================
        // Locations: unlinked sections, partial matches, locations with no pin
        // =====================================================================

        private Control BuildLocationsTab()
        {
            var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 8);
            scroll.AddChild(box);

            var search = new LineEdit { PlaceholderText = "Filter by pin, section or location name…", Text = _locationFilter, ClearButtonEnabled = true };
            search.TextSubmitted += t => { _locationFilter = t; RenderCurrentTab(); };
            search.TextChanged += t => { if (t.Length == 0 && _locationFilter.Length > 0) { _locationFilter = ""; RenderCurrentTab(); } };
            box.AddChild(search);
            bool Pass(params string[] texts) => _locationFilter.Length == 0 || texts.Any(t => t != null && t.IndexOf(_locationFilter, StringComparison.OrdinalIgnoreCase) >= 0);

            // ---- Unlinked sections ----
            var unlinked = _report.Findings.Where(f => f.Key.StartsWith("loc:unmatched:") && !f.Ignored).ToList();
            box.AddChild(Kit.Heading($"Pin sections not linked to a location ({unlinked.Count})"));
            box.AddChild(Kit.Subtle("These pins stay red and never clear. Accept the suggested match, choose another, or ignore ones the game doesn't have."));
            var strong = unlinked.Where(f => f.Suggestions.Count > 0 && f.Suggestions[0].Score >= 0.85).ToList();
            if (strong.Count > 0)
            {
                box.AddChild(Kit.Button($"Accept all {strong.Count} strong suggestions (85%+)", "Link each to its best match in one step (undoable)", () =>
                {
                    PackFixes.Edit(_key, $"Accept {strong.Count} suggestions", file =>
                    {
                        foreach (var f in strong)
                        {
                            var parts = f.Subject.Substring(5).Split('|');
                            var s = f.Suggestions[0];
                            file.Links.RemoveAll(x => x.Subject == f.Subject);
                            file.Links.Add(new LocationLinkFix
                            {
                                Subject = f.Subject,
                                PinPath = parts[0],
                                SectionName = parts.Length > 1 ? parts[1] : "",
                                ApLocationId = s.Id,
                                ApLocationName = s.Label,
                                AuthorStamp = PackFixes.AuthorStamp(_original, f.Subject)
                            });
                        }
                    });
                    SetStatus($"Linked {strong.Count} sections.");
                }));
            }
            foreach (var f in unlinked.Take(300))
            {
                var parts = f.Subject.Substring(5).Split('|');
                string pinPath = parts[0], section = parts.Length > 1 ? parts[1] : "";
                var best = f.Suggestions.FirstOrDefault();
                if (!Pass(pinPath, section, best?.Label)) continue;
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                var label = new Label { Text = $"{pinPath} / {(string.IsNullOrEmpty(section) ? "(pin)" : section)}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                row.AddChild(label);
                if (best != null)
                {
                    var match = new Label { Text = $"→ {best.Label} ({best.Score:P0})", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                    match.AddThemeColorOverride("font_color", best.Score >= 0.85 ? Good : best.Score >= 0.6 ? Warn : Muted);
                    row.AddChild(match);
                    row.AddChild(Kit.Button("Accept", "Link to the suggested location", () => LinkSection(pinPath, section, best.Id, best.Label)));
                }
                else row.AddChild(Kit.Text("no close match", Muted));
                var fc = f;
                row.AddChild(Kit.Button("Choose…", "Pick the location", () => PickSectionLocation(pinPath, section, fc.Suggestions)));
                row.AddChild(Kit.Button("Map", "Show this pin in the Maps tab", () => { _focusPinPath = pinPath; SelectTab("Maps"); }));
                row.AddChild(Kit.Button("Ignore", "Hide this finding", () => ToggleIgnore(fc)));
                box.AddChild(row);
            }
            if (unlinked.Count > 300) box.AddChild(Kit.Subtle($"…and {unlinked.Count - 300} more (use the filter)."));

            // ---- Partial-name matches ----
            var loose = _report.Index.ByLocation
                .SelectMany(kv => kv.Value.Where(m => m.Source == MatchSource.LooseName).Select(m => (Id: kv.Key, Match: m)))
                .ToList();
            box.AddChild(new HSeparator());
            box.AddChild(Kit.Heading($"Matched by partial name ({loose.Count})"));
            box.AddChild(Kit.Subtle("Matched on the part of the Archipelago name before \" - \". Usually right; change any that aren't."));
            foreach (var (id, m) in loose.Take(300))
            {
                string section = m.Section?.Name ?? "";
                string apName = _report.Index.LocationName(id);
                if (!Pass(m.Pin.FullPath, section, apName)) continue;
                var row = new HBoxContainer();
                row.AddChild(new Label { Text = $"{m.Pin.FullPath} / {(string.IsNullOrEmpty(section) ? "(pin)" : section)}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart });
                row.AddChild(new Label { Text = "→ " + apName, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart });
                string pp = m.Pin.FullPath;
                row.AddChild(Kit.Button("Change…", "Pick a different location", () => PickSectionLocation(pp, section, null)));
                row.AddChild(Kit.Button("Unlink", "This section shows no location", () => LinkSection(pp, section, null, null)));
                box.AddChild(row);
            }

            // ---- Locations with no pin ----
            var unplaced = _report.Index.UnplacedLocations().Select(id => (Id: id, Name: _report.Index.LocationName(id))).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            box.AddChild(new HSeparator());
            box.AddChild(Kit.Heading($"Game locations with no pin ({unplaced.Count})"));
            box.AddChild(Kit.Subtle("Some may be turned off by your game's options. Place a pin for any of them on a map."));
            int shown = 0;
            foreach (var (id, name) in unplaced)
            {
                if (!Pass(name)) continue;
                if (++shown > 300) { box.AddChild(Kit.Subtle("…more (use the filter).")); break; }
                var row = new HBoxContainer();
                row.AddChild(new Label { Text = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart });
                long lid = id; string lname = name;
                row.AddChild(Kit.Button("Place on map…", "Click a spot on a map to put a pin there", () => { _placeLocation = (lid, lname); SelectTab("Maps"); }));
                row.AddChild(Kit.Button("Add to a pin…", "Show it in an existing pin as an extra section", () => PickPinForLocation(lid, lname)));
                box.AddChild(row);
            }
            return scroll;
        }

        /// <summary>Links an unplaced location into an existing pin (as an added section on that pin).</summary>
        private void PickPinForLocation(long id, string name)
        {
            var pins = _report.Pack.Locations
                .Select((p, i) => (Key: (long)i, Name: p.FullPath))
                .ToDictionary(x => x.Key, x => x.Name);
            var suggestions = PackDoctor.Suggest(name, pins, 8);
            NamePickerDialog.Open(this, "Which pin should show it?", name, pins, suggestions, (pinIndex, pinPath) =>
            {
                var pin = _report.Pack.Locations[(int)pinIndex];
                var mapId = MapTrackerControl.MapsOf(pin).FirstOrDefault() ?? "";
                var (x, y) = PinPosition(pin, mapId);
                PackFixes.Edit(_key, $"Add {name} to pin {pin.Name}", f =>
                {
                    // A one-section added pin at the same spot reads as part of that pin.
                    f.AddedPins.Add(new AddedPin { Subject = "added:pin:" + id, Name = name, MapId = mapId, X = x + 6, Y = y + 6, ApLocationIds = new List<long> { id } });
                });
            });
        }

        private static (float X, float Y) PinPosition(PopTrackerLocation pin, string mapId)
        {
            if (string.Equals(pin.MapRef, mapId, StringComparison.OrdinalIgnoreCase)) return (pin.X, pin.Y);
            var ml = pin.MapLocations?.FirstOrDefault(m => string.Equals(m.Map, mapId, StringComparison.OrdinalIgnoreCase));
            return ml != null ? (ml.X, ml.Y) : (pin.X, pin.Y);
        }

        // =====================================================================
        // Maps: pin editor
        // =====================================================================

        private string _focusPinPath;
        private string _editorMapId;
        /// <summary>The editor's zoom; 0 until the map was fitted once.</summary>
        private float _editorZoom = 0;
        private (long Id, string Name)? _placeLocation;
        private string _selectedPinPath;

        private Control BuildMapsTab()
        {
            var pack = _report.Pack;
            var root = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            root.AddThemeConstantOverride("separation", 6);
            if (pack.Maps.Count == 0)
            {
                root.AddChild(Kit.Subtle("This pack has no maps."));
                return root;
            }

            // Pick the map: the focused pin's, else the last one, else the first.
            if (_focusPinPath != null)
            {
                var focusPin = pack.Locations.FirstOrDefault(l => l.FullPath == _focusPinPath);
                if (focusPin != null)
                {
                    string focusMap = MapTrackerControl.MapsOf(focusPin).FirstOrDefault() ?? _editorMapId;
                    if (focusMap != _editorMapId) { _editorZoom = 0; _mapScrollPos = Vector2.Zero; }
                    _editorMapId = focusMap;
                    _selectedPinPath = _focusPinPath;
                    _centerOnSelected = true;
                }
                _focusPinPath = null;
            }
            if (_editorMapId == null || !pack.Maps.ContainsKey(_editorMapId)) _editorMapId = pack.Maps.Values.OrderBy(m => m.Name).First().Id;
            var map = pack.Maps[_editorMapId];

            // ---- Toolbar ----
            var bar = new HFlowContainer();
            bar.AddThemeConstantOverride("h_separation", 6);
            var picker = new OptionButton();
            var ordered = pack.Maps.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                picker.AddItem(ordered[i].Name + (ordered[i].Background == null ? "  (no image)" : ""), i);
                if (ordered[i].Id == _editorMapId) picker.Selected = i;
            }
            picker.ItemSelected += i => { _editorMapId = ordered[(int)i].Id; _editorZoom = 0; _mapScrollPos = Vector2.Zero; _selectedPinPath = null; RenderCurrentTab(); };
            bar.AddChild(picker);
            bar.AddChild(Kit.Button("−", "Zoom out (or mouse wheel)", () => _mapCanvas?.ZoomAtCenter(1 / 1.25f)));
            bar.AddChild(Kit.Button("+", "Zoom in (or mouse wheel)", () => _mapCanvas?.ZoomAtCenter(1.25f)));
            bar.AddChild(Kit.Button("Fit", "Fit the whole map in view", () => _mapCanvas?.FitToView()));
            bar.AddChild(Kit.Button("1:1", "Actual size", () => _mapCanvas?.SetZoom(1f)));
            if (_selectedPinPath != null)
                bar.AddChild(Kit.Button("Go to selected", "Center on the selected pin", () => FocusPin(_selectedPinPath)));
            var find = new LineEdit { PlaceholderText = "Find a pin on this map…", CustomMinimumSize = new Vector2(260, 0), ClearButtonEnabled = true };
            find.TextSubmitted += text =>
            {
                text = text.Trim();
                if (text.Length == 0) return;
                var hit = PinsOn(pack, _editorMapId).Select(p => p.Pin)
                    .FirstOrDefault(p => p.FullPath.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         (p.Sections?.Any(s => (s.Name ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) ?? false));
                if (hit == null) { SetStatus($"No pin on this map matches \"{text}\"."); return; }
                FocusPin(hit.FullPath);
            };
            bar.AddChild(find);
            bar.AddChild(Kit.Button("Add pin…", "Pick a location, then click the map where its pin goes", () =>
                NamePickerDialog.Open(this, "Place a pin for which location?", null, LocationNames, new List<Suggestion>(),
                    (id, name) => { _placeLocation = (id, name); RenderCurrentTab(); })));
            bar.AddChild(Kit.Button("Replace background…", "Use an image file for this map", () => ReplaceMapImage(_editorMapId)));
            if (!string.IsNullOrEmpty(_original.Maps.TryGetValue(_editorMapId, out var om) ? om.MapBg : null))
                bar.AddChild(Kit.Button("Save original image…", "Save a copy of the pack's image where you choose (e.g. to re-save it as PNG)", () => SavePackImage(om.MapBg)));
            var mapFix = PackFixes.Get(_key).MapImages.FirstOrDefault(m => m.MapId == _editorMapId);
            if (mapFix != null) bar.AddChild(Kit.Button("Reset background", "Use the pack's image again", () => PackFixes.Reset(_key, "maps", mapFix.Subject)));
            root.AddChild(bar);

            if (_placeLocation != null)
            {
                var banner = new HBoxContainer();
                var msg = Kit.Text($"Click on the map to place a pin for: {_placeLocation.Value.Name}", Fixed);
                banner.AddChild(msg);
                banner.AddChild(Kit.Button("Cancel", "Stop placing", () => { _placeLocation = null; RenderCurrentTab(); }));
                root.AddChild(banner);
            }
            else root.AddChild(Kit.Subtle("Drag pins to move them. Click a pin to see and change its links. Outline: green = all linked · orange = some · red = none · gold = added by you."));

            var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            root.AddChild(split);

            var canvas = new MapCanvas { SizeFlagsStretchRatio = 2.2f };
            split.AddChild(canvas);
            var side = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            side.AddThemeConstantOverride("separation", 6);
            var sideScroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            sideScroll.AddChild(side);
            split.AddChild(sideScroll);

            _mapCanvas = canvas;
            var background = map.Background;
            _mapImgSize = background?.GetSize() ?? new Vector2(1600, 1000);
            canvas.SetMap(background, _mapImgSize);
            // Remember where the user was, so selecting a pin (which redraws the tab) doesn't jump the view.
            canvas.ViewChanged += () =>
            {
                if (_mapCanvas != canvas) return;
                _editorZoom = canvas.Zoom;
                _mapScrollPos = canvas.ScrollPosition;
            };
            // Placement: a left click on the empty map places the pending pin.
            canvas.Clicked += at =>
            {
                if (_placeLocation == null) return;
                var (id, name) = _placeLocation.Value;
                PackFixes.Edit(_key, $"Place a pin for {name}", f =>
                {
                    f.AddedPins.RemoveAll(p => p.Subject == "added:pin:" + id);
                    f.AddedPins.Add(new AddedPin { Subject = "added:pin:" + id, Name = name, MapId = _editorMapId, X = at.X, Y = at.Y, ApLocationIds = new List<long> { id } });
                });
                _selectedPinPath = "atlas/" + name;
                _placeLocation = null;
                SetStatus($"Placed a pin for {name}.");
            };

            var fixes = PackFixes.Get(_key);
            var added = new HashSet<string>(fixes.AddedPins.Select(p => p.PinPath));
            var moved = new HashSet<string>(fixes.Pins.Where(p => !p.Removed && p.MapId == _editorMapId).Select(p => p.PinPath));
            (PopTrackerLocation Pin, float X, float Y) selected = default;
            var pins = new List<MapCanvas.Pin>();
            foreach (var (pin, x, y) in PinsOn(pack, _editorMapId))
            {
                pins.Add(new MapCanvas.Pin { Key = pin.FullPath + "@" + x + "," + y, X = x, Y = y, Control = PinButton(pin, added.Contains(pin.FullPath), moved.Contains(pin.FullPath)) });
                if (pin.FullPath == _selectedPinPath) selected = (pin, x, y);
            }
            canvas.SetPins(pins);

            // A pulsing ring makes the selected pin easy to spot at any zoom.
            if (selected.Pin != null)
            {
                // Yellow ring with a dark outline (shadow) so it reads on light and dark maps alike.
                var ring = new Panel { Size = new Vector2(MapCanvas.FixedPinSize * 3.2f, MapCanvas.FixedPinSize * 3.2f) };
                ring.AddThemeStyleboxOverride("panel", new StyleBoxFlat
                {
                    BgColor = new Color(1f, 0.85f, 0.1f, 0.18f),
                    BorderColor = new Color("#FFD43B"),
                    ShadowColor = new Color(0, 0, 0, 0.85f),
                    ShadowSize = 4,
                    BorderWidthTop = 6,
                    BorderWidthBottom = 6,
                    BorderWidthLeft = 6,
                    BorderWidthRight = 6,
                    CornerRadiusTopLeft = 99,
                    CornerRadiusTopRight = 99,
                    CornerRadiusBottomLeft = 99,
                    CornerRadiusBottomRight = 99
                });
                ring.PivotOffset = ring.Size / 2;
                canvas.SetRing(new Vector2(selected.X, selected.Y), ring);
                var tween = ring.CreateTween().SetLoops();
                tween.TweenProperty(ring, "scale", new Vector2(1.35f, 1.35f), 0.6f).SetTrans(Tween.TransitionType.Sine);
                tween.TweenProperty(ring, "scale", Vector2.One, 0.6f).SetTrans(Tween.TransitionType.Sine);
            }

            BuildPinInspector(side, pack);

            // Once the view has its real size: fit if it never was, else the last view; centred on the selected pin when asked.
            bool center = _centerOnSelected && selected.Pin != null;
            _centerOnSelected = false;
            float zoom = _editorZoom;
            var scrollPos = _mapScrollPos;
            Ui.NextFrame(this, () =>
            {
                if (_mapCanvas != canvas || !IsInstanceValid(canvas)) return;
                if (zoom <= 0) canvas.FitToView();
                else canvas.SetView((scrollPos + canvas.ViewSize / 2) / zoom, zoom);
                if (center) canvas.CenterOn(new Vector2(selected.X, selected.Y), 1f);
            });
            return root;
        }

        // ---- Map view state (the canvas zooms and pans in place; the tab's rebuilds restore it) ----

        private MapCanvas _mapCanvas;
        private Vector2 _mapImgSize;
        private Vector2 _mapScrollPos;
        private bool _centerOnSelected;

        /// <summary>Selects a pin and centers the view on it, zoomed in enough to see it.</summary>
        private void FocusPin(string pinPath)
        {
            _selectedPinPath = pinPath;
            _centerOnSelected = true;
            RenderCurrentTab();
        }

        private static IEnumerable<(PopTrackerLocation Pin, float X, float Y)> PinsOn(LoadedPack pack, string mapId)
        {
            foreach (var pin in pack.Locations)
            {
                if (string.Equals(pin.MapRef, mapId, StringComparison.OrdinalIgnoreCase)) yield return (pin, pin.X, pin.Y);
                foreach (var ml in pin.MapLocations ?? new List<PopTrackerMapLocation>())
                    if (string.Equals(ml.Map, mapId, StringComparison.OrdinalIgnoreCase)) yield return (pin, ml.X, ml.Y);
            }
        }

        private Button PinButton(PopTrackerLocation pin, bool isAdded, bool isMoved)
        {
            const float size = MapCanvas.FixedPinSize;
            int sections = pin.Sections != null && pin.Sections.Count > 0 ? pin.Sections.Count : 1;
            int linked = _report.Index.IdsFor(pin).Count;
            Color fill = isAdded ? Fixed : linked == 0 ? Bad : linked >= sections ? Good : Warn;
            bool selected = pin.FullPath == _selectedPinPath;
            var b = new Button
            {
                CustomMinimumSize = new Vector2(size, size),
                Size = new Vector2(size, size),
                TooltipText = $"{pin.FullPath}\n{linked} of {sections} linked" + (isMoved ? "\n(moved by you)" : ""),
                FocusMode = Control.FocusModeEnum.None
            };
            var style = new StyleBoxFlat
            {
                BgColor = fill,
                BorderColor = selected ? Colors.White : isMoved ? Fixed : Colors.Black,
                BorderWidthTop = selected ? 3 : 2,
                BorderWidthBottom = selected ? 3 : 2,
                BorderWidthLeft = selected ? 3 : 2,
                BorderWidthRight = selected ? 3 : 2,
                CornerRadiusTopLeft = (int)size,
                CornerRadiusTopRight = (int)size,
                CornerRadiusBottomLeft = (int)size,
                CornerRadiusBottomRight = (int)size
            };
            foreach (var s in new[] { "normal", "hover", "pressed", "focus" }) b.AddThemeStyleboxOverride(s, style);

            // Drag to move; a click without movement selects.
            bool dragging = false;
            Vector2 grabOffset = Vector2.Zero, startPos = Vector2.Zero;
            b.GuiInput += ev =>
            {
                // Wheel and right/middle drag over a pin act on the map, as they do elsewhere on it.
                if (_mapCanvas != null && _mapCanvas.HandleWheel(ev, b))
                {
                    b.AcceptEvent();
                    return;
                }
                if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Pressed) { dragging = true; startPos = b.Position; grabOffset = mb.Position; b.AcceptEvent(); }
                    else if (dragging)
                    {
                        dragging = false;
                        b.AcceptEvent();
                        if ((b.Position - startPos).Length() < 3)
                        {
                            _selectedPinPath = pin.FullPath;
                            RenderCurrentTab();
                            return;
                        }
                        var center = _mapCanvas.ToMap(b.Position + new Vector2(size / 2, size / 2));
                        MovePin(pin, center.X, center.Y, isAdded);
                    }
                }
                else if (ev is InputEventMouseMotion mm && dragging)
                {
                    b.Position += mm.Position - grabOffset;
                    b.AcceptEvent();
                }
            };
            return b;
        }

        private void MovePin(PopTrackerLocation pin, float x, float y, bool isAdded)
        {
            _selectedPinPath = pin.FullPath;
            if (isAdded)
            {
                PackFixes.Edit(_key, $"Move added pin {pin.Name}", f =>
                {
                    var ap = f.AddedPins.FirstOrDefault(p => p.PinPath == pin.FullPath);
                    if (ap != null) { ap.X = x; ap.Y = y; ap.MapId = _editorMapId; ap.Made = DateTime.Now; }
                });
                return;
            }
            string subject = $"pin:{pin.FullPath}@{_editorMapId}";
            PackFixes.Edit(_key, $"Move pin {pin.Name}", f =>
            {
                f.Pins.RemoveAll(p => p.Subject == subject);
                f.Pins.Add(new PinFix { Subject = subject, PinPath = pin.FullPath, MapId = _editorMapId, X = x, Y = y, AuthorStamp = PackFixes.AuthorStamp(_original, subject) });
            });
            SetStatus($"Moved {pin.Name}.");
        }

        private void BuildPinInspector(VBoxContainer box, LoadedPack pack)
        {
            var fixes = PackFixes.Get(_key);
            BuildRemovedPinsList(box, fixes);
            var pin = _selectedPinPath == null ? null : pack.Locations.FirstOrDefault(l => l.FullPath == _selectedPinPath);
            if (pin == null)
            {
                box.AddChild(Kit.Heading("Pin"));
                box.AddChild(Kit.Subtle("Click a pin to see which Archipelago locations it shows and change them."));
                return;
            }
            bool isAdded = fixes.AddedPins.Any(p => p.PinPath == pin.FullPath);
            box.AddChild(Kit.Heading(pin.Name));
            box.AddChild(Kit.Subtle(pin.FullPath));

            var sections = pin.Sections != null && pin.Sections.Count > 0 ? pin.Sections.Select(s => (PopTrackerSection)s).ToList() : new List<PopTrackerSection> { null };
            foreach (var sec in sections)
            {
                string secName = sec?.Name ?? "";
                var ids = _report.Index.IdsFor(pin).Where(id => _report.Index.ByLocation.TryGetValue(id, out var ms) && ms.Any(m => m.Pin == pin && m.Section == sec)).ToList();
                var match = ids.Select(id => _report.Index.ByLocation[id].First(m => m.Pin == pin && m.Section == sec)).FirstOrDefault();
                string shown = ids.Count == 0 ? "not linked" : string.Join(", ", ids.Select(id => _report.Index.LocationName(id)));
                string src = match == null ? "" : match.Source switch
                {
                    MatchSource.MappingScript => "script",
                    MatchSource.Name => "name",
                    MatchSource.LooseName => "partial name",
                    _ => "your fix"
                };
                var row = new VBoxContainer();
                row.AddChild(new Label { Text = secName.StartsWith("atlas:") ? "(added)" : string.IsNullOrEmpty(secName) ? "(the pin itself)" : secName, AutowrapMode = TextServer.AutowrapMode.WordSmart });
                var l = Kit.Text($"→ {shown}" + (src.Length > 0 ? $"  ({src})" : ""), ids.Count == 0 ? Bad : ThemeColors.TextMuted);
                row.AddChild(l);
                if (!secName.StartsWith("atlas:"))
                {
                    var btns = new HBoxContainer();
                    string pp = pin.FullPath;
                    btns.AddChild(Kit.Button("Change…", "Pick the location this section shows", () => PickSectionLocation(pp, secName, null)));
                    if (ids.Count > 0) btns.AddChild(Kit.Button("Unlink", "Show no location here", () => LinkSection(pp, secName, null, null)));
                    var linkFix = fixes.Links.FirstOrDefault(x => x.Subject == $"link:{pp}|{secName}");
                    if (linkFix != null) btns.AddChild(Kit.Button("Reset", "Use the author's link", () => PackFixes.Reset(_key, "links", linkFix.Subject)));
                    row.AddChild(btns);
                }
                box.AddChild(row);
            }

            box.AddChild(new HSeparator());
            if (isAdded)
            {
                var ap = fixes.AddedPins.First(p => p.PinPath == pin.FullPath);
                box.AddChild(Kit.Button("Delete this pin", "Remove the pin you added", () => PackFixes.Edit(_key, "Delete added pin", f => f.AddedPins.RemoveAll(p => p.Subject == ap.Subject))));
            }
            else
            {
                string subject = $"pin:{pin.FullPath}@{_editorMapId}";
                var pf = fixes.Pins.FirstOrDefault(p => p.Subject == subject);
                if (pf != null && !pf.Removed) box.AddChild(Kit.Button("Reset position", "Back to the author's position", () => PackFixes.Reset(_key, "pins", subject)));
                box.AddChild(Kit.Button("Remove this pin", "Hide it from the map (reset brings it back)", () =>
                    PackFixes.Edit(_key, $"Remove pin {pin.Name}", f =>
                    {
                        f.Pins.RemoveAll(p => p.Subject == subject);
                        f.Pins.Add(new PinFix { Subject = subject, PinPath = pin.FullPath, MapId = _editorMapId, Removed = true, AuthorStamp = PackFixes.AuthorStamp(_original, subject) });
                    })));
            }
        }

        private void BuildRemovedPinsList(VBoxContainer box, PackFixFile fixes)
        {
            var removed = fixes.Pins.Where(p => p.Removed && p.MapId == _editorMapId).ToList();
            if (removed.Count > 0)
            {
                box.AddChild(Kit.Heading("Removed pins on this map"));
                foreach (var r in removed)
                {
                    var row = new HBoxContainer();
                    row.AddChild(new Label { Text = r.PinPath, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart });
                    string subj = r.Subject;
                    row.AddChild(Kit.Button("Restore", "Show it again", () => PackFixes.Reset(_key, "pins", subj)));
                    box.AddChild(row);
                }
            }
        }
    }
}
