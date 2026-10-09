#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AP_Atlas.Core.EngineSetup;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// "Test every ready game…": the solo test over every game the engine has a map pack for (or every game it has),
    /// without a server or a slot, one report each and a summary. Atlas stays usable meanwhile; Cancel ends after the
    /// game that's running.
    /// </summary>
    public partial class SoloBatchDialog : AcceptDialog
    {
        private readonly Func<string, string> _tr;
        private readonly SoloTestHooks _hooks;
        private readonly CheckBox _includeAll;
        private readonly Label _count, _current, _outcome;
        private readonly Button _start, _cancel, _openFolder;
        private readonly ProgressBar _bar;
        private readonly VBoxContainer _rows;
        private readonly Dictionary<string, Label> _rowLabels = new();
        private CancellationTokenSource? _cts;
        private string? _summaryPath;

        /// <summary>The summary file's path once the batch ended, or null (for tests).</summary>
        public string? SummaryPath => _summaryPath;

        /// <summary>Whether the batch ended (for tests).</summary>
        public bool Done { get; private set; }

        /// <summary>The rows as shown: game and its line (for tests).</summary>
        public IReadOnlyList<(string Game, string Text)> Rows() => _rowLabels.Select(kv => (kv.Key, kv.Value.Text)).ToList();

        private SoloBatchDialog(Func<string, string> tr, SoloTestHooks hooks)
        {
            _tr = tr;
            _hooks = hooks;
            Title = tr("Test every ready game");
            OkButtonText = tr("Close");
            Exclusive = false;
            SetMeta("solo_batch", true);
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(760, 0) };
            box.AddThemeConstantOverride("separation", 8);
            AddChild(box);
            var intro = Kit.Muted(tr("For each game in turn: a YAML with its default options, a one-player seed generated in the engine, Atlas's logic scored against the seed's spheres and the installed map pack scored by the Pack Doctor. No server, no slot, nothing downloaded or asked: a game whose apworld the engine lacks fails its first step. One report per game under Atlas's reports folder, and a summary table at the end. Minutes per big game; Atlas stays usable meanwhile."));
            intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            intro.CustomMinimumSize = new Vector2(760, 0);
            box.AddChild(intro);
            _includeAll = new CheckBox { Text = tr("Include games without a map pack (logic only)") };
            _includeAll.Toggled += _ => RefreshCount();
            box.AddChild(_includeAll);
            _count = Kit.Text("");
            box.AddChild(_count);
            var buttons = new HBoxContainer();
            buttons.AddThemeConstantOverride("separation", 8);
            _start = Kit.Button(tr("Start"), tr("Runs the games in turn."), Start);
            buttons.AddChild(_start);
            _cancel = Kit.Button(tr("Cancel the batch"), tr("Ends after the game that's running; its report and the summary are still written."), () => _cts?.Cancel(), enabled: false);
            buttons.AddChild(_cancel);
            _openFolder = Kit.Button(tr("Open report folder"), tr("The folder in Atlas's data folder that holds every solo test's report and the batch summaries."), () => AP_Atlas.Core.ExternalLinks.OpenFolder(SoloTestRunner.ReportsFolder));
            buttons.AddChild(_openFolder);
            box.AddChild(buttons);
            _bar = new ProgressBar { MinValue = 0, MaxValue = 1, Value = 0, ShowPercentage = false, Visible = false, CustomMinimumSize = new Vector2(0, 6) };
            box.AddChild(_bar);
            _current = Kit.Subtle("");
            _current.ClipText = true;
            _current.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            box.AddChild(_current);
            var scroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 220), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            _rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _rows.AddThemeConstantOverride("separation", 2);
            scroll.AddChild(_rows);
            box.AddChild(scroll);
            _outcome = Kit.Text("");
            _outcome.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _outcome.CustomMinimumSize = new Vector2(760, 0);
            _outcome.Visible = false;
            box.AddChild(_outcome);
            Confirmed += OnClosed;
            Canceled += OnClosed;
            RefreshCount();
        }

        /// <summary>Opens the dialog (one batch at a time: a running one's window comes back).</summary>
        public static SoloBatchDialog Open(Node parent, Func<string, string> tr, SoloTestHooks hooks)
        {
            var hidden = parent.GetChildren().OfType<SoloBatchDialog>().FirstOrDefault(d => !d.IsQueuedForDeletion());
            if (hidden != null)
            {
                hidden.PopupCentered(hidden.Size);
                return hidden;
            }
            var dialog = new SoloBatchDialog(tr, hooks);
            parent.AddChild(dialog);
            WindowFit.Pop(dialog, 820);
            if (SoloTestRunner.Running is { } other)
            {
                dialog._start.Disabled = true;
                dialog._outcome.Text = tr("A solo test ({0}) is running or still serving; stop it first.").Replace("{0}", other.Game);
                dialog._outcome.Visible = true;
            }
            return dialog;
        }

        private List<string> Games() => SoloBatch.ReadyGames(AtlasEngine.Current, _includeAll.ButtonPressed);

        private void RefreshCount()
        {
            int n = Games().Count;
            _count.Text = n == 1 ? _tr("1 game to test.") : _tr("{0} games to test.").Replace("{0}", n.ToString());
            _start.Disabled = n == 0 || _cts != null;
        }

        private void Start()
        {
            if (_cts != null) return;
            var games = Games();
            if (games.Count == 0) return;
            _start.Disabled = true;
            _includeAll.Disabled = true;
            _cancel.Disabled = false;
            _bar.Visible = true;
            _bar.Value = 0;
            _cts = new CancellationTokenSource();
            foreach (Node child in _rows.GetChildren()) child.QueueFree();
            _rowLabels.Clear();
            foreach (string game in games)
            {
                var label = Kit.Text(game + ": " + _tr("waiting"));
                label.ClipText = true;
                label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                _rowLabels[game] = label;
                _rows.AddChild(label);
            }
            var progress = new Progress<SoloBatchProgress>(OnProgress);
            var ct = _cts.Token;
            AP_Atlas.Core.Async.Fire(RunAsync(games, progress, ct), "running the solo test batch", tellUser: false);
        }

        private async System.Threading.Tasks.Task RunAsync(List<string> games, IProgress<SoloBatchProgress> progress, CancellationToken ct)
        {
            string? summary = null;
            try
            {
                summary = await SoloBatch.RunAsync(games, _hooks, progress, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException)
            {
                _outcome.Text = ex.Message;
                _outcome.Visible = true;
            }
            if (!IsInstanceValid(this)) return;
            _summaryPath = summary;
            Done = true;
            _cts = null;
            _cancel.Disabled = true;
            _bar.Visible = false;
            _includeAll.Disabled = false;
            _current.Text = string.Empty;
            if (summary != null)
            {
                _outcome.Text = _tr("Done. The summary is {0}, beside each game's report.").Replace("{0}", System.IO.Path.GetFileName(summary));
                _outcome.Visible = true;
            }
            RefreshCount();
        }

        private void OnProgress(SoloBatchProgress p)
        {
            if (!IsInstanceValid(this)) return;
            _bar.Value = p.Count == 0 ? 0 : (p.Index + (p.Finished != null ? 1 : 0)) / (double)p.Count;
            if (p.Finished is { } row)
            {
                if (_rowLabels.TryGetValue(row.Game, out var done)) done.Text = row.Game + ": " + row.Outcome + "; " + _tr("logic") + " " + row.Logic + "; " + _tr("pins") + " " + row.Pins + "; " + _tr("tiles") + " " + row.Tiles + "; " + row.ToCheck + " " + _tr("to check");
                return;
            }
            if (_rowLabels.TryGetValue(p.Game, out var label) && p.Step == null) label.Text = p.Game + ": " + _tr("running");
            _current.Text = p.Step == null ? p.Game + "…" : p.Game + ": " + p.Step.Step + (p.Step.Detail.Length > 0 ? " (" + p.Step.Detail + ")" : "") + (p.Step.LogLine != null ? " " + p.Step.LogLine : "");
        }

        private void OnClosed()
        {
            // A running batch goes on without the window (Test every ready game… brings it back).
            if (_cts != null)
            {
                Hide();
                _hooks.Notice?.Invoke(_tr("The batch keeps going; Test every ready game… on the Games page brings its window back."));
                return;
            }
            QueueFree();
        }

        // ---- For tests ----

        internal void StartForTests() => Start();
        internal void IncludeAllForTests(bool on) => _includeAll.ButtonPressed = on;
    }
}
