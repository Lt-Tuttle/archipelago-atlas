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
    private string _apworldFixStatus;
    private readonly HashSet<string> _apworldFixAttempted = new HashSet<string>();

    /// <summary>
    /// Matches the slot's apworld to the version its seed was made with. When Atlas has that version (identified before,
    /// or chosen for this slot), logic restarts on it at once, for this slot only. Otherwise nothing is downloaded by
    /// itself: the slot says so (a card and the Logic Tracker's banner) and the version picker lists the game's releases
    /// for the user to pick the host's version, or to let Atlas try them.
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
        _apworldFixStatus = "\"Choose the version…\" lists the game's releases: pick the one the seed's host used, or let Atlas try them.";
        SyncAccuracyBanner();
        ShowActionToast?.Invoke($"{_slotName}: this seed was made with another version of the {game} apworld, so logic may be off.", AP_Atlas.Core.ThemeColors.Warning,
            "Choose the version…", OpenApworldPicker);
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
    public void UseApworld(string file, string version, string source, bool matchesSeed)
    {
        string game = Game, checksum = ServerChecksumFor(game);
        if (checksum == null) return;
        _appSettings.SlotApworlds ??= new Dictionary<string, SlotApworldChoice>();
        _appSettings.SlotApworlds[AnnotationKey] = new SlotApworldChoice { Game = game, SeedChecksum = checksum, File = file, Version = version, Source = source, MatchesSeed = matchesSeed };
        DataManager.SaveSettings(_appSettings);
        _apworldFixStatus = null;
        Model.Logic.RetrySeedApworld(checksum);
        EnginePools.Retire(ProfileId);
        AP_Atlas.Core.Logger.LogInfo($"[{_slotName}] Using {game} {version} ({source}) for this seed{(matchesSeed ? ", the version it was made with" : ", chosen though its data differs from the seed's")}.");
        ShowToast?.Invoke($"{_slotName}: using {game} {version}. Restarting logic…", matchesSeed ? AP_Atlas.Core.ThemeColors.Success : AP_Atlas.Core.ThemeColors.Warning);
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
        if (_logicTree != null) _logicTree.Visible = !LogicHidden && !problem;
        // Key Items repeats the empty state only when it changes (this runs on every logic change; a burst brings many).
        string emptyState = _progressionTracker != null && _progressionTracker.ShowingEmptyState ? LogicEmptyState("") : null;
        if (emptyState != null && emptyState != _shownEmptyState) _progressionTracker.RefreshMarkers();
        _shownEmptyState = emptyState;
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

    /// <summary>
    /// What a logic view says while it has nothing to list: hidden by race mode, the engine's problem, that it's starting,
    /// or <paramref name="whileRunning"/> once it runs. The Logic Tracker and Key Items say the same thing.
    /// </summary>
    public string LogicEmptyState(string whileRunning)
    {
        if (LogicHidden) return "Logic is hidden by race mode.";
        if (Model.Logic.Running) return whileRunning;
        if (EngineProblem != null) return EngineProblemText(EngineProblem).Replace("\n", " ");
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
        || (!LogicHidden && LogicAccuracyWarning != null && ApworldMatchesSeed != false);

    public string InstalledWorldVersion => Model.Logic.Running ? Model.Logic.Engine.LastWorldVersion : null;

    /// <summary>True when this slot runs on Atlas's cached copy of the seed's apworld version instead of the installed one.</summary>
    public bool UsingSeedApworld => Model.Logic.Running && Model.Logic.Engine.LastApworldOverride?["used"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean &&
        (bool)Model.Logic.Engine.LastApworldOverride["used"];

    /// <summary>Opens the Atlas Engine setup window (set by MainTrackerWindow).</summary>
    public Action OpenEngineSetup { get; set; }

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
            _mapTracker.UpdateLogicColors(Model.Logic.Reachable, Session.Locations.AllLocationsChecked, Model.HintedLocations, Model.Logic.Engine?.LastGlitchedLocations);
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
