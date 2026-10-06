using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core;

/// <summary>
/// Markdown, as Atlas's own documents are written, turned into the BBCode its rich text reads: Atlas's own tags only
/// (b, i, code, color, url; see <see cref="Bbcode.Safe"/>). Headings, paragraphs, bullet and numbered lists, quotes, code
/// blocks, tables (as rows of cells) and rules; bold, italic, code and links. A link is a link only to an https page
/// (any other shows its text alone), and every "[" in the text is escaped, so a document can't carry a tag of its own.
/// </summary>
public static partial class Markdown
{
    /// <summary>The colour headings wear when none is given.</summary>
    public const string DefaultHeadingColor = "#B388FF";

    private const string Rule = "────────────────────────────";

    /// <summary>The document as BBCode.</summary>
    public static string ToBbcode(string? markdown, string? headingColor = null)
    {
        string color = headingColor != null && HeadingColor().IsMatch(headingColor) ? headingColor : DefaultHeadingColor;
        var output = new StringBuilder();
        var paragraph = new List<string>();
        var table = new List<string[]>();
        bool inCode = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            output.Append(Inline(string.Join(" ", paragraph))).Append("\n\n");
            paragraph.Clear();
        }

        void FlushTable()
        {
            if (table.Count == 0) return;
            for (int i = 0; i < table.Count; i++)
            {
                string row = string.Join("  ·  ", Array.ConvertAll(table[i], Inline));
                output.Append(i == 0 ? "[b]" + row + "[/b]" : row).Append('\n');
            }
            output.Append('\n');
            table.Clear();
        }

        foreach (string raw in (markdown ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushTable();
                output.Append(inCode ? "[/code]\n\n" : "[code]");
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                output.Append(Bbcode.Escape(line)).Append('\n');
                continue;
            }
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                FlushTable();
                continue;
            }
            if (trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (!TableRule().IsMatch(trimmed)) table.Add(Cells(trimmed));
                continue;
            }
            FlushTable();
            var heading = Heading().Match(trimmed);
            if (heading.Success)
            {
                FlushParagraph();
                string text = Inline(heading.Groups[2].Value);
                if (output.Length > 0 && !output.ToString().EndsWith("\n\n", StringComparison.Ordinal)) output.Append('\n');
                output.Append(heading.Groups[1].Value.Length <= 2 ? $"[b][color={color}]{text}[/color][/b]" : $"[b]{text}[/b]").Append("\n\n");
                continue;
            }
            if (RuleLine().IsMatch(trimmed))
            {
                FlushParagraph();
                output.Append(Rule).Append("\n\n");
                continue;
            }
            var bullet = Bullet().Match(line);
            if (bullet.Success)
            {
                FlushParagraph();
                int depth = bullet.Groups[1].Value.Length / 2;
                output.Append(' ', 2 + depth * 4).Append(depth == 0 ? "• " : "– ").Append(Inline(bullet.Groups[2].Value)).Append('\n');
                continue;
            }
            var numbered = Numbered().Match(line);
            if (numbered.Success)
            {
                FlushParagraph();
                int depth = numbered.Groups[1].Value.Length / 2;
                output.Append(' ', 2 + depth * 4).Append(numbered.Groups[2].Value).Append(". ").Append(Inline(numbered.Groups[3].Value)).Append('\n');
                continue;
            }
            var quote = Quote().Match(trimmed);
            if (quote.Success)
            {
                FlushParagraph();
                output.Append("[i]").Append(Inline(quote.Groups[1].Value)).Append("[/i]\n");
                continue;
            }
            paragraph.Add(trimmed);
        }
        FlushParagraph();
        FlushTable();
        if (inCode) output.Append("[/code]\n");
        return output.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// A line's text as BBCode: bold (**), italic (*), code (`) and links ([text](https://…)); "&lt;br&gt;" is a line
    /// break and other HTML tags are dropped; every "[" that isn't a link is escaped.
    /// </summary>
    public static string Inline(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var output = new StringBuilder(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    output.Append("[code]").Append(Bbcode.Escape(text.Substring(i + 1, end - i - 1))).Append("[/code]");
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    output.Append("[b]").Append(Inline(text.Substring(i + 2, end - i - 2))).Append("[/b]");
                    i = end + 2;
                    continue;
                }
            }
            else if (c == '*')
            {
                int end = text.IndexOf('*', i + 1);
                if (end > i + 1 && !char.IsWhiteSpace(text[i + 1]))
                {
                    output.Append("[i]").Append(Inline(text.Substring(i + 1, end - i - 1))).Append("[/i]");
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '[')
            {
                var link = Link().Match(text, i);
                if (link.Success && link.Index == i)
                {
                    string url = link.Groups[2].Value;
                    string shown = Inline(link.Groups[1].Value);
                    output.Append(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? $"[url={url}]{shown}[/url]" : shown);
                    i = link.Index + link.Length;
                    continue;
                }
                output.Append("[lb]");
                i++;
                continue;
            }
            else if (c == '<')
            {
                var tag = HtmlTag().Match(text, i);
                if (tag.Success && tag.Index == i)
                {
                    if (tag.Value.StartsWith("<br", StringComparison.OrdinalIgnoreCase)) output.Append('\n');
                    i = tag.Index + tag.Length;
                    continue;
                }
            }
            output.Append(c);
            i++;
        }
        return output.ToString();
    }

    /// <summary>The document's sections at a heading level (2 for "## "): each heading's text and what follows it until the next heading of that level or above.</summary>
    public static IReadOnlyList<(string Title, string Body)> Sections(string? markdown, int level = 2)
    {
        var sections = new List<(string, string)>();
        string? title = null;
        var body = new StringBuilder();
        bool inCode = false;
        foreach (string raw in (markdown ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal)) inCode = !inCode;
            var heading = inCode ? Match.Empty : Heading().Match(raw.Trim());
            if (heading.Success && heading.Groups[1].Value.Length <= level)
            {
                if (title != null) sections.Add((title, body.ToString().Trim('\n')));
                body.Clear();
                title = heading.Groups[1].Value.Length == level ? Plain(heading.Groups[2].Value) : null;
                continue;
            }
            if (title != null) body.Append(raw).Append('\n');
        }
        if (title != null) sections.Add((title, body.ToString().Trim('\n')));
        return sections;
    }

    /// <summary>A line's text with its markup removed (for a title or a list entry).</summary>
    public static string Plain(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string plain = Link().Replace(text, "$1");
        plain = HtmlTag().Replace(plain, " ");
        return plain.Replace("**", "").Replace("`", "").Trim();
    }

    private static string[] Cells(string row)
    {
        string inner = row.Trim();
        if (inner.StartsWith("|", StringComparison.Ordinal)) inner = inner.Substring(1);
        if (inner.EndsWith("|", StringComparison.Ordinal)) inner = inner.Substring(0, inner.Length - 1);
        var cells = inner.Split('|');
        for (int i = 0; i < cells.Length; i++) cells[i] = cells[i].Trim();
        return cells;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*$", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\s*)(\d{1,3})[.)]\s+(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^>\s?(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$", RegexOptions.CultureInvariant)]
    private static partial Regex TableRule();

    [GeneratedRegex(@"\[([^\[\]]+)\]\(([^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"</?[a-zA-Z][a-zA-Z0-9]*(\s[^<>]*)?/?>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingColor();
}
