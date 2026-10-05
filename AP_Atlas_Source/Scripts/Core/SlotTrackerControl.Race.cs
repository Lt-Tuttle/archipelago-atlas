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

/// <summary>Race mode (the model detects it) and the logic accuracy banner.</summary>
public partial class SlotTrackerControl : MarginContainer
{
    // =====================================================================
    // Race mode
    // =====================================================================

    /// <summary>The server reports this room as a race (false until it answers).</summary>
    public bool IsRaceRoom => Model.IsRaceRoom;

    /// <summary>The server has answered whether this room is a race (IsRaceRoom is meaningful).</summary>
    public bool RaceStateKnown => Model.RaceStateKnown;

    /// <summary>Race restrictions apply: no "why" explanations from the logic engine.</summary>
    public bool RaceRestricted => Model.RaceRestricted;

    /// <summary>Race restrictions hide all in-logic information for this slot.</summary>
    public bool LogicHidden => Model.LogicHidden;

    /// <summary>
    /// Shows logic, or why there is none: race mode, or an engine problem with buttons to fix it. While logic is unknown
    /// the map uses neutral colors (open / hinted) instead of calling every check out of logic.
    /// </summary>
    private PanelContainer _accuracyBanner;
    private Label _accuracyText;
    private Button _accuracyLinkYaml;

    /// <summary>
    /// Why this slot's logic is only approximate (null when the rebuild matches the seed as far as Atlas can check), with
    /// how fixing the apworld version is going.
    /// </summary>
    public string LogicAccuracyWarning
    {
        get
        {
            string warning = Model.LogicAccuracyWarning;
            return warning != null && ApworldMatchesSeed == false && _apworldFixStatus != null ? warning + " " + _apworldFixStatus : warning;
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
