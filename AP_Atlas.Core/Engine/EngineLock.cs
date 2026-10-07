using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AP_Atlas.Core.EngineSetup;

/// <summary>
/// Reads Atlas's package lock (pip's requirements format: comment lines, then one block per package, "name==version \"
/// followed by its "    --hash=sha256:…" lines). One package can be taken out of the lock for Atlas to install itself,
/// with the hashes that block allows.
/// </summary>
public static class EngineLock
{
    /// <summary>A package's pin and the SHA-256 hashes its block allows.</summary>
    public sealed record Block(string Name, string Version, IReadOnlyList<string> Hashes, string Text);

    private static readonly Regex Pin = new(@"^(?<name>[A-Za-z0-9_.\-]+)==(?<version>\S+) \\$", RegexOptions.Compiled);
    private static readonly Regex Hash = new(@"^\s+--hash=sha256:(?<hash>[0-9a-fA-F]{64})( \\)?$", RegexOptions.Compiled);

    /// <summary>Every package block of the lock, in order (comment and blank lines left out).</summary>
    public static IReadOnlyList<Block> Blocks(string lockText)
    {
        var blocks = new List<Block>();
        string? name = null, version = null;
        var hashes = new List<string>();
        var lines = new List<string>();
        void Finish()
        {
            if (name != null) blocks.Add(new Block(name, version!, hashes.ToArray(), string.Join("\n", lines) + "\n"));
            name = null; version = null; hashes = new List<string>(); lines = new List<string>();
        }
        foreach (string raw in lockText.Replace("\r\n", "\n").Split('\n'))
        {
            var pin = Pin.Match(raw);
            if (pin.Success)
            {
                Finish();
                name = pin.Groups["name"].Value; version = pin.Groups["version"].Value;
                lines.Add(raw);
                continue;
            }
            var hash = Hash.Match(raw);
            if (hash.Success && name != null)
            {
                hashes.Add(hash.Groups["hash"].Value.ToLowerInvariant());
                lines.Add(raw);
            }
        }
        Finish();
        return blocks;
    }

    /// <summary>
    /// The lock without <paramref name="package"/>'s block (for pip), and that block alone (for Atlas), or null when the
    /// lock doesn't pin it. Package names compare the way pip does: case and the '-', '_' and '.' separators don't count.
    /// </summary>
    public static (string Remaining, Block? Taken) Take(string lockText, string package)
    {
        var blocks = Blocks(lockText);
        var taken = blocks.FirstOrDefault(b => SameName(b.Name, package));
        string header = string.Join("\n", lockText.Replace("\r\n", "\n").Split('\n').TakeWhile(l => l.Length == 0 || l.StartsWith('#')));
        string rest = header.TrimEnd('\n') + "\n\n" + string.Join("", blocks.Where(b => b != taken).Select(b => b.Text));
        return (rest, taken);
    }

    /// <summary>pip's normalised package name: lower case, runs of '-', '_' and '.' as one '-'.</summary>
    public static string Normalise(string name) => Regex.Replace(name, "[-_.]+", "-").ToLowerInvariant();

    private static bool SameName(string a, string b) => Normalise(a) == Normalise(b);
}
