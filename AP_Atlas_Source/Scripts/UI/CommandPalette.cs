using System;
using System.Collections.Generic;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The command palette (Ctrl+Shift+P): type a few letters, pick a command, Enter runs it. It lists the same commands
    /// as the menus, with their keys. A typed word matches the start of a word in a command's name or menu, so "map p"
    /// finds Map Packs and not Map Tracker. It frees itself when it closes.
    /// </summary>
    public partial class CommandPalette : PopupPanel
    {
        /// <summary>The command that opens the palette: the one command it never lists.</summary>
        public const string OwnCommandId = "view.command-palette";

        private readonly AP_Atlas.Core.Commands _commands;
        private readonly Func<string, string> _tr;
        private readonly List<AP_Atlas.Core.Command> _shown = new();
        private LineEdit _input = null!;
        private Tree _list = null!;
        private Label _nothing = null!;

        /// <param name="tr">Translates text as it's shown.</param>
        public CommandPalette(AP_Atlas.Core.Commands commands, Func<string, string> tr)
        {
            _commands = commands;
            _tr = tr;
        }

        public override void _Ready()
        {
            AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = AP_Atlas.Core.ThemeColors.SurfacePanel,
                BorderColor = AP_Atlas.Core.ThemeColors.BorderSoft,
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft = 8,
                ContentMarginRight = 8,
                ContentMarginTop = 8,
                ContentMarginBottom = 8
            });
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(560, 380) };
            box.AddThemeConstantOverride("separation", 6);
            _input = new LineEdit { PlaceholderText = _tr("Type a command…"), ClearButtonEnabled = true };
            _input.TextChanged += _ => Refresh();
            _input.GuiInput += OnInputKey;
            box.AddChild(_input);
            _list = new Tree
            {
                Columns = 2,
                HideRoot = true,
                SelectMode = Tree.SelectModeEnum.Row,
                FocusMode = Control.FocusModeEnum.None,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill
            };
            _list.SetColumnExpand(0, true);
            _list.SetColumnExpand(1, false);
            _list.SetColumnCustomMinimumWidth(1, 130);
            _list.ItemActivated += RunSelected;
            box.AddChild(_list);
            _nothing = new Label { Text = _tr("No command matches."), HorizontalAlignment = HorizontalAlignment.Center, Visible = false };
            box.AddChild(_nothing);
            AddChild(box);
            PopupHide += QueueFree;
            Refresh();
        }

        /// <summary>Opens the palette near the top of the window, with the typing box focused.</summary>
        public void Open(Control window)
        {
            var size = new Vector2I(580, 400);
            var area = window.GetViewportRect().Size;
            Popup(new Rect2I(new Vector2I(Math.Max(0, ((int)area.X - size.X) / 2), 60), size));
            _input.GrabFocus();
        }

        private void OnInputKey(InputEvent @event)
        {
            if (@event is not InputEventKey key || !key.Pressed) return;
            switch (key.Keycode)
            {
                case Key.Down: Move(1); break;
                case Key.Up: Move(-1); break;
                case Key.Enter:
                case Key.KpEnter: RunSelected(); break;
                case Key.Escape: Hide(); break;
                default: return;
            }
            _input.AcceptEvent();
        }

        private void Refresh()
        {
            _list.Clear();
            _shown.Clear();
            var root = _list.CreateItem();
            var typed = AP_Atlas.Core.WordSearch.Words(_input.Text);
            foreach (var command in _commands.All)
            {
                if (command.Id == OwnCommandId || !command.Enabled() || !Matches(typed, command)) continue;
                var row = _list.CreateItem(root);
                row.SetText(0, _tr(command.Title));
                row.SetText(1, _commands.ShortcutOf(command.Id));
                row.SetTooltipText(0, _tr(command.Menu));
                _shown.Add(command);
            }
            if (_shown.Count > 0) root.GetFirstChild().Select(0);
            _nothing.Visible = _shown.Count == 0;
        }


        /// <summary>Every typed word starts a word of the command's name or menu.</summary>
        private bool Matches(string[] typed, AP_Atlas.Core.Command command) =>
            AP_Atlas.Core.WordSearch.Matches(typed, _tr(command.Title), _tr(command.Menu));

        private int SelectedIndex()
        {
            var selected = _list.GetSelected();
            int index = 0;
            for (var item = _list.GetRoot()?.GetFirstChild(); item != null; item = item.GetNext(), index++)
                if (item == selected) return index;
            return -1;
        }

        private void Move(int delta)
        {
            if (_shown.Count == 0) return;
            int index = Math.Clamp(SelectedIndex() + delta, 0, _shown.Count - 1);
            var item = _list.GetRoot().GetFirstChild();
            for (int i = 0; i < index && item != null; i++) item = item.GetNext();
            if (item == null) return;
            item.Select(0);
            _list.ScrollToItem(item);
        }

        private void RunSelected()
        {
            int index = SelectedIndex();
            if (index < 0 || index >= _shown.Count) return;
            var command = _shown[index];
            Hide();
            _commands.Run(command.Id);
        }
    }
}
