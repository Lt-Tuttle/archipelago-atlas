#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using AP_Atlas.Core;

namespace AP_Atlas.UI
{
    /// <summary>A multiworld as the setup window hands it back: an existing one to update (by id) or a new one, with its slots, and each slot's game and YAML when known.</summary>
    public sealed record SetupMultiworldDraft(string? ExistingId, string Name, string RoomLink, string Server, string Password, List<string> Slots, Dictionary<string, string> YamlBySlot, Dictionary<string, string> GameBySlot);

    /// <summary>A map pack GitHub's search found, as the setup window lists it.</summary>
    /// <param name="Fit">How the pack lab measured it against the game's checks (percent), when Atlas's game list knows it.</param>
    /// <param name="Notes">What the lab found about it, in words (another version, another game, scripts that stop).</param>
    public sealed record SetupPackCandidate(string Repo, string Description, int Stars, bool Fork, bool Recommended, int? Fit = null, IReadOnlyList<string>? Notes = null);

    /// <summary>
    /// Setting Atlas up as a click-through: one window, a list of pages on the left (a tick for each that's done, the
    /// current one lit, the engine's progress at the bottom) and one page at a time on the right, with Back, Skip this
    /// step and Next. Nothing opens on top of it: a permission is a row on the page with Allow once / Always allow / Not
    /// now, trusting a project is a tick beside its install, and the engine installs in the background while the next
    /// pages are filled in. Resume opens it at the first page not done or skipped. Not exclusive: Atlas stays usable, and
    /// closing it keeps whatever runs (the engine's setup goes on; Home's setup card shows it).
    /// </summary>
    public sealed partial class SetupWizard : AcceptDialog
    {
        /// <summary>What the window needs from the main window.</summary>
        public sealed class Hooks
        {
            public required Func<string, string> Tr { get; init; }
            public required AppSettings Settings { get; init; }
            public required SetupSteps Steps { get; init; }
            /// <summary>The Welcome page's "Don't open this by itself again".</summary>
            // The look.
            public required Action<string> ApplyTheme { get; init; }
            public required Action<bool> ApplyColourBlindSafe { get; init; }
            public required Action<int> SetZoom { get; init; }
            public required IReadOnlyList<int> ZoomSteps { get; init; }
            // The engine.
            public required Func<bool> EngineReady { get; init; }
            public required Action<IReadOnlyList<string>> ShowEnginePage { get; init; }
            // The multiworld and its slots.
            public required Func<IReadOnlyList<MultiworldProfile>> Profiles { get; init; }
            /// <summary>The multiworld selected on the Multiworlds page, if any.</summary>
            public required Func<MultiworldProfile?> SelectedProfile { get; init; }
            /// <summary>Saves the draft as a new multiworld, or into the existing one it names, and hands it back.</summary>
            public required Func<SetupMultiworldDraft, MultiworldProfile> SaveMultiworld { get; init; }
            public required Action<Action<string>> PickYaml { get; init; }
            public required Func<string, (string Kept, List<(string Name, List<string> Games)> Players)?> KeepYaml { get; init; }
            /// <summary>Every game Atlas knows, for the slots' game boxes.</summary>
            public required Func<IReadOnlyList<string>> KnownGames { get; init; }
            // Games and map packs.
            public required Func<string, GameStatus> GameStatus { get; init; }
            /// <summary>Whether a game's apworld is in the Atlas Engine (its logic can run).</summary>
            public required Func<string, bool> ApworldInstalled { get; init; }
            /// <summary>The GitHub project ("owner/name") a game's apworld comes from, when one is known.</summary>
            public required Func<string, string?> KnownProject { get; init; }
            /// <summary>Installs a game's newest apworld, any question of its own opening on the node given; false when it didn't happen.</summary>
            public required Func<string, Node, Task<bool>> InstallApworldAsync { get; init; }
            public required Func<string, bool> PackInstalledFor { get; init; }
            /// <summary>The project the game's own setup guide names as its tracker, once its apworld is installed.</summary>
            public required Func<string, string?> RecommendedPack { get; init; }
            public required Func<string, Task<(List<SetupPackCandidate> Candidates, string? Problem)>> SearchPacksAsync { get; init; }
            /// <summary>Installs a project's pack release that fits the game (nothing asked); what was tried, and the file.</summary>
            public required Func<string, string, Action<string>, Task<(bool Ok, string Detail, string? File)>> InstallPackAsync { get; init; }
            /// <summary>Atlas picks a game's pack from GitHub's projects by itself (the first whose release fits the game's names): whether it installed one, what it tried, the file.</summary>
            public required Func<string, Action<string>, Task<(bool Ok, string Detail, string? File)>> PickPackAsync { get; init; }
            public required Action<string, string> PreferPack { get; init; }
            // Trackers.
            public required Action<MultiworldProfile, string> LinkCheese { get; init; }
            /// <summary>Links the host's Sphere Tracker room; its one question (is this the host's room?) opens on the node given.</summary>
            public required Action<Node, MultiworldProfile, string> LinkSphere { get; init; }
            // Reports and updates.
            public required Action<bool> DailyUpdateChecks { get; init; }
            public required Action<bool> CrashReportsAlways { get; init; }
            // Ready.
            public required Func<string, string, bool> IsSlotLive { get; init; }
            public required Action<MultiworldProfile, string> ConnectSlot { get; init; }
        }

        /// <summary>The pages' ids, in order.</summary>
        public static readonly string[] PageIds = { "welcome", SetupSteps.Look.Id, SetupSteps.Engine.Id, SetupSteps.Multiworld.Id, SetupSteps.Slots.Id, SetupSteps.Games.Id, SetupSteps.Trackers.Id, SetupSteps.Reports.Id, "ready" };

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
        private readonly Button _back, _skip, _next;
        private readonly EngineSetupProgress _engineRun;
        private readonly Label _railEngineTitle;
        private int _current = -1, _furthest;
        private bool _busy;

        // What the pages gather.
        private MultiworldProfile? _profile;

        /// <summary>
        /// The multiworld the pages after Your slots are about: the one saved on Your slots in this window; else (opened
        /// past it, as Resume does for a set-up Atlas) the one chosen on Your multiworld, the one selected on the
        /// Multiworlds page, or the one played most recently.
        /// </summary>
        private MultiworldProfile? Profile => _profile ?? ChosenExisting ?? _hooks.SelectedProfile() ?? MultiworldProfile.MostRecentFirst(_hooks.Profiles()).FirstOrDefault();
        private List<string> _roomPlayers = new();

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
                start = next == null ? wizard._pages.Count - 1 : wizard._pages.FindIndex(p => p.Step == next);
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
            CloseRequested += () => { Hide(); QueueFree(); };
            Canceled += () => { Hide(); QueueFree(); };

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
            _back = Kit.Button(_tr("Back"), _tr("The page before; what you typed stays."), () => Go(_current - 1), flat: true);
            footer.AddChild(_back);
            footer.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
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
            Button Rail(string title, int index) => Kit.Button(_tr(title), null, () => { if (index <= _furthest && !_busy) Go(index); }, flat: true);
            AddPage(new Page { Id = "welcome", Title = "Welcome", Body = BuildWelcome(), RailButton = Rail("Welcome", 0), NextText = "Start" });
            AddPage(new Page { Id = SetupSteps.Look.Id, Title = SetupSteps.Look.Title, Step = SetupSteps.Look, Body = BuildLook(), RailButton = Rail(SetupSteps.Look.Title, 1), OnNext = () => { _hooks.Steps.MarkPageDone(SetupSteps.Look); return Task.FromResult(true); } });
            AddPage(new Page { Id = SetupSteps.Engine.Id, Title = SetupSteps.Engine.Title, Step = SetupSteps.Engine, Body = BuildEngine(), RailButton = Rail(SetupSteps.Engine.Title, 2), OnEnter = RefreshEnginePage });
            AddPage(new Page { Id = SetupSteps.Multiworld.Id, Title = SetupSteps.Multiworld.Title, Step = SetupSteps.Multiworld, Body = BuildMultiworld(), RailButton = Rail(SetupSteps.Multiworld.Title, 3), OnEnter = RefreshMultiworldPage, OnNext = MultiworldNextAsync });
            AddPage(new Page { Id = SetupSteps.Slots.Id, Title = SetupSteps.Slots.Title, Step = SetupSteps.Slots, Body = BuildSlots(), RailButton = Rail(SetupSteps.Slots.Title, 4), OnEnter = RefreshSlotsPage, OnNext = SlotsNextAsync });
            AddPage(new Page { Id = SetupSteps.Games.Id, Title = SetupSteps.Games.Title, Step = SetupSteps.Games, Body = BuildGames(), RailButton = Rail(SetupSteps.Games.Title, 5), OnEnter = RefreshGamesPage, OnNext = GamesNextAsync });
            AddPage(new Page { Id = SetupSteps.Trackers.Id, Title = SetupSteps.Trackers.Title, Step = SetupSteps.Trackers, Body = BuildTrackers(), RailButton = Rail(SetupSteps.Trackers.Title, 6), OnEnter = RefreshTrackersPage, OnNext = TrackersNextAsync });
            AddPage(new Page { Id = SetupSteps.Reports.Id, Title = SetupSteps.Reports.Title, Step = SetupSteps.Reports, Body = BuildReports(), RailButton = Rail(SetupSteps.Reports.Title, 7), OnNext = ReportsNextAsync });
            AddPage(new Page { Id = "ready", Title = "Ready", Body = BuildReady(), RailButton = Rail("Ready", 8), OnEnter = RefreshReadyPage, OnNext = FinishAsync, NextText = "Finish" });
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
                page.RailButton.Disabled = i > _furthest;
                page.RailButton.TooltipText = done ? _tr("Done") : skipped ? _tr("Skipped") : i > _furthest ? _tr("Next leads here.") : "";
                if (i == _current) page.RailButton.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = ThemeColors.AccentTint, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 3, ContentMarginBottom = 3 });
                else page.RailButton.RemoveThemeStyleboxOverride("normal");
            }
        }

        private void Skip()
        {
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
            _next.Disabled = true;
            try
            {
                bool go = page.OnNext == null || await page.OnNext();
                if (!IsInstanceValid(this)) return;
                if (!go) return;
                if (page.Step != null) _hooks.Steps.SetSkipped(page.Step, false);
                if (_current == _pages.Count - 1) return; // Finish closed the window
                Go(_current + 1);
            }
            finally
            {
                _busy = false;
                if (IsInstanceValid(this) && !IsQueuedForDeletion()) RefreshNext();
            }
        }

        /// <summary>Next's state: on the Ready page it waits for an engine run.</summary>
        private void RefreshNext()
        {
            if (_current < 0) return;
            bool waiting = _pages[_current].Id == "ready" && _engineRun.Running;
            _next.Disabled = _busy || waiting;
            if (_pages[_current].Id == "ready") _next.Text = waiting ? _tr("Waiting for the Atlas Engine…") : _tr("Finish");
        }

        private void OnEngineRunChanged()
        {
            _engineRun.Visible = true;
            _railEngineTitle.Visible = true;
            RefreshEnginePage();
            if (_current >= 0 && _pages[_current].Id == "ready") RefreshReadyPage();
            RefreshRail();
            RefreshNext();
        }

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

        // ---- Look ----

        private Control BuildLook()
        {
            var box = PageBox("Look", "How Atlas looks. Each choice applies as you make it, so pick what reads best to you; every one of them is in Settings → Appearance later.");
            var grid = Grid(box);
            var theme = new OptionButton { Name = "ThemeChoice", AccessibilityName = _tr("Theme") };
            foreach (var (_, title) in ThemeColors.ThemeChoices) theme.AddItem(_tr(title));
            theme.Selected = Math.Max(0, Array.FindIndex(ThemeColors.ThemeChoices, c => c.Key == _hooks.Settings.Theme));
            theme.ItemSelected += index => _hooks.ApplyTheme(ThemeColors.ThemeChoices[(int)index].Key);
            Row(grid, _tr("Theme"), theme);
            var blind = new CheckButton { Name = "ColourBlindSwitch", Text = _tr("Colour-blind-safe colours"), ButtonPressed = _hooks.Settings.ColourBlindSafe, TooltipText = _tr("Blue for in logic and orange for out of logic, so nothing rests on red against green.") };
            blind.Toggled += on => _hooks.ApplyColourBlindSafe(on);
            Row(grid, _tr("Colours"), blind);
            var zoom = new OptionButton { Name = "ZoomChoice", AccessibilityName = _tr("Zoom") };
            foreach (int step in _hooks.ZoomSteps) zoom.AddItem(step + "%");
            int zoomIndex = _hooks.ZoomSteps.ToList().IndexOf(_hooks.Settings.UiZoom);
            zoom.Selected = Math.Max(0, zoomIndex);
            zoom.ItemSelected += index => _hooks.SetZoom(_hooks.ZoomSteps[(int)index]);
            Row(grid, _tr("Zoom"), zoom);
            box.AddChild(Wrapped(_tr("The theme's full colours reach every part of the window after a restart; the frame, headings and lists change at once. Ctrl+= and Ctrl+- change the zoom any time."), muted: true));
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
                _engineState.Text = _tr("The setup is running: its progress shows on the left, and you can go on to the next pages meanwhile. The Ready page waits for it.");
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

        // ---- Your multiworld ----

        private OptionButton _which = null!;
        private Label _whichLabel = null!;
        private LineEdit _name = null!, _roomLink = null!, _server = null!, _password = null!;
        private VBoxContainer _roomConsentHost = null!;
        private ConsentRow? _roomConsent;
        private Label _multiworldProblem = null!;
        private List<MultiworldProfile> _choices = new();

        private Control BuildMultiworld()
        {
            var box = PageBox("Your multiworld", "The room you play in. Paste the room link the host shared and Atlas fills in the server and the players from the room's status page (one small read, asked below); or type the server yourself.");
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
            box.AddChild(Wrapped(_tr("The Multiworlds page holds the rest later: each slot's details, Cheese Tracker and the host's Sphere Tracker room."), muted: true));
            _multiworldProblem = Problem(box);
            return box;
        }

        private void RefreshMultiworldPage()
        {
            string? keep = _choices.Count > 0 && _which.Selected > 0 && _which.Selected - 1 < _choices.Count ? _choices[_which.Selected - 1].Id : _profile?.Id;
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
        }

        private MultiworldProfile? ChosenExisting => _which.Selected > 0 && _which.Selected - 1 < _choices.Count ? _choices[_which.Selected - 1] : null;

        private void FillFromChoice()
        {
            var chosen = ChosenExisting;
            _name.Text = chosen?.Name ?? "";
            _roomLink.Text = chosen?.RoomLink ?? "";
            _server.Text = chosen?.ServerUrl ?? "";
            _password.Text = chosen?.Password ?? "";
            RefreshRoomConsent();
        }

        /// <summary>A room link the status read would need permission for shows the permission's row under it.</summary>
        private void RefreshRoomConsent()
        {
            var room = AP_Atlas.Core.Rooms.RoomLinks.Parse(_roomLink.Text, out _);
            bool needs = room != null && !Permissions.IsAllowed(_hooks.Settings, Permissions.RoomStatusReads) && _roomConsent?.Answered != true;
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

        private async Task<bool> MultiworldNextAsync()
        {
            Say(_multiworldProblem, null);
            if (_name.Text.Trim().Length == 0)
            {
                Say(_multiworldProblem, _tr("Give the multiworld a name first."));
                return false;
            }
            _roomPlayers = new List<string>();
            var room = AP_Atlas.Core.Rooms.RoomLinks.Parse(_roomLink.Text, out _);
            if (room != null && Permissions.IsAllowed(_hooks.Settings, Permissions.RoomStatusReads))
            {
                _next.Text = _tr("Reading the room…");
                var (status, error) = await AP_Atlas.Core.Rooms.RoomLinks.ReadAsync(room.Value.Site, room.Value.Id);
                if (!IsInstanceValid(this)) return false;
                if (status == null)
                {
                    Say(_multiworldProblem, _tr("The room's status couldn't be read: {0}. Type the server, or try again.").Replace("{0}", error ?? ""));
                    return false;
                }
                if (status.Port > 0) _server.Text = status.ServerAddress(room.Value.Site);
                _roomPlayers = status.Players.ToList();
            }
            if (_server.Text.Trim().Length == 0 && room == null)
            {
                Say(_multiworldProblem, _tr("Type the server address (archipelago.gg and the room's port), or paste the room link."));
                return false;
            }
            return true;
        }

        // ---- Your slots ----

        private VBoxContainer _playersBox = null!, _slotRows = null!;
        private Label _playersTitle = null!, _slotsProblem = null!;
        private readonly List<CheckBox> _playerChecks = new();
        private readonly List<string> _slotNamesShownFor = new();

        private Control BuildSlots()
        {
            var box = PageBox("Your slots", "Which of the room's slots are yours. Tick the room's players you play, or type a slot's name; a YAML names a slot and its game at once. Each slot's game lets the next page set it up; Atlas learns it when the slot first connects otherwise.");
            _playersTitle = Kit.Text(_tr("The room's players"));
            _playersTitle.Visible = false;
            box.AddChild(_playersTitle);
            _playersBox = new VBoxContainer { Name = "PlayersBox" };
            _playersBox.AddThemeConstantOverride("separation", 2);
            box.AddChild(_playersBox);
            box.AddChild(Kit.Text(_tr("Slots")));
            _slotRows = new VBoxContainer { Name = "SlotsBox" };
            _slotRows.AddThemeConstantOverride("separation", 4);
            box.AddChild(_slotRows);
            var buttons = new HBoxContainer();
            buttons.AddThemeConstantOverride("separation", 6);
            var add = Kit.Button(_tr("+ Add slot"), _tr("Another slot you play in this multiworld."), () => AddSlotRow("", null, null), small: true);
            add.Name = "AddSlotRow";
            buttons.AddChild(add);
            var addYaml = Kit.Button(_tr("Add YAML…"), _tr("Keeps a player YAML in Atlas's YAML folder and adds a slot for each player it names, with its game and the YAML tied to it."), () => _hooks.PickYaml(path =>
            {
                var kept = _hooks.KeepYaml(path);
                if (kept != null && IsInstanceValid(this)) TakePlayers(kept.Value.Kept, kept.Value.Players);
            }), small: true);
            addYaml.Name = "AddYamlButton";
            buttons.AddChild(addYaml);
            box.AddChild(buttons);
            _slotsProblem = Problem(box);
            return box;
        }

        private void RefreshSlotsPage()
        {
            // The room's players, each a tick (one ticked already when the multiworld had it as a slot).
            var existing = ChosenExisting;
            foreach (Node child in _playersBox.GetChildren()) { _playersBox.RemoveChild(child); child.QueueFree(); }
            _playerChecks.Clear();
            foreach (string player in _roomPlayers)
            {
                var check = new CheckBox { Text = player, ButtonPressed = existing?.Slots.Contains(player) == true };
                check.SetMeta("player", player);
                _playersBox.AddChild(check);
                _playerChecks.Add(check);
            }
            _playersTitle.Visible = _roomPlayers.Count > 0;
            _playersBox.Visible = _roomPlayers.Count > 0;
            // The typed rows: the existing multiworld's named slots once, else one empty row.
            string key = existing?.Id ?? "";
            if (!_slotNamesShownFor.Contains(key))
            {
                _slotNamesShownFor.Add(key);
                if (existing != null)
                    foreach (string slot in existing.Slots.Where(s => !SetupSteps.IsPlaceholderSlot(s) && !_roomPlayers.Contains(s)))
                        AddSlotRow(slot, existing.SavedStats.TryGetValue(slot, out var stats) ? stats.GameName : null, null);
            }
            // Without the room's players, there's always a row to type into.
            if (!_slotRows.GetChildren().OfType<HBoxContainer>().Any(r => !r.IsQueuedForDeletion()) && _roomPlayers.Count == 0) AddSlotRow("", null, null);
            WindowFit.RequestShrink(this);
        }

        private HBoxContainer AddSlotRow(string slotName, string? game, string? yaml)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            var edit = new LineEdit { Text = slotName, PlaceholderText = _tr("The slot's name in the room"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = _tr("Slot name") };
            row.AddChild(edit);
            var gameEdit = new LineEdit { Text = game ?? "", PlaceholderText = _tr("Game (optional)"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = _tr("Game"), TooltipText = _tr("The game as Archipelago names it; the next page sets it up.") };
            row.AddChild(gameEdit);
            if (yaml != null) row.SetMeta("yaml", yaml);
            row.AddChild(Kit.Button("×", _tr("Removes this slot"), () =>
            {
                _slotRows.RemoveChild(row);
                row.QueueFree();
            }, small: true));
            _slotRows.AddChild(row);
            return row;
        }

        private void TakePlayers(string kept, List<(string Name, List<string> Games)> players)
        {
            // An untouched empty row gives way to the YAML's first player.
            var rows = _slotRows.GetChildren().OfType<HBoxContainer>().ToList();
            if (rows.Count == 1 && rows[0].GetChild<LineEdit>(0).Text.Trim().Length == 0)
            {
                _slotRows.RemoveChild(rows[0]);
                rows[0].QueueFree();
            }
            foreach (var (playerName, games) in players)
            {
                // A name with a placeholder is filled in by Archipelago when it rolls: the room's name is the user's to type.
                string name = string.IsNullOrWhiteSpace(playerName) || playerName.Contains('{') ? "" : playerName;
                var row = AddSlotRow(name, games.Count == 1 ? games[0] : null, kept);
                if (name.Length == 0) row.GetChild<LineEdit>(0).PlaceholderText = _tr("{0} names its player \"{1}\": type the slot's name in the room").Replace("{0}", System.IO.Path.GetFileName(kept)).Replace("{1}", string.IsNullOrWhiteSpace(playerName) ? "?" : playerName);
            }
            WindowFit.RequestShrink(this);
        }

        /// <summary>A game as typed, matched to one Atlas knows (its exact name, else the one name that starts with it); the text itself otherwise.</summary>
        private string ResolveGame(string typed)
        {
            typed = typed.Trim();
            if (typed.Length == 0) return "";
            var known = _hooks.KnownGames();
            var exact = known.FirstOrDefault(g => string.Equals(g, typed, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var starts = known.Where(g => g.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList();
            return starts.Count == 1 ? starts[0] : typed;
        }

        private Task<bool> SlotsNextAsync()
        {
            Say(_slotsProblem, null);
            var names = new List<string>();
            var yamlBySlot = new Dictionary<string, string>();
            var gameBySlot = new Dictionary<string, string>();
            foreach (var check in _playerChecks.Where(c => c.ButtonPressed))
            {
                string player = check.GetMeta("player").AsString();
                if (!names.Contains(player)) names.Add(player);
            }
            foreach (var row in _slotRows.GetChildren().OfType<HBoxContainer>())
            {
                string slot = row.GetChild<LineEdit>(0).Text.Trim();
                if (slot.Length == 0 || names.Contains(slot)) continue;
                names.Add(slot);
                string game = ResolveGame(row.GetChild<LineEdit>(1).Text);
                if (game.Length > 0) gameBySlot[slot] = game;
                if (row.HasMeta("yaml")) yamlBySlot[slot] = row.GetMeta("yaml").AsString();
            }
            if (names.Count == 0)
            {
                Say(_slotsProblem, _tr("Tick or type at least one slot: the one you play in this multiworld."));
                return Task.FromResult(false);
            }
            var draft = new SetupMultiworldDraft(ChosenExisting?.Id, _name.Text.Trim(), _roomLink.Text.Trim(), _server.Text.Trim(), _password.Text, names, yamlBySlot, gameBySlot);
            _profile = _hooks.SaveMultiworld(draft);
            return Task.FromResult(true);
        }

        // ---- Games and map packs ----

        private VBoxContainer _gamesList = null!, _gitHubConsentHost = null!;
        private Label _gamesStatus = null!;
        private ConsentRow? _gitHubConsent;
        private readonly List<(string Game, CheckBox? Apworld, string? Repo, CheckBox? Pack)> _gameRows = new();

        private Control BuildGames()
        {
            var box = PageBox("Games and map packs", "What each of your slots' games needs: its apworld in the Atlas Engine (its logic), and a map pack (its checks on the game's map). Atlas installs what's ticked when you press Install and continue; a project is trusted by its tick, and nothing is downloaded from one you didn't tick.");
            _gamesList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _gamesList.AddThemeConstantOverride("separation", 8);
            box.AddChild(_gamesList);
            _gitHubConsentHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddChild(_gitHubConsentHost);
            _gamesStatus = Wrapped("", muted: true);
            _gamesStatus.Visible = false;
            box.AddChild(_gamesStatus);
            return box;
        }

        private IEnumerable<string> SlotGames()
        {
            var profile = Profile;
            return profile == null ? Enumerable.Empty<string>()
                : profile.Slots.Select(slot => profile.SavedStats.TryGetValue(slot, out var stats) ? stats.GameName : null).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g!).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private void RefreshGamesPage()
        {
            foreach (Node child in _gamesList.GetChildren()) { _gamesList.RemoveChild(child); child.QueueFree(); }
            _gameRows.Clear();
            var games = SlotGames().ToList();
            if (games.Count == 0)
            {
                _gamesList.AddChild(Wrapped(_tr("None of your slots names its game yet. Atlas learns a slot's game when it first connects; the Games page then sets each one up (its apworld, a map pack, your YAML), and Add YAML… on the slots page names a game now."), muted: true));
            }
            foreach (string game in games)
            {
                var card = Card(out var cbox);
                cbox.AddChild(Kit.Heading(game, 1.05f));
                var status = _hooks.GameStatus(game);
                cbox.AddChild(Wrapped(status.Text, muted: true, width: PageWidth - 40));
                CheckBox? apworld = null;
                string? repo = null;
                bool installed = _hooks.ApworldInstalled(game);
                if (!installed && _hooks.EngineReady() && _hooks.KnownProject(game) is { } project)
                {
                    repo = project;
                    apworld = new CheckBox { Text = _tr("Trust github.com/{0} and install its newest apworld").Replace("{0}", project), ButtonPressed = true };
                    apworld.SetMeta("apworld_tick", game);
                    apworld.Toggled += _ => RefreshGitHubConsent();
                    cbox.AddChild(apworld);
                }
                CheckBox? pack = null;
                if (_hooks.PackInstalledFor(game)) cbox.AddChild(Wrapped(_tr("A map pack for it is installed."), muted: true, width: PageWidth - 40));
                else
                {
                    string? recommended = _hooks.RecommendedPack(game);
                    // The guide's pack when it names one; else the pack Atlas finds fits the game's names best (it tries the
                    // projects GitHub finds), so no game's pack has to be chosen by hand.
                    pack = new CheckBox
                    {
                        Text = recommended != null ? _tr("Install the map pack its setup guide recommends (github.com/{0})").Replace("{0}", recommended)
                            : _tr("Install the map pack that fits it (Atlas tries the packs GitHub finds)"),
                        ButtonPressed = true
                    };
                    pack.SetMeta("pack_tick", game);
                    cbox.AddChild(pack);
                    var searchRow = new HBoxContainer();
                    searchRow.AddThemeConstantOverride("separation", 8);
                    var results = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    results.AddThemeConstantOverride("separation", 4);
                    var searchStatus = Kit.Muted("");
                    searchStatus.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                    string target = game;
                    Button search = null!;
                    search = Kit.Button(_tr("Search GitHub for a pack…"), _tr("One search of GitHub for the game's PopTracker packs; what it finds is listed here, each with Install."), () =>
                    {
                        search.Disabled = true;
                        searchStatus.Text = _tr("Searching…");
                        Async.Fire(async () =>
                        {
                            var (candidates, problem) = await _hooks.SearchPacksAsync(target);
                            if (!IsInstanceValid(this)) return;
                            search.Disabled = false;
                            searchStatus.Text = problem != null ? _tr("GitHub couldn't be searched: {0}").Replace("{0}", problem) : candidates.Count == 0 ? _tr("No pack found on GitHub.") : "";
                            ShowCandidates(results, target, candidates);
                        }, "searching GitHub for " + target + " map packs");
                    }, small: true);
                    searchRow.AddChild(search);
                    searchRow.AddChild(searchStatus);
                    cbox.AddChild(searchRow);
                    cbox.AddChild(results);
                }
                _gamesList.AddChild(card);
                _gameRows.Add((game, apworld, repo, pack));
            }
            RefreshGitHubConsent();
            _next.Text = _tr(AnyInstallTicked() ? "Install and continue" : "Next");
            WindowFit.RequestShrink(this);
        }

        private bool AnyInstallTicked() => _gameRows.Any(r => (r.Apworld?.ButtonPressed ?? false) || (r.Pack?.ButtonPressed ?? false));

        /// <summary>Installing an apworld reads the project's releases on GitHub: that permission is asked on the page when it isn't kept.</summary>
        private void RefreshGitHubConsent()
        {
            bool needs = _gameRows.Any(r => r.Apworld?.ButtonPressed == true) && !Permissions.IsAllowed(_hooks.Settings, Permissions.GitHubLookups) && _gitHubConsent?.Answered != true;
            if (_gitHubConsent == null && needs)
            {
                _gitHubConsent = new ConsentRow(this, Permissions.GitHubLookups, null, _tr("Not now"), allowed =>
                {
                    if (!allowed) foreach (var row in _gameRows) if (row.Apworld != null) row.Apworld.ButtonPressed = false;
                    RefreshGitHubConsent();
                });
                _gitHubConsentHost.AddChild(_gitHubConsent.Panel);
            }
            _gitHubConsentHost.Visible = needs;
            if (_current >= 0 && _pages[_current].Id == SetupSteps.Games.Id) _next.Text = _tr(AnyInstallTicked() ? "Install and continue" : "Next");
        }

        private void ShowCandidates(VBoxContainer into, string game, List<SetupPackCandidate> candidates)
        {
            foreach (Node child in into.GetChildren()) { into.RemoveChild(child); child.QueueFree(); }
            foreach (var c in candidates)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                row.SetMeta("pack_row", c.Repo);
                string repo = c.Repo;
                var line = Kit.Muted("");
                Button install = null!;
                install = Kit.Button(_tr("Install"), _tr("Downloads and installs the project's newest release that fits the game's names; nothing else is asked."), () =>
                {
                    install.Disabled = true;
                    Async.Fire(async () =>
                    {
                        var (ok, detail, file) = await _hooks.InstallPackAsync(game, repo, said => Ui.Defer(this, () => line.Text = said));
                        if (!IsInstanceValid(this)) return;
                        if (ok && file != null) _hooks.PreferPack(game, System.IO.Path.GetFileName(file));
                        line.Text = ok ? _tr("Installed: {0}").Replace("{0}", detail) : _tr("Not installed: {0}").Replace("{0}", detail);
                        install.Disabled = !ok;
                    }, "installing a map pack from " + repo);
                }, small: true);
                row.AddChild(install);
                string fork = c.Fork ? " · " + _tr("a fork") : "";
                string recommended = c.Recommended ? _tr("Recommended by the game's setup guide") + " · " : "";
                string fits = c.Fit is int fit ? _tr("fits {0}% of the game's checks").Replace("{0}", fit.ToString()) + " · " : "";
                var info = new Label
                {
                    Text = recommended + fits + c.Repo + "  ★" + c.Stars + fork + (string.IsNullOrWhiteSpace(c.Description) ? "" : " · " + c.Description),
                    ClipText = true,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    VerticalAlignment = VerticalAlignment.Center,
                    TooltipText = c.Description ?? ""
                };
                row.AddChild(info);
                into.AddChild(row);
                // What Atlas's pack lab found about it, under its row.
                foreach (string sentence in c.Notes ?? Array.Empty<string>())
                {
                    var noted = Kit.Muted(sentence);
                    noted.SetMeta("pack_note", c.Repo);
                    noted.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Warning);
                    into.AddChild(noted);
                }
                into.AddChild(line);
            }
            WindowFit.RequestShrink(this);
        }

        private async Task<bool> GamesNextAsync()
        {
            var work = _gameRows.Where(r => (r.Apworld?.ButtonPressed ?? false) || (r.Pack?.ButtonPressed ?? false)).ToList();
            if (work.Count > 0 && _gameRows.Any(r => r.Apworld?.ButtonPressed == true) && !Permissions.IsAllowed(_hooks.Settings, Permissions.GitHubLookups))
            {
                _gamesStatus.Text = _tr("Answer the GitHub question above first (or untick the apworld installs).");
                _gamesStatus.Visible = true;
                return false;
            }
            var problems = new List<string>();
            foreach (var (game, apworld, repo, pack) in work)
            {
                if (apworld?.ButtonPressed == true && repo != null)
                {
                    _gamesStatus.Text = _tr("Installing {0}'s apworld from github.com/{1}…").Replace("{0}", game).Replace("{1}", repo);
                    _gamesStatus.Visible = true;
                    AP_Atlas.Core.EngineSetup.ApworldSources.ApproveRepo(_hooks.Settings, repo);
                    bool ok = await _hooks.InstallApworldAsync(game, this);
                    if (!IsInstanceValid(this)) return false;
                    if (!ok) problems.Add(_tr("{0}'s apworld wasn't installed (the Games page says why).").Replace("{0}", game));
                    apworld.ButtonPressed = ok ? false : apworld.ButtonPressed;
                    apworld.Disabled = ok;
                }
                if (pack?.ButtonPressed == true)
                {
                    string? recommended = _hooks.RecommendedPack(game);
                    if (recommended == null)
                    {
                        if (_hooks.PackInstalledFor(game)) continue;
                        _gamesStatus.Text = _tr("Finding the map pack that fits {0}…").Replace("{0}", game);
                        _gamesStatus.Visible = true;
                        var (picked, why, chosenFile) = await _hooks.PickPackAsync(game, said => Ui.Defer(this, () => _gamesStatus.Text = said));
                        if (!IsInstanceValid(this)) return false;
                        if (picked && chosenFile != null) _hooks.PreferPack(game, System.IO.Path.GetFileName(chosenFile));
                        if (!picked) problems.Add(_tr("{0}: no map pack was installed ({1}); Search GitHub for a pack… lists what there is.").Replace("{0}", game).Replace("{1}", why));
                        pack.ButtonPressed = picked ? false : pack.ButtonPressed;
                        pack.Disabled = picked;
                        continue;
                    }
                    _gamesStatus.Text = _tr("Installing {0}'s map pack from github.com/{1}…").Replace("{0}", game).Replace("{1}", recommended);
                    _gamesStatus.Visible = true;
                    var (packOk, detail, file) = await _hooks.InstallPackAsync(game, recommended, said => Ui.Defer(this, () => _gamesStatus.Text = said));
                    if (!IsInstanceValid(this)) return false;
                    if (packOk && file != null) _hooks.PreferPack(game, System.IO.Path.GetFileName(file));
                    if (!packOk) problems.Add(_tr("{0}'s map pack wasn't installed: {1}").Replace("{0}", game).Replace("{1}", detail));
                    pack.ButtonPressed = packOk ? false : pack.ButtonPressed;
                    pack.Disabled = packOk;
                }
            }
            if (problems.Count > 0)
            {
                _gamesStatus.Text = string.Join("\n", problems) + "\n" + _tr("Next goes on without them; the Games page can finish each one later.");
                _gamesStatus.Visible = true;
                _next.Text = _tr("Next");
                foreach (var row in _gameRows) { if (row.Apworld != null) row.Apworld.ButtonPressed = false; if (row.Pack != null) row.Pack.ButtonPressed = false; }
                return false;
            }
            _gamesStatus.Visible = false;
            _hooks.Steps.MarkPageDone(SetupSteps.Games);
            return true;
        }

        // ---- Trackers ----

        private LineEdit _cheese = null!, _sphere = null!;
        private Label _trackersFor = null!;

        private Control BuildTrackers()
        {
            var box = PageBox("Trackers (optional)", "Two trackers many groups use. Cheese Tracker is the shared tracker of an async multiworld: paste its page (or the archipelago.gg room link). spheretracker.de shows a multiworld sphere by sphere: paste the room the host created and shared; Atlas uses the host's room and no other.");
            _trackersFor = Kit.Muted("");
            box.AddChild(_trackersFor);
            var grid = Grid(box);
            _cheese = new LineEdit { Name = "CheeseBox", PlaceholderText = _tr("Cheese Tracker link, or the archipelago.gg room link (optional)"), AccessibilityName = _tr("Cheese Tracker link") };
            Row(grid, _tr("Cheese Tracker"), _cheese);
            _sphere = new LineEdit { Name = "SphereBox", PlaceholderText = _tr("https://spheretracker.de/room/… (optional)"), AccessibilityName = _tr("Sphere Tracker room link") };
            Row(grid, _tr("Sphere Tracker"), _sphere);
            box.AddChild(Wrapped(_tr("Both are checked online when you press Next, and the Cheese Tracker and Sphere Tracker tools then show them. Leave them empty, or Skip this step, if your group uses neither."), muted: true));
            return box;
        }

        private void RefreshTrackersPage()
        {
            var profile = Profile;
            _trackersFor.Text = profile == null ? _tr("No multiworld yet: the pages before make one.") : _tr("For {0}.").Replace("{0}", profile.Name);
            if (profile != null)
            {
                if (_cheese.Text.Length == 0) _cheese.Text = profile.CheeseTrackerUrl ?? "";
                if (_sphere.Text.Length == 0) _sphere.Text = profile.SphereTrackerUrl ?? "";
            }
        }

        private Task<bool> TrackersNextAsync()
        {
            var profile = Profile;
            if (profile != null)
            {
                string cheese = _cheese.Text.Trim(), sphere = _sphere.Text.Trim();
                if (cheese.Length > 0 && cheese != (profile.CheeseTrackerUrl ?? "")) _hooks.LinkCheese(profile, cheese);
                if (sphere.Length > 0 && sphere != (profile.SphereTrackerUrl ?? "")) _hooks.LinkSphere(this, profile, sphere);
            }
            _hooks.Steps.MarkPageDone(SetupSteps.Trackers);
            return Task.FromResult(true);
        }

        // ---- Reports and updates ----

        private CheckButton _dailyChecks = null!, _crashAlways = null!;

        private Control BuildReports()
        {
            var box = PageBox("Reports and updates", "Two things Atlas can do online by itself, both off until you say so. Each is a permission: Settings → Privacy & permissions lists it and takes it back.");
            var updates = Card(out var ubox);
            _dailyChecks = new CheckButton { Name = "DailyChecksSwitch", Text = _tr("Check GitHub once a day for a newer Atlas"), ButtonPressed = _hooks.Settings.UpdateCheckDaily && Permissions.IsAlwaysAllowed(_hooks.Settings, Permissions.UpdateChecks) };
            ubox.AddChild(_dailyChecks);
            ubox.AddChild(Wrapped(_tr(Permissions.UpdateChecks.Explanation), muted: true, width: PageWidth - 40));
            box.AddChild(updates);
            var crashes = Card(out var cbox);
            _crashAlways = new CheckButton { Name = "CrashReportsSwitch", Text = _tr("Send crash reports to Atlas's developer without asking each time"), ButtonPressed = Permissions.IsAlwaysAllowed(_hooks.Settings, Permissions.CrashReports) };
            cbox.AddChild(_crashAlways);
            cbox.AddChild(Wrapped(_tr(Permissions.CrashReports.Explanation), muted: true, width: PageWidth - 40));
            cbox.AddChild(Wrapped(_tr("Off, Atlas still offers to send a report after a problem, and shows it first; you decide each time."), muted: true, width: PageWidth - 40));
            box.AddChild(crashes);
            return box;
        }

        private Task<bool> ReportsNextAsync()
        {
            _hooks.DailyUpdateChecks(_dailyChecks.ButtonPressed);
            _hooks.CrashReportsAlways(_crashAlways.ButtonPressed);
            _hooks.Steps.MarkPageDone(SetupSteps.Reports);
            return Task.FromResult(true);
        }

        // ---- Ready ----

        private VBoxContainer _summary = null!, _connectBox = null!;
        private readonly List<CheckBox> _connectChecks = new();

        private Control BuildReady()
        {
            var box = PageBox("Ready", "What's set up, and what Finish does.");
            _summary = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _summary.AddThemeConstantOverride("separation", 4);
            box.AddChild(_summary);
            _connectBox = new VBoxContainer { Name = "ConnectBox", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _connectBox.AddThemeConstantOverride("separation", 2);
            box.AddChild(_connectBox);
            box.AddChild(Wrapped(_tr("Home shows your multiworlds from now on, with one line about the setup until every step is done; Help → Set up Atlas… brings this window back."), muted: true));
            return box;
        }

        private void RefreshReadyPage()
        {
            foreach (Node child in _summary.GetChildren()) { _summary.RemoveChild(child); child.QueueFree(); }
            foreach (Node child in _connectBox.GetChildren()) { _connectBox.RemoveChild(child); child.QueueFree(); }
            _connectChecks.Clear();
            void Line(string text, Color colour) => _summary.AddChild(Wrapped(text).Also(l => l.AddThemeColorOverride("font_color", colour)));
            bool ready = _hooks.EngineReady();
            Line(ready ? _tr("✓ The Atlas Engine is set up.") : _engineRun.Running ? _tr("… The Atlas Engine is still installing: Finish waits for it.") : _tr("✗ The Atlas Engine isn't set up: logic, maps and hints wait until it is (Home's setup line, or the Engine button on the bar)."),
                ready ? ThemeColors.Success : _engineRun.Running ? ThemeColors.Text : ThemeColors.Warning);
            var profile = Profile;
            if (profile == null) Line(_tr("✗ No multiworld yet: Back to Your multiworld makes one."), ThemeColors.Warning);
            else
            {
                Line(_tr("✓ {0}: {1}").Replace("{0}", profile.Name).Replace("{1}", string.IsNullOrWhiteSpace(profile.ServerUrl) ? _tr("no server address yet") : profile.ServerUrl), ThemeColors.Success);
                var games = SlotGames().ToList();
                Line(_tr("✓ Slots: {0}").Replace("{0}", string.Join(", ", profile.Slots)) + (games.Count > 0 ? "  ·  " + string.Join(", ", games) : ""), ThemeColors.Success);
                bool hasServer = !string.IsNullOrWhiteSpace(profile.ServerUrl);
                _connectBox.AddChild(Kit.Text(_tr("Connect now")));
                foreach (string slot in profile.Slots)
                {
                    bool live = _hooks.IsSlotLive(profile.Id, slot);
                    var check = new CheckBox { Text = live ? _tr("{0} (connected)").Replace("{0}", slot) : slot, ButtonPressed = hasServer && !live, Disabled = live || !hasServer };
                    check.SetMeta("connect_slot", slot);
                    _connectBox.AddChild(check);
                    _connectChecks.Add(check);
                }
                if (!hasServer) _connectBox.AddChild(Wrapped(_tr("Connecting needs the server address: the Multiworlds page takes it any time."), muted: true));
            }
            RefreshNext();
        }

        private Task<bool> FinishAsync()
        {
            var profile = Profile;
            if (profile != null)
                foreach (var check in _connectChecks.Where(c => c.ButtonPressed && !c.Disabled))
                    _hooks.ConnectSlot(profile, check.GetMeta("connect_slot").AsString());
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
