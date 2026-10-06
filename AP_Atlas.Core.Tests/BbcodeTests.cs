namespace AP_Atlas.Core.Tests;

public class BbcodeTests
{
    [Fact]
    public void Escaped_text_has_no_tags_left()
    {
        Assert.Equal("[lb]img]//host/share/a.png[lb]/img]", Bbcode.Escape("[img]//host/share/a.png[/img]"));
        Assert.Equal("", Bbcode.Escape(null));
        Assert.Equal("plain text, no brackets", Bbcode.Escape("plain text, no brackets"));
        // Escaped text is Safe as it stands: "[lb]" is a tag Safe keeps, and nothing else opens one.
        string escaped = Bbcode.Escape("[color=red]x[/color] [url=1]y[/url] [img]z[/img] [[");
        Assert.Equal(escaped, Bbcode.Safe(escaped));
    }

    [Fact]
    public void Colored_text_is_escaped_and_only_a_real_colour_is_used()
    {
        Assert.Equal("[color=orange]a [lb]b] c[/color]", Bbcode.Colored("a [b] c", "orange"));
        Assert.Equal("[color=#8A2BE2]x[/color]", Bbcode.Colored("x", "#8A2BE2"));
        Assert.Equal("plain", Bbcode.Colored("plain", null));
        // A "colour" that would close the tag early and open one of its own is left out.
        Assert.Equal("x", Bbcode.Colored("x", "red][img]//host/a.png[/img"));
        Assert.Equal("x", Bbcode.Colored("x", "red x"));
    }

    [Theory]
    [InlineData("[b]x[/b]")]
    [InlineData("[i]x[/i]")]
    [InlineData("[u]x[/u] [s]y[/s]")]
    [InlineData("[code]x[/code]")]
    [InlineData("[color=#8A2BE2]x[/color]")]
    [InlineData("[color=orange]x[/color]")]
    [InlineData("[bgcolor=#222222][color=lime] badge [/color][/bgcolor]")]
    [InlineData("[url=12][color=#aaaaff]a link[/color][/url]")]
    [InlineData("[url=L|3|2001]a location[/url]")]
    [InlineData("[lb]not a tag] and [rb]")]
    [InlineData("no tags at all")]
    [InlineData("")]
    public void Atlas_own_markup_is_kept(string bbcode) => Assert.Equal(bbcode, Bbcode.Safe(bbcode));

    [Theory]
    // The tags Godot loads a file for, in the forms its parser accepts (it matches "img" and "dropcap" as prefixes).
    [InlineData("[img]//host/share/a.png[/img]", "[lb]img]//host/share/a.png[lb]/img]")]
    [InlineData("[img=64x64]C:/a.png[/img]", "[lb]img=64x64]C:/a.png[lb]/img]")]
    [InlineData("[img width=9 height=9]a.png[/img]", "[lb]img width=9 height=9]a.png[lb]/img]")]
    [InlineData("[imgx]a.png[/img]", "[lb]imgx]a.png[lb]/img]")]
    [InlineData("[font=//host/share/a.ttf]x[/font]", "[lb]font=//host/share/a.ttf]x[lb]/font]")]
    [InlineData("[font name=//host/a.ttf size=9]x[/font]", "[lb]font name=//host/a.ttf size=9]x[lb]/font]")]
    [InlineData("[dropcap font=//host/a.ttf]X[/dropcap]", "[lb]dropcap font=//host/a.ttf]X[lb]/dropcap]")]
    // Anything else Atlas doesn't use: sizes, tables, effects, and tags a later Godot might add.
    [InlineData("[font_size=99999]x[/font_size]", "[lb]font_size=99999]x[lb]/font_size]")]
    [InlineData("[outline_size=500]x", "[lb]outline_size=500]x")]
    [InlineData("[table=999][cell]x[/cell][/table]", "[lb]table=999][lb]cell]x[lb]/cell][lb]/table]")]
    [InlineData("[wave amp=99]x[/wave]", "[lb]wave amp=99]x[lb]/wave]")]
    [InlineData("[IMG]a.png[/IMG]", "[lb]IMG]a.png[lb]/IMG]")]
    [InlineData("[ img]a.png", "[lb] img]a.png")]
    // One of Atlas's tags with something after it isn't one of Atlas's tags.
    [InlineData("[b x]y", "[lb]b x]y")]
    [InlineData("[urlx=1]y", "[lb]urlx=1]y")]
    // A "[" inside a tag: Godot reads the tag up to the first "]", so "img[x" would load a file.
    [InlineData("[img[x]a.png", "[lb]img[lb]x]a.png")]
    [InlineData("[color=red[img]]a", "[lb]color=red[lb]img]]a")]
    // A "[" with no "]" after it is text either way.
    [InlineData("a [ b", "a [lb] b")]
    public void Every_other_tag_is_made_text(string bbcode, string safe) => Assert.Equal(safe, Bbcode.Safe(bbcode));

    private static readonly string Break = ((char)0x200B).ToString();

    [Fact]
    public void A_long_run_without_a_space_gets_a_break_every_MaxRun_characters()
    {
        string run = new('x', 200);
        string safe = Bbcode.Safe(run);
        // The same text, with a zero-width space before the 65th, 129th and 193rd character.
        Assert.Equal(run, safe.Replace(Break, ""));
        Assert.Equal(3, safe.Split(Break).Length - 1);
        Assert.All(safe.Split(Break), part => Assert.True(part.Length <= Bbcode.MaxRun));
        Assert.Equal(safe, Bbcode.Safe(safe));
        // Spaces end a run, so words never get breaks; markup doesn't count toward one.
        string words = string.Join(" ", Enumerable.Repeat("word", 100));
        Assert.Equal(words, Bbcode.Safe(words));
        string tagged = "[color=red]" + new string('y', Bbcode.MaxRun) + "[/color][b]z[/b]";
        Assert.Equal("[color=red]" + new string('y', Bbcode.MaxRun) + "[/color][b]" + Break + "z[/b]", Bbcode.Safe(tagged));
    }

    [Fact]
    public void Breaks_never_split_a_tag_or_a_character()
    {
        // Escaped brackets show one each, so they're a run too: breaks go between whole [lb] tags.
        string brackets = Bbcode.Safe(Bbcode.Escape(new string('[', 1000)));
        Assert.Equal(Bbcode.Escape(new string('[', 1000)), brackets.Replace(Break, ""));
        Assert.Equal(1000 / Bbcode.MaxRun, brackets.Split(Break).Length - 1);
        Assert.All(brackets.Split(Break).Skip(1), part => Assert.StartsWith("[lb]", part));
        // A character outside the basic plane is two halves; one with an accent added is two characters too.
        string emoji = string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F600), 200));
        string accented = string.Concat(Enumerable.Repeat("e" + (char)0x0301, 200));
        foreach (string text in new[] { emoji, accented })
        {
            string safe = Bbcode.Safe(text);
            Assert.Equal(text, safe.Replace(Break, ""));
            Assert.Equal(200 / Bbcode.MaxRun, safe.Split(Break).Length - 1);
            Assert.All(safe.Split(Break).Skip(1), part => Assert.False(char.IsLowSurrogate(part[0]) || part[0] == (char)0x0301));
        }
    }

    [Fact]
    public void Crafted_markup_is_made_safe_in_linear_time()
    {
        // A million "[" with no "]" after them, then the same before one "]": each "[" once searched the rest of the text.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string open = new('[', 1_000_000);
        Assert.Equal(Bbcode.Escape(open), Bbcode.Safe(open).Replace(Break, ""));
        Assert.Equal(Bbcode.Escape(open) + "]", Bbcode.Safe(open + "]").Replace(Break, ""));
        Assert.Equal(1_000_000, Bbcode.Safe(new string('x', 1_000_000)).Replace(Break, "").Length);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public void Anything_safe_stays_safe()
    {
        var random = new Random(5);
        const string alphabet = "[]/=imgfontdrpcabus lr0123456789:.#\\";
        for (int i = 0; i < 5000; i++)
        {
            var chars = new char[random.Next(0, 40)];
            for (int c = 0; c < chars.Length; c++) chars[c] = alphabet[random.Next(alphabet.Length)];
            string safe = Bbcode.Safe(new string(chars));
            Assert.Equal(safe, Bbcode.Safe(safe));
            // No tag but Atlas's own can start anywhere in it.
            for (int open = safe.IndexOf('['); open >= 0; open = safe.IndexOf('[', open + 1))
            {
                int close = safe.IndexOf(']', open + 1);
                if (close < 0) continue;
                string tag = safe.Substring(open + 1, close - open - 1);
                Assert.DoesNotContain("img", tag, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("font", tag, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
