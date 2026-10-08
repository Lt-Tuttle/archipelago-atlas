using AP_Atlas.Core.Geometry;

namespace AP_Atlas.Core.Tests;

/// <summary>A window is fitted inside its screen: never larger than it, never with a part (its title bar first) outside.</summary>
public sealed class GeometryTests
{
    private static readonly IntRect Screen = new(0, 40, 1920, 1000); // a taskbar at the top

    [Fact]
    public void A_window_that_fits_is_left_alone()
    {
        var wanted = new IntRect(100, 100, 800, 600);
        Assert.Equal(wanted, IntRect.Fit(wanted, Screen));
    }

    [Fact]
    public void A_window_above_or_beyond_the_screen_is_moved_inside()
    {
        Assert.Equal(new IntRect(0, 40, 800, 600), IntRect.Fit(new IntRect(-200, -300, 800, 600), Screen));
        Assert.Equal(new IntRect(1120, 440, 800, 600), IntRect.Fit(new IntRect(1500, 900, 800, 600), Screen));
    }

    [Fact]
    public void A_window_larger_than_the_screen_is_shrunk_to_it()
    {
        Assert.Equal(new IntRect(0, 40, 1920, 1000), IntRect.Fit(new IntRect(-50, -50, 2765, 1685), Screen));
        Assert.Equal(new IntRect(0, 100, 1920, 600), IntRect.Fit(new IntRect(100, 100, 2765, 600), Screen)); // too wide: full width, its own height and top
    }

    [Fact]
    public void Without_a_screen_nothing_changes()
    {
        var wanted = new IntRect(-50, -50, 5000, 5000);
        Assert.Equal(wanted, IntRect.Fit(wanted, new IntRect(0, 0, 0, 0)));
        Assert.Equal(new IntRect(0, 0, 500, 400), IntRect.Centred(500, 400, default));
    }

    [Fact]
    public void Centred_puts_a_window_in_the_middle_shrunk_if_it_must()
    {
        Assert.Equal(new IntRect(560, 240, 800, 600), IntRect.Centred(800, 600, Screen));
        Assert.Equal(new IntRect(0, 40, 1920, 1000), IntRect.Centred(3000, 2000, Screen));
    }

    [Fact]
    public void The_screen_a_window_is_on_is_the_one_holding_its_middle()
    {
        var second = new IntRect(1920, 0, 2560, 1440);
        Assert.True(new IntRect(1800, 100, 800, 600).CentreIn(second));
        Assert.False(new IntRect(1800, 100, 800, 600).CentreIn(Screen));
    }
}
