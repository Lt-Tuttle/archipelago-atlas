#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Pick one of a map pack's own pictures: the likeliest first (named like what it's for), each with its picture when the
    /// pack's images are decoded, a search over every path. Double-click or "Use this picture" confirms. Frees itself.
    /// </summary>
    public partial class PackPicturePicker : ConfirmationDialog
    {
        private readonly LoadedPack _pack;
        private readonly List<string> _ordered;
        private readonly Action<string> _onChosen;
        private LineEdit _search = null!;
        private ItemList _list = null!;
        private readonly List<string> _rows = new List<string>();

        private PackPicturePicker(string title, LoadedPack pack, List<string> ordered, Action<string> onChosen)
        {
            Title = title;
            _pack = pack;
            _ordered = ordered;
            _onChosen = onChosen;
            OkButtonText = MapTrackerControl.Translate("Use this picture");
            MinSize = new Vector2I(720, 560);
            SetMeta("pack_picture_picker", true);
        }

        public override void _Ready()
        {
            var box = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 6);
            AddChild(box);
            _search = new LineEdit { PlaceholderText = MapTrackerControl.Translate("Search the pack's pictures…"), ClearButtonEnabled = true };
            box.AddChild(_search);
            _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 380), FixedIconSize = new Vector2I(40, 40) };
            box.AddChild(_list);
            _search.TextChanged += _ => Fill();
            _list.ItemActivated += i => Choose((int)i);
            Confirmed += () => { var sel = _list.GetSelectedItems(); if (sel.Length > 0) Choose(sel[0]); };
            Canceled += QueueFree;
            Fill();
            Ui.Defer(this, () => _search.GrabFocus());
        }

        /// <summary>The paths listed now, in order (for tests).</summary>
        public IReadOnlyList<string> Listed => _rows;

        private void Fill()
        {
            _list.Clear();
            _rows.Clear();
            string q = _search.Text.Trim();
            foreach (string path in _ordered)
            {
                if (q.Length > 0 && path.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (_rows.Count >= 400) break;
                int i = _list.AddItem(path, _pack.FindImage(path));
                _list.SetItemTooltip(i, path);
                _rows.Add(path);
            }
            if (_rows.Count > 0) _list.Select(0);
        }

        /// <summary>Chooses a listed picture by its path (for tests: what a double-click does).</summary>
        public void ChooseForTests(string path)
        {
            int i = _rows.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) Choose(i);
        }

        private void Choose(int index)
        {
            if (index < 0 || index >= _rows.Count) return;
            _onChosen(_rows[index]);
            Hide();
            QueueFree();
        }

        /// <summary>
        /// Opens the picker over the window: the pack's pictures named like <paramref name="name"/> first, after any
        /// <paramref name="first"/> (likely candidates found otherwise).
        /// </summary>
        public static PackPicturePicker Open(Node parent, string title, LoadedPack pack, string? name, IEnumerable<string>? first, Action<string> onChosen)
        {
            var ordered = (first ?? Enumerable.Empty<string>()).Concat(PackFixActions.PicturesFor(pack, name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var dialog = new PackPicturePicker(title, pack, ordered, onChosen);
            parent.GetTree().Root.AddChild(dialog);
            dialog.PopupCentered();
            return dialog;
        }
    }
}
