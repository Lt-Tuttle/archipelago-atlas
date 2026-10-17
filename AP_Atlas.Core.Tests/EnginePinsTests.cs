using System.Text.RegularExpressions;
using AP_Atlas.Core.EngineSetup;

namespace AP_Atlas.Core.Tests;

/// <summary>
/// The engine's pins as AtlasEngine.cs states them (it's in the Godot project, so its text is read): the Archipelago and
/// Universal Tracker versions, their addresses and hashes, and the requirements Atlas leaves out or adds.
/// </summary>
internal sealed record EnginePins(string Archipelago, string ArchipelagoUrl, string ArchipelagoSha256, string Tracker, string TrackerUrl,
    IReadOnlyList<string> Skipped, IReadOnlyList<string> Extra)
{
    public static EnginePins Read()
    {
        string text = File.ReadAllText(TestEnvironment.RepoFile("AP_Atlas_Source", "Scripts", "Core", "Engine", "AtlasEngine.cs"));
        return new EnginePins(
            One(text, @"public const string ArchipelagoVersion = ""([^""]+)"";"),
            One(text, @"private const string ArchipelagoUrl = ""([^""]+)"";"),
            One(text, @"private const string ArchipelagoSha256 = ""([0-9a-f]{64})"";"),
            One(text, @"public const string TrackerVersion = ""([^""]+)"";"),
            One(text, @"private const string TrackerUrl = ""([^""]+)"";"),
            Quoted(One(text, @"private static readonly string\[\] SkippedRequirements = \{([^}]*)\};")),
            Quoted(One(text, @"private static readonly string\[\] ExtraRequirements = \{([^}]*)\};")));
    }

    /// <summary>The one match's group (a pin stated twice, or not at all, fails here).</summary>
    public static string One(string text, string pattern)
    {
        var matches = Regex.Matches(text, pattern, RegexOptions.Multiline);
        if (matches.Count != 1) throw new InvalidDataException($"{matches.Count} matches for {pattern}");
        return matches[0].Groups[1].Value;
    }

    /// <summary>The quoted strings of a list, in order.</summary>
    public static List<string> Quoted(string list) => Regex.Matches(list, @"""([^""]*)""|'([^']*)'").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
}

/// <summary>Every file that names the pinned Archipelago or Universal Tracker agrees with AtlasEngine.cs.</summary>
public sealed class EnginePinsTests
{
    private static string Repo(params string[] parts) => File.ReadAllText(TestEnvironment.RepoFile(parts));

    private static WorldsIndex Index() => WorldsIndex.Parse(Repo("AP_Atlas_Source", "Scripts", "Core", "Engine", "Data", "archipelago_worlds.json"));

    [Fact]
    public void The_worlds_index_is_the_pinned_archipelagos_release()
    {
        var pins = EnginePins.Read();
        var index = Index();
        Assert.Equal(pins.Archipelago, index.Archipelago);
        Assert.Equal($"https://github.com/ArchipelagoMW/Archipelago/archive/refs/tags/{pins.Archipelago}.zip", pins.ArchipelagoUrl);
        Assert.Equal(pins.ArchipelagoSha256, index.ZipSha256, ignoreCase: true);
        Assert.Equal($"Archipelago-{pins.Archipelago}/", index.ZipRoot);
        Assert.True(index.ZipBytes > 0, "the index doesn't say the zip's size");
    }

    [Fact]
    public void The_package_lock_is_built_for_the_pinned_archipelago()
    {
        var pins = EnginePins.Read();
        // Either newline: a fresh clone checks the lock out with CRLF.
        string first = Repo("AP_Atlas_Source", "Scripts", "Core", "Engine", "Data", "engine_packages.lock.txt").Split('\n')[0].TrimEnd('\r');
        Assert.Contains($"for Archipelago {pins.Archipelago},", first);
    }

    [Fact]
    public void The_credits_and_notices_name_the_pinned_archipelago()
    {
        var pins = EnginePins.Read();
        string credits = Repo("CREDITS.md");
        Assert.Contains($"| Archipelago {pins.Archipelago} |", credits);
        Assert.Contains($"/ArchipelagoMW/Archipelago/releases/tag/{pins.Archipelago} |", credits);
        Assert.Contains($"Archipelago {pins.Archipelago}'s core", Repo("THIRD_PARTY_NOTICES.md"));
    }

    [Fact]
    public void The_tracker_download_and_the_bridge_name_the_pinned_tracker()
    {
        var pins = EnginePins.Read();
        Assert.Contains($"Tracker_{pins.Tracker}/", pins.TrackerUrl);
        string bridge = Repo("AP_Atlas_Source", "Scripts", "Core", "Engine", "Python", "atlas_bridge.py");
        Assert.Contains(pins.Tracker, EnginePins.Quoted(EnginePins.One(bridge, @"^TESTED_UT_VERSIONS = \[([^\]]*)\]")));
    }

    [Fact]
    public void The_lock_builder_leaves_out_and_adds_the_same_requirements_as_the_engine()
    {
        var pins = EnginePins.Read();
        string builder = Repo("AP_Atlas_Source", "Tools", "build_engine_lock.py");
        Assert.Equal(pins.Skipped.Order(StringComparer.Ordinal).ToArray(), EnginePins.Quoted(EnginePins.One(builder, @"^SKIPPED = \{([^}]*)\}")).Order(StringComparer.Ordinal));
        Assert.Equal(pins.Extra.ToArray(), EnginePins.Quoted(EnginePins.One(builder, @"^EXTRA = \[([^\]]*)\]")));
        Assert.NotEmpty(pins.Skipped);
        Assert.NotEmpty(pins.Extra);
    }
}
