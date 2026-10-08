using System;

namespace AP_Atlas.Core;

/// <summary>What kind of page a web link opens, so a button can say so ("Discord thread", "Project page", "Setup guide").</summary>
public enum LinkKind
{
    /// <summary>Not an https link.</summary>
    None,
    /// <summary>A Discord channel or thread (discord.com and its subdomains, discordapp.com, discord.gg).</summary>
    Discord,
    GitHub,
    GitLab,
    /// <summary>archipelago.gg (a game's info or setup page).</summary>
    Archipelago,
    Other
}

public static class Links
{
    /// <summary>The kind of page an https link opens, by its host.</summary>
    public static LinkKind KindOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return LinkKind.None;
        string host = uri.Host.ToLowerInvariant();
        if (Is(host, "discord.com") || Is(host, "discordapp.com") || host == "discord.gg") return LinkKind.Discord;
        if (Is(host, "github.com")) return LinkKind.GitHub;
        if (Is(host, "gitlab.com")) return LinkKind.GitLab;
        if (Is(host, "archipelago.gg")) return LinkKind.Archipelago;
        return LinkKind.Other;
    }

    private static bool Is(string host, string site) => host == site || host.EndsWith("." + site, StringComparison.Ordinal);
}
