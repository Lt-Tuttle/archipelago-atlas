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

    private Button _accuracyFix;
    private string _lastAccuracyWarning;

    /// <summary>Raised when this slot's accuracy warning or fix status changes (Properties shows it too).</summary>
    public event Action AccuracyChanged;
    private readonly HashSet<string> _apworldFixAttempted = new HashSet<string>();

    /// <summary>Where Atlas's own look for the seed's version stands (the connect-time rule's tries).</summary>
    private enum SeedVersionSearch { NotTried, Trying, NoneMatched }

    private SeedVersionSearch _seedVersionSearch;
    // The seed (its data checksum) the search state is about: another seed starts afresh.
    private string _seedVersionSearchFor;
    private System.Threading.CancellationTokenSource _seedVersionTries;

    /// <summary>
    /// The connect-time version rule (SeedVersionRule): the newest installed version runs; only when the seed was made with
    /// another version and logic doesn't work with it does Atlas look for the seed's version.
    /// </summary>
    public bool SeedVersionWanted => Model.Logic.SeedVersionWanted;

    /// <summary>How far logic can be trusted as Atlas checks it (the one gate every view uses).</summary>
    public AP_Atlas.Core.Games.LogicAccuracy LogicAccuracy => Model.Logic.Accuracy;

    private bool TryingSeedVersions => _seedVersionSearch == SeedVersionSearch.Trying && _seedVersionSearchFor == ServerChecksumFor(Game);

    /// <summary>What Atlas is doing about the seed's version, in plain words (null unless the rule fired).</summary>
    public string SeedVersionNote
    {
        get
        {
            if (!SeedVersionWanted) return null;
            var state = _seedVersionSearchFor == ServerChecksumFor(Game) ? _seedVersionSearch : SeedVersionSearch.NotTried;
            return state switch
            {
                SeedVersionSearch.Trying => Tr("Atlas is trying the seed's version…"),
                SeedVersionSearch.NoneMatched => Tr("None of the versions Atlas knows from projects you trust is the seed's: choose it."),
                _ => Tr("Choose the seed's version to fix it.")
            };
        }
    }

    /// <summary>Stops a search for the seed's version still going (a new one, the user's choice, or the slot ending).</summary>
    private void StopSeedVersionTries() => _seedVersionTries?.Cancel();

    private void SetSeedVersionSearch(SeedVersionSearch state, string checksum)
    {
        _seedVersionSearch = state;
        _seedVersionSearchFor = checksum;
        SyncLogicViews();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// After a start, or a start that failed: when the rule fires and "Use each seed's apworld version automatically" is on,
    /// Atlas looks for the seed's version, once per seed for this slot.
    /// </summary>
    private void CheckSeedVersion()
    {
        string checksum = ServerChecksumFor(Game);
        if (!SeedVersionWanted || !_appSettings.AutoFixApworldVersions || checksum == null || _apworldFixAttempted.Contains(checksum)) return;
        FixApworldVersion(interactive: false);
    }

    /// <summary>
    /// Matches the slot's apworld to the version its seed was made with. When Atlas has that version (identified before, or
    /// chosen for this slot), logic restarts on it at once, for this slot only. Asked by the user (interactive), the version
    /// picker opens. Otherwise Atlas tries up to three versions it already knows from projects the user trusts, newest first,
    /// asking GitHub nothing (TrySeedVersionsAsync); the picker is the way on when none is the seed's.
    /// </summary>
    public void FixApworldVersion(bool interactive)
    {
        string game = Game, checksum = ServerChecksumFor(game);
        if (checksum == null || Session == null) return;
        _apworldFixAttempted.Add(checksum);
        if (AP_Atlas.Core.EngineSetup.ApworldSources.CachedFor(game, checksum) != null && !Model.Logic.SeedApworldFailed(checksum))
        {
            AppendDebugLog($"Atlas already has the {game} apworld this seed was made with; restarting logic on it.");
            RetryLogicEngine();
            return;
        }
        if (interactive)
        {
            OpenApworldPicker();
            return;
        }
        AP_Atlas.Core.Async.Fire(TrySeedVersionsAsync(game, checksum), $"trying {game} versions for {_slotName}'s seed");
    }

    /// <summary>
    /// The silent tries (ApworldSources.FindSeedVersionAsync): each downloaded and loaded in a throwaway engine process until
    /// one's data is the seed's, which is used for this slot only. The banner says Atlas is trying meanwhile; a card says
    /// which version was found, or that none was (the banner's "Choose the version…" opens the picker).
    /// </summary>
    private async System.Threading.Tasks.Task TrySeedVersionsAsync(string game, string checksum)
    {
        var install = Model.Logic.Engine?.Install;
        if (install == null || !install.CanLaunch)
        {
            SetSeedVersionSearch(SeedVersionSearch.NoneMatched, checksum);
            return;
        }
        // Read on the main thread (the settings change there); the rest runs off it.
        var trusted = (_appSettings.ApprovedApworldSources ?? new List<string>()).ToList();
        var added = _appSettings.ExtraApworldRepos != null && _appSettings.ExtraApworldRepos.TryGetValue(game, out var extra) ? extra.ToList() : new List<string>();
        bool useCached = !Model.Logic.SeedApworldFailed(checksum);
        string slot = _slotName;
        StopSeedVersionTries();
        var cts = _seedVersionTries = new System.Threading.CancellationTokenSource();
        SetSeedVersionSearch(SeedVersionSearch.Trying, checksum);
        (AP_Atlas.Core.EngineSetup.ApworldVersion Version, string File) found = (null, null);
        try
        {
            // No ConfigureAwait(false): what follows touches the slot's views, on Godot's main thread.
            found = await System.Threading.Tasks.Task.Run(() => AP_Atlas.Core.EngineSetup.ApworldSources.FindSeedVersionAsync(trusted, added, install, game, checksum, useCached,
                line => AP_Atlas.Core.Logger.LogDebug($"[{slot}] {line}"), cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AP_Atlas.Core.Logger.LogWarning($"[{slot}] Couldn't look for the {game} version this seed was made with: {ex.Message}");
        }
        if (!GodotObject.IsInstanceValid(this) || _ended || cts.IsCancellationRequested || ServerChecksumFor(game) != checksum) return;
        if (found.File != null)
        {
            var v = found.Version;
            string source = v?.Repo != null ? "github.com/" + v.Repo : v?.Url != null ? AP_Atlas.Core.EngineSetup.ApworldSources.SourceKey(v.Url) : Tr("Atlas's copy");
            UseApworld(found.File, v?.Version ?? "?", source, matchesSeed: true, found: true);
            return;
        }
        SetSeedVersionSearch(SeedVersionSearch.NoneMatched, checksum);
        ShowActionToast?.Invoke(Tr("{slot}: this seed was made with another version of {game}, and logic doesn't match it. Atlas couldn't find that version among the ones it knows from projects you trust.")
            .Replace("{slot}", _slotName).Replace("{game}", game), AP_Atlas.Core.ThemeColors.Warning, Tr("Choose the version…"), OpenApworldPicker);
    }

    /// <summary>The version picker for this slot's seed.</summary>
    public void OpenApworldPicker()
    {
        if (!GodotObject.IsInstanceValid(this) || Session == null) return;
        if (Model.Logic.Engine?.Install == null || !Model.Logic.Engine.Install.CanLaunch)
        {
            ShowToast?.Invoke("The Atlas Engine isn't set up, so Atlas can't check apworld versions yet.", AP_Atlas.Core.ThemeColors.Warning);
            return;
        }
        AP_Atlas.UI.ApworldPickerDialog.Open(GetTree().Root, _appSettings, this, AP_Atlas.UI.Kit.Translate);
    }

    /// <summary>
    /// Uses an apworld file for this slot's seed (the version picker's choice): kept for the slot and the seed, so a new
    /// seed asks again; the multiworld's engine processes start afresh (one that loaded another version of the world can't
    /// load this one) and this slot's logic restarts on it. Nothing in the user's own Archipelago changes.
    /// </summary>
    public void UseApworld(string file, string version, string source, bool matchesSeed, bool found = false)
    {
        string game = Game, checksum = ServerChecksumFor(game);
        if (checksum == null) return;
        // The user's choice (or what Atlas found) ends any search still going.
        StopSeedVersionTries();
        _seedVersionSearch = SeedVersionSearch.NotTried;
        _appSettings.SlotApworlds ??= new Dictionary<string, SlotApworldChoice>();
        _appSettings.SlotApworlds[AnnotationKey] = new SlotApworldChoice { Game = game, SeedChecksum = checksum, File = file, Version = version, Source = source, MatchesSeed = matchesSeed };
        DataManager.SaveSettings(_appSettings);
        Model.Logic.RetrySeedApworld(checksum);
        EnginePools.Retire(ProfileId);
        AP_Atlas.Core.Logger.LogInfo($"[{_slotName}] Using {game} {version} ({source}) for this seed{(matchesSeed ? ", the version it was made with" : ", chosen though its data differs from the seed's")}{(found ? "; Atlas found it by itself" : "")}.");
        ShowToast?.Invoke(found
                ? Tr("{slot}: this seed was made with {game} {version}. Atlas found it and uses it for this slot (your Archipelago isn't changed). Restarting logic…")
                    .Replace("{slot}", _slotName).Replace("{game}", game).Replace("{version}", version)
                : $"{_slotName}: using {game} {version}. Restarting logic…",
            matchesSeed ? AP_Atlas.Core.ThemeColors.Success : AP_Atlas.Core.ThemeColors.Warning);
        RetryLogicEngine();
        SyncAccuracyBanner();
    }

    /// <summary>The version chosen for this slot's seed, if any (Properties and the Games page say which).</summary>
    public SlotApworldChoice ChosenApworld =>
        _appSettings.SlotApworlds != null && _appSettings.SlotApworlds.TryGetValue(AnnotationKey, out var c) && c.SeedChecksum == ServerChecksumFor(Game) ? c : null;

    private string _shownEmptyState;

    private void SyncLogicViews()
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        SyncAccuracyBanner();
        bool problem = !LogicHidden && !Model.Logic.Running && EngineProblem != null;
        if (_mapTracker != null)
            _mapTracker.Logic = LogicHidden ? AP_Atlas.UI.MapTrackerControl.LogicShown.Hidden
                : !Model.Logic.Running ? AP_Atlas.UI.MapTrackerControl.LogicShown.NotRunning
                : AP_Atlas.UI.MapTrackerControl.LogicShown.Running;
        // A lasting problem lists every check left as a checklist under its notice; a passing one (a restart) shows the notice alone.
        if (_logicTree != null) _logicTree.Visible = !LogicHidden && (!problem || LastingProblem);
        if (_logicNotice != null) _logicNotice.SizeFlagsVertical = problem && LastingProblem ? SizeFlags.ShrinkBegin : SizeFlags.ExpandFill;
        if (problem && LastingProblem) RenderLogicTree();
        // Key Items repeats the empty state only when it changes (this runs on every logic change; a burst brings many).
        string emptyState = _progressionTracker != null && _progressionTracker.ShowingEmptyState ? LogicEmptyState("") : null;
        if (emptyState != null && emptyState != _shownEmptyState) _progressionTracker.RefreshMarkers();
        _shownEmptyState = emptyState;
        if (_logicFlaggedOnly != null) _logicFlaggedOnly.Visible = !LogicHidden && !problem;
        if (_logicShowChecked != null) _logicShowChecked.Visible = !LogicHidden && !problem;
        if (_logicShowHidden != null && (LogicHidden || problem)) _logicShowHidden.Visible = false;
        if (_logicNotice == null) return;
        _logicNotice.Visible = LogicHidden || problem;
        foreach (Node n in _logicNoticeActions.GetChildren()) n.QueueFree();
        if (LogicHidden)
        {
            _logicNoticeText.Text = "Logic is hidden by race mode.\nChange this under Settings → Race Mode.";
            return;
        }
        if (!problem) return;
        // The setup window adds this game next (its Add games page): the banner says so, with nothing to press meanwhile.
        if (SetupQuietText() is { } quiet)
        {
            _logicNoticeText.Text = quiet;
            return;
        }
        _logicNoticeText.Text = EngineProblemText(EngineProblem);
        // A start that failed on a seed made with another version: say so, and what Atlas is doing about it.
        if (SeedVersionNote is { } versionNote)
            _logicNoticeText.Text += "\n" + Tr("This seed was made with another version of {0}.").Replace("{0}", Game) + " " + versionNote;
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
                if (SeedVersionWanted && !TryingSeedVersions)
                    AddButton(Tr("Choose the version…"), Tr("Lists the game's releases: pick the one the seed's host used, choose their file, or let Atlas try them (for this slot only; your install isn't changed)"),
                        () => FixApworldVersion(interactive: true));
                AddButton("Link YAML…", "Choose this player's YAML; Atlas remembers it for this slot", PickYaml);
                AddButton("Atlas Engine…", "Open the engine setup", () => OpenEngineSetup?.Invoke());
                break;
            case "ut_disabled":
                break;
            case "restarting":
                AddButton("Restart now", "Start the logic engine again now", RetryLogicEngine);
                break;
            case "no_engine":
                AddButton("Set up Atlas Engine…", "Download and check what logic needs", () => OpenEngineSetup?.Invoke());
                AddButton("Try again", "Start the logic engine again", RetryLogicEngine);
                break;
            case "world_download":
                AddButton("Get the game's world…", "Downloads Archipelago's release once (" + AP_Atlas.Core.EngineSetup.AtlasEngine.WorldsZipSize + " from GitHub) and unpacks this game's world from it; asks first", GetGameWorld);
                break;
            case "world_missing":
                // The engine runs; this game's logic is what's missing. Add games installs it (the game ticked) when Atlas knows
                // where it comes from; else the game's page, where its project is added.
                if (AP_Atlas.Core.EngineSetup.ApworldSources.LogicSourceKnown(_appSettings, Game))
                    AddButton(Tr("Add this game…"), Tr("Opens Add games with this game ticked: Atlas says what it downloads, installs its logic (and a map pack), and logic starts again by itself"), () => AddGames?.Invoke(Game));
                else
                    AddButton("Open the Games page", "Add the game's project on its page, then add the game", () => OpenGamesPageFor?.Invoke(Game));
                AddButton("Try again", "Start the logic engine again", RetryLogicEngine);
                break;
            default:
                AddButton("Try again", "Start the logic engine again", RetryLogicEngine);
                AddButton("Atlas Engine…", "Open the engine setup and run a health check", () => OpenEngineSetup?.Invoke());
                break;
        }
    }

    /// <summary>
    /// What a logic view says while it has nothing to list: hidden by race mode, the engine's problem, that it's starting,
    /// or <paramref name="whileRunning"/> once it runs. The Logic Tracker and Key Items say the same thing.
    /// </summary>
    public string LogicEmptyState(string whileRunning)
    {
        if (LogicHidden) return "Logic is hidden by race mode.";
        if (Model.Logic.Running) return whileRunning;
        if (EngineProblem != null) return (SetupQuietText() ?? EngineProblemText(EngineProblem)).Replace("\n", " ");
        if (Model.Logic.Booting) return "Starting the logic engine…";
        return "Waiting for the logic engine…";
    }

    private string EngineProblemText(EngineStartError e)
    {
        switch (e.Code)
        {
            case "no_engine":
                return "Logic needs the Atlas Engine.\n" + e.Message + "\nAtlas can download everything it needs (about 50 MB, no installer).";
            case "world_missing":
                return AP_Atlas.Core.EngineSetup.ApworldSources.LogicSourceKnown(_appSettings, Game)
                    ? $"{Game} isn't in the logic engine yet.\nAdd this game: Atlas installs its logic (and a map pack), and logic starts again by itself."
                    : $"{Game} isn't in the logic engine yet, and Atlas doesn't know where its apworld comes from.\nAdd its project on the Games page.";
            case "world_download":
                return $"{Game}'s logic needs its world.\n{e.Message}";
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

    /// <summary>"Get the game's world…": the engine-setup permission (asked here), then logic starts again, which unpacks it.</summary>
    private void GetGameWorld() =>
        AP_Atlas.UI.PermissionDialog.Ask(this, _appSettings, AP_Atlas.Core.Permissions.EngineSetup, null, null, allowed =>
        {
            if (allowed && GodotObject.IsInstanceValid(this)) RetryLogicEngine();
        });

    // --- Engine state for the setup window and Properties ---

    /// <summary>Why logic isn't running for this slot (null while it runs or starts).</summary>
    public EngineStartError EngineProblem => Model.Logic.Problem;

    public bool EngineBooting => Model.Logic.Booting;

    /// <summary>How the engine rebuilt this slot's world: which YAML (or none) and whether its locations match the server's.</summary>
    public Newtonsoft.Json.Linq.JObject EngineYamlInfo => Model.Logic.Running ? Model.Logic.Engine.LastYamlInfo : null;

    public Newtonsoft.Json.Linq.JObject EngineVersions => Model.Logic.Engine.LastVersions;

    /// <summary>
    /// Whether the installed apworld's data matches the seed's (checksums of names, ids and groups; null when either
    /// side didn't report one). It can't see rule-only changes, which the location check and seed tests cover.
    /// </summary>
    public bool? ApworldMatchesSeed => Model.Logic.ApworldMatchesSeed;

    /// <summary>
    /// Whether linking the player's YAML would help this slot's logic: the engine asked for one (it can't rebuild the
    /// world from the server's data), or the rebuilt world differs from the server's for a reason other than the apworld
    /// version. Otherwise nothing offers to link one.
    /// </summary>
    public bool YamlWouldHelp =>
        EngineProblem?.Code is "yaml_needed" or "generation_failed"
        || (!LogicHidden && Model.Logic.Accuracy == AP_Atlas.Core.Games.LogicAccuracy.RebuildDiffers);

    public string InstalledWorldVersion => Model.Logic.Running ? Model.Logic.Engine.LastWorldVersion : null;

    /// <summary>True when this slot runs on Atlas's cached copy of the seed's apworld version instead of the installed one.</summary>
    public bool UsingSeedApworld => Model.Logic.Running && Model.Logic.Engine.LastApworldOverride?["used"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean &&
        (bool)Model.Logic.Engine.LastApworldOverride["used"];

    /// <summary>Opens the Atlas Engine's setup, or its page when it works (set by MainTrackerWindow).</summary>
    public Action OpenEngineSetup { get; set; }

    /// <summary>Shows the Games page on a game (set by MainTrackerWindow): the banner's way to a missing apworld.</summary>
    public Action<string> OpenGamesPageFor { get; set; }

    /// <summary>Opens Add games with a game ticked (set by MainTrackerWindow): the banner's way to a game's missing logic, and the empty map's to its pack.</summary>
    public Action<string> AddGames { get; set; }

    /// <summary>Whether the setup window adds this slot's game next (set by MainTrackerWindow): the Logic banner then stays quiet about it.</summary>
    public Func<bool> SetupAddsGames { get; set; }

    /// <summary>What the banner and the empty logic views say while the setup window is about to add this slot's missing game; else null.</summary>
    private string SetupQuietText() =>
        // Only a game the window can add: one whose logic source Atlas knows (else the banner's way out is the Games page).
        (EngineProblem?.Code == "world_download"
         || EngineProblem?.Code == "world_missing" && AP_Atlas.Core.EngineSetup.ApworldSources.LogicSourceKnown(_appSettings, Game))
        && SetupAddsGames?.Invoke() == true
            ? Tr("The setup window adds {0} next; logic starts by itself once it's in.").Replace("{0}", Game)
            : null;

    /// <summary>Shows the Logic banner again (the setup window stopped or started keeping it quiet).</summary>
    public void RefreshLogicNotice() => SyncLogicViews();

    /// <summary>The logic banner's buttons, by text (for tests).</summary>
    internal List<string> LogicNoticeButtons() =>
        _logicNoticeActions == null ? new List<string>() : _logicNoticeActions.GetChildren().OfType<Button>().Where(b => !b.IsQueuedForDeletion()).Select(b => b.Text).ToList();

    /// <summary>The Logic Tracker's accuracy banner as shown, or null while it's hidden (for tests).</summary>
    internal string AccuracyBannerShown => _accuracyBanner != null && _accuracyBanner.Visible ? _accuracyText.Text : null;

    /// <summary>Whether the accuracy banner offers "Choose the version…" (for tests).</summary>
    internal bool ChooseVersionOffered => _accuracyFix != null && _accuracyFix.Visible;

    /// <summary>Presses one of the logic banner's buttons (for tests).</summary>
    internal void PressLogicNotice(string text) =>
        _logicNoticeActions?.GetChildren().OfType<Button>().FirstOrDefault(b => !b.IsQueuedForDeletion() && b.Text == text)?.EmitSignal(BaseButton.SignalName.Pressed);

    /// <summary>The player YAML linked to this slot, if the file still exists.</summary>
    public string LinkedYamlPath => Model.Logic.LinkedYamlPath;

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
                ShowToast?.Invoke($"That YAML can't be used for {_slotName}: {check.Error}", AP_Atlas.Core.ThemeColors.Error);
                return;
            }
            LinkYaml(path);
            ShowToast?.Invoke($"Linked {System.IO.Path.GetFileName(path)} to {_slotName}. Restarting logic…", AP_Atlas.Core.ThemeColors.TextSubtle);
        };
        dialog.Canceled += () => dialog.QueueFree();
        GetTree().Root.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    /// <summary>Starts this slot's logic engine again from scratch (after setup, a new YAML or apworld, or a failure).</summary>
    public void RetryLogicEngine()
    {
        if (!GodotObject.IsInstanceValid(this) || Session == null) return;
        Model.Logic.Restart();
    }

    /// <summary>Re-evaluates the hint table (e.g. after another slot's logic changed).</summary>
    public void RefreshHints() => _hintTracker?.Refresh();

    private void RaiseStateChanged()
    {
        using var __perf = AP_Atlas.Core.PerfMonitor.Measure($"[{_slotName}] Map colors, hints and listeners");
        if (_mapTracker != null && Session != null)
        {
            _mapTracker.UpdateLogicColors(Model.Logic.Reachable, Session.Locations.AllLocationsChecked, Model.HintedLocations, Model.Logic.Engine?.LastGlitchedLocations,
                Model.UnsureShown);
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
