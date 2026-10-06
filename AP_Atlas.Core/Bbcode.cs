using System;
using System.Text;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core;

/// <summary>
/// BBCode, the markup Godot's rich text reads, made safe for text from outside Atlas: a pack's or an apworld's names, a
/// server's or another player's messages, a site's data, a zip's file names.
/// <list type="bullet">
/// <item><see cref="Escape"/> every piece of outside text put into BBCode, so it shows exactly as written, with no colours,
/// sizes or links of its own.</item>
/// <item><see cref="Safe"/> all BBCode before Godot reads it (SafeRichText does), so only the tags Atlas uses can work,
/// even where outside text wasn't escaped. Godot reads the paths in [img], [font] and [dropcap] with its resource
/// loader, which opens files next to the path first (path.remap, checked by a self-test): a network path there would
/// connect to the computer the text names and offer it the user's Windows sign-in.</item>
/// </list>
/// </summary>
public static partial class Bbcode
{
    /// <summary>Outside text, made to show as written: every "[" becomes the "[lb]" tag, so no tag can start.</summary>
    public static string Escape(string? text) => (text ?? "").Replace("[", "[lb]");

    /// <summary>
    /// Text shown as written, in a colour: a name ("orange") or a hex code ("#8A2BE2"). A colour that is neither is left
    /// out, so a colour from somewhere else can't close the tag early and start one of its own.
    /// </summary>
    public static string Colored(string? text, string? color) =>
        color != null && ColorName().IsMatch(color) ? $"[color={color}]{Escape(text)}[/color]" : Escape(text);

    [GeneratedRegex("^#?[A-Za-z0-9]{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorName();

    /// <summary>
    /// BBCode with every tag but Atlas's own made inert. Atlas's own: b, i, u, s and code, color, bgcolor and url (each
    /// with or without "="), and lb and rb. Any other "[" becomes "[lb]", so what follows shows as text: a tag Godot
    /// would load a file for, a size or a table, or a tag a later Godot adds.
    /// </summary>
    public static string Safe(string? bbcode)
    {
        if (string.IsNullOrEmpty(bbcode)) return "";
        StringBuilder? safe = null;
        int copied = 0;
        for (int open = bbcode.IndexOf('['); open >= 0; open = bbcode.IndexOf('[', open + 1))
        {
            // Godot takes a tag to run from "[" to the next "]".
            int close = bbcode.IndexOf(']', open + 1);
            if (close >= 0 && OwnTag().IsMatch(bbcode.AsSpan(open + 1, close - open - 1))) continue;
            safe ??= new StringBuilder(bbcode.Length + 16);
            safe.Append(bbcode, copied, open - copied).Append("[lb]");
            copied = open + 1;
        }
        return safe == null ? bbcode : safe.Append(bbcode, copied, bbcode.Length - copied).ToString();
    }

    [GeneratedRegex(@"^(?:/?(?:b|i|u|s|code|color|bgcolor|url)|(?:color|bgcolor|url)=[^\[\]]*|lb|rb)$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnTag();
}
