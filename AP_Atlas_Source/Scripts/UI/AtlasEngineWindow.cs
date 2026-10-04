using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core;
using AP_Atlas.Core.EngineSetup;
using Godot;
using Color = Godot.Color;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The Atlas Engine setup page: which engine logic runs on (Atlas's portable copy or the user's Archipelago install),
    /// each setup step with its status and fix, the games the connected slots need, how each slot's world was rebuilt,
    /// and a log. "Set up everything" runs whatever is missing; every download is pinned and hash-checked.
    /// </summary>
    public partial class AtlasEngineWindow : Window
    {
        private static readonly Color Good = Colors.LimeGreen;
        private static readonly Color Bad = Colors.Salmon;
        private static readonly Color Warn = Colors.Orange;
        private static readonly Color Muted = Colors.Gray;

        private static AtlasEngineWindow _open;

        private readonly AppSettings _settings;
        private readonly Func<IEnumerable<SlotTrackerControl>> _slots;
        private readonly Func<IEnumerable<string>> _knownGames;
        private readonly int _fontSize;

        private VBoxContainer _modeBox, _stepsBox, _gamesBox, _slotsBox;
        private Button _setupAll, _cancel;
        private ProgressBar _progress;
        private Label _status;
        private RichTextLabel _log;
        private CancellationTokenSource _cts;
        private bool _busy;
        private Dictionary<string, List<string>> _localApworlds;
        private string _slotsSignature = "";

        public static void Open(Node anyNode, AppSettings settings, Func<IEnumerable<SlotTrackerControl>> slots, Func<IEnumerable<string>> knownGames, int fontSize)
        {
            if (_open != null && IsInstanceValid(_open))
            {
                _open.GrabFocus();
                _open.Render();
                return;
            }
            var w = new AtlasEngineWindow(settings, slots, knownGames, fontSize);
            _open = w;
            anyNode.GetTree().Root.AddChild(w);
            var screen = DisplayServer.ScreenGetSize();
            w.PopupCentered(new Vector2I(Math.Min(1200, (int)(screen.X * 0.7f)), (int)(screen.Y * 0.8f)));
        }

        private AtlasEngineWindow(AppSettings settings, Func<IEnumerable<SlotTrackerControl>> slots, Func<IEnumerable<string>> knownGames, int fontSize)
        {
            _settings = settings;
            _slots = slots;
            _knownGames = knownGames;
            _fontSize = fontSize;
            Title = "Atlas Engine";
            Transient = false;
            Exclusive = false;
            WrapControls = false;
            MinSize = new Vector2I(820, 600);
        }

        public override void _Ready()
        {
            CloseRequested += Close;
            var bg = new Panel();
            bg.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color("#1a1a1f") });
            bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            AddChild(bg);
            var margin = new MarginContainer();
            margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 14);
            AddChild(margin);
            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", 10);
            margin.AddChild(root);

            var split = new VSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            root.AddChild(split);
            var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            split.AddChild(scroll);
            var page = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            page.AddThemeConstantOverride("separation", 8);
            scroll.AddChild(page);

            var intro = new Label
            {
                Text = "Logic runs on Archipelago and the Universal Tracker. Atlas can download and keep its own copy (about 50 MB, " +
                       "no installer or admin rights), or use an Archipelago install you already have.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            intro.AddThemeColorOverride("font_color", Colors.LightGray);
            page.AddChild(intro);

            page.AddChild(Header("Engine"));
            _modeBox = new VBoxContainer();
            page.AddChild(_modeBox);

            page.AddChild(Header("Setup"));
            _stepsBox = new VBoxContainer();
            _stepsBox.AddThemeConstantOverride("separation", 6);
            page.AddChild(_stepsBox);
            var setupRow = new HBoxContainer();
            setupRow.AddThemeConstantOverride("separation", 8);
            _setupAll = new Button { Text = "Set up everything", TooltipText = "Download and install whatever is missing, then run a health check" };
            _setupAll.Pressed += () => RunOperation("Setting up the Atlas Engine", (log, progress, ct) => AtlasEngine.SetUpAsync(AtlasEngine.Current, log, progress, ct));
            setupRow.AddChild(_setupAll);
            _cancel = new Button { Text = "Cancel", Disabled = true };
            _cancel.Pressed += () => _cts?.Cancel();
            setupRow.AddChild(_cancel);
            _progress = new ProgressBar { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MaxValue = 1, Step = 0.001, Visible = false, ShowPercentage = false };
            setupRow.AddChild(_progress);
            page.AddChild(setupRow);

            page.AddChild(Header("Games"));
            page.AddChild(BuildSourcesRow());
            _gamesBox = new VBoxContainer();
            _gamesBox.AddThemeConstantOverride("separation", 4);
            page.AddChild(_gamesBox);
            var verifyRow = new HBoxContainer();
            verifyRow.AddThemeConstantOverride("separation", 8);
            var verify = new Button
            {
                Text = "Verify logic against a seed…",
                TooltipText = "Pick a seed generated with Archipelago (the .zip in its output folder). Atlas rebuilds each world the way a live\n" +
                              "slot does, replays the seed's playthrough, and checks its logic matches the real one at every step."
            };
            verify.Pressed += PickSeed;
            verifyRow.AddChild(verify);
            var sweep = new Button { Text = "Test every game", TooltipText = "Rebuild each installed game with default options and compute its starting logic (a few seconds)" };
            sweep.Pressed += () =>
            {
                var install = AtlasEngine.Current;
                RunOperation("Testing every game", (log, _, ct) => GameSweep.RunAsync(install, log, ct));
            };
            verifyRow.AddChild(sweep);
            var verifyNote = new Label { Text = "Proves the logic for a game against the real generator, not just its location list.", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            verifyNote.AddThemeColorOverride("font_color", Muted);
            verifyRow.AddChild(verifyNote);
            page.AddChild(verifyRow);

            page.AddChild(Header("Slots"));
            _slotsBox = new VBoxContainer();
            _slotsBox.AddThemeConstantOverride("separation", 4);
            page.AddChild(_slotsBox);

            var logBox = new VBoxContainer { CustomMinimumSize = new Vector2(0, 160) };
            split.AddChild(logBox);
            var logHeader = new HBoxContainer();
            logHeader.AddChild(new Label { Text = "Log", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            var copyLog = new Button { Text = "Copy log", Flat = true };
            copyLog.Pressed += () => DisplayServer.ClipboardSet(_log.GetParsedText());
            logHeader.AddChild(copyLog);
            logBox.AddChild(logHeader);
            _log = new RichTextLabel { SizeFlagsVertical = Control.SizeFlags.ExpandFill, ScrollFollowing = true, SelectionEnabled = true, BbcodeEnabled = false };
            _log.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = new Color("#111116"), ContentMarginLeft = 8, ContentMarginTop = 6, ContentMarginRight = 8, ContentMarginBottom = 6 });
            logBox.AddChild(_log);

            var footer = new HBoxContainer();
            root.AddChild(footer);
            _status = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _status.AddThemeColorOverride("font_color", Muted);
            footer.AddChild(_status);
            var folder = new Button { Text = "Open engine folder", TooltipText = "Show the engine's files" };
            folder.Pressed += () =>
            {
                string dir = AtlasEngine.Current.Mode == EngineMode.Portable ? AtlasEngine.EngineDir : AtlasEngine.Current.Root;
                if (Directory.Exists(dir)) OS.ShellOpen(dir);
            };
            footer.AddChild(folder);
            var close = new Button { Text = "Close" };
            close.Pressed += Close;
            footer.AddChild(close);

            AtlasEngine.Changed += OnEngineChanged;
            var timer = new Godot.Timer { WaitTime = 1.5, Autostart = true };
            timer.Timeout += RefreshSlotsIfChanged;
            AddChild(timer);

            AddThemeFontSizeOverride("title_font_size", _fontSize);
            Render();
            ScanLocalApworlds();
            // Fill in the Games list right away when the engine is usable but hasn't been checked this session.
            var current = AtlasEngine.Current;
            if (AtlasEngine.ProblemWith(current) == null && AtlasEngine.LastCheck(current) == null)
                RunOperation("Health check", async (log, _, ct) => { await AtlasEngine.RunCheckAsync(current, log, ct); });
        }

        public override void _ExitTree()
        {
            AtlasEngine.Changed -= OnEngineChanged;
            _cts?.Cancel();
            if (_open == this) _open = null;
        }

        private void Close()
        {
            if (_busy)
            {
                SetStatus("Setup is still running. Cancel it first, or let it finish.", Warn);
                return;
            }
            QueueFree();
        }

        private void OnEngineChanged() => Callable.From(() => { if (IsInstanceValid(this)) Render(); }).CallDeferred();

        // =====================================================================
        // Rendering
        // =====================================================================

        private void Render()
        {
            var install = AtlasEngine.Current;
            RenderMode(install);
            RenderSteps(install);
            RenderGames(install);
            RenderSlots();
            _setupAll.Disabled = _busy;
            string problem = AtlasEngine.ProblemWith(install);
            if (!_busy) SetStatus(problem == null ? $"Ready: logic runs on the {install.Describe()}." : problem, problem == null ? Good : Warn);
            MainTrackerWindow.SetFontSizeRecursive(this, _fontSize);
        }

        private void RenderMode(EngineInstall install)
        {
            Clear(_modeBox);
            var group = new ButtonGroup();
            var portable = new CheckBox
            {
                Text = "Atlas portable engine (recommended)" + (install.Mode == EngineMode.Portable ? "  ← in use" : ""),
                ButtonGroup = group,
                ButtonPressed = install.Mode == EngineMode.Portable,
                TooltipText = $"Python {AtlasEngine.PythonVersion}, Archipelago {AtlasEngine.ArchipelagoVersion} and Universal Tracker {AtlasEngine.TrackerVersion}, kept in Atlas's PortableData folder",
                Disabled = _busy
            };
            Readable(portable);
            portable.Toggled += on => { if (on && AtlasEngine.Current.Mode != EngineMode.Portable) AtlasEngine.SetMode(EngineMode.Portable); };
            _modeBox.AddChild(portable);
            _modeBox.AddChild(Note(AtlasEngine.PortableInstalled ? "Installed in " + AtlasEngine.EngineDir : "Not downloaded yet.", 28));

            var existingRow = new HBoxContainer();
            existingRow.AddThemeConstantOverride("separation", 8);
            string path = _settings.ArchipelagoInstallationPath;
            var existing = new CheckBox
            {
                Text = "My Archipelago install" + (string.IsNullOrEmpty(path) ? "" : ": " + path) + (install.Mode == EngineMode.Existing ? "  ← in use" : ""),
                ButtonGroup = group,
                ButtonPressed = install.Mode == EngineMode.Existing,
                TooltipText = "Use the Archipelago you installed. Atlas adds its bridge apworld to its custom_worlds folder.",
                Disabled = _busy
            };
            Readable(existing);
            existing.Toggled += on =>
            {
                if (!on || AtlasEngine.Current.Mode == EngineMode.Existing) return;
                if (EngineInstall.Existing(_settings.ArchipelagoInstallationPath).CanLaunch) AtlasEngine.SetMode(EngineMode.Existing);
                else PickInstallFolder();
            };
            existingRow.AddChild(existing);
            var change = new Button { Text = "Choose folder…", Disabled = _busy };
            change.Pressed += PickInstallFolder;
            existingRow.AddChild(change);
            var find = new Button { Text = "Find", TooltipText = "Look for Archipelago in the usual places and where its installer registered it", Disabled = _busy };
            find.Pressed += () =>
            {
                var found = AtlasEngine.FindArchipelagoInstalls();
                if (found.Count == 0) { Log("No Archipelago install found. Use Choose folder…, or the portable engine."); return; }
                Log("Found Archipelago at: " + string.Join(", ", found));
                AtlasEngine.UseExistingInstall(found[0]);
            };
            existingRow.AddChild(find);
            _modeBox.AddChild(existingRow);
        }

        private void RenderSteps(EngineInstall install)
        {
            Clear(_stepsBox);
            foreach (var step in AtlasEngine.Steps(install))
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 10);
                var (glyph, color) = step.State switch
                {
                    EngineStepState.Ok => ("✔", Good),
                    EngineStepState.Warning => ("▲", Warn),
                    EngineStepState.Error => ("✖", Bad),
                    _ => ("○", Muted)
                };
                var icon = new Label { Text = glyph, CustomMinimumSize = new Vector2(22, 0), HorizontalAlignment = HorizontalAlignment.Center };
                icon.AddThemeColorOverride("font_color", color);
                row.AddChild(icon);
                row.AddChild(new Label { Text = step.Title, CustomMinimumSize = new Vector2(170, 0) });
                var detail = new Label { Text = step.Detail, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                detail.AddThemeColorOverride("font_color", step.State == EngineStepState.Ok ? Muted : Colors.LightGray);
                row.AddChild(detail);
                if (step.Action != null)
                {
                    var button = new Button { Text = step.Action, Disabled = _busy };
                    var id = step.Id;
                    button.Pressed += () => RunStep(id);
                    row.AddChild(button);
                }
                _stepsBox.AddChild(row);
            }
        }

        private void RenderGames(EngineInstall install)
        {
            Clear(_gamesBox);
            var games = (_knownGames?.Invoke() ?? Enumerable.Empty<string>()).Where(g => !string.IsNullOrEmpty(g) && g != "Archipelago")
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g).ToList();
            var check = AtlasEngine.LastCheck(install);
            if (games.Count == 0)
            {
                _gamesBox.AddChild(Note("Games appear here once you've connected a slot (or saved one in a profile)."));
                return;
            }
            if (check == null) _gamesBox.AddChild(Note("Run the health check to see which games the engine has."));
            foreach (var game in games)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 10);
                bool? installed = check?.Games == null ? null : check.Games.Contains(game, StringComparer.OrdinalIgnoreCase);
                var icon = new Label { Text = installed == true ? "✔" : installed == false ? "✖" : "○", CustomMinimumSize = new Vector2(22, 0), HorizontalAlignment = HorizontalAlignment.Center };
                icon.AddThemeColorOverride("font_color", installed == true ? Good : installed == false ? Bad : Muted);
                row.AddChild(icon);
                row.AddChild(new Label { Text = game, CustomMinimumSize = new Vector2(240, 0) });
                GameSweepResult swept = null;
                GameSweep.LastFor(install)?.Results.TryGetValue(game, out swept);
                var tested = SeedVerifier.For(game, null);
                string testedText = tested == null ? "" : tested.Exact
                    ? $" · logic verified exactly against seed {tested.SeedName} ({tested.Spheres} spheres, {tested.Tested:d})"
                    : $" · logic differs from seed {tested.SeedName}: {tested.Late} late, {tested.Early} early ({tested.Tested:d})";
                var detail = new Label
                {
                    Text = (installed == true ? "Installed" : installed == false ? "Not in the engine: its apworld is needed" : "") +
                           (installed == true && swept != null ? (swept.Ok ? " · rebuilds and computes logic" : " · not testable with default options") : "") + testedText,
                    TooltipText = string.Join("\n", new[] { swept?.Summary, tested?.Verdict }.Where(x => x != null)),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart
                };
                detail.AddThemeColorOverride("font_color", installed == true ? Muted : Colors.LightGray);
                row.AddChild(detail);
                if (installed == false && install.CanLaunch)
                {
                    List<string> local = null;
                    if (install.Mode == EngineMode.Portable && _localApworlds != null) _localApworlds.TryGetValue(game, out local);
                    if (local != null && local.Count > 0)
                    {
                        var copy = new Button { Text = "Copy from my install", TooltipText = "Copy " + local[0] + " into the Atlas engine", Disabled = _busy };
                        string localFile = local[0];
                        copy.Pressed += () => InstallApworld(localFile);
                        row.AddChild(copy);
                    }
                    var source = ApworldSources.Find(game);
                    if (source != null)
                    {
                        string seedChecksum = SeedChecksumFor(game);
                        var download = new Button
                        {
                            Text = seedChecksum != null ? "Download the seed's version…" : "Download…",
                            TooltipText = (seedChecksum != null ? "Finds the version whose data matches your connected seed, then installs it.\n" : "Installs the newest known version.\n") +
                                          "Source: " + (source.Repo != null ? "github.com/" + source.Repo : source.Home ?? "community index"),
                            Disabled = _busy
                        };
                        string g = game;
                        download.Pressed += () => DownloadGame(g, seedChecksum, null);
                        row.AddChild(download);
                    }
                    var pick = new Button { Text = "Choose apworld…", TooltipText = "Pick the game's .apworld file (from its release page or the Archipelago Discord)", Disabled = _busy };
                    pick.Pressed += PickApworld;
                    row.AddChild(pick);
                }
                _gamesBox.AddChild(row);
            }
            if (check?.FailedWorlds?.Count > 0)
            {
                string Why(string world) =>
                    check.FailedDetails != null && check.FailedDetails.TryGetValue(world, out var d) && !string.IsNullOrEmpty(d.MissingModule)
                        ? $"{world} (needs package {d.MissingModule})" : world;
                bool fixable = AtlasEngine.InstallableWorldPackages(install, includeTried: true).Count > 0;
                _gamesBox.AddChild(Note($"{check.FailedWorlds.Count} world(s) couldn't load: {string.Join(", ", check.FailedWorlds.Take(10).Select(Why))}." +
                    (fixable ? " Setup → Python packages → Install adds the packages they declare." : "")));
            }
        }

        private string SlotsSignature() =>
            string.Join("|", (_slots?.Invoke() ?? Enumerable.Empty<SlotTrackerControl>()).Where(IsInstanceValid)
                .Select(s => $"{s.SlotName}:{s.EngineRunning}:{s.EngineBooting}:{s.EngineProblem?.Code}:{s.LinkedYamlSetting}:{s.EngineYamlInfo?["source"]}:{s.ApworldMatchesSeed}"));

        private void RefreshSlotsIfChanged()
        {
            string sig = SlotsSignature();
            if (sig == _slotsSignature) return;
            RenderSlots();
            MainTrackerWindow.SetFontSizeRecursive(_slotsBox, _fontSize);
        }

        private void RenderSlots()
        {
            _slotsSignature = SlotsSignature();
            Clear(_slotsBox);
            var slots = (_slots?.Invoke() ?? Enumerable.Empty<SlotTrackerControl>()).Where(IsInstanceValid).ToList();
            if (slots.Count == 0)
            {
                _slotsBox.AddChild(Note("Connect a slot to see how the engine rebuilds its world."));
                return;
            }
            foreach (var slot in slots)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 10);
                var (glyph, color, text) = SlotState(slot);
                var icon = new Label { Text = glyph, CustomMinimumSize = new Vector2(22, 0), HorizontalAlignment = HorizontalAlignment.Center };
                icon.AddThemeColorOverride("font_color", color);
                row.AddChild(icon);
                row.AddChild(new Label { Text = $"{slot.SlotName} ({slot.Game})", CustomMinimumSize = new Vector2(240, 0), ClipText = true });
                var detail = new Label { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                detail.AddThemeColorOverride("font_color", Colors.LightGray);
                row.AddChild(detail);
                var link = new Button
                {
                    Text = slot.LinkedYamlSetting == null ? "Link YAML…" : "Change YAML…",
                    TooltipText = "The player YAML used to generate this seed. Only needed when the server's data can't rebuild the world.",
                    Disabled = slot.EngineBooting
                };
                link.Pressed += slot.PickYaml;
                row.AddChild(link);
                if (slot.LinkedYamlSetting != null)
                {
                    var unlink = new Button { Text = "Unlink", TooltipText = slot.LinkedYamlSetting, Disabled = slot.EngineBooting };
                    unlink.Pressed += () => slot.LinkYaml(null);
                    row.AddChild(unlink);
                }
                if (slot.ApworldMatchesSeed == false && ApworldSources.Find(slot.Game) != null)
                {
                    var fix = new Button { Text = "Find the seed's version…", TooltipText = "Download the version of this game's apworld whose data matches the seed, install it and restart logic", Disabled = slot.EngineBooting || _busy };
                    var s = slot;
                    fix.Pressed += () => DownloadGame(s.Game, s.ServerChecksumFor(s.Game), s);
                    row.AddChild(fix);
                }
                var retry = new Button { Text = "Restart logic", TooltipText = "Start this slot's logic engine again", Disabled = slot.EngineBooting };
                retry.Pressed += slot.RetryLogicEngine;
                row.AddChild(retry);
                _slotsBox.AddChild(row);
            }
        }

        private static (string, Color, string) SlotState(SlotTrackerControl slot)
        {
            if (slot.EngineBooting) return ("…", Muted, "Starting the logic engine…");
            if (slot.EngineProblem != null) return ("✖", Bad, slot.EngineProblem.Message);
            if (!slot.EngineRunning) return ("○", Muted, "Logic isn't running");
            var info = slot.EngineYamlInfo;
            string source = info?["source"]?.ToString();
            string file = info?["file"]?.ToString();
            string how = source switch
            {
                "not_needed" => "Rebuilt from the server's data (this game needs no YAML)",
                "slot_data" => $"Rebuilt from the options in the server's slot data ({info?["options_from_slot_data"]} of {info?["options_total"]} options)",
                "linked" => $"Rebuilt from your linked YAML {file}",
                "players" => $"Rebuilt from {file} in the Players folder",
                _ => "Running"
            };
            var match = info?["match"];
            if (slot.ApworldMatchesSeed == false)
                return ("▲", Warn, $"{how}, but the installed {slot.Game} apworld" + (slot.InstalledWorldVersion != null ? $" (version {slot.InstalledWorldVersion})" : "") +
                                   " isn't the one this seed was made with (its data differs from the server's). Logic may be wrong: install the version the seed used.");
            if (match?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && !(bool)match)
                return ("▲", Warn, $"{how}, but it differs from the server: {info["missing"]} locations missing, {info["extra"]} extra. Link the YAML used for the seed.");
            if (match?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                return ("✔", Good, $"{how}; all {info["expected"]} locations match the server" + (slot.ApworldMatchesSeed == true ? ", and the apworld's data matches the seed's." : "."));
            return ("✔", Good, how);
        }

        // =====================================================================
        // Actions
        // =====================================================================

        private void RunStep(EngineStepId id)
        {
            var install = AtlasEngine.Current;
            string name = id switch
            {
                EngineStepId.Runtime => "Python runtime",
                EngineStepId.Archipelago => "Archipelago",
                EngineStepId.Packages => "Python packages",
                EngineStepId.Tracker => "Universal Tracker",
                EngineStepId.Bridge => "Atlas bridge",
                _ => "Health check"
            };
            // Every step runs through the engine's safe path: exclusive, verified, rolled back if the check fails.
            void Run() => RunOperation(name, (log, p, ct) => AtlasEngine.RunStepAsync(install, id, log, p, ct));
            if (id == EngineStepId.Archipelago && install.Mode == EngineMode.Existing) { PickInstallFolder(); return; }
            var others = id == EngineStepId.Tracker ? install.FindAllTrackers() : new List<string>();
            if (install.Mode == EngineMode.Existing && others.Count > 0)
                Confirm($"Atlas will move {string.Join(", ", others.Select(Path.GetFileName))} out of your Archipelago install " +
                        $"(into Atlas's engine backups folder) and install Universal Tracker {AtlasEngine.TrackerVersion}, the version Atlas is tested with. " +
                        "Your own Universal Tracker client will use that version too.", Run);
            else Run();
        }

        private void InstallApworld(string source)
        {
            var install = AtlasEngine.Current;
            RunOperation("Adding " + Path.GetFileName(source), (log, _, ct) => AtlasEngine.InstallApworldAsync(install, source, log, ct));
        }

        private Control BuildSourcesRow()
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var list = ApworldSources.List;
            var info = new Label { Text = $"Apworld sources: {list.Games.Count} games (list built {list.Built}), plus each project's GitHub releases.", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            info.AddThemeColorOverride("font_color", Muted);
            row.AddChild(info);
            var url = new LineEdit { PlaceholderText = "Newer list URL (optional)", Text = _settings.ApworldSourcesUrl ?? "", CustomMinimumSize = new Vector2(260, 0), TooltipText = "A URL serving a newer Atlas apworld source list (for example a maintained fork of the community index)." };
            row.AddChild(url);
            var refresh = new Button { Text = "Refresh" };
            refresh.Pressed += () =>
            {
                _settings.ApworldSourcesUrl = url.Text.Trim();
                DataManager.SaveSettings(_settings);
                RunOperation("Refreshing the apworld source list", async (log, _, ct) => log(await ApworldSources.RefreshAsync(_settings.ApworldSourcesUrl, ct)));
            };
            row.AddChild(refresh);
            return row;
        }

        /// <summary>The checksum of a connected slot's seed for this game (to pick the matching apworld version), or null.</summary>
        private string SeedChecksumFor(string game) =>
            (_slots?.Invoke() ?? Enumerable.Empty<SlotTrackerControl>()).Where(IsInstanceValid)
                .Where(s => string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.ServerChecksumFor(game)).FirstOrDefault(c => c != null);

        /// <summary>
        /// Downloads a game's apworld, after the user approves the source: the version matching a seed when its checksum
        /// is known, else the newest. Installs it (verified by a health check) and restarts logic for a waiting slot.
        /// </summary>
        private void DownloadGame(string game, string seedChecksum, SlotTrackerControl slot)
        {
            var source = ApworldSources.Find(game);
            if (source == null) return;
            var newest = source.Versions.LastOrDefault();
            string where = source.Repo != null ? "github.com/" + source.Repo : newest != null ? ApworldSources.SourceKey(newest.Url) : "?";
            void Go()
            {
                var install = AtlasEngine.Current;
                RunOperation(seedChecksum != null ? $"Finding {game}'s version for the seed" : $"Downloading {game}", async (log, _, ct) =>
                {
                    string file;
                    if (seedChecksum != null)
                    {
                        var (version, match) = await ApworldSources.FindMatchingAsync(install, game, seedChecksum, log, ct);
                        if (match == null) return;
                        log($"Installing {game} {version.Version}…");
                        file = match;
                    }
                    else
                    {
                        var versions = await ApworldSources.VersionsAsync(source, ct);
                        var latest = versions.FirstOrDefault() ?? throw new Exception("No versions are listed.");
                        file = await ApworldSources.DownloadAsync(source, latest, log, ct);
                        log($"Installing {game} {latest.Version}…");
                    }
                    bool ok = await AtlasEngine.InstallApworldAsync(install, file, log, ct);
                    if (ok && slot != null) Callable.From(() => { if (IsInstanceValid(slot)) slot.RetryLogicEngine(); }).CallDeferred();
                });
            }
            if (newest != null && ApworldSources.IsApproved(_settings, newest.Url)) { Go(); return; }
            var dialog = new ConfirmationDialog
            {
                Title = "Download an apworld",
                DialogText = $"Download {game} from {where}?\n\n" +
                             (seedChecksum != null ? "Atlas will try the listed versions (newest first) until one's data matches your seed, and install that one.\n" : "Atlas will install the newest listed version.\n") +
                             "Every file is checked against its published SHA-256 when one exists.\n\n" +
                             "Apworlds are programs that run inside the engine. Only continue if you trust this source.",
                DialogAutowrap = true,
                MinSize = new Vector2I(560, 0),
                OkButtonText = "Download"
            };
            var trust = new CheckBox { Text = $"Don't ask again for {where}" };
            dialog.AddChild(trust);
            dialog.Confirmed += () =>
            {
                if (trust.ButtonPressed && newest != null) ApworldSources.Approve(_settings, newest.Url);
                dialog.QueueFree();
                Go();
            };
            dialog.Canceled += () => dialog.QueueFree();
            AddChild(dialog);
            dialog.PopupCentered();
        }

        private void PickSeed()
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.zip, *.archipelago ; Generated Archipelago seed" },
                UseNativeDialog = true,
                Title = "Choose a generated seed (Archipelago's output folder)"
            };
            string folder = SeedVerifier.DefaultSeedFolder();
            if (folder != null) dialog.CurrentDir = folder;
            dialog.FileSelected += path =>
            {
                dialog.QueueFree();
                var install = AtlasEngine.Current;
                RunOperation("Seed test", (log, _, ct) => SeedVerifier.VerifyAsync(install, path, log, ct));
            };
            dialog.Canceled += () => dialog.QueueFree();
            GetTree().Root.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        private void PickApworld()
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.apworld ; Archipelago world" },
                UseNativeDialog = true,
                Title = "Choose an apworld to add to the engine"
            };
            dialog.FileSelected += path =>
            {
                dialog.QueueFree();
                string game = AtlasEngine.GameOfApworld(path);
                Confirm($"Add {Path.GetFileName(path)}{(game != null ? $" ({game})" : "")} to the {AtlasEngine.Current.Describe()}?\n\n" +
                        "Apworlds are programs: only add ones from a source you trust (the game's official release page or the Archipelago Discord).",
                    () => InstallApworld(path));
            };
            dialog.Canceled += () => dialog.QueueFree();
            GetTree().Root.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        private void PickInstallFolder()
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenDir,
                Access = FileDialog.AccessEnum.Filesystem,
                UseNativeDialog = true,
                Title = "Choose your Archipelago folder (the one with ArchipelagoLauncher.exe)"
            };
            if (Directory.Exists(_settings.ArchipelagoInstallationPath)) dialog.CurrentDir = _settings.ArchipelagoInstallationPath;
            dialog.DirSelected += dir =>
            {
                dialog.QueueFree();
                if (!File.Exists(Path.Combine(dir, "ArchipelagoLauncher.exe")))
                {
                    Log($"{dir} doesn't contain ArchipelagoLauncher.exe; choose the folder Archipelago is installed in.");
                    Render();
                    return;
                }
                Log("Using the Archipelago install at " + dir);
                AtlasEngine.UseExistingInstall(dir);
            };
            dialog.Canceled += () => { dialog.QueueFree(); Render(); };
            GetTree().Root.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        /// <summary>Runs one setup operation on a worker thread, with progress, cancel and log, then re-renders.</summary>
        private void RunOperation(string name, Func<Action<string>, Action<float>, CancellationToken, Task> operation)
        {
            if (_busy) return;
            if (AtlasEngine.SetupRunning)
            {
                SetStatus("Another engine change is still running; wait for it to finish.", Warn);
                return;
            }
            _busy = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _cancel.Disabled = false;
            _progress.Visible = true;
            _progress.Value = 0;
            SetStatus(name + "…", Colors.LightGray);
            Render();
            void log(string line) => Callable.From(() => { if (IsInstanceValid(this)) Log(line); }).CallDeferred();
            void progress(float f) => Callable.From(() =>
            {
                if (!IsInstanceValid(this)) return;
                _progress.Value = f < 0 ? 0 : f;
            }).CallDeferred();
            Task.Run(async () =>
            {
                string failure = null;
                try { await operation(log, progress, ct); }
                catch (OperationCanceledException) { failure = "Cancelled."; }
                catch (Exception ex) { failure = ex.Message; Logger.LogWarning("[Atlas Engine] " + ex); }
                Callable.From(() =>
                {
                    if (!IsInstanceValid(this)) return;
                    _busy = false;
                    _cancel.Disabled = true;
                    _progress.Visible = false;
                    if (failure != null) Log(name + " stopped: " + failure);
                    Render();
                    if (failure != null) SetStatus(name + " stopped: " + failure, Bad);
                    ScanLocalApworlds();
                }).CallDeferred();
            });
        }

        /// <summary>Finds apworlds in the user's Archipelago installs, by game, for "Copy from my install".</summary>
        private void ScanLocalApworlds()
        {
            Task.Run(() =>
            {
                var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in AtlasEngine.FindArchipelagoInstalls())
                {
                    foreach (var dir in new[] { Path.Combine(root, "custom_worlds"), Path.Combine(root, "lib", "worlds") })
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (var file in Directory.GetFiles(dir, "*.apworld"))
                        {
                            string game = AtlasEngine.GameOfApworld(file);
                            if (game == null) continue;
                            if (!map.TryGetValue(game, out var list)) map[game] = list = new List<string>();
                            list.Add(file);
                        }
                    }
                }
                Callable.From(() =>
                {
                    if (!IsInstanceValid(this)) return;
                    _localApworlds = map;
                    RenderGames(AtlasEngine.Current);
                    MainTrackerWindow.SetFontSizeRecursive(_gamesBox, _fontSize);
                }).CallDeferred();
            });
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private void Log(string line)
        {
            _log.AddText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
        }

        private void SetStatus(string text, Color color)
        {
            _status.Text = text;
            _status.AddThemeColorOverride("font_color", color);
        }

        private void Confirm(string text, Action onYes)
        {
            var dialog = new ConfirmationDialog { Title = "Atlas Engine", DialogText = text, DialogAutowrap = true, MinSize = new Vector2I(520, 0) };
            dialog.Confirmed += () => { dialog.QueueFree(); onYes(); };
            dialog.Canceled += () => dialog.QueueFree();
            AddChild(dialog);
            dialog.PopupCentered();
        }

        private static Label Header(string text)
        {
            var label = new Label { Text = text.ToUpperInvariant() };
            label.AddThemeColorOverride("font_color", ThemeColors.Accent);
            return label;
        }

        private static Control Note(string text, int indent = 0)
        {
            var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            label.AddThemeColorOverride("font_color", Muted);
            if (indent <= 0) return label;
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", indent);
            margin.AddChild(label);
            return margin;
        }

        /// <summary>The app theme draws pressed buttons' text dark (for its accent fill); radio rows have no fill.</summary>
        private static void Readable(BaseButton button)
        {
            foreach (var key in new[] { "font_pressed_color", "font_hover_pressed_color", "font_color", "font_focus_color", "font_hover_color" })
                button.AddThemeColorOverride(key, Colors.White);
            button.AddThemeColorOverride("font_disabled_color", Muted);
        }

        private static void Clear(Node box)
        {
            foreach (Node child in box.GetChildren())
            {
                box.RemoveChild(child);
                child.QueueFree();
            }
        }
    }
}
