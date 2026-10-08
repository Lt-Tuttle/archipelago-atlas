namespace AP_Atlas.Core.Tests;

/// <summary>A link's kind, from its host, so a button can say where it leads.</summary>
public sealed class LinkKindTests
{
    [Theory]
    [InlineData("https://discord.com/channels/731205301247803413/1255822049163612271", LinkKind.Discord)]
    [InlineData("https://canary.discord.com/channels/1/2", LinkKind.Discord)]
    [InlineData("https://discordapp.com/channels/1/2", LinkKind.Discord)]
    [InlineData("https://discord.gg/archipelago", LinkKind.Discord)]
    [InlineData("https://github.com/Ishigh1/ITB-randomizer-for-AP", LinkKind.GitHub)]
    [InlineData("https://gitlab.com/nanashi-archipelago/manual-gunfire-reborn", LinkKind.GitLab)]
    [InlineData("https://archipelago.gg/games/Hollow%20Knight/info/en", LinkKind.Archipelago)]
    [InlineData("https://example.org/guide", LinkKind.Other)]
    [InlineData("http://discord.com/channels/1/2", LinkKind.None)]
    [InlineData("notdiscord.com", LinkKind.None)]
    [InlineData("https://notdiscord.com/x", LinkKind.Other)]
    [InlineData("", LinkKind.None)]
    [InlineData(null, LinkKind.None)]
    public void A_links_kind_comes_from_its_host(string? url, LinkKind expected) => Assert.Equal(expected, Links.KindOf(url));
}
