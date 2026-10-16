#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>What the Add a slot dialog hands back: the slot's name, its game when known, and whether to connect now.</summary>
    public sealed record SlotDraft(string Name, string? Game, bool ConnectNow);

    /// <summary>A line about a slot's game in the Atlas Engine: what it says, and a button when something can be done about it.</summary>
    public sealed record GameStatus(string Text, string? ActionText = null, Action? Action = null);

    /// <summary>
    /// Adding a slot to a multiworld with everything the tools need: its name; its game, found by typed words among every
    /// game Atlas knows; a line saying whether the game's logic is ready in the Atlas Engine, with Set up the Atlas Engine
    /// or Add this game… (Add games, the game ticked, over this dialog) when it isn't; and Connect now. No YAML: logic asks
    /// for one only when the server's data can't rebuild the world (the slot's Details and the Logic Tracker's banner take
    /// it then). Frees itself when closed.
    /// </summary>
    public sealed partial class AddSlotDialog : ConfirmationDialog
    {
        /// <summary>What the dialog needs from the window.</summary>
        public sealed class Hooks
        {
            public required Func<string, string> Tr { get; init; }
            /// <summary>Every game Atlas knows, the games of the user's own slots first.</summary>
            public required Func<IReadOnlyList<string>> Games { get; init; }
            public required Func<string, bool> SlotNameTaken { get; init; }
            /// <summary>Whether the multiworld has a server address (Connect now starts on).</summary>
            public required bool HasServer { get; init; }
            /// <summary>The engine line for a game.</summary>
            public required Func<string, GameStatus> GameStatus { get; init; }
            public required Action<SlotDraft> Add { get; init; }
        }

        private readonly Hooks _hooks;
        private readonly List<string> _games;
        private readonly LineEdit _name;
        private readonly LineEdit _gameSearch;
        private readonly ItemList _gameList;
        private readonly Label _status;
        private readonly Button _statusAction;
        private readonly CheckBox _connectNow;
        private readonly Label _problem;
        private readonly List<string> _shownGames = new();
        private string? _game;
        private Action? _statusActionRun;

        private AddSlotDialog(Hooks hooks)
        {
            _hooks = hooks;
            var tr = hooks.Tr;
            _games = hooks.Games().ToList();
            Title = tr("Add a slot");
            OkButtonText = tr("Add");
            DialogHideOnOk = false;
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(560, 0) };
            box.AddThemeConstantOverride("separation", 8);
            AddChild(box);
            _problem = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(560, 0) }; // added last; made first, since typing clears it
            _problem.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Error);
            var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            grid.AddThemeConstantOverride("h_separation", 10);
            grid.AddThemeConstantOverride("v_separation", 6);
            box.AddChild(grid);
            void Row(string label, Control value)
            {
                var l = Kit.Muted(label);
                l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                grid.AddChild(l);
                value.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                grid.AddChild(value);
            }

            // The name, as the room knows the slot.
            _name = new LineEdit { PlaceholderText = tr("The slot's name in the room"), AccessibilityName = tr("Slot name") };
            _name.TextChanged += _ => _problem.Visible = false;
            Row(tr("Name"), _name);

            // The game: typed words narrow every game Atlas knows; the chosen one (or a name typed in full) is the slot's.
            var gameBox = new VBoxContainer();
            gameBox.AddThemeConstantOverride("separation", 4);
            _gameSearch = new LineEdit { PlaceholderText = tr("Type a few letters of the game"), ClearButtonEnabled = true, AccessibilityName = tr("Game") };
            _gameSearch.TextChanged += _ => OnGameTyped();
            gameBox.AddChild(_gameSearch);
            _gameList = new ItemList { CustomMinimumSize = new Vector2(0, 140), SizeFlagsVertical = Control.SizeFlags.ExpandFill, AccessibilityName = tr("Games") };
            _gameList.ItemSelected += index => SetGame(_shownGames[(int)index]);
            gameBox.AddChild(_gameList);
            Row(tr("Game"), gameBox);

            // The engine line: whether the game's logic is ready, and what to do when it isn't.
            var statusLine = new HBoxContainer();
            statusLine.AddThemeConstantOverride("separation", 6);
            _status = Kit.Muted("");
            _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _status.CustomMinimumSize = new Vector2(320, 0); // wrapped at a real width from the first measure
            _status.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            statusLine.AddChild(_status);
            _statusAction = Kit.Button("", null, () => _statusActionRun?.Invoke(), small: true);
            _statusAction.Visible = false;
            _statusAction.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            statusLine.AddChild(_statusAction);
            Row(tr("Logic"), statusLine);

            _connectNow = new CheckBox { Text = tr("Connect now"), ButtonPressed = hooks.HasServer, TooltipText = tr("Saves the slot and connects it as the dialog closes (the multiworld needs its server address).") };
            box.AddChild(_connectNow);
            box.AddChild(_problem);

            Confirmed += OnConfirmed;
            Canceled += Close;
            FillGames();
            RefreshStatus();
        }

        public override void _EnterTree() => AP_Atlas.Core.EngineSetup.AtlasEngine.Changed += OnEngineChanged;

        public override void _ExitTree() => AP_Atlas.Core.EngineSetup.AtlasEngine.Changed -= OnEngineChanged;

        private void OnEngineChanged() => Ui.Defer(this, RefreshStatus);

        /// <summary>Opens the dialog over the window.</summary>
        public static AddSlotDialog Open(Node parent, Hooks hooks)
        {
            var dialog = new AddSlotDialog(hooks);
            parent.AddChild(dialog);
            WindowFit.Pop(dialog, 620);
            Ui.Defer(dialog, () => dialog._name.GrabFocus());
            return dialog;
        }

        // ---- The game ----

        /// <summary>The slot's game: the one picked from the list, or a known game typed in full; null while none is.</summary>
        public string? CurrentGame => _game;

        private void OnGameTyped()
        {
            string typed = _gameSearch.Text.Trim();
            _game = _games.FirstOrDefault(g => string.Equals(g, typed, StringComparison.OrdinalIgnoreCase));
            FillGames();
            RefreshStatus();
        }

        private void FillGames()
        {
            _gameList.Clear();
            _shownGames.Clear();
            string[] words = _gameSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (string game in _games.Where(g => words.All(w => g.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(400))
            {
                _shownGames.Add(game);
                _gameList.AddItem(game);
                if (game == _game) _gameList.Select(_gameList.ItemCount - 1);
            }
        }

        private void SetGame(string? game)
        {
            _game = game;
            if (game != null && !string.Equals(_gameSearch.Text.Trim(), game, StringComparison.OrdinalIgnoreCase))
            {
                _gameSearch.Text = game; // setting the text doesn't raise TextChanged
                FillGames();
            }
            RefreshStatus();
        }

        /// <summary>Picks a game as a click on the list does (for tests).</summary>
        public void ChooseGame(string game) => SetGame(game);

        /// <summary>The games the list shows now (for tests).</summary>
        public IReadOnlyList<string> ShownGames => _shownGames;

        private void RefreshStatus()
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            if (_game == null)
            {
                _status.Text = _hooks.Tr("Pick the game and Atlas says whether its logic is ready.");
                _statusAction.Visible = false;
                _statusActionRun = null;
                return;
            }
            var status = _hooks.GameStatus(_game);
            _status.Text = status.Text;
            _statusActionRun = status.Action;
            _statusAction.Visible = status.ActionText != null && status.Action != null;
            _statusAction.Text = status.ActionText ?? "";
            _statusAction.AccessibilityName = _statusAction.Text;
        }

        // ---- Add ----

        private void OnConfirmed()
        {
            string name = _name.Text.Trim();
            if (name.Length == 0)
            {
                Say(_hooks.Tr("Give the slot its name in the room first."));
                return;
            }
            if (_hooks.SlotNameTaken(name))
            {
                Say(_hooks.Tr("This multiworld already has a slot named {0}.").Replace("{0}", name));
                return;
            }
            var draft = new SlotDraft(name, _game, _connectNow.ButtonPressed);
            Close();
            _hooks.Add(draft);
        }

        /// <summary>
        /// Closes the dialog. An Add games opened over it that's past its list (its confirm, a running batch, its summary)
        /// moves to the window and goes on; one still on its list goes with it.
        /// </summary>
        private void Close()
        {
            var to = GetParent();
            foreach (var addGames in GetChildren().OfType<AddGamesDialog>().ToList())
                if (to != null && GodotObject.IsInstanceValid(addGames) && !addGames.IsQueuedForDeletion() && addGames.Panel.CurrentStage != AddGamesPanel.Stage.Pick)
                    addGames.MoveTo(to);
            Hide();
            QueueFree();
        }

        private void Say(string problem)
        {
            _problem.Text = problem;
            _problem.Visible = true;
        }

        // ---- For tests ----

        public LineEdit NameInput => _name;
        public LineEdit GameSearch => _gameSearch;
        public CheckBox ConnectNow => _connectNow;
        public string StatusText => _status.Text;
        /// <summary>The engine line's button text, or null while it has none.</summary>
        public string? StatusActionText => _statusAction.Visible ? _statusAction.Text : null;
        /// <summary>Presses the engine line's button, as a click does (for tests).</summary>
        public void PressStatusActionForTests() => _statusActionRun?.Invoke();
        public string ProblemText => _problem.Visible ? _problem.Text : "";
    }
}
