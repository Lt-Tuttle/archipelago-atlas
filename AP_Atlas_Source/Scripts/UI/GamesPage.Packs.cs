#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The Games page's map packs (the Map Packs page folded into Games, the owner's choice): a game's page lists its packs
    /// as rows (the Pack Doctor's word with Review and Fix, Doctor…, Update, Delete…; a click shows the pack in
    /// Properties), and "All map packs" at the foot of the list shows every pack, found by typed words, those that match
    /// no game Atlas lists apart, with the folder's tools (Search GitHub for your games' packs, Check for updates, Install
    /// pack (zip), Open packs folder, Rescan). The window's MapPackManagerControl does the work: it builds the rows, reads
    /// the folder again and installs, hidden, and says when a rescan is done (the page redraws then).
    /// </summary>
    public sealed partial class GamesPage
    {
        /// <summary>An installed pack as the page lists it: its zip and its manifest (null when it has none).</summary>
        public sealed record PackFile(string Path, PopTrackerManifest? Manifest)
        {
            public string Name => Manifest?.Name ?? System.IO.Path.GetFileNameWithoutExtension(Path);
        }

        // Each zip's manifest, read once per version of the file (its last write and size): every pack counts from the
        // start, read or not (the packs themselves are read by the service's rescan, off the main thread).
        private static readonly Dictionary<string, (DateTime Written, long Length, PopTrackerManifest? Manifest)> Manifests = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>What <see cref="_selected"/> holds while All map packs shows.</summary>
        private const string AllPacksKey = "\u0001all-packs";

        private List<PackFile> _packs = new();
        private VBoxContainer? _allPacksBox;
        private string _packFilterText = "";
        private VBoxContainer? _packProgressHost;

        /// <summary>The packs All map packs lists now, matched to a game first, then those matching none (for tests).</summary>
        public List<string> ShownPacks { get; private set; } = new();

        /// <summary>The packs All map packs lists as matching no game Atlas lists (for tests).</summary>
        public List<string> UnmatchedPacks { get; private set; } = new();

        /// <summary>Whether All map packs shows (for tests).</summary>
        public bool AllPacksShown => _selected == AllPacksKey;

        /// <summary>Shows All map packs, as its entry at the list's foot does.</summary>
        public void SelectAllPacks()
        {
            _selected = AllPacksKey;
            Refresh();
        }

        /// <summary>Shows a download's progress (the pack service's row) above the page's status line.</summary>
        public void AddPackProgress(Control row)
        {
            if (_packProgressHost == null) return;
            row.GetParent()?.RemoveChild(row);
            _packProgressHost.AddChild(row);
        }

        private static List<PackFile> ReadPacks()
        {
            var packs = new List<PackFile>();
            string dir = PopTrackerPackLoader.GetPacksDirectory();
            if (!Directory.Exists(dir)) return packs;
            foreach (var zip in Directory.GetFiles(dir, "*.zip"))
            {
                PopTrackerManifest? manifest = null;
                var info = new FileInfo(zip);
                if (Manifests.TryGetValue(zip, out var known) && known.Written == info.LastWriteTimeUtc && known.Length == info.Length) manifest = known.Manifest;
                else
                {
                    try
                    {
                        manifest = ManifestOf(zip);
                    }
                    catch (Exception ex)
                    {
                        // A zip that isn't a pack (or a damaged one) is listed by its file name, as one Atlas can't read.
                        AP_Atlas.Core.Logger.LogDebug($"The map pack {Path.GetFileName(zip)}'s manifest couldn't be read: {ex.Message}");
                    }
                    Manifests[zip] = (info.LastWriteTimeUtc, info.Length, manifest);
                }
                packs.Add(new PackFile(zip, manifest));
            }
            return packs;
        }

        /// <summary>
        /// A zip's manifest as the loader reads one: manifest.json or pack.json, else (a pack without one, its maps in a
        /// maps folder) the zip's own name as the pack's and the game's.
        /// </summary>
        private static PopTrackerManifest? ManifestOf(string zip)
        {
            var manifest = MapPackManagerControl.ReadManifest(zip);
            if (manifest != null) return manifest;
            using var archive = SafeZip.Open(zip);
            bool hasMaps = archive.Entries.Any(e => e.FullName.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) || e.FullName.Contains("/maps/", StringComparison.OrdinalIgnoreCase));
            string name = Path.GetFileNameWithoutExtension(zip);
            return hasMaps ? new PopTrackerManifest { Name = name, GameName = name } : null;
        }

        /// <summary>The pack a game's page measures: the one its slots use (the preferred one), else the first installed.</summary>
        private PackFile? ChosenPack(string game)
        {
            var packs = PacksFor(game);
            var chosen = _hooks.Settings.PreferredPacks.TryGetValue(game, out var preferred)
                ? packs.FirstOrDefault(p => string.Equals(Path.GetFileName(p.Path), preferred, StringComparison.OrdinalIgnoreCase)) : null;
            return chosen ?? packs.FirstOrDefault();
        }

        /// <summary>Reads a game's pack off the main thread when it isn't read yet, then goes on (on the main thread).</summary>
        private void ReadPackThen(string game, Action then)
        {
            var file = ChosenPack(game);
            if (file == null || PopTrackerPackLoader.IsPackCached(file.Path))
            {
                then();
                return;
            }
            Async.Fire(System.Threading.Tasks.Task.Run(() => PopTrackerPackLoader.InspectZipPack(file.Path))
                .ContinueWith(_ => Ui.Defer(this, then), System.Threading.Tasks.TaskScheduler.Default), "reading a map pack", tellUser: false);
        }

        /// <summary>
        /// A rescan finished: the packs are read again and only what shows them is redrawn (a game's Map packs section, All
        /// map packs' rows, the list's counts and marks), so the rest of the page (a filter being typed in, an open menu,
        /// a folded section) stays as it is.
        /// </summary>
        public void RefreshPacks()
        {
            _packs = ReadPacks();
            FillList();
            if (AllPacksShown)
            {
                FillAllPacks();
                return;
            }
            if (_selected == null) return;
            // A pack installed for a game that had none (or its last one deleted) changes its card: the page is drawn again.
            string shown = _selected;
            if (_games.Any(g => g.Game == shown) && PacksFor(shown).Count > 0 != _shownHasPack)
            {
                Ui.Defer(this, () => { if (_selected == shown) ShowGame(shown); });
                return;
            }
            foreach (var body in _detail.FindChildren("*", nameof(VBoxContainer), true, false).OfType<VBoxContainer>().Where(b => b.HasMeta("game_packs") && GodotObject.IsInstanceValid(b)).ToList())
            {
                Clear(body);
                FillMapPacks(body, PacksFor(_selected));
            }
        }

        /// <summary>Shows the game a pack is for (as its rows pair them), or All map packs when it's for none Atlas lists.</summary>
        public void SelectPackGame(string zip)
        {
            _packs = ReadPacks();
            _games = CollectGames();
            var pack = _packs.FirstOrDefault(p => string.Equals(p.Path, zip, StringComparison.OrdinalIgnoreCase));
            string? game = pack == null ? null : _games.FirstOrDefault(g => PackIsFor(pack, g.Game))?.Game;
            if (game != null) Select(game);
            else SelectAllPacks();
        }

        private static bool PackIsFor(PackFile pack, string game) =>
            pack.Manifest != null && (PopTrackerPackLoader.IsGameNameMatch(pack.Manifest.GameName, game) || PopTrackerPackLoader.IsGameNameMatch(pack.Manifest.Name, game));

        private List<PackFile> PacksFor(string game) => _packs.Where(p => PackIsFor(p, game)).ToList();

        /// <summary>A game's map packs, as rows in a box of their own that RefreshPacks redraws (the card finds one when there's none).</summary>
        private void MapPacksSection(VBoxContainer fold, string game, List<PackFile> packs)
        {
            var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            body.AddThemeConstantOverride("separation", 4);
            body.SetMeta("game_packs", game);
            fold.AddChild(body);
            FillMapPacks(body, packs);
        }

        private void FillMapPacks(VBoxContainer body, List<PackFile> packs)
        {
            if (packs.Count == 0)
            {
                body.AddChild(Wrapped(Kit.Muted(_tr("No map pack for this game is installed. The card above finds one (Add this game, or Find a map pack…); All map packs, at the foot of the list, installs a zip you have."))));
                return;
            }
            if (_hooks.Packs is { } service) service.BuildPackRows(body, packs.Select(p => p.Path).ToList(), showGame: false);
        }

        /// <summary>The entry at the list's foot that shows every installed pack.</summary>
        private void AddAllPacksEntry(TreeItem root)
        {
            var item = _list.CreateItem(root);
            item.SetText(0, _tr("All map packs") + $" ({_packs.Count})");
            item.SetIcon(0, LucideTextures.Get("package", ThemeColors.TextMuted, 0.7f));
            item.SetTooltipText(0, _tr("Every PopTracker pack in Atlas's packs folder, with installing a zip, update checks and the folder"));
            item.SetMetadata(0, 1);
            if (_selected == AllPacksKey) item.Select(0);
        }

        private void ShowAllPacks()
        {
            _selected = AllPacksKey;
            Clear(_detail);
            _detail.AddChild(Kit.Heading(_tr("All map packs"), 1.4f));
            _detail.AddChild(Wrapped(Kit.Muted(_tr("Every PopTracker pack in Atlas's packs folder. Each game's page lists its own; a connected slot uses its game's pack by itself, and a pack you add or copy into the folder reaches it without a reconnect."))));
            var service = _hooks.Packs;
            var tools = new HFlowContainer();
            tools.AddThemeConstantOverride("h_separation", 8);
            tools.AddThemeConstantOverride("v_separation", 6);
            tools.AddChild(Kit.Button(_tr("Search GitHub for your games' packs…"), _tr("One search of GitHub per game you play that has no pack (from your connected slots and saved multiworlds); you choose what to install."), () => service?.SearchYourGames(), small: true));
            tools.AddChild(Kit.Button(_tr("Check for updates"), _tr("Asks each pack's versions link (the packs that publish one) whether a newer version is out."), () => service?.CheckUpdates(), small: true));
            tools.AddChild(Kit.Button(_tr("Install pack (zip)…"), _tr("Installs a PopTracker pack from a .zip file you have."), () => service?.ImportZip(), small: true));
            tools.AddChild(Kit.Button(_tr("Open packs folder"), _tr("Opens Atlas's packs folder (a pack copied into it shows here after a rescan)."), () => OpenFolder(PopTrackerPackLoader.GetPacksDirectory()), small: true));
            tools.AddChild(Kit.Button(_tr("Rescan"), _tr("Reads the packs folder again."), () => service?.Rescan(), small: true));
            _detail.AddChild(tools);
            var filter = new LineEdit { PlaceholderText = _tr("Filter packs by name or game"), ClearButtonEnabled = true, Text = _packFilterText, AccessibilityName = _tr("Filter packs") };
            filter.TextChanged += text =>
            {
                _packFilterText = text;
                FillAllPacks();
            };
            _detail.AddChild(filter);
            _allPacksBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _allPacksBox.AddThemeConstantOverride("separation", 6);
            _detail.AddChild(_allPacksBox);
            FillAllPacks();
        }

        /// <summary>Filters All map packs as typing in its box does (for tests).</summary>
        public void FilterPacks(string text)
        {
            _packFilterText = text;
            if (AllPacksShown) ShowAllPacks();
        }

        private void FillAllPacks()
        {
            if (_allPacksBox == null || !GodotObject.IsInstanceValid(_allPacksBox)) return;
            Clear(_allPacksBox);
            string[] words = _packFilterText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var shown = _packs.Where(p => words.All(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || (p.Manifest?.GameName ?? "").Contains(w, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p.Manifest?.GameName ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var matched = shown.Where(p => _games.Any(g => PackIsFor(p, g.Game))).ToList();
            var unmatched = shown.Except(matched).ToList();
            ShownPacks = matched.Concat(unmatched).Select(p => p.Name).ToList();
            UnmatchedPacks = unmatched.Select(p => p.Name).ToList();
            if (_packs.Count == 0)
            {
                _allPacksBox.AddChild(Wrapped(Kit.Muted(_tr("No map packs installed. A game's page finds one on GitHub; Install pack (zip)… takes a file you have."))));
                return;
            }
            if (shown.Count == 0)
            {
                _allPacksBox.AddChild(Wrapped(Kit.Muted(_tr("No pack matches the filter."))));
                return;
            }
            var service = _hooks.Packs;
            if (service == null) return;
            if (matched.Count > 0)
            {
                var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                rows.AddThemeConstantOverride("separation", 6);
                _allPacksBox.AddChild(rows);
                service.BuildPackRows(rows, matched.Select(p => p.Path).ToList(), showGame: true);
            }
            if (unmatched.Count > 0)
            {
                _allPacksBox.AddChild(Kit.Heading(_tr("Matching no game Atlas lists") + $" ({unmatched.Count})", 1.1f));
                _allPacksBox.AddChild(Wrapped(Kit.Muted(_tr("Their manifests name a game Atlas doesn't list (or none): add the game's project, or the pack may be for another game's name."))));
                var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                rows.AddThemeConstantOverride("separation", 6);
                rows.SetMeta("unmatched_packs", true);
                _allPacksBox.AddChild(rows);
                service.BuildPackRows(rows, unmatched.Select(p => p.Path).ToList(), showGame: true);
            }
        }
    }
}
