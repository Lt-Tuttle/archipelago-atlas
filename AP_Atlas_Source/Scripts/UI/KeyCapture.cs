using System;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// A button showing a command's key. Pressed, it waits for the next key press and takes it: Escape keeps the key as
    /// it is, Backspace means no key, and a modifier on its own isn't a key. Losing the focus ends the wait. The keys it
    /// sees while waiting are its own, never a command's.
    /// </summary>
    public sealed partial class KeyCapture : Button
    {
        private readonly Func<string, string> _tr;
        private string _key = "";

        /// <summary>The key, as commands write it ("Ctrl+F6"); "" for none.</summary>
        public string Key
        {
            get => _key;
            set
            {
                _key = value;
                if (!Capturing) ShowKey();
            }
        }

        /// <summary>Whether the next key press is taken as the key.</summary>
        public bool Capturing { get; private set; }

        /// <summary>The user chose a key ("" for none).</summary>
        public event Action<string>? KeyChosen;

        public KeyCapture(Func<string, string> tr)
        {
            _tr = tr;
            CustomMinimumSize = new Vector2(150, 0);
            Pressed += Begin;
            FocusExited += End;
            GuiInput += OnGuiInput;
            ShowKey();
        }

        private void Begin()
        {
            if (Capturing) return;
            Capturing = true;
            Text = _tr("Press a key…");
            TooltipText = _tr("Escape keeps the key as it is; Backspace means no key.");
        }

        private void End()
        {
            if (!Capturing) return;
            Capturing = false;
            ShowKey();
        }

        private void ShowKey()
        {
            Text = _key.Length > 0 ? _key : _tr("None");
            TooltipText = _tr("Press, then press the key you want.");
        }

        private void OnGuiInput(InputEvent @event)
        {
            if (!Capturing || @event is not InputEventKey key || !key.Pressed || key.Echo) return;
            AcceptEvent(); // whatever the key, it's for this button alone, not a command
            if (key.Keycode == Godot.Key.Escape)
            {
                End();
                return;
            }
            string? chosen = key.Keycode == Godot.Key.Backspace ? "" : CommandKeys.Of(key);
            if (chosen == null) return; // a modifier on its own: keep waiting
            _key = chosen;
            End();
            KeyChosen?.Invoke(chosen);
        }
    }
}
