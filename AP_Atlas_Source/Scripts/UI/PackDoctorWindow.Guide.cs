#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// "Make this pack work", the Doctor's first tab: the pack's health in one line and the steps that fix what's left, in
    /// order, each with how many rows it clears and the one button that does it (every bulk step previews first): the right
    /// version, the confident matches, a rule taught by example, the rest one by one (with keys), the checks no pin shows,
    /// tiles and pictures, and the end (set the rest aside, or tell the author).
    /// </summary>
    public partial class PackDoctorWindow
    {
        /// <summary>The guide's tab.</summary>
        public const string GuideTab = "Make this pack work";

        /// <summary>Opens the Games page on a game (set by the main window): where another release of the apworld is looked at and installed.</summary>
        public static Action<string>? OpenGamesPage { get; set; }

        /// <summary>One step of the guide as shown (for tests): its id, title, how many rows it's about, and whether it's done.</summary>
        public sealed record GuideStep(string Id, string Title, int Count, bool Done);

        private readonly List<GuideStep> _guideSteps = new();

        /// <summary>The guide's steps as last shown (for tests).</summary>
        public IReadOnlyList<GuideStep> GuideSteps() => _guideSteps;

        // ---- What the steps count ----

        private int SectionsShare => _report == null || _report.SectionsTotal == 0 ? 100 : (int)Math.Round(100.0 * _report.SectionsLinked / _report.SectionsTotal);

        private bool FitIsLow => _report != null && _report.SectionsTotal >= 2 && _report.SectionsLinked < AP_Atlas.Core.Games.PackReleaseFit.LinkedEnough * _report.SectionsTotal;

        /// <summary>The game's locations no pin shows (only this seed's when a slot of the game is connected).</summary>
        private int UnplacedCount()
        {
            if (_report?.Index == null) return 0;
            var unplaced = _report.Index.UnplacedLocations();
            return _report.SeedLocationIds.Count > 0 ? unplaced.Count(_report.SeedLocationIds.Contains) : unplaced.Count();
        }

        private int TileProblems() => _report?.Findings.Count(f => !f.Ignored && f.Severity >= FindingSeverity.Warning && f.Key.StartsWith("tile:", StringComparison.Ordinal)
            && !f.Actions.HasFlag(FindingActions.LinkItem)) ?? 0;

        /// <summary>The pin sections the user linked by hand, newest first (fix by example's examples).</summary>
        private List<PatternExample> HandExamples()
        {
            var pins = _report?.Pack?.Locations.GroupBy(p => p.FullPath).ToDictionary(g => g.Key, g => g.First()) ?? new Dictionary<string, PopTrackerLocation>();
            return PackFixes.Get(_key).Links.Where(l => l.Source == null && l.ApLocationId != null && !string.IsNullOrEmpty(l.ApLocationName))
                .OrderByDescending(l => l.Made)
                .Select(l => new PatternExample(new PatternRow(l.Subject, l.PinPath, pins.TryGetValue(l.PinPath, out var pin) ? pin.Name ?? "" : l.PinPath.Split('/').Last(), l.SectionName ?? ""), l.ApLocationName))
                .ToList();
        }

        private PatternRow RowOf(Finding f)
        {
            string subject = f.Subject.Substring(f.Subject.IndexOf(':') + 1);
            var parts = subject.Split('|');
            var pin = _report?.Pack?.Locations.FirstOrDefault(l => l.FullPath == parts[0]);
            return new PatternRow(f.Key, parts[0], pin?.Name ?? parts[0].Split('/').Last(), parts.Length > 1 ? parts[1] : "");
        }

        /// <summary>The rules the user's hand-made links explain, with the rows each links (pin sections still to decide).</summary>
        public List<Pattern> Patterns()
        {
            if (_report?.Index == null) return new List<Pattern>();
            var examples = HandExamples();
            if (examples.Count < PackPatterns.MinExamples) return new List<Pattern>();
            // Rows linked since the report (the check after an edit may not be back yet) aren't rows to decide, and what they took is taken.
            var file = PackFixes.Get(_key);
            var linkedNow = new HashSet<string>(file.Links.Select(l => l.Subject), StringComparer.Ordinal);
            var rows = Recommendations().Where(f => f.Actions.HasFlag(FindingActions.LinkLocation) && !linkedNow.Contains(f.Subject)).Select(RowOf).ToList();
            var taken = new HashSet<long>(_report.Index.ByLocation.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key));
            taken.UnionWith(file.Links.Where(l => l.ApLocationId != null).Select(l => l.ApLocationId!.Value));
            return PackPatterns.FromExamples(examples, rows, LocationNames, taken);
        }

        /// <summary>Previews linking a rule's rows (for tests and the guide's button).</summary>
        public void PreviewPattern(int index)
        {
            var patterns = Patterns();
            if (index < 0 || index >= patterns.Count || _report == null) return;
            var pattern = patterns[index];
            var byKey = Recommendations().ToDictionary(f => f.Key, StringComparer.Ordinal);
            var links = pattern.Matches.Where(m => byKey.ContainsKey(m.Key))
                .Select(m => (byKey[m.Key], new Suggestion { Id = m.Id, Label = m.Name, Score = 1, Reason = "follows your examples" })).ToList();
            string reason = "Follows your examples: " + pattern.Description + ".";
            var plan = PackBulk.Plan(_report, PackFixes.Get(_key), links, source: "example", reason: reason);
            FixPreviewDialog.Show(this, $"Link {links.Count} rows by your examples", plan, _report, () => RunPlan(plan, $"Link {plan.Link.Count} rows by your examples",
                $"Linked {plan.Link.Count} row{(plan.Link.Count == 1 ? "" : "s")} by your examples. Undo reverses all of them."), SetStatus);
        }

        /// <summary>Previews linking the confident matches only (the guide's second step; nothing is set aside).</summary>
        public void PreviewConfident()
        {
            if (_report == null) return;
            var plan = PackBulk.Plan(_report, PackFixes.Get(_key), PackBulk.ConfidentLinks(_report), source: "suggested");
            FixPreviewDialog.Show(this, "Link the confident matches", plan, _report, () => RunPlan(plan, $"Link {plan.Link.Count} confident matches",
                $"Linked {plan.Link.Count} confident match{(plan.Link.Count == 1 ? "" : "es")}. Undo reverses all of them."), SetStatus);
        }

        // ---- The tab ----

        private Control BuildGuideTab()
        {
            if (_triageOn) return BuildTriage();
            _guideSteps.Clear();
            var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 10);
            scroll.AddChild(box);
            var report = _report!;
            var recs = Recommendations();
            string game = report.Names?.Game ?? _original.Manifest?.GameName ?? "the game";

            box.AddChild(Kit.Heading("Make this pack work"));
            box.AddChild(CoverageBar("Pin sections that show a check", report.SectionsLinked, report.SectionsTotal,
                $"{recs.Count} row{(recs.Count == 1 ? "" : "s")} to decide; tiles {report.TilesLinked} of {report.TilesTotal} linked"));
            box.AddChild(Kit.Subtle("The steps below fix what's left, in order. Each says how many rows it clears; the ones that change many rows show what they'll do first, and Undo (below) reverses any of them."));

            int number = 0;
            // 1. The right version (only when few sections link: the pack is probably for another version).
            if (FitIsLow)
            {
                var step = Step(box, ++number, "version", "Check the pack is for your version", 0, false,
                    $"Only {SectionsShare}% of its pin sections link to {game}'s names ({report.Names?.Source ?? "no names"}): the pack may be made for another version of the game, and no fix here makes up for that.");
                step.AddChild(VersionCompare(game));
            }
            // 2. The confident matches.
            var confident = PackBulk.ConfidentLinks(report);
            var confidentStep = Step(box, ++number, "confident", "Link the confident matches", confident.Count, confident.Count == 0,
                confident.Count > 0 ? $"{confident.Count} row{(confident.Count == 1 ? " matches" : "s match")} a check at 85% or more, with no clash." : "No confident match is left to link.");
            if (confident.Count > 0) confidentStep.AddChild(Kit.Button($"Link {confident.Count}…", "Shows what it links, then links them in one undoable step", PreviewConfident));
            // 3. Fix by example.
            var patterns = Patterns();
            int examples = HandExamples().Count;
            int pinRows = recs.Count(f => f.Actions.HasFlag(FindingActions.LinkLocation));
            string exampleText = patterns.Count > 0 ? "Your links follow a rule Atlas can use for other rows:"
                : pinRows == 0 ? "No pin section is left to decide."
                : examples < PackPatterns.MinExamples ? $"Link {PackPatterns.MinExamples - examples} more row{(PackPatterns.MinExamples - examples == 1 ? "" : "s")} by hand (one by one, below, or Choose… anywhere) and Atlas looks for the rule behind your links, then offers it for the rest."
                : "Your links don't share a rule that fits other rows yet; a few more might.";
            var exampleStep = Step(box, ++number, "example", "Teach Atlas by example", patterns.Sum(p => p.Matches.Count), pinRows == 0, exampleText);
            for (int i = 0; i < patterns.Count; i++)
            {
                int index = i;
                var p = patterns[i];
                var line = new HFlowContainer();
                line.AddThemeConstantOverride("h_separation", 8);
                var rule = Kit.Text($"{p.Description}: links {p.Matches.Count} more row{(p.Matches.Count == 1 ? "" : "s")}", ThemeColors.Text);
                rule.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                rule.CustomMinimumSize = new Vector2(360, 0);
                line.AddChild(rule);
                var preview = Kit.Button("Preview…", "Shows the rows the rule links, then links them in one undoable step", () => PreviewPattern(index));
                preview.SetMeta("pattern_preview", index);
                line.AddChild(preview);
                exampleStep.AddChild(line);
            }
            // 4. One by one.
            var oneStep = Step(box, ++number, "triage", "Decide the rest one by one", recs.Count, recs.Count == 0,
                recs.Count > 0 ? $"{recs.Count} row{(recs.Count == 1 ? "" : "s")} left, your seed's first, each with the pin on its map and Atlas's guesses. Keys: Enter takes the first guess, 1–6 another, C chooses by name, N says it isn't a check, S skips, Ctrl+Z undoes, Escape comes back here."
                    : "Every row is decided.");
            if (recs.Count > 0)
            {
                var start = Kit.Button("Start", "One row at a time, with keys", StartTriage);
                start.SetMeta("triage_start", true);
                oneStep.AddChild(start);
            }
            // 5. Checks no pin shows.
            int unplaced = UnplacedCount();
            var placeStep = Step(box, ++number, "unplaced", "Place the checks no pin shows", unplaced, unplaced == 0,
                unplaced > 0 ? $"{unplaced} of {game}'s locations{(report.SeedLocationIds.Count > 0 ? " in your seed" : "")} have no pin. Put each on a map, add it to a pin, or leave the ones your options turn off."
                    : "Every location has a pin.");
            if (unplaced > 0) placeStep.AddChild(Kit.Button("Place them", "Opens the list of locations with no pin", () => SelectTab("Locations")));
            // 6. Tiles and pictures.
            int tiles = TileProblems();
            var tileStep = Step(box, ++number, "tiles", "Tiles and pictures", tiles, tiles == 0,
                tiles > 0 ? $"{tiles} Key Items tile problem{(tiles == 1 ? "" : "s")}: a tile without a picture or an item, a cell the pack doesn't define." : "Every tile has its item and picture.");
            if (tiles > 0) tileStep.AddChild(Kit.Button("Open Key Items", "Opens the tiles to fix", () => SelectTab("Key Items")));
            // 7. The end.
            if (recs.Count > 0)
            {
                var endStep = Step(box, ++number, "finish", "Set the rest aside", recs.Count, false,
                    $"When you've done what you want: {recs.Count} row{(recs.Count == 1 ? "" : "s")} without a decision can be set aside (they stay in the report to the author, and Undo brings them back), so the Map Tracker's Notices stay quiet about them.");
                endStep.AddChild(Kit.Button($"Set {recs.Count} aside…", "Shows the rows, then sets them aside in one undoable step", () => PreviewIgnore(recs)));
            }
            else
            {
                var endStep = Step(box, ++number, "finish", "Done", 0, true, "This pack works for your seed as far as Atlas can tell. Tell its author what you fixed, so the next version has it.");
                endStep.AddChild(Kit.Button("Report to the author", "Opens the report for the pack's author", () => SelectTab("Report")));
            }
            if (!FitIsLow)
            {
                box.AddChild(new HSeparator());
                box.AddChild(VersionCompare(game));
            }
            return scroll;
        }

        /// <summary>A step's card: its number (or a tick when done), title, what it's about; the caller adds its buttons to the returned box.</summary>
        private VBoxContainer Step(VBoxContainer parent, int number, string id, string title, int count, bool done, string detail)
        {
            _guideSteps.Add(new GuideStep(id, title, count, done));
            var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            card.SetMeta("guide_step", id);
            card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = ThemeColors.SurfacePanel,
                BorderColor = done ? ThemeColors.Success : ThemeColors.Accent,
                BorderWidthLeft = 3,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                ContentMarginLeft = 12,
                ContentMarginRight = 10,
                ContentMarginTop = 8,
                ContentMarginBottom = 8
            });
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var mark = new Label { Text = done ? "✓" : number.ToString(System.Globalization.CultureInfo.InvariantCulture), CustomMinimumSize = new Vector2(22, 0), VerticalAlignment = VerticalAlignment.Top };
            mark.AddThemeColorOverride("font_color", done ? Good : ThemeColors.AccentText(ThemeColors.SurfacePanel));
            row.AddChild(mark);
            var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            column.AddThemeConstantOverride("separation", 4);
            var heading = Kit.Text(title, done ? ThemeColors.TextMuted : ThemeColors.Text);
            column.AddChild(heading);
            var text = Kit.Subtle(detail);
            text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            text.CustomMinimumSize = new Vector2(360, 0);
            column.AddChild(text);
            row.AddChild(column);
            card.AddChild(row);
            parent.AddChild(card);
            return column;
        }

        // ---- Which version the pack fits ----

        private List<(string Label, int Linked, int Total)>? _versionFits;
        private bool _comparing;

        /// <summary>How the pack fits each table of names Atlas has (for tests): label, sections linked, of all.</summary>
        public IReadOnlyList<(string Label, int Linked, int Total)>? VersionFits => _versionFits;

        private Control VersionCompare(string game)
        {
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 4);
            var buttons = new HFlowContainer();
            buttons.AddThemeConstantOverride("h_separation", 8);
            var compare = Kit.Button(_comparing ? "Comparing…" : "Compare versions", "Measures the pack against this seed's names, the installed apworld's, and the releases the Games page looked at", CompareVersions, !_comparing);
            compare.SetMeta("compare_versions", true);
            buttons.AddChild(compare);
            if (OpenGamesPage != null)
                buttons.AddChild(Kit.Button($"{game}'s releases on the Games page…", "Where Atlas looks at the game's releases and installs the one the pack fits", () => OpenGamesPage(game)));
            box.AddChild(buttons);
            if (_versionFits == null) return box;
            if (_versionFits.Count == 0)
            {
                box.AddChild(Kit.Subtle("Atlas has no names of another version to compare with. The Games page can look at the game's releases (\"Which version works with my map pack?\")."));
                return box;
            }
            foreach (var (label, linked, total) in _versionFits)
            {
                int share = total == 0 ? 0 : (int)Math.Round(100.0 * linked / total);
                bool fits = total > 0 && linked >= AP_Atlas.Core.Games.PackReleaseFit.LinkedEnough * total;
                var line = Kit.Text($"{(fits ? "✓" : "✗")}  {label}: {share}% of the pack's sections link ({linked} of {total})", fits ? Good : ThemeColors.TextMuted);
                line.SetMeta("version_fit", label);
                box.AddChild(line);
            }
            return box;
        }

        private void CompareVersions() => AP_Atlas.Core.Async.Fire(CompareVersionsAsync(), "comparing a map pack with the game's versions");

        /// <summary>Measures the pack against every table of names Atlas has for the game (for tests: awaitable).</summary>
        public async Task CompareVersionsAsync()
        {
            if (_comparing || _report == null) return;
            _comparing = true;
            RenderCurrentTab();
            var fits = new List<(string, int, int)>();
            try
            {
                string game = _report.Names?.Game ?? GameNames.ResolveGame(_original.Manifest) ?? _original.Manifest?.GameName ?? "";
                foreach (var (label, table) in new[] { ("This seed's names", GameNames.Server(game)), ("The installed apworld's names", GameNames.Local(game)) })
                {
                    if (table == null) continue;
                    var measured = await PackDoctorService.MeasureAsync(_original, table);
                    if (measured == null) continue;
                    string version = string.IsNullOrEmpty(table.Version) || table.Version == "0.0.0" ? "" : $" ({table.Version})";
                    fits.Add((label + version, measured.SectionsLinked, measured.SectionsTotal));
                }
                string fitKey = AP_Atlas.Core.EngineSetup.ReleaseLook.FitKey(_original);
                foreach (var record in AP_Atlas.Core.Games.ReleaseFactsStore.Load(DataManager.GetDataDirectory(), game).Records)
                    if (record.FitOf(fitKey) is { } fit)
                        fits.Add(($"Release {record.Version} ({record.Project})", fit.SectionsLinked, fit.SectionsTotal));
            }
            catch (Exception ex)
            {
                AP_Atlas.Core.Logger.LogWarning($"Pack Doctor couldn't compare the pack with the game's versions: {ex.Message}");
                SetStatus("Couldn't compare: " + ex.Message);
            }
            finally
            {
                _comparing = false;
            }
            _versionFits = fits;
            if (IsInstanceValid(this)) RenderCurrentTab();
        }

        // ---- One by one ----

        private bool _triageOn;
        private readonly List<string> _triageQueue = new();
        private readonly HashSet<string> _triageActed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _triageSkipped = new(StringComparer.Ordinal);
        private readonly Stack<string> _triageHistory = new();

        /// <summary>The row the one-by-one review shows (for tests), or null.</summary>
        public string? TriageCurrent => _triageOn ? CurrentTriage()?.Key : null;

        /// <summary>Starts the one-by-one review: every row to decide, the seed's first, the strongest guesses first.</summary>
        public void StartTriage()
        {
            _triageQueue.Clear();
            _triageActed.Clear();
            _triageSkipped.Clear();
            _triageHistory.Clear();
            _triageQueue.AddRange(Recommendations().OrderBy(f => Matters(f) ? 0 : 1).ThenByDescending(f => f.Suggestions[0].Score).Select(f => f.Key));
            _triageOn = true;
            SelectTab(GuideTab);
            RenderCurrentTab();
        }

        /// <summary>The rows skipped in the review are shown again.</summary>
        public void TriageAgain()
        {
            _triageSkipped.Clear();
            RenderCurrentTab();
        }

        private void LeaveTriage()
        {
            _triageOn = false;
            RenderCurrentTab();
        }

        private Finding? CurrentTriage()
        {
            var recs = Recommendations().ToDictionary(f => f.Key, StringComparer.Ordinal);
            foreach (string key in _triageQueue)
                if (!_triageActed.Contains(key) && !_triageSkipped.Contains(key) && recs.TryGetValue(key, out var f)) return f;
            return null;
        }

        private int TriageLeft()
        {
            var recs = new HashSet<string>(Recommendations().Select(f => f.Key), StringComparer.Ordinal);
            return _triageQueue.Count(k => !_triageActed.Contains(k) && recs.Contains(k));
        }

        /// <summary>A key in the one-by-one review, as the keyboard sends it (for tests too); true when it did something.</summary>
        public bool TriageKey(Key key, bool ctrl = false)
        {
            if (!_triageOn) return false;
            if (key == Key.Escape) { LeaveTriage(); return true; }
            if (ctrl && key == Key.Z) { TriageUndo(); return true; }
            var f = CurrentTriage();
            if (f == null) return false;
            int digit = key is >= Key.Key1 and <= Key.Key9 ? (int)(key - Key.Key1) : key is >= Key.Kp1 and <= Key.Kp9 ? (int)(key - Key.Kp1) : -1;
            if (key is Key.Enter or Key.KpEnter) digit = 0;
            if (digit >= 0)
            {
                if (digit >= f.Suggestions.Count) return false;
                TriageAccept(f, digit);
                return true;
            }
            switch (key)
            {
                case Key.C: TriageChoose(f); return true;
                case Key.N: TriageNotACheck(f); return true;
                case Key.S: TriageSkip(f); return true;
                default: return false;
            }
        }

        private void TriageAccept(Finding f, int choice)
        {
            _recChoice[f.Key] = choice;
            ApplyRecommendations(new List<Finding> { f });
            TriageActed(f.Key);
        }

        private void TriageChoose(Finding f)
        {
            string subject = f.Subject.Substring(f.Subject.IndexOf(':') + 1);
            string what = PackDoctor.RowName(_report, f);
            string key = f.Key;
            if (IsTileRow(f))
                NamePickerDialog.Open(this, "Which Archipelago item does this tile track?", what, ItemNames, f.Suggestions, (id, name) => { LinkTile(subject, id, name); TriageActed(key); });
            else
            {
                var parts = subject.Split('|');
                string section = parts.Length > 1 ? parts[1] : "";
                NamePickerDialog.Open(this, "Which Archipelago location is this?", $"{parts[0]} / {what}", LocationNames, f.Suggestions, (id, name) => { LinkSection(parts[0], section, id, name); TriageActed(key); });
            }
        }

        private void TriageNotACheck(Finding f)
        {
            string subject = f.Subject.Substring(f.Subject.IndexOf(':') + 1);
            if (IsTileRow(f))
            {
                SetTileHidden(subject, true);
                SetStatus($"Tile '{PackDoctor.RowName(_report, f)}' is hidden. Undo brings it back.");
            }
            else
            {
                var parts = subject.Split('|');
                PackFixActions.NotACheck(_original, parts[0], parts.Length > 1 ? parts[1] : "");
                SetStatus($"'{PackDoctor.RowName(_report, f)}' isn't counted as a check any more. Undo brings it back.");
            }
            TriageActed(f.Key);
        }

        private void TriageSkip(Finding f)
        {
            _triageSkipped.Add(f.Key);
            RenderCurrentTab();
        }

        private void TriageActed(string key)
        {
            _triageActed.Add(key);
            _triageHistory.Push(key);
            RenderCurrentTab();
        }

        private void TriageUndo()
        {
            if (!PackFixes.Undo(_key)) return;
            if (_triageHistory.Count > 0) _triageActed.Remove(_triageHistory.Pop());
            SetStatus("Undone.");
            RenderCurrentTab();
        }

        private Control BuildTriage()
        {
            var root = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            root.AddThemeConstantOverride("separation", 10);
            var keys = new KeyCatcher { OnKey = k => TriageKey(k.Keycode, k.CtrlPressed) };
            root.AddChild(keys);
            var top = new HBoxContainer();
            top.AddThemeConstantOverride("separation", 10);
            int left = TriageLeft();
            var progress = Kit.Heading($"One by one: {left} left");
            progress.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            top.AddChild(progress);
            top.AddChild(Kit.Button("Undo (Ctrl+Z)", "Takes back your last decision here", TriageUndo, _triageHistory.Count > 0));
            top.AddChild(Kit.Button("Back to the steps (Esc)", "Leaves the one-by-one review", LeaveTriage));
            root.AddChild(top);

            // A rule the links made here teach, as soon as there is one.
            var patterns = Patterns();
            if (patterns.Count > 0)
            {
                var banner = new HFlowContainer();
                banner.AddThemeConstantOverride("h_separation", 8);
                var text = Kit.Text($"Your links follow a rule ({patterns[0].Description}) that links {patterns[0].Matches.Count} more row{(patterns[0].Matches.Count == 1 ? "" : "s")}.", Good);
                text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                text.CustomMinimumSize = new Vector2(360, 0);
                banner.AddChild(text);
                banner.AddChild(Kit.Button("Preview…", "Shows the rows the rule links, then links them in one undoable step", () => PreviewPattern(0)));
                root.AddChild(banner);
            }

            var f = CurrentTriage();
            if (f == null)
            {
                int skipped = _triageSkipped.Count(k => Recommendations().Any(r => r.Key == k));
                root.AddChild(Kit.Text(skipped > 0 ? $"Every row is decided but the {skipped} you skipped." : "Every row is decided.", Good));
                if (skipped > 0) root.AddChild(Kit.Button("Go through the skipped ones", "Shows the rows you skipped again", TriageAgain));
                return root;
            }

            bool tile = IsTileRow(f);
            string subject = SubjectOf(f);
            var parts = subject.Split('|');
            var pin = tile ? null : _report?.Pack?.Locations.FirstOrDefault(l => l.FullPath == parts[0]);
            var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            body.AddThemeConstantOverride("separation", 16);
            root.AddChild(body);

            var info = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            info.AddThemeConstantOverride("separation", 6);
            body.AddChild(info);
            info.AddChild(Kit.Subtle(tile ? "Key Items tile" : $"Pin {parts[0]}"));
            var name = Kit.Heading(PackDoctor.RowName(_report, f), 1.3f);
            name.SetMeta("triage_row", f.Key);
            info.AddChild(name);
            if (pin?.Sections != null && pin.Sections.Count > 1)
            {
                var others = Kit.Subtle("Its pin's other sections: " + string.Join(", ", pin.Sections.Select(s => s.Name).Where(n => !string.IsNullOrEmpty(n) && n != (parts.Length > 1 ? parts[1] : "")).Take(8)));
                others.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                info.AddChild(others);
            }
            info.AddChild(Kit.Subtle(Matters(f) ? "Atlas's guesses (1–6):" : "Atlas's guesses (1–6); none is a check of your seed:"));
            Button? first = null;
            for (int i = 0; i < Math.Min(6, f.Suggestions.Count); i++)
            {
                var s = f.Suggestions[i];
                int index = i;
                var choice = Kit.Button($"{i + 1}   {s.Label}   ({s.Score:P0})", s.Reason ?? "Take this match", () => TriageAccept(f, index));
                choice.Alignment = HorizontalAlignment.Left;
                choice.AddThemeColorOverride("font_color", s.Score >= 0.85 ? Good : s.Score >= 0.6 ? Warn : Bad);
                first ??= choice;
                info.AddChild(choice);
            }
            var actions = new HFlowContainer();
            actions.AddThemeConstantOverride("h_separation", 8);
            actions.AddThemeConstantOverride("v_separation", 6);
            actions.AddChild(Kit.Button("Choose by name… (C)", "Pick the match from every name", () => TriageChoose(f)));
            actions.AddChild(Kit.Button(tile ? "Not an item: hide it (N)" : "Not a check (N)", tile ? "Hides the tile" : "A marker or a label: it shows no location and isn't counted", () => TriageNotACheck(f)));
            actions.AddChild(Kit.Button("Skip (S)", "Leaves it for later", () => TriageSkip(f)));
            if (!tile) actions.AddChild(Kit.Button("Show on the Maps tab", "Opens the pin in the map editor", () => { _focusPinPath = parts[0]; SelectTab("Maps"); }));
            info.AddChild(actions);

            if (pin != null && MapOf(pin) is { } place && _report?.Pack?.Maps.TryGetValue(place.MapId, out var map) == true)
            {
                var preview = new PinPreview();
                var background = map.Background;
                preview.Show(background, background?.GetSize() ?? new Vector2(Math.Max(1, place.X * 2), Math.Max(1, place.Y * 2)), new Vector2(place.X, place.Y));
                var side = new VBoxContainer();
                side.AddChild(preview);
                side.AddChild(Kit.Subtle(_report.Pack.DisplayNameOfMap(map.Name) + (background == null ? " (its picture isn't loaded)" : "")));
                body.AddChild(side);
            }
            // Enter takes the first guess: the first guess has the keyboard focus.
            if (first != null) Ui.Defer(first, () => first.GrabFocus(), "focusing the first guess");
            return root;
        }

        private static (string MapId, float X, float Y)? MapOf(PopTrackerLocation pin)
        {
            if (!string.IsNullOrEmpty(pin.MapRef)) return (pin.MapRef, pin.X, pin.Y);
            var place = pin.MapLocations?.FirstOrDefault(m => !string.IsNullOrEmpty(m.Map));
            return place == null ? null : (place.Map, place.X, place.Y);
        }
    }
}
