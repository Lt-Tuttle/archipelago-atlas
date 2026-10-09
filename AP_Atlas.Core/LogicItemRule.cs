#nullable enable
namespace AP_Atlas.Core;

/// <summary>
/// Which received items the logic engine hears about. Progression by the server's flags or the world's item pool, and
/// every item the server didn't find in a location: a start inventory comes as location -2 with no flags (MultiServer
/// builds its NetworkItems without them) and isn't in the item pool, yet it's what opens the first checks; -1 is a
/// server command's gift. Filler from a location changes nothing about logic and isn't sent.
/// </summary>
public static class LogicItemRule
{
    public static bool Counts(bool advancement, bool neverExclude, bool inProgressionPool, long locationId) =>
        advancement || neverExclude || inProgressionPool || locationId < 0;
}
