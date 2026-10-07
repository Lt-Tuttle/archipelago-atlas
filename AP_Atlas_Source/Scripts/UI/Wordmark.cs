using System.Collections.Generic;
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The wordmark as a texture, in the theme's text colours and the accent (its scale bar), drawn by Godot's SVG
    /// renderer at the height asked for (twice that, for sharpness on high-DPI screens). Kept per theme, accent and
    /// height; a page shows it in a <see cref="TextureRect"/> and asks again after the accent or the theme changed.
    /// </summary>
    public static class Wordmark
    {
        private static readonly Dictionary<string, ImageTexture> _cache = new();

        /// <summary>The wordmark <paramref name="height"/> pixels tall (its width follows), for the palette and accent in use.</summary>
        public static Texture2D Texture(float height)
        {
            bool dark = ThemeColors.Current.IsDark;
            string key = $"{ThemeColors.Current.Name}|{ThemeColors.Accent.ToHtml(false)}|{height}";
            if (_cache.TryGetValue(key, out var kept) && GodotObject.IsInstanceValid(kept)) return kept;
            string svg = WordmarkSvg.For(dark, "#" + ThemeColors.Text.ToHtml(false), "#" + ThemeColors.TextSubtle.ToHtml(false), "#" + ThemeColors.Heading.ToHtml(false));
            var image = new Image();
            image.LoadSvgFromString(svg, 2f * height / WordmarkSvg.Height);
            var texture = ImageTexture.CreateFromImage(image);
            _cache[key] = texture;
            return texture;
        }

        /// <summary>A control showing the wordmark at a height; <see cref="Refresh"/> redraws it for a new accent or theme.</summary>
        public static TextureRect Make(float height, string accessibleName)
        {
            var rect = new TextureRect
            {
                Name = "Wordmark",
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(height * WordmarkSvg.Width / WordmarkSvg.Height, height),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
                AccessibilityName = accessibleName
            };
            rect.SetMeta("wordmark_height", height);
            Refresh(rect);
            return rect;
        }

        public static void Refresh(TextureRect rect)
        {
            if (!GodotObject.IsInstanceValid(rect)) return;
            rect.Texture = Texture((float)rect.GetMeta("wordmark_height").AsDouble());
        }
    }
}
