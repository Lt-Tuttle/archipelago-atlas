using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core.Reports;

/// <summary>One line of a stack trace: the method, its type (module), the file's name (never its path) and the line.</summary>
public sealed record CrashFrame(string Function, string? Module, string? File, int? Line, bool InApp);

/// <summary>One exception of a crash: its type, its message and its frames (outermost call last, as .NET writes them).</summary>
public sealed record CrashException(string Type, string Message, IReadOnlyList<CrashFrame> Frames);

/// <summary>
/// A crash report as CrashGuard writes it (PortableData/logs/crash_*.txt): when, what Atlas was doing, and the exception
/// chain, read back into parts so a report can carry the code's names and nothing else.
/// </summary>
public sealed class CrashReport
{
    /// <summary>When the problem happened, in UTC, when the file says.</summary>
    public DateTime? WhenUtc { get; init; }

    /// <summary>What Atlas was doing ("Unhandled exception", "A background task failed without being checked").</summary>
    public string What { get; init; } = "";

    /// <summary>The exceptions, innermost first (the one that started it), as Sentry lists them.</summary>
    public IReadOnlyList<CrashException> Exceptions { get; init; } = Array.Empty<CrashException>();

    private static readonly Regex FrameLine = new(@"^\s+at (?<function>.+?)(?: in (?<file>.+?):line (?<line>\d+))?\s*$", RegexOptions.Compiled);
    private const string InnerStart = " ---> ";
    private const string InnerEnd = "--- End of inner exception stack trace ---";

    /// <summary>Reads a crash file's text.</summary>
    public static CrashReport Parse(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        int at = 0;
        DateTime? when = null;
        if (lines.Length > 0 && DateTimeOffset.TryParse(lines[0].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp))
        {
            when = stamp.UtcDateTime;
            at = 1;
        }
        string what = "";
        if (at < lines.Length && !LooksLikeException(lines[at]))
        {
            what = lines[at].Trim();
            at++;
        }
        while (at < lines.Length && lines[at].Trim().Length == 0) at++;
        return new CrashReport { WhenUtc = when, What = what, Exceptions = ParseExceptions(lines, at) };
    }

    private static bool LooksLikeException(string line) => line.Contains(": ", StringComparison.Ordinal) && line.TrimStart().IndexOf(' ') > line.TrimStart().IndexOf(':');

    /// <summary>The exception chain .NET writes: "Outer: message ---> Inner: message [frames] --- End of inner ... --- [frames]".</summary>
    private static IReadOnlyList<CrashException> ParseExceptions(string[] lines, int from)
    {
        var finished = new List<CrashException>();
        var stack = new Stack<(string Type, List<string> Message, List<CrashFrame> Frames)>();
        (string Type, List<string> Message, List<CrashFrame> Frames)? current = null;
        (string Type, List<string> Message, List<CrashFrame> Frames) Start(string header)
        {
            int colon = header.IndexOf(": ", StringComparison.Ordinal);
            string type = colon < 0 ? header.Trim() : header[..colon].Trim();
            string message = colon < 0 ? "" : header[(colon + 2)..].Trim();
            return (type, new List<string> { message }, new List<CrashFrame>());
        }
        CrashException Finish((string Type, List<string> Message, List<CrashFrame> Frames) e) =>
            new(e.Type, string.Join("\n", e.Message).Trim(), e.Frames);

        for (int i = from; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0 && current == null) continue;
            if (current == null)
            {
                current = Start(line);
                continue;
            }
            int inner = line.IndexOf(InnerStart, StringComparison.Ordinal);
            if (inner >= 0 && current.Value.Frames.Count == 0)
            {
                // The outer exception's message may end on this line, before the inner one starts.
                string before = line[..inner].Trim();
                if (before.Length > 0) current.Value.Message.Add(before);
                stack.Push(current.Value);
                current = Start(line[(inner + InnerStart.Length)..]);
                continue;
            }
            if (line.Trim() == InnerEnd)
            {
                finished.Add(Finish(current.Value));
                current = stack.Count > 0 ? stack.Pop() : null;
                continue;
            }
            var frame = FrameLine.Match(line);
            if (frame.Success)
            {
                current.Value.Frames.Add(ParseFrame(frame.Groups["function"].Value, frame.Groups["file"].Success ? frame.Groups["file"].Value : null,
                    frame.Groups["line"].Success ? int.Parse(frame.Groups["line"].Value, CultureInfo.InvariantCulture) : null));
                continue;
            }
            if (current.Value.Frames.Count == 0 && line.Trim().Length > 0) current.Value.Message.Add(line.Trim());
        }
        if (current != null) finished.Add(Finish(current.Value));
        while (stack.Count > 0) finished.Add(Finish(stack.Pop()));
        return finished;
    }

    private static CrashFrame ParseFrame(string function, string? file, int? line)
    {
        // "Namespace.Type.Method(args)": the type is everything before the last dot ahead of the arguments.
        int paren = function.IndexOf('(');
        string head = paren < 0 ? function : function[..paren];
        int dot = head.LastIndexOf('.');
        string? module = dot < 0 ? null : head[..dot];
        string name = (dot < 0 ? head : head[(dot + 1)..]) + (paren < 0 ? "" : function[paren..]);
        bool inApp = module == null || !(module.StartsWith("System", StringComparison.Ordinal) || module.StartsWith("Godot", StringComparison.Ordinal)
            || module.StartsWith("Newtonsoft", StringComparison.Ordinal) || module.StartsWith("Archipelago.MultiClient", StringComparison.Ordinal)
            || module.StartsWith("Microsoft", StringComparison.Ordinal) || module.StartsWith("MoonSharp", StringComparison.Ordinal));
        return new CrashFrame(name, module, file == null ? null : Path.GetFileName(file.Trim()), line, inApp);
    }
}
