using System;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// A view's refresh that runs only while the view shows. Asked for while the view is hidden (another tool or another
    /// slot in front, or the view moved out of the window), it waits until the view shows again; asked for several times
    /// in a frame, it runs once, at the end of the frame. With 20 slots connected, each with its own copy of every tool,
    /// only what's on screen does the work. Main thread only.
    /// </summary>
    public sealed class ViewRefresh
    {
        private readonly CanvasItem _view;
        private readonly Action _refresh;
        private readonly string _doing;
        private bool _pending, _scheduled;

        /// <param name="view">The view whose visibility decides when the refresh runs.</param>
        /// <param name="refresh">The refresh itself.</param>
        /// <param name="doing">What the refresh does, for the log if it fails ("refreshing the hints").</param>
        public ViewRefresh(CanvasItem view, Action refresh, string doing)
        {
            _view = view;
            _refresh = refresh;
            _doing = doing;
            // Shown again: its tool chosen, its slot selected, or moved back into the window.
            view.VisibilityChanged += Schedule;
            view.TreeEntered += Schedule;
        }

        /// <summary>Whether a refresh is waiting (something changed while the view was hidden).</summary>
        public bool Pending => _pending;

        /// <summary>Asks for a refresh: at the end of this frame if the view shows, otherwise when it next shows.</summary>
        public void Request()
        {
            _pending = true;
            Schedule();
        }

        /// <summary>From inside the refresh: there's more to do, next frame (long work spread over frames).</summary>
        public void ContinueNextFrame()
        {
            _pending = true;
            if (_scheduled) return;
            _scheduled = true;
            Ui.NextFrame(_view, Run, _doing);
        }

        /// <summary>Runs a waiting refresh now, shown or not: before reading or revealing what the view shows.</summary>
        public void Flush()
        {
            if (!_pending) return;
            _pending = false;
            _refresh();
        }

        private void Schedule()
        {
            if (!_pending || _scheduled || !GodotObject.IsInstanceValid(_view) || !_view.IsVisibleInTree()) return;
            _scheduled = true;
            Ui.Defer(_view, Run, _doing);
        }

        private void Run()
        {
            _scheduled = false;
            if (!_pending || !_view.IsVisibleInTree()) return;
            _pending = false;
            _refresh();
        }
    }
}
