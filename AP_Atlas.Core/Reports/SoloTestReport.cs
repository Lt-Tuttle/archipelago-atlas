#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AP_Atlas.Core.Games;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AP_Atlas.Core.Reports
{
    /// <summary>The chain's steps, in order.</summary>
    public enum SoloTestStep
    {
        Apworld,
        MapPack,
        Yaml,
        Generate,
        Host,
        Connect,
        Score,
        Report
    }

    public enum SoloStepOutcome
    {
        Pending,
        Running,
        Done,
        Skipped,
        Failed,
        Cancelled
    }

    public sealed record SoloStepRecord(SoloTestStep Step, SoloStepOutcome Outcome, string Detail, double Seconds);

    /// <summary>What the test ran on: the apworld, the engine, the pack and Atlas itself.</summary>
    public sealed record SoloVersions(string Game, string? ApworldVersion, string? ApworldSource, string? ApworldChecksum, string Engine, string? PackName, string? PackVersion, string? PackSource, string Atlas);

    /// <remarks>The seed is text: Archipelago's seeds have up to twenty digits, more than a 64-bit number holds.</remarks>
    public sealed record SoloGeneration(string? SeedName, string? Seed, int Locations, int Items, int Spheres, int Sphere0, double Seconds, bool PatchSkipped, string? Error, int YamlOptions, string? YamlFrom);

    /// <param name="ActiveAtConnect">Reachable locations to do (not checked, not excluded by the seed) once logic settled.</param>
    /// <param name="ReachableAtConnect">Every reachable location then (what sphere 0 counts).</param>
    /// <param name="NotReached">Sphere 0's locations the live logic didn't reach at connect.</param>
    /// <param name="BeyondSphere0">Locations the live logic reached at connect that sphere 0 doesn't hold.</param>
    public sealed record SoloLogic(bool? Exact, int Spheres, int Late, int Early, string Verdict, bool? ChecksumMatch, int ActiveAtConnect, int TotalLocations, string? YamlSource, string? EngineProblem,
        int ReachableAtConnect = 0, int ExcludedAtConnect = 0, IReadOnlyList<string>? NotReached = null, IReadOnlyList<string>? BeyondSphere0 = null);

    public sealed record SoloPins(int LocationsTotal, int LocationsPlaced, int SectionsTotal, int SectionsLinked, int SectionsUnmatched, int DanglingPaths, int NeedsReview,
        IReadOnlyList<string>? Unplaced = null, IReadOnlyList<string>? ToReview = null);

    public sealed record SoloKeyItems(int TilesTotal, int TilesLinked, int TilesByScript);

    public sealed record SoloScripts(bool Ran, IReadOnlyList<string> UnsupportedApis, IReadOnlyList<string> IgnoredWrites, IReadOnlyList<string> Errors, bool FollowsMaps, bool ReadsGameMemory, bool Stopped, string? StopReason, int ScriptFindings);

    /// <summary>What only a person sees: the four prompts the dialog asks at the end.</summary>
    public sealed record SoloOwnerNotes(string WrongOnMap, string WrongItems, string RealGameNeeds, string Other);

    /// <summary>Everything a solo test found; the report is written from it (the JSON twin is it as-is).</summary>
    public sealed record SoloTestResult(string Game, DateTime StartedLocal, SoloVersions Versions, IReadOnlyList<SoloStepRecord> Steps,
        SoloGeneration? Generation, SoloLogic? Logic, SoloPins? Pins, SoloKeyItems? KeyItems, SoloScripts? Scripts, string? ServerAddress, SoloOwnerNotes? Notes, int Schema = 1);

    /// <summary>
    /// The solo test's report: Markdown for people (fixed headings in a fixed order, so reports of different games and
    /// days read alike and a later batch can diff them) and a JSON twin for machines, both scrubbed of paths, addresses and
    /// names. The owner's notes are appended under their own heading, replacing an earlier set.
    /// </summary>
    public static class SoloTestReport
    {
        public const string Folder = "solo-tests";
        public const string NotesHeading = "## Owner's notes";
        public static readonly string[] Headings = { "## Game and versions", "## Steps", "## Generation", "## Logic", "## Pins", "## Key Items", "## Scripts", NotesHeading };
        public static readonly string[] NotePrompts = { "What looked wrong on the map?", "Which items showed wrongly?", "Anything the real game would need (ROM, client, mod)?", "Other" };

        public static string FileStem(string game, DateTime localNow) => StemPrefix(game) + localNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        /// <summary>
        /// A pack's public address as the report shows it: "github.com/owner/repo" for GitHub (its raw files' address
        /// included), else the address without its scheme; null for none. The scrubber hides addresses with a scheme,
        /// and a public project's place is what the report's reader needs.
        /// </summary>
        public static string? PublicSource(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return null;
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if ((uri.Host == "github.com" || uri.Host == "raw.githubusercontent.com") && parts.Length >= 2) return "github.com/" + parts[0] + "/" + parts[1];
            return uri.Host + uri.AbsolutePath.TrimEnd('/');
        }

        /// <summary>What every report file of a game starts with ("Dark_Souls_III-"), for finding them again.</summary>
        public static string StemPrefix(string game) => GameFiles.SafeName(game).Replace(' ', '_') + "-";

        public static string Percent(int part, int total) => total <= 0 ? "n/a" : (100.0 * part / total).ToString("0", CultureInfo.InvariantCulture) + "%";

        public static string Markdown(SoloTestResult r, Scrubber scrubber)
        {
            var sb = new StringBuilder();
            string Line(string text) => scrubber.Scrub(text);
            sb.AppendLine("# Solo test: " + Line(r.Game));
            sb.AppendLine();
            sb.AppendLine("Started " + r.StartedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (local time). Written by The Archipelago Atlas " + Line(r.Versions.Atlas) + "; paths, addresses and names replaced by marks.");
            sb.AppendLine();
            sb.AppendLine(Headings[0]);
            sb.AppendLine();
            sb.AppendLine("- Game: " + Line(r.Game));
            sb.AppendLine("- Apworld: " + Line(r.Versions.ApworldVersion ?? "version unknown") + (r.Versions.ApworldSource != null ? " from " + Line(r.Versions.ApworldSource) : "") +
                (r.Versions.ApworldChecksum != null ? " (data " + Line(Short(r.Versions.ApworldChecksum)) + ")" : ""));
            sb.AppendLine("- Engine: " + Line(r.Versions.Engine));
            sb.AppendLine("- Map pack: " + (r.Versions.PackName == null ? "none" : Line(r.Versions.PackName) + (r.Versions.PackVersion != null ? " " + Line(r.Versions.PackVersion) : "") + (r.Versions.PackSource != null ? " from " + Line(r.Versions.PackSource) : "")));
            sb.AppendLine();
            sb.AppendLine(Headings[1]);
            sb.AppendLine();
            sb.AppendLine("| Step | Outcome | Time | Detail |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var step in r.Steps)
                sb.AppendLine("| " + step.Step + " | " + step.Outcome + " | " + step.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s | " + Line(step.Detail).Replace("|", "/").Replace("\n", " ") + " |");
            sb.AppendLine();
            sb.AppendLine(Headings[2]);
            sb.AppendLine();
            if (r.Generation == null) sb.AppendLine("Not scored: the seed wasn't generated.");
            else
            {
                var g = r.Generation;
                if (g.Error != null) sb.AppendLine("Not scored: generation failed (" + Line(g.Error) + ").");
                else
                {
                    sb.AppendLine("- Seed: " + Line(g.SeedName ?? "?") + (!string.IsNullOrEmpty(g.Seed) ? " (" + Line(g.Seed) + ")" : ""));
                    sb.AppendLine("- Locations: " + g.Locations + "; items: " + g.Items);
                    sb.AppendLine("- Spheres: " + g.Spheres + "; in sphere 0: " + g.Sphere0);
                    sb.AppendLine("- Time: " + g.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                    if (g.PatchSkipped) sb.AppendLine("- The game's patch output was skipped: its base ROM isn't on this PC, which the tracker doesn't need.");
                }
                sb.AppendLine("- YAML: " + g.YamlOptions + " options at their defaults (" + Line(g.YamlFrom ?? "template") + ")");
            }
            sb.AppendLine();
            sb.AppendLine(Headings[3]);
            sb.AppendLine();
            if (r.Logic == null) sb.AppendLine("Not scored: the slot didn't connect, or the seed wasn't generated.");
            else
            {
                var l = r.Logic;
                sb.AppendLine("- Verdict: " + Line(l.Verdict));
                sb.AppendLine("- Spheres compared: " + l.Spheres + "; shown too late: " + l.Late + "; too early: " + l.Early);
                sb.AppendLine("- At connect: " + l.ReachableAtConnect + " of " + l.TotalLocations + " locations reachable (" + l.ActiveAtConnect + " to do, " + l.ExcludedAtConnect + " excluded by the seed)"
                    + (r.Generation != null && r.Generation.Error == null ? "; the generator's sphere 0 has " + r.Generation.Sphere0 : ""));
                if (r.Generation != null && r.Generation.Error == null)
                {
                    var notReached = l.NotReached ?? Array.Empty<string>();
                    var beyond = l.BeyondSphere0 ?? Array.Empty<string>();
                    if (notReached.Count == 0 && beyond.Count == 0) sb.AppendLine("- Sphere 0 and the live logic at connect: identical");
                    if (notReached.Count > 0) sb.AppendLine("- In sphere 0 but not reachable at connect (" + notReached.Count + "): " + Line(string.Join("; ", notReached.Take(25))) + (notReached.Count > 25 ? "; …" : ""));
                    if (beyond.Count > 0) sb.AppendLine("- Reachable at connect but not in sphere 0 (" + beyond.Count + "): " + Line(string.Join("; ", beyond.Take(25))) + (beyond.Count > 25 ? "; …" : ""));
                }
                sb.AppendLine("- YAML source: " + Line(l.YamlSource ?? "unknown") + "; apworld matches the seed: " + (l.ChecksumMatch == null ? "unknown" : l.ChecksumMatch.Value ? "yes" : "no"));
                if (l.EngineProblem != null) sb.AppendLine("- Engine problem: " + Line(l.EngineProblem));
            }
            sb.AppendLine();
            sb.AppendLine(Headings[4]);
            sb.AppendLine();
            if (r.Pins == null) sb.AppendLine(NotScoredPack(r));
            else
            {
                var p = r.Pins;
                sb.AppendLine("- Game locations on a map: " + p.LocationsPlaced + " of " + p.LocationsTotal + " (" + Percent(p.LocationsPlaced, p.LocationsTotal) + ")");
                sb.AppendLine("- Pin sections linked: " + p.SectionsLinked + " of " + p.SectionsTotal + " (" + Percent(p.SectionsLinked, p.SectionsTotal) + "); unmatched: " + p.SectionsUnmatched);
                sb.AppendLine("- Dangling paths: " + p.DanglingPaths + "; things to review: " + p.NeedsReview);
                if (p.Unplaced is { Count: > 0 } unplaced)
                    sb.AppendLine("- Not on any map (" + (p.LocationsTotal - p.LocationsPlaced) + (p.LocationsTotal - p.LocationsPlaced > unplaced.Count ? "; the first " + unplaced.Count : "") + "): " + Line(string.Join("; ", unplaced)));
                if (p.ToReview is { Count: > 0 } review) sb.AppendLine("- To review: " + Line(string.Join("; ", review)));
            }
            sb.AppendLine();
            sb.AppendLine(Headings[5]);
            sb.AppendLine();
            if (r.KeyItems == null) sb.AppendLine(NotScoredPack(r));
            else sb.AppendLine("- Tiles linked: " + r.KeyItems.TilesLinked + " of " + r.KeyItems.TilesTotal + " (" + Percent(r.KeyItems.TilesLinked, r.KeyItems.TilesTotal) + "); by the pack's scripts: " + r.KeyItems.TilesByScript);
            sb.AppendLine();
            sb.AppendLine(Headings[6]);
            sb.AppendLine();
            if (r.Scripts == null) sb.AppendLine(NotScoredPack(r));
            else
            {
                var s = r.Scripts;
                sb.AppendLine("- Ran: " + (s.Ran ? "yes" : "no") + (s.Stopped ? "; stopped: " + Line(s.StopReason ?? "") : ""));
                sb.AppendLine("- Follows the game's map: " + (s.FollowsMaps ? "yes" : "no") + "; reads game memory: " + (s.ReadsGameMemory ? "yes" : "no"));
                sb.AppendLine("- Unsupported calls: " + (s.UnsupportedApis.Count == 0 ? "none" : Line(string.Join(", ", s.UnsupportedApis))));
                sb.AppendLine("- Ignored writes: " + (s.IgnoredWrites.Count == 0 ? "none" : Line(string.Join(", ", s.IgnoredWrites))));
                sb.AppendLine("- Script findings: " + s.ScriptFindings);
                if (s.Errors.Count > 0)
                {
                    sb.AppendLine("- Errors:");
                    foreach (string error in s.Errors.Take(10)) sb.AppendLine("  - " + Line(error).Replace("\n", " "));
                }
            }
            sb.AppendLine();
            sb.AppendLine(NotesHeading);
            sb.AppendLine();
            if (r.Notes == null) sb.AppendLine("(none yet)");
            else AppendNotes(sb, r.Notes, scrubber);
            return sb.ToString();
        }

        private static string NotScoredPack(SoloTestResult r) => r.Versions.PackName == null ? "Not scored: no map pack."
            : r.Logic == null ? "Not scored: the slot didn't connect, so the pack wasn't used." : "Not scored: the Pack Doctor's check of the pack didn't finish during the test.";

        private static void AppendNotes(StringBuilder sb, SoloOwnerNotes notes, Scrubber? scrubber)
        {
            string[] answers = { notes.WrongOnMap, notes.WrongItems, notes.RealGameNeeds, notes.Other };
            for (int i = 0; i < NotePrompts.Length; i++)
            {
                sb.AppendLine("### " + NotePrompts[i]);
                sb.AppendLine();
                string answer = (answers[i] ?? "").Trim();
                sb.AppendLine(answer.Length == 0 ? "(nothing)" : scrubber == null ? answer : scrubber.Scrub(answer));
                sb.AppendLine();
            }
        }

        /// <summary>The Markdown with the owner's notes under their heading (an earlier set replaced); the notes aren't scrubbed: they're the owner's own words.</summary>
        public static string WithNotes(string markdown, SoloOwnerNotes notes)
        {
            int at = markdown.IndexOf(NotesHeading, StringComparison.Ordinal);
            string head = at < 0 ? markdown.TrimEnd() + Environment.NewLine + Environment.NewLine : markdown.Substring(0, at);
            var sb = new StringBuilder(head);
            sb.AppendLine(NotesHeading);
            sb.AppendLine();
            AppendNotes(sb, notes, null);
            return sb.ToString();
        }

        public static string Json(SoloTestResult r, Scrubber scrubber)
        {
            // Every string value passes the same rules; the structure stays valid JSON (scrubbing the text would break
            // escaped paths).
            // Steps and outcomes by name: the file is read by people and tools that don't know the enums' order.
            var token = (JObject)JToken.FromObject(r, JsonSerializer.Create(new JsonSerializerSettings { Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() } }));
            foreach (var value in token.DescendantsAndSelf().OfType<JValue>().Where(v => v.Type == JTokenType.String).ToList())
                value.Value = scrubber.Scrub((string?)value.Value);
            return token.ToString(Formatting.Indented);
        }

        /// <summary>Writes both files and returns their paths.</summary>
        public static (string Md, string Json) Write(string reportsDir, SoloTestResult r, Scrubber scrubber, DateTime localNow)
        {
            Directory.CreateDirectory(reportsDir);
            string stem = Path.Combine(reportsDir, FileStem(r.Game, localNow));
            string md = stem + ".md", json = stem + ".json";
            SafeFile.WriteAllText(md, Markdown(r, scrubber));
            SafeFile.WriteAllText(json, Json(r, scrubber));
            return (md, json);
        }

        private static string Short(string checksum) => checksum.Length > 8 ? checksum.Substring(0, 8) : checksum;
    }
}
