#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// What a bulk fix will change, before it changes anything (the owner, 2026-10-13: every bulk action previews first):
    /// one line of counts, then the rows by what happens to them (linked, set aside, skipped because they clash, replacing
    /// a link of the user's), and Apply / Cancel. Apply carries out the same <see cref="BulkPlan"/> the dialog shows.
    /// <see cref="ShowAll"/> shows several packs' plans as one preview with one Apply (Add games' "Fix them now").
    /// </summary>
    public partial class FixPreviewDialog : ConfirmationDialog
    {
        /// <summary>The most rows listed per group (the rest are counted).</summary>
        public const int RowsShown = 40;

        /// <summary>The plan shown.</summary>
        public BulkPlan Plan { get; }

        /// <summary>Every plan the dialog shows (one for a pack's own preview; each pack's for Add games' "Fix them now").</summary>
        public IReadOnlyList<BulkPlan> Plans { get; }

        private readonly Action _apply;
        private bool _answered;

        /// <summary>The last preview opened while it's open (for tests).</summary>
        public static FixPreviewDialog? Showing { get; private set; }

        private FixPreviewDialog(IReadOnlyList<BulkPlan> plans, Action apply)
        {
            Plans = plans;
            Plan = plans[0];
            _apply = apply;
        }

        /// <summary>
        /// Shows a plan with Apply / Cancel; <paramref name="apply"/> runs on Apply. A plan with nothing to change is said
        /// on <paramref name="nothing"/> instead (no dialog).
        /// </summary>
        public static void Show(Node parent, string title, BulkPlan plan, DoctorReport report, Action apply, Action<string>? nothing = null)
        {
            if (plan.Nothing)
            {
                nothing?.Invoke(plan.Summary());
                return;
            }
            var (dialog, list) = Build(new[] { plan }, title, plan.Summary(), plan.Reason, apply);
            AddRows(list, plan, report);
            Open(parent, dialog);
        }

        /// <summary>
        /// One preview for several packs' plans (Add games' "Fix them now"): the total, then each pack's name, its line and its
        /// rows; one Apply carries out every plan shown. Plans that change nothing are left out; when none changes anything,
        /// <paramref name="nothing"/> says so instead (no dialog).
        /// </summary>
        public static void ShowAll(Node parent, string title, IReadOnlyList<(string Name, BulkPlan Plan, DoctorReport Report)> packs, Action apply, Action<string>? nothing = null)
        {
            var changing = packs.Where(p => !p.Plan.Nothing).ToList();
            if (changing.Count == 0)
            {
                nothing?.Invoke(Kit.Translate("Nothing to change."));
                return;
            }
            var (dialog, list) = Build(changing.Select(p => p.Plan).ToList(), title, Total(changing), null, apply);
            foreach (var (name, plan, report) in changing)
            {
                var heading = Kit.Text(name, ThemeColors.Heading);
                heading.SetMeta("fix_preview_pack", name);
                list.AddChild(heading);
                list.AddChild(Kit.Subtle(plan.Summary()));
                AddRows(list, plan, report);
            }
            Open(parent, dialog);
        }

        private static string Total(IReadOnlyList<(string Name, BulkPlan Plan, DoctorReport Report)> packs)
        {
            static string N(int n) => n.ToString(System.Globalization.CultureInfo.CurrentCulture);
            string line = packs.Count == 1
                ? Kit.Translate("1 map pack: links {1}, sets {2} aside, skips {3} that clash. One step: Undo in the Pack Doctor reverses it.")
                : Kit.Translate("{0} map packs: links {1}, sets {2} aside, skips {3} that clash. One step per pack: Undo in the Pack Doctor reverses each.");
            return line.Replace("{0}", N(packs.Count)).Replace("{1}", N(packs.Sum(p => p.Plan.Link.Count)))
                .Replace("{2}", N(packs.Sum(p => p.Plan.SetAside.Count))).Replace("{3}", N(packs.Sum(p => p.Plan.Skipped.Count)));
        }

        private static (FixPreviewDialog Dialog, VBoxContainer List) Build(IReadOnlyList<BulkPlan> plans, string title, string summaryText, string? reason, Action apply)
        {
            var dialog = new FixPreviewDialog(plans, apply) { Title = title, OkButtonText = Kit.Translate("Apply"), DialogHideOnOk = false };
            dialog.SetMeta("fix_preview", true);
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            var summary = new Label { Text = summaryText, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(580, 0) };
            summary.AddThemeColorOverride("font_color", ThemeColors.Text);
            box.AddChild(summary);
            if (!string.IsNullOrEmpty(reason)) box.AddChild(Kit.Subtle(reason));
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(580, 280), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 2);
            scroll.AddChild(list);
            box.AddChild(scroll);
            dialog.AddChild(box);
            return (dialog, list);
        }

        private static void AddRows(VBoxContainer list, BulkPlan plan, DoctorReport report)
        {
            Group(list, Kit.Translate("Linked"), plan.Link.Select(x => Line(report, x.Finding, plan.ReplacesYours.TryGetValue(x.Finding.Key, out var yours)
                ? $"{x.Pick.Label}  ({Kit.Translate("replaces your link to {0}").Replace("{0}", yours)})"
                : $"{x.Pick.Label}  ({x.Pick.Score:P0})")).ToList(), ThemeColors.Success);
            Group(list, Kit.Translate("Set aside (they stay in the report to the author; Undo brings them back)"), plan.SetAside.Select(f => Line(report, f, f.Suggestions.Count > 0
                ? Kit.Translate("best match {0}, {1}").Replace("{0}", f.Suggestions[0].Label).Replace("{1}", f.Suggestions[0].Score.ToString("P0", System.Globalization.CultureInfo.CurrentCulture))
                : Kit.Translate("no match"))).ToList(), ThemeColors.TextMuted);
            Group(list, Kit.Translate("Skipped: they clash"), plan.Skipped.Select(x => Line(report, x.Finding, x.Why)).ToList(), ThemeColors.Warning);
        }

        private static void Open(Node parent, FixPreviewDialog dialog)
        {
            dialog.Confirmed += () => dialog.Answer(true);
            dialog.Canceled += () => dialog.Answer(false);
            dialog.TreeExiting += () =>
            {
                dialog._answered = true;
                if (Showing == dialog) Showing = null;
            };
            Showing = dialog;
            parent.AddChild(dialog);
            WindowFit.Pop(dialog, 640);
        }

        private static string Line(DoctorReport report, Finding f, string what)
        {
            string kind = f.Actions.HasFlag(FindingActions.LinkItem) ? Kit.Translate("Tile") : Kit.Translate("Pin");
            string subject = f.Subject.Substring(f.Subject.IndexOf(':') + 1);
            string where = f.Actions.HasFlag(FindingActions.LinkItem) ? "" : "  " + subject.Split('|')[0];
            return $"{kind}  {PackDoctor.RowName(report, f)}{where}  →  {what}";
        }

        private static void Group(VBoxContainer list, string title, List<string> lines, Color colour)
        {
            if (lines.Count == 0) return;
            var heading = Kit.Text($"{title} ({lines.Count})", colour);
            list.AddChild(heading);
            foreach (string line in lines.Take(RowsShown))
            {
                var label = Kit.Subtle(line);
                label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                label.CustomMinimumSize = new Vector2(540, 0);
                list.AddChild(label);
            }
            if (lines.Count > RowsShown) list.AddChild(Kit.Subtle(Kit.Translate("… and {0} more").Replace("{0}", (lines.Count - RowsShown).ToString(System.Globalization.CultureInfo.CurrentCulture))));
        }

        private void Answer(bool apply)
        {
            if (_answered) return;
            _answered = true;
            if (Showing == this) Showing = null;
            QueueFree();
            if (apply) _apply();
        }

        /// <summary>Presses Apply or Cancel (for tests).</summary>
        public void ChooseForTests(bool apply) => Answer(apply);
    }
}
