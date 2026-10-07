using System;
using AP_Atlas.Core;
using AP_Atlas.Core.Reports;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// "Send a crash report?": exactly what would be sent, as it would be sent, a box for a note, and the three answers
    /// (Send once, Always send, Don't send), which are the crash-report permission's.
    /// </summary>
    public sealed partial class CrashReportDialog : AcceptDialog
    {
        private readonly Action<ReportDecision, string?> _decided;
        private readonly TextEdit _note;
        private bool _answered;

        public CrashReportDialog(string preview, Func<string, string> tr, Action<ReportDecision, string?> decided)
        {
            _decided = decided;
            Title = tr("Send a crash report?");
            OkButtonText = tr("Send once");
            DialogHideOnOk = false;
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            box.AddChild(new Label
            {
                Text = tr("Atlas had a problem last time it ran. You can send a report to Atlas's developer through Sentry, a crash-reporting service. This is the whole report, as it would be sent: what the code was doing, Atlas's version and the kind of PC. No names, paths, servers, slots, chat or log lines."),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(620, 0)
            });
            var report = new TextEdit
            {
                Text = preview,
                Editable = false,
                CustomMinimumSize = new Vector2(620, 260),
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                WrapMode = TextEdit.LineWrappingMode.None,
                ScrollFitContentHeight = false
            };
            report.AccessibilityName = tr("The report");
            box.AddChild(report);
            box.AddChild(new Label { Text = tr("What were you doing? (optional; names and addresses are taken out of this too)") });
            _note = new TextEdit { CustomMinimumSize = new Vector2(620, 60), WrapMode = TextEdit.LineWrappingMode.Boundary };
            _note.AccessibilityName = tr("What you were doing");
            box.AddChild(_note);
            var where = new Label
            {
                Text = tr("\"Always send\" can be taken back in Settings → Privacy & permissions. Reports are kept in Atlas's logs folder either way."),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(620, 0)
            };
            where.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            box.AddChild(where);
            AddChild(box);
            AddButton(tr("Always send"), true, "always");
            AddCancelButton(tr("Don't send"));
            CustomAction += action =>
            {
                if (action.ToString() == "always") Answer(ReportDecision.SendAlways);
            };
            Confirmed += () => Answer(ReportDecision.SendOnce);
            Canceled += () => Answer(ReportDecision.DontSend);
        }

        /// <summary>The note as typed (for tests).</summary>
        public string Note
        {
            get => _note.Text;
            set => _note.Text = value;
        }

        private void Answer(ReportDecision decision)
        {
            if (_answered) return;
            _answered = true;
            string? note = _note.Text.Trim().Length == 0 ? null : _note.Text.Trim();
            QueueFree();
            _decided(decision, note);
        }
    }
}
