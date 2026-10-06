using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core;
using Xunit;

namespace AP_Atlas.Core.Tests;

public class TableSortTests
{
    private static int N(string? a, string? b) => Math.Sign(TableSort.NaturalCompare(a, b));

    [Fact]
    public void Natural_order_compares_digits_by_value_and_letters_ignoring_case()
    {
        Assert.True(N("Chest 9", "chest 10") < 0);
        Assert.Equal(0, N("a02", "a2"));
        Assert.True(N("a010", "a9") > 0);
        Assert.True(N("x", "x1") < 0);
        Assert.True(N("B", "a") > 0);
        Assert.True(N("", "a") < 0);
        Assert.True(N("Room 3b", "Room 3a") > 0);
        Assert.True(N("v1.10", "v1.9") > 0);
        Assert.Equal(0, N(null, null));
    }

    [Fact]
    public void Numeric_text_sorts_as_numbers_and_other_text_naturally()
    {
        Assert.True(TableSort.CompareText("9.5", "10") < 0);
        Assert.True(TableSort.CompareText("Chest 10", "Chest 9") > 0);
        Assert.True(TableSort.Compare(3, 10) < 0);
        Assert.True(TableSort.Compare(2.5, 2) > 0);
        Assert.True(TableSort.Compare(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)) < 0);
    }

    [Fact]
    public void Order_is_stable_keeps_nothing_last_either_way_and_pins_rows_first()
    {
        var rows = new (string Name, int? Checks, bool Mine)[]
        {
            ("Slot 10", 5, false), ("Slot 2", null, false), ("Slot 1", 5, false), ("Slot 3", 7, true)
        };
        var byChecks = TableSort.Order(rows, r => r.Checks, descending: false);
        Assert.Equal(new[] { "Slot 10", "Slot 1", "Slot 3", "Slot 2" }, byChecks.Select(r => r.Name));
        var byChecksDown = TableSort.Order(rows, r => r.Checks, descending: true);
        Assert.Equal(new[] { "Slot 3", "Slot 10", "Slot 1", "Slot 2" }, byChecksDown.Select(r => r.Name));
        var byName = TableSort.Order(rows, r => r.Name, descending: false);
        Assert.Equal(new[] { "Slot 1", "Slot 2", "Slot 3", "Slot 10" }, byName.Select(r => r.Name));
        var mineFirst = TableSort.Order(rows, r => r.Name, descending: false, pinned: r => r.Mine);
        Assert.Equal(new[] { "Slot 3", "Slot 1", "Slot 2", "Slot 10" }, mineFirst.Select(r => r.Name));
        var mineFirstDown = TableSort.Order(rows, r => r.Checks, descending: true, pinned: r => r.Mine);
        Assert.Equal("Slot 3", mineFirstDown[0].Name);
    }
}

public class TableExportTests
{
    private static readonly string[] Headers = { "Item", "For", "Location" };
    private static readonly List<IReadOnlyList<string>> Rows = new()
    {
        new[] { "Hookshot", "Alice", "Chest, in the Temple" },
        new[] { "Bow \"of Light\"", "Bob", "Line one\nline two" },
        new[] { "Sword | Shield", "Carol", "Tab\there" }
    };

    [Fact]
    public void Tsv_keeps_every_cell_on_its_line()
    {
        var lines = TableExport.Tsv(Headers, Rows).TrimEnd('\n').Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("Item\tFor\tLocation", lines[0]);
        Assert.Equal("Bow \"of Light\"\tBob\tLine one line two", lines[2]);
        Assert.Equal("Sword | Shield\tCarol\tTab here", lines[3]);
    }

    [Fact]
    public void Csv_quotes_what_needs_it_and_doubles_quotes()
    {
        var lines = TableExport.Csv(Headers, Rows).Split("\r\n");
        Assert.Equal("Item,For,Location", lines[0]);
        Assert.Equal("Hookshot,Alice,\"Chest, in the Temple\"", lines[1]);
        Assert.Equal("\"Bow \"\"of Light\"\"\",Bob,\"Line one\nline two\"", lines[2]);
    }

    [Fact]
    public void Markdown_escapes_pipes_and_has_a_separator_line()
    {
        var lines = TableExport.Markdown(Headers, Rows).TrimEnd('\n').Split('\n');
        Assert.Equal("| Item | For | Location |", lines[0]);
        Assert.Equal("|---|---|---|", lines[1]);
        Assert.Equal("| Sword \\| Shield | Carol | Tab here |", lines[4]);
    }

    [Fact]
    public void Discord_lines_columns_up_and_splits_into_messages_that_fit()
    {
        var one = TableExport.Discord(Headers, Rows);
        Assert.Single(one);
        var lines = one[0].Split('\n');
        Assert.Equal("```", lines[0]);
        Assert.Matches(@"^Item\s+For\s+Location$", lines[1]);
        Assert.Matches(@"^-+\s+-+\s+-+$", lines[2]);
        Assert.Equal(lines[1].IndexOf("For", StringComparison.Ordinal), lines[3].IndexOf("Alice", StringComparison.Ordinal));
        Assert.EndsWith("```", one[0]);
        var many = Enumerable.Range(0, 400).Select(i => (IReadOnlyList<string>)new[] { $"Item {i}", "Player", "Somewhere far away" }).ToList();
        var parts = TableExport.Discord(Headers, many);
        Assert.True(parts.Count > 1);
        Assert.All(parts, part => Assert.True(part.Length <= TableExport.DiscordMessageLimit, $"a part is {part.Length} characters"));
        Assert.All(parts, part => Assert.StartsWith("```\nItem", part));
        Assert.Equal(400, parts.Sum(part => part.Split('\n').Count(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^Item \d+"))));
    }

    [Fact]
    public void Discord_cuts_a_cell_wider_than_a_column_may_be()
    {
        var wide = new List<IReadOnlyList<string>> { new[] { new string('x', 100), "a", "b" } };
        string part = TableExport.Discord(Headers, wide).Single();
        Assert.Contains(new string('x', TableExport.DiscordColumnLimit - 1) + "…", part);
        Assert.DoesNotContain(new string('x', TableExport.DiscordColumnLimit), part);
    }

    [Fact]
    public void A_short_row_is_padded_to_the_headers()
    {
        string tsv = TableExport.Tsv(Headers, new List<IReadOnlyList<string>> { new[] { "only" } });
        Assert.Equal("only\t\t", tsv.Split('\n')[1]);
    }
}
