#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core.PopTracker;
using AP_Atlas.Core.Reports;
using Godot;
using Color = Godot.Color;

namespace AP_Atlas.UI
{
    public partial class PackDoctorWindow
    {
        // Review state survives redraws (each applied fix triggers a re-check and a redraw).
        private float _recThreshold = 0.85f;
        private readonly HashSet<string> _recUnticked = new HashSet<string>();
        private readonly HashSet<string> _recTicked = new HashSet<string>();
        private readonly Dictionary<string, int> _recChoice = new Dictionary<string, int>();
        // The filters: typed words, pins or tiles, one map; and whether rows for checks not in this seed are shown.
        private string _recText = "";
        private int _recKind;
        private string _recMap;
        private bool _recShowAll;
        private LineEdit _recTextBox;

        /// <summary>Findings the Doctor can fix with one of its suggestions (unlinked tiles and pin sections).</summary>
        private List<Finding> Recommendations() => PackDoctor.Recommendations(_report);

        // What Apply last did, until the report after it says whether every applied row is gone.
        private readonly HashSet<string> _appliedKeys = new HashSet<string>();
        private string _appliedText = "";

        /// <summary>The Recommended tab's rows as the report has them: key, the best match's score, whether the row clashes (for tests).</summary>
        public IReadOnlyList<(string Key, double Score, bool Conflict)> RecommendedRows()
        {
            var recs = Recommendations();
            var conflicts = PackDoctor.Conflicts(_report, recs, ChosenSuggestion);
            return recs.Select(f => (f.Key, ChosenSuggestion(f).Score, conflicts.ContainsKey(f.Key))).ToList();
        }

        /// <summary>The rows the filters leave, in the order shown (for tests).</summary>
        public IReadOnlyList<string> RecommendedShownKeys() => Shown(Recommendations()).Select(f => f.Key).ToList();

        /// <summary>The group headings as shown (for tests).</summary>
        public IReadOnlyList<string> RecommendedGroupTitles() =>
            Shown(Recommendations()).GroupBy(f => FindingCatalog.KindOf(f.Key)).Select(g => GroupTitle(g.Key, g.Count())).ToList();

        /// <summary>The "what to do" line of a row (for tests).</summary>
        public string RecommendedToDo(string key) => Recommendations().FirstOrDefault(f => f.Key == key) is { } f ? ToDoLine(f) : null;

        /// <summary>Sets the filters as the bar does (for tests): typed words, 0 all / 1 pins / 2 tiles, a map's name or null.</summary>
        public void SetRecommendedFilter(string text, int kind, string map)
        {
            _recText = text ?? "";
            _recKind = kind;
            _recMap = map;
            RenderCurrentTab();
        }

        /// <summary>Selects every shown row at or above the confidence level, as the bar's button does (for tests).</summary>
        public void SelectShownRecommendations() => SelectShown(Recommendations());

        /// <summary>Ignores every selected row in one undoable step, as the bar's button does (for tests).</summary>
        public void IgnoreSelectedRecommendations() => IgnoreRecommendations(Recommendations().Where(IsTicked).ToList());

        /// <summary>Applies one recommendation's chosen match, as its Apply button does (for tests).</summary>
        public void ApplyRecommendation(string key)
        {
            var f = Recommendations().FirstOrDefault(r => r.Key == key);
            if (f != null) ApplyRecommendations(new List<Finding> { f });
        }

        /// <summary>The status line (for tests).</summary>
        public string StatusText => _status?.Text ?? "";

        /// <summary>Whether a report has arrived (for tests).</summary>
        public bool HasReport => _report != null;

        private Suggestion ChosenSuggestion(Finding f) =>
            f.Suggestions[Math.Clamp(_recChoice.TryGetValue(f.Key, out int i) ? i : 0, 0, f.Suggestions.Count - 1)];

        private bool IsTicked(Finding f)
        {
            if (_recTicked.Contains(f.Key)) return true;
            if (_recUnticked.Contains(f.Key)) return false;
            // A row whose match another (stronger) row or an existing pin already claims isn't pre-selected.
            if (_recConflicts.ContainsKey(f.Key)) return false;
            return ChosenSuggestion(f).Score >= _recThreshold - 0.0001f;
        }

        // Finding key → why its chosen match conflicts (recomputed each render).
        private Dictionary<string, string> _recConflicts = new Dictionary<string, string>();

        /// <summary>
        /// Rows that would give the same location (or item) to two things: the strongest row keeps it, the rest are
        /// flagged. A location some pin already shows is flagged too.
        /// </summary>
        private void ComputeConflicts(List<Finding> recs) => _recConflicts = PackDoctor.Conflicts(_report, recs, ChosenSuggestion);

        private static bool IsTileRow(Finding f) => f.Actions.HasFlag(FindingActions.LinkItem);

        private static string SubjectOf(Finding f) => f.Subject.Substring(f.Subject.IndexOf(':') + 1);

        private static string PinPathOf(Finding f) => IsTileRow(f) ? "" : SubjectOf(f).Split('|')[0];

        /// <summary>The maps a row's pin sits on (none for a tile).</summary>
        private IEnumerable<string> MapsOf(Finding f)
        {
            if (IsTileRow(f) || _report?.Pack == null) yield break;
            string path = PinPathOf(f);
            var pin = _report.Pack.Locations.FirstOrDefault(l => l.FullPath == path || l.FullPath.TrimEnd('/') == path);
            if (pin?.MapLocations == null) yield break;
            foreach (var place in pin.MapLocations) if (!string.IsNullOrEmpty(place.Map)) yield return place.Map;
        }

        /// <summary>Whether a row is about a check of this seed (a pin whose suggestions include one of the seed's locations; every tile).</summary>
        private bool Matters(Finding f) => IsTileRow(f) || FindingCatalog.MattersToSeed(f.Suggestions.Select(s => s.Id), _report?.SeedLocationIds);

        /// <summary>The rows the filters leave, what matters first (the rest only when "Show all" is on), each group by score.</summary>
        private List<Finding> Shown(List<Finding> recs)
        {
            var words = _recText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Passes(Finding f)
            {
                if (_recKind == 1 && IsTileRow(f)) return false;
                if (_recKind == 2 && !IsTileRow(f)) return false;
                if (_recMap != null && !MapsOf(f).Contains(_recMap, StringComparer.OrdinalIgnoreCase)) return false;
                if (words.Length > 0)
                {
                    string hay = PackDoctor.RowName(_report, f) + " " + PinPathOf(f) + " " + SubjectOf(f);
                    if (!words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase))) return false;
                }
                return _recShowAll || Matters(f);
            }
            return recs.Where(Passes)
                .OrderBy(f => FindingCatalog.KindOf(f.Key), StringComparer.Ordinal)
                .ThenBy(f => Matters(f) ? 0 : 1)
                .ThenByDescending(f => ChosenSuggestion(f).Score)
                .ToList();
        }

        private void SelectShown(List<Finding> recs)
        {
            foreach (var f in Shown(recs))
            {
                bool on = ChosenSuggestion(f).Score >= _recThreshold - 0.0001f && !_recConflicts.ContainsKey(f.Key);
                if (on) { _recTicked.Add(f.Key); _recUnticked.Remove(f.Key); }
                else { _recUnticked.Add(f.Key); _recTicked.Remove(f.Key); }
            }
            RenderCurrentTab();
        }

        private static string GroupTitle(string kind, int count) => FindingCatalog.Explain(kind).Summary.Replace("{n}", count.ToString());

        private string ToDoLine(Finding f)
        {
            string line = FindingCatalog.ToDo(FindingCatalog.KindOf(f.Key)) ?? "Pick the match it stands for, or Ignore it.";
            return _recConflicts.TryGetValue(f.Key, out var why) ? line + " ⚠ " + why : line;
        }

        /// <summary>
        /// Every suggested fix, grouped by kind with the group's explanation, what matters for this seed first, each row with
        /// what to do; filters by words, kind and map; select the shown rows, then apply or ignore them in one undoable step.
        /// </summary>
        private Control BuildRecommendedTab()
        {
            var root = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            root.AddThemeConstantOverride("separation", 8);
            var recs = Recommendations();
            ComputeConflicts(recs);
            var shown = Shown(recs);
            int hidden = recs.Count(f => !Matters(f)) - (_recShowAll ? recs.Count(f => !Matters(f)) : 0);

            root.AddChild(Kit.Subtle("Fixes the Doctor can make from its suggested matches, grouped by kind, with what matters for this seed first. Each row says what to do. " +
                               "Narrow the rows, select the ones you want, then apply or ignore them together: one step, Undo reverses it."));

            // ---- Filters: words, pins or tiles, a map ----
            var filters = new HFlowContainer();
            filters.AddThemeConstantOverride("h_separation", 10);
            filters.AddThemeConstantOverride("v_separation", 6);
            _recTextBox = new LineEdit { PlaceholderText = Tr("Rows matching…"), Text = _recText, CustomMinimumSize = new Vector2(220, 0), AccessibilityName = "Rows matching" };
            _recTextBox.TextSubmitted += text => { _recText = text; RenderCurrentTab(); };
            filters.AddChild(_recTextBox);
            var kindPick = new OptionButton { AccessibilityName = "Pins or tiles" };
            kindPick.AddItem("Pins and tiles");
            kindPick.AddItem("Pins only");
            kindPick.AddItem("Tiles only");
            kindPick.Selected = _recKind;
            kindPick.ItemSelected += i => { _recKind = (int)i; RenderCurrentTab(); };
            filters.AddChild(kindPick);
            var mapPick = new OptionButton { AccessibilityName = "Map" };
            mapPick.AddItem("Every map");
            var mapNames = _report?.Pack?.Maps.Values.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            foreach (string name in mapNames) mapPick.AddItem(name);
            mapPick.Selected = _recMap == null ? 0 : Math.Max(0, mapNames.IndexOf(_recMap) + 1);
            mapPick.ItemSelected += i => { _recMap = i <= 0 ? null : mapNames[(int)i - 1]; RenderCurrentTab(); };
            filters.AddChild(mapPick);
            if (hidden > 0 || _recShowAll)
                filters.AddChild(Kit.Button(_recShowAll ? "Hide rows for checks not in this seed" : $"Show {hidden} more for checks not in this seed",
                    "Pins whose matches are all checks this seed doesn't have (turned off by its options, or another version's)", () => { _recShowAll = !_recShowAll; RenderCurrentTab(); }, flat: true));
            root.AddChild(filters);

            // ---- Confidence level ----
            var bar = new HFlowContainer();
            bar.AddThemeConstantOverride("h_separation", 10);
            bar.AddThemeConstantOverride("v_separation", 6);
            var thresholdLabel = new Label { Text = $"Select matches at or above {_recThreshold:P0}", CustomMinimumSize = new Vector2(330, 0) };
            bar.AddChild(thresholdLabel);
            var slider = new HSlider { MinValue = 0.35, MaxValue = 1.0, Step = 0.05, Value = _recThreshold, CustomMinimumSize = new Vector2(260, 24), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            slider.ValueChanged += v => thresholdLabel.Text = $"Select matches at or above {v:P0}";
            // Re-select on release, so dragging doesn't redraw the list every step.
            slider.DragEnded += changed =>
            {
                _recThreshold = (float)slider.Value;
                _recTicked.Clear();
                _recUnticked.Clear();
                RenderCurrentTab();
            };
            bar.AddChild(slider);
            foreach (var (label, value) in new[] { ("95%", 0.95f), ("85%", 0.85f), ("70%", 0.70f), ("50%", 0.50f) })
            {
                bar.AddChild(Kit.Button(label, $"Select matches at or above {label}", () =>
                {
                    _recThreshold = value;
                    _recTicked.Clear();
                    _recUnticked.Clear();
                    RenderCurrentTab();
                }));
            }
            root.AddChild(bar);

            int selected = recs.Count(IsTicked);
            var actions = new HFlowContainer();
            actions.AddThemeConstantOverride("h_separation", 8);
            actions.AddThemeConstantOverride("v_separation", 6);
            var applyAll = Kit.Button($"Apply {selected} selected", "Apply every selected row in one undoable step", () => ApplyRecommendations(recs.Where(IsTicked).ToList()), selected > 0);
            applyAll.AddThemeColorOverride("font_color", selected > 0 ? Good : Muted);
            actions.AddChild(applyAll);
            var ignoreAll = Kit.Button($"Ignore {selected} selected", "Hide every selected row in one undoable step (they stay in the report to the author)", () => IgnoreRecommendations(recs.Where(IsTicked).ToList()), selected > 0);
            actions.AddChild(ignoreAll);
            actions.AddChild(Kit.Button("Select shown", "Select every shown row at or above the confidence level", () => SelectShown(recs), shown.Count > 0));
            actions.AddChild(Kit.Button("Select none", "Clear the selection", () => { foreach (var f in recs) { _recUnticked.Add(f.Key); _recTicked.Remove(f.Key); } RenderCurrentTab(); }, recs.Count > 0));
            // The counts agree with the selection: rows at the level that aren't selected are the ones that clash.
            int clashing = recs.Count(f => !IsTicked(f) && _recConflicts.ContainsKey(f.Key) && ChosenSuggestion(f).Score >= _recThreshold - 0.0001f);
            actions.AddChild(Kit.Subtle($"{recs.Count} suggestion{(recs.Count == 1 ? "" : "s")}, {shown.Count} shown · {selected} selected (at or above {_recThreshold:P0})" +
                (clashing > 0 ? $" · {clashing} more at that level clash with another row or a placed check (⚠)" : "") +
                $" · {recs.Count(f => f.Suggestions[0].Score < 0.6)} below 60% (check those by hand)"));
            root.AddChild(actions);

            if (recs.Count == 0)
            {
                root.AddChild(Kit.Text("No suggested fixes right now. Unlinked items or pins without a close match are listed on the Overview, Key Items and Locations tabs.", Good));
                return root;
            }
            if (shown.Count == 0)
            {
                root.AddChild(Kit.Text(hidden > 0 ? "Nothing to show for this seed with these filters; the rows left are for checks this seed doesn't have (Show more, above)." : "No row matches these filters.", Muted));
                return root;
            }

            // ---- Rows, grouped by kind ----
            var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 4);
            scroll.AddChild(list);
            root.AddChild(scroll);

            int stripe = 0;
            string currentKind = null;
            foreach (var f in shown)
            {
                string kind = FindingCatalog.KindOf(f.Key);
                if (kind != currentKind)
                {
                    currentKind = kind;
                    var entry = FindingCatalog.Explain(kind);
                    int inGroup = shown.Count(x => FindingCatalog.KindOf(x.Key) == kind);
                    var header = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    header.AddChild(Kit.Heading(GroupTitle(kind, inGroup), 1.05f));
                    var meaning = Kit.Muted(entry.ForUser + " " + (FindingCatalog.ToDo(kind) ?? ""));
                    meaning.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                    header.AddChild(meaning);
                    header.SetMeta("rec_group", kind);
                    list.AddChild(header);
                }
                var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
                {
                    BgColor = stripe++ % 2 == 0 ? AP_Atlas.Core.ThemeColors.RowOdd : AP_Atlas.Core.ThemeColors.RowEven,
                    ContentMarginLeft = 6,
                    ContentMarginRight = 6,
                    ContentMarginTop = 4,
                    ContentMarginBottom = 4
                });
                panel.SetMeta("rec_row", f.Key);
                var rowBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                rowBox.AddThemeConstantOverride("separation", 2);
                panel.AddChild(rowBox);
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                rowBox.AddChild(row);

                var fc = f;
                var tick = new CheckBox { ButtonPressed = IsTicked(f), TooltipText = "Include in \"Apply selected\" and \"Ignore selected\"", AccessibilityName = "Include in the selection" };
                tick.Toggled += on =>
                {
                    if (on) { _recTicked.Add(fc.Key); _recUnticked.Remove(fc.Key); }
                    else { _recUnticked.Add(fc.Key); _recTicked.Remove(fc.Key); }
                    int n = recs.Count(IsTicked);
                    applyAll.Text = $"Apply {n} selected";
                    applyAll.Disabled = n == 0;
                    ignoreAll.Text = $"Ignore {n} selected";
                    ignoreAll.Disabled = n == 0;
                };
                row.AddChild(tick);

                bool isTile = IsTileRow(f);
                var kindLabel = new Label { Text = isTile ? "Tile" : "Pin", CustomMinimumSize = new Vector2(48, 0) };
                kindLabel.AddThemeColorOverride("font_color", isTile ? AP_Atlas.Core.ThemeColors.Progression : AP_Atlas.Core.ThemeColors.Location);
                row.AddChild(kindLabel);

                string subject = SubjectOf(f);
                string what = PackDoctor.RowName(_report, f);
                var whatLabel = new Label
                {
                    Text = what,
                    TooltipText = isTile ? $"Pack tile '{subject}'" : $"Pin {subject.Split('|')[0]}",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    SizeFlagsStretchRatio = 1f,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart
                };
                row.AddChild(whatLabel);
                row.AddChild(new Label { Text = "→" });

                // The match: the best suggestion by default, or another of the Doctor's suggestions.
                var choice = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.3f, FitToLongestItem = false, ClipText = true };
                for (int i = 0; i < f.Suggestions.Count; i++) choice.AddItem($"{f.Suggestions[i].Label}   ({f.Suggestions[i].Score:P0})", i);
                choice.Selected = _recChoice.TryGetValue(f.Key, out int chosen) ? Math.Clamp(chosen, 0, f.Suggestions.Count - 1) : 0;
                choice.TooltipText = ChosenSuggestion(f).Label;
                choice.ItemSelected += i => { _recChoice[fc.Key] = (int)i; RenderCurrentTab(); };
                row.AddChild(choice);

                var s = ChosenSuggestion(f);
                bool conflict = _recConflicts.TryGetValue(f.Key, out var why);
                var score = new Label { Text = conflict ? $"⚠ {s.Score:P0}" : $"{s.Score:P0}", CustomMinimumSize = new Vector2(70, 0), HorizontalAlignment = HorizontalAlignment.Right, TooltipText = why ?? "" };
                score.AddThemeColorOverride("font_color", conflict ? Warn : s.Score >= 0.85 ? Good : s.Score >= 0.6 ? Warn : Bad);
                row.AddChild(score);
                if (conflict) choice.TooltipText = $"{s.Label}\n⚠ {why}";

                row.AddChild(Kit.Button("Apply", "Apply this match now", () => ApplyRecommendations(new List<Finding> { fc })));
                row.AddChild(Kit.Button("Choose…", "Pick a different match from every name", () =>
                {
                    if (isTile) PickTileItem(subject, what, fc.Suggestions);
                    else
                    {
                        var p = subject.Split('|');
                        PickSectionLocation(p[0], p.Length > 1 ? p[1] : "", fc.Suggestions);
                    }
                }));
                if (!isTile) row.AddChild(Kit.Button("Map", "Show this pin on the map", () => { _focusPinPath = subject.Split('|')[0]; SelectTab("Maps"); }));
                row.AddChild(Kit.Button("Ignore", "Hide this suggestion (it stays in the report to the author)", () => ToggleIgnore(fc)));

                // What to do, in plain words (and why the match clashes, when it does).
                var toDo = Kit.Subtle(ToDoLine(f) + (Matters(f) ? "" : "  (not in this seed)"));
                toDo.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                toDo.SetMeta("rec_todo", f.Key);
                rowBox.AddChild(toDo);
                list.AddChild(panel);
            }
            return root;
        }

        /// <summary>Applies each finding's chosen suggestion, all as one undoable edit.</summary>
        private void ApplyRecommendations(List<Finding> findings)
        {
            if (findings.Count == 0) return;
            var picks = findings.Select(f => (Finding: f, Pick: ChosenSuggestion(f))).ToList();
            string description = picks.Count == 1 ? $"Apply suggestion: {picks[0].Pick.Label}" : $"Apply {picks.Count} suggested fixes";
            try
            {
                PackFixes.Edit(_key, description, file =>
                {
                    foreach (var (f, s) in picks)
                    {
                        string subject = f.Subject.Substring(f.Subject.IndexOf(':') + 1);
                        if (f.Actions.HasFlag(FindingActions.LinkItem))
                        {
                            var t = UpsertTile(file, subject);
                            t.ApItemId = s.Id;
                            t.ApItemName = s.Label;
                        }
                        else
                        {
                            var parts = subject.Split('|');
                            file.Links.RemoveAll(x => x.Subject == f.Subject);
                            file.Links.Add(new LocationLinkFix
                            {
                                Subject = f.Subject,
                                PinPath = parts[0],
                                SectionName = parts.Length > 1 ? parts[1] : "",
                                ApLocationId = s.Id,
                                ApLocationName = s.Label,
                                AuthorStamp = PackFixes.AuthorStamp(_original, f.Subject)
                            });
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                AP_Atlas.Core.Logger.LogWarning($"Pack Doctor couldn't apply {description}: {ex.Message}");
                SetStatus($"Couldn't apply: {ex.Message}");
                return;
            }
            foreach (var (f, _) in picks) { _recTicked.Remove(f.Key); _recUnticked.Remove(f.Key); _recChoice.Remove(f.Key); _appliedKeys.Add(f.Key); }
            _appliedText = picks.Count == 1 ? $"Applied: {picks[0].Pick.Label}. Undo reverses it." : $"Applied {picks.Count} fixes. Undo reverses all of them.";
            SetStatus(_appliedText + " Checking the pack again…");
        }

        /// <summary>Ignores every given row, all as one undoable edit (they stay in the report to the author).</summary>
        private void IgnoreRecommendations(List<Finding> findings)
        {
            if (findings.Count == 0) return;
            string description = findings.Count == 1 ? "Ignore: " + findings[0].Title : $"Ignore {findings.Count} suggestions";
            try
            {
                PackFixes.Edit(_key, description, file =>
                {
                    foreach (var f in findings) if (!file.Ignored.Contains(f.Key)) file.Ignored.Add(f.Key);
                });
            }
            catch (Exception ex)
            {
                AP_Atlas.Core.Logger.LogWarning($"Pack Doctor couldn't ignore {description}: {ex.Message}");
                SetStatus($"Couldn't ignore: {ex.Message}");
                return;
            }
            // Kept like an Apply, so the report after the re-check doesn't overwrite the line with a bare "Checked".
            foreach (var f in findings) { _recTicked.Remove(f.Key); _recUnticked.Remove(f.Key); _appliedKeys.Add(f.Key); }
            _appliedText = findings.Count == 1 ? "Ignored one suggestion. Undo reverses it." : $"Ignored {findings.Count} suggestions. Undo reverses all of them.";
            SetStatus(_appliedText + " Checking the pack again…");
        }
    }
}
