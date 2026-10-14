#nullable enable
using System;
using System.IO;
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// "Start a pack" for a game with no map pack: what it makes (every location a pin, grouped by area, every item, the
    /// pack's own autotracking), its name, and a map picture of the user's (optional; a plain one otherwise).
    /// </summary>
    public sealed partial class StartPackDialog : ConfirmationDialog
    {
        private readonly LineEdit _name;
        private readonly Label _pictureLabel;
        private readonly Action<string, string?> _make;
        private string? _picture;
        private bool _answered;

        /// <summary>The dialog open now (for tests), or null.</summary>
        public static StartPackDialog? Showing { get; private set; }

        public StartPackDialog(string game, int locations, int areas, int items, int tiles, Action<string, string?> make)
        {
            _make = make;
            Title = Kit.Translate("Start a pack for {0}").Replace("{0}", game);
            OkButtonText = Kit.Translate("Make the pack");
            DialogHideOnOk = false;
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            var what = new Label
            {
                Text = Kit.Translate("No map pack exists for {0}, so Atlas can start one from the game's own names: {1} locations as pins, grouped into {2} areas on one map; all {3} items, {4} of them in Key Items; and the pack's own autotracking, so it works in PopTracker too. Then move the pins onto a real map (right-click a pin: Move this pin), give tiles their pictures, and share it.")
                    .Replace("{0}", game).Replace("{1}", locations.ToString()).Replace("{2}", areas.ToString()).Replace("{3}", items.ToString()).Replace("{4}", tiles.ToString()),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(560, 0)
            };
            box.AddChild(what);
            var nameRow = new HBoxContainer();
            nameRow.AddThemeConstantOverride("separation", 8);
            nameRow.AddChild(new Label { Text = Kit.Translate("Pack name:") });
            _name = new LineEdit { Text = Kit.Translate("{0} (made in Atlas)").Replace("{0}", game), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = Kit.Translate("Pack name") };
            nameRow.AddChild(_name);
            box.AddChild(nameRow);
            var pictureRow = new HBoxContainer();
            pictureRow.AddThemeConstantOverride("separation", 8);
            pictureRow.AddChild(Kit.Button(Kit.Translate("Choose a map picture…"), Kit.Translate("A picture of the game's world to lay the pins on (PNG or JPEG); without one, the pack gets a plain map with a block per area"), PickPicture));
            _pictureLabel = Kit.Subtle(Kit.Translate("No picture: a plain map with a block per area."));
            pictureRow.AddChild(_pictureLabel);
            box.AddChild(pictureRow);
            AddChild(box);
            Confirmed += () => Answer(true);
            Canceled += () => Answer(false);
            TreeExiting += () =>
            {
                _answered = true;
                if (Showing == this) Showing = null;
            };
            Showing = this;
        }

        private void PickPicture()
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.png, *.jpg, *.jpeg ; Pictures" },
                UseNativeDialog = true,
                Title = Kit.Translate("A map picture")
            };
            dialog.FileSelected += path =>
            {
                dialog.QueueFree();
                SetPicture(path);
            };
            dialog.Canceled += () => dialog.QueueFree();
            AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        /// <summary>Uses a picture file for the map (for tests: what choosing one does).</summary>
        public void SetPicture(string? path)
        {
            _picture = path;
            _pictureLabel.Text = path == null ? Kit.Translate("No picture: a plain map with a block per area.") : Path.GetFileName(path);
        }

        /// <summary>Makes the pack with this name (for tests: what Make the pack does).</summary>
        public void ChooseForTests(string name)
        {
            _name.Text = name;
            Answer(true);
        }

        private void Answer(bool make)
        {
            if (_answered) return;
            if (make && _name.Text.Trim().Length == 0) return;
            _answered = true;
            if (Showing == this) Showing = null;
            QueueFree();
            if (make) _make(_name.Text.Trim(), _picture);
        }
    }
}
