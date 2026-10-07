using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.CheeseTracker;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Rooms;

/// <summary>
/// A room's status, as archipelago.gg's small status API gives it (api/room_status/&lt;id&gt;): the port the room last
/// ran on, its players, when it was last active and how long it stays up without activity. Reading it only reads;
/// Atlas never requests the room's own page, which would wake a sleeping room.
/// </summary>
public sealed class RoomStatus
{
    /// <summary>The port the room last ran on (0 when it hasn't run yet).</summary>
    public int Port { get; init; }

    /// <summary>The first team's player names, in slot order (a multiworld's slots).</summary>
    public IReadOnlyList<string> Players { get; init; } = Array.Empty<string>();

    public int Teams { get; init; }

    public DateTime? LastActivityUtc { get; init; }

    /// <summary>How long the room stays up without activity before it shuts down, in seconds.</summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>The room's status page: the only thing Atlas reads about a room.</summary>
    public static string Url(string site, string id) => $"{site}/api/room_status/{Uri.EscapeDataString(id)}";

    private static readonly JsonSerializerSettings Json = new() { MaxDepth = 16 };

    /// <summary>Reads the API's answer; null (with why) when it isn't a room's status.</summary>
    public static RoomStatus? Parse(string? json, out string? error)
    {
        error = null;
        JObject? root;
        try
        {
            root = string.IsNullOrWhiteSpace(json) ? null : JsonConvert.DeserializeObject<JObject>(json, Json);
        }
        catch (JsonException)
        {
            root = null;
        }
        if (root == null)
        {
            error = "The room's status couldn't be read.";
            return null;
        }
        var teams = root["players"] as JArray;
        var first = teams?.FirstOrDefault() as JArray;
        var players = first?.Select(name => name.Type == JTokenType.String ? (string?)name : null).Where(name => !string.IsNullOrEmpty(name)).Select(name => name!).ToList() ?? new List<string>();
        int port = root["last_port"]?.Type == JTokenType.Integer ? (int)root["last_port"]! : 0;
        DateTime? last = DateTime.TryParse((string?)root["last_activity"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
        int timeout = root["timeout"]?.Type == JTokenType.Integer ? (int)root["timeout"]! : 0;
        return new RoomStatus { Port = Math.Max(0, port), Players = players, Teams = teams?.Count ?? 0, LastActivityUtc = last, TimeoutSeconds = timeout };
    }

    /// <summary>Whether the room has shut down for want of activity (it wakes when someone opens its page).</summary>
    public bool IsAsleep(DateTime nowUtc) => LastActivityUtc is { } last && TimeoutSeconds > 0 && nowUtc - last > TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary>The server address a multiworld needs ("archipelago.gg:38281"), from the link's site and the room's port.</summary>
    public string ServerAddress(string site)
    {
        string host = Uri.TryCreate(site, UriKind.Absolute, out var uri) ? uri.Host : site;
        return Port > 0 ? $"{host}:{Port}" : host;
    }

    /// <summary>The port in a server address ("archipelago.gg:38281" → 38281; 0 when there is none).</summary>
    public static int PortOf(string? serverAddress)
    {
        if (string.IsNullOrWhiteSpace(serverAddress)) return 0;
        string text = serverAddress.Trim();
        int slash = text.IndexOf("://", StringComparison.Ordinal);
        if (slash >= 0) text = text[(slash + 3)..];
        int colon = text.LastIndexOf(':');
        return colon > 0 && int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : 0;
    }
}

/// <summary>Room links as the host shares them (https://archipelago.gg/room/&lt;id&gt;), and the one read Atlas makes about a room.</summary>
public static class RoomLinks
{
    public const string SiteName = "archipelago.gg";

    /// <summary>The room's site and id from its link; null (with why) for anything else, a tracker link included.</summary>
    public static (string Site, string Id)? Parse(string? text, out string? error)
    {
        var link = CtLink.Parse(text ?? "", null, out error);
        if (link == null) return null;
        if (link.Kind != CtLink.LinkKind.ApRoom)
        {
            error = "Paste the room's own link (…/room/…), the one the host shared.";
            return null;
        }
        return (link.Site, link.Id);
    }

    /// <summary>Reads the room's status (one small request through the polite client); null with why when it can't.</summary>
    public static async Task<(RoomStatus? Status, string? Error)> ReadAsync(string site, string id, CancellationToken ct = default)
    {
        var response = await PoliteHttp.GetAsync(RoomStatus.Url(site, id), SiteName, ct: ct).ConfigureAwait(false);
        if (!response.Ok) return (null, response.Message ?? "The room's status couldn't be read.");
        var status = RoomStatus.Parse(response.Text, out string? error);
        return (status, error);
    }
}
