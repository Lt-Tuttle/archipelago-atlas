using System;

namespace AP_Atlas.Core;

/// <summary>
/// The wordmark ("The Archipelago Atlas" in Cormorant SC small capitals, outlined, with a scale bar above and hairlines
/// beside "Atlas"), as SVG text with its three colours swapped for the theme's: the text, the quiet lines, and the bar's
/// filled segments in the accent. The two files in docs/wordmark are the templates; their own colours are the keys.
/// </summary>
public static class WordmarkSvg
{
    /// <summary>The template's own colours: text, lines, accent; the dark file's and the light file's.</summary>
    private static readonly (string Text, string Soft, string Accent) DarkKeys = ("#F3EFE6", "#A9A3B8", "#B98AF0");
    private static readonly (string Text, string Soft, string Accent) LightKeys = ("#201B33", "#6B6480", "#8A2BE2");

    /// <summary>The wordmark's drawing size, in SVG units (its viewBox).</summary>
    public const float Width = 1300f, Height = 370f;

    /// <summary>The SVG for a theme: the dark or light template, recoloured. Hexes are "#RRGGBB".</summary>
    public static string For(bool dark, string textHex, string softHex, string accentHex)
    {
        var keys = dark ? DarkKeys : LightKeys;
        string svg = Docs.Read(dark ? "wordmark-dark.svg" : "wordmark-light.svg");
        // The keys are swapped through stand-ins first, so a theme colour that equals another key isn't swapped twice.
        return svg
            .Replace(keys.Text, "\u0001T", StringComparison.OrdinalIgnoreCase)
            .Replace(keys.Soft, "\u0001S", StringComparison.OrdinalIgnoreCase)
            .Replace(keys.Accent, "\u0001A", StringComparison.OrdinalIgnoreCase)
            .Replace("\u0001T", Hex(textHex))
            .Replace("\u0001S", Hex(softHex))
            .Replace("\u0001A", Hex(accentHex));
    }

    private static string Hex(string hex)
    {
        if (string.IsNullOrEmpty(hex)) throw new ArgumentException("a colour is needed", nameof(hex));
        return hex.StartsWith('#') ? hex : "#" + hex;
    }
}
