using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Reports;

/// <summary>
/// One fix the user made by hand, as a suggestion sees it: the kind ("location" for a pin section, "item" for a tile), what
/// the pack wrote ("Pin path / Section", "tile code (Item name)"), what the user chose (the game's name, or "not a check"),
/// what Atlas guessed for it and how sure it was, and the finding it answered.
/// </summary>
public sealed record FixPair(string Kind, string PackWrote, string Chose, string? AtlasGuess, double? AtlasScore, string? Finding);

/// <summary>
/// What "Suggest these fixes to Atlas" sends: the game and the version of its names, the pack (its name, version, project
/// and file's SHA-256), the fixes the user ticked, and their note.
/// </summary>
public sealed record FixSuggestion(string Game, string? ApworldVersion, string NamesSource, string PackName, string PackVersion, string? PackProject, string? PackSha256, IReadOnlyList<FixPair> Pairs, string? Note);

/// <summary>
/// Fix suggestions go to Atlas's developer through Sentry (its own project, apart from crash reports; the owner's choice,
/// 2026-10-14): one event naming the game and the pack, with the fixes as a JSON attachment (Sentry trims long lists in an
/// event's data, never an attachment's). Everything a person typed or a pack wrote goes through the scrubber; Atlas's own
/// facts (versions, the file's hash) don't. What the dialog shows is <see cref="Document"/>, exactly what's sent.
/// </summary>
public static class FixSuggestions
{
    /// <summary>The most fixes one suggestion carries.</summary>
    public const int MaxPairs = 500;

    /// <summary>The attachment's file name.</summary>
    public const string AttachmentName = "fixes.json";

    /// <summary>The attachment's format, for the review tool.</summary>
    public const string Format = "atlas-fix-suggestion/1";

    /// <summary>The suggestion as sent (the attachment), scrubbed: the dialog shows this text.</summary>
    public static JObject Document(FixSuggestion suggestion, string release, Func<string, string> scrub)
    {
        var pairs = new JArray();
        foreach (var pair in suggestion.Pairs.Take(MaxPairs))
        {
            var p = new JObject { ["kind"] = pair.Kind, ["pack_wrote"] = scrub(pair.PackWrote), ["chose"] = scrub(pair.Chose) };
            if (pair.AtlasGuess != null) p["atlas_guess"] = scrub(pair.AtlasGuess);
            if (pair.AtlasScore is double score) p["atlas_score"] = Math.Round(score, 3);
            if (pair.Finding != null) p["finding"] = pair.Finding;
            pairs.Add(p);
        }
        var document = new JObject
        {
            ["format"] = Format,
            ["atlas"] = release,
            ["game"] = scrub(suggestion.Game),
            ["names"] = scrub(suggestion.NamesSource),
            ["pack"] = new JObject
            {
                ["name"] = scrub(suggestion.PackName),
                ["version"] = scrub(suggestion.PackVersion),
                ["project"] = suggestion.PackProject == null ? null : scrub(suggestion.PackProject),
                ["sha256"] = suggestion.PackSha256
            },
            ["fixes"] = pairs
        };
        if (!string.IsNullOrWhiteSpace(suggestion.ApworldVersion)) document["apworld_version"] = scrub(suggestion.ApworldVersion);
        if (suggestion.Pairs.Count > MaxPairs) document["fixes_left_out"] = suggestion.Pairs.Count - MaxPairs;
        if (!string.IsNullOrWhiteSpace(suggestion.Note)) document["note"] = scrub(suggestion.Note.Trim());
        return document;
    }

    /// <summary>The event the attachment rides on: which game and pack, how many fixes, Atlas's version and the PC's kind.</summary>
    public static JObject Event(string eventId, FixSuggestion suggestion, ReportContext context, Func<string, string> scrub, DateTime nowUtc)
    {
        string game = scrub(suggestion.Game);
        return new JObject
        {
            ["event_id"] = eventId,
            ["timestamp"] = nowUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["platform"] = "other",
            ["level"] = "info",
            ["logger"] = "atlas.fixes",
            ["release"] = "atlas@" + context.Release,
            ["environment"] = context.Environment,
            ["sdk"] = new JObject { ["name"] = SentryEnvelope.ClientName, ["version"] = context.Release },
            ["message"] = new JObject { ["formatted"] = $"Pack fix suggestion: {game} ({scrub(suggestion.PackName)})" },
            // One issue per game on Sentry's side: the fixes for a game gather together.
            ["fingerprint"] = new JArray("pack-fix-suggestion", game),
            ["tags"] = new JObject
            {
                ["kind"] = "fix-suggestion",
                ["game"] = game,
                ["pack_project"] = suggestion.PackProject == null ? "unknown" : scrub(suggestion.PackProject),
            },
            ["extra"] = new JObject { ["fixes"] = Math.Min(suggestion.Pairs.Count, MaxPairs) }
        };
    }

    /// <summary>The envelope: the event, then the fixes as an attachment.</summary>
    public static byte[] Envelope(SentryDsn dsn, string eventId, JObject @event, JObject document, DateTime sentUtc)
    {
        byte[] eventBody = Encoding.UTF8.GetBytes(@event.ToString(Formatting.None));
        byte[] attachment = Encoding.UTF8.GetBytes(document.ToString(Formatting.Indented));
        var header = new JObject { ["event_id"] = eventId, ["sent_at"] = sentUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), ["dsn"] = dsn.ToString() };
        var eventItem = new JObject { ["type"] = "event", ["content_type"] = "application/json", ["length"] = eventBody.Length };
        var attachmentItem = new JObject { ["type"] = "attachment", ["filename"] = AttachmentName, ["content_type"] = "application/json", ["length"] = attachment.Length };
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.UTF8.GetBytes(header.ToString(Formatting.None) + "\n" + eventItem.ToString(Formatting.None) + "\n"));
        bytes.AddRange(eventBody);
        bytes.AddRange(Encoding.UTF8.GetBytes("\n" + attachmentItem.ToString(Formatting.None) + "\n"));
        bytes.AddRange(attachment);
        return bytes.ToArray();
    }

    /// <summary>Sends a suggestion through PoliteHttp (the user said yes in the dialog that showed <see cref="Document"/>).</summary>
    public static async Task<WebResponse> SendAsync(SentryDsn dsn, FixSuggestion suggestion, ReportContext context, Func<string, string> scrub, CancellationToken ct = default)
    {
        string eventId = SentryEnvelope.NewEventId();
        var now = DateTime.UtcNow; // wall clock: a time in a report
        var bytes = Envelope(dsn, eventId, Event(eventId, suggestion, context, scrub, now), Document(suggestion, context.Release, scrub), now);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(SentryEnvelope.ContentType);
        var headers = new Dictionary<string, string> { ["X-Sentry-Auth"] = SentryEnvelope.AuthHeader(dsn, context.Release) };
        string site = PoliteHttp.NormalizeSite(dsn.Site) ?? dsn.Site;
        var response = await PoliteHttp.SendAsync(site, CrashReporter.SiteName, HttpMethod.Post, dsn.EnvelopePath, content, headers, accept: "application/json", ct: ct).ConfigureAwait(false);
        if (response.Ok) Logger.LogInfo($"Pack fix suggestion sent ({suggestion.Pairs.Count} fixes for {suggestion.Game}).");
        else Logger.LogWarning($"A pack fix suggestion couldn't be sent: {response.Message}");
        return response;
    }
}
