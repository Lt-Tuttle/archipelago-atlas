using Archipelago.MultiClient.Net;
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using AP_Atlas.Core.PopTracker;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core
{
    public partial class MapPackManagerControl : MarginContainer
    {
        public event Action OnDataRefreshed;
        private VBoxContainer _packListVBox;
        private PanelContainer _selectedPackRow;
        private Action<string> _logAction;
        private Action _showOverlayAction;
        private Action _hideOverlayAction;
        private Func<HashSet<string>> _getActiveGamesFunc;

        private Func<List<ArchipelagoSession>> _getActiveSessionsFunc;
        private ScrollContainer _mainScroll;
        private MarginContainer _inspectorContainer;
        public Control SidebarContent { get; private set; }

        public MapPackManagerControl(Action<string> logAction, Action showOverlayAction, Action hideOverlayAction, Func<HashSet<string>> getActiveGamesFunc, Func<List<ArchipelagoSession>> getActiveSessionsFunc = null)
        {
            _logAction = logAction;
            _showOverlayAction = showOverlayAction;
            _hideOverlayAction = hideOverlayAction;
            _getActiveGamesFunc = getActiveGamesFunc;
            _getActiveSessionsFunc = getActiveSessionsFunc;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;

            SidebarContent = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            var sidebarMargin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            SidebarContent.AddChild(sidebarMargin);
            var sidebarVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            sidebarMargin.AddChild(sidebarVBox);

            _mainScroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            _packListVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _mainScroll.AddChild(_packListVBox);
            sidebarVBox.AddChild(_mainScroll);

            var btnVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            btnVBox.AddThemeConstantOverride("separation", 10);

            var searchBtn = new Button { Text = "Find Missing Packs Online" };
            AddAccentText(searchBtn);
            searchBtn.Pressed += OnSearchPressed;
            btnVBox.AddChild(searchBtn);

            var updatesBtn = new Button { Text = "Check for Updates" };
            AddAccentText(updatesBtn);
            updatesBtn.Pressed += OnCheckUpdatesPressed;
            btnVBox.AddChild(updatesBtn);

            var importBtn = new Button { Text = "Install Pack (Zip)" };
            importBtn.Pressed += OnImportPackPressed;
            btnVBox.AddChild(importBtn);

            var folderBtn = new Button { Text = "Open Packs Folder" };
            folderBtn.Pressed += OnOpenFolderPressed;
            btnVBox.AddChild(folderBtn);

            var rescanBtn = new Button { Text = "Rescan Packs" };
            AddAccentText(rescanBtn);
            rescanBtn.Pressed += RefreshPackList;
            btnVBox.AddChild(rescanBtn);

            sidebarVBox.AddChild(new HSeparator());
            sidebarVBox.AddChild(btnVBox);

            _inspectorContainer = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            _inspectorContainer.AddThemeConstantOverride("margin_left", 20);
            _inspectorContainer.AddThemeConstantOverride("margin_top", 20);
            _inspectorContainer.AddThemeConstantOverride("margin_right", 20);
            _inspectorContainer.AddThemeConstantOverride("margin_bottom", 20);
            AddChild(_inspectorContainer);

            var emptyLbl = new Label { Text = "Select a map pack to view details.", HorizontalAlignment = HorizontalAlignment.Center };
            _inspectorContainer.AddChild(emptyLbl);

            // Re-scan only when the packs folder changed since the list was last built.
            VisibilityChanged += () => { if (Visible) RefreshPackListIfChanged(); };
        }



        // Buttons whose label is drawn in the theme accent; re-colored when the accent changes.
        private readonly List<Button> _accentTextButtons = new List<Button>();

        private void AddAccentText(Button button)
        {
            button.AddThemeColorOverride("font_color", ThemeColors.Accent);
            _accentTextButtons.Add(button);
        }

        private static StyleBoxFlat PackRowStyle(bool selected) => new StyleBoxFlat
        {
            BgColor = new Color(selected ? "#2A2D2E" : "#252526"),
            BorderColor = selected ? ThemeColors.Accent : new Color("#333"),
            BorderWidthLeft = selected ? 4 : 1,
            BorderWidthBottom = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 15,
            ContentMarginRight = 15,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };

        public override void _EnterTree()
        {
            ThemeColors.AccentChanged += OnAccentChanged;
            PackDoctorService.ReportReady += OnDoctorReportReady;
        }

        public override void _ExitTree()
        {
            ThemeColors.AccentChanged -= OnAccentChanged;
            PackDoctorService.ReportReady -= OnDoctorReportReady;
        }

        private void OnAccentChanged()
        {
            _accentTextButtons.RemoveAll(b => !GodotObject.IsInstanceValid(b));
            foreach (var b in _accentTextButtons) b.AddThemeColorOverride("font_color", ThemeColors.Accent);
            if (_selectedPackRow != null && GodotObject.IsInstanceValid(_selectedPackRow))
                _selectedPackRow.AddThemeStyleboxOverride("panel", PackRowStyle(true));
        }

        private void ShowPackDetails(string zipPath)
        {
            foreach (Node n in _inspectorContainer.GetChildren()) n.QueueFree();
            var pack = PopTrackerPackLoader.InspectZipPack(zipPath, null);
            if (pack == null) return;
            _shownPackPath = zipPath;
            var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            _inspectorContainer.AddChild(vbox);

            var title = new Label { Text = pack.Manifest.Name };
            title.AddThemeFontSizeOverride("font_size", 24);
            vbox.AddChild(title);

            ArchipelagoSession activeSession = null;
            if (_getActiveSessionsFunc != null)
            {
                var sessions = _getActiveSessionsFunc();
                foreach (var s in sessions)
                {
                    if (PopTrackerPackLoader.IsGameNameMatch(pack.Manifest.GameName, s.ConnectionInfo.Game) || PopTrackerPackLoader.IsGameNameMatch(pack.Manifest.Name, s.ConnectionInfo.Game))
                    {
                        activeSession = s;
                        break;
                    }
                }
            }

            var richText = new RichTextLabel { BbcodeEnabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            string txt = $"[b]Game:[/b] {pack.Manifest.GameName}\n";
            txt += $"[b]Author:[/b] {pack.Manifest.Author}\n";
            txt += $"[b]Version:[/b] {pack.Manifest.GetActualVersion()}\n\n";

            txt += $"[b]--- METADATA ---[/b]\n";
            txt += $"[color=lime]Key Items Extracted:[/color] {pack.ItemsByCode.Count}\n";
            txt += $"[color=lime]Maps Extracted:[/color] {pack.Maps.Count}\n";
            txt += $"[color=lime]Nodes Extracted:[/color] {pack.Locations.Count}\n\n";

            // Pack Doctor summary: how well the pack pairs with the game's real items and locations.
            txt += "[b]--- PACK DOCTOR ---[/b]\n";
            if (PackDoctorService.Reports.TryGetValue(PackFixes.KeyFor(pack), out var report) && report != null)
            {
                int needs = report.NeedsReview.Count();
                txt += report.Names == null
                    ? "[color=orange]No name list for this game yet (connect a slot or set the Archipelago install path).[/color]\n"
                    : $"[color=gray]Checked against {report.Names.Game} names from the {report.Names.Source}.[/color]\n";
                txt += $"[color=cyan]Key Items tiles linked:[/color] {report.TilesLinked} / {report.TilesTotal}\n";
                txt += $"[color=cyan]Pin sections linked:[/color] {report.SectionsLinked} / {report.SectionsTotal}\n";
                txt += $"[color=cyan]Game locations on a map:[/color] {report.ApLocationsPlaced} / {report.ApLocationsTotal}\n";
                txt += needs > 0 ? $"[color=orange]{needs} thing{(needs == 1 ? "" : "s")} to review.[/color]\n" : "[color=lime]Nothing needs your attention.[/color]\n";
                int fixCount = PackFixes.Get(PackFixes.KeyFor(pack)).Count;
                if (fixCount > 0) txt += $"[color=#FFC53D]{fixCount} local fix{(fixCount == 1 ? "" : "es")} applied.[/color]\n";
            }
            else
            {
                txt += "[color=gray]Checking…[/color]\n";
                _ = PackDoctorService.CheckAsync(pack, prompt: false);
            }
            _ = activeSession; // the Doctor uses every name source, not just a connected session

            txt += $"\n[b]--- MAP LIST ---[/b]\n";
            foreach (var map in pack.Maps.Values)
            {
                txt += $" - {map.Name} [color=gray]({map.Id})[/color]\n";
            }

            richText.Text = txt;
            vbox.AddChild(richText);

            vbox.AddChild(new HSeparator());
            var btnHBox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            btnHBox.AddThemeConstantOverride("separation", 20);

            var doctorBtn = new Button { Text = "Pack Doctor…", CustomMinimumSize = new Vector2(200, 40), TooltipText = "Check this pack against the game and fix problems locally" };
            AddAccentText(doctorBtn);
            doctorBtn.Pressed += () => OpenDoctor?.Invoke(zipPath);
            btnHBox.AddChild(doctorBtn);

            var updateBtn = new Button { Text = "Update Map Pack", CustomMinimumSize = new Vector2(200, 40) };
            AddAccentText(updateBtn);
            updateBtn.Pressed += () => CheckSinglePackUpdate(zipPath);
            btnHBox.AddChild(updateBtn);

            var deleteBtn = new Button { Text = "Delete Map Pack", CustomMinimumSize = new Vector2(200, 40) };
            deleteBtn.AddThemeColorOverride("font_color", Colors.Crimson);
            deleteBtn.Pressed += () =>
            {
                try
                {
                    System.IO.File.Delete(zipPath);
                }
                catch (Exception ex)
                {
                    _logAction($"[color=red]Failed to delete map pack: {ex.Message}[/color]");
                    return;
                }
                RefreshPackList();
                foreach (Node n in _inspectorContainer.GetChildren()) n.QueueFree();
                _inspectorContainer.AddChild(new Label { Text = "Select a map pack to view details.", HorizontalAlignment = HorizontalAlignment.Center });
            };
            btnHBox.AddChild(deleteBtn);

            vbox.AddChild(btnHBox);
            OnDataRefreshed?.Invoke();
        }

        private async void CheckSinglePackUpdate(string zipPath)
        {
            _logAction($"[color=cyan]Checking for updates...[/color]");
            try
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
                var manifestEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
                if (manifestEntry == null) { _logAction("[color=red]No manifest found in pack.[/color]"); return; }
                using var stream = manifestEntry.Open();
                using var reader = new System.IO.StreamReader(stream);
                var json = reader.ReadToEnd();
                var manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<PopTrackerManifest>(json);
                if (manifest == null || string.IsNullOrEmpty(manifest.VersionsUrl)) { _logAction("[color=yellow]Pack does not support auto-updates (No VersionsUrl provided).[/color]"); return; }

                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "AP-Atlas-Tracker");
                var res = await client.GetStringAsync(manifest.VersionsUrl);
                Newtonsoft.Json.Linq.JObject latestObj = null;
                if (res.TrimStart().StartsWith("["))
                {
                    var versionsArray = Newtonsoft.Json.Linq.JArray.Parse(res);
                    if (versionsArray.HasValues) latestObj = versionsArray[0] as Newtonsoft.Json.Linq.JObject;
                }
                else if (res.TrimStart().StartsWith("{"))
                {
                    latestObj = Newtonsoft.Json.Linq.JObject.Parse(res);
                }
                if (latestObj != null)
                {
                    string latestVersion = latestObj["version"]?.ToString() ?? latestObj["tag_name"]?.ToString() ?? latestObj["name"]?.ToString();
                    if (!string.IsNullOrEmpty(latestVersion) && latestVersion != manifest.GetActualVersion())
                    {
                        string downloadUrl = latestObj?["download_url"]?.ToString();
                        if (string.IsNullOrEmpty(downloadUrl))
                        {
                            var assets = latestObj?["assets"] as Newtonsoft.Json.Linq.JArray;
                            if (assets != null && assets.Count > 0) downloadUrl = assets[0]["browser_download_url"]?.ToString();
                            else downloadUrl = latestObj?["html_url"]?.ToString() ?? latestObj?["url"]?.ToString() ?? "";
                        }
                        _logAction($"[color=lime]Update available for {manifest.Name}:[/color] v{latestVersion}");
                        if (!string.IsNullOrEmpty(downloadUrl))
                        {
                            _logAction($"Opening download page...");
                            Godot.OS.ShellOpen(downloadUrl);
                        }
                    }
                    else
                    {
                        _logAction($"[color=green]{manifest.Name} is up to date (v{manifest.GetActualVersion()}).[/color]");
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"[color=red]Failed to check for updates: {ex.Message}[/color]");
            }
        }
        private string _packListSignature;
        private bool _packListLoading;

        private static string[] GetPackFiles()
        {
            string dataDir = System.IO.Path.Combine(DataManager.GetDataDirectory(), "packs");
            if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
            return Directory.GetFiles(dataDir, "*.zip");
        }

        private static string PackFolderSignature(string[] files) =>
            string.Join("|", files.Select(f => { var i = new FileInfo(f); return $"{f}:{i.Length}:{i.LastWriteTimeUtc.Ticks}"; }));

        private void RefreshPackListIfChanged()
        {
            if (PackFolderSignature(GetPackFiles()) != _packListSignature) RefreshPackList();
        }

        /// <summary>
        /// Rebuilds the pack list. Packs that haven't been parsed yet (each zip holds every map/item image)
        /// are read on a worker thread first so opening the tab doesn't freeze the UI.
        /// </summary>
        private async void RefreshPackList()
        {
            if (_packListLoading) return;
            _packListLoading = true;
            try
            {
                var files = GetPackFiles();
                var uncached = files.Where(f => !PopTrackerPackLoader.IsPackCached(f)).ToList();
                if (uncached.Count > 0)
                {
                    foreach (Node n in _packListVBox.GetChildren()) n.QueueFree();
                    var loading = new Label { Text = $"Reading {uncached.Count} map pack(s)...", HorizontalAlignment = HorizontalAlignment.Center };
                    loading.AddThemeColorOverride("font_color", Colors.Gray);
                    _packListVBox.AddChild(loading);
                    _logAction($"[color=gray]Reading {uncached.Count} map pack(s) in the background (map and item images are decoded once, then cached)...[/color]");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await Task.Run(() =>
                    {
                        foreach (var f in uncached) PopTrackerPackLoader.InspectZipPack(f, null);
                    });
                    if (!GodotObject.IsInstanceValid(this)) return;
                    _logAction($"[color=gray]Map packs ready ({sw.ElapsedMilliseconds} ms).[/color]");
                }
                _packListSignature = PackFolderSignature(files);
                using (PerfMonitor.Measure("Build map pack list"))
                {
                    BuildPackRows(files);
                }
            }
            finally
            {
                _packListLoading = false;
            }
        }

        private void BuildPackRows(string[] files)
        {
            foreach (Node n in _packListVBox.GetChildren()) n.QueueFree();

            if (files.Length == 0)
            {
                var lbl = new Label { Text = "No map packs installed.", HorizontalAlignment = HorizontalAlignment.Center };
                lbl.AddThemeColorOverride("font_color", Colors.Gray);
                _packListVBox.AddChild(lbl);
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    var pack = PopTrackerPackLoader.InspectZipPack(file, null);
                    if (pack == null || pack.Manifest == null) continue;
                    var manifest = pack.Manifest;

                    var row = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    row.AddThemeStyleboxOverride("panel", PackRowStyle(false));

                    var hbox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    var infoVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };

                    var titleLbl = new Label { Text = manifest.Name };
                    titleLbl.AddThemeFontSizeOverride("font_size", 16);
                    titleLbl.AddThemeColorOverride("font_color", Colors.White);
                    infoVBox.AddChild(titleLbl);

                    var detailLbl = new Label { Text = $"Game: {manifest.GameName} | v{manifest.GetActualVersion()}" };
                    detailLbl.AddThemeFontSizeOverride("font_size", 12);
                    detailLbl.AddThemeColorOverride("font_color", Colors.LightGray);
                    infoVBox.AddChild(detailLbl);

                    var capsHbox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    capsHbox.AddThemeConstantOverride("separation", 15);
                    var itemIndicator = new Label { Text = $"Items: {pack.ItemsByCode.Count}" };
                    itemIndicator.AddThemeFontSizeOverride("font_size", 11);
                    itemIndicator.AddThemeColorOverride("font_color", pack.ItemsByCode.Count > 0 ? Colors.LightGreen : Colors.DimGray);
                    capsHbox.AddChild(itemIndicator);
                    var mapIndicator = new Label { Text = $"Maps: {pack.Maps.Count}" };
                    mapIndicator.AddThemeFontSizeOverride("font_size", 11);
                    mapIndicator.AddThemeColorOverride("font_color", pack.Maps.Count > 0 ? Colors.LightGreen : Colors.DimGray);
                    capsHbox.AddChild(mapIndicator);
                    var locIndicator = new Label { Text = $"Locs: {pack.Locations.Count}" };
                    locIndicator.AddThemeFontSizeOverride("font_size", 11);
                    locIndicator.AddThemeColorOverride("font_color", pack.Locations.Count > 0 ? Colors.LightGreen : Colors.DimGray);
                    capsHbox.AddChild(locIndicator);
                    // Pack Doctor status (filled in when a check finishes).
                    var doctorBadge = new Label { Name = "DoctorBadge" };
                    doctorBadge.AddThemeFontSizeOverride("font_size", 11);
                    doctorBadge.SetMeta("pack_key", PackFixes.KeyFor(pack));
                    capsHbox.AddChild(doctorBadge);
                    UpdateDoctorBadge(doctorBadge);
                    infoVBox.AddChild(capsHbox);

                    hbox.AddChild(infoVBox);

                    row.MouseFilter = MouseFilterEnum.Stop;
                    row.MouseDefaultCursorShape = CursorShape.PointingHand;
                    string localFilePath = file;
                    row.GuiInput += (@event) =>
                    {
                        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                        {
                            if (_selectedPackRow != null && GodotObject.IsInstanceValid(_selectedPackRow))
                            {
                                _selectedPackRow.AddThemeStyleboxOverride("panel", PackRowStyle(false));
                            }
                            _selectedPackRow = row;
                            row.AddThemeStyleboxOverride("panel", PackRowStyle(true));

                            ShowPackDetails(localFilePath);
                            Inspector.Inspect(InspectTarget.ForPack(localFilePath));
                        }
                    };
                    row.AddChild(hbox);
                    _packListVBox.AddChild(row);
                }
                catch (Exception e)
                {
                    GD.PrintErr($"Failed to read pack {file}: {e.Message}");
                }
            }
            OnDataRefreshed?.Invoke();
            CheckNewOrChangedPacks(files);
        }

        // =====================================================================
        // Pack Doctor
        // =====================================================================

        /// <summary>Opens the Pack Doctor for a zip (set by the main window).</summary>
        public Action<string> OpenDoctor { get; set; }

        // Zips seen this session (path → last write), so installs and updates are checked, but not every pack at startup.
        private static readonly Dictionary<string, DateTime> _seenPacks = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static bool _firstScanDone;

        private void CheckNewOrChangedPacks(string[] files)
        {
            foreach (var file in files)
            {
                var stamp = System.IO.File.GetLastWriteTimeUtc(file);
                bool isNewOrChanged = !_seenPacks.TryGetValue(file, out var seen) || seen != stamp;
                _seenPacks[file] = stamp;
                if (!_firstScanDone || !isNewOrChanged) continue;
                var pack = PopTrackerPackLoader.InspectZipPack(file, null);
                if (pack != null) _ = PackDoctorService.CheckAsync(pack);
            }
            _firstScanDone = true;
        }

        private void UpdateDoctorBadge(Label badge)
        {
            if (!GodotObject.IsInstanceValid(badge)) return;
            string key = badge.GetMeta("pack_key").AsString();
            if (!PackDoctorService.Reports.TryGetValue(key, out var report) || report == null)
            {
                badge.Text = "";
                return;
            }
            int needs = report.NeedsReview.Count();
            int fixes = PackFixes.Get(key).Count;
            badge.Text = needs > 0 ? $"⚠ {needs} to review" : "✔ Checked" + (fixes > 0 ? $" · {fixes} fix{(fixes == 1 ? "" : "es")}" : "");
            badge.AddThemeColorOverride("font_color", needs > 0 ? Colors.Orange : Colors.LightGreen);
        }

        private void OnDoctorReportReady(string key)
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            foreach (Node row in _packListVBox.GetChildren())
            {
                var badge = row.FindChild("DoctorBadge", true, false) as Label;
                if (badge != null && badge.GetMeta("pack_key").AsString() == key) UpdateDoctorBadge(badge);
            }
            if (_shownPackPath != null && PackFixes.KeyFor(PopTrackerPackLoader.InspectZipPack(_shownPackPath)) == key) ShowPackDetails(_shownPackPath);
        }

        private string _shownPackPath;

        private void OnOpenFolderPressed()
        {
            string dataDir = System.IO.Path.Combine(DataManager.GetDataDirectory(), "packs");
            if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
            OS.ShellOpen(dataDir);
            _logAction($"[color=yellow]Opened Packs directory.[/color]");
        }

        private async void OnSearchPressed()
        {
            var games = _getActiveGamesFunc?.Invoke() ?? new HashSet<string>();
            if (games.Count == 0)
            {
                var dialog = new Godot.AcceptDialog { Title = "No Active Games", DialogText = "Please connect to at least one Archipelago slot first so AP Atlas knows which games to search for." };
                AddChild(dialog);
                dialog.PopupCentered();
                _logAction("[color=yellow]Pack Search aborted: No active slots connected to Archipelago.[/color]");
                return;
            }

            _showOverlayAction();
            try
            {
                _logAction($"[color=cyan]Searching GitHub for map packs for {games.Count} active game(s)...[/color]");

                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "AP-Atlas-Tracker");

                HashSet<string> installedGames = new HashSet<string>();
                string packsDir = System.IO.Path.Combine(DataManager.GetDataDirectory(), "packs");
                if (System.IO.Directory.Exists(packsDir))
                {
                    foreach (var file in System.IO.Directory.GetFiles(packsDir, "*.zip"))
                    {
                        try
                        {
                            using var archive = System.IO.Compression.ZipFile.OpenRead(file);
                            var manifestEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
                            if (manifestEntry != null)
                            {
                                using var stream = manifestEntry.Open();
                                using var reader = new System.IO.StreamReader(stream);
                                string json = reader.ReadToEnd();
                                var manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<PopTracker.PopTrackerManifest>(json);
                                if (manifest != null && !string.IsNullOrEmpty(manifest.GameName))
                                {
                                    installedGames.Add(manifest.GameName);
                                }
                            }
                        }
                        catch { }
                    }
                }

                foreach (var game in games)
                {
                    bool alreadyInstalled = false;
                    foreach (var installed in installedGames)
                    {
                        if (PopTracker.PopTrackerPackLoader.IsGameNameMatch(installed, game))
                        {
                            alreadyInstalled = true;
                            break;
                        }
                    }

                    if (alreadyInstalled)
                    {
                        _logAction($"[color=green]--- {game} already has a map pack installed. Skipping search. ---[/color]");
                        continue;
                    }

                    _logAction($"[color=gray]--- Searching for {game} ---[/color]");
                    try
                    {
                        var variations = GetSearchVariations(game);
                        bool found = false;
                        Newtonsoft.Json.Linq.JArray items = null;

                        foreach (var query in variations)
                        {
                            string searchUrl = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(query)}";
                            var res = await client.GetStringAsync(searchUrl);
                            var jObject = Newtonsoft.Json.Linq.JObject.Parse(res);
                            items = jObject["items"] as Newtonsoft.Json.Linq.JArray;

                            if (items != null && items.Count > 0)
                            {
                                found = true;
                                _logAction($"[color=gray](Matched using query: {query})[/color]");
                                break;
                            }
                        }

                        if (found && items != null)
                        {
                            var firstHit = items[0];
                            string repoFullName = firstHit["full_name"]?.ToString() ?? "";
                            _logAction($"[color=lime]Found best map pack for {game}:[/color] {repoFullName}");

                            string downloadUrl = "";
                            string zipFileName = repoFullName.Replace("/", "_") + ".zip";

                            try
                            {
                                var releaseUrl = $"https://api.github.com/repos/{repoFullName}/releases/latest";
                                var releaseRes = await client.GetStringAsync(releaseUrl);
                                var releaseObj = Newtonsoft.Json.Linq.JObject.Parse(releaseRes);
                                var assets = releaseObj["assets"] as Newtonsoft.Json.Linq.JArray;
                                if (assets != null)
                                {
                                    foreach (var asset in assets)
                                    {
                                        string assetName = asset["name"]?.ToString();
                                        if (!string.IsNullOrEmpty(assetName) && assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                        {
                                            downloadUrl = asset["browser_download_url"]?.ToString();
                                            zipFileName = assetName;
                                            break;
                                        }
                                    }
                                }
                            }
                            catch { /* No releases or error fetching release */ }

                            if (string.IsNullOrEmpty(downloadUrl))
                            {
                                _logAction("[color=gray]No release zip found, falling back to repository source zip...[/color]");
                                downloadUrl = $"https://api.github.com/repos/{repoFullName}/zipball/HEAD";
                            }

                            _logAction($"[color=cyan]Downloading pack...[/color] ({downloadUrl})");
                            try
                            {
                                var reqMsg = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, downloadUrl);
                                reqMsg.Headers.Add("Accept", "application/vnd.github.v3+json");
                                var response = await client.SendAsync(reqMsg);
                                response.EnsureSuccessStatusCode();
                                var zipBytes = await response.Content.ReadAsByteArrayAsync();

                                string destDir = System.IO.Path.Combine(DataManager.GetDataDirectory(), "packs");
                                if (!System.IO.Directory.Exists(destDir)) System.IO.Directory.CreateDirectory(destDir);
                                string destPath = System.IO.Path.Combine(destDir, zipFileName);
                                // A truncated download or an error page saved as .zip would break pack loading: verify first.
                                using (var check = new System.IO.Compression.ZipArchive(new System.IO.MemoryStream(zipBytes), System.IO.Compression.ZipArchiveMode.Read))
                                {
                                    if (!check.Entries.Any(e => e.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)))
                                        throw new System.IO.InvalidDataException("the download isn't a PopTracker pack (no manifest.json)");
                                }
                                AP_Atlas.Core.SafeFile.WriteAllBytes(destPath, zipBytes);
                                _logAction($"[color=lime]Successfully auto-installed {zipFileName}![/color]");

                                // Re-render UI list
                                Callable.From(RefreshPackList).CallDeferred();
                            }
                            catch (Exception ex)
                            {
                                _logAction($"[color=red]Failed to download pack:[/color] {ex.Message}");
                            }
                        }
                        else
                        {
                            _logAction($"[color=orange]No map packs found for {game} after trying {variations.Count} variations.[/color]");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logAction($"[color=red]Search failed for {game}: {ex.Message}[/color]");
                    }
                }

            }
            finally
            {
                _hideOverlayAction();
            }
        }

        private async void OnCheckUpdatesPressed()
        {
            _logAction($"[color=cyan]Checking installed packs for updates...[/color]");
            _showOverlayAction();
            try
            {

                string dataDir = System.IO.Path.Combine(DataManager.GetDataDirectory(), "packs");
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);

                var files = Directory.GetFiles(dataDir, "*.zip");
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "AP-Atlas-Tracker");

                int checkedCount = 0;
                int updatesAvailable = 0;

                foreach (var file in files)
                {
                    try
                    {
                        using var archive = ZipFile.OpenRead(file);
                        var manifestEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
                        if (manifestEntry == null) continue;

                        using var stream = manifestEntry.Open();
                        using var reader = new StreamReader(stream);
                        var json = reader.ReadToEnd();
                        var manifest = JsonConvert.DeserializeObject<PopTrackerManifest>(json);

                        if (manifest == null || string.IsNullOrEmpty(manifest.VersionsUrl)) continue;

                        checkedCount++;
                        _logAction($"[color=gray]Checking {manifest.Name} (Current: {manifest.GetActualVersion()})...[/color]");

                        var res = await client.GetStringAsync(manifest.VersionsUrl);
                        Newtonsoft.Json.Linq.JObject latestObj = null;
                        if (res.TrimStart().StartsWith("["))
                        {
                            var versionsArray = Newtonsoft.Json.Linq.JArray.Parse(res);
                            if (versionsArray.HasValues) latestObj = versionsArray[0] as Newtonsoft.Json.Linq.JObject;
                        }
                        else if (res.TrimStart().StartsWith("{"))
                        {
                            latestObj = Newtonsoft.Json.Linq.JObject.Parse(res);
                        }

                        if (latestObj != null)
                        {
                            string latestVersion = latestObj["version"]?.ToString() ?? latestObj["tag_name"]?.ToString() ?? latestObj["name"]?.ToString();

                            if (!string.IsNullOrEmpty(latestVersion) && latestVersion != manifest.GetActualVersion())
                            {
                                string downloadUrl = latestObj?["download_url"]?.ToString();
                                if (string.IsNullOrEmpty(downloadUrl))
                                {
                                    var assets = latestObj?["assets"] as Newtonsoft.Json.Linq.JArray;
                                    if (assets != null && assets.Count > 0)
                                    {
                                        downloadUrl = assets[0]["browser_download_url"]?.ToString();
                                    }
                                    else
                                    {
                                        downloadUrl = latestObj?["html_url"]?.ToString() ?? latestObj?["url"]?.ToString() ?? "";
                                    }
                                }
                                _logAction($"[color=lime]Update available for {manifest.Name}:[/color] v{latestVersion}");
                                if (!string.IsNullOrEmpty(downloadUrl))
                                {
                                    _logAction($"[url={downloadUrl}]Download v{latestVersion}[/url]");
                                }
                                updatesAvailable++;
                            }
                            else
                            {
                                _logAction($"[color=green]{manifest.Name} is up to date.[/color]");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logAction($"[color=red]Failed to check update for {Path.GetFileName(file)}: {ex.Message}[/color]");
                    }
                }

                if (checkedCount == 0)
                {
                    _logAction("[color=orange]None of the installed packs support auto-updates (missing versions_url).[/color]");
                }
                else
                {
                    _logAction($"[color=cyan]Update check complete. {updatesAvailable} update(s) found across {checkedCount} pack(s).[/color]");
                }

            }
            finally
            {
                _hideOverlayAction();
            }
        }

        private void OnImportPackPressed()
        {
            var fd = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Title = "Select PopTracker Pack (.zip)",
                Filters = new string[] { "*.zip ; Zip Archives" },
                UseNativeDialog = true
            };

            fd.FileSelected += (path) =>
            {
                try
                {
                    string destDir = System.IO.Path.Combine(DataManager.GetDataDirectory(), "packs");
                    if (!System.IO.Directory.Exists(destDir)) System.IO.Directory.CreateDirectory(destDir);
                    string destPath = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(path));
                    System.IO.File.Copy(path, destPath, true);
                    _logAction($"[color=lime]Successfully installed map pack: {System.IO.Path.GetFileName(path)}[/color]");
                    RefreshPackList();
                }
                catch (Exception ex)
                {
                    _logAction($"[color=red]Failed to install pack: {ex.Message}[/color]");
                }
                fd.QueueFree();
            };
            fd.Canceled += () => fd.QueueFree();
            AddChild(fd);
            fd.PopupCentered();
        }

        private List<string> GetSearchVariations(string gameName)
        {
            var variations = new List<string>();
            variations.Add($"\"{gameName}\" Archipelago PopTracker");
            variations.Add($"\"{gameName}\" PopTracker");

            string roman = gameName;
            roman = Regex.Replace(roman, @"\bVIII\b", "8");
            roman = Regex.Replace(roman, @"\bVII\b", "7");
            roman = Regex.Replace(roman, @"\bVI\b", "6");
            roman = Regex.Replace(roman, @"\bIV\b", "4");
            roman = Regex.Replace(roman, @"\bV\b", "5");
            roman = Regex.Replace(roman, @"\bIII\b", "3");
            roman = Regex.Replace(roman, @"\bII\b", "2");
            roman = Regex.Replace(roman, @"\bI\b", "1");

            if (roman != gameName)
            {
                variations.Add($"\"{roman}\" PopTracker");
                variations.Add($"{roman} PopTracker");
            }

            if (gameName.Contains(":"))
            {
                string subtitle = gameName.Substring(gameName.IndexOf(":") + 1).Trim();
                if (!string.IsNullOrEmpty(subtitle))
                {
                    variations.Add($"\"{subtitle}\" PopTracker");
                }
            }

            variations.Add($"{gameName} PopTracker");
            return variations;
        }
    }
}
