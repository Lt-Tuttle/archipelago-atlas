#nullable disable
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core.PopTracker;
namespace AP_Atlas.UI
{
    public partial class MapTrackerControl : MarginContainer
    {
        public Control SidebarContent { get; private set; }
        public event Action OnDataRefreshed;

        /// <summary>A pin was clicked: map id, pin (pack location) name, and the AP location ids it covers.</summary>
        public event Action<string, string, List<long>> PinPicked;

        /// <summary>A map was chosen in the list.</summary>
        public event Action<string> MapPicked;

        /// <summary>Flag color and special mark for an AP location id. Set by the owner.</summary>
        public Func<long, (int Flag, bool Special)> MarkerLookup { get; set; }

        /// <summary>Whether a location is excluded (by the seed's options or by the user). Set by the owner.</summary>
        public Func<long, bool> IsExcluded { get; set; }

        /// <summary>Raised when the map display options change, so every slot's map redraws with them.</summary>
        public static event Action DisplayOptionsChanged;

        private const int ModeShow = 0, ModeDim = 1, ModeHide = 2;
        private const float DimAlpha = 0.35f;
        private static readonly Color NotInSeedColor = new Color("#5A5F6E");

        private bool Excluded(long id) => IsExcluded?.Invoke(id) == true;

        /// <summary>The checks of a pin that count: excluded ones drop out unless the user shows them.</summary>
        private List<long> CountedIds(List<long> ids) =>
            _appSettings.MapExcludedMode == ModeShow || IsExcluded == null ? ids : ids.Where(id => !Excluded(id)).ToList();

        public LoadedPack Pack => _pack;

        private bool _logicHidden;

        /// <summary>Race mode: pins show open/hinted/checked only, never in or out of logic.</summary>
        public bool LogicHidden
        {
            get => _logicHidden;
            set
            {
                if (_logicHidden == value) return;
                _logicHidden = value;
                RefreshMapListCounters();
                if (!string.IsNullOrEmpty(_currentMapId)) RenderLocations();
            }
        }

        private static readonly Color OpenNeutral = Colors.SteelBlue;
        private static readonly Color HintedNeutral = Colors.MediumPurple;
        public string CurrentMapId => _currentMapId;

        /// <summary>Re-draws pins after flags or special marks changed.</summary>
        public void RefreshMarkers()
        {
            if (!string.IsNullOrEmpty(_currentMapId)) RenderLocations();
        }

        public bool HasLocation(long locationId) => _locIdToMap.ContainsKey(locationId);

        public void ShowMap(string mapId) => SwitchMap(mapId);

        /// <summary>Switches to the map holding a location, centers it, and pulses its pin. False if the pack doesn't place it.</summary>
        public bool RevealLocation(long locationId)
        {
            if (_pack == null || !_locIdToMap.TryGetValue(locationId, out var mapId)) return false;
            if (!string.Equals(mapId, _currentMapId, StringComparison.OrdinalIgnoreCase)) SwitchMap(mapId);
            foreach (var pin in PinsOnMap(mapId))
            {
                if (!GetLocationIds(pin.Loc).Contains(locationId)) continue;
                _camera.Position = new Vector2(pin.X, pin.Y);
                SaveCurrentCamera();
                RenderLocations(new HashSet<long> { locationId });
                return true;
            }
            return false;
        }

        /// <summary>Pack locations (pins) that cover an AP location id, with every map each appears on.</summary>
        public List<(PopTrackerLocation Pin, PopTrackerSection Section, List<string> Maps)> PinsFor(long locationId)
        {
            var result = new List<(PopTrackerLocation, PopTrackerSection, List<string>)>();
            if (_pack == null || _index == null || !_index.ByLocation.TryGetValue(locationId, out var matches)) return result;
            foreach (var m in matches) result.Add((m.Pin, m.Section, MapsOf(m.Pin)));
            return result;
        }

        public PopTrackerLocation FindPin(string mapId, string pinName)
        {
            if (_pack == null) return null;
            return _pack.Locations.FirstOrDefault(l => l.Name == pinName && MapsOf(l).Any(m => string.Equals(m, mapId, StringComparison.OrdinalIgnoreCase)))
                   ?? _pack.Locations.FirstOrDefault(l => l.Name == pinName);
        }

        public static List<string> MapsOf(PopTrackerLocation loc)
        {
            var maps = new List<string>();
            if (!string.IsNullOrEmpty(loc.MapRef)) maps.Add(loc.MapRef);
            if (loc.MapLocations != null)
            {
                foreach (var ml in loc.MapLocations)
                {
                    if (!string.IsNullOrEmpty(ml.Map) && !maps.Contains(ml.Map, StringComparer.OrdinalIgnoreCase)) maps.Add(ml.Map);
                }
            }
            return maps;
        }

        public List<(PopTrackerLocation Loc, float X, float Y)> PinsOnMap(string mapId)
        {
            var pins = new List<(PopTrackerLocation, float, float)>();
            if (_pack == null) return pins;
            foreach (var loc in _pack.Locations)
            {
                if (loc.MapRef.Equals(mapId, StringComparison.OrdinalIgnoreCase)) pins.Add((loc, loc.X, loc.Y));
                if (loc.MapLocations == null) continue;
                foreach (var ml in loc.MapLocations)
                {
                    if (ml.Map.Equals(mapId, StringComparison.OrdinalIgnoreCase)) pins.Add((loc, ml.X, ml.Y));
                }
            }
            return pins;
        }

        /// <summary>Status of one AP location as the map colors it.</summary>
        public string LocationState(long id)
        {
            if (_checkedLocs.Contains(id)) return "checked";
            string excluded = Excluded(id) ? ", excluded" : "";
            if (_logicHidden) return (_hintedLocs.Contains(id) ? "hinted" : "open") + excluded;
            bool r = _reachableLocs.Contains(id), h = _hintedLocs.Contains(id);
            return (h && r ? "hinted, in logic" : h ? "hinted, out of logic" : r ? "in logic" : "out of logic") + excluded;
        }

        private AppSettings _appSettings;
        private Archipelago.MultiClient.Net.ArchipelagoSession _session;
        private LoadedPack _pack;
        private string _currentMapId = "";

        private Dictionary<long, string> _locIdToMap = new Dictionary<long, string>();

        private IReadOnlySet<long> _reachableLocs = new HashSet<long>();
        private HashSet<long> _checkedLocs = new HashSet<long>();
        private IReadOnlySet<long> _hintedLocs = new HashSet<long>();

        private VBoxContainer _mapListContainer;
        private OptionButton _sortDropdown;
        private SubViewportContainer _viewportContainer;
        private SubViewport _viewport;
        private Sprite2D _mapBackground;
        private Control _nodesOverlay;
        private Camera2D _camera;
        private CenterContainer _emptyStateContainer;

        private bool _isDragging = false;
        private Vector2 _lastMousePos;

        public MapTrackerControl(AppSettings appSettings)
        {
            _appSettings = appSettings;
            if (_appSettings.MapCameras == null) _appSettings.MapCameras = new Dictionary<string, MapCameraSave>();
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            // --- BUILD SIDEBAR ---
            var leftPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            SidebarContent = leftPanel;
            var leftVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            leftPanel.AddChild(leftVBox);
            var sortMargin = new MarginContainer();
            sortMargin.AddThemeConstantOverride("margin_top", 10);
            sortMargin.AddThemeConstantOverride("margin_left", 10);
            sortMargin.AddThemeConstantOverride("margin_right", 10);
            leftVBox.AddChild(sortMargin);
            var sortHBox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            sortHBox.AddChild(new Label { Text = "Sort:", Modulate = Colors.DarkGray });
            _sortDropdown = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _sortDropdown.AddItem("A-Z");
            _sortDropdown.AddItem("Most Checks");
            _sortDropdown.Selected = _appSettings.MapSortIndex;
            _sortDropdown.ItemSelected += (idx) => { _appSettings.MapSortIndex = (int)idx; DataManager.SaveSettings(_appSettings); RefreshMapList(); };
            sortHBox.AddChild(_sortDropdown);
            var sortVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            sortVBox.AddChild(sortHBox);
            sortVBox.AddChild(BuildDisplayOptions());
            sortMargin.AddChild(sortVBox);
            var listScroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            leftVBox.AddChild(listScroll);
            var listMargin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            listScroll.AddChild(listMargin);
            _mapListContainer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            listMargin.AddChild(_mapListContainer);
            // --- BUILD CENTER CANVAS ---
            var rightPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            var rightStyle = new StyleBoxFlat { BgColor = new Color("#0A0A0F") };
            rightPanel.AddThemeStyleboxOverride("panel", rightStyle);
            AddChild(rightPanel);
            _viewportContainer = new SubViewportContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, Stretch = true };
            rightPanel.AddChild(_viewportContainer);
            _viewport = new SubViewport { Size = new Vector2I(800, 600) };
            _viewportContainer.AddChild(_viewport);
            _mapBackground = new Sprite2D { Centered = false };
            _viewport.AddChild(_mapBackground);
            _nodesOverlay = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            _viewport.AddChild(_nodesOverlay);
            _camera = new Camera2D { Zoom = new Vector2(1, 1), AnchorMode = Camera2D.AnchorModeEnum.DragCenter };
            _viewport.AddChild(_camera);
            _viewportContainer.GuiInput += OnViewportGuiInput;
            _viewportContainer.Resized += () =>
            {
                if (!string.IsNullOrEmpty(_currentMapId) && !_appSettings.MapCameras.ContainsKey(_currentMapId)) AutoFitCamera();
            };
            _emptyStateContainer = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            var emptyVBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            emptyVBox.AddThemeConstantOverride("separation", 20);
            _emptyStateContainer.AddChild(emptyVBox);
            var emptyLbl = new Label { Text = "No Map Pack Installed", HorizontalAlignment = HorizontalAlignment.Center };
            emptyLbl.AddThemeFontSizeOverride("font_size", 28);
            emptyVBox.AddChild(emptyLbl);
            var emptySubLbl = new Label { Text = "We could not find a PopTracker map pack for this game.\nGo to the Map Pack Manager in the main sidebar to download one.", HorizontalAlignment = Godot.HorizontalAlignment.Center };
            emptySubLbl.AddThemeColorOverride("font_color", Colors.Gray);
            emptyVBox.AddChild(emptySubLbl);
            AddChild(_emptyStateContainer);

        }

        public override void _EnterTree()
        {
            DisplayOptionsChanged += OnDisplayOptionsChanged;
            // Options changed while this map was out of the tree: catch up.
            if (_shownDisplayVersion != _displayVersion) OnDisplayOptionsChanged();
        }

        // --- Display options (shared by every slot's map) ---

        private Button _displayToggle;
        private HSlider _nodeSizeSlider;
        private Label _nodeSizeValue;
        private OptionButton _excludedModeDropdown;
        private OptionButton _notInSeedDropdown;
        private CheckBox _hideCheckedBox;
        private bool _syncingDisplay;
        private static int _displayVersion;
        private int _shownDisplayVersion;

        private Control BuildDisplayOptions()
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _displayToggle = new Button { Text = "▸ Display", Flat = true, Alignment = HorizontalAlignment.Left, TooltipText = "Node size and which pins the map shows" };
            box.AddChild(_displayToggle);
            var panel = new VBoxContainer { Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            panel.AddThemeConstantOverride("separation", 6);
            box.AddChild(panel);
            _displayToggle.Pressed += () =>
            {
                panel.Visible = !panel.Visible;
                _displayToggle.Text = (panel.Visible ? "▾" : "▸") + " Display";
            };

            var sizeRow = new HBoxContainer();
            sizeRow.AddChild(new Label { Text = "Node size", Modulate = Colors.DarkGray, CustomMinimumSize = new Vector2(100, 0) });
            _nodeSizeSlider = new HSlider { MinValue = 0.3, MaxValue = 3.0, Step = 0.05, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "Size of the map pins (double-click to reset)" };
            _nodeSizeValue = new Label { CustomMinimumSize = new Vector2(48, 0), HorizontalAlignment = HorizontalAlignment.Right };
            _nodeSizeSlider.ValueChanged += v =>
            {
                _nodeSizeValue.Text = $"{Math.Round(v * 100)}%";
                if (_syncingDisplay) return;
                _appSettings.MapNodeScale = (float)v;
                DisplayChanged();
            };
            _nodeSizeSlider.GuiInput += e =>
            {
                if (e is InputEventMouseButton { DoubleClick: true, ButtonIndex: MouseButton.Left }) _nodeSizeSlider.Value = 1.0;
            };
            sizeRow.AddChild(_nodeSizeSlider);
            sizeRow.AddChild(_nodeSizeValue);
            panel.AddChild(sizeRow);

            OptionButton ModeRow(string label, string tooltip, Action<int> set)
            {
                var row = new HBoxContainer { TooltipText = tooltip };
                row.AddChild(new Label { Text = label, Modulate = Colors.DarkGray, CustomMinimumSize = new Vector2(100, 0), TooltipText = tooltip, MouseFilter = MouseFilterEnum.Pass });
                var dd = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = tooltip };
                dd.AddItem("Show");
                dd.AddItem("Dim");
                dd.AddItem("Hide");
                dd.ItemSelected += idx =>
                {
                    if (_syncingDisplay) return;
                    set((int)idx);
                    DisplayChanged();
                };
                row.AddChild(dd);
                panel.AddChild(row);
                return dd;
            }
            _excludedModeDropdown = ModeRow("Excluded", "Checks excluded by your seed's options, or by you (Properties → Exclude).\nDim or Hide also leaves them out of the map counts.",
                m => _appSettings.MapExcludedMode = m);
            _notInSeedDropdown = ModeRow("Not in seed", "Pins whose checks don't exist in your seed (options turned them off), or that the pack couldn't match.\nThe Pack Doctor can fix pins that should match.",
                m => _appSettings.MapNotInSeedMode = m);

            _hideCheckedBox = new CheckBox { Text = "Hide checked pins", TooltipText = "Hide pins whose checks are all done" };
            _hideCheckedBox.Toggled += on =>
            {
                if (_syncingDisplay) return;
                _appSettings.MapHideChecked = on;
                DisplayChanged();
            };
            panel.AddChild(_hideCheckedBox);
            SyncDisplayControls();
            return box;
        }

        private void SyncDisplayControls()
        {
            _syncingDisplay = true;
            _nodeSizeSlider.Value = Math.Clamp(_appSettings.MapNodeScale, 0.3f, 3.0f);
            _nodeSizeValue.Text = $"{Math.Round(_nodeSizeSlider.Value * 100)}%";
            _excludedModeDropdown.Selected = Math.Clamp(_appSettings.MapExcludedMode, 0, 2);
            _notInSeedDropdown.Selected = Math.Clamp(_appSettings.MapNotInSeedMode, 0, 2);
            _hideCheckedBox.ButtonPressed = _appSettings.MapHideChecked;
            _syncingDisplay = false;
        }

        private void DisplayChanged()
        {
            // The slider fires continuously while dragged: save once it settles.
            DataManager.SaveSettingsSoon(_appSettings);
            _displayVersion++;
            DisplayOptionsChanged?.Invoke();
        }

        private void OnDisplayOptionsChanged()
        {
            if (!IsInstanceValid(this)) return;
            _shownDisplayVersion = _displayVersion;
            SyncDisplayControls();
            RefreshMapListCounters();
            if (!string.IsNullOrEmpty(_currentMapId)) RenderLocations();
        }

        public void SetSession(Archipelago.MultiClient.Net.ArchipelagoSession session)
        {
            _session = session;
            RefreshMapListCounters();
        }
        public void UpdateLogicColors(System.Collections.Generic.IReadOnlySet<long> reachableLocs, System.Collections.ObjectModel.ReadOnlyCollection<long> checkedLocs, System.Collections.Generic.IReadOnlySet<long> hintedLocs)
        {
            if (reachableLocs != null) _reachableLocs = reachableLocs;
            if (checkedLocs != null) _checkedLocs = new System.Collections.Generic.HashSet<long>(checkedLocs);
            if (hintedLocs != null) _hintedLocs = hintedLocs;
            (_colorsRefresh ??= new ViewRefresh(this, RedrawColors, "redrawing the map's colors")).Request();
        }

        private ViewRefresh _colorsRefresh;

        private void RedrawColors()
        {
            RefreshMapListCounters();
            if (!string.IsNullOrEmpty(_currentMapId)) RenderLocations();
        }
        private void BuildLocationMaps()
        {
            _locIdToMap.Clear();
            if (_pack == null || _index == null) return;
            foreach (var loc in _pack.Locations)
            {
                foreach (long id in _index.IdsFor(loc))
                {
                    if (!string.IsNullOrEmpty(loc.MapRef)) _locIdToMap[id] = loc.MapRef;
                    if (loc.MapLocations != null)
                    {
                        foreach (var ml in loc.MapLocations)
                        {
                            if (!string.IsNullOrEmpty(ml.Map)) _locIdToMap[id] = ml.Map;
                        }
                    }
                }
            }
        }

        private PackIndex _index;

        /// <summary>The pin ↔ location pairing in use (mapping scripts, names and the user's fixes).</summary>
        public PackIndex Index => _index;

        /// <summary>Loads a pack with its pairing to this slot's locations.</summary>
        public void LoadPack(LoadedPack pack, PackIndex index)
        {
            using var __perf = AP_Atlas.Core.PerfMonitor.Measure("Map Tracker: load pack");
            _pack = pack;
            _index = index;
            if (_emptyStateContainer != null) _emptyStateContainer.Visible = false;
            if (_viewportContainer != null) _viewportContainer.Visible = true;
            BuildLocationMaps();
            RefreshMapList();
            if (_pack.Maps.Count > 0 && (string.IsNullOrEmpty(_currentMapId) || !_pack.Maps.ContainsKey(_currentMapId)))
            {
                SwitchMap(_pack.Maps.Keys.First());
            }
            else if (!string.IsNullOrEmpty(_currentMapId))
            {
                RenderLocations();
            }
        }

        /// <summary>AP location ids a pin covers.</summary>
        public List<long> GetLocationIds(PopTrackerLocation loc) => _index?.IdsFor(loc) ?? new List<long>();
        private class MapStat
        {
            public int Reachable = 0;
            public int Inaccessible = 0;
            public int HintedReachable = 0;
            public int HintedInaccessible = 0;
            public int Checked = 0;
            public int TotalSort => Reachable + HintedReachable;
        }
        private void RefreshMapList()
        {
            var oldNodes = _mapListContainer.GetChildren();
            foreach (Node n in oldNodes)
            {
                _mapListContainer.RemoveChild(n);
                n.QueueFree();
            }
            if (_pack == null) return;
            var sortedMaps = _pack.Maps.Values.OrderBy(m => m.Name).ToList();
            foreach (var map in sortedMaps)
            {
                var btn = new Button { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                btn.SetMeta("map_id", map.Id);
                btn.SetMeta("map_name", map.Name);
                var margin = new MarginContainer { Name = "Margin", MouseFilter = MouseFilterEnum.Ignore };
                margin.AddThemeConstantOverride("margin_left", 10);
                margin.AddThemeConstantOverride("margin_right", 10);
                margin.AddThemeConstantOverride("margin_top", 10);
                margin.AddThemeConstantOverride("margin_bottom", 10);
                margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                var hbox = new HBoxContainer { Name = "HBox", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
                hbox.AddThemeConstantOverride("separation", 10);
                var lbl = new Label { Text = map.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = Godot.VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
                hbox.AddChild(lbl);
                var counterHBox = new HBoxContainer { Name = "Counters", SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
                counterHBox.AddThemeConstantOverride("separation", 4);
                hbox.AddChild(counterHBox);
                margin.AddChild(hbox);
                btn.AddChild(margin);
                // A Button only sizes to its own Text; keep its height in sync with the child layout.
                var rowBtn = btn;
                var rowMargin = margin;
                rowMargin.MinimumSizeChanged += () => rowBtn.CustomMinimumSize = new Vector2(0, rowMargin.GetCombinedMinimumSize().Y);
                rowBtn.CustomMinimumSize = new Vector2(0, rowMargin.GetCombinedMinimumSize().Y);
                _mapListContainer.AddChild(btn);
                string mapId = map.Id;
                btn.Pressed += () => { SwitchMap(mapId); MapPicked?.Invoke(mapId); };
            }
            _mapListContainer.AddThemeConstantOverride("separation", 4);
            RefreshMapListCounters();
            OnDataRefreshed?.Invoke();
        }
        private void RefreshMapListCounters()
        {
            using var __perf = AP_Atlas.Core.PerfMonitor.Measure("Map Tracker: map list counters");
            if (_pack == null || _session == null) return;
            var mapCounts = new Dictionary<string, MapStat>(StringComparer.OrdinalIgnoreCase);
            foreach (var map in _pack.Maps.Values) mapCounts[map.Id] = new MapStat();
            foreach (var loc in _pack.Locations)
            {
                var ids = CountedIds(GetLocationIds(loc));
                if (ids.Count == 0) continue;
                int reachableChecks = 0;
                int hintedReachableChecks = 0;
                int hintedInaccessibleChecks = 0;
                int inaccessibleChecks = 0;
                foreach (long id in ids)
                {
                    if (_checkedLocs.Contains(id)) continue;
                    bool isR = _reachableLocs.Contains(id);
                    bool isH = _hintedLocs.Contains(id);
                    if (isH && isR) hintedReachableChecks++;
                    else if (isH && !isR) hintedInaccessibleChecks++;
                    else if (isR) reachableChecks++;
                    else inaccessibleChecks++;
                }
                bool isAllChecked = (reachableChecks == 0 && hintedReachableChecks == 0 && hintedInaccessibleChecks == 0 && inaccessibleChecks == 0);
                var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(loc.MapRef)) refs.Add(loc.MapRef);
                if (loc.MapLocations != null)
                {
                    foreach (var ml in loc.MapLocations)
                    {
                        if (!string.IsNullOrEmpty(ml.Map)) refs.Add(ml.Map);
                    }
                }
                foreach (var mRef in refs)
                {
                    if (mapCounts.TryGetValue(mRef, out var stat))
                    {
                        if (isAllChecked) stat.Checked++;
                        else
                        {
                            stat.HintedReachable += hintedReachableChecks;
                            stat.HintedInaccessible += hintedInaccessibleChecks;
                            stat.Reachable += reachableChecks;
                            stat.Inaccessible += inaccessibleChecks;
                        }
                    }
                }
            }
            foreach (Node child in _mapListContainer.GetChildren())
            {
                if (child is Button btn && btn.HasMeta("map_id"))
                {
                    string mId = btn.GetMeta("map_id").AsString();
                    if (mapCounts.TryGetValue(mId, out var stat))
                    {
                        // With logic hidden, sort by all open checks so the order doesn't reveal logic.
                        btn.SetMeta("sort_count", _logicHidden
                            ? stat.Reachable + stat.Inaccessible + stat.HintedReachable + stat.HintedInaccessible
                            : stat.TotalSort);
                        var counterHBox = btn.GetNodeOrNull<HBoxContainer>("Margin/HBox/Counters");
                        if (counterHBox != null)
                        {
                            foreach (Node n in counterHBox.GetChildren()) { counterHBox.RemoveChild(n); n.QueueFree(); }
                            void AddPill(int count, Color bgColor, Color textColor)
                            {
                                if (count <= 0) return;
                                var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
                                var style = new StyleBoxFlat { BgColor = bgColor, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 2, ContentMarginBottom = 2 };
                                panel.AddThemeStyleboxOverride("panel", style);
                                var lbl = new Label { Text = count.ToString(), HorizontalAlignment = Godot.HorizontalAlignment.Center, VerticalAlignment = Godot.VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
                                lbl.AddThemeColorOverride("font_color", textColor);
                                panel.AddChild(lbl);
                                counterHBox.AddChild(panel);
                            }
                            if (_logicHidden)
                            {
                                AddPill(stat.Reachable + stat.Inaccessible, OpenNeutral, Colors.White);
                                AddPill(stat.HintedReachable + stat.HintedInaccessible, HintedNeutral, Colors.White);
                            }
                            else
                            {
                                AddPill(stat.Reachable, Colors.LimeGreen, Colors.Black);
                                AddPill(stat.HintedReachable, Colors.DeepSkyBlue, Colors.Black);
                                AddPill(stat.HintedInaccessible, Colors.Purple, Colors.White);
                                AddPill(stat.Inaccessible, Colors.Crimson, Colors.White);
                            }
                        }
                    }
                }
            }
            if (_sortDropdown != null)
            {
                var children = _mapListContainer.GetChildren().Cast<Button>().ToList();
                if (_sortDropdown.Selected == 1) // Most Checks
                {
                    children = children.OrderByDescending(b => b.GetMeta("sort_count").AsInt32()).ToList();
                }
                else // A-Z
                {
                    children = children.OrderBy(b => b.GetMeta("map_name").AsString(), StringComparer.OrdinalIgnoreCase).ToList();
                }
                for (int i = 0; i < children.Count; i++)
                {
                    _mapListContainer.MoveChild(children[i], i);
                }
            }
        }
        private void AutoFitCamera()
        {
            if (_pack == null || string.IsNullOrEmpty(_currentMapId) || !_pack.Maps.ContainsKey(_currentMapId)) return;
            var background = Background(_pack.Maps[_currentMapId]);
            if (background == null) return;
            _camera.Position = background.GetSize() / 2f;
            var mapSize = background.GetSize();
            var viewSize = _viewportContainer.Size;
            if (viewSize.X > 0 && viewSize.Y > 0 && (mapSize.X > viewSize.X || mapSize.Y > viewSize.Y))
            {
                float scaleX = viewSize.X / mapSize.X;
                float scaleY = viewSize.Y / mapSize.Y;
                float minScale = Math.Min(scaleX, scaleY) * 0.95f; // 5% padding
                _camera.Zoom = new Vector2(minScale, minScale);
            }
            else
            {
                _camera.Zoom = new Vector2(1, 1);
            }
            // Before the view is laid out the fit is only a placeholder; don't persist it, so the
            // Resized handler re-fits once the real size is known.
            if (viewSize.X > 0 && viewSize.Y > 0) SaveCurrentCamera();
        }
        private void SaveCurrentCamera()
        {
            if (string.IsNullOrEmpty(_currentMapId) || _appSettings == null) return;
            if (_appSettings.MapCameras == null) _appSettings.MapCameras = new Dictionary<string, MapCameraSave>();
            _appSettings.MapCameras[_currentMapId] = new MapCameraSave
            {
                X = _camera.Position.X,
                Y = _camera.Position.Y,
                Zoom = _camera.Zoom.X
            };
            // Camera moves fire on every mouse-motion event: save once they settle.
            DataManager.SaveSettingsSoon(_appSettings);
        }

        public override void _ExitTree()
        {
            DisplayOptionsChanged -= OnDisplayOptionsChanged;
        }
        /// <summary>A map's background, or null when it has none (or it was freed with its pack).</summary>
        private static ImageTexture Background(PopTrackerMap map) => map?.Background;

        private void SwitchMap(string mapId)
        {
            if (_pack == null || !_pack.Maps.ContainsKey(mapId)) return;
            if (!string.IsNullOrEmpty(_currentMapId))
            {
                SaveCurrentCamera();
            }
            _currentMapId = mapId;
            var background = Background(_pack.Maps[mapId]);
            if (background != null)
            {
                _mapBackground.Texture = background;
                if (_appSettings != null && _appSettings.MapCameras != null && _appSettings.MapCameras.TryGetValue(mapId, out var state))
                {
                    _camera.Position = new Vector2(state.X, state.Y);
                    _camera.Zoom = new Vector2(state.Zoom, state.Zoom);
                }
                else
                {
                    AutoFitCamera();
                }
            }
            else
            {
                _mapBackground.Texture = null;
            }
            RenderLocations();
        }
        private void RenderLocations(HashSet<long> newlyUnlocked = null)
        {
            using var __perf = AP_Atlas.Core.PerfMonitor.Measure("Map Tracker: draw markers");
            foreach (Node n in _nodesOverlay.GetChildren()) n.QueueFree();
            if (_pack == null || string.IsNullOrEmpty(_currentMapId)) return;
            var nodesToDraw = new List<(PopTrackerLocation Loc, float X, float Y)>();
            foreach (var loc in _pack.Locations)
            {
                if (loc.MapRef.Equals(_currentMapId, StringComparison.OrdinalIgnoreCase))
                {
                    nodesToDraw.Add((loc, loc.X, loc.Y));
                }
                if (loc.MapLocations != null)
                {
                    foreach (var ml in loc.MapLocations)
                    {
                        if (ml.Map.Equals(_currentMapId, StringComparison.OrdinalIgnoreCase))
                        {
                            nodesToDraw.Add((loc, ml.X, ml.Y));
                        }
                    }
                }
            }
            int excludedMode = _appSettings.MapExcludedMode, notInSeedMode = _appSettings.MapNotInSeedMode;
            foreach (var node in nodesToDraw)
            {
                var loc = node.Loc;
                var ids = GetLocationIds(loc);
                Color nodeColor = Colors.Crimson; // Red by default
                bool dim = false;
                string stateNote = "";
                if (ids.Count == 0)
                {
                    // No check of this pin exists in the seed (turned off by options, or the pack doesn't match).
                    if (notInSeedMode == ModeHide) continue;
                    if (notInSeedMode == ModeDim) { nodeColor = NotInSeedColor; dim = true; }
                    stateNote = "\n(Not in your seed, or not matched to it)";
                }
                else
                {
                    var open = ids.Where(id => !_checkedLocs.Contains(id)).ToList();
                    int excludedOpen = open.Count(Excluded);
                    // Excluded checks don't color the pin unless they're shown like any other check.
                    var counted = excludedMode == ModeShow ? open : open.Where(id => !Excluded(id)).ToList();
                    if (open.Count == 0)
                    {
                        if (_appSettings.MapHideChecked) continue;
                        nodeColor = Colors.DimGray;
                    }
                    else if (counted.Count == 0)
                    {
                        // Everything left here is excluded.
                        if (excludedMode == ModeHide) continue;
                        dim = true;
                        nodeColor = Colors.DimGray;
                        stateNote = excludedOpen == 1 ? "\nExcluded" : $"\nExcluded ({excludedOpen} checks)";
                    }
                    else
                    {
                        bool anyReachable = counted.Any(_reachableLocs.Contains);
                        bool anyHinted = counted.Any(_hintedLocs.Contains);
                        if (_logicHidden) nodeColor = anyHinted ? HintedNeutral : OpenNeutral;
                        else if (anyHinted && anyReachable) nodeColor = Colors.DeepSkyBlue;
                        else if (anyHinted && !anyReachable) nodeColor = Colors.Purple;
                        else if (anyReachable) nodeColor = Colors.LimeGreen;
                        if (excludedOpen > 0) stateNote = $"\n{excludedOpen} excluded";
                    }
                }
                float size = 24f;
                if (_pack.Maps.TryGetValue(_currentMapId, out var currentMap) && currentMap.LocationSize > 0)
                {
                    size = currentMap.LocationSize;
                }
                else if (Background(currentMap) is { } background)
                {
                    size = Math.Max(16f, Math.Min(background.GetWidth(), background.GetHeight()) * 0.015f);
                }
                size = size * _appSettings.MapNodeScale;
                var btn = new Button
                {
                    Position = new Vector2(node.X - (size / 2), node.Y - (size / 2)),
                    CustomMinimumSize = new Vector2(size, size),
                    TooltipText = loc.Name + stateNote,
                    MouseDefaultCursorShape = CursorShape.PointingHand
                };
                // Dimmed pins stay clickable, so an excluded check can be included again from Properties.
                if (dim) btn.Modulate = new Color(1f, 1f, 1f, DimAlpha);
                var style = new StyleBoxFlat
                {
                    BgColor = nodeColor,
                    CornerRadiusTopLeft = (int)size,
                    CornerRadiusTopRight = (int)size,
                    CornerRadiusBottomLeft = (int)size,
                    CornerRadiusBottomRight = (int)size,
                    BorderWidthTop = 2,
                    BorderWidthBottom = 2,
                    BorderWidthLeft = 2,
                    BorderWidthRight = 2,
                    BorderColor = Colors.Black
                };
                // Flags color the pin's ring; special locations get a gold glow.
                if (MarkerLookup != null && ids.Count > 0)
                {
                    int flag = 0;
                    bool special = false;
                    foreach (long id in ids)
                    {
                        var m = MarkerLookup(id);
                        if (m.Flag > 0 && flag == 0) flag = m.Flag;
                        special |= m.Special;
                    }
                    if (flag > 0)
                    {
                        style.BorderColor = AP_Atlas.Core.Annotations.FlagColor(flag);
                        int ring = Math.Max(3, (int)(size * 0.18f));
                        style.BorderWidthTop = style.BorderWidthBottom = style.BorderWidthLeft = style.BorderWidthRight = ring;
                    }
                    if (special)
                    {
                        style.ShadowColor = AP_Atlas.Core.Annotations.SpecialColor;
                        style.ShadowSize = Math.Max(4, (int)(size * 0.35f));
                    }
                    if (flag > 0 || special)
                    {
                        string extra = (flag > 0 ? $"\nFlag: {AP_Atlas.Core.Annotations.FlagLabel(flag)}" : "") + (special ? "\n◆ Special" : "");
                        btn.TooltipText += extra;
                    }
                }
                var hoverStyle = (StyleBoxFlat)style.Duplicate();
                hoverStyle.BgColor = nodeColor.Lightened(0.2f);
                btn.AddThemeStyleboxOverride("normal", style);
                btn.AddThemeStyleboxOverride("hover", hoverStyle);
                btn.AddThemeStyleboxOverride("pressed", style);
                btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
                var pinIds = ids;
                string pinName = loc.Name;
                string pinMap = _currentMapId;
                btn.Pressed += () => PinPicked?.Invoke(pinMap, pinName, pinIds);
                _nodesOverlay.AddChild(btn);
                if (newlyUnlocked != null)
                {
                    bool isNew = false;
                    foreach (long id in ids) if (newlyUnlocked.Contains(id)) isNew = true;
                    if (isNew)
                    {
                        btn.PivotOffset = new Vector2(size / 2, size / 2);
                        var scaleTween = btn.CreateTween().SetLoops(10);
                        scaleTween.TweenProperty(btn, "scale", new Vector2(1.5f, 1.5f), 0.4f).SetTrans(Tween.TransitionType.Sine);
                        scaleTween.TweenProperty(btn, "scale", new Vector2(1.0f, 1.0f), 0.4f).SetTrans(Tween.TransitionType.Sine);
                        var colorTween = btn.CreateTween().SetLoops(10);
                        float alpha = btn.Modulate.A;
                        colorTween.TweenProperty(btn, "modulate", new Color(2f, 2f, 2f, Math.Max(alpha, 0.8f)), 0.4f);
                        colorTween.TweenProperty(btn, "modulate", new Color(1f, 1f, 1f, alpha), 0.4f);
                    }
                }
            }
        }
        private void OnViewportGuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton mb)
            {
                if (mb.ButtonIndex == MouseButton.Middle || mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Pressed)
                    {
                        _isDragging = true;
                        _lastMousePos = mb.Position;
                    }
                    else
                    {
                        _isDragging = false;
                    }
                }
                else if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
                {
                    _camera.Zoom *= 1.1f;
                    SaveCurrentCamera();
                }
                else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
                {
                    _camera.Zoom *= 0.9f;
                    SaveCurrentCamera();
                }
            }
            else if (@event is InputEventMouseMotion mm && _isDragging)
            {
                var delta = mm.Position - _lastMousePos;
                _camera.Position -= delta / _camera.Zoom;
                _lastMousePos = mm.Position;
                SaveCurrentCamera();
            }
        }
    }
}
