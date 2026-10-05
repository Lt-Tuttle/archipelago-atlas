#nullable disable
using System.Linq;
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;

/// <summary>Switching what the window shows: tabs, the explorer, the content stage and the terminal pane.</summary>
public partial class MainTrackerWindow : Control, AP_Atlas.UI.IPropertiesHost
{
    private SlotTrackerControl _currentSelectedSlot = null;
    private void SwapSidebar(string title, Control activeContent = null)
    {
        SetExplorerVisible(activeContent != null);
        if (activeContent == null) return;
        _midLeftTitle.Text = title.ToUpper();
        if (activeContent.GetParent() != _midLeftVBox)
        {
            using var _ = AP_Atlas.Core.PerfMonitor.Measure($"Mount sidebar '{title}'");

            activeContent.GetParent()?.RemoveChild(activeContent);
            SetFontSizeRecursive(activeContent, _appSettings.ExplorerFontSize);
            _midLeftVBox.AddChild(activeContent);
        }
        foreach (Godot.Node c in _midLeftVBox.GetChildren())
        {
            if (c is Control ctrl && ctrl != _midLeftHeaderBox) ctrl.Visible = false;
        }
        activeContent.Visible = true;
    }
    /// <summary>
    /// Shows or hides the explorer. The panel directly under the tab bar gets the square top-left corner
    /// so it reads as attached to the tabs. Skips the work when nothing changes, so tab switches don't relayout.
    /// </summary>
    private void SetExplorerVisible(bool visible)
    {
        if (_midLeftSidebar.Visible == visible) return;
        _midLeftSidebar.Visible = visible;
        if (_contentStageStyle != null) _contentStageStyle.CornerRadiusTopLeft = visible ? _midLeftStyle.CornerRadiusTopRight : 0;
    }
    private void SwapContentView(Control target)
    {
        if (_contentStage == null) return;
        bool hasTarget = target != null;
        foreach (Node n in _contentStage.GetChildren())
        {
            if (n is Control c)
            {
                if (c == _landingPage) c.Visible = !hasTarget;
                else c.Visible = (c == target);
            }
        }
    }
    private void RefreshTerminalView()
    {
        if (_terminalStage == null) return;
        foreach (Node n in _terminalStage.GetChildren())
        {
            if (n is Control c)
            {
                if (_currentTerminalTab == 0) // Chat
                {
                    c.Visible = (c == _currentSelectedSlot);
                }
                else if (_currentTerminalTab == 1) // System Log
                {
                    c.Visible = (c == _sysLogVBox);
                }
                else if (_currentTerminalTab == 2) // Debug Log
                {
                    c.Visible = (c == _debugLogVBox);
                }
                else
                {
                    c.Visible = false;
                }
            }
        }
    }
    private void RefreshContextViews()
    {
        using var __perf = AP_Atlas.Core.PerfMonitor.Measure("Refresh context views");

        _propertiesPanel?.OnSelectedSlotChanged(_currentSelectedSlot);
        UpdateSidebarHighlighting();
        RefreshTerminalView();
        // Tabs 0 (Connections), 1 (Map Packs), Cheese Tracker and Sphere Tracker are global and handled by ChangeGlobalTab.
        if (_currentGlobalTab < 2 || _currentGlobalTab == CheeseTabIndex || _currentGlobalTab == SphereTabIndex) return;
        if (_currentSelectedSlot == null || !GodotObject.IsInstanceValid(_currentSelectedSlot))
        {
            _currentSelectedSlot = null;
            SwapSidebar("", null);
            ShowNoSlotPlaceholder();
            return;
        }
        Control view = null;
        Control sidebar = null;
        string sidebarTitle = "";
        switch (_currentGlobalTab)
        {
            case 2: view = _currentSelectedSlot.MapTracker; sidebar = _currentSelectedSlot.MapTracker?.SidebarContent; sidebarTitle = "Maps"; break;
            case 3: view = _currentSelectedSlot.ProgressionTracker; break;
            case 4: view = _currentSelectedSlot.LogicTrackerView; break;
            case 5: view = _currentSelectedSlot.ItemHistoryView; break;
            case 6: view = _currentSelectedSlot.HintsView; break;
        }
        MountInContentStage(view);
        SwapSidebar(sidebarTitle, sidebar);
        SwapContentView(view);
    }
    /// <summary>Slots are mounted in the terminal pane. This is the single place that enumerates them.</summary>
    private System.Collections.Generic.List<Node> ActiveSlotNodes()
    {
        var result = new System.Collections.Generic.List<Node>();
        if (_terminalStage == null) return result;
        foreach (Node n in _terminalStage.GetChildren())
        {
            if (n is SlotTrackerControl && !n.IsQueuedForDeletion()) result.Add(n);
        }
        return result;
    }
    /// <summary>Other connected slots in the same multiworld (same profile and team) as the given slot.</summary>
    private IEnumerable<SlotTrackerControl> SiblingSlots(SlotTrackerControl slot)
    {
        if (slot == null || !GodotObject.IsInstanceValid(slot) || slot.Session == null) yield break;
        int team = slot.Session.ConnectionInfo.Team;
        foreach (Node n in ActiveSlotNodes())
        {
            if (n is SlotTrackerControl other && other != slot && other.ProfileId == slot.ProfileId &&
                other.Session != null && other.Session.ConnectionInfo.Team == team)
            {
                yield return other;
            }
        }
    }
    /// <summary>In-logic state of another player's location, if that player is connected here; otherwise null.</summary>
    private bool? ResolveSlotLogic(SlotTrackerControl asker, int slotNumber, long locationId)
    {
        foreach (var other in SiblingSlots(asker))
        {
            if (other.Session.ConnectionInfo.Slot == slotNumber) return other.IsLocationInLogic(locationId);
        }
        return null;
    }
    private void MountInContentStage(Control view)
    {
        if (view == null || _contentStage == null) return;
        if (view.GetParent() == _contentStage) return;
        using var _ = AP_Atlas.Core.PerfMonitor.Measure($"Mount view '{view.Name}'");

        view.GetParent()?.RemoveChild(view);
        // Font sizes first: changing them after the view is in the tree makes every cell re-shape again.
        SetFontSizeRecursive(view, _appSettings.ContentFontSize);
        _contentStage.AddChild(view);
    }
    /// <summary>
    /// Attaches a new slot's views (still empty) to the content stage and explorer, hidden.
    /// Attaching a Tree that already holds thousands of rows costs hundreds of milliseconds, so doing it now,
    /// before the views are populated, keeps the first switch to each tab fast.
    /// </summary>
    private void PreMountSlotViews(SlotTrackerControl slot)
    {
        using var _ = AP_Atlas.Core.PerfMonitor.Measure($"Attach views for {slot.SlotName}");

        foreach (var view in new Control[] { slot.MapTracker, slot.ProgressionTracker, slot.LogicTrackerView, slot.ItemHistoryView, slot.HintsView })
        {
            if (view == null || view.GetParent() != null) continue;
            view.Visible = false;
            SetFontSizeRecursive(view, _appSettings.ContentFontSize);
            _contentStage.AddChild(view);
        }
        var sidebar = slot.MapTracker?.SidebarContent;
        if (sidebar != null && sidebar.GetParent() == null)
        {
            sidebar.Visible = false;
            SetFontSizeRecursive(sidebar, _appSettings.ExplorerFontSize);
            _midLeftVBox.AddChild(sidebar);
        }
    }
    private Label _noSlotPlaceholder;
    private void ShowNoSlotPlaceholder()
    {
        if (_noSlotPlaceholder == null || !GodotObject.IsInstanceValid(_noSlotPlaceholder))
        {
            _noSlotPlaceholder = new Label
            {
                Text = "Select a connected slot in the SLOTS panel to view this tool.",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            _noSlotPlaceholder.AddThemeColorOverride("font_color", Colors.Gray);
            _contentStage.AddChild(_noSlotPlaceholder);
        }
        SwapContentView(_noSlotPlaceholder);
    }
    private void ChangeGlobalTab(int tab)
    {
        string tabName = _workspaceSwitcher.GetTabTitle(tab);
        AP_Atlas.Core.PerfMonitor.SetAction($"Switch to {tabName} tab");
        using var _ = AP_Atlas.Core.PerfMonitor.Measure($"Switch to {tabName} tab");

        _currentGlobalTab = tab;
        if (tab == 0) { SwapSidebar("Connections", _connectionSidebarContent); SwapContentView(_connectionPanel); RefreshContextViews(); }
        else if (tab == 1) { SwapSidebar("Packs", _packManagerPanel.SidebarContent); SwapContentView(_packManagerPanel); RefreshContextViews(); }
        else if (tab == CheeseTabIndex) { SwapSidebar("Cheese Tracker", _cheeseTab.SidebarContent); SwapContentView(_cheeseTab); RefreshContextViews(); _cheeseTab.OnShown(); }
        else if (tab == SphereTabIndex) { SwapSidebar("Sphere Tracker", _sphereTab.SidebarContent); SwapContentView(_sphereTab); RefreshContextViews(); _sphereTab.OnShown(); }
        else RefreshContextViews(); // 2 Map Tracker, 3 Key Items, 4 Logic Tracker, 5 Item History, 6 Hints
    }
}
