#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core;
using AP_Atlas.Core.EngineSetup;
using AP_Atlas.Core.Games;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>What the Add games screen reads (the Games page's <c>ReadForAdding</c>): the plan's context, every game to list (those of your multiworlds marked), and a game's facts on request.</summary>
    public sealed record AddGamesSource(AddContext Context, IReadOnlyList<AddGamesSource.Entry> Games, Func<string, AddGameFacts> Facts)
    {
        /// <summary>A game the screen lists, and whether a slot of yours plays it (listed first, ticked when it needs something).</summary>
        public sealed record Entry(string Game, bool Played);

        /// <summary>Nothing to list (the Games page isn't built yet): the screen leads with the engine and lists nothing.</summary>
        public static readonly AddGamesSource Empty = new(new AddContext { EngineReady = false }, Array.Empty<Entry>(), game => new AddGameFacts { Game = game });
    }

    /// <summary>
    /// The one Add games screen (the owner's "adding games, simply"): every game Atlas knows with a check box, the games of
    /// your multiworlds first and ticked where they need something, each row saying in a few words what installing it gets
    /// and its download, a search, and the totals of download and disk. Then ONE confirm: a line per game, "From N projects"
    /// (the projects and what installing trusts in its tooltip) and the tick "Don't ask again for Atlas's downloads"; then
    /// moving dots, the step and one bar while <see cref="GameAdder"/> runs (Cancel stops after the step it's on); then a
    /// summary, a line per game with one number per item (its logic's version, its map's pinned checks, what Atlas fixed, with
    /// Review) and the way on where something's missing or the map fits poorly, and the tip about fixing on the map. Few
    /// words everywhere (round 13, "don't be wordy"), details in tooltips. It leads with the Atlas Engine while that isn't
    /// set up. A control, so the setup window holds it on a page;
    /// <see cref="AddGamesDialog"/> pops it everywhere else. Its host shows its two actions and presses them. Main thread.
    /// </summary>
    public sealed partial class AddGamesPanel : VBoxContainer
    {
        /// <summary>What the screen needs from the window.</summary>
        public sealed class Hooks
        {
            public required Func<string, string> Tr { get; init; }
            public required AppSettings Settings { get; init; }
            /// <summary>Reads the lists again and gives the games and their facts (the Games page's ReadForAdding).</summary>
            public required Func<AddGamesSource> Source { get; init; }
            /// <summary>Runs a confirmed plan (MainTrackerWindow.AddGamesAsync); it never throws.</summary>
            public required Func<AddPlan, AddGamesConsent, Action<AddGamesStatus>?, CancellationToken, Task<GameAdderRun>> RunAsync { get; init; }
            public required Action OpenEngineSetup { get; init; }
            /// <summary>Shows a game's page and searches GitHub for its packs (one search, when pressed).</summary>
            public required Action<string> FindPack { get; init; }
            /// <summary>Shows a game's page and measures its map pack against the game's newest releases ("Which version works with my map pack?", asked first).</summary>
            public required Action<string> FindVersion { get; init; }
            /// <summary>Installs a project's pack for a game, asking first (the guide's project Atlas's list lacks).</summary>
            public required Action<string, string> InstallPack { get; init; }
            /// <summary>Start a pack for a game (its names come from a connected slot).</summary>
            public required Action<string> StartPack { get; init; }
            /// <summary>Free bytes where Atlas keeps its data (null: unknown).</summary>
            public required Func<long?> FreeBytes { get; init; }
            /// <summary>Shows what Atlas fixed by itself in a pack (its key, its zip), each fix undoable (null: no Review button).</summary>
            public Action<string, string>? ReviewFixes { get; init; }
            /// <summary>A batch ended (the window's pages read again).</summary>
            public Action<GameAdderRun>? BatchEnded { get; init; }
        }

        public enum Stage { Pick, Confirm, Running, Summary }

        /// <summary>The most one frame spends reading games' facts; the rest wait for the next frame.</summary>
        private const long FrameBudgetMs = 6;
        private const int GetsCell = 2, SizeCell = 3;
        /// <summary>The most the summary's list stands before it scrolls.</summary>
        private const int SummaryMostHeight = 420;
        private bool Narrow => _width < 720;
        private int NameWidth => Narrow ? 170 : 220;

        private readonly Hooks _hooks;
        private readonly Func<string, string> _tr;
        private readonly int _width;
        private readonly VBoxContainer _pick, _confirm, _running, _summary;
        private PanelContainer _engineLead = null!;
        private Label _note = null!, _totals = null!, _runningTitle = null!, _status = null!;
        private AtlasTable _table = null!;
        private ProgressBar _bar = null!;
        private BusyDots _busy = null!;
        private CheckBox? _dontAsk;
        private AddGamesSource _source = AddGamesSource.Empty;
        private readonly Dictionary<string, AddGameFacts> _facts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GamePlan> _alone = new(StringComparer.OrdinalIgnoreCase); // each game's plan on its own: its row
        private readonly HashSet<string> _ticked = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _toRead = new();
        private readonly List<AtlasTable.Row> _rows = new();
        private readonly Dictionary<string, AtlasTable.Row> _rowOf = new(StringComparer.OrdinalIgnoreCase);
        private AddPlan _tickedPlan = AddGamesPlan.Make(Array.Empty<AddGameFacts>(), new AddContext());
        private AddPlan? _plan;
        private string? _preselect;
        private bool _confirmOk, _cancelling;
        // The games of your multiworlds already offered a tick: one that becomes addable later (the engine set up from here)
        // is ticked then, once; one the user unticked stays unticked.
        private readonly HashSet<string> _preTicked = new(StringComparer.OrdinalIgnoreCase);
        private int _generation;
        private CancellationTokenSource? _cts;

        /// <summary>The stage shown.</summary>
        public Stage CurrentStage { get; private set; }

        /// <summary>Leaving the tree stops a running batch (the dialog: yes; the setup window lets it go on).</summary>
        public bool CancelOnExit { get; set; }

        /// <summary>The host's buttons should follow <see cref="PrimaryText"/>, <see cref="PrimaryEnabled"/> and <see cref="SecondaryText"/>.</summary>
        public event Action? ActionsChanged;

        /// <summary>Cancel on the list, or Done on the summary: the host closes (the setup window goes on).</summary>
        public event Action? CloseRequested;

        /// <summary>A batch ended (after the summary is built).</summary>
        public event Action<GameAdderRun>? Finished;

        /// <summary>Each line the batch reports, as it comes (main thread; for tests).</summary>
        public event Action<AddGamesStatus>? StatusChanged;

        public AddGamesPanel(Hooks hooks, string? preselect = null, int width = 860)
        {
            _hooks = hooks;
            _tr = hooks.Tr;
            _preselect = preselect;
            _width = width;
            Name = "AddGamesPanel";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            CustomMinimumSize = new Vector2(width, 0);
            AddThemeConstantOverride("separation", 8);
            _pick = Part();
            _confirm = Part();
            _running = Part();
            _summary = Part();
            BuildPick();
            BuildRunning();
            GoTo(Stage.Pick);
        }

        public override void _Ready() => Start();

        public override void _EnterTree() => AtlasEngine.Changed += OnEngineChanged;

        public override void _ExitTree()
        {
            AtlasEngine.Changed -= OnEngineChanged;
            if (CancelOnExit) _cts?.Cancel();
        }

        // The engine set up, repaired or changed while the list shows: what each game needs is read again (ticks kept).
        private void OnEngineChanged() => Ui.Defer(this, () => { if (CurrentStage == Stage.Pick) Start(); }, "reading the games again after an engine change");

        // ---- The host's two actions ----

        public string PrimaryText => CurrentStage switch
        {
            Stage.Pick => AddGamesWords.Adding(_tickedPlan) switch
            {
                0 => _tr("Install games…"),
                1 => _tr("Install 1 game…"),
                int n => _tr("Install {0} games…").Replace("{0}", n.ToString(CultureInfo.InvariantCulture))
            },
            Stage.Confirm => _tr("Install"),
            Stage.Running => _cancelling ? _tr("Stopping…") : _tr("Cancel"),
            _ => _preselect != null ? _tr("Install {0}…").Replace("{0}", _preselect) : _tr("Done")
        };

        public bool PrimaryEnabled => CurrentStage switch
        {
            Stage.Pick => AddGamesWords.Adding(_tickedPlan) > 0 && _source.Context.EngineReady && !GameAdder.Busy,
            Stage.Confirm => _confirmOk && !GameAdder.Busy,
            Stage.Running => !_cancelling,
            _ => true
        };

        /// <summary>
        /// The second action, or null for none: Cancel on the list, Back on the confirm; on the summary, Try those again when
        /// games weren't done.
        /// </summary>
        public string? SecondaryText => CurrentStage switch
        {
            Stage.Pick => _tr("Cancel"),
            Stage.Confirm => _tr("Back"),
            Stage.Summary when Result?.Outcome.Games.Any(g => !g.Ok) == true => _tr("Try those again"),
            _ => null
        };

        /// <summary>The primary action's tooltip, or null: while running, what Cancel keeps (round 13: no paragraph under the bar).</summary>
        public string? PrimaryTooltip => CurrentStage == Stage.Running && !_cancelling
            ? _tr("Stops after the step it's on: nothing half-done is kept, and what's done stays. Atlas stays usable meanwhile.") : null;

        /// <summary>How many games the ticks add (0: nothing to add, and a host may move on).</summary>
        public int AddingCount => AddGamesWords.Adding(_tickedPlan);

        public void PressPrimary()
        {
            if (!PrimaryEnabled) return;
            switch (CurrentStage)
            {
                case Stage.Pick: GoConfirm(); break;
                case Stage.Confirm: Run(); break;
                case Stage.Running: Cancel(); break;
                case Stage.Summary when _preselect != null:
                    // A game asked for while this batch ran: the list again, with it ticked.
                    _ticked.Clear();
                    GoTo(Stage.Pick);
                    Start();
                    break;
                default: CloseRequested?.Invoke(); break;
            }
        }

        public void PressSecondary()
        {
            switch (CurrentStage)
            {
                case Stage.Pick: CloseRequested?.Invoke(); break;
                case Stage.Confirm:
                    GoTo(Stage.Pick);
                    ApplyPreselect(); // a game asked for while the confirm showed
                    break;
                case Stage.Summary when SecondaryText != null: TryAgain(); break;
            }
        }

        /// <summary>Stops a running batch after the step it's on (nothing half-done is kept; what's done stays).</summary>
        public void Cancel()
        {
            if (CurrentStage != Stage.Running || _cts == null || _cancelling) return;
            _cancelling = true;
            _cts.Cancel();
            _status.Text = _tr("Stopping after the step it's on…");
            RaiseActions();
        }

        /// <summary>Ticks a game (an entry point's), or says why there's nothing to add for it; the list scrolls to it.</summary>
        public void Preselect(string game)
        {
            _preselect = game;
            if (CurrentStage == Stage.Pick && _rows.Count > 0) ApplyPreselect();
            RaiseActions(); // on the summary, its button offers the game
        }

        // ---- The list ----

        private void BuildPick()
        {
            _engineLead = Card(_pick, ThemeColors.Warning, out var lead);
            _engineLead.SetMeta("add_games_engine_lead", true);
            lead.AddChild(Wrap(Kit.Text(_tr("Games' logic runs in the Atlas Engine: set it up first. Your ticks stay."))));
            var setUp = Kit.Button(_tr("Set up the Atlas Engine…"), _tr("Sets up the logic engine in Atlas's own folder (asked first)."), _hooks.OpenEngineSetup);
            setUp.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            lead.AddChild(setUp);
            _pick.AddChild(Wrap(Kit.Muted(_tr("Tick the games you play; yours come first."))));
            _note = Wrap(Kit.Text("", ThemeColors.Warning));
            _note.Visible = false;
            _pick.AddChild(_note);
            _table = new AtlasTable("add-games", _hooks.Settings, _tr)
            {
                CustomMinimumSize = new Vector2(0, 280), // the dialog stays well under the window's height
                EmptyText = _tr("Atlas knows no game yet."),
                NoMatchText = _tr("No game matches."),
                CountWords = (_tr("{0} game"), _tr("{0} games"), _tr("{0} of {1} games"))
            };
            _table.SearchBox.PlaceholderText = _tr("Search games");
            _table.SearchBox.AccessibilityName = _tr("Search games");
            _table.ColumnsMenu.Visible = false;
            _table.ExportMenu.Visible = false;
            // A few words a cell (AddGamesPlan.Words), the sentence in its tooltip; a narrower host (the setup window) gives the game's name way first.
            _table.SetColumns(new[]
            {
                new AtlasTable.Column { Id = "tick", Title = "", MinWidth = 34, Ratio = 0, Sortable = false },
                new AtlasTable.Column { Id = "game", Title = _tr("Game"), MinWidth = Narrow ? 140 : 200, Ratio = 3 },
                new AtlasTable.Column { Id = "gets", Title = _tr("Installs"), MinWidth = Narrow ? 190 : 230, Ratio = 3 },
                new AtlasTable.Column { Id = "size", Title = _tr("Download"), MinWidth = 170, Ratio = 0, Align = HorizontalAlignment.Right }
            }, "game");
            // A check box per row with something to add (the versions table's way); a game with nothing to add has none.
            _table.Customize = (item, row) =>
            {
                item.SetText(0, "");
                if (!Tickable(row.Key)) return;
                item.SetCellMode(0, TreeItem.TreeCellMode.Check);
                item.SetEditable(0, true);
                item.SetChecked(0, _ticked.Contains(row.Key));
                item.SetTooltipText(0, _tr("Tick to install it"));
            };
            _table.Tree.ItemEdited += () =>
            {
                var edited = _table.Tree.GetEdited();
                if (edited != null && edited.GetMetadata(0).VariantType == Variant.Type.String) SetTicked(edited.GetMetadata(0).AsString(), edited.IsChecked(0));
            };
            _table.Activated += row => SetTicked(row.Key, !_ticked.Contains(row.Key)); // Enter or a double-click ticks too
            _pick.AddChild(_table);
            _totals = Wrap(Kit.Text(""));
            _totals.SetMeta("add_games_totals", true);
            _pick.AddChild(_totals);
        }

        /// <summary>Reads the games (as it opens, and again after an engine change): yours and the preselected game now, the rest a few per frame. Ticks stay where there's still something to add.</summary>
        public void Start()
        {
            int generation = ++_generation;
            _source = _hooks.Source();
            _facts.Clear();
            _alone.Clear();
            _toRead.Clear();
            _rows.Clear();
            _rowOf.Clear();
            foreach (var entry in _source.Games.OrderByDescending(e => e.Played).ThenBy(e => e.Game, StringComparer.OrdinalIgnoreCase))
            {
                if (_rowOf.ContainsKey(entry.Game)) continue;
                var row = new AtlasTable.Row
                {
                    Key = entry.Game,
                    Pinned = entry.Played,
                    Cells = new[]
                    {
                        new AtlasTable.Cell(""),
                        new AtlasTable.Cell(entry.Game, null, entry.Played ? _tr("A game of your multiworlds") : null) { Icon = entry.Played ? LucideTextures.Get("users", ThemeColors.TextMuted, 0.7f) : null },
                        new AtlasTable.Cell("…", ThemeColors.TextSubtle),
                        new AtlasTable.Cell("", ThemeColors.TextMuted, null, 0L)
                    }
                };
                _rows.Add(row);
                _rowOf[entry.Game] = row;
            }
            foreach (var row in _rows.Where(r => r.Pinned)) Read(row.Key);
            foreach (var row in _rows.Where(r => r.Pinned && Tickable(r.Key)))
                if (_preTicked.Add(row.Key)) _ticked.Add(row.Key);
            foreach (string game in _ticked.ToList())
            {
                if (!_rowOf.TryGetValue(game, out var row)) { _ticked.Remove(game); continue; }
                if (!_facts.ContainsKey(row.Key)) Read(row.Key);
                if (!Tickable(row.Key)) _ticked.Remove(game);
            }
            foreach (var row in _rows.Where(r => !_facts.ContainsKey(r.Key))) _toRead.Enqueue(row.Key);
            _engineLead.Visible = !_source.Context.EngineReady;
            _note.Visible = false;
            _table.SetRows(_rows);
            ApplyPreselect();
            RefreshTotals();
            if (_toRead.Count > 0) Ui.NextFrame(this, () => Pump(generation), "reading what each game needs");
        }

        /// <summary>Facts read for the rest, a frame's budget at a time (a game's apworld copies, its world, its guide).</summary>
        private void Pump(int generation)
        {
            if (generation != _generation) return;
            var clock = Stopwatch.StartNew();
            bool read = false;
            while (_toRead.Count > 0 && clock.ElapsedMilliseconds < FrameBudgetMs)
            {
                string game = _toRead.Dequeue();
                if (_facts.ContainsKey(game)) continue;
                Read(game);
                read = true;
            }
            if (read) _table.SetRows(_rows);
            if (_toRead.Count > 0) Ui.NextFrame(this, () => Pump(generation), "reading what each game needs");
        }

        private void Read(string game)
        {
            var facts = SafeFacts(_source, game);
            _facts[game] = facts;
            _alone[game] = AddGamesPlan.Make(new[] { facts }, _source.Context).Games.FirstOrDefault() ?? new GamePlan(game, AddRow.NothingKnown, Array.Empty<AddItem>());
            FillRow(game);
        }

        private static AddGameFacts SafeFacts(AddGamesSource source, string game)
        {
            try { return source.Facts(game); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
            {
                AP_Atlas.Core.Logger.LogWarning($"[Add games] What {game} needs couldn't be read: {ex.Message}");
                return new AddGameFacts { Game = game };
            }
        }

        /// <summary>A game with something to add (one not read yet counts: it's read when ticked).</summary>
        private bool Tickable(string game) => !_alone.TryGetValue(game, out var plan) || plan.Items.Count > 0;

        /// <summary>A row's words and size: its part of the ticked plan when ticked (GitHub's allowance is shared), else its own.</summary>
        private void FillRow(string game)
        {
            if (!_rowOf.TryGetValue(game, out var row) || !_alone.TryGetValue(game, out var alone)) return;
            var shown = _ticked.Contains(game) ? _tickedPlan.Games.FirstOrDefault(g => string.Equals(g.Game, game, StringComparison.OrdinalIgnoreCase)) ?? alone : alone;
            bool tickable = alone.Items.Count > 0;
            row.Dim = !tickable;
            string explain = _tr(AddGamesPlan.Explain(shown)) + (AddGamesWords.VersionWhy(shown, _tr) is { } why ? "\n" + why : "");
            row.Cells[GetsCell] = new AtlasTable.Cell(_tr(AddGamesPlan.Words(shown)), tickable ? null : ThemeColors.TextSubtle, explain);
            bool release = shown.Items.Any(i => i.Kind == AddItemKind.World) && !_source.Context.WorldsZipPresent;
            var tips = new List<string>();
            if (release) tips.Add(_tr("Besides Archipelago's release ({0}), downloaded once for every game that ships with it.").Replace("{0}", AddGamesWords.Megabytes(_source.Context.WorldsZipBytes, _tr)));
            if (shown.UnknownSizes > 0) tips.Add(shown.UnknownSizes == 1 ? _tr("1 file doesn't list its size.") : _tr("{0} files don't list their size.").Replace("{0}", shown.UnknownSizes.ToString(CultureInfo.InvariantCulture)));
            row.Cells[SizeCell] = new AtlasTable.Cell(AddGamesWords.Download(shown.DownloadBytes, shown.UnknownSizes, _tr), ThemeColors.TextMuted, tips.Count > 0 ? string.Join(" ", tips) : null, shown.DownloadBytes);
        }

        private void SetTicked(string game, bool on)
        {
            if (CurrentStage != Stage.Pick || !_rowOf.TryGetValue(game, out var row)) return;
            if (!_facts.ContainsKey(row.Key)) Read(row.Key);
            if (on && Tickable(row.Key)) _ticked.Add(row.Key);
            else _ticked.Remove(row.Key);
            RefreshTotals();
            FillRow(row.Key);
            _table.SetRows(_rows);
        }

        private List<string> TickedInOrder() => _rows.Where(r => _ticked.Contains(r.Key)).Select(r => r.Key).ToList();

        private void RefreshTotals()
        {
            _tickedPlan = AddGamesPlan.Make(TickedInOrder().Select(g => _facts[g]).ToList(), _source.Context);
            foreach (var g in _tickedPlan.Games) FillRow(g.Game);
            _totals.Text = AddGamesWords.Totals(_tickedPlan, _tr);
            RaiseActions();
        }

        private void ApplyPreselect()
        {
            if (_preselect == null) return;
            if (!_rowOf.TryGetValue(_preselect, out var row))
            {
                Note(_tr("Atlas doesn't list {0} yet: add its project on the Games page (Add a game by its project link…).").Replace("{0}", _preselect));
                return;
            }
            _preselect = null; // applied once: a later read (an engine change) keeps the user's own ticks
            if (!_facts.ContainsKey(row.Key)) Read(row.Key);
            if (Tickable(row.Key)) _ticked.Add(row.Key);
            else Note(_tr("{0}: {1}").Replace("{1}", _tr(AddGamesPlan.Explain(_alone[row.Key]))).Replace("{0}", row.Key));
            _table.SetRows(_rows);
            _table.Select(row.Key, scrollTo: true);
            RefreshTotals();
        }

        private void Note(string text)
        {
            _note.Text = text;
            _note.Visible = true;
        }

        // ---- The one confirm ----

        private void GoConfirm()
        {
            // The ticked games read again: the engine, the packs or the list may have changed since their rows were read.
            var source = _hooks.Source();
            var plan = AddGamesPlan.Make(TickedInOrder().Select(g => SafeFacts(source, g)).ToList(), source.Context);
            if (!plan.EngineReady)
            {
                Start(); // the engine went: the list leads with it again
                return;
            }
            BuildConfirm(plan, Permissions.IsAllowed(_hooks.Settings, Permissions.GitHubLookups), _hooks.FreeBytes());
            GoTo(Stage.Confirm);
        }

        /// <summary>
        /// The one confirm (round 13, "don't be wordy"; the owner's layout): "Install N games · size", a line per game (its name,
        /// what it gets, its download; where each part comes from in its tooltip), "From N projects (i)" (the projects, what each
        /// is for and what installing trusts in its tooltip), GitHub's lookups when asked, the tick, and the room line when
        /// there isn't room.
        /// </summary>
        private void BuildConfirm(AddPlan plan, bool gitHubAllowed, long? free)
        {
            _plan = plan;
            Clear(_confirm);
            var words = AddGamesWords.Confirm(plan, gitHubAllowed, free, AtlasEngine.FreeSpaceFloorBytes, _tr);
            Card(_confirm, ThemeColors.AccentTint, out var box).SetMeta("add_games_confirm", true);
            var title = Kit.Heading(words.Title, 1.15f);
            title.TooltipText = words.TitleTooltip;
            title.MouseFilter = MouseFilterEnum.Pass;
            box.AddChild(title);
            // One grid for every game, so the columns line up whatever a size says ("+ Archipelago's release").
            var games = new GridContainer { Columns = 3 };
            games.AddThemeConstantOverride("h_separation", 12);
            games.AddThemeConstantOverride("v_separation", 4);
            games.SetMeta("add_games_games", true);
            foreach (var game in words.Games) ConfirmLine(games, game);
            box.AddChild(games);
            var projects = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass, TooltipText = words.ProjectsTooltip };
            projects.AddThemeConstantOverride("separation", 6);
            projects.SetMeta("add_games_projects", true);
            var projectsText = Kit.Text(words.Projects);
            projectsText.AutowrapMode = TextServer.AutowrapMode.Off;
            projects.AddChild(projectsText);
            projects.AddChild(Icon("info", ThemeColors.TextMuted));
            box.AddChild(projects);
            if (words.GitHub != null) box.AddChild(IconLine("globe", ThemeColors.TextMuted, Kit.Muted(words.GitHub), null));
            _dontAsk = new CheckBox
            {
                Text = _tr("Don't ask again for Atlas's downloads"),
                TooltipText = _tr("Archipelago's release and the packages its games declare download without asking from now on; Settings → Privacy & permissions takes it back. The projects are still named each time."),
                Visible = !Permissions.IsAlwaysAllowed(_hooks.Settings, Permissions.EngineSetup)
            };
            _dontAsk.SetMeta("add_games_dont_ask", true);
            box.AddChild(_dontAsk);
            _confirmOk = words.Room == null;
            if (words.Room != null) box.AddChild(CardLine(Kit.Text(words.Room, ThemeColors.Error)));
            ConfirmText = words.AllText;
        }

        /// <summary>A game's line of the confirm, three cells of the grid: its name, what it gets and its download, the name and what it
        /// gets cut with "…" if long (each cell's tooltip has them whole, and the details).</summary>
        private void ConfirmLine(GridContainer grid, AddConfirmGame game)
        {
            string tooltip = game.Game + " · " + game.Gets + " · " + game.Size + "\n" + game.Tooltip;
            var name = OneLine(Kit.Text(game.Game), expand: true);
            var gets = OneLine(Kit.Muted(game.Gets), expand: false);
            gets.CustomMinimumSize = new Vector2(Narrow ? 200 : 230, 0);
            // The size is never cut: the game's name gives way instead.
            var size = Kit.Muted(game.Size);
            size.AutowrapMode = TextServer.AutowrapMode.Off;
            size.SizeFlagsHorizontal = SizeFlags.Fill;
            size.HorizontalAlignment = HorizontalAlignment.Right;
            foreach (var cell in new[] { name, gets, size })
            {
                cell.MouseFilter = MouseFilterEnum.Pass;
                cell.TooltipText = tooltip;
                cell.SetMeta("add_games_game_line", game.Game);
                grid.AddChild(cell);
            }
        }

        // ---- The run ----

        private void BuildRunning()
        {
            _runningTitle = Kit.Heading("", 1.15f);
            _running.AddChild(_runningTitle);
            // The step in a few words, with dots that move while a step runs with no bar movement of its own (the engine's
            // check, the packs' checks), so it never looks frozen (round 13). What Cancel keeps is its tooltip (PrimaryTooltip).
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 8);
            _busy = new BusyDots(_tr("Working"));
            line.AddChild(_busy);
            _status = Kit.Text("");
            _status.CustomMinimumSize = new Vector2(_width - 60, 0);
            line.AddChild(_status);
            _running.AddChild(line);
            _bar = new ProgressBar { MaxValue = 1, Step = 0.001, ShowPercentage = true, CustomMinimumSize = new Vector2(0, 18) };
            _running.AddChild(_bar);
        }

        private void Run()
        {
            var plan = _plan;
            if (plan == null || GameAdder.Busy) return;
            var consent = new AddGamesConsent(_dontAsk is { Visible: true, ButtonPressed: true }, plan.NeedsGitHubLookups);
            _cts = new CancellationTokenSource();
            _cancelling = false;
            _runningTitle.Text = RunningTitle(AddGamesWords.Adding(plan));
            _status.Text = _tr("Starting…");
            _bar.Value = 0;
            GoTo(Stage.Running);
            _busy.Running = true;
            Async.Fire(RunAsync(plan, consent, _cts.Token), "adding games");
        }

        private string RunningTitle(int games) => games == 1 ? _tr("Installing 1 game") : _tr("Installing {0} games").Replace("{0}", games.ToString(CultureInfo.InvariantCulture));

        private async Task RunAsync(AddPlan plan, AddGamesConsent consent, CancellationToken ct)
        {
            var run = await _hooks.RunAsync(plan, consent, OnStatus, ct);
            var cts = _cts;
            _cts = null;
            cts?.Dispose();
            _cancelling = false;
            Result = run;
            _hooks.BatchEnded?.Invoke(run);
            if (IsInstanceValid(this) && !IsQueuedForDeletion())
            {
                BuildSummary(run);
                GoTo(Stage.Summary);
            }
            Finished?.Invoke(run);
        }

        private void OnStatus(AddGamesStatus status)
        {
            if (IsInstanceValid(this))
            {
                if (!_cancelling || status.Fraction >= 1f) _status.Text = status.Text;
                _bar.Value = status.Fraction;
                // The dots stand for work the bar doesn't show: a step begins (or says where it is) with them; a download's
                // bar moving within its step stops them (round 13).
                _busy.Running = CurrentStage == Stage.Running && !status.BarMoves && status.Fraction < 1f;
            }
            StatusChanged?.Invoke(status);
        }

        // ---- The summary ----

        private void BuildSummary(GameAdderRun run)
        {
            Clear(_summary);
            var o = run.Outcome;
            var heading = Kit.Heading(AddGamesWords.Heading(o, _tr), 1.15f);
            heading.SetMeta("add_games_headline", true);
            if (AddGamesWords.HeadingTooltip(o, _tr) is { } why)
            {
                heading.TooltipText = why;
                heading.MouseFilter = MouseFilterEnum.Pass;
            }
            _summary.AddChild(heading);
            if (!string.IsNullOrWhiteSpace(o.EngineProblem))
                _summary.AddChild(Wrap(Kit.Text(_tr("The Atlas Engine's health check said: {0}").Replace("{0}", o.EngineProblem), ThemeColors.Warning)));
            // A line per game (round 13), then the tip. The list is as tall as its lines once laid out (measured next frame;
            // WindowFit re-sizes the dialog when the content's minimum changes), up to the most; only past that does it scroll.
            var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 8);
            var scroll = new ScrollContainer
            {
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, Math.Min(SummaryMostHeight, 40 * Math.Max(1, o.Games.Count) + 40))
            };
            scroll.AddChild(list);
            _summary.AddChild(scroll);
            foreach (var game in o.Games)
                list.AddChild(ResultRow(game, o.PacksToLook.FirstOrDefault(p => string.Equals(p.Game, game.Game, StringComparison.OrdinalIgnoreCase))));
            if (o.MapsAdded > 0)
            {
                var tipText = Kit.Muted(_tr(AddGamesWords.Tip));
                tipText.SetMeta("add_games_line", true);
                var tip = IconLine("lightbulb", ThemeColors.TextMuted, tipText, _tr(MapTrackerControl.FixTipText));
                tip.SetMeta("add_games_tip", true);
                list.AddChild(tip);
            }
            Ui.NextFrame(this, () => { if (IsInstanceValid(scroll)) scroll.CustomMinimumSize = new Vector2(0, Math.Min(SummaryMostHeight, list.GetCombinedMinimumSize().Y)); }, "sizing the Install games summary");
        }

        /// <summary>
        /// A game's line (the owner's "✓ logic 0.6.0-a2 · ✓ map 1250/1288 · ✓ 35 fixes [Review]"): its name, then one item each
        /// for its logic, its map (the game's checks with a pin) and what Atlas fixed, one number each, the details in their
        /// tooltips; the way on beside the item it's for: Review, Find the version that fits… (a map under three quarters
        /// pinned), Find one… (no map), and the guide's own project when that wasn't installed.
        /// </summary>
        private Control ResultRow(AddGameResult r, PackToLook? look)
        {
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 12);
            row.SetMeta("add_games_result", r.Game);
            // The name whole, at least NameWidth so short names line up; a long one widens the row and the items wrap.
            var name = Kit.Text(r.Game);
            name.AutowrapMode = TextServer.AutowrapMode.Off;
            name.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            name.CustomMinimumSize = new Vector2(NameWidth, 0);
            row.AddChild(name);
            var items = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            items.AddThemeConstantOverride("h_separation", 16);
            items.AddThemeConstantOverride("v_separation", 4);
            string game = r.Game;
            // A game that ships with Archipelago has no versions of its own to switch to (round 13's review).
            bool official = r.LogicShips || AtlasEngine.Worlds.FolderFor(game) != null;
            items.AddChild(Item(AddGamesWords.LogicChip(r, _tr), "logic"));
            var map = Item(AddGamesWords.MapChip(r, look, _tr, official), "map");
            if (look != null && !official && r.Map.Outcome == AddStepOutcome.Done && AddGamesWords.PoorFit(look))
            {
                var fits = Kit.Button(_tr("Find the version that fits…"), _tr("Measures this map pack against the game's newest releases on its page (asked first), so you can switch to the one it fits."),
                    () => _hooks.FindVersion(game), small: true);
                fits.SetMeta("find_version", game);
                map.AddChild(fits);
            }
            else if (r.Map.Outcome is AddStepOutcome.Unavailable or AddStepOutcome.Failed)
            {
                var find = Kit.Button(_tr("Find one…"), _tr("One search of GitHub for this game's PopTracker packs, on its page; you choose what to install."), () => _hooks.FindPack(game), small: true);
                find.SetMeta("find_pack", game);
                map.AddChild(find);
            }
            items.AddChild(map);
            // The guide's own project, an item of its own so it can wrap to its own line (the setup window's page is narrow).
            if (r.GuideRepo is { } repo && r.PackNote != AddPackNote.None)
            {
                var guide = Kit.Button(_tr("Install github.com/{0}…").Replace("{0}", repo), AddGamesWords.GuideTip(r, _tr), () => _hooks.InstallPack(game, repo), small: true);
                guide.SetMeta("install_guide_pack", game);
                items.AddChild(guide);
            }
            if (AddGamesWords.FixedChip(look, _tr) is { } fixedChip)
            {
                var fixedItem = Item(fixedChip, "fixed");
                if (_hooks.ReviewFixes is { } review)
                {
                    string key = look!.PackKey, file = look.File;
                    var button = Kit.Button(_tr("Review"), _tr("What Atlas changed in this pack, each with Undo, and Undo all."), () => review(key, file), flat: true, small: true);
                    button.SetMeta("review_fixes", key);
                    fixedItem.AddChild(button);
                }
                items.AddChild(fixedItem);
            }
            row.AddChild(items);
            return row;
        }

        /// <summary>An item of a game's line: its state's icon (a tick, a dot to look at, a cross, a dash), its few words, the details as its tooltip.</summary>
        private HBoxContainer Item(AddChip chip, string kind)
        {
            var (icon, color, state) = chip.State switch
            {
                AddChipState.Ok => ("circle-check", ThemeColors.Success, _tr("done")),
                AddChipState.Already => ("circle-check", ThemeColors.TextMuted, _tr("there already")),
                AddChipState.Warn => ("circle-dot", ThemeColors.Warning, _tr("worth a look")),
                AddChipState.Bad => ("circle-x", ThemeColors.Error, _tr("failed")),
                _ => ("circle-minus", ThemeColors.TextMuted, _tr("not done"))
            };
            var item = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass, TooltipText = chip.Tooltip ?? "", AccessibilityName = chip.Text + ", " + state };
            item.AddThemeConstantOverride("separation", 4);
            item.SetMeta("add_games_chip", kind);
            item.SetMeta("add_games_state", chip.State.ToString());
            item.AddChild(Icon(icon, color));
            var text = Kit.Text(chip.Text, chip.State is AddChipState.Already or AddChipState.Skipped ? ThemeColors.TextMuted : ThemeColors.Text);
            text.AutowrapMode = TextServer.AutowrapMode.Off;
            text.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            text.SetMeta("add_games_line", true);
            item.AddChild(text);
            return item;
        }

        private static TextureRect Icon(string name, Color color) => new()
        {
            Texture = LucideTextures.Get(name, color, 0.75f),
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(20, 0)
        };

        /// <summary>A label kept to one line, cut with "…" where it doesn't fit (its line's tooltip has it whole).</summary>
        private static Label OneLine(Label label, bool expand)
        {
            label.AutowrapMode = TextServer.AutowrapMode.Off;
            label.ClipText = true;
            label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            label.SizeFlagsHorizontal = expand ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin;
            return label;
        }

        /// <summary>An icon and a line of text (wrapping at the panel's width), with a tooltip for the details.</summary>
        private HBoxContainer IconLine(string icon, Color color, Label label, string? tooltip)
        {
            var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass, TooltipText = tooltip ?? "" };
            line.AddThemeConstantOverride("separation", 8);
            line.AddChild(Icon(icon, color));
            label.CustomMinimumSize = new Vector2(_width - 80, 0);
            line.AddChild(label);
            return line;
        }

        /// <summary>A line inside a card, wrapped at the card's inner width (a card's margins and border take about 30).</summary>
        private Label CardLine(Label label)
        {
            label.CustomMinimumSize = new Vector2(_width - 40, 0);
            return label;
        }

        private void TryAgain()
        {
            var again = Result?.Outcome.Games.Where(g => !g.Ok).Select(g => g.Game).ToList() ?? new List<string>();
            _ticked.Clear();
            foreach (string game in again) _ticked.Add(game);
            GoTo(Stage.Pick); // a game asked for meanwhile is ticked too
            Start(); // read again: what was added is ready now; what's left keeps its tick
        }

        // ---- Parts ----

        private void GoTo(Stage stage)
        {
            CurrentStage = stage;
            if (stage != Stage.Running) _busy.Running = false; // the dots stop with the run: nothing animates once nothing runs
            _pick.Visible = stage == Stage.Pick;
            _confirm.Visible = stage == Stage.Confirm;
            _running.Visible = stage == Stage.Running;
            _summary.Visible = stage == Stage.Summary;
            RaiseActions();
        }

        private void RaiseActions() => ActionsChanged?.Invoke();

        private VBoxContainer Part()
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, Visible = false };
            box.AddThemeConstantOverride("separation", 8);
            AddChild(box);
            return box;
        }

        private static PanelContainer Card(Container parent, Color border, out VBoxContainer box)
        {
            var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = ThemeColors.SurfaceSunken,
                BorderColor = border,
                BorderWidthLeft = 3,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft = 12,
                ContentMarginRight = 12,
                ContentMarginTop = 10,
                ContentMarginBottom = 10
            });
            box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            panel.AddChild(box);
            parent.AddChild(panel);
            return panel;
        }

        /// <summary>Wrapped at the panel's width from the first measure (a popped dialog sizes to it).</summary>
        private Label Wrap(Label label)
        {
            label.CustomMinimumSize = new Vector2(_width - 20, 0);
            return label;
        }

        private static void Clear(Node box)
        {
            foreach (Node child in box.GetChildren())
            {
                box.RemoveChild(child);
                child.QueueFree();
            }
        }

        // ---- For tests ----

        public AtlasTable Table => _table;
        public IReadOnlyList<string> Ticked => TickedInOrder();
        public bool IsTicked(string game) => _ticked.Contains(game);
        /// <summary>Ticks or unticks a game, as its check box does.</summary>
        public void Tick(string game, bool on) => SetTicked(game, on);
        public bool RowTickable(string game) => _rowOf.TryGetValue(game, out var row) && Tickable(row.Key);
        public string RowWords(string game) => _rowOf.TryGetValue(game, out var row) ? row.Cells[GetsCell].Text : "";
        public string RowSize(string game) => _rowOf.TryGetValue(game, out var row) ? row.Cells[SizeCell].Text : "";
        /// <summary>Games whose facts aren't read yet (their rows say "…").</summary>
        public int FactsPending => _toRead.Count;
        public string TotalsText => _totals.Text;
        public bool EngineLeadShown => _engineLead.Visible;
        public string NoteText => _note.Visible ? _note.Text : "";
        /// <summary>The confirm's lines, one per line.</summary>
        public string ConfirmText { get; private set; } = "";
        public CheckBox? DontAskAgain => _dontAsk;
        public AddPlan? ConfirmedPlan => _plan;
        public string StatusText => _status.Text;
        public double ProgressValue => _bar.Value;
        public bool BusyRunning => _busy.Running;
        public bool BusyAnimating => _busy.Animating;
        /// <summary>A summary item's tooltip ("logic", "map", "fixed") for a game, or null.</summary>
        public string? ChipTooltip(string game, string kind) => SummaryItem(game, kind)?.TooltipText;
        /// <summary>A summary item's state ("Ok", "Warn", …) for a game, or null.</summary>
        public string? ChipState(string game, string kind) => SummaryItem(game, kind)?.GetMeta("add_games_state").AsString();
        private HBoxContainer? SummaryItem(string game, string kind) =>
            _summary.FindChildren("*", nameof(HBoxContainer), true, false).OfType<HBoxContainer>()
                .FirstOrDefault(c => c.HasMeta("add_games_chip") && c.GetMeta("add_games_chip").AsString() == kind && ResultOf(c) == game);
        private static string? ResultOf(Node node)
        {
            for (var n = node.GetParent(); n != null; n = n.GetParent())
                if (n.HasMeta("add_games_result")) return n.GetMeta("add_games_result").AsString();
            return null;
        }
        /// <summary>The confirm's line per game, as shown ("name · gets · size").</summary>
        public List<string> ConfirmLines() =>
            _confirm.FindChildren("*", nameof(GridContainer), true, false).OfType<GridContainer>().Where(g => g.HasMeta("add_games_games"))
                .SelectMany(g => g.GetChildren().OfType<Label>().Chunk(3)).Select(cells => string.Join(" · ", cells.Select(l => l.Text))).ToList();
        public GameAdderRun? Result { get; private set; }
        public List<string> SummaryLines() =>
            _summary.FindChildren("*", nameof(Label), true, false).OfType<Label>().Where(l => l.HasMeta("add_games_line") || l.HasMeta("add_games_headline")).Select(l => l.Text).ToList();
        /// <summary>The summary of a made-up batch (the visual check's picture); nothing runs.</summary>
        internal void ShowSummaryForTests(GameAdderRun run)
        {
            Result = run;
            BuildSummary(run);
            GoTo(Stage.Summary);
        }

        /// <summary>The confirm for a made-up plan (the visual check's picture): GitHub's lookups not yet allowed, the disk's room unknown; nothing runs.</summary>
        internal void ShowConfirmForTests(AddPlan plan)
        {
            BuildConfirm(plan, gitHubAllowed: false, free: null);
            GoTo(Stage.Confirm);
        }

        /// <summary>The running stage at a made-up step (the visual check's picture, the dots' check): nothing runs; <paramref name="holdStill"/> holds the dots still for a stable picture.</summary>
        internal void ShowRunningForTests(int games, AddGamesStatus status, bool holdStill = true)
        {
            _runningTitle.Text = RunningTitle(games);
            GoTo(Stage.Running);
            OnStatus(status);
            if (holdStill) _busy.HoldStill();
        }

        /// <summary>A made-up step's line, as a batch reports it (the dots' check).</summary>
        internal void StatusForTests(AddGamesStatus status) => OnStatus(status);
    }
}
