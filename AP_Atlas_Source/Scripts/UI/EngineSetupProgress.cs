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
    /// One run of the Atlas Engine's setup as the user sees it: the step it's on in plain words, one bar for the whole
    /// run that never goes down, and the outcome (Ready, or why it stopped). The short setup panel and the setup window's
    /// rail both show one of these. pip's and Python's own lines never show here; they go to <see cref="Log"/>, for the
    /// full engine window's log.
    /// </summary>
    public sealed partial class EngineSetupProgress : VBoxContainer
    {
        private readonly Func<string, string> _tr;
        private readonly Label _step = new();
        private readonly ProgressBar _progress = new() { MaxValue = 1, Step = 0.001, ShowPercentage = true, CustomMinimumSize = new Vector2(0, 18) };
        private readonly Label _outcome = new();
        private readonly List<string> _log = new();
        private CancellationTokenSource? _cts;

        /// <summary>What the run says it's doing (for tests).</summary>
        public string StepText => _step.Text;
        /// <summary>How far the bar is (0 to 1; for tests).</summary>
        public double ProgressValue => _progress.Value;
        /// <summary>The outcome line (Ready, or why it stopped), empty while running (for tests).</summary>
        public string OutcomeText => _outcome.Text;
        public bool Running { get; private set; }
        /// <summary>Whether the last run finished (false before any run, while one runs, or after a failed one).</summary>
        public bool Succeeded { get; private set; }
        /// <summary>The lines the run logged (pip's included), for the full engine window.</summary>
        public IReadOnlyList<string> Log { get { lock (_log) return _log.ToArray(); } }
        /// <summary>Whether leaving the tree cancels a run (the short panel's Close; the setup window lets the run go on).</summary>
        public bool CancelOnExit { get; set; }

        /// <summary>A run started (true) or ended (false, with whether it succeeded in <see cref="Succeeded"/>). Main thread.</summary>
        public event Action<bool>? RunChanged;

        /// <param name="wrapWidth">The labels' width (an autowrapped label measured narrow reports a tall minimum).</param>
        public EngineSetupProgress(Func<string, string> tr, int wrapWidth = 520)
        {
            _tr = tr;
            AddThemeConstantOverride("separation", 8);
            _step.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _step.CustomMinimumSize = new Vector2(wrapWidth, 0);
            AddChild(_step);
            _progress.Visible = false;
            AddChild(_progress);
            _outcome.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _outcome.CustomMinimumSize = new Vector2(wrapWidth, 0);
            _outcome.Visible = false;
            AddChild(_outcome);
        }

        public override void _ExitTree()
        {
            if (CancelOnExit) _cts?.Cancel();
        }

        /// <summary>Cancels the run (the setup stops at the step it's on; the engine is left as it was).</summary>
        public void Cancel() => _cts?.Cancel();

        /// <summary>Runs the setup (nothing when one already runs); the outcome says why a run didn't start or stopped.</summary>
        public void Start()
        {
            if (Running) return;
            if (AtlasEngine.SetupRunning)
            {
                ShowOutcome(_tr("Another engine change is still running; wait for it to finish."), ThemeColors.Warning);
                return;
            }
            Running = true;
            Succeeded = false;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _outcome.Visible = false;
            _progress.Value = 0;
            _progress.Visible = true;
            _step.Text = _tr("Starting…");
            _step.AddThemeColorOverride("font_color", ThemeColors.Text);
            RunChanged?.Invoke(true);
            // One number for the whole setup: the bar never goes down, and the step says where it is ("Step 3 of 9: …").
            void OnProgress(AtlasEngine.SetupProgress p) => Ui.Defer(this, () =>
            {
                if (p.Text != null) _step.Text = p.Steps > 0 ? _tr("Step {0} of {1}: {2}").Replace("{0}", p.Step.ToString()).Replace("{1}", p.Steps.ToString()).Replace("{2}", p.Text) : p.Text;
                if (p.Fraction >= 0) _progress.Value = Math.Max(_progress.Value, p.Fraction);
            });
            AtlasEngine.SetupProgressed += OnProgress;
            void log(string line) { lock (_log) _log.Add(line); }
            void progress(float f) => Ui.Defer(this, () => { if (f >= 0) _progress.Value = Math.Max(_progress.Value, f); });
            Async.Fire(Task.Run(async () =>
            {
                bool ok;
                try { ok = await AtlasEngine.SetUpAsync(AtlasEngine.Current, log, progress, ct); }
                catch (OperationCanceledException) { ok = false; }
                catch (Exception ex) { ok = false; log(ex.Message); AP_Atlas.Core.Logger.LogWarning("[Atlas Engine] " + ex); }
                AtlasEngine.SetupProgressed -= OnProgress;
                Ui.Defer(this, () =>
                {
                    Running = false;
                    Succeeded = ok;
                    _progress.Visible = false;
                    if (ok)
                    {
                        _step.Text = _tr("Ready.");
                        ShowOutcome(_tr("The Atlas Engine is set up. Connect a slot and its logic runs on it."), ThemeColors.Success);
                    }
                    else
                    {
                        _step.Text = _tr("Stopped.");
                        ShowOutcome(AtlasEngine.LastSetupProblem ?? _tr("Setup didn't finish."), ThemeColors.Warning);
                    }
                    RunChanged?.Invoke(false);
                });
            }), "setting up the Atlas Engine", tellUser: false);
        }

        private void ShowOutcome(string text, Color color)
        {
            _outcome.Text = text;
            _outcome.AddThemeColorOverride("font_color", color);
            _outcome.Visible = true;
        }
    }
}
