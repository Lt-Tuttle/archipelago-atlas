using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AP_Atlas.Core.Reports;

/// <summary>One part of a problem report, already scrubbed: its file name in the report, its title for the user, its text.</summary>
public sealed record ProblemPart(string Name, string Title, string Text)
{
    /// <summary>Its size as sent (UTF-8 bytes).</summary>
    public long Bytes => Encoding.UTF8.GetByteCount(Text);
}

/// <summary>
/// Help → Report a problem: the parts a report can hold (<see cref="Gather"/>: the system information, the end of the log,
/// the update log, the newest crash reports), every line through the <see cref="Scrubber"/>, for the user to look at and
/// tick; then sent (<see cref="ProblemReport"/>) or kept as a zip in Atlas's own folder (<see cref="Write(string,
/// IEnumerable{ProblemPart}, string, string, DateTime)"/>), with a note saying what it is.
/// </summary>
public static class ProblemBundle
{
    /// <summary>How much of the end of a log goes in.</summary>
    public const long LogTail = 512 * 1024;
    /// <summary>How many of the newest crash reports go in.</summary>
    public const int CrashFiles = 10;
    public const string FilePrefix = "atlas-report-";
    public const string NoteFile = "note.txt";

    /// <summary>Every part a report can hold, scrubbed, in the order they're shown.</summary>
    public static IReadOnlyList<ProblemPart> Gather(string logsDir, Scrubber scrubber, string systemInfo)
    {
        var parts = new List<ProblemPart> { new("system.txt", "System information", scrubber.Scrub(systemInfo)) };
        foreach (var (log, title) in new[] { ("atlas_log.txt", "The end of Atlas's log"), ("atlas_update_log.txt", "The update log") })
        {
            string logPath = Path.Combine(logsDir, log);
            if (File.Exists(logPath)) parts.Add(new ProblemPart(log, title, scrubber.Scrub(Tail(logPath, LogTail))));
        }
        if (Directory.Exists(logsDir))
        {
            foreach (string crash in Directory.GetFiles(logsDir, CrashReporter.CrashFilePattern).OrderByDescending(File.GetLastWriteTimeUtc).Take(CrashFiles))
                parts.Add(new ProblemPart(Path.GetFileName(crash), "Crash report " + Path.GetFileName(crash), scrubber.Scrub(File.ReadAllText(crash))));
        }
        return parts;
    }

    /// <summary>
    /// Writes the parts (and the user's note, already scrubbed) into <paramref name="reportsDir"/> and returns the zip's
    /// path. With <paramref name="reportId"/>, it's the copy of a report that was sent, and says so.
    /// </summary>
    public static string Write(string reportsDir, IEnumerable<ProblemPart> parts, string? note, string? reportId, DateTime nowLocal)
    {
        Directory.CreateDirectory(reportsDir);
        // A sent report's copy and a saved one can come in the same second: neither is written over.
        string stem = Path.Combine(reportsDir, FilePrefix + nowLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        string path = stem + ".zip";
        for (int n = 2; File.Exists(path); n++) path = $"{stem}-{n}.zip";
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var list = parts.ToList();
        string intro = reportId != null
            ? "This is a copy of the problem report The Archipelago Atlas sent to its developer as report " + reportId + ".\n" +
              "Mention that id in an issue at " + AtlasVersion.RepoUrl + "/issues so the developer can find it.\n\n"
            : "This is a problem report from The Archipelago Atlas, made for attaching to an issue at\n" + AtlasVersion.RepoUrl + "/issues\n\n";
        Add(zip, "README.txt",
            intro +
            "It holds " + string.Join(", ", list.Select(p => p.Title.ToLowerInvariant())) + (string.IsNullOrWhiteSpace(note) ? "" : ", and your note") + ".\n" +
            "Paths, web and e-mail addresses, server names, slot and player names were replaced by marks (<path>, <url>, <email>, <host>, <name>) before it was written.\n" +
            "Open the files and read them before you attach it; remove anything you'd rather not share.\n");
        foreach (var part in list) Add(zip, part.Name, part.Text);
        if (!string.IsNullOrWhiteSpace(note)) Add(zip, NoteFile, note.Trim() + "\n");
        return path;
    }

    /// <summary>Every part, scrubbed, as a zip for an issue (nothing sent).</summary>
    public static string Write(string reportsDir, string logsDir, Scrubber scrubber, string systemInfo, DateTime nowLocal) =>
        Write(reportsDir, Gather(logsDir, scrubber, systemInfo), null, null, nowLocal);

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    /// <summary>The last <paramref name="bytes"/> of a text file (from a line's start), read while the file may be in use.</summary>
    private static string Tail(string path, long bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long skip = Math.Max(0, stream.Length - bytes);
        stream.Position = skip;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string text = reader.ReadToEnd();
        if (skip == 0) return text;
        int newline = text.IndexOf('\n');
        return newline < 0 ? text : "[…]\n" + text[(newline + 1)..];
    }
}
