using System.IO.Compression;
using System.Text;
using AP_Atlas.Core.PopTracker;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Tests;

/// <summary>"Start a pack": a PopTracker pack made from the game's names, every location a pin grouped by area, with its own autotracking.</summary>
public sealed class PackStarterTests
{
    private static readonly Dictionary<long, string> Locations = new()
    {
        [1] = "UP: Andre",
        [2] = "UP: Bell Key",
        [3] = "Route 218 - Item Ball",
        [4] = "Yoel/Yuria Reward",
        [5] = "UP: Andre", // two of the game's checks can share a name; each keeps its own pin
    };

    private static readonly Dictionary<long, string> Items = new() { [10] = "Bell Key", [11] = "Lift Chamber Key", [12] = "Bell Key!" };

    private static Dictionary<string, string> Texts(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        return archive.Entries.Where(e => e.Name.EndsWith(".json") || e.Name.EndsWith(".lua") || e.Name.EndsWith(".md"))
            .ToDictionary(e => e.FullName, e => new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd());
    }

    [Fact]
    public void Every_location_is_a_pin_in_its_area_on_the_one_map_with_its_mapping()
    {
        var started = PackStarter.Build("Test Game (made in Atlas)", "Test Game", Locations, Items, new long[] { 10, 99 });
        Assert.Equal(3, started.Areas); // UP, Route 218, Other
        var files = Texts(started.Zip);
        var areas = JArray.Parse(files["locations/locations.json"]);
        Assert.Equal(new[] { "Route 218", "UP", "Other" }, areas.Select(a => (string)a["name"]!));
        var up = areas.Single(a => (string)a["name"]! == "UP")["children"]!;
        Assert.Equal(new[] { "Andre", "Andre (2)", "Bell Key" }, up.Select(p => (string)p["name"]!));
        Assert.All(up, p => Assert.Equal(PackStarter.MapName, (string)p["map_locations"]![0]!["map"]!));
        // A "/" can't be in a path's part: it becomes "-", and the mapping says the same path.
        var other = areas.Single(a => (string)a["name"]! == "Other")["children"]!.Single();
        Assert.Equal("Yoel-Yuria Reward", (string)other["sections"]![0]!["name"]!);
        string locationMapping = files["scripts/autotracking/location_mapping.lua"];
        Assert.Contains("[4] = {\"@Other/Yoel-Yuria Reward/Yoel-Yuria Reward\"}", locationMapping);
        Assert.Contains("[3] = {\"@Route 218/Item Ball/Route 218 - Item Ball\"}", locationMapping);
        // The mapping is read as a pack's mapping is: every location keyed by its id.
        var read = LuaMappingReader.Read(locationMapping);
        Assert.Empty(read.Problems);
        Assert.Equal(5, read.Entries.Count);
    }

    [Fact]
    public void Every_item_is_in_the_pack_and_the_grid_holds_the_ones_given()
    {
        var started = PackStarter.Build("P", "Test Game", Locations, Items, new long[] { 11, 10 });
        var files = Texts(started.Zip);
        var items = JArray.Parse(files["items/items.json"]);
        Assert.Equal(3, items.Count);
        Assert.Equal(new[] { "bell_key", "bell_key_2", "lift_chamber_key" }, items.Select(i => (string)i["codes"]!).OrderBy(c => c));
        Assert.Equal(2, started.Tiles);
        var rows = JObject.Parse(files["layouts/tracker.json"]).SelectToken("$..rows")!;
        Assert.Equal(new[] { "lift_chamber_key", "bell_key" }, rows[0]!.Select(c => (string)c!));
        Assert.Contains("[11] = {{\"lift_chamber_key\", \"toggle\"}}", files["scripts/autotracking/item_mapping.lua"]);
        Assert.Contains("Archipelago:AddLocationHandler", files["scripts/autotracking/archipelago.lua"]);
        Assert.Equal("Test Game", (string)JObject.Parse(files["manifest.json"])["game_name"]!);
    }

    [Fact]
    public void The_map_is_a_picture_and_pins_sit_inside_it()
    {
        var started = PackStarter.Build("P", "Test Game", Locations, Items, Array.Empty<long>());
        using var archive = new ZipArchive(new MemoryStream(started.Zip));
        using var picture = new BinaryReader(archive.GetEntry("images/map.png")!.Open());
        var header = picture.ReadBytes(24);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, header.Take(4));
        int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        var pins = JArray.Parse(Texts(started.Zip)["locations/locations.json"]).SelectMany(a => a["children"]!).Select(p => p["map_locations"]![0]!).ToList();
        Assert.All(pins, p => Assert.InRange((double)p["x"]!, 0, width));
        Assert.All(pins, p => Assert.InRange((double)p["y"]!, 0, height));
        Assert.Equal(pins.Count, pins.Select(p => ((double)p["x"]!, (double)p["y"]!)).Distinct().Count());
    }
}
