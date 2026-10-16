#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core.EngineSetup;

namespace AP_Atlas.Core.PopTracker
{
    /// <summary>One pack of Add games' "fix them now?", as it stands when the preview opens: its newest report and the plan made from it.</summary>
    public sealed record HandoffPack(string Key, string Name, string Game, string File, DoctorReport Report, BulkPlan Plan);

    /// <summary>What "Fix them now" did to one pack: the fix (null when its plan changed nothing), whether its file couldn't be read, and the undo step it made.</summary>
    public sealed record HandoffResult(HandoffPack Pack, PackDoctorService.SuggestedFixResult? Fixed, bool Unreadable, string? UndoStep);

    /// <summary>
    /// Add games' one question for the packs the Pack Doctor wants looked at (the owner's decision 9, 2026-10-15): the plans
    /// made when the preview opens, carried out exactly as shown, each pack marked reviewed first (its "Fix now" card doesn't
    /// come back, and an Undo of the fix keeps that answer), and what's left once the packs are checked again. Main thread.
    /// </summary>
    public static class PackHandoff
    {
        /// <summary>Each offered pack's plan made now: its newest report (a check since the batch may have one) and its fixes as they are.</summary>
        public static List<HandoffPack> Current(IEnumerable<PackFixOffer> offers) => offers.Select(o =>
        {
            var report = PackDoctorService.Reports.TryGetValue(o.Look.PackKey, out var newer) && newer != null ? newer : o.Report;
            return new HandoffPack(o.Look.PackKey, o.Look.PackName, o.Look.Game, o.Look.File, report, PackBulk.Suggested(report, PackFixes.Get(o.Look.PackKey)));
        }).ToList();

        /// <summary>Carries out every pack's plan as the preview showed it, each as one undoable step.</summary>
        public static List<HandoffResult> Apply(IReadOnlyList<HandoffPack> packs)
        {
            var results = new List<HandoffResult>();
            foreach (var p in packs)
            {
                LoadedPack? original;
                try { original = PopTrackerPackLoader.InspectZipPack(p.File); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogWarning($"[Add games] {p.Name} couldn't be read to fix it: {ex.Message}");
                    original = null;
                }
                if (original == null) { results.Add(new HandoffResult(p, null, true, null)); continue; }
                PackDoctorService.MarkReviewed(original); // first: the fix's undo snapshot keeps the answer
                if (p.Plan.Nothing) { results.Add(new HandoffResult(p, null, false, null)); continue; }
                var done = PackDoctorService.ApplyPlan(p.Key, original, p.Report, p.Plan);
                results.Add(new HandoffResult(p, done, false, PackFixes.UndoDescription(p.Key)));
            }
            return results;
        }

        /// <summary>The rows still to decide in a pack once the check its fix queued is in (at most <paramref name="most"/>).</summary>
        public static async Task<int> LeftAsync(string key, TimeSpan most, CancellationToken ct = default)
        {
            await PackDoctorService.WhenIdleAsync(key, most, ct);
            return PackDoctorService.Reports.TryGetValue(key, out var report) && report != null ? report.NeedsReview.Count() : 0;
        }
    }
}
