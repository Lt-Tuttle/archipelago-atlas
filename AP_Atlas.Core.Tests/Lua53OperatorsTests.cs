using System.Diagnostics;

namespace AP_Atlas.Core.Tests;

public class Lua53OperatorsTests
{
    [Theory]
    // Ship of Harkinian's logic (the line that stopped its whole logic file compiling).
    [InlineData("return count + (pieces // 4)", "return count + (__atlas_idiv(pieces, 4))")]
    // Tighter than a comparison, looser than arithmetic, as Lua 5.3 has it.
    [InlineData("local x = a & b == c", "local x = __atlas_band(a, b) == c")]
    [InlineData("y = a & b + 1", "y = __atlas_band(a, b + 1)")]
    [InlineData("y = a .. b & c", "y = __atlas_band(a .. b, c)")]
    [InlineData("z = a * b // c + d", "z = __atlas_idiv(a * b, c) + d")]
    [InlineData("z = a // b * c", "z = __atlas_idiv(a, b) * c")]
    [InlineData("z = a // b // c", "z = __atlas_idiv(__atlas_idiv(a, b), c)")]
    // Inner brackets first, then the tighter operators: shifts, &, ~, |.
    [InlineData("y = (a | b) & c << 2", "y = __atlas_band((__atlas_bor(a, b)), __atlas_shl(c, 2))")]
    [InlineData("timer = timer | (ReadU8(segment, base + 3) << 24)", "timer = __atlas_bor(timer, (__atlas_shl(ReadU8(segment, base + 3), 24)))")]
    [InlineData("v = a | b ~ c & d", "v = __atlas_bor(a, __atlas_bxor(b, __atlas_band(c, d)))")]
    [InlineData("v = a >> 1 << 2", "v = __atlas_shl(__atlas_shr(a, 1), 2)")]
    // Unary ~, and unary operators and powers inside an operand.
    [InlineData("z = ~x ~ y", "z = __atlas_bxor(__atlas_bnot(x), y)")]
    [InlineData("z = ~~x", "z = __atlas_bnot(__atlas_bnot(x))")]
    [InlineData("x = -a ^ 2 & 3", "x = __atlas_band(-a ^ 2, 3)")]
    [InlineData("x = #t & 1", "x = __atlas_band(#t, 1)")]
    [InlineData("if value & 2^i > 0 then end", "if __atlas_band(value, 2^i) > 0 then end")]
    // Calls, methods, fields and indexes belong to their operand.
    [InlineData("v = obj:get(1).flags & 0xFF", "v = __atlas_band(obj:get(1).flags, 0xFF)")]
    [InlineData("item.Active = (roomData & (1 << slot[2])) ~= 0", "item.Active = (__atlas_band(roomData, (__atlas_shl(1, slot[2])))) ~= 0")]
    [InlineData("f(a & b, c | d)", "f(__atlas_band(a, b), __atlas_bor(c, d))")]
    [InlineData("t = { a & 1, [k] = b | 2 }", "t = { __atlas_band(a, 1), [k] = __atlas_bor(b, 2) }")]
    [InlineData("S = SLOT_DATA[S] & 2    ~= 2", "S = __atlas_band(SLOT_DATA[S], 2)    ~= 2")]
    public void Lua_53_operators_become_calls_with_their_operands(string lua, string expected) =>
        Assert.Equal(expected, Lua53Operators.Rewrite(lua));

    [Theory]
    [InlineData("local s = \"a & b | c // d\" -- x ~ y << z")]
    [InlineData("if a ~= b and a <= c and a >= d then return a / b end")]
    [InlineData("local t = [[ & | ~ ]] --[==[ // ]==]")]
    [InlineData("print(\"no operators at all\")")]
    [InlineData("local broken = (a & b")]
    public void Code_without_them_or_that_doesnt_parse_is_left_exactly_as_it_was(string lua)
    {
        string result = Lua53Operators.Rewrite(lua, out int rewritten);
        Assert.Same(lua, result);
        Assert.Equal(0, rewritten);
    }

    [Fact]
    public void Lines_keep_their_numbers_and_comments_stay()
    {
        const string lua = "local a = b -- first\n& c --[[ two\nlines ]] | d\nreturn a";
        string result = Lua53Operators.Rewrite(lua, out int rewritten);
        Assert.Equal(2, rewritten);
        Assert.Equal(lua.Split('\n').Length, result.Split('\n').Length);
        Assert.Equal("local a = __atlas_bor(__atlas_band(b,\n c),\n d)\nreturn a", result);
    }

    [Fact]
    public void An_operator_whose_operands_cant_be_told_is_left_for_the_compiler()
    {
        Assert.Equal("x = & b", Lua53Operators.Rewrite("x = & b"));
        Assert.Equal("x = __atlas_band(a, b) | ", Lua53Operators.Rewrite("x = a & b | "));
    }

    [Fact]
    public void Long_chains_and_deep_nesting_stay_linear()
    {
        string chain = "x = a" + string.Concat(Enumerable.Repeat(" & a", 15000));
        var watch = Stopwatch.StartNew();
        string result = Lua53Operators.Rewrite(chain, out int rewritten);
        Assert.Equal(15000, rewritten);
        Assert.StartsWith("x = " + string.Concat(Enumerable.Repeat("__atlas_band(", 3)), result);
        string nested = "x = " + string.Concat(Enumerable.Repeat("(a | ", 5000)) + "b" + new string(')', 5000);
        Assert.Equal(5000, Lua53Operators.Rewrite(nested, out _).Split("__atlas_bor(").Length - 1);
        Assert.True(watch.Elapsed.TotalSeconds < 5, $"took {watch.Elapsed.TotalSeconds:0.0} s");
        string tooMany = "x = a" + string.Concat(Enumerable.Repeat(" & a", Lua53Operators.MaxRewrites + 1));
        Assert.Same(tooMany, Lua53Operators.Rewrite(tooMany));
    }
}
