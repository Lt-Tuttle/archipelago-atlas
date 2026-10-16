#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>
    /// What a bulk action in the Pack Doctor will do, worked out before it's done: the rows it links (and to what), the
    /// rows it sets aside, the rows it skips because their match clashes, and the user's own links it replaces. The preview
    /// shows this plan and <see cref="PackBulk.Apply"/> carries out exactly this plan, so the two can't disagree.
    /// </summary>
    public sealed class BulkPlan
    {
        public List<(Finding Finding, Suggestion Pick)> Link { get; } = new();
        public List<Finding> SetAside { get; } = new();
        public List<(Finding Finding, string Why)> Skipped { get; } = new();
        /// <summary>Rows whose link replaces one the user made: finding key → the location (or item) the user's link names.</summary>
        public Dictionary<string, string> ReplacesYours { get; } = new(StringComparer.Ordinal);
        /// <summary>The links' source: null for the user's own choice, "suggested" (Fix what Atlas can), "example" (a rule from the user's examples).</summary>
        public string? Source { get; init; }
        /// <summary>Why the links were made, kept with each (a rule's description), or null.</summary>
        public string? Reason { get; init; }

        public bool Nothing => Link.Count == 0 && SetAside.Count == 0;

        /// <summary>One line: "Links 132, sets 40 aside, skips 4 that clash, replaces 2 of your own links."</summary>
        public string Summary()
        {
            if (Nothing) return Skipped.Count > 0 ? $"Nothing to change: the {Plural(Skipped.Count, "row clashes", "rows clash")} with another row or a placed check." : "Nothing to change.";
            var parts = new List<string>();
            if (Link.Count > 0) parts.Add($"links {Link.Count}");
            if (SetAside.Count > 0) parts.Add($"sets {SetAside.Count} aside");
            if (Skipped.Count > 0) parts.Add($"skips {Skipped.Count} that clash");
            if (ReplacesYours.Count > 0) parts.Add($"replaces {Plural(ReplacesYours.Count, "of your own links", "of your own links")}");
            string line = string.Join(", ", parts);
            return char.ToUpperInvariant(line[0]) + line.Substring(1) + ". One step: Undo reverses all of it.";
        }

        private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
    }

    /// <summary>Plans and carries out the Pack Doctor's bulk actions (Apply selected, Ignore selected, Fix what Atlas can, a rule from examples).</summary>
    public static class PackBulk
    {
        /// <summary>
        /// The plan for linking each row to its pick (a pick that clashes with another row's or a check another pin shows is
        /// skipped, with why) and setting the other rows aside.
        /// </summary>
        public static BulkPlan Plan(DoctorReport report, PackFixFile fixes, IEnumerable<(Finding Finding, Suggestion Pick)> links, IEnumerable<Finding>? setAside = null, string? source = null, string? reason = null)
        {
            var plan = new BulkPlan { Source = source, Reason = reason };
            var wanted = links.GroupBy(x => x.Finding.Key).Select(g => g.First()).ToList();
            var picks = wanted.ToDictionary(x => x.Finding.Key, x => x.Pick, StringComparer.Ordinal);
            var conflicts = PackDoctor.Conflicts(report, wanted.Select(x => x.Finding).ToList(), f => picks[f.Key]);
            foreach (var (f, pick) in wanted)
            {
                if (conflicts.TryGetValue(f.Key, out var why)) { plan.Skipped.Add((f, why)); continue; }
                plan.Link.Add((f, pick));
                if (YoursFor(fixes, f.Subject) is { } yours) plan.ReplacesYours[f.Key] = yours;
            }
            var linking = new HashSet<string>(wanted.Select(x => x.Finding.Key), StringComparer.Ordinal);
            foreach (var f in setAside ?? Enumerable.Empty<Finding>())
                if (!f.Ignored && !linking.Contains(f.Key) && !fixes.Ignored.Contains(f.Key)) plan.SetAside.Add(f);
            return plan;
        }

        /// <summary>
        /// Fix what Atlas can: every row to decide gets its best match when it scores 85% or more and clashes with nothing,
        /// and is set aside otherwise.
        /// </summary>
        public static BulkPlan Suggested(DoctorReport report, PackFixFile fixes)
        {
            var fixable = report.Findings.Where(f => !f.Ignored && (f.Actions.HasFlag(FindingActions.LinkItem) || f.Actions.HasFlag(FindingActions.LinkLocation))).ToList();
            var link = ConfidentLinks(report);
            var linking = new HashSet<string>(link.Select(x => x.Finding.Key), StringComparer.Ordinal);
            return Plan(report, fixes, link, fixable.Where(f => !linking.Contains(f.Key)), source: "suggested");
        }

        /// <summary>The rows whose best match scores 85% or more and clashes with nothing (what's linked without a second thought).</summary>
        public static List<(Finding Finding, Suggestion Pick)> ConfidentLinks(DoctorReport report)
        {
            var recs = PackDoctor.Recommendations(report);
            var conflicts = PackDoctor.Conflicts(report, recs, f => f.Suggestions[0]);
            return recs.Where(f => f.Suggestions[0].Score >= PackDoctorService.SuggestedMinScore - 0.0001 && !conflicts.ContainsKey(f.Key))
                .Select(f => (f, f.Suggestions[0])).ToList();
        }

        /// <summary>The user's own link (or tile) for a subject, by the name it gives, or null.</summary>
        private static string? YoursFor(PackFixFile fixes, string subject)
        {
            var link = fixes.Links.FirstOrDefault(l => l.Subject == subject && l.Source == null && l.ApLocationId != null);
            if (link != null) return link.ApLocationName ?? link.ApLocationId.ToString();
            var tile = fixes.Tiles.FirstOrDefault(t => t.Subject == subject && t.Source == null && t.ApItemId != null);
            return tile == null ? null : tile.ApItemName ?? tile.ApItemId.ToString();
        }

        /// <summary>Carries out a plan as one undoable edit. Main thread.</summary>
        public static void Apply(string key, LoadedPack original, BulkPlan plan, string description)
        {
            if (plan.Nothing) return;
            PackFixes.Edit(key, description, file =>
            {
                foreach (var (f, s) in plan.Link)
                {
                    string subject = f.Subject.Substring(f.Subject.IndexOf(':') + 1);
                    if (f.Actions.HasFlag(FindingActions.LinkItem))
                    {
                        var tile = PackFixActions.UpsertTile(original, file, subject);
                        tile.ApItemId = s.Id;
                        tile.ApItemName = s.Label;
                        tile.Source = plan.Source;
                        tile.Reason = plan.Reason ?? (plan.Source == "suggested" ? PackChangeWords.SuggestedReason(s.Score) : null);
                    }
                    else
                    {
                        var parts = subject.Split('|');
                        file.Links.RemoveAll(l => l.Subject == f.Subject);
                        file.Links.Add(new LocationLinkFix
                        {
                            Subject = f.Subject,
                            PinPath = parts[0],
                            SectionName = parts.Length > 1 ? parts[1] : "",
                            ApLocationId = s.Id,
                            ApLocationName = s.Label,
                            Source = plan.Source,
                            Reason = plan.Reason ?? (plan.Source == "suggested" ? PackChangeWords.SuggestedReason(s.Score) : null),
                            AuthorStamp = PackFixes.AuthorStamp(original, f.Subject)
                        });
                    }
                }
                foreach (var f in plan.SetAside)
                    if (!file.Ignored.Contains(f.Key)) file.Ignored.Add(f.Key);
            });
        }
    }
}
