using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AP_Atlas.Core.Reports;

/// <summary>
/// Help → Report a problem: a zip in Atlas's own folder, for the user to attach to an issue themselves. It holds the end of
/// the log, the update log, the newest crash reports and the system information, every line through the
/// <see cref="Scrubber"/>, and a note saying what it is. Atlas uploads nothing.
/// </summary>
public static class ProblemBundle
{
    /// <summary>How much of the end of a log goes in.</summary>
    public const long LogTail = 512 * 1024;
    /// <summary>How many of the newest crash reports go in.</summary>
    public const int CrashFiles = 10;
    public const string FilePrefix = "atlas-report-";

    /// <summary>Writes the bundle into <paramref name="reportsDir"/> and returns its path.</summary>
    public static string Write(string reportsDir, string logsDir, Scrubber scrubber, string systemInfo, DateTime nowLocal)
    {
        Directory.CreateDirectory(reportsDir);
        string path = Path.Combine(reportsDir, FilePrefix + nowLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip");
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        Add(zip, "README.txt",
            "This is a problem report from The Archipelago Atlas, made for attaching to an issue at\n" + AtlasVersion.RepoUrl + "/issues\n\n" +
            "It holds the end of Atlas's log, the update log, the newest crash reports and the system information.\n" +
            "Paths, web and e-mail addresses, server names and slot names were replaced by marks (<path>, <url>, <email>, <host>, <name>) before it was written.\n" +
            "Open the files and read them before you attach it; remove anything you'd rather not share.\n");
        Add(zip, "system.txt", scrubber.Scrub(systemInfo));
        foreach (string log in new[] { "atlas_log.txt", "atlas_update_log.txt" })
        {
            string logPath = Path.Combine(logsDir, log);
            if (File.Exists(logPath)) Add(zip, log, scrubber.Scrub(Tail(logPath, LogTail)));
        }
        if (Directory.Exists(logsDir))
        {
            foreach (string crash in Directory.GetFiles(logsDir, CrashReporter.CrashFilePattern).OrderByDescending(File.GetLastWriteTimeUtc).Take(CrashFiles))
                Add(zip, Path.GetFileName(crash), scrubber.Scrub(File.ReadAllText(crash)));
        }
        return path;
    }

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
