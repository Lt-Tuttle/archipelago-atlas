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
    /// and text filters, "mine first", and a status summary, with the selected slot's notes, hints and actions below.
    /// </summary>
    public partial class CheeseTrackerTab : MarginContainer
    {
        public const string MineView = "*mine";
        public const string SettingsView = "*settings";

        private static readonly Color Good = Colors.LimeGreen;
        private static readonly Color Warn = Colors.Orange;
        private static readonly Color Bad = Colors.Salmon;
        private static readonly Color Muted = Colors.Gray;

        private readonly AppSettings _settings;
        private readonly CheeseTrackerService _cheese;
        private readonly Func<IReadOnlyList<MultiworldProfile>> _profiles;
        private readonly Action<string, Color> _toast;

        /// <summary>The explorer list; the window mounts it in the explorer while this tab is shown.</summary>
        public VBoxContainer SidebarContent { get; }

        private string _view;
        private readonly CheeseFilter _filter = new CheeseFilter();
        private string _selectedKey;
        private bool _suppressPick;
        private List<CheeseRow> _rows = new List<CheeseRow>();
        private List<string> _renderedKeys = new List<string>();
        private string _columnsSignature, _barsSignature, _headerSignature, _detailsSignature;

        // Pages
        private VBoxContainer _trackerPage;
        private CheeseSettingsPage _settingsPage;
        private VBoxContainer _messagePage;

        // Tracker page
        private Label _title, _subtitle, _problemLabel, _countLabel;
        private HBoxContainer _headerButtons, _problemRow;
        private Button _accountButton, _tryNow;
        private RichTextLabel _summaryText;
        private HBoxContainer _checksBar, _slotsBar;
        private LineEdit _search;
        private MenuButton _statusMenu, _availabilityMenu, _ownerMenu, _gameMenu;
        private Button _mineFirst, _percent, _clearFilters;
        private Tree _tree;
        private VBoxContainer _details;
        private bool _sentHints, _includeFoundHints;
        private Timer _refreshDebounce, _searchDebounce;

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
        {
            _settings = settings;
            _cheese = cheese;
            _profiles = profiles;
            _toast = toast;
            Name = "Cheese Tracker";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            foreach (var side in new[] { "left", "top", "right", "bottom" }) AddThemeConstantOverride("margin_" + side, 10);
            SidebarContent = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            SidebarContent.AddThemeConstantOverride("separation", 4);
            _view = string.IsNullOrEmpty(settings.CheeseTabView) ? MineView : settings.CheeseTabView;
        }

        public override void _Ready()
        {
            BuildTrackerPage();
            _settingsPage = new CheeseSettingsPage(_settings, _cheese, _profiles, _toast, ShowView) { Visible = false };
            AddChild(_settingsPage);
            _messagePage = new VBoxContainer { Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
            _messagePage.AddThemeConstantOverride("separation", 10);
            AddChild(_messagePage);

            _refreshDebounce = new Timer { OneShot = true, WaitTime = 0.3 };
            _refreshDebounce.Timeout += Refresh;
            AddChild(_refreshDebounce);
            _searchDebounce = new Timer { OneShot = true, WaitTime = 0.25 };
            _searchDebounce.Timeout += () =>
            {
                _filter.Text = _search.Text;
                RenderTable();
            };
            AddChild(_searchDebounce);
            // While shown: keep the trackers on screen fresh (the service reads at most every 10 minutes) and the ages current.
            var minute = new Timer { WaitTime = 60, Autostart = true };
            minute.Timeout += () =>
            {
                if (!IsVisibleInTree()) return;
                WatchShown();
                Refresh();
            };
            AddChild(minute);

            // Made while the tab is in the window, again after it's moved (docking, pop-outs), and removed while it isn't.
            AddChild(new TreeSubscriptions()
                .On(() => _cheese.Changed += QueueRefresh, () => _cheese.Changed -= QueueRefresh)
                .On(() => _cheese.LinkChanged += OnLinkChanged, () => _cheese.LinkChanged -= OnLinkChanged));
            Refresh();
        }

        private void OnLinkChanged(string profileId) => QueueRefresh();

        /// <summary>The tab was just shown: read what it shows if that's due, and draw it.</summary>
        public void OnShown()
        {
            WatchShown();
            Refresh();
        }

        /// <summary>Shows a multiworld (profile id), <see cref="MineView"/> or <see cref="SettingsView"/>.</summary>
        public void ShowView(string view)
        {
            if (string.IsNullOrEmpty(view)) view = MineView;
            if (view != _view)
            {
                _view = view;
                _selectedKey = null;
                _renderedKeys.Clear();
                _settings.CheeseTabView = view;
                DataManager.SaveSettings(_settings);
            }
            WatchShown();
            Refresh();
        }

        private void QueueRefresh()
        {
            if (!IsVisibleInTree()) return; // OnShown redraws
            if (_refreshDebounce != null && _refreshDebounce.IsInsideTree() && _refreshDebounce.IsStopped()) _refreshDebounce.Start();
        }

        private List<MultiworldProfile> ProfilesShown()
        {
            if (_view == SettingsView) return new List<MultiworldProfile>();
            if (_view == MineView) return _cheese.LinkedProfiles();
            var p = _profiles().FirstOrDefault(x => x.Id == _view);
            return p == null ? new List<MultiworldProfile>() : new List<MultiworldProfile> { p };
        }

        private void WatchShown()
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
            _accountButton = new Button { FocusMode = FocusModeEnum.None, TooltipText = "Your Cheese Tracker account and API key" };
            _accountButton.Pressed += () => ShowView(SettingsView);
            header.AddChild(_accountButton);

            _subtitle = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _subtitle.AddThemeColorOverride("font_color", Muted);
            _trackerPage.AddChild(_subtitle);

            _problemRow = new HBoxContainer { Visible = false };
            _problemRow.AddThemeConstantOverride("separation", 6);
            _problemLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _problemLabel.AddThemeColorOverride("font_color", Warn);
            _problemRow.AddChild(_problemLabel);
            _tryNow = new Button { Text = "Try now", FocusMode = FocusModeEnum.None, TooltipText = "Ask Cheese Tracker again now" };
            _tryNow.Pressed += () =>
            {
                foreach (var p in ProfilesShown()) CheeseDialogs.Run(this, _cheese.RefreshAsync(p.Id, tryNow: true), _toast, null, QueueRefresh);
            };
            _problemRow.AddChild(_tryNow);
            _trackerPage.AddChild(_problemRow);

            // Summary: counts, then remaining checks by progression status and slots by status (like Cheese Tracker's).
            _summaryText = Rich();
            _trackerPage.AddChild(_summaryText);
            _checksBar = Bar("Checks left, by status");
            _slotsBar = Bar("Slots, by status");

            // Filters and display options.
            var toolbar = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            toolbar.AddThemeConstantOverride("h_separation", 6);
            toolbar.AddThemeConstantOverride("v_separation", 4);
            _trackerPage.AddChild(toolbar);
            _search = new LineEdit { PlaceholderText = "Find a slot, owner, game or note…", ClearButtonEnabled = true, CustomMinimumSize = new Vector2(240, 0) };
            _search.TextChanged += _ => _searchDebounce.Start();
            toolbar.AddChild(_search);
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
            _mineFirst = new Button { Text = "Mine first", ToggleMode = true, ButtonPressed = _settings.CheeseMineFirst, FocusMode = FocusModeEnum.None, TooltipText = "Your slots on top, before any column sort" };
            _mineFirst.Toggled += on =>
            {
                _settings.CheeseMineFirst = on;
                DataManager.SaveSettings(_settings);
                RenderTable();
            };
            toolbar.AddChild(_mineFirst);
            _percent = new Button { Text = "Checks %", ToggleMode = true, ButtonPressed = _settings.CheeseChecksAsPercent, FocusMode = FocusModeEnum.None, TooltipText = "Show checks as a percentage" };
            _percent.Toggled += on =>
            {
                _settings.CheeseChecksAsPercent = on;
                DataManager.SaveSettings(_settings);
                RenderTable();
            };
            toolbar.AddChild(_percent);
            _clearFilters = new Button { Text = "Clear filters", FocusMode = FocusModeEnum.None, Visible = false };
            _clearFilters.Pressed += () =>
            {
                _filter.Clear();
                _search.Text = "";
                BuildStatusMenu();
                BuildAvailabilityMenu();
                RenderTable();
            };
            toolbar.AddChild(_clearFilters);
            _countLabel = new Label { VerticalAlignment = VerticalAlignment.Center };
            _countLabel.AddThemeColorOverride("font_color", Muted);
            toolbar.AddChild(_countLabel);

            var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffsets = new[] { _settings.CheeseTableSplitOffset } };
            split.Dragged += offset =>
            {
                _settings.CheeseTableSplitOffset = (int)offset;
                DataManager.SaveSettingsSoon(_settings);
            };
            _trackerPage.AddChild(split);
            _tree = new Tree
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                ColumnTitlesVisible = true,
                HideRoot = true,
                SelectMode = Tree.SelectModeEnum.Row,
                AllowRmbSelect = true,
                CustomMinimumSize = new Vector2(0, 160)
            };
            _tree.AddThemeConstantOverride("v_separation", 6);
            _tree.ColumnTitleClicked += (column, button) =>
            {
                if (button != (long)MouseButton.Left || column < 0 || column >= _columns.Count) return;
                string id = _columns[(int)column].Id;
                if (_settings.CheeseSortColumn == id) _settings.CheeseSortDescending = !_settings.CheeseSortDescending;
                else
                {
                    _settings.CheeseSortColumn = id;
                    _settings.CheeseSortDescending = CheeseTable.DefaultDescending(id);
                }
                DataManager.SaveSettings(_settings);
                RenderTable();
            };
            _tree.ItemSelected += OnRowSelected;
            _tree.ItemMouseSelected += (position, button) =>
            {
                if (button == (long)MouseButton.Right) ShowRowMenu();
            };
            split.AddChild(_tree);

            var detailScroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.Fill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 150) };
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
            name.AddThemeColorOverride("font_color", Muted);
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

        private string _sidebarSignature;

        private void RenderSidebar()
        {
            var profiles = _profiles();
            string signature = _view + "|" + string.Join("|", profiles.Select(p => p.Id + "=" + p.Name + "=" + (string.IsNullOrWhiteSpace(p.CheeseTrackerUrl) ? "" : "L")));
            if (signature == _sidebarSignature) return;
            _sidebarSignature = signature;
            foreach (Node child in SidebarContent.GetChildren()) child.QueueFree();
            SidebarContent.AddChild(ExplorerButton("My slots", "Your slots in every linked multiworld", MineView, false));
            SidebarContent.AddChild(new HSeparator());
            foreach (var p in profiles)
            {
                bool linked = !string.IsNullOrWhiteSpace(p.CheeseTrackerUrl);
                SidebarContent.AddChild(ExplorerButton(linked ? p.Name : p.Name + "  (not linked)", linked ? "Show this multiworld's tracker" : "Link this multiworld to Cheese Tracker", p.Id, !linked));
            }
            if (profiles.Count == 0)
            {
                var none = new Label { Text = "No multiworlds yet (Connections tab).", AutowrapMode = TextServer.AutowrapMode.WordSmart };
                none.AddThemeColorOverride("font_color", Muted);
                SidebarContent.AddChild(none);
            }
            SidebarContent.AddChild(new HSeparator());
            SidebarContent.AddChild(ExplorerButton("Settings", "Your API key, links and automatic updates", SettingsView, false));
            MainTrackerWindow.SetFontSizeRecursive(SidebarContent, _settings.ExplorerFontSize);
        }

        private Button ExplorerButton(string text, string tooltip, string view, bool dim)
        {
            var b = new Button { Text = text, TooltipText = tooltip, Alignment = HorizontalAlignment.Left, ToggleMode = true, ButtonPressed = view == _view, FocusMode = FocusModeEnum.None, ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            if (dim && view != _view) b.AddThemeColorOverride("font_color", Muted);
            b.Pressed += () =>
            {
                _sidebarSignature = null;
                ShowView(view);
            };
            return b;
        }

        // =====================================================================
        // Pages
        // =====================================================================

        /// <summary>Redraws the tab from the service's data (keeps the selection, scroll and filters).</summary>
        public void Refresh()
        {
            if (_trackerPage == null) return;
            using var __perf = PerfMonitor.Measure("Cheese Tracker tab: refresh");
            RenderSidebar();
            _accountButton.Text = _cheese.KeyRejected ? "Key not accepted" : _cheese.HasKey ? "● " + (_cheese.AccountName ?? "Signed in") : "Add API key";
            _accountButton.AddThemeColorOverride("font_color", _cheese.KeyRejected ? Warn : _cheese.HasKey ? Good : Muted);

            if (_view == SettingsView)
            {
                ShowPage(_settingsPage);
                _settingsPage.Refresh();
                return;
            }
            var profile = _view == MineView ? null : _profiles().FirstOrDefault(p => p.Id == _view);
            if (_view != MineView && profile == null)
            {
                _view = MineView;
                _sidebarSignature = null;
                RenderSidebar();
            }
            if (profile != null && string.IsNullOrWhiteSpace(profile.CheeseTrackerUrl))
            {
                ShowNotLinked(profile);
                return;
            }
            var linked = _cheese.LinkedProfiles();
            if (_view == MineView && linked.Count == 0)
            {
                ShowMessage("No multiworld is linked to Cheese Tracker yet.",
                    "Link one from the explorer on the left (or the multiworld's field on the Connections tab) to see everyone's status here.",
                    _profiles().Take(6).Select(p => ($"Link {p.Name}…", (Action)(() => CheeseDialogs.Link(this, _cheese, p, _toast, Refresh)))).ToList());
                return;
            }
            ShowPage(_trackerPage);
            RenderTrackerPage(profile, linked);
        }

        private void ShowPage(Control page)
        {
            _trackerPage.Visible = page == _trackerPage;
            _settingsPage.Visible = page == _settingsPage;
            _messagePage.Visible = page == _messagePage;
        }

        private void ShowNotLinked(MultiworldProfile profile)
        {
            var actions = new List<(string, Action)> { ($"Link {profile.Name}…", () => CheeseDialogs.Link(this, _cheese, profile, _toast, Refresh)) };
            if (_cheese.HasKey) actions.Add(("Find on my dashboard", () => CheeseDialogs.FindOnDashboard(this, _cheese, profile, _toast, Refresh)));
            else actions.Add(("Settings…", () => ShowView(SettingsView)));
            ShowMessage($"{profile.Name} isn't linked to Cheese Tracker.",
                "Paste its Cheese Tracker link, or the archipelago.gg room link (Cheese Tracker finds or starts the room's tracker). " +
                (_cheese.HasKey ? "Or let Atlas look for it among the trackers on your dashboard." : "With your API key added (Settings), Atlas can also look for it on your dashboard."),
                actions);
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

        private void RenderTrackerPage(MultiworldProfile profile, List<MultiworldProfile> linked)
        {
            var shown = profile != null ? new List<MultiworldProfile> { profile } : linked;
            var rooms = shown.Select(p => (Profile: p, Room: _cheese.RoomView(p.Id))).Where(x => x.Room != null).ToList();

            // Header (its buttons are rebuilt only when what they act on changes).
            string headerSignature = _view + "|" + profile?.CheeseTrackerUrl + "|" + string.Join(",", rooms.Select(r => r.Room.Busy + r.Room.Link));
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
                    _headerButtons.AddChild(HeaderButton("Refresh", "Read the tracker again now", () => CheeseDialogs.Run(this, _cheese.RefreshAsync(profile.Id), _toast, null, QueueRefresh), room?.Busy != true));
                    if (room != null) _headerButtons.AddChild(HeaderButton("Open ↗", "Open this tracker on Cheese Tracker", () => AP_Atlas.Core.ExternalLinks.OpenWeb(room.Link)));
                    _headerButtons.AddChild(HeaderButton("Change link…", "Link this multiworld to a different tracker", () => CheeseDialogs.Link(this, _cheese, profile, _toast, Refresh)));
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
                    _headerButtons.AddChild(HeaderButton("Refresh", "Read every linked tracker again now", () =>
                    {
                        foreach (var (p, _) in rooms) CheeseDialogs.Run(this, _cheese.RefreshAsync(p.Id), _toast, null, QueueRefresh);
                    }, busy == 0));
            }
            if (rebuildButtons) MainTrackerWindow.SetFontSizeRecursive(_headerButtons, _settings.ContentFontSize);

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

        private Button HeaderButton(string text, string tooltip, Action onPressed, bool enabled = true)
        {
            var b = new Button { Text = text, TooltipText = tooltip, Disabled = !enabled, FocusMode = FocusModeEnum.None };
            b.Pressed += () => onPressed();
            return b;
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
            string counts = string.Join(Colored(" · ", Muted), order.Where(unified.ContainsKey).Select(id => Colored($"{unified[id]} {UnifiedLabel(id)}", UnifiedColor(id))));
            _summaryText.Text = Colored($"{Plural(stat.Count, "slot")} · {Plural(players, "player")} · {Plural(games, "game")} · {done}/{total} checks" + (total > 0 ? $" ({100.0 * done / total:0}%)" : ""), Colors.LightGray) +
                                (counts.Length > 0 ? "   " + counts : "");

            string barsSignature = string.Join(",", unified.OrderBy(kv => kv.Key).Select(kv => kv.Key + kv.Value)) + "|" +
                string.Join(",", stat.GroupBy(r => r.Game.Progression).OrderBy(g => g.Key).Select(g => g.Key + g.Sum(r => Math.Max(0, r.Game.ChecksTotal - r.Game.ChecksDone)))) + "|" + done;
            if (barsSignature == _barsSignature) return;
            _barsSignature = barsSignature;
            var progressionOrder = new[] { "unknown", "bk", "soft_bk", "unblocked", "go" };
            var checks = progressionOrder.Select(id => (CtStatus.Label(id) + " (remaining)", (double)stat.Where(r => r.Game.Progression == id).Sum(r => Math.Max(0, r.Game.ChecksTotal - r.Game.ChecksDone)), AP_Atlas.UI.CheeseColors.Of(id) == Colors.WhiteSmoke ? Colors.LightSlateGray : AP_Atlas.UI.CheeseColors.Of(id)))
                .Append(("Checked", done, Good.Darkened(0.25f)));
            FillBar(_checksBar, checks);
            FillBar(_slotsBar, order.Select(id => (UnifiedLabel(id), (double)unified.GetValueOrDefault(id), UnifiedColor(id))));
        }

        private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

        /// <summary>Cheese Tracker's combined status: BK or Soft BK while incomplete, otherwise the completion status.</summary>
        private static string Unified(CtGame g) => !g.IsComplete && g.Progression is "bk" or "soft_bk" ? g.Progression : g.Completion;

        private static string UnifiedLabel(string id) => id == "incomplete" ? "in progress" : CtStatus.Label(id);

        private static Color UnifiedColor(string id) => id == "incomplete" ? Colors.LightSlateGray : AP_Atlas.UI.CheeseColors.Of(id);

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

        private static string Colored(string text, Color c) => $"[color=#{c.ToHtml(false)}]{(text ?? "").Replace("[", "[lb]")}[/color]";

        // =====================================================================
        // Table
        // =====================================================================

        private void ConfigureColumns(bool includeMultiworld)
        {
            // "My slots" are yours, so their ping and availability say little; the multiworld matters instead.
            var columns = AllColumns.Where(c => includeMultiworld ? c.Id is not ("ping" or "availability") : c.Id != "multiworld").ToList();
            float scale = Math.Max(0.8f, _settings.ContentFontSize / 14f);
            string signature = string.Join(",", columns.Select(c => c.Id)) + "@" + scale;
            if (signature == _columnsSignature) return;
            _columnsSignature = signature;
            _columns = columns;
            _tree.Clear();
            _renderedKeys.Clear();
            _tree.Columns = columns.Count;
            for (int i = 0; i < columns.Count; i++)
            {
                var c = columns[i];
                _tree.SetColumnExpand(i, c.Ratio > 0);
                _tree.SetColumnExpandRatio(i, Math.Max(1, c.Ratio));
                _tree.SetColumnCustomMinimumWidth(i, (int)(c.MinWidth * scale));
                _tree.SetColumnClipContent(i, true);
                _tree.SetColumnTitleAlignment(i, c.Align);
            }
        }

        private void RenderTable()
        {
            if (_tree == null || _columns.Count == 0) return;
            using var __perf = PerfMonitor.Measure("Cheese Tracker tab: table");
            var now = DateTime.UtcNow;
            for (int i = 0; i < _columns.Count; i++)
            {
                string arrow = _columns[i].Id == _settings.CheeseSortColumn ? (_settings.CheeseSortDescending ? " ▼" : " ▲") : "";
                _tree.SetColumnTitle(i, _columns[i].Title + arrow);
            }

            var shown = CheeseTable.Sort(_rows.Where(r => CheeseTable.Passes(r, _filter, ClaimedByYou(r))), _settings.CheeseSortColumn, _settings.CheeseSortDescending, _settings.CheeseMineFirst, now);
            var keys = shown.Select(r => r.Key).ToList();
            _countLabel.Text = _filter.IsActive ? $"Showing {shown.Count} of {_rows.Count}" : $"{_rows.Count} slots";
            _clearFilters.Visible = _filter.IsActive;
            SetMenuActive(_statusMenu, _filter.HiddenProgression.Count + _filter.HiddenCompletion.Count > 0);
            SetMenuActive(_availabilityMenu, _filter.HiddenAvailability.Count > 0);
            SetMenuActive(_ownerMenu, _filter.Owner != null);
            SetMenuActive(_gameMenu, _filter.Game != null);

            _suppressPick = true;
            try
            {
                if (keys.SequenceEqual(_renderedKeys) && _tree.GetRoot() != null)
                {
                    // Same rows in the same order: update the cells in place (keeps the scroll position and selection).
                    var item = _tree.GetRoot().GetFirstChild();
                    foreach (var row in shown)
                    {
                        if (item == null) break;
                        FillRow(item, row, now);
                        item = item.GetNext();
                    }
                }
                else
                {
                    float scroll = ScrollBar()?.Value is double v ? (float)v : 0f;
                    _tree.Clear();
                    var root = _tree.CreateItem();
                    TreeItem reselect = null;
                    foreach (var row in shown)
                    {
                        var item = _tree.CreateItem(root);
                        item.SetMetadata(0, row.Key);
                        FillRow(item, row, now);
                        if (row.Key == _selectedKey) reselect = item;
                    }
                    if (shown.Count == 0)
                    {
                        var empty = _tree.CreateItem(root);
                        empty.SetText(Math.Min(2, _columns.Count - 1), _rows.Count == 0 ? "No slots to show yet." : "No slots match the filters.");
                        empty.SetCustomColor(Math.Min(2, _columns.Count - 1), Muted);
                        for (int i = 0; i < _columns.Count; i++) empty.SetSelectable(i, false);
                    }
                    _renderedKeys = keys;
                    if (reselect != null) reselect.Select(0);
                    var bar = ScrollBar();
                    if (bar != null) Ui.Defer(bar, () => bar.Value = scroll);
                }
            }
            finally
            {
                _suppressPick = false;
            }
            RenderDetails();
        }

        private VScrollBar ScrollBar() => _tree.GetChildren(true).OfType<VScrollBar>().FirstOrDefault();

        private static void SetMenuActive(MenuButton menu, bool active)
        {
            string text = menu.GetMeta("base_text").AsString();
            menu.Text = active ? text + " (filtered) ▾" : text + " ▾";
            if (active) menu.AddThemeColorOverride("font_color", Colors.Gold);
            else menu.RemoveThemeColorOverride("font_color");
        }

        private bool ClaimedByYou(CheeseRow r) => _cheese.OwnershipOf(r.Game) is CheeseOwnership.You or CheeseOwnership.YouByName;

        private void FillRow(TreeItem item, CheeseRow row, DateTime now)
        {
            var g = row.Game;
            for (int i = 0; i < _columns.Count; i++)
            {
                var (text, color, tip) = Cell(_columns[i].Id, row, now);
                item.SetText(i, text ?? "");
                item.SetCustomColor(i, color);
                item.SetTooltipText(i, tip ?? "");
                item.SetTextAlignment(i, _columns[i].Align);
            }
            int nameColumn = _columns.FindIndex(c => c.Id == "name");
            if (nameColumn < 0) return;
            if (row.Mine) item.SetCustomBgColor(nameColumn, new Color(1, 0, 1, 0.08f));
            else item.ClearCustomBgColor(nameColumn);
        }

        private (string Text, Color Color, string Tip) Cell(string column, CheeseRow row, DateTime now)
        {
            var g = row.Game;
            switch (column)
            {
                case "multiworld":
                    return (row.ProfileName, Colors.LightGray, null);
                case "position":
                    return (g.Position.ToString(CultureInfo.InvariantCulture), Muted, null);
                case "name":
                    return (g.Name, row.Mine ? Colors.Magenta : Colors.White,
                        row.SlotName != null ? $"Your slot {row.SlotName} in Atlas (click to show it in Properties)" : ClaimedByYou(row) ? "Claimed by you" : null);
                case "ping":
                    if (string.IsNullOrEmpty(g.OwnerName)) return ("", Muted, null);
                    string ping = CheeseTable.EffectivePing(row);
                    return (CtStatus.Label(ping), PingColor(ping), !string.IsNullOrEmpty(row.Tracker?.GlobalPingPolicy) ? "Set for everyone by the organizer" : null);
                case "availability":
                    return (CtStatus.Label(g.Availability), g.Availability switch { "open" => Good, "public" => Colors.SkyBlue, "claimed" => Colors.LightGray, _ => Muted }, null);
                case "owner":
                    if (string.IsNullOrEmpty(g.OwnerName)) return ("—", Muted, "Unclaimed");
                    return (g.OwnerName + (g.OwnerAway ? " (away)" : ""), g.OwnerAway ? Warn : ClaimedByYou(row) ? Colors.Magenta : Colors.LightGray,
                        g.OwnerAway ? "The owner is away" : g.ClaimedByUserId == null ? "Claimed without signing in" : null);
                case "game":
                    return (g.Game, Colors.LightGray, null);
                case "status":
                    return StatusCell(row);
                case "activity":
                    {
                        int level = CheeseTable.ActivityLevel(g, row.Tracker, now);
                        var latest = g.LastCheckedUtc != null && (g.LastActivityUtc == null || g.LastCheckedUtc > g.LastActivityUtc) ? g.LastCheckedUtc : g.LastActivityUtc;
                        string tip = latest == null ? "No checks yet" : latest.Value.ToLocalTime().ToString("g") + (latest == g.LastCheckedUtc && g.LastCheckedUtc != g.LastActivityUtc ? " (still BK)" : "");
                        return (CheeseTable.ActivityText(g, now), level == 0 ? Good : level == 1 ? Colors.Gold : Bad, tip);
                    }
                case "checks":
                    {
                        bool complete = g.ChecksTotal > 0 && g.ChecksDone >= g.ChecksTotal;
                        string text = _settings.CheeseChecksAsPercent
                            ? (g.ChecksTotal > 0 ? $"{100.0 * g.ChecksDone / g.ChecksTotal:0}%" : "—")
                            : $"{g.ChecksDone}/{g.ChecksTotal}";
                        return (text, complete ? Good : Colors.White, $"{g.ChecksDone} of {g.ChecksTotal} checks");
                    }
                case "hints":
                    {
                        bool notes = !string.IsNullOrWhiteSpace(g.Notes);
                        int n = row.UnfoundHints;
                        var color = n == 0 ? (notes ? Colors.SkyBlue : Muted) : n <= 5 ? Colors.SkyBlue : n <= 10 ? Colors.Gold : Bad;
                        bool hintsKnown = row.Tracker?.Hints != null && row.Tracker.Hints.Count > 0;
                        return (n + (notes ? "*" : ""), color, (hintsKnown ? $"{n} unfound hint{(n == 1 ? "" : "s")} for items in this world" : "Hints load with the next read of the tracker") + (notes ? "; this slot has notes" : ""));
                    }
            }
            return ("", Muted, null);
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
            if (view?.AutoOn == true && view.AutoPaused != null) return (text + " · paused", Warn, tip + "\nAutomatic updates are paused: " + view.AutoPaused);
            if (a?.Status != null && a.Ready)
                return ($"{text} → {CtStatus.Label(a.Status)}?", Colors.Yellow, tip + $"\nAtlas suggests {CtStatus.Label(a.Status)}: {a.Reason}" + (view.AutoOn ? " (it will set it automatically)" : ""));
            if (view?.AutoOn == true) return (text + " · auto", color, tip + "\nAtlas keeps this updated automatically" + (a?.InSync == true ? " (its logic agrees)" : ""));
            if (a?.InSync == true) return (text, color, tip + "\nAtlas's logic agrees: " + a.Reason);
            return (text, color, tip);
        }

        private static Color PingColor(string ping) => ping switch
        {
            "liberally" => Good,
            "sparingly" or "hints" => Colors.Gold,
            "see_notes" => Colors.SkyBlue,
            _ => Bad
        };

        private CheeseRow SelectedRow() => _selectedKey == null ? null : _rows.FirstOrDefault(r => r.Key == _selectedKey);

        private void OnRowSelected()
        {
            var meta = _tree.GetSelected()?.GetMetadata(0) ?? default;
            if (meta.VariantType != Variant.Type.String) return;
            _selectedKey = meta.AsString();
            RenderDetails();
            if (_suppressPick) return;
            var row = SelectedRow();
            if (row?.SlotName != null) Inspector.Inspect(InspectTarget.ForSlot(row.ProfileId, row.SlotName));
        }

        // =====================================================================
        // Actions (row menu and details)
        // =====================================================================

        private CheeseTarget TargetOf(CheeseRow row) => row.SlotName != null ? CheeseTarget.Slot(row.ProfileId, row.SlotName) : CheeseTarget.Row(row.ProfileId, row.Game.Id);

        private void Run(System.Threading.Tasks.Task<string> change, string success = null) => CheeseDialogs.Run(this, change, _toast, success, QueueRefresh);

        private const int MenuShowInProperties = 50, MenuOpenCheese = 51, MenuOpenArchipelago = 52, MenuCopyName = 53;

        private void ShowRowMenu()
        {
            var row = SelectedRow();
            if (row == null) return;
            var menu = new PopupMenu();
            var actions = new Dictionary<long, Action>();
            void Add(string label, Action action, bool enabled = true)
            {
                long id = actions.Count + 1;
                menu.AddItem(label, (int)id);
                menu.SetItemDisabled(menu.GetItemIndex((int)id), !enabled);
                actions[id] = action;
            }
            string cannot = _cheese.CannotEdit(row.ProfileId, row.Game);
            var g = row.Game;
            var target = TargetOf(row);
            if (cannot != null)
            {
                Add(cannot.Length > 80 ? cannot.Substring(0, 77) + "…" : cannot, () => { }, false);
                if (_cheese.CanClaim(row.ProfileId, g)) Add("Claim", () => Run(_cheese.ClaimAsync(target), $"Claimed {g.Name}"));
                menu.AddSeparator();
            }
            else
            {
                if (!g.IsComplete)
                    foreach (var status in CtStatus.ProgressionIds.Where(s => s != "unknown" && s != g.Progression))
                        Add("Set " + CtStatus.Label(status), () => Run(_cheese.SetProgressionAsync(target, status)));
                if (g.Progression is "bk" or "soft_bk" && !g.IsComplete) Add("Still BK", () => Run(_cheese.StillBkAsync(target), "Marked still BK"));
                Add("Notes…", () => CheeseDialogs.Notes(this, g.Name, g.Notes, text => Run(_cheese.SetNotesAsync(target, text), "Notes saved")));
                var ping = new PopupMenu();
                for (int i = 0; i < CtStatus.PingIds.Length; i++)
                {
                    ping.AddRadioCheckItem(CtStatus.Label(CtStatus.PingIds[i]), i);
                    ping.SetItemChecked(i, CtStatus.PingIds[i] == g.Ping);
                }
                ping.IdPressed += id => Run(_cheese.SetPingAsync(target, CtStatus.PingIds[(int)id]));
                menu.AddSubmenuNodeItem("Ping", ping);
                if (g.Completion != "done") Add("Mark Done…", () => CheeseDialogs.ConfirmDone(this, g.Name, () => Run(_cheese.SetCompletionAsync(target, "done"))));
                if (g.Completion != "released") Add("Forfeit…", () => CheeseDialogs.ConfirmForfeit(this, g.Name, () => Run(_cheese.SetCompletionAsync(target, "released"))));
                if (g.Completion is "done" or "released") Add("Not done after all", () => Run(_cheese.SetCompletionAsync(target, "incomplete")));
                var owner = _cheese.OwnershipOf(g);
                if (owner is CheeseOwnership.Nobody or CheeseOwnership.YouByName) Add("Claim", () => Run(_cheese.ClaimAsync(target), $"Claimed {g.Name}"), _cheese.HasKey);
                else if (owner == CheeseOwnership.You) Add("Disclaim…", () => CheeseDialogs.ConfirmDisclaim(this, g.Name, () => Run(_cheese.DisclaimAsync(target), $"Released {g.Name}")));
                menu.AddSeparator();
            }
            if (row.SlotName != null) Add("Show in Properties", () => Inspector.Inspect(InspectTarget.ForSlot(row.ProfileId, row.SlotName)));
            var room = _cheese.RoomView(row.ProfileId);
            if (room != null) Add("Open on Cheese Tracker ↗", () => AP_Atlas.Core.ExternalLinks.OpenWeb(room.Link));
            if (!string.IsNullOrEmpty(row.Tracker?.UpstreamUrl)) Add("Open on the Archipelago tracker ↗", () => AP_Atlas.Core.ExternalLinks.OpenWeb($"{row.Tracker.UpstreamUrl.TrimEnd('/')}/0/{g.Position}"));
            Add("Copy name", () => DisplayServer.ClipboardSet(g.Name));
            menu.IdPressed += id =>
            {
                if (actions.TryGetValue(id, out var action)) action();
            };
            menu.PopupHide += () => menu.QueueFree();
            AddChild(menu);
            MainTrackerWindow.SetFontSizeRecursive(menu, _settings.ContentFontSize);
            menu.Position = (Vector2I)GetGlobalMousePosition();
            menu.Popup();
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
                _details.AddChild(DetailText(_rows.Count == 0 ? "" : "Select a slot to see its notes and hints. Right-click a slot for its actions.", Muted));
                MainTrackerWindow.SetFontSizeRecursive(_details, _settings.ContentFontSize);
                return;
            }
            var g = row.Game;
            var now = DateTime.UtcNow;
            var title = new Label { Text = $"#{g.Position}  {g.Name}  ·  {g.Game}" + (_view == MineView ? "  ·  " + row.ProfileName : ""), ClipText = true };
            title.SetMeta("font_size_ratio", 1.15);
            title.AddThemeColorOverride("font_color", row.Mine ? Colors.Magenta : Colors.White);
            _details.AddChild(title);

            var facts = Rich();
            string owner = string.IsNullOrEmpty(g.OwnerName) ? Colored("unclaimed", Muted) : Colored(g.OwnerName + (g.OwnerAway ? " (away)" : ""), g.OwnerAway ? Warn : Colors.LightGray);
            int level = CheeseTable.ActivityLevel(g, row.Tracker, now);
            facts.Text = (g.IsComplete ? "" : Colored(CtStatus.Label(g.Progression), AP_Atlas.UI.CheeseColors.Of(g.Progression)) + Colored(" · ", Muted)) +
                         Colored(CtStatus.Label(g.Completion), AP_Atlas.UI.CheeseColors.Of(g.Completion)) + Colored("   Owner: ", Muted) + owner +
                         Colored("   Availability: ", Muted) + Colored(CtStatus.Label(g.Availability), Colors.LightGray) +
                         (string.IsNullOrEmpty(g.OwnerName) ? "" : Colored("   Ping: ", Muted) + Colored(CtStatus.Label(CheeseTable.EffectivePing(row)), PingColor(CheeseTable.EffectivePing(row)))) +
                         Colored("   Last activity: ", Muted) + Colored(CheeseTable.ActivityText(g, now), level == 0 ? Good : level == 1 ? Colors.Gold : Bad) +
                         Colored($"   Checks: ", Muted) + Colored($"{g.ChecksDone}/{g.ChecksTotal}", Colors.LightGray);
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
                buttons.AddChild(DetailButton("Set " + CtStatus.Label(advice.Status), $"Atlas suggests it: {advice.Reason}", () => Run(_cheese.SetProgressionAsync(target, advice.Status)), canEdit));
            if (!g.IsComplete)
                foreach (var status in CtStatus.ProgressionIds.Where(s => s != "unknown" && s != g.Progression && s != advice?.Status))
                    buttons.AddChild(DetailButton(CtStatus.Label(status), $"Set {g.Name} to {CtStatus.Label(status)}", () => Run(_cheese.SetProgressionAsync(target, status)), canEdit));
            if (g.Progression is "bk" or "soft_bk" && !g.IsComplete)
                buttons.AddChild(DetailButton("Still BK", "Tell the room you're still watching this slot (resets its inactivity clock)", () => Run(_cheese.StillBkAsync(target), "Marked still BK"), canEdit));
            buttons.AddChild(DetailButton("Notes…", "Edit this slot's notes", () => CheeseDialogs.Notes(this, g.Name, g.Notes, text => Run(_cheese.SetNotesAsync(target, text), "Notes saved")), canEdit));
            var ownership = _cheese.OwnershipOf(g);
            if (ownership is CheeseOwnership.Nobody or CheeseOwnership.YouByName)
                buttons.AddChild(DetailButton("Claim", "Claim this slot with your Cheese Tracker account", () => Run(_cheese.ClaimAsync(target), $"Claimed {g.Name}"), _cheese.CanClaim(row.ProfileId, g) && !busy));
            else if (ownership == CheeseOwnership.You)
                buttons.AddChild(DetailButton("Disclaim…", "Release your claim on this slot", () => CheeseDialogs.ConfirmDisclaim(this, g.Name, () => Run(_cheese.DisclaimAsync(target), $"Released {g.Name}")), canEdit));
            if (row.SlotName != null)
                buttons.AddChild(DetailButton("Show in Properties", "All of this slot's details, Atlas's suggestion and automatic updates", () => Inspector.Inspect(InspectTarget.ForSlot(row.ProfileId, row.SlotName))));
            if (cannot != null) _details.AddChild(DetailText(cannot, Muted));
            if (busy) _details.AddChild(DetailText("Updating Cheese Tracker…", Muted));

            // Automatic updates, for your slots in Atlas.
            if (row.SlotName != null)
            {
                var view = _cheese.SlotView(row.ProfileId, row.SlotName);
                if (view != null)
                {
                    if (advice != null)
                        _details.AddChild(DetailText(advice.InSync ? "Atlas's logic agrees: " + advice.Reason :
                            advice.Status != null ? $"Atlas suggests {CtStatus.Label(advice.Status)}: {advice.Reason}" + (advice.Ready ? "" : " (confirming)") :
                            advice.Quiet ?? "", advice.InSync ? Good : advice.Status != null ? Colors.Yellow : Muted));
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
                        if (error != null) _toast(error, Bad);
                        QueueRefresh();
                    };
                    _details.AddChild(auto);
                    if (view.AutoOn && view.AutoPaused != null)
                    {
                        _details.AddChild(DetailText("Paused: " + view.AutoPaused + ".", Warn));
                        var resume = new HFlowContainer();
                        resume.AddChild(DetailButton("Resume", "Carry on from the status the slot has now", () => _cheese.ResumeAuto(profileId, slotName)));
                        _details.AddChild(resume);
                    }
                    if (view.LastError != null) _details.AddChild(DetailText(view.LastError, Warn));
                }
            }

            // Notes.
            _details.AddChild(DetailHeading("Notes"));
            _details.AddChild(DetailText(string.IsNullOrWhiteSpace(g.Notes) ? "No notes." : g.Notes, string.IsNullOrWhiteSpace(g.Notes) ? Muted : Colors.White));

            RenderHints(row);
            MainTrackerWindow.SetFontSizeRecursive(_details, _settings.ContentFontSize);
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
                _cheese.CannotEdit(row.ProfileId, g), _cheese.CanClaim(row.ProfileId, g), _cheese.IsBusy(TargetOf(row)), _sentHints, _includeFoundHints, _view == MineView);
        }

        private void RenderHints(CheeseRow row)
        {
            _details.AddChild(DetailHeading("Hints"));
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
                _details.AddChild(DetailText(tracker?.Games.Count > 0 ? "No hints yet (or they load with the next read of the tracker)." : "No hints yet.", Muted));
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
                _details.AddChild(DetailText("There are no unfound hints right now.", Muted));
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
                lines.Add(Colored(HintClassLabel(h.Classification), dim ? Muted : HintClassColor(h.Classification)) + "  " +
                          Colored(text, dim ? Muted : Colors.White) + (state == "notfound" ? "" : Colored($"  ({state})", Muted)));
            }
            var list = Rich();
            list.SelectionEnabled = true;
            list.Text = string.Join("\n", lines);
            _details.AddChild(list);
            var copy = new HFlowContainer();
            copy.AddChild(DetailButton("Copy all", "Copy these hints as text", () =>
            {
                DisplayServer.ClipboardSet(string.Join("\n", plain));
                _toast($"Copied {plain.Count} hint{(plain.Count == 1 ? "" : "s")}", Colors.Gray);
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
            "critical" => Colors.Tomato,
            "progression" => Colors.Gold,
            "qol" => Colors.CornflowerBlue,
            "trash" => Colors.Gray,
            _ => Colors.LightGray
        };

        /// <summary>Rich text in the same font as the labels around it.</summary>
        private RichTextLabel Rich()
        {
            var rtl = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var font = GetThemeFont("font", "Label");
            if (font != null) rtl.AddThemeFontOverride("normal_font", font);
            return rtl;
        }

        private static Button DetailButton(string text, string tooltip, Action onPressed, bool enabled = true)
        {
            var b = new Button { Text = text, TooltipText = tooltip ?? "", Disabled = !enabled, FocusMode = FocusModeEnum.None };
            b.Pressed += () => onPressed();
            return b;
        }

        private static Label DetailHeading(string text)
        {
            var label = new Label { Text = text };
            label.AddThemeColorOverride("font_color", ThemeColors.Accent.Lightened(0.2f));
            return label;
        }

        private static Label DetailText(string text, Color color)
        {
            var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            label.AddThemeColorOverride("font_color", color);
            return label;
        }
    }
}
