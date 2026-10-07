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
    public partial class SphereTrackerTab : ExplorerTabBase
    {
        public const string SettingsView = "*settings";
        private const char Sep = '\u0001';
        private const int MaxRows = 2000;

        private readonly SphereService _spheres;
        private string _settingsSignature;

        private VBoxContainer _trackerPage, _settingsBox;
        private ScrollContainer _settingsPage;
        private Label _title, _subtitle, _problemLabel, _note, _overview;
        private HBoxContainer _headerButtons, _problemRow;
        private OptionButton _tablePicker;
        private Button _onlySlot;
        private AtlasTable _table;
        private SphereData _data;
        private string _dataSlot;

        public SphereTrackerTab(AppSettings settings, SphereService spheres, Func<IReadOnlyList<MultiworldProfile>> profiles, Action<string, Color> toast)
            : base(settings, profiles, toast, "Sphere Tracker", settings.SphereTabView)
        {
            _spheres = spheres;
        }

        protected override string DefaultView => SettingsView;

        protected override void SaveView(string view)
        {
            Settings.SphereTabView = view;
            // A view preference: saved with the next burst of settings (every slot that connects is followed).
            DataManager.SaveSettingsSoon(Settings);
        }

        protected override void BuildPages()
        {
            BuildTrackerPage();
            _settingsPage = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            _settingsBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _settingsBox.AddThemeConstantOverride("separation", 8);
            _settingsPage.AddChild(_settingsBox);
            AddPage(_settingsPage);
        }

        protected override TreeSubscriptions Subscribe(TreeSubscriptions subscriptions) => subscriptions
            .On(() => _spheres.Changed += QueueRefresh, () => _spheres.Changed -= QueueRefresh)
            .On(() => RaceRules.Changed += OnRaceRulesChanged, () => RaceRules.Changed -= OnRaceRulesChanged);

        private void OnRaceRulesChanged()
        {
            _settingsSignature = null;
            InvalidateSidebar();
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
            if (!IsSlotView(View, out string profileId, out string slot)) return (null, null);
            var profile = Profiles().FirstOrDefault(p => p.Id == profileId);
            string name = profile?.Slots.FirstOrDefault(s => string.Equals(s, slot, StringComparison.OrdinalIgnoreCase));
            return name == null ? (null, null) : (profile, name);
        }

        /// <summary>The slot you're viewing in Atlas changed: show its multiworld's room for it (when the tab is shown).</summary>
        public void FollowSlot(string profileId, string slot)
        {
            if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(slot)) return;
            string view = SlotView(profileId, slot);
            if (view != View) ShowView(view);
        }

        /// <summary>Reads the shown room if it's due (Atlas reads nothing for rooms that aren't shown).</summary>
        protected override void WatchShown()
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
                Toast("Atlas read it moments ago, so it would be the same. Try again in a minute.", ThemeColors.TextSubtle);
                return;
            }
            CheeseDialogs.Run(this, _spheres.RefreshAsync(profile.Id, tryNow), Toast, null, QueueRefresh);
        }

        // =====================================================================
        // Layout
        // =====================================================================

        private void BuildTrackerPage()
        {
            _trackerPage = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            _trackerPage.AddThemeConstantOverride("separation", 6);
            AddPage(_trackerPage);

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
            _subtitle.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            _trackerPage.AddChild(_subtitle);

            _problemRow = new HBoxContainer { Visible = false };
            _problemRow.AddThemeConstantOverride("separation", 6);
            _problemLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _problemLabel.AddThemeColorOverride("font_color", ThemeColors.Warning);
            _problemRow.AddChild(_problemLabel);
            _problemRow.AddChild(Kit.Button("Try now", "Read the host's room again now", () => RefreshShown(tryNow: true)));
            _trackerPage.AddChild(_problemRow);

            _overview = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
            _overview.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
            _trackerPage.AddChild(_overview);

            _table = new AtlasTable("sphere", Settings, text => text) { MaxRows = MaxRows, Toast = Toast };
            // The table's search, count and menus join the tab's own toolbar: the room's tables, then this slot's rows.
            var toolbar = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            toolbar.AddThemeConstantOverride("h_separation", 6);
            toolbar.AddThemeConstantOverride("v_separation", 4);
            _trackerPage.AddChild(toolbar);
            _tablePicker = new OptionButton { FitToLongestItem = false, TooltipText = "The tables on the host's room page", AccessibilityName = Tr("Table") };
            _tablePicker.ItemSelected += _ => RenderTable();
            toolbar.AddChild(_tablePicker);
            _onlySlot = new Button { ToggleMode = true, ButtonPressed = true, TooltipText = "Only the rows that name this slot", AccessibilityName = Tr("Only this slot") };
            _onlySlot.Toggled += _ => RenderTable();
            toolbar.AddChild(_onlySlot);
            toolbar.AddChild(_table.Take(_table.SearchBox));
            _table.SearchBox.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            toolbar.AddChild(_table.Take(_table.CountLabel));
            toolbar.AddChild(_table.Take(_table.ColumnsMenu));
            toolbar.AddChild(_table.Take(_table.ExportMenu));
            _table.Toolbar.Visible = false;
            _note = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _note.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            _trackerPage.AddChild(_note);
            _trackerPage.AddChild(_table);
        }

        // =====================================================================
        // Explorer list: each multiworld's slots, then the settings
        // =====================================================================

        private void RenderSidebar()
        {
            var profiles = Profiles();
            string signature = View + "|" + string.Join("|", profiles.Select(p => p.Id + "=" + p.Name + "=" + StateOf(p) + "=" + string.Join(",", p.Slots)));
            RenderSidebar(signature, () =>
            {
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
                    header.AddThemeColorOverride("font_color", state.Length == 0 ? ThemeColors.TextMuted : ThemeColors.TextSubtle);
                    header.SetMeta("font_size_ratio", 0.9);
                    SidebarContent.AddChild(header);
                    foreach (var slot in p.Slots)
                    {
                        string profileId = p.Id, slotName = slot;
                        // Choosing a slot here shows it in Properties too, as clicking its card does.
                        SidebarContent.AddChild(ExplorerButton(slot, $"{slot} in the host's sphere tracker", SlotView(p.Id, slot), state.Length > 0, 12,
                            () => Inspector.Inspect(InspectTarget.ForSlot(profileId, slotName))));
                    }
                    if (p.Slots.Count == 0) SidebarContent.AddChild(SidebarNote("No slots yet."));
                    SidebarContent.AddChild(new HSeparator());
                }
                if (profiles.Count == 0)
                {
                    SidebarContent.AddChild(SidebarNote("No multiworlds yet (the Multiworlds page)."));
                    SidebarContent.AddChild(new HSeparator());
                }
                SidebarContent.AddChild(ExplorerButton("Settings", "The host's spheretracker.de room for each multiworld", SettingsView, false));
            });
        }

        /// <summary>"" when the host's room is linked and shown; otherwise a short reason shown next to the name.</summary>
        private string StateOf(MultiworldProfile p)
        {
            if (string.IsNullOrWhiteSpace(p.SphereTrackerUrl)) return "no room";
            return _spheres.HiddenBecause(p) != null ? "race" : "";
        }

        // =====================================================================
        // Pages
        // =====================================================================

        protected override void RefreshNow()
        {
            if (_trackerPage == null) return;
            using var __perf = PerfMonitor.Measure("Sphere Tracker tab: refresh");
            if (View != SettingsView && ViewedSlot().Profile == null)
            {
                // A view from an earlier Atlas (a multiworld, or My slots) or a slot that's gone: the multiworld's first
                // slot, or any multiworld's, or the settings.
                var profiles = Profiles();
                var first = profiles.FirstOrDefault(p => p.Id == View && p.Slots.Count > 0) ?? profiles.FirstOrDefault(p => p.Slots.Count > 0);
                ShowView(first != null ? SlotView(first.Id, first.Slots[0]) : SettingsView);
                return;
            }
            RenderSidebar();
            if (View == SettingsView)
            {
                ShowPage(_settingsPage);
                RenderSettings();
                return;
            }
            var (profile, slot) = ViewedSlot();
            RenderSlot(profile, slot);
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
            _headerButtons.AddChild(Kit.Button("Refresh", "Read the host's room again now (at most once a minute)", () => RefreshShown(tryNow: false), !room.Busy));
            _headerButtons.AddChild(Kit.Button("Open ↗", "Open the host's room in your browser", () => AP_Atlas.Core.ExternalLinks.OpenWeb(room.Url)));
            _headerButtons.AddChild(Kit.Button("Settings", "The host's room for each multiworld", () => ShowView(SettingsView)));
            MainTrackerWindow.SetFontSizeRecursive(_headerButtons, Settings.ContentFontSize);
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
                                 (first == null ? "" : $" The multiworld's earliest open location{(first.Value.Unfound == 1 ? " is" : "s are")} in sphere {first.Value.Sphere} ({first.Value.Unfound:N0} unfound).");
            }
            _onlySlot.Text = $"Only {slot}'s rows";
            _note.Text = (room.Data?.Tables?.Count ?? 0) == 0
                ? (room.Busy ? "Reading the host's room…" : room.Problem == null ? "Atlas couldn't read this page's layout. Open it in your browser." : "")
                : "As the host's page has it. Atlas only reads the room; it never asks spheretracker.de to refresh it.";
            if (ReferenceEquals(room.Data, _data) && _dataSlot == slot && _table.Rows.Count > 0) return;
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

        /// <summary>The picked table of the room's page: its columns as the page names them, this slot's rows lit, the slot's own column left out while only its rows show.</summary>
        private void RenderTable()
        {
            var tables = _data?.Tables ?? new List<PageTable>();
            if (tables.Count == 0 || _tablePicker.ItemCount == 0)
            {
                _onlySlot.Visible = false;
                _table.TotalCount = null;
                _table.SetColumns(Array.Empty<AtlasTable.Column>());
                _table.SetRows(Array.Empty<AtlasTable.Row>());
                return;
            }
            var table = tables[Math.Clamp(_tablePicker.GetItemId(Math.Max(0, _tablePicker.Selected)), 0, tables.Count - 1)];
            int slotColumn = SphereTable.ColumnOf(table, "Finder");
            if (slotColumn < 0) slotColumn = SphereTable.ColumnOf(table, "Slot");
            var pattern = _dataSlot == null ? null : SphereTable.SlotPattern(_dataSlot);
            // "Only this slot's rows" applies to tables that list slots (the open locations); the summary has none.
            bool slotTable = slotColumn >= 0 || (pattern != null && table.Rows.Any(r => SphereTable.Names(r, pattern)));
            _onlySlot.Visible = slotTable;
            bool onlySlot = slotTable && pattern != null && _onlySlot.ButtonPressed;
            var shownColumns = Enumerable.Range(0, table.Columns.Count).Where(i => !(onlySlot && i == slotColumn)).ToList();
            var columns = new List<AtlasTable.Column>();
            var ids = new HashSet<string>();
            foreach (int i in shownColumns)
            {
                string name = string.IsNullOrWhiteSpace(table.Columns[i]) ? $"Column {i + 1}" : table.Columns[i];
                string id = name;
                for (int n = 2; !ids.Add(id); n++) id = $"{name} ({n})";
                columns.Add(new AtlasTable.Column { Id = id, Title = name, MinWidth = 60, Ratio = string.Equals(table.Columns[i], "Location", StringComparison.OrdinalIgnoreCase) ? 4 : 1 });
            }
            var rows = new List<AtlasTable.Row>();
            for (int r = 0; r < table.Rows.Count; r++)
            {
                var cells = table.Rows[r];
                bool mine = pattern != null && SphereTable.IsSlotRow(table, cells, _dataSlot, pattern);
                if (onlySlot && !mine) continue;
                var row = new AtlasTable.Row { Key = "r" + r, Cells = new AtlasTable.Cell[shownColumns.Count], Pinned = false };
                for (int k = 0; k < shownColumns.Count; k++)
                {
                    int i = shownColumns[k];
                    string text = i < cells.Count ? cells[i] ?? "" : "";
                    row.Cells[k] = new AtlasTable.Cell(text, mine && i == slotColumn ? ThemeColors.You : mine ? ThemeColors.Text : ThemeColors.TextMuted, text);
                }
                rows.Add(row);
            }
            _table.TotalCount = table.Rows.Count;
            _table.EmptyText = onlySlot && rows.Count == 0 && table.Rows.Count > 0 ? $"{_dataSlot} has no rows here. Switch off \"Only {_dataSlot}'s rows\" to see the whole table." : "Nothing here.";
            _table.SetColumns(columns);
            _table.SetRows(rows);
            MainTrackerWindow.SetFontSizeRecursive(_table, Settings.ContentFontSize);
        }

        // =====================================================================
        // Settings: the host's room for each multiworld
        // =====================================================================

        private void RenderSettings()
        {
            var profiles = Profiles();
            string signature = string.Join("|", profiles.Select(p => $"{p.Id}={p.Name}={p.SphereTrackerUrl}={_spheres.HiddenBecause(p)}"));
            if (signature == _settingsSignature) return;
            _settingsSignature = signature;
            foreach (Node child in _settingsBox.GetChildren()) child.QueueFree();

            _settingsBox.AddChild(Kit.Heading("Sphere Tracker settings", 1.3f));
            _settingsBox.AddChild(Kit.Text("Hosts create a spheretracker.de room for their multiworld and share its link with the players; it lists every slot's open locations with their sphere. " +
                                       "Atlas uses only that, and takes a room link only when the room exists (rooms need a login to create; Atlas never creates one), " +
                                       "isn't another multiworld's (when Cheese Tracker tells Atlas which tracker this multiworld has), and was created by the host: " +
                                       "the room names its creator, and Atlas takes it at once when that's the multiworld's organizer on Cheese Tracker, otherwise only if you confirm the creator is the host. " +
                                       "The room stays hidden while race mode applies, and Atlas reads it only while this tab shows it, at most every 10 minutes; " +
                                       "it never asks spheretracker.de to refresh a room.", ThemeColors.TextMuted));
            if (profiles.Count == 0) _settingsBox.AddChild(Kit.Subtle("No multiworlds yet: add one on the Multiworlds page."));
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
                    nameRow.AddChild(Kit.Button("Show", $"Show {profile.Slots[0]}'s rows", () => ShowView(SlotView(p.Id, p.Slots[0]))));
                _settingsBox.AddChild(nameRow);
                string hidden = _spheres.HiddenBecause(profile);
                if (hidden != null) _settingsBox.AddChild(Kit.Text("Hidden now: " + hidden, ThemeColors.Warning));
                var row = new HFlowContainer();
                row.AddThemeConstantOverride("h_separation", 6);
                row.AddThemeConstantOverride("v_separation", 4);
                bool linked = !string.IsNullOrWhiteSpace(profile.SphereTrackerUrl);
                var label = new Label { Text = linked ? "Host's room: " + profile.SphereTrackerUrl : "Host's room: not linked", VerticalAlignment = VerticalAlignment.Center };
                label.AddThemeColorOverride("font_color", linked ? ThemeColors.Success : ThemeColors.TextSubtle);
                row.AddChild(label);
                row.AddChild(Kit.Button(linked ? "Change…" : "Link…", "Paste the spheretracker.de room link your host created and shared", () => AskLinkSite(p)));
                if (linked)
                {
                    row.AddChild(Kit.Button("Unlink", "Stop using this room", () => { _spheres.UnlinkSphereSite(p.Id); _settingsSignature = null; InvalidateSidebar(); }));
                    row.AddChild(Kit.Button("Open ↗", "Open the host's room in your browser", () => AP_Atlas.Core.ExternalLinks.OpenWeb(p.SphereTrackerUrl)));
                }
                _settingsBox.AddChild(row);
            }
            MainTrackerWindow.SetFontSizeRecursive(_settingsBox, Settings.ContentFontSize);
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
                if (IsInstanceValid(this)) Toast("Checking the room failed: " + ex.Message, ThemeColors.Error);
                return;
            }
            if (!IsInstanceValid(this)) return;
            if (check.Error != null)
            {
                Toast(check.Error, ThemeColors.Error);
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
                Toast(error, ThemeColors.Error);
                return;
            }
            Toast(check.ByOrganizer ? $"Linked: the room was created by {check.Creator}, who runs {profile.Name}'s Cheese Tracker." : $"Linked {profile.Name}'s sphere tracker.", ThemeColors.TextSubtle);
            _settingsSignature = null;
            InvalidateSidebar();
            WatchShown();
            Refresh();
        }
    }
}
