#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AP_Atlas.Core.CheeseTracker
{
    /// <summary>What Atlas knows about a slot right now, from its connection and its logic.</summary>
    public sealed class SlotSnapshot
    {
        /// <summary>Connected, with logic loaded.</summary>
        public bool Live { get; init; }
        /// <summary>Null when Atlas's logic for the slot can be trusted; otherwise why not ("race mode hides logic").</summary>
        public string Untrusted { get; init; }
        public int InLogic { get; init; }
        /// <summary>Locations not checked yet.</summary>
        public int Remaining { get; init; }
        public bool? GoalInLogic { get; init; }
        public bool GoalCompleted { get; init; }
    }

    /// <summary>Atlas's suggestion for a slot's progression status on Cheese Tracker.</summary>
    public sealed class CheeseAdvice
    {
        /// <summary>The status Atlas suggests ("unblocked", "bk", "go"), or null for none.</summary>
        public string Status { get; init; }
        public string Reason { get; init; }
        /// <summary>Why there's no suggestion, when there isn't one and the status doesn't already agree.</summary>
        public string Quiet { get; init; }
        /// <summary>Cheese Tracker already shows what Atlas's logic says.</summary>
        public bool InSync { get; init; }
        /// <summary>The suggestion has held long enough to show on the slot card and to be applied automatically.</summary>
        public bool Ready { get; init; }

        /// <summary>The same suggestion (the reason's details, like how many checks are in logic, aside).</summary>
        public bool SameAs(CheeseAdvice o) =>
            o != null && Status == o.Status && Quiet == o.Quiet && InSync == o.InSync && Ready == o.Ready;
    }

    /// <summary>
    /// The rules for suggesting and automatically setting a slot's Cheese Tracker progression status. Kept free of UI
    /// and network so the self-test can check every rule directly.
    /// </summary>
    public static class CheeseAdvisor
    {
        /// <summary>How long logic must show nothing to do before BK is suggested (items often arrive in bursts).</summary>
        public static TimeSpan BkSettle = TimeSpan.FromMinutes(5);
        /// <summary>How long good news (unblocked, go mode) must hold before it's suggested on the card or applied.</summary>
        public static TimeSpan GoodSettle = TimeSpan.FromMinutes(1);
        /// <summary>Least time between two automatic changes to one slot.</summary>
        public static TimeSpan AutoSpacing = TimeSpan.FromMinutes(5);
        /// <summary>Most automatic changes to one slot in a day; more means something is wrong, so Atlas stops.</summary>
        public const int AutoPerDay = 20;

        /// <summary>The answer Atlas's logic gives, and since when (on the steady clock) it has given it without a break.</summary>
        public sealed class Stability
        {
            public string Status;
            public DateTime Since;
        }

        /// <summary>The progression status Atlas's logic points to, or null and why it can't say.</summary>
        public static (string Status, string Reason, string Quiet) Desired(SlotSnapshot s)
        {
            if (s == null || !s.Live) return (null, null, "Connect this slot for Atlas to suggest a status from its logic.");
            if (s.GoalCompleted) return (null, null, "This slot reached its goal; Cheese Tracker marks that from the Archipelago tracker.");
            if (s.Remaining <= 0) return (null, null, "Every location is checked; Cheese Tracker marks that from the Archipelago tracker.");
            if (s.Untrusted != null) return (null, null, "Atlas won't suggest a status while " + s.Untrusted + ".");
            if (s.GoalInLogic == true) return ("go", "Your goal is in logic", null);
            if (s.InLogic > 0) return ("unblocked", s.InLogic == 1 ? "1 check in logic" : $"{s.InLogic} checks in logic", null);
            return ("bk", "Nothing in logic", null);
        }

        /// <summary>
        /// Atlas's suggestion for a slot, compared with its row on Cheese Tracker. Updates <paramref name="stability"/>.
        /// <paramref name="now"/> is on the steady clock (SteadyClock.UtcNow), so setting the PC's clock can't settle a suggestion early.
        /// </summary>
        public static CheeseAdvice Advise(SlotSnapshot s, CtGame row, Stability stability, DateTime now)
        {
            var (status, reason, quiet) = Desired(s);
            if (status == null || stability.Status != status)
            {
                stability.Status = status;
                stability.Since = now;
            }

            if (row == null) return new CheeseAdvice { Quiet = quiet ?? "This slot isn't on the linked tracker." };
            if (row.IsComplete) return new CheeseAdvice { Quiet = $"Cheese Tracker shows this slot as {CtStatus.Label(row.Completion)}." };
            if (status == null) return new CheeseAdvice { Quiet = quiet };
            if (row.Progression == status) return new CheeseAdvice { InSync = true, Reason = reason };
            // Go mode and Soft BK are judgments a player makes; Atlas only ever adds go mode on top of Soft BK.
            if (row.Progression == "go") return new CheeseAdvice { Quiet = "Cheese Tracker shows go mode; Atlas leaves that to you." };
            if (row.Progression == "soft_bk" && status != "go") return new CheeseAdvice { Quiet = "Soft BK is your call; Atlas only suggests go mode over it." };

            var settle = status == "bk" ? BkSettle : GoodSettle;
            return new CheeseAdvice
            {
                Status = status,
                Reason = status == "bk" ? $"Nothing in logic since {SteadyClock.Shown(stability.Since).ToLocalTime():HH:mm}" : reason,
                Ready = now - stability.Since >= settle
            };
        }

        /// <summary>
        /// Why Atlas must not apply a suggestion by itself right now, or null when it may. <paramref name="pause"/> is set
        /// when the reason should stop automatic updates for the slot until the user resumes them. The times are on the
        /// steady clock (SteadyClock.UtcNow), so setting the PC's clock can't lift the limits.
        /// </summary>
        public static string AutoBlocker(CheeseAdvice advice, CtGame row, int? myUserId, string lastSetByAtlas,
            IReadOnlyList<DateTime> recentAuto, DateTime now, out bool pause)
        {
            pause = false;
            if (advice?.Status == null || !advice.Ready) return "nothing to change yet";
            if (row == null) return "the slot isn't on the tracker";
            if (myUserId == null || row.ClaimedByUserId != myUserId) return "automatic updates only change slots claimed by your Cheese Tracker account";
            if (row.IsComplete) return "the slot is complete";
            if (row.Progression == "soft_bk") return "Soft BK is yours to change";
            if (lastSetByAtlas != null && row.Progression != lastSetByAtlas)
            {
                pause = true;
                return $"the status was changed on Cheese Tracker (to {CtStatus.Label(row.Progression)}) since Atlas last set it";
            }
            if (recentAuto != null && recentAuto.Count > 0 && now - recentAuto.Max() < AutoSpacing) return "Atlas changed it less than 5 minutes ago";
            if (recentAuto != null && recentAuto.Count(t => now - t < TimeSpan.FromDays(1)) >= AutoPerDay)
            {
                pause = true;
                return $"Atlas already changed it {AutoPerDay} times today";
            }
            return null;
        }
    }
}
