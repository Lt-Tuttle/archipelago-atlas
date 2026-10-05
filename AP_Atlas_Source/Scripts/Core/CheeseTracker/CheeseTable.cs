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
        public string Text { get; set; } = "";

        public bool IsActive =>
            HiddenProgression.Count > 0 || HiddenCompletion.Count > 0 || HiddenAvailability.Count > 0 ||
            Owner != null || Game != null || !string.IsNullOrWhiteSpace(Text);

        public void Clear()
        {
            HiddenProgression.Clear();
            HiddenCompletion.Clear();
            HiddenAvailability.Clear();
            Owner = null;
            Game = null;
            Text = "";
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
            string text = (f.Text ?? "").Trim();
            if (text.Length > 0)
            {
                bool Has(string s) => s != null && s.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!Has(g.Name) && !Has(g.OwnerName) && !Has(g.Game) && !Has(g.Notes) && !Has(r.ProfileName)) return false;
            }
            return true;
        }

        /// <summary>The direction a column sorts in first (Cheese Tracker's: least active and most hinted first).</summary>
        public static bool DefaultDescending(string column) => column is "activity" or "hints";

        public static List<CheeseRow> Sort(IEnumerable<CheeseRow> rows, string column, bool descending, bool mineFirst, DateTime nowUtc)
        {
            Comparison<CheeseRow> byColumn = column switch
            {
                "multiworld" => (a, b) => string.Compare(a.ProfileName, b.ProfileName, StringComparison.OrdinalIgnoreCase),
                "position" => (a, b) => a.Game.Position.CompareTo(b.Game.Position),
                "ping" => (a, b) => Array.IndexOf(CtStatus.PingIds, EffectivePing(a)).CompareTo(Array.IndexOf(CtStatus.PingIds, EffectivePing(b))),
                "availability" => (a, b) => AvailabilityRank(a.Game).CompareTo(AvailabilityRank(b.Game)),
                "owner" => (a, b) => string.Compare(a.Game.OwnerName ?? "", b.Game.OwnerName ?? "", StringComparison.OrdinalIgnoreCase),
                "game" => (a, b) => string.Compare(a.Game.Game, b.Game.Game, StringComparison.OrdinalIgnoreCase),
                "progression" => (a, b) => ProgressionRank(a.Game).CompareTo(ProgressionRank(b.Game)),
                "status" => (a, b) =>
                {
                    int c = ProgressionRank(a.Game).CompareTo(ProgressionRank(b.Game));
                    return c != 0 ? c : Array.IndexOf(CtStatus.CompletionIds, a.Game.Completion).CompareTo(Array.IndexOf(CtStatus.CompletionIds, b.Game.Completion));
                }
                ,
                "completion" => (a, b) => Array.IndexOf(CtStatus.CompletionIds, a.Game.Completion).CompareTo(Array.IndexOf(CtStatus.CompletionIds, b.Game.Completion)),
                "activity" => (a, b) => (DaysSinceActivity(a.Game, nowUtc) ?? double.PositiveInfinity).CompareTo(DaysSinceActivity(b.Game, nowUtc) ?? double.PositiveInfinity),
                "checks" => (a, b) => ChecksRatio(a.Game).CompareTo(ChecksRatio(b.Game)),
                "hints" => (a, b) => a.UnfoundHints.CompareTo(b.UnfoundHints),
                _ => (a, b) => NaturalCompare(a.Game.Name, b.Game.Name)
            };
            var list = rows.ToList();
            list.Sort((a, b) =>
            {
                if (mineFirst && a.Mine != b.Mine) return a.Mine ? -1 : 1;
                int c = byColumn(a, b);
                if (c != 0) return descending ? -c : c;
                // Ties fall back on the slot name, then the multiworld (stable and predictable).
                c = NaturalCompare(a.Game.Name, b.Game.Name);
                return c != 0 ? c : string.Compare(a.ProfileName, b.ProfileName, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        /// <summary>Compares names with runs of digits as numbers ("Slot 2" before "Slot 10"), ignoring case.</summary>
        public static int NaturalCompare(string a, string b)
        {
            a ??= "";
            b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    // Numbers compare by value: leading zeros skipped, then the longer is larger, then digit by digit
                    // (without making strings: big tables compare a lot).
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    while (si < i && a[si] == '0') si++;
                    while (sj < j && b[sj] == '0') sj++;
                    if (i - si != j - sj) return (i - si).CompareTo(j - sj);
                    for (; si < i; si++, sj++)
                        if (a[si] != b[sj]) return a[si].CompareTo(b[sj]);
                    continue;
                }
                int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }

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
