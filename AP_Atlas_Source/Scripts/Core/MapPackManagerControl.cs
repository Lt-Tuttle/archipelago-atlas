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

        /// <summary>Shows a card (the window's alert feed); the window sets it.</summary>
        public Action<string, Color> Toast { get; set; }

        private HBoxContainer _downloadRow;
        private Label _downloadLabel;
        private ProgressBar _downloadBar;

        /// <summary>Whether a pack is downloading now (for tests).</summary>
        public bool Downloading => _downloadRow != null && _downloadRow.Visible;

        /// <summary>The last progress line shown for a download (for tests).</summary>
        public string DownloadText { get; private set; } = "";

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

            // The page's main action, filled with the accent (the owner's choice, 2026-10-09); the rest plain.
            var searchBtn = new Button { Text = "Search GitHub for Packs…", TooltipText = "Look on GitHub for map packs for your connected games. You choose what to install.", ThemeTypeVariation = AP_Atlas.UI.Kit.PrimaryButton };
            searchBtn.Pressed += OnSearchPressed;
            btnVBox.AddChild(searchBtn);

            var updatesBtn = new Button { Text = "Check for Updates" };
            updatesBtn.Pressed += OnCheckUpdatesPressed;
            btnVBox.AddChild(updatesBtn);

            var importBtn = new Button { Text = "Install Pack (Zip)" };
            importBtn.Pressed += OnImportPackPressed;
            btnVBox.AddChild(importBtn);

            var folderBtn = new Button { Text = "Open Packs Folder" };
            folderBtn.Pressed += OnOpenFolderPressed;
            btnVBox.AddChild(folderBtn);

            var rescanBtn = new Button { Text = "Rescan Packs" };
            rescanBtn.Pressed += RefreshPackList;
            btnVBox.AddChild(rescanBtn);

            sidebarVBox.AddChild(new HSeparator());
            sidebarVBox.AddChild(btnVBox);

            // A download's progress: "Downloading <pack>: 3 of 11 MB" with a bar, in place of the whole-window overlay.
            _downloadRow = new HBoxContainer { Visible = false };
            _downloadRow.AddThemeConstantOverride("separation", 8);
            _downloadLabel = AP_Atlas.UI.Kit.Muted("");
            _downloadLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _downloadLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _downloadRow.AddChild(_downloadLabel);
            _downloadBar = new ProgressBar { MaxValue = 1, Step = 0.001, ShowPercentage = false, CustomMinimumSize = new Vector2(100, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            _downloadRow.AddChild(_downloadBar);
            sidebarVBox.AddChild(_downloadRow);

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
            doctorBtn.Pressed += () => OpenDoctor?.Invoke(zipPath, null);
            btnHBox.AddChild(doctorBtn);

            var updateBtn = new Button { Text = "Update Map Pack", CustomMinimumSize = new Vector2(200, 40) };
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

                    // Two lines: the name and its game; then the version, what it holds, the Doctor's word and the buttons (which wrap when the list is narrow).
                    var titleRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    titleRow.AddThemeConstantOverride("separation", 10);
                    var titleLbl = new Label { Text = manifest.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
                    titleLbl.SetMeta("font_size_ratio", 1.15f);
                    titleLbl.AddThemeColorOverride("font_color", ThemeColors.Text);
                    titleRow.AddChild(titleLbl);
                    var gameLbl = new Label { Text = manifest.GameName, SizeFlagsVertical = SizeFlags.ShrinkCenter };
                    gameLbl.SetMeta("font_size_ratio", 0.85f);
                    gameLbl.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
                    titleRow.AddChild(gameLbl);
                    infoVBox.AddChild(titleRow);

                    var capsHbox = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    capsHbox.AddThemeConstantOverride("h_separation", 12);
                    capsHbox.AddThemeConstantOverride("v_separation", 4);
                    var detailLbl = new Label { Text = $"v{manifest.GetActualVersion()} · {pack.ItemsByCode.Count} items · {pack.Maps.Count} maps · {pack.Locations.Count} pins", SizeFlagsVertical = SizeFlags.ShrinkCenter };
                    detailLbl.SetMeta("font_size_ratio", 0.8f);
                    detailLbl.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
                    capsHbox.AddChild(detailLbl);
                    // Pack Doctor status (filled in when a check finishes).
                    string key = PackFixes.KeyFor(pack);
                    var doctorBadge = new Label { Name = "DoctorBadge", SizeFlagsVertical = SizeFlags.ShrinkCenter };
                    doctorBadge.SetMeta("font_size_ratio", 0.8f);
                    doctorBadge.SetMeta("pack_key", key);
                    capsHbox.AddChild(doctorBadge);
                    // What the check left to decide, straight to the Doctor's Recommended tab (its Overview when nothing is suggested).
                    string reviewFile = file;
                    var reviewBtn = AP_Atlas.UI.Kit.Button("", Tr("Opens the Pack Doctor on what's left to decide."), () => OpenDoctor?.Invoke(reviewFile, ReviewTab(key)), small: true);
                    reviewBtn.Name = "ReviewButton";
                    reviewBtn.Visible = false;
                    reviewBtn.SetMeta("pack_key", key);
                    capsHbox.AddChild(reviewBtn);
                    // One click: the confident matches linked, the rest set aside (the window says what was done).
                    var fixBtn = AP_Atlas.UI.Kit.Button(Tr("Fix"), Tr("Links every pin and tile whose best match is confident (85% or more, no clash) and sets the rest aside, in one step; Undo in the Pack Doctor reverses it."), () => FixPack?.Invoke(key), small: true);
                    fixBtn.Name = "FixButton";
                    fixBtn.Visible = false;
                    fixBtn.SetMeta("pack_key", key);
                    capsHbox.AddChild(fixBtn);
                    UpdateDoctorBadge(doctorBadge, reviewBtn, fixBtn);
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

        /// <summary>Opens the Pack Doctor for a zip, on a tab (null: its first); set by the main window.</summary>
        public Action<string, string> OpenDoctor { get; set; }

        /// <summary>Makes a pack (its zip's file name) the one a game's slots use: set by the window (the settings' PreferredPacks).</summary>
        public Action<string, string> PreferPack { get; set; }

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

        /// <summary>"Fix what Atlas can" for a pack by its key (set by the window; the result shows as a card).</summary>
        public Action<string> FixPack { get; set; }

        private void UpdateDoctorBadge(Label badge, Button review, Button fix = null)
        {
            if (!GodotObject.IsInstanceValid(badge)) return;
            bool reviewValid = review != null && GodotObject.IsInstanceValid(review);
            bool fixValid = fix != null && GodotObject.IsInstanceValid(fix);
            string key = badge.GetMeta("pack_key").AsString();
            if (!PackDoctorService.Reports.TryGetValue(key, out var report) || report == null)
            {
                badge.Text = "";
                if (reviewValid) review.Visible = false;
                if (fixValid) fix.Visible = false;
                return;
            }
            int needs = report.NeedsReview.Count();
            int fixes = PackFixes.Get(key).Count;
            badge.Text = needs > 0 ? $"⚠ {needs} to review" : "✔ Checked" + (fixes > 0 ? $" · {fixes} fix{(fixes == 1 ? "" : "es")}" : "");
            badge.AddThemeColorOverride("font_color", needs > 0 ? ThemeColors.Warning : ThemeColors.Success);
            if (reviewValid)
            {
                review.Visible = needs > 0;
                review.Text = Tr("Review {0}").Replace("{0}", needs.ToString());
            }
            if (fixValid) fix.Visible = report.Findings.Any(f => !f.Ignored && (f.Actions.HasFlag(FindingActions.LinkItem) || f.Actions.HasFlag(FindingActions.LinkLocation)));
        }

        /// <summary>A pack row's Fix button, by the pack's key (for tests).</summary>
        internal Button FixButtonFor(string key) =>
            _packListVBox.GetChildren().Select(row => row.FindChild("FixButton", true, false) as Button).FirstOrDefault(b => b != null && b.GetMeta("pack_key").AsString() == key);

        /// <summary>The Doctor's tab for a pack's review: Recommended while it suggests matches, else the Overview.</summary>
        private static string ReviewTab(string key) =>
            PackDoctorService.Reports.TryGetValue(key, out var report) && report != null && PackDoctor.Recommendations(report).Count > 0 ? "Recommended" : "Overview";

        /// <summary>A pack row's Review button, by the pack's key (for tests).</summary>
        internal Button ReviewButtonFor(string key) =>
            _packListVBox.GetChildren().Select(row => row.FindChild("ReviewButton", true, false) as Button).FirstOrDefault(b => b != null && b.GetMeta("pack_key").AsString() == key);

        private void OnDoctorReportReady(string key)
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            foreach (Node row in _packListVBox.GetChildren())
            {
                var badge = row.FindChild("DoctorBadge", true, false) as Label;
                if (badge != null && badge.GetMeta("pack_key").AsString() == key) UpdateDoctorBadge(badge, row.FindChild("ReviewButton", true, false) as Button, row.FindChild("FixButton", true, false) as Button);
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

        internal sealed class PackCandidate
        {
            public string Repo, Description, HtmlUrl;
            public int Stars;
            public DateTime? Updated;
            /// <summary>A fork of another project: often how a pack is kept up with a new apworld.</summary>
            public bool Fork;
            /// <summary>The game's own setup guide links this project as its tracker: the author's recommendation.</summary>
            public bool Recommended;
        }

        /// <summary>One of a project's releases that holds a pack (for the choice before a download).</summary>
        internal sealed class PackRelease
        {
            public string Tag, Name, Notes, Url, FileName;
            public long Size;
            public bool Prerelease;
            public DateTime? Published;
            /// <summary>The tag, and the release's name when it says more ("v1.0.6 · for v3.0.13").</summary>
            public string Label => string.IsNullOrWhiteSpace(Name) || Name.Trim() == Tag ? Tag : $"{Tag} · {Name.Trim()}";
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
            await SearchGamesAsync(missing);
        }

        /// <summary>
        /// Searches GitHub for one game's PopTracker packs and shows what it found (nothing installed until chosen): the Map
        /// Tracker's empty state and a game's page call it, so a slot without a pack is one press from the search, connected
        /// or not. One search, because the user pressed for it.
        /// </summary>
        public void SearchForGame(string game)
        {
            if (string.IsNullOrWhiteSpace(game)) return;
            AP_Atlas.Core.Async.Fire(SearchGamesAsync(new List<string> { game }), $"searching GitHub for {game} map packs");
        }

        private async Task SearchGamesAsync(List<string> missing)
        {
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

        /// <summary>
        /// A game's packs on GitHub: the project its own setup guide recommends first (marked), then what GitHub's search
        /// finds. The guide's project is kept even when the search can't be made, and looked up once for its stars and
        /// description when the search didn't return it.
        /// </summary>
        internal static async Task<(List<PackCandidate> Candidates, string Problem)> SearchPacksAsync(string game)
        {
            var list = new List<PackCandidate>();
            var (found, problem) = await SearchGitHubAsync(game);
            list.AddRange(found);
            foreach (var (repo, _) in GuideRecommendations(game).AsEnumerable().Reverse())
            {
                var known = list.FirstOrDefault(c => c.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));
                if (known == null)
                {
                    known = new PackCandidate { Repo = repo, Description = "", HtmlUrl = "https://github.com/" + repo };
                    var project = await GitHubApi.GetAsync($"/repos/{repo}");
                    if (project.Ok && project.Json is JObject item)
                    {
                        known.Repo = item["full_name"]?.ToString() ?? repo;
                        known.Description = item["description"]?.ToString() ?? "";
                        known.HtmlUrl = item["html_url"]?.ToString() ?? known.HtmlUrl;
                        known.Stars = item["stargazers_count"]?.Value<int?>() ?? 0;
                        known.Updated = item["pushed_at"]?.Value<DateTime?>();
                        known.Fork = item["fork"]?.Value<bool?>() ?? false;
                    }
                }
                else list.Remove(known);
                known.Recommended = true;
                list.Insert(0, known);
            }
            return (list, problem);
        }

        /// <summary>The tracker projects the game's setup guide links to, from the engine's copy of the apworld; none without one.</summary>
        internal static List<(string Repo, string Text)> GuideRecommendations(string game)
        {
            try
            {
                return AP_Atlas.Core.EngineSetup.AtlasEngine.Current is { } install ? AP_Atlas.Core.EngineSetup.ApworldSources.GuideTrackerProjects(install, game) : new List<(string, string)>();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Couldn't read {game}'s setup guide for its tracker: {ex.Message}");
                return new List<(string, string)>();
            }
        }

        private static async Task<(List<PackCandidate> Candidates, string Problem)> SearchGitHubAsync(string game)
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
                        Updated = item["pushed_at"]?.Value<DateTime?>(),
                        Fork = item["fork"]?.Value<bool?>() ?? false
                    });
                }
                if (list.Count > 0) break;
            }
            return (list, null);
        }

        /// <summary>
        /// At most two searches per game: the exact name, then a looser form (digits for roman numerals, the subtitle). Forks
        /// included: GitHub leaves them out unless asked, and a fork is how the community keeps a pack up with a new apworld
        /// (the owner's Dark Souls III pack, 2026-10-11).
        /// </summary>
        internal static List<string> SearchQueries(string game)
        {
            var queries = new List<string> { $"\"{game}\" poptracker fork:true" };
            string loose = game;
            loose = Regex.Replace(loose, @"\bVIII\b", "8");
            loose = Regex.Replace(loose, @"\bVII\b", "7");
            loose = Regex.Replace(loose, @"\bVI\b", "6");
            loose = Regex.Replace(loose, @"\bIV\b", "4");
            loose = Regex.Replace(loose, @"\bV\b", "5");
            loose = Regex.Replace(loose, @"\bIII\b", "3");
            loose = Regex.Replace(loose, @"\bII\b", "2");
            if (loose.Contains(':')) loose = loose.Substring(loose.IndexOf(':') + 1).Trim();
            queries.Add(loose.Length > 0 && !string.Equals(loose, game, StringComparison.OrdinalIgnoreCase) ? $"\"{loose}\" poptracker fork:true" : $"{game} poptracker fork:true");
            return queries;
        }

        // The results dialog that's open (closed by itself once a pack chosen from it is installed).
        private AcceptDialog _candidatesDialog;

        private void ShowPackCandidates(List<(string Game, List<PackCandidate> Candidates, string Problem)> results, int wanted)
        {
            var dialog = _candidatesDialog = new AcceptDialog { Title = "Map packs on GitHub", OkButtonText = "Close", MinSize = new Vector2I(760, 520) };
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
                heading.AddThemeColorOverride("font_color", ThemeColors.AccentText(ThemeColors.Surface));
                box.AddChild(heading);
                if (problem != null) box.AddChild(Muted($"GitHub couldn't be searched: {problem}"));
                else if (candidates.Count == 0) box.AddChild(Muted("No map packs found on GitHub. Packs are often shared in the game's Archipelago Discord thread; install one from its zip with Install Pack (Zip)."));
                if (candidates.Count > 0)
                {
                    string pickGame = game;
                    var found = candidates;
                    var pick = new Button
                    {
                        Text = "Install the one that fits",
                        TooltipText = "Atlas tries these projects' newest releases against the game's names (the guide's project first, then by stars) and installs the first that fits; the rest are deleted",
                        SizeFlagsHorizontal = SizeFlags.ShrinkBegin
                    };
                    pick.SetMeta("pick_pack", game);
                    pick.Pressed += () => PickForGame(pickGame, found);
                    box.AddChild(pick);
                }
                foreach (var c in candidates)
                {
                    var row = new HBoxContainer();
                    row.AddThemeConstantOverride("separation", 8);
                    string updated = c.Updated.HasValue ? $" · updated {c.Updated.Value.ToLocalTime():yyyy-MM-dd}" : "";
                    string fork = c.Fork ? " · a fork" : "";
                    var info = new Label
                    {
                        Text = $"{c.Repo}  ★{c.Stars}{updated}{fork}" + (string.IsNullOrWhiteSpace(c.Description) ? "" : "\n" + c.Description),
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                        SizeFlagsHorizontal = SizeFlags.ExpandFill
                    };
                    if (c.Recommended)
                    {
                        // The game's own guide names it: said above the project, in the accent, so it's the first thing read.
                        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                        column.AddThemeConstantOverride("separation", 2);
                        var recommended = new Label { Text = "Recommended by the game's setup guide" };
                        recommended.SetMeta("recommended_pack", c.Repo);
                        recommended.AddThemeColorOverride("font_color", ThemeColors.AccentText(ThemeColors.Surface));
                        column.AddChild(recommended);
                        column.AddChild(info);
                        row.AddChild(column);
                    }
                    else row.AddChild(info);
                    string repo = c.Repo, g = game, page = c.HtmlUrl;
                    var install = new Button { Text = "Install…", TooltipText = "See what would be downloaded, then confirm" };
                    install.SetMeta("candidate_repo", repo);
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

        /// <summary>
        /// "Install the one that fits": asks once (how many projects and files at most), then lets Atlas pick the game's pack
        /// from what the search found (<see cref="InstallBestPackAsync(string, IReadOnlyList{PackCandidate}, Action{string})"/>),
        /// makes it the game's pack, and says which and why.
        /// </summary>
        private void PickForGame(string game, IReadOnlyList<PackCandidate> candidates)
        {
            int projects = Math.Min(candidates.Count, AP_Atlas.Core.Games.PackProjectPick.MostProjects);
            AP_Atlas.UI.Dialogs.Confirm(this, "Let Atlas pick the pack?",
                $"Atlas downloads the newest releases of up to {projects} of these projects (at most {AP_Atlas.Core.Games.PackProjectPick.MostDownloads} files) beside the packs, measures each against {game}'s names, and installs the first that fits. The rest are deleted.",
                "Pick and install", () => AP_Atlas.Core.Async.Fire(PickForGameAsync(game, candidates), "picking a map pack for " + game));
        }

        internal async Task<(bool Ok, string Detail, string File, string Repo)> PickForGameAsync(string game, IReadOnlyList<PackCandidate> candidates)
        {
            var answer = await InstallBestPackAsync(game, candidates, line => AP_Atlas.UI.Ui.Defer(this, () => ShowTrying(line)));
            AP_Atlas.UI.Ui.Defer(this, () =>
            {
                _downloadRow.Visible = false;
                if (answer.Ok && answer.File != null)
                {
                    PreferPack?.Invoke(game, System.IO.Path.GetFileName(answer.File));
                    if (_candidatesDialog != null && GodotObject.IsInstanceValid(_candidatesDialog)) _candidatesDialog.QueueFree();
                    PopTrackerPackLoader.NotifyPacksChanged();
                }
                Toast?.Invoke(answer.Ok ? $"{game}'s map pack: {answer.Detail}." : $"No pack was installed for {game}: {answer.Detail}.", answer.Ok ? ThemeColors.Success : ThemeColors.Warning);
                _logAction($"Picking {game}'s map pack: {(answer.Ok ? "" : "nothing installed: ")}{answer.Detail}", answer.Ok ? "green" : "yellow");
            });
            return answer;
        }

        /// <summary>What a pick is trying now, on the page's download line.</summary>
        private void ShowTrying(string line)
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            DownloadText = line;
            _downloadLabel.Text = line;
            _downloadBar.Indeterminate = true;
            _downloadRow.Visible = true;
        }

        /// <summary>Looks up the project's latest release, then shows exactly what would be downloaded and asks.</summary>
        private void InstallFromRepo(string game, string repo) => AP_Atlas.Core.Async.Fire(InstallFromRepoAsync(game, repo), $"looking up {repo}'s latest release");

        private Task InstallFromRepoAsync(string game, string repo) => InstallPackFromRepoAsync(game, repo, confirm: true);

        /// <summary>
        /// Installs a project's newest released pack (or its current files) for a game, asking first unless told not to
        /// (the solo test, which said what it does); true once the pack is in Atlas's packs folder. When asking and the
        /// project has several releases with a pack, the question offers them with their notes: a project's newest release
        /// can be for a newer apworld than the engine's, and its notes say so.
        /// </summary>
        internal async Task<bool> InstallPackFromRepoAsync(string game, string repo, bool confirm)
        {
            GitHubApi.Result release;
            var releases = new List<PackRelease>();
            _showOverlayAction();
            try
            {
                if (confirm)
                {
                    var list = await GitHubApi.GetAsync($"/repos/{repo}/releases?per_page=12");
                    if (list.Ok) releases = ReleasesWithPacks(list.Json);
                }
                release = await GitHubApi.GetAsync($"/repos/{repo}/releases/latest");
            }
            finally { _hideOverlayAction(); }
            if (!GodotObject.IsInstanceValid(this)) return false;
            if (releases.Count > 1) return await ConfirmReleaseAsync(game, repo, releases);
            if (release.Unknown)
            {
                _logAction($"Couldn't read {repo}'s releases: {release.Message}", "orange");
                return false;
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
            if (!confirm) return await DownloadPackAsync(downloadUrl, file, where);
            var answered = new TaskCompletionSource<bool>();
            AP_Atlas.UI.Dialogs.Confirm(this, "Install a map pack", text, "Download and install", () => AP_Atlas.Core.Async.Fire(async () => answered.TrySetResult(await DownloadPackAsync(downloadUrl, file, where)), "downloading a map pack"),
                null, () => answered.TrySetResult(false));
            return await answered.Task;
        }

        /// <summary>A project's releases that hold a pack (a zip), as GitHub lists them: newest first, drafts left out.</summary>
        internal static List<PackRelease> ReleasesWithPacks(JToken json)
        {
            var list = new List<PackRelease>();
            foreach (var item in json as JArray ?? new JArray())
            {
                if (item["draft"]?.Value<bool?>() == true) continue;
                var zip = (item["assets"] as JArray ?? new JArray()).FirstOrDefault(a => (a["name"]?.ToString() ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (zip == null) continue;
                list.Add(new PackRelease
                {
                    Tag = item["tag_name"]?.ToString() ?? "",
                    Name = item["name"]?.ToString() ?? "",
                    Notes = item["body"]?.ToString() ?? "",
                    Url = zip["browser_download_url"]?.ToString(),
                    FileName = zip["name"]?.ToString(),
                    Size = zip["size"]?.Value<long?>() ?? 0,
                    Prerelease = item["prerelease"]?.Value<bool?>() == true,
                    Published = item["published_at"]?.Value<DateTime?>()
                });
            }
            return list;
        }

        /// <summary>The release notes as a short plain line or two: markdown marks dropped, cut after a few hundred characters.</summary>
        internal static string NotesExcerpt(string notes, int most = 420)
        {
            if (string.IsNullOrWhiteSpace(notes)) return "";
            string text = Regex.Replace(notes.Replace("\r", ""), @"^\s*#+\s*", "", RegexOptions.Multiline);
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
            text = Regex.Replace(text, @"[*_`]+", "");
            text = Regex.Replace(text, @"\n{2,}", "\n").Trim();
            return text.Length <= most ? text : text.Substring(0, most).TrimEnd() + "…";
        }

        /// <summary>The question before a download when the project has several releases: which one, with its notes shown.</summary>
        private Task<bool> ConfirmReleaseAsync(string game, string repo, List<PackRelease> releases)
        {
            var answered = new TaskCompletionSource<bool>();
            var dialog = new ConfirmationDialog { Title = "Install a map pack", OkButtonText = "Download and install", DialogHideOnOk = false };
            dialog.SetMeta("release_picker", repo);
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            box.AddChild(new Label { Text = $"github.com/{repo} has {releases.Count} releases with a pack. Pick the one for your game's version (the notes often say which); the newest is first.", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(520, 0) });
            var picker = new OptionButton { Name = "Releases", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            foreach (var r in releases) picker.AddItem(r.Label + (r.Prerelease ? " (pre-release)" : ""));
            box.AddChild(picker);
            var notes = Muted("");
            notes.CustomMinimumSize = new Vector2(520, 0);
            box.AddChild(notes);
            var size = Muted("");
            box.AddChild(size);
            void ShowRelease(int i)
            {
                var r = releases[Math.Clamp(i, 0, releases.Count - 1)];
                string when = r.Published.HasValue ? $", {r.Published.Value.ToLocalTime():yyyy-MM-dd}" : "";
                size.Text = $"{r.FileName} ({r.Size / (1024.0 * 1024.0):0.0} MB{when}), installed as a map pack for {game}.";
                string excerpt = NotesExcerpt(r.Notes);
                notes.Text = excerpt.Length > 0 ? excerpt : "(The release has no notes.)";
            }
            picker.ItemSelected += i => ShowRelease((int)i);
            ShowRelease(0);
            box.AddChild(new Label { Text = Tr("Map packs are made by the community, and their scripts run inside Atlas's sandbox. Only install packs from projects you trust."), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(520, 0) });
            dialog.AddChild(box);
            dialog.Confirmed += () =>
            {
                var r = releases[Math.Clamp(picker.Selected, 0, releases.Count - 1)];
                dialog.QueueFree();
                AP_Atlas.Core.Async.Fire(async () => answered.TrySetResult(await DownloadPackAsync(r.Url, r.FileName, "github.com/" + repo)), "downloading a map pack");
            };
            dialog.Canceled += () =>
            {
                dialog.QueueFree();
                answered.TrySetResult(false);
            };
            AddChild(dialog);
            AP_Atlas.UI.WindowFit.Pop(dialog, 580);
            return answered.Task;
        }

        /// <summary>
        /// Installs a project's pack without asking (the solo test, whose Start covered the downloads): the newest release
        /// that fits the game's names, by the Pack Doctor's check on each tried (newest first, at most three); with none
        /// fitting, the best tried. Says what was tried, and gives the installed file.
        /// </summary>
        internal async Task<(bool Ok, string Detail, string File)> InstallFittingReleaseAsync(string game, string repo, Action<string> say)
        {
            // No overlay: whoever asked shows the progress (the solo test's window), and Atlas stays usable meanwhile.
            string where = "github.com/" + repo, trying = NewTryingFolder();
            try
            {
                var project = await TryProjectAsync(repo, trying, AP_Atlas.Core.Games.PackReleasePick.MostTried, say);
                if (!GodotObject.IsInstanceValid(this)) return (false, "the window closed", null);
                var chosen = AP_Atlas.Core.Games.PackReleasePick.Best(project.Tried);
                if (chosen == null) return (false, project.Note ?? "no release of " + where + " could be downloaded and read", null);
                string installed = await InstallDownloadedAsync(project.Files[chosen.Tag], project.Names[chosen.Tag], where, quiet: false);
                if (installed == null) return (false, chosen.Tag + " fits best, but it couldn't be put among the packs (the log says why)", null);
                return (true, AP_Atlas.Core.Games.PackReleasePick.Describe(project.Tried, chosen), installed);
            }
            finally { RemoveTryingFolder(trying, repo); }
        }

        /// <summary>
        /// The pack Atlas picks for a game by itself, from GitHub's projects for it (see <see cref="SearchPacksAsync"/>):
        /// the project the game's own setup guide links first, then by stars, then the most recently updated; each tried
        /// as <see cref="InstallFittingReleaseAsync"/> tries one, and the first whose release fits the game's names is
        /// installed. With none fitting, the closest is installed when half its pins link (<see cref="AP_Atlas.Core.Games.PackProjectPick"/>);
        /// else nothing is, and the answer says why. No question (whoever asked said what it does); what was tried is said.
        /// </summary>
        internal async Task<(bool Ok, string Detail, string File, string Repo)> InstallBestPackAsync(string game, Action<string> say)
        {
            say?.Invoke($"looking on GitHub for {game}'s map packs");
            var (candidates, problem) = await SearchPacksAsync(game);
            if (candidates.Count == 0) return (false, problem != null ? "GitHub couldn't be searched: " + problem : "GitHub has no map pack for " + game, null, null);
            return await InstallBestPackAsync(game, candidates, say);
        }

        internal async Task<(bool Ok, string Detail, string File, string Repo)> InstallBestPackAsync(string game, IReadOnlyList<PackCandidate> candidates, Action<string> say)
        {
            var order = candidates.Select((c, i) => (c, i)).OrderByDescending(x => x.c.Recommended).ThenByDescending(x => x.c.Stars)
                .ThenByDescending(x => x.c.Updated ?? DateTime.MinValue).ThenBy(x => x.i).Select(x => x.c).Take(AP_Atlas.Core.Games.PackProjectPick.MostProjects).ToList();
            string trying = NewTryingFolder();
            try
            {
                var tried = new List<(PackCandidate Candidate, ProjectTry Project, AP_Atlas.Core.Games.PackReleaseFit Best)>();
                int downloads = 0;
                foreach (var candidate in order)
                {
                    int budget = Math.Min(AP_Atlas.Core.Games.PackReleasePick.MostTried, AP_Atlas.Core.Games.PackProjectPick.MostDownloads - downloads);
                    if (budget <= 0) break;
                    var project = await TryProjectAsync(candidate.Repo, trying, budget, say);
                    if (!GodotObject.IsInstanceValid(this)) return (false, "the window closed", null, null);
                    downloads += project.Downloads;
                    var best = AP_Atlas.Core.Games.PackReleasePick.Best(project.Tried);
                    tried.Add((candidate, project, best));
                    if (best?.Fits == true) break;
                }
                string Summary(int except) => string.Join("; ", tried.Where((_, i) => i != except)
                    .Select(t => "github.com/" + t.Candidate.Repo + " " + (t.Best != null ? t.Best.Describe() : t.Project.Note ?? "couldn't be read")));
                int chosen = AP_Atlas.Core.Games.PackProjectPick.Choose(tried.Select(t => t.Best).ToList());
                if (chosen < 0)
                    return (false, tried.Count == 0 ? "no project could be tried" : "none of the packs found fits " + game + "'s names (" + Summary(-1) + ")", null, null);
                var (pick, picked, fit) = tried[chosen];
                string where = "github.com/" + pick.Repo;
                string installed = await InstallDownloadedAsync(picked.Files[fit.Tag], picked.Names[fit.Tag], where, quiet: false);
                if (installed == null) return (false, where + " " + fit.Tag + " fits best, but it couldn't be put among the packs (the log says why)", null, null);
                string others = Summary(chosen);
                return (true, (fit.Fits ? "" : "none fits well; the closest: ") + where + " " + AP_Atlas.Core.Games.PackReleasePick.Describe(picked.Tried, fit)
                    + (pick.Recommended ? " (recommended by the game's setup guide)" : "") + (others.Length > 0 ? "; also tried " + others : ""), installed, pick.Repo);
            }
            finally { RemoveTryingFolder(trying, game); }
        }

        /// <summary>One project's releases (or its current files) downloaded beside the packs and measured: each one's fit, file and name.</summary>
        private sealed class ProjectTry
        {
            public readonly List<AP_Atlas.Core.Games.PackReleaseFit> Tried = new List<AP_Atlas.Core.Games.PackReleaseFit>();
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);
            public int Downloads;
            /// <summary>Why nothing could be tried, when nothing was.</summary>
            public string Note;
        }

        /// <summary>
        /// A project's newest releases with a pack (at most <paramref name="most"/>), or its current files when it has none,
        /// each downloaded into <paramref name="trying"/> (never among the packs) and measured against the game's names;
        /// it stops at the first that fits. Nothing is installed.
        /// </summary>
        private async Task<ProjectTry> TryProjectAsync(string repo, string trying, int most, Action<string> say)
        {
            var result = new ProjectTry();
            string where = "github.com/" + repo;
            var list = await GitHubApi.GetAsync($"/repos/{repo}/releases?per_page=12");
            var releases = list.Ok ? ReleasesWithPacks(list.Json) : new List<PackRelease>();
            if (releases.Count == 0)
            {
                var latest = await GitHubApi.GetAsync($"/repos/{repo}/releases/latest");
                if (latest.Ok && latest.Json is JObject one) releases = ReleasesWithPacks(new JArray(one));
            }
            if (!GodotObject.IsInstanceValid(this)) return result;
            // No released pack: the project's current files, as the Map Packs page offers them.
            var tries = releases.Count > 0
                ? releases.Take(most).Select(r => (Tag: r.Tag, Url: r.Url, Name: SafeZipName(r.FileName))).ToList()
                : new List<(string Tag, string Url, string Name)> { ("current files", $"{GitHubApi.Site}/repos/{repo}/zipball", repo.Replace('/', '_') + ".zip") };
            foreach (var (tag, url, name) in tries)
            {
                say?.Invoke(releases.Count > 0 ? $"trying {where} {tag}" : $"trying {where}'s current files (it has no release)");
                string file = System.IO.Path.Combine(trying, Guid.NewGuid().ToString("N") + ".zip");
                result.Downloads++;
                if (!await DownloadToAsync(url, where, file, name)) continue;
                if (!GodotObject.IsInstanceValid(this)) return result;
                var fit = await FitOfAsync(tag, file);
                if (fit == null) continue;
                result.Files[tag] = file;
                result.Names[tag] = name;
                result.Tried.Add(fit);
                say?.Invoke(fit.Describe());
                if (fit.Fits) break;
            }
            if (result.Tried.Count == 0) result.Note = releases.Count > 0 ? "no release could be downloaded and read as a pack" : "its files couldn't be downloaded or aren't a pack";
            return result;
        }

        // Each release tried is downloaded to a folder of its own beside the packs (never among them), measured there, and
        // only the one kept is installed: no pack comes and goes, connected slots don't reload for a try, and no installed
        // pack's file is written while something reads it (a release named like an installed one in another case is the
        // same file on Windows).
        private static string NewTryingFolder()
        {
            string trying = System.IO.Path.Combine(DataManager.GetDataDirectory(), "packs", TryingFolder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(trying);
            return trying;
        }

        private static void RemoveTryingFolder(string trying, string what)
        {
            try { Directory.Delete(trying, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogDebug($"The releases tried for {what} weren't all removed: {ex.Message}"); }
        }

        /// <summary>The folder beside the installed packs where releases being tried are downloaded (the pack lists read only the packs folder's own files).</summary>
        internal const string TryingFolder = ".trying";

        private static string SafeZipName(string fileName)
        {
            string safeName = string.Concat((fileName ?? "pack.zip").Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            return safeName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? safeName : safeName + ".zip";
        }

        /// <summary>How a downloaded release fits the game's names, by the Pack Doctor's analysis (null when the zip isn't a pack).</summary>
        private static async Task<AP_Atlas.Core.Games.PackReleaseFit> FitOfAsync(string tag, string file)
        {
            LoadedPack pack;
            try { pack = await Task.Run(() => PopTrackerPackLoader.ReadOnce(file)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                Logger.LogWarning($"Couldn't read the downloaded pack {System.IO.Path.GetFileName(file)}: {ex.Message}");
                return null;
            }
            if (pack == null) return null;
            // Measured, not checked: a check of the installed pack running meanwhile can't hide this one's numbers.
            var report = await PackDoctorService.MeasureAsync(pack);
            if (report == null) return null;
            return new AP_Atlas.Core.Games.PackReleaseFit(tag, report.SectionsTotal, report.SectionsLinked, pack.LocationMappingById?.Count ?? 0, report.Index?.UnknownMappedLocationIds.Count ?? 0);
        }

        private async Task<bool> DownloadPackAsync(string url, string fileName, string where)
        {
            string packsDir = System.IO.Path.Combine(DataManager.GetDataDirectory(), "packs");
            Directory.CreateDirectory(packsDir);
            string safeName = SafeZipName(fileName);
            string temp = System.IO.Path.Combine(packsDir, $".download-{Guid.NewGuid():N}.tmp");
            if (!await DownloadToAsync(url, where, temp, safeName)) return false;
            return await InstallDownloadedAsync(temp, safeName, where, quiet: false) != null;
        }

        /// <summary>Downloads a pack to a file, checked as a pack's zip; false (and said) when it can't be.</summary>
        private async Task<bool> DownloadToAsync(string url, string where, string temp, string safeName)
        {
            // The progress shows in the page (the window stays usable); the progress callback runs off the main thread.
            ShowDownload(safeName, 0, -1);
            long shown = 0;
            void Progress(long done, long total)
            {
                if (done - shown < 256 * 1024 && done != total) return;
                shown = done;
                AP_Atlas.UI.Ui.Defer(this, () => ShowDownload(safeName, done, total));
            }
            WebResponse r;
            try { r = await PoliteHttp.DownloadAsync(url, where, temp, null, 256L * 1024 * 1024, TimeSpan.FromMinutes(10), Progress); }
            finally { if (GodotObject.IsInstanceValid(this)) _downloadRow.Visible = false; }
            if (!r.Ok)
            {
                PoliteHttp.TryDelete(temp);
                Failed($"Couldn't download the pack from {where}: {r.Message}");
                return false;
            }
            string problem = CheckPackZip(temp);
            if (problem != null)
            {
                PoliteHttp.TryDelete(temp);
                Failed($"The download from {where} wasn't installed: {problem}.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Puts a downloaded pack among the installed ones and tells the slots; the installed file's path, or null (said).
        /// The same name as another pack's file (in any case: Windows takes them for one file) gets a name of its own
        /// rather than writing over that pack; a file held a moment (a check reading it) is tried again.
        /// </summary>
        private async Task<string> InstallDownloadedAsync(string temp, string safeName, string where, bool quiet)
        {
            string packsDir = System.IO.Path.Combine(DataManager.GetDataDirectory(), "packs");
            string dest = System.IO.Path.Combine(packsDir, safeName);
            if (File.Exists(dest) && !SamePack(dest, temp))
            {
                string stem = System.IO.Path.GetFileNameWithoutExtension(safeName);
                for (int n = 2; File.Exists(dest); n++) dest = System.IO.Path.Combine(packsDir, $"{stem} ({n}).zip");
            }
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, dest, true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 6)
                {
                    Logger.LogDebug($"{System.IO.Path.GetFileName(dest)} is held ({ex.Message}); trying again.");
                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    PoliteHttp.TryDelete(temp);
                    Failed($"Couldn't install the pack: {ex.Message}");
                    return null;
                }
            }
            if (!GodotObject.IsInstanceValid(this)) return dest;
            _logAction($"Installed {System.IO.Path.GetFileName(dest)} from {where}.", "lime");
            if (!quiet) SayInstalled(dest);
            if (_candidatesDialog != null && GodotObject.IsInstanceValid(_candidatesDialog))
            {
                _candidatesDialog.Hide();
                _candidatesDialog.QueueFree();
            }
            _candidatesDialog = null;
            PopTrackerPackLoader.NotifyPacksChanged();
            await RefreshPackListAsync();
            return dest;
        }

        /// <summary>Whether an installed pack's file holds the same pack as a download (its id, else its name): then the download replaces it.</summary>
        private static bool SamePack(string installed, string downloaded)
        {
            PopTrackerManifest a, b;
            try
            {
                a = ReadManifest(installed);
                b = ReadManifest(downloaded);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                Logger.LogDebug($"Couldn't compare {System.IO.Path.GetFileName(installed)} with the download: {ex.Message}");
                return false;
            }
            if (a == null) return true; // not a readable pack: nothing of the user's is lost by replacing it
            if (b == null) return false;
            if (!string.IsNullOrEmpty(a.PackageUid) && !string.IsNullOrEmpty(b.PackageUid)) return string.Equals(a.PackageUid, b.PackageUid, StringComparison.OrdinalIgnoreCase);
            return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        private void Failed(string message)
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            _logAction(message, "red");
            Toast?.Invoke(message, ThemeColors.Error);
        }

        private void ShowDownload(string name, long done, long total)
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            // Sizes in the unit the whole download is measured in: KB under a megabyte, else MB with one decimal.
            bool mb = Math.Max(done, total) >= 1024 * 1024;
            string Size(long bytes) => mb ? (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : Math.Max(1, bytes / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string unit = mb ? "MB" : "KB";
            DownloadText = total > 0 ? AP_Atlas.UI.Kit.Translate("Downloading {0}: {1} of {2} {3}").Replace("{0}", name).Replace("{1}", Size(done)).Replace("{2}", Size(total)).Replace("{3}", unit)
                : done > 0 ? AP_Atlas.UI.Kit.Translate("Downloading {0}: {1} {2}").Replace("{0}", name).Replace("{1}", Size(done)).Replace("{2}", unit)
                : AP_Atlas.UI.Kit.Translate("Downloading {0}…").Replace("{0}", name);
            _downloadLabel.Text = DownloadText;
            _downloadBar.Indeterminate = total <= 0;
            _downloadBar.Value = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
            _downloadRow.Visible = true;
        }

        /// <summary>A card saying the pack is in: connected slots of its game show it at once (PacksChanged reaches them).</summary>
        private void SayInstalled(string zipPath)
        {
            PopTrackerManifest manifest = null;
            try { manifest = ReadManifest(zipPath); }
            catch (Exception ex) { Logger.LogDebug($"Couldn't read the installed pack's manifest for the card: {ex.Message}"); }
            string name = manifest?.Name ?? System.IO.Path.GetFileNameWithoutExtension(zipPath);
            string game = manifest?.GameName;
            Toast?.Invoke(string.IsNullOrEmpty(game)
                ? AP_Atlas.UI.Kit.Translate("Installed {0}.").Replace("{0}", name)
                : AP_Atlas.UI.Kit.Translate("Installed {0}: connected slots of {1} use it now.").Replace("{0}", name).Replace("{1}", game), ThemeColors.Success);
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
                        SayInstalled(destPath);
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
