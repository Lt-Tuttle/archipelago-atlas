using System;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Keys as commands name them ("Ctrl+Shift+P", see AP_Atlas.Core.Commands.Normalize) and as Godot reports them, both
    /// ways: a key press to its name, to run the command it's bound to; a name to a key, to show it in a menu.
    /// </summary>
    public static class CommandKeys
    {
        /// <summary>The name of a key press ("Ctrl+3"), or null for a modifier on its own, a release or a repeat.</summary>
        public static string? Of(InputEventKey key)
        {
            if (!key.Pressed || key.Echo) return null;
            string name = OS.GetKeycodeString(key.Keycode);
            if (string.IsNullOrEmpty(name)) return null;
            return AP_Atlas.Core.Commands.Normalize(
                (key.CtrlPressed ? "Ctrl+" : "") + (key.ShiftPressed ? "Shift+" : "") + (key.AltPressed ? "Alt+" : "") + (key.MetaPressed ? "Win+" : "") + name);
        }

        /// <summary>A Shortcut Godot can show in a menu for a key's name; null when the name isn't a key.</summary>
        public static Shortcut? ToShortcut(string name)
        {
            var key = ToEvent(name);
            return key == null ? null : new Shortcut { Events = new Godot.Collections.Array { key } };
        }

        /// <summary>The key press a name stands for (to show it, or for a test to press it); null when the name isn't a key.</summary>
        public static InputEventKey? ToEvent(string name)
        {
            string? canonical = AP_Atlas.Core.Commands.Normalize(name);
            if (canonical == null) return null;
            // "Ctrl++" is Ctrl and the plus key: the key is whatever follows the last modifier.
            bool plus = canonical.EndsWith("++", StringComparison.Ordinal);
            var parts = (plus ? canonical[..^2] : canonical).Split('+');
            string keyName = plus ? "+" : parts[^1];
            var press = new InputEventKey { Pressed = true };
            foreach (string part in plus ? parts : parts[..^1])
            {
                if (part == "Ctrl") press.CtrlPressed = true;
                else if (part == "Shift") press.ShiftPressed = true;
                else if (part == "Alt") press.AltPressed = true;
                else if (part == "Win") press.MetaPressed = true;
            }
            var keycode = KeycodeOf(keyName);
            if (keycode == Key.None) return null;
            press.Keycode = keycode;
            return press;
        }

        private static Key KeycodeOf(string name)
        {
            switch (name)
            {
                case "Tab": return Key.Tab;
                case "Escape": return Key.Escape;
                case "Enter": return Key.Enter;
                case "Space": return Key.Space;
                case "Backspace": return Key.Backspace;
                case "Delete": return Key.Delete;
                case "Insert": return Key.Insert;
                case "Home": return Key.Home;
                case "End": return Key.End;
                case "PageUp": return Key.Pageup;
                case "PageDown": return Key.Pagedown;
                case "Up": return Key.Up;
                case "Down": return Key.Down;
                case "Left": return Key.Left;
                case "Right": return Key.Right;
                // Punctuation, as Commands.Normalize writes it (Godot names these "Comma", "Period", ...).
                case ",": return Key.Comma;
                case ".": return Key.Period;
                case "-": return Key.Minus;
                case "+": return Key.Plus;
                case "/": return Key.Slash;
                case ";": return Key.Semicolon;
                case "'": return Key.Apostrophe;
                case "[": return Key.Bracketleft;
                case "]": return Key.Bracketright;
                case "`": return Key.Quoteleft;
            }
            if (name.Length == 1 && name[0] == (char)92) return Key.Backslash;
            if (name.Length == 1 && char.IsAsciiDigit(name[0])) return Key.Key0 + (name[0] - '0');
            if (name.Length == 1 && char.IsAsciiLetterUpper(name[0])) return Key.A + (name[0] - 'A');
            if (name.Length >= 2 && name[0] == 'F' && int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 24) return Key.F1 + (f - 1);
            return OS.FindKeycodeFromString(name);
        }
    }
}
