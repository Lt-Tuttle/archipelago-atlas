#nullable disable
using System.Linq;
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;

/// <summary>The multiworld profile editor on the Multiworlds page.</summary>
public partial class MainTrackerWindow : Control, AP_Atlas.UI.IPropertiesHost
{
    private LineEdit _roomLinkInput;
    private Button _fillFromRoomButton;
    /// <summary>The selected multiworld has edits not yet written to disk.</summary>
    private bool _dirty;
    /// <summary>The selected multiworld as it was when selected or last saved: what Don't save goes back to.</summary>
    private ProfileEdits _editBaseline;

    /// <summary>Whether the selected multiworld has edits that would be lost: typed into it, or into the link boxes.</summary>
    private bool HasUnsavedEdits => _dirty && _selectedProfile != null;

    private sealed record ProfileEdits(string Name, string RoomLink, string ServerUrl, string Password, List<string> Slots, Dictionary<string, SlotStats> Stats);

    private static ProfileEdits SnapshotEdits(MultiworldProfile profile) =>
        new(profile.Name, profile.RoomLink ?? "", profile.ServerUrl ?? "", profile.Password ?? "", profile.Slots.ToList(), new Dictionary<string, SlotStats>(profile.SavedStats));

    /// <summary>Don't save: the selected multiworld goes back to how it was when selected or last saved, in memory and on the page.</summary>
    private void DiscardProfileEdits()
    {
        if (_selectedProfile == null || _editBaseline == null) return;
        var profile = _selectedProfile;
        var baseline = _editBaseline;
        profile.Name = baseline.Name;
        profile.RoomLink = baseline.RoomLink;
        profile.ServerUrl = baseline.ServerUrl;
        profile.Password = baseline.Password;
        profile.Slots.Clear();
        profile.Slots.AddRange(baseline.Slots);
        // A renamed slot's stats go back under its old name; stats a connection added meanwhile stay.
        foreach (var (name, stats) in baseline.Stats)
            if (!profile.SavedStats.ContainsKey(name)) profile.SavedStats[name] = stats;
        foreach (string name in profile.SavedStats.Keys.ToList())
            if (!baseline.Stats.ContainsKey(name) && baseline.Stats.ContainsValue(profile.SavedStats[name])) profile.SavedStats.Remove(name);
        _dirty = false;
        _saveButton.RemoveThemeColorOverride("font_color");
        FillEditor(profile);
        RefreshProfileList();
    }

    /// <summary>Save: every edit to disk, and the link boxes checked and linked.</summary>
    private void CommitProfileEdits()
    {
        if (_selectedProfile == null) return;
        SaveProfileEdits(announce: true);
        string cheeseLink = _cheeseInput.Text.Trim();
        if (cheeseLink != (_selectedProfile.CheeseTrackerUrl ?? "")) LinkCheeseFromEditor(_selectedProfile, cheeseLink);
        string sphereLink = _sphereInput.Text.Trim();
        if (sphereLink != (_selectedProfile.SphereTrackerUrl ?? "")) LinkSphereFromEditor(_selectedProfile, sphereLink);
    }

    /// <summary>Runs <paramref name="proceed"/> now, or after Save / Don't save when the selected multiworld has unsaved edits (Cancel runs nothing).</summary>
    private void GuardUnsaved(Action proceed)
    {
        if (!HasUnsavedEdits)
        {
            proceed();
            return;
        }
        AP_Atlas.UI.Dialogs.SaveChanges(this, _selectedProfile.Name, CommitProfileEdits, DiscardProfileEdits, proceed, text => Tr(text));
    }

    private void LinkSphereFromEditor(MultiworldProfile profile, string text) => AP_Atlas.Core.Async.Fire(LinkSphereFromEditorAsync(profile, text), "linking the sphere tracker");

    /// <summary>The Sphere Tracker box, on Save: emptied, the room is unlinked; changed, the room is checked and linked (the host's room only); a refused link stays in the box with the reason below.</summary>
    private async Task LinkSphereFromEditorAsync(MultiworldProfile profile, string text)
    {
        if (_spheres == null) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            _spheres.UnlinkSphereSite(profile.Id);
            ShowToast(Tr("{0} is no longer linked to a sphere tracker").Replace("{0}", profile.Name), AP_Atlas.Core.ThemeColors.TextSubtle);
            return;
        }
        string error = await AP_Atlas.UI.SphereLinkFlow.CheckAndLinkAsync(this, _spheres, profile, text, ShowToast, () =>
        {
            if (_selectedProfile == profile && _sphereInput != null) _sphereInput.Text = profile.SphereTrackerUrl ?? "";
            UpdateSidebar();
        });
        if (!IsInstanceValid(this) || error == null || _selectedProfile != profile) return;
        _statusLabel.Text = Tr("Sphere Tracker: {0}").Replace("{0}", error);
        _statusLabel.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Error);
        MarkDirty();
    }

    /// <summary>Whether the password box shows its text; hidden again whenever another multiworld is selected.</summary>
    private void SetPasswordShown(bool shown)
    {
        if (_passwordInput == null || _passwordToggle == null) return;
        _passwordInput.Secret = !shown;
        _passwordToggle.Icon = AP_Atlas.UI.LucideTextures.Get(shown ? "eye-off" : "eye", AP_Atlas.Core.ThemeColors.TextMuted, 1.0f);
        _passwordToggle.TooltipText = shown ? Tr("Hide the password") : Tr("Show the password");
        _passwordToggle.AccessibilityName = _passwordToggle.TooltipText;
    }
    /// <summary>When each multiworld's room status was last read after a failed reconnect (the steady clock), so it's read at most every ten minutes.</summary>
    private readonly Dictionary<string, DateTime> _roomChecks = new();
    internal static readonly TimeSpan RoomCheckSpacing = TimeSpan.FromMinutes(10);

    private void MarkDirty()
    {
        _dirty = true;
        _saveButton.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Pending); // unsaved changes
    }

    /// <summary>An edit of the selected multiworld, as typed: into the multiworld at once, written to disk on Save or when another is selected.</summary>
    private void EditSelected(Action<MultiworldProfile> edit)
    {
        if (_selectedProfile == null) return;
        edit(_selectedProfile);
        MarkDirty();
    }

    /// <summary>Writes the selected multiworld's edits to disk (a waiting reconnect takes the new address and password).</summary>
    private void SaveProfileEdits(bool announce)
    {
        if (_selectedProfile == null) return;
        _sessions?.UpdateLogins(_selectedProfile.Id, _selectedProfile.ServerUrl, string.IsNullOrEmpty(_selectedProfile.Password) ? null : _selectedProfile.Password);
        DataManager.SaveProfiles(_profiles);
        _dirty = false;
        _editBaseline = SnapshotEdits(_selectedProfile);
        _saveButton.RemoveThemeColorOverride("font_color");
        RefreshProfileList();
        if (announce) LogToSystem($"Profile '{_selectedProfile.Name}' saved.", "green");
    }

    /// <summary>A slot's new name, once typing is done (Enter, or leaving the field): the saved stats follow it; a duplicate or an empty name is refused.</summary>
    private void CommitSlotRename(int index, HBoxContainer row, LineEdit lineEdit)
    {
        if (_selectedProfile == null || index >= _selectedProfile.Slots.Count) return;
        string old = _selectedProfile.Slots[index];
        string text = lineEdit.Text.Trim();
        if (text.Length == 0 || text == old)
        {
            lineEdit.Text = old;
            return;
        }
        if (_selectedProfile.Slots.Contains(text))
        {
            lineEdit.Text = old;
            ShowToast(Tr("This multiworld already has a slot named {0}.").Replace("{0}", text), AP_Atlas.Core.ThemeColors.Warning);
            return;
        }
        _selectedProfile.Slots[index] = text;
        if (_selectedProfile.SavedStats.Remove(old, out var stats) && !_selectedProfile.SavedStats.ContainsKey(text)) _selectedProfile.SavedStats[text] = stats;
        row.SetMeta("slot_name", text);
        MarkDirty();
        RefreshProfileListStyles();
    }

    private static bool IsDefaultSlotName(string name) => name is "Player1" or "New Slot";

    private void FillFromRoomLink()
    {
        // A typed server address would be replaced (slots the user named stay): ask first.
        var profile = _selectedProfile;
        bool replaces = profile != null && !string.IsNullOrWhiteSpace(profile.ServerUrl);
        if (!replaces)
        {
            AP_Atlas.Core.Async.Fire(FillFromRoomLinkAsync(), "filling in a multiworld from its room link");
            return;
        }
        AP_Atlas.UI.Dialogs.Confirm(this, Tr("Fill in from the room?"),
            Tr("The room's status page gives the server address and the players. The address typed here, {0}, will be replaced; slots you named stay, and the room's players are added beside them.").Replace("{0}", profile.ServerUrl),
            Tr("Fill in"), () => AP_Atlas.Core.Async.Fire(FillFromRoomLinkAsync(), "filling in a multiworld from its room link"));
    }

    /// <summary>One read of the room's status page (with permission): the server address from its port, the slots from its players.</summary>
    private async Task FillFromRoomLinkAsync()
    {
        var profile = _selectedProfile;
        if (profile == null) return;
        var room = AP_Atlas.Core.Rooms.RoomLinks.Parse(_roomLinkInput.Text, out string error);
        if (room == null)
        {
            ShowToast(error, AP_Atlas.Core.ThemeColors.Error);
            return;
        }
        string link = $"{room.Value.Site}/room/{room.Value.Id}";
        if (!await AskRoomStatusAsync(link)) return;
        var (status, readError) = await AP_Atlas.Core.Rooms.RoomLinks.ReadAsync(room.Value.Site, room.Value.Id);
        if (!IsInstanceValid(this) || _selectedProfile != profile) return;
        if (status == null)
        {
            ShowToast(Tr("The room's status couldn't be read: {0}").Replace("{0}", readError ?? ""), AP_Atlas.Core.ThemeColors.Error);
            return;
        }
        profile.RoomLink = link;
        _roomLinkInput.Text = link;
        if (status.Port > 0)
        {
            profile.ServerUrl = status.ServerAddress(room.Value.Site);
            _serverInput.Text = profile.ServerUrl;
        }
        if (status.Players.Count > 0)
        {
            // Atlas's own placeholder slots make way for the room's players; slots the user named stay (their saved stats with them).
            if (profile.Slots.All(IsDefaultSlotName)) profile.Slots.Clear();
            foreach (string name in status.Players)
                if (!profile.Slots.Contains(name)) profile.Slots.Add(name);
            PopulateSlotsList();
        }
        MarkDirty();
        RefreshProfileListStyles();
        ShowToast(Tr("Filled in from the room: {0}, {1} slots").Replace("{0}", profile.ServerUrl).Replace("{1}", status.Players.Count.ToString()), AP_Atlas.Core.ThemeColors.Info);
    }

    /// <summary>Asks to read a room's status (Allow once / Always allow / Don't allow), unless already allowed.</summary>
    private Task<bool> AskRoomStatusAsync(string link)
    {
        var decided = new TaskCompletionSource<bool>();
        AP_Atlas.UI.PermissionDialog.Ask(this, _appSettings, AP_Atlas.Core.Permissions.RoomStatusReads, null, link, allowed => decided.TrySetResult(allowed));
        return decided.Task;
    }

    /// <summary>After a reconnect gave up: one status read (at most every ten minutes per multiworld) says whether the room moved to another port or is asleep.</summary>
    private void CheckRoomAfterFailure(MultiworldProfile profile, AP_Atlas.Core.Connections.SlotId slot)
    {
        if (string.IsNullOrEmpty(profile.RoomLink)) return;
        if (_roomChecks.TryGetValue(profile.Id, out var last) && AP_Atlas.Core.SteadyClock.UtcNow - last < RoomCheckSpacing) return;
        _roomChecks[profile.Id] = AP_Atlas.Core.SteadyClock.UtcNow;
        AP_Atlas.Core.Async.Fire(CheckRoomAfterFailureAsync(profile, slot), "checking a room's status after a failed reconnect", tellUser: false);
    }

    private async Task CheckRoomAfterFailureAsync(MultiworldProfile profile, AP_Atlas.Core.Connections.SlotId slot)
    {
        var room = AP_Atlas.Core.Rooms.RoomLinks.Parse(profile.RoomLink, out _);
        if (room == null) return;
        if (!await AskRoomStatusAsync(profile.RoomLink)) return;
        var (status, _) = await AP_Atlas.Core.Rooms.RoomLinks.ReadAsync(room.Value.Site, room.Value.Id);
        if (!IsInstanceValid(this) || _shuttingDown || status == null) return;
        int savedPort = AP_Atlas.Core.Rooms.RoomStatus.PortOf(profile.ServerUrl);
        if (status.Port > 0 && status.Port != savedPort)
        {
            string address = status.ServerAddress(room.Value.Site);
            string port = status.Port.ToString();
            ShowToast(Tr("{0}'s room moved to port {1}.").Replace("{0}", profile.Name).Replace("{1}", port), AP_Atlas.Core.ThemeColors.Warning, Tr("Use port {0}").Replace("{0}", port), () =>
            {
                profile.ServerUrl = address;
                if (_selectedProfile == profile) _serverInput.Text = address;
                _sessions?.UpdateLogins(profile.Id, address, string.IsNullOrEmpty(profile.Password) ? null : profile.Password);
                DataManager.SaveProfiles(_profiles);
                OnConnectSlotPressed(slot.SlotName, profile);
            });
        }
        // The room's last activity is the server's own clock, so it's measured against the wall clock, not the steady one.
        else if (status.IsAsleep(AP_Atlas.Core.Deadline.WallClock()))
        {
            ShowToast(Tr("{0}'s room is asleep. Open its page to wake it, then connect again.").Replace("{0}", profile.Name), AP_Atlas.Core.ThemeColors.Warning,
                Tr("Open the room page"), () => AP_Atlas.Core.ExternalLinks.OpenWeb(profile.RoomLink));
        }
    }
    private void RefreshProfileList()
    {
        foreach (Node child in _profileListContainer.GetChildren())
        {
            child.QueueFree();
        }
        foreach (var profile in _profiles)
        {
            var btn = new Button { Text = profile.Name };
            btn.SetMeta("profile_id", profile.Id);
            btn.Pressed += () => SelectProfileGuarded(profile, () => AP_Atlas.Core.Inspector.Inspect(AP_Atlas.Core.InspectTarget.ForProfile(profile.Id)));
            _profileListContainer.AddChild(btn);
        }
        SetFontSizeRecursive(_profileListContainer, _appSettings.ExplorerFontSize);
        RefreshProfileListStyles();
    }
    private void RefreshProfileListStyles()
    {
        var accentColor = AP_Atlas.Core.ThemeColors.Accent;
        var textOnAccent = AP_Atlas.Core.ThemeColors.TextOnAccent;
        string[] fontColors = { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" };
        foreach (Node child in _profileListContainer.GetChildren())
        {
            if (child is Button btn && btn.HasMeta("profile_id"))
            {
                string id = btn.GetMeta("profile_id").AsString();
                if (_selectedProfile != null && _selectedProfile.Id == id)
                {
                    var style = new Godot.StyleBoxFlat
                    {
                        BgColor = accentColor,
                        CornerRadiusTopLeft = 4,
                        CornerRadiusTopRight = 4,
                        CornerRadiusBottomLeft = 4,
                        CornerRadiusBottomRight = 4,
                        ContentMarginLeft = 10,
                        ContentMarginRight = 10,
                        ContentMarginTop = 5,
                        ContentMarginBottom = 5
                    };
                    btn.AddThemeStyleboxOverride("normal", style);
                    btn.AddThemeStyleboxOverride("hover", style);
                    btn.AddThemeStyleboxOverride("pressed", style);
                    btn.AddThemeStyleboxOverride("focus", style);
                    foreach (var c in fontColors) btn.AddThemeColorOverride(c, textOnAccent);
                }
                else
                {
                    foreach (var c in fontColors) btn.RemoveThemeColorOverride(c);
                    btn.RemoveThemeStyleboxOverride("normal");
                    btn.RemoveThemeStyleboxOverride("hover");
                    btn.RemoveThemeStyleboxOverride("pressed");
                    btn.RemoveThemeStyleboxOverride("focus");
                }
            }
        }
    }
    /// <summary>
    /// Shows a multiworld in the editor. Unsaved edits of the one selected until now must have been saved or discarded
    /// first (<see cref="GuardUnsaved"/> asks); they're never written silently.
    /// </summary>
    private void SelectProfile(MultiworldProfile profile)
    {
        _selectedProfile = profile;
        _dirty = false;
        _saveButton.RemoveThemeColorOverride("font_color");
        FillEditor(profile);
        _editBaseline = profile == null ? null : SnapshotEdits(profile);
    }

    /// <summary>Selects a multiworld from the list: unsaved edits of the current one ask first; <paramref name="then"/> runs once it's selected (or at once when it already is).</summary>
    private void SelectProfileGuarded(MultiworldProfile profile, Action then = null)
    {
        if (_selectedProfile == profile)
        {
            then?.Invoke();
            return;
        }
        GuardUnsaved(() =>
        {
            SelectProfile(profile);
            then?.Invoke();
        });
    }

    private void FillEditor(MultiworldProfile profile)
    {
        SetPasswordShown(false);
        if (profile == null)
        {
            _nameInput.Text = "";
            _roomLinkInput.Text = string.Empty;
            _serverInput.Text = "";
            _passwordInput.Text = "";
            _cheeseInput.Text = "";
            _sphereInput.Text = "";
            _nameInput.Editable = false;
            _roomLinkInput.Editable = false;
            _fillFromRoomButton.Disabled = true;
            _serverInput.Editable = false;
            _passwordInput.Editable = false;
            _passwordToggle.Disabled = true;
            _cheeseInput.Editable = false;
            _sphereInput.Editable = false;
            _saveButton.Disabled = true;
            _deleteButton.Disabled = true;
            _addSlotButton.Disabled = true;
            PopulateSlotsList(); // the rows of the multiworld selected until now go
            return;
        }
        _nameInput.Text = profile.Name;
        _roomLinkInput.Text = profile.RoomLink ?? "";
        _serverInput.Text = profile.ServerUrl ?? "";
        _passwordInput.Text = profile.Password ?? "";
        _cheeseInput.Text = profile.CheeseTrackerUrl ?? "";
        _sphereInput.Text = profile.SphereTrackerUrl ?? "";
        _nameInput.Editable = true;
        _roomLinkInput.Editable = true;
        _fillFromRoomButton.Disabled = false;
        _serverInput.Editable = true;
        _passwordInput.Editable = true;
        _passwordToggle.Disabled = false;
        _cheeseInput.Editable = true;
        _sphereInput.Editable = true;
        _saveButton.Disabled = false;
        _deleteButton.Disabled = false;
        _addSlotButton.Disabled = false;
        PopulateSlotsList();
        RefreshProfileListStyles();
    }
    private void PopulateSlotsList()
    {
        foreach (Node child in _slotsListVBox.GetChildren())
        {
            child.QueueFree();
        }
        // The Connect All button goes with the list; one is made again below only for a multiworld of several slots
        // (the connecting overlay would otherwise reach for the freed one).
        _connectAllBtn = null;
        if (_selectedProfile == null)
        {
            var watermark = new Label { Text = "Select a profile to edit slots.", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Godot.Color(0.5f, 0.5f, 0.5f) };
            _slotsListVBox.AddChild(watermark);
            return;
        }
        if (_selectedProfile.Slots.Count == 0)
        {
            var watermark = new Label { Text = "No slots configured. Click 'Add Slot' below.", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Godot.Color(0.5f, 0.5f, 0.5f) };
            _slotsListVBox.AddChild(watermark);
        }
        for (int i = 0; i < _selectedProfile.Slots.Count; i++)
        {
            int index = i;
            string slotName = _selectedProfile.Slots[i];
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.SetMeta("slot_name", slotName);
            row.AddThemeConstantOverride("separation", 5);
            var lineEdit = new LineEdit
            {
                Text = slotName,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                PlaceholderText = "Slot Name (e.g. Player1)"
            };
            // A rename takes effect once typing is done (Enter, or leaving the field), not on every keystroke.
            lineEdit.TextSubmitted += _ => CommitSlotRename(index, row, lineEdit);
            lineEdit.FocusExited += () => CommitSlotRename(index, row, lineEdit);
            // Clicking into a slot row shows that slot's details (saved stats, connection) in Properties.
            string rowProfileId = _selectedProfile.Id;
            lineEdit.FocusEntered += () =>
                AP_Atlas.Core.Inspector.Inspect(AP_Atlas.Core.InspectTarget.ForProfile(rowProfileId, lineEdit.Text));
            row.AddChild(lineEdit);
            var connectBtn = new Button
            {
                Name = "ConnectBtn",
                Icon = _iconConnect,
                Text = "",
                CustomMinimumSize = new Godot.Vector2(36, 36),
                SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter,
                SizeFlagsHorizontal = Godot.Control.SizeFlags.ShrinkCenter,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                ClipText = true
            };
            connectBtn.SetMeta("is_icon_button", true);
            connectBtn.SetMeta("slot_name", slotName);
            connectBtn.TooltipText = Tr("Connect");
            connectBtn.AccessibilityName = connectBtn.TooltipText;
            connectBtn.AddThemeColorOverride("icon_disabled_color", AP_Atlas.Core.ThemeColors.Text);
            connectBtn.Modulate = AP_Atlas.Core.ThemeColors.Success;
            connectBtn.Pressed += () =>
            {
                if (connectBtn.Icon == _iconConnect) OnConnectSlotPressed(lineEdit.Text, _selectedProfile);
            };
            row.AddChild(connectBtn);
            var disconnectBtn = new Button
            {
                Name = "DisconnectBtn",
                Icon = _iconDisconnect,
                Text = "",
                CustomMinimumSize = new Godot.Vector2(36, 36),
                SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter,
                SizeFlagsHorizontal = Godot.Control.SizeFlags.ShrinkCenter,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                Disabled = true,
                TooltipText = Tr("Disconnect")
            };
            disconnectBtn.AccessibilityName = disconnectBtn.TooltipText;
            disconnectBtn.SetMeta("is_icon_button", true);
            disconnectBtn.AddThemeColorOverride("icon_disabled_color", AP_Atlas.Core.ThemeColors.Disabled);
            disconnectBtn.Modulate = AP_Atlas.Core.ThemeColors.Disabled;
            disconnectBtn.Pressed += () =>
            {
                DisconnectSlot(_selectedProfile.Id, lineEdit.Text);
            };
            row.AddChild(disconnectBtn);
            var delBtn = new Button
            {
                Icon = _iconDelete,
                Text = "",
                CustomMinimumSize = new Godot.Vector2(36, 36),
                SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter,
                SizeFlagsHorizontal = Godot.Control.SizeFlags.ShrinkCenter,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                TooltipText = Tr("Delete Slot")
            };
            delBtn.AccessibilityName = delBtn.TooltipText;
            delBtn.SetMeta("is_icon_button", true);
            delBtn.Modulate = AP_Atlas.Core.ThemeColors.Danger;
            delBtn.Pressed += () =>
            {
                var profile = _selectedProfile;
                string slotName = lineEdit.Text;
                AP_Atlas.UI.Dialogs.Confirm(this, Tr("Delete slot"),
                    string.Format(Tr("Remove the slot {0} from {1}? If it's connected, its connection closes."), slotName, profile.Name), Tr("Delete"), () =>
                    {
                        if (_selectedProfile != profile || index >= profile.Slots.Count) return;
                        DisconnectSlot(profile.Id, slotName);
                        profile.ActiveSlots.Remove(slotName);
                        profile.Slots.RemoveAt(index);
                        MarkDirty();
                        PopulateSlotsList();
                        RefreshProfileListStyles();
                        UpdateSidebar();
                    });
            };
            row.AddChild(delBtn);
            _slotsListVBox.AddChild(row);
        }
        if (_selectedProfile.Slots.Count > 1)
        {
            var btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            _connectAllBtn = new Button { Text = "Connect All Slots", CustomMinimumSize = new Godot.Vector2(200, 40) };
            _connectAllBtn.AddThemeColorOverride("font_color", AP_Atlas.Core.ThemeColors.Success);
            _connectAllBtn.Pressed += OnConnectAllPressed;
            btnRow.AddChild(_connectAllBtn);
            _slotsListVBox.AddChild(btnRow);
        }
        SetFontSizeRecursive(_slotsListVBox, _appSettings.SlotsFontSize);
        UpdateSlotStatuses(); // Force immediate update of lights
    }
    /// <summary>A slot card's Cheese Tracker badge: the slot's status there, or a suggestion from Atlas's logic that's ready.</summary>
    private void UpdateCheeseBadge(Label badge, string profileId, string slotName)
    {
        if (badge == null) return;
        var info = _cheese?.Badge(profileId, slotName);
        badge.Visible = info != null;
        if (info == null) return;
        badge.Text = info.Value.Text;
        badge.TooltipText = info.Value.Tooltip;
        badge.AddThemeColorOverride("font_color", info.Value.Color);
    }
    private void OnAddSlotPressed()
    {
        if (_selectedProfile == null) return;
        _selectedProfile.Slots.Add("New Slot");
        MarkDirty();
        PopulateSlotsList();
        RefreshProfileListStyles();
    }
    /// <summary>
    /// A new multiworld, shown where it can be filled in: the Multiworlds page with it selected and its name ready to
    /// type. A new one that was never touched is selected again instead of adding another beside it.
    /// </summary>
    private void OnAddProfilePressed() => GuardUnsaved(AddProfile);

    private void AddProfile()
    {
        var untouched = _profiles.FirstOrDefault(IsUntouchedNew);
        var profile = untouched;
        if (profile == null)
        {
            profile = new MultiworldProfile();
            profile.Slots.Add("Player1");
            _profiles.Add(profile);
            DataManager.SaveProfiles(_profiles);
            RefreshProfileList();
        }
        ((AP_Atlas.UI.IPropertiesHost)this).ShowTool(AP_Atlas.UI.Tool.Connections);
        SelectProfile(profile);
        if (untouched != null) ShowToast(Tr("Give your new multiworld a name first."), AP_Atlas.Core.ThemeColors.Info);
        _nameInput.GrabFocus();
        _nameInput.SelectAll();
        UpdateSidebar();
        _homePage?.Refresh();
    }

    /// <summary>A multiworld as Add made it, with nothing filled in yet: the default name, one slot "Player1", every box empty, no stats.</summary>
    private static bool IsUntouchedNew(MultiworldProfile profile) =>
        profile.Name == new MultiworldProfile().Name
        && profile.Slots.Count == 1 && profile.Slots[0] == "Player1"
        && (string.IsNullOrEmpty(profile.ServerUrl) || profile.ServerUrl == new MultiworldProfile().ServerUrl)
        && string.IsNullOrEmpty(profile.RoomLink) && string.IsNullOrEmpty(profile.Password)
        && string.IsNullOrEmpty(profile.CheeseTrackerUrl) && string.IsNullOrEmpty(profile.SphereTrackerUrl)
        && profile.SavedStats.Count == 0;
    private void OnSaveProfilePressed() => CommitProfileEdits();
    private void LinkCheeseFromEditor(MultiworldProfile profile, string text) => AP_Atlas.Core.Async.Fire(LinkCheeseFromEditorAsync(profile, text), "linking Cheese Tracker");
    private async Task LinkCheeseFromEditorAsync(MultiworldProfile profile, string text)
    {
        string error = await _cheese.LinkAsync(profile.Id, text);
        if (!IsInstanceValid(this)) return;
        if (error != null)
        {
            ShowToast("Cheese Tracker: " + error, AP_Atlas.Core.ThemeColors.Error);
            LogToSystem("Cheese Tracker: " + error, "salmon");
        }
        else ShowToast(string.IsNullOrWhiteSpace(text) ? $"{profile.Name} is no longer linked to Cheese Tracker" : $"{profile.Name} is linked to Cheese Tracker", AP_Atlas.Core.ThemeColors.TextSubtle);
        if (_selectedProfile == profile && _cheeseInput != null) _cheeseInput.Text = profile.CheeseTrackerUrl ?? "";
    }
    private void OnDeleteProfilePressed()
    {
        if (_selectedProfile != null)
        {
            var profile = _selectedProfile;
            AP_Atlas.UI.Dialogs.Confirm(this, Tr("Delete multiworld"),
                string.Format(Tr("Delete the multiworld {0} and its {1} slot(s)? Its connections close, and its Cheese Tracker and sphere room links are forgotten."), profile.Name, profile.Slots.Count),
                Tr("Delete"), () =>
                {
                    DeleteProfile(profile);
                    ShowToast(Tr("Multiworld deleted"), AP_Atlas.Core.ThemeColors.Warning);
                });
        }
    }
    /// <summary>
    /// Deletes a multiworld: its connections close (and no automatic reconnect brings one back), its Cheese Tracker link
    /// and sphere room are forgotten, its slots' views go, and so do its logic engine pools.
    /// </summary>
    private void DeleteProfile(MultiworldProfile profile)
    {
        var profileId = profile.Id;
        if (_sessions != null) AP_Atlas.Core.Async.Fire(_sessions.ForgetProfileAsync(profileId), "closing a deleted multiworld's connections", tellUser: false);
        _cheese?.Unlink(profileId);
        _spheres?.ForgetProfile(profileId);
        _profiles.Remove(profile);
        DataManager.SaveProfiles(_profiles);
        foreach (Node n in ActiveSlotNodes().ToList())
        {
            if (n is SlotTrackerControl slot && slot.ProfileId == profileId)
            {
                if (_currentSelectedSlot == slot) _currentSelectedSlot = null;
                slot.EndSlot();
                slot.QueueFree();
            }
        }
        EnginePools.Forget(profileId);
        RefreshProfileList();
        if (_selectedProfile == profile) SelectProfile(null);
        UpdateSidebar();
        // A slot tool that showed one of its slots now asks for a slot instead of going blank.
        RefreshContextViews();
    }
}
