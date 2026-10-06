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
    /// when the same rows come in the same order the cells are updated in place.
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
        private List<string> _renderedKeys = new();
        private readonly Dictionary<string, Row> _byKey = new();
        private string? _selectedKey;
        private bool _suppress;
        private string _defaultSort = "";
        private readonly Timer _searchDebounce;

        public Tree Tree { get; }
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
            ColumnsMenu = new MenuButton { Text = tr("Columns") + " ▾", Flat = true, FocusMode = FocusModeEnum.None, TooltipText = tr("Show or hide columns (also a right-click on a column's title)") };
            ColumnsMenu.GetPopup().AboutToPopup += () => FillColumnsMenu(ColumnsMenu.GetPopup());
            ColumnsMenu.GetPopup().IdPressed += id => ToggleColumn((int)id);
            Toolbar.AddChild(ColumnsMenu);
            ExportMenu = new MenuButton { Text = tr("Export") + " ▾", Flat = true, FocusMode = FocusModeEnum.None, TooltipText = tr("Copy or save the rows shown, in their order") };
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
            AddChild(Tree);

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
            _renderedKeys.Clear();
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
            var item = ItemOf(key);
            _suppress = true;
            try
            {
                if (item != null)
                {
                    item.Select(0);
                    if (scrollTo) Tree.ScrollToItem(item, true);
                }
                else Tree.DeselectAll();
            }
            finally
            {
                _suppress = false;
            }
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
            var keys = shown.Select(r => r.Key).ToList();
            _suppress = true;
            try
            {
                if (keys.Count > 0 && keys.SequenceEqual(_renderedKeys) && Tree.GetRoot() != null)
                {
                    // The same rows in the same order: the cells change in place (the scroll and the selection stay).
                    var item = Tree.GetRoot().GetFirstChild();
                    int index = 0;
                    foreach (var row in shown)
                    {
                        if (item == null) break;
                        Fill(item, row, index++);
                        item = item.GetNext();
                    }
                    return;
                }
                var bar = ScrollBar();
                double scroll = bar?.Value ?? 0;
                Tree.Clear();
                var root = Tree.CreateItem();
                TreeItem? reselect = null;
                int at = 0;
                foreach (var row in shown)
                {
                    var item = Tree.CreateItem(root);
                    Fill(item, row, at++);
                    if (row.Key == _selectedKey) reselect = item;
                }
                if (shown.Count == 0)
                {
                    var empty = Tree.CreateItem(root);
                    empty.SetText(0, _rows.Count == 0 ? EmptyText : NoMatchText);
                    empty.SetCustomColor(0, ThemeColors.TextSubtle);
                    empty.SetExpandRight(0, true);
                    for (int k = 0; k < _shownColumns.Count; k++) empty.SetSelectable(k, false);
                }
                _renderedKeys = keys;
                if (reselect != null) reselect.Select(0);
                if (bar != null && scroll > 0) Ui.Defer(bar, () => bar.Value = scroll);
            }
            finally
            {
                _suppress = false;
            }
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

        private VScrollBar? ScrollBar() => Tree.GetChildren(true).OfType<VScrollBar>().FirstOrDefault();

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
