#nullable disable
using System.Linq;
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AP_Atlas.Core.Connections;

/// <summary>
/// Connections, as the window shows them: connecting slots (one at a time, with the overlay), building a slot's view once
/// it has logged in, and reporting drops and reconnects. The connections themselves (time limits, drops, careful
/// reconnects, closing) are AP_Atlas.Core's SessionManager.
/// </summary>
public partial class MainTrackerWindow : Control, AP_Atlas.UI.IPropertiesHost
{
    private bool _shuttingDown = false;
    private void GracefulShutdown() => AP_Atlas.Core.Async.Fire(GracefulShutdownAsync(), "closing Atlas", tellUser: false);
    private async Task GracefulShutdownAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try
        {
            var win = GetWindow();
            _appSettings.WindowMaximized = win.Mode == Window.ModeEnum.Maximized;
            if (!_appSettings.WindowMaximized)
            {
                _appSettings.WindowWidth = win.Size.X;
                _appSettings.WindowHeight = win.Size.Y;
                _appSettings.WindowX = win.Position.X;
                _appSettings.WindowY = win.Position.Y;
            }
            DataManager.SaveSettings(_appSettings);
            // What changed during the session (slot stats, links) is kept even if nothing else saved the profiles.
            DataManager.SaveProfiles(_profiles);
            if (_globalStatusLabel != null) _globalStatusLabel.Text = "Disconnecting sessions...";
            LogToSystem("[color=yellow]Shutting down... Disconnecting active slots...[/color]");
            // Close every session (connected slots and any still connecting) with a proper close frame, so the server
            // drops them at once instead of waiting for a timeout.
            if (_sessions != null)
            {
                var (count, inTime) = await _sessions.CloseAllAsync(TimeSpan.FromSeconds(3));
                if (count > 0 && inTime) LogToSystem($"[color=yellow]Closed {count} server connection(s).[/color]");
                else if (count > 0) AP_Atlas.Core.Logger.LogWarning("Some server connections did not confirm closing within 3 seconds; the OS closes them as the app exits.");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Error during shutdown: {ex}");
        }
        finally
        {
            GetTree().Quit();
        }
    }
    /// <summary>Slots with a login in flight, keyed by SlotKey(profileId, slotName).</summary>
    private System.Collections.Generic.HashSet<string> _connectingSlots = new System.Collections.Generic.HashSet<string>();
    private static string SlotKey(string profileId, string slotName) => profileId + "|" + slotName;
    /// <summary>Every connection to an Archipelago server: connecting, drops, careful reconnects and closing.</summary>
    private SessionManager _sessions;

    /// <summary>Starts the connections and shows what they report (once, from _Ready, after the settings load).</summary>
    private void StartSessions()
    {
        _sessions = new SessionManager(DataManager.DataPackages, UiTestRequested ? UiTestSessions : null) { AutoReconnect = _appSettings.AutoReconnect };
        _sessions.Dropped += (slot, reason) => AP_Atlas.UI.Ui.Defer(this, () => OnSessionDropped(slot, reason));
        _sessions.ReconnectScheduled += (slot, attempt, tries, wait) => AP_Atlas.UI.Ui.Defer(this, () => OnReconnectScheduled(slot, attempt, tries, wait));
        _sessions.Reconnected += connected => AP_Atlas.UI.Ui.Defer(this, () => OnReconnected(connected));
        _sessions.ReconnectStopped += (slot, refusal) => AP_Atlas.UI.Ui.Defer(this, () => OnReconnectStopped(slot, refusal));
        _sessions.SocketError += (slot, message) => AP_Atlas.UI.Ui.Defer(this, () =>
        {
            LogToSystem("[color=red]Socket Error (" + slot.SlotName + "):[/color] " + message);
            if (_globalStatusLabel != null) _globalStatusLabel.Text = "Socket Error: " + message;
        });
    }

    private MultiworldProfile ProfileById(string profileId) => _profiles?.FirstOrDefault(p => p.Id == profileId);

    private void OnSessionDropped(SlotId slot, string reason)
    {
        if (_shuttingDown) return;
        LogToSystem($"[color=yellow]Connection to {slot.SlotName} dropped ({reason}).[/color]");
        if (_globalStatusLabel != null) _globalStatusLabel.Text = "Disconnected: " + slot.SlotName;
        UpdateSidebar();
        if (_sessions.AutoReconnect) return;
        var profile = ProfileById(slot.ProfileId);
        if (profile != null) ShowToast($"Connection to {slot.SlotName} was lost.", Godot.Colors.Orange, "Reconnect", () => OnConnectSlotPressed(slot.SlotName, profile));
    }

    private void OnReconnectScheduled(SlotId slot, int attempt, int tries, TimeSpan wait)
    {
        if (_shuttingDown) return;
        LogToSystem($"[color=orange]Reconnecting {slot.SlotName} in {wait.TotalSeconds:0} s[/color] (try {attempt} of {tries}).");
        if (attempt == 1) ShowToast($"Connection to {slot.SlotName} lost. Reconnecting automatically…", Godot.Colors.Orange);
    }

    private void OnReconnected(ConnectedSlot connected)
    {
        var profile = ProfileById(connected.Slot.ProfileId);
        if (profile == null || _shuttingDown)
        {
            // The multiworld was deleted (or Atlas is closing) while it reconnected.
            AP_Atlas.Core.Async.Fire(_sessions.DisconnectAsync(connected.Slot), "closing a server connection", tellUser: false);
            return;
        }
        BuildSlotTracker(profile, connected);
        LogToSystem($"[color=lime]Reconnected {connected.Slot.SlotName}.[/color]");
        ShowToast($"Reconnected {connected.Slot.SlotName}.", Godot.Colors.LimeGreen);
    }

    private void OnReconnectStopped(SlotId slot, string refusal)
    {
        if (_shuttingDown) return;
        if (refusal != null)
        {
            // The server answered and refused (slot gone, wrong password, room changed): retrying won't help.
            LogToSystem($"[color=red]Stopped reconnecting {slot.SlotName}: the server refused the login ({refusal}).[/color]");
            ShowToast($"{slot.SlotName} can't reconnect: {refusal}", Godot.Colors.Salmon);
        }
        else
        {
            LogToSystem($"[color=orange]Stopped trying to reconnect {slot.SlotName} after {_sessions.ReconnectTries} tries over about 20 minutes.[/color] Reconnect it when the server is back.");
            var profile = ProfileById(slot.ProfileId);
            if (profile != null)
                ShowToast($"{slot.SlotName} couldn't reconnect. The server may be down or the room closed.", Godot.Colors.Orange, "Try again", () => OnConnectSlotPressed(slot.SlotName, profile));
        }
        UpdateSidebar();
    }
    private void DisconnectSlot(string profileId, string slotName)
    {
        // The user chose to disconnect: the session closes properly, and no automatic reconnect follows.
        if (_sessions != null) AP_Atlas.Core.Async.Fire(_sessions.DisconnectAsync(new SlotId(profileId, slotName)), "closing a server connection", tellUser: false);
        if (_terminalStage == null) return;
        if (ActiveSlotNodes().OfType<SlotTrackerControl>().Any(slot => slot.ProfileId == profileId && slot.SlotName == slotName))
            LogToSystem("[color=yellow]Disconnected slot: " + slotName + "[/color]");
        DataManager.SaveProfiles(_profiles);
        AP_Atlas.UI.Ui.Defer(this, UpdateSidebar);
    }
    private bool _isConnectingSlot = false;
    private void ShowConnectingOverlay(string message)
    {
        AP_Atlas.UI.Ui.Defer(this, () =>
        {
            if (_connectingOverlay == null)
            {
                _connectingOverlay = new CenterContainer { Name = "ConnectingOverlay" };
                _connectingOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                var popup = new PanelContainer { Name = "PopupPanel" };
                popup.CustomMinimumSize = new Godot.Vector2(450, 150);
                var style = new StyleBoxFlat { BgColor = new Godot.Color(0.12f, 0.12f, 0.15f, 0.95f), CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10, BorderWidthBottom = 2, BorderWidthTop = 2, BorderWidthLeft = 2, BorderWidthRight = 2, BorderColor = Godot.Colors.DarkGray };
                popup.AddThemeStyleboxOverride("panel", style);
                var lbl = new Label { Name = "MessageLabel", HorizontalAlignment = HorizontalAlignment.Center };
                lbl.AddThemeFontSizeOverride("font_size", 28);
                lbl.AddThemeColorOverride("font_color", Godot.Colors.Yellow);
                var popupCenter = new CenterContainer { Name = "CenterContainer" };
                popupCenter.AddChild(lbl);
                popup.AddChild(popupCenter);
                _connectingOverlay.AddChild(popup);
                var canvas = new CanvasLayer { Layer = 50 };
                canvas.AddChild(_connectingOverlay);
                AddChild(canvas);
            }
            var msgLabel = _connectingOverlay.GetNodeOrNull<Label>("PopupPanel/CenterContainer/MessageLabel");
            if (msgLabel != null) msgLabel.Text = message;
            _connectingOverlay.Visible = true;
            _serverInput.Editable = false;
            _nameInput.Editable = false;
            _passwordInput.Editable = false;
            if (_connectAllBtn != null) _connectAllBtn.Disabled = true;
        });
    }
    private void HideConnectingOverlay()
    {
        AP_Atlas.UI.Ui.Defer(this, () =>
        {
            if (_connectingOverlay != null) _connectingOverlay.Visible = false;
            _serverInput.Editable = true;
            _nameInput.Editable = true;
            _passwordInput.Editable = true;
            if (_connectAllBtn != null) _connectAllBtn.Disabled = false;
        });
    }
    private void OnConnectAllPressed() => AP_Atlas.Core.Async.Fire(OnConnectAllPressedAsync(), "connecting the multiworld's slots");
    private async Task OnConnectAllPressedAsync()
    {
        if (_isConnectingSlot || _selectedProfile == null || _selectedProfile.Slots.Count == 0) return;
        _isConnectingSlot = true;
        try
        {
            AP_Atlas.Core.Logger.LogInfo("Starting sequential connection for all slots...");
            foreach (var slotName in _selectedProfile.Slots)
            {
                if (string.IsNullOrWhiteSpace(slotName)) continue;
                ShowConnectingOverlay($"CONNECTING TO\n{slotName}...");
                await ConnectSlotInternalAsync(slotName, _selectedProfile);
                await ToSignal(GetTree().CreateTimer(1.5f), "timeout");
            }
        }
        finally
        {
            HideConnectingOverlay();
            _isConnectingSlot = false;
        }
    }
    private void OnConnectSlotPressed(string slotName, MultiworldProfile profile) => AP_Atlas.Core.Async.Fire(OnConnectSlotPressedAsync(slotName, profile), $"connecting {slotName}");
    private async Task OnConnectSlotPressedAsync(string slotName, MultiworldProfile profile)
    {
        if (_isConnectingSlot) return;
        _isConnectingSlot = true;
        try
        {
            ShowConnectingOverlay($"CONNECTING TO\n{slotName}...");
            await ConnectSlotInternalAsync(slotName, profile);
        }
        finally
        {
            HideConnectingOverlay();
            _isConnectingSlot = false;
        }
    }
    private async System.Threading.Tasks.Task<bool> ConnectSlotInternalAsync(string slotName, MultiworldProfile profile)
    {
        if (_shuttingDown || _sessions == null) return false;
        if (string.IsNullOrWhiteSpace(profile.ServerUrl))
        {
            _statusLabel.Text = "Status: Server URL cannot be empty";
            _statusLabel.AddThemeColorOverride("font_color", Colors.Red);
            if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Error";
            return false;
        }
        if (string.IsNullOrWhiteSpace(slotName))
        {
            _statusLabel.Text = "Status: Slot Name cannot be empty";
            _statusLabel.AddThemeColorOverride("font_color", Colors.Red);
            if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Error";
            return false;
        }
        _statusLabel.Text = "Status: Connecting to " + slotName + "...";
        _statusLabel.AddThemeColorOverride("font_color", Colors.Yellow);
        if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connecting to " + profile.ServerUrl + " as " + slotName + "...";
        LogToSystem("[color=cyan]Attempting to connect to " + profile.ServerUrl + " as " + slotName + "...[/color]");
        _connectingSlots.Add(SlotKey(profile.Id, slotName));
        UpdateSidebar();
        var login = new SlotLogin(new SlotId(profile.Id, slotName), profile.ServerUrl, string.IsNullOrEmpty(profile.Password) ? null : profile.Password);
        ConnectResult result;
        try
        {
            result = await _sessions.ConnectAsync(login);
        }
        catch (Exception ex)
        {
            // SessionManager reports failures as results; anything else is a bug, shown like a failed connection.
            AP_Atlas.Core.Logger.LogError($"Connecting {slotName} failed unexpectedly: {ex}");
            result = new ConnectResult(ConnectOutcome.Failed, ex.Message);
        }
        AP_Atlas.UI.Ui.Defer(this, () => ShowConnectResult(profile, slotName, result));
        return true;
    }

    private void ShowConnectResult(MultiworldProfile profile, string slotName, ConnectResult result)
    {
        if (result.Outcome == ConnectOutcome.Connected && !_shuttingDown)
        {
            BuildSlotTracker(profile, result.Slot);
            return;
        }
        if (result.Slot != null) AP_Atlas.Core.Async.Fire(_sessions.DisconnectAsync(result.Slot.Slot), "closing a server connection", tellUser: false);
        _connectingSlots.Remove(SlotKey(profile.Id, slotName));
        UpdateSidebar();
        if (_shuttingDown || result.Outcome == ConnectOutcome.Cancelled) return;
        _statusLabel.AddThemeColorOverride("font_color", Colors.Red);
        switch (result.Outcome)
        {
            case ConnectOutcome.TimedOut:
                _statusLabel.Text = "Status: Connection Timeout";
                if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Timeout (" + slotName + ")";
                LogToSystem("[color=red]Connection timed out for " + slotName + ":[/color] " + result.Message);
                break;
            case ConnectOutcome.Refused:
                _statusLabel.Text = "Status: Failed to connect:\n" + result.Message;
                if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Failed (" + slotName + ")";
                LogToSystem("[color=red]The server refused the login for " + slotName + ":[/color] " + result.Message);
                break;
            case ConnectOutcome.Unreachable:
                _statusLabel.Text = "Status: Failed to connect:\n" + result.Message;
                if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Failed (" + slotName + ")";
                LogToSystem("[color=orange]Couldn't reach the server for " + slotName + ":[/color] " + result.Message);
                break;
            default:
                _statusLabel.Text = "Status: Connection Error:\n" + result.Message;
                if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Error";
                LogToSystem("[color=red]Couldn't connect " + slotName + ":[/color] " + result.Message);
                break;
        }
    }

    /// <summary>Builds the view for a slot that logged in (or reconnected), replacing its earlier view.</summary>
    private void BuildSlotTracker(MultiworldProfile profile, ConnectedSlot connected)
    {
        string slotName = connected.Slot.SlotName;
        var session = connected.Session;
        _statusLabel.Text = "Status: Connected successfully as " + slotName + "!";
        _statusLabel.AddThemeColorOverride("font_color", Colors.Green);
        if (_globalStatusLabel != null) _globalStatusLabel.Text = "Booting Engine for " + slotName + "...";
        LogToSystem("[color=lime]Successfully authenticated as " + slotName + ".[/color]");
        foreach (Node n in ActiveSlotNodes())
        {
            if (n is SlotTrackerControl oldSlot && oldSlot.ProfileId == profile.Id && oldSlot.SlotName == slotName)
            {
                // Its session was closed by the session manager when the new one logged in.
                if (_currentSelectedSlot == oldSlot) _currentSelectedSlot = null;
                oldSlot.QueueFree();
            }
        }
        if (!profile.SavedStats.ContainsKey(slotName)) profile.SavedStats[slotName] = new SlotStats();
        profile.SavedStats[slotName].GameName = session.ConnectionInfo.Game;
        profile.SavedStats[slotName].SlotNumber = session.ConnectionInfo.Slot;
        DataManager.SaveProfiles(_profiles);
        // The model takes the session's events from here on (and the messages that arrived before it).
        var model = new AP_Atlas.Core.SlotModel(connected);
        var slotTracker = new SlotTrackerControl(model, _appSettings,
            (msg) => { if (_globalStatusLabel != null) _globalStatusLabel.Text = msg; },
            (msg) => { LogToDebug(msg, slotName); }
        );
        slotTracker.ResolveOtherSlotLogic = (slot, loc) => ResolveSlotLogic(slotTracker, slot, loc);
        slotTracker.ShowToast = ShowToast;
        slotTracker.ShowActionToast = ShowToast;
        slotTracker.OpenEngineSetup = OpenEngineSetup;
        slotTracker.AccuracyChanged += () => _propertiesPanel?.QueueRefresh();
        // When this slot's logic moves, hints at its locations change for the other slots of the same multiworld.
        slotTracker.StateChanged += () =>
        {
            foreach (var sibling in SiblingSlots(slotTracker)) sibling.RefreshHints();
            _propertiesPanel?.QueueRefresh();
        };
        _terminalStage.AddChild(slotTracker);
        PreMountSlotViews(slotTracker);
        _connectingSlots.Remove(SlotKey(profile.Id, slotName));
        UpdateSidebar();
        _currentSelectedSlot = slotTracker; RefreshContextViews();
        _sphereTab?.FollowSlot(profile.Id, slotName);
        // Scale just the new slot's text client once its UI is built (not the whole window).
        var timer = GetTree().CreateTimer(0.1);
        timer.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(slotTracker)) SetFontSizeRecursive(slotTracker, _appSettings.ConsoleFontSize);
        };
    }
}
