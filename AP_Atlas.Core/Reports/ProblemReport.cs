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
/// Help → Report a problem, sent (the owner's choice, 2026-10-15): one event in the crash-report project, tagged
/// kind=problem-report with a short report id, the parts the user ticked as text attachments (Sentry trims long text in an
/// event, never in an attachment) and their note. Everything was scrubbed when the parts were gathered and the note when
/// it was typed; Atlas's own facts (versions, the PC's kind) aren't names. The dialog shows <see cref="Event"/> and every
/// part's text before anything goes, and the user presses Send each time: nothing here is ever sent by itself.
/// </summary>
public static class ProblemReport
{
    /// <summary>The most a report may carry (its parts' text), so a huge log can't make a send that fails or costs a lot.</summary>
    public const long MaxBytes = 4 * 1024 * 1024;

    /// <summary>The id the user is shown and can quote: the event's first eight hex digits.</summary>
    public static string IdOf(string eventId) => eventId[..8];

    /// <summary>How much the parts carry (UTF-8 bytes).</summary>
    public static long SizeOf(IEnumerable<ProblemPart> parts) => parts.Sum(p => p.Bytes);

    /// <summary>The event: which build and kind of PC, the report id, the parts' names and sizes, and the user's note.</summary>
    public static JObject Event(string eventId, IReadOnlyList<ProblemPart> parts, string? note, ReportContext context, DateTime createdUtc)
    {
        string id = IdOf(eventId);
        var e = new JObject
        {
            ["event_id"] = eventId,
            ["timestamp"] = createdUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["platform"] = "other",
            ["level"] = "warning",
            ["logger"] = "atlas.problem",
            ["release"] = "atlas@" + context.Release,
            ["environment"] = context.Environment,
            ["sdk"] = new JObject { ["name"] = SentryEnvelope.ClientName, ["version"] = context.Release },
            ["message"] = new JObject { ["formatted"] = "Problem report " + id },
            // Each report is its own issue on Sentry's side: two people's problems aren't one.
            ["fingerprint"] = new JArray("problem-report", eventId),
            ["contexts"] = new JObject
            {
                ["os"] = new JObject { ["name"] = context.OsName, ["version"] = context.OsVersion },
                ["runtime"] = new JObject { ["name"] = ".NET", ["version"] = context.Runtime },
                ["app"] = new JObject { ["app_name"] = "The Archipelago Atlas", ["app_version"] = context.Release }
            },
            ["tags"] = new JObject { ["kind"] = "problem-report", ["report_id"] = id, ["godot"] = context.Godot, ["renderer"] = context.Renderer },
            ["extra"] = new JObject
            {
                ["parts"] = new JArray(parts.Select(p => (object)$"{p.Name} ({p.Bytes} bytes)").ToArray()),
                ["processors"] = context.Processors
            }
        };
        if (!string.IsNullOrWhiteSpace(note)) e["extra"]!["note"] = note.Trim();
        return e;
    }

    /// <summary>The envelope: the event, then each part as a text attachment.</summary>
    public static byte[] Envelope(SentryDsn dsn, string eventId, JObject @event, IReadOnlyList<ProblemPart> parts, DateTime sentUtc)
    {
        var bytes = new List<byte>();
        void Item(JObject header, byte[] body)
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(header.ToString(Formatting.None) + "\n"));
            bytes.AddRange(body);
            bytes.AddRange(Encoding.UTF8.GetBytes("\n"));
        }
        var envelopeHeader = new JObject { ["event_id"] = eventId, ["sent_at"] = sentUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), ["dsn"] = dsn.ToString() };
        bytes.AddRange(Encoding.UTF8.GetBytes(envelopeHeader.ToString(Formatting.None) + "\n"));
        byte[] eventBody = Encoding.UTF8.GetBytes(@event.ToString(Formatting.None));
        Item(new JObject { ["type"] = "event", ["content_type"] = "application/json", ["length"] = eventBody.Length }, eventBody);
        foreach (var part in parts)
        {
            byte[] body = Encoding.UTF8.GetBytes(part.Text);
            Item(new JObject { ["type"] = "attachment", ["filename"] = part.Name, ["content_type"] = "text/plain", ["length"] = body.Length }, body);
        }
        return bytes.ToArray();
    }

    /// <summary>Sends a report the user saw and chose to send, through PoliteHttp.</summary>
    public static async Task<WebResponse> SendAsync(SentryDsn dsn, string eventId, IReadOnlyList<ProblemPart> parts, string? note, ReportContext context, DateTime createdUtc, CancellationToken ct = default)
    {
        var bytes = Envelope(dsn, eventId, Event(eventId, parts, note, context, createdUtc), parts, DateTime.UtcNow); // wall clock: a time in a report
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(SentryEnvelope.ContentType);
        var headers = new Dictionary<string, string> { ["X-Sentry-Auth"] = SentryEnvelope.AuthHeader(dsn, context.Release) };
        string site = PoliteHttp.NormalizeSite(dsn.Site) ?? dsn.Site;
        var response = await PoliteHttp.SendAsync(site, CrashReporter.SiteName, HttpMethod.Post, dsn.EnvelopePath, content, headers, accept: "application/json", ct: ct).ConfigureAwait(false);
        if (response.Ok) Logger.LogInfo($"A problem report was sent (report {IdOf(eventId)}, {parts.Count} part(s)).");
        else Logger.LogWarning($"A problem report couldn't be sent: {response.Message}");
        return response;
    }
}
