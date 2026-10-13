namespace AP_Atlas.Core.Tests;

public class LuaGotoTests
{
    [Fact]
    public void A_loop_body_ending_in_its_continue_label_is_wrapped_so_the_jump_is_legal()
    {
        // Cat Quest's autotracking, in small: the local after the goto is what MoonSharp refuses.
        const string lua = "for _, v in pairs(T) do\n    if not v[1] then\n        goto continue\n    end\n    local obj = f(v[1])\n    obj.Active = true\n    ::continue::\nend";
        const string expected = "for _, v in pairs(T) do do\n    if not v[1] then\n        goto continue\n    end\n    local obj = f(v[1])\n    obj.Active = true\n    end ::continue::\nend";
        Assert.Equal(expected, LuaGoto.Rewrite(lua, out int wrapped));
        Assert.Equal(1, wrapped);
        // The same number of lines: errors name the lines the author sees.
        Assert.Equal(lua.Split('\n').Length, expected.Split('\n').Length);
    }

    [Fact]
    public void Function_bodies_and_then_blocks_are_wrapped_from_their_own_openers()
    {
        Assert.Equal("local function f(a) do\n  local x = a\n  goto done\n  end ::done::\nend",
            LuaGoto.Rewrite("local function f(a)\n  local x = a\n  goto done\n  ::done::\nend"));
        Assert.Equal("if c then do local x = 1 goto skip end ::skip:: else return end",
            LuaGoto.Rewrite("if c then local x = 1 goto skip ::skip:: else return end"));
        // Only the block holding the label is wrapped; an outer loop's own locals don't count.
        Assert.Equal("local n = 0\nwhile n < 3 do do\n  local m = n\n  n = n + 1\n  if m == 1 then goto continue end\n  end ::continue::\nend",
            LuaGoto.Rewrite("local n = 0\nwhile n < 3 do\n  local m = n\n  n = n + 1\n  if m == 1 then goto continue end\n  ::continue::\nend"));
    }

    [Theory]
    // No local before the label: the jump is fine as it is.
    [InlineData("for i = 1, 3 do if i == 2 then goto continue end print(i) ::continue:: end")]
    // The label isn't at the block's end: Lua itself refuses the jump, and so does MoonSharp (left for the compiler).
    [InlineData("for i = 1, 3 do local x = i goto continue ::continue:: print(x) end")]
    // Nothing jumps to it.
    [InlineData("for i = 1, 3 do local x = i ::continue:: end")]
    // A repeat's condition sees the body's locals: not wrapped.
    [InlineData("repeat local x = f() if x then goto continue end ::continue:: until x")]
    // Labels and gotos in strings and comments aren't code.
    [InlineData("local s = '::continue::' -- goto continue\nlocal t = [[goto x ::x::]]")]
    [InlineData("")]
    public void Code_without_a_jump_to_wrap_is_left_as_written(string lua) => Assert.Same(lua, LuaGoto.Rewrite(lua));

    [Fact]
    public void Unbalanced_brackets_are_left_for_the_compiler()
    {
        const string lua = "for i = 1, 3 do local x = (i goto continue ::continue:: end";
        Assert.Same(lua, LuaGoto.Rewrite(lua));
    }
}
