using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core;

/// <summary>
/// What a game's own setup guide recommends. An apworld's docs/setup_en.md often links the tracker pack its author
/// expects players to use ("- [Map tracker](https://github.com/TVV1GK/DS3_AP_Maptracker)"), which a GitHub search can't
/// tell from any other project: the guide's word puts that project first and names it as recommended.
/// </summary>
public static class GuideLinks
{
    // [text](https://github.com/owner/repo...) and [text]: https://github.com/owner/repo..., then bare addresses (the match time limit is RegexDefaults's, set for every pattern).
    private static readonly Regex Inline = new(@"\[([^\]]*)\]\(\s*(https?://[^\s)]+)\s*\)", RegexOptions.IgnoreCase);
    private static readonly Regex Reference = new(@"^\s*\[([^\]]+)\]:\s*(https?://\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex Bare = new(@"https?://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", RegexOptions.IgnoreCase);

    /// <summary>
    /// The GitHub projects a guide links to as a tracker: the link's text or address says "tracker" (a map tracker, a
    /// PopTracker pack), and the project isn't PopTracker itself. In the guide's order, each once, as "owner/name".
    /// </summary>
    public static List<(string Repo, string Text)> TrackerProjects(string? markdown)
    {
        var found = new List<(string Repo, string Text)>();
        if (string.IsNullOrWhiteSpace(markdown)) return found;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Consider(string text, string url)
        {
            string? repo = RepoOf(url);
            if (repo == null || !seen.Add(repo)) return;
            bool tracker = Says(text, "tracker") || Says(repo, "tracker");
            if (!tracker || IsPopTrackerItself(repo)) seen.Remove(repo);
            else found.Add((repo, text.Trim()));
        }
        foreach (Match m in Inline.Matches(markdown)) Consider(m.Groups[1].Value, m.Groups[2].Value);
        foreach (Match m in Reference.Matches(markdown)) Consider(m.Groups[1].Value, m.Groups[2].Value);
        foreach (Match m in Bare.Matches(markdown)) Consider("", m.Value);
        return found;
    }

    /// <summary>"owner/name" from a GitHub address (any deeper path dropped), or null for anything else.</summary>
    public static string? RepoOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim().TrimEnd('.', ',', ')'), UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) && !string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
        string name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return parts[0] + "/" + name;
    }

    private static bool Says(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);

    private static bool IsPopTrackerItself(string repo) =>
        repo.EndsWith("/PopTracker", StringComparison.OrdinalIgnoreCase) || repo.EndsWith("/poptracker-pack", StringComparison.OrdinalIgnoreCase);
}
