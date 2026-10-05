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

/// <summary>Race mode: detecting a race room, and what it hides.</summary>
public partial class SlotTrackerControl : MarginContainer
{
    // =====================================================================
    // Race mode
    // =====================================================================

    /// <summary>The server reports this room as a race (null until asked).</summary>
    public bool IsRaceRoom { get; private set; }

    /// <summary>The server has answered whether this room is a race (IsRaceRoom is meaningful).</summary>
    public bool RaceStateKnown { get; private set; }

    /// <summary>Race restrictions apply: no "why" explanations from the logic engine.</summary>
    public bool RaceRestricted => AP_Atlas.Core.RaceRules.IsActive(IsRaceRoom);

    /// <summary>Race restrictions hide all in-logic information for this slot.</summary>
    public bool LogicHidden => AP_Atlas.Core.RaceRules.HidesLogic(IsRaceRoom);

    private bool _raceStateAnnounced;

    private void DetectRaceMode() => AP_Atlas.Core.Async.Fire(DetectRaceModeAsync(), $"checking whether {_slotName}'s room is a race");

    private async Task DetectRaceModeAsync()
    {
        try
        {
            bool race = await Session.DataStorage.GetRaceModeAsync();
            if (!GodotObject.IsInstanceValid(this)) return;
            IsRaceRoom = race;
            RaceStateKnown = true;
            AppendDebugLog($"Race mode reported by the server: {race}");
        }
        catch (Exception ex)
        {
            AppendDebugLog("Could not read the room's race mode: " + ex.Message);
        }
        ApplyRaceRules();
    }

    /// <summary>Re-applies race restrictions to every view (after detection or a settings change).</summary>
    private void ApplyRaceRules()
    {
        if (!GodotObject.IsInstanceValid(this) || Session == null) return;
        bool restricted = RaceRestricted;
        if (restricted && !_raceStateAnnounced)
        {
            _raceStateAnnounced = true;
            string what = LogicHidden ? "all logic information is hidden" : "logic explanations are disabled";
            string why = IsRaceRoom ? "this room is in race mode" : "race mode is set to Always On";
            AP_Atlas.Core.Logger.LogInfo($"[color=orange][{_slotName}] Race mode: {what} ({why}).[/color]");
        }
        else if (!restricted) _raceStateAnnounced = false;

        _explainCache.Clear();
        SyncLogicViews();
        RaiseStateChanged();
    }

    /// <summary>
    /// Shows logic, or why there is none: race mode, or an engine problem with buttons to fix it. While logic is unknown
    /// the map uses neutral colors (open / hinted) instead of calling every check out of logic.
    /// </summary>
    private PanelContainer _accuracyBanner;
    private Label _accuracyText;
    private Button _accuracyLinkYaml;

    /// <summary>Why this slot's logic is only approximate (null when the rebuild matches the seed as far as Atlas can check).</summary>
    public string LogicAccuracyWarning
    {
        get
        {
            if (!_engineRunning) return null;
            if (ApworldMatchesSeed == false)
                return $"Logic may be off: this seed was made with a different version of the {Game} apworld than the one installed" +
                       (InstalledWorldVersion != null ? $" ({InstalledWorldVersion})" : "") + "." +
                       (_apworldFixStatus != null ? " " + _apworldFixStatus : "");
            var info = _logicEngine?.LastYamlInfo;
            if (info?["match"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && !(bool)info["match"])
                return $"Logic is approximate: your world was rebuilt, but it doesn't match the seed ({info["missing"]} locations missing, {info["extra"]} extra). " +
                       "Usually the game keeps some options out of the server's data. Link the YAML used to generate the seed for exact logic.";
            return null;
        }
    }

    private void SyncAccuracyBanner()
    {
        if (_accuracyBanner == null) return;
        string warning = LogicHidden ? null : LogicAccuracyWarning;
        _accuracyBanner.Visible = warning != null;
        _accuracyText.Text = "⚠ " + warning;
        if (warning != _lastAccuracyWarning)
        {
            _lastAccuracyWarning = warning;
            AccuracyChanged?.Invoke();
        }
        bool versionProblem = warning != null && ApworldMatchesSeed == false;
        _accuracyLinkYaml.Visible = warning != null && !versionProblem;
        _accuracyFix.Visible = versionProblem && !_apworldFixRunning;
        _accuracyAddSource.Visible = versionProblem && !_apworldFixRunning;
        _accuracyChooseApworld.Visible = versionProblem && !_apworldFixRunning;
    }
}
