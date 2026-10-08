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
    /// <summary>Closes everything, then runs <paramref name="andThen"/> (an update's swap); a problem it returns keeps Atlas open and shows it.</summary>
    private void GracefulShutdown(Func<string> andThen) => AP_Atlas.Core.Async.Fire(GracefulShutdownAsync(andThen), "closing Atlas to update", tellUser: false);
    private async Task GracefulShutdownAsync(Func<string> andThen = null)
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try
        {
            var win = GetWindow();
            // In full screen (F11), what to come back to was noted when it was turned on: the window's size and place now aren't its own.
            if (win.Mode != Window.ModeEnum.Fullscreen)
            {
                _appSettings.WindowMaximized = win.Mode == Window.ModeEnum.Maximized;
                if (!_appSettings.WindowMaximized)
                {
                    _appSettings.WindowWidth = win.Size.X;
                    _appSettings.WindowHeight = win.Size.Y;
                    _appSettings.WindowX = win.Position.X;
                    _appSettings.WindowY = win.Position.Y;
                }
            }
            DataManager.SaveSettings(_appSettings);
            // What changed during the session (slot stats, links) is kept even if nothing else saved the profiles.
            DataManager.SaveProfiles(_profiles);
            ShowStatus(Tr("Closing connections…"));
            LogToSystem("Shutting down... Disconnecting active slots...", "yellow");
            // Close every session (connected slots and any still connecting) with a proper close frame, so the server
            // drops them at once instead of waiting for a timeout.
            if (_sessions != null)
            {
                var (count, inTime) = await _sessions.CloseAllAsync(TimeSpan.FromSeconds(3));
                if (count > 0 && inTime) LogToSystem($"Closed {count} server connection(s).", "yellow");
                else if (count > 0) AP_Atlas.Core.Logger.LogWarning("Some server connections did not confirm closing within 3 seconds; the OS closes them as the app exits.");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Error during shutdown: {ex}");
        }
        if (andThen != null)
        {
            string problem;
            try { problem = andThen(); }
            catch (Exception ex) { problem = ex.Message; }
            if (problem != null)
            {
                // The update didn't go in: Atlas stays open (its connections are closed; the user can connect again).
                _shuttingDown = false;
                AP_Atlas.Core.Logger.LogError("The update wasn't applied: " + problem);
                ShowToast(problem, AP_Atlas.Core.ThemeColors.Error);
                ShowStatus(Tr("The update wasn't applied."));
                return;
            }
        }
        AP_Atlas.Core.Logger.LogInfo("Closing: the window is going; the engine's processes and the rest end with it.");
        GetTree().Quit();
        // A last resort: a process that stays alive after its window closed keeps the one-instance lock for its folder, and
        // the next start says "Atlas is already open" with no Atlas in sight. Everything worth keeping was saved above.
        AP_Atlas.Core.Async.Fire(Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            AP_Atlas.Core.Logger.LogWarning("Atlas hadn't ended 10 seconds after closing; ending it now.");
            System.Environment.Exit(0);
        }), "ending Atlas after closing", tellUser: false);
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
            LogToSystem("Socket Error (" + slot.SlotName + "): " + message, "red");
            ShowStatus(Tr("{0}: connection trouble (see the System Log)").Replace("{0}", slot.SlotName));
        });
    }

    private MultiworldProfile ProfileById(string profileId) => _profiles?.FirstOrDefault(p => p.Id == profileId);

    private void OnSessionDropped(SlotId slot, string reason)
    {
        if (_shuttingDown) return;
        LogToSystem($"Connection to {slot.SlotName} dropped ({reason}).", "yellow");
        ShowStatus(Tr("{0} disconnected").Replace("{0}", slot.SlotName));
        UpdateSidebar();
        if (_sessions.AutoReconnect) return;
        var profile = ProfileById(slot.ProfileId);
        if (profile != null) ShowToast($"Connection to {slot.SlotName} was lost.", AP_Atlas.Core.ThemeColors.Warning, "Reconnect", () => OnConnectSlotPressed(slot.SlotName, profile));
    }

    private void OnReconnectScheduled(SlotId slot, int attempt, int tries, TimeSpan wait)
    {
        if (_shuttingDown) return;
        LogToSystem($"Reconnecting {slot.SlotName} in {wait.TotalSeconds:0} s (try {attempt} of {tries}).", "orange");
        if (attempt == 1) ShowToast($"Connection to {slot.SlotName} lost. Reconnecting automatically…", AP_Atlas.Core.ThemeColors.Warning);
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
        LogToSystem($"Reconnected {connected.Slot.SlotName}.", "lime");
        ShowToast($"Reconnected {connected.Slot.SlotName}.", AP_Atlas.Core.ThemeColors.Success);
    }

    private void OnReconnectStopped(SlotId slot, string refusal)
    {
        if (_shuttingDown) return;
        if (refusal != null)
        {
            // The server answered and refused (slot gone, wrong password, room changed): retrying won't help.
            LogToSystem($"Stopped reconnecting {slot.SlotName}: the server refused the login ({refusal}).", "red");
            ShowToast($"{slot.SlotName} can't reconnect: {refusal}", AP_Atlas.Core.ThemeColors.Error);
        }
        else
        {
            LogToSystem($"Stopped trying to reconnect {slot.SlotName} after {_sessions.ReconnectTries} tries over about 20 minutes. Reconnect it when the server is back.", "orange");
            var profile = ProfileById(slot.ProfileId);
            if (profile != null)
            {
                ShowToast($"{slot.SlotName} couldn't reconnect. The server may be down or the room closed.", AP_Atlas.Core.ThemeColors.Warning, "Try again", () => OnConnectSlotPressed(slot.SlotName, profile));
                CheckRoomAfterFailure(profile, slot); // a moved port, or a room asleep, from one status read
            }
        }
        UpdateSidebar();
    }
    private void DisconnectSlot(string profileId, string slotName)
    {
        // The user chose to disconnect: the session closes properly, and no automatic reconnect follows.
        if (_sessions != null) AP_Atlas.Core.Async.Fire(_sessions.DisconnectAsync(new SlotId(profileId, slotName)), "closing a server connection", tellUser: false);
        if (_terminalStage == null) return;
        if (ActiveSlotNodes().OfType<SlotTrackerControl>().Any(slot => slot.ProfileId == profileId && slot.SlotName == slotName))
            LogToSystem("Disconnected slot: " + slotName, "yellow");
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
                var style = new StyleBoxFlat { BgColor = new Godot.Color(AP_Atlas.Core.ThemeColors.SurfaceDeep, 0.95f), CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10, BorderWidthBottom = 2, BorderWidthTop = 2, BorderWidthLeft = 2, BorderWidthRight = 2, BorderColor = AP_Atlas.Core.ThemeColors.BorderSoft };
                popup.AddThemeStyleboxOverride("panel", style);
                var lbl = new Label { Name = "MessageLabel", HorizontalAlignment = HorizontalAlignment.Center };
                lbl.SetMeta("font_size_ratio", 2.0f);
                lbl.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Pending);
                var popupCenter = new CenterContainer { Name = "CenterContainer" };
                popupCenter.AddChild(lbl);
                SetFontSizeRecursive(lbl, _appSettings.GlobalFontSize);
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
        // One connect at a time through the window (the overlay names it): a press while one runs (a reconnect's own
        // attempt, Connect All, Connect now from the Add a slot dialog) waits its turn instead of being dropped without a word.
        for (int waited = 0; _isConnectingSlot && waited < 900 && !_shuttingDown; waited++) await Task.Delay(100);
        if (_isConnectingSlot || _shuttingDown) return;
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
            _statusLabel.Text = Tr("Enter the server address first: archipelago.gg and the room's port, as the room page shows them.");
            _statusLabel.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Error);
            ShowStatus(Tr("Can't connect: the server address is empty"));
            return false;
        }
        if (string.IsNullOrWhiteSpace(slotName))
        {
            _statusLabel.Text = "Status: Slot Name cannot be empty";
            _statusLabel.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Error);
            ShowStatus(Tr("Can't connect: the slot name is empty"));
            return false;
        }
        _statusLabel.Text = "Status: Connecting to " + slotName + "...";
        _statusLabel.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Pending);
        ShowStatus(Tr("Connecting {0}…").Replace("{0}", slotName));
        LogToSystem("Attempting to connect to " + profile.ServerUrl + " as " + slotName + "...", "cyan");
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
        // Never a slot for a multiworld that was deleted while it connected: that connection is just closed.
        bool deleted = ProfileById(profile.Id) == null;
        if (result.Outcome == ConnectOutcome.Connected && !_shuttingDown && !deleted)
        {
            BuildSlotTracker(profile, result.Slot);
            return;
        }
        if (result.Slot != null) AP_Atlas.Core.Async.Fire(_sessions.DisconnectAsync(result.Slot.Slot), "closing a server connection", tellUser: false);
        _connectingSlots.Remove(SlotKey(profile.Id, slotName));
        UpdateSidebar();
        if (_shuttingDown || deleted || result.Outcome == ConnectOutcome.Cancelled) return;
        _statusLabel.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Error);
        switch (result.Outcome)
        {
            case ConnectOutcome.TimedOut:
                _statusLabel.Text = "Status: Connection Timeout";
                ShowStatus(Tr("{0}: the server didn't answer in time").Replace("{0}", slotName));
                LogToSystem("Connection timed out for " + slotName + ": " + result.Message, "red");
                ShowToast(Tr("{0}: the server didn't answer in time.").Replace("{0}", slotName), AP_Atlas.Core.ThemeColors.Error);
                break;
            case ConnectOutcome.Refused:
                _statusLabel.Text = "Status: Failed to connect:\n" + result.Message;
                ShowStatus(Tr("{0}: the server refused the login").Replace("{0}", slotName));
                LogToSystem("The server refused the login for " + slotName + ": " + result.Message, "red");
                ShowToast(Tr("{0}: the server refused the login ({1}).").Replace("{0}", slotName).Replace("{1}", result.Message ?? ""), AP_Atlas.Core.ThemeColors.Error);
                break;
            case ConnectOutcome.Unreachable:
                _statusLabel.Text = "Status: Failed to connect:\n" + result.Message;
                ShowStatus(Tr("{0}: couldn't reach the server").Replace("{0}", slotName));
                LogToSystem("Couldn't reach the server for " + slotName + ": " + result.Message, "orange");
                ShowToast(Tr("{0}: couldn't reach the server.").Replace("{0}", slotName), AP_Atlas.Core.ThemeColors.Error);
                break;
            default:
                _statusLabel.Text = "Status: Connection Error:\n" + result.Message;
                ShowStatus(Tr("{0}: couldn't connect").Replace("{0}", slotName));
                LogToSystem("Couldn't connect " + slotName + ": " + result.Message, "red");
                ShowToast(Tr("{0}: couldn't connect ({1}).").Replace("{0}", slotName).Replace("{1}", result.Message ?? ""), AP_Atlas.Core.ThemeColors.Error);
                break;
        }
    }

    /// <summary>Builds the view for a slot that logged in (or reconnected), replacing its earlier view.</summary>
    private void BuildSlotTracker(MultiworldProfile profile, ConnectedSlot connected)
    {
        string slotName = connected.Slot.SlotName;
        var session = connected.Session;
        _statusLabel.Text = "Status: Connected successfully as " + slotName + "!";
        _statusLabel.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Success);
        ShowStatus(Tr("{0} connected; starting its logic…").Replace("{0}", slotName));
        LogToSystem("Successfully authenticated as " + slotName + ".", "lime");
        foreach (Node n in ActiveSlotNodes())
        {
            if (n is SlotTrackerControl oldSlot && oldSlot.ProfileId == profile.Id && oldSlot.SlotName == slotName)
            {
                // Its session was closed by the session manager when the new one logged in.
                if (_currentSelectedSlot == oldSlot) _currentSelectedSlot = null;
                oldSlot.EndSlot();
                oldSlot.QueueFree();
            }
        }
        if (!profile.SavedStats.ContainsKey(slotName)) profile.SavedStats[slotName] = new SlotStats();
        profile.SavedStats[slotName].GameName = session.ConnectionInfo.Game;
        profile.SavedStats[slotName].SlotNumber = session.ConnectionInfo.Slot;
        using (AP_Atlas.Core.PerfMonitor.Measure("Save multiworlds")) DataManager.SaveProfiles(_profiles);
        // The model takes the session's events from here on (and the messages that arrived before it), and runs its logic.
        using var __model = AP_Atlas.Core.PerfMonitor.Measure($"[{slotName}] Slot set up");
        var model = new AP_Atlas.Core.SlotModel(connected, _appSettings, msg =>
        {
            GD.Print(msg);
            LogToDebug(msg, slotName);
        });
        var slotTracker = new SlotTrackerControl(model, _appSettings,
            ShowStatus,
            (msg) => { LogToDebug(msg, slotName); }
        );
        slotTracker.ResolveOtherSlotLogic = (slot, loc) => ResolveSlotLogic(slotTracker, slot, loc);
        slotTracker.ShowToast = ShowToast;
        slotTracker.ShowActionToast = ShowToast;
        slotTracker.OpenEngineSetup = OpenEngineSetup;
        slotTracker.OpenGamesPageFor = game =>
        {
            ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Games);
            if (!string.IsNullOrEmpty(game)) _gamesPage?.Select(game);
        };
        slotTracker.FindMapPack = FindMapPack;
        slotTracker.NewChatLines += () => MarkTerminalTabNew(0);
        slotTracker.AccuracyChanged += () => _propertiesPanel?.QueueRefresh();
        // When this slot's logic moves, hints at its locations change for the other slots of the same multiworld.
        slotTracker.StateChanged += () =>
        {
            using var __perf = AP_Atlas.Core.PerfMonitor.Measure("Other slots' hints refresh");
            foreach (var sibling in SiblingSlots(slotTracker)) sibling.RefreshHints();
            _propertiesPanel?.QueueRefresh();
        };
        _slots.Add(slotTracker);
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
            using var __perf = AP_Atlas.Core.PerfMonitor.Measure($"[{slotName}] Text client font size");
            if (GodotObject.IsInstanceValid(slotTracker)) SetFontSizeRecursive(slotTracker, _appSettings.ConsoleFontSize);
        };
    }
}
