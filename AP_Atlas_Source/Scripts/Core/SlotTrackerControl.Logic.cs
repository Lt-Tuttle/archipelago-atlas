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

/// <summary>The slot's logic as this panel shows it (the model runs it): status, the Logic Tracker, and exclusions.</summary>
public partial class SlotTrackerControl : MarginContainer
{
    // =====================================================================
    // Showing the model's logic; exclusions
    // =====================================================================

    private string _shownStatus;

    /// <summary>Shows the slot's logic as it is now: the status line, the Logic Tracker, and its notices.</summary>
    private void ShowLogic()
    {
        string status = Model.Logic.Status;
        if (status != null && status != _shownStatus)
        {
            _shownStatus = status;
            SetStatus(status);
        }
        RenderLogicTree();
        SyncLogicViews();
    }

    /// <summary>A logic engine finished starting: its item pool is new, and its apworld may not be the seed's.</summary>
    private void OnEngineStarted()
    {
        UpdateItemHistoryUI(); // item pool is now available for "Not Yet Collected"
        RebuildPackIndex();    // ...and item names for pairing pack items that have no mapping
        UpdateKeyItemsUI();
        // Fix the apworld without asking when the download source is already trusted (or the version is cached).
        string checksum = ServerChecksumFor(Game);
        if (Model.Logic.ApworldMatchesSeed == false && _appSettings.AutoFixApworldVersions && checksum != null && !_apworldFixAttempted.Contains(checksum))
            FixApworldVersion(interactive: false);
    }

    /// <summary>Excluded by your choice if you made one, else by the seed (as the logic engine reads its options).</summary>
    private bool IsExcluded(long loc) => Model.IsExcluded(loc);

    public bool IsExcludedBySeed(long loc) => Model.Logic.ExcludedBySeed(loc);

    /// <summary>Why a location is excluded or included: "seed", "you", or null when it's a normal check.</summary>
    public string ExclusionSource(long loc) => Model.ExclusionSource(loc);

    /// <summary>
    /// Excludes the locations a player YAML lists (location names or location group names).
    /// Returns how many were newly excluded and the names that matched nothing in this seed.
    /// </summary>
    public async System.Threading.Tasks.Task<(int Applied, int Listed, List<string> Unknown)> ApplyYamlExclusionsAsync(List<string> names)
    {
        var ids = new HashSet<long>();
        var unknown = new List<string>();
        if (Session == null) return (0, 0, names);
        var inSeed = new HashSet<long>(Session.Locations.AllLocations);
        Dictionary<string, string[]> groups = null;
        bool groupsFetched = false;
        foreach (var name in names)
        {
            long id = Session.Locations.GetLocationIdFromName(Game, name);
            if (id >= 0 && inSeed.Contains(id)) { ids.Add(id); continue; }
            if (id >= 0) continue; // a real location that this seed doesn't have (turned off by its options)
            if (!groupsFetched)
            {
                groups = await LocationGroupsAsync(Game);
                groupsFetched = true;
            }
            var group = groups?.FirstOrDefault(g => string.Equals(g.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (group == null) { unknown.Add(name); continue; }
            foreach (var member in group)
            {
                long memberId = Session.Locations.GetLocationIdFromName(Game, member);
                if (memberId >= 0 && inSeed.Contains(memberId)) ids.Add(memberId);
            }
        }
        if (!GodotObject.IsInstanceValid(this)) return (0, ids.Count, unknown);
        var toExclude = ids.Where(id => !IsExcluded(id)).ToList();
        if (toExclude.Count > 0)
        {
            // Seed-excluded ones just drop a manual "include"; the rest become your exclusions.
            var bySeed = toExclude.Where(IsExcludedBySeed).ToList();
            if (bySeed.Count > 0) AP_Atlas.Core.Annotations.SetExclusionOverrides(AnnotationKey, bySeed, null);
            var mine = toExclude.Where(id => !IsExcludedBySeed(id)).ToList();
            if (mine.Count > 0) AP_Atlas.Core.Annotations.SetExclusionOverrides(AnnotationKey, mine, true);
        }
        return (toExclude.Count, ids.Count, unknown);
    }

    /// <summary>Shown when the YAML linked to this slot lists excluded locations (once per distinct list).</summary>
    public Action<string, Color, string, Action> ShowActionToast { get; set; }

    /// <summary>
    /// Offers the excluded locations of the YAML the user linked to this slot. Atlas reads only that file: it never looks
    /// through folders for YAMLs.
    /// </summary>
    private void OfferYamlExclusions() => AP_Atlas.Core.Async.Fire(OfferYamlExclusionsAsync(), $"reading {_slotName}'s YAML");

    private async Task OfferYamlExclusionsAsync()
    {
        string path = LinkedYamlPath, game = Game, slot = _slotName;
        if (path == null) return;
        var yaml = await System.Threading.Tasks.Task.Run(() => AP_Atlas.Core.YamlExclusions.Read(path, game, slot));
        if (!GodotObject.IsInstanceValid(this) || Session == null || yaml.Error != null || yaml.Names.Count == 0) return;
        string signature = System.IO.Path.GetFileName(yaml.File) + "|" + string.Join("|", yaml.Names.OrderBy(n => n));
        if (!AP_Atlas.Core.Annotations.FirstOfferOfYamlExclusions(AnnotationKey, signature)) return;
        string file = System.IO.Path.GetFileName(yaml.File);
        AppendDebugLog($"[Exclusions] {file} lists {yaml.Names.Count} excluded location(s) for {slot}.");
        ShowActionToast?.Invoke($"{file} excludes {yaml.Names.Count} location(s) for {slot}. Apply them to the tracker?", AP_Atlas.Core.ThemeColors.TextSubtle, "Apply", () => AP_Atlas.Core.Async.Fire(async () =>
        {
            var (applied, listed, unknown) = await ApplyYamlExclusionsAsync(yaml.Names);
            ShowToast?.Invoke($"Excluded {applied} location(s) from {file}" + (unknown.Count > 0 ? $" ({unknown.Count} name(s) not found)" : ""), AP_Atlas.Core.ThemeColors.TextSubtle);
        }, $"applying {file}'s excluded locations"));
    }
}
