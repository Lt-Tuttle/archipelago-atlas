#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Home: where Atlas opens, as a dashboard. The multiworlds you play fill the width with their live state and a
    /// one-click connect (a first start shows one "Set up Atlas" block there instead), the tools as a row of buttons
    /// under them, and a narrow column beside: the setup's one line with Resume, a tip, what's new, and the links.
    /// Everything here ships with Atlas; nothing loads online. It reads Atlas's state again whenever it shows
    /// (<see cref="OnShown"/>) and after a connect (<see cref="Refresh"/>).
    /// </summary>
    public sealed partial class HomePage : VBoxContainer
    {
        /// <summary>The right column's width, and the page width below which it goes under the multiworlds instead of beside them.</summary>
        public const int ColumnWidth = 260, TwoColumnsFrom = 780;

        /// <summary>What Home reads, and what its buttons do, from the window.</summary>
        public sealed class Hooks
        {
            public Func<bool> EngineReady { get; init; } = () => false;
            public Func<IReadOnlyList<MultiworldProfile>> Profiles { get; init; } = () => Array.Empty<MultiworldProfile>();
            public Func<bool> AnyPackInstalled { get; init; } = () => false;
            /// <summary>Whether a multiworld's slot is connected (or connecting) right now.</summary>
            public Func<string, string, bool> IsSlotLive { get; init; } = (_, _) => false;
            public Func<int> ContentFontSize { get; init; } = () => 14;
            /// <summary>The setup steps Home's line counts (shared with the setup window).</summary>
            public SetupSteps Steps { get; init; } = new SetupSteps(new SetupSteps.Hooks());
            /// <summary>Opens the setup window: at the first step left (true), or from its Welcome page.</summary>
            public Action<bool> OpenSetup { get; init; } = _ => { };
            /// <summary>Takes the user to the Multiworlds page with a new multiworld selected (the first start's second button).</summary>
            public Action AddMultiworld { get; init; } = () => { };
            /// <summary>New…: a new multiworld in a small dialog here.</summary>
            public Action QuickAddMultiworld { get; init; } = () => { };
            /// <summary>Paste a room link…: the Multiworlds page with a new multiworld and its room link box ready.</summary>
            public Action AddFromRoomLink { get; init; } = () => { };
            /// <summary>Open: the Multiworlds page with that multiworld selected.</summary>
            public Action<MultiworldProfile> OpenProfile { get; init; } = _ => { };
            public Action<Tool> ShowTool { get; init; } = _ => { };
            /// <summary>Connects every slot of a multiworld that isn't connected yet.</summary>
            public Action<MultiworldProfile> Connect { get; init; } = _ => { };
            public Action ShowAbout { get; init; } = () => { };
            /// <summary>The newest version's headline changes, one line each (from the changelog built into Atlas).</summary>
            public Func<IReadOnlyList<string>> WhatsNew { get; init; } = () => Array.Empty<string>();
            public Action ShowWhatsNew { get; init; } = () => { };
            /// <summary>Opens a link in the browser (https only), when the user clicks it.</summary>
            public Action<string> OpenWeb { get; init; } = _ => { };
        }

        /// <summary>The tips, one at a time: the next each time Home shows, or on Next tip.</summary>
        public static readonly string[] Tips =
        {
            "Ctrl+Shift+P opens the command palette: type a few letters of anything in the menus.",
            "F9 is focus mode: the content alone, until you press it again.",
            "Ctrl+Tab and Ctrl+Shift+Tab go through your connected slots.",
            "Click a name anywhere (an item, a location, a player) and the Properties panel tells you everything about it.",
            "Race mode hides what a race shouldn't show. It follows the room by itself; Multiworld → Race Mode… changes that.",
            "Every key can be changed: Settings → Keyboard, press the key's button, then the key you want.",
            "The Pack Doctor, on the Map Packs page, looks a pack over and fixes what it can before you use it.",
            "Each panel's … button changes its font size; Settings → Appearance sets them all.",
            "Atlas never looks outside its own folder, or goes online for something new, without asking. Settings → Privacy & permissions lists every answer.",
            "A dropped connection reconnects by itself: a few tries over about 20 minutes, then it stops, so a closed room is never kept busy.",
        };

        /// <summary>Where the links go: https only, opened in the browser when clicked, never fetched by Atlas.</summary>
        public static readonly (string Group, string Title, string Url)[] LinkList =
        {
            ("Archipelago", "Supported games", "https://archipelago.gg/games"),
            ("Archipelago", "Setup guides", "https://archipelago.gg/tutorial/"),
            ("Archipelago", "Generate a game", "https://archipelago.gg/generate"),
            ("Archipelago", "Host a game", "https://archipelago.gg/uploads"),
            ("Archipelago", "FAQ", "https://archipelago.gg/faq/en/"),
            ("Archipelago", "Archipelago Discord", "https://discord.gg/8Z65BR2"),
            ("Archipelago", "Archipelago on GitHub", "https://github.com/ArchipelagoMW/Archipelago"),
            ("Community tools", "Cheese Tracker", AP_Atlas.Core.CheeseTracker.CheeseClient.DefaultInstance),
            ("Community tools", "spheretracker.de", "https://spheretracker.de"),
            ("Community tools", "Archipelago Alerts", "https://github.com/wrjones104/ap-tracker"),
            ("Community tools", "Archipelago Games Library", "https://mk-404.github.io/Archipelago-Games-Library/"),
            ("Community tools", "PopTracker", "https://github.com/black-sliver/PopTracker"),
            ("Community tools", "Universal Tracker", "https://github.com/FarisTheAncient/Archipelago/releases"),
        };

        private readonly Func<string, string> _tr;
        private readonly Hooks _hooks;
        private readonly BoxContainer _columns = new();
        private readonly Control _rightColumn;
        private readonly VBoxContainer _recents = new();
        private readonly Dictionary<string, Button> _connectButtons = new();
        private readonly Dictionary<string, Button> _openButtons = new();
        private readonly Dictionary<string, string> _rowLines = new();
        private readonly Dictionary<Tool, Button> _toolCards = new();
        private readonly Label _tip;
        private readonly Label _setupLine;
        private readonly Label _setupProgress;
        private readonly Button _resume;
        private readonly Button _reopenSetup;
        private readonly VBoxContainer _whatsNew = new();
        private readonly Button _whatsNewButton;
        private Button? _addMultiworldButton;
        private Button? _setUpButton;
        private readonly TextureRect _wordmark;
        private int _tipIndex = -1;
        private bool _twoColumns = true;

        public HomePage(Func<string, string> tr, Hooks hooks)
        {
            _tr = tr;
            _hooks = hooks;
            Name = "Home";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            // The page never asks the window for width (ShowNever: no sideways scrolling; the columns fold instead).
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(scroll);
            var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(side, 20);
            scroll.AddChild(margin);
            var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            body.AddThemeConstantOverride("separation", 16);
            margin.AddChild(body);

            // The header: the icon and the wordmark, the tagline beside them.
            var header = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            header.AddThemeConstantOverride("separation", 14);
            header.AddChild(new TextureRect
            {
                Texture = GD.Load<Texture2D>("res://Assets/Graphics/icon.png"),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                CustomMinimumSize = new Vector2(52, 52),
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            });
            _wordmark = Wordmark.Make(48, _tr("The Archipelago Atlas"));
            _wordmark.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            header.AddChild(_wordmark);
            header.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
            var tagline = Kit.Text(_tr("Your multiworld, charted."), AP_Atlas.Core.ThemeColors.TextMuted);
            tagline.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            tagline.HorizontalAlignment = HorizontalAlignment.Right;
            header.AddChild(tagline);
            body.AddChild(header);

            // Two columns: the multiworlds and the tools; the setup, a tip, what's new and the links beside them.
            _columns.Vertical = false;
            _columns.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _columns.AddThemeConstantOverride("separation", 14);
            body.AddChild(_columns);
            var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            left.AddThemeConstantOverride("separation", 14);
            _columns.AddChild(left);
            var right = new VBoxContainer { CustomMinimumSize = new Vector2(ColumnWidth, 0), SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
            right.AddThemeConstantOverride("separation", 10);
            _columns.AddChild(right);
            _rightColumn = right;
            scroll.Resized += () => Ui.DeferQuiet(this, () => Reflow(scroll.Size.X - 40));

            // The multiworlds card: most recently played first, each with its live state, Open and Connect.
            var multiworlds = Card(out var multiworldsBox);
            var multiworldsHeader = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            multiworldsHeader.AddThemeConstantOverride("separation", 8);
            var multiworldsTitle = Kit.Heading(_tr("Your multiworlds"), 1.15f);
            multiworldsTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            multiworldsTitle.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            multiworldsHeader.AddChild(multiworldsTitle);
            var newButton = Kit.Button(_tr("New…"), _tr("A new multiworld: its name, server and slots, in a small window here."), () => _hooks.QuickAddMultiworld(), small: true);
            newButton.Name = "NewMultiworld";
            multiworldsHeader.AddChild(newButton);
            var roomButton = Kit.Button(_tr("Paste a room link…"), _tr("A new multiworld from the room link the host shared: Atlas fills in the server and the players from the room's status page, with your permission."), () => _hooks.AddFromRoomLink(), small: true);
            roomButton.Name = "RoomLinkMultiworld";
            multiworldsHeader.AddChild(roomButton);
            multiworldsBox.AddChild(multiworldsHeader);
            _recents.AddThemeConstantOverride("separation", 4);
            _recents.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            multiworldsBox.AddChild(_recents);
            left.AddChild(multiworlds);

            // The tools, as a row of buttons.
            var toolsCard = Card(out var toolsBox);
            toolsBox.AddChild(Kit.Heading(_tr("Tools"), 1.15f));
            var tools = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            tools.AddThemeConstantOverride("h_separation", 6);
            tools.AddThemeConstantOverride("v_separation", 6);
            foreach (var tool in Tool.All)
            {
                if (tool == Tool.Home) continue;
                var button = new Button { Text = _tr(tool.Title), Icon = LucideTextures.Get(tool.Icon, AP_Atlas.Core.ThemeColors.Text, 1.1f), ThemeTypeVariation = Kit.QuietButton, TooltipText = _tr(Blurb(tool)) };
                button.AddThemeConstantOverride("h_separation", 6);
                var target = tool;
                button.Pressed += () => _hooks.ShowTool(target);
                tools.AddChild(button);
                _toolCards[tool] = button;
            }
            toolsBox.AddChild(tools);
            left.AddChild(toolsCard);

            // The setup card: one line about the steps, and Resume; the engine's progress while it installs.
            var setup = Card(out var setupBox);
            setupBox.AddChild(CardTitle(_tr("Setup")));
            _setupLine = Small("");
            _setupLine.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _setupLine.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            setupBox.AddChild(_setupLine);
            _setupProgress = Small("");
            _setupProgress.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _setupProgress.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _setupProgress.Visible = false;
            setupBox.AddChild(_setupProgress);
            var setupButtons = new HBoxContainer();
            setupButtons.AddThemeConstantOverride("separation", 6);
            _resume = Kit.Button(_tr("Resume"), _tr("Opens the setup window at the first step left."), () => _hooks.OpenSetup(true), small: true);
            _resume.Name = "ResumeSetup";
            setupButtons.AddChild(_resume);
            _reopenSetup = Kit.Button(_tr("Open setup"), _tr("The setup window from its first page, to change anything."), () => _hooks.OpenSetup(false), flat: true, small: true);
            setupButtons.AddChild(_reopenSetup);
            setupBox.AddChild(setupButtons);
            right.AddChild(setup);

            // A tip.
            var tipCard = Card(out var tipBox);
            tipBox.AddChild(CardTitle(_tr("Tip")));
            _tip = Small("");
            _tip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _tip.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            tipBox.AddChild(_tip);
            var next = Kit.Button(_tr("Next tip"), null, NextTip, flat: true, small: true);
            next.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            tipBox.AddChild(next);
            right.AddChild(tipCard);

            // What's new: the newest version's headline changes, from the changelog built into Atlas.
            var newsCard = Card(out var newsBox);
            newsBox.AddChild(CardTitle(_tr("What's new")));
            _whatsNew.AddThemeConstantOverride("separation", 2);
            newsBox.AddChild(_whatsNew);
            _whatsNewButton = Kit.Button(_tr("All changes…"), null, () => _hooks.ShowWhatsNew(), flat: true, small: true);
            _whatsNewButton.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            newsBox.AddChild(_whatsNewButton);
            right.AddChild(newsCard);

            // Links: each opens in the browser when clicked; Atlas fetches none of them.
            var linksCard = Card(out var linksBox);
            linksBox.AddChild(CardTitle(_tr("Links")));
            foreach (var group in LinkList.Select(l => l.Group).Distinct())
            {
                linksBox.AddChild(Small(_tr(group)));
                var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                flow.AddThemeConstantOverride("h_separation", 4);
                flow.AddThemeConstantOverride("v_separation", 2);
                foreach (var (_, linkTitle, url) in LinkList.Where(l => l.Group == group))
                {
                    var link = new Button { Text = _tr(linkTitle), Icon = LucideTextures.Get("external-link", AP_Atlas.Core.ThemeColors.TextMuted, 1.0f), IconAlignment = HorizontalAlignment.Right, TooltipText = url, ThemeTypeVariation = Kit.QuietButton };
                    link.AddThemeConstantOverride("h_separation", 4);
                    link.SetMeta("font_size_ratio", 0.9f);
                    string target = url;
                    link.Pressed += () => _hooks.OpenWeb(target);
                    flow.AddChild(link);
                }
                linksBox.AddChild(flow);
            }
            right.AddChild(linksCard);

            // The footer.
            var footer = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            footer.AddThemeConstantOverride("separation", 12);
            var about = Kit.Text(_tr("The Archipelago Atlas {0} · MIT licence · Unofficial: not affiliated with or endorsed by Archipelago.").Replace("{0}", AP_Atlas.Core.AtlasVersion.Display), AP_Atlas.Core.ThemeColors.TextSubtle);
            about.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            about.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            footer.AddChild(about);
            var aboutButton = Kit.Button(_tr("About"), null, () => _hooks.ShowAbout(), flat: true, small: true);
            aboutButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            footer.AddChild(aboutButton);
            var repo = Kit.Button(_tr("Atlas on GitHub"), null, () => _hooks.OpenWeb(AP_Atlas.Core.AtlasVersion.RepoUrl), flat: true, small: true);
            repo.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            footer.AddChild(repo);
            body.AddChild(footer);
        }

        public override void _EnterTree() => AP_Atlas.Core.EngineSetup.AtlasEngine.SetupProgressed += OnSetupProgress;

        public override void _ExitTree() => AP_Atlas.Core.EngineSetup.AtlasEngine.SetupProgressed -= OnSetupProgress;

        /// <summary>The engine's setup, running anywhere (the setup window, the panel): its step on the setup card. Any thread.</summary>
        private void OnSetupProgress(AP_Atlas.Core.EngineSetup.AtlasEngine.SetupProgress p) => Ui.Defer(this, () =>
        {
            if (p.Text != null) _setupProgress.Text = _tr("Setting up the Atlas Engine: {0}").Replace("{0}", p.Text);
            else if (p.Fraction >= 0) _setupProgress.Text = _tr("Setting up the Atlas Engine… {0}%").Replace("{0}", ((int)Math.Round(p.Fraction * 100)).ToString());
            _setupProgress.Visible = true;
        });

        /// <summary>The columns side by side when the page is wide enough, else one under the other; set only when that changes.</summary>
        private void Reflow(float width)
        {
            if (width <= 0) return;
            bool two = width >= TwoColumnsFrom;
            if (two == _twoColumns) return;
            _twoColumns = two;
            _columns.Vertical = !two;
            _rightColumn.SizeFlagsHorizontal = two ? SizeFlags.ShrinkBegin : SizeFlags.ExpandFill;
        }

        /// <summary>A tool's card line, from its id; "" for a tool nobody has written one for (the UI test fails on it).</summary>
        public static string Blurb(Tool tool) => tool.Id switch
        {
            "map-tracker" => "Your checks on the game's map, coloured by what's in logic.",
            "key-items" => "The progression items you've received, at a glance.",
            "logic-tracker" => "Every location, and whether it's in logic right now.",
            "item-history" => "Everything you've received, in order, with who found it.",
            "hints" => "Hints for you and from you, in one list.",
            "cheese-tracker" => "Your async multiworld's shared tracker, kept up to date from Atlas's logic.",
            "sphere-tracker" => "The host's spheretracker.de room: the multiworld, sphere by sphere.",
            "connections" => "Your servers, passwords and slots.",
            "games" => "Every game, set up in one place: its apworld, a map pack, your YAML and its files.",
            "map-packs" => "PopTracker packs for your games, looked over by the Pack Doctor.",
            "settings" => "Every setting, searchable; every key, yours to change.",
            _ => ""
        };

        // --- what tests read and press -----------------------------------------------------------------------------------

        /// <summary>The header's wordmark.</summary>
        public TextureRect WordmarkRect => _wordmark;

        /// <summary>Redraws the wordmark for a new accent or theme.</summary>
        public void RefreshWordmark() => Wordmark.Refresh(_wordmark);

        /// <summary>The setup card's line ("Setup 4 of 7 done. Next: …", or "You're set up.").</summary>
        public string SetupLine => _setupLine.Text;

        public Button ResumeButton => _resume;

        /// <summary>Whether the columns sit side by side (false: the right column is under the multiworlds).</summary>
        public bool TwoColumns => _twoColumns;

        /// <summary>The right column's right edge, in the window (for the fit check).</summary>
        public float RightColumnRight => _rightColumn.GlobalPosition.X + _rightColumn.Size.X;

        /// <summary>The first start's "Set up Atlas…" button (null while multiworlds exist).</summary>
        public Button? SetUpButton => _setUpButton;

        /// <summary>The first start's "Add a multiworld…" button (null while multiworlds exist).</summary>
        public Button? AddMultiworldButton => _addMultiworldButton;

        /// <summary>The multiworlds shown, most recently played first.</summary>
        public IReadOnlyList<string> RecentProfileIds { get; private set; } = Array.Empty<string>();

        public Button ConnectButtonOf(string profileId) => _connectButtons[profileId];

        public Button OpenButtonOf(string profileId) => _openButtons[profileId];

        /// <summary>A multiworld's detail line (its server, slots and games).</summary>
        public string RowLineOf(string profileId) => _rowLines[profileId];

        public Button ToolCardOf(Tool tool) => _toolCards[tool];

        public int TipIndex => _tipIndex;

        public string TipText => _tip.Text;

        public IReadOnlyList<(string Title, string Url)> Links => LinkList.Select(l => (l.Title, l.Url)).ToList();

        /// <summary>The lines the What's new card shows.</summary>
        public IReadOnlyList<string> WhatsNewLines { get; private set; } = Array.Empty<string>();

        public Button WhatsNewButton => _whatsNewButton;

        // --- drawing -------------------------------------------------------------------------------------------------------

        /// <summary>Reads Atlas's state again (the setup, the multiworlds) and shows the next tip.</summary>
        public void OnShown()
        {
            Refresh();
            NextTip();
        }

        /// <summary>How many times the page re-read Atlas's state (for tests).</summary>
        public int RefreshCount { get; private set; }

        /// <summary>Reads Atlas's state again: the setup's line and the multiworlds.</summary>
        public void Refresh()
        {
            RefreshCount++;
            var steps = _hooks.Steps;
            _setupLine.Text = steps.Line(_tr);
            _setupLine.AddThemeColorOverride("font_color", steps.AllSettled ? AP_Atlas.Core.ThemeColors.Success : AP_Atlas.Core.ThemeColors.TextMuted);
            _resume.Visible = !steps.AllSettled;
            _reopenSetup.Visible = steps.AllSettled;
            if (!AP_Atlas.Core.EngineSetup.AtlasEngine.SetupRunning) _setupProgress.Visible = false;
            RefreshRecents();
            RefreshWhatsNew();
            MainTrackerWindow.SetFontSizeRecursive(this, _hooks.ContentFontSize());
        }

        private void RefreshWhatsNew()
        {
            var lines = _hooks.WhatsNew();
            if (lines.SequenceEqual(WhatsNewLines)) return;
            WhatsNewLines = lines.ToList();
            Clear(_whatsNew);
            foreach (string line in lines)
            {
                var item = Small("•  " + line);
                item.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                item.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                _whatsNew.AddChild(item);
            }
        }

        public void NextTip()
        {
            _tipIndex = (_tipIndex + 1) % Tips.Length;
            _tip.Text = _tr(Tips[_tipIndex]);
        }

        private void RefreshRecents()
        {
            Clear(_recents);
            _connectButtons.Clear();
            _openButtons.Clear();
            _rowLines.Clear();
            _addMultiworldButton = null;
            _setUpButton = null;
            var profiles = _hooks.Profiles();
            var ordered = profiles.Select((profile, index) => (profile, index))
                .OrderByDescending(p => p.profile.SavedStats.Count == 0 ? DateTime.MinValue : p.profile.SavedStats.Values.Max(s => s.LastUpdated))
                .ThenBy(p => p.index)
                .Select(p => p.profile)
                .ToList();
            RecentProfileIds = ordered.Take(8).Select(p => p.Id).ToList();
            if (ordered.Count == 0)
            {
                // A first start: nothing to play with yet, and the two ways to begin.
                var block = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                block.AddThemeConstantOverride("separation", 8);
                block.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
                var welcome = Kit.Text(_tr("Nothing to play with yet. Setting Atlas up takes a few pages: its logic engine, your multiworld and slots, the games' map packs."));
                welcome.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                welcome.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                block.AddChild(welcome);
                var buttons = new HBoxContainer();
                buttons.AddThemeConstantOverride("separation", 8);
                _setUpButton = Kit.Button(_tr("Set up Atlas…"), _tr("The setup window, page by page."), () => _hooks.OpenSetup(false));
                _setUpButton.ThemeTypeVariation = Kit.PrimaryButton;
                buttons.AddChild(_setUpButton);
                _addMultiworldButton = Kit.Button(_tr("Add a multiworld…"), _tr("Straight to the Multiworlds page, for a multiworld typed by hand."), () => _hooks.AddMultiworld());
                buttons.AddChild(_addMultiworldButton);
                block.AddChild(buttons);
                block.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
                _recents.AddChild(block);
                return;
            }
            foreach (var profile in ordered.Take(8))
            {
                var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 10);
                row.SetMeta("profile_row", profile.Id);
                int live = profile.Slots.Count(slot => _hooks.IsSlotLive(profile.Id, slot));
                var dot = Kit.Text("●", live > 0 ? AP_Atlas.Core.ThemeColors.Success : AP_Atlas.Core.ThemeColors.TextSubtle);
                dot.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                dot.SetMeta("font_size_ratio", 0.8f);
                row.AddChild(dot);
                var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
                text.AddThemeConstantOverride("separation", 1);
                text.AddChild(new Label { Text = profile.Name, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis });
                var games = profile.SavedStats.Values.Select(s => s.GameName).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().Take(3).ToList();
                int checksDone = profile.SavedStats.Values.Sum(s => s.CompleteCount), checksAll = profile.SavedStats.Values.Sum(s => s.TotalCount);
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(profile.ServerUrl)) parts.Add(profile.ServerUrl);
                parts.Add(_tr("Slots: {0}").Replace("{0}", profile.Slots.Count.ToString()));
                if (games.Count > 0) parts.Add(string.Join(", ", games));
                if (checksAll > 0) parts.Add(_tr("{0} of {1} checks").Replace("{0}", checksDone.ToString()).Replace("{1}", checksAll.ToString()));
                string line = string.Join("  ·  ", parts);
                _rowLines[profile.Id] = line;
                var detail = Small(line);
                detail.ClipText = true;
                detail.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                detail.AutowrapMode = TextServer.AutowrapMode.Off;
                text.AddChild(detail);
                row.AddChild(text);
                if (live > 0)
                {
                    var chip = new PanelContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
                    chip.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.AccentTint, CornerRadiusTopLeft = 9, CornerRadiusTopRight = 9, CornerRadiusBottomLeft = 9, CornerRadiusBottomRight = 9, ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 1, ContentMarginBottom = 1 });
                    var chipText = Kit.Text(live == 1 ? _tr("1 connected") : _tr("{0} connected").Replace("{0}", live.ToString()), AP_Atlas.Core.ThemeColors.AccentText(AP_Atlas.Core.ThemeColors.AccentTint));
                    chipText.SetMeta("font_size_ratio", 0.85f);
                    chip.AddChild(chipText);
                    row.AddChild(chip);
                }
                var target = profile;
                var open = Kit.Button(_tr("Open"), _tr("The Multiworlds page, with this one selected."), () => _hooks.OpenProfile(target), flat: true, small: true);
                open.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                row.AddChild(open);
                _openButtons[profile.Id] = open;
                bool allLive = profile.Slots.Count > 0 && profile.Slots.All(slot => _hooks.IsSlotLive(profile.Id, slot));
                var connect = Kit.Button(allLive ? _tr("Connected") : _tr("Connect"), _tr("Connects every slot of this multiworld."), () => _hooks.Connect(target), enabled: !allLive && profile.Slots.Count > 0, small: true);
                connect.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                row.AddChild(connect);
                _connectButtons[profile.Id] = connect;
                _recents.AddChild(row);
            }
            if (ordered.Count > 8)
            {
                var all = Kit.Button(_tr("All {0} multiworlds…").Replace("{0}", ordered.Count.ToString()), null, () => _hooks.ShowTool(Tool.Connections), flat: true, small: true);
                all.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
                _recents.AddChild(all);
            }
        }

        private Label CardTitle(string text)
        {
            var name = new Label { Text = text };
            name.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.AccentText(AP_Atlas.Core.ThemeColors.RowOdd));
            return name;
        }

        private static PanelContainer Card(out VBoxContainer box)
        {
            var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = AP_Atlas.Core.ThemeColors.RowOdd,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft = 12,
                ContentMarginRight = 12,
                ContentMarginTop = 10,
                ContentMarginBottom = 10
            });
            box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 6);
            panel.AddChild(box);
            return panel;
        }

        /// <summary>Takes a box's rows out now (a freed node stays a child until the frame ends, which would confuse anyone counting).</summary>
        private static void Clear(Node box)
        {
            foreach (Node child in box.GetChildren())
            {
                box.RemoveChild(child);
                child.QueueFree();
            }
        }

        private static Label Small(string text)
        {
            var label = Kit.Text(text, AP_Atlas.Core.ThemeColors.TextMuted);
            label.SetMeta("font_size_ratio", 0.9f);
            return label;
        }
    }
}
