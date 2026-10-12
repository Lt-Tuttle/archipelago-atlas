using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Games;

/// <summary>
/// What one release of a game's apworld says about itself and how it fits, kept per game in
/// <c>games\&lt;Game&gt;\release_facts.json</c>: the map trackers its own setup guide names, the Archipelago versions its
/// manifest (archipelago.json) asks for, and, once it was loaded in a throwaway engine process, its location count and
/// how each installed map pack fits its names. A release whose guide names a tracker is often the one that tracker was
/// made for (Dark Souls II's map tracker works with its 0.6.0 pre-release, not the 0.5.5 full release, which has no docs).
/// </summary>
public sealed class ReleaseRecord
{
    /// <summary>"github.com/owner/repo", or "your file".</summary>
    public string Project { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Prerelease { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>Whether its zip bundles docs/setup_en.md.</summary>
    public bool HasGuide { get; set; }
    /// <summary>The map tracker projects its setup guide (or its game page) links, as "owner/name", in the guide's order.</summary>
    public List<string> GuideTrackers { get; set; } = new();
    public string? Game { get; set; }
    public string? WorldVersion { get; set; }
    /// <summary>archipelago.json's minimum_ap_version and maximum_ap_version, when it says.</summary>
    public string? MinimumAp { get; set; }
    public string? MaximumAp { get; set; }
    /// <summary>Why it couldn't be loaded in the engine (the last line of the error), or null.</summary>
    public string? LoadError { get; set; }
    /// <summary>Its location count and data checksum, once loaded.</summary>
    public int? Locations { get; set; }
    public string? DataChecksum { get; set; }
    /// <summary>How each installed map pack fits its names, by the pack's key.</summary>
    public Dictionary<string, PackFitRecord> PackFits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When it was read (wall clock: shown only).</summary>
    public DateTime Read { get; set; }

    /// <summary>The pack's fit, as <see cref="PackReleaseFit"/>, or null when it wasn't measured.</summary>
    public PackReleaseFit? FitOf(string? packKey) =>
        packKey != null && PackFits.TryGetValue(packKey, out var f) ? new PackReleaseFit(Version, f.SectionsTotal, f.SectionsLinked, f.MappedIds, f.UnknownIds) : null;
}

/// <summary>One map pack measured against one release's names (the Pack Doctor's analysis, nothing linked).</summary>
public sealed class PackFitRecord
{
    public string PackName { get; set; } = "";
    public string PackVersion { get; set; } = "";
    public int SectionsTotal { get; set; }
    public int SectionsLinked { get; set; }
    public int MappedIds { get; set; }
    public int UnknownIds { get; set; }
}

public static class ReleaseFacts
{
    /// <summary>
    /// Reads a release's apworld file: its bundled setup guide and game page (the trackers they link) and its manifest.
    /// Nothing is run. Throws <see cref="InvalidDataException"/> or <see cref="IOException"/> for a file that isn't a
    /// readable zip.
    /// </summary>
    public static ReleaseRecord Read(string apworldFile, string project, string version, bool prerelease)
    {
        var record = new ReleaseRecord { Project = project, Version = version, Prerelease = prerelease, Read = DateTime.Now }; // wall clock: shown only
        using var zip = SafeZip.Open(apworldFile);
        var documents = new List<(bool Setup, string Text)>();
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            string name = entry.FullName.Replace('\\', '/');
            string leaf = Path.GetFileName(name);
            if (name.Contains("/docs/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                bool setup = string.Equals(leaf, "setup_en.md", StringComparison.OrdinalIgnoreCase);
                if (setup) record.HasGuide = true;
                if (setup || leaf.StartsWith("en_", StringComparison.OrdinalIgnoreCase)) documents.Add((setup, zip.ReadText(entry)));
            }
            else if (string.Equals(leaf, "archipelago.json", StringComparison.OrdinalIgnoreCase) && name.Count(c => c == '/') <= 1)
                ReadManifest(zip.ReadText(entry), record);
        }
        foreach (var (_, text) in documents.OrderBy(d => d.Setup ? 0 : 1))
            foreach (var (repo, _) in GuideLinks.TrackerProjects(text))
                if (!record.GuideTrackers.Contains(repo, StringComparer.OrdinalIgnoreCase)) record.GuideTrackers.Add(repo);
        return record;
    }

    private static void ReadManifest(string json, ReleaseRecord record)
    {
        try
        {
            if (JToken.Parse(json) is not JObject manifest) return;
            record.Game = Text(manifest["game"]);
            record.WorldVersion = Text(manifest["world_version"]);
            record.MinimumAp = Text(manifest["minimum_ap_version"]);
            record.MaximumAp = Text(manifest["maximum_ap_version"]);
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            Logger.LogDebug($"An apworld's archipelago.json couldn't be read: {ex.Message}");
        }
    }

    private static string? Text(JToken? token) => token is JValue { Value: not null } v && v.ToString().Trim() is { Length: > 0 } s ? s : null;

    /// <summary>
    /// Whether a release runs on this Archipelago, by its manifest: null when it can, else why not ("needs Archipelago
    /// 0.7.0 or newer; the engine has 0.6.7"). A manifest that says nothing is taken at its word.
    /// </summary>
    public static string? ArchipelagoProblem(ReleaseRecord release, string? engineAp)
    {
        if (string.IsNullOrWhiteSpace(engineAp)) return null;
        var compare = ApworldChoices.VersionComparer.Instance;
        if (release.MinimumAp != null && compare.Compare(engineAp, release.MinimumAp) < 0)
            return $"needs Archipelago {release.MinimumAp} or newer; the engine has {engineAp}";
        if (release.MaximumAp != null && compare.Compare(engineAp, release.MaximumAp) > 0)
            return $"is for Archipelago {release.MaximumAp} at most; the engine has {engineAp}";
        return null;
    }

    /// <summary>
    /// Why a release doesn't work here, or null: it couldn't be loaded, its manifest asks for another Archipelago, or the
    /// map pack doesn't fit it while another release does.
    /// </summary>
    public static string? WhyNot(ReleaseRecord release, string? engineAp, string? packKey, bool anotherFits)
    {
        if (release.LoadError != null) return "it couldn't be loaded in the engine";
        if (ArchipelagoProblem(release, engineAp) is { } ap) return "it " + ap;
        if (anotherFits && release.FitOf(packKey) is { Fits: false } fit)
            return $"your map pack places {Math.Round(fit.LinkedShare * 100)}% of its pins with it";
        return null;
    }
}

/// <summary>Which release of a game's apworld Atlas picks, and why.</summary>
public static class ReleaseChoice
{
    /// <summary>At most this many of a game's newest releases are looked inside (downloaded and read; nothing installed).</summary>
    public const int MostLooked = 4;

    public sealed record Pick(ReleaseRecord Release, string Why);

    /// <summary>
    /// Of the releases looked inside (newest first), the one to run, pre-release or not:
    /// <list type="number">
    /// <item>the newest the installed map pack fits;</item>
    /// <item>else the newest whose own setup guide names a map tracker (the build that tracker was made for);</item>
    /// <item>else, with the pack measured, the one it fits best;</item>
    /// <item>else the newest full release.</item>
    /// </list>
    /// Releases that can't run here (not loadable, another Archipelago) are passed over. Null for none.
    /// </summary>
    public static Pick? Best(IReadOnlyList<ReleaseRecord> newestFirst, string? packKey, string? engineAp)
    {
        var usable = newestFirst.Where(r => r.LoadError == null && ReleaseFacts.ArchipelagoProblem(r, engineAp) == null).ToList();
        if (usable.Count == 0) return null;
        string Pre(ReleaseRecord r) => r.Prerelease ? " (a pre-release)" : "";
        if (packKey != null && usable.FirstOrDefault(r => r.FitOf(packKey) is { Fits: true }) is { } fitting)
            return new Pick(fitting, $"your map pack fits it{Pre(fitting)}: {Math.Round(fitting.FitOf(packKey)!.LinkedShare * 100)}% of its pins link");
        if (usable.FirstOrDefault(r => r.GuideTrackers.Count > 0) is { } named)
            return new Pick(named, $"its own setup guide names the map tracker github.com/{named.GuideTrackers[0]}{Pre(named)}");
        var measured = usable.Select((r, i) => (r, i, fit: r.FitOf(packKey))).Where(x => x.fit != null).ToList();
        if (measured.Count > 0)
        {
            var best = measured.OrderByDescending(x => x.fit!.LinkedShare).ThenBy(x => x.fit!.UnknownShare).ThenBy(x => x.i).First();
            return new Pick(best.r, $"your map pack fits it best{Pre(best.r)}: {Math.Round(best.fit!.LinkedShare * 100)}% of its pins link");
        }
        if (usable.FirstOrDefault(r => !r.Prerelease) is { } full) return new Pick(full, "the newest full release");
        return null;
    }
}

/// <summary>
/// A game's release facts in its folder (<c>games\&lt;Game&gt;\release_facts.json</c>), by project and version. Nothing
/// is written until a release is read.
/// </summary>
public sealed class ReleaseFactsStore
{
    private readonly string _path;
    private readonly List<ReleaseRecord> _records;

    private ReleaseFactsStore(string path, List<ReleaseRecord> records)
    {
        _path = path;
        _records = records;
    }

    public static string PathFor(string dataDir, string game) => Path.Combine(GameFiles.GameFolder(dataDir, game), "release_facts.json");

    public static ReleaseFactsStore Load(string dataDir, string game)
    {
        string path = PathFor(dataDir, game);
        var records = SafeFile.ReadJson(path, () => new List<ReleaseRecord>()) ?? new List<ReleaseRecord>();
        records.RemoveAll(r => r == null || string.IsNullOrEmpty(r.Version));
        return new ReleaseFactsStore(path, records);
    }

    public IReadOnlyList<ReleaseRecord> Records => _records;

    /// <summary>A release's record: by its file's SHA-256 when both know it, else by project and version.</summary>
    public ReleaseRecord? Find(string project, string version, string? sha256 = null) =>
        (sha256 != null ? _records.FirstOrDefault(r => r.Sha256 != null && r.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)) : null)
        ?? _records.FirstOrDefault(r => string.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase) && ApworldChoices.SameLabel(r.Version, version));

    /// <summary>Keeps a record in place of the release's earlier one (its pack fits carried over), and saves.</summary>
    public void Put(ReleaseRecord record)
    {
        var earlier = Find(record.Project, record.Version, record.Sha256);
        if (earlier != null)
        {
            foreach (var fit in earlier.PackFits) record.PackFits.TryAdd(fit.Key, fit.Value);
            _records.Remove(earlier);
        }
        _records.Add(record);
        SafeFile.WriteJson(_path, _records);
    }
}
