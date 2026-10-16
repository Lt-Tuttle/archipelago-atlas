using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.Tests;

/// <summary>Add games: the batch's one bar, which pack each game gets, where the list's packs come from, and what the summary reads.</summary>
public sealed class AddGamesTests
{
    private const long Mb = 1024 * 1024;
    private static readonly AddGamesProgress.GameWork Both = new(true, 2 * Mb, true, 4 * Mb);

    [Fact]
    public void The_bar_only_moves_forward_through_every_part_and_ends_full()
    {
        var bar = AddGamesProgress.For(new[] { Both, new AddGamesProgress.GameWork(false, 0, true, 0) }, engine: true, worldsDownloadBytes: 22 * Mb);
        var seen = new List<float>();
        foreach (string id in new[] { AddGamesProgress.ApworldPart(0), AddGamesProgress.EnginePart, AddGamesProgress.NamesPart, AddGamesProgress.PackPart(0), AddGamesProgress.PackPart(1), AddGamesProgress.DoctorPart })
        {
            seen.Add(bar.Enter(id).Fraction);
            seen.Add(bar.Within(0.5f));
            seen.Add(bar.Within(0.25f)); // a download starting over doesn't move the bar back
        }
        Assert.Equal(0f, seen[0]);
        Assert.Equal(seen.OrderBy(f => f), seen);
        Assert.Equal(40f / 41f, seen[^1], 3); // apworld 2, engine 8+22, names 2, packs 4+1, doctor 2: halfway into the Doctor's share
        Assert.Equal(1f, bar.Complete());
    }

    [Fact]
    public void A_game_s_part_says_its_place_and_a_part_for_every_game_says_none()
    {
        var bar = AddGamesProgress.For(new[] { Both, Both, Both }, engine: true, worldsDownloadBytes: 0);
        var third = bar.Enter(AddGamesProgress.PackPart(2));
        Assert.Equal((3, 3), (third.Game, third.Games));
        Assert.Equal(0, bar.Enter(AddGamesProgress.DoctorPart).Game);
        Assert.Equal(1, bar.Enter(AddGamesProgress.ApworldPart(0)).Game);
    }

    [Fact]
    public void Parts_weigh_their_download_size_and_an_unlisted_size_counts_a_megabyte()
    {
        var bar = AddGamesProgress.For(new[] { new AddGamesProgress.GameWork(true, 9 * Mb, true, 0) }, engine: false, worldsDownloadBytes: 0);
        Assert.Equal(3, bar.Parts); // no engine: no engine part, no names part
        Assert.Equal(9f / 11f, bar.Enter(AddGamesProgress.PackPart(0)).Fraction, 3);
    }

    [Fact]
    public void A_part_the_plan_lacks_and_progress_outside_a_part_leave_the_bar_alone()
    {
        var bar = AddGamesProgress.For(new[] { new AddGamesProgress.GameWork(false, 0, true, 0) }, engine: false, worldsDownloadBytes: 0);
        Assert.Equal(0f, bar.Within(0.9f));
        var missing = bar.Enter(AddGamesProgress.ApworldPart(0));
        Assert.Equal((0, 0f), (missing.Game, missing.Fraction));
        Assert.Equal(0f, bar.Within(0.9f));
        bar.Enter(AddGamesProgress.PackPart(0));
        Assert.Equal(0f, bar.Within(-1f));
        Assert.Equal(0f, bar.Within(float.NaN));
    }

    [Fact]
    public void A_listed_release_s_file_comes_from_its_release_with_its_hash()
    {
        var d = IndexPackDownloads.For("Owner/Pack", "v1.0+b/2", "My Pack.zip", " ABCD ", "https://github.com/", "https://api.github.com")!;
        Assert.Equal("https://github.com/Owner/Pack/releases/download/v1.0%2Bb/2/My%20Pack.zip", d.Url);
        Assert.Equal(("My Pack.zip", "abcd", false), (d.FileName, d.Sha256, d.Zipball));
    }

    [Fact]
    public void A_pack_listed_as_its_project_s_current_files_is_the_zipball_with_no_hash_to_check()
    {
        var d = IndexPackDownloads.For("owner/pack", "current files", "zipball", "abcd", "https://github.com", "https://api.github.com/")!;
        Assert.Equal(("https://api.github.com/repos/owner/pack/zipball", "owner_pack.zip", (string?)null, true), (d.Url, d.FileName, d.Sha256, d.Zipball));
    }

    [Theory]
    [InlineData(null, "v1", "p.zip")]
    [InlineData("owner", "v1", "p.zip")]
    [InlineData("owner/pack/extra", "v1", "p.zip")]
    [InlineData("owner/..", "v1", "p.zip")]
    [InlineData("owner/pack", "", "p.zip")]
    [InlineData("owner/pack", "v1", "")]
    [InlineData("owner/pack", "v1", "../p.zip")]
    [InlineData("owner/pack", "v1", "..")]
    [InlineData("owner/pack", "../..", "p.zip")]
    [InlineData("owner/pack", "a//b", "p.zip")]
    public void An_entry_that_doesn_t_plainly_name_a_project_release_and_file_gives_no_address(string? repo, string release, string asset) =>
        Assert.Null(IndexPackDownloads.For(repo, release, asset, null, "https://github.com", "https://api.github.com"));

    [Fact]
    public void The_outcome_counts_what_was_added_and_an_unfinished_part_takes_the_end_s_word()
    {
        var outcome = new AddGamesOutcome(new[] { ("A", true, true), ("B", true, false), ("C", false, true), ("a", false, false) });
        Assert.Equal(3, outcome.Games.Count); // a game named twice is one row
        outcome.For("a").Logic = AddStep.Done("1.0");
        outcome.For("A").Map = AddStep.Done("pack");
        outcome.For("B").Logic = AddStep.Failed("it couldn't be downloaded");
        outcome.Finish(AddStep.Cancelled);
        Assert.Equal((1, 1), (outcome.LogicAdded, outcome.MapsAdded));
        Assert.Equal(AddStepOutcome.NotNeeded, outcome.For("B").Map.Outcome);
        Assert.Equal(AddStepOutcome.NotNeeded, outcome.For("C").Logic.Outcome);
        Assert.Equal(AddStepOutcome.Cancelled, outcome.For("C").Map.Outcome);
        Assert.Equal(new[] { "B" }, outcome.Failures.Select(g => g.Game));
        Assert.Equal("it couldn't be downloaded", outcome.For("B").Logic.Detail);
        Assert.True(outcome.For("A").Ok);
        Assert.False(outcome.For("B").Ok);
        Assert.False(outcome.For("C").Ok);
        Assert.Throws<ArgumentException>(() => outcome.For("D"));
    }
}
