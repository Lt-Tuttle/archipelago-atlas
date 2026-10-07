using System;
using System.Threading.Tasks;
using AP_Atlas.Core.Rooms;
using AP_Atlas.Core.Testing;
using Xunit;

namespace AP_Atlas.Core.Tests;

public class RoomStatusTests
{
    private const string Id = "AbCdEfGhIjKlMnOpQrStUw";
    private const string Answer = """{"tracker": "TrAcKeRiDaBcDeFgHiJkLm", "players": [["Alice", "Bob"], ["Carol"]], "last_port": 38281, "last_activity": "Tue, 06 Oct 2026 10:00:00 GMT", "timeout": 7200, "downloads": []}""";

    [Fact]
    public void The_status_page_is_read_never_the_rooms_page()
    {
        string url = RoomStatus.Url("https://archipelago.gg", Id);
        Assert.Equal("https://archipelago.gg/api/room_status/" + Id, url);
        Assert.DoesNotContain("/room/", url, StringComparison.Ordinal);
    }

    [Fact]
    public void The_answer_gives_the_port_the_first_teams_players_and_the_activity()
    {
        var status = RoomStatus.Parse(Answer, out string? error);
        Assert.Null(error);
        Assert.NotNull(status);
        Assert.Equal(38281, status!.Port);
        Assert.Equal(new[] { "Alice", "Bob" }, status.Players);
        Assert.Equal(2, status.Teams);
        Assert.Equal(7200, status.TimeoutSeconds);
        Assert.Equal(new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc), status.LastActivityUtc);
        Assert.Equal("archipelago.gg:38281", status.ServerAddress("https://archipelago.gg"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void Anything_else_is_refused_with_why(string json)
    {
        Assert.Null(RoomStatus.Parse(json, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void A_room_without_a_port_yet_gives_the_host_alone()
    {
        var status = RoomStatus.Parse("""{"players": [], "last_port": -1, "last_activity": null, "timeout": 7200}""", out _)!;
        Assert.Equal(0, status.Port);
        Assert.Equal("archipelago.gg", status.ServerAddress("https://archipelago.gg"));
        Assert.Null(status.LastActivityUtc);
        Assert.False(status.IsAsleep(DateTime.UtcNow));
    }

    [Fact]
    public void A_room_is_asleep_once_its_timeout_has_passed_since_its_last_activity()
    {
        var status = RoomStatus.Parse(Answer, out _)!;
        var last = status.LastActivityUtc!.Value;
        Assert.False(status.IsAsleep(last.AddSeconds(7199)));
        Assert.True(status.IsAsleep(last.AddSeconds(7201)));
    }

    [Theory]
    [InlineData("archipelago.gg:38281", 38281)]
    [InlineData("wss://archipelago.gg:38281", 38281)]
    [InlineData("archipelago.gg", 0)]
    [InlineData("", 0)]
    [InlineData("host:notaport", 0)]
    public void The_port_of_a_server_address(string address, int port) => Assert.Equal(port, RoomStatus.PortOf(address));

    [Fact]
    public void Only_a_rooms_own_link_is_taken()
    {
        var room = RoomLinks.Parse("https://archipelago.gg/room/" + Id, out string? error);
        Assert.Null(error);
        Assert.Equal(("https://archipelago.gg", Id), room);
        Assert.Null(RoomLinks.Parse("https://archipelago.gg/tracker/" + Id, out error));
        Assert.Contains("room", error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(RoomLinks.Parse("https://archipelago.gg/room/short", out error));
        Assert.Null(RoomLinks.Parse("", out error));
    }

    [Fact]
    public async Task The_status_is_read_from_the_site_with_one_request()
    {
        await using var site = new FakeWebSite();
        site.Respond = path => path == "/api/room_status/" + Id ? (200, Answer) : (404, "");
        var room = RoomLinks.Parse(site.Site + "/room/" + Id, out _)!.Value;
        var (status, error) = await RoomLinks.ReadAsync(room.Site, room.Id, TestContext.Current.CancellationToken);
        Assert.Null(error);
        Assert.Equal(38281, status!.Port);
        Assert.Equal(new[] { "/api/room_status/" + Id }, site.Requests);
        // A site that answers with something else: no status, a reason, still no request for the room's page.
        site.Respond = _ => (500, "");
        (status, error) = await RoomLinks.ReadAsync(room.Site, room.Id, TestContext.Current.CancellationToken);
        Assert.Null(status);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.DoesNotContain(site.Requests, path => path.StartsWith("/room/", StringComparison.Ordinal));
    }
}
