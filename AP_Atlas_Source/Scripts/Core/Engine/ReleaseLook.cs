#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.Games;
using AP_Atlas.Core.PopTracker;

namespace AP_Atlas.Core.EngineSetup
{
    /// <summary>
    /// Looks inside a game's releases to find the one that works with its map pack: each is downloaded into the game's
    /// folder (where every release Atlas downloads is kept; nothing installed), its setup guide and manifest are read, and,
    /// with the engine set up, it's loaded in a throwaway engine process for its names, which the installed pack is
    /// measured against (the Pack Doctor's analysis; nothing linked). What's found is kept in the game's release facts.
    /// </summary>
    public static class ReleaseLook
    {
        /// <summary>A release to look inside: a published one (downloaded first), or a file Atlas has (the installed copy).</summary>
        public sealed record Candidate(string Project, string Version, bool Prerelease, ApworldVersion? Published, string? LocalFile);

        /// <summary>
        /// A pack's place in a release's fits: the pack and its version (a new version of the pack is measured again).
        /// </summary>
        public static string FitKey(LoadedPack pack) => PackFixes.KeyFor(pack) + "@" + (pack.Manifest?.GetActualVersion() ?? "");

        /// <summary>What the published candidates come to when downloaded: bytes, and how many don't list their size.</summary>
        public static (long Bytes, int Unknown) DownloadSize(IEnumerable<Candidate> candidates)
        {
            var published = candidates.Where(c => c.LocalFile == null && c.Published != null).ToList();
            return (published.Sum(c => c.Published!.Size), published.Count(c => c.Published!.Size <= 0));
        }

        /// <param name="pack">The installed map pack, prepared on the main thread (<see cref="PackDoctor.Prepare"/>), or null for none.</param>
        public static Task<List<ReleaseRecord>> LookAsync(EngineInstall? install, string game, string? apworldName, IReadOnlyList<Candidate> candidates, PackDoctor.Inputs? pack,
            Action<string> log, CancellationToken ct) =>
            AtlasEngine.ExclusiveAsync(async () =>
            {
                var store = ReleaseFactsStore.Load(DataManager.GetDataDirectory(), game);
                bool canLoad = install != null && install.CanLaunch;
                string? packKey = pack != null ? FitKey(pack.Original) : null;
                if (!canLoad) log($"The Atlas Engine isn't set up, so the releases' names can't be read{(pack != null ? " and your map pack isn't measured" : "")}: only what their guides and manifests say.");
                var records = new List<ReleaseRecord>();
                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    string label = $"{candidate.Version} ({candidate.Project})";
                    string? file = candidate.LocalFile;
                    if (file == null && candidate.Published != null)
                    {
                        try { file = await ApworldSources.DownloadAsync(game, apworldName, candidate.Published, log, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            log($"  {label}: couldn't download ({ex.Message})");
                            continue;
                        }
                    }
                    if (file == null || !File.Exists(file)) continue;
                    ReleaseRecord record;
                    try { record = ReleaseFacts.Read(file, candidate.Project, candidate.Version, candidate.Prerelease); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        log($"  {label}: isn't a readable apworld ({ex.Message})");
                        continue;
                    }
                    record.Sha256 = EngineDownloader.Sha256Of(file);
                    if (canLoad)
                    {
                        var (names, checksum, error) = await ApworldSources.NamesOfAsync(install!, file, game, ct);
                        if (names == null)
                            record.LoadError = (error ?? "it didn't load").Trim().Split('\n').Last().Trim();
                        else
                        {
                            record.Locations = names.Locations.Count;
                            record.DataChecksum = checksum;
                            // Every identified release is remembered, so a seed made with it is matched at once.
                            if (checksum != null) ApworldSources.RememberIdentified(names.Game, checksum, candidate.Version, file, $"{candidate.Project} {candidate.Version}");
                            if (pack != null)
                            {
                                var report = PackDoctor.Analyze(new PackDoctor.Inputs { Original = pack.Original, Effective = pack.Effective, Fixes = pack.Fixes, Names = names });
                                record.PackFits[packKey!] = new PackFitRecord
                                {
                                    PackName = pack.Original.Manifest?.Name ?? "",
                                    PackVersion = pack.Original.Manifest?.GetActualVersion() ?? "",
                                    SectionsTotal = report.SectionsTotal,
                                    SectionsLinked = report.SectionsLinked,
                                    MappedIds = pack.Original.LocationMappingById?.Count ?? 0,
                                    UnknownIds = report.Index?.UnknownMappedLocationIds.Count ?? 0
                                };
                            }
                        }
                    }
                    store.Put(record);
                    records.Add(record);
                    log($"  {label}: " + Describe(record, packKey));
                }
                return records;
            }, ct);

        /// <summary>One line about a release: what its guide names, the Archipelago it asks for, how the pack fits.</summary>
        public static string Describe(ReleaseRecord record, string? packKey)
        {
            var parts = new List<string>();
            if (record.LoadError != null) parts.Add("couldn't be loaded (" + record.LoadError + ")");
            if (record.FitOf(packKey) is { } fit) parts.Add($"your map pack: {Math.Round(fit.LinkedShare * 100)}% of its pins link");
            else if (record.Locations is { } count) parts.Add($"{count} locations");
            parts.Add(record.GuideTrackers.Count > 0 ? "its guide names github.com/" + record.GuideTrackers[0] : record.HasGuide ? "its guide names no tracker" : "no setup guide");
            if (record.MinimumAp != null) parts.Add("needs Archipelago " + record.MinimumAp + " or newer");
            return string.Join("; ", parts);
        }
    }
}
