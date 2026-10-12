using System;
using System.Text;

namespace AP_Atlas.Core;

/// <summary>
/// Lua source text, read without running it: for looking through a map pack's scripts (which options they read, say).
/// Linear in the text's length, whatever the text: a pack's scripts come from outside Atlas, and a regular expression
/// that backtracks could take hours on one crafted file.
/// </summary>
public static class LuaText
{
    /// <summary>The longest run of "=" a block comment's brackets are taken to have. Real scripts use none to three.</summary>
    public const int MaxCommentLevel = 32;

    /// <summary>
    /// The text without its comments. A block comment (--[[ … ]], --[==[ … ]==]) leaves its line breaks, so lines keep
    /// their numbers; a line comment goes to the end of its line. A block comment that's never closed, or whose level is
    /// over <see cref="MaxCommentLevel"/>, is taken as a line comment. Strings aren't told apart: "--" in one starts a
    /// comment here, which is close enough for looking through scripts.
    /// </summary>
    /// <summary>
    /// Lua 5.3's \u{XXXX} escapes written out as the characters they name (an invalid code point is left as written).
    /// MoonSharp compiles Lua 5.2, which has no such escape: a pack's "Siegbr\u{00e4}u" would otherwise end up as
    /// "Siegbru{00e4}u". The sequence can't occur in Lua 5.2 code outside a string, so the whole text is safe to expand.
    /// </summary>
    public static string ExpandUnicodeEscapes(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("\\u{", StringComparison.Ordinal)) return text;
        return System.Text.RegularExpressions.Regex.Replace(text, @"\\u\{([0-9A-Fa-f]{1,8})\}", m =>
        {
            if (!int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int point)) return m.Value;
            if (point < 0 || point > 0x10FFFF || (point >= 0xD800 && point <= 0xDFFF)) return m.Value;
            return char.ConvertFromUtf32(point);
        });
    }

    public static string StripComments(string text)
    {
        var result = new StringBuilder(text.Length);
        // Levels already known to have no closing bracket from some point on: as the scan only moves forward, none later
        // either, so each level is searched to the end at most once.
        var unclosed = new bool[MaxCommentLevel + 1];
        int i = 0;
        while (i < text.Length)
        {
            int dash = text.IndexOf("--", i, StringComparison.Ordinal);
            if (dash < 0)
            {
                result.Append(text, i, text.Length - i);
                break;
            }
            result.Append(text, i, dash - i);
            int level = BlockLevel(text, dash + 2);
            if (level >= 0 && !unclosed[level])
            {
                string close = "]" + new string('=', level) + "]";
                int end = text.IndexOf(close, dash + 4 + level, StringComparison.Ordinal);
                if (end >= 0)
                {
                    for (int c = dash; c < end; c++)
                        if (text[c] == '\n') result.Append('\n');
                    i = end + close.Length;
                    continue;
                }
                unclosed[level] = true;
            }
            int newline = text.IndexOf('\n', dash);
            i = newline < 0 ? text.Length : newline;
        }
        return result.ToString();
    }

    // The level of a block comment's opening bracket at this position ("[[" is 0, "[==[" is 2), or -1 if there isn't one.
    private static int BlockLevel(string text, int at)
    {
        if (at >= text.Length || text[at] != '[') return -1;
        int level = 0;
        while (at + 1 + level < text.Length && text[at + 1 + level] == '=' && level <= MaxCommentLevel) level++;
        return level <= MaxCommentLevel && at + 1 + level < text.Length && text[at + 1 + level] == '[' ? level : -1;
    }
}
