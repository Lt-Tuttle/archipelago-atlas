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

    private void TogglePart(string commandId)
    {
        switch (commandId)
        {
            case "view.slots-panel": _appSettings.ShowSlotsPanel = !_appSettings.ShowSlotsPanel; break;
            case "view.explorer": _appSettings.ShowExplorer = !_appSettings.ShowExplorer; break;
            case "view.properties-panel": _appSettings.ShowPropertiesPanel = !_appSettings.ShowPropertiesPanel; break;
            case "view.bottom-pane": _appSettings.ShowBottomPane = !_appSettings.ShowBottomPane; break;
            case "view.status-bar": _appSettings.ShowStatusBar = !_appSettings.ShowStatusBar; break;
            default: return;
        }
        DataManager.SaveSettings(_appSettings);
        ApplyWindowParts();
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
