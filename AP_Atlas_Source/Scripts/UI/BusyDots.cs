#nullable enable
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Three dots that light in turn while the work they stand for runs (the owner, round 13: during Install games' check "there
    /// is no feedback that Atlas is doing work and not just frozen"): beside a step with no bar movement of its own, such as the
    /// engine's health check or the Pack Doctor's checks. A timer steps them five times a second only while <see cref="Running"/>,
    /// and they redraw only while on screen; nothing runs per frame, so Atlas idles again (low-processor mode) once the work
    /// ends. Drawn, not a font glyph, so no font can lack it.
    /// </summary>
    public sealed partial class BusyDots : Control
    {
        private const int Dots = 3;
        private const float Radius = 3f, Gap = 9f;
        private readonly Godot.Timer _timer = new() { WaitTime = 0.2, OneShot = false };
        private bool _running, _held;

        public BusyDots(string accessibleName)
        {
            CustomMinimumSize = new Vector2(Dots * Gap + 8, 18);
            SizeFlagsVertical = SizeFlags.ShrinkCenter;
            MouseFilter = MouseFilterEnum.Ignore;
            AccessibilityName = accessibleName;
            _timer.Timeout += Step;
            AddChild(_timer);
        }

        /// <summary>Whether the work runs: the dots draw and step while true; when false they stop and draw nothing, their place kept (nothing beside them jumps).</summary>
        public bool Running
        {
            get => _running;
            set
            {
                if (_running == value) return;
                _running = value;
                if (!value) _held = false;
                QueueRedraw();
                Sync();
            }
        }

        /// <summary>Whether its timer runs (for tests: never once <see cref="Running"/> is false, it's held, or it left the tree).</summary>
        public bool Animating => _timer.IsInsideTree() && !_timer.IsStopped();

        /// <summary>How many times the dots stepped on screen (for tests).</summary>
        public int Ticks { get; private set; }

        /// <summary>Shown but still, the first dot lit (the visual check's picture: a moving dot would differ from run to run).</summary>
        internal void HoldStill()
        {
            _held = true;
            _timer.Stop();
            Ticks = 0;
            QueueRedraw();
        }

        // The timer (a child) enters the tree after this control does: started at the end of the frame.
        public override void _EnterTree() => Ui.Defer(this, Sync, "starting the busy dots");

        public override void _ExitTree() => _timer.Stop();

        private void Sync()
        {
            if (!_timer.IsInsideTree()) return; // entering: _EnterTree's deferred call comes back once the timer is in
            if (_running && !_held && _timer.IsStopped()) _timer.Start();
            else if ((!_running || _held) && !_timer.IsStopped()) _timer.Stop();
        }

        // Off screen (a dialog hidden while its batch runs) nothing is redrawn that nobody sees.
        private void Step()
        {
            if (!IsVisibleInTree()) return;
            Ticks++;
            QueueRedraw();
        }

        public override void _Draw()
        {
            if (!_running && !_held) return;
            var lit = ThemeColors.AccentText(ThemeColors.Surface);
            var dim = new Color(lit.R, lit.G, lit.B, 0.3f);
            float y = Size.Y / 2f, x = (Size.X - (Dots - 1) * Gap) / 2f;
            for (int i = 0; i < Dots; i++) DrawCircle(new Vector2(x + i * Gap, y), Radius, i == Ticks % Dots ? lit : dim);
        }
    }
}
