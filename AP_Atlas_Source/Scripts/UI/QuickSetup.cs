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
    /// Home's New… : a new multiworld in a small dialog in place (no page jump), with its slots and YAMLs. The dialog
    /// frees itself when closed and carries the meta "quick_setup" (its id) for tests. The rest of the setup is the setup
    /// window (<see cref="SetupWizard"/>).
    /// </summary>
    public static class QuickSetup
    {
        /// <summary>What the dialogs need from the window.</summary>
        public sealed class Hooks
        {
            public required Func<string, string> Tr { get; init; }
            public required Action<Action<string>> PickYaml { get; init; }
            /// <summary>Keeps a YAML in Atlas's YAML folder: the kept copy's path and the players it names; null when it can't be kept (the hook says why).</summary>
            public required Func<string, (string Kept, List<(string Name, List<string> Games)> Players)?> KeepYaml { get; init; }
            public required Action<NewMultiworldDraft> Create { get; init; }
        }

        // ---- The pieces the dialogs share ----

        private static T Show<T>(Node parent, T dialog, string id) where T : AcceptDialog
        {
            dialog.SetMeta("quick_setup", id);
            dialog.Canceled += dialog.QueueFree;
            parent.AddChild(dialog);
            WindowFit.Pop(dialog, 600);
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
            text.CustomMinimumSize = new Vector2(540, 0); // wrapped at its real width from the first measure (a 0-wide first pass reports a tall minimum)
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
            var problem = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(540, 0) };
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
    }
}
