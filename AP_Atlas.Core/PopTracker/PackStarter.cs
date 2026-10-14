using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.PopTracker;

/// <summary>What a started pack holds, said back to the user.</summary>
public sealed record StartedPack(byte[] Zip, int Areas, int Locations, int Items, int Tiles, string MapName);

/// <summary>
/// "Start a pack" for a game no map pack exists for (the owner's choice, 2026-10-14): a real PopTracker pack made from the
/// game's own names, so it works in Atlas and in PopTracker alike and can be shared. Every location is a pin of its own,
/// grouped by its area (the name's tag: "UP: Andre" is in "UP"), laid out area by area on one map (a plain picture Atlas
/// draws, or the user's own); every item is in the pack, the ones given go in the Key Items grid; the pack's own
/// autotracking scripts (location_mapping.lua, item_mapping.lua and the usual Archipelago handlers) mark checks and items as
/// the room reports them. The user then moves pins onto a real map with the same tools as any pack.
/// </summary>
public static class PackStarter
{
    public const string MapName = "All checks";
    public const string OtherArea = "Other";

    /// <summary>Pixels between pins, and around an area's block.</summary>
    private const int Spacing = 32, Pad = 24, Columns = 12, BlockColumns = 4;

    /// <summary>
    /// Builds the pack's zip. <paramref name="locations"/> and <paramref name="items"/> are the game's names (id → name);
    /// <paramref name="gridItems"/> the items Key Items shows (an empty grid when none). <paramref name="picture"/> is the
    /// map's picture (PNG or JPEG bytes and its size), or null for a plain one.
    /// </summary>
    public static StartedPack Build(string packName, string game, IReadOnlyDictionary<long, string> locations, IReadOnlyDictionary<long, string> items,
        IReadOnlyCollection<long> gridItems, (byte[] Bytes, string Extension, int Width, int Height)? picture = null)
    {
        // Areas and their pins: each location a pin named for the rest of its name, its section the full name.
        var areas = locations.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .GroupBy(kv => AreaLabel(kv.Value), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key == OtherArea ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Area: Safe(g.Key), Pins: g.ToList()))
            .ToList();

        // Where each area's block goes: blocks in columns, each block rows of pins.
        int blockWidth = Pad * 2 + Columns * Spacing;
        int Rows(int count) => (count + Columns - 1) / Columns;
        int BlockHeight(int count) => Pad * 2 + Rows(count) * Spacing;
        var columnHeights = new int[BlockColumns];
        var blocks = new List<(int X, int Y, int W, int H)>();
        foreach (var area in areas)
        {
            int column = Array.IndexOf(columnHeights, columnHeights.Min());
            blocks.Add((column * (blockWidth + Pad) + Pad, columnHeights[column] + Pad, blockWidth, BlockHeight(area.Pins.Count)));
            columnHeights[column] += BlockHeight(area.Pins.Count) + Pad;
        }
        int width = BlockColumns * (blockWidth + Pad) + Pad, height = Math.Max(columnHeights.Max() + Pad, 200);
        float scaleX = 1, scaleY = 1;
        string mapImage = "images/map.png";
        byte[] mapBytes;
        if (picture is { } own)
        {
            // The user's picture: the layout is fitted onto it (they move the pins to where things are).
            scaleX = own.Width / (float)width;
            scaleY = own.Height / (float)height;
            float scale = Math.Min(scaleX, scaleY);
            scaleX = scaleY = scale;
            mapImage = "images/map" + own.Extension.ToLowerInvariant();
            mapBytes = own.Bytes;
        }
        else mapBytes = MapPicture(width, height, blocks);

        var locationJson = new JArray();
        var locationMapping = new StringBuilder("LOCATION_MAPPING = {\n");
        for (int a = 0; a < areas.Count; a++)
        {
            var (area, pins) = areas[a];
            var block = blocks[a];
            var children = new JArray();
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < pins.Count; i++)
            {
                var (id, name) = (pins[i].Key, pins[i].Value);
                string pinName = Unique(Safe(PinLabel(name)), usedNames);
                string section = Safe(name);
                float x = (block.X + Pad + (i % Columns) * Spacing + Spacing / 2f) * scaleX;
                float y = (block.Y + Pad + (i / Columns) * Spacing + Spacing / 2f) * scaleY;
                children.Add(new JObject
                {
                    ["name"] = pinName,
                    ["sections"] = new JArray(new JObject { ["name"] = section, ["item_count"] = 1 }),
                    ["map_locations"] = new JArray(new JObject { ["map"] = MapName, ["x"] = Math.Round(x), ["y"] = Math.Round(y) })
                });
                locationMapping.Append(CultureInfo.InvariantCulture, $"    [{id}] = {{{Lua($"@{area}/{pinName}/{section}")}}},\n");
            }
            locationJson.Add(new JObject { ["name"] = area, ["children"] = children });
        }
        locationMapping.Append("}\n");

        var itemJson = new JArray();
        var itemMapping = new StringBuilder("ITEM_MAPPING = {\n");
        var codes = new Dictionary<long, string>();
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in items.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            string code = Unique(Code(name), usedCodes, "_");
            codes[id] = code;
            itemJson.Add(new JObject { ["name"] = name, ["type"] = "toggle", ["img"] = "images/item.png", ["codes"] = code });
            itemMapping.Append(CultureInfo.InvariantCulture, $"    [{id}] = {{{{{Lua(code)}, \"toggle\"}}}},\n");
        }
        itemMapping.Append("}\n");
        var grid = gridItems.Where(codes.ContainsKey).Select(id => codes[id]).Distinct().ToList();
        var rows = new JArray(grid.Chunk(10).Select(row => new JArray(row)));

        var layout = new JObject
        {
            ["tracker_default"] = new JObject
            {
                ["type"] = "container",
                ["content"] = new JObject
                {
                    ["type"] = "dock",
                    ["content"] = new JArray(
                        new JObject
                        {
                            ["type"] = "dock",
                            ["dock"] = "left",
                            ["content"] = new JObject { ["type"] = "group", ["header"] = "Items", ["content"] = new JObject { ["type"] = "itemgrid", ["item_margin"] = "2", ["rows"] = rows } }
                        },
                        new JObject { ["type"] = "map", ["maps"] = new JArray(MapName) })
                }
            }
        };
        var manifest = new JObject
        {
            ["name"] = packName,
            ["game_name"] = game,
            ["package_uid"] = Code(packName),
            ["package_version"] = "1.0.0",
            ["platform"] = "pc",
            ["author"] = "Made with The Archipelago Atlas",
        };
        var maps = new JArray(new JObject { ["name"] = MapName, ["img"] = mapImage, ["location_size"] = 18, ["location_border_thickness"] = 2 });

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Text(string name, string text)
            {
                using var entry = zip.CreateEntry(name).Open();
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                entry.Write(bytes, 0, bytes.Length);
            }
            void Bytes(string name, byte[] bytes)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(bytes, 0, bytes.Length);
            }
            Text("manifest.json", manifest.ToString(Formatting.Indented));
            Text("items/items.json", itemJson.ToString(Formatting.Indented));
            Text("maps/maps.json", maps.ToString(Formatting.Indented));
            Text("locations/locations.json", locationJson.ToString(Formatting.Indented));
            Text("layouts/tracker.json", layout.ToString(Formatting.Indented));
            Text("scripts/init.lua", InitLua);
            Text("scripts/autotracking.lua", AutotrackingLua);
            Text("scripts/autotracking/archipelago.lua", ArchipelagoLua);
            Text("scripts/autotracking/location_mapping.lua", locationMapping.ToString());
            Text("scripts/autotracking/item_mapping.lua", itemMapping.ToString());
            Bytes(mapImage, mapBytes);
            Bytes("images/item.png", ItemPicture());
            Text("README.md", $"# {packName}\n\nA map pack for {game}, started with The Archipelago Atlas from the game's own names: every location is a pin, grouped by area on the map \"{MapName}\". Move the pins onto a real map, give the tiles pictures, and share it.\n");
        }
        return new StartedPack(stream.ToArray(), areas.Count, locations.Count, items.Count, grid.Count, MapName);
    }

    // ---- Names ----

    /// <summary>A location's area as written ("UP: Andre" → "UP", "Route 218 - Item" → "Route 218"), or <see cref="OtherArea"/>.</summary>
    public static string AreaLabel(string name)
    {
        int colon = name.IndexOf(':');
        if (colon > 0 && colon <= 24) return name[..colon].Trim();
        int dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && dash <= 40) return name[..dash].Trim();
        return OtherArea;
    }

    /// <summary>The rest of a location's name after its area ("UP: Andre" → "Andre").</summary>
    private static string PinLabel(string name)
    {
        int colon = name.IndexOf(':');
        if (colon > 0 && colon <= 24 && colon + 1 < name.Length) return name[(colon + 1)..].Trim();
        int dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && dash <= 40) return name[(dash + 3)..].Trim();
        return name.Trim();
    }

    /// <summary>A name PopTracker's paths can carry: no "/" (it separates a path's parts).</summary>
    private static string Safe(string name) => name.Replace('/', '-').Replace('\\', '-').Trim();

    /// <summary>An item's code: lower case, letters and digits, "_" between words.</summary>
    private static string Code(string name)
    {
        var code = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
            code.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        string squeezed = string.Join("_", code.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        return squeezed.Length == 0 ? "item" : squeezed;
    }

    private static string Unique(string name, HashSet<string> used, string join = " ")
    {
        string unique = name;
        for (int n = 2; !used.Add(unique); n++) unique = join == " " ? $"{name} ({n})" : $"{name}{join}{n}";
        return unique;
    }

    /// <summary>A Lua string literal.</summary>
    private static string Lua(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    // ---- The scripts (PopTracker's Archipelago autotracking, written for these packs) ----

    private const string InitLua = """
        Tracker:AddItems("items/items.json")
        Tracker:AddMaps("maps/maps.json")
        Tracker:AddLocations("locations/locations.json")
        Tracker:AddLayouts("layouts/tracker.json")
        ScriptHost:LoadScript("scripts/autotracking.lua")
        """;

    private const string AutotrackingLua = """
        ScriptHost:LoadScript("scripts/autotracking/item_mapping.lua")
        ScriptHost:LoadScript("scripts/autotracking/location_mapping.lua")
        ScriptHost:LoadScript("scripts/autotracking/archipelago.lua")
        """;

    private const string ArchipelagoLua = """
        -- Marks the room's checks and items on the pack (PopTracker's Archipelago autotracking).
        function onClear(slot_data)
            for _, paths in pairs(LOCATION_MAPPING) do
                for _, path in ipairs(paths) do
                    local section = Tracker:FindObjectForCode(path)
                    if section then section.AvailableChestCount = section.ChestCount end
                end
            end
            for _, entries in pairs(ITEM_MAPPING) do
                for _, entry in ipairs(entries) do
                    local item = Tracker:FindObjectForCode(entry[1])
                    if item then item.Active = false end
                end
            end
        end

        function onItem(index, item_id, item_name, player_number)
            local entries = ITEM_MAPPING[item_id]
            if not entries then return end
            for _, entry in ipairs(entries) do
                local item = Tracker:FindObjectForCode(entry[1])
                if item then item.Active = true end
            end
        end

        function onLocation(location_id, location_name)
            local paths = LOCATION_MAPPING[location_id]
            if not paths then return end
            for _, path in ipairs(paths) do
                local section = Tracker:FindObjectForCode(path)
                if section and section.AvailableChestCount > 0 then section.AvailableChestCount = section.AvailableChestCount - 1 end
            end
        end

        Archipelago:AddClearHandler("clear handler", onClear)
        Archipelago:AddItemHandler("item handler", onItem)
        Archipelago:AddLocationHandler("location handler", onLocation)
        """;

    // ---- Pictures (PNG, written here: nothing to ship) ----

    /// <summary>The plain map: a dark ground with a lighter block per area.</summary>
    private static byte[] MapPicture(int width, int height, List<(int X, int Y, int W, int H)> blocks)
    {
        var ground = new byte[] { 0x26, 0x26, 0x2E, 0xFF };
        var block = new byte[] { 0x34, 0x34, 0x40, 0xFF };
        var edge = new byte[] { 0x48, 0x48, 0x58, 0xFF };
        return Png(width, height, (x, y) =>
        {
            foreach (var b in blocks)
            {
                if (x < b.X || x >= b.X + b.W || y < b.Y || y >= b.Y + b.H) continue;
                bool border = x == b.X || x == b.X + b.W - 1 || y == b.Y || y == b.Y + b.H - 1;
                return border ? edge : block;
            }
            return ground;
        });
    }

    /// <summary>The tiles' picture until the user gives them their own: a grey disc.</summary>
    private static byte[] ItemPicture() => Png(32, 32, (x, y) =>
    {
        double dx = x - 15.5, dy = y - 15.5, d = Math.Sqrt(dx * dx + dy * dy);
        return d <= 13 ? new byte[] { 0x8A, 0x8A, 0x96, 0xFF } : d <= 14.5 ? new byte[] { 0x5A, 0x5A, 0x66, 0xFF } : new byte[] { 0, 0, 0, 0 };
    });

    /// <summary>A PNG, RGBA, 8 bits per channel, each pixel from <paramref name="pixel"/>.</summary>
    public static byte[] Png(int width, int height, Func<int, int, byte[]> pixel)
    {
        var rows = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            rows.WriteByte(0);
            for (int x = 0; x < width; x++) rows.Write(pixel(x, y), 0, 4);
        }
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) rows.WriteTo(zlib);
        var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new MemoryStream();
        BigEndian(header, (uint)width);
        BigEndian(header, (uint)height);
        header.Write(new byte[] { 8, 6, 0, 0, 0 });
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
        BigEndian(png, (uint)data.Length);
        png.Write(typed);
        BigEndian(png, Crc32(typed));
    }

    private static void BigEndian(Stream stream, uint value) => stream.Write(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

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
