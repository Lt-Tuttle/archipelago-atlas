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
    /// One run of an Atlas Engine operation as the user sees it (the setup, a repair, a health check): the step it's on in
    /// plain words, one bar for the whole run that never goes down with dots beside it that move while a step runs with no bar
    /// movement of its own (round 13: a long check never looks frozen), and the outcome (Ready, or why it stopped). The short
    /// setup panel, the setup window's rail and the Atlas Engine page each show one of these. pip's and Python's own lines
    /// never show here; they go to <see cref="Log"/> (and <see cref="LineLogged"/>), for the Atlas Engine page's log.
    /// </summary>
    public sealed partial class EngineSetupProgress : VBoxContainer
    {
        private readonly Func<string, string> _tr;
        private readonly Label _step = new();
        private readonly ProgressBar _progress = new() { MaxValue = 1, Step = 0.001, ShowPercentage = true, CustomMinimumSize = new Vector2(0, 18) };
        private readonly Label _outcome = new();
        private readonly HBoxContainer _progressRow = new();
        private readonly BusyDots _busy;
        private readonly ProgressDots _dots = new(); // the raw high-water fraction, not the bar's rounded value
        private readonly List<string> _log = new();
        private CancellationTokenSource? _cts;

        /// <summary>What the run says it's doing (for tests).</summary>
        public string StepText => _step.Text;
        /// <summary>How far the bar is (0 to 1; for tests).</summary>
        public double ProgressValue => _progress.Value;
        /// <summary>The outcome line (Ready, or why it stopped), empty while running (for tests).</summary>
        public string OutcomeText => _outcome.Text;
        /// <summary>Whether the busy dots run (for tests).</summary>
        public bool BusyRunning => _busy.Running;
        public bool Running { get; private set; }
        /// <summary>Whether the last run finished (false before any run, while one runs, or after a failed one).</summary>
        public bool Succeeded { get; private set; }
        /// <summary>The lines the runs logged (pip's included), for the Atlas Engine page's log.</summary>
        public IReadOnlyList<string> Log { get { lock (_log) return _log.ToArray(); } }
        /// <summary>Whether leaving the tree cancels a run (the short panel's Close; the setup window lets the run go on).</summary>
        public bool CancelOnExit { get; set; }

        /// <summary>A run started (true) or ended (false, with whether it succeeded in <see cref="Succeeded"/>). Main thread.</summary>
        public event Action<bool>? RunChanged;

        /// <summary>A line a run logged (main thread), as it comes.</summary>
        public event Action<string>? LineLogged;

        /// <param name="wrapWidth">The labels' width (an autowrapped label measured narrow reports a tall minimum).</param>
        public EngineSetupProgress(Func<string, string> tr, int wrapWidth = 520)
        {
            _tr = tr;
            AddThemeConstantOverride("separation", 8);
            _step.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _step.CustomMinimumSize = new Vector2(wrapWidth, 0);
            AddChild(_step);
            // The bar, and the dots beside it. The row hides as a whole, so an idle panel keeps no gap.
            _busy = new BusyDots(tr("Working"));
            _progressRow.AddThemeConstantOverride("separation", 8);
            _progress.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _progressRow.AddChild(_progress);
            _progressRow.AddChild(_busy);
            _progressRow.Visible = false;
            AddChild(_progressRow);
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
        public void Start() => Run(_tr("Setting up the Atlas Engine"), (log, progress, ct) => AtlasEngine.SetUpAsync(AtlasEngine.Current, log, progress, ct),
            _tr("The Atlas Engine is set up. Connect a slot and its logic runs on it."), _tr("Setup didn't finish."), () => AtlasEngine.LastSetupProblem);

        /// <summary>
        /// Runs one engine operation on a worker thread (nothing when one already runs): its steps and bar as the engine
        /// reports them, its lines to <see cref="Log"/>, and an outcome: <paramref name="done"/> when it returns true, else
        /// why it stopped: the run's own reason (<paramref name="problem"/>, read when it ends: only a run that sets it, as
        /// the setup sets <see cref="AtlasEngine.LastSetupProblem"/>, may name one), "Cancelled.", or <paramref name="notDone"/>.
        /// </summary>
        public void Run(string name, Func<Action<string>, Action<float>, CancellationToken, Task<bool>> operation, string? done = null, string? notDone = null, Func<string?>? problem = null)
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
            _dots.Reset();
            _progressRow.Visible = true;
            _busy.Running = true;
            _step.Text = _tr("Starting…");
            _step.AddThemeColorOverride("font_color", ThemeColors.Text);
            RunChanged?.Invoke(true);
            // One number for the whole run: the bar never goes down, and the step says where it is ("Step 3 of 9: …").
            void OnProgress(AtlasEngine.SetupProgress p) => Ui.Defer(this, () =>
            {
                if (p.Text != null) _step.Text = p.Steps > 0 ? _tr("Step {0} of {1}: {2}").Replace("{0}", p.Step.ToString()).Replace("{1}", p.Steps.ToString()).Replace("{2}", p.Text) : p.Text;
                // A step begins with the dots; the bar moving within it (a download) stops them.
                if (Running) _busy.Running = _dots.Step(p.Text, p.Fraction);
                if (p.Fraction >= 0) _progress.Value = Math.Max(_progress.Value, p.Fraction);
            });
            AtlasEngine.SetupProgressed += OnProgress;
            void log(string line)
            {
                lock (_log) _log.Add(line);
                Ui.Defer(this, () => LineLogged?.Invoke(line));
            }
            void progress(float f) => Ui.Defer(this, () =>
            {
                if (Running) _busy.Running = _dots.Bar(f);
                if (f >= 0) _progress.Value = Math.Max(_progress.Value, f);
            });
            Async.Fire(Task.Run(async () =>
            {
                bool ok;
                bool cancelled = false;
                string? failure = null;
                try { ok = await operation(log, progress, ct); }
                catch (OperationCanceledException) { ok = false; cancelled = true; log(name + ": " + _tr("Cancelled.")); }
                catch (Exception ex) { ok = false; failure = ex.Message; log(ex.Message); AP_Atlas.Core.Logger.LogWarning("[Atlas Engine] " + ex); }
                AtlasEngine.SetupProgressed -= OnProgress;
                Ui.Defer(this, () =>
                {
                    Running = false;
                    Succeeded = ok;
                    _progressRow.Visible = false;
                    _busy.Running = false;
                    if (ok)
                    {
                        _step.Text = _tr("Ready.");
                        if (done != null) ShowOutcome(done, ThemeColors.Success);
                    }
                    else
                    {
                        _step.Text = _tr("Stopped.");
                        ShowOutcome(failure != null ? _tr("{0} stopped: {1}").Replace("{0}", name).Replace("{1}", failure)
                            : problem?.Invoke() ?? (cancelled ? _tr("Cancelled.") : notDone) ?? _tr("{0} stopped; the log says why.").Replace("{0}", name), ThemeColors.Warning);
                    }
                    RunChanged?.Invoke(false);
                });
            }), $"running \"{name}\"", tellUser: false);
        }

        private void ShowOutcome(string text, Color color)
        {
            _outcome.Text = text;
            _outcome.AddThemeColorOverride("font_color", color);
            _outcome.Visible = true;
        }
    }
}
