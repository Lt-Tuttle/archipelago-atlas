#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AP_Atlas.Core;
using AP_Atlas.Core.CheeseTracker;
using AP_Atlas.Core.Spheres;
using Godot;
using Color = Godot.Color;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The Sphere Tracker tab: the spheretracker.de room a multiworld's host created and shared, shown for one slot at a
    /// time (the slot you're viewing in Atlas, or any slot from the explorer): the slot's open locations with their
    /// spheres, its earliest, and the multiworld's earliest. The room's other tables are there as the page has them.
    /// Nothing is shown in race mode.
    /// </summary>
    public partial class SphereTrackerTab : MarginContainer
    {
        public const string SettingsView = "*settings";
        private const char Sep = '\u0001';
        private const int MaxRows = 2000;

        private static readonly Color Good = Colors.LimeGreen;
        private static readonly Color Warn = Colors.Orange;
        private static readonly Color Muted = Colors.Gray;
        private static readonly Color MineColor = Colors.Magenta;

        private readonly AppSettings _settings;
        private readonly SphereService _spheres;
        private readonly Func<IReadOnlyList<MultiworldProfile>> _profiles;
        private readonly Action<string, Color> _toast;

        /// <summary>The explorer list; the window mounts it in the explorer while this tab is shown.</summary>
        public VBoxContainer SidebarContent { get; }

        private string _view;
        private string _sidebarSignature, _settingsSignature;

        private VBoxContainer _trackerPage, _messagePage, _settingsBox;
        private ScrollContainer _settingsPage;
        private Label _title, _subtitle, _problemLabel, _note, _count, _overview;
        /// <summary>The table's columns as shown (the slot's own column is left out while only its rows are shown).</summary>
        private List<int> _shownColumns = new List<int>();
        private HBoxContainer _headerButtons, _problemRow;
        private OptionButton _tablePicker;
        private Button _onlySlot;
        private LineEdit _search;
        private Tree _tree;
        private Timer _refreshDebounce;
        private SphereData _data;
        private string _dataSlot;
        private int _sortColumn = -1;
        private bool _sortDescending;

        public SphereTrackerTab(AppSettings settings, SphereService spheres, Func<IReadOnlyList<MultiworldProfile>> profiles, Action<string, Color> toast)
        {
            _settings = settings;
            _spheres = spheres;
            _profiles = profiles;
            _toast = toast;
            Name = "Sphere Tracker";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            foreach (var side in new[] { "left", "top", "right", "bottom" }) AddThemeConstantOverride("margin_" + side, 10);
            SidebarContent = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            SidebarContent.AddThemeConstantOverride("separation", 4);
            _view = settings.SphereTabView ?? "";
        }

        public override void _Ready()
        {
            BuildTrackerPage();
            _settingsPage = new ScrollContainer { Visible = false, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            _settingsBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _settingsBox.AddThemeConstantOverride("separation", 8);
            _settingsPage.AddChild(_settingsBox);
            AddChild(_settingsPage);
            _messagePage = new VBoxContainer { Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
            _messagePage.AddThemeConstantOverride("separation", 10);
            AddChild(_messagePage);

            _refreshDebounce = new Timer { OneShot = true, WaitTime = 0.3 };
            // Something changed (a link, a read finished): read what's on screen if that's due, and redraw.
            _refreshDebounce.Timeout += () =>
            {
                WatchShown();
                Refresh();
            };
            AddChild(_refreshDebounce);
            // While shown: keep the room fresh (it's read at most every 10 minutes).
            var minute = new Timer { WaitTime = 60, Autostart = true };
            minute.Timeout += () =>
            {
                if (!IsVisibleInTree()) return;
                WatchShown();
                Refresh();
            };
            AddChild(minute);
            _spheres.Changed += QueueRefresh;
            RaceRules.Changed += OnRaceRulesChanged;
            Refresh();
        }

        public override void _ExitTree()
        {
            _spheres.Changed -= QueueRefresh;
            RaceRules.Changed -= OnRaceRulesChanged;
        }

        private void OnRaceRulesChanged()
        {
            _settingsSignature = null;
            _sidebarSignature = null;
            QueueRefresh();
        }

        // =====================================================================
        // Views: a slot ("slot" + profile id + slot name) or the settings
        // =====================================================================

        public static string SlotView(string profileId, string slot) => "slot" + Sep + profileId + Sep + slot;

        private static bool IsSlotView(string view, out string profileId, out string slot)
        {
            profileId = slot = null;
            var parts = (view ?? "").Split(Sep);
            if (parts.Length != 3 || parts[0] != "slot") return false;
            profileId = parts[1];
            slot = parts[2];
            return true;
        }

        /// <summary>The slot shown, if the view is a slot that still exists.</summary>
        private (MultiworldProfile Profile, string Slot) ViewedSlot()
        {
            if (!IsSlotView(_view, out string profileId, out string slot)) return (null, null);
            var profile = _profiles().FirstOrDefault(p => p.Id == profileId);
            string name = profile?.Slots.FirstOrDefault(s => string.Equals(s, slot, StringComparison.OrdinalIgnoreCase));
            return name == null ? (null, null) : (profile, name);
        }

        /// <summary>The tab was just shown: read what it shows if that's due, and draw it.</summary>
        public void OnShown()
        {
            WatchShown();
            Refresh();
        }

        /// <summary>The slot you're viewing in Atlas changed: show its multiworld's room for it (when the tab is shown).</summary>
        public void FollowSlot(string profileId, string slot)
        {
            if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(slot)) return;
            string view = SlotView(profileId, slot);
            if (view != _view) ShowView(view);
        }

        /// <summary>Shows a slot (<see cref="SlotView"/>) or <see cref="SettingsView"/>.</summary>
        public void ShowView(string view)
        {
            if (string.IsNullOrEmpty(view)) view = SettingsView;
            if (view != _view)
            {
                _view = view;
                _settings.SphereTabView = view;
                DataManager.SaveSettings(_settings);
            }
            _sidebarSignature = null;
            WatchShown();
            Refresh();
        }

        private void QueueRefresh()
        {
            if (!IsVisibleInTree()) return; // OnShown redraws
            if (_refreshDebounce != null && _refreshDebounce.IsInsideTree() && _refreshDebounce.IsStopped()) _refreshDebounce.Start();
        }

        /// <summary>Reads the shown room if it's due (Atlas reads nothing for rooms that aren't shown).</summary>
        private void WatchShown()
        {
            if (!IsVisibleInTree()) return;
            var (profile, _) = ViewedSlot();
            if (profile != null) _spheres.Watch(profile.Id);
        }

        /// <summary>Refresh / Try now: reads the shown room again (at most once a minute).</summary>
        private void RefreshShown(bool tryNow)
        {
            var (profile, _) = ViewedSlot();
            if (profile == null) return;
            if (_spheres.ReadRecently(profile.Id))
            {
                _toast("Atlas read it moments ago, so it would be the same. Try again in a minute.", Colors.Gray);
                return;
            }
            CheeseDialogs.Run(this, _spheres.RefreshAsync(profile.Id, tryNow), _toast, null, QueueRefresh);
        }

        // =====================================================================
        // Layout
        // =====================================================================

        private void BuildTrackerPage()
        {
            _trackerPage = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            _trackerPage.AddThemeConstantOverride("separation", 6);
            AddChild(_trackerPage);

            var header = new HBoxContainer();
            header.AddThemeConstantOverride("separation", 6);
            _trackerPage.AddChild(header);
            _title = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, VerticalAlignment = VerticalAlignment.Center };
            _title.SetMeta("font_size_ratio", 1.35);
            header.AddChild(_title);
            _headerButtons = new HBoxContainer();
            _headerButtons.AddThemeConstantOverride("separation", 4);
            header.AddChild(_headerButtons);

            _subtitle = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _subtitle.AddThemeColorOverride("font_color", Muted);
            _trackerPage.AddChild(_subtitle);

            _problemRow = new HBoxContainer { Visible = false };
            _problemRow.AddThemeConstantOverride("separation", 6);
            _problemLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _problemLabel.AddThemeColorOverride("font_color", Warn);
            _problemRow.AddChild(_problemLabel);
            var tryNow = new Button { Text = "Try now", FocusMode = FocusModeEnum.None };
            tryNow.Pressed += () => RefreshShown(tryNow: true);
            _problemRow.AddChild(tryNow);
            _trackerPage.AddChild(_problemRow);

            _overview = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
            _overview.AddThemeColorOverride("font_color", Colors.LightGray);
            _trackerPage.AddChild(_overview);

            var toolbar = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            toolbar.AddThemeConstantOverride("h_separation", 6);
            toolbar.AddThemeConstantOverride("v_separation", 4);
            _trackerPage.AddChild(toolbar);
            _tablePicker = new OptionButton { FocusMode = FocusModeEnum.None, FitToLongestItem = false, TooltipText = "The tables on the host's room page" };
            _tablePicker.ItemSelected += _ =>
            {
                _sortColumn = -1;
                RenderTable();
            };
            toolbar.AddChild(_tablePicker);
            _onlySlot = new Button { ToggleMode = true, ButtonPressed = true, FocusMode = FocusModeEnum.None, TooltipText = "Only the rows that name this slot" };
            _onlySlot.Toggled += _ => RenderTable();
            toolbar.AddChild(_onlySlot);
            _search = new LineEdit { PlaceholderText = "Find…", ClearButtonEnabled = true, CustomMinimumSize = new Vector2(240, 0) };
            _search.TextChanged += _ => RenderTable();
            toolbar.AddChild(_search);
            _count = new Label { VerticalAlignment = VerticalAlignment.Center };
            _count.AddThemeColorOverride("font_color", Muted);
            toolbar.AddChild(_count);
            _note = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _note.AddThemeColorOverride("font_color", Muted);
            _trackerPage.AddChild(_note);
            _tree = new Tree
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                ColumnTitlesVisible = true,
                HideRoot = true,
                SelectMode = Tree.SelectModeEnum.Row,
                CustomMinimumSize = new Vector2(0, 160)
            };
            _tree.AddThemeConstantOverride("v_separation", 6);
            _tree.ColumnTitleClicked += (column, button) =>
            {
                if (button != (long)MouseButton.Left) return;
                int index = column >= 0 && column < _shownColumns.Count ? _shownColumns[(int)column] : (int)column;
                if (_sortColumn == index) _sortDescending = !_sortDescending;
                else
                {
                    _sortColumn = index;
                    _sortDescending = false;
                }
                RenderTable();
            };
            _trackerPage.AddChild(_tree);
        }

        // =====================================================================
        // Explorer list: each multiworld's slots, then the settings
        // =====================================================================

        private void RenderSidebar()
        {
            var profiles = _profiles();
            string signature = _view + "|" + string.Join("|", profiles.Select(p => p.Id + "=" + p.Name + "=" + StateOf(p) + "=" + string.Join(",", p.Slots)));
            if (signature == _sidebarSignature) return;
            _sidebarSignature = signature;
            foreach (Node child in SidebarContent.GetChildren()) child.QueueFree();
            foreach (var p in profiles)
            {
                string state = StateOf(p);
                var header = new Label
                {
                    Text = state.Length == 0 ? p.Name : $"{p.Name}  ({state})",
                    ClipText = true,
                    TooltipText = state switch
                    {
                        "" => "Your slots in this multiworld",
                        "no room" => "No spheretracker.de room from the host is linked (Settings)",
                        _ => "Hidden while race mode applies"
                    },
                    MouseFilter = MouseFilterEnum.Stop
                };
                header.AddThemeColorOverride("font_color", state.Length == 0 ? Colors.LightGray : Muted);
                header.SetMeta("font_size_ratio", 0.9);
                SidebarContent.AddChild(header);
                foreach (var slot in p.Slots)
                    SidebarContent.AddChild(ExplorerButton(slot, $"{slot} in the host's sphere tracker", SlotView(p.Id, slot), state.Length > 0, 12));
                if (p.Slots.Count == 0)
                {
                    var none = new Label { Text = "No slots yet.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
                    none.AddThemeColorOverride("font_color", Muted);
                    SidebarContent.AddChild(none);
                }
                SidebarContent.AddChild(new HSeparator());
            }
            if (profiles.Count == 0)
            {
                var none = new Label { Text = "No multiworlds yet (Connections tab).", AutowrapMode = TextServer.AutowrapMode.WordSmart };
                none.AddThemeColorOverride("font_color", Muted);
                SidebarContent.AddChild(none);
                SidebarContent.AddChild(new HSeparator());
            }
            SidebarContent.AddChild(ExplorerButton("Settings", "The host's spheretracker.de room for each multiworld", SettingsView, false, 0));
            MainTrackerWindow.SetFontSizeRecursive(SidebarContent, _settings.ExplorerFontSize);
        }

        /// <summary>"" when the host's room is linked and shown; otherwise a short reason shown next to the name.</summary>
        private string StateOf(MultiworldProfile p)
        {
            if (string.IsNullOrWhiteSpace(p.SphereTrackerUrl)) return "no room";
            return _spheres.HiddenBecause(p) != null ? "race" : "";
        }

        private Control ExplorerButton(string text, string tooltip, string view, bool dim, int indent)
        {
            var b = new Button { Text = text, TooltipText = tooltip, Alignment = HorizontalAlignment.Left, ToggleMode = true, ButtonPressed = view == _view, FocusMode = FocusModeEnum.None, ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            if (dim && view != _view) b.AddThemeColorOverride("font_color", Muted);
            b.Pressed += () =>
            {
                ShowView(view);
                // Choosing a slot here shows it in Properties too, as clicking its card does.
                if (IsSlotView(view, out string profileId, out string slot)) Inspector.Inspect(InspectTarget.ForSlot(profileId, slot));
            };
            if (indent == 0) return b;
            var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            margin.AddThemeConstantOverride("margin_left", indent);
            margin.AddChild(b);
            return margin;
        }

        // =====================================================================
        // Pages
        // =====================================================================

        /// <summary>Redraws the tab (keeps the table's scroll, search and sort).</summary>
        public void Refresh()
        {
            if (_trackerPage == null) return;
            using var __perf = PerfMonitor.Measure("Sphere Tracker tab: refresh");
            if (_view != SettingsView && ViewedSlot().Profile == null)
            {
                // A view from an earlier Atlas (a multiworld, or My slots) or a slot that's gone: the multiworld's first
                // slot, or any multiworld's, or the settings.
                var profiles = _profiles();
                var first = profiles.FirstOrDefault(p => p.Id == _view && p.Slots.Count > 0) ?? profiles.FirstOrDefault(p => p.Slots.Count > 0);
                _view = first != null ? SlotView(first.Id, first.Slots[0]) : SettingsView;
                _sidebarSignature = null;
            }
            RenderSidebar();
            if (_view == SettingsView)
            {
                ShowPage(_settingsPage);
                RenderSettings();
                return;
            }
            var (profile, slot) = ViewedSlot();
            RenderSlot(profile, slot);
        }

        private void ShowPage(Control page)
        {
            _trackerPage.Visible = page == _trackerPage;
            _settingsPage.Visible = page == _settingsPage;
            _messagePage.Visible = page == _messagePage;
        }

        private void ShowMessage(string title, string text, List<(string Label, Action Action)> actions)
        {
            ShowPage(_messagePage);
            foreach (Node child in _messagePage.GetChildren()) child.QueueFree();
            var heading = new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            heading.SetMeta("font_size_ratio", 1.25);
            _messagePage.AddChild(heading);
            var body = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            body.AddThemeColorOverride("font_color", Muted);
            _messagePage.AddChild(body);
            var row = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.Center };
            row.AddThemeConstantOverride("h_separation", 6);
            foreach (var (label, action) in actions)
            {
                var b = new Button { Text = label, FocusMode = FocusModeEnum.None };
                b.Pressed += action;
                row.AddChild(b);
            }
            _messagePage.AddChild(row);
            MainTrackerWindow.SetFontSizeRecursive(_messagePage, _settings.ContentFontSize);
        }

        private void RenderSlot(MultiworldProfile profile, string slot)
        {
            var view = _spheres.ViewOf(profile.Id);
            if (view.HiddenBecause != null)
            {
                ShowMessage("Spheres are hidden in race mode.", view.HiddenBecause + " Atlas neither shows nor reads the sphere tracker meanwhile.", new List<(string, Action)>());
                return;
            }
            if (view.Room == null)
            {
                ShowMessage($"No sphere tracker from {profile.Name}'s host.",
                    $"Hosts create a spheretracker.de room for their multiworld and share its link. If {profile.Name}'s host has, link it here to see {slot}'s rows. " +
                    "Atlas only takes a room the host created, for this multiworld.",
                    new List<(string, Action)> { ("Link the host's room…", () => AskLinkSite(profile)), ("Settings", () => ShowView(SettingsView)) });
                return;
            }
            ShowPage(_trackerPage);
            var room = view.Room;
            var data = room.Data;
            _title.Text = $"{slot} spheres";
            string about = string.IsNullOrWhiteSpace(data?.RoomName) ? "the host's spheretracker.de room" : $"\"{data.RoomName}\" on spheretracker.de";
            if (!string.IsNullOrWhiteSpace(data?.Creator))
                about += SphereService.SameName(data.Creator, view.Organizer) ? $", created by {data.Creator} (who runs its Cheese Tracker)" : $", created by {data.Creator}";
            var parts = new List<string> { profile.Name, about };
            if (data?.UpdatedUtc != null) parts.Add("room updated " + CtTime.Ago(data.UpdatedUtc));
            parts.Add(room.Busy ? "reading…" : room.FetchedUtc == null ? "not read yet" : "read " + CtTime.Ago(room.FetchedUtc));
            if (room.ReadEvery > TimeSpan.FromMinutes(10))
                parts.Add($"a {room.PageBytes / (1024 * 1024)} MB page, so Atlas reads it every {room.ReadEvery.TotalMinutes:0} minutes while it's shown");
            _subtitle.Text = string.Join(" · ", parts);
            foreach (Node child in _headerButtons.GetChildren()) child.QueueFree();
            _headerButtons.AddChild(HeaderButton("Refresh", "Read the host's room again now (at most once a minute)", () => RefreshShown(tryNow: false), !room.Busy));
            _headerButtons.AddChild(HeaderButton("Open ↗", "Open the host's room in your browser", () => AP_Atlas.Core.ExternalLinks.OpenWeb(room.Url)));
            _headerButtons.AddChild(HeaderButton("Settings", "The host's room for each multiworld", () => ShowView(SettingsView)));
            MainTrackerWindow.SetFontSizeRecursive(_headerButtons, _settings.ContentFontSize);
            _problemRow.Visible = room.Problem != null;
            _problemLabel.Text = room.Problem ?? "";
            // The slot at a glance: its open locations and the earliest, against the multiworld's earliest.
            var open = SphereTable.OpenLocations(data);
            _overview.Visible = open != null;
            if (open != null)
            {
                var (rows, earliest) = SphereTable.OpenFor(open, slot);
                var first = SphereTable.EarliestOpen(SphereTable.Summary(data));
                _overview.Text = (rows.Count == 0
                                     ? $"{slot} has no open locations on the host's tracker."
                                     : $"{slot}: {rows.Count:N0} open location{(rows.Count == 1 ? "" : "s")}, the earliest in sphere {earliest}.") +
                                 (first == null ? "" : $" The multiworld's earliest open location{(first.Value.Unfound == 1 ? " is" : "s are")} in sphere {first.Value.Sphere} ({first.Value.Unfound:N0} left).");
            }
            _onlySlot.Text = $"Only {slot}'s rows";
            _note.Text = (room.Data?.Tables?.Count ?? 0) == 0
                ? (room.Busy ? "Reading the host's room…" : room.Problem == null ? "Atlas couldn't read this page's layout. Open it in your browser." : "")
                : "As the host's page has it. Atlas only reads the room; it never asks spheretracker.de to refresh it.";
            if (ReferenceEquals(room.Data, _data) && _dataSlot == slot && _tree.GetRoot() != null) return;
            _data = room.Data;
            _dataSlot = slot;
            var tables = _data?.Tables ?? new List<PageTable>();
            var summary = SphereTable.Summary(_data);
            string previous = _tablePicker.Selected >= 0 && _tablePicker.Selected < _tablePicker.ItemCount ? _tablePicker.GetItemText(_tablePicker.Selected) : null;
            _tablePicker.Clear();
            int select = -1;
            for (int i = 0; i < tables.Count; i++)
            {
                // Tables the page fills in with script are empty here: left out.
                if (tables[i].Rows.Count == 0 && tables[i] != open) continue;
                string label = tables[i] == open ? "Open locations" : tables[i] == summary ? "Summary" : string.IsNullOrWhiteSpace(tables[i].Title) ? $"Table {i + 1}" : tables[i].Title;
                _tablePicker.AddItem(label, i);
                if (label == previous) select = _tablePicker.ItemCount - 1;
            }
            if (select < 0 && open != null) select = Enumerable.Range(0, _tablePicker.ItemCount).FirstOrDefault(k => _tablePicker.GetItemId(k) == tables.IndexOf(open));
            _tablePicker.Visible = _tablePicker.ItemCount > 1;
            if (_tablePicker.ItemCount > 0) _tablePicker.Select(Math.Max(0, select));
            RenderTable();
        }

        private void RenderTable()
        {
            var bar = _tree.GetChildren(true).OfType<VScrollBar>().FirstOrDefault();
            double scroll = bar?.Value ?? 0;
            _tree.Clear();
            var tables = _data?.Tables ?? new List<PageTable>();
            if (tables.Count == 0 || _tablePicker.ItemCount == 0)
            {
                _count.Text = "";
                _onlySlot.Visible = false;
                return;
            }
            if (bar != null) Ui.Defer(bar, () => bar.Value = scroll);
            var table = tables[Math.Clamp(_tablePicker.GetItemId(Math.Max(0, _tablePicker.Selected)), 0, tables.Count - 1)];
            int slotColumn = SphereTable.ColumnOf(table, "Finder");
            if (slotColumn < 0) slotColumn = SphereTable.ColumnOf(table, "Slot");
            var pattern = _dataSlot == null ? null : SphereTable.SlotPattern(_dataSlot);
            // "Only this slot's rows" applies to tables that list slots (the open locations); the summary has none.
            bool slotTable = slotColumn >= 0 || (pattern != null && table.Rows.Any(r => SphereTable.Names(r, pattern)));
            _onlySlot.Visible = slotTable;
            bool onlySlot = slotTable && pattern != null && _onlySlot.ButtonPressed;
            _shownColumns = Enumerable.Range(0, table.Columns.Count).Where(i => !(onlySlot && i == slotColumn)).ToList();
            _tree.Columns = Math.Max(1, _shownColumns.Count);
            for (int k = 0; k < _shownColumns.Count; k++)
            {
                int i = _shownColumns[k];
                _tree.SetColumnTitle(k, table.Columns[i] + (i == _sortColumn ? (_sortDescending ? " ▼" : " ▲") : ""));
                _tree.SetColumnTitleAlignment(k, HorizontalAlignment.Left);
                _tree.SetColumnExpand(k, true);
                _tree.SetColumnExpandRatio(k, string.Equals(table.Columns[i], "Location", StringComparison.OrdinalIgnoreCase) ? 4 : 1);
                _tree.SetColumnClipContent(k, true);
                _tree.SetColumnCustomMinimumWidth(k, 60);
            }
            var all = SphereTable.FilterAndSort(table, _search.Text, _sortColumn, _sortDescending);
            var rows = onlySlot ? all.Where(r => SphereTable.IsSlotRow(table, r, _dataSlot, pattern)).ToList() : all;
            var root = _tree.CreateItem();
            foreach (var r in rows.Take(MaxRows))
            {
                var item = _tree.CreateItem(root);
                bool mine = pattern != null && SphereTable.IsSlotRow(table, r, _dataSlot, pattern);
                for (int k = 0; k < _shownColumns.Count; k++)
                {
                    int i = _shownColumns[k];
                    item.SetText(k, r[i]);
                    item.SetTooltipText(k, r[i]);
                    item.SetCustomColor(k, mine && i == slotColumn ? MineColor : mine ? Colors.White : Colors.LightGray);
                }
            }
            if (rows.Count == 0)
            {
                var empty = _tree.CreateItem(root);
                empty.SetText(0, all.Count == 0 ? "Nothing matches." : $"{_dataSlot} has no rows here. Switch off \"Only {_dataSlot}'s rows\" to see the whole table.");
                empty.SetCustomColor(0, Muted);
                empty.SetExpandRight(0, true);
                for (int k = 0; k < _shownColumns.Count; k++) empty.SetSelectable(k, false);
            }
            _count.Text = (rows.Count != all.Count ? $"{rows.Count:N0} of {all.Count:N0} rows" : $"{rows.Count:N0} rows") +
                          (rows.Count > MaxRows ? $" (showing the first {MaxRows:N0})" : "");
            MainTrackerWindow.SetFontSizeRecursive(_tree, _settings.ContentFontSize);
        }

        private Button HeaderButton(string text, string tooltip, Action onPressed, bool enabled = true)
        {
            var b = new Button { Text = text, TooltipText = tooltip, Disabled = !enabled, FocusMode = FocusModeEnum.None };
            b.Pressed += () => onPressed();
            return b;
        }

        // =====================================================================
        // Settings: the host's room for each multiworld
        // =====================================================================

        private void RenderSettings()
        {
            var profiles = _profiles();
            string signature = string.Join("|", profiles.Select(p => $"{p.Id}={p.Name}={p.SphereTrackerUrl}={_spheres.HiddenBecause(p)}"));
            if (signature == _settingsSignature) return;
            _settingsSignature = signature;
            foreach (Node child in _settingsBox.GetChildren()) child.QueueFree();

            _settingsBox.AddChild(Heading("Sphere Tracker settings", ThemeColors.Accent.Lightened(0.2f), 1.3f));
            _settingsBox.AddChild(Text("Hosts create a spheretracker.de room for their multiworld and share its link with the players; it lists every slot's open locations with their sphere. " +
                                       "Atlas uses only that, and takes a room link only when the room exists (rooms need a login to create; Atlas never creates one), " +
                                       "isn't another multiworld's (when Cheese Tracker tells Atlas which tracker this multiworld has), and was created by the host: " +
                                       "the room names its creator, and Atlas takes it at once when that's the multiworld's organizer on Cheese Tracker, otherwise only if you confirm the creator is the host. " +
                                       "The room stays hidden while race mode applies, and Atlas reads it only while this tab shows it, at most every 10 minutes; " +
                                       "it never asks spheretracker.de to refresh a room.", Colors.LightGray));
            if (profiles.Count == 0) _settingsBox.AddChild(Text("No multiworlds yet: add one on the Connections tab.", Muted));
            foreach (var profile in profiles)
            {
                var p = profile;
                _settingsBox.AddChild(new HSeparator());
                var nameRow = new HBoxContainer();
                nameRow.AddThemeConstantOverride("separation", 6);
                var name = new Label { Text = profile.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                name.SetMeta("font_size_ratio", 1.1);
                nameRow.AddChild(name);
                if (profile.Slots.Count > 0 && !string.IsNullOrWhiteSpace(profile.SphereTrackerUrl))
                    nameRow.AddChild(SmallButton("Show", $"Show {profile.Slots[0]}'s rows", () => ShowView(SlotView(p.Id, p.Slots[0]))));
                _settingsBox.AddChild(nameRow);
                string hidden = _spheres.HiddenBecause(profile);
                if (hidden != null) _settingsBox.AddChild(Text("Hidden now: " + hidden, Warn));
                var row = new HFlowContainer();
                row.AddThemeConstantOverride("h_separation", 6);
                row.AddThemeConstantOverride("v_separation", 4);
                bool linked = !string.IsNullOrWhiteSpace(profile.SphereTrackerUrl);
                var label = new Label { Text = linked ? "Host's room: " + profile.SphereTrackerUrl : "Host's room: not linked", VerticalAlignment = VerticalAlignment.Center };
                label.AddThemeColorOverride("font_color", linked ? Good : Muted);
                row.AddChild(label);
                row.AddChild(SmallButton(linked ? "Change…" : "Link…", "Paste the spheretracker.de room link your host created and shared", () => AskLinkSite(p)));
                if (linked)
                {
                    row.AddChild(SmallButton("Unlink", "Stop using this room", () => { _spheres.UnlinkSphereSite(p.Id); _settingsSignature = null; _sidebarSignature = null; }));
                    row.AddChild(SmallButton("Open ↗", "Open the host's room in your browser", () => AP_Atlas.Core.ExternalLinks.OpenWeb(p.SphereTrackerUrl)));
                }
                _settingsBox.AddChild(row);
            }
            MainTrackerWindow.SetFontSizeRecursive(_settingsBox, _settings.ContentFontSize);
        }

        private void AskLinkSite(MultiworldProfile profile)
        {
            Dialogs.Prompt(this, "Link the host's sphere tracker",
                $"Paste the spheretracker.de room link {profile.Name}'s host shared (spheretracker.de/room/…). Atlas checks the room exists, that it's this multiworld's, and who created it.",
                profile.SphereTrackerUrl, "https://spheretracker.de/room/…", "Check", text => CheckAndLink(profile, text));
        }

        /// <summary>Reads the room once; links it at once if its creator runs the multiworld's Cheese Tracker, else asks.</summary>
        private void CheckAndLink(MultiworldProfile profile, string text) => AP_Atlas.Core.Async.Fire(CheckAndLinkAsync(profile, text), "checking the spheretracker.de room");

        private async Task CheckAndLinkAsync(MultiworldProfile profile, string text)
        {
            SphereRoomCheck check;
            try
            {
                check = await _spheres.CheckRoomAsync(profile.Id, text);
            }
            catch (Exception ex)
            {
                if (IsInstanceValid(this)) _toast("Checking the room failed: " + ex.Message, Colors.Salmon);
                return;
            }
            if (!IsInstanceValid(this)) return;
            if (check.Error != null)
            {
                _toast(check.Error, Colors.Salmon);
                return;
            }
            if (check.ByOrganizer)
            {
                FinishLink(profile, check, confirmed: false);
                return;
            }
            string room = string.IsNullOrWhiteSpace(check.Data?.RoomName) ? "This room" : $"The room \"{check.Data.RoomName}\"";
            string question, requirement;
            if (check.Creator.Length == 0)
            {
                question = $"{room} doesn't say who created it. Only link it if {profile.Name}'s host created it and shared the link.";
                requirement = $"{profile.Name}'s host created this room";
            }
            else if (check.Organizer != null)
            {
                question = $"{room} was created by {check.Creator}, but Cheese Tracker says {profile.Name} is organized by {check.Organizer}. Only link it if {check.Creator} is the host.";
                requirement = $"{check.Creator} is {profile.Name}'s host";
            }
            else
            {
                question = $"{room} was created by {check.Creator}. Atlas can't tell who hosts {profile.Name} (once it's linked to Cheese Tracker, Atlas checks this itself), so only link it if {check.Creator} is the host.";
                requirement = $"{check.Creator} is {profile.Name}'s host";
            }
            Dialogs.Confirm(this, "Is this the host's room?", question, "Link", () => FinishLink(profile, check, confirmed: true), requirement);
        }

        private void FinishLink(MultiworldProfile profile, SphereRoomCheck check, bool confirmed)
        {
            string error = _spheres.LinkRoom(check, confirmed);
            if (error != null)
            {
                _toast(error, Colors.Salmon);
                return;
            }
            _toast(check.ByOrganizer ? $"Linked: the room was created by {check.Creator}, who runs {profile.Name}'s Cheese Tracker." : $"Linked {profile.Name}'s sphere tracker.", Colors.Gray);
            _settingsSignature = null;
            _sidebarSignature = null;
            WatchShown();
            Refresh();
        }

        // =====================================================================
        // Small builders
        // =====================================================================

        private static Button SmallButton(string text, string tooltip, Action onPressed)
        {
            var b = new Button { Text = text, TooltipText = tooltip ?? "", FocusMode = FocusModeEnum.None };
            b.Pressed += () => onPressed();
            return b;
        }

        private static Label Heading(string text, Color color, float ratio = 1.15f)
        {
            var label = new Label { Text = text, ClipText = true };
            label.AddThemeColorOverride("font_color", color);
            label.SetMeta("font_size_ratio", ratio);
            return label;
        }

        private static Label Text(string text, Color color)
        {
            var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            label.AddThemeColorOverride("font_color", color);
            return label;
        }
    }
}
