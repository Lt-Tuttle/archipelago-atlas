#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Pick one Archipelago item or location by name: suggestions first (with match %), then a live search over
    /// every name. Double-click or "Choose" confirms. Frees itself when closed.
    /// </summary>
    public partial class NamePickerDialog : ConfirmationDialog
    {
        private readonly IReadOnlyDictionary<long, string> _names;
        private readonly List<Suggestion> _suggestions;
        private readonly Action<long, string> _onChosen;
        private readonly string _query;

        private LineEdit _search;
        private ItemList _list;
        private readonly List<(long Id, string Name)> _rows = new List<(long, string)>();

        public NamePickerDialog(string title, string query, IReadOnlyDictionary<long, string> names, List<Suggestion> suggestions, Action<long, string> onChosen)
        {
            Title = title;
            _names = names ?? new Dictionary<long, string>();
            _suggestions = suggestions ?? new List<Suggestion>();
            _onChosen = onChosen;
            _query = query ?? "";
            OkButtonText = "Choose";
            MinSize = new Vector2I(720, 560);
        }

        public override void _Ready()
        {
            var box = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 6);
            AddChild(box);
            if (!string.IsNullOrEmpty(_query)) box.AddChild(new Label { Text = $"For: {_query}", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            _search = new LineEdit { PlaceholderText = "Search names…", ClearButtonEnabled = true };
            box.AddChild(_search);
            _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 380) };
            box.AddChild(_list);

            _search.TextChanged += _ => Fill();
            _list.ItemActivated += i => Choose((int)i);
            Confirmed += () => { var sel = _list.GetSelectedItems(); if (sel.Length > 0) Choose(sel[0]); };
            Canceled += QueueFree;
            Fill();
            Ui.Defer(this, () => _search.GrabFocus());
        }

        private void Fill()
        {
            _list.Clear();
            _rows.Clear();
            string q = _search.Text.Trim();
            if (q.Length == 0)
            {
                foreach (var s in _suggestions)
                {
                    _rows.Add((s.Id, s.Label));
                    _list.AddItem($"{s.Label}   ({s.Score:P0} match)");
                }
                if (_suggestions.Count > 0) { _list.AddItem("──────────"); _rows.Add((0, null)); _list.SetItemDisabled(_list.ItemCount - 1, true); }
            }
            foreach (var kv in _names
                         .Where(kv => q.Length == 0 || kv.Value.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                         .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                         .Take(400))
            {
                _rows.Add((kv.Key, kv.Value));
                _list.AddItem(kv.Value);
            }
            if (_list.ItemCount > 0 && !_list.IsItemDisabled(0)) _list.Select(0);
        }

        private void Choose(int index)
        {
            if (index < 0 || index >= _rows.Count || _rows[index].Name == null) return;
            var (id, name) = _rows[index];
            _onChosen?.Invoke(id, name);
            Hide();
            QueueFree();
        }

        /// <summary>Opens a picker over the given root node.</summary>
        public static void Open(Node parent, string title, string query, IReadOnlyDictionary<long, string> names, List<Suggestion> suggestions, Action<long, string> onChosen)
        {
            var dialog = new NamePickerDialog(title, query, names, suggestions, onChosen);
            parent.GetTree().Root.AddChild(dialog);
            dialog.PopupCentered();
        }
    }
}
