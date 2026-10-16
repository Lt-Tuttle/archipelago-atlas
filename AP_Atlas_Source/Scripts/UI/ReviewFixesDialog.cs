#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Review fixes (the owner, 2026-10-16): what Atlas fixed by itself in one map pack, a line each with why it was sure, each
    /// with Undo, and Undo all. What's undone is declined: Atlas's automatic pass leaves it to the user from then on (it waits on
    /// the map's Notices). The rows are read again after each undo. Main thread.
    /// </summary>
    public sealed partial class ReviewFixesDialog : AcceptDialog
    {
        /// <summary>The dialog open now (for tests).</summary>
        public static ReviewFixesDialog? Showing { get; private set; }

        private readonly Func<IReadOnlyList<PackChange>> _rows;
        private readonly Action<PackChange> _undo;
        private readonly Action<IReadOnlyList<PackChange>> _undoAll;
        private readonly VBoxContainer _list = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        private readonly Label _lead = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(580, 0) };
        private readonly Button _undoAllButton;

        /// <summary>Opens Review fixes for a pack (its fix key and its zip) over <paramref name="parent"/>; an open one is replaced.</summary>
        public static ReviewFixesDialog Open(Node parent, string packKey, string zipPath)
        {
            LoadedPack? original = null;
            try { original = PopTrackerPackLoader.InspectZipPack(zipPath); }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
            {
                AP_Atlas.Core.Logger.LogWarning($"Review fixes couldn't read {System.IO.Path.GetFileName(zipPath)}: {ex.Message}");
            }
            string name = original?.Manifest?.Name ?? System.IO.Path.GetFileNameWithoutExtension(zipPath);
            return Show(parent, Kit.Translate("What Atlas fixed in {0}").Replace("{0}", name),
                () => PackChanges.Of(PackFixes.Get(packKey), original, Kit.Translate, unaskedOnly: true),
                change => PackChanges.Undo(packKey, change),
                changes => PackChanges.UndoAll(packKey, changes.ToList()));
        }

        /// <summary>Opens it on the caller's rows and undos (Open's pack, the visual check's made-up rows).</summary>
        public static ReviewFixesDialog Show(Node parent, string title, Func<IReadOnlyList<PackChange>> rows, Action<PackChange> undo, Action<IReadOnlyList<PackChange>> undoAll)
        {
            if (Showing != null && IsInstanceValid(Showing)) Showing.QueueFree();
            var dialog = new ReviewFixesDialog(title, rows, undo, undoAll);
            Showing = dialog;
            dialog.TreeExiting += () => { if (Showing == dialog) Showing = null; };
            parent.AddChild(dialog);
            WindowFit.Pop(dialog, 640);
            return dialog;
        }

        private ReviewFixesDialog(string title, Func<IReadOnlyList<PackChange>> rows, Action<PackChange> undo, Action<IReadOnlyList<PackChange>> undoAll)
        {
            Title = title;
            OkButtonText = Kit.Translate("Close");
            Exclusive = false;
            SetMeta("review_fixes", true);
            _rows = rows;
            _undo = undo;
            _undoAll = undoAll;
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            box.AddChild(_lead);
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(580, 240), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            _list.AddThemeConstantOverride("separation", 6);
            scroll.AddChild(_list);
            box.AddChild(scroll);
            _undoAllButton = Kit.Button(Kit.Translate("Undo all"), Kit.Translate("Takes back every fix listed here in one step; Atlas leaves them to you from then on."), PressUndoAll, small: true);
            _undoAllButton.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            box.AddChild(_undoAllButton);
            AddChild(box);
            Confirmed += () => QueueFree();
            Canceled += () => QueueFree();
            Render();
        }

        /// <summary>Each row's line as shown (for tests).</summary>
        public List<string> RowTexts() => _list.FindChildren("*", nameof(Label), true, false).OfType<Label>().Where(l => l.HasMeta("review_row")).Select(l => l.Text).ToList();

        /// <summary>A row's Undo (for tests, and its button).</summary>
        public void PressUndo(string subject)
        {
            var row = _rows().FirstOrDefault(c => c.Subject == subject);
            if (row == null) return;
            _undo(row);
            Render();
        }

        /// <summary>Undo all (for tests, and its button).</summary>
        public void PressUndoAll()
        {
            var rows = _rows();
            if (rows.Count == 0) return;
            _undoAll(rows);
            Render();
        }

        private void Render()
        {
            foreach (Node child in _list.GetChildren())
            {
                _list.RemoveChild(child);
                child.QueueFree();
            }
            var rows = _rows();
            _lead.Text = rows.Count == 0 ? Kit.Translate("Nothing Atlas fixed by itself is left in this pack: what you undid waits for you on the map's Notices.")
                : rows.Count == 1 ? Kit.Translate("Atlas made 1 fix by itself, sure of it as said below. Undo takes it back, and Atlas leaves it to you from then on.")
                : Kit.Translate("Atlas made {0} fixes by itself, sure of each as said below. Undo takes one back, and Atlas leaves it to you from then on.").Replace("{0}", rows.Count.ToString(CultureInfo.CurrentCulture));
            _undoAllButton.Disabled = rows.Count == 0;
            foreach (var change in rows)
            {
                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 8);
                var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                var text = new Label { Text = change.Text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0) };
                text.SetMeta("review_row", change.Subject);
                column.AddChild(text);
                column.AddChild(Kit.Subtle(PackChangeWords.ByAtlas(change.Why, Kit.Translate)));
                row.AddChild(column);
                string subject = change.Subject;
                var undo = Kit.Button(Kit.Translate("Undo"), Kit.Translate("Takes this fix back; Atlas leaves it to you from then on."), () => PressUndo(subject), small: true);
                undo.SetMeta("review_undo", subject);
                undo.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
                row.AddChild(undo);
                _list.AddChild(row);
            }
        }
    }
}
