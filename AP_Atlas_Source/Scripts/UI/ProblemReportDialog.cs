#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core.Reports;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>What the user chose in Report a problem.</summary>
    public enum ProblemChoice
    {
        Send,
        Save,
        Cancel
    }

    /// <summary>
    /// Help → Report a problem: each part the report can hold with a tick (all on) and View, exactly what would be sent
    /// (the event, following the ticks and the note), a "What went wrong?" box, and Send, Just save it or Cancel. Send is
    /// the user's say each time (nothing is kept); it's greyed with the reason when this build can't send or the parts are too big.
    /// </summary>
    public sealed partial class ProblemReportDialog : AcceptDialog
    {
        private readonly IReadOnlyList<ProblemPart> _parts;
        private readonly bool[] _ticked;
        private readonly Func<IReadOnlyList<ProblemPart>, string?, string> _preview;
        private readonly Func<IReadOnlyList<ProblemPart>, string?> _cannotSend;
        private readonly Action<ProblemChoice, IReadOnlyList<ProblemPart>, string?> _decided;
        private readonly TextEdit _text;
        private readonly TextEdit _note;
        private readonly Label _why;
        private bool _answered;

        /// <summary>The dialog open now (for tests), or null.</summary>
        public static ProblemReportDialog? Showing { get; private set; }

        public ProblemReportDialog(IReadOnlyList<ProblemPart> parts, Func<IReadOnlyList<ProblemPart>, string?, string> preview, Func<IReadOnlyList<ProblemPart>, string?> cannotSend,
            Action<ProblemChoice, IReadOnlyList<ProblemPart>, string?> decided)
        {
            _parts = parts;
            _ticked = Enumerable.Repeat(true, parts.Count).ToArray();
            _preview = preview;
            _cannotSend = cannotSend;
            _decided = decided;
            Title = Kit.Translate("Report a problem");
            OkButtonText = Kit.Translate("Send");
            DialogHideOnOk = false;
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            box.AddChild(new Label
            {
                Text = Kit.Translate("Send Atlas's developer what's needed to look into a problem. Tick the parts to send and View any of them first: paths, web and e-mail addresses, servers, slot and player names and room passwords are already replaced by marks. It goes through Sentry, with a short report id you can quote in an issue."),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(640, 0)
            });
            var list = new VBoxContainer();
            for (int i = 0; i < parts.Count; i++)
            {
                int index = i;
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                var tick = new CheckBox { Text = $"{Kit.Translate(parts[i].Title)} ({SizeText(parts[i].Bytes)})", ButtonPressed = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                tick.AccessibilityName = Kit.Translate("Send this part");
                tick.SetMeta("problem_part", parts[i].Name);
                tick.Toggled += on => SetTicked(index, on);
                row.AddChild(tick);
                row.AddChild(Kit.Button(Kit.Translate("View"), Kit.Translate("Shows this part exactly as it would be sent"), () => View(parts[index]), small: true));
                list.AddChild(row);
            }
            box.AddChild(list);
            box.AddChild(new Label { Text = Kit.Translate("What went wrong? (optional; names and addresses are taken out of this too)") });
            _note = new TextEdit { CustomMinimumSize = new Vector2(640, 70), WrapMode = TextEdit.LineWrappingMode.Boundary };
            _note.AccessibilityName = Kit.Translate("What went wrong");
            _note.TextChanged += Refresh;
            box.AddChild(_note);
            box.AddChild(Kit.Subtle(Kit.Translate("What would be sent with the parts:")));
            _text = new TextEdit { Editable = false, CustomMinimumSize = new Vector2(640, 160), WrapMode = TextEdit.LineWrappingMode.None, ScrollFitContentHeight = false };
            _text.AccessibilityName = Kit.Translate("What would be sent");
            box.AddChild(_text);
            _why = Kit.Subtle("");
            _why.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _why.CustomMinimumSize = new Vector2(640, 0);
            box.AddChild(_why);
            AddChild(box);
            AddButton(Kit.Translate("Just save it"), true, "save");
            AddCancelButton(Kit.Translate("Cancel"));
            CustomAction += action =>
            {
                if (action.ToString() == "save") Answer(ProblemChoice.Save);
            };
            Confirmed += () => Answer(ProblemChoice.Send);
            Canceled += () => Answer(ProblemChoice.Cancel);
            TreeExiting += () =>
            {
                if (Showing == this) Showing = null;
                if (!_answered) { _answered = true; _decided(ProblemChoice.Cancel, Array.Empty<ProblemPart>(), null); }
            };
            Showing = this;
            Refresh();
        }

        private static string SizeText(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

        /// <summary>The parts ticked now.</summary>
        public IReadOnlyList<ProblemPart> Ticked => _parts.Where((_, i) => _ticked[i]).ToList();

        /// <summary>What would be sent now (for tests).</summary>
        public string PreviewText => _text.Text;

        /// <summary>Why Send is greyed, or "" (for tests).</summary>
        public string WhyNotSend => _why.Text;

        /// <summary>Whether Send can be pressed (for tests).</summary>
        public bool CanSend => !GetOkButton().Disabled;

        /// <summary>The user's note (for tests: what typing in the box does).</summary>
        public string Note
        {
            get => _note.Text;
            set
            {
                _note.Text = value;
                Refresh();
            }
        }

        /// <summary>Ticks or unticks a part (for tests: what its check box does).</summary>
        public void SetTicked(int index, bool on)
        {
            if (index < 0 || index >= _ticked.Length) return;
            _ticked[index] = on;
            Refresh();
        }

        private string? TrimmedNote => _note.Text.Trim().Length == 0 ? null : _note.Text.Trim();

        private void Refresh()
        {
            _text.Text = _preview(Ticked, TrimmedNote);
            string? why = Ticked.Count == 0 ? Kit.Translate("Tick at least one part to send.") : _cannotSend(Ticked);
            _why.Text = why ?? "";
            _why.Visible = why != null;
            GetOkButton().Disabled = why != null;
        }

        /// <summary>A part's text, read-only, in a window of its own.</summary>
        private void View(ProblemPart part)
        {
            var viewer = new AcceptDialog { Title = Kit.Translate(part.Title), OkButtonText = Kit.Translate("Close"), Unresizable = false };
            var text = new TextEdit { Editable = false, Text = part.Text, CustomMinimumSize = new Vector2(760, 440), WrapMode = TextEdit.LineWrappingMode.None };
            text.AccessibilityName = Kit.Translate(part.Title);
            viewer.AddChild(text);
            viewer.SetMeta("problem_view", part.Name);
            viewer.Confirmed += viewer.QueueFree;
            viewer.Canceled += viewer.QueueFree;
            AddChild(viewer);
            viewer.PopupCentered(new Vector2I(800, 520));
        }

        /// <summary>Answers as a button does (for tests).</summary>
        public void Choose(ProblemChoice choice) => Answer(choice);

        private void Answer(ProblemChoice choice)
        {
            if (_answered) return;
            // Whether it can be sent is asked again at the press (another report may be going now).
            Refresh();
            if (choice == ProblemChoice.Send && GetOkButton().Disabled) return;
            if (choice == ProblemChoice.Save && Ticked.Count == 0) return;
            _answered = true;
            var ticked = Ticked;
            string? note = TrimmedNote;
            if (Showing == this) Showing = null;
            // Hidden at once, so a dialog the answer opens isn't behind it while it's freed.
            Hide();
            QueueFree();
            _decided(choice, ticked, note);
        }
    }
}
