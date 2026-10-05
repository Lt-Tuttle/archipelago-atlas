using System;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>Helpers for the window.</summary>
    public static class Ui
    {
        /// <summary>
        /// Runs <paramref name="action"/> on the main thread at the end of the frame. It can be called from any thread
        /// (network, engine and download callbacks). It doesn't run if <paramref name="owner"/> has been freed by then (a
        /// closed slot, window or tab); pass null when the work must happen regardless. A failure is logged with what was
        /// being done, instead of only reaching Godot's output.
        /// </summary>
        public static void Defer(GodotObject? owner, Action action, string doing = "updating the window")
        {
            Callable.From(() =>
            {
                if (owner != null && !GodotObject.IsInstanceValid(owner)) return;
                try { action(); }
                catch (Exception ex) { AP_Atlas.Core.Async.Report(ex, doing, tellUser: false); }
            }).CallDeferred();
        }

        /// <summary>
        /// Like <see cref="Defer"/>, for writing to the window's logs: a failure goes only to Godot's output, because
        /// logging it would write to the logs again (and fail again, every frame).
        /// </summary>
        public static void DeferQuiet(GodotObject? owner, Action action)
        {
            Callable.From(() =>
            {
                if (owner != null && !GodotObject.IsInstanceValid(owner)) return;
                try { action(); }
                catch (Exception ex) { GD.PrintErr("Couldn't write to a log in the window: " + ex.Message); }
            }).CallDeferred();
        }
    }
}
