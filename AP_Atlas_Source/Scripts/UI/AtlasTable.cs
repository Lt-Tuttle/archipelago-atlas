using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AP_Atlas.Core;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The table every list of rows in Atlas is. A click on a column's title sorts by it (a second click turns the order;
    /// the arrow says which); a right-click on a title hides or shows columns; typed words narrow the rows (each word must
    /// start a word of a cell, as everywhere in Atlas); the shown rows export as TSV, Markdown, Discord messages or a CSV
    /// file; a right-click on a row offers the host's actions and "Copy row". The sort and the hidden columns are
    /// remembered per table (<see cref="AppSettings.Tables"/>). The selection and the scroll survive a change of rows, and
    /// the cells are updated in place.
    /// Only the rows on screen are laid out: the tree holds as many items as fit, filled from the shown rows at the scroll
    /// position, and the table's own scroll bar (<see cref="Scroll"/>) stands for all of them. Godot's Tree lays out every
    /// item it holds, so a table of thousands of rows took half a second to show; this way it takes the same as one of ten.
    /// The wheel and the keys (arrows, Page Up and Down, Home, End) move through the rows, not the items.
    /// The host gives columns (<see cref="SetColumns"/>) and rows (<see cref="SetRows"/>); each cell has its text, colour,
    /// tooltip and, when the text isn't what it should sort by, a sort key. The search box, count, Columns and Export
    /// menus sit in <see cref="Toolbar"/> above the rows; a host with a toolbar of its own moves them there with
    /// <see cref="Take"/> and hides the rest.
    /// </summary>
    public sealed partial class AtlasTable : VBoxContainer
    {
        public sealed class Column
        {
            public string Id = "";
            public string Title = "";
            /// <summary>The least width at the pane's usual text size; it scales with the text.</summary>
            public int MinWidth = 60;
            /// <summary>The column's share of the spare width; 0 keeps it at its least width.</summary>
            public int Ratio = 1;
            public HorizontalAlignment Align = HorizontalAlignment.Left;
            public bool Sortable = true;
            /// <summary>The first click on the title sorts the column downward (most hints, least active first).</summary>
            public bool DescendingFirst;
        }

        public sealed class Cell
        {
            public string Text = "";
            public Color? Color;
            public string? Tooltip;
            /// <summary>What the column sorts by for this cell when not its text (a number, a rank, a time).</summary>
            public IComparable? SortKey;
            public Texture2D? Icon;
            public Color? Background;

            public Cell() { }

            public Cell(string text, Color? color = null, string? tooltip = null, IComparable? sortKey = null)
            {
                Text = text;
                Color = color;
                Tooltip = tooltip;
                SortKey = sortKey;
            }
        }

        public sealed class Row
        {
            /// <summary>Names the row across changes (the selection follows it).</summary>
            public string Key = "";
            /// <summary>Whatever the host wants back with the row.</summary>
            public object? Tag;
            /// <summary>One cell per column of <see cref="SetColumns"/>, hidden ones included.</summary>
            public Cell[] Cells = Array.Empty<Cell>();
            /// <summary>Sorts first whichever way the table sorts (your own slots).</summary>
            public bool Pinned;
            /// <summary>Drawn in the quiet text colour (done, found).</summary>
            public bool Dim;
            /// <summary>Words the search also looks at, beyond the cells (a slot's notes).</summary>
            public string? SearchText;
        }

        private readonly string _id;
        private readonly AppSettings _settings;
        private readonly Func<string, string> _tr;
        private IReadOnlyList<Column> _columns = Array.Empty<Column>();
        private List<Column> _shownColumns = new();
        private IReadOnlyList<Row> _rows = Array.Empty<Row>();
        private List<Row> _shownRows = new();
        private readonly Dictionary<string, Row> _byKey = new();
        private string? _selectedKey;
        private bool _suppress;
        // The rows on screen: _shownRows[_first..] fill the tree's items, _visible of them at most.
        private int _first;
        private int _visible = 1;
        // A row's pitch and the first row's top, measured from the tree (0 until measured).
        private float _pitch, _top;
        private bool _settingScroll;
        // The frame a row was selected by the table itself (not the user), so the tree's selection signal isn't taken for a pick.
        private ulong _selectFrame = ulong.MaxValue;
        private string _defaultSort = "";
        private readonly Timer _searchDebounce;

        public Tree Tree { get; }
        /// <summary>The table's scroll bar: its range is every shown row, its page the rows on screen.</summary>
        public VScrollBar Scroll { get; }
        public HBoxContainer Toolbar { get; }
        public LineEdit SearchBox { get; }
        public Label CountLabel { get; }
        public MenuButton ColumnsMenu { get; }
        public MenuButton ExportMenu { get; }

        /// <summary>The rows before the host's own filters, for "n of m" (the table's own search counts by itself).</summary>
        public int? TotalCount { get; set; }

        public string EmptyText { get; set; }
        public string NoMatchText { get; set; }

        /// <summary>The most rows drawn (a room's page can hold tens of thousands); the count says so.</summary>
        public int MaxRows { get; set; } = int.MaxValue;

        /// <summary>Alternate row backgrounds.</summary>
        public bool Stripes { get; set; } = true;

        /// <summary>The host's actions for a row's right-click menu ("Copy row" is added after them).</summary>
        public Func<Row, IEnumerable<(string Label, Action Action, bool Enabled)>>? RowMenu { get; set; }

        /// <summary>Runs after a row's cells are drawn, for what cells can't say (an editable cell, an icon's width).</summary>
        public Action<TreeItem, Row>? Customize { get; set; }

        /// <summary>Where "Copied…" goes.</summary>
        public Action<string, Color>? Toast { get; set; }

        /// <summary>The user picked a row (a click, or the keyboard), or cleared the selection.</summary>
        public event Action<Row?>? SelectionChanged;

        /// <summary>The user clicked a cell: the row and the column's id (null from the keyboard).</summary>
        public event Action<Row, string?>? CellPicked;

        /// <summary>A double-click or Enter on a row.</summary>
        public event Action<Row>? Activated;

        /// <summary>The sort column or direction changed by a click.</summary>
        public event Action? SortChanged;

        public AtlasTable(string id, AppSettings settings, Func<string, string> tr)
        {
            _id = id;
            _settings = settings;
            _tr = tr;
            Name = "Table_" + id;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            AddThemeConstantOverride("separation", 6);
            EmptyText = tr("Nothing to show yet.");
            NoMatchText = tr("Nothing matches.");

            Toolbar = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            Toolbar.AddThemeConstantOverride("separation", 6);
            AddChild(Toolbar);
            SearchBox = new LineEdit { PlaceholderText = tr("Find…"), ClearButtonEnabled = true, CustomMinimumSize = new Vector2(220, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill, CaretBlink = false, TooltipText = tr("Type words: each must start a word of a cell") };
            SearchBox.AccessibilityName = tr("Find in the table");
            SearchBox.TextChanged += _ => _searchDebounce!.Start();
            Toolbar.AddChild(SearchBox);
            CountLabel = new Label { VerticalAlignment = VerticalAlignment.Center };
            CountLabel.AddThemeColorOverride("font_color", ThemeColors.TextSubtle);
            Toolbar.AddChild(CountLabel);
            ColumnsMenu = new MenuButton { Text = tr("Columns") + " ▾", Flat = true, TooltipText = tr("Show or hide columns (also a right-click on a column's title)") };
            ColumnsMenu.GetPopup().AboutToPopup += () => FillColumnsMenu(ColumnsMenu.GetPopup());
            ColumnsMenu.GetPopup().IdPressed += id => ToggleColumn((int)id);
            Toolbar.AddChild(ColumnsMenu);
            ExportMenu = new MenuButton { Text = tr("Export") + " ▾", Flat = true, TooltipText = tr("Copy or save the rows shown, in their order") };
            var export = ExportMenu.GetPopup();
            export.AddItem(tr("Copy as TSV (for a spreadsheet)"), (int)ExportFormat.Tsv);
            export.AddItem(tr("Copy as a Markdown table"), (int)ExportFormat.Markdown);
            export.AddItem(tr("Copy for Discord"), (int)ExportFormat.Discord);
            export.AddItem(tr("Save as CSV…"), (int)ExportFormat.Csv);
            export.IdPressed += id =>
            {
                if ((ExportFormat)id == ExportFormat.Csv) AskSaveCsv();
                else CopyShown((ExportFormat)id);
            };
            Toolbar.AddChild(ExportMenu);

            Tree = new Tree
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                ColumnTitlesVisible = true,
                HideRoot = true,
                SelectMode = Tree.SelectModeEnum.Row,
                AllowRmbSelect = true,
                CustomMinimumSize = new Vector2(0, 160)
            };
            Tree.AddThemeConstantOverride("v_separation", 6);
            Tree.ColumnTitleClicked += OnTitleClicked;
            Tree.ItemMouseSelected += (position, button) =>
            {
                if (button == (long)MouseButton.Right) ShowRowMenu();
            };
            Tree.ItemActivated += () =>
            {
                var row = Selected;
                if (row != null) Activated?.Invoke(row);
            };
            TreePicks.Hook(Tree, (item, column) =>
            {
                // The table selected the row itself (drawing, scrolling, Select): not a pick.
                if (column < 0 && Godot.Engine.GetProcessFrames() == _selectFrame) return;
                var row = RowOf(item);
                if (row == null) return;
                bool changed = row.Key != _selectedKey;
                _selectedKey = row.Key;
                if (_suppress) return;
                if (changed) SelectionChanged?.Invoke(row);
                CellPicked?.Invoke(row, column >= 0 && column < _shownColumns.Count ? _shownColumns[column].Id : null);
            });
            Tree.NothingSelected += () =>
            {
                if (_selectedKey == null || _suppress) return;
                _selectedKey = null;
                SelectionChanged?.Invoke(null);
            };
            Tree.GuiInput += OnTreeInput;
            Tree.Resized += () => Ui.Defer(this, Reflow);
            Tree.ThemeChanged += () => Ui.Defer(this, () =>
            {
                _pitch = 0; // the text size changed: measure again
                Reflow();
            });
            Scroll = new VScrollBar { SizeFlagsVertical = SizeFlags.ExpandFill, Step = 1, Visible = false };
            Scroll.AccessibilityName = tr("Scroll the rows");
            Scroll.ValueChanged += value =>
            {
                if (!_settingScroll) ScrollTo((int)Math.Round(value));
            };
            var body = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            body.AddThemeConstantOverride("separation", 0);
            body.AddChild(Tree);
            body.AddChild(Scroll);
            AddChild(body);

            _searchDebounce = new Timer { OneShot = true, WaitTime = 0.2 };
            _searchDebounce.Timeout += Render;
            AddChild(_searchDebounce);
        }

        // =====================================================================
        // Columns and the remembered preferences
        // =====================================================================

        private TablePrefs Prefs
        {
            get
            {
                if (!_settings.Tables.TryGetValue(_id, out var prefs))
                {
                    prefs = new TablePrefs();
                    _settings.Tables[_id] = prefs;
                }
                return prefs;
            }
        }

        public string SortColumn => _columns.Any(c => c.Id == Prefs.SortColumn && c.Sortable) ? Prefs.SortColumn : _defaultSort;

        public bool SortDescending => SortColumn == Prefs.SortColumn ? Prefs.SortDescending : _columns.FirstOrDefault(c => c.Id == _defaultSort)?.DescendingFirst ?? false;

        /// <summary>The columns, in order; <paramref name="defaultSort"/> is the column sorted by until the user picks one ("" keeps the host's order).</summary>
        public void SetColumns(IReadOnlyList<Column> columns, string defaultSort = "")
        {
            _columns = columns;
            _defaultSort = defaultSort;
            _shownColumns = columns.Where(c => !Prefs.HiddenColumns.Contains(c.Id)).ToList();
            if (_shownColumns.Count == 0) _shownColumns = columns.ToList();
            Tree.Clear();
            Tree.Columns = Math.Max(1, _shownColumns.Count);
            ApplyColumnLayout();
            Render();
        }

        public IReadOnlyList<Column> Columns => _columns;

        /// <summary>The columns shown, in order.</summary>
        public IReadOnlyList<Column> ShownColumns => _shownColumns;

        private void ApplyColumnLayout()
        {
            float scale = Math.Max(0.8f, Tree.GetThemeFontSize("font_size") / 14f);
            for (int k = 0; k < _shownColumns.Count; k++)
            {
                var c = _shownColumns[k];
                Tree.SetColumnExpand(k, c.Ratio > 0);
                Tree.SetColumnExpandRatio(k, Math.Max(1, c.Ratio));
                Tree.SetColumnCustomMinimumWidth(k, (int)(c.MinWidth * scale));
                Tree.SetColumnClipContent(k, true);
                Tree.SetColumnTitleAlignment(k, c.Align);
                string arrow = c.Id == SortColumn && c.Sortable ? (SortDescending ? " ▼" : " ▲") : "";
                Tree.SetColumnTitle(k, _tr(c.Title) + arrow);
            }
        }

        private void OnTitleClicked(long column, long button)
        {
            if (column < 0 || column >= _shownColumns.Count) return;
            if (button == (long)MouseButton.Right)
            {
                ShowColumnsMenu();
                return;
            }
            if (button != (long)MouseButton.Left) return;
            SortBy(_shownColumns[(int)column].Id);
        }

        /// <summary>Sorts by a column as a click on its title does: again on the same column turns the order.</summary>
        public void SortBy(string columnId)
        {
            var column = _columns.FirstOrDefault(c => c.Id == columnId);
            if (column == null || !column.Sortable) return;
            var prefs = Prefs;
            if (SortColumn == columnId)
            {
                prefs.SortColumn = columnId;
                prefs.SortDescending = !SortDescending;
            }
            else
            {
                prefs.SortColumn = columnId;
                prefs.SortDescending = column.DescendingFirst;
            }
            DataManager.SaveSettingsSoon(_settings);
            ApplyColumnLayout();
            Render();
            SortChanged?.Invoke();
        }

        private void FillColumnsMenu(PopupMenu popup)
        {
            popup.Clear();
            for (int i = 0; i < _columns.Count; i++)
            {
                popup.AddCheckItem(_tr(_columns[i].Title), i);
                popup.SetItemChecked(popup.GetItemIndex(i), _shownColumns.Contains(_columns[i]));
            }
        }

        private void ShowColumnsMenu()
        {
            var popup = new PopupMenu();
            FillColumnsMenu(popup);
            popup.IdPressed += id => ToggleColumn((int)id);
            popup.PopupHide += () => popup.QueueFree();
            AddChild(popup);
            popup.AddThemeFontSizeOverride("font_size", Tree.GetThemeFontSize("font_size"));
            popup.Position = (Vector2I)GetGlobalMousePosition();
            popup.Popup();
        }

        /// <summary>Hides a shown column or shows a hidden one; the last shown column stays.</summary>
        public void ToggleColumn(int index)
        {
            if (index < 0 || index >= _columns.Count) return;
            var column = _columns[index];
            var hidden = Prefs.HiddenColumns;
            if (hidden.Contains(column.Id)) hidden.Remove(column.Id);
            else if (_shownColumns.Count > 1) hidden.Add(column.Id);
            else return;
            DataManager.SaveSettingsSoon(_settings);
            SetColumns(_columns, _defaultSort);
        }

        // =====================================================================
        // Rows
        // =====================================================================

        /// <summary>The rows, in the host's order (the order ties keep); the table searches, sorts and draws them.</summary>
        public void SetRows(IReadOnlyList<Row> rows)
        {
            _rows = rows;
            _byKey.Clear();
            foreach (var row in rows) _byKey[row.Key] = row;
            Render();
        }

        /// <summary>The rows as shown: searched, sorted, at most <see cref="MaxRows"/>.</summary>
        public IReadOnlyList<Row> ShownRows => _shownRows;

        public IReadOnlyList<Row> Rows => _rows;

        public Row? Selected => _selectedKey != null && _byKey.TryGetValue(_selectedKey, out var row) ? row : null;

        /// <summary>Selects a row by its key (drawn or not; a hidden one is selected once shown), and scrolls to it if asked.</summary>
        public void Select(string? key, bool scrollTo = false)
        {
            _selectedKey = key;
            int index = IndexOfKey(key);
            if (index >= 0 && (scrollTo || index < _first || index >= _first + _visible)) BringIntoView(index);
            else ShowSelection();
        }

        /// <summary>The index in the shown rows of the first row on screen (for tests).</summary>
        public int FirstShownIndex => _first;

        /// <summary>How many rows fit on screen (for tests).</summary>
        public int VisibleRowCount => _visible;

        /// <summary>Scrolls so the shown row at <paramref name="first"/> is the top one (as far as the end allows).</summary>
        public void ScrollTo(int first)
        {
            _first = first;
            FillWindow();
        }

        /// <summary>Draws the rows again (after a change of search, sort or cells), keeping the selection and the scroll.</summary>
        public void Render()
        {
            if (_shownColumns.Count == 0) return;
            using var __perf = PerfMonitor.Measure($"Table {_id}: draw");
            var words = WordSearch.Words(SearchBox.Text);
            IEnumerable<Row> rows = _rows;
            if (words.Length > 0) rows = rows.Where(r => WordSearch.Matches(words, r.Cells.Select(c => c.Text).Append(r.SearchText ?? "").ToArray()));
            int sortIndex = _columns.ToList().FindIndex(c => c.Id == SortColumn);
            var shown = sortIndex >= 0 && _columns[sortIndex].Sortable
                ? TableSort.Order(rows, r => KeyOf(r, sortIndex), SortDescending, r => r.Pinned)
                : (_rows.Any(r => r.Pinned) ? TableSort.Order(rows, _ => null, false, r => r.Pinned) : rows.ToList());
            int found = shown.Count;
            if (shown.Count > MaxRows) shown = shown.Take(MaxRows).ToList();
            _shownRows = shown;

            int total = TotalCount ?? _rows.Count;
            string count = found == total ? string.Format(_tr(total == 1 ? "{0} row" : "{0} rows"), total.ToString("N0"))
                : string.Format(_tr("{0} of {1} rows"), found.ToString("N0"), total.ToString("N0"));
            if (found > MaxRows) count += " " + string.Format(_tr("(showing the first {0})"), MaxRows.ToString("N0"));
            CountLabel.Text = count;

            ApplyColumnLayout();
            FillWindow();
        }

        // =====================================================================
        // The rows on screen
        // =====================================================================

        /// <summary>
        /// Fills the tree's items from the shown rows at the scroll position: as many items as fit, kept from one fill to
        /// the next (only their number changes), so an unchanged row keeps its item. Then the selection and the scroll bar.
        /// </summary>
        private void FillWindow()
        {
            int count = _shownRows.Count;
            _first = Math.Clamp(_first, 0, Math.Max(0, count - _visible));
            int shown = Math.Min(_visible, count - _first);
            _suppress = true;
            try
            {
                var root = Tree.GetRoot() ?? Tree.CreateItem();
                int have = root.GetChildCount();
                int wanted = Math.Max(shown, 1);
                while (have < wanted)
                {
                    Tree.CreateItem(root);
                    have++;
                }
                while (have > wanted)
                {
                    root.GetChild(have - 1).Free();
                    have--;
                }
                var item = root.GetFirstChild();
                for (int i = 0; i < shown && item != null; i++, item = item.GetNext()) Fill(item, _shownRows[_first + i], _first + i);
                if (shown == 0)
                {
                    // One item carries the empty text (and nothing else: it isn't a row).
                    var empty = root.GetFirstChild()!;
                    for (int k = 0; k < _shownColumns.Count; k++)
                    {
                        empty.SetText(k, "");
                        empty.SetIcon(k, null);
                        empty.ClearCustomBgColor(k);
                        empty.SetSelectable(k, false);
                        empty.SetTooltipText(k, "");
                    }
                    empty.SetText(0, _rows.Count == 0 ? EmptyText : NoMatchText);
                    empty.SetCustomColor(0, ThemeColors.TextSubtle);
                    empty.SetExpandRight(0, true);
                    empty.SetMetadata(0, default);
                }
            }
            finally
            {
                _suppress = false;
            }
            ShowSelection();
            UpdateScroll();
            if (_pitch <= 0) Ui.Defer(this, Reflow); // the first fill: measure the rows, then fit as many as the tree holds
            else Ui.Defer(this, TrimOverflow);
        }

        /// <summary>Selects the selected row's item if it's on screen (without that counting as a pick), or nothing.</summary>
        private void ShowSelection()
        {
            _suppress = true;
            try
            {
                var item = ItemOf(_selectedKey);
                _selectFrame = Godot.Engine.GetProcessFrames();
                if (item != null)
                {
                    if (Tree.GetSelected() != item) item.Select(0);
                }
                else Tree.DeselectAll();
            }
            finally
            {
                _suppress = false;
            }
        }

        private void UpdateScroll()
        {
            _settingScroll = true;
            try
            {
                int count = _shownRows.Count;
                Scroll.MaxValue = Math.Max(count, 1);
                Scroll.Page = Math.Min(_visible, Math.Max(count, 1));
                Scroll.Value = _first;
                Scroll.Visible = count > _visible;
            }
            finally
            {
                _settingScroll = false;
            }
        }

        /// <summary>Measures a row's pitch from the tree's items and fits as many as the tree's height holds.</summary>
        private void Reflow()
        {
            if (!IsInsideTree()) return;
            Measure();
            int visible = _pitch > 0 ? Math.Max(1, (int)Math.Floor((Tree.Size.Y - _top) / _pitch)) : _visible;
            if (visible != _visible)
            {
                _visible = visible;
                FillWindow();
            }
            else TrimOverflow();
        }

        private void Measure()
        {
            var first = Tree.GetRoot()?.GetFirstChild();
            if (first == null) return;
            var rect = Tree.GetItemAreaRect(first);
            var second = first.GetNext();
            float pitch = second != null ? Tree.GetItemAreaRect(second).Position.Y - rect.Position.Y : rect.Size.Y + Tree.GetThemeConstant("v_separation");
            if (pitch <= 0) return;
            _pitch = pitch;
            _top = rect.Position.Y;
        }

        /// <summary>A row taller than measured (an icon) can push the last item past the tree's bottom: one row fewer then.</summary>
        private void TrimOverflow()
        {
            if (!IsInsideTree() || _visible <= 1 || _shownRows.Count <= _visible) return;
            var root = Tree.GetRoot();
            if (root == null || root.GetChildCount() < _visible) return;
            var last = root.GetChild(root.GetChildCount() - 1);
            var rect = Tree.GetItemAreaRect(last);
            if (rect.Position.Y + rect.Size.Y <= Tree.Size.Y) return;
            _visible--;
            FillWindow();
        }

        private void BringIntoView(int index)
        {
            if (index < _first) _first = index;
            else if (index >= _first + _visible) _first = index - _visible + 1;
            FillWindow();
        }

        private int IndexOfKey(string? key) => key == null ? -1 : _shownRows.FindIndex(r => r.Key == key);

        /// <summary>The wheel scrolls the rows, and the keys move the selection through them; the tree is left its clicks.</summary>
        private void OnTreeInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton { Pressed: true } mouse && mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                if (_shownRows.Count <= _visible) return; // nothing to scroll: a container around the table may
                ScrollTo(_first + (mouse.ButtonIndex == MouseButton.WheelDown ? 3 : -3) * Math.Max(1, (int)mouse.Factor));
                Tree.AcceptEvent();
                return;
            }
            if (@event is not InputEventKey { Pressed: true } key || _shownRows.Count == 0) return;
            int count = _shownRows.Count;
            int current = IndexOfKey(_selectedKey);
            int target = key.Keycode switch
            {
                Key.Down => Math.Min(count - 1, current + 1),
                Key.Up => current < 0 ? 0 : Math.Max(0, current - 1),
                Key.Pagedown => Math.Min(count - 1, Math.Max(0, current) + _visible),
                Key.Pageup => Math.Max(0, current - _visible),
                Key.Home => 0,
                Key.End => count - 1,
                _ => -1
            };
            if (target < 0) return;
            Tree.AcceptEvent();
            if (target == current) return;
            var row = _shownRows[target];
            _selectedKey = row.Key;
            BringIntoView(target);
            SelectionChanged?.Invoke(row);
            CellPicked?.Invoke(row, null);
        }

        private static IComparable? KeyOf(Row row, int column)
        {
            if (column >= row.Cells.Length) return null;
            var cell = row.Cells[column];
            return cell.SortKey ?? (cell.Text.Length == 0 ? null : cell.Text);
        }

        private void Fill(TreeItem item, Row row, int index)
        {
            for (int k = 0; k < _shownColumns.Count; k++)
            {
                int i = IndexOf(_shownColumns[k]);
                var cell = i < row.Cells.Length ? row.Cells[i] : null;
                item.SetText(k, cell?.Text ?? "");
                item.SetCustomColor(k, cell?.Color ?? (row.Dim ? ThemeColors.TextSubtle : ThemeColors.Text));
                item.SetTooltipText(k, cell?.Tooltip ?? "");
                item.SetTextAlignment(k, _shownColumns[k].Align);
                item.SetIcon(k, cell?.Icon);
                if (cell?.Icon != null) item.SetIconMaxWidth(k, Math.Max(16, Tree.GetThemeFontSize("font_size") * 2));
                var background = cell?.Background ?? (Stripes ? (index % 2 == 0 ? ThemeColors.RowEven : ThemeColors.RowOdd) : (Color?)null);
                if (background is { } bg) item.SetCustomBgColor(k, bg);
                else item.ClearCustomBgColor(k);
                item.SetSelectable(k, true);
                item.SetEditable(k, false);
                item.SetCellMode(k, TreeItem.TreeCellMode.String);
                item.SetExpandRight(k, false);
            }
            item.SetMetadata(0, row.Key);
            Customize?.Invoke(item, row);
        }

        private int IndexOf(Column column)
        {
            for (int i = 0; i < _columns.Count; i++)
                if (ReferenceEquals(_columns[i], column)) return i;
            return -1;
        }

        /// <summary>The shown column index of a column id, or -1 when hidden.</summary>
        public int ShownIndexOf(string columnId) => _shownColumns.FindIndex(c => c.Id == columnId);

        private Row? RowOf(TreeItem? item)
        {
            if (item == null) return null;
            var meta = item.GetMetadata(0);
            return meta.VariantType == Variant.Type.String && _byKey.TryGetValue(meta.AsString(), out var row) ? row : null;
        }

        /// <summary>The row an item of the tree stands for (for a host's own handlers on the tree).</summary>
        public Row? RowAt(TreeItem? item) => RowOf(item);

        private TreeItem? ItemOf(string? key)
        {
            if (key == null) return null;
            for (var item = Tree.GetRoot()?.GetFirstChild(); item != null; item = item.GetNext())
            {
                var meta = item.GetMetadata(0);
                if (meta.VariantType == Variant.Type.String && meta.AsString() == key) return item;
            }
            return null;
        }

        // =====================================================================
        // The row menu
        // =====================================================================

        private void ShowRowMenu()
        {
            var row = RowOf(Tree.GetSelected());
            if (row == null) return;
            var menu = new PopupMenu();
            var actions = new Dictionary<long, Action>();
            void Add(string label, Action action, bool enabled = true)
            {
                long id = actions.Count + 1;
                menu.AddItem(label, (int)id);
                menu.SetItemDisabled(menu.GetItemIndex((int)id), !enabled);
                actions[id] = action;
            }
            int own = 0;
            foreach (var (label, action, enabled) in RowMenu?.Invoke(row) ?? Array.Empty<(string, Action, bool)>())
            {
                Add(label, action, enabled);
                own++;
            }
            if (own > 0) menu.AddSeparator();
            Add(_tr("Copy row"), () => CopyRows(new[] { row }, ExportFormat.Tsv));
            menu.IdPressed += id =>
            {
                if (actions.TryGetValue(id, out var action)) action();
            };
            menu.PopupHide += () => menu.QueueFree();
            AddChild(menu);
            menu.AddThemeFontSizeOverride("font_size", Tree.GetThemeFontSize("font_size"));
            menu.Position = (Vector2I)GetGlobalMousePosition();
            menu.Popup();
        }

        // =====================================================================
        // Export
        // =====================================================================

        private IReadOnlyList<string> Headers => _shownColumns.Select(c => _tr(c.Title)).ToList();

        private IReadOnlyList<string> CellsOf(Row row) => _shownColumns.Select(c => IndexOf(c) is var i && i < row.Cells.Length ? row.Cells[i].Text : "").ToList();

        /// <summary>The shown rows as text in a format (Discord's parts joined by a blank line).</summary>
        public string ExportText(ExportFormat format) => TableExport.Text(format, Headers, _shownRows.Select(CellsOf));

        /// <summary>The shown rows as Discord messages, each short enough to send.</summary>
        public List<string> DiscordParts() => TableExport.Discord(Headers, _shownRows.Select(CellsOf));

        /// <summary>Copies the shown rows to the clipboard; Discord's parts, when there are several, through a dialog with a button each.</summary>
        public void CopyShown(ExportFormat format) => CopyRows(_shownRows, format);

        private void CopyRows(IReadOnlyList<Row> rows, ExportFormat format)
        {
            if (format == ExportFormat.Discord)
            {
                var parts = TableExport.Discord(Headers, rows.Select(CellsOf));
                if (parts.Count > 1)
                {
                    ShowPartsDialog(parts);
                    return;
                }
                DisplayServer.ClipboardSet(parts[0]);
            }
            else DisplayServer.ClipboardSet(TableExport.Text(format, Headers, rows.Select(CellsOf)));
            Toast?.Invoke(string.Format(_tr(rows.Count == 1 ? "Copied {0} row as {1}" : "Copied {0} rows as {1}"), rows.Count, FormatName(format)), ThemeColors.TextSubtle);
        }

        private string FormatName(ExportFormat format) => format switch
        {
            ExportFormat.Tsv => "TSV",
            ExportFormat.Csv => "CSV",
            ExportFormat.Markdown => "Markdown",
            _ => _tr("Discord messages")
        };

        private void ShowPartsDialog(List<string> parts)
        {
            var dialog = new AcceptDialog { Title = _tr("Copy for Discord"), OkButtonText = _tr("Close") };
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 6);
            box.AddChild(Kit.Text(string.Format(_tr("The rows make {0} Discord messages (each at most {1} characters). Copy and paste them one at a time."), parts.Count, TableExport.DiscordMessageLimit)));
            var buttons = new HFlowContainer();
            buttons.AddThemeConstantOverride("h_separation", 6);
            for (int i = 0; i < parts.Count; i++)
            {
                string part = parts[i];
                int number = i + 1;
                buttons.AddChild(Kit.Button(string.Format(_tr("Copy part {0} of {1}"), number, parts.Count), null, () =>
                {
                    DisplayServer.ClipboardSet(part);
                    Toast?.Invoke(string.Format(_tr("Copied part {0} of {1}"), number, parts.Count), ThemeColors.TextSubtle);
                }));
            }
            box.AddChild(buttons);
            dialog.AddChild(box);
            dialog.Confirmed += dialog.QueueFree;
            dialog.Canceled += dialog.QueueFree;
            AddChild(dialog);
            MainTrackerWindow.SetFontSizeRecursive(dialog, Tree.GetThemeFontSize("font_size"));
            dialog.PopupCentered(new Vector2I(520, 0));
        }

        /// <summary>Asks where to save the shown rows as a CSV file (Atlas's own folder first; the user picks any other).</summary>
        public void AskSaveCsv()
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.SaveFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Title = _tr("Save as CSV"),
                CurrentDir = DataManager.GetDataDirectory(),
                CurrentFile = _id + ".csv",
                UseNativeDialog = false
            };
            dialog.AddFilter("*.csv", "CSV");
            dialog.FileSelected += path =>
            {
                string? problem = SaveCsv(path);
                if (problem != null) Toast?.Invoke(problem, ThemeColors.Error);
                else Toast?.Invoke(string.Format(_tr("Saved {0}"), Path.GetFileName(path)), ThemeColors.Success);
            };
            dialog.Canceled += dialog.QueueFree;
            dialog.Confirmed += dialog.QueueFree;
            AddChild(dialog);
            dialog.PopupCentered(new Vector2I(760, 520));
        }

        /// <summary>Writes the shown rows as a CSV file (UTF-8 with a byte order mark, as spreadsheets expect); the problem if it couldn't.</summary>
        public string? SaveCsv(string path)
        {
            try
            {
                // An export the user chose where to put: a whole-file write is right here (nothing of Atlas's own is at stake).
                File.WriteAllText(path, ExportText(ExportFormat.Csv), new UTF8Encoding(true));
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                AP_Atlas.Core.Logger.LogWarning($"Couldn't save a CSV export: {ex.Message}");
                return string.Format(_tr("Couldn't save the file: {0}"), ex.Message);
            }
        }

        /// <summary>Takes one of the toolbar's pieces out, for a host that lays its toolbar out itself.</summary>
        public T Take<T>(T piece) where T : Control
        {
            piece.GetParent()?.RemoveChild(piece);
            return piece;
        }
    }
}
