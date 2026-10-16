#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AP_Atlas.Core;
using AP_Atlas.Core.EngineSetup;
using AP_Atlas.Core.Games;
using AP_Atlas.Core.PopTracker;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// The top of a game's page: one card saying where the game stands, in plain words, with the one thing to do, from
    /// the same plan the Add games screen makes (<see cref="GameStandings.Of"/>): Set up the Atlas Engine first; Add this
    /// game (what adding gets and its download); Ready to play (the logic's version and project, the map pack, whether a
    /// slot's logic is the seed's), with Update to … when a newer full release is known that nothing Atlas looked inside
    /// rules out; Ready to play without a map; or why Atlas can't add it yet. Beside it, the map's own buttons: Doctor…
    /// for the game's pack, Find a (another) map pack…, Start a pack… when none is installed and the game's names are
    /// known. A slot whose seed needs another version says so here, with Choose the version….
    /// </summary>
    public sealed partial class GamesPage
    {
        private const string AdvancedKey = "games:advanced";
        private GameStanding? _cardStanding;
        private string _cardHeading = "", _cardLine = "";

        /// <summary>The game's card as shown: its standing, heading and line (for tests).</summary>
        public (GameStanding? Standing, string Heading, string Line) GameCardShown => (_cardStanding, _cardHeading, _cardLine);

        /// <summary>The version the card's Update button installs, or null when it offers none (for tests).</summary>
        public string? UpdateOffered =>
            _detail.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.HasMeta("update_game") && !b.IsQueuedForDeletion())?.GetMeta("update_game").AsString();

        private PanelContainer GameCard(GameEntry entry, EngineInstall? install, List<(string File, string Sha256)> installed, List<ApworldChoice> choices, ReleaseView view, List<PackFile> packs)
        {
            string game = entry.Game;
            var (plan, part) = PlanFor(game, install);
            var standing = GameStandings.Of(plan, part);
            _cardStanding = standing;
            var panel = CardPanel();
            panel.SetMeta("game_card", standing.ToString());
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            panel.AddChild(box);

            string words = _tr(AddGamesPlan.Words(part));
            string logic = LogicLine(entry, installed, choices, install);
            (_cardHeading, _cardLine) = standing switch
            {
                GameStanding.EngineFirst => (_tr("Set up the Atlas Engine first"),
                    _tr("Atlas runs a game's logic in its engine.") + (part.Items.Count > 0 ? " " + _tr("Once it's set up, Add this game gets {0}.").Replace("{0}", words) : "")),
                GameStanding.Add => (_tr("Add this game"), _tr("Adding it gets {0}.").Replace("{0}", words) + " " + DownloadLine(plan)),
                GameStanding.Ready => (_tr("Ready to play"), logic + " " + _tr("Map pack: {0}.").Replace("{0}", ChosenPack(game)?.Name ?? "")),
                GameStanding.ReadyNoMap => (_tr("Ready to play, without a map"), logic + " " + NoMapLine(game)),
                _ => (_tr("Atlas can't add this game yet"),
                    _tr("Atlas doesn't know where its logic comes from: Add a project… takes the GitHub link of the project that publishes its apworld, and Choose a file… installs an .apworld you have."))
            };
            box.AddChild(Kit.Heading(_cardHeading, 1.15f));
            box.AddChild(Wrapped(Kit.Text(_cardLine)));
            if (standing is GameStanding.Ready or GameStanding.ReadyNoMap)
                box.AddChild(Wrapped(Kit.Muted(_tr("Connect a slot that plays it: Atlas runs its logic and shows its map by itself."))));
            if (standing != GameStanding.EngineFirst && GameTrustRecords.Line(install, game, _tr) is { } trust)
            {
                var trustLabel = Wrapped(Kit.Muted(trust));
                trustLabel.SetMeta("trust_line", true);
                box.AddChild(trustLabel);
            }
            // A slot whose seed needs another version: the one thing Your slots, folded below, would otherwise keep out of sight.
            if (_hooks.LiveSlots().FirstOrDefault(s => string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase) && s.SeedVersionWanted) is { } waiting)
            {
                box.AddChild(Wrapped(Kit.Text(_tr("{0}: its seed was made with another version, and logic doesn't match it.").Replace("{0}", waiting.SlotName), ThemeColors.Warning)));
                var versionRow = new HFlowContainer();
                versionRow.AddChild(Kit.Button(_tr("Choose the version…"), _tr("Lists the game's releases; pick the one the seed's host used, or let Atlas try them."),
                    () => ApworldPickerDialog.Open(this, _hooks.Settings, waiting, _tr), small: true));
                box.AddChild(versionRow);
            }

            var actions = new HFlowContainer();
            actions.AddThemeConstantOverride("h_separation", 8);
            actions.AddThemeConstantOverride("v_separation", 6);
            switch (standing)
            {
                case GameStanding.EngineFirst:
                    actions.AddChild(Kit.Button(_tr("Set up the Atlas Engine…"), null, _hooks.OpenEngineSetup));
                    break;
                case GameStanding.Add:
                    var add = Kit.Button(_tr("Add this game…"), _tr("Shows what adding it downloads and from where, asks once, then installs its logic and its map pack."),
                        () => _hooks.AddGames(game), enabled: !GameAdder.Busy);
                    add.SetMeta("add_game", game);
                    actions.AddChild(add);
                    break;
                case GameStanding.Unknown:
                    actions.AddChild(Kit.Button(_tr("Add a project…"), _tr("Paste the GitHub link of a project that publishes this game's apworld."), AddProject));
                    actions.AddChild(Kit.Button(_tr("Choose a file…"), _tr("Install an .apworld file you have."), () => InstallFile(game)));
                    break;
            }
            // Update: only for an apworld the engine has, never a release Atlas found can't work here.
            if (entry.InEngine && standing != GameStanding.EngineFirst
                && GameStandings.Update(choices, c => RecordOf(view.Store, c) is { } record && ReleaseFacts.WhyNot(record, view.EngineAp, view.FitKey, view.AnyFits) != null) is { } newer
                && VersionOf(game, newer) is { } newerVersion)
            {
                var update = Kit.Button(_tr("Update to {0}").Replace("{0}", newer.Version),
                    _tr("Installs {0} from {1} in the Atlas Engine in place of the installed version (checked against its published SHA-256); connected slots restart their logic on it.").Replace("{0}", newer.Version).Replace("{1}", newer.Source),
                    () => ConfirmInstall(game, RepoOf(newer), newerVersion));
                update.SetMeta("update_game", newer.Version);
                actions.AddChild(update);
            }
            if (ChosenPack(game) is { } chosen && _hooks.Packs is { } service)
            {
                var doctor = Kit.Button(_tr("Doctor…"), _tr("Check this pack against the game and fix problems locally"), () => service.OpenDoctor?.Invoke(chosen.Path, null));
                doctor.SetMeta("game_doctor", chosen.Path);
                actions.AddChild(doctor);
            }
            var find = Kit.Button(packs.Count > 0 ? _tr("Find another map pack…") : _tr("Find a map pack…"),
                _tr("One search of GitHub for this game's PopTracker packs, when you press; you choose what to install."), () => _hooks.FindPack(game));
            find.SetMeta("find_pack", true);
            actions.AddChild(find);
            if (packs.Count == 0 && NamesKnown(game))
            {
                var start = Kit.Button(_tr("Start a pack…"), _tr("Makes a map pack from the game's own names: every location a pin, grouped by area, for you to move where it belongs."), () => _hooks.StartPack(game));
                start.SetMeta("start_pack", true);
                actions.AddChild(start);
            }
            box.AddChild(actions);
            return panel;
        }

        /// <summary>The logic the engine runs for the game, in a sentence: a release with its project, a version, an official game's Archipelago.</summary>
        private string LogicLine(GameEntry entry, List<(string File, string Sha256)> installed, List<ApworldChoice> choices, EngineInstall? install)
        {
            if (entry.Section == Section.Official && installed.Count == 0) return _tr("Logic: ships with Archipelago {0}.").Replace("{0}", EngineAp(install));
            if (choices.FirstOrDefault(c => c.Installed) is { } mine) return _tr("Logic: {0} from {1}.").Replace("{0}", mine.Version).Replace("{1}", mine.Source);
            string? tag = installed.Select(c => ApworldSources.KnownSourceOf(c.Sha256).Tag).FirstOrDefault(t => t != null);
            return tag != null ? _tr("Logic: {0}.").Replace("{0}", ApworldChoices.NormalizeLabel(tag)) : _tr("Logic: an apworld you installed.");
        }

        /// <summary>What the engine has of a community game's apworld, in a line (the top of Advanced).</summary>
        private string ApworldLine(GameEntry entry, bool engineReady, List<(string File, string Sha256)> installed, EngineCheckResult? check)
        {
            string? version = installed.Select(c => ApworldSources.KnownSourceOf(c.Sha256).Tag).FirstOrDefault(t => t != null);
            return !engineReady ? _tr("The Atlas Engine isn't set up yet.")
                : entry.InEngine ? _tr("In the engine") + (version != null ? " (" + version + ")" : "") + "."
                : check == null ? _tr("Atlas hasn't checked the engine yet: Check now on the Atlas Engine page says which games it has.")
                : _tr("Not in the engine yet.");
        }

        private string NoMapLine(string game) => NamesKnown(game)
            ? _tr("No map pack is known for it yet, so the Map Tracker has no map: Find a map pack… searches GitHub once, and Start a pack… makes one from the game's names.")
            : _tr("No map pack is known for it yet, so the Map Tracker has no map: Find a map pack… searches GitHub once; once a slot of it has connected, Start a pack… can make one from the game's names.");

        /// <summary>Whether Atlas has the game's names (a connection's, or the local install's): what Start a pack is made from.</summary>
        private static bool NamesKnown(string game) => AP_Atlas.Core.PopTracker.GameNames.Best(game)?.Locations is { Count: > 0 };

        /// <summary>The card's download line, in the Add games screen's words.</summary>
        private string DownloadLine(AddPlan plan) => AddGamesWords.Download(plan.DownloadBytes, plan.UnknownSizes, _tr) is { Length: > 0 } size
            ? _tr("Download: {0}.").Replace("{0}", size) : _tr("Nothing to download.");

        /// <summary>The card's frame (as the Next step card had it).</summary>
        private static PanelContainer CardPanel()
        {
            var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
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
                ContentMarginTop = 10,
                ContentMarginBottom = 10
            });
            return panel;
        }
    }
}
