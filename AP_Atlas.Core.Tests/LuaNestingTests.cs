using System.Diagnostics;

namespace AP_Atlas.Core.Tests;

public class LuaNestingTests
{
    private static string Repeat(string text, int times) => string.Concat(Enumerable.Repeat(text, times));

    // Every way Lua can nest that MoonSharp's compiler recurses for (each crashed it, on a 1 MB stack, somewhere between
    // 600 and 20,000 levels deep).
    private static readonly Dictionary<string, Func<int, string>> Nested = new()
    {
        ["parentheses"] = n => "return " + Repeat("(", n) + "1" + Repeat(")", n),
        ["tables"] = n => "return " + Repeat("{", n) + Repeat("}", n),
        ["table fields"] = n => "return " + Repeat("{ a = ", n) + "1" + Repeat(" }", n),
        ["indexing"] = n => "local t = {} return t" + Repeat("[1]", n),
        ["unary minus"] = n => "return " + Repeat("- ", n) + "1",
        ["not"] = n => "return " + Repeat("not ", n) + "true",
        ["length"] = n => "return " + Repeat("#", n) + "'x'",
        ["concatenation"] = n => "local a = 'x' return a" + Repeat(" .. a", n),
        ["concatenation over lines"] = n => "return 'a'" + Repeat(" ..\n'a'", n),
        ["power"] = n => "local a = 1 return a" + Repeat(" ^ a", n),
        ["addition"] = n => "local a = 1 return a" + Repeat(" + a", n),
        ["and"] = n => "local a = true return a" + Repeat(" and a", n),
        ["comparison"] = n => "local a = 1 return a" + Repeat(" < a", n),
        ["fields"] = n => "local t = {} return t" + Repeat(".x", n),
        ["calls"] = n => "local function f() return f end return f" + Repeat("()", n),
        ["methods"] = n => "local o = {} return o" + Repeat(":m()", n),
        ["calls with a string"] = n => "return f" + Repeat(" 'x'", n),
        ["do blocks"] = n => Repeat("do ", n) + Repeat("end ", n),
        ["if blocks"] = n => Repeat("if true then ", n) + Repeat("end ", n),
        ["while loops"] = n => Repeat("while true do ", n) + Repeat("end ", n),
        ["for loops"] = n => Repeat("for i = 1, 1 do ", n) + Repeat("end ", n),
        ["repeat loops"] = n => Repeat("repeat ", n) + Repeat("until true ", n),
        ["functions"] = n => Repeat("local function f() ", n) + Repeat("end ", n),
        ["function values"] = n => "return " + Repeat("function() return ", n) + "1" + Repeat(" end", n),
    };

    // What MoonSharp compiles with loops, not recursion: shallow however long.
    private static readonly Dictionary<string, Func<int, string>> Flat = new()
    {
        ["statements"] = n => Repeat("x = 1 ", n),
        ["calls on their own lines"] = n => Repeat("Tracker:AddItems('items/items.json')\n", n),
        ["field and method chains, one a statement"] = n => Repeat("a.b.c:d(1) ", n),
        ["table fields"] = n => "return { " + Repeat("1, ", n) + "}",
        ["named fields"] = n => "return { " + string.Join(", ", Enumerable.Range(0, n).Select(i => $"f{i} = {i}")) + " }",
        ["arguments"] = n => "f(" + string.Join(", ", Enumerable.Repeat("1", n)) + ")",
        ["return values"] = n => "return " + string.Join(", ", Enumerable.Repeat("1", n)),
        ["elseif chains"] = n => "if x then " + Repeat("elseif x then ", n) + "end",
        ["function definitions"] = n => string.Concat(Enumerable.Range(0, n).Select(i => $"function f{i}() return 1 end ")),
        ["brackets in a string"] = n => "return '" + Repeat("(", n) + "'",
        ["brackets in a long string"] = n => "return [==[" + Repeat("((]]", n) + "]==]",
        ["brackets in a comment"] = n => "-- " + Repeat("(", n) + "\nreturn 1",
        ["brackets in a long comment"] = n => "--[[" + Repeat("{(", n) + "]] return 1",
        ["escaped quotes"] = n => "return \"" + Repeat("\\\"(", n) + "\"",
        ["numbers with exponents"] = n => "return " + Repeat("1e-5, 0x1p+4, ", n) + "0",
    };

    [Fact]
    public void Every_way_of_nesting_counts_as_deep_as_it_goes()
    {
        foreach (var (kind, make) in Nested)
        {
            int depth = LuaNesting.Depth(make(5000));
            Assert.True(depth >= 5000, $"{kind} 5,000 deep counted as {depth}");
        }
    }

    [Fact]
    public void What_doesnt_nest_stays_shallow_however_long()
    {
        foreach (var (kind, make) in Flat)
        {
            int depth = LuaNesting.Depth(make(20000));
            Assert.True(depth <= 10, $"{kind}, 20,000 long, counted as {depth} deep");
        }
    }

    [Fact]
    public void Runs_of_operators_add_up_along_the_levels_still_open()
    {
        // 300 additions at each of 10 nested levels: the compiler can recurse through all of them.
        string code = "return " + Repeat("a" + Repeat(" + a", 300) + " + (", 10) + "1" + Repeat(")", 10);
        Assert.True(LuaNesting.Depth(code) >= 3000);
    }

    [Fact]
    public void Code_like_a_packs_is_shallow()
    {
        const string code = """
            ITEM_MAPPING = {
                [1001] = { { "sword", "toggle" } },
                [1002] = { { "bow", "progressive" } },
            }
            function onItem(index, item_id, item_name, player_number)
                local v = ITEM_MAPPING[item_id]
                if not v or not v[1] then return end
                for _, entry in ipairs(v) do
                    local obj = Tracker:FindObjectForCode(entry[1])
                    if obj then
                        if entry[2] == "toggle" then obj.Active = true
                        elseif entry[2] == "progressive" then
                            if obj.Active then obj.CurrentStage = obj.CurrentStage + 1 else obj.Active = true end
                        elseif entry[2] == "consumable" then obj.AcquiredCount = obj.AcquiredCount + obj.Increment
                        end
                    end
                end
                print(string.format("item %s from %s", item_name, tostring(player_number)) .. " (" .. index .. ")")
            end
            Archipelago:AddItemHandler("item handler", onItem)
            """;
        Assert.InRange(LuaNesting.Depth(code), 3, 20);
    }

    [Fact]
    public void Any_text_is_read_without_failing()
    {
        Assert.Equal(0, LuaNesting.Depth(""));
        Assert.Equal(0, LuaNesting.Depth(null!));
        foreach (string odd in new[] { "))))end end until", "\"unterminated", "'also\nunterminated", "--[==[ unterminated", "[=[ unterminated", "x = 1e-", "\\", "..." })
            LuaNesting.Depth(odd);
        const string alphabet = "([{)]}\"'\\-=.:;, end function if do repeat until x 1e-5 [[ ]] --\n";
        var random = new Random(6);
        for (int i = 0; i < 500; i++)
        {
            var chars = new char[random.Next(0, 300)];
            for (int j = 0; j < chars.Length; j++) chars[j] = alphabet[random.Next(alphabet.Length)];
            LuaNesting.Depth(new string(chars));
        }
    }

    [Fact]
    public void A_big_file_is_read_quickly()
    {
        string big = Repeat("local t = { a = 1, b = { 'x', \"y\" } } -- a comment\nif t.a then t.b[1] = t.b[2] .. 'z' end\n", 20000);
        var clock = Stopwatch.StartNew();
        int depth = LuaNesting.Depth(big);
        clock.Stop();
        Assert.True(depth < 10, $"counted {depth} deep");
        Assert.True(clock.ElapsedMilliseconds < 1000, $"{big.Length:N0} characters took {clock.ElapsedMilliseconds} ms");
    }
}
