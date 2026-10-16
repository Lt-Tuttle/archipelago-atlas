using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.Games;

/// <summary>
/// What Atlas's pack lab found about a map pack, as the game list writes it (short codes), said in words for the pack
/// search, the setup window and the solo test; and whether a pack so noted should wait behind the others when Atlas picks.
/// </summary>
public static class IndexPackNotes
{
    /// <summary>Most of the pack's mapping is for ids this version of the game doesn't have.</summary>
    public const string OtherVersion = "other_version";

    /// <summary>The pack's mapping holds another game's ids (its manifest names the wrong game).</summary>
    public const string OtherGame = "other_game";

    /// <summary>The pack's scripts stop in Atlas (over a limit, or broken).</summary>
    public const string ScriptsStop = "scripts_stop";

    /// <summary>A quarter or more of the pack's maps have no picture.</summary>
    public const string NoPictures = "no_pictures";

    /// <summary>
    /// The notes as sentences, in a fixed order; codes Atlas doesn't know are left out (a newer list may have more).
    /// <paramref name="forGame"/> is the game an <see cref="OtherGame"/> pack's ids are for; <paramref name="namedGame"/>
    /// the game its manifest names, when the pack is listed under the game it's for.
    /// </summary>
    public static List<string> Sentences(IEnumerable<string>? notes, string? forGame = null, string? namedGame = null)
    {
        var codes = new HashSet<string>(notes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var said = new List<string>();
        if (!string.IsNullOrWhiteSpace(namedGame))
            said.Add($"Its manifest names {namedGame}, but its checks are this game's.");
        if (codes.Contains(OtherGame))
            said.Add(string.IsNullOrWhiteSpace(forGame) ? "Its checks are another game's." : $"Its checks are {forGame}'s, not this game's.");
        if (codes.Contains(OtherVersion))
            said.Add("Made for another version of the game: few of its checks link to this one.");
        if (codes.Contains(ScriptsStop))
            said.Add("Its scripts stop in Atlas, so its items follow the pack's item list only.");
        if (codes.Contains(NoPictures))
            said.Add("Some of its maps have no picture; Atlas lists their checks instead.");
        return said;
    }

    /// <summary>The notes as a word or two each, in the same order (the pack search's rows; the sentences go in the row's tooltip).</summary>
    public static List<string> Labels(IEnumerable<string>? notes, string? forGame = null, string? namedGame = null)
    {
        var codes = new HashSet<string>(notes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var said = new List<string>();
        if (!string.IsNullOrWhiteSpace(namedGame)) said.Add($"Manifest says {namedGame}");
        if (codes.Contains(OtherGame)) said.Add(string.IsNullOrWhiteSpace(forGame) ? "Another game's" : $"For {forGame}");
        if (codes.Contains(OtherVersion)) said.Add("Another version");
        if (codes.Contains(ScriptsStop)) said.Add("Scripts stop");
        if (codes.Contains(NoPictures)) said.Add("Some maps lack pictures");
        return said;
    }

    /// <summary>
    /// Whether a pack so noted waits behind the others when Atlas picks a pack by itself: one made for another version or
    /// another game, or whose scripts stop. It's still offered, and still taken when nothing else fits.
    /// </summary>
    public static bool WaitsBehind(IEnumerable<string>? notes) =>
        notes != null && notes.Any(n => string.Equals(n, OtherVersion, StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, OtherGame, StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, ScriptsStop, StringComparison.OrdinalIgnoreCase));
}
