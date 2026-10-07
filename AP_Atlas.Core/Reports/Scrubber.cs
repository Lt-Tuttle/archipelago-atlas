using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core.Reports;

/// <summary>
/// Takes what could name the user, their PC or their games out of text that leaves it: a crash report, the bundle for an
/// issue. The rules, in order: any Windows or UNC path keeps only its file name; web, e-mail and IP addresses, and a server
/// named with its port, become marks; long hex tokens (keys, hashes) become marks; the Windows user name, the servers of
/// the user's multiworlds and their slot names become marks, as whole words, whatever their case. The rules are plain and
/// the tests hold each one; nothing here is clever. What the scrubber doesn't know (a player's name in a chat line) is why
/// reports hold no chat, no log lines and no names of things, only what the code was doing.
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

    // A drive or UNC path: segments up to whitespace, quotes and the characters Windows refuses; a colon ends it, so
    // "File.cs:line 42" keeps its line.
    private static readonly Regex WindowsPath = new(@"(?<![\p{L}\p{N}_])(?:[A-Za-z]:[\\/]|\\\\[^\\/\s""'<>|*?:]+[\\/])(?:[^\\/\s""'<>|*?:]+[\\/])*[^\\/\s""'<>|*?:]*", RegexOptions.Compiled);
    private static readonly Regex WebUrl = new(@"\b(?:https?|wss?|ftp)://[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmailAddress = new(@"[\p{L}\p{N}._%+-]+@[\p{L}\p{N}.-]+\.\p{L}{2,}", RegexOptions.Compiled);
    private static readonly Regex Ipv4 = new(@"(?<![\p{N}.])(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?(?![\p{N}.])", RegexOptions.Compiled);
    // Four or more groups (a time of day has three), or a "::" form.
    private static readonly Regex Ipv6 = new(@"(?<![\p{L}\p{N}:])(?:(?:[0-9A-Fa-f]{1,4}:){4,7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4})*)::(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4})*)?|::(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4})*))(?![\p{L}\p{N}:])", RegexOptions.Compiled);
    private static readonly Regex HostWithPort = new(@"(?<![\p{L}\p{N}.-])(?:[\p{L}\p{N}-]+\.)+\p{L}{2,}:\d{2,5}(?![\p{N}])", RegexOptions.Compiled);
    private static readonly Regex HexToken = new(@"(?<![\p{L}\p{N}])[0-9A-Fa-f]{32,}(?![\p{L}\p{N}])", RegexOptions.Compiled);

    private readonly Regex? _words;

    /// <param name="userName">The Windows user name (null or short: not scrubbed as a word).</param>
    /// <param name="hosts">The servers of the user's multiworlds, as typed ("archipelago.gg:38281", "192.168.1.5").</param>
    /// <param name="names">The user's slot names.</param>
    public Scrubber(string? userName, IEnumerable<string>? hosts = null, IEnumerable<string>? names = null)
    {
        var groups = new List<string>();
        void Group(string mark, IEnumerable<string>? words)
        {
            var list = (words ?? Array.Empty<string>()).Select(w => w?.Trim() ?? "").Where(w => w.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(w => w.Length).Select(Regex.Escape).ToList();
            if (list.Count > 0) groups.Add($"(?<{mark[1..^1]}>{string.Join("|", list)})");
        }
        Group(UserMark, userName == null ? null : new[] { userName });
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

    /// <summary>The text with every rule applied.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string s = WindowsPath.Replace(text, match => PathMark + FileNameOf(match.Value));
        s = WebUrl.Replace(s, UrlMark);
        s = EmailAddress.Replace(s, EmailMark);
        s = Ipv4.Replace(s, IpMark);
        s = Ipv6.Replace(s, IpMark);
        s = HostWithPort.Replace(s, HostMark);
        s = HexToken.Replace(s, TokenMark);
        if (_words != null)
            s = _words.Replace(s, match => match.Groups["user"].Success ? UserMark : match.Groups["host"].Success ? HostMark : NameMark);
        return s;
    }

    /// <summary>The file at the end of a path, with a separator ("\File.cs"), or "" for a folder.</summary>
    private static string FileNameOf(string path)
    {
        int cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        string last = cut < 0 ? path : path[(cut + 1)..];
        return last.Length > 0 && last.Contains('.') ? "\\" + last : "";
    }
}
