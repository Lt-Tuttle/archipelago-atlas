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
        /// <summary>Shows the Map Packs page and searches GitHub for a game's packs (one search, when pressed).</summary>
        public required Action<string> FindPack { get; init; }
    }

    /// <summary>
    /// The Games page: every game Atlas knows, in three groups (Official: they ship with Archipelago; Community: from the
    /// community index; Added by you: projects you added and apworlds you installed that the index doesn't know), found by
    /// typed words and sorted by name, by readiness or by when you last played. A game's page walks its setup through in
    /// one place: the apworld in the engine (every version every known project publishes, the installed one marked, a
    /// recommendation per connected slot), a map pack (installed, or found on GitHub from here), a YAML (kept in Atlas's
    /// YAML folder, a multi-game one under each of its games), the loose files some games need (downloaded from a release
    /// of your choice into the game's own folder; Atlas never runs them), your slots that play it (with the version
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

        /// <summary>A game the page lists: its group, whether the engine has it, its project and guide, and whether a slot of the user's plays it (listed first).</summary>
        public sealed record GameEntry(string Game, Section Section, bool InEngine, string? Repo, string? Guide, bool Played = false);

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
        // What GitHub said this session, per game: the projects and every version they publish (nothing is asked twice).
        private readonly Dictionary<string, List<ApworldSources.ApworldRepo>> _projects = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ApworldVersion>> _versions = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _versionsAsked = new(StringComparer.OrdinalIgnoreCase);
        private AtlasTable? _versionsTable;
        private string _versionsLine = "";

        /// <summary>The explorer's content: the search, the sort and the list.</summary>
        public Control SidebarContent => _explorer;

        /// <summary>Every game the page lists, by section (for tests).</summary>
        public IReadOnlyList<GameEntry> Games => _games;

        /// <summary>The games the list shows now (after the search), in order (for tests).</summary>
        public List<string> ShownGames { get; private set; } = new();

        /// <summary>The list's group headers as shown, in order (for tests).</summary>
        public List<string> ShownSections { get; private set; } = new();

        /// <summary>The game whose page shows (null: the overview).</summary>
        public string? SelectedGame => _selected;

        /// <summary>The status line (for tests).</summary>
        public string StatusText => _status.Text;

        /// <summary>The line above the versions table ("Newest known: …"; for tests).</summary>
        public string VersionsLine => _versionsLine;

        /// <summary>The versions table's rows as shown: version, project, whether it's a pre-release (for tests).</summary>
        public List<(string Version, string Project, bool Prerelease)> VersionRows() =>
            _versionsTable == null || !IsInstanceValid(_versionsTable) ? new List<(string, string, bool)>()
            : _versionsTable.ShownRows.Select(r => r.Tag as ApworldChoice).Where(c => c != null).Select(c => (c!.Version, c.Source, c.Prerelease)).ToList();

        /// <summary>What the page says about each connected slot of the game (for tests).</summary>
        public List<string> SlotLines() =>
            _detail.FindChildren("*", nameof(Label), true, false).OfType<Label>().Where(l => l.HasMeta("slot_line")).Select(l => l.Text).ToList();

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

        /// <summary>Shows a game's page (and selects it in the list), as a click on it does, with the lists read again.</summary>
        public void Select(string game)
        {
            _selected = game;
            Refresh();
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
            // Games the user's multiworlds play are listed even before the engine knows them, and first (Played).
            var played = PlayedGames();
            foreach (string game in played.Keys)
                if (!games.ContainsKey(game)) games[game] = new GameEntry(game, Section.Yours, inEngine.Contains(game), null, null);
            return games.Values.Select(g => g with { Played = played.ContainsKey(g.Game) }).ToList();
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
            // A slot's linked YAML names its game before the slot ever connects.
            foreach (var profile in _hooks.Profiles())
                foreach (string slot in profile.Slots)
                {
                    if (!_hooks.Settings.SlotYamlPaths.TryGetValue(AP_Atlas.Core.Annotations.SlotKey(profile.Id, slot), out var path) || !File.Exists(path)) continue;
                    try
                    {
                        foreach (string game in AP_Atlas.Core.YamlExclusions.GamesFor(File.ReadAllText(path), slot)) played.TryAdd(game, DateTime.MinValue);
                    }
                    catch (Exception ex) { AP_Atlas.Core.Logger.LogDebug($"Couldn't read the YAML linked to {slot}: {ex.Message}"); }
                }
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
            ShownSections = new List<string>();
            // The games of the user's own slots come first, each listed once; the rest in their groups.
            var groups = new List<(string Title, List<GameEntry> Games)> { (_tr("In your multiworlds"), list.Where(g => g.Played).ToList()) };
            foreach (var section in new[] { Section.Official, Section.Community, Section.Yours })
                groups.Add((SectionTitle(section), list.Where(g => g.Section == section && !g.Played).ToList()));
            foreach (var (title, inSection) in groups)
            {
                if (inSection.Count == 0) continue;
                var header = _list.CreateItem(root);
                header.SetText(0, title + $" ({inSection.Count})");
                ShownSections.Add(header.GetText(0));
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
            _versionsTable = null;
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

            // The community index Atlas ships, and a newer copy from a URL of the user's choice (advanced).
            _detail.AddChild(Kit.Heading(_tr("The community index"), 1.15f));
            var list = ApworldSources.List;
            _detail.AddChild(Wrapped(Kit.Muted(_tr("Atlas ships a list of {0} community games with their projects (built {1}), and reads each project's GitHub releases when you allow it. A newer list can be read from a URL, such as a maintained fork of the index.")
                .Replace("{0}", list.Games.Count.ToString()).Replace("{1}", list.Built ?? "?"))));
            var sources = new HBoxContainer();
            sources.AddThemeConstantOverride("separation", 8);
            var url = new LineEdit { PlaceholderText = _tr("Newer list URL (optional)"), Text = _hooks.Settings.ApworldSourcesUrl ?? "", SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = _tr("Newer list URL") };
            sources.AddChild(url);
            sources.AddChild(Kit.Button(_tr("Refresh the list"), _tr("Reads the list at that URL; it's used only if it's a valid Atlas apworld source list."), () =>
            {
                _hooks.Settings.ApworldSourcesUrl = url.Text.Trim();
                DataManager.SaveSettings(_hooks.Settings);
                Run(_tr("Refreshing the apworld source list…"), (log, ct) => ApworldSources.RefreshAsync(_hooks.Settings.ApworldSourcesUrl, ct), message => message);
            }));
            _detail.AddChild(sources);
        }

        // ---- A game's page ----

        private void ShowGame(string game)
        {
            _selected = game;
            _versionsTable = null;
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

            // 1. The apworld: what's installed, and every version every known project publishes.
            var installed = engineReady ? ApworldSources.InstalledCopies(install!, game) : new List<(string File, string Sha256)>();
            string? version = installed.Select(c => ApworldSources.KnownSourceOf(c.Sha256).Tag).FirstOrDefault(t => t != null);
            var choices = Choices(game, installed);
            var newest = choices.FirstOrDefault(c => !c.Prerelease && c.Url != null);
            bool anyProject = entry.Repo != null || ApworldSources.Find(game)?.Repo != null || _projects.TryGetValue(game, out var known) && known.Count > 0;
            var apworldButtons = new List<Button>();
            if (!engineReady) apworldButtons.Add(Kit.Button(_tr("Set up the Atlas Engine…"), null, _hooks.OpenEngineSetup));
            else
            {
                if (entry.Section != Section.Official && (newest != null || anyProject))
                    apworldButtons.Add(Kit.Button(entry.InEngine ? _tr("Update…") : _tr("Install…"),
                        _tr("Installs the newest full release from the game's projects in the Atlas Engine (you're asked to trust a project once; the file is checked against its published SHA-256)."),
                        () => InstallNewest(game)));
                if (entry.Section != Section.Official)
                    apworldButtons.Add(Kit.Button(_tr("Choose a file…"), _tr("Install an .apworld file you have."), () => InstallFile(game)));
                if (entry.Section != Section.Official && install!.Mode == EngineMode.Portable && InstallFolderKnown)
                    apworldButtons.Add(Kit.Button(_tr("Copy from my install…"), _tr("Looks in the Archipelago install you chose (its custom_worlds and lib/worlds folders, nothing else) for this game's apworld and copies it into the Atlas Engine."), () => CopyFromInstall(game)));
                apworldButtons.Add(Kit.Button(_tr("Check this game"), _tr("Rebuilds the game with default options and computes its starting logic."), () =>
                    Run(_tr("Checking {0}…").Replace("{0}", game), (log, ct) => GameSweep.RunAsync(install!, log, ct, new[] { game }), _ => CheckSummary(install!, game))));
            }
            Step(steps, entry.InEngine, _tr("The apworld in the Atlas Engine"),
                !engineReady ? _tr("The Atlas Engine isn't set up yet.")
                : entry.InEngine ? _tr("In the engine") + (version != null ? " (" + version + ")" : "") + "." + (CheckSummary(install!, game) is { Length: > 0 } s ? " " + s : "")
                : check == null ? _tr("Atlas hasn't checked the engine yet: the engine window's health check says which games it has.")
                : _tr("Not in the engine yet."), apworldButtons);
            if (entry.Section != Section.Official) steps.AddChild(VersionsSection(game, choices));

            // 2. A map pack.
            var packs = PacksFor(game);
            Step(steps, packs.Count > 0, _tr("A map pack"),
                packs.Count > 0 ? _tr("Installed: {0}.").Replace("{0}", string.Join(", ", packs.Select(p => p.Manifest?.Name ?? Path.GetFileNameWithoutExtension(p.SourcePath))))
                    : _tr("None installed: the Map Tracker shows the game's map with one (a PopTracker pack)."),
                new List<Button>
                {
                    Kit.Button(_tr("Search GitHub for a pack…"), _tr("One search of GitHub for this game's PopTracker packs, when you press; you choose what to install."), () => _hooks.FindPack(game)),
                    Kit.Button(_tr("Map Packs"), _tr("Install a pack from its project, or a .zip you have; the Pack Doctor looks it over."), () => _hooks.ShowTool(Tool.MapPacks))
                });

            // 3. A YAML.
            var yamls = _yamls?.For(game) ?? new List<YamlEntry>();
            var linked = _hooks.LiveSlots().Where(s => s.Game == game && s.LinkedYamlSetting != null).ToList();
            Step(steps, yamls.Count > 0 || linked.Count > 0, _tr("Your YAML"),
                yamls.Count > 0 ? _tr("{0} in Atlas's YAML folder.").Replace("{0}", yamls.Count.ToString())
                    : _tr("Most games rebuild your world from the server's data; a few need your YAML. Keep yours here to link it to a slot in one click."),
                new List<Button>
                {
                    Kit.Button(_tr("Add YAML…"), _tr("Copies a player YAML into Atlas's YAML folder (a multi-game YAML is listed under each of its games)."), () => AddYaml(null)),
                    YamlPlacesMenu(game)
                });
            foreach (var yaml in yamls) _detail.AddChild(YamlRow(game, yaml));

            // 4. Files the game's setup needs (optional), each with the release it came from.
            string tools = GameFiles.ToolsFolder(DataManager.GetDataDirectory(), game);
            var records = Directory.Exists(tools) ? SafeFile.ReadJson(Path.Combine(tools, "tools.json"), () => new List<ToolRecord>()) ?? new List<ToolRecord>() : new List<ToolRecord>();
            int toolCount = Directory.Exists(tools) ? Directory.GetFiles(tools).Count(f => !f.EndsWith("tools.json", StringComparison.OrdinalIgnoreCase)) : 0;
            var toolButtons = new List<Button>();
            if (entry.Section != Section.Official && (newest != null || anyProject))
                toolButtons.Add(Kit.Button(_tr("Get from a release…"), _tr("Pick one of the game's releases (the slot's version first), then the files to download besides the apworld."), () => ListReleaseFiles(game)));
            toolButtons.Add(Kit.Button(_tr("Open folder"), _tr("The game's own folder in Atlas: its tools and the apworld releases Atlas downloaded."), () => OpenFolder(GameFiles.GameFolder(DataManager.GetDataDirectory(), game))));
            Step(steps, toolCount > 0, _tr("Files its setup needs (only if its guide asks)"),
                (toolCount > 0 ? _tr("{0} file(s) in the game's folder.").Replace("{0}", toolCount.ToString()) + " " : "") +
                _tr("Some games need a client, a patcher or a .bat from their release. Atlas keeps them in the game's folder and never runs them: the setup guide says what to do with each."),
                toolButtons);
            foreach (var record in records.Where(r => File.Exists(Path.Combine(tools, r.File))))
            {
                var toolRow = new HBoxContainer();
                toolRow.AddChild(new Control { CustomMinimumSize = new Vector2(34, 0) });
                var toolLabel = Kit.Muted(record.File + "  ·  " + _tr("from {0} ({1})").Replace("{0}", record.Version).Replace("{1}", record.Source));
                toolLabel.ClipText = true;
                toolLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                toolRow.AddChild(toolLabel);
                string toolPath = Path.Combine(tools, record.File);
                if (IsYamlFile(toolPath))
                    toolRow.AddChild(Kit.Button(_tr("Add as YAML"), _tr("Keeps this file in Atlas's YAML folder as a player YAML, listed under its games."), () => AddYamlFile(toolPath), small: true));
                _detail.AddChild(toolRow);
            }

            // Your slots that play it, each with the version it should run.
            var slots = _hooks.LiveSlots().Where(s => string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase)).ToList();
            if (slots.Count > 0)
            {
                _detail.AddChild(Kit.Heading(_tr("Your slots"), 1.15f));
                foreach (var slot in slots) _detail.AddChild(SlotRow(slot));
            }

            // Links: the button says where the link leads (most community games' "home" is their thread in the Archipelago Discord).
            var links = new HFlowContainer();
            links.AddThemeConstantOverride("h_separation", 8);
            if (entry.Guide != null)
            {
                var (text, tip) = LinkButtonText(entry.Guide);
                var link = Kit.Button(text, tip, () => ExternalLinks.OpenWeb(entry.Guide));
                link.SetMeta("game_link", true);
                links.AddChild(link);
            }
            string? repo = entry.Repo ?? ApworldSources.Find(game)?.Repo;
            if (repo != null) links.AddChild(Kit.Button(_tr("Project ↗"), "https://github.com/" + repo, () => ExternalLinks.OpenWeb("https://github.com/" + repo)));
            if (links.GetChildCount() > 0) _detail.AddChild(links);

            // Once GitHub may be asked, the projects' releases are read by themselves (once per game per session), so a
            // fork a seed's host used shows before anyone asks.
            if (entry.Section != Section.Official && anyProject && !_versions.ContainsKey(game) && !_versionsAsked.Contains(game) && Permissions.IsAllowed(_hooks.Settings, Permissions.GitHubLookups))
                LoadVersions(game, again: false);
        }

        /// <summary>What a game's link button says, by where the link leads, and the tooltip that explains it.</summary>
        private (string Text, string Tip) LinkButtonText(string url) => Links.KindOf(url) switch
        {
            LinkKind.Discord => (_tr("Discord thread ↗"),
                _tr("Opens the game's thread in the Archipelago Discord in your browser. Join the Archipelago Discord first (discord.gg/archipelago). If the thread doesn't open, the game's channel is hidden on your side: in the server, open Channels & Roles (or Browse Channels), tick the game's channel, then come back and press this again.") + "\n" + url),
            LinkKind.GitHub or LinkKind.GitLab => (_tr("Project page ↗"), url),
            _ => (_tr("Setup guide ↗"), url)
        };

        /// <summary>The link buttons on the game's page, by their text (for tests).</summary>
        public List<string> LinkTexts() =>
            _detail.FindChildren("*", nameof(Button), true, false).OfType<Button>().Where(b => b.HasMeta("game_link")).Select(b => b.Text).ToList();

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

        // ---- Versions: every project, every release ----

        /// <summary>The projects known for a game: what GitHub said this session, else the index's and the user's (nothing asked).</summary>
        private List<(string Repo, string Reason)> ProjectsKnown(string game)
        {
            if (_projects.TryGetValue(game, out var asked)) return asked.Select(p => (p.Repo, p.Reason)).ToList();
            var list = new List<(string, string)>();
            if (ApworldSources.Find(game)?.Repo is { } indexed) list.Add((indexed, _tr("listed in the community index")));
            if (_hooks.Settings.ExtraApworldRepos != null && _hooks.Settings.ExtraApworldRepos.TryGetValue(game, out var added))
                foreach (var r in added.Where(r => !list.Any(l => l.Item1.Equals(r, StringComparison.OrdinalIgnoreCase)))) list.Add((r, _tr("added by you")));
            return list;
        }

        /// <summary>
        /// Every version the page can list, newest first: what GitHub said this session, else the community index's
        /// versions (known offline), plus the files Atlas has identified; the installed one marked.
        /// </summary>
        private List<ApworldChoice> Choices(string game, List<(string File, string Sha256)> installed)
        {
            IEnumerable<PublishedApworld> published;
            if (_versions.TryGetValue(game, out var versions))
                published = versions.Select(v => new PublishedApworld(v.Version, "github.com/" + (v.Repo ?? ApworldSources.SourceKey(v.Url).Replace("github.com/", "")), v.Url, v.Sha256, v.Prerelease, v.Published));
            else
            {
                var source = ApworldSources.Find(game);
                published = source?.Versions.Select(v => new PublishedApworld(ApworldChoices.NormalizeLabel(v.Version), source.Repo != null ? "github.com/" + source.Repo : ApworldSources.SourceKey(v.Url), v.Url, v.Sha256))
                            ?? Enumerable.Empty<PublishedApworld>();
            }
            return ApworldChoices.Compose(published, ApworldSources.IdentifiedFor(game), installed.Select(c => c.Sha256), null);
        }

        private ApworldVersion? VersionOf(string game, ApworldChoice choice)
        {
            if (choice.Url == null) return null;
            if (_versions.TryGetValue(game, out var versions) && versions.FirstOrDefault(v => v.Url == choice.Url) is { } known) return known;
            return new ApworldVersion { Version = choice.Version, Url = choice.Url, Sha256 = choice.Sha256, Repo = RepoOf(choice), Prerelease = choice.Prerelease };
        }

        private static string? RepoOf(ApworldChoice choice) =>
            ApworldSources.ParseRepo(choice.Url ?? "") ?? (choice.Source.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase) ? choice.Source.Substring("github.com/".Length) : null);

        private Control VersionsSection(string game, List<ApworldChoice> choices)
        {
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 6);
            var indent = new MarginContainer();
            indent.AddThemeConstantOverride("margin_left", 34);
            indent.AddChild(box);
            bool loaded = _versions.ContainsKey(game);
            bool allowed = Permissions.IsAllowed(_hooks.Settings, Permissions.GitHubLookups);
            var projects = ProjectsKnown(game);
            var newest = choices.FirstOrDefault(c => !c.Prerelease && c.Url != null);
            string line = newest != null ? _tr("Newest known: {0} from {1}.").Replace("{0}", newest.Version).Replace("{1}", newest.Source)
                : projects.Count == 0 ? _tr("No project is known for this game yet: add the one its apworld comes from.")
                : _tr("No release of this game is known yet.");
            if (!loaded) line += " " + (_versionsAsked.Contains(game) ? _tr("Reading the projects' releases…")
                : allowed ? "" : _tr("\"Look up releases on GitHub\" lists every release of every known project, and other projects of the same name."));
            else line += " " + _tr("{0} version(s) from {1} project(s): {2}.").Replace("{0}", choices.Count(c => c.Url != null).ToString()).Replace("{1}", projects.Count.ToString())
                .Replace("{2}", string.Join("; ", projects.Select(p => "github.com/" + p.Repo + " (" + p.Reason + ")")));
            _versionsLine = line;
            var lineLabel = Wrapped(Kit.Muted(line));
            lineLabel.SetMeta("versions_line", true);
            box.AddChild(lineLabel);

            Button? installSelected = null;
            if (choices.Count > 0)
            {
                var table = new AtlasTable("game-versions", _hooks.Settings, _tr) { Stripes = true, SizeFlagsVertical = SizeFlags.ShrinkBegin };
                table.Toolbar.Visible = false;
                // Room for the titles and up to five rows; more scroll inside the table (the page scrolls around it).
                table.Tree.CustomMinimumSize = new Vector2(0, 50 + 36 * Math.Min(5, choices.Count));
                table.SetColumns(new[]
                {
                    new AtlasTable.Column { Id = "version", Title = "Version", MinWidth = 130, Ratio = 0 },
                    new AtlasTable.Column { Id = "project", Title = "Project", MinWidth = 160, Ratio = 3 },
                    new AtlasTable.Column { Id = "have", Title = "Atlas has it", MinWidth = 120, Ratio = 2 },
                    new AtlasTable.Column { Id = "released", Title = "Released", MinWidth = 96, Ratio = 0 }
                });
                var rows = new List<AtlasTable.Row>();
                for (int i = 0; i < choices.Count; i++)
                {
                    var c = choices[i];
                    Color? background = c.Installed ? ThemeColors.AccentTint : null;
                    string have = c.Installed ? _tr("In the engine") : c.Downloaded ? _tr("Downloaded") : "";
                    rows.Add(new AtlasTable.Row
                    {
                        Key = c.Url ?? c.LocalFile ?? c.Version + "|" + c.Source,
                        Tag = c,
                        SearchText = c.Version + " " + c.Source,
                        Cells = new[]
                        {
                            new AtlasTable.Cell(c.Version + (c.Prerelease ? " " + _tr("(pre-release)") : ""), c.Prerelease ? ThemeColors.TextMuted : null,
                                c.Prerelease ? _tr("The project marked this release a pre-release.") : null, choices.Count - i) { Background = background },
                            new AtlasTable.Cell(c.Source) { Background = background },
                            new AtlasTable.Cell(have, c.Installed ? ThemeColors.Success : null) { Background = background },
                            new AtlasTable.Cell(c.Published is { } when ? when.ToString("yyyy-MM-dd") : "", null, null, c.Published ?? DateTime.MinValue) { Background = background }
                        }
                    });
                }
                table.SetRows(rows);
                table.SelectionChanged += row => { if (installSelected != null) installSelected.Disabled = row?.Tag is not ApworldChoice { Url: not null } and not ApworldChoice { LocalFile: not null }; };
                box.AddChild(table);
                _versionsTable = table;
            }

            var buttons = new HFlowContainer();
            buttons.AddThemeConstantOverride("h_separation", 8);
            if (choices.Count > 0 && AtlasEngine.Current is { CanLaunch: true })
            {
                installSelected = Kit.Button(_tr("Install the selected version"), _tr("Installs the version selected in the table in the Atlas Engine (a pre-release too, if that's what the seed's host used)."), () =>
                {
                    if (_versionsTable?.Selected?.Tag is not ApworldChoice choice) return;
                    if (VersionOf(game, choice) is { } v) ConfirmInstall(game, RepoOf(choice), v);
                    else if (choice.LocalFile != null)
                        Run(_tr("Installing {0}…").Replace("{0}", Path.GetFileName(choice.LocalFile)), (log, ct) => AtlasEngine.InstallApworldAsync(AtlasEngine.Current, choice.LocalFile, log, ct),
                            ok => ok ? _tr("{0} is in the Atlas Engine.").Replace("{0}", Path.GetFileName(choice.LocalFile)) : _tr("It couldn't be installed: the log says why."));
                });
                installSelected.Disabled = true;
                buttons.AddChild(installSelected);
            }
            if (!loaded && !allowed && projects.Count > 0)
                buttons.AddChild(Kit.Button(_tr("Look up releases on GitHub"), _tr("Reads the projects' release lists and looks for projects of the same name (nothing is downloaded)."), () => AskThenLoad(game, again: false)));
            else if (projects.Count > 0)
                buttons.AddChild(Kit.Button(_tr("Find other projects…"), _tr("Searches GitHub again for projects named like this game's (forks and re-uploads a seed's host may have used)."), () => AskThenLoad(game, again: true)));
            buttons.AddChild(Kit.Button(_tr("Add a project…"), _tr("Paste the GitHub link of a project that publishes this game's apworld."), AddProject));
            box.AddChild(buttons);
            return indent;
        }

        private void AskThenLoad(string game, bool again, Action? then = null)
        {
            PermissionDialog.Ask(this, _hooks.Settings, Permissions.GitHubLookups, null, _tr("The releases of the {0} apworld, from every project that publishes it.").Replace("{0}", game), allowed =>
            {
                if (!allowed || !IsInstanceValid(this)) return;
                if (!LoadVersions(game, again, then)) _hooks.Toast(_tr("Wait for the current task to finish first."), ThemeColors.TextSubtle);
            });
        }

        /// <summary>
        /// Reads every known project's releases for a game (and, once per game per session, looks for projects of the same
        /// name), then shows the page again with them. False when another task is running.
        /// </summary>
        private bool LoadVersions(string game, bool again, Action? then = null)
        {
            var install = AtlasEngine.Current;
            bool started = Run(_tr("Finding {0}'s releases…").Replace("{0}", game), async (log, ct) =>
            {
                var projects = await ApworldSources.ProjectsForAsync(_hooks.Settings, install, game, log, ct, again);
                string? apworldName = ApworldSources.Find(game)?.Apworld ?? ApworldSources.InstalledCopies(install, game).Select(c => Path.GetFileNameWithoutExtension(c.File)).FirstOrDefault();
                var problems = new List<string>();
                var versions = await ApworldSources.VersionsAsync(game, projects.Select(p => p.Repo), apworldName, ct, problems);
                return (Projects: projects, Versions: versions, Problems: problems);
            }, result =>
            {
                Ui.Defer(this, () =>
                {
                    _projects[game] = result.Projects;
                    _versions[game] = result.Versions;
                    then?.Invoke();
                });
                return result.Problems.Count > 0 && result.Versions.Count == 0 ? _tr("GitHub couldn't be read just now: {0}").Replace("{0}", result.Problems[0])
                    : _tr("{0}: {1} version(s) from {2} project(s).").Replace("{0}", game).Replace("{1}", result.Versions.Count.ToString()).Replace("{2}", result.Projects.Count.ToString());
            });
            if (started) _versionsAsked.Add(game);
            return started;
        }

        private Control SlotRow(SlotTrackerControl slot)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            string game = slot.Game;
            string? checksum = slot.ServerChecksumFor(game);
            var chosen = slot.ChosenApworld;
            var cached = checksum != null ? ApworldSources.CachedFor(game, checksum) : null;
            string state;
            Button? action = null;
            if (slot.ApworldMatchesSeed == true)
                state = chosen != null ? _tr("runs {0} ({1}), the version its seed was made with").Replace("{0}", chosen.Version).Replace("{1}", chosen.Source)
                    : slot.UsingSeedApworld ? _tr("runs the version its seed was made with (not the installed one)") : _tr("the installed apworld matches its seed");
            else if (slot.ApworldMatchesSeed == false)
            {
                if (chosen != null)
                    state = _tr("runs {0} ({1}) for this seed, though its data differs from the seed's").Replace("{0}", chosen.Version).Replace("{1}", chosen.Source);
                else if (cached != null)
                {
                    state = _tr("its seed was made with {0} ({1}), which Atlas has: use it").Replace("{0}", cached.Version ?? "?").Replace("{1}", cached.Source ?? "");
                    action = Kit.Button(_tr("Use it"), _tr("Restarts this slot's logic on the version its seed was made with."), () => slot.UseApworld(cached.File, cached.Version ?? "?", cached.Source ?? "", matchesSeed: true));
                }
                else
                    state = _tr("its seed's data ({0}) isn't among the known versions: pick the host's version, or let Atlas try them").Replace("{0}", Short(checksum));
            }
            else state = slot.EngineProblem != null ? _tr("logic isn't running") : _tr("the apworld's version isn't known yet");
            var label = Kit.Text($"{slot.SlotName}: {state}", slot.ApworldMatchesSeed == false ? ThemeColors.Warning : (Color?)null);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SetMeta("slot_line", true);
            row.AddChild(label);
            if (action != null) row.AddChild(action);
            if (slot.ApworldMatchesSeed == false || slot.UsingSeedApworld)
                row.AddChild(Kit.Button(_tr("Choose the version…"), _tr("Lists the game's releases; pick the one the seed's host used, or let Atlas try them."), () => ApworldPickerDialog.Open(this, _hooks.Settings, slot, _tr)));
            if (slot.LinkedYamlSetting != null) row.AddChild(Kit.Muted(_tr("YAML: {0}").Replace("{0}", Path.GetFileName(slot.LinkedYamlSetting))));
            return row;
        }

        private static string Short(string? checksum) => string.IsNullOrEmpty(checksum) ? "?" : checksum.Substring(0, Math.Min(8, checksum.Length));

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

        /// <summary>Runs one operation at a time, with its last line in the status and everything in the log. False when one is running.</summary>
        private bool Run<T>(string starting, Func<Action<string>, CancellationToken, Task<T>> work, Func<T, string> done)
        {
            if (_progress.Visible) return false;
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
            return true;
        }

        public override void _ExitTree() => _running?.Cancel();

        /// <summary>The newest full release of the game's apworld across its projects, after the user trusts the project (asked once).</summary>
        private void InstallNewest(string game)
        {
            void Offer()
            {
                var install = AtlasEngine.Current;
                var installed = install != null ? ApworldSources.InstalledCopies(install, game) : new List<(string File, string Sha256)>();
                var newest = Choices(game, installed).FirstOrDefault(c => !c.Prerelease && c.Url != null);
                if (newest == null || VersionOf(game, newest) is not { } version)
                {
                    _status.Text = _tr("No release of {0} is known. Add the game's project, or choose its file.").Replace("{0}", game);
                    return;
                }
                ConfirmInstall(game, RepoOf(newest), version);
            }
            if (_versions.ContainsKey(game)) Offer();
            else AskThenLoad(game, again: false, Offer);
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

        /// <summary>The Archipelago install the user chose, when it still looks like one (Atlas never looks for one by itself).</summary>
        private bool InstallFolderKnown =>
            !string.IsNullOrWhiteSpace(_hooks.Settings.ArchipelagoInstallationPath) && File.Exists(Path.Combine(_hooks.Settings.ArchipelagoInstallationPath, "ArchipelagoLauncher.exe"));

        /// <summary>Copies the game's apworld from the chosen Archipelago install's world folders (those two folders, nothing else) into the engine.</summary>
        private void CopyFromInstall(string game)
        {
            string root = _hooks.Settings.ArchipelagoInstallationPath;
            Run(_tr("Looking for {0} in your Archipelago install…").Replace("{0}", game), async (log, ct) =>
            {
                string? found = null;
                foreach (var dir in new[] { Path.Combine(root, "custom_worlds"), Path.Combine(root, "lib", "worlds") })
                {
                    if (found != null || !Directory.Exists(dir)) continue;
                    foreach (var file in Directory.GetFiles(dir, "*.apworld"))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (string.Equals(AtlasEngine.GameOfApworld(file), game, StringComparison.OrdinalIgnoreCase))
                        {
                            found = file;
                            break;
                        }
                    }
                }
                if (found == null) return (Ok: false, File: "");
                log($"Copying {found} into the Atlas Engine…");
                return (Ok: await AtlasEngine.InstallApworldAsync(AtlasEngine.Current, found, log, ct), File: Path.GetFileName(found));
            }, r => r.File.Length == 0 ? _tr("Your Archipelago install has no {0} apworld in custom_worlds or lib/worlds.").Replace("{0}", game)
                : r.Ok ? _tr("{0} is in the Atlas Engine (copied from your install).").Replace("{0}", r.File)
                : _tr("{0} couldn't be installed: the log says why.").Replace("{0}", r.File));
        }

        private const string DownArrow = "▾";

        private static bool IsYamlFile(string path) => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

        /// <summary>The chosen Archipelago install's Players folder, when the install is known and the folder exists (never searched for).</summary>
        private string? PlayersFolder()
        {
            string root = _hooks.Settings.ArchipelagoInstallationPath;
            if (string.IsNullOrWhiteSpace(root)) return null;
            string players = Path.Combine(root, "Players");
            return Directory.Exists(players) ? players : null;
        }

        /// <summary>A ▾ beside Add YAML…: the other places a YAML may be (this game's downloaded files, the install's Players folder, Atlas's own folder).</summary>
        private MenuButton YamlPlacesMenu(string game)
        {
            var menu = new MenuButton { Text = DownArrow, TooltipText = _tr("Other places to take a YAML from"), AccessibilityName = _tr("Add YAML from…"), FocusMode = FocusModeEnum.All };
            var popup = menu.GetPopup();
            string tools = GameFiles.ToolsFolder(DataManager.GetDataDirectory(), game);
            popup.AboutToPopup += () =>
            {
                popup.Clear();
                if (Directory.Exists(tools) && Directory.EnumerateFiles(tools).Any(IsYamlFile)) popup.AddItem(_tr("From this game's downloaded files…"), 0);
                if (PlayersFolder() != null) popup.AddItem(_tr("From my Archipelago install's Players folder…"), 1);
                popup.AddItem(_tr("From Atlas's YAML folder…"), 2);
            };
            popup.IdPressed += id => AddYaml(id switch { 0 => tools, 1 => PlayersFolder(), _ => GameFiles.YamlsFolder(DataManager.GetDataDirectory()) });
            menu.SetMeta("yaml_places", true);
            return menu;
        }

        /// <summary>The places the Add YAML menu offers right now (for tests).</summary>
        public List<string> YamlMenuItems()
        {
            var menu = _detail.FindChildren("*", nameof(MenuButton), true, false).OfType<MenuButton>().FirstOrDefault(m => m.HasMeta("yaml_places"));
            if (menu == null) return new List<string>();
            var popup = menu.GetPopup();
            popup.EmitSignal(PopupMenu.SignalName.AboutToPopup);
            return Enumerable.Range(0, popup.ItemCount).Select(i => popup.GetItemText(i)).ToList();
        }

        /// <summary>Picks a player YAML to keep: from <paramref name="startFolder"/>, else where one was last picked, else the install's Players folder.</summary>
        private void AddYaml(string? startFolder)
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.yaml, *.yml ; Archipelago player YAML" },
                UseNativeDialog = true,
                Title = _tr("A player YAML to keep in Atlas")
            };
            string? start = new[] { startFolder, _hooks.Settings.LastYamlFolder, PlayersFolder() }.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d));
            if (start != null) dialog.CurrentDir = start;
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

        /// <summary>
        /// Lists a release's other files for the user to pick what to download into the game's folder: first the release
        /// (the version a connected slot's seed was made with, else the newest full release), then its files.
        /// </summary>
        private void ListReleaseFiles(string game)
        {
            void Open()
            {
                if (!IsInstanceValid(this)) return;
                var versions = _versions.TryGetValue(game, out var known) ? known.Where(v => v.Repo != null).ToList() : new List<ApworldVersion>();
                if (versions.Count == 0)
                {
                    _status.Text = _tr("No release of {0} is known. Add the game's project first.").Replace("{0}", game);
                    return;
                }
                PickReleaseFiles(game, versions);
            }
            if (_versions.ContainsKey(game)) Open();
            else AskThenLoad(game, again: false, Open);
        }

        private void PickReleaseFiles(string game, List<ApworldVersion> versions)
        {
            // The slot's version first: a connected slot's chosen version, or the one Atlas identified as its seed's.
            string? preferred = _hooks.LiveSlots().Where(s => string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.ChosenApworld?.Version ?? (s.ServerChecksumFor(game) is { } sum ? ApworldSources.CachedFor(game, sum)?.Version : null)).FirstOrDefault(v => v != null);
            int start = Math.Max(0, preferred != null ? versions.FindIndex(v => ApworldChoices.SameLabel(v.Version, preferred)) : versions.FindIndex(v => !v.Prerelease));
            var dialog = new ConfirmationDialog { Title = _tr("Release files for {0}").Replace("{0}", game), OkButtonText = _tr("Download"), DialogHideOnOk = false, Unresizable = false };
            dialog.SetMeta("release_files", true);
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 6);
            box.AddChild(Wrapped(Kit.Text(_tr("Pick the release, then tick the files the game's setup guide asks for. Atlas keeps them in the game's folder and never runs them."))));
            var releaseRow = new HBoxContainer();
            releaseRow.AddThemeConstantOverride("separation", 8);
            var releaseLabel = Kit.Muted(_tr("Release:"));
            releaseLabel.AutowrapMode = TextServer.AutowrapMode.Off;
            releaseLabel.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            releaseRow.AddChild(releaseLabel);
            var release = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = _tr("Release"), ClipText = true };
            foreach (var v in versions) release.AddItem(v.Version + (v.Prerelease ? " " + _tr("(pre-release)") : "") + "  ·  github.com/" + v.Repo);
            release.Selected = start;
            releaseRow.AddChild(release);
            box.AddChild(releaseRow);
            var selectRow = new HBoxContainer();
            selectRow.AddThemeConstantOverride("separation", 8);
            var boxes = new List<(CheckBox Box, ApworldSources.ReleaseFile File)>();
            selectRow.AddChild(Kit.Button(_tr("Select all"), null, () => { foreach (var (b, _) in boxes) b.ButtonPressed = true; }, small: true));
            selectRow.AddChild(Kit.Button(_tr("Select none"), null, () => { foreach (var (b, _) in boxes) b.ButtonPressed = false; }, small: true));
            var note = Wrapped(Kit.Muted(""));
            selectRow.AddChild(note);
            box.AddChild(selectRow);
            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 220) };
            var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 2);
            scroll.AddChild(list);
            box.AddChild(scroll);
            dialog.AddChild(box);
            string chosenTag = "";
            string chosenRepo = "";
            void Fill(int index)
            {
                var v = versions[index];
                Clear(list);
                boxes.Clear();
                note.Text = _tr("Reading the release…");
                Async.Fire(async () =>
                {
                    var (found, problem, tag) = await Task.Run(() => ApworldSources.ReleaseFilesAsync(v.Repo, v.Version, CancellationToken.None));
                    if (!IsInstanceValid(dialog) || release.Selected != index) return;
                    chosenTag = tag ?? v.Version;
                    chosenRepo = v.Repo;
                    foreach (var f in found)
                    {
                        var tick = new CheckBox { Text = $"{f.Name}  ({Math.Max(1, f.Size / 1024)} KB{(f.Sha256 != null ? "" : ", " + _tr("no published hash"))})", AccessibilityName = f.Name };
                        boxes.Add((tick, f));
                        list.AddChild(tick);
                    }
                    note.Text = problem != null ? _tr("GitHub couldn't be read just now: {0}").Replace("{0}", problem)
                        : found.Count == 0 ? _tr("This release has no files besides the apworld.")
                        : _tr("{0} file(s) in {1}.").Replace("{0}", found.Count.ToString()).Replace("{1}", chosenTag);
                }, $"reading the files of {game} {v.Version}");
            }
            release.ItemSelected += index => Fill((int)index);
            Fill(start);
            dialog.Confirmed += () =>
            {
                var chosen = boxes.Where(b => b.Box.ButtonPressed).Select(b => b.File).ToList();
                if (chosen.Count == 0)
                {
                    note.Text = _tr("Tick the files to download first.");
                    return;
                }
                dialog.QueueFree();
                string repo = chosenRepo, tag = chosenTag;
                void Go() => Run(_tr("Downloading {0} file(s)…").Replace("{0}", chosen.Count.ToString()), async (log, ct) =>
                {
                    string folder = GameFiles.ToolsFolder(DataManager.GetDataDirectory(), game);
                    Directory.CreateDirectory(folder);
                    var record = SafeFile.ReadJson(Path.Combine(folder, "tools.json"), () => new List<ToolRecord>()) ?? new List<ToolRecord>();
                    foreach (var f in chosen)
                    {
                        string target = Path.Combine(folder, GameFiles.SafeName(f.Name));
                        log($"Downloading {f.Name} from github.com/{repo} ({tag})" + (f.Sha256 != null ? ", checked against its published SHA-256…" : "…"));
                        string sha = await EngineDownloader.DownloadAsync(f.Url, target, f.Sha256!, null!, ct);
                        record.RemoveAll(r => string.Equals(r.File, Path.GetFileName(target), StringComparison.OrdinalIgnoreCase));
                        record.Add(new ToolRecord { File = Path.GetFileName(target), Source = "github.com/" + repo, Version = tag, Sha256 = sha });
                    }
                    SafeFile.WriteJson(Path.Combine(folder, "tools.json"), record);
                    return chosen.Count;
                }, n => _tr("{0} file(s) from {1} in the game's folder. Atlas doesn't run them: the setup guide says what to do.").Replace("{0}", n.ToString()).Replace("{1}", tag));
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
            dialog.PopupCentered(new Vector2I(680, 520));
        }

        /// <summary>Opens the release-files dialog for a game (for tests; the page's button asks the GitHub permission first).</summary>
        internal void ListReleaseFilesForTests(string game) => ListReleaseFiles(game);

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
            string game = _selected != null && _games.Any(g => g.Game == _selected && g.Section != Section.Official) ? _selected : repo.Split('/').Last();
            ApworldSources.AddUserRepo(_hooks.Settings, game, repo);
            // A project added by hand joins the versions already read, so its releases show at once.
            _versions.Remove(game);
            _versionsAsked.Remove(game);
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
