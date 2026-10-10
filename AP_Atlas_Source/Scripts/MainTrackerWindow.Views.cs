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
        _explorerWanted = visible;
        // The tool's explorer shows only while the user shows the explorer, and never in focus mode.
        bool shown = visible && _appSettings.ShowExplorer && !_focusMode;
        if (_midLeftSidebar.Visible == shown) return;
        _midLeftSidebar.Visible = shown;
        if (_contentStageStyle != null) _contentStageStyle.CornerRadiusTopLeft = shown ? _midLeftStyle.CornerRadiusTopRight : 0;
    }
    private void SwapContentView(Control target)
    {
        if (_contentStage == null) return;
        foreach (Node n in _contentStage.GetChildren())
        {
            if (n is Control c) c.Visible = (c == target);
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
                    bool hasSlot = _currentSelectedSlot != null && GodotObject.IsInstanceValid(_currentSelectedSlot);
                    c.Visible = c == _chatEmptyHint ? !hasSlot : c == _currentSelectedSlot;
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

        RefreshSlotPicker();
        _propertiesPanel?.OnSelectedSlotChanged(_currentSelectedSlot);
        UpdateSidebarHighlighting();
        RefreshTerminalView();
        // Tools that aren't per slot show their own view (ShowTool).
        if (_currentTool.Scope != AP_Atlas.UI.ToolScope.Slot) return;
        if (_currentSelectedSlot == null || !GodotObject.IsInstanceValid(_currentSelectedSlot))
        {
            _currentSelectedSlot = null;
            SwapSidebar("", null);
            ShowNoSlotPlaceholder();
            return;
        }
        // The selected slot's view of the tool.
        Control view = _currentTool.SlotView?.Invoke(_currentSelectedSlot);
        Control sidebar = _currentTool.SlotExplorer?.Invoke(_currentSelectedSlot);
        MountInContentStage(view);
        SwapSidebar(_currentTool.ExplorerTitle, sidebar);
        SwapContentView(view);
    }
    /// <summary>Every slot started here, in the order they connected, while it lives (wherever its panel is).</summary>
    private readonly List<SlotTrackerControl> _slots = new();

    /// <summary>
    /// The live slots' panels, wherever they are: the terminal pane, or (with docking) another part of the window or a
    /// pop-out. This is the single place that enumerates them.
    /// </summary>
    private System.Collections.Generic.List<Node> ActiveSlotNodes()
    {
        _slots.RemoveAll(slot => !GodotObject.IsInstanceValid(slot) || slot.Ended || slot.IsQueuedForDeletion());
        return _slots.Cast<Node>().ToList();
    }

    /// <summary>The live slots themselves (their models), for services, which never use a slot's panel.</summary>
    private IEnumerable<AP_Atlas.Core.SlotModel> SlotModels() => ActiveSlotNodes().OfType<SlotTrackerControl>().Select(slot => slot.Model);
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
    private Control _noSlotPlaceholder;
    private CenterContainer _chatEmptyHint;
    private TextureRect _noSlotIcon;
    private Label _noSlotTitle;
    private Label _noSlotBody;
    /// <summary>A slot tool with no slot to show: the tool's icon, what it needs, and the way to a slot.</summary>
    private void ShowNoSlotPlaceholder()
    {
        if (_noSlotPlaceholder == null || !GodotObject.IsInstanceValid(_noSlotPlaceholder))
        {
            var center = new CenterContainer { Name = "NoSlotPlaceholder", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            var box = new VBoxContainer { CustomMinimumSize = new Godot.Vector2(380, 0) };
            box.AddThemeConstantOverride("separation", 10);
            center.AddChild(box);
            _noSlotIcon = new TextureRect { StretchMode = TextureRect.StretchModeEnum.KeepCentered, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
            box.AddChild(_noSlotIcon);
            _noSlotTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
            _noSlotTitle.SetMeta("font_size_ratio", 1.3f);
            box.AddChild(_noSlotTitle);
            _noSlotBody = AP_Atlas.UI.Kit.Subtle(string.Empty);
            _noSlotBody.HorizontalAlignment = HorizontalAlignment.Center;
            box.AddChild(_noSlotBody);
            var open = AP_Atlas.UI.Kit.Button(Tr("Open Multiworlds"), Tr("Connect a slot from its multiworld's page."), () => ShowTool(AP_Atlas.UI.Tool.Connections));
            open.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            box.AddChild(open);
            _noSlotPlaceholder = center;
            _contentStage.AddChild(_noSlotPlaceholder);
        }
        var tool = _currentTool ?? AP_Atlas.UI.Tool.MapTracker;
        _noSlotIcon.Texture = AP_Atlas.UI.LucideTextures.Get(tool.Icon, AP_Atlas.Core.ThemeColors.TextSubtle, 2.5f);
        bool anyConnected = ActiveSlotNodes().OfType<SlotTrackerControl>().Any(slot => slot.Session != null && slot.Session.Socket.Connected && slot.IsFullyLoaded);
        _noSlotTitle.Text = anyConnected ? Tr("No slot selected") : Tr("No slot connected");
        _noSlotBody.Text = anyConnected
            ? string.Format(Tr("The {0} shows one slot at a time: pick a connected slot in the SLOTS panel or the picker above."), Tr(tool.Title))
            : string.Format(Tr("The {0} follows a connected slot. Connect one from its multiworld's page, and it shows here."), Tr(tool.Title));
        SwapContentView(_noSlotPlaceholder);
    }
    /// <summary>Shows a tool: its own view, or for a slot tool the selected slot's view of it.</summary>
    private void ShowTool(AP_Atlas.UI.Tool tool)
    {
        AP_Atlas.Core.PerfMonitor.SetAction($"Switch to {tool.Title} tab");
        using var _ = AP_Atlas.Core.PerfMonitor.Measure($"Switch to {tool.Title} tab");

        _currentTool = tool;
        ApplyExplorerOffset(tool);
        if (_uiReady && _appSettings.LastTool != tool.Id)
        {
            _appSettings.LastTool = tool.Id; // for "where I left off" at the next start
            DataManager.SaveSettingsSoon(_appSettings);
        }
        _activityBar?.Select(tool);
        if (_toolTitle != null) _toolTitle.Text = Tr(tool.Title);
        if (_toolViews.TryGetValue(tool, out var own))
        {
            SwapSidebar(own.ExplorerTitle, own.Explorer);
            SwapContentView(own.View);
            RefreshContextViews();
            own.Shown?.Invoke();
        }
        else RefreshContextViews();
    }
}
