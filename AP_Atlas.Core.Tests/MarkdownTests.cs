namespace AP_Atlas.Core.Tests;

public class MarkdownTests
{
    [Fact]
    public void Headings_are_bold_and_the_top_two_levels_wear_the_colour()
    {
        string bbcode = Markdown.ToBbcode("# Title\n\nText.\n\n## Part\n\n### Detail\n", "#ABCDEF");
        Assert.Contains("[b][color=#ABCDEF]Title[/color][/b]", bbcode);
        Assert.Contains("[b][color=#ABCDEF]Part[/color][/b]", bbcode);
        Assert.Contains("[b]Detail[/b]", bbcode);
        Assert.DoesNotContain("#", bbcode.Replace("#ABCDEF", ""));
    }

    [Fact]
    public void A_colour_that_is_not_one_is_replaced_by_the_default() =>
        Assert.Contains($"[color={Markdown.DefaultHeadingColor}]", Markdown.ToBbcode("# T", "red]text[/color"));

    [Fact]
    public void Paragraph_lines_join_and_blank_lines_separate() =>
        Assert.Equal("One two.\n\nThree.\n", Markdown.ToBbcode("One\ntwo.\n\nThree.\n"));

    [Fact]
    public void Lists_get_bullets_and_numbers_with_their_depth()
    {
        string bbcode = Markdown.ToBbcode("- a\n  - b\n1. one\n2. two\n");
        Assert.Contains("  • a\n", bbcode);
        Assert.Contains("      – b\n", bbcode);
        Assert.Contains("  1. one\n  2. two\n", bbcode);
    }

    [Fact]
    public void Inline_markup_becomes_the_tags_Atlas_uses() =>
        Assert.Equal("[b]bold[/b] [i]it[/i] [code]x[lb]1][/code] plain", Markdown.Inline("**bold** *it* `x[1]` plain"));

    [Fact]
    public void Only_https_links_are_links()
    {
        Assert.Equal("[url=https://example.org/a]site[/url]", Markdown.Inline("[site](https://example.org/a)"));
        Assert.Equal("site", Markdown.Inline("[site](http://example.org/a)"));
        Assert.Equal("the file", Markdown.Inline("[the file](LICENSE)"));
        Assert.Equal("[lb]not a link]", Markdown.Inline("[not a link]"));
    }

    [Fact]
    public void Html_breaks_become_lines_and_other_tags_are_dropped() =>
        Assert.Equal("a\nb c", Markdown.Inline("a<br>b <span>c</span>"));

    [Fact]
    public void Tables_become_rows_of_cells_with_a_bold_header()
    {
        string bbcode = Markdown.ToBbcode("| Name | Made by |\n|---|---|\n| Godot | Juan |\n");
        Assert.Contains("[b]Name  ·  Made by[/b]\n", bbcode);
        Assert.Contains("Godot  ·  Juan\n", bbcode);
        Assert.DoesNotContain("---", bbcode);
    }

    [Fact]
    public void Code_blocks_keep_their_lines_as_written() =>
        Assert.Contains("[code]let x = a[lb]0];\n[/code]", Markdown.ToBbcode("```\nlet x = a[0];\n```\n"));

    [Fact]
    public void Quotes_and_rules_show()
    {
        string bbcode = Markdown.ToBbcode("> careful\n\n---\n");
        Assert.Contains("[i]careful[/i]", bbcode);
        Assert.Contains("────", bbcode);
    }

    [Fact]
    public void Sections_split_at_their_level_and_keep_their_bodies()
    {
        var sections = Markdown.Sections("# Doc\n\nintro\n\n## [First](https://x.y)\n\ntext one\n\n### Sub\n\nmore\n\n## **Second**\n\ntext two\n");
        Assert.Equal(new[] { "First", "Second" }, sections.Select(s => s.Title));
        Assert.Equal("text one\n\n### Sub\n\nmore", sections[0].Body);
        Assert.Equal("text two", sections[1].Body);
    }

    [Fact]
    public void A_heading_inside_a_code_block_is_not_a_section() =>
        Assert.Single(Markdown.Sections("## Real\n\n```\n## not real\n```\n"));

    [Fact]
    public void Every_shipped_document_renders_and_the_guide_covers_every_tool()
    {
        foreach (string name in Docs.All)
        {
            string text = Docs.Read(name);
            Assert.False(string.IsNullOrWhiteSpace(text), name + " is empty");
            Assert.False(string.IsNullOrWhiteSpace(Markdown.ToBbcode(text)), name + " renders to nothing");
        }
        var titles = Markdown.Sections(Docs.Read(Docs.Guide)).Select(s => s.Title).ToList();
        foreach (string tool in new[] { "Map Tracker", "Key Items", "Logic Tracker", "Item History", "Hints", "Cheese Tracker", "Sphere Tracker", "Multiworlds", "Map Packs", "Settings", "Home" })
            Assert.Contains(titles, t => t.Contains(tool));
    }

    [Fact]
    public void Whats_new_is_the_changelogs_first_version()
    {
        var first = Markdown.Sections(Docs.Read(Docs.Changelog)).First();
        Assert.StartsWith("[", first.Title);
        Assert.Contains("### Added", first.Body);
    }
}
