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
    /// Importing someone's fixes for a pack, before anything changes: what it adds, what you have already, and where you both
    /// fixed the same thing (yours stays unless you tick theirs); Import does it as one undoable step.
    /// </summary>
    public sealed partial class ImportFixesDialog : ConfirmationDialog
    {
        private readonly HashSet<string> _theirs = new(StringComparer.Ordinal);
        private readonly Action<ISet<string>> _import;
        private bool _answered;

        /// <summary>The dialog open now (for tests), or null.</summary>
        public static ImportFixesDialog? Showing { get; private set; }

        /// <summary>What importing does.</summary>
        public ImportPlan Plan { get; }

        public ImportFixesDialog(ImportPlan plan, string from, Action<ISet<string>> import)
        {
            Plan = plan;
            _import = import;
            Title = Kit.Translate("Import fixes");
            OkButtonText = Kit.Translate("Import");
            DialogHideOnOk = false;
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            var summary = new Label
            {
                Text = Kit.Translate("From {0}: adds {1}, {2} you have already, {3} where you both fixed the same thing (yours stays unless you tick theirs), {4} findings set aside. One step: Undo reverses it.")
                    .Replace("{0}", from).Replace("{1}", plan.Adds.Count.ToString()).Replace("{2}", plan.Same.Count.ToString()).Replace("{3}", plan.Clashes.Count.ToString()).Replace("{4}", plan.Ignored.Count.ToString()),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(600, 0)
            };
            box.AddChild(summary);
            if (plan.Warning != null)
            {
                var warning = Kit.Text(plan.Warning, ThemeColors.Warning);
                warning.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                warning.CustomMinimumSize = new Vector2(600, 0);
                box.AddChild(warning);
            }
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(600, 260), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 2);
            scroll.AddChild(list);
            if (plan.Clashes.Count > 0)
            {
                list.AddChild(Kit.Text(Kit.Translate("You both fixed these: tick to use theirs"), ThemeColors.Warning));
                foreach (var row in plan.Clashes)
                {
                    var tick = new CheckBox { Text = Kit.Translate("Theirs: {0}   (yours: {1})").Replace("{0}", row.Theirs).Replace("{1}", row.Yours ?? ""), AccessibilityName = Kit.Translate("Use theirs") };
                    string key = row.Key;
                    tick.SetMeta("import_clash", key);
                    tick.Toggled += on => TakeTheirs(key, on);
                    list.AddChild(tick);
                }
            }
            if (plan.Adds.Count > 0)
            {
                list.AddChild(Kit.Text(Kit.Translate("Adds"), ThemeColors.Success));
                foreach (var row in plan.Adds.Take(200)) list.AddChild(Kit.Subtle(row.Theirs));
                if (plan.Adds.Count > 200) list.AddChild(Kit.Subtle(Kit.Translate("… and {0} more").Replace("{0}", (plan.Adds.Count - 200).ToString())));
            }
            box.AddChild(scroll);
            AddChild(box);
            Confirmed += () => Answer(true);
            Canceled += () => Answer(false);
            TreeExiting += () =>
            {
                _answered = true;
                if (Showing == this) Showing = null;
            };
            Showing = this;
        }

        /// <summary>Uses the file's fix where you both fixed the same thing (for tests: what its tick does).</summary>
        public void TakeTheirs(string key, bool on = true)
        {
            if (on) _theirs.Add(key);
            else _theirs.Remove(key);
        }

        /// <summary>Presses Import or Cancel (for tests).</summary>
        public void ChooseForTests(bool import) => Answer(import);

        private void Answer(bool import)
        {
            if (_answered) return;
            _answered = true;
            if (Showing == this) Showing = null;
            QueueFree();
            if (import) _import(_theirs);
        }
    }
}
