#nullable disable
using Archipelago.MultiClient.Net;
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AP_Atlas.Core.PopTracker;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core
{
    public partial class MapPackManagerControl : MarginContainer
    {
        public event Action OnDataRefreshed;
        private VBoxContainer _packListVBox;
        private PanelContainer _selectedPackRow;
        // Writes a line to the System Log: plain text, and its colour (null for none).
        private Action<string, string> _logAction;
        private Action _showOverlayAction;
        private Action _hideOverlayAction;
        private Func<HashSet<string>> _getActiveGamesFunc;

        private Func<List<ArchipelagoSession>> _getActiveSessionsFunc;
        private ScrollContainer _mainScroll;
        private MarginContainer _inspectorContainer;
        public Control SidebarContent { get; private set; }

        /// <summary>The page's text size (the content setting); the window sets it.</summary>
        public Func<int> FontSize { get; set; } = () => 14;

        public MapPackManagerControl(Action<string, string> logAction, Action showOverlayAction, Action hideOverlayAction, Func<HashSet<string>> getActiveGamesFunc, Func<List<ArchipelagoSession>> getActiveSessionsFunc = null)
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

            var searchBtn = new Button { Text = "Search GitHub for Packs…", TooltipText = "Look on GitHub for map packs for your connected games. You choose what to install." };
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
            BorderColor = selected ? ThemeColors.Accent : ThemeColors.BorderSoft,
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

        /// <summary>Shows a pack's details in the inspector (internal for the UI test).</summary>
        internal void ShowPackDetails(string zipPath)
        {
            foreach (Node n in _inspectorContainer.GetChildren()) n.QueueFree();
            var pack = PopTrackerPackLoader.InspectZipPack(zipPath, null);
            if (pack == null) return;
            _shownPackPath = zipPath;
            var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            _inspectorContainer.AddChild(vbox);

            var title = new Label { Text = pack.Manifest.Name };
            title.SetMeta("font_size_ratio", 1.7f);
            vbox.AddChild(title);
            // Built after the window sized this page: sized once the rows are in.
            AP_Atlas.UI.Ui.NextFrame(this, () => MainTrackerWindow.SetFontSizeRecursive(vbox, FontSize()));

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

            var richText = new AP_Atlas.UI.SafeRichText { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            string txt = $"[b]Game:[/b] {Bbcode.Escape(pack.Manifest.GameName)}\n";
            txt += $"[b]Author:[/b] {Bbcode.Escape(pack.Manifest.Author)}\n";
            txt += $"[b]Version:[/b] {Bbcode.Escape(pack.Manifest.GetActualVersion())}\n\n";

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
                    : Bbcode.Colored($"Checked against {report.Names.Game} names from the {report.Names.Source}.", "gray") + "\n";
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
                AP_Atlas.Core.Async.Fire(PackDoctorService.CheckAsync(pack, prompt: false), "checking a map pack");
            }
            _ = activeSession; // the Doctor uses every name source, not just a connected session

            txt += $"\n[b]--- MAP LIST ---[/b]\n";
            foreach (var map in pack.Maps.Values)
            {
                txt += $" - {Bbcode.Escape(map.Name)} " + Bbcode.Colored($"({map.Id})", "gray") + "\n";
            }

            richText.Markup = txt;
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
            deleteBtn.AddThemeColorOverride("font_color", ThemeColors.Danger);
            deleteBtn.Pressed += () => AP_Atlas.UI.Dialogs.Confirm(this, "Delete map pack",
                $"Delete {pack.Manifest.Name} ({Path.GetFileName(zipPath)}) from Atlas's packs folder? Fixes you made in the Pack Doctor are kept, in case you install it again.",
                "Delete", () =>
                {
                    try
                    {
                        SafeFile.Delete(zipPath);
                    }
                    catch (Exception ex)
                    {
                        _logAction($"Couldn't delete the map pack: {ex.Message}", "red");
                        return;
                    }
                    _logAction($"Deleted the map pack {Path.GetFileName(zipPath)}.", null);
                    PopTrackerPackLoader.NotifyPacksChanged();
                    RefreshPackList();
                    foreach (Node n in _inspectorContainer.GetChildren()) n.QueueFree();
                    _inspectorContainer.AddChild(new Label { Text = "Select a map pack to view details.", HorizontalAlignment = HorizontalAlignment.Center });
                });
            btnHBox.AddChild(deleteBtn);

            vbox.AddChild(btnHBox);
            OnDataRefreshed?.Invoke();
        }

        // =====================================================================
        // Updates: each pack's own versions link (its author's update feed), read only when the user asks
        // =====================================================================

        private sealed class PackUpdate
        {
            public string Pack, Current, Latest, DownloadUrl, Sha256;
        }

        private static PopTrackerManifest ReadManifest(string zipPath)
        {
            using var archive = SafeZip.Open(zipPath);
            var manifestEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
            if (manifestEntry == null) return null;
            return JsonConvert.DeserializeObject<PopTrackerManifest>(archive.ReadText(manifestEntry));
        }

        /// <summary>
        /// Reads a pack's versions link and says whether a newer version exists. Understands PopTracker's versions.json
        /// ({"versions":[{"package_version", "download_url", "sha256"}]}, newest first), a plain list, and a GitHub release.
        /// </summary>
        private static async Task<(PackUpdate Update, string Problem)> CheckPackUpdateAsync(PopTrackerManifest manifest)
        {
            string host = Uri.TryCreate(manifest.VersionsUrl, UriKind.Absolute, out var u) ? u.Host : "the pack's site";
            var r = await PoliteHttp.GetAsync(manifest.VersionsUrl, host, accept: "application/json", maxBytes: 1024 * 1024, timeout: TimeSpan.FromSeconds(20));
            if (!r.Ok) return (null, r.Message);
            JToken root;
            try { root = JToken.Parse(r.Text); }
            catch (Exception ex) { return (null, $"its versions file couldn't be read ({ex.Message})"); }
            JToken latest = root is JObject o && o["versions"] is JArray list ? list.FirstOrDefault()
                : root is JArray array ? array.FirstOrDefault()
                : root;
            string version = (latest?["package_version"] ?? latest?["version"] ?? latest?["tag_name"] ?? latest?["name"])?.ToString();
            if (string.IsNullOrWhiteSpace(version)) return (null, "its versions file doesn't name a version");
            string current = manifest.GetActualVersion();
            if (CompareVersions(version, current) <= 0) return (null, null);
            string url = latest?["download_url"]?.ToString();
            if (string.IsNullOrEmpty(url) && latest?["assets"] is JArray assets)
                url = assets.FirstOrDefault(a => (a["name"]?.ToString() ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase))?["browser_download_url"]?.ToString();
            if (string.IsNullOrEmpty(url)) url = latest?["html_url"]?.ToString();
            return (new PackUpdate { Pack = manifest.Name, Current = current, Latest = version, DownloadUrl = url, Sha256 = latest?["sha256"]?.ToString() }, null);
        }

        /// <summary>Compares versions by their numbers ("1.10" is newer than "1.9"; a leading "v" is ignored).</summary>
        internal static int CompareVersions(string a, string b)
        {
            var x = Regex.Matches(a ?? "", @"\d+").Select(m => long.TryParse(m.Value, out var n) ? n : 0).ToList();
            var y = Regex.Matches(b ?? "", @"\d+").Select(m => long.TryParse(m.Value, out var n) ? n : 0).ToList();
            if (x.Count == 0 || y.Count == 0) return string.Equals((a ?? "").Trim().TrimStart('v', 'V'), (b ?? "").Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            for (int k = 0; k < Math.Max(x.Count, y.Count); k++)
            {
                long p = k < x.Count ? x[k] : 0, q = k < y.Count ? y[k] : 0;
                if (p != q) return p.CompareTo(q);
            }
            return 0;
        }

        private void OfferDownloadPage(PackUpdate update)
        {
            if (string.IsNullOrEmpty(update.DownloadUrl) || AP_Atlas.Core.ExternalLinks.CheckWeb(update.DownloadUrl, out _) != null)
            {
                _logAction($"{update.Pack} {update.Latest} is available (you have {update.Current}); its versions file has no download page Atlas can open.", "lime");
                return;
            }
            string host = new Uri(update.DownloadUrl).Host;
            AP_Atlas.UI.Dialogs.Confirm(this, "Map pack update",
                $"{update.Pack} {update.Latest} is available (you have {update.Current}).\n\nOpen its download page on {host} in your browser? " +
                "After downloading, install it with Install Pack (Zip). (Installing updates inside Atlas is coming.)",
                "Open download page", () => AP_Atlas.Core.ExternalLinks.OpenWeb(update.DownloadUrl));
        }

        private void CheckSinglePackUpdate(string zipPath) => AP_Atlas.Core.Async.Fire(CheckSinglePackUpdateAsync(zipPath), "checking a map pack for updates");

        private async Task CheckSinglePackUpdateAsync(string zipPath)
        {
            PopTrackerManifest manifest;
            try { manifest = ReadManifest(zipPath); }
            catch (Exception ex) { _logAction($"Couldn't read the pack: {ex.Message}", "red"); return; }
            if (manifest == null) { _logAction("This pack has no manifest.json.", "red"); return; }
            if (string.IsNullOrEmpty(manifest.VersionsUrl)) { _logAction($"{manifest.Name} doesn't publish updates (its manifest has no versions_url).", "yellow"); return; }
            _logAction($"Checking {manifest.Name} for updates…", null);
            var (update, problem) = await CheckPackUpdateAsync(manifest);
            if (!GodotObject.IsInstanceValid(this)) return;
            if (problem != null) _logAction($"Couldn't check {manifest.Name} for updates: {problem}", "orange");
            else if (update == null) _logAction($"{manifest.Name} is up to date (v{manifest.GetActualVersion()}).", "green");
            else OfferDownloadPage(update);
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
        /// Rebuilds the pack list. Packs that haven't been read yet are read on a worker thread first, so opening the tab
        /// doesn't freeze the UI. Only their structure is read: their images are decoded only while a slot or the Pack
        /// Doctor window uses the pack (PackImages).
        /// </summary>
        private void RefreshPackList() => AP_Atlas.Core.Async.Fire(RefreshPackListAsync(), "reading your map packs");

        private async Task RefreshPackListAsync()
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
                    loading.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
                    _packListVBox.AddChild(loading);
                    _logAction($"Reading {uncached.Count} map pack(s) in the background...", "gray");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await Task.Run(() =>
                    {
                        foreach (var f in uncached) PopTrackerPackLoader.InspectZipPack(f, null);
                    });
                    if (!GodotObject.IsInstanceValid(this)) return;
                    _logAction($"Map packs ready ({sw.ElapsedMilliseconds} ms).", "gray");
                }
                // A pack copied into the folder by hand (seen at a rescan) reaches connected slots too.
                string signature = PackFolderSignature(files);
                bool changed = _packListSignature != null && signature != _packListSignature;
                _packListSignature = signature;
                if (changed) PopTrackerPackLoader.NotifyPacksChanged();
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
            AP_Atlas.UI.Ui.NextFrame(this, () => MainTrackerWindow.SetFontSizeRecursive(_packListVBox, FontSize()));

            if (files.Length == 0)
            {
                var lbl = new Label { Text = "No map packs installed.", HorizontalAlignment = HorizontalAlignment.Center };
                lbl.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
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
                    titleLbl.SetMeta("font_size_ratio", 1.15f);
                    titleLbl.AddThemeColorOverride("font_color", ThemeColors.Text);
                    infoVBox.AddChild(titleLbl);

                    var detailLbl = new Label { Text = $"Game: {manifest.GameName} | v{manifest.GetActualVersion()}" };
                    detailLbl.SetMeta("font_size_ratio", 0.85f);
                    detailLbl.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
                    infoVBox.AddChild(detailLbl);

                    var capsHbox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    capsHbox.AddThemeConstantOverride("separation", 15);
                    var itemIndicator = new Label { Text = $"Items: {pack.ItemsByCode.Count}" };
                    itemIndicator.SetMeta("font_size_ratio", 0.8f);
                    itemIndicator.AddThemeColorOverride("font_color", pack.ItemsByCode.Count > 0 ? ThemeColors.Success : ThemeColors.TextSubtle);
                    capsHbox.AddChild(itemIndicator);
                    var mapIndicator = new Label { Text = $"Maps: {pack.Maps.Count}" };
                    mapIndicator.SetMeta("font_size_ratio", 0.8f);
                    mapIndicator.AddThemeColorOverride("font_color", pack.Maps.Count > 0 ? ThemeColors.Success : ThemeColors.TextSubtle);
                    capsHbox.AddChild(mapIndicator);
                    var locIndicator = new Label { Text = $"Locs: {pack.Locations.Count}" };
                    locIndicator.SetMeta("font_size_ratio", 0.8f);
                    locIndicator.AddThemeColorOverride("font_color", pack.Locations.Count > 0 ? ThemeColors.Success : ThemeColors.TextSubtle);
                    capsHbox.AddChild(locIndicator);
                    // Pack Doctor status (filled in when a check finishes).
                    var doctorBadge = new Label { Name = "DoctorBadge" };
                    doctorBadge.SetMeta("font_size_ratio", 0.8f);
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
                if (pack != null) AP_Atlas.Core.Async.Fire(PackDoctorService.CheckAsync(pack), "checking a map pack");
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
            badge.AddThemeColorOverride("font_color", needs > 0 ? ThemeColors.Warning : ThemeColors.Success);
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
            AP_Atlas.Core.ExternalLinks.OpenFolder(dataDir);
        }

        // =====================================================================
        // Finding packs on GitHub: only when asked, at most two searches per game, nothing installed without the user's OK
        // =====================================================================

        private sealed class PackCandidate
        {
            public string Repo, Description, HtmlUrl;
            public int Stars;
            public DateTime? Updated;
        }

        private static HashSet<string> InstalledPackGames()
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in GetPackFiles())
            {
                try
                {
                    var manifest = ReadManifest(file);
                    if (!string.IsNullOrEmpty(manifest?.GameName)) installed.Add(manifest.GameName);
                }
                catch (Exception ex) { Logger.LogDebug($"Skipped the map pack {Path.GetFileName(file)}: {ex.Message}"); }
            }
            return installed;
        }

        private void OnSearchPressed() => AP_Atlas.Core.Async.Fire(OnSearchPressedAsync(), "searching GitHub for map packs");

        private async Task OnSearchPressedAsync()
        {
            var games = (_getActiveGamesFunc?.Invoke() ?? new HashSet<string>()).ToList();
            if (games.Count == 0)
            {
                AP_Atlas.UI.Dialogs.Confirm(this, "Search GitHub for map packs", "Connect at least one slot first, so Atlas knows which games to look for.", "OK", () => { });
                return;
            }
            var installed = InstalledPackGames();
            var missing = games.Where(g => !installed.Any(i => PopTrackerPackLoader.IsGameNameMatch(i, g))).OrderBy(g => g).ToList();
            foreach (var g in games.Except(missing)) _logAction($"{g} already has a map pack.", "green");
            if (missing.Count == 0) return;

            var results = new List<(string Game, List<PackCandidate> Candidates, string Problem)>();
            _showOverlayAction();
            try
            {
                foreach (var game in missing)
                {
                    var (candidates, problem) = await SearchPacksAsync(game);
                    results.Add((game, candidates, problem));
                    // GitHub allows 10 anonymous searches a minute: stop once it says the limit is used up.
                    if (problem != null && problem.Contains("limit")) break;
                }
            }
            finally
            {
                _hideOverlayAction();
            }
            if (!GodotObject.IsInstanceValid(this)) return;
            ShowPackCandidates(results, missing.Count);
        }

        private static async Task<(List<PackCandidate> Candidates, string Problem)> SearchPacksAsync(string game)
        {
            var list = new List<PackCandidate>();
            foreach (var query in SearchQueries(game))
            {
                var answer = await GitHubApi.GetAsync($"/search/repositories?q={Uri.EscapeDataString(query)}&per_page=8");
                if (!answer.Ok) return (list, answer.Message);
                foreach (var item in answer.Json?["items"] as JArray ?? new JArray())
                {
                    string repo = item["full_name"]?.ToString();
                    if (repo == null || list.Any(c => c.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(new PackCandidate
                    {
                        Repo = repo,
                        Description = item["description"]?.ToString() ?? "",
                        HtmlUrl = item["html_url"]?.ToString(),
                        Stars = item["stargazers_count"]?.Value<int?>() ?? 0,
                        Updated = item["pushed_at"]?.Value<DateTime?>()
                    });
                }
                if (list.Count > 0) break;
            }
            return (list, null);
        }

        /// <summary>At most two searches per game: the exact name, then a looser form (digits for roman numerals, the subtitle).</summary>
        private static List<string> SearchQueries(string game)
        {
            var queries = new List<string> { $"\"{game}\" poptracker" };
            string loose = game;
            loose = Regex.Replace(loose, @"\bVIII\b", "8");
            loose = Regex.Replace(loose, @"\bVII\b", "7");
            loose = Regex.Replace(loose, @"\bVI\b", "6");
            loose = Regex.Replace(loose, @"\bIV\b", "4");
            loose = Regex.Replace(loose, @"\bV\b", "5");
            loose = Regex.Replace(loose, @"\bIII\b", "3");
            loose = Regex.Replace(loose, @"\bII\b", "2");
            if (loose.Contains(':')) loose = loose.Substring(loose.IndexOf(':') + 1).Trim();
            queries.Add(loose.Length > 0 && !string.Equals(loose, game, StringComparison.OrdinalIgnoreCase) ? $"\"{loose}\" poptracker" : $"{game} poptracker");
            return queries;
        }

        private void ShowPackCandidates(List<(string Game, List<PackCandidate> Candidates, string Problem)> results, int wanted)
        {
            var dialog = new AcceptDialog { Title = "Map packs on GitHub", OkButtonText = "Close", MinSize = new Vector2I(760, 520) };
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(720, 460), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 10);
            scroll.AddChild(box);
            dialog.AddChild(scroll);
            box.AddChild(new Label
            {
                Text = "GitHub's search results. Nothing is installed until you choose a pack. Packs are made by the community: check who made it before installing.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(680, 0)
            });
            foreach (var (game, candidates, problem) in results)
            {
                var heading = new Label { Text = game };
                heading.AddThemeColorOverride("font_color", ThemeColors.Accent.Lightened(0.2f));
                box.AddChild(heading);
                if (problem != null) box.AddChild(Muted($"GitHub couldn't be searched: {problem}"));
                else if (candidates.Count == 0) box.AddChild(Muted("No map packs found on GitHub. Packs are often shared in the game's Archipelago Discord thread; install one from its zip with Install Pack (Zip)."));
                foreach (var c in candidates)
                {
                    var row = new HBoxContainer();
                    row.AddThemeConstantOverride("separation", 8);
                    string updated = c.Updated.HasValue ? $" · updated {c.Updated.Value.ToLocalTime():yyyy-MM-dd}" : "";
                    var info = new Label
                    {
                        Text = $"{c.Repo}  ★{c.Stars}{updated}" + (string.IsNullOrWhiteSpace(c.Description) ? "" : "\n" + c.Description),
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                        SizeFlagsHorizontal = SizeFlags.ExpandFill
                    };
                    row.AddChild(info);
                    string repo = c.Repo, g = game, page = c.HtmlUrl;
                    var install = new Button { Text = "Install…", TooltipText = "See what would be downloaded, then confirm" };
                    install.Pressed += () => InstallFromRepo(g, repo);
                    row.AddChild(install);
                    if (page != null)
                    {
                        var view = new Button { Text = "Open ↗", TooltipText = "Open the project on GitHub in your browser" };
                        view.Pressed += () => AP_Atlas.Core.ExternalLinks.OpenWeb(page);
                        row.AddChild(view);
                    }
                    box.AddChild(row);
                }
            }
            if (results.Count < wanted) box.AddChild(Muted("GitHub's search limit was reached before every game was searched. Search again in a minute for the rest."));
            dialog.Confirmed += () => dialog.QueueFree();
            dialog.Canceled += () => dialog.QueueFree();
            AddChild(dialog);
            dialog.PopupCentered();
        }

        private static Label Muted(string text)
        {
            var l = AP_Atlas.UI.Kit.Subtle(text);
            l.CustomMinimumSize = new Vector2(680, 0);
            return l;
        }

        /// <summary>Looks up the project's latest release, then shows exactly what would be downloaded and asks.</summary>
        private void InstallFromRepo(string game, string repo) => AP_Atlas.Core.Async.Fire(InstallFromRepoAsync(game, repo), $"looking up {repo}'s latest release");

        private async Task InstallFromRepoAsync(string game, string repo)
        {
            GitHubApi.Result release;
            _showOverlayAction();
            try { release = await GitHubApi.GetAsync($"/repos/{repo}/releases/latest"); }
            finally { _hideOverlayAction(); }
            if (!GodotObject.IsInstanceValid(this)) return;
            if (release.Unknown)
            {
                _logAction($"Couldn't read {repo}'s releases: {release.Message}", "orange");
                return;
            }
            string where = "github.com/" + repo, url = null, fileName = null, text;
            long size = 0;
            if (release.Ok)
            {
                foreach (var asset in release.Json?["assets"] as JArray ?? new JArray())
                {
                    string name = asset["name"]?.ToString() ?? "";
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    url = asset["browser_download_url"]?.ToString();
                    fileName = name;
                    size = asset["size"]?.Value<long?>() ?? 0;
                    break;
                }
            }
            if (url != null)
                text = $"Download {fileName} ({size / (1024.0 * 1024.0):0.0} MB) from {where}, release {release.Json?["tag_name"]}, and install it as a map pack for {game}?";
            else
            {
                url = $"{GitHubApi.Site}/repos/{repo}/zipball";
                fileName = repo.Replace('/', '_') + ".zip";
                text = $"{where} has no released pack. Download the project's current files instead and install them as a map pack for {game}? Unreleased files may be unfinished.";
            }
            text += "\n\nMap packs are made by the community, and their scripts run inside Atlas's sandbox. Only install packs from projects you trust.";
            string downloadUrl = url, file = fileName;
            AP_Atlas.UI.Dialogs.Confirm(this, "Install a map pack", text, "Download and install", () => DownloadPack(downloadUrl, file, where));
        }

        private void DownloadPack(string url, string fileName, string where) => AP_Atlas.Core.Async.Fire(DownloadPackAsync(url, fileName, where), "downloading a map pack");

        private async Task DownloadPackAsync(string url, string fileName, string where)
        {
            string packsDir = System.IO.Path.Combine(DataManager.GetDataDirectory(), "packs");
            Directory.CreateDirectory(packsDir);
            string safeName = string.Concat((fileName ?? "pack.zip").Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            if (!safeName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) safeName += ".zip";
            string dest = System.IO.Path.Combine(packsDir, safeName);
            string temp = System.IO.Path.Combine(packsDir, $".download-{Guid.NewGuid():N}.tmp");
            WebResponse r;
            _showOverlayAction();
            try { r = await PoliteHttp.DownloadAsync(url, where, temp, null, 256L * 1024 * 1024, TimeSpan.FromMinutes(10)); }
            finally { _hideOverlayAction(); }
            if (!r.Ok)
            {
                PoliteHttp.TryDelete(temp);
                if (GodotObject.IsInstanceValid(this)) _logAction($"Couldn't download the pack: {r.Message}", "red");
                return;
            }
            string problem = CheckPackZip(temp);
            if (problem != null)
            {
                PoliteHttp.TryDelete(temp);
                if (GodotObject.IsInstanceValid(this)) _logAction($"The download from {where} wasn't installed: {problem}.", "red");
                return;
            }
            try { File.Move(temp, dest, true); }
            catch (Exception ex)
            {
                PoliteHttp.TryDelete(temp);
                if (GodotObject.IsInstanceValid(this)) _logAction($"Couldn't install the pack: {ex.Message}", "red");
                return;
            }
            if (!GodotObject.IsInstanceValid(this)) return;
            _logAction($"Installed {safeName} from {where}.", "lime");
            PopTrackerPackLoader.NotifyPacksChanged();
            await RefreshPackListAsync();
        }

        /// <summary>Why a file isn't a usable PopTracker pack zip, or null when it is.</summary>
        internal static string CheckPackZip(string path)
        {
            try
            {
                using var zip = SafeZip.Open(path);
                if (!zip.Entries.Any(e => e.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))) return "it isn't a PopTracker pack (it has no manifest.json)";
                if (zip.Entries.Sum(e => e.Length) > 1024L * 1024 * 1024) return "it unpacks to more than 1 GB";
                return null;
            }
            catch (Exception ex)
            {
                return "it isn't a readable zip file (" + ex.Message + ")";
            }
        }

        private void OnCheckUpdatesPressed() => AP_Atlas.Core.Async.Fire(OnCheckUpdatesPressedAsync(), "checking map packs for updates");

        private async Task OnCheckUpdatesPressedAsync()
        {
            var updates = new List<PackUpdate>();
            var problems = new List<string>();
            int checkedCount = 0;
            _showOverlayAction();
            try
            {
                foreach (var file in GetPackFiles())
                {
                    PopTrackerManifest manifest;
                    try { manifest = ReadManifest(file); }
                    catch { continue; }
                    if (manifest == null || string.IsNullOrEmpty(manifest.VersionsUrl)) continue;
                    checkedCount++;
                    var (update, problem) = await CheckPackUpdateAsync(manifest);
                    if (problem != null) problems.Add($"{manifest.Name}: {problem}");
                    else if (update != null) updates.Add(update);
                }
            }
            finally
            {
                _hideOverlayAction();
            }
            if (!GodotObject.IsInstanceValid(this)) return;
            if (checkedCount == 0)
            {
                _logAction("None of the installed packs publish updates (no versions_url in their manifests).", "orange");
                return;
            }
            _logAction($"Update check: {updates.Count} update(s) among {checkedCount} pack(s)" + (problems.Count > 0 ? $"; {problems.Count} couldn't be checked." : "."), null);
            var dialog = new AcceptDialog { Title = "Map pack updates", OkButtonText = "Close", MinSize = new Vector2I(640, 300) };
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            dialog.AddChild(box);
            if (updates.Count == 0) box.AddChild(new Label { Text = $"All {checkedCount} pack(s) that publish updates are up to date." });
            foreach (var u in updates)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                row.AddChild(new Label { Text = $"{u.Pack}: {u.Current} → {u.Latest}", SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart });
                if (!string.IsNullOrEmpty(u.DownloadUrl) && AP_Atlas.Core.ExternalLinks.CheckWeb(u.DownloadUrl, out _) == null)
                {
                    var open = new Button { Text = "Open download page ↗", TooltipText = u.DownloadUrl };
                    var update = u;
                    open.Pressed += () => AP_Atlas.Core.ExternalLinks.OpenWeb(update.DownloadUrl);
                    row.AddChild(open);
                }
                box.AddChild(row);
            }
            foreach (var p in problems) box.AddChild(Muted("Couldn't check " + p));
            dialog.Confirmed += () => dialog.QueueFree();
            dialog.Canceled += () => dialog.QueueFree();
            AddChild(dialog);
            dialog.PopupCentered();
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
                fd.QueueFree();
                string problem = CheckPackZip(path);
                if (problem != null)
                {
                    _logAction($"{System.IO.Path.GetFileName(path)} wasn't installed: {problem}.", "red");
                    return;
                }
                string destDir = System.IO.Path.Combine(DataManager.GetDataDirectory(), "packs");
                Directory.CreateDirectory(destDir);
                string destPath = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(path));
                void Install()
                {
                    try
                    {
                        if (string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase)) return;
                        string temp = destPath + ".importing";
                        System.IO.File.Copy(path, temp, true);
                        System.IO.File.Move(temp, destPath, true);
                        _logAction($"Installed the map pack {System.IO.Path.GetFileName(path)}.", "lime");
                        PopTrackerPackLoader.NotifyPacksChanged();
                        RefreshPackList();
                    }
                    catch (Exception ex)
                    {
                        _logAction($"Couldn't install the pack: {ex.Message}", "red");
                    }
                }
                if (System.IO.File.Exists(destPath))
                    AP_Atlas.UI.Dialogs.Confirm(this, "Replace map pack", $"A pack named {System.IO.Path.GetFileName(path)} is already installed. Replace it?", "Replace", Install);
                else Install();
            };
            fd.Canceled += () => fd.QueueFree();
            AddChild(fd);
            fd.PopupCentered();
        }
    }
}
