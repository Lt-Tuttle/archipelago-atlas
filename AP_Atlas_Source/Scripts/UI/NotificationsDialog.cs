using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Window → Notifications: everything the alert feed told the user this session, newest first, with when and what
    /// kind; opening it marks them seen, Clear empties the list. It follows the log while open, and frees itself when closed.
    /// The rows are a table like any other: searchable, sortable, exportable.
    /// </summary>
    public sealed partial class NotificationsDialog : AcceptDialog
    {
        private readonly AlertLog _log;
        private readonly Func<string, string> _tr;
        private readonly AtlasTable _table;

        public NotificationsDialog(AlertLog log, AppSettings settings, Func<string, string> tr, Action<string, Color> toast)
        {
            _log = log;
            _tr = tr;
            Title = _tr("Notifications");
            OkButtonText = _tr("Close");
            MinSize = new Vector2I(720, 460);
            Unresizable = false;
            _table = new AtlasTable("notifications", settings, tr) { Toast = toast, EmptyText = _tr("Nothing yet this session.") };
            _table.SetColumns(new List<AtlasTable.Column>
            {
                new() { Id = "when", Title = "When", MinWidth = 90, Ratio = 0 },
                new() { Id = "kind", Title = "Kind", MinWidth = 90, Ratio = 0 },
                new() { Id = "what", Title = "What", MinWidth = 300, Ratio = 1 }
            });
            _table.CustomMinimumSize = new Vector2(680, 360);
            AddChild(_table);
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
        public int RowCount => _table.ShownRows.Count;

        /// <summary>The table, for the window's tests.</summary>
        public AtlasTable Table => _table;

        private void Render()
        {
            var rows = _log.Entries.Select(entry => new AtlasTable.Row
            {
                Key = entry.Id.ToString(),
                Cells = new[]
                {
                    new AtlasTable.Cell(entry.At.ToString("HH:mm:ss"), ThemeColors.TextMuted, null, entry.At),
                    new AtlasTable.Cell(_tr(entry.Kind.ToString()), KindColor(entry.Kind), null, (int)entry.Kind),
                    new AtlasTable.Cell(entry.Message, null, entry.Message)
                }
            }).ToList();
            _table.SetRows(rows);
        }

        private static Color KindColor(AlertKind kind) => kind switch
        {
            AlertKind.Error => ThemeColors.Error,
            AlertKind.Warning => ThemeColors.Warning,
            AlertKind.Success => ThemeColors.Success,
            _ => ThemeColors.TextMuted
        };
    }
}
