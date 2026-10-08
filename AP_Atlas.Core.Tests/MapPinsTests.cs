using AP_Atlas.Core.Maps;

namespace AP_Atlas.Core.Tests;

/// <summary>A map pin's colour state, and its size, border and shape as a pack sets them.</summary>
public sealed class MapPinsTests
{
    [Theory]
    [InlineData(0, 0, 0, true, MapPinState.Checked)]
    [InlineData(0, 0, 0, false, MapPinState.Checked)]
    [InlineData(3, 3, 0, false, MapPinState.LogicUnknown)]
    [InlineData(3, 3, 0, true, MapPinState.InLogic)]
    [InlineData(3, 1, 0, true, MapPinState.Mixed)]
    [InlineData(3, 1, 2, true, MapPinState.Mixed)]
    [InlineData(3, 0, 1, true, MapPinState.SequenceBreak)]
    [InlineData(3, 0, 0, true, MapPinState.OutOfLogic)]
    [InlineData(2, 5, 0, true, MapPinState.InLogic)]
    public void A_pins_state_follows_its_open_checks(int open, int reachable, int glitched, bool known, MapPinState expected) =>
        Assert.Equal(expected, MapPinLogic.StateOf(open, reachable, glitched, known));

    [Theory]
    [InlineData(3, 3, 0, true, MapPinState.HintedInLogic)]
    [InlineData(3, 1, 0, true, MapPinState.HintedInLogic)]
    [InlineData(3, 0, 1, true, MapPinState.HintedOutOfLogic)]
    [InlineData(3, 0, 0, true, MapPinState.HintedOutOfLogic)]
    [InlineData(3, 3, 0, false, MapPinState.HintedUnknown)]
    [InlineData(0, 0, 0, true, MapPinState.Checked)]
    public void A_hinted_pin_keeps_its_logic_in_its_own_colours(int open, int reachable, int glitched, bool known, MapPinState expected) =>
        Assert.Equal(expected, MapPinLogic.StateOf(open, reachable, glitched, known, hinted: true));

    [Fact]
    public void Every_state_has_a_title_and_a_distinct_key_and_only_a_mixed_pin_is_split()
    {
        Assert.Equal(8, MapPinLogic.All.Length);
        Assert.DoesNotContain(MapPinState.Mixed, MapPinLogic.All);
        var every = Enum.GetValues<MapPinState>();
        Assert.Equal(every.Length, every.Select(MapPinLogic.Key).Distinct().Count());
        Assert.All(every, state => Assert.False(string.IsNullOrWhiteSpace(MapPinLogic.Title(state))));
        Assert.True(MapPinLogic.IsSplit(MapPinState.Mixed));
        Assert.All(MapPinLogic.All, state => Assert.False(MapPinLogic.IsSplit(state)));
    }

    [Fact]
    public void The_pin_wins_over_the_map_which_wins_over_Atlas()
    {
        var pin = MapPinGeometry.Resolve(40, 20, 16, 3, 1, "diamond", "rect", MapPinShape.Round);
        Assert.Equal(new MapPinGeometry.Resolved(40, 3, MapPinShape.Diamond), pin);
        var map = MapPinGeometry.Resolve(0, 20, 16, -1, 1, null, "rect", MapPinShape.Round);
        Assert.Equal(new MapPinGeometry.Resolved(20, 1, MapPinShape.Square), map);
        var atlas = MapPinGeometry.Resolve(0, 0, 16, -1, -1, null, null, MapPinShape.Diamond);
        Assert.Equal(new MapPinGeometry.Resolved(16, MapPinGeometry.DefaultBorder, MapPinShape.Diamond), atlas);
    }

    [Theory]
    [InlineData("rect", MapPinShape.Square)]
    [InlineData("Trapezoid", MapPinShape.Square)]
    [InlineData(" diamond ", MapPinShape.Diamond)]
    [InlineData("circle", MapPinShape.Round)]
    public void PopTrackers_shapes_map_to_Atlases(string name, MapPinShape expected) => Assert.Equal(expected, MapPinGeometry.ShapeOf(name));

    [Fact]
    public void An_unknown_shape_or_a_silly_border_falls_back()
    {
        Assert.Null(MapPinGeometry.ShapeOf("star"));
        var pin = MapPinGeometry.Resolve(-5, 0, 0, 500, -1, "star", null, MapPinShape.Square);
        Assert.Equal(1f, pin.Size);
        Assert.Equal(16f, pin.Border);
        Assert.Equal(MapPinShape.Square, pin.Shape);
        Assert.Equal(MapPinShape.Round, MapPinGeometry.FromSetting("nonsense"));
        Assert.Equal("diamond", MapPinGeometry.SettingOf(MapPinGeometry.FromSetting("diamond")));
    }
}
