using AP_Atlas.Core.PopTracker;

namespace AP_Atlas.Core.Tests;

/// <summary>Fix by example: two hand-made links teach how the game names a pack's sections, and only that rule is offered.</summary>
public sealed class PackPatternsTests
{
    private static PatternRow Row(string path, string section) =>
        new("loc:unmatched:" + path + "|" + section, path, path.Split('/')[^1], section);

    private static readonly Dictionary<long, string> Upgrades = new()
    {
        [1] = "Item Upgrade - Dagger",
        [2] = "Item Upgrade - Sword",
        [3] = "Item Upgrade - Axe",
        [4] = "Item Upgrade - Bow",
        [5] = "Shop - Dagger",
    };

    [Fact]
    public void The_pins_name_and_the_sections_with_fixed_text_link_the_other_rows()
    {
        var rows = new[] { Row("Town/Item Upgrade", "Dagger"), Row("Town/Item Upgrade", "Sword"), Row("Town/Item Upgrade", "Axe"), Row("Town/Item Upgrade", "Bow") };
        var a = new PatternExample(rows[0], "Item Upgrade - Dagger");
        var b = new PatternExample(rows[1], "Item Upgrade - Sword");
        var pattern = PackPatterns.Infer(a, b, rows, Upgrades);
        Assert.NotNull(pattern);
        Assert.Equal(new long[] { 3, 4 }, pattern!.Matches.Select(m => m.Id).OrderBy(id => id));
        Assert.Contains("the pin's name", pattern.Description);
        Assert.Contains("the section's name", pattern.Description);
    }

    [Fact]
    public void An_area_and_a_number_carried_across()
    {
        var names = new Dictionary<long, string>
        {
            [10] = "Cave: Chest 1",
            [11] = "Cave: Chest 2",
            [12] = "Forest: Chest 7",
            [13] = "Forest: Chest 8",
        };
        var rows = new[] { Row("Cave/Room A/Box 1", "Item"), Row("Cave/Room B/Box 2", "Item"), Row("Forest/Room C/Box 7", "Item"), Row("Forest/Room D/Box 8", "Item") };
        var pattern = PackPatterns.Infer(new PatternExample(rows[0], "Cave: Chest 1"), new PatternExample(rows[1], "Cave: Chest 2"), rows, names);
        Assert.NotNull(pattern);
        Assert.Equal(new long[] { 12, 13 }, pattern!.Matches.Select(m => m.Id).OrderBy(id => id));
    }

    [Fact]
    public void Examples_with_no_rule_in_common_offer_none()
    {
        var rows = new[] { Row("Town/Item Upgrade", "Dagger"), Row("Town/Shop", "Sword"), Row("Town/Item Upgrade", "Axe") };
        var pattern = PackPatterns.Infer(new PatternExample(rows[0], "Item Upgrade - Dagger"), new PatternExample(rows[1], "Bow"), rows, Upgrades);
        Assert.Null(pattern);
    }

    [Fact]
    public void A_location_shown_already_or_made_for_two_rows_is_given_to_none()
    {
        var rows = new[]
        {
            Row("Town/Item Upgrade", "Dagger"), Row("Town/Item Upgrade", "Sword"),
            Row("Town/Item Upgrade", "Axe"), Row("Castle/Item Upgrade", "Bow"), Row("Field/Item Upgrade", "Bow"),
        };
        var a = new PatternExample(rows[0], "Item Upgrade - Dagger");
        var b = new PatternExample(rows[1], "Item Upgrade - Sword");
        var pattern = PackPatterns.Infer(a, b, rows, Upgrades, taken: new HashSet<long> { 3 });
        Assert.NotNull(pattern);
        Assert.Empty(pattern!.Matches); // the axe's is shown already; the two bows make the same name
    }

    [Fact]
    public void A_name_the_game_does_not_have_links_nothing()
    {
        var rows = new[] { Row("Town/Item Upgrade", "Dagger"), Row("Town/Item Upgrade", "Sword"), Row("Town/Item Upgrade", "Spear") };
        var pattern = PackPatterns.Infer(new PatternExample(rows[0], "Item Upgrade - Dagger"), new PatternExample(rows[1], "Item Upgrade - Sword"), rows, Upgrades);
        Assert.NotNull(pattern);
        Assert.Empty(pattern!.Matches);
    }

    [Fact]
    public void One_example_is_not_enough_and_the_best_rules_come_first()
    {
        var rows = new[] { Row("Town/Item Upgrade", "Dagger"), Row("Town/Item Upgrade", "Sword"), Row("Town/Item Upgrade", "Axe"), Row("Town/Item Upgrade", "Bow") };
        var one = new[] { new PatternExample(rows[0], "Item Upgrade - Dagger") };
        Assert.Empty(PackPatterns.FromExamples(one, rows, Upgrades));
        var two = new[] { new PatternExample(rows[1], "Item Upgrade - Sword"), one[0] };
        var patterns = PackPatterns.FromExamples(two, rows, Upgrades);
        Assert.Single(patterns);
        Assert.Equal(2, patterns[0].Matches.Count);
    }

    [Fact]
    public void A_number_is_never_copied_from_the_examples()
    {
        // Both examples are their pin's first copy ("Scraps 1"): the next pin's "Scraps" may be its second.
        var names = new Dictionary<long, string> { [1] = "Church Scraps 1", [2] = "Caravan Scraps 1", [3] = "Cabin Scraps 1", [4] = "Cabin Scraps 2" };
        var rows = new[] { Row("Town/Church", "Scraps"), Row("Town/Caravan", "Scraps"), Row("Town/Cabin", "Scraps") };
        var pattern = PackPatterns.Infer(new PatternExample(rows[0], "Church Scraps 1"), new PatternExample(rows[1], "Caravan Scraps 1"), rows, names);
        Assert.True(pattern == null || pattern.Matches.Count == 0);
    }

    [Fact]
    public void A_rule_reaches_only_rows_like_its_examples()
    {
        // Both examples are "Cooked" pins: the "Shipped" pins of the same dishes are another kind of check.
        var names = new Dictionary<long, string>
        {
            [1] = "Cook Bean Hotpot",
            [2] = "Cook Pumpkin Pie",
            [3] = "Cook Crab Cakes",
            [4] = "Cook Baked Fish",
            [5] = "Shipsanity: Crab Cakes",
            [6] = "Shipsanity: Baked Fish",
        };
        var rows = new[]
        {
            Row("Cooksanity/Bean Hotpot/Cooked", ""), Row("Cooksanity/Pumpkin Pie/Cooked", ""),
            Row("Cooksanity/Crab Cakes/Cooked", ""), Row("Cooksanity/Baked Fish/Shipped", ""),
        };
        var pattern = PackPatterns.Infer(new PatternExample(rows[0], "Cook Bean Hotpot"), new PatternExample(rows[1], "Cook Pumpkin Pie"), rows, names);
        Assert.NotNull(pattern);
        Assert.Equal(new long[] { 3 }, pattern!.Matches.Select(m => m.Id));
    }

    [Fact]
    public void Names_are_compared_without_case_or_punctuation()
    {
        Assert.Equal("item upgrade dagger 1", PackPatterns.Normalize("Item Upgrade - Dagger #1"));
        Assert.Equal("", PackPatterns.Normalize(null));
    }
}
