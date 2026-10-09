using AP_Atlas.Core;

namespace AP_Atlas.Core.Tests;

public class InstanceCheckTests
{
    private static InstanceCheck.Twin Old(int id, bool window = false) => new(id, window, TimeSpan.FromMinutes(5));

    [Fact]
    public void Windowless_old_twins_are_offered()
    {
        Assert.Equal(new[] { 11, 12 }, InstanceCheck.LeftoversToEnd(new[] { Old(11), Old(12) }));
    }

    [Fact]
    public void A_twin_with_a_window_means_Atlas_is_open()
    {
        Assert.Empty(InstanceCheck.LeftoversToEnd(new[] { Old(11), Old(12, window: true) }));
    }

    [Fact]
    public void A_twin_still_starting_is_left_alone()
    {
        Assert.Empty(InstanceCheck.LeftoversToEnd(new[] { new InstanceCheck.Twin(11, false, TimeSpan.FromSeconds(3)) }));
    }

    [Fact]
    public void No_twins_nothing_to_end()
    {
        Assert.Empty(InstanceCheck.LeftoversToEnd(Array.Empty<InstanceCheck.Twin>()));
    }
}
