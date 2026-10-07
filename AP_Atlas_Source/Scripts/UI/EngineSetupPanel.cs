#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using AP_Atlas.Core;
using AP_Atlas.Core.EngineSetup;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The short way to set up the Atlas Engine: one sentence, the step it's on in plain words, a progress bar and Cancel;
    /// then Ready, or what went wrong with Try again and Show details (the full engine window with its log). pip's and
    /// Python's own lines never show here; they go to the log the details window keeps. One panel at a time.
    /// </summary>
    public sealed partial class EngineSetupPanel : AcceptDialog
    {
        private static EngineSetupPanel? _open;

        private readonly Func<string, string> _tr;
        private readonly Action<IReadOnlyList<string>> _showDetails;
        private readonly Label _step = new();
        private readonly ProgressBar _progress = new() { MaxValue = 1, Step = 0.001, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 14) };
        private readonly Label _outcome = new();
        private readonly Button _tryAgain;
        private readonly Button _details;
        private readonly List<string> _log = new();
        private CancellationTokenSource? _cts;
        private bool _running;

        /// <summary>What the panel says the setup is doing (for tests).</summary>
        public string StepText => _step.Text;
        /// <summary>The outcome line (Ready, or why it stopped), empty while running (for tests).</summary>
        public string OutcomeText => _outcome.Text;
        public bool TryAgainShown => _tryAgain.Visible;
        public bool DetailsShown => _details.Visible;

        /// <param name="showDetails">Opens the full engine window with the lines the panel's run logged.</param>
        public static EngineSetupPanel Open(Node parent, Func<string, string> tr, Action<IReadOnlyList<string>> showDetails)
        {
            if (_open != null && IsInstanceValid(_open))
            {
                _open.GrabFocus();
                return _open;
            }
            var panel = new EngineSetupPanel(tr, showDetails);
            _open = panel;
            parent.AddChild(panel);
            panel.PopupCentered(new Vector2I(560, 0));
            panel.Start();
            return panel;
        }

        private EngineSetupPanel(Func<string, string> tr, Action<IReadOnlyList<string>> showDetails)
        {
            _tr = tr;
            _showDetails = showDetails;
            Title = tr("Setting up the Atlas Engine");
            OkButtonText = tr("Cancel");
            DialogHideOnOk = false;
            Unresizable = false;
            Exclusive = false; // the window stays usable while the setup runs, and the permission dialog may still be closing
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 10);
            var intro = new Label
            {
                Text = tr("Atlas is setting up its logic engine in its own folder: Archipelago, Python and the Universal Tracker, about 55 MB. This takes a few minutes the first time, and nothing else on your PC changes."),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(520, 0)
            };
            intro.AddThemeColorOverride("font_color", ThemeColors.TextMuted);
            box.AddChild(intro);
            _step.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _step.CustomMinimumSize = new Vector2(520, 0);
            box.AddChild(_step);
            box.AddChild(_progress);
            _outcome.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _outcome.CustomMinimumSize = new Vector2(520, 0);
            _outcome.Visible = false;
            box.AddChild(_outcome);
            AddChild(box);
            _tryAgain = AddButton(tr("Try again"), true, "try-again");
            _details = AddButton(tr("Show details"), false, "details");
            _tryAgain.Visible = false;
            _details.Visible = false;
            CustomAction += action =>
            {
                if (action == "try-again") Start();
                else if (action == "details") _showDetails(_log.ToArray());
            };
            Confirmed += () =>
            {
                if (_running) _cts?.Cancel();
                else QueueFree();
            };
            Canceled += () => { if (!_running) QueueFree(); };
            CloseRequested += () => { if (_running) _cts?.Cancel(); };
        }

        public override void _ExitTree()
        {
            _cts?.Cancel();
            if (_open == this) _open = null;
        }

        private void Start()
        {
            if (_running) return;
            if (AtlasEngine.SetupRunning)
            {
                ShowOutcome(_tr("Another engine change is still running; wait for it to finish."), ThemeColors.Warning, retry: true);
                return;
            }
            _running = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _outcome.Visible = false;
            _tryAgain.Visible = false;
            _details.Visible = false;
            _progress.Value = 0;
            _progress.Visible = true;
            OkButtonText = _tr("Cancel");
            _step.Text = _tr("Starting…");
            _step.AddThemeColorOverride("font_color", ThemeColors.Text);
            void OnStep(string text) => Ui.Defer(this, () => { _step.Text = text; _progress.Value = 0; });
            AtlasEngine.SetupStep += OnStep;
            void log(string line) { lock (_log) _log.Add(line); }
            void progress(float f) => Ui.Defer(this, () => _progress.Value = f < 0 ? 0 : f);
            Async.Fire(Task.Run(async () =>
            {
                bool ok;
                try { ok = await AtlasEngine.SetUpAsync(AtlasEngine.Current, log, progress, ct); }
                catch (OperationCanceledException) { ok = false; }
                catch (Exception ex) { ok = false; log(ex.Message); AP_Atlas.Core.Logger.LogWarning("[Atlas Engine] " + ex); }
                AtlasEngine.SetupStep -= OnStep;
                Ui.Defer(this, () =>
                {
                    _running = false;
                    _progress.Visible = false;
                    if (ok)
                    {
                        _step.Text = _tr("Ready.");
                        ShowOutcome(_tr("The Atlas Engine is set up. Connect a slot and its logic runs on it."), ThemeColors.Success, retry: false);
                    }
                    else
                    {
                        _step.Text = _tr("Stopped.");
                        ShowOutcome(AtlasEngine.LastSetupProblem ?? _tr("Setup didn't finish."), ThemeColors.Warning, retry: true);
                    }
                });
            }), "setting up the Atlas Engine", tellUser: false);
        }

        private void ShowOutcome(string text, Color color, bool retry)
        {
            _outcome.Text = text;
            _outcome.AddThemeColorOverride("font_color", color);
            _outcome.Visible = true;
            _tryAgain.Visible = retry;
            _details.Visible = true;
            OkButtonText = _tr("Close");
        }
    }
}
