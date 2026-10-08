#nullable disable
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using AP_Atlas.Core.Maps;
namespace AP_Atlas.UI
{
    /// <summary>
    /// The Map Tracker: a map pack's maps with a pin per location, coloured by what logic says (in logic, some in logic as
    /// a pin split down the middle, hinted in or out of logic, out of logic, checked, excluded, not in the seed; neutral
    /// colours in race mode), on a <see cref="MapCanvas"/> shared with
    /// the Pack Doctor's editor. The explorer lists the maps with their counts; the display options and the legend sit
    /// above it. The view (the map point at the middle, and the zoom) is remembered per map.
    /// </summary>
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

        /// <summary>Translates text as it's shown (the window sets it).</summary>
        public static Func<string, string> Translate { get; set; } = text => text;

        /// <summary>The user turned "Follow the game's current map" on or off.</summary>
        public event Action<bool> FollowToggled;

        /// <summary>The user picked one of the pack's variants (its id).</summary>
        public event Action<string> VariantPicked;

        private HBoxContainer _variantRow;
        private OptionButton _variantPicker;
        private List<string> _variantIds = new List<string>();
        private bool _syncingVariant;

        /// <summary>The variants the picker offers, by id (for tests); empty when the pack has one or none.</summary>
        public IReadOnlyList<string> VariantOptions => _variantIds;

        /// <summary>The variant picker (for tests).</summary>
        public OptionButton VariantPicker => _variantPicker;

        /// <summary>Offers the pack's variants (shown only when there's more than one), with the one in use chosen.</summary>
        public void SetVariants(IReadOnlyList<(string Id, string Name)> variants, string current)
        {
            if (_variantPicker == null) return;
            _syncingVariant = true;
            _variantPicker.Clear();
            _variantIds = variants.Select(v => v.Id).ToList();
            foreach (var (id, name) in variants) _variantPicker.AddItem(name == id ? id : $"{name} ({id})");
            int index = _variantIds.IndexOf(current ?? "");
            if (index >= 0) _variantPicker.Selected = index;
            _variantRow.Visible = variants.Count > 1;
            _syncingVariant = false;
        }

        /// <summary>The user pressed "Find a map pack for {game}…" on the empty state (the owner searches GitHub for one).</summary>
        public event Action FindPackRequested;

        private Button _findPackButton;
        private string _gameName = "";

        /// <summary>The slot's game, named on the empty state's button ("Find a map pack for Dark Souls Remastered…").</summary>
        public string GameName
        {
            get => _gameName;
            set
            {
                _gameName = value ?? "";
                if (_findPackButton != null)
                    _findPackButton.Text = _gameName.Length > 0 ? Translate("Find a map pack for {0}…").Replace("{0}", _gameName) : Translate("Find a map pack…");
            }
        }

        /// <summary>The empty state's button (for tests).</summary>
        public Button FindPackButton => _findPackButton;

        private const int ModeShow = 0, ModeDim = 1, ModeHide = 2;
        private const float DimAlpha = 0.35f;

        private bool Excluded(long id) => IsExcluded?.Invoke(id) == true;

        /// <summary>The checks of a pin that count: excluded ones drop out unless the user shows them.</summary>
        private List<long> CountedIds(List<long> ids) =>
            _appSettings.MapExcludedMode == ModeShow || IsExcluded == null ? ids : ids.Where(id => !Excluded(id)).ToList();

        public LoadedPack Pack => _pack;

        /// <summary>Whether the pins show logic: it runs, it doesn't run (yet), or race mode hides it.</summary>
        public enum LogicShown
        {
            Running,
            NotRunning,
            Hidden
        }

        private LogicShown _logic = LogicShown.Running;

        /// <summary>Whether the pins show logic. Without it they show open, hinted and checked only, never in or out of logic.</summary>
        public LogicShown Logic
        {
            get => _logic;
            set
            {
                if (_logic == value) return;
                _logic = value;
                RefreshMapListCounters();
                BuildLegend();
                if (!string.IsNullOrEmpty(_currentMapId)) RenderLocations();
            }
        }

        /// <summary>The pins don't show logic (it isn't running, or race mode hides it).</summary>
        public bool LogicHidden => _logic != LogicShown.Running;

        public string CurrentMapId => _currentMapId;

        /// <summary>The canvas the map is drawn on.</summary>
        public MapCanvas Canvas => _canvas;

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
                _canvas.CenterOn(new Vector2(pin.X, pin.Y));
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

        public List<(PopTrackerLocation Loc, float X, float Y)> PinsOnMap(string mapId) =>
            PlacementsOn(mapId).Select(p => (p.Loc, p.X, p.Y)).ToList();

        /// <summary>The pins on a map with their placement (null for a location placed by its own x and y), which may set its size and shape.</summary>
        private List<(PopTrackerLocation Loc, PopTrackerMapLocation Place, float X, float Y)> PlacementsOn(string mapId)
        {
            var pins = new List<(PopTrackerLocation, PopTrackerMapLocation, float, float)>();
            if (_pack == null) return pins;
            foreach (var loc in _pack.Locations)
            {
                if (loc.MapRef.Equals(mapId, StringComparison.OrdinalIgnoreCase)) pins.Add((loc, null, loc.X, loc.Y));
                if (loc.MapLocations == null) continue;
                foreach (var ml in loc.MapLocations)
                {
                    if (ml.Map.Equals(mapId, StringComparison.OrdinalIgnoreCase)) pins.Add((loc, ml, ml.X, ml.Y));
                }
            }
            return pins;
        }

        /// <summary>Status of one AP location as the map colors it.</summary>
        public string LocationState(long id)
        {
            if (_checkedLocs.Contains(id)) return "checked";
            string excluded = Excluded(id) ? ", excluded" : "";
            if (LogicHidden) return (_hintedLocs.Contains(id) ? "hinted" : "open") + excluded;
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
        private IReadOnlySet<long> _glitchedLocs = new HashSet<long>();

        private VBoxContainer _mapListContainer;
        private OptionButton _sortDropdown;
        private MapCanvas _canvas;
        private Control _canvasPanel;
        private CenterContainer _emptyStateContainer;
        private VBoxContainer _legend;
        private bool _restoringView;

        public MapTrackerControl(AppSettings appSettings)
        {
            _appSettings = appSettings;
            if (_appSettings.MapCameras == null) _appSettings.MapCameras = new Dictionary<string, MapCameraSave>();
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            // --- The explorer: sort, display options, the legend, the maps ---
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
            var sortLabel = new Label { Text = "Sort:" };
            sortLabel.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
            sortHBox.AddChild(sortLabel);
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
            // --- The canvas ---
            var rightPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            rightPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = ThemeColors.SurfaceDeep });
            AddChild(rightPanel);
            _canvasPanel = rightPanel;
            _canvas = new MapCanvas();
            _canvas.ViewChanged += SaveCurrentView;
            rightPanel.AddChild(_canvas);
            _emptyStateContainer = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            var emptyVBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            emptyVBox.AddThemeConstantOverride("separation", 20);
            _emptyStateContainer.AddChild(emptyVBox);
            var emptyLbl = new Label { Text = Translate("No map pack installed"), HorizontalAlignment = HorizontalAlignment.Center };
            emptyLbl.SetMeta("font_size_ratio", 2.0f);
            emptyVBox.AddChild(emptyLbl);
            var emptySubLbl = new Label { Text = Translate("Atlas has no PopTracker map pack for this game.\nFind one on GitHub from here, or install one on the Map Packs page (Packs, on the left); it shows here at once."), HorizontalAlignment = Godot.HorizontalAlignment.Center };
            emptySubLbl.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            emptyVBox.AddChild(emptySubLbl);
            _findPackButton = Kit.Button(Translate("Find a map pack…"), Translate("One search of GitHub for this game's PopTracker packs, when you press; you choose what to install."), () => FindPackRequested?.Invoke());
            _findPackButton.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            emptyVBox.AddChild(_findPackButton);
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
        private CheckBox _followBox;
        private bool _syncingFollow;
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
            // The view: fit, zoom.
            var viewRow = new HBoxContainer();
            viewRow.AddThemeConstantOverride("separation", 4);
            viewRow.AddChild(Kit.Button("Fit", "Show the whole map (the wheel zooms around the cursor; drag to move)", () => _canvas.FitToView()));
            viewRow.AddChild(Kit.Button("−", "Zoom out", () => _canvas.ZoomAtCenter(1 / MapCanvas.WheelStep)));
            viewRow.AddChild(Kit.Button("+", "Zoom in", () => _canvas.ZoomAtCenter(MapCanvas.WheelStep)));
            box.AddChild(viewRow);
            // Shown only when the pack has more than one variant: which one this slot uses (remembered per slot).
            _variantRow = new HBoxContainer { Visible = false };
            _variantRow.AddThemeConstantOverride("separation", 6);
            var variantLabel = new Label { Text = Translate("Pack variant:") };
            variantLabel.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
            _variantRow.AddChild(variantLabel);
            _variantPicker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = Translate("Pack variant"), TooltipText = Translate("The pack's variants (its author's versions of the maps and items); the pack's default unless you pick another for this slot.") };
            _variantPicker.ItemSelected += index =>
            {
                if (!_syncingVariant && index >= 0 && index < _variantIds.Count) VariantPicked?.Invoke(_variantIds[(int)index]);
            };
            _variantRow.AddChild(_variantPicker);
            box.AddChild(_variantRow);
            // Shown only when the pack's scripts can switch the map to where the player is.
            _followBox = new CheckBox
            {
                Text = Translate("Follow the game's current map"),
                TooltipText = Translate("The pack switches the map to where you are in the game, from what the game's client tells the room. Turn it off to look around on your own."),
                Visible = false,
                ButtonPressed = true
            };
            _followBox.Toggled += on =>
            {
                if (!_syncingFollow) FollowToggled?.Invoke(on);
            };
            box.AddChild(_followBox);
            _displayToggle = new Button { Text = "▸ Display", ThemeTypeVariation = "QuietButton", Alignment = HorizontalAlignment.Left, TooltipText = "Node size and which pins the map shows" };
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
            var sizeLabel = new Label { Text = "Node size", CustomMinimumSize = new Vector2(100, 0) };
            sizeLabel.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
            sizeRow.AddChild(sizeLabel);
            // Not moved by the wheel: scrolling the explorer over it changed every pin's size.
            _nodeSizeSlider = new HSlider { MinValue = 0.3, MaxValue = 3.0, Step = 0.05, Scrollable = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "Size of the map's pins (double-click resets)" };
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
                var rowLabel = new Label { Text = label, CustomMinimumSize = new Vector2(100, 0), TooltipText = tooltip, MouseFilter = MouseFilterEnum.Pass };
                rowLabel.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
                row.AddChild(rowLabel);
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

            _legend = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _legend.AddThemeConstantOverride("separation", 2);
            box.AddChild(_legend);
            BuildLegend();
            return box;
        }

        /// <summary>What the legend shows, colour and words, in order (for tests).</summary>
        public IReadOnlyList<(Color Color, string Text)> LegendEntries { get; private set; } = Array.Empty<(Color, string)>();

        /// <summary>What the pins' colours mean: PopTracker's states while logic runs; otherwise open and checked, and why logic isn't shown.</summary>
        private void BuildLegend()
        {
            if (_legend == null) return;
            foreach (Node child in _legend.GetChildren()) child.QueueFree();
            var entries = new List<(Color Color, string Text)>();
            if (_logic == LogicShown.Running)
            {
                // In logic, then the half-and-half pin (some in logic), then the rest; the "logic unknown" colours don't occur.
                foreach (var state in MapPinLogic.All.Where(s => s != MapPinState.LogicUnknown && s != MapPinState.HintedUnknown))
                {
                    entries.Add((ThemeColors.MapColour(state), MapPinLogic.Title(state)));
                    if (state == MapPinState.InLogic) entries.Add((ThemeColors.MapColour(MapPinState.InLogic), MapPinLogic.Title(MapPinState.Mixed)));
                }
            }
            else
            {
                entries.Add((ThemeColors.MapColour(MapPinState.LogicUnknown), _logic == LogicShown.Hidden ? "Open: logic hidden by race mode" : "Open: logic not running"));
                entries.Add((ThemeColors.MapColour(MapPinState.HintedUnknown), MapPinLogic.Title(MapPinState.HintedUnknown)));
                entries.Add((ThemeColors.MapColour(MapPinState.Checked), MapPinLogic.Title(MapPinState.Checked)));
            }
            LegendEntries = entries;
            var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            flow.AddThemeConstantOverride("h_separation", 10);
            flow.AddThemeConstantOverride("v_separation", 2);
            string markerStyle = _appSettings.MapMarkerStyle;
            void Add(StyleBoxFlat swatchStyle, string text, Color? splitRight)
            {
                var item = new HBoxContainer();
                item.AddThemeConstantOverride("separation", 4);
                Control swatch;
                if (splitRight is { } right)
                {
                    // The half-and-half swatch is drawn like the pin: the left half the in-logic colour, the right half out of logic.
                    var split = new MapPinButton { SplitRight = right, Shape = MapPinGeometry.FromSetting(markerStyle), Border = 1, MouseFilter = MouseFilterEnum.Ignore, FocusMode = FocusModeEnum.None, AccessibilityName = text };
                    foreach (var name in new[] { "normal", "hover", "pressed", "focus", "disabled", "hover_pressed" }) split.AddThemeStyleboxOverride(name, swatchStyle);
                    swatch = split;
                }
                else
                {
                    swatch = new Panel();
                    swatch.AddThemeStyleboxOverride("panel", swatchStyle);
                }
                swatch.CustomMinimumSize = new Vector2(10, 10);
                swatch.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                ShapePin(swatchStyle, swatch, markerStyle, 10);
                item.AddChild(swatch);
                var label = new Label { Text = text };
                label.SetMeta("font_size_ratio", 0.85f);
                label.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
                item.AddChild(label);
                flow.AddChild(item);
            }
            foreach (var (color, text) in entries)
                Add(new StyleBoxFlat { BgColor = color, BorderColor = ThemeColors.MapPinBorder, BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1 }, text,
                    text == MapPinLogic.Title(MapPinState.Mixed) ? ThemeColors.MapColour(MapPinState.OutOfLogic) : null);
            var dim = new Label { Text = "Dimmed: excluded, or not in your seed" };
            dim.SetMeta("font_size_ratio", 0.85f);
            dim.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            _legend.AddChild(flow);
            _legend.AddChild(dim);
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

        /// <summary>Every map tracker draws its pins again (the pin shape setting changed).</summary>
        public static void RedrawAll()
        {
            _displayVersion++;
            DisplayOptionsChanged?.Invoke();
        }

        /// <summary>
        /// Shapes a pin (or a legend swatch) for the pin shape setting: round (corners as wide as the pin), square, or a
        /// diamond (a square turned on its corner, around its centre).
        /// </summary>
        internal static void ShapePin(StyleBoxFlat style, Control pin, string markerStyle, float size)
        {
            int radius = markerStyle is "square" or "diamond" ? 0 : (int)size;
            style.CornerRadiusTopLeft = style.CornerRadiusTopRight = style.CornerRadiusBottomLeft = style.CornerRadiusBottomRight = radius;
            pin.Rotation = markerStyle == "diamond" ? Mathf.Pi / 4 : 0f;
            pin.PivotOffset = pin.Size / 2;
            if (markerStyle == "diamond") pin.Resized += () => pin.PivotOffset = pin.Size / 2; // the canvas sizes pins as it lays them out
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
            BuildLegend();
            RefreshMapListCounters();
            if (!string.IsNullOrEmpty(_currentMapId)) RenderLocations();
        }

        public void SetSession(Archipelago.MultiClient.Net.ArchipelagoSession session)
        {
            _session = session;
            RefreshMapListCounters();
        }
        public void UpdateLogicColors(System.Collections.Generic.IReadOnlySet<long> reachableLocs, System.Collections.ObjectModel.ReadOnlyCollection<long> checkedLocs, System.Collections.Generic.IReadOnlySet<long> hintedLocs,
            System.Collections.Generic.IReadOnlySet<long> glitchedLocs = null)
        {
            if (reachableLocs != null) _reachableLocs = reachableLocs;
            if (glitchedLocs != null) _glitchedLocs = glitchedLocs;
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
            if (_canvasPanel != null) _canvasPanel.Visible = true;
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

        /// <summary>The slot's pack was removed or replaced: nothing of it is shown, and the empty state says why.</summary>
        public void ClearPack()
        {
            FollowAvailable = false;
            if (_variantRow != null) _variantRow.Visible = false;
            _pack = null;
            _index = null;
            _currentMapId = "";
            _locIdToMap.Clear();
            _canvas.SetPins(Array.Empty<MapCanvas.Pin>());
            _canvas.SetMap(null, Vector2.Zero);
            RefreshMapList();
            if (_canvasPanel != null) _canvasPanel.Visible = false;
            if (_emptyStateContainer != null) _emptyStateContainer.Visible = true;
        }

        /// <summary>Whether the pack can follow the game's map (its scripts switch tabs): the switch shows only then.</summary>
        public bool FollowAvailable
        {
            get => _followBox?.Visible == true;
            set { if (_followBox != null) _followBox.Visible = value; }
        }

        /// <summary>Whether the map follows the game's current map (the switch's state; set without telling anyone).</summary>
        public bool FollowGame
        {
            get => _followBox?.ButtonPressed != false;
            set
            {
                if (_followBox == null) return;
                _syncingFollow = true;
                _followBox.ButtonPressed = value;
                _syncingFollow = false;
            }
        }

        /// <summary>The switch (for tests).</summary>
        internal CheckBox FollowBox => _followBox;

        /// <summary>
        /// Shows the map of a pack tab, as a pack's script asks (Tracker:UiHint "ActivateTab", title): the tab's first map,
        /// else a map of that name, else one whose name holds it (letters and digits compared). False when nothing matches.
        /// </summary>
        public bool ActivateTab(string title)
        {
            string mapId = MapForTab(title);
            if (mapId == null) return false;
            if (!string.Equals(mapId, _currentMapId, StringComparison.OrdinalIgnoreCase)) SwitchMap(mapId);
            return true;
        }

        private string MapForTab(string title)
        {
            if (_pack == null || string.IsNullOrWhiteSpace(title)) return null;
            title = title.Trim();
            if (_pack.TabMaps.TryGetValue(title, out var maps))
            {
                string first = maps.FirstOrDefault(m => _pack.Maps.ContainsKey(m));
                if (first != null) return _pack.Maps[first].Id;
            }
            if (_pack.Maps.TryGetValue(title, out var named)) return named.Id;
            static string Plain(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            string wanted = Plain(title);
            if (wanted.Length == 0) return null;
            var byName = _pack.Maps.Values.FirstOrDefault(m => Plain(m.Name) == wanted);
            if (byName != null) return byName.Id;
            return _pack.Maps.Values.Where(m => Plain(m.Name).Contains(wanted, StringComparison.Ordinal) || (Plain(m.Name).Length > 2 && wanted.Contains(Plain(m.Name), StringComparison.Ordinal)))
                .OrderBy(m => m.Name.Length).Select(m => m.Id).FirstOrDefault();
        }

        /// <summary>Whether the map says there's no pack (for tests).</summary>
        public bool ShowingEmptyState => _emptyStateContainer?.Visible == true && _pack == null;

        /// <summary>The size slider of the display options (for tests).</summary>
        internal HSlider NodeSizeSlider => _nodeSizeSlider;

        /// <summary>AP location ids a pin covers.</summary>
        public List<long> GetLocationIds(PopTrackerLocation loc) => _index?.IdsFor(loc) ?? new List<long>();
        private class MapStat
        {
            public int Reachable = 0;
            public int Inaccessible = 0;
            public int HintedReachable = 0;
            public int HintedInaccessible = 0;
            public int Glitched = 0;
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
                int glitchedChecks = 0;
                int inaccessibleChecks = 0;
                foreach (long id in ids)
                {
                    if (_checkedLocs.Contains(id)) continue;
                    bool isR = _reachableLocs.Contains(id);
                    bool isH = _hintedLocs.Contains(id);
                    if (isH && isR) hintedReachableChecks++;
                    else if (isH && !isR) hintedInaccessibleChecks++;
                    else if (isR) reachableChecks++;
                    else if (_glitchedLocs.Contains(id)) glitchedChecks++;
                    else inaccessibleChecks++;
                }
                bool isAllChecked = reachableChecks == 0 && hintedReachableChecks == 0 && hintedInaccessibleChecks == 0 && glitchedChecks == 0 && inaccessibleChecks == 0;
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
                            stat.Glitched += glitchedChecks;
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
                        btn.SetMeta("sort_count", LogicHidden
                            ? stat.Reachable + stat.Inaccessible + stat.Glitched + stat.HintedReachable + stat.HintedInaccessible
                            : stat.TotalSort);
                        var counterHBox = btn.GetNodeOrNull<HBoxContainer>("Margin/HBox/Counters");
                        if (counterHBox != null)
                        {
                            foreach (Node n in counterHBox.GetChildren()) { counterHBox.RemoveChild(n); n.QueueFree(); }
                            void AddPill(int count, Color bgColor, string what)
                            {
                                if (count <= 0) return;
                                var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, TooltipText = $"{count} {what}" };
                                var style = new StyleBoxFlat { BgColor = bgColor, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 1, ContentMarginBottom = 1 };
                                panel.AddThemeStyleboxOverride("panel", style);
                                var lbl = new Label { Text = count.ToString(), HorizontalAlignment = Godot.HorizontalAlignment.Center, VerticalAlignment = Godot.VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
                                lbl.AddThemeColorOverride("font_color", ThemeColors.TextOn(bgColor));
                                panel.AddChild(lbl);
                                counterHBox.AddChild(panel);
                            }
                            if (LogicHidden)
                            {
                                AddPill(stat.Reachable + stat.Inaccessible + stat.Glitched, ThemeColors.MapColour(MapPinState.LogicUnknown), "open");
                                AddPill(stat.HintedReachable + stat.HintedInaccessible, ThemeColors.HintedNeutral, "hinted");
                            }
                            else
                            {
                                AddPill(stat.Reachable, ThemeColors.MapColour(MapPinState.InLogic), "in logic");
                                AddPill(stat.HintedReachable, ThemeColors.Hinted, "hinted, in logic");
                                AddPill(stat.HintedInaccessible, ThemeColors.HintedOutOfLogic, "hinted, out of logic");
                                AddPill(stat.Glitched, ThemeColors.MapColour(MapPinState.SequenceBreak), "sequence break");
                                AddPill(stat.Inaccessible, ThemeColors.MapColour(MapPinState.OutOfLogic), "out of logic");
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

        /// <summary>Remembers the view (the map point at the middle, and the zoom) for the map; a fit before the view has a size isn't kept.</summary>
        private void SaveCurrentView()
        {
            if (_restoringView || string.IsNullOrEmpty(_currentMapId) || _appSettings == null || !_canvas.HasView) return;
            if (_appSettings.MapCameras == null) _appSettings.MapCameras = new Dictionary<string, MapCameraSave>();
            var center = _canvas.Center;
            _appSettings.MapCameras[_currentMapId] = new MapCameraSave { X = center.X, Y = center.Y, Zoom = _canvas.Zoom };
            // Moves fire on every mouse-motion event: save once they settle.
            DataManager.SaveSettingsSoon(_appSettings);
        }

        public override void _ExitTree()
        {
            DisplayOptionsChanged -= OnDisplayOptionsChanged;
        }
        /// <summary>A map's background, or null when it has none (or it was freed with its pack).</summary>
        private static ImageTexture Background(PopTrackerMap map) => map?.Background;

        /// <summary>A map without an image is as big as its pins reach, with room around them.</summary>
        private Vector2 PinExtent(string mapId)
        {
            var pins = PinsOnMap(mapId);
            return pins.Count == 0 ? new Vector2(1600, 1000) : new Vector2(pins.Max(p => p.X) + 100, pins.Max(p => p.Y) + 100);
        }

        private void SwitchMap(string mapId)
        {
            if (_pack == null || !_pack.Maps.ContainsKey(mapId)) return;
            _currentMapId = mapId;
            var map = _pack.Maps[mapId];
            var background = Background(map);
            _restoringView = true;
            try
            {
                _canvas.SetMap(background, background?.GetSize() ?? PinExtent(mapId));
                if (_appSettings?.MapCameras != null && _appSettings.MapCameras.TryGetValue(mapId, out var state) && state.Zoom > 0)
                    _canvas.SetView(new Vector2(state.X, state.Y), state.Zoom);
                else
                    _canvas.FitToView();
            }
            finally
            {
                _restoringView = false;
            }
            RenderLocations();
        }

        private void RenderLocations(HashSet<long> newlyUnlocked = null)
        {
            using var __perf = AP_Atlas.Core.PerfMonitor.Measure("Map Tracker: draw markers");
            if (_pack == null || string.IsNullOrEmpty(_currentMapId))
            {
                _canvas.SetPins(Array.Empty<MapCanvas.Pin>());
                return;
            }
            int excludedMode = _appSettings.MapExcludedMode, notInSeedMode = _appSettings.MapNotInSeedMode;
            _pack.Maps.TryGetValue(_currentMapId, out var currentMap);
            // Atlas's own size for the pins when the pack sets none: from the map's image.
            float fallbackSize = Background(currentMap) is { } background ? Math.Max(16f, Math.Min(background.GetWidth(), background.GetHeight()) * 0.015f) : 24f;
            var appShape = MapPinGeometry.FromSetting(_appSettings.MapMarkerStyle);
            var pins = new List<MapCanvas.Pin>();
            var pulse = new List<Control>();
            foreach (var (loc, place, x, y) in PlacementsOn(_currentMapId))
            {
                var geometry = MapPinGeometry.Resolve(place?.Size ?? 0f, currentMap?.LocationSize ?? 0f, fallbackSize, place?.BorderThickness ?? -1f,
                    currentMap?.LocationBorderThickness ?? -1f, place?.Shape, currentMap?.LocationShape, appShape);
                float size = geometry.Size * _appSettings.MapNodeScale;
                var ids = GetLocationIds(loc);
                Color nodeColor;
                Color? splitRight = null;
                var shape = geometry.Shape;
                bool dim = false;
                string stateNote;
                if (ids.Count == 0)
                {
                    // No check of this pin exists in the seed (turned off by options, or the pack doesn't match).
                    if (notInSeedMode == ModeHide) continue;
                    nodeColor = ThemeColors.MapColour(LogicHidden ? MapPinState.LogicUnknown : MapPinState.OutOfLogic);
                    if (notInSeedMode == ModeDim) { nodeColor = ThemeColors.TextSubtle; dim = true; }
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
                        nodeColor = ThemeColors.MapColour(MapPinState.Checked);
                        stateNote = "\n" + MapPinLogic.Title(MapPinState.Checked);
                    }
                    else if (counted.Count == 0)
                    {
                        // Everything left here is excluded.
                        if (excludedMode == ModeHide) continue;
                        dim = true;
                        nodeColor = ThemeColors.TextSubtle;
                        stateNote = excludedOpen == 1 ? "\nExcluded" : $"\nExcluded ({excludedOpen} checks)";
                    }
                    else
                    {
                        int reachable = counted.Count(_reachableLocs.Contains);
                        int glitched = counted.Count(id => !_reachableLocs.Contains(id) && _glitchedLocs.Contains(id));
                        bool anyHinted = counted.Any(_hintedLocs.Contains);
                        var state = MapPinLogic.StateOf(counted.Count, reachable, glitched, !LogicHidden, anyHinted);
                        // A pin with some checks in logic is drawn half in logic, half out; a hinted pin has its own colours.
                        nodeColor = ThemeColors.MapColour(MapPinLogic.IsSplit(state) ? MapPinState.InLogic : state);
                        if (MapPinLogic.IsSplit(state)) splitRight = ThemeColors.MapColour(MapPinState.OutOfLogic);
                        string stateText = state != MapPinState.LogicUnknown ? MapPinLogic.Title(state)
                            : _logic == LogicShown.Hidden ? "Open" : "Open (logic isn't running)";
                        if (MapPinLogic.IsSplit(state)) stateText += $": {reachable} of {counted.Count}";
                        stateNote = "\n" + stateText;
                        if (excludedOpen > 0) stateNote += $"\n{excludedOpen} excluded";
                    }
                }
                var btn = new MapPinButton
                {
                    TooltipText = loc.Name + stateNote,
                    MouseDefaultCursorShape = CursorShape.PointingHand,
                    AccessibilityName = loc.Name + stateNote.Replace("\n", ": "),
                    SplitRight = splitRight,
                    Shape = shape
                };
                // Dimmed pins stay clickable, so an excluded check can be included again from Properties.
                if (dim) btn.Modulate = new Color(1f, 1f, 1f, DimAlpha);
                int border = (int)Math.Round(geometry.Border);
                var style = new StyleBoxFlat
                {
                    BgColor = nodeColor,
                    BorderWidthTop = border,
                    BorderWidthBottom = border,
                    BorderWidthLeft = border,
                    BorderWidthRight = border,
                    BorderColor = ThemeColors.MapPinBorder
                };
                // A flag the user set is a ring in the flag's colour (their own mark), and a special location gets a gold glow.
                void Ring(Color color)
                {
                    style.BorderColor = color;
                    int ring = Math.Max(3, (int)(size * 0.18f));
                    style.BorderWidthTop = style.BorderWidthBottom = style.BorderWidthLeft = style.BorderWidthRight = ring;
                }
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
                    if (flag > 0) Ring(AP_Atlas.Core.Annotations.FlagColor(flag));
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
                ShapePin(style, btn, MapPinGeometry.SettingOf(geometry.Shape), size);
                btn.Border = style.BorderWidthLeft;
                var hoverStyle = (StyleBoxFlat)style.Duplicate();
                hoverStyle.BgColor = nodeColor.Lightened(0.2f);
                var focusStyle = (StyleBoxFlat)style.Duplicate();
                focusStyle.BorderColor = ThemeColors.MapPinFocus;
                btn.AddThemeStyleboxOverride("normal", style);
                btn.AddThemeStyleboxOverride("hover", hoverStyle);
                btn.AddThemeStyleboxOverride("pressed", style);
                btn.AddThemeStyleboxOverride("focus", focusStyle);
                var pinIds = ids;
                string pinName = loc.Name;
                string pinMap = _currentMapId;
                btn.Pressed += () => PinPicked?.Invoke(pinMap, pinName, pinIds);
                // The wheel over a pin zooms the map as it does elsewhere.
                btn.GuiInput += ev =>
                {
                    if (_canvas.HandleWheel(ev, btn)) btn.AcceptEvent();
                };
                pins.Add(new MapCanvas.Pin { Key = $"{loc.Name}@{x},{y}", X = x, Y = y, Control = btn, MapSize = size });
                if (newlyUnlocked != null && ids.Any(newlyUnlocked.Contains)) pulse.Add(btn);
            }
            _canvas.SetPins(pins);
            foreach (var btn in pulse)
            {
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
