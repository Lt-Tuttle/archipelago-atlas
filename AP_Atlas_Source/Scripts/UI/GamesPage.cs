#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core;
using AP_Atlas.Core.EngineSetup;
using AP_Atlas.Core.Games;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>What the Games page needs from the window.</summary>
    public sealed class GamesHooks
    {
        public required AppSettings Settings { get; init; }
        public required Func<string, string> Tr { get; init; }
        public required Func<IReadOnlyList<MultiworldProfile>> Profiles { get; init; }
        public required Func<IEnumerable<SlotTrackerControl>> LiveSlots { get; init; }
        public required Action<string, Color> Toast { get; init; }
        public required Action<Tool> ShowTool { get; init; }
        public required Action OpenEngineSetup { get; init; }
    }

    /// <summary>
    /// The Games page: every game Atlas knows, in three groups (Official: they ship with Archipelago; Community: from the
    /// community index; Added by you: projects you added and apworlds you installed that the index doesn't know), found by
    /// typed words and sorted by name, by readiness or by when you last played. A game's page walks its setup through in
    /// one place: the apworld in the engine (installed or updated from a project you trust), a map pack, a YAML (kept in
    /// Atlas's YAML folder, a multi-game one under each of its games), the loose files some games need (downloaded from
    /// the game's release into the game's own folder; Atlas never runs them), your slots that play it (with the version
    /// picker for a seed made with another version), a check of the game, and its setup guide.
    /// </summary>
    public sealed partial class GamesPage : VBoxContainer
    {
        public enum Section
        {
            Official,
            Community,
            Yours
        }

        public sealed record GameEntry(string Game, Section Section, bool InEngine, string? Repo, string? Guide);

        private readonly GamesHooks _hooks;
        private readonly Func<string, string> _tr;
        private readonly VBoxContainer _explorer;
        private readonly LineEdit _search;
        private readonly OptionButton _sort;
        private readonly Tree _list;
        private readonly VBoxContainer _detail;
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private List<GameEntry> _games = new();
        private string? _selected;
        private CancellationTokenSource? _running;
        private YamlLibrary? _yamls;
        private List<LoadedPack> _packs = new();

        /// <summary>The explorer's content: the search, the sort and the list.</summary>
        public Control SidebarContent => _explorer;

        /// <summary>Every game the page lists, by section (for tests).</summary>
        public IReadOnlyList<GameEntry> Games => _games;

        /// <summary>The games the list shows now (after the search), in order (for tests).</summary>
        public List<string> ShownGames { get; private set; } = new();

        /// <summary>The game whose page shows (null: the overview).</summary>
        public string? SelectedGame => _selected;

        /// <summary>The status line (for tests).</summary>
        public string StatusText => _status.Text;

        public GamesPage(GamesHooks hooks)
        {
            _hooks = hooks;
            _tr = hooks.Tr;
            Name = "GamesPage";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            AddThemeConstantOverride("separation", 6);

            _explorer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            _explorer.AddThemeConstantOverride("separation", 6);
            _search = new LineEdit { PlaceholderText = _tr("Search games"), ClearButtonEnabled = true, AccessibilityName = _tr("Search games") };
            _search.TextChanged += _ => FillList();
            _explorer.AddChild(_search);
            var sortRow = new HBoxContainer();
            var sortLabel = Kit.Muted(_tr("Sort:"));
            sortLabel.AutowrapMode = TextServer.AutowrapMode.Off;
            sortRow.AddChild(sortLabel);
            _sort = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = _tr("Sort games") };
            _sort.AddItem(_tr("A to Z"));
            _sort.AddItem(_tr("Ready first"));
            _sort.AddItem(_tr("Played recently"));
            _sort.ItemSelected += _ => FillList();
            sortRow.AddChild(_sort);
            _explorer.AddChild(sortRow);
            _list = new Tree { HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill, SelectMode = Tree.SelectModeEnum.Single, AccessibilityName = _tr("Games") };
            _list.ItemSelected += () =>
            {
                var item = _list.GetSelected();
                if (item != null && item.GetMetadata(0).VariantType == Variant.Type.String) ShowGame(item.GetMetadata(0).AsString());
            };
            _explorer.AddChild(_list);
            _explorer.AddChild(Kit.Button(_tr("Add a game's project…"), _tr("Paste the GitHub link of a game's apworld project: Atlas lists the game under Added by you and can install it."), AddProject, small: true));

            var statusRow = new HBoxContainer();
            statusRow.AddThemeConstantOverride("separation", 8);
            _progress = new ProgressBar { Indeterminate = true, Visible = false, CustomMinimumSize = new Vector2(120, 0), ShowPercentage = false, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            statusRow.AddChild(_progress);
            _status = Kit.Muted("");
            _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            statusRow.AddChild(_status);
            var scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(side, 16);
            _detail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _detail.AddThemeConstantOverride("separation", 10);
            margin.AddChild(_detail);
            scroll.AddChild(margin);
            AddChild(scroll);
            AddChild(statusRow);
        }

        /// <summary>The page shows: its lists are read again (cheap: nothing is asked of any site).</summary>
        public void OnShown() => Refresh();

        public void Refresh()
        {
            _yamls = YamlLibrary.Load(DataManager.GetDataDirectory());
            _packs = ReadPacks();
            _games = CollectGames();
            FillList();
            if (_selected != null && _games.Any(g => g.Game == _selected)) ShowGame(_selected);
            else ShowOverview();
        }

        // ---- The list ----

        private List<GameEntry> CollectGames()
        {
            var install = AtlasEngine.Current;
            var check = install != null ? AtlasEngine.LastCheck(install) : null;
            var inEngine = new HashSet<string>((check?.Games ?? new List<string>()).Where(g => g != "Archipelago" && g != "Universal Tracker"), StringComparer.OrdinalIgnoreCase);
            var games = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in ApworldSources.List.Games.Where(g => !string.IsNullOrWhiteSpace(g.Game)))
                games[source.Game] = new GameEntry(source.Game, Section.Community, inEngine.Contains(source.Game), source.Repo, HttpsOrNull(source.Home));
            foreach (var (game, repos) in _hooks.Settings.ExtraApworldRepos ?? new Dictionary<string, List<string>>())
                if (!games.ContainsKey(game)) games[game] = new GameEntry(game, Section.Yours, inEngine.Contains(game), repos.FirstOrDefault(), null);
            var fromFiles = install != null ? ApworldSources.GamesWithApworldFiles(install) : new HashSet<string>();
            foreach (string game in inEngine)
            {
                if (games.ContainsKey(game)) continue;
                // A game the engine has from an apworld file (not shipped with Archipelago) is one the user added.
                bool added = fromFiles.Contains(game);
                games[game] = new GameEntry(game, added ? Section.Yours : Section.Official, true, null, added ? null : OfficialGuide(game));
            }
            // Games the user's multiworlds play are listed even before the engine knows them.
            foreach (string game in PlayedGames().Keys)
                if (!games.ContainsKey(game)) games[game] = new GameEntry(game, Section.Yours, inEngine.Contains(game), null, null);
            return games.Values.ToList();
        }

        private static string? HttpsOrNull(string? url) => url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;

        private static string OfficialGuide(string game) => "https://archipelago.gg/games/" + Uri.EscapeDataString(game) + "/info/en";

        /// <summary>Each game the user's multiworlds play, with when it was last played (wall clock: shown and sorted only).</summary>
        private Dictionary<string, DateTime> PlayedGames()
        {
            var played = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in _hooks.Profiles())
                foreach (var stats in profile.SavedStats.Values)
                    if (!string.IsNullOrWhiteSpace(stats.GameName) && (!played.TryGetValue(stats.GameName, out var when) || stats.LastUpdated > when))
                        played[stats.GameName] = stats.LastUpdated;
            foreach (var slot in _hooks.LiveSlots())
                if (!string.IsNullOrWhiteSpace(slot.Game)) played[slot.Game] = DateTime.Now; // wall clock: compared with saved times
            return played;
        }

        private bool IsReady(GameEntry entry) => entry.InEngine && PacksFor(entry.Game).Count > 0;

        private void FillList()
        {
            _list.Clear();
            var root = _list.CreateItem();
            string[] words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var played = PlayedGames();
            IEnumerable<GameEntry> shown = _games.Where(g => words.All(w => g.Game.Contains(w, StringComparison.OrdinalIgnoreCase)));
            shown = _sort.Selected switch
            {
                1 => shown.OrderByDescending(IsReady).ThenByDescending(g => g.InEngine).ThenBy(g => g.Game, StringComparer.OrdinalIgnoreCase),
                2 => shown.OrderByDescending(g => played.TryGetValue(g.Game, out var when) ? when : DateTime.MinValue).ThenBy(g => g.Game, StringComparer.OrdinalIgnoreCase),
                _ => shown.OrderBy(g => g.Game, StringComparer.OrdinalIgnoreCase)
            };
            var list = shown.ToList();
            ShownGames = new List<string>();
            foreach (var section in new[] { Section.Official, Section.Community, Section.Yours })
            {
                var inSection = list.Where(g => g.Section == section).ToList();
                if (inSection.Count == 0) continue;
                var header = _list.CreateItem(root);
                header.SetText(0, SectionTitle(section) + $" ({inSection.Count})");
                header.SetSelectable(0, false);
                header.SetCustomBgColor(0, ThemeColors.AccentTint);
                header.SetCustomColor(0, ThemeColors.Heading);
                foreach (var game in inSection)
                {
                    var row = _list.CreateItem(header);
                    row.SetText(0, (IsReady(game) ? "✔ " : game.InEngine ? "● " : "○ ") + game.Game);
                    row.SetTooltipText(0, IsReady(game) ? _tr("In the engine, with a map pack") : game.InEngine ? _tr("In the engine") : _tr("Not in the engine yet"));
                    row.SetMetadata(0, game.Game);
                    if (game.Game == _selected) row.Select(0);
                    ShownGames.Add(game.Game);
                }
            }
        }

        private string SectionTitle(Section section) => section switch
        {
            Section.Official => _tr("Official"),
            Section.Community => _tr("Community"),
            _ => _tr("Added by you")
        };

        // ---- The overview ----

        private void ShowOverview()
        {
            _selected = null;
            Clear(_detail);
            _detail.AddChild(Kit.Heading(_tr("Games"), 1.4f));
            int official = _games.Count(g => g.Section == Section.Official), community = _games.Count(g => g.Section == Section.Community), yours = _games.Count(g => g.Section == Section.Yours);
            _detail.AddChild(Wrapped(Kit.Text(_tr("Pick a game on the left to set it up: its apworld in the Atlas Engine, a map pack, your YAML and any files its setup needs, all from its page."))));
            _detail.AddChild(Wrapped(Kit.Muted(_tr("{0} official games (they ship with Archipelago), {1} from the community index, {2} added by you.")
                .Replace("{0}", official.ToString()).Replace("{1}", community.ToString()).Replace("{2}", yours.ToString()))));
            var install = AtlasEngine.Current;
            if (install == null || !install.CanLaunch)
            {
                _detail.AddChild(Wrapped(Kit.Text(_tr("The Atlas Engine isn't set up yet, so Atlas can't say which games it has."), ThemeColors.Warning)));
                _detail.AddChild(Kit.Button(_tr("Set up the Atlas Engine…"), null, _hooks.OpenEngineSetup));
                return;
            }
            var row = new HFlowContainer();
            row.AddThemeConstantOverride("h_separation", 8);
            row.AddChild(Kit.Button(_tr("Check every game"), _tr("Rebuilds each game in the engine with default options and computes its starting logic (a minute or two)."), () =>
                Run(_tr("Checking every game…"), (log, ct) => GameSweep.RunAsync(install, log, ct), _ => _tr("Checked every game."))));
            row.AddChild(Kit.Button(_tr("Open the games folder"), _tr("Where Atlas keeps each game's apworld releases and files."), () => OpenFolder(GameFiles.GamesFolder(DataManager.GetDataDirectory()))));
            row.AddChild(Kit.Button(_tr("Open the YAML folder"), _tr("Where Atlas keeps the YAMLs you add."), () => OpenFolder(GameFiles.YamlsFolder(DataManager.GetDataDirectory()))));
            _detail.AddChild(row);
        }

        // ---- A game's page ----

        private void ShowGame(string game)
        {
            _selected = game;
            var entry = _games.FirstOrDefault(g => g.Game == game);
            if (entry == null)
            {
                ShowOverview();
                return;
            }
            Clear(_detail);
            var install = AtlasEngine.Current;
            bool engineReady = install != null && install.CanLaunch;
            var check = install != null ? AtlasEngine.LastCheck(install) : null;
            _detail.AddChild(Kit.Heading(game, 1.4f));
            _detail.AddChild(Wrapped(Kit.Muted(entry.Section switch
            {
                Section.Official => _tr("Official: ships with Archipelago, so the Atlas Engine has it."),
                Section.Community => _tr("Community: from the community index") + (entry.Repo != null ? " (github.com/" + entry.Repo + ")." : "."),
                _ => _tr("Added by you") + (entry.Repo != null ? " (github.com/" + entry.Repo + ")." : ".")
            })));

            // Get ready: each step says what Atlas has, with the button that does it.
            _detail.AddChild(Kit.Heading(_tr("Get ready"), 1.15f));
            var steps = new VBoxContainer();
            steps.AddThemeConstantOverride("separation", 8);
            _detail.AddChild(steps);

            // 1. The apworld.
            var installed = engineReady ? ApworldSources.InstalledCopies(install!, game) : new List<(string File, string Sha256)>();
            string? version = installed.Select(c => ApworldSources.KnownSourceOf(c.Sha256).Tag).FirstOrDefault(t => t != null);
            var apworldButtons = new List<Button>();
            if (!engineReady) apworldButtons.Add(Kit.Button(_tr("Set up the Atlas Engine…"), null, _hooks.OpenEngineSetup));
            else
            {
                if (entry.Section != Section.Official && (entry.Repo != null || ApworldSources.Find(game) != null))
                    apworldButtons.Add(Kit.Button(entry.InEngine ? _tr("Update…") : _tr("Install…"), _tr("Downloads the newest release from the game's project (you're asked to trust it once) and installs it in the Atlas Engine."), () => InstallNewest(game)));
                if (entry.Section != Section.Official)
                    apworldButtons.Add(Kit.Button(_tr("Choose a file…"), _tr("Install an .apworld file you have."), () => InstallFile(game)));
                apworldButtons.Add(Kit.Button(_tr("Check this game"), _tr("Rebuilds the game with default options and computes its starting logic."), () =>
                    Run(_tr("Checking {0}…").Replace("{0}", game), (log, ct) => GameSweep.RunAsync(install!, log, ct, new[] { game }), _ => CheckSummary(install!, game))));
            }
            Step(steps, entry.InEngine, _tr("The apworld in the Atlas Engine"),
                !engineReady ? _tr("The Atlas Engine isn't set up yet.")
                : entry.InEngine ? _tr("In the engine") + (version != null ? " (" + version + ")" : "") + "." + (CheckSummary(install!, game) is { Length: > 0 } s ? " " + s : "")
                : check == null ? _tr("Atlas hasn't checked the engine yet: the engine window's health check says which games it has.")
                : _tr("Not in the engine yet."), apworldButtons);

            // 2. A map pack.
            var packs = PacksFor(game);
            Step(steps, packs.Count > 0, _tr("A map pack"),
                packs.Count > 0 ? _tr("Installed: {0}.").Replace("{0}", string.Join(", ", packs.Select(p => p.Manifest?.Name ?? Path.GetFileNameWithoutExtension(p.SourcePath))))
                    : _tr("None installed: the Map Tracker shows the game's map with one (a PopTracker pack)."),
                new List<Button> { Kit.Button(_tr("Map Packs"), _tr("Install a pack from its project, or a .zip you have; the Pack Doctor looks it over."), () => _hooks.ShowTool(Tool.MapPacks)) });

            // 3. A YAML.
            var yamls = _yamls?.For(game) ?? new List<YamlEntry>();
            var linked = _hooks.LiveSlots().Where(s => s.Game == game && s.LinkedYamlSetting != null).ToList();
            Step(steps, yamls.Count > 0 || linked.Count > 0, _tr("Your YAML"),
                yamls.Count > 0 ? _tr("{0} in Atlas's YAML folder.").Replace("{0}", yamls.Count.ToString())
                    : _tr("Most games rebuild your world from the server's data; a few need your YAML. Keep yours here to link it to a slot in one click."),
                new List<Button> { Kit.Button(_tr("Add YAML…"), _tr("Copies a player YAML into Atlas's YAML folder (a multi-game YAML is listed under each of its games)."), AddYaml) });
            foreach (var yaml in yamls) _detail.AddChild(YamlRow(game, yaml));

            // 4. Files the game's setup needs (optional).
            string tools = GameFiles.ToolsFolder(DataManager.GetDataDirectory(), game);
            int toolCount = Directory.Exists(tools) ? Directory.GetFiles(tools).Count(f => !f.EndsWith("tools.json", StringComparison.OrdinalIgnoreCase)) : 0;
            var toolButtons = new List<Button>();
            string? repo = entry.Repo ?? ApworldSources.Find(game)?.Repo;
            if (repo != null) toolButtons.Add(Kit.Button(_tr("Get from the release…"), _tr("Lists the files of the project's newest release besides the apworld; you pick what to download."), () => ListReleaseFiles(game, repo)));
            toolButtons.Add(Kit.Button(_tr("Open folder"), _tr("The game's own folder in Atlas: its tools and the apworld releases Atlas downloaded."), () => OpenFolder(GameFiles.GameFolder(DataManager.GetDataDirectory(), game))));
            Step(steps, toolCount > 0, _tr("Files its setup needs (only if its guide asks)"),
                (toolCount > 0 ? _tr("{0} file(s) in the game's folder.").Replace("{0}", toolCount.ToString()) + " " : "") +
                _tr("Some games need a client, a patcher or a .bat from their release. Atlas keeps them in the game's folder and never runs them: the setup guide says what to do with each."),
                toolButtons);

            // Your slots that play it.
            var slots = _hooks.LiveSlots().Where(s => string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase)).ToList();
            if (slots.Count > 0)
            {
                _detail.AddChild(Kit.Heading(_tr("Your slots"), 1.15f));
                foreach (var slot in slots) _detail.AddChild(SlotRow(slot));
            }

            // Links.
            var links = new HFlowContainer();
            links.AddThemeConstantOverride("h_separation", 8);
            if (entry.Guide != null) links.AddChild(Kit.Button(_tr("Setup guide ↗"), entry.Guide, () => ExternalLinks.OpenWeb(entry.Guide)));
            if (repo != null) links.AddChild(Kit.Button(_tr("Project ↗"), "https://github.com/" + repo, () => ExternalLinks.OpenWeb("https://github.com/" + repo)));
            if (links.GetChildCount() > 0) _detail.AddChild(links);
        }

        private string CheckSummary(EngineInstall install, string game) =>
            GameSweep.LastFor(install, game) is { } r ? _tr("Checked: {0}.").Replace("{0}", r.Summary) : "";

        private void Step(VBoxContainer steps, bool done, string title, string detail, List<Button> buttons)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var mark = new TextureRect
            {
                Texture = LucideTextures.Get(done ? "circle-check" : "circle", done ? ThemeColors.Success : ThemeColors.TextSubtle, 1.0f),
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
                CustomMinimumSize = new Vector2(24, 24),
                SizeFlagsVertical = SizeFlags.ShrinkBegin,
                TooltipText = done ? _tr("Done") : _tr("Not yet")
            };
            row.AddChild(mark);
            var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            text.AddChild(Kit.Text(title));
            text.AddChild(Wrapped(Kit.Muted(detail)));
            row.AddChild(text);
            foreach (var b in buttons)
            {
                b.SizeFlagsVertical = SizeFlags.ShrinkBegin;
                row.AddChild(b);
            }
            row.SetMeta("step_done", done);
            row.SetMeta("step_title", title);
            steps.AddChild(row);
        }

        /// <summary>The page's checklist: each step's title and whether it's done (for tests).</summary>
        public List<(string Title, bool Done)> Steps() =>
            _detail.FindChildren("*", nameof(HBoxContainer), true, false).Where(c => c.HasMeta("step_title"))
                .Select(c => (c.GetMeta("step_title").AsString(), c.GetMeta("step_done").AsBool())).ToList();

        private Control SlotRow(SlotTrackerControl slot)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            string state = slot.ApworldMatchesSeed switch
            {
                true => _tr("the apworld matches its seed"),
                false => _tr("its seed was made with another version of the apworld"),
                _ => slot.EngineProblem != null ? _tr("logic isn't running") : _tr("the apworld's version isn't known yet")
            };
            var label = Kit.Text($"{slot.SlotName}: {state}", slot.ApworldMatchesSeed == false ? ThemeColors.Warning : (Color?)null);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            row.AddChild(label);
            if (slot.ApworldMatchesSeed == false || slot.UsingSeedApworld)
                row.AddChild(Kit.Button(_tr("Choose the version…"), _tr("Lists the game's releases; pick the one the seed's host used, or let Atlas try them."), () => ApworldPickerDialog.Open(this, _hooks.Settings, slot, _tr)));
            if (slot.LinkedYamlSetting != null) row.AddChild(Kit.Muted(_tr("YAML: {0}").Replace("{0}", Path.GetFileName(slot.LinkedYamlSetting))));
            return row;
        }

        private Control YamlRow(string game, YamlEntry yaml)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(new Control { CustomMinimumSize = new Vector2(34, 0) });
            string others = string.Join(", ", yaml.Games.Where(g => !string.Equals(g, game, StringComparison.OrdinalIgnoreCase)));
            var label = Kit.Text(yaml.File + (yaml.Players.Count > 0 ? "  ·  " + string.Join(", ", yaml.Players) : "") + (others.Length > 0 ? "  ·  " + _tr("also {0}").Replace("{0}", others) : ""));
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.ClipText = true;
            row.AddChild(label);
            string path = _yamls!.PathOf(yaml);
            foreach (var slot in _hooks.LiveSlots().Where(s => string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase) && s.LinkedYamlSetting != path))
            {
                var target = slot;
                row.AddChild(Kit.Button(_tr("Link to {0}").Replace("{0}", slot.SlotName), _tr("Logic rebuilds this slot's world from this YAML."), () =>
                {
                    target.LinkYaml(path);
                    _hooks.Toast(_tr("Linked {0} to {1}.").Replace("{0}", yaml.File).Replace("{1}", target.SlotName), ThemeColors.TextSubtle);
                    ShowGame(game);
                }, small: true));
            }
            row.AddChild(Kit.Button(_tr("Remove"), _tr("Deletes Atlas's copy (not the file you added it from)."), () =>
                Dialogs.Confirm(this, _tr("Remove YAML"), _tr("Remove {0} from Atlas's YAML folder? The file you added it from isn't touched.").Replace("{0}", yaml.File), _tr("Remove"), () =>
                {
                    _yamls.Remove(yaml);
                    ShowGame(game);
                }), small: true));
            return row;
        }

        // ---- Operations ----

        /// <summary>Runs one operation at a time, with its last line in the status and everything in the log.</summary>
        private void Run<T>(string starting, Func<Action<string>, CancellationToken, Task<T>> work, Func<T, string> done)
        {
            if (_progress.Visible) return;
            var cts = _running = new CancellationTokenSource();
            _progress.Visible = true;
            _status.Text = starting;
            void Log(string line)
            {
                AP_Atlas.Core.Logger.LogInfo(line);
                Ui.Defer(this, () => { if (_progress.Visible) _status.Text = line.Trim(); });
            }
            Async.Fire(async () =>
            {
                string message;
                try
                {
                    var result = await Task.Run(() => work(Log, cts.Token));
                    message = done(result);
                }
                catch (OperationCanceledException) { message = _tr("Stopped."); }
                catch (Exception ex) { message = _tr("It didn't work: {0}").Replace("{0}", ex.Message); }
                Ui.Defer(this, () =>
                {
                    _progress.Visible = false;
                    _status.Text = message;
                    Refresh();
                });
            }, starting);
        }

        public override void _ExitTree() => _running?.Cancel();

        /// <summary>The newest release of the game's apworld from its project, after the user trusts the project (asked once).</summary>
        private void InstallNewest(string game)
        {
            PermissionDialog.Ask(this, _hooks.Settings, Permissions.GitHubLookups, null, _tr("The releases of the {0} apworld.").Replace("{0}", game), allowed =>
            {
                if (!allowed || !IsInstanceValid(this)) return;
                var install = AtlasEngine.Current;
                Run(_tr("Finding {0}'s releases…").Replace("{0}", game), async (log, ct) =>
                {
                    var repos = await ApworldSources.ReposForAsync(_hooks.Settings, install, game, log, ct);
                    var versions = await ApworldSources.VersionsAsync(game, repos.Select(r => r.Repo), null, ct);
                    var newest = versions.OrderByDescending(v => v.Version, ApworldChoices.VersionComparer.Instance).FirstOrDefault();
                    if (newest == null) return (Repo: (string?)null, Version: (ApworldVersion?)null);
                    return (Repo: ApworldSources.ParseRepo(newest.Url ?? "") ?? newest.Repo, Version: newest);
                }, found =>
                {
                    if (found.Version == null) return _tr("No release of {0} is known. Add the game's project, or choose its file.").Replace("{0}", game);
                    Ui.Defer(this, () => ConfirmInstall(game, found.Repo, found.Version));
                    return _tr("Newest: {0} {1}.").Replace("{0}", game).Replace("{1}", found.Version.Version);
                });
            });
        }

        private void ConfirmInstall(string game, string? repo, ApworldVersion version)
        {
            void Go() => Run(_tr("Installing {0} {1}…").Replace("{0}", game).Replace("{1}", version.Version), async (log, ct) =>
            {
                var install = AtlasEngine.Current;
                string file = await ApworldSources.DownloadAsync(game, ApworldSources.Find(game)?.Apworld, version, log, ct);
                return await AtlasEngine.InstallApworldAsync(install, file, log, ct);
            }, ok => ok ? _tr("{0} {1} is in the Atlas Engine; connected slots restart their logic on it.").Replace("{0}", game).Replace("{1}", version.Version)
                       : _tr("{0} {1} couldn't be installed: the log says why.").Replace("{0}", game).Replace("{1}", version.Version));
            if (repo == null || ApworldSources.IsRepoApproved(_hooks.Settings, repo))
            {
                Go();
                return;
            }
            Dialogs.Confirm(this, _tr("Trust this project?"),
                _tr("Install {0} {1} from github.com/{2}? Apworlds are programs that run inside the logic engine, so only continue if you trust this project. The file is checked against its published SHA-256 when there is one.")
                    .Replace("{0}", game).Replace("{1}", version.Version).Replace("{2}", repo),
                _tr("Trust and install"), () =>
                {
                    ApworldSources.ApproveRepo(_hooks.Settings, repo);
                    Go();
                }, _tr("github.com/{0} is a project I trust").Replace("{0}", repo));
        }

        private void InstallFile(string game)
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.apworld ; Archipelago world" },
                UseNativeDialog = true,
                Title = _tr("The {0} apworld").Replace("{0}", game)
            };
            dialog.FileSelected += path =>
            {
                dialog.QueueFree();
                Run(_tr("Installing {0}…").Replace("{0}", Path.GetFileName(path)), (log, ct) => AtlasEngine.InstallApworldAsync(AtlasEngine.Current, path, log, ct),
                    ok => ok ? _tr("{0} is in the Atlas Engine.").Replace("{0}", Path.GetFileName(path)) : _tr("It couldn't be installed: the log says why."));
            };
            dialog.Canceled += dialog.QueueFree;
            AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        private void AddYaml()
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.yaml, *.yml ; Archipelago player YAML" },
                UseNativeDialog = true,
                Title = _tr("A player YAML to keep in Atlas")
            };
            if (!string.IsNullOrWhiteSpace(_hooks.Settings.LastYamlFolder) && Directory.Exists(_hooks.Settings.LastYamlFolder)) dialog.CurrentDir = _hooks.Settings.LastYamlFolder;
            dialog.FileSelected += path =>
            {
                dialog.QueueFree();
                _hooks.Settings.LastYamlFolder = Path.GetDirectoryName(path) ?? "";
                AddYamlFile(path);
            };
            dialog.Canceled += dialog.QueueFree;
            AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        /// <summary>Adds a YAML to Atlas's YAML folder and shows the page again (also used by tests).</summary>
        public YamlEntry? AddYamlFile(string path)
        {
            _yamls ??= YamlLibrary.Load(DataManager.GetDataDirectory());
            var (entry, problem) = _yamls.Add(path);
            if (entry == null)
            {
                _hooks.Toast(_tr("That YAML can't be added: {0}.").Replace("{0}", problem ?? ""), ThemeColors.Error);
                return null;
            }
            _hooks.Toast(_tr("Added {0} ({1}).").Replace("{0}", entry.File).Replace("{1}", string.Join(", ", entry.Games)), ThemeColors.TextSubtle);
            Refresh();
            return entry;
        }

        /// <summary>Lists the project's newest release's other files; the user picks what to download into the game's folder.</summary>
        private void ListReleaseFiles(string game, string repo)
        {
            PermissionDialog.Ask(this, _hooks.Settings, Permissions.GitHubLookups, null, _tr("The files of github.com/{0}'s newest release.").Replace("{0}", repo), allowed =>
            {
                if (!allowed || !IsInstanceValid(this)) return;
                Run(_tr("Reading github.com/{0}'s newest release…").Replace("{0}", repo), (log, ct) => ApworldSources.ReleaseFilesAsync(repo, ct), result =>
                {
                    if (result.Problem != null) return _tr("GitHub couldn't be read just now: {0}").Replace("{0}", result.Problem);
                    if (result.Files.Count == 0) return _tr("The newest release has no files besides the apworld.");
                    Ui.Defer(this, () => PickReleaseFile(game, repo, result.Files));
                    return _tr("{0} file(s) in {1}.").Replace("{0}", result.Files.Count.ToString()).Replace("{1}", result.Files[0].Tag);
                });
            });
        }

        private void PickReleaseFile(string game, string repo, List<ApworldSources.ReleaseFile> files)
        {
            var dialog = new ConfirmationDialog { Title = _tr("Files of {0}").Replace("{0}", files[0].Tag), OkButtonText = _tr("Download"), DialogHideOnOk = false };
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
            box.AddThemeConstantOverride("separation", 6);
            box.AddChild(Wrapped(Kit.Text(_tr("Pick the files the game's setup guide asks for. Atlas keeps them in the game's folder and never runs them."))));
            var list = new ItemList { CustomMinimumSize = new Vector2(500, 220), SelectMode = ItemList.SelectModeEnum.Multi };
            foreach (var f in files) list.AddItem($"{f.Name}  ({Math.Max(1, f.Size / 1024)} KB{(f.Sha256 != null ? "" : ", " + _tr("no published hash"))})");
            box.AddChild(list);
            dialog.AddChild(box);
            dialog.Confirmed += () =>
            {
                var chosen = list.GetSelectedItems().Select(i => files[i]).ToList();
                dialog.QueueFree();
                if (chosen.Count == 0) return;
                void Go() => Run(_tr("Downloading {0} file(s)…").Replace("{0}", chosen.Count.ToString()), async (log, ct) =>
                {
                    string folder = GameFiles.ToolsFolder(DataManager.GetDataDirectory(), game);
                    Directory.CreateDirectory(folder);
                    var record = SafeFile.ReadJson(Path.Combine(folder, "tools.json"), () => new List<ToolRecord>()) ?? new List<ToolRecord>();
                    foreach (var f in chosen)
                    {
                        string target = Path.Combine(folder, GameFiles.SafeName(f.Name));
                        log($"Downloading {f.Name} from github.com/{repo} ({f.Tag})" + (f.Sha256 != null ? ", checked against its published SHA-256…" : "…"));
                        string sha = await EngineDownloader.DownloadAsync(f.Url, target, f.Sha256!, null!, ct);
                        record.RemoveAll(r => string.Equals(r.File, Path.GetFileName(target), StringComparison.OrdinalIgnoreCase));
                        record.Add(new ToolRecord { File = Path.GetFileName(target), Source = "github.com/" + repo, Version = f.Tag, Sha256 = sha });
                    }
                    SafeFile.WriteJson(Path.Combine(folder, "tools.json"), record);
                    return chosen.Count;
                }, n => _tr("{0} file(s) in the game's folder. Atlas doesn't run them: the setup guide says what to do.").Replace("{0}", n.ToString()));
                if (ApworldSources.IsRepoApproved(_hooks.Settings, repo))
                {
                    Go();
                    return;
                }
                Dialogs.Confirm(this, _tr("Trust this project?"),
                    _tr("Download from github.com/{0}? Atlas never runs these files, but you may: only continue if you trust this project.").Replace("{0}", repo),
                    _tr("Trust and download"), () =>
                    {
                        ApworldSources.ApproveRepo(_hooks.Settings, repo);
                        Go();
                    }, _tr("github.com/{0} is a project I trust").Replace("{0}", repo));
            };
            dialog.Canceled += dialog.QueueFree;
            AddChild(dialog);
            dialog.PopupCentered();
        }

        /// <summary>A file Atlas downloaded into a game's tools folder: where from, which release, its SHA-256.</summary>
        public sealed class ToolRecord
        {
            public string File { get; set; } = "";
            public string Source { get; set; } = "";
            public string Version { get; set; } = "";
            public string Sha256 { get; set; } = "";
        }

        private void AddProject()
        {
            Dialogs.Prompt(this, _tr("Add a game's project"),
                _tr("Paste the GitHub link of a project that publishes a game's .apworld (its repository or releases page). Atlas reads its release list, finds the game, and trusts the project for it."),
                "", "https://github.com/owner/project/releases", _tr("Add"), link =>
                {
                    PermissionDialog.Ask(this, _hooks.Settings, Permissions.GitHubLookups, null, link, allowed =>
                    {
                        if (allowed) Async.Fire(AddProjectAsync(link), "adding a game's project");
                    });
                });
        }

        private async Task AddProjectAsync(string link)
        {
            _status.Text = _tr("Checking that project's releases…");
            var (repo, problem) = await Task.Run(() => ApworldSources.CheckUserRepoAsync(link, CancellationToken.None));
            if (!IsInstanceValid(this)) return;
            if (repo == null)
            {
                _status.Text = _tr("Couldn't add it: {0}.").Replace("{0}", problem);
                return;
            }
            // The game is named by the project's apworld once installed; until then the project's name stands for it.
            string game = _selected != null && _games.Any(g => g.Game == _selected && g.Repo == null && g.Section != Section.Official) ? _selected : repo.Split('/').Last();
            ApworldSources.AddUserRepo(_hooks.Settings, game, repo);
            _status.Text = _tr("Added github.com/{0} for {1}.").Replace("{0}", repo).Replace("{1}", game);
            _selected = game;
            Refresh();
        }

        // ---- Helpers ----

        /// <summary>The installed packs Atlas has already read (the Map Packs page reads every pack; nothing is opened here).</summary>
        private static List<LoadedPack> ReadPacks()
        {
            var packs = new List<LoadedPack>();
            string dir = PopTrackerPackLoader.GetPacksDirectory();
            if (!Directory.Exists(dir)) return packs;
            foreach (var zip in Directory.GetFiles(dir, "*.zip"))
                if (PopTrackerPackLoader.IsPackCached(zip) && PopTrackerPackLoader.InspectZipPack(zip) is { Manifest: not null } pack) packs.Add(pack);
            return packs;
        }

        private List<LoadedPack> PacksFor(string game) =>
            _packs.Where(p => PopTrackerPackLoader.IsGameNameMatch(p.Manifest.GameName, game) || PopTrackerPackLoader.IsGameNameMatch(p.Manifest.Name, game)).ToList();

        private void OpenFolder(string folder)
        {
            Directory.CreateDirectory(folder);
            if (!ExternalLinks.OpenFolder(folder)) _hooks.Toast(_tr("Couldn't open the folder."), ThemeColors.Error);
        }

        private static Label Wrapped(Label label)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            return label;
        }

        private static void Clear(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                node.RemoveChild(child);
                child.QueueFree();
            }
        }
    }
}
