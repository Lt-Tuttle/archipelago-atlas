using System.Text.RegularExpressions;

namespace AP_Atlas.Core.Tests;

/// <summary>The engine that ships with Atlas is credited: every package its lock installs is named, at its version, with its license.</summary>
public sealed class EngineNoticesTests
{
    [Fact]
    public void Every_package_the_engine_ships_is_in_the_notices_and_the_credits()
    {
        string lockText = File.ReadAllText(TestEnvironment.RepoFile("AP_Atlas_Source", "Scripts", "Core", "Engine", "Data", "engine_packages.lock.txt"));
        var packages = Regex.Matches(lockText, @"^([A-Za-z0-9_.\-]+)==([^\s\\]+)", RegexOptions.Multiline).Select(m => (Name: m.Groups[1].Value, Version: m.Groups[2].Value)).ToList();
        Assert.True(packages.Count >= 20, $"the lock lists {packages.Count} packages");
        string notices = File.ReadAllText(TestEnvironment.RepoFile("THIRD_PARTY_NOTICES.md"));
        string credits = File.ReadAllText(TestEnvironment.RepoFile("CREDITS.md"));
        foreach (var (name, version) in packages.Append(("pip", "26.2.1")))
        {
            Assert.Contains($"| {name} {version} |", notices);
            Assert.Contains($"| {name} {version} |", credits);
        }
        Assert.Contains("Python 3.12.10", notices);
        Assert.Contains("engine/python/LICENSE.txt", notices);
        Assert.Contains($"Universal Tracker {EnginePins.Read().Tracker}", notices);
    }
}
