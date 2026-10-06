#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.CheeseTracker;
using Godot;
using Color = Godot.Color;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The Cheese Tracker tab. The explorer lists "My slots" (yours in every linked multiworld), each multiworld, and the
    /// settings. A tracker shows the way Cheese Tracker's own page does: sortable columns, status/availability/owner/game
    /// filters and a search, "mine first", and a status summary, with the selected slot's notes, hints and actions below.
    /// </summary>
    public partial class CheeseTrackerTab : ExplorerTabBase
    {
        public const string MineView = "*mine";
        public const string SettingsView = "*settings";

        private readonly CheeseTrackerService _cheese;

        private readonly CheeseFilter _filter = new CheeseFilter();
        private List<CheeseRow> _rows = new List<CheeseRow>();
        private string _columnsSignature, _barsSignature, _headerSignature, _detailsSignature;

        // Pages
        private VBoxContainer _trackerPage;
        private CheeseSettingsPage _settingsPage;

        // Tracker page
        private Label _title, _subtitle, _problemLabel;
        private HBoxContainer _headerButtons, _problemRow;
        private Button _accountButton, _tryNow;
        private SafeRichText _summaryText;
        private HBoxContainer _checksBar, _slotsBar;
        private MenuButton _statusMenu, _availabilityMenu, _ownerMenu, _gameMenu;
        private Button _mineFirst, _percent, _clearFilters;
        private AtlasTable _table;
        private VBoxContainer _details;
        private bool _sentHints, _includeFoundHints;

        private sealed class Column
        {
            public string Id, Title;
            public int MinWidth, Ratio;
            public HorizontalAlignment Align = HorizontalAlignment.Left;
        }

        // Cheese Tracker's columns, in its order. Status combines progression and completion (and, for your connected slots,
        // Atlas's suggestion) the way the slot cards' badges do, so the table fits beside the explorer and Properties.
        private static readonly Column[] AllColumns =
        {
            new Column { Id = "multiworld", Title = "Multiworld", MinWidth = 96, Ratio = 2 },
            new Column { Id = "position", Title = "#", MinWidth = 34, Align = HorizontalAlignment.Right },
            new Column { Id = "name", Title = "Name", MinWidth = 96, Ratio = 3 },
            new Column { Id = "ping", Title = "Ping", MinWidth = 70 },
            new Column { Id = "availability", Title = "Availability", MinWidth = 80 },
            new Column { Id = "owner", Title = "Owner", MinWidth = 80, Ratio = 2 },
            new Column { Id = "game", Title = "Game", MinWidth = 96, Ratio = 3 },
            new Column { Id = "status", Title = "Status", MinWidth = 150, Ratio = 2 },
            new Column { Id = "activity", Title = "Activity", MinWidth = 66, Align = HorizontalAlignment.Right },
            new Column { Id = "checks", Title = "Checks", MinWidth = 80, Align = HorizontalAlignment.Right },
            new Column { Id = "hints", Title = "Hints", MinWidth = 48, Align = HorizontalAlignment.Right }
        };

        private List<Column> _columns = new List<Column>();

        public CheeseTrackerTab(AppSettings settings, CheeseTrackerService cheese, Func<IReadOnlyList<MultiworldProfile>> profiles, Action<string, Color> toast)
            : base(settings, profiles, toast, "Cheese Tracker", settings.CheeseTabView)
        {
            _cheese = cheese;
        }

        protected override string DefaultView => MineView;

        protected override void SaveView(string view)
        {
            Settings.CheeseTabView = view;
            DataManager.SaveSettings(Settings);
        }

        protected override void OnViewChanged() => _table?.Select(null);

        protected override void BuildPages()
        {
            BuildTrackerPage();
            _settingsPage = new CheeseSettingsPage(Settings, _cheese, Profiles, Toast, ShowView);
            AddPage(_settingsPage);
        }

        protected override TreeSubscriptions Subscribe(TreeSubscriptions subscriptions) => subscriptions
            .On(() => _cheese.Changed += QueueRefresh, () => _cheese.Changed -= QueueRefresh)
            .On(() => _cheese.LinkChanged += OnLinkChanged, () => _cheese.LinkChanged -= OnLinkChanged);

        private void OnLinkChanged(string profileId) => QueueRefresh();

        private List<MultiworldProfile> ProfilesShown()
        {
            if (View == SettingsView) return new List<MultiworldProfile>();
            if (View == MineView) return _cheese.LinkedProfiles();
            var p = Profiles().FirstOrDefault(x => x.Id == View);
            return p == null ? new List<MultiworldProfile>() : new List<MultiworldProfile> { p };
        }

        protected override void WatchShown()
        {
            if (!IsVisibleInTree()) return;
            foreach (var p in ProfilesShown()) _cheese.Watch(p.Id);
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
            _accountButton = Kit.Button("", "Your Cheese Tracker account and API key", () => ShowView(SettingsView));
            header.AddChild(_accountButton);

            _subtitle = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _subtitle.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            _trackerPage.AddChild(_subtitle);

            _problemRow = new HBoxContainer { Visible = false };
            _problemRow.AddThemeConstantOverride("separation", 6);
            _problemLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _problemLabel.AddThemeColorOverride("font_color", ThemeColors.Warning);
            _problemRow.AddChild(_problemLabel);
            _tryNow = Kit.Button("Try now", "Ask Cheese Tracker again now", () =>
            {
                foreach (var p in ProfilesShown()) CheeseDialogs.Run(this, _cheese.RefreshAsync(p.Id, tryNow: true), Toast, null, QueueRefresh);
            });
            _problemRow.AddChild(_tryNow);
            _trackerPage.AddChild(_problemRow);

            // Summary: counts, then remaining checks by progression status and slots by status (like Cheese Tracker's).
            _summaryText = Rich();
            _trackerPage.AddChild(_summaryText);
            _checksBar = Bar("Checks left, by status");
            _slotsBar = Bar("Slots, by status");

            // The table: Cheese Tracker's columns; the search, the count and the menus join the tab's own toolbar with the filters.
            _table = new AtlasTable("cheese", Settings, text => text) { Toast = Toast };
            _table.SearchBox.PlaceholderText = "Find a slot, owner, game or note…";
            _table.SearchBox.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            _table.SearchBox.CustomMinimumSize = new Vector2(240, 0);
            _table.SearchBox.TextChanged += _ => UpdateClearFilters();
            _table.SelectionChanged += OnRowSelected;
            _table.RowMenu = row => RowActions(row.Tag as CheeseRow);
            _table.EmptyText = "No slots to show yet.";
            _table.NoMatchText = "No slots match the filters.";

            var toolbar = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            toolbar.AddThemeConstantOverride("h_separation", 6);
            toolbar.AddThemeConstantOverride("v_separation", 4);
            _trackerPage.AddChild(toolbar);
            toolbar.AddChild(_table.Take(_table.SearchBox));
            _statusMenu = FilterMenu("Status", "Show or hide progression and completion statuses (progression filters don't hide completed slots)");
            BuildStatusMenu();
            toolbar.AddChild(_statusMenu);
            _availabilityMenu = FilterMenu("Availability", "Show or hide availability states");
            BuildAvailabilityMenu();
            toolbar.AddChild(_availabilityMenu);
            _ownerMenu = FilterMenu("Owner", "Everyone, unclaimed slots, yours, or one player's");
            _ownerMenu.GetPopup().AboutToPopup += BuildOwnerMenu;
            _ownerMenu.GetPopup().IdPressed += OnOwnerPicked;
            toolbar.AddChild(_ownerMenu);
            _gameMenu = FilterMenu("Game", "Every game, or one");
            _gameMenu.GetPopup().AboutToPopup += BuildGameMenu;
            _gameMenu.GetPopup().IdPressed += OnGamePicked;
            toolbar.AddChild(_gameMenu);
            _mineFirst = new Button { Text = "Mine first", ToggleMode = true, ButtonPressed = Settings.CheeseMineFirst, FocusMode = FocusModeEnum.None, TooltipText = "Your slots on top, before any column sort" };
            _mineFirst.Toggled += on =>
            {
                Settings.CheeseMineFirst = on;
                DataManager.SaveSettings(Settings);
                RenderTable();
            };
            toolbar.AddChild(_mineFirst);
            _percent = new Button { Text = "Checks %", ToggleMode = true, ButtonPressed = Settings.CheeseChecksAsPercent, FocusMode = FocusModeEnum.None, TooltipText = "Show checks as a percentage" };
            _percent.Toggled += on =>
            {
                Settings.CheeseChecksAsPercent = on;
                DataManager.SaveSettings(Settings);
                RenderTable();
            };
            toolbar.AddChild(_percent);
            _clearFilters = Kit.Button("Clear filters", null, () =>
            {
                _filter.Clear();
                _table.SearchBox.Text = "";
                BuildStatusMenu();
                BuildAvailabilityMenu();
                RenderTable();
            });
            _clearFilters.Visible = false;
            toolbar.AddChild(_clearFilters);
            toolbar.AddChild(_table.Take(_table.CountLabel));
            toolbar.AddChild(_table.Take(_table.ColumnsMenu));
            toolbar.AddChild(_table.Take(_table.ExportMenu));
            _table.Toolbar.Visible = false;

            var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffsets = new[] { Settings.CheeseTableSplitOffset } };
            split.Dragged += offset =>
            {
                Settings.CheeseTableSplitOffset = (int)offset;
                DataManager.SaveSettingsSoon(Settings);
            };
            _trackerPage.AddChild(split);
            split.AddChild(_table);

            var detailScroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.Fill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 120) };
            split.AddChild(detailScroll);
            _details = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _details.AddThemeConstantOverride("separation", 4);
            detailScroll.AddChild(_details);
        }

        private HBoxContainer Bar(string label)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var name = new Label { Text = label, CustomMinimumSize = new Vector2(150, 0) };
            name.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            name.SetMeta("font_size_ratio", 0.85);
            row.AddChild(name);
            var bar = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 10) };
            bar.AddThemeConstantOverride("separation", 1);
            row.AddChild(bar);
            _trackerPage.AddChild(row);
            return bar;
        }

        private static MenuButton FilterMenu(string text, string tooltip)
        {
            var menu = new MenuButton { Text = text + " ▾", Flat = false, FocusMode = FocusModeEnum.None, TooltipText = tooltip };
            menu.SetMeta("base_text", text);
            return menu;
        }

        private void BuildStatusMenu()
        {
            var popup = _statusMenu.GetPopup();
            popup.Clear();
            popup.HideOnCheckableItemSelection = false;
            int id = 0;
            foreach (var status in CtStatus.ProgressionIds)
            {
                popup.AddCheckItem(CtStatus.Label(status), id);
                popup.SetItemChecked(popup.GetItemIndex(id), !_filter.HiddenProgression.Contains(status));
                id++;
            }
            popup.AddSeparator();
            id = 100;
            foreach (var status in CtStatus.CompletionIds)
            {
                popup.AddCheckItem(CtStatus.Label(status), id);
                popup.SetItemChecked(popup.GetItemIndex(id), !_filter.HiddenCompletion.Contains(status));
                id++;
            }
            if (!_statusMenuHooked)
            {
                _statusMenuHooked = true;
                popup.IdPressed += pressed =>
                {
                    int i = (int)pressed;
                    if (i < 100) Toggle(_filter.HiddenProgression, CtStatus.ProgressionIds[i]);
                    else Toggle(_filter.HiddenCompletion, CtStatus.CompletionIds[i - 100]);
                    popup.SetItemChecked(popup.GetItemIndex(i), !popup.IsItemChecked(popup.GetItemIndex(i)));
                    RenderTable();
                };
            }
        }

        private bool _statusMenuHooked, _availabilityMenuHooked;
        private static readonly string[] AvailabilityIds = { "unknown", "open", "claimed", "public" };

        private void BuildAvailabilityMenu()
        {
            var popup = _availabilityMenu.GetPopup();
            popup.Clear();
            popup.HideOnCheckableItemSelection = false;
            for (int i = 0; i < AvailabilityIds.Length; i++)
            {
                popup.AddCheckItem(CtStatus.Label(AvailabilityIds[i]), i);
                popup.SetItemChecked(popup.GetItemIndex(i), !_filter.HiddenAvailability.Contains(AvailabilityIds[i]));
            }
            if (!_availabilityMenuHooked)
            {
                _availabilityMenuHooked = true;
                popup.IdPressed += pressed =>
                {
                    int i = (int)pressed;
                    Toggle(_filter.HiddenAvailability, AvailabilityIds[i]);
                    popup.SetItemChecked(popup.GetItemIndex(i), !popup.IsItemChecked(popup.GetItemIndex(i)));
                    RenderTable();
                };
            }
        }

        private static void Toggle(HashSet<string> set, string id)
        {
            if (!set.Remove(id)) set.Add(id);
        }

        private List<string> _ownerChoices = new List<string>();

        private void BuildOwnerMenu()
        {
            var popup = _ownerMenu.GetPopup();
            popup.Clear();
            _ownerChoices = new List<string> { null, "" };
            popup.AddRadioCheckItem("Everyone", 0);
            popup.AddRadioCheckItem("Unclaimed", 1);
            if (_cheese.AccountName != null)
            {
                _ownerChoices.Add(CheeseFilter.You);
                popup.AddRadioCheckItem($"You ({_cheese.AccountName})", 2);
            }
            var owners = _rows.Select(r => r.Game.OwnerName).Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (owners.Count > 0) popup.AddSeparator();
            foreach (var owner in owners)
            {
                popup.AddRadioCheckItem(owner, _ownerChoices.Count);
                _ownerChoices.Add(owner);
            }
            for (int i = 0; i < _ownerChoices.Count; i++)
                popup.SetItemChecked(popup.GetItemIndex(i), string.Equals(_ownerChoices[i], _filter.Owner, StringComparison.OrdinalIgnoreCase) && (_ownerChoices[i] == null) == (_filter.Owner == null));
        }

        private void OnOwnerPicked(long id)
        {
            if (id < 0 || id >= _ownerChoices.Count) return;
            _filter.Owner = _ownerChoices[(int)id];
            RenderTable();
        }

        private List<string> _gameChoices = new List<string>();

        private void BuildGameMenu()
        {
            var popup = _gameMenu.GetPopup();
            popup.Clear();
            _gameChoices = new List<string> { null };
            popup.AddRadioCheckItem("Every game", 0);
            var games = _rows.Select(r => r.Game.Game).Where(g => !string.IsNullOrEmpty(g)).Distinct().OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
            if (games.Count > 0) popup.AddSeparator();
            foreach (var game in games)
            {
                popup.AddRadioCheckItem(game, _gameChoices.Count);
                _gameChoices.Add(game);
            }
            for (int i = 0; i < _gameChoices.Count; i++)
                popup.SetItemChecked(popup.GetItemIndex(i), _gameChoices[i] == _filter.Game);
        }

        private void OnGamePicked(long id)
        {
            if (id < 0 || id >= _gameChoices.Count) return;
            _filter.Game = _gameChoices[(int)id];
            RenderTable();
        }

        // =====================================================================
        // Explorer list
        // =====================================================================

        private void RenderSidebar()
        {
            var profiles = Profiles();
            string signature = View + "|" + string.Join("|", profiles.Select(p => p.Id + "=" + p.Name + "=" + (string.IsNullOrWhiteSpace(p.CheeseTrackerUrl) ? "" : "L")));
            RenderSidebar(signature, () =>
            {
                SidebarContent.AddChild(ExplorerButton("My slots", "Your slots in every linked multiworld", MineView, false));
                SidebarContent.AddChild(new HSeparator());
                foreach (var p in profiles)
                {
                    bool linked = !string.IsNullOrWhiteSpace(p.CheeseTrackerUrl);
                    SidebarContent.AddChild(ExplorerButton(linked ? p.Name : p.Name + "  (not linked)", linked ? "Show this multiworld's tracker" : "Link this multiworld to Cheese Tracker", p.Id, !linked));
                }
                if (profiles.Count == 0) SidebarContent.AddChild(SidebarNote("No multiworlds yet (the Multiworlds page)."));
                SidebarContent.AddChild(new HSeparator());
                SidebarContent.AddChild(ExplorerButton("Settings", "Your API key, links and automatic updates", SettingsView, false));
            });
        }

        // =====================================================================
        // Pages
        // =====================================================================

        /// <summary>Redraws the tab from the service's data (keeps the selection, scroll and filters).</summary>
        protected override void RefreshNow()
        {
            if (_trackerPage == null) return;
            using var __perf = PerfMonitor.Measure("Cheese Tracker tab: refresh");
            RenderSidebar();
            _accountButton.Text = _cheese.KeyRejected ? "Key not accepted" : _cheese.HasKey ? "● " + (_cheese.AccountName ?? "Signed in") : "Add API key";
            _accountButton.AddThemeColorOverride("font_color", _cheese.KeyRejected ? ThemeColors.Warning : _cheese.HasKey ? ThemeColors.Success : ThemeColors.TextSubtle);

            if (View == SettingsView)
            {
                ShowPage(_settingsPage);
                _settingsPage.Refresh();
                return;
            }
            var profile = View == MineView ? null : Profiles().FirstOrDefault(p => p.Id == View);
            if (View != MineView && profile == null)
            {
                ShowView(MineView);
                return;
            }
            if (profile != null && string.IsNullOrWhiteSpace(profile.CheeseTrackerUrl))
            {
                ShowNotLinked(profile);
                return;
            }
            var linked = _cheese.LinkedProfiles();
            if (View == MineView && linked.Count == 0)
            {
                ShowMessage("No multiworld is linked to Cheese Tracker yet.",
                    "Link one from the explorer on the left (or the multiworld's field on the Multiworlds page) to see everyone's status here.",
                    Profiles().Take(6).Select(p => ($"Link {p.Name}…", (Action)(() => CheeseDialogs.Link(this, _cheese, p, Toast, Refresh)))).ToList());
                return;
            }
            ShowPage(_trackerPage);
            RenderTrackerPage(profile, linked);
        }

        private void ShowNotLinked(MultiworldProfile profile)
        {
            var actions = new List<(string, Action)> { ($"Link {profile.Name}…", () => CheeseDialogs.Link(this, _cheese, profile, Toast, Refresh)) };
            if (_cheese.HasKey) actions.Add(("Find on my dashboard", () => CheeseDialogs.FindOnDashboard(this, _cheese, profile, Toast, Refresh)));
            else actions.Add(("Settings…", () => ShowView(SettingsView)));
            ShowMessage($"{profile.Name} isn't linked to Cheese Tracker.",
                "Paste its Cheese Tracker link, or the archipelago.gg room link (Cheese Tracker finds or starts the room's tracker). " +
                (_cheese.HasKey ? "Or let Atlas look for it among the trackers on your dashboard." : "With your API key added (Settings), Atlas can also look for it on your dashboard."),
                actions);
        }

        private void RenderTrackerPage(MultiworldProfile profile, List<MultiworldProfile> linked)
        {
            var shown = profile != null ? new List<MultiworldProfile> { profile } : linked;
            var rooms = shown.Select(p => (Profile: p, Room: _cheese.RoomView(p.Id))).Where(x => x.Room != null).ToList();

            // Header (its buttons are rebuilt only when what they act on changes).
            string headerSignature = View + "|" + profile?.CheeseTrackerUrl + "|" + string.Join(",", rooms.Select(r => r.Room.Busy + r.Room.Link));
            bool rebuildButtons = headerSignature != _headerSignature;
            _headerSignature = headerSignature;
            if (rebuildButtons) foreach (Node child in _headerButtons.GetChildren()) child.QueueFree();
            if (profile != null)
            {
                var room = rooms.FirstOrDefault().Room;
                var t = room?.Tracker;
                _title.Text = string.IsNullOrWhiteSpace(t?.Title) ? profile.Name : t.Title;
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(t?.OwnerName)) parts.Add("organized by " + t.OwnerName);
                parts.Add(room?.Busy == true ? "reading…" : "read " + CtTime.Ago(room?.FetchedUtc));
                if (t != null) parts.Add($"{t.Games.Count} slots");
                if (room?.OtherSite == true) parts.Add("on another Cheese Tracker site, so read only");
                if (!string.IsNullOrEmpty(t?.GlobalPingPolicy)) parts.Add("ping policy: " + CtStatus.Label(t.GlobalPingPolicy));
                _subtitle.Text = profile.Name + " · " + string.Join(" · ", parts);
                if (rebuildButtons)
                {
                    _headerButtons.AddChild(Kit.Button("Refresh", "Read the tracker again now", () => CheeseDialogs.Run(this, _cheese.RefreshAsync(profile.Id), Toast, null, QueueRefresh), room?.Busy != true));
                    if (room != null) _headerButtons.AddChild(Kit.Button("Open ↗", "Open this tracker on Cheese Tracker", () => AP_Atlas.Core.ExternalLinks.OpenWeb(room.Link)));
                    _headerButtons.AddChild(Kit.Button("Change link…", "Link this multiworld to a different tracker", () => CheeseDialogs.Link(this, _cheese, profile, Toast, Refresh)));
                }
            }
            else
            {
                _title.Text = "My slots";
                int busy = rooms.Count(r => r.Room.Busy);
                var oldest = rooms.Where(r => r.Room.FetchedUtc != null).Select(r => r.Room.FetchedUtc.Value).DefaultIfEmpty().Min();
                _subtitle.Text = $"Your slots in {rooms.Count} linked multiworld{(rooms.Count == 1 ? "" : "s")} · " +
                                 (busy > 0 ? "reading…" : rooms.Any(r => r.Room.FetchedUtc == null) ? "not all read yet" : "oldest read " + CtTime.Ago(oldest));
                if (rebuildButtons)
                    _headerButtons.AddChild(Kit.Button("Refresh", "Read every linked tracker again now", () =>
                    {
                        foreach (var (p, _) in rooms) CheeseDialogs.Run(this, _cheese.RefreshAsync(p.Id), Toast, null, QueueRefresh);
                    }, busy == 0));
            }
            if (rebuildButtons) MainTrackerWindow.SetFontSizeRecursive(_headerButtons, Settings.ContentFontSize);

            var problems = rooms.Where(r => r.Room.Problem != null).Select(r => (rooms.Count > 1 ? r.Profile.Name + ": " : "") + r.Room.Problem).ToList();
            _problemRow.Visible = problems.Count > 0;
            _problemLabel.Text = string.Join("\n", problems);

            // Rows: the whole tracker, or (My slots) your rows of every linked tracker.
            _rows = new List<CheeseRow>();
            foreach (var (p, room) in rooms)
            {
                if (room.Tracker == null) continue;
                var rows = _cheese.RowsOf(p.Id);
                _rows.AddRange(profile != null ? rows : rows.Where(r => r.Mine));
            }
            ConfigureColumns(includeMultiworld: profile == null);
            RenderSummary();
            RenderTable();
        }

        // =====================================================================
        // Summary
        // =====================================================================

        private void RenderSummary()
        {
            // Like Cheese Tracker's numbers: forfeited slots don't count.
            var stat = _rows.Where(r => r.Game.Completion != "released").ToList();
            long done = stat.Sum(r => (long)r.Game.ChecksDone), total = stat.Sum(r => (long)r.Game.ChecksTotal);
            int players = stat.Select(r => r.Game.OwnerName).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int games = stat.Select(r => r.Game.Game).Distinct().Count();
            var unified = stat.GroupBy(r => Unified(r.Game)).ToDictionary(g => g.Key, g => g.Count());
            var order = new[] { "bk", "soft_bk", "incomplete", "all_checks", "goal", "done" };
            string counts = string.Join(Colored(" · ", ThemeColors.TextSubtle), order.Where(unified.ContainsKey).Select(id => Colored($"{unified[id]} {UnifiedLabel(id)}", UnifiedColor(id))));
            _summaryText.Markup = Colored($"{Plural(stat.Count, "slot")} · {Plural(players, "player")} · {Plural(games, "game")} · {done}/{total} checks" + (total > 0 ? $" ({100.0 * done / total:0}%)" : ""), ThemeColors.TextMuted) +
                                (counts.Length > 0 ? "   " + counts : "");

            string barsSignature = string.Join(",", unified.OrderBy(kv => kv.Key).Select(kv => kv.Key + kv.Value)) + "|" +
                string.Join(",", stat.GroupBy(r => r.Game.Progression).OrderBy(g => g.Key).Select(g => g.Key + g.Sum(r => Math.Max(0, r.Game.ChecksTotal - r.Game.ChecksDone)))) + "|" + done;
            if (barsSignature == _barsSignature) return;
            _barsSignature = barsSignature;
            var progressionOrder = new[] { "unknown", "bk", "soft_bk", "unblocked", "go" };
            var checks = progressionOrder.Select(id => (CtStatus.Label(id) + " (remaining)", (double)stat.Where(r => r.Game.Progression == id).Sum(r => Math.Max(0, r.Game.ChecksTotal - r.Game.ChecksDone)), AP_Atlas.UI.CheeseColors.Of(id) == Colors.WhiteSmoke ? CheeseColors.Incomplete : AP_Atlas.UI.CheeseColors.Of(id)))
                .Append(("Checked", done, ThemeColors.TextMuted));
            FillBar(_checksBar, checks);
            FillBar(_slotsBar, order.Select(id => (UnifiedLabel(id), (double)unified.GetValueOrDefault(id), UnifiedColor(id))));
        }

        private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

        /// <summary>Cheese Tracker's combined status: BK or Soft BK while incomplete, otherwise the completion status.</summary>
        private static string Unified(CtGame g) => !g.IsComplete && g.Progression is "bk" or "soft_bk" ? g.Progression : g.Completion;

        private static string UnifiedLabel(string id) => id == "incomplete" ? "in progress" : CtStatus.Label(id);

        private static Color UnifiedColor(string id) => id == "incomplete" ? CheeseColors.Incomplete : AP_Atlas.UI.CheeseColors.Of(id);

        private static void FillBar(HBoxContainer bar, IEnumerable<(string Label, double Value, Color Color)> parts)
        {
            foreach (Node child in bar.GetChildren()) child.QueueFree();
            var list = parts.Where(p => p.Value > 0).ToList();
            double sum = list.Sum(p => p.Value);
            foreach (var (label, value, color) in list)
            {
                bar.AddChild(new ColorRect
                {
                    Color = color,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    SizeFlagsStretchRatio = (float)value,
                    CustomMinimumSize = new Vector2(2, 10),
                    TooltipText = $"{label}: {value:0} ({100.0 * value / sum:0}%)"
                });
            }
        }

        private static string Colored(string text, Color c) => Bbcode.Colored(text, "#" + c.ToHtml(false));

        // =====================================================================
        // Table
        // =====================================================================

        private void ConfigureColumns(bool includeMultiworld)
        {
            // "My slots" are yours, so their ping and availability say little; the multiworld matters instead.
            var columns = AllColumns.Where(c => includeMultiworld ? c.Id is not ("ping" or "availability") : c.Id != "multiworld").ToList();
            string signature = string.Join(",", columns.Select(c => c.Id));
            if (signature == _columnsSignature) return;
            _columnsSignature = signature;
            _columns = columns;
            _table.SetColumns(columns.Select(c => new AtlasTable.Column
            {
                Id = c.Id,
                Title = c.Title,
                MinWidth = c.MinWidth,
                Ratio = c.Ratio,
                Align = c.Align,
                DescendingFirst = CheeseTable.DefaultDescending(c.Id)
            }).ToList(), defaultSort: "name");
        }

        private void RenderTable()
        {
            if (_table == null || _columns.Count == 0) return;
            using var __perf = PerfMonitor.Measure("Cheese Tracker tab: table");
            var now = DateTime.UtcNow; // wall clock: how long ago the site's times were, for showing
            UpdateClearFilters();
            SetMenuActive(_statusMenu, _filter.HiddenProgression.Count + _filter.HiddenCompletion.Count > 0);
            SetMenuActive(_availabilityMenu, _filter.HiddenAvailability.Count > 0);
            SetMenuActive(_ownerMenu, _filter.Owner != null);
            SetMenuActive(_gameMenu, _filter.Game != null);

            // Ties in any sort keep this order: by name, then by multiworld (stable and predictable, as Cheese Tracker's page).
            var passing = TableSort.Order(_rows.Where(r => CheeseTable.Passes(r, _filter, ClaimedByYou(r))), r => CheeseTable.SortKey(r, "name", now), false);
            var rows = new List<AtlasTable.Row>(passing.Count);
            foreach (var r in passing)
            {
                var row = new AtlasTable.Row { Key = r.Key, Tag = r, Pinned = Settings.CheeseMineFirst && r.Mine, SearchText = r.Game.Notes, Cells = new AtlasTable.Cell[_columns.Count] };
                for (int i = 0; i < _columns.Count; i++)
                {
                    var (text, color, tip) = Cell(_columns[i].Id, r, now);
                    row.Cells[i] = new AtlasTable.Cell(text ?? "", color, tip, CheeseTable.SortKey(r, _columns[i].Id, now));
                    if (_columns[i].Id == "name" && r.Mine) row.Cells[i].Background = new Color(1, 0, 1, 0.08f);
                }
                rows.Add(row);
            }
            _table.TotalCount = _rows.Count;
            _table.SetRows(rows);
            RenderDetails();
        }

        private void UpdateClearFilters()
        {
            if (_clearFilters != null) _clearFilters.Visible = _filter.IsActive || _table.SearchBox.Text.Length > 0;
        }

        private static void SetMenuActive(MenuButton menu, bool active)
        {
            string text = menu.GetMeta("base_text").AsString();
            menu.Text = active ? text + " (filtered) ▾" : text + " ▾";
            if (active) menu.AddThemeColorOverride("font_color", ThemeColors.Pending);
            else menu.RemoveThemeColorOverride("font_color");
        }

        private bool ClaimedByYou(CheeseRow r) => _cheese.OwnershipOf(r.Game) is CheeseOwnership.You or CheeseOwnership.YouByName;

        private (string Text, Color Color, string Tip) Cell(string column, CheeseRow row, DateTime now)
        {
            var g = row.Game;
            switch (column)
            {
                case "multiworld":
                    return (row.ProfileName, ThemeColors.TextMuted, null);
                case "position":
                    return (g.Position.ToString(CultureInfo.InvariantCulture), ThemeColors.TextSubtle, null);
                case "name":
                    return (g.Name, row.Mine ? ThemeColors.You : ThemeColors.Text,
                        row.SlotName != null ? $"Your slot {row.SlotName} in Atlas (click to show it in Properties)" : ClaimedByYou(row) ? "Claimed by you" : null);
                case "ping":
                    if (string.IsNullOrEmpty(g.OwnerName)) return ("", ThemeColors.TextSubtle, null);
                    string ping = CheeseTable.EffectivePing(row);
                    return (CtStatus.Label(ping), PingColor(ping), !string.IsNullOrEmpty(row.Tracker?.GlobalPingPolicy) ? "Set for everyone by the organizer" : null);
                case "availability":
                    return (CtStatus.Label(g.Availability), g.Availability switch { "open" => ThemeColors.Success, "public" => ThemeColors.Info, "claimed" => ThemeColors.TextMuted, _ => ThemeColors.TextSubtle }, null);
                case "owner":
                    if (string.IsNullOrEmpty(g.OwnerName)) return ("—", ThemeColors.TextSubtle, "Unclaimed");
                    return (g.OwnerName + (g.OwnerAway ? " (away)" : ""), g.OwnerAway ? ThemeColors.Warning : ClaimedByYou(row) ? ThemeColors.You : ThemeColors.TextMuted,
                        g.OwnerAway ? "The owner is away" : g.ClaimedByUserId == null ? "Claimed without signing in" : null);
                case "game":
                    return (g.Game, ThemeColors.TextMuted, null);
                case "status":
                    return StatusCell(row);
                case "activity":
                    {
                        int level = CheeseTable.ActivityLevel(g, row.Tracker, now);
                        var latest = g.LastCheckedUtc != null && (g.LastActivityUtc == null || g.LastCheckedUtc > g.LastActivityUtc) ? g.LastCheckedUtc : g.LastActivityUtc;
                        string tip = latest == null ? "No checks yet" : latest.Value.ToLocalTime().ToString("g") + (latest == g.LastCheckedUtc && g.LastCheckedUtc != g.LastActivityUtc ? " (still BK)" : "");
                        return (CheeseTable.ActivityText(g, now), level == 0 ? ThemeColors.Success : level == 1 ? CheeseColors.Caution : ThemeColors.Error, tip);
                    }
                case "checks":
                    {
                        bool complete = g.ChecksTotal > 0 && g.ChecksDone >= g.ChecksTotal;
                        string text = Settings.CheeseChecksAsPercent
                            ? (g.ChecksTotal > 0 ? $"{100.0 * g.ChecksDone / g.ChecksTotal:0}%" : "—")
                            : $"{g.ChecksDone}/{g.ChecksTotal}";
                        return (text, complete ? ThemeColors.Success : ThemeColors.Text, $"{g.ChecksDone} of {g.ChecksTotal} checks");
                    }
                case "hints":
                    {
                        bool notes = !string.IsNullOrWhiteSpace(g.Notes);
                        int n = row.UnfoundHints;
                        var color = n == 0 ? (notes ? ThemeColors.Info : ThemeColors.TextSubtle) : n <= 5 ? ThemeColors.Info : n <= 10 ? CheeseColors.Caution : ThemeColors.Error;
                        bool hintsKnown = row.Tracker?.Hints != null && row.Tracker.Hints.Count > 0;
                        return (n + (notes ? "*" : ""), color, (hintsKnown ? $"{n} unfound hint{(n == 1 ? "" : "s")} for items in this world" : "Hints load with the next read of the tracker") + (notes ? "; this slot has notes" : ""));
                    }
            }
            return ("", ThemeColors.TextSubtle, null);
        }

        /// <summary>The slot's status (progression while in progress, else completion), with Atlas's ready suggestion for your slots.</summary>
        private (string, Color, string) StatusCell(CheeseRow row)
        {
            var g = row.Game;
            string status = CtStatus.Headline(g);
            string text = CtStatus.Label(status);
            var color = AP_Atlas.UI.CheeseColors.Of(status);
            string tip = g.IsComplete ? $"{CtStatus.Label(g.Completion)} (progression: {CtStatus.Label(g.Progression)})" : $"{CtStatus.Label(g.Progression)}, {CtStatus.Label(g.Completion)}";
            if (row.SlotName == null) return (text, color, tip);
            var view = _cheese.SlotView(row.ProfileId, row.SlotName);
            var a = view?.Advice;
            if (view?.AutoOn == true && view.AutoPaused != null) return (text + " · paused", ThemeColors.Warning, tip + "\nAutomatic updates are paused: " + view.AutoPaused);
            if (a?.Status != null && a.Ready)
                return ($"{text} → {CtStatus.Label(a.Status)}?", ThemeColors.Pending, tip + $"\nAtlas suggests {CtStatus.Label(a.Status)}: {a.Reason}" + (view.AutoOn ? " (it will set it automatically)" : ""));
            if (view?.AutoOn == true) return (text + " · auto", color, tip + "\nAtlas keeps this updated automatically" + (a?.InSync == true ? " (its logic agrees)" : ""));
            if (a?.InSync == true) return (text, color, tip + "\nAtlas's logic agrees: " + a.Reason);
            return (text, color, tip);
        }

        private static Color PingColor(string ping) => ping switch
        {
            "liberally" => ThemeColors.Success,
            "sparingly" or "hints" => CheeseColors.Caution,
            "see_notes" => ThemeColors.Info,
            _ => ThemeColors.Error
        };

        private CheeseRow SelectedRow() => _table.Selected?.Tag as CheeseRow;

        private void OnRowSelected(AtlasTable.Row picked)
        {
            RenderDetails();
            if (picked?.Tag is CheeseRow row && row.SlotName != null) Inspector.Inspect(InspectTarget.ForSlot(row.ProfileId, row.SlotName));
        }

        // =====================================================================
        // Actions (row menu and details)
        // =====================================================================

        private CheeseTarget TargetOf(CheeseRow row) => row.SlotName != null ? CheeseTarget.Slot(row.ProfileId, row.SlotName) : CheeseTarget.Row(row.ProfileId, row.Game.Id);

        private void Run(System.Threading.Tasks.Task<string> change, string success = null) => CheeseDialogs.Run(this, change, Toast, success, QueueRefresh);

        /// <summary>A row's right-click menu: what Cheese Tracker lets you change for it, then where it leads.</summary>
        private IEnumerable<(string Label, Action Action, bool Enabled)> RowActions(CheeseRow row)
        {
            if (row == null) yield break;
            string cannot = _cheese.CannotEdit(row.ProfileId, row.Game);
            var g = row.Game;
            var target = TargetOf(row);
            if (cannot != null)
            {
                yield return (cannot.Length > 80 ? cannot.Substring(0, 77) + "…" : cannot, () => { }, false);
                if (_cheese.CanClaim(row.ProfileId, g)) yield return ("Claim", () => Run(_cheese.ClaimAsync(target), $"Claimed {g.Name}"), true);
            }
            else
            {
                if (!g.IsComplete)
                    foreach (var status in CtStatus.ProgressionIds.Where(s => s != "unknown" && s != g.Progression))
                    {
                        string s = status;
                        yield return ("Set " + CtStatus.Label(s), () => Run(_cheese.SetProgressionAsync(target, s)), true);
                    }
                if (g.Progression is "bk" or "soft_bk" && !g.IsComplete) yield return ("Still BK", () => Run(_cheese.StillBkAsync(target), "Marked still BK"), true);
                yield return ("Notes…", () => CheeseDialogs.Notes(this, g.Name, g.Notes, text => Run(_cheese.SetNotesAsync(target, text), "Notes saved")), true);
                foreach (var ping in CtStatus.PingIds)
                {
                    string p = ping;
                    yield return ((p == g.Ping ? "✓ " : "") + "Ping: " + CtStatus.Label(p), () => Run(_cheese.SetPingAsync(target, p)), p != g.Ping);
                }
                if (g.Completion != "done") yield return ("Mark Done…", () => CheeseDialogs.ConfirmDone(this, g.Name, () => Run(_cheese.SetCompletionAsync(target, "done"))), true);
                if (g.Completion != "released") yield return ("Forfeit…", () => CheeseDialogs.ConfirmForfeit(this, g.Name, () => Run(_cheese.SetCompletionAsync(target, "released"))), true);
                if (g.Completion is "done" or "released") yield return ("Not done after all", () => Run(_cheese.SetCompletionAsync(target, "incomplete")), true);
                var owner = _cheese.OwnershipOf(g);
                if (owner is CheeseOwnership.Nobody or CheeseOwnership.YouByName) yield return ("Claim", () => Run(_cheese.ClaimAsync(target), $"Claimed {g.Name}"), _cheese.HasKey);
                else if (owner == CheeseOwnership.You) yield return ("Disclaim…", () => CheeseDialogs.ConfirmDisclaim(this, g.Name, () => Run(_cheese.DisclaimAsync(target), $"Released {g.Name}")), true);
            }
            if (row.SlotName != null) yield return ("Show in Properties", () => Inspector.Inspect(InspectTarget.ForSlot(row.ProfileId, row.SlotName)), true);
            var room = _cheese.RoomView(row.ProfileId);
            if (room != null) yield return ("Open on Cheese Tracker ↗", () => AP_Atlas.Core.ExternalLinks.OpenWeb(room.Link), true);
            if (!string.IsNullOrEmpty(row.Tracker?.UpstreamUrl)) yield return ("Open on the Archipelago tracker ↗", () => AP_Atlas.Core.ExternalLinks.OpenWeb($"{row.Tracker.UpstreamUrl.TrimEnd('/')}/0/{g.Position}"), true);
            yield return ("Copy name", () => DisplayServer.ClipboardSet(g.Name), true);
        }

        private void RenderDetails()
        {
            var row = SelectedRow();
            string signature = DetailsSignature(row);
            if (signature == _detailsSignature) return;
            _detailsSignature = signature;
            foreach (Node child in _details.GetChildren()) child.QueueFree();
            if (row == null)
            {
                _details.AddChild(Kit.Subtle(_rows.Count == 0 ? "" : "Select a slot to see its notes and hints. Right-click a slot for its actions."));
                MainTrackerWindow.SetFontSizeRecursive(_details, Settings.ContentFontSize);
                return;
            }
            var g = row.Game;
            var now = DateTime.UtcNow; // wall clock: how long ago the site's times were, for showing
            var title = new Label { Text = $"#{g.Position}  {g.Name}  ·  {g.Game}" + (View == MineView ? "  ·  " + row.ProfileName : ""), ClipText = true };
            title.SetMeta("font_size_ratio", 1.15);
            title.AddThemeColorOverride("font_color", row.Mine ? ThemeColors.You : ThemeColors.Text);
            _details.AddChild(title);

            var facts = Rich();
            string owner = string.IsNullOrEmpty(g.OwnerName) ? Colored("unclaimed", ThemeColors.TextSubtle) : Colored(g.OwnerName + (g.OwnerAway ? " (away)" : ""), g.OwnerAway ? ThemeColors.Warning : ThemeColors.TextMuted);
            int level = CheeseTable.ActivityLevel(g, row.Tracker, now);
            facts.Markup = (g.IsComplete ? "" : Colored(CtStatus.Label(g.Progression), AP_Atlas.UI.CheeseColors.Of(g.Progression)) + Colored(" · ", ThemeColors.TextSubtle)) +
                         Colored(CtStatus.Label(g.Completion), AP_Atlas.UI.CheeseColors.Of(g.Completion)) + Colored("   Owner: ", ThemeColors.TextSubtle) + owner +
                         Colored("   Availability: ", ThemeColors.TextSubtle) + Colored(CtStatus.Label(g.Availability), ThemeColors.TextMuted) +
                         (string.IsNullOrEmpty(g.OwnerName) ? "" : Colored("   Ping: ", ThemeColors.TextSubtle) + Colored(CtStatus.Label(CheeseTable.EffectivePing(row)), PingColor(CheeseTable.EffectivePing(row)))) +
                         Colored("   Last activity: ", ThemeColors.TextSubtle) + Colored(CheeseTable.ActivityText(g, now), level == 0 ? ThemeColors.Success : level == 1 ? CheeseColors.Caution : ThemeColors.Error) +
                         Colored($"   Checks: ", ThemeColors.TextSubtle) + Colored($"{g.ChecksDone}/{g.ChecksTotal}", ThemeColors.TextMuted);
            _details.AddChild(facts);

            // Actions for rows Atlas may change.
            string cannot = _cheese.CannotEdit(row.ProfileId, g);
            var target = TargetOf(row);
            bool busy = _cheese.IsBusy(target);
            var buttons = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            buttons.AddThemeConstantOverride("h_separation", 4);
            buttons.AddThemeConstantOverride("v_separation", 4);
            _details.AddChild(buttons);
            bool canEdit = cannot == null && !busy;
            var advice = row.SlotName != null ? _cheese.SlotView(row.ProfileId, row.SlotName)?.Advice : null;
            if (advice?.Status != null && !g.IsComplete)
                buttons.AddChild(Kit.Button("Set " + CtStatus.Label(advice.Status), $"Atlas suggests it: {advice.Reason}", () => Run(_cheese.SetProgressionAsync(target, advice.Status)), canEdit));
            if (!g.IsComplete)
                foreach (var status in CtStatus.ProgressionIds.Where(s => s != "unknown" && s != g.Progression && s != advice?.Status))
                    buttons.AddChild(Kit.Button(CtStatus.Label(status), $"Set {g.Name} to {CtStatus.Label(status)}", () => Run(_cheese.SetProgressionAsync(target, status)), canEdit));
            if (g.Progression is "bk" or "soft_bk" && !g.IsComplete)
                buttons.AddChild(Kit.Button("Still BK", "Tell the room you're still watching this slot (resets its inactivity clock)", () => Run(_cheese.StillBkAsync(target), "Marked still BK"), canEdit));
            buttons.AddChild(Kit.Button("Notes…", "Edit this slot's notes", () => CheeseDialogs.Notes(this, g.Name, g.Notes, text => Run(_cheese.SetNotesAsync(target, text), "Notes saved")), canEdit));
            var ownership = _cheese.OwnershipOf(g);
            if (ownership is CheeseOwnership.Nobody or CheeseOwnership.YouByName)
                buttons.AddChild(Kit.Button("Claim", "Claim this slot with your Cheese Tracker account", () => Run(_cheese.ClaimAsync(target), $"Claimed {g.Name}"), _cheese.CanClaim(row.ProfileId, g) && !busy));
            else if (ownership == CheeseOwnership.You)
                buttons.AddChild(Kit.Button("Disclaim…", "Release your claim on this slot", () => CheeseDialogs.ConfirmDisclaim(this, g.Name, () => Run(_cheese.DisclaimAsync(target), $"Released {g.Name}")), canEdit));
            if (row.SlotName != null)
                buttons.AddChild(Kit.Button("Show in Properties", "All of this slot's details, Atlas's suggestion and automatic updates", () => Inspector.Inspect(InspectTarget.ForSlot(row.ProfileId, row.SlotName))));
            if (cannot != null) _details.AddChild(Kit.Subtle(cannot));
            if (busy) _details.AddChild(Kit.Subtle("Updating Cheese Tracker…"));

            // Automatic updates, for your slots in Atlas.
            if (row.SlotName != null)
            {
                var view = _cheese.SlotView(row.ProfileId, row.SlotName);
                if (view != null)
                {
                    if (advice != null)
                        _details.AddChild(Kit.Text(advice.InSync ? "Atlas's logic agrees: " + advice.Reason :
                            advice.Status != null ? $"Atlas suggests {CtStatus.Label(advice.Status)}: {advice.Reason}" + (advice.Ready ? "" : " (confirming)") :
                            advice.Quiet ?? "", advice.InSync ? ThemeColors.Success : advice.Status != null ? ThemeColors.Pending : ThemeColors.TextSubtle));
                    var auto = new Button
                    {
                        Text = view.AutoOn ? "Updating automatically" : "Update automatically",
                        ToggleMode = true,
                        ButtonPressed = view.AutoOn,
                        FocusMode = FocusModeEnum.None,
                        SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                        Disabled = !view.AutoOn && (view.Ownership != CheeseOwnership.You || view.CannotEdit != null),
                        TooltipText = "Atlas sets BK, Unblocked and Go mode from its logic while this slot is connected.\n" +
                                      "Only for slots claimed by your account; never Soft BK, Goal, Done or Forfeit; it pauses if anyone else changes the status."
                    };
                    string slotName = row.SlotName, profileId = row.ProfileId;
                    auto.Toggled += on =>
                    {
                        string error = _cheese.SetAuto(profileId, slotName, on);
                        if (error != null) Toast(error, ThemeColors.Error);
                        QueueRefresh();
                    };
                    _details.AddChild(auto);
                    if (view.AutoOn && view.AutoPaused != null)
                    {
                        _details.AddChild(Kit.Text("Paused: " + view.AutoPaused + ".", ThemeColors.Warning));
                        var resume = new HFlowContainer();
                        resume.AddChild(Kit.Button("Resume", "Carry on from the status the slot has now", () => _cheese.ResumeAuto(profileId, slotName)));
                        _details.AddChild(resume);
                    }
                    if (view.LastError != null) _details.AddChild(Kit.Text(view.LastError, ThemeColors.Warning));
                }
            }

            // Notes.
            _details.AddChild(Kit.Heading("Notes", 1f));
            _details.AddChild(Kit.Text(string.IsNullOrWhiteSpace(g.Notes) ? "No notes." : g.Notes, string.IsNullOrWhiteSpace(g.Notes) ? ThemeColors.TextSubtle : ThemeColors.Text));

            RenderHints(row);
            MainTrackerWindow.SetFontSizeRecursive(_details, Settings.ContentFontSize);
        }

        /// <summary>Everything the details pane shows for a row, so it's rebuilt only when that changes.</summary>
        private string DetailsSignature(CheeseRow row)
        {
            if (row == null) return "none|" + _rows.Count;
            var g = row.Game;
            var view = row.SlotName != null ? _cheese.SlotView(row.ProfileId, row.SlotName) : null;
            var a = view?.Advice;
            return string.Join("|", row.Key, g.Progression, g.Completion, g.Availability, g.Ping, g.Notes, g.OwnerName, g.OwnerAway, g.ClaimedByUserId, g.DiscordUsername,
                g.ChecksDone, g.ChecksTotal, CheeseTable.ActivityText(g, DateTime.UtcNow), row.UnfoundHints, row.Tracker?.Hints?.Count, row.Tracker?.GlobalPingPolicy,
                a?.Status, a?.Ready, a?.InSync, a?.Reason, a?.Quiet, view?.AutoOn, view?.AutoPaused, view?.LastError, view?.CannotEdit,
                _cheese.CannotEdit(row.ProfileId, g), _cheese.CanClaim(row.ProfileId, g), _cheese.IsBusy(TargetOf(row)), _sentHints, _includeFoundHints, View == MineView);
        }

        private void RenderHints(CheeseRow row)
        {
            _details.AddChild(Kit.Heading("Hints", 1f));
            var toggles = new HFlowContainer();
            toggles.AddThemeConstantOverride("h_separation", 4);
            var received = new Button { Text = "In this world", ToggleMode = true, ButtonPressed = !_sentHints, FocusMode = FocusModeEnum.None, TooltipText = "Items in this slot's world that other slots need (what Cheese Tracker calls received hints)" };
            var sent = new Button { Text = "For this slot", ToggleMode = true, ButtonPressed = _sentHints, FocusMode = FocusModeEnum.None, TooltipText = "This slot's items, in other worlds (sent hints)" };
            received.Pressed += () => { _sentHints = false; Ui.Defer(this, RenderDetails); };
            sent.Pressed += () => { _sentHints = true; Ui.Defer(this, RenderDetails); };
            var found = new Button { Text = "Include found and useless", ToggleMode = true, ButtonPressed = _includeFoundHints, FocusMode = FocusModeEnum.None };
            found.Toggled += on => { _includeFoundHints = on; Ui.Defer(this, RenderDetails); };
            toggles.AddChild(received);
            toggles.AddChild(sent);
            toggles.AddChild(found);
            _details.AddChild(toggles);

            var tracker = row.Tracker;
            if (tracker?.Hints == null || tracker.Hints.Count == 0)
            {
                _details.AddChild(Kit.Subtle(tracker?.Games.Count > 0 ? "No hints yet (or they load with the next read of the tracker)." : "No hints yet."));
                return;
            }
            var games = new Dictionary<int, CtGame>();
            foreach (var game in tracker.Games) games.TryAdd(game.Id, game);
            var hints = tracker.Hints
                .Where(h => _sentHints ? h.ReceiverGameId == row.Game.Id : h.FinderGameId == row.Game.Id)
                .Select(h => (Hint: h, State: CheeseTable.HintState(h, games)))
                .Where(x => _includeFoundHints || (x.State == "notfound" && x.Hint.Classification != "trash"))
                .OrderBy(x => x.State == "notfound" ? 0 : x.State == "useless" ? 1 : 2)
                .ThenBy(x => Array.IndexOf(HintClassOrder, x.Hint.Classification))
                .ThenBy(x => x.Hint.Id)
                .ToList();
            if (hints.Count == 0)
            {
                _details.AddChild(Kit.Subtle("There are no unfound hints right now."));
                return;
            }
            var lines = new List<string>();
            var plain = new List<string>();
            foreach (var (h, state) in hints)
            {
                string receiver = h.ReceiverGameId is int r && games.TryGetValue(r, out var rg) ? rg.Name : !string.IsNullOrEmpty(h.ItemLinkName) ? "[LINK] " + h.ItemLinkName : "(Item link)";
                string finder = games.TryGetValue(h.FinderGameId, out var fg) ? fg.Name : "?";
                string entrance = string.IsNullOrEmpty(h.Entrance) || h.Entrance == "Vanilla" ? "" : $" ({h.Entrance})";
                string text = $"{receiver}'s {h.Item} is at {finder}'s {h.Location}{entrance}";
                plain.Add(text);
                var dim = state != "notfound";
                lines.Add(Colored(HintClassLabel(h.Classification), dim ? ThemeColors.TextSubtle : HintClassColor(h.Classification)) + "  " +
                          Colored(text, dim ? ThemeColors.TextSubtle : ThemeColors.Text) + (state == "notfound" ? "" : Colored($"  ({state})", ThemeColors.TextSubtle)));
            }
            var list = Rich();
            list.SelectionEnabled = true;
            list.Markup = string.Join("\n", lines);
            _details.AddChild(list);
            var copy = new HFlowContainer();
            copy.AddChild(Kit.Button("Copy all", "Copy these hints as text", () =>
            {
                DisplayServer.ClipboardSet(string.Join("\n", plain));
                Toast($"Copied {plain.Count} hint{(plain.Count == 1 ? "" : "s")}", ThemeColors.TextSubtle);
            }));
            _details.AddChild(copy);
        }

        private static readonly string[] HintClassOrder = { "unset", "critical", "progression", "qol", "trash", "unknown" };

        private static string HintClassLabel(string id) => id switch
        {
            "critical" => "Critical",
            "progression" => "Progression",
            "qol" => "Quality of life",
            "trash" => "Trash",
            "unknown" => "Unknown",
            _ => "Unset"
        };

        private static Color HintClassColor(string id) => id switch
        {
            "critical" => CheeseColors.Critical,
            "progression" => ThemeColors.Progression,
            "qol" => CheeseColors.Qol,
            "trash" => ThemeColors.TextSubtle,
            _ => ThemeColors.TextMuted
        };

        /// <summary>Rich text in the same font as the labels around it.</summary>
        private SafeRichText Rich()
        {
            var rtl = new SafeRichText { FitContent = true, ScrollActive = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var font = GetThemeFont("font", "Label");
            if (font != null) rtl.AddThemeFontOverride("normal_font", font);
            return rtl;
        }
    }
}
