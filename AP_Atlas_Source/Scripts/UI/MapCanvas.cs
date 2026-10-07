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
            /// <summary>The pin's size in map pixels; 0 for <see cref="FixedPinSize"/> on screen whatever the zoom.</summary>
            public float MapSize;
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
            _noImage = new Label { Text = "This map has no usable image. Its pins are still shown where they go.", Position = new Vector2(10, 10), MouseFilter = MouseFilterEnum.Ignore, Visible = false };
            _noImage.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.TextMuted);
            _surface.AddChild(_noImage);
            _scroll.Resized += () =>
            {
                if (_fitWhenSized && HasView) FitToView();
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
        public void SetMap(Texture2D? background, Vector2 size)
        {
            _mapSize = size.X > 0 && size.Y > 0 ? size : new Vector2(1600, 1000);
            _background.Texture = background;
            _background.Visible = background != null;
            _noImage.Visible = background == null;
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
                pin.Control.Size = new Vector2(size, size);
                pin.Control.CustomMinimumSize = new Vector2(size, size);
                pin.Control.Position = new Vector2(pin.X * _zoom - size / 2, pin.Y * _zoom - size / 2);
                pin.Control.PivotOffset = new Vector2(size / 2, size / 2);
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
        public void FitToView(float padding = 0.95f)
        {
            if (!HasView)
            {
                _fitWhenSized = true;
                return;
            }
            _fitWhenSized = false;
            var view = _scroll.Size;
            float fit = Math.Min(view.X / _mapSize.X, view.Y / _mapSize.Y) * padding;
            _zoom = Math.Clamp(Math.Min(fit, 1f), MinZoom, MaxZoom);
            Layout();
            ScrollPosition = Vector2.Zero;
            ViewChanged?.Invoke();
        }

        /// <summary>A saved view: the map point at the middle, and the zoom.</summary>
        public void SetView(Vector2 center, float zoom)
        {
            _fitWhenSized = false;
            _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
            Layout();
            var target = center * _zoom - _scroll.Size / 2;
            ScrollPosition = target;
            Ui.NextFrame(this, () => ScrollPosition = target);
            ViewChanged?.Invoke();
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
