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

    /// <summary>Shows each part as the settings say, or nothing but the content in focus mode.</summary>
    private void ApplyWindowParts()
    {
        bool show = !_focusMode;
        if (_sidebar != null) _sidebar.Visible = show && _appSettings.ShowSlotsPanel;
        if (_propertiesSidebar != null) _propertiesSidebar.Visible = show && _appSettings.ShowPropertiesPanel;
        if (_bottomPane != null) _bottomPane.Visible = show && _appSettings.ShowBottomPane;
        if (_globalStatusBar != null) _globalStatusBar.Visible = show && _appSettings.ShowStatusBar;
        if (_activityBar != null) _activityBar.Visible = show;
        SetExplorerVisible(_explorerWanted);
    }
}
