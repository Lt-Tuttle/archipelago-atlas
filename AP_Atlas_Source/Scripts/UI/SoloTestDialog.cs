#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.EngineSetup;
using AP_Atlas.Core.Reports;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// "Test this game…": the chain's steps with ticks as they run, the live log line, the pack choice when a pack is
    /// needed, and at the end the four questions for the owner, the report folder and Stop the test server. Frees
    /// itself when closed (and ends the server then).
    /// </summary>
    public sealed partial class SoloTestDialog : AcceptDialog
    {
        private readonly string _game;
        private readonly Func<string, string> _tr;
        private readonly SoloTestHooks _hooks;
        private readonly SoloTestRunner _runner;
        private readonly Dictionary<SoloTestStep, (TextureRect Mark, Label Detail)> _rows = new();
        private readonly Dictionary<SoloTestStep, SoloStepOutcome> _states = new();
        private readonly Label _logLine;
        private readonly ProgressBar _busy;
        private readonly Button _start, _cancelRun, _saveNotes, _openFolder, _stopServer;
        private readonly VBoxContainer _packBox, _notesBox;
        private readonly Label _packStatus, _outcome;
        private readonly List<TextEdit> _notes = new();
        private CancellationTokenSource? _cts;
        private TaskCompletionSource<PackChoice?>? _packChoice;

        private SoloTestDialog(string game, Func<string, string> tr, SoloTestHooks hooks)
        {
            _game = game;
            _tr = tr;
            _hooks = hooks;
            _runner = new SoloTestRunner(game);
            Title = tr("Test {0}").Replace("{0}", game);
            OkButtonText = tr("Close");
            // Not exclusive: the owner looks at the Map Tracker, Key Items and the Logic Tracker while this window stays open.
            Exclusive = false;
            SetMeta("solo_test", game);
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(600, 0) };
            box.AddThemeConstantOverride("separation", 8);
            AddChild(box);
            var intro = Kit.Muted(tr("Atlas installs what's missing (asking as always before anything is downloaded), makes a YAML with the game's default options, generates a one-player seed in the engine (minutes for a big game), hosts it on this PC only (127.0.0.1, no password), connects a slot called AtlasTest with that YAML, scores its logic and the pack, and writes a report in Atlas's folder with a few questions for you at the end. Atlas stays usable meanwhile; Close keeps the test and its server going, and Test this game… on the Games page brings this window back."));
            intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            intro.CustomMinimumSize = new Vector2(600, 0);
            box.AddChild(intro);
            var buttons = new HBoxContainer();
            buttons.AddThemeConstantOverride("separation", 8);
            _start = Kit.Button(tr("Start"), tr("Runs the whole chain; you can watch the map and Key Items while it goes."), Start);
            buttons.AddChild(_start);
            _cancelRun = Kit.Button(tr("Cancel the test"), tr("Stops at the step it's on; the report says what happened."), () => _cts?.Cancel(), enabled: false);
            buttons.AddChild(_cancelRun);
            box.AddChild(buttons);
            var steps = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            steps.AddThemeConstantOverride("h_separation", 10);
            steps.AddThemeConstantOverride("v_separation", 4);
            box.AddChild(steps);
            foreach (var (step, title) in new[]
            {
                (SoloTestStep.Apworld, "The apworld in the Atlas Engine"), (SoloTestStep.MapPack, "A map pack"), (SoloTestStep.Yaml, "A YAML with the default options"),
                (SoloTestStep.Generate, "A one-player seed"), (SoloTestStep.Host, "A server on this PC"), (SoloTestStep.Connect, "The AtlasTest slot connected"),
                (SoloTestStep.Score, "Logic and the pack scored"), (SoloTestStep.Report, "The report written")
            })
            {
                var mark = new TextureRect { Texture = LucideTextures.Get("circle", AP_Atlas.Core.ThemeColors.TextSubtle, 1.0f), CustomMinimumSize = new Vector2(20, 20), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
                steps.AddChild(mark);
                var name = Kit.Text(tr(title));
                name.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                name.CustomMinimumSize = new Vector2(220, 0);
                steps.AddChild(name);
                var detail = Kit.Muted("");
                detail.ClipText = true;
                detail.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                detail.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                steps.AddChild(detail);
                _rows[step] = (mark, detail);
                _states[step] = SoloStepOutcome.Pending;
            }
            _busy = new ProgressBar { Indeterminate = true, ShowPercentage = false, Visible = false, CustomMinimumSize = new Vector2(0, 6) };
            box.AddChild(_busy);
            _logLine = Kit.Subtle("");
            _logLine.ClipText = true;
            _logLine.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            box.AddChild(_logLine);
            // The pack choice, when a pack is needed: GitHub's results, Install per row, or on without a pack.
            _packBox = new VBoxContainer { Visible = false };
            _packBox.AddThemeConstantOverride("separation", 6);
            _packStatus = Kit.Muted("");
            _packStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _packStatus.CustomMinimumSize = new Vector2(600, 0);
            _packBox.AddChild(_packStatus);
            box.AddChild(_packBox);
            // The end: the outcome, the owner's four answers, the report folder, the server.
            _outcome = Kit.Text("");
            _outcome.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _outcome.CustomMinimumSize = new Vector2(600, 0);
            _outcome.Visible = false;
            box.AddChild(_outcome);
            _notesBox = new VBoxContainer { Visible = false };
            _notesBox.AddThemeConstantOverride("separation", 6);
            _notesBox.AddChild(Kit.Heading(tr("What did you see?"), 1.05f));
            foreach (string prompt in SoloTestReport.NotePrompts)
            {
                var edit = new TextEdit { PlaceholderText = tr(prompt), CustomMinimumSize = new Vector2(0, 54), WrapMode = TextEdit.LineWrappingMode.Boundary, AccessibilityName = tr(prompt) };
                _notes.Add(edit);
                _notesBox.AddChild(edit);
            }
            var endButtons = new HFlowContainer();
            endButtons.AddThemeConstantOverride("h_separation", 8);
            _saveNotes = Kit.Button(tr("Save notes"), tr("Writes your answers into the report (replacing earlier ones)."), SaveNotes);
            endButtons.AddChild(_saveNotes);
            _openFolder = Kit.Button(tr("Open report folder"), tr("The folder in Atlas's data folder that holds every solo test's report."), () => AP_Atlas.Core.ExternalLinks.OpenFolder(SoloTestRunner.ReportsFolder));
            endButtons.AddChild(_openFolder);
            _stopServer = Kit.Button(tr("Stop the test server"), tr("Ends the server on this PC, disconnects the AtlasTest slot and removes its multiworld (closing this window keeps them)."), StopServer);
            endButtons.AddChild(_stopServer);
            _notesBox.AddChild(endButtons);
            box.AddChild(_notesBox);
            Confirmed += OnClosed;
            Canceled += OnClosed;
        }

        /// <summary>Opens the dialog for a game (one test at a time: a second one says so and closes).</summary>
        public static SoloTestDialog Open(Node parent, string game, Func<string, string> tr, SoloTestHooks hooks)
        {
            // A closed window whose test or server still runs comes back as it was.
            var hidden = parent.GetChildren().OfType<SoloTestDialog>().FirstOrDefault(d => !d.IsQueuedForDeletion() && d._game == game);
            if (hidden != null)
            {
                hidden.PopupCentered(hidden.Size);
                return hidden;
            }
            var dialog = new SoloTestDialog(game, tr, hooks);
            parent.AddChild(dialog);
            WindowFit.Pop(dialog, 640);
            if (SoloTestRunner.Running is { } other && other != dialog._runner)
            {
                dialog._start.Disabled = true;
                dialog._outcome.Text = tr("Another test ({0}) is running or still serving; stop it first.").Replace("{0}", other.Game);
                dialog._outcome.Visible = true;
            }
            return dialog;
        }

        private void Start()
        {
            if (_cts != null) return;
            _start.Disabled = true;
            _cancelRun.Disabled = false;
            _busy.Visible = true;
            _cts = new CancellationTokenSource();
            var hooks = new SoloTestHooks
            {
                Settings = _hooks.Settings,
                InstallApworldAsync = _hooks.InstallApworldAsync,
                ChoosePackAsync = ChoosePackAsync,
                InstallPackAsync = _hooks.InstallPackAsync,
                AddProfile = _hooks.AddProfile,
                RemoveProfile = _hooks.RemoveProfile,
                ConnectSlotAsync = _hooks.ConnectSlotAsync,
                Scrubber = _hooks.Scrubber,
                AtlasVersionLine = _hooks.AtlasVersionLine,
            };
            var progress = new Progress<SoloTestProgress>(OnProgress);
            AP_Atlas.Core.Async.Fire(async () =>
            {
                SoloTestResult? result = null;
                try { result = await _runner.RunAsync(hooks, progress, _cts.Token); }
                catch (Exception ex) { AP_Atlas.Core.Logger.LogError("The solo test of " + _game + " failed: " + ex); }
                if (!GodotObject.IsInstanceValid(this)) return;
                Finish(result);
            }, "testing " + _game);
        }

        private void OnProgress(SoloTestProgress p)
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            if (p.LogLine != null) _logLine.Text = p.LogLine;
            if (p.Outcome == SoloStepOutcome.Running && p.Detail.Length == 0 && p.LogLine != null) return;
            _states[p.Step] = p.Outcome;
            var (mark, detail) = _rows[p.Step];
            if (p.Detail.Length > 0 || p.Outcome != SoloStepOutcome.Running) detail.Text = p.Detail;
            detail.TooltipText = p.Detail;
            mark.Texture = LucideTextures.Get(p.Outcome is SoloStepOutcome.Done or SoloStepOutcome.Skipped ? "circle-check" : "circle",
                p.Outcome switch
                {
                    SoloStepOutcome.Done => AP_Atlas.Core.ThemeColors.Success,
                    SoloStepOutcome.Skipped => AP_Atlas.Core.ThemeColors.TextMuted,
                    SoloStepOutcome.Failed or SoloStepOutcome.Cancelled => AP_Atlas.Core.ThemeColors.Error,
                    SoloStepOutcome.Running => AP_Atlas.Core.ThemeColors.Heading,
                    _ => AP_Atlas.Core.ThemeColors.TextSubtle
                }, 1.0f);
            mark.TooltipText = p.Outcome.ToString();
            WindowFit.RequestShrink(this);
        }

        private void Finish(SoloTestResult? result)
        {
            _busy.Visible = false;
            _cancelRun.Disabled = true;
            _logLine.Text = string.Empty;
            bool connected = result?.Steps.Any(s => s.Step == SoloTestStep.Connect && s.Outcome == SoloStepOutcome.Done) == true;
            _outcome.Text = result == null ? _tr("The test couldn't run: the log says why.")
                : connected ? _tr("The slot is connected to the test server. Look at the Map Tracker, Key Items and the Logic Tracker, then answer below and save; Stop the test server when you're done.")
                : _tr("The test stopped early; the report says where. The questions below still help.");
            _outcome.Visible = true;
            _notesBox.Visible = true;
            _stopServer.Disabled = _runner.ServerAddress == null;
            WindowFit.RequestShrink(this);
        }

        private Task<PackChoice?> ChoosePackAsync(string game)
        {
            _packChoice = new TaskCompletionSource<PackChoice?>();
            _packBox.Visible = true;
            _packStatus.Text = _tr("No pack for {0} is installed: looking on GitHub…").Replace("{0}", game);
            AP_Atlas.Core.Async.Fire(async () =>
            {
                var (candidates, problem) = await AP_Atlas.Core.MapPackManagerControl.SearchPacksAsync(game);
                if (!GodotObject.IsInstanceValid(this)) return;
                _packStatus.Text = problem != null ? _tr("GitHub couldn't be searched: {0}").Replace("{0}", problem)
                    : candidates.Count == 0 ? _tr("No map packs found on GitHub for {0}.").Replace("{0}", game)
                    : _tr("Packs on GitHub for {0}. Packs are made by the community: check who made it before installing.").Replace("{0}", game);
                foreach (var c in candidates)
                {
                    var row = new HBoxContainer();
                    row.AddThemeConstantOverride("separation", 8);
                    var info = Kit.Text(c.Repo + "  ★" + c.Stars + (string.IsNullOrWhiteSpace(c.Description) ? "" : " · " + c.Description));
                    info.ClipText = true;
                    info.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    info.TooltipText = c.Description ?? "";
                    row.AddChild(info);
                    string repo = c.Repo;
                    row.AddChild(Kit.Button(_tr("Install"), _tr("Downloads this pack's newest release and installs it."), () => PickPack(new PackChoice(repo)), small: true));
                    _packBox.AddChild(row);
                }
                _packBox.AddChild(Kit.Button(_tr("Continue without a pack"), _tr("Pins, Key Items and the pack's scripts aren't scored then."), () => PickPack(new PackChoice(null)), small: true));
                WindowFit.RequestShrink(this);
            }, "looking for packs for " + game);
            return _packChoice.Task;
        }

        private void PickPack(PackChoice choice)
        {
            _packChoice?.TrySetResult(choice);
            _packChoice = null;
            foreach (Node child in _packBox.GetChildren().Skip(1).ToList())
            {
                _packBox.RemoveChild(child);
                child.QueueFree();
            }
            _packStatus.Text = choice.Repo == null ? _tr("Going on without a pack.") : _tr("Installing the pack from github.com/{0}…").Replace("{0}", choice.Repo);
        }

        private SoloOwnerNotes Notes() => new(_notes[0].Text, _notes[1].Text, _notes[2].Text, _notes[3].Text);

        private void SaveNotes()
        {
            var written = _runner.SaveNotes(Notes());
            _outcome.Text = written == null ? _tr("There's no report yet.") : _tr("Notes saved to {0}.").Replace("{0}", System.IO.Path.GetFileName(written.Value.Md));
        }

        private void StopServer()
        {
            _runner.StopServer(_hooks);
            _stopServer.Disabled = true;
            if (_states.TryGetValue(SoloTestStep.Host, out var state) && state == SoloStepOutcome.Done) OnProgress(new SoloTestProgress(SoloTestStep.Host, SoloStepOutcome.Done, _tr("stopped"), null));
        }

        private void OnClosed()
        {
            // A running test or a served seed goes on without the window (the Games page brings it back or stops it).
            if (_runner.Busy || _runner.ServerAddress != null)
            {
                Hide();
                _hooks.Notice?.Invoke(_tr("The test keeps going; Test this game… on the Games page brings its window back."));
                return;
            }
            _packChoice?.TrySetResult(new PackChoice(null));
            QueueFree();
        }

        // ---- For tests ----

        internal IReadOnlyDictionary<SoloTestStep, SoloStepOutcome> StepStates() => _states;
        internal SoloTestRunner Runner => _runner;
        internal void StartForTests() => Start();
        internal void ChoosePackForTests(string? repo) => PickPack(new PackChoice(repo));
        internal void FillNotesForTests(SoloOwnerNotes notes)
        {
            _notes[0].Text = notes.WrongOnMap;
            _notes[1].Text = notes.WrongItems;
            _notes[2].Text = notes.RealGameNeeds;
            _notes[3].Text = notes.Other;
        }
        internal void SaveNotesForTests() => SaveNotes();
        internal void StopServerForTests() => StopServer();
        internal string OutcomeText => _outcome.Text;
    }
}
