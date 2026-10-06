using System;
using System.Linq;

namespace AP_Atlas.Core;

/// <summary>
/// Finding things by typed words, the way the command palette and the Settings page do: every typed word starts a
/// word of the thing's texts (its title, its menu or section, its description). Nothing typed matches everything.
/// </summary>
public static class WordSearch
{
    private static readonly char[] Separators = { ' ', '\t', '\r', '\n', '-', '/', '&', '…', '.', '·' };
    private static readonly char[] Trimmed = { '(', ')', ',', ':', ';', '?', '!', '"', '\'' };

    /// <summary>The words of a text: split at spaces and the punctuation that joins words, with the punctuation around them removed.</summary>
    public static string[] Words(string? text) =>
        text == null
            ? Array.Empty<string>()
            : text.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim(Trimmed)).Where(w => w.Length > 0).ToArray();

    /// <summary>Whether every typed word starts a word of one of the texts (case doesn't matter); nothing typed matches.</summary>
    public static bool Matches(string[] typed, params string?[] texts)
    {
        if (typed.Length == 0) return true;
        var words = texts.SelectMany(Words).ToList();
        return typed.All(t => words.Any(w => w.StartsWith(t, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool Matches(string? typedText, params string?[] texts) => Matches(Words(typedText), texts);
}
