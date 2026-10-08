#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>What the Add a slot dialog hands back: the slot's name, its game when known, the YAML kept for it, and whether to connect now.</summary>
    public sealed record SlotDraft(string Name, string? Game, string? YamlPath, bool ConnectNow);

    /// <summary>A line about a slot's game in the Atlas Engine: what it says, and a button when something can be done about it.</summary>
    public sealed record GameStatus(string Text, string? ActionText = null, Action? Action = null);

    /// <summary>
    /// Adding a slot to a multiworld with everything the tools need: its name; its game, found by typed words among every
    /// game Atlas knows (a YAML names the slot and its game at once, with a choice when it names several players); a
    /// line saying whether the game's logic is ready in the Atlas Engine, with Set up the Atlas Engine or Install the
    /// newest apworld… when it isn't; and Connect now. Frees itself when closed.
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
            /// <summary>Opens the YAML file picker and hands back the chosen path.</summary>
            public required Action<Action<string>> PickYaml { get; init; }
            /// <summary>Keeps a YAML in Atlas's YAML folder: the kept copy's path and the players it names; null when it can't be kept (the hook says why).</summary>
            public required Func<string, (string Kept, List<(string Name, List<string> Games)> Players)?> KeepYaml { get; init; }
            public required Action<SlotDraft> Add { get; init; }
        }

        private readonly Hooks _hooks;
        private readonly List<string> _games;
        private readonly LineEdit _name;
        private readonly LineEdit _gameSearch;
        private readonly ItemList _gameList;
        private readonly Label _yamlLabel;
        private readonly OptionButton _players;
        private readonly Label _status;
        private readonly Button _statusAction;
        private readonly CheckBox _connectNow;
        private readonly Label _problem;
        private readonly List<string> _shownGames = new();
        private List<(string Name, List<string> Games)> _yamlPlayers = new();
        private string? _yamlPath;
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
            _problem = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart }; // added last; made first, since typing clears it
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
            _name = new LineEdit { PlaceholderText = tr("The slot's name in the room (the YAML's name)"), AccessibilityName = tr("Slot name") };
            _name.TextChanged += _ => _problem.Visible = false;
            Row(tr("Name"), _name);

            // The game: typed words narrow every game Atlas knows; the chosen one (or a name typed in full) is the slot's.
            var gameBox = new VBoxContainer();
            gameBox.AddThemeConstantOverride("separation", 4);
            _gameSearch = new LineEdit { PlaceholderText = tr("Type a few letters of the game, or pick a YAML below"), ClearButtonEnabled = true, AccessibilityName = tr("Game") };
            _gameSearch.TextChanged += _ => OnGameTyped();
            gameBox.AddChild(_gameSearch);
            _gameList = new ItemList { CustomMinimumSize = new Vector2(0, 140), SizeFlagsVertical = Control.SizeFlags.ExpandFill, AccessibilityName = tr("Games") };
            _gameList.ItemSelected += index => SetGame(_shownGames[(int)index]);
            gameBox.AddChild(_gameList);
            Row(tr("Game"), gameBox);

            // The YAML: kept in Atlas's folder and tied to the slot; it names the slot and its game.
            var yamlLine = new HBoxContainer();
            yamlLine.AddThemeConstantOverride("separation", 6);
            _yamlLabel = Kit.Text(tr("None (most games don't need one)"));
            _yamlLabel.ClipText = true;
            _yamlLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _yamlLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            yamlLine.AddChild(_yamlLabel);
            yamlLine.AddChild(Kit.Button(tr("Pick a YAML…"), tr("The player YAML this slot was rolled from: kept in Atlas's YAML folder and tied to the slot; it names the slot and its game."), () => _hooks.PickYaml(TakeYaml), small: true));
            _players = new OptionButton { Visible = false, AccessibilityName = tr("Which player"), TooltipText = tr("The YAML names several players: which one is this slot?") };
            _players.ItemSelected += index => ApplyPlayer((int)index);
            yamlLine.AddChild(_players);
            Row(tr("YAML"), yamlLine);

            // The engine line: whether the game's logic is ready, and what to do when it isn't.
            var statusLine = new HBoxContainer();
            statusLine.AddThemeConstantOverride("separation", 6);
            _status = Kit.Muted("");
            _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
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
            Canceled += QueueFree;
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
            dialog.PopupCentered(new Vector2I(620, 0));
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
                _status.Text = _hooks.Tr("Pick the game (or a YAML that names it) and Atlas says whether its logic is ready.");
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

        // ---- The YAML ----

        /// <summary>Keeps a YAML for the slot and fills in what it names (also for tests, in place of the file picker).</summary>
        internal void TakeYaml(string path)
        {
            var kept = _hooks.KeepYaml(path);
            if (kept == null) return;
            _yamlPath = kept.Value.Kept;
            _yamlPlayers = kept.Value.Players;
            _yamlLabel.Text = System.IO.Path.GetFileName(_yamlPath);
            _yamlLabel.TooltipText = _yamlPath;
            _players.Clear();
            _players.Visible = _yamlPlayers.Count > 1;
            foreach (var (name, games) in _yamlPlayers)
                _players.AddItem((string.IsNullOrWhiteSpace(name) ? "?" : name) + " (" + string.Join(", ", games) + ")");
            if (_yamlPlayers.Count == 0)
            {
                _yamlLabel.Text = _hooks.Tr("{0} names no player with a game").Replace("{0}", System.IO.Path.GetFileName(_yamlPath));
                return;
            }
            _players.Selected = 0;
            ApplyPlayer(0);
        }

        private void ApplyPlayer(int index)
        {
            if (index < 0 || index >= _yamlPlayers.Count) return;
            var (name, games) = _yamlPlayers[index];
            // A name with a placeholder ({player}, {number}) is filled in by Archipelago when it rolls: the room's name is the user's to type.
            _name.Text = !string.IsNullOrWhiteSpace(name) && !name.Contains('{') ? name : "";
            _name.PlaceholderText = name.Contains('{') ? _hooks.Tr("The slot's name in the room ({0} is filled in when it rolls)").Replace("{0}", name) : _hooks.Tr("The slot's name in the room (the YAML's name)");
            if (games.Count == 1) SetGame(games[0]);
            _problem.Visible = false;
        }

        /// <summary>The YAML's players as the choice lists them (for tests).</summary>
        public List<string> PlayerChoices() => Enumerable.Range(0, _players.ItemCount).Select(i => _players.GetItemText(i)).ToList();

        /// <summary>Chooses one of the YAML's players (for tests).</summary>
        public void ChoosePlayer(int index)
        {
            _players.Selected = index;
            ApplyPlayer(index);
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
            var draft = new SlotDraft(name, _game, _yamlPath, _connectNow.ButtonPressed);
            Hide();
            QueueFree();
            _hooks.Add(draft);
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
        public string ProblemText => _problem.Visible ? _problem.Text : "";
    }
}
