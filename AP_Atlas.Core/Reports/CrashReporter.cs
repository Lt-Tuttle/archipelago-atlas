using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AP_Atlas.Core.Reports;

/// <summary>
/// Sends the crash reports CrashGuard wrote, once the user says so: each file once, through PoliteHttp, scrubbed, as the
/// event <see cref="SentryEnvelope.Event"/> makes (the same text the user is shown first). A file that was sent, dismissed,
/// failed for good, tried <see cref="MaxAttempts"/> times or is older than <see cref="MaxAge"/> isn't offered again
/// (<see cref="StateFile"/> in the reports folder keeps which).
/// </summary>
public sealed class CrashReporter
{
    public const string StateFile = "crash_reports.json";
    public const string CrashFilePattern = "crash_*.txt";
    public const int MaxAttempts = 3;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    public const string SiteName = "Sentry";

    private readonly string _logsDir;
    private readonly string _reportsDir;
    private readonly SentryDsn _dsn;
    private readonly Scrubber _scrubber;
    private readonly ReportContext _context;

    public CrashReporter(string logsDir, string reportsDir, SentryDsn dsn, Scrubber scrubber, ReportContext context)
    {
        _logsDir = logsDir;
        _reportsDir = reportsDir;
        _dsn = dsn;
        _scrubber = scrubber;
        _context = context;
    }

    public SentryDsn Dsn => _dsn;

    public sealed class Entry
    {
        /// <summary>"sent", "dismissed" or "failed"; "" while it may still be tried.</summary>
        public string Status { get; set; } = "";
        public int Attempts { get; set; }
        // Shown nowhere; the wall clock is fine for a record.
        public DateTime? AtUtc { get; set; }
    }

    public sealed class State
    {
        public Dictionary<string, Entry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private string StatePath => Path.Combine(_reportsDir, StateFile);

    public State ReadState() => SafeFile.ReadJson(StatePath, () => new State());

    private void WriteState(State state)
    {
        Directory.CreateDirectory(_reportsDir);
        SafeFile.WriteJson(StatePath, state);
    }

    /// <summary>The crash files still to be offered, newest first (full paths).</summary>
    public IReadOnlyList<string> PendingFiles()
    {
        if (!Directory.Exists(_logsDir)) return Array.Empty<string>();
        var state = ReadState();
        var cutoff = DateTime.UtcNow - MaxAge; // wall clock: the files' times
        var pending = new List<(string Path, DateTime Written)>();
        foreach (string path in Directory.GetFiles(_logsDir, CrashFilePattern))
        {
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (written < cutoff) continue;
            if (state.Files.TryGetValue(Path.GetFileName(path), out var entry) && (entry.Status.Length > 0 || entry.Attempts >= MaxAttempts)) continue;
            pending.Add((path, written));
        }
        return pending.OrderByDescending(p => p.Written).Select(p => p.Path).ToList();
    }

    /// <summary>Reads a crash file into its parts.</summary>
    public CrashReport Read(string path) => CrashReport.Parse(File.ReadAllText(path));

    /// <summary>Exactly what would be sent for a file, as readable JSON, for the user to see first.</summary>
    public string Preview(string path, string? note) =>
        SentryEnvelope.Event("<event id>", Read(path), _context, note, _scrubber.Scrub, DateTime.UtcNow).ToString(Formatting.Indented); // wall clock: a time in a report

    /// <summary>
    /// Sends one file's report. The file is marked sent on success; a refusal that trying again can't mend marks it failed,
    /// any other failure counts an attempt.
    /// </summary>
    public async Task<WebResponse> SendAsync(string path, string? note, CancellationToken ct = default)
    {
        string eventId = SentryEnvelope.NewEventId();
        var report = Read(path);
        var @event = SentryEnvelope.Event(eventId, report, _context, note, _scrubber.Scrub, DateTime.UtcNow); // wall clock: a time in a report
        byte[] bytes = SentryEnvelope.Build(_dsn, eventId, @event, DateTime.UtcNow); // wall clock: a time in a report
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(SentryEnvelope.ContentType);
        var headers = new Dictionary<string, string> { ["X-Sentry-Auth"] = SentryEnvelope.AuthHeader(_dsn, _context.Release) };
        string site = PoliteHttp.NormalizeSite(_dsn.Site) ?? _dsn.Site;
        var response = await PoliteHttp.SendAsync(site, SiteName, HttpMethod.Post, _dsn.EnvelopePath, content, headers, accept: "application/json", ct: ct).ConfigureAwait(false);
        var state = ReadState();
        string name = Path.GetFileName(path);
        if (!state.Files.TryGetValue(name, out var entry)) state.Files[name] = entry = new Entry();
        entry.AtUtc = DateTime.UtcNow; // wall clock: a record
        if (response.Ok) entry.Status = "sent";
        else if (response.Outcome is WebOutcome.NotFound or WebOutcome.Unauthorized or WebOutcome.Forbidden or WebOutcome.Rejected or WebOutcome.Precondition) entry.Status = "failed";
        else entry.Attempts++;
        WriteState(state);
        if (response.Ok) Logger.LogInfo($"A crash report was sent ({name}).");
        else Logger.LogWarning($"A crash report couldn't be sent ({name}): {response.Message}");
        return response;
    }

    /// <summary>The user said no: these files aren't offered again.</summary>
    public void Dismiss(IEnumerable<string> paths)
    {
        var state = ReadState();
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            if (!state.Files.TryGetValue(name, out var entry)) state.Files[name] = entry = new Entry();
            entry.Status = "dismissed";
            entry.AtUtc = DateTime.UtcNow; // wall clock: a record
        }
        WriteState(state);
    }
}
