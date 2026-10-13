using System.Diagnostics;

namespace AP_Atlas.Core.Tests;

public class LuaTextTests
{
    [Fact]
    public void Lua_53_unicode_escapes_become_their_characters_and_the_rest_is_left_alone()
    {
        Assert.Equal("\"Siegbräu\"", LuaText.ExpandUnicodeEscapes("\"Siegbr\\u{00e4}u\""));
        Assert.Equal("a 😀 b", LuaText.ExpandUnicodeEscapes("a \\u{1F600} b"));
        Assert.Equal("\\u{D800} \\u{110000} \\u{} \\x41 \\n", LuaText.ExpandUnicodeEscapes("\\u{D800} \\u{110000} \\u{} \\x41 \\n"));
        Assert.Equal("", LuaText.ExpandUnicodeEscapes(""));
    }

    [Fact]
    public void Lua_54_attribs_come_off_locals_and_nothing_else()
    {
        // The Hades packs: a <const> local that stopped the whole file compiling.
        Assert.Equal("local DEFAULT_SEED = \"default\"", LuaText.StripAttribs("local DEFAULT_SEED <const> = \"default\""));
        Assert.Equal("  local a, b = f()", LuaText.StripAttribs("  local a <close>, b < const > = f()"));
        // Comparisons stay, on lines that aren't declarations and on ones that are.
        Assert.Same("if a <const> b then end", LuaText.StripAttribs("if a <const> b then end"));
        Assert.Equal("local x = a <const2 and 1", LuaText.StripAttribs("local x = a <const2 and 1"));
        Assert.Same("", LuaText.StripAttribs(""));
    }

    [Fact]
    public void Comments_go_and_lines_keep_their_numbers()
    {
        const string lua = "local a = 1 -- one\n--[[ block\nstill block ]] local b = 2\n--[==[ level two ]] still ]==]\nlocal c = 3";
        Assert.Equal("local a = 1 \n\n local b = 2\n\nlocal c = 3", LuaText.StripComments(lua));
        Assert.Equal("no comments here", LuaText.StripComments("no comments here"));
        Assert.Equal("", LuaText.StripComments(""));
    }

    [Fact]
    public void A_block_comment_never_closed_is_a_line_comment()
    {
        Assert.Equal("x\ny", LuaText.StripComments("x--[[ never closed\ny"));
        // A level past the limit isn't a block comment either.
        string deep = "--[" + new string('=', LuaText.MaxCommentLevel + 1) + "[ a\nb ]" + new string('=', LuaText.MaxCommentLevel + 1) + "]";
        Assert.Equal("\nb ]" + new string('=', LuaText.MaxCommentLevel + 1) + "]", LuaText.StripComments(deep));
    }

    [Fact]
    public void Crafted_text_is_read_in_linear_time()
    {
        // A line each opening a block comment that never closes, at every level: each would be searched for its closing
        // brackets to the end of the text (400,000 searches of up to 2.8 MB) if the scanner didn't remember which levels
        // have none left.
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < 400_000; i++) text.Append("--[").Append('=', i % (LuaText.MaxCommentLevel + 1)).Append("[ x\n");
        var clock = Stopwatch.StartNew();
        string stripped = LuaText.StripComments(text.ToString());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(new string('\n', 400_000), stripped); // each a line comment, its line break kept
    }
}
