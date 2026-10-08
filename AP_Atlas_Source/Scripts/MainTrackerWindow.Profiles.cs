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
            var watermark = new Label { Text = Tr("Select a multiworld to edit its slots."), HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Godot.Color(0.5f, 0.5f, 0.5f) };
            _slotsListVBox.AddChild(watermark);
            return;
        }
        if (_selectedProfile.Slots.Count == 0)
        {
            var watermark = new Label { Text = Tr("No slots yet: + Add Slot below."), HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Godot.Color(0.5f, 0.5f, 0.5f) };
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
            // The slot's game, when known (its YAML, its saved stats or its connection).
            string game = GameOfSlot(_selectedProfile, slotName);
            if (game != null)
            {
                var gameLabel = AP_Atlas.UI.Kit.Muted(game);
                gameLabel.ClipText = true;
                gameLabel.CustomMinimumSize = new Godot.Vector2(90, 0);
                gameLabel.SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter;
                gameLabel.TooltipText = game;
                row.AddChild(gameLabel);
            }
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
            // A collapsed section with the slot's own settings: its YAML, pack variant, apworld version and map following.
            var details = new VBoxContainer { Visible = false };
            details.SetMeta("slot_details", slotName);
            var detailsToggle = AP_Atlas.UI.Kit.Button(Tr("▸ Details"), Tr("This slot's YAML, map pack variant, apworld version and map following."), () => { }, flat: true, small: true);
            string detailsProfileId = _selectedProfile.Id;
            detailsToggle.Pressed += () =>
            {
                details.Visible = !details.Visible;
                detailsToggle.Text = details.Visible ? Tr("▾ Details") : Tr("▸ Details");
                if (details.Visible) FillSlotDetails(details, detailsProfileId, lineEdit.Text);
            };
            row.AddChild(detailsToggle);
            _slotsListVBox.AddChild(row);
            _slotsListVBox.AddChild(details);
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
    /// <summary>+ Add Slot: the dialog with everything a slot needs (its name, game, YAML, the engine line and Connect now).</summary>
    private void OnAddSlotPressed()
    {
        if (_selectedProfile == null) return;
        var profile = _selectedProfile;
        AP_Atlas.UI.AddSlotDialog.Open(this, new AP_Atlas.UI.AddSlotDialog.Hooks
        {
            Tr = text => Tr(text),
            Games = () => _gamesPage?.GameNames() ?? new List<string>(),
            SlotNameTaken = name => profile.Slots.Contains(name),
            HasServer = !string.IsNullOrWhiteSpace(profile.ServerUrl),
            GameStatus = SlotGameStatus,
            PickYaml = PickYamlFor,
            KeepYaml = KeepYamlForSlots,
            Add = draft => AddSlotFromDraft(profile, draft),
        });
    }

    /// <summary>
    /// A slot from the Add a slot dialog: added to the multiworld (a new multiworld's placeholder slot gives way), its game
    /// noted, its YAML tied to it, the multiworld saved to disk, and connected when asked.
    /// </summary>
    internal void AddSlotFromDraft(MultiworldProfile profile, AP_Atlas.UI.SlotDraft draft)
    {
        if (profile == null || _selectedProfile != profile) return;
        string name = (draft.Name ?? "").Trim();
        if (name.Length == 0 || profile.Slots.Contains(name)) return;
        if (profile.Slots.Count == 1 && IsDefaultSlotName(profile.Slots[0]) && !profile.SavedStats.ContainsKey(profile.Slots[0])) profile.Slots.Clear();
        profile.Slots.Add(name);
        if (!string.IsNullOrWhiteSpace(draft.Game))
        {
            if (!profile.SavedStats.TryGetValue(name, out var stats)) profile.SavedStats[name] = stats = new SlotStats();
            if (string.IsNullOrWhiteSpace(stats.GameName)) stats.GameName = draft.Game;
        }
        if (!string.IsNullOrEmpty(draft.YamlPath))
        {
            _appSettings.SlotYamlPaths[AP_Atlas.Core.Annotations.SlotKey(profile.Id, name)] = draft.YamlPath;
            DataManager.SaveSettings(_appSettings);
        }
        SaveProfileEdits(announce: true);
        PopulateSlotsList();
        RefreshProfileListStyles();
        UpdateSidebar();
        _homePage?.Refresh();
        if (draft.ConnectNow) OnConnectSlotPressed(name, profile);
    }

    /// <summary>
    /// The Add a slot dialog's engine line for a game: the Atlas Engine isn't set up (Set up the Atlas Engine); the game's
    /// apworld is in it (with its version when known); it isn't yet (Install the newest apworld… when a project is known).
    /// </summary>
    private AP_Atlas.UI.GameStatus SlotGameStatus(string game)
    {
        var install = AP_Atlas.Core.EngineSetup.AtlasEngine.Current;
        if (install == null || !install.CanLaunch)
            return new AP_Atlas.UI.GameStatus(Tr("The Atlas Engine isn't set up yet, so no game's logic can run."), Tr("Set up the Atlas Engine"), OpenEngineSetup);
        var games = AP_Atlas.Core.EngineSetup.AtlasEngine.LastCheck(install)?.Games;
        if (games != null && games.Contains(game, StringComparer.OrdinalIgnoreCase))
        {
            string version = AP_Atlas.Core.EngineSetup.ApworldSources.InstalledCopies(install, game).Select(c => AP_Atlas.Core.EngineSetup.ApworldSources.KnownSourceOf(c.Sha256).Tag).FirstOrDefault(t => t != null);
            bool fromFile = AP_Atlas.Core.EngineSetup.ApworldSources.GamesWithApworldFiles(install).Contains(game);
            return new AP_Atlas.UI.GameStatus(version != null ? Tr("Its apworld is in the Atlas Engine ({0}), so its logic is ready.").Replace("{0}", version)
                : fromFile ? Tr("Its apworld is in the Atlas Engine, so its logic is ready.") : Tr("It ships with Archipelago, so the Atlas Engine has it and its logic is ready."));
        }
        bool known = AP_Atlas.Core.EngineSetup.ApworldSources.Find(game) != null || (_appSettings.ExtraApworldRepos?.ContainsKey(game) ?? false);
        if (known && _gamesPage != null)
            return new AP_Atlas.UI.GameStatus(Tr("Its apworld isn't in the Atlas Engine yet, so its logic can't run."), Tr("Install the newest apworld…"), () => _gamesPage.InstallNewest(game, this));
        return new AP_Atlas.UI.GameStatus(Tr("Its apworld isn't in the Atlas Engine yet, and no project is known for it: add one on the Games page."));
    }

    /// <summary>Keeps a YAML in Atlas's YAML folder: the kept copy's path and the players it names; null (with a card saying why) when it can't be kept or read.</summary>
    internal (string Kept, List<(string Name, List<string> Games)> Players)? KeepYamlForSlots(string path)
    {
        var library = AP_Atlas.Core.Games.YamlLibrary.Load(DataManager.GetDataDirectory());
        var (entry, problem) = library.Add(path);
        if (entry == null)
        {
            ShowToast(Tr("That YAML can't be added: {0}.").Replace("{0}", problem ?? ""), AP_Atlas.Core.ThemeColors.Error);
            return null;
        }
        string kept = library.PathOf(entry);
        try { return (kept, AP_Atlas.Core.YamlExclusions.Players(System.IO.File.ReadAllText(kept))); }
        catch (Exception ex)
        {
            ShowToast(Tr("The YAML couldn't be read: {0}").Replace("{0}", ex.Message), AP_Atlas.Core.ThemeColors.Error);
            return null;
        }
    }

    // ---- A slot's own settings, and slots from a YAML ----

    /// <summary>The slot's game, when Atlas knows it: a connected slot's, the saved stats', or its YAML's (the first game it may roll).</summary>
    private string GameOfSlot(MultiworldProfile profile, string slotName)
    {
        if (profile == null) return null;
        var live = LiveSlotOf(profile.Id, slotName);
        if (!string.IsNullOrEmpty(live?.Game)) return live.Game;
        if (profile.SavedStats.TryGetValue(slotName, out var stats) && !string.IsNullOrWhiteSpace(stats.GameName)) return stats.GameName;
        string yaml = SlotYamlPath(profile.Id, slotName);
        if (yaml != null && System.IO.File.Exists(yaml))
        {
            try
            {
                var games = AP_Atlas.Core.YamlExclusions.GamesFor(System.IO.File.ReadAllText(yaml), slotName);
                if (games.Count > 0) return games[0];
            }
            catch (Exception ex) { AP_Atlas.Core.Logger.LogDebug($"Couldn't read the YAML linked to {slotName}: {ex.Message}"); }
        }
        return null;
    }

    private SlotTrackerControl LiveSlotOf(string profileId, string slotName) =>
        ActiveSlotNodes().OfType<SlotTrackerControl>().FirstOrDefault(s => GodotObject.IsInstanceValid(s) && s.ProfileId == profileId && s.SlotName == slotName);

    private string SlotYamlPath(string profileId, string slotName) =>
        _appSettings.SlotYamlPaths.TryGetValue(AP_Atlas.Core.Annotations.SlotKey(profileId, slotName), out var path) && !string.IsNullOrEmpty(path) ? path : null;

    /// <summary>Links (or with null, unlinks) a YAML to a slot of a multiworld, connected or not; a connected slot's logic restarts on it.</summary>
    private void LinkSlotYaml(string profileId, string slotName, string path)
    {
        var live = LiveSlotOf(profileId, slotName);
        if (live != null)
        {
            live.LinkYaml(path);
            return;
        }
        string key = AP_Atlas.Core.Annotations.SlotKey(profileId, slotName);
        if (string.IsNullOrEmpty(path)) _appSettings.SlotYamlPaths.Remove(key);
        else _appSettings.SlotYamlPaths[key] = path;
        DataManager.SaveSettings(_appSettings);
    }

    /// <summary>The details section of a slot's row (for tests), or null while it's collapsed.</summary>
    internal VBoxContainer SlotDetailsOf(string slotName) =>
        _slotsListVBox.GetChildren().OfType<VBoxContainer>().FirstOrDefault(v => v.HasMeta("slot_details") && v.GetMeta("slot_details").AsString() == slotName && v.Visible);

    /// <summary>Opens (or closes) a slot row's details (for tests).</summary>
    internal void ToggleSlotDetails(string slotName)
    {
        var row = _slotsListVBox.GetChildren().OfType<HBoxContainer>().FirstOrDefault(r => r.HasMeta("slot_name") && r.GetMeta("slot_name").AsString() == slotName);
        row?.GetChildren().OfType<Button>().LastOrDefault(b => b.Text.EndsWith("Details"))?.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private void FillSlotDetails(VBoxContainer details, string profileId, string slotName)
    {
        foreach (Node child in details.GetChildren()) child.QueueFree();
        var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;
        var indent = new MarginContainer();
        indent.AddThemeConstantOverride("margin_left", 24);
        indent.AddThemeConstantOverride("margin_bottom", 6);
        details.AddChild(indent);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 6);
        indent.AddChild(grid);
        void Row(string label, Control value)
        {
            var l = AP_Atlas.UI.Kit.Muted(label);
            l.SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter;
            grid.AddChild(l);
            grid.AddChild(value);
        }
        HBoxContainer Line()
        {
            var h = new HBoxContainer { SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill };
            h.AddThemeConstantOverride("separation", 6);
            return h;
        }
        void Refill() => FillSlotDetails(details, profileId, slotName);
        var live = LiveSlotOf(profileId, slotName);
        string game = GameOfSlot(profile, slotName);
        string key = AP_Atlas.Core.Annotations.SlotKey(profileId, slotName);

        // The game.
        Row(Tr("Game"), AP_Atlas.UI.Kit.Text(game ?? Tr("Not known yet (from its YAML, or once it connects)")));

        // The YAML: linked or not, kept in Atlas's folder or elsewhere.
        string yaml = SlotYamlPath(profileId, slotName);
        var yamlLine = Line();
        var yamlLabel = AP_Atlas.UI.Kit.Text(yaml != null ? System.IO.Path.GetFileName(yaml) : Tr("None linked (most games don't need one)"));
        yamlLabel.ClipText = true;
        yamlLabel.SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill;
        yamlLabel.TooltipText = yaml ?? "";
        yamlLine.AddChild(yamlLabel);
        yamlLine.AddChild(AP_Atlas.UI.Kit.Button(yaml != null ? Tr("Change…") : Tr("Link…"), Tr("The player YAML this slot was rolled from; logic rebuilds the world from it when the server's data isn't enough."), () =>
            PickYamlFor(path => { LinkSlotYaml(profileId, slotName, path); Refill(); }), small: true));
        if (yaml != null)
        {
            yamlLine.AddChild(AP_Atlas.UI.Kit.Button(Tr("Unlink"), null, () => { LinkSlotYaml(profileId, slotName, null); Refill(); }, small: true));
            string yamlsFolder = System.IO.Path.GetFullPath(AP_Atlas.Core.Games.GameFiles.YamlsFolder(DataManager.GetDataDirectory()));
            if (!System.IO.Path.GetFullPath(yaml).StartsWith(yamlsFolder, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(yaml))
                yamlLine.AddChild(AP_Atlas.UI.Kit.Button(Tr("Keep in Atlas's YAML folder"), Tr("Copies the file into Atlas's YAML folder and links the copy, so it stays with Atlas."), () =>
                {
                    var library = AP_Atlas.Core.Games.YamlLibrary.Load(DataManager.GetDataDirectory());
                    var (entry, problem) = library.Add(yaml);
                    if (entry == null) ShowToast(Tr("That YAML can't be kept: {0}.").Replace("{0}", problem ?? ""), AP_Atlas.Core.ThemeColors.Error);
                    else LinkSlotYaml(profileId, slotName, library.PathOf(entry));
                    Refill();
                }, small: true));
        }
        Row(Tr("YAML"), yamlLine);

        // The map pack's variant, when its pack has more than one.
        var pack = game != null ? AP_Atlas.Core.PopTracker.PopTrackerPackLoader.LoadPackForGame(game) : null;
        var variants = pack != null ? AP_Atlas.Core.PopTracker.PopTrackerPackLoader.VariantsOf(pack.Manifest) : new List<(string Id, string Name)>();
        if (variants.Count > 1)
        {
            var picker = new OptionButton { SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill, AccessibilityName = Tr("Pack variant") };
            string chosen = _appSettings.PackVariants.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : pack.Variant;
            for (int i = 0; i < variants.Count; i++)
            {
                picker.AddItem(variants[i].Name == variants[i].Id ? variants[i].Id : $"{variants[i].Name} ({variants[i].Id})");
                if (variants[i].Id == chosen) picker.Selected = i;
            }
            picker.ItemSelected += index =>
            {
                string id = variants[(int)index].Id;
                if (live != null) live.SetPackVariant(id);
                else
                {
                    _appSettings.PackVariants[key] = id;
                    DataManager.SaveSettingsSoon(_appSettings);
                }
            };
            Row(Tr("Pack variant"), picker);
        }

        // The apworld version chosen for the slot's seed.
        var apworldLine = Line();
        var choice = _appSettings.SlotApworlds.TryGetValue(key, out var c) ? c : null;
        apworldLine.AddChild(AP_Atlas.UI.Kit.Text(choice != null ? Tr("{0} ({1}) for its seed").Replace("{0}", choice.Version).Replace("{1}", choice.Source)
            : live != null && live.ApworldMatchesSeed == true ? Tr("The installed apworld matches its seed") : Tr("The engine's installed version")));
        if (live != null && (live.ApworldMatchesSeed == false || live.UsingSeedApworld))
            apworldLine.AddChild(AP_Atlas.UI.Kit.Button(Tr("Choose the version…"), Tr("Lists the game's releases; pick the one the seed's host used."), live.OpenApworldPicker, small: true));
        Row(Tr("Apworld"), apworldLine);

        // Following the game's map.
        bool follows = !_appSettings.MapFollowGame.TryGetValue(key, out bool on) || on;
        var follow = new CheckBox { Text = Tr("Follow the game's current map"), ButtonPressed = follows, TooltipText = Tr("With a pack whose scripts can, the map switches to where you are in the game.") };
        follow.Toggled += isOn =>
        {
            if (live != null) live.SetFollowGame(isOn);
            else
            {
                _appSettings.MapFollowGame[key] = isOn;
                DataManager.SaveSettingsSoon(_appSettings);
            }
        };
        Row(Tr("Map"), follow);
        SetFontSizeRecursive(details, _appSettings.SlotsFontSize);
    }

    private void PickYamlFor(Action<string> chosen)
    {
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.yaml, *.yml ; Archipelago player YAML" },
            UseNativeDialog = true,
            Title = Tr("A player YAML")
        };
        if (!string.IsNullOrWhiteSpace(_appSettings.LastYamlFolder) && System.IO.Directory.Exists(_appSettings.LastYamlFolder)) dialog.CurrentDir = _appSettings.LastYamlFolder;
        dialog.FileSelected += path =>
        {
            dialog.QueueFree();
            _appSettings.LastYamlFolder = System.IO.Path.GetDirectoryName(path) ?? "";
            chosen(path);
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    private void OnAddYamlPressed()
    {
        if (_selectedProfile == null) return;
        PickYamlFor(path => AddYamlToProfile(path));
    }

    /// <summary>
    /// Keeps a YAML in Atlas's YAML folder and adds a slot to the selected multiworld for each player it names, the YAML
    /// tied to each (a name with placeholders, "{player}" or "{number}", asks for the slot's room name). The page is
    /// marked dirty: Save writes the slots, Don't save drops them (the YAML stays kept). Also used by tests.
    /// </summary>
    internal void AddYamlToProfile(string path)
    {
        var profile = _selectedProfile;
        if (profile == null) return;
        var keptYaml = KeepYamlForSlots(path);
        if (keptYaml == null) return;
        var (kept, players) = keptYaml.Value;
        string file = System.IO.Path.GetFileName(kept);
        if (players.Count == 0)
        {
            ShowToast(Tr("{0} names no player with a game, so no slot was added (it's kept in Atlas's YAML folder).").Replace("{0}", file), AP_Atlas.Core.ThemeColors.Warning);
            return;
        }
        var added = new List<string>();
        void Next(int index)
        {
            if (index >= players.Count)
            {
                if (added.Count > 0)
                {
                    MarkDirty();
                    PopulateSlotsList();
                    RefreshProfileListStyles();
                    ShowToast(Tr("Added {0} from {1}; Save Settings keeps the slots.").Replace("{0}", string.Join(", ", added)).Replace("{1}", file), AP_Atlas.Core.ThemeColors.TextSubtle);
                }
                return;
            }
            var (name, games) = players[index];
            void Take(string slotName)
            {
                slotName = (slotName ?? "").Trim();
                if (slotName.Length == 0 || _selectedProfile != profile) { Next(index + 1); return; }
                if (!profile.Slots.Contains(slotName))
                {
                    // A new multiworld's placeholder slot gives way to the YAML's first player.
                    if (profile.Slots.Count == 1 && IsDefaultSlotName(profile.Slots[0]) && !profile.SavedStats.ContainsKey(profile.Slots[0])) profile.Slots.Clear();
                    profile.Slots.Add(slotName);
                    added.Add(slotName);
                }
                if (games.Count == 1)
                {
                    if (!profile.SavedStats.TryGetValue(slotName, out var stats)) profile.SavedStats[slotName] = stats = new SlotStats();
                    if (string.IsNullOrWhiteSpace(stats.GameName)) stats.GameName = games[0];
                }
                _appSettings.SlotYamlPaths[AP_Atlas.Core.Annotations.SlotKey(profile.Id, slotName)] = kept;
                DataManager.SaveSettings(_appSettings);
                Next(index + 1);
            }
            if (string.IsNullOrWhiteSpace(name) || name.Contains('{'))
                AP_Atlas.UI.Dialogs.Prompt(this, Tr("Slot name"),
                    string.Format(Tr("{0} names its player \"{1}\", a pattern Archipelago fills in when it rolls. What is this slot called in the room?"), file, string.IsNullOrWhiteSpace(name) ? "?" : name),
                    "", "Player1", Tr("Add slot"), Take);
            else Take(name);
        }
        Next(0);
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
