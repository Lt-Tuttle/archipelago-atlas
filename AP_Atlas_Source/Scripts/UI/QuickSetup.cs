#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>A new multiworld as Home's quick dialog hands it back: its name, server and password, its slots, and each slot's YAML and game when a YAML named them.</summary>
    public sealed record NewMultiworldDraft(string Name, string Server, string Password, List<string> Slots, Dictionary<string, string> YamlBySlot, Dictionary<string, string> GameBySlot);

    /// <summary>
    /// Home's quick setup: each Getting started step opens a small dialog in place (no page jump): a new multiworld with
    /// its slots and YAMLs, connecting a slot, finding a map pack for a game of the user's slots, linking Cheese Tracker.
    /// Every dialog frees itself when closed and carries the meta "quick_setup" (its id) for tests.
    /// </summary>
    public static class QuickSetup
    {
        /// <summary>What the dialogs need from the window.</summary>
        public sealed class Hooks
        {
            public required Func<string, string> Tr { get; init; }
            public required Func<IReadOnlyList<MultiworldProfile>> Profiles { get; init; }
            public required Func<string, string, bool> IsSlotLive { get; init; }
            public required Action<MultiworldProfile, string> ConnectSlot { get; init; }
            public required Action<MultiworldProfile> ConnectAll { get; init; }
            /// <summary>The games of the user's slots, first in the pack dialog's choice.</summary>
            public required Func<IReadOnlyList<string>> PlayedGames { get; init; }
            /// <summary>Shows the Map Packs page and searches GitHub for a game's packs, once.</summary>
            public required Action<string> FindPack { get; init; }
            public required Action<Action<string>> PickYaml { get; init; }
            /// <summary>Keeps a YAML in Atlas's YAML folder: the kept copy's path and the players it names; null when it can't be kept (the hook says why).</summary>
            public required Func<string, (string Kept, List<(string Name, List<string> Games)> Players)?> KeepYaml { get; init; }
            public required Action<NewMultiworldDraft> Create { get; init; }
            public required Action<MultiworldProfile, string> LinkCheese { get; init; }
        }

        // ---- The pieces the dialogs share ----

        private static T Show<T>(Node parent, T dialog, string id) where T : AcceptDialog
        {
            dialog.SetMeta("quick_setup", id);
            dialog.Canceled += dialog.QueueFree;
            parent.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(600, 0));
            return dialog;
        }

        private static VBoxContainer Body(AcceptDialog dialog, string intro)
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(540, 0) };
            box.AddThemeConstantOverride("separation", 8);
            dialog.AddChild(box);
            var text = Kit.Muted(intro);
            text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            box.AddChild(text);
            return box;
        }

        private static GridContainer Grid(VBoxContainer box)
        {
            var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            grid.AddThemeConstantOverride("h_separation", 10);
            grid.AddThemeConstantOverride("v_separation", 6);
            box.AddChild(grid);
            return grid;
        }

        private static void Row(GridContainer grid, string label, Control value)
        {
            var l = Kit.Muted(label);
            l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            grid.AddChild(l);
            value.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            grid.AddChild(value);
        }

        private static Label Problem(VBoxContainer box)
        {
            var problem = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            problem.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Error);
            box.AddChild(problem);
            return problem;
        }

        private static void Say(Label problem, string text)
        {
            problem.Text = text;
            problem.Visible = true;
        }

        private static void Close(AcceptDialog dialog)
        {
            dialog.Hide();
            dialog.QueueFree();
        }

        // ---- New multiworld ----

        /// <summary>A new multiworld: its name, server and password, and its slots (typed, or from a YAML, which names a slot and its game).</summary>
        public static ConfirmationDialog NewMultiworld(Node parent, Hooks hooks)
        {
            var tr = hooks.Tr;
            var dialog = new ConfirmationDialog { Title = tr("New multiworld"), OkButtonText = tr("Create"), DialogHideOnOk = false };
            var box = Body(dialog, tr("Its name, its server and the slots you play; a YAML names a slot and its game. The Multiworlds page holds the rest (the room link, Cheese Tracker, each slot's details)."));
            var grid = Grid(box);
            var name = new LineEdit { Name = "NameBox", PlaceholderText = tr("A name for this multiworld"), AccessibilityName = tr("Name") };
            Row(grid, tr("Name"), name);
            var server = new LineEdit { Name = "ServerBox", PlaceholderText = tr("archipelago.gg:12345"), AccessibilityName = tr("Server address") };
            Row(grid, tr("Server"), server);
            var passwordLine = new HBoxContainer();
            passwordLine.AddThemeConstantOverride("separation", 6);
            var password = new LineEdit { Name = "PasswordBox", PlaceholderText = tr("Password (optional)"), Secret = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = tr("Password") };
            passwordLine.AddChild(password);
            Button eye = null!;
            eye = Kit.Button("", tr("Show the password"), () =>
            {
                password.Secret = !password.Secret;
                eye.Icon = LucideTextures.Get(password.Secret ? "eye" : "eye-off", AP_Atlas.Core.ThemeColors.TextMuted, 1.0f);
                eye.TooltipText = password.Secret ? tr("Show the password") : tr("Hide the password");
                eye.AccessibilityName = eye.TooltipText;
            }, focusable: false);
            eye.Icon = LucideTextures.Get("eye", AP_Atlas.Core.ThemeColors.TextMuted, 1.0f);
            passwordLine.AddChild(eye);
            Row(grid, tr("Password"), passwordLine);

            // The slots: a row each; a YAML's players arrive as rows with their game and the YAML kept for them.
            var slots = new VBoxContainer { Name = "SlotsBox" };
            slots.AddThemeConstantOverride("separation", 4);
            HBoxContainer AddSlotRow(string slotName, string? game, string? yaml)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                var edit = new LineEdit { Text = slotName, PlaceholderText = tr("The slot's name in the room"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = tr("Slot name") };
                row.AddChild(edit);
                if (game != null)
                {
                    var gameLabel = Kit.Muted(game);
                    gameLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                    row.AddChild(gameLabel);
                    row.SetMeta("game", game);
                }
                if (yaml != null) row.SetMeta("yaml", yaml);
                row.AddChild(Kit.Button("×", tr("Removes this slot"), () =>
                {
                    slots.RemoveChild(row);
                    row.QueueFree();
                }, small: true));
                slots.AddChild(row);
                return row;
            }
            AddSlotRow("", null, null);
            Row(grid, tr("Slots"), slots);
            void TakePlayers(string kept, List<(string Name, List<string> Games)> players)
            {
                // An untouched empty row gives way to the YAML's first player.
                var rows = slots.GetChildren().OfType<HBoxContainer>().ToList();
                if (rows.Count == 1 && rows[0].GetChild<LineEdit>(0).Text.Trim().Length == 0)
                {
                    slots.RemoveChild(rows[0]);
                    rows[0].QueueFree();
                }
                void Next(int index)
                {
                    if (index >= players.Count || !GodotObject.IsInstanceValid(dialog)) return;
                    var (playerName, games) = players[index];
                    string? game = games.Count == 1 ? games[0] : null;
                    if (string.IsNullOrWhiteSpace(playerName) || playerName.Contains('{'))
                    {
                        // A name with a placeholder is filled in by Archipelago when it rolls: the room's name is the user's to type.
                        Dialogs.Prompt(dialog, tr("Slot name"),
                            tr("{0} names its player \"{1}\", a pattern Archipelago fills in when it rolls. What is this slot called in the room?").Replace("{0}", System.IO.Path.GetFileName(kept)).Replace("{1}", string.IsNullOrWhiteSpace(playerName) ? "?" : playerName),
                            "", "Player1", tr("Add slot"), typed =>
                            {
                                if (!string.IsNullOrWhiteSpace(typed)) AddSlotRow(typed.Trim(), game, kept);
                                Next(index + 1);
                            });
                        return;
                    }
                    AddSlotRow(playerName, game, kept);
                    Next(index + 1);
                }
                Next(0);
            }
            var slotButtons = new HBoxContainer();
            slotButtons.AddThemeConstantOverride("separation", 6);
            var addSlot = Kit.Button(tr("+ Add slot"), tr("Another slot you play in this multiworld."), () => AddSlotRow("", null, null), small: true);
            addSlot.Name = "AddSlotRow";
            slotButtons.AddChild(addSlot);
            var addYaml = Kit.Button(tr("Add YAML…"), tr("Keeps a player YAML in Atlas's YAML folder and adds a slot for each player it names, with the YAML tied to that slot."), () => hooks.PickYaml(path =>
            {
                var kept = hooks.KeepYaml(path);
                if (kept != null && GodotObject.IsInstanceValid(dialog)) TakePlayers(kept.Value.Kept, kept.Value.Players);
            }), small: true);
            addYaml.Name = "AddYamlButton";
            slotButtons.AddChild(addYaml);
            grid.AddChild(new Control());
            grid.AddChild(slotButtons);
            var problem = Problem(box);

            dialog.Confirmed += () =>
            {
                if (name.Text.Trim().Length == 0)
                {
                    Say(problem, tr("Give the multiworld a name first."));
                    return;
                }
                var names = new List<string>();
                var yamlBySlot = new Dictionary<string, string>();
                var gameBySlot = new Dictionary<string, string>();
                foreach (var row in slots.GetChildren().OfType<HBoxContainer>())
                {
                    string slot = row.GetChild<LineEdit>(0).Text.Trim();
                    if (slot.Length == 0 || names.Contains(slot)) continue;
                    names.Add(slot);
                    if (row.HasMeta("yaml")) yamlBySlot[slot] = row.GetMeta("yaml").AsString();
                    if (row.HasMeta("game")) gameBySlot[slot] = row.GetMeta("game").AsString();
                }
                var draft = new NewMultiworldDraft(name.Text.Trim(), server.Text.Trim(), password.Text, names, yamlBySlot, gameBySlot);
                Close(dialog);
                hooks.Create(draft);
            };
            Show(parent, dialog, "new-multiworld");
            Ui.Defer(dialog, () => name.GrabFocus());
            return dialog;
        }

        // ---- Connect a slot ----

        /// <summary>Every multiworld's slots with a Connect button each (and Connect all per multiworld); a connected slot says so.</summary>
        public static AcceptDialog ConnectSlots(Node parent, Hooks hooks)
        {
            var tr = hooks.Tr;
            var dialog = new AcceptDialog { Title = tr("Connect a slot"), OkButtonText = tr("Close") };
            var box = Body(dialog, tr("Connect a slot and its tools follow the room from then on: items, checks, hints and chat. Each multiworld needs its server address first."));
            var profiles = hooks.Profiles().Where(p => p.Slots.Count > 0).ToList();
            if (profiles.Count == 0) box.AddChild(Kit.Muted(tr("No multiworld has a slot yet: New… on the step above makes one.")));
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, Math.Min(360, 20 + profiles.Sum(p => 36 + p.Slots.Count * 34))) };
            var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 4);
            scroll.AddChild(list);
            box.AddChild(scroll);
            foreach (var profile in profiles)
            {
                var header = new HBoxContainer();
                header.AddThemeConstantOverride("separation", 8);
                var title = Kit.Heading(profile.Name, 1.05f);
                title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                header.AddChild(title);
                bool hasServer = !string.IsNullOrWhiteSpace(profile.ServerUrl);
                var server = Kit.Muted(hasServer ? profile.ServerUrl : tr("No server address yet"));
                server.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                header.AddChild(server);
                if (hasServer)
                {
                    var target = profile;
                    var all = Kit.Button(tr("Connect all"), tr("Connects every slot of this multiworld that isn't connected."), () => hooks.ConnectAll(target), small: true);
                    all.SetMeta("connect_all", profile.Id);
                    header.AddChild(all);
                }
                list.AddChild(header);
                foreach (string slot in profile.Slots)
                {
                    var indent = new MarginContainer();
                    indent.AddThemeConstantOverride("margin_left", 16);
                    var row = new HBoxContainer();
                    row.AddThemeConstantOverride("separation", 8);
                    var label = Kit.Text(slot);
                    label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                    row.AddChild(label);
                    bool live = hooks.IsSlotLive(profile.Id, slot);
                    string slotName = slot;
                    var target = profile;
                    Button connect = null!;
                    connect = Kit.Button(live ? tr("Connected") : tr("Connect"), tr("Connects this slot; its tools follow the room from then on."), () =>
                    {
                        connect.Disabled = true;
                        connect.Text = tr("Connecting…");
                        hooks.ConnectSlot(target, slotName);
                    }, enabled: !live && hasServer, small: true);
                    connect.SetMeta("connect_slot", profile.Id + "|" + slot);
                    row.AddChild(connect);
                    indent.AddChild(row);
                    list.AddChild(indent);
                }
            }
            dialog.Confirmed += dialog.QueueFree;
            return Show(parent, dialog, "connect-slots");
        }

        // ---- Find a map pack ----

        /// <summary>A game of the user's slots (or another, typed), then one GitHub search shown on the Map Packs page.</summary>
        public static ConfirmationDialog FindPack(Node parent, Hooks hooks)
        {
            var tr = hooks.Tr;
            var dialog = new ConfirmationDialog { Title = tr("Find a map pack"), OkButtonText = tr("Search GitHub"), DialogHideOnOk = false };
            var box = Body(dialog, tr("Atlas searches GitHub once for the game's PopTracker packs and shows what it found on the Map Packs page, where a pack you choose is installed; nothing is installed by itself. The Map Packs page also installs a zip you have."));
            var grid = Grid(box);
            var played = hooks.PlayedGames();
            var choice = new OptionButton { Name = "GameChoice", AccessibilityName = tr("A game of your slots") };
            foreach (string game in played) choice.AddItem(game);
            if (played.Count > 0) choice.Selected = 0;
            else
            {
                choice.AddItem(tr("(no slot has a game yet)"));
                choice.Selected = 0;
                choice.Disabled = true;
            }
            Row(grid, tr("A game of your slots"), choice);
            var other = new LineEdit { Name = "OtherGame", PlaceholderText = tr("Or type another game's name"), AccessibilityName = tr("Another game") };
            Row(grid, tr("Another game"), other);
            var problem = Problem(box);
            dialog.Confirmed += () =>
            {
                string typed = other.Text.Trim();
                string game = typed.Length > 0 ? typed : played.Count > 0 && choice.Selected >= 0 && choice.Selected < played.Count ? played[choice.Selected] : "";
                if (game.Length == 0)
                {
                    Say(problem, tr("Pick a game, or type its name."));
                    return;
                }
                Close(dialog);
                hooks.FindPack(game);
            };
            return Show(parent, dialog, "find-pack");
        }

        // ---- Link Cheese Tracker ----

        /// <summary>A multiworld and its Cheese Tracker link (or the archipelago.gg room link), checked online and linked.</summary>
        public static ConfirmationDialog LinkCheese(Node parent, Hooks hooks)
        {
            var tr = hooks.Tr;
            var dialog = new ConfirmationDialog { Title = tr("Link Cheese Tracker"), OkButtonText = tr("Link"), DialogHideOnOk = false };
            var box = Body(dialog, tr("Cheese Tracker is the shared tracker many async multiworlds use. Paste the multiworld's page there, or the archipelago.gg room link (Atlas looks it up); Atlas checks the link online and links the multiworld, and the Cheese Tracker tool then shows it."));
            var grid = Grid(box);
            var profiles = hooks.Profiles().ToList();
            var choice = new OptionButton { Name = "MultiworldChoice", AccessibilityName = tr("Multiworld") };
            foreach (var profile in profiles) choice.AddItem(profile.Name);
            if (profiles.Count > 0) choice.Selected = 0;
            Row(grid, tr("Multiworld"), choice);
            var link = new LineEdit { Name = "LinkBox", PlaceholderText = tr("Cheese Tracker link, or the archipelago.gg room link"), AccessibilityName = tr("Cheese Tracker link") };
            Row(grid, tr("Link"), link);
            var problem = Problem(box);
            dialog.Confirmed += () =>
            {
                if (profiles.Count == 0 || choice.Selected < 0 || choice.Selected >= profiles.Count)
                {
                    Say(problem, tr("Make a multiworld first (New… on the step above)."));
                    return;
                }
                if (link.Text.Trim().Length == 0)
                {
                    Say(problem, tr("Paste the link first."));
                    return;
                }
                var profile = profiles[choice.Selected];
                string text = link.Text.Trim();
                Close(dialog);
                hooks.LinkCheese(profile, text);
            };
            Show(parent, dialog, "link-cheese");
            Ui.Defer(dialog, () => link.GrabFocus());
            return dialog;
        }
    }
}
