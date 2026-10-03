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

        private AppSettings _appSettings;
        private Archipelago.MultiClient.Net.ArchipelagoSession _session;
        private LoadedPack _pack;
        private string _currentMapId = "";

        private Dictionary<long, string> _locIdToMap = new Dictionary<long, string>();
        private Dictionary<string, long> _locNameToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<long, string> _locIdToName = new Dictionary<long, string>();

        private HashSet<long> _reachableLocs = new HashSet<long>();
        private HashSet<long> _checkedLocs = new HashSet<long>();
        private HashSet<long> _hintedLocs = new HashSet<long>();

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
            sortMargin.AddChild(sortHBox);
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

            // Camera moves fire on every mouse-motion event; batch them into one settings write.
            _cameraSaveTimer = new Timer { OneShot = true, WaitTime = 0.5 };
            _cameraSaveTimer.Timeout += () => DataManager.SaveSettings(_appSettings);
            AddChild(_cameraSaveTimer);
        }

        private Timer _cameraSaveTimer;
        public void SetSession(Archipelago.MultiClient.Net.ArchipelagoSession session)
        {
            _session = session;
            RefreshMapListCounters();
        }
        public void UpdateLogicColors(System.Collections.Generic.HashSet<long> reachableLocs, System.Collections.ObjectModel.ReadOnlyCollection<long> checkedLocs, System.Collections.Generic.HashSet<long> hintedLocs)
        {
            if (reachableLocs != null) _reachableLocs = reachableLocs;
            if (checkedLocs != null) _checkedLocs = new System.Collections.Generic.HashSet<long>(checkedLocs);
            if (hintedLocs != null) _hintedLocs = hintedLocs;
            RefreshMapListCounters();
            if (!string.IsNullOrEmpty(_currentMapId)) RenderLocations();
        }
        private void BuildLocationMaps()
        {
            _locIdToMap.Clear();
            if (_pack == null) return;
            foreach (var loc in _pack.Locations)
            {
                var ids = GetLocationIds(loc);
                foreach (long id in ids)
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
        public void LoadPack(LoadedPack pack)
        {
            _pack = pack;
            if (_emptyStateContainer != null) _emptyStateContainer.Visible = false;
            if (_viewportContainer != null) _viewportContainer.Visible = true;
            BuildNameLookup();
            if (_session != null) BuildLocationMaps();
            RefreshMapList();
            if (_pack.Maps.Count > 0)
            {
                SwitchMap(_pack.Maps.Keys.First());
            }
        }
        private void BuildNameLookup()
        {
            _locNameToId.Clear();
            _locIdToName.Clear();
            if (_session == null) return;
            foreach (var id in _session.Locations.AllLocations)
            {
                string n = _session.Locations.GetLocationNameFromId(id);
                if (string.IsNullOrEmpty(n)) continue;
                _locIdToName[id] = n;
                _locNameToId[n] = id;
                int dash = n.IndexOf(" - ");
                if (dash > 0)
                {
                    string prefix = n.Substring(0, dash).Trim();
                    if (!_locNameToId.ContainsKey(prefix)) _locNameToId[prefix] = id;
                }
            }
        }
        private List<long> GetLocationIds(PopTrackerLocation loc)
        {
            var ids = new List<long>();
            if (_session == null || _pack == null) return ids;
            string searchName = loc.Name;
            if (loc.Sections != null && loc.Sections.Count > 0)
            {
                foreach (var sec in loc.Sections)
                {
                    string secName = !string.IsNullOrEmpty(sec.Name) ? sec.Name : searchName;
                    if (_locNameToId.TryGetValue(secName, out long sId)) ids.Add(sId);
                }
            }
            else
            {
                if (_locNameToId.TryGetValue(searchName, out long id)) ids.Add(id);
            }
            return ids;
        }
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
                btn.Pressed += () => SwitchMap(mapId);
            }
            _mapListContainer.AddThemeConstantOverride("separation", 4);
            RefreshMapListCounters();
            OnDataRefreshed?.Invoke();
        }
        private void RefreshMapListCounters()
        {
            if (_pack == null || _session == null) return;
            var mapCounts = new Dictionary<string, MapStat>(StringComparer.OrdinalIgnoreCase);
            foreach (var map in _pack.Maps.Values) mapCounts[map.Id] = new MapStat();
            foreach (var loc in _pack.Locations)
            {
                var ids = GetLocationIds(loc);
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
                        btn.SetMeta("sort_count", stat.TotalSort);
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
                            AddPill(stat.Reachable, Colors.LimeGreen, Colors.Black);
                            AddPill(stat.HintedReachable, Colors.DeepSkyBlue, Colors.Black);
                            AddPill(stat.HintedInaccessible, Colors.Purple, Colors.White);
                            AddPill(stat.Inaccessible, Colors.Crimson, Colors.White);
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
            var map = _pack.Maps[_currentMapId];
            if (map.BackgroundTexture == null) return;
            _camera.Position = map.BackgroundTexture.GetSize() / 2f;
            var mapSize = map.BackgroundTexture.GetSize();
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
            if (_cameraSaveTimer.IsInsideTree()) _cameraSaveTimer.Start();
            else DataManager.SaveSettings(_appSettings);
        }

        public override void _ExitTree()
        {
            // Flush a pending debounced camera save.
            if (!_cameraSaveTimer.IsStopped())
            {
                _cameraSaveTimer.Stop();
                DataManager.SaveSettings(_appSettings);
            }
        }
        private void SwitchMap(string mapId)
        {
            if (_pack == null || !_pack.Maps.ContainsKey(mapId)) return;
            if (!string.IsNullOrEmpty(_currentMapId))
            {
                SaveCurrentCamera();
            }
            _currentMapId = mapId;
            var map = _pack.Maps[mapId];
            if (map.BackgroundTexture != null)
            {
                _mapBackground.Texture = map.BackgroundTexture;
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
            foreach (var node in nodesToDraw)
            {
                var loc = node.Loc;
                var ids = GetLocationIds(loc);
                Color nodeColor = Colors.Crimson; // Red by default
                if (ids.Count > 0 && _session != null)
                {
                    bool anyReachable = false;
                    bool allChecked = true;
                    bool anyHinted = false;
                    foreach (long id in ids)
                    {
                        if (!_checkedLocs.Contains(id)) allChecked = false;
                        if (_reachableLocs.Contains(id) && !_checkedLocs.Contains(id)) anyReachable = true;
                        if (_hintedLocs.Contains(id)) anyHinted = true;
                    }
                    if (allChecked) nodeColor = Colors.DimGray;
                    else if (anyHinted && anyReachable) nodeColor = Colors.DeepSkyBlue;
                    else if (anyHinted && !anyReachable) nodeColor = Colors.Purple;
                    else if (anyReachable) nodeColor = Colors.LimeGreen;
                }
                float size = 24f;
                if (_pack.Maps.TryGetValue(_currentMapId, out var currentMap) && currentMap.LocationSize > 0)
                {
                    size = currentMap.LocationSize;
                }
                else if (currentMap != null && currentMap.BackgroundTexture != null)
                {
                    size = Math.Max(16f, Math.Min(currentMap.BackgroundTexture.GetWidth(), currentMap.BackgroundTexture.GetHeight()) * 0.015f);
                }
                size = size * _appSettings.MapNodeScale;
                var btn = new Button
                {
                    Position = new Vector2(node.X - (size / 2), node.Y - (size / 2)),
                    CustomMinimumSize = new Vector2(size, size),
                    TooltipText = loc.Name + (ids.Count > 0 ? "" : "\n(Not found in AP logic)"),
                    MouseDefaultCursorShape = CursorShape.PointingHand
                };
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
                var hoverStyle = (StyleBoxFlat)style.Duplicate();
                hoverStyle.BgColor = nodeColor.Lightened(0.2f);
                btn.AddThemeStyleboxOverride("normal", style);
                btn.AddThemeStyleboxOverride("hover", hoverStyle);
                btn.AddThemeStyleboxOverride("pressed", style);
                btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
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
                        colorTween.TweenProperty(btn, "modulate", new Color(2f, 2f, 2f, 1f), 0.4f);
                        colorTween.TweenProperty(btn, "modulate", new Color(1f, 1f, 1f, 1f), 0.4f);
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
