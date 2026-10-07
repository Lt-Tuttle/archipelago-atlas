using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AP_Atlas.Core.Testing;

/// <summary>
/// A small PopTracker map pack for tests, written as a real pack zip: a manifest, two tiles in its tracker's item grid
/// ("sword", and "shield" whose image can't be decoded), two maps ("World" with a 64×32 background, "Broken" whose background can't be decoded), and
/// two pins on World, one inside its image and one outside. Its PNGs are written here (real ones, any decoder reads
/// them), so it needs no Godot. It can also carry scripts (an init.lua, and more files).
/// </summary>
internal static class FakeMapPack
{
    /// <summary>The World map's background size.</summary>
    public const int MapWidth = 64, MapHeight = 32;

    /// <param name="initLua">The pack's scripts/init.lua, or null for a pack without scripts.</param>
    /// <param name="files">More files, by their path in the pack (e.g. "scripts/helper.lua").</param>
    /// <param name="binaryFiles">More files given as bytes (e.g. a crafted image).</param>
    /// <param name="mapWidth">The map image's size (a test that zooms and drags the map wants one bigger than the view).</param>
    public static void Write(string zipPath, string name, string game, string? initLua = null, IReadOnlyDictionary<string, string>? files = null,
        IReadOnlyDictionary<string, byte[]>? binaryFiles = null, int mapWidth = MapWidth, int mapHeight = MapHeight)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Text(string entry, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(text);
        }
        void Bytes(string entry, byte[] data)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(data);
        }
        Text("pack/manifest.json", $$"""{"name":"{{name}}","game_name":"{{game}}","package_version":"1.0","author":"Atlas tests"}""");
        Text("pack/items/items.json",
            """[{"name":"Sword","type":"toggle","img":"images/sword.png","codes":"sword"},{"name":"Shield","type":"toggle","img":"images/broken.png","codes":"shield"}]""");
        Text("pack/layouts/tracker.json", """{"tracker_default":{"type":"itemgrid","rows":[["sword","shield"]]}}""");
        Text("pack/maps/maps.json", """[{"name":"World","img":"images/world.png","location_size":16},{"name":"Broken","img":"images/broken.png"}]""");
        Text("pack/locations/locations.json",
            """[{"name":"Cave","sections":[{"name":"Chest"}],"map_locations":[{"map":"World","x":10,"y":10}]},""" +
            """{"name":"Far","sections":[{"name":"Chest"}],"map_locations":[{"map":"World","x":500,"y":10}]}]""");
        Bytes("pack/images/sword.png", Png(8, 8));
        Bytes("pack/images/world.png", Png(mapWidth, mapHeight));
        Bytes("pack/images/broken.png", new byte[] { 1, 2, 3, 4 });
        if (initLua != null) Text("pack/scripts/init.lua", initLua);
        foreach (var file in files ?? new Dictionary<string, string>()) Text("pack/" + file.Key, file.Value);
        foreach (var file in binaryFiles ?? new Dictionary<string, byte[]>()) Bytes("pack/" + file.Key, file.Value);
    }

    /// <summary>A PNG of one colour (Atlas purple), RGBA, 8 bits per channel.</summary>
    public static byte[] Png(int width, int height)
    {
        var rows = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            rows.WriteByte(0); // no filter
            for (int x = 0; x < width; x++) rows.Write(new byte[] { 0x8A, 0x2B, 0xE2, 0xFF });
        }
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) rows.WriteTo(zlib);

        var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new MemoryStream();
        WriteBigEndian(header, (uint)width);
        WriteBigEndian(header, (uint)height);
        header.Write(new byte[] { 8, 6, 0, 0, 0 }); // 8 bits, RGBA, deflate, adaptive filtering, not interlaced
        Chunk(png, "IHDR", header.ToArray());
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        byte[] typed = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        WriteBigEndian(png, (uint)data.Length);
        png.Write(typed);
        WriteBigEndian(png, Crc32(typed));
    }

    private static void WriteBigEndian(Stream stream, uint value) =>
        stream.Write(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

    /// <summary>The CRC-32 PNG chunks carry (ISO 3309, as zlib's).</summary>
    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
