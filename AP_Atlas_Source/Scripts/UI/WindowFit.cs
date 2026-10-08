#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using AP_Atlas.Core.Geometry;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Atlas on the screen it's on: the window follows Windows' display scale (the zoom setting is relative to it), every
    /// dialog and window fits the space it opens in at any scale, and the Engine and Pack Doctor windows are real
    /// Windows windows that follow whichever monitor they're moved to. The one place that reads the screen or sets a
    /// content scale (the guard rails keep it so).
    /// </summary>
    public static class WindowFit
    {
        /// <summary>The main window's smallest size, in logical units (before the scale).</summary>
        public static readonly Vector2I MainMinLogical = new(980, 680); // the activity bar's compact layout, the menu and the status bar need the height

        /// <summary>The main window's first size, in logical units, when nothing is remembered.</summary>
        public static readonly Vector2I MainFirstLogical = new(1152, 648);

        /// <summary>Test hooks: stand in for Windows' display scale and for the screen's usable area (a headless run has neither).</summary>
        internal static float? TestScale;
        internal static Rect2I? TestUsableRect;

        /// <summary>Raised after the main window's scale changed (zoom, or a move to a screen with another scale): open windows follow.</summary>
        public static event Action? ScaleChanged;

        private static readonly List<Window> _watched = new();
        private static readonly Dictionary<Window, Action<Rect2I>> _rememberers = new();

        /// <summary>Whether a window is being fitted (for tests).</summary>
        internal static bool IsWatched(Window window) => _watched.Contains(window);
        private static readonly List<Window> _natives = new();
        private static Window? _root;
        private static Func<AppSettings>? _settings;

        /// <summary>Windows' display scale for a screen (1 at 100%, 1.5 at 150%); 1 where there's no screen (headless).</summary>
        public static float WindowsScale(int screen)
        {
            if (TestScale is { } test) return test;
            if (!DisplayServer.HasFeature(DisplayServer.Feature.Hidpi)) return 1f;
            int dpi = DisplayServer.ScreenGetDpi(screen);
            return dpi <= 0 ? 1f : Math.Clamp(dpi / 96f, 1f, 4f);
        }

        /// <summary>The screen's area without the taskbar, in pixels; empty where there's no screen (headless).</summary>
        public static Rect2I UsableRect(int screen) => TestUsableRect ?? DisplayServer.ScreenGetUsableRect(screen);

        /// <summary>Windows' scale times the zoom setting: what the window is drawn at.</summary>
        public static float EffectiveScale(int screen, AppSettings settings) => WindowsScale(screen) * Math.Clamp(settings.UiZoom, 50, 200) / 100f;

        /// <summary>The screen whose usable area holds the middle of <paramref name="rect"/>, or the primary one.</summary>
        public static int ScreenHolding(Rect2I rect)
        {
            if (TestUsableRect != null) return 0;
            var middle = new IntRect(rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y);
            for (int i = 0; i < DisplayServer.GetScreenCount(); i++)
                if (middle.CentreIn(ToInt(DisplayServer.ScreenGetUsableRect(i)))) return i;
            return DisplayServer.GetPrimaryScreen();
        }

        /// <summary>
        /// Watches the tree for dialogs and windows and fits each one as it shows; keeps the root for the scale. Once, at
        /// startup.
        /// </summary>
        public static void Watch(SceneTree tree, Func<AppSettings> settings)
        {
            _root = tree.Root;
            _settings = settings;
            tree.NodeAdded += node =>
            {
                if (node is Window window && window != _root && Fits(window)) Attach(window);
            };
            tree.Root.SizeChanged += () => Ui.DeferQuiet(tree.Root, RefitAll);
        }

        // Atlas's own windows and every dialog; Godot's menus and tooltips place themselves.
        private static bool Fits(Window window) => window is AcceptDialog || window.GetType().Assembly == typeof(WindowFit).Assembly;

        // Dialogs opened by Pop (dialog → its height cap), sized to their content, and the ones with a shrink pending this frame.
        private static readonly Dictionary<Window, int> _popped = new();
        private static readonly HashSet<Window> _shrinking = new();

        /// <summary>
        /// Opens a dialog centred at a width, as tall as its content and never the screen's height: Godot grows a window to
        /// its content's minimum (WrapControls) and never shrinks it, and a wrapped label measured before it has its width
        /// reports a height of hundreds of pixels; so the content lays out first, then the window is sized to it
        /// (Window.ResetSize), and again whenever the content's minimum changes (a line shown, text wrapped at its real
        /// width). The height is capped at <paramref name="maxHeightLogical"/> and at the area; a dialog that can be taller
        /// holds its list in a ScrollContainer.
        /// </summary>
        public static void Pop(Window dialog, int width, int maxHeightLogical = 720)
        {
            dialog.WrapControls = true;
            dialog.MinSize = new Vector2I(width, 0); // ResetSize keeps the width: MinSize counts where it's the bigger
            _popped[dialog] = maxHeightLogical;
            dialog.TreeExiting += () => { _popped.Remove(dialog); _shrinking.Remove(dialog); };
            // The caller's content box (AcceptDialog's own label and buttons are internal, not listed): its minimum changing is the cue.
            foreach (var content in dialog.GetChildren().OfType<Control>()) content.MinimumSizeChanged += () => RequestShrink(dialog);
            dialog.PopupCentered(new Vector2I(width, 0));
            RequestShrink(dialog);
        }

        /// <summary>Sizes a popped dialog to its content again (after its content changed); one pass per frame.</summary>
        public static void RequestShrink(Window dialog)
        {
            if (!_popped.ContainsKey(dialog) || !_shrinking.Add(dialog)) return;
            // Two deferrals: the containers sort at the end of this frame and the labels re-shape at their width then; the
            // second deferral sees the settled minimum.
            Ui.DeferQuiet(dialog, () => Ui.DeferQuiet(dialog, () =>
            {
                _shrinking.Remove(dialog);
                ShrinkToContent(dialog);
            }));
        }

        private static void ShrinkToContent(Window dialog)
        {
            if (!GodotObject.IsInstanceValid(dialog) || !dialog.Visible || dialog.ForceNative || !_popped.TryGetValue(dialog, out int maxHeight)) return;
            var area = AvailableLogical(dialog).Size;
            if (area.X <= 0 || area.Y <= 0) return;
            int cap = Math.Min(maxHeight, Math.Max(80, area.Y - 8));
            dialog.WrapControls = true;
            dialog.ResetSize(); // max(MinSize, the content's minimum): the natural size
            if (dialog.Size.Y > cap)
            {
                // The content's minimum would hold the window taller than the cap: the window stops following it (its lists scroll).
                dialog.WrapControls = false;
                dialog.Size = new Vector2I(dialog.Size.X, cap);
            }
            dialog.Position = new Vector2I(Math.Max(0, (area.X - dialog.Size.X) / 2), Math.Max(0, (area.Y - dialog.Size.Y) / 2));
            Fit(dialog);
        }

        private static void Attach(Window window)
        {
            _watched.Add(window);
            // Fitted at the end of the frame it was added in (callers add, then pop up, in one go) and whenever it shows again.
            Ui.DeferQuiet(window, () => Fit(window));
            window.VisibilityChanged += () => { if (window.Visible && !window.ForceNative) Ui.DeferQuiet(window, () => Fit(window)); };
            window.TreeExiting += () => { _watched.Remove(window); _natives.Remove(window); };
        }

        /// <summary>
        /// Sets the main window's scale from Windows' and the zoom, its smallest size, and keeps it within its screen;
        /// then the open windows follow.
        /// </summary>
        public static void ApplyRootScale(Window root, AppSettings settings, Action<int>? fitContents = null)
        {
            int screen = root.CurrentScreen;
            float scale = EffectiveScale(screen, settings);
            if (!Mathf.IsEqualApprox(root.ContentScaleFactor, scale)) root.ContentScaleFactor = scale;
            var usable = UsableRect(screen);
            var min = Scaled(MainMinLogical, scale);
            if (usable.Size.X > 0 && usable.Size.Y > 0) min = new Vector2I(Math.Min(min.X, usable.Size.X), Math.Min(min.Y, usable.Size.Y));
            root.MinSize = min;
            // Resizable (a maximized or full-screen window is the screen's size already; a headless run says "minimized").
            bool resizable = root.Mode is Window.ModeEnum.Windowed or Window.ModeEnum.Minimized;
            if (resizable && usable.Size.X > 0 && usable.Size.Y > 0)
            {
                var fitted = ToGodot(IntRect.Fit(ToInt(new Rect2I(root.Position, root.Size)), ToInt(usable)));
                // The contents first (panes that can't fit the new width hide), so the window can shrink to it.
                fitContents?.Invoke((int)(fitted.Size.X / scale));
                if (fitted.Size != root.Size) root.Size = fitted.Size;
                if (fitted.Position != root.Position) root.Position = fitted.Position;
            }
            else
            {
                fitContents?.Invoke((int)(root.Size.X / scale));
            }
            ScaleChanged?.Invoke();
            RefitAll();
        }

        /// <summary>Where the main window first opens, and where it comes back to: the remembered place, kept within its screen.</summary>
        public static void PlaceRoot(Window root, AppSettings settings, bool fresh)
        {
            var remembered = new Rect2I(new Vector2I(settings.WindowX, settings.WindowY), new Vector2I(settings.WindowWidth, settings.WindowHeight));
            int screen = settings.WindowX >= 0 && settings.WindowY >= 0 ? ScreenHolding(remembered) : root.CurrentScreen;
            var usable = UsableRect(screen);
            if (usable.Size.X <= 0 || usable.Size.Y <= 0) return; // headless: no screen to fit
            float scale = EffectiveScale(screen, settings);
            IntRect rect;
            if (fresh || settings.WindowWidth <= 0 || settings.WindowHeight <= 0)
            {
                var first = Scaled(MainFirstLogical, scale);
                rect = IntRect.Centred(Math.Min(first.X, usable.Size.X * 9 / 10), Math.Min(first.Y, usable.Size.Y * 9 / 10), ToInt(usable));
            }
            else if (settings.WindowX >= 0 && settings.WindowY >= 0)
            {
                rect = IntRect.Fit(ToInt(remembered), ToInt(usable));
            }
            else
            {
                rect = IntRect.Centred(settings.WindowWidth, settings.WindowHeight, ToInt(usable));
            }
            var fitted = ToGodot(rect);
            root.Size = fitted.Size;
            root.Position = fitted.Position;
        }

        /// <summary>
        /// Shows one of Atlas's own windows as a Windows window of its own (movable to another monitor, kept within the
        /// screen it's on, drawn at that screen's scale), or embedded where that isn't possible (headless runs).
        /// </summary>
        /// <param name="remembered">Where the window was last (screen pixels), or null: it opens centred on the owner's screen.</param>
        /// <param name="remember">Told the window's place and size (screen pixels) when it moves, resizes or closes, to keep for next time.</param>
        public static void ShowNative(Window window, Node owner, Vector2I wantedLogical, Vector2I minLogical, Rect2I? remembered = null, Action<Rect2I>? remember = null)
        {
            bool native = TestScale == null && TestUsableRect == null && DisplayServer.HasFeature(DisplayServer.Feature.Subwindows);
            window.Transient = false;
            window.Exclusive = false;
            // A window is visible as made; Godot refuses to change force_native on a shown window, so it's hidden first.
            window.Visible = false;
            window.ForceNative = native;
            var root = owner.GetTree().Root;
            root.AddChild(window);
            if (!native)
            {
                window.MinSize = minLogical;
                window.PopupCentered(wantedLogical);
                return;
            }
            int screen = owner.GetWindow().CurrentScreen;
            var settings = _settings?.Invoke();
            float scale = settings != null ? EffectiveScale(screen, settings) : WindowsScale(screen);
            var usable = UsableRect(screen);
            IntRect rect;
            if (remembered is { } last && last.Size.X > 0 && last.Size.Y > 0)
            {
                // Back where it was, within the screen that holds its middle (another monitor may have gone).
                screen = ScreenHolding(last);
                usable = UsableRect(screen);
                scale = settings != null ? EffectiveScale(screen, settings) : WindowsScale(screen);
                rect = IntRect.Fit(ToInt(last), ToInt(usable));
            }
            else
            {
                var wanted = Scaled(wantedLogical, scale);
                rect = IntRect.Centred(Math.Min(wanted.X, usable.Size.X * 9 / 10), Math.Min(wanted.Y, usable.Size.Y * 9 / 10), ToInt(usable));
            }
            window.ContentScaleFactor = scale;
            var min = Scaled(minLogical, scale);
            window.MinSize = new Vector2I(Math.Min(min.X, rect.Width), Math.Min(min.Y, rect.Height));
            window.Size = new Vector2I(rect.Width, rect.Height);
            window.Position = new Vector2I(rect.X, rect.Y);
            if (remember != null)
            {
                _rememberers[window] = remember;
                window.TreeExiting += () => { Remember(window); _rememberers.Remove(window); };
            }
            window.Show();
            _natives.Add(window);
        }

        /// <summary>A native window's place and size, kept for next time (its own windows call this when they move or resize).</summary>
        public static void Remember(Window window)
        {
            if (!GodotObject.IsInstanceValid(window) || !window.ForceNative || window.Mode != Window.ModeEnum.Windowed) return;
            if (_rememberers.TryGetValue(window, out var remember)) remember(new Rect2I(window.Position, window.Size));
        }

        /// <summary>
        /// A native window after it moved (maybe to another monitor) or the scale changed: drawn at its screen's scale and
        /// kept within it. Atlas's native windows call it from their position-change notification.
        /// </summary>
        public static void Refit(Window window)
        {
            if (!GodotObject.IsInstanceValid(window) || !window.ForceNative) return;
            int screen = window.CurrentScreen;
            var settings = _settings?.Invoke();
            float scale = settings != null ? EffectiveScale(screen, settings) : WindowsScale(screen);
            var usable = UsableRect(screen);
            if (usable.Size.X <= 0 || usable.Size.Y <= 0) return;
            bool rescaled = !Mathf.IsEqualApprox(window.ContentScaleFactor, scale);
            if (rescaled)
            {
                float factor = scale / Math.Max(0.01f, window.ContentScaleFactor);
                window.ContentScaleFactor = scale;
                window.MinSize = new Vector2I(Math.Min((int)(window.MinSize.X * factor), usable.Size.X), Math.Min((int)(window.MinSize.Y * factor), usable.Size.Y));
            }
            if (window.Mode != Window.ModeEnum.Windowed) return;
            var fitted = ToGodot(IntRect.Fit(ToInt(new Rect2I(window.Position, window.Size)), ToInt(usable)));
            if (fitted.Size != window.Size) window.Size = fitted.Size;
            if (fitted.Position != window.Position) window.Position = fitted.Position;
        }

        /// <summary>The logical area an embedded window can take: its parent viewport's visible rectangle.</summary>
        public static Rect2I AvailableLogical(Window window)
        {
            var viewport = window.GetParent()?.GetViewport() ?? _root;
            if (viewport == null) return new Rect2I(0, 0, 100000, 100000);
            var visible = viewport.GetVisibleRect();
            return new Rect2I(Vector2I.Zero, new Vector2I((int)visible.Size.X, (int)visible.Size.Y));
        }

        /// <summary>
        /// An embedded dialog or window fitted into its parent's area: controls asking for more room than there is are
        /// capped (their contents scroll), the window is no larger than the area, and it's moved so every part of it,
        /// its title bar first, is inside.
        /// </summary>
        public static void Fit(Window window)
        {
            if (!GodotObject.IsInstanceValid(window) || !window.Visible || window.ForceNative) return;
            var area = AvailableLogical(window).Size;
            if (area.X <= 0 || area.Y <= 0) return;
            int controlWidth = Math.Max(120, area.X - 48), controlHeight = Math.Max(80, area.Y - 140);
            bool capped = false;
            foreach (var control in Descendants(window).OfType<Control>())
            {
                var min = control.CustomMinimumSize;
                if (min.X > controlWidth || min.Y > controlHeight)
                {
                    control.CustomMinimumSize = new Vector2(Math.Min(min.X, controlWidth), Math.Min(min.Y, controlHeight));
                    capped = true;
                }
            }
            // A capped control changes the window's own smallest size on the next layout: fit once more then.
            if (capped) Ui.DeferQuiet(window, () => Fit(window));
            var most = new Vector2I(Math.Max(100, area.X - 8), Math.Max(80, area.Y - 8));
            if (window.MinSize.X > most.X || window.MinSize.Y > most.Y) window.MinSize = new Vector2I(Math.Min(window.MinSize.X, most.X), Math.Min(window.MinSize.Y, most.Y));
            if (window.Size.X > most.X || window.Size.Y > most.Y) window.Size = new Vector2I(Math.Min(window.Size.X, most.X), Math.Min(window.Size.Y, most.Y));
            var fitted = ToGodot(IntRect.Fit(ToInt(new Rect2I(window.Position, window.Size)), new IntRect(0, 0, area.X, area.Y)));
            if (fitted.Position != window.Position) window.Position = fitted.Position;
            // A popped dialog standing at the clamp while its content asks for far less: sized to the content again.
            if (_popped.ContainsKey(window) && window.Size.Y >= most.Y && window.GetContentsMinimumSize().Y + 2 < window.Size.Y) RequestShrink(window);
        }

        /// <summary>Every open window fitted again (the main window resized, or its scale changed).</summary>
        public static void RefitAll()
        {
            foreach (var window in _watched.ToArray())
            {
                if (!GodotObject.IsInstanceValid(window)) { _watched.Remove(window); continue; }
                if (window.ForceNative) Refit(window);
                else if (window.Visible) Fit(window);
            }
        }

        private static IEnumerable<Node> Descendants(Node node)
        {
            foreach (var child in node.GetChildren())
            {
                yield return child;
                if (child is Window) continue; // a window inside a window fits on its own
                foreach (var grandchild in Descendants(child)) yield return grandchild;
            }
        }

        private static Vector2I Scaled(Vector2I logical, float scale) => new((int)Math.Round(logical.X * scale), (int)Math.Round(logical.Y * scale));
        private static IntRect ToInt(Rect2I rect) => new(rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y);
        private static Rect2I ToGodot(IntRect rect) => new(new Vector2I(rect.X, rect.Y), new Vector2I(rect.Width, rect.Height));
    }
}
