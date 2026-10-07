using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Reports;

/// <summary>
/// Where reports go: Sentry's DSN ("https://key@host/project"), as the project's settings show it. Plain http only for a
/// site on this PC (the tests' fake).
/// </summary>
public sealed record SentryDsn(string Scheme, string Host, string PublicKey, string ProjectId)
{
    /// <summary>"https://host", as PoliteHttp names a site.</summary>
    public string Site => $"{Scheme}://{Host}";

    /// <summary>Where envelopes are posted.</summary>
    public string EnvelopePath => $"/api/{ProjectId}/envelope/";

    public static bool TryParse(string? text, out SentryDsn? dsn)
    {
        dsn = null;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) return false;
        string key = uri.UserInfo;
        if (key.Length == 0 || key.Contains(':') || !key.All(char.IsAsciiLetterOrDigit)) return false;
        string project = uri.AbsolutePath.Trim('/');
        if (project.Length == 0 || !project.All(char.IsAsciiDigit)) return false;
        dsn = new SentryDsn(uri.Scheme, uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}", key, project);
        return true;
    }

    public override string ToString() => $"{Scheme}://{PublicKey}@{Host}/{ProjectId}";
}

/// <summary>What every report says about the build and the PC: versions and kinds, never names.</summary>
public sealed record ReportContext(string Release, string Environment, string OsName, string OsVersion, string Runtime, string Godot, string Renderer, int Processors);

/// <summary>
/// Sentry's envelope, built by Atlas itself: one event, as documented at develop.sentry.dev, so what leaves is exactly what
/// <see cref="Event"/> makes and nothing an SDK would add (device names, users, breadcrumbs). Sent through PoliteHttp.
/// </summary>
public static class SentryEnvelope
{
    public const string ClientName = "atlas-reporter";
    public const string ContentType = "application/x-sentry-envelope";

    public static string NewEventId() => Guid.NewGuid().ToString("N");

    /// <summary>The X-Sentry-Auth header for a DSN.</summary>
    public static string AuthHeader(SentryDsn dsn, string clientVersion) =>
        $"Sentry sentry_version=7, sentry_client={ClientName}/{clientVersion}, sentry_key={dsn.PublicKey}";

    /// <summary>
    /// The event: the exceptions (innermost first) with the code's names, what Atlas was doing, the build and the PC's
    /// kind, and the user's note. Everything free-form goes through <paramref name="scrub"/>.
    /// </summary>
    public static JObject Event(string eventId, CrashReport report, ReportContext context, string? note, Func<string, string> scrub, DateTime nowUtc)
    {
        var values = new JArray();
        foreach (var exception in report.Exceptions)
        {
            var frames = new JArray();
            foreach (var frame in exception.Frames)
            {
                var f = new JObject { ["function"] = scrub(frame.Function), ["in_app"] = frame.InApp };
                if (frame.Module != null) f["module"] = scrub(frame.Module);
                if (frame.File != null) f["filename"] = scrub(frame.File);
                if (frame.Line != null) f["lineno"] = frame.Line;
                frames.Add(f);
            }
            var value = new JObject { ["type"] = scrub(exception.Type), ["value"] = scrub(exception.Message) };
            int dot = exception.Type.LastIndexOf('.');
            if (dot > 0) value["module"] = scrub(exception.Type[..dot]);
            if (frames.Count > 0) value["stacktrace"] = new JObject { ["frames"] = frames };
            values.Add(value);
        }
        string kind = report.What.Contains("background task", StringComparison.OrdinalIgnoreCase) ? "task" : report.What.Contains("closing", StringComparison.OrdinalIgnoreCase) ? "fatal" : "unhandled";
        var e = new JObject
        {
            ["event_id"] = eventId,
            ["timestamp"] = (report.WhenUtc ?? nowUtc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["platform"] = "csharp",
            ["level"] = kind == "fatal" ? "fatal" : "error",
            ["logger"] = "atlas",
            ["release"] = "atlas@" + context.Release,
            ["environment"] = context.Environment,
            ["sdk"] = new JObject { ["name"] = ClientName, ["version"] = context.Release },
            ["contexts"] = new JObject
            {
                ["os"] = new JObject { ["name"] = context.OsName, ["version"] = context.OsVersion },
                ["runtime"] = new JObject { ["name"] = ".NET", ["version"] = context.Runtime },
                ["app"] = new JObject { ["app_name"] = "The Archipelago Atlas", ["app_version"] = context.Release }
            },
            ["tags"] = new JObject { ["godot"] = context.Godot, ["renderer"] = context.Renderer, ["kind"] = kind },
            ["extra"] = new JObject { ["what"] = scrub(report.What), ["processors"] = context.Processors }
        };
        if (values.Count > 0) e["exception"] = new JObject { ["values"] = values };
        else e["message"] = new JObject { ["formatted"] = scrub(report.What) };
        if (!string.IsNullOrWhiteSpace(note)) e["extra"]!["note"] = scrub(note.Trim());
        return e;
    }

    /// <summary>The envelope's bytes: the header line, the item's header line and the event, UTF-8.</summary>
    public static byte[] Build(SentryDsn dsn, string eventId, JObject @event, DateTime sentUtc)
    {
        byte[] body = Encoding.UTF8.GetBytes(@event.ToString(Formatting.None));
        var header = new JObject { ["event_id"] = eventId, ["sent_at"] = sentUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), ["dsn"] = dsn.ToString() };
        var item = new JObject { ["type"] = "event", ["content_type"] = "application/json", ["length"] = body.Length };
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.UTF8.GetBytes(header.ToString(Formatting.None) + "\n" + item.ToString(Formatting.None) + "\n"));
        bytes.AddRange(body);
        return bytes.ToArray();
    }
}
