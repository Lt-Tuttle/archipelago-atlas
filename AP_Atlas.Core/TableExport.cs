using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AP_Atlas.Core;

/// <summary>The ways a table's rows leave Atlas.</summary>
public enum ExportFormat
{
    /// <summary>Tab-separated, for a spreadsheet's paste.</summary>
    Tsv,
    /// <summary>Comma-separated (RFC 4180: quoted where needed), for a file.</summary>
    Csv,
    /// <summary>A Markdown table, for GitHub and most chat.</summary>
    Markdown,
    /// <summary>Discord has no tables: columns lined up in code blocks, each short enough for one message.</summary>
    Discord
}

/// <summary>
/// Turns a table's rows (the headers, then each row's cells as text) into text to copy or save. Cells are plain text;
/// a line break or a tab inside one becomes a space, so a cell never breaks the row.
/// </summary>
public static class TableExport
{
    /// <summary>Discord's longest message.</summary>
    public const int DiscordMessageLimit = 2000;

    /// <summary>The widest a column is drawn for Discord before its cells are cut.</summary>
    public const int DiscordColumnLimit = 40;

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Tsv => "tsv",
        ExportFormat.Csv => "csv",
        ExportFormat.Markdown => "md",
        _ => "txt"
    };

    /// <summary>The rows as one text (for Discord, the parts joined by a blank line; <see cref="Discord"/> gives them apart).</summary>
    public static string Text(ExportFormat format, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows) => format switch
    {
        ExportFormat.Tsv => Tsv(headers, rows),
        ExportFormat.Csv => Csv(headers, rows),
        ExportFormat.Markdown => Markdown(headers, rows),
        _ => string.Join("\n\n", Discord(headers, rows))
    };

    public static string Tsv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var text = new StringBuilder();
        text.Append(string.Join("\t", headers.Select(Flat))).Append('\n');
        foreach (var row in rows) text.Append(string.Join("\t", Cells(row, headers.Count).Select(Flat))).Append('\n');
        return text.ToString();
    }

    /// <summary>RFC 4180: a cell with a comma, a quote or a line break is quoted, with quotes doubled; lines end in CRLF.</summary>
    public static string Csv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var text = new StringBuilder();
        text.Append(string.Join(",", headers.Select(CsvCell))).Append("\r\n");
        foreach (var row in rows) text.Append(string.Join(",", Cells(row, headers.Count).Select(CsvCell))).Append("\r\n");
        return text.ToString();
    }

    public static string Markdown(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var text = new StringBuilder();
        text.Append("| ").Append(string.Join(" | ", headers.Select(MarkdownCell))).Append(" |\n");
        text.Append("|").Append(string.Concat(Enumerable.Repeat("---|", Math.Max(1, headers.Count)))).Append('\n');
        foreach (var row in rows) text.Append("| ").Append(string.Join(" | ", Cells(row, headers.Count).Select(MarkdownCell))).Append(" |\n");
        return text.ToString();
    }

    /// <summary>
    /// Code blocks with the columns lined up (a monospace font keeps them so), each at most <paramref name="limit"/>
    /// characters with its fences, the headers repeated in each; a cell wider than <see cref="DiscordColumnLimit"/> is cut.
    /// </summary>
    public static List<string> Discord(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, int limit = DiscordMessageLimit)
    {
        var all = rows.Select(row => Cells(row, headers.Count).Select(cell => Cut(Flat(cell), DiscordColumnLimit)).ToArray()).ToList();
        var heads = headers.Select(h => Cut(Flat(h), DiscordColumnLimit)).ToArray();
        var widths = new int[headers.Count];
        for (int i = 0; i < widths.Length; i++)
            widths[i] = Math.Max(heads[i].Length, all.Count == 0 ? 0 : all.Max(row => row[i].Length));
        string Line(string[] cells) => string.Join("  ", cells.Select((cell, i) => i == cells.Length - 1 ? cell : cell.PadRight(widths[i]))).TrimEnd();
        string header = Line(heads) + "\n" + string.Join("  ", widths.Select(w => new string('-', w))).TrimEnd() + "\n";
        const string open = "```\n", close = "```";
        var parts = new List<string>();
        var part = new StringBuilder(open + header);
        foreach (var row in all)
        {
            string line = Line(row) + "\n";
            if (part.Length + line.Length + close.Length > limit && part.Length > open.Length + header.Length)
            {
                parts.Add(part.ToString() + close);
                part = new StringBuilder(open + header);
            }
            part.Append(line);
        }
        parts.Add(part.ToString() + close);
        return parts;
    }

    private static IReadOnlyList<string> Cells(IReadOnlyList<string> row, int count)
    {
        if (row.Count == count) return row;
        var cells = new string[count];
        for (int i = 0; i < count; i++) cells[i] = i < row.Count ? row[i] : "";
        return cells;
    }

    /// <summary>A cell on one line: line breaks and tabs become spaces.</summary>
    private static string Flat(string? cell) => (cell ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();

    private static string CsvCell(string? cell)
    {
        string text = cell ?? "";
        if (text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static string MarkdownCell(string? cell) => Flat(cell).Replace("|", "\\|");

    private static string Cut(string text, int most) => text.Length <= most ? text : text.Substring(0, most - 1) + "…";
}
