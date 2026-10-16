using System;
using System.Globalization;

namespace AP_Atlas.Core.PopTracker;

/// <summary>
/// What Atlas says after it fixed a map pack by itself (the owner, 2026-10-16: the confident fixes are made without asking, one
/// line says so, with Review fixes): the card's line and Add games' line per pack. English templates through the caller's
/// translation, numbers filled in first and names last.
/// </summary>
public static class AutoFixWords
{
    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>"1 thing", "12 things".</summary>
    public static string Things(int n, Func<string, string> tr) => n == 1 ? tr("1 thing") : tr("{0} things").Replace("{0}", N(n));

    /// <summary>The card: "Atlas fixed 12 things in Emerald Tracker by itself. 3 are left for you on the map's Notices."</summary>
    public static string Card(string packName, int fixedCount, int left, Func<string, string> tr) =>
        tr("Atlas fixed {0} in {1} by itself.").Replace("{0}", Things(fixedCount, tr)).Replace("{1}", packName) + Left(left, tr);

    /// <summary>Add games' line for a pack it installed: what Atlas fixed by itself and what's left.</summary>
    public static string Summary(string packName, string game, int fixedCount, int left, Func<string, string> tr) =>
        (fixedCount > 0 ? tr("{0} ({1}): Atlas fixed {2} by itself.").Replace("{2}", Things(fixedCount, tr)) : tr("{0} ({1}): nothing Atlas could fix by itself."))
            .Replace("{1}", game).Replace("{0}", packName) + Left(left, tr);

    private static string Left(int left, Func<string, string> tr) => left <= 0 ? ""
        : " " + (left == 1 ? tr("1 is left for you on the map's Notices.") : tr("{0} are left for you on the map's Notices.").Replace("{0}", N(left)));
}
