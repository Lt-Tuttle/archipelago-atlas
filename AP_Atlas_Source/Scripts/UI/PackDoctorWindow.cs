#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Godot;
using Color = Godot.Color;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The Pack Doctor: reviews one map pack against the game's real Archipelago names and lets the user fix what's
    /// wrong locally (Key Items tiles, pin ↔ location links, pin positions, added pins, map images), with undo,
    /// per-fix reset, and a ready-to-send report for the pack's author. Fixes live in PackFixes, never the zip.
    /// </summary>
    public partial class PackDoctorWindow : Window
    {
        private static readonly Color Good = ThemeColors.Success;
        private static readonly Color Bad = ThemeColors.Error;
        private static readonly Color Muted = ThemeColors.TextSubtle;
        private static readonly Color Warn = ThemeColors.Warning;
        private static readonly Color Fixed = new Color("#FFC53D");

        private readonly LoadedPack _original;
        // The pack's images, used while the window is open (it shows tiles and maps).
        private IDisposable _images;
        private bool _closed;
        private readonly string _key;
        private readonly int _fontSize;
        private DoctorReport _report;

        private TabContainer _tabs;
        private Label _status;
        private Button _undoButton;

        private static readonly Dictionary<string, PackDoctorWindow> _open = new Dictionary<string, PackDoctorWindow>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Opens (or focuses) the Doctor for a pack.</summary>
        public static void Open(Node anyNode, LoadedPack original, int fontSize, string startTab = null, string focusPinPath = null)
        {
            if (original == null) return;
            string key = PackFixes.KeyFor(original);
            if (_open.TryGetValue(key, out var existing) && IsInstanceValid(existing))
            {
                existing.GrabFocus();
                if (focusPinPath != null) { existing._focusPinPath = focusPinPath; startTab = "Maps"; }
                if (startTab != null)
                {
                    existing.SelectTab(startTab);
                    if (focusPinPath != null) existing.RenderCurrentTab();
                }
                return;
            }
            var w = new PackDoctorWindow(original, fontSize) { _focusPinPath = focusPinPath };
            if (focusPinPath != null) startTab = "Maps";
            _open[key] = w;
            anyNode.GetTree().Root.AddChild(w);
            var screen = DisplayServer.ScreenGetSize();
            w.PopupCentered(new Vector2I((int)(screen.X * 0.72f), (int)(screen.Y * 0.78f)));
            if (startTab != null) w.SelectTab(startTab);
        }

        private PackDoctorWindow(LoadedPack original, int fontSize)
        {
            _original = original;
            _key = PackFixes.KeyFor(original);
            _fontSize = fontSize;
            Title = $"Pack Doctor — {original.Manifest?.Name}";
            Transient = false;
            Exclusive = false;
            WrapControls = false;
            MinSize = new Vector2I(900, 600);
        }

        public override void _Ready()
        {
            CloseRequested += () =>
            {
                PackDoctorService.MarkReviewed(_original);
                QueueFree();
            };
            var bg = new Panel();
            bg.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = ThemeColors.SurfaceSunken });
            bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            AddChild(bg);
            var margin = new MarginContainer();
            margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 12);
            AddChild(margin);
            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", 8);
            margin.AddChild(root);

            _tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            root.AddChild(_tabs);
            _tabs.TabChanged += _ => RenderCurrentTab();

            var footer = new HBoxContainer();
            root.AddChild(footer);
            _status = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _status.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            footer.AddChild(_status);
            _undoButton = new Button { Text = "Undo", TooltipText = "Undo your last change" };
            _undoButton.Pressed += () => { if (PackFixes.Undo(_key)) SetStatus("Undone."); };
            footer.AddChild(_undoButton);
            var recheck = new Button { Text = "Re-check", TooltipText = "Run every check again (also re-reads names from your Archipelago install)" };
            recheck.Pressed += () => { SetStatus("Checking…"); AP_Atlas.Core.Async.Fire(PackDoctorService.CheckAsync(_original, prompt: false, refreshLocalNames: true), "checking the map pack"); };
            footer.AddChild(recheck);
            var done = new Button { Text = "Done", TooltipText = "Close. You won't be prompted about this pack version again." };
            done.Pressed += () => { PackDoctorService.MarkReviewed(_original); QueueFree(); };
            footer.AddChild(done);

            foreach (var name in new[] { "Overview", "Recommended", "Key Items", "Locations", "Maps", "Your fixes", "Report" })
            {
                var page = new MarginContainer { Name = name };
                page.AddThemeConstantOverride("margin_top", 8);
                _tabs.AddChild(page);
            }

            // The window's own chrome (tab strip, footer) needs the app's font size too; pages are sized as they render.
            MainTrackerWindow.SetFontSizeRecursive(margin, _fontSize);
            _tabs.AddThemeFontSizeOverride("font_size", _fontSize);
            AddThemeFontSizeOverride("title_font_size", _fontSize);

            PackDoctorService.ReportReady += OnReportReady;
            PackFixes.Changed += OnFixesChanged;
            _report = PackDoctorService.Reports.TryGetValue(_key, out var r) ? r : null;
            // The pack's images are decoded in the background (a big pack takes seconds), then the pack is checked again,
            // so what the window shows has them. An earlier report shows meanwhile.
            SetStatus("Reading the pack's images…");
            AP_Atlas.Core.Async.Then(System.Threading.Tasks.Task.Run(() => PackImages.Use(_original)), images => AP_Atlas.UI.Ui.Defer(null, () =>
            {
                if (_closed)
                {
                    images?.Dispose();
                    return;
                }
                _images = images;
                SetStatus("Checking the pack…");
                AP_Atlas.Core.Async.Fire(PackDoctorService.CheckAsync(_original, prompt: false), "checking the map pack");
            }, "showing the pack's images"), "reading the map pack's images");
            RenderCurrentTab();
        }

        public override void _ExitTree()
        {
            PackDoctorService.ReportReady -= OnReportReady;
            PackFixes.Changed -= OnFixesChanged;
            _open.Remove(_key);
            _closed = true;
            _images?.Dispose();
            _images = null;
        }

        private void OnReportReady(string key)
        {
            if (key != _key || !IsInstanceValid(this)) return;
            _report = PackDoctorService.Reports[key];
            SetStatus($"Checked {DateTime.Now:HH:mm:ss}. Names: {_report.Names?.Source ?? "none"}{(_report.Names != null ? $" ({_report.Names.Game})" : "")}.");
            RenderCurrentTab();
        }

        private void OnFixesChanged(string key)
        {
            if (key != _key) return;
            UpdateUndo();
            // The service re-checks after every change; the tab redraws when the new report arrives.
        }

        private void UpdateUndo()
        {
            string d = PackFixes.UndoDescription(_key);
            _undoButton.Disabled = d == null;
            _undoButton.TooltipText = d == null ? "Nothing to undo" : "Undo: " + d;
        }

        private void SetStatus(string text) => _status.Text = text;

        private void SelectTab(string name)
        {
            for (int i = 0; i < _tabs.GetTabCount(); i++)
                if (_tabs.GetTabTitle(i) == name) { _tabs.CurrentTab = i; return; }
        }

        private Control Page(string name) => _tabs.GetNodeOrNull<Control>(name);

        /// <summary>Rebuilds the visible tab from the latest report.</summary>
        private void RenderCurrentTab()
        {
            if (_tabs == null) return;
            UpdateUndo();
            var page = _tabs.GetCurrentTabControl();
            if (page == null) return;
            foreach (Node child in page.GetChildren()) { page.RemoveChild(child); child.QueueFree(); }
            if (_report == null)
            {
                page.AddChild(new Label { Text = "Checking the pack…" });
                return;
            }
            Control content = page.Name.ToString() switch
            {
                "Overview" => BuildOverview(),
                "Recommended" => BuildRecommendedTab(),
                "Key Items" => BuildItemsTab(),
                "Locations" => BuildLocationsTab(),
                "Maps" => BuildMapsTab(),
                "Your fixes" => BuildFixesTab(),
                "Report" => BuildReportTab(),
                _ => new Control()
            };
            MainTrackerWindow.SetFontSizeRecursive(content, _fontSize);
            page.AddChild(content);
        }

        // =====================================================================
        // Shared fix actions
        // =====================================================================

        private IReadOnlyDictionary<long, string> ItemNames => _report?.Index?.ItemNames ?? new Dictionary<long, string>();
        private IReadOnlyDictionary<long, string> LocationNames => _report?.Index?.LocationNames ?? new Dictionary<long, string>();

        private void LinkTile(string code, long id, string name)
        {
            PackFixes.Edit(_key, $"Link tile '{code}' to {name}", f =>
            {
                var t = UpsertTile(f, code);
                t.ApItemId = id;
                t.ApItemName = name;
            });
            SetStatus($"Tile '{code}' now tracks {name}.");
        }

        private void SetTileHidden(string code, bool hidden)
        {
            PackFixes.Edit(_key, (hidden ? "Hide" : "Show") + $" tile '{code}'", f => UpsertTile(f, code).Hidden = hidden);
        }

        private TileFix UpsertTile(PackFixFile f, string code)
        {
            string subject = "tile:" + code;
            var t = f.Tiles.FirstOrDefault(x => x.Subject == subject);
            if (t == null)
            {
                t = new TileFix { Subject = subject, Code = code, AuthorStamp = PackFixes.AuthorStamp(_original, subject) };
                f.Tiles.Add(t);
            }
            t.Made = DateTime.Now;
            return t;
        }

        private void PickTileItem(string code, string query, List<Suggestion> suggestions)
        {
            NamePickerDialog.Open(this, "Which Archipelago item does this tile track?", query, ItemNames,
                suggestions ?? PackDoctor.Suggest(query, ItemNames, 8), (id, name) => LinkTile(code, id, name));
        }

        private void PickTileImage(string code)
        {
            PickImageFile("Choose an image for this tile", path =>
            {
                string stored = PackFixes.ImportImage(_key, path);
                PackFixes.Edit(_key, $"New image for tile '{code}'", f => UpsertTile(f, code).ImageFile = stored);
            });
        }

        private void SetGridHidden(string subject, bool hidden)
        {
            PackFixes.Edit(_key, (hidden ? "Hide" : "Show") + " a Key Items grid", f => UpsertGrid(f, subject).Hidden = hidden);
        }

        private GridFix UpsertGrid(PackFixFile f, string subject)
        {
            var g = f.Grids.FirstOrDefault(x => x.Subject == subject);
            if (g == null)
            {
                g = new GridFix { Subject = subject, AuthorStamp = PackFixes.AuthorStamp(_original, subject) };
                f.Grids.Add(g);
            }
            g.Made = DateTime.Now;
            return g;
        }

        private void LinkSection(string pinPath, string sectionName, long? id, string name)
        {
            string subject = $"link:{pinPath}|{sectionName}";
            PackFixes.Edit(_key, id == null ? $"Unlink {sectionName}" : $"Link {sectionName} to {name}", f =>
            {
                f.Links.RemoveAll(x => x.Subject == subject);
                f.Links.Add(new LocationLinkFix
                {
                    Subject = subject,
                    PinPath = pinPath,
                    SectionName = sectionName ?? "",
                    ApLocationId = id,
                    ApLocationName = name,
                    AuthorStamp = PackFixes.AuthorStamp(_original, subject)
                });
            });
            SetStatus(id == null ? $"'{sectionName}' unlinked." : $"'{sectionName}' now shows {name}.");
        }

        private void PickSectionLocation(string pinPath, string sectionName, List<Suggestion> suggestions)
        {
            string query = string.IsNullOrEmpty(sectionName) ? pinPath.Split('/').Last() : sectionName;
            NamePickerDialog.Open(this, "Which Archipelago location is this?", $"{pinPath} / {query}", LocationNames,
                suggestions ?? PackDoctor.Suggest(query, LocationNames, 8), (id, name) => LinkSection(pinPath, sectionName, id, name));
        }

        private void ToggleIgnore(Finding f)
        {
            PackFixes.Edit(_key, (f.Ignored ? "Stop ignoring" : "Ignore") + ": " + f.Title, file =>
            {
                if (f.Ignored) file.Ignored.Remove(f.Key);
                else if (!file.Ignored.Contains(f.Key)) file.Ignored.Add(f.Key);
            });
        }

        private void ReplaceMapImage(string mapId)
        {
            PickImageFile("Choose a background image for this map", path =>
            {
                string stored = PackFixes.ImportImage(_key, path);
                string subject = "map:" + mapId;
                PackFixes.Edit(_key, $"New background for map '{mapId}'", f =>
                {
                    f.MapImages.RemoveAll(x => x.Subject == subject);
                    f.MapImages.Add(new MapImageFix { Subject = subject, MapId = mapId, ImageFile = stored, AuthorStamp = PackFixes.AuthorStamp(_original, subject) });
                });
            });
        }

        private void PickImageFile(string title, Action<string> onPicked)
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.png, *.jpg, *.jpeg, *.webp ; Images" },
                UseNativeDialog = true,
                Title = title
            };
            dialog.FileSelected += path =>
            {
                try { onPicked(path); }
                catch (Exception ex) { SetStatus("Couldn't use that image: " + ex.Message); }
                dialog.QueueFree();
            };
            dialog.Canceled += () => dialog.QueueFree();
            AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tga", ".svg" };

        /// <summary>
        /// Saves a copy of an image from the pack where the user chooses, so they can re-save it as PNG in an image editor.
        /// Only image files, and Atlas never opens the copy itself (a pack could name anything an "image").
        /// </summary>
        private void SavePackImage(string relativePath)
        {
            string ext = Path.GetExtension(relativePath ?? "").ToLowerInvariant();
            if (!ImageExtensions.Contains(ext))
            {
                SetStatus("The pack's map background isn't an image file, so Atlas won't save it.");
                return;
            }
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.SaveFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { $"*{ext} ; Image" },
                UseNativeDialog = true,
                Title = "Save a copy of the pack's image",
                CurrentFile = Path.GetFileName(relativePath)
            };
            dialog.FileSelected += path =>
            {
                dialog.QueueFree();
                try
                {
                    if (!ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) path += ext;
                    using var zip = SafeZip.Open(_original.SourcePath);
                    var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(relativePath.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
                    if (entry == null) { SetStatus("That image isn't in the pack."); return; }
                    byte[] image = zip.ReadImage(entry); // read whole first: a refused image leaves no partial copy behind
                    using (var dst = File.Create(path)) dst.Write(image);
                    SetStatus($"Saved {Path.GetFileName(path)}. Re-save it as PNG in an image editor, then use \"Replace background…\".");
                }
                catch (Exception ex) { SetStatus("Couldn't save the image: " + ex.Message); }
            };
            dialog.Canceled += () => dialog.QueueFree();
            AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        // =====================================================================
        // Small UI helpers
        // =====================================================================

        private static string SeverityText(FindingSeverity s) => s switch
        {
            FindingSeverity.Problem => "Problem",
            FindingSeverity.Warning => "Review",
            FindingSeverity.AutoFixed => "Auto-fixed",
            _ => "Info"
        };

        private static Color SeverityColor(FindingSeverity s) => s switch
        {
            FindingSeverity.Problem => Bad,
            FindingSeverity.Warning => Warn,
            FindingSeverity.AutoFixed => Fixed,
            _ => ThemeColors.TextMuted
        };

        private Control CoverageBar(string label, int done, int total, string detail)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(220, 0) });
            var bar = new ProgressBar { MinValue = 0, MaxValue = Math.Max(1, total), Value = done, ShowPercentage = false, CustomMinimumSize = new Vector2(260, 18), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            var fill = new StyleBoxFlat { BgColor = total > 0 && done >= total ? Good : ThemeColors.Accent };
            bar.AddThemeStyleboxOverride("fill", fill);
            row.AddChild(bar);
            row.AddChild(new Label { Text = total == 0 ? "—" : $"{done} / {total}" });
            if (!string.IsNullOrEmpty(detail)) row.AddChild(Kit.Subtle(detail));
            return row;
        }

        // =====================================================================
        // Overview
        // =====================================================================

        private string _selectedFindingKey;
        private bool _showQuiet;

        private Control BuildOverview()
        {
            var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            var left = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.4f };
            left.AddThemeConstantOverride("separation", 6);
            split.AddChild(left);

            var m = _original.Manifest;
            left.AddChild(Kit.Heading($"{m?.Name}  {m?.GetActualVersion()}"));
            string game = _report.Names?.Game ?? m?.GameName;
            string namesText = _report.Names == null
                ? "No name list yet: connect a slot of this game, or set your Archipelago install path."
                : $"Checked against {game} names from the {_report.Names.Source} ({_report.Names.Items.Count} items, {_report.Names.Locations.Count} locations{(string.IsNullOrEmpty(_report.Names.Version) || _report.Names.Version == "0.0.0" ? "" : ", version " + _report.Names.Version)}).";
            left.AddChild(Kit.Subtle($"By {m?.Author}. {namesText}"));

            left.AddChild(CoverageBar("Key Items tiles linked", _report.TilesLinked, _report.TilesTotal,
                _report.TilesByScript > 0 ? $"{_report.TilesByScript} by the pack's script" : ""));
            left.AddChild(CoverageBar("Pin sections linked", _report.SectionsLinked, _report.SectionsTotal,
                $"script {_report.SectionsByScript} · name {_report.SectionsByName} · partial {_report.SectionsLoose} · yours {_report.SectionsByFix}"));
            left.AddChild(CoverageBar("Game locations on a map", _report.ApLocationsPlaced, _report.ApLocationsTotal, ""));

            var recs = Recommendations();
            if (recs.Count > 0)
            {
                int strong = recs.Count(r => r.Suggestions[0].Score >= 0.85);
                var review = Kit.Button($"Review {recs.Count} recommended fix{(recs.Count == 1 ? "" : "es")} ({strong} at 85%+)…", "See every suggested match in one list and apply them one at a time or all at once", () => SelectTab("Recommended"));
                review.AddThemeColorOverride("font_color", Good);
                left.AddChild(review);
            }

            var filterRow = new HBoxContainer();
            var quiet = new CheckBox { Text = "Show info, auto-fixed and ignored", ButtonPressed = _showQuiet };
            quiet.Toggled += on => { _showQuiet = on; RenderCurrentTab(); };
            filterRow.AddChild(quiet);
            left.AddChild(filterRow);

            var tree = new Tree { Columns = 3, HideRoot = true, ColumnTitlesVisible = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SelectMode = Tree.SelectModeEnum.Row };
            tree.SetColumnTitle(0, "Status");
            tree.SetColumnTitle(1, "Area");
            tree.SetColumnTitle(2, "Finding");
            tree.SetColumnExpandRatio(0, 1);
            tree.SetColumnExpandRatio(1, 1);
            tree.SetColumnExpandRatio(2, 6);
            var root = tree.CreateItem();
            TreeItem toSelect = null;
            int shown = 0;
            foreach (var f in _report.Findings)
            {
                bool quietOne = f.Ignored || f.Severity < FindingSeverity.Warning;
                if (quietOne && !_showQuiet) continue;
                var row = tree.CreateItem(root);
                row.SetText(0, f.Ignored ? "Ignored" : SeverityText(f.Severity));
                row.SetCustomColor(0, f.Ignored ? Muted : SeverityColor(f.Severity));
                row.SetText(1, f.Category);
                row.SetText(2, f.Title);
                if (f.Ignored) { row.SetCustomColor(1, Muted); row.SetCustomColor(2, Muted); }
                row.SetMetadata(0, f.Key);
                if (f.Key == _selectedFindingKey) toSelect = row;
                shown++;
            }
            if (shown == 0)
            {
                var row = tree.CreateItem(root);
                row.SetText(2, _report.NeedsReview.Any() ? "Nothing to show with these filters." : "Nothing needs your attention.");
            }
            left.AddChild(tree);

            var detail = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var detailBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            detailBox.AddThemeConstantOverride("separation", 8);
            detail.AddChild(detailBox);
            split.AddChild(detail);

            void ShowFinding(string key)
            {
                foreach (Node c in detailBox.GetChildren()) c.QueueFree();
                var f = _report.Findings.FirstOrDefault(x => x.Key == key);
                if (f == null) { detailBox.AddChild(Kit.Subtle("Select a finding to see details and fixes.")); return; }
                _selectedFindingKey = key;
                BuildFindingDetail(f, detailBox);
                MainTrackerWindow.SetFontSizeRecursive(detailBox, _fontSize);
            }
            tree.ItemSelected += () =>
            {
                var meta = tree.GetSelected()?.GetMetadata(0) ?? default;
                if (meta.VariantType == Variant.Type.String) ShowFinding(meta.AsString());
            };
            if (toSelect != null) { toSelect.Select(0); ShowFinding(_selectedFindingKey); }
            else ShowFinding(null);
            return split;
        }

        private void BuildFindingDetail(Finding f, VBoxContainer box)
        {
            var title = new Label { Text = f.Title, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            title.SetMeta("font_size_ratio", 1.1);
            title.AddThemeColorOverride("font_color", SeverityColor(f.Severity));
            box.AddChild(title);
            if (!string.IsNullOrEmpty(f.Detail)) box.AddChild(Kit.Text(f.Detail, ThemeColors.TextMuted));

            // Suggestions with one-click "Use".
            if (f.Suggestions.Count > 0)
            {
                box.AddChild(Kit.Subtle("Likely matches:"));
                foreach (var s in f.Suggestions)
                {
                    var row = new HBoxContainer();
                    row.AddChild(new Label { Text = $"{s.Label}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart });
                    row.AddChild(new Label { Text = $"{s.Score:P0}", Modulate = s.Score >= 0.85 ? Good : s.Score >= 0.6 ? Warn : Muted });
                    row.AddChild(Kit.Button("Use", "Apply this match", () => ApplySuggestion(f, s)));
                    box.AddChild(row);
                }
            }

            var actions = new HFlowContainer();
            actions.AddThemeConstantOverride("h_separation", 6);
            actions.AddThemeConstantOverride("v_separation", 6);
            box.AddChild(actions);
            string subject = f.Subject ?? "";
            string what = subject.Contains(':') ? subject.Substring(subject.IndexOf(':') + 1) : subject;

            if (f.Actions.HasFlag(FindingActions.LinkItem))
                actions.AddChild(Kit.Button("Choose item…", "Pick the Archipelago item this tile tracks", () => PickTileItem(what, ItemNameOf(what), f.Suggestions)));
            if (f.Actions.HasFlag(FindingActions.AddImage))
                actions.AddChild(Kit.Button("Add image…", "Use an image file for this tile", () => PickTileImage(what)));
            if (f.Actions.HasFlag(FindingActions.HideTile))
                actions.AddChild(Kit.Button("Hide tile", "Remove this tile from Key Items", () => SetTileHidden(what, true)));
            if (f.Actions.HasFlag(FindingActions.ToggleGrid))
                actions.AddChild(Kit.Button("Show this grid anyway", "Show it in Key Items", () => SetGridHidden(subject, false)));
            if (f.Actions.HasFlag(FindingActions.LinkLocation))
            {
                var parts = what.Split('|');
                actions.AddChild(Kit.Button("Choose location…", "Pick the Archipelago location this section shows", () => PickSectionLocation(parts[0], parts.Length > 1 ? parts[1] : "", f.Suggestions)));
                actions.AddChild(Kit.Button("Show on map", "Open the pin in the Maps tab", () => { _focusPinPath = parts[0]; SelectTab("Maps"); }));
            }
            if (f.Actions.HasFlag(FindingActions.AddPin))
                actions.AddChild(Kit.Button("Place pins…", "Go to the Locations tab to place pins for these", () => SelectTab("Locations")));
            if (f.Actions.HasFlag(FindingActions.ReplaceMapImage))
            {
                actions.AddChild(Kit.Button("Replace background…", "Use an image file for this map", () => ReplaceMapImage(what)));
                if (_original.Maps.TryGetValue(what, out var map) && !string.IsNullOrEmpty(map.MapBg))
                    actions.AddChild(Kit.Button("Save original image…", "Save a copy of the pack's image where you choose, to re-save it as PNG", () => SavePackImage(map.MapBg)));
            }
            if (f.Actions.HasFlag(FindingActions.ResetFixes) && f.RelatedSubjects.Count > 0)
            {
                var subjects = f.RelatedSubjects.ToList();
                actions.AddChild(Kit.Button($"Undo these {subjects.Count} links", "Remove these links; the Doctor will suggest a match for each again", () =>
                    PackFixes.Edit(_key, $"Undo {subjects.Count} links", file => file.Links.RemoveAll(l => subjects.Contains(l.Subject)))));
            }
            if (f.Actions.HasFlag(FindingActions.RestoreFix))
            {
                var s = PackFixes.Get(_key).Superseded.FirstOrDefault(x => x.Subject == subject);
                if (s != null) actions.AddChild(Kit.Button("Restore my fix", "Use your fix again over the author's change", () => PackFixes.Restore(_key, s, _original)));
            }
            if (f.Severity >= FindingSeverity.Warning || f.Ignored)
                actions.AddChild(Kit.Button(f.Ignored ? "Stop ignoring" : "Ignore", f.Ignored ? "Show this finding again" : "Hide this finding (it stays in the report to the author)", () => ToggleIgnore(f)));

            if (f.Details.Count > 0)
            {
                box.AddChild(Kit.Subtle($"Entries ({f.Details.Count}):"));
                var list = new ItemList { CustomMinimumSize = new Vector2(0, 260), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
                foreach (var d in f.Details) list.AddItem(d);
                box.AddChild(list);
                box.AddChild(Kit.Button("Copy list", "Copy these entries", () => DisplayServer.ClipboardSet(string.Join("\n", f.Details))));
            }
        }

        private string ItemNameOf(string code) =>
            _report?.Pack?.ItemsByCode.TryGetValue(code, out var item) == true ? item.Name : code;

        private void ApplySuggestion(Finding f, Suggestion s)
        {
            string subject = f.Subject ?? "";
            string what = subject.Contains(':') ? subject.Substring(subject.IndexOf(':') + 1) : subject;
            if (f.Actions.HasFlag(FindingActions.LinkItem)) LinkTile(what, s.Id, s.Label);
            else if (f.Actions.HasFlag(FindingActions.LinkLocation))
            {
                var parts = what.Split('|');
                LinkSection(parts[0], parts.Length > 1 ? parts[1] : "", s.Id, s.Label);
            }
        }

        // =====================================================================
        // Your fixes
        // =====================================================================

        private Control BuildFixesTab()
        {
            var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 6);
            scroll.AddChild(box);
            var f = PackFixes.Get(_key);

            box.AddChild(Kit.Subtle("Your fixes are stored beside the pack (never inside it) and applied when it loads. " +
                              "If the pack's author later changes the same thing, their version wins and your fix is set aside below, ready to restore."));
            var top = new HBoxContainer();
            top.AddChild(Kit.Button("Open fixes folder", "Show where fixes are saved", () => { Directory.CreateDirectory(PackFixes.Dir); ExternalLinks.OpenFolder(PackFixes.Dir); }));
            top.AddChild(Kit.Button("Reset everything…", "Remove all your fixes for this pack", () => Confirm("Remove every fix for this pack?", () => PackFixes.Reset(_key)), f.Count > 0 || f.Ignored.Count > 0));
            box.AddChild(top);

            void Group<T>(string title, string category, List<T> list, Func<T, string> describe) where T : PackFixBase
            {
                box.AddChild(Kit.Heading($"{title} ({list.Count})"));
                if (list.Count == 0) { box.AddChild(Kit.Subtle("None.")); return; }
                box.AddChild(Kit.Button($"Reset all {title.ToLowerInvariant()}", null, () => PackFixes.Reset(_key, category)));
                foreach (var fix in list.OrderByDescending(x => x.Made))
                {
                    var row = new HBoxContainer();
                    row.AddChild(new Label { Text = describe(fix), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart });
                    row.AddChild(Kit.Subtle(fix.Made.ToString("yyyy-MM-dd HH:mm")));
                    string subj = fix.Subject;
                    row.AddChild(Kit.Button("Reset", "Use the author's version again", () => PackFixes.Reset(_key, category, subj)));
                    box.AddChild(row);
                }
            }

            Group("Tiles", "tiles", f.Tiles, t =>
                $"Tile '{t.Code}': " + string.Join(", ", new[]
                {
                    t.ApItemId != null ? $"tracks {t.ApItemName}" : null,
                    t.Hidden ? "hidden" : null,
                    t.ImageFile != null ? "custom image" : null
                }.Where(x => x != null)));
            Group("Added tiles", "tiles", f.AddedTiles, t => $"Added tile for {t.ApItemName}");
            Group("Grids", "grids", f.Grids, g => $"Grid {g.Subject.Substring(5)}: " + string.Join(", ", new[]
            {
                g.Hidden == true ? "hidden" : g.Hidden == false ? "shown" : null,
                g.ItemSize != null ? $"tile size {g.ItemSize}" : null,
                g.Rows != null ? "reordered" : null
            }.Where(x => x != null)));
            Group("Location links", "links", f.Links, l => $"{l.PinPath} / {(string.IsNullOrEmpty(l.SectionName) ? "(pin)" : l.SectionName)} → {(l.ApLocationId == null ? "nothing" : l.ApLocationName)}");
            Group("Pins", "pins", f.Pins, p => p.Removed ? $"Removed pin {p.PinPath}" : $"Moved pin {p.PinPath} on {p.MapId}");
            Group("Added pins", "pins", f.AddedPins, p => $"Added pin '{p.Name}' on {p.MapId} ({p.ApLocationIds.Count} location{(p.ApLocationIds.Count == 1 ? "" : "s")})");
            Group("Map images", "maps", f.MapImages, m => $"Map '{m.MapId}' uses {m.ImageFile}");

            box.AddChild(Kit.Heading($"Set aside by pack updates ({f.Superseded.Count})"));
            if (f.Superseded.Count == 0) box.AddChild(Kit.Subtle("None."));
            foreach (var s in f.Superseded.OrderByDescending(x => x.When))
            {
                var row = new HBoxContainer();
                row.AddChild(new Label { Text = $"{s.Subject} (author changed it in {s.PackVersion})", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart });
                var sc = s;
                row.AddChild(Kit.Button("Restore", "Use your fix again", () => PackFixes.Restore(_key, sc, _original)));
                box.AddChild(row);
            }

            box.AddChild(Kit.Heading($"Ignored findings ({f.Ignored.Count})"));
            if (f.Ignored.Count > 0) box.AddChild(Kit.Button("Stop ignoring all", null, () => PackFixes.Edit(_key, "Stop ignoring all", x => x.Ignored.Clear())));
            return scroll;
        }

        private void Confirm(string text, Action onYes)
        {
            var d = new ConfirmationDialog { DialogText = text, Title = "Pack Doctor" };
            d.Confirmed += () => { onYes(); d.QueueFree(); };
            d.Canceled += () => d.QueueFree();
            AddChild(d);
            d.PopupCentered();
        }

        // =====================================================================
        // Report to the author
        // =====================================================================

        private Control BuildReportTab()
        {
            var box = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 6);
            box.AddChild(Kit.Subtle("A summary of problems in the pack itself, ready to paste into an issue on the pack's page. Your local fixes and settings aren't included."));
            string text = PackDoctor.AuthorReport(_report);
            var edit = new TextEdit { Text = text, Editable = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, WrapMode = TextEdit.LineWrappingMode.Boundary };
            edit.AddThemeFontSizeOverride("font_size", _fontSize);
            box.AddChild(edit);
            var row = new HBoxContainer();
            row.AddChild(Kit.Button("Copy", "Copy the report", () => { DisplayServer.ClipboardSet(edit.Text); SetStatus("Report copied."); }));
            string issues = IssuesUrl(_original.Manifest?.VersionsUrl);
            if (issues != null) row.AddChild(Kit.Button("Open the pack's issue page", issues, () => ExternalLinks.OpenWeb(issues)));
            box.AddChild(row);
            return box;
        }

        /// <summary>github.com issue page from a raw.githubusercontent.com versions URL, when it is one.</summary>
        private static string IssuesUrl(string versionsUrl)
        {
            if (string.IsNullOrEmpty(versionsUrl)) return null;
            if (!Uri.TryCreate(versionsUrl, UriKind.Absolute, out var uri)) return null;
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                return $"https://github.com/{parts[0]}/{parts[1]}/issues/new";
            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                return $"https://github.com/{parts[0]}/{parts[1]}/issues/new";
            return null;
        }
    }
}
