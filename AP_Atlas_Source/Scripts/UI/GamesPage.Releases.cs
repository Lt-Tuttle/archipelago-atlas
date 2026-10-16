#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AP_Atlas.Core;
using AP_Atlas.Core.EngineSetup;
using AP_Atlas.Core.Games;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The Games page's release facts: what each release of a game's apworld says about itself (the tracker its setup
    /// guide names, the Archipelago it needs), how the installed map pack fits it, the installed version as a card of
    /// those facts, and the release Atlas picks (the one the pack fits, else the one whose guide names a tracker).
    /// </summary>
    public sealed partial class GamesPage
    {
        /// <summary>The Archipelago the engine runs (the health check's), else the one the portable engine sets up.</summary>
        private static string EngineAp(EngineInstall? install) => (install != null ? AtlasEngine.LastCheck(install)?.Archipelago : null) ?? AtlasEngine.ArchipelagoVersion;

        /// <summary>The game's map pack the page measures: the one its slots use (the preferred one), else the first installed.</summary>
        private LoadedPack? PackOf(string game)
        {
            // Only a pack already read: the pack service's rescan reads the rest off the main thread, then the page redraws.
            var chosen = ChosenPack(game);
            return chosen != null && PopTrackerPackLoader.IsPackCached(chosen.Path) ? PopTrackerPackLoader.InspectZipPack(chosen.Path) : null;
        }

        /// <summary>What the page knows of a game's releases: their facts, the pack measured, the engine's Archipelago, and Atlas's pick.</summary>
        private sealed record ReleaseView(ReleaseFactsStore Store, LoadedPack? Pack, string? FitKey, string EngineAp, bool AnyFits, ReleaseChoice.Pick? Pick);

        private ReleaseView ViewOf(string game, List<ApworldChoice> choices)
        {
            var store = ReleaseFactsStore.Load(DataManager.GetDataDirectory(), game);
            var pack = PackOf(game);
            string? fitKey = pack != null ? ReleaseLook.FitKey(pack) : null;
            string ap = EngineAp(AtlasEngine.Current);
            var records = choices.Where(c => c.Url != null).Select(c => RecordOf(store, c)).OfType<ReleaseRecord>().ToList();
            bool anyFits = fitKey != null && records.Any(r => r.FitOf(fitKey) is { Fits: true });
            return new ReleaseView(store, pack, fitKey, ap, anyFits, ReleaseChoice.Best(records, fitKey, ap));
        }

        private static ReleaseRecord? RecordOf(ReleaseFactsStore store, ApworldChoice choice) => store.Find(choice.Source, choice.Version, choice.Sha256);

        private static bool SameRelease(ReleaseRecord record, ApworldChoice choice) =>
            string.Equals(record.Project, choice.Source, StringComparison.OrdinalIgnoreCase) && ApworldChoices.SameLabel(record.Version, choice.Version);

        /// <summary>Whether the game's newest releases have been looked inside already.</summary>
        private bool LookedInside(string game, List<ApworldChoice> choices)
        {
            var store = ReleaseFactsStore.Load(DataManager.GetDataDirectory(), game);
            var newest = choices.Where(c => c.Url != null).Take(ReleaseChoice.MostLooked).ToList();
            return newest.Count > 0 && newest.All(c => RecordOf(store, c) != null);
        }

        /// <summary>A table row's "Works here" text, and why it's greyed (null when it isn't).</summary>
        private (string Text, string? Why, bool Picked) WorksText(ApworldChoice choice, ReleaseView view)
        {
            var record = RecordOf(view.Store, choice);
            if (record == null) return ("", null, false);
            if (ReleaseFacts.WhyNot(record, view.EngineAp, view.FitKey, view.AnyFits) is { } why)
                return (char.ToUpperInvariant(why[0]) + why.Substring(1), why, false);
            var parts = new List<string>();
            if (record.FitOf(view.FitKey) is { } fit) parts.Add(_tr("map pack {0}%").Replace("{0}", Math.Round(fit.LinkedShare * 100).ToString()));
            if (record.GuideTrackers.Count > 0) parts.Add(_tr("its guide names a tracker"));
            if (parts.Count == 0 && record.Locations is { } count) parts.Add(_tr("{0} locations").Replace("{0}", count.ToString()));
            bool picked = view.Pick != null && ReferenceEquals(view.Pick.Release, record);
            if (picked) parts.Insert(0, _tr("Atlas's pick"));
            return (string.Join(" · ", parts), null, picked);
        }

        /// <summary>The installed version as a card: its facts with ✓/✗, and what to do about them.</summary>
        private Control VersionCard(string game, ApworldChoice? choice, (string File, string Sha256) copy, ReleaseView view, List<ApworldChoice> choices)
        {
            var (repo, tag) = ApworldSources.KnownSourceOf(copy.Sha256);
            string version = choice?.Version ?? (tag != null ? ApworldChoices.NormalizeLabel(tag) : _tr("version unknown"));
            string project = choice?.Source ?? (repo != null ? "github.com/" + repo : _tr("your own file"));
            ReleaseRecord facts;
            try { facts = ReleaseFacts.Read(copy.File, project, version, choice?.Prerelease ?? false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                AP_Atlas.Core.Logger.LogDebug($"The installed {game} apworld couldn't be read: {ex.Message}");
                facts = new ReleaseRecord { Project = project, Version = version };
            }
            if (view.Store.Find(project, version, copy.Sha256) is { } known)
            {
                foreach (var fit in known.PackFits) facts.PackFits[fit.Key] = fit.Value;
                facts.LoadError = known.LoadError;
            }
            var panel = new PanelContainer();
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = ThemeColors.SurfaceSunken,
                BorderColor = ThemeColors.AccentTint,
                BorderWidthLeft = 3,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft = 12,
                ContentMarginRight = 12,
                ContentMarginTop = 8,
                ContentMarginBottom = 8
            });
            panel.SetMeta("version_card", version);
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 4);
            panel.AddChild(box);
            box.AddChild(Kit.Text(_tr("Installed: {0} from {1}").Replace("{0}", version).Replace("{1}", project) + (facts.Prerelease ? " " + _tr("(pre-release)") : "")));
            _cardFacts = CardFacts(game, facts, view);
            foreach (var (good, text) in _cardFacts) box.AddChild(FactRow(good, text));

            var actions = new HFlowContainer();
            actions.AddThemeConstantOverride("h_separation", 8);
            actions.AddThemeConstantOverride("v_separation", 6);
            if (view.Pick is { } pick && !(string.Equals(pick.Release.Project, project, StringComparison.OrdinalIgnoreCase) && ApworldChoices.SameLabel(pick.Release.Version, version))
                && choices.FirstOrDefault(c => c.Url != null && SameRelease(pick.Release, c)) is { } pickChoice && VersionOf(game, pickChoice) is { } pickVersion)
            {
                var switchButton = Kit.Button(_tr("Switch to {0}").Replace("{0}", pickChoice.Version),
                    _tr("Atlas picks {0} from {1}: {2}. Installs it in the Atlas Engine in place of {3}; connected slots restart their logic on it.")
                        .Replace("{0}", pickChoice.Version).Replace("{1}", pickChoice.Source).Replace("{2}", pick.Why).Replace("{3}", version),
                    () => ConfirmInstall(game, RepoOf(pickChoice), pickVersion));
                switchButton.SetMeta("switch_button", pickChoice.Version);
                actions.AddChild(switchButton);
                box.AddChild(Wrapped(Kit.Muted(_tr("Atlas would pick {0}: {1}.").Replace("{0}", pickChoice.Version).Replace("{1}", pick.Why))));
            }
            if (repo != null || choice?.Url != null)
                actions.AddChild(Kit.Button(_tr("Get from this release…"), _tr("This release's other files (a client, a mod, a patcher): tick the ones the game's setup guide asks for. Atlas keeps them in the game's folder and never runs them."),
                    () => ListReleaseFiles(game, version)));
            if (actions.GetChildCount() > 0) box.AddChild(actions);
            return panel;
        }

        private List<(bool? Good, string Text)> _cardFacts = new();

        /// <summary>The installed version's card, as its lines: ✓ (true), ✗ (false) or – (null), and the text (for tests).</summary>
        public IReadOnlyList<(bool? Good, string Text)> CardFactsShown => _cardFacts;

        /// <summary>The card's lines: the map pack's fit, what its guide names, the Archipelago it needs, the multiworlds that use it.</summary>
        private List<(bool? Good, string Text)> CardFacts(string game, ReleaseRecord facts, ReleaseView view)
        {
            var lines = new List<(bool?, string)>();
            string packName = view.Pack?.Manifest?.Name ?? "";
            if (view.Pack == null) lines.Add((null, _tr("No map pack is installed for this game.")));
            else if (facts.FitOf(view.FitKey) is { } fit)
                lines.Add((fit.Fits, (fit.Fits ? _tr("Your map pack {0} fits it: {1}% of its pins link to its checks.") : _tr("Your map pack {0} doesn't fit it well: {1}% of its pins link to its checks."))
                    .Replace("{0}", packName).Replace("{1}", Math.Round(fit.LinkedShare * 100).ToString())));
            else lines.Add((null, _tr("Your map pack {0} isn't measured against it yet: \"Which version works with my map pack?\" does that.").Replace("{0}", packName)));
            lines.Add(facts.GuideTrackers.Count > 0 ? (true, _tr("Its setup guide names the map tracker github.com/{0}.").Replace("{0}", facts.GuideTrackers[0]))
                : (null, facts.HasGuide ? _tr("Its setup guide names no map tracker.") : _tr("It bundles no setup guide.")));
            if (facts.LoadError != null) lines.Add((false, _tr("It couldn't be loaded in the engine: {0}").Replace("{0}", facts.LoadError)));
            else if (ReleaseFacts.ArchipelagoProblem(facts, view.EngineAp) is { } problem) lines.Add((false, _tr("It {0}.").Replace("{0}", problem)));
            else lines.Add((true, facts.MinimumAp != null ? _tr("It runs on the engine's Archipelago {0} (it needs {1} or newer).").Replace("{0}", view.EngineAp).Replace("{1}", facts.MinimumAp)
                : _tr("It runs on the engine's Archipelago {0} (its manifest doesn't say which it needs).").Replace("{0}", view.EngineAp)));
            var users = MultiworldsRunning(game, null);
            lines.Add(users.Count > 0 ? (true, _tr("Used by: {0}.").Replace("{0}", string.Join("; ", users))) : (null, _tr("None of your multiworlds plays this game yet.")));
            return lines;
        }

        /// <summary>
        /// The user's multiworlds whose slots run a version of the game: those that chose this version for their seed, or
        /// (for the installed one, <paramref name="version"/> null) those that run what's installed. "Name (slot, slot)".
        /// </summary>
        private List<string> MultiworldsRunning(string game, string? version)
        {
            var found = new List<string>();
            foreach (var profile in _hooks.Profiles())
            {
                var slots = profile.Slots.Where(slot => profile.SavedStats.TryGetValue(slot, out var stats) && string.Equals(stats.GameName, game, StringComparison.OrdinalIgnoreCase)).Where(slot =>
                {
                    bool chose = _hooks.Settings.SlotApworlds.TryGetValue(Annotations.SlotKey(profile.Id, slot), out var choice) && string.Equals(choice.Game, game, StringComparison.OrdinalIgnoreCase);
                    return version == null ? !chose : chose && ApworldChoices.SameLabel(choice!.Version, version);
                }).ToList();
                if (slots.Count > 0) found.Add($"{profile.Name} ({string.Join(", ", slots)})");
            }
            return found;
        }

        private Control FactRow(bool? good, string text)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(new TextureRect
            {
                Texture = LucideTextures.Get(good == true ? "circle-check" : good == false ? "circle-x" : "circle-minus",
                    good == true ? ThemeColors.Success : good == false ? ThemeColors.Warning : ThemeColors.TextSubtle, 0.8f),
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
                CustomMinimumSize = new Vector2(20, 20),
                SizeFlagsVertical = SizeFlags.ShrinkBegin,
                TooltipText = good == true ? _tr("Yes") : good == false ? _tr("No") : _tr("Not known")
            });
            var label = Wrapped(Kit.Text(text));
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(label);
            row.SetMeta("fact", text);
            return row;
        }

        /// <summary>With nothing installed: the release Atlas would install, and why, with its Install button.</summary>
        private Control PickLine(string game, ApworldChoice choice, ReleaseChoice.Pick pick)
        {
            var row = new HFlowContainer();
            row.AddThemeConstantOverride("h_separation", 8);
            var label = Wrapped(Kit.Text(_tr("Atlas would install {0} from {1}: {2}.").Replace("{0}", choice.Version).Replace("{1}", choice.Source).Replace("{2}", pick.Why)));
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(label);
            row.AddChild(Kit.Button(_tr("Install {0}").Replace("{0}", choice.Version), _tr("Installs it in the Atlas Engine (you're asked to trust its project once; the file is checked against its published SHA-256)."), () =>
            {
                if (VersionOf(game, choice) is { } version) ConfirmInstall(game, RepoOf(choice), version);
            }));
            row.SetMeta("pick_line", choice.Version);
            return row;
        }

        // ---- Looking inside the releases ----

        /// <summary>
        /// "Which version works with my map pack?": downloads the newest few releases into the game's folder (asked once
        /// per game, with their sizes; the projects trusted by that answer, since loading a release runs its code), reads
        /// their guides and manifests, and with the engine set up measures the installed pack against each one's names.
        /// <paramref name="then"/> hears whether it ran.
        /// </summary>
        private void LookInside(string game, Action<bool>? then = null)
        {
            if (!_versions.ContainsKey(game))
            {
                AskThenLoad(game, again: false, () => LookInside(game, then), () => then?.Invoke(false));
                return;
            }
            var install = AtlasEngine.Current;
            var installed = install != null && install.CanLaunch ? ApworldSources.InstalledCopies(install, game) : new List<(string File, string Sha256)>();
            var choices = Choices(game, installed);
            var candidates = new List<ReleaseLook.Candidate>();
            foreach (var c in choices.Where(c => c.Url != null).Take(ReleaseChoice.MostLooked))
                if (VersionOf(game, c) is { } v) candidates.Add(new ReleaseLook.Candidate(c.Source, c.Version, c.Prerelease, v, c.LocalFile != null && File.Exists(c.LocalFile) ? c.LocalFile : null));
            // The installed copy is read from the engine (nothing downloaded) when it isn't among the newest.
            if (installed.Count > 0 && !choices.Where(c => c.Url != null).Take(ReleaseChoice.MostLooked).Any(c => c.Installed))
            {
                var mine = choices.FirstOrDefault(c => c.Installed);
                var (repo, tag) = ApworldSources.KnownSourceOf(installed[0].Sha256);
                candidates.Add(new ReleaseLook.Candidate(mine?.Source ?? (repo != null ? "github.com/" + repo : _tr("your own file")),
                    mine?.Version ?? (tag != null ? ApworldChoices.NormalizeLabel(tag) : _tr("installed")), mine?.Prerelease ?? false, null, installed[0].File));
            }
            if (candidates.Count == 0)
            {
                _status.Text = _tr("No release of {0} is known to look inside. Add the game's project first.").Replace("{0}", game);
                then?.Invoke(false);
                return;
            }
            var projects = candidates.Where(c => c.LocalFile == null).Select(c => c.Project).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            void Go() => Async.Fire(LookAsync(game, candidates, then), "looking inside " + game + "'s releases");
            if (projects.Count == 0)
            {
                Go();
                return;
            }
            var (bytes, unknown) = ReleaseLook.DownloadSize(candidates);
            string size = (bytes > 0 ? _tr("about {0} MB").Replace("{0}", Math.Max(0.1, Math.Round(bytes / 1048576.0, 1)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)) : _tr("their size isn't listed"))
                + (bytes > 0 && unknown > 0 ? _tr(", besides {0} whose size isn't listed").Replace("{0}", unknown.ToString()) : "");
            // One permission for every game (Allow once lasts until Atlas closes; Always allow never asks again): looking
            // inside loads each release, so the projects it comes from become trusted ones.
            PermissionDialog.Ask(DialogParent, _hooks.Settings, Permissions.ReleaseLook, null,
                _tr("This time: {0} release(s) of {1} ({2}) from {3}.").Replace("{0}", candidates.Count(c => c.LocalFile == null).ToString()).Replace("{1}", game).Replace("{2}", size).Replace("{3}", string.Join(", ", projects)),
                allowed =>
                {
                    if (!allowed || !IsInstanceValid(this))
                    {
                        then?.Invoke(false);
                        return;
                    }
                    foreach (var p in projects.Where(p => p.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)))
                        if (!ApworldSources.IsRepoApproved(_hooks.Settings, p.Substring("github.com/".Length))) ApworldSources.ApproveRepo(_hooks.Settings, p.Substring("github.com/".Length));
                    Go();
                });
        }

        private async Task LookAsync(string game, List<ReleaseLook.Candidate> candidates, Action<bool>? then)
        {
            var install = AtlasEngine.Current;
            // The pack read off the main thread, prepared on it (the Doctor's stores are the main thread's).
            var pack = await Task.Run(() => SoloTestRunner.InstalledPackFor(game));
            if (!IsInstanceValid(this)) return;
            var inputs = pack != null ? PackDoctor.Prepare(pack, null) : null;
            string? fitKey = pack != null ? ReleaseLook.FitKey(pack) : null;
            string ap = EngineAp(install);
            string? apworldName = ApworldSources.Find(game)?.Apworld
                ?? (install != null ? ApworldSources.InstalledCopies(install, game).Select(c => Path.GetFileNameWithoutExtension(c.File)).FirstOrDefault() : null);
            var found = await RunAsync(_tr("Looking inside {0}'s releases…").Replace("{0}", game), (log, ct) => ReleaseLook.LookAsync(install, game, apworldName, candidates, inputs, log, ct),
                records => ReleaseChoice.Best(records, fitKey, ap) is { } pick
                    ? _tr("Looked inside {0} release(s). Atlas's pick: {1}: {2}.").Replace("{0}", records.Count.ToString()).Replace("{1}", pick.Release.Version).Replace("{2}", pick.Why)
                    : _tr("Looked inside {0} release(s); none of them runs here.").Replace("{0}", records.Count.ToString()));
            Ui.Defer(this, () => then?.Invoke(found != null));
        }

        /// <summary>Forgets what GitHub said of a game's releases this session (for tests: the next install reads them again first).</summary>
        internal void ForgetVersionsForTests(string game)
        {
            _versions.Remove(game);
            _versionsAsked.Remove(game);
            _projects.Remove(game);
        }

        /// <summary>Presses "Which version works with my map pack?" (for tests: the question is asked as the button asks it).</summary>
        internal void LookInsideForTests(string game) => LookInside(game);

        /// <summary>"Which version works with my map pack?" for a game, from elsewhere (Install games' summary, a map that fits poorly): asked first, as the button asks.</summary>
        internal void FindVersionThatFits(string game) => LookInside(game);

        /// <summary>
        /// Installs the release Atlas picks: the newest the installed map pack fits, else the newest whose own guide names a
        /// map tracker (a pre-release too), else the newest full release; with <paramref name="look"/> it first looks inside
        /// the newest releases when it hasn't (asking once per game). The permission and trust questions as always; says
        /// whether it was installed, and which and why.
        /// </summary>
        internal Task<(bool Ok, string? Why)> InstallBestAsync(string game, Node? dialogParent, bool look)
        {
            _dialogParent = dialogParent;
            var done = new TaskCompletionSource<bool>();
            string? why = null;
            void Offer()
            {
                var install = AtlasEngine.Current;
                var installed = install != null ? ApworldSources.InstalledCopies(install, game) : new List<(string File, string Sha256)>();
                var choices = Choices(game, installed);
                var view = ViewOf(game, choices);
                ApworldChoice? chosen = null;
                string reason = _tr("the newest full release");
                if (view.Pick is { } pick && choices.FirstOrDefault(c => c.Url != null && SameRelease(pick.Release, c)) is { } picked)
                {
                    chosen = picked;
                    reason = pick.Why;
                }
                chosen ??= choices.FirstOrDefault(c => !c.Prerelease && c.Url != null);
                if (chosen == null || VersionOf(game, chosen) is not { } version)
                {
                    _status.Text = _tr("No release of {0} is known. Add the game's project, or choose its file.").Replace("{0}", game);
                    done.TrySetResult(false);
                    return;
                }
                why = $"{chosen.Version} from {chosen.Source}: {reason}";
                ConfirmInstall(game, RepoOf(chosen), version, done);
            }
            // The game's pack is read first (off the main thread), so Atlas's pick knows how it fits.
            void Ready() => ReadPackThen(game, () =>
            {
                if (look && !LookedInside(game, Choices(game, new List<(string File, string Sha256)>()))) LookInside(game, _ => Offer());
                else Offer();
            });
            if (_versions.ContainsKey(game)) Ready();
            else AskThenLoad(game, again: false, Ready, () => done.TrySetResult(false));
            return WithWhyAsync();

            async Task<(bool Ok, string? Why)> WithWhyAsync()
            {
                bool ok = await AnsweredAsync(done.Task, dialogParent);
                return (ok, ok ? why : null);
            }
        }
    }
}
