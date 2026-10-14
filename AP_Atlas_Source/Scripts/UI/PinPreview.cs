#nullable enable
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>A map's picture fitted into a small box with one pin marked on it (the Pack Doctor's one-by-one review).</summary>
    public partial class PinPreview : Control
    {
        private Texture2D? _map;
        private Vector2 _mapSize;
        private Vector2 _pin;

        public PinPreview()
        {
            CustomMinimumSize = new Vector2(360, 220);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        /// <summary>Shows a map (its picture and size in map pixels) and where the pin sits on it.</summary>
        public void Show(Texture2D? map, Vector2 mapSize, Vector2 pin)
        {
            _map = map;
            _mapSize = mapSize.X > 0 && mapSize.Y > 0 ? mapSize : map?.GetSize() ?? new Vector2(1, 1);
            _pin = pin;
            QueueRedraw();
        }

        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, Size), ThemeColors.SurfaceDeep);
            if (_map == null || _mapSize.X <= 0 || _mapSize.Y <= 0) return;
            float scale = Mathf.Min(Size.X / _mapSize.X, Size.Y / _mapSize.Y);
            var drawn = _mapSize * scale;
            var origin = (Size - drawn) / 2;
            DrawTextureRect(_map, new Rect2(origin, drawn), false);
            var at = origin + _pin * scale;
            // A ring with a dark outline reads on light and dark maps alike.
            DrawCircle(at, 9, new Color(0, 0, 0, 0.55f));
            DrawArc(at, 8, 0, Mathf.Tau, 32, ThemeColors.MapPinFocus, 3, true);
            DrawCircle(at, 3, ThemeColors.MapPinFocus);
        }
    }
}
