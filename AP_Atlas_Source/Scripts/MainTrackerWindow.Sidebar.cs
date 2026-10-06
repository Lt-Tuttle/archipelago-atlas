#nullable disable
using System.Linq;
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;

/// <summary>The slots sidebar: a card per slot, with its status.</summary>
public partial class MainTrackerWindow : Control, AP_Atlas.UI.IPropertiesHost
{
    private int _spinnerIndex = 0;
    private string[] _spinnerFrames = { "/", "-", "\\", "|" };
    private Texture2D _iconConnect;
    private Texture2D _iconCheck;
    private Texture2D _iconDisconnect;
    private Texture2D _iconDelete;
    /// <summary>Styles a slot card in the SLOTS sidebar; the selected card is tinted and outlined in the accent color.</summary>
    private static void ApplySlotCardStyle(PanelContainer card, Button slotButton, bool isSelected)
    {
        var accent = AP_Atlas.Core.ThemeColors.Accent;
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = isSelected ? AP_Atlas.Core.ThemeColors.AccentTint : AP_Atlas.Core.ThemeColors.SurfaceSunken,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = isSelected ? accent : AP_Atlas.Core.ThemeColors.BorderSoft,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 5,
            ContentMarginBottom = 5
        });
        if (slotButton == null) return;
        string[] fontColors = { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" };
        string[] styles = { "normal", "hover", "pressed", "focus" };
        if (isSelected)
        {
            var textColor = AP_Atlas.Core.ThemeColors.TextOnAccent;
            var style = new StyleBoxFlat
            {
                BgColor = accent,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                ContentMarginLeft = 8,
                ContentMarginRight = 8,
                ContentMarginTop = 4,
                ContentMarginBottom = 4
            };
            foreach (var c in fontColors) slotButton.AddThemeColorOverride(c, textColor);
            foreach (var s in styles) slotButton.AddThemeStyleboxOverride(s, style);
        }
        else
        {
            foreach (var c in fontColors) slotButton.RemoveThemeColorOverride(c);
            foreach (var s in styles) slotButton.RemoveThemeStyleboxOverride(s);
        }
    }
    private void UpdateSidebarHighlighting()
    {
        if (_activeSessionsList == null) return;
        foreach (Node child in _activeSessionsList.GetChildren())
        {
            if (child is PanelContainer card && card.HasMeta("slot_name") && card.HasMeta("profile_id"))
            {
                string slotName = card.GetMeta("slot_name").AsString();
                string profileId = card.GetMeta("profile_id").AsString();
                bool isSelected = _currentSelectedSlot != null &&
                                  _currentSelectedSlot.SlotName == slotName &&
                                  _currentSelectedSlot.ProfileId == profileId;
                var cardVBox = card.GetChildOrNull<VBoxContainer>(0);
                var btn = cardVBox?.GetChildOrNull<HBoxContainer>(0)?.GetChildOrNull<Button>(0);
                ApplySlotCardStyle(card, btn, isSelected);
            }
        }
    }
    private string _sidebarLayout;

    /// <summary>
    /// What the sidebar's cards are made of: the multiworlds, their slots, which have a special-items column, and the look.
    /// Not their state, nor a slot's game (both are updated in place).
    /// </summary>
    private string SidebarLayout()
    {
        var layout = new System.Text.StringBuilder();
        layout.Append(AP_Atlas.Core.ThemeColors.Accent.ToHtml()).Append('|');
        foreach (var profile in _profiles)
        {
            layout.Append(profile.Id).Append('\u0001').Append(profile.Name).Append('\u0002');
            foreach (var slotName in profile.Slots)
            {
                layout.Append(slotName).Append('\u0001')
                    .Append(AP_Atlas.Core.Annotations.SpecialItemNames(CardGame(profile, slotName)).Any() ? '1' : '0').Append('\u0002');
            }
            layout.Append('\u0003');
        }
        return layout.ToString();
    }

    /// <summary>A slot's game, from its connection or as saved from the last one.</summary>
    private string CardGame(MultiworldProfile profile, string slotName)
    {
        var live = ActiveSlotNodes().OfType<SlotTrackerControl>().FirstOrDefault(s => s.ProfileId == profile.Id && s.SlotName == slotName);
        if (!string.IsNullOrEmpty(live?.Session?.ConnectionInfo?.Game)) return live.Session.ConnectionInfo.Game;
        return profile.SavedStats != null && profile.SavedStats.TryGetValue(slotName, out var stats) ? stats.GameName ?? "" : "";
    }

    /// <summary>
    /// Brings the SLOTS sidebar up to date. Its cards are rebuilt only when the multiworlds, their slots or the look
    /// changed; a slot connecting, dropping or changing just updates its card in place (rebuilding every card took
    /// about 50 ms with 20 slots, on each connect).
    /// </summary>
    private void UpdateSidebar()
    {
        // Home follows the slots while it shows (a slot connected or ended, a multiworld added or deleted); this never runs on a timer.
        if (_currentTool == AP_Atlas.UI.Tool.Home) _homePage?.Refresh();
        RefreshSlotPicker(); // the slots changed: a connected one, an ended one
        if (_activeSessionsList == null) return;
        string layout = SidebarLayout();
        if (layout == _sidebarLayout && _activeSessionsList.GetChildCount() > 0)
        {
            UpdateSlotStatuses();
            UpdateSidebarHighlighting();
            return;
        }
        _sidebarLayout = layout;
        using var __perf = AP_Atlas.Core.PerfMonitor.Measure("Rebuild SLOTS sidebar");
        foreach (Node child in _activeSessionsList.GetChildren())
        {
            child.QueueFree();
        }
        foreach (var profile in _profiles)
        {
            var profileHeaderPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var profileHeaderStyle = new StyleBoxFlat { BgColor = AP_Atlas.Core.ThemeColors.Accent, ContentMarginTop = 4, ContentMarginBottom = 4, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 };
            profileHeaderPanel.AddThemeStyleboxOverride("panel", profileHeaderStyle);
            var header = new Label
            {
                Text = profile.Name,
                HorizontalAlignment = HorizontalAlignment.Center,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            header.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.TextOnAccent);
            profileHeaderPanel.AddChild(header);
            _activeSessionsList.AddChild(profileHeaderPanel);
            foreach (var slotName in profile.Slots)
            {
                SlotTrackerControl session = null;
                if (_contentStage != null)
                {
                    foreach (Node active in ActiveSlotNodes())
                    {
                        if (active is SlotTrackerControl slot && slot.ProfileId == profile.Id && slot.SlotName == slotName)
                        {
                            session = slot;
                            break;
                        }
                    }
                }
                bool isSocketConnected = session != null && session.Session != null && session.Session.Socket.Connected;
                bool isFullyLoaded = session != null && session.IsFullyLoaded;
                bool isConnected = isSocketConnected && isFullyLoaded;
                bool isConnecting = _connectingSlots.Contains(SlotKey(profile.Id, slotName)) || (isSocketConnected && !isFullyLoaded);
                bool isSelected = session != null && session == _currentSelectedSlot;
                var cardPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                cardPanel.SetMeta("slot_name", slotName);
                cardPanel.SetMeta("profile_id", profile.Id);
                var cardVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                cardVBox.AddThemeConstantOverride("separation", 3);
                cardPanel.AddChild(cardVBox);
                var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 2);
                string gameName = "";
                if (session != null && session.Session != null && session.Session.ConnectionInfo != null && !string.IsNullOrEmpty(session.Session.ConnectionInfo.Game))
                {
                    gameName = session.Session.ConnectionInfo.Game;
                }
                else if (profile.SavedStats != null && profile.SavedStats.TryGetValue(slotName, out var stats) && !string.IsNullOrEmpty(stats.GameName))
                {
                    gameName = stats.GameName;
                }
                var btn = new Button
                {
                    Text = slotName,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    Alignment = HorizontalAlignment.Left
                };
                ApplySlotCardStyle(cardPanel, btn, isSelected);
                string capturedSlotName = slotName;
                string capturedProfileId = profile.Id;
                btn.Pressed += () =>
                {
                    SlotTrackerControl targetSlot = null;
                    if (_contentStage != null)
                    {
                        foreach (Node active in ActiveSlotNodes())
                        {
                            if (active is SlotTrackerControl slot && slot.ProfileId == capturedProfileId && slot.SlotName == capturedSlotName)
                            {
                                targetSlot = slot;
                                break;
                            }
                        }
                    }
                    if (targetSlot != null)
                    {
                        AP_Atlas.Core.PerfMonitor.SetAction($"Select slot {capturedSlotName}");
                        using var _ = AP_Atlas.Core.PerfMonitor.Measure($"Select slot {capturedSlotName}");

                        _currentSelectedSlot = targetSlot;
                        RefreshContextViews();
                    }
                    // Clicking a card also shows that slot in Properties (its summary, or saved stats when offline), and
                    // in the Sphere Tracker (which needs no connection).
                    AP_Atlas.Core.Inspector.Inspect(AP_Atlas.Core.InspectTarget.ForSlot(capturedProfileId, capturedSlotName));
                    _sphereTab?.FollowSlot(capturedProfileId, capturedSlotName);
                };
                cardPanel.GuiInput += (ev) =>
                {
                    if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    {
                        btn.EmitSignal("pressed");
                    }
                };
                row.AddChild(btn);
                var connectBtn = new Button
                {
                    Name = "ConnectBtn",
                    Icon = isConnecting ? null : (isConnected ? _iconCheck : _iconConnect),
                    Text = isConnecting ? _spinnerFrames[_spinnerIndex] : "",
                    CustomMinimumSize = new Godot.Vector2(32, 32),
                    SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter,
                    SizeFlagsHorizontal = Godot.Control.SizeFlags.ShrinkCenter,
                    ExpandIcon = true,
                    IconAlignment = HorizontalAlignment.Center,
                    ClipText = true,
                    Disabled = isConnected || isConnecting
                };
                connectBtn.SetMeta("is_icon_button", true);
                connectBtn.SetMeta("slot_name", slotName);
                connectBtn.SetMeta("profile_id", profile.Id);
                connectBtn.TooltipText = isConnecting ? "Connecting..." : (isConnected ? "Connected" : "Connect");
                connectBtn.AddThemeColorOverride("icon_disabled_color", AP_Atlas.Core.ThemeColors.Text);
                if (isConnected) connectBtn.Modulate = AP_Atlas.Core.ThemeColors.Text;
                else if (isConnecting) connectBtn.Modulate = AP_Atlas.Core.ThemeColors.Text;
                else connectBtn.Modulate = AP_Atlas.Core.ThemeColors.Success;
                connectBtn.Pressed += () =>
                {
                    // The card outlives the state it was built in: check now.
                    var current = ProfileById(capturedProfileId);
                    if (current == null || _connectingSlots.Contains(SlotKey(capturedProfileId, capturedSlotName))) return;
                    var live = ActiveSlotNodes().OfType<SlotTrackerControl>().FirstOrDefault(s => s.ProfileId == capturedProfileId && s.SlotName == capturedSlotName);
                    if (live?.Session?.Socket.Connected == true) return;
                    OnConnectSlotPressed(capturedSlotName, current);
                };
                row.AddChild(connectBtn);
                var disconnectBtn = new Button
                {
                    Name = "DisconnectBtn",
                    Icon = _iconDisconnect,
                    Text = "",
                    CustomMinimumSize = new Godot.Vector2(32, 32),
                    SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter,
                    SizeFlagsHorizontal = Godot.Control.SizeFlags.ShrinkCenter,
                    ExpandIcon = true,
                    IconAlignment = HorizontalAlignment.Center,
                    Disabled = !isSocketConnected,
                    TooltipText = "Disconnect"
                };
                disconnectBtn.SetMeta("is_icon_button", true);
                disconnectBtn.AddThemeColorOverride("icon_disabled_color", AP_Atlas.Core.ThemeColors.Disabled);
                if (isSocketConnected) disconnectBtn.Modulate = AP_Atlas.Core.ThemeColors.Danger;
                else disconnectBtn.Modulate = AP_Atlas.Core.ThemeColors.Disabled;
                disconnectBtn.Pressed += () =>
                {
                    DisconnectSlot(profile.Id, slotName);
                };
                row.AddChild(disconnectBtn);
                cardVBox.AddChild(row);
                // Stats calculation and persistence
                int total = 0, complete = 0, logic = 0;
                bool hasData = false;
                bool logicHidden = false;
                System.DateTime lastUpdated = System.DateTime.MinValue;
                if (isConnected && session != null)
                {
                    total = session.TotalLocationsCount;
                    complete = session.CheckedLocationsCount;
                    logic = session.ActiveLogicCount;
                    logicHidden = session.LogicHidden;
                    hasData = true;
                    lastUpdated = System.DateTime.Now;
                    if (profile.SavedStats == null) profile.SavedStats = new Dictionary<string, SlotStats>();
                    if (!profile.SavedStats.ContainsKey(slotName)) profile.SavedStats[slotName] = new SlotStats();
                    profile.SavedStats[slotName].TotalCount = total;
                    profile.SavedStats[slotName].CompleteCount = complete;
                    if (!logicHidden) profile.SavedStats[slotName].LogicCount = logic;
                    profile.SavedStats[slotName].LastUpdated = lastUpdated;
                }
                else if (profile.SavedStats != null && profile.SavedStats.TryGetValue(slotName, out var saved))
                {
                    total = saved.TotalCount;
                    complete = saved.CompleteCount;
                    logic = saved.LogicCount;
                    hasData = true;
                    lastUpdated = saved.LastUpdated;
                }
                int percent = total > 0 ? (int)System.Math.Round((double)complete / total * 100.0) : 0;
                var statsContainer = new PanelContainer { Name = "StatsContainer", SizeFlagsHorizontal = SizeFlags.ExpandFill };
                var statsBg = new StyleBoxFlat
                {
                    BgColor = AP_Atlas.Core.ThemeColors.SurfaceDeep,
                    CornerRadiusTopLeft = 3,
                    CornerRadiusTopRight = 3,
                    CornerRadiusBottomLeft = 3,
                    CornerRadiusBottomRight = 3,
                    ContentMarginLeft = 4,
                    ContentMarginRight = 4,
                    ContentMarginTop = 3,
                    ContentMarginBottom = 3
                };
                statsContainer.AddThemeStyleboxOverride("panel", statsBg);
                var statsHBox = new HBoxContainer { Name = "StatsHBox", SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
                statsHBox.AddThemeConstantOverride("separation", 2);
                statsContainer.AddChild(statsHBox);
                void AddKpiCol(string title, string val, Godot.Color valColor, string name = null)
                {
                    var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    if (name != null) col.Name = name;
                    col.AddThemeConstantOverride("separation", 0);
                    var lblTitle = new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center };
                    lblTitle.SetMeta("font_size_ratio", 0.55);
                    lblTitle.AddThemeFontSizeOverride("font_size", 9);
                    lblTitle.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.TextSubtle);
                    col.AddChild(lblTitle);
                    var lblVal = new Label { Text = val, HorizontalAlignment = HorizontalAlignment.Center };
                    lblVal.SetMeta("font_size_ratio", 0.65);
                    lblVal.AddThemeFontSizeOverride("font_size", 10);
                    lblVal.AddThemeColorOverride("font_color", valColor);
                    col.AddChild(lblVal);
                    statsHBox.AddChild(col);
                }
                AddKpiCol("Total", total.ToString(), AP_Atlas.Core.ThemeColors.TextMuted);
                AddKpiCol("Done", complete.ToString(), AP_Atlas.Core.ThemeColors.Progress);
                AddKpiCol("%", $"{percent}%", percent >= 100 ? AP_Atlas.Core.ThemeColors.Success : (percent > 0 ? AP_Atlas.Core.ThemeColors.Progress : AP_Atlas.Core.ThemeColors.TextMuted));
                if (logicHidden) AddKpiCol("Logic", "—", AP_Atlas.Core.ThemeColors.TextSubtle);
                else AddKpiCol("Logic", logic.ToString(), logic > 0 ? AP_Atlas.Core.ThemeColors.Success : AP_Atlas.Core.ThemeColors.TextSubtle);
                // Special-item progress for games that have a special list (e.g. items needed to goal).
                if (isConnected && session != null)
                {
                    var (specialGot, specialTotal) = session.SpecialItemProgress();
                    if (specialTotal > 0)
                        AddKpiCol("◆", $"{specialGot}/{specialTotal}", specialGot >= specialTotal ? AP_Atlas.Core.ThemeColors.Success : AP_Atlas.Core.Annotations.SpecialColor, "SpecialKpi");
                }
                else if (AP_Atlas.Core.Annotations.SpecialItemNames(gameName).Any())
                {
                    AddKpiCol("◆", AP_Atlas.Core.Annotations.SpecialItemNames(gameName).Count().ToString(), AP_Atlas.Core.ThemeColors.TextSubtle, "SpecialKpi");
                }
                cardVBox.AddChild(statsContainer);
                var footerHBox = new HBoxContainer { Name = "FooterHBox", SizeFlagsHorizontal = SizeFlags.ExpandFill };
                var statusFooter = new Label
                {
                    Name = "StatusFooter",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                statusFooter.SetMeta("font_size_ratio", 0.50);
                statusFooter.AddThemeFontSizeOverride("font_size", 8);
                var gameNameFooter = new Label
                {
                    Name = "GameName",
                    Text = gameName,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                gameNameFooter.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.TextSubtle);
                gameNameFooter.SetMeta("font_size_ratio", 0.50);
                gameNameFooter.AddThemeFontSizeOverride("font_size", 8);
                var cheeseBadge = new Label { Name = "CheeseBadge", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Pass, Visible = false };
                cheeseBadge.SetMeta("font_size_ratio", 0.50);
                cheeseBadge.AddThemeFontSizeOverride("font_size", 8);
                footerHBox.AddChild(statusFooter);
                footerHBox.AddChild(cheeseBadge);
                footerHBox.AddChild(gameNameFooter);
                UpdateCheeseBadge(cheeseBadge, profile.Id, slotName);
                if (isConnected)
                {
                    statusFooter.Text = session.RaceRestricted ? "● Live · Race mode" : "● Live";
                    statusFooter.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Success);
                }
                else if (isConnecting)
                {
                    statusFooter.Text = "◌ Connecting...";
                    statusFooter.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Pending);
                }
                else
                {
                    if (hasData && lastUpdated != System.DateTime.MinValue)
                    {
                        statusFooter.Text = $"Last update: {lastUpdated:MM/dd HH:mm}";
                    }
                    else
                    {
                        statusFooter.Text = "Not connected yet";
                    }
                    statusFooter.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.TextSubtle);
                }
                cardVBox.AddChild(footerHBox);
                _activeSessionsList.AddChild(cardPanel);
            }
            _activeSessionsList.AddChild(new HSeparator { CustomMinimumSize = new Godot.Vector2(0, 5) });
        }
        SetFontSizeRecursive(_activeSessionsList, SlotListFontSize);
    }

    /// <summary>The text size of the slot lists (the sidebar's and a multiworld's). Fixed: font sizes are reworked with the new shell.</summary>
    private const int SlotListFontSize = 16;
    private void UpdateSlotStatuses()
    {
        using var __perf = AP_Atlas.Core.PerfMonitor.Measure("Update slot status lights");
        _sessions?.CheckForDrops();

        _spinnerIndex = (_spinnerIndex + 1) % _spinnerFrames.Length;
        // Also update sidebar buttons directly
        if (_activeSessionsList != null)
        {
            foreach (Node child in _activeSessionsList.GetChildren())
            {
                if (child is PanelContainer card)
                {
                    var cardVBox = card.GetChildOrNull<VBoxContainer>(0);
                    if (cardVBox != null)
                    {
                        var row = cardVBox.GetChildOrNull<HBoxContainer>(0);
                        if (row != null)
                        {
                            var btn = row.GetNodeOrNull<Button>("ConnectBtn");
                            if (btn != null && btn.HasMeta("slot_name"))
                            {
                                string slotName = btn.GetMeta("slot_name").AsString();
                                string profileId = btn.HasMeta("profile_id") ? btn.GetMeta("profile_id").AsString() : null;
                                MultiworldProfile profile = _profiles.FirstOrDefault(p => p.Id == profileId);
                                bool isSocketConnected = false;
                                bool isFullyLoaded = false;
                                SlotTrackerControl activeSlot = null;
                                if (_contentStage != null)
                                {
                                    foreach (Node active in ActiveSlotNodes())
                                    {
                                        if (active is SlotTrackerControl slot && slot.SlotName == slotName && (profileId == null || slot.ProfileId == profileId))
                                        {
                                            activeSlot = slot;
                                            isSocketConnected = slot.Session != null && slot.Session.Socket.Connected;
                                            isFullyLoaded = slot.IsFullyLoaded;
                                            break;
                                        }
                                    }
                                }
                                bool isConnecting = _connectingSlots.Contains(SlotKey(profileId, slotName)) || (isSocketConnected && !isFullyLoaded);
                                bool isConnected = isSocketConnected && isFullyLoaded;
                                var disconnectBtn = row.GetNodeOrNull<Button>("DisconnectBtn");
                                if (isConnecting)
                                {
                                    btn.Icon = null;
                                    btn.Text = _spinnerFrames[_spinnerIndex];
                                    btn.Disabled = false;
                                    btn.Modulate = AP_Atlas.Core.ThemeColors.Text;
                                    if (disconnectBtn != null) { disconnectBtn.Disabled = true; disconnectBtn.Modulate = AP_Atlas.Core.ThemeColors.Disabled; }
                                }
                                else if (isConnected)
                                {
                                    btn.Icon = _iconCheck;
                                    btn.Text = "";
                                    btn.Disabled = true;
                                    btn.Modulate = AP_Atlas.Core.ThemeColors.Success;
                                    if (disconnectBtn != null) { disconnectBtn.Disabled = false; disconnectBtn.Modulate = AP_Atlas.Core.ThemeColors.Danger; }
                                }
                                else
                                {
                                    btn.Icon = _iconConnect;
                                    btn.Text = "";
                                    btn.Disabled = false;
                                    btn.Modulate = AP_Atlas.Core.ThemeColors.Text;
                                    if (disconnectBtn != null) { disconnectBtn.Disabled = true; disconnectBtn.Modulate = AP_Atlas.Core.ThemeColors.Disabled; }
                                }
                                int total = 0, complete = 0, logic = 0;
                                bool hasSaved = false;
                                System.DateTime lastUpdated = System.DateTime.MinValue;
                                bool logicHidden = false;
                                if (isConnected && activeSlot != null)
                                {
                                    total = activeSlot.TotalLocationsCount;
                                    complete = activeSlot.CheckedLocationsCount;
                                    logic = activeSlot.ActiveLogicCount;
                                    logicHidden = activeSlot.LogicHidden;
                                    hasSaved = true;
                                    lastUpdated = System.DateTime.Now;
                                    if (profile != null)
                                    {
                                        if (profile.SavedStats == null) profile.SavedStats = new Dictionary<string, SlotStats>();
                                        if (!profile.SavedStats.ContainsKey(slotName)) profile.SavedStats[slotName] = new SlotStats();
                                        profile.SavedStats[slotName].TotalCount = total;
                                        profile.SavedStats[slotName].CompleteCount = complete;
                                        if (!logicHidden) profile.SavedStats[slotName].LogicCount = logic;
                                        profile.SavedStats[slotName].LastUpdated = lastUpdated;
                                    }
                                }
                                else if (profile != null && profile.SavedStats != null && profile.SavedStats.TryGetValue(slotName, out var saved))
                                {
                                    total = saved.TotalCount;
                                    complete = saved.CompleteCount;
                                    logic = saved.LogicCount;
                                    hasSaved = true;
                                    lastUpdated = saved.LastUpdated;
                                }
                                int percent = total > 0 ? (int)System.Math.Round((double)complete / total * 100.0) : 0;
                                var statsContainer = cardVBox.GetNodeOrNull<PanelContainer>("StatsContainer");
                                if (statsContainer != null)
                                {
                                    var statsHBox = statsContainer.GetNodeOrNull<HBoxContainer>("StatsHBox");
                                    if (statsHBox != null)
                                    {
                                        var totalVBox = statsHBox.GetChildOrNull<VBoxContainer>(0);
                                        var compVBox = statsHBox.GetChildOrNull<VBoxContainer>(1);
                                        var percentVBox = statsHBox.GetChildOrNull<VBoxContainer>(2);
                                        var availVBox = statsHBox.GetChildOrNull<VBoxContainer>(3);
                                        if (totalVBox != null)
                                        {
                                            var valTotal = totalVBox.GetChildOrNull<Label>(1);
                                            if (valTotal != null) valTotal.Text = total.ToString();
                                        }
                                        if (compVBox != null)
                                        {
                                            var valComp = compVBox.GetChildOrNull<Label>(1);
                                            if (valComp != null) valComp.Text = complete.ToString();
                                        }
                                        if (percentVBox != null)
                                        {
                                            var valPercent = percentVBox.GetChildOrNull<Label>(1);
                                            if (valPercent != null)
                                            {
                                                valPercent.Text = $"{percent}%";
                                                if (percent >= 100) SetFontColor(valPercent, AP_Atlas.Core.ThemeColors.Success);
                                                else if (percent > 0) SetFontColor(valPercent, AP_Atlas.Core.ThemeColors.Progress);
                                                else SetFontColor(valPercent, null);
                                            }
                                        }
                                        if (availVBox != null)
                                        {
                                            var valAvail = availVBox.GetChildOrNull<Label>(1);
                                            if (valAvail != null)
                                            {
                                                valAvail.Text = logicHidden ? "—" : logic.ToString();
                                                if (logic > 0 && !logicHidden) SetFontColor(valAvail, AP_Atlas.Core.ThemeColors.Success);
                                                else SetFontColor(valAvail, null);
                                            }
                                        }
                                    }
                                }
                                var special = statsContainer?.GetNodeOrNull<HBoxContainer>("StatsHBox")?.GetNodeOrNull<VBoxContainer>("SpecialKpi")?.GetChildOrNull<Label>(1);
                                if (special != null && isConnected && activeSlot != null)
                                {
                                    var (specialGot, specialTotal) = activeSlot.SpecialItemProgress();
                                    if (specialTotal > 0)
                                    {
                                        special.Text = $"{specialGot}/{specialTotal}";
                                        SetFontColor(special, specialGot >= specialTotal ? AP_Atlas.Core.ThemeColors.Success : AP_Atlas.Core.Annotations.SpecialColor);
                                    }
                                }
                                var game = cardVBox.GetNodeOrNull<HBoxContainer>("FooterHBox")?.GetNodeOrNull<Label>("GameName");
                                if (game != null && !string.IsNullOrEmpty(activeSlot?.Game)) game.Text = activeSlot.Game;
                                var statusFooter = cardVBox.GetNodeOrNull<HBoxContainer>("FooterHBox")?.GetNodeOrNull<Label>("StatusFooter");
                                if (statusFooter != null)
                                {
                                    if (isConnected)
                                    {
                                        statusFooter.Text = activeSlot != null && activeSlot.RaceRestricted ? "● Live · Race mode" : "● Live";
                                        SetFontColor(statusFooter, AP_Atlas.Core.ThemeColors.Success);
                                    }
                                    else if (isConnecting)
                                    {
                                        statusFooter.Text = "◌ Connecting...";
                                        SetFontColor(statusFooter, AP_Atlas.Core.ThemeColors.Pending);
                                    }
                                    else
                                    {
                                        if (hasSaved && lastUpdated != System.DateTime.MinValue)
                                        {
                                            statusFooter.Text = $"Last update: {lastUpdated:MM/dd HH:mm}";
                                        }
                                        else
                                        {
                                            statusFooter.Text = "Not connected yet";
                                        }
                                        SetFontColor(statusFooter, AP_Atlas.Core.ThemeColors.TextSubtle);
                                    }
                                }
                                UpdateCheeseBadge(cardVBox.GetNodeOrNull<HBoxContainer>("FooterHBox")?.GetNodeOrNull<Label>("CheeseBadge"), profileId, slotName);
                            }
                        }
                    }
                }
            }
        }
        if (_slotsListVBox == null || _selectedProfile == null) return;
        foreach (Node n in _slotsListVBox.GetChildren())
        {
            if (n is HBoxContainer row && row.HasMeta("slot_name"))
            {
                string slotName = row.GetMeta("slot_name").AsString();
                SlotTrackerControl activeSlot = null;
                if (_contentStage != null)
                {
                    foreach (Node active in ActiveSlotNodes())
                    {
                        if (active is SlotTrackerControl slot && slot.ProfileId == _selectedProfile.Id && slot.SlotName == slotName)
                        {
                            activeSlot = slot;
                            break;
                        }
                    }
                }
                var connectBtn = row.GetNodeOrNull<Button>("ConnectBtn");
                var disconnectBtn = row.GetNodeOrNull<Button>("DisconnectBtn");
                if (connectBtn != null)
                {
                    bool isSocketConnected = activeSlot != null && activeSlot.Session != null && activeSlot.Session.Socket.Connected;
                    bool isConnecting = _connectingSlots.Contains(SlotKey(_selectedProfile.Id, slotName)) || (isSocketConnected && !activeSlot.IsFullyLoaded);
                    bool isConnected = isSocketConnected && activeSlot.IsFullyLoaded;
                    if (isConnecting)
                    {
                        connectBtn.Icon = null;
                        connectBtn.Text = _spinnerFrames[_spinnerIndex];
                        connectBtn.Disabled = false;
                        connectBtn.Modulate = AP_Atlas.Core.ThemeColors.Text;
                        if (disconnectBtn != null) { disconnectBtn.Disabled = true; disconnectBtn.Modulate = AP_Atlas.Core.ThemeColors.Disabled; }
                    }
                    else if (isConnected)
                    {
                        connectBtn.Icon = _iconCheck;
                        connectBtn.Text = "";
                        connectBtn.Disabled = true;
                        connectBtn.Modulate = AP_Atlas.Core.ThemeColors.Success;
                        if (disconnectBtn != null) { disconnectBtn.Disabled = false; disconnectBtn.Modulate = AP_Atlas.Core.ThemeColors.Danger; }
                    }
                    else
                    {
                        connectBtn.Icon = _iconConnect;
                        connectBtn.Text = "";
                        connectBtn.Disabled = false;
                        connectBtn.Modulate = AP_Atlas.Core.ThemeColors.Success;
                        if (disconnectBtn != null) { disconnectBtn.Disabled = true; disconnectBtn.Modulate = AP_Atlas.Core.ThemeColors.Disabled; }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sets a label's font colour (null: the theme's) only when it changes. Setting an override redraws and re-lays out
    /// the label even when it's the same colour, and the slot statuses refresh twice a second: Atlas wouldn't idle.
    /// </summary>
    private static void SetFontColor(Control label, Color? color)
    {
        if (color is { } wanted)
        {
            if (label.HasThemeColorOverride("font_color") && label.GetThemeColor("font_color") == wanted) return;
            label.AddThemeColorOverride("font_color", wanted);
        }
        else if (label.HasThemeColorOverride("font_color")) label.RemoveThemeColorOverride("font_color");
    }
}
