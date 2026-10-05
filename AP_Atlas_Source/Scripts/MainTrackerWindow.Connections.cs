#nullable disable
using System.Linq;
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;

/// <summary>Connections: connecting slots, careful reconnects, tracking open sessions, and closing them when Atlas closes.</summary>
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
            // Close every session we opened (connected slots and any still connecting) with a proper close frame,
            // so the server drops them immediately instead of waiting for a timeout.
            ArchipelagoSession[] sessions;
            lock (_openSessions) sessions = _openSessions.ToArray();
            var disconnectTasks = sessions.Select(CloseSessionAsync).ToList();
            if (disconnectTasks.Count > 0)
            {
                var all = Task.WhenAll(disconnectTasks);
                await Task.WhenAny(all, Task.Delay(3000));
                if (all.IsCompleted) LogToSystem($"[color=yellow]Closed {disconnectTasks.Count} server connection(s).[/color]");
                else AP_Atlas.Core.Logger.LogWarning("Some server connections did not confirm closing within 3 seconds; the OS closes them as the app exits.");
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
    // Every session this app has opened and not yet closed, including ones still connecting or logging in,
    // so shutdown can close all of them (not just the slots that made it into the UI).
    private readonly HashSet<ArchipelagoSession> _openSessions = new HashSet<ArchipelagoSession>();
    /// <summary>Sessions that finished logging in (only those are reconnected when they drop). Guarded by _openSessions.</summary>
    private readonly HashSet<ArchipelagoSession> _loggedInSessions = new HashSet<ArchipelagoSession>();
    private readonly Dictionary<ArchipelagoSession, (MultiworldProfile Profile, string SlotName)> _sessionOwners = new Dictionary<ArchipelagoSession, (MultiworldProfile, string)>();
    /// <summary>
    /// Catches connections that died without a "socket closed" event (a server that crashed or lost power only
    /// produces a socket error): every half second, a logged-in session that's no longer connected counts as dropped.
    /// </summary>
    private void CheckForDroppedSessions()
    {
        if (_shuttingDown) return;
        List<(MultiworldProfile, string)> dropped = null;
        lock (_openSessions)
        {
            foreach (var session in _loggedInSessions.ToList())
            {
                bool connected;
                try { connected = session.Socket.Connected; } catch { connected = false; }
                if (connected) continue;
                _loggedInSessions.Remove(session);
                _openSessions.Remove(session);
                if (_sessionOwners.Remove(session, out var owner)) (dropped ??= new()).Add(owner);
            }
        }
        if (dropped == null) return;
        foreach (var (profile, slotName) in dropped)
        {
            LogToSystem($"[color=yellow]Connection to {slotName} is gone (no close from the server).[/color]");
            OnSessionDropped(profile, slotName);
        }
    }
    private readonly Dictionary<string, int> _reconnectAttempts = new Dictionary<string, int>();
    private readonly Dictionary<string, string> _lastLoginErrors = new Dictionary<string, string>();
    /// <summary>
    /// Waits between reconnect tries, about 30 minutes in all. Deliberately short and slow: a connection attempt wakes a
    /// sleeping archipelago.gg room, so Atlas must never keep a closed room busy with endless retries.
    /// </summary>
    private static readonly int[] ReconnectDelaysSeconds = { 15, 30, 60, 120, 300, 600 };
    private void OnSessionDropped(MultiworldProfile profile, string slotName)
    {
        if (_shuttingDown) return;
        if (!_appSettings.AutoReconnect)
        {
            ShowToast($"Connection to {slotName} was lost.", Godot.Colors.Orange, "Reconnect", () => OnConnectSlotPressed(slotName, profile));
            return;
        }
        ScheduleReconnect(profile, slotName);
    }
    private void ScheduleReconnect(MultiworldProfile profile, string slotName)
    {
        string key = SlotKey(profile.Id, slotName);
        int attempt = _reconnectAttempts.TryGetValue(key, out var a) ? a : 0;
        if (attempt >= ReconnectDelaysSeconds.Length)
        {
            _reconnectAttempts.Remove(key);
            LogToSystem($"[color=orange]Stopped trying to reconnect {slotName} after {attempt} tries over about 30 minutes.[/color] Reconnect it when the server is back.");
            ShowToast($"{slotName} couldn't reconnect. The server may be down or the room closed.", Godot.Colors.Orange, "Try again", () => OnConnectSlotPressed(slotName, profile));
            return;
        }
        _reconnectAttempts[key] = attempt + 1;
        // ±20% jitter, so many trackers that lost the same server don't all come back in the same second.
        double delay = ReconnectDelaysSeconds[attempt] * (0.8 + System.Random.Shared.NextDouble() * 0.4);
        LogToSystem($"[color=orange]Connection to {slotName} lost.[/color] Reconnecting in {delay:0} s (try {attempt + 1} of {ReconnectDelaysSeconds.Length}).");
        if (attempt == 0) ShowToast($"Connection to {slotName} lost. Reconnecting automatically…", Godot.Colors.Orange);
        GetTree().CreateTimer(delay).Timeout += () => TryReconnect(profile, slotName);
    }
    private bool IsSlotLive(string profileId, string slotName) =>
        ActiveSlotNodes().OfType<SlotTrackerControl>().Any(s => IsInstanceValid(s) && s.ProfileId == profileId && s.SlotName == slotName && s.Session?.Socket?.Connected == true);
    private void TryReconnect(MultiworldProfile profile, string slotName) => AP_Atlas.Core.Async.Fire(TryReconnectAsync(profile, slotName), $"reconnecting {slotName}");
    private async Task TryReconnectAsync(MultiworldProfile profile, string slotName)
    {
        string key = SlotKey(profile.Id, slotName);
        if (_shuttingDown || !_reconnectAttempts.ContainsKey(key)) return; // cancelled: the user connected or disconnected
        if (IsSlotLive(profile.Id, slotName)) { _reconnectAttempts.Remove(key); return; }
        if (_isConnectingSlot || _connectingSlots.Contains(key))
        {
            GetTree().CreateTimer(5).Timeout += () => TryReconnect(profile, slotName);
            return;
        }
        _lastLoginErrors.Remove(key);
        try { await ConnectSlotInternalAsync(slotName, profile); }
        catch (Exception ex) { AP_Atlas.Core.Logger.LogWarning($"Reconnect of {slotName} failed: {ex.Message}"); }
        // The login result is applied on the main thread just after; look once it has been.
        GetTree().CreateTimer(1.0).Timeout += () =>
        {
            if (_shuttingDown || !_reconnectAttempts.ContainsKey(key)) return;
            if (IsSlotLive(profile.Id, slotName))
            {
                _reconnectAttempts.Remove(key);
                LogToSystem($"[color=lime]Reconnected {slotName}.[/color]");
                ShowToast($"Reconnected {slotName}.", Godot.Colors.LimeGreen);
                return;
            }
            if (_lastLoginErrors.TryGetValue(key, out var errors))
            {
                // The server answered and refused (slot gone, wrong password, room changed): retrying won't help.
                _reconnectAttempts.Remove(key);
                LogToSystem($"[color=red]Stopped reconnecting {slotName}: the server refused the login ({errors}).[/color]");
                ShowToast($"{slotName} can't reconnect: {errors}", Godot.Colors.Salmon);
                return;
            }
            ScheduleReconnect(profile, slotName);
        };
    }
    private void TrackSession(ArchipelagoSession session)
    {
        lock (_openSessions) _openSessions.Add(session);
    }
    /// <summary>Closes a session's socket with a normal close frame and forgets it. Safe to call more than once.</summary>
    private Task CloseSessionAsync(ArchipelagoSession session)
    {
        if (session == null) return Task.CompletedTask;
        lock (_openSessions)
        {
            _loggedInSessions.Remove(session);
            _sessionOwners.Remove(session);
            if (!_openSessions.Remove(session)) return Task.CompletedTask;
        }
        try
        {
            return session.Socket.DisconnectAsync() ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Error closing session: {ex.Message}");
            return Task.CompletedTask;
        }
    }
    private void DisconnectSlot(string profileId, string slotName)
    {
        _reconnectAttempts.Remove(SlotKey(profileId, slotName)); // the user chose to disconnect: no automatic reconnect
        if (_terminalStage == null) return;
        foreach (Node n in ActiveSlotNodes())
        {
            if (n is SlotTrackerControl slot && slot.ProfileId == profileId && slot.SlotName == slotName)
            {
                LogToSystem("[color=yellow]Disconnected slot: " + slotName + "[/color]");
                AP_Atlas.Core.Async.Fire(CloseSessionAsync(slot.Session), "closing a server connection", tellUser: false);
            }
        }
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
                _reconnectAttempts.Remove(SlotKey(_selectedProfile.Id, slotName));
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
        _reconnectAttempts.Remove(SlotKey(profile.Id, slotName)); // the user took over
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
        if (_shuttingDown) return false;
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
        ArchipelagoSession session = null;
        try
        {
            session = ArchipelagoSessionFactory.CreateSession(profile.ServerUrl);
            TrackSession(session);
            var earlyMessages = new List<Archipelago.MultiClient.Net.MessageLog.Messages.LogMessage>();
            void earlyHandler(Archipelago.MultiClient.Net.MessageLog.Messages.LogMessage msg)
            {
                lock (earlyMessages) earlyMessages.Add(msg);
            }
            session.MessageLog.OnMessageReceived += earlyHandler;
            // The server's RoomInfo (sent before login) carries each game's data checksum: kept so the slot can
            // skip re-downloading names it already has, and compare its apworld version with the seed's.
            var dataChecksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void roomInfoHandler(Archipelago.MultiClient.Net.ArchipelagoPacketBase packet)
            {
                if (packet is Archipelago.MultiClient.Net.Packets.RoomInfoPacket info && info.DataPackageChecksums != null)
                    lock (dataChecksums) foreach (var kv in info.DataPackageChecksums) dataChecksums[kv.Key] = kv.Value;
            }
            session.Socket.PacketReceived += roomInfoHandler;
            session.Socket.ErrorReceived += (ex, msg) =>
            {
                LogToSystem("[color=red]Socket Error (" + slotName + "):[/color] " + msg);
                if (_globalStatusLabel != null)
                {
                    AP_Atlas.UI.Ui.Defer(_globalStatusLabel, () => _globalStatusLabel.Text = "Socket Error: " + msg);
                }
            };
            session.Socket.SocketClosed += (reason) =>
            {
                LogToSystem("[color=yellow]Socket Closed (" + slotName + "):[/color] " + reason);
                if (_globalStatusLabel != null)
                {
                    AP_Atlas.UI.Ui.Defer(_globalStatusLabel, () => _globalStatusLabel.Text = "Disconnected: " + reason);
                }
                // Atlas forgets a session before closing it on purpose, so one still tracked here dropped by itself.
                bool dropped;
                lock (_openSessions)
                {
                    dropped = _loggedInSessions.Remove(session) && _openSessions.Remove(session);
                    _sessionOwners.Remove(session);
                }
                if (dropped && !_shuttingDown) AP_Atlas.UI.Ui.Defer(this, () => OnSessionDropped(profile, slotName));
            };
            var connectTask = Task.Run(() => session.TryConnectAndLogin(
                "",
                slotName,
                Archipelago.MultiClient.Net.Enums.ItemsHandlingFlags.AllItems,
                new Version(0, 5, 0),
                // "Tracker": the server treats it like TextOnly (can't send checks) and announces Atlas as "tracking".
                new[] { "Tracker" },
                null,
                profile.Password == "" ? null : profile.Password
            ));
            var timeoutTask = Task.Delay(10000);
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            if (completedTask == timeoutTask)
            {
                session.MessageLog.OnMessageReceived -= earlyHandler;
                AP_Atlas.Core.Async.Fire(CloseSessionAsync(session), "closing a server connection", tellUser: false);
                // The login may still finish after we gave up on it; close it then too so it isn't left open on the server.
                AP_Atlas.Core.Async.Fire(connectTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        AP_Atlas.Core.Logger.LogDebug($"A connection to {slotName} that had timed out failed afterwards: {t.Exception?.GetBaseException().Message}");
                        return;
                    }
                    if (t.Status == TaskStatus.RanToCompletion && t.Result.Successful)
                    {
                        TrackSession(session);
                        AP_Atlas.Core.Async.Fire(CloseSessionAsync(session), "closing a server connection", tellUser: false);
                    }
                }, TaskScheduler.Default), "closing a connection that finished after it timed out", tellUser: false);
                AP_Atlas.UI.Ui.Defer(this, () =>
                {
                    _statusLabel.Text = "Status: Connection Timeout";
                    _statusLabel.AddThemeColorOverride("font_color", Colors.Red);
                    if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Timeout (" + slotName + ")";
                    LogToSystem("[color=red]Connection timed out for " + slotName + " after 10 seconds.[/color]");
                    _connectingSlots.Remove(SlotKey(profile.Id, slotName));
                    UpdateSidebar();
                });
                return false;
            }
            var result = await connectTask;
            AP_Atlas.UI.Ui.Defer(this, () =>
            {
                if (_shuttingDown)
                {
                    // The app is closing; don't build a slot around this session, just close it.
                    session.MessageLog.OnMessageReceived -= earlyHandler;
                    AP_Atlas.Core.Async.Fire(CloseSessionAsync(session), "closing a server connection", tellUser: false);
                    return;
                }
                if (result.Successful)
                {
                    lock (_openSessions)
                    {
                        _loggedInSessions.Add(session);
                        _sessionOwners[session] = (profile, slotName);
                    }
                    _lastLoginErrors.Remove(SlotKey(profile.Id, slotName));
                    _statusLabel.Text = "Status: Connected successfully as " + slotName + "!";
                    _statusLabel.AddThemeColorOverride("font_color", Colors.Green);
                    if (_globalStatusLabel != null) _globalStatusLabel.Text = "Booting Engine for " + slotName + "...";
                    LogToSystem("[color=lime]Successfully authenticated as " + slotName + ".[/color]");
                    var slotData = (result as Archipelago.MultiClient.Net.LoginSuccessful)?.SlotData;
                    foreach (Node n in ActiveSlotNodes())
                    {
                        if (n is SlotTrackerControl oldSlot && oldSlot.ProfileId == profile.Id && oldSlot.SlotName == slotName)
                        {
                            if (_currentSelectedSlot == oldSlot) _currentSelectedSlot = null;
                            AP_Atlas.Core.Async.Fire(CloseSessionAsync(oldSlot.Session), "closing a server connection", tellUser: false);
                            oldSlot.QueueFree();
                        }
                    }
                    if (!profile.SavedStats.ContainsKey(slotName)) profile.SavedStats[slotName] = new SlotStats();
                    profile.SavedStats[slotName].GameName = session.ConnectionInfo.Game;
                    profile.SavedStats[slotName].SlotNumber = session.ConnectionInfo.Slot;
                    DataManager.SaveProfiles(_profiles);
                    var slotTracker = new SlotTrackerControl(session, profile.Id, slotName, _appSettings, slotData,
                        (msg) => { if (_globalStatusLabel != null) _globalStatusLabel.Text = msg; },
                        (msg) => { LogToDebug(msg, slotName); }
                    );
                    session.Socket.PacketReceived -= roomInfoHandler;
                    lock (dataChecksums) slotTracker.ServerDataChecksums = new Dictionary<string, string>(dataChecksums, StringComparer.OrdinalIgnoreCase);
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
                    session.MessageLog.OnMessageReceived -= earlyHandler;
                    lock (earlyMessages)
                    {
                        if (earlyMessages.Count > 0) slotTracker.InjectEarlyMessages(earlyMessages);
                    }
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
                else
                {
                    session.MessageLog.OnMessageReceived -= earlyHandler;
                    AP_Atlas.Core.Async.Fire(CloseSessionAsync(session), "closing a server connection", tellUser: false);
                    var loginFailure = (Archipelago.MultiClient.Net.LoginFailure)result;
                    string errs = string.Join(", ", loginFailure.Errors);
                    // Only a refusal (wrong slot, game, version, password) stops automatic reconnects; a server that
                    // couldn't be reached just means try again later.
                    var refusals = (loginFailure.ErrorCodes ?? System.Array.Empty<Archipelago.MultiClient.Net.Enums.ConnectionRefusedError>())
                        .Where(c => c != Archipelago.MultiClient.Net.Enums.ConnectionRefusedError.UnknownError).ToList();
                    if (refusals.Count > 0) _lastLoginErrors[SlotKey(profile.Id, slotName)] = errs;
                    _statusLabel.Text = "Status: Failed to connect:\n" + errs;
                    _statusLabel.AddThemeColorOverride("font_color", Colors.Red);
                    if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Failed (" + slotName + ")";
                    LogToSystem(refusals.Count > 0
                        ? "[color=red]The server refused the login for " + slotName + ":[/color] " + errs
                        : "[color=orange]Couldn't reach the server for " + slotName + ":[/color] " + errs);
                    _connectingSlots.Remove(SlotKey(profile.Id, slotName));
                    UpdateSidebar();
                }
            });
        }
        catch (System.Exception ex)
        {
            AP_Atlas.Core.Async.Fire(CloseSessionAsync(session), "closing a server connection", tellUser: false);
            AP_Atlas.UI.Ui.Defer(this, () =>
            {
                _statusLabel.Text = "Status: Connection Error:\n" + ex.Message;
                _statusLabel.AddThemeColorOverride("font_color", Colors.Red);
                if (_globalStatusLabel != null) _globalStatusLabel.Text = "Connection Error";
                LogToSystem("[color=red]Exception during connection:[/color] " + ex.Message);
                _connectingSlots.Remove(SlotKey(profile.Id, slotName));
                UpdateSidebar();
            });
        }
        return true;
    }
}
