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
        private void ApplyZoom(float zoomVal) { float size = 64f * zoomVal; if (_visualGrid != null) { foreach (Node row in _visualGrid.GetChildren()) { foreach (Node cell in row.GetChildren()) { if (cell is Control c) { c.CustomMinimumSize = new Vector2(size, size); } } } } }
private ScrollContainer _textScroll;
        private Tree _textTree;

        private LoadedPack _pack;
        private bool _isVisualMode = false;

        private Dictionary<string, int> _receivedCounts = new Dictionary<string, int>();

        private int _globalTotalProgression = 0;
        private int _globalCollectedProgression = 0;

        private ImageTexture _texCollected;
        private ImageTexture _texMissing;

        public void Initialize(ArchipelagoSession session, LogicEngineManager logicEngine, string profileId, string slotName, Action<string> appendDebugLog)
        {
            _session = session;
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
            for(int x=0; x<16; x++) for(int y=0; y<16; y++) {
                float dist = (x-7.5f)*(x-7.5f) + (y-7.5f)*(y-7.5f);
                if (dist <= 30) imgCol.SetPixel(x, y, Colors.LimeGreen);
                if (dist <= 30) imgMis.SetPixel(x, y, Colors.DarkGray);
                else if (dist <= 40) imgMis.SetPixel(x, y, Colors.DimGray); // Outline for missing
            }
            _texCollected = ImageTexture.CreateFromImage(imgCol);
            _texMissing = ImageTexture.CreateFromImage(imgMis);

            BuildUI();

            // Try load pack
            if (_session != null && _session.ConnectionInfo != null && !string.IsNullOrEmpty(_session.ConnectionInfo.Game))
            {
                _pack = PopTrackerPackLoader.LoadPackForGame(_session.ConnectionInfo.Game);
            }

            if (_pack != null && _pack.ItemGrids.Count > 0)
            {
                _isVisualMode = true;
            }
            else
            {
                _isVisualMode = false;
            }

            UpdateModeToggleBtn();
            RefreshData();
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
            zoomLabel.AddThemeColorOverride("font_color", Colors.LightGray);
            _visualFooter.AddChild(zoomLabel);

            var zoomMinus = new Button { Text = "-", CustomMinimumSize = new Vector2(24, 24), FocusMode = FocusModeEnum.None };
            zoomMinus.Pressed += () => {
                var appSettings = DataManager.LoadSettings() ?? new AppSettings();
                appSettings.KeyItemZoom = Math.Max(0.5f, appSettings.KeyItemZoom - 0.25f);
                DataManager.SaveSettings(appSettings);
                ApplyZoom(appSettings.KeyItemZoom);
            };
            _visualFooter.AddChild(zoomMinus);

            var zoomPlus = new Button { Text = "+", CustomMinimumSize = new Vector2(24, 24), FocusMode = FocusModeEnum.None };
            zoomPlus.Pressed += () => {
                var appSettings = DataManager.LoadSettings() ?? new AppSettings();
                appSettings.KeyItemZoom = Math.Min(3.0f, appSettings.KeyItemZoom + 0.25f);
                DataManager.SaveSettings(appSettings);
                ApplyZoom(appSettings.KeyItemZoom);
            };
            _visualFooter.AddChild(zoomPlus);
            AddChild(_visualFooter);

            // Text Fallback Container
            _textScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
            var textMargin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            textMargin.AddThemeConstantOverride("margin_left", 10);
            textMargin.AddThemeConstantOverride("margin_right", 10);
            textMargin.AddThemeConstantOverride("margin_top", 10);
            _textScroll.AddChild(textMargin);
            _textTree = new Tree { 
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
            foreach (var item in _session.Items.AllItemsReceived)
            {
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
            string filter = _searchBox.Text.ToLowerInvariant();
            
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

            var appSettings = DataManager.LoadSettings() ?? new AppSettings();
            float zoomSize = 64f * appSettings.KeyItemZoom;

            foreach (var row in _pack.ItemGrids)
            {
                var rowContainer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
                _visualGrid.AddChild(rowContainer);

                foreach (var code in row)
                {
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        // Empty space in grid
                        var spacer = new Control { CustomMinimumSize = new Vector2(zoomSize, zoomSize) };
                        rowContainer.AddChild(spacer);
                        continue;
                    }

                    if (!_pack.ItemsByCode.TryGetValue(code, out var itemDef)) continue;

                    string nameLower = itemDef.Name.ToLowerInvariant();
                    bool isSearchMatch = string.IsNullOrEmpty(filter) || nameLower.Contains(filter);

                    int receivedQty = 0;
                    _receivedCounts.TryGetValue(itemDef.Name, out receivedQty);

                    var tile = CreateVisualTile(itemDef, receivedQty, zoomSize);
                    
                    if (!isSearchMatch)
                    {
                        tile.Modulate = new Color(0.2f, 0.2f, 0.2f, 0.2f);
                    }

                    rowContainer.AddChild(tile);
                }
            }
        }

        private Control CreateVisualTile(PopTrackerItem itemDef, int receivedQty, float zoomSize)
        {
            var container = new PanelContainer { CustomMinimumSize = new Vector2(zoomSize, zoomSize) };
            var style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) };
            container.AddThemeStyleboxOverride("panel", style);
            container.TooltipText = $"{itemDef.Name}\nCollected: {receivedQty}";

            var texRect = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(zoomSize, zoomSize) };
            
            // Determine image to show based on progressive stages
            string imgPath = itemDef.Img;
            if (string.IsNullOrEmpty(imgPath))
            {
                var codes = itemDef.GetCodes();
                if (codes.Count > 0) imgPath = "images/items/" + codes[0] + ".png";
            }
            
            if (itemDef.Type == "progressive" && itemDef.Stages.Count > 0)
            {
                int stageIdx = Math.Min(receivedQty, itemDef.Stages.Count) - 1;
                if (stageIdx >= 0)
                {
                    if (!string.IsNullOrEmpty(itemDef.Stages[stageIdx].Img))
                        imgPath = itemDef.Stages[stageIdx].Img;
                    else
                    {
                        var codesStr = itemDef.Stages[stageIdx].Codes;
                        if (!string.IsNullOrEmpty(codesStr))
                        {
                            var codes = codesStr.Split(new char[]{','}, System.StringSplitOptions.RemoveEmptyEntries);
                            if (codes.Length > 0) imgPath = "images/items/" + codes[0].Trim() + ".png";
                        }
                    }
                }
            }

            Texture2D matchedTex = null;
            if (!string.IsNullOrEmpty(imgPath))
            {
                if (_pack.Images.TryGetValue(imgPath, out var tex)) matchedTex = tex;
                else if (_pack.Images.TryGetValue("/" + imgPath, out tex)) matchedTex = tex;
                else
                {
                    // Fallback: try different extensions or ignore extension
                    string baseName = imgPath.Contains(".") ? imgPath.Substring(0, imgPath.LastIndexOf('.')) : imgPath;
                    foreach(var kvp in _pack.Images)
                    {
                        string testName = kvp.Key.Contains(".") ? kvp.Key.Substring(0, kvp.Key.LastIndexOf('.')) : kvp.Key;
                        if (testName.TrimStart('/') == baseName.TrimStart('/'))
                        {
                            matchedTex = kvp.Value;
                            break;
                        }
                    }
                }
            }

            if (matchedTex != null)
            {
                texRect.Texture = matchedTex;
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
                
                var dotTex = new TextureRect {
                    Texture = _texCollected,
                    StretchMode = TextureRect.StretchModeEnum.Keep,
                    SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
                    SizeFlagsVertical = SizeFlags.ShrinkEnd
                };
                dotContainer.AddChild(dotTex);
                container.AddChild(dotContainer);
                
                if (receivedQty > 1 && itemDef.Type == "progressive")
                {
                    var qtyLabel = new Label {
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
            _logger?.Invoke("[Key Items] RenderTextMode called.");
            _textTree.Clear();
            var root = _textTree.CreateItem();

            int collectedProgression = 0;
            int totalProgression = 0;

            if (_logicEngine == null)
            {
                _logger?.Invoke("[Key Items] ERROR: _logicEngine is null!");
                return;
            }

            if (_logicEngine.LastItemPool == null)
            {
                _logger?.Invoke("[Key Items] ERROR: _logicEngine.LastItemPool is null!");
                return;
            }

            _logger?.Invoke($"[Key Items] Pool size: {_logicEngine.LastItemPool.Count}");

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
            
            _logger?.Invoke($"[Key Items] Items marked as Progression flags: {poolCounts.Count} unique items.");

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
            
            _logger?.Invoke($"[Key Items] Current search filter: '{filter}'");

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

            _logger?.Invoke($"[Key Items] Filtered into {categories.Count} categories. Total items across categories: {totalProgression}.");

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
                catItem.SetCustomColor(0, Colors.LightSkyBlue);
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
                        row.SetCustomColor(0, Colors.White);
                        row.SetCustomColor(1, Colors.LightGray);
                        row.SetCustomColor(2, Colors.LimeGreen);
                    }
                    else
                    {
                        row.SetCustomColor(0, Colors.DarkGray);
                        row.SetCustomColor(1, Colors.DarkGray);
                        row.SetCustomColor(2, Colors.DarkGray);
                    }

                    shownCount++;
                }
            }

            _logger?.Invoke($"[Key Items] Rendering complete. Created {shownCount} item rows.");
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
