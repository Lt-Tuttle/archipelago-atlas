using System;

namespace AP_Atlas.Core.Maps;

/// <summary>
/// What a map pin's colour says, as PopTracker colours its locations: every open check in logic, some of them (mixed:
/// drawn half in logic, half out), reachable only by sequence breaks (the game's glitch logic), none, all checked, or
/// logic not known (not running, or hidden by race mode); a hinted pin has its own colours, in or out of logic (or
/// while logic isn't known), so a hint never hides the pin's logic.
/// </summary>
public enum MapPinState
{
    InLogic,
    OutOfLogic,
    Mixed,
    SequenceBreak,
    Checked,
    LogicUnknown,
    HintedInLogic,
    HintedOutOfLogic,
    HintedUnknown
}

/// <summary>A pin's state from its open checks and what logic says about them.</summary>
public static class MapPinLogic
{
    /// <summary>
    /// Every state with a colour of its own, in the order the legend and the settings list them. Mixed isn't one: a mixed
    /// pin is drawn half in the in-logic colour, half in the out-of-logic colour.
    /// </summary>
    public static readonly MapPinState[] All =
    {
        MapPinState.InLogic, MapPinState.SequenceBreak, MapPinState.OutOfLogic, MapPinState.HintedInLogic, MapPinState.HintedOutOfLogic,
        MapPinState.HintedUnknown, MapPinState.Checked, MapPinState.LogicUnknown
    };

    /// <param name="open">The pin's checks still to do (those that count: excluded ones left out unless shown).</param>
    /// <param name="reachable">How many of them logic can reach.</param>
    /// <param name="glitchedOnly">How many of the rest the game's glitch logic can reach.</param>
    /// <param name="logicKnown">Whether logic is running and shown (not before the engine runs, never in race mode).</param>
    /// <param name="hinted">Whether any of the open checks is hinted (a hinted pin counts as in logic when any check is).</param>
    public static MapPinState StateOf(int open, int reachable, int glitchedOnly, bool logicKnown, bool hinted = false)
    {
        if (open <= 0) return MapPinState.Checked;
        if (!logicKnown) return hinted ? MapPinState.HintedUnknown : MapPinState.LogicUnknown;
        reachable = Math.Clamp(reachable, 0, open);
        if (hinted) return reachable > 0 ? MapPinState.HintedInLogic : MapPinState.HintedOutOfLogic;
        if (reachable == open) return MapPinState.InLogic;
        if (reachable > 0) return MapPinState.Mixed;
        return glitchedOnly > 0 ? MapPinState.SequenceBreak : MapPinState.OutOfLogic;
    }

    /// <summary>Whether a pin of this state is drawn in two colours (half in logic, half out).</summary>
    public static bool IsSplit(MapPinState state) => state == MapPinState.Mixed;

    /// <summary>The state in plain words, as a tooltip and the legend say it.</summary>
    public static string Title(MapPinState state) => state switch
    {
        MapPinState.InLogic => "In logic",
        MapPinState.Mixed => "Some in logic",
        MapPinState.SequenceBreak => "Sequence break",
        MapPinState.OutOfLogic => "Out of logic",
        MapPinState.HintedInLogic => "Hinted, in logic",
        MapPinState.HintedOutOfLogic => "Hinted, out of logic",
        MapPinState.HintedUnknown => "Hinted",
        MapPinState.Checked => "Checked",
        _ => "Logic unknown"
    };

    /// <summary>The settings' key for a state ("in-logic").</summary>
    public static string Key(MapPinState state) => state switch
    {
        MapPinState.InLogic => "in-logic",
        MapPinState.Mixed => "mixed",
        MapPinState.SequenceBreak => "sequence-break",
        MapPinState.OutOfLogic => "out-of-logic",
        MapPinState.HintedInLogic => "hinted-in-logic",
        MapPinState.HintedOutOfLogic => "hinted-out-of-logic",
        MapPinState.HintedUnknown => "hinted",
        MapPinState.Checked => "checked",
        _ => "logic-unknown"
    };
}

/// <summary>The shapes a pin can take.</summary>
public enum MapPinShape
{
    Round,
    Square,
    Diamond
}

/// <summary>
/// A pin's size, border and shape, as a PopTracker pack sets them: on the pin's map location first, then on the map,
/// then Atlas's own (the size from the map image, the border 2, the shape from Settings → Appearance). PopTracker's
/// "rect" and "trapezoid" are drawn square, "diamond" a diamond, "circle" round.
/// </summary>
public static class MapPinGeometry
{
    public const float DefaultBorder = 2f;

    public readonly record struct Resolved(float Size, float Border, MapPinShape Shape);

    /// <param name="pinSize">The map location's "size" (0 or less: not set).</param>
    /// <param name="mapSize">The map's "location_size" (0 or less: not set).</param>
    /// <param name="fallbackSize">Atlas's size for the map (from its image).</param>
    /// <param name="pinBorder">The map location's "border_thickness" (below 0: not set).</param>
    /// <param name="mapBorder">The map's "location_border_thickness" (below 0: not set).</param>
    /// <param name="pinShape">The map location's "shape", or null.</param>
    /// <param name="mapShape">The map's "location_shape", or null.</param>
    /// <param name="appShape">The shape chosen under Settings → Appearance.</param>
    public static Resolved Resolve(float pinSize, float mapSize, float fallbackSize, float pinBorder, float mapBorder, string? pinShape, string? mapShape, MapPinShape appShape)
    {
        float size = pinSize > 0 ? pinSize : mapSize > 0 ? mapSize : fallbackSize;
        float border = pinBorder >= 0 ? pinBorder : mapBorder >= 0 ? mapBorder : DefaultBorder;
        var shape = ShapeOf(pinShape) ?? ShapeOf(mapShape) ?? appShape;
        return new Resolved(Math.Max(1f, size), Math.Clamp(border, 0f, 16f), shape);
    }

    /// <summary>A pack's shape name, or null for none (or one Atlas doesn't know).</summary>
    public static MapPinShape? ShapeOf(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "rect" or "rectangle" or "square" or "trapezoid" => MapPinShape.Square,
        "diamond" => MapPinShape.Diamond,
        "circle" or "round" or "oval" => MapPinShape.Round,
        _ => null
    };

    /// <summary>The Appearance setting's shape ("round", "square", "diamond").</summary>
    public static MapPinShape FromSetting(string? setting) => ShapeOf(setting) ?? MapPinShape.Round;

    /// <summary>The setting's name for a shape.</summary>
    public static string SettingOf(MapPinShape shape) => shape switch
    {
        MapPinShape.Square => "square",
        MapPinShape.Diamond => "diamond",
        _ => "round"
    };
}
