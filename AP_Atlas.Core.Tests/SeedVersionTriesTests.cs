using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.Tests;

/// <summary>The versions Atlas tries by itself for a seed: known ones, from trusted projects, newest first, three at most.</summary>
public class SeedVersionTriesTests
{
    private const string Mine = "github.com/a/dsr";

    private static PublishedApworld V(string version, string project = Mine, string? sha = null, bool pre = false) =>
        new(version, project, $"https://{project}/releases/download/{version}/dsr.apworld", sha ?? $"sha-{project}-{version}", pre);

    private static string[] Versions(IEnumerable<ApworldChoice> picked) => picked.Select(c => c.Version).ToArray();

    [Fact]
    public void Picks_up_to_three_versions_from_trusted_projects_newest_first()
    {
        var known = new[] { V("0.9.0"), V("1.2.0"), V("1.0.0"), V("1.1.0"), V("1.3.0-beta", pre: true), V("2.0.0", "github.com/stranger/dsr") };
        var picked = SeedVersionTries.Pick(known, Array.Empty<IdentifiedApworld>(), Array.Empty<string>(), new[] { Mine }, "5eed");
        Assert.Equal(new[] { "1.3.0-beta", "1.2.0", "1.1.0" }, Versions(picked));
        Assert.Equal(3, SeedVersionTries.Max);
    }

    [Fact]
    public void Skips_the_installed_copy_and_versions_already_identified()
    {
        var known = new[] { V("1.2.0", sha: "cc"), V("1.1.0"), V("1.0.0"), V("0.9.0") };
        var identified = new[] { new IdentifiedApworld("1.1.0", Mine + " 1.1.0", "C:/atlas/games/dsr/1.1.0/dsr.apworld", "f00") };
        var picked = SeedVersionTries.Pick(known, identified, new[] { "CC" }, new[] { Mine }, "5eed");
        Assert.Equal(new[] { "1.0.0", "0.9.0" }, Versions(picked));
    }

    [Fact]
    public void A_release_known_from_the_index_and_a_release_list_is_tried_once()
    {
        var known = new[] { V("1.0.0", sha: "aa"), new PublishedApworld("v1.0.0", Mine, $"https://{Mine}/releases/download/v1.0.0/dsr.apworld", null) };
        Assert.Single(SeedVersionTries.Pick(known, Array.Empty<IdentifiedApworld>(), Array.Empty<string>(), new[] { Mine }, "5eed"));
    }

    [Fact]
    public void Trust_is_by_project_whatever_its_case()
    {
        var picked = SeedVersionTries.Pick(new[] { V("1.0.0") }, Array.Empty<IdentifiedApworld>(), Array.Empty<string>(), new[] { "GitHub.com/A/DSR" }, "5eed");
        Assert.Equal(new[] { "1.0.0" }, Versions(picked));
    }

    [Fact]
    public void Nothing_is_tried_without_a_seed_a_trusted_project_or_room_to_try()
    {
        var known = new[] { V("1.0.0") };
        Assert.Empty(SeedVersionTries.Pick(known, Array.Empty<IdentifiedApworld>(), Array.Empty<string>(), new[] { Mine }, null));
        Assert.Empty(SeedVersionTries.Pick(known, Array.Empty<IdentifiedApworld>(), Array.Empty<string>(), Array.Empty<string>(), "5eed"));
        Assert.Empty(SeedVersionTries.Pick(known, Array.Empty<IdentifiedApworld>(), Array.Empty<string>(), new[] { Mine }, "5eed", max: 0));
    }
}
