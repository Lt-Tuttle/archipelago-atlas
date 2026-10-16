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
    /// your multiworlds first and ticked where they need something, each row saying in plain words what adding it gets and
    /// its download, a search, and the totals of download and disk. Then ONE confirm naming every project the downloads
    /// come from, what pressing it trusts, and the tick "Don't ask again for Atlas's downloads"; then one bar while
    /// <see cref="GameAdder"/> runs (Cancel stops after the step it's on); then a summary per game, with what to do where
    /// something's missing, the Pack Doctor's "fix them now?" (<see cref="Hooks.OfferFixes"/>) and the tip about fixing on the
    /// map. It leads with the Atlas Engine while that isn't set up. A control, so the setup window holds it on a page;
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
            /// <summary>Installs a project's pack for a game, asking first (the guide's project Atlas's list lacks).</summary>
            public required Action<string, string> InstallPack { get; init; }
            /// <summary>Start a pack for a game (its names come from a connected slot).</summary>
            public required Action<string> StartPack { get; init; }
            /// <summary>Free bytes where Atlas keeps its data (null: unknown).</summary>
            public required Func<long?> FreeBytes { get; init; }
            /// <summary>The Pack Doctor's "fix them now?" for the packs a batch found things in, built into the summary (null: a line says where they wait).</summary>
            public Action<VBoxContainer, GameAdderRun>? OfferFixes { get; init; }
            /// <summary>A batch ended (the window's pages read again).</summary>
            public Action<GameAdderRun>? BatchEnded { get; init; }
        }

        public enum Stage { Pick, Confirm, Running, Summary }

        /// <summary>The most one frame spends reading games' facts; the rest wait for the next frame.</summary>
        private const long FrameBudgetMs = 6;
        private const int GetsCell = 2, SizeCell = 3;

        private readonly Hooks _hooks;
        private readonly Func<string, string> _tr;
        private readonly int _width;
        private readonly VBoxContainer _pick, _confirm, _running, _summary;
        private PanelContainer _engineLead = null!;
        private Label _note = null!, _totals = null!, _runningTitle = null!, _status = null!;
        private AtlasTable _table = null!;
        private ProgressBar _bar = null!;
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
        // The summary's "fix them now?" while it asks: the host's two buttons carry its Fix them now… and Later.
        private PackFixHandoff? _handoff;
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

        public AddGamesPanel(Hooks hooks, string? preselect = null, int width = 720)
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
                0 => _tr("Add games…"),
                1 => _tr("Add 1 game…"),
                int n => _tr("Add {0} games…").Replace("{0}", n.ToString(CultureInfo.InvariantCulture))
            },
            Stage.Confirm => _tr("Download and install"),
            Stage.Running => _cancelling ? _tr("Stopping…") : _tr("Cancel"),
            _ => HandoffAsks ? _tr("Fix them now…") : _preselect != null ? _tr("Add {0}…").Replace("{0}", _preselect) : _tr("Done")
        };

        public bool PrimaryEnabled => CurrentStage switch
        {
            Stage.Pick => AddGamesWords.Adding(_tickedPlan) > 0 && _source.Context.EngineReady && !GameAdder.Busy,
            Stage.Confirm => _confirmOk && !GameAdder.Busy,
            Stage.Running => !_cancelling,
            _ => true
        };

        /// <summary>
        /// The second action, or null for none: Cancel on the list, Back on the confirm; on the summary, Later while the Pack
        /// Doctor's question asks, then Try those again when games weren't done.
        /// </summary>
        public string? SecondaryText => CurrentStage switch
        {
            Stage.Pick => _tr("Cancel"),
            Stage.Confirm => _tr("Back"),
            Stage.Summary when HandoffAsks => _tr("Later"),
            Stage.Summary when Result?.Outcome.Games.Any(g => !g.Ok) == true => _tr("Try those again"),
            _ => null
        };

        /// <summary>How many games the ticks add (0: nothing to add, and a host may move on).</summary>
        public int AddingCount => AddGamesWords.Adding(_tickedPlan);

        /// <summary>Whether the summary's Pack Doctor question still asks (its Fix them now… and Later are the host's actions).</summary>
        public bool Asking => HandoffAsks;

        public void PressPrimary()
        {
            if (!PrimaryEnabled) return;
            switch (CurrentStage)
            {
                case Stage.Pick: GoConfirm(); break;
                case Stage.Confirm: Run(); break;
                case Stage.Running: Cancel(); break;
                case Stage.Summary when HandoffAsks: _handoff!.PressFixThemNow(); break;
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
                case Stage.Summary when HandoffAsks: _handoff!.PressLater(); break;
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

        /// <summary>Whether the summary's Pack Doctor question still asks (its Fix them now… and Later are the host's buttons).</summary>
        private bool HandoffAsks => CurrentStage == Stage.Summary && _handoff != null && IsInstanceValid(_handoff) && _handoff.CanFix;

        // ---- The list ----

        private void BuildPick()
        {
            _engineLead = Card(_pick, ThemeColors.Warning, out var lead);
            _engineLead.SetMeta("add_games_engine_lead", true);
            lead.AddChild(Wrap(Kit.Text(_tr("Games' logic runs in the Atlas Engine, which isn't set up yet. Set it up first: what you tick stays ticked, and Add follows once it's ready."))));
            var setUp = Kit.Button(_tr("Set up the Atlas Engine…"), _tr("Sets up the logic engine in Atlas's own folder (asked first)."), _hooks.OpenEngineSetup);
            setUp.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            lead.AddChild(setUp);
            _pick.AddChild(Wrap(Kit.Muted(_tr("Tick the games you play: Atlas adds each one's logic and a map pack, with one question first. The games of your multiworlds come first (marked), ticked where they need something; a game with nothing to add is greyed."))));
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
            // Hosted on a narrower page (the setup window), the game and its words give way (clipped) before the download does.
            bool narrow = _width < 720;
            _table.SetColumns(new[]
            {
                new AtlasTable.Column { Id = "tick", Title = "", MinWidth = 34, Ratio = 0, Sortable = false },
                new AtlasTable.Column { Id = "game", Title = _tr("Game"), MinWidth = narrow ? 140 : 180, Ratio = 3 },
                new AtlasTable.Column { Id = "gets", Title = _tr("Adding it gets"), MinWidth = narrow ? 150 : 220, Ratio = 4 },
                new AtlasTable.Column { Id = "size", Title = _tr("Download"), MinWidth = 110, Ratio = 0, Align = HorizontalAlignment.Right }
            }, "game");
            // A check box per row with something to add (the versions table's way); a game with nothing to add has none.
            _table.Customize = (item, row) =>
            {
                item.SetText(0, "");
                if (!Tickable(row.Key)) return;
                item.SetCellMode(0, TreeItem.TreeCellMode.Check);
                item.SetEditable(0, true);
                item.SetChecked(0, _ticked.Contains(row.Key));
                item.SetTooltipText(0, _tr("Tick to add it"));
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
            row.Cells[GetsCell] = new AtlasTable.Cell(_tr(AddGamesPlan.Words(shown)), tickable ? null : ThemeColors.TextSubtle);
            bool release = shown.Items.Any(i => i.Kind == AddItemKind.World) && !_source.Context.WorldsZipPresent;
            row.Cells[SizeCell] = new AtlasTable.Cell(AddGamesWords.Download(shown.DownloadBytes, shown.UnknownSizes, _tr), ThemeColors.TextMuted,
                release ? _tr("Besides Archipelago's release ({0}), downloaded once for every game that ships with it.").Replace("{0}", AddGamesWords.Megabytes(_source.Context.WorldsZipBytes, _tr)) : null,
                shown.DownloadBytes);
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
                Note(_tr("Atlas doesn't list {0} yet: add its project on the Games page (Add a game's project…).").Replace("{0}", _preselect));
                return;
            }
            _preselect = null; // applied once: a later read (an engine change) keeps the user's own ticks
            if (!_facts.ContainsKey(row.Key)) Read(row.Key);
            if (Tickable(row.Key)) _ticked.Add(row.Key);
            else Note(_tr("{0}: {1}.").Replace("{1}", _tr(AddGamesPlan.Words(_alone[row.Key]))).Replace("{0}", row.Key));
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
            _plan = plan;
            Clear(_confirm);
            var words = AddGamesWords.Confirm(plan, Permissions.IsAllowed(_hooks.Settings, Permissions.GitHubLookups), _hooks.FreeBytes(), AtlasEngine.FreeSpaceFloorBytes, _tr);
            Card(_confirm, ThemeColors.AccentTint, out var box).SetMeta("add_games_confirm", true);
            box.AddChild(Kit.Heading(words.Title, 1.15f));
            box.AddChild(Wrap(Kit.Text(words.Lead)));
            foreach (string line in words.Sources) box.AddChild(Wrap(Kit.Muted("•  " + line)));
            if (words.GitHub != null) box.AddChild(Wrap(Kit.Muted("•  " + words.GitHub)));
            box.AddChild(Wrap(Kit.Text(words.Trust)));
            _dontAsk = new CheckBox
            {
                Text = _tr("Don't ask again for Atlas's downloads"),
                TooltipText = _tr("Archipelago's release and the packages its games declare download without asking from now on; Settings → Privacy & permissions takes it back. The projects are still named each time."),
                Visible = !Permissions.IsAlwaysAllowed(_hooks.Settings, Permissions.EngineSetup)
            };
            _dontAsk.SetMeta("add_games_dont_ask", true);
            box.AddChild(_dontAsk);
            _confirmOk = words.Room == null;
            if (words.Room != null) box.AddChild(Wrap(Kit.Text(words.Room, ThemeColors.Error)));
            ConfirmText = string.Join("\n", new[] { words.Title, words.Lead }.Concat(words.Sources).Append(words.GitHub ?? "").Append(words.Trust).Append(words.Room ?? "").Where(s => s.Length > 0));
            GoTo(Stage.Confirm);
        }

        // ---- The run ----

        private void BuildRunning()
        {
            _runningTitle = Kit.Heading("", 1.15f);
            _running.AddChild(_runningTitle);
            _status = Wrap(Kit.Text(""));
            _running.AddChild(_status);
            _bar = new ProgressBar { MaxValue = 1, Step = 0.001, ShowPercentage = true, CustomMinimumSize = new Vector2(0, 18) };
            _running.AddChild(_bar);
            _running.AddChild(Wrap(Kit.Muted(_tr("Atlas stays usable meanwhile. Cancel stops after the step it's on: nothing half-done is kept, and what's done stays."))));
        }

        private void Run()
        {
            var plan = _plan;
            if (plan == null || GameAdder.Busy) return;
            var consent = new AddGamesConsent(_dontAsk is { Visible: true, ButtonPressed: true }, plan.NeedsGitHubLookups);
            _cts = new CancellationTokenSource();
            _cancelling = false;
            int games = AddGamesWords.Adding(plan);
            _runningTitle.Text = games == 1 ? _tr("Adding 1 game") : _tr("Adding {0} games").Replace("{0}", games.ToString(CultureInfo.InvariantCulture));
            _status.Text = _tr("Starting…");
            _bar.Value = 0;
            GoTo(Stage.Running);
            Async.Fire(RunAsync(plan, consent, _cts.Token), "adding games");
        }

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
            StatusChanged?.Invoke(status);
            if (!IsInstanceValid(this)) return;
            if (!_cancelling || status.Fraction >= 1f) _status.Text = status.Text;
            _bar.Value = status.Fraction;
        }

        // ---- The summary ----

        private void BuildSummary(GameAdderRun run)
        {
            Clear(_summary);
            _handoff = null;
            var o = run.Outcome;
            _summary.AddChild(Kit.Heading(o.Cancelled ? _tr("Stopped") : _tr("Games added"), 1.15f));
            var headline = Wrap(Kit.Text(AddGamesWords.Headline(o, _tr)));
            headline.SetMeta("add_games_headline", true);
            _summary.AddChild(headline);
            if (!string.IsNullOrWhiteSpace(o.EngineProblem))
                _summary.AddChild(Wrap(Kit.Text(_tr("The Atlas Engine's health check said: {0}").Replace("{0}", o.EngineProblem), ThemeColors.Warning)));
            // Everything else scrolls as one (a row per game, the Pack Doctor's question, the tip): its labels can't make the dialog tall.
            int height = 72 * Math.Max(1, o.Games.Count) + (run.Fixes.Count > 0 ? 120 + 36 * run.Fixes.Count : 0) + (o.MapsAdded > 0 ? 48 : 0);
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, Math.Min(420, height)) };
            var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 10);
            scroll.AddChild(list);
            _summary.AddChild(scroll);
            foreach (var game in o.Games) list.AddChild(ResultRow(game));
            if (run.Fixes.Count > 0)
            {
                var handoff = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                handoff.SetMeta("add_games_handoff", true);
                list.AddChild(handoff);
                if (_hooks.OfferFixes != null)
                {
                    _hooks.OfferFixes(handoff, run);
                    // Its question is answered with the host's buttons: they're always in sight, the scrolled list isn't.
                    _handoff = handoff.GetChildren().OfType<PackFixHandoff>().FirstOrDefault();
                    if (_handoff != null)
                    {
                        _handoff.HideOwnButtons();
                        _handoff.StateChanged += RaiseActions;
                    }
                }
                else handoff.AddChild(Line(run.Fixes.Count == 1 ? _tr("Atlas found things to look at in 1 map pack: its row under Map packs on its game's page has Review and Fix.")
                    : _tr("Atlas found things to look at in {0} map packs: each one's row under Map packs on its game's page has Review and Fix.").Replace("{0}", run.Fixes.Count.ToString(CultureInfo.InvariantCulture)), false));
            }
            // The closing tip, once a map came in (after the Pack Doctor's question, so it reads after the fixes).
            if (o.MapsAdded > 0)
            {
                var tip = Line(_tr(PackFixHandoff.FixTip), false);
                tip.SetMeta("add_games_tip", true);
                list.AddChild(tip);
            }
        }

        private Control ResultRow(AddGameResult r)
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 2);
            box.SetMeta("add_games_result", r.Game);
            var name = Kit.Text(r.Game);
            name.SetMeta("font_size_ratio", 1.05f);
            box.AddChild(name);
            box.AddChild(Line(AddGamesWords.LogicLine(r, _tr), Bad(r.Logic)));
            box.AddChild(Line(AddGamesWords.MapLine(r, _tr), Bad(r.Map)));
            if (AddGamesWords.GuideLine(r, _tr) is { } guide) box.AddChild(Line(guide, false));
            var buttons = new HFlowContainer();
            buttons.AddThemeConstantOverride("h_separation", 8);
            string game = r.Game;
            if (r.GuideRepo is { } repo && r.PackNote != AddPackNote.None)
                buttons.AddChild(Kit.Button(_tr("Install github.com/{0}…").Replace("{0}", repo), _tr("The pack the game's setup guide names: Atlas shows what would be downloaded first."), () => _hooks.InstallPack(game, repo), small: true));
            if (r.Map.Outcome is AddStepOutcome.Unavailable or AddStepOutcome.Failed)
            {
                buttons.AddChild(Kit.Button(_tr("Find one on GitHub…"), _tr("One search of GitHub for this game's PopTracker packs, on its page; you choose what to install."), () => _hooks.FindPack(game), small: true));
                buttons.AddChild(Kit.Button(_tr("Start a pack…"), _tr("Atlas makes one from the game's own names (a slot of it connected once gives them)."), () => _hooks.StartPack(game), flat: true, small: true));
            }
            if (buttons.GetChildCount() > 0) box.AddChild(buttons);
            return box;
        }

        private static bool Bad(AddStep step) => step.Outcome is AddStepOutcome.Failed or AddStepOutcome.Cancelled;

        private Label Line(string text, bool bad)
        {
            var label = bad ? Kit.Text(text, ThemeColors.Warning) : Kit.Muted(text);
            label.CustomMinimumSize = new Vector2(_width - 40, 0);
            label.SetMeta("add_games_line", true);
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
    }
}
