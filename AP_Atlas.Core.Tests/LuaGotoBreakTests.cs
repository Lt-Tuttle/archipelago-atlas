namespace AP_Atlas.Core.Tests;

/// <summary>A goto that leaves a loop for the label right after it is a break (MoonSharp runs the goto, but the enclosing loop then fails).</summary>
public class LuaGotoBreakTests
{
    [Fact]
    public void A_goto_out_of_a_loop_to_the_label_after_it_becomes_break()
    {
        // Oracle of Seasons' dungeon images, in small: the inner loop's goto leaves it for the label after its end.
        const string lua = "for d = 0, 9 do\n  local idx = 1\n  for _, loc in ipairs(L[d]) do\n    if f(loc) then\n      idx = idx + 2\n      goto continue\n    end\n  end\n  ::continue::\n  g(d, idx)\nend";
        const string expected = "for d = 0, 9 do\n  local idx = 1\n  for _, loc in ipairs(L[d]) do\n    if f(loc) then\n      idx = idx + 2\n      break\n    end\n  end\n  ::continue::\n  g(d, idx)\nend";
        Assert.Equal(expected, LuaGoto.Rewrite(lua, out int wrapped, out int breaks));
        Assert.Equal(0, wrapped);
        Assert.Equal(1, breaks);
        Assert.Equal(lua.Split('\n').Length, expected.Split('\n').Length);
    }

    [Fact]
    public void Only_gotos_directly_in_that_loop_become_breaks()
    {
        // From a loop nested in the loop, a break wouldn't reach the label: left as a goto. From a function: left too.
        const string nested = "for i = 1, 3 do\n  for j = 1, 3 do\n    if j == 2 then goto next end\n  end\nend\n::next::\nprint(1)";
        Assert.Equal(nested, LuaGoto.Rewrite(nested));
        const string inner = "for i = 1, 3 do\n  local h = function() goto out end\nend\n::out::";
        Assert.Equal(inner, LuaGoto.Rewrite(inner));
        // The goto out of the inner loop is a break of the inner loop when the label follows the inner loop's end.
        Assert.Equal("for i = 1, 3 do\n  for j = 1, 3 do\n    if j == 2 then break end\n  end\n  ::next::\nend",
            LuaGoto.Rewrite("for i = 1, 3 do\n  for j = 1, 3 do\n    if j == 2 then goto next end\n  end\n  ::next::\nend"));
    }

    [Theory]
    // The label isn't right after the loop: a statement between.
    [InlineData("for i = 1, 3 do if i == 2 then goto skip end end\nprint(0)\n::skip::")]
    // The label is inside the loop (the continue idiom, no local): not a break.
    [InlineData("for i = 1, 3 do if i == 2 then goto continue end print(i) ::continue:: end")]
    // A plain do-block isn't a loop: a goto out of it works as it is.
    [InlineData("do if c then goto done end end ::done::")]
    // A repeat's until has its condition after it.
    [InlineData("repeat if c then goto out end until x ::out::")]
    public void Other_gotos_are_left_alone(string lua) => Assert.Equal(lua, LuaGoto.Rewrite(lua));
}
