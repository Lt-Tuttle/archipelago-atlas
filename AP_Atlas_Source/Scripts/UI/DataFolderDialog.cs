using System;
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The first-run choice when Atlas's own folder can't be written to: the user's local app data folder, or another
    /// folder (remembered in a one-line file, named here). Continue checks the folder can be written and says why not;
    /// Quit writes nothing at all.
    /// </summary>
    public sealed partial class DataFolderDialog : ConfirmationDialog
    {
        private readonly string _fallback;
        private readonly Func<string, string?> _probe;
        private readonly Action<string, bool> _decided;
        private readonly Func<string, string> _tr;
        private readonly CheckBox _useFallback;
        private readonly CheckBox _useAnother;
        private readonly LineEdit _another;
        private readonly Label _problem;
        private bool _answered;

        /// <param name="problem">Why the portable folder can't be used.</param>
        /// <param name="portable">The folder that couldn't be used.</param>
        /// <param name="fallback">The local app data folder offered first.</param>
        /// <param name="pointerPath">Where another folder's choice is remembered (named in the dialog).</param>
        /// <param name="probe">Why a folder can't be written, or null when it can.</param>
        /// <param name="decided">The folder, and whether it was the user's own choice (to remember).</param>
        /// <param name="quit">The user chose to quit instead.</param>
        public DataFolderDialog(string problem, string portable, string fallback, string pointerPath, Func<string, string?> probe,
            Action<string, bool> decided, Action quit, Func<string, string> tr)
        {
            _fallback = fallback;
            _probe = probe;
            _decided = decided;
            _tr = tr;
            Title = tr("Where should Atlas keep your data?");
            OkButtonText = tr("Continue");
            CancelButtonText = tr("Quit");
            DialogHideOnOk = false;
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            box.AddChild(new Label
            {
                Text = tr("Atlas is in a folder it can't write to ({0}: {1}), so your multiworlds, settings and logs can't be kept there. Choose where they go; nothing is written until you do.")
                    .Replace("{0}", portable).Replace("{1}", problem),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(600, 0)
            });
            var group = new ButtonGroup();
            _useFallback = new CheckBox { Text = tr("Your local app data folder: {0}").Replace("{0}", fallback), ButtonGroup = group, ButtonPressed = true };
            _useFallback.AccessibilityName = tr("Your local app data folder");
            box.AddChild(_useFallback);
            _useAnother = new CheckBox { Text = tr("Another folder (remembered in {0}):").Replace("{0}", pointerPath), ButtonGroup = group };
            _useAnother.AccessibilityName = tr("Another folder");
            box.AddChild(_useAnother);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            _another = new LineEdit { PlaceholderText = tr("A folder on this PC"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _another.AccessibilityName = tr("The other folder");
            _another.TextChanged += _ => _useAnother.ButtonPressed = true;
            row.AddChild(_another);
            row.AddChild(Kit.Button(tr("Choose…"), tr("Pick the folder"), ChooseFolder));
            box.AddChild(row);
            _problem = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(600, 0), Visible = false };
            _problem.AddThemeColorOverride("font_color", ThemeColors.Error);
            box.AddChild(_problem);
            AddChild(box);
            Confirmed += Continue;
            Canceled += () =>
            {
                if (_answered) return;
                _answered = true;
                QueueFree();
                quit();
            };
        }

        /// <summary>Whether "another folder" is chosen (for tests).</summary>
        public bool UseAnother
        {
            get => _useAnother.ButtonPressed;
            set
            {
                _useAnother.ButtonPressed = value;
                _useFallback.ButtonPressed = !value;
            }
        }

        /// <summary>The other folder as typed (for tests).</summary>
        public string AnotherPath
        {
            get => _another.Text;
            set => _another.Text = value;
        }

        /// <summary>What's wrong with the folder tried last, or "" (for tests).</summary>
        public string Problem => _problem.Visible ? _problem.Text : "";

        private void ChooseFolder()
        {
            var picker = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenDir, Access = FileDialog.AccessEnum.Filesystem, Title = Title, UseNativeDialog = false };
            picker.DirSelected += dir =>
            {
                _another.Text = dir;
                _useAnother.ButtonPressed = true;
            };
            picker.Canceled += picker.QueueFree;
            picker.Confirmed += picker.QueueFree;
            AddChild(picker);
            picker.PopupCentered(new Vector2I(760, 520));
        }

        private void Continue()
        {
            if (_answered) return;
            string folder = _useAnother.ButtonPressed ? _another.Text.Trim() : _fallback;
            string? problem = folder.Length == 0 ? "no folder was given" : _probe(folder);
            if (problem != null)
            {
                _problem.Text = _tr("That folder can't be used: {0}.").Replace("{0}", problem);
                _problem.Visible = true;
                return;
            }
            _answered = true;
            QueueFree();
            _decided(folder, _useAnother.ButtonPressed);
        }
    }
}
