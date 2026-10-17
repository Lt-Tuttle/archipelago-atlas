#nullable enable
using System;
using System.Collections.Generic;
using AP_Atlas.Core.Maps;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// A map pin: a button styled by its state's colour, which can be split down the middle when some of its checks are
    /// in logic and some aren't: the left half keeps the pin's own colour (in logic), the right half is painted in the
    /// out-of-logic colour, inside the border, for the pin's shape. The legend's swatch for that state is one of these too.
    /// </summary>
    public partial class MapPinButton : Button
    {
        /// <summary>The right half's colour, or null for a pin of one colour.</summary>
        public Color? SplitRight { get; set; }

        /// <summary>The pin's shape (the right half follows it).</summary>
        public MapPinShape Shape { get; set; } = MapPinShape.Round;

        /// <summary>The border's width, left alone by the paint.</summary>
        public float Border { get; set; } = MapPinGeometry.DefaultBorder;

        /// <summary>The pin holds a priority check of the seed: a ★ in its middle.</summary>
        public bool Priority { get; set; }

        public override void _Draw()
        {
            if (SplitRight is { } right) DrawRightHalf(this, Size, Border, Shape, right);
            if (Priority) DrawPriorityStar(this, Size, Rotation);
        }

        /// <summary>A small ★ in the pin's middle, upright however the pin is turned (a diamond is a square turned 45°).</summary>
        public static void DrawPriorityStar(CanvasItem item, Vector2 size, float turn)
        {
            float s = Math.Min(size.X, size.Y);
            if (s <= 0) return;
            item.DrawSetTransform(new Vector2(s / 2, s / 2), -turn, Vector2.One);
            var star = AP_Atlas.Core.Annotations.StarCorners(0, 0, s * 0.36f);
            item.DrawColoredPolygon(star, AP_Atlas.Core.Annotations.PriorityColor);
            var outline = new Vector2[star.Length + 1];
            star.CopyTo(outline, 0);
            outline[star.Length] = star[0];
            item.DrawPolyline(outline, AP_Atlas.Core.ThemeColors.MapPinBorder, 1f, true);
            item.DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        }

        /// <summary>Sets the border's width on every stylebox the pin wears (the canvas scales it with the zoom).</summary>
        public void ApplyBorderWidth(int width)
        {
            if ((int)Border == width) return;
            Border = width;
            foreach (string name in new[] { "normal", "hover", "pressed", "focus" })
                if (HasThemeStyleboxOverride(name) && GetThemeStylebox(name) is StyleBoxFlat style)
                    style.BorderWidthTop = style.BorderWidthBottom = style.BorderWidthLeft = style.BorderWidthRight = width;
            QueueRedraw();
        }

        /// <summary>
        /// Paints the right half of a pin's face (what's inside the border) in a colour: the right half of the disc, the
        /// right half of the square, or, for a diamond (a square turned on its corner), the half that shows to the right.
        /// </summary>
        public static void DrawRightHalf(CanvasItem item, Vector2 size, float border, MapPinShape shape, Color colour)
        {
            float s = Math.Min(size.X, size.Y);
            if (s <= 0) return;
            float b = Math.Clamp(border, 0f, s / 2 - 0.5f);
            switch (shape)
            {
                case MapPinShape.Square:
                    item.DrawRect(new Rect2(s / 2, b, s / 2 - b, s - 2 * b), colour);
                    break;
                case MapPinShape.Diamond:
                    // The control is rotated 45° about its centre: the local triangle above the diagonal is the half on the screen's right.
                    item.DrawColoredPolygon(new[] { new Vector2(b, b), new Vector2(s - b, b), new Vector2(s - b, s - b) }, colour);
                    break;
                default:
                    var centre = new Vector2(s / 2, s / 2);
                    float r = s / 2 - b;
                    var points = new List<Vector2> { centre };
                    for (int degrees = -90; degrees <= 90; degrees += 10)
                    {
                        float a = Mathf.DegToRad(degrees);
                        points.Add(centre + new Vector2(r * Mathf.Cos(a), r * Mathf.Sin(a)));
                    }
                    item.DrawColoredPolygon(points.ToArray(), colour);
                    break;
            }
        }
    }
}
