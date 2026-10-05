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

/// <summary>The Logic Tracker view.</summary>
public partial class SlotTrackerControl : MarginContainer
{
    // =====================================================================
    // Logic Tracker view
    // =====================================================================

    private void BuildLogicTrackerView()
    {
        _logicView = new MarginContainer { Name = "Logic Tracker", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _logicView.AddThemeConstantOverride("margin_left", 10);
        _logicView.AddThemeConstantOverride("margin_top", 10);
        _logicView.AddThemeConstantOverride("margin_right", 10);
        _logicView.AddThemeConstantOverride("margin_bottom", 10);

        var vbox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _logicView.AddChild(vbox);

        var toolbar = new HBoxContainer();
        vbox.AddChild(toolbar);
        _logicFlaggedOnly = new Button { ToggleMode = true, Text = "Flagged / special only", TooltipText = "Show only checks you flagged, noted or marked special." };
        _logicFlaggedOnly.Toggled += _ => ApplyAllLogicMarkers();
        toolbar.AddChild(_logicFlaggedOnly);
        _engineStatusLabel = new Label { Text = "Engine: Offline", SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right };
        toolbar.AddChild(_engineStatusLabel);

        // Shown when the logic can't be trusted as exact: the rebuilt world or the apworld doesn't match the seed.
        _accuracyBanner = new PanelContainer { Visible = false };
        _accuracyBanner.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Godot.Color("#3a2a10"),
            BorderColor = Colors.Orange,
            BorderWidthLeft = 3,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        });
        var bannerRow = new HBoxContainer();
        bannerRow.AddThemeConstantOverride("separation", 10);
        _accuracyText = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _accuracyText.AddThemeColorOverride("font_color", Colors.Orange);
        bannerRow.AddChild(_accuracyText);
        _accuracyLinkYaml = new Button { Text = "Link YAML…", TooltipText = "Choose the YAML used to generate this seed; Atlas remembers it for this slot" };
        _accuracyLinkYaml.Pressed += PickYaml;
        bannerRow.AddChild(_accuracyLinkYaml);
        _accuracyFix = new Button { Text = "Fix automatically", TooltipText = "Find the apworld version this seed was made with and use it for this slot (your install isn't changed)" };
        _accuracyFix.Pressed += () => FixApworldVersion(interactive: true);
        bannerRow.AddChild(_accuracyFix);
        _accuracyChooseApworld = new Button { Text = "Choose apworld file…", TooltipText = "Use the apworld file the seed's host gave you, if it's this seed's version" };
        _accuracyChooseApworld.Pressed += ChooseSeedApworld;
        bannerRow.AddChild(_accuracyChooseApworld);
        _accuracyAddSource = new Button { Text = "Add a source…", TooltipText = "Paste the GitHub link of the project the seed's apworld comes from" };
        _accuracyAddSource.Pressed += AddApworldSource;
        bannerRow.AddChild(_accuracyAddSource);
        _accuracyBanner.AddChild(bannerRow);
        vbox.AddChild(_accuracyBanner);

        _logicTree = new Tree { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, Columns = 3 };
        _logicTree.SetColumnTitle(0, "Order");
        _logicTree.SetColumnTitle(1, "Location");
        _logicTree.SetColumnTitle(2, "Unlocked By");
        _logicTree.SetColumnExpandRatio(0, 1);
        _logicTree.SetColumnExpandRatio(1, 6);
        _logicTree.SetColumnExpandRatio(2, 4);
        _logicTree.SetColumnCustomMinimumWidth(0, 50);
        _logicTree.SetColumnCustomMinimumWidth(1, 250);
        _logicTree.SetColumnCustomMinimumWidth(2, 150);
        _logicTree.HideRoot = true;
        _logicTree.ColumnTitlesVisible = true;
        _logicTree.SelectMode = Tree.SelectModeEnum.Row;
        _logicTree.AddThemeConstantOverride("v_separation", 6);
        AP_Atlas.Core.TreePicks.Hook(_logicTree, (selected, column) =>
        {
            var meta = selected.GetMetadata(0);
            if (meta.VariantType == Variant.Type.Int)
            {
                long locId = meta.AsInt64();
                // "Unlocked By" column inspects the unlocking item; anything else the location.
                if (column == 2 && selected.GetText(2) != "Starting Logic") Inspect(ItemTargetByName(selected.GetText(2)));
                else Inspect(LocationTarget(locId));
            }
            else if (meta.VariantType == Variant.Type.String) Inspect(ItemTargetByName(meta.AsString()));
        });
        vbox.AddChild(_logicTree);
        _logicTree.CreateItem();

        // Shown instead of the list when there's no logic to show: race mode, or an engine problem with its fixes.
        _logicNotice = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center, Visible = false };
        _logicNotice.AddThemeConstantOverride("separation", 14);
        _logicNoticeText = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _logicNoticeText.AddThemeColorOverride("font_color", Colors.Gray);
        _logicNotice.AddChild(_logicNoticeText);
        _logicNoticeActions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _logicNoticeActions.AddThemeConstantOverride("separation", 10);
        _logicNotice.AddChild(_logicNoticeActions);
        vbox.AddChild(_logicNotice);
    }

    private VBoxContainer _logicNotice;
    private Label _logicNoticeText;
    private HBoxContainer _logicNoticeActions;
    private Button _logicFlaggedOnly;

    /// <summary>Flag/special/note marker on a Logic Tracker row, and its visibility under the "flagged only" filter.</summary>
    private void ApplyLogicRowMarker(TreeItem row, long locId)
    {
        var a = AP_Atlas.Core.Annotations.Get(AnnotationKey, AP_Atlas.Core.Annotations.LocationKey(locId));
        bool special = AP_Atlas.Core.Annotations.IsSpecialLocation(Game, Session.Locations.GetLocationNameFromId(locId));
        row.SetIcon(1, AP_Atlas.Core.Annotations.MarkerIcon(a?.Flag ?? 0, special, !string.IsNullOrWhiteSpace(a?.Note)));
        row.SetIconMaxWidth(1, Math.Max(16, _appSettings.ContentFontSize * 2));
        bool marked = a != null || special;
        row.Visible = _logicFlaggedOnly == null || !_logicFlaggedOnly.ButtonPressed || marked;
    }

    private void ApplyAllLogicMarkers()
    {
        if (_logicTree == null || Session == null) return;
        foreach (var kv in _logicRows) ApplyLogicRowMarker(kv.Value, kv.Key);
        // Hide step dividers with no visible rows under the filter.
        bool filtering = _logicFlaggedOnly?.ButtonPressed == true;
        TreeItem divider = null;
        bool anyVisible = false;
        for (var child = _logicTree.GetRoot()?.GetFirstChild(); child != null; child = child.GetNext())
        {
            bool isDivider = child.GetText(0).StartsWith("●");
            if (isDivider)
            {
                if (divider != null) divider.Visible = !filtering || anyVisible;
                divider = child;
                anyVisible = false;
            }
            else if (child.Visible) anyVisible = true;
        }
        if (divider != null) divider.Visible = !filtering || anyVisible;
    }

    // Incremental render state. Godot's Tree re-shapes the text of every new cell (~0.05 ms each), so a full
    // Clear()+rebuild of a few thousand cells costs hundreds of milliseconds. Within one engine run steps are only ever
    // appended and rows only change color, so after the first build we append and recolor in place; a new run (a
    // restart) rebuilds.
    private int _logicRenderedRun = -1;
    private int _logicRenderedSteps = 0;
    private int _logicOrderCount = 1;
    private int _logicSectionIndex = 1;
    private TreeItem _logicPlaceholder;
    private readonly Dictionary<long, TreeItem> _logicRows = new Dictionary<long, TreeItem>();
    private readonly HashSet<long> _logicRowsShownChecked = new HashSet<long>();

    private AP_Atlas.UI.ViewRefresh _logicRefresh;
    private bool _logicFullPending;

    /// <summary>Redraws the Logic Tracker (all of it with <paramref name="forceFull"/>): now if it shows, else when it does.</summary>
    private void RenderLogicTree(bool forceFull = false)
    {
        _logicFullPending |= forceFull;
        _logicRefresh?.Request();
    }

    private void RenderLogicTreeNow(bool forceFull)
    {
        using var __perf = AP_Atlas.Core.PerfMonitor.Measure($"[{_slotName}] Logic Tracker refresh");
        if (_logicTree == null || Session == null) return;

        var steps = Model.Logic.Steps;
        bool full = forceFull || _logicTree.GetRoot() == null || _logicRenderedRun != Model.Logic.Run || _logicRenderedSteps > steps.Count ||
                    (_logicPlaceholder != null && steps.Count > 0);
        if (full)
        {
            _logicRenderedRun = Model.Logic.Run;
            _logicTree.Clear();
            _logicTree.CreateItem();
            _logicRows.Clear();
            _logicRowsShownChecked.Clear();
            _logicPlaceholder = null;
            _logicRenderedSteps = 0;
            _logicOrderCount = 1;
            _logicSectionIndex = 1;
        }
        var root = _logicTree.GetRoot();

        if (steps.Count == 0)
        {
            _logicPlaceholder ??= _logicTree.CreateItem(root);
            _logicPlaceholder.SetText(1, Model.Logic.Running ? "No reachable checks found yet." : "Waiting for Logic Engine...");
            _logicPlaceholder.SetCustomColor(1, Colors.Gray);
            return;
        }

        var checkedLocs = new HashSet<long>(Session.Locations.AllLocationsChecked);
        var dividerBg = new Godot.Color("#252836");
        var dividerFg = AP_Atlas.Core.ThemeColors.Accent;

        for (; _logicRenderedSteps < steps.Count; _logicRenderedSteps++)
        {
            var step = steps[_logicRenderedSteps];
            var shown = ShownLocs(step.Locations);
            if (shown.Count == 0) continue;
            bool isBase = step.IsStart;

            var divider = _logicTree.CreateItem(root);
            divider.SetText(0, isBase ? "● Base" : $"● Step {_logicSectionIndex}");
            divider.SetText(1, isBase ? "── Base Logic (Starting Reachable) ──" : $"── Unlocked by: {step.ItemName} ({shown.Count} checks) ──");
            divider.SetText(2, isBase ? "Starting Checks" : step.ItemName);
            for (int c = 0; c < 3; c++)
            {
                divider.SetCustomBgColor(c, dividerBg);
                divider.SetCustomColor(c, dividerFg);
            }
            // Selecting a step divider inspects the item that unlocked it.
            if (isBase) { for (int c = 0; c < 3; c++) divider.SetSelectable(c, false); }
            else divider.SetMetadata(0, step.ItemName);
            if (!isBase) _logicSectionIndex++;

            foreach (var locId in shown)
            {
                var row = _logicTree.CreateItem(root);
                row.SetText(0, _logicOrderCount.ToString());
                row.SetText(1, Session.Locations.GetLocationNameFromId(locId) ?? "Unknown Check");
                row.SetText(2, step.ItemName);
                row.SetMetadata(0, locId);
                var rowBg = (_logicOrderCount % 2 == 0) ? new Godot.Color("#16161C") : new Godot.Color("#1F1F27");
                for (int c = 0; c < 3; c++) row.SetCustomBgColor(c, rowBg);
                bool isChecked = checkedLocs.Contains(locId);
                ColorLogicRow(row, isChecked);
                if (isChecked) _logicRowsShownChecked.Add(locId);
                _logicRows[locId] = row;
                ApplyLogicRowMarker(row, locId);
                _logicOrderCount++;
            }
        }

        // Grey out rows whose check was completed since they were drawn (only those cells change).
        foreach (var kv in _logicRows)
        {
            bool isChecked = checkedLocs.Contains(kv.Key);
            if (isChecked == _logicRowsShownChecked.Contains(kv.Key)) continue;
            ColorLogicRow(kv.Value, isChecked);
            if (isChecked) _logicRowsShownChecked.Add(kv.Key);
            else _logicRowsShownChecked.Remove(kv.Key);
        }
        if (_logicFlaggedOnly?.ButtonPressed == true) ApplyAllLogicMarkers();
    }

    private static void ColorLogicRow(TreeItem row, bool isChecked)
    {
        row.SetCustomColor(0, isChecked ? Colors.DimGray : Colors.LightGray);
        row.SetCustomColor(1, isChecked ? Colors.DimGray : Colors.White);
        row.SetCustomColor(2, isChecked ? Colors.DimGray : Colors.Plum);
    }
}
