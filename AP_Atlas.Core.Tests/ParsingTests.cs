using AP_Atlas.Core.CheeseTracker;
using AP_Atlas.Core.Spheres;

namespace AP_Atlas.Core.Tests;

public class YamlExclusionsTests
{
    [Fact]
    public void Block_lists_quotes_comments_and_unquoted_colons()
    {
        using var dir = new TempFolder();
        string path = dir.File("a.yaml");
        File.WriteAllText(path, "# comment\nname: Drew{number}\ngame: Dark Souls III\nDark Souls III:\n  exclude_locations:\n" +
            "    - \"FS: Coiled Sword - received from Siegward\"\n    - 'HWL: Broadsword #2'\n    - FS: Ashen Estus Flask\n    - Painted World\n" +
            "  priority_locations: []\n");

        var r = YamlExclusions.Read(path, "Dark Souls III", "Drew2");

        Assert.Null(r.Error);
        Assert.Equal(new[] { "FS: Coiled Sword - received from Siegward", "HWL: Broadsword #2", "FS: Ashen Estus Flask", "Painted World" }, r.Names);
    }

    [Fact]
    public void Weighted_games_flow_lists_and_several_players()
    {
        using var dir = new TempFolder();
        string path = dir.File("b.yaml");
        File.WriteAllText(path, "name: Alice\ngame:\n  A Link to the Past: 0\n  Dark Souls II: 50\nDark Souls II:\n  exclude_locations: [\"Things Betwixt: Chest\", 'Majula: Pot']\n" +
            "---\nname: Bob\ngame: Dark Souls II\nDark Souls II:\n  exclude_locations:\n  - Heide: Chest\n");

        Assert.Equal(new[] { "Things Betwixt: Chest", "Majula: Pot" }, YamlExclusions.Read(path, "Dark Souls II", "Alice").Names);
        Assert.Equal(new[] { "Heide: Chest" }, YamlExclusions.Read(path, "Dark Souls II", "Bob").Names);
        Assert.NotNull(YamlExclusions.Read(path, "Dark Souls II", "Carol").Error);
        Assert.NotNull(YamlExclusions.Read(dir.File("does-not-exist.yaml"), "X", "Y").Error);
    }
}

public class LinkParsingTests
{
    private const string TrackerId = "AbCdEfGhIjKlMnOpQrStUw";
    private const string RoomId = "QrStUvWxYzAbCdEfGhIjKg";

    public static TheoryData<string, CtLink.LinkKind?, string?> CheeseLinks()
    {
        string site = CheeseClient.DefaultInstance;
        return new TheoryData<string, CtLink.LinkKind?, string?>
        {
            { $"{site}/tracker/{TrackerId}", CtLink.LinkKind.CheeseTracker, $"{site}/tracker/{TrackerId}" },
            { $"cheesetrackers.theincrediblewheelofchee.se/tracker/{TrackerId}?x=1#top", CtLink.LinkKind.CheeseTracker, $"{site}/tracker/{TrackerId}" },
            { $"https://archipelago.gg/room/{TrackerId}", CtLink.LinkKind.ApRoom, $"https://archipelago.gg/room/{TrackerId}" },
            { $"https://archipelago.gg/tracker/{TrackerId}/0/3", CtLink.LinkKind.ApTracker, $"https://archipelago.gg/tracker/{TrackerId}" },
            { $"http://archipelago.gg/room/{TrackerId}", null, null },
            { "https://archipelago.gg/room/AbCdEfGhIjKlMnOpQrStU", null, null },
            { "https://archipelago.gg/room/AbCdEfGhIjKlMnOpQrStUz", null, null },
            { $"https://archipelago.gg/seed/{TrackerId}", null, null },
            { "not a link", null, null },
            { "", null, null }
        };
    }

    [Theory]
    [MemberData(nameof(CheeseLinks))]
    public void Cheese_Tracker_links_in_any_form_are_understood(string text, CtLink.LinkKind? kind, string? url)
    {
        var link = CtLink.Parse(text, CheeseClient.DefaultInstance, out string error);
        if (kind == null)
        {
            Assert.Null(link);
            Assert.NotNull(error);
        }
        else
        {
            Assert.NotNull(link);
            Assert.Equal(kind, link.Kind);
            Assert.Equal(url, link.Url);
        }
    }

    [Theory]
    [InlineData("https://spheretracker.de/room/cwEj7kIAtD566-NXUU1xj0vo2oPYBY3h", true)]
    [InlineData("https://spheretracker.de/room/cwEj7kIAtD566-NXUU1xj0vo2oPYBY3h/collected", true)]
    [InlineData("www.spheretracker.de/room/cwEj7kIAtD566-NXUU1xj0vo2oPYBY3h/", true)]
    [InlineData("http://spheretracker.de/room/cwEj7kIAtD566-NXUU1xj0vo2oPYBY3h/all", true)]
    [InlineData("https://spheretracker.de/room/cwEj7kIAtD566-NXUU1xj0vo2oPYBY3h/all?refresh=1", true)]
    [InlineData("https://spheretracker.de/dashboard", false)]
    [InlineData("https://spheretracker.de/auth/login?next=/settings", false)]
    [InlineData("https://spheretracker.de/room", false)]
    [InlineData("https://archipelago.gg/room/" + RoomId, false)]
    [InlineData("", false)]
    public void Only_spheretracker_room_links_are_taken_and_never_with_refresh(string text, bool isRoom)
    {
        string? got = SphereSite.Normalize(text, out string error, out string roomId);
        if (isRoom)
        {
            Assert.Equal("https://spheretracker.de/room/cwEj7kIAtD566-NXUU1xj0vo2oPYBY3h/all", got);
            Assert.Equal("cwEj7kIAtD566-NXUU1xj0vo2oPYBY3h", roomId);
        }
        else
        {
            Assert.Null(got);
            Assert.NotNull(error);
        }
    }
}
