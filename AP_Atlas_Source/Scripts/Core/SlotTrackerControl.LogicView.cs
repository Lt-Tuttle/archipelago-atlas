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
        // Done checks are hidden: the list is what's left in logic. This brings them back (remembered).
        _logicShowChecked = new Button { ToggleMode = true, Text = "Show checked", TooltipText = "Also list the checks you've done (greyed), under the step that opened them.", ButtonPressed = _appSettings.LogicShowChecked };
        _logicShowChecked.Toggled += on =>
        {
            _appSettings.LogicShowChecked = on;
            DataManager.SaveSettingsSoon(_appSettings);
            ApplyLogicVisibility();
        };
        toolbar.AddChild(_logicShowChecked);
        _goModeLabel = new Label { Text = "GO MODE: your goal is in logic", Visible = false, TooltipText = "Everything the goal needs is reachable with what this slot has now" };
        _goModeLabel.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Success);
        toolbar.AddChild(_goModeLabel);
        // BK: nothing is in logic while checks remain; said above the list (Key Items and the slot card say it too).
        _bkLabel = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _bkLabel.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Warning);
        vbox.AddChild(_bkLabel);
        // Logic Atlas is unsure about for this seed: said above the list, with the choice to see it anyway.
        _unsureBanner = new HBoxContainer { Visible = false };
        _unsureBanner.AddThemeConstantOverride("separation", 10);
        _unsureText = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _unsureText.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Warning);
        _unsureBanner.AddChild(_unsureText);
        _unsureButton = new Button();
        _unsureButton.Pressed += () => Model.ShowUnsureLogic = !Model.ShowUnsureLogic;
        _unsureBanner.AddChild(_unsureButton);
        vbox.AddChild(_unsureBanner);
        _engineStatusLabel = new Label { Text = "Logic not running", SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right };
        toolbar.AddChild(_engineStatusLabel);

        // Shown when the logic can't be trusted as exact: the rebuilt world or the apworld doesn't match the seed.
        _accuracyBanner = new PanelContainer { Visible = false };
        _accuracyBanner.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = AP_Atlas.Core.ThemeColors.WarningTint,
            BorderColor = AP_Atlas.Core.ThemeColors.Warning,
            BorderWidthLeft = 3,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        });
        var bannerRow = new HBoxContainer();
        bannerRow.AddThemeConstantOverride("separation", 10);
        _accuracyText = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _accuracyText.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Warning);
        bannerRow.AddChild(_accuracyText);
        _accuracyLinkYaml = new Button { Text = "Link YAML…", TooltipText = "Choose the YAML used to generate this seed; Atlas remembers it for this slot" };
        _accuracyLinkYaml.Pressed += PickYaml;
        bannerRow.AddChild(_accuracyLinkYaml);
        _accuracyFix = new Button { Text = "Choose the version…", TooltipText = "Lists the game's releases: pick the one the seed's host used, choose their file, or let Atlas try them (for this slot only; your install isn't changed)" };
        _accuracyFix.Pressed += () => FixApworldVersion(interactive: true);
        bannerRow.AddChild(_accuracyFix);
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
                if (column == 2 && selected.GetText(2) is { Length: > 0 } item && item != "Starting Logic" && item != AP_Atlas.Core.LogicStep.EntrancesName)
                    Inspect(ItemTargetByName(item));
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
        _logicNoticeText.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.TextSubtle);
        _logicNotice.AddChild(_logicNoticeText);
        _logicNoticeActions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _logicNoticeActions.AddThemeConstantOverride("separation", 10);
        _logicNotice.AddChild(_logicNoticeActions);
        vbox.AddChild(_logicNotice);
    }

    private VBoxContainer _logicNotice;
    private Label _logicNoticeText;
    private HBoxContainer _unsureBanner;
    private Label _unsureText;
    private Button _unsureButton;
    private bool _logicChecklistShown;

    /// <summary>What the Logic Tracker says about logic it's unsure of (null while it says nothing), for the UI test.</summary>
    public string UnsureBannerText => _unsureBanner?.Visible == true ? _unsureText.Text : null;

    /// <summary>Presses the unsure banner's button (Show logic anyway / List them as a checklist), for the UI test.</summary>
    public void PressUnsureBanner() => _unsureButton?.EmitSignal(BaseButton.SignalName.Pressed);

    /// <summary>
    /// The checks listed under Checklist: those logic is unsure about for this seed while it runs, or every check left
    /// while it can't run (a lasting problem: the world can't be rebuilt, isn't installed, …). Its title says which.
    /// </summary>
    private List<long> ChecklistRows(out string title)
    {
        title = "";
        if (Session == null || LogicHidden) return new List<long>();
        if (Model.Logic.Running)
        {
            var unsure = Model.UnsureOpen();
            title = $"Checklist: logic unsure for this seed ({unsure.Count})";
            return unsure;
        }
        if (!LastingProblem) return new List<long>();
        var done = new HashSet<long>(Session.Locations.AllLocationsChecked);
        var open = Session.Locations.AllLocations.Where(l => !done.Contains(l) && !Model.IsExcluded(l)).ToList();
        title = $"Checklist: logic isn't running ({open.Count})";
        return open;
    }

    /// <summary>A problem that keeps logic from running until something changes (not a restart or an engine update under way).</summary>
    private bool LastingProblem => !Model.Logic.Running && EngineProblem is { } problem && problem.Code is not ("restarting" or "paused");

    private void UpdateUnsureBanner()
    {
        if (_unsureBanner == null) return;
        int unsure = Model.Logic.Running && !LogicHidden ? Model.Logic.Unsure.Count : 0;
        _unsureBanner.Visible = unsure > 0;
        if (unsure == 0) return;
        if (Model.ShowUnsureLogic)
        {
            _unsureText.Text = $"Showing logic for {unsure} checks Atlas is unsure about: it may be wrong for this seed.";
            _unsureButton.Text = "List them as a checklist";
            _unsureButton.TooltipText = "List the checks logic is unsure about under Checklist, without saying whether they're in logic";
        }
        else
        {
            _unsureText.Text = $"Logic is unsure about {unsure} checks of this seed: this world doesn't keep all of its seed's choices in its slot data, " +
                               "so Atlas can't tell whether they're in logic. They're listed under Checklist.";
            _unsureButton.Text = "Show logic anyway";
            _unsureButton.TooltipText = "Show what Atlas's rebuild of the world says about them (it may be wrong for this seed); remembered for this slot";
        }
    }
    private HBoxContainer _logicNoticeActions;
    private Button _logicFlaggedOnly, _logicShowChecked;
    // Rows the user flagged, noted or marked special (for the "flagged only" filter), kept as their markers are drawn.
    private readonly HashSet<long> _logicMarkedRows = new HashSet<long>();

    /// <summary>Turns the Logic Tracker's Show checked on or off, as its button does (for the UI test).</summary>
    public void ShowCheckedLogic(bool on)
    {
        if (_logicShowChecked != null) _logicShowChecked.ButtonPressed = on;
    }

    /// <summary>Flag/special/note marker on a Logic Tracker row.</summary>
    private void ApplyLogicRowMarker(TreeItem row, long locId)
    {
        var a = AP_Atlas.Core.Annotations.Get(AnnotationKey, AP_Atlas.Core.Annotations.LocationKey(locId));
        bool special = AP_Atlas.Core.Annotations.IsSpecialLocation(Game, Session.Locations.GetLocationNameFromId(locId));
        row.SetIcon(1, AP_Atlas.Core.Annotations.MarkerIcon(a?.Flag ?? 0, special, !string.IsNullOrWhiteSpace(a?.Note)));
        row.SetIconMaxWidth(1, Math.Max(16, _appSettings.ContentFontSize * 2));
        if (a != null || special) _logicMarkedRows.Add(locId);
        else _logicMarkedRows.Remove(locId);
    }

    private void ApplyAllLogicMarkers()
    {
        if (_logicTree == null || Session == null) return;
        foreach (var kv in _logicRows) ApplyLogicRowMarker(kv.Value, kv.Key);
        ApplyLogicVisibility();
    }

    /// <summary>
    /// Which rows show: the checks left (and the done ones with Show checked), only marked ones under "flagged only".
    /// A step's count says how many of its checks show, and a step with none goes.
    /// </summary>
    private void ApplyLogicVisibility()
    {
        if (_logicTree == null || Session == null) return;
        bool flaggedOnly = _logicFlaggedOnly?.ButtonPressed == true;
        bool showChecked = _appSettings.LogicShowChecked;
        foreach (var (locId, row) in _logicRows)
        {
            bool visible = (showChecked || !_logicRowsShownChecked.Contains(locId)) && (!flaggedOnly || _logicMarkedRows.Contains(locId));
            if (row.Visible != visible) row.Visible = visible;
        }
        TreeItem divider = null;
        int shown = 0;
        void CloseStep()
        {
            if (divider == null) return;
            if (divider.Visible != shown > 0) divider.Visible = shown > 0;
            // A step's divider names the item that opened it (column 2) and how many of its checks show.
            if (divider.GetText(0).StartsWith("● Step") && divider.GetText(2) is { Length: > 0 } item)
            {
                string text = $"── Unlocked by: {item} ({shown} checks) ──";
                if (shown > 0 && divider.GetText(1) != text) divider.SetText(1, text);
            }
        }
        for (var child = _logicTree.GetRoot()?.GetFirstChild(); child != null; child = child.GetNext())
        {
            if (child.GetText(0).StartsWith("●"))
            {
                CloseStep();
                divider = child;
                shown = 0;
            }
            else if (child.Visible) shown++;
        }
        CloseStep();
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
        if (_goModeLabel != null) _goModeLabel.Visible = !LogicHidden && Model.Logic.GoalInLogic == true;
        bool bk = Model.Bk;
        if (_bkLabel != null)
        {
            if (bk) _bkLabel.Text = BkText;
            _bkLabel.Visible = bk;
        }
        if (_progressionTracker != null) _progressionTracker.Banner = bk ? BkText : null;

        UpdateUnsureBanner();
        var steps = Model.Logic.Steps;
        var checklist = ChecklistRows(out string checklistTitle);
        bool hasChecklist = checklist.Count > 0;
        // The checklist sits after the steps and changes with them: a list with one is drawn whole.
        bool full = forceFull || _logicTree.GetRoot() == null || _logicRenderedRun != Model.Logic.Run || _logicRenderedSteps > steps.Count ||
                    (_logicPlaceholder != null && (steps.Count > 0 || hasChecklist)) || hasChecklist || _logicChecklistShown;
        _logicChecklistShown = hasChecklist;
        if (full)
        {
            _logicRenderedRun = Model.Logic.Run;
            _logicTree.Clear();
            _logicTree.CreateItem();
            _logicRows.Clear();
            _logicRowsShownChecked.Clear();
            _logicMarkedRows.Clear();
            _logicPlaceholder = null;
            _logicRenderedSteps = 0;
            _logicOrderCount = 1;
            _logicSectionIndex = 1;
        }
        var root = _logicTree.GetRoot();

        if (steps.Count == 0 && !hasChecklist)
        {
            _logicPlaceholder ??= _logicTree.CreateItem(root);
            _logicPlaceholder.SetText(1, LogicEmptyState("No reachable checks found yet."));
            _logicPlaceholder.SetCustomColor(1, AP_Atlas.Core.ThemeColors.TextSubtle);
            return;
        }

        var checkedLocs = new HashSet<long>(Session.Locations.AllLocationsChecked);
        var dividerBg = AP_Atlas.Core.ThemeColors.SurfacePanel;
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
            // (Entrances found in the game are no item to inspect.)
            else if (!step.IsEntrances) divider.SetMetadata(0, step.ItemName);
            if (!isBase) _logicSectionIndex++;

            foreach (var locId in shown)
            {
                var row = _logicTree.CreateItem(root);
                row.SetText(0, _logicOrderCount.ToString());
                row.SetText(1, Session.Locations.GetLocationNameFromId(locId) ?? "Unknown Check");
                row.SetText(2, step.ItemName);
                row.SetMetadata(0, locId);
                var rowBg = (_logicOrderCount % 2 == 0) ? AP_Atlas.Core.ThemeColors.RowEven : AP_Atlas.Core.ThemeColors.RowOdd;
                for (int c = 0; c < 3; c++) row.SetCustomBgColor(c, rowBg);
                bool isChecked = checkedLocs.Contains(locId);
                ColorLogicRow(row, isChecked);
                if (isChecked) _logicRowsShownChecked.Add(locId);
                _logicRows[locId] = row;
                ApplyLogicRowMarker(row, locId);
                _logicOrderCount++;
            }
        }

        if (hasChecklist)
        {
            var divider = _logicTree.CreateItem(root);
            divider.SetText(0, "● Checklist");
            divider.SetText(1, $"── {checklistTitle} ──");
            for (int c = 0; c < 3; c++)
            {
                divider.SetCustomBgColor(c, dividerBg);
                divider.SetCustomColor(c, AP_Atlas.Core.ThemeColors.Warning);
                divider.SetSelectable(c, false);
            }
            int n = 0;
            foreach (long locId in checklist)
            {
                var row = _logicTree.CreateItem(root);
                row.SetText(0, "–");
                row.SetText(1, Session.Locations.GetLocationNameFromId(locId) ?? "Unknown Check");
                row.SetText(2, "");
                row.SetMetadata(0, locId);
                var rowBg = (n++ % 2 == 0) ? AP_Atlas.Core.ThemeColors.RowEven : AP_Atlas.Core.ThemeColors.RowOdd;
                for (int c = 0; c < 3; c++) row.SetCustomBgColor(c, rowBg);
                ColorLogicRow(row, false);
                _logicRows[locId] = row;
                ApplyLogicRowMarker(row, locId);
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
        ApplyLogicVisibility();
    }

    private static void ColorLogicRow(TreeItem row, bool isChecked)
    {
        row.SetCustomColor(0, isChecked ? AP_Atlas.Core.ThemeColors.TextSubtle : AP_Atlas.Core.ThemeColors.TextMuted);
        row.SetCustomColor(1, isChecked ? AP_Atlas.Core.ThemeColors.TextSubtle : AP_Atlas.Core.ThemeColors.Text);
        row.SetCustomColor(2, isChecked ? AP_Atlas.Core.ThemeColors.TextSubtle : AP_Atlas.Core.ThemeColors.Progression);
    }
}
