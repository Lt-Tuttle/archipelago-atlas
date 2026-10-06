#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AP_Atlas.Core.CheeseTracker
{
    /// <summary>One row of the Cheese Tracker tab: a slot on a linked tracker.</summary>
    public sealed class CheeseRow
    {
        public string ProfileId { get; init; }
        public string ProfileName { get; init; }
        /// <summary>The profile slot this row is (null for other players' rows).</summary>
        public string SlotName { get; init; }
        public CtGame Game { get; init; }
        public CtTracker Tracker { get; init; }
        /// <summary>Claimed by the user's account (or name), or one of the user's slots in Atlas.</summary>
        public bool Mine { get; init; }
        /// <summary>Unfound hints for items in this slot's world (what others wait on from it).</summary>
        public int UnfoundHints { get; init; }

        public string Key => ProfileId + "#" + Game.Id;
    }

    /// <summary>The tab's filters, as Cheese Tracker's own page has them.</summary>
    public sealed class CheeseFilter
    {
        /// <summary>The owner filter's "claimed by you" choice.</summary>
        public const string You = "\u0001you";

        public HashSet<string> HiddenProgression { get; } = new HashSet<string>();
        public HashSet<string> HiddenCompletion { get; } = new HashSet<string>();
        public HashSet<string> HiddenAvailability { get; } = new HashSet<string>();
        /// <summary>Null: everyone. "": unclaimed. <see cref="You"/>: claimed by you. Otherwise an owner's name.</summary>
        public string Owner { get; set; }
        /// <summary>Null: every game.</summary>
        public string Game { get; set; }

        public bool IsActive =>
            HiddenProgression.Count > 0 || HiddenCompletion.Count > 0 || HiddenAvailability.Count > 0 ||
            Owner != null || Game != null;

        public void Clear()
        {
            HiddenProgression.Clear();
            HiddenCompletion.Clear();
            HiddenAvailability.Clear();
            Owner = null;
            Game = null;
        }
    }

    /// <summary>
    /// Filtering, sorting and the numbers behind the Cheese Tracker tab, matching Cheese Tracker's own page: a progression
    /// filter doesn't hide completed slots, names sort naturally ("Slot 2" before "Slot 10"), slots with no activity
    /// count as the least active, and "mine first" puts your slots on top before any column sort.
    /// </summary>
    public static class CheeseTable
    {
        public static bool Passes(CheeseRow r, CheeseFilter f, bool claimedByYou)
        {
            var g = r.Game;
            if (!g.IsComplete && f.HiddenProgression.Contains(g.Progression)) return false;
            if (f.HiddenCompletion.Contains(g.Completion)) return false;
            if (f.HiddenAvailability.Contains(g.Availability)) return false;
            if (f.Owner != null)
            {
                if (f.Owner.Length == 0) { if (!string.IsNullOrEmpty(g.OwnerName)) return false; }
                else if (f.Owner == CheeseFilter.You) { if (!claimedByYou) return false; }
                else if (!string.Equals(g.OwnerName, f.Owner, StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (f.Game != null && !string.Equals(g.Game, f.Game, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>The direction a column sorts in first (Cheese Tracker's: least active and most hinted first).</summary>
        public static bool DefaultDescending(string column) => column is "activity" or "hints";

        /// <summary>
        /// What a column sorts by for a row, as Cheese Tracker's own page orders it (every table compares keys through
        /// TableSort: numbers as numbers, names naturally). Ties keep the rows' order: the tab gives them by name, then multiworld.
        /// </summary>
        public static IComparable SortKey(CheeseRow r, string column, DateTime nowUtc) => column switch
        {
            "multiworld" => r.ProfileName ?? "",
            "position" => r.Game.Position,
            "ping" => Array.IndexOf(CtStatus.PingIds, EffectivePing(r)),
            "availability" => AvailabilityRank(r.Game),
            "owner" => r.Game.OwnerName ?? "",
            "game" => r.Game.Game ?? "",
            "progression" => ProgressionRank(r.Game),
            "status" => ProgressionRank(r.Game) * 100 + Array.IndexOf(CtStatus.CompletionIds, r.Game.Completion),
            "completion" => Array.IndexOf(CtStatus.CompletionIds, r.Game.Completion),
            "activity" => DaysSinceActivity(r.Game, nowUtc) ?? double.PositiveInfinity,
            "checks" => ChecksRatio(r.Game),
            "hints" => r.UnfoundHints,
            _ => r.Game.Name ?? ""
        };

        /// <summary>Names in natural order ("Slot 2" before "Slot 10"), as every table sorts: see <see cref="TableSort.NaturalCompare"/>.</summary>
        public static int NaturalCompare(string a, string b) => TableSort.NaturalCompare(a, b);

        /// <summary>BK first (it needs attention), then Soft BK, Unknown, Unblocked, Go mode; completed slots last.</summary>
        public static int ProgressionRank(CtGame g) => g.IsComplete ? 5 : g.Progression switch
        {
            "bk" => 0,
            "soft_bk" => 1,
            "unknown" => 2,
            "unblocked" => 3,
            "go" => 4,
            _ => 2
        };

        private static int AvailabilityRank(CtGame g) => g.Availability switch
        {
            "open" => 0,
            "public" => 1,
            "claimed" => 2,
            _ => 3
        };

        private static double ChecksRatio(CtGame g) => g.ChecksTotal > 0 ? (double)g.ChecksDone / g.ChecksTotal : 1.0;

        /// <summary>The ping preference that applies: none once complete, else the tracker's policy, else the slot's.</summary>
        public static string EffectivePing(CheeseRow r) =>
            r.Game.IsComplete ? "never" : !string.IsNullOrEmpty(r.Tracker?.GlobalPingPolicy) ? r.Tracker.GlobalPingPolicy : r.Game.Ping;

        /// <summary>Days since the slot's last check or "still BK", whichever is later (null: neither ever happened).</summary>
        public static double? DaysSinceActivity(CtGame g, DateTime nowUtc)
        {
            var activity = g.LastActivityUtc;
            var stillBk = g.LastCheckedUtc;
            var latest = stillBk != null && (activity == null || stillBk > activity) ? stillBk : activity;
            return latest == null ? null : Math.Max(0, (nowUtc - latest.Value).TotalDays);
        }

        /// <summary>"found", "useless" (its receiver finished or forfeited with every check done) or "notfound".</summary>
        public static string HintState(CtHint h, IReadOnlyDictionary<int, CtGame> games)
        {
            if (h.Found) return "found";
            if (h.ReceiverGameId is int receiverId && games.TryGetValue(receiverId, out var receiver) &&
                receiver.ChecksDone == receiver.ChecksTotal && receiver.Completion is "done" or "released") return "useless";
            return "notfound";
        }

        /// <summary>Cheese Tracker's hint count: unfound hints for items in this slot's world needed by another slot, not marked trash.</summary>
        public static int CountUnfoundHints(CtGame g, IEnumerable<CtHint> hintsInWorld, IReadOnlyDictionary<int, CtGame> games) =>
            hintsInWorld?.Count(h => h.FinderGameId == g.Id && h.ReceiverGameId != g.Id && h.Classification != "trash" && HintState(h, games) == "notfound") ?? 0;

        /// <summary>"1.2d" (days, like Cheese Tracker), or "Never".</summary>
        public static string ActivityText(CtGame g, DateTime nowUtc)
        {
            double? days = DaysSinceActivity(g, nowUtc);
            return days == null ? "Never" : days.Value.ToString("0.0", CultureInfo.InvariantCulture) + "d";
        }

        /// <summary>Green, yellow or red by the tracker's inactivity thresholds; complete slots are green, never-active red.</summary>
        public static int ActivityLevel(CtGame g, CtTracker t, DateTime nowUtc)
        {
            if (g.IsComplete) return 0;
            double? days = DaysSinceActivity(g, nowUtc);
            if (days == null) return 2;
            if (days >= (t?.RedHours ?? 48) / 24.0) return 2;
            if (days >= (t?.YellowHours ?? 24) / 24.0) return 1;
            return 0;
        }
    }
}
