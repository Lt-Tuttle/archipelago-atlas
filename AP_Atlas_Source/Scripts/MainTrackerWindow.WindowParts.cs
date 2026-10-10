using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Showing and hiding the window's parts (the View menu's check items, remembered in the settings), and focus mode:
/// the content and its header alone, with the menu bar, until it's turned off again.
/// </summary>
public partial class MainTrackerWindow
{
    private VBoxContainer? _bottomPane;
    private bool _explorerWanted; // the tool showing has an explorer
    private bool _focusMode;

    // The panes' smallest widths, in logical units (the scale multiplies them). Together with the activity bar, margins and
    // separators they must fit the window, or a pane hides itself: Properties first, then the explorer, then the slots.
    internal const int SlotsMinWidth = 320, ExplorerMinWidth = 220, ContentMinWidth = 480, PropertiesMinWidth = 250;
    /// <summary>How wide Properties starts on a fresh settings file (its minimum is narrower; the user's own drag is kept after that).</summary>
    internal const int PropertiesStartWidth = 360;
    /// <summary>The tool header's margins: its text on the side panels' header line (their 2 px border and 12 px margin), indented like the content's text, its menu over the content's edge.</summary>
    internal const int ToolHeaderTop = 12, ToolHeaderLeft = 14, ToolHeaderRight = 8;
    private const int PaneSeparation = 8, OuterMargins = 16;
    /// <summary>The bottom pane's smallest height, and the share of the content's height it starts with on a fresh settings file (a third).</summary>
    internal const int BottomPaneMinHeight = 150, BottomPaneStartFraction = 3;
    private bool _bottomPanePlaced;

    /// <summary>
    /// On a fresh settings file, once the window is laid out: the bottom pane takes a third of the height instead of the
    /// split's half (which squeezed the Multiworlds editor's slot rows); the user's own drag is kept after that, as for
    /// Properties. The split's offset counts from its default position, so it's taken from the laid-out sizes.
    /// </summary>
    private void PlaceBottomPaneOnFresh()
    {
        if (_bottomPanePlaced || _contentSplit == null || _bottomPane == null || !_bottomPane.Visible) return;
        if (!_appSettings.Fresh || _appSettings.SplitContentOffset != 0)
        {
            _bottomPanePlaced = true;
            return;
        }
        float height = _contentSplit.Size.Y - PaneSeparation, current = _bottomPane.Size.Y;
        if (height <= 0 || current <= 0) return; // not laid out yet
        int wanted = Math.Max(BottomPaneMinHeight, (int)(height / BottomPaneStartFraction));
        int offset = (int)(current - wanted); // a positive offset moves the divider down
        _contentSplit.SplitOffsets = new[] { offset };
        _appSettings.SplitContentOffset = offset;
        DataManager.SaveSettingsSoon(_appSettings);
        _bottomPanePlaced = true;
    }

    /// <summary>
    /// The explorer's width for the tool that shows: the width the user dragged it to for that tool, else the width a
    /// returning user dragged before tools had their own, else the tool's starting width (Map Packs and Games start wider
    /// than the minimum: their lists hold long names). The explorer doesn't expand, so the split's default position is its
    /// start and the offset is the explorer's width itself (clamped to its minimum).
    /// </summary>
    private void ApplyExplorerOffset(AP_Atlas.UI.Tool tool)
    {
        if (_explorerSplit == null || tool == null) return;
        int offset = _appSettings.ExplorerSplitOffsets.TryGetValue(tool.Id, out int kept) ? kept
            : _appSettings.SplitRightSidebarOffset != 0 ? _appSettings.SplitRightSidebarOffset
            : tool.ExplorerStartWidth;
        var offsets = _explorerSplit.SplitOffsets;
        if (offsets.Length == 0 || offsets[0] != offset) _explorerSplit.SplitOffsets = new[] { offset };
    }

    /// <summary>The parts hidden because the window is too narrow for them (their settings untouched; they return with the width).</summary>
    private readonly HashSet<string> _autoHidden = new();
    private HBoxContainer? _toolHeader; // the tool's header row above the content: its buttons need their width too
    internal IReadOnlyCollection<string> AutoHiddenParts => _autoHidden;

    /// <summary>Whether the user shows a part, by the command that toggles it (the View menu's check marks).</summary>
    private bool PartShown(string commandId) => commandId switch
    {
        "view.slots-panel" => _appSettings.ShowSlotsPanel,
        "view.explorer" => _appSettings.ShowExplorer,
        "view.properties-panel" => _appSettings.ShowPropertiesPanel,
        "view.bottom-pane" => _appSettings.ShowBottomPane,
        "view.status-bar" => _appSettings.ShowStatusBar,
        _ => true
    };

    private void TogglePart(string commandId) => SetPart(commandId, !PartShown(commandId));

    /// <summary>Shows or hides a part (the View menu, the Settings page), remembering it.</summary>
    private void SetPart(string commandId, bool shown)
    {
        switch (commandId)
        {
            case "view.slots-panel": _appSettings.ShowSlotsPanel = shown; break;
            case "view.explorer": _appSettings.ShowExplorer = shown; break;
            case "view.properties-panel": _appSettings.ShowPropertiesPanel = shown; break;
            case "view.bottom-pane": _appSettings.ShowBottomPane = shown; break;
            case "view.status-bar": _appSettings.ShowStatusBar = shown; break;
            default: return;
        }
        DataManager.SaveSettings(_appSettings);
        ApplyWindowParts();
    }

    /// <summary>Which of the bottom pane's tabs show, as the settings say (Chat always does).</summary>
    private void ApplyConsoleTabs()
    {
        if (_bottomTabs == null) return;
        _bottomTabs.SetTabHidden(1, !_appSettings.ShowSystemLogTab);
        _bottomTabs.SetTabHidden(2, !_appSettings.DeveloperMode); // the Debug Log is for developer mode
        if (_bottomTabs.IsTabHidden(_bottomTabs.CurrentTab)) ShowTerminalTab(0);
    }

    /// <summary>F9: the content alone; again, every part as the user had it.</summary>
    private void ToggleFocusMode()
    {
        _focusMode = !_focusMode;
        ApplyWindowParts();
    }

    /// <summary>Shows each part as the settings say, or nothing but the content in focus mode; a part the width can't hold hides itself.</summary>
    /// <param name="width">The window's width in logical units to fit, when it's about to change; the current one otherwise.</param>
    private void ApplyWindowParts(float? width = null)
    {
        bool show = !_focusMode;
        FitPanes(show, width ?? GetViewport().GetVisibleRect().Size.X);
        if (_sidebar != null) _sidebar.Visible = show && _appSettings.ShowSlotsPanel && !_autoHidden.Contains("view.slots-panel");
        if (_propertiesSidebar != null) _propertiesSidebar.Visible = show && _appSettings.ShowPropertiesPanel && !_autoHidden.Contains("view.properties-panel");
        if (_bottomPane != null)
        {
            _bottomPane.Visible = show && _appSettings.ShowBottomPane;
            if (_bottomPane.Visible) ClearTerminalTabNew(_currentTerminalTab); // the showing tab's lines are seen now
        }
        if (_globalStatusBar != null) _globalStatusBar.Visible = show && _appSettings.ShowStatusBar;
        if (_activityBar != null) _activityBar.Visible = show;
        SetExplorerVisible(_explorerWanted && !_autoHidden.Contains("view.explorer"));
    }

    /// <summary>Which wanted parts the window's width can hold, Properties giving way first, then the explorer, then the slots.</summary>
    private void FitPanes(bool show, float width)
    {
        _autoHidden.Clear();
        if (!show || _contentStage == null) return;
        float available = width - OuterMargins - (_activityBar != null ? Math.Max(_activityBar.Size.X, _activityBar.GetCombinedMinimumSize().X) : 0);
        if (available <= 0) return; // not laid out yet
        bool slots = _appSettings.ShowSlotsPanel, explorer = _explorerWanted && _appSettings.ShowExplorer, properties = _appSettings.ShowPropertiesPanel;
        // The middle column is as wide as its widest row: the explorer beside the content, the tool's header, or the bottom pane.
        float header = _toolHeader?.GetCombinedMinimumSize().X ?? 0, bottom = _bottomPane?.Visible == true ? _bottomPane.GetCombinedMinimumSize().X : 0;
        float Middle() => Math.Max(Math.Max(ContentMinWidth + (explorer ? ExplorerMinWidth + PaneSeparation : 0), header), bottom);
        float Needed() => Middle() + (slots ? SlotsMinWidth + PaneSeparation : 0) + (properties ? PropertiesMinWidth + PaneSeparation : 0);
        if (Needed() > available && properties) { properties = false; _autoHidden.Add("view.properties-panel"); }
        if (Needed() > available && explorer) { explorer = false; _autoHidden.Add("view.explorer"); }
        if (Needed() > available && slots) { slots = false; _autoHidden.Add("view.slots-panel"); }
    }
}
