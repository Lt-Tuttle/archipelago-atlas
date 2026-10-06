using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AP_Atlas.Core;

/// <summary>
/// Atlas's own documents, shipped inside it: the guide, what's new, the credits, the third-party notices and the
/// licence. They're embedded from the repository's files as Atlas is built (one source), and read as text here, never
/// from disk.
/// </summary>
public static class Docs
{
    public const string Guide = "GUIDE.md";
    public const string Changelog = "CHANGELOG.md";
    public const string Credits = "CREDITS.md";
    public const string ThirdPartyNotices = "THIRD_PARTY_NOTICES.md";
    public const string License = "LICENSE";

    /// <summary>Every document Atlas ships.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Guide, Changelog, Credits, ThirdPartyNotices, License };

    /// <summary>A document's text. A document that isn't built in is a build error, not a condition to handle.</summary>
    public static string Read(string name)
    {
        using var stream = typeof(Docs).Assembly.GetManifestResourceStream("Docs." + name)
            ?? throw new InvalidOperationException($"{name} isn't built into Atlas.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
