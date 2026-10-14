#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.Reports;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// "Suggest these fixes to Atlas": the user's own fixes, each with a tick (all on), exactly what would be sent (it
    /// follows the ticks), a note, and the three answers that are the permission's (Send once, Always send, Don't send).
    /// </summary>
    public sealed partial class SuggestFixesDialog : AcceptDialog
    {
        private readonly IReadOnlyList<FixPair> _pairs;
        private readonly bool[] _ticked;
        private readonly Func<IReadOnlyList<FixPair>, string?, string> _preview;
        private readonly Action<ReportDecision, IReadOnlyList<FixPair>, string?> _decided;
        private readonly TextEdit _text;
        private readonly TextEdit _note;
        private bool _answered;

        /// <summary>The dialog open now (for tests), or null.</summary>
        public static SuggestFixesDialog? Showing { get; private set; }

        public SuggestFixesDialog(IReadOnlyList<FixPair> pairs, string game, Func<IReadOnlyList<FixPair>, string?, string> preview, Action<ReportDecision, IReadOnlyList<FixPair>, string?> decided)
        {
            _pairs = pairs;
            _ticked = Enumerable.Repeat(true, pairs.Count).ToArray();
            _preview = preview;
            _decided = decided;
            Title = Kit.Translate("Suggest your fixes to Atlas");
            OkButtonText = Kit.Translate("Send once");
            DialogHideOnOk = false;
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            box.AddChild(new Label
            {
                Text = Kit.Translate("Atlas's developer reads the fixes people make for patterns that become rules for every pack ({0}'s and games no one has fixed yet). Tick the ones to send; below is exactly what goes, through Sentry. No names, paths, servers, slots or chat.").Replace("{0}", game),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(640, 0)
            });
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(640, 160), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            scroll.AddChild(list);
            for (int i = 0; i < pairs.Count; i++)
            {
                int index = i;
                var tick = new CheckBox { Text = $"{pairs[i].PackWrote}  →  {pairs[i].Chose}", ButtonPressed = true, AccessibilityName = Kit.Translate("Send this fix") };
                tick.SetMeta("suggest_pair", i);
                tick.Toggled += on => SetTicked(index, on);
                list.AddChild(tick);
            }
            box.AddChild(scroll);
            _text = new TextEdit { Editable = false, CustomMinimumSize = new Vector2(640, 200), WrapMode = TextEdit.LineWrappingMode.None, ScrollFitContentHeight = false };
            _text.AccessibilityName = Kit.Translate("What would be sent");
            box.AddChild(_text);
            box.AddChild(new Label { Text = Kit.Translate("Anything to add? (optional; names and addresses are taken out of this too)") });
            _note = new TextEdit { CustomMinimumSize = new Vector2(640, 50), WrapMode = TextEdit.LineWrappingMode.Boundary };
            _note.AccessibilityName = Kit.Translate("Your note");
            _note.TextChanged += Refresh;
            box.AddChild(_note);
            var where = Kit.Subtle(Kit.Translate("\"Always send\" can be taken back in Settings → Privacy & permissions. A copy of what's sent stays in Atlas's reports folder."));
            where.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            where.CustomMinimumSize = new Vector2(640, 0);
            box.AddChild(where);
            AddChild(box);
            AddButton(Kit.Translate("Always send"), true, "always");
            AddCancelButton(Kit.Translate("Don't send"));
            CustomAction += action =>
            {
                if (action.ToString() == "always") Answer(ReportDecision.SendAlways);
            };
            Confirmed += () => Answer(ReportDecision.SendOnce);
            Canceled += () => Answer(ReportDecision.DontSend);
            TreeExiting += () =>
            {
                if (Showing == this) Showing = null;
                if (!_answered) { _answered = true; _decided(ReportDecision.DontSend, Array.Empty<FixPair>(), null); }
            };
            Showing = this;
            Refresh();
        }

        /// <summary>The fixes ticked now.</summary>
        public IReadOnlyList<FixPair> Ticked => _pairs.Where((_, i) => _ticked[i]).ToList();

        /// <summary>What would be sent now (for tests).</summary>
        public string PreviewText => _text.Text;

        /// <summary>Ticks or unticks a fix (for tests: what its check box does).</summary>
        public void SetTicked(int index, bool on)
        {
            if (index < 0 || index >= _ticked.Length) return;
            _ticked[index] = on;
            Refresh();
        }

        private string? Note => _note.Text.Trim().Length == 0 ? null : _note.Text.Trim();

        private void Refresh()
        {
            _text.Text = _preview(Ticked, Note);
            GetOkButton().Disabled = Ticked.Count == 0;
        }

        /// <summary>Answers as a button does (for tests).</summary>
        public void Choose(ReportDecision decision) => Answer(decision);

        private void Answer(ReportDecision decision)
        {
            if (_answered) return;
            if (decision != ReportDecision.DontSend && Ticked.Count == 0) return;
            _answered = true;
            var ticked = Ticked;
            string? note = Note;
            if (Showing == this) Showing = null;
            QueueFree();
            _decided(decision, ticked, note);
        }
    }
}
