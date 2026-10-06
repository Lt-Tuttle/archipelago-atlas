using System;
using System.Globalization;
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
    /// The longest run of text without a place to break a line (a space, a zero-width space) that rich text gets: a longer
    /// one gets a zero-width space every this many characters. Godot breaks a run too wide for its line one character at
    /// a time, in time that grows with the square of the run: in a narrow column (a text client that isn't showing, a
    /// small window) 2,000 characters without a space held up the window for 3 seconds, and 30,000 for 5 minutes. No
    /// word comes near this; a long link gets breaks it doesn't show.
    /// </summary>
    public const int MaxRun = 64;

    /// <summary>
    /// BBCode with every tag but Atlas's own made inert, and no run of text too long to lay out (<see cref="MaxRun"/>).
    /// Atlas's own tags: b, i, u, s and code, color, bgcolor and url (each with or without "="), and lb and rb. Any other
    /// "[" becomes "[lb]", so what follows shows as text: a tag Godot would load a file for, a size or a table, or a tag a
    /// later Godot adds. It takes linear time, whatever the text.
    /// </summary>
    public static string Safe(string? bbcode)
    {
        if (string.IsNullOrEmpty(bbcode)) return "";
        StringBuilder? safe = null;
        int copied = 0, run = 0;
        // Godot takes a tag to run from "[" to the next "]": the next "]" after the last one found serves every "[" before it.
        int close = -1;
        for (int i = 0; i < bbcode.Length; i++)
        {
            char c = bbcode[i];
            if (c == '[')
            {
                if (close != int.MaxValue && close < i)
                {
                    close = bbcode.IndexOf(']', i + 1);
                    if (close < 0) close = int.MaxValue; // no "]" after this point: no more tags
                }
                if (close != int.MaxValue && OwnTag().IsMatch(bbcode.AsSpan(i + 1, close - i - 1)))
                {
                    // An own tag shows nothing, except [lb] and [rb], which show one bracket.
                    if (close - i == 3 && bbcode[i + 2] == 'b' && (bbcode[i + 1] == 'l' || bbcode[i + 1] == 'r')) Shown(i);
                    i = close;
                    continue;
                }
                Shown(i);
                safe ??= new StringBuilder(bbcode.Length + 16);
                safe.Append(bbcode, copied, i - copied).Append("[lb]");
                copied = i + 1;
            }
            else if (char.IsWhiteSpace(c) || c == ZeroWidthSpace) run = 0;
            // A low surrogate or a combining mark belongs to the character before it: it neither counts nor takes a break.
            else if (!char.IsLowSurrogate(c) && CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                Shown(i);
        }
        return safe == null ? bbcode : safe.Append(bbcode, copied, bbcode.Length - copied).ToString();

        // A character shown from position "at" on: once the run is full, a zero-width space goes before it.
        void Shown(int at)
        {
            if (run >= MaxRun)
            {
                safe ??= new StringBuilder(bbcode.Length + 16);
                safe.Append(bbcode, copied, at - copied).Append(ZeroWidthSpace);
                copied = at;
                run = 0;
            }
            run++;
        }
    }

    private const char ZeroWidthSpace = '\u200B';

    [GeneratedRegex(@"^(?:/?(?:b|i|u|s|code|color|bgcolor|url)|(?:color|bgcolor|url)=[^\[\]]*|lb|rb)$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnTag();
}
