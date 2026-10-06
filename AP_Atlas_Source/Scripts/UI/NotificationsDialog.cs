using System;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Window → Notifications: everything the alert feed told the user this session, newest first, with when and what
    /// kind; opening it marks them seen, Clear empties the list. It follows the log while open, and frees itself when closed.
    /// </summary>
    public sealed partial class NotificationsDialog : AcceptDialog
    {
        private readonly AP_Atlas.Core.AlertLog _log;
        private readonly Func<string, string> _tr;
        private readonly Tree _tree = new();
        private readonly Label _empty;

        public NotificationsDialog(AP_Atlas.Core.AlertLog log, Func<string, string> tr)
        {
            _log = log;
            _tr = tr;
            Title = _tr("Notifications");
            OkButtonText = _tr("Close");
            MinSize = new Vector2I(720, 460);
            Unresizable = false;
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 8);
            _tree.Columns = 3;
            _tree.HideRoot = true;
            _tree.SetColumnTitlesVisible(true);
            _tree.SetColumnTitle(0, _tr("When"));
            _tree.SetColumnTitle(1, _tr("Kind"));
            _tree.SetColumnTitle(2, _tr("What"));
            _tree.SetColumnExpand(0, false);
            _tree.SetColumnExpand(1, false);
            _tree.SetColumnExpand(2, true);
            _tree.SetColumnCustomMinimumWidth(0, 90);
            _tree.SetColumnCustomMinimumWidth(1, 90);
            _tree.CustomMinimumSize = new Vector2(680, 360);
            _tree.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            box.AddChild(_tree);
            _empty = new Label { Text = _tr("Nothing yet this session."), HorizontalAlignment = HorizontalAlignment.Center };
            _empty.AddThemeColorOverride("font_color", Colors.Gray);
            box.AddChild(_empty);
            AddChild(box);
            AddButton(_tr("Clear"), false, "clear");
            CustomAction += action =>
            {
                if (action == "clear") _log.Clear();
            };
            AddChild(new TreeSubscriptions().On(() => _log.Changed += Render, () => _log.Changed -= Render));
            Confirmed += QueueFree;
            Canceled += QueueFree;
            _log.MarkSeen();
            Render();
        }

        /// <summary>The rows shown, newest first: when, kind, what.</summary>
        public int RowCount { get; private set; }

        private void Render()
        {
            _tree.Clear();
            var root = _tree.CreateItem();
            RowCount = 0;
            foreach (var entry in _log.Entries)
            {
                var row = _tree.CreateItem(root);
                row.SetText(0, entry.At.ToString("HH:mm:ss"));
                row.SetText(1, _tr(entry.Kind.ToString()));
                row.SetText(2, entry.Message);
                row.SetTooltipText(2, entry.Message);
                RowCount++;
            }
            _empty.Visible = RowCount == 0;
        }
    }
}
