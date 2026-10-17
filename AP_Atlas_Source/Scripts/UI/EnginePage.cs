#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core;
using AP_Atlas.Core.EngineSetup;
using Godot;
using Color = Godot.Color;

namespace AP_Atlas.UI
{
    /// <summary>What the Atlas Engine page needs from the window.</summary>
    public sealed class EngineHooks
    {
        public required AppSettings Settings { get; init; }
        public required Func<string, string> Tr { get; init; }
        /// <summary>The connected slots (their logic, each with its engine's state).</summary>
        public required Func<IEnumerable<SlotTrackerControl>> Slots { get; init; }
        /// <summary>Shows the Games page.</summary>
        public required Action OpenGamesPage { get; init; }
        /// <summary>The page's text size (the content setting).</summary>
        public required Func<int> FontSize { get; init; }
        /// <summary>Whether the page may start a health check by itself when it shows (not at startup, not in the visual check).</summary>
        public required Func<bool> MayCheckByItself { get; init; }
    }

    /// <summary>
    /// The Atlas Engine page (the Engine tool, last on the activity bar; it was a window of its own). On top, in plain words:
    /// whether logic can run, Set up everything or Check now with the run's progress, how each connected slot's world was
    /// rebuilt (with its fixes), and where games are set up. Under a folded Advanced: which engine logic runs on (Atlas's
    /// portable copy or the user's Archipelago install, with what Atlas may change there and a way to undo it), each part
    /// of the engine with its repair, the seed test (developer mode), the log and the engine's folder. Every download is
    /// pinned and hash-checked, and asked for first.
    /// </summary>
    public sealed partial class EnginePage : VBoxContainer
    {
        /// <summary>The Advanced section's key (its fold is remembered).</summary>
        public const string AdvancedKey = "engine:Advanced";

        private static readonly Color Good = ThemeColors.Success;
        private static readonly Color Bad = ThemeColors.Error;
        private static readonly Color Warn = ThemeColors.Warning;
        private static readonly Color Muted = ThemeColors.TextSubtle;

        private readonly EngineHooks _hooks;
        private readonly Func<string, string> _tr;
        private readonly AppSettings _settings;
        private readonly Label _status;
        private readonly Button _setupAll, _check, _cancel, _showAdvanced;
        private readonly ScrollContainer _scroll;
        private readonly EngineSetupProgress _run;
        private readonly VBoxContainer _slotsBox, _modeBox, _stepsBox;
        private readonly HBoxContainer _verifyRow;
        private readonly RichTextLabel _log;
        private readonly VBoxContainer _advanced;
        private string _slotsSignature = "";
        private string _runName = "";
        private string? _autoChecked; // the engine the page last checked by itself (a cancelled check isn't started again)

        public EnginePage(EngineHooks hooks)
        {
            _hooks = hooks;
            _tr = hooks.Tr;
            _settings = hooks.Settings;
            Name = "EnginePage";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;

            // Nothing on the page may need more width than the content area gives (the scroll never goes sideways, so a
            // wide row would widen the whole window's middle): rows of buttons flow, long paths wrap.
            var scroll = _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            AddChild(scroll);
            var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(side, 16);
            scroll.AddChild(margin);
            var page = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            page.AddThemeConstantOverride("separation", 10);
            margin.AddChild(page);

            // ---- On top: can logic run, and the one thing to do about it ----
            page.AddChild(Kit.Heading(_tr("Atlas Engine"), 1.4f));
            page.AddChild(Wrapped(Kit.Muted(_tr("Logic, maps and hints run on Archipelago's own code and the Universal Tracker: the Atlas Engine. Atlas keeps its own copy, or uses an Archipelago install you have (under Advanced)."))));
            _status = Wrapped(new Label());
            page.AddChild(_status);
            var actions = new HFlowContainer();
            actions.AddThemeConstantOverride("h_separation", 8);
            actions.AddThemeConstantOverride("v_separation", 6);
            _setupAll = Kit.Button(_tr("Set up everything"), _tr("Downloads and installs whatever is missing, then runs a health check (asked first; every file is checked against a fixed hash)."),
                () => WithDownloadPermission(() => Run(_tr("Setting up the Atlas Engine"), (log, progress, ct) => AtlasEngine.SetUpAsync(AtlasEngine.Current, log, progress, ct),
                    problem: () => AtlasEngine.LastSetupProblem)));
            actions.AddChild(_setupAll);
            _check = Kit.Button(_tr("Check now"), _tr("Runs the engine's health check: Python, Archipelago, the Universal Tracker and the games it can load."), RunCheck);
            actions.AddChild(_check);
            _cancel = Kit.Button(_tr("Cancel"), _tr("Stops the run at the step it's on; the engine is left as it was."), () => _run!.Cancel());
            _cancel.Visible = false;
            actions.AddChild(_cancel);
            _showAdvanced = Kit.Button(_tr("Show Advanced"), _tr("Opens Advanced, where this is fixed: which engine Atlas uses, what it may change in your Archipelago install, and each part."), () => ShowDetails(null));
            _showAdvanced.Visible = false;
            actions.AddChild(_showAdvanced);
            page.AddChild(actions);
            _run = new EngineSetupProgress(_tr, 0) { CancelOnExit = false, Visible = false };
            _run.RunChanged += running =>
            {
                _run.Visible = true;
                Render();
            };
            _run.LineLogged += Log;
            page.AddChild(_run);

            page.AddChild(Kit.Heading(_tr("Your slots"), 1.15f));
            _slotsBox = new VBoxContainer();
            _slotsBox.AddThemeConstantOverride("separation", 4);
            page.AddChild(_slotsBox);

            var gamesRow = new HBoxContainer();
            gamesRow.AddThemeConstantOverride("separation", 8);
            var gamesNote = Wrapped(Kit.Muted(_tr("Games are installed on the Games page (Install games…): each one's logic and a map pack in one step. A game's page has the rest: its versions under Advanced, and the files its setup needs.")));
            gamesRow.AddChild(gamesNote);
            gamesRow.AddChild(Kit.Button(_tr("Open the Games page"), _tr("Shows the Games page"), () => _hooks.OpenGamesPage()));
            page.AddChild(gamesRow);

            // ---- Advanced (folded): which engine, its parts, the seed test, the log ----
            _advanced = Kit.Section(page, _tr("Advanced"), AdvancedKey, true, _settings);
            _advanced.AddChild(Kit.Heading(_tr("Which engine"), 1.1f));
            _advanced.AddChild(Wrapped(Kit.Muted(_tr("Atlas can download and keep its own copy (about 55 MB, no installer or admin rights), or use an Archipelago install you already have."))));
            _modeBox = new VBoxContainer();
            _advanced.AddChild(_modeBox);
            _advanced.AddChild(Kit.Heading(_tr("Parts"), 1.1f));
            _stepsBox = new VBoxContainer();
            _stepsBox.AddThemeConstantOverride("separation", 6);
            _advanced.AddChild(_stepsBox);
            _verifyRow = new HBoxContainer();
            _verifyRow.AddThemeConstantOverride("separation", 8);
            _verifyRow.AddChild(Kit.Button(_tr("Verify logic against a seed…"),
                _tr("Pick a seed generated with Archipelago (the .zip in its output folder). Atlas rebuilds each world the way a live slot does, replays the seed's playthrough, and checks its logic matches the real one at every step."), PickSeed));
            _verifyRow.AddChild(Wrapped(Kit.Muted(_tr("Proves the logic for a game against the real generator, not just its location list (developer mode)."))));
            _advanced.AddChild(_verifyRow);
            var logHeader = new HBoxContainer();
            logHeader.AddThemeConstantOverride("separation", 8);
            var logTitle = Kit.Heading(_tr("Log"), 1.1f);
            logTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            logHeader.AddChild(logTitle);
            logHeader.AddChild(Kit.Button(_tr("Copy log"), _tr("Copies the log (for a problem report)"), () =>
            {
                DisplayServer.ClipboardSet(_log!.GetParsedText());
                SetStatus(_tr("Log copied."), Good);
            }, flat: true, small: true));
            logHeader.AddChild(Kit.Button(_tr("Open engine folder"), _tr("Shows the engine's files"), () =>
            {
                string dir = AtlasEngine.Current.Mode == EngineMode.Portable ? AtlasEngine.EngineDir : AtlasEngine.Current.Root;
                ExternalLinks.OpenFolder(dir);
            }, flat: true, small: true));
            _advanced.AddChild(logHeader);
            _log = new RichTextLabel { CustomMinimumSize = new Vector2(0, 220), ScrollFollowing = true, SelectionEnabled = true, BbcodeEnabled = false, ThemeTypeVariation = "LogText" };
            _log.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = ThemeColors.SurfaceDeep, ContentMarginLeft = 8, ContentMarginTop = 6, ContentMarginRight = 8, ContentMarginBottom = 6 });
            _advanced.AddChild(_log);

            var timer = new Godot.Timer { WaitTime = 1.5, Autostart = true };
            timer.Timeout += () => { if (IsVisibleInTree()) RefreshSlotsIfChanged(); };
            AddChild(timer);
        }

        public override void _EnterTree() => AtlasEngine.Changed += OnEngineChanged;

        public override void _ExitTree()
        {
            AtlasEngine.Changed -= OnEngineChanged;
            _run.Cancel(); // Atlas is closing
        }

        private void OnEngineChanged() => Ui.Defer(this, () => { if (IsVisibleInTree()) Render(); });

        /// <summary>
        /// The page shows: drawn again, and the health check run by itself when the engine is usable but hasn't been
        /// checked this session (not at startup, nor in the visual check).
        /// </summary>
        public void OnShown()
        {
            Render();
            var current = AtlasEngine.Current;
            if (!_run.Running && !AtlasEngine.SetupRunning && _hooks.MayCheckByItself() && _autoChecked != current.Root
                && AtlasEngine.ProblemWith(current) == null && AtlasEngine.LastCheck(current) == null)
            {
                _autoChecked = current.Root;
                RunCheck();
            }
        }

        /// <summary>Opens Advanced (Show details: the log and the parts), and adds another run's lines to the log.</summary>
        public void ShowDetails(IReadOnlyList<string>? lines)
        {
            if (Kit.FoldedState(this, AdvancedKey) == true) Kit.ToggleFold(this, AdvancedKey);
            foreach (string line in lines ?? Array.Empty<string>()) Log(line);
            // The opened part is laid out over the next frames; then the log (or, without lines, Advanced) comes into view.
            Control target = lines != null ? _log : _advanced;
            Ui.NextFrame(this, () => Ui.NextFrame(this, () => _scroll.EnsureControlVisible(target)));
        }

        /// <summary>Opens Advanced and brings the seed test into view (Verify logic against a seed under Debug; developer mode).</summary>
        public void ShowVerify()
        {
            if (Kit.FoldedState(this, AdvancedKey) == true) Kit.ToggleFold(this, AdvancedKey);
            _verifyRow.Visible = _settings.DeveloperMode;
            Ui.NextFrame(this, () => Ui.NextFrame(this, () => _scroll.EnsureControlVisible(_verifyRow)));
        }

        // ---- For tests ----

        /// <summary>The status line's text.</summary>
        public string StatusText => _status.Text;
        /// <summary>The setup button's text while it shows (Set up everything, or Update Archipelago), else null.</summary>
        public string? SetUpShown => _setupAll.Visible ? _setupAll.Text : null;
        /// <summary>Draws the page again now (no health check of its own, as showing it may start).</summary>
        public void RenderNow() => Render();
        /// <summary>Whether Advanced is open.</summary>
        public bool AdvancedOpen => Kit.FoldedState(this, AdvancedKey) == false;
        /// <summary>The log's text.</summary>
        public string LogText => _log.GetParsedText();
        /// <summary>The page's run (setup, repair, check).</summary>
        public EngineSetupProgress RunView => _run;
        /// <summary>Whether the seed test's row is shown (developer mode; it sits under Advanced).</summary>
        public bool VerifyShown => _verifyRow.Visible;
        /// <summary>Whether some of the log is inside the page's visible area.</summary>
        public bool LogInView => _log.IsVisibleInTree() && _scroll.GetGlobalRect().Intersects(_log.GetGlobalRect());
        /// <summary>Lays Your slots out again now, if the slots changed (the timer does this every 1.5 s while the page shows).</summary>
        public void RefreshSlotsNow() => RefreshSlotsIfChanged();
        /// <summary>The texts of the buttons in Your slots' rows.</summary>
        public IReadOnlyList<string> SlotButtonTexts => _slotsBox.FindChildren("*", "Button", true, false).OfType<Button>().Select(b => b.Text).ToList();

        // ---- Drawing ----

        private void Render()
        {
            var install = AtlasEngine.Current;
            bool busy = _run.Running;
            RenderMode(install, busy);
            RenderSteps(install, busy);
            RenderSlots();
            string? problem = AtlasEngine.ProblemWith(install);
            // Logic runs, but on another Archipelago than Atlas's: Set up everything updates it (the step under Advanced says so too).
            bool update = problem == null && AtlasEngine.ArchipelagoUpdateDue(install);
            bool canSetUp = install.Mode == EngineMode.Portable || (install.CanLaunch && AtlasEngine.MayWriteTo(install));
            _setupAll.Visible = (problem != null || update) && canSetUp;
            _setupAll.Text = update ? _tr("Update Archipelago") : _tr("Set up everything");
            _setupAll.TooltipText = update
                ? _tr("Puts Archipelago {0} ({1} from GitHub) in place of {2}, then runs a health check (asked first; the file is checked against a fixed hash).")
                    .Replace("{0}", AtlasEngine.ArchipelagoVersion).Replace("{1}", AtlasEngine.WorldsZipSize).Replace("{2}", AtlasEngine.State.Archipelago)
                : _tr("Downloads and installs whatever is missing, then runs a health check (asked first; every file is checked against a fixed hash).");
            _setupAll.Disabled = busy;
            _showAdvanced.Visible = problem != null && !canSetUp && !busy;
            _check.Disabled = busy;
            _cancel.Visible = busy;
            _verifyRow.Visible = _settings.DeveloperMode;
            if (busy) SetStatus(_runName + "…", ThemeColors.TextMuted);
            else if (problem != null) SetStatus(problem, Warn);
            else if (update) SetStatus(_tr("Archipelago needs updating: {0} → {1}.").Replace("{0}", AtlasEngine.State.Archipelago).Replace("{1}", AtlasEngine.ArchipelagoVersion), Warn);
            else if (AtlasEngine.LastCheck(install) is { } check && !check.Passed) SetStatus(_tr("Logic runs on the {0}, but its last health check found a problem: see Parts under Advanced.").Replace("{0}", install.Describe()), Warn);
            else SetStatus(_tr("Ready: logic runs on the {0}.").Replace("{0}", install.Describe()), Good);
            Ui.NextFrame(this, () => MainTrackerWindow.SetFontSizeRecursive(this, _hooks.FontSize()));
        }

        private void RenderMode(EngineInstall install, bool busy)
        {
            Clear(_modeBox);
            var group = new ButtonGroup();
            var portable = new CheckBox
            {
                Text = _tr("Atlas portable engine (recommended)") + (install.Mode == EngineMode.Portable ? _tr("  ← in use") : ""),
                ButtonGroup = group,
                ButtonPressed = install.Mode == EngineMode.Portable,
                TooltipText = _tr("Python {0}, Archipelago {1} and Universal Tracker {2}, kept in Atlas's PortableData folder")
                    .Replace("{0}", AtlasEngine.PythonVersion).Replace("{1}", AtlasEngine.ArchipelagoVersion).Replace("{2}", AtlasEngine.TrackerVersion),
                Disabled = busy
            };
            Readable(portable);
            portable.Toggled += on => { if (on && AtlasEngine.Current.Mode != EngineMode.Portable) AtlasEngine.SetMode(EngineMode.Portable); };
            _modeBox.AddChild(portable);
            _modeBox.AddChild(Note(AtlasEngine.IsShipped ? _tr("Ships with Atlas ({0}); what it adds is kept in {1}").Replace("{0}", AtlasEngine.CoreDir).Replace("{1}", AtlasEngine.EngineDir)
                : AtlasEngine.PortableInstalled ? _tr("Installed in {0}").Replace("{0}", AtlasEngine.EngineDir) : _tr("Not downloaded yet."), 28));

            var existingRow = new HBoxContainer();
            existingRow.AddThemeConstantOverride("separation", 8);
            string? path = _settings.ArchipelagoInstallationPath;
            var existing = new CheckBox
            {
                Text = _tr("My Archipelago install") + (install.Mode == EngineMode.Existing ? _tr("  ← in use") : ""),
                ButtonGroup = group,
                ButtonPressed = install.Mode == EngineMode.Existing,
                TooltipText = _tr("Use the Archipelago you installed. With your OK, Atlas adds its bridge apworld to its worlds folder (and can remove it again)."),
                Disabled = busy
            };
            Readable(existing);
            existing.Toggled += on =>
            {
                if (!on || AtlasEngine.Current.Mode == EngineMode.Existing) return;
                if (!string.IsNullOrEmpty(_settings.ArchipelagoInstallationPath) && EngineInstall.Existing(_settings.ArchipelagoInstallationPath).CanLaunch) SwitchToExisting(_settings.ArchipelagoInstallationPath);
                else PickInstallFolder();
            };
            existingRow.AddChild(existing);
            var change = Kit.Button(_tr("Choose folder…"), _tr("Choose the folder Archipelago is installed in (the one with ArchipelagoLauncher.exe)"), PickInstallFolder, enabled: !busy);
            existingRow.AddChild(change);
            var find = Kit.Button(_tr("Find…"), _tr("Look for Archipelago in the usual places on this PC (Atlas asks first)"), FindInstall, enabled: !busy);
            existingRow.AddChild(find);
            _modeBox.AddChild(existingRow);
            if (!string.IsNullOrEmpty(path)) _modeBox.AddChild(Note(_tr("Installed in {0}").Replace("{0}", path), 28));

            // What Atlas may do in that install, and a way to undo it.
            if (!string.IsNullOrEmpty(path) && EngineInstall.Existing(path).CanLaunch)
            {
                var target = EngineInstall.Existing(path);
                bool allowed = AtlasEngine.MayWriteTo(target);
                bool hasFiles = AtlasEngine.ChangesIn(path).Count > 0 || AtlasEngine.AtlasFilesIn(target).Count > 0;
                var consentRow = new HBoxContainer();
                consentRow.AddThemeConstantOverride("separation", 8);
                consentRow.AddChild(Note(allowed ? _tr("Atlas may add and update its files in this install.") : _tr("Atlas adds nothing to this install without your OK."), 28));
                if (!allowed)
                    consentRow.AddChild(Kit.Button(_tr("Allow…"), _tr("See exactly what Atlas adds to this install, then decide"), () => SwitchToExisting(path), enabled: !busy));
                if (hasFiles)
                    consentRow.AddChild(Kit.Button(_tr("Remove Atlas's files"), _tr("Undo everything Atlas added to or moved in this install"), () => RemoveAtlasFiles(target), enabled: !busy));
                _modeBox.AddChild(consentRow);
            }
        }

        private void RenderSteps(EngineInstall install, bool busy)
        {
            Clear(_stepsBox);
            foreach (var step in AtlasEngine.Steps(install))
            {
                // Every row as tall as one with a button, its text centred on it, so the list keeps an even rhythm.
                var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 34) };
                row.AddThemeConstantOverride("separation", 10);
                var (glyph, color) = step.State switch
                {
                    EngineStepState.Ok => ("✔", Good),
                    EngineStepState.Warning => ("▲", Warn),
                    EngineStepState.Error => ("✖", Bad),
                    _ => ("○", Muted)
                };
                var icon = new Label { Text = glyph, CustomMinimumSize = new Vector2(22, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                icon.AddThemeColorOverride("font_color", color);
                row.AddChild(icon);
                row.AddChild(new Label { Text = step.Title, CustomMinimumSize = new Vector2(170, 0), VerticalAlignment = VerticalAlignment.Center });
                var detail = new Label { Text = step.Detail, SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, VerticalAlignment = VerticalAlignment.Center };
                detail.AddThemeColorOverride("font_color", step.State == EngineStepState.Ok ? Muted : ThemeColors.TextMuted);
                row.AddChild(detail);
                if (step.Action != null)
                {
                    var id = step.Id;
                    var button = Kit.Button(step.Action, _tr("Repairs this part (asked first when it downloads)"), () => RunStep(id), enabled: !busy);
                    button.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                    row.AddChild(button);
                }
                _stepsBox.AddChild(row);
            }
        }

        private string SlotsSignature() =>
            string.Join("|", _hooks.Slots().Where(IsInstanceValid)
                .Select(s => $"{s.SlotName}:{s.EngineRunning}:{s.EngineBooting}:{s.EngineProblem?.Code}:{s.LinkedYamlSetting}:{s.EngineYamlInfo?["source"]}:{s.ApworldMatchesSeed}:{s.LogicAccuracy}:{s.SeedVersionNote}:{s.YamlWouldHelp}"));

        private void RefreshSlotsIfChanged()
        {
            if (SlotsSignature() == _slotsSignature) return;
            RenderSlots();
            Ui.NextFrame(this, () => MainTrackerWindow.SetFontSizeRecursive(_slotsBox, _hooks.FontSize()));
        }

        private void RenderSlots()
        {
            _slotsSignature = SlotsSignature();
            Clear(_slotsBox);
            var slots = _hooks.Slots().Where(IsInstanceValid).ToList();
            if (slots.Count == 0)
            {
                _slotsBox.AddChild(Note(_tr("Connect a slot to see how the engine rebuilds its world.")));
                return;
            }
            foreach (var slot in slots)
            {
                var entry = new VBoxContainer();
                entry.AddThemeConstantOverride("separation", 4);
                var line = new HBoxContainer();
                line.AddThemeConstantOverride("separation", 10);
                var (glyph, color, text) = SlotState(slot);
                var icon = new Label { Text = glyph, CustomMinimumSize = new Vector2(22, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                icon.AddThemeColorOverride("font_color", color);
                line.AddChild(icon);
                line.AddChild(new Label { Text = $"{slot.SlotName} ({slot.Game})", CustomMinimumSize = new Vector2(160, 0), ClipText = true });
                var detail = new Label { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                detail.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
                line.AddChild(detail);
                entry.AddChild(line);
                var row = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("h_separation", 8);
                row.AddThemeConstantOverride("v_separation", 4);
                // A YAML is offered only when it would help: the engine asked for one, or the rebuilt world differs from the
                // server's. A linked one can be changed or unlinked.
                if (slot.LinkedYamlSetting != null || slot.YamlWouldHelp)
                    row.AddChild(Kit.Button(slot.LinkedYamlSetting == null ? _tr("Link YAML…") : _tr("Change YAML…"),
                        _tr("The player YAML used to generate this seed. Only needed when the server's data can't rebuild the world."), slot.PickYaml, enabled: !slot.EngineBooting));
                if (slot.LinkedYamlSetting != null)
                    row.AddChild(Kit.Button(_tr("Unlink"), slot.LinkedYamlSetting, () => slot.LinkYaml(null), enabled: !slot.EngineBooting));
                if (slot.SeedVersionWanted)
                    row.AddChild(Kit.Button(_tr("Use the seed's version…"), _tr("Find the apworld version this seed was made with and use it for this slot only (your install isn't changed)"),
                        () => slot.FixApworldVersion(interactive: true), enabled: !slot.EngineBooting));
                row.AddChild(Kit.Button(_tr("Restart logic"), _tr("Start this slot's logic engine again"), slot.RetryLogicEngine, enabled: !slot.EngineBooting));
                var buttons = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                buttons.AddThemeConstantOverride("margin_left", 32);
                buttons.AddChild(row);
                entry.AddChild(buttons);
                _slotsBox.AddChild(entry);
            }
        }

        private (string, Color, string) SlotState(SlotTrackerControl slot)
        {
            if (slot.EngineBooting) return ("…", Muted, _tr("Starting the logic engine…"));
            if (slot.EngineProblem != null)
                return ("✖", Bad, (slot.EngineProblem.Reason is not { } why ? slot.EngineProblem.Message
                    : slot.EngineProblem.Code == "world_missing" ? _tr("{0} isn't loaded: {1}").Replace("{0}", slot.Game).Replace("{1}", why)
                    : _tr("World not rebuilt: {0}").Replace("{0}", why)) + (slot.SeedVersionNote is { } note
                    ? " " + _tr("This seed was made with another version of {0}.").Replace("{0}", slot.Game) + " " + note : ""));
            if (!slot.EngineRunning) return ("○", Muted, _tr("Logic isn't running"));
            var info = slot.EngineYamlInfo;
            string? source = info?["source"]?.ToString();
            string? file = info?["file"]?.ToString();
            string how = source switch
            {
                "not_needed" => _tr("Rebuilt from the server's data (this game needs no YAML)"),
                "slot_data" => _tr("Rebuilt from the options in the server's slot data ({0} of {1} options)").Replace("{0}", info?["options_from_slot_data"]?.ToString()).Replace("{1}", info?["options_total"]?.ToString()),
                "linked" => _tr("Rebuilt from your linked YAML {0}").Replace("{0}", file),
                "players" => _tr("Rebuilt from {0} in the Players folder").Replace("{0}", file),
                _ => _tr("Running")
            };
            var match = info?["match"];
            if (slot.LogicAccuracy == AP_Atlas.Core.Games.LogicAccuracy.SeedVersion)
                return ("▲", Warn, how + _tr(", but this seed was made with another version of {0} than the installed one{1}, and logic doesn't match it.")
                    .Replace("{0}", slot.Game).Replace("{1}", slot.InstalledWorldVersion != null ? _tr(" (version {0})").Replace("{0}", slot.InstalledWorldVersion) : "")
                    + " " + slot.SeedVersionNote);
            if (match?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && !(bool)match)
                return ("▲", Warn, how + _tr(", but it differs from the server: {0} locations missing, {1} extra.")
                    .Replace("{0}", info?["missing"]?.ToString()).Replace("{1}", info?["extra"]?.ToString())
                    + (slot.Model.Logic.YamlCouldHelpRebuild ? " " + _tr("Link the YAML used for the seed.") : ""));
            if (match?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                return ("✔", Good, how + _tr("; all {0} locations match the server").Replace("{0}", info?["expected"]?.ToString())
                    + (slot.ApworldMatchesSeed == true ? _tr(", and the apworld's data matches the seed's.")
                        : slot.ApworldMatchesSeed == false ? _tr(", so logic runs on the installed version though the seed was made with another.") : "."));
            return ("✔", Good, how);
        }

        /// <summary>A slot's line as the page shows it: its glyph and text (for tests).</summary>
        internal (string Glyph, string Text) SlotStateForTests(SlotTrackerControl slot)
        {
            var (glyph, _, text) = SlotState(slot);
            return (glyph, text);
        }

        // ---- Actions ----

        /// <summary>Runs one engine operation as the page's run (its progress and outcome on top, its lines in the log).</summary>
        private void Run(string name, Func<Action<string>, Action<float>, CancellationToken, Task<bool>> operation, string? notDone = null, Func<string?>? problem = null)
        {
            if (_run.Running) return;
            if (AtlasEngine.SetupRunning)
            {
                SetStatus(_tr("Another engine change is still running; wait for it to finish."), Warn);
                return;
            }
            _runName = name;
            Log(name + "…");
            _run.Run(name, operation, _tr("{0}: done.").Replace("{0}", name), notDone, problem);
        }

        private void RunCheck()
        {
            var install = AtlasEngine.Current;
            Run(_tr("Health check"), async (log, _, ct) => (await AtlasEngine.RunCheckAsync(install, log, ct)).Passed,
                _tr("The health check found a problem: see Parts under Advanced, and the log."));
        }

        /// <summary>
        /// Downloads ask the engine permission first (once; "Always allow" is kept under Settings → Privacy &amp; permissions),
        /// the way every other online action does. An install of the user's own downloads nothing here.
        /// </summary>
        private void WithDownloadPermission(Action run)
        {
            if (AtlasEngine.Current.Mode != EngineMode.Portable || !AtlasEngine.SetupDownloads(AtlasEngine.Current)) { run(); return; }
            PermissionDialog.Ask(this, _settings, Permissions.EngineSetup, null, null, allowed => { if (allowed) run(); });
        }

        private void RunStep(EngineStepId id)
        {
            var install = AtlasEngine.Current;
            string name = id switch
            {
                EngineStepId.Runtime => _tr("Python runtime"),
                EngineStepId.Archipelago => _tr("Archipelago"),
                EngineStepId.Packages => _tr("Python packages"),
                EngineStepId.Tracker => _tr("Universal Tracker"),
                EngineStepId.Bridge => _tr("Atlas bridge"),
                _ => _tr("Health check")
            };
            // Every step runs through the engine's safe path: exclusive, verified, rolled back if the check fails.
            void RunIt()
            {
                bool downloads = id is EngineStepId.Runtime or EngineStepId.Archipelago or EngineStepId.Packages or EngineStepId.Tracker;
                if (downloads) WithDownloadPermission(() => Run(name, (log, p, ct) => AtlasEngine.RunStepAsync(install, id, log, p, ct)));
                else Run(name, (log, p, ct) => AtlasEngine.RunStepAsync(install, id, log, p, ct));
            }
            if (id == EngineStepId.Archipelago && install.Mode == EngineMode.Existing) { PickInstallFolder(); return; }
            var others = id == EngineStepId.Tracker ? install.FindAllTrackers() : new List<string>();
            if (install.Mode == EngineMode.Existing && others.Count > 0)
                Dialogs.Confirm(this, _tr("Atlas Engine"), _tr("Atlas will move {0} out of your Archipelago install (into Atlas's engine backups folder) and install Universal Tracker {1}, the version Atlas is tested with. Your own Universal Tracker client will use that version too.")
                    .Replace("{0}", string.Join(", ", others.Select(Path.GetFileName))).Replace("{1}", AtlasEngine.TrackerVersion), _tr("Install"), RunIt);
            else RunIt();
        }

        /// <summary>Switches to the user's install, after they've agreed to what Atlas adds there (asked once, or each time).</summary>
        private void SwitchToExisting(string path)
        {
            var target = EngineInstall.Existing(path);
            if (!target.CanLaunch)
            {
                Log(_tr("{0} doesn't contain ArchipelagoLauncher.exe; choose the folder Archipelago is installed in.").Replace("{0}", path));
                Render();
                return;
            }
            string worlds = target.WorldsDir ?? Path.Combine(path, "custom_worlds");
            PermissionDialog.Ask(this, _settings, Permissions.WriteArchipelago, path,
                $"Install: {path}\nAtlas adds: {Path.Combine(worlds, "UltimateBridge.apworld")}", allowed =>
                {
                    if (!IsInstanceValid(this)) return;
                    if (!allowed)
                    {
                        Log(_tr("Kept the current engine: Atlas doesn't add files to that install without your OK."));
                        Render();
                        return;
                    }
                    Log(_tr("Using the Archipelago install at {0}").Replace("{0}", path));
                    AtlasEngine.UseExistingInstall(path);
                });
        }

        /// <summary>Looks for Archipelago in the usual places, after the user agrees (it searches this PC).</summary>
        private void FindInstall()
        {
            PermissionDialog.Ask(this, _settings, Permissions.FindArchipelago, null, null, allowed =>
            {
                if (!allowed || !IsInstanceValid(this)) return;
                var found = AtlasEngine.FindArchipelagoInstalls();
                if (found.Count == 0)
                {
                    Log(_tr("No Archipelago install found. Use Choose folder…, or the portable engine."));
                    return;
                }
                Log(_tr("Found Archipelago at: {0}").Replace("{0}", string.Join(", ", found)));
                SwitchToExisting(found[0]);
            });
        }

        private void RemoveAtlasFiles(EngineInstall install)
        {
            var changes = AtlasEngine.ChangesIn(install.Root);
            var files = changes.Where(c => c.Kind == "added").Select(c => c.Path).Concat(AtlasEngine.AtlasFilesIn(install)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var moved = changes.Where(c => c.Kind == "moved").ToList();
            string text = _tr("Undo everything Atlas changed in {0}?").Replace("{0}", install.Root) + "\n\n" +
                          (files.Count > 0 ? _tr("Removes:") + "\n" + string.Join("\n", files.Select(f => "  •  " + f)) + "\n" : "") +
                          (moved.Count > 0 ? _tr("Puts back:") + "\n" + string.Join("\n", moved.Select(m => "  •  " + m.Path)) + "\n" : "") +
                          "\n" + _tr("Logic can't run on this install until you allow Atlas again (or use the portable engine).");
            Dialogs.Confirm(this, _tr("Atlas Engine"), text, _tr("Remove"), () =>
            {
                string root = install.Root;
                Run(_tr("Removing Atlas's files"), async (log, _, ct) =>
                {
                    await AtlasEngine.RemoveAtlasFilesAsync(install, log, ct);
                    Ui.Defer(null, () => Permissions.SetAlways(_settings, Permissions.WriteArchipelago, root, false), "taking back the permission to change your Archipelago install");
                    return true;
                });
            });
        }

        private void PickSeed()
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.zip, *.archipelago ; " + _tr("Generated Archipelago seed") },
                UseNativeDialog = true,
                Title = _tr("Choose a generated seed (Archipelago's output folder)")
            };
            string? folder = SeedVerifier.DefaultSeedFolder();
            if (folder != null) dialog.CurrentDir = folder;
            dialog.FileSelected += path =>
            {
                dialog.QueueFree();
                var install = AtlasEngine.Current;
                Run(_tr("Seed test"), async (log, _, ct) => (await SeedVerifier.VerifyAsync(install, path, log, ct)).Error == null, _tr("The seed test couldn't run; the log says why."));
            };
            dialog.Canceled += () => dialog.QueueFree();
            GetTree().Root.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        private void PickInstallFolder()
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenDir,
                Access = FileDialog.AccessEnum.Filesystem,
                UseNativeDialog = true,
                Title = _tr("Choose your Archipelago folder (the one with ArchipelagoLauncher.exe)")
            };
            if (Directory.Exists(_settings.ArchipelagoInstallationPath)) dialog.CurrentDir = _settings.ArchipelagoInstallationPath;
            dialog.DirSelected += dir =>
            {
                dialog.QueueFree();
                if (!File.Exists(Path.Combine(dir, "ArchipelagoLauncher.exe")))
                {
                    Log(_tr("{0} doesn't contain ArchipelagoLauncher.exe; choose the folder Archipelago is installed in.").Replace("{0}", dir));
                    Render();
                    return;
                }
                SwitchToExisting(dir);
            };
            dialog.Canceled += () => { dialog.QueueFree(); Render(); };
            GetTree().Root.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        // ---- Helpers ----

        private void Log(string line) => _log.AddText($"[{DateTime.Now:HH:mm:ss}] {line}\n"); // wall clock: a log line's time

        private void SetStatus(string text, Color color)
        {
            _status.Text = text;
            _status.AddThemeColorOverride("font_color", color);
        }

        private static Label Wrapped(Label label)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            return label;
        }

        private static Control Note(string text, int indent = 0)
        {
            var label = Wrapped(Kit.Muted(text));
            if (indent <= 0) return label;
            var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            margin.AddThemeConstantOverride("margin_left", indent);
            margin.AddChild(label);
            return margin;
        }

        /// <summary>The app theme draws pressed buttons' text dark (for its accent fill); radio rows have no fill.</summary>
        private static void Readable(BaseButton button)
        {
            foreach (var key in new[] { "font_pressed_color", "font_hover_pressed_color", "font_color", "font_focus_color", "font_hover_color" })
                button.AddThemeColorOverride(key, ThemeColors.Text);
            button.AddThemeColorOverride("font_disabled_color", Muted);
        }

        private static void Clear(Node box)
        {
            foreach (Node child in box.GetChildren())
            {
                box.RemoveChild(child);
                child.QueueFree();
            }
        }
    }
}
