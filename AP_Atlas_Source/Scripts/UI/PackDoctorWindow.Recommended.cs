#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core.PopTracker;
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

        /// <summary>Findings the Doctor can fix with one of its suggestions (unlinked tiles and pin sections).</summary>
        private List<Finding> Recommendations() =>
            _report?.Findings
                .Where(f => !f.Ignored && f.Suggestions.Count > 0 && (f.Actions.HasFlag(FindingActions.LinkItem) || f.Actions.HasFlag(FindingActions.LinkLocation)))
                .OrderByDescending(f => f.Suggestions[0].Score)
                .ToList() ?? new List<Finding>();

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
        private void ComputeConflicts(List<Finding> recs)
        {
            _recConflicts = new Dictionary<string, string>();
            foreach (var group in recs.GroupBy(f => (f.Actions.HasFlag(FindingActions.LinkItem), ChosenSuggestion(f).Id)).Where(g => g.Count() > 1))
            {
                var ordered = group.OrderByDescending(f => ChosenSuggestion(f).Score).ToList();
                foreach (var f in ordered.Skip(1))
                    _recConflicts[f.Key] = $"Same match as \"{RowName(ordered[0])}\" (stronger); pick another for this row.";
            }
            foreach (var f in recs.Where(f => f.Actions.HasFlag(FindingActions.LinkLocation) && !_recConflicts.ContainsKey(f.Key)))
            {
                var id = ChosenSuggestion(f).Id;
                if (!_report.Index.ByLocation.TryGetValue(id, out var shownBy) || shownBy.Count == 0) continue;
                // The same check on another map (same name) is fine; a differently named check there is a clash.
                var parts = f.Subject.Substring(f.Subject.IndexOf(':') + 1).Split('|');
                string mine = PackDoctor.SameCheckKey(parts[0], parts.Length > 1 ? parts[1] : "");
                var other = shownBy.FirstOrDefault(m => PackDoctor.SameCheckKey(m.Pin.FullPath, m.Section?.Name) != mine);
                if (other != null) _recConflicts[f.Key] = $"Already shown by a different check: \"{other.Section?.Name ?? other.Pin.Name}\" on pin {other.Pin.FullPath}.";
            }
            // Two rows with the same match but the same check name (one check on two maps) aren't a conflict.
            foreach (var key in _recConflicts.Keys.ToList())
            {
                var f = recs.First(r => r.Key == key);
                if (!_recConflicts[key].StartsWith("Same match as")) continue;
                var rivals = recs.Where(r => r != f && r.Actions == f.Actions && ChosenSuggestion(r).Id == ChosenSuggestion(f).Id).ToList();
                if (rivals.All(r => RowName(r).Equals(RowName(f), StringComparison.OrdinalIgnoreCase))) _recConflicts.Remove(key);
            }
        }

        private string RowName(Finding f)
        {
            string subject = f.Subject.Substring(f.Subject.IndexOf(':') + 1);
            if (f.Actions.HasFlag(FindingActions.LinkItem)) return ItemNameOf(subject);
            var parts = subject.Split('|');
            return parts.Length > 1 && parts[1].Length > 0 ? parts[1] : parts[0].Split('/').Last();
        }

        /// <summary>
        /// Every suggested fix in one list: pick a confidence level to pre-select, adjust any row, then apply
        /// them one at a time or all selected at once (a single undoable step).
        /// </summary>
        private Control BuildRecommendedTab()
        {
            var root = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            root.AddThemeConstantOverride("separation", 8);
            var recs = Recommendations();
            ComputeConflicts(recs);

            root.AddChild(Kit.Subtle("Fixes the Doctor can make from its suggested matches. Rows at or above the confidence level are selected for you; " +
                               "change any row's match, untick what you don't want, then apply. Applying all selected is one step: Undo reverses it."));

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
            var actions = new HBoxContainer();
            actions.AddThemeConstantOverride("separation", 8);
            var applyAll = Kit.Button($"Apply {selected} selected", "Apply every selected row in one undoable step", () => ApplyRecommendations(recs.Where(IsTicked).ToList()), selected > 0);
            applyAll.AddThemeColorOverride("font_color", selected > 0 ? Good : Muted);
            actions.AddChild(applyAll);
            actions.AddChild(Kit.Button("Select all", "Select every row", () => { foreach (var f in recs) { _recTicked.Add(f.Key); _recUnticked.Remove(f.Key); } RenderCurrentTab(); }, recs.Count > 0));
            actions.AddChild(Kit.Button("Select none", "Clear the selection", () => { foreach (var f in recs) { _recUnticked.Add(f.Key); _recTicked.Remove(f.Key); } RenderCurrentTab(); }, recs.Count > 0));
            actions.AddChild(Kit.Subtle($"{recs.Count} suggestion{(recs.Count == 1 ? "" : "s")} · {recs.Count(f => f.Suggestions[0].Score >= 0.85)} at 85%+ · {recs.Count(f => f.Suggestions[0].Score < 0.6)} below 60% (check those by hand)"));
            root.AddChild(actions);

            if (recs.Count == 0)
            {
                root.AddChild(Kit.Text("No suggested fixes right now. Unlinked items or pins without a close match are listed on the Overview, Key Items and Locations tabs.", Good));
                return root;
            }

            // ---- Rows ----
            var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 4);
            scroll.AddChild(list);
            root.AddChild(scroll);

            int stripe = 0;
            foreach (var f in recs)
            {
                var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
                {
                    BgColor = stripe++ % 2 == 0 ? AP_Atlas.Core.ThemeColors.RowOdd : AP_Atlas.Core.ThemeColors.RowEven,
                    ContentMarginLeft = 6,
                    ContentMarginRight = 6,
                    ContentMarginTop = 4,
                    ContentMarginBottom = 4
                });
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                panel.AddChild(row);

                var fc = f;
                var tick = new CheckBox { ButtonPressed = IsTicked(f), TooltipText = "Include in \"Apply selected\"", AccessibilityName = "Include in Apply selected" };
                tick.Toggled += on =>
                {
                    if (on) { _recTicked.Add(fc.Key); _recUnticked.Remove(fc.Key); }
                    else { _recUnticked.Add(fc.Key); _recTicked.Remove(fc.Key); }
                    int n = recs.Count(IsTicked);
                    applyAll.Text = $"Apply {n} selected";
                    applyAll.Disabled = n == 0;
                };
                row.AddChild(tick);

                bool isTile = f.Actions.HasFlag(FindingActions.LinkItem);
                var kind = new Label { Text = isTile ? "Tile" : "Pin", CustomMinimumSize = new Vector2(48, 0) };
                kind.AddThemeColorOverride("font_color", isTile ? AP_Atlas.Core.ThemeColors.Progression : AP_Atlas.Core.ThemeColors.Location);
                row.AddChild(kind);

                string subject = f.Subject.Substring(f.Subject.IndexOf(':') + 1);
                string what = isTile
                    ? ItemNameOf(subject)
                    : (subject.Split('|') is var parts && parts.Length > 1 && parts[1].Length > 0 ? parts[1] : parts[0].Split('/').Last());
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
            foreach (var (f, _) in picks) { _recTicked.Remove(f.Key); _recUnticked.Remove(f.Key); _recChoice.Remove(f.Key); }
            SetStatus(picks.Count == 1 ? $"Applied: {picks[0].Pick.Label}. Undo reverses it." : $"Applied {picks.Count} fixes. Undo reverses all of them.");
        }
    }
}
