using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The Settings page: every setting in one place, in sections, found by typing words (the same matching as the
    /// command palette, <see cref="AP_Atlas.Core.WordSearch"/>). The window adds the rows (<see cref="AddToggle"/>,
    /// <see cref="AddChoice"/>, <see cref="AddNumber"/>, <see cref="AddAction"/>), each reading and writing its own
    /// setting; the rows read their values again whenever the page shows (<see cref="OnShown"/>), so a setting changed
    /// elsewhere (a menu, a key) shows as it is. The explorer lists the sections (<see cref="SectionList"/>), and a
    /// tool's gear button jumps to its section (<see cref="ShowSection"/>).
    /// </summary>
    public sealed partial class SettingsPage : VBoxContainer
    {
        private readonly Func<string, string> _tr;
        private readonly LineEdit _search;
        private readonly Label _nothing;
        private readonly ScrollContainer _scroll;
        private readonly VBoxContainer _body;
        private readonly List<Section> _sections = new();
        private readonly List<Row> _rows = new();
        private readonly Dictionary<string, (Button Reset, Label Conflict)> _keyRows = new();

        /// <summary>The sections, for the explorer: a button each, jumping to the section.</summary>
        public VBoxContainer SectionList { get; }

        private sealed class Section
        {
            public Section(string id, string title, VBoxContainer box, Label heading)
            {
                Id = id;
                Title = title;
                Box = box;
                Heading = heading;
            }

            public string Id { get; }
            public string Title { get; }
            public VBoxContainer Box { get; }
            public Label Heading { get; }
        }

        private sealed class Row
        {
            public Row(string id, Section section, string title, string description, Control node, Control control, Action refresh)
            {
                Id = id;
                Section = section;
                Title = title;
                Description = description;
                Node = node;
                Control = control;
                Refresh = refresh;
            }

            public string Id { get; }
            public Section Section { get; }
            public string Title { get; }
            public string Description { get; }
            /// <summary>The whole row (hidden when a search leaves it out).</summary>
            public Control Node { get; }
            /// <summary>The control that changes the setting.</summary>
            public Control Control { get; }
            /// <summary>Reads the setting again into the control, without telling anyone.</summary>
            public Action Refresh { get; }
        }

        public SettingsPage(Func<string, string> tr)
        {
            _tr = tr;
            Name = "Settings";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            AddThemeConstantOverride("separation", 8);

            // A steady caret: a blinking one made the visual check's pictures differ by the caret.
            _search = new LineEdit { PlaceholderText = _tr("Search settings"), ClearButtonEnabled = true, CaretBlink = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _search.TextChanged += _ => Filter();
            AddChild(_search);

            _nothing = new Label { Text = _tr("No setting matches what you typed."), Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
            _nothing.AddThemeColorOverride("font_color", Colors.Gray);
            AddChild(_nothing);

            _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(_scroll);
            _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _body.AddThemeConstantOverride("separation", 18);
            _scroll.AddChild(_body);

            SectionList = new VBoxContainer { Name = "SettingsSections", SizeFlagsHorizontal = SizeFlags.ExpandFill };

            // The headings wear the accent, so they follow it when the user picks another one on this very page.
            AddChild(new TreeSubscriptions().On(
                () => AP_Atlas.Core.ThemeColors.AccentChanged += RecolourHeadings,
                () => AP_Atlas.Core.ThemeColors.AccentChanged -= RecolourHeadings));
        }

        /// <summary>Adds a section, after the others, with a note under its heading if given; its rows follow.</summary>
        public void AddSection(string id, string title, string note = "")
        {
            if (_sections.Any(s => s.Id == id)) throw new InvalidOperationException($"Two settings sections are \"{id}\".");
            var box = new VBoxContainer { Name = "Section_" + id, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 6);
            var heading = new Label { Text = _tr(title) };
            heading.AddThemeColorOverride("font_color", HeadingColour);
            heading.SetMeta("font_size_ratio", 1.3f);
            box.AddChild(heading);
            if (note.Length > 0)
            {
                var text = new Label { Text = _tr(note), AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                text.AddThemeColorOverride("font_color", Colors.LightGray);
                text.SetMeta("font_size_ratio", 0.9f);
                box.AddChild(text);
            }
            _body.AddChild(box);

            var link = new Button { Text = _tr(title), Alignment = HorizontalAlignment.Left, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            string shown = id;
            link.Pressed += () => ShowSection(shown);
            SectionList.AddChild(link);
            _sections.Add(new Section(id, title, box, heading));
        }

        /// <summary>A setting that's on or off.</summary>
        public void AddToggle(string section, string id, string title, string description, Func<bool> get, Action<bool> set)
        {
            var toggle = new CheckButton { ButtonPressed = get() };
            toggle.Toggled += on => set(on);
            AddRow(section, id, title, description, toggle, toggle, () => toggle.SetPressedNoSignal(get()));
        }

        /// <summary>A setting that's one of a few named choices; <paramref name="get"/> gives the chosen one's place, or -1 for none of them.</summary>
        public void AddChoice(string section, string id, string title, string description, IReadOnlyList<string> options, Func<int> get, Action<int> set)
        {
            var choice = new OptionButton();
            foreach (string option in options) choice.AddItem(_tr(option));
            choice.Select(get());
            choice.ItemSelected += index => set((int)index);
            AddRow(section, id, title, description, choice, choice, () => choice.Select(get()));
        }

        /// <summary>A whole number between two bounds.</summary>
        public void AddNumber(string section, string id, string title, string description, int min, int max, Func<int> get, Action<int> set)
        {
            var number = new SpinBox { MinValue = min, MaxValue = max, Step = 1, Rounded = true, CustomMinimumSize = new Vector2(110, 0) };
            number.SetValueNoSignal(get());
            number.ValueChanged += value => set((int)Math.Round(value));
            AddRow(section, id, title, description, number, number, () => number.SetValueNoSignal(get()));
        }

        /// <summary>Something to open or do from here (a tool's own settings, a folder).</summary>
        public void AddAction(string section, string id, string title, string description, string buttonText, Action run)
        {
            var button = new Button { Text = _tr(buttonText) };
            button.Pressed += run;
            AddRow(section, id, title, description, button, button, () => { });
        }

        /// <summary>
        /// A command's key: press the button, then the key (<see cref="KeyCapture"/>). A reset is enabled while the key
        /// isn't the default; a note shows while another command has the same key. <paramref name="set"/> gets the key,
        /// "" for none, or null for the default again.
        /// </summary>
        public void AddKey(string section, string id, string title, string description, Func<string> get, Func<string> getDefault, Action<string?> set, Func<string> conflict)
        {
            var box = new HBoxContainer();
            box.AddThemeConstantOverride("separation", 6);
            var warning = new Label { Text = _tr("Conflict"), Visible = false, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            warning.AddThemeColorOverride("font_color", Colors.Orange);
            var capture = new KeyCapture(_tr) { Key = get() };
            var reset = new Button { Text = _tr("Reset"), TooltipText = _tr("Back to the key Atlas ships with.") };
            void Refresh()
            {
                capture.Key = get();
                reset.Disabled = get() == getDefault();
                string others = conflict();
                warning.Visible = others.Length > 0;
                warning.TooltipText = others.Length > 0 ? _tr("Also bound to: {0}").Replace("{0}", others) : "";
            }
            capture.KeyChosen += key =>
            {
                set(key);
                RefreshAll(); // another row's conflict may have changed
            };
            reset.Pressed += () =>
            {
                set(null);
                RefreshAll();
            };
            box.AddChild(warning);
            box.AddChild(capture);
            box.AddChild(reset);
            AddRow(section, id, title, description, box, capture, Refresh);
            _keyRows[id] = (reset, warning);
            Refresh();
        }

        /// <summary>
        /// A block that draws itself (a list of things with their own buttons), the section's full width, found by the
        /// <paramref name="keywords"/> as well as the section's name; <paramref name="refresh"/> runs whenever the page shows.
        /// </summary>
        public void AddBlock(string section, string id, string keywords, Control content, Action refresh) =>
            AddRow(section, id, "", keywords, content, content, refresh, below: true);

        private void RefreshAll()
        {
            foreach (var row in _rows) row.Refresh();
        }

        /// <param name="shown">What the row shows: on its right, or under its words with <paramref name="below"/>.</param>
        /// <param name="primary">The control that changes the setting (what <see cref="ControlOf"/> gives), within <paramref name="shown"/> or the same.</param>
        private void AddRow(string sectionId, string id, string title, string description, Control shown, Control primary, Action refresh, bool below = false)
        {
            var section = _sections.FirstOrDefault(s => s.Id == sectionId) ?? throw new InvalidOperationException($"No settings section is \"{sectionId}\".");
            if (_rows.Any(r => r.Id == id)) throw new InvalidOperationException($"Two settings are \"{id}\".");
            BoxContainer row = below ? new VBoxContainer { Name = "Setting_" + id, SizeFlagsHorizontal = SizeFlags.ExpandFill }
                : new HBoxContainer { Name = "Setting_" + id, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", below ? 6 : 16);
            var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            text.AddThemeConstantOverride("separation", 2);
            if (title.Length > 0) text.AddChild(new Label { Text = _tr(title) });
            if (description.Length > 0 && !below)
            {
                var hint = new Label { Text = _tr(description), AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                hint.AddThemeColorOverride("font_color", Colors.LightGray);
                hint.SetMeta("font_size_ratio", 0.9f);
                text.AddChild(hint);
            }
            if (text.GetChildCount() > 0) row.AddChild(text);
            shown.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            if (title.Length > 0)
            {
                if (primary.TooltipText.Length == 0) primary.TooltipText = _tr(title);
                primary.AccessibilityName = _tr(title);
            }
            row.AddChild(shown);
            section.Box.AddChild(row);
            _rows.Add(new Row(id, section, title, description, row, primary, refresh));
        }

        private static Color HeadingColour => AP_Atlas.Core.ThemeColors.Accent.Lightened(0.2f);

        private void RecolourHeadings()
        {
            foreach (var section in _sections) section.Heading.AddThemeColorOverride("font_color", HeadingColour);
        }

        /// <summary>Shows the rows the typed words find (title, description or section), and the sections that keep a row.</summary>
        private void Filter()
        {
            var typed = AP_Atlas.Core.WordSearch.Words(_search.Text);
            bool anyShown = false;
            foreach (var section in _sections)
            {
                bool any = false;
                foreach (var row in _rows.Where(r => r.Section == section))
                {
                    bool match = AP_Atlas.Core.WordSearch.Matches(typed, _tr(row.Title), _tr(row.Description), _tr(section.Title));
                    row.Node.Visible = match;
                    any |= match;
                }
                section.Box.Visible = any;
                anyShown |= any;
            }
            _nothing.Visible = !anyShown;
        }

        /// <summary>Types into the search box, as the user would.</summary>
        public void Search(string text)
        {
            _search.Text = text; // setting the text tells no one, unlike typing
            Filter();
        }

        public bool SearchHasFocus => _search.HasFocus();

        /// <summary>Whether a row shows (the search left it in).</summary>
        public bool IsShown(string rowId) => RowOf(rowId).Node.Visible && RowOf(rowId).Section.Box.Visible;

        /// <summary>Whether the page says nothing matches the search.</summary>
        public bool NothingMatches => _nothing.Visible;

        /// <summary>The control that changes a setting (for a test to use as the user would).</summary>
        public Control ControlOf(string rowId) => RowOf(rowId).Control;

        public IEnumerable<string> RowIds => _rows.Select(r => r.Id);

        /// <summary>A key row's reset button (for a test to press as the user would).</summary>
        public Button KeyResetOf(string rowId) => KeyRowOf(rowId).Reset;

        /// <summary>A key row's conflict note: shown while another command has the key, naming it in its tooltip.</summary>
        public Label KeyConflictOf(string rowId) => KeyRowOf(rowId).Conflict;

        private (Button Reset, Label Conflict) KeyRowOf(string rowId) =>
            _keyRows.TryGetValue(rowId, out var parts) ? parts : throw new InvalidOperationException($"No key setting is {rowId}.");

        /// <summary>How far down the page is scrolled, in pixels.</summary>
        public int ScrollPosition => _scroll.ScrollVertical;

        private Row RowOf(string rowId) => _rows.FirstOrDefault(r => r.Id == rowId) ?? throw new InvalidOperationException($"No setting is \"{rowId}\".");

        /// <summary>Reads every setting again (something may have changed one while the page was away) and takes the search box.</summary>
        public void OnShown()
        {
            foreach (var row in _rows) row.Refresh();
            _search.GrabFocus();
        }

        /// <summary>Clears the search and scrolls to a section's heading (the explorer, a tool's gear button).</summary>
        public void ShowSection(string id)
        {
            var section = _sections.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException($"No settings section is \"{id}\".");
            if (_search.Text.Length > 0) Search("");
            // The rows lay out again over a frame once they all show; then the heading goes to the top.
            Ui.NextFrame(this, () => _scroll.ScrollVertical = (int)section.Box.Position.Y);
        }
    }
}
