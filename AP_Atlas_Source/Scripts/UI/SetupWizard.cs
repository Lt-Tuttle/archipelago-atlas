#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using AP_Atlas.Core;

namespace AP_Atlas.UI
{
    /// <summary>
    /// A multiworld as the setup window hands it back: an existing one to update (by id) or a new one, with its slots, and
    /// the slots this window added to it earlier that the user has since corrected or removed (<paramref name="Drop"/>;
    /// a multiworld's own earlier slots are never in it).
    /// </summary>
    public sealed record SetupMultiworldDraft(string? ExistingId, string Name, string RoomLink, string Server, string Password, List<string> Slots, List<string> Drop);

    /// <summary>
    /// Setting Atlas up as a click-through (the owner's "adding games, simply"): one window, a list of pages on the left (a
    /// tick for each that's done, the current one lit, the engine's progress at the bottom) and one page at a time on the
    /// right, with Back, Skip this step and Next: Welcome; The Atlas Engine (only while a downloaded engine isn't set up);
    /// Your multiworld and slots, whose Next saves the multiworld and connects the slots so Atlas learns their games from the
    /// room; Add games (the <see cref="AddGamesPanel"/> on the page, its two actions the footer's); and Ready. Nothing opens
    /// on top of it: a permission is a row on the page, adding games asks its one question on the page, and the one thing
    /// that opens on it is the Pack Doctor's preview of every change (Fix them now…). A button that leads elsewhere puts the
    /// window out of the way; Help → Set up Atlas… or Home's Resume brings it back as it was. Resume opens it at the first
    /// step not done or skipped. Not exclusive: Atlas stays usable, and closing it keeps whatever runs (the engine's setup;
    /// an Add games batch, after which the window comes back with its summary).
    /// </summary>
    public sealed partial class SetupWizard : AcceptDialog
    {
        /// <summary>What the window needs from the main window.</summary>
        public sealed class Hooks
        {
            public required Func<string, string> Tr { get; init; }
            public required AppSettings Settings { get; init; }
            public required SetupSteps Steps { get; init; }
            // The engine.
            public required Func<bool> EngineReady { get; init; }
            public required Action<IReadOnlyList<string>> ShowEnginePage { get; init; }
            // The multiworld and its slots.
            public required Func<IReadOnlyList<MultiworldProfile>> Profiles { get; init; }
            /// <summary>The multiworld selected on the Multiworlds page, if any.</summary>
            public required Func<MultiworldProfile?> SelectedProfile { get; init; }
            /// <summary>Saves the draft as a new multiworld, or into the existing one it names, and hands it back.</summary>
            public required Func<SetupMultiworldDraft, MultiworldProfile> SaveMultiworld { get; init; }
            public required Func<string, string, bool> IsSlotLive { get; init; }
            /// <summary>Connects a slot as the Multiworlds page's Connect does and waits for the answer: whether it connected (its game is then the room's, in the saved stats).</summary>
            public required Func<MultiworldProfile, string, Task<bool>> ConnectSlotAsync { get; init; }
            /// <summary>Keeps a multiworld's slots' Logic banners quiet about a missing game (null: none): this window adds it next.</summary>
            public required Action<string?> QuietLogicFor { get; init; }
            // Add games.
            /// <summary>What the Add games screen needs from the window (its AddGamesPanelHooks); the page puts its own ways out in place of those that open something.</summary>
            public required Func<AddGamesPanel.Hooks> AddGames { get; init; }
            /// <summary>Opens the Pack Doctor on a pack's zip at a tab (a summary's Review…).</summary>
            public required Action<string, string?> OpenPackDoctor { get; init; }
            // Ready.
            /// <summary>Shows a section of the Settings page ("appearance", "tools", "updates").</summary>
            public required Action<string> ShowSettings { get; init; }
        }

        private sealed class Page
        {
            public required string Id { get; init; }
            public required string Title { get; init; }
            public SetupSteps.Step? Step { get; init; }
            public required Control Body { get; init; }
            public required Button RailButton { get; init; }
            public Action? OnEnter { get; init; }
            /// <summary>What Next does; false keeps the page (a problem is said on it).</summary>
            public Func<Task<bool>>? OnNext { get; init; }
            public string NextText { get; init; } = "Next";
        }

        private const int PageWidth = 620, RailWidth = 210;
        private readonly Hooks _hooks;
        private readonly Func<string, string> _tr;
        private readonly List<Page> _pages = new();
        private readonly VBoxContainer _pageHost = new();
        private readonly Button _back, _secondary, _skip, _next;
        private readonly EngineSetupProgress _engineRun;
        private readonly Label _railEngineTitle;
        private int _current = -1, _furthest;
        private bool _busy, _connecting, _railLocked;

        // What the pages gather.
        private MultiworldProfile? _profile;

        /// <summary>
        /// The multiworld the pages after Your multiworld and slots are about: the one saved there in this window; else
        /// (opened past it, as Resume does for a set-up Atlas) the one chosen there, the one selected on the Multiworlds
        /// page, or the one played most recently.
        /// </summary>
        private MultiworldProfile? Profile => _profile ?? ChosenExisting ?? _hooks.SelectedProfile() ?? MultiworldProfile.MostRecentFirst(_hooks.Profiles()).FirstOrDefault();

        /// <summary>Opens the window: at the Welcome page, or (resume) at the first step not done or skipped.</summary>
        public static SetupWizard Open(Node parent, Hooks hooks, bool resume)
        {
            var open = parent.GetChildren().OfType<SetupWizard>().FirstOrDefault(w => !w.IsQueuedForDeletion());
            if (open != null)
            {
                open.Show();
                open.GrabFocus();
                return open;
            }
            var wizard = new SetupWizard(hooks);
            parent.AddChild(wizard);
            WindowFit.Pop(wizard, PageWidth + RailWidth + 60, 700);
            int start = 0;
            // Resume goes to the first step left; while nothing is set up yet, that's the Welcome page.
            if (resume && !hooks.Steps.Untouched)
            {
                var next = hooks.Steps.Next;
                start = next == null ? wizard._pages.Count - 1 : wizard.PageFor(next);
            }
            wizard._furthest = Math.Max(0, start);
            wizard.Go(Math.Max(0, start));
            return wizard;
        }

        private SetupWizard(Hooks hooks)
        {
            _hooks = hooks;
            _tr = hooks.Tr;
            Title = _tr("Set up Atlas");
            Exclusive = false;
            Unresizable = false;
            DialogHideOnOk = false;
            SetMeta("setup_wizard", true);
            GetOkButton().Visible = false; // the footer's buttons lead; the window's own close is the way out
            CloseRequested += CloseOrKeepRunning;
            Canceled += CloseOrKeepRunning;

            var columns = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            columns.AddThemeConstantOverride("separation", 16);
            AddChild(columns);

            // The rail: the pages, then the engine's progress once a run starts.
            var railPanel = new PanelContainer { CustomMinimumSize = new Vector2(RailWidth, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            railPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = ThemeColors.SurfaceSunken, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 10, ContentMarginBottom = 10 });
            columns.AddChild(railPanel);
            var rail = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            rail.AddThemeConstantOverride("separation", 4);
            railPanel.AddChild(rail);
            var railTitle = new HBoxContainer();
            railTitle.AddThemeConstantOverride("separation", 8);
            railTitle.AddChild(new TextureRect { Texture = GD.Load<Texture2D>("res://Assets/Graphics/icon.png"), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, CustomMinimumSize = new Vector2(28, 28) });
            var railHeading = Kit.Heading(_tr("Set up Atlas"), 1.1f);
            railHeading.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            railTitle.AddChild(railHeading);
            rail.AddChild(railTitle);
            rail.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
            var railList = new VBoxContainer();
            railList.AddThemeConstantOverride("separation", 2);
            rail.AddChild(railList);
            rail.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
            _railEngineTitle = Kit.Muted(_tr("The Atlas Engine"));
            _railEngineTitle.Visible = false;
            rail.AddChild(_railEngineTitle);
            _engineRun = new EngineSetupProgress(_tr, RailWidth - 20) { Visible = false, CancelOnExit = false };
            _engineRun.RunChanged += _ => OnEngineRunChanged();
            rail.AddChild(_engineRun);

            // The page and the footer.
            var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(PageWidth, 0) };
            right.AddThemeConstantOverride("separation", 10);
            columns.AddChild(right);
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(PageWidth, 520) };
            right.AddChild(scroll);
            _pageHost.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _pageHost.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            scroll.AddChild(_pageHost);
            var footer = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            footer.AddThemeConstantOverride("separation", 8);
            right.AddChild(new HSeparator());
            right.AddChild(footer);
            _back = Kit.Button(_tr("Back"), _tr("The page before; what you typed stays."), () => { if (!_busy) Go(_current - 1); }, flat: true);
            footer.AddChild(_back);
            footer.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            // Add games' second action (Back to its list on the confirm, Later, Try those again), beside the window's own.
            _secondary = Kit.Button("", null, () => { if (!_busy) _addGames?.PressSecondary(); }, flat: true);
            _secondary.Name = "SecondaryAction";
            _secondary.Visible = false;
            footer.AddChild(_secondary);
            _skip = Kit.Button(_tr("Skip this step"), _tr("Counts this step as done without doing it; Atlas's setup line says it was skipped."), Skip, flat: true);
            footer.AddChild(_skip);
            _next = Kit.Button(_tr("Next"), null, () => Async.Fire(NextAsync(), "the setup window's Next"));
            _next.ThemeTypeVariation = Kit.PrimaryButton;
            footer.AddChild(_next);

            // The pages, in order; each gets its rail button.
            void AddPage(Page page)
            {
                page.Body.Visible = false;
                _pageHost.AddChild(page.Body);
                railList.AddChild(page.RailButton);
                _pages.Add(page);
            }
            Button Rail(string title, int index) => Kit.Button(_tr(title), null, () => { if (index <= _furthest && !_busy && !AddGamesRunning) Go(index); }, flat: true);
            AddPage(new Page { Id = "welcome", Title = "Welcome", Body = BuildWelcome(), RailButton = Rail("Welcome", _pages.Count), NextText = "Start" });
            // The engine's page only where Atlas downloads it (run from source) and it isn't set up: one that ships with Atlas gets ready by itself.
            if (!hooks.EngineReady() && !AP_Atlas.Core.EngineSetup.AtlasEngine.IsShipped)
                AddPage(new Page { Id = SetupSteps.Engine.Id, Title = SetupSteps.Engine.Title, Step = SetupSteps.Engine, Body = BuildEngine(), RailButton = Rail(SetupSteps.Engine.Title, _pages.Count), OnEnter = RefreshEnginePage });
            AddPage(new Page { Id = SetupSteps.Multiworld.Id, Title = SetupSteps.Multiworld.Title, Step = SetupSteps.Multiworld, Body = BuildMultiworld(), RailButton = Rail(SetupSteps.Multiworld.Title, _pages.Count), OnEnter = RefreshMultiworldPage, OnNext = MultiworldNextAsync });
            AddPage(new Page { Id = SetupSteps.Games.Id, Title = SetupSteps.Games.Title, Step = SetupSteps.Games, Body = BuildAddGames(), RailButton = Rail(SetupSteps.Games.Title, _pages.Count), OnEnter = RefreshAddGamesPage, OnNext = AddGamesNextAsync });
            AddPage(new Page { Id = "ready", Title = "Ready", Body = BuildReady(), RailButton = Rail("Ready", _pages.Count), OnEnter = RefreshReadyPage, OnNext = FinishAsync, NextText = "Finish" });
            VisibilityChanged += SyncQuiet; // after the pages: Profile reads the multiworld page
        }

        // ---- Moving between pages ----

        /// <summary>The page shown (its id).</summary>
        public string CurrentPageId => _current < 0 ? "" : _pages[_current].Id;

        private void Go(int index)
        {
            if (index < 0 || index >= _pages.Count) return;
            if (_current >= 0) _pages[_current].Body.Visible = false;
            _current = index;
            _furthest = Math.Max(_furthest, index);
            var page = _pages[index];
            page.OnEnter?.Invoke();
            page.Body.Visible = true;
            _back.Visible = index > 0;
            _skip.Visible = page.Step != null;
            _next.Text = _tr(page.NextText);
            RefreshRail();
            RefreshNext();
            WindowFit.RequestShrink(this);
            SyncQuiet();
        }

        private void RefreshRail()
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                var page = _pages[i];
                // A page that isn't a setup step (Welcome) is done once the window has gone past it.
                bool done = page.Step != null ? _hooks.Steps.Done(page.Step) : i < _furthest;
                bool skipped = page.Step != null && !done && _hooks.Steps.Skipped(page.Step);
                string icon = i == _current ? "circle-dot" : done ? "circle-check" : skipped ? "circle-check" : "circle";
                var colour = i == _current ? ThemeColors.Heading : done ? ThemeColors.Success : skipped ? ThemeColors.TextMuted : ThemeColors.TextSubtle;
                page.RailButton.Icon = LucideTextures.Get(icon, colour, 1.0f);
                page.RailButton.Alignment = HorizontalAlignment.Left;
                page.RailButton.Disabled = i > _furthest || AddGamesRunning;
                page.RailButton.TooltipText = done ? _tr("Done") : skipped ? _tr("Skipped") : i > _furthest ? _tr("Next leads here.") : "";
                if (i == _current) page.RailButton.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = ThemeColors.AccentTint, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 3, ContentMarginBottom = 3 });
                else page.RailButton.RemoveThemeStyleboxOverride("normal");
            }
        }

        private void Skip()
        {
            if (_busy) return;
            var page = _pages[_current];
            if (page.Step == null) return;
            _hooks.Steps.SetSkipped(page.Step, true);
            Go(_current + 1);
        }

        private async Task NextAsync()
        {
            if (_busy) return;
            var page = _pages[_current];
            _busy = true;
            RefreshNext(); // Next, Back and Skip wait while it works (a connect can take a login's time limit)
            try
            {
                bool go = page.OnNext == null || await page.OnNext();
                if (!IsInstanceValid(this)) return;
                if (!go) return;
                if (page.Step != null && _hooks.Steps.Done(page.Step)) _hooks.Steps.SetSkipped(page.Step, false); // a step left undone stays as it was
                if (_current == _pages.Count - 1) return; // Finish closed the window
                Go(_current + 1);
            }
            finally
            {
                _busy = false;
                if (IsInstanceValid(this) && !IsQueuedForDeletion()) RefreshNext();
            }
        }

        /// <summary>Next's state: on Add games it carries the screen's action; on the Ready page it waits for an engine run.</summary>
        private void RefreshNext()
        {
            if (_current < 0) return;
            if (OnAddGames)
            {
                SyncAddGamesButtons();
                return;
            }
            _secondary.Visible = false;
            var page = _pages[_current];
            bool waiting = page.Id == "ready" && _engineRun.Running;
            _next.Disabled = _busy || waiting;
            _back.Disabled = _skip.Disabled = _busy;
            if (!_busy) _next.Text = waiting ? _tr("Waiting for the Atlas Engine…") : _tr(page.NextText);
        }

        private void OnEngineRunChanged()
        {
            _engineRun.Visible = true;
            _railEngineTitle.Visible = true;
            if (HasEnginePage) RefreshEnginePage();
            if (_current >= 0 && _pages[_current].Id == "ready") RefreshReadyPage();
            RefreshRail();
            RefreshNext();
        }

        private bool HasEnginePage => _pages.Any(p => p.Step == SetupSteps.Engine);

        /// <summary>A step's page, or (a step this window has no page for: a shipped engine) the first page after it.</summary>
        private int PageFor(SetupSteps.Step step)
        {
            int at = _pages.FindIndex(p => p.Step == step);
            if (at >= 0) return at;
            var order = SetupSteps.All.ToList();
            at = _pages.FindIndex(p => p.Step != null && order.IndexOf(p.Step) > order.IndexOf(step));
            return at >= 0 ? at : _pages.Count - 1;
        }

        /// <summary>The window's × (or Escape): closed, unless an Add games batch runs (then hidden; it comes back with its summary).</summary>
        private void CloseOrKeepRunning()
        {
            Hide();
            if (!AddGamesRunning) QueueFree();
        }

        /// <summary>What a button leads to elsewhere: this window steps out of its way (Help → Set up Atlas… or Home's Resume brings it back as it was).</summary>
        private void OutOfTheWay(Action show)
        {
            show();
            Hide();
        }

        /// <summary>
        /// While this window connects the slots or shows Add games in sight, the multiworld's slots keep their Logic banners
        /// quiet about a game that isn't added yet: this window adds it next.
        /// </summary>
        private void SyncQuiet()
        {
            bool quiet = IsInstanceValid(this) && !IsQueuedForDeletion() && Visible && (_connecting || OnAddGames);
            _hooks.QuietLogicFor(quiet ? Profile?.Id : null);
        }

        public override void _ExitTree() => _hooks.QuietLogicFor(null);

        // ---- The shared pieces ----

        private VBoxContainer PageBox(string title, string intro)
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(PageWidth, 0) };
            box.AddThemeConstantOverride("separation", 10);
            box.AddChild(Kit.Heading(_tr(title), 1.3f));
            box.AddChild(Wrapped(_tr(intro), muted: true));
            return box;
        }

        private static Label Wrapped(string text, bool muted = false, int width = PageWidth)
        {
            var label = muted ? Kit.Muted(text) : Kit.Text(text);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            label.CustomMinimumSize = new Vector2(width, 0);
            return label;
        }

        private static GridContainer Grid(VBoxContainer box)
        {
            var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            grid.AddThemeConstantOverride("h_separation", 10);
            grid.AddThemeConstantOverride("v_separation", 6);
            box.AddChild(grid);
            return grid;
        }

        private static Label Row(GridContainer grid, string label, Control value)
        {
            var l = Kit.Muted(label);
            l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            grid.AddChild(l);
            value.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            grid.AddChild(value);
            return l;
        }

        private Label Problem(VBoxContainer box)
        {
            var problem = Wrapped("");
            problem.Visible = false;
            problem.AddThemeColorOverride("font_color", ThemeColors.Error);
            box.AddChild(problem);
            return problem;
        }

        private static void Say(Label problem, string? text)
        {
            problem.Text = text ?? "";
            problem.Visible = !string.IsNullOrEmpty(text);
        }

        private static PanelContainer Card(out VBoxContainer box, Color? background = null)
        {
            var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = background ?? ThemeColors.RowOdd,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft = 12,
                ContentMarginRight = 12,
                ContentMarginTop = 10,
                ContentMarginBottom = 10
            });
            box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 6);
            panel.AddChild(box);
            return panel;
        }

        /// <summary>
        /// A permission asked on the page: what Atlas wants to do and exactly what happens, then Allow once / Always allow /
        /// a refusal in the caller's words. Answered, the row says so; nothing else opens. Carries the meta "consent"
        /// (the permission's id) and names its buttons AllowOnce, AllowAlways and Refuse, for tests.
        /// </summary>
        private sealed class ConsentRow
        {
            public PanelContainer Panel { get; }
            public bool Answered { get; private set; }
            public bool Allowed { get; private set; }

            public ConsentRow(SetupWizard owner, Permissions.Kind kind, string? details, string refuseText, Action<bool> decided)
            {
                Panel = Card(out var box, ThemeColors.AccentTint);
                Panel.SetMeta("consent", kind.Id);
                var title = Kit.Text(owner._tr(kind.Title));
                title.AddThemeColorOverride("font_color", ThemeColors.AccentText(ThemeColors.AccentTint));
                box.AddChild(title);
                box.AddChild(Wrapped(owner._tr(kind.Summary), width: PageWidth - 40));
                if (!string.IsNullOrWhiteSpace(details)) box.AddChild(Wrapped(details, muted: true, width: PageWidth - 40));
                box.AddChild(Kit.MoreInfo(owner._tr(kind.Explanation), owner._tr, PageWidth - 40));
                var where = Wrapped(owner._tr("\"Allow once\" lasts until Atlas closes. You can take back \"Always allow\" in Settings → Privacy & permissions."), muted: true, width: PageWidth - 40);
                where.SetMeta("font_size_ratio", 0.9f);
                box.AddChild(where);
                var buttons = new HBoxContainer();
                buttons.AddThemeConstantOverride("separation", 8);
                var answered = Kit.Muted("");
                answered.Visible = false;
                void Answer(bool allow, bool always)
                {
                    if (Answered) return;
                    Answered = true;
                    Allowed = allow;
                    if (allow && always) Permissions.SetAlways(owner._hooks.Settings, kind, null, true);
                    else if (allow) Permissions.AllowForSession(kind);
                    else Permissions.DenyForSession(kind);
                    buttons.Visible = false;
                    answered.Text = allow ? (always ? owner._tr("Always allowed.") : owner._tr("Allowed until Atlas closes.")) : owner._tr("Not now.");
                    answered.Visible = true;
                    decided(allow);
                }
                var once = Kit.Button(owner._tr("Allow once"), null, () => Answer(true, false));
                once.Name = "AllowOnce";
                once.ThemeTypeVariation = Kit.PrimaryButton;
                buttons.AddChild(once);
                var always = Kit.Button(owner._tr("Always allow"), null, () => Answer(true, true));
                always.Name = "AllowAlways";
                buttons.AddChild(always);
                var refuse = Kit.Button(refuseText, null, () => Answer(false, false), flat: true);
                refuse.Name = "Refuse";
                buttons.AddChild(refuse);
                box.AddChild(buttons);
                box.AddChild(answered);
            }
        }

        // ---- Welcome ----

        private Control BuildWelcome()
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(PageWidth, 0) };
            box.AddThemeConstantOverride("separation", 12);
            var header = new HBoxContainer();
            header.AddThemeConstantOverride("separation", 16);
            header.AddChild(new TextureRect { Texture = GD.Load<Texture2D>("res://Assets/Graphics/icon.png"), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, CustomMinimumSize = new Vector2(72, 72) });
            var titles = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            titles.AddChild(Wordmark.Make(64, _tr("The Archipelago Atlas")));
            titles.AddChild(Kit.Muted(_tr("Your multiworld, charted.")));
            header.AddChild(titles);
            box.AddChild(header);
            box.AddChild(Wrapped(_tr("Atlas shows your Archipelago multiworlds on their maps: every check and whether it's in logic, your items, your hints and the trackers your group uses, all in one window. A few pages set it up; each says what it does before anything happens.")));
            box.AddChild(Kit.MoreInfo(_tr("Atlas keeps everything in its own folder, never looks elsewhere on your PC, and goes online only for what you allow here or later. Each permission is a row on its page: Allow once, Always allow, or not now. You can take any answer back in Settings → Privacy & permissions.") + "\n\n"
                + _tr("Skip this step is always there, and Help → Set up Atlas… or Home's Resume brings this window back at the first step left."), _tr, PageWidth - 20));
            return box;
        }

        // ---- The Atlas Engine ----

        private Label _engineState = null!;
        private VBoxContainer _engineConsentHost = null!;
        private HBoxContainer _engineButtons = null!;
        private Button _engineTryAgain = null!, _engineDetails = null!;
        private ConsentRow? _engineConsent;

        private Control BuildEngine()
        {
            var box = PageBox("The Atlas Engine", AP_Atlas.Core.EngineSetup.AtlasEngine.IsShipped
                ? "Logic, maps and hints run on Archipelago's own code, which ships with Atlas: Python, Archipelago and the Universal Tracker. A game's world is unpacked from Archipelago's release (about 22 MB, downloaded once, asked first) when a slot first needs it."
                : "Logic, maps and hints run on Archipelago's own code, which Atlas downloads into its own folder: Python, Archipelago and the Universal Tracker, about 55 MB. It installs in the background while you go on; its progress shows on the left.");
            _engineState = Wrapped("");
            box.AddChild(_engineState);
            _engineConsentHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddChild(_engineConsentHost);
            _engineButtons = new HBoxContainer();
            _engineButtons.AddThemeConstantOverride("separation", 8);
            _engineTryAgain = Kit.Button(_tr("Try again"), _tr("Runs the setup again from where it is."), () => _engineRun.Start());
            _engineButtons.AddChild(_engineTryAgain);
            _engineDetails = Kit.Button(_tr("Show details"), _tr("The Atlas Engine page, with the setup's log."), () =>
            {
                _hooks.ShowEnginePage(_engineRun.Log);
                Hide(); // out of the page's way; Help → Set up Atlas… (or Home's Resume) brings it back as it was
            }, flat: true);
            _engineButtons.AddChild(_engineDetails);
            box.AddChild(_engineButtons);
            return box;
        }

        private void RefreshEnginePage()
        {
            bool ready = _hooks.EngineReady();
            if (ready)
            {
                _engineState.Text = AP_Atlas.Core.EngineSetup.AtlasEngine.IsShipped ? _tr("The Atlas Engine ships with Atlas and is ready. Nothing to do here.") : _tr("The Atlas Engine is set up. Nothing to do here.");
                _engineState.AddThemeColorOverride("font_color", ThemeColors.Success);
                _engineConsentHost.Visible = false;
                _engineButtons.Visible = false;
                return;
            }
            if (_engineRun.Running)
            {
                _engineState.Text = _tr("The setup is running: its progress shows on the left, and you can go on to the next pages meanwhile. Add games' Add and the Ready page wait for it.");
                _engineState.AddThemeColorOverride("font_color", ThemeColors.Text);
                _engineConsentHost.Visible = false;
                _engineButtons.Visible = true;
                _engineTryAgain.Visible = false;
                return;
            }
            if (_engineRun.Succeeded || _engineConsent?.Answered == true)
            {
                _engineState.Text = _engineRun.Succeeded ? _tr("The Atlas Engine is set up.") : _engineConsent?.Allowed == true ? _tr("The setup stopped: ") + _engineRun.OutcomeText : _tr("Not now: Atlas runs without logic until the engine is set up (Home's setup line, or the Engine button on the bar).");
                _engineState.AddThemeColorOverride("font_color", _engineRun.Succeeded ? ThemeColors.Success : ThemeColors.Text);
                _engineConsentHost.Visible = _engineConsent?.Allowed != true;
                _engineButtons.Visible = _engineConsent?.Allowed == true;
                _engineTryAgain.Visible = !_engineRun.Succeeded;
                return;
            }
            _engineState.Text = string.Empty;
            _engineButtons.Visible = false;
            if (_engineConsent == null)
            {
                if (Permissions.IsAllowed(_hooks.Settings, Permissions.EngineSetup))
                {
                    // Allowed before (Settings → Privacy, or an earlier run): one button starts it.
                    var start = Kit.Button(_tr("Set up the Atlas Engine"), _tr("Starts the download and setup in the background."), () =>
                    {
                        _engineRun.Start();
                        RefreshEnginePage();
                    });
                    start.Name = "StartEngine";
                    start.ThemeTypeVariation = Kit.PrimaryButton;
                    _engineConsentHost.AddChild(start);
                    _engineConsent = null;
                    _engineConsentHost.Visible = true;
                    return;
                }
                _engineConsent = new ConsentRow(this, Permissions.EngineSetup, null, _tr("Not now"), allowed =>
                {
                    if (allowed) _engineRun.Start();
                    RefreshEnginePage();
                });
                _engineConsentHost.AddChild(_engineConsent.Panel);
            }
            _engineConsentHost.Visible = true;
        }

        // ---- Your multiworld and slots ----

        private OptionButton _which = null!;
        private Label _whichLabel = null!, _roomReading = null!, _playersTitle = null!, _multiworldProblem = null!;
        private LineEdit _name = null!, _roomLink = null!, _server = null!, _password = null!;
        private VBoxContainer _roomConsentHost = null!, _playersBox = null!, _slotRows = null!;
        private ConsentRow? _roomConsent;
        private List<MultiworldProfile> _choices = new();
        private readonly List<CheckBox> _playerChecks = new();
        private readonly List<string> _slotNamesShownFor = new();
        private List<string> _roomPlayers = new();
        private string? _roomReadFor;
        private Task<bool>? _roomRead;
        // The slots Next couldn't connect (Add games says their games aren't known).
        private List<string> _notConnected = new();
        // The slots this window added, by multiworld: only these can be taken back by correcting or removing their row.
        private readonly Dictionary<string, HashSet<string>> _addedHere = new();
        // The room's status as last read (whether it sleeps, when a connect fails), and the button that opens its page.
        private AP_Atlas.Core.Rooms.RoomStatus? _roomStatus;
        private Button _wakeRoom = null!;

        private Control BuildMultiworld()
        {
            var box = PageBox("Your multiworld and slots", "The room you play in, and which of its slots are yours. Paste the room link the host shared and Atlas reads the room's status page once (asked below) for the server and the players to tick; or type the server and your slots. Next saves the multiworld and connects your slots, so Atlas learns each one's game from the room.");
            var grid = Grid(box);
            _which = new OptionButton { Name = "WhichMultiworld", AccessibilityName = _tr("Multiworld") };
            _which.ItemSelected += _ => FillFromChoice();
            _whichLabel = Row(grid, _tr("Multiworld"), _which);
            _name = new LineEdit { Name = "NameBox", PlaceholderText = _tr("A name for this multiworld"), AccessibilityName = _tr("Name") };
            Row(grid, _tr("Name"), _name);
            _roomLink = new LineEdit { Name = "RoomLinkBox", PlaceholderText = _tr("https://archipelago.gg/room/… (optional)"), AccessibilityName = _tr("Room link") };
            _roomLink.TextChanged += _ => RefreshRoomConsent();
            Row(grid, _tr("Room link"), _roomLink);
            _roomConsentHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddChild(_roomConsentHost);
            _server = new LineEdit { Name = "ServerBox", PlaceholderText = _tr("archipelago.gg:12345"), AccessibilityName = _tr("Server address") };
            Row(grid, _tr("Server"), _server);
            var passwordLine = new HBoxContainer();
            passwordLine.AddThemeConstantOverride("separation", 6);
            _password = new LineEdit { Name = "PasswordBox", PlaceholderText = _tr("Password (optional)"), Secret = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = _tr("Password") };
            passwordLine.AddChild(_password);
            Button eye = null!;
            eye = Kit.Button("", _tr("Show the password"), () =>
            {
                _password.Secret = !_password.Secret;
                eye.Icon = LucideTextures.Get(_password.Secret ? "eye" : "eye-off", ThemeColors.TextMuted, 1.0f);
                eye.TooltipText = _password.Secret ? _tr("Show the password") : _tr("Hide the password");
                eye.AccessibilityName = eye.TooltipText;
            }, focusable: false);
            eye.Icon = LucideTextures.Get("eye", ThemeColors.TextMuted, 1.0f);
            passwordLine.AddChild(eye);
            Row(grid, _tr("Password"), passwordLine);
            _roomReading = Wrapped("", muted: true);
            _roomReading.Visible = false;
            box.AddChild(_roomReading);
            // The room's players, each a tick; then slots typed by their name in the room.
            _playersTitle = Kit.Text(_tr("The room's players: tick yours"));
            _playersTitle.Visible = false;
            box.AddChild(_playersTitle);
            _playersBox = new VBoxContainer { Name = "PlayersBox" };
            _playersBox.AddThemeConstantOverride("separation", 2);
            box.AddChild(_playersBox);
            box.AddChild(Kit.Text(_tr("Slots")));
            _slotRows = new VBoxContainer { Name = "SlotsBox" };
            _slotRows.AddThemeConstantOverride("separation", 4);
            box.AddChild(_slotRows);
            var add = Kit.Button(_tr("+ Add slot"), _tr("Another slot you play in this multiworld, by its name in the room."), () => AddSlotRow(""), small: true);
            add.Name = "AddSlotRow";
            add.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            box.AddChild(add);
            box.AddChild(Wrapped(_tr("The Multiworlds page holds the rest later: each slot's details (its YAML, when logic needs one), Cheese Tracker and the host's Sphere Tracker room."), muted: true));
            _multiworldProblem = Problem(box);
            // A sleeping room wakes when its page is opened in a browser: only the user does that, never Atlas by itself.
            _wakeRoom = Kit.Button(_tr("Open the room page"), _tr("Opens the room's page in your browser, which wakes the room."), () => AP_Atlas.Core.ExternalLinks.OpenWeb(_roomLink.Text.Trim()), small: true);
            _wakeRoom.Name = "WakeRoom";
            _wakeRoom.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            _wakeRoom.Visible = false;
            box.AddChild(_wakeRoom);
            return box;
        }

        private void RefreshMultiworldPage()
        {
            // Opened past this page (Resume), its first visit shows the multiworld the later pages are about, not a blank new one.
            string? keep = _choices.Count > 0 && _which.Selected > 0 && _which.Selected - 1 < _choices.Count ? _choices[_which.Selected - 1].Id
                : _profile?.Id ?? (_choices.Count == 0 && _current < _furthest ? Profile?.Id : null);
            _choices = _hooks.Profiles().ToList();
            _which.Clear();
            _which.AddItem(_tr("New multiworld"));
            foreach (var profile in _choices) _which.AddItem(profile.Name);
            int index = keep == null ? -1 : _choices.FindIndex(p => p.Id == keep);
            _which.Selected = index < 0 ? 0 : index + 1;
            _which.Visible = _choices.Count > 0;
            _whichLabel.Visible = _choices.Count > 0;
            if (_name.Text.Length == 0 && index >= 0) FillFromChoice();
            RefreshRoomConsent();
            RefreshPlayers();
        }

        private MultiworldProfile? ChosenExisting => _which.Selected > 0 && _which.Selected - 1 < _choices.Count ? _choices[_which.Selected - 1] : null;

        private void FillFromChoice()
        {
            // What the previous choice left goes: its typed rows and its ticks (the new choice's own slots come back below).
            foreach (Node row in _slotRows.GetChildren()) { _slotRows.RemoveChild(row); row.QueueFree(); }
            _slotNamesShownFor.Clear();
            foreach (var check in _playerChecks) check.ButtonPressed = false;
            var chosen = ChosenExisting;
            _name.Text = chosen?.Name ?? "";
            _roomLink.Text = chosen?.RoomLink ?? "";
            _server.Text = chosen?.ServerUrl ?? "";
            _password.Text = chosen?.Password ?? "";
            RefreshRoomConsent();
            RefreshPlayers();
        }

        /// <summary>
        /// A room link the status read needs permission for shows the permission's row under it; once allowed, the room is
        /// read (once per room in this window; a room id is whole only at its 22nd character, so typing reads once). A link
        /// cleared, or no longer a room, takes the room's players with it.
        /// </summary>
        private void RefreshRoomConsent()
        {
            var room = AP_Atlas.Core.Rooms.RoomLinks.Parse(_roomLink.Text, out _);
            if (room == null && (_roomPlayers.Count > 0 || _roomReadFor != null))
            {
                _roomPlayers = new List<string>();
                _roomReadFor = null;
                _roomRead = null;
                _roomStatus = null;
                RefreshPlayers();
            }
            bool allowed = Permissions.IsAllowed(_hooks.Settings, Permissions.RoomStatusReads);
            if (room != null && allowed) Async.Fire(ReadRoomAsync(room.Value.Site, room.Value.Id), "reading the room's status");
            bool needs = room != null && !allowed && _roomConsent?.Answered != true;
            if (!needs)
            {
                _roomConsentHost.Visible = false;
                return;
            }
            if (_roomConsent == null)
            {
                _roomConsent = new ConsentRow(this, Permissions.RoomStatusReads, $"{room!.Value.Site}/room/{room.Value.Id}", _tr("No, I'll type the server"), _ => RefreshRoomConsent());
                _roomConsentHost.AddChild(_roomConsent.Panel);
            }
            _roomConsentHost.Visible = true;
            WindowFit.RequestShrink(this);
        }

        /// <summary>The room's status page, read once per room in this window (a failed read is tried again on Next): its server and its players to tick.</summary>
        private Task<bool> ReadRoomAsync(string site, string id)
        {
            string key = site + "/room/" + id;
            if (_roomReadFor == key && _roomRead != null) return _roomRead;
            _roomReadFor = key;
            return _roomRead = ReadRoomOnceAsync(site, id);
        }

        private async Task<bool> ReadRoomOnceAsync(string site, string id)
        {
            _roomReading.Text = _tr("Reading the room…");
            _roomReading.Visible = true;
            var (status, error) = await AP_Atlas.Core.Rooms.RoomLinks.ReadAsync(site, id);
            if (!IsInstanceValid(this)) return false;
            // The link changed (or was cleared) meanwhile: this answer is another room's, and changes nothing.
            if (_roomReadFor != site + "/room/" + id)
            {
                if (_roomReadFor == null) _roomReading.Visible = false;
                return false;
            }
            _roomReading.Visible = false;
            if (status == null)
            {
                _roomReadFor = null;
                Say(_multiworldProblem, _tr("The room's status couldn't be read: {0}. Type the server, or try again.").Replace("{0}", error ?? ""));
                return false;
            }
            _roomStatus = status;
            if (status.Port > 0) _server.Text = status.ServerAddress(site);
            _roomPlayers = status.Players.ToList();
            RefreshPlayers();
            return true;
        }

        /// <summary>The names typed in the slot rows (rows still there).</summary>
        private List<string> TypedSlots() =>
            _slotRows.GetChildren().OfType<HBoxContainer>().Where(r => !r.IsQueuedForDeletion()).Select(r => r.GetChild<LineEdit>(0).Text.Trim()).Where(s => s.Length > 0).ToList();

        /// <summary>The room's players as ticks (keeping what's ticked; a slot the multiworld has comes ticked), and the typed rows: the existing multiworld's other named slots once (none typed twice), else one empty row while the room lists no one.</summary>
        private void RefreshPlayers()
        {
            var existing = ChosenExisting ?? _profile;
            var ticked = _playerChecks.Where(c => c.ButtonPressed).Select(c => c.GetMeta("player").AsString()).ToHashSet();
            foreach (Node child in _playersBox.GetChildren()) { _playersBox.RemoveChild(child); child.QueueFree(); }
            _playerChecks.Clear();
            foreach (string player in _roomPlayers)
            {
                var check = new CheckBox { Text = player, ButtonPressed = ticked.Contains(player) || existing?.Slots.Contains(player) == true };
                check.SetMeta("player", player);
                _playersBox.AddChild(check);
                _playerChecks.Add(check);
            }
            _playersTitle.Visible = _roomPlayers.Count > 0;
            _playersBox.Visible = _roomPlayers.Count > 0;
            string key = existing?.Id ?? "";
            if (!_slotNamesShownFor.Contains(key))
            {
                _slotNamesShownFor.Add(key);
                var typed = TypedSlots();
                if (existing != null)
                    foreach (string slot in existing.Slots.Where(s => !SetupSteps.IsPlaceholderSlot(s) && !_roomPlayers.Contains(s) && !typed.Contains(s)))
                        AddSlotRow(slot);
            }
            if (!_slotRows.GetChildren().OfType<HBoxContainer>().Any(r => !r.IsQueuedForDeletion()) && _roomPlayers.Count == 0) AddSlotRow("");
            WindowFit.RequestShrink(this);
        }

        /// <summary>A slot typed by its name in the room (no game: the room says it when the slot connects).</summary>
        private void AddSlotRow(string slotName)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            row.AddChild(new LineEdit { Text = slotName, PlaceholderText = _tr("The slot's name in the room"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = _tr("Slot name") });
            row.AddChild(Kit.Button("×", _tr("Removes this slot"), () =>
            {
                _slotRows.RemoveChild(row);
                row.QueueFree();
            }, small: true));
            _slotRows.AddChild(row);
        }

        /// <summary>The slots ticked and typed, each once.</summary>
        private List<string> ChosenSlots()
        {
            var names = _playerChecks.Where(c => c.ButtonPressed).Select(c => c.GetMeta("player").AsString()).Distinct().ToList();
            foreach (string slot in TypedSlots()) if (!names.Contains(slot)) names.Add(slot);
            return names;
        }

        private async Task<bool> MultiworldNextAsync()
        {
            Say(_multiworldProblem, null);
            _wakeRoom.Visible = false;
            if (_name.Text.Trim().Length == 0)
            {
                Say(_multiworldProblem, _tr("Give the multiworld a name first."));
                return false;
            }
            var room = AP_Atlas.Core.Rooms.RoomLinks.Parse(_roomLink.Text, out _);
            if (room != null && Permissions.IsAllowed(_hooks.Settings, Permissions.RoomStatusReads))
            {
                _next.Text = _tr("Reading the room…");
                bool read = await ReadRoomAsync(room.Value.Site, room.Value.Id); // read already: nothing asked again
                if (!IsInstanceValid(this)) return false;
                if (!read && _server.Text.Trim().Length == 0) return false; // the read's message asks for the server
                if (!read) Say(_multiworldProblem, null); // a server typed by hand stands in for the room's
            }
            if (_server.Text.Trim().Length == 0)
            {
                Say(_multiworldProblem, _tr("Type the server address (archipelago.gg and the room's port), or paste the room link and allow its one read."));
                return false;
            }
            var names = ChosenSlots();
            if (names.Count == 0)
            {
                Say(_multiworldProblem, _roomPlayers.Count > 0 ? _tr("Tick your players in the room above, or type a slot's name.") : _tr("Type at least one slot: the one you play in this multiworld."));
                return false;
            }
            // Slots this window added to the chosen multiworld and the user has since corrected or removed go; its earlier slots stay.
            var target = ChosenExisting;
            var addedHere = target != null && _addedHere.TryGetValue(target.Id, out var added) ? added : new HashSet<string>();
            var dropped = addedHere.Where(s => !names.Contains(s)).ToList();
            var held = target?.Slots.ToList() ?? new List<string>();
            _profile = _hooks.SaveMultiworld(new SetupMultiworldDraft(target?.Id, _name.Text.Trim(), _roomLink.Text.Trim(), _server.Text.Trim(), _password.Text, names, dropped));
            if (!_addedHere.TryGetValue(_profile.Id, out var mine)) _addedHere[_profile.Id] = mine = new HashSet<string>();
            mine.ExceptWith(dropped);
            foreach (string slot in names) if (!held.Contains(slot)) mine.Add(slot);
            // A new multiworld is the page's choice from now on: Next again (after a failed connect) updates it, never another copy.
            if (target == null) RefreshMultiworldPage();
            return await ConnectSlotsAsync(_profile, names);
        }

        /// <summary>
        /// Connects the slots just saved, one after another (as Home's Connect does), so Atlas learns each one's game from
        /// the room before Add games lists them; their Logic banners stay quiet meanwhile. When none connects, the page says
        /// so: Next tries again, and Skip this step goes on without them.
        /// </summary>
        private async Task<bool> ConnectSlotsAsync(MultiworldProfile profile, List<string> slots)
        {
            var failed = new List<string>();
            _connecting = true;
            SyncQuiet();
            try
            {
                foreach (string slot in slots)
                {
                    if (_hooks.IsSlotLive(profile.Id, slot)) continue;
                    _next.Text = _tr("Connecting {0}…").Replace("{0}", slot);
                    bool ok = await _hooks.ConnectSlotAsync(profile, slot);
                    if (!IsInstanceValid(this)) return false;
                    if (!ok) failed.Add(slot);
                }
            }
            finally
            {
                _connecting = false;
                if (IsInstanceValid(this)) SyncQuiet();
            }
            _notConnected = failed;
            if (failed.Count == 0 || failed.Count < slots.Count) return true;
            // The room sleeps (its last read says so): opening its page in a browser wakes it. The next Next reads it again.
            if (_roomStatus?.IsAsleep(AP_Atlas.Core.Deadline.WallClock()) == true && AP_Atlas.Core.Rooms.RoomLinks.Parse(_roomLink.Text, out _) != null)
            {
                _roomReadFor = null;
                Say(_multiworldProblem, _tr("The room is asleep. Open its page to wake it, then press Next."));
                _wakeRoom.Visible = true;
                return false;
            }
            Say(_multiworldProblem, _tr("{0} didn't connect (the status bar says why: the room may be asleep, or the address wrong). Next tries again; Skip this step goes on, and Add games then lists every game for you to tick.").Replace("{0}", string.Join(", ", failed)));
            return false;
        }

        // ---- Add games ----

        private VBoxContainer _addGamesHost = null!;
        private Label _addGamesNote = null!;
        private AddGamesPanel? _addGames;

        private bool OnAddGames => _current >= 0 && _pages[_current].Step == SetupSteps.Games;

        private bool AddGamesRunning => _addGames?.CurrentStage == AddGamesPanel.Stage.Running;

        private Control BuildAddGames()
        {
            var box = PageBox("Add games", "Your slots' games come first, ticked. Atlas adds each one's logic and a map pack after one question naming everything it downloads; nothing is downloaded before it.");
            _addGamesNote = Wrapped("", muted: true);
            _addGamesNote.Visible = false;
            box.AddChild(_addGamesNote);
            _addGamesHost = new VBoxContainer { Name = "AddGamesHost", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            box.AddChild(_addGamesHost);
            return box;
        }

        /// <summary>The screen, made the first time the page shows (the slots have connected by then, so their games come ticked); on a later visit, read again while it's on its list.</summary>
        private void RefreshAddGamesPage()
        {
            if (_addGames == null)
            {
                _addGames = new AddGamesPanel(InlineAddGamesHooks(), null, PageWidth) { CancelOnExit = false }; // the setup window lets a batch go on
                _addGames.ActionsChanged += () => { if (IsInstanceValid(this) && !IsQueuedForDeletion()) RefreshNext(); };
                // A batch closed out of sight (the window's ×) ends with its summary in sight, as the Add games dialog does.
                _addGames.Finished += _ => { if (IsInstanceValid(this) && !IsQueuedForDeletion() && !Visible) Show(); };
                _addGamesHost.AddChild(_addGames); // its _Ready reads the games, the slots' ticked
            }
            else if (_addGames.CurrentStage == AddGamesPanel.Stage.Pick) _addGames.Start(); // a game a slot named since is ticked, once
            _addGamesNote.Text = _notConnected.Count == 0 ? ""
                : _tr("{0} didn't connect, so Atlas doesn't know its game yet: tick it below, or connect it later and use Add games… on Home.").Replace("{0}", string.Join(", ", _notConnected));
            _addGamesNote.Visible = _notConnected.Count > 0;
            WindowFit.RequestShrink(this);
        }

        /// <summary>The window's Add games hooks with this window's own ways out: the engine's page here, and whatever opens elsewhere with this window out of the way.</summary>
        private AddGamesPanel.Hooks InlineAddGamesHooks()
        {
            var window = _hooks.AddGames();
            return new AddGamesPanel.Hooks
            {
                Tr = window.Tr,
                Settings = window.Settings,
                Source = window.Source,
                RunAsync = window.RunAsync,
                FreeBytes = window.FreeBytes,
                BatchEnded = window.BatchEnded,
                OpenEngineSetup = ShowEngineStep,
                FindPack = game => OutOfTheWay(() => window.FindPack(game)),
                InstallPack = (game, repo) => OutOfTheWay(() => window.InstallPack(game, repo)),
                StartPack = game => OutOfTheWay(() => window.StartPack(game)),
                // The Pack Doctor's "fix them now?" in the summary: its preview of every change opens on this window; Review… opens the Doctor with this window out of the way.
                OfferFixes = (box, run) => box.AddChild(new PackFixHandoff(run.Fixes, (zip, tab) => OutOfTheWay(() => _hooks.OpenPackDoctor(zip, tab)))),
            };
        }

        /// <summary>Add games' "Set up the Atlas Engine…": the engine's page here when the window has one, else the Atlas Engine page with this window out of the way.</summary>
        private void ShowEngineStep()
        {
            int at = _pages.FindIndex(p => p.Step == SetupSteps.Engine);
            if (at >= 0) Go(at);
            else OutOfTheWay(() => _hooks.ShowEnginePage(Array.Empty<string>()));
        }

        /// <summary>Next moves on once there's nothing to add, or the summary's question is answered.</summary>
        private static bool AddGamesMovesOn(AddGamesPanel panel) =>
            (panel.CurrentStage == AddGamesPanel.Stage.Pick && panel.AddingCount == 0) || (panel.CurrentStage == AddGamesPanel.Stage.Summary && !panel.Asking);

        /// <summary>Next on Add games: the screen's own action (Add…, Download and install, Cancel, Fix them now…), or on to Ready.</summary>
        private Task<bool> AddGamesNextAsync()
        {
            var panel = _addGames;
            if (panel == null || AddGamesMovesOn(panel))
            {
                // Nothing Atlas can add (games it knows no logic source for, or none learnt): moving on settles the step as skipped.
                if (!_hooks.Steps.Done(SetupSteps.Games)) _hooks.Steps.SetSkipped(SetupSteps.Games, true);
                return Task.FromResult(true);
            }
            panel.PressPrimary();
            return Task.FromResult(false);
        }

        /// <summary>
        /// The footer on Add games: Next carries the screen's action and the button beside it its second one (Back on the
        /// confirm, Later, Try those again); the window's Back and Skip show on the list, and Back on an answered summary.
        /// The list on the left is locked while a batch runs.
        /// </summary>
        private void SyncAddGamesButtons()
        {
            var panel = _addGames;
            var stage = panel?.CurrentStage ?? AddGamesPanel.Stage.Pick;
            bool moveOn = panel == null || AddGamesMovesOn(panel);
            if (!_busy) _next.Text = moveOn ? _tr("Next") : panel!.PrimaryText;
            _next.Disabled = _busy || (!moveOn && !panel!.PrimaryEnabled);
            _back.Visible = stage == AddGamesPanel.Stage.Pick || (stage == AddGamesPanel.Stage.Summary && moveOn);
            _skip.Visible = stage == AddGamesPanel.Stage.Pick;
            _back.Disabled = _skip.Disabled = _busy;
            string? second = stage == AddGamesPanel.Stage.Pick ? null : panel?.SecondaryText; // the list's own Cancel is the window's Back and Skip
            _secondary.Text = second ?? "";
            _secondary.Visible = second != null;
            if (AddGamesRunning != _railLocked)
            {
                _railLocked = AddGamesRunning;
                RefreshRail(); // only when the lock flips: the rail's ticks read every game's state
            }
            WindowFit.RequestShrink(this);
        }

        // ---- Ready ----

        private Label _readyLine = null!;
        private VBoxContainer _summary = null!;

        private Control BuildReady()
        {
            var box = PageBox("Ready", "What's set up. Finish closes this window; Home keeps one line about the setup until every step is done, and Help → Set up Atlas… brings this window back.");
            _readyLine = Wrapped("");
            _readyLine.SetMeta("setup_ready_line", true);
            box.AddChild(_readyLine);
            _summary = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _summary.AddThemeConstantOverride("separation", 4);
            box.AddChild(_summary);
            // The rest, whenever you like: each a section of Settings, this window out of the way.
            box.AddChild(Kit.Text(_tr("More, whenever you like")));
            var links = new HFlowContainer();
            links.AddThemeConstantOverride("h_separation", 8);
            void Link(string name, string text, string tip, string section)
            {
                var link = Kit.Button(_tr(text), _tr(tip), () => OutOfTheWay(() => _hooks.ShowSettings(section)), flat: true);
                link.Name = name;
                links.AddChild(link);
            }
            Link("LookLink", "Look…", "Settings → Appearance: the theme, colour-blind-safe colours and the zoom.", "appearance");
            Link("TrackersLink", "Trackers…", "Settings → Tools: Cheese Tracker and the host's Sphere Tracker room.", "tools");
            Link("ReportsLink", "Reports and updates…", "Settings → Updates: a daily check for a newer Atlas. Crash reports are under Privacy & permissions.", "updates");
            box.AddChild(links);
            return box;
        }

        private IEnumerable<string> SlotGames()
        {
            var profile = Profile;
            return profile == null ? Enumerable.Empty<string>()
                : profile.Slots.Select(slot => profile.SavedStats.TryGetValue(slot, out var stats) ? stats.GameName : null).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g!).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private void RefreshReadyPage()
        {
            foreach (Node child in _summary.GetChildren()) { _summary.RemoveChild(child); child.QueueFree(); }
            void Line(string text, Color colour) => _summary.AddChild(Wrapped(text).Also(l => l.AddThemeColorOverride("font_color", colour)));
            bool all = _hooks.Steps.AllSettled;
            _readyLine.Text = all ? _tr("You're set up. Right-click a pin or tile to fix it.")
                : _tr("Not everything is set up yet: Home's setup line names what's left, and Resume brings this window back there. Right-click a pin or tile to fix it.");
            _readyLine.AddThemeColorOverride("font_color", all ? ThemeColors.Success : ThemeColors.Text);
            bool ready = _hooks.EngineReady();
            Line(ready ? _tr("✓ The Atlas Engine is set up.") : _engineRun.Running ? _tr("… The Atlas Engine is still installing: Finish waits for it.") : _tr("✗ The Atlas Engine isn't set up: logic, maps and hints wait until it is (Home's setup line, or the Engine button on the bar)."),
                ready ? ThemeColors.Success : _engineRun.Running ? ThemeColors.Text : ThemeColors.Warning);
            var profile = Profile;
            if (profile == null) Line(_tr("✗ No multiworld yet: Back to Your multiworld and slots makes one."), ThemeColors.Warning);
            else
            {
                Line(_tr("✓ {0}: {1}").Replace("{0}", profile.Name).Replace("{1}", string.IsNullOrWhiteSpace(profile.ServerUrl) ? _tr("no server address yet") : profile.ServerUrl), ThemeColors.Success);
                var games = SlotGames().ToList();
                Line(_tr("✓ Slots: {0}").Replace("{0}", string.Join(", ", profile.Slots)) + (games.Count > 0 ? "  ·  " + string.Join(", ", games) : ""), ThemeColors.Success);
                bool gamesReady = _hooks.Steps.Done(SetupSteps.Games);
                Line(gamesReady ? _tr("✓ Every game your slots play is ready.")
                    : _hooks.Steps.Skipped(SetupSteps.Games) ? _tr("… Adding games was skipped: Add games… on Home or the Games page adds them whenever you like.")
                    : _tr("… Not every game of your slots is ready: Add games (Home, or the Games page) finishes them."),
                    gamesReady ? ThemeColors.Success : ThemeColors.Text);
            }
            RefreshNext();
        }

        private Task<bool> FinishAsync()
        {
            Hide();
            QueueFree();
            return Task.FromResult(true);
        }

        // ---- For tests ----

        internal Button NextButton => _next;
        internal Button BackButton => _back;
        internal Button SkipButton => _skip;
        internal EngineSetupProgress EngineRun => _engineRun;
        internal Control PageOf(string id) => _pages.First(p => p.Id == id).Body;
        internal Button RailButtonOf(string id) => _pages.First(p => p.Id == id).RailButton;
        internal bool Busy => _busy;
        internal MultiworldProfile? SavedProfile => _profile;
        internal MultiworldProfile? ShownProfile => Profile;
        internal IReadOnlyList<string> RoomPlayers => _roomPlayers;
        internal Task NextForTestsAsync() => NextAsync();
        /// <summary>Add games' second action beside Next (Back on its confirm, Later, Try those again).</summary>
        internal Button SecondaryButton => _secondary;
        internal IReadOnlyList<string> PageIdsShown => _pages.Select(p => p.Id).ToList();
        internal AddGamesPanel? AddGamesPanelShown => _addGames;
        /// <summary>Shows a page as if Next had led there (the visual check's pictures).</summary>
        internal void ShowPageForTests(string id)
        {
            int at = _pages.FindIndex(p => p.Id == id);
            if (at < 0) return;
            _furthest = Math.Max(_furthest, at);
            Go(at);
        }
    }

    internal static class NodeExtensions
    {
        /// <summary>Applies an action to a node and hands it back (for building a tree in one expression).</summary>
        public static T Also<T>(this T node, Action<T> apply) where T : Node
        {
            apply(node);
            return node;
        }
    }
}
