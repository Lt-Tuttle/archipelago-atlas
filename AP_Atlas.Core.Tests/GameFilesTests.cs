using AP_Atlas.Core.Games;

namespace AP_Atlas.Core.Tests;

/// <summary>A game's own folder, the YAML library, and the apworld versions the picker offers.</summary>
public sealed class GameFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atlas-gamefiles-" + Guid.NewGuid().ToString("N"));

    public GameFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData("Dark Souls Remastered", "Dark Souls Remastered")]
    [InlineData("A Link to the Past: Redux?", "A Link to the Past_ Redux_")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("CON", "_CON")]
    [InlineData("", "_")]
    public void A_games_name_becomes_a_safe_folder_name(string game, string expected) => Assert.Equal(expected, GameFiles.SafeName(game));

    [Fact]
    public void A_YAML_names_its_games_whether_one_weighted_or_several_documents()
    {
        Assert.Equal(new[] { "Dark Souls Remastered" }, GameFiles.GamesOf("name: Me\ngame: Dark Souls Remastered\nDark Souls Remastered:\n  goal: 1\n"));
        Assert.Equal(new[] { "Hollow Knight", "Celeste" }, GameFiles.GamesOf("name: Me\ngame:\n  Hollow Knight: 50\n  Celeste: 25\n  Terraria: 0\n"));
        Assert.Equal(new[] { "Factorio", "Ocarina of Time" }, GameFiles.GamesOf("name: A\ngame: Factorio\n---\nname: B\ngame: Ocarina of Time\n"));
        Assert.Empty(GameFiles.GamesOf("just: text\n"));
    }

    [Fact]
    public void A_YAML_added_once_is_listed_under_every_game_it_names()
    {
        string source = Path.Combine(_dir, "mine.yaml");
        File.WriteAllText(source, "name: Me\ngame:\n  Hollow Knight: 1\n  Celeste: 1\n");
        string data = Path.Combine(_dir, "data");
        var library = YamlLibrary.Load(data);
        var (entry, problem) = library.Add(source);
        Assert.Null(problem);
        Assert.NotNull(entry);
        Assert.Equal(new[] { "Me" }, entry!.Players);
        Assert.Single(library.For("Hollow Knight"));
        Assert.Single(library.For("celeste"));
        Assert.Empty(library.For("Terraria"));
        // The same file again isn't copied twice; another file of the same name gets a new name.
        Assert.Same(entry, library.Add(source).Entry);
        string other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "mine.yaml"), "name: You\ngame: Celeste\n");
        var second = library.Add(Path.Combine(other, "mine.yaml")).Entry;
        Assert.Equal("mine (2).yaml", second!.File);
        Assert.Equal(2, YamlLibrary.Load(data).For("Celeste").Count);
        // A file that names no game is refused; a removed one is gone from the folder and the list.
        File.WriteAllText(Path.Combine(_dir, "notes.yaml"), "just: notes\n");
        Assert.NotNull(library.Add(Path.Combine(_dir, "notes.yaml")).Problem);
        library.Remove(second);
        Assert.False(File.Exists(Path.Combine(GameFiles.YamlsFolder(data), "mine (2).yaml")));
        Assert.Single(YamlLibrary.Load(data).For("Celeste"));
    }

    [Fact]
    public void Versions_order_by_their_numbers_with_a_release_after_its_prerelease()
    {
        var sorted = new[] { "1.9.2", "v1.10.0", "1.10.0-beta", "0.2.5", "nightly" }.OrderByDescending(v => v, ApworldChoices.VersionComparer.Instance).ToArray();
        Assert.Equal(new[] { "v1.10.0", "1.10.0-beta", "1.9.2", "0.2.5", "nightly" }, sorted);
    }

    [Theory]
    [InlineData("v0.2.5", "0.2.5")]
    [InlineData("0.0.22-0", "0.0.22.0")]
    [InlineData("0.1.0-0", "0.1.0.0")]
    [InlineData("1.0.0-beta", "1.0.0-beta")]
    [InlineData(" V2 ", "2")]
    [InlineData("vanilla", "vanilla")]
    public void One_label_names_one_release_as_the_index_and_GitHub_write_it(string label, string expected)
    {
        Assert.Equal(expected, ApworldChoices.NormalizeLabel(label));
        Assert.True(ApworldChoices.SameLabel(label, expected));
    }

    [Fact]
    public void A_build_number_is_a_release_not_a_prerelease_and_a_prerelease_sorts_before_its_release()
    {
        // The index's "0.0.22-0" is the release 0.0.22.0: the same release as 0.0.22, after 0.0.21, and not a pre-release of 0.0.22.
        Assert.Equal(0, ApworldChoices.VersionComparer.Instance.Compare("0.0.22-0", "0.0.22"));
        Assert.True(ApworldChoices.VersionComparer.Instance.Compare("0.0.22-0", "0.0.21") > 0);
        Assert.True(ApworldChoices.VersionComparer.Instance.Compare("0.0.22-beta", "0.0.22") < 0);
        Assert.Equal(0, ApworldChoices.VersionComparer.Instance.Compare("0.0.22-0", "v0.0.22.0"));
        // The picker's list carries whether a version is a pre-release and when it was published.
        var published = new[]
        {
            new PublishedApworld("1.3.0-beta", "github.com/fork/dsr", "https://github.com/fork/dsr/releases/download/1.3.0-beta/dsr.apworld", "cc", Prerelease: true, Published: new DateTime(2026, 9, 1)),
            new PublishedApworld("1.2.0", "github.com/fork/dsr", "https://github.com/fork/dsr/releases/download/1.2.0/dsr.apworld", "bb"),
        };
        var choices = ApworldChoices.Compose(published, Array.Empty<IdentifiedApworld>(), Array.Empty<string>(), null);
        Assert.Equal(new[] { "1.3.0-beta", "1.2.0" }, choices.Select(c => c.Version).ToArray());
        Assert.True(choices[0].Prerelease && choices[0].Published == new DateTime(2026, 9, 1) && !choices[1].Prerelease);
    }

    [Fact]
    public void The_picker_puts_the_seeds_match_first_then_the_newest_and_marks_what_Atlas_has()
    {
        var published = new[]
        {
            new PublishedApworld("0.2.4", "github.com/a/dsr", "https://github.com/a/dsr/releases/download/0.2.4/dsr.apworld", "aa"),
            new PublishedApworld("0.2.5", "github.com/a/dsr", "https://github.com/a/dsr/releases/download/0.2.5/dsr.apworld", "bb"),
            new PublishedApworld("0.3.0", "github.com/a/dsr", "https://github.com/a/dsr/releases/download/0.3.0/dsr.apworld", "cc"),
        };
        var identified = new[]
        {
            new IdentifiedApworld("0.2.5", "github.com/a/dsr 0.2.5", "C:/atlas/games/dsr/0.2.5/dsr.apworld", "f7f4d1e2aa"),
            new IdentifiedApworld("custom", "your file", "C:/atlas/games/dsr/yours/dsr.apworld", "0000"),
        };
        var choices = ApworldChoices.Compose(published, identified, new[] { "cc" }, "F7F4D1E2AA");
        Assert.Equal(new[] { "0.2.5", "0.3.0", "0.2.4", "custom" }, choices.Select(c => c.Version).ToArray());
        Assert.True(choices[0].MatchesSeed && choices[0].Downloaded);
        Assert.True(choices[1].Installed && !choices[1].Downloaded);
        Assert.True(choices[3].Downloaded && choices[3].Url == null && !choices[3].MatchesSeed);
    }
}
