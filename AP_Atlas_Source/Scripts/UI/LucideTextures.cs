using System.Collections.Generic;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>Lucide icons (<see cref="LucideIcons"/>) as textures, coloured as they're loaded; one texture per icon, colour and size, kept.</summary>
    public static class LucideTextures
    {
        private static readonly Dictionary<string, Texture2D> Loaded = new();

        /// <summary>The icon as a texture, or null for a name Atlas doesn't ship.</summary>
        public static Texture2D? Get(string name, Color color, float scale = 1.5f)
        {
            string key = name + "|" + color.ToHtml(true) + "|" + scale;
            if (Loaded.TryGetValue(key, out var texture)) return texture;
            string? svg = LucideIcons.Svg(name, "#" + color.ToHtml(false));
            if (svg == null) return null;
            var image = new Image();
            image.LoadSvgFromString(svg, scale);
            texture = ImageTexture.CreateFromImage(image);
            Loaded[key] = texture;
            return texture;
        }
    }
}
