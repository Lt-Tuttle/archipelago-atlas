#nullable enable
using System;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Hands the keys nothing else took (a focused text box keeps its own) to a view while it's in the tree: the Pack
    /// Doctor's one-by-one review reads Enter, the digits, C, N, S, Ctrl+Z and Escape through it.
    /// </summary>
    public partial class KeyCatcher : Control
    {
        /// <summary>A key pressed (not a repeat); true when the view used it.</summary>
        public Func<InputEventKey, bool>? OnKey { get; set; }

        public KeyCatcher()
        {
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _UnhandledKeyInput(InputEvent @event)
        {
            if (@event is InputEventKey { Pressed: true, Echo: false } key && OnKey?.Invoke(key) == true)
                GetViewport().SetInputAsHandled();
        }
    }
}
