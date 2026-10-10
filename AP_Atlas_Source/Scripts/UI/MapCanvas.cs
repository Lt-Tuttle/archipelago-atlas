using System;
using System.Collections.Generic;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// A map with pins on it, as the Map Tracker and the Pack Doctor's editor both show one: the map's image and the pins
    /// are ordinary controls inside a scrolling area (so pins have focus, tooltips and screen-reader names, and nothing
    /// runs every frame), scaled by one zoom. The wheel zooms around the cursor, any mouse button drags the map, a left
    /// click on the empty map reports the map point (<see cref="Clicked"/>), and the host saves and restores the view
    /// (<see cref="Center"/>, <see cref="Zoom"/>). A pin is placed by map coordinates; its size on screen is its map size
    /// scaled by the zoom (never below <see cref="MinPinScreen"/>), or a fixed size for an editor's handles.
    /// </summary>
    public sealed partial class MapCanvas : Control
    {
        public sealed class Pin
        {
            public string Key = "";
            /// <summary>The pin's place in map pixels.</summary>
            public float X, Y;
            public Control Control = null!;
            /// <summary>The pin's size in map pixels (the border included); 0 for <see cref="FixedPinSize"/> on screen whatever the zoom.</summary>
            public float MapSize;
            /// <summary>The pin's border in map pixels (0: left as the pin was styled); scaled with the zoom like the size.</summary>
            public float MapBorder;
        }

        public const float FixedPinSize = 22f;
        public const float MinPinScreen = 8f;
        public const float MinZoom = 0.05f;
        public const float MaxZoom = 6f;
        /// <summary>One wheel notch.</summary>
        public const float WheelStep = 1.2f;

        private readonly ScrollContainer _scroll;
        private readonly Control _surface;
        private readonly TextureRect _background;
        private readonly Label _noImage;
        private Vector2 _mapSize = new(1600, 1000);
        private float _zoom = 1f;
        private readonly List<Pin> _pins = new();
        private Control? _ring;
        private Vector2 _ringAt;
        private bool _dragging;
        private Vector2 _dragLast, _dragStart;
        private bool _dragMoved;
        private bool _fitWhenSized;

        /// <summary>The zoom or the scroll changed (by the user, or a call); the host saves the view.</summary>
        public event Action? ViewChanged;

        /// <summary>A left click on the empty map, in map pixels.</summary>
        public event Action<Vector2>? Clicked;

        public MapCanvas()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            _scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            _scroll.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(_scroll);
            _surface = new Control { MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = _mapSize, AccessibilityName = "Map" };
            _surface.GuiInput += OnSurfaceInput;
            _scroll.AddChild(_surface);
            _background = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
            _surface.AddChild(_background);
            _noImage = new Label { Position = new Vector2(10, 10), MouseFilter = MouseFilterEnum.Ignore, Visible = false };
            _noImage.Text = Tr("This map has no usable image. Its pins are still shown where they go.");
            _noImage.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Text);
            _noImage.AddThemeFontSizeOverride("font_size", 16);
            _surface.AddChild(_noImage);
            _scroll.Resized += () =>
            {
                if (!HasView) return;
                if (_viewWhenSized is { } pending) SetView(pending.Center, pending.Zoom, pending.FitIfSmaller);
                else if (_fitWhenSized) FitToView(remember: false);
            };
            _scroll.GetHScrollBar().ValueChanged += _ => ViewChanged?.Invoke();
            _scroll.GetVScrollBar().ValueChanged += _ => ViewChanged?.Invoke();
        }

        /// <summary>The control the map's own input reaches (for tests that send it events).</summary>
        public Control Surface => _surface;

        public float Zoom => _zoom;
        public Vector2 MapSize => _mapSize;
        public Vector2 ViewSize => _scroll.Size;

        /// <summary>The scrolling area has a size (before the window laid it out, a fit would be meaningless).</summary>
        public bool HasView => _scroll.Size.X > 0 && _scroll.Size.Y > 0;

        private (Vector2 Center, float Zoom, bool FitIfSmaller)? _viewWhenSized;

        /// <summary>A saved view waiting for the map to have a size (for tests).</summary>
        public bool ViewPending => _viewWhenSized != null;

        /// <summary>The map point at the middle of the view, in map pixels.</summary>
        public Vector2 Center => (ScrollPosition + _scroll.Size / 2) / _zoom;

        public Vector2 ScrollPosition
        {
            get => new(_scroll.ScrollHorizontal, _scroll.ScrollVertical);
            set
            {
                _scroll.ScrollHorizontal = (int)Math.Max(0, value.X);
                _scroll.ScrollVertical = (int)Math.Max(0, value.Y);
            }
        }

        public IReadOnlyList<Pin> Pins => _pins;

        /// <summary>Shows a map: its image (or none) and its size in map pixels. The view stays where it was; the host restores or fits it.</summary>
        public void SetMap(Texture2D? background, Vector2 size, string? missingImage = null)
        {
            _mapSize = size.X > 0 && size.Y > 0 ? size : new Vector2(1600, 1000);
            _background.Texture = background;
            _background.Visible = background != null;
            _noImage.Visible = background == null;
            // The file is named so a pack's missing or unreadable image is seen for what it is, not taken for an Atlas fault.
            _noImage.Text = string.IsNullOrEmpty(missingImage) ? Tr("This map has no usable image. Its pins are still shown where they go.")
                : Tr("The map's image \"{0}\" isn't in the pack, or can't be read. Its pins are still shown where they go.").Replace("{0}", missingImage);
            Layout();
        }

        /// <summary>The pins; the controls of the old ones go.</summary>
        public void SetPins(IEnumerable<Pin> pins)
        {
            foreach (var old in _pins)
            {
                if (GodotObject.IsInstanceValid(old.Control))
                {
                    _surface.RemoveChild(old.Control);
                    old.Control.QueueFree();
                }
            }
            _pins.Clear();
            foreach (var pin in pins)
            {
                _pins.Add(pin);
                _surface.AddChild(pin.Control);
            }
            Layout();
        }

        /// <summary>A ring (or any mark) drawn under the pins at a map point; null takes it away.</summary>
        public void SetRing(Vector2 at, Control? ring)
        {
            if (_ring != null && GodotObject.IsInstanceValid(_ring))
            {
                _surface.RemoveChild(_ring);
                _ring.QueueFree();
            }
            _ring = ring;
            _ringAt = at;
            if (ring == null) return;
            ring.MouseFilter = MouseFilterEnum.Ignore;
            _surface.AddChild(ring);
            _surface.MoveChild(ring, 2); // above the image and the note, under the pins
            Layout();
        }

        /// <summary>The size a pin takes on screen at the zoom.</summary>
        public float ScreenSizeOf(Pin pin) => pin.MapSize > 0 ? Math.Max(MinPinScreen, pin.MapSize * _zoom) : FixedPinSize;

        /// <summary>Places the image, the pins and the ring for the zoom.</summary>
        public void Layout()
        {
            _surface.CustomMinimumSize = _mapSize * _zoom;
            _background.Size = _mapSize * _zoom;
            foreach (var pin in _pins)
            {
                if (!GodotObject.IsInstanceValid(pin.Control)) continue;
                float size = ScreenSizeOf(pin);
                // The minimum first: Godot clamps a size to the minimum as it's set, so a pin shrunk by a zoom out kept
                // its old size (big and off its point) until the map was drawn again.
                pin.Control.CustomMinimumSize = new Vector2(size, size);
                pin.Control.Size = new Vector2(size, size);
                pin.Control.Position = new Vector2(pin.X * _zoom - size / 2, pin.Y * _zoom - size / 2);
                pin.Control.PivotOffset = new Vector2(size / 2, size / 2);
                // The border scales with the pin (a fixed border swallowed a zoomed-out pin's colour), never past its middle.
                if (pin.MapBorder > 0 && pin.MapSize > 0 && pin.Control is MapPinButton button)
                    button.ApplyBorderWidth(Math.Clamp((int)Math.Round(pin.MapBorder * (size / pin.MapSize)), 1, Math.Max(1, (int)(size / 2) - 1)));
            }
            if (_ring != null && GodotObject.IsInstanceValid(_ring)) _ring.Position = _ringAt * _zoom - _ring.Size / 2;
        }

        /// <summary>A map point in map pixels from a point on the surface.</summary>
        public Vector2 ToMap(Vector2 surfacePoint) => surfacePoint / _zoom;

        /// <summary>Zooms (within the limits) around a point of the view; the scroll follows a frame later, once the range grew.</summary>
        public void ZoomAt(Vector2 anchorInView, float factor)
        {
            float oldZoom = _zoom;
            float newZoom = Math.Clamp(oldZoom * factor, MinZoom, MaxZoom);
            if (Math.Abs(newZoom - oldZoom) < 0.0001f) return;
            var mapPoint = (ScrollPosition + anchorInView) / oldZoom;
            _zoom = newZoom;
            Layout();
            var target = mapPoint * newZoom - anchorInView;
            Ui.NextFrame(this, () => ScrollPosition = target);
            ViewChanged?.Invoke();
        }

        public void ZoomAtCenter(float factor) => ZoomAt(_scroll.Size / 2, factor);

        /// <summary>Sets the zoom without moving the map point at the middle of the view.</summary>
        public void SetZoom(float zoom) => ZoomAt(_scroll.Size / 2, Math.Clamp(zoom, MinZoom, MaxZoom) / _zoom);

        /// <summary>Shows the whole map, with a little room around it; before the view has a size, it fits once it has.</summary>
        /// <param name="remember">False for Atlas's own fits (the first fit once the view has a size, a remembered view replaced): those aren't remembered as the user's view.</param>
        public void FitToView(float padding = 0.95f, bool remember = true)
        {
            if (!HasView)
            {
                _fitWhenSized = true;
                return;
            }
            _fitWhenSized = false;
            if (!remember) _byCode++;
            try
            {
                _zoom = Math.Clamp(FitZoom(padding), MinZoom, MaxZoom);
                Layout();
                ScrollPosition = Vector2.Zero;
                ViewChanged?.Invoke();
            }
            finally { if (!remember) _byCode--; }
        }

        /// <summary>The zoom at which the whole map fits the view (at most 1), or 0 while the view has no size.</summary>
        public float FitZoom(float padding = 0.95f) =>
            !HasView || _mapSize.X <= 0 || _mapSize.Y <= 0 ? 0 : Math.Min(Math.Min(_scroll.Size.X / _mapSize.X, _scroll.Size.Y / _mapSize.Y) * padding, 1f);

        private int _byCode;

        /// <summary>Whether the view is being moved by Atlas (a fit, a restored view), not the user: such a view isn't remembered.</summary>
        public bool ChangingByCode => _byCode > 0;

        /// <summary>A saved view: the map point at the middle, and the zoom.</summary>
        /// <param name="fitIfSmaller">A remembered view smaller than the whole map's fit (one saved while the view was tiny) shows the fit instead.</param>
        public void SetView(Vector2 center, float zoom, bool fitIfSmaller = false)
        {
            _fitWhenSized = false;
            if (!HasView)
            {
                // No size yet (the map loaded while its tab was hidden): applied once the view has one.
                _viewWhenSized = (center, zoom, fitIfSmaller);
                return;
            }
            _viewWhenSized = null;
            if (fitIfSmaller && zoom < FitZoom() * 0.9f)
            {
                FitToView(remember: false);
                return;
            }
            _byCode++;
            try
            {
                _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
                Layout();
                var target = center * _zoom - _scroll.Size / 2;
                ScrollPosition = target;
                Ui.NextFrame(this, () =>
                {
                    _byCode++;
                    try { ScrollPosition = target; }
                    finally { _byCode--; }
                });
                ViewChanged?.Invoke();
            }
            finally { _byCode--; }
        }

        /// <summary>Centres the view on a map point, zooming in to at least <paramref name="minZoom"/> if given.</summary>
        public void CenterOn(Vector2 point, float? minZoom = null)
        {
            SetView(point, minZoom is { } z && _zoom < z ? z : _zoom);
        }

        /// <summary>Handles the wheel over a pin as over the map (a pin's own input handler calls it).</summary>
        public bool HandleWheel(InputEvent @event, Control over)
        {
            if (@event is not InputEventMouseButton wheel || !wheel.Pressed || (wheel.ButtonIndex != MouseButton.WheelUp && wheel.ButtonIndex != MouseButton.WheelDown)) return false;
            ZoomAt(over.GlobalPosition + wheel.Position - _scroll.GlobalPosition, wheel.ButtonIndex == MouseButton.WheelUp ? WheelStep : 1 / WheelStep);
            return true;
        }

        private void OnSurfaceInput(InputEvent @event)
        {
            switch (@event)
            {
                case InputEventMouseButton wheel when wheel.Pressed && (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown):
                    ZoomAt(wheel.Position - ScrollPosition, wheel.ButtonIndex == MouseButton.WheelUp ? WheelStep : 1 / WheelStep);
                    _surface.AcceptEvent();
                    break;
                case InputEventMouseButton press when press.Pressed && press.ButtonIndex is MouseButton.Left or MouseButton.Right or MouseButton.Middle:
                    _dragging = true;
                    _dragMoved = false;
                    _dragLast = press.GlobalPosition;
                    _dragStart = press.Position;
                    _surface.AcceptEvent();
                    break;
                case InputEventMouseButton release when !release.Pressed && _dragging && release.ButtonIndex is MouseButton.Left or MouseButton.Right or MouseButton.Middle:
                    _dragging = false;
                    if (!_dragMoved && release.ButtonIndex == MouseButton.Left) Clicked?.Invoke(ToMap(_dragStart));
                    _surface.AcceptEvent();
                    break;
                case InputEventMouseMotion motion when _dragging:
                    var delta = motion.GlobalPosition - _dragLast;
                    _dragLast = motion.GlobalPosition;
                    if (delta.Length() > 0) _dragMoved = _dragMoved || (motion.Position - _dragStart).Length() >= 3;
                    ScrollPosition -= delta;
                    _surface.AcceptEvent();
                    break;
            }
        }
    }
}
