#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AP_Atlas.Core;
using AP_Atlas.Core.EngineSetup;
using AP_Atlas.Core.Games;
using Godot;

namespace AP_Atlas.UI
{
    /// <summary>
    /// Picks the apworld version a slot's seed was made with. Lists the game's releases (from the project the installed
    /// copy came from, the community index and projects the user added), newest first, with the ones Atlas has and the
    /// one known to match the seed marked. The user picks the host's version (Use this one), lets Atlas try them one by
    /// one, newest first, until one matches (Try to find it, which says how many it may download and can be cancelled),
    /// chooses a file, or adds the project the host used. The choice is kept for the slot and its seed; only that slot's
    /// logic uses it, and nothing in the user's own Archipelago changes. Nothing is downloaded without a press, and only
    /// from projects the user trusts.
    /// </summary>
    public sealed partial class ApworldPickerDialog : AcceptDialog
    {
        private readonly AppSettings _settings;
        private readonly SlotTrackerControl _slot;
        private readonly Func<string, string> _tr;
        private readonly string _game, _seed;
        private readonly EngineInstall _install;
        private readonly Tree _list;
        private readonly Label _intro, _status;
        private readonly ProgressBar _progress;
        private readonly Button _use, _find, _choose, _addProject, _lookUp;
        private List<ApworldChoice> _choices = new();
        private List<ApworldSources.ApworldRepo> _repos = new();
        private CancellationTokenSource? _running;

        /// <summary>What the list shows (for tests).</summary>
        public IReadOnlyList<ApworldChoice> Choices => _choices;

        /// <summary>The status line (for tests).</summary>
        public string StatusText => _status.Text;

        public static ApworldPickerDialog Open(Node parent, AppSettings settings, SlotTrackerControl slot, Func<string, string> tr)
        {
            var dialog = new ApworldPickerDialog(settings, slot, tr);
            parent.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(760, 520));
            dialog.Load(askGitHub: false);
            return dialog;
        }

        private ApworldPickerDialog(AppSettings settings, SlotTrackerControl slot, Func<string, string> tr)
        {
            _settings = settings;
            _slot = slot;
            _tr = tr;
            _game = slot.Game ?? "";
            _seed = slot.ServerChecksumFor(_game) ?? "";
            _install = slot.Model.Logic.Engine.Install;
            Title = _tr("Which {0} apworld?").Replace("{0}", _game);
            OkButtonText = _tr("Close");
            Unresizable = false;
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(700, 420) };
            box.AddThemeConstantOverride("separation", 8);
            _intro = Kit.Text("");
            _intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            box.AddChild(_intro);
            _list = new Tree { Columns = 3, HideRoot = true, ColumnTitlesVisible = true, SelectMode = Tree.SelectModeEnum.Row, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            _list.SetColumnTitle(0, _tr("Version"));
            _list.SetColumnTitle(1, _tr("From"));
            _list.SetColumnTitle(2, _tr("Atlas has it"));
            _list.SetColumnExpand(0, false);
            _list.SetColumnCustomMinimumWidth(0, 130);
            _list.SetColumnExpand(2, false);
            _list.SetColumnCustomMinimumWidth(2, 220);
            _list.ItemSelected += SyncButtons;
            _list.ItemActivated += () => Use();
            box.AddChild(_list);
            var buttons = new HFlowContainer();
            buttons.AddThemeConstantOverride("h_separation", 8);
            _use = Kit.Button(_tr("Use this one"), _tr("Use the selected version for this slot's seed (downloaded first if Atlas doesn't have it)."), Use);
            _find = Kit.Button(_tr("Try to find it"), _tr("Atlas downloads the versions it hasn't tried, newest first, checks each and stops at the first that matches the seed."), Find);
            _choose = Kit.Button(_tr("Choose a file…"), _tr("Use an apworld file you have, such as the one the seed's host shared."), ChooseFile);
            _addProject = Kit.Button(_tr("Add the host's project…"), _tr("Paste the GitHub link of the project the seed's host used."), AddProject);
            _lookUp = Kit.Button(_tr("Look up releases on GitHub"), _tr("Reads the projects' release lists (nothing is downloaded)."), () => Load(askGitHub: true));
            foreach (var b in new[] { _use, _find, _choose, _addProject, _lookUp }) buttons.AddChild(b);
            box.AddChild(buttons);
            var statusRow = new HBoxContainer();
            statusRow.AddThemeConstantOverride("separation", 8);
            _progress = new ProgressBar { Indeterminate = true, Visible = false, CustomMinimumSize = new Vector2(120, 0), ShowPercentage = false, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            statusRow.AddChild(_progress);
            _status = Kit.Muted("");
            _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            statusRow.AddChild(_status);
            box.AddChild(statusRow);
            AddChild(box);
            // Closing stops what's running; the window goes.
            Confirmed += Close;
            Canceled += Close;
            CloseRequested += Close;
            UpdateIntro();
        }

        private void Close()
        {
            _running?.Cancel();
            QueueFree();
        }

        private static string Short(string? checksum) => string.IsNullOrEmpty(checksum) ? "?" : checksum.Substring(0, Math.Min(8, checksum.Length));

        private void UpdateIntro()
        {
            string installed = _slot.InstalledWorldVersion is { Length: > 0 } v ? _tr(" (version {0})").Replace("{0}", v) : "";
            _intro.Text = _tr("{slot} plays {game}. Its seed was made with a version of the apworld whose data is {seed}; the one in the engine{installed} differs, so logic may be off. Pick the version the seed's host used, or let Atlas try them. Only {slot}'s logic uses it; your own Archipelago isn't changed.")
                .Replace("{slot}", _slot.SlotName).Replace("{game}", _game).Replace("{seed}", Short(_seed)).Replace("{installed}", installed);
        }

        private void SetBusy(string? status)
        {
            bool busy = status != null;
            _progress.Visible = busy;
            if (status != null) _status.Text = status;
            foreach (var b in new[] { _use, _find, _choose, _addProject, _lookUp }) b.Disabled = busy;
            if (!busy) SyncButtons();
        }

        private void SyncButtons()
        {
            if (_progress.Visible) return;
            _use.Disabled = Selected() == null;
            _find.Disabled = !_choices.Any(c => !c.Downloaded && c.Url != null) || !_repos.Any();
        }

        private ApworldChoice? Selected()
        {
            var item = _list.GetSelected();
            return item != null && item.GetMetadata(0).VariantType == Variant.Type.Int && item.GetMetadata(0).AsInt32() is int i && i >= 0 && i < _choices.Count ? _choices[i] : null;
        }

        /// <summary>
        /// Fills the list: what Atlas already has at once; the projects' releases too once GitHub may be asked (it asks,
        /// when <paramref name="askGitHub"/>, through the usual permission).
        /// </summary>
        private void Load(bool askGitHub)
        {
            Show(new List<PublishedApworld>());
            bool allowed = Permissions.IsAllowed(_settings, Permissions.GitHubLookups);
            _lookUp.Visible = !allowed;
            if (!allowed && !askGitHub)
            {
                _status.Text = _tr("Atlas hasn't asked GitHub for this game's releases. \"Look up releases on GitHub\" reads the release lists; nothing is downloaded until you choose.");
                return;
            }
            PermissionDialog.Ask(this, _settings, Permissions.GitHubLookups, null, _tr("For {0}: the releases of the {1} apworld.").Replace("{0}", _slot.SlotName).Replace("{1}", _game), allowed2 =>
            {
                if (!allowed2 || !IsInstanceValid(this)) return;
                _lookUp.Visible = false;
                Async.Fire(LoadReleasesAsync(), $"listing the {_game} apworld's releases");
            });
        }

        private async Task LoadReleasesAsync()
        {
            SetBusy(_tr("Finding where {0} versions are published…").Replace("{0}", _game));
            var cts = _running = new CancellationTokenSource();
            var problems = new List<string>();
            List<ApworldVersion> versions;
            try
            {
                _repos = await Task.Run(() => ApworldSources.ReposForAsync(_settings, _install, _game, line => AP_Atlas.Core.Logger.LogDebug(line), cts.Token));
                string? apworldName = ApworldSources.Find(_game)?.Apworld ?? ApworldSources.InstalledCopies(_install, _game).Select(c => System.IO.Path.GetFileNameWithoutExtension(c.File)).FirstOrDefault();
                versions = await Task.Run(() => ApworldSources.VersionsAsync(_game, _repos.Select(r => r.Repo), apworldName, cts.Token, problems));
            }
            catch (OperationCanceledException) { return; }
            if (!IsInstanceValid(this)) return;
            SetBusy(null);
            Show(versions.Select(v => new PublishedApworld(v.Version, "github.com/" + v.Repo, v.Url, v.Sha256)).ToList());
            _status.Text = _repos.Count == 0
                ? _tr("Atlas doesn't know where {0} versions are published. Add the project the seed's host used, or choose their file.").Replace("{0}", _game)
                : problems.Count > 0 && versions.Count == 0
                    ? _tr("GitHub couldn't be read just now ({0}). Try again in a while.").Replace("{0}", problems[0])
                    : _tr("{0} versions from {1}.").Replace("{0}", versions.Count.ToString()).Replace("{1}", string.Join(", ", _repos.Select(r => r.Display)));
        }

        private void Show(List<PublishedApworld> published)
        {
            var installed = ApworldSources.InstalledCopies(_install, _game).Select(c => c.Sha256);
            _choices = ApworldChoices.Compose(published, ApworldSources.IdentifiedFor(_game), installed, _seed);
            _list.Clear();
            var root = _list.CreateItem();
            for (int i = 0; i < _choices.Count; i++)
            {
                var c = _choices[i];
                var row = _list.CreateItem(root);
                row.SetText(0, c.Version);
                row.SetMetadata(0, i);
                row.SetText(1, c.Source);
                string has = c.MatchesSeed ? _tr("✔ matches this seed") : c.Installed ? _tr("in the engine") : c.Downloaded ? _tr("downloaded") : "";
                row.SetText(2, has);
                if (c.MatchesSeed) row.SetCustomColor(2, ThemeColors.Success);
                row.SetTooltipText(0, c.Downloaded ? c.LocalFile : c.Url ?? "");
            }
            if (_choices.Count > 0) _list.GetRoot().GetFirstChild()?.Select(0);
            SyncButtons();
        }

        // ---- Use this one ----

        private void Use()
        {
            var choice = Selected();
            if (choice == null) return;
            if (choice.Downloaded)
            {
                Async.Fire(UseFileAsync(choice.LocalFile!, choice.Checksum, choice.Version, choice.Source), $"using {_game} {choice.Version}");
                return;
            }
            string? repo = ApworldSources.ParseRepo(choice.Url ?? "");
            WithTrust(repo, () => Async.Fire(DownloadAndUseAsync(choice), $"downloading {_game} {choice.Version}"));
        }

        /// <summary>Downloads only from a project the user trusts: asks once (with the reason), and remembers a yes.</summary>
        private void WithTrust(string? repo, Action go)
        {
            if (repo == null || ApworldSources.IsRepoApproved(_settings, repo))
            {
                go();
                return;
            }
            Dialogs.Confirm(this, _tr("Trust this project?"),
                _tr("Atlas will download the {0} apworld from github.com/{1}. Apworlds are programs that run inside the logic engine, so only continue if you trust this project. Each file is checked against its published SHA-256 when there is one.")
                    .Replace("{0}", _game).Replace("{1}", repo),
                _tr("Trust and download"), () =>
                {
                    ApworldSources.ApproveRepo(_settings, repo);
                    go();
                }, _tr("github.com/{0} is a project I trust").Replace("{0}", repo));
        }

        private async Task DownloadAndUseAsync(ApworldChoice choice)
        {
            SetBusy(_tr("Downloading {0} {1}…").Replace("{0}", _game).Replace("{1}", choice.Version));
            var cts = _running = new CancellationTokenSource();
            string file;
            try
            {
                string? apworldName = ApworldSources.Find(_game)?.Apworld;
                var version = new ApworldVersion { Version = choice.Version, Url = choice.Url, Sha256 = choice.Sha256, Repo = ApworldSources.ParseRepo(choice.Url ?? "") };
                file = await Task.Run(() => ApworldSources.DownloadAsync(_game, apworldName, version, line => AP_Atlas.Core.Logger.LogDebug(line), cts.Token));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!IsInstanceValid(this)) return;
                SetBusy(null);
                _status.Text = _tr("Couldn't download it: {0}").Replace("{0}", ex.Message);
                return;
            }
            if (!IsInstanceValid(this)) return;
            await UseFileAsync(file, null, choice.Version, choice.Source);
        }

        /// <summary>Uses a file Atlas has: identified first if it isn't known yet; one whose data differs from the seed's is used only when the user says so.</summary>
        private async Task UseFileAsync(string file, string? checksum, string version, string source)
        {
            if (string.IsNullOrEmpty(checksum))
            {
                SetBusy(_tr("Checking {0} {1}…").Replace("{0}", _game).Replace("{1}", version));
                var cts = _running = new CancellationTokenSource();
                (string Game, string Checksum, string WorldVersion, string Error) id;
                try { id = await Task.Run(() => ApworldSources.IdentifyAsync(_install, file, _game, cts.Token)); }
                catch (OperationCanceledException) { return; }
                if (!IsInstanceValid(this)) return;
                SetBusy(null);
                if (id.Error != null || id.Checksum == null)
                {
                    _status.Text = _tr("That version can't be loaded in this engine: {0}").Replace("{0}", (id.Error ?? "").Trim().Split('\n').Last());
                    return;
                }
                ApworldSources.RememberIdentified(_game, id.Checksum, version, file, source);
                checksum = id.Checksum;
            }
            bool matches = string.Equals(checksum, _seed, StringComparison.OrdinalIgnoreCase);
            if (matches)
            {
                Choose(file, checksum!, version, source, true);
                return;
            }
            Dialogs.Confirm(this, _tr("Not the seed's data"),
                _tr("{0} {1}'s data ({2}) differs from the seed's ({3}). Logic may be off with it. Use it anyway?")
                    .Replace("{0}", _game).Replace("{1}", version).Replace("{2}", Short(checksum)).Replace("{3}", Short(_seed)),
                _tr("Use it anyway"), () => Choose(file, checksum!, version, source, false));
            Show(_choices.Where(c => c.Url != null).Select(c => new PublishedApworld(c.Version, c.Source, c.Url, c.Sha256)).ToList());
        }

        private void Choose(string file, string checksum, string version, string source, bool matches)
        {
            _slot.UseApworld(file, version, source, matches);
            _status.Text = _tr("{0} uses {1} {2} now.").Replace("{0}", _slot.SlotName).Replace("{1}", _game).Replace("{2}", version);
            Close();
        }

        // ---- Try to find it ----

        private void Find()
        {
            int toTry = _choices.Count(c => !c.Downloaded && c.Url != null);
            var untrusted = _repos.Where(r => !r.Approved && !ApworldSources.IsRepoApproved(_settings, r.Repo)).ToList();
            string text = _tr("Atlas will download up to {0} versions it hasn't tried yet, newest first, check each in a throwaway engine process, and stop at the first whose data matches the seed. You can stop it at any time.")
                .Replace("{0}", toTry.ToString());
            if (untrusted.Count > 0)
                text += "\n\n" + _tr("It downloads from {0}, which you haven't trusted yet. Apworlds are programs that run inside the logic engine.").Replace("{0}", string.Join(", ", untrusted.Select(r => r.Display)));
            Dialogs.Confirm(this, _tr("Try to find the seed's version?"), text, _tr("Try them"), () =>
            {
                foreach (var r in untrusted) ApworldSources.ApproveRepo(_settings, r.Repo);
                Async.Fire(FindAsync(toTry, _choices.Count(c => c.Url != null)), $"looking for the {_game} version the seed was made with");
            }, untrusted.Count > 0 ? _tr("I trust these projects") : null);
        }

        /// <param name="toTry">How many versions it may download (shown).</param>
        /// <param name="listed">How many published versions it may look through (those Atlas has are checked at once).</param>
        private async Task FindAsync(int toTry, int listed)
        {
            int tried = 0;
            SetBusy(_tr("Trying the versions, newest first…"));
            var cts = _running = new CancellationTokenSource();
            (ApworldVersion Version, string File) found;
            try
            {
                found = await Task.Run(() => ApworldSources.FindMatchingAsync(_install, _game, _seed, _repos.Select(r => r.Repo), line =>
                {
                    AP_Atlas.Core.Logger.LogDebug(line);
                    // One line per version tried ("  0.2.4 (owner/project): data 1a2b3c4d").
                    if (line.StartsWith("  ", StringComparison.Ordinal) && line.Contains(':'))
                    {
                        int n = Interlocked.Increment(ref tried);
                        Ui.Defer(this, () => _status.Text = _tr("Trying {0} of {1}: {2}").Replace("{0}", n.ToString()).Replace("{1}", toTry.ToString()).Replace("{2}", line.Trim()));
                    }
                }, cts.Token, Math.Max(1, listed)));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!IsInstanceValid(this)) return;
                SetBusy(null);
                _status.Text = _tr("Couldn't look: {0}").Replace("{0}", ex.Message);
                return;
            }
            if (!IsInstanceValid(this)) return;
            SetBusy(null);
            if (found.File != null)
            {
                Choose(found.File, _seed, found.Version?.Version ?? "?", found.Version?.Url != null ? ApworldSources.SourceKey(found.Version.Url) : "");
                return;
            }
            Show(_choices.Where(c => c.Url != null).Select(c => new PublishedApworld(c.Version, c.Source, c.Url, c.Sha256)).ToList());
            _status.Text = _tr("None of the published versions matches this seed. It likely came from another project, or an unreleased build: add the host's project, or choose their file.");
        }

        private void Choose(string file, string checksum, string version, string source) => Choose(file, checksum, version, source, true);

        // ---- Choose a file / add a project ----

        private void ChooseFile()
        {
            var dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[] { "*.apworld ; Archipelago world" },
                UseNativeDialog = true,
                Title = _tr("The {0} apworld the seed's host used").Replace("{0}", _game)
            };
            dialog.FileSelected += path =>
            {
                dialog.QueueFree();
                Async.Fire(KeepFileAsync(path), $"checking the {_game} apworld you chose");
            };
            dialog.Canceled += dialog.QueueFree;
            AddChild(dialog);
            dialog.PopupCentered(new Vector2I(900, 600));
        }

        private async Task KeepFileAsync(string path)
        {
            SetBusy(_tr("Checking that file…"));
            var cts = _running = new CancellationTokenSource();
            (string File, string Checksum, string Version, string Problem) kept;
            try { kept = await Task.Run(() => ApworldSources.KeepUserFileAsync(_install, path, _game, cts.Token)); }
            catch (OperationCanceledException) { return; }
            if (!IsInstanceValid(this)) return;
            SetBusy(null);
            if (kept.Problem != null)
            {
                _status.Text = _tr("That file can't be used: {0}.").Replace("{0}", kept.Problem);
                return;
            }
            await UseFileAsync(kept.File, kept.Checksum, kept.Version ?? System.IO.Path.GetFileNameWithoutExtension(path), "your file");
        }

        private void AddProject()
        {
            Dialogs.Prompt(this, _tr("The host's project"),
                _tr("Paste the GitHub link of the project that publishes the {0} apworld the seed's host used (its repository or releases page). Atlas reads its release list and trusts it for {0}.").Replace("{0}", _game),
                "", "https://github.com/owner/project/releases", _tr("Add"), link => Async.Fire(AddProjectAsync(link), $"adding a source of {_game} apworlds"));
        }

        private async Task AddProjectAsync(string link)
        {
            SetBusy(_tr("Checking that project's releases…"));
            var (repo, problem) = await Task.Run(() => ApworldSources.CheckUserRepoAsync(link, CancellationToken.None));
            if (!IsInstanceValid(this)) return;
            SetBusy(null);
            if (repo == null)
            {
                _status.Text = _tr("Couldn't add it: {0}.").Replace("{0}", problem);
                return;
            }
            // Main thread: the settings change here.
            ApworldSources.AddUserRepo(_settings, _game, repo);
            await LoadReleasesAsync();
        }
    }
}
