using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AP_Atlas.Core;

/// <summary>
/// Atlas's own documents, shipped inside it: the guide, what's new, the credits, the third-party notices, the
/// licence and Godot's copyright file. They're embedded from the repository's files as Atlas is built (one source), and
/// read as text here, never from disk.
/// </summary>
public static class Docs
{
    public const string Guide = "GUIDE.md";
    public const string Changelog = "CHANGELOG.md";
    public const string Credits = "CREDITS.md";
    public const string ThirdPartyNotices = "THIRD_PARTY_NOTICES.md";
    public const string License = "LICENSE";
    /// <summary>
    /// Godot's own copyright file for the engine version Atlas runs on: every component Godot bundles with its licence, then
    /// the licence texts. It ships with every release and is refreshed from Godot's tag when Godot is updated; the self-test
    /// checks it names every component and licence the engine reports.
    /// </summary>
    public const string GodotCopyright = "GODOT_COPYRIGHT.txt";

    /// <summary>Every document Atlas ships.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Guide, Changelog, Credits, ThirdPartyNotices, License, GodotCopyright };

    /// <summary>A document's text. A document that isn't built in is a build error, not a condition to handle.</summary>
    public static string Read(string name)
    {
        using var stream = typeof(Docs).Assembly.GetManifestResourceStream("Docs." + name)
            ?? throw new InvalidOperationException($"{name} isn't built into Atlas.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
