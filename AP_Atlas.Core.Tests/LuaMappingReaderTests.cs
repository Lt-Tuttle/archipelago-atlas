using AP_Atlas.Core.PopTracker;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// A pack's mapping scripts, read without running Lua: the id written as a plain number, a hexadecimal one, or
/// arithmetic on a number the file sets first (packs write all three), and the table that holds the mapping.
/// </summary>
public sealed class LuaMappingReaderTests
{
    [Fact]
    public void The_table_keyed_by_ids_is_the_mapping_when_a_table_of_names_comes_first()
    {
        const string lua = "SETTINGS_MAPPING = {\n  [\"Include_Temple\"] = \"YesTemple\",\n}\nITEM_MAPPING = {\n  [5] = {{\"PV1500Extensions\"}},\n}";
        var result = LuaMappingReader.Read(lua);
        Assert.Equal("ITEM_MAPPING", result.TableName);
        Assert.Equal(5, result.Entries.Single().Id);
        // With no id-keyed table, the one of names is the mapping.
        Assert.Equal("ITEM_MAPPING", LuaMappingReader.Read("ITEM_MAPPING = {\n  [\"Boo Portrait\"] = {\"boo_portrait\"},\n}").TableName);
    }

    [Fact]
    public void An_entry_keyed_by_a_name_keeps_the_name()
    {
        var result = LuaMappingReader.Read("LOCATION_MAPPING = {\n  [\"Passage - The Martyr\"] = {\"@Achievements/The Martyr/The Martyr\"},\n  [12] = {\"@A/B\"},\n}");
        Assert.Empty(result.Problems);
        Assert.Equal(2, result.Entries.Count);
        Assert.Null(result.Entries[0].Id);
        Assert.Equal("Passage - The Martyr", result.Entries[0].Key);
        Assert.Equal(new[] { "@Achievements/The Martyr/The Martyr" }, result.Entries[0].Strings);
        Assert.Equal(12, result.Entries[1].Id);
        Assert.Null(result.Entries[1].Key);
    }

    [Fact]
    public void Plain_ids_nested_entries_and_unkeyed_paths_are_read_as_before()
    {
        const string lua = """
            -- items
            ITEM_MAPPING = {
                [100824] = {"banner", "toggle"},
                [102640] = {{"rosabeth", "toggle"}, {"melfia", "toggle"}},
                {"@Area/Pin/Section"},
            }
            """;
        var result = LuaMappingReader.Read(lua);
        Assert.Empty(result.Problems);
        Assert.Equal("ITEM_MAPPING", result.TableName);
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(100824, result.Entries[0].Id);
        Assert.Equal(new[] { "banner", "toggle" }, result.Entries[0].Strings);
        Assert.Equal(new[] { "rosabeth", "toggle", "melfia", "toggle" }, result.Entries[1].Strings);
        Assert.Null(result.Entries[2].Id);
        Assert.Equal("@Area/Pin/Section", Assert.Single(result.Entries[2].Strings));
    }

    [Fact]
    public void Hexadecimal_ids_are_numbers()
    {
        const string lua = """
            LOCATION_MAPPING = {
                [0x696969] = {"@Area 1/1F - First Item/1F - First Item"},
                [0XD55C0035] = {"@Sealed Room: Main shaft left fake wall/"},
            }
            """;
        var result = LuaMappingReader.Read(lua);
        Assert.Empty(result.Problems);
        Assert.Equal(new long?[] { 0x696969, 0xD55C0035 }, result.Entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Ids_written_as_arithmetic_on_numbers_the_file_sets_first_are_worked_out()
    {
        const string lua = """
            -- the base, as the apworld numbers its locations
            BASE_LOCATION_ID = 2023011800
            local OFFSET = BASE_LOCATION_ID + 300 -- the relics
            BASE = 6242624000;
            LOCATION_MAPPING = {
                [BASE_LOCATION_ID + 3286] = { { "@Disguise Locations/Ambrose Island Disguise Checks/Hippie"} },
                [BASE + 0] = {"@Hyrule Overworld/Ravio's Shop/Ravio's Gift"},
                [ OFFSET - 1 ] = {"@A/B"},
                [(BASE + 3) * 2] = {"@A/C"},
            }
            """;
        var result = LuaMappingReader.Read(lua);
        Assert.Empty(result.Problems);
        Assert.Equal(new long?[] { 2023011800L + 3286, 6242624000L, 2023011800L + 299, (6242624000L + 3) * 2 }, result.Entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void A_key_that_isnt_a_number_the_file_sets_is_read_without_an_id_and_said()
    {
        const string lua = """
            LOCATION_MAPPING = {
                [SOMETHING_ELSE + 1] = {"@A/B"},
                ["123"] = {"@A/C"},
                ["pickups/Chozo Ruins"] = "@Chozo Ruins/Main Plaza/Tree",
            }
            """;
        var result = LuaMappingReader.Read(lua);
        Assert.Equal(new long?[] { null, 123, null }, result.Entries.Select(e => e.Id).ToArray());
        // A string key is the game's own name for the entry (the Entry's Key), not a problem.
        Assert.Single(result.Problems);
        Assert.Contains("[SOMETHING_ELSE + 1]", result.Problems[0]);
        Assert.Contains("line 2", result.Problems[0]);
        Assert.Equal("pickups/Chozo Ruins", result.Entries[2].Key);
    }

    [Fact]
    public void The_table_named_for_the_mapping_is_read_even_after_a_helper_table()
    {
        const string lua = """
            local TYPES = {"toggle", "progressive"}
            LOCATION_MAPPING = {
                [1] = {"@A/B"},
            }
            """;
        var result = LuaMappingReader.Read(lua);
        Assert.Equal("LOCATION_MAPPING", result.TableName);
        Assert.Equal(1, Assert.Single(result.Entries).Id);
    }

    [Fact]
    public void A_mapping_built_by_a_loop_leaves_the_list_it_is_built_from()
    {
        // Dark Souls II's pack: the paths in order, LOCATION_MAPPING filled from their positions as the script runs.
        const string lua = """
            LOCATIONS = {
                {"@Majula/Majula: Titanite Shard x3 - mansion attic/"},
                {"@Majula/Majula: Torch x3 - mansion attic/"},
            }
            LOCATION_MAPPING = {
            }
            for n, item in ipairs(LOCATIONS) do
                LOCATION_MAPPING[n] = item
            end
            """;
        var result = LuaMappingReader.Read(lua);
        Assert.Equal("LOCATIONS", result.TableName);
        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Null(e.Id));
    }

    [Fact]
    public void Unreadable_entries_are_listed_up_to_a_limit_and_counted_after()
    {
        var lua = "LOCATION_MAPPING = {\n" + string.Concat(Enumerable.Range(0, 30).Select(i => $"    [UNKNOWN + {i}] = {{\"@A/{i}\"}},\n")) + "}\n";
        var result = LuaMappingReader.Read(lua);
        Assert.Equal(30, result.Entries.Count);
        Assert.Equal(LuaMappingReader.MaxListedProblems + 1, result.Problems.Count);
        Assert.Equal("…and 10 more entries that couldn't be read.", result.Problems[^1]);
    }

    [Fact]
    public void A_value_nested_far_too_deep_is_refused_without_harm()
    {
        string deep = new string('{', 5000) + "\"x\"" + new string('}', 5000);
        var result = LuaMappingReader.Read("LOCATION_MAPPING = {\n [1] = " + deep + ",\n [2] = {\"@A/B\"},\n}");
        Assert.Contains(result.Problems, p => p.Contains("nested too deeply"));
        Assert.Contains(result.Entries, e => e.Id == 2);
    }

    [Theory]
    [InlineData("42", 42L)]
    [InlineData(" -1 ", -1L)]
    [InlineData("0x10", 16L)]
    [InlineData("BASE + 0x10 - 2", 1014L)]
    [InlineData("(BASE - 1) * 3", 2997L)]
    [InlineData("- - 5", 5L)]
    public void Arithmetic_on_whole_numbers_is_worked_out(string expression, long expected)
    {
        Assert.Equal(expected, LuaMappingReader.EvaluateNumber(expression, new Dictionary<string, long> { ["BASE"] = 1000 }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("n")]
    [InlineData("f(1)")]
    [InlineData("\"5\"")]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("9223372036854775807 + 1")]
    [InlineData("0x")]
    [InlineData("1 -- comment")]
    public void Anything_else_is_not_a_number(string expression)
    {
        Assert.Null(LuaMappingReader.EvaluateNumber(expression, new Dictionary<string, long>()));
    }

    [Fact]
    public void Brackets_nested_past_the_limit_are_not_a_number()
    {
        string deep = new string('(', 40) + "1" + new string(')', 40);
        Assert.Null(LuaMappingReader.EvaluateNumber(deep, new Dictionary<string, long>()));
        Assert.Equal(1L, LuaMappingReader.EvaluateNumber("((1))", new Dictionary<string, long>()));
    }
}
