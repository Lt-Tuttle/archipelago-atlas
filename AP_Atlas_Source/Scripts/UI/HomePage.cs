using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Home: where Atlas opens. A getting-started checklist with ticks and buttons, the user's multiworlds with a one-click
    /// connect, a card per tool and for what sets Atlas apart, a tip, links to Archipelago and the community's tools, and
    /// the footer. Everything here ships with Atlas; nothing loads online. It reads Atlas's state again whenever it shows
    /// (<see cref="OnShown"/>) and after a connect (<see cref="Refresh"/>).
    /// </summary>
    public sealed partial class HomePage : VBoxContainer
    {
        /// <summary>What Home reads, and what its buttons do, from the window.</summary>
        public sealed class Hooks
        {
            public Func<bool> EngineReady { get; init; } = () => false;
            public Func<IReadOnlyList<MultiworldProfile>> Profiles { get; init; } = () => Array.Empty<MultiworldProfile>();
            public Func<bool> AnyPackInstalled { get; init; } = () => false;
            /// <summary>Whether a multiworld's slot is connected (or connecting) right now.</summary>
            public Func<string, string, bool> IsSlotLive { get; init; } = (_, _) => false;
            public Func<int> ContentFontSize { get; init; } = () => 14;
            public Action SetUpEngine { get; init; } = () => { };
            public Action AddMultiworld { get; init; } = () => { };
            public Action<Tool> ShowTool { get; init; } = _ => { };
            /// <summary>Connects every slot of a multiworld that isn't connected yet.</summary>
            public Action<MultiworldProfile> Connect { get; init; } = _ => { };
            public Action OpenCheeseSettings { get; init; } = () => { };
            public Action ShowAbout { get; init; } = () => { };
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

        private sealed class Step
        {
            public Step(string id, Func<bool> done, TextureRect mark, Button button)
            {
                Id = id;
                Done = done;
                Mark = mark;
                Button = button;
            }

            public string Id { get; }
            public Func<bool> Done { get; }
            public TextureRect Mark { get; }
            public Button Button { get; }
            public bool LastDone { get; set; }
        }

        private readonly Func<string, string> _tr;
        private readonly Hooks _hooks;
        private readonly List<Step> _steps = new();
        private readonly VBoxContainer _recents = new();
        private readonly Dictionary<string, Button> _connectButtons = new();
        private readonly Dictionary<Tool, Button> _toolCards = new();
        private readonly Label _tip;
        private int _tipIndex = -1;

        public HomePage(Func<string, string> tr, Hooks hooks)
        {
            _tr = tr;
            _hooks = hooks;
            Name = "Home";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(scroll);
            var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(side, 24);
            scroll.AddChild(margin);
            var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            body.AddThemeConstantOverride("separation", 22);
            margin.AddChild(body);

            // The header: the icon until the wordmark is chosen, the name and what Atlas is.
            var header = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            header.AddThemeConstantOverride("separation", 18);
            header.AddChild(new TextureRect
            {
                Texture = GD.Load<Texture2D>("res://Assets/Graphics/icon.png"),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                CustomMinimumSize = new Vector2(96, 96)
            });
            var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            titles.AddThemeConstantOverride("separation", 4);
            var title = new Label { Text = _tr("The Archipelago Atlas") };
            title.SetMeta("font_size_ratio", 2.0f);
            title.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Accent.Lightened(0.2f));
            titles.AddChild(title);
            titles.AddChild(Text(_tr("Maps, logic, hints and the trackers your group uses, in one window."), Colors.LightGray));
            header.AddChild(titles);
            body.AddChild(header);

            // Getting started: each step reads Atlas's state and has the button that does it.
            body.AddChild(Heading(_tr("Getting started")));
            var steps = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            steps.AddThemeConstantOverride("separation", 8);
            body.AddChild(steps);
            AddStep(steps, "engine", "Set up the Atlas Engine", "Maps, logic and hints need it: Archipelago itself, in Atlas's own folder. About 55 MB to download, no installer.",
                "Set up…", hooks.EngineReady, hooks.SetUpEngine);
            AddStep(steps, "multiworld", "Add a multiworld", "Its server, its password and the slots you play.",
                "Add…", () => hooks.Profiles().Count > 0, hooks.AddMultiworld);
            AddStep(steps, "connect", "Connect a slot", "From then on Atlas follows the room: items, checks, hints and chat.",
                "Multiworlds", () => hooks.Profiles().Any(p => p.SavedStats.Count > 0 || p.Slots.Any(slot => hooks.IsSlotLive(p.Id, slot))), () => hooks.ShowTool(Tool.Connections));
            AddStep(steps, "pack", "Install a map pack", "A PopTracker pack puts your checks on the game's map. The Pack Doctor looks it over first.",
                "Map Packs", hooks.AnyPackInstalled, () => hooks.ShowTool(Tool.MapPacks));
            AddStep(steps, "cheese", "Link Cheese Tracker, if your multiworld uses it", "Your async multiworld's shared tracker, kept up to date from Atlas's logic.",
                "Cheese Tracker", () => hooks.Profiles().Any(p => !string.IsNullOrWhiteSpace(p.CheeseTrackerUrl)), hooks.OpenCheeseSettings);

            // The user's multiworlds, the most recently played first.
            body.AddChild(Heading(_tr("Your multiworlds")));
            _recents.AddThemeConstantOverride("separation", 6);
            _recents.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            body.AddChild(_recents);

            // The tools.
            body.AddChild(Heading(_tr("Tools")));
            var tools = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            tools.AddThemeConstantOverride("h_separation", 10);
            tools.AddThemeConstantOverride("v_separation", 10);
            foreach (var tool in Tool.All)
            {
                if (tool == Tool.Home) continue;
                tools.AddChild(ToolCard(tool));
            }
            body.AddChild(tools);

            // What sets Atlas apart.
            body.AddChild(Heading(_tr("What sets Atlas apart")));
            var features = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            features.AddThemeConstantOverride("h_separation", 10);
            features.AddThemeConstantOverride("v_separation", 10);
            features.AddChild(FeatureCard("Asks first", "Nothing outside Atlas's own folder, and nothing new online, without your yes. Settings → Privacy & permissions lists every answer, and takes any back."));
            features.AddChild(FeatureCard("Easy on the servers", "Polite, capped and backed-off traffic to archipelago.gg and every other site, and a closed room is never kept busy."));
            features.AddChild(FeatureCard("Logic on your PC", "The Atlas Engine runs Archipelago's own logic here, so the map and the Logic Tracker know what you can reach, with no extra load on the server."));
            features.AddChild(FeatureCard("Yours to shape", "Every action is a command: the menus, Ctrl+Shift+P, and keys you can change. Hide any part of the window, or press F9 for the content alone."));
            body.AddChild(features);

            // A tip.
            var tipRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            tipRow.AddThemeConstantOverride("separation", 12);
            _tip = Text("", Colors.LightGray);
            tipRow.AddChild(_tip);
            var next = new Button { Text = _tr("Next tip"), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            next.Pressed += NextTip;
            tipRow.AddChild(next);
            var tipCard = Card(out var tipBox);
            tipBox.AddChild(tipRow);
            body.AddChild(tipCard);

            // Links: each opens in the browser when clicked; Atlas fetches none of them.
            body.AddChild(Heading(_tr("Links")));
            foreach (var group in LinkList.Select(l => l.Group).Distinct())
            {
                var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 12);
                var name = new Label { Text = _tr(group), CustomMinimumSize = new Vector2(150, 0), SizeFlagsVertical = SizeFlags.ShrinkBegin };
                name.AddThemeColorOverride("font_color", Colors.LightGray);
                row.AddChild(name);
                var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                flow.AddThemeConstantOverride("h_separation", 8);
                flow.AddThemeConstantOverride("v_separation", 6);
                foreach (var (_, linkTitle, url) in LinkList.Where(l => l.Group == group))
                {
                    var link = new Button { Text = _tr(linkTitle), Icon = LucideTextures.Get("external-link", Colors.LightGray, 1.1f), IconAlignment = HorizontalAlignment.Right, TooltipText = url };
                    link.AddThemeConstantOverride("h_separation", 6);
                    string target = url;
                    link.Pressed += () => _hooks.OpenWeb(target);
                    flow.AddChild(link);
                }
                row.AddChild(flow);
                body.AddChild(row);
            }

            // The footer.
            var footer = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            footer.AddThemeConstantOverride("separation", 12);
            footer.AddChild(Text(_tr("The Archipelago Atlas {0} · MIT licence · Unofficial: not affiliated with or endorsed by Archipelago.").Replace("{0}", AP_Atlas.Core.AtlasVersion.Display), Colors.Gray));
            var about = new Button { Text = _tr("About"), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            about.Pressed += () => _hooks.ShowAbout();
            footer.AddChild(about);
            var repo = new Button { Text = _tr("Atlas on GitHub"), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            repo.Pressed += () => _hooks.OpenWeb(AP_Atlas.Core.AtlasVersion.RepoUrl);
            footer.AddChild(repo);
            body.AddChild(footer);
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
            "map-packs" => "PopTracker packs for your games, looked over by the Pack Doctor.",
            "settings" => "Every setting, searchable; every key, yours to change.",
            _ => ""
        };

        // --- what tests read and press -----------------------------------------------------------------------------------

        public IEnumerable<string> StepIds => _steps.Select(s => s.Id);

        /// <summary>Whether a step showed as done the last time Home read Atlas's state.</summary>
        public bool StepDone(string id) => StepOf(id).LastDone;

        public Button StepButtonOf(string id) => StepOf(id).Button;

        /// <summary>The multiworlds shown, most recently played first.</summary>
        public IReadOnlyList<string> RecentProfileIds { get; private set; } = Array.Empty<string>();

        public Button ConnectButtonOf(string profileId) => _connectButtons[profileId];

        public Button ToolCardOf(Tool tool) => _toolCards[tool];

        public int TipIndex => _tipIndex;

        public string TipText => _tip.Text;

        public IReadOnlyList<(string Title, string Url)> Links => LinkList.Select(l => (l.Title, l.Url)).ToList();

        private Step StepOf(string id) => _steps.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException($"No getting-started step is {id}.");

        // --- drawing -------------------------------------------------------------------------------------------------------

        /// <summary>Reads Atlas's state again (the steps, the multiworlds) and shows the next tip.</summary>
        public void OnShown()
        {
            Refresh();
            NextTip();
        }

        /// <summary>Reads Atlas's state again: the steps' ticks and the multiworlds.</summary>
        public void Refresh()
        {
            foreach (var step in _steps)
            {
                step.LastDone = step.Done();
                step.Mark.Texture = LucideTextures.Get(step.LastDone ? "circle-check" : "circle", step.LastDone ? AP_Atlas.Core.ThemeColors.Accent.Lightened(0.3f) : Colors.Gray);
                step.Mark.TooltipText = step.LastDone ? _tr("Done") : _tr("Not yet");
            }
            RefreshRecents();
            MainTrackerWindow.SetFontSizeRecursive(this, _hooks.ContentFontSize());
        }

        public void NextTip()
        {
            _tipIndex = (_tipIndex + 1) % Tips.Length;
            _tip.Text = _tr("Tip: {0}").Replace("{0}", _tr(Tips[_tipIndex]));
        }

        private void AddStep(VBoxContainer into, string id, string title, string description, string buttonText, Func<bool> done, Action act)
        {
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 12);
            var mark = new TextureRect
            {
                CustomMinimumSize = new Vector2(22, 22),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            row.AddChild(mark);
            var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            text.AddThemeConstantOverride("separation", 2);
            text.AddChild(new Label { Text = _tr(title) });
            text.AddChild(Small(_tr(description)));
            row.AddChild(text);
            var button = new Button { Text = _tr(buttonText), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            button.Pressed += act;
            row.AddChild(button);
            into.AddChild(row);
            _steps.Add(new Step(id, done, mark, button));
        }

        private void RefreshRecents()
        {
            Clear(_recents);
            _connectButtons.Clear();
            var profiles = _hooks.Profiles();
            var ordered = profiles.Select((profile, index) => (profile, index))
                .OrderByDescending(p => p.profile.SavedStats.Count == 0 ? DateTime.MinValue : p.profile.SavedStats.Values.Max(s => s.LastUpdated))
                .ThenBy(p => p.index)
                .Select(p => p.profile)
                .Take(5)
                .ToList();
            RecentProfileIds = ordered.Select(p => p.Id).ToList();
            if (ordered.Count == 0)
            {
                var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 12);
                row.AddChild(Small(_tr("No multiworlds yet.")));
                var add = new Button { Text = _tr("Add a multiworld…") };
                add.Pressed += () => _hooks.AddMultiworld();
                row.AddChild(add);
                _recents.AddChild(row);
                return;
            }
            foreach (var profile in ordered)
            {
                var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 12);
                row.AddChild(new TextureRect
                {
                    Texture = GD.Load<Texture2D>("res://Assets/Graphics/UI/UnknownGame.png"), // per-game art comes later (R6)
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    CustomMinimumSize = new Vector2(40, 40),
                    SizeFlagsVertical = SizeFlags.ShrinkCenter
                });
                var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
                text.AddThemeConstantOverride("separation", 2);
                text.AddChild(new Label { Text = profile.Name, ClipText = true });
                var games = profile.SavedStats.Values.Select(s => s.GameName).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().Take(3).ToList();
                string line = _tr("Slots: {0}").Replace("{0}", profile.Slots.Count.ToString()) + "  ·  " + profile.ServerUrl + (games.Count > 0 ? "  ·  " + string.Join(", ", games) : "");
                var detail = Small(line);
                detail.ClipText = true;
                detail.AutowrapMode = TextServer.AutowrapMode.Off;
                text.AddChild(detail);
                row.AddChild(text);
                bool allLive = profile.Slots.Count > 0 && profile.Slots.All(slot => _hooks.IsSlotLive(profile.Id, slot));
                var connect = new Button { Text = allLive ? _tr("Connected") : _tr("Connect"), Disabled = allLive || profile.Slots.Count == 0, SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = _tr("Connects every slot of this multiworld.") };
                var target = profile;
                connect.Pressed += () => _hooks.Connect(target);
                row.AddChild(connect);
                _connectButtons[profile.Id] = connect;
                _recents.AddChild(row);
            }
        }

        private Control ToolCard(Tool tool)
        {
            var card = Card(out var box);
            var button = new Button { Text = _tr(tool.Title), Icon = LucideTextures.Get(tool.Icon, Colors.White, 1.2f), Alignment = HorizontalAlignment.Left, Flat = true };
            button.AddThemeConstantOverride("h_separation", 8);
            button.Pressed += () => _hooks.ShowTool(tool);
            box.AddChild(button);
            box.AddChild(Small(_tr(Blurb(tool))));
            _toolCards[tool] = button;
            return card;
        }

        private Control FeatureCard(string title, string text)
        {
            var card = Card(out var box);
            var name = new Label { Text = _tr(title) };
            name.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Accent.Lightened(0.3f));
            box.AddChild(name);
            box.AddChild(Small(_tr(text)));
            return card;
        }

        private static PanelContainer Card(out VBoxContainer box)
        {
            var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color("#1F1F25"),
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

        private static Label Heading(string text)
        {
            var label = new Label { Text = text };
            label.SetMeta("font_size_ratio", 1.25f);
            label.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Accent.Lightened(0.2f));
            return label;
        }

        private static Label Text(string text, Color color)
        {
            var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            label.AddThemeColorOverride("font_color", color);
            return label;
        }

        private static Label Small(string text)
        {
            var label = Text(text, Colors.LightGray);
            label.SetMeta("font_size_ratio", 0.9f);
            return label;
        }
    }
}
