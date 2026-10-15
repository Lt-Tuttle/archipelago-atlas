#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using AP_Atlas.Core;
using AP_Atlas.Core.EngineSetup;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The short way to set up the Atlas Engine: one sentence, the run itself (<see cref="EngineSetupProgress"/>: the
    /// step it's on in plain words, a progress bar) and Cancel; then Ready, or what went wrong with Try again and Show
    /// details (the Atlas Engine page with its log). One panel at a time.
    /// </summary>
    public sealed partial class EngineSetupPanel : AcceptDialog
    {
        private static EngineSetupPanel? _open;

        private readonly Func<string, string> _tr;
        private readonly Action<IReadOnlyList<string>> _showDetails;
        private readonly EngineSetupProgress _run;
        private readonly Button _tryAgain;
        private readonly Button _details;

        /// <summary>What the panel says the setup is doing (for tests).</summary>
        public string StepText => _run.StepText;
        /// <summary>How far the bar is (0 to 1; for tests).</summary>
        public double ProgressValue => _run.ProgressValue;
        /// <summary>The outcome line (Ready, or why it stopped), empty while running (for tests).</summary>
        public string OutcomeText => _run.OutcomeText;
        public bool TryAgainShown => _tryAgain.Visible;
        public bool DetailsShown => _details.Visible;

        /// <param name="showDetails">Shows the Atlas Engine page with the lines the panel's run logged.</param>
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
            WindowFit.Pop(panel, 560);
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
            _run = new EngineSetupProgress(tr) { CancelOnExit = true };
            box.AddChild(_run);
            AddChild(box);
            _tryAgain = AddButton(tr("Try again"), true, "try-again");
            _details = AddButton(tr("Show details"), false, "details");
            _tryAgain.Visible = false;
            _details.Visible = false;
            _run.RunChanged += running =>
            {
                if (running) return;
                _tryAgain.Visible = !_run.Succeeded;
                _details.Visible = true;
                OkButtonText = _tr("Close");
            };
            CustomAction += action =>
            {
                if (action == "try-again") Start();
                else if (action == "details")
                {
                    _showDetails(_run.Log); // the page has the run's log now; the panel would only cover it
                    QueueFree();
                }
            };
            Confirmed += () =>
            {
                if (_run.Running) _run.Cancel();
                else QueueFree();
            };
            Canceled += () => { if (!_run.Running) QueueFree(); };
            CloseRequested += () => { if (_run.Running) _run.Cancel(); };
        }

        public override void _ExitTree()
        {
            if (_open == this) _open = null;
        }

        private void Start()
        {
            if (_run.Running) return;
            _tryAgain.Visible = false;
            _details.Visible = false;
            OkButtonText = _tr("Cancel");
            _run.Start();
            if (!_run.Running)
            {
                // Didn't start (another engine change is running): the outcome says so, and Try again is the way on.
                _tryAgain.Visible = true;
                _details.Visible = true;
                OkButtonText = _tr("Close");
            }
        }
    }
}
