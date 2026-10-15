namespace AP_Atlas.Core.Tests;

/// <summary>Folding sections remember the user's choice against their default.</summary>
public sealed class FoldsTests
{
    [Fact]
    public void A_section_starts_as_its_default()
    {
        var collapsed = new List<string>();
        var expanded = new List<string>();
        Assert.True(Folds.IsCollapsed("Advanced", true, collapsed, expanded));
        Assert.False(Folds.IsCollapsed("Overview", false, collapsed, expanded));
    }

    [Fact]
    public void Opening_a_section_closed_by_default_is_kept_and_closing_it_again_forgets_it()
    {
        var collapsed = new List<string>();
        var expanded = new List<string>();
        Folds.Remember("Rules", false, true, collapsed, expanded);
        Assert.Equal(new[] { "Rules" }, expanded);
        Assert.False(Folds.IsCollapsed("Rules", true, collapsed, expanded));
        Folds.Remember("Rules", true, true, collapsed, expanded);
        Assert.Empty(expanded);
        Assert.Empty(collapsed);
        Assert.True(Folds.IsCollapsed("Rules", true, collapsed, expanded));
    }

    [Fact]
    public void Closing_a_section_open_by_default_is_kept_once()
    {
        var collapsed = new List<string>();
        var expanded = new List<string>();
        Folds.Remember("Overview", true, false, collapsed, expanded);
        Folds.Remember("Overview", true, false, collapsed, expanded);
        Assert.Equal(new[] { "Overview" }, collapsed);
        Assert.True(Folds.IsCollapsed("Overview", false, collapsed, expanded));
        Folds.Remember("Overview", false, false, collapsed, expanded);
        Assert.Empty(collapsed);
    }
}
