#nullable disable
using Godot;
using System;
using System.Collections.Generic;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Packets;
using Archipelago.MultiClient.Net.Models;
using System.Linq;
using System.Threading.Tasks;
using Color = Godot.Color;

/// <summary>Matching the slot's apworld to the version the seed was made with.</summary>
public partial class SlotTrackerControl : MarginContainer
{
    // =====================================================================
    // Using the apworld version the seed was made with
    // =====================================================================

    private Button _accuracyFix, _accuracyChooseApworld, _accuracyAddSource;
    private string _lastAccuracyWarning;

    /// <summary>Raised when this slot's accuracy warning or fix status changes (Properties shows it too).</summary>
    public event Action AccuracyChanged;
    private string _apworldFixStatus;
    private bool _apworldFixRunning;
    private readonly HashSet<string> _apworldFixAttempted = new HashSet<string>();
    private readonly HashSet<string> _apworldOverrideFailed = new HashSet<string>();

    /// <summary>Atlas's cached copy of the seed's apworld version for this slot, if it has one (used in place of the installed copy).</summary>
    private string SeedApworldFile()
    {
        string checksum = ServerChecksumFor(Game);
        if (checksum == null || _apworldOverrideFailed.Contains(checksum)) return null;
        return AP_Atlas.Core.EngineSetup.ApworldSources.CachedFor(Game, checksum)?.File;
    }

    /// <summary>
    /// Gets the apworld version this seed was made with and restarts logic on it, for this slot only (the installed
    /// copy and other slots are untouched). Uses Atlas's cache when it has the version; otherwise looks where the
    /// game's versions are published: the repository the installed copy came from (found by its SHA-256), the one in
    /// the community index, and any the user added. Without interaction it only downloads from trusted repositories.
    /// </summary>
    public void FixApworldVersion(bool interactive) => AP_Atlas.Core.Async.Fire(FixApworldVersionAsync(interactive), $"matching {_slotName}'s apworld to the seed");

    private async Task FixApworldVersionAsync(bool interactive)
    {
        string game = Game, checksum = ServerChecksumFor(game);
        if (checksum == null || _apworldFixRunning || Session == null) return;
        _apworldFixAttempted.Add(checksum);
        if (AP_Atlas.Core.EngineSetup.ApworldSources.CachedFor(game, checksum) != null && !_apworldOverrideFailed.Contains(checksum))
        {
            AppendDebugLog($"Atlas already has the {game} apworld this seed was made with; restarting logic on it.");
            RetryLogicEngine();
            return;
        }
        // Looking on GitHub contacts a site: automatically only once the user allowed it; a button press asks.
        if (!AP_Atlas.Core.Permissions.IsAllowed(_appSettings, AP_Atlas.Core.Permissions.GitHubLookups))
        {
            if (!interactive)
            {
                _apworldFixStatus = "Press \"Fix automatically\" to look up the seed's version on GitHub.";
                SyncAccuracyBanner();
                return;
            }
            AP_Atlas.UI.PermissionDialog.Ask(GetTree().Root, _appSettings, AP_Atlas.Core.Permissions.GitHubLookups, null,
                $"For {_slotName}: Atlas looks for the {game} apworld this seed was made with.", allowed =>
                {
                    if (!GodotObject.IsInstanceValid(this)) return;
                    if (allowed) FixApworldVersion(interactive: true);
                    else
                    {
                        _apworldFixStatus = "Not looked up: Atlas didn't contact GitHub.";
                        SyncAccuracyBanner();
                    }
                });
            return;
        }

        // Where are this game's versions published? (Reads release lists only; nothing is downloaded.)
        _apworldFixRunning = true;
        _apworldFixStatus = $"Finding where {game} versions are published…";
        SyncAccuracyBanner();
        var install = _logicEngine.Install;
        List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo> repos;
        try
        {
            repos = await System.Threading.Tasks.Task.Run(() => AP_Atlas.Core.EngineSetup.ApworldSources.ReposForAsync(_appSettings, install, game, AppendDebugLog, System.Threading.CancellationToken.None));
        }
        catch (Exception ex)
        {
            AppendDebugLog("Couldn't look up where the apworld is published: " + ex.Message);
            repos = new List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo>();
        }
        if (!GodotObject.IsInstanceValid(this)) return;
        _apworldFixRunning = false;
        if (repos.Count == 0)
        {
            _apworldFixStatus = $"Atlas doesn't know where {game} versions are published. Add the project's GitHub link, or choose the apworld file the seed's host used.";
            SyncAccuracyBanner();
            return;
        }

        var trusted = repos.Where(r => r.Approved).ToList();
        var untrusted = repos.Where(r => !r.Approved).ToList();
        if (!interactive)
        {
            if (trusted.Count > 0) await SearchSeedApworldAsync(game, checksum, trusted, untrusted);
            else
            {
                _apworldFixStatus = "Fix automatically looks in " + string.Join(" and ", repos.Select(r => $"{r.Display} ({r.Reason})")) + ". You'll be asked to trust them once.";
                SyncAccuracyBanner();
            }
            return;
        }
        if (untrusted.Count == 0)
        {
            await SearchSeedApworldAsync(game, checksum, repos, new List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo>());
            return;
        }
        var dialog = new ConfirmationDialog
        {
            Title = "Use the seed's apworld version",
            DialogText = $"Look for the {game} apworld this seed was made with, and use it for {_slotName}?\n\nAtlas will check versions published in:\n" +
                         string.Join("\n", repos.Select(r => $"  •  {r.Display}: {r.Reason}{(r.Approved ? " (trusted)" : "")}")) +
                         "\n\nOnly this slot's logic uses it: your Archipelago install isn't changed. Each file is checked against its published SHA-256 when one exists.\n\n" +
                         "Apworlds are programs that run inside the logic engine. Only continue if you trust these sources.",
            DialogAutowrap = true,
            MinSize = new Vector2I(600, 0),
            OkButtonText = "Look and use it"
        };
        var trust = new CheckBox { Text = "Trust these from now on (fix future seeds automatically)", ButtonPressed = true };
        dialog.AddChild(trust);
        dialog.Confirmed += () =>
        {
            if (trust.ButtonPressed) foreach (var r in untrusted) AP_Atlas.Core.EngineSetup.ApworldSources.ApproveRepo(_appSettings, r.Repo);
            dialog.QueueFree();
            SearchSeedApworld(game, checksum, repos, new List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo>());
        };
        dialog.Canceled += () => dialog.QueueFree();
        GetTree().Root.AddChild(dialog);
        dialog.PopupCentered();
    }

    private void SearchSeedApworld(string game, string checksum, List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo> repos, List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo> notSearched) => AP_Atlas.Core.Async.Fire(SearchSeedApworldAsync(game, checksum, repos, notSearched), $"looking for the {game} version this seed was made with");

    private async Task SearchSeedApworldAsync(string game, string checksum, List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo> repos, List<AP_Atlas.Core.EngineSetup.ApworldSources.ApworldRepo> notSearched)
    {
        _apworldFixRunning = true;
        _apworldFixStatus = $"Looking for the {game} version this seed was made with…";
        SyncAccuracyBanner();
        var install = _logicEngine.Install;
        (AP_Atlas.Core.EngineSetup.ApworldVersion Version, string File) found = (null, null);
        string failure = null;
        try
        {
            found = await System.Threading.Tasks.Task.Run(() => AP_Atlas.Core.EngineSetup.ApworldSources.FindMatchingAsync(install, game, checksum, repos.Select(r => r.Repo), line =>
            {
                AppendDebugLog(line);
                string trimmed = line.Trim();
                AP_Atlas.UI.Ui.Defer(this, () =>
                {
                    if (!_apworldFixRunning) return;
                    _apworldFixStatus = $"Looking for the seed's version… {trimmed}";
                    SyncAccuracyBanner();
                });
            }, System.Threading.CancellationToken.None));
        }
        catch (Exception ex) { failure = ex.Message; }
        if (!GodotObject.IsInstanceValid(this)) return;
        _apworldFixRunning = false;
        if (found.File != null)
        {
            _apworldFixStatus = null;
            string from = found.Version?.Url != null ? " from " + AP_Atlas.Core.EngineSetup.ApworldSources.SourceKey(found.Version.Url) : "";
            AP_Atlas.Core.Logger.LogInfo($"[{_slotName}] Found the {game} apworld this seed was made with ({found.Version?.Version}{from}); restarting logic on it.");
            ShowToast?.Invoke($"{_slotName}: using {game} {found.Version?.Version}{from}, the version this seed was made with.", Colors.LimeGreen);
            RetryLogicEngine();
            return;
        }
        _apworldFixStatus = failure != null
            ? $"Couldn't look for the seed's version: {failure}"
            : notSearched.Count > 0
                ? $"Not in the trusted sources. Fix automatically also looks in {string.Join(" and ", notSearched.Select(r => r.Display))}."
                : $"None of the published {game} versions matches this seed. Add the project the seed's host used, or choose their apworld file.";
        if (notSearched.Count > 0) _apworldFixAttempted.Remove(checksum); // let the user widen the search
        SyncAccuracyBanner();
    }

    /// <summary>Adds a GitHub project (pasted link) as a source of this game's apworld, then looks there for the seed's version.</summary>
    public void AddApworldSource()
    {
        string game = Game;
        var dialog = new ConfirmationDialog
        {
            Title = $"Add a source for {game}",
            DialogText = $"Paste the GitHub link of the project that publishes the {game} apworld (its repository or releases page).\n" +
                         "Atlas will trust it and download versions from it to match your seeds.",
            DialogAutowrap = true,
            MinSize = new Vector2I(560, 0),
            OkButtonText = "Add and look"
        };
        var input = new LineEdit { PlaceholderText = "https://github.com/owner/project/releases" };
        dialog.AddChild(input);
        dialog.RegisterTextEnter(input);
        dialog.Confirmed += () => AP_Atlas.Core.Async.Fire(async () =>
        {
            string link = input.Text;
            dialog.QueueFree();
            _apworldFixStatus = "Checking that project's releases…";
            SyncAccuracyBanner();
            var (repo, problem) = await System.Threading.Tasks.Task.Run(() => AP_Atlas.Core.EngineSetup.ApworldSources.CheckUserRepoAsync(link, System.Threading.CancellationToken.None));
            if (!GodotObject.IsInstanceValid(this)) return;
            if (repo == null)
            {
                _apworldFixStatus = "Couldn't add it: " + problem + ".";
                SyncAccuracyBanner();
                return;
            }
            // Back on the main thread: settings change only here.
            AP_Atlas.Core.EngineSetup.ApworldSources.AddUserRepo(_appSettings, game, repo);
            AppendDebugLog($"Added github.com/{repo} as a source of {game} apworlds.");
            _apworldFixAttempted.Remove(ServerChecksumFor(game) ?? "");
            await FixApworldVersionAsync(interactive: true);
        }, $"adding a source of {game} apworlds");
        dialog.Canceled += () => dialog.QueueFree();
        GetTree().Root.AddChild(dialog);
        dialog.PopupCentered();
        input.GrabFocus();
    }

    /// <summary>Uses an apworld file the user picked (e.g. from the seed's host), if it's the seed's version.</summary>
    public void ChooseSeedApworld()
    {
        string game = Game, checksum = ServerChecksumFor(game);
        if (checksum == null) return;
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.apworld ; Archipelago world" },
            UseNativeDialog = true,
            Title = $"The {game} apworld this seed was made with"
        };
        dialog.FileSelected += path => AP_Atlas.Core.Async.Fire(async () =>
        {
            dialog.QueueFree();
            _apworldFixRunning = true;
            _apworldFixStatus = "Checking that file…";
            SyncAccuracyBanner();
            var install = _logicEngine.Install;
            var check = System.Threading.Tasks.Task.Run(() => AP_Atlas.Core.EngineSetup.ApworldSources.AddUserFileAsync(install, path, game, checksum, System.Threading.CancellationToken.None));
            // Copying the file can fail (a full disk): the fix must not stay "running" then.
            try { await check; }
            finally { _apworldFixRunning = false; }
            var (entry, problem) = await check;
            if (!GodotObject.IsInstanceValid(this)) return;
            if (entry != null)
            {
                _apworldFixStatus = null;
                _apworldOverrideFailed.Remove(checksum);
                ShowToast?.Invoke($"{_slotName}: using your {game} apworld, which matches this seed.", Colors.LimeGreen);
                RetryLogicEngine();
                return;
            }
            _apworldFixStatus = $"That file can't be used: {problem}.";
            SyncAccuracyBanner();
        }, $"using the {game} apworld you chose");
        dialog.Canceled += () => dialog.QueueFree();
        GetTree().Root.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    private void SyncLogicViews()
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        SyncAccuracyBanner();
        bool problem = !LogicHidden && !_engineRunning && EngineProblem != null;
        if (_mapTracker != null) _mapTracker.LogicHidden = LogicHidden || !_engineRunning;
        if (_logicTree != null) _logicTree.Visible = !LogicHidden && !problem;
        if (_logicFlaggedOnly != null) _logicFlaggedOnly.Visible = !LogicHidden && !problem;
        if (_logicNotice == null) return;
        _logicNotice.Visible = LogicHidden || problem;
        foreach (Node n in _logicNoticeActions.GetChildren()) n.QueueFree();
        if (LogicHidden)
        {
            _logicNoticeText.Text = "Logic is hidden by race mode.\nChange this under Settings → Race Mode.";
            return;
        }
        if (!problem) return;
        _logicNoticeText.Text = EngineProblemText(EngineProblem);
        void AddButton(string text, string tip, Action action)
        {
            var b = new Button { Text = text, TooltipText = tip };
            b.Pressed += action;
            _logicNoticeActions.AddChild(b);
        }
        switch (EngineProblem.Code)
        {
            case "yaml_needed":
            case "generation_failed":
                AddButton("Link YAML…", "Choose this player's YAML; Atlas remembers it for this slot", PickYaml);
                AddButton("Atlas Engine…", "Open the engine setup", () => OpenEngineSetup?.Invoke());
                break;
            case "ut_disabled":
                break;
            case "restarting":
                AddButton("Restart now", "Start the logic engine again now", RetryLogicEngine);
                break;
            case "no_engine":
            case "world_missing":
                AddButton("Set up Atlas Engine…", "Download and check what logic needs", () => OpenEngineSetup?.Invoke());
                AddButton("Try again", "Start the logic engine again", RetryLogicEngine);
                break;
            default:
                AddButton("Try again", "Start the logic engine again", RetryLogicEngine);
                AddButton("Atlas Engine…", "Open the engine setup and run a health check", () => OpenEngineSetup?.Invoke());
                break;
        }
    }

    private string EngineProblemText(EngineStartError e)
    {
        switch (e.Code)
        {
            case "no_engine":
                return "Logic needs the Atlas Engine.\n" + e.Message + "\nAtlas can download everything it needs (about 50 MB, no installer).";
            case "world_missing":
                return $"{Game} isn't installed in the logic engine.\nAdd its apworld under Atlas Engine → Games.";
            case "yaml_needed":
                return $"{Game} can't rebuild your world from the server's data alone.\nLink this player's YAML (the file used to generate the seed).";
            case "generation_failed":
                return $"Your world couldn't be rebuilt.\n{e.Message}\nLink the YAML used to generate the seed, or check the game's apworld version.";
            case "ut_disabled":
                return $"The author of {Game}'s apworld asked trackers not to compute its logic.\nEverything else in Atlas still works.";
            case "restarting":
                return e.Message;
            default:
                return "The logic engine couldn't start.\n" + e.Message;
        }
    }

    private static string ProblemStatus(EngineStartError e) => e.Code switch
    {
        "no_engine" => "Not Set Up",
        "world_missing" => "Game Not Installed",
        "yaml_needed" => "YAML Needed",
        "generation_failed" => "World Rebuild Failed",
        "ut_disabled" => "Disabled By Game",
        "no_response" => "No Response",
        "crashed" => "Engine Crashed",
        _ => "Engine Error"
    };

    // --- Engine state for the setup window and Properties ---

    /// <summary>Why logic isn't running for this slot (null while it runs or starts).</summary>
    public EngineStartError EngineProblem { get; private set; }

    public bool EngineBooting => _engineBooting;

    /// <summary>How the engine rebuilt this slot's world: which YAML (or none) and whether its locations match the server's.</summary>
    public Newtonsoft.Json.Linq.JObject EngineYamlInfo => _engineRunning ? _logicEngine?.LastYamlInfo : null;

    public Newtonsoft.Json.Linq.JObject EngineVersions => _logicEngine?.LastVersions;

    /// <summary>
    /// Whether the installed apworld's data matches the seed's (checksums of names, ids and groups; null when either
    /// side didn't report one). It can't see rule-only changes, which the location check and seed tests cover.
    /// </summary>
    public bool? ApworldMatchesSeed { get; private set; }

    public string InstalledWorldVersion => _engineRunning ? _logicEngine?.LastWorldVersion : null;

    /// <summary>True when this slot runs on Atlas's cached copy of the seed's apworld version instead of the installed one.</summary>
    public bool UsingSeedApworld => _engineRunning && _logicEngine?.LastApworldOverride?["used"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && (bool)_logicEngine.LastApworldOverride["used"];

    /// <summary>Opens the Atlas Engine setup window (set by MainTrackerWindow).</summary>
    public Action OpenEngineSetup { get; set; }

    private bool _engineBooting;

    /// <summary>The player YAML linked to this slot, if the file still exists.</summary>
    public string LinkedYamlPath =>
        _appSettings.SlotYamlPaths != null && _appSettings.SlotYamlPaths.TryGetValue(AnnotationKey, out var p) && System.IO.File.Exists(p) ? p : null;

    /// <summary>The linked YAML as stored (even if the file has since moved).</summary>
    public string LinkedYamlSetting =>
        _appSettings.SlotYamlPaths != null && _appSettings.SlotYamlPaths.TryGetValue(AnnotationKey, out var p) ? p : null;

    /// <summary>Links (or with null, unlinks) a player YAML to this slot and restarts its logic with it.</summary>
    public void LinkYaml(string path)
    {
        _appSettings.SlotYamlPaths ??= new Dictionary<string, string>();
        if (string.IsNullOrEmpty(path)) _appSettings.SlotYamlPaths.Remove(AnnotationKey);
        else _appSettings.SlotYamlPaths[AnnotationKey] = path;
        DataManager.SaveSettings(_appSettings);
        RetryLogicEngine();
    }

    public void PickYaml()
    {
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.yaml, *.yml ; Archipelago player YAML" },
            UseNativeDialog = true,
            Title = $"YAML for {_slotName} ({Game})"
        };
        // Start where the user last picked a YAML (or their chosen install's Players folder). Atlas never searches for YAMLs.
        string linkedFolder = LinkedYamlSetting != null ? System.IO.Path.GetDirectoryName(LinkedYamlSetting) : null;
        string chosenPlayers = string.IsNullOrWhiteSpace(_appSettings.ArchipelagoInstallationPath) ? null : System.IO.Path.Combine(_appSettings.ArchipelagoInstallationPath, "Players");
        string start = new[] { _appSettings.LastYamlFolder, linkedFolder, chosenPlayers }.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && System.IO.Directory.Exists(d));
        if (start != null) dialog.CurrentDir = start;
        dialog.FileSelected += path =>
        {
            dialog.QueueFree();
            _appSettings.LastYamlFolder = System.IO.Path.GetDirectoryName(path) ?? "";
            var check = AP_Atlas.Core.YamlExclusions.Read(path, Game, _slotName);
            if (check.Error != null && !check.Error.StartsWith("Several"))
            {
                ShowToast?.Invoke($"That YAML can't be used for {_slotName}: {check.Error}", Colors.Salmon);
                return;
            }
            LinkYaml(path);
            ShowToast?.Invoke($"Linked {System.IO.Path.GetFileName(path)} to {_slotName}. Restarting logic…", Colors.Gray);
        };
        dialog.Canceled += () => dialog.QueueFree();
        GetTree().Root.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    /// <summary>Starts this slot's logic engine again (after setup, a new YAML, or a failure).</summary>
    public void RetryLogicEngine()
    {
        if (!GodotObject.IsInstanceValid(this) || Session == null || _engineBooting) return;
        if (_logicBusy)
        {
            // Let the running evaluation finish first, so it can't append steps from the old engine.
            GetTree().CreateTimer(0.5).Timeout += RetryLogicEngine;
            return;
        }
        if (_engineRunning)
        {
            _logicEngine.StopEngine();
            _engineRunning = false;
        }
        ResetLogicState();
        _recentEngineFailures.Clear(); // the user asked: give it a fresh set of automatic restarts
        InitializeLogicEngine();
    }

    /// <summary>The engine this slot uses is about to be updated: stop logic now; it resumes when the update is done.</summary>
    private void OnEnginePauseRequested(string root)
    {
        // Stop the process right away (any thread): setup waits for it to exit before replacing files.
        var install = _logicEngine?.Install;
        if (install == null || !string.Equals(System.IO.Path.GetFullPath(install.Root ?? "").TrimEnd('\\'), System.IO.Path.GetFullPath(root ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        _logicEngine.StopEngine();
        AP_Atlas.UI.Ui.Defer(this, () =>
        {
            if (Session == null) return;
            bool wasActive = _engineRunning || _engineBooting;
            _engineRunning = false;
            ResetLogicState();
            if (wasActive || EngineProblem != null)
            {
                EngineProblem = new EngineStartError { Code = "paused", Message = "Logic is paused while the Atlas Engine is updated. It resumes by itself when the update finishes." };
                SetStatus("Paused (engine update)");
                SyncLogicViews();
                RaiseStateChanged();
            }
        });
    }

    /// <summary>Setup finished or changed: a slot that was waiting on the engine tries again.</summary>
    private void OnEngineChanged() => AP_Atlas.UI.Ui.Defer(this, () =>
    {
        if (Session == null || _engineRunning || _engineBooting || EngineProblem == null) return;
        if (EngineProblem.Code is "no_engine" or "world_missing" or "no_response" or "crashed" or "error" or "paused") InitializeLogicEngine();
    });

    private void OnRaceRulesChanged()
    {
        _raceStateAnnounced = false;
        ApplyRaceRules();
    }

    /// <summary>Re-evaluates the hint table (e.g. after another slot's logic changed).</summary>
    public void RefreshHints() => _hintTracker?.Refresh();

    private void RaiseStateChanged()
    {
        if (_mapTracker != null && Session != null)
        {
            _mapTracker.UpdateLogicColors(_knownReachableLocations, Session.Locations.AllLocationsChecked, Model.HintedLocations);
        }
        _hintTracker?.Refresh();
        StateChanged?.Invoke();
    }

    private void RefreshAllViews()
    {
        UpdateItemHistoryUI();
        UpdateKeyItemsUI();
        RenderLogicTree();
        RaiseStateChanged();
    }
}
