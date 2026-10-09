#nullable disable
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Archipelago.MultiClient.Net;

namespace AP_Atlas.Core.PopTracker
{
    public partial class ProgressionTrackerControl : VBoxContainer
    {
        private ArchipelagoSession _session;
        private AppSettings _appSettings;
        private LogicEngineManager _logicEngine;
        private string _profileId;
        private string _slotName;
        private Action<string> _logger;

        private Label _lblProgress;
        private Button _btnViewVisual;
        private Button _btnViewText;
        private Button _btnShowCollected;
        private Button _btnShowMissing;
        private LineEdit _searchBox;
        private OptionButton _optSortBy;

        private ScrollContainer _visualScroll;
        private VBoxContainer _visualGrid;
        private HBoxContainer _visualFooter;
        private ScrollContainer _textScroll;
        private Tree _textTree;

        private LoadedPack _pack;
        private bool _isVisualMode = false;

        private Dictionary<string, int> _receivedCounts = new Dictionary<string, int>();

        private int _globalTotalProgression = 0;
        private int _globalCollectedProgression = 0;

        private ImageTexture _texCollected;
        private ImageTexture _texMissing;

        /// <summary>An item tile or row was clicked (item name).</summary>
        public event Action<string> ItemPicked;

        /// <summary>Flag, special and note markers for an item name. Set by the owner.</summary>
        public Func<string, (int Flag, bool Special, bool Note)> MarkerLookup { get; set; }

        public void RefreshMarkers() => RenderActiveMode();

        /// <summary>What the view says while logic has no items for it (race mode, the engine's problem, starting); set by the owner.</summary>
        public Func<string, string> EmptyState { get; set; }

        /// <summary>The text mode has its empty state to show (no items known yet), not items.</summary>
        public bool ShowingEmptyState => _logicEngine == null || _logicEngine.LastItemPool == null || _logicEngine.LastItemPool.Count == 0;

        private Label _banner;

        /// <summary>A line above the items (the slot's BK banner); null or empty for none.</summary>
        public string Banner
        {
            get => _banner != null && _banner.Visible ? _banner.Text : null;
            set
            {
                if (_banner == null) return;
                _banner.Text = value ?? "";
                _banner.Visible = !string.IsNullOrEmpty(value);
            }
        }

        /// <summary>The text mode's rows, for tests.</summary>
        public Tree TextTree => _textTree;

        /// <summary>Filters the view to one item so it's easy to spot.</summary>
        public void RevealItem(string itemName)
        {
            if (_searchBox == null) return;
            _searchBox.Text = itemName ?? "";
            RenderActiveMode();
        }

        public void Initialize(ArchipelagoSession session, LogicEngineManager logicEngine, string profileId, string slotName, AppSettings appSettings, Action<string> appendDebugLog)
        {
            _session = session;
            _appSettings = appSettings;
            _logicEngine = logicEngine;
            _profileId = profileId;
            _slotName = slotName;
            _logger = appendDebugLog;

            SizeFlagsVertical = SizeFlags.ExpandFill;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;

            // Generate Circle Textures
            var imgCol = Godot.Image.CreateEmpty(16, 16, false, Godot.Image.Format.Rgba8);
            var imgMis = Godot.Image.CreateEmpty(16, 16, false, Godot.Image.Format.Rgba8);
            imgCol.Fill(Colors.Transparent);
            imgMis.Fill(Colors.Transparent);
            for (int x = 0; x < 16; x++)
            {
                for (int y = 0; y < 16; y++)
                {
                    float dist = (x - 7.5f) * (x - 7.5f) + (y - 7.5f) * (y - 7.5f);
                    if (dist <= 30) imgCol.SetPixel(x, y, Colors.LimeGreen);
                    if (dist <= 30) imgMis.SetPixel(x, y, Colors.DarkGray);
                    else if (dist <= 40) imgMis.SetPixel(x, y, Colors.DimGray); // Outline for missing
                }
            }
            _texCollected = ImageTexture.CreateFromImage(imgCol);
            _texMissing = ImageTexture.CreateFromImage(imgMis);

            BuildUI();

            // The pack is loaded off the main thread by SlotTrackerControl and handed over via SetPack().
            _isVisualMode = false;
            UpdateModeToggleBtn();
            RefreshData();
        }

        private PackIndex _index;
        private readonly Dictionary<long, int> _receivedById = new Dictionary<long, int>();

        /// <summary>Uses a pack's item grids, paired with this slot's items through the index.</summary>
        public void SetPack(LoadedPack pack, PackIndex index)
        {
            bool firstPack = _pack == null;
            _pack = pack;
            _index = index;
            if (firstPack) _isVisualMode = _pack != null && VisibleGrids().Any();
            UpdateModeToggleBtn();
            RefreshData();
        }

        // ---- The pack's variant ----

        private OptionButton _variantPicker;
        private readonly List<string> _variantIds = new List<string>();
        private bool _syncingVariant;

        /// <summary>The user picked another variant of the pack for this slot.</summary>
        public event Action<string> VariantPicked;

        /// <summary>The variants on offer, by id (for tests).</summary>
        public IReadOnlyList<string> VariantOptions => _variantIds;

        /// <summary>The picker (for tests).</summary>
        public OptionButton VariantPicker => _variantPicker;

        /// <summary>Offers the pack's variants (shown when there's more than one) with the current one chosen.</summary>
        public void SetVariants(IReadOnlyList<(string Id, string Name)> variants, string current)
        {
            if (_variantPicker == null) return;
            _syncingVariant = true;
            _variantIds.Clear();
            _variantPicker.Clear();
            foreach (var (id, name) in variants)
            {
                _variantIds.Add(id);
                _variantPicker.AddItem(name);
            }
            int index = _variantIds.IndexOf(current ?? "");
            if (index >= 0) _variantPicker.Selected = index;
            _variantPicker.Visible = variants.Count > 1;
            _syncingVariant = false;
        }

        /// <summary>The grids Key Items shows: item grids, without the pack's settings toggles.</summary>
        private IEnumerable<PackItemGrid> VisibleGrids() =>
            _pack?.ItemGridGroups.Where(g => !g.LooksLikeSettings) ?? Enumerable.Empty<PackItemGrid>();

        // ---- Layouts ----

        private OptionButton _layoutPicker;
        private List<string> _layoutIds = new List<string>();
        private bool _syncingLayout;

        private string SlotKey => AP_Atlas.Core.Annotations.SlotKey(_profileId ?? "", _slotName ?? "");

        /// <summary>The layouts on offer, by id: the pack's roots (in PopTracker's order) and the two Atlas builds (for tests).</summary>
        public List<string> LayoutChoices()
        {
            var ids = new List<string>();
            if (_pack != null) ids.AddRange(_pack.LayoutGrids.Keys.OrderBy(k => Array.IndexOf(RootOrder, k) is var i && i >= 0 ? i : 99).ThenBy(k => k));
            ids.Add(KeyItemsLayouts.Vertical);
            ids.Add(KeyItemsLayouts.Horizontal);
            return ids;
        }

        private static readonly string[] RootOrder = { "tracker_default", "tracker_horizontal", "tracker_vertical", "tracker_broadcast" };

        /// <summary>The layout in use: the slot's choice when the pack still offers it, else the pack's first root, else Vertical.</summary>
        public string CurrentLayout
        {
            get
            {
                var choices = LayoutChoices();
                if (_appSettings != null && _appSettings.KeyItemsLayout.TryGetValue(SlotKey, out var chosen) && choices.Contains(chosen)) return chosen;
                return _pack != null && _pack.LayoutGrids.Count > 0 ? choices[0] : KeyItemsLayouts.Vertical;
            }
        }

        /// <summary>Uses a layout for this slot (remembered) and draws the tiles again.</summary>
        public void SetLayout(string id)
        {
            if (_appSettings == null || !LayoutChoices().Contains(id)) return;
            _appSettings.KeyItemsLayout[SlotKey] = id;
            DataManager.SaveSettingsSoon(_appSettings);
            RenderActiveMode();
        }

        private void SyncLayoutPicker()
        {
            if (_layoutPicker == null) return;
            _syncingLayout = true;
            _layoutIds = LayoutChoices();
            _layoutPicker.Clear();
            foreach (var id in _layoutIds) _layoutPicker.AddItem(Tr(KeyItemsLayouts.NameOf(id)));
            int index = _layoutIds.IndexOf(CurrentLayout);
            if (index >= 0) _layoutPicker.Selected = index;
            _layoutPicker.Visible = _isVisualMode && (_pack != null || _layoutIds.Count > 0);
            _syncingLayout = false;
        }

        /// <summary>The grids of the pack's root layout in use, or null for a built layout.</summary>
        private List<PackItemGrid> RootGrids(string layout) =>
            _pack != null && _pack.LayoutGrids.TryGetValue(layout, out var grids) ? grids.Where(g => !g.LooksLikeSettings).ToList() : null;

        /// <summary>The built layouts' blocks: the pack's item groups, or, without a pack, the progression items by category.</summary>
        private List<LayoutBlock> BuiltBlocks()
        {
            var grids = VisibleGrids().ToList();
            if (grids.Count > 0) return KeyItemsLayouts.Blocks(grids);
            var pool = _logicEngine?.LastItemPool ?? new List<WorldItemInfo>();
            var items = pool.Where(i => (i.Flags & 1) != 0 && !string.IsNullOrEmpty(i.Name)).Select(i => (Code: "atlas_pool_" + i.Id, Category: InferItemType(i.Name))).ToList();
            if (items.Count == 0) items = pool.Where(i => !string.IsNullOrEmpty(i.Name)).Select(i => (Code: "atlas_pool_" + i.Id, Category: InferItemType(i.Name))).ToList();
            return KeyItemsLayouts.BlocksByCategory(items);
        }

        /// <summary>A tile's definition: the pack's item, or a plain one for a pool item (a game without a pack).</summary>
        private PopTrackerItem TileDefinition(string code)
        {
            if (_pack != null && _pack.ItemsByCode.TryGetValue(code, out var itemDef)) return itemDef;
            if (code.StartsWith("atlas_pool_", StringComparison.Ordinal) && long.TryParse(code.Substring("atlas_pool_".Length), out long id))
            {
                var item = _logicEngine?.LastItemPool?.FirstOrDefault(i => i.Id == id);
                if (item != null) return new PopTrackerItem { Name = item.Name, Type = "toggle", Img = "", CodesRaw = code };
            }
            return null;
        }

        /// <summary>The Archipelago name a grid code stands for (falls back to the pack's display name).</summary>
        private string ApNameFor(string code, PopTrackerItem itemDef)
        {
            var ids = _index?.ItemIdsFor(code);
            if (ids != null && ids.Count > 0)
            {
                string n = _index.ItemName(ids[0]) ?? _session?.Items.GetItemName(ids[0], _session.ConnectionInfo.Game);
                if (!string.IsNullOrEmpty(n)) return n;
            }
            return itemDef?.Name ?? code;
        }

        private void ChangeZoom(float delta)
        {
            _appSettings.KeyItemZoom = Math.Clamp(_appSettings.KeyItemZoom + delta, 0.5f, 3.0f);
            DataManager.SaveSettings(_appSettings);
            RenderActiveMode();
        }

        private void BuildUI()
        {
            if (GetChildCount() > 0)
            {
                // UI already built! Just update the references.
                return;
            }

            // Toolbar
            var toolbar = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            AddChild(toolbar);

            _scriptNote = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
            _scriptNote.AddThemeColorOverride("font_color", ThemeColors.Warning);
            AddChild(_scriptNote);
            _banner = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
            _banner.AddThemeColorOverride("font_color", ThemeColors.Warning);
            AddChild(_banner);

            // View Mode Segmented Control
            var viewHBox = new HBoxContainer();
            viewHBox.AddThemeConstantOverride("separation", 0);
            _btnViewVisual = new Button { Text = "🖼️ Visual", ToggleMode = true };
            _btnViewText = new Button { Text = "📄 Text", ToggleMode = true };
            _btnViewVisual.Pressed += () => { _isVisualMode = true; UpdateModeToggleBtn(); RenderActiveMode(); };
            _btnViewText.Pressed += () => { _isVisualMode = false; UpdateModeToggleBtn(); RenderActiveMode(); };
            viewHBox.AddChild(_btnViewVisual);
            viewHBox.AddChild(_btnViewText);
            toolbar.AddChild(viewHBox);

            // The layout: the pack's own root layouts, then the two Atlas builds from the item groups; kept per slot.
            _layoutPicker = new OptionButton { TooltipText = Tr("How the tiles are laid out: the pack's own layouts, or Atlas's by item group (stacked, or side by side)"), AccessibilityName = Tr("Key Items layout"), Visible = false };
            _layoutPicker.ItemSelected += index =>
            {
                if (_syncingLayout || index < 0 || index >= _layoutIds.Count) return;
                SetLayout(_layoutIds[(int)index]);
            };
            toolbar.AddChild(_layoutPicker);
            // The pack's variant (its author's versions of the layouts, most often the tiles' orientation), when it has more than one.
            _variantPicker = new OptionButton { TooltipText = Tr("The pack's variants (its author's versions of the layouts and items); the pack's default unless you pick another for this slot"), AccessibilityName = Tr("Pack variant"), Visible = false };
            _variantPicker.ItemSelected += index =>
            {
                if (_syncingVariant || index < 0 || index >= _variantIds.Count) return;
                VariantPicked?.Invoke(_variantIds[(int)index]);
            };
            toolbar.AddChild(_variantPicker);

            var sep1 = new VSeparator();
            toolbar.AddChild(sep1);

            _searchBox = new LineEdit { PlaceholderText = "Search...", CustomMinimumSize = new Vector2(200, 0) };
            _searchBox.TextChanged += (txt) => RenderActiveMode();
            toolbar.AddChild(_searchBox);

            _btnShowCollected = new Button { Text = "Collected", ToggleMode = true, ButtonPressed = true };
            _btnShowMissing = new Button { Text = "Missing", ToggleMode = true, ButtonPressed = true };
            _btnShowCollected.Toggled += (b) => RenderActiveMode();
            _btnShowMissing.Toggled += (b) => RenderActiveMode();
            toolbar.AddChild(_btnShowCollected);
            toolbar.AddChild(_btnShowMissing);

            _optSortBy = new OptionButton();
            _optSortBy.AddItem("Categories (Alphabetical)");
            _optSortBy.AddItem("Categories (Priority)");
            _optSortBy.AddItem("Items (Collected First)");
            _optSortBy.AddItem("Items (Missing First)");
            _optSortBy.ItemSelected += (idx) => RenderActiveMode();
            toolbar.AddChild(_optSortBy);

            var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            toolbar.AddChild(spacer);

            _lblProgress = new Label { Text = "0 / 0", CustomMinimumSize = new Vector2(100, 0), HorizontalAlignment = HorizontalAlignment.Right };
            toolbar.AddChild(_lblProgress);

            // Visual Mode Container
            _visualScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
            var visualCenter = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _visualScroll.AddChild(visualCenter);
            _visualGrid = new VBoxContainer();
            visualCenter.AddChild(_visualGrid);
            AddChild(_visualScroll);

            _visualFooter = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
            var footerSpacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _visualFooter.AddChild(footerSpacer);
            var zoomLabel = new Label { Text = "Item Size:" };
            zoomLabel.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
            _visualFooter.AddChild(zoomLabel);

            var zoomMinus = new Button { Text = "-", CustomMinimumSize = new Vector2(24, 24), AccessibilityName = Tr("Zoom out") };
            zoomMinus.Pressed += () => ChangeZoom(-0.25f);
            _visualFooter.AddChild(zoomMinus);

            var zoomPlus = new Button { Text = "+", CustomMinimumSize = new Vector2(24, 24), AccessibilityName = Tr("Zoom in") };
            zoomPlus.Pressed += () => ChangeZoom(0.25f);
            _visualFooter.AddChild(zoomPlus);
            AddChild(_visualFooter);

            // Text Fallback Container
            _textScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
            var textMargin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            textMargin.AddThemeConstantOverride("margin_left", 10);
            textMargin.AddThemeConstantOverride("margin_right", 10);
            textMargin.AddThemeConstantOverride("margin_top", 10);
            _textScroll.AddChild(textMargin);
            _textTree = new Tree
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                Columns = 3,
                ColumnTitlesVisible = true,
                HideRoot = true
            };
            _textTree.SetColumnTitle(0, "Item Name");
            _textTree.SetColumnTitle(1, "Type");
            _textTree.SetColumnTitle(2, "Progress");
            _textTree.SetColumnExpandRatio(0, 5);
            _textTree.SetColumnExpandRatio(1, 3);
            _textTree.SetColumnExpandRatio(2, 2);
            TreePicks.Hook(_textTree, (row, column) =>
            {
                var meta = row.GetMetadata(0);
                if (meta.VariantType == Variant.Type.String) ItemPicked?.Invoke(meta.AsString());
            });
            textMargin.AddChild(_textTree);
            AddChild(_textScroll);
        }

        private void UpdateModeToggleBtn()
        {
            if (_btnViewVisual != null) _btnViewVisual.ButtonPressed = _isVisualMode;
            if (_btnViewText != null) _btnViewText.ButtonPressed = !_isVisualMode;

            if (_visualScroll != null) _visualScroll.Visible = _isVisualMode;
            if (_visualFooter != null) _visualFooter.Visible = _isVisualMode;
            if (_textScroll != null) _textScroll.Visible = !_isVisualMode;
            if (_btnShowCollected != null) _btnShowCollected.Visible = !_isVisualMode;
            if (_btnShowMissing != null) _btnShowMissing.Visible = !_isVisualMode;
            if (_optSortBy != null) _optSortBy.Visible = !_isVisualMode;
        }



        public void UpdateFromSession()
        {
            RefreshData();
        }

        private void RefreshData()
        {
            if (_session == null || _logicEngine == null) return;

            // Recalculate received counts
            _receivedCounts.Clear();
            _receivedById.Clear();
            foreach (var item in _session.Items.AllItemsReceived)
            {
                _receivedById[item.ItemId] = _receivedById.GetValueOrDefault(item.ItemId) + 1;
                string name = _session.Items.GetItemName(item.ItemId);
                if (!string.IsNullOrEmpty(name))
                {
                    if (_receivedCounts.ContainsKey(name)) _receivedCounts[name]++;
                    else _receivedCounts[name] = 1;
                }
            }

            int total = 0;
            int collected = 0;
            if (_logicEngine.LastItemPool != null)
            {
                var poolCounts = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var wItem in _logicEngine.LastItemPool)
                {
                    if ((wItem.Flags & 1) != 0)
                    {
                        string wName = wItem.Name ?? "Unknown Item";
                        if (poolCounts.ContainsKey(wName)) poolCounts[wName]++;
                        else poolCounts[wName] = 1;
                    }
                }
                if (poolCounts.Count == 0 && _logicEngine.LastItemPool.Count > 0)
                {
                    foreach (var wItem in _logicEngine.LastItemPool)
                    {
                        string wName = wItem.Name ?? "Unknown Item";
                        if (poolCounts.ContainsKey(wName)) poolCounts[wName]++;
                        else poolCounts[wName] = 1;
                    }
                }
                foreach (var kvp in poolCounts)
                {
                    int maxQty = kvp.Value;
                    int receivedQty = 0;
                    _receivedCounts.TryGetValue(kvp.Key, out receivedQty);
                    total += maxQty;
                    collected += System.Math.Min(receivedQty, maxQty);
                }
            }

            _globalTotalProgression = total;
            _globalCollectedProgression = collected;

            if (_lblProgress != null) _lblProgress.Text = $"{collected} / {total}";

            RenderActiveMode();
        }

        private void RenderActiveMode()
        {
            using var __perf = AP_Atlas.Core.PerfMonitor.Measure("Key Items refresh");
            string filter = _searchBox.Text.ToLowerInvariant();
            UpdateScriptNote();

            if (_isVisualMode && _pack != null)
            {
                RenderVisualMode(filter);
            }
            else
            {
                RenderTextMode(filter);
            }
        }

        private void RenderVisualMode(string filter)
        {
            foreach (Node n in _visualGrid.GetChildren()) n.QueueFree();
            SyncLayoutPicker();

            float baseSize = 64f * _appSettings.KeyItemZoom;
            string layout = CurrentLayout;
            var grids = RootGrids(layout) ?? VisibleGrids().ToList();
            // The pack's tile sizes are kept relative to its most common size (e.g. DS3's 62px Cinders beside 40px keys).
            int commonSize = grids.Count == 0 ? 32 : grids.GroupBy(g => g.ItemSize).OrderByDescending(g => g.Count()).First().Key;
            string lastHeader = null;

            // A built layout: the item groups as blocks, stacked (Vertical) or side by side (Horizontal), each under its header.
            if (KeyItemsLayouts.IsBuilt(layout))
            {
                var blocks = BuiltBlocks();
                Container host = layout == KeyItemsLayouts.Horizontal ? new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center } : new VBoxContainer();
                host.AddThemeConstantOverride("separation", layout == KeyItemsLayouts.Horizontal ? 18 : 6);
                host.SetMeta("built_layout", layout);
                _visualGrid.AddChild(host);
                foreach (var block in blocks)
                {
                    var box = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkBegin };
                    if (blocks.Count > 1 || !string.IsNullOrEmpty(block.Header))
                    {
                        var header = new Label { Text = block.Header, HorizontalAlignment = HorizontalAlignment.Center };
                        header.AddThemeColorOverride("font_color", ThemeColors.AccentText(ThemeColors.Surface));
                        box.AddChild(header);
                    }
                    float blockSize = baseSize * Math.Clamp(block.ItemSize / (float)Math.Max(1, commonSize), 0.6f, 1.8f);
                    foreach (var row in block.Rows) box.AddChild(TileRow(row, blockSize, filter));
                    host.AddChild(box);
                }
                RenderSeedSettings(baseSize, commonSize, filter);
                return;
            }

            foreach (var grid in grids)
            {
                if (!string.IsNullOrEmpty(grid.Header) && grid.Header != lastHeader && grids.Select(g => g.Header).Distinct().Count() > 1)
                {
                    var header = new Label { Text = grid.Header, HorizontalAlignment = HorizontalAlignment.Center };
                    header.AddThemeColorOverride("font_color", ThemeColors.AccentText(ThemeColors.Surface));
                    _visualGrid.AddChild(header);
                }
                lastHeader = grid.Header;
                float scale = Math.Clamp(grid.ItemSize / (float)Math.Max(1, commonSize), 0.6f, 1.8f);
                float zoomSize = baseSize * scale;

                foreach (var row in grid.Rows) _visualGrid.AddChild(TileRow(row, zoomSize, filter));
            }

            RenderSeedSettings(baseSize, commonSize, filter);
        }

        /// <summary>The visual view's shape (for tests): the tile rows drawn straight under the grid, and the built layout's id when one hosts them.</summary>
        public (int Rows, string Built) VisualShape()
        {
            int rows = 0;
            string built = null;
            foreach (Node n in _visualGrid.GetChildren())
            {
                if (n.IsQueuedForDeletion()) continue;
                if (n.HasMeta("built_layout")) built = n.GetMeta("built_layout").AsString();
                else if (n is HBoxContainer) rows++;
            }
            return (rows, built);
        }

        /// <summary>One row of tiles (an empty cell, or a code nothing defines, keeps the row's spacing).</summary>
        private HBoxContainer TileRow(List<string> row, float zoomSize, string filter)
        {
            var rowContainer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            foreach (var code in row)
            {
                var itemDef = string.IsNullOrWhiteSpace(code) ? null : TileDefinition(code);
                if (itemDef == null)
                {
                    rowContainer.AddChild(new Control { CustomMinimumSize = new Vector2(zoomSize, zoomSize) });
                    continue;
                }

                string apName = ApNameFor(code, itemDef);
                bool isSearchMatch = string.IsNullOrEmpty(filter) ||
                                     (itemDef.Name ?? "").ToLowerInvariant().Contains(filter) ||
                                     apName.ToLowerInvariant().Contains(filter);

                int receivedQty = _index != null
                    ? _index.ReceivedCount(code, _receivedById, n => _receivedCounts.GetValueOrDefault(n))
                    : _receivedCounts.GetValueOrDefault(itemDef.Name ?? "");

                // The pack's own scripts know this item best (stages, counts); the mapping still lights
                // tiles the scripts miss (e.g. an outdated script id the user re-linked in the Doctor).
                int? stage = null;
                var state = ScriptState?.Invoke(code);
                if (state != null && state.Touched)
                {
                    bool consumable = (itemDef.Type ?? "") == "consumable";
                    int scriptQty = consumable ? state.Count : state.Active ? Math.Max(1, state.Stage) : 0;
                    receivedQty = Math.Max(receivedQty, scriptQty);
                    if (state.Active && (itemDef.Type ?? "").StartsWith("progressive")) stage = state.Stage;
                }

                var tile = CreateVisualTile(itemDef, apName, receivedQty, zoomSize, stage);
                if (!isSearchMatch) tile.Modulate = new Color(0.2f, 0.2f, 0.2f, 0.2f);
                rowContainer.AddChild(tile);
            }
            return rowContainer;
        }

        /// <summary>Gets the pack scripts' state for a code (null when the scripts aren't running). Set by the owner.</summary>
        public Func<string, PackScriptHost.TileState> ScriptState { get; set; }

        /// <summary>The seed's settings as the pack shows them. Set by the owner.</summary>
        public Func<List<PackScriptHost.SettingInfo>> SeedSettings { get; set; }

        /// <summary>Why the pack's scripts were stopped (null while they run, or for a pack without any). Set by the owner.</summary>
        public Func<string> ScriptStopReason { get; set; }

        private Label _scriptNote;

        /// <summary>The note Key Items shows about the pack's scripts, or null when none shows.</summary>
        public string ShownScriptNote => _scriptNote is { Visible: true } ? _scriptNote.Text : null;

        /// <summary>Says so when the pack's scripts were stopped: tiles then come from the pack's item list alone.</summary>
        private void UpdateScriptNote()
        {
            string reason = ScriptStopReason?.Invoke();
            _scriptNote.Visible = reason != null;
            if (reason != null)
                _scriptNote.Text = $"This map pack's scripts were stopped: {reason}. Key Items still shows your items from the pack's item list, but not what its scripts work out (stages, counts and seed settings).";
        }

        /// <summary>
        /// The pack's settings grid(s), lit from the seed's options by the pack's own scripts. Shown only when the
        /// scripts ran (otherwise the state would be a guess).
        /// </summary>
        private void RenderSeedSettings(float baseSize, int commonSize, string filter)
        {
            var settingGrids = _pack.ItemGridGroups.Where(g => g.LooksLikeSettings).ToList();
            var settings = SeedSettings?.Invoke();
            if (settingGrids.Count == 0 || settings == null || settings.Count == 0) return;
            var byCode = new Dictionary<string, PackScriptHost.SettingInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in settings)
            {
                if (_pack.ItemsByCode.TryGetValue(s.Code, out var def))
                    foreach (var c in def.GetCodes()) byCode.TryAdd(c, s);
            }

            _visualGrid.AddChild(new HSeparator());
            var header = new Label { Text = "Seed settings", HorizontalAlignment = HorizontalAlignment.Center, TooltipText = "Read from this slot's options by the map pack's own script" };
            header.AddThemeColorOverride("font_color", ThemeColors.AccentText(ThemeColors.Surface));
            _visualGrid.AddChild(header);

            foreach (var grid in settingGrids)
            {
                float zoomSize = baseSize * Math.Clamp(grid.ItemSize / (float)Math.Max(1, commonSize), 0.6f, 1.8f) * 0.85f;
                foreach (var row in grid.Rows)
                {
                    var rowContainer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
                    _visualGrid.AddChild(rowContainer);
                    foreach (var code in row)
                    {
                        if (string.IsNullOrWhiteSpace(code) || !_pack.ItemsByCode.TryGetValue(code, out var def))
                        {
                            rowContainer.AddChild(new Control { CustomMinimumSize = new Vector2(zoomSize, zoomSize) });
                            continue;
                        }
                        byCode.TryGetValue(code, out var info);
                        bool on = info?.On == true;
                        var tile = CreateVisualTile(def, def.Name ?? code, on ? 1 : 0, zoomSize, info != null && def.Stages?.Count > 0 ? info.Stage : null, isSetting: true);
                        string value = info?.ValueText ?? "";
                        tile.TooltipText = $"{def.Name}: {(info?.StageName ?? (on ? "on" : "off"))}" +
                                           (info?.OptionPath != null ? $"\nOption: {info.OptionPath}" + (info.OptionMissing ? " (not in this slot's data)" : $" = {value}") : "") +
                                           "\nSet by the map pack's script from this slot's options.";
                        if (!string.IsNullOrEmpty(filter) && !(def.Name ?? "").ToLowerInvariant().Contains(filter)) tile.Modulate = new Color(0.2f, 0.2f, 0.2f, 0.2f);
                        rowContainer.AddChild(tile);
                    }
                }
            }
        }

        /// <summary>The caption of a tile without an image: the item's own name, or the last part of a path-like pack name ("characters/base/jack" → "jack").</summary>
        private static string TileCaption(PopTrackerItem itemDef, string apName)
        {
            string name = !string.IsNullOrEmpty(apName) && apName != itemDef.Name ? apName : itemDef.Name ?? "";
            int cut = name.LastIndexOf('/');
            return cut >= 0 && cut < name.Length - 1 ? name[(cut + 1)..] : name;
        }

        private Control CreateVisualTile(PopTrackerItem itemDef, string apName, int receivedQty, float zoomSize, int? stage = null, bool isSetting = false)
        {
            var container = new PanelContainer { CustomMinimumSize = new Vector2(zoomSize, zoomSize), MouseDefaultCursorShape = isSetting ? CursorShape.Arrow : CursorShape.PointingHand };
            var style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) };
            // Setting indicators aren't items: no flags, specials or item details.
            (int Flag, bool Special, bool Note) marker = isSetting ? (0, false, false) : MarkerLookup?.Invoke(apName) ?? (0, false, false);
            if (marker.Flag > 0 || marker.Special)
            {
                // Special items get a gold frame and glow; flagged ones a frame in the flag's color.
                style.BorderColor = marker.Special ? Annotations.SpecialColor : Annotations.FlagColor(marker.Flag);
                style.BorderWidthTop = style.BorderWidthBottom = style.BorderWidthLeft = style.BorderWidthRight = 3;
                style.CornerRadiusTopLeft = style.CornerRadiusTopRight = style.CornerRadiusBottomLeft = style.CornerRadiusBottomRight = 6;
                if (marker.Special) { style.ShadowColor = new Color(Annotations.SpecialColor, 0.5f); style.ShadowSize = 6; }
            }
            container.AddThemeStyleboxOverride("panel", style);
            container.TooltipText = apName + (apName != itemDef.Name ? $"\n(pack: {itemDef.Name})" : "") + $"\nCollected: {receivedQty}" +
                                    (marker.Special ? "\n◆ Special" : "") +
                                    (marker.Flag > 0 ? $"\nFlag: {Annotations.FlagLabel(marker.Flag)}" : "") +
                                    "\nClick for details";
            string pickedName = apName;
            container.GuiInput += ev =>
            {
                if (!isSetting && ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left) ItemPicked?.Invoke(pickedName);
            };

            var texRect = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(zoomSize, zoomSize) };

            // Determine image to show based on progressive stages
            string imgPath = itemDef.Img;
            if (string.IsNullOrEmpty(imgPath))
            {
                var codes = itemDef.GetCodes();
                if (codes.Count > 0) imgPath = "images/items/" + codes[0] + ".png";
            }

            if ((itemDef.Type ?? "").StartsWith("progressive") && itemDef.Stages.Count > 0)
            {
                // The scripts give the exact stage; otherwise the received count picks it.
                int stageIdx = stage != null ? Math.Clamp(stage.Value, 0, itemDef.Stages.Count - 1) : Math.Min(receivedQty, itemDef.Stages.Count) - 1;
                if (stageIdx >= 0)
                {
                    if (!string.IsNullOrEmpty(itemDef.Stages[stageIdx].Img))
                        imgPath = itemDef.Stages[stageIdx].Img;
                    else
                    {
                        var codesStr = itemDef.Stages[stageIdx].Codes;
                        if (!string.IsNullOrEmpty(codesStr))
                        {
                            var codes = codesStr.Split(new char[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
                            if (codes.Length > 0) imgPath = "images/items/" + codes[0].Trim() + ".png";
                        }
                    }
                }
            }

            Texture2D matchedTex = _pack.FindImage(imgPath);

            if (matchedTex != null)
            {
                texRect.Texture = matchedTex;
            }
            else
            {
                // The pack has no image for this item (or it can't be read): the item's name on a plain tile, never a blank.
                var frame = new Panel { MouseFilter = MouseFilterEnum.Ignore };
                frame.SetAnchorsPreset(LayoutPreset.FullRect);
                frame.AddThemeStyleboxOverride("panel", new StyleBoxFlat
                {
                    BgColor = ThemeColors.SurfaceSunken,
                    BorderColor = ThemeColors.TextSubtle,
                    BorderWidthLeft = 1,
                    BorderWidthTop = 1,
                    BorderWidthRight = 1,
                    BorderWidthBottom = 1,
                    CornerRadiusTopLeft = 3,
                    CornerRadiusTopRight = 3,
                    CornerRadiusBottomLeft = 3,
                    CornerRadiusBottomRight = 3
                });
                texRect.AddChild(frame);
                var nameLabel = new Label
                {
                    Text = TileCaption(itemDef, apName),
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    ClipText = true,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    MouseFilter = MouseFilterEnum.Ignore
                };
                nameLabel.SetAnchorsPreset(LayoutPreset.FullRect);
                nameLabel.AddThemeFontSizeOverride("font_size", Math.Max(8, (int)(zoomSize / 4.5f)));
                nameLabel.AddThemeColorOverride("font_color", ThemeColors.Text);
                texRect.AddChild(nameLabel);
                container.TooltipText += "\nNo image in the pack: " + (imgPath ?? "(none named)");
            }

            if (receivedQty == 0)
            {
                texRect.Modulate = new Color(0.4f, 0.4f, 0.5f, 0.5f);
            }

            container.AddChild(texRect);

            // Add collected green dot if collected
            if (receivedQty > 0)
            {
                var dotContainer = new MarginContainer();
                dotContainer.AddThemeConstantOverride("margin_right", 4);
                dotContainer.AddThemeConstantOverride("margin_bottom", 4);

                var dotTex = new TextureRect
                {
                    Texture = _texCollected,
                    StretchMode = TextureRect.StretchModeEnum.Keep,
                    SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
                    SizeFlagsVertical = SizeFlags.ShrinkEnd
                };
                dotContainer.AddChild(dotTex);
                container.AddChild(dotContainer);

                if (receivedQty > 1 && itemDef.Type == "progressive")
                {
                    var qtyLabel = new Label
                    {
                        Text = receivedQty.ToString(),
                        LabelSettings = new LabelSettings { FontSize = 16, OutlineSize = 4, OutlineColor = Colors.Black },
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    container.AddChild(qtyLabel);
                }
            }

            return container;
        }

        private void RenderTextMode(string filter)
        {
            _textTree.Clear();
            var root = _textTree.CreateItem();

            int collectedProgression = 0;
            int totalProgression = 0;

            if (ShowingEmptyState)
            {
                // Nothing to list yet: say why, as the Logic Tracker does.
                var note = _textTree.CreateItem(root);
                note.SetText(0, EmptyState?.Invoke("No items known yet.") ?? "Waiting for the logic engine…");
                note.SetCustomColor(0, ThemeColors.TextSubtle);
                note.SetExpandRight(0, true);
                for (int c = 0; c < _textTree.Columns; c++) note.SetSelectable(c, false);
                return;
            }


            // Group pool by item name
            var poolCounts = new Dictionary<string, int>();
            foreach (var wItem in _logicEngine.LastItemPool)
            {
                string wName = wItem.Name ?? "Unknown Item";
                if ((wItem.Flags & 1) != 0) // Progression
                {
                    if (poolCounts.ContainsKey(wName)) poolCounts[wName]++;
                    else poolCounts[wName] = 1;
                }
            }


            // Failsafe: If python bridge failed to assign flags, show everything
            if (poolCounts.Count == 0 && _logicEngine.LastItemPool.Count > 0)
            {
                _logger?.Invoke($"[Key Items] Failsafe Triggered: No progression items found. Treating all items as progression.");
                foreach (var wItem in _logicEngine.LastItemPool)
                {
                    string wName = wItem.Name ?? "Unknown Item";
                    if (poolCounts.ContainsKey(wName)) poolCounts[wName]++;
                    else poolCounts[wName] = 1;
                }
                _logger?.Invoke($"[Key Items] Failsafe resulted in {poolCounts.Count} unique items.");
            }

            // Group by basic inferred types
            var categories = new Dictionary<string, List<(string Name, int Received, int Max)>>();


            bool showCollected = _btnShowCollected.ButtonPressed;
            bool showMissing = _btnShowMissing.ButtonPressed;
            int sortMode = _optSortBy.Selected; // 0 = Alphabetical, 1 = Priority

            foreach (var kvp in poolCounts.OrderBy(x => x.Key))
            {
                string name = kvp.Key;
                int maxQty = kvp.Value;

                if (!string.IsNullOrEmpty(filter) && !name.ToLowerInvariant().Contains(filter)) continue;

                int receivedQty = 0;
                _receivedCounts.TryGetValue(name, out receivedQty);

                totalProgression += maxQty;
                collectedProgression += Math.Min(receivedQty, maxQty);

                if (!showCollected && receivedQty >= maxQty) continue;
                if (!showMissing && receivedQty < maxQty) continue;

                string type = InferItemType(name);
                if (!categories.ContainsKey(type)) categories[type] = new List<(string Name, int Received, int Max)>();
                categories[type].Add((name, receivedQty, maxQty));
            }


            int shownCount = 0;

            var orderedCategories = sortMode == 0
                ? categories.OrderBy(c => c.Key)
                : categories.OrderByDescending(c => c.Key == "Key / Access")
                            .ThenByDescending(c => c.Key == "Magic / Spell")
                            .ThenByDescending(c => c.Key == "Weapon / Shield")
                            .ThenByDescending(c => c.Key == "Ring")
                            .ThenByDescending(c => c.Key == "Map / Compass")
                            .ThenByDescending(c => c.Key == "Upgrade")
                            .ThenByDescending(c => c.Key == "Health")
                            .ThenByDescending(c => c.Key == "Armor")
                            .ThenByDescending(c => c.Key == "Soul")
                            .ThenBy(c => c.Key);

            foreach (var cat in orderedCategories)
            {
                // Category Header
                var catItem = _textTree.CreateItem(root);
                catItem.SetText(0, cat.Key);
                catItem.SetCustomColor(0, ThemeColors.AccentText(ThemeColors.Surface));
                catItem.SetCustomBgColor(0, new Color(0.1f, 0.1f, 0.15f, 0.8f));
                catItem.SetCustomBgColor(1, new Color(0.1f, 0.1f, 0.15f, 0.8f));
                catItem.SetCustomBgColor(2, new Color(0.1f, 0.1f, 0.15f, 0.8f));
                catItem.DisableFolding = false;

                var items = cat.Value.AsEnumerable();
                if (sortMode == 2) items = items.OrderByDescending(i => i.Received >= i.Max).ThenBy(i => i.Name);
                else if (sortMode == 3) items = items.OrderBy(i => i.Received >= i.Max).ThenBy(i => i.Name);
                else items = items.OrderBy(i => i.Name);

                foreach (var item in items)
                {
                    var row = _textTree.CreateItem(catItem);
                    row.SetText(0, " " + item.Name);
                    row.SetIcon(0, item.Received > 0 ? _texCollected : _texMissing);
                    row.SetText(1, cat.Key);
                    row.SetMetadata(0, item.Name);
                    var marker = MarkerLookup?.Invoke(item.Name) ?? (0, false, false);
                    row.SetIcon(1, Annotations.MarkerIcon(marker.Flag, marker.Special, marker.Note));
                    row.SetIconMaxWidth(1, Math.Max(16, _appSettings.ContentFontSize * 2));

                    if (item.Max > 1 || item.Received > 1)
                        row.SetText(2, $"{item.Received} / {item.Max}");
                    else if (item.Received > 0)
                        row.SetText(2, "Collected");
                    else
                        row.SetText(2, "Missing");

                    Color bg = (shownCount % 2 == 0) ? new Color(0.08f, 0.08f, 0.11f, 0.9f) : new Color(0.12f, 0.12f, 0.15f, 0.9f);
                    row.SetCustomBgColor(0, bg);
                    row.SetCustomBgColor(1, bg);
                    row.SetCustomBgColor(2, bg);

                    if (item.Received > 0)
                    {
                        row.SetCustomColor(0, ThemeColors.Text);
                        row.SetCustomColor(1, ThemeColors.TextMuted);
                        row.SetCustomColor(2, ThemeColors.Success);
                    }
                    else
                    {
                        row.SetCustomColor(0, ThemeColors.Disabled);
                        row.SetCustomColor(1, ThemeColors.Disabled);
                        row.SetCustomColor(2, ThemeColors.Disabled);
                    }

                    shownCount++;
                }
            }

        }

        private string InferItemType(string name)
        {
            string lower = name.ToLowerInvariant();
            if (lower.Contains("key") || lower.Contains("card") || lower.Contains("pass")) return "Key / Access";
            if (lower.Contains("ring")) return "Ring";
            if (lower.Contains("soul")) return "Soul";
            if (lower.Contains("weapon") || lower.Contains("sword") || lower.Contains("bow") || lower.Contains("shield")) return "Weapon / Shield";
            if (lower.Contains("armor") || lower.Contains("helm") || lower.Contains("gauntlet") || lower.Contains("legging")) return "Armor";
            if (lower.Contains("spell") || lower.Contains("magic") || lower.Contains("scroll") || lower.Contains("tome")) return "Magic / Spell";
            if (lower.Contains("map") || lower.Contains("compass")) return "Map / Compass";
            if (lower.Contains("upgrade") || lower.Contains("capacity") || lower.Contains("expansion")) return "Upgrade";
            if (lower.Contains("heart") || lower.Contains("health") || lower.Contains("piece")) return "Health";
            return "Misc Item";
        }

        public (int Collected, int Total) GetProgressionCounts()
        {
            return (_globalCollectedProgression, _globalTotalProgression);
        }
    }
}
