using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core.Reports;

/// <summary>
/// Takes what could name the user, their PC or their games out of text that leaves it: a crash report, a problem report,
/// the bundle for an issue. The rules, in order: any Windows or UNC path keeps only its file name (folders with spaces in
/// their names, and a quoted path whole); web, e-mail and IP addresses, and a server named with its port, become marks
/// (a code file's line, "atlas_bridge.py:532", isn't a server); long hex tokens (keys, hashes) become marks; the Windows
/// user name and the profile folder's name, the servers, the slot and player names and the room passwords Atlas knows
/// become marks, as whole words, whatever their case. Each line is scrubbed on its own, and a line that takes too long is
/// left out rather than sent as it was. The rules are plain and the tests hold each one; nothing here is clever. A crash
/// report holds no log lines, chat or names of things, only what the code was doing; a problem report holds the end of
/// the log, which the user sees and ticks part by part before it goes.
/// </summary>
public sealed class Scrubber
{
    public const string UserMark = "<user>";
    public const string PathMark = "<path>";
    public const string UrlMark = "<url>";
    public const string EmailMark = "<email>";
    public const string IpMark = "<ip>";
    public const string HostMark = "<host>";
    public const string NameMark = "<name>";
    public const string TokenMark = "<token>";
    public const string SecretMark = "<secret>";
    /// <summary>What stands for a line the rules took too long over (it isn't sent as it was).</summary>
    public const string LeftOutMark = "<a line left out: it took too long to scrub>";

    // A drive or UNC path: segments up to whitespace, double quotes and the characters Windows refuses (an apostrophe is
    // allowed: "O'Brien"); a colon ends it, so "File.cs:line 42" keeps its line, and so does a ";" or "," that starts
    // another drive's path ("C:\a;D:\b" is two). A folder's name may hold single spaces ("C:\Users\Kim J\Documents\") when
    // its first word has no dot (so "foo.zip (3/5)" isn't taken for a folder); a file's name holds none (the rest of the
    // line would be taken for it).
    private const string SegChar = @"(?![;,][A-Za-z]:[\\/])[^\\/\s""<>|*?:]";
    private const string Segment = "(?:" + SegChar + ")+";
    private const string FolderWord = @"(?:(?![;,][A-Za-z]:[\\/])[^\\/\s""<>|*?:.])+";
    private static readonly Regex WindowsPath = new(@"(?<![\p{L}\p{N}_])(?:[A-Za-z]:[\\/]|\\\\" + Segment + @"[\\/])(?:" + Segment + @"[\\/]|" + FolderWord + "(?: " + Segment + @"){1,8}[\\/])*" + "(?:" + SegChar + ")*", RegexOptions.Compiled);
    // A quoted path, spaces and all, up to its closing quote: one not followed by more of a name, so an apostrophe inside
    // a single-quoted path ('C:\Users\Kim O'Brien\x.json') doesn't end it.
    private static readonly Regex QuotedPath = new(@"(?<q>[""'])(?<path>(?:[A-Za-z]:[\\/]|\\\\)[^""\r\n<>|*?]*?)\k<q>(?![\p{L}\p{N}_\\/])", RegexOptions.Compiled);
    private static readonly Regex WebUrl = new(@"\b(?:https?|wss?|ftp)://[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmailAddress = new(@"[\p{L}\p{N}._%+-]+@[\p{L}\p{N}.-]+\.\p{L}{2,}", RegexOptions.Compiled);
    private static readonly Regex Ipv4 = new(@"(?<![\p{N}.])(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?(?![\p{N}.])", RegexOptions.Compiled);
    // Four or more groups (a time of day has three), or a "::" form.
    private static readonly Regex Ipv6 = new(@"(?<![\p{L}\p{N}:])(?:(?:[0-9A-Fa-f]{1,4}:){4,7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4})*)::(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4})*)?|::(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4})*))(?![\p{L}\p{N}:])", RegexOptions.Compiled);
    // Not right after a path's backslash (a file in a folder); a code or data file's line ("bridge.py:532", one name and
    // its extension) is kept below.
    private static readonly Regex HostWithPort = new(@"(?<![\p{L}\p{N}.\\-])(?:[\p{L}\p{N}-]+\.)+(?<tld>\p{L}{2,}):\d{2,5}(?![\p{N}])", RegexOptions.Compiled);
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase) { "py", "gd", "cs", "lua", "json", "yaml", "yml", "txt", "log", "tscn", "tres" };
    private static readonly Regex HexToken = new(@"(?<![\p{L}\p{N}])[0-9A-Fa-f]{32,}(?![\p{L}\p{N}])", RegexOptions.Compiled);

    private readonly Regex? _words;

    /// <param name="userName">The Windows user name (null or short: not scrubbed as a word).</param>
    /// <param name="hosts">The servers Atlas knows, as typed ("archipelago.gg:38281", "192.168.1.5").</param>
    /// <param name="names">Slot and player names.</param>
    /// <param name="secrets">Room passwords (in memory only; never written anywhere by the scrubber).</param>
    /// <param name="userFolder">The name of the user's profile folder, when it differs from the user name.</param>
    public Scrubber(string? userName, IEnumerable<string>? hosts = null, IEnumerable<string>? names = null, IEnumerable<string>? secrets = null, string? userFolder = null)
    {
        var groups = new List<string>();
        void Group(string mark, IEnumerable<string?>? words)
        {
            var list = (words ?? Array.Empty<string>()).Select(w => w?.Trim() ?? "").Where(w => w.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(w => w.Length).Select(Regex.Escape).ToList();
            if (list.Count > 0) groups.Add($"(?<{mark[1..^1]}>{string.Join("|", list)})");
        }
        // The user's names whole, and each word of three letters or more on its own: what a path's rule leaves of a folder
        // name with a space in it ("C:\Users\Kim Jones" at a line's end) is still a mark.
        var userWords = new[] { userName, userFolder }.Where(w => !string.IsNullOrWhiteSpace(w))
            .SelectMany(w => new[] { w }.Concat(w!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(part => part.Length >= 3)));
        Group(UserMark, userWords);
        Group(SecretMark, secrets);
        Group(HostMark, (hosts ?? Array.Empty<string>()).SelectMany(HostParts));
        Group(NameMark, names);
        _words = groups.Count == 0 ? null : new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", groups)})(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>A server as typed, and its host alone ("archipelago.gg:38281" is also "archipelago.gg").</summary>
    private static IEnumerable<string> HostParts(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) yield break;
        string s = server.Trim();
        yield return s;
        if (Uri.TryCreate(s.Contains("://") ? s : "wss://" + s, UriKind.Absolute, out var uri) && uri.Host.Length >= 2) yield return uri.Host;
    }

    /// <summary>The text with every rule applied, line by line.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.IndexOf('\n') < 0) return ScrubLine(text);
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++) lines[i] = ScrubLine(lines[i]);
        return string.Join("\n", lines);
    }

    private string ScrubLine(string line)
    {
        if (line.Length == 0) return line;
        try
        {
            string s = QuotedPath.Replace(line, match => match.Groups["q"].Value + PathMark + FileNameOf(match.Groups["path"].Value) + match.Groups["q"].Value);
            s = WindowsPath.Replace(s, match => PathMark + FileNameOf(match.Value));
            s = WebUrl.Replace(s, UrlMark);
            s = EmailAddress.Replace(s, EmailMark);
            s = Ipv4.Replace(s, IpMark);
            s = Ipv6.Replace(s, IpMark);
            s = HostWithPort.Replace(s, match => FileExtensions.Contains(match.Groups["tld"].Value) && match.Value.IndexOf('.') == match.Value.LastIndexOf('.') ? match.Value : HostMark);
            s = HexToken.Replace(s, TokenMark);
            if (_words != null)
                s = _words.Replace(s, match => match.Groups["user"].Success ? UserMark : match.Groups["secret"].Success ? SecretMark : match.Groups["host"].Success ? HostMark : NameMark);
            return s;
        }
        catch (RegexMatchTimeoutException)
        {
            // Never sent as it was: a line the rules couldn't finish is left out.
            return LeftOutMark;
        }
    }

    /// <summary>The file at the end of a path, with a separator ("\File.cs"), or "" for a folder.</summary>
    private static string FileNameOf(string path)
    {
        string trimmed = path.TrimEnd();
        int cut = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        string last = cut < 0 ? trimmed : trimmed[(cut + 1)..];
        return last.Length > 0 && last.Contains('.') ? "\\" + last : "";
    }
}
