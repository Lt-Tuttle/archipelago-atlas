using System;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>Small confirm and text-entry dialogs, freed when closed.</summary>
    public static class Dialogs
    {
        /// <summary>Asks to confirm; with <paramref name="requirement"/>, a statement the user has to switch on first.</summary>
        public static void Confirm(Node parent, string title, string text, string okText, Action onConfirm, string requirement = null)
        {
            // The text is a label of a set width (the dialog's own wrapped text leaves a tall empty gap below it).
            var dialog = new ConfirmationDialog { Title = title, OkButtonText = okText, DialogHideOnOk = false };
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            box.AddChild(new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(460, 0) });
            var confirmation = Requirement(dialog, requirement);
            if (confirmation != null) box.AddChild(confirmation);
            dialog.AddChild(box);
            dialog.Confirmed += () =>
            {
                if (confirmation != null && !confirmation.ButtonPressed) return;
                dialog.QueueFree();
                onConfirm();
            };
            dialog.Canceled += () => dialog.QueueFree();
            parent.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(500, 0));
        }

        /// <summary>A toggle that lights up when on, rather than a check box (the theme hides an empty check box); OK waits for it.</summary>
        private static Button Requirement(ConfirmationDialog dialog, string requirement)
        {
            if (requirement == null) return null;
            var confirmation = new Button { Text = "Click to confirm: " + requirement, ToggleMode = true, Alignment = HorizontalAlignment.Left, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(460, 0) };
            confirmation.Toggled += on =>
            {
                confirmation.Text = (on ? "Confirmed: " : "Click to confirm: ") + requirement;
                dialog.GetOkButton().Disabled = !on;
            };
            dialog.GetOkButton().Disabled = true;
            return confirmation;
        }

        /// <summary>
        /// Asks for one line of text (a link); <paramref name="onOk"/> gets what was typed. With <paramref name="requirement"/>,
        /// a statement the user has to switch on first: until then the OK button (and Enter) does nothing.
        /// </summary>
        public static void Prompt(Node parent, string title, string text, string initial, string placeholder, string okText, Action<string> onOk, string requirement = null)
        {
            var dialog = new ConfirmationDialog { Title = title, OkButtonText = okText, DialogHideOnOk = false };
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 6);
            box.AddChild(new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(480, 0) });
            var input = new LineEdit { Text = initial ?? "", PlaceholderText = placeholder ?? "", SelectAllOnFocus = true };
            box.AddChild(input);
            var confirmation = Requirement(dialog, requirement);
            if (confirmation != null) box.AddChild(confirmation);
            dialog.AddChild(box);
            dialog.RegisterTextEnter(input);
            dialog.Confirmed += () =>
            {
                if (confirmation != null && !confirmation.ButtonPressed) return;
                string value = input.Text;
                dialog.QueueFree();
                onOk(value);
            };
            dialog.Canceled += () => dialog.QueueFree();
            parent.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(540, 0));
            input.GrabFocus();
        }
    }
}
