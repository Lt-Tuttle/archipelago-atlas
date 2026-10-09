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

        public override void _Draw()
        {
            if (SplitRight is { } right) DrawRightHalf(this, Size, Border, Shape, right);
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
