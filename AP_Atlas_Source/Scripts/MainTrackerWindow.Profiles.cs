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
    private void MarkDirty()
    {
        _saveButton.Modulate = AP_Atlas.Core.ThemeColors.Pending;
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
            btn.Pressed += () =>
            {
                SelectProfile(profile);
                AP_Atlas.Core.Inspector.Inspect(AP_Atlas.Core.InspectTarget.ForProfile(profile.Id));
            };
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
    private void SelectProfile(MultiworldProfile profile)
    {
        _selectedProfile = profile;
        _saveButton.Modulate = AP_Atlas.Core.ThemeColors.Text;
        if (profile == null)
        {
            _nameInput.Text = "";
            _serverInput.Text = "";
            _passwordInput.Text = "";
            _cheeseInput.Text = "";
            _nameInput.Editable = false;
            _serverInput.Editable = false;
            _passwordInput.Editable = false;
            _cheeseInput.Editable = false;
            _saveButton.Disabled = true;
            _deleteButton.Disabled = true;
            _addSlotButton.Disabled = true;
            return;
        }
        _nameInput.Text = profile.Name;
        _serverInput.Text = profile.ServerUrl;
        _passwordInput.Text = profile.Password;
        _cheeseInput.Text = profile.CheeseTrackerUrl ?? "";
        _nameInput.Editable = true;
        _serverInput.Editable = true;
        _passwordInput.Editable = true;
        _cheeseInput.Editable = true;
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
            lineEdit.TextChanged += (newText) =>
            {
                _selectedProfile.Slots[index] = newText;
                row.SetMeta("slot_name", newText);
                MarkDirty();
            };
            lineEdit.TextSubmitted += (newText) =>
            {
                OnConnectSlotPressed(newText, _selectedProfile);
            };
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
            connectBtn.TooltipText = "Connect";
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
                TooltipText = "Disconnect"
            };
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
                TooltipText = "Delete Slot"
            };
            delBtn.SetMeta("is_icon_button", true);
            delBtn.Modulate = AP_Atlas.Core.ThemeColors.Danger;
            delBtn.Pressed += () =>
            {
                DisconnectSlot(_selectedProfile.Id, lineEdit.Text);
                _selectedProfile.ActiveSlots.Remove(lineEdit.Text);
                _selectedProfile.Slots.RemoveAt(index);
                MarkDirty();
                PopulateSlotsList();
                RefreshProfileListStyles();
                UpdateSidebar();
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
        SetFontSizeRecursive(_slotsListVBox, SlotListFontSize);
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
    private void OnAddProfilePressed()
    {
        var newProfile = new MultiworldProfile();
        newProfile.Slots.Add("Player1");
        _profiles.Add(newProfile);
        DataManager.SaveProfiles(_profiles);
        RefreshProfileList();
        SelectProfile(newProfile);
    }
    private void OnSaveProfilePressed()
    {
        if (_selectedProfile != null)
        {
            _selectedProfile.Name = _nameInput.Text;
            _selectedProfile.ServerUrl = _serverInput.Text;
            _selectedProfile.Password = _passwordInput.Text;
            // An automatic reconnect waiting for one of its slots uses the new address and password.
            _sessions?.UpdateLogins(_selectedProfile.Id, _selectedProfile.ServerUrl, string.IsNullOrEmpty(_selectedProfile.Password) ? null : _selectedProfile.Password);
            DataManager.SaveProfiles(_profiles);
            _saveButton.Modulate = AP_Atlas.Core.ThemeColors.Text;
            RefreshProfileList();
            LogToSystem($"Profile '{_selectedProfile.Name}' saved.", "green");
            string cheeseLink = _cheeseInput.Text.Trim();
            if (cheeseLink != (_selectedProfile.CheeseTrackerUrl ?? "")) LinkCheeseFromEditor(_selectedProfile, cheeseLink);
        }
    }
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
            var confirmDialog = new ConfirmationDialog
            {
                Title = "Delete Profile",
                DialogText = $"Are you sure you want to delete the profile '{profile.Name}' and all its slots?",
                Transient = true,
                Exclusive = true
            };
            confirmDialog.Confirmed += () =>
            {
                DeleteProfile(profile);
                ShowToast("Profile Deleted", AP_Atlas.Core.ThemeColors.Warning);
                confirmDialog.QueueFree();
            };
            confirmDialog.Canceled += () => confirmDialog.QueueFree();
            AddChild(confirmDialog);
            confirmDialog.PopupCentered();
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
