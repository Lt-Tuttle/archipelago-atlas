using System;
using System.Globalization;

namespace AP_Atlas.Core.PopTracker;

/// <summary>
/// What Atlas says after it fixed a map pack by itself (the owner, 2026-10-16: the confident fixes are made without asking, one
/// line says so, with Review fixes): the card's line, and the count Install games' summary says per pack. English templates
/// through the caller's translation, numbers filled in first and names last.
/// </summary>
public static class AutoFixWords
{
    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>"1 thing", "12 things".</summary>
    public static string Things(int n, Func<string, string> tr) => n == 1 ? tr("1 thing") : tr("{0} things").Replace("{0}", N(n));

    /// <summary>
    /// The card: "Atlas fixed 12 things in Emerald Tracker by itself." (Round 13 dropped "N are left for you on the map's
    /// Notices": it counted the Doctor's findings, one per unlinked pin section, not what the Notices show.)
    /// </summary>
    public static string Card(string packName, int fixedCount, Func<string, string> tr) =>
        tr("Atlas fixed {0} in {1} by itself.").Replace("{0}", Things(fixedCount, tr)).Replace("{1}", packName);
}
